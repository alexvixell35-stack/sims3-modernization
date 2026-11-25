using System;
using System.Collections.Generic;
using Sims3.Gameplay.Core;

namespace Ts3Fixes
{
    // docs/321 §10, docs/332 CP-3 / CP-4 (managed part): lane A consumers of the CP-2 lot hub (LotHub.cs). One switch each, all
    // default off (SwDefault: unknown names are off). They only drop rows of ts3fixes' own tables or re-send a lot's DoorGen
    // hold; no Sim is touched and no answer changes for household Sims (docs/332 H column):
    //   laneaenvlist (O18)  envlistmemoexact sExLotOff (lot ids): the unloaded lot's row dropped (a lot that streams back in is
    //                       tried again; world quit already clears it, WorldQuitLeaks.cs L19)
    //   lanealoop    (L15)  gohome sLastShortcut (SimDescriptionId -> tick): cleared on world load and quit (ticks of another
    //                       world or session are not this one's loop)
    //   laneatp      (M15)  unseenfbteleport sUfTpPlaced: at its 4096 cap, rows of other worlds go first instead of all rows.
    //                       Not cleared on world change: a travel round trip must still find the home world's placements for the
    //                       load-time cleanup (rule "clean up our damage on load"); rows are ids + a point, bounded at 4096
    //   laneadoorgen (DoorGen per lot) doorgen: a lot that streams in has its hold re-sent to ts3native (op 36), as at arm
    // Counters 1699-1706 (docs/registry.md fixes-1699): envlist rows / high water, teleport rows / high water, loop rows / high
    // water, doorgen per-lot sends, envlist rows dropped by a lot remove. A "lanea:" line at each world load and quit.
    public static partial class Fixes
    {
        const int kLaSlot = 1699;
        static bool sLaEnv, sLaLoop, sLaTp, sLaDoor;
        static readonly LotHubCore.LotFn sLaLot = LaLot;
        static readonly LotHubCore.WorldFn sLaWorld = LaWorld;

        // DiagWorldLoad (UserDiag.cs), after DoorGenWorldLoad: read the switches and (un)subscribe; static delegates keep one row
        public static void LaneAWorldLoad()
        {
            sLaEnv = ShortcutOn("laneaenvlist");
            sLaLoop = ShortcutOn("lanealoop");
            sLaTp = ShortcutOn("laneatp");
            sLaDoor = ShortcutOn("laneadoorgen");
            if (sLaEnv || sLaDoor) LotHubOnLotLoad(sLaLot); else LotHubOff(sLaLot, null);
            if (sLaLoop) LotHubOnWorldChange(sLaWorld); else LotHubOff(null, sLaWorld);
            if (!sLaEnv && !sLaLoop && !sLaTp && !sLaDoor) return;
            if (sLaLoop) GoHomeHook.LoopClear();  // subscribed after this load's dispatch: clear here once
            LaLog("world load");
        }

        static void LaWorld(bool load)
        {
            if (sLaLoop) GoHomeHook.LoopClear();
            if (!load) LaLog("world quit");
        }

        static void LaLot(int kind, uint lotLow)
        {
            int[] c = PatchCounters();
            if (sLaEnv && kind != LotHubCore.Add) c[kLaSlot + 7] += LaneACore.DropLot(sExLotOff, kind == LotHubCore.Overflow, lotLow);
            if (sLaDoor && kind == LotHubCore.Add)
                foreach (Lot lot in LotManager.AllLots)
                    if (lot != null && (uint)lot.LotId == lotLow) { DoorGenHold(lot); c[kLaSlot + 6]++; break; }
            LaSee();
        }

        // unseenfbteleport cap (UnseenTeleportGoal.cs UfTpNote): rows of other worlds first; true = room made
        static bool LaTpPrune(string world)
        {
            if (!sLaTp) return false;
            int n = LaneACore.DropOtherWorlds(sUfTpPlaced, world);
            LaSee();
            return n > 0 && sUfTpPlaced.Count < 4096;
        }

        static void LaSee()
        {
            int[] c = PatchCounters();
            LaneACore.See(c, kLaSlot, sExLotOff.Count);
            LaneACore.See(c, kLaSlot + 2, sUfTpPlaced.Count);
            LaneACore.See(c, kLaSlot + 4, GoHomeHook.LoopCount);
        }

        // Test entry for cp4b-correct-1123 (reflect_call, never called by the game): ts3native op 49 sub 7 window checks of the
        // game-state gate (docs/321 §11.3): mark, still (no sweep / dedupe-invalidation work since the mark), skipped (the gate
        // turned work away), drained (nothing queued), ok (verify counters 0), requeue (one whole-map pass waiting), m8 (the M8 A vs
        // B counter lines into ts3native.log).
        public static string LaneAGate(string what)
        {
            int sub = Array.IndexOf(new string[] { "mark", "still", "skipped", "drained", "ok", "requeue", "m8" }, what);
            bool a = sub >= 0 && DiagSend(kLhOp, 0, (ushort)(7 | sub << 8));
            string r = "lanea gate: " + what + " " + a;
            DiagLine(r);
            return r;
        }

        static void LaLog(string why)
        {
            LaSee();
            int[] c = PatchCounters();
            DiagLine("lanea: " + why + ", envlist " + c[kLaSlot] + "/" + c[kLaSlot + 1] + ", tp " + c[kLaSlot + 2] + "/" + c[kLaSlot + 3]
                + ", loop " + c[kLaSlot + 4] + "/" + c[kLaSlot + 5] + ", doorgen lots " + c[kLaSlot + 6] + ", envlist drops " + c[kLaSlot + 7]
                + ", lots live " + LotHubLotsLive + ", subscribers " + LotHubCore.Subscribers);
        }
    }
}
