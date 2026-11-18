using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.DreamsAndPromises;
using Sims3.Gameplay.EventSystem;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;
using Sims3.UI;

namespace Ts3Fixes
{
    // Wishes engine review fixes (docs/619 findings 1-6, built in docs/623, s86). All default off, EA bug fixes, household
    // Sims included: each one restores what EA's own code meant to do (an alarm EA removes elsewhere, a listener EA keeps until
    // the wish completes, a notice EA wrote, an update EA requested, a promise EA's own subject-gone path breaks). Nothing is
    // skipped, cut or faked. Hooks: W86TimerHook (ActiveTimerNode), W86FeedbackHook (ActiveDreamNode), W86MgrHook
    // (DreamsAndPromisesManager); each hook class has only hook/stub pairs on one game class, so swapdelegates maps it (the
    // TimerCallback alarm is saved). Update is shared with WlMgr.UpdateHook (WishListenerFixes.cs): W86UpdateBegin/End.
    public static partial class Fixes
    {
        const BindingFlags kW86All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        // ---- docs/623 §1 timeralarmleak ------------------------------------------------------------------------------------
        // [verified, ilspycmd ActiveTimerNode] the ctor and SetupFromExport add an AlwaysPersisted repeating alarm
        // (IL `ldftn TimerCallback` -> GameObject::AddAlarmRepeating). RemoveListenerFromTracker, the call every path makes when
        // a node leaves play (RemoveActiveNode, ScoreChildren throw-away of a duplicate, OnLoadFixup's refreshed trees,
        // ClearActiveNodeListeners from Sim.NullDnPManager on ResetDnP / unselectable / travel), is empty for a timer node, so
        // its alarm stays, keeps the dead node reachable and calls SetToUpdate(full) on the Sim's current manager every
        // TimeBetweenSatisfies hours until NumberOfSatisfies. Fix part 1: the timer node's "listener" is its alarm, so
        // RemoveListenerFromTracker removes it as KillNode does (mOwner.RemoveAlarm, handle invalid).
        // Part 2, alarms already out there (an earlier session, a save made without the fix): EA's TimerCallback already ends
        // a node that lost its owner or node instance (WasKilled, mCount = int.MaxValue, RemoveAlarm). A save keeps such an
        // orphan's alarm, but its mNodeInstance is [Persistable(false)] and only nodes still in a manager get OnLoadFixup, so
        // EA's guard removes the loaded orphan at its first fire. The same-session orphan (node instance still set) is the
        // case EA's guard misses: when the owner's current manager does not hold the node, TimerCallback takes EA's own kill
        // path. A node in its manager runs EA's callback unchanged.
        // Counters: [3520] alarms removed when the node left, [3521] left alive with the switch off, [3522] orphan alarms
        // ended at their fire, [3523] orphan fires with the switch off, [3524] errors.
        static readonly FieldInfo sW86TAlarm = typeof(ActiveTimerNode).GetField("mAlarmHandle", kW86All);
        static readonly FieldInfo sW86TCount = typeof(ActiveTimerNode).GetField("mCount", kW86All);
        static readonly FieldInfo sW86TInst = typeof(ActiveTimerNode).GetField("mNodeInstance", kW86All);
        static readonly FieldInfo sW86TOwner = typeof(ActiveNodeBase).GetField("mOwner", kW86All);
        static readonly MethodInfo sW86TCallback = typeof(ActiveTimerNode).GetMethod("TimerCallback", kW86All, null, Type.EmptyTypes, null);
        static string sW86TMissing = "";

        static void ApplyTimerAlarmLeak()
        {
            sW86TMissing = ((sW86TAlarm == null ? "mAlarmHandle " : "") + (sW86TCount == null ? "mCount " : "") + (sW86TInst == null ? "mNodeInstance " : "")
                + (sW86TOwner == null ? "mOwner " : "") + (sW86TCallback == null ? "TimerCallback" : "")).Trim();
            if (sW86TMissing.Length > 0) { AppliedLog().Add("timeralarmleak: not found: " + sW86TMissing); return; }
            Patch(typeof(ActiveTimerNode), "RemoveListenerFromTracker", Type.EmptyTypes, typeof(W86TimerHook), "RemoveHook", "RemoveOrig");
            Patch(typeof(ActiveTimerNode), "TimerCallback", Type.EmptyTypes, typeof(W86TimerHook), "CallbackHook", "CallbackOrig");
        }

        // Pure: remove the alarm when the node leaves? (EA: never)
        internal static bool W86TimerRemoveAtLeave(bool on, bool alarmLive)
        {
            return on && alarmLive;
        }

        // Pure: take EA's kill path at a fire? EA's own guard covers no owner / no node instance; the fix adds "not in the
        // owner's current manager" (no manager counts as not in it).
        internal static bool W86TimerKillAtFire(bool on, bool ownerAndInstance, bool inManager)
        {
            return on && ownerAndInstance && !inManager;
        }

        static void W86TimerLeave(object node)
        {
            try
            {
                if (sW86TMissing.Length > 0) return;
                AlarmHandle h = (AlarmHandle)sW86TAlarm.GetValue(node);
                bool live = h != AlarmHandle.kInvalidHandle;
                if (!live) return;
                bool on = ShortcutOn("timeralarmleak");
                if (!W86TimerRemoveAtLeave(on, live)) { PatchCounters()[3521]++; return; }
                Sim owner = sW86TOwner.GetValue(node) as Sim;
                if (owner != null) owner.RemoveAlarm(h);
                else if (AlarmManager.Global != null) AlarmManager.Global.RemoveAlarm(h);
                sW86TAlarm.SetValue(node, AlarmHandle.kInvalidHandle);
                PatchCounters()[3520]++;
            }
            catch (ResetException) { throw; }
            catch (Exception) { PatchCounters()[3524]++; }
        }

