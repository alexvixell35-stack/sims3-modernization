using System;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.EventSystem;
using Sims3.Gameplay.Objects;
using Sims3.Gameplay.Skills;

namespace Ts3Fixes
{
    // Into the Future logic fixes (docs/572 §9-12), each off by default.
    //
    // plumbotfacelistener (kPlumbotFaceListener, docs/572 §9) [verified, OccultRobot.cs]: every plumbot with a face VFX
    //   registers OnConversationLeft for kLeftConversation with no actor filter, so it reacts to every Sim in town leaving
    //   any conversation: it re-adds a 2-minute persisted alarm, flags "conversation ended" and rebuilds its face VFX,
    //   which cuts its own conversation-driven face short. Fix: the handler acts only when the bot itself is the Sim who
    //   left or the one left. Counters [2622] other Sims' events ignored, [2623] errors.
    // efficientchip (kEfficientChip, docs/572 §10) [verified, TraitChip.cs SetLearningModifier / Add/RemoveTraitChipFromSim]:
    //   the robot skill-gain modifier sums the Ability to Learn and Efficient chip bonuses, but it is recomputed only when
    //   the Ability to Learn chip is added or removed. Efficient alone (or added after) gives no bonus; removing Efficient
    //   keeps it. Fix: after SkillManager.RobotSkillsChanged (called at the end of every chip add / remove) and at world
    //   load, the modifier is recomputed the way SetLearningModifier does. Counters [2624] modifiers corrected, [2625] errors.
    // sentiencename (kSentienceName, docs/572 §11) [verified, TraitChip.cs / Sim.Name]: installing the Sentience chip saves
    //   mNonSentientName = OwnerSim.Name, which is the FULL name; removing it sets FirstName to that, so "Bob Smith" becomes
    //   "Bob Smith Smith". Fix: before the removal runs, a saved name that ends in " " + the last name loses that suffix.
    //   Counters [2626] names repaired, [2627] errors.
    // jetpackflyingbuff (kJetpackFlyingBuff, docs/572 §12) [verified, Jetpack.cs FlyAround]: Fly Around adds Free Flying
    //   paused and unpauses it only on the normal end of Run; Cleanup does not, so a flight that is reset or fails mid-loop
    //   leaves the buff (and its accumulated death chance) paused forever. Fix: FlyAround.Cleanup unpauses a paused Free
    //   Flying buff of a living Sim. Counters [2628] buffs unpaused, [2629] errors.
    // Checks: FutureLogicChecks.cs.
    public static partial class Fixes
    {
        const BindingFlags kFlAll = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
        static readonly FieldInfo sPfOwner = typeof(OccultRobot).GetField("mOwningSim", kFlAll);
        static readonly FieldInfo sEcAtl = typeof(TraitChip).GetField("kAbilityToLearnSkillGainModifier", kFlAll);
        static readonly FieldInfo sScName = typeof(TraitChip).GetField("mTraitChipName", kFlAll);

        internal static string FlMissing()
        {
            string m = "";
            if (sPfOwner == null) m += "OccultRobot.mOwningSim ";
            if (sEcAtl == null) m += "TraitChip.kAbilityToLearnSkillGainModifier ";
            if (sScName == null) m += "TraitChip.mTraitChipName ";
            return m.Trim();
        }

        static void ApplyFutureLogic()
        {
            if (sPfOwner != null) Patch(typeof(OccultRobot), "OnConversationLeft", typeof(PlumbotFaceHook), "LeftHook", "LeftOrig");
            if (sEcAtl != null) Patch(typeof(SkillManager), "RobotSkillsChanged", typeof(EfficientChipHook), "ChangedHook", "ChangedOrig");
            if (sScName != null) Patch(typeof(TraitChip), "RemoveTraitChipFromSim", typeof(SentienceNameHook), "RemoveHook", "RemoveOrig");
            Patch(typeof(Jetpack.FlyAround), "Cleanup", typeof(JetpackBuffHook), "CleanHook", "CleanOrig");
            if (FlMissing().Length > 0) AppliedLog().Add("future logic: members not found (" + FlMissing() + ")");
        }

        // ---- plumbotfacelistener ----
        internal static bool PfIgnore(bool on, bool botLeft, bool botWasLeft)
        {
            return on && !botLeft && !botWasLeft;
        }

