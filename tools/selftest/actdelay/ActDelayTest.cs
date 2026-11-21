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

        // hook health: the s218 / s219 shape (OnAddedToQueue never entered while interactions ran) must be named
        int[] n = new int[ActDelayCore.kHooks];
        Check(ActDelayCore.HookHealth(n) == "", "health: nothing ran yet");
        for (int i = 0; i < ActDelayCore.kHooks; i++) n[i] = 1000;
        n[ActDelayCore.kInJobWait] = 0; n[ActDelayCore.kInFgl] = 0;
        Check(ActDelayCore.HookHealth(n) == "", "health: all live (no job wait, no placement search is fine)");
        n[ActDelayCore.kInQueued] = 0;
        string dead = ActDelayCore.HookHealth(n);
        Check(dead.StartsWith("DEAD HOOK OnAddedToQueue 0 with PushRunningInteraction 1000") && !dead.Contains("OnRemovedFromQueue"), "health: dead queued hook: " + dead);
        n[ActDelayCore.kInQueued] = 1000; n[ActDelayCore.kInMove] = 0; n[ActDelayCore.kInSetActor] = 0;
        dead = ActDelayCore.HookHealth(n);
        Check(dead.Contains("OnAboutToStartFollowingRoute 0") && dead.Contains("SetActor 0"), "health: two dead: " + dead);
        n[ActDelayCore.kInMove] = 0; n[ActDelayCore.kInPlanEnd] = ActDelayCore.kHealthMin - 1; n[ActDelayCore.kInSetActor] = 5;
        Check(ActDelayCore.HookHealth(n) == "", "health: too few witnesses to judge");
        Check(ActDelayCore.HookLine(n).StartsWith("hooks entered: OnAddedToQueue 1000, OnRemovedFromQueue 1000, ") && ActDelayCore.HookLine(n).Length < 420, "health: line " + ActDelayCore.HookLine(n));
        // and a tracker that never sees Queued records nothing, whatever else arrives (why the runs read all zero)
        ActDelayCore nq = new ActDelayCore();
        object nk = new object();
        nq.Push(nk, nk, S, false, 10 * ms, 1, "K", 0); nq.PlanBegin(S, 20 * ms, 1); nq.PlanEnd(S, true, 30 * ms);
        Check(nq.Move(S, 40 * ms, 2) == null && nq.Pop(nk, S, false, 50 * ms, 3) == null && nq.Acted == 0 && nq.OpenCount == 0, "no Queued, no record");

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
        c.Push(a, a, S, false, 30 * ms, 101, "K", 0);
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
        c.Push(b2, b2, S, false, 8010 * ms, 400, "K", 0);
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
        c.Push(c3, c3, S, false, 9000 * ms, 500, "K", 0);
        c.Anim(S, 9100 * ms, 501);
        c.PlanBegin(S, 9900 * ms, 521); c.PlanEnd(S, true, 9950 * ms);
        r = c.Move(S, 9960 * ms, 522);
        Check(r != null && r.How == ActDelayCore.kHowAnim && !r.Routed && r.Ms(ActDelayCore.kTotal) == 100 && r.Ms(ActDelayCore.kPreRoute) == 100, "3: animation first");
        Check(Sum(r) == r.Us[ActDelayCore.kTotal], "3: sum");

        // 4. route failure, nothing followed: ended without acting; logged because it planned; out of the percentiles
        int acted = c.Acted;
        object d4 = new object();
        c.Queued(d4, "T4", S, ActDelayCore.kNpc, true, true, true, 20000 * ms, 600);
        c.Push(d4, d4, S, false, 20000 * ms, 600, "K", 0);
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
        c.Push(e5, e5, S, false, 30300 * ms, 801, "K", 0);
        Check(c.Removed(f5, S, false, false, 30400 * ms, 802) == null && c.Cancelled == 1, "5: cancelled");
        r = c.Move(S, 30500 * ms, 803);
        Check(r != null && r.Ms(ActDelayCore.kWake) == 300 && r.Ms(ActDelayCore.kTotal) == 500, "5: first");
        c.Pop(e5, S, false, 31000 * ms, 820);
        c.Removed(e5, S, true, false, 31000 * ms, 820);
        c.Push(g5, g5, S, false, 31250 * ms, 829, "K", 0);
        r = c.Move(S, 31300 * ms, 830);
        Check(r != null && r.Ms(ActDelayCore.kQueue) == 800 && r.Ms(ActDelayCore.kWake) == 250 && r.Ms(ActDelayCore.kTotal) == 1100 && r.TicksBeforeRun == 29, "5: queue behind another");

        // 6. a transition's route is the first visible movement, not the act; nested plans count once
        object h6 = new object(), tr = new object();
        c.Queued(h6, "T6", S, ActDelayCore.kHousehold, false, true, true, 40000 * ms, 900);
        c.Push(tr, h6, S, true, 40100 * ms, 901, "K", 0);
        c.PlanBegin(S, 40110 * ms, 901); c.PlanEnd(S, true, 40150 * ms);
        Check(c.Move(S, 40200 * ms, 902) == null, "6: transition route does not finish");
        c.Pop(tr, S, true, 41000 * ms, 930);
        c.Push(h6, h6, S, false, 41000 * ms, 930, "K", 0);
        c.PlanBegin(S, 41100 * ms, 931); c.PlanBegin(S, 41120 * ms, 931); c.PlanEnd(S, true, 41180 * ms); c.PlanEnd(S, true, 41300 * ms);
        r = c.Move(S, 41400 * ms, 940);
        Check(r != null && r.Plans == 1 && r.TrN == 1 && r.TrPlans == 1 && r.TrUs == 900 * ms, "6: counts");
        Check(r.Ms(ActDelayCore.kVisible) == 200 && r.Ms(ActDelayCore.kPreRun) == 900 && r.Ms(ActDelayCore.kPlanCpu) == 200 && r.Ms(ActDelayCore.kTotal) == 1400, "6: buckets");

        // 7. the sweep: a set-up that stood 30 s is finished; a record 30 min old is dropped
        object i7 = new object(), j7 = new object();
        c.Queued(i7, "T7", S, ActDelayCore.kNpc, true, true, true, 50000 * ms, 1000);
        c.Queued(j7, "T7", 43, ActDelayCore.kNpc, true, true, true, 50000 * ms, 1000);
        c.Push(i7, i7, S, false, 50010 * ms, 1000, "K", 0);
        c.Anim(S, 50020 * ms, 1000);
        Check(c.SweepNext(60000 * ms) == null, "7: too early");
        r = c.SweepNext(90000 * ms);
        Check(r != null && r.How == ActDelayCore.kHowAnim && r.Ms(ActDelayCore.kTotal) == 20, "7: sweep finishes the set-up");
        Check(c.SweepNext(90000 * ms) == null && c.OpenCount == 1, "7: pass over");
        Check(c.SweepNext(50000 * ms + ActDelayCore.kStaleUs + 1) == null && c.OpenCount == 0 && c.Stale == 1, "7: stale dropped");

        // 8. outfit wait: the Sim's task sat 1.2 s in SwitchOutfitHelper.Wait inside Run before the spin; an overlay, the
        // buckets still add up; a wait under a millisecond (outfit ready) is not counted; a wait with no record still counts
        object k8 = new object();
        c.Queued(k8, "T8", S, ActDelayCore.kHousehold, false, true, true, 100000 * ms, 2000);
        c.Push(k8, k8, S, false, 100010 * ms, 2000, "K", 0);
        c.OutfitWait(S, ActDelayCore.kHousehold, 1200 * ms);
        c.OutfitWait(S, ActDelayCore.kHousehold, 900);
        c.OutfitWait(777, ActDelayCore.kNpc, 300 * ms);
        c.OutfitWait(778, 99, 50 * ms);
        c.Anim(S, 101210 * ms, 2400);
        r = c.Pop(k8, S, false, 103000 * ms, 2500);
        Check(r != null && r.OutfitN == 1 && r.OutfitUs == 1200 * ms && r.Ms(ActDelayCore.kPreRoute) == 1200 && Sum(r) == r.Us[ActDelayCore.kTotal], "8: outfit overlay");
        Check(c.OutfitWaits == 3 && c.OutfitPlayer == 2 && c.OutfitMaxMs == 1200 && c.OutfitSumMs == 1550, "8: totals " + c.OutfitWaits + " " + c.OutfitPlayer + " " + c.OutfitSumMs);
        Check(c.StatN(ActDelayCore.kHousehold, ActDelayCore.kOutfit) == 2 && c.StatN(ActDelayCore.kNpc, ActDelayCore.kOutfit) == 1 && c.StatMax(ActDelayCore.kKinds, ActDelayCore.kOutfit) == 1200, "8: histogram");
        line = ActDelayCore.Line(r, "T8", "A Sim", "day 1 hour 8.00", null);
        Check(line.Contains(", outfit waits 1 (1200 ms)") && line.EndsWith(", day 1 hour 8.00, Sim A Sim"), "8: line: " + line);

        // 9. sync: the receiver waits 900 ms for the starter (ReceiveSocialInteraction with a nested wait), then plans inside the
        // same call (plan time is not sync time), waits 100 ms more and moves while the call is still open
        object l9 = new object();
        c.Queued(l9, "T9", S, ActDelayCore.kHousehold, true, true, true, 200000 * ms, 3000);
        c.Push(l9, l9, S, false, 200000 * ms, 3000, "K", 0);
        c.SyncBegin(S, 200010 * ms);
        c.SyncBegin(S, 200500 * ms); c.SyncEnd(S, 200700 * ms);
        c.PlanBegin(S, 200910 * ms, 3300); c.PlanEnd(S, true, 201000 * ms);
        r = c.Move(S, 201100 * ms, 3310);
        Check(r != null && r.SyncN == 1 && r.SyncUs == 1000 * ms && r.Ms(ActDelayCore.kPreRoute) == 910 && r.Ms(ActDelayCore.kPostPlan) == 100, "9: sync " + (r == null ? -1 : r.SyncUs));
        Check(Sum(r) == r.Us[ActDelayCore.kTotal] && r.SyncUs <= r.Us[ActDelayCore.kTotal], "9: sum");
        Check(ActDelayCore.Line(r, "T9", null, null, null).Contains(", sync waits 1 (1000 ms)"), "9: line");
        c.SyncEnd(S, 201200 * ms);   // the call returns after the record is gone: nothing to do, nothing thrown
        Check(c.SyncWaits == 1 && c.SyncSumMs == 1000 && c.StatN(ActDelayCore.kHousehold, ActDelayCore.kSync) == 1, "9: totals");
        // a wait that closes inside the record
        object m9 = new object();
        c.Queued(m9, "T9", S, ActDelayCore.kNpc, true, true, true, 210000 * ms, 3400);
        c.Push(m9, m9, S, false, 210000 * ms, 3400, "K", 0);
        c.SyncBegin(S, 210100 * ms); c.SyncEnd(S, 210400 * ms);
        c.SyncEnd(S, 210500 * ms);   // unbalanced end: ignored
        c.Anim(S, 210450 * ms, 3500);
        r = c.Pop(m9, S, false, 212000 * ms, 3600);
        Check(r != null && r.SyncN == 1 && r.SyncUs == 300 * ms && r.How == ActDelayCore.kHowAnim, "9: closed wait");

        // 10. Run begins with the Sim in a vehicle (GoToSchool behind the carpool) or inside a rabbit hole: finished there,
        // no preroute; a transition in a vehicle does not finish it
        object n10 = new object(), tr10 = new object();
        c.Queued(n10, "T10", S, ActDelayCore.kSelected, false, true, true, 300000 * ms, 4000);
        Check(c.Push(tr10, n10, S, true, 300020 * ms, 4001, "Tr", ActDelayCore.kAwayRide) == null, "10: a transition never ends the record");
        c.Pop(tr10, S, true, 300050 * ms, 4002);
        r = c.Push(n10, n10, S, false, 300050 * ms, 4002, "K", ActDelayCore.kAwayRide);
        Check(r != null && r.How == ActDelayCore.kHowRide && !r.NoAct && r.Ms(ActDelayCore.kTotal) == 50 && r.Ms(ActDelayCore.kPreRoute) == 0 && r.Ms(ActDelayCore.kPreRun) == 30, "10: ride");
        Check(ActDelayCore.Line(r, "T10", null, null, null).StartsWith("actdelay: 50 ms queued -> ride, selected user T10: "), "10: line");
        Check(c.Pop(n10, S, false, 305000 * ms, 4500) == null && c.Removed(n10, S, true, true, 305000 * ms, 4500) == null, "10: nothing left to finish");
        object o10 = new object();
        c.Queued(o10, "T10", S, ActDelayCore.kNpc, true, true, true, 310000 * ms, 4600);
        r = c.Push(o10, o10, S, false, 310040 * ms, 4601, "K", ActDelayCore.kAwayHidden);
        Check(r != null && r.How == ActDelayCore.kHowHidden && r.Ms(ActDelayCore.kTotal) == 40 && c.Rides == 1 && c.Hidden == 1, "10: hidden");

        // 11. names: the last transition's class and the first interaction run inside Run, without namespaces
        object p11 = new object(), tr11 = new object(), in11 = new object();
        c.Queued(p11, "T11", S, ActDelayCore.kHousehold, true, true, true, 400000 * ms, 5000);
        c.Push(tr11, p11, S, true, 400010 * ms, 5000, "Sims3.Gameplay.Objects.Seating.Sit+StandUp", 0);
        c.Pop(tr11, S, true, 400480 * ms, 5140);
        c.Push(p11, p11, S, false, 400480 * ms, 5140, "K", 0);
        c.Push(in11, p11, S, false, 400490 * ms, 5140, "Objects.Seating.SitTransitionAngledToStraight", 0);
        c.Push(new object(), p11, S, false, 400500 * ms, 5140, "Other", 0);
        c.Anim(S, 400800 * ms, 5240);
        r = c.Pop(p11, S, false, 402000 * ms, 5600);
        Check(r != null && r.TrN == 1 && r.NestN == 2 && (string)r.TrType == "Sims3.Gameplay.Objects.Seating.Sit+StandUp", "11: types");
        line = ActDelayCore.Line(r, "T11", "A Sim", "day 1", null);
        Check(line.Contains("transitions 1 (470 ms, 0 plans, last Sit+StandUp), ran inside 2 (first SitTransitionAngledToStraight)"), "11: line: " + line);
        // a line with everything in it stays under the native log buffer; the name is dropped first
        string longName = new string('n', 300);
        Check(ActDelayCore.Line(r, longName.Substring(0, 60), longName, "day 1 hour 8.00", null).Length <= ActDelayCore.kLineMax, "11: long line");

        // report and minute line
        string rep = c.Report(null, 15);
        Check(rep.Contains("top interaction types") && rep.Contains("1. T") && rep.Contains("  all (n=") && rep.Contains("  npc (n="), "report: " + rep);
        Check(rep.Contains("not idle at Run start (kept out of preroute): riding 1, in a rabbit hole 1; outfit waits that slept: 3 (household or selected 2), 1550 ms in all, longest 1200; instances with a sync wait: 2, 1300 ms in all"), "report overlays: " + rep);
        Check(rep.Contains(" outfit 1216/1216/1200 (n=2)") || rep.Contains(" outfit "), "report outfit column");
        Check(rep.Contains("; of that outfit "), "report: per-type overlay");
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
                case 1: if (head[s] != null && !running[s] && rnd.Next(3) == 0) { trans[s] = new object(); z.Push(trans[s], head[s], id, true, now, ticks, "K", 0); } break;
                case 2: if (trans[s] != null) { z.Pop(trans[s], id, true, now, ticks); trans[s] = null; } break;
                case 3: if (head[s] != null && !running[s]) { running[s] = true; q = z.Push(head[s], head[s], id, false, now, ticks, "K", rnd.Next(12) == 0 ? 1 + rnd.Next(2) : 0); } break;
                case 4: z.PlanBegin(id, now, ticks); break;
                case 5: z.PlanEnd(id, rnd.Next(4) != 0, now); break;
                case 6:
                    z.JobWaited(id, rnd.Next(0, 500000)); z.Fgl(id, rnd.Next(0, 90000));
                    switch (rnd.Next(4))
                    {
                        case 0: z.OutfitWait(id, rnd.Next(4), rnd.Next(0, 3000000)); break;
                        case 1: z.SyncBegin(id, now); break;
                        case 2: z.SyncEnd(id, now); break;
                        default: if (head[s] != null && running[s]) z.Push(new object(), head[s], id, false, now, ticks, "N", 0); break;
                    }
                    break;
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
        Check(q.SyncUs >= 0 && q.SyncUs <= q.Us[ActDelayCore.kTotal], "random: sync " + q.SyncUs + " over total " + q.Us[ActDelayCore.kTotal]);
        Check(q.OutfitUs >= 0 && q.OutfitN >= 0, "random: outfit");
        if (q.How == ActDelayCore.kHowRide || q.How == ActDelayCore.kHowHidden) Check(q.Us[ActDelayCore.kPreRoute] == 0 && !q.Routed, "random: ride with a preroute");
        Check(ActDelayCore.Line(q, "R", "Sim", "day 1").Length <= ActDelayCore.kLineMax, "random: line length");
    }
}
