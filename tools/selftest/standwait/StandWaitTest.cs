// Usage: node tools/selftest/standwait/run.ts  (compiles this with mods/ts3fixes/src/StandWaitCore.cs)
// Offline self-check of StandWaitCore.cs (docs/686): the per-cycle decision (yield, push set, mutual order, pass
// allowed), the pass schedule, the end classification, and a small model of waiting Sims run cycle by cycle: with the
// rules every mutual wait ends within a few cycles, without them it runs to the timeout. Exit code 1 on failure.
using System;
using System.Collections.Generic;
using Ts3Fixes;

static class StandWaitTest
{
    static int sFails;
    static void Check(bool ok, string what) { if (!ok) { sFails++; Console.WriteLine("FAIL: " + what); } }

    static StandWaitCore.Blocker B(ulong id, int rank, bool waiting, bool namedMe)
    {
        StandWaitCore.Blocker b = new StandWaitCore.Blocker();
        b.Id = id; b.Rank = rank; b.Pushable = true; b.InScope = true; b.Waiting = waiting; b.NamedMe = namedMe;
        return b;
    }

    static void Decisions()
    {
        StandWaitCore.Blocker[] b = new StandWaitCore.Blocker[4];
        bool[] p = new bool[4];
        StandWaitCore.Decision d = StandWaitCore.Decide(true, false, 0, 1, b, 0, p);
        Check(d.Yield && !d.YieldTerminate, "push request: yield, route goes on");
        d = StandWaitCore.Decide(false, true, 0, 1, b, 0, p);
        Check(d.Yield && d.YieldTerminate, "cancel alone: yield and end the route");
        d = StandWaitCore.Decide(true, true, 0, 1, b, 0, p);
        Check(d.Yield && !d.YieldTerminate, "push and cancel: the push wins, as in the game");
        b[0] = B(2, 0, false, false);
        d = StandWaitCore.Decide(true, false, 0, 1, b, 1, p);
        Check(d.Yield && d.Pushes == 0 && !p[0], "a yielding Sim pushes nobody");

        d = StandWaitCore.Decide(false, false, 0, 1, b, 1, p);
        Check(!d.Yield && d.Pushes == 1 && p[0] && d.PassAllowed && d.Mutual == 0, "plain blocker: pushed, pass allowed");
        b[0].Pushable = false;
        d = StandWaitCore.Decide(false, false, 0, 1, b, 1, p);
        Check(d.Pushes == 0 && d.PassAllowed, "not pushable: no push, pass still allowed");
        b[0] = B(2, 0, false, false); b[0].InScope = false;
        Check(StandWaitCore.Decide(false, false, 0, 1, b, 1, p).Pushes == 0, "out of scope (household without the switch): no push");
        b[0] = B(2, 0, false, false); b[0].Partner = true; b[0].AtDest = true;
        d = StandWaitCore.Decide(false, false, 0, 1, b, 1, p);
        Check(d.Pushes == 0 && d.PassAllowed, "partner at the destination: not pushed, pass allowed");
        b[0] = B(2, 0, false, false); b[0].AtDest = true;
        d = StandWaitCore.Decide(false, false, 0, 1, b, 1, p);
        Check(d.Pushes == 1 && !d.PassAllowed, "stranger at the destination: pushed, no pass into it");
        b[0] = B(1, 9, true, true);
        d = StandWaitCore.Decide(false, false, 0, 1, b, 1, p);
        Check(d.Pushes == 0 && d.Mutual == 0 && d.PassAllowed, "the waiter itself in the list is ignored");

        // mutual pair, equal rank: the lower id pushes
        b[0] = B(2, 0, true, true);
        d = StandWaitCore.Decide(false, false, 0, 1, b, 1, p);
        Check(d.Mutual == 1 && d.Pushes == 1 && d.Withheld == 0, "mutual, I outrank (lower id): push");
        b[0] = B(1, 0, true, true);
        d = StandWaitCore.Decide(false, false, 0, 2, b, 1, p);
        Check(d.Mutual == 1 && d.Pushes == 0 && d.Withheld == 1, "mutual, outranked: withheld");
        // rank beats id
        b[0] = B(1, 0, true, true);
        d = StandWaitCore.Decide(false, false, 3, 2, b, 1, p);
        Check(d.Pushes == 1 && d.Withheld == 0, "mutual, higher rank with the higher id: push");
        // a waiting blocker that did not name me is not mutual: pushed whatever its rank
        b[0] = B(1, 9, true, false);
        d = StandWaitCore.Decide(false, false, 0, 2, b, 1, p);
        Check(d.Mutual == 0 && d.Pushes == 1, "blocker waits on someone else: pushed");
        // exactly one of any pair pushes
        Random rnd = new Random(7);
        for (int i = 0; i < 2000; i++)
        {
            int ra = rnd.Next(3), rb = rnd.Next(3);
            ulong ia = (ulong)rnd.Next(1, 50), ib = (ulong)rnd.Next(1, 50);
            if (ia == ib) continue;
            Check(StandWaitCore.Outranks(ra, ia, rb, ib) != StandWaitCore.Outranks(rb, ib, ra, ia), "Outranks is a strict order for " + ra + "/" + ia + " " + rb + "/" + ib);
        }
        Check(StandWaitCore.Rank(2, false) > StandWaitCore.Rank(1, true) && StandWaitCore.Rank(1, true) > StandWaitCore.Rank(1, false), "rank: priority, then selected");
    }

