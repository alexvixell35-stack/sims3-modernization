using System;
using System.Collections.Generic;
using System.Text;

namespace Ts3Fixes
{
    // actdelay (docs/684): where the time goes between "an interaction was put in a Sim's queue" and "the Sim visibly acts".
    // Pure bookkeeping, no game types: ActDelay.cs feeds the events from pass-through hooks, tools/selftest/actdelay drives
    // the same class offline. Times are microseconds of one monotonic clock (never 0), ticks are sim ticks.
    //
    // One record per queued interaction instance, from OnAddedToQueue until it acts, ends or leaves the queue:
    //   TQ     added to the queue
    //   THead  nothing is in front of it any more (added to an empty queue, or the head before it was removed)
    //   TWake  the Sim's loop took it up: the first posture transition or its own Run begins
    //   TRun   InteractionInstance.Run begins (PushRunningInteraction of the head)
    //   TPlan1 / TPlanEnd  first route plan asked / last route plan returned, inside Run
    //   act    the Sim starts to follow a route (OnAboutToStartFollowingRoute) or, when the interaction sets the Sim up on
    //          an animation state machine well before any plan (or never routes), that moment
    // Buckets (they add up to act - TQ):
    //   queue    TQ -> THead      waiting behind earlier interactions (the Sim is busy, not idle)
    //   wake     THead -> TWake   the Sim's own loop has not picked it up (loop sleep, idle animation, a frozen sim thread)
    //   prerun   TWake -> TRun    posture transitions and tests before Run
    //   preroute TRun -> TPlan1   Run's own setup before it asks for a route (all of Run -> act when it never plans)
    //   plan-cpu                  plan time on the sim thread (sync plans, lock waits inside them, the hand-over)
    //   plan-job                  plan time the Sim's task slept waiting for a ts3native route job
    //   retry                     time between plans (TPlan1 -> TPlanEnd minus the plans): re-plans, placement searches, waits
    //   postplan TPlanEnd -> act  from the last plan to the first step
    // Beside the buckets, per record (they overlap the buckets, they do not add to them; docs/684 §8):
    //   outfit   time the Sim's task spent in Sim.SwitchOutfitHelper.Wait (the native outfit build) before it acted
    //   sync     time in the sync waits (StartSync, WaitForSynchronizationLevelWithSim, ReceiveSocialInteraction) outside plans
    //   the type of the last posture transition and of the first interaction run inside Run (RunInteraction of another instance)
    // A Sim that sits in a vehicle or is inside a rabbit hole when its Run begins is not standing idle: the record ends there
    // as "ride" / "hidden" (queue, wake and prerun kept, no preroute).
    public sealed class ActDelayCore
    {
        public const int kQueue = 0, kWake = 1, kPreRun = 2, kPreRoute = 3, kPlanCpu = 4, kPlanJob = 5, kRetry = 6, kPostPlan = 7, kBuckets = 8;
        public const int kTotal = 8, kVisible = 9, kPick = 10, kOutfit = 11, kSync = 12, kStats = 13;
        public const int kSelected = 0, kHousehold = 1, kNpc = 2, kKinds = 3;   // row kKinds = every Sim
        public const int kHowMove = 0, kHowAnim = 1, kHowEnd = 2, kHowRide = 3, kHowHidden = 4;
        public const int kAwayNone = 0, kAwayRide = 1, kAwayHidden = 2;   // where the Sim is when its Run begins
        public static readonly string[] StatNames = { "queue", "wake", "prerun", "preroute", "plan-cpu", "plan-job", "retry", "postplan", "total", "visible", "pick", "outfit", "sync" };
        public static readonly string[] KindNames = { "selected", "household", "npc", "all" };
        public static readonly string[] HowNames = { "move", "anim", "end", "ride", "hidden" };

        public const int kMaxOpen = 1024, kMaxSims = 1024, kMaxTypes = 512, kRing = 16, kBins = 120;
        public const long kStaleUs = 30L * 60 * 1000000;     // an open record this old is dropped (also bounds what a lost removal keeps alive)
        public const long kAnimSettleUs = 30L * 1000000;     // set up on a state machine this long with no route: the animation was the act
        public const int kAnimLeadTicks = 4;                 // set up at least this many ticks before the first plan: the animation came first

        public delegate string Namer(object typeKey);

        // Entry counts of the probe's hooks (counted whether the switch is on or not).
        public const int kInQueued = 0, kInRemoved = 1, kInPush = 2, kInPop = 3, kInPlanBegin = 4, kInPlanEnd = 5, kInJobWait = 6, kInMove = 7, kInSetActor = 8,
            kInFgl = 9, kInOutfit = 10, kInSyncWait = 11, kInStartSync = 12, kInReceive = 13, kHooks = 14;
        public static readonly string[] HookNames = { "OnAddedToQueue", "OnRemovedFromQueue", "PushRunningInteraction", "PopRunningInteraction", "plan begin",
            "plan end", "AsyncWait", "OnAboutToStartFollowingRoute", "SetActor", "FindGoodLocationNearby", "SwitchOutfitHelper.Wait",
            "WaitForSynchronizationLevelWithSim", "StartSync", "ReceiveSocialInteraction" };
        public const int kHealthMin = 50;

