using System;

namespace Ts3Fixes
{
    // Game-independent part of FglInBound.cs (docs/682): how far from the start the lot-wide "indoors" search of
    // GlobalFunctions.FindGoodLocationNearby can find anything, and a model of the native search's radius rule and
    // ring order for the self-check (tools/selftest/fglinbound) and FglInBoundCheck.
    public static class FglInBoundCore
    {
        // Added to the distance of the farthest tile that may be indoors. The native test compares the tile's integer
        // coordinates with the start's float coordinates (search 0xB470C0, block 0xB475C7); we measure from the centre
        // of the start's tile to the tile's centre, which is at most 0.71 + 0.71 off. The rest is slack.
        public const float kMargin = 2.5f;
        public const float kNativeNoLimit = 1000000f;   // the native search tests the radius only when it is at most this
        public const int kOk = 1, kNoGain = 0, kBadStart = -1;

        // cls: w * h bytes, index z * w + x; nonzero = a spot on that tile may be indoors (any level the search can test).
        // radius: every such tile passes the native radius test with it. marked: tiles that may be indoors.
        // within: tiles inside the radius (what the bounded search can still test).
        public static int Bound(byte[] cls, int w, int h, int x0, int z0, out float radius, out int marked, out int within)
        {
            radius = 0f; marked = 0; within = 0;
            if (cls == null || w <= 0 || h <= 0 || cls.Length < w * h || x0 < 0 || z0 < 0 || x0 >= w || z0 >= h) return kBadStart;
            if (cls[z0 * w + x0] == 0) return kBadStart;   // EA runs the stage only from a start that is indoors
            float maxIn = 0f;
            for (int z = 0; z < h; z++)
                for (int x = 0; x < w; x++)
                {
                    if (cls[z * w + x] == 0) continue;
                    marked++;
                    float d2 = Dist2(x, z, x0, z0);
                    if (d2 > maxIn) maxIn = d2;
                }
            radius = (float)Math.Sqrt(maxIn) + kMargin;
            float r2 = radius * radius;
            for (int z = 0; z < h; z++)
                for (int x = 0; x < w; x++)
                    if (Dist2(x, z, x0, z0) <= r2) within++;
            return within >= w * h || radius > kNativeNoLimit ? kNoGain : kOk;
        }

        static float Dist2(int x, int z, int x0, int z0)
        {
            float dx = x - x0, dz = z - z0;
            return dx * dx + dz * dz;
        }

        // A spot the probe search (radius r) returned on square ring `ring` around the start's tile is the spot the
        // unlimited search returns when no tile of that ring or an inner one can fail the radius test: such a tile's
        // integer coordinates are at most ring + 1 from the start in x and in z.
        public static bool ProbeExact(int ring, float r)
        {
            return ring >= 0 && (ring + 1) * 1.41422f <= r;
        }

        // The native radius test (0xB475C7): skipped above kNativeNoLimit, else tile inside iff dx^2 + dz^2 <= r^2 with
        // the tile's integer coordinates and the start's float coordinates.
        public static bool Tested(int tx, int tz, float sx, float sz, float radius)
        {
            if (radius > kNativeNoLimit) return true;
            float dx = tx - sx, dz = tz - sz;
            return radius * radius >= dx * dx + dz * dz;
        }

        // Model of one lot-wide search with a position check: square rings around the start's tile, inner ring first;
        // a tile outside the radius is stepped over; the first tile that passes the placement test (pass) and the
        // check (accept) ends it. Returns the tile index (z * w + x) or -1. tested: placement tests run.
        public static int ModelScan(int w, int h, float sx, float sz, bool[] pass, bool[] accept, float radius, out int tested)
        {
            tested = 0;
            int x0 = (int)Math.Floor(sx), z0 = (int)Math.Floor(sz);
            int rings = Math.Max(Math.Max(x0, w - 1 - x0), Math.Max(z0, h - 1 - z0));
            for (int k = 0; k <= rings; k++)
            {
                int side = k == 0 ? 1 : 8 * k;
                for (int i = 0; i < side; i++)
                {
                    int x, z;
                    RingTile(k, i, out x, out z);
                    x += x0; z += z0;
                    if (x < 0 || z < 0 || x >= w || z >= h) continue;
                    if (!Tested(x, z, sx, sz, radius)) continue;
                    tested++;
                    int t = z * w + x;
                    if (pass[t] && accept[t]) return t;
                }
            }
            return -1;
        }