    static void PassSchedule()
    {
        int next = 2, delays = 0;
        Check(!StandWaitCore.PassDue(1, ref next, ref delays, false), "cycle 1: not due");
        Check(StandWaitCore.PassDue(2, ref next, ref delays, false), "cycle 2: due");
        StandWaitCore.PassFailed(2, ref next, 3);
        Check(!StandWaitCore.PassDue(3, ref next, ref delays, false) && !StandWaitCore.PassDue(4, ref next, ref delays, false) && StandWaitCore.PassDue(5, ref next, ref delays, false), "after a failed plan: 3 cycles later");
        next = 2; delays = 0;
        Check(!StandWaitCore.PassDue(2, ref next, ref delays, true) && next == 3 && delays == 1, "accepted push: one cycle of grace");
        Check(StandWaitCore.PassDue(3, ref next, ref delays, true), "grace is given once");
        next = 0; delays = 0;
        Check(!StandWaitCore.PassDue(40, ref next, ref delays, false), "pass cycle 0: never");
        next = 2; StandWaitCore.PassFailed(2, ref next, 0);
        Check(next == 3, "passEvery below 1 counts as 1");
    }

    static void Ends()
    {
        Check(StandWaitCore.EndKind(0, false, false, true, 0, 3f, 50f) == StandWaitCore.kEndNone, "no blocked cycle: none");
        Check(StandWaitCore.EndKind(3, true, false, false, 0, 9f, 50f) == StandWaitCore.kEndYield, "yield");
        Check(StandWaitCore.EndKind(3, false, true, true, 1, 9f, 50f) == StandWaitCore.kEndPass, "pass");
        Check(StandWaitCore.EndKind(3, false, false, true, 2, 9f, 50f) == StandWaitCore.kEndPush, "replan ok right after a push");
        Check(StandWaitCore.EndKind(9, false, false, true, 2, 30f, 50f) == StandWaitCore.kEndOther, "replan ok long after the last push: not ours");
        Check(StandWaitCore.EndKind(13, false, false, false, 0, 50.4f, 50f) == StandWaitCore.kEndTimeout, "ran to the timeout");
        Check(StandWaitCore.EndKind(13, false, false, false, 0, 46f, 50f) == StandWaitCore.kEndTimeout, "within a cycle of the timeout");
        Check(StandWaitCore.EndKind(5, false, false, false, 0, 20f, 50f) == StandWaitCore.kEndOther, "ended early without us");
        Check(StandWaitCore.TimeoutCyclesLeft(2, 7.6f, 50f) == 11, "2 cycles in 7.6 min: 11 left");
        Check(StandWaitCore.TimeoutCyclesLeft(13, 50f, 50f) == 0 && StandWaitCore.TimeoutCyclesLeft(0, 5f, 50f) == 0 && StandWaitCore.TimeoutCyclesLeft(3, 0f, 50f) == 0, "nothing left / no data");
        Check(StandWaitCore.TimeoutCyclesLeft(1, 0.1f, 50f) == 30, "capped at 30");
        Check(StandWaitCore.kEndNames.Length == 7, "a name per end kind");
        Check(StandWaitCore.EndKind(3, true, true, true, 1, 60f, 50f, true) == StandWaitCore.kEndGaveUp && StandWaitCore.EndKind(0, false, false, false, 0, 1f, 50f, true) == StandWaitCore.kEndNone, "gave up at a privacy lock: named first, not without a blocked cycle");
    }

