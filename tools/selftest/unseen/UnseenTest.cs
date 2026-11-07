// Usage: node tools/selftest/unseen/run.ts  (compiles this with mods/ts3fixes/src/UnseenThrottleCore.cs)
// Offline self-check of the unseen script throttle math (docs/144): the per-key "one call in n" gate with its stagger
// and snap-back, the motive time gate with its cap, and the N x dt equivalence of a clamped linear integrator (the shape
// of Motive.UpdateMotiveWithDecay + SetValue) against N steps of dt, including how late a threshold is crossed.
// Exit code 1 on failure.
using System;
using System.Collections.Generic;
using Ts3Fixes;

static class UnseenTest
{
    static int sFails;
    static void Check(bool ok, string what) { if (!ok) { sFails++; Console.WriteLine("FAIL: " + what); } }

    static void GateCadence()
    {
        EveryN g = new EveryN();
        for (int n = 1; n <= 8; n++)
            for (int key = 0; key < 40; key++)
            {
                g.Clear();
                int runs = 0, last = -1, first = -1;
                bool even = true;
                for (int call = 0; call < n * 10; call++)
                    if (g.Unseen(key * 7919, n))
                    {
                        if (first < 0) first = call;
                        if (last >= 0 && call - last != n) even = false;
                        last = call; runs++;
                    }
                Check(runs == 10, "n=" + n + " key " + key + ": 10 runs in 10n calls (" + runs + ")");
                Check(even, "n=" + n + " key " + key + ": runs exactly n calls apart");
                Check(first == (key * 7919) % n, "n=" + n + " key " + key + ": first run staggered by the key");
            }
        // eight Sims called once each per round, n = 4: two of them run every round (load spread, not all at once)
        g.Clear();
        for (int round = 0; round < 12; round++)
        {
            int runs = 0;
            for (int key = 0; key < 8; key++) if (g.Unseen(key, 4)) runs++;
            Check(runs == 2, "round " + round + ": 2 of 8 Sims run (" + runs + ")");
        }
        // snap back: a seen call forgets the key, the next unseen stretch starts staggered again
        g.Clear();
        Check(!g.Unseen(3, 4) && !g.Unseen(3, 4) && !g.Unseen(3, 4) && g.Unseen(3, 4), "key 3, n 4: runs on its 4th unseen call");
        g.Seen(3);
        Check(g.Count == 0, "Seen removes the key");
        Check(!g.Unseen(3, 4), "after Seen: staggered again, not run at once (the seen call just ran)");
        Check(g.Unseen(4, 4), "key 4, n 4: first unseen call runs (4 % 4 == 0)");
        Check(g.Unseen(-5, 0) && g.Unseen(-5, 1), "n <= 1 always runs");
        Check(g.Unseen(-5, 3) == (((-5) & 0x7FFFFFFF) % 3 == 0), "negative key staggered on its low 31 bits");
        // a full table starts over instead of growing
        g.Clear();
        for (int key = 0; key < EveryN.kMaxKeys + 10; key++) g.Unseen(key, 4);
        Check(g.Clears == 1 && g.Count == 10, "full table restarts once (" + g.Clears + ", " + g.Count + ")");
    }

    static void TimeGate()
    {
        Check(!UnseenMath.Due(3.99f, 4, 1f, 30f), "3.99 of 4 sim-minutes: wait");
        Check(UnseenMath.Due(4f, 4, 1f, 30f), "4 of 4: due");
        Check(UnseenMath.Due(0f, 1, 1f, 30f), "n = 1: always due (the game's own 1-minute floor applies)");
        Check(!UnseenMath.Due(29.9f, 100, 1f, 30f) && UnseenMath.Due(30f, 100, 1f, 30f), "cap: due at 30 whatever n");
        Check(UnseenMath.Key(0x0000000100000002UL) == 3, "key folds the high half in");
    }

    // Clamped linear integrator: value += rate * dt, clamped to [-100, 100] after each step (SetValue's clamp).
    static float Step(float v, float rate, float dt) { return Math.Max(-100f, Math.Min(100f, v + rate * dt)); }

