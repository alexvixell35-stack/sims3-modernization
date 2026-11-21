namespace Ts3Fixes
{
    // sculptpose harness check (docs/568 §3). Read-only: nothing in the world changes.
    public static partial class Fixes
    {
        // SculptPoseCheck run: after a failed Sculpt Sim, the subject's pose for this sculptor is cancelled.
        //   Controls: a successful sculpture, a pose for another sculptor -> left alone (EA's code).
        //   off BROKEN = pose left; on FIXED = cancelled.
        //   result  counters 2562-2563 and whether the private members were found
        public static object SculptPoseCheck(string what)
        {
            int[] c = PatchCounters();
            string counters = "sculptpose: poses cancelled 2562=" + c[2562] + ", errors 2563=" + c[2563]
                + "; members found=" + (sSpSculptType != null && sSpSculptee != null && sSpPoseType != null && sSpSculptor != null);
            if (what == "result") return counters;
            if (what != "run") return "sculptpose <run|result>";
            bool on = ShortcutOn("sculptpose");
            bool cancel = SpCancelPose(on, false, true);
            bool control = SpCancelPose(on, true, true) || SpCancelPose(on, false, false);
            string verdict = control ? "UNEXPECTED" : !on ? (cancel ? "UNEXPECTED" : "BROKEN") : (cancel ? "FIXED" : "UNEXPECTED");
            return "sculptpose check (switch " + (on ? "on" : "off") + "): pose cancelled after a failed sculptor=" + cancel + ", controls=" + control + "; verdict: " + verdict + "; " + counters;
        }
    }
}
