using System;
using System.Reflection;
using Sims3.Gameplay;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.RealEstate;
using Sims3.Gameplay.Socializing;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;
using GameSpeed = Sims3.SimIFace.Gameflow.GameSpeed;
using Gameflow = Sims3.Gameplay.Gameflow;

namespace Ts3Fixes
{
    // Game speed, speed lock and repeating-alarm fixes (docs/609, s78). All default off; EA bug fixes as EA wrote the code
    // next to each bug. Household rule: these are the game clock and the speed buttons, which EA meant to behave this way in
    // every household (the player's own included), so there is no household exemption: the result is what EA intended.
    // heartbrokenbcast (§7) touches household Sims too: it only disposes a broadcaster EA itself stopped referencing.
    public static partial class Fixes
    {
        // ---- docs/609 §2 speedskiplevel, §3 speedskipnoactor ----------------------------------------------------------------
        // [verified, ilspycmd Sims3.Gameplay.Gameflow] SetGameSpeed(GameSpeed, bool lockSpeed) sets the time scale and
        // mGameSpeed in a first switch on the requested speed, then tells the engine the speed level (GameUtils.
        // SetGameTimeSpeedLevel) in a second switch on the requested speed again (IL ldarg.0 at IL_0095), not on the speed
        // it set. Skip has two branches that do not set Skip:
        //   - no head interaction on the active Sim: time scale and mGameSpeed fall back to Normal, but the engine is told
        //     level 3 (Triple/Skip), so the clock UI and engine show fast speed while time runs at 1x (speedskiplevel);
        //   - no active Sim at all (Sim.ActiveActor null): no branch runs, the time scale stays 0 and mGameSpeed keeps the old
        //     speed, so time silently freezes while every reader of CurrentGameSpeed still sees the old speed
        //     (speedskipnoactor; also persisted and sent to GameSpeedChanged as the old speed).
        // Fix as EA intended: the level follows mGameSpeed, and Skip with nobody to skip falls back to Normal as the
        // no-head-interaction branch right next to it does. Both are done by handing EA's body Normal in those cases: EA's
        // Normal path sets exactly what the fallback sets (time scale, mGameSpeed, persisted speed, event) plus level 1.
        // With only speedskiplevel on and no active Sim, the level is set from the unchanged mGameSpeed after EA's body.
        // Counters: [3405] Skip fallbacks given level 1, [3406] level 3 sent for Normal with the switch off, [3407] errors;
        // [3408] Skip with no active Sim run as Normal, [3409] time frozen by it with the switch off, [3410] errors.
        static string sGfMissing = "";
        static MethodInfo sGfSetSpeed;   // Gameflow.SetGameSpeed(GameSpeed, bool): the live body (ours once swapped)

        static void ApplySpeedSkipLevel()
        {
            sGfSetSpeed = typeof(Gameflow).GetMethod("SetGameSpeed", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public, null,
                new Type[] { typeof(GameSpeed), typeof(bool) }, null);
            Patch(typeof(Gameflow), "SetGameSpeed", new Type[] { typeof(GameSpeed), typeof(bool) }, typeof(GfSpeedHook), "SetHook", "SetOrig");
            sGfMissing = EpLive(null, 0, "SetGameSpeed(GameSpeed,bool)", sGfSetSpeed, 2);
        }

        // Pure: the speed level EA's second switch sends for a speed (Pause 0, Normal 1, Double 2, Triple and Skip 3; -1 none).
        internal static int GfLevel(GameSpeed s)
        {
            switch (s)
            {
                case GameSpeed.Pause: return 0;
                case GameSpeed.Normal: return 1;
                case GameSpeed.Double: return 2;
                case GameSpeed.Triple: case GameSpeed.Skip: return 3;
            }
            return -1;
        }

        // Pure model of EA's SetGameSpeed body: speed set, level sent, time running. prev = mGameSpeed before the call.
        internal static void GfEa(GameSpeed requested, bool hasActor, bool hasHead, GameSpeed prev, out GameSpeed speed, out int level, out bool running)
        {
            speed = prev;
            running = false;
            if (requested != GameSpeed.Skip) { speed = requested; running = requested != GameSpeed.Pause; }
            else if (hasActor) { speed = hasHead ? GameSpeed.Skip : GameSpeed.Normal; running = true; }
            level = GfLevel(requested);
        }

        // Pure: the speed our hook hands EA's body (Normal for a Skip with nobody or nothing to skip, when that fix is on).
        internal static GameSpeed GfPass(GameSpeed requested, bool hasActor, bool hasHead, bool levelOn, bool noActorOn)
        {
            if (requested != GameSpeed.Skip) return requested;
            if (!hasActor) return noActorOn ? GameSpeed.Normal : requested;
            return !hasHead && levelOn ? GameSpeed.Normal : requested;
        }

