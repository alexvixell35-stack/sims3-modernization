using System;

namespace Ts3Fixes
{
    // FarHub lifecycle and order (docs/351 §4; lane MS). No EA types: compiled into tools/selftest/farhub.
    // One entry per owner tag (FarHoldCore.OwnerFS / FO / LZ); every event calls the lanes in the fixed order FS, FO, LZ.
    // A lane that throws is counted and the others still run: a release must never depend on another lane.
    public static class FarHubCore
    {
        public delegate void Fn();
        public delegate bool More();             // true while this frame's budget lasts
        public delegate void StepFn(More more);
        public delegate void LotFn(ulong lotId);
        public delegate uint Clock();            // microseconds, wraps

        public const uint kBudgetUs = 2000;      // per sim frame, all lanes together (docs/331 §7)
        public const int kFrameMaxMs = 25;       // no step in a frame longer than this
        public const int kOverRunEvery = 8;
        public const uint kForcedBudgetUs = 500; // budget of a step forced in a long frame
        static int sOverRun;

        class Lane { public Fn Load, Flush, Reset; public StepFn Step; public LotFn LotGone; public bool Want; }
        static readonly Lane[] sLanes = new Lane[4];  // index = owner tag; [0] is the hub's own want flag (farhold)

        public static int Loads, Quits, Steps, PausedFrames, OverFrames, ForcedSteps, Errors, Replaced, LotsGone, BudgetOut;
        public static bool World_;               // a world is loaded

        public static int Owners { get { int n = 0; for (int i = 1; i < sLanes.Length; i++) if (sLanes[i] != null && sLanes[i].Step != null) n++; return n; } }

        static Lane At(int owner)
        {
            if (owner < 0 || owner >= sLanes.Length) return null;
            return sLanes[owner] ?? (sLanes[owner] = new Lane());
        }

        // At most one entry per owner tag: a second call replaces the first (counted). Any delegate may be null.
        public static bool Register(int owner, Fn load, Fn flush, Fn reset, StepFn step, LotFn lotGone)
        {
            if (owner < FarHoldCore.OwnerFS || owner > FarHoldCore.OwnerLZ) return false;
            Lane l = At(owner);
            if (l.Step != null || l.Load != null || l.Flush != null || l.Reset != null || l.LotGone != null) Replaced++;
            l.Load = load; l.Flush = flush; l.Reset = reset; l.Step = step; l.LotGone = lotGone;
            return true;
        }

        // A lane (or the hub itself, owner 0) says whether one of its switches is on. The frame task runs while any is.
        public static void Want(int owner, bool on) { Lane l = At(owner); if (l != null) l.Want = on; }
        public static bool Wanted { get { for (int i = 0; i < sLanes.Length; i++) if (sLanes[i] != null && sLanes[i].Want) return true; return false; } }

        static void Call(Fn f) { if (f == null) return; try { f(); } catch (Exception) { Errors++; } }

        // World load finished: FS load, FO load, LZ load. World quit, travel, Edit Town, quit to menu: every flush, then
        // FarReleaseAll(worldQuit), then every reset (docs/351 §4).
        public static void World(bool load)
        {
            World_ = load;
            if (load)
            {
                Loads++;
                for (int i = 1; i < sLanes.Length; i++) if (sLanes[i] != null) Call(sLanes[i].Load);
                return;
            }
            Quits++; sOverRun = 0;
            Drop("worldQuit");
        }

        // Every lane flushes, both hold sets are emptied, every lane resets. Also the path for "all switches off" and stand-down.
        public static void Drop(string reason)
        {
            for (int i = 1; i < sLanes.Length; i++) if (sLanes[i] != null) Call(sLanes[i].Flush);
            FarHoldCore.ReleaseAll(reason);
            for (int i = 1; i < sLanes.Length; i++) if (sLanes[i] != null) Call(sLanes[i].Reset);
        }

        // A lot is being destroyed (bulldoze, world shutdown): the lane drops its record, then the lot leaves the parked set.
        // Never parks, so it also runs while frozen (docs/350 §11).
        public static void LotGone(ulong lotId)
        {
            LotsGone++;
            for (int i = 1; i < sLanes.Length; i++)
                if (sLanes[i] != null && sLanes[i].LotGone != null)
                    try { sLanes[i].LotGone(lotId); } catch (Exception) { Errors++; }
            FarHoldCore.ReleaseLot(lotId);
        }

        // One sim frame. No lane step while paused / CAS / Build / Buy / menus, and none in a frame over kFrameMaxMs.
        // The lanes share one budget, FS first: a lane asks more() between units of work and stops on false.
        public static void Frame(bool paused, int frameMs, Clock clock)
        {
            if (paused) { PausedFrames++; sOverRun = 0; return; }
            // docs/331 §7 keeps our work out of long frames: frameMs is the frame that just ended, the best guess for this one.
            // Our deviation (docs/358 §6, recorded in docs/331 §7 and docs/351 §4): a game that runs above kFrameMaxMs all
            // the time would never get a boundary pass, a switch-off drop or a go-near, so after kOverRunEvery - 1 skipped
            // long frames in a row the next one steps, with a quarter of the budget. The count restarts on a normal frame,
            // on pause and on world quit.
            sBudget = kBudgetUs;
            if (frameMs > kFrameMaxMs)
            {
                if (++sOverRun < kOverRunEvery) { OverFrames++; return; }
                sBudget = kForcedBudgetUs; ForcedSteps++;
            }
            sOverRun = 0;
            sClock = clock; sT0 = clock(); sSpent = false;
            Steps++;
            for (int i = 1; i < sLanes.Length; i++)
            {
                Lane l = sLanes[i];
                if (l == null || l.Step == null) continue;
                try { l.Step(sMore); } catch (Exception) { Errors++; }
            }
        }

        // static, not a closure: no allocation per frame
        static Clock sClock; static uint sT0, sBudget; static bool sSpent;
        static readonly More sMore = delegate
        {
            if (sSpent) return false;
            if (unchecked(sClock() - sT0) < sBudget) return true;
            sSpent = true; BudgetOut++;
            return false;
        };

        // Selftests only.
        public static void Reset()
        {
            for (int i = 0; i < sLanes.Length; i++) sLanes[i] = null;
            Loads = Quits = Steps = PausedFrames = OverFrames = ForcedSteps = Errors = Replaced = LotsGone = BudgetOut = 0;
            World_ = false; sOverRun = 0;
        }
    }
}