        // tile i of square ring k (k >= 1: 8 * k tiles), starting at (k, -k + 1) and going round once
        public static void RingTile(int k, int i, out int x, out int z)
        {
            if (k == 0) { x = 0; z = 0; return; }
            int side = 2 * k, edge = i / side, off = i % side;
            switch (edge)
            {
                case 0: x = k; z = -k + 1 + off; break;
                case 1: x = k - 1 - off; z = k; break;
                case 2: x = -k; z = k - 1 - off; break;
                default: x = -k + 1 + off; z = -k; break;
            }
        }

        // ---- clusters (docs/682 §8): one search per group of tiles that may be indoors, centred on the group ----

        // A search centred anywhere in tile (cx, cz) keeps tile t when r >= |t - c| + sqrt 2 (integer tile coordinates
        // against a float start inside the centre tile: at most 1 off in x and in z).
        public const float kClusterMargin = 1.5f;
        public const int kMaxComponents = 96;

        public sealed class Cluster
        {
            public int[] Members;      // tile indices (z * w + x)
            public int Cx, Cz;         // centre tile
            public float Radius;       // covers every member from anywhere in the centre tile
            public int MinRing;        // nearest member's square ring around the search's own start tile
            public int Cost;           // tiles of the lot inside the radius (what the search can test)
        }

        public static float RadiusFor(int[] members, int w, int cx, int cz, float margin)
        {
            float max = 0f;
            for (int i = 0; i < members.Length; i++)
            {
                float d2 = Dist2(members[i] % w, members[i] / w, cx, cz);
                if (d2 > max) max = d2;
            }
            return (float)Math.Sqrt(max) + margin;
        }

        public static int CostFor(int w, int h, int cx, int cz, float radius)
        {
            int n = 0, k = (int)radius + 1;
            float r2 = radius * radius;
            for (int z = Math.Max(0, cz - k); z <= Math.Min(h - 1, cz + k); z++)
                for (int x = Math.Max(0, cx - k); x <= Math.Min(w - 1, cx + k); x++)
                    if (Dist2(x, z, cx, cz) <= r2) n++;
            return n;
        }

        static void Centre(int[] members, int w, int h, out int cx, out int cz)
        {
            long sx = 0, sz = 0;
            for (int i = 0; i < members.Length; i++) { sx += members[i] % w; sz += members[i] / w; }
            cx = (int)((sx + members.Length / 2) / members.Length);
            cz = (int)((sz + members.Length / 2) / members.Length);
            if (cx >= w) cx = w - 1;
            if (cz >= h) cz = h - 1;
        }

