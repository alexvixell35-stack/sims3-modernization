namespace Ts3Fixes
{
    // Game-independent rules of queuewake (QueueWake.cs, docs/686 §23). Offline self-check: tools/selftest/queuewake.
    public static class QueueWakeCore
    {
        public const int kNoMark = 0, kWake = 1, kStale = 2, kNotIdle = 3, kOwnTask = 4, kNpc = 5;
        public const uint kMinSleep = 3;  // shorter loop sleeps (2 ticks: autonomy about to run) are not worth a wake

        // Ticks the idle loop of Sim.DoInteraction is about to sleep after LoopIdle (EA: autonomy off 20, waiting in the
        // autonomy queue 10, else 2 after it adds the Sim to that queue); 0 = do not mark.
        public static uint Sleep(bool autonomyEnabled, bool inAutonomyQueue, uint disabledTicks, uint inQueueTicks, uint notInQueueTicks)
        {
            uint t = !autonomyEnabled ? disabledTicks : inAutonomyQueue ? inQueueTicks : notInQueueTicks;
            return t >= kMinSleep ? t : 0u;
        }

        // An entry was added to a Sim's queue. `marked`: the Sim's idle loop set a deadline; `now` / `deadline`: simulator
        // ticks. `onlyEntry`: the queue holds just this entry and nothing runs. `ownTask`: the add runs on the Sim's own task.
        public static int Decide(bool marked, long now, long deadline, bool onlyEntry, bool ownTask, bool playerSim)
        {
            if (!marked) return kNoMark;
            if (ownTask) return kOwnTask;
            if (now >= deadline) return kStale;  // the loop has woken by itself: it may be anywhere now
            if (!onlyEntry) return kNotIdle;
            if (!playerSim) return kNpc;
            return kWake;
        }
    }
}
