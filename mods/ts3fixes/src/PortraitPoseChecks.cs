namespace Ts3Fixes
{
    // portraitpose harness check (docs/566 §4). Read-only: nothing in the world changes.
    public static partial class Fixes
    {
        // PortraitPoseCheck run: after a failed Paint Portrait, the subject's pose for this painter is cancelled.
        //   Controls: a successful portrait, a pose for another painter -> left alone (EA's code).
        //   off BROKEN = pose left; on FIXED = cancelled.
        //   result  counters 2546-2547
        public static object PortraitPoseCheck(string what)
        {
            int[] c = PatchCounters();
            string counters = "portraitpose: poses cancelled 2546=" + c[2546] + ", errors 2547=" + c[2547];
            if (what == "result") return counters;
            if (what != "run") return "portraitpose <run|result>";
            bool on = ShortcutOn("portraitpose");
            bool cancel = PpCancelPose(on, false, true);
            bool control = PpCancelPose(on, true, true) || PpCancelPose(on, false, false);
            string verdict = control ? "UNEXPECTED" : !on ? (cancel ? "UNEXPECTED" : "BROKEN") : (cancel ? "FIXED" : "UNEXPECTED");
            return "portraitpose check (switch " + (on ? "on" : "off") + "): pose cancelled after a failed painter=" + cancel + ", controls=" + control + "; verdict: " + verdict + "; " + counters;
        }
    }
}
