using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Controllers;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.EventSystem;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Objects.Miscellaneous;
using Sims3.Gameplay.Situations;
using Sims3.Gameplay.Skills;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;
using Sims3.SimIFace.Enums;

namespace Ts3Fixes
{
    // Batch 9 (docs/582 B9): the EA defects queued by the batch 3-8b review, plus the Imminent Nemesis decision (user:
    // option A, drop the romance test). All default off. simler90 UPDATE242 changes none of these methods.
    // Folded into existing switches instead (same method or same defect): rolenullguard (FutureFoodSynthesizer
    // .TestIfSocialValid), sandwichalarm (one-shot during a player show), talkoccupationchance (best-choice cap),
    // elevatorridertimeout (failsafe return), holidaylights (correction), weddinglistener's hook (partycleanupnull),
    // hiddenroomspawn's PutHook (safelocunhide), traitlistenerremove's hook (traitremovenull), freshclothingresume's hook
    // (laundrymoodeach).
    public static partial class Fixes
    {
        // ---- B9 §1 traitremovenull (tuning kTraitRemoveNull) ---------------------------------------------------------------
        // [verified, decompiled] TraitManager.RemoveElement(ulong) reads `traitFromDictionary.Guid` when the Sim is
        // instantiated, without the null test it uses two lines above. A trait missing from the trait dictionary (removed CC
        // or an uninstalled pack) makes Trait.Fixup return false, and TraitManager.Fixup then calls RemoveElement for it: it
        // throws for every instantiated Sim with such a trait. base.RemoveElement never runs (the trait stays), and the throw
        // leaves Fixup's loop and its AddDesireAlarm, so the Sim's other traits are not fixed up either.
        // Fix (shares traitlistenerremove's hook; switch on, trait missing, Sim instantiated): EA's method in EA's order, with
        // the two Future-trait tests reading the removed guid (what the dictionary clone's Guid is when it exists), and
        // EA's base step (mValues remove + OnRemoved) done through reflection. Every other case is EA's code.
        // Household-visible: yes (a household Sim with a missing-pack trait).
        // Counters: [3693] removals finished, [3694] would have thrown (switch off), [3695] errors.
        static readonly FieldInfo sTnDesc = typeof(TraitManager).GetField("mSimDescription", kInstAll);
        static readonly FieldInfo sTnValues = typeof(GenericManager<TraitNames, Trait, Trait>).GetField("mValues", kInstAll);
        static readonly MethodInfo sTnOnRemoved = typeof(TraitManager).GetMethod("OnRemoved", kInstAll, null, new Type[] { typeof(Trait) }, null);
        static readonly Type sTnImport = typeof(TraitManager).Assembly.GetType("Sims3.Gameplay.WorldBuilderUtil.CharacterImportOnGameLoad");
        static readonly FieldInfo sTnImporting = sTnImport == null ? null : sTnImport.GetField("sbIsCharacterImportOnGameLoadInProgress", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        static string sTnMissing = "";

        static void ApplyTraitRemoveNull()
        {
            sTnMissing = ((sTnDesc == null ? "TraitManager.mSimDescription " : "") + (sTnValues == null ? "GenericManager.mValues " : "")
                + (sTnOnRemoved == null ? "TraitManager.OnRemoved " : "") + (sTnImporting == null ? "CharacterImportOnGameLoad.sbIsCharacterImportOnGameLoadInProgress" : "")).Trim();
            if (sTnMissing.Length > 0) AppliedLog().Add("traitremovenull: not found: " + sTnMissing);
            // the hook is traitlistenerremove's (Batch582gLogic.cs BTlHook): one swap of TraitManager.RemoveElement(ulong)
        }

        // Pure: does EA's RemoveElement throw? (trait not in the dictionary, Sim instantiated)
        internal static bool TnThrows(bool inDictionary, bool instantiated) { return !inDictionary && instantiated; }

        // From BTlHook.RemoveHook. true: handled, skip EA's method.
        internal static bool TnRemove(object mgr, ulong guid)
        {
            if (sTnMissing.Length > 0) return false;
            try
            {
                TraitManager tm = (TraitManager)mgr;
                SimDescription sd = sTnDesc.GetValue(tm) as SimDescription;
                Sim actor = sd == null ? null : sd.CreatedSim;
                if (!TnThrows(TraitManager.GetTraitFromDictionary((TraitNames)guid) != null, actor != null)) return false;
                if (!ShortcutOn("traitremovenull")) { PatchCounters()[3694]++; return false; }
                actor.UpdateSacsParametersForTraitOrBuff(typeof(TraitNames), guid, YesOrNo.no);
                if (actor.SocialComponent != null) actor.SocialComponent.UpdateTraits();
                if ((TraitNames)guid == TraitNames.FutureSim && actor.BuffManager != null && !actor.BuffManager.HasElement(BuffNames.EmbracingTheFuture))
                    actor.SkillManager.SubtractFromSkillGainModifier(SkillNames.Future, BuffEmbracingTheFuture.kEmbracingFutureSkillMultiplier - 1f);
                if ((TraitNames)guid == TraitNames.FutureSimLHR) tm.RemoveElement(TraitNames.FutureSim);
                Dictionary<ulong, Trait> values = sTnValues.GetValue(tm) as Dictionary<ulong, Trait>;
                Trait t;
                if (values != null && values.TryGetValue(guid, out t))
                {
                    values.Remove(guid);
                    sTnOnRemoved.Invoke(tm, new object[] { t });  // EA's base.RemoveElement; OnRemoved is not swapped
                }
                if (!(bool)sTnImporting.GetValue(null))  // EA's !CharacterImportOnGameLoad.InProgress (an internal class)
                {
                    tm.AddDesireAlarm();
                    MetaAutonomyManager.UpdatePreferredVenuesForSim(actor);
                }
                PatchCounters()[3693]++;
                return true;
            }
            catch (Exception) { PatchCounters()[3695]++; return false; }
        }

        // ---- B9 §2 homeworkcopyold (tuning kHomeworkCopyOld) ---------------------------------------------------------------
        // [verified, decompiled; user 2026-11-15: "as EA intended"] Homework.GetCompletionRate multiplies a copy by kCopyingMultiplier
        // (2) or, for old homework, kCopyingMultiplierOld (1), testing the homework it is called on. CopyHomework.LoopDel
        // calls it on mMyHomework, the copier's own current homework (School.OwnersHomework, never old), so the old rate
        // never applies: copying a stale (old) homework is as fast as copying today's. Fix (switch on): the copy loop's rate
        // uses the multiplier of the homework being copied (Target), i.e. EA's rate times kOld / k when Target is old. The
        // rest of LoopDel is EA's, in EA's order. Household-visible: copying old homework takes twice as long, as tuned.
        // Counters: [3696] loop ticks at the old rate, [3697] the same seen with the switch off, [3698] errors.
        static readonly Type sHcType = typeof(Homework).GetNestedType("CopyHomework", BindingFlags.Public | BindingFlags.NonPublic);
        static readonly FieldInfo sHcMine = sHcType == null ? null : sHcType.GetField("mMyHomework", kInstAll);
        static string sHcMissing = "";

        static void ApplyHomeworkCopyOld()
        {
            sHcMissing = ((sHcMine == null ? "Homework+CopyHomework.mMyHomework " : "") + (sHwCopy == null || sHwCopyOld == null ? "Homework.kCopyingMultiplier/Old" : "")).Trim();
            if (sHcMissing.Length > 0) { AppliedLog().Add("homeworkcopyold: not found: " + sHcMissing); return; }
            Patch(sHcType, "LoopDel", new Type[] { typeof(StateMachineClient), typeof(InteractionInstance.LoopData) }, typeof(BHcHook), "LoopHook", "LoopOrig");
        }

        // Pure: the rate to use. ea: EA's rate (with the own homework's multiplier); k / kOld: the two multipliers.
        internal static float HcRate(bool on, float ea, bool targetOld, bool mineOld, float k, float kOld)
        {
            if (!on || targetOld == mineOld) return ea;
            float from = mineOld ? kOld : k, to = targetOld ? kOld : k;
            return from == 0f ? ea : ea * to / from;
        }

        public class BHcHook
        {
            public void LoopHook(StateMachineClient smc, InteractionInstance.LoopData loopData)
            {
                Homework mine = null, target = null;
                Sim actor = null;
                bool differ = false;
                try
                {
                    InteractionInstance<Sim, Homework> ii = (InteractionInstance<Sim, Homework>)(object)this;
                    actor = ii.InstanceActor;
                    target = ii.Target;
                    mine = sHcMine.GetValue(this) as Homework;
                    differ = actor != null && mine != null && target != null && target.IsOld != mine.IsOld;
                }
                catch (Exception) { PatchCounters()[3698]++; differ = false; }
                if (!differ) { LoopOrig(smc, loopData); return; }
                if (!ShortcutOn("homeworkcopyold")) { PatchCounters()[3697]++; LoopOrig(smc, loopData); return; }
                // EA's LoopDel, the rate scaled
                EventTracker.SendEvent(new HomeworkEvent(EventTypeId.kCopiedHomework, actor, null, target));
                float rate = mine.GetCompletionRate(actor, true, false);
                rate = HcRate(true, rate, target.IsOld, mine.IsOld, (float)sHwCopy.GetValue(null), (float)sHwCopyOld.GetValue(null));
                PatchCounters()[3696]++;
                mine.UpdateProgress(actor, rate, loopData.mDeltaTime);
                if (mine.IsComplete())
                {
                    if (actor.HasTrait(TraitNames.Perfectionist)) actor.PlayReaction(ReactionTypes.Mwah, ReactionSpeed.AfterInteraction);
                    actor.AddExitReason(ExitReason.Finished);
                }
            }
            public void LoopOrig(StateMachineClient smc, InteractionInstance.LoopData loopData) { } // Homework+CopyHomework.LoopDel
        }

        // ---- B9 §3 laundrymoodeach (tuning kLaundryMoodEach) ---------------------------------------------------------------
        // [verified, decompiled] LaundryManager.AddLaundryBuff applies the washer's mood multiplier (mMoodMultiplier, > 1
        // after an expensive washer) to the new Fresh Clothing buff only while the lot-wide flag mIsMoodMultiplierApplied is
        // false, then sets it: in a household only the first Sim who dresses from the load gets the stronger moodlet. Any
        // Sim's Fresh Clothing ending clears the flag (OnFreshClothingBuffRemoved), so the next add, even onto a buff that
        // already has the multiplier (a re-add keeps the larger value), multiplies again. Fix (shares freshclothingresume's
        // hook; switch on): EA's add runs with the flag held set (EA multiplies nothing), then each Sim's buff is raised to
        // the buff's base value times the multiplier, never above it and never twice; EA's flag value is restored.
        // Household-visible: every Sim dressed from an expensive-washer load gets the stronger moodlet.
        // Counters: [3820] buffs given the multiplier, [3821] Sims EA leaves without it (switch off), [3822] errors.
        static readonly FieldInfo sLmFlag = typeof(LaundryManager).GetField("mIsMoodMultiplierApplied", kInstAll);
        static readonly FieldInfo sLmMult = typeof(LaundryManager).GetField("mMoodMultiplier", kInstAll);
        static string sLmMissing = "";

        static void ApplyLaundryMoodEach()
        {
            sLmMissing = ((sLmFlag == null ? "LaundryManager.mIsMoodMultiplierApplied " : "") + (sLmMult == null ? "LaundryManager.mMoodMultiplier" : "")).Trim();
            if (sLmMissing.Length > 0) AppliedLog().Add("laundrymoodeach: not found: " + sLmMissing);
            // the hook is freshclothingresume's (Batch582eLogic.cs BFcHook): one swap of LaundryManager.AddLaundryBuff
        }

        // Pure: the effect value the buff should have. current: its value now; baseValue: the buff's tuned value.
        internal static int LmTarget(bool on, int current, int baseValue, float mult)
        {
            if (!on || mult == 1f) return current;
            int want = (int)(baseValue * mult);
            return current < want ? want : current;
        }

        // From BFcHook.AddHook: true when this call owns the multiplier (EA's flag held set). prevFlag: EA's flag before.
        internal static bool LmBefore(Sim actor, out bool prevFlag)
        {
            prevFlag = false;
            if (sLmMissing.Length > 0 || actor == null || actor.LotHome == null || actor.LotHome.LaundryManager == null) return false;
            try
            {
                LaundryManager lm = actor.LotHome.LaundryManager;
                prevFlag = (bool)sLmFlag.GetValue(lm);
                bool on = ShortcutOn("laundrymoodeach");
                if (!on)
                {
                    if (prevFlag && (float)sLmMult.GetValue(lm) != 1f) PatchCounters()[3821]++;  // EA skips this Sim
                    return false;
                }
                sLmFlag.SetValue(lm, true);
                return true;
            }
            catch (Exception) { PatchCounters()[3822]++; return false; }
        }

        internal static void LmAfter(Sim actor, bool prevFlag)
        {
            try
            {
                LaundryManager lm = actor.LotHome.LaundryManager;
                sLmFlag.SetValue(lm, prevFlag);
                float mult = (float)sLmMult.GetValue(lm);
                BuffInstance b = FcBuff(actor);
                if (b == null) return;
                BuffNames guid = b.Guid;
                int baseValue = BuffManager.BuffDictionary[(ulong)guid].EffectValue;
                int v = LmTarget(true, b.mEffectValue, baseValue, mult);
                if (v != b.mEffectValue) { b.mEffectValue = v; PatchCounters()[3820]++; }
            }
            catch (Exception) { PatchCounters()[3822]++; }
        }

        // ---- B9 §4 partycleanupnull (tuning kPartyCleanupNull) -------------------------------------------------------------
        // [verified, decompiled] The root cause behind partylotlistener and weddinglistener: NpcParty+Happening.CleanUp reads
        // Parent.Host.Motives, and the host's, other hosts' and guests' Motives, with no null test; WeddingParty.CleanUp
        // reads each candidate's BuffManager and Host.Autonomy.SituationComponent. A party whose host (or a guest) is gone
        // throws there, before base.CleanUp: the situation's desires, checks, alarms and role are never cleaned up, and
        // Dispose never runs (Happening's OnLotChangedEvent subscriber stays). Fix (switch on, only when EA's method would
        // throw): EA's steps in EA's order with each missing Sim skipped, then EA's base.CleanUp (the hook classes derive
        // from EA's base types, so `base.CleanUp()` is EA's non-virtual base call). WeddingParty's candidate list is copied
        // before the loop (buff removal can raise events that change it). Household-visible: yes (player-hosted parties).
        // Counters: [3823] cleanups finished null-safe, [3824] would have thrown (switch off), [3825] errors.
        static readonly Type sPcHappen = typeof(NpcParty).GetNestedType("Happening", BindingFlags.Public | BindingFlags.NonPublic);
        static readonly FieldInfo sPcMotives = sPcHappen == null ? null : sPcHappen.GetField("mPartyMotives", kInstAll);
        static readonly FieldInfo sPcCandidates = typeof(WeddingParty).GetField("mWeddingCandidates", kInstAll);
        static readonly FieldInfo sPcBuffAlarms = typeof(WeddingParty).GetField("mBuffAlarmHandles", kInstAll);
        static string sPcMissing = "";

        static void ApplyPartyCleanupNull()
        {
            sPcMissing = ((sPcMotives == null ? "NpcParty+Happening.mPartyMotives " : "") + (sPcCandidates == null ? "WeddingParty.mWeddingCandidates " : "")
                + (sPcBuffAlarms == null ? "WeddingParty.mBuffAlarmHandles" : "")).Trim();
            if (sPcMissing.Length > 0) { AppliedLog().Add("partycleanupnull: not found: " + sPcMissing); return; }
            Patch(sPcHappen, "CleanUp", Type.EmptyTypes, typeof(BPcHapHook), "CleanHook", "CleanOrig");
            // WeddingParty.CleanUp: weddinglistener's hook (Batch582gLogic.cs BWlHook) calls PcWedding
        }

        // Pure: does EA's CleanUp throw? hostOk: host and its Motives (Happening) / Autonomy.SituationComponent (wedding)
        // present; simsOk: every listed Sim and the member EA reads present.
        internal static bool PcThrows(bool hostOk, bool simsOk) { return !hostOk || !simsOk; }

        static bool PcSimsOk(List<Sim> l, bool motives)
        {
            if (l == null) return false;
            foreach (Sim s in l) if (s == null || (motives ? s.Motives == null : s.BuffManager == null)) return false;
            return true;
        }

        public class BPcHapHook : ChildSituation<NpcParty>
        {
            public void CleanHook()
            {
                bool safeCopy = false;
                try
                {
                    NpcParty p = Parent;
                    bool hostOk = p != null && p.Host != null && p.Host.Motives != null;
                    bool simsOk = p != null && PcSimsOk(p.OtherHosts, true) && PcSimsOk(p.Guests, true) && PcSimsOk(p.SimsWhoLeftParty, true);
                    if (PcThrows(hostOk, simsOk))
                    {
                        if (ShortcutOn("partycleanupnull")) safeCopy = true;
                        else PatchCounters()[3824]++;
                    }
                }
                catch (Exception) { PatchCounters()[3825]++; safeCopy = false; }
                if (!safeCopy) { CleanOrig(); return; }
                try
                {
                    NpcParty p = Parent;
                    Dictionary<Sim, CommodityKind> pm = sPcMotives.GetValue(this) as Dictionary<Sim, CommodityKind>;
                    if (p != null)
                    {
                        CommodityKind m = p.HostMotive();
                        if (p.Host != null && p.Host.Motives != null) { p.Host.Motives.RemoveMotive(m); PcBeAt(pm, p.Host); }
                        if (p.OtherHosts != null)
                            foreach (Sim s in p.OtherHosts) if (s != null && s.Motives != null) { s.Motives.RemoveMotive(m); PcBeAt(pm, s); }
                        if (p.Guests != null) foreach (Sim s in p.Guests) PcBeAt(pm, s);
                        if (p.SimsWhoLeftParty != null) foreach (Sim s in p.SimsWhoLeftParty) PcBeAt(pm, s);
                    }
                    PatchCounters()[3823]++;
                }
                catch (Exception) { PatchCounters()[3825]++; }
                base.CleanUp();  // EA's base.CleanUp (Situation.CleanUp), non-virtual
            }
            public void CleanOrig() { } // receives NpcParty+Happening.CleanUp

            // EA's private RemoveBeAtPartyMotive, null-safe (a null key would throw in TryGetValue)
            static void PcBeAt(Dictionary<Sim, CommodityKind> pm, Sim s)
            {
                if (pm == null || s == null || s.Motives == null) return;
                CommodityKind v;
                if (pm.TryGetValue(s, out v) && v != CommodityKind.None) s.Motives.RemoveMotive(v);
            }
        }

        // From BWlHook.CleanHook (WeddingParty.CleanUp). true: EA's method would throw and the null-safe copy of its own
        // steps ran (the caller then runs EA's base, HouseParty.CleanUp); false: run EA's method.
        internal static bool PcWedding(WeddingParty party)
        {
            if (sPcMissing.Length > 0 || party == null) return false;
            try
            {
                List<Sim> cands = sPcCandidates.GetValue(party) as List<Sim>;
                Sim host = party.Host;
                bool hostOk = host != null && host.Autonomy != null && host.Autonomy.SituationComponent != null;
                if (!PcThrows(hostOk, PcSimsOk(cands, false))) return false;
                if (!ShortcutOn("partycleanupnull")) { PatchCounters()[3824]++; return false; }
                List<AlarmHandle> alarms = sPcBuffAlarms.GetValue(party) as List<AlarmHandle>;
                bool flag = false;
                if (cands != null)
                    foreach (Sim c in new List<Sim>(cands))
                    {
                        if (c == null) continue;
                        if (c.BuffManager != null && !c.IsMarried && c.BuffManager.HasElement(BuffNames.WeddingDay))
                        {
                            c.BuffManager.RemoveElement(BuffNames.WeddingDay);
                            c.BuffManager.AddElement(BuffNames.MissedWedding, Origin.FromWedding);
                        }
                        if (alarms != null) foreach (AlarmHandle h in alarms) c.RemoveAlarm(h);
                        if (!flag && c.IsMarried && c.Partner != null) flag |= WeddingParty.GiveTimeOffToWedding(c, c.Partner.CreatedSim);
                    }
                if (alarms != null) { alarms.Clear(); sPcBuffAlarms.SetValue(party, null); }
                if (hostOk)
                {
                    List<Situation> situations = host.Autonomy.SituationComponent.Situations;
                    List<WeddingCeremony> list = new List<WeddingCeremony>();
                    if (situations != null) foreach (Situation s in situations) { WeddingCeremony w = s as WeddingCeremony; if (w != null) list.Add(w); }
                    foreach (WeddingCeremony w in list) w.Exit();
                }
                PatchCounters()[3823]++;
                return true;
            }
            catch (Exception) { PatchCounters()[3825]++; return false; }
        }

        // ---- B9 §5 safelocunhide (tuning kSafeLocUnhide) -------------------------------------------------------------------
        // [verified, decompiled] Sim.AttemptToPutInSafeLocation (the reset placement) unhides the Sim (out of and back into the
        // world, SetHiddenFlags(Nothing), opacity 1) only when AttemptToFindSafeLocation finds a spot; its fallback (lot
        // position of the home / virtual home / current lot) only moves it. A Sim hidden when it was reset (in a rabbit hole,
        // mid fade, hidden by a service) and placed by the fallback stays invisible. Fix (shares hiddenroomspawn's hook; switch
        // on): the fallback runs EA's same unhide steps after the move. Household Sims too (an EA bug fix).
        // Counters: [3826] fallback placements unhidden, [3827] the same seen with the switch off, [3828] errors.
        // Pure: unhide after the fallback move?
        internal static bool SuUnhide(bool on, bool fallbackMoved) { return on && fallbackMoved; }

        static void ApplySafeLocUnhide() { }  // the hook is hiddenroomspawn's (HiddenRoomSpawn.cs HrResetHook.PutHook)

        internal static void SuAfterFallback(Sim s)
        {
            try
            {
                if (!SuUnhide(ShortcutOn("safelocunhide"), true)) { PatchCounters()[3827]++; return; }
                s.RemoveFromWorld();
                s.AddToWorld();
                s.SetHiddenFlags(HiddenFlags.Nothing);
                s.SetOpacity(1f, 0f);
                PatchCounters()[3826]++;
            }
            catch (Exception) { PatchCounters()[3828]++; }
        }

        // ---- B9 §6 midlifeuiending (tuning kMidlifeUiEnding) ---------------------------------------------------------------
        // [verified, decompiled] MidlifeCrisisManager.GetMinutesLeft is the getter behind the Having a Midlife Crisis
        // moodlet's UITimeoutCount (the tooltip and moodlet timer), and it calls SuccessfulEnding when the time is up:
        // reading the tooltip ends the crisis (removes the buffs and wishes, shows the result notice, adds lifetime
        // happiness) from inside a UI property read. OnDayPassed ends it daily anyway. Fix (switch on): the getter returns
        // EA's value and changes nothing; the crisis ends through OnDayPassed (and OnBecameElder) as EA's other paths do.
        // Household-visible: the crisis ends at the day pass instead of when the player hovers the moodlet.
        // Counters: [3829] reads that would have ended the crisis, [3830] the same seen with the switch off, [3831] errors.
        static readonly MethodInfo sMuLeft = typeof(MidlifeCrisisManager).GetMethod("GetMinutesLeft", BindingFlags.Instance | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
        static readonly FieldInfo sMuOwner = typeof(MidlifeCrisisManager).GetField("mOwnerDescription", kInstAll);
        static readonly FieldInfo sMuLength = typeof(MidlifeCrisisManager).GetField("kCrisisLength", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        static string sMuMissing = "";

        static void ApplyMidlifeUiEnding()
        {
            sMuMissing = ((sMuLeft == null ? "MidlifeCrisisManager.GetMinutesLeft() " : "") + (sMuOwner == null ? "MidlifeCrisisManager.mOwnerDescription " : "")
                + (sMuLength == null ? "MidlifeCrisisManager.kCrisisLength " : "") + (sMcDate == null ? "MidlifeCrisisManager.mLastUpdatedDate" : "")).Trim();
            if (sMuMissing.Length > 0) { AppliedLog().Add("midlifeuiending: not found: " + sMuMissing); return; }
            Patch(typeof(MidlifeCrisisManager), "GetMinutesLeft", Type.EmptyTypes, typeof(BMuHook), "LeftHook", "LeftOrig");
        }

        // Pure: does EA's getter end the crisis? (EA: `if (num4 < num5) SuccessfulEnding()`)
        internal static bool MuEnds(float minutesLeftInAge, float minutesIntoDay) { return minutesLeftInAge < minutesIntoDay; }

        public class BMuHook
        {
            public float LeftHook()
            {
                try
                {
                    SimDescription d = sMuOwner.GetValue(this) as SimDescription;
                    Sim cs = d == null ? null : d.CreatedSim;
                    bool having = cs != null && cs.BuffManager != null && cs.BuffManager.HasAnyElement(BuffNames.HavingAMidlifeCrisis, BuffNames.HavingAMidlifeCrisisWithPromise);
                    if (having)
                    {
                        // EA's arithmetic, without the ending
                        float num = AgingManager.Singleton.AgingYearsToSimDays(AgingManager.Singleton.GetCurrentAgingStageLength(d));
                        float num3 = (float)Math.Ceiling(num * (float)sMuLength.GetValue(null));
                        DateAndTime now = SimClock.CurrentTime();
                        if (now.DayOfWeek != ((DateAndTime)sMcDate.GetValue(this)).DayOfWeek) num3--;
                        num3 -= d.UserDaysInCurrentAge;
                        float num4 = num3 * 24f * 60f, num5 = now.Hour * 60f;
                        if (MuEnds(num4, num5))
                        {
                            if (ShortcutOn("midlifeuiending")) { PatchCounters()[3829]++; return num4 - num5; }
                            PatchCounters()[3830]++;
                        }
                    }
                }
                catch (Exception) { PatchCounters()[3831]++; }
                return LeftOrig();
            }
            public float LeftOrig() { return 0f; } // receives MidlifeCrisisManager.GetMinutesLeft()
        }

        // ---- B9 §7 nemesisnoromance (tuning kNemesisNoRomance) -------------------------------------------------------------
        // [verified code; intent decided by the user 2026-11-15: option A] BuffImminentNemesis.OnSocialization (the spell's
        // instant nemesis) requires TargetSim.CanHaveRomanceWith(other), copied from BuffImminentRomance: relatives, teen and
        // adult pairs and time travellers can never become nemeses. The user's call: a copy-paste bug, the test goes. Fix
        // (switch on): where EA's only refusal is that test, EA's own CreateInstantNemesis and buff removal run. EA's
        // IsHuman test stays, and both Sims must be child or older (the forced "Fight!" social's own age set, C-E: a toddler
        // or baby cannot run it). Household-visible: yes.
        // Counters: [3832] nemeses made where EA's romance test refused, [3833] the same seen with the switch off, [3834] errors.
        static readonly MethodInfo sInCreate = typeof(BuffImminentNemesis.BuffInstanceImminentNemesis).GetMethod("CreateInstantNemesis", kInstAll, null, new Type[] { typeof(Sim), typeof(Sim) }, null);
        static string sInMissing = "";

        static void ApplyNemesisNoRomance()
        {
            sInMissing = sInCreate == null ? "BuffInstanceImminentNemesis.CreateInstantNemesis" : "";
            if (sInMissing.Length > 0) { AppliedLog().Add("nemesisnoromance: not found: " + sInMissing); return; }
            Patch(typeof(BuffImminentNemesis.BuffInstanceImminentNemesis), "OnSocialization", new Type[] { typeof(Event) }, typeof(BInHook), "SocialHook", "SocialOrig");
        }

        // Pure: does the instant nemesis fire? romance: EA's CanHaveRomanceWith; ages: both child or older.
        internal static bool InFires(bool on, bool human, bool romance, bool ages) { return human && (romance || (on && ages)); }

        public class BInHook
        {
            public ListenerAction SocialHook(Event e)
            {
                Sim a = null, b = null;  // EA's CreateInstantNemesis(actor, target) order
                try
                {
                    BuffImminentNemesis.BuffInstanceImminentNemesis bi = (BuffImminentNemesis.BuffInstanceImminentNemesis)(object)this;
                    SimDescription ts = bi.TargetSim;
                    Sim other = e == null ? null : e.TargetObject as Sim;
                    Sim actor = e == null ? null : e.Actor as Sim;
                    if (ts != null && actor != null && actor.SimDescription != null)
                    {
                        Sim partner = null;
                        if (actor.SimDescription.SimDescriptionId == ts.SimDescriptionId) { partner = other; a = ts.CreatedSim; b = other; }
                        else if (other != null && other.SimDescription.SimDescriptionId == ts.SimDescriptionId) { partner = actor; a = other; b = actor; }
                        if (partner != null && a != null && partner.SimDescription != null && partner.SimDescription.IsHuman
                            && !ts.CanHaveRomanceWith(partner.SimDescription))
                        {
                            bool ages = ts.ChildOrAbove && partner.SimDescription.ChildOrAbove;
                            if (InFires(true, true, false, ages))
                            {
                                if (!ShortcutOn("nemesisnoromance")) { PatchCounters()[3833]++; a = null; }
                            }
                            else a = null;
                        }
                        else a = null;  // EA decides (romance passes, not human, or not this Sim's social)
                    }
                    else a = null;
                }
                catch (Exception) { PatchCounters()[3834]++; a = null; }
                if (a == null || b == null) return SocialOrig(e);
                try
                {
                    BuffImminentNemesis.BuffInstanceImminentNemesis bi = (BuffImminentNemesis.BuffInstanceImminentNemesis)(object)this;
                    sInCreate.Invoke(bi, new object[] { a, b });  // EA's CreateInstantNemesis, not swapped
                    bi.TargetSim.CreatedSim.BuffManager.RemoveElement(bi.BuffGuid);
                    PatchCounters()[3832]++;
                }
                catch (Exception) { PatchCounters()[3834]++; }
                return ListenerAction.Keep;
            }
            public ListenerAction SocialOrig(Event e) { return ListenerAction.Keep; } // BuffInstanceImminentNemesis.OnSocialization
        }
    }
}
