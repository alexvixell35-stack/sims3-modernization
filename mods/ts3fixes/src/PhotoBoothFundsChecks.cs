namespace Ts3Fixes
{
    // photoboothfunds harness check (docs/561 §2). Pure decision; nothing in the world changes.
    public static partial class Fixes
    {
        // PhotoBoothFundsCheck run: the step after a failed TakePhoto whose Sim is still listed inside the booth (EA's
        //   no-funds exit) and, as a control, a failed run whose Sim already left. off BROKEN = no exit (Sim stays faded
        //   out); on FIXED = exit queued for the first, none for the control.
        //   result  counters 2479-2480
        public static object PhotoBoothFundsCheck(string what)
        {
            int[] c = PatchCounters();
            string counters = "photoboothfunds: Sims taken out 2479=" + c[2479] + ", errors 2480=" + c[2480];
            if (what == "result") return counters;
            if (what != "run") return "photoboothfunds <run|result>";
            bool on = ShortcutOn("photoboothfunds");
            bool stuck = PbNeedsExit(on, false, true, false);
            bool control = PbNeedsExit(on, false, false, false) || PbNeedsExit(on, false, true, true) || PbNeedsExit(on, true, true, false);
            string verdict = control ? "UNEXPECTED" : !on ? (stuck ? "UNEXPECTED" : "BROKEN") : (stuck ? "FIXED" : "UNEXPECTED");
            return "photoboothfunds check (switch " + (on ? "on" : "off") + "): Sim left inside after the no-funds exit -> exit queued=" + stuck + ", controls=" + control + "; verdict: " + verdict + "; " + counters;
        }
    }
}
