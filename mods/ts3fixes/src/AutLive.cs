using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using Sims3.Gameplay;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Careers;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;
using AutonomyT = Sims3.Gameplay.Autonomy.Autonomy;

namespace Ts3Fixes
{
    // [AutShadow] go-live (docs/124 §16-§17): the game uses the worker's meta scores. ts3native [AutShadow] Mode=3 plus the
    // ts3fixes switches autshadow and autlive (both off by default; Mode 3 with autlive off = the game's search, no request).
    // Per manager meta search (B13 entry, the manager task):
    //   1. eligible (manager search, a yielding context, not the inspection window): request as Mode R (TryRequest), the
    //      actor fields kept for the resume compare, the meta objects and their lots watched for ObjStale; then
    //      Simulator.Sleep(0) until the result is ready (AutFinish copied it) or MaxWait ticks passed. After each yield the
    //      game's own YieldIfPossible returns: interrupted -> SearchInterrupted, actor destroyed -> ActorDestroyed.
    //   2. result checks (§3.1): run status, world epoch since the cut, game state, LotCurrent, head interaction, actor fields.
    //      Any miss: the game's B13 as today (counted by reason).
    //   3. selection: per object valid = allow-listed implementor, no worker abort, no AlwaysChooseBest ad, no event since the
    //      cut (ObjStale on the object and its lot); the worker's passing pairs of valid objects by score; band 1 = TopN.
    //   4. the game's B13 runs; the B14 hook replaces the body for valid objects in the request: per ad in MetaAds order, the
    //      game's commodity filter, then off-list ads and band-1 pairs through the game's C1 (live Test + score + BestScores);
    //      other objects, objects after a B13 yield (late) and objects not in the request run the game's B14.
    //   5. after B13 (still in the manager task, before D2): the §16.1 bands through C1 (AutLiveCore.Bands).
    // Dry=1 (G7): steps 1-3 as live, then the game's own B13 unchanged; its C1 calls are recorded and replayed with and
    // without what consume would hold back (AutLiveCore.Heap), compared with the game's queue: top-10 set and top 1.
    // Counters: ts3native ts3native_autshadow.al[] (LiveStat, slots kS* below). Records: autshadow.jsonl live-drop, dry-diff,
    // dry-sim-mis, dry-nocmp.
    public static partial class Fixes
    {
        // al[] slots (docs/124 §17.3, docs/registry.md autshadow v20)
        const int kSEligible = 0, kSPosted = 1, kSConsumed = 2, kSDryRuns = 3, kSNotPosted = 4, kSDropWait = 5, kSDropStopped = 6,
            kSDropEpochRun = 7, kSDropNoWorker = 8, kSDropExc = 9, kSDropEpoch = 10, kSDropLot = 11, kSDropActor = 12, kSDropState = 13,
            kSInterrupted = 14, kSDestroyed = 15, kSObjNotInReq = 16, kSObjInvalid = 17, kSObjStale = 18, kSObjIdent = 19, kSObjAcb = 20,
            kSObjLate = 21, kSObjConsumed = 22, kSPairsBand1 = 23, kSPairsBand = 24, kSPairsOffList = 25, kSBand2 = 26, kSBand3 = 27,
            kSBandFull = 28, kSWhyCount = 29, kSWhyMargin = 30, kSDrift0 = 31 /* 31-35: drift bins */, kSDryTop10 = 36, kSDryTop1 = 37,
            kSDrySimMis = 38, kSApplyUs = 39, kSWaitTicks = 40, kSVerifyOff = 41, kSCandidates = 42, kSDropHead = 43,
            kSDryMissStale = 44, kSDryMissBand = 45, kSGuardOff = 46, kSVerifyRuns = 47, kSVerifyTop1 = 48, kSVerifyTop10 = 49,
            kSPairsFilterDiff = 50, kSDropOther = 51, kSDryOffList = 52, kSDryMissOther = 53, kSC2Rehomed = 54, kSDryTop1Tie = 55;
        // Dry only: kSDryRuns searches whose compare never ran (game B13 returned 1/2 = interrupted/destroyed, or a compare
        // exception), and our predicted top-10 entries the game's queue does not hold (the set differs with no missed pair).
        // shortcut: they reuse acting-only slots (Consumed, PairsBand1: never counted with Dry=1) because al[] is fixed at 56 in
        // ts3native autshadow v20; give them own slots at the next autshadow layout bump.
        const int kSDryNoCmp = kSConsumed, kSDryExtra = kSPairsBand1;
        const int kLiveMaxLogPerReason = 20, kDryMaxLog = 50, kDryDiffFirst = 200, kDryDiffEvery = 16, kLogMaxChars = 1100;

        public sealed class LiveSearch
        {
            public bool InUse, Ready, Ok, Abandoned, Dry, Late, Active;  // Active: its game B13 or bands run (may be parked in a B13 yield)
            public int Id, LastRun, Objs, Pairs, CutEpoch, CutFrame, PostFrame, ReadyFrame, ResumeFrame;
            // docs/217 L1 (switch autlivelate, read at the resume): frame of the slice last checked, its verdict
            public bool LateOn, LateOk;
            public int Feat;  // per-search feature mask (AutLiveCore.Feat), set at the finish, late bit at a late consume
            public int SliceFrame;
            // docs/222 unseen-direct: Direct = this search uses the worker's ranking (actor unseen at post and resume); Snapped =
            // the actor was seen at a later B13 slice (rest exact; in Dry: a seen actor); UnseenDry = a Dry search whose actor was
            // unseen at the post; Cut0/Wait0 = native cut / GC-lock-wait words at the post (own cost per search); SavedPairs =
            // consumed allow-listed ads
            public bool Direct, Snapped, UnseenDry;
            public int Cut0, Wait0, SavedPairs;
            // docs/222 §9 (autunseencmp, Dry): compare on for this search, the actor was seen at a later slice (compare stops),
            // frame of the slice last checked, per object: the direct path would consume it at its B14 call
            public bool CmpOn, CmpSnapped;
            public int CmpSlice;
            public bool[] ObjCmp = new bool[64];
            public readonly AutLiveCore.Pace Pace = new AutLiveCore.Pace();  // docs/222 §8, per B13 slice of a direct search
            public AutonomyT Aut;
            public Sim Actor;
            public IMetaObject[] Obj = new IMetaObject[64];
            public int[] ObjImpl = new int[64], Start = new int[65], ObjWhy = new int[64];
            public ulong[] ObjKey = new ulong[64], LotKey = new ulong[64];
            public bool[] Visited = new bool[64], Consumed = new bool[64];
            public InteractionObjectPair[] Pair = new InteractionObjectPair[128];
            public int[] PairObj = new int[128], Def = new int[128], Code = new int[128], Buf = new int[128];
            public float[] W = new float[128], Live = new float[128];
            public bool[] Acb = new bool[128], LiveHas = new bool[128], Sent = new bool[128], Sel = new bool[128], Eligible = new bool[128];
            public readonly Dictionary<IMetaObject, int> ObjIndex = new Dictionary<IMetaObject, int>();
            public readonly Dictionary<InteractionObjectPair, int> PairIndex = new Dictionary<InteractionObjectPair, int>();
            public readonly AutLiveCore.Bands Bands = new AutLiveCore.Bands();
            // actor fields at the request (docs/124 §3.1, §13.6)
            public int AgeSpecies, JobCount;
            public Lot LotCur, LotHome;
            public object Household, Role, Occ, Job;
            public IMetaObject Career;
            public bool Selectable, Chip;
            // dry: the game's C1 calls during its B13 (pair index, or a negative id for pairs outside the request)
            public int EvN;
            public int[] EvId = new int[128];
            public float[] EvScore = new float[128];
            public bool[] EvSend = new bool[128];
            public readonly Dictionary<InteractionObjectPair, int> OtherId = new Dictionary<InteractionObjectPair, int>();

            public void Size(int objs, int pairs)
            {
                if (Obj.Length < objs)
                {
                    int n = Math.Max(objs, Obj.Length * 2);
                    Obj = new IMetaObject[n]; ObjImpl = new int[n]; Start = new int[n + 1]; ObjWhy = new int[n];
                    ObjKey = new ulong[n]; LotKey = new ulong[n]; Visited = new bool[n]; Consumed = new bool[n]; ObjCmp = new bool[n];
                }
                if (Pair.Length < pairs)
                {
                    int n = Math.Max(pairs, Pair.Length * 2);
                    Pair = new InteractionObjectPair[n]; PairObj = new int[n]; Def = new int[n]; Code = new int[n]; Buf = new int[n];
                    W = new float[n]; Live = new float[n]; Acb = new bool[n]; LiveHas = new bool[n]; Sent = new bool[n]; Sel = new bool[n];
                    Eligible = new bool[n];
                }
                if (Bands.Order.Length < pairs) Bands.Order = new int[Math.Max(pairs, Bands.Order.Length * 2)];
            }
            public void AddEvent(int id, float score)
            {
                if (EvN == EvId.Length)
                {
                    int n = EvN * 2;
                    int[] a = new int[n]; Array.Copy(EvId, a, EvN); EvId = a;
                    float[] s = new float[n]; Array.Copy(EvScore, s, EvN); EvScore = s;
                    EvSend = new bool[n];
                }
                EvId[EvN] = id; EvScore[EvN] = score; EvN++;
            }
        }

        static readonly LiveSearch[] sLive = new LiveSearch[8];
        static LiveSearch sLiveCur;     // the search whose game B13 runs now with the consumer on (sim thread)
        static int sLiveActive;         // searches with Active set

        // the active search of this autonomy: sLiveCur, else the short list (a B13 that yielded lost sLiveCur to another
        // search; its later C2 / B14 calls still belong to it)
        static LiveSearch LiveOf(object aut)
        {
            LiveSearch L = sLiveCur;
            if (L != null && (object)L.Aut == aut) return L;
            if (sLiveActive == 0) return null;
            for (int i = 0; i < sLive.Length; i++)
            {
                L = sLive[i];
                if (L != null && L.Active && (object)L.Aut == aut) return L;
            }
            return null;
        }
        static void LiveActive(LiveSearch L, bool on) { if (L.Active != on) { L.Active = on; sLiveActive += on ? 1 : -1; } }
        static bool sLiveMode, sLiveOff, sB14Ok, sLiveGuardOk;
        static int sLiveTopN = 10, sLiveBands = 3, sLiveMaxWait = 3;
        static float sLiveMargin;
        static bool sLiveDry;
        static readonly int[] sLiveLogged = new int[64];
        static int sDryLogged, sDryUnequal, sDryNoCmpLogged;  // dry-sim-mis records; unequal searches (dry-diff sampling); dry-nocmp records
        static FieldInfo fInterrupted, fInspection, fBestScores;
        static StopWatch sLiveSw, sLiveB14Sw;
        // docs/124 §17.10 crowd cost guard (AutLiveCore.CostGuard; switch autliveguard, default kAutLiveGuard = on); acting only
        static readonly AutLiveCore.CostGuard sGuard = new AutLiveCore.CostGuard();
        static bool sGuardNative;  // ts3native answers the cost keys (Config 24 = 1)
        static int sGuardLogged;
        const int kGuardMinRefRuns = 3, kCWindows = 538, kCSkipWindows = 539, kCSkipped = 540, kCRatioPm = 541;
        // docs/217 L1 autlivelate (fixes-550): later B13 slices checked, refused by reason, late objects consumed / stale,
        // consumed late objects by age (1, 2, 3+ ticks after the resume), B14 us by class (consumed, game's B14 for late, for
        // invalid/stale/ident objects; counted only with autlivelate on)
        const int kCLateSlices = 550, kCLateRefAge = 551, kCLateRefEpoch = 552, kCLateRefState = 553, kCLateRefLot = 554, kCLateRefHead = 555,
            kCLateRefActor = 556, kCLateRefOther = 557, kCLateConsumed = 558, kCLateStale = 559, kCLateAge1 = 560, kCConsumedUs = 563,
            kCLateGameUs = 564, kCInvalidGameUs = 565;

