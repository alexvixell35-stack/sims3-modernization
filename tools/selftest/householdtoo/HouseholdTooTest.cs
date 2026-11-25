using System;
using Ts3Fixes;

// docs/271 [Experimental] HouseholdToo: flag off = the household rule exactly as before; flag on = live household-scope Sims are
// let through on the realistic-only sites; dead / gone / unknown-object scopes and the unseen direct path never change.
static class HouseholdTooTest
{
    static int sFail;
    static void Check(bool ok, string what) { if (!ok) { sFail++; Console.WriteLine("FAIL " + what); } }

    static int Main()
    {
        // player, live -> exempt with the flag off / on
        Check(HouseholdTooCore.Exempt(true, true, false), "off: household exempt");
        Check(HouseholdTooCore.Exempt(true, false, false), "off: gone household-scope exempt");
        Check(!HouseholdTooCore.Exempt(false, true, false), "off: NPC not exempt");
        Check(!HouseholdTooCore.Exempt(true, true, true), "on: live household let through");
        Check(HouseholdTooCore.Exempt(true, false, true), "on: null / destroyed / no description stays exempt");
        Check(!HouseholdTooCore.Exempt(false, true, true), "on: NPC unchanged");

        int hh = UnseenFallbackCore.kHousehold;
        // AutLive seen path (autseengame + autliveseen on): household runs the game's search unless lifted
        int off = AutLiveCore.Unseen.SeenPath(true, true, false, HouseholdTooCore.SeenExactOk(hh, false));
        int on = AutLiveCore.Unseen.SeenPath(true, true, false, HouseholdTooCore.SeenExactOk(hh, true));
        Check(off == AutLiveCore.Unseen.kSeenGame, "off: household -> game search");
        Check(on == AutLiveCore.Unseen.kSeenExact, "on: household -> exact path");
        // flag off: SeenExactOk equals SeenNpc for every reason (no behaviour change)
        for (int w = -1; w <= UnseenFallbackCore.kBirth; w++)
        {
            Check(HouseholdTooCore.SeenExactOk(w, false) == UnseenFallbackCore.SeenNpc(w), "off: SeenExactOk == SeenNpc for why " + w);
            if (w != hh) Check(HouseholdTooCore.SeenExactOk(w, true) == UnseenFallbackCore.SeenNpc(w), "on: only kHousehold changes, why " + w);
        }
        // the lift never makes a household Sim unseen: the unseen direct path reads why, which stays kHousehold
        Check(!AutLiveCore.Unseen.AtPost(true, false, hh), "household never takes the unseen direct path");
        Check(!AutLiveCore.Unseen.Stay(hh), "household never stays direct");
        Check(!HouseholdTooCore.SeenExactOk(UnseenFallbackCore.kNotLive, true) && !HouseholdTooCore.SeenExactOk(UnseenFallbackCore.kGone, true),
            "on: not live / gone stay on the game search");

        Console.WriteLine(sFail == 0 ? "householdtoo: PASS" : "householdtoo: " + sFail + " FAIL");
        return sFail == 0 ? 0 : 1;
    }
}