        // Pure: the outcome with our hook (EA's body on GfPass, then the level re-sent from mGameSpeed in the one case left).
        internal static void GfOurs(GameSpeed requested, bool hasActor, bool hasHead, GameSpeed prev, bool levelOn, bool noActorOn,
            out GameSpeed speed, out int level, out bool running)
        {
            GfEa(GfPass(requested, hasActor, hasHead, levelOn, noActorOn), hasActor, hasHead, prev, out speed, out level, out running);
            if (levelOn && requested == GameSpeed.Skip && !hasActor && !noActorOn) level = GfLevel(speed);
        }

        public static class GfSpeedHook
        {
            public static void SetHook(GameSpeed requestedSpeed, bool lockSpeed)
            {
                GameSpeed pass = requestedSpeed;
                bool levelOn = false, noActor = false;
                if (requestedSpeed == GameSpeed.Skip)
                {
                    try
                    {
                        levelOn = ShortcutOn("speedskiplevel");
                        bool noActorOn = ShortcutOn("speedskipnoactor");
                        Sim a = Sim.ActiveActor;
                        noActor = a == null;
                        // EA reads the head interaction only with an active Sim; a null queue throws in EA's body as before
                        bool hasHead = noActor || a.InteractionQueue == null || a.InteractionQueue.GetHeadInteraction() != null;
                        pass = GfPass(requestedSpeed, !noActor, hasHead, levelOn, noActorOn);
                        int[] c = PatchCounters();
                        if (noActor) c[noActorOn ? 3408 : 3409]++;
                        else if (!hasHead) c[levelOn ? 3405 : 3406]++;
                        levelOn &= noActor && !noActorOn;   // the one case left for after EA's body
                    }
                    catch (ResetException) { throw; }
                    catch (Exception) { PatchCounters()[3407]++; pass = requestedSpeed; levelOn = false; }
                }
                SetOrig(pass, lockSpeed);
                if (!levelOn) return;
                try { GameUtils.SetGameTimeSpeedLevel(GfLevel(Gameflow.CurrentGameSpeed)); }
                catch (ResetException) { throw; }
                catch (Exception) { PatchCounters()[3407]++; }
            }
            public static void SetOrig(GameSpeed requestedSpeed, bool lockSpeed) { } // receives Gameflow.SetGameSpeed(GameSpeed, bool)
        }

        public static object SpeedSkipLevelCheck(string what)
        {
            string counters = B582Counters("speedskiplevel", 3405, new[] { "Skip fallbacks given level 1", "level 3 for Normal with switch off", "errors" }, sGfMissing);
            if (what == "result") return counters;
            if (what != "run") return "speedskiplevel <run|result>";
            bool on = ShortcutOn("speedskiplevel");
            GameSpeed s; int lv; bool run;
            // the bug: EA's Skip fallback runs at Normal but sends level 3
            GfEa(GameSpeed.Skip, true, false, GameSpeed.Normal, out s, out lv, out run);
            bool eaBug = s == GameSpeed.Normal && run && lv == 3;
            GfOurs(GameSpeed.Skip, true, false, GameSpeed.Double, on, false, out s, out lv, out run);
            bool fixedPure = on && s == GameSpeed.Normal && run && lv == 1;
            // controls: a real Skip and every other speed keep EA's level; no-actor Skip with only this fix: level of the kept speed
            bool controls = eaBug;
            foreach (GameSpeed r in new[] { GameSpeed.Pause, GameSpeed.Normal, GameSpeed.Double, GameSpeed.Triple, GameSpeed.Skip })
            {
                GameSpeed es, os; int el, ol; bool er, or;
                GfEa(r, true, true, GameSpeed.Normal, out es, out el, out er);
                GfOurs(r, true, true, GameSpeed.Normal, true, true, out os, out ol, out or);
                controls &= es == os && el == ol && er == or && el == GfLevel(es);
            }
            GfOurs(GameSpeed.Skip, false, false, GameSpeed.Double, true, false, out s, out lv, out run);
            controls &= s == GameSpeed.Double && lv == 2 && !run;
            return B582Line("speedskiplevel", on, "Skip with no head interaction runs Normal at level 1", fixedPure && sGfMissing.Length == 0,
                "EA sends level 3 for Normal (" + eaBug + "), real Skip and other speeds unchanged, no-actor level follows the kept speed", controls, sGfMissing, counters);
        }

