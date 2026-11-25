namespace Ts3Fixes
{
    // Game-independent decisions of aptgasleak / aptpossess (AptJobTargets.cs, docs/863), kept free of game types so the
    // offline self-test (tools/selftest/aptjobtargets) compiles it alone.
    public static class AptCore
    {
        // Drop this stove's leak and one GasLeak task? Only a leak inside a hidden (or fogged) room of a lot with virtual
        // residential slots (EA's own hidden test in FindIsolatedFirePosition), while the job still waits for a gas leak.
        public static bool GasDrop(bool on, bool virtualResidential, bool leaking, bool hidden, bool taskPending)
        {
            return on && virtualResidential && leaking && hidden && taskPending;
        }

        // Keep EA's spirit hop target? Everything except a hidden (or fogged) room of such a lot, as firetrapcount's spot rule.
        public static bool KeepHop(bool on, bool virtualResidential, bool hidden)
        {
            return !(on && virtualResidential && hidden);
        }

        // A re-pick candidate: EA's own rule in GhostHunterJob.GetPossessableObjectInRange (same level as the possessed
        // object, within kPossessedObjectRange), outside hidden rooms.
        public static bool HopCandidate(bool sameLevel, float distance, float range, bool hidden)
        {
            return sameLevel && distance <= range && !hidden;
        }
    }
}