    static void Integrators()
    {
        Random r = new Random(1234);
        for (int i = 0; i < 2000; i++)
        {
            int n = 1 + r.Next(16);
            float dt = 0.05f + (float)r.NextDouble() * 0.5f;  // hours per base update
            float rate = (float)(r.NextDouble() * 400 - 200);  // motive points per hour, either sign
            float v0 = (float)(r.NextDouble() * 200 - 100);
            float small = v0;
            for (int k = 0; k < n; k++) small = Step(small, rate, dt);
            float big = Step(v0, rate, n * dt);
            Check(Math.Abs(small - big) < 1e-2f, "constant rate: N steps == one N x dt step (" + small + " vs " + big + ")");
        }
        // the rate changes inside the window (an interaction ends): the error is bounded by |r1 - r2| x window
        for (int i = 0; i < 2000; i++)
        {
            int n = 2 + r.Next(15), j = 1 + r.Next(n - 1);
            float dt = 0.05f + (float)r.NextDouble() * 0.5f;
            float r1 = (float)(r.NextDouble() * 100 - 50), r2 = (float)(r.NextDouble() * 100 - 50);
            float v0 = (float)(r.NextDouble() * 100 - 50);
            float small = v0;
            for (int k = 0; k < n; k++) small = Step(small, k < j ? r1 : r2, dt);
            float big = Step(v0, r2, n * dt); // one update at the end sees only the new rate
            Check(Math.Abs(small - big) <= Math.Abs(r1 - r2) * j * dt + 1e-2f, "rate change: error within |r1 - r2| x (j dt)");
        }
        // a threshold (a moodlet trigger such as Hungry at -50) is noticed at most one window late
        for (int i = 0; i < 500; i++)
        {
            int n = 1 + r.Next(16);
            float dt = 0.05f + (float)r.NextDouble() * 0.2f, rate = -(10f + (float)r.NextDouble() * 90f), th = -50f;
            float v = 100f, t = 0f, tSmall = -1f;
            for (int k = 0; k < 10000 && tSmall < 0; k++) { v = Step(v, rate, dt); t += dt; if (v <= th) tSmall = t; }
            v = 100f; t = 0f; float tBig = -1f;
            for (int k = 0; k < 10000 && tBig < 0; k++) { v = Step(v, rate, n * dt); t += n * dt; if (v <= th) tBig = t; }
            Check(tBig >= tSmall - 1e-3f && tBig - tSmall <= (n - 1) * dt + 1e-3f, "threshold seen at most N-1 base steps late");
        }
    }

    // The game skips an update below 1 sim-minute and leaves mLastTime alone; the throttle does the same below N. Calls
    // arrive at irregular gaps (SimUpdate slots): integrated + pending time always equals elapsed time, and the pending gap
    // stays under the cap.
    static void NoTimeLost()
    {
        Random r = new Random(99);
        for (int run = 0; run < 200; run++)
        {
            int n = 1 + r.Next(40);
            float now = 0f, last = 0f, integrated = 0f, maxPending = 0f;
            for (int call = 0; call < 5000; call++)
            {
                now += 0.1f + (float)r.NextDouble() * 1.5f;
                float gap = now - last;
                if (gap < 1f || !UnseenMath.Due(gap, n, 1f, 30f)) { maxPending = Math.Max(maxPending, gap); continue; }
                integrated += gap; last = now;
            }
            Check(Math.Abs(integrated + (now - last) - now) < 0.05f, "n=" + n + ": no sim time lost or added");
            Check(maxPending < 30f + 1.6f, "n=" + n + ": pending gap stays under the cap plus one call gap (" + maxPending + ")");
            Check(maxPending < 60f, "n=" + n + ": never reaches the 60-minute auto-satisfy switch");
        }
    }

    // docs/144 §8: 40 Sims due on the same sim-minute run at most kCatchUpsPerTick per tick, every one within ceil(40/4)
    // ticks, none lost; a forced (late) one runs over the cap.
    static void CatchUpCap()
    {
        TickCap cap = new TickCap();
        int k = UnseenMath.kCatchUpsPerTick;
        List<int> waiting = new List<int>();
        for (int i = 0; i < 40; i++) waiting.Add(i);
        long tick = 100;
        int ticks = 0;
        while (waiting.Count > 0 && ticks < 100)
        {
            int ran = 0;
            for (int i = waiting.Count - 1; i >= 0; i--) if (cap.Take(tick, k, false)) { waiting.RemoveAt(i); ran++; }
            Check(ran <= k, "tick " + tick + ": " + ran + " catch-ups, cap " + k);
            tick++; ticks++;
        }
        Check(waiting.Count == 0 && ticks == (40 + k - 1) / k, "40 due Sims all run within " + ((40 + k - 1) / k) + " ticks (" + ticks + ")");
        Check(cap.Most == k && cap.Held > 0, "most per tick " + cap.Most + ", held " + cap.Held);
        for (int i = 0; i < k; i++) cap.Take(500, k, false);
        Check(!cap.Take(500, k, false) && cap.Take(500, k, true), "over the cap: waits, unless forced");
        Check(UnseenMath.kHardGapMin < 60f && UnseenMath.kHardGapMin > 30f, "hard gap between the 30-minute cap and the 60-minute auto-satisfy switch");
    }