        // True when the fire was handled (EA's kill path taken), false to run EA's callback.
        static bool W86TimerOrphanFire(object node)
        {
            try
            {
                if (sW86TMissing.Length > 0) return false;
                Sim owner = sW86TOwner.GetValue(node) as Sim;
                bool ownerAndInstance = owner != null && sW86TInst.GetValue(node) != null;
                if (!ownerAndInstance) return false;  // EA's own guard
                DreamsAndPromisesManager m = owner.DreamsAndPromisesManager;
                bool inManager = m != null && m.ActiveNodes != null && m.ActiveNodes.Contains((ActiveNodeBase)node);
                if (inManager) return false;
                bool on = ShortcutOn("timeralarmleak");
                if (!W86TimerKillAtFire(on, true, false)) { PatchCounters()[3523]++; return false; }
                ((ActiveNodeBase)node).WasKilled = true;
                sW86TCount.SetValue(node, int.MaxValue);
                AlarmHandle h = (AlarmHandle)sW86TAlarm.GetValue(node);
                if (AlarmManager.Global != null) AlarmManager.Global.RemoveAlarm(h);
                sW86TAlarm.SetValue(node, AlarmHandle.kInvalidHandle);
                PatchCounters()[3522]++;
                return true;
            }
            catch (ResetException) { throw; }
            catch (Exception) { PatchCounters()[3524]++; return false; }
        }

        public class W86TimerHook
        {
            public void RemoveHook()
            {
                RemoveOrig();
                W86TimerLeave(this);
            }
            public void RemoveOrig() { } // receives ActiveTimerNode.RemoveListenerFromTracker (EA: empty)

            public void CallbackHook()
            {
                if (W86TimerOrphanFire(this)) return;
                CallbackOrig();
            }
            public void CallbackOrig() { } // receives ActiveTimerNode.TimerCallback
        }

        static Sim W86CheckSim()
        {
            Sim s = PlumbBob.SelectedActor;
            return s != null && s.SimDescription != null && s.DreamsAndPromisesManager != null ? s : null;
        }

        static bool W86AlarmAlive(AlarmHandle h)
        {
            return h != AlarmHandle.kInvalidHandle && AlarmManager.Global != null && AlarmManager.Global.GetTimeLeft(h, TimeUnit.Hours) > 0f;
        }

        // TimerAlarmLeakCheck run: makes its own timer nodes (a TimerNodeInstance of its own: 3 satisfies, 24 h apart) for the
        // selected Sim, none of them in the Sim's manager.
        //   (1) leave: RemoveListenerFromTracker on a node with a live alarm (what RemoveActiveNode and NullDnPManager do).
        //       Off: the alarm stays (BROKEN). On: removed.
        //   (2) same-session orphan fire: TimerCallback of a node not in the manager. Off: EA counts it and keeps the alarm.
        //       On: EA's kill path (alarm removed, node killed).
        //   Control (3), loaded-save orphan: node instance null as after a load: EA's guard removes the alarm with either value.
        //   Every alarm the check made is removed at the end. The off arm's orphan fire asks the Sim's manager for one full
        //   update, as EA's callback does.
        public static object TimerAlarmLeakCheck(string what)
        {
            string counters = B582Counters("timeralarmleak", 3520, new[] { "removed at leave", "left, switch off", "orphans ended at fire", "orphan fires, switch off", "errors" }, sW86TMissing);
            if (what == "result") return counters;
            if (what != "run") return "timeralarmleak <run|result>";
            bool on = ShortcutOn("timeralarmleak");
            bool controls = W86TimerRemoveAtLeave(true, true) && !W86TimerRemoveAtLeave(true, false) && !W86TimerRemoveAtLeave(false, true)
                && W86TimerKillAtFire(true, true, false) && !W86TimerKillAtFire(true, true, true) && !W86TimerKillAtFire(true, false, false);
            string live = "no selected Sim with a wish manager";
            bool fixedNow = false;
            Sim sim = W86CheckSim();
            if (sim != null && sW86TMissing.Length == 0)
            {
                List<AlarmHandle> made = new List<AlarmHandle>();
                try
                {
                    TimerNodeInstance inst = new TimerNodeInstance(0x7E570086u, 0x7E57008600000001uL, false, false, 3, 24f, 0f, false);
                    ActiveTimerNode a = new ActiveTimerNode(sim, inst, null);
                    AlarmHandle ha = (AlarmHandle)sW86TAlarm.GetValue(a); made.Add(ha);
                    bool aBefore = W86AlarmAlive(ha);
                    a.RemoveListenerFromTracker();
                    bool leaveRemoved = aBefore && !W86AlarmAlive(ha);

                    ActiveTimerNode b = new ActiveTimerNode(sim, inst, null);
                    AlarmHandle hb = (AlarmHandle)sW86TAlarm.GetValue(b); made.Add(hb);
                    ((AlarmTimerCallback)Fixes.Bind(typeof(AlarmTimerCallback), b, sW86TCallback))();
                    bool fireRemoved = W86AlarmAlive(hb) == false && b.WasKilled;

                    ActiveTimerNode l = new ActiveTimerNode(sim, inst, null);
                    AlarmHandle hl = (AlarmHandle)sW86TAlarm.GetValue(l); made.Add(hl);
                    sW86TInst.SetValue(l, null);  // as after a load: mNodeInstance is not saved
                    ((AlarmTimerCallback)Fixes.Bind(typeof(AlarmTimerCallback), l, sW86TCallback))();
                    bool loadGuard = !W86AlarmAlive(hl) && l.WasKilled;
                    controls = controls && aBefore && loadGuard;
                    fixedNow = leaveRemoved && fireRemoved;
                    live = "leave removed alarm=" + leaveRemoved + ", orphan fire removed alarm=" + fireRemoved + ", loaded orphan removed by EA's guard=" + loadGuard;
                }
                catch (ResetException) { throw; }
                catch (Exception e) { live = "threw " + e.GetType().Name; controls = false; }
                finally
                {
                    foreach (AlarmHandle h in made) if (AlarmManager.Global != null) AlarmManager.Global.RemoveAlarm(h);
                }
            }
            else controls = false;
            return B582Line("timeralarmleak", on, "timer alarms end with their node (" + live + ")", fixedNow,
                "EA's guard and the pure decisions", controls, sW86TMissing, counters);
        }

