namespace Ts3Fixes
{
    // Game-independent decisions of fireleftover (FireLeftover.cs, docs/862), kept free of game types so the offline
    // self-test (tools/selftest/fireleftover) compiles it alone.
    public static class FloCore
    {
        // Sim-minutes a leftover must be seen in a row before the fix acts: EA's own fire-task fallback waits 15 too
        // (StartFireCompleteAlarm). Much longer than EA's own gap between a victim going free and the task completing (a dig
        // destroys the rubble pile during its Exit animation, before SimCompletedTask), so a late EA completion cannot take
        // another victim's task.
        public const int kConfirmTicks = 15;

        // Ticks in a row the same positive excess has now been seen; 0 when there is none.
        public static int Seen(int excess, int lastExcess, int lastTicks)
        {
            return excess <= 0 ? 0 : excess == lastExcess ? lastTicks + 1 : 1;
        }

        // Rescue tasks to drop: the excess of pending tasks over the job's Sims still trapped on its lot, once seen for
        // kConfirmTicks in a row. 0 when the switch is off.
        public static int Drop(bool on, int excess, int ticks)
        {
            return on && excess > 0 && ticks >= kConfirmTicks ? excess : 0;
        }

        // The window-fire task is done once no spreading window fire is left (EA's own test at the end of an extinguish
        // run) for kConfirmTicks in a row.
        public static bool WindowDone(bool on, bool pending, bool majorWindowFires, int ticksClear)
        {
            return on && pending && !majorWindowFires && ticksClear >= kConfirmTicks;
        }
    }
}
