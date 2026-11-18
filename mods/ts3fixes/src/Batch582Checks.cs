using System;
using System.Collections.Generic;

namespace Ts3Fixes
{
    // Harness checks for Batch582Logic.cs (docs/582). Pure decisions with controls; nothing in the world is touched.
    // B582Counters / B582Line add the switch's live-path problems (CkLive, CheckLiveMap.cs: swaps in place, handles usable).
    public static partial class Fixes
    {
        // VehicleFxHelperCheck run: a held tire / hover helper is destroyed before EA creates the next one.
        //   Controls: no helper held -> nothing destroyed (first route of a vehicle, EA's normal path).
        public static object VehicleFxHelperCheck(string what)
        {
            int[] c = PatchCounters();
            string miss = EpJoin(sVfMissing, CkLive("vehiclefxhelper"));
            string counters = "vehiclefxhelper: destroyed before re-create 1118=" + c[2808] + ", seen with switch off 1119=" + c[2809] + ", errors 1120=" + c[2810]
                + "; " + GlMembers(miss);
            if (what == "result") return counters;
            if (what != "run") return "vehiclefxhelper <run|result>";
            bool on = ShortcutOn("vehiclefxhelper");
            bool fixedNow = VfDestroyFirst(on, true);
            bool ctlNone = !VfDestroyFirst(on, false) && !VfDestroyFirst(!on, false);
            bool controls = ctlNone;
            return "vehiclefxhelper check (switch " + (on ? "on" : "off") + "): held helper destroyed before the re-create=" + fixedNow
                + "; control: no helper held unchanged=" + ctlNone + "; controls ok=" + controls
                + "; verdict: " + GlVerdict(on, fixedNow, controls, miss.Length == 0) + "; " + counters;
        }

        static string B582Counters(string name, int first, string[] what, string missing)
        {
            int[] c = PatchCounters();
            System.Text.StringBuilder b = new System.Text.StringBuilder(name + ":");
            for (int i = 0; i < what.Length; i++) b.Append((i == 0 ? " " : ", ") + what[i] + " " + (first + i) + "=" + c[first + i]);
            return b.Append("; ").Append(GlMembers(EpJoin(missing, CkLive(name)))).ToString();
        }

        static string B582Line(string name, bool on, string fixedText, bool fixedNow, string ctlText, bool controls, string missing, string counters)
        {
            return name + " check (switch " + (on ? "on" : "off") + "): " + fixedText + "=" + fixedNow + "; control: " + ctlText + "; controls ok=" + controls
                + "; verdict: " + GlVerdict(on, fixedNow, controls, EpJoin(missing, CkLive(name)).Length == 0) + "; " + counters;
        }

        // PrankDitchEmptyCheck run: the empty-prankee throw ends Run as EA's null pick does.
        //   Controls: another ArgumentException rethrown; somebody at home (not the empty-list case) rethrown.
        public static object PrankDitchEmptyCheck(string what)
        {
            string counters = B582Counters("prankditchempty", 2811, new[] { "empty-list ends handled", "thrown with switch off", "errors" }, sPdMissing);
            if (what == "result") return counters;
            if (what != "run") return "prankditchempty <run|result>";
            bool on = ShortcutOn("prankditchempty");
            bool fixedNow = PdAbsorb(on, true, true);
            bool ctlOther = !PdAbsorb(on, false, true), ctlHome = !PdAbsorb(on, true, false);
            bool controls = ctlOther && ctlHome;
            return B582Line("prankditchempty", on, "empty prankee list ends like the null pick", fixedNow,
                "other ArgumentException rethrown=" + ctlOther + ", somebody home rethrown=" + ctlHome, controls, sPdMissing, counters);
        }

        // ServiceAssignExitCheck run: the exiting socializing situation's entry is dropped; at load an exited one is.
        //   Controls: another situation's live entry kept; a live entry at load kept.
        public static object ServiceAssignExitCheck(string what)
        {
            string counters = B582Counters("serviceassignexit", 2814, new[] { "entries removed at exit", "seen with switch off", "saved stale removed at load", "errors" }, sSeMissing);
            if (what == "result") return counters;
            if (what != "run") return "serviceassignexit <run|result>";
            bool on = ShortcutOn("serviceassignexit");
            bool fixedNow = SeDrop(on, true, false) && SeDrop(on, false, true);
            bool ctlOther = !SeDrop(on, false, false);
            bool controls = ctlOther;
            return B582Line("serviceassignexit", on, "stale socializing entry dropped at exit and at load", fixedNow,
                "other live entries kept=" + ctlOther, controls, sSeMissing, counters);
        }