        // ---- docs/623 §2 wishfeedbackkeep ----------------------------------------------------------------------------------
        // [verified, ilspycmd EventTracker::ProcessEvent IL_0069..IL_0081] `catch [mscorlib]System.Exception` ->
        // RemoveListener(list, listener): a listener that throws once is dropped. A wish's listener is
        // ActiveDreamNode.RunFeedbackFunction, which calls the reflection-bound feedback function (sFeedbackWrapper,
        // DreamsAndPromisesDelegateFunctions.FeedBackFunctionWrapper -> d(e, node)). A NullReference inside one of them on a
        // gone subject or object drops the listener for good: mEventListener still points at it, the wish stays in its slot,
        // and it can never complete until Reset(true) or a load. EA keeps the listener until the wish completes (Keep for
        // every "not yet" answer, Remove only after AddCompletedNode). Fix: when the node is a live wish (owner, its
        // description, its CreatedSim, a wish manager, its node instance and its listener all present: every reference
        // RunFeedbackFunction makes before the feedback function; read after the throw, so the hot path costs nothing) and
        // the call throws, the answer is Keep, as EA's "not completed" answer. A broken node (any of those missing) still throws to EventTracker, which drops it as before.
        // Counters: [3525] wishes kept after a feedback throw, [3526] dropped with the switch off, [3527] broken node throws
        // (EA's removal kept), [3528] errors.
        static readonly FieldInfo sW86FListener = typeof(ActiveDreamNode).GetField("mEventListener", kW86All);
        static readonly FieldInfo sW86FWrapper = typeof(ActiveDreamNode).GetField("sFeedbackWrapper", kW86All);
        static readonly MethodInfo sW86FRun = typeof(ActiveDreamNode).GetMethod("RunFeedbackFunction", kW86All, null, new Type[] { typeof(Event) }, null);
        static string sW86FMissing = "";

        static void ApplyWishFeedbackKeep()
        {
            sW86FMissing = ((sW86FListener == null ? "mEventListener " : "") + (sW86FWrapper == null ? "sFeedbackWrapper " : "") + (sW86FRun == null ? "RunFeedbackFunction" : "")).Trim();
            if (sW86FMissing.Length > 0) { AppliedLog().Add("wishfeedbackkeep: not found: " + sW86FMissing); return; }
            Patch(typeof(ActiveDreamNode), "RunFeedbackFunction", new Type[] { typeof(Event) }, typeof(W86FeedbackHook), "FeedbackHook", "FeedbackOrig");
        }

        // Pure: the listener's answer after a throw. Keep = stays registered; null = rethrow (EventTracker drops it).
        internal static bool W86FeedbackKeep(bool on, bool liveWish)
        {
            return on && liveWish;
        }

        static bool W86FeedbackLive(object self)
        {
            try
            {
                ActiveDreamNode n = self as ActiveDreamNode;
                if (n == null || sW86FMissing.Length > 0) return false;
                Sim o = n.Owner;
                return o != null && o.SimDescription != null && o.SimDescription.CreatedSim != null && o.DreamsAndPromisesManager != null
                    && n.NodeInstance != null && sW86FListener.GetValue(n) != null;
            }
            catch (ResetException) { throw; }
            catch (Exception) { PatchCounters()[3528]++; return false; }
        }

        // After a throw: true = answer Keep; false = rethrow.
        static bool W86FeedbackThrew(bool liveWish)
        {
            if (!liveWish) { PatchCounters()[3527]++; return false; }
            bool on = ShortcutOn("wishfeedbackkeep");
            if (!W86FeedbackKeep(on, true)) { PatchCounters()[3526]++; return false; }
            PatchCounters()[3525]++;
            return true;
        }

        public class W86FeedbackHook
        {
            public ListenerAction FeedbackHook(Event e)
            {
                try { return FeedbackOrig(e); }
                catch (ResetException) { throw; }
                catch (Exception)
                {
                    if (!W86FeedbackThrew(W86FeedbackLive(this))) throw;
                    return ListenerAction.Keep;
                }
            }
            public ListenerAction FeedbackOrig(Event e) { return ListenerAction.Keep; } // receives ActiveDreamNode.RunFeedbackFunction
        }

        static bool W86ThrowingWrapper(Event e, ActiveDreamNode n, DreamsAndPromisesFeedbackFunctionDelegate d)
        {
            throw new InvalidOperationException("wishfeedbackkeep check: feedback function throws");
        }

        static ListenerAction W86NoListener(Event e) { return ListenerAction.Keep; }

        // An EA wish node instance for checks: any tree (or the trees given), not a midlife-crisis wish, matching want.
        static DreamNodeInstance W86FindInstance(Sim sim, ulong[] trees, bool validAfterDeath)
        {
            FieldInfo f = typeof(DreamsAndPromisesManager).GetField("sDreamTrees", kW86All);
            IDictionary all = f == null ? null : f.GetValue(null) as IDictionary;
            if (all == null) return null;
            foreach (object v in all.Values)
            {
                DreamTree t = v as DreamTree;
                if (t == null) continue;
                if (trees != null && Array.IndexOf(trees, t.Id) < 0) continue;
                foreach (DreamNodeInstanceBase b in t.Nodes)
                {
                    DreamNodeInstance d = b as DreamNodeInstance;
                    if (d == null || d.Primitive == null || d.IsRootNode) continue;
                    if (DreamsAndPromisesManager.sValidAfterDeathNodes.ContainsKey(d.PrimitiveId) != validAfterDeath) continue;
                    ActiveDreamNode probe = ActiveDreamNode.CreateNodeForPotentialStartTesting(sim, d);
                    if (DreamsAndPromisesManager.IsMLCrisisDream(probe)) continue;
                    return d;
                }
            }
            return null;
        }

