namespace Ts3Fixes
{
    // bribeguard harness check (docs/561 §7). Pure decision; nothing in the world changes.
    public static partial class Fixes
    {
        // BouncerBribeGuardCheck run: a bribe charged after NightlyCleanUp cleared the dictionary. Control: a dictionary
        //   still there. off BROKEN = EA's TryGetValue on null (throws); on FIXED = no charge, false.
        //   result  counters 2493-2494
        public static object BouncerBribeGuardCheck(string what)
        {
            int[] c = PatchCounters();
            string counters = "bribeguard: charges guarded 2493=" + c[2493] + ", errors 2494=" + c[2494];
            if (what == "result") return counters;
            if (what != "run") return "bribeguard <run|result>";
            bool on = ShortcutOn("bribeguard");
            bool g = BrGuard(on, true);
            bool control = BrGuard(on, false);
            string verdict = control ? "UNEXPECTED" : !on ? (g ? "UNEXPECTED" : "BROKEN") : (g ? "FIXED" : "UNEXPECTED");
            return "bribeguard check (switch " + (on ? "on" : "off") + "): charge after the nightly cleanup -> guarded=" + g
                + ", control=" + control + "; verdict: " + verdict + "; " + counters;
        }
    }
}
