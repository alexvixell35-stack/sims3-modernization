namespace Ts3Fixes
{
    // Game-independent rules of lineswitch (LineSwitch.cs, docs/686 §25). Offline self-check: tools/selftest/lineswitch.
    public static class LineSwitchCore
    {
        public const int kGo = 0, kWould = 1, kKeep = 2;
        public const int kWhyNone = 0, kWhyNotHuman = 1, kWhyPlayer = 2, kWhyNoFree = 3, kWhyExempt = 4, kWhyRefused = 5;
        public const float kMinMinutes = 0.5f, kMaxMinutes = 15f;

        // The tunable wait before a switch, kept in a sane range (sim-minutes).
        public static float After(float tuned)
        {
            if (!(tuned >= kMinMinutes)) return kMinMinutes;  // NaN too
            return tuned > kMaxMinutes ? kMaxMinutes : tuned;
        }

        // Has this Sim waited long enough in the line?
        public static bool Due(float waited, float tuned) { return waited >= After(tuned); }

        // `human`: a human Sim, not riding. `autonomous`: the Sim picked the action itself (a player's order keeps its
        // object). `free`: a free, unclaimed, reachable object of the same kind is on the lot. `inScope`: an NPC, or a
        // household Sim with lineswitchhh on. Off = compare mode: would.
        public static int Decide(bool on, bool human, bool autonomous, bool free, bool inScope, out int why)
        {
            why = kWhyNone;
            if (!human) { why = kWhyNotHuman; return kKeep; }
            if (!autonomous) { why = kWhyPlayer; return kKeep; }
            if (!free) { why = kWhyNoFree; return kKeep; }
            if (!on) return kWould;
            if (!inScope) { why = kWhyExempt; return kKeep; }
            return kGo;
        }

        // Lower is better: the same room first, then the same level, then the shortest distance (m).
        public static float Rank(bool sameRoom, bool sameLevel, float dist)
        {
            return (sameRoom ? 0f : 1000f) + (sameLevel ? 0f : 500f) + (dist < 0f ? 0f : dist);
        }
    }
}
