namespace Ts3Fixes
{
    // upgradefailalarm harness check (docs/567 §1). Read-only: nothing in the world changes.
    public static partial class Fixes
    {
        // UpgradeFailAlarmCheck run: a failed upgrade attempt booked before its failure alarm fired has the alarm
        //   removed. Controls: alarm already fired, attempt still running -> left alone (EA's code).
        //   off BROKEN = alarm left to cancel a later interaction; on FIXED = removed.
        //   result  counters 2550-2552, live noted alarms
        public static object UpgradeFailAlarmCheck(string what)
        {
            int[] c = PatchCounters();
            string counters = "upgradefailalarm: removed at booking 2550=" + c[2550] + ", at restart 2551=" + c[2551] + ", errors 2552=" + c[2552] + ", noted now " + sUfaPending.Count;
            if (what == "result") return counters;
            if (what != "run") return "upgradefailalarm <run|result>";
            bool on = ShortcutOn("upgradefailalarm");
            bool remove = UfaRemove(on, true, true);
            bool control = UfaRemove(on, true, false) || UfaRemove(on, false, true);
            string verdict = control ? "UNEXPECTED" : !on ? (remove ? "UNEXPECTED" : "BROKEN") : (remove ? "FIXED" : "UNEXPECTED");
            return "upgradefailalarm check (switch " + (on ? "on" : "off") + "): pending failure alarm removed=" + remove + ", controls=" + control + "; verdict: " + verdict + "; " + counters;
        }
    }
}
