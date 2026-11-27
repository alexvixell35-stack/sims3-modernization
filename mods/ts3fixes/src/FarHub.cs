using System;
using Sims3.Gameplay;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // FarHub (docs/351 §4; lane MS): the one place that calls the far lanes: world load / quit, the pause gate, the per-frame
    // budget. No switch of its own: its frame task runs only while a lane (or farhold) says it is wanted, so with every far
    // switch off there is no task and nothing changes.
    //   world load / quit: LotHubOnWorldChange (LotHub.cs; passive, arms nothing native).
    //   pause gate: FarHubPaused() wraps SsState() of LotHub.cs (1 = live and running); docs/351 Q2.
    //   Edit Town: lots can be bulldozed, moved or replaced there, so entering it drops everything (flush, release all, reset).
    //   lot gone: the lot add / remove hub events are not used (docs/348 §5: gaps, and the id is only a low dword). A sweep
    //   every 64 frames, frozen or not, releases parked ids whose lot is gone; lanes sweep their own records.
    // Counters [1848-1855]: world loads, quits, frames with a step, frames skipped frozen, frames skipped as too long,
    // lane errors, frames that used the whole 2 ms budget, drops (all switches off, Edit Town). The steps forced in a long
    // frame (FarHubCore.ForcedSteps) are in the FarHubDiag line.
    public static partial class Fixes
    {
        const int kFhub = 1848;
        static bool sFarWorld, sFarTaskOn, sFarModeHooked;
        static int sFarTaskGen, sFarDrops;
        static ObjectGuid sFarTaskId = ObjectGuid.InvalidObjectGuid;
        static readonly FarHubCore.Clock sFarClock = delegate { return SrcNowUs(); };

        static readonly LotHubCore.WorldFn sFarWorldFn = delegate(bool load)
        {
            if (!load) { sFarTaskGen++; sFarTaskOn = false; }  // the running task ends
            sFarWorld = load;
            FarHubCore.World(load);
            if (load) { if (!sFarModeHooked) { InWorldState.InWorldSubStateChanging += FarHubMode; sFarModeHooked = true; } FarHubEnsure(); }
            else if (sFarModeHooked) { InWorldState.InWorldSubStateChanging -= FarHubMode; sFarModeHooked = false; }
            FarHubCounters();
        };

        static void ApplyFarHub() { LotHubOnWorldChange(sFarWorldFn); }

        // docs/351 §4: lanes register once, at start-up. lotGone is ours (docs/351 §8): the lot of a parked id is gone.
        public static bool FarHubRegister(int owner, FarHubCore.Fn load, FarHubCore.Fn flush, FarHubCore.Fn reset, FarHubCore.StepFn step, FarHubCore.LotFn lotGone)
        {
            return FarHubCore.Register(owner, load, flush, reset, step, lotGone);
        }

        // A lane tells the hub when its switch state changes (owner 0 = farhold itself); the task starts with the first yes.
        public static void FarHubWant(int owner, bool on) { FarHubCore.Want(owner, on); if (on) FarHubEnsure(); }

        // The one pause predicate: true in pause, CAS, Build / Buy, Edit Town, travel, shutdown, menus, or when the state is not readable.
        public static bool FarHubPaused()
        {
            try { return SsState() != 1; } catch (Exception) { return true; }
        }

        // Distance from the nearest viewer (docs/322: camera target, camera position, active household Sims), read once per sim tick.
        static float FarViewerDist(float x, float y, float z)
        {
            long now = SimClock.CurrentTicks;
            if (now != sViewTick) RefreshViewers(now);
            float dx = x - sCamTarget.x, dy = y - sCamTarget.y, dz = z - sCamTarget.z;
            float best = dx * dx + dy * dy + dz * dz;
            dx = x - sCamPos.x; dy = y - sCamPos.y; dz = z - sCamPos.z;
            float d = dx * dx + dy * dy + dz * dz;
            if (d < best) best = d;
            for (int i = 0; i < sViewerCount; i++)
            {
                dx = x - sViewers[i].x; dy = y - sViewers[i].y; dz = z - sViewers[i].z;
                d = dx * dx + dy * dy + dz * dz;
                if (d < best) best = d;
            }
            return (float)Math.Sqrt(best);
        }

        static void FarHubMode(InWorldState.SubState prev, InWorldState.SubState next)
        {
            try
            {
                if (next == InWorldState.SubState.EditTown) { sFarDrops++; FarHubCore.Drop("standDown"); FarHubCounters(); }
                else if (next == InWorldState.SubState.LiveMode) FarHubEnsure();  // a mode change can destroy the task's Simulator object
            }
            catch (Exception) { FarHubCore.Errors++; }
        }

        static void FarHubEnsure()
        {
            if (!sFarWorld || !FarHubCore.Wanted) return;
            if (sFarTaskOn && sFarTaskId != ObjectGuid.InvalidObjectGuid && Simulator.GetProxy(sFarTaskId) != null) return;
            sFarTaskOn = true;
            sFarTaskId = Simulator.AddObject(new FarHubTask(++sFarTaskGen));
        }

        class FarHubTask : Task
        {
            readonly int mGen;
            public FarHubTask(int gen) { mGen = gen; }
            public override void Simulate()
            {
                uint last = SrcNowUs(); int n = 0;
                while (mGen == sFarTaskGen && !GameStates.IsWorldShuttingDown)
                {
                    try
                    {
                        if ((n++ & 63) == 0) FarHoldSweep();
                        if (!FarHubCore.Wanted && !FarHubPaused())
                        {
                            // every far switch went off: the lanes promote what they own, both hold sets empty, the task ends
                            sFarDrops++;
                            FarHubCore.Drop("switchOff");
                            break;
                        }
                        uint now = SrcNowUs();
                        FarHubCore.Frame(FarHubPaused(), (int)(unchecked(now - last) / 1000u), sFarClock);
                        last = now;
                        FarHubCounters();
                    }
                    catch (Exception) { FarHubCore.Errors++; }
                    Simulator.Sleep(0);  // every frame, also while paused (as LotHub.cs LhTask)
                }
                if (mGen == sFarTaskGen) sFarTaskOn = false;
                FarHubCounters();
            }
        }

        static void FarHubCounters()
        {
            int[] c = PatchCounters();
            c[kFhub] = FarHubCore.Loads; c[kFhub + 1] = FarHubCore.Quits; c[kFhub + 2] = FarHubCore.Steps; c[kFhub + 3] = FarHubCore.PausedFrames;
            c[kFhub + 4] = FarHubCore.OverFrames; c[kFhub + 5] = FarHubCore.Errors; c[kFhub + 6] = FarHubCore.BudgetOut; c[kFhub + 7] = sFarDrops;
            FarHoldCounters();
        }

        public static string FarHubDiag(string what)
        {
            FarHubCounters();
            string r = "farhub: " + what + ", world " + sFarWorld + ", task " + sFarTaskOn + ", wanted " + FarHubCore.Wanted + ", paused " + FarHubPaused() + ", owners " + FarHubCore.Owners
                + ", loads " + FarHubCore.Loads + ", quits " + FarHubCore.Quits + ", steps " + FarHubCore.Steps + ", paused frames " + FarHubCore.PausedFrames + ", over frames " + FarHubCore.OverFrames + ", forced steps " + FarHubCore.ForcedSteps
                + ", budget out " + FarHubCore.BudgetOut + ", drops " + sFarDrops + ", errors " + FarHubCore.Errors;
            DiagLine(r);
            return r;
        }
    }
}
