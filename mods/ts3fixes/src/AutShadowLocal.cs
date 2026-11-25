using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;
using AutonomyT = Sims3.Gameplay.Autonomy.Autonomy;

namespace Ts3Fixes
{
    // HouseholdToo local exact path, piece 3 (docs/276 rank 5, docs/data/tier2-local-phase-handoff.md §2 L2): the local
    // request kind and the ScoreLocal replay. Shadow only (Mode R / W): the worker's and the reference's local scores are
    // counted, never used; the game's own B9 has already run unchanged when the request is made. Acting is piece 6.
    // Switch autt2local (default off) AND [Experimental] HouseholdToo=1 (default 0): off = AutShadowLocalCore.Post answers
    // kOff before anything is read, nothing is posted, every hook below runs the game's method.
    //   1. sim thread, inside B7 right after the game's B9 (B13 entry, or B7's return when the search has no meta phase):
    //      post gate (AutShadowLocalCore.Post), then the request: the lot B9 picked (B10 again for the world lot), the other
    //      Sims B9 walked (lot.GetObjects<Sim>()), the generic commodity order (B11), the situation pairs (generic only), the
    //      source mask (AutShadowLocalCore.Sources) and the actor's C0 of this search. Rows: the actor, every Sim (as meta),
    //      the picked lot (kind 3). Object rows for local targets are piece 4: until then their ICs abort on the worker.
    //   2. cut (shared batch, docs/122 §14.18): ScoreLocal on the reference (sim thread) and on the worker. Both walk the B9
    //      order over the live maps at the cut with their own LocalPairSet (never InteractionCheckTable, never BestScores,
    //      never the B12 map removal: an exception aborts that pair `exc`). Off-list definitions (exact runtime type not in
    //      AutShadowLocalList) are `skip` without Test; pairs past kMaxLocalPairs are `skip`, counted lpcap.
    //   3. finish: counters, then (piece 5) the Mode R per-pair compare by walk index, the walk-order check, the game report
    //      against the game's own B9 C2 calls of the same search (recorded in AutScoreHook while the B7 window is open) and
    //      the {"t":"local"} / {"t":"LA<k>"} lines.
    // H8 local rule (L1): inside ScoreLocal, on both sides, Autonomy.UpdateCacheIfNeeded is a no-op only for the request's
    // own autonomy with its C0 still equal to the request's copy; anything else aborts `cache` (other) for that pair.
    // Household Sims: AutShadowLocalCore.Post lets a household-scope actor through only with HouseholdLift (switch on, live
    // Sim); unknown scope (-1), not live and gone never post. A request whose T2Hh is set (household, selected or unknown
    // scope not lifted) takes the explicit uncounted abort in LocalCache and in every tier-2 worker hook. Nothing here
    // changes a Sim, a map, a score or a choice of anyone: the game's search runs before the request and does not wait for it.
    // Counters 1158-1181 (fixes-1158, agent aut_lp_*): [1158-1165] post gate codes (AutShadowLocalCore.kPost..kBusy),
    // [1166] posts refused by the session (not ready, mode, Begin busy), [1167] reference runs done, [1168] worker runs done,
    // [1169] worker results dropped (overrun, epoch, not done), [1170] worker pairs walked (first occurrences), [1171]
    // duplicates, [1172] lpcap, [1173] off-list skips, [1174] pass, [1175] Test failed, [1176] aborts, [1177] pair
    // exceptions, [1178] H8 local no-ops, [1179] H8 local cache aborts, [1180] sim-thread exceptions (request, reference walk and pairs), [1181]
    // reference pairs walked.
    public static partial class Fixes
    {
        const int kMaxLocalKinds = 256;
        const int kCLp = 1158;
        const int kCLpNotReady = kCLp + 8, kCLpRefDone = kCLp + 9, kCLpWorkDone = kCLp + 10, kCLpDropped = kCLp + 11,
            kCLpPairs = kCLp + 12, kCLpDup = kCLp + 13, kCLpCap = kCLp + 14, kCLpSkip = kCLp + 15, kCLpPass = kCLp + 16,
            kCLpFail = kCLp + 17, kCLpAbort = kCLp + 18, kCLpPairExc = kCLp + 19, kCLpNoop = kCLp + 20, kCLpCacheAb = kCLp + 21,
            kCLpExc = kCLp + 22, kCLpRefPairs = kCLp + 23;
        const int kLocalUnit = 0x40000000;  // RNG unit base of a local pair (meta units are o << 16 | pair, o < kMaxObjs)