        public static object SpeedSkipNoActorCheck(string what)
        {
            string counters = B582Counters("speedskipnoactor", 3408, new[] { "no-actor Skips run as Normal", "time frozen with switch off", "errors" }, sGfMissing);
            if (what == "result") return counters;
            if (what != "run") return "speedskipnoactor <run|result>";
            bool on = ShortcutOn("speedskipnoactor");
            GameSpeed s; int lv; bool run;
            GfEa(GameSpeed.Skip, false, false, GameSpeed.Double, out s, out lv, out run);
            bool eaBug = !run && s == GameSpeed.Double;   // frozen, still claims Double
            GfOurs(GameSpeed.Skip, false, false, GameSpeed.Double, false, on, out s, out lv, out run);
            bool fixedPure = on && run && s == GameSpeed.Normal && lv == 1;
            // controls: Skip with a head interaction stays Skip; Skip with an idle active Sim is EA's Normal fallback
            GameSpeed s2, s3; int l2, l3; bool r2, r3;
            GfOurs(GameSpeed.Skip, true, true, GameSpeed.Normal, false, true, out s2, out l2, out r2);
            GfOurs(GameSpeed.Skip, true, false, GameSpeed.Normal, false, true, out s3, out l3, out r3);
            bool controls = eaBug && s2 == GameSpeed.Skip && r2 && l2 == 3 && s3 == GameSpeed.Normal && r3;
            return B582Line("speedskipnoactor", on, "Skip with no active Sim falls back to Normal", fixedPure && sGfMissing.Length == 0,
                "EA freezes time (" + eaBug + "), Skip with a head interaction and the idle-Sim fallback unchanged", controls, sGfMissing, counters);
        }

        // ---- docs/609 §4 speedrecalclock ------------------------------------------------------------------------------------
        // [verified, ilspycmd + managed-callgraph callers] RecalculateGameSpeed() = SetGameSpeed(mGameSpeed, GameStates), which
        // skips the lock test and calls SetGameSpeed(.., lockSpeed: false): that clears mGameSpeedLocked and writes
        // sPersistedGameSpeed (saved) while a lock is held. Callers: PlayFlowState.Shutdown and EditTownState.Shutdown. A
        // thumbnail or MiniSimDescription Pause lock held across that shutdown is dropped: the holder's UnlockGameSpeed then
        // does nothing (mGameSpeedLocked false), the Pause it forced is saved as the player's speed, and the lock no longer
        // keeps other speed requests out.
        // Fix as EA intended: with a lock held, recalculate re-applies the current speed as a locked set (lockSpeed true):
        // the lock stays and the speed is not persisted (EA's own !lockSpeed test). Without a lock: EA's body.
        // Counters: [3411] recalculations kept the lock, [3412] lock cleared with the switch off, [3413] errors.
        static void ApplySpeedRecalcLock()
        {
            Patch(typeof(Gameflow), "RecalculateGameSpeed", Type.EmptyTypes, typeof(GfRecalcHook), "RecalcHook", "RecalcOrig");
        }

        // Pure: lock state and whether the speed is persisted after a recalculation. EA: unlocked, persisted.
        internal static void GfRecalc(bool locked, bool fixedOn, out bool lockedAfter, out bool persisted)
        {
            bool keep = fixedOn && locked;
            lockedAfter = keep;
            persisted = !keep;
        }

        public static class GfRecalcHook
        {
            public static void RecalcHook()
            {
                bool locked = false, keep = false;
                try
                {
                    locked = Gameflow.GameSpeedLocked;
                    bool lockedAfter, persisted;
                    GfRecalc(locked, ShortcutOn("speedrecalclock"), out lockedAfter, out persisted);
                    keep = lockedAfter && sGfSetSpeed != null;
                    if (locked) PatchCounters()[keep ? 3411 : 3412]++;
                }
                catch (ResetException) { throw; }
                catch (Exception) { PatchCounters()[3413]++; keep = false; }
                if (!keep) { RecalcOrig(); return; }
                // the live SetGameSpeed(GameSpeed, bool) (our GfSpeedHook when swapped), as a locked set
                try { sGfSetSpeed.Invoke(null, new object[] { Gameflow.CurrentGameSpeed, true }); }
                catch (TargetInvocationException e) { if (e.InnerException is ResetException) throw e.InnerException; PatchCounters()[3413]++; }
            }
            public static void RecalcOrig() { } // receives Gameflow.RecalculateGameSpeed
        }

        public static object SpeedRecalcLockCheck(string what)
        {
            string missing = EpJoin(sGfMissing, "");
            string counters = B582Counters("speedrecalclock", 3411, new[] { "recalculations kept the lock", "lock cleared with switch off", "errors" }, missing);
            if (what == "result") return counters;
            if (what != "run") return "speedrecalclock <run|result>";
            bool on = ShortcutOn("speedrecalclock");
            bool la, pe, la0, pe0, la1, pe1;
            GfRecalc(true, false, out la0, out pe0);
            bool eaBug = !la0 && pe0;
            GfRecalc(true, on, out la, out pe);
            bool fixedPure = on && la && !pe;
            GfRecalc(false, true, out la1, out pe1);
            bool controls = eaBug && !la1 && pe1;   // no lock held: EA's recalculation (unlocked, persisted)
            return B582Line("speedrecalclock", on, "recalculation keeps a held lock and does not persist", fixedPure && missing.Length == 0,
                "EA drops the lock and persists (" + eaBug + "), no lock held unchanged", controls, missing, counters);
        }

