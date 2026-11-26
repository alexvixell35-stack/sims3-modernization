namespace Ts3Fixes
{
    // Game-independent stand-down rule of envlistmemoexact (EnvListExact.cs, docs/274 §11.8). Self-check: tools/selftest/envlistexact.
    public static class EnvListExactCore
    {
        public const string Ours = "ts3fixes", Ea = "Sims3GameplaySystems";

        // Script assemblies known to replace the env scan or a lot's object cache table (docs/274 §11.8 survey).
        public static readonly string[] ConflictMods = { };

        // "<mod> (<what>)" when the exact path must stand down for the session, else null.
        //   conflicts:   ConflictMods (a parameter so the self-check can name its own)
        //   loaded:      names of the loaded assemblies (null = not checked this call)
        //   preOwner:    assembly of the class that owned SimUpdate.CalculateSimEnvironmentScore's header just before our
        //                swap, null when it was EA's own
        //   nowOwner:    the same now, null when the header is our own swap (or EA's)
        //   cacheOwners: assembly of each cache object in the lot's cache table (null = not checked this call)
        public static string Why(string[] conflicts, string[] loaded, string preOwner, string nowOwner, string[] cacheOwners)
        {
            if (loaded != null)
                foreach (string a in loaded)
                    foreach (string k in conflicts)
                        if (a == k) return a + " (known to replace the env scan or the lot object cache)";
            if (preOwner != null) return preOwner + " (replaced SimUpdate.CalculateSimEnvironmentScore before ts3fixes)";
            if (nowOwner != null) return nowOwner + " (replaced SimUpdate.CalculateSimEnvironmentScore after ts3fixes)";
            if (cacheOwners != null)
                foreach (string a in cacheOwners)
                    if (a != Ea && a != Ours) return a + " (keeps its own cache in a lot's object cache table)";
            return null;
        }
    }
}