    // docs/243 D1: an idle Sim asks every 5 sim-minutes (EA's kAutonomyDelayNormal), held for 30 after each search. mLastTime
    // moves only when a search runs, so the search rate is the gate's and a held Sim is never held past N plus one ask.
    static void ParkGate()
    {
        Check(!UnseenMath.ParkHold(0f, 100f, 30f), "no clock yet never holds");
        Check(!UnseenMath.ParkHold(200f, 100f, 30f), "a clock ahead of the game never holds");
        Check(UnseenMath.ParkHold(100f, 100f, 30f) && UnseenMath.ParkHold(100f, 129.9f, 30f), "inside the window holds, also under 1 sim-minute");
        Check(!UnseenMath.ParkHold(100f, 130f, 30f), "at N it lets go");
        Check(!UnseenMath.ParkHold(100f, 100f + UnseenMath.kParkCapMin, 500f), "a big tunable stays under the cap");
        Check(UnseenMath.kParkCapMin <= 240f, "cap inside I-243-4");
        Check(!UnseenMath.ParkHold(100f, 105f, 0f) && !UnseenMath.ParkHold(100f, 105f, -1f), "0 or less turns it off");
        Check(UnseenMath.ParkVerdict(true, true, false) == UnseenMath.kParkHold, "unseen in the window: held");
        Check(UnseenMath.ParkVerdict(false, true, false) == UnseenMath.kParkSnap, "seen in the window: snaps back");
        Check(UnseenMath.ParkVerdict(false, true, true) == UnseenMath.kParkSnap, "seen wins over exempt");
        Check(UnseenMath.ParkVerdict(true, true, true) == UnseenMath.kParkExempt, "exempt in the window: goes");
        Check(UnseenMath.ParkVerdict(true, false, false) == UnseenMath.kParkUnseen && UnseenMath.ParkVerdict(false, false, false) == UnseenMath.kParkSeen, "outside the window: goes");
        float[] edges = { -1f, 0.99f, 1f, 4.99f, 5f, 14.99f, 15f, 29.99f, 30f, 500f };
        int[] slots = { 0, 0, 1, 1, 2, 2, 3, 3, 4, 4 };
        for (int i = 0; i < edges.Length; i++) Check(UnseenMath.RefreshBucket(edges[i]) == slots[i], "bucket of " + edges[i]);
        float last = 5f, worst = 0f;
        int runs = 0, held = 0;
        for (float now = 10f; now <= 1440f; now += 5f)
        {
            if (UnseenMath.ParkHold(last, now, 30f)) { held++; continue; }
            if (now - last > worst) worst = now - last;
            last = now;
            runs++;
        }
        Check(runs == 47 && held > 200, "one search per 30 sim-minutes, got " + runs + ", held " + held);
        Check(worst <= 35f, "never held past N plus one ask, got " + worst);
    }

    // Stagger of snap-back: 30 Sims seen in one tick release spread over the 16 ticks, each within 16, for every poll phase and gap.
    static void ParkStagger()
    {
        Check(UnseenMath.ParkStaggerPass(5, 7UL, 1) && UnseenMath.ParkStaggerPass(5, 7UL, 0) && UnseenMath.ParkStaggerPass(5, 7UL, 7), "stagger 1 (or under two slots) = old behaviour, always passes");
        Check(UnseenMath.ParkStaggerPass(9, 12345UL, 16) == UnseenMath.ParkStaggerPass(9, 12345UL, 16), "deterministic");
        foreach (int gap in new[] { 2, 3, 4 })     // 2 is the real poll; 3 and 4 stand for a late wake
            for (int phase = 0; phase < 4; phase++)  // poll phase against the slot grid, even and odd
            {
                int[] perTick = new int[64];
                int worst = 0;
                for (ulong id = 1; id <= 30; id++)
                {
                    long t = 1000 + phase;
                    int jitter = (int)(id % 3);        // some Sims wake a tick late now and then, never past the slot width
                    int n = 0;
                    while (!UnseenMath.ParkStaggerPass(t, 0x1000000UL + id * 977UL, 16) && n++ < 64) t += Math.Min(gap + (n % 2 == 0 ? jitter : 0), UnseenMath.kParkSlotTicks);
                    Check(n < 64, "gap " + gap + " phase " + phase + ": Sim " + id + " starved");
                    int delay = (int)(t - 1000 - phase);
                    if (delay > worst) worst = delay;
                    if (delay < 64) perTick[delay]++;
                }
                int max = 0;
                foreach (int p in perTick) if (p > max) max = p;
                Check(worst <= 16 + UnseenMath.kParkSlotTicks, "gap " + gap + " phase " + phase + ": release within the stagger, got " + worst);
                Check(max <= 12, "gap " + gap + " phase " + phase + ": max per tick well below 30, got " + max);
            }
        // no starvation for any id, any phase, polls at the real 2-tick gap: bounded by the stagger
        int bad = 0;
        for (ulong id = 1; id <= 2000; id++)
            for (int phase = 0; phase < 2; phase++)
            {
                long t = 777 + phase; int n = 0;
                while (!UnseenMath.ParkStaggerPass(t, id * 0x10001UL, 16) && n < 100) { t += 2; n++; }
                if (t - 777 - phase > 16) bad++;
            }
        Check(bad == 0, "every Sim releases within 16 ticks at 2-tick polls, late: " + bad);
    }

    static int Main()
    {
        ParkStagger();
        ParkGate();
        CatchUpCap();
        GateCadence();
        TimeGate();
        Integrators();
        NoTimeLost();
        Console.WriteLine(sFails == 0 ? "unseen selftest: PASS" : "unseen selftest: FAIL (" + sFails + ")");
        return sFails == 0 ? 0 : 1;
    }
}
