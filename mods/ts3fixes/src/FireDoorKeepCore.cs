namespace Ts3Fixes
{
    // Game-independent decisions of firedoorkeep (FireDoorKeep.cs, docs/864), kept free of game types so the offline
    // self-test (tools/selftest/firedoorkeep) compiles it alone.
    // One door's lock state: CommonDoor.LockType, LockOwner, OneWayRoomDestinationId.
    public sealed class FdkState
    {
        public const int Nobody = 0, Anybody = 3, NoRoom = 36863;  // CommonDoor.tLock values; 36863 = no one-way room
        public readonly int Type, OneWay;
        public readonly object Owner;
        public FdkState(int type, object owner, int oneWay) { Type = type; Owner = owner; OneWay = oneWay; }
        public bool Same(FdkState o) { return o != null && Type == o.Type && ReferenceEquals(Owner, o.Owner) && OneWay == o.OneWay; }
        // The only states the job's own code leaves: SetLockType(Nobody / Anybody), which clears owner and one-way room.
        public bool Bare(int type) { return Type == type && Owner == null && OneWay == NoRoom; }
    }

    public static class FdkCore
    {
        // The state a door gets back after EA's FireFightingJob.CleanUpJob, or null to leave EA's outcome.
        //   pre: just before CleanUpJob; cur: just after it; before: before the job's first LockDoorsInRoom (null: no record,
        //   e.g. no trapped Sim or a game loaded mid-job); jobLocked: EA's LockDoorsInRoom changed this door in this job.
        public static FdkState Restore(FdkState pre, FdkState cur, FdkState before, bool jobLocked)
        {
            return Restore(pre, cur, before, jobLocked, false);
        }

        //   npcDoor: Door.IsNPCDoor (an apartment neighbour's door, locked bare Nobody by EA): never a job lock, since EA's
        //   job code skips doors that are already locked, so its lock stays even without a record.
        public static FdkState Restore(FdkState pre, FdkState cur, FdkState before, bool jobLocked, bool npcDoor)
        {
            if (!cur.Bare(FdkState.Anybody) && !cur.Bare(FdkState.Nobody)) return null;  // not a state EA's job leaves: someone else's
            if (pre.Type == FdkState.Anybody) return null;  // unlocked before the cleanup: EA does not touch it
            FdkState want;
            if (!pre.Bare(FdkState.Nobody)) want = pre;  // a lock the job never sets (player, resort, owner list, one-way): keep it
            else if (npcDoor && !jobLocked) want = pre;  // EA's apartment NPC door lock: keep it
            else if (before == null) return null;        // bare Nobody without a record: the job's lock or an older one, unknown
            else if (jobLocked) want = before;           // the job's own lock: back to the state before it (unlocked, one-way room)
            else if (before.Bare(FdkState.Nobody)) want = pre;  // bare Nobody before the job too (lot designer, NPC door): keep it
            else return null;                            // locked during the job by something else: EA's outcome
            return want.Same(cur) ? null : want;
        }
    }
}