        // ---- docs/609 §5 speedlockowner -----------------------------------------------------------------------------------
        // [verified, ilspycmd LotManager] EnterBuildBuyMode locks Pause only when nobody holds a speed lock
        // (`if (!ClockSpeedModel.GameSpeedLocked) Gameflow.LockGameSpeed(Pause)`), but ExitBuildBuyMode always calls
        // Gameflow.UnlockGameSpeed(): when a thumbnail (ThumbnailHelper), MiniSimDescription.AddMiniSims or CAS held the lock at
        // enter, the exit releases that holder's lock and restores the speed that holder saved, under it.
        // Fix as EA intended: build/buy unlocks only if it took the lock itself. Enter is wrapped to remember whether its own
        // LockGameSpeed took the lock (free before, held after); ExitBuildBuyMode sleeps (Simulator.Sleep(0)), so it is not
        // swapped: RealEstateManager.OnExitBuildBuy (called only by ExitBuildBuyMode, three statements before the unlock; it can yield, but
        // nothing between its return and the unlock can: field sets and UnlockActiveLot)
        // arms the next UnlockGameSpeed on the same task, which skips the unlock when build/buy did not take the lock.
        // Counters: [3414] exit unlocks skipped (lock not ours), [3415] another holder's lock released with the switch off, [3416] errors.
        static FieldInfo sBbOn;
        static bool sBbOwns;            // build/buy's enter took the speed lock
        static ObjectGuid sBbExitTask;  // armed by OnExitBuildBuy: the next UnlockGameSpeed on this task is the exit's
        static bool sBbExitArmed;