        public static string HookLine(int[] n)
        {
            StringBuilder b = new StringBuilder(380);
            b.Append("hooks entered:");
            for (int i = 0; i < kHooks; i++) b.Append(i == 0 ? " " : ", ").Append(HookNames[i]).Append(' ').Append(n[i]);
            return b.ToString();
        }

        // "" while every hook that must run with the others has run. Each pair is entered by the same game code path, so one
        // side at zero with the other past kHealthMin is a hook that is not reached (its swap missing, or a hook stacked over
        // it that answers without calling its original); with OnAddedToQueue dead no record is ever made and nothing is counted.
        public static string HookHealth(int[] n)
        {
            string bad = "";
            bad = Dead(bad, n, kInQueued, kInPush);        // every run interaction was queued (or pushed straight in: far fewer)
            bad = Dead(bad, n, kInRemoved, kInPop);        // every head that ran is removed
            bad = Dead(bad, n, kInPush, kInQueued);
            bad = Dead(bad, n, kInPop, kInPush);
            bad = Dead(bad, n, kInPlanEnd, kInPlanBegin);
            bad = Dead(bad, n, kInPlanBegin, kInMove);     // a followed route was planned
            bad = Dead(bad, n, kInMove, kInPlanEnd);
            bad = Dead(bad, n, kInSetActor, kInPush);
            return bad.Length == 0 ? "" : "DEAD HOOK " + bad + " (not reached: swap missing, or a hook stacked over it does not call its original)";
        }
        static string Dead(string bad, int[] n, int hook, int witness)
        {
            if (n[hook] != 0 || n[witness] < kHealthMin || bad.Contains(HookNames[hook])) return bad;
            return bad + (bad.Length == 0 ? "" : ", ") + HookNames[hook] + " 0 with " + HookNames[witness] + " " + n[witness];
        }

        public sealed class Rec
        {
            public object Key, Type;
            public ulong Sim;
            public int Kind;
            public bool Auto;
            public long TQ, THead, TWake, TRun, TPlan1, TPlanEnd, TAnim, TVis;
            public long KQ, KHead, KWake, KRun, KPlan1, KAnim;
            public long PlanUs, JobUs, TrUs, FglUs, PlanT0, TrT0, OutfitUs, SyncUs, SyncT0, SyncAtAnim;
            public int Plans, Fails, Async, TrN, TrPlans, FglN, Depth, OutfitN, SyncN, SyncDepth, NestN;
            public object TrType, NestType;
            public void Clear()
            {
                Key = null; Type = null; Sim = 0; Kind = 0; Auto = false; TrType = null; NestType = null;
                OutfitUs = SyncUs = SyncT0 = SyncAtAnim = 0; OutfitN = SyncN = SyncDepth = NestN = 0;
                TQ = THead = TWake = TRun = TPlan1 = TPlanEnd = TAnim = TVis = 0;
                KQ = KHead = KWake = KRun = KPlan1 = KAnim = 0;
                PlanUs = JobUs = TrUs = FglUs = PlanT0 = TrT0 = 0;
                Plans = Fails = Async = TrN = TrPlans = FglN = Depth = 0;
            }
        }

        // One finished instance. The same object is handed out every time: read it before the next call.
        public sealed class Result
        {
            public readonly long[] Us = new long[kStats];
            public object Key, Type, TrType, NestType;
            public ulong Sim;
            public int Kind, How, Plans, Fails, Async, TrN, TrPlans, FglN, OutfitN, SyncN, NestN;
            public bool Auto, Routed, NoAct, Over;
            public long TrUs, FglUs, OutfitUs, SyncUs, TicksBeforeRun, TicksInRun;
            public int Ms(int stat) { return (int)Math.Min(int.MaxValue, Us[stat] / 1000); }
        }

        sealed class SimSlot { public int Cur = -1; public long THeadFree, KHeadFree, IdleFrom; }

        sealed class TypeAgg
        {
            public object Key;
            public int N, Over, NoAct, MaxMs;
            public long SumMs, OutfitMs, SyncMs;
            public readonly long[] Bucket = new long[kBuckets];
        }

        readonly Rec[] mRecs = new Rec[kMaxOpen];
        readonly int[] mFree = new int[kMaxOpen];
        int mFreeN, mSweep;
        readonly Dictionary<object, int> mOpen = new Dictionary<object, int>(kMaxOpen);
        readonly Dictionary<ulong, SimSlot> mSims = new Dictionary<ulong, SimSlot>(256);
        readonly Dictionary<object, TypeAgg> mTypes = new Dictionary<object, TypeAgg>(256);
        readonly Result mResult = new Result();

        // [kind][stat]: histogram, count, sum, max (ms)
        readonly int[][][] mHist = new int[kKinds + 1][][];
        readonly int[][] mN = new int[kKinds + 1][], mMax = new int[kKinds + 1][];
        readonly long[][] mSum = new long[kKinds + 1][];

        public int LogMs = 500;
        public int Acted, Over, NoAct, Cancelled, Dropped, Stale, PlansSeen, MaxMs;
        public int Rides, Hidden, OutfitWaits, OutfitPlayer, OutfitMaxMs, SyncWaits;   // totals of the overlays (docs/684 §8)
        public long SumMs, OutfitSumMs, SyncSumMs;
        public readonly int[] ActedByKind = new int[kKinds];
        public readonly string[] Ring = new string[kRing];
        public int RingN;