        // WishFeedbackKeepCheck run: a listener-less wish node of the selected Sim (CreateNodeForPotentialStartTesting, not in
        // the manager) gets a stand-in listener object, and EA's feedback wrapper is swapped for one that throws, for this
        // one call. Off: RunFeedbackFunction throws (EventTracker drops the listener: BROKEN). On: it answers Keep.
        //   Control: the same node without a listener (broken node) still throws with either value.
        public static object WishFeedbackKeepCheck(string what)
        {
            string counters = B582Counters("wishfeedbackkeep", 3525, new[] { "kept after a throw", "dropped, switch off", "broken node throws (EA kept)", "errors" }, sW86FMissing);
            if (what == "result") return counters;
            if (what != "run") return "wishfeedbackkeep <run|result>";
            bool on = ShortcutOn("wishfeedbackkeep");
            bool controls = W86FeedbackKeep(true, true) && !W86FeedbackKeep(false, true) && !W86FeedbackKeep(true, false);
            bool fixedNow = false;
            string live = "no selected Sim with a wish manager";
            Sim sim = W86CheckSim();
            if (sim != null && sW86FMissing.Length == 0)
            {
                object wrapper = sW86FWrapper.GetValue(null);
                EventListener stand = null;
                try
                {
                    DreamNodeInstance inst = W86FindInstance(sim, null, false);
                    if (inst == null) { live = "no wish node instance"; controls = false; }
                    else
                    {
                        ActiveDreamNode n = ActiveDreamNode.CreateNodeForPotentialStartTesting(sim, inst);
                        stand = EventTracker.AddListener(EventTypeId.kEventNone, W86NoListener);
                        EventTracker.RemoveListener(stand);
                        sW86FListener.SetValue(n, stand);
                        sW86FWrapper.SetValue(null, Delegate.CreateDelegate(typeof(DreamsAndPromisesFeedbackFunctionWrapperDelegate),
                            typeof(Fixes).GetMethod("W86ThrowingWrapper", kW86All)));
                        ProcessEventDelegate run = (ProcessEventDelegate)Fixes.Bind(typeof(ProcessEventDelegate), n, sW86FRun);
                        bool threw = false, kept = false;
                        try { kept = run(new Event(EventTypeId.kEventNone)) == ListenerAction.Keep; }
                        catch (ResetException) { throw; }
                        catch (Exception) { threw = true; }
                        sW86FListener.SetValue(n, null);
                        bool ctlThrew = false;
                        try { run(new Event(EventTypeId.kEventNone)); }
                        catch (ResetException) { throw; }
                        catch (Exception) { ctlThrew = true; }
                        fixedNow = kept && !threw;
                        controls = controls && ctlThrew;
                        live = "feedback throw: listener answer " + (threw ? "threw (dropped by EventTracker)" : kept ? "Keep" : "Remove") + "; broken node threw=" + ctlThrew;
                    }
                }
                catch (ResetException) { throw; }
                catch (Exception e) { live = "threw " + e.GetType().Name; controls = false; }
                finally { sW86FWrapper.SetValue(null, wrapper); }
            }
            else controls = false;
            return B582Line("wishfeedbackkeep", on, "a wish keeps its listener when its feedback throws (" + live + ")", fixedNow,
                "broken node still dropped, pure decisions", controls, sW86FMissing, counters);
        }

        // ---- shared: DreamsAndPromisesManager hooks ---------------------------------------------------------------------
        static readonly FieldInfo sW86MPromise = typeof(DreamsAndPromisesManager).GetField("mPromiseNodes", kW86All);
        static readonly FieldInfo sW86MActor = typeof(DreamsAndPromisesManager).GetField("mActor", kW86All);
        static readonly MethodInfo sW86MDestroyed = typeof(DreamsAndPromisesManager).GetMethod("ObjectDestroyedDelegate", kW86All, null, new Type[] { typeof(Event) }, null);
        static readonly MethodInfo sW86MChild = typeof(DreamsAndPromisesManager).GetMethod("ChildTakenBySocialWorkerCallback", kW86All, null, new Type[] { typeof(Event) }, null);
        static readonly MethodInfo sW86MLocalize = typeof(DreamsAndPromisesManager).GetMethod("LocalizeString", kW86All, null, new Type[] { typeof(bool), typeof(string), typeof(object[]) }, null);
        static readonly FieldInfo sW86Draining = typeof(EventTracker).GetField("sCurrentlyUpdatingDreamsAndPromisesManagers", kW86All);
        static readonly FieldInfo sW86DeathStyle = typeof(SimDescription).GetField("mDeathStyle", kW86All);

        public class W86MgrHook
        {
            public ListenerAction DestroyedHook(Event e)
            {
                W86Gone pre = W86GoneBefore(this, e);
                ListenerAction r = DestroyedOrig(e);
                W86GoneAfter(this, e, pre);
                return r;
            }
            public ListenerAction DestroyedOrig(Event e) { return ListenerAction.Keep; } // receives DreamsAndPromisesManager.ObjectDestroyedDelegate

            public ListenerAction ChildTakenHook(Event e)
            {
                ListenerAction r = ChildTakenOrig(e);
                W86ChildTakenAfter(this, e);
                return r;
            }
            public ListenerAction ChildTakenOrig(Event e) { return ListenerAction.Keep; } // receives DreamsAndPromisesManager.ChildTakenBySocialWorkerCallback

            public void SetToUpdateHook(bool fullUpdate, bool forceFullRefresh)
            {
                W86SetToUpdateSeen(this);
                SetToUpdateOrig(fullUpdate, forceFullRefresh);
            }
            public void SetToUpdateOrig(bool fullUpdate, bool forceFullRefresh) { } // receives DreamsAndPromisesManager.SetToUpdate
        }

        static DreamsAndPromisesManager W86TestManager(Sim actor)
        {
            DreamsAndPromisesManager m = Activator.CreateInstance(typeof(DreamsAndPromisesManager), true) as DreamsAndPromisesManager;  // private ctor: no listeners
            if (m != null) sW86MActor.SetValue(m, actor);
            return m;
        }

        static bool W86Promised(object mgr, ActiveDreamNode n)
        {
            ActiveDreamNode[] p = sW86MPromise.GetValue(mgr) as ActiveDreamNode[];
            return p != null && Array.IndexOf(p, n) >= 0;
        }