        static AutonomyT sLocB7;            // the search whose B7 is open with autt2local on (sim thread), else null
        static bool sInLocal;               // ScoreLocal is walking (reference or worker): the H8 hook takes the local rule
        static int sLocCalls;
        static readonly LocalPairSet sLocSetRef = new LocalPairSet(AutShadowLocalCore.kMaxLocalPairs);
        static readonly LocalPairSet sLocSetWork = new LocalPairSet(AutShadowLocalCore.kMaxLocalPairs);
        static readonly LocalC0 sLocC0 = new LocalC0();  // scratch read of the current C0 (one side runs at a time)
        static Dictionary<Type, bool> sLocalTypes;  // exact runtime types of AutShadowLocalList (sim thread fill, read-only after)
        static FieldInfo fLocForce, fLocOverLot, fLocForceSolo, fLocGroup;
        static MethodInfo mLocClosest;
        static readonly int[] sLocUseKeys = new int[kMaxLocalKinds], sLocDesKinds = new int[kMaxLocalKinds];
        static readonly float[] sLocUseVals = new float[kMaxLocalKinds], sLocDesVals = new float[kMaxLocalKinds], sLocDesMax = new float[kMaxLocalKinds];

        // B7 entry (AutLocalHook, sim thread): open the window (not nested, not the worker); the post happens when B9 is over
        static bool LocalB7Open(AutonomyT a, CommodityKind c)
        {
            if (sLocB7 != null || OnWorker() || !ShortcutOn("autt2local")) return false;
            sLocB7 = a; sLocC = c;
            sLocGP.Clear(); sLocGS.Clear();
            sLocRec = HouseholdTooOn();  // piece 5: record the game's C2 only when a post can follow (flag off = nothing recorded)
            return true;
        }

        // B13 entry (AutMetaHook) or B7's return: B9 has run for this search; gate, then post. Never throws.
        static void LocalAfterB9(AutonomyT a)
        {
            if (sLocB7 == null || (object)sLocB7 != (object)a) return;
            sLocB7 = null;
            int[] c = PatchCounters();
            try
            {
                if (fLocForce == null)
                {
                    const BindingFlags all = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                    fLocForce = typeof(AutonomyT).GetField("mForceRunLocalAutonomy", all);
                    fLocOverLot = typeof(AutonomyT).GetField("mOverriddenLocalAutonomyLot", all);
                    fLocForceSolo = typeof(AutonomyT).GetField("mForceScoreSoloInteractions", all);
                    fLocGroup = typeof(AutonomyT).GetField("ActorsGroupSituation", all);
                    mLocClosest = typeof(AutonomyT).GetMethod("FindClosestLotForLocalAutonomy", all);
                }
                bool hhToo = HouseholdTooOn();
                Sim actor = a.Actor as Sim;
                int why = hhToo ? SafeUfWhy(actor) : -1;
                bool lift = hhToo && why == UnseenFallbackCore.kHousehold && HouseholdLift(actor);
                int code = AutShadowLocalCore.Post(ShortcutOn("autt2local"), hhToo, a.CurrentSearchType == AutonomySearchType.Autonomy,
                    (bool)fLocForce.GetValue(a), fLocOverLot.GetValue(a) != null, (bool)fLocForceSolo.GetValue(a),
                    a.IsRunningLocalAutonomy, sPosted && sReqN >= kMaxReq, why, lift);
                c[kCLp + code]++;
                if (code == AutShadowLocalCore.kPost && !LocalRequest(a, actor)) c[kCLpNotReady]++;
            }
            catch (Exception e) { c[kCLpExc]++; AutNote("local post: " + e.Message); }
            finally { sLocRec = false; sLocGP.Clear(); sLocGS.Clear(); }  // copied into the request by LocalRequest, never kept here
        }

