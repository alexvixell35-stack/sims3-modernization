using System;
using System.Collections.Generic;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Objects.Island;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // nearestlotlazy (docs/677) [verified, ilspycmd LotManager.GetNearestLot / AllLotsWithoutCommonExceptions /
    // Lot.HasValidStreetParking / StreetParking.ParkingIsOnSamePieceOfLand / UnchartedIslandMarker.IsUnchartedIsland;
    // stall-watch script stacks of s149-s170]: LotManager.GetNearestLot(point, ignoreList, requireStreetParking) first
    // builds AllLotsWithoutCommonExceptions, which runs one native object count per lot of the world
    // (Lot.CountObjects<UnchartedIslandMarker>), then asks every lot for HasValidStreetParking, which is up to ten
    // World.ArePointsConnectedByLand calls (two closest-waypoint searches each), and only then compares the distances.
    // CarNpcManager.InitCarForLotRoute calls it for every NPC car sent to a lot without valid street parking (all the
    // island lots of Isla Paradiso): 330-560 ms sim-thread frames whose stall leaf is World_ArePointsConnectedByLand or
    // Query_CountObjectsOnLot. Switch on: distances first, then the same tests on the nearest lot, the next nearest, ...
    // until one passes. The same lot as EA's loop (NearestLotCore selftest); the tests have no side effects. With
    // kNearestLotLazyShadow EA's loop runs too, its answer is returned and a difference is logged.
    // No counter slot: NearestLotLazyCheck result prints the session counts; log line "ts3fixes: nearestlot:".
    public static partial class Fixes
    {
        static bool sNlReady;
        static int sNlCalls, sNlLots, sNlTests, sNlShadow, sNlShadowDiff, sNlErrors, sNlEaSlow;
        static uint sNlUs, sNlEaUs;

        static void ApplyNearestLot()
        {
            sNlReady = Patch(typeof(LotManager), "GetNearestLot", new Type[] { typeof(Vector3), typeof(List<Lot>), typeof(bool) }, typeof(NearestLotHook), "NearestHook", "NearestOrig");
        }

        class NlTest
        {
            public List<Lot> Lots;
            public bool Parking;
            // the tests EA's two loops run on a lot besides the ones done before the distance (world lot, ignore list)
            public bool Valid(int i)
            {
                Lot l = Lots[i];
                return !l.IsCommunityLotOfType(CommercialLotSubType.kEP10_Diving) && !l.IsHouseboatLot() && !UnchartedIslandMarker.IsUnchartedIsland(l)
                    && (!Parking || l.HasValidStreetParking());
            }
        }

        // Our path. tests = lots whose costly tests ran.
        static Lot NearestLotLazy(Vector3 point, List<Lot> ignoreList, bool requireStreetParking, out int lots, out int tests)
        {
            NlTest t = new NlTest();
            t.Lots = new List<Lot>();
            t.Parking = requireStreetParking;
            List<float> d = new List<float>();
            foreach (Lot l in LotManager.AllLots)
            {
                if (l.IsWorldLot || (ignoreList != null && ignoreList.Contains(l))) continue;
                t.Lots.Add(l);
                d.Add((point - l.Position).LengthSqr());
            }
            lots = t.Lots.Count;
            int pick = NearestLotCore.LazyPick(d.ToArray(), t.Valid, out tests);
            return pick < 0 ? null : t.Lots[pick];
        }

        public static class NearestLotHook
        {
            public static Lot NearestHook(Vector3 point, List<Lot> ignoreList, bool requireStreetParking)
            {
                uint t0 = SrcNowUs();
                if (!ShortcutOn("nearestlotlazy"))
                {
                    Lot ea = NearestOrig(point, ignoreList, requireStreetParking);
                    uint us0 = unchecked(SrcNowUs() - t0);
                    if (us0 >= 50000)
                    {
                        sNlEaSlow++;
                        HcLine("nearestlot: EA's loop " + (us0 / 1000) + " ms (switch off), street parking " + requireStreetParking + ", lot " + NlName(ea));
                    }
                    return ea;
                }
                Lot mine;
                int lots, tests;
                try { mine = NearestLotLazy(point, ignoreList, requireStreetParking, out lots, out tests); }
                catch (Exception) { sNlErrors++; return NearestOrig(point, ignoreList, requireStreetParking); }
                uint us = unchecked(SrcNowUs() - t0);
                sNlCalls++; sNlLots += lots; sNlTests += tests; sNlUs += us;
                if (Instantiator.kNearestLotLazyShadow)
                {
                    uint t1 = SrcNowUs();
                    Lot ea = NearestOrig(point, ignoreList, requireStreetParking);
                    uint eaUs = unchecked(SrcNowUs() - t1);
                    sNlShadow++; sNlEaUs += eaUs;
                    bool same = ReferenceEquals(ea, mine);
                    if (!same) sNlShadowDiff++;
                    HcLine("nearestlot: shadow " + (same ? "same" : "DIFFERENT") + ", ours " + us + " us (" + tests + " of " + lots + " lots tested) lot " + NlName(mine)
                        + ", EA " + eaUs + " us lot " + NlName(ea) + ", street parking " + requireStreetParking);
                    return ea;
                }
                if (us >= 20000) HcLine("nearestlot: " + (us / 1000) + " ms, " + tests + " of " + lots + " lots tested, street parking " + requireStreetParking + ", lot " + NlName(mine));
                return mine;
            }
            public static Lot NearestOrig(Vector3 point, List<Lot> ignoreList, bool requireStreetParking) { return null; } // receives LotManager.GetNearestLot(Vector3, List<Lot>, bool)
        }

        static string NlName(Lot l) { return l == null ? "none" : l.LotId.ToString("X"); }

        static string NlCounters()
        {
            return "nearestlotlazy: calls by our path " + sNlCalls + " (" + (sNlUs / 1000) + " ms, " + sNlTests + " of " + sNlLots + " lots tested), shadow compares " + sNlShadow
                + " (different " + sNlShadowDiff + ", EA " + (sNlEaUs / 1000) + " ms), EA's loop 50 ms or more with the switch off " + sNlEaSlow + ", errors " + sNlErrors
                + "; " + GlMembers(CkLive("nearestlotlazy"));
        }

        // NearestLotLazyCheck run: EA's loop against ours from up to four lots of the world, with and without the street
        // parking rule (EA's loop is the slow one: up to eight calls of it), then one call through the hooked method.
        public static object NearestLotLazyCheck(string what)
        {
            string counters = NlCounters();
            if (what == "result") return counters;
            if (what != "run") return "nearestlotlazy <run|result>";
            bool on = ShortcutOn("nearestlotlazy");
            string missing = sNlReady ? "" : "LotManager.GetNearestLot swap";
            int n = 0, diff = 0, lots = 0, tests = 0;
            uint eaUs = 0, ourUs = 0;
            bool viaHook = false;
            try
            {
                List<Lot> from = new List<Lot>();
                foreach (Lot l in LotManager.AllLots) if (!l.IsWorldLot) from.Add(l);
                if (from.Count == 0) missing = EpJoin(missing, "lots (no world)");
                int step = Math.Max(1, from.Count / 4);
                for (int i = 0; sNlReady && i < from.Count && n < 8; i += step)
                    for (int p = 0; p < 2; p++)
                    {
                        // the lot itself is on the ignore list, as for a caller that wants another lot
                        List<Lot> ignore = new List<Lot>();
                        ignore.Add(from[i]);
                        int a, b;
                        uint t0 = SrcNowUs();
                        Lot ea = NearestLotHook.NearestOrig(from[i].Position, ignore, p == 1);
                        uint t1 = SrcNowUs();
                        Lot mine = NearestLotLazy(from[i].Position, ignore, p == 1, out a, out b);
                        uint t2 = SrcNowUs();
                        eaUs += unchecked(t1 - t0); ourUs += unchecked(t2 - t1);
                        n++; lots += a; tests += b;
                        if (!ReferenceEquals(ea, mine)) diff++;
                    }
                if (sNlReady && from.Count > 0)
                {
                    int before = sNlCalls;
                    LotManager.GetNearestLot(from[0].Position, null, true);
                    viaHook = sNlCalls > before;
                }
            }
            catch (Exception e) { missing = EpJoin(missing, "threw " + e.GetType().Name); }
            bool controls = missing.Length == 0 && diff == 0 && n > 0;
            bool fixedNow = on && viaHook && diff == 0 && n > 0;
            return B582Line("nearestlotlazy", on, "hooked call via our path=" + viaHook + ", answers that differ " + diff + " of " + n, fixedNow,
                "EA's loop " + (eaUs / 1000) + " ms, ours " + (ourUs / 1000) + " ms (" + tests + " of " + lots + " lots tested) for the same " + n + " questions", controls, missing, counters);
        }
    }
}
