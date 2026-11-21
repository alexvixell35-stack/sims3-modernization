// Offline self-check of the strand lines' rules (mods/ts3fixes/src/StrandLogCore.cs, docs/183 §8): which failed plans get
// lines, how often, EA's privacy lock rule as the line states it, the stair filter.
using System;
using Ts3Fixes;

static class StrandLogTest
{
    static int fails;
    static void Check(bool ok, string what) { if (!ok) { fails++; Console.WriteLine("FAIL " + what); } }

    static int Main()
    {
        Check(StrandCore.Wants(true, true, true, true, true, 2, 0), "household, no path, same lot, two levels down");
        Check(StrandCore.Wants(true, true, true, true, true, 0, 1), "one level up counts too");
        Check(!StrandCore.Wants(true, true, true, true, true, 1, 1), "same level: no line");
        Check(!StrandCore.Wants(false, true, true, true, true, 2, 0), "routefaillog off: no line");
        Check(!StrandCore.Wants(true, false, true, true, true, 2, 0), "an NPC: no line");
        Check(!StrandCore.Wants(true, true, false, true, true, 2, 0), "another result: no line");
        Check(!StrandCore.Wants(true, true, true, false, true, 2, 0), "on the street: no line");
        Check(!StrandCore.Wants(true, true, true, true, false, 2, 0), "a goal on another lot: no line");

        int n = 0;
        for (int i = 1; i <= 100; i++) if (StrandCore.Due(i)) n++;
        Check(StrandCore.Due(1) && StrandCore.Due(3) && !StrandCore.Due(4) && !StrandCore.Due(24) && StrandCore.Due(25) && StrandCore.Due(50) && n == 7,
            "the first three fails of a Sim, then every 25th");
        Check(!StrandCore.Due(0) && !StrandCore.Due(-25), "no fail, no line");

        // InteractionPriorityLevel: Zero 0, Autonomous 1, NonCriticalNPCBehavior 2, UserDirected 3, High 4, Privacy 5, CriticalNPCBehavior 6
        Check(StrandCore.PrivacyLocks(true, true, 1, false), "autonomous at Autonomous: locked");
        Check(StrandCore.PrivacyLocks(true, true, 5, false) && !StrandCore.PrivacyLocks(true, true, 6, false), "autonomous: locked up to Privacy");
        Check(StrandCore.PrivacyLocks(true, false, 2, false) && !StrandCore.PrivacyLocks(true, false, 3, false), "pushed: locked only below UserDirected");
        Check(!StrandCore.PrivacyLocks(true, false, 5, false), "a shooed Sim (pushed at Privacy) is let through");
        Check(!StrandCore.PrivacyLocks(false, true, 1, false), "no interaction: open");
        Check(!StrandCore.PrivacyLocks(true, true, 1, true), "a Sim the situation lets ignore it: open");

        Check(StrandCore.Blocked(0) == "free" && StrandCore.Blocked(1) == "sim" && StrandCore.Blocked(3) == "object" && StrandCore.Blocked(4) == "wall" &&
              StrandCore.Blocked(6) == "other reason" && StrandCore.Blocked(9) == "?9", "slot test names");

        Check(StrandCore.StairOnWay(1, 2, 2, 0) && StrandCore.StairOnWay(0, 1, 2, 0), "both flights on the way down from level 2");
        Check(StrandCore.StairOnWay(2, 1, 0, 2), "ends given top first, way up");
        Check(!StrandCore.StairOnWay(2, 3, 2, 0) && !StrandCore.StairOnWay(-1, 0, 2, 0), "a flight above or below the way");
        Check(!StrandCore.StairOnWay(1, 1, 2, 0), "both ends on one level: not a way");
        // the room graph of s372's lot as its line gave it for room 9, plus a one-way edge (12 > 9 only) and an island (20, 21)
        StrandCore.Neighbours g = delegate(int r)
        {
            switch (r)
            {
                case 9: return new int[] { 2, 3, 4, 7, 10, 11, 15, 0, 36863 };
                case 11: return new int[] { 9, 0 };
                case 2: return new int[] { 9, 14, 1 };
                case 14: return new int[] { 1, 2, 13 };
                case 12: return new int[] { 9 };
                case 20: return new int[] { 21 };
                case 21: return new int[] { 20 };
                case 0: return new int[] { 20 };
                default: return new int[0];
            }
        };
        int v;
        Check(StrandCore.RoomPath(11, 14, 48, g, out v) == "11>9>2>14", "third floor to the ground-floor hall: the shortest chain");
        Check(StrandCore.RoomPath(14, 11, 48, g, out v) == "14>2>9>11", "and back");
        Check(StrandCore.RoomPath(9, 9, 48, g, out v) == "9" && v == 0, "the same room");
        Check(StrandCore.RoomPath(12, 11, 48, g, out v) == "12>9>11" && StrandCore.RoomPath(11, 12, 48, g, out v) == null && v > 0, "a one-way edge shows as a path one way only");
        Check(StrandCore.RoomPath(11, 20, 48, g, out v) == null, "outside (room 0) is not walked through");
        Check(StrandCore.RoomPath(11, 0, 48, g, out v) == "11>0", "outside as the goal is an end");
        Check(StrandCore.RoomPath(11, 14, 2, g, out v) == null && v == 2, "the walk stops at the room cap");
        Check(StrandCore.RoomPath(11, 14, 48, delegate(int r) { return null; }, out v) == null, "a room with no answer");

        Check(StrandCore.LinksLevels(1, 2, "StaircaseSpiral") && StrandCore.LinksLevels(0, 1, null), "lane ends on two levels: a link, whatever the class");
        Check(!StrandCore.LinksLevels(1, 1, "DoorSingleCountry") && !StrandCore.LinksLevels(0, 0, null), "a door: both ends on one level");
        Check(StrandCore.LinksLevels(1, 1, "ElevatorDoors") && StrandCore.LinksLevels(0, 0, "TubeElevator"), "elevators link levels through a second object");
        Console.WriteLine(fails == 0 ? "strandlog self-check ok" : "strandlog self-check: " + fails + " failed");
        return fails == 0 ? 0 : 1;
    }
}
