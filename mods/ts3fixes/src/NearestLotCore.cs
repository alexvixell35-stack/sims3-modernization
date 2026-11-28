namespace Ts3Fixes
{
    // Game-independent part of NearestLot.cs (docs/677): the nearest valid item of a list, with the cheap value (squared
    // distance) for every item first and the costly validity test only for the items that could still win.
    // Self-check: tools/selftest/nearestlot.
    public static class NearestLotCore
    {
        public delegate bool Valid(int index);

        // EA's loop, word for word: every item is tested, the first one with the strictly smallest distance wins.
        public static int EaPick(float[] d2, Valid valid, out int tests)
        {
            int best = -1;
            float min = float.MaxValue;
            tests = 0;
            for (int i = 0; i < d2.Length; i++)
            {
                tests++;
                if (!valid(i)) continue;
                if (d2[i] < min) { best = i; min = d2[i]; }
            }
            return best;
        }

        // The same item with the tests in distance order: take the nearest untested item (the first of equal ones), test
        // it, return it if valid. An item EA's loop can never take (distance NaN or not below float.MaxValue) is never
        // tested. Each round is one pass over the list; a round ends the call unless its item is invalid.
        public static int LazyPick(float[] d2, Valid valid, out int tests)
        {
            int n = d2.Length;
            bool[] done = new bool[n];
            tests = 0;
            for (;;)
            {
                int best = -1;
                float min = float.MaxValue;
                for (int i = 0; i < n; i++)
                    if (!done[i] && d2[i] < min) { best = i; min = d2[i]; }
                if (best < 0) return -1;
                done[best] = true;
                tests++;
                if (valid(best)) return best;
            }
        }
    }
}
