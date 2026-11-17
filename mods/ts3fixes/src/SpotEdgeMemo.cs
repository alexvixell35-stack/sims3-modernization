using System;
using System.Collections.Generic;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Core;
using Sims3.SimIFace;
using Sims3.SimIFace.RouteDestinations;

namespace Ts3Fixes
{
    // ---- docs/636 spotedgememo (default off) -----------------------------------------------------------------------------------
    // Sim.IsPointInLotSafelyRoutable plans one route from the spot to 8 RadialRangeDestinations (the lot's 4 corners and 4 edge
    // midpoints, radius 1 m, 2 m on a houseboat lot). Bridgeport Eternal Rest Cemetery's checks are the slowest spot checks of
    // the 2026-11-1x runs (300-578 ms, all Succeeded): the planner works on all 8 goals while only some edges can be reached.
    // With the switch on, the check remembers per lot and route profile (SpotVerdicts.Edge, dropped with the lot's verdicts:
    // a sim-day, a household change, a lot move, the clock going back) which destination indices a successful full plan
    // actually reached (its PlanResult.mDestination, matched to the nearest goal). The next check plans the same route (same
    // CreateRouteAsAdult, EnableWaterPlanning = IsHuman, same radius) to the remembered goals only:
    //   - success: a path from the spot to one of EA's own goals exists, so EA's 8-goal plan succeeds too: true;
    //   - failure: EA's own full call answers (true or false), so a "false" is always EA's own verdict; the memo is relearned.
    // Exact [inferred: a multi-goal plan succeeds when any goal is reachable; the planner's abort budget is the one way an
    // 8-goal plan could fail where a fewer-goal plan succeeds, not seen]. Stale memos (build/buy, doors) cost only a fallback,
    // never a wrong answer. Household Sims use it too (proven-exact speedup, CLAUDE.md). A spotasync memo plan that was cut or
    // deferred (spotcut / spotbusydefer) returns false without the fallback plan: SpotVerdict discards that verdict anyway.
    // Counters: [3806] memo hits (true from the memo plan), [3807] misses (no memo: EA's full call), [3808] fallbacks (memo plan
    // failed: EA's full call), [3809] ms saved estimate (the lot's last full-call ms minus the memo plan's ms, per hit),
    // [3810] goals learned, [3811] check mismatches (memo true where EA said false; must stay 0).
    public static partial class Fixes
    {
        sealed class SemEntry { public int Mask; public int FullMs; }
        // task -> the last PlanFromPoint result inside an armed full call (RouteBackoffHook.PlanFromPointHook)
        static readonly Dictionary<ulong, RoutePlanResult> sSemCap = new Dictionary<ulong, RoutePlanResult>();
        static bool sSemBypass;  // SpotEdgeMemoCheck: EA's own call, no memo

        static void SemCapture(RoutePlanResult r)
        {
            if (sSemCap.Count == 0) return;
            ulong task = Simulator.CurrentTask.Value;
            if (sSemCap.ContainsKey(task)) sSemCap[task] = r;
        }

        // EA's early answers decide without a plan: run EA's own call for them
        static bool SemEligible(Sim sim, Lot lot, Vector3 pos, out Vector3[] centers, out float radius)
        {
            centers = null; radius = 1f;
            if (sim == null || sim.SimRoutingComponent == null || lot == null || lot.IsWorldLot || lot.Corners == null) return false;
            if (pos.IsSimilarTo(Vector3.Zero) || pos.IsSimilarTo(Vector3.OutOfWorld)) return false;
            LotLocation loc = LotLocation.Invalid;
            if (World.GetLotLocation(pos, ref loc) != lot.LotId) return false;
            TerrainType t = World.GetTerrainType(pos);
            if (t == TerrainType.WorldSea || t == TerrainType.WorldPond || t == TerrainType.LotPool || t == TerrainType.LotPond) return false;
            if (lot.IsHouseboatLot()) radius = 2f;
            centers = new Vector3[8];
            for (int i = 0; i < 4; i++)
            {
                centers[2 * i] = lot.Corners[i];
                centers[2 * i + 1] = (lot.Corners[i] + lot.Corners[(i + 1) % 4]) * 0.5f;
            }
            return true;
        }

        // The goal a successful plan reached: the nearest center within the radius (+0.25 m), else -1
        static int SemMatch(Vector3[] centers, float radius, Vector3 dest)
        {
            int best = -1;
            float bestD = radius + 0.25f;
            for (int i = 0; i < centers.Length; i++)
            {
                float dx = centers[i].x - dest.x, dz = centers[i].z - dest.z, d = (float)Math.Sqrt(dx * dx + dz * dz);
                if (d <= bestD) { bestD = d; best = i; }
            }
            return best;
        }