        // ---- docs/623 §3 wishsubjectgone -----------------------------------------------------------------------------------
        // [verified, ilspycmd DreamsAndPromisesManager::ObjectDestroyedDelegate] per filed node: IL_0115
        // RemoveActiveNode(item, false) (-> CancelDream, BreakPromise -> RemoveDisplayedPromiseNode empties the slot), then
        // IL_012b `if (RemoveDisplayedPromiseNode(item))`, always false: the "DreamFailureTNS" notice (IL_0171
        // ShowTNSIfSelectable) and the sting_dream_fail sound (IL_01e4) never happen. BreakPromise already fired the UI break
        // and the midlife-crisis update, so only the notice and the sting are lost. And for a dead subject the nodes valid
        // after death are skipped (`continue`), but IL_01ce Dictionary::Remove(obj) drops the whole index entry, so those kept
        // wishes are no longer filed under the subject: when its SimDescription is destroyed later the clean-up finds nothing.
        // Fix (EA's body runs unchanged): before it, the subject key is derived as EA derives it and the filed nodes it will
        // remove that are in a promise slot, and the ones it keeps, are noted; after it, each removed promised wish gets EA's
        // notice (when the event names the subject's SimDescription, as EA's statement needs; kEventObjectDisposed has none)
        // and the sting plays once when this is the active manager, as EA wrote; kept nodes still active are filed again
        // under the same key. No second FireBreakPromise or crisis update (BreakPromise did them).
        // Counters: [3529] failure notices given, [3530] kept nodes filed again, [3531] notices lost with the switch off,
        // [3532] kept nodes unfiled with the switch off, [3533] errors.
        internal class W86Gone
        {
            public object Key;
            public bool On;
            public List<ActiveDreamNode> Promised = new List<ActiveDreamNode>();
            public List<ActiveDreamNode> Kept = new List<ActiveDreamNode>();
        }

        internal static int sW86DryNotices;  // check seam: count the notice instead of showing it
        static bool sW86Dry;

        static void ApplyWishSubjectGone()
        {
            string miss = ((sW86MPromise == null ? "mPromiseNodes " : "") + (sW86MActor == null ? "mActor " : "") + (sW86MDestroyed == null ? "ObjectDestroyedDelegate " : "")
                + (sW86MLocalize == null ? "LocalizeString " : "") + (sDcRemoval == null ? "mPotentialRemovalNodes" : "")).Trim();
            if (miss.Length > 0) { sW86GMissing = miss; AppliedLog().Add("wishsubjectgone: not found: " + miss); return; }
            Patch(typeof(DreamsAndPromisesManager), "ObjectDestroyedDelegate", new Type[] { typeof(Event) }, typeof(W86MgrHook), "DestroyedHook", "DestroyedOrig");
        }
        static string sW86GMissing = "";

        // EA's subject key for the event (null: nothing to do); dead = the subject is a dead Sim (kept-after-death rule applies).
        internal static object W86GoneKey(Event e, out bool dead)
        {
            dead = false;
            if (e == null) return null;
            if (e.Id == EventTypeId.kEventObjectDisposed)
            {
                Sims3.Gameplay.Abstracts.GameObject go = e.TargetObject as Sims3.Gameplay.Abstracts.GameObject;
                if (go == null) return null;
                Sim s = go as Sim;
                if (s != null && s.SimDescription.DeathStyle != SimDescription.DeathType.None && !s.SimDescription.IsPlayableGhost) { dead = true; return s.SimDescription; }
                return go;
            }
            DnPSubjectDestroyedEvent ev = e as DnPSubjectDestroyedEvent;
            if (ev == null) return null;
            if (ev.WasSimDescriptionDestroyed && ev.SimDescription != null) return ev.SimDescription.SimDescriptionId;
            if (ev.SimDescription != null)
            {
                if (ev.SimDescription.DeathStyle != SimDescription.DeathType.None && !ev.SimDescription.IsPlayableGhost) { dead = true; return ev.SimDescription.SimDescriptionId; }
                return null;
            }
            return ev.GameObject;
        }

        // Pure: what the fix adds per filed node. EA removes it unless (dead && validAfterDeath).
        internal static bool W86GoneNotice(bool on, bool removed, bool promised) { return on && removed && promised; }
        internal static bool W86GoneRefile(bool on, bool dead, bool validAfterDeath, bool stillActive) { return on && dead && validAfterDeath && stillActive; }

        static W86Gone W86GoneBefore(object mgr, Event e)
        {
            try
            {
                if (sW86GMissing.Length > 0) return null;
                bool dead;
                object key = W86GoneKey(e, out dead);
                if (key == null) return null;
                IDictionary index = sDcRemoval.GetValue(mgr) as IDictionary;
                IList list = index == null ? null : DcIndexList(index, key);
                if (list == null || list.Count == 0) return null;
                W86Gone g = new W86Gone();
                g.Key = key;
                foreach (object o in list)
                {
                    ActiveDreamNode n = o as ActiveDreamNode;
                    if (n == null) continue;
                    if (dead && n.IsValidAfterSubjectDeath()) g.Kept.Add(n);
                    else if (W86Promised(mgr, n)) g.Promised.Add(n);
                }
                if (g.Promised.Count == 0 && g.Kept.Count == 0) return null;
                g.On = ShortcutOn("wishsubjectgone");
                if (!g.On) { PatchCounters()[3531] += g.Promised.Count; PatchCounters()[3532] += g.Kept.Count; }
                return g;
            }
            catch (ResetException) { throw; }
            catch (Exception) { PatchCounters()[3533]++; return null; }
        }

        static void W86GoneAfter(object mgr, Event e, W86Gone g)
        {
            if (g == null || !g.On) return;
            try
            {
                DreamsAndPromisesManager m = (DreamsAndPromisesManager)mgr;
                DnPSubjectDestroyedEvent ev = e as DnPSubjectDestroyedEvent;
                bool failed = false;
                foreach (ActiveDreamNode n in g.Promised)
                {
                    if (!W86GoneNotice(true, !m.ActiveNodes.Contains(n), !W86Promised(mgr, n))) continue;
                    failed = true;
                    PatchCounters()[3529]++;
                    if (ev == null || ev.SimDescription == null) continue;
                    string text = (string)sW86MLocalize.Invoke(null, new object[] { ev.SimDescription.IsFemale, "DreamFailureTNS", new object[] { ev.SimDescription } });
                    if (sW86Dry) { if (!string.IsNullOrEmpty(text)) sW86DryNotices++; continue; }
                    Sim actor = sW86MActor.GetValue(mgr) as Sim;
                    if (actor != null) actor.ShowTNSIfSelectable(text, StyledNotification.NotificationStyle.kSystemMessage, ObjectGuid.InvalidObjectGuid);
                }
                if (failed && !sW86Dry && m == DreamsAndPromisesManager.ActiveDreamsAndPromisesManager) Audio.StartSound("sting_dream_fail");
                if (g.Kept.Count == 0) return;
                IDictionary index = sDcRemoval.GetValue(mgr) as IDictionary;
                if (index == null) return;
                Type listType = sDcRemoval.FieldType.GetGenericArguments()[1];
                foreach (ActiveDreamNode n in g.Kept)
                {
                    if (!W86GoneRefile(true, true, true, m.ActiveNodes.Contains(n)) || DcIndexHas(index, g.Key, n)) continue;
                    DcIndexAdd(index, g.Key, n, listType);
                    PatchCounters()[3530]++;
                }
            }
            catch (ResetException) { throw; }
            catch (Exception) { PatchCounters()[3533]++; }
        }

