namespace Ts3Fixes
{
    // burglaridle harness check (docs/568 §1). Read-only: nothing in the world changes.
    public static partial class Fixes
    {
        // BurglarIdleCheck run: a burglar Stealing state that started nothing (fire branch / no household) is moved on.
        //   Controls: state already changed (occult/quota path) -> EA; normal steal branch -> EA; no household with
        //   stolen goods -> EA; watch tick while ReactToFire is still queued -> wait.
        //   off BROKEN = burglar left idle; on FIXED = leave / fire watch.
        //   result  counters 2556-2558
        public static object BurglarIdleCheck(string what)
        {
            int[] c = PatchCounters();
            string counters = "burglaridle: no-household leaves 2556=" + c[2556] + ", fire-watch leaves 2557=" + c[2557] + ", errors 2558=" + c[2558];
            if (what == "result") return counters;
            if (what != "run") return "burglaridle <run|result>";
            bool on = ShortcutOn("burglaridle");
            int noHh = BiAfterInit(on, true, true, 0, false);
            int fire = BiAfterInit(on, true, false, 0, true);
            bool controls = BiAfterInit(on, false, true, 0, true) == 0 && BiAfterInit(on, true, false, 0, false) == 0
                && BiAfterInit(on, true, true, 2, false) == 0 && BiWatch(true, true) == 1 && BiWatch(false, false) == 0;
            bool fixedNow = noHh == 1 && fire == 2 && BiWatch(true, false) == 2;
            bool broken = noHh == 0 && fire == 0;
            string verdict = !controls ? "UNEXPECTED" : !on ? (broken ? "BROKEN" : "UNEXPECTED") : (fixedNow ? "FIXED" : "UNEXPECTED");
            return "burglaridle check (switch " + (on ? "on" : "off") + "): no-household action=" + noHh + ", fire action=" + fire + ", controls ok=" + controls + "; verdict: " + verdict + "; " + counters;
        }
    }
}