        // EA's route, its goals limited to `mask` (same order, same radius)
        static RoutePlanResult SemPlan(Sim sim, Vector3[] centers, float radius, int mask, Vector3 pos)
        {
            Route r = sim.SimRoutingComponent.CreateRouteAsAdult();
            r.SetOption(Route.RouteOption.EnableWaterPlanning, sim.IsHuman);
            for (int i = 0; i < 8; i++)
            {
                if ((mask & (1 << i)) == 0) continue;
                RadialRangeDestination d = new RadialRangeDestination();
                d.mCenterPoint = centers[i];
                d.mfMinRadius = 0f;
                d.mfMaxRadius = radius;
                r.AddDestination(d);
            }
            r.PlanFromPoint(pos);
            return r.PlanResult;
        }

        static int SemBits(int m) { int n = 0; for (; m != 0; m &= m - 1) n++; return n; }

        // Pure: goals the check plans first (8 = EA's full call)
        internal static int SemGoals(bool on, int mask) { return on && (mask & 0xFF) != 0 ? SemBits(mask & 0xFF) : 8; }
        // Pure: 0 = EA's full call, 1 = memo true (no full call), 2 = memo failed: EA's full call answers
        // Pure: add goal idx to the memo, at most 4 goals per lot (several gates on big lots; more would bring
        // back the flood toward goals this spot can't reach). Past 4, start over from the goal just reached.
        internal static int SemAdd(int mask, int idx) { int m = mask | (1 << idx); return SemBits(m & 0xFF) > 4 ? 1 << idx : m; }
        internal static int SemNext(bool on, int mask, bool memoOk) { return !on || (mask & 0xFF) == 0 ? 0 : memoOk ? 1 : 2; }

        // RoutableCacheHook's EA call: the memo route first when on
        static bool SemRoutable(RoutableCacheHook h, Lot lot, Vector3 pos)
        {
            Sim sim = (Sim)(object)h;
            Vector3[] centers;
            float radius;
            if (sSemBypass || !ShortcutOn("spotedgememo") || !SemEligible(sim, lot, pos, out centers, out radius)) return h.RoutableOrig(lot, pos);
            RouteProfile p = ProfileOf(sim);
            if (!p.Ok) return h.RoutableOrig(lot, pos);
            int[] c = PatchCounters();
            SpotVerdicts v = Verdicts(lot);
            VerdictKey key = KeyOf(p, Vector3.Zero);
            key.Cell = 0;
            SemEntry e;
            v.Edge.TryGetValue(key, out e);
            ulong task = Simulator.CurrentTask.Value;
            if (e != null && e.Mask != 0)
            {
                int t0 = System.Environment.TickCount;
                bool ok = SemPlan(sim, centers, radius, e.Mask, pos).mType == RoutePlanResultType.Succeeded;
                int ms = unchecked(System.Environment.TickCount - t0);
                if (SemNext(true, e.Mask, ok) == 1) { c[3806]++; if (e.FullMs > ms) c[3809] += e.FullMs - ms; return true; }
                c[3808]++;
                // keep the learned goals: the full call below adds the one this spot reaches (up to 4, see SemAdd)
                // spotcut / spotbusydefer: this check answers no verdict (SpotVerdict null), so no second plan
                if ((sSpotCut.Count > 0 && sSpotCut.ContainsKey(task)) || (sSpotDeferred.Count > 0 && sSpotDeferred.ContainsKey(task))) return false;
            }
            else c[3807]++;
            bool full;
            int t1 = System.Environment.TickCount;
            RoutePlanResult r;
            if (sSemCap.Count >= 64) sSemCap.Clear();
            sSemCap[task] = default(RoutePlanResult);
            try { full = h.RoutableOrig(lot, pos); }
            finally { if (!sSemCap.TryGetValue(task, out r)) r = default(RoutePlanResult); sSemCap.Remove(task); }
            if (full && r.mType == RoutePlanResultType.Succeeded)
            {
                int idx = SemMatch(centers, radius, r.mDestination);
                if (idx >= 0)
                {
                    if (e == null) { e = new SemEntry(); v.Edge[key] = e; }
                    if ((e.Mask & (1 << idx)) == 0) c[3810]++;
                    e.Mask = SemAdd(e.Mask, idx);
                    e.FullMs = unchecked(System.Environment.TickCount - t1);
                }
            }
            return full;
        }

