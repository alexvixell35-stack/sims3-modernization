namespace Ts3Fixes
{
    // Game-independent part of JobYield's step-aside (RouteBackoff.cs StepAside, docs/183 §7), checked offline by
    // tools/selftest/stepaside. One value per sleep: the caller runs Begin, then per tick AfterSleep and (when that
    // answered true) Answer with ts3native's JOBBUSY answer; FinalOp is what it sends once the loop is left, whichever way.
    // ts3native holds the route's records (refusal reason, scope, marks) while the task sleeps: a "not busy" answer puts
    // them back by itself, the cap needs kOpEnd, and a sleep left by an exception (the Sim was reset) needs kOpDrop,
    // because no plan follows. So every sleep ends with exactly one of the three.
    public struct StepAsideLoop
    {
        public const int kOver = 0, kCap = 1, kReset = 2;   // how the sleep ended; kReset until the loop says otherwise
        public const int kOpNone = 0, kOpEnd = 1, kOpDrop = 2;  // JOBBUSY's low word for the message after the loop

        public int Ticks, End;
        int mT0, mCapMs;
        bool mDone;

        public void Begin(int nowMs, int capMs) { Ticks = 0; End = kReset; mT0 = nowMs; mCapMs = capMs; mDone = false; }

        // after each Sleep: false = the real-time cap is reached (the plan waits natively for the job, as before JobYield)
        public bool AfterSleep(int nowMs)
        {
            Ticks++;
            if (unchecked(nowMs - mT0) < mCapMs) return true;
            End = kCap; mDone = true;
            return false;
        }

        // ts3native's answer to the poll after a sleep: true = still busy, sleep again
        public bool Answer(bool busy)
        {
            if (busy) return true;
            End = kOver; mDone = true;
            return false;
        }

        public int FinalOp() { return End == kOver && mDone ? kOpNone : End == kCap ? kOpEnd : kOpDrop; }
    }

    public static class StepAsideCore
    {
        // A plan that the async path keeps synchronous for the game's own order must not sleep here either: a pending
        // social jig (asyncjigsync, docs/240: the partner must not run between CreateObjectOutOfWorld and AddToWorld),
        // held by the planning task's Sim or by the route's Sim.
        public static bool KeepSync(int jigOfTaskSim, int jigOfRouteSim) { return jigOfTaskSim != 0 || jigOfRouteSim != 0; }

        static readonly string[] kEnd = { "job over", "cap", "reset" };
        public static string EndName(int end) { return end >= 0 && end < kEnd.Length ? kEnd[end] : "?"; }
    }
}