        // docs/222 unseen-direct (switch autunseen, fixes-611..640): searches by path, snap-backs, direct consume, D2 live Test,
        // queued-pick snap-back, per-path own cost guard (the exact path keeps 538-541), vanilla B14 rate, Dry by actor class
        const int kCUnPosted = 611, kCUnSeenPost = 612, kCUnSnapResume = 613, kCUnSnapSlice = 614, kCUnDropped = 615, kCUnDirect = 616,
            kCUnPairs = 617, kCUnOffList = 618, kCUnObjGame = 619, kCUnTestPass = 620, kCUnTestFail = 621, kCUnVanilla = 622,
            kCUnNoPick = 623, kCUnTagged = 624, kCUnFlipKept = 625, kCUnFlipCancel = 626, kCUnGuardWin = 627, kCUnGuardSkipWin = 628,
            kCUnGuardSkipped = 629, kCUnGuardPm = 630, kCUnCostUs = 631, kCUnSavedUs = 632, kCB14Pairs = 633, kCB14Us = 634,
            kCUnDryRuns = 635, kCUnDryTop10 = 636, kCUnDryTop1 = 637, kCUnExc = 638, kCUnDrySeenRuns = 639, kCUnTestUs = 640;
        // docs/222 §8 (fixes-659): pacing = us charged to B13's yield timer for skipped ads, yields the charge brought forward
        const int kCUnPaceUs = 659, kCUnPaceForced = 660;
        const int kUnRedraws = 1, kUnMaxTags = 64, kB14SampleMask = 7;
        static StopWatch sMetaTimer;             // Autonomy.sUpdateTimer (B13's 1 ms yield timer, docs/59 Y2); null = no pacing
        static readonly AutLiveCore.CostGuard sGuardU = new AutLiveCore.CostGuard();
        static readonly AutLiveCore.B14Rate sB14Rate = new AutLiveCore.B14Rate();
        static bool sChooseOk;                   // the D2 hook is patched (AutShadow.ApplyAutLiveB14); without it no direct search
        static AutonomyT sDirectAut;             // the autonomy whose next D2 call follows a direct search
        static InteractionInstance sDirectInst;  // the pick DirectChoose returned (tagged at the push)
        static readonly List<InteractionObjectPair> sDirectPairs = new List<InteractionObjectPair>();  // inserted without a live Test
        static readonly Dictionary<InteractionObjectPair, bool> sDirectSeen = new Dictionary<InteractionObjectPair, bool>();
        static readonly List<KeyValuePair<InteractionInstance, Sim>> sDirectTags = new List<KeyValuePair<InteractionInstance, Sim>>();
        static int sFlipFrame = -1, sB14Sample;
        static StopWatch sRateSw;

        static void LiveStat(int slot) { AutShadowNative.LiveStat(slot, 1); }