        // SpotEdgeMemoCheck run: on a real lot (a Sim's own spot, Bridgeport's cemetery first), EA's 8-goal call vs the memo
        // route to the goal EA's plan reached: fewer goals, same verdict. probe: every goal alone (ms, result) and EA's plan
        // distance vs the straight distance, for the docs/636 hypothesis (a failed goal floods the lot: probe hitches).
        public static object SpotEdgeMemoCheck(string what)
        {
            string counters = B582Counters("spotedgememo", 3806, new[] { "memo hits", "misses", "fallbacks", "ms saved est", "goals learned", "check mismatches" }, "");
            if (what == "result") return counters;
            if (what != "run" && what != "probe") return "spotedgememo <run|probe|result>";
            bool on = ShortcutOn("spotedgememo");
            Sim sim = null; Lot lot = null; Vector3 pos = Vector3.Zero; Vector3[] centers = null; float radius = 1f;
            foreach (Sim s in Sims3.Gameplay.Queries.GetObjects<Sim>())
            {
                Vector3[] cs; float rad;
                if (s == null || s.HasBeenDestroyed || s.LotCurrent == null || !ProfileOf(s).Ok || !SemEligible(s, s.LotCurrent, s.Position, out cs, out rad)) continue;
                bool cem = s.LotCurrent.Name != null && s.LotCurrent.Name.IndexOf("Cemetery", StringComparison.OrdinalIgnoreCase) >= 0;
                if (sim == null || cem) { sim = s; lot = s.LotCurrent; pos = s.Position; centers = cs; radius = rad; }
                if (cem) break;
            }
            string live = "no Sim on an eligible lot spot";
            bool liveOk = false, liveFewer = false;
            if (sim != null)
            {
                ulong task = Simulator.CurrentTask.Value;
                bool eaOk; int eaMs; RoutePlanResult r;
                sSemBypass = true; sInOwnCheck = true;
                sSemCap[task] = default(RoutePlanResult);
                int t0 = System.Environment.TickCount;
                try { eaOk = sim.IsPointInLotSafelyRoutable(lot, pos); }
                finally
                {
                    eaMs = unchecked(System.Environment.TickCount - t0);
                    sSemBypass = false; sInOwnCheck = false;
                    if (!sSemCap.TryGetValue(task, out r)) r = default(RoutePlanResult);
                    sSemCap.Remove(task);
                }
                int idx = eaOk ? SemMatch(centers, radius, r.mDestination) : -1;
                live = sim.FullName + " on " + lot.Name + ": EA 8 goals " + eaOk + " in " + eaMs + " ms";
                if (idx >= 0)
                {
                    float dx = centers[idx].x - pos.x, dz = centers[idx].z - pos.z;
                    live += ", reached goal " + idx + (idx % 2 == 0 ? " (corner)" : " (edge mid)") + ", path " + Math.Round(r.mDistanceTotal, 1)
                        + " m vs straight " + Math.Round(Math.Sqrt(dx * dx + dz * dz), 1) + " m";
                    int mask = 1 << idx;
                    t0 = System.Environment.TickCount;
                    bool memoOk = SemPlan(sim, centers, radius, mask, pos).mType == RoutePlanResultType.Succeeded;
                    int memoMs = unchecked(System.Environment.TickCount - t0);
                    live += "; memo " + SemGoals(true, mask) + " goal " + memoOk + " in " + memoMs + " ms";
                    liveOk = memoOk == eaOk;
                    liveFewer = SemGoals(on, mask) < 8;
                }
                else if (eaOk) live += ", reached goal not matched (dest " + r.mDestination + ")";
                else live += " (EA false: the memo never answers false, nothing to compare)";
                if (what == "probe" && centers != null)
                {
                    live += "; per goal:";
                    for (int i = 0; i < 8; i++)
                    {
                        t0 = System.Environment.TickCount;
                        RoutePlanResult g = SemPlan(sim, centers, radius, 1 << i, pos);
                        live += " " + i + "=" + (g.mType == RoutePlanResultType.Succeeded ? "ok " + Math.Round(g.mDistanceTotal, 1) + "m" : g.mType.ToString())
                            + "/" + unchecked(System.Environment.TickCount - t0) + "ms";
                    }
                }
                if (eaOk && idx >= 0 && !liveOk) PatchCounters()[3811]++;
            }
            bool controls = SemGoals(false, 0x1) == 8 && SemGoals(true, 0) == 8 && SemNext(false, 0x1, true) == 0 && SemNext(true, 0, true) == 0
                && SemNext(true, 0x1, false) == 2 && SemNext(true, 0x1, true) == 1 && liveOk && PatchCounters()[3811] == 0;
            return B582Line("spotedgememo", on, "the check plans only the goals a full plan reached, same verdict", liveFewer && liveOk,
                "off / no memo = EA's 8-goal call, memo failure = EA's full call; live: " + live, controls, "", counters);
        }
    }
}
