namespace Ts3Fixes
{
    // Game-independent part of outfitwaitpoll (OutfitWait.cs, issue #115): how many ticks a Sim.SwitchOutfitHelper wait
    // sleeps before it reads mReady again. Self-check: tools/selftest/outfitwait.
    public static class OutfitWaitCore
    {
        // EA's 1 tick; one sim-minute once the wait has run staleAfter ticks and the switch is on.
        public static uint SleepTicks(long waited, long staleAfter, uint minute, bool on)
        {
            return on && waited >= staleAfter ? minute : 1u;
        }
    }
}
