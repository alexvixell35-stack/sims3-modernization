namespace Ts3Fixes
{
    // convmerge harness check (docs/566 §2). Read-only: nothing in the world changes.
    public static partial class Fixes
    {
        // ConvMergeCheck run: an old conversation merged into a new one has its discourage footprint cleaned.
        //   Controls: the merged conversation itself, an old one with no footprint -> left alone.
        //   off BROKEN = old footprint kept; on FIXED = cleaned.
        //   result  counters 2544-2545
        public static object ConvMergeCheck(string what)
        {
            int[] c = PatchCounters();
            string counters = "convmerge: old regions cleaned 2544=" + c[2544] + ", errors 2545=" + c[2545];
            if (what == "result") return counters;
            if (what != "run") return "convmerge <run|result>";
            bool on = ShortcutOn("convmerge");
            bool clean = CmCleanOld(on, false, true);
            bool control = CmCleanOld(on, true, true) || CmCleanOld(on, false, false);
            string verdict = control ? "UNEXPECTED" : !on ? (clean ? "UNEXPECTED" : "BROKEN") : (clean ? "FIXED" : "UNEXPECTED");
            return "convmerge check (switch " + (on ? "on" : "off") + "): old footprint cleaned=" + clean + ", controls=" + control + "; verdict: " + verdict + "; " + counters;
        }
    }
}