        static void ApplySpeedLockOwner()
        {
            sBbOn = typeof(LotManager).GetField("sIsBuildBuyModeOn", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            Patch(typeof(LotManager), "EnterBuildBuyMode", new Type[] { typeof(Lot), typeof(bool) }, typeof(BbLockHook), "EnterHook", "EnterOrig");
            Patch(typeof(RealEstateManager), "OnExitBuildBuy", Type.EmptyTypes, typeof(BbLockHook), "ExitArmHook", "ExitArmOrig");
            Patch(typeof(Gameflow), "UnlockGameSpeed", Type.EmptyTypes, typeof(BbLockHook), "UnlockHook", "UnlockOrig");
        }

        // Pure: does build/buy's exit call EA's UnlockGameSpeed? EA: always.
        internal static bool BbUnlock(bool ownsLock, bool fixedOn)
        {
            return !fixedOn || ownsLock;
        }

        // Pure: does build/buy hold the lock after an enter? It took it now (free before, held after), or this enter is a
        // Buy <-> Build/Blueprint switch (build/buy already on: BuyModeState/BuildModeState.Shutdown skip ExitBuildBuyMode while
        // transitioning, and the next mode calls EnterBuildBuyMode again) and it still holds the lock its first enter took.
        // Without the switch case, the second enter saw its own lock as someone else's, and the exit to live mode never
        // unlocked: the game stayed paused with every time control dead (reported on v0.3.0).
        internal static bool BbOwns(bool lockedBefore, bool lockedAfter, bool modeSwitch, bool ownedBefore)
        {
            return lockedAfter && (!lockedBefore || (modeSwitch && ownedBefore));
        }

        public static class BbLockHook
        {
            public static void EnterHook(Lot lot, bool disableFunds)
            {
                bool before = Gameflow.GameSpeedLocked;
                bool modeSwitch = false;
                try { modeSwitch = sBbOn != null && (bool)sBbOn.GetValue(null); }
                catch (Exception) { PatchCounters()[3416]++; }
                sBbExitArmed = false;
                EnterOrig(lot, disableFunds);
                try { sBbOwns = BbOwns(before, Gameflow.GameSpeedLocked, modeSwitch, sBbOwns); }
                catch (Exception) { PatchCounters()[3416]++; }
            }
            public static void EnterOrig(Lot lot, bool disableFunds) { } // receives LotManager.EnterBuildBuyMode

            public static void ExitArmHook()
            {
                ExitArmOrig();
                sBbExitArmed = true;
                sBbExitTask = Simulator.CurrentTask;
            }
            public static void ExitArmOrig() { } // receives RealEstateManager.OnExitBuildBuy

            public static void UnlockHook()
            {
                bool unlock = true;
                if (sBbExitArmed)
                {
                    sBbExitArmed = false;
                    try
                    {
                        // the exit's own call: same task, build/buy already flagged off two statements earlier
                        bool exiting = sBbExitTask == Simulator.CurrentTask && (sBbOn == null || !(bool)sBbOn.GetValue(null));
                        if (exiting)
                        {
                            bool owns = sBbOwns;
                            sBbOwns = false;
                            unlock = BbUnlock(owns, ShortcutOn("speedlockowner"));
                            if (!owns && Gameflow.GameSpeedLocked) PatchCounters()[unlock ? 3415 : 3414]++;
                        }
                    }
                    catch (ResetException) { throw; }
                    catch (Exception) { PatchCounters()[3416]++; unlock = true; }
                }
                if (unlock) UnlockOrig();
            }
            public static void UnlockOrig() { } // receives Gameflow.UnlockGameSpeed
        }

        public static object SpeedLockOwnerCheck(string what)
        {
            string missing = EpLive(null, 0, "sIsBuildBuyModeOn", sBbOn, "static");
            string counters = B582Counters("speedlockowner", 3414, new[] { "exit unlocks skipped (not ours)", "other holder released with switch off", "errors" }, missing);
            if (what == "result") return counters;
            if (what != "run") return "speedlockowner <run|result>";
            bool on = ShortcutOn("speedlockowner");
            // a thumbnail held the lock at enter: build/buy did not take it
            bool owns = BbOwns(true, true, false, false);
            bool eaBug = !owns && BbUnlock(owns, false);
            bool fixedPure = on && !BbUnlock(owns, on);
            // controls: build/buy took the lock itself -> unlocked at exit either way; nobody locked and enter failed to -> not owned;
            // a Buy -> Build switch (second enter, own lock held) keeps ownership, so the exit to live mode still unlocks
            bool controls = eaBug && BbOwns(false, true, false, false) && BbUnlock(BbOwns(false, true, false, false), true) && BbUnlock(true, false)
                && !BbOwns(false, false, false, false) && BbUnlock(BbOwns(true, true, true, true), true) && !BbOwns(true, true, true, false);
            return B582Line("speedlockowner", on, "build/buy exit leaves another holder's lock", fixedPure && missing.Length == 0,
                "EA releases it (" + eaBug + "), build/buy's own lock still released", controls, missing, counters);
        }

        // ---- docs/609 §6 alarmzeroperiod ----------------------------------------------------------------------------------
        // [verified, ilspycmd AlarmManager] Simulate moves a fired repeating timer to its next time with
        //   p = SimClock.ConvertToTicks(RepeatLength, UnitOfTime); for (n = t + p; n < now; n += p) {}
        // ConvertToTicks truncates (0.625 ticks per sim-second, `(long)` cast; a UnitOfTime of 6 or 7 from the 3-bit field gives
        // 0), and the Timer constructor only replaces RepeatLength <= 0 with 2 s. A period under 1.6 sim-seconds (or a bad unit,
        // or a RepeatLength set later by a mod or a bad save) is p <= 0 and the loop never ends: the game hangs. EA's own callers
        // are at 2 s or more, so only mods and bad saves reach it. For p > 0 the loop is one step per missed period.
        // AlarmManager.Simulate is not swapped (an inline alarm may sleep, docs/141; see the alarmtime notes in HitchCause.cs):
        // the leaf Timer.get_YieldRequired, read once per fired timer right after it leaves the queue and before the loop,
        // moves the timer's time so EA's loop gives the same next time in no steps:
        //   p > 0:  t' = t + (K - 1) p, K = max(1, ceil((now - t) / p)): EA's loop from t' ends at t + K p = its answer from t.
        //   p <= 0: treated as 1 tick: the next time is now (the first t + k >= now, k >= 1, for a due t < now); t' = now - p
        //           makes EA's loop start at now and stop at once.
        // The getter is also read by the reset/dispose paths, only on timers they clear at once; a timer not due is left alone.
        // shortcut: `now` is the time at the leaf; Simulate's own `now` was taken before the pass, so only an inline alarm that
        // sleeps (EA-wrong, docs/141) earlier in the same pass can make them differ; then a p > 0 timer can land one period later
        // than EA's stale answer (which was already in the past). The load path (InternalFixLoadAlarms) divides by p and is not
        // covered (DivideByZeroException at load for p = 0; docs/609 §6.3).
        // This hook also carries the alarmtime stamp (HitchCause.cs) that used to swap the same getter.
        // Counters: [3417] periods <= 0 treated as 1 tick, [3418] catch-ups done by one division (moved >= 1 period),
        // [3419] repeating timers checked, [3420] errors.
        static Type sAzTimer;
        static FieldInfo sAzDue, sAzLen;
        static PropertyInfo sAzRepeating, sAzUnit;
        static string sAzMissing = "";

        static void ApplyAlarmZeroPeriod()
        {
            sAzTimer = typeof(AlarmManager).GetNestedType("Timer", BindingFlags.NonPublic);
            sAzDue = sAzTimer != null ? sAzTimer.GetField("AlarmDateAndTime") : null;
            sAzLen = sAzTimer != null ? sAzTimer.GetField("RepeatLength") : null;
            sAzRepeating = sAzTimer != null ? sAzTimer.GetProperty("Repeating") : null;
            sAzUnit = sAzTimer != null ? sAzTimer.GetProperty("UnitOfTime") : null;
            Patch(sAzTimer, "get_YieldRequired", Type.EmptyTypes, typeof(AzTimerHook), "YieldHook", "YieldOrig");   // a null type is refused and logged
            sAzMissing = EpLive(null, 0, "Timer", sAzTimer, null, "AlarmDateAndTime", sAzDue, typeof(DateAndTime), "RepeatLength", sAzLen, typeof(float),
                "Repeating", sAzRepeating, "get", "UnitOfTime", sAzUnit, "get");
        }

        // Pure: the time to give a fired repeating timer due at t (< now) with period p so EA's loop answers in no steps.
        internal static long AzStart(long t, long p, long now)
        {
            if (p <= 0) return now - p;                // treated as 1 tick: EA's loop starts at now
            if (now - t <= p) return t;                // one period reaches now: EA's loop does no step already
            long k = (now - t + p - 1) / p;            // ceil((now - t) / p) >= 2
            return t + (k - 1) * p;
        }

        // Pure: EA's catch-up loop from t, at most cap steps; -1 when it has not ended by then (p <= 0 never ends).
        internal static long AzEaLoop(long t, long p, long now, int cap, out int steps)
        {
            long n = t + p;
            for (steps = 0; n < now; n += p)
                if (++steps > cap) return -1;
            return n;
        }

        // the fix on one timer object (hook and check); returns the moved-by-ticks or -1 when nothing changed
        static long AzFix(object timer, long now)
        {
            if (!(bool)sAzRepeating.GetValue(timer, null)) return -1;
            int[] c = PatchCounters();
            c[3419]++;
            long t = ((DateAndTime)sAzDue.GetValue(timer)).Ticks;
            if (t >= now) return -1;   // not due: a reset or dispose path reading the getter
            long p = SimClock.ConvertToTicks((float)sAzLen.GetValue(timer), (TimeUnit)sAzUnit.GetValue(timer, null));
            long t2 = AzStart(t, p, now);
            if (t2 == t) return -1;
            sAzDue.SetValue(timer, new DateAndTime(t2));
            c[p <= 0 ? 3417 : 3418]++;
            return t2 - t;
        }

        // same signature as the target: a header swap hands callers the hook's
        public class AzTimerHook
        {
            public bool YieldHook()
            {
                if (sHcDue != null && ShortcutOn("alarmtime")) HcAlarmDue(this);   // HitchCause stamp (test packages only)
                if (ShortcutOn("alarmzeroperiod"))
                {
                    try { AzFix(this, SimClock.CurrentTime().Ticks); }
                    catch (ResetException) { throw; }
                    catch (Exception) { PatchCounters()[3420]++; }
                }
                return YieldOrig();
            }
            public bool YieldOrig() { return false; } // receives AlarmManager.Timer.get_YieldRequired
        }

        // AlarmZeroPeriodCheck run: EA's loop vs ours on periods <= 0 (EA never ends) and on a spread of p > 0 cases (same next
        // time, no steps), plus a live private Timer (never queued) built with a 1 s period, whose ConvertToTicks is 0.
        public static object AlarmZeroPeriodCheck(string what)
        {
            string counters = B582Counters("alarmzeroperiod", 3417, new[] { "periods <= 0 as 1 tick", "catch-ups by division", "repeating timers checked", "errors" }, sAzMissing);
            if (what == "result") return counters;
            if (what != "run") return "alarmzeroperiod <run|result>";
            bool on = ShortcutOn("alarmzeroperiod");
            string missing = sAzMissing, live = "not run";
            bool eaBug = false, fixedPure = false, controls = false, liveOk = false;
            try
            {
                int st;
                long now = 1000000;
                eaBug = AzEaLoop(now - 5, 0, now, 100000, out st) == -1 && AzEaLoop(now - 5, -3, now, 100000, out st) == -1
                    && SimClock.ConvertToTicks(1f, TimeUnit.Seconds) == 0;
                bool zero = AzEaLoop(AzStart(now - 5, 0, now), 0, now, 0, out st) == now && st == 0
                    && AzEaLoop(AzStart(now - 5, -3, now), -3, now, 0, out st) == now && st == 0
                    && AzEaLoop(now - 5, 1, now, 100000, out st) == now;   // = a 1-tick period
                bool same = true, ctl = true;
                long[] ps = { 1, 2, 3, 7, 37, 2250, 54000 };
                long[] gaps = { 1, 2, 3, 6, 7, 8, 36, 37, 38, 100, 2250, 2251, 99999, 5000000 };
                foreach (long p in ps)
                    foreach (long g in gaps)
                    {
                        long t = now - g;
                        int s0, s1;
                        long ea = AzEaLoop(t, p, now, 10000000, out s0);
                        long ours = AzEaLoop(AzStart(t, p, now), p, now, 0, out s1);
                        same &= ea == ours && s1 == 0 && ea >= now && ea - p < now;
                        if (g <= p) ctl &= AzStart(t, p, now) == t;   // no catch-up needed: time untouched
                    }
                fixedPure = on && zero && same;
                controls = eaBug && ctl && same;
                // live: a private Timer (never added to any AlarmManager) with RepeatLength 1 s (0 ticks), due 5 ticks ago
                if (sAzTimer != null && sAzDue != null)
                {
                    ConstructorInfo ci = sAzTimer.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                        new Type[] { typeof(long), typeof(AlarmTimerCallback), typeof(float), typeof(TimeUnit), typeof(AlarmHandle), typeof(string), typeof(AlarmType), typeof(IAlarmOwner) }, null);
                    if (ci == null) missing = EpJoin(missing, "Timer(long, ..) ctor missing");
                    else
                    {
                        object tm = ci.Invoke(new object[] { 0L, null, 1f, TimeUnit.Seconds, AlarmHandle.kInvalidHandle, "ts3fixes check", AlarmType.NeverPersisted, null });
                        long cur = SimClock.CurrentTime().Ticks;
                        sAzDue.SetValue(tm, new DateAndTime(cur - 5));
                        int before3417 = PatchCounters()[3417];
                        long moved = AzFix(tm, cur);
                        long due = ((DateAndTime)sAzDue.GetValue(tm)).Ticks;
                        PatchCounters()[3417] = before3417;   // the check's own timer is not counted
                        PatchCounters()[3419]--;
                        int sx;
                        liveOk = moved == 5 && AzEaLoop(due, 0, cur, 0, out sx) == cur;
                        live = "live 1 s timer: next " + (liveOk ? "= now" : "wrong (moved " + moved + ")");
                    }
                }
            }
            catch (Exception e) { missing = EpJoin(missing, "threw " + e.GetType().Name); }
            return B582Line("alarmzeroperiod", on, "period <= 0 ticks as 1 tick, catch-up in one division", fixedPure && liveOk && sAzMissing.Length == 0,
                "EA's loop never ends for p <= 0 (" + eaBug + "), same next time for p > 0, untouched when no catch-up, " + live, controls, missing, counters);
        }

