namespace Ts3Fixes
{
    // docs/208: game-free compare rule of AutOffload.cs, so tools/selftest/autoffload can run it offline.
    public static class AutOffloadCore
    {
        // Broadcaster candidate lists (Mode R): 0 = same ids, order and distance flags; 1 = same (id, flag) pairs in another
        // order; 2 = anything else. Ids are compared by reference (the Sims themselves).
        public static int SeqClass(object[] w, bool[] wn, int nw, object[] r, bool[] rn, int nr)
        {
            if (nw != nr) return 2;
            bool same = true;
            for (int i = 0; i < nw; i++) same &= w[i] == r[i] && wn[i] == rn[i];
            if (same) return 0;
            for (int i = 0; i < nw; i++)
            {
                int at = -1;
                for (int j = 0; j < nr && at < 0; j++) if (r[j] == w[i]) at = j;
                if (at < 0 || rn[at] != wn[i]) return 2;
            }
            return 1;
        }

        // docs/208 §8.1 due pick: keep the `max` largest keys in descending order (ties keep the earlier one); returns the
        // new count. items moves with keys.
        public static int InsertTop(float[] keys, object[] items, int n, int max, float f, object item)
        {
            int at = n < max ? n++ : max;
            if (at == max) { if (f <= keys[max - 1]) return n; at = max - 1; }
            while (at > 0 && keys[at - 1] < f) { keys[at] = keys[at - 1]; items[at] = items[at - 1]; at--; }
            keys[at] = f; items[at] = item;
            return n;
        }

        // docs/208 §9.2, Camera.GetClosestVisibleLot: where a camera target lies against a lot, in lot space. Cell 0-8 = row
        // (0 beyond the extent's z, 1 within, 2 before the origin's z) x 3 + column (0 before the origin's x, 1 within, 2 beyond
        // the extent's x); 4 = on the lot. pad widens the lot (a houseboat's 10 m).
        public static int LotCell(float tx, float tz, float ox, float oz, float ex, float ez, float pad)
        {
            int col = tx < ox - pad ? 0 : tx > ex + pad ? 2 : 1;
            int row = tz > ez + pad ? 0 : tz < oz - pad ? 2 : 1;
            return row * 3 + col;
        }

        // Squared distance to the lot for that cell: an edge cell uses the delta to that edge, a corner cell the corner's own
        // squared distance (tl, tr, bl, br). Cell 4 = 0.
        public static float LotCellDist(int cell, float dxo, float dzo, float dxe, float dze, float tl, float tr, float bl, float br)
        {
            switch (cell)
            {
                case 0: return tl;
                case 1: return dze * dze;
                case 2: return tr;
                case 3: return dxo * dxo;
                case 5: return dxe * dxe;
                case 6: return bl;
                case 7: return dzo * dzo;
                case 8: return br;
                default: return 0f;
            }
        }

        // One lot of the closest-lot walk: 0 keep the current pick, 1 new pick, 2 new pick and focus lot. On a lot (cell 4) the
        // first one wins unless a houseboat lot comes later; else the nearest wins, ties keep the earlier one. Cell -1 (a lot
        // with no corners) counts as distance d off the lot.
        public static int LotPickStep(int cell, float d, bool houseboat, ref float best)
        {
            if (cell == 4)
            {
                if (best <= 0f && !houseboat) return 0;
                best = 0f;
                return 2;
            }
            if (d >= best) return 0;
            best = d;
            return 1;
        }
    }
}
