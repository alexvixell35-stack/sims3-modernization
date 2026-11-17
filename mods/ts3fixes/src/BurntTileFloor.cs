using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // burnttilefloor (tuning kBurntTileFloor, off by default; docs/277 §3, §4.1, §8). EA bug fix.
    //
    // Sim+MopBurntTiles (MopFloor, solo, hidden "clean up burnt floor") asks its definition's GetMoppableLocations(lot)
    // = World.GetBurntTiles(lot) in both its Test (offer while any tile passes CanMopPuddle) and its Run (pushes
    // Terrain.MopBurnt for the first valid tile); MopBurnt's RouteToPuddle plans a radial goal around the tile, limited to the
    // tile's room and level, and retries once with CheckForFootprintsNearGoals off [verified, decompiled]. On the SV-Spring
    // Keaton lot all 432 MopBurnt plans in our logs (async and one sync, so not our async planner) aimed at the same goal
    // 1064.7,52.8,1243.2 and ended FailedBecauseNoDestinationSpecified; the tile stays burnt, so autonomy offers it again.
    //
    // Design 2 (2026-11-17, docs/277 §8): the first design dropped burnt tiles above the ground floor with no solid floor;
    // the SV list step counted 80 burnt tiles, no floor 0, while 20 MopBurnt plans failed, so that guess matched no real tile.
    // Now EA's own planner decides: when the last plan of an autonomous MopBurnt (footprint check off, EA's fallback) ends
    // FailedBecauseNoDestinationSpecified, its goal tile is learned as unreachable for kBtMemoHours sim-hours (7 sim-days), and
    // GetMoppableLocations leaves learned tiles out. Test then answers false when only such tiles are left, and Run goes to
    // the next tile. Learned tiles are forgotten when build/buy adds or removes an object on that lot (stairs, ladders,
    // doors), after kBtMemoHours, and at world quit.
    // Household rule: only autonomous mop targets go. MopBurntTiles is only ever autonomous in EA code (solo ad, imaginary
    // friend push, both isAutonomous true [verified]); a player-ordered Terrain.MopBurnt (click on a tile) is never learned
    // from and never filtered. A plan with no Sim or no lot (unknown scope) learns nothing. The visible outcome equals
    // vanilla's: EA's planner already said no destination, so the tile is never mopped either way. Nothing is written: the
    // tile stays burnt in the save. Other GetBurntTiles callers are untouched.
    // Counters (registry fixes-1135, fixes-1139): [1135] tiles left out, [1136] lists that lost a tile, [1137] lists left
    // empty, [1138] errors, [1139] tiles learned unreachable, [1140] NoDestination fails on a tile already learned (0 = the
    // memo held). Harness check: BurntTileCheck ("list", "push", "result"), repro burnt-tile-floor.
    public static partial class Fixes
    {
        const int kCBtDropped = 1135, kCBtLists = 1136, kCBtEmpty = 1137, kCBtErr = 1138, kCBtLearned = 1139, kCBtRepeat = 1140;
        // 7 sim-days: 24 sim-hours expired within ~2 real minutes at ultra speed (rerun2 on arm: every learned tile failed again
        // 122-155 s after it was learned, docs/277 §8.3); build/buy object changes on the lot still forget at once
        const float kBtMemoHours = 168f;
        static bool sBtReady;
        // lot -> tile key -> sim-hour learned (main thread only: plan results and autonomy tests)
        static readonly Dictionary<ulong, Dictionary<int, float>> sBtUnreach = new Dictionary<ulong, Dictionary<int, float>>();

        static void ApplyBurntTileFloor()
        {
            Type mop = typeof(Sim).GetNestedType("MopBurntTiles", BindingFlags.Public | BindingFlags.NonPublic);
            Type def = mop != null ? mop.GetNestedType("Definition", BindingFlags.Public | BindingFlags.NonPublic) : null;
            if (def == null) { AppliedLog().Add("burnttilefloor: Sim+MopBurntTiles+Definition not found"); return; }
            sBtReady = Patch(def, "GetMoppableLocations", new Type[] { typeof(ulong) }, typeof(BurntTileHook), "LocHook", "LocOrig");
        }

        static int BtKey(LotLocation l) { return (l.mLevel & 0xFF) << 24 | (l.mX & 0xFFF) << 12 | (l.mZ & 0xFFF); }

        static bool BtLearned(ulong lot, LotLocation l)
        {
            Dictionary<int, float> m;
            float at;
            if (!sBtUnreach.TryGetValue(lot, out m) || !m.TryGetValue(BtKey(l), out at)) return false;
            float now = SimClock.ElapsedTime(TimeUnit.Hours);
            if (now >= at && now - at < kBtMemoHours) return true;
            m.Remove(BtKey(l));
            return false;
        }

        // RouteFailLog.RflPlan, after every failed plan: learn the goal tile of an autonomous MopBurnt whose last try found no destination
        static void BtPlanFailed(ScriptCore.Route r, Sim sim, RoutePlanResult res)
        {
            if (!sBtReady || res.mType != RoutePlanResultType.FailedBecauseNoDestinationSpecified) return;
            Terrain.MopBurnt mb = sim.CurrentInteraction as Terrain.MopBurnt;
            if (mb == null || !mb.Autonomous || !ShortcutOn("burnttilefloor")) return;
            int[] c = PatchCounters();
            try
            {
                if (r.GetOption(Route.RouteOption.CheckForFootprintsNearGoals)) return;  // EA retries with the footprint check off; only that answer counts
                Vector3 goal = r.GetApproximateOriginalDestination();
                LotLocation l = LotLocation.Invalid;
                ulong lot = World.GetLotLocation(goal, ref l);
                World.GetLotLocationAtFloor(goal, ref l);  // the level EA's RouteToPuddle limits the goals to
                if (lot == 0 || lot == ulong.MaxValue) return;  // unknown scope: keep
                if (BtLearned(lot, l)) { c[kCBtRepeat]++; return; }
                Dictionary<int, float> m;
                if (!sBtUnreach.TryGetValue(lot, out m)) sBtUnreach[lot] = m = new Dictionary<int, float>();
                if (m.Count >= 256) return;  // a lot with more unreachable tiles than that keeps EA's behaviour for the rest
                m[BtKey(l)] = SimClock.ElapsedTime(TimeUnit.Hours);
                c[kCBtLearned]++;
            }
            catch (Exception) { c[kCBtErr]++; }
        }

        // RouteFailLog.RflObjectEvent in build/buy, and world quit (lot null = all)
        static void BtForget(ulong lot)
        {
            if (sBtUnreach.Count == 0) return;
            if (lot == 0) sBtUnreach.Clear(); else sBtUnreach.Remove(lot);
        }

        public class BurntTileHook
        {
            public LotLocation[] LocHook(ulong lotId)
            {
                LotLocation[] all = LocOrig(lotId);
                if (!sBtReady || all == null || all.Length == 0 || sBtUnreach.Count == 0 || OnWorker() || !ShortcutOn("burnttilefloor")) return all;
                int[] c = PatchCounters();
                try
                {
                    if (!sBtUnreach.ContainsKey(lotId)) return all;
                    int keep = 0;
                    bool[] drop = new bool[all.Length];
                    for (int i = 0; i < all.Length; i++) { drop[i] = BtLearned(lotId, all[i]); if (!drop[i]) keep++; }
                    if (keep == all.Length) return all;
                    LotLocation[] r = new LotLocation[keep];
                    for (int i = 0, k = 0; i < all.Length; i++) if (!drop[i]) r[k++] = all[i];  // EA's order kept
                    c[kCBtDropped] += all.Length - keep; c[kCBtLists]++;
                    if (keep == 0) c[kCBtEmpty]++;
                    return r;
                }
                catch (Exception) { c[kCBtErr]++; return all; }
            }
            public LotLocation[] LocOrig(ulong lotId) { return null; } // receives Sim+MopBurntTiles+Definition.GetMoppableLocations's original header
        }

        // Harness check (test only, agent reflect_call), on the active Sim's lot:
        //   list    burnt tiles: count, levels, solid floor, learned unreachable; the first 12 with world position and room
        //   push    queues EA's MopBurntTiles (autonomous, as EA pushes it) on every household Sim on that lot, with why not
        //   result  counters 1135-1140
        public static object BurntTileCheck(string what)
        {
            int[] c = PatchCounters();
            if (what == "result")
                return "burnttilefloor: tiles left out 1135=" + c[kCBtDropped] + ", lists 1136=" + c[kCBtLists] + ", left empty 1137=" + c[kCBtEmpty]
                    + ", errors 1138=" + c[kCBtErr] + ", learned 1139=" + c[kCBtLearned] + ", repeat 1140=" + c[kCBtRepeat];
            Sim sim = Sim.ActiveActor;
            Lot lot = sim != null ? sim.LotCurrent : null;
            if (lot == null || lot.IsWorldLot) return "burnttilefloor check: no active Sim on a lot";
            if (what == "list")
            {
                LotLocation[] t = World.GetBurntTiles(lot.LotId, LotLocation.Invalid);
                StringBuilder b = new StringBuilder();
                int nf = 0, learned = 0;
                int[] lv = new int[8];
                for (int i = 0; i < t.Length; i++)
                {
                    bool floor = World.HasSolidFloor(lot.LotId, t[i]);
                    bool un = BtLearned(lot.LotId, t[i]);
                    if (!floor) nf++;
                    if (un) learned++;
                    lv[t[i].mLevel & 7]++;
                    if (i >= 12) continue;
                    Vector3 p = Vector3.Invalid;
                    World.GetWorldPosition(lot.LotId, t[i], ref p);
                    LotLocation at = LotLocation.Invalid;
                    World.GetLotLocationAtFloor(p, ref at);
                    b.Append(" [").Append(t[i].mX).Append(',').Append(t[i].mZ).Append(" L").Append(t[i].mLevel).Append(" r").Append(at.mRoom)
                        .Append(" at ").Append(V(p)).Append(floor ? " floor" : " nofloor").Append(un ? " unreachable]" : "]");
                }
                StringBuilder lvs = new StringBuilder();
                for (int i = 0; i < lv.Length; i++) if (lv[i] > 0) lvs.Append(" L").Append(i).Append('=').Append(lv[i]);
                return "burnttilefloor: lot " + lot.LotId.ToString("X") + " burnt tiles " + t.Length + ", levels" + lvs + ", no floor " + nf
                    + ", learned unreachable " + learned + " (switch " + (ShortcutOn("burnttilefloor") ? "on" : "off") + "):" + b;
            }
            if (what == "push")
            {
                int n = 0, other = 0, pet = 0, noIa = 0, refused = 0;
                foreach (Sim s in lot.GetSims())
                {
                    if (s == null || s.Household != sim.Household) { other++; continue; }
                    if (s.IsPet) { pet++; continue; }
                    InteractionInstance ii = Sim.MopBurntTiles.Singleton.CreateInstance(s, s, new InteractionPriority(InteractionPriorityLevel.Autonomous), true, true);
                    if (ii == null) noIa++;
                    else if (s.InteractionQueue.Add(ii)) n++;
                    else refused++;
                }
                return "burnttilefloor: MopBurntTiles queued for " + n + " household Sims on lot " + lot.LotId.ToString("X") + " (not household " + other + ", pets " + pet
                    + ", no instance " + noIa + ", queue refused " + refused + "; switch " + (ShortcutOn("burnttilefloor") ? "on" : "off") + ")";
            }
            return "burnttilefloor <list|push|result>";
        }
    }
}