        // ---- docs/609 §7 heartbrokenbcast ---------------------------------------------------------------------------------
        // [verified, ilspycmd SocialCallback + BuffHeartBroken + ReactionBroadcaster] SocialCallback.AddHeartBroken (breakup
        // callbacks) adds HeartBroken to the dumped Sim, then sets the instance's HeartBrokenBroadcaster to a new
        // ReactionBroadcaster on the dumper. When the Sim is already HeartBroken, AddElement re-adds onto the same
        // BuffInstanceHeartBroken, so the assignment drops the old broadcaster without Dispose. HeartBroken params never expire
        // (TimeDuration -1), and BuffInstanceHeartBroken.Dispose frees only the current one, so the old broadcaster stays
        // registered on the first ex's lot (Lot reaction + active-broadcaster lists) until that Sim is destroyed, pulsing
        // HeartBrokenReaction for any HeartBroken Sim near the first ex. EA's own Dispose path (BuffInstanceHeartBroken.Dispose)
        // shows the intent: Dispose, then drop.
        // Fix as EA intended: after EA's body, the broadcaster the instance held before is disposed when EA replaced it
        // (same as disposing it just before the assignment: nothing else references it). Household Sims included: the
        // current broadcaster and the buff stay as EA sets them; only the unreachable duplicate goes.
        // Counters: [3421] replaced broadcasters disposed, [3422] left registered with the switch off, [3423] errors.
        static string sHbMissing = "";