        // Ready(), sim thread, once per load: Mode 3 and its settings
        static void LiveReady()
        {
            sLiveMode = AutShadowNative.Config(0) == 3;
            Array.Clear(sLiveLogged, 0, sLiveLogged.Length);
            sDryLogged = sDryUnequal = sDryNoCmpLogged = 0;
            if (!sLiveMode) return;
            sLiveTopN = AutShadowNative.Config(13);
            sLiveBands = AutShadowNative.Config(14);
            sLiveMargin = AutShadowNative.Config(15) / 1000f;
            sLiveMaxWait = AutShadowNative.Config(16);
            sLiveDry = AutShadowNative.Config(18) != 0;
            const BindingFlags inst = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            fInterrupted = typeof(AutonomyT).GetField("mAutonomySearchWasInterrupted", inst);
            fInspection = typeof(AutonomyT).GetField("IsRunningAutonomyForInspectionWindow", inst);
            fBestScores = typeof(AutonomyT).GetField("mBestScores", inst);
            FieldInfo fTimer = typeof(AutonomyT).GetField("sUpdateTimer", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            sMetaTimer = fTimer != null ? fTimer.GetValue(null) as StopWatch : null;
            Type mt = typeof(Sims3.Gameplay.Autonomy.AutonomyManager);
            fMgrInstance = mt.GetField("sInstance", BindingFlags.Static | BindingFlags.NonPublic);
            fMgrHigh = mt.GetField("mAutonomyQueueHighLOD", inst); fMgrLow = mt.GetField("mAutonomyQueueLowLOD", inst);
            if (fMgrHigh == null || fMgrLow == null) fMgrInstance = null;
            if (fInterrupted == null || fInspection == null || fBestScores == null)
            {
                AppliedLog().Add("AutShadow: go-live off: a private Autonomy field was not found");
                sLiveMode = false;
                return;
            }
            if (sLiveSw == null) sLiveSw = StopWatch.Create(StopWatch.TickStyles.Microseconds);
            if (sLiveB14Sw == null) sLiveB14Sw = StopWatch.Create(StopWatch.TickStyles.Microseconds);
            sGuard.Reset();
            sGuard.OffPm = Instantiator.kAutLiveGuardOffPm; sGuard.OnPm = Instantiator.kAutLiveGuardOnPm;
            sGuard.MinSearches = Instantiator.kAutLiveGuardMinSearches; sGuard.MinFrames = Instantiator.kAutLiveGuardMinFrames;
            sGuard.Hold = Instantiator.kAutLiveGuardHold;
            sGuardNative = AutShadowNative.Config(24) == 1;
            sGuardLogged = 0;
            PatchCounters()[kCRatioPm] = -1;
            // docs/222 §6.3: both paths count only measured sim cost against the vanilla B14 rate (one guard per path)
            sGuard.Own = true;
            sGuardU.Reset(); sGuardU.Own = true;
            sGuardU.OffPm = sGuard.OffPm; sGuardU.OnPm = sGuard.OnPm; sGuardU.MinSearches = sGuard.MinSearches;
            sGuardU.MinFrames = sGuard.MinFrames; sGuardU.Hold = sGuard.Hold;
            PatchCounters()[kCUnGuardPm] = -1;
            sB14Rate.Reset();
            if (sRateSw == null) sRateSw = StopWatch.Create(StopWatch.TickStyles.Microseconds);
            sDirectAut = null; sDirectInst = null; sDirectPairs.Clear(); sDirectSeen.Clear(); sDirectTags.Clear(); sFlipFrame = -1;
            sDefCmp.Clear(); sDefCmpN = sDefCmpLogN = 0; sDefCmpLogFrame = AutShadowNative.Config(19);
            AppliedLog().Add("AutShadow: go-live Mode 3 (" + (sLiveDry ? "dry" : "acting") + ") TopN " + sLiveTopN + " Bands " + sLiveBands + " Margin "
                + sLiveMargin + " MaxWait " + sLiveMaxWait + "; cost guard " + (sLiveDry ? "n/a (dry)" : !sGuardNative ? "off (ts3native without cost keys)"
                : "Off " + sGuard.OffPm + " On " + sGuard.OnPm + " MinSearches " + sGuard.MinSearches + " MinFrames " + sGuard.MinFrames + " Hold " + sGuard.Hold)
                + (sB14Ok ? "" : "; B14 hook missing: off") + (sLiveGuardOk ? "" : "; consumer path swapped: off")
                + (sChooseOk ? "; unseen-direct ready (switch autunseen)" : "; unseen-direct off (D2 hook missing)")
                + (sMetaTimer != null ? "; pacing ready (switch autunseenpace)" : "; pacing off (Autonomy.sUpdateTimer not found)"));
        }

        // 1 = consume, -1 = Mode 3 with autlive off (the game's search, no request), 0 = not Mode 3 (shadow modes as before)
        static int LiveWanted()
        {
            if (sProbe == 0)
            {
                try { Ready(); } catch (Exception) { }
            }
            if (sProbe <= 0 || !sLiveMode) return 0;
            return ShortcutOn("autlive") ? 1 : -1;
        }

        // docs/124 §13.5: the 18 consumer-path methods by exact signature; ours (B13, B14, C2) pass as own swaps
        static bool LiveGuard()
        {
            Type ip = typeof(InteractionObjectPair), ck = typeof(CommodityKind);
            bool ok = AllL3Ok(typeof(AutonomyT), new string[] { "ScoreInteractionsOnObjects", "ScoreInteractionsForMetaAutonomy", "YieldIfPossible",
                    "ScoreInteractionsForMetaObject", "CalculateScoreAndAddToCandidates", "CalculateScore", "AssignProbabilitiesAndChooseInteraction" },
                    new Type[][] { new Type[] { ck, typeof(bool) }, new Type[] { ck }, Type.EmptyTypes, new Type[] { typeof(IMetaObject), ck },
                        new Type[] { ip }, new Type[] { ip }, Type.EmptyTypes })
                && AllL3Ok(ip, new string[] { "get_Tuning" }, new Type[][] { Type.EmptyTypes })
                && AllL3Ok(typeof(BestScores), new string[] { "Add", "AddInteractionOnlyIfBest", "ChooseInteraction", "ChooseInteractions" },
                    new Type[][] { new Type[] { ip, typeof(float) }, new Type[] { ip, typeof(float) }, new Type[] { typeof(float), typeof(DecisionSnapshot) },
                        new Type[] { typeof(int) } });
            if (!ok) AppliedLog().Add("AutShadow: go-live off for the load: a consumer-path method is swapped (docs/124 §13.5)");
            return ok;
        }

        static LiveSearch LiveAcquire()
        {
            int now = AutShadowNative.Config(19);
            for (int i = 0; i < sLive.Length; i++)
            {
                LiveSearch L = sLive[i];
                if (L == null) return sLive[i] = Taken(new LiveSearch(), now);
                // a search whose task never came back (a reset destroys the parked task): reclaimed
                if (L.InUse && ((L.Ready && now - L.ReadyFrame > 64) || (!L.Ready && now - L.PostFrame > 600))) LiveRelease(L);
                if (!L.InUse) return Taken(L, now);
            }
            return null;
        }
        static LiveSearch Taken(LiveSearch L, int now) { L.InUse = true; L.PostFrame = now; return L; }

        static void LiveRelease(LiveSearch L)
        {
            Array.Clear(L.Obj, 0, L.Objs);
            Array.Clear(L.Pair, 0, L.Pairs);
            L.ObjIndex.Clear(); L.PairIndex.Clear(); L.OtherId.Clear();
            L.Aut = null; L.Actor = null; L.LotCur = L.LotHome = null; L.Household = L.Role = L.Occ = L.Job = null; L.Career = null;
            L.Ready = L.Ok = L.Abandoned = L.Late = false; L.EvN = 0; L.Objs = L.Pairs = 0;
            L.Direct = L.Snapped = L.UnseenDry = false; L.SavedPairs = 0;
            L.CmpOn = L.CmpSnapped = false;
            LiveActive(L, false);
            L.InUse = false;
        }
        static void LiveAbandon(LiveSearch L) { if (L.Ready) LiveRelease(L); else L.Abandoned = true; }

        // TryRequest, after the request is built (sim thread, before Post)
        static void LiveFill(LiveSearch L, Request q)
        {
            L.Size(q.Objs, q.Pairs);
            L.Id = q.Id; L.Aut = q.Aut; L.Actor = q.Actor; L.Objs = q.Objs; L.Pairs = q.Pairs;
            L.Ready = L.Ok = L.Abandoned = L.Late = false; L.Dry = sLiveDry; L.EvN = 0; L.Feat = 0;
            L.ObjIndex.Clear(); L.PairIndex.Clear(); L.OtherId.Clear();
            for (int o = 0; o < q.Objs; o++)
            {
                IMetaObject m = q.Obj[o];
                L.Obj[o] = m; L.ObjImpl[o] = q.ObjImpl[o]; L.Start[o] = q.Start[o];
                L.Visited[o] = L.Consumed[o] = L.ObjCmp[o] = false;
                if (!L.ObjIndex.ContainsKey(m)) L.ObjIndex[m] = o;
                L.ObjKey[o] = m.ObjectId.Value;
                Lot lot = m as Lot ?? m.LotCurrent;
                L.LotKey[o] = lot != null ? lot.LotId : 0UL;
                AutShadowNative.LiveWatch(L.ObjKey[o], 0);
                if (L.LotKey[o] != 0UL) AutShadowNative.LiveWatch(L.LotKey[o], 1);
            }
            L.Start[q.Objs] = q.Pairs;
            for (int p = 0; p < q.Pairs; p++)
            {
                L.Pair[p] = q.Pair[p]; L.PairObj[p] = q.PairObj[p]; L.Def[p] = q.Def[p]; L.Acb[p] = q.Acb[p];
                L.LiveHas[p] = L.Sent[p] = L.Sel[p] = L.Eligible[p] = false;
                if (!L.PairIndex.ContainsKey(q.Pair[p])) L.PairIndex[q.Pair[p]] = p;
            }
            ActorFields(L.Actor, out L.AgeSpecies, out L.LotCur, out L.LotHome, out L.Household, out L.Selectable, out L.Role, out L.Occ,
                out L.JobCount, out L.Job, out L.Career, out L.Chip);
        }

        static void ActorFields(Sim s, out int ageSpecies, out Lot lotCur, out Lot lotHome, out object household, out bool selectable,
            out object role, out object occ, out int jobCount, out object job, out IMetaObject career, out bool chip)
        {
            SimDescription d = s.SimDescription;
            ageSpecies = d != null ? (int)(d.Age | d.Species) : -1;
            lotCur = s.LotCurrent; lotHome = s.LotHome;
            household = d != null ? d.Household : null;
            selectable = s.IsSelectable;
            role = d != null ? d.AssignedRole : null;
            Occupation o = d != null ? d.Occupation : null;
            occ = o;
            jobCount = o != null ? o.JobCount : -1;
            job = o != null ? o.CurrentJob : null;
            career = s.CareerLocation;
            chip = s.HasTrait(TraitNames.CapacityToLoveChip);
        }

        // AutFinish (sim thread, end of frame): the worker's answers into the search; valid = the run (or its kept part) counts
        internal static void LiveFinish(LiveSearch L, Request q, Result w, int lastRun, bool valid)
        {
            if (L == null || !L.InUse || L.Id != q.Id) return;
            for (int p = 0; p < L.Pairs; p++) { L.Code[p] = w.Code[p]; L.W[p] = w.Score[p]; }
            L.LastRun = lastRun;
            L.Ok = valid && w.Id == q.Id;
            L.Feat = AutLiveCore.Feat.Build(sFeatNative ? AutShadowNative.Config(33) : 0, w.RainUsed, true, sGuard.Probation, w.Partial);
            L.CutEpoch = AutShadowNative.Config(21);
            L.CutFrame = AutShadowNative.Config(22);
            L.ReadyFrame = AutShadowNative.Config(19);
            L.Ready = true;
            if (L.Abandoned) LiveRelease(L);
        }

        static bool LiveEligible(AutonomyT a)
        {
            if (sLiveOff || !sB14Ok || !sLiveGuardOk || a.Actor == null) return false;
            if (a.CurrentSearchType != AutonomySearchType.Autonomy || (bool)fInspection.GetValue(a)) return false;
            return Simulator.CheckYieldingContext(false);
        }

        // MetaHook with Mode 3 and autlive on. Returns B13's YieldResult as int (0 Continue, 1 SearchInterrupted, 2 ActorDestroyed).
        static int LiveMeta(AutMetaHook h, AutonomyT a, CommodityKind c)
        {
            if (!LiveEligible(a)) return h.MetaOrig(c);
            LiveStat(kSEligible);
            FlipCheck();
            int[] uk = PatchCounters();
            // docs/222 scope: the actor only; UfWhy 0 = unseen (never the active household or a selectable Sim)
            bool unseenOn = sChooseOk && ShortcutOn("autunseen");
            int why0 = unseenOn ? SafeUfWhy(a.Actor) : -1;
            bool direct = AutLiveCore.Unseen.AtPost(unseenOn, sLiveDry, why0);
            // docs/222 §11.8 V6 test: a seen actor runs the game's own search, no post and no worker wait in the shared manager
            if (unseenOn && !direct && !sLiveDry && ShortcutOn("autseengame")) { uk[kCSeenGameRun]++; return h.MetaOrig(c); }
            if (unseenOn && !direct && !sLiveDry) uk[kCUnSeenPost]++;
            if (!GuardAllow(direct)) return h.MetaOrig(c);
            LiveSearch L = null;
            try
            {
                Request q = TryRequest(a, c, true);
                L = q != null ? q.Live : null;
            }
            catch (Exception e) { PatchCounters()[121]++; AutNote("go-live request: " + e.Message); }
            if (L == null) { LiveStat(kSNotPosted); return h.MetaOrig(c); }
            double share = 0;  // consumed share of the allow-listed pairs (0 = dropped; live-use record)
            LiveStat(kSPosted);
            L.Direct = direct; L.UnseenDry = sLiveDry && unseenOn && why0 == 0; L.SavedPairs = 0;
            L.Snapped = sLiveDry && unseenOn && why0 != 0;  // Dry: a seen actor (by-class counters)
            L.Cut0 = AutShadowNative.Config(25); L.Wait0 = AutShadowNative.Config(27);
            if (direct) uk[kCUnPosted]++;
            int waited = 0;
            while (true)
            {
                Simulator.Sleep(0);
                waited++;
                // the game's YieldIfPossible after its own Sleep: the same two returns
                if ((bool)fInterrupted.GetValue(a)) { fInterrupted.SetValue(a, false); LiveAbandon(L); LiveStat(kSInterrupted); return 1; }
                if (a.Actor == null || a.Actor.HasBeenDestroyed) { LiveAbandon(L); LiveStat(kSDestroyed); return 2; }
                if (L.Ready) break;
                if (waited >= sLiveMaxWait) { GuardAdd(L, 0); if (L.Direct) uk[kCUnDropped]++; LiveAbandon(L); LiveDrop(L, kSDropWait); AutShadowNative.LiveStat(kSWaitTicks, waited); WorkerWait(a.Actor, waited); return h.MetaOrig(c); }
            }
            AutShadowNative.LiveStat(kSWaitTicks, waited);
            WorkerWait(a.Actor, waited);
            uint us = 0;
            sLiveSw.Restart();
            int why;
            try { why = LiveCheck(L, a); if (why == 0) LiveSelect(L); }
            catch (Exception e) { why = kSDropOther; PatchCounters()[121]++; AutNote("go-live check: " + e.Message); }
            us += sLiveSw.GetElapsedTime();
            if (why != 0) { if (L.Direct) uk[kCUnDropped]++; GuardAdd(L, us); LiveDrop(L, why); LiveRelease(L); AutShadowNative.LiveStat(kSApplyUs, (int)us); return h.MetaOrig(c); }
            // snap-back at the resume: seen now = this search runs the exact path
            if (L.Direct && !AutLiveCore.Unseen.Stay(SafeUfWhy(a.Actor))) { L.Direct = false; uk[kCUnSnapResume]++; }
            if (L.Direct)
            {
                sDirectPairs.Clear(); sDirectSeen.Clear(); sDirectInst = null; sDirectAut = null; L.Pace.Slice();
                // docs/222 §11.8: each tick spent waiting for the worker already held the manager like one vanilla slice (>= 1 ms)
                L.Pace.CreditMs = ShortcutOn("autunseenpacecredit") ? waited : 0;
            }
            BestScores bs = (BestScores)fBestScores.GetValue(a);
            AutLiveCore.Heap before = L.Dry ? Snapshot(L, bs) : null;
            L.ResumeFrame = L.SliceFrame = L.CmpSlice = AutShadowNative.Config(19);
            L.LateOn = ShortcutOn("autlivelate"); L.LateOk = false;
            // docs/222 §9: an unseen actor's Dry search (still unseen at the resume, as the direct path's snap-back) compared per pair
            L.CmpOn = L.UnseenDry && ShortcutOn("autunseencmp") && AutLiveCore.Unseen.Stay(SafeUfWhy(a.Actor));
            if (L.CmpOn) uk[kCUnCmpSearches]++;
            sLiveCur = L;
            LiveActive(L, true);  // cleared by LiveRelease
            int r, req = L.Id, feat = 0;
            bool dry = L.Dry, compared = false;
            string exc = null;
            try
            {
                r = h.MetaOrig(c);
                if (sLiveCur == L) sLiveCur = null;
                if (r == 0)
                {
                    sLiveSw.Restart();
                    try
                    {
                        if (L.Dry) { DryCompare(L, bs, before); compared = true; }
                        else if (L.Direct) { sDirectAut = sDirectPairs.Count > 0 ? a : null; uk[kCUnDirect]++; }  // no bands: D2 tests the pick live
                        else { LiveBands(L, a, bs); sExactAut = a; }
                    }
                    catch (Exception e) { PatchCounters()[121]++; exc = e.GetType().Name + ": " + e.Message; AutNote("go-live bands: " + e.Message); }
                    us += sLiveSw.GetElapsedTime();
                }
            }
            finally
            {
                if (sLiveCur == L) sLiveCur = null;
                if (!dry) { share = ConsumedShare(L); GuardAdd(L, us); }
                feat = L.Feat;
                try { SearchEnd(L, dry, share); } catch (Exception) { PatchCounters()[121]++; }
                LiveRelease(L);
            }
            LiveStat(dry ? kSDryRuns : kSConsumed);
            // a dry search counted without a compare would read as unequal in the G7 rates: count and record it on its own
            if (dry && !compared)
            {
                LiveStat(kSDryNoCmp);
                AutLiveCore.Feat.Count(PatchCounters(), kCFeatDrop, feat);
                if (sDryNoCmpLogged++ < kDryMaxLog)
                    AutShadowNative.Log("{\"t\":\"dry-nocmp\",\"req\":" + req + ",\"r\":" + r + AutLiveCore.Feat.Json(feat) + ",\"exc\":\""
                        + (exc == null ? "" : JsonEsc(exc.Length > 300 ? exc.Substring(0, 300) : exc)) + "\"}");
            }
            AutShadowNative.LiveStat(kSApplyUs, (int)us);
            return r;
        }

        // docs/124 §17.10: false = skip this eligible search (no post: the game's B13). Measures in both switch states; skips
        // only with autliveguard on. Dry (G7) and a ts3native without the cost keys: always post.
        // docs/222 §6.3: one guard per path (exact = sGuard, slots 538-541; unseen-direct = sGuardU, 627-630). Both count only
        // measured sim cost per search (GuardAdd) against the vanilla B14 rate x consumed ads; no rate yet = no decision.
        static bool GuardAllow(bool direct)
        {
            if (sLiveDry || !sGuardNative) return true;
            int[] k = PatchCounters();
            AutLiveCore.CostGuard g = direct ? sGuardU : sGuard;
            int kWin = direct ? kCUnGuardWin : kCWindows, kSkipWin = direct ? kCUnGuardSkipWin : kCSkipWindows,
                kSkipped = direct ? kCUnGuardSkipped : kCSkipped, kPm = direct ? kCUnGuardPm : kCRatioPm;
            bool act = ShortcutOn("autliveguard");
            int now = AutShadowNative.Config(19);
            if (!g.Allow(now, act)) { k[kSkipped]++; return false; }
            int r = g.Tick(now, 0, 0, 0, sB14Rate.PerPair > 0 ? 1 : 0, act);
            if (r == 0) return true;
            k[kWin]++; k[kPm] = g.LastPm;
            if (r == 1) return true;
            k[kSkipWin]++; k[kSkipped]++;
            if (sGuardLogged++ < kLiveMaxLogPerReason)
                AutShadowNative.Log("{\"t\":\"live-guard\",\"path\":\"" + (direct ? "unseen" : "exact") + "\",\"pm\":" + g.LastPm
                    + ",\"holdFrames\":" + (g.Until - now) + ",\"frame\":" + now + "}");
            return false;
        }

        // a used or dropped search's own sim cost: consumer us + its cut + the sim's GC lock waits while it was in flight (native
        // words since the post; one request in flight, docs/122 §14.1); saved = vanilla B14 us per ad x the ads it consumed
        static void GuardAdd(LiveSearch L, double consumerUs)
        {
            if (L.Dry) return;
            double own = consumerUs;
            if (sGuardNative) own += unchecked(AutShadowNative.Config(25) - L.Cut0) + unchecked(AutShadowNative.Config(27) - L.Wait0);
            double saved = sB14Rate.PerPair * L.SavedPairs;
            (L.Direct ? sGuardU : sGuard).Add(own, saved);
            if (L.Direct) { int[] k = PatchCounters(); k[kCUnCostUs] += (int)own; k[kCUnSavedUs] += (int)saved; }
        }

        // the share of the request's allow-listed pairs whose object the B14 hook consumed (the game did not score them itself;
        // band pairs it re-scored are in the consumer time)
        static double ConsumedShare(LiveSearch L)
        {
            int all = 0, used = 0;
            for (int p = 0; p < L.Pairs; p++)
                if (L.Def[p] >= 0) { all++; if (L.Consumed[L.PairObj[p]]) used++; }
            return all > 0 ? used / (double)all : 0;
        }

        static void LiveDrop(LiveSearch L, int why)
        {
            LiveStat(why);
            int[] k = PatchCounters();
            AutLiveCore.Feat.Count(k, kCFeatSearches, L.Feat);
            AutLiveCore.Feat.Count(k, kCFeatDrop, L.Feat);
            if (sFeatNative) AutShadowNative.SearchMark(L.Id, L.Feat, null);
            if (why < sLiveLogged.Length && sLiveLogged[why]++ < kLiveMaxLogPerReason)
                AutShadowNative.Log("{\"t\":\"live-drop\",\"why\":" + why + ",\"req\":" + L.Id + ",\"lastRun\":" + L.LastRun + AutLiveCore.Feat.Json(L.Feat) + "}");
        }

        // ---- per-search feature mask (AutLiveCore.Feat; fixes-575: 575-583 searches, 584-592 mismatches, 593-601 give-ups, per bit) ----
        const int kCFeatSearches = 575, kCFeatMis = 584, kCFeatDrop = 593, kLiveUseMaxLog = 50;
        static bool sFeatNative;  // ts3native 0.36.148+: Config(33) job bits, SearchMark
        static int sLiveUseLogged;
        // a used search's end (sim thread): per-bit search count, the crash-log mark (top worker pick's definition), and for an
        // acting search a capped live-use record with its mask (the dry records carry the mask themselves)
        static void SearchEnd(LiveSearch L, bool dry, double share)
        {
            AutLiveCore.Feat.Count(PatchCounters(), kCFeatSearches, L.Feat);
            int top = -1;
            for (int p = 0; p < L.Pairs; p++)
                if (L.Code[p] == AutShadowCore.kPass && (top < 0 || L.W[p] > L.W[top])) top = p;
            string def = top >= 0 && L.Pair[top] != null && L.Pair[top].InteractionDefinition != null ? L.Pair[top].InteractionDefinition.GetType().Name : null;
            if (sFeatNative) AutShadowNative.SearchMark(L.Id, L.Feat, def);
            if (!dry && sLiveUseLogged < kLiveUseMaxLog)
            {
                sLiveUseLogged++;
                AutShadowNative.Log("{\"t\":\"live-use\",\"req\":" + L.Id + ",\"share\":" + share.ToString("0.###", CultureInfo.InvariantCulture)
                    + ",\"top\":\"" + JsonEsc(def ?? "") + "\"" + AutLiveCore.Feat.Json(L.Feat) + "}");
            }
        }

        // §3.1 result level; 0 = use it
        static int LiveCheck(LiveSearch L, AutonomyT a)
        {
            if (!L.Ok) return L.LastRun == 2 ? kSDropEpochRun : L.LastRun == 3 || L.LastRun == 4 ? kSDropNoWorker : L.LastRun == 5 ? kSDropExc : kSDropStopped;
            if (AutShadowNative.Config(20) != L.CutEpoch) return kSDropEpoch;
            if (!GameStates.IsLiveState || GameStates.IsGameShuttingDown) return kSDropState;
            Sim s = L.Actor;
            if (s.LotCurrent != L.LotCur) return kSDropLot;
            if (s.InteractionQueue != null && s.InteractionQueue.GetHeadInteraction() != null) return kSDropHead;
            int ags, jobs; Lot lc, lh; object hh, role, occ, job; bool sel, chip; IMetaObject career;
            ActorFields(s, out ags, out lc, out lh, out hh, out sel, out role, out occ, out jobs, out job, out career, out chip);
            if (ags != L.AgeSpecies || lh != L.LotHome || hh != L.Household || sel != L.Selectable || role != L.Role || occ != L.Occ
                || jobs != L.JobCount || job != L.Job || career != L.Career || chip != L.Chip) return kSDropActor;
            return 0;
        }

        // per object validity, the worker order of the valid objects' passing pairs, band 1
        static void LiveSelect(LiveSearch L)
        {
            for (int o = 0; o < L.Objs; o++)
            {
                int why = 0;
                if (L.ObjImpl[o] < 0) why = kSObjInvalid;
                for (int p = L.Start[o]; p < L.Start[o + 1] && why == 0; p++)
                {
                    if (L.Acb[p]) why = kSObjAcb;
                    else if (L.Def[p] >= 0 && (L.Code[p] == AutShadowCore.kAbort || L.Code[p] == AutShadowCore.kSkip)) why = kSObjInvalid;
                }
                if (why == 0 && (AutShadowNative.ObjStale(L.ObjKey[o], 0, L.CutFrame) != 0
                    || (L.LotKey[o] != 0UL && AutShadowNative.ObjStale(L.LotKey[o], 1, L.CutFrame) != 0))) why = kSObjStale;
                L.ObjWhy[o] = why;
            }
            for (int p = 0; p < L.Pairs; p++)
                L.Eligible[p] = L.Def[p] >= 0 && L.Code[p] == AutShadowCore.kPass && L.ObjWhy[L.PairObj[p]] == 0;
            int n = AutLiveCore.Order(L.W, L.Eligible, L.Pairs, L.Bands.Order);
            L.Bands.Start(sLiveTopN, sLiveBands, sLiveMargin, n);
            for (int r = 0; r < n && r < sLiveTopN; r++) L.Sel[L.Bands.Order[r]] = true;
            AutShadowNative.LiveStat(kSCandidates, n);
            // a candidate stays eligible for the bands only once B14 consumed its object (set in LiveB14)
            for (int p = 0; p < L.Pairs; p++) L.Eligible[p] = false;
        }

        // B14 hook, the consumer for one meta object (inside the game's B13)
        static void LiveB14(LiveSearch L, AutonomyT a, IMetaObject m, CommodityKind c, AutB14Hook h)
        {
            int o;
            if (m == null || !L.ObjIndex.TryGetValue(m, out o)) { LiveStat(kSObjNotInReq); h.B14Orig(m, c); return; }
            L.Visited[o] = true;
            if (L.Direct) { DirectB14(L, a, m, c, h, o); return; }
            if (L.CmpOn) DefCmpObj(L, m, o);
            // B13 yielded (Y2) since the resume: the rest of this search is older than one tick. autlivelate (docs/217 L1):
            // a later slice still consumes when it passes the resume checks again and is at most kAutLiveLateTicks old
            int f = AutShadowNative.Config(19);
            bool late = L.Late || f != L.ResumeFrame;
            if (late && !(L.LateOn && LateSliceOk(L, a, f))) { L.Late = true; LiveStat(kSObjLate); B14Timed(L, m, c, h, kCLateGameUs); return; }
            int why = L.ObjWhy[o];
            // the resume-time ObjStale answer is older than this slice: ask again (object, then its lot)
            if (late && why == 0 && (AutShadowNative.ObjStale(L.ObjKey[o], 0, L.CutFrame) != 0
                || (L.LotKey[o] != 0UL && AutShadowNative.ObjStale(L.LotKey[o], 1, L.CutFrame) != 0))) { why = kSObjStale; PatchCounters()[kCLateStale]++; }
            if (why == 0 && !SameAds(L, o, m.MetaAds)) why = kSObjIdent;
            if (why != 0) { LiveStat(why); B14Timed(L, m, c, h, kCInvalidGameUs); return; }
            L.Consumed[o] = true;
            LiveStat(kSObjConsumed);
            if (late) { L.Feat |= 1 << AutLiveCore.Feat.Late; int[] k = PatchCounters(); k[kCLateConsumed]++; k[kCLateAge1 + Math.Min(f - L.ResumeFrame, 3) - 1]++; }
            for (int p = L.Start[o]; p < L.Start[o + 1]; p++)
                if (L.Def[p] >= 0 && L.Code[p] == AutShadowCore.kPass) L.Eligible[p] = true;
            if (L.Dry) { B14Timed(L, m, c, h, kCConsumedUs); return; }  // the game scores every ad; the C2 hook records what consume would have sent
            uint bandUs = 0;
            sLiveB14Sw.Restart();
            for (int p = L.Start[o]; p < L.Start[o + 1]; p++)
            {
                InteractionObjectPair iop = L.Pair[p];
                if (!PassesFilter(iop, c)) continue;
                if (L.Def[p] < 0) { LiveStat(kSPairsOffList); a.CalculateScoreAndAddToCandidates(iop); }
                else if (L.Code[p] == AutShadowCore.kFiltered) { LiveStat(kSPairsFilterDiff); a.CalculateScoreAndAddToCandidates(iop); }
                else
                {
                    L.SavedPairs++;
                    if (L.Sel[p])
                    {
                        L.Sent[p] = true; LiveStat(kSPairsBand1);
                        uint t0 = sLiveB14Sw.GetElapsedTime();
                        a.CalculateScoreAndAddToCandidates(iop);
                        bandUs += sLiveB14Sw.GetElapsedTime() - t0;
                    }
                }
            }
            uint us = sLiveB14Sw.GetElapsedTime();
            // docs/222 §6.3: only the band-1 C1 calls are a go-live cost; off-list C1 is work vanilla does anyway
            sGuard.CostUs += bandUs;
            if (L.LateOn) PatchCounters()[kCConsumedUs] += (int)us;
        }

        // docs/217 L1: the slice verdict, once per B13 time slice (its first B14 call); a refusal is sticky (L.Late)
        static bool LateSliceOk(LiveSearch L, AutonomyT a, int f)
        {
            if (f == L.SliceFrame) return L.LateOk;
            L.SliceFrame = f;
            int[] k = PatchCounters();
            k[kCLateSlices]++;
            int chk = 0;
            if (!L.Late && f - L.ResumeFrame >= 0 && f - L.ResumeFrame <= Instantiator.kAutLiveLateTicks)
            {
                try { chk = LiveCheck(L, a); }
                catch (Exception e) { chk = kSDropOther; k[121]++; AutNote("go-live late check: " + e.Message); }
            }
            int v = AutLiveCore.LateSlice(L.Late, f, L.ResumeFrame, Instantiator.kAutLiveLateTicks, chk);
            L.LateOk = v == AutLiveCore.kLateOk;
            if (v == AutLiveCore.kLateAge) k[kCLateRefAge]++;
            else if (v == AutLiveCore.kLateCheck)
                k[chk == kSDropEpoch ? kCLateRefEpoch : chk == kSDropState ? kCLateRefState : chk == kSDropLot ? kCLateRefLot
                    : chk == kSDropHead ? kCLateRefHead : chk == kSDropActor ? kCLateRefActor : kCLateRefOther]++;
            return L.LateOk;
        }

        // the game's B14, timed into a ts3fixes us slot when autlivelate is on (B14 cost by class, docs/217 §4)
        static void B14Timed(LiveSearch L, IMetaObject m, CommodityKind c, AutB14Hook h, int slot)
        {
            sLiveB14Sw.Restart();
            h.B14Orig(m, c);
            uint us = sLiveB14Sw.GetElapsedTime();
            if (L.LateOn) PatchCounters()[slot] += (int)us;
            RateAdd(us, m);
        }

        // the vanilla B14 rate (docs/222 §6.3): one timed game B14, us per meta ad
        static void RateAdd(uint us, IMetaObject m)
        {
            ArrayList ads = m != null ? m.MetaAds : null;
            int n = ads != null ? ads.Count : 0;
            if (n <= 0) return;
            sB14Rate.Add(us, n);
            int[] k = PatchCounters();
            k[kCB14Pairs] += n; k[kCB14Us] += (int)us;
        }

        // B14's own commodity filter (the same as the worker driver's ScorePair)
        static bool PassesFilter(InteractionObjectPair iop, CommodityKind c)
        {
            Tradeoff t = iop.Tradeoff;
            if (t == null) return false;
            if (c == CommodityKind.None || t.SatisfiesCommodity(c)) return true;
            IMetaInteractionDefinition md = iop.InteractionDefinition as IMetaInteractionDefinition;
            return md != null && md.MetaAdSatisfiesCommodity(iop.Target as Lot, c);
        }

        // the object's MetaAds now are the request's pairs, same references and count (AddObj skipped non-pairs; the game's B14
        // would throw on one, so a mismatch hands the object to the game's B14)
        static bool SameAds(LiveSearch L, int o, ArrayList ads)
        {
            if (ads == null) return L.Start[o + 1] == L.Start[o];
            int n = L.Start[o + 1] - L.Start[o];
            if (ads.Count != n) return false;
            for (int j = 0; j < n; j++) if (!ReferenceEquals(ads[j], L.Pair[L.Start[o] + j])) return false;
            return true;
        }

        // C2 hook (AutScoreHook): the game's own score of a pair of the live search
        internal static void LiveScored(LiveSearch L, InteractionObjectPair iop, float s)
        {
            int p = -1;
            if (iop == null || !L.PairIndex.TryGetValue(iop, out p)) p = -1;
            if (p >= 0) { L.Live[p] = s; L.LiveHas[p] = true; }
            if (L.Dry)
            {
                if (L.CmpOn && p >= 0) DefCmpNote(L, p, s);
                // AlwaysChooseBest pairs go to BestScores' side list, never the queue [verified, decompiled BestScores.Add]
                InteractionTuning tun = iop != null ? iop.Tuning : null;
                if (tun != null && tun.AlwaysChooseBest) return;
                int id = p;
                if (p < 0)
                {
                    if (iop == null || !L.OtherId.TryGetValue(iop, out id)) { id = -1 - L.OtherId.Count; if (iop != null) L.OtherId[iop] = id; }
                }
                L.AddEvent(id, s);
            }
            else if (p >= 0 && L.Sent[p] && L.Def[p] >= 0 && L.Code[p] == AutShadowCore.kPass) L.Bands.Saw(L.W[p], s);
        }

        // after the game's B13: the §16.1 bands through C1, on the game's own BestScores
        static void LiveBands(LiveSearch L, AutonomyT a, BestScores bs)
        {
            sLiveCur = L;  // the bands' C2 calls feed drift
            try
            {
                while (true)
                {
                    int cnt = bs.Count;
                    float s10 = 0f;
                    for (int i = 0; i < cnt; i++) { float v = bs.Element(i).InteractionScore; if (i == 0 || v < s10) s10 = v; }
                    int n = L.Bands.Step(cnt, AutLiveCore.kCap, s10, L.Eligible, L.Sent, L.W, L.Buf);
                    if (n == 0) break;
                    LiveStat(L.Bands.Full ? kSBandFull : L.Bands.Fired == 1 ? kSBand2 : kSBand3);
                    LiveStat(L.Bands.LastWhy == 1 ? kSWhyCount : kSWhyMargin);
                    for (int i = 0; i < n; i++)
                    {
                        int p = L.Buf[i];
                        L.Sent[p] = true;
                        if (L.Obj[L.PairObj[p]].HasBeenDestroyed) continue;
                        LiveStat(kSPairsBand);
                        a.CalculateScoreAndAddToCandidates(L.Pair[p]);
                    }
                    if (L.Bands.Full) break;
                }
            }
            finally { if (sLiveCur == L) sLiveCur = null; }
            LiveStat(kSDrift0 + AutLiveCore.DriftBin(L.Bands.DriftMax));
        }

        // ---- G7 dry compare ----

        static int OtherIdOf(LiveSearch L, InteractionObjectPair iop)
        {
            int id;
            if (iop != null && L.PairIndex.TryGetValue(iop, out id)) return id;
            if (iop == null || !L.OtherId.TryGetValue(iop, out id)) { id = -1 - L.OtherId.Count; if (iop != null) L.OtherId[iop] = id; }
            return id;
        }
        static AutLiveCore.Heap Snapshot(LiveSearch L, BestScores bs)
        {
            AutLiveCore.Heap h = new AutLiveCore.Heap(AutLiveCore.kCap);
            for (int i = 0; i < bs.Count; i++) { InteractionCandidate e = bs.Element(i); h.Add(OtherIdOf(L, e.InteractionObjectPair), e.InteractionScore); }
            return h;
        }
        static AutLiveCore.Heap Copy(AutLiveCore.Heap h)
        {
            AutLiveCore.Heap x = new AutLiveCore.Heap(AutLiveCore.kCap);
            for (int i = 0; i < h.N; i++) x.Add(h.Id[i], h.S[i]);
            return x;
        }

        // [[id,score],...] in slot order
        static StringBuilder Entries(StringBuilder s, AutLiveCore.Heap h)
        {
            s.Append('[');
            for (int i = 0; i < h.N; i++) s.Append(i > 0 ? ",[" : "[").Append(h.Id[i]).Append(',').Append(h.S[i].ToString("R")).Append(']');
            return s.Append(']');
        }

        static void DryCompare(LiveSearch L, BestScores bs, AutLiveCore.Heap before)
        {
            AutLiveCore.Heap game = Snapshot(L, bs), van = Copy(before), pred = Copy(before);
            int offList = 0;
            for (int i = 0; i < L.EvN; i++)
            {
                int p = L.EvId[i];
                bool send = true;
                if (p >= 0)
                {
                    int o = L.PairObj[p];
                    if (L.Def[p] < 0) offList++;
                    else if (L.Consumed[o] && L.Code[p] != AutShadowCore.kFiltered) send = L.Sel[p];
                    if (send && L.Sel[p] && L.Consumed[o]) { L.Sent[p] = true; L.Bands.Saw(L.W[p], L.EvScore[i]); }
                }
                L.EvSend[i] = send;
            }
            AutLiveCore.Replay(van, L.EvN, L.EvId, L.EvScore, null);
            AutLiveCore.Replay(pred, L.EvN, L.EvId, L.EvScore, L.EvSend);
            int fired = AutLiveCore.RunBands(pred, L.Bands, L.Eligible, L.Sent, L.W, L.Live, L.LiveHas, L.Buf);
            for (int k = 1; k <= fired; k++) LiveStat(k == fired && L.Bands.Full ? kSBandFull : k == 1 ? kSBand2 : kSBand3);  // as LiveBands
            if (fired > 0) LiveStat(L.Bands.LastWhy == 1 ? kSWhyCount : kSWhyMargin);  // the last band's rule
            LiveStat(kSDrift0 + AutLiveCore.DriftBin(L.Bands.DriftMax));
            AutShadowNative.LiveStat(kSDryOffList, offList);
            bool mis = false;
            if (!van.SameSet(game))
            {
                mis = true;
                LiveStat(kSDrySimMis);
                if (sDryLogged < kDryMaxLog)
                {
                    sDryLogged++;
                    StringBuilder m = new StringBuilder(512);
                    m.Append("{\"t\":\"dry-sim-mis\",\"req\":").Append(L.Id).Append(AutLiveCore.Feat.Json(L.Feat)).Append(",\"events\":").Append(L.EvN).Append(",\"before\":");
                    Entries(m, before).Append(",\"van\":");
                    Entries(m, van).Append(",\"game\":");
                    Entries(m, game).Append('}');
                    AutShadowNative.Log(m.ToString());
                }
            }
            // top 1 up to ties: same top score and the game's top entry among ours at that score (which of several equal tops
            // sits first in slot order is heap order, not a different pick) [inferred: the choice weighs entries by score only]
            bool top10 = pred.SameSet(game), top1 = pred.SameTop(game);
            if (top10) LiveStat(kSDryTop10);
            if (top1) LiveStat(kSDryTop1);
            if (L.UnseenDry) { int[] uk = PatchCounters(); uk[kCUnDryRuns]++; if (top10) uk[kCUnDryTop10]++; if (top1) uk[kCUnDryTop1]++; }
            else if (L.Snapped) PatchCounters()[kCUnDrySeenRuns]++;
            if (top1 && pred.TopId() != game.TopId()) LiveStat(kSDryTop1Tie);
            if (mis || !(top10 && top1)) AutLiveCore.Feat.Count(PatchCounters(), kCFeatMis, L.Feat);  // once per search
            if (top10 && top1) return;
            // every unequal search is counted by cause (game entries we miss, our entries the game lacks); records: the first
            // kDryDiffFirst per load, then 1 in kDryDiffEvery, each under the native line limit (AsIcLog drops >= 1300 chars)
            sDryUnequal++;
            StringBuilder s = sDryUnequal <= kDryDiffFirst || sDryUnequal % kDryDiffEvery == 0 ? new StringBuilder(1024) : null;
            if (s != null)
            {
                s.Append("{\"t\":\"dry-diff\",\"n\":").Append(sDryUnequal).Append(",\"req\":").Append(L.Id).Append(AutLiveCore.Feat.Json(L.Feat))
                 .Append(",\"actor\":").Append(L.Actor != null ? L.Actor.ObjectId.Value : 0UL)
                 .Append(",\"top10\":").Append(top10 ? 1 : 0).Append(",\"top1\":").Append(top1 ? 1 : 0)
                 .Append(",\"predTop\":[").Append(pred.TopId()).Append(',').Append(pred.TopScore().ToString("R"))
                 .Append("],\"gameTop\":[").Append(game.TopId()).Append(',').Append(game.TopScore().ToString("R"))
                 .Append("],\"ties\":").Append(game.CountAt(game.TopScore())).Append(",\"predTies\":").Append(pred.CountAt(pred.TopScore()))
                 .Append(",\"drift\":").Append(L.Bands.DriftMax.ToString("R")).Append(",\"bands\":").Append(fired).Append(",\"pred\":");
                Entries(s, pred).Append(",\"game\":");
                Entries(s, game).Append(",\"miss\":[");
            }
            int cause = 0, extra = 0;
            bool first = true, trunc = false;
            for (int i = 0; i < game.N; i++)
            {
                int id = game.Id[i];
                if (pred.Has(id)) continue;
                int why = id < 0 ? kSDryMissOther : L.Code[id] == AutShadowCore.kTestFailed ? kSDryMissStale
                    : L.Code[id] == AutShadowCore.kPass && !L.Sent[id] ? kSDryMissBand : kSDryMissOther;
                LiveStat(why);
                if (cause == 0) cause = why;
                if (s == null) continue;
                if (s.Length > kLogMaxChars) { trunc = true; continue; }
                if (!first) s.Append(',');
                first = false;
                s.Append("{\"id\":").Append(id).Append(",\"live\":").Append(game.S[i].ToString("R"));
                if (id >= 0) PairFields(s, L, id);
                s.Append('}');
            }
            if (s != null) s.Append("],\"extra\":[");
            first = true;
            for (int i = 0; i < pred.N; i++)
            {
                int id = pred.Id[i];
                if (game.Has(id)) continue;
                extra++;
                LiveStat(kSDryExtra);
                if (s == null) continue;
                if (s.Length > kLogMaxChars) { trunc = true; continue; }
                if (!first) s.Append(',');
                first = false;
                s.Append("{\"id\":").Append(id).Append(",\"pred\":").Append(pred.S[i].ToString("R"));
                if (id >= 0) PairFields(s, L, id);
                s.Append('}');
            }
            if (s == null) return;
            // cause: the first missed pair's bucket, else extra (our set holds entries the game's lacks), else top1 (same set)
            s.Append("],\"cause\":\"").Append(cause == kSDryMissStale ? "stale" : cause == kSDryMissBand ? "band" : cause != 0 ? "other"
                : extra > 0 ? "extra" : "top1").Append('"');
            if (trunc) s.Append(",\"trunc\":1");
            AutShadowNative.Log(s.Append('}').ToString());
        }

        static void PairFields(StringBuilder s, LiveSearch L, int id)
        {
            string def = L.Pair[id] != null && L.Pair[id].InteractionDefinition != null ? L.Pair[id].InteractionDefinition.GetType().Name : "?";
            if (def.Length > 48) def = def.Substring(0, 48);
            s.Append(",\"def\":\"").Append(def).Append("\",\"code\":").Append(L.Code[id]).Append(",\"work\":").Append(L.W[id].ToString("R"))
             .Append(",\"sel\":").Append(L.Sel[id] ? 1 : 0).Append(",\"sent\":").Append(L.Sent[id] ? 1 : 0)
             .Append(",\"lv\":").Append(L.LiveHas[id] ? L.Live[id].ToString("R") : "null");
        }

        static string JsonEsc(string v) { return v.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " "); }

