using System;
using Sims3.Gameplay.Objects;

namespace Ts3Fixes
{
    // Harness checks for FutureLogic.cs (docs/572 §9-12). Read-only decisions with controls.
    public static partial class Fixes
    {
        // PlumbotFaceListenerCheck run: another Sim in town leaves a conversation the bot is not in.
        //   Controls: the bot leaving, or being left, still updates its face (both states).
        public static object PlumbotFaceListenerCheck(string what)
        {
            int[] c = PatchCounters();
            string missing = sPfOwner == null ? "OccultRobot.mOwningSim" : "";
            string counters = "plumbotfacelistener: other Sims' events ignored 2622=" + c[2622] + ", errors 2623=" + c[2623] + "; " + GlMembers(missing);
            if (what == "result") return counters;
            if (what != "run") return "plumbotfacelistener <run|result>";
            bool on = ShortcutOn("plumbotfacelistener");
            bool ignored = PfIgnore(on, false, false);
            bool ctlLeft = !PfIgnore(on, true, false), ctlWasLeft = !PfIgnore(on, false, true);
            bool controls = ctlLeft && ctlWasLeft;
            return "plumbotfacelistener check (switch " + (on ? "on" : "off") + "): unrelated conversation end ignored=" + ignored
                + "; control: bot left handled=" + ctlLeft + ", bot was left handled=" + ctlWasLeft + "; controls ok=" + controls
                + "; verdict: " + GlVerdict(on, ignored, controls, missing.Length == 0) + "; " + counters;
        }

        // EfficientChipCheck run: a bot with only the Efficient chip (bonus 0.5 stands in for the tuning) after a chip change.
        //   Controls: no chip -> 0; Ability to Learn alone -> its bonus (EA's own case).
        public static object EfficientChipCheck(string what)
        {
            int[] c = PatchCounters();
            string missing = sEcAtl == null ? "TraitChip.kAbilityToLearnSkillGainModifier" : "";
            string counters = "efficientchip: modifiers corrected 2624=" + c[2624] + ", errors 2625=" + c[2625] + "; " + GlMembers(missing);
            if (what == "result") return counters;
            if (what != "run") return "efficientchip <run|result>";
            bool on = ShortcutOn("efficientchip");
            // EA leaves the modifier at its old value (0) when only Efficient changes; the fix recomputes it
            float after = on ? EcModifier(false, true, 2f, 0.5f) : 0f;
            bool bonus = after == 0.5f;
            bool ctlNone = EcModifier(false, false, 2f, 0.5f) == 0f, ctlAtl = EcModifier(true, false, 2f, 0.5f) == 2f;
            bool controls = ctlNone && ctlAtl;
            return "efficientchip check (switch " + (on ? "on" : "off") + "): Efficient-only bot skill bonus=" + after + " (bonus applied=" + bonus + ")"
                + "; control: no chip 0=" + ctlNone + ", Ability to Learn alone=" + ctlAtl + "; controls ok=" + controls
                + "; verdict: " + GlVerdict(on, bonus, controls, missing.Length == 0) + "; " + counters;
        }

        // SentienceNameCheck run: "Bob Smith" saved for a bot named Bob, last name Smith.
        //   Controls: a saved first name only stays; a first name that is only the last name stays.
        public static object SentienceNameCheck(string what)
        {
            int[] c = PatchCounters();
            string missing = sScName == null ? "TraitChip.mTraitChipName" : "";
            string counters = "sentiencename: names repaired 2626=" + c[2626] + ", errors 2627=" + c[2627] + "; " + GlMembers(missing);
            if (what == "result") return counters;
            if (what != "run") return "sentiencename <run|result>";
            bool on = ShortcutOn("sentiencename");
            string restored = ScFirstName(on, "Bob Smith", "Smith");
            bool ok = restored == "Bob";
            bool ctlFirst = ScFirstName(on, "Bob", "Smith") == "Bob", ctlSame = ScFirstName(on, "Smith", "Smith") == "Smith";
            bool controls = ctlFirst && ctlSame;
            return "sentiencename check (switch " + (on ? "on" : "off") + "): first name restored as '" + restored + "' (correct=" + ok + ")"
                + "; control: first name only kept=" + ctlFirst + ", name equal to last name kept=" + ctlSame + "; controls ok=" + controls
                + "; verdict: " + GlVerdict(on, ok, controls, missing.Length == 0) + "; " + counters;
        }

        // JetpackFlyingBuffCheck run: Fly Around is reset mid-flight with Free Flying paused.
        //   Controls: a normal end (already unpaused) and a dead Sim are left alone.
        public static object JetpackFlyingBuffCheck(string what)
        {
            int[] c = PatchCounters();
            bool members = typeof(Jetpack.FlyAround).GetMethod("Cleanup") != null;
            string counters = "jetpackflyingbuff: buffs unpaused 2628=" + c[2628] + ", errors 2629=" + c[2629] + "; " + GlMembers(members ? "" : "Jetpack.FlyAround.Cleanup");
            if (what == "result") return counters;
            if (what != "run") return "jetpackflyingbuff <run|result>";
            bool on = ShortcutOn("jetpackflyingbuff");
            bool unpaused = JbUnpause(on, true, true, false);
            bool ctlNormal = !JbUnpause(on, true, false, false), ctlDead = !JbUnpause(on, true, true, true);
            bool controls = ctlNormal && ctlDead;
            return "jetpackflyingbuff check (switch " + (on ? "on" : "off") + "): paused Free Flying unpaused at cleanup=" + unpaused
                + "; control: normal end untouched=" + ctlNormal + ", dead Sim untouched=" + ctlDead + "; controls ok=" + controls
                + "; verdict: " + GlVerdict(on, unpaused, controls, members) + "; " + counters;
        }
    }
}