        // WishSubjectGoneCheck run: a test manager of its own (EA's private ctor: no listeners, not the active manager, so
        // no UI event and no sting) for the selected Sim holds a promised wish P and a wish K that is valid after its
        // subject's death, both filed under the id of a fresh dead SimDescription; EA's ObjectDestroyedDelegate runs with
        // kDnPSubjectDestroyed for that description (the notice is counted, not shown).
        //   Off: P leaves its slot with no notice, K stays active but unfiled (BROKEN). On: notice given, K filed.
        //   Controls: P removed and K kept active with either value (EA's own rule), pure decisions.
        public static object WishSubjectGoneCheck(string what)
        {
            string counters = B582Counters("wishsubjectgone", 3529, new[] { "notices given", "kept nodes filed again", "notices lost, switch off", "kept unfiled, switch off", "errors" }, sW86GMissing);
            if (what == "result") return counters;
            if (what != "run") return "wishsubjectgone <run|result>";
            bool on = ShortcutOn("wishsubjectgone");
            bool controls = W86GoneNotice(true, true, true) && !W86GoneNotice(false, true, true) && !W86GoneNotice(true, true, false)
                && W86GoneRefile(true, true, true, true) && !W86GoneRefile(true, false, true, true) && !W86GoneRefile(false, true, true, true);
            bool fixedNow = false;
            string live = "no selected Sim with a wish manager";
            Sim sim = W86CheckSim();
            if (sim != null && sW86GMissing.Length == 0 && sW86DeathStyle != null)
            {
                sW86Dry = true;
                int notices0 = sW86DryNotices;
                try
                {
                    DreamNodeInstance ip = W86FindInstance(sim, null, false), ik = W86FindInstance(sim, null, true);
                    if (ip == null || ik == null) { live = "no wish node instances"; controls = false; }
                    else
                    {
                        DreamsAndPromisesManager m = W86TestManager(sim);
                        SimDescription dead = new SimDescription();
                        sW86DeathStyle.SetValue(dead, W86SomeDeath());
                        DnPSubjectDestroyedEvent ev = new DnPSubjectDestroyedEvent(null, dead, false);
                        bool isDead;
                        object key = W86GoneKey(ev, out isDead);
                        ActiveDreamNode p = ActiveDreamNode.CreateNodeForPotentialStartTesting(sim, ip);
                        ActiveDreamNode k = ActiveDreamNode.CreateNodeForPotentialStartTesting(sim, ik);
                        IList active = sDcActive.GetValue(m) as IList;
                        active.Add(p); active.Add(k);
                        ((ActiveDreamNode[])sW86MPromise.GetValue(m))[1] = p;
                        p.PromiseDream();
                        IDictionary index = sDcRemoval.GetValue(m) as IDictionary;
                        Type listType = sDcRemoval.FieldType.GetGenericArguments()[1];
                        DcIndexAdd(index, key, p, listType);
                        DcIndexAdd(index, key, k, listType);
                        ((ProcessEventDelegate)Fixes.Bind(typeof(ProcessEventDelegate), m, sW86MDestroyed))(ev);
                        bool notice = sW86DryNotices > notices0;
                        bool keptFiled = DcIndexHas(index, key, k);
                        bool eaRule = isDead && !active.Contains(p) && !W86Promised(m, p) && active.Contains(k);
                        fixedNow = notice && keptFiled;
                        controls = controls && eaRule;
                        live = "failure notice=" + notice + ", kept wish still filed=" + keptFiled + ", EA removed the promised one and kept the other=" + eaRule;
                    }
                }
                catch (ResetException) { throw; }
                catch (Exception e) { live = "threw " + e.GetType().Name; controls = false; }
                finally { sW86Dry = false; }
            }
            else controls = false;
            return B582Line("wishsubjectgone", on, "a gone subject's promised wish gets EA's notice and kept wishes stay filed (" + live + ")", fixedNow,
                "EA's removal rule and the pure decisions", controls, sW86GMissing, counters);
        }

        static object W86SomeDeath()
        {
            foreach (object v in Enum.GetValues(typeof(SimDescription.DeathType)))
                if ((SimDescription.DeathType)v != SimDescription.DeathType.None) return v;
            return SimDescription.DeathType.None;
        }

        // ---- docs/623 §4 wishupdatelost ------------------------------------------------------------------------------------
        // [verified, ilspycmd EventTracker::ProcessEvent IL_01ef..IL_020d] the drain runs sNeedToUpdateList[0].Update() and
        // then Remove(sNeedToUpdateList[0]). SetToUpdate adds the manager only `if (!sNeedToUpdateList.Contains(this))`. So a
        // SetToUpdate for a manager made while the drain runs its Update (a wish completed by an event that Update itself
        // raised: OnCompletion's kPromisedDreamCompleted, RemoveActiveNode's events, RunFeedbackFunction ->
        // SetToUpdate(false) + AddCompletedNode) finds it listed, adds nothing, and the drain then removes it: the update
        // waits for some later, unrelated event on that Sim. Fix: SetToUpdate notes when it is called for the manager whose
        // Update is running while that manager is listed; after Update, when the drain is about to remove it (draining, it is
        // first in the list), it is added once more at the end, so the drain's Remove(list[0]) takes the first copy and the
        // requested update runs in the same drain, as SetToUpdate meant. Loop guard: at most 8 such re-adds in a row per
        // manager (counted).
        // Counters: [3534] updates kept (re-added), [3535] lost with the switch off, [3536] loop guard hits, [3537] errors.
        static object sW86Updating;
        static bool sW86Lost;
        static readonly Dictionary<object, int> sW86Chain = new Dictionary<object, int>();
        const int kW86ChainCap = 8;
        static object sW86TestMgr;   // check seam: this manager raises one SetToUpdate inside its own Update
        static bool sW86TestRaise;
        static int sW86TestUpdates;
        static string sW86UMissing = "";

