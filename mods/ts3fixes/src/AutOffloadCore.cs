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
    }
}
