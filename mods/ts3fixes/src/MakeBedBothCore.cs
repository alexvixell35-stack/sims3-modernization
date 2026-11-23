namespace Ts3Fixes
{
    // Game-independent rules of MakeBedBoth.cs (makebedboth / makebedbothhh, docs/686 §13), checked offline by
    // tools/selftest/makebedboth.
    //
    // A bed has two parts (halves of a double bed, sides of a single bed). Make Bed walks to a routing slot of a part
    // that is unmade and free. When that slot cannot be reached the bed stays unmade. The rules say when the Sim
    // stands at the other part's slot instead and the bed plays the unmade half's animation.
    public static class MakeBedBothCore
    {
        public const int kNone = 0, kKeepEa = 1, kKeepNoSide = 2, kGo = 3;

        // parts: the bed's part count. plainParts: both are plain bed parts (no tent, igloo, pod, upper bunk).
        // unmadeFree: parts that are unmade with no Sim in them. otherUsable: the other part is made, with no Sim in it
        // and not in use. planUnmade / planOther: a route to the unmade part's slots / the other part's slots exists.
        //   kNone       not this case (nothing to make, both halves unmade: the game picks the one it can reach)
        //   kKeepEa     the unmade half can be reached: the game's walk
        //   kKeepNoSide no side to make it from
        //   kGo         made from the other part's slot
        public static int Decide(int parts, bool plainParts, int unmadeFree, bool otherUsable, bool planUnmade, bool planOther)
        {
            if (parts != 2 || !plainParts || unmadeFree != 1) return kNone;
            if (planUnmade) return kKeepEa;
            if (!otherUsable || !planOther) return kKeepNoSide;
            return kGo;
        }

        // The route plans are only made when they can change the answer.
        public static bool NeedPlans(int parts, bool plainParts, int unmadeFree) { return parts == 2 && plainParts && unmadeFree == 1; }

        // The run with the swapped flags made the bed: the game's run answered true and set the part the Sim stood at
        // made. Otherwise the flags go back.
        public static bool Made(bool runOk, bool standMade) { return runOk && standMade; }

        // The second half right after the same Sim made the first one on this bed (the game's continuation), or a
        // first make whose only unmade half is closed.
        public static bool Second(ulong lastSim, ulong lastBed, ulong sim, ulong bed) { return sim != 0 && lastSim == sim && lastBed == bed; }

        // The opposite-side parameter is set for the part the Sim stands at, and only while that Sim is in the part.
        public static bool Opposite(bool partArmed, bool simIsArmedSim) { return partArmed && simIsArmedSim; }
    }
}
