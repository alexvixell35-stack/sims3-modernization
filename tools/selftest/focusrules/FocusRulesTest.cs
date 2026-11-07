using System;
using System.Collections.Generic;
using Ts3Agent;

static class FocusRulesTest
{
    static int sFails;
    static FocusRules.Sample S(double sec, double x = 0, bool on = true, bool act = true, bool route = false, bool held = false, int bad = 0, double simMin = -1, double motive = 50, bool hidden = false)
    {
        return new FocusRules.Sample { Sec = sec, SimMin = simMin < 0 ? sec : simMin, X = x, OnScreen = on, HasAction = act, Routing = route, Held = held, BadQueue = bad, MinMotive = motive, Hidden = hidden };
    }
    static void Check(string name, string want, params FocusRules.Sample[] s)
    {
        string got = FocusRules.Judge(new List<FocusRules.Sample>(s), new FocusRules.Limits(), new FocusRules.Stats());
        bool ok = want == null ? got == null : got != null && got.Contains(want);
        if (!ok) { sFails++; Console.WriteLine("FAIL " + name + ": got " + (got ?? "null")); }
    }
    static int Main()
    {
        Check("plain walk", null, S(0), S(1, 2), S(2, 4), S(4, 6));
        Check("never seen", "never on screen", S(0, on: false), S(5, on: false));
        Check("too short to prove snap-back", "no sample", S(0), S(1));
        Check("snap back in time", null, S(0, held: true, bad: 1), S(1, held: true), S(2.5), S(4));
        Check("hold never ends", "relaxed hold", S(0, held: true), S(3.5, held: true));
        Check("bad pick not cancelled", "fail a live Test", S(0, bad: 1), S(3.2, bad: 2));
        Check("pop on screen", "pop", S(0), S(1), S(2, 30), S(4, 30));
        Check("jump before seen is fine", null, S(0, on: false), S(1, 50, on: false), S(2, 50), S(6, 50));
        Check("rabbit hole exit is no pop", null, S(0, hidden: true), S(1, 40), S(4, 41));
        Check("stuck routing", "stuck", S(0, route: true), S(30, route: true), S(61, route: true));
        Check("routing and moving", null, S(0, route: true), S(30, 3, route: true), S(61, 6, route: true));
        Check("idle too long", "idle", S(0, act: false, simMin: 0), S(4, act: false, simMin: 100), S(5, act: false, simMin: 241));
        Check("idle under the limit", null, S(0, act: false, simMin: 0), S(4, act: false, simMin: 239));
        Check("needs starving", "starving", S(0), S(4, motive: -96));
        Console.WriteLine(sFails == 0 ? "focusrules: PASS" : "focusrules: FAIL");
        return sFails == 0 ? 0 : 1;
    }
}