        // the minute in progress
        readonly int[] mMinByKind = new int[kKinds];
        readonly long[] mMinBucket = new long[kBuckets];
        readonly int[] mMinHist = new int[kBins];
        int mMinN, mMinOver, mMinNoAct, mMinCancelled, mMinMax;
        object mMinWorstType;

        public ActDelayCore()
        {
            for (int i = 0; i < kMaxOpen; i++) { mRecs[i] = new Rec(); mFree[i] = kMaxOpen - 1 - i; }
            mFreeN = kMaxOpen;
            for (int k = 0; k <= kKinds; k++)
            {
                mHist[k] = new int[kStats][];
                for (int s = 0; s < kStats; s++) mHist[k][s] = new int[kBins];
                mN[k] = new int[kStats]; mMax[k] = new int[kStats]; mSum[k] = new long[kStats];
            }
        }

        public int OpenCount { get { return mOpen.Count; } }

        // ---- histogram: exact below 4 ms, then four bins an octave ----
        public static int Bin(int ms)
        {
            if (ms < 4) return ms < 0 ? 0 : ms;
            int hb = 2;
            while (hb < 30 && (ms >> (hb + 1)) != 0) hb++;
            return 4 + (hb - 2) * 4 + ((ms >> (hb - 2)) & 3);
        }
        public static int BinValue(int bin)
        {
            if (bin < 4) return bin;
            int hb = (bin - 4) / 4 + 2, sub = (bin - 4) % 4;
            return ((4 + sub) << (hb - 2)) + ((1 << (hb - 2)) >> 1);
        }
        public static int Percentile(int[] hist, int n, int p)
        {
            if (n <= 0) return 0;
            int want = (n * p + 99) / 100, seen = 0;
            if (want < 1) want = 1;
            for (int b = 0; b < hist.Length; b++) { seen += hist[b]; if (seen >= want) return BinValue(b); }
            return BinValue(hist.Length - 1);
        }

        void AddStat(int kind, int stat, int ms)
        {
            for (int k = kind; ; k = kKinds)
            {
                mHist[k][stat][Bin(ms)]++;
                mN[k][stat]++;
                mSum[k][stat] += ms;
                if (ms > mMax[k][stat]) mMax[k][stat] = ms;
                if (k == kKinds) break;
            }
        }
        public int StatN(int kind, int stat) { return mN[kind][stat]; }
        public int StatMax(int kind, int stat) { return mMax[kind][stat]; }
        public int StatP(int kind, int stat, int p) { return Percentile(mHist[kind][stat], mN[kind][stat], p); }

        // ---- records ----
        int Find(object key)
        {
            int i;
            return key != null && mOpen.TryGetValue(key, out i) ? i : -1;
        }

        SimSlot Slot(ulong sim, bool create)
        {
            SimSlot s;
            if (mSims.TryGetValue(sim, out s)) return s;
            if (!create) return null;
            if (mSims.Count >= kMaxSims) mSims.Clear();
            s = new SimSlot();
            mSims[sim] = s;
            return s;
        }

        Rec Cur(ulong sim)
        {
            SimSlot s;
            if (!mSims.TryGetValue(sim, out s) || s.Cur < 0) return null;
            Rec r = mRecs[s.Cur];
            if (r.Key == null || r.Sim != sim) { s.Cur = -1; return null; }
            return r;
        }

        void FreeRec(int i)
        {
            Rec r = mRecs[i];
            if (r.Key == null) return;
            SimSlot s;
            if (mSims.TryGetValue(r.Sim, out s) && s.Cur == i) s.Cur = -1;
            mOpen.Remove(r.Key);
            r.Clear();
            mFree[mFreeN++] = i;
        }

        // A new interaction is in the queue. isHead: nothing is in front of it; wasEmpty: the queue held nothing else.
        public void Queued(object key, object type, ulong sim, int kind, bool auto, bool isHead, bool wasEmpty, long now, long ticks)
        {
            if (key == null) return;
            int i = Find(key);
            if (i >= 0) FreeRec(i);
            SimSlot s = Slot(sim, true);
            if (wasEmpty)
            {
                // the gap a Sim stood with an empty queue before it picked something itself
                if (auto && s.IdleFrom != 0 && now > s.IdleFrom) AddStat(kind, kPick, (int)Math.Min(int.MaxValue, (now - s.IdleFrom) / 1000));
                s.IdleFrom = 0;
            }
            if (mFreeN == 0) { Dropped++; return; }
            i = mFree[--mFreeN];
            Rec r = mRecs[i];
            r.Key = key; r.Type = type; r.Sim = sim; r.Kind = kind < 0 || kind >= kKinds ? kHousehold : kind; r.Auto = auto;
            r.TQ = now; r.KQ = ticks;
            if (isHead) { r.THead = now; r.KHead = ticks; }
            mOpen[key] = i;
        }

