using Sims3.Gameplay.Situations;

namespace Ts3Fixes
{
    // visitgreetalarm harness check (docs/565 §1). Read-only: nothing in the world changes.
    public static partial class Fixes
    {
        // VisitGreetAlarmCheck run: (a) a re-run VisitorWantsToComeIn.Init with EA's first wait alarm still pending drops
        //   the new one; (b) a wait timeout for a state that is no longer current, or for an ended visit, is ignored.
        //   Controls: the first Init (no earlier alarm), an earlier alarm that already fired, Init that kept the same handle,
        //   the timeout of the live current state.
        //   Live (read-only): the visits in the world, their state and whether a VisitorWantsToComeIn holds a wait alarm.
        //   off BROKEN = new alarm kept (leak), stale timeout runs; on FIXED = dropped / ignored.
        //   result  counters 2524-2526
        public static object VisitGreetAlarmCheck(string what)
        {
            int[] c = PatchCounters();
            string counters = "visitgreetalarm: re-armed wait alarms dropped 2524=" + c[2524] + ", stale timeouts ignored 2525=" + c[2525] + ", errors 2526=" + c[2526];
            if (what == "result") return counters;
            if (what != "run") return "visitgreetalarm <run|result>";
            bool on = ShortcutOn("visitgreetalarm");
            bool drop = VgaDropNew(on, true, true, true);
            bool stale = VgaStale(on, false, false) && VgaStale(on, true, true);
            bool control = VgaDropNew(on, false, false, true) || VgaDropNew(on, true, false, true) || VgaDropNew(on, true, true, false) || VgaStale(on, true, false);
            string live = "";
            int n = 0;
            try
            {
                foreach (VisitSituation v in Sims3.Gameplay.Autonomy.Situation.GetSituations<VisitSituation>())
                {
                    if (n++ >= 6) break;
                    string st = v.Child == null ? "no state" : v.Child.GetType().Name;
                    if (v.Child is VisitSituation.VisitorWantsToComeIn && sVgaWait != null)
                        st += " (wait alarm " + (((Sims3.Gameplay.Utilities.AlarmHandle)sVgaWait.GetValue(v.Child)) != Sims3.Gameplay.Utilities.AlarmHandle.kInvalidHandle ? "set" : "unset") + ")";
                    live += (live.Length > 0 ? "; " : "") + (v.Guest == null ? "?" : v.Guest.FullName) + " -> " + st;
                }
            }
            catch (System.Exception e) { live = "live read threw " + e.GetType().Name; }
            string verdict = control ? "UNEXPECTED" : !on ? (drop || stale ? "UNEXPECTED" : "BROKEN") : (drop && stale ? "FIXED" : "UNEXPECTED");
            return "visitgreetalarm check (switch " + (on ? "on" : "off") + "): new alarm dropped=" + drop + ", stale timeout ignored=" + stale + ", controls=" + control
                + "; live: " + (live.Length == 0 ? "no visits" : live) + "; verdict: " + verdict + "; " + counters;
        }
    }
}
