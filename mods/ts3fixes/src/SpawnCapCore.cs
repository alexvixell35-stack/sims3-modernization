namespace Ts3Fixes
{
    // Game-independent rule of spawncapcount (docs/147 §5 D1), selftest tools/selftest/spawncap.
    // EA's cap checks (MinorPet.CanSpawnMoreMinorPetsInWorld, Wildflower.CanSpawnWildflowers) walk every object of the type
    // and return false once the uncaptured / uncollected ones reach the cap (num >= cap after each increment). The
    // uncaptured ones are a subset of all objects, so when the native total is below the cap the walk can never return
    // false: the answer is the guards' answer alone. At or over the cap the walk must run.
    public static class SpawnCapCore
    {
        public static bool UnderCap(uint total, int cap) { return (long)total < cap; }

        // EA's walk, written out for the selftest: flags[i] = object i counts (not captured / not collected).
        public static bool EaWalk(bool[] flags, int cap)
        {
            int num = 0;
            for (int i = 0; i < flags.Length; i++)
                if (flags[i] && ++num >= cap) return false;
            return true;
        }
    }
}