        // AutFinish in Mode 3 on a cut that ran the reference (VerifyEvery): top picks of worker vs reference over the pairs both
        // answered; an A2/A3 from CompareR turns go-live off for the session
        static void LiveVerify(int a23Before)
        {
            int[] c = PatchCounters();
            if (c[108] + c[109] > a23Before)
            {
                if (!sLiveOff) AppliedLog().Add("AutShadow: go-live off for the session: VerifyEvery found an A2/A3 (request " + sReq.Id + ")");
                sLiveOff = true;
                LiveStat(kSVerifyOff);
            }
            bool[] ans = new bool[sReq.Pairs];
            for (int i = 0; i < sReq.Pairs; i++)
            {
                if (sReq.Def[i] < 0 || sReq.ObjImpl[sReq.PairObj[i]] < 0) continue;
                int a = sWork.Code[i], b = sRef.Code[i];
                ans[i] = (a == AutShadowCore.kPass || a == AutShadowCore.kTestFailed) && (b == AutShadowCore.kPass || b == AutShadowCore.kTestFailed);
            }
            int[] tw = Top(sWork, 10, ans), tr = Top(sRef, 10, ans);
            int k = Math.Min(Count(tw), Count(tr));
            LiveStat(kSVerifyRuns);
            if (tw[0] == tr[0]) LiveStat(kSVerifyTop1);
            if (Count(tw) == Count(tr) && SameSet(tw, tr, k)) LiveStat(kSVerifyTop10);
        }