        // The instance left the queue (done, cancelled, replaced). A record that ran and never acted is finished here.
        public Result Removed(object key, ulong sim, bool isHead, bool emptyAfter, long now, long ticks)
        {
            Result res = null;
            int i = Find(key);
            if (i >= 0)
            {
                Rec r = mRecs[i];
                if (r.TRun != 0) res = FinishEnd(i, now, ticks);
                else { Cancelled++; mMinCancelled++; FreeRec(i); }
            }
            if (isHead)
            {
                SimSlot s = Slot(sim, true);
                s.THeadFree = now; s.KHeadFree = ticks;
                if (emptyAfter) s.IdleFrom = now;
            }
            return res;
        }

        // PushRunningInteraction: key (of class keyType) runs; head is the queue's head, transition says key is the queue's
        // posture transition; away = where the Sim is (kAway*). A head whose Run begins with the Sim in a vehicle or inside
        // a rabbit hole is finished here (the result), since what follows is a ride or a stay, not a Sim standing idle.
        public Result Push(object key, object head, ulong sim, bool transition, long now, long ticks, object keyType, int away)
        {
            int h = Find(head);
            if (h < 0) return null;
            Rec r = mRecs[h];
            SimSlot s = Slot(sim, true);
            if (r.TWake == 0)
            {
                r.TWake = now; r.KWake = ticks;
                if (r.THead == 0)
                {
                    r.THead = s.THeadFree > r.TQ ? s.THeadFree : r.TQ;
                    r.KHead = s.KHeadFree > r.KQ ? s.KHeadFree : r.KQ;
                }
            }
            s.Cur = h;
            if (ReferenceEquals(key, head))
            {
                if (r.TRun != 0) return null;
                r.TRun = now; r.KRun = ticks;
                if (away == kAwayRide) { Rides++; return Finish(h, now, ticks, kHowRide); }
                if (away == kAwayHidden) { Hidden++; return Finish(h, now, ticks, kHowHidden); }
            }
            else if (r.TRun != 0) { if (r.NestN++ == 0) r.NestType = keyType; }
            else if (transition) { r.TrN++; r.TrT0 = now; r.TrType = keyType; }
            return null;
        }

        // PopRunningInteraction: the head's Run is over (finished here when it never acted), or a transition ended.
        public Result Pop(object key, ulong sim, bool transition, long now, long ticks)
        {
            int i = Find(key);
            if (i >= 0) return mRecs[i].TRun != 0 ? FinishEnd(i, now, ticks) : null;
            if (!transition) return null;
            Rec r = Cur(sim);
            if (r != null && r.TrT0 != 0) { r.TrUs += now - r.TrT0; r.TrT0 = 0; }
            return null;
        }

        // A route plan begins / ends in the Sim's own task (nested plans count once).
        public void PlanBegin(ulong sim, long now, long ticks)
        {
            Rec r = Cur(sim);
            if (r == null) return;
            if (r.Depth++ > 0) return;
            if (r.SyncT0 != 0) { r.SyncUs += now - r.SyncT0; r.SyncT0 = 0; }   // a plan inside a sync wait is plan time
            r.PlanT0 = now;
            if (r.TRun != 0 && r.TPlan1 == 0) { r.TPlan1 = now; r.KPlan1 = ticks; }
        }
        public void PlanEnd(ulong sim, bool ok, long now)
        {
            Rec r = Cur(sim);
            if (r == null || r.Depth <= 0) return;
            if (--r.Depth > 0) return;
            if (r.SyncDepth > 0) r.SyncT0 = now;
            if (r.TRun == 0 || r.TPlan1 == 0) { r.TrPlans++; return; }   // a transition's plan: part of prerun
            r.Plans++; PlansSeen++;
            if (!ok) r.Fails++;
            r.PlanUs += now - r.PlanT0;
            r.TPlanEnd = now;
        }
        // the Sim's task slept us waiting for a route job, inside the plan now open
        public void JobWaited(ulong sim, long us)
        {
            Rec r = Cur(sim);
            if (r == null || r.Depth <= 0 || r.TRun == 0 || us < 0) return;
            r.JobUs += us; r.Async++;
        }
        // a placement search (FindGoodLocationNearby) took us in the Sim's task
        public void Fgl(ulong sim, long us)
        {
            Rec r = Cur(sim);
            if (r == null || r.TRun == 0 || us < 0) return;
            r.FglUs += us; r.FglN++;
        }
        // The Sim's task left Sim.SwitchOutfitHelper.Wait after us (the native outfit build, docs/684 §8). Every wait that
        // slept is counted by Sim kind, with or without an open record; the Sim's open record keeps its share.
        public void OutfitWait(ulong sim, int kind, long us)
        {
            if (us < 1000) return;   // already built (or no change): the task did not sleep
            int ms = (int)Math.Min(int.MaxValue, us / 1000);
            if (kind < 0 || kind >= kKinds) kind = kHousehold;
            AddStat(kind, kOutfit, ms);
            OutfitWaits++; OutfitSumMs += ms;
            if (kind != kNpc) OutfitPlayer++;
            if (ms > OutfitMaxMs) OutfitMaxMs = ms;
            Rec r = Cur(sim);
            if (r != null) { r.OutfitUs += us; r.OutfitN++; }
        }
        // A sync wait of the Sim begins / ends (nested waits count once; time inside a route plan is left to the plan).
        public void SyncBegin(ulong sim, long now)
        {
            Rec r = Cur(sim);
            if (r != null && r.SyncDepth++ == 0 && r.Depth == 0) r.SyncT0 = now;
        }
        public void SyncEnd(ulong sim, long now)
        {
            Rec r = Cur(sim);
            if (r == null || r.SyncDepth <= 0 || --r.SyncDepth > 0) return;
            if (r.SyncT0 != 0 && now > r.SyncT0) { r.SyncUs += now - r.SyncT0; r.SyncN++; }
            r.SyncT0 = 0;
        }
        // the Sim was set as an actor of an animation state machine
        public void Anim(ulong sim, long now, long ticks)
        {
            Rec r = Cur(sim);
            if (r == null) return;
            if (r.TRun == 0) { if (r.TrT0 != 0 && r.TVis == 0) r.TVis = now; return; }
            if (r.TAnim != 0) return;
            r.TAnim = now; r.KAnim = ticks;
            r.SyncAtAnim = r.SyncUs + (r.SyncT0 != 0 && now > r.SyncT0 ? now - r.SyncT0 : 0);   // sync time up to here, should this be the act
        }
        // the Sim is about to follow a route
        public Result Move(ulong sim, long now, long ticks)
        {
            SimSlot s;
            if (!mSims.TryGetValue(sim, out s) || s.Cur < 0) return null;
            Rec r = Cur(sim);
            if (r == null) return null;
            if (r.TVis == 0) r.TVis = now;
            if (r.TRun == 0) return null;   // a posture transition's route
            if (AnimFirst(r)) return Finish(s.Cur, r.TAnim, r.KAnim, kHowAnim);
            return Finish(s.Cur, now, ticks, kHowMove);
        }

