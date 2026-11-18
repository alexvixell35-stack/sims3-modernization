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
    // [3810] goals learned, [3811] check mismatches (memo or seed true where EA said false; must stay 0), [3812] seed hits (no memo
    // yet: the 1-goal plan to the street-side edge midpoint succeeded, true), [3813] seed misses (EA's full call followed).
    // Seed: with no memo for the profile, the check first plans to the edge midpoint nearest the lot's street side (Lot.LotEntryPnts[0],
    // else Lot.EntryPoint); exact for the same reason as a memo hit. A lot's seed stops after 2 misses. The memo is its own per-lot
    // dictionary (SemLot, up to 512 lots), kept across sim-days.
    // r99 (s79: EA's plan reached goal 7 in 129 ms, goal 7 alone 116 ms, the seed goal 3 alone 13 ms): each learned goal keeps an
    // EWMA cost of its single-goal plan ms (the seed's too; a failed try counts, so it sinks). A check plans the learned goals ALONE,
    // cheapest first, at most 2 tries, then EA's full call; over the 2-goal cap the most expensive goal is evicted.
    // [3814] first single try hits.
    public static partial class Fixes
    {
        sealed class SemEntry
        {
            public int Mask, FullMs;
            public readonly float[] Cost = { -1f, -1f, -1f, -1f, -1f, -1f, -1f, -1f };  // per goal: EWMA of its single-goal plan ms, -1 = unknown
        }
        // Per-lot hints, kept across sim-days (a stale goal costs one extra plan, never a wrong answer): dropped on world load or
        // quit, lot added or removed, the clock going back (another save), or when the lot moved or its corners changed (replaced).
        sealed class SemLot { public Vector3 LotPos, Corner0, Corner2; public int Seed = -2, SeedFails; public readonly Dictionary<VerdictKey, SemEntry> Edge = new Dictionary<VerdictKey, SemEntry>(new VerdictKeyComparer()); }
        static readonly Dictionary<ulong, SemLot> sSemLots = new Dictionary<ulong, SemLot>();
        const int kSemMaxLots = 512, kSemMaxSeedFails = 2;
        static long sSemTick = -1;
        static void SemForget(object sender, EventArgs e) { sSemLots.Clear(); }
        static void SemHookWorld()
        {
            World.OnWorldLoadStartedEventHandler += SemForget;
            World.OnWorldQuitEventHandler += SemForget;
            World.OnLotAddedEventHandler += SemForget;
            World.OnLotRemovedEventHandler += SemForget;
        }
        static SemLot SemLotOf(Lot lot)
        {
            long now = Sims3.Gameplay.Utilities.SimClock.CurrentTicks;
            if (now < sSemTick) sSemLots.Clear();
            sSemTick = now;
            Vector3 c0 = lot.Corners[0], c2 = lot.Corners[2];
            SemLot m;
            if (sSemLots.TryGetValue(lot.LotId, out m) && (lot.Position - m.LotPos).Length() <= 0.5f && (c0 - m.Corner0).Length() <= 0.5f
                && (c2 - m.Corner2).Length() <= 0.5f) return m;
            if (sSemLots.Count >= kSemMaxLots) sSemLots.Clear();
            m = new SemLot();
            m.LotPos = lot.Position; m.Corner0 = c0; m.Corner2 = c2;
            sSemLots[lot.LotId] = m;
            return m;
        }

        // The seed goal: the edge midpoint nearest the lot's street side (Lot.LotEntryPnts[0], EA's road-nearest entry point
        // between an edge midpoint and the lot center), else the front door or mailbox (Lot.EntryPoint); -1 = none
        static int SemSeedOf(Lot lot, Vector3[] centers)
        {
            Vector3[] entry = lot.LotEntryPnts;
            Vector3 at = entry != null && entry.Length > 0 ? entry[0] : lot.EntryPoint();
            if (at == Vector3.Invalid || (at - lot.Position).Length() < 0.01f) return -1;  // EA's fallback: the lot position, no side
            return SemNearestEdge(centers, at);
        }
        // Pure: the edge midpoint (odd index) nearest `at` in x/z
        internal static int SemNearestEdge(Vector3[] centers, Vector3 at)
        {
            int best = -1;
            float bestD = float.MaxValue;
            for (int i = 1; i < centers.Length; i += 2)
            {
                float dx = centers[i].x - at.x, dz = centers[i].z - at.z, d = dx * dx + dz * dz;
                if (d < bestD) { bestD = d; best = i; }
            }
            return best;
        }
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
        // Pure: add goal idx to the memo, at most kSemMaxGoals per lot and profile (more would bring back the flood toward goals this
        // spot can't reach). Over the cap the most expensive goal leaves (unknown cost counts as most expensive; ties: higher index).
        const int kSemMaxGoals = 2, kSemMaxTries = 2;
        internal static int SemAdd(int mask, int idx, float[] cost)
        {
            int m = (mask & 0xFF) | (1 << idx);
            while (SemBits(m) > kSemMaxGoals)
            {
                int worst = -1;
                for (int i = 0; i < 8; i++)
                    if (i != idx && (m & (1 << i)) != 0 && (worst < 0 || SemCostKey(cost[i]) >= SemCostKey(cost[worst]))) worst = i;
                m &= ~(1 << worst);
            }
            return m;
        }
        static float SemCostKey(float c) { return c < 0f ? float.MaxValue : c; }
        // Pure: the order of single-goal tries: cheapest known cost first (unknown last, ties: lower index), at most kSemMaxTries
        internal static int[] SemOrder(int mask, float[] cost)
        {
            List<int> g = new List<int>();
            for (int i = 0; i < 8; i++) if ((mask & (1 << i)) != 0) g.Add(i);
            g.Sort(delegate (int a, int b) { int k = SemCostKey(cost[a]).CompareTo(SemCostKey(cost[b])); return k != 0 ? k : a.CompareTo(b); });
            if (g.Count > kSemMaxTries) g.RemoveRange(kSemMaxTries, g.Count - kSemMaxTries);
            return g.ToArray();
        }
        // Pure: EWMA of a goal's plan ms (alpha 0.3; the first sample sets it)
        internal static float SemEwma(float old, int ms) { return old < 0f ? ms : old + 0.3f * (ms - old); }
        static string SemCosts(SemEntry e)
        {
            if (e == null) return "none";
            string t = "";
            for (int i = 0; i < 8; i++) if ((e.Mask & (1 << i)) != 0) t += (t.Length > 0 ? ", " : "") + "goal " + i + " " + (e.Cost[i] < 0f ? "?" : Math.Round(e.Cost[i]) + " ms");
            return t.Length > 0 ? t : "none";
        }
        // Pure: 0 = EA's full call, 1 = memo true (no full call), 2 = memo failed: EA's full call answers
        internal static int SemNext(bool on, int mask, bool memoOk) { return !on || (mask & 0xFF) == 0 ? 0 : memoOk ? 1 : 2; }

        // spotcut / spotbusydefer: this check's job was cut or deferred, SpotVerdict answers no verdict, so no second plan
        static bool SemNoVerdict(ulong task)
        {
            return (sSpotCut.Count > 0 && sSpotCut.ContainsKey(task)) || (sSpotDeferred.Count > 0 && sSpotDeferred.ContainsKey(task));
        }

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
            SemLot m = SemLotOf(lot);
            VerdictKey key = KeyOf(p, Vector3.Zero);
            key.Cell = 0;
            SemEntry e;
            m.Edge.TryGetValue(key, out e);
            ulong task = Simulator.CurrentTask.Value;
            if (e != null && e.Mask != 0)
            {
                // each learned goal alone, cheapest first, at most 2 tries; any success = true (exact), then EA's full call
                int[] order = SemOrder(e.Mask, e.Cost);
                int spent = 0;
                for (int k = 0; k < order.Length; k++)
                {
                    int g = order[k], t0 = System.Environment.TickCount;
                    bool ok = SemPlan(sim, centers, radius, 1 << g, pos).mType == RoutePlanResultType.Succeeded;
                    int ms = unchecked(System.Environment.TickCount - t0);
                    spent += ms;
                    e.Cost[g] = SemEwma(e.Cost[g], ms);  // a failed try floods: its cost rises, so it is tried later
                    if (ok)
                    {
                        c[3806]++;
                        if (k == 0) c[3814]++;
                        if (e.FullMs > spent) c[3809] += e.FullMs - spent;
                        return true;
                    }
                    // spotcut / spotbusydefer: this check answers no verdict (SpotVerdict null), so no further plan
                    if (SemNoVerdict(task)) return false;
                }
                c[3808]++;
            }
            else
            {
                c[3807]++;
                // seed (no memo for this profile yet): one goal, the street-side edge midpoint; success = true, as a memo hit
                if (m.Seed == -2) m.Seed = SemSeedOf(lot, centers);
                if (m.Seed >= 0 && m.SeedFails < kSemMaxSeedFails)
                {
                    int t0 = System.Environment.TickCount;
                    if (SemPlan(sim, centers, radius, 1 << m.Seed, pos).mType == RoutePlanResultType.Succeeded)
                    {
                        c[3812]++;
                        if (e == null) { e = new SemEntry(); m.Edge[key] = e; }
                        e.Cost[m.Seed] = SemEwma(e.Cost[m.Seed], unchecked(System.Environment.TickCount - t0));
                        e.Mask = SemAdd(e.Mask, m.Seed, e.Cost);
                        return true;
                    }
                    c[3813]++;
                    m.SeedFails++;
                    if (SemNoVerdict(task)) return false;
                }
            }
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
                    if (e == null) { e = new SemEntry(); m.Edge[key] = e; }
                    if ((e.Mask & (1 << idx)) == 0) c[3810]++;
                    int before = e.Mask;
                    e.Mask = SemAdd(e.Mask, idx, e.Cost);
                    for (int i = 0; i < 8; i++) if ((before & ~e.Mask & (1 << i)) != 0) e.Cost[i] = -1f;  // evicted: forget its cost
                    e.FullMs = unchecked(System.Environment.TickCount - t1);
                }
            }
            return full;
        }

        // SpotEdgeMemoCheck run: on a real lot (a Sim's own spot, Bridgeport's cemetery first), EA's 8-goal call vs the memo
        // route to the goal EA's plan reached: fewer goals, same verdict. probe: every goal alone (ms, result) and EA's plan
        // distance vs the straight distance, for the docs/636 hypothesis (a failed goal floods the lot: probe hitches).
        // Pure test lot: corners (0,0) (0,10) (10,10) (10,0) in EA's goal order (corner i, then the midpoint of edge i..i+1)
        static Vector3[] SemSquare()
        {
            Vector3[] k = { new Vector3(0f, 0f, 0f), new Vector3(0f, 0f, 10f), new Vector3(10f, 0f, 10f), new Vector3(10f, 0f, 0f) };
            Vector3[] c = new Vector3[8];
            for (int i = 0; i < 4; i++) { c[2 * i] = k[i]; c[2 * i + 1] = (k[i] + k[(i + 1) % 4]) * 0.5f; }
            return c;
        }
        public static object SpotEdgeMemoCheck(string what)
        {
            string counters = B582Counters("spotedgememo", 3806, new[] { "memo hits", "misses", "fallbacks", "ms saved est", "goals learned", "check mismatches", "seed hits", "seed misses", "first single try hits" }, "");
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
                int seed = SemSeedOf(lot, centers);
                if (seed >= 0)
                {
                    t0 = System.Environment.TickCount;
                    bool seedOk = SemPlan(sim, centers, radius, 1 << seed, pos).mType == RoutePlanResultType.Succeeded;
                    live += "; seed goal " + seed + " " + seedOk + " in " + unchecked(System.Environment.TickCount - t0) + " ms";
                    if (seedOk && !eaOk) { PatchCounters()[3811]++; liveOk = false; }
                }
                else live += "; no seed (no street side)";
                // the memo the hook uses for this Sim's profile on this lot: per-goal costs and the single-goal try order
                SemEntry me = null;
                VerdictKey mk = KeyOf(ProfileOf(sim), Vector3.Zero);
                mk.Cell = 0;
                SemLotOf(lot).Edge.TryGetValue(mk, out me);
                live += "; memo costs: " + SemCosts(me);
                if (me != null && me.Mask != 0)
                {
                    int[] ord = SemOrder(me.Mask, me.Cost);
                    live += ", tried first goal " + ord[0] + (ord.Length > 1 ? " then " + ord[1] : "");
                }
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
                && SemNext(true, 0x1, false) == 2 && SemNext(true, 0x1, true) == 1 && liveOk
                && SemNearestEdge(SemSquare(), new Vector3(2.5f, 0f, 5f)) == 1 && SemNearestEdge(SemSquare(), new Vector3(5f, 0f, 8f)) == 3 && PatchCounters()[3811] == 0;
            // eviction keeps the cheap goal (s79 cemetery: goal 3 13 ms, goal 7 116 ms); the order tries the cheap one first, at most 2
            float[] tc = { -1f, 50f, -1f, 13f, -1f, -1f, -1f, 116f };
            int ev = SemAdd((1 << 3) | (1 << 7), 1, tc);
            int[] to = SemOrder((1 << 3) | (1 << 7), tc), tu = SemOrder((1 << 1) | (1 << 3) | (1 << 7), tc);
            controls = controls && ev == ((1 << 1) | (1 << 3)) && SemAdd(1 << 3, 7, tc) == ((1 << 3) | (1 << 7)) && SemAdd(1 << 3, 3, tc) == (1 << 3)
                && to.Length == 2 && to[0] == 3 && to[1] == 7 && tu.Length == 2 && tu[0] == 3 && tu[1] == 1
                && SemEwma(-1f, 100) == 100f && Math.Abs(SemEwma(100f, 0) - 70f) < 0.01f;
            return B582Line("spotedgememo", on, "the check plans only the goals a full plan reached, same verdict", liveFewer && liveOk,
                "off / no memo = EA's 8-goal call, memo failure = EA's full call; live: " + live, controls, "", counters);
        }
    }
}
