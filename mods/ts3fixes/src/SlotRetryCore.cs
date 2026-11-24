namespace Ts3Fixes
{
    // Game-independent rules of slotretry (SlotRetry.cs, docs/686 §24). Offline self-check: tools/selftest/slotretry.
    public static class SlotRetryCore
    {
        public const int kGo = 0, kWould = 1, kKeep = 2;
        public const int kWhyNone = 0, kWhyExit = 1, kWhyObject = 2, kWhyInUse = 3, kWhyNoFree = 4, kWhyNotHuman = 5;
        public const int kTries = 2;          // retries after the game's own try
        public const uint kWaitTicks = 15;    // before a retry from the same spot (the plan failed where the Sim stands)
        public const float kMoved = 0.5f;     // the Sim walked at least this far: the plan worked, the walk ended (m)

        // After the game's try failed. `human`: a human Sim, not riding. `otherExit`: an exit reason besides RouteFailed
        // (cancelled, interrupted, object in use). `objOk`: the object still stands in the world. `inUseByOther`: another
        // Sim uses the object (H1) - left alone, as clipwait does. `free`: free slots or parts left to plan to.
        public static int Decide(bool on, bool human, bool otherExit, bool objOk, bool inUseByOther, int free, out int why)
        {
            why = kWhyNone;
            if (!human) { why = kWhyNotHuman; return kKeep; }
            if (otherExit) { why = kWhyExit; return kKeep; }
            if (!objOk) { why = kWhyObject; return kKeep; }
            if (inUseByOther) { why = kWhyInUse; return kKeep; }
            if (free <= 0) { why = kWhyNoFree; return kKeep; }
            return on ? kGo : kWould;
        }

        // The failed try walked (moved) towards slot `failed` (-1: none chosen, the plan failed): that slot is dropped
        // when another one is left; a plan that failed where the Sim stands is tried again after a short wait.
        public static bool Drop(bool moved, int failed, int n) { return moved && failed >= 0 && n > 1; }

        public static uint Wait(bool moved) { return moved ? 0u : kWaitTicks; }

        // The last retry keeps the game's route-fail behaviour (balloon), earlier ones are quiet.
        public static bool Quiet(int retry) { return retry < kTries; }
    }
}