        static void ApplyWishUpdateLost()
        {
            sW86UMissing = sW86Draining == null ? "EventTracker.sCurrentlyUpdatingDreamsAndPromisesManagers" : "";
            if (sW86UMissing.Length > 0) { AppliedLog().Add("wishupdatelost: not found: " + sW86UMissing); return; }
            Patch(typeof(DreamsAndPromisesManager), "SetToUpdate", new Type[] { typeof(bool), typeof(bool) }, typeof(W86MgrHook), "SetToUpdateHook", "SetToUpdateOrig");
            // Update: WlMgr.UpdateHook (WishListenerFixes.cs) calls W86UpdateBegin / W86UpdateEnd.
        }

        // Pure: re-add after Update? (EA: never)
        internal static bool W86Requeue(bool on, bool requestedDuringUpdate, bool drainRemovesIt, int chain)
        {
            return on && requestedDuringUpdate && drainRemovesIt && chain < kW86ChainCap;
        }

        static void W86SetToUpdateSeen(object mgr)
        {
            if (sW86Updating == null || !ReferenceEquals(sW86Updating, mgr)) return;
            try { if (DreamsAndPromisesManager.sNeedToUpdateList.Contains((DreamsAndPromisesManager)mgr)) sW86Lost = true; }
            catch (ResetException) { throw; }
            catch (Exception) { PatchCounters()[3537]++; }
        }

        internal static void W86UpdateBegin(object mgr, out object prev, out bool prevLost)
        {
            prev = sW86Updating; prevLost = sW86Lost;
            sW86Updating = mgr; sW86Lost = false;
        }

        internal static void W86UpdateEnd(object mgr, object prev, bool prevLost, bool ok)
        {
            if (ok && sW86TestMgr != null && ReferenceEquals(mgr, sW86TestMgr))
            {
                sW86TestUpdates++;
                if (sW86TestRaise) { sW86TestRaise = false; ((DreamsAndPromisesManager)mgr).SetToUpdate(false, false); }
            }
            bool lost = sW86Lost;
            sW86Updating = prev; sW86Lost = prevLost;
            if (!ok || sW86UMissing.Length > 0) return;
            try
            {
                if (!lost) { if (sW86Chain.Count > 0) sW86Chain.Remove(mgr); return; }
                List<DreamsAndPromisesManager> list = DreamsAndPromisesManager.sNeedToUpdateList;
                bool drainRemovesIt = (bool)sW86Draining.GetValue(null) && list.Count > 0 && ReferenceEquals(list[0], mgr);
                if (!drainRemovesIt) return;  // the manager stays listed: nothing lost
                bool on = ShortcutOn("wishupdatelost");
                int chain;
                sW86Chain.TryGetValue(mgr, out chain);
                if (!on) { PatchCounters()[3535]++; return; }
                if (!W86Requeue(on, true, true, chain)) { PatchCounters()[3536]++; sW86Chain.Remove(mgr); return; }
                sW86Chain[mgr] = chain + 1;
                list.Add((DreamsAndPromisesManager)mgr);
                PatchCounters()[3534]++;
            }
            catch (ResetException) { throw; }
            catch (Exception) { PatchCounters()[3537]++; }
        }

        // WishUpdateLostCheck run: a test manager of its own (no listeners, no nodes: its Update is EA's PartialUpdate over
        // empty lists) is listed with SetToUpdate and drained by a kEventNone SendEvent, the way every event drains; inside
        // its Update it requests one more update (as a wish completed by Update's own events does).
        //   Off: Update runs once, the request is lost (BROKEN). On: twice.
        //   Controls: the drain was idle (not inside another drain), pure decisions.
        public static object WishUpdateLostCheck(string what)
        {
            string counters = B582Counters("wishupdatelost", 3534, new[] { "updates kept", "lost, switch off", "loop guard", "errors" }, sW86UMissing);
            if (what == "result") return counters;
            if (what != "run") return "wishupdatelost <run|result>";
            bool on = ShortcutOn("wishupdatelost");
            bool controls = W86Requeue(true, true, true, 0) && !W86Requeue(false, true, true, 0) && !W86Requeue(true, false, true, 0)
                && !W86Requeue(true, true, false, 0) && !W86Requeue(true, true, true, kW86ChainCap);
            bool fixedNow = false;
            string live = "no selected Sim";
            Sim sim = PlumbBob.SelectedActor;
            if (sim != null && sW86UMissing.Length == 0)
            {
                DreamsAndPromisesManager m = null;
                try
                {
                    bool idle = !(bool)sW86Draining.GetValue(null);
                    m = W86TestManager(sim);
                    sW86TestMgr = m; sW86TestRaise = true; sW86TestUpdates = 0;
                    m.SetToUpdate(false, false);
                    EventTracker.SendEvent(new Event(EventTypeId.kEventNone));
                    int updates = sW86TestUpdates;
                    fixedNow = updates >= 2;
                    controls = controls && idle && updates >= 1;
                    live = "test manager updates in one drain=" + updates + " (EA: 1, the request made inside is lost)";
                }
                catch (ResetException) { throw; }
                catch (Exception e) { live = "threw " + e.GetType().Name; controls = false; }
                finally
                {
                    sW86TestMgr = null; sW86TestRaise = false;
                    if (m != null) { while (DreamsAndPromisesManager.sNeedToUpdateList.Remove(m)) { } sW86Chain.Remove(m); }
                }
            }
            else controls = false;
            return B582Line("wishupdatelost", on, "an update requested inside Update runs in the same drain (" + live + ")", fixedNow,
                "idle drain and the pure decisions", controls, sW86UMissing, counters);
        }

