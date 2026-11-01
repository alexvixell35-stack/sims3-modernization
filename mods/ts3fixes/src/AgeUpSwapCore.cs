namespace Ts3Fixes
{
    // Game-independent rule of ageupswap (AgeUpSwap.cs, docs/503). Self-check: tools/selftest/ageupswap.
    public static class AgeUpSwapCore
    {
        public const int kEa = 0, kEveryday = 1, kCallback = 2;

        // What the hook does after EA's Sim.SwitchToOutfitDelayed(category, callback) returned origOk.
        // kEa: return EA's result (it started a swap, the switch is off, or the caller is not an age-up SimBuilderTask).
        // kEveryday: ask EA again for Everyday, which every age may wear. kCallback: no swap can start, so run the
        // caller's callback now; SimBuilderTask.FinishModelLoad waits on it and would otherwise wait forever.
        public static int Next(bool origOk, bool on, bool builderCallback, bool wasEveryday)
        {
            if (origOk || !on || !builderCallback) return kEa;
            return wasEveryday ? kCallback : kEveryday;
        }
    }
}