        // The request (sim thread, after the game's B9 of the same search; no yield in between, docs/59 §1.3). False = not posted.
        static bool LocalRequest(AutonomyT a, Sim actor)
        {
            if (sSessionOff || !ShortcutOn("autshadow") || actor == null) return false;
            bool join = sPosted;
            if (join ? sReqN >= kMaxReq : (++sLocCalls % sSample != 0 || sGameAut != null)) return false;
            if (!Ready()) return false;
            int mode = AutShadowNative.Config(0);
            if ((mode != 1 && mode != 2) || !sHooksOk || !sWarmOk) return false;  // shadow modes only; Mode 3 posts live searches
            if (sLocalTypes == null) LocalTypes();
            if (!AutShadowNative.Begin()) return false;
            if (!join) sReqN = 0;
            int at = sReqN;
            Request q = sReq = sReqs[at];
            q.Live = null; q.Local = true;
            q.Id = ++sNextId; q.Mode = mode; q.Actor = actor; q.Aut = a; q.C = sLocC; q.Hh = HouseholdExempt(actor);
            q.SearchType = a.CurrentSearchType; q.Flags = a.GetInteractionFlagsForCurrentSearch(); q.Hours = SimClock.HoursPassedOfDay;
            q.Objs = q.Pairs = 0;
            sRefs[at].Id = sWorks[at].Id = -1;
            q.PairIndex.Clear(); q.Lots.Clear(); q.BakeSales.Clear();
            LocalClear(q);
            try { LocalFill(q, a, actor); }
            catch (Exception e) { LocalClear(q); PatchCounters()[kCLpExc]++; AutNote("local request: " + e.Message); }  // posted empty (no walk)
            q.LGame.AddRange(sLocGP); q.LGameS.AddRange(sLocGS);
            PreWarm(actor);
            T2P1Fill(q, actor);
            T2HFill(q);
            T2P2Fill(q, actor);
            ClassifyListTypes();
            ClassifyObjects();
            q.Start[0] = 0;
            sPosted = true;
            sReqN = at + 1;
            if (at == 0) OffRequest();  // docs/208: env / broadcaster items ride the batch's cut (AutOffload.cs)
            AutShadowNative.Post(q.Id);
            return true;
        }

        // B9's inputs, read the way B9 / B10 / B11 read them (Steam 1.67 [verified, decompiled]); q.C = the c of this B7
        static void LocalFill(Request q, AutonomyT a, Sim actor)
        {
            Lot cur = actor.LotCurrent, lot = cur;
            if (lot != null && lot.IsWorldLot) lot = mLocClosest != null ? mLocClosest.Invoke(a, null) as Lot : null;  // B10, as B9
            q.LLot = lot;
            bool generic = q.C == CommodityKind.None;
            q.LSrc = AutShadowLocalCore.Sources(lot == null, lot != null && lot.IsWorldLot, false, actor.IsNPC, cur != null && cur.IsWorldLot,
                generic, q.C == CommodityKind.AlienBrainPower, false);
            if (lot != null && (q.LSrc & 1 << AutShadowLocalCore.kSrcOtherSims) != 0)
                foreach (Sim s in lot.GetObjects<Sim>()) if (s != actor) q.LSims.Add(s);
            if (generic)
            {
                InteractionScorer sc = a.InteractionScorer;
                int un = 0, dn = 0;
                foreach (KeyValuePair<int, float> u in sc.UseValues)
                    if (un < kMaxLocalKinds) { sLocUseKeys[un] = u.Key; sLocUseVals[un++] = u.Value; }
                foreach (Desire d in sc.mDesires)
                    if (dn < kMaxLocalKinds) { sLocDesKinds[dn] = (int)d.Commodity; sLocDesVals[dn] = sc.GetDesireValue(d); sLocDesMax[dn++] = d.MaxY; }
                q.LKindN = AutShadowLocalCore.Commodities(sLocUseKeys, sLocUseVals, un, sLocDesKinds, sLocDesVals, sLocDesMax, dn,
                    (int)CommodityKind.Energy, a.ConsiderSleepingEvenWhenNotSleepy(), q.LKinds);
                foreach (Situation st in a.SituationComponent.Situations)
                    foreach (Sim s in st.SimsWithInteractions)
                    {
                        if (s.HasBeenDestroyed) continue;
                        List<InteractionObjectPair> l = s.GetSituationSpecificInteractionsForActor(actor);
                        if (l != null) q.LSit.AddRange(l);
                    }
            }
            q.LC0.OnFire = a.IsOnFire; q.LC0.BabyComing = a.BabyIsComing; q.LC0.Neat = a.IsNeat; q.LC0.PartyGuest = a.IsGuestAtAParty;
            q.LC0.Scuba = a.IsActorScubaDiving; q.LC0.FireOnLot = a.IsFireOnLot; q.LC0.TombRoom = a.IsActorInTombRoom;
            q.LC0.GroupSituation = fLocGroup != null ? fLocGroup.GetValue(a) : null; q.LC0.HouseholdMembers = a.CachedSimHouseholdMembers;
            AutShadowNative.AddRow(actor.ObjectId.Value, 1);
            if (lot != null)
            {
                q.Lots[lot.LotId] = true;
                if (sReqLotRows) AutShadowNative.AddRow(lot.LotId, 3);
                AutShadowNative.AddRow(lot.ObjectId.Value, 0);
            }
            foreach (Sim s in LotManager.Actors)
                if (s != null && s != actor) AutShadowNative.AddRow(s.ObjectId.Value, Reachable(s) ? 1 : 2);
            LocalRows(q, actor);
        }

