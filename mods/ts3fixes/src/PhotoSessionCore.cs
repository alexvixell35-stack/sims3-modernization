namespace Ts3Fixes
{
    // photosession (docs/508): which expected Sims a greeting-card photo session drops, with no game types, so
    // tools/selftest/photosession can run it offline.
    public static class PhotoSessionCore
    {
        // active[i]: Sim i is in the world and has an interaction (running or queued) on the session's owner.
        // young[i]: toddler or younger (a baby is carried by its adult and has no interaction of its own on the owner).
        // An older Sim that is not active is dropped. A young Sim that is not active stays while an active older Sim stays.
        // keepOne: drop nothing when every Sim would go (SimMadeItInside runs for a Sim inside, so an empty list there means
        // our view of the queues is wrong, and EA's own rule decides).
        public static bool[] Drop(bool[] active, bool[] young, bool keepOne)
        {
            int n = active.Length, left = 0;
            bool[] d = new bool[n];
            bool olderStays = false;
            for (int i = 0; i < n; i++) if (active[i] && !young[i]) olderStays = true;
            for (int i = 0; i < n; i++)
            {
                d[i] = !active[i] && (!young[i] || !olderStays);
                if (!d[i]) left++;
            }
            return keepOne && left == 0 ? new bool[n] : d;
        }
    }
}
