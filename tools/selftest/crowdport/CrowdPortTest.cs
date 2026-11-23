using System;
using Ts3Fixes;

// Offline checks of CrowdPortCore (crowdport / crowdporthh, docs/686 §19).
static class CrowdPortTest
{
    static int sFail, sRun;

    static void Check(bool ok, string what)
    {
        sRun++;
        if (!ok) { sFail++; Console.WriteLine("FAIL " + what); }
    }

    static int Main()
    {
        const int Go = CrowdPortCore.kGo, None = CrowdPortCore.kNone, Would = CrowdPortCore.kWould, Exempt = CrowdPortCore.kExempt, Keep = CrowdPortCore.kKeep, Wait = CrowdPortCore.kWait;
        const int N = 3;
        const float W = 1f;

        // the tunables' bounds
        Check(CrowdPortCore.Min(3) == 3 && CrowdPortCore.Min(8) == 8, "the tuned crowd size");
        Check(CrowdPortCore.Min(1) == 2 && CrowdPortCore.Min(0) == 2 && CrowdPortCore.Min(-4) == 2, "a crowd is never one Sim");
        Check(CrowdPortCore.Wait(1f) == 1f && CrowdPortCore.Wait(0f) == 0f, "the tuned wait");
        Check(CrowdPortCore.Wait(-2f) == 0f && CrowdPortCore.Wait(60f) == CrowdPortCore.kWaitCeiling, "the wait stays within 0 and the game's 15");

        // crowd and trigger
        Check(!CrowdPortCore.Crowd(1, N) && !CrowdPortCore.Crowd(2, N) && CrowdPortCore.Crowd(3, N) && CrowdPortCore.Crowd(9, N), "three Sims are a crowd");
        Check(CrowdPortCore.Crowd(2, 1), "the floor of two applies to the crowd test");
        Check(!CrowdPortCore.Trigger(1, N, 99f, W), "a single Sim never triggers, however long it stands");
        Check(!CrowdPortCore.Trigger(2, N, 0.9f, W) && CrowdPortCore.Trigger(2, N, 1f, W), "behind one Sim: after the short wait");
        Check(CrowdPortCore.Trigger(3, N, 0f, W), "a crowd triggers at once");
        Check(CrowdPortCore.Trigger(2, N, 0f, 0f), "wait 0: behind one Sim at once");
        Check(!CrowdPortCore.WouldLate(false, true, 0.9f, W) && CrowdPortCore.WouldLate(false, true, 2.2f, W), "off: a wait behind one Sim counts as would once it lasted the short wait");
        Check(!CrowdPortCore.WouldLate(true, true, 5f, W) && !CrowdPortCore.WouldLate(false, false, 5f, W), "off: not with the switch on, not when the first look counted or the Sim is out of the rule");

        // elevator
        Check(CrowdPortCore.Elevator(true, true, true, 3, N, 0f, W, true) == Go, "crowd, skip possible: the game's skip now");
        Check(CrowdPortCore.Elevator(true, true, true, 2, N, 1.2f, W, true) == Go, "long wait behind one Sim: skip");
        Check(CrowdPortCore.Elevator(true, true, true, 3, N, 0f, W, false) == Wait, "crowd, doors closed: the line with a short timeout");
        Check(CrowdPortCore.Elevator(true, true, true, 2, N, 0.3f, W, true) == Keep, "short wait so far: the line");
        Check(CrowdPortCore.Elevator(true, true, true, 1, N, 9f, W, true) == Keep, "a single Sim keeps the walk");
        Check(CrowdPortCore.Elevator(true, false, true, 3, N, 0f, W, true) == Exempt, "household without the hh switch");
        Check(CrowdPortCore.Elevator(true, false, true, 1, N, 0f, W, true) == None, "out of scope, no crowd: nothing to count");
        Check(CrowdPortCore.Elevator(true, true, false, 9, N, 9f, W, true) == None, "horse, rider, carrier: the game's line");
        Check(CrowdPortCore.Elevator(false, true, true, 3, N, 0f, W, true) == Would, "off: would");
        Check(CrowdPortCore.Elevator(false, true, true, 2, N, 0f, W, true) == None, "off, no crowd: nothing");
        Check(CrowdPortCore.ElevatorTimeout(15f, Wait, 0f, W) == 1f, "no skip now: the timeout is the short wait");
        Check(CrowdPortCore.ElevatorTimeout(15f, Keep, 0.25f, W) == 0.75f, "short wait so far: what is left of it");
        Check(CrowdPortCore.ElevatorTimeout(15f, Keep, 0.95f, W) == CrowdPortCore.kLineFloor, "never under the floor");
        Check(CrowdPortCore.ElevatorTimeout(0.1f, Wait, 0f, W) == 0.1f, "never longer than the game's");
        Check(CrowdPortCore.ElevatorTimeout(15f, None, 0f, W) == 15f && CrowdPortCore.ElevatorTimeout(15f, Would, 0f, W) == 15f
            && CrowdPortCore.ElevatorTimeout(15f, Exempt, 0f, W) == 15f && CrowdPortCore.ElevatorTimeout(15f, Go, 0f, W) == 15f, "other answers: the game's timeout");
        Check(CrowdPortCore.SameEpisode(true, 0.25f) && CrowdPortCore.SameEpisode(true, CrowdPortCore.kEpisodeGap), "asked again soon: the wait goes on");
        Check(!CrowdPortCore.SameEpisode(false, 0.25f) && !CrowdPortCore.SameEpisode(true, 2.5f) && !CrowdPortCore.SameEpisode(true, -1f), "other doors, a pause, a clock that went back: a new wait");

        // rabbit hole, going in
        Check(CrowdPortCore.RhIn(true, true, true, true, true, true, true, 3, N, 0f, W) == Go, "third Sim at a held slot: set on it");
        Check(CrowdPortCore.RhIn(true, true, true, true, true, true, true, 2, N, 0f, W) == Wait, "second Sim: our short wait");
        Check(CrowdPortCore.RhIn(true, true, true, true, true, true, true, 2, N, 1f, W) == Go, "second Sim after the short wait");
        Check(CrowdPortCore.RhIn(true, false, true, true, true, true, true, 5, N, 5f, W) == None, "a free slot: the game's code");
        Check(CrowdPortCore.RhIn(true, true, true, false, true, true, true, 5, N, 5f, W) == None, "horse, rider, carrier, toddler: the game's wait");
        Check(CrowdPortCore.RhIn(true, true, false, true, true, true, true, 3, N, 0f, W) == Exempt, "household without the hh switch");
        Check(CrowdPortCore.RhIn(true, true, true, true, false, false, true, 3, N, 0f, W) == None, "dead reservation: left to rhshare or the game");
        Check(CrowdPortCore.RhIn(true, true, true, true, true, false, true, 3, N, 0f, W) == Keep, "holder out of scope: the game's wait");
        Check(CrowdPortCore.RhIn(true, true, true, true, true, true, false, 3, N, 0f, W) == Keep, "far from the slot: the game's wait");
        Check(CrowdPortCore.RhClear(8f, -1f) && CrowdPortCore.RhClear(CrowdPortCore.kRhHolderClear, 20f), "holder still far from its slot, Sim not set lately: clear");
        Check(!CrowdPortCore.RhClear(2f, -1f) && !CrowdPortCore.RhClear(0f, -1f), "holder at the door (cp-r1, cp-r3: 2 m): not set on the slot");
        Check(!CrowdPortCore.RhClear(8f, 1.2f) && !CrowdPortCore.RhClear(8f, 14.9f), "a Sim set on a slot comes back after a failed route: not set again");
        Check(CrowdPortCore.RhIn(false, true, true, true, true, true, true, 3, N, 0f, W) == Would, "off: would");
        Check(CrowdPortCore.RhIn(false, true, true, true, true, true, true, 2, N, 0f, W) == None, "off, no crowd: nothing");

        // rabbit hole, coming out
        Check(CrowdPortCore.RhOut(true, false, true, true) == Go, "an NPC's exit wait is ours");
        Check(CrowdPortCore.RhOut(true, true, true, true) == None, "the game lets this Sim out at once");
        Check(CrowdPortCore.RhOut(true, false, true, false) == None, "with a horse: the game's wait");
        Check(CrowdPortCore.RhOut(true, false, false, true) == Exempt && CrowdPortCore.RhOut(false, false, true, true) == Would, "scope, off");
        Check(!CrowdPortCore.Burst(1, N) && !CrowdPortCore.Burst(2, N) && CrowdPortCore.Burst(3, N), "three waiting: a burst");
        Check(CrowdPortCore.Released(300, 100) && CrowdPortCore.Released(300, 300), "Sims waiting at the stamp go");
        Check(!CrowdPortCore.Released(300, 301) && !CrowdPortCore.Released(0, 0), "a Sim that came later waits; no stamp, no burst");

        // three NPCs come to the exit at ticks 100, 200, 300: all leave on the third's first look
        long[] start = { 100, 200, 300 };
        long stamp = 0;
        int pending = 0, left = 0;
        for (int i = 0; i < 3; i++)
        {
            pending++;
            if (CrowdPortCore.Burst(pending, N)) stamp = start[i];
        }
        for (int i = 0; i < 3; i++) if (CrowdPortCore.Released(stamp, start[i])) { left++; pending--; }
        Check(left == 3 && pending == 0, "the burst lets all three out");

        Console.WriteLine(sFail == 0 ? "crowdport selftest: " + sRun + " checks ok" : "crowdport selftest: " + sFail + " of " + sRun + " FAILED");
        return sFail == 0 ? 0 : 1;
    }
}
