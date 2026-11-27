using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // Route plans the game runs in candidate loops, where one call can plan many times on the sim thread (docs/141 §7,
    // risks #1, #3 and #5 of §4). Three switches, all default off until the candidate-loop A/B
    // (tools/harness/queues/candidate-loop-ab.json). Counters [137-145].
    public static partial class Fixes
    {
        static void ApplyCandidateLoops()
        {
            Patch(typeof(Sim), "FindRoutablePointInsideNearFrontDoor", typeof(SafeSpotCapHook), "FrontDoorHook", "FrontDoorOrig");
            Patch(typeof(LotManager), "GetFarthestLot", new Type[] { typeof(Lot), typeof(Sim), typeof(bool) }, typeof(FarthestLotHook), "FarthestHook", "FarthestOrig");
            Patch(typeof(Lot), "GetRoutableIndoorPositions", new Type[] { typeof(Sim), typeof(uint), typeof(string), typeof(ProductVersion) }, typeof(IndoorPositionsHook), "IndoorHook", "IndoorOrig");
        }

        // #1, Sim reset -> safe spot. Sim.FindRoutablePointInsideNearFrontDoor (every reset or send-home that does not find a
        // spot where the Sim stands: AttemptToFindSafeLocation, plus performers, pregnancy, caregivers) places a tile with
        // World.FindGoodLocation and the check Sim.IsPointSafelyRoutable.Test, which runs IsPointInLotSafelyRoutable (one plan
        // to the lot's 4 corners and 4 edge midpoints) for every cell that passes the native tests. The native search returns
        // at the first cell whose check passes (docs/110 §3.3), so the game already takes the first safe spot; the cost is the
        // failed cells before it, up to four searches per call (door, door unconstrained, mailbox, mailbox unconstrained).
        // EA caps that check itself in one place: the vacation start sets a shared checker with SetLimit(5) (GameStates).
        // Switch "safespotcap" (default off) gives every call its own checker with the same limit 5: at most 6 plans per call,
        // the same cells in the same order, so the answer is the game's whenever the first safe cell is among the first 6
        // checked; past that the call falls through to the game's own last resorts (tile at the mailbox, else no spot: the
        // caller puts the Sim at the lot centre). The game's code reads the shared field once at the start of the call, so
        // ours is only visible to calls that start while one of ours is in flight (a plan that yields); those calls come
        // through this hook too and get their own. Counters: [137] calls capped, [138] calls that reached the limit.
        public class SafeSpotCapHook
        {
            const int kLimit = 5; // EA's own vacation-start limit; IsPointSafelyRoutable.Test plans while count <= limit
            static readonly List<Sim.IsPointSafelyRoutable> sLive = new List<Sim.IsPointSafelyRoutable>();
            static FieldInfo sCount;

            public bool FrontDoorHook(Lot lot, out IGameObject reservedTile)
            {
                Sim sim = (Sim)(object)this;
                FrontDoorTrace(sim);  // docs/546 §10.1: which game code searches the front door for this Sim (routefaillog)
                Sim.IsPointSafelyRoutable cur = Sim.sIsPointSafelyRoutable;
                // shortcut: a call that starts while one of ours is in flight is capped even with the switch just turned
                // off, since the game would otherwise read our checker (bound to another lot); runtime toggles only
                bool ours = cur != null && sLive.Contains(cur);
                if (lot == null || (cur != null && !ours) || (!ours && !ShortcutOn("safespotcap"))) return FrontDoorOrig(lot, out reservedTile);
                Sim.IsPointSafelyRoutable t = new Sim.IsPointSafelyRoutable(lot, sim);
                t.SetLimit(kLimit);
                sLive.Add(t);
                Sim.sIsPointSafelyRoutable = t;
                int[] c = PatchCounters();
                c[137]++;
                try { return FrontDoorOrig(lot, out reservedTile); }
                finally
                {
                    sLive.Remove(t);
                    if (Sim.sIsPointSafelyRoutable == t) Sim.sIsPointSafelyRoutable = null;
                    if (sCount == null) sCount = typeof(Sim.IsPointSafelyRoutable).GetField("mCount", BindingFlags.Instance | BindingFlags.NonPublic);
                    if (sCount != null && (int)sCount.GetValue(t) > kLimit) c[138]++;
                }
            }
            static int sTraceLines;
            static void FrontDoorTrace(Sim sim)
            {
                try
                {
                    if (sim == null || !sim.IsInActiveHousehold || sTraceLines >= 200 || !ShortcutOn("routefaillog")) return;
                    sTraceLines++;
                    DiagLine("frontdoor search: " + sim.FullName + (sim.SimDescription.IsPet ? " (pet)" : "") + " in "
                        + (sim.CurrentInteraction != null ? sim.CurrentInteraction.GetType().Name : "none") + ", from " + RfpCaller());
                }
                catch (ResetException) { throw; }
                catch (Exception) { }
            }
            public bool FrontDoorOrig(Lot lot, out IGameObject reservedTile) { reservedTile = null; return false; } // receives Sim.FindRoutablePointInsideNearFrontDoor's original header
        }

        // #3, LotManager.GetFarthestLot(lot, actor, true): only two callers route (AlienSituation.SendAlienHome,
        // TravelUtil's early-departure destination); every other caller uses the one-argument overload, which never plans.
        // The game walks all lots in list order with a running maximum of the distance and plans a world route from the
        // actor to every lot farther than the best so far: the farthest routable lot, at the price of a plan per record
        // lot plus every unroutable one beyond the answer. Switch "farthestsorted" (default off): FarthestPick.Sorted plans
        // farthest first and stops at the first success: the same lot (FarthestPick selftest: 20k random worlds) with only
        // the unroutable lots beyond it planned. Two changes on top that can differ from the game: after kCap failed plans
        // the next lot by distance is returned unplanned (the game would keep planning nearer lots, and return null if none
        // routes), and a verified answer is reused for a sim-day for the same start lot, actor lot and route profile (a
        // lot that became unroutable since is still returned; the Sim then fails that route as with any other).
        // Counters: [139] calls, [140] plans run, [141] cache answers, [142] answers returned unplanned after the cap.
        public static class FarthestLotHook
        {
            const int kCap = 3;
            class Entry { public long Created; public ulong ActorLot, Answer; public RouteProfile Profile; }
            static readonly Dictionary<ulong, Entry> sCache = new Dictionary<ulong, Entry>();

            public static Lot FarthestHook(Lot inputLot, Sim actor, bool bSimCanRoute)
            {
                if (actor == null || !bSimCanRoute || inputLot == null || inputLot.IsWorldLot || !ShortcutOn("farthestsorted"))
                    return FarthestOrig(inputLot, actor, bSimCanRoute);
                int[] c = PatchCounters();
                c[139]++;
                List<Lot> lots = new List<Lot>();
                foreach (Lot l in LotManager.AllLotsWithoutCommonExceptions) if (l != inputLot) lots.Add(l);
                ulong actorLot = actor.LotCurrent != null ? actor.LotCurrent.LotId : 0;
                RouteProfile p = ProfileOf(actor);
                long now = SimClock.CurrentTicks;
                Entry e;
                if (p.Ok && sCache.TryGetValue(inputLot.LotId, out e) && now >= e.Created && now - e.Created < kVerdictTicks && e.ActorLot == actorLot
                    && e.Profile.Opts == p.Opts && e.Profile.Opts2 == p.Opts2 && e.Profile.Flags == p.Flags)
                    foreach (Lot l in lots) if (l.LotId == e.Answer) { c[141]++; return l; }
                float[] d2 = new float[lots.Count];
                for (int i = 0; i < d2.Length; i++) d2[i] = (inputLot.Position - lots[i].Position).LengthSqr();
                int plans;
                bool capped;
                int pick = FarthestPick.Sorted(d2, delegate(int i) { return lots[i].PlanToLot(actor.CreateRoute()).Succeeded(); }, kCap, out plans, out capped);
                c[140] += plans;
                if (capped) c[142]++;
                if (pick < 0) return null;
                if (!capped && p.Ok)
                {
                    e = new Entry();
                    e.Created = now; e.ActorLot = actorLot; e.Answer = lots[pick].LotId; e.Profile = p;
                    sCache[inputLot.LotId] = e;
                }
                return lots[pick];
            }
            public static Lot FarthestOrig(Lot inputLot, Sim actor, bool bSimCanRoute) { return null; } // receives LotManager.GetFarthestLot(Lot, Sim, bool)'s original header
        }

        // #5, Lot.GetRoutableIndoorPositions: callers are FireFightingJob.SetupTasks (large fires and earthquakes, in the
        // firefighter's own interaction) and NpcParty.BringNpcGuests (a OneShotFunction task): both already run in a
        // yielding task, so their plans are own-task or foreign-task plans that [RouteAsync] can send to the worker (no task
        // move needed; the event and social-callback chains in docs/141 are class-hierarchy artefacts of Job.SetupTasks).
        // The loop: per inside room, objects in random order, per object its fire positions; per position a FindGoodLocation
        // and, when that finds a spot, a PlanToPointRadius from the Sim; the first success ends the room. A room nobody
        // can reach plans every object x position. Switch "indoorcap" (default off): the same loop with the same random
        // calls in the same order, but a room is given up after kRoomFails failed plans. Same answer whenever every room's
        // first success comes within kRoomFails failures; otherwise that room has no position (the game would keep trying)
        // and the random sequence after it differs. Counters: [143] calls, [144] plans run, [145] rooms given up.
        public class IndoorPositionsHook
        {
            const int kRoomFails = 4;

            public Dictionary<Vector3, Vector3> IndoorHook(Sim sim, uint minRoomSizeInTileQuadrants, string placementTestObjectMedatorName, ProductVersion placementTestObjectProductVersion)
            {
                if (sim == null || !ShortcutOn("indoorcap")) return IndoorOrig(sim, minRoomSizeInTileQuadrants, placementTestObjectMedatorName, placementTestObjectProductVersion);
                Lot lot = (Lot)(object)this;
                int[] c = PatchCounters();
                c[143]++;
                Dictionary<Vector3, Vector3> found = new Dictionary<Vector3, Vector3>();
                LotDisplayLevelInfo levels = World.LotGetDisplayLevelInfo(lot.LotId);
                IGameObject probe = GlobalFunctions.CreateObjectOutOfWorld(placementTestObjectMedatorName, placementTestObjectProductVersion, null, null);
                Route route = sim.CreateRoute();
                for (int level = levels.mMin; level <= levels.mMax; level++)
                {
                    foreach (int room in World.GetInsideRoomsAtLevel(lot.LotId, level, eRoomDefinition.PlacementBlocking))
                    {
                        if ((lot.HasVirtualResidentialSlots && lot.IsRoomPublic(room)) || World.GetInformationAboutRoom(lot.LotId, room).roomData.sizeInTileQuadrants <= minRoomSizeInTileQuadrants) continue;
                        List<GameObject> objs = lot.GetObjectsInRoom<GameObject>(room);
                        RandomUtil.RandomizeListOfObjects(objs);
                        int fails = 0;
                        bool done = false;
                        foreach (GameObject o in objs)
                        {
                            foreach (Vector3 v in o.GetFirePositions())
                            {
                                if (World.GetRoomId(v) != room) continue;
                                Vector3 pos = v, fwd = RandomUtil.GetRandomDirXZ();
                                if (!GlobalFunctions.FindGoodLocationInSameRoom(probe, ref pos, ref fwd, false)) continue;
                                c[144]++;
                                if (!route.PlanToPointRadius(pos, 0.5f).Succeeded())
                                {
                                    if (++fails < kRoomFails) continue;
                                    c[145]++;
                                    done = true;
                                    break;
                                }
                                if (found.ContainsKey(pos)) continue; // the game moves on to the next position too
                                found.Add(pos, fwd);
                                done = true;
                                break;
                            }
                            if (done) break;
                        }
                    }
                }
                probe.Destroy();
                return found;
            }
            public Dictionary<Vector3, Vector3> IndoorOrig(Sim sim, uint minRoomSizeInTileQuadrants, string placementTestObjectMedatorName, ProductVersion placementTestObjectProductVersion) { return null; } // receives Lot.GetRoutableIndoorPositions(Sim, uint, string, ProductVersion)'s original header
        }

        // Repro probe (tools/harness/repros.json candidate-loops): runs the three loops once for the selected Sim, on the
        // sim thread in the agent's call, and reports time and result. Run it with the switches off and on.
        public static string CandidateLoopProbe()
        {
            Sim sim = PlumbBob.SelectedActor;
            if (sim == null) return "no selected Sim";
            Lot home = sim.LotHome ?? sim.LotCurrent;
            string r = "";
            int t0 = System.Environment.TickCount;
            IGameObject tile = null;
            bool ok = false;
            try { ok = sim.FindRoutablePointInsideNearFrontDoor(home, out tile); }
            finally { if (tile != null) tile.Destroy(); }
            r += "frontdoor " + ok + " " + (System.Environment.TickCount - t0) + " ms; ";
            t0 = System.Environment.TickCount;
            Lot far = LotManager.GetFarthestLot(sim.LotCurrent ?? home, sim, true);
            r += "farthest " + (far != null ? far.LotId.ToString("X") : "null") + " " + (System.Environment.TickCount - t0) + " ms; ";
            t0 = System.Environment.TickCount;
            Dictionary<Vector3, Vector3> pos = home != null ? home.GetRoutableIndoorPositions(sim, 10u) : null;
            r += "indoor " + (pos != null ? pos.Count : -1) + " " + (System.Environment.TickCount - t0) + " ms";
            return r;
        }
    }
}
