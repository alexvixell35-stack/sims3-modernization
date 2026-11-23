using System;
using System.Collections.Generic;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // Harness checks for IslandParadiseLogic.cs (docs/572 §6-7). Read-only decisions with controls.
    public static partial class Fixes
    {
        // SeasickPausedCheck run: a seasick Sim climbs off a sailing houseboat; another boards a parked one.
        //   Controls: moving between rooms of the same houseboat leaves the buff paused; boarding a sailing houseboat
        //   keeps EA's alarm.
        public static object SeasickPausedCheck(string what)
        {
            int[] c = PatchCounters();
            string counters = "seasickpaused: buffs unpaused / alarms dropped 2616=" + c[2616] + ", errors 2617=" + c[2617] + "; " + GlMembers("");
            if (what == "result") return counters;
            if (what != "run") return "seasickpaused <run|result>";
            bool on = ShortcutOn("seasickpaused");
            bool leave = SsUnpauseOnLeave(on, true, false, true), board = SsFixArrival(on, true, false);
            bool fixedNow = leave && board;
            bool ctlSame = !SsUnpauseOnLeave(on, true, true, true), ctlSailing = !SsFixArrival(on, true, true);
            bool controls = ctlSame && ctlSailing;
            return "seasickpaused check (switch " + (on ? "on" : "off") + "): left sailing houseboat unpaused=" + leave + ", boarded parked houseboat fixed=" + board
                + "; control: same houseboat stays paused=" + ctlSame + ", sailing houseboat keeps EA alarm=" + ctlSailing + "; controls ok=" + controls
                + "; verdict: " + GlVerdict(on, fixedNow, controls, true) + "; " + counters;
        }

        // ResortWeddingAlarmCheck run: WeddingCallback fails after the latest hour and sets no retry.
        //   Controls: a retry (new handle) is kept.
        public static object ResortWeddingAlarmCheck(string what)
        {
            int[] c = PatchCounters();
            string counters = "resortweddingalarm: handles released 2618=" + c[2618] + ", errors 2619=" + c[2619] + "; "
                + GlMembers(sRwAlarm == null ? "ResortManager.mWeddingArchAlarm" : "");
            if (what == "result") return counters;
            if (what != "run") return "resortweddingalarm <run|result>";
            bool on = ShortcutOn("resortweddingalarm");
            bool released = RwRelease(on, true);
            bool ctlRetry = !RwRelease(on, false);
            return "resortweddingalarm check (switch " + (on ? "on" : "off") + "): fired wedding handle released=" + released
                + "; control: retry handle kept=" + ctlRetry + "; controls ok=" + ctlRetry
                + "; verdict: " + GlVerdict(on, released, ctlRetry, sRwAlarm != null) + "; " + counters;
        }
    }
}
