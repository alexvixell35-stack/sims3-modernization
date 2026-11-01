using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;
using Sims3.SimIFace.CAS;

namespace Ts3Fixes
{
    // outfitwaitpoll (issue #115, docs/224): Sim.SwitchOutfitHelper.Wait sleeps one tick at a time until mReady, which only
    // the helper's own delayed swap sets (its Ready callback). Abort (another helper's Start on the same Sim,
    // MagicMirror.MagicalMakeover.Cleanup, ...) clears mReady and cancels that swap, and the loop does not look at mStarted
    // again, so a wait already in its loop sleeps one tick at a time for the rest of the session. #115: a magical-makeover
    // alarm (ChangeOutfitCB, a yielding alarm task) doing this cost 3.0 ms of sim thread per sim-minute on the day-406 save.
    // Fix: with the switch on, a wait that has run kOutfitWaitStaleMinutes sim-minutes reads mReady once per sim-minute
    // (37 ticks) instead of every tick. It still returns at the first read that sees mReady, as EA's does: a late ready
    // moves the outfit change by under a sim-minute, and a wait that never gets ready never returns, as in EA's game.
    // Off at entry: EA's Wait. The loop reads the switch again at each stale poll, so an in-load A/B moves waits that began
    // in an on arm back to EA's rate in the off arms. [680] waits gone stale, [681] minute polls, [682] stale waits ended.
    public static partial class Fixes
    {
        const float kOutfitWaitStaleMinutes = 60f;
        static readonly FieldInfo sSohReady = typeof(Sim.SwitchOutfitHelper).GetField("mReady", kInstAll);
        static readonly FieldInfo sSohStarted = typeof(Sim.SwitchOutfitHelper).GetField("mStarted", kInstAll);
        static readonly FieldInfo sSohAborted = typeof(Sim.SwitchOutfitHelper).GetField("mAborted", kInstAll);
        static readonly FieldInfo sSohDisposed = typeof(Sim.SwitchOutfitHelper).GetField("mDisposed", kInstAll);
        static readonly FieldInfo sSohNoChange = typeof(Sim.SwitchOutfitHelper).GetField("mNoChange", kInstAll);
        static readonly FieldInfo sSohSim = typeof(Sim.SwitchOutfitHelper).GetField("mSim", kInstAll);
        static readonly FieldInfo sSimActiveSoh = typeof(Sim).GetField("mActiveSwitchOutfitHelper", kInstAll);
        static readonly Type sAlarmOneShot = typeof(AlarmManager).GetNestedType("AlarmOneShot", BindingFlags.Public | BindingFlags.NonPublic);
        static readonly FieldInfo sOneShotTimer = sAlarmOneShot != null ? sAlarmOneShot.GetField("mTimer", kInstAll) : null;