        // ---- piece 4 (L4): object rows for every local target ----
        // The targets of every pair the replay will walk (same sources, same map lists, read here on the sim thread at the
        // request) get an object row (kind 0: the (ulong) ICs and GetTask, refreshed at the cut when dirty, docs/122 §2.3), so
        // their ICs answer from the cut instead of aborting on the worker. Sims are rows already (kind 1/2 above). Cap
        // kMaxLocalObjs per request (the native table is shared, AS_CAP 8192 rows; `full` counts its refusals: read it in the Mode R runs). Cut cost: ts3native's
        // cutUs / cutMaxUs / full counters of the same run.
        // Counters [1182] target rows added, [1183] requests that hit the cap, [1184] most rows in one request.
        const int kMaxLocalObjs = 256, kCLpRows = 1182;  // ~22 (ulong) object ICs per object: 256 objects = ~5.6k of AS_CAP 8192 rows
        static readonly Dictionary<ulong, bool> sLocObjSeen = new Dictionary<ulong, bool>();
        static int sLocRowN;
        static bool sLocRowCap;

        static void LocalRows(Request q, Sim actor)
        {
            sLocObjSeen.Clear(); sLocRowN = 0; sLocRowCap = false;
            int m = q.LSrc;
            if ((m & 1 << AutShadowLocalCore.kSrcLotMap) != 0) LocalRowsMap(q, q.LLot.Map);
            if ((m & 1 << AutShadowLocalCore.kSrcOwnMap) != 0) LocalRowsMap(q, actor.SimCommodityInteractionMap);
            if ((m & 1 << AutShadowLocalCore.kSrcOtherSims) != 0) for (int i = 0; i < q.LSims.Count; i++) LocalRowsMap(q, q.LSims[i].SimCommodityInteractionMap);
            if ((m & 1 << AutShadowLocalCore.kSrcInventory) != 0 && actor.Inventory != null) LocalRowsMap(q, actor.Inventory.CommodityInteractionMap);
            if ((m & 1 << AutShadowLocalCore.kSrcSituations) != 0) LocalRowsList(q.LSit);
            if ((m & 1 << AutShadowLocalCore.kSrcSolo) != 0) LocalRowsList(actor.SoloInteractions);
            int[] c = PatchCounters();
            c[kCLpRows] += sLocRowN;
            if (sLocRowCap) c[kCLpRows + 1]++;
            if (sLocRowN > c[kCLpRows + 2]) c[kCLpRows + 2] = sLocRowN;
            sLocObjSeen.Clear();  // no destroyed object id kept between requests
        }

        static void LocalRowsMap(Request q, CommodityInteractionMap map)
        {
            if (map == null) return;
            if (q.C != CommodityKind.None) { LocalRowsList(map.InteractionsForCommodity(q.C)); return; }
            for (int k = 0; k < q.LKindN; k++) LocalRowsList(map.InteractionsForCommodity((CommodityKind)q.LKinds[k]));
        }

        static void LocalRowsList(List<InteractionObjectPair> list)
        {
            if (list == null) return;
            for (int j = 0; j < list.Count; j++)
            {
                InteractionObjectPair iop = list[j];
                IGameObject t = iop != null ? iop.Target : null;
                if (t == null || t is Sim || t.HasBeenDestroyed) continue;
                ulong id = t.ObjectId.Value;
                if (sLocObjSeen.ContainsKey(id)) continue;
                if (sLocRowN >= kMaxLocalObjs) { sLocRowCap = true; return; }
                sLocObjSeen[id] = true;
                AutShadowNative.AddRow(id, 0);
                sLocRowN++;
            }
        }

        static CommodityKind sLocC;  // c of the open B7 (AutLocalHook), read by LocalFill

        static void LocalClear(Request q)
        {
            q.LLot = null; q.LSrc = 0; q.LKindN = 0;
            q.LSims.Clear(); q.LSit.Clear(); q.LGame.Clear(); q.LGameS.Clear();
            q.LC0.CopyFrom(sLocC0Empty);
        }
        static readonly LocalC0 sLocC0Empty = new LocalC0();

