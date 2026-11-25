using System;
using System.Collections.Generic;
using Ts3Fixes;

// Offline checks of ReachCacheCore (reachcache, docs/688): the room flood, the verdict and the autonomy memo.
static class ReachCacheTest
{
    static int sFail, sRun;

    static void Check(bool ok, string what)
    {
        sRun++;
        if (!ok) { sFail++; Console.WriteLine("FAIL " + what); }
    }

    // a lot: 0 outdoors, 1 hall, 2 kitchen (open arch from 1), 3 bathroom (locked from 1, open from 3 to 1: a one-way
    // privacy-style lock), 4 closet behind 3, 5 upstairs over stairs from 1, 6 locked room upstairs, 7 a room reached only
    // through an elevator list from 5
    sealed class Lot : IRcRooms
    {
        public bool BathLocked = true;
        public void Edges(int room, List<RcEdge> into)
        {
            switch (room)
            {
                case 0: into.Add(new RcEdge(1, false)); break;
                case 1: into.Add(new RcEdge(0, false)); into.Add(new RcEdge(2, false)); into.Add(new RcEdge(3, BathLocked)); into.Add(new RcEdge(5, false)); break;
                case 2: into.Add(new RcEdge(1, false)); break;
                case 3: into.Add(new RcEdge(1, false)); into.Add(new RcEdge(4, false)); break;
                case 4: into.Add(new RcEdge(3, false)); break;
                case 5: into.Add(new RcEdge(1, false)); into.Add(new RcEdge(6, true)); into.Add(new RcEdge(7, false)); break;
                case 7: into.Add(new RcEdge(5, false)); break;
            }
        }
    }

    sealed class Chain : IRcRooms
    {
        public void Edges(int room, List<RcEdge> into) { into.Add(new RcEdge(room + 1, false)); }
    }

    static int V(Dictionary<int, bool> r, params int[] rooms) { return ReachCacheCore.Verdict(r, rooms, rooms.Length, true); }

    static int Main()
    {
        Lot lot = new Lot();
        Dictionary<int, bool> hall = ReachCacheCore.Flood(1, lot);
        Check(hall != null && hall.ContainsKey(2) && hall.ContainsKey(5) && hall.ContainsKey(7) && !hall.ContainsKey(3) && !hall.ContainsKey(4) && !hall.ContainsKey(6), "flood from the hall");
        Check(V(hall, 3) == ReachCacheCore.kRefuse, "locked bathroom refused");
        Check(V(hall, 4) == ReachCacheCore.kRefuse, "closet behind the bathroom refused");
        Check(V(hall, 6) == ReachCacheCore.kRefuse, "locked room upstairs refused");
        Check(V(hall, 3, 4) == ReachCacheCore.kRefuse, "all destinations behind locks refused");
        Check(V(hall, 2) == ReachCacheCore.kReach, "open kitchen reached");
        Check(V(hall, 7) == ReachCacheCore.kReach, "elevator room reached");
        Check(V(hall, 3, 2) == ReachCacheCore.kReach, "one reachable destination runs the plan");
        Check(V(hall, 0) == ReachCacheCore.kReach, "outdoors always reachable");
        Check(V(hall, ReachCacheCore.kNoRoom) == ReachCacheCore.kUnknown, "unplaced destination unknown");
        Check(V(hall, -1) == ReachCacheCore.kUnknown, "bad room unknown");
        Check(ReachCacheCore.Verdict(hall, new int[] { 3 }, 1, false) == ReachCacheCore.kUnknown, "a destination without a room: unknown");
        Check(ReachCacheCore.Verdict(hall, new int[0], 0, true) == ReachCacheCore.kUnknown, "no destinations: unknown");
        Check(ReachCacheCore.Verdict(null, new int[] { 3 }, 1, true) == ReachCacheCore.kUnknown, "flood gave up: unknown");

        // from inside the bathroom the way out is open (the lock is one-way): everything but room 6 is reached
        Dictionary<int, bool> bath = ReachCacheCore.Flood(3, lot);
        Check(V(bath, 2) == ReachCacheCore.kReach && V(bath, 4) == ReachCacheCore.kReach && V(bath, 6) == ReachCacheCore.kRefuse, "flood from the bathroom");
        // unlock: the bathroom is reached again
        lot.BathLocked = false;
        Check(V(ReachCacheCore.Flood(1, lot), 3) == ReachCacheCore.kReach, "unlocked bathroom reached");
        Check(ReachCacheCore.Flood(ReachCacheCore.kNoRoom, lot) == null && ReachCacheCore.Flood(-1, lot) == null, "bad start gives up");
        Check(ReachCacheCore.Flood(1, new Chain()) == null, "a flood past kMaxRooms gives up");

        RcMemo m = new RcMemo();
        m.Record(1, 2, 3, 10, 5, 100);
        Check(m.Blocked(1, 2, 3, 10, 5, 50) == 1, "memo blocks while lot version is unchanged");
        Check(m.Blocked(1, 2, 4, 10, 5, 50) == 0 && m.Blocked(9, 2, 3, 10, 5, 50) == 0, "other kind or Sim not blocked");
        Check(m.Blocked(1, 2, 3, 10, 6, 50) == -1 && m.Count == 0, "a lock / privacy / build change drops the entry");
        m.Record(1, 2, 3, 10, 5, 100);
        Check(m.Blocked(1, 2, 3, 10, 5, 100) == -1, "expired entry dropped");
        m.Record(1, 2, 3, 10, 5, 100);
        Check(m.Blocked(1, 2, 3, 11, 5, 50) == -1, "another lot drops the entry");
        for (ulong i = 0; i < RcMemo.kMax + 5; i++) m.Record(i, 1, 1, 1, 1, 10);
        Check(m.Count <= RcMemo.kMax, "memo capped");

        Console.WriteLine("reachcache selftest: " + (sRun - sFail) + "/" + sRun + " ok");
        return sFail == 0 ? 0 : 1;
    }
}