        public class OutfitWaitHook
        {
            public void WaitHook(bool idleIfNecessary)
            {
                object h = this; // `this` is really a Sim.SwitchOutfitHelper
                if (sSohReady == null || sSohStarted == null || sSohDisposed == null || sSohNoChange == null || sSohSim == null || !ShortcutOn("outfitwaitpoll"))
                {
                    WaitOrig(idleIfNecessary);
                    return;
                }
                // EA's Wait up to its loop, in EA's order
                if ((bool)sSohDisposed.GetValue(h)) return;
                if (!(bool)sSohStarted.GetValue(h)) ((Sim.SwitchOutfitHelper)h).Start();
                if ((bool)sSohNoChange.GetValue(h)) return;
                if (idleIfNecessary && !(bool)sSohReady.GetValue(h)) ((Sim)sSohSim.GetValue(h)).LoopIdle();
                long t0 = SimClock.CurrentTicks, staleAfter = SimClock.ConvertToTicks(kOutfitWaitStaleMinutes, TimeUnit.Minutes);
                uint minute = (uint)SimClock.ConvertToTicks(1f, TimeUnit.Minutes);
                bool stale = false;
                while (!(bool)sSohReady.GetValue(h))
                {
                    long waited = SimClock.CurrentTicks - t0;
                    uint sleep = OutfitWaitCore.SleepTicks(waited, staleAfter, minute, waited >= staleAfter && ShortcutOn("outfitwaitpoll"));
                    if (sleep > 1)
                    {
                        if (!stale)
                        {
                            stale = true;
                            PatchCounters()[680]++;
                            Sim s = sSohSim.GetValue(h) as Sim;
                            string line = "outfitwaitpoll: " + (s != null ? s.FullName : "?") + " outfit wait not ready after "
                                + kOutfitWaitStaleMinutes + " sim-minutes (aborted " + (sSohAborted != null ? sSohAborted.GetValue(h) : "?")
                                + "), now read once per sim-minute; task " + OutfitWaitCaller();
                            ShortcutLog(line);
                            if (sOwLines.Count < 20) sOwLines.Add(line); // the 40-line recent log is soon full of other shortcuts
                        }
                        PatchCounters()[681]++;
                    }
                    Simulator.Sleep(sleep);
                }
                if (stale) PatchCounters()[682]++;
            }
            public void WaitOrig(bool idleIfNecessary) { } // receives Sim.SwitchOutfitHelper.Wait's original header
        }

        // the waiting task, and for a yielding alarm the callback it runs (the #115 case: an AlarmManager one-shot)
        static string OutfitWaitCaller()
        {
            try
            {
                IScriptProxy p = Simulator.GetProxy(Simulator.CurrentTask);
                object t = p != null ? p.Target : null;
                if (t == null) return "?";
                object timer = sOneShotTimer != null && sAlarmOneShot.IsInstanceOfType(t) ? sOneShotTimer.GetValue(t) : null; // AlarmManager.Timer is private
                FieldInfo cbf = timer != null ? timer.GetType().GetField("CallBack", kInstAll) : null;
                Delegate cb = cbf != null ? cbf.GetValue(timer) as Delegate : null;
                if (cb != null) return "alarm " + cb.Method.DeclaringType.FullName + "." + cb.Method.Name;
                return t.ToString();
            }
            catch (Exception e) { return "? (" + e.GetType().Name + ")"; }
        }

        static Sim.SwitchOutfitHelper sOwHelper;
        static ObjectGuid sOwWait = ObjectGuid.InvalidObjectGuid, sOwAbort = ObjectGuid.InvalidObjectGuid;
        static string sOwAbortSaw, sOwWaitEnd; // sOwWaitEnd null = task 1 still in Wait (GetProxy is null for a script Task)
        static readonly List<string> sOwLines = new List<string>(); // first 20 stale-wait lines this session (check "log")