        // AutShadowLocalList by exact runtime type, resolved once on the sim thread from the game modules (a name a module does
        // not define is dropped; a mod subclass is a different type and stays off-list)
        static void LocalTypes()
        {
            Dictionary<Type, bool> d = new Dictionary<Type, bool>();
            foreach (string mod in new string[] { "Sims3GameplaySystems", "Sims3GameplayObjects", "Sims3StoreObjects" })
            {
                Assembly asm = GameAssembly(mod);
                if (asm == null) continue;
                foreach (string n in AutShadowLocalList.Names)
                {
                    Type t = asm.GetType(n, false);
                    if (t != null) d[t] = true;
                }
            }
            sLocalTypes = d;
        }

        // ---- ScoreLocal: reference (sim thread) and worker, same code ----

        public static void ScoreLocal(Request q, Result r, LocalPairSet set)
        {
            r.Id = q.Id; r.Exceptions = 0; r.DesireDriver = 0; r.RainUsed = 0; r.Done = false; r.Partial = false;
            r.LN = r.LDup = r.LCap = r.LNoop = r.LCacheAb = 0;
            Array.Clear(r.RainWhy, 0, r.RainWhy.Length);
            Array.Clear(r.T2Ab, 0, r.T2Ab.Length);
            Array.Clear(r.T2H, 0, r.T2H.Length);
            Array.Clear(r.T2P2, 0, r.T2P2.Length);
            r.T2CallPet = 0;
            sCurRes = r;
            set.Clear();
            if (sActorScope && OnWorker()) AutShadowNative.ActorScope(q.Hh ? 1 : 0);
            AutShadowNative.ResetAbort();
            sInLocal = true;
            try
            {
                Sim actor = q.Actor;
                int m = q.LSrc;
                if ((m & 1 << AutShadowLocalCore.kSrcLotMap) != 0 && !LocalMap(q, r, set, q.LLot.Map)) return;
                if ((m & 1 << AutShadowLocalCore.kSrcOwnMap) != 0 && !LocalMap(q, r, set, actor.SimCommodityInteractionMap)) return;
                if ((m & 1 << AutShadowLocalCore.kSrcOtherSims) != 0)
                    for (int i = 0; i < q.LSims.Count; i++) if (!LocalMap(q, r, set, q.LSims[i].SimCommodityInteractionMap)) return;
                if ((m & 1 << AutShadowLocalCore.kSrcInventory) != 0 && actor.Inventory != null
                    && !LocalMap(q, r, set, actor.Inventory.CommodityInteractionMap)) return;
                if ((m & 1 << AutShadowLocalCore.kSrcSituations) != 0 && !LocalList(q, r, set, q.LSit, false)) return;
                if ((m & 1 << AutShadowLocalCore.kSrcSolo) != 0 && !LocalList(q, r, set, actor.SoloInteractions, true)) return;
                r.Done = true;
            }
            catch (Exception) { r.Exceptions++; r.Done = false; }  // the walk itself (not a pair) failed: no result
            finally { sInLocal = false; }
        }

        // B11 over one map (generic: every kind in q.LKinds order; commodity search: c alone). False = stopped.
        static bool LocalMap(Request q, Result r, LocalPairSet set, CommodityInteractionMap map)
        {
            if (map == null) return true;
            if (q.C != CommodityKind.None) return LocalList(q, r, set, map.InteractionsForCommodity(q.C), false);
            for (int k = 0; k < q.LKindN; k++)
                if (!LocalList(q, r, set, map.InteractionsForCommodity((CommodityKind)q.LKinds[k]), false)) return false;
            return true;
        }