        public class PlumbotFaceHook
        {
            public ListenerAction LeftHook(Sims3.Gameplay.EventSystem.Event e)
            {
                try
                {
                    if (ShortcutOn("plumbotfacelistener") && e != null)
                    {
                        Sim bot = sPfOwner.GetValue((object)this) as Sim;
                        if (bot != null && PfIgnore(true, e.Actor == bot, e.TargetObject == bot)) { PatchCounters()[2622]++; return ListenerAction.Keep; }
                    }
                }
                catch (Exception) { PatchCounters()[2623]++; }
                return LeftOrig(e);
            }
            public ListenerAction LeftOrig(Sims3.Gameplay.EventSystem.Event e) { return ListenerAction.Keep; }
        }

        // ---- efficientchip ----
        // The modifier SetLearningModifier would compute for these chips.
        internal static float EcModifier(bool abilityToLearn, bool efficient, float atlBonus, float efficientBonus)
        {
            return (abilityToLearn ? atlBonus : 0f) + (efficient ? efficientBonus : 0f);
        }

        static bool EcSync(SimDescription d)
        {
            if (d == null || d.TraitChipManager == null || d.SkillManager == null) return false;
            float want = EcModifier(d.TraitChipManager.HasElement((ulong)TraitChipName.AbilityToLearn), d.TraitChipManager.HasElement((ulong)TraitChipName.Efficient),
                (float)sEcAtl.GetValue(null), Sims3.Gameplay.TuningValues.TraitTuning.EfficientChipSkillGainMultiplier);
            if (d.SkillManager.RobotLearningModifier == want) return false;
            d.SkillManager.RobotLearningModifier = want;
            return true;
        }

        public class EfficientChipHook
        {
            public void ChangedHook()
            {
                try
                {
                    if (ShortcutOn("efficientchip") && EcSync(((SkillManager)(object)this).ActorDescription)) PatchCounters()[2624]++;
                }
                catch (Exception) { PatchCounters()[2625]++; }
                ChangedOrig();
            }
            public void ChangedOrig() { }
        }

        // World load finished (Instantiator): bots saved with a stale modifier.
        public static void EfficientChipWorldLoad()
        {
            try
            {
                if (!ShortcutOn("efficientchip") || sEcAtl == null) return;
                foreach (SimDescription d in Household.EveryHumanSimDescription())
                    if (d != null && d.IsEP11Bot && EcSync(d)) PatchCounters()[2624]++;
            }
            catch (Exception) { PatchCounters()[2625]++; }
        }

        // ---- sentiencename ----
        // The saved non-sentient name with a trailing " <last name>" removed (EA saved the full name).
        internal static string ScFirstName(bool on, string saved, string lastName)
        {
            if (!on || string.IsNullOrEmpty(saved) || string.IsNullOrEmpty(lastName)) return saved;
            string suffix = " " + lastName;
            return saved.Length > suffix.Length && saved.EndsWith(suffix) ? saved.Substring(0, saved.Length - suffix.Length) : saved;
        }

        public class SentienceNameHook
        {
            public void RemoveHook()
            {
                try
                {
                    TraitChip me = (TraitChip)(object)this;
                    SimDescription d = me.Owner;
                    if (ShortcutOn("sentiencename") && d != null && d.TraitChipManager != null && (TraitChipName)sScName.GetValue(me) == TraitChipName.Sentience)
                    {
                        string saved = d.TraitChipManager.mNonSentientName, fixedName = ScFirstName(true, saved, d.LastName);
                        if (fixedName != saved) { d.TraitChipManager.mNonSentientName = fixedName; PatchCounters()[2626]++; }
                    }
                }
                catch (Exception) { PatchCounters()[2627]++; }
                RemoveOrig();
            }
            public void RemoveOrig() { }
        }

        // ---- jetpackflyingbuff ----
        internal static bool JbUnpause(bool on, bool hasBuff, bool paused, bool dead)
        {
            return on && hasBuff && paused && !dead;
        }

        public class JetpackBuffHook
        {
            public void CleanHook()
            {
                CleanOrig();
                try
                {
                    if (!ShortcutOn("jetpackflyingbuff")) return;
                    Sim actor = ((Jetpack.FlyAround)(object)this).Actor;
                    BuffInstance bi = actor == null || actor.BuffManager == null ? null : actor.BuffManager.GetElement(BuffNames.FreeFlying);
                    if (JbUnpause(true, bi != null, bi != null && bi.mTimeoutPaused, actor == null || actor.SimDescription == null || actor.SimDescription.IsDead))
                    {
                        actor.BuffManager.UnpauseBuff(BuffNames.FreeFlying);
                        PatchCounters()[2628]++;
                    }
                }
                catch (Exception) { PatchCounters()[2629]++; }
            }
            public void CleanOrig() { }
        }
    }
}
