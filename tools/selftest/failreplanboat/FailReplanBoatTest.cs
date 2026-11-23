using System;
using Ts3Fixes;

static class FailReplanBoatTest
{
    static int sFail;
    static void Eq(int got, int want, string what)
    {
        if (got != want) { sFail++; Console.WriteLine("FAIL " + what + ": got " + got + ", want " + want); }
    }

    static int Main()
    {
        // the one case that acts, and the same with the switch off
        Eq(FailReplanBoatCore.Decide(false, true, true, true, true, true, true), FailReplanBoatCore.kOff, "unseen NPC boat, on");
        Eq(FailReplanBoatCore.Decide(false, true, true, true, true, true, false), FailReplanBoatCore.kWould, "unseen NPC boat, off");
        // exhaustive: only (failed, boat, driver, flag set, held unseen, npc only) may leave "kept"
        int acts = 0, would = 0;
        for (int m = 0; m < 128; m++)
        {
            bool ok = (m & 1) != 0, boat = (m & 2) != 0, driver = (m & 4) != 0, flag = (m & 8) != 0, held = (m & 16) != 0, npc = (m & 32) != 0, on = (m & 64) != 0;
            int v = FailReplanBoatCore.Decide(ok, boat, driver, flag, held, npc, on);
            bool rule = !ok && boat && driver && flag && held && npc;
            if (v == FailReplanBoatCore.kOff) acts++;
            if (v == FailReplanBoatCore.kWould) would++;
            Eq(v == FailReplanBoatCore.kOff ? 1 : 0, rule && on ? 1 : 0, "acts only by the rule (mask " + m + ")");
            Eq(v == FailReplanBoatCore.kWould ? 1 : 0, rule && !on ? 1 : 0, "would only by the rule (mask " + m + ")");
            if (ok || !boat) Eq(v, FailReplanBoatCore.kNotOurs, "succeeded or no boat (mask " + m + ")");
            else if (!driver) Eq(v, FailReplanBoatCore.kNoDriver, "driverless (mask " + m + ")");
            else if (!flag) Eq(v, FailReplanBoatCore.kFlagOff, "flag off already (mask " + m + ")");
            else if (!rule) Eq(v, FailReplanBoatCore.kExempt, "exempt (mask " + m + ")");
        }
        Eq(acts, 1, "exactly one acting input of 128");
        Eq(would, 1, "exactly one would-act input of 128");
        // a seen, household or unknown-scope driver (not held unseen) and a household or selectable occupant (not npc only) are kept
        Eq(FailReplanBoatCore.Decide(false, true, true, true, false, true, true), FailReplanBoatCore.kExempt, "seen or household driver kept");
        Eq(FailReplanBoatCore.Decide(false, true, true, true, true, false, true), FailReplanBoatCore.kExempt, "household aboard kept");
        Console.WriteLine(sFail == 0 ? "failreplanboat: ok (128 inputs)" : "failreplanboat: " + sFail + " failures");
        return sFail == 0 ? 0 : 1;
    }
}