        // B12 / situations / solo over one list. solo: B9's solo filter (generic, or the pair's tradeoff satisfies c).
        static bool LocalList(Request q, Result r, LocalPairSet set, List<InteractionObjectPair> list, bool solo)
        {
            if (list == null) return true;
            for (int j = 0; j < list.Count; j++)
            {
                InteractionObjectPair iop = list[j];
                if (iop == null) continue;
                if (solo && q.C != CommodityKind.None && (iop.Tradeoff == null || !iop.Tradeoff.SatisfiesCommodity(q.C))) continue;
                if (OnWorker() && AutShadowNative.Stopped()) { r.Partial = true; r.Done = sKeepPartial; return false; }
                int add = set.Add(iop);
                if (add == AutShadowLocalCore.kDup) { r.LDup++; continue; }
                if (add == AutShadowLocalCore.kCap) { r.LCap++; continue; }
                int i = r.LN++;
                r.LPair[i] = iop; r.Score[i] = 0f; r.Reason[i] = -1;
                InteractionDefinition d = iop.InteractionDefinition;
                if (d == null || !sLocalTypes.ContainsKey(d.GetType())) { r.Code[i] = AutShadowCore.kSkip; continue; }
                try
                {
                    AutShadowNative.RngUnit(kLocalUnit | i);  // docs/125: one stream per pair
                    r.Code[i] = ScorePair(q, -1, iop, out r.Score[i]);
                }
                catch (Exception) { r.Exceptions++; r.Code[i] = AutShadowCore.kAbort; r.Reason[i] = kReasonExc; }  // never the B12 map removal
                int ab = AutShadowNative.Aborted();
                if (ab >= 0) { r.Code[i] = AutShadowCore.kAbort; r.Reason[i] = ab; AutShadowNative.ResetAbort(); }
                // KeepPartial per pair prefix: the pair counts only if the stop was still clear after its last read
                if (OnWorker() && AutShadowNative.Stopped())
                {
                    r.Code[i] = AutShadowCore.kAbort; r.Reason[i] = kReasonStop;
                    r.Partial = true; r.Done = sKeepPartial;
                    return false;
                }
            }
            return true;
        }

        // H8 inside ScoreLocal (AutCacheHook): no-op for the request's own autonomy with an unchanged C0, else abort `cache`
        static void LocalCache(object aut)
        {
            if (sReq.T2Hh) { AutShadowNative.Abort(kReasonOther); return; }  // household, selected or unknown scope not lifted: abort, uncounted
            bool own = aut == (object)sReq.Aut;
            if (own)
            {
                AutonomyT a = (AutonomyT)aut;
                sLocC0.OnFire = a.IsOnFire; sLocC0.BabyComing = a.BabyIsComing; sLocC0.Neat = a.IsNeat; sLocC0.PartyGuest = a.IsGuestAtAParty;
                sLocC0.Scuba = a.IsActorScubaDiving; sLocC0.FireOnLot = a.IsFireOnLot; sLocC0.TombRoom = a.IsActorInTombRoom;
                sLocC0.GroupSituation = fLocGroup != null ? fLocGroup.GetValue(a) : null; sLocC0.HouseholdMembers = a.CachedSimHouseholdMembers;
            }
            // the cache was on for this search's B9 (B7 sets it before B9 [verified]), so cacheActiveAtRequest is true
            if (AutShadowLocalCore.LocalCacheNoop(own, true, own && sLocC0.Same(sReq.LC0))) { if (sCurRes != null) sCurRes.LNoop++; return; }
            if (sCurRes != null) sCurRes.LCacheAb++;
            AutShadowNative.Abort(kReasonOther);
        }

        // ---- piece 5 (L5 + L6): Mode R compare and the game report ----
        // Counters 1185-1199 (fixes-1185, agent aut_lp_*): [1185-1190] A0..A5 over local pairs (reference vs worker by walk
        // index, Mode 1 only; off-list skips on both sides have no class; A5 = the worker stopped before that index), [1191]
        // walk-order misses (the two walks hold different pairs at an index, or one walk is longer without a stop), [1192-1196]
        // game report over worker pairs the game's own B9 C2 also scored (AutShadowCore.Report): pairs, bit-equal scores, Test
        // agree, requests with a top 1, top 1 agree; [1197] game C2 order misses against the worker walk, [1198] game C2 pairs
        // not in the worker walk, [1199] searches whose game record hit kMaxLocalPairs. Lines in autshadow.jsonl: {"t":"local"}
        // per used worker result (cap kMaxLocalLines a process) and {"t":"LA<k>"} per non-A0 pair (the meta log cap, sLogged).
        // The game record is the game's C2 calls of B9 inside the open B7 window (AutScoreHook), same score returned, and only
        // with HouseholdToo=1 at the open; the replay walks the live maps at the cut, so a map change between B9 and the cut
        // shows as game-only pairs or order misses, not as a bug.
        const int kCLpA = 1185, kCLpWalk = 1191, kCLpG = 1192, kCLpGOrder = 1197, kCLpGOnly = 1198, kCLpGCap = 1199;
        const int kMaxLocalLines = 2000;
        static bool sLocRec;  // the open B7 window records the game's C2
        static readonly List<InteractionObjectPair> sLocGP = new List<InteractionObjectPair>();
        static readonly List<float> sLocGS = new List<float>();
        static readonly Dictionary<InteractionObjectPair, int> sLocWorkIdx = new Dictionary<InteractionObjectPair, int>();
        static readonly int[] sLocGIdx = new int[AutShadowLocalCore.kMaxLocalPairs];
        static readonly float[] sLocGScore = new float[AutShadowLocalCore.kMaxLocalPairs];
        static readonly bool[] sLocGHas = new bool[AutShadowLocalCore.kMaxLocalPairs];
        static readonly int[] sLocA = new int[6];
        static int sLocLines;

