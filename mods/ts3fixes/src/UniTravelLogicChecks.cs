using System;

namespace Ts3Fixes
{
    // Harness checks for UniTravelLogic.cs (docs/573 §7-9). Pure decisions with controls; nothing in the world is touched.
    public static partial class Fixes
    {
        static string UtPick(params string[] names)
        {
            string all = UtMissing(), m = "";
            foreach (string n in names) if (all.IndexOf(n) >= 0) m += n + " ";
            return m.Trim();
        }

        // ResumeTnsCheck run: GraduateInPlace grants a degree's rewards in the home world (return-home graduation).
        //   Controls: in the University world nothing is shown; a degree already granted (ceremony, EA shows it) is skipped.
        public static object ResumeTnsCheck(string what)
        {
            string missing = UtPick("CollegeGraduation.GraduateInPlace.mDegree");
            string counters = PhCounters("resumetns", 2648, "notices shown", missing);
            if (what == "result") return counters;
            if (what != "run") return "resumetns <run|result>";
            bool on = ShortcutOn("resumetns");
            bool shown = RtShow(on, false, false, true);
            bool ctlUni = !RtShow(on, true, false, true), ctlGranted = !RtShow(on, false, true, true);
            bool controls = ctlUni && ctlGranted;
            return "resumetns check (switch " + (on ? "on" : "off") + "): resume notice after return-home graduation=" + shown
                + "; control: University world skipped=" + ctlUni + ", already granted skipped=" + ctlGranted + "; controls ok=" + controls
                + "; verdict: " + GlVerdict(on, shown, controls, missing.Length == 0) + "; " + counters;
        }

        // UniTravelFlagCheck run: enrolment set the travel flag and no moving van was queued.
        //   Controls: a queued van keeps the flag (its Run/Cleanup clear it); a flag that was not set before is left alone.
        public static object UniTravelFlagCheck(string what)
        {
            string missing = UtPick("TravelUtil.TriggerTravelToUniversityWorld(List,int)");
            string counters = PhCounters("unitravelflag", 2650, "flags cleared", missing);
            if (what == "result") return counters;
            if (what != "run") return "unitravelflag <run|result>";
            bool on = ShortcutOn("unitravelflag");
            bool cleared = UtClear(on, true, true, false);
            bool ctlVan = !UtClear(on, true, true, true), ctlUnset = !UtClear(on, false, true, false);
            bool controls = ctlVan && ctlUnset;
            return "unitravelflag check (switch " + (on ? "on" : "off") + "): stuck travel flag cleared=" + cleared
                + "; control: queued van keeps it=" + ctlVan + ", flag set elsewhere kept=" + ctlUnset + "; controls ok=" + controls
                + "; verdict: " + GlVerdict(on, cleared, controls, missing.Length == 0) + "; " + counters;
        }

        // ReturnHomeAlarmsCheck run: the world is quit with a return-home alarm handle still set.
        //   Controls: an unset handle is not touched.
        public static object ReturnHomeAlarmsCheck(string what)
        {
            string missing = UtPick("ReturnHomeBehaviorInHomeWorld.mRideInCarAlarm", "ReturnHomeBehaviorInHomeWorld.mDegreeUpdatesAlarm");
            string counters = PhCounters("returnhomealarms", 2652, "handles reset", missing);
            if (what == "result") return counters;
            if (what != "run") return "returnhomealarms <run|result>";
            bool on = ShortcutOn("returnhomealarms");
            bool reset = RhReset(on, true);
            bool ctlUnset = !RhReset(on, false);
            return "returnhomealarms check (switch " + (on ? "on" : "off") + "): stale handle reset at quit=" + reset
                + "; control: unset handle untouched=" + ctlUnset + "; controls ok=" + ctlUnset
                + "; verdict: " + GlVerdict(on, reset, ctlUnset, missing.Length == 0) + "; " + counters;
        }
    }
}
