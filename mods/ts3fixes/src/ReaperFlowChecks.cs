namespace Ts3Fixes
{
    // reaperflow harness check (docs/563 §4). Pure decisions; nothing in the world changes.
    public static partial class Fixes
    {
        // ReaperFlowCheck run: (a) the reaper's soda push gets a failure path, (b) Challenge Death to Chess with the grave
        //   cleared after a match. Controls: no machine (EA's body), EA's test already says no, a grave with its dead Sim.
        //   off BROKEN = no failure path, challenge offered; on FIXED = failure path, challenge hidden.
        //   result  counters 2513-2515
        public static object ReaperFlowCheck(string what)
        {
            int[] c = PatchCounters();
            string counters = "reaperflow: soda pushes with failure path 2513=" + c[2513] + ", chess hidden 2514=" + c[2514] + ", errors 2515=" + c[2515];
            if (what == "result") return counters;
            if (what != "run") return "reaperflow <run|result>";
            bool on = ShortcutOn("reaperflow");
            bool vending = RfVendingFailPath(on, true);
            bool chess = RfChessHide(on, true, false);
            bool control = RfVendingFailPath(on, false) || RfChessHide(on, false, false) || RfChessHide(on, true, true);
            bool any = vending || chess, all = vending && chess;
            string verdict = control ? "UNEXPECTED" : !on ? (any ? "UNEXPECTED" : "BROKEN") : (all ? "FIXED" : "UNEXPECTED");
            return "reaperflow check (switch " + (on ? "on" : "off") + "): soda failure path=" + vending + ", chess hidden with no grave=" + chess
                + ", controls=" + control + "; verdict: " + verdict + "; " + counters;
        }
    }
}