        // ServiceShowAlarmCheck run: a pending show alarm is not re-armed.
        //   Controls: no alarm held -> EA runs; the held alarm already fired -> EA runs.
        public static object ServiceShowAlarmCheck(string what)
        {
            string counters = B582Counters("serviceshowalarm", 2818, new[] { "re-arms skipped", "seen with switch off", "errors" }, sSsMissing);
            if (what == "result") return counters;
            if (what != "run") return "serviceshowalarm <run|result>";
            bool on = ShortcutOn("serviceshowalarm");
            bool fixedNow = SsSkip(on, true, true);
            bool ctlNone = !SsSkip(on, false, false), ctlFired = !SsSkip(on, true, false);
            bool controls = ctlNone && ctlFired;
            return B582Line("serviceshowalarm", on, "pending show alarm not re-armed", fixedNow,
                "no alarm held runs EA=" + ctlNone + ", fired alarm runs EA=" + ctlFired, controls, sSsMissing, counters);
        }

        // MailDogChaserCheck run: the chase's own dog is cleared at the chase end.
        //   Controls: another dog (set by another lot's delivery) kept.
        public static object MailDogChaserCheck(string what)
        {
            string counters = B582Counters("maildogchaser", 2821, new[] { "chasers cleared", "seen with switch off", "errors" }, sMdMissing);
            if (what == "result") return counters;
            if (what != "run") return "maildogchaser <run|result>";
            bool on = ShortcutOn("maildogchaser");
            bool fixedNow = MdClear(on, true);
            bool ctlOther = !MdClear(on, false);
            return B582Line("maildogchaser", on, "chase's dog cleared at the chase end", fixedNow, "another dog kept=" + ctlOther, ctlOther, sMdMissing, counters);
        }

        // LifeguardSurveyCheck run: a completed survey's event alarm is removed.
        //   Controls: another job type untouched; a running survey (job not over) untouched; no alarm held untouched.
        public static object LifeguardSurveyCheck(string what)
        {
            string counters = B582Counters("lifeguardsurvey", 2824, new[] { "alarms removed", "seen with switch off", "errors" }, sLgMissing);
            if (what == "result") return counters;
            if (what != "run") return "lifeguardsurvey <run|result>";
            bool on = ShortcutOn("lifeguardsurvey");
            bool fixedNow = LgRemove(on, true, true, true);
            bool ctlJob = !LgRemove(on, false, true, true), ctlRunning = !LgRemove(on, true, true, false), ctlNone = !LgRemove(on, true, false, true);
            bool controls = ctlJob && ctlRunning && ctlNone;
            return B582Line("lifeguardsurvey", on, "completed survey's event alarm removed", fixedNow,
                "other job type untouched=" + ctlJob + ", running survey untouched=" + ctlRunning + ", no alarm held untouched=" + ctlNone, controls, sLgMissing, counters);
        }

        // MoonRiseListenerCheck run: the held kMoonRise listener is removed before the re-add.
        //   Controls: none held (first instantiation) -> nothing removed.
        public static object MoonRiseListenerCheck(string what)
        {
            string counters = B582Counters("moonriselistener", 2827, new[] { "listeners removed before re-add", "seen with switch off", "errors" }, sMrMissing);
            if (what == "result") return counters;
            if (what != "run") return "moonriselistener <run|result>";
            bool on = ShortcutOn("moonriselistener");
            bool fixedNow = MrRemoveFirst(on, true);
            bool ctlNone = !MrRemoveFirst(on, false);
            return B582Line("moonriselistener", on, "held listener removed before the re-add", fixedNow, "none held unchanged=" + ctlNone, ctlNone, sMrMissing, counters);
        }

        // HolidayLightsCheck run: a 0 pulse with lots waiting becomes 1; the load sweep drops orphan trickle alarms.
        //   Controls: a non-zero pulse kept; no lots waiting keeps 0; the held alarm and other callbacks kept.
        public static object HolidayLightsCheck(string what)
        {
            string counters = B582Counters("holidaylights", 2830, new[] { "pulses raised from 0", "held alarms removed before re-add", "saved orphans removed at load", "errors" }, sHlMissing);
            if (what == "result") return counters;
            if (what != "run") return "holidaylights <run|result>";
            bool on = ShortcutOn("holidaylights");
            bool pulseFixed = HlPulse(on, 0, 4) == 1;
            List<int> pick = HlPick(new[] { "TrickleOnHouseLights", "TrickleOnHouseLights", "SeasonChanged" }, new[] { true, false, false });
            bool sweepOk = pick.Count == 1 && pick[0] == 1;
            bool fixedNow = pulseFixed && sweepOk;
            bool ctlPulse = HlPulse(on, 3, 40) == 3 && HlPulse(on, 0, 0) == 0;
            bool ctlHeld = HlPick(new[] { "TrickleOnHouseLights" }, new[] { true }).Count == 0;
            bool controls = ctlPulse && ctlHeld;
            return B582Line("holidaylights", on, "0 pulse with lots waiting raised to 1=" + pulseFixed + ", load sweep drops the orphan", fixedNow,
                "non-zero / nothing waiting kept=" + ctlPulse + ", held alarm and other callbacks kept=" + ctlHeld, controls, sHlMissing, counters);
        }
    }
}
