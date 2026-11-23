using System;

namespace Ts3Fixes
{
    // Game-independent decisions of StandWaitLocked.cs (docs/686 §7), checked offline by tools/selftest/standwaitlocked.
    //
    // A "locked cycle" is a blocked cycle of the game's obstacle stand-and-wait (StandWaitCore) whose replan failed with
    // "destination portal locked". When the lock is a privacy lock on the destination's room that holds for this Sim, the
    // wait cannot end before the privacy situation does, and the game itself fails such a route at once when it is planned
    // from a standstill. After giveUpCycles locked cycles in a row the wait ends the way the game's timeout ends it.
    public static class StandWaitLockedCore
    {
        public const int kLockNone = 0, kLockPrivacy = 1, kLockPrivacyOpen = 2, kLockDoor = 3, kLockOther = 4;
        public static readonly string[] kLockNames = { "none", "privacy", "privacy situation that does not lock this Sim", "door", "other" };

        public const int kKeep = 0, kGiveUp = 1, kWould = 2, kExempt = 3, kYieldFirst = 4;

        // The game's own rule in PrivacySituation.OnPortalPathPlan for the Sim's running (else transition) interaction:
        // an autonomous one is locked out up to the Privacy priority, an ordered one only below UserDirected. So a
        // player's own order is never locked by privacy, and a Sim with no interaction at all is not either.
        public static bool PrivacyLocks(bool hasInteraction, bool autonomous, int level, int privacyLevel, int userDirectedLevel)
        {
            if (!hasInteraction) return false;
            return (!autonomous || level <= privacyLevel) && (autonomous || level < userDirectedLevel);
        }

        // What locks the destination. lockingSituations = privacy situations of the destination's (or the target's) room
        // that hold for this Sim (the rule above, and the Sim may not ignore privacy: not a toddler, not a partner);
        // situations = all privacy situations of that room; lockedDoors = doors the Sim's own door rule refused in the plan.
        public static int LockKind(bool simInRoom, int situations, int lockingSituations, int lockedDoors)
        {
            if (!simInRoom && lockingSituations > 0) return kLockPrivacy;
            if (lockedDoors > 0) return kLockDoor;
            if (situations > 0) return kLockPrivacyOpen;
            return kLockOther;
        }

        // lockedRun = locked cycles in a row with a privacy lock, this one included.
        public static int Decide(int lockKind, int lockedRun, int giveUpCycles, bool fixOn, bool inScope, bool yieldDue)
        {
            if (lockKind != kLockPrivacy || giveUpCycles <= 0 || lockedRun < giveUpCycles) return kKeep;
            if (!fixOn) return kWould;
            if (!inScope) return kExempt;
            if (yieldDue) return kYieldFirst;  // standwaitfix answers a push or cancel with the game's own exit this cycle
            return kGiveUp;
        }
    }
}
