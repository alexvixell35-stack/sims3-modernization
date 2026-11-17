using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.DynamicChallenges;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Objects;
using Sims3.Gameplay.Objects.Appliances;
using Sims3.Gameplay.Objects.ChallengeDelegates;
using Sims3.Gameplay.Objects.Miscellaneous;
using Sims3.Gameplay.Objects.Rewards;
using Sims3.Gameplay.Objects.Vehicles;
using Sims3.Gameplay.Opportunities;
using Sims3.Gameplay.Skills;
using Sims3.Gameplay.TuningValues;
using Sims3.SimIFace;
using Sims3.SimIFace.CAS;

namespace Ts3Fixes
{
    // docs/582 batch 5: Roslyn analyzer leads (docs/583 §2, user-approved). Every switch off by default. simler90 core UPDATE242
    // checked first (docs/582 "simler90"): it fixes the barista ConfigureInteraction / Run null tests, so only the parts it
    // leaves are hooked here. Checks: Batch582cChecks.cs.
    public static partial class Fixes
    {
        // ---- B5 §1 rolenullguard (tuning kRoleNullGuard; docs/583 lead 1) --------------------------------------------------
        // [verified, decompiled] The "no role" test is written `role == null && role.RoleGivingObject == null`, which
        // dereferences a null role. It throws whenever the barista (University) or the future-food waiter (Into the Future) has
        // lost the role while a pie menu or a queued order still targets them. simler90 fixes the barista Runs and
        // ConfigureInteraction; left: BuyDrinkFromBar/BuySnackFromBar Definition.AddInteractions and OrderFoodOnWaiter
        // Definition.Test / AddInteractions. Fix: a target with no role gets EA's own "no" (nothing added / Test false) before
        // EA's code runs. Also (batch 9, 2026-11-15 review): FutureFoodSynthesizer.TestIfSocialValid has the same typo on a
        // target with an ACTIVE role: a role whose RoleGivingObject is null passes the test and `.LotCurrent` throws. With
        // the switch on that case answers EA's intended "no" (false). Counted in [2861] / [2862].
        // Counters: [2861] calls answered "no role", [2862] seen with the switch off, [2863] errors.
        static readonly Type sRnWaiterDef = RnWaiterDef();
        static string sRnMissing = "";

        static Type RnWaiterDef()
        {
            Type o = typeof(FutureFoodSynthesizer).GetNestedType("OrderFoodOnWaiter", BindingFlags.Public | BindingFlags.NonPublic);
            return o == null ? null : o.GetNestedType("Definition", BindingFlags.Public | BindingFlags.NonPublic);
        }

        static void ApplyRoleNullGuard()
        {
            Type[] add = new Type[] { typeof(InteractionObjectPair), typeof(Sim), typeof(Sim), typeof(List<InteractionObjectPair>) };
            Type[] test = new Type[] { typeof(Sim), typeof(Sim), typeof(bool), typeof(GreyedOutTooltipCallback).MakeByRefType() };
            sRnMissing = (sRnWaiterDef == null ? "FutureFoodSynthesizer+OrderFoodOnWaiter+Definition" : "").Trim();
            Patch(typeof(BaristaBar.BuyDrinkFromBar.Definition), "AddInteractions", add, typeof(BRnDrinkHook), "AddHook", "AddOrig");
            Patch(typeof(BaristaBar.BuySnackFromBar.Definition), "AddInteractions", add, typeof(BRnSnackHook), "AddHook", "AddOrig");
            if (sRnMissing.Length > 0) { AppliedLog().Add("rolenullguard: not found: " + sRnMissing); return; }
            Type waiter = sRnWaiterDef;
            Patch(waiter, "AddInteractions", add, typeof(BRnWaiterHook), "AddHook", "AddOrig");
            Patch(waiter, "Test", test, typeof(BRnWaiterHook), "TestHook", "TestOrig");
            Patch(typeof(FutureFoodSynthesizer), "TestIfSocialValid", new Type[] { typeof(Sim), typeof(Sim) }, typeof(BRnSocialHook), "SocialHook", "SocialOrig");
        }

