namespace Ts3Fixes
{
    // Game-independent watch table of boatgetoutdiag (BoatGetOutDiag.cs, docs/257 §7). Self-check: tools/selftest/boatgetoutdiag.
    // One entry per Boat.GetOut in flight: the Sim, its SittingInBoat posture at entry, the calling task, the depth (GetOuts of
    // the same Sim already in flight + 1). Sim thread only.
    public sealed class BgoTable
    {
        public const int kMax = 8, kIgnore = -1, kOwnEnd = 0, kForeign = 1, kNested = 2;
        public readonly object[] Sim = new object[kMax], Entry = new object[kMax];
        public readonly ulong[] Task = new ulong[kMax];
        public readonly int[] Depth = new int[kMax], Id = new int[kMax];
        public readonly bool[] Replaced = new bool[kMax], Water = new bool[kMax], Houseboat = new bool[kMax];
        public readonly long[] At = new long[kMax];
        public int N, Overflow;

        // slot, or -1 when full (counted in Overflow, that GetOut is not watched)
        public int Push(object sim, object entry, ulong task)
        {
            if (N >= kMax) { Overflow++; return -1; }
            int d = 1, free = -1;
            for (int i = 0; i < kMax; i++)
            {
                if (Sim[i] == null) { if (free < 0) free = i; }
                else if (Sim[i] == sim) d++;
            }
            Sim[free] = sim; Entry[free] = entry; Task[free] = task; Depth[free] = d; Replaced[free] = false;
            N++;
            return free;
        }

        public void Pop(int i)
        {
            if (i < 0 || Sim[i] == null) return;
            Sim[i] = null; Entry[i] = null; Replaced[i] = false;
            N--;
        }

        // A posture set on sim while its current posture is cur, by task: kIgnore when no watch has cur as its entry posture;
        // else at = the outermost such watch and kOwnEnd (that watch's own task, no other GetOut of the Sim in flight: the
        // normal end, since the own task sleeps through the fade), kNested (another GetOut of the same Sim is in flight) or
        // kForeign (another task).
        public int Classify(object sim, object cur, ulong task, out int at)
        {
            at = -1;
            int n = 0;
            for (int i = 0; i < kMax; i++)
            {
                if (Sim[i] == null || Sim[i] != sim) continue;
                n++;
                if (Entry[i] == cur && (at < 0 || Depth[i] < Depth[at])) at = i;
            }
            if (at < 0) return kIgnore;
            if (n > 1) return kNested;
            return Task[at] == task ? kOwnEnd : kForeign;
        }
    }
}
