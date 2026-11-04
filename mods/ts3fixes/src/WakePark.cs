using System;
using System.Collections.Generic;
using Sims3.Gameplay;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // Unseen wake-ups (docs/211, docs/209 #94 actions 1-2). EA's LOD cuts what an unseen Sim computes, not how often its script
    // tasks wake: every low-LOD Sim's SimUpdate task wakes every tick (Sleep(0)), every looping interaction polls its exit
    // reason every tick (Sim.WaitForExitReason: Sleep(1)), idle Sims loop every 2-20 ticks, every commercial lot runs
    // UpdateReactions every tick.
    //   wakeprobe : counts those wake-ups per class (seen/unseen by the docs/202 rule UfWhy x EA's cached LOD), times 1 in 16
    //               SimUpdate passes (from the pass's LOD check to its end) and UpdateReactions calls. No skips.
    //   wakepark  : an unseen low-LOD Sim's SimUpdate pass (not the active Sim) sleeps kWakeParkTicks ticks instead of 1 after
    //               UpdateTimer (InteractionQueue.UpdateTimer is a no-op in 1.67: no script sets its timer flag). Elapsed time
    //               is kept (the pass reads the clock), so the next pass sees the whole gap. Woken at once on a view change
    //               (camera jump, active household or selected Sim, leaving live mode); the verdict is re-asked at every wake.
    //   wakepoll  : an unseen Sim's WaitForExitReason sleeps up to kWakePollTicks per poll, never past its deadline, and is woken
    //               by Sim.AddExitReason (the only script writer that sets a reason), so it returns on the same tick as the game.
    // Hooks are patched only with kWakeHooks, kWakePark or kWakePoll on, once the tuning is set (Fixes.ApplyTuned; test package: build-fixes.ts --on kWakeHooks):
    // the SimUpdate and IsActiveSim hooks add a call on every pass. Seen Sims run the game's code path unchanged.
    // Counters: [491-494] SimUpdate passes by class (seen high-LOD, seen low-LOD, unseen high-LOD, unseen low-LOD), [495-498] their
    // timed us, [530-533] timed passes, [499-502] exit-reason polls by class, [503-504] waits seen / unseen, [505-508] idle passes by
    // class, [509-512] UpdateReactions calls (full lot seen / unseen, broadcasters-only seen / unseen), [513-516] their timed us (1 in
    // 16 by count), [517] timed samples dropped (the call yielded), [518] verdicts asked (UfWhy), [519] their timed us (1 in 16),
    // [520] parks, [521] park ticks asked, [522] parks woken early, [523] view-change wake-alls, [524] parks refused (save),
    // [525] long polls, [526] polls woken by AddExitReason, [527] parked waits ended at their deadline, [528] second waiter on a
    // Sim (polled every tick), [529] most ticks past the deadline at a parked wait's end.
    public static partial class Fixes
    {
        const int kWpSuPass = 491, kWpSuUs = 495, kWpPoll = 499, kWpWait = 503, kWpIdle = 505, kWpReact = 509, kWpReactUs = 513,
            kWpYielded = 517, kWpClassify = 518, kWpClassifyUs = 519, kWpParks = 520, kWpParkTicks = 521, kWpParkEarly = 522,
            kWpWakeAll = 523, kWpParkRefused = 524, kWpPollLong = 525, kWpPollWoken = 526, kWpPollDeadline = 527, kWpPollShared = 528,
            kWpPollOver = 529, kWpSuSamples = 530;
        const int kWpSeenMaxTicks = 60;  // EA's own LOD cache length (Autonomy.IsSufficientlyOnScreenForHighLODSimulation)
        static bool sWpProbe, sWpPark, sWpPoll, sWpPassEnd;
        static int sWpSwLeft;
        static readonly WpCache sWpSims = new WpCache(), sWpLots = new WpCache();
        static readonly WpWaiters sWpWaiters = new WpWaiters();
        static readonly WpParked sWpParked = new WpParked();
        static readonly WpView sWpView = new WpView();
        static readonly List<ulong> sWpTmp = new List<ulong>();
        static long sWpViewTick = -1, sWpPruneTick;
        static StopWatch sWpWatch;
        // one armed timing at a time: the Sim's next pass is timed from its LOD check to its end
        static Autonomy sWpArmAut;
        static ulong sWpArmTask;
        static uint sWpT0;
        static long sWpT0Tick = -1, sWpArmTick;

        // Called by ApplyTuned once the tuning is set (docs/124 §17.8: Apply only sees C# defaults). All seven or none: wakepoll
        // parks in WaitForExitReason and needs the AddExitReason hook to wake it. If the game already compiled any target (the
        // first world load ran it), nothing is patched and the MISSING line fails the job.
        static void ApplyWakePark()
        {
            if (!Instantiator.kWakeHooks && !Instantiator.kWakePark && !Instantiator.kWakePoll) return;
            Type[] f = { typeof(float) }, none = Type.EmptyTypes, er = { typeof(ExitReason) }, wf = { typeof(float), typeof(ExitReason) }, rf = { typeof(float), typeof(bool) };
            string late = NotPatchable(typeof(InteractionQueue), "UpdateTimer", f) ?? NotPatchable(typeof(Sim), "get_IsActiveSim", none)
                ?? NotPatchable(typeof(Sim), "DoInteraction", none) ?? NotPatchable(typeof(Sim), "WaitForExitReason", wf)
                ?? NotPatchable(typeof(Sim), "AddExitReason", er) ?? NotPatchable(typeof(Autonomy), "get_IsRunningHighLODSimulation", none)
                ?? NotPatchable(typeof(Lot), "UpdateReactions", rf);
            if (late != null)
            {
                TunedLog("kWakeHooks/kWakePark/kWakePoll", false, "WakePark hooks (" + late + " missing or already compiled, none patched)");
                return;
            }
            bool ok = Patch(typeof(InteractionQueue), "UpdateTimer", f, typeof(WpTimerHook), "TimerHook", "TimerOrig")
                & Patch(typeof(Sim), "get_IsActiveSim", none, typeof(WpSimHook), "ActiveHook", "ActiveOrig")
                & Patch(typeof(Sim), "DoInteraction", none, typeof(WpSimHook), "DoHook", "DoOrig")
                & Patch(typeof(Sim), "WaitForExitReason", wf, typeof(WpSimHook), "WaitHook", "WaitOrig")
                & Patch(typeof(Sim), "AddExitReason", er, typeof(WpSimHook), "AddHook", "AddOrig")
                & Patch(typeof(Autonomy), "get_IsRunningHighLODSimulation", none, typeof(WpAutHook), "LodHook", "LodOrig")
                & Patch(typeof(Lot), "UpdateReactions", rf, typeof(WpLotHook), "ReactHook", "ReactOrig");
            World.OnWorldQuitEventHandler += delegate { WakeParkWorldLoad(); };
            TunedLog("kWakeHooks/kWakePark/kWakePoll", ok, "WakePark hooks");
        }

        // World load and quit: every task of the old world is gone; drop the tables.
        internal static void WakeParkWorldLoad()
        {
            sWpSims.Clear(); sWpLots.Clear(); sWpWaiters.Clear(); sWpParked.Clear(); sWpView.Reset();
            sWpArmAut = null; sWpT0Tick = -1; sWpPassEnd = false; sWpViewTick = -1;
        }

        // Switches read every 256 hook calls (ShortcutOn is a string lookup; the SimUpdate hooks run thousands of times a second).
        static void WpSw()
        {
            if (--sWpSwLeft >= 0) return;
            sWpSwLeft = 255;
            sWpProbe = ShortcutOn("wakeprobe"); sWpPark = ShortcutOn("wakepark"); sWpPoll = ShortcutOn("wakepoll");
            if (sWpWatch == null && sWpProbe)
            {
                sWpWatch = StopWatch.Create(StopWatch.TickStyles.Microseconds);
                if (sWpWatch != null) sWpWatch.Start();
            }
        }

        // UfWhy (docs/202 rule, 0 = unseen) cached per Sim: unseen verdicts for one park length, seen ones for 60 ticks.
        static int WpWhy(Sim s, long now)
        {
            ulong id = s.ObjectId.Value;
            int v; long t;
            if (sWpSims.Get(id, out v, out t) && !WakeParkCore.Stale(now - t, v == 0, Instantiator.kWakeParkTicks, kWpSeenMaxTicks)) return v;
            int[] c = PatchCounters();
            if ((++c[kWpClassify] & 15) == 0 && sWpWatch != null)
            {
                uint t0 = sWpWatch.GetElapsedTime();
                v = UfWhy(s);
                c[kWpClassifyUs] += (int)unchecked(sWpWatch.GetElapsedTime() - t0);
            }
            else v = UfWhy(s);
            sWpSims.Put(id, v, now);
            return v;
        }

        static int WpClass(Sim s, long now, out int why)
        {
            why = WpWhy(s, now);
            Autonomy a = s.Autonomy;
            return WakeParkCore.Class(why == 0, a != null && a.GetCachedHighLODValue);
        }

        // Once per tick: a view change wakes every parked task and drops the cached verdicts.
        static void WpViewCheck(long now)
        {
            if (now == sWpViewTick) return;
            sWpViewTick = now;
            if (now - sWpPruneTick > 600)
            {
                sWpPruneTick = now;
                long max = 4L * Math.Max(Instantiator.kWakeParkTicks, Instantiator.kWakePollTicks) + 60;
                sWpParked.Prune(now, max); sWpWaiters.Prune(now, max);
            }
            Vector3 cam = CameraController.GetTarget();
            Household h = Household.ActiveHousehold;
            Sim sel = Sim.ActiveActor;
            if (!sWpView.Changed(cam.x, cam.z, h != null ? h.HouseholdId : 0UL, sel != null ? sel.ObjectId.Value : 0UL, GameStates.IsLiveState)) return;
            sWpSims.Clear(); sWpLots.Clear();
            if (sWpParked.Count == 0 && sWpWaiters.Count == 0) return;
            PatchCounters()[kWpWakeAll]++;
            sWpTmp.Clear();
            sWpParked.AllTasks(sWpTmp); sWpWaiters.AllTasks(sWpTmp);
            foreach (ulong t in sWpTmp) WpWake(t);
        }

        static void WpWake(ulong task)
        {
            ObjectGuid g = new ObjectGuid(task);
            if (Simulator.GetProxy(g) != null) Simulator.Wake(g, false); // a task destroyed while asleep has no proxy
        }

        // End of a SimUpdate pass (inside the SimUpdate task, right after UpdateTimer). Counts and times it; returns the ticks
        // to park (0 = run as the game does).
        static uint WpPassEnd(Sim s, bool active)
        {
            int[] c = PatchCounters();
            long now = SimClock.CurrentTicks;
            int why;
            int cls = WpClass(s, now, out why);
            Autonomy a = s.Autonomy;
            if (sWpProbe)
            {
                c[kWpSuPass + cls]++;
                if (sWpArmAut != null)
                {
                    if ((object)a == (object)sWpArmAut && sWpT0Tick >= 0 && Simulator.CurrentTask.Value == sWpArmTask)
                    {
                        if (sWpT0Tick == now) { c[kWpSuUs + cls] += (int)unchecked(sWpWatch.GetElapsedTime() - sWpT0); c[kWpSuSamples + cls]++; }
                        else c[kWpYielded]++;
                        sWpArmAut = null;
                    }
                    else if (now - sWpArmTick > 240) sWpArmAut = null; // the armed Sim's next pass never came (destroyed, reset)
                }
                if (sWpArmAut == null && a != null && sWpWatch != null && (c[kWpSuPass + cls] & 15) == 0)
                {
                    sWpArmAut = a; sWpArmTask = Simulator.CurrentTask.Value; sWpT0Tick = -1; sWpArmTick = now;
                }
            }
            if (!sWpPark) return 0;
            WpViewCheck(now);
            if (active || why != 0 || (a != null && a.GetCachedHighLODValue)) return 0;
            if (SaveInProgress()) { c[kWpParkRefused]++; return 0; }
            return WakeParkCore.ParkSleep(Instantiator.kWakeParkTicks);
        }

        // InteractionQueue.UpdateTimer: only caller is SimUpdate.Simulate, and the next call there is mSim.IsActiveSim.
        public class WpTimerHook
        {
            public void TimerHook(float timePassed)
            {
                TimerOrig(timePassed);
                WpSw();
                sWpPassEnd = sWpProbe || sWpPark;
            }
            public void TimerOrig(float timePassed) { } // receives InteractionQueue.UpdateTimer's original header
        }

        // Sim has a parameterless constructor, but nothing here needs its members: a plain class as the other Sim hooks.
        public class WpSimHook
        {
            public bool ActiveHook()
            {
                bool r = ActiveOrig();
                if (!sWpPassEnd || OnWorker()) return r; // the [AutShadow] worker may score with IsActiveSim: never ours
                sWpPassEnd = false;
                Sim s = (Sim)(object)this;
                uint k = 0;
                try { k = WpPassEnd(s, r); } catch (Exception) { k = 0; }
                if (k == 0) return r;
                int[] c = PatchCounters();
                ulong task = Simulator.CurrentTask.Value;
                long t0 = SimClock.CurrentTicks;
                c[kWpParks]++; c[kWpParkTicks] += (int)k + 1;
                sWpParked.Add(task, t0);
                try { Simulator.Sleep(k); } // ResetException passes through: SimUpdate.Simulate catches it
                finally { sWpParked.Remove(task); }
                if (SimClock.CurrentTicks - t0 < k) c[kWpParkEarly]++;
                return r;
            }
            public bool ActiveOrig() { return false; } // receives Sim.get_IsActiveSim's original header

            // Idle pass = the Sim's loop found an empty queue (LoopIdle, autonomy queue, then a 2-20 tick sleep).
            public void DoHook()
            {
                WpSw();
                if (sWpProbe)
                {
                    Sim s = (Sim)(object)this;
                    InteractionQueue q = s.InteractionQueue;
                    if (q != null && q.GetHeadInteraction() == null)
                    {
                        int why;
                        PatchCounters()[kWpIdle + WpClass(s, SimClock.CurrentTicks, out why)]++;
                    }
                }
                DoOrig();
            }
            public void DoOrig() { } // receives Sim.DoInteraction's original header

            // The game's loop (check, sleep one tick, read the clock, until the deadline) with a longer sleep for unseen Sims.
            public bool WaitHook(float simMinutes, ExitReason acceptedExitReasons)
            {
                WpSw();
                if (!sWpProbe && !sWpPoll) return WaitOrig(simMinutes, acceptedExitReasons);
                Sim s = (Sim)(object)this;
                int[] c = PatchCounters();
                long now = SimClock.CurrentTicks;
                long end = simMinutes != float.MaxValue ? now + SimClock.ConvertToTicks(simMinutes, TimeUnit.Minutes) : long.MaxValue;
                ulong sim = s.ObjectId.Value;
                bool first = true;
                do
                {
                    if (s.HasExitReason(acceptedExitReasons)) return true;
                    int why;
                    int cls = WpClass(s, now, out why);
                    if (first) { c[kWpWait + (why == 0 ? 1 : 0)]++; first = false; }
                    c[kWpPoll + cls]++;
                    uint t = 1u;
                    ulong task = 0;
                    if (sWpPoll && why == 0)
                    {
                        WpViewCheck(now);
                        t = WakeParkCore.PollSleep(now, end, Instantiator.kWakePollTicks);
                        if (t > 1)
                        {
                            task = Simulator.CurrentTask.Value;
                            if (!sWpWaiters.TryAdd(sim, task, (uint)acceptedExitReasons, now)) { c[kWpPollShared]++; task = 0; t = 1u; }
                        }
                    }
                    if (task == 0) Simulator.Sleep(1u);
                    else
                    {
                        c[kWpPollLong]++;
                        try { Simulator.Sleep(t); }
                        finally { sWpWaiters.Remove(sim, task); }
                    }
                    now = SimClock.CurrentTicks;
                    if (task != 0 && now >= end && end != long.MaxValue)
                    {
                        c[kWpPollDeadline]++;
                        long over = now - end;
                        if (over > c[kWpPollOver]) c[kWpPollOver] = (int)Math.Min(over, int.MaxValue);
                    }
                }
                while (now < end);
                return false;
            }
            public bool WaitOrig(float simMinutes, ExitReason acceptedExitReasons) { return false; } // receives Sim.WaitForExitReason's original header

            public void AddHook(ExitReason reason)
            {
                AddOrig(reason);
                if (sWpWaiters.Count == 0) return;
                ulong t = sWpWaiters.Match(((Sim)(object)this).ObjectId.Value, (uint)reason);
                if (t == 0) return;
                PatchCounters()[kWpPollWoken]++;
                WpWake(t);
            }
            public void AddOrig(ExitReason reason) { } // receives Sim.AddExitReason's original header
        }

        // Autonomy has no parameterless constructor: plain class. Start of an armed timed pass (the pass's LOD check).
        public class WpAutHook
        {
            public bool LodHook()
            {
                if ((object)this == (object)sWpArmAut && sWpT0Tick < 0 && !OnWorker() && Simulator.CurrentTask.Value == sWpArmTask)
                {
                    sWpT0Tick = SimClock.CurrentTicks;
                    sWpT0 = sWpWatch.GetElapsedTime();
                }
                return LodOrig();
            }
            public bool LodOrig() { return false; } // receives Autonomy.get_IsRunningHighLODSimulation's original header
        }

        // Lot.UpdateReactions (Lot.Simulate, every tick for a simulated lot, else while broadcasters hold Sims).
        public class WpLotHook
        {
            public void ReactHook(float timePassed, bool simulateFullLot)
            {
                WpSw();
                if (!sWpProbe) { ReactOrig(timePassed, simulateFullLot); return; }
                Lot lot = (Lot)(object)this;
                long now = SimClock.CurrentTicks;
                int v; long at;
                ulong id = lot.LotId;
                if (!sWpLots.Get(id, out v, out at) || WakeParkCore.Stale(now - at, false, 0, kWpSeenMaxTicks))
                {
                    v = !GameStates.IsLiveState || UfLotSeen(lot) ? 1 : 0;
                    sWpLots.Put(id, v, now);
                }
                int i = (simulateFullLot ? 0 : 2) + (v != 0 ? 0 : 1);
                int[] c = PatchCounters();
                if ((++c[kWpReact + i] & 15) != 0 || sWpWatch == null) { ReactOrig(timePassed, simulateFullLot); return; }
                uint t0 = sWpWatch.GetElapsedTime();
                ReactOrig(timePassed, simulateFullLot);
                if (SimClock.CurrentTicks == now) c[kWpReactUs + i] += (int)unchecked(sWpWatch.GetElapsedTime() - t0);
                else c[kWpYielded]++;
            }
            public void ReactOrig(float timePassed, bool simulateFullLot) { } // receives Lot.UpdateReactions' original header
        }
    }
}