        // Pure: does EA's TestIfSocialValid throw? (EA's order: same Sim, actor role, target role, then the giver)
        internal static bool RnSocialThrows(bool same, bool actorRole, bool targetRole, bool giverNull)
        {
            return !same && !actorRole && targetRole && giverNull;
        }

        public class BRnSocialHook
        {
            public static bool SocialHook(Sim actor, Sim target)
            {
                try
                {
                    if (actor != null && target != null && target.SimDescription != null && actor.SimDescription != null
                        && RnSocialThrows(actor == target, actor.SimDescription.HasActiveRole, target.SimDescription.HasActiveRole,
                            target.SimDescription.AssignedRole != null && target.SimDescription.AssignedRole.RoleGivingObject == null))
                    {
                        if (ShortcutOn("rolenullguard")) { PatchCounters()[2861]++; return false; }
                        PatchCounters()[2862]++;
                    }
                }
                catch (Exception) { PatchCounters()[2863]++; }
                return SocialOrig(actor, target);
            }
            public static bool SocialOrig(Sim actor, Sim target) { return false; } // FutureFoodSynthesizer.TestIfSocialValid
        }

        // Pure: answer "no role" before EA's code? (only the null role throws; a role with no giver is EA's own "no")
        internal static bool RnSkip(bool on, bool roleNull)
        {
            return on && roleNull;
        }

        // true: skip EA's code (the target has no role and the switch is on)
        static bool RnNoRole(Sim target)
        {
            try
            {
                if (target == null || target.SimDescription == null || target.SimDescription.AssignedRole != null) return false;
                if (!ShortcutOn("rolenullguard")) { PatchCounters()[2862]++; return false; }
                if (RnSkip(true, true)) { PatchCounters()[2861]++; return true; }
            }
            catch (Exception) { PatchCounters()[2863]++; }
            return false;
        }

        public class BRnDrinkHook
        {
            public void AddHook(InteractionObjectPair iop, Sim sim, Sim target, List<InteractionObjectPair> results)
            {
                if (sim != null && RnNoRole(target)) return;
                AddOrig(iop, sim, target, results);
            }
            public void AddOrig(InteractionObjectPair iop, Sim sim, Sim target, List<InteractionObjectPair> results) { } // BuyDrinkFromBar.Definition.AddInteractions
        }

        public class BRnSnackHook
        {
            public void AddHook(InteractionObjectPair iop, Sim sim, Sim target, List<InteractionObjectPair> results)
            {
                if (sim != null && RnNoRole(target)) return;
                AddOrig(iop, sim, target, results);
            }
            public void AddOrig(InteractionObjectPair iop, Sim sim, Sim target, List<InteractionObjectPair> results) { } // BuySnackFromBar.Definition.AddInteractions
        }

        public class BRnWaiterHook
        {
            public void AddHook(InteractionObjectPair iop, Sim actor, Sim target, List<InteractionObjectPair> results)
            {
                if (actor != null && RnNoRole(target)) return;
                AddOrig(iop, actor, target, results);
            }
            public void AddOrig(InteractionObjectPair iop, Sim actor, Sim target, List<InteractionObjectPair> results) { } // OrderFoodOnWaiter.Definition.AddInteractions
            public bool TestHook(Sim a, Sim target, bool isAutonomous, ref GreyedOutTooltipCallback greyedOutTooltipCallback)
            {
                if (a != null && !a.SimDescription.ToddlerOrBelow && RnNoRole(target)) return false;  // EA's order: toddler and social tests first, both "false" too
                return TestOrig(a, target, isAutonomous, ref greyedOutTooltipCallback);
            }
            public bool TestOrig(Sim a, Sim target, bool isAutonomous, ref GreyedOutTooltipCallback greyedOutTooltipCallback) { return false; } // OrderFoodOnWaiter.Definition.Test
        }