        public static bool AnimFirst(Rec r)
        {
            return r.TAnim != 0 && (r.TPlan1 == 0 || r.TAnim < r.TPlan1 && r.KPlan1 - r.KAnim >= kAnimLeadTicks);
        }

        Result FinishEnd(int i, long now, long ticks)
        {
            Rec r = mRecs[i];
            if (r.TAnim != 0) return Finish(i, r.TAnim, r.KAnim, kHowAnim);
            return Finish(i, now, ticks, kHowEnd);
        }

        // Once a minute (and from the check): finishes records whose animation set-up stood long enough, drops stale ones.
        // Returns one finished record a call, null when the pass is over.
        public Result SweepNext(long now)
        {
            while (mSweep < kMaxOpen)
            {
                int i = mSweep++;
                Rec r = mRecs[i];
                if (r.Key == null) continue;
                if (r.TRun != 0 && r.TAnim != 0 && now - r.TAnim > kAnimSettleUs) return Finish(i, r.TAnim, r.KAnim, kHowAnim);
                if (now - r.TQ > kStaleUs) { Stale++; FreeRec(i); }
            }
            mSweep = 0;
            return null;
        }

        // World load, or the switch went off: open records and Sims go, the totals stay.
        public void ClearOpen()
        {
            for (int i = 0; i < kMaxOpen; i++) if (mRecs[i].Key != null) FreeRec(i);
            mSims.Clear();
            mSweep = 0;
        }

        public void Reset()
        {
            ClearOpen();
            mTypes.Clear();
            for (int k = 0; k <= kKinds; k++)
                for (int s = 0; s < kStats; s++) { Array.Clear(mHist[k][s], 0, kBins); mN[k][s] = 0; mMax[k][s] = 0; mSum[k][s] = 0; }
            Acted = Over = NoAct = Cancelled = Dropped = Stale = PlansSeen = MaxMs = 0; SumMs = 0;
            Rides = Hidden = OutfitWaits = OutfitPlayer = OutfitMaxMs = SyncWaits = 0; OutfitSumMs = SyncSumMs = 0;
            Array.Clear(ActedByKind, 0, kKinds);
            Array.Clear(Ring, 0, kRing); RingN = 0;
            MinuteClear();
        }

        // The split of one record. us[] gets the eight buckets (they add up to us[kTotal]) and kVisible.
        public static bool Split(Rec r, long act, long[] us)
        {
            long tq = r.TQ;
            long head = r.THead != 0 ? r.THead : tq; if (head < tq) head = tq;
            long wake = r.TWake != 0 ? r.TWake : head; if (wake < head) wake = head;
            long run = r.TRun != 0 ? r.TRun : wake; if (run < wake) run = wake;
            if (act < run) act = run;
            for (int i = 0; i < kStats; i++) us[i] = 0;
            us[kQueue] = head - tq; us[kWake] = wake - head; us[kPreRun] = run - wake;
            bool routed = r.Plans > 0 && r.TPlan1 != 0 && r.TPlan1 <= act;
            if (routed)
            {
                long p1 = r.TPlan1 < run ? run : r.TPlan1;
                long pend = r.TPlanEnd > act ? act : r.TPlanEnd; if (pend < p1) pend = p1;
                long span = pend - p1;
                long plan = r.PlanUs > span ? span : r.PlanUs;
                long job = r.JobUs > plan ? plan : r.JobUs;
                us[kPreRoute] = p1 - run; us[kPlanJob] = job; us[kPlanCpu] = plan - job; us[kRetry] = span - plan; us[kPostPlan] = act - pend;
            }
            else us[kPreRoute] = act - run;
            us[kTotal] = act - tq;
            long vis = r.TVis != 0 && r.TVis < act ? r.TVis : act;
            us[kVisible] = (vis < tq ? tq : vis) - tq;
            return routed;
        }

