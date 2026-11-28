namespace Ts3Fixes
{
    // termlenguard (TermLenGuard.cs, docs/692 §2): the game-independent rule, checked offline by tools/selftest/termlenguard.
    public static class TermLenGuardCore
    {
        // EA's index is (uint)(value - 1) into a table of len entries: out of range for value 0 (kInvalid term length, no
        // courses) and for value > len (a 3- or 4-week term against the 2-entry table)
        public static bool IndexBad(uint value, int len) { return len < 0 || unchecked(value - 1u) >= (uint)len; }

        // the factor EA's guarded copy (AcademicCourse's class-tone multiplier) applies: the entry, or 1 when out of range;
        // the guard reaches the same value by pointing the index at entry 0 of a one-entry {1} table
        public static float Factor(float[] table, uint value)
        {
            if (table == null || IndexBad(value, table.Length)) return 1f;
            return table[value - 1u];
        }
    }
}