        // ---- B5 §2 homeworkskillbonus (tuning kHomeworkSkillBonus; docs/583 lead 2) ----------------------------------------
        // [verified, decompiled] Homework.GetCompletionRate adds kCompletetionRateModifierHomeworkSkill (5 per minute, on a base
        // of 2) times SkillLevel / MaxSkillLevel of the better homework skill (the student's or the helper's), but both are ints,
        // so the fraction is 0 below max level and the bonus only exists at level 10. Fix: after EA's rate, the missing part
        // (k * level / max, below max level) is added, scaled by EA's own end multipliers (copying x2 / x1 when old,
        // MultiTasker). Household-visible: homework goes faster with the Homework skill, as EA tuned it.
        // Counters: [2864] rates raised, [2865] seen with the switch off, [2866] errors.
        static readonly FieldInfo sHwK = typeof(Homework).GetField("kCompletetionRateModifierHomeworkSkill", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        static readonly FieldInfo sHwCopy = typeof(Homework).GetField("kCopyingMultiplier", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        static readonly FieldInfo sHwCopyOld = typeof(Homework).GetField("kCopyingMultiplierOld", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        static readonly FieldInfo sHwIsOld = typeof(Homework).GetField("IsOld", kInstAll);  // a public field, not a property (live 2026-11-15: members found=False)
        static string sHwMissing = "";

        static void ApplyHomeworkSkillBonus()
        {
            sHwMissing = ((sHwK == null ? "Homework.kCompletetionRateModifierHomeworkSkill " : "") + (sHwCopy == null || sHwCopyOld == null ? "Homework.kCopyingMultiplier(Old) " : "")
                + (sHwIsOld == null ? "Homework.IsOld" : "")).Trim();
            if (sHwMissing.Length > 0) { AppliedLog().Add("homeworkskillbonus: not found: " + sHwMissing); return; }
            Patch(typeof(Homework), "GetCompletionRate", new Type[] { typeof(Sim), typeof(bool), typeof(bool) }, typeof(BHwHook), "RateHook", "RateOrig");
        }

        // Pure: the bonus EA's integer division dropped (0 at max level, where EA's own result is already right).
        internal static float HwMissing(bool on, int level, int max, float k, float mult)
        {
            if (!on || max <= 0 || level <= 0 || level >= max) return 0f;
            return k * level / max * mult;
        }

        public class BHwHook
        {
            public float RateHook(Sim sim, bool isCopying, bool isInStudyClub)
            {
                float rate = RateOrig(sim, isCopying, isInStudyClub);
                try
                {
                    Homework hw = (Homework)(object)this;
                    Skill mine = sim.SkillManager.GetElement(SkillNames.Homework);
                    Skill helper = hw.HelpingSim == null ? null : hw.HelpingSim.SkillManager.GetElement(SkillNames.Homework);
                    Skill used = mine != null && (helper == null || mine.SkillLevel > helper.SkillLevel) ? mine : helper;  // EA's pick
                    if (used == null || used.SkillLevel <= 0 || used.SkillLevel >= used.MaxSkillLevel) return rate;
                    if (!ShortcutOn("homeworkskillbonus")) { PatchCounters()[2865]++; return rate; }
                    float mult = 1f;
                    if (isCopying) mult *= (bool)sHwIsOld.GetValue(hw) ? (float)sHwCopyOld.GetValue(null) : (float)sHwCopy.GetValue(null);
                    if (sim.HasTrait(TraitNames.MultiTasker)) mult *= TraitTuning.MultiTaskerHomeworkTimeMultiplier;
                    float add = HwMissing(true, used.SkillLevel, used.MaxSkillLevel, (float)sHwK.GetValue(null), mult);
                    if (add > 0f) { PatchCounters()[2864]++; return rate + add; }
                }
                catch (Exception) { PatchCounters()[2866]++; }
                return rate;
            }
            public float RateOrig(Sim sim, bool isCopying, bool isInStudyClub) { return 0f; } // Homework.GetCompletionRate
        }

        // ---- B5 §3 horseranchchallenge (tuning kHorseRanchChallenge; docs/583 lead 3) --------------------------------------
        // [verified, decompiled] ChallengeDelegateFunctions.VisitAHorseRanchSimCheckFunction tests the household's horse count
        // with `>= 0`, always true, so "visit a horse ranch" becomes active and visible for every human Sim, horses or not;
        // kBecomeActiveButNonVisible is unreachable. Fix: an active answer for a household with no horse becomes EA's
        // non-visible answer. Household-visible (the challenge is hidden until the household has a horse).
        // Counters: [2867] answers hidden, [2868] seen with the switch off, [2869] errors.
        static string sHrMissing = "";

        static void ApplyHorseRanchChallenge()
        {
            sHrMissing = typeof(ChallengeDelegateFunctions).GetMethod("VisitAHorseRanchSimCheckFunction", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                null, new Type[] { typeof(ChallengeNode), typeof(Sim) }, null) == null ? "ChallengeDelegateFunctions.VisitAHorseRanchSimCheckFunction" : "";
            if (sHrMissing.Length > 0) { AppliedLog().Add("horseranchchallenge: not found: " + sHrMissing); return; }
            Patch(typeof(ChallengeDelegateFunctions), "VisitAHorseRanchSimCheckFunction", new Type[] { typeof(ChallengeNode), typeof(Sim) }, typeof(BHrHook), "CheckHook", "CheckOrig");
        }

        // Pure: hide this answer? (EA's own result was "active" because of the always-true count test)
        internal static bool HrHide(bool on, bool active, int horses)
        {
            return on && active && horses <= 0;
        }

        public class BHrHook
        {
            public static ChallengeCheckResult CheckHook(ChallengeNode node, Sim Owner)
            {
                ChallengeCheckResult r = CheckOrig(node, Owner);
                if (r != ChallengeCheckResult.kBecomeActive) return r;
                try
                {
                    int horses = Owner.Household.CurrentMembers.GetActorsOfType(CASAGSAvailabilityFlags.AllHorsesMask).Count;
                    if (horses > 0) return r;
                    if (!ShortcutOn("horseranchchallenge")) { PatchCounters()[2868]++; return r; }
                    if (HrHide(true, true, horses)) { PatchCounters()[2867]++; return ChallengeCheckResult.kBecomeActiveButNonVisible; }
                }
                catch (Exception) { PatchCounters()[2869]++; }
                return r;
            }
            public static ChallengeCheckResult CheckOrig(ChallengeNode node, Sim Owner) { return ChallengeCheckResult.kThrowAway; } // the original
        }

        // ---- B5 §4 digsiteemptypick (tuning kDigSiteEmptyPick; docs/583 lead 4) --------------------------------------------
        // [verified, decompiled] DigSite.ExcavateSomething spends one of the site's treasures when the pick `Count >= 0`,
        // always true, so a dig that found nothing still uses up the site. Fix (switch on, the 4-line method replaced): the
        // same calls in the same order (GetStandardTreasure, GiveTreasuresToSim with the collection behavior), and the site
        // count drops only when the pick holds something. Household-visible: empty digs no longer exhaust a site.
        // Cap (2026-11-15 review): a site whose treasure info does not resolve always picks nothing, and the dig loop ends
        // only when the count reaches 0, so after kDsMaxEmpty empty digs in a row on one site the count drops as in EA.
        // Counters: [2870] empty digs that kept the site's treasure, [2871] empty digs with the switch off, [2872] errors.
        static readonly FieldInfo sDsLeft = typeof(DigSite).GetField("mNumTreasuresRemaining", kInstAll);
        static string sDsMissing = "";

        static void ApplyDigSiteEmptyPick()
        {
            sDsMissing = sDsLeft == null ? "DigSite.mNumTreasuresRemaining" : "";
            if (sDsMissing.Length > 0) { AppliedLog().Add("digsiteemptypick: not found: " + sDsMissing); return; }
            Patch(typeof(DigSite), "ExcavateSomething", new Type[] { typeof(InteractionInstance) }, typeof(BDsHook), "DigHook", "DigOrig");
        }

        const int kDsMaxEmpty = 3;
        static readonly Dictionary<ulong, int> sDsEmptyRun = new Dictionary<ulong, int>();  // site object id -> empty digs in a row

        // Pure: does this dig use up one of the site's treasures? emptyRun: empty digs in a row before this one.
        internal static bool DsSpend(bool on, int picked, int emptyRun)
        {
            return on ? picked > 0 || emptyRun >= kDsMaxEmpty : picked >= 0;
        }

        public class BDsHook
        {
            public void DigHook(InteractionInstance inst)
            {
                if (!ShortcutOn("digsiteemptypick")) { DigOrig(inst); return; }
                DigSite site = (DigSite)(object)this;
                Sim actor = inst.InstanceActor;
                List<IGameObject> picked = site.TreasureComponent.GetStandardTreasure(actor);
                try
                {
                    ulong id = site.ObjectId.Value;
                    int run;
                    sDsEmptyRun.TryGetValue(id, out run);
                    int n = picked == null ? 0 : picked.Count;
                    if (DsSpend(true, n, run)) { sDsLeft.SetValue(site, (int)sDsLeft.GetValue(site) - 1); sDsEmptyRun.Remove(id); }
                    else { PatchCounters()[2870]++; sDsEmptyRun[id] = run + 1; }
                }
                catch (Exception) { PatchCounters()[2872]++; }
                site.TreasureComponent.GiveTreasuresToSim(picked, actor, true);
            }
            public void DigOrig(InteractionInstance inst) { } // DigSite.ExcavateSomething
        }

        // ---- B5 §5 oppchainworld (tuning kOppChainWorld; docs/583 lead 6) --------------------------------------------------
        // [verified, decompiled] Opportunity.ClearChainHistory: ordinary categories just walk the parents. For the ADVENTURE
        // categories (China / Egypt / France) EA decides whether the chain crosses worlds with `source.World != target.World
        // && (target.World != Undefined || target.World != Undefined)`: the second test repeats the target where the source was
        // meant, so a chain whose source needs another world and whose target needs none is treated as same-world, and the
        // whole chain's history is cleared instead of the part after the world change. Fix: in exactly that case (source world
        // set, target world Undefined, EA's other clause false), EA's own world-change branch runs: walk up to the source or
        // target opportunity, then clear from its parent (recursing into ClearChainHistory as EA does). The same walk is
        // null-safe (EA's walk and its parent step can reach null: NullReferenceException or a recursion on null); when it
        // reaches null with the switch on, nothing is cleared. Ordinary categories and every other case are EA's code.
        // (Fixed 2026-11-15: the first build tested the category the wrong way round and never reached the adventure case.)
        // Household-visible (which opportunities can repeat).
        // Counters: [2873] world-change branch taken, [2874] seen with the switch off, [2875] errors.
        static readonly MethodInfo sOcReqWorld = typeof(Opportunity).GetMethod("RequiredWorldForCategory", kInstAll, null, Type.EmptyTypes, null);
        static string sOcMissing = "";

        static void ApplyOppChainWorld()
        {
            sOcMissing = sOcReqWorld == null ? "Opportunity.RequiredWorldForCategory" : "";
            if (sOcMissing.Length > 0) { AppliedLog().Add("oppchainworld: not found: " + sOcMissing); return; }
            Patch(typeof(Opportunity), "ClearChainHistory", new Type[] { typeof(Opportunity), typeof(Sim) }, typeof(BOcHook), "ClearHook", "ClearOrig");
        }

        // Pure: the case EA's typo misses. otherClause: EA's second test (opp's own target world vs its category world).
        internal static bool OcMissed(bool on, WorldName source, WorldName target, bool otherClause)
        {
            return on && !otherClause && source != target && target == WorldName.Undefined && source != WorldName.Undefined;
        }

        static Opportunity OcOriginal(Opportunity opp, OpportunityTargetTypes type)
        {
            if (type != OpportunityTargetTypes.PreviousSource && type != OpportunityTargetTypes.PreviousTarget) return opp;
            OpportunityTargetTypes t; string d;
            return OpportunityManager.FindOriginalSourceOrTarget(opp, type, out t, out d);
        }

        public class BOcHook
        {
            public static OpportunityNames ClearHook(Opportunity opp, Sim s)
            {
                Opportunity from = null;
                try
                {
                    OpportunityCategory cat = opp.OpportunityCategory;
                    if (cat == OpportunityCategory.AdventureChina || cat == OpportunityCategory.AdventureEgypt || cat == OpportunityCategory.AdventureFrance)
                    {
                        Opportunity src = OcOriginal(opp, opp.SharedData.mSourceType), tgt = OcOriginal(opp, opp.SharedData.mTargetType);
                        bool other = opp.TargetWorldRequired != WorldName.Undefined && (WorldName)sOcReqWorld.Invoke(opp, null) != opp.TargetWorldRequired;
                        bool eaBranch = src != null && tgt != null && (src.TargetWorldRequired != tgt.TargetWorldRequired && tgt.TargetWorldRequired != WorldName.Undefined || other);
                        bool missed = src != null && tgt != null && OcMissed(true, src.TargetWorldRequired, tgt.TargetWorldRequired, other);
                        if ((missed || eaBranch) && ShortcutOn("oppchainworld"))
                        {
                            Opportunity o = opp;
                            while (o != null && o.Guid != src.Guid && o.Guid != tgt.Guid) o = OpportunityManager.GetStaticOpportunity(o.SharedData.mParentOpportunity);
                            if (o != null) from = OpportunityManager.GetStaticOpportunity(o.SharedData.mParentOpportunity);
                            if (from == null) { PatchCounters()[2873]++; return opp.Guid; }  // EA would throw here: nothing cleared
                            if (!missed) return ClearOrig(opp, s);  // EA's branch with a valid walk: EA's own code
                        }
                        else if (missed) PatchCounters()[2874]++;
                    }
                }
                catch (Exception) { PatchCounters()[2875]++; from = null; }
                if (from == null) return ClearOrig(opp, s);
                PatchCounters()[2873]++;
                return Opportunity.ClearChainHistory(from, s);  // EA's recursion
            }
            public static OpportunityNames ClearOrig(Opportunity opp, Sim s) { return default(OpportunityNames); } // the original
        }

        // ---- B5 §6 teleportcommunitylot (tuning kTeleportCommunityLot; docs/583 lead 9) ------------------------------------
        // [verified, decompiled] Teleporter+TeleportToCommunityLot.Definition.Test returns `GetValidCommunityLots(a).Count >= 0`,
        // always true, so "Teleport to community lot" is offered in a world with no valid community lot (Run then fails
        // cleanly). Fix: EA's true becomes false when that collection is empty. Household-visible (menu only).
        // Counters: [2876] options hidden, [2877] seen with the switch off, [2878] errors.
        static readonly Type sTcDef = TcDef();
        static string sTcMissing = "";

        static Type TcDef()
        {
            Type t = typeof(Teleporter).GetNestedType("TeleportToCommunityLot", BindingFlags.Public | BindingFlags.NonPublic);
            return t == null ? null : t.GetNestedType("Definition", BindingFlags.Public | BindingFlags.NonPublic);
        }

        static void ApplyTeleportCommunityLot()
        {
            sTcMissing = sTcDef == null ? "Teleporter+TeleportToCommunityLot+Definition" : "";
            if (sTcMissing.Length > 0) { AppliedLog().Add("teleportcommunitylot: not found: " + sTcMissing); return; }
            Type def = sTcDef;
            Patch(def, "Test", new Type[] { typeof(Sim), typeof(Teleporter), typeof(bool), typeof(GreyedOutTooltipCallback).MakeByRefType() }, typeof(BTcHook), "TestHook", "TestOrig");
        }

        // Pure: refuse EA's true?
        internal static bool TcRefuse(bool on, bool eaTrue, int lots)
        {
            return on && eaTrue && lots <= 0;
        }

        public class BTcHook
        {
            public bool TestHook(Sim a, Teleporter target, bool isAutonomous, ref GreyedOutTooltipCallback greyedOutTooltipCallback)
            {
                bool r = TestOrig(a, target, isAutonomous, ref greyedOutTooltipCallback);
                if (!r) return r;
                try
                {
                    ICollection<Lot> lots = CarOwnable.GetValidCommunityLots(a);
                    int n = lots == null ? 0 : lots.Count;
                    if (n > 0) return r;
                    if (!ShortcutOn("teleportcommunitylot")) { PatchCounters()[2877]++; return r; }
                    if (TcRefuse(true, true, n)) { PatchCounters()[2876]++; return false; }
                }
                catch (Exception) { PatchCounters()[2878]++; }
                return r;
            }
            public bool TestOrig(Sim a, Teleporter target, bool isAutonomous, ref GreyedOutTooltipCallback greyedOutTooltipCallback) { return false; } // the original
        }

        // ---- B5 §7 photosubjectstars (tuning kPhotoSubjectStars; docs/583 lead 11) -----------------------------------------
        // [verified, decompiled] SubjectDefinition.Difficulty divides SkillPoints and Value (ints) by the largest ones (ints),
        // so both fractions are 0 below the maximum and the photography journal shows 1 star for almost every subject (its
        // only reader, Photography's journal row, maps Difficulty * 5 to stars). Fix: the same weighted mean with float
        // division. UI only. Counters: [2879] values corrected, [2880] seen with the switch off, [2881] errors.
        static string sPsMissing = "";

        static void ApplyPhotoSubjectStars()
        {
            sPsMissing = typeof(SubjectDefinition).GetProperty("Difficulty") == null ? "SubjectDefinition.Difficulty" : "";
            if (sPsMissing.Length > 0) { AppliedLog().Add("photosubjectstars: not found: " + sPsMissing); return; }
            Patch(typeof(SubjectDefinition), "get_Difficulty", Type.EmptyTypes, typeof(BPsHook), "DifficultyHook", "DifficultyOrig");
        }

        // Pure: the weighted mean with float division (0 when a maximum is 0, as EA).
        internal static float PsDifficulty(int points, int maxPoints, int value, int maxValue, float wPoints, float wValue)
        {
            if (maxPoints == 0 || maxValue == 0) return 0f;
            float w = wPoints + wValue;
            return ((float)points / maxPoints * wPoints + (float)value / maxValue * wValue) / w;
        }

        public class BPsHook
        {
            public float DifficultyHook()
            {
                float ea = DifficultyOrig();
                try
                {
                    SubjectDefinition d = (SubjectDefinition)(object)this;
                    float f = PsDifficulty(d.SkillPoints, PhotographySubjects.sMaxSubjectSkillPoints, d.Value, PhotographySubjects.sMaxSubjectValue,
                        PhotographySubjects.kDifficultyWeightSubjectSkillPoints, PhotographySubjects.kDifficultyWeightSubjectValue);
                    if (f == ea) return ea;
                    if (!ShortcutOn("photosubjectstars")) { PatchCounters()[2880]++; return ea; }
                    PatchCounters()[2879]++;
                    return f;
                }
                catch (Exception) { PatchCounters()[2881]++; }
                return ea;
            }
            public float DifficultyOrig() { return 0f; } // SubjectDefinition.get_Difficulty
        }
    }
}