        // AutScoreHook inside the open B7 window (sim thread, the game's own B9): record only
        static void LocalGameScored(InteractionObjectPair iop, float s)
        {
            if (sLocGP.Count < AutShadowLocalCore.kMaxLocalPairs) { sLocGP.Add(iop); sLocGS.Add(s); return; }
            if (sLocGP.Count == AutShadowLocalCore.kMaxLocalPairs) { PatchCounters()[kCLpGCap]++; sLocGP.Add(null); sLocGS.Add(0f); }  // once per search
        }

        // a used worker result (sim thread, inside LocalFinish): cmp = the reference ran on this request (Mode 1)
        static void LocalCompare(int[] c, bool cmp)
        {
            int walk = 0;
            Array.Clear(sLocA, 0, sLocA.Length);
            if (cmp)
            {
                int n = Math.Min(sRef.LN, sWork.LN), m = Math.Max(sRef.LN, sWork.LN);
                for (int i = 0; i < n; i++)
                {
                    int k = AutShadowLocalCore.LocalClass(sRef.LPair[i].Equals(sWork.LPair[i]), sRef.Code[i], sRef.Score[i], sWork.Code[i], sWork.Score[i]);
                    if (k == AutShadowCore.kNoClass) continue;
                    if (k == AutShadowLocalCore.kWalkMiss) { walk++; continue; }
                    sLocA[k]++;
                    if (k != AutShadowCore.A0 && sLogged < kMaxLogPerLoad) { sLogged++; LogLocalPair(i, k); }
                }
                for (int i = n; i < m; i++) { if (sWork.Partial && i >= sWork.LN) sLocA[AutShadowCore.A5]++; else walk++; }
                for (int k = 0; k < 6; k++) c[kCLpA + k] += sLocA[k];
                c[kCLpWalk] += walk;
            }
            // the game's own C2 of this search against the worker walk (value equality, first index wins)
            sLocWorkIdx.Clear();
            for (int i = 0; i < sWork.LN; i++) if (!sLocWorkIdx.ContainsKey(sWork.LPair[i])) sLocWorkIdx[sWork.LPair[i]] = i;
            int gn = Math.Min(sReq.LGame.Count, AutShadowLocalCore.kMaxLocalPairs);
            Array.Clear(sLocGHas, 0, sWork.LN);
            for (int g = 0; g < gn; g++)
            {
                InteractionObjectPair p = sReq.LGame[g];
                int w;
                if (p != null && sLocWorkIdx.TryGetValue(p, out w))
                {
                    sLocGIdx[g] = w;
                    if (!sLocGHas[w]) { sLocGHas[w] = true; sLocGScore[w] = sReq.LGameS[g]; }
                }
                else sLocGIdx[g] = -1;
            }
            sLocWorkIdx.Clear();
            int only, order = AutShadowLocalCore.GameOrderMisses(sLocGIdx, gn, out only);
            AutShadowCore.LReport r = AutShadowCore.Report(sLocGScore, sLocGHas, sWork.Score, sWork.Code, sWork.LN);
            c[kCLpG] += r.Pairs; c[kCLpG + 1] += r.Equal; c[kCLpG + 2] += r.TestAgree;
            if (r.Top1Valid) c[kCLpG + 3]++;
            if (r.Top1Agree) c[kCLpG + 4]++;
            c[kCLpGOrder] += order; c[kCLpGOnly] += only;
            if (sLocLines >= kMaxLocalLines) return;
            sLocLines++;
            StringBuilder s = new StringBuilder(320);
            s.Append("{\"t\":\"local\",\"req\":").Append(sReq.Id).Append(",\"actor\":").Append(sReq.Actor.ObjectId.Value)
             .Append(",\"hh\":").Append(sReq.Hh ? 1 : 0).Append(",\"c\":").Append((int)sReq.C).Append(",\"src\":").Append(sReq.LSrc)
             .Append(",\"ref\":").Append(cmp ? sRef.LN : -1).Append(",\"work\":").Append(sWork.LN).Append(",\"partial\":").Append(sWork.Partial ? 1 : 0)
             .Append(",\"a\":");
            if (cmp) s.Append('[').Append(sLocA[0]).Append(',').Append(sLocA[1]).Append(',').Append(sLocA[2]).Append(',').Append(sLocA[3])
                .Append(',').Append(sLocA[4]).Append(',').Append(sLocA[5]).Append("],\"walk\":").Append(walk);
            else s.Append("null");
            s.Append(",\"game\":").Append(gn).Append(",\"gpairs\":").Append(r.Pairs).Append(",\"gequal\":").Append(r.Equal)
             .Append(",\"gtest\":").Append(r.TestAgree).Append(",\"gtop1\":").Append(r.Top1Valid ? (r.Top1Agree ? 1 : 0) : -1)
             .Append(",\"gorder\":").Append(order).Append(",\"gonly\":").Append(only).Append('}');
            AutShadowNative.Log(s.ToString());
        }

