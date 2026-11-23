namespace Ts3Fixes
{
    // Game-independent decisions of firenre (FireNre.cs, docs/867), kept free of game types so the offline self-test
    // (tools/selftest/firenre) compiles it alone.
    public static class FnCore
    {
        // FirefighterEmergencySituation.FirefighterAddedToLot calls Occupation?.CurrentJob.RestartTimer: EA throws when the Sim
        // has an occupation whose CurrentJob is null (ActiveFireFighter.CurrentJob is null for a visibly pregnant owner)
        // [verified, IL].
        public static bool AddThrows(bool hasOccupation, bool hasCurrentJob)
        {
            return hasOccupation && !hasCurrentJob;
        }

        // The hook skips EA's RestartTimer only where EA throws, and only with the switch on.
        public static bool AddSkips(bool on, bool hasOccupation, bool hasCurrentJob)
        {
            return on && AddThrows(hasOccupation, hasCurrentJob);
        }

        // FireFightingJob.OnCompletion hands the thank-you call to the owner when the owner is in the situation's Firefighters,
        // else to a random one of them, as (sim.Occupation as ActiveFireFighter).StartGratefulSimAlarm with no null test
        // [verified, decompiled]. True when a Sim EA can pick has no ActiveFireFighter occupation: EA throws when its roll passes.
        public static bool GratefulMayThrow(int listed, bool ownerListed, bool ownerFirefighter, int listedWithoutCareer)
        {
            if (listed <= 0) return false;  // EA skips the whole block
            return ownerListed ? !ownerFirefighter : listedWithoutCareer > 0;
        }

        // The thank-you roll EA makes once its completion notice names the pick: never (0) for a pick with no fire career when
        // the switch is on (EA's RandomChance(0) draws as before and is always false), else EA's own chance.
        public static int ThanksChance(bool on, bool pickIsFirefighter, int eaChance)
        {
            return on && !pickIsFirefighter ? 0 : eaChance;
        }
    }
}