    // Model: Sims stand in a spot too small for more than one. Each cycle a Sim's replan fails while another Sim stands
    // there. A pushed Sim that honours the push leaves the spot at its next cycle. Without the rules nobody pushes and
    // nobody obeys (the game's early return): every Sim waits maxCycles.
    class M { public ulong Id; public int Rank; public bool Here = true, PushReq, Done; public int Blocked, Waited; public List<ulong> Named = new List<ulong>(); }

    static int Model(int sims, bool rules, Random rnd, out int pushesInOneRound)
    {
        const int maxCycles = 13;
        List<M> all = new List<M>();
        for (int i = 0; i < sims; i++) { M m = new M(); m.Id = (ulong)(100 + i); m.Rank = rnd.Next(2); all.Add(m); }
        pushesInOneRound = 0;
        int longest = 0;
        StandWaitCore.Blocker[] b = new StandWaitCore.Blocker[8];
        bool[] p = new bool[8];
        for (int cycle = 1; cycle <= maxCycles; cycle++)
        {
            int roundPushes = 0;
            // a random order each round: the game runs the Sims' tasks in no order we control
            List<M> order = new List<M>(all);
            for (int i = order.Count - 1; i > 0; i--) { int j = rnd.Next(i + 1); M t = order[i]; order[i] = order[j]; order[j] = t; }
            foreach (M me in order)
            {
                if (me.Done) continue;
                List<M> others = all.FindAll(delegate(M o) { return o != me && o.Here && !o.Done; });
                if (!me.Here) { me.Done = true; continue; }  // stepped aside: out of the model
                if (others.Count == 0) { me.Done = true; me.Here = false; continue; }  // replan succeeds, walks on
                me.Blocked++; me.Waited = cycle;
                if (!rules) continue;
                int n = 0;
                foreach (M o in others)
                {
                    b[n] = B(o.Id, o.Rank, o.Blocked > 0, o.Named.Contains(me.Id));
                    b[n].Pushable = !o.PushReq;
                    n++;
                }
                StandWaitCore.Decision d = StandWaitCore.Decide(me.PushReq, false, me.Rank, me.Id, b, n, p);
                me.Named.Clear();
                foreach (M o in others) me.Named.Add(o.Id);
                if (d.Yield) { me.Here = false; me.PushReq = false; continue; }
                for (int i = 0; i < n; i++) if (p[i]) { others[i].PushReq = true; roundPushes++; }
            }
            if (roundPushes > pushesInOneRound) pushesInOneRound = roundPushes;
        }
        foreach (M m in all) if (m.Waited > longest) longest = m.Waited;
        return longest;
    }

    static void Models()
    {
        Random rnd = new Random(11);
        int pr;
        for (int sims = 2; sims <= 4; sims++)
        {
            Check(Model(sims, false, rnd, out pr) == 13, sims + " Sims, the game's rules: the wait runs to the timeout");
            int worst = 0;
            for (int i = 0; i < 500; i++) { int w = Model(sims, true, rnd, out pr); if (w > worst) worst = w; }
            Check(worst <= sims + 2, sims + " Sims, with the rules: everyone is through within " + (sims + 2) + " cycles (worst " + worst + ")");
        }
    }

    static int Main()
    {
        Decisions();
        PassSchedule();
        Ends();
        Models();
        Console.WriteLine(sFails == 0 ? "standwait core: all checks passed" : "standwait core: " + sFails + " check(s) FAILED");
        return sFails == 0 ? 0 : 1;
    }
}
