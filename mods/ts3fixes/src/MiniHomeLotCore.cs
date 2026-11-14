namespace Ts3Fixes
{
    // Game-independent rule of minihomelot (MiniHomeLot.cs, docs/266). Self-check: tools/selftest/minihomelot.
    public static class MiniHomeLotCore
    {
        // A Sim's MiniSimDescription home lot is set to its household's lot only at home (not while changing worlds, Sim's
        // home world = the current world), for a real lot, and only when it differs.
        public static bool ShouldSync(bool changingWorlds, bool homeWorldIsCurrent, ulong householdLotId, ulong miniLotId)
        {
            return !changingWorlds && homeWorldIsCurrent && householdLotId != 0 && miniLotId != householdLotId;
        }
    }
}
