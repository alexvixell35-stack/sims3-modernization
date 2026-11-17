namespace Ts3Fixes
{
    // Game-independent part of the tier-2 L0 local-phase probe (AutLocalProbe.cs, switch autt2l0,
    // docs/data/tier2-local-phase-handoff.md §7 L0). Self-check: tools/selftest/autlocalprobe.
    public static class AutLocalProbeCore
    {
        // counter slots (docs/registry.md fixes-903)
        public const int kBase = 903;
        public const int kHh = 0, kSeen = 1, kUnseen = 2;                  // actor class
        public const int kSearches = 903, kUs = 906, kPairSearches = 909, kPairs = 912;  // + class
        public const int kSideN = 915, kSideUs = 916, kNoLocalN = 917, kNoLocalUs = 918;
        public const int kReason = 919;                                     // + reason, unseen non-household local searches only
        public const int kRActiveLot = 0, kRSameLot = 1, kRDaycare = 2, kRResort = 3, kRHousehold = 4, kROther = 5;
        public const int kSpanMs = 925, kNested = 926, kExc = 927;
        public const int kLast = 927;

        // UfWhy code -> class. Household, gone, not live and a failed check (-1) are household (unknown scope = household);
        // kSeen (0) is the only unseen answer.
        public static int Class(int why)
        {
            if (UnseenFallbackCore.HouseholdScope(why)) return kHh;
            return why == UnseenFallbackCore.kSeen ? kUnseen : kSeen;
        }

        // B8's branches in its own order (read-only replica, evaluated after the search): which one holds now. kROther = on
        // screen, a hosted-situation guest, or the 60-tick cached flag from an earlier state.
        public static int Reason(bool sameHousehold, bool lotActive, bool sameNonWorldLot, bool daycare, bool resort)
        {
            if (sameHousehold) return kRHousehold;
            if (lotActive) return kRActiveLot;
            if (sameNonWorldLot) return kRSameLot;
            if (daycare) return kRDaycare;
            if (resort) return kRResort;
            return kROther;
        }
    }
    // docs/276 §2.3 A + docs/277 §5, switch hhsearchprobe (HhSearchProbe.cs, measurement only, default off): is the household
    // autonomy search rate a retry loop or real demand? One record per household-class Sim (household or unknown scope, as the
    // L0 probe; by object id, no Sim reference kept), fed at each search's push decision (Autonomy.ConsiderPerformingAutonomousAction,
    // reached by every RunAutonomy search with addToQueue, pick or no pick). Counters (docs/registry.md fixes-1069, fixes-1115):
    // [1073] searches, [1074] with the local phase, [1075] pushed, [1076] nothing picked, [1077] picked but not queued under a
    // user-directed head, [1078] the same under an autonomous head, [1079] same pick as the Sim's previous pick, [1080] < 2
    // sim-minutes since the Sim's previous search, [1081] no push after a no push, [1082] Sims, [1083] most searches by one Sim,
    // [1084] exceptions; [1115-1118] scope household / unknown / gone / not live; [1119-1125] why enqueued (AutonomyManager.Add,
    // read-only replica of Sim.DoInteraction's idle branch): immediate after a succeeded autonomous interaction, immediate after a
    // failed one (non-selectable only), delay path with the queue empty < 5, 5-10, 10-60, > 60 sim-minutes, no Add seen;
    // [1126-1129] gap since the Sim's previous search < 0.5 s, 0.5-2 s, 2-10 s, >= 10 s; [1130] nothing picked after > 60 sim-min
    // empty (the docs/277 §2.1 signature); [1131-1132] searches with the L0 pair count, pairs; [1133] on the active household
    // lot; [1134] pets.
    public sealed class HhSearchRec
    {
        public const int kN = 1073, kLocal = 1074, kPush = 1075, kNoPick = 1076, kBusyUser = 1077, kBusyAuto = 1078,
            kSameDef = 1079, kShortGap = 1080, kNoPushRun = 1081, kSims = 1082, kMaxPerSim = 1083, kExc = 1084,
            kScope = 1115, kReason = 1119, kGap = 1126, kNoPick60 = 1130, kPairN = 1131, kPairs = 1132, kActiveLot = 1133, kPets = 1134, kLast = 1134;
        public const int kBusyNone = 0, kBusyUserHead = 1, kBusyAutoHead = 2;
        public const int kScopeHh = 0, kScopeUnknown = 1, kScopeGone = 2, kScopeNotLive = 3;
        public const int kRImmOk = 0, kRImmFail = 1, kRLt5 = 2, kR5to10 = 3, kR10to60 = 4, kROver60 = 5, kRNone = 6;
        public const float kShortGapMin = 2f;
        static readonly string[] kReasonName = { "immOk", "immFail", "lt5", "5-10", "10-60", ">60", "none" };
        const int kMaxKeys = 64;

        // UfWhy code (household class only) -> scope slot
        public static int Scope(int why)
        {
            if (why == UnseenFallbackCore.kHousehold) return kScopeHh;
            if (why == UnseenFallbackCore.kGone) return kScopeGone;
            if (why == UnseenFallbackCore.kNotLive) return kScopeNotLive;
            return kScopeUnknown;
        }

        // Sim.CanRunAutonomyImmediately and the delay test of Sim.DoInteraction's idle branch, in EA's order, as a reason code
        public static int Reason(bool shouldRunLocal, bool inConvAsActiveUser, bool lastWasAuto, bool lastOk, bool selectable, float minSinceEmpty)
        {
            if (shouldRunLocal && !inConvAsActiveUser && lastWasAuto && (lastOk || !selectable)) return lastOk ? kRImmOk : kRImmFail;
            if (minSinceEmpty < 5f) return kRLt5;  // negative, service, role, or a shorter mounted / socializing delay
            if (minSinceEmpty < 10f) return kR5to10;
            return minSinceEmpty < 60f ? kR10to60 : kROver60;
        }

        public static int GapBin(int ms) { return ms < 500 ? 0 : ms < 2000 ? 1 : ms < 10000 ? 2 : 3; }

        public readonly string Name;
        public readonly bool Pet, Selectable;
        public int N, Local, Push, NoPick, BusyUser, BusyAuto, SameDef, ShortGap, NoPushRun, OnActiveLot;
        public int ScopeLast;
        public readonly int[] Reasons = new int[7], Gaps = new int[4];
        public float FirstMin = -1f, LastMin = -1f;
        public int LastMs;
        public string LastDef;          // last pick (null = none yet)
        bool mLastPushed = true, mHasMs;
        public ulong LotId; public int Level;
        readonly System.Collections.Generic.Dictionary<string, int> mDefs = new System.Collections.Generic.Dictionary<string, int>();
        readonly System.Collections.Generic.Dictionary<string, int> mPrev = new System.Collections.Generic.Dictionary<string, int>();
        readonly System.Collections.Generic.Dictionary<string, int> mLow = new System.Collections.Generic.Dictionary<string, int>();

        public HhSearchRec(string name, bool pet, bool selectable) { Name = name; Pet = pet; Selectable = selectable; }

        static void Bump(System.Collections.Generic.Dictionary<string, int> d, string k)
        {
            int n;
            if (d.TryGetValue(k, out n)) d[k] = n + 1; else if (d.Count < kMaxKeys) d[k] = 1;
        }

        // One search end. def = pick (null = nothing picked); busy = queue head before the push (kBusy*); reason = why enqueued
        // (kR*); prevPair = "<previous autonomous pick or user/other>|ok|fail" from the Add read; lowest = lowest motive at a
        // no-pick ("Hygiene=-100"), else null; nowMs = Environment.TickCount; simMin = sim minutes.
        public void Note(int[] c, int scope, bool local, string def, bool pushed, int busy, int reason, string prevPair, string lowest,
            ulong lot, int level, bool activeLot, int nowMs, float simMin)
        {
            N++; c[kN]++;
            ScopeLast = scope; c[kScope + scope]++;
            if (local) { Local++; c[kLocal]++; }
            if (def == null) { NoPick++; c[kNoPick]++; if (reason == kROver60) c[kNoPick60]++; if (lowest != null) Bump(mLow, lowest); }
            else if (pushed) { Push++; c[kPush]++; Bump(mDefs, def); }
            else if (busy == kBusyUserHead) { BusyUser++; c[kBusyUser]++; }
            else { BusyAuto++; c[kBusyAuto]++; }
            Reasons[reason]++; c[kReason + reason]++;
            if (prevPair != null) Bump(mPrev, prevPair);
            if (def != null && def == LastDef) { SameDef++; c[kSameDef]++; }
            if (LastMin >= 0f && simMin >= LastMin && simMin - LastMin < kShortGapMin) { ShortGap++; c[kShortGap]++; }
            if (mHasMs) { int b = GapBin(System.Math.Max(0, unchecked(nowMs - LastMs))); Gaps[b]++; c[kGap + b]++; }
            if (!pushed && !mLastPushed) { NoPushRun++; c[kNoPushRun]++; }
            if (activeLot) { OnActiveLot++; c[kActiveLot]++; }
            if (def != null) LastDef = def;
            mLastPushed = pushed;
            mHasMs = true; LastMs = nowMs;
            LotId = lot; Level = level;
            if (FirstMin < 0f) FirstMin = simMin;
            LastMin = simMin;
            if (N > c[kMaxPerSim]) c[kMaxPerSim] = N;
        }

        static string Top(System.Collections.Generic.Dictionary<string, int> d, int k)
        {
            System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, int>> all = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, int>>(d);
            all.Sort(delegate (System.Collections.Generic.KeyValuePair<string, int> x, System.Collections.Generic.KeyValuePair<string, int> y) { return y.Value != x.Value ? y.Value.CompareTo(x.Value) : string.CompareOrdinal(x.Key, y.Key); });
            System.Text.StringBuilder b = new System.Text.StringBuilder();
            for (int i = 0; i < all.Count && i < k; i++) b.Append(i > 0 ? "," : "").Append(all[i].Key).Append('(').Append(all[i].Value).Append(')');
            return all.Count == 0 ? "-" : b.ToString();
        }

        // One summary line per Sim (docs/277 §5 items 1-7).
        public string Line(ulong descId)
        {
            System.Text.StringBuilder r = new System.Text.StringBuilder();
            for (int i = 0; i < Reasons.Length; i++) r.Append(i > 0 ? "," : "").Append(kReasonName[i]).Append('=').Append(Reasons[i]);
            float h = (LastMin - FirstMin) / 60f;
            return Name + " desc=" + descId.ToString("x") + (Pet ? " pet" : "") + (Selectable ? " sel" : " notsel") + " scope=" + ScopeLast
                + ": n=" + N + " local=" + Local + " push=" + Push + " nopick=" + NoPick + " notQueuedUser=" + BusyUser + " notQueuedAuto=" + BusyAuto
                + " sameDef=" + SameDef + " gap<" + kShortGapMin + "m=" + ShortGap + " noPushRun=" + NoPushRun
                + " gapMs=" + Gaps[0] + "/" + Gaps[1] + "/" + Gaps[2] + "/" + Gaps[3] + " why=[" + r + "]"
                + " perSimHour=" + (h > 0f ? (N / h).ToString("0.0") : "-") + " lot=" + LotId.ToString("x") + " level=" + Level + " onActiveLot=" + OnActiveLot
                + " pushed=[" + Top(mDefs, 10) + "] prev=[" + Top(mPrev, 10) + "] lowestAtNoPick=[" + Top(mLow, 3) + "]";
        }
    }
}
