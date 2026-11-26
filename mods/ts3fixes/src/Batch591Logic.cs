using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.ActiveCareer.ActiveCareers;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.EventSystem;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.ObjectComponents;
using Sims3.Gameplay.Objects.CookingObjects;
using Sims3.Gameplay.Objects.Environment;
using Sims3.Gameplay.Objects.Miscellaneous;
using Sims3.Gameplay.Situations;
using Sims3.Gameplay.Skills;
using Sims3.Gameplay.Socializing;
using Sims3.Gameplay.TuningValues;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // Batch 12b (docs/591 usage sweep 3, callees of ranks 21-100 and ranks 201-378): EA bug fixes as EA intended,
    // household included. All default off. Slots 3115-3171. simler90 UPDATE242 leaves every hooked method as EA.
    // Not built: C3 (false positive: the shipped tuning has 6 chess ranks), C8 (in compositionnull), R4 (the NRE sits in
    // the middle of a 200-line private method: IL patch queued), R8, R14, R19 (V/I, effect unconfirmed), R15 (false positive:
    // the first plan never dereferences r), R17 (several sites, own pass), R18 (inferred: needs a species with no slot data at
    // a portal; a pre-check doubles a per-door call), R20 (mid-Run, IL patch queued), R21 VisitSituation (in shoodef),
    // R22/R23 (one-line leads without notes).
    public static partial class Fixes
    {
        static FieldInfo B12FUp(Type t, string n)
        {
            for (; t != null; t = t.BaseType) { FieldInfo f = t.GetField(n, kB11All | BindingFlags.DeclaredOnly); if (f != null) return f; }
            return null;
        }

        // ---- B12 §18 awkwardscience (docs/591 C1) --------------------------------------------------------------------
        // [verified, decompiled] Skill.ComputeSkillGainMod adds TraitTuning.SociallyAwkwardScienceSkillGainMod to every
        // skill of a Socially Awkward Sim who has the Science skill; the bonus is the Science one (its own name). Fix: the
        // bonus is taken back off for every other skill, with EA's -1 floor kept. Counters: [3115] gains corrected,
        // [3116] bonus on another skill (switch off), [3117] errors.
        static readonly FieldInfo sAsOwner = B12F(typeof(Skill), "mSkillOwner");
        static string sAsM12 = "";

        static void ApplyAwkwardScience()
        {
            sAsM12 = SpNames("Skill.mSkillOwner", sAsOwner);
            if (sAsM12.Length > 0) { AppliedLog().Add("awkwardscience: not found: " + sAsM12); return; }
            Patch(typeof(Skill), "ComputeSkillGainMod", Type.EmptyTypes, typeof(B12AsHook), "GainHook", "GainOrig");
        }

        // Pure: EA's result r (1 + max(-1, sum)) without the science bonus on a non-science skill.
        internal static float AsGain(float r, float mod, bool awkwardWithScience, bool isScience)
        {
            if (!awkwardWithScience || isScience) return r;
            if (r <= 0f) return mod >= 0f ? 0f : r;  // already at EA's floor: removing a bonus keeps it there
            return Math.Max(0f, r - mod);
        }

        public class B12AsHook
        {
            public float GainHook()
            {
                float r = GainOrig();
                try
                {
                    Skill sk = (Skill)(object)this;
                    SimDescription o = sAsOwner.GetValue(sk) as SimDescription;
                    if (o == null || sk.Guid == SkillNames.Science || o.TraitManager == null || !o.TraitManager.HasElement(TraitNames.SociallyAwkward)) return r;
                    if (o.SkillManager == null || o.SkillManager.GetSkill<ScienceSkill>(SkillNames.Science) == null) return r;
                    if (!ShortcutOn("awkwardscience")) { PatchCounters()[3116]++; return r; }
                    PatchCounters()[3115]++;
                    return AsGain(r, TraitTuning.SociallyAwkwardScienceSkillGainMod, true, false);
                }
                catch (Exception) { PatchCounters()[3117]++; return r; }
            }

            public float GainOrig() { return 0f; }
        }

        // ---- B12 §19 chessdefeated (docs/591 C2) ---------------------------------------------------------------------
        // [verified, decompiled] LogicSkill.UpdateChessTournamentStatistics keeps the "highest ranking defeated opponent"
        // when the stored rank is >= the opponent's (so a weaker opponent replaces a stronger one) and stores the
        // opponent's own highest-defeated rank instead of the opponent's chess rank. Fix: a beaten opponent of the same or
        // higher rank is stored with its rank, then EA's FindSuitableChessRankOpponent runs. Counters: [3118] wins recorded
        // our way, [3119] EA's record (switch off), [3120] errors.
        static readonly FieldInfo sCdRank = B12F(typeof(LogicSkill), "mChessRank");
        static readonly FieldInfo sCdBest = B12F(typeof(LogicSkill), "mRankOfHighestRankingDefeatedOpponent");
        static readonly FieldInfo sCdWho = B12F(typeof(LogicSkill), "mChessHighestRankingDefeatedOpponent");
        static readonly MethodInfo sCdFind = B12M(typeof(LogicSkill), "FindSuitableChessRankOpponent");
        static string sCdM12 = "";

        static void ApplyChessDefeated()
        {
            sCdM12 = SpNames("LogicSkill.mChessRank", sCdRank, "mRankOfHighestRankingDefeatedOpponent", sCdBest, "mChessHighestRankingDefeatedOpponent", sCdWho, "FindSuitableChessRankOpponent", sCdFind);
            if (sCdM12.Length > 0) { AppliedLog().Add("chessdefeated: not found: " + sCdM12); return; }
            Patch(typeof(LogicSkill), "UpdateChessTournamentStatistics", new Type[] { typeof(SimDescription) }, typeof(B12CdHook), "UpdateHook", "UpdateOrig");
        }

        internal static bool CdBetter(int stored, int opponentRank) { return opponentRank >= stored; }

        public class B12CdHook
        {
            public void UpdateHook(SimDescription sd)
            {
                if (!ShortcutOn("chessdefeated")) { PatchCounters()[3119]++; UpdateOrig(sd); return; }
                if (sd == null || sd.SkillManager == null) return;
                try
                {
                    LogicSkill opp = sd.SkillManager.GetSkill<LogicSkill>(SkillNames.Logic);
                    if (opp != null)
                    {
                        int rank = (int)sCdRank.GetValue(opp);
                        if (CdBetter((int)sCdBest.GetValue(this), rank)) { sCdWho.SetValue(this, sd); sCdBest.SetValue(this, rank); }
                    }
                    PatchCounters()[3118]++;
                    sCdFind.Invoke(this, null);
                }
                catch (Exception) { PatchCounters()[3120]++; }
            }

            public void UpdateOrig(SimDescription sd) { }
        }

        // ---- B12 §20 privacyothers (docs/591 C4) ---------------------------------------------------------------------
        // [verified, decompiled] GenericPrivacySituation.Undisturbed.GenericPrivacyIntruderCheck (every minute) removes the
        // room checks of a Sim in OtherSims who left the lot, but leaves the Sim in OtherSims: the checks are removed again
        // every minute, and when that Sim comes back it is "known" and gets no new room check, so it can walk in freely.
        // Fix: after EA's check, Sims that left the lot leave OtherSims too. Counters: [3121] Sims dropped, [3122] kept
        // (switch off), [3123] errors.
        static readonly Type sPoUnd = typeof(GenericPrivacySituation).GetNestedType("Undisturbed", BindingFlags.NonPublic);
        static string sPoM12 = "";

        static void ApplyPrivacyOthers()
        {
            sPoM12 = SpNames("GenericPrivacySituation+Undisturbed", sPoUnd);
            if (sPoM12.Length > 0) { AppliedLog().Add("privacyothers: not found: " + sPoM12); return; }
            Patch(sPoUnd, "GenericPrivacyIntruderCheck", Type.EmptyTypes, typeof(B12PoHook), "CheckHook", "CheckOrig");
        }

        internal static bool PoGone(bool onLot) { return !onLot; }

        public class B12PoHook
        {
            public void CheckHook()
            {
                CheckOrig();
                try
                {
                    GenericPrivacySituation p = ((ChildSituation<GenericPrivacySituation>)(object)this).Parent;
                    if (p == null || p.Lot == null || p.SyncTarget == null || p.OtherSims.Count == 0) return;
                    List<Sim> here = new List<Sim>(p.Lot.GetSims());
                    for (int i = p.OtherSims.Count - 1; i >= 0; i--)
                    {
                        Sim s = p.OtherSims[i];
                        if (!PoGone(s != null && here.Contains(s))) continue;
                        if (ShortcutOn("privacyothers")) { p.OtherSims.RemoveAt(i); PatchCounters()[3121]++; }
                        else PatchCounters()[3122]++;
                    }
                }
                catch (Exception) { PatchCounters()[3123]++; }
            }

            public void CheckOrig() { }
        }

        // ---- B12 §21 gigmaster (docs/591 C5) -------------------------------------------------------------------------
        // [verified, decompiled + shipped tuning] RockBand.OppGigMasterCompleted is `gigs > kNumGigsToBecomeGigMaster`
        // (tuning 15) while the reward text (RewardDescription) says that number of gigs: 16 are needed. Fix: `>=`.
        // Counters: [3124] answers our way, [3125] off by one (switch off, at exactly the number), [3126] errors.
        static readonly FieldInfo sGmNeed = B12F(typeof(RockBand), "kNumGigsToBecomeGigMaster");
        static string sGmM12 = "";

        static void ApplyGigMaster()
        {
            sGmM12 = SpNames("RockBand.kNumGigsToBecomeGigMaster", sGmNeed);
            if (sGmM12.Length > 0) { AppliedLog().Add("gigmaster: not found: " + sGmM12); return; }
            Patch(typeof(RockBand), "get_OppGigMasterCompleted", Type.EmptyTypes, typeof(B12GmHook), "DoneHook", "DoneOrig");
        }

        internal static bool GmDone(bool fixedRule, int gigs, int need) { return fixedRule ? gigs >= need : gigs > need; }

        public class B12GmHook
        {
            public bool DoneHook()
            {
                try
                {
                    int gigs = ((RockBand)(object)this).GetTotalGigsPlayed(), need = (int)sGmNeed.GetValue(null);
                    bool on = ShortcutOn("gigmaster");
                    if (gigs == need) PatchCounters()[on ? 3124 : 3125]++;
                    return GmDone(on, gigs, need);
                }
                catch (Exception) { PatchCounters()[3126]++; return DoneOrig(); }
            }

            public bool DoneOrig() { return false; }
        }

        // ---- B12 §22 intmultiple (docs/591 C6, docs/592 S4) ----------------------------------------------------------
        // [verified, decompiled] RandomUtil.GetIntMultiple(min, max, base) divides as ints before Ceiling/Floor and returns
        // (int)GetDouble(lo, hi) * base, GetDouble being [lo, hi): the top multiple never comes (sales 10..75 instead of
        // 10..80, screenplays 0..1400 of 1500, Absent-Minded / Nervous / Clueless idles 10..29 of 30) and a min that is not
        // a multiple rounds down. Fix: ceil(min / base) .. floor(max / base) in floating point, drawn with EA's inclusive
        // GetInt (one random draw, as EA). Counters: [3127] draws our way, [3128] EA's draws (switch off), [3129] errors.
        static void ApplyIntMultiple()
        {
            Patch(typeof(RandomUtil), "GetIntMultiple", new Type[] { typeof(int), typeof(int), typeof(int) }, typeof(B12ImHook), "MultHook", "MultOrig");
        }

        // Pure: the inclusive multiple range [lo, hi] (in units of baseMultiple).
        internal static void ImRange(int min, int max, int baseMultiple, out int lo, out int hi)
        {
            lo = (int)Math.Ceiling((double)min / baseMultiple);
            hi = (int)Math.Floor((double)max / baseMultiple);
        }

        public class B12ImHook
        {
            public static int MultHook(int min, int max, int baseMultiple)
            {
                if (baseMultiple <= 0 || !ShortcutOn("intmultiple")) { PatchCounters()[3128]++; return MultOrig(min, max, baseMultiple); }
                try
                {
                    int lo, hi;
                    ImRange(min, max, baseMultiple, out lo, out hi);
                    PatchCounters()[3127]++;
                    return (hi < lo ? lo : RandomUtil.GetInt(lo, hi)) * baseMultiple;
                }
                catch (Exception) { PatchCounters()[3129]++; return MultOrig(min, max, baseMultiple); }
            }

            public static int MultOrig(int min, int max, int baseMultiple) { return 0; }
        }

        // ---- B12 §23 doorprivacynull (docs/591 C7) -------------------------------------------------------------------
        // [verified, decompiled] Lot.UpdateDoorPrivacy (every lot Simulate tick while flagged) collects the rooms of every
        // PrivacySituation handler inside one try/catch: one with a null SyncTarget throws and every later situation's room
        // is lost, so their doors lose privacy. Fix: our copy skips null SyncTargets (and uses a set, not IndexOf per door).
        // Counters: [3130] updates with a null target skipped, [3131] updates that threw (switch off), [3132] errors.
        static readonly FieldInfo sDpNeed = B12F(typeof(Lot), "mbNeedPrivacyUpdate");
        static readonly FieldInfo sDpHandler = B12F(typeof(Lot), "mOnPortalPathPlanHandler");
        static string sDpM12 = "";

        static void ApplyDoorPrivacyNull()
        {
            sDpM12 = SpNames("Lot.mbNeedPrivacyUpdate", sDpNeed, "Lot.mOnPortalPathPlanHandler", sDpHandler);
            if (sDpM12.Length > 0) { AppliedLog().Add("doorprivacynull: not found: " + sDpM12); return; }
            Patch(typeof(Lot), "UpdateDoorPrivacy", Type.EmptyTypes, typeof(B12DpHook), "UpdateHook", "UpdateOrig");
        }

        // Pure: the private rooms from the handlers' sync-target rooms (null = no sync target); nullSeen when EA would throw.
        internal static Dictionary<int, bool> DpRooms(List<int?> rooms, out bool nullSeen)
        {
            Dictionary<int, bool> set = new Dictionary<int, bool>();
            nullSeen = false;
            foreach (int? r in rooms) { if (r == null) nullSeen = true; else set[r.Value] = true; }
            return set;
        }

        public class B12DpHook
        {
            public void UpdateHook()
            {
                Lot lot = (Lot)(object)this;
                if (!(bool)sDpNeed.GetValue(lot)) return;
                if (!ShortcutOn("doorprivacynull")) { UpdateOrig(); return; }
                try
                {
                    Door[] doors = lot.GetObjects<Door>();
                    Delegate h = sDpHandler.GetValue(lot) as Delegate;
                    if (h == null) { foreach (Door d in doors) d.DisablePrivacy(); }
                    else
                    {
                        List<int?> rooms = new List<int?>();
                        foreach (Delegate d in h.GetInvocationList())
                        {
                            PrivacySituation ps = d.Target as PrivacySituation;
                            if (ps != null) rooms.Add(ps.SyncTarget == null ? (int?)null : ps.SyncTarget.RoomId);
                        }
                        bool nullSeen;
                        Dictionary<int, bool> set = DpRooms(rooms, out nullSeen);
                        if (nullSeen) PatchCounters()[3130]++;
                        foreach (Door d in doors) { if (set.ContainsKey(d.RoomId)) d.EnablePrivacy(); else d.DisablePrivacy(); }
                    }
                    sDpNeed.SetValue(lot, false);
                }
                catch (Exception) { PatchCounters()[3132]++; }
            }

            public void UpdateOrig() { }
        }

        // ---- B12 §24 leafburnfail (docs/591 R1) ----------------------------------------------------------------------
        // [verified, decompiled] LeafPile.Burn.Run sets mBeingBurned before FireManager.AddFire and ignores its result (no
        // fire spot, a lot that allows no fire); mBeingBurned makes InUse true and blocks every pile interaction, and only
        // the fire destroying the pile ever ends it. Fix: a Run that set the flag and left the pile intact and not burning
        // clears it. Counters: [3133] piles freed, [3134] left blocked (switch off), [3135] errors.
        static readonly Type sLbBurn = typeof(LeafPile).GetNestedType("Burn", BindingFlags.NonPublic);
        static readonly FieldInfo sLbFlag = B12F(typeof(LeafPile), "mBeingBurned");
        static string sLbM12 = "";

        static void ApplyLeafBurnFail()
        {
            sLbM12 = SpNames("LeafPile+Burn", sLbBurn, "LeafPile.mBeingBurned", sLbFlag);
            if (sLbM12.Length > 0) { AppliedLog().Add("leafburnfail: not found: " + sLbM12); return; }
            Patch(sLbBurn, "Run", Type.EmptyTypes, typeof(B12LbHook), "RunHook", "RunOrig");
        }

        internal static bool LbClear(bool hadFlag, bool flagNow, bool destroyed, bool burning) { return !hadFlag && flagNow && !destroyed && !burning; }

        public class B12LbHook
        {
            public bool RunHook()
            {
                LeafPile pile = ((InteractionInstance)(object)this).Target as LeafPile;
                bool had = pile != null && (bool)sLbFlag.GetValue(pile);
                bool r = RunOrig();
                try
                {
                    if (pile == null || !LbClear(had, (bool)sLbFlag.GetValue(pile), pile.HasBeenDestroyed, Sims3.Gameplay.Controllers.FireManager.IsObjectBurning(pile))) return r;
                    if (ShortcutOn("leafburnfail")) { sLbFlag.SetValue(pile, false); PatchCounters()[3133]++; }
                    else PatchCounters()[3134]++;
                }
                catch (Exception) { PatchCounters()[3135]++; }
                return r;
            }

            public bool RunOrig() { return false; }
        }

        // ---- B12 §25 catchpreyodds (docs/591 R2) ---------------------------------------------------------------------
        // [verified, decompiled] CatHuntingComponent.CatchPrey.RollDiceForCatchingPrey: `(opp && lizard) || snake || turtle`
        // returns true for every snake and turtle; the Slither Stalker opportunity is what makes the reptiles a sure catch.
        // Fix: without the opportunity a snake or turtle takes EA's skill roll (no beetle bonus applies to them). Counters:
        // [3136] rolls taken, [3137] sure catches (switch off), [3138] errors.
        static readonly Type sCpType = typeof(CatHuntingComponent).GetNestedType("CatchPrey", BindingFlags.NonPublic);
        static readonly FieldInfo sCpSkill = B12F(sCpType, "mHuntingSkill");
        static readonly FieldInfo sCpChance = B12F(sCpType, "kMinMaxChanceCatchPrey");
        static readonly MethodInfo sCpAdjust = B12M(typeof(CatHuntingComponent), "GetTraitAdjustedChance", typeof(Sim), typeof(int[]));
        static string sCpM12 = "";

        static void ApplyCatchPreyOdds()
        {
            sCpM12 = SpNames("CatHuntingComponent+CatchPrey", sCpType, "mHuntingSkill", sCpSkill, "kMinMaxChanceCatchPrey", sCpChance, "GetTraitAdjustedChance", sCpAdjust);
            if (sCpM12.Length > 0) { AppliedLog().Add("catchpreyodds: not found: " + sCpM12); return; }
            Patch(sCpType, "RollDiceForCatchingPrey", Type.EmptyTypes, typeof(B12CpHook), "RollHook", "RollOrig");
        }

        // Pure: a sure catch. fixedRule = the opportunity covers all three reptiles (EA: only the lizard).
        internal static bool CpSure(bool fixedRule, bool opp, bool lizard, bool snake, bool turtle)
        {
            return fixedRule ? opp && (lizard || snake || turtle) : (opp && lizard) || snake || turtle;
        }

        public class B12CpHook
        {
            public bool RollHook()
            {
                try
                {
                    InteractionInstance ii = (InteractionInstance)(object)this;
                    ICatPrey prey = ii.Target as ICatPrey;
                    CatHuntingSkill skill = sCpSkill.GetValue(ii) as CatHuntingSkill;
                    CatHuntingComponent.PreyData d = prey == null || prey.CatHuntingComponent == null ? null : prey.CatHuntingComponent.mPreyData;
                    if (d == null || skill == null) return RollOrig();
                    bool snake = d.PreyType == CatHuntingSkill.PreyType.Snake, turtle = d.PreyType == CatHuntingSkill.PreyType.Turtle;
                    bool lizard = d.PreyType == CatHuntingSkill.PreyType.Lizard, opp = skill.OppSlitherStalkerCompleted;
                    if (CpSure(true, opp, lizard, snake, turtle) || !CpSure(false, opp, lizard, snake, turtle)) return RollOrig();
                    if (!ShortcutOn("catchpreyodds")) { PatchCounters()[3137]++; return RollOrig(); }
                    float[] adj = (float[])sCpAdjust.Invoke(null, new object[] { ii.InstanceActor as Sim, sCpChance.GetValue(null) });
                    float num = MathHelpers.LinearInterpolate(d.MinSkillLevel, d.MaxSkillLevel, adj[0], adj[1], skill.SkillLevel);
                    PatchCounters()[3136]++;
                    return RandomUtil.RandomChance(num);
                }
                catch (Exception) { PatchCounters()[3138]++; return RollOrig(); }
            }

            public bool RollOrig() { return false; }
        }

        // ---- B12 §26 picnicants (docs/591 R3) ------------------------------------------------------------------------
        // [verified, decompiled] PicnicBasket.DoCleanUp (pack up) hides the ants with Stop() and never disposes the effect,
        // and nothing removes mAntStartAlarmHandle (an AlwaysPersisted lot alarm): ants come back on a packed basket, and
        // each hide leaks the effect. Fix: HideAnts disposes the effect, and DoCleanUp / Dispose remove the ant alarm.
        // Counters: [3139] cleanups done, [3140] left (switch off), [3141] errors.
        static readonly FieldInfo sPaFx = B12F(typeof(PicnicBasket), "mAntFX");
        static readonly FieldInfo sPaAlarm = B12F(typeof(PicnicBasket), "mAntStartAlarmHandle");
        static string sPaM12 = "";

        static void ApplyPicnicAnts()
        {
            sPaM12 = SpNames("PicnicBasket.mAntFX", sPaFx, "mAntStartAlarmHandle", sPaAlarm);
            if (sPaM12.Length > 0) { AppliedLog().Add("picnicants: not found: " + sPaM12); return; }
            Patch(typeof(PicnicBasket), "HideAnts", Type.EmptyTypes, typeof(B12PaHook), "HideHook", "HideOrig");
            Patch(typeof(PicnicBasket), "DoCleanUp", Type.EmptyTypes, typeof(B12PaHook), "CleanHook", "CleanOrig");
            Patch(typeof(PicnicBasket), "Dispose", Type.EmptyTypes, typeof(B12PaHook), "DisposeHook", "DisposeOrig");
        }

        internal static bool PaAlarmLive(AlarmHandle h) { return h != AlarmHandle.kInvalidHandle; }

        static void PaRemoveAlarm(PicnicBasket b)
        {
            AlarmHandle h = (AlarmHandle)sPaAlarm.GetValue(b);
            if (!PaAlarmLive(h)) return;
            if (!ShortcutOn("picnicants")) { PatchCounters()[3140]++; return; }
            if (b.LotCurrent != null) b.LotCurrent.AlarmManager.RemoveAlarm(h);
            sPaAlarm.SetValue(b, AlarmHandle.kInvalidHandle);
            PatchCounters()[3139]++;
        }

        public class B12PaHook
        {
            public void HideHook()
            {
                VisualEffect fx = sPaFx.GetValue(this) as VisualEffect;
                if (fx == null || !ShortcutOn("picnicants")) { if (fx != null) PatchCounters()[3140]++; HideOrig(); return; }
                try { fx.Stop(VisualEffect.TransitionType.HardTransition); fx.Dispose(); sPaFx.SetValue(this, null); PatchCounters()[3139]++; }
                catch (Exception) { PatchCounters()[3141]++; }
            }

            public void CleanHook()
            {
                CleanOrig();
                try { PaRemoveAlarm((PicnicBasket)(object)this); } catch (Exception) { PatchCounters()[3141]++; }
            }

            public void DisposeHook()
            {
                try { PaRemoveAlarm((PicnicBasket)(object)this); } catch (Exception) { PatchCounters()[3141]++; }
                DisposeOrig();
            }

            public void HideOrig() { }
            public void CleanOrig() { }
            public void DisposeOrig() { }
        }

        // ---- B12 §27 loyaldoglay (docs/591 R5) -----------------------------------------------------------------------
        // [verified, decompiled] PetSurfacePosture.LoyalDogLayDownCallback queues LayDownHere with the BFF Sim as target and
        // the BFF's position; LayDownHere.Definition.Test refuses any Sim target other than the actor when autonomous, so
        // the Loyal dog never goes to lie down near its human. Fix: the dog itself is the target (EA's own pattern in
        // PetGuardSituation), at the BFF's position; the rest of the callback is EA's. Counters: [3142] lay-downs queued,
        // [3143] refused targets (switch off), [3144] errors.
        static readonly FieldInfo sLdLast = B12F(typeof(PetSurfacePosture), "LastSimToLayDownNear");
        static readonly FieldInfo sLdAlarm = B12F(typeof(PetSurfacePosture), "mLoyalDogAlarmHandle");
        static readonly MethodInfo sLdSelf = B12M(typeof(PetSurfacePosture), "LoyalDogLayDownCallback");
        static string sLdM12 = "";

        static void ApplyLoyalDogLay()
        {
            sLdM12 = SpNames("PetSurfacePosture.LastSimToLayDownNear", sLdLast, "mLoyalDogAlarmHandle", sLdAlarm, "LoyalDogLayDownCallback", sLdSelf);
            if (sLdM12.Length > 0) { AppliedLog().Add("loyaldoglay: not found: " + sLdM12); return; }
            Patch(typeof(PetSurfacePosture), "LoyalDogLayDownCallback", Type.EmptyTypes, typeof(B12LdHook), "LayHook", "LayOrig");
        }

        // Pure: the LayDownHere target (EA passes the human).
        internal static Sim LdTarget(bool fixedRule, Sim dog, Sim human) { return fixedRule ? dog : human; }

        public class B12LdHook
        {
            public void LayHook()
            {
                if (!ShortcutOn("loyaldoglay")) { PatchCounters()[3143]++; LayOrig(); return; }
                PetSurfacePosture p = (PetSurfacePosture)(object)this;
                Sim dog = p.Sim;
                try
                {
                    Sim last = sLdLast.GetValue(p) as Sim;
                    if (last == null || last.RoomId != dog.RoomId)
                    {
                        Sim bff = Relationship.GetBFFOrSimWithHighestLikingForPet(dog);
                        if (bff != null && dog.LotCurrent == bff.LotCurrent && dog.RoomId != bff.RoomId)
                        {
                            if (!dog.InteractionQueue.HasInteractionOfType(PetSurfacePosture.LayDownHere.Singleton))
                            {
                                PetSurfacePosture.LayDownHere.Definition def = new PetSurfacePosture.LayDownHere.Definition(PetSurfacePosture.LayDownHere.LayDownType.Laydown, bff.Position);
                                InteractionInstance entry = def.CreateInstance(LdTarget(true, dog, bff), dog, new InteractionPriority(InteractionPriorityLevel.UserDirected), true, true);
                                bool ok = dog.InteractionQueue.AddNext(entry);
                                sLdLast.SetValue(p, ok ? bff : null);
                                if (ok) PatchCounters()[3142]++;
                            }
                        }
                        else sLdLast.SetValue(p, null);
                    }
                }
                catch (Exception) { PatchCounters()[3144]++; }
                // EA re-arms at the end in every case
                sLdAlarm.SetValue(p, dog.AddAlarm(TraitTuning.LoyalDogLayDownTimer, TimeUnit.Minutes, (AlarmTimerCallback)Fixes.Bind(typeof(AlarmTimerCallback), p, sLdSelf), "loyal dog laydown alarm", AlarmType.AlwaysPersisted));
            }

            public void LayOrig() { }
        }

        // ---- B12 §28 visitlisteners (docs/591 R6) --------------------------------------------------------------------
        // [verified, decompiled] VisitSituation.Remove(Sim) removes that Sim's event listeners from EventTracker but keeps
        // the mEventListeners entry; PostWorldFixUp (after every load) re-adds each listener that is not completed, so a
        // removed host's relationship listeners come back and strikes count twice. Fix: the entry goes too. Counters:
        // [3145] entries dropped, [3146] kept (switch off), [3147] errors.
        static readonly FieldInfo sVlMap = B12F(typeof(VisitSituation), "mEventListeners");
        static string sVlM12 = "";

        static void ApplyVisitListeners()
        {
            sVlM12 = SpNames("VisitSituation.mEventListeners", sVlMap);
            if (sVlM12.Length > 0) { AppliedLog().Add("visitlisteners: not found: " + sVlM12); return; }
            Patch(typeof(VisitSituation), "Remove", new Type[] { typeof(Sim) }, typeof(B12VlHook), "RemoveHook", "RemoveOrig");
        }

        internal static bool VlDrop(bool hadEntry) { return hadEntry; }

        public class B12VlHook
        {
            public void RemoveHook(Sim s)
            {
                RemoveOrig(s);
                try
                {
                    Dictionary<Sim, List<EventListener>> map = sVlMap.GetValue(this) as Dictionary<Sim, List<EventListener>>;
                    if (map == null || s == null || !VlDrop(map.ContainsKey(s))) return;
                    if (ShortcutOn("visitlisteners")) { map.Remove(s); PatchCounters()[3145]++; }
                    else PatchCounters()[3146]++;
                }
                catch (Exception) { PatchCounters()[3147]++; }
            }

            public void RemoveOrig(Sim s) { }
        }

        // ---- B12 §29 sciencefxstop (docs/591 R7) ---------------------------------------------------------------------
        // [verified, decompiled] ScienceComponent.StartVisualEffects(size, ...) replaces mVisualEffects with a new set and
        // never stops the old one; ActivateSound(random) calls SoundAlarmCallback (which starts a set) and then
        // StartVisualEffects again, so the first set runs forever. Fix: a set still running is stopped and disposed (EA's
        // StopVisualEffects) before a new one starts. Counters: [3148] old sets stopped, [3149] leaked (switch off),
        // [3150] errors.
        static readonly FieldInfo sSfSet = B12F(typeof(ScienceComponent), "mVisualEffects");
        static readonly MethodInfo sSfStop = B12M(typeof(ScienceComponent), "StopVisualEffects");
        static MethodInfo sSfStart;
        static string sSfM12 = "";

        static void ApplyScienceFxStop()
        {
            foreach (MethodInfo m in typeof(ScienceComponent).GetMethods(kB11All | BindingFlags.DeclaredOnly))
                if (m.Name == "StartVisualEffects" && m.GetParameters().Length == 4) sSfStart = m;
            sSfM12 = SpNames("ScienceComponent.mVisualEffects", sSfSet, "StopVisualEffects", sSfStop, "StartVisualEffects(4)", sSfStart);
            if (sSfM12.Length > 0) { AppliedLog().Add("sciencefxstop: not found: " + sSfM12); return; }
            Type[] sig = Array.ConvertAll(sSfStart.GetParameters(), delegate(ParameterInfo pi) { return pi.ParameterType; });
            Patch(typeof(ScienceComponent), "StartVisualEffects", sig, typeof(B12SfHook), "StartHook", "StartOrig");
        }

        internal static bool SfStopFirst(bool running) { return running; }

        public class B12SfHook
        {
            // (Size size, bool isPositive, bool isAoe, Slot[] slots); Size is a private enum, passed through as an int
            public void StartHook(int size, bool isPositive, bool isAoe, Slot[] slots)
            {
                try
                {
                    if (SfStopFirst(sSfSet.GetValue(this) != null))
                    {
                        if (ShortcutOn("sciencefxstop")) { sSfStop.Invoke(this, null); PatchCounters()[3148]++; }
                        else PatchCounters()[3149]++;
                    }
                }
                catch (Exception) { PatchCounters()[3150]++; }
                StartOrig(size, isPositive, isAoe, slots);
            }

            public void StartOrig(int size, bool isPositive, bool isAoe, Slot[] slots) { }
        }

        // ---- B12 §30 daycareliking (docs/591 R9) ---------------------------------------------------------------------
        // [verified, decompiled] Daycare.GetExperience reads the child's liking from `new Relationship(child, owner)` (a
        // fresh, unregistered relationship: liking 0), so the LTR experience multiplier is always the zero-liking one. Fix:
        // EA's result is moved by (multiplier at the real liking - multiplier at 0) x base experience, the real liking
        // being Relationship.Get(child, owner) (0 when there is none, EA's value). Counters: [3151] payouts corrected,
        // [3152] payouts at liking 0 (switch off), [3153] errors.
        static void ApplyDaycareLiking()
        {
            Patch(typeof(Daycare), "GetExperience", new Type[] { typeof(ulong), typeof(Sims3.Gameplay.ActiveCareer.ActiveCareers.Rating), typeof(string).MakeByRefType() }, typeof(B12DlHook), "XpHook", "XpOrig");
        }

        internal static float DlXp(float eaXp, float baseXp, float multReal, float multEa) { return eaXp + (multReal - multEa) * baseXp; }

        public class B12DlHook
        {
            public float XpHook(ulong childId, Sims3.Gameplay.ActiveCareer.ActiveCareers.Rating rating, out string debug)
            {
                float r = XpOrig(childId, rating, out debug);
                try
                {
                    Daycare dc = (Daycare)(object)this;
                    SimDescription child = SimDescription.Find(childId);
                    if (child == null || dc.OwnerDescription == null) return r;
                    Relationship rel = Relationship.Get(child, dc.OwnerDescription, false);
                    if (rel == null) return r;
                    float eaLiking = new Relationship(child, dc.OwnerDescription).LTR.Liking;
                    if (rel.LTR.Liking == eaLiking) return r;
                    if (!ShortcutOn("daycareliking")) { PatchCounters()[3152]++; return r; }
                    Dictionary<Sims3.Gameplay.ActiveCareer.ActiveCareers.Rating, RatingTuning<float>> ltr = Daycare.sLTRRatingData;
                    float real = RatingTuning<float>.GetData(ref ltr, rel.LTR.Liking), ea = RatingTuning<float>.GetData(ref ltr, eaLiking);
                    PatchCounters()[3151]++;
                    return DlXp(r, Daycare.sOverallRatingData[rating].tRatingData.fExperience, real, ea);
                }
                catch (Exception) { PatchCounters()[3153]++; return r; }
            }

            public float XpOrig(ulong childId, Sims3.Gameplay.ActiveCareer.ActiveCareers.Rating rating, out string debug) { debug = null; return 0f; }
        }

        // ---- B12 §31 kegbreakage (docs/591 R10) ----------------------------------------------------------------------
        // [verified, decompiled] RepairableComponent.UpdateBreakage applies JuiceKeggerParty.kPercentageMultiplierForObjectsBreaking
        // to every WildParty on the lot whenever EP9 is installed (the same party test as the EP4 wild-party multiplier),
        // so every wild party breaks objects at the kegger rate too. Fix: our copy applies it only when a JuiceKeggerParty
        // runs on the object's lot, and scans the lot's parties once. Counters: [3154] chances computed our way, [3155]
        // kegger rate on a non-kegger party (switch off), [3156] errors.
        static readonly MethodInfo sKbChance = B12M(typeof(RepairableComponent), "ChanceOfBreak");
        static readonly FieldInfo sKbDorm = B12F(typeof(RepairableComponent), "kDormLotBreakageMult");
        static string sKbM12 = "";

        static void ApplyKegBreakage()
        {
            sKbM12 = SpNames("RepairableComponent.ChanceOfBreak", sKbChance, "kDormLotBreakageMult", sKbDorm);
            if (sKbM12.Length > 0) { AppliedLog().Add("kegbreakage: not found: " + sKbM12); return; }
            Patch(typeof(RepairableComponent), "UpdateBreakage", new Type[] { typeof(int), typeof(Sim) }, typeof(B12KbHook), "BreakHook", "BreakOrig");
        }

        // Pure: the party part of the chance (EA: kegger rate on any party with EP9).
        internal static float KbParty(float num, bool fixedRule, bool ep4, bool ep9, bool party, bool kegger, float wildMult, float kegMult)
        {
            if (ep4 && party) num *= wildMult;
            if (ep9 && (fixedRule ? kegger : party)) num *= kegMult;
            return num;
        }

        public class B12KbHook
        {
            public bool BreakHook(int additional, Sim actor)
            {
                if (!ShortcutOn("kegbreakage")) { PatchCounters()[3155]++; return BreakOrig(additional, actor); }
                RepairableComponent rc = (RepairableComponent)(object)this;
                GameObject o = rc.Owner;
                try
                {
                    if (o.Upgradable != null && o.Upgradable.Unbreakable) return false;
                    if (!rc.CanBreak()) return false;
                    float num = (int)sKbChance.Invoke(rc, null) + additional;
                    if (actor != null)
                    {
                        BuffManager bm = actor.BuffManager;
                        if (bm.HasLuckyBuff()) num *= TraitTuning.FeelingLuckyTraitObjectBreakMultiplier;
                        if (bm.HasUnluckyBuff()) num *= TraitTuning.FeelingUnluckyTraitObjectBreakMultiplier;
                        bool party = false, kegger = false;
                        foreach (WildParty w in Situation.GetSituations<WildParty>())
                            if (w.Lot == o.LotCurrent) { party = true; if (w is JuiceKeggerParty) { kegger = true; break; } }
                        num = KbParty(num, true, GameUtils.IsInstalled(ProductVersion.EP4), GameUtils.IsInstalled(ProductVersion.EP9), party, kegger, WildParty.kBreakageMultiplier, JuiceKeggerParty.kPercentageMultiplierForObjectsBreaking);
                    }
                    if (o.LotCurrent != null && o.LotCurrent.IsDormitoryLot) num *= (float)sKbDorm.GetValue(null);
                    PatchCounters()[3154]++;
                    if (RandomUtil.RandomChance(num)) { rc.BreakObject(actor, false); return true; }
                    return false;
                }
                catch (Exception) { PatchCounters()[3156]++; return false; }
            }

            public bool BreakOrig(int additional, Sim actor) { return false; }
        }

        // ---- B12 §32 romancecooldown (docs/591 R11) ------------------------------------------------------------------
        // [verified, decompiled] GroupingSituation.ContinueRomanticSTC: in a group of more than two the bad-date cooldown
        // (kBadDateCooldownTime = 6) is measured in minutes; the two-Sim branch measures it in hours. Fix: the 3+ branch
        // blocks within 6 hours too (EA's TNS and false), else EA's method runs. Counters: [3157] blocked our way, [3158]
        // let through (switch off), [3159] errors.
        static readonly FieldInfo sRcStc = B12F(typeof(GroupingSituation), "kSTCConsideredTooRomantic");
        static string sRcM12 = "";

        static void ApplyRomanceCooldown()
        {
            sRcM12 = SpNames("GroupingSituation.kSTCConsideredTooRomantic", sRcStc);
            if (sRcM12.Length > 0) { AppliedLog().Add("romancecooldown: not found: " + sRcM12); return; }
            Patch(typeof(GroupingSituation), "ContinueRomanticSTC", new Type[] { typeof(SimDescription), typeof(SimDescription), typeof(ShortTermContextTypes) }, typeof(B12RcHook), "StcHook", "StcOrig");
        }

        // Pure: blocked by the cooldown? hoursRule = the fix (EA compares minutes).
        internal static bool RcBlocked(bool hoursRule, float elapsedHours, float cooldown) { return (hoursRule ? elapsedHours : elapsedHours * 60f) < cooldown; }

        public class B12RcHook
        {
            public static bool StcHook(SimDescription actor, SimDescription target, ShortTermContextTypes stc)
            {
                try
                {
                    Sim a = actor == null ? null : actor.CreatedSim, t = target == null ? null : target.CreatedSim;
                    if (a != null && t != null && !(a.IsNPC && t.IsNPC))
                    {
                        GroupingSituation g = a.GetSituationOfType<GroupingSituation>();
                        if (g != null && g.IsSimInGroup(t) && !g.RomanticGrouping && g.Count > 2 && Array.IndexOf((ShortTermContextTypes[])sRcStc.GetValue(null), stc) >= 0)
                        {
                            Relationship rel = Relationship.Get(a, t, false);
                            if (rel != null)
                            {
                                float h = SimClock.ElapsedTime(TimeUnit.Hours, rel.WhenLastHadBadDate, SimClock.CurrentTime());
                                if (RcBlocked(true, h, GroupingSituation.kBadDateCooldownTime) && !RcBlocked(false, h, GroupingSituation.kBadDateCooldownTime))
                                {
                                    if (ShortcutOn("romancecooldown")) { g.ShowTNSForNotBeingAbleToHaveDate(a, t); PatchCounters()[3157]++; return false; }
                                    PatchCounters()[3158]++;
                                }
                            }
                        }
                    }
                }
                catch (Exception) { PatchCounters()[3159]++; }
                return StcOrig(actor, target, stc);
            }

            public static bool StcOrig(SimDescription actor, SimDescription target, ShortTermContextTypes stc) { return false; }
        }

        // ---- B12 §33 lightcustom (docs/591 R12) ----------------------------------------------------------------------
        // [verified, decompiled] LightGameObject.SetIntensityRoom / SetIntensityHouse(CustomIntensity) give every other light
        // `mCustomIntensity = Intensity` (the clicked light's current level, which is still the old one when the loop
        // reaches another light first) instead of the clicked light's chosen mCustomIntensity. Fix: our copy copies
        // mCustomIntensity. Counters: [3160] lights set our way, [3161] EA's copies (switch off), [3162] errors.
        static readonly FieldInfo sLcCustom = B12F(typeof(LightGameObject), "mCustomIntensity");
        static string sLcM12 = "";

        static void ApplyLightCustom()
        {
            sLcM12 = SpNames("LightGameObject.mCustomIntensity", sLcCustom);
            if (sLcM12.Length > 0) { AppliedLog().Add("lightcustom: not found: " + sLcM12); return; }
            Patch(typeof(LightGameObject), "SetIntensityRoom", new Type[] { typeof(LightGameObject.LightIntensity) }, typeof(B12LcHook), "RoomHook", "RoomOrig");
            Patch(typeof(LightGameObject), "SetIntensityHouse", new Type[] { typeof(LightGameObject.LightIntensity) }, typeof(B12LcHook), "HouseHook", "HouseOrig");
        }

        internal static float LcValue(bool fixedRule, float custom, float intensity) { return fixedRule ? custom : intensity; }

        static void LcSet(LightGameObject self, LightGameObject.LightIntensity intensity, bool room)
        {
            int roomId = self.RoomId;
            float custom = (float)sLcCustom.GetValue(self);
            foreach (LightGameObject l in self.LotCurrent.GetObjects<LightGameObject>())
            {
                if (room ? l.RoomId != roomId : l.RoomId < 0) continue;
                if (intensity == LightGameObject.LightIntensity.CustomIntensity && l != self) sLcCustom.SetValue(l, LcValue(true, custom, self.Intensity));
                l.SetLightIntensity(intensity);
            }
            PatchCounters()[3160]++;
        }

        public class B12LcHook
        {
            public void RoomHook(LightGameObject.LightIntensity i)
            {
                if (i != LightGameObject.LightIntensity.CustomIntensity || !ShortcutOn("lightcustom")) { if (i == LightGameObject.LightIntensity.CustomIntensity) PatchCounters()[3161]++; RoomOrig(i); return; }
                try { LcSet((LightGameObject)(object)this, i, true); } catch (Exception) { PatchCounters()[3162]++; }
            }

            public void HouseHook(LightGameObject.LightIntensity i)
            {
                if (i != LightGameObject.LightIntensity.CustomIntensity || !ShortcutOn("lightcustom")) { if (i == LightGameObject.LightIntensity.CustomIntensity) PatchCounters()[3161]++; HouseOrig(i); return; }
                try { LcSet((LightGameObject)(object)this, i, false); } catch (Exception) { PatchCounters()[3162]++; }
            }

            public void RoomOrig(LightGameObject.LightIntensity i) { }
            public void HouseOrig(LightGameObject.LightIntensity i) { }
        }

        // ---- B12 §34 eatpreycancel (docs/591 R13) --------------------------------------------------------------------
        // [verified, decompiled] CatHuntingComponent.PetEatPrey.Run calls Target.DisableInteractions() and nothing enables them again: a pet that
        // fails to route to the prey leaves it with no interactions for good. SharedNearDistanceBehavior sets mDestroyPrey
        // after a cancelled DoTimedLoop too, so a cancelled eat still destroys the prey in Cleanup. Fix: a cancelled eat
        // keeps the prey (mDestroyPrey back to false), and a failed Run that will not destroy the prey enables its
        // interactions again. Counters: [3163] prey kept / re-enabled, [3164] seen with the switch off, [3165] errors.
        static readonly FieldInfo sEpDestroy = B12F(typeof(CatHuntingComponent.PetEatPrey), "mDestroyPrey");
        static string sEpM12 = "";

        static void ApplyEatPreyCancel()
        {
            sEpM12 = SpNames("CatHuntingComponent.PetEatPrey.mDestroyPrey", sEpDestroy);
            if (sEpM12.Length > 0) { AppliedLog().Add("eatpreycancel: not found: " + sEpM12); return; }
            Patch(typeof(CatHuntingComponent.PetEatPrey), "Run", Type.EmptyTypes, typeof(B12EpHook), "RunHook", "RunOrig");
            Patch(typeof(CatHuntingComponent.PetEatPrey), "SharedNearDistanceBehavior", new Type[] { typeof(float), typeof(float) }, typeof(B12EpHook), "NearHook", "NearOrig");
        }

        internal static bool EpKeepPrey(bool ate) { return !ate; }
        internal static bool EpReenable(bool result, bool destroy) { return !result && !destroy; }

        public class B12EpHook
        {
            public bool NearHook(float dist, float loop)
            {
                bool had = (bool)sEpDestroy.GetValue(this);
                bool r = NearOrig(dist, loop);
                try
                {
                    if (had || !(bool)sEpDestroy.GetValue(this) || !EpKeepPrey(r)) return r;
                    if (ShortcutOn("eatpreycancel")) { sEpDestroy.SetValue(this, false); PatchCounters()[3163]++; }
                    else PatchCounters()[3164]++;
                }
                catch (Exception) { PatchCounters()[3165]++; }
                return r;
            }

            public bool RunHook()
            {
                bool r = RunOrig();
                try
                {
                    GameObject prey = ((InteractionInstance)(object)this).Target as GameObject;
                    if (prey == null || prey.HasBeenDestroyed || !EpReenable(r, (bool)sEpDestroy.GetValue(this))) return r;
                    if (ShortcutOn("eatpreycancel")) { prey.EnableInteractions(); PatchCounters()[3163]++; }
                    else PatchCounters()[3164]++;
                }
                catch (Exception) { PatchCounters()[3165]++; }
                return r;
            }

            public bool NearOrig(float dist, float loop) { return false; }
            public bool RunOrig() { return false; }
        }

        // ---- B12 §35 meltalarm (docs/591 R16) ------------------------------------------------------------------------
        // [verified, decompiled] SculptureComponent.StartMelting(bool) reads Artist.SkillManager with no null test (Artist is
        // SimDescription.Find of the sculptor: null for a bought sculpture or a sculptor gone from the world; the fire path
        // calls it for every ice sculpture in reach) and overwrites mMeltingAlarm without removing the running one, so a
        // second start (fire near a melting sculpture) leaves two melt alarms. Fix: our copy treats a missing artist as no
        // Ice Personality and removes the old alarm first. Counters: [3166] starts our way, [3167] starts that threw or
        // doubled (switch off), [3168] errors.
        static readonly FieldInfo sMaAlarm = B12F(typeof(SculptureComponent), "mMeltingAlarm");
        static readonly FieldInfo sMaHours = B12F(typeof(SculptureComponent), "kIceMeltToBlock");
        static readonly MethodInfo sMaStage = B12M(typeof(SculptureComponent), "AdvanceMeltStage");
        static readonly MethodInfo sMaInstant = B12M(typeof(SculptureComponent), "AdvanceMeltStageInstant");
        static string sMaM12 = "";

        static void ApplyMeltAlarm()
        {
            sMaM12 = SpNames("SculptureComponent.mMeltingAlarm", sMaAlarm, "kIceMeltToBlock", sMaHours, "AdvanceMeltStage", sMaStage, "AdvanceMeltStageInstant", sMaInstant);
            if (sMaM12.Length > 0) { AppliedLog().Add("meltalarm: not found: " + sMaM12); return; }
            Patch(typeof(SculptureComponent), "StartMelting", new Type[] { typeof(bool) }, typeof(B12MaHook), "MeltHook", "MeltOrig");
        }

        // Pure: does the sculpture melt? (no artist = no Ice Personality)
        internal static bool MaMelts(bool artistKnown, bool icePersonality) { return !(artistKnown && icePersonality); }

        public class B12MaHook
        {
            public void MeltHook(bool now)
            {
                SculptureComponent sc = (SculptureComponent)(object)this;
                AlarmHandle old = (AlarmHandle)sMaAlarm.GetValue(sc);
                if (!ShortcutOn("meltalarm"))
                {
                    if (sc.Artist == null || old != AlarmHandle.kInvalidHandle) PatchCounters()[3167]++;
                    MeltOrig(now);
                    return;
                }
                try
                {
                    if (sc.Material != SculptureComponent.SculptureMaterial.Ice) return;
                    SimDescription artist = sc.Artist;
                    SculptingSkill skill = artist == null || artist.SkillManager == null ? null : artist.SkillManager.GetSkill<SculptingSkill>(SkillNames.Sculpting);
                    if (!MaMelts(artist != null, skill != null && skill.IsIcePersonality())) return;
                    GameObject o = sc.Owner;
                    if (old != AlarmHandle.kInvalidHandle) o.RemoveAlarm(old);
                    AlarmTimerCallback cb = (AlarmTimerCallback)Fixes.Bind(typeof(AlarmTimerCallback), sc, now ? sMaInstant : sMaStage);
                    sMaAlarm.SetValue(sc, now ? o.AddAlarm(1f, TimeUnit.Minutes, cb, "Ice Sculpture Melt Stage", AlarmType.AlwaysPersisted)
                        : o.AddAlarm((float)sMaHours.GetValue(null), TimeUnit.Hours, cb, "Ice Sculpture Melt Stage", AlarmType.AlwaysPersisted));
                    PatchCounters()[3166]++;
                }
                catch (Exception) { PatchCounters()[3168]++; }
            }

            public void MeltOrig(bool now) { }
        }

        // ---- B12 §36 newspaperdef (docs/591 R21 Newspaper half) ------------------------------------------------------
        // [verified, decompiled] Newspaper.GetReadInteraction makes its instance from GetNewspaperChooser.Singleton (the
        // newspaper's pie-menu root, IntDef null) and writes IntDef = CheckCurrentEvents into it, so the root's name and
        // path read as "Check Current Events" from then on. Fix: a Definition of its own with that IntDef (EA's own
        // constructor). Counters: [3169] reads with an own definition, [3170] shared writes (switch off), [3171] errors.
        static void ApplyNewspaperDef()
        {
            Patch(typeof(Newspaper), "GetReadInteraction", new Type[] { typeof(Sim) }, typeof(B12NdHook), "ReadHook", "ReadOrig");
        }

        internal static GetNewspaperChooser.Definition NdDefinition() { return new GetNewspaperChooser.Definition(CheckCurrentEvents.Singleton); }

        public class B12NdHook
        {
            public InteractionInstance ReadHook(Sim s)
            {
                if (!ShortcutOn("newspaperdef")) { PatchCounters()[3170]++; return ReadOrig(s); }
                try
                {
                    PatchCounters()[3169]++;
                    return NdDefinition().CreateInstance((Newspaper)(object)this, s, new InteractionPriority(InteractionPriorityLevel.Autonomous), true, true);
                }
                catch (Exception) { PatchCounters()[3171]++; return ReadOrig(s); }
            }

            public InteractionInstance ReadOrig(Sim s) { return null; }
        }
    }
}
