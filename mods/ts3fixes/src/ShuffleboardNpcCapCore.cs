using System;
using System.Collections.Generic;

namespace Ts3Fixes
{
    // Game-independent rule of shuffleboardnpccap (ShuffleboardNpcCap.cs, docs/647). Self-check: tools/selftest/shuffleboardnpccap.
    // A board keeps a streak: the round starts seen at the start of a Play continuation (EA's mShouldRestart or
    // mShouldClearRound set), the sim time of the first one and of the last one. A start later than GapTicks after the last
    // one (or a clock that went back) begins a new streak. At a round start the streak is capped when, with at least one
    // round done, it is night (NightFrom..NightTo, wrapping midnight), when the rounds done reach RoundsCap (games x EA's
    // rounds per game), or when the streak is CapTicks old. A capped streak stays capped (the other players of the board
    // leave at the same round start) until the gap starts a new one. A Sim whose Play was ended is refused autonomous re-joins
    // for CooldownTicks. Tables are bounded (kMax): past it the stale entries go, then all.
    public sealed class ShuffleboardNpcCapCore
    {
        public const int kMax = 128;
        // verdicts
        public const int kNone = 0, kGames = 1, kNight = 2, kHours = 3;

        public struct Tun
        {
            public int RoundsCap;      // rounds done before the cap (0 = no round cap)
            public long CapTicks;      // streak age before the cap (0 = no age cap)
            public long GapTicks;      // a round start later than this after the last one begins a new streak
            public int NightFrom, NightTo;  // hours; equal = no night window
            public long CooldownTicks; // re-join refusal after an end (0 = none)
        }

        public sealed class Board { public int Rounds; public long First, Last; public bool Capped; public int Reason; }

        readonly Dictionary<ulong, Board> mBoards = new Dictionary<ulong, Board>();
        readonly Dictionary<ulong, long> mCool = new Dictionary<ulong, long>();  // Sim -> sim time of its end

        public int CoolCount { get { return mCool.Count; } }

        public static string Why(int v)
        {
            return v == kGames ? "games" : v == kNight ? "night" : v == kHours ? "hours" : "none";
        }

        public static bool Night(int hour, int from, int to)
        {
            if (from == to) return false;
            return from < to ? hour >= from && hour < to : hour >= from || hour < to;
        }

        // pure: the cap for a streak with rounds done and age at this hour
        public static int Rule(int rounds, long age, int hour, Tun t)
        {
            if (rounds <= 0) return kNone;  // the first round of a streak always runs
            if (Night(hour, t.NightFrom, t.NightTo)) return kNight;
            if (t.RoundsCap > 0 && rounds >= t.RoundsCap) return kGames;
            if (t.CapTicks > 0 && age >= t.CapTicks) return kHours;
            return kNone;
        }

        // a round start on board at now: kNone (the round runs, counted) or the cap verdict (the Play ends)
        public int RoundStart(ulong board, long now, int hour, Tun t, out Board b)
        {
            if (!mBoards.TryGetValue(board, out b) || now < b.Last || now - b.Last > t.GapTicks)
            {
                if (b == null && mBoards.Count >= kMax) Prune(now, t.GapTicks);
                b = new Board();
                b.First = now;
                mBoards[board] = b;
            }
            b.Last = now;
            if (b.Capped) return b.Reason;
            int v = Rule(b.Rounds, now - b.First, hour, t);
            if (v == kNone) { b.Rounds++; return kNone; }
            b.Capped = true;
            b.Reason = v;
            return v;
        }

        public void Ended(ulong sim, long now, long cooldownTicks)
        {
            if (cooldownTicks <= 0 || sim == 0) return;
            if (mCool.Count >= kMax && !mCool.ContainsKey(sim)) PruneCool(now, cooldownTicks);
            mCool[sim] = now;
        }

        // true: refuse this autonomous re-join (ended less than cooldownTicks ago)
        public bool Cooling(ulong sim, long now, long cooldownTicks)
        {
            long at;
            if (cooldownTicks <= 0 || !mCool.TryGetValue(sim, out at)) return false;
            if (now >= at && now - at < cooldownTicks) return true;
            mCool.Remove(sim);
            return false;
        }

        void Prune(long now, long gap)
        {
            List<ulong> old = new List<ulong>();
            foreach (KeyValuePair<ulong, Board> kv in mBoards)
                if (now < kv.Value.Last || now - kv.Value.Last > gap) old.Add(kv.Key);
            for (int i = 0; i < old.Count; i++) mBoards.Remove(old[i]);
            if (mBoards.Count >= kMax) mBoards.Clear();
        }

        void PruneCool(long now, long cooldown)
        {
            List<ulong> old = new List<ulong>();
            foreach (KeyValuePair<ulong, long> kv in mCool)
                if (now < kv.Value || now - kv.Value >= cooldown) old.Add(kv.Key);
            for (int i = 0; i < old.Count; i++) mCool.Remove(old[i]);
            if (mCool.Count >= kMax) mCool.Clear();
        }

        public void Clear() { mBoards.Clear(); mCool.Clear(); }
    }
}