        // ---- docs/124 §8 G8 behaviour counters (kAutLive packages, both autlive arms; fixes-550 slots 566-574) ----
        // ConsiderPerformingAutonomousAction (private, one caller: RunAutonomy with addToQueue) passes through; after it:
        // pushes, searches with no pick, the MakeSimGoHome fallback RunAutonomy takes next (its own condition, read-only
        // getters), Sim-hours (instantiated Sims x sim hours, x100, sampled per sim hour), request-to-push latency in ticks from
        // the B13 entry (Mode 3 searches only; bins 0, 1, 2, 3, 4+), and a {"t":"picks"} autshadow.jsonl line (cumulative pushes
        // per definition, "DeclaringType.Definition", most first) every kPicksEvery pushes.
        // docs/222 §8 (fixes-661): the same by actor class at the push / no-pick (UfWhy 0 = unseen, else seen): searches (every
        // ConsiderPerformingAutonomousAction), pushes, no-pick, GoHome; unseen 661-664, seen 665-668; unseen Sim-hours x100 669
        // (the hourly sample's unseen actors), so per-search rates and the cadence (searches per Sim-hour) split by class.
        const int kCPushes = 566, kCNullPicks = 567, kCGoHome = 568, kCSimHours100 = 569, kCLatency0 = 570, kPicksEvery = 500;
        const int kCG8Unseen = 661, kCG8Seen = 665, kCG8UnseenHours100 = 669;  // + 0 searches, 1 pushes, 2 no-pick, 3 GoHome
        static readonly Dictionary<object, int> sMetaStart = new Dictionary<object, int>();  // autonomy -> frame of its B13 entry
        static readonly Dictionary<string, int> sPicks = new Dictionary<string, int>();
        static float sG8Hours = -1f;
        static int sPushN;
        // docs/222 §11.8 (fixes-785): why seen cadence falls. Queue wait = AutonomyManager.Add to the search's start (B13 entry; a
        // search with no meta phase starts at its push, the local phase never yields, docs/59 §1.3): n, tick sum, bins (QueueBin) for
        // seen 785-791, unseen 792-798. Manager queues at each search end: high sum, low sum, samples, samples with both empty
        // (799-802). Seen searches by path + their no-picks: exact 803-804, game (guard skip, drop, autseengame) 805-806. Household
        // searches, pushes, Sim-hours x100 807-809. Worker wait: seen n, ticks 810-811, unseen 812-813. Search length start to end,
        // ticks: seen 814, unseen 815. autseengame searches 816, pacing credit used us 817. Household part of seen (HouseholdScope:
        // household or scope unknown; still in the seen slots): queue wait n, ticks 867-868, worker wait n, ticks 869-870.
        const int kCQSeen = 785, kCQUnseen = 792, kCQHigh = 799, kCQLow = 800, kCQDepthN = 801, kCQEmpty = 802, kCSeenExact = 803, kCSeenGame = 805,
            kCHousehold = 807, kCHouseholdHours100 = 809, kCWorkSeen = 810, kCWorkUnseen = 812, kCLenSeen = 814, kCLenUnseen = 815, kCSeenGameRun = 816, kCUnPaceCredit = 817, kCHhQueue = 867, kCHhWork = 869;
        static readonly Dictionary<object, int> sEnq = new Dictionary<object, int>();  // autonomy -> tick of its AutonomyManager.Add
        static FieldInfo fMgrInstance, fMgrHigh, fMgrLow;
        static AutonomyT sExactAut;  // the autonomy whose last search the exact consumer finished

