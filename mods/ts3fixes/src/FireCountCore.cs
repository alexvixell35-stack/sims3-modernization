namespace Ts3Fixes
{
    // Game-independent decisions of firecount (FireCount.cs, docs/868), kept free of game types so the offline self-test
    // (tools/selftest/firecount) compiles it alone.
    public static class FcCore
    {
        // What a rescue task completion (TrappedSims / TrappedRubble reaching FireFightingJob.OnTaskComplete) does.
        public const int Ea = 0, Count = 1, Skip = 2;

        // mapped: the freed Sim (TrappedSims) or the dug pile (TrappedRubble) is known for this completion; listed: that Sim
        //   (the pile's Sim) is in the job's list for this task; accounted: its task was already completed or dropped in
        //   this job (by firecount, or by firetrapcount's drop for a Sim that left the lot).
        // Ea: not a rescue we can place (firetrapcount / fireleftover drops, a game loaded mid-job without the list): EA's.
        // Count: a listed Sim's first rescue: EA's, and the Sim is accounted from now on. Skip: a Sim the job never
        // trapped (a passer-by), or one already accounted (re-trapped after its rescue, a second rubble pile): no task.
        public static int Rescue(bool mapped, bool listed, bool accounted)
        {
            if (!mapped) return Ea;
            return listed && !accounted ? Count : Skip;
        }

        // A listed Sim died (FireFightingJob.SimDied). EA takes one pending task of the list whatever the Sim's state and
        // never completes the job. The fix takes one, through EA's OnTaskComplete, only for a Sim not accounted yet: still
        // trapped, or freed by a rescue that never reached the job (another firefighter's: the occupation's listener
        // ignores any Sim but its owner), so its task is still pending. A Sim whose rescue counted (re-trapped or not)
        // takes none. After a load nobody is accounted, so a death takes one, as EA's does.
        public static bool DeathDrops(bool accounted)
        {
            return !accounted;
        }

        // Pending tasks of the list after a rescue completion: EA (on = false) takes one for every completion.
        public static int AfterRescue(bool on, int pending, int decision)
        {
            if (pending <= 0) return 0;
            return on && decision == Skip ? pending : pending - 1;
        }

        // Pending tasks of the list after a listed Sim's death, and whether the job completes now (its last pending task
        // went through OnTaskComplete). EA: one fewer, never completes (the job then waits for its timeout).
        public static int AfterDeath(bool on, int pending, bool accounted, out bool completes)
        {
            completes = false;
            if (pending <= 0) return 0;
            if (!on) return pending - 1;
            if (!DeathDrops(accounted)) return pending;
            completes = pending == 1;
            return pending - 1;
        }
    }
}
