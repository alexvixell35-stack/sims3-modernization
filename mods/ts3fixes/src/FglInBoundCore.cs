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
    }
}