        // The tiles that may be indoors as at most maxSearches clusters, nearest (by ring around x0, z0) first: the
        // 8-connected groups, the two with the nearest centres merged while there are too many. cost: sum of the
        // clusters' Cost. kNoGain: too many groups, or the searches would test 60% of the lot or more.
        public static int Clusters(byte[] cls, int w, int h, int x0, int z0, int maxSearches, out Cluster[] list, out int marked, out int cost)
        {
            list = null; marked = 0; cost = 0;
            if (cls == null || w <= 0 || h <= 0 || cls.Length < w * h || x0 < 0 || z0 < 0 || x0 >= w || z0 >= h || maxSearches < 1) return kBadStart;
            if (cls[z0 * w + x0] == 0) return kBadStart;
            int n = w * h;
            bool[] seen = new bool[n];
            int[] stack = new int[n], buf = new int[n];
            System.Collections.Generic.List<int[]> groups = new System.Collections.Generic.List<int[]>();
            for (int t = 0; t < n; t++)
            {
                if (cls[t] == 0 || seen[t]) continue;
                int sp = 0, len = 0;
                stack[sp++] = t; seen[t] = true;
                while (sp > 0)
                {
                    int u = stack[--sp];
                    buf[len++] = u;
                    int ux = u % w, uz = u / w;
                    for (int dz = -1; dz <= 1; dz++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int x = ux + dx, z = uz + dz;
                            if (x < 0 || z < 0 || x >= w || z >= h) continue;
                            int v = z * w + x;
                            if (cls[v] == 0 || seen[v]) continue;
                            seen[v] = true; stack[sp++] = v;
                        }
                }
                int[] g = new int[len];
                Array.Copy(buf, g, len);
                groups.Add(g);
                marked += len;
                if (groups.Count > kMaxComponents) return kNoGain;
            }
            while (groups.Count > maxSearches)
            {
                int bi = -1, bj = -1;
                float best = float.MaxValue;
                int[] cx = new int[groups.Count], cz = new int[groups.Count];
                for (int i = 0; i < groups.Count; i++) Centre(groups[i], w, h, out cx[i], out cz[i]);
                for (int i = 0; i < groups.Count; i++)
                    for (int j = i + 1; j < groups.Count; j++)
                    {
                        float d2 = Dist2(cx[i], cz[i], cx[j], cz[j]);
                        if (d2 < best) { best = d2; bi = i; bj = j; }
                    }
                int[] m = new int[groups[bi].Length + groups[bj].Length];
                Array.Copy(groups[bi], m, groups[bi].Length);
                Array.Copy(groups[bj], 0, m, groups[bi].Length, groups[bj].Length);
                groups[bi] = m;
                groups.RemoveAt(bj);
            }
            list = new Cluster[groups.Count];
            for (int i = 0; i < list.Length; i++)
            {
                Cluster c = new Cluster();
                c.Members = groups[i];
                Centre(c.Members, w, h, out c.Cx, out c.Cz);
                c.Radius = RadiusFor(c.Members, w, c.Cx, c.Cz, kClusterMargin);
                c.Cost = CostFor(w, h, c.Cx, c.Cz, c.Radius);
                c.MinRing = int.MaxValue;
                for (int k = 0; k < c.Members.Length; k++)
                {
                    int ring = Ring(c.Members[k] % w, c.Members[k] / w, x0, z0);
                    if (ring < c.MinRing) c.MinRing = ring;
                }
                cost += c.Cost;
                list[i] = c;
            }
            // nearest first (insertion sort: a handful of clusters; equal rings keep their order)
            for (int i = 1; i < list.Length; i++)
            {
                Cluster c = list[i];
                int j = i - 1;
                while (j >= 0 && list[j].MinRing > c.MinRing) { list[j + 1] = list[j]; j--; }
                list[j + 1] = c;
            }
            return (long)cost * 10 >= (long)n * 6 ? kNoGain : kOk;
        }

        public static int Ring(int x, int z, int x0, int z0) { return Math.Max(Math.Abs(x - x0), Math.Abs(z - z0)); }

        // the spot kept over all cluster searches: the nearest ring around the start, then the nearest tile
        public static bool Better(int ring, float d2, int bestRing, float bestD2)
        {
            return ring < bestRing || (ring == bestRing && d2 < bestD2);
        }

        // a cluster may be left out once a spot is kept on a ring no member of it can beat
        public static bool Skip(int clusterMinRing, int bestRing) { return bestRing <= clusterMinRing; }

        // Model of one cluster search: every tile inside the radius (native rule, start anywhere in the centre tile)
        // that passes and is accepted is offered; nothing ends the search. Returns the offers' best by Better.
        public static void ModelCluster(int w, int h, float sx, float sz, float radius, bool[] pass, bool[] accept, int x0, int z0,
            ref int bestTile, ref int bestRing, ref float bestD2, ref int tested)
        {
            for (int z = 0; z < h; z++)
                for (int x = 0; x < w; x++)
                {
                    if (!Tested(x, z, sx, sz, radius)) continue;
                    tested++;
                    int t = z * w + x;
                    if (!pass[t] || !accept[t]) continue;
                    int ring = Ring(x, z, x0, z0);
                    float d2 = Dist2(x, z, x0, z0);
                    if (Better(ring, d2, bestRing, bestD2)) { bestTile = t; bestRing = ring; bestD2 = d2; }
                }
        }

        // all clusters, nearest first, with the skip rule. Returns the kept tile or -1.
        public static int ModelClusters(Cluster[] list, int w, int h, float fx, float fz, float margin, bool[] pass, bool[] accept, int x0, int z0, out int tested, out int searches)
        {
            int bestTile = -1, bestRing = int.MaxValue;
            float bestD2 = float.MaxValue;
            tested = 0; searches = 0;
            for (int i = 0; i < list.Length; i++)
            {
                if (Skip(list[i].MinRing, bestRing)) break;
                searches++;
                float r = RadiusFor(list[i].Members, w, list[i].Cx, list[i].Cz, margin);
                ModelCluster(w, h, list[i].Cx + fx, list[i].Cz + fz, r, pass, accept, x0, z0, ref bestTile, ref bestRing, ref bestD2, ref tested);
            }
            return bestTile;
        }
    }
}
