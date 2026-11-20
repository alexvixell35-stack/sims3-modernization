using System;
using Ts3Fixes;

// Offline self-check of ActDelayCore (docs/684): the bucket split of one interaction instance from the stamps the hooks
// give, the rules for "acted" (first route step, or an animation set-up well before any plan), the aggregate, and
// random event streams in which every finished instance's buckets must add up to its queued -> act time.
static class ActDelayTest
{
    static int sFails, sChecks;
    static void Check(bool ok, string what) { sChecks++; if (!ok) { sFails++; if (sFails < 20) Console.WriteLine("FAIL " + what); } }
    const long ms = 1000;
    const ulong S = 42;

    static long Sum(ActDelayCore.Result r) { long s = 0; for (int i = 0; i < ActDelayCore.kBuckets; i++) s += r.Us[i]; return s; }

    static int Main()
    {
        Check(ActDelayCore.Model() == "", "model: " + ActDelayCore.Model());

        // histogram: a bin's value lies in the bin, bins rise, percentiles within 12.5 %
        int last = -1;
        for (int v = 0; v < 4000000; v += v < 5000 ? 1 : 997)
        {
            int b = ActDelayCore.Bin(v);
            Check(b >= last && b < ActDelayCore.kBins, "bin order at " + v);
            last = b;
            int mid = ActDelayCore.BinValue(b);
            Check(ActDelayCore.Bin(mid) == b, "bin value " + mid + " of bin " + b + " (" + v + ")");
            Check(Math.Abs(mid - v) <= Math.Max(1, v / 8), "bin value " + mid + " far from " + v);
        }
        Check(ActDelayCore.Bin(int.MaxValue) < ActDelayCore.kBins, "bin of the largest int");
        int[] h = new int[ActDelayCore.kBins];
        for (int v = 1; v <= 1000; v++) h[ActDelayCore.Bin(v)]++;
        int p50 = ActDelayCore.Percentile(h, 1000, 50), p90 = ActDelayCore.Percentile(h, 1000, 90);
        Check(p50 >= 440 && p50 <= 570, "p50 " + p50);
        Check(p90 >= 790 && p90 <= 1010, "p90 " + p90);
        Check(ActDelayCore.Percentile(h, 0, 50) == 0, "empty percentile");

        // 1. no route: Run sets the Sim up on a state machine, never plans; finished when Run ends, act = the set-up
        ActDelayCore c = new ActDelayCore();
        object a = new object();
        c.Queued(a, "T1", S, ActDelayCore.kSelected, false, true, true, 10 * ms, 100);
        c.Push(a, a, S, false, 30 * ms, 101);
        c.Anim(S, 80 * ms, 102);
        c.Anim(S, 500 * ms, 110);
        ActDelayCore.Result r = c.Pop(a, S, false, 5000 * ms, 300);
        Check(r != null && r.How == ActDelayCore.kHowAnim && !r.Routed && !r.NoAct, "1: finished by the animation");
        Check(r.Ms(ActDelayCore.kTotal) == 70 && r.Ms(ActDelayCore.kWake) == 20 && r.Ms(ActDelayCore.kPreRoute) == 50 && r.Ms(ActDelayCore.kQueue) == 0, "1: buckets");
        Check(Sum(r) == r.Us[ActDelayCore.kTotal], "1: sum");
        Check(c.Removed(a, S, true, true, 5000 * ms, 300) == null && c.OpenCount == 0, "1: removal after the finish");
        string line = ActDelayCore.Line(r, "T1", "A Sim", "day 1 hour 8.00");
        Check(line.StartsWith("actdelay: 70 ms queued -> anim, selected user T1: queue 0, wake 20, prerun 0, preroute 50, no plan; first visible 70"), "1: line: " + line);
        Check(line.Length <= 470, "1: line length " + line.Length);

        // 2. the pick gap: the queue stood empty 3 s before the Sim picked something itself
        object b2 = new object();
        c.Queued(b2, "T2", S, ActDelayCore.kSelected, true, true, true, 8000 * ms, 400);
        Check(c.StatN(ActDelayCore.kSelected, ActDelayCore.kPick) == 1 && Math.Abs(c.StatP(ActDelayCore.kSelected, ActDelayCore.kPick, 50) - 3000) < 400, "2: pick gap");
        // set up on a state machine and a plan in the same tick: the route is the act
        c.Push(b2, b2, S, false, 8010 * ms, 400);
        c.Anim(S, 8020 * ms, 401);
        c.PlanBegin(S, 8030 * ms, 401); c.PlanEnd(S, true, 8130 * ms);
        r = c.Move(S, 8200 * ms, 405);
        Check(r != null && r.How == ActDelayCore.kHowMove && r.Routed && r.Ms(ActDelayCore.kTotal) == 200, "2: route is the act");
        Check(r.Ms(ActDelayCore.kPreRoute) == 20 && r.Ms(ActDelayCore.kPlanCpu) == 100 && r.Ms(ActDelayCore.kPlanJob) == 0 && r.Ms(ActDelayCore.kPostPlan) == 70 && r.Ms(ActDelayCore.kRetry) == 0, "2: buckets");
        Check(Sum(r) == r.Us[ActDelayCore.kTotal], "2: sum");
        line = ActDelayCore.Line(r, "T2", null, null);
        Check(line.Contains("plan-cpu 100, plan-job 0, retry 0, postplan 70 (1 plans, 0 failed, 0 job waits)"), "2: line: " + line);

        // 3. the animation ran 20 ticks before a later route: the animation was the act, the plan is not in the buckets
        object c3 = new object();
        c.Queued(c3, "T3", S, ActDelayCore.kNpc, true, true, true, 9000 * ms, 500);
        c.Push(c3, c3, S, false, 9000 * ms, 500);
        c.Anim(S, 9100 * ms, 501);
        c.PlanBegin(S, 9900 * ms, 521); c.PlanEnd(S, true, 9950 * ms);
        r = c.Move(S, 9960 * ms, 522);
        Check(r != null && r.How == ActDelayCore.kHowAnim && !r.Routed && r.Ms(ActDelayCore.kTotal) == 100 && r.Ms(ActDelayCore.kPreRoute) == 100, "3: animation first");
        Check(Sum(r) == r.Us[ActDelayCore.kTotal], "3: sum");

        // 4. route failure, nothing followed: ended without acting; logged because it planned; out of the percentiles
        int acted = c.Acted;
        object d4 = new object();
        c.Queued(d4, "T4", S, ActDelayCore.kNpc, true, true, true, 20000 * ms, 600);
        c.Push(d4, d4, S, false, 20000 * ms, 600);
        c.PlanBegin(S, 20010 * ms, 600); c.JobWaited(S, 700 * ms); c.PlanEnd(S, false, 20800 * ms);
        r = c.Pop(d4, S, false, 23000 * ms, 700);
        Check(r != null && r.NoAct && r.Over && r.How == ActDelayCore.kHowEnd && c.Acted == acted && c.NoAct == 1, "4: ended without acting");
        Check(r.Ms(ActDelayCore.kPlanJob) == 700 && r.Ms(ActDelayCore.kPlanCpu) == 90 && r.Ms(ActDelayCore.kPostPlan) == 2200 && r.Fails == 1, "4: buckets");
        Check(ActDelayCore.Line(r, "T4", null, null).Contains("queued -> ended without acting"), "4: line");

        // 5. cancelled in the queue before Run; the next head counts its queue time from the removal of the one before
        object e5 = new object(), f5 = new object(), g5 = new object();
        c.Queued(e5, "T5", S, ActDelayCore.kHousehold, false, true, true, 30000 * ms, 800);
        c.Queued(f5, "T5", S, ActDelayCore.kHousehold, false, false, false, 30100 * ms, 800);
        c.Queued(g5, "T5", S, ActDelayCore.kHousehold, false, false, false, 30200 * ms, 800);
        c.Push(e5, e5, S, false, 30300 * ms, 801);
        Check(c.Removed(f5, S, false, false, 30400 * ms, 802) == null && c.Cancelled == 1, "5: cancelled");
        r = c.Move(S, 30500 * ms, 803);
        Check(r != null && r.Ms(ActDelayCore.kWake) == 300 && r.Ms(ActDelayCore.kTotal) == 500, "5: first");
        c.Pop(e5, S, false, 31000 * ms, 820);
        c.Removed(e5, S, true, false, 31000 * ms, 820);
        c.Push(g5, g5, S, false, 31250 * ms, 829);
        r = c.Move(S, 31300 * ms, 830);
        Check(r != null && r.Ms(ActDelayCore.kQueue) == 800 && r.Ms(ActDelayCore.kWake) == 250 && r.Ms(ActDelayCore.kTotal) == 1100 && r.TicksBeforeRun == 29, "5: queue behind another");

        // 6. a transition's route is the first visible movement, not the act; nested plans count once
        object h6 = new object(), tr = new object();
        c.Queued(h6, "T6", S, ActDelayCore.kHousehold, false, true, true, 40000 * ms, 900);
        c.Push(tr, h6, S, true, 40100 * ms, 901);
        c.PlanBegin(S, 40110 * ms, 901); c.PlanEnd(S, true, 40150 * ms);
        Check(c.Move(S, 40200 * ms, 902) == null, "6: transition route does not finish");
        c.Pop(tr, S, true, 41000 * ms, 930);
        c.Push(h6, h6, S, false, 41000 * ms, 930);
        c.PlanBegin(S, 41100 * ms, 931); c.PlanBegin(S, 41120 * ms, 931); c.PlanEnd(S, true, 41180 * ms); c.PlanEnd(S, true, 41300 * ms);
        r = c.Move(S, 41400 * ms, 940);
        Check(r != null && r.Plans == 1 && r.TrN == 1 && r.TrPlans == 1 && r.TrUs == 900 * ms, "6: counts");
        Check(r.Ms(ActDelayCore.kVisible) == 200 && r.Ms(ActDelayCore.kPreRun) == 900 && r.Ms(ActDelayCore.kPlanCpu) == 200 && r.Ms(ActDelayCore.kTotal) == 1400, "6: buckets");

        // 7. the sweep: a set-up that stood 30 s is finished; a record 30 min old is dropped
        object i7 = new object(), j7 = new object();
        c.Queued(i7, "T7", S, ActDelayCore.kNpc, true, true, true, 50000 * ms, 1000);
        c.Queued(j7, "T7", 43, ActDelayCore.kNpc, true, true, true, 50000 * ms, 1000);
        c.Push(i7, i7, S, false, 50010 * ms, 1000);
        c.Anim(S, 50020 * ms, 1000);
        Check(c.SweepNext(60000 * ms) == null, "7: too early");
        r = c.SweepNext(90000 * ms);
        Check(r != null && r.How == ActDelayCore.kHowAnim && r.Ms(ActDelayCore.kTotal) == 20, "7: sweep finishes the set-up");
        Check(c.SweepNext(90000 * ms) == null && c.OpenCount == 1, "7: pass over");
        Check(c.SweepNext(50000 * ms + ActDelayCore.kStaleUs + 1) == null && c.OpenCount == 0 && c.Stale == 1, "7: stale dropped");

        // report and minute line
        string rep = c.Report(null, 15);
        Check(rep.Contains("top interaction types") && rep.Contains("1. T") && rep.Contains("  all (n=") && rep.Contains("  npc (n="), "report: " + rep);
        string min = c.MinuteLine(null);
        Check(min != null && min.StartsWith("actdelay minute: ") && min.Contains(" over 500 ms, queued -> act p50 ") && min.Length <= 470, "minute line: " + min);
        Check(c.MinuteLine(null) == null, "minute line twice");
        c.Reset();
        Check(c.Acted == 0 && c.StatN(ActDelayCore.kKinds, ActDelayCore.kTotal) == 0, "reset");

        // table full: dropped, never thrown; world load frees everything
        ActDelayCore full = new ActDelayCore();
        for (int i = 0; i < ActDelayCore.kMaxOpen + 50; i++) full.Queued(new object(), "T", (ulong)(i % 2000), ActDelayCore.kNpc, true, false, false, 1 + i, 1);
        Check(full.OpenCount == ActDelayCore.kMaxOpen && full.Dropped == 50, "full: open " + full.OpenCount + " dropped " + full.Dropped);
        full.ClearOpen();
        Check(full.OpenCount == 0, "cleared");
        full.Queued(new object(), "T", 1, 99, true, true, true, 5, 1);
        Check(full.OpenCount == 1, "reusable after clear");

        // random streams: 40 Sims, legal and illegal event orders; every result's buckets add up, nothing negative
        Random rnd = new Random(684);
        ActDelayCore z = new ActDelayCore();
        const int sims = 40;
        object[] head = new object[sims], queued = new object[sims], trans = new object[sims];
        bool[] running = new bool[sims];
        long now = 1, ticks = 1;
        int results = 0, routed = 0, noact = 0;
        for (int step = 0; step < 400000; step++)
        {
            now += rnd.Next(0, 40000); if (rnd.Next(3) == 0) ticks += rnd.Next(0, 3);
            int s = rnd.Next(sims);
            ulong id = (ulong)(s + 1);
            ActDelayCore.Result q = null;
            switch (rnd.Next(12))
            {
                case 0:
                    if (head[s] == null) { head[s] = new object(); running[s] = false; z.Queued(head[s], "R" + rnd.Next(30), id, rnd.Next(3), rnd.Next(2) == 0, true, true, now, ticks); }
                    else if (queued[s] == null) { queued[s] = new object(); z.Queued(queued[s], "R" + rnd.Next(30), id, rnd.Next(3), rnd.Next(2) == 0, false, false, now, ticks); }
                    break;
                case 1: if (head[s] != null && !running[s] && rnd.Next(3) == 0) { trans[s] = new object(); z.Push(trans[s], head[s], id, true, now, ticks); } break;
                case 2: if (trans[s] != null) { z.Pop(trans[s], id, true, now, ticks); trans[s] = null; } break;
                case 3: if (head[s] != null && !running[s]) { running[s] = true; z.Push(head[s], head[s], id, false, now, ticks); } break;
                case 4: z.PlanBegin(id, now, ticks); break;
                case 5: z.PlanEnd(id, rnd.Next(4) != 0, now); break;
                case 6: z.JobWaited(id, rnd.Next(0, 500000)); z.Fgl(id, rnd.Next(0, 90000)); break;
                case 7: z.Anim(id, now, ticks); break;
                case 8: q = z.Move(id, now, ticks); break;
                case 9:
                    if (head[s] != null && running[s]) { q = z.Pop(head[s], id, false, now, ticks); Use(q, ref results, ref routed, ref noact); q = z.Removed(head[s], id, true, queued[s] == null, now, ticks); head[s] = queued[s]; queued[s] = null; running[s] = false; }
                    break;
                case 10: if (queued[s] != null && rnd.Next(4) == 0) { q = z.Removed(queued[s], id, false, false, now, ticks); queued[s] = null; } break;
                case 11: if (rnd.Next(2000) == 0) { while ((q = z.SweepNext(now)) != null) Use(q, ref results, ref routed, ref noact); } break;
            }
            Use(q, ref results, ref routed, ref noact);
        }
        Check(results > 5000 && routed > 500 && noact > 100, "random: " + results + " results, " + routed + " routed, " + noact + " without act");
        Check(z.Acted + z.NoAct == results, "random: acted " + z.Acted + " + noact " + z.NoAct + " != " + results);
        Check(z.StatN(ActDelayCore.kKinds, ActDelayCore.kTotal) == z.Acted, "random: histogram count");
        Check(z.OpenCount <= 2 * sims, "random: open " + z.OpenCount);
        Check(z.Report(null, 15).Split('\n').Length >= 20, "random: report");

        Console.WriteLine(sFails == 0 ? "actdelay self-test: PASS (" + sChecks + " checks, " + results + " random instances, " + routed + " with plans)" : "actdelay self-test: FAIL (" + sFails + ")");
        return sFails == 0 ? 0 : 1;
    }

    static void Use(ActDelayCore.Result q, ref int results, ref int routed, ref int noact)
    {
        if (q == null) return;
        results++;
        if (q.Routed) routed++;
        if (q.NoAct) noact++;
        long sum = 0;
        for (int i = 0; i < ActDelayCore.kBuckets; i++) { Check(q.Us[i] >= 0, "random: negative " + ActDelayCore.StatNames[i]); sum += q.Us[i]; }
        Check(sum == q.Us[ActDelayCore.kTotal], "random: buckets " + sum + " total " + q.Us[ActDelayCore.kTotal]);
        Check(q.Us[ActDelayCore.kVisible] >= 0 && q.Us[ActDelayCore.kVisible] <= q.Us[ActDelayCore.kTotal], "random: visible");
        Check(ActDelayCore.Line(q, "R", "Sim", "day 1").Length <= 470, "random: line length");
    }
}
