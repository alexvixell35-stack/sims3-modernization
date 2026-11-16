using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Careers;
using Sims3.Gameplay.Controllers;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.ObjectComponents;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // mopunreachable (tuning kMopUnreachable, off; docs/578 §3b, s58 lead: MopPuddle #3 by route-plan time, 96 s, household-visible
    // routefails NoPathToDestination / DestinationPortalLocked).
    //
    // [verified, decompiled] Terrain.Mopping.Run loops `GetClosestPuddle; MopTile; if (HasExitReason) break`. MopTile adds
    // RouteFailed when RouteToPuddle fails or CanMopPuddle names a blocker, so one unreachable puddle (locked room, roof,
    // blocked) ends the run while reachable puddles in the same room stay wet; Run returns true. Autonomy reaches mopping
    // through Sim.MopPuddles / MopBurntTiles (MopFloor): Test passes while any puddle passes IsLocationMoppableBySim and
    // CanMopPuddle, and Run pushes Mopping on the first listed puddle that passes IsLocationMoppableBySim (no CanMopPuddle,
    // no reachability, not the closest), so the same unreachable puddle is picked again and again.
    //
    // Fix (1): our copy of Mopping.Run skips a puddle whose route fails or that is blocked (per-run list) and tries the next
    // closest one; a cancel (exit reason after the failed route) still ends the run as EA. Only the first failed route
    // plays the route-fail reaction. The run ends RouteFailed (and shows the last blocker) only when nothing was mopped,
    // as EA's run does. A tile that was mopped at but is still dirty without an exit reason is not tried again (EA would
    // loop on it). (2): the skipped puddles are remembered per Sim (lot, puddle + burnt count, sim-minute, locations), with
    // these rules: another lot, a changed count or 30 sim-minutes drop the memo. While it holds,
    // MopFloor.IsLocationMoppableBySim answers false for those locations, so MopFloor's Test stops offering a mop whose
    // only puddles are unreachable and its Run picks a reachable one. Every Sim, household included: outcomes are EA's
    // with the unreachable tries removed.
    // Counters: [2766] puddles skipped in a run (on), [2767] runs that mopped after a skip (on), [2768] runs that ended on an
    // unreachable puddle with others left (switch off), [2769] locations withheld by the memo (on), [2770] the same seen
    // with the switch off, [2771] errors.
    //
    // petpuddletile (kPetPuddleTile, off): [verified, decompiled] Terrain.DrinkPuddle.Run and PlayInPuddle.Run route to
    // GetClosestPuddle(lot, Hit.mPoint) and drink / play there, but then remove (or spread) the puddle at Hit's tile, so a
    // pet drinking the next puddle over dries the clicked one. Fix: before EA's Run, Hit becomes the closest puddle
    // (GetClosestPuddle from it returns itself), so the tile removed is the tile used. [2772] hits moved (on), [2773] the
    // same seen with the switch off, [2774] errors.
    public static partial class Fixes
    {
        const float kMuExpiryMin = 30f;
        const int kMuMax = 64;

        class MuMemo { internal ulong Lot; internal int Count; internal float At; internal List<LotLocation> Locs = new List<LotLocation>(); internal bool Counted; }
        static readonly Dictionary<ulong, MuMemo> sMuMemo = new Dictionary<ulong, MuMemo>();  // sim thread only, by SimDescriptionId

        static readonly FieldInfo sMuJig = typeof(Terrain.Mopping).GetField("mJig", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly MethodInfo sMuMoppable = typeof(Sim.MopFloor).GetMethod("IsLocationMoppableBySim", BindingFlags.Static | BindingFlags.NonPublic,
            null, new Type[] { typeof(Sim), typeof(Lot), typeof(LotLocation) }, null);

        static void ApplyMopUnreachable()
        {
            string miss = RlNames("Terrain+Mopping.mJig", sMuJig, "MopFloor.IsLocationMoppableBySim", sMuMoppable);
            if (miss.Length > 0) { AppliedLog().Add("mopunreachable: not found: " + miss); return; }
            Patch(typeof(Terrain.Mopping), "Run", Type.EmptyTypes, typeof(MopRunHook), "RunHook", "RunOrig");
            Patch(typeof(Sim.MopFloor), "IsLocationMoppableBySim", new Type[] { typeof(Sim), typeof(Lot), typeof(LotLocation) }, typeof(MopFloorHook), "MoppableHook", "MoppableOrig");
        }

        static void ApplyPetPuddleTile()
        {
            Patch(typeof(Terrain.DrinkPuddle), "Run", Type.EmptyTypes, typeof(PetPuddleHook), "DrinkHook", "DrinkOrig");
            Patch(typeof(Terrain.PlayInPuddle), "Run", Type.EmptyTypes, typeof(PetPuddleHook), "PlayHook", "PlayOrig");
        }

        // Pure: what a run does after one tile. failed = route failed or blocked. 0 go on, 1 end the run (EA), 2 skip the tile.
        internal static int MuStep(bool on, bool failed, bool cancelled)
        {
            if (!failed) return 0;
            return on && !cancelled ? 2 : 1;
        }

        // Pure: does the fixed run end RouteFailed? Only when it mopped nothing and skipped an unreachable tile.
        internal static bool MuEndsFailed(int mopped, int unreachable) { return mopped == 0 && unreachable > 0; }

        // Pure: withhold a memo'd location (same lot, same count, younger than the expiry).
        internal static bool MuWithhold(bool on, bool hasMemo, bool sameLot, int countNow, int countAtMemo, float ageMin, bool locInMemo)
        {
            return on && hasMemo && locInMemo && sameLot && countNow > 0 && countNow == countAtMemo && ageMin >= 0f && ageMin < kMuExpiryMin;
        }

        // Pure: should a pet puddle Run use the closest puddle's tile as its hit?
        internal static bool PtUseClosest(bool on, bool closestValid, int hx, int hz, int hl, int cx, int cz, int cl)
        {
            return on && closestValid && (hx != cx || hz != cz || hl != cl);
        }

        static float MuNow() { return SimClock.ElapsedTime(TimeUnit.Minutes); }  // sim-minutes, for the memo expiry

        static bool MuSameLoc(LotLocation a, LotLocation b) { return a.mX == b.mX && a.mZ == b.mZ && a.mLevel == b.mLevel; }

        static int MuCount(ulong lot)
        {
            LotLocation[] p = World.GetPuddles(lot, LotLocation.Invalid), b = World.GetBurntTiles(lot, LotLocation.Invalid);
            return (p == null ? 0 : p.Length) + (b == null ? 0 : b.Length);
        }

        static void MuRemember(Sim actor, ulong lot, List<LotLocation> locs)
        {
            try
            {
                if (actor == null || actor.SimDescription == null || locs.Count == 0) return;
                ulong id = actor.SimDescription.SimDescriptionId;
                if (!sMuMemo.ContainsKey(id) && sMuMemo.Count >= kMuMax)
                {
                    float now = MuNow();
                    List<ulong> drop = new List<ulong>();
                    foreach (KeyValuePair<ulong, MuMemo> kv in sMuMemo) if (now - kv.Value.At >= kMuExpiryMin) drop.Add(kv.Key);
                    if (drop.Count == 0) sMuMemo.Clear();  // all fresh: start over rather than grow (a memo only saves tries)
                    foreach (ulong k in drop) sMuMemo.Remove(k);
                }
                MuMemo m = new MuMemo { Lot = lot, Count = MuCount(lot), At = MuNow() };
                m.Locs.AddRange(locs);
                sMuMemo[id] = m;
            }
            catch (Exception) { PatchCounters()[2771]++; }
        }

        public class MopRunHook : Terrain.Mopping
        {
            protected override void OnTileMopped(ulong lotId, LotLocation location) { }
            protected override bool TestTile(Vector3 pos) { return false; }
            protected override Vector3[] GetValidTiles(ulong lotId, Vector3 startPos) { return null; }

            public bool RunHook()
            {
                if (!ShortcutOn("mopunreachable"))
                {
                    bool r = RunOrig();
                    try
                    {
                        LotLocation l = default(LotLocation);
                        ulong lot = World.GetLotLocation(Hit.mPoint, ref l);
                        if (Actor.HasExitReason(ExitReason.RouteFailed) && lot != 0 && MuClosest(lot, Hit.mPoint, null) != Vector3.Invalid) PatchCounters()[2768]++;
                    }
                    catch (Exception) { PatchCounters()[2771]++; }
                    return r;
                }
                return RunFixed();
            }

            public bool RunOrig() { return false; } // receives Terrain.Mopping.Run's original body

            // Copy of EA's Mopping.Run with the per-run skip list (see the header). The yields are EA's own (route, animate,
            // timed loop); the stub remap covers saves made inside it (docs/242).
            bool RunFixed()
            {
                Sim actor = Actor;
                IGameObject jig = Sims3.Gameplay.GlobalFunctions.CreateJigFromObject("TrashPileIndoor");
                sMuJig.SetValue(this, jig);  // Mopping.Cleanup destroys it on a reset, as for EA's run
                mCurrentStateMachine = StateMachineClient.Acquire(actor, "cleaningMop");
                mCurrentStateMachine.SetActor("x", actor);
                mCurrentStateMachine.SetActor("mopJig", jig);
                LotLocation location = default(LotLocation);
                Vector3 vector = Hit.mPoint;
                ulong lot = World.GetLotLocation(Hit.mPoint, ref location);
                BeginCommodityUpdates();
                CleanableComponent.ICleaningInteractionDefinition cd = InteractionDefinition as CleanableComponent.ICleaningInteractionDefinition;
                if (cd != null) cd.SetFunMotive(actor, this);
                bool succeeded = false;
                int mopped = 0;
                List<Vector3> skipped = new List<Vector3>();
                List<LotLocation> unreachable = new List<LotLocation>();
                GameObject lastBlocker = null;
                while (true)
                {
                    vector = MuClosest(lot, vector, skipped);
                    if (vector == Vector3.Invalid) break;
                    int r;
                    Sim.RouteFailFrequency keep = actor.PlayRouteFailFrequency;
                    if (unreachable.Count > 0) actor.PlayRouteFailFrequency = Sim.RouteFailFrequency.NeverPlayRouteFail;  // one reaction per run
                    try { r = MuMopTile(lot, vector, jig, ref lastBlocker); }
                    finally { actor.PlayRouteFailFrequency = keep; }
                    int step = MuStep(true, r == 1 || r == 2, actor.HasExitReason());
                    if (step == 1) break;
                    if (step == 2)
                    {
                        skipped.Add(vector);
                        LotLocation l = default(LotLocation);
                        World.GetLotLocationAtFloor(vector, ref l);
                        unreachable.Add(l);
                        PatchCounters()[2766]++;
                        continue;
                    }
                    if (actor.HasExitReason()) break;
                    if (r == 3) { skipped.Add(vector); continue; }  // still dirty, no exit reason: EA would mop it forever
                    Occupation.SimCompletedTask(actor, TaskId.MopPuddle, null);
                    succeeded = true;
                    mopped++;
                    skipped.Add(vector);  // mopped: never picked again, so the loop ends even if a tile stays listed
                }
                if (unreachable.Count > 0)
                {
                    if (mopped > 0) PatchCounters()[2767]++;
                    MuRemember(actor, lot, unreachable);
                    if (MuEndsFailed(mopped, unreachable.Count) && !actor.HasExitReason())
                    {
                        if (lastBlocker != null) actor.PlayRouteFailure(lastBlocker.GetThoughtBalloonThumbnailKey());
                        actor.AddExitReason(ExitReason.RouteFailed);
                    }
                }
                if (jig != null) { jig.Destroy(); sMuJig.SetValue(this, null); }
                EndCommodityUpdates(succeeded);
                return true;
            }

            // EA's MopTile, returning what happened: 0 mopped, 1 route failed, 2 blocked, 3 mopped at but not finished.
            int MuMopTile(ulong lotId, Vector3 pos, IGameObject jig, ref GameObject lastBlocker)
            {
                LotLocation location = default(LotLocation);
                World.GetLotLocationAtFloor(pos, ref location);
                GameObject blocker;
                if (!RouteToPuddle(lotId, pos, true)) return 1;
                if (!PuddleManager.CanMopPuddle(lotId, location, out blocker, false)) { if (blocker != null) lastBlocker = blocker; return 2; }
                if (World.IsOnPlatform(pos)) pos.y += 0.1875f;
                jig.SetPosition(pos);
                jig.SetForward(pos - Actor.Position);
                jig.AddToWorld();
                Actor.PushSims(pos, 1f);
                mCurrentStateMachine.EnterState("x", "Enter");
                AnimateSim("MopUp");
                bool done = DoTimedLoop(PuddleManager.TimeToMopPuddle);
                if (done) OnTileMopped(lotId, location);
                AnimateSim("exit");
                jig.RemoveFromWorld();
                return done ? 0 : 3;
            }

            // EA's GetClosestPuddle (same room and level as startPos, object-free tiles first) minus the skipped tiles.
            Vector3 MuClosest(ulong lotId, Vector3 startPos, List<Vector3> skipped)
            {
                float best = float.MaxValue, bestBlocked = float.MaxValue;
                Vector3 free = Vector3.Invalid, blocked = Vector3.Invalid;
                Vector3[] tiles = GetValidTiles(lotId, startPos);
                if (tiles == null) return Vector3.Invalid;
                LotLocation start = default(LotLocation);
                World.GetLotLocationAtFloor(startPos, ref start);
                foreach (Vector3 v in tiles)
                {
                    LotLocation l = default(LotLocation);
                    World.GetLotLocationAtFloor(v, ref l);
                    if (l.mRoom != start.mRoom || l.mLevel != start.mLevel || !TestTile(v)) continue;
                    if (skipped != null && MuSkipped(skipped, v)) continue;
                    float dx = v.x - startPos.x, dz = v.z - startPos.z, d = dx * dx + dz * dz;
                    if (Sims3.Gameplay.Queries.GetObjects<IGameObject>(v, 0.7f).Length == 0) { if (d < best) { best = d; free = v; } }
                    else if (d < bestBlocked) { bestBlocked = d; blocked = v; }
                }
                return free == Vector3.Invalid ? blocked : free;
            }

            static bool MuSkipped(List<Vector3> skipped, Vector3 v)
            {
                foreach (Vector3 s in skipped) { float dx = s.x - v.x, dy = s.y - v.y, dz = s.z - v.z; if (dx * dx + dy * dy + dz * dz < 0.0001f) return true; }
                return false;
            }
        }

        public class MopFloorHook
        {
            public static bool MoppableHook(Sim actor, Lot targetLot, LotLocation location)
            {
                if (!MoppableOrig(actor, targetLot, location)) return false;
                try
                {
                    MuMemo m;
                    if (sMuMemo.Count > 0 && actor != null && actor.SimDescription != null && targetLot != null
                        && sMuMemo.TryGetValue(actor.SimDescription.SimDescriptionId, out m))
                    {
                        bool inMemo = false;
                        foreach (LotLocation l in m.Locs) if (MuSameLoc(l, location)) { inMemo = true; break; }
                        if (inMemo)
                        {
                            bool sameLot = m.Lot == targetLot.LotId;
                            if (!MuWithhold(true, true, sameLot, sameLot ? MuCount(m.Lot) : -1, m.Count, MuNow() - m.At, true))
                                sMuMemo.Remove(actor.SimDescription.SimDescriptionId);  // stale: EA decides again
                            else if (ShortcutOn("mopunreachable")) { PatchCounters()[2769]++; return false; }
                            else if (!m.Counted) { m.Counted = true; PatchCounters()[2770]++; }
                        }
                    }
                }
                catch (Exception) { PatchCounters()[2771]++; }
                return true;
            }
            public static bool MoppableOrig(Sim actor, Lot targetLot, LotLocation location) { return false; } // receives MopFloor.IsLocationMoppableBySim
        }

        public class PetPuddleHook : Terrain.PetPuddleInteraction
        {
            protected override bool Run() { return false; }

            public bool DrinkHook() { PtMove(); return DrinkOrig(); }
            public bool PlayHook() { PtMove(); return PlayOrig(); }
            public bool DrinkOrig() { return false; } // receives Terrain.DrinkPuddle.Run
            public bool PlayOrig() { return false; }  // receives Terrain.PlayInPuddle.Run

            void PtMove()
            {
                try
                {
                    LotLocation h = default(LotLocation);
                    ulong lot = World.GetLotLocation(Hit.mPoint, ref h);
                    if (lot == 0 || lot == ulong.MaxValue) return;
                    Vector3 v = GetClosestPuddle(lot, Hit.mPoint);
                    LotLocation c = default(LotLocation);
                    if (v != Vector3.Invalid) World.GetLotLocation(v, ref c);
                    if (!PtUseClosest(true, v != Vector3.Invalid, h.mX, h.mZ, h.mLevel, c.mX, c.mZ, c.mLevel)) return;
                    if (!ShortcutOn("petpuddletile")) { PatchCounters()[2773]++; return; }
                    Hit.mPoint = v;
                    PatchCounters()[2772]++;
                }
                catch (Exception) { PatchCounters()[2774]++; }
            }
        }

        // MopUnreachableCheck run: the hook's step on an unreachable puddle (skip when on, end when off) and the memo's
        // withhold. Controls: a mopped tile goes on, a cancel still ends, off ends, the fixed run fails only with nothing
        // mopped, and the memo drops on another lot, a changed count, the expiry or an unlisted location. Read-only.
        public static object MopUnreachableCheck(string what)
        {
            string counters = RlCounters("mopunreachable", 2766, new[] { "puddles skipped in a run", "runs that mopped after a skip", "runs ended on an unreachable puddle with others left (switch off)", "locations withheld by the memo", "memo cases seen with the switch off", "errors" }, "");
            if (what == "result") return counters;
            if (what != "run") return "mopunreachable <run|result>";
            bool on = ShortcutOn("mopunreachable");
            bool fixedNow = MuStep(on, true, false) == 2 && MuWithhold(on, true, true, 4, 4, 5f, true);
            bool controls = MuStep(true, false, false) == 0 && MuStep(true, true, true) == 1 && MuStep(false, true, false) == 1
                && MuEndsFailed(0, 2) && !MuEndsFailed(1, 2) && !MuEndsFailed(0, 0)
                && !MuWithhold(true, true, false, 4, 4, 5f, true) && !MuWithhold(true, true, true, 3, 4, 5f, true)
                && !MuWithhold(true, true, true, 4, 4, 31f, true) && !MuWithhold(true, true, true, 4, 4, 5f, false) && !MuWithhold(true, false, true, 4, 4, 5f, true);
            string missing = RlNames("Terrain+Mopping.mJig", sMuJig, "MopFloor.IsLocationMoppableBySim", sMuMoppable);
            return RlLine("mopunreachable", on, "unreachable puddle skipped and remembered", fixedNow, "mopped / cancel / off / end rule / memo expiry as EA=" + controls, controls, missing, counters);
        }

        // PetPuddleTileCheck run: a pet hit on tile (1,1) whose closest puddle is (3,1) uses (3,1). Controls: same tile,
        // other level, no closest puddle, switch off. Read-only.
        public static object PetPuddleTileCheck(string what)
        {
            string counters = RlCounters("petpuddletile", 2772, new[] { "hits moved to the puddle used", "hits that dried another puddle (switch off)", "errors" }, "");
            if (what == "result") return counters;
            if (what != "run") return "petpuddletile <run|result>";
            bool on = ShortcutOn("petpuddletile");
            bool fixedNow = PtUseClosest(on, true, 1, 1, 0, 3, 1, 0);
            bool controls = !PtUseClosest(true, true, 1, 1, 0, 1, 1, 0) && PtUseClosest(true, true, 1, 1, 0, 1, 1, 1) && !PtUseClosest(true, false, 1, 1, 0, 3, 1, 0) && !PtUseClosest(false, true, 1, 1, 0, 3, 1, 0);
            return RlLine("petpuddletile", on, "pet uses and removes the same puddle", fixedNow, "same tile / level / none / off as EA=" + controls, controls, "", counters);
        }
    }
}
