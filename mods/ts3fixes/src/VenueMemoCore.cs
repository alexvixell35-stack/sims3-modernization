using System;

namespace Ts3Fixes
{
    // Game-independent part of the venue score memo (VenueMemo.cs, docs/122 §14.24), kept free of game types so the
    // offline self-test (tools/selftest/venuememo) compiles it alone.
    //
    // Answers of MetaAutonomyManager.GetTuning per venue type (the enum value as the index) for one world name and type.
    // Only non-null answers are kept: the tuning lists are only ever appended to (the Venues XML parse), and an append
    // never changes the first match of a type that already had one, but it can give a type without one its first match.
    public sealed class TuningMemo
    {
        object[] mAnswers;
        int mWorld = int.MinValue, mWorldType = int.MinValue;

        public int Count { get { int n = 0; if (mAnswers != null) foreach (object o in mAnswers) if (o != null) n++; return n; } }

        // Sizes the table to the venue type range (largest enum value + 1); a type outside it is never kept.
        public TuningMemo Size(int types) { mAnswers = new object[types < 0 ? 0 : types]; return this; }

        // Names the world the answers belong to. True when that is a change and kept answers were dropped.
        public bool World(int world, int worldType)
        {
            if (world == mWorld && worldType == mWorldType) return false;
            bool had = Clear();
            mWorld = world; mWorldType = worldType;
            return had;
        }

        // Drops every answer; true when there was one. The world stays named.
        public bool Clear()
        {
            if (mAnswers == null) return false;
            bool had = false;
            for (int i = 0; i < mAnswers.Length; i++) if (mAnswers[i] != null) { mAnswers[i] = null; had = true; }
            return had;
        }

        public bool TryGet(int type, out object answer)
        {
            answer = mAnswers != null && type >= 0 && type < mAnswers.Length ? mAnswers[type] : null;
            return answer != null;
        }

        public void Store(int type, object answer)
        {
            if (answer != null && mAnswers != null && type >= 0 && type < mAnswers.Length) mAnswers[type] = answer;
        }

        // Verify compare of two scores: equal bits count as the same answer, NaN included.
        public static bool SameFloat(float a, float b)
        {
            return BitConverter.ToInt32(BitConverter.GetBytes(a), 0) == BitConverter.ToInt32(BitConverter.GetBytes(b), 0);
        }
    }
}