        static void ApplyHeartBrokenBcast()
        {
            Patch(typeof(SocialCallback), "AddHeartBroken", new Type[] { typeof(Sim), typeof(Sim), typeof(string), typeof(ActiveTopic), typeof(InteractionInstance) }, typeof(HbBcastHook), "AddHook", "AddOrig");
        }

        // Pure: dispose the broadcaster held before EA's body? Only one EA replaced (still there and not the current one).
        internal static bool HbDispose(bool hadOld, bool replaced, bool fixedOn)
        {
            return fixedOn && hadOld && replaced;
        }

        static ReactionBroadcaster HbHeld(Sim target)
        {
            if (target == null || target.BuffManager == null) return null;
            BuffHeartBroken.BuffInstanceHeartBroken bi = target.BuffManager.GetElement(BuffNames.HeartBroken) as BuffHeartBroken.BuffInstanceHeartBroken;
            return bi != null ? bi.HeartBrokenBroadcaster : null;
        }

        // the step after EA's body (hook and check): returns true when old was disposed
        static bool HbAfter(ReactionBroadcaster old, ReactionBroadcaster now, bool on)
        {
            bool replaced = old != null && !ReferenceEquals(old, now);
            if (!HbDispose(old != null, replaced, on))
            {
                if (replaced) PatchCounters()[3422]++;
                return false;
            }
            old.Dispose();
            PatchCounters()[3421]++;
            return true;
        }

