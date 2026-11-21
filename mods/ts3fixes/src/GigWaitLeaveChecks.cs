namespace Ts3Fixes
{
    // gigwaitleave harness check (docs/568 §2). Read-only: nothing in the world changes.
    public static partial class Fixes
    {
        // GigWaitLeaveCheck run: a gig wait pulse whose customer waited too long stops after EA's ForceLeaveJob.
        //   Control: a pulse within the wait limit -> EA's body (push the wait).
        //   off BROKEN = pulse continues and pushes a wait for the cancelled job; on FIXED = stops.
        //   result  counters 2559-2561 and whether both private states were found
        public static object GigWaitLeaveCheck(string what)
        {
            int[] c = PatchCounters();
            string counters = "gigwaitleave: stylist stops 2559=" + c[2559] + ", singer stops 2560=" + c[2560] + ", errors 2561=" + c[2561]
                + "; states found stylist=" + (sGwStylist != null) + " singer=" + (sGwSinger != null);
            if (what == "result") return counters;
            if (what != "run") return "gigwaitleave <run|result>";
            bool on = ShortcutOn("gigwaitleave");
            bool stop = GwStop(on, true);
            bool control = GwStop(on, false);
            string verdict = control ? "UNEXPECTED" : !on ? (stop ? "UNEXPECTED" : "BROKEN") : (stop ? "FIXED" : "UNEXPECTED");
            return "gigwaitleave check (switch " + (on ? "on" : "off") + "): timed-out pulse stops=" + stop + ", controls=" + control + "; verdict: " + verdict + "; " + counters;
        }
    }
}
