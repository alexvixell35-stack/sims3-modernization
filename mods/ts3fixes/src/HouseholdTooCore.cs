namespace Ts3Fixes
{
    // Game-independent part of docs/271 [Experimental] HouseholdToo (PlayerSims.cs HouseholdExempt, AutLive.cs LiveMeta), checked
    // offline by tools/selftest/householdtoo. Flag off = every rule here is exactly the household rule as before.
    public static class HouseholdTooCore
    {
        // true = keep the household exemption. player = IsPlayerSim (household, selected or unknown scope); live = the Sim object
        // exists and has a description (null, destroyed or description-less Sims stay exempt with the flag on)
        public static bool Exempt(bool player, bool live, bool flagOn) { return player && !(live && flagOn); }

        // AutLive seen path: a proven seen NPC, or a household-scope actor the flag let through (lift); never kNotLive / kGone / -1
        public static bool SeenExactOk(int why, bool lift) { return UnseenFallbackCore.SeenNpc(why) || (lift && why == UnseenFallbackCore.kHousehold); }
    }
}
