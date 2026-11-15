using System;
using Sims3.Gameplay;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Core;

namespace Ts3Fixes
{
    // privateeyecase harness check (docs/562 §5). Read-only: nothing in the world changes.
    public static partial class Fixes
    {
        // PrivateEyeCaseCheck run: the four decisions (mail with no case / a case Sim with no door, garbage search finished
        //   after the case ended, a stale snoop alarm, a break-in on a household-locked door) plus, when the world has an
        //   apartment wall mailbox (Bridgeport), a live door lookup on it for an instantiated Sim who does not live there:
        //   EA's GetDoorOfSim returns null (what LookThroughMail then dereferences); with the switch on and the lookup marked
        //   as a LookThroughMail's, it returns one of the mailbox's doors. The Sim is picked so that EA's MoveIn branch is
        //   not taken (home lot elsewhere), so the lookup changes no assignment.
        //   off BROKEN = EA's paths; on FIXED = all four fixed and the live lookup gets a door.
        //   (e) stakeout: a deleted required Sim (id cleared for good; a Sim that only is not instantiated keeps its id),
        //   and no crime candidates (result left WitnessCrime with no Sketchy).
        //   result  counters 2499-2504
        public static object PrivateEyeCaseCheck(string what)
        {
            int[] c = PatchCounters();
            string counters = "privateeyecase: mail 2499=" + c[2499] + ", garbage 2500=" + c[2500] + ", stale snoop alarms 2501=" + c[2501]
                + ", locks restored 2502=" + c[2502] + ", errors 2503=" + c[2503] + ", stakeouts 2504=" + c[2504];
            if (what == "result") return counters;
            if (what != "run") return "privateeyecase <run|result>";
            bool on = ShortcutOn("privateeyecase");
            bool mail = PeMailAction(on, false, false, false, false) == 1 && PeMailAction(on, true, true, true, true) == 2;
            bool garbage = PeGarbageSwallow(on, true, false);
            bool snoop = !PeSnoopAct(on, false);
            bool lockBack = PeRestoreLock(on, true, false);
            bool stakeout = PeStakeoutClearId(on, true, false) && PeStakeoutFail(on, false, false, true);
            bool control = PeMailAction(on, true, true, false, true) != 0 || PeGarbageSwallow(on, false, false) || PeGarbageSwallow(on, true, true)
                || !PeSnoopAct(on, true) || PeRestoreLock(on, false, false) || PeRestoreLock(on, true, true)
                || PeStakeoutClearId(on, true, true) || PeStakeoutClearId(on, false, false) || PeStakeoutFail(on, true, true, true) || PeStakeoutFail(on, false, false, false);
            bool any = mail || garbage || snoop || lockBack || stakeout, all = mail && garbage && snoop && lockBack && stakeout;

            string live = "no apartment wall mailbox in this world";
            bool liveFixed = true, liveRan = false;
            try
            {
                foreach (MailboxWall box in Queries.GetObjects<MailboxWall>())
                {
                    if (box.LotCurrent == null || box.GetDoors().Length == 0) continue;
                    Sim pick = null;
                    foreach (Sim s in Queries.GetObjects<Sim>())
                        if (s.Household != null && s.LotHome != box.LotCurrent && box.GetDoorOfSim(s) == null) { pick = s; break; }
                    if (pick == null) continue;
                    if (on) lock (sPeMailFallback) sPeMailFallback[box] = pick;
                    MailboxDoor d;
                    try { d = box.GetDoorOfSim(pick); }
                    finally { if (on) lock (sPeMailFallback) sPeMailFallback.Remove(box); }
                    liveRan = true;
                    liveFixed = d != null;
                    live = "wall mailbox on lot " + box.LotCurrent.LotId + " (" + box.GetDoors().Length + " doors), Sim from another lot -> door " + (d == null ? "null" : "found");
                    break;
                }
            }
            catch (Exception e) { live = "live lookup threw " + e.GetType().Name; liveFixed = !on; }
            bool liveOk = !liveRan || (on ? liveFixed : !liveFixed);

            string verdict = control || !liveOk ? "UNEXPECTED" : !on ? (any ? "UNEXPECTED" : "BROKEN") : (all ? "FIXED" : "UNEXPECTED");
            return "privateeyecase check (switch " + (on ? "on" : "off") + "): mail=" + mail + ", garbage=" + garbage + ", snoop alarm ignored=" + snoop
                + ", lock restored=" + lockBack + ", stakeout fixed=" + stakeout + ", controls=" + control + "; live: " + live + "; verdict: " + verdict + "; " + counters;
        }
    }
}
