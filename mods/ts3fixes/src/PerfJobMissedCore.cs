namespace Ts3Fixes
{
    // Game-independent rule of perfjobmissedlistener (PerfJobMissedListener.cs, docs/654 §1). Self-check:
    // tools/selftest/perfjobmissedlistener.
    // A PerformanceCareer.OnJobMissed listener (kOccupationJobMissed, no owner key) is stale once its career is no longer
    // its owner's current occupation: the owner's description is gone (disposed, CareerManager null) or the owner left the
    // career (LeaveJobNow's AssignOccupation(null), or another occupation took its place). A career that is still the
    // owner's occupation keeps its listener, whatever else happens.
    public static class PerfJobMissedCore
    {
        public const int kKeep = 0, kRemove = 1, kSeen = 2;

        // ownerValid: the career's OwnerDescription is a valid description with a CareerManager.
        // current: that CareerManager's Occupation is this career.
        public static bool Stale(bool ownerValid, bool current)
        {
            return !ownerValid || !current;
        }

        // on: the switch; a stale listener with the switch off is only counted
        public static int Decide(bool on, bool ownerValid, bool current)
        {
            if (!Stale(ownerValid, current)) return kKeep;
            return on ? kRemove : kSeen;
        }
    }
}
