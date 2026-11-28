using System;
using System.Collections.Generic;

namespace Ts3Fixes
{
    // nextplan (docs/686 §38): the game-independent rules, offline-tested by tools/selftest/nextplan.
    //   Decide   whether a Sim whose queue just ran empty runs EA's autonomy pass on its own task at once
    //   Claims   the short "claimed next target" table: one claim per Sim, expiring, O(1) per call, bounded sweep
    //   Hist     p50 / p90 of a gap in fixed log bins (no sort, no list growth)
    public sealed class NextPlanCore
    {
        // Decide's answers
        public const int kGo = 0, kOff = 1, kNotIdle = 2, kNoAutonomy = 3, kActiveDelay = 4, kBudget = 5, kLowLod = 6, kInManager = 7;
        public static readonly string[] DecideNames = { "go", "switch off", "not idle", "autonomy off", "active Sim keeps EA's delay", "per-tick budget", "low LOD NPC", "already in EA's queue" };

        // idle: queue empty, nothing running, no transition, the Sim's own task. eaNow: EA's CanRunAutonomyImmediately
        // or its Service / active-role rule (the Sim would be queued at once). activeSim: the selected Sim, or a Sim talking
        // with it (EA's socializing delay), or the last action was the player's cancel: EA's delay is the player's window.
        // household: a Sim of the active household. highLod: the Sim runs local autonomy. budgetLeft: NPC passes left this tick.
        public static int Decide(bool on, bool idle, bool autonomyOn, bool eaNow, bool activeSim, bool household, bool highLod, int budgetLeft)
        {
            if (!on) return kOff;
            if (!idle) return kNotIdle;
            if (!autonomyOn) return kNoAutonomy;
            if (!eaNow && (activeSim || !household)) return kActiveDelay;  // the player's window and every NPC delay stay EA's
            if (!household && !highLod) return kLowLod;                     // unseen NPCs: EA's one-a-frame queue is fine
            if (!household && budgetLeft <= 0) return kBudget;              // NPCs share a small per-tick budget; household never waits
            return kGo;
        }

        // ---- contention (user 2026-11-26: "different based on how many sims, utilities etc, so like a more relaxed routing
        // on a 8 sim household") ----
        // Facility kinds counted per lot: toilet, shower or bath, sink, fridge, bed.
        public const int kToilet = 0, kWash = 1, kSink = 2, kFridge = 3, kBed = 4, kKinds = 5;
        public const int kLow = 0, kMid = 1, kHigh = 2;
        public static readonly string[] LevelNames = { "low", "mid", "high" };

        // Sims on the lot per object of its scarcest kind (a kind with none counts as one object: the Sims go elsewhere for it,
        // which is not a line at home). sims: household Sims plus guests on the lot.
        public static float Ratio(int sims, int[] counts)
        {
            if (sims <= 0 || counts == null) return 0f;
            float r = 0f;
            for (int k = 0; k < counts.Length && k < kKinds; k++)
            {
                int n = counts[k] < 1 ? 1 : counts[k];
                // a double bed sleeps two; a fridge use is a 2 sim-min grab (a toilet 30): one fridge serves three Sims
                // (storms s660-s662: with one fridge per house every family home read "high")
                float per = k == kBed ? sims / (2f * n) : k == kFridge ? sims / (3f * n) : sims / (float)n;
                if (per > r) r = per;
            }
            return r;
        }

        // low: 1-2 Sims or plenty of facilities (fewer than 2 Sims per object); mid: up to 4; high: more (8 Sims, 1-2 bathrooms)
        public static int Level(float ratio) { return ratio < 2f ? kLow : ratio < 4f ? kMid : kHigh; }

        // claim life in sim-min: low a short window (the pick -> first step gap only), mid the base, high twice the base
        public static float ClaimMinutes(int level, float baseMin)
        {
            if (baseMin < 0.5f) baseMin = 0.5f;
            return level == kLow ? Math.Min(baseMin, 2f) : level == kHigh ? baseMin * 2f : baseMin;
        }

        // the factor another Sim's search gives a claimed object: EA's route-pending factor, and at high contention EA's in-use
        // factor (the object is as good as taken: a second-choice object or another room wins). Never above EA's own value.
        public static float ClaimFactor(int level, float orig, float routePending, float inUse)
        {
            float f = level == kHigh ? Math.Min(routePending, inUse) : routePending;
            return orig <= f ? orig : f;
        }

        // ---- claims ----
        public const int kMaxClaims = 256;
        struct Claim { public ulong Sim; public long Until; }
        readonly Dictionary<ulong, Claim> mByTarget = new Dictionary<ulong, Claim>();
        readonly Dictionary<ulong, ulong> mBySim = new Dictionary<ulong, ulong>();
        readonly List<ulong> mSweep = new List<ulong>(kMaxClaims);
        long mNextSweep;
        public int Expired, Full;

        public int Count { get { return mByTarget.Count; } }

