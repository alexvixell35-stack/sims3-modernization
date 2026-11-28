using System;
using Ts3Fixes;

// Offline self-check of UnseenTrafficCore (unseentraffic + npccarexit, mods/ts3fixes/src/UnseenTraffic.cs).
static class UnseenTrafficTest
{
    static int sFails;
    static void Check(bool ok, string what) { if (!ok) { sFails++; Console.WriteLine("FAIL " + what); } }

    static void Main()
    {
        // corridor sampling
        Check(UnseenTrafficCore.CorridorSteps(0, 0, 10, 0) == 1, "a short leg is one step (ends only)");
        Check(UnseenTrafficCore.CorridorSteps(0, 0, 0, 120) == 2, "120 m: samples at 0, 60, 120");
        Check(UnseenTrafficCore.CorridorSteps(0, 0, 9000, 0) == UnseenTrafficCore.kCorridorMaxSteps, "steps capped");
        Check(UnseenTrafficCore.Lerp(100, 200, 0, 4) == 100 && UnseenTrafficCore.Lerp(100, 200, 4, 4) == 200, "lerp ends");
        Check(UnseenTrafficCore.Lerp(100, 200, 1, 4) == 125, "lerp middle");

        // teleport leg hold time: straight x 1.4 / CarSpeed, 1-90 sim min
        Check(Math.Abs(UnseenTrafficCore.LegMinutes(450f, 18f) - 35f) < 0.01f, "450 m at 18 m/min: 35 min");
        Check(UnseenTrafficCore.LegMinutes(5f, 18f) == UnseenTrafficCore.kMinLegMin, "short leg: at least 1 min");
        Check(UnseenTrafficCore.LegMinutes(5000f, 18f) == UnseenTrafficCore.kMaxLegMin, "long leg capped");
        Check(Math.Abs(UnseenTrafficCore.LegMinutes(450f, 0f) - 35f) < 0.01f, "no speed: EA's default 18");
        Check(Math.Abs(UnseenTrafficCore.LegMinutes(450f, float.NaN) - 35f) < 0.01f, "NaN speed: EA's default 18");
        Check(UnseenTrafficCore.LegMinutes(float.NaN, 18f) == UnseenTrafficCore.kMinLegMin, "NaN distance: 1 min");

        // only DriveAwayInNpcCar's call shape is taken
        Check(UnseenTrafficCore.IsDriveAwayAsk(100f, 5, 1, 7, 5, 1, 7), "car.Position + 100 m is the drive-away ask");
        Check(!UnseenTrafficCore.IsDriveAwayAsk(150f, 5, 1, 7, 5, 1, 7), "another distance is not");
        Check(!UnseenTrafficCore.IsDriveAwayAsk(100f, 50, 1, 7, 5, 1, 7), "a lot point (InitCarForLotRoute) is not");

        // memo keys
        ulong k = UnseenTrafficCore.ExitKey(1100.2f, 900.1f, 1272.0f, 1108.0f);
        Check(k == UnseenTrafficCore.ExitKey(1103.9f, 903.0f, 1272.4f, 1108.6f), "same 8 m start cell and 1 m goal cell: same key");
        Check(k != UnseenTrafficCore.ExitKey(1108.1f, 900.1f, 1272.0f, 1108.0f), "next start cell: other key");
        Check(k != UnseenTrafficCore.ExitKey(1100.2f, 900.1f, 1273.1f, 1108.0f), "next goal cell: other key");
        Check(UnseenTrafficCore.ExitKey(-3f, -3f, -1f, -1f) != UnseenTrafficCore.ExitKey(3f, 3f, 1f, 1f), "negative coordinates do not fold onto positive ones");

        // learning match
        Check(UnseenTrafficCore.SameGoal(1272.0f, 1108.0f, 1272.3f, 1108.4f), "goal within 1 m matches");
        Check(!UnseenTrafficCore.SameGoal(1272.0f, 1108.0f, 1274.0f, 1108.0f), "goal 2 m away does not");

        Console.WriteLine(sFails == 0 ? "unseentraffic selftest: ok" : "unseentraffic selftest: " + sFails + " FAIL");
        Environment.Exit(sFails == 0 ? 0 : 1);
    }
}
