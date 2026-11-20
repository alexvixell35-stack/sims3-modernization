using System;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;
using Sims3.UI;

namespace Ts3Fixes
{
    // sandboxtns (kSandboxTns, docs/575 §10) [verified, decompiled]: every Sim in town who plays in the sand pops a
    // notice for the player (player report 2026-11-20: "notifications from other sims playing in the sand and finding
    // objects, not my household sims"). PlayInSandJig.FindSomething (play in sand, the sandbox included) calls
    // NotificationSystem.ShowWithAutomaticGender(TNSNames.SandboxFoundObject, Actor, found), and Sandbox's bury step calls
    // it with TNSNames.SandboxBuryObject, with no check on who the Sim is; NotificationSystem.Show checks only that the
    // notice exists and its pack is installed.
    // Fix: those two notices are dropped when the Sim is not exempt (Fixes.HouseholdExempt: household, selected and
    // unknown-scope Sims still get them). The find, the reward and the bury run as EA's; only the notice is skipped.
    // Counters [2174] notices dropped, [2175] errors. Checks: SandboxTnsCheck below.
    public static partial class Fixes
    {
        // Pure: is this sand notice dropped?
        internal static bool SbtDrop(bool on, bool sandNotice, bool simKnown, bool exempt)
        {
            return on && sandNotice && simKnown && !exempt;
        }

        static bool SbtIsSand(TNSNames id)
        {
            return id == TNSNames.SandboxFoundObject || id == TNSNames.SandboxBuryObject;
        }

        static void ApplySandboxTns()
        {
            Patch(typeof(NotificationSystem), "ShowWithAutomaticGender", new Type[] { typeof(TNSNames), typeof(object[]) },
                typeof(SbtHook), "ShowHook", "ShowOrig");
        }

        public class SbtHook
        {
            public static StyledNotification ShowHook(TNSNames notificationId, params object[] actorTargetEtc)
            {
                try
                {
                    if (SbtIsSand(notificationId))
                    {
                        Sim sim = actorTargetEtc != null && actorTargetEtc.Length > 0 ? actorTargetEtc[0] as Sim : null;
                        if (SbtDrop(ShortcutOn("sandboxtns"), true, sim != null, sim != null && HouseholdExempt(sim)))
                        {
                            PatchCounters()[2174]++;
                            return null;
                        }
                    }
                }
                catch (ResetException) { throw; }
                catch (Exception) { PatchCounters()[2175]++; }
                return ShowOrig(notificationId, actorTargetEtc);
            }
            public static StyledNotification ShowOrig(TNSNames notificationId, params object[] actorTargetEtc) { return null; } // NotificationSystem.ShowWithAutomaticGender
        }

        // Harness: "run" pure decisions with controls. "live": EA's own call for the sand-found notice, once with a Sim
        // outside the household and once with the active Sim; a notice shown or not is read from the return value.
        public static object SandboxTnsCheck(string what)
        {
            string counters = B582Counters("sandboxtns", 2174, new[] { "notices dropped", "errors" }, "");
            if (what == "result") return counters;
            bool on = ShortcutOn("sandboxtns");
            if (what == "live") return SbtLive();
            if (what != "run") return "sandboxtns <run|live|result>";
            bool fixedNow = SbtDrop(on, true, true, false);
            bool controls = !SbtDrop(on, true, true, true) && !SbtDrop(on, false, true, false) && !SbtDrop(on, true, false, false);
            return B582Line("sandboxtns", on, "a townie's sand notice dropped", fixedNow,
                "household / other notices / no Sim left to EA=" + controls, controls, "", counters);
        }

        static string SbtLive()
        {
            Sim mine = Sim.ActiveActor, other = null;
            foreach (Sim s in LotManager.Actors)
                if (s != null && !HouseholdExempt(s)) { other = s; break; }
            if (mine == null || other == null) return "need an active Sim and a Sim outside the household";
            bool otherShown = NotificationSystem.ShowWithAutomaticGender(TNSNames.SandboxFoundObject, other, other) != null;
            bool mineShown = NotificationSystem.ShowWithAutomaticGender(TNSNames.SandboxFoundObject, mine, mine) != null;
            return "townie " + other.FullName + ": notice shown=" + otherShown + (otherShown ? " (BROKEN)" : " (FIXED)")
                + "; active Sim " + mine.FullName + ": shown=" + mineShown + (mineShown ? " (kept)" : " (missing: check)");
        }
    }
}
