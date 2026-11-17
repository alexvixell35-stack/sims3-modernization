namespace Ts3Fixes
{
    // picniccamera harness check (docs/566 §5). Read-only: nothing in the world changes.
    public static partial class Fixes
    {
        // PicnicCameraCheck run: a served picnic's map-view callback still on the camera event is removed after Run.
        //   Control: a Serve whose callback EA already removed -> nothing to do.
        //   off BROKEN = callback kept; on FIXED = removed.
        //   result  counters 2548-2549
        public static object PicnicCameraCheck(string what)
        {
            int[] c = PatchCounters();
            string counters = "picniccamera: callbacks removed 2548=" + c[2548] + ", errors 2549=" + c[2549] + ", camera map-view handlers now " + PcCount();
            if (what == "result") return counters;
            if (what != "run") return "picniccamera <run|result>";
            bool on = ShortcutOn("picniccamera");
            bool remove = PcRemove(on, true);
            bool control = PcRemove(on, false);
            string verdict = control ? "UNEXPECTED" : !on ? (remove ? "UNEXPECTED" : "BROKEN") : (remove ? "FIXED" : "UNEXPECTED");
            return "picniccamera check (switch " + (on ? "on" : "off") + "): leaked callback removed=" + remove + ", controls=" + control + "; verdict: " + verdict + "; " + counters;
        }
    }
}
