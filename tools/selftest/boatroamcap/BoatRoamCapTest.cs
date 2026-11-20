using System;
using Ts3Fixes;

// Offline self-check of BoatRoamCapCore (docs/853 §4).
static class BoatRoamCapTest
{
    static int sFails;
    static void Check(bool ok, string what) { if (!ok) { sFails++; if (sFails < 20) Console.WriteLine("FAIL " + what); } }

    const long H = 100;  // ticks per sim-hour in this test

    static BoatRoamCapCore.Tun Tun()
    {
        BoatRoamCapCore.Tun t = new BoatRoamCapCore.Tun();
        t.MaxRoams = 3; t.CapTicks = 2 * H; t.GapTicks = 4 * H; t.BackoffTicks = 1 * H;
        t.NightFrom = 23; t.NightTo = 6; t.MotiveLow = -50f;
        return t;
    }

    static void Main()
    {
        BoatRoamCapCore.Tun t = Tun();
        BoatRoamCapCore.Entry e;

        // night window, wrapping midnight
        Check(BoatRoamCapCore.Night(23, 23, 6) && BoatRoamCapCore.Night(0, 23, 6) && BoatRoamCapCore.Night(5, 23, 6), "23..6: 23, 0, 5 are night");
        Check(!BoatRoamCapCore.Night(6, 23, 6) && !BoatRoamCapCore.Night(22, 23, 6) && !BoatRoamCapCore.Night(12, 23, 6), "23..6: 6, 22, 12 are day");
        Check(BoatRoamCapCore.Night(2, 1, 4) && !BoatRoamCapCore.Night(4, 1, 4), "1..4 without wrap");
        Check(!BoatRoamCapCore.Night(3, 5, 5), "from == to: no night window");

        // pure rule
        Check(BoatRoamCapCore.Rule(0, 0, 12, 0, 0, t) == BoatRoamCapCore.kNone, "first roam at noon, fine motives: no cap");
        Check(BoatRoamCapCore.Rule(2, H, 12, 0, 0, t) == BoatRoamCapCore.kNone, "2 roams, 1 h: no cap");
        Check(BoatRoamCapCore.Rule(3, H, 12, 0, 0, t) == BoatRoamCapCore.kStreak, "3 roams done: streak cap");
        Check(BoatRoamCapCore.Rule(1, 2 * H, 12, 0, 0, t) == BoatRoamCapCore.kStreak, "2 sim-hours: streak cap");
        Check(BoatRoamCapCore.Rule(0, 0, 23, 0, 0, t) == BoatRoamCapCore.kNight, "11 PM: night cap");
        Check(BoatRoamCapCore.Rule(0, 0, 3, 0, 0, t) == BoatRoamCapCore.kNight, "3 AM: night cap");
        Check(BoatRoamCapCore.Rule(0, 0, 12, -60f, 0, t) == BoatRoamCapCore.kMotive, "low energy: motive cap");
        Check(BoatRoamCapCore.Rule(0, 0, 12, 0, -51f, t) == BoatRoamCapCore.kMotive, "low bladder: motive cap");
        Check(BoatRoamCapCore.Rule(0, 0, 12, -50f, -50f, t) == BoatRoamCapCore.kNone, "motive at the threshold: no cap");
        BoatRoamCapCore.Tun off = t; off.MaxRoams = 0; off.CapTicks = 0;
        Check(BoatRoamCapCore.Rule(99, 99 * H, 12, 0, 0, off) == BoatRoamCapCore.kNone, "streak limits 0: off");

        // streak: Joel-like every ~40 min at noon -> 3 roams, then capped
        BoatRoamCapCore m = new BoatRoamCapCore();
        long now = 1000;
        Check(m.Check(1, 7, now, 12, 0, 0, t, out e) == BoatRoamCapCore.kRoam && e.Roams == 1, "run 1 roams");
        Check(m.Check(1, 7, now + 40, 12, 0, 0, t, out e) == BoatRoamCapCore.kRoam && e.Roams == 2, "run 2 roams");
        Check(m.Check(1, 7, now + 80, 13, 0, 0, t, out e) == BoatRoamCapCore.kRoam && e.Roams == 3, "run 3 roams");
        Check(m.Check(1, 7, now + 120, 13, 0, 0, t, out e) == BoatRoamCapCore.kCap && e.Reason == BoatRoamCapCore.kStreak && e.Roams == 3, "run 4 capped, roams stay 3");
        m.Pushed(1, now + 120);
        Check(m.Returned(1, true, now + 130, t.BackoffTicks) && !m.TryGet(1, out e), "return reached shore: streak ends");
        Check(!m.Returned(1, true, now + 131, t.BackoffTicks), "a return we did not push: not ours");
        Check(m.Check(1, 7, now + 140, 13, 0, 0, t, out e) == BoatRoamCapCore.kRoam && e.Roams == 1, "boating again later: a new streak");

        // pushed return failed -> back-off (EA roams), then capped again
        m.Clear();
        now = 5000;
        m.Check(2, 7, now, 23, 0, 0, t, out e);
        Check(e.Reason == BoatRoamCapCore.kNight, "night: capped on the first run");
        m.Pushed(2, now);
        Check(m.Returned(2, false, now + 1, t.BackoffTicks), "return failed: ours");
        Check(m.Check(2, 7, now + 10, 23, 0, 0, t, out e) == BoatRoamCapCore.kBackoff && e.Roams == 1, "in back-off: EA roams");
        Check(m.Check(2, 7, now + 50, 0, 0, 0, t, out e) == BoatRoamCapCore.kBackoff && e.Roams == 2, "still in back-off");
        Check(m.Check(2, 7, now + 1 + H, 0, 0, 0, t, out e) == BoatRoamCapCore.kCap, "back-off over: capped again");

        // pushed return never ran: the next BoatAround finds it pending
        m.Pushed(2, now + 1 + H);
        Check(m.Check(2, 7, now + 2 + H, 0, 0, 0, t, out e) == BoatRoamCapCore.kReturnLost && e.Pending == 0 && e.BackoffUntil == now + 2 + 2 * H, "pending return lost: back-off starts, EA roams");
        Check(!m.Returned(2, false, now + 3 + H, t.BackoffTicks), "a late return result after the loss: not ours any more");

        // push could not be made
        m.Clear();
        m.Check(3, 7, 100, 3, 0, 0, t, out e);
        m.Failed(3, 100, t.BackoffTicks);
        Check(m.Check(3, 7, 120, 3, 0, 0, t, out e) == BoatRoamCapCore.kBackoff, "push failed: back-off");

        // resets: another boat, a long gap, the clock going back
        m.Clear();
        m.Check(4, 7, 100, 12, 0, 0, t, out e); m.Check(4, 7, 140, 12, 0, 0, t, out e);
        Check(m.Check(4, 8, 150, 12, 0, 0, t, out e) == BoatRoamCapCore.kRoam && e.Roams == 1 && e.Start == 150, "another boat: new streak");
        Check(m.Check(4, 8, 150 + 4 * H + 1, 12, 0, 0, t, out e) == BoatRoamCapCore.kRoam && e.Roams == 1, "gap over 4 h: new streak");
        Check(m.Check(4, 8, 50, 12, 0, 0, t, out e) == BoatRoamCapCore.kRoam && e.Roams == 1 && e.Start == 50, "clock went back: new streak");
        Check(m.Check(4, 8, 50 + 2 * H, 12, 0, 0, t, out e) == BoatRoamCapCore.kCap && e.Reason == BoatRoamCapCore.kStreak, "2 sim-hours of streak: capped");
        Check(BoatRoamCapCore.Why(BoatRoamCapCore.kMotive) == "low motive" && BoatRoamCapCore.Why(9) == "none", "Why names");

        // bounded table
        m.Clear();
        for (ulong i = 0; i < (ulong)BoatRoamCapCore.kMax; i++) m.Check(i, 1, i < 10 ? 0 : 5000, 12, 0, 0, t, out e);
        Check(m.Count == BoatRoamCapCore.kMax, "full table");
        m.Check(1000, 1, 5000, 12, 0, 0, t, out e);
        Check(m.Count == BoatRoamCapCore.kMax - 10 + 1, "full: stale streaks dropped first");
        for (ulong i = 2000; m.Count < BoatRoamCapCore.kMax; i++) m.Check(i, 1, 5000, 12, 0, 0, t, out e);
        m.Check(3000, 1, 5000, 12, 0, 0, t, out e);
        Check(m.Count == 1 && m.TryGet(3000, out e), "full of live streaks: cleared, new one kept");
        m.Check(3000, 1, 5001, 12, 0, 0, t, out e);
        Check(m.Count == 1 && e.Roams == 2, "an existing Sim never triggers the bound");

        Console.WriteLine(sFails == 0 ? "boatroamcap selftest: ok" : "boatroamcap selftest: " + sFails + " FAIL");
        Environment.Exit(sFails == 0 ? 0 : 1);
    }
}
