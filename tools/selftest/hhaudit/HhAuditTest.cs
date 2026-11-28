using System;
using Ts3Fixes;

// docs/289: sites without a household exemption. woohootywait and serviceshowalarm have no scope input at all (one answer
// for every Sim); exitcbsweep takes the scope and lifts it only for killed route actions.
static class HhAuditTest
{
    static int sFail;
    static void Check(bool ok, string what) { if (!ok) { sFail++; Console.WriteLine("FAIL " + what); } }

    static int Main()
    {
        // woohootywait: the cap and the gone exits end the wait for any Sim; under the cap it is EA's wait; off = EA
        int cap = HhAuditCore.kWwCap;
        Check(cap >= 400, "woohootywait: the ceiling stays long (>= 400 polls of 5 ticks)");
        Check(HhAuditCore.WwPoll(true, cap, false, false, true) == 2, "woohootywait: busy texter at the cap gives up");
        Check(HhAuditCore.WwPoll(true, cap - 1, false, false, true) == 1, "woohootywait: busy texter under the cap waits as EA");
        Check(HhAuditCore.WwPoll(true, 0, true, false, true) == 2 && HhAuditCore.WwPoll(true, 0, false, true, true) == 2, "woohootywait: gone Sim ends the wait");
        Check(HhAuditCore.WwPoll(true, 3, false, false, false) == 0 && HhAuditCore.WwPoll(true, cap + 9, false, false, false) == 0, "woohootywait: free texter goes on as EA");
        Check(HhAuditCore.WwPoll(false, cap * 10, false, false, true) == 1 && HhAuditCore.WwPoll(false, 0, true, true, true) == 1, "woohootywait: off waits for ever as EA");

        // serviceshowalarm: pending alarm + live worker that CleanUp will not destroy = shown, whatever the household
        Check(HhAuditCore.SsShowAtCleanUp(true, true, false, true, false, false), "serviceshowalarm: live worker of a non-service household is shown");
        Check(HhAuditCore.SsShowAtCleanUp(true, true, true, true, false, false), "serviceshowalarm: destroy flag, non-service household: not destroyed, shown");
        Check(HhAuditCore.SsShowAtCleanUp(true, true, true, true, true, true), "serviceshowalarm: service household with a virtual home is shown");
        Check(!HhAuditCore.SsShowAtCleanUp(true, true, true, true, true, false), "serviceshowalarm: a worker CleanUp destroys is left alone");
        Check(!HhAuditCore.SsShowAtCleanUp(true, false, false, true, false, false), "serviceshowalarm: no pending alarm = EA");
        Check(!HhAuditCore.SsShowAtCleanUp(true, true, false, false, false, false), "serviceshowalarm: destroyed worker = EA");
        Check(!HhAuditCore.SsShowAtCleanUp(false, true, false, true, false, false), "serviceshowalarm: off = EA");

        // exitcbsweep: live route's own callback stays for all; killed action's callback goes for all; replaced adaptor on a
        // live action goes for townies only
        foreach (bool player in new[] { false, true })
        {
            Check(!HhAuditCore.XcbRemove(true, 0, player), "exitcbsweep: live route's own callback stays, player " + player);
            for (int kill = 1; kill <= 6; kill++)
            {
                Check(HhAuditCore.XcbRemove(true, kill, player), "exitcbsweep: killed action, current adaptor, removed, player " + player);
                Check(HhAuditCore.XcbRemove(false, kill, player), "exitcbsweep: killed action, replaced adaptor, removed, player " + player);
            }
        }
        Check(HhAuditCore.XcbRemove(false, 0, false), "exitcbsweep: townie, replaced adaptor on a live action removed");
        Check(!HhAuditCore.XcbRemove(false, 0, true), "exitcbsweep: household keeps a replaced adaptor on a live action (not proven exact)");

        // hiddenroomspawn: no scope input; a bad spot is repicked for any Sim (household too), a good spot is left as EA, off = EA
        Check(HhAuditCore.HrActs(true, true), "hiddenroomspawn: bad spot (hidden room) is repicked, household too");
        Check(!HhAuditCore.HrActs(true, false), "hiddenroomspawn: good spot unchanged");
        Check(!HhAuditCore.HrActs(false, true), "hiddenroomspawn: off = EA");

        // residentspawnslot / deadrefconvo / ghostreactdeadsim: no scope input, household Sims take the fixed path
        Check(HhAuditCore.RsUseHome(true, true, false), "residentspawnslot: resident with a home on another lot tests its own door slot, household too");
        Check(!HhAuditCore.RsUseHome(true, true, true) && !HhAuditCore.RsUseHome(true, false, false) && !HhAuditCore.RsUseHome(false, true, false), "residentspawnslot: same lot, no home, off = EA");
        Check(HhAuditCore.DeadHolderDrop(true, true), "deadrefconvo / ghostreactdeadsim: gone Sim dropped from a household holder");
        Check(!HhAuditCore.DeadHolderDrop(true, false) && !HhAuditCore.DeadHolderDrop(false, true), "deadrefconvo / ghostreactdeadsim: live Sim kept, off = EA");

        Console.WriteLine(sFail == 0 ? "hhaudit: PASS" : "hhaudit: " + sFail + " FAIL");
        return sFail == 0 ? 0 : 1;
    }
}
