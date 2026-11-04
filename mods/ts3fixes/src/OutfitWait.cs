using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
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
    // Each stale wait is classed once (OutfitWaitCore.StaleCause, docs/224 §5): [2003] own swap still pending, [2004] replaced
    // by another delayed swap of the Sim, [2005] nothing pending (aborted waits are the #115 cause and are not counted here).
    // [2011] of those, waits whose helper's swap callback EA dropped (SwapCbHook below).
    // Whether the native swap needs the Sim in view (docs/224 §5): [2012] stale waits whose Sim was on screen when the wait
    // went stale, [2013] stale waits whose Sim was on screen when the wait ended; the end is logged (first 10 lines).
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
        static readonly FieldInfo sSohCategory = typeof(Sim.SwitchOutfitHelper).GetField("mCategory", kInstAll);
        static readonly FieldInfo sSohIndex = typeof(Sim.SwitchOutfitHelper).GetField("mIndex", kInstAll);
        static readonly FieldInfo sSimPending = typeof(Sim).GetField("mPendingOutfitInfo", kInstAll);
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
                                + "), now read once per sim-minute; " + ViewText(s, 2012) + "; task " + OutfitWaitCaller() + "; " + OutfitWaitCause(h, s);
                            ShortcutLog(line);
                            if (sOwLines.Count < 20) sOwLines.Add(line); // the 40-line recent log is soon full of other shortcuts
                        }
                        PatchCounters()[681]++;
                    }
                    Simulator.Sleep(sleep);
                }
                if (stale)
                {
                    PatchCounters()[682]++;
                    Sim s = sSohSim.GetValue(h) as Sim;
                    string view = ViewText(s, 2013);
                    if (sOwEndLines++ < 10)
                    {
                        string line = "outfitwaitpoll: " + (s != null ? s.FullName : "?") + " stale outfit wait ended after "
                            + SimClock.ConvertFromTicks(SimClock.CurrentTicks - t0, TimeUnit.Minutes).ToString("0") + " sim-minutes; " + view;
                        ShortcutLog(line);
                        if (sOwLines.Count < 30) sOwLines.Add(line);
                    }
                }
            }
            public void WaitOrig(bool idleIfNecessary) { } // receives Sim.SwitchOutfitHelper.Wait's original header
        }

        static int sOwEndLines;
        // on screen / near the camera; counts slot when on screen
        static string ViewText(Sim s, int slot)
        {
            if (s == null) return "view ?";
            try
            {
                bool on = World.IsObjectOnScreen(s.ObjectId);
                if (on) PatchCounters()[slot]++;
                return "on screen " + on + ", far from camera " + FarFromViewers(s.Position) + ", on active lot "
                    + (s.LotCurrent != null && s.LotCurrent == LotManager.ActiveLot);
            }
            catch (Exception e) { return "view ? (" + e.GetType().Name + ")"; }
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

        // docs/224 §5: why a wait is stale, counted once per wait ([2003-2005]); the text goes on the stale-wait line
        static string OutfitWaitCause(object h, Sim s)
        {
            try
            {
                if (s == null || sSimPending == null || sSohCategory == null || sSohIndex == null) return "cause unread";
                object pend = sSimPending.GetValue(s); // Sim.OutfitInfo (a struct): Category, Index
                OutfitCategories pc = (OutfitCategories)pend.GetType().GetField("Category").GetValue(pend);
                int pi = (int)pend.GetType().GetField("Index").GetValue(pend);
                OutfitCategories hc = (OutfitCategories)sSohCategory.GetValue(h);
                int hi = (int)sSohIndex.GetValue(h);
                object act = sSimActiveSoh != null ? sSimActiveSoh.GetValue(s) : null;
                bool aborted = sSohAborted != null && (bool)sSohAborted.GetValue(h);
                int cause = OutfitWaitCore.StaleCause(aborted, object.ReferenceEquals(act, h), (int)pc, pi, (int)hc, hi, (int)OutfitCategories.None);
                if (cause != OutfitWaitCore.kCauseAborted) PatchCounters()[2002 + cause]++;
                Delegate dcb;
                bool dropped = sCbDropped.TryGetValue(s.ObjectId.Value, out dcb) && dcb != null && object.ReferenceEquals(dcb.Target, h);
                if (dropped && cause != OutfitWaitCore.kCauseAborted) PatchCounters()[2011]++;
                Sims3.Gameplay.Interactions.InteractionInstance cur = s.CurrentInteraction;
                bool tf = false;
                try { tf = s.BuffManager != null && s.BuffManager.HasTransformBuff(); } catch (Exception) { }
                return "cause " + new string[] { "aborted", "own", "replaced", "cleared" }[cause] + ": pending " + pc + "/" + pi + " (helper " + hc + "/" + hi
                    + "), callback dropped " + dropped + ", active helper " + (act == null ? "none" : object.ReferenceEquals(act, h) ? "this" : "other") + ", Sim doing "
                    + (cur != null ? cur.GetType().Name : "nothing") + ", transform buff " + tf + ", in world " + s.InWorld + ", by " + Callers();
            }
            catch (Exception e) { return "cause unread (" + e.GetType().Name + ")"; }
        }

        static Sim.SwitchOutfitHelper sOwHelper;
        static ObjectGuid sOwWait = ObjectGuid.InvalidObjectGuid, sOwAbort = ObjectGuid.InvalidObjectGuid;
        // docs/224 §5, the cleared cause: calls that reset a Sim's pending outfit while its active helper is started, not
        // ready and not aborted. Switch on only; logged with the callers. [2008] instant switches
        // (SwitchToOutfitWithoutSpin with an outfit), [2009] delayed swaps completed by another path (DoOutfitDelayedSwap;
        // the helper's own ChangeOutfit runs it only after Ready, so it is not counted).
        public class OutfitClearHook
        {
            public void InstantHook(OutfitCategories category, SimOutfit outfit, int index)
            {
                try { ClearSeen((Sim)(object)this, 2008, "instant switch to " + category); } catch (Exception) { } // logging only: never stop EA's call
                InstantOrig(category, outfit, index);
            }
            public void InstantOrig(OutfitCategories category, SimOutfit outfit, int index) { } // receives EA's original header
            public void DoneHook()
            {
                try { ClearSeen((Sim)(object)this, 2009, "delayed swap completed by another path"); } catch (Exception) { } // logging only: never stop EA's call
                DoneOrig();
            }
            public void DoneOrig() { } // receives EA's original header
        }

        // docs/224 §5, I-414-8: CASUtils.RegisterCallback (SimIFace) keeps one swap-ready callback per object. A second delayed
        // swap of the Sim while the first callback is still registered does not register its own callback: when the swap is
        // ready only the first one runs, and a caller waiting on the second waits forever. Switch on only, EA's call runs
        // unchanged. [2010] callbacks dropped, each logged with both callbacks and the callers (first 8 lines).
        static readonly FieldInfo sCasSwapCbs = typeof(CASUtils).GetField("sClothingSwapDelegates", BindingFlags.Static | BindingFlags.NonPublic);
        static readonly Dictionary<ulong, Delegate> sCbDropped = new Dictionary<ulong, Delegate>(); // the last dropped callback per object
        static int sCbLineCount;
        public class SwapCbHook
        {
            public static void RegisterHook(ObjectGuid objectId, CASUtils.SwapReadyCallback finishedCallback)
            {
                if (ShortcutOn("outfitwaitpoll")) CbDropSeen(objectId, finishedCallback);
                RegisterOrig(objectId, finishedCallback);
            }
            public static void RegisterOrig(ObjectGuid objectId, CASUtils.SwapReadyCallback finishedCallback) { } // receives CASUtils.RegisterCallback's original header
        }

        static void CbDropSeen(ObjectGuid id, CASUtils.SwapReadyCallback cb)
        {
            try
            {
                Dictionary<ulong, CASUtils.SwapReadyCallback> d = sCasSwapCbs != null ? sCasSwapCbs.GetValue(null) as Dictionary<ulong, CASUtils.SwapReadyCallback> : null;
                CASUtils.SwapReadyCallback old;
                if (d == null || !d.TryGetValue(id.Value, out old)) return;
                PatchCounters()[2010]++;
                if (sCbDropped.Count > 1000) sCbDropped.Clear(); // shortcut: bounded; only the dropped mark of older waits is lost
                sCbDropped[id.Value] = cb;
                if (sCbLineCount >= 8) return;
                sCbLineCount++;
                IScriptProxy p = Simulator.GetProxy(id);
                Sim sim = p != null ? p.Target as Sim : null;
                string line = "outfitwaitpoll: " + (sim != null && sim.SimDescription != null ? sim.SimDescription.FullName : "object " + id.Value)
                    + " swap callback dropped: new " + CbText(cb) + ", kept " + CbText(old) + ", by " + Callers();
                ShortcutLog(line);
                if (sOwLines.Count < 20) sOwLines.Add(line);
            }
            catch (Exception) { }
        }

        static string CbText(Delegate cb)
        {
            if (cb == null) return "null";
            string s = cb.Method.DeclaringType.Name + "." + cb.Method.Name;
            object t = cb.Target;
            if (t is Sim.SwitchOutfitHelper && sSohStarted != null && sSohReady != null && sSohDisposed != null)
                s += " (helper started " + sSohStarted.GetValue(t) + ", ready " + sSohReady.GetValue(t) + ", aborted "
                    + (sSohAborted != null ? sSohAborted.GetValue(t) : "?") + ", disposed " + sSohDisposed.GetValue(t) + ")";
            return s;
        }

        static void ClearSeen(Sim sim, int slot, string what)
        {
            if (sSimActiveSoh == null || sSohStarted == null || sSohReady == null || !ShortcutOn("outfitwaitpoll")) return;
            object h = sSimActiveSoh.GetValue(sim);
            if (h == null || !(bool)sSohStarted.GetValue(h) || (bool)sSohReady.GetValue(h) || (sSohAborted != null && (bool)sSohAborted.GetValue(h))) return;
            PatchCounters()[slot]++;
            string line = "outfitwaitpoll: " + (sim.SimDescription != null ? sim.SimDescription.FullName : "?") + " " + what + " while its helper waits for "
                + (sSohCategory != null ? sSohCategory.GetValue(h) : "?") + "; pending was " + PendingText(sim) + ", Sim doing "
                + (sim.CurrentInteraction != null ? sim.CurrentInteraction.GetType().Name : "nothing") + ", by " + Callers();
            ShortcutLog(line);
            if (sOwLines.Count < 20) sOwLines.Add(line);
        }

        static string PendingText(Sim sim)
        {
            object pi = sSimPending != null ? sSimPending.GetValue(sim) : null;
            if (pi == null) return "?";
            FieldInfo c = pi.GetType().GetField("Category", kInstAll | BindingFlags.Public), i = pi.GetType().GetField("Index", kInstAll | BindingFlags.Public);
            return (c != null ? c.GetValue(pi) : "?") + "/" + (i != null ? i.GetValue(pi) : "?");
        }

        static bool sOwReplReady; // the replace check's second swap called back
        static string sOwAbortSaw, sOwWaitEnd; // sOwWaitEnd null = task 1 still in Wait (GetProxy is null for a script Task)
        static readonly List<string> sOwLines = new List<string>(); // first 20 stale-wait lines this session (check "log")

        // Agent checks for #115 (repro outfit-wait-stale-check), EA's abort-in-the-loop path. The test Sim's outfit does not
        // change: its swap is cancelled before it completes, and "ready" ends the wait without ChangeOutfit.
        //   "start": a Sim in the world in a normal outfit with no outfit helper of its own (an active-household one first)
        //            gets a helper for an outfit category it is not wearing; task 1 runs Wait(false) (Start, then the loop),
        //            task 2 aborts the helper in the same tick, after Start and before Ready (what another helper's Start does).
        //   "start replace": as "start", but task 2 starts a second delayed swap of the same Sim (a third category, its own
        //            callback) instead of the abort, as SimBuilderTask.FinishModelLoad, BuffTransformation and
        //            Sim.SwitchToOutfitWithHiding do without aborting the active helper (docs/224 §5). "state" then says which
        //            ready callbacks ran. "start control": as "start" with no task 2 (the swap should be ready in seconds).
        //   "start clear": as "start", but task 2 switches the Sim to a third category at once (SwitchToOutfitWithoutSpin with
        //            an outfit), which resets the pending outfit without the helper: the "cleared" stale cause (docs/224 §5).
        //   "state": what task 2 saw, the helper's flags, whether task 1 still runs (else how its Wait ended), counters [680-682].
        //   "log":   the first 20 stale-wait lines of the session (Sim, aborted flag, waiting task).
        //   "ready": sets mReady, as a late Ready callback would; on, task 1 returns within a sim-minute; off, within a tick.
        //   "stop":  destroys both tasks and aborts the helper (which cancels a pending swap; neither swap is completed).
        public static string OutfitWaitCheck(string what)
        {
            switch (what)
            {
                case "start":
                case "start replace":
                case "start clear":
                case "start control":
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
                    OutfitCategories third = OutfitCategories.None;
                    foreach (OutfitCategories k in kinds)
                        if (k != cur && k != pick && sim.SimDescription.GetOutfitCount(k) > 0) { third = k; break; }
                    if ((what == "start replace" || what == "start clear") && third == OutfitCategories.None) return sim.FullName + ": no third outfit category";
                    Sim.SwitchOutfitHelper helper = sOwHelper = new Sim.SwitchOutfitHelper(sim, pick, 0);
                    sOwAbortSaw = what == "start control" ? "none (control)" : "not run"; sOwWaitEnd = null; sOwReplReady = false;
                    sOwWait = Simulator.AddObject(new LoopTask(delegate
                    {
                        try { helper.Wait(false); sOwWaitEnd = "returned"; }
                        catch (Exception e) { sOwWaitEnd = "threw " + e.GetType().Name; }
                        sOwWaitEnd += " (aborted " + (sSohAborted != null ? sSohAborted.GetValue(helper) : "?") + ", no change " + sSohNoChange.GetValue(helper)
                            + ", disposed " + sSohDisposed.GetValue(helper) + ")";
                    }));
                    Sim simR = sim;
                    if (what != "start control")
                        sOwAbort = Simulator.AddObject(new LoopTask(delegate
                        {
                            while (!(bool)sSohStarted.GetValue(helper)) Simulator.Sleep(1);
                            if ((bool)sSohReady.GetValue(helper)) { sOwAbortSaw = "ready before task 2 (swap was immediate); stop and start again"; return; }
                            if (what == "start replace")
                            {
                                bool ok = simR.SwitchToOutfitDelayed(third, 0, new CASUtils.SwapReadyCallback(delegate { sOwReplReady = true; }));
                                sOwAbortSaw = "second swap to " + third + " started " + ok + " (helper not aborted)";
                                return;
                            }
                            if (what == "start clear")
                            {
                                simR.SwitchToOutfitWithoutSpin(third, simR.SimDescription.GetOutfit(third, 0), 0);
                                sOwAbortSaw = "instant switch to " + third + " (helper not aborted, pending now " + PendingText(simR) + ")";
                                return;
                            }
                            helper.Abort();
                            sOwAbortSaw = "aborted in the loop";
                        }));
                    return sim.FullName + ": waiting for " + pick + " (wearing " + cur + ")" + (what == "start replace" ? ", second swap " + third : what == "start clear" ? ", instant switch " + third : "") + ", switch " + ShortcutOn("outfitwaitpoll");
                }
                case "state":
                {
                    if (sOwHelper == null) return "start first";
                    int[] c = PatchCounters();
                    return "abort task: " + sOwAbortSaw + "; ready " + sSohReady.GetValue(sOwHelper) + ", started " + sSohStarted.GetValue(sOwHelper)
                        + ", wait task " + (sOwWaitEnd == null ? "in Wait" : "returned: " + sOwWaitEnd) + ", second swap ready " + sOwReplReady
                        + "; stale " + c[680] + ", minute polls " + c[681] + ", ended " + c[682] + "; causes own " + c[2003] + ", replaced " + c[2004] + ", cleared " + c[2005]
                        + "; callbacks dropped " + c[2010] + ", stale with a dropped callback " + c[2011];
                }
                case "log":
                    return sOwLines.Count + " stale-wait lines: " + string.Join(" | ", sOwLines.ToArray());
                case "counts": // no start needed (repro outfit-wait-view-log)
                {
                    int[] c = PatchCounters();
                    return "switch " + ShortcutOn("outfitwaitpoll") + "; stale " + c[680] + ", minute polls " + c[681] + ", ended " + c[682]
                        + "; causes own " + c[2003] + ", replaced " + c[2004] + ", cleared " + c[2005] + "; callbacks dropped " + c[2010]
                        + ", stale with a dropped callback " + c[2011] + "; on screen when stale " + c[2012] + ", on screen when ended " + c[2013];
                }
                case "ready":
                    if (sOwHelper == null) return "start first";
                    sSohReady.SetValue(sOwHelper, true);
                    return "mReady set; " + OutfitWaitCheck("state");
                case "stop":
                    if (sOwWait != ObjectGuid.InvalidObjectGuid && Simulator.GetProxy(sOwWait) != null) Simulator.DestroyObject(sOwWait);
                    if (sOwAbort != ObjectGuid.InvalidObjectGuid && Simulator.GetProxy(sOwAbort) != null) Simulator.DestroyObject(sOwAbort);
                    sOwWait = sOwAbort = ObjectGuid.InvalidObjectGuid;
                    if (sOwHelper != null) sOwHelper.Abort(); // cancels a swap still pending (the replace check's second one too)
                    sOwHelper = null;
                    return "stopped";
                default: return "unknown check " + what + " (start, start replace, start clear, start control, state, log, ready, stop)";
            }
        }
    }
}