        // ---- docs/623 §5 socialworkerpromise -------------------------------------------------------------------------------
        // [verified, ilspycmd DreamsAndPromisesManager::ChildTakenBySocialWorkerCallback IL_0083 KillNode, IL_0097
        // CancelDream] for active nodes of four trees whose subject is the taken child, EA kills the node and takes it off the
        // displayed list; it is never removed. KillNode only sets mTimeActive = SatisfyTime, and Update times a node out only
        // when `!IsPromised` (ActiveDreamNode::Update), so a promised wish about the child stays in its promise slot for good
        // (the child is gone, it can never complete). EA's own path for a subject that left (ObjectDestroyedDelegate) removes
        // the node with RemoveActiveNode, which breaks the promise. Fix (EA's body runs unchanged): after it, each node EA
        // just killed here that is still promised (same filter: not one-off, Sim subject = the child, one of the four trees,
        // WasKilled) is removed with RemoveActiveNode. Unpromised ones keep EA's timeout path.
        // Counters: [3538] promised wishes removed, [3539] left in their slot with the switch off, [3540] errors.
        static readonly ulong[] kW86SwTrees = { 13278830765380689789uL, 556925676188524974uL, 418218285870389475uL, 1598595854461485554uL };
        static string sW86SMissing = "";

        static void ApplySocialWorkerPromise()
        {
            sW86SMissing = sW86MChild == null ? "ChildTakenBySocialWorkerCallback" : "";
            if (sW86SMissing.Length > 0) { AppliedLog().Add("socialworkerpromise: not found: " + sW86SMissing); return; }
            Patch(typeof(DreamsAndPromisesManager), "ChildTakenBySocialWorkerCallback", new Type[] { typeof(Event) }, typeof(W86MgrHook), "ChildTakenHook", "ChildTakenOrig");
        }

        // Pure: remove after EA's body? (EA: never; its timeout path skips promised nodes)
        internal static bool W86SwRemove(bool on, bool killedHere, bool promised)
        {
            return on && killedHere && promised;
        }

        static void W86ChildTakenAfter(object mgr, Event e)
        {
            try
            {
                Sim child = e == null ? null : e.TargetObject as Sim;
                if (child == null || child.SimDescription == null) return;
                ulong id = child.SimDescription.SimDescriptionId;
                DreamsAndPromisesManager m = (DreamsAndPromisesManager)mgr;
                List<ActiveDreamNode> hit = null;
                foreach (ActiveNodeBase a in m.ActiveNodes)
                {
                    ActiveDreamNode n = a as ActiveDreamNode;
                    if (n == null || n.IsOneOffNode || n.Subject == null || n.Subject.mType != DreamNodePrimitive.InputSubjectType.Sim || n.Subject.mSimDescriptionId != id) continue;
                    DreamNodeInstanceBase inst = n.GetNodeInstance();
                    if (inst == null || Array.IndexOf(kW86SwTrees, inst.OwnerTreeId) < 0 || !n.WasKilled || !n.IsPromised) continue;
                    if (hit == null) hit = new List<ActiveDreamNode>();
                    hit.Add(n);
                }
                if (hit == null) return;
                bool on = ShortcutOn("socialworkerpromise");
                foreach (ActiveDreamNode n in hit)
                {
                    if (!W86SwRemove(on, true, true)) { PatchCounters()[3539]++; continue; }
                    m.RemoveActiveNode(n);
                    PatchCounters()[3538]++;
                }
            }
            catch (ResetException) { throw; }
            catch (Exception) { PatchCounters()[3540]++; }
        }

        // SocialWorkerPromiseCheck run: a test manager of its own (not the active manager: no UI events) for the selected Sim
        // holds a promised wish P and an unpromised wish U from one of EA's four trees, both about the selected Sim; EA's
        // ChildTakenBySocialWorkerCallback runs with kChildTakenBySocialWorker for that Sim (the callback only reads it).
        //   Off: P stays active and in its promise slot (BROKEN). On: removed, promise broken.
        //   Controls: EA killed both, U stays active (EA's timeout path), pure decisions.
        public static object SocialWorkerPromiseCheck(string what)
        {
            string counters = B582Counters("socialworkerpromise", 3538, new[] { "promised wishes removed", "left in slot, switch off", "errors" }, sW86SMissing);
            if (what == "result") return counters;
            if (what != "run") return "socialworkerpromise <run|result>";
            bool on = ShortcutOn("socialworkerpromise");
            bool controls = W86SwRemove(true, true, true) && !W86SwRemove(false, true, true) && !W86SwRemove(true, false, true) && !W86SwRemove(true, true, false);
            bool fixedNow = false;
            string live = "no selected Sim with a wish manager";
            Sim sim = W86CheckSim();
            if (sim != null && sW86SMissing.Length == 0 && sW86MPromise != null)
            {
                try
                {
                    DreamNodeInstance inst = W86FindInstance(sim, kW86SwTrees, false);
                    if (inst == null) { live = "none of the four trees loaded"; controls = false; }
                    else
                    {
                        DreamsAndPromisesManager m = W86TestManager(sim);
                        ActiveNodeBase.NodeSubject subj = ActiveNodeBase.NodeSubject.Create(DreamNodePrimitive.InputSubjectType.Sim, sim);
                        ActiveDreamNode p = ActiveDreamNode.CreateNodeForPotentialStartTesting(sim, inst);
                        ActiveDreamNode u = ActiveDreamNode.CreateNodeForPotentialStartTesting(sim, inst);
                        p.SetSubject(subj); u.SetSubject(subj);
                        IList active = sDcActive.GetValue(m) as IList;
                        active.Add(p); active.Add(u);
                        ((ActiveDreamNode[])sW86MPromise.GetValue(m))[1] = p;
                        p.PromiseDream();
                        ((ProcessEventDelegate)Fixes.Bind(typeof(ProcessEventDelegate), m, sW86MChild))(new Event(EventTypeId.kChildTakenBySocialWorker, null, sim));
                        bool freed = !active.Contains(p) && !W86Promised(m, p);
                        bool eaRule = p.WasKilled && u.WasKilled && active.Contains(u);
                        fixedNow = freed;
                        controls = controls && eaRule;
                        live = "promised wish freed=" + freed + ", EA killed both and kept the unpromised one=" + eaRule;
                    }
                }
                catch (ResetException) { throw; }
                catch (Exception e) { live = "threw " + e.GetType().Name; controls = false; }
            }
            else controls = false;
            return B582Line("socialworkerpromise", on, "a taken child's promised wish leaves its slot (" + live + ")", fixedNow,
                "EA's kill and timeout rule and the pure decisions", controls, sW86SMissing, counters);
        }
    }
}