        static void WorkerWait(Sim s, int waited)
        {
            int[] k = PatchCounters();
            int why = SafeUfWhy(s), b = why == 0 ? kCWorkUnseen : kCWorkSeen;
            k[b]++; k[b + 1] += waited;
            if (UnseenFallbackCore.HouseholdScope(why)) { k[kCHhWork]++; k[kCHhWork + 1] += waited; }
        }

        static void QueueNote(AutonomyT a, bool timed, int f0, bool seen, bool hh, bool noPick)
        {
            int[] k = PatchCounters();
            int now = AutShadowNative.Config(19), start = timed ? f0 : now, enq;
            k[seen ? kCLenSeen : kCLenUnseen] += now - start;
            if (sEnq.TryGetValue(a, out enq))
            {
                sEnq.Remove(a);
                int w = Math.Max(0, start - enq), b = seen ? kCQSeen : kCQUnseen;
                k[b]++; k[b + 1] += w; k[b + 2 + AutLiveCore.QueueBin(w)]++;
                if (hh) { k[kCHhQueue]++; k[kCHhQueue + 1] += w; }
            }
            if (seen)
            {
                int p = ReferenceEquals(sExactAut, a) ? kCSeenExact : kCSeenGame;
                k[p]++; if (noPick) k[p + 1]++;
            }
            if (ReferenceEquals(sExactAut, a)) sExactAut = null;
            object mgr = fMgrInstance != null ? fMgrInstance.GetValue(null) : null;
            if (mgr == null) return;
            int hi = ((ICollection<AutonomyT>)fMgrHigh.GetValue(mgr)).Count, lo = ((ICollection<AutonomyT>)fMgrLow.GetValue(mgr)).Count;
            k[kCQHigh] += hi; k[kCQLow] += lo; k[kCQDepthN]++;
            if (hi + lo == 0) k[kCQEmpty]++;
        }

        static void MetaStart(AutonomyT a)
        {
            if (!sLiveMode) return;
            if (sMetaStart.Count > 256) sMetaStart.Clear();  // searches that never reached the push (destroyed Sims) keep no autonomy alive
            sMetaStart[a] = AutShadowNative.Config(19);
        }

