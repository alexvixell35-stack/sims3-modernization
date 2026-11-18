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
    // outfitwaitend (docs/583 §1, kOutfitWaitEnd, off by default): the native swap of a Sim with no animation instance (off
    // screen, low LOD) is re-posted until the Sim is drawn (docs/224 §5), so EA's loop holds the Sim's task for hours: a
    // Bridgeport NPC sat 18 sim-hours in "Switch To Outfit", another 11 h in a meet-up route's GoingOffLot change, motives
    // frozen. With the switch on, a live NPC's own-cause wait (pending outfit still the helper's) past kOutfitWaitEndMinutes,
    // off screen and not inside the helper's ChangeOutfit, is ended the way EA ends a change it does not animate: Abort
    // (EA's cancel of the delayed swap), then Sim.SwitchToOutfitWithoutSpin(category, index) (EA's instant switch), the
    // laundry and carried-toddler steps of ChangeOutfit, and mNoChange set so the caller's spin and ChangeOutfit do nothing.
    // Household, selected, selectable and unknown-scope Sims keep EA's loop. Read once per sim-minute past the mark.
    // [2124] waits ended, [2125] waits that would end with the switch off, [2126] waits past the mark kept (on screen, other
    // cause, household, inside ChangeOutfit; once per wait), [2127] errors.
    // Household rule (docs/580 §15): a live player Sim (IsPlayerSim: household, selected, selectable, unknown) keeps EA's
    // per-tick loop past the mark; only NPC and gone-Sim waits are polled. [3544] waits kept on EA's loop by the rule.
    // outfitwaitend (docs/601 §1, kOutfitWaitEnd, off by default): the native swap of a Sim with no animation instance (off
    // screen, low LOD) is re-posted until the Sim is drawn (docs/224 §5), so EA's loop holds the Sim's task for hours: a
    // Bridgeport NPC sat 18 sim-hours in "Switch To Outfit", another 11 h in a meet-up route's GoingOffLot change, motives
    // frozen. With the switch on, a live NPC's own-cause wait (pending outfit still the helper's) past kOutfitWaitEndMinutes,
    // off screen and not inside the helper's ChangeOutfit, is ended the way EA ends a change it does not animate: Abort
    // (EA's cancel of the delayed swap), then Sim.SwitchToOutfitWithoutSpin(category, index) (EA's instant switch), the
    // laundry and carried-toddler steps of ChangeOutfit, and mNoChange set so the caller's spin and ChangeOutfit do nothing.
    // Household, selected, selectable and unknown-scope Sims keep EA's loop. Read once per sim-minute past the mark.
    // [2124] waits ended, [2125] waits that would end with the switch off, [2126] waits past the mark kept (on screen, other
    // cause, household, inside ChangeOutfit; once per wait), [2127] errors.
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
        // Sim.OutfitInfo (the pending field's struct): Category, Index
        static readonly FieldInfo sOiCategory = sSimPending != null ? sSimPending.FieldType.GetField("Category", kInstAll) : null;
        static readonly FieldInfo sOiIndex = sSimPending != null ? sSimPending.FieldType.GetField("Index", kInstAll) : null;
        static readonly Type sAlarmOneShot = typeof(AlarmManager).GetNestedType("AlarmOneShot", BindingFlags.Public | BindingFlags.NonPublic);
        static readonly FieldInfo sOneShotTimer = sAlarmOneShot != null ? sAlarmOneShot.GetField("mTimer", kInstAll) : null;

        public class OutfitWaitHook
        {
            public void WaitHook(bool idleIfNecessary)
            {
                object h = this; // `this` is really a Sim.SwitchOutfitHelper
                if (sSohReady == null || sSohStarted == null || sSohDisposed == null || sSohNoChange == null || sSohSim == null
                    || (!ShortcutOn("outfitwaitpoll") && !ShortcutOn("outfitwaitend")))
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
                long endAfter = SimClock.ConvertToTicks(Instantiator.kOutfitWaitEndMinutes, TimeUnit.Minutes), endNext = endAfter;
                uint minute = (uint)SimClock.ConvertToTicks(1f, TimeUnit.Minutes);
                bool stale = false, hhKept = false, endKept = false, endSeenOff = false;
                while (!(bool)sSohReady.GetValue(h))
                {
                    long waited = SimClock.CurrentTicks - t0;
                    if (waited >= endNext)
                    {
                        endNext = waited + minute;  // once per sim-minute past the mark
                        if (OutfitWaitEndTry(h, waited, endAfter, ref endKept, ref endSeenOff)) return;
                    }
                    bool poll = waited >= staleAfter && ShortcutOn("outfitwaitpoll");
                    if (poll && !OutfitWaitPollable(h))
                    {
                        poll = false;
                        if (!hhKept) { hhKept = true; PatchCounters()[3544]++; }
                    }
                    uint sleep = OutfitWaitCore.SleepTicks(waited, staleAfter, minute, poll);
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
                        if (OutfitWaitDead(h)) throw new ResetException(); // what Simulator.Sleep throws when the task is reset
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

        // issue #40: a stale wait whose helper's Sim is gone (null or destroyed; e.g. MagicalMakeover.Cleanup aborted the helper
        // under the makeover alarm, then the NPC was destroyed) can never be ready, and its task holds the dead Sim and its graph.
        // It ends as a task reset during the loop's Sleep would (EA's callers already handle that ResetException; the alarm's
        // OneShotFunction drops it), so ChangeOutfit's tail never runs on the dead Sim. A live Sim is never touched, and a wait
        // on a task that belongs to another Sim (household or not) keeps EA's loop. [933] dead-Sim waits ended.
        static bool OutfitWaitDead(object h)
        {
            Sim s = sSohSim.GetValue(h) as Sim;
            bool gone = s == null || s.HasBeenDestroyed;
            if (!gone) return false;
            IScriptProxy p = Simulator.GetProxy(Simulator.CurrentTask);
            Sim w = p != null ? p.Target as Sim : null;
            if (!OutfitWaitCore.EndDead(gone, w != null && !object.ReferenceEquals(w, s))) return false;
            PatchCounters()[933]++;
            if (sOwEndLines++ < 10)
            {
                string name = "?";
                try { if (s != null && s.SimDescription != null) name = s.SimDescription.FullName; } catch (Exception) { }
                string line = "outfitwaitpoll: " + name + " outfit wait ended: its Sim is destroyed (aborted "
                    + (sSohAborted != null ? sSohAborted.GetValue(h) : "?") + "); task " + OutfitWaitCaller();
                ShortcutLog(line);
                if (sOwLines.Count < 30) sOwLines.Add(line);
            }
            return true;
        }

        // docs/580 §15: scope check before the minute poll (read at each wake past the mark, so a Sim that joins the household
        // mid-wait goes back to EA's per-tick loop). A household, selected, selectable or unknown-scope live Sim is exempt;
        // a gone Sim is not (the issue #40 dead-wait end needs the poll path). [3544] waits kept on EA's loop by the rule
        // (counted once per wait in WaitHook).
        static bool OutfitWaitPollable(object h)
        {
            Sim s = sSohSim.GetValue(h) as Sim;
            bool live = s != null && !s.HasBeenDestroyed;
            return OutfitWaitCore.PollAllowed(live, live && IsPlayerSim(s));
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
                if (s == null || sSimPending == null || sOiCategory == null || sOiIndex == null || sSohCategory == null || sSohIndex == null) return "cause unread";
                object pend = sSimPending.GetValue(s);
                OutfitCategories pc = (OutfitCategories)sOiCategory.GetValue(pend);
                int pi = (int)sOiIndex.GetValue(pend);
                OutfitCategories hc = (OutfitCategories)sSohCategory.GetValue(h);
                int hi = (int)sSohIndex.GetValue(h);
                object act = sSimActiveSoh != null ? sSimActiveSoh.GetValue(s) : null;
                int cause = OutfitWaitCauseCode(h, s);
                if (cause < 0) return "cause unread";
                if (cause != OutfitWaitCore.kCauseAborted) PatchCounters()[2002 + cause]++;
                WeakReference dcb;
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

        // docs/224 §5 class of a wait (OutfitWaitCore.StaleCause), -1 when unreadable
        static int OutfitWaitCauseCode(object h, Sim s)
        {
            if (s == null || sSimPending == null || sOiCategory == null || sOiIndex == null || sSohCategory == null || sSohIndex == null || sSimActiveSoh == null) return -1;
            object pend = sSimPending.GetValue(s);
            bool aborted = sSohAborted != null && (bool)sSohAborted.GetValue(h);
            return OutfitWaitCore.StaleCause(aborted, object.ReferenceEquals(sSimActiveSoh.GetValue(s), h), (int)(OutfitCategories)sOiCategory.GetValue(pend),
                (int)sOiIndex.GetValue(pend), (int)(OutfitCategories)sSohCategory.GetValue(h), (int)sSohIndex.GetValue(h), (int)OutfitCategories.None);
        }

        // ---- outfitwaitend (docs/583 §1) ----
        static readonly FieldInfo sSohLaundry = typeof(Sim.SwitchOutfitHelper).GetField("mPlaceLaundryPile", kInstAll);
        static readonly MethodInfo sSimToddlerSpin = typeof(Sim).GetMethod("DoCarriedToddlerSpinIfNeeded", kInstAll, null, new Type[] { typeof(Sim.ClothesChangeReason), typeof(bool) }, null);
        static int sOeLines;

        // true when the wait was ended (the caller returns from Wait). Past the mark only, once per sim-minute.
        static bool OutfitWaitEndTry(object h, long waited, long endAfter, ref bool kept, ref bool seenOff)
        {
            int[] c = PatchCounters();
            try
            {
                Sim s = sSohSim.GetValue(h) as Sim;
                bool live = s != null && !s.HasBeenDestroyed && s.SimDescription != null;
                bool npc = live && !IsPlayerSim(s);  // household rule (PlayerSims.cs): unknown scope counts as household
                bool onScreen = live && World.IsObjectOnScreen(s.ObjectId);
                int cause = npc ? OutfitWaitCauseCode(h, s) : -1;
                bool inChange = npc && cause == OutfitWaitCore.kCauseOwn && OutfitWaitInChangeOutfit();
                if (!OutfitWaitCore.EndNow(true, waited, endAfter, npc, onScreen, cause, inChange))
                {
                    if (!kept) { kept = true; c[2126]++; }
                    return false;
                }
                if (!ShortcutOn("outfitwaitend"))
                {
                    if (!seenOff) { seenOff = true; c[2125]++; }
                    return false;
                }
                Sim.SwitchOutfitHelper helper = (Sim.SwitchOutfitHelper)h;
                OutfitCategories cat = (OutfitCategories)sSohCategory.GetValue(h), from = s.GetCurrentOutfitCategoryFromOutfitInGameObject();
                int idx = (int)sSohIndex.GetValue(h);
                bool laundry = sSohLaundry == null || (bool)sSohLaundry.GetValue(h);
                helper.Abort();                   // EA's cancel: active helper, pending outfit and the native delayed swap cleared
                sSohNoChange.SetValue(h, true);   // the caller's spin and ChangeOutfit now do nothing (EA's state for "no change")
                bool switched = s.SwitchToOutfitWithoutSpin(cat, idx);  // EA's instant switch (its own age / trait / outfit checks)
                if (switched)
                {
                    Sims3.Gameplay.Controllers.LaundryManager.OnClothesChanged(s, from, cat, laundry);  // as ChangeOutfit
                    if (sSimToddlerSpin != null) sSimToddlerSpin.Invoke(s, new object[] { helper.Reason, true });
                }
                c[2124]++;
                if (sOeLines++ < 10)
                {
                    string line = "outfitwaitend: " + s.FullName + " outfit wait ended after " + SimClock.ConvertFromTicks(waited, TimeUnit.Minutes).ToString("0")
                        + " sim-minutes with EA's instant switch to " + cat + "/" + idx + " (switched " + switched + "); task " + OutfitWaitCaller();
                    ShortcutLog(line);
                    if (sOwLines.Count < 30) sOwLines.Add(line);
                }
                return true;
            }
            catch (Exception) { c[2127]++; return false; }
        }

        // the wait runs inside the helper's ChangeOutfit (its Wait(false) before DoOutfitDelayedSwap): never ended there
        static bool OutfitWaitInChangeOutfit()
        {
            System.Diagnostics.StackTrace st = new System.Diagnostics.StackTrace(1, false);
            for (int i = 0; i < st.FrameCount && i < 6; i++)
            {
                MethodBase m = st.GetFrame(i).GetMethod();
                if (m != null && m.Name == "ChangeOutfit" && m.DeclaringType == typeof(Sim.SwitchOutfitHelper)) return true;
            }
            return false;
        }

        // OutfitWaitEndCheck run: pure EndNow with controls (docs/583 §1); "result": counters 2124-2127 and the live path.
        public static object OutfitWaitEndCheck(string what)
        {
            string miss = EpJoin(sSohCategory == null ? "mCategory" : "", sSimPending == null ? "mPendingOutfitInfo" : "");
            string counters = B582Counters("outfitwaitend", 2124, new[] { "ended", "would end (switch off)", "kept past the mark", "errors" }, miss);
            if (what == "result") return counters;
            if (what != "run") return "outfitwaitend <run|result>";
            bool on = ShortcutOn("outfitwaitend");
            const long mark = 30 * 37;
            int own = OutfitWaitCore.kCauseOwn;
            bool fixedNow = OutfitWaitCore.EndNow(on, mark, mark, true, false, own, false);
            bool controls = !OutfitWaitCore.EndNow(true, mark - 1, mark, true, false, own, false)       // before the mark
                && !OutfitWaitCore.EndNow(true, mark, mark, false, false, own, false)                  // household / gone
                && !OutfitWaitCore.EndNow(true, mark, mark, true, true, own, false)                    // on screen
                && !OutfitWaitCore.EndNow(true, mark, mark, true, false, OutfitWaitCore.kCauseAborted, false)
                && !OutfitWaitCore.EndNow(true, mark, mark, true, false, OutfitWaitCore.kCauseReplaced, false)
                && !OutfitWaitCore.EndNow(true, mark, mark, true, false, OutfitWaitCore.kCauseCleared, false)
                && !OutfitWaitCore.EndNow(true, mark, mark, true, false, own, true);                   // inside ChangeOutfit
            return B582Line("outfitwaitend", on, "off-screen NPC own-cause wait past the mark ended", fixedNow,
                "early/household/on screen/aborted/replaced/cleared/in ChangeOutfit keep EA's loop=" + controls, controls, miss, counters);
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
        static readonly Dictionary<ulong, WeakReference> sCbDropped = new Dictionary<ulong, WeakReference>(); // the last dropped callback's target per object; weak (issue #40: a strong delegate held the helper and its Sim)
        static int sCbLineCount;

        // world quit (Instantiator): drop statics that hold Sims, lots or interactions of the old world (issue #40)
        public static void HeldRefsWorldQuit()
        {
            sCbDropped.Clear();
            sRflCmds.Clear();
            sStuckQueue.Clear();
            sUfCamLot = null; sUfCamLotTick = -1;
            OdWorldQuit();  // unseenoutfitdefer (UnseenOutfitDefer.cs): object ids of the old world
        }
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
                sCbDropped[id.Value] = new WeakReference(cb != null ? cb.Target : null);
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
            return (sOiCategory != null ? sOiCategory.GetValue(pi) : "?") + "/" + (sOiIndex != null ? sOiIndex.GetValue(pi) : "?");
        }

        static bool sOwReplReady; // the replace check's second swap called back
        static string sOwAbortSaw, sOwWaitEnd; // sOwWaitEnd null = task 1 still in Wait (GetProxy is null for a script Task)
        const int kOwStartWaitTicks = 6000; // test task 2: Sleep(1) rounds it waits for the helper to start before giving up
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
        //   "scope": pure, docs/580 §15: a household Sim's stale wait keeps EA's per-tick loop; an NPC's and a gone Sim's
        //            are polled once per sim-minute as before (controls), and nothing changes before the 60-minute mark.
        //   "ready": sets mReady, as a late Ready callback would; on, task 1 returns within a sim-minute; off, within a tick.
        //   "stop":  destroys both tasks and aborts the helper (which cancels a pending swap; neither swap is completed).
        //   "start dead": issue #40. As "start" on a Sim outside the active household (never a selectable one); task 2 aborts the
        //            helper in the loop (as MagicalMakeover.Cleanup does) and then destroys the Sim (reset, 30 ticks, then
        //            SimDescription.Dispose, as the agent's remove_sims). "dead" then reads whether the wait ended ([933]).
        public static object OutfitWaitCheck(string what)
        {
            switch (what)
            {
                case "start":
                case "start replace":
                case "start clear":
                case "start control":
                case "start dead":
                {
                    if (sOwHelper != null) return "a test wait exists; stop first";
                    if (sSohReady == null || sSohStarted == null || sSimActiveSoh == null) return "SwitchOutfitHelper fields not found";
                    Sim sim = null;
                    OutfitCategories[] kinds = { OutfitCategories.Sleepwear, OutfitCategories.Everyday, OutfitCategories.Formalwear, OutfitCategories.Athletic };
                    // a Sim in a special outfit (towel, costume) may not change (a 2026-10-31 run's wait ended at once): any Sim
                    // in the world in a normal outfit with no helper of its own, an active-household one first
                    bool dead = what == "start dead";
                    foreach (Sim c in Sims3.Gameplay.Queries.GetObjects<Sim>())
                        if (dead && IsPlayerSim(c)) continue;  // the dead test only on NPCs (PlayerSims.cs)
                        else if (c.InWorld && c.SimDescription != null && sSimActiveSoh.GetValue(c) == null && Array.IndexOf(kinds, c.CurrentOutfitCategory) >= 0
                            // with another category to change into: a household pet has one only (2026-11-17: the check took the dog and never started)
                            && Array.Exists(kinds, delegate(OutfitCategories k) { return k != c.CurrentOutfitCategory && c.SimDescription.GetOutfitCount(k) > 0; })
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
                            int startWait = 0;  // bounded: a helper that never starts must not keep this test task alive
                            while (!(bool)sSohStarted.GetValue(helper))
                            {
                                if (++startWait > kOwStartWaitTicks) { sOwAbortSaw = "helper not started after " + kOwStartWaitTicks + " ticks; stop and start again"; return; }
                                Simulator.Sleep(1);
                            }
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
                            if (!dead) return;
                            SimDescription sd = simR.SimDescription;
                            simR.SetObjectToReset();
                            Simulator.Sleep(30);
                            if (sd.Household != null) sd.Household.Remove(sd);
                            sd.Dispose(true, false, true);
                            sOwAbortSaw = "aborted in the loop, Sim destroyed " + simR.HasBeenDestroyed;
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
                case "dead": // "start dead": the wait task's end and [933]
                {
                    Dictionary<string, object> r = new Dictionary<string, object>();
                    r["abort"] = sOwAbortSaw;
                    r["sim_destroyed"] = sOwHelper != null && sSohSim.GetValue(sOwHelper) is Sim && ((Sim)sSohSim.GetValue(sOwHelper)).HasBeenDestroyed;
                    r["wait_ended"] = sOwWaitEnd != null;
                    r["wait_end"] = sOwWaitEnd;
                    r["dead_ended"] = PatchCounters()[933];
                    r["stale"] = PatchCounters()[680];
                    return r;
                }
                case "log":
                    return sOwLines.Count + " stale-wait lines: " + string.Join(" | ", sOwLines.ToArray());
                case "scope": // pure (docs/580 §15): the household rule on the minute poll; nothing in the world is touched
                {
                    bool on = ShortcutOn("outfitwaitpoll");
                    const long stale = 60 * 37; const uint minute = 37;
                    uint npc = OutfitWaitCore.SleepTicks(stale, stale, minute, on && OutfitWaitCore.PollAllowed(true, false));
                    uint hh = OutfitWaitCore.SleepTicks(stale, stale, minute, on && OutfitWaitCore.PollAllowed(true, true));
                    uint gone = OutfitWaitCore.SleepTicks(stale, stale, minute, on && OutfitWaitCore.PollAllowed(false, true));
                    uint early = OutfitWaitCore.SleepTicks(stale - 1, stale, minute, on && OutfitWaitCore.PollAllowed(true, false));
                    bool fixedNow = hh == 1u;
                    bool controls = npc == (on ? minute : 1u) && gone == (on ? minute : 1u) && early == 1u;
                    return "outfitwaitpoll scope (switch " + (on ? "on" : "off") + "): household Sim past the mark sleeps " + hh + " tick(s) (EA's loop)="
                        + fixedNow + "; control: NPC sleeps " + npc + ", gone Sim (issue #40) sleeps " + gone + ", NPC before the mark sleeps " + early
                        + "; controls ok=" + controls + "; household waits kept on EA's loop 1109=" + PatchCounters()[3544];
                }
                case "counts": // no start needed (repro outfit-wait-view-log)
                {
                    int[] c = PatchCounters();
                    return "switch " + ShortcutOn("outfitwaitpoll") + "; stale " + c[680] + ", minute polls " + c[681] + ", ended " + c[682]
                        + "; causes own " + c[2003] + ", replaced " + c[2004] + ", cleared " + c[2005] + "; callbacks dropped " + c[2010]
                        + ", stale with a dropped callback " + c[2011] + "; on screen when stale " + c[2012] + ", on screen when ended " + c[2013] + "; dead-Sim waits ended " + c[933];
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
                default: return "unknown check " + what + " (start, start replace, start clear, start control, start dead, state, dead, log, scope, counts, ready, stop)";
            }
        }
    }
}
