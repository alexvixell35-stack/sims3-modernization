using System;
using System.Reflection;
using Sims3.Gameplay.ActorSystems;

namespace Ts3Fixes
{
    // Harness check for SupernaturalLogic.cs (docs/571 §8). Read-only: runs the re-register decision with controls.
    public static partial class Fixes
    {
        // PermaZombieMoonCheck run: a saved PermaZombie buff is loaded and kept; its saved listener is not registered.
        //   Controls: another transformation buff (werewolf, vampire...) is not touched; a buff the load dropped gets no
        //   listener; an instance whose listener EA's fixup just registered is not registered twice.
        public static object PermaZombieMoonCheck(string what)
        {
            int[] c = PatchCounters();
            bool members = typeof(BuffPermaZombie.BuffInstancePermaZombie).GetField("mListener", BindingFlags.Instance | BindingFlags.Public) != null
                && typeof(BuffTransformation.BuffTransformationInstance).GetMethod("OnLoadFixup", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly) != null;
            string counters = "permazombiemoon: listeners re-registered at load 2595=" + c[2595] + ", errors 2596=" + c[2596] + "; "
                + GlMembers(members ? "" : "BuffInstancePermaZombie.mListener / BuffTransformationInstance.OnLoadFixup");
            if (what == "result") return counters;
            if (what != "run") return "permazombiemoon <run|result>";
            bool on = ShortcutOn("permazombiemoon");
            bool rearmed = PzRearm(on, true, true, false);
            bool ctlOther = !PzRearm(on, false, true, false);
            bool ctlDropped = !PzRearm(on, true, false, false);
            bool ctlTwice = !PzRearm(on, true, true, true);
            bool controls = ctlOther && ctlDropped && ctlTwice;
            return "permazombiemoon check (switch " + (on ? "on" : "off") + "): loaded PermaZombie gets a live new-moon listener=" + rearmed
                + "; control: other transformation untouched=" + ctlOther + ", dropped buff untouched=" + ctlDropped + ", no double listener=" + ctlTwice
                + "; controls ok=" + controls + "; verdict: " + GlVerdict(on, rearmed, controls, members) + "; " + counters;
        }

        // LunacySocialsCheck run: three Sims get Lunacy at the full moon; how many keep a random-social alarm?
        //   Controls: one Sim -> 1 alarm and no Sim -> 0 in both states.
        public static object LunacySocialsCheck(string what)
        {
            int[] c = PatchCounters();
            bool members = sLnActor != null && typeof(BuffLunacy).GetField("mRandomSocialAlarm") != null && typeof(BuffLunacy).GetMethod("AddRandomSocial") != null;
            int lunatics = 0;
            try { foreach (Sims3.Gameplay.Actors.Sim sim in Sims3.Gameplay.Queries.GetObjects<Sims3.Gameplay.Actors.Sim>()) if (sim.BuffManager != null && sim.BuffManager.HasElement(BuffNames.Lunacy)) lunatics++; } catch (Exception) { }
            string counters = "lunacysocials: per-Sim alarms scheduled 2604=" + c[2604] + ", re-armed at load 2605=" + c[2605] + ", errors 2606=" + c[2606]
                + "; Sims with Lunacy now=" + lunatics + ", our alarms now=" + sLnAlarms.Count + "; " + GlMembers(members ? "" : "BuffLunacy.mActorSim / mRandomSocialAlarm / AddRandomSocial");
            if (what == "result") return counters;
            if (what != "run") return "lunacysocials <run|result>";
            bool on = ShortcutOn("lunacysocials");
            int alive = LnAliveAfter(on, 3);
            bool all = alive == 3;
            bool ctlOne = LnAliveAfter(on, 1) == 1, ctlNone = LnAliveAfter(on, 0) == 0;
            bool controls = ctlOne && ctlNone;
            return "lunacysocials check (switch " + (on ? "on" : "off") + "): 3 lunatics, alarms alive=" + alive + " (every lunatic acts out=" + all + ")"
                + "; control: one lunatic keeps 1=" + ctlOne + ", none keeps 0=" + ctlNone + "; controls ok=" + controls
                + "; verdict: " + GlVerdict(on, all, controls, members) + "; " + counters;
        }

        // FairyAuraTimerCheck run: a fairy saved with a Creative aura is loaded; does the re-added aura keep its timer?
        //   Controls: Soothe is left to EA's own copy; a non-aura buff is never touched.
        public static object FairyAuraTimerCheck(string what)
        {
            int[] c = PatchCounters();
            bool members = typeof(OccultFairy).GetMethod("FixUpOccult", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly) != null;
            string counters = "fairyauratimer: timers restored 2607=" + c[2607] + ", errors 2608=" + c[2608] + "; " + GlMembers(members ? "" : "OccultFairy.FixUpOccult");
            if (what == "result") return counters;
            if (what != "run") return "fairyauratimer <run|result>";
            bool on = ShortcutOn("fairyauratimer");
            bool creative = FaRestore(on, BuffNames.FairyAuraProjectingCreative), mind = FaRestore(on, BuffNames.FairyAuraProjectingMind);
            bool restored = creative && mind;
            bool ctlSoothe = !FaRestore(on, BuffNames.FairyAuraProjectingSoothe), ctlOther = !FaRestore(on, BuffNames.Lunacy);
            bool controls = ctlSoothe && ctlOther;
            return "fairyauratimer check (switch " + (on ? "on" : "off") + "): Creative/Mind aura keeps its timer on load=" + restored + " (creative=" + creative + ", mind=" + mind + ")"
                + "; control: Soothe left to EA=" + ctlSoothe + ", other buff untouched=" + ctlOther + "; controls ok=" + controls
                + "; verdict: " + GlVerdict(on, restored, controls, members) + "; " + counters;
        }
    }
}