        static void LogLocalPair(int i, int k)
        {
            try
            {
                InteractionObjectPair p = sWork.LPair[i];
                StringBuilder s = new StringBuilder(320);
                s.Append("{\"t\":\"LA").Append(k).Append("\",\"req\":").Append(sReq.Id).Append(",\"actor\":").Append(sReq.Actor.ObjectId.Value)
                 .Append(",\"hh\":").Append(sReq.Hh ? 1 : 0).Append(",\"i\":").Append(i)
                 .Append(",\"def\":\"").Append(p.InteractionDefinition != null ? p.InteractionDefinition.GetType().FullName : "")
                 .Append("\",\"target\":").Append(p.Target != null ? p.Target.ObjectId.Value : 0UL);
                AutNum(s, "ref", sRef.Score[i]); AutNum(s, "work", sWork.Score[i]);
                s.Append(",\"refCode\":").Append(sRef.Code[i]).Append(",\"workCode\":").Append(sWork.Code[i])
                 .Append(",\"refReason\":").Append(sRef.Reason[i]).Append(",\"workReason\":").Append(sWork.Reason[i]).Append('}');
                AutShadowNative.Log(s.ToString());
            }
            catch (Exception) { }
        }

        // AutFinish for a local request (sim thread): counters, then drop every reference the request and results hold
        static void LocalFinish(int[] c, int lr)
        {
            try
            {
                bool refOk = sRef.Id == sReq.Id && sRef.Done, workOk = sWork.Id == sReq.Id && sWork.Done && (lr == 0 || (lr == 1 && sKeepPartial && sWork.Partial));
                c[kCLpExc] += sRef.Id == sReq.Id ? sRef.Exceptions : 0;
                if (refOk) { c[kCLpRefDone]++; c[kCLpRefPairs] += sRef.LN; c[kCLpNoop] += sRef.LNoop; c[kCLpCacheAb] += sRef.LCacheAb; }
                if (!workOk) { c[kCLpDropped]++; }
                else
                {
                    c[kCLpWorkDone]++;
                    c[kCLpPairs] += sWork.LN; c[kCLpDup] += sWork.LDup; c[kCLpCap] += sWork.LCap;
                    c[kCLpNoop] += sWork.LNoop; c[kCLpCacheAb] += sWork.LCacheAb;
                    for (int i = 0; i < sWork.LN; i++)
                    {
                        int k = sWork.Code[i];
                        if (k == AutShadowCore.kSkip) c[kCLpSkip]++;
                        else if (k == AutShadowCore.kPass) c[kCLpPass]++;
                        else if (k == AutShadowCore.kTestFailed) c[kCLpFail]++;
                        else if (k == AutShadowCore.kAbort) { c[kCLpAbort]++; if (sWork.Reason[i] == kReasonExc) c[kCLpPairExc]++; }
                    }
                    LocalCompare(c, refOk && sReq.Mode == 1);
                }
            }
            catch (Exception) { c[kCLpExc]++; }
            if (sRef.Id == sReq.Id) Array.Clear(sRef.LPair, 0, Math.Min(sRef.LN, sRef.LPair.Length));
            if (sWork.Id == sReq.Id) Array.Clear(sWork.LPair, 0, Math.Min(sWork.LN, sWork.LPair.Length));
            sLocSetRef.Clear(); sLocSetWork.Clear();
            LocalClear(sReq);
            sReq.Local = false; sReq.Actor = null; sReq.Aut = null;
        }
    }
}
