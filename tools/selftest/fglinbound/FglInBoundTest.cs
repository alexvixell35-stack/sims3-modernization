using System;
using Ts3Fixes;

// Offline self-check of FglInBoundCore (docs/682): on random lots the search with the bound's radius returns the tile
// the unlimited search returns, for a start anywhere in its tile, with tiles the tile pass can only mark as "may be
// indoors" (diagonal walls, a room on the level above) and with the probe's ring rule. The model is the native rule
// as disassembled: rings around the start's tile, a tile outside the radius (integer tile coordinates against the
// float start) is stepped over. A radius without the margin must fail somewhere, or the cases prove nothing.
static class FglInBoundTest
{
    static int sFails;
    static void Check(bool ok, string what) { if (!ok) { sFails++; if (sFails < 20) Console.WriteLine("FAIL " + what); } }

    static int Main()
    {
        Random r = new Random(682);
        int clSame = 0, clOther = 0, clOk = 0, clSearches = 0, clTight = 0;
        long clTests = 0, clEaTests = 0;
        int cases = 0, ok = 0, noGain = 0, found = 0, eaTests = 0, ourTests = 0, tight = 0, probeHits = 0, probeTaken = 0;
        for (int k = 0; k < 20000; k++)
        {
            int w = r.Next(4, 65), h = r.Next(4, 65), n = w * h;
            byte[] cls = new byte[n];
            bool[] inside = new bool[n], pass = new bool[n];
            // rooms: a few rectangles; the start is in the first
            int rooms = r.Next(1, 4);
            int x0 = 0, z0 = 0;
            for (int i = 0; i < rooms; i++)
            {
                int rw = r.Next(1, Math.Min(w, 12) + 1), rh = r.Next(1, Math.Min(h, 12) + 1);
                int rx = r.Next(0, w - rw + 1), rz = r.Next(0, h - rh + 1);
                for (int z = rz; z < rz + rh; z++) for (int x = rx; x < rx + rw; x++) { cls[z * w + x] = 1; inside[z * w + x] = true; }
                if (i == 0) { x0 = rx + r.Next(rw); z0 = rz + r.Next(rh); }
            }
            // tiles the pass marks without knowing: a diagonal wall (the spot may be on either side), a room one level up
            int odd = r.Next(0, 6);
            for (int i = 0; i < odd; i++)
            {
                int t = r.Next(n);
                if (cls[t] != 0) continue;
                cls[t] = 2; inside[t] = r.Next(2) == 0;
            }
            int passPct = r.Next(0, 40);
            for (int t = 0; t < n; t++) pass[t] = r.Next(100) < passPct;
            if (r.Next(3) == 0) for (int t = 0; t < n; t++) if (inside[t]) pass[t] = false;   // the logged shape: no free indoor spot
            float sx = x0 + (float)r.NextDouble() * 0.999f, sz = z0 + (float)r.NextDouble() * 0.999f;
            float radius;
            int marked, within, a, b;
            int rc = FglInBoundCore.Bound(cls, w, h, x0, z0, out radius, out marked, out within);
            Check(rc != FglInBoundCore.kBadStart, "case " + k + ": start refused");
            int ea = FglInBoundCore.ModelScan(w, h, sx, sz, pass, inside, 10000000f, out a);
            int mine = FglInBoundCore.ModelScan(w, h, sx, sz, pass, inside, radius, out b);
            Check(ea == mine, "case " + k + ": EA " + ea + " bounded " + mine + " radius " + radius);
            Check(b <= a || ea >= 0, "case " + k + ": more tests " + b + " than EA " + a);
            cases++;
            if (rc == FglInBoundCore.kOk) { ok++; eaTests += a; ourTests += b; } else noGain++;
            if (ea >= 0) found++;
            // without the margin, and a little less, some case must differ
            int c;
            if (FglInBoundCore.ModelScan(w, h, sx, sz, pass, inside, radius - FglInBoundCore.kMargin - 1.5f, out c) != ea) tight++;
            // design 2: cluster searches. Found iff EA finds, on EA's ring; the start of each search anywhere in its centre tile
            {
                FglInBoundCore.Cluster[] list;
                int cm, cost, ct, cs;
                int crc = FglInBoundCore.Clusters(cls, w, h, x0, z0, r.Next(1, 9), out list, out cm, out cost);
                Check(crc != FglInBoundCore.kBadStart && cm == marked, "case " + k + ": clusters rc " + crc + " marked " + cm + " / " + marked);
                if (list != null)
                {
                    int members = 0;
                    for (int i = 0; i < list.Length; i++) { members += list[i].Members.Length; Check(i == 0 || list[i - 1].MinRing <= list[i].MinRing, "case " + k + ": cluster order"); }
                    Check(members == marked, "case " + k + ": members " + members + " marked " + marked);
                    float fx = (float)r.NextDouble() * 0.999f, fz = (float)r.NextDouble() * 0.999f;
                    int cl = FglInBoundCore.ModelClusters(list, w, h, fx, fz, FglInBoundCore.kClusterMargin, pass, inside, x0, z0, out ct, out cs);
                    Check((cl >= 0) == (ea >= 0), "case " + k + ": clusters " + cl + " EA " + ea);
                    if (cl >= 0 && ea >= 0)
                    {
                        Check(FglInBoundCore.Ring(cl % w, cl / w, x0, z0) == FglInBoundCore.Ring(ea % w, ea / w, x0, z0), "case " + k + ": cluster spot " + cl + " not on EA's ring (" + ea + ")");
                        Check(pass[cl] && inside[cl], "case " + k + ": cluster spot not valid");
                        if (cl == ea) clSame++; else clOther++;
                    }
                    if (crc == FglInBoundCore.kOk) { clOk++; clTests += ct; clEaTests += a; clSearches += cs; }
                    int cl0 = FglInBoundCore.ModelClusters(list, w, h, fx, fz, -0.5f, pass, inside, x0, z0, out ct, out cs);
                    if ((cl0 >= 0) != (ea >= 0) || (cl0 >= 0 && FglInBoundCore.Ring(cl0 % w, cl0 / w, x0, z0) != FglInBoundCore.Ring(ea % w, ea / w, x0, z0))) clTight++;
                }
            }
            // the probe: its spot is taken only on a ring the radius cannot cut, and then it is EA's
            float pr = 3f + r.Next(0, 10);
            int p = FglInBoundCore.ModelScan(w, h, sx, sz, pass, inside, pr, out c);
            if (p >= 0)
            {
                probeHits++;
                int ring = Math.Max(Math.Abs(p % w - x0), Math.Abs(p / w - z0));
                if (FglInBoundCore.ProbeExact(ring, pr)) { probeTaken++; Check(p == ea, "case " + k + ": probe " + p + " EA " + ea + " ring " + ring + " r " + pr); }
            }
        }
        Check(tight > 0, "a radius without the margin never differed: the cases do not reach the boundary");
        Check(probeTaken > 100 && probeTaken < probeHits, "probe rule not exercised: taken " + probeTaken + " of " + probeHits);
        Check(clTight > 0, "cluster radius below the farthest member never differed: the cases do not reach the boundary");
        Check(clOk > 1000 && clOther > 0, "cluster cases not exercised: " + clOk + " / other tile " + clOther);
        // the s208 shape: a 3 x 3 room at the start, four other buildings, 196 free outdoor spots, no free indoor spot
        {
            const int W = 64, H = 64, X0 = 31, Z0 = 54;
            byte[] c2 = new byte[W * H];
            bool[] p2 = new bool[W * H], i2 = new bool[W * H];
            int[] bx = new int[] { X0 - 1, 4, 50, 8, 44 }, bz = new int[] { Z0 - 1, 4, 6, 40, 30 }, bs = new int[] { 3, 8, 7, 6, 9 };
            for (int q = 0; q < bx.Length; q++)
                for (int z = bz[q]; z < bz[q] + bs[q]; z++) for (int x = bx[q]; x < bx[q] + bs[q]; x++) { c2[z * W + x] = 1; i2[z * W + x] = true; }
            for (int i = 0; i < 196; i++) { int t = (14 + i / 14 * 3) * W + 16 + i % 14 * 2; if (!i2[t]) p2[t] = true; }
            FglInBoundCore.Cluster[] list;
            int cm, cost, et, ot, sr;
            Check(FglInBoundCore.Clusters(c2, W, H, X0, Z0, 8, out list, out cm, out cost) == FglInBoundCore.kOk && list.Length == 5 && cm == 239, "shape: clusters");
            int e2 = FglInBoundCore.ModelScan(W, H, X0 + 0.1f, Z0 + 0.6f, p2, i2, 10000000f, out et);
            int m2 = FglInBoundCore.ModelClusters(list, W, H, 0.3f, 0.7f, FglInBoundCore.kClusterMargin, p2, i2, X0, Z0, out ot, out sr);
            Check(e2 == -1 && m2 == -1 && et == W * H && ot * 3 < et && sr == 5, "shape: EA " + e2 + " (" + et + " tiles) ours " + m2 + " (" + ot + " tiles, " + sr + " searches)");
            Console.WriteLine("fglinbound selftest: shape of s208: " + ot + " tiles tested in " + sr + " searches against EA's " + et);
            // more groups than searches: merged, still every member covered
            Check(FglInBoundCore.Clusters(c2, W, H, X0, Z0, 2, out list, out cm, out cost) != FglInBoundCore.kBadStart && list.Length == 2, "shape: merge to 2");
        }
        // ring walk: every tile of a ring once, all on the ring
        for (int k = 1; k < 6; k++)
        {
            bool[] seen = new bool[(2 * k + 1) * (2 * k + 1)];
            int nRing = 0;
            for (int i = 0; i < 8 * k; i++)
            {
                int x, z;
                FglInBoundCore.RingTile(k, i, out x, out z);
                Check(Math.Max(Math.Abs(x), Math.Abs(z)) == k, "ring " + k + " tile " + i + " off the ring");
                int t = (z + k) * (2 * k + 1) + x + k;
                if (!seen[t]) { seen[t] = true; nRing++; }
            }
            Check(nRing == 8 * k, "ring " + k + ": " + nRing + " tiles");
        }
        // fixed shapes
        float rr; int m, wi;
        Check(FglInBoundCore.Bound(null, 4, 4, 0, 0, out rr, out m, out wi) == FglInBoundCore.kBadStart, "null grid");
        Check(FglInBoundCore.Bound(new byte[16], 4, 4, 1, 1, out rr, out m, out wi) == FglInBoundCore.kBadStart, "start outdoors");
        Check(FglInBoundCore.Bound(new byte[16], 4, 4, 4, 1, out rr, out m, out wi) == FglInBoundCore.kBadStart, "start off the grid");
        byte[] one = new byte[64 * 64];
        one[54 * 64 + 31] = 1;
        Check(FglInBoundCore.Bound(one, 64, 64, 31, 54, out rr, out m, out wi) == FglInBoundCore.kOk && rr == FglInBoundCore.kMargin && m == 1 && wi < 30, "one tile room");
        Check(FglInBoundCore.Tested(0, 0, 500f, 500f, 2000000f) && !FglInBoundCore.Tested(0, 0, 3f, 4f, 4.99f) && FglInBoundCore.Tested(0, 0, 3f, 4f, 5f), "native radius rule");
        Console.WriteLine("fglinbound selftest: clusters: worth it on " + clOk + " lots, " + clTests + " tiles tested against EA's " + clEaTests + " in " + clSearches
            + " searches; EA's tile " + clSame + ", another tile of EA's ring " + clOther + "; radius 0.5 short differs in " + clTight);
        Console.WriteLine("fglinbound selftest: " + cases + " random lots (bounded " + ok + ", radius covers the lot " + noGain + ", a spot found " + found + "), placement tests "
            + ourTests + " against EA's " + eaTests + " where bounded, radius without the margin differs in " + tight + ", probe spots taken " + probeTaken + " of " + probeHits + ", fails " + sFails);
        return sFails == 0 ? 0 : 1;
    }
}
