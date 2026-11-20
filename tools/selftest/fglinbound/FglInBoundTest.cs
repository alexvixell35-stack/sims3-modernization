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
        Console.WriteLine("fglinbound selftest: " + cases + " random lots (bounded " + ok + ", radius covers the lot " + noGain + ", a spot found " + found + "), placement tests "
            + ourTests + " against EA's " + eaTests + " where bounded, radius without the margin differs in " + tight + ", probe spots taken " + probeTaken + " of " + probeHits + ", fails " + sFails);
        return sFails == 0 ? 0 : 1;
    }
}