        Result Finish(int i, long act, long actTicks, int how)
        {
            Rec r = mRecs[i];
            Result res = mResult;
            res.Routed = Split(r, act, res.Us);
            res.Key = r.Key; res.Type = r.Type; res.Sim = r.Sim; res.Kind = r.Kind; res.How = how; res.Auto = r.Auto;
            res.Plans = r.Plans; res.Fails = r.Fails; res.Async = r.Async; res.TrN = r.TrN; res.TrPlans = r.TrPlans; res.FglN = r.FglN;
            res.TrUs = r.TrUs; res.FglUs = r.FglUs; res.TrType = r.TrType; res.NestType = r.NestType; res.NestN = r.NestN;
            // a sync wait still open when the Sim acts (the route inside ReceiveSocialInteraction) ends at the act
            if (how == kHowAnim) { if (r.SyncAtAnim > r.SyncUs) r.SyncN++; r.SyncUs = r.SyncAtAnim; }   // waits after the act are not delay
            else if (r.SyncT0 != 0 && act > r.SyncT0) { r.SyncUs += act - r.SyncT0; r.SyncN++; }
            r.SyncT0 = 0;
            res.OutfitUs = r.OutfitUs; res.OutfitN = r.OutfitN; res.SyncUs = r.SyncUs; res.SyncN = r.SyncN;
            res.TicksBeforeRun = r.KRun > r.KQ ? r.KRun - r.KQ : 0;
            res.TicksInRun = actTicks > r.KRun && r.KRun != 0 ? actTicks - r.KRun : 0;
            res.NoAct = how == kHowEnd;
            int total = res.Ms(kTotal);
            TypeAgg t = TypeOf(r.Type);
            if (res.NoAct)
            {
                // ended with neither a route followed nor an animation: counted, kept out of the percentiles; worth a
                // line only when it asked for a route (the "stood there, then gave up" shape)
                NoAct++; mMinNoAct++;
                if (t != null) t.NoAct++;
                res.Over = total >= LogMs && r.Plans > 0;
            }
            else
            {
                Acted++; ActedByKind[r.Kind]++; SumMs += total;
                if (total > MaxMs) MaxMs = total;
                for (int s = 0; s <= kVisible; s++) AddStat(r.Kind, s, res.Ms(s));
                if (r.SyncUs >= 1000) { int sy = (int)Math.Min(int.MaxValue, r.SyncUs / 1000); AddStat(r.Kind, kSync, sy); SyncWaits++; SyncSumMs += sy; }
                res.Over = total >= LogMs;
                if (res.Over) Over++;
                if (t != null)
                {
                    t.N++; t.SumMs += total;
                    if (res.Over) t.Over++;
                    if (total > t.MaxMs) t.MaxMs = total;
                    for (int b = 0; b < kBuckets; b++) t.Bucket[b] += res.Ms(b);
                    t.OutfitMs += r.OutfitUs / 1000; t.SyncMs += r.SyncUs / 1000;
                }
                mMinN++; mMinByKind[r.Kind]++; mMinHist[Bin(total)]++;
                if (res.Over) mMinOver++;
                if (total > mMinMax) { mMinMax = total; mMinWorstType = r.Type; }
                for (int b = 0; b < kBuckets; b++) mMinBucket[b] += res.Ms(b);
            }
            FreeRec(i);
            return res;
        }

        TypeAgg TypeOf(object type)
        {
            if (type == null) return null;
            TypeAgg t;
            if (mTypes.TryGetValue(type, out t)) return t;
            if (mTypes.Count >= kMaxTypes) return null;
            t = new TypeAgg();
            t.Key = type;
            mTypes[type] = t;
            return t;
        }

        // ---- text ----
        // One instance over the threshold.
        public static string Line(Result r, string type, string sim, string when) { return Line(r, type, sim, when, null); }
        public static string Line(Result r, string type, string sim, string when, Namer name)
        {
            StringBuilder b = new StringBuilder(420);
            b.Append("actdelay: ").Append(r.Ms(kTotal)).Append(" ms queued -> ").Append(r.NoAct ? "ended without acting" : HowNames[r.How])
                .Append(", ").Append(KindNames[r.Kind]).Append(r.Auto ? " auto " : " user ").Append(type).Append(": ");
            for (int i = 0; i < kBuckets; i++)
            {
                if (i == kPlanCpu && !r.Routed) { b.Append(", no plan"); i = kPostPlan; continue; }
                if (i > 0) b.Append(", ");
                b.Append(StatNames[i]).Append(' ').Append(r.Ms(i));
            }
            if (r.Routed) b.Append(" (").Append(r.Plans).Append(" plans, ").Append(r.Fails).Append(" failed, ").Append(r.Async).Append(" job waits)");
            b.Append("; first visible ").Append(r.Ms(kVisible));
            if (r.TrN > 0)
            {
                b.Append(", transitions ").Append(r.TrN).Append(" (").Append(r.TrUs / 1000).Append(" ms, ").Append(r.TrPlans).Append(" plans");
                if (r.TrType != null) b.Append(", last ").Append(TypeText(r.TrType, name));
                b.Append(')');
            }
            if (r.NestN > 0) b.Append(", ran inside ").Append(r.NestN).Append(" (first ").Append(TypeText(r.NestType, name)).Append(')');
            if (r.OutfitN > 0) b.Append(", outfit waits ").Append(r.OutfitN).Append(" (").Append(r.OutfitUs / 1000).Append(" ms)");
            if (r.SyncN > 0 && r.SyncUs >= 1000) b.Append(", sync waits ").Append(r.SyncN).Append(" (").Append(r.SyncUs / 1000).Append(" ms)");
            if (r.FglN > 0) b.Append(", placement searches ").Append(r.FglN).Append(" (").Append(r.FglUs / 1000).Append(" ms)");
            b.Append(", ticks ").Append(r.TicksBeforeRun).Append(" before Run + ").Append(r.TicksInRun).Append(" in Run");
            // the native log line holds 512 bytes: the time and the name go last and only while they fit
            if (when != null && b.Length + when.Length + 2 <= kLineMax) b.Append(", ").Append(when);
            if (sim != null && b.Length + sim.Length + 6 <= kLineMax) b.Append(", Sim ").Append(sim);
            return b.ToString();
        }

