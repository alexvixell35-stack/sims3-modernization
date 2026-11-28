namespace Ts3Fixes
{
    // Rules the household fixes share (docs/686 §41), checked offline by tools/selftest/hhshared. One answer each for:
    //   the grace     how long a Sim stands in a wait before a relaxation (walk through a Sim, clip to a closed seat, pull a
    //                 chair out past a neighbour) may act: 2.5 sim-min, 2.5 s at normal speed (the player's "2 or 3 second
    //                 wait"). The tunable kClipGraceMinutes is the one value; fixes that count real time use GraceMs.
    //   the scope     who a relaxation covers: the base switch = Sims the player does not control, the hh switch = household
    //                 and selected Sims, each alone; with both off every case counts as "would".
    //   the order     an action the Sim did not pick itself (the player's click, a script's push) is never moved to another
    //                 object or another Sim's turn.
    //   the snap      a Sim moved without a walk is faded out and in when the Sim, the place it leaves or the place it
    //                 lands on can be seen.
    public static class HhSharedCore
    {
        public const float kGraceMin = 2.5f;

        public static float GraceMin(float tunable) { return tunable >= 0f ? tunable : 0f; }  // NaN and negatives: no grace (as ClipWaitCore.Due)

        // real ms of the grace at normal speed (1 sim-min = 1 s)
        public static int GraceMs(float graceMin) { return (int)(GraceMin(graceMin) * 1000f + 0.5f); }

        public const int kNone = 0, kWould = 1, kExempt = 2, kGo = 3;

        public static int Scope(bool kind, bool human, bool player, bool on, bool hhOn)
        {
            if (!kind) return kNone;
            if (!human) return kExempt;
            if (!on && !hhOn) return kWould;
            if (player) return hhOn ? kGo : kExempt;
            return on ? kGo : kWould;
        }

        // directed: not the Sim's own pick. playerOrder: directed, for a Sim the player controls.
        public static bool Directed(bool hasAction, bool autonomous) { return hasAction && !autonomous; }
        public static bool PlayerOrder(bool hasAction, bool autonomous, bool playerSim) { return Directed(hasAction, autonomous) && playerSim; }

        public static bool SnapFade(bool simOnScreen, bool fromNearCamera, bool toNearCamera) { return simOnScreen || fromNearCamera || toNearCamera; }
    }
}
