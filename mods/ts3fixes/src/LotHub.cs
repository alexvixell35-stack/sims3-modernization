using System;
using Sims3.Gameplay;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // CP-2 managed lot hub (docs/321 §9, docs/332): OnWorldChange and OnLotLoad for ts3fixes consumers (CP-4).
    // OnWorldChange: straight from Instantiator's world load / quit handlers (via DiagWorldLoad / DiagWorldQuit), no native call.
    // OnLotLoad: ts3native's CP-1 hub queues each lot add / remove execute (lotstream.cpp) once a consumer exists; a task polls
    // UserDiag op 49 once per sim frame (one call when idle, 35 per event) and dispatches. No consumer = no task, no native
    // queue (it is armed by the first pop): the hub alone changes nothing. No ts3native, [UserDiag] off or the hub not
    // installed (lhReady 0): no lot events ever; consumers keep today's behaviour (LotHubLotsLive false).
    // Touches no Sim: dispatch only. Consumers carry their own household rule.
    public static partial class Fixes
    {
        const int kLhOp = 49;
        static bool sLhWorld, sLhTaskOn, sLhNative, sLhModeHooked;
        static int sLhTaskGen;
        static ObjectGuid sLhTaskId = ObjectGuid.InvalidObjectGuid;

        // CP-4 entry points: subscribe from the consumer's own switch-on path (any time; a live world starts polling at once).
        public static void LotHubOnWorldChange(LotHubCore.WorldFn f) { LotHubCore.SubscribeWorld(f); }
        public static void LotHubOnLotLoad(LotHubCore.LotFn f) { if (LotHubCore.SubscribeLot(f)) LhEnsureTask(); }
        // switch-off path: the row goes; the poll task ends with the last lot subscriber
        public static void LotHubOff(LotHubCore.LotFn lot, LotHubCore.WorldFn world) { LotHubCore.UnsubscribeLot(lot); LotHubCore.UnsubscribeWorld(world); }
        // true while lot events arrive in this world; false = no hub, so a consumer must not rely on lot events.
        public static bool LotHubLotsLive { get { return sLhTaskOn; } }

        public static void LotHubWorldLoad()
        {
            sLhWorld = true;
            sLhNative = DiagSend(kLhOp, 0, 4);
            LotHubCore.World(true);
            if (!sLhModeHooked) { InWorldState.InWorldSubStateChanging += LhModeChanging; sLhModeHooked = true; }
            LhEnsureTask();
        }

        // Every in-world mode change (Edit Town, CAS, Build / Buy and back): on the way back to live mode the task is re-armed
        // if it ended or its Simulator object is gone. The task itself survives a change that does not quit the world.
        static void LhModeChanging(InWorldState.SubState prev, InWorldState.SubState next)
        {
            try { if (next == InWorldState.SubState.LiveMode) { LotHubCore.ModeReturns++; LhEnsureTask(); } } catch (Exception) { LotHubCore.Faults++; }
        }

        public static void LotHubWorldQuit()
        {
            sLhWorld = false;
            sLhTaskGen++;  // the running task ends
            sLhTaskOn = false;
            if (sLhModeHooked) { InWorldState.InWorldSubStateChanging -= LhModeChanging; sLhModeHooked = false; }
            LotHubCore.World(false);
        }

        static void LhEnsureTask()
        {
            bool alive = sLhTaskOn && sLhTaskId != ObjectGuid.InvalidObjectGuid && Simulator.GetProxy(sLhTaskId) != null;
            if (!LotHubCore.ShouldArm(sLhWorld, sLhNative, sLhTaskOn, alive)) return;
            if (sLhTaskOn) LotHubCore.Rearms++;  // the armed task was destroyed without ending
            sLhTaskOn = true;
            sLhTaskId = Simulator.AddObject(new LhTask(++sLhTaskGen));
        }

        static readonly LotHubCore.Pop sLhPop = delegate { return DiagSend(kLhOp, 0, 0); };
        static readonly LotHubCore.Ask sLhOver = delegate { return DiagSend(kLhOp, 0, 3); };
        static readonly LotHubCore.Ask sLhRemove = delegate { return DiagSend(kLhOp, 0, 2); };
        static readonly LotHubCore.Bit sLhBit = delegate(int b) { return DiagSend(kLhOp, 0, (ushort)(1 | b << 8)); };

        class LhTask : Task
        {
            readonly int mGen;
            public LhTask(int gen) { mGen = gen; }
            public override void Simulate()
            {
                int step;
                while ((step = LotHubCore.TaskStep(mGen == sLhTaskGen, sLhWorld && !GameStates.IsWorldShuttingDown, GameStates.IsLiveState)) != LotHubCore.StepEnd)
                {
                    if (step == LotHubCore.StepPoll)
                        try { LotHubCore.Poll(sLhPop, sLhOver, sLhRemove, sLhBit, 16); } catch (Exception) { LotHubCore.Faults++; }
                    Simulator.Sleep(0);  // every frame, also while paused (AdaptDiag.cs AdTask); outside live mode it only waits
                }
                if (mGen == sLhTaskGen) sLhTaskOn = false;
            }
        }

        // Test entry for the cp2 queue (reflect_call): "arm" subscribes a logging consumer (one line per world change, per lot
        // event up to 32 lines, then every 64th; "arm" again after a reload must keep subscribers at 2); "result" returns the totals. Diagnostics only, never called by the game.
        static int sLhDiagLines;
        static readonly LotHubCore.LotFn sLhDiagLot = delegate(int kind, uint lot)
        {
            int n = LotHubCore.LotEvents;
            if (sLhDiagLines++ < 32 || (n & 63) == 0)
                DiagLine("lothub: " + (kind == LotHubCore.Add ? "add" : kind == LotHubCore.Remove ? "remove" : "overflow") + " lot 0x" + lot.ToString("X") + ", events " + n + ", world gen " + LotHubCore.WorldGen);
        };
        static readonly LotHubCore.WorldFn sLhDiagWorld = delegate(bool load)
        {
            DiagLine("lothub: world " + (load ? "load" : "quit") + ", world gen " + LotHubCore.WorldGen + ", lot events " + LotHubCore.LotEvents + ", faults " + LotHubCore.Faults + ", subscribers " + LotHubCore.Subscribers);
        };
        public static string LotHubDiag(string what)
        {
            if (what == "arm") { LotHubOnWorldChange(sLhDiagWorld); LotHubOnLotLoad(sLhDiagLot); }
            string r = "lothub: " + what + ", native " + sLhNative + ", polling " + sLhTaskOn + ", world gen " + LotHubCore.WorldGen + ", lot events " + LotHubCore.LotEvents + ", mode returns " + LotHubCore.ModeReturns + ", rearms " + LotHubCore.Rearms + ", faults " + LotHubCore.Faults + ", subscribers " + LotHubCore.Subscribers;
            DiagLine(r);
            return r;
        }
    }
}
