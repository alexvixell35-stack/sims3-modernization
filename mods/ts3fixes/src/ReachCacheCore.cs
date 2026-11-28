using System.Collections.Generic;

namespace Ts3Fixes
{
    // Game-independent rules of reachcache (ReachCache.cs, docs/688): the room flood and the verdict, and the autonomy memo.
    // Offline self-check: tools/selftest/reachcache.
    public struct RcEdge
    {
        public int To;
        public bool Locked;  // the planner's own portal oracle answered PortalLocked for this direction
        public RcEdge(int to, bool locked) { To = to; Locked = locked; }
    }

    public interface IRcRooms
    {
        // every portal of `room` as (adjoining room, locked from `room` to it); fills `into`, which the caller cleared
        void Edges(int room, List<RcEdge> into);
    }

    public static class ReachCacheCore
    {
        public const int kNoRoom = 36863;  // the game's "no room" id
        public const int kMaxRooms = 512;  // a flood that grows past this gives up (unknown)

        public const int kUnknown = 0;     // a destination or the start could not be placed in a room: the plan runs
        public const int kReach = 1;       // a destination room is reached (or outdoors): the plan runs
        public const int kRefuse = 2;      // every destination room lies behind portals locked for this Sim

        // Rooms the Sim can walk to from `start` through unlocked portals. Room 0 (the lot's outdoors) is reached
        // whenever any of its portals is; it is never needed for the verdict (outdoors is always taken as reachable).
        // null = the flood gave up (more than kMaxRooms rooms, or a bad start).
        public static Dictionary<int, bool> Flood(int start, IRcRooms g)
        {
            if (start < 0 || start == kNoRoom || g == null) return null;
            Dictionary<int, bool> seen = new Dictionary<int, bool>();
            Queue<int> q = new Queue<int>();
            List<RcEdge> edges = new List<RcEdge>();
            seen[start] = true;
            q.Enqueue(start);
            while (q.Count > 0)
            {
                int room = q.Dequeue();
                edges.Clear();
                g.Edges(room, edges);
                for (int i = 0; i < edges.Count; i++)
                {
                    RcEdge e = edges[i];
                    if (e.Locked || e.To < 0 || e.To == kNoRoom || seen.ContainsKey(e.To)) continue;
                    if (seen.Count >= kMaxRooms) return null;
                    seen[e.To] = true;
                    q.Enqueue(e.To);
                }
            }
            return seen;
        }

        // rooms: the destination rooms (n of them); allKnown false = some destination had no room (radial, lot, unknown kind)
        public static int Verdict(Dictionary<int, bool> reached, int[] rooms, int n, bool allKnown)
        {
            if (!allKnown || rooms == null || n <= 0 || reached == null) return kUnknown;
            for (int i = 0; i < n; i++)
            {
                int r = rooms[i];
                if (r < 0 || r == kNoRoom) return kUnknown;
                if (r == 0 || reached.ContainsKey(r)) return kReach;
            }
            return kRefuse;
        }
    }

    // (Sim, target, interaction kind) pairs whose route reachcache refused, valid while the lot's portal cache version
    // (EA's Lot.GetObjectCacheVersion<IPortalConnectionObject>, bumped by every lock change, privacy start / end and build
    // or buy refresh) is the one recorded and before the expiry tick. Autonomy leaves such a pair out instead of picking
    // it again. Cleared whole at kMax entries.
    public sealed class RcMemo
    {
        public const int kMax = 256;
        struct Key
        {
            public ulong Sim, Target; public int Kind;
            public override int GetHashCode() { return Sim.GetHashCode() ^ (Target.GetHashCode() * 31) ^ (Kind * 17); }
            public override bool Equals(object o) { if (!(o is Key)) return false; Key k = (Key)o; return k.Sim == Sim && k.Target == Target && k.Kind == Kind; }
        }
        struct Entry { public ulong Lot; public int Version; public long Until; }
        readonly Dictionary<Key, Entry> mMap = new Dictionary<Key, Entry>();

        public int Count { get { return mMap.Count; } }
        public void Clear() { mMap.Clear(); }

        public void Record(ulong sim, ulong target, int kind, ulong lot, int version, long until)
        {
            if (mMap.Count >= kMax) mMap.Clear();
            Key k; k.Sim = sim; k.Target = target; k.Kind = kind;
            Entry e; e.Lot = lot; e.Version = version; e.Until = until;
            mMap[k] = e;
        }

        // 1 = blocked, 0 = no entry, -1 = an entry was stale (dropped)
        public int Blocked(ulong sim, ulong target, int kind, ulong lot, int versionNow, long now)
        {
            if (mMap.Count == 0) return 0;
            Key k; k.Sim = sim; k.Target = target; k.Kind = kind;
            Entry e;
            if (!mMap.TryGetValue(k, out e)) return 0;
            if (e.Lot != lot || e.Version != versionNow || now >= e.Until) { mMap.Remove(k); return -1; }
            return 1;
        }
    }
}
