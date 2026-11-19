using System;
using Ts3Fixes;

// Offline self-check of StrayLeaveCore (docs/656 §4.2): clock start, buff / busy reset, send once, exemption, clock back, bound.
static class StrayLeaveTest
{
    static int sFails;
    static void Check(bool ok, string what) { if (!ok) { sFails++; if (sFails < 20) Console.WriteLine("FAIL " + what); } }

    static void Main()
    {
        StrayLeaveCore m = new StrayLeaveCore();
        const long hour = 100, limit = 3 * hour;

        Check(m.Step(1, false, true, false, 0, limit) == StrayLeaveCore.kClear && m.Count == 0, "buff on: nothing to do");
        Check(m.Step(1, false, false, true, 0, limit) == StrayLeaveCore.kClear && m.Count == 0, "no buff but leaving / busy: nothing");
        Check(m.Step(1, false, false, false, 0, limit) == StrayLeaveCore.kNew && m.Count == 1, "no buff, idle: clock starts");
        Check(m.Step(1, false, false, false, 2 * hour, limit) == StrayLeaveCore.kWait, "2 h: wait");
        Check(m.Step(1, false, false, false, limit, limit) == StrayLeaveCore.kSend && m.Count == 0, "3 h: EA's go-home, entry gone");
        Check(m.Step(1, false, false, false, limit + hour, limit) == StrayLeaveCore.kNew, "walk home failed: a whole new wait");

        // buff back (a new visit) or a trip resets the clock
        m.Step(2, false, false, false, 0, limit);
        Check(m.Step(2, false, true, false, hour, limit) == StrayLeaveCore.kClear && m.Step(2, false, false, false, 2 * hour, limit) == StrayLeaveCore.kNew, "buff in between: clock restarts");
        m.Step(3, false, false, false, 0, limit);
        Check(m.Step(3, false, false, true, hour, limit) == StrayLeaveCore.kClear && m.Step(3, false, false, false, 4 * hour, limit) == StrayLeaveCore.kNew, "busy in between: clock restarts");

        // exempt (household, selected, unknown scope): never sent, entry dropped
        m.Step(4, false, false, false, 0, limit);
        Check(m.Step(4, true, false, false, 10 * hour, limit) == StrayLeaveCore.kExempt, "turned exempt: never sent");
        Check(m.Step(4, false, false, false, 11 * hour, limit) == StrayLeaveCore.kNew, "exempt dropped the clock");

        // limit 0 = off; clock back starts over
        Check(m.Step(5, false, false, false, 0, 0) == StrayLeaveCore.kClear, "limit 0: nothing");
        m.Step(6, false, false, false, 1000, limit);
        Check(m.Step(6, false, false, false, 500, limit) == StrayLeaveCore.kNew, "clock went back: starts over");

        // bounded
        StrayLeaveCore b = new StrayLeaveCore();
        for (ulong i = 0; b.Count < StrayLeaveCore.kMax; i++) b.Step(100 + i, false, false, false, 0, limit);
        b.Step(9999, false, false, false, 0, limit);
        Check(b.Count == 1, "full table: cleared, new one kept");
        for (int d = 0; d <= 4; d++) Check(StrayLeaveCore.Name(d) != "?", "named " + d);

        Console.WriteLine(sFails == 0 ? "strayleave selftest: ok" : "strayleave selftest: " + sFails + " FAIL");
        Environment.Exit(sFails == 0 ? 0 : 1);
    }
}
