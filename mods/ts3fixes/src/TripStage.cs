using System;
using System.Collections.Generic;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    public static partial class Fixes
    {
        // ---- stagedarrival (default off, townies only), the shared trip helpers of icecreamdirect and nearvenue ----
        // "Walk on from the edge". A shortcut refused only because the destination is in view or near the household (a lot trip,
        // GoHome, a rabbit hole) places the unseen townie at the street point nearest to the destination that is out of view, then the
        // game's own trip runs and plans only the last stretch, which the player sees as a normal arrival.
        // Counters 3455 asked, 3456 placed, 3457 not in scope (household, selected, no household, seen), 3458 destination colour
        // unknown, 3459 no candidate passed, 3460 trip too short to stage, 3461 candidates tested, 3462 loop guard, 3463-3465 placed
        // by kind (lot trip, GoHome, rabbit hole), 3466 the hold gave up. Diagnostics (why nobody was placed): candidates refused by
        // reason 3467 inside a lot, 3468 water or not outdoor ground, 3469 seen (camera reach, household, goal in view), 3470 colour
        // not A's, 3471 not routable; 3472 colour unknown rescued by a point on the 10 / 20 m rings around the destination; 3473
        // placed at a lot entry (street side) instead of on the straight line; 3474 lot-entry candidates tested; 3475 sim colour known
        // when the destination's was not. The first 40 refusals are also logged (TsLog 3).
        const int kStageLot = 0, kStageGoHome = 1, kStageRabbit = 2;
        const int kCStAsked = 3455, kCStPlaced = 3456, kCStScope = 3457, kCStColour = 3458, kCStNoSpot = 3459, kCStShort = 3460,
            kCStTested = 3461, kCStLoop = 3462, kCStKind = 3463, kCStHold = 3466,
            kCStRej = 3467, kCStRing = 3472, kCStEntryPlaced = 3473, kCStEntryTested = 3474, kCStSimColour = 3475;

        // stagedarrival: true = the Sim stands at the staging point facing `dest`; the caller then runs the game's own trip.
        // mode 2 (UfPreCore.TripMode): D's walk-time hold for the skipped leg first. leadIn runs right before the move (GoHome:
        // the game's leaving bookkeeping on the lot the Sim leaves, as the go-home shortcut does).
        delegate void StageLeadIn();  // the game's mscorlib (2.0) has no plain Action
        static bool StageNear(Sim sim, Vector3 dest, Lot lot, int kind, int mode, StageLeadIn leadIn)
        {
            int[] c = PatchCounters();
            c[kCStAsked]++;
            try
            {
                if (IsPlayerSim(sim) || sim.Household == null || !TownieUnseen(sim, null)) { c[kCStScope]++; return false; }
                if (StageLooping(sim)) { c[kCStLoop]++; return false; }
                float dist = (dest - sim.Position).Length();
                int n = TripStageCore.StageCount(dist);
                if (n == 0) { c[kCStShort]++; return false; }
                // A = the destination's flood colour: a candidate must be in it (land path to the destination, never across water)
                if (!UfColourA(dest, lot) && !StageColourRing(sim, dest, lot)) { c[kCStColour]++; return false; }
                Vector3 h;
                if (!StageFind(sim, dest, dist, n, lot, out h)) { c[kCStNoSpot]++; return false; }
                if (mode == 2 && !StageHold(sim, h)) { c[kCStHold]++; return false; }
                if (leadIn != null) leadIn();
                Vector3 fwd = dest - h;
                fwd.y = 0f;
                fwd = fwd.LengthSqr() > 0.01f ? fwd / fwd.Length() : sim.ForwardVector;
                PlaceSim(sim, h, fwd, "staged " + kind + " lot " + (lot != null ? lot.LotId : 0UL));
                StageNoteLoop(sim);
                c[kCStPlaced]++;
                c[kCStKind + kind]++;
                TsLog(0, "stagedarrival sim " + sim.ObjectId.Value + " kind " + kind + " lot " + (lot != null ? lot.LotId : 0UL) + " trip " + (int)dist + " m, left " + (int)(dest - h).Length() + " m");
                return true;
            }
            catch (Exception) { return false; }  // on any doubt the game's own trip
        }

        // the first candidate (nearest to the destination) that is on the street (never inside a lot), not water, outdoors on the
        // ground, out of view (FarFromViewers: camera reach and the household; UfGoalUnseen: on screen, its lot), in colour A, and
        // routable for this Sim. Two sources, in this order: points on the straight line to the Sim every 10 m, then the street side
        // (entry point, and 3 m outward) of every lot within kStageLateralM of that line, nearest the destination first (the line
        // alone almost never met a street: 117 of 118 "no spot" on Sunset Valley / Bridgeport, 2026-11-14). Rejections are counted by reason.
        static bool StageFind(Sim sim, Vector3 dest, float dist, int n, Lot destLot, out Vector3 h)
        {
            int[] c = PatchCounters();
            int[] rej = new int[TripStageCore.kCandReasons];
            Vector3 dir = (sim.Position - dest) / dist;
            Route r = null;
            for (int i = 0; i < n; i++)
            {
                h = dest + dir * TripStageCore.StageAt(i);
                if (StageCandidate(sim, ref h, rej, ref r)) return true;
            }
            int tested = n;
            List<Vector3> es = StageEntries(dest, dir, dist, destLot);
            foreach (Vector3 e in es)
            {
                h = e;
                c[kCStEntryTested]++;
                tested++;
                if (StageCandidate(sim, ref h, rej, ref r)) { c[kCStEntryPlaced]++; return true; }
            }
            if (TsLogOpen(3))
                TsLog(3, "stagedarrival no spot sim " + sim.ObjectId.Value + " lot " + (destLot != null ? destLot.LotId : 0UL) + " trip " + (int)dist + " m, " + n + " on the line + "
                    + es.Count + " lot entries tried, refused: in lot " + rej[0] + ", water/indoor " + rej[1] + ", seen " + rej[2] + ", colour " + rej[3] + ", not routable " + rej[4]);
            h = Vector3.Invalid;
            return false;
        }

        // one candidate through the unchanged rules: not inside a lot, not water, outdoors on the ground, out of view (never loosened),
        // in colour A, routable for this Sim. Counts the reason a point is refused (c[3480..3484], rej[] for the log line).
        static bool StageCandidate(Sim sim, ref Vector3 h, int[] rej, ref Route r)
        {
            int[] c = PatchCounters();
            h.y = World.GetTerrainHeight(h.x, h.z);
            c[kCStTested]++;
            int why = -1;
            Lot hl = LotManager.GetLotAtPoint(h);
            if (hl != null && !hl.IsWorldLot) why = TripStageCore.kCandLot;
            else if ((World.GetTerrainType(h) & (TerrainType.WorldSea | TerrainType.WorldPond)) != 0 || !UfOutdoorGround(h)) why = TripStageCore.kCandGround;
            else if (!FarFromViewers(h) || !UfGoalUnseen(h)) why = TripStageCore.kCandSeen;
            else if (UfColourB(h, null) != 1) why = TripStageCore.kCandColour;
            else
            {
                if (r == null) r = sim.CreateRoute();
                if (r.IsPointRoutable(h)) return true;
                why = TripStageCore.kCandRoute;
            }
            rej[why]++;
            c[kCStRej + why]++;
            return false;
        }

        // street-side candidates: each lot's entry point and the point 3 m outward from the lot's position, those near the straight
        // line (TripStageCore.EntryOk), nearest the destination first, at most kStageMaxEntry points
        static List<Vector3> StageEntries(Vector3 dest, Vector3 dir, float dist, Lot destLot)
        {
            List<Vector3> pts = new List<Vector3>();
            List<float> keys = new List<float>();
            foreach (object o in LotManager.AllLots)
            {
                Lot l = o as Lot;
                if (l == null || l.IsWorldLot || l == destLot) continue;
                Vector3 e = l.EntryPoint(), v = e - dest;
                float along = v.x * dir.x + v.z * dir.z, lx = v.x - dir.x * along, lz = v.z - dir.z * along;
                if (!TripStageCore.EntryOk(along, (float)Math.Sqrt(lx * lx + lz * lz), dist)) continue;
                Vector3 outw = e - l.Position;
                outw.y = 0f;
                int at = keys.Count;
                while (at > 0 && keys[at - 1] > along) at--;
                keys.Insert(at, along);
                pts.Insert(at, e);
                if (outw.LengthSqr() > 1f) { outw = outw / outw.Length() * 3f; keys.Insert(at + 1, along); pts.Insert(at + 1, e + outw); }
            }
            if (pts.Count > TripStageCore.kStageMaxEntry) pts.RemoveRange(TripStageCore.kStageMaxEntry, pts.Count - TripStageCore.kStageMaxEntry);
            return pts;
        }

        // the destination's colour was not found at the point or the lot's entry point: ask again around it (water points skipped);
        // A is the colour of the first point that has one. Counts the rescue (3485); else the reason is logged (3488: the Sim's own
        // colour is known, so the graph has colours here and only none near the destination).
        static bool StageColourRing(Sim sim, Vector3 dest, Lot lot)
        {
            int[] c = PatchCounters();
            for (int i = 0; ; i++)
            {
                float dx, dz;
                if (!TripStageCore.ColourRing(i, out dx, out dz)) break;
                Vector3 p = new Vector3(dest.x + dx, 0f, dest.z + dz);
                p.y = World.GetTerrainHeight(p.x, p.z);
                if ((World.GetTerrainType(p) & (TerrainType.WorldSea | TerrainType.WorldPond)) != 0) continue;
                if (UfColourAsk(11, p)) { c[kCStRing]++; return true; }
            }
            bool simKnown = UfColourA(sim.Position, sim.LotCurrent);
            if (simKnown) c[kCStSimColour]++;
            if (TsLogOpen(3))
                TsLog(3, "stagedarrival colour unknown lot " + (lot != null ? lot.LotId : 0UL) + " dest (" + (int)dest.x + ", " + (int)dest.z + ") sim colour " + (simKnown ? "known" : "unknown")
                    + " dest terrain " + (int)World.GetTerrainType(dest) + " room " + World.GetRoomId(dest));
            return false;
        }

        // D's hold for the walk to the staging point (the time the skipped leg takes), then the same checks again
        static bool StageHold(Sim sim, Vector3 h)
        {
            if (Simulator.CurrentTask != sim.ObjectId || !MayYield()) return false;
            if (!UfHold(sim, h, UfPreCore.HoldMinutes((h - sim.Position).Length())) || sim.HasBeenDestroyed) return false;
            return TownieUnseen(sim, null) && FarFromViewers(h) && UfGoalUnseen(h);
        }

        static readonly Dictionary<ulong, long> sStageAt = new Dictionary<ulong, long>();
        static readonly long kStageLoopTicks = SimClock.ConvertToTicks(TripStageCore.kStageLoopMin, TimeUnit.Minutes);
        static bool StageLooping(Sim sim)
        {
            long at;
            return sStageAt.TryGetValue(sim.ObjectId.Value, out at) && SimClock.CurrentTicks - at < kStageLoopTicks;
        }
        static void StageNoteLoop(Sim sim)
        {
            if (sStageAt.Count >= TripStageCore.kStageGuardCap) PruneTicks(sStageAt, kStageLoopTicks);
            sStageAt[sim.ObjectId.Value] = SimClock.CurrentTicks;
        }

        // drops entries older than `age` sim ticks; when none is old (a burst filled the table) it starts over
        static void PruneTicks<K>(Dictionary<K, long> d, long age)
        {
            long now = SimClock.CurrentTicks;
            List<K> old = new List<K>();
            foreach (KeyValuePair<K, long> e in d) if (now - e.Value >= age || e.Value > now) old.Add(e.Key);
            if (old.Count == 0) d.Clear();
            foreach (K k in old) d.Remove(k);
        }

        // the first 40 lines per feature (0 stagedarrival placed, 1 nearvenue, 2 rabbitholedirect, 3 stagedarrival refusals, 4 icecreamdirect); object ids only
        static readonly int[] sTsLogN = new int[5];
        const int kTsLogMax = 40;
        static bool TsLogOpen(int feature) { return sTsLogN[feature] < kTsLogMax; }
        static void TsLog(int feature, string line)
        {
            if (sTsLogN[feature] >= kTsLogMax) return;
            sTsLogN[feature]++;
            DiagLine(line);
        }
    }
}
