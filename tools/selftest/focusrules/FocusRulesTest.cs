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
        // bp-gu2-arrive-off 2026-11-08: no direct pick tagged (autunseen off) = the game's own queue, not judged; tagged = judged
        if (FocusRules.SnapQueue(1, 0) != 0 || FocusRules.SnapQueue(2, 3) != 2) { sFails++; Console.WriteLine("FAIL SnapQueue gate"); }
        // bp-gu2-arrive-on 2026-11-08: only spawn_sims crowd Sims count as arrivals, passers-by do not
        Dictionary<ulong, bool> crowd = new Dictionary<ulong, bool> { { 42, true } };
        if (!FocusRules.CrowdArrival(42, crowd) || FocusRules.CrowdArrival(7, crowd) || FocusRules.CrowdArrival(0, crowd)) { sFails++; Console.WriteLine("FAIL CrowdArrival"); }
        // ip-gu2-arrive-off 2026-11-08: Atlantis houseboat docked in Picturesque Port, camera on the boat (3 m) = on the home lot
        if (!FocusRules.CameraOnLot(true, false, 999) || !FocusRules.CameraOnLot(false, true, 3) || FocusRules.CameraOnLot(false, true, 40) || FocusRules.CameraOnLot(false, false, 3)) { sFails++; Console.WriteLine("FAIL CameraOnLot"); }
        FocusRules.Sample bn =S(3.2, bad: 1); bn.BadNames = "Sit";
        Check("bad pick named", "fail a live Test (Sit)", S(0), bn);
        Check("pop on screen", "pop", S(0), S(1), S(2, 30), S(4, 30));
        // docs/222 §11.10: the pop names both points, their seconds, the interaction and rabbit-hole posture on each side
        FocusRules.Sample pa = S(2, 0), pb = S(2.5, 15.2); pa.Interaction = "Work Until End of Shift";
        Check("pop names both points", "(0.0,0.0 @2.00 s -> 15.2,0.0 @2.50 s; interaction Work Until End of Shift -> none; in_rh False -> False)", S(0), S(1), pa, pb, S(4, 15.2));
        // docs/222 §11.10: EA rabbit-hole entry (end in_rh) or exit (start in_rh, end not) is rh_snap, not a pop; a plain 15 m step still pops
        FocusRules.Sample ea = S(2, 0), eb = S(2.5, 15.2); eb.InRabbitHole = true;
        Check("rabbit hole entry is no pop", null, S(0), S(1), ea, eb, S(4, 15.2));
        FocusRules.Sample xa = S(2, 0), xb = S(2.5, 15.2); xa.InRabbitHole = true;
        Check("rabbit hole exit is no pop", null, S(0), S(1), xa, xb, S(4, 15.2));
        FocusRules.Sample na = S(2, 0), nb = S(2.5, 15.2);
        Check("normal 15 m step still pops", "pop", S(0), S(1), na, nb, S(4, 15.2));
        FocusRules.Stats rs = new FocusRules.Stats();
        FocusRules.Judge(new List<FocusRules.Sample> { S(0), S(1), ea, eb, S(4, 15.2) }, new FocusRules.Limits(), rs);
        if (rs.RhSnaps != 1 || rs.RhNote == null) { sFails++; Console.WriteLine("FAIL rh_snap not counted"); }
        Check("jump before seen is fine", null, S(0, on: false), S(1, 50, on: false), S(2, 50), S(6, 50));
        Check("rabbit hole exit is no pop", null, S(0, hidden: true), S(1, 40), S(4, 41));
        Check("stuck routing", "stuck", S(0, route: true), S(30, route: true), S(61, route: true));
        Check("routing and moving", null, S(0, route: true), S(30, 3, route: true), S(61, 6, route: true));
        Check("idle too long", "idle", S(0, act: false, simMin: 0), S(4, act: false, simMin: 100), S(5, act: false, simMin: 241));
        Check("idle under the limit", null, S(0, act: false, simMin: 0), S(4, act: false, simMin: 239));
        Check("needs starving", "starving", S(0), S(4, motive: -96));
        // distinct pick (ip-gu2-focus 11-07: the same Sim twice, a gone tag)
        List<ulong> ring = new List<ulong>();
        foreach (ulong id in new ulong[] { 5, 7, 5, 9, 0, 11 }) FocusRules.RingAdd(ring, id, 3);
        if (string.Join(",", ring.ConvertAll(x => x.ToString()).ToArray()) != "5,9,11") { sFails++; Console.WriteLine("FAIL ring order/cap/no repeat"); }
        Dictionary<ulong, bool> judged = new Dictionary<ulong, bool> { { 11, true } };
        if (FocusRules.PickNewest(ring, judged, id => true) != 9) { sFails++; Console.WriteLine("FAIL judged Sim picked again"); }
        if (FocusRules.PickNewest(ring, judged, id => id != 9) != 5) { sFails++; Console.WriteLine("FAIL gone Sim not skipped"); }
        if (FocusRules.PickNewest(ring, judged, id => false) != 0) { sFails++; Console.WriteLine("FAIL none left"); }
        Console.WriteLine(sFails == 0 ? "focusrules: PASS" : "focusrules: FAIL");
        return sFails == 0 ? 0 : 1;
    }
}
