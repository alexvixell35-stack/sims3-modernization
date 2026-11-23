namespace Ts3Fixes
{
    // repairtimecache harness check (docs/567 §2). Read-only: nothing in the world changes.
    public static partial class Fixes
    {
        // RepairTimeCacheCheck run: a repair time cached for another Sim who is not repairing now is dropped and
        //   recomputed for the asking Sim; while that Sim repairs, the asker gets its own time and the cache stays.
        //   Controls: same Sim, nothing cached -> EA's code.
        //   off BROKEN = stale time used; on FIXED = dropped / own time.
        //   result  counters 2553-2555
        public static object RepairTimeCacheCheck(string what)
        {
            int[] c = PatchCounters();
            string counters = "repairtimecache: stale dropped 2553=" + c[2553] + ", own time while another repairs 2554=" + c[2554] + ", errors 2555=" + c[2555] + ", noted now " + sRtcWho.Count;
            if (what == "result") return counters;
            if (what != "run") return "repairtimecache <run|result>";
            bool on = ShortcutOn("repairtimecache");
            bool fix = RtcAction(on, true, true, false, false) == 1 && RtcAction(on, true, true, false, true) == 2;
            bool control = RtcAction(on, true, true, true, false) != 0 || RtcAction(on, false, true, false, false) != 0;
            bool off = RtcAction(on, true, true, false, false) == 0;
            string verdict = control ? "UNEXPECTED" : !on ? (off ? "BROKEN" : "UNEXPECTED") : (fix ? "FIXED" : "UNEXPECTED");
            return "repairtimecache check (switch " + (on ? "on" : "off") + "): stale time dropped=" + fix + ", controls=" + control + "; verdict: " + verdict + "; " + counters;
        }
    }
}