        public static class HbBcastHook
        {
            public static void AddHook(Sim actor, Sim target, string interaction, ActiveTopic topic, InteractionInstance i)
            {
                ReactionBroadcaster old = null;
                try { old = HbHeld(target); }
                catch (Exception) { PatchCounters()[3423]++; }
                AddOrig(actor, target, interaction, topic, i);
                if (old == null) return;
                try { HbAfter(old, HbHeld(target), ShortcutOn("heartbrokenbcast")); }
                catch (ResetException) { throw; }
                catch (Exception) { PatchCounters()[3423]++; }
            }
            public static void AddOrig(Sim actor, Sim target, string interaction, ActiveTopic topic, InteractionInstance i) { } // receives SocialCallback.AddHeartBroken
        }

        // HeartBrokenBcastCheck run: pure decision with controls, plus a live replay on the selected Sim when there is one: two
        // broadcasters A then B on a private BuffInstanceHeartBroken (never added to a BuffManager), EA's overwrite, then our
        // step. Off: A still registered (BroadcastingObject set). On: A disposed, B kept. The check disposes both after.
        public static object HeartBrokenBcastCheck(string what)
        {
            string counters = B582Counters("heartbrokenbcast", 3421, new[] { "replaced broadcasters disposed", "left registered with switch off", "errors" }, sHbMissing);
            if (what == "result") return counters;
            if (what != "run") return "heartbrokenbcast <run|result>";
            bool on = ShortcutOn("heartbrokenbcast");
            bool eaBug = !HbDispose(true, true, false);
            bool fixedPure = on && HbDispose(true, true, on);
            // controls: first breakup (no old broadcaster) and an unchanged broadcaster are never disposed
            bool controls = eaBug && !HbDispose(false, false, true) && !HbDispose(true, false, true);
            string live = "no selected Sim (pure only)";
            bool liveOk = true;
            Sim s = Sim.ActiveActor;
            if (s != null && s.LotCurrent != null)
            {
                ReactionBroadcaster a = null, b = null;
                try
                {
                    int[] c = PatchCounters();
                    int c1 = c[3421], c2 = c[3422];
                    BuffHeartBroken.BuffInstanceHeartBroken bi = new BuffHeartBroken.BuffInstanceHeartBroken();
                    a = new ReactionBroadcaster(s, BuffHeartBroken.HeartBrokenParams, BuffHeartBroken.HeartBrokenReaction);
                    bi.HeartBrokenBroadcaster = a;
                    ReactionBroadcaster old = bi.HeartBrokenBroadcaster;
                    b = new ReactionBroadcaster(s, BuffHeartBroken.HeartBrokenParams, BuffHeartBroken.HeartBrokenReaction);
                    bi.HeartBrokenBroadcaster = b;   // EA's overwrite
                    HbAfter(old, bi.HeartBrokenBroadcaster, on);
                    c[3421] = c1; c[3422] = c2;      // the check's own broadcasters are not counted
                    bool aGone = a.BroadcastingObject == null, bKept = b.BroadcastingObject != null;
                    liveOk = bKept && (on ? aGone : !aGone);
                    live = "live replay on the selected Sim: old " + (aGone ? "disposed" : "still registered") + ", current " + (bKept ? "kept" : "lost");
                }
                catch (Exception e) { liveOk = false; live = "live replay threw " + e.GetType().Name; }
                finally
                {
                    try { if (a != null) a.Dispose(); } catch (Exception) { }
                    try { if (b != null) b.Dispose(); } catch (Exception) { }
                }
            }
            return B582Line("heartbrokenbcast", on, "a repeat breakup disposes the replaced heartbroken broadcaster", fixedPure && liveOk && sHbMissing.Length == 0,
                "EA leaves it registered (" + eaBug + "), first breakup and unchanged broadcaster untouched, " + live, controls && liveOk, sHbMissing, counters);
        }
    }
}