        static void G8Note(AutonomyT a, InteractionInstance best, bool pushed)
        {
            int[] k = PatchCounters();
            float h = SimClock.ElapsedTime(TimeUnit.Hours);
            if (sG8Hours < 0f || h < sG8Hours) sG8Hours = h;
            else if (h - sG8Hours >= 1f)
            {
                int n = 0, nu = 0, nh = 0;
                foreach (Sim s in LotManager.Actors) if (s != null) { n++; int w = SafeUfWhy(s); if (w == 0) nu++; else if (w == UnseenFallbackCore.kHousehold) nh++; }
                k[kCSimHours100] += (int)(n * (h - sG8Hours) * 100f);
                k[kCHouseholdHours100] += (int)(nh * (h - sG8Hours) * 100f);
                k[kCG8UnseenHours100] += (int)(nu * (h - sG8Hours) * 100f);
                sG8Hours = h;
            }
            int f0;
            bool timed = sMetaStart.TryGetValue(a, out f0);
            if (timed) sMetaStart.Remove(a);
            Sim actor = a.Actor;
            int wc = SafeUfWhy(actor), cls = wc == 0 ? kCG8Unseen : kCG8Seen;
            k[cls]++;
            if (wc == UnseenFallbackCore.kHousehold) { k[kCHousehold]++; if (pushed) k[kCHousehold + 1]++; }
            QueueNote(a, timed, f0, wc != 0, UnseenFallbackCore.HouseholdScope(wc), best == null);
            if (best == null)
            {
                k[kCNullPicks]++; k[cls + 2]++;
                if (actor != null && !actor.HasBeenDestroyed && actor.LotCurrent != null && actor.Service == null && !actor.IsSelectable
                    && !actor.IsSimInSituationWhichRulesOutMetaAutonomy) { k[kCGoHome]++; k[cls + 3]++; }
                return;
            }
            if (!pushed) return;
            k[kCPushes]++; k[cls + 1]++;
            if (timed) k[kCLatency0 + Math.Max(0, Math.Min(AutShadowNative.Config(19) - f0, 4))]++;
            string def = best.InteractionDefinition != null ? DefKey(best.InteractionDefinition.GetType()) : "?";
            int c;
            sPicks[def] = sPicks.TryGetValue(def, out c) ? c + 1 : 1;
            if (++sPushN % kPicksEvery == 0 && sProbe > 0) LogPicks();
        }

        // most definitions are a nested "Definition": the owning interaction names it
        static string DefKey(Type t) { return t.DeclaringType != null ? t.DeclaringType.Name + "." + t.Name : t.Name; }

        static void LogPicks()
        {
            List<KeyValuePair<string, int>> all = new List<KeyValuePair<string, int>>(sPicks);
            all.Sort(delegate (KeyValuePair<string, int> x, KeyValuePair<string, int> y) { return y.Value.CompareTo(x.Value); });
            StringBuilder b = new StringBuilder(1300);
            b.Append("{\"t\":\"picks\",\"n\":").Append(sPushN).Append(",\"def\":[");
            for (int i = 0; i < all.Count; i++)
            {
                string d = all[i].Key.Length > 64 ? all[i].Key.Substring(0, 64) : all[i].Key;
                if (b.Length + d.Length + 16 > 1250) break;  // native line limit 1300 (AsIcLog)
                b.Append(i > 0 ? ",[\"" : "[\"").Append(JsonEsc(d)).Append("\",").Append(all[i].Value).Append(']');
            }
            AutShadowNative.Log(b.Append("]}").ToString());
        }

        // docs/222 §11.8 AutonomyManager.Add (public static; the Sim task's enqueue, docs/59 §1.1). Passes through; Mode 3 only.
        public class AutQueueHook
        {
            public static void AddHook(AutonomyT a)
            {
                try
                {
                    if (sLiveMode && a != null)
                    {
                        if (sEnq.Count > 256) sEnq.Clear();  // Sims destroyed in the queue keep no autonomy alive
                        sEnq[a] = AutShadowNative.Config(19);
                    }
                }
                catch (Exception) { PatchCounters()[121]++; }
                AddOrig(a);
            }
            public static void AddOrig(AutonomyT a) { } // receives AutonomyManager.Add's original header
        }

        public class AutPushHook
        {
            public bool PushHook(InteractionInstance best)
            {
                bool r = PushOrig(best);
                // Mode 3 only: the hook is in every package since O6, and G8Note walks every Sim each sim-hour
                try { if (sLiveMode) G8Note((AutonomyT)(object)this, best, r); } catch (Exception) { PatchCounters()[121]++; }
                if (best != null && ReferenceEquals(best, sDirectInst))
                {
                    sDirectInst = null;
                    try { if (r) DirectTag(best, ((AutonomyT)(object)this).Actor); } catch (Exception) { PatchCounters()[kCUnExc]++; }
                }
                return r;
            }
            public bool PushOrig(InteractionInstance best) { return false; } // receives Autonomy.ConsiderPerformingAutonomousAction's original header
        }

        // ---- docs/222 unseen-direct ----

        static int SafeUfWhy(Sim s)
        {
            try { return s == null ? -1 : UfWhy(s); }
            catch (Exception) { PatchCounters()[kCUnExc]++; return -1; }
        }

        // the B14 consumer of a direct search: every object of every B13 slice (no late fallback). A new slice re-checks the
        // actor (seen = the rest of the search exact) and, after the resume slice, the object's ObjStale. Valid objects: the
        // worker's passing allow-listed ads go into BestScores with the worker's score (no C1/C2), off-list ads through the
        // game's C1; invalid, stale, destroyed or changed objects run the game's B14.
        static void DirectB14(LiveSearch L, AutonomyT a, IMetaObject m, CommodityKind c, AutB14Hook h, int o)
        {
            int[] k = PatchCounters();
            int f = AutShadowNative.Config(19);
            if (f != L.SliceFrame)
            {
                L.SliceFrame = f;
                if (L.Pace.Forced) k[kCUnPaceForced]++;
                L.Pace.Slice();
                if (!L.Snapped && f != L.ResumeFrame && !AutLiveCore.Unseen.Stay(SafeUfWhy(L.Actor))) { L.Snapped = true; k[kCUnSnapSlice]++; }
            }
            if (L.Snapped) { k[kCUnObjGame]++; B14Timed(L, m, c, h, kCLateGameUs); return; }
            int why = L.ObjWhy[o];
            if (why == 0 && f != L.ResumeFrame && (AutShadowNative.ObjStale(L.ObjKey[o], 0, L.CutFrame) != 0
                || (L.LotKey[o] != 0UL && AutShadowNative.ObjStale(L.LotKey[o], 1, L.CutFrame) != 0))) why = kSObjStale;
            if (why == 0 && (m.HasBeenDestroyed || !SameAds(L, o, m.MetaAds))) why = kSObjIdent;
            if (why != 0) { LiveStat(why); k[kCUnObjGame]++; B14Timed(L, m, c, h, kCInvalidGameUs); return; }
            L.Consumed[o] = true;
            LiveStat(kSObjConsumed);
            BestScores bs = (BestScores)fBestScores.GetValue(a);
            int saved0 = L.SavedPairs;
            bool rh = ShortcutOn("autunseenrh");
            for (int p = L.Start[o]; p < L.Start[o + 1]; p++)
            {
                InteractionObjectPair iop = L.Pair[p];
                if (!PassesFilter(iop, c)) continue;
                if (L.Def[p] < 0 || L.Code[p] == AutShadowCore.kFiltered) { k[kCUnOffList]++; a.CalculateScoreAndAddToCandidates(iop); continue; }
                // docs/222 §10: rabbit-hole pairs (score = RabbitHole.HowMuchItWantsSimToCome) get the game's own C1, like off-list
                // pairs; real sim time, so not in SavedPairs (no pacing charge, no saved side)
                if (rh && iop.Target is Sims3.Gameplay.Abstracts.RabbitHole) { k[kCUnRhGame]++; a.CalculateScoreAndAddToCandidates(iop); continue; }
                L.SavedPairs++;
                // the worker's failed Test = vanilla C2 scores 0 and BestScores.Add drops it: nothing to add
                if (L.Code[p] != AutShadowCore.kPass || sDirectSeen.ContainsKey(iop)) continue;
                sDirectSeen[iop] = true;
                sDirectPairs.Add(iop);
                bs.Add(iop, L.W[p]);
                k[kCUnPairs]++;
            }
            if (L.SavedPairs > saved0 && sMetaTimer != null && ShortcutOn("autunseenpace")) PaceCharge(L, L.SavedPairs - saved0);
        }

        // docs/222 §8: the skipped ads' vanilla cost onto B13's yield timer, so the meta loop yields on vanilla's schedule. The
        // native Set writes only the stored part (start tick kept), so a running watch is stopped (stored += running), set and
        // restarted [verified: disassembly 0x4F3460 set, 0x4F3220 stop, 0x408700 start].
        static void PaceCharge(LiveSearch L, int ads)
        {
            StopWatch t = sMetaTimer;
            bool run = t.IsRunning();
            if (run) t.Stop();
            float ms = t.GetElapsedTimeFloat();
            double credit = L.Pace.CreditMs;
            double add = L.Pace.Charge(sB14Rate.PerPair * Instantiator.kAutUnseenPacePct / 100.0, ads, ms, 1.0);  // docs/222 §10: 100% overshot
            PatchCounters()[kCUnPaceCredit] += (int)((credit - L.Pace.CreditMs) * 1000.0);
            if (add > 0) { t.SetElapsedTimeFloat(ms + (float)add); PatchCounters()[kCUnPaceUs] += (int)(add * 1000.0); }
            if (run) t.Start();
        }

        // ---- docs/222 §9 autunseencmp (Dry, measurement only): the worker's per-pair Test/score vs the game's own C2 ----
        // Scope as the direct path: an unseen actor's search on every B13 slice (late ones too); a slice where the actor is seen
        // stops the compare (the direct path snaps back there); per object, only one the direct path would consume (ObjWhy 0,
        // ObjStale re-asked after the resume slice, not destroyed, same ads). Per pair with a worker verdict (pass / Test failed):
        // counters 670-679, and per definition (DeclaringType.Name + "." + Name) {"t":"defcmp"} lines, cumulative, the top
        // kDefCmpTop by mismatches every kDefCmpEvery compared pairs or kDefCmpFrames ticks.
        const int kCUnCmpSearches = 670, kCUnCmpPairs = 671, kCUnCmpWfGp = 672, kCUnCmpWpGf = 673, kCUnCmpBothPass = 674, kCUnCmpBothFail = 675,
            kCUnCmpLatePairs = 676, kCUnCmpLateMis = 677, kCUnCmpObjSkip = 678, kCUnCmpSnapped = 679, kCUnRhGame = 685, kCUnCmpWpGfW0 = 686, kDefCmpEvery = 1000, kDefCmpFrames = 300, kDefCmpTop = 15;
        static readonly Dictionary<string, AutLiveCore.DefCmp> sDefCmp = new Dictionary<string, AutLiveCore.DefCmp>();
        static int sDefCmpN, sDefCmpLogN, sDefCmpLogFrame;

        static void DefCmpObj(LiveSearch L, IMetaObject m, int o)
        {
            int[] k = PatchCounters();
            try
            {
                int f = AutShadowNative.Config(19);
                if (f != L.CmpSlice)
                {
                    L.CmpSlice = f;
                    if (!L.CmpSnapped && !AutLiveCore.Unseen.Stay(SafeUfWhy(L.Actor))) { L.CmpSnapped = true; k[kCUnCmpSnapped]++; }
                }
                bool ok = !L.CmpSnapped && L.ObjWhy[o] == 0;
                if (ok && f != L.ResumeFrame && (AutShadowNative.ObjStale(L.ObjKey[o], 0, L.CutFrame) != 0
                    || (L.LotKey[o] != 0UL && AutShadowNative.ObjStale(L.LotKey[o], 1, L.CutFrame) != 0))) ok = false;
                if (ok && (m.HasBeenDestroyed || !SameAds(L, o, m.MetaAds))) ok = false;
                if (!L.CmpSnapped && !ok) k[kCUnCmpObjSkip]++;
                L.ObjCmp[o] = ok;
            }
            catch (Exception) { k[kCUnExc]++; L.ObjCmp[o] = false; }
        }

