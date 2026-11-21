namespace Ts3Fixes
{
    // docs/314: the tester kit's A/B session counters (KitArm.cs), no game types, so tools/selftest/kitarm checks them offline.
    // Gap() gets the real time between two wakes of the sim-thread task (one per sim update). Seconds close on the real
    // clock: a 3 s hitch leaves seconds with 0 updates. Gaps that touch a save are counted apart and close no second.
    public sealed class KitStats
    {
        public int LiveMs, H250, H1000, MaxMs, SaveGaps, SaveMaxMs, Secs;
        public readonly int[] Ups = new int[121];   // seconds by sim updates in them (120 = 120 or more)
        int mSecStart, mSecN;

        public void Restart(int now) { mSecStart = now; mSecN = 0; }

        public void Gap(int gapMs, bool save, int now)
        {
            if (gapMs < 0) gapMs = 0;
            if (save)
            {
                if (gapMs >= 250) { SaveGaps++; if (gapMs > SaveMaxMs) SaveMaxMs = gapMs; }
                Restart(now);
                return;
            }
            LiveMs += gapMs;
            if (gapMs >= 250) H250++;
            if (gapMs >= 1000) H1000++;
            if (gapMs > MaxMs) MaxMs = gapMs;
            mSecN++;
            while (now - mSecStart >= 1000)
            {
                Ups[mSecN > 120 ? 120 : mSecN]++;
                Secs++;
                mSecN = 0;
                mSecStart += 1000;
            }
        }

        // median and lowest sim updates per real second; -1 before the first whole second
        public void Rates(out int median, out int low)
        {
            median = -1; low = -1;
            int seen = 0;
            for (int i = 0; i < Ups.Length; i++)
            {
                if (Ups[i] == 0) continue;
                if (low < 0) low = i;
                seen += Ups[i];
                if (median < 0 && seen * 2 >= Secs) median = i;
            }
        }
    }
}