        // sim claims target until now + ttl. A Sim holds one claim: its old one goes. false: table full (nothing claimed).
        public bool Add(ulong target, ulong sim, long now, long ttl)
        {
            Release(sim);
            Claim old;
            if (mByTarget.TryGetValue(target, out old))
            {
                ulong ot;
                if (old.Sim != sim && mBySim.TryGetValue(old.Sim, out ot) && ot == target) mBySim.Remove(old.Sim);  // newer claim wins (EA queued both)
            }
            else if (mByTarget.Count >= kMaxClaims) { Sweep(now, true); if (mByTarget.Count >= kMaxClaims) { Full++; return false; } }
            Claim c; c.Sim = sim; c.Until = now + ttl;
            mByTarget[target] = c;
            mBySim[sim] = target;
            return true;
        }

        // the Sim that holds target (0: none, or its claim expired; an expired claim is dropped here)
        public ulong Holder(ulong target, long now)
        {
            Claim c;
            if (!mByTarget.TryGetValue(target, out c)) return 0;
            if (c.Until > now) return c.Sim;
            mByTarget.Remove(target); Expired++;
            ulong t;
            if (mBySim.TryGetValue(c.Sim, out t) && t == target) mBySim.Remove(c.Sim);  // a hold has no by-Sim entry: the Sim's claim stays
            return 0;
        }

        public bool HeldByOther(ulong target, ulong sim, long now)
        {
            ulong h = Holder(target, now);
            return h != 0 && h != sim;
        }

        public ulong TargetOf(ulong sim) { ulong t; return mBySim.TryGetValue(sim, out t) ? t : 0; }

        // ---- holds (docs/686 §41): the other household fixes' reservations (a chair a Sim walks to, a puddle a Sim mops)
        // live in the same table, so every search and every fix reads one answer from Holder. A hold differs from a claim
        // in one way: it is not the Sim's "next target", so the Sim's next claim does not drop it and Release(sim) leaves
        // it; it ends by Drop(target, sim) or by its expiry. A hold on a claimed target replaces the claim.
        public bool Hold(ulong target, ulong sim, long now, long ttl)
        {
            Claim old;
            if (mByTarget.TryGetValue(target, out old))
            {
                ulong t;
                if (mBySim.TryGetValue(old.Sim, out t) && t == target) mBySim.Remove(old.Sim);
            }
            else if (mByTarget.Count >= kMaxClaims) { Sweep(now, true); if (mByTarget.Count >= kMaxClaims) { Full++; return false; } }
            Claim c; c.Sim = sim; c.Until = now + ttl;
            mByTarget[target] = c;
            return true;
        }

        // ends sim's hold or claim on target; another Sim's stays
        public void Drop(ulong target, ulong sim)
        {
            Claim c;
            if (!mByTarget.TryGetValue(target, out c) || c.Sim != sim) return;
            mByTarget.Remove(target);
            ulong t;
            if (mBySim.TryGetValue(sim, out t) && t == target) mBySim.Remove(sim);
        }

        public void Release(ulong sim)
        {
            ulong t;
            if (!mBySim.TryGetValue(sim, out t)) return;
            mBySim.Remove(sim);
            Claim c;
            if (mByTarget.TryGetValue(t, out c) && c.Sim == sim) mByTarget.Remove(t);
        }

        // drop expired claims: at most once per sweepEvery ticks unless forced; one pass over at most kMaxClaims entries
        public const long kSweepEvery = 40;
        public void Sweep(long now, bool force)
        {
            if (!force && now < mNextSweep) return;
            mNextSweep = now + kSweepEvery;
            mSweep.Clear();
            foreach (KeyValuePair<ulong, Claim> kv in mByTarget) if (kv.Value.Until <= now) mSweep.Add(kv.Key);
            for (int i = 0; i < mSweep.Count; i++)
            {
                Claim c = mByTarget[mSweep[i]];
                mByTarget.Remove(mSweep[i]);
                ulong t;
                if (mBySim.TryGetValue(c.Sim, out t) && t == mSweep[i]) mBySim.Remove(c.Sim);
                Expired++;
            }
        }

        public void Clear() { mByTarget.Clear(); mBySim.Clear(); mNextSweep = 0; }

        // ---- gap histogram ----
        public sealed class Hist
        {
            // bin i holds values below Edge(i): 0, 1, 2, 4, 8 ... (x2) up to 2^24
            public const int kBins = 26;
            public readonly int[] Bins = new int[kBins];
            public int N; public long Sum, Max;

            public static int BinOf(long v)
            {
                if (v <= 0) return 0;
                int b = 1;
                while (b < kBins - 1 && (1L << (b - 1)) <= v) b++;
                return b;
            }
            public static long Edge(int b) { return b <= 0 ? 0 : 1L << (b - 1); }

            public void Add(long v)
            {
                if (v < 0) v = 0;
                Bins[BinOf(v)]++; N++; Sum += v; if (v > Max) Max = v;
            }

            // upper edge of the bin the p-th percentile falls in (an upper bound, at most x2 off)
            public long P(int p)
            {
                if (N == 0) return 0;
                long want = ((long)N * p + 99) / 100, seen = 0;
                for (int b = 0; b < kBins; b++) { seen += Bins[b]; if (seen >= want) return b == kBins - 1 ? Max : Math.Min(Max, Edge(b + 1)); }
                return Max;
            }

            public string Text() { return N == 0 ? "n=0" : "p50 " + P(50) + " p90 " + P(90) + " max " + Max + " mean " + (Sum / N) + " (n=" + N + ")"; }
            public void Reset() { Array.Clear(Bins, 0, kBins); N = 0; Sum = 0; Max = 0; }
        }
    }
}
