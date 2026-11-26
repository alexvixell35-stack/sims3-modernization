using System.Collections.Generic;

namespace Ts3Fixes
{
    // docs/321 §10 (LaneA.cs): pure parts, offline selftest tools/selftest/lanea
    public static class LaneACore
    {
        public static void See(int[] c, int slot, int n) { c[slot] = n; if (n > c[slot + 1]) c[slot + 1] = n; }

        // rows whose key's low dword is the lot's (Overflow: every row); returns rows dropped
        public static int DropLot<V>(Dictionary<ulong, V> d, bool all, uint lotLow)
        {
            int n = d.Count;
            if (all) { d.Clear(); return n; }
            List<ulong> gone = null;
            foreach (ulong k in d.Keys) if ((uint)k == lotLow) (gone ?? (gone = new List<ulong>())).Add(k);
            if (gone != null) foreach (ulong k in gone) d.Remove(k);
            return n - d.Count;
        }

        public interface IWorldRow { string WorldName { get; } }
        public static int DropOtherWorlds<V>(Dictionary<ulong, V> d, string world) where V : IWorldRow
        {
            List<ulong> gone = new List<ulong>();
            foreach (KeyValuePair<ulong, V> kv in d) if (kv.Value.WorldName != world) gone.Add(kv.Key);
            foreach (ulong k in gone) d.Remove(k);
            return gone.Count;
        }
    }
}
