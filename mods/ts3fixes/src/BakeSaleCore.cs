namespace Ts3Fixes
{
    // Game-independent decisions of bakesaleseller (BakeSale.cs, issue #102). Self-check: tools/selftest/bakesale.
    public static class BakeSaleCore
    {
        // A stored seller the table reads as no seller: deleted (HasBeenDestroyed) or without an interaction queue (what a
        // disposed Sim has). EA's HasSeller only tests for null.
        public static bool SellerGone(bool hasSeller, bool destroyed, bool hasQueue)
        {
            return hasSeller && (destroyed || !hasQueue);
        }

        // An NPC table gets its clean-up alarm back only when it has none: SetUpForNPC always sets one, and the handler that
        // clears it destroys the table, so a live NPC table without an alarm was orphaned by a handler that threw.
        public static bool NeedsRearm(bool isNpcTable, bool hasAlarm, bool destroyed)
        {
            return isNpcTable && !hasAlarm && !destroyed;
        }
    }
}