        public const int kLineMax = 470;
        // the class without its namespace ("Sit+StandUp"), at most 48 characters
        static string TypeText(object type, Namer name)
        {
            if (type == null) return "?";
            string n = name != null ? name(type) : type.ToString();
            if (n == null) return "?";
            int dot = n.LastIndexOf('.');
            if (dot >= 0 && dot + 1 < n.Length) n = n.Substring(dot + 1);
            return n.Length > 48 ? n.Substring(0, 48) : n;
        }

        public void RingAdd(string line)
        {
            Ring[RingN++ % kRing] = line;
        }

        void MinuteClear()
        {
            Array.Clear(mMinByKind, 0, kKinds); Array.Clear(mMinBucket, 0, kBuckets); Array.Clear(mMinHist, 0, kBins);
            mMinN = mMinOver = mMinNoAct = mMinCancelled = mMinMax = 0;
            mMinWorstType = null;
        }

        // The summary of the minute in progress; starts the next one. Null when nothing happened.
        public string MinuteLine(Namer name)
        {
            if (mMinN == 0 && mMinNoAct == 0 && mMinCancelled == 0) return null;
            StringBuilder b = new StringBuilder(320);
            b.Append("actdelay minute: ").Append(mMinN).Append(" acted (selected ").Append(mMinByKind[kSelected]).Append(", household ").Append(mMinByKind[kHousehold])
                .Append(", npc ").Append(mMinByKind[kNpc]).Append("), ").Append(mMinOver).Append(" over ").Append(LogMs).Append(" ms, queued -> act p50 ")
                .Append(Percentile(mMinHist, mMinN, 50)).Append(" p90 ").Append(Percentile(mMinHist, mMinN, 90)).Append(" max ").Append(mMinMax).Append(" ms; mean ms");
            for (int i = 0; i < kBuckets; i++) b.Append(' ').Append(StatNames[i]).Append(' ').Append(mMinN > 0 ? mMinBucket[i] / mMinN : 0);
            if (mMinWorstType != null) b.Append("; slowest ").Append(name != null ? name(mMinWorstType) : mMinWorstType.ToString());
            b.Append("; ended without acting ").Append(mMinNoAct).Append(", cancelled before Run ").Append(mMinCancelled).Append(", open ").Append(mOpen.Count)
                .Append(", dropped ").Append(Dropped + Stale);
            MinuteClear();
            return b.ToString();
        }

        void StatRow(StringBuilder b, int kind, int stat)
        {
            int n = mN[kind][stat];
            b.Append(' ').Append(StatNames[stat]).Append(' ').Append(StatP(kind, stat, 50)).Append('/').Append(StatP(kind, stat, 90)).Append('/').Append(mMax[kind][stat]);
            if (stat >= kPick) b.Append(" (n=").Append(n).Append(')');   // pick, outfit, sync: their own counts
        }

