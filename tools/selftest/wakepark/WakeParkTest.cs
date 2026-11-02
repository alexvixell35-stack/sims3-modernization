// Usage: node tools/selftest/wakepark/run.ts  (compiles this with mods/ts3fixes/src/WakeParkCore.cs)
// Offline self-check of the docs/211 wake-up switches: the parked exit-reason poll returns the same (result, tick) as the
// game's one-tick poll for every deadline and reason schedule when AddExitReason wakes it, and no later than the game when
// the wake is missed (only up to k ticks late on the reason, never past the deadline); the park length; the verdict cache
// (unseen verdicts age out after one park, epochs drop all); the waiter table (one per Sim, mask match); the view-change rule.
// Exit code 1 on failure.
using System;
using System.Collections.Generic;
using Ts3Fixes;

static class WakeParkTest
{
    static int sFails;
    static void Check(bool ok, string what) { if (!ok) { sFails++; Console.WriteLine("FAIL: " + what); } }

    // The game's Sim.WaitForExitReason on a tick clock: check, sleep one tick, read the clock, until now >= end.
    // reasonAt = tick the accepted reason appears (long.MaxValue = never). Returns the tick it returns and the result.
    static long Game(long start, long end, long reasonAt, out bool result)
    {
        long now = start;
        do
        {
            if (now >= reasonAt) { result = true; return now; }
            now += 1;
        }
        while (now < end);
        result = false;
        return now;
    }

    // Ours: sleep PollSleep ticks; woken (wake = true) the tick after the reason appears, as Sleep(1) would see it.
    static long Ours(long start, long end, long reasonAt, int k, bool wake, out bool result)
    {
        long now = start;
        do
        {
            if (now >= reasonAt) { result = true; return now; }
            long t = WakeParkCore.PollSleep(now, end, k);
            long woke = now + t;
            if (wake && reasonAt > now && reasonAt < woke) woke = reasonAt; // AddExitReason in tick r: runnable at r (next scheduling)
            now = woke;
        }
        while (now < end);
        result = false;
        return now;
    }

    static void PollEquivalence()
    {
        int cases = 0;
        foreach (int k in new int[] { 1, 2, 5, 30, 61 })
            for (long len = 0; len <= 70; len++)
                for (long r = 0; r <= 75; r++)
                {
                    long start = 1000, end = start + len, reasonAt = start + r;
                    bool g, o, m;
                    long tg = Game(start, end, reasonAt, out g);
                    long to = Ours(start, end, reasonAt, k, true, out o);
                    Check(tg == to && g == o, "woken poll k " + k + " len " + len + " reason " + r + ": game " + g + "@" + tg + " ours " + o + "@" + to);
                    long tm = Ours(start, end, reasonAt, k, false, out m);
                    // a reason that bypasses AddExitReason: seen up to k-1 ticks late, or missed (false) when it lands in the
                    // last poll before the deadline; with no reason before the deadline the two are identical
                    if (!g) Check(!m && tm == tg, "no reason: identical (k " + k + " len " + len + ")");
                    Check(tm >= tg && tm - tg < Math.Max(1, k), "missed wake k " + k + " len " + len + " reason " + r + ": " + tm + " vs " + tg);
                    cases++;
                }
        // open-ended wait (simMinutes = float.MaxValue): ends only on the reason
        bool res;
        Check(Ours(0, long.MaxValue, 500, 30, true, out res) == 500 && res, "open-ended woken wait returns on the reason tick");
        Check(WakeParkCore.PollSleep(0, long.MaxValue, 30) == 30, "open-ended poll sleeps k");
        Check(WakeParkCore.PollSleep(10, 5, 30) == 1 && WakeParkCore.PollSleep(10, 11, 30) == 1 && WakeParkCore.PollSleep(10, 13, 30) == 3, "deadline cap");
        Check(WakeParkCore.PollSleep(0, 100, 0) == 1 && WakeParkCore.PollSleep(0, 100, 1) == 1, "k <= 1 = the game's step");
        Check(cases > 1000, "poll cases ran");
    }

    static void Park()
    {
        Check(WakeParkCore.ParkSleep(12) == 11 && WakeParkCore.ParkSleep(1) == 0 && WakeParkCore.ParkSleep(0) == 0, "park sleep = k - 1");
        Check(WakeParkCore.Class(false, true) == 0 && WakeParkCore.Class(false, false) == 1 && WakeParkCore.Class(true, true) == 2 && WakeParkCore.Class(true, false) == 3, "class order");
        Check(!WakeParkCore.Stale(11, true, 12, 60) && WakeParkCore.Stale(12, true, 12, 60), "unseen verdict ages out after one park");
        Check(!WakeParkCore.Stale(59, false, 12, 60) && WakeParkCore.Stale(60, false, 12, 60) && WakeParkCore.Stale(-1, false, 12, 60), "seen verdict 60 ticks, clock going back = stale");
    }

    static void Cache()
    {
        WpCache c = new WpCache();
        int v; long t;
        Check(!c.Get(42, out v, out t), "empty miss");
        c.Put(42, 7, 100);
        Check(c.Get(42, out v, out t) && v == 7 && t == 100, "hit");
        Check(!c.Get(0, out v, out t), "id 0 never hits");
        ulong other = 42;
        for (ulong i = 43; i < 1000000; i++) { c.Put(i, 1, 1); if (!c.Get(42, out v, out t)) { other = i; break; } }
        Check(other != 42 && !c.Get(42, out v, out t), "collision evicts");
        c.Put(5, 3, 9);
        c.Clear();
        Check(!c.Get(5, out v, out t), "epoch clears");
    }

    static void Waiters()
    {
        WpWaiters w = new WpWaiters();
        Check(w.TryAdd(1, 100, 0x4, 0) && !w.TryAdd(1, 101, 0x4, 0), "one waiter per Sim");
        Check(w.Match(1, 0x2) == 0 && w.Match(1, 0x6) == 100 && w.Match(2, 0x4) == 0, "mask match");
        w.Remove(1, 101);
        Check(w.Count == 1, "remove by the wrong task keeps the entry");
        w.Remove(1, 100);
        Check(w.Count == 0, "remove");
        w.TryAdd(3, 7, 1, 0); w.TryAdd(4, 8, 1, 500);
        Check(w.Prune(600, 200) == 1 && w.Count == 1 && w.Match(4, 1) == 8, "prune old");
        List<ulong> all = new List<ulong>();
        w.AllTasks(all);
        Check(all.Count == 1 && all[0] == 8, "all tasks");
        WpParked p = new WpParked();
        p.Add(9, 0); p.Add(10, 400);
        Check(p.Prune(500, 200) == 1 && p.Count == 1 && p.Remove(10) && !p.Remove(10), "parked prune/remove");
    }

    static void View()
    {
        WpView v = new WpView();
        Check(!v.Changed(0, 0, 1, 2, true), "first feed records");
        Check(!v.Changed(5, 5, 1, 2, true), "small pan");
        Check(v.Changed(30, 5, 1, 2, true), "jump");
        Check(v.Changed(30, 5, 9, 2, true) && v.Changed(30, 5, 9, 3, true) && v.Changed(30, 5, 9, 3, false), "household, selection, live mode");
        v.Reset();
        Check(!v.Changed(500, 500, 0, 0, false), "reset = first feed again");
    }

    static int Main()
    {
        PollEquivalence(); Park(); Cache(); Waiters(); View();
        Console.WriteLine(sFails == 0 ? "wakepark self-test ok" : sFails + " failure(s)");
        return sFails == 0 ? 0 : 1;
    }
}
