using System.Collections.Generic;

namespace Ts3Fixes
{
    // Game-independent part of simbotworld (SimbotWorld.cs, docs/870), kept free of game types so the offline self-test
    // (tools/selftest/simbotworld) compiles it alone. T is WorldType in game, TN WorldName.
    public static class SbwCore
    {
        // EA's shipped MakeFrankensim world line: RestrictionType Allow, Types exactly {Base}, Names empty (namesCount -1 = null).
        // Any other shape is another mod's (e.g. the ModTheSims ITUN override) and is left alone.
        public static bool EaShape<T>(bool allow, List<T> types, T baseType, int namesCount)
        {
            return allow && types != null && types.Count == 1 && EqualityComparer<T>.Default.Equals(types[0], baseType) && namesCount == 0;
        }

        // Adds each of add not in types yet; returns what was added (empty when nothing).
        public static List<T> Widen<T>(List<T> types, T[] add)
        {
            List<T> added = new List<T>();
            foreach (T t in add)
                if (!types.Contains(t)) { types.Add(t); added.Add(t); }
            return added;
        }

        // Removes exactly what Widen added (one entry each, if still there); returns how many went.
        public static int Unwiden<T>(List<T> types, List<T> added)
        {
            int n = 0;
            foreach (T t in added)
                if (types.Remove(t)) n++;
            return n;
        }

        // EA's world test in Autonomy.CheckAvailability: true = the interaction is refused here (Tuning_World). A null list
        // counts as empty here; EA would throw on it.
        public static bool Blocks<T, TN>(bool allow, bool disallow, List<T> types, List<TN> names, T type, TN name)
        {
            bool has = (types != null && types.Contains(type)) || (names != null && names.Contains(name));
            return allow ? !has : disallow && has;
        }
    }
}