        // The aggregate since the start (or the last reset): p50/p90/max ms per bucket and Sim kind, the top interaction types
        // by summed queued -> act ms, the last lines over the threshold.
        public string Report(Namer name, int top)
        {
            StringBuilder b = new StringBuilder(4096);
            b.Append("actdelay: ").Append(Acted).Append(" acted (selected ").Append(ActedByKind[kSelected]).Append(", household ").Append(ActedByKind[kHousehold])
                .Append(", npc ").Append(ActedByKind[kNpc]).Append("), over ").Append(LogMs).Append(" ms: ").Append(Over).Append(", ended without acting: ").Append(NoAct)
                .Append(", cancelled before Run: ").Append(Cancelled).Append(", dropped: ").Append(Dropped).Append(" table full + ").Append(Stale).Append(" stale, open: ")
                .Append(mOpen.Count).Append(", plans in records: ").Append(PlansSeen).Append('\n');
            b.Append("not idle at Run start (kept out of preroute): riding ").Append(Rides).Append(", in a rabbit hole ").Append(Hidden)
                .Append("; outfit waits that slept: ").Append(OutfitWaits).Append(" (household or selected ").Append(OutfitPlayer).Append("), ").Append(OutfitSumMs)
                .Append(" ms in all, longest ").Append(OutfitMaxMs).Append("; instances with a sync wait: ").Append(SyncWaits).Append(", ").Append(SyncSumMs).Append(" ms in all\n");
            b.Append("p50/p90/max ms per bucket (queued -> act):\n");
            for (int k = kKinds; k >= 0; k = k == kKinds ? 0 : k + 1)
            {
                b.Append("  ").Append(KindNames[k]).Append(" (n=").Append(mN[k][kTotal]).Append("):");
                for (int s = 0; s < kStats; s++) StatRow(b, k, s);
                b.Append('\n');
                if (k == kKinds - 1) break;
            }
            List<TypeAgg> list = new List<TypeAgg>(mTypes.Values);
            list.Sort(delegate(TypeAgg x, TypeAgg y) { return y.SumMs.CompareTo(x.SumMs); });
            b.Append("top interaction types by summed queued -> act ms (n, sum, mean, max, over; mean ms per bucket):\n");
            for (int i = 0; i < list.Count && i < top; i++)
            {
                TypeAgg t = list[i];
                if (t.N == 0) break;
                b.Append("  ").Append(i + 1).Append(". ").Append(name != null ? name(t.Key) : t.Key.ToString()).Append(": n=").Append(t.N).Append(" sum=").Append(t.SumMs)
                    .Append(" mean=").Append(t.SumMs / t.N).Append(" max=").Append(t.MaxMs).Append(" over=").Append(t.Over).Append(" noact=").Append(t.NoAct).Append(';');
                for (int s = 0; s < kBuckets; s++) b.Append(' ').Append(StatNames[s]).Append(' ').Append(t.Bucket[s] / t.N);
                if (t.OutfitMs > 0 || t.SyncMs > 0) b.Append("; of that outfit ").Append(t.OutfitMs / t.N).Append(" sync ").Append(t.SyncMs / t.N);
                b.Append('\n');
            }
            int lines = RingN < kRing ? RingN : kRing;
            if (lines > 0) b.Append("last lines over the threshold:\n");
            for (int i = 0; i < lines; i++) b.Append("  ").Append(Ring[(RingN - lines + i) % kRing]).Append('\n');
            return b.ToString();
        }

        // A fixed scenario through a fresh tracker (also run in the game by ActDelayCheck("run")): "" when every bucket is
        // as built, else what differs.
        public static string Model()
        {
            ActDelayCore c = new ActDelayCore();
            object a = new object(), b = new object(), tr = new object(), type = "Model";
            const ulong sim = 7;
            long ms = 1000;
            // a runs; b is queued behind it at 100 ms; a ends at 400; the loop wakes at 650 with a transition (650-900);
            // Run at 900; first plan 950-1250 (failed, 200 ms asleep on a job); second plan 1400-1500; steps at 1600
            c.Queued(a, type, sim, kNpc, true, true, true, 1 * ms, 10);
            c.Push(a, a, sim, false, 2 * ms, 10, type, kAwayNone);
            c.Queued(b, type, sim, kNpc, false, false, false, 100 * ms, 12);
            if (c.Move(sim, 50 * ms, 11) == null) return "a did not finish at its first step";
            c.Pop(a, sim, false, 400 * ms, 20);
            c.Removed(a, sim, true, false, 400 * ms, 20);
            c.Push(tr, b, sim, true, 650 * ms, 30, type, kAwayNone);
            c.PlanBegin(sim, 700 * ms, 31); c.PlanEnd(sim, true, 720 * ms);
            c.Pop(tr, sim, true, 900 * ms, 40);
            c.Push(b, b, sim, false, 900 * ms, 40, type, kAwayNone);
            c.PlanBegin(sim, 950 * ms, 41); c.JobWaited(sim, 200 * ms); c.PlanEnd(sim, false, 1250 * ms);
            c.Fgl(sim, 60 * ms);
            c.PlanBegin(sim, 1400 * ms, 60); c.PlanEnd(sim, true, 1500 * ms);
            Result r = c.Move(sim, 1600 * ms, 70);
            if (r == null) return "b did not finish at its first step";
            int[] want = { 300, 250, 250, 50, 200, 200, 150, 100 };
            long sum = 0;
            for (int i = 0; i < kBuckets; i++)
            {
                if (r.Ms(i) != want[i]) return StatNames[i] + " " + r.Ms(i) + " not " + want[i];
                sum += r.Us[i];
            }
            if (sum != r.Us[kTotal] || r.Ms(kTotal) != 1500) return "total " + r.Ms(kTotal) + ", buckets add up to " + sum / 1000;
            if (r.Plans != 2 || r.Fails != 1 || r.Async != 1 || r.TrN != 1 || r.TrPlans != 1 || r.FglN != 1 || !r.Over || r.How != kHowMove)
                return "counts: plans " + r.Plans + " failed " + r.Fails + " job waits " + r.Async + " transitions " + r.TrN + "/" + r.TrPlans + " fgl " + r.FglN;
            if (c.OpenCount != 0 || c.Acted != 2 || c.Over != 1) return "open " + c.OpenCount + " acted " + c.Acted + " over " + c.Over;
            return "";
        }
    }
}