        // the C2 hook's score s of request pair p (game pass = s != 0: C2 scores only after its Test passed, docs/59 C2)
        static void DefCmpNote(LiveSearch L, int p, float s)
        {
            int[] k = PatchCounters();
            try
            {
                if (L.Def[p] < 0 || !L.ObjCmp[L.PairObj[p]]) return;
                int code = L.Code[p];
                if (code != AutShadowCore.kPass && code != AutShadowCore.kTestFailed) return;
                InteractionObjectPair iop = L.Pair[p];
                // docs/222 §10: scope as the direct path, which hands rabbit-hole pairs to the game's C1 (counted, not compared)
                if (iop != null && iop.Target is Sims3.Gameplay.Abstracts.RabbitHole && ShortcutOn("autunseenrh")) { k[kCUnRhGame]++; return; }
                bool wp = code == AutShadowCore.kPass, gp = s != 0f;
                int f = AutShadowNative.Config(19);
                bool late = f != L.ResumeFrame;
                k[kCUnCmpPairs]++;
                if (late) k[kCUnCmpLatePairs]++;
                if (wp != gp) { k[wp ? kCUnCmpWpGf : kCUnCmpWfGp]++; if (late) k[kCUnCmpLateMis]++; if (wp && L.W[p] == 0f) k[kCUnCmpWpGfW0]++; }
                else k[wp ? kCUnCmpBothPass : kCUnCmpBothFail]++;
                string def = iop != null && iop.InteractionDefinition != null ? DefKey(iop.InteractionDefinition.GetType()) : "?";
                AutLiveCore.DefCmp d;
                if (!sDefCmp.TryGetValue(def, out d)) sDefCmp[def] = d = new AutLiveCore.DefCmp();
                d.Note(wp, gp, L.W[p], s, f - L.CutFrame);
                // cumulative snapshots every kDefCmpEvery pairs or kDefCmpFrames ticks, so the job's last one is recent
                if (++sDefCmpN - sDefCmpLogN >= kDefCmpEvery || f - sDefCmpLogFrame >= kDefCmpFrames) { sDefCmpLogN = sDefCmpN; sDefCmpLogFrame = f; DefCmpLog(); }
            }
            catch (Exception) { k[kCUnExc]++; }
        }

        static void DefCmpLog()
        {
            List<KeyValuePair<string, AutLiveCore.DefCmp>> all = new List<KeyValuePair<string, AutLiveCore.DefCmp>>(sDefCmp);
            all.Sort(delegate (KeyValuePair<string, AutLiveCore.DefCmp> x, KeyValuePair<string, AutLiveCore.DefCmp> y)
            {
                int c = y.Value.Mis.CompareTo(x.Value.Mis);
                return c != 0 ? c : y.Value.N.CompareTo(x.Value.N);
            });
            for (int i = 0; i < all.Count && i < kDefCmpTop; i++)
            {
                AutLiveCore.DefCmp d = all[i].Value;
                string name = all[i].Key.Length > 96 ? all[i].Key.Substring(0, 96) : all[i].Key;
                StringBuilder b = new StringBuilder(320);
                b.Append("{\"t\":\"defcmp\",\"n\":").Append(sDefCmpN).Append(",\"def\":\"").Append(JsonEsc(name)).Append("\",\"c\":").Append(d.N)
                 .Append(",\"wfgp\":").Append(d.WfGp).Append(",\"wpgf\":").Append(d.WpGf).Append(",\"bp\":").Append(d.BothPass).Append(",\"bf\":").Append(d.BothFail)
                 .Append(",\"med\":").Append(JsonNum(d.Quantile(0.5))).Append(",\"p90\":").Append(JsonNum(d.Quantile(0.9))).Append(",\"age\":[");
                for (int j = 0; j < d.Age.Length; j++) b.Append(j > 0 ? "," : "").Append(d.Age[j]);
                b.Append("],\"ageMis\":[");
                for (int j = 0; j < d.AgeMis.Length; j++) b.Append(j > 0 ? "," : "").Append(d.AgeMis[j]);
                AutShadowNative.Log(b.Append("]}").ToString());
            }
        }
        static string JsonNum(float v) { return float.IsNaN(v) || float.IsInfinity(v) ? "null" : v.ToString("R", CultureInfo.InvariantCulture); }

        static int DirectIndex(InteractionInstance inst)
        {
            if (inst == null) return -1;
            for (int i = 0; i < sDirectPairs.Count; i++)
            {
                InteractionObjectPair iop = sDirectPairs[i];
                if (ReferenceEquals(iop.InteractionDefinition, inst.InteractionDefinition) && ReferenceEquals(iop.Target, inst.Target)) return i;
            }
            return -1;
        }

        // BestScores without one pair (no Remove in the game's API): the entries (the remembered AlwaysChooseBest ones were merged
        // into the queue by ChooseInteraction) re-added with their scores; vanilla = every untested direct pair re-scored live (C2)
        static void Rebuild(AutonomyT a, BestScores bs, InteractionObjectPair drop, bool vanilla)
        {
            int n = bs.Count;
            InteractionObjectPair[] ps = new InteractionObjectPair[n];
            float[] sc = new float[n];
            for (int i = 0; i < n; i++) { InteractionCandidate e = bs.Element(i); ps[i] = e.InteractionObjectPair; sc[i] = e.InteractionScore; }
            bs.Clear();
            for (int i = 0; i < n; i++)
            {
                if (ps[i] == null || ReferenceEquals(ps[i], drop)) continue;
                float s = sc[i];
                if (vanilla && sDirectSeen.ContainsKey(ps[i])) s = a.CalculateScore(ps[i]);
                bs.Add(ps[i], s);
            }
            if (vanilla) sDirectPairs.Clear();
        }

        // D2 after a direct search: the game's draw; a pick from an inserted (untested) pair gets one live Test; a fail draws the
        // next pick without it (kUnRedraws times), then vanilla: the remaining direct pairs scored live and the game draws.
        static InteractionInstance DirectChoose(AutonomyT a, AutChooseHook h)
        {
            int[] k = PatchCounters();
            BestScores bs = (BestScores)fBestScores.GetValue(a);
            for (int fails = 0; ;)
            {
                InteractionInstance inst = h.ChooseOrig();
                int i = DirectIndex(inst);
                if (inst == null) { k[kCUnNoPick]++; return null; }
                if (i < 0) return inst;  // off-list, game-scored, or vanilla: already live
                sRateSw.Restart();
                bool pass = false;
                try { pass = inst.Test(); } catch (Exception) { k[kCUnExc]++; }
                k[kCUnTestUs] += (int)sRateSw.GetElapsedTime();
                InteractionObjectPair iop = sDirectPairs[i];
                if (pass) { k[kCUnTestPass]++; sDirectPairs.RemoveAt(i); sDirectInst = inst; return inst; }
                k[kCUnTestFail]++;
                fails++;
                bool vanilla = AutLiveCore.Unseen.AfterFail(fails, kUnRedraws) == AutLiveCore.Unseen.kVanilla;
                if (vanilla) k[kCUnVanilla]++;
                Rebuild(a, bs, iop, vanilla);
                sDirectPairs.Remove(iop);
            }
        }

        // snap-back of queued direct picks (docs/222 §3): once per frame at a meta search; a tagged pick that is still queued
        // and not the head keeps only if a live Test passes when its Sim is seen
        static void FlipCheck()
        {
            if (sDirectTags.Count == 0) return;
            int f = AutShadowNative.Config(19);
            if (f == sFlipFrame) return;
            sFlipFrame = f;
            int[] k = PatchCounters();
            for (int i = sDirectTags.Count - 1; i >= 0; i--)
            {
                InteractionInstance inst = sDirectTags[i].Key;
                Sim s = sDirectTags[i].Value;
                int v;
                try
                {
                    bool gone = s == null || s.HasBeenDestroyed || s.InteractionQueue == null;
                    bool inQ = !gone && s.InteractionQueue.HasInteraction(inst);
                    bool head = inQ && ReferenceEquals(s.InteractionQueue.GetHeadInteraction(), inst);
                    bool seen = !gone && inQ && !head && !AutLiveCore.Unseen.Stay(SafeUfWhy(s));
                    v = AutLiveCore.Unseen.Flip(gone, inQ, head, seen, seen && inst.Test());
                    if (v == AutLiveCore.Unseen.kFlipCancel) s.InteractionQueue.CancelInteraction(inst, false);
                }
                catch (Exception) { k[kCUnExc]++; v = AutLiveCore.Unseen.kFlipDrop; }
                if (v == AutLiveCore.Unseen.kFlipWait) continue;
                if (v == AutLiveCore.Unseen.kFlipKeep) k[kCUnFlipKept]++;
                else if (v == AutLiveCore.Unseen.kFlipCancel) k[kCUnFlipCancel]++;
                sDirectTags.RemoveAt(i);
            }
        }

        static void DirectTag(InteractionInstance inst, Sim s)
        {
            if (sDirectTags.Count >= kUnMaxTags) sDirectTags.RemoveAt(0);
            sDirectTags.Add(new KeyValuePair<InteractionInstance, Sim>(inst, s));
            if (s != null) AppDomain.CurrentDomain.SetData("ts3fixes.autunseen.tagged", s.ObjectId.Value);  // ts3agent camera_to_tagged (repro)
            PatchCounters()[kCUnTagged]++;
        }

        // D2 (public, no args). Passes through unless the autonomy's last meta search was direct.
        public class AutChooseHook
        {
            public InteractionInstance ChooseHook()
            {
                AutonomyT a = (AutonomyT)(object)this;
                if (sDirectAut == null || !ReferenceEquals(sDirectAut, a)) return ChooseOrig();
                sDirectAut = null;
                try { return DirectChoose(a, this); }
                catch (Exception e) { PatchCounters()[kCUnExc]++; AutNote("unseen-direct D2: " + e.Message); sDirectPairs.Clear(); return ChooseOrig(); }
            }
            public InteractionInstance ChooseOrig() { return null; } // receives Autonomy.AssignProbabilitiesAndChooseInteraction's original header
        }

        // B14 (private, void, one caller: B13). Passes through unless a live search of this autonomy is in its game B13.
        public class AutB14Hook
        {
            public void B14Hook(IMetaObject m, CommodityKind c)
            {
                LiveSearch L = LiveOf(this);
                if (L == null)
                {
                    // docs/222 §6.3 vanilla B14 rate: 1 in 8 pass-through calls timed while Mode 3 is on
                    if (!sLiveMode || sRateSw == null || (++sB14Sample & kB14SampleMask) != 0) { B14Orig(m, c); return; }
                    sRateSw.Restart();
                    B14Orig(m, c);
                    RateAdd(sRateSw.GetElapsedTime(), m);
                    return;
                }
                LiveB14(L, (AutonomyT)(object)this, m, c, this);
            }
            public void B14Orig(IMetaObject m, CommodityKind c) { } // receives Autonomy.ScoreInteractionsForMetaObject's original header
        }
    }
}
