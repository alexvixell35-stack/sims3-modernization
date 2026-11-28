using System;
using System.Collections.Generic;
using Ts3Fixes;

// Offline self-check of mods/ts3fixes/src/AutonomyFailCooldownCore.cs: AfcRow against a list model of failure times, plus
// the failure and offer decisions.
static class AutonomyFailCooldownTest
{
    static int sFail;
    static void Check(bool ok, string what) { if (!ok) { sFail++; Console.WriteLine("FAIL " + what); } }

    static int Main()
    {
        const long W = 60, L = 120;

        // fixed cases
        AfcRow r = new AfcRow();
        Check(!r.Cooling(0, L), "new row is not cooling");
        Check(!r.Fail(0, W, L), "one failure starts nothing");
        Check(r.Fail(60, W, L), "second failure within the window (60 after the first) starts the cooldown");
        Check(r.Cooling(61, L) && r.Cooling(179, L) && !r.Cooling(180, L), "cooldown lasts len");
        Check(!r.Cooling(30, L), "clock went back: a cooldown ending more than len ahead does not refuse");
        Check(!r.Cooling(59, L), "clock went back 1 tick before the start: ends 121 ahead, not refused");
        Check(!r.Fail(200, W, L), "after a cooldown the count starts again");
        r.Ok();
        Check(!r.Cooling(70, L) && !r.Fail(210, W, L), "a good run clears the cooldown and the streak");
        r = new AfcRow();
        r.Fail(0, W, L);
        Check(!r.Fail(61, W, L), "first failure fell out of the window: no cooldown");
        Check(r.Fail(100, W, L), "61 and 100 are inside one window");

        // decisions
        Check(AfcRow.CountsAsRouteFailure(true, false, false, true, false), "autonomous, RouteFailed: counts");
        Check(AfcRow.CountsAsRouteFailure(true, false, false, false, true), "autonomous, last plan failed: counts");
        Check(!AfcRow.CountsAsRouteFailure(false, false, false, true, true), "player-queued never counts");
        Check(!AfcRow.CountsAsRouteFailure(true, true, false, true, true), "cancelled never counts");
        Check(!AfcRow.CountsAsRouteFailure(true, false, true, true, true), "a cancel reason (user, higher priority) never counts");
        Check(!AfcRow.CountsAsRouteFailure(true, false, false, false, false), "no failed route: not counted");
        Check(!AfcRow.CountsAsRouteFailure(true, false, false, true, true, true), "a locked door (privacy lock) in the last minutes: not counted");
        Check(AfcRow.CountsAsRouteFailure(true, false, false, true, false, false), "no recent lock: counts as before");
        Check(AfcRow.AutonomousOffer(true, false), "autonomy search: may refuse");
        Check(!AfcRow.AutonomousOffer(false, false) && !AfcRow.AutonomousOffer(true, true) && !AfcRow.AutonomousOffer(false, true),
            "pie menu (UserDirected) or a non-autonomous call: never refused");

        // which row a dequeued head counts on (continuations count on their autonomous pusher)
        Check(AfcRow.Row(true, true, false, true, false) == AfcRow.kWait, "MopBurntTiles succeeded with MopBurnt queued behind: wait, nothing cleared");
        Check(AfcRow.Row(false, true, true, false, true) == AfcRow.kPusher, "MopBurnt (Terrain) failed: counts on its pusher MopBurntTiles");
        Check(AfcRow.Row(true, true, true, false, true) == AfcRow.kPusher, "MopBurnt succeeded: clears its pusher's row");
        Check(AfcRow.Row(false, false, true, false, false) == AfcRow.kPusher, "a continuation without the Autonomous flag still counts on its autonomous pusher");
        Check(AfcRow.Row(true, true, true, true, false) == AfcRow.kWait, "a continuation that pushed another one: wait for the last");
        Check(AfcRow.Row(false, true, false, false, true) == AfcRow.kNone, "a Terrain target with no recorded pusher: not keyed");
        Check(AfcRow.Row(false, false, false, false, false) == AfcRow.kNone, "player-queued, no autonomous pusher: never keyed");
        Check(AfcRow.Row(false, true, false, true, false) == AfcRow.kOwn, "a failure never waits, even with a continuation queued");
        Check(AfcRow.Row(true, true, false, false, false) == AfcRow.kOwn, "a plain success clears its own row");
        // script pushes (Hidden): SwimAroundCallback's DoggySwimAround failures record nothing; successes still clear
        Check(!AfcRow.OwnRow(true, true, false), "a Hidden autonomous head that failed: no row (no scored offer to refuse)");
        Check(AfcRow.OwnRow(true, true, true), "a Hidden autonomous head that succeeded: still clears its row");
        Check(AfcRow.OwnRow(true, false, false) && AfcRow.OwnRow(true, false, true), "an autonomy pick (not Hidden): as before");
        Check(!AfcRow.OwnRow(false, false, false) && !AfcRow.OwnRow(false, true, true), "player-queued: never its own row");
        Check(AfcRow.Row(false, AfcRow.OwnRow(true, true, false), false, false, false) == AfcRow.kNone, "Hidden failure: kNone");
        Check(AfcRow.Row(false, AfcRow.OwnRow(false, true, false), true, false, false) == AfcRow.kPusher, "a Hidden continuation still counts on its autonomous pusher");
        // LookOutWindow: a run that reached the inside paws spot after EA rejected the outside one ends succeeded: clears, never counts
        Check(AfcRow.Row(true, AfcRow.OwnRow(true, false, true), false, false, false) == AfcRow.kOwn, "LookOutWindow succeeded after a failed side plan: clears");
        Check(!AfcRow.CountsAsRouteFailure(true, false, true, true, true), "LookOutWindow cancelled by HigherPriorityNext after a failed plan: not counted");
        // a MopBurntTiles -> MopBurnt chain: two failed MopBurnts within the window cool the pusher's row down
        r = new AfcRow();
        Check(!r.Fail(0, W, L) && r.Fail(40, W, L) && r.Cooling(41, L), "two failed continuations on one pusher row start the cooldown");

        // all targets (AfcDefRow): Roxy's LookOutWindow, 2 failures per window, the third window starts it
        AfcDefRow d = new AfcDefRow();
        Check(!d.Fail(1, 0, W, L) && !d.Fail(1, 10, W, L) && !d.Fail(2, 20, W, L) && !d.Fail(2, 30, W, L), "two windows, 2 failures each: nothing");
        Check(d.Fail(3, 40, W, L) && d.Cooling(41, L) && d.Cooling(159, L) && !d.Cooling(160, L), "third distinct window within W: all targets for len");
        Check(!d.Fail(4, 50, W, L) && !d.Fail(5, 51, W, L) && d.Fail(6, 52, W, L), "after it starts, counting starts over");
        d = new AfcDefRow();
        Check(!d.Fail(7, 0, W, L) && !d.Fail(7, 1, W, L) && !d.Fail(7, 2, W, L) && !d.Fail(7, 3, W, L), "one target failing again and again is the per-target row's case");
        d = new AfcDefRow();
        Check(!d.Fail(1, 0, W, L) && !d.Fail(2, 10, W, L) && !d.Fail(3, W + 5, W, L), "the first target fell out of the window");
        Check(d.Fail(4, W + 6, W, L), "then two in the window plus a new one");
        d = new AfcDefRow();
        Check(!d.Fail(1, 0, W, L) && !d.Fail(2, 1, W, L), "two targets");
        d.Ok();
        Check(!d.Fail(3, 2, W, L) && !d.Cooling(3, L), "a success forgets the failed targets");
        d = new AfcDefRow();
        Check(!d.Fail(1, 100, W, L) && !d.Fail(2, 50, W, L) && !d.Fail(3, 51, W, L), "clock went back: a failure from the future is dropped");
        d = new AfcDefRow();
        Check(!d.Fail(1, 0, W, L) && !d.Fail(2, 30, W, L) && !d.Fail(1, 50, W, L) && d.Fail(3, 70, W, L),
            "a target failing again keeps its newest time (target 1 at 0 alone would be out of the window at 70)");

        // random model for AfcDefRow: per target its last failure; starts when kDistinct targets are within W, then empties
        Random rd = new Random(4354);
        for (int round = 0; round < 2000; round++)
        {
            AfcDefRow row = new AfcDefRow();
            Dictionary<ulong, long> last = new Dictionary<ulong, long>();
            long until = 0, now = 0;
            for (int i = 0; i < 60; i++)
            {
                now += rd.Next(0, 40);
                int op = rd.Next(10);
                if (op < 8)
                {
                    ulong t = (ulong)rd.Next(1, 6);
                    List<ulong> drop = new List<ulong>();
                    foreach (KeyValuePair<ulong, long> kv in last) if (now - kv.Value > W) drop.Add(kv.Key);
                    foreach (ulong k in drop) last.Remove(k);
                    last[t] = now;
                    bool start = last.Count >= AfcDefRow.kDistinct;
                    if (start) { until = now + L; last.Clear(); }
                    Check(row.Fail(t, now, W, L) == start, "random all-targets fail " + round + "/" + i);
                }
                else if (op < 9) { row.Ok(); last.Clear(); until = 0; }
                Check(row.Cooling(now, L) == (until != 0 && now < until && until - now <= L), "random all-targets cooling " + round + "/" + i);
            }
        }

        // random model: failures kept as a list, a cooldown starts when the last two are within W
        Random rnd = new Random(4343);
        for (int round = 0; round < 2000; round++)
        {
            AfcRow row = new AfcRow();
            List<long> fails = new List<long>();
            long until = 0, now = 0;
            for (int i = 0; i < 60; i++)
            {
                now += rnd.Next(0, 70);
                int op = rnd.Next(10);
                if (op < 7)
                {
                    fails.RemoveAll(t => now - t > W);
                    bool start = fails.Count == 1;
                    if (start) { until = now + L; fails.Clear(); } else fails.Add(now);
                    Check(row.Fail(now, W, L) == start, "random fail " + round + "/" + i);
                }
                else if (op < 8) { row.Ok(); fails.Clear(); until = 0; }
                bool cool = until != 0 && now < until && until - now <= L;
                Check(row.Cooling(now, L) == cool, "random cooling " + round + "/" + i);
            }
        }
        Console.WriteLine(sFail == 0 ? "autonomyfailcooldown self-test: OK" : "autonomyfailcooldown self-test: " + sFail + " FAILED");
        return sFail == 0 ? 0 : 1;
    }
}
