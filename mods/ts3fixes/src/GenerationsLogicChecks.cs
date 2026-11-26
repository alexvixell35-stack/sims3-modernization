using System;
using Sims3.Gameplay.EventSystem;
using Sims3.Gameplay.Objects.Vehicles;
using Sims3.Gameplay.Utilities;

namespace Ts3Fixes
{
    // Harness checks for GenerationsLogic.cs (docs/571 §1-4). All read-only: they run the fixes' decisions with the
    // switch's current value plus explicit controls, and print which EA members were not found.
    //   off: verdict BROKEN (EA's path); on: verdict FIXED. A failed control or a missing member: UNEXPECTED.
    public static partial class Fixes
    {
        static string GlVerdict(bool on, bool fixedNow, bool controlsOk, bool membersOk)
        {
            if (!controlsOk || !membersOk) return "UNEXPECTED";
            return !on ? (fixedNow ? "UNEXPECTED" : "BROKEN") : (fixedNow ? "FIXED" : "UNEXPECTED");
        }

        static string GlMembers(string missing)
        {
            return "members found=" + (missing.Length == 0) + (missing.Length == 0 ? "" : " (missing: " + missing + ")");
        }

        // BirthdayAgersCheck run: a second Sim ages up at the party (EA's listener answer for the first ager is Remove).
        //   Controls: a Sim who is not at the party (EA answers Keep) stays Keep either way.
        public static object BirthdayAgersCheck(string what)
        {
            int[] c = PatchCounters();
            string missing = BaMissing();
            string counters = "birthdayagers: later agers recorded 2577=" + c[3960] + ", listeners removed at cleanup 2578=" + c[3961] + ", errors 2579=" + c[3962] + "; " + GlMembers(missing);
            if (what == "result") return counters;
            if (what != "run") return "birthdayagers <run|result>";
            bool on = ShortcutOn("birthdayagers");
            bool stillListening = BaAnswer(on, ListenerAction.Remove) == ListenerAction.Keep;
            bool ctlKeep = BaAnswer(on, ListenerAction.Keep) == ListenerAction.Keep;
            bool controls = ctlKeep;
            return "birthdayagers check (switch " + (on ? "on" : "off") + "): after the first ager the party still listens (a second ager is recorded)=" + stillListening
                + "; control: non-party ager stays Keep=" + ctlKeep + "; controls ok=" + controls
                + "; verdict: " + GlVerdict(on, stillListening, controls, missing.Length == 0) + "; " + counters;
        }

        // SneakOutListenerCheck run: a teen caught sneaking out (no longer grounded) later rides the school carpool, and
        //   the left-over sneak listener fires.
        //   Controls: a grounded teen leaving on foot (a real sneak-out) is not skipped; switch off never skips.
        public static object SneakOutListenerCheck(string what)
        {
            int[] c = PatchCounters();
            string missing = SoMissing();
            string counters = "sneakoutlistener: stale sneak-outs skipped 2580=" + c[3963] + ", duplicate return-home listeners removed 2581=" + c[3964]
                + ", left-over sneak listeners removed 2582=" + c[3965] + ", errors 2583=" + c[3966] + "; " + GlMembers(missing);
            if (what == "result") return counters;
            if (what != "run") return "sneakoutlistener <run|result>";
            bool on = ShortcutOn("sneakoutlistener");
            bool skipSchool = SoSkip(on, true, true, true);
            bool skipUngrounded = SoSkip(on, false, false, false);
            bool skipped = skipSchool && skipUngrounded;
            bool realKept = !SoSkip(on, true, false, false);
            bool offNever = !SoSkip(false, false, true, true);
            bool controls = realKept && offNever;
            return "sneakoutlistener check (switch " + (on ? "on" : "off") + "): stale sneak-out skipped (school carpool=" + skipSchool + ", grounding over=" + skipUngrounded + ")"
                + "; control: real sneak-out applied=" + realKept + ", switch off never skips=" + offNever + "; controls ok=" + controls
                + "; verdict: " + GlVerdict(on, skipped, controls, missing.Length == 0) + "; " + counters;
        }

        // PromCarpoolCheck run: the prom cleans up while its weekly limo carpool is still scheduled.
        //   Controls: no carpool -> nothing removed; a carpool CarpoolManager no longer schedules -> not removed again.
        public static object PromCarpoolCheck(string what)
        {
            int[] c = PatchCounters();
            string missing = sPcCarpool == null ? "PromSituation.mCarpool" : "";
            int scheduled = -1;
            try { scheduled = CarpoolManager.sScheduledCarpools.Count; } catch (Exception) { }
            string counters = "promcarpool: carpools removed 2584=" + c[3618] + ", errors 2585=" + c[3619] + "; scheduled carpools now=" + scheduled + "; " + GlMembers(missing);
            if (what == "result") return counters;
            if (what != "run") return "promcarpool <run|result>";
            bool on = ShortcutOn("promcarpool");
            bool removed = PcRemove(on, true, true);
            bool ctlNone = !PcRemove(on, false, false);
            bool ctlGone = !PcRemove(on, true, false);
            bool controls = ctlNone && ctlGone;
            return "promcarpool check (switch " + (on ? "on" : "off") + "): weekly prom limo removed at cleanup=" + removed
                + "; control: no carpool untouched=" + ctlNone + ", unscheduled carpool untouched=" + ctlGone + "; controls ok=" + controls
                + "; verdict: " + GlVerdict(on, removed, controls, missing.Length == 0) + "; " + counters;
        }

        // CurfewAlarmCheck run: world quit while the curfew police alarm handle is set.
        //   Controls: an invalid handle is left alone.
        public static object CurfewAlarmCheck(string what)
        {
            int[] c = PatchCounters();
            string missing = sCaHandle == null ? "SimUpdate.sCurfewCheckPoliceAlarmHandle" : "";
            string now = "?";
            try { if (sCaHandle != null) now = ((AlarmHandle)sCaHandle.GetValue(null)) == AlarmHandle.kInvalidHandle ? "invalid" : "set"; } catch (Exception) { }
            string counters = "curfewalarm: handles reset at quit 2586=" + c[3967] + ", errors 2587=" + c[3968] + "; handle now=" + now + "; " + GlMembers(missing);
            if (what == "result") return counters;
            if (what != "run") return "curfewalarm <run|result>";
            bool on = ShortcutOn("curfewalarm");
            bool reset = CaReset(on, true);
            bool ctlInvalid = !CaReset(on, false);
            return "curfewalarm check (switch " + (on ? "on" : "off") + "): curfew alarm handle reset at quit=" + reset
                + "; control: invalid handle untouched=" + ctlInvalid + "; controls ok=" + ctlInvalid
                + "; verdict: " + GlVerdict(on, reset, ctlInvalid, missing.Length == 0) + "; " + counters;
        }
    }
}