        // Agent checks for #115 (repro outfit-wait-stale-check), EA's abort-in-the-loop path. The test Sim's outfit does not
        // change: its swap is cancelled before it completes, and "ready" ends the wait without ChangeOutfit.
        //   "start": a Sim in the world in a normal outfit with no outfit helper of its own (an active-household one first)
        //            gets a helper for an outfit category it is not wearing; task 1 runs Wait(false) (Start, then the loop),
        //            task 2 aborts the helper in the same tick, after Start and before Ready (what another helper's Start does).
        //   "state": what task 2 saw, the helper's flags, whether task 1 still runs (else how its Wait ended), counters [680-682].
        //   "log":   the first 20 stale-wait lines of the session (Sim, aborted flag, waiting task).
        //   "ready": sets mReady, as a late Ready callback would; on, task 1 returns within a sim-minute; off, within a tick.
        //   "stop":  destroys both tasks.
        public static string OutfitWaitCheck(string what)
        {
            switch (what)
            {
                case "start":
                {
                    if (sOwHelper != null) return "a test wait exists; stop first";
                    if (sSohReady == null || sSohStarted == null || sSimActiveSoh == null) return "SwitchOutfitHelper fields not found";
                    Sim sim = null;
                    OutfitCategories[] kinds = { OutfitCategories.Sleepwear, OutfitCategories.Everyday, OutfitCategories.Formalwear, OutfitCategories.Athletic };
                    // a Sim in a special outfit (towel, costume) may not change (a 2026-10-31 run's wait ended at once): any Sim
                    // in the world in a normal outfit with no helper of its own, an active-household one first
                    foreach (Sim c in Sims3.Gameplay.Queries.GetObjects<Sim>())
                        if (c.InWorld && c.SimDescription != null && sSimActiveSoh.GetValue(c) == null && Array.IndexOf(kinds, c.CurrentOutfitCategory) >= 0
                            && (sim == null || (c.Household == Household.ActiveHousehold && sim.Household != Household.ActiveHousehold))) sim = c;
                    if (sim == null) return "no Sim in the world in a normal outfit without an outfit helper";
                    OutfitCategories cur = sim.CurrentOutfitCategory, pick = OutfitCategories.None;
                    foreach (OutfitCategories k in kinds)
                        if (k != cur && sim.SimDescription.GetOutfitCount(k) > 0) { pick = k; break; }
                    if (pick == OutfitCategories.None) return sim.FullName + ": no other outfit category";
                    Sim.SwitchOutfitHelper helper = sOwHelper = new Sim.SwitchOutfitHelper(sim, pick, 0);
                    sOwAbortSaw = "not run"; sOwWaitEnd = null;
                    sOwWait = Simulator.AddObject(new LoopTask(delegate
                    {
                        try { helper.Wait(false); sOwWaitEnd = "returned"; }
                        catch (Exception e) { sOwWaitEnd = "threw " + e.GetType().Name; }
                        sOwWaitEnd += " (aborted " + (sSohAborted != null ? sSohAborted.GetValue(helper) : "?") + ", no change " + sSohNoChange.GetValue(helper)
                            + ", disposed " + sSohDisposed.GetValue(helper) + ")";
                    }));
                    sOwAbort = Simulator.AddObject(new LoopTask(delegate
                    {
                        while (!(bool)sSohStarted.GetValue(helper)) Simulator.Sleep(1);
                        if ((bool)sSohReady.GetValue(helper)) { sOwAbortSaw = "ready before the abort (swap was immediate); stop and start again"; return; }
                        helper.Abort();
                        sOwAbortSaw = "aborted in the loop";
                    }));
                    return sim.FullName + ": waiting for " + pick + " (wearing " + cur + "), switch " + ShortcutOn("outfitwaitpoll");
                }
                case "state":
                {
                    if (sOwHelper == null) return "start first";
                    int[] c = PatchCounters();
                    return "abort task: " + sOwAbortSaw + "; ready " + sSohReady.GetValue(sOwHelper) + ", started " + sSohStarted.GetValue(sOwHelper)
                        + ", wait task " + (sOwWaitEnd == null ? "in Wait" : "returned: " + sOwWaitEnd) + "; stale " + c[680] + ", minute polls " + c[681] + ", ended " + c[682];
                }
                case "log":
                    return sOwLines.Count + " stale-wait lines: " + string.Join(" | ", sOwLines.ToArray());
                case "ready":
                    if (sOwHelper == null) return "start first";
                    sSohReady.SetValue(sOwHelper, true);
                    return "mReady set; " + OutfitWaitCheck("state");
                case "stop":
                    if (sOwWait != ObjectGuid.InvalidObjectGuid && Simulator.GetProxy(sOwWait) != null) Simulator.DestroyObject(sOwWait);
                    if (sOwAbort != ObjectGuid.InvalidObjectGuid && Simulator.GetProxy(sOwAbort) != null) Simulator.DestroyObject(sOwAbort);
                    sOwWait = sOwAbort = ObjectGuid.InvalidObjectGuid;
                    sOwHelper = null;
                    return "stopped";
                default: return "unknown check " + what + " (start, state, log, ready, stop)";
            }
        }
    }
}
