using System;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Services;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // serviceleave (tuning kServiceLeave, off by default; docs/564 §1). EA bug fixes for service NPCs (never household
    // Sims: only a ServiceSituation's own Worker is touched).
    //
    // (a) [verified, decompiled] Service states arm their one-shot timeouts as AlarmType.DeleteOnReset alarms owned by
    //     the worker: WaitToRoute -> TimeToRoute (arrive), HangAroundBeforeLeaving -> TimeToRoute (leave), Firefighter's
    //     wait -> TimeToGo, Repairman Fixing -> TimeUp (MaxFixingTime), Maid -> TimeToFinish (cleaning time). A Sim reset
    //     (EA's stuck reset, a reset from any mod) runs GameObject.DoReset -> AlarmManager.OnReset first, which deletes
    //     them, and then SituationComponent.OnReset -> Situation.OnReset, which no service situation overrides (only the
    //     repo man re-arms its own). The state keeps waiting for an alarm that no longer exists: a service that never
    //     arrives (and cannot be requested again while it is "on its way"), a maid or repairman who never stops working
    //     while one dirty / broken object is out of reach, and a service Sim who stands on the lot for good after the job.
    //     Fix: after a worker reset, the timeout of the current state is armed again exactly as EA arms it (same
    //     callback, same alarm type and owner, EA's tunable delay: the state's Tuning field or the service's static
    //     DelayBeforeArriving / DelayBeforeLeaving / MaxFixingTime; the maid's finish time from her start time and
    //     Maid.MinCleaningTime, at least 15 minutes). States with no delay found are left as EA has them (counted).
    //     Bartender situations are left alone (another fix family owns them).
    // (b) [verified, decompiled] RepomanSituation.Leave pushes its walk out through the lot's mailbox and does nothing
    //     when Mailbox.GetMailboxOnLot is null (a lot with its mailbox deleted, or a lot type GetMailboxOnLot skips): the
    //     repo man never drives away. Fix: with no mailbox, EA's next step (OnRouteOutOfHouse: drive away, Exit either way)
    //     runs at once.
    // (a, babysitter) Babysitter / Butler states arm the same kind of alarms: WaitToRoute (delay = the situation's
    //     DelayBeforeArriving(), Babysitter or Butler tuning), ParentsAreStillHome (3-minute CheckForParentsLeave, and for
    //     a hired sitter TimeToLeave at max(EndTime - now, MinBabysittingTime)), ParentsHaveLeft (3-minute
    //     CheckForParentsReturn, TimeToHunkerDown at ParentsAwayForRealTime), BabysitUntilParentsReturn (3-minute
    //     CheckForParentsReturn). After a reset the sitter never notices the parents leave or return, so never leaves.
    //     Re-armed the same way (EA's expressions, EA's 3-minute repeat).
    // (c) RepoWhileRoutingThrough / RepoTeleport push nothing when the lot has no household or the object is null (or,
    //     for the first, the service is not the repo man): the repo man stands for good. He leaves instead (SetToLeave).
    // Counters: [2522] babysitter periodic checks re-armed, [2523] repo-man no-op states left,
    // [2517] wait / leave timeouts re-armed, [2518] work-limit timeouts re-armed, [2519] repo man left with no
    // mailbox, [2520] states with no delay found, [2521] errors. Check: ServiceLeaveChecks.cs.
    public static partial class Fixes
    {
        static readonly MethodInfo sSlRepoOut = typeof(RepomanSituation.Leave).GetMethod("OnRouteOutOfHouse", kInstAll);

        static void ApplyServiceLeave()
        {
            Patch(typeof(Situation), "OnReset", typeof(SlResetHook), "ResetHook", "ResetOrig");
            if (sSlRepoOut != null) Patch(typeof(RepomanSituation.Leave), "Init", typeof(SlRepoHook), "InitHook", "InitOrig");
            else AppliedLog().Add("serviceleave: RepomanSituation.Leave.OnRouteOutOfHouse not found, repo man part not patched");
            Patch(typeof(RepomanSituation.RepoWhileRoutingThrough), "Init", typeof(SlRepoThroughHook), "InitHook", "InitOrig");
            Patch(typeof(RepomanSituation.RepoTeleport), "Init", typeof(SlRepoTeleportHook), "InitHook", "InitOrig");
        }

        // ---- decisions (also used by the check) ----
        // (a) which timeout a state re-arms: 0 none, 1 wait/leave, 2 work limit, 3 periodic check (babysitter, 3-minute repeat)
        internal static int SlRearmKind(bool on, bool isWorker, bool exited, bool bartender, string field, string method)
        {
            if (!on || !isWorker || exited || bartender) return 0;
            if (field == "mAlarmHandle" && (method == "TimeToRoute" || method == "TimeToGo")) return 1;
            if (field == "mBabysitterShouldLeaveAlarm" && method == "TimeToLeave") return 1;
            if ((field == "mTimeUpHandle" && method == "TimeUp") || (field == "mTimeToFinishHandle" && method == "TimeToFinish")
                || (field == "mBabysitterShouldHunkerDown" && method == "TimeToHunkerDown")) return 2;
            if ((field == "mParentsLeaveAlarm" && method == "CheckForParentsLeave") || (field == "mParentsReturnAlarm" && method == "CheckForParentsReturn")) return 3;
            return 0;
        }
        // (a) the maid's finish timeout belongs to her working states only (EA never clears the handle after it fires)
        internal static bool SlMaidWorking(string childState)
        {
            return childState == "Cleaning" || childState == "BeInappropriate" || childState == "StartCleaning";
        }
        // (c) a repo-man state whose Init pushes nothing (EA's own conditions) leaves instead of standing for good
        internal static bool SlRepoIdleLeave(bool on, bool isRepoman, bool hasHousehold, bool hasObject)
        {
            return on && (!isRepoman || !hasHousehold || !hasObject);
        }
        // (b) the repo man leaves through EA's next step: no mailbox to walk out by
        internal static bool SlRepoLeave(bool on, bool hasMailbox)
        {
            return on && !hasMailbox;
        }

        static FieldInfo SlField(Type t, string name)
        {
            for (; t != null; t = t.BaseType)
            {
                FieldInfo f = t.GetField(name, kInstAll | BindingFlags.DeclaredOnly);
                if (f != null) return f;
            }
            return null;
        }
        static MethodInfo SlMethod(Type t, string name)
        {
            for (; t != null; t = t.BaseType)
            {
                MethodInfo m = t.GetMethod(name, kInstAll | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
                if (m != null) return m;
            }
            return null;
        }
        // EA's delay for a state: its Tuning field (generic service states), else the service's static property.
        static float SlDelay(object state, Service svc, string prop)
        {
            FieldInfo tf = SlField(state.GetType(), "Tuning");
            object tuning = tf == null ? null : tf.GetValue(state);
            if (tuning != null)
            {
                FieldInfo d = tuning.GetType().GetField(prop, kInstAll);
                if (d != null && d.FieldType == typeof(float)) return (float)d.GetValue(tuning);
            }
            for (Type t = svc == null ? null : svc.GetType(); t != null; t = t.BaseType)
            {
                PropertyInfo p = t.GetProperty(prop, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (p != null && (p.PropertyType == typeof(float) || p.PropertyType == typeof(int))) return Convert.ToSingle(p.GetValue(null, null));
            }
            return -1f;
        }

        // Re-arm one state's timeout (field + callback) if EA had armed it; returns the kind re-armed (0 none).
        static int SlRearm(ServiceSituation svc, object state, string field, string method)
        {
            FieldInfo f = SlField(state.GetType(), field);
            if (f == null || f.FieldType != typeof(AlarmHandle)) return 0;
            MethodInfo m = SlMethod(state.GetType(), method);
            if (m == null) return 0;
            int kind = SlRearmKind(true, true, svc.SituationExited, svc.GetType().Name.IndexOf("Bartender", StringComparison.Ordinal) >= 0, field, method);
            if (kind == 0) return 0;
            AlarmHandle h = (AlarmHandle)f.GetValue(state);
            if (h == AlarmHandle.kInvalidHandle) return 0;  // EA had not armed it (or already cleared it)
            float hours;
            string name = state.GetType().Name;
            if (method == "TimeToFinish")
            {
                // EA leaves the handle set after TimeToFinish fired: re-arm only while the maid is still at work
                string child = svc.Child == null ? "" : svc.Child.GetType().Name;
                if (!SlMaidWorking(child)) return 0;
                FieldInfo st = SlField(state.GetType(), "mStartTime");
                float elapsed = st == null ? 0f : SimClock.ElapsedTime(TimeUnit.Hours, (DateAndTime)st.GetValue(state));
                hours = Math.Max(0.25f, Maid.MinCleaningTime - elapsed);
            }
            else if (method == "TimeUp") hours = SlDelay(state, svc.Service, "MaxFixingTime");
            else if (method == "TimeToLeave") hours = Math.Max(Babysitter.EndTime - SimClock.Hours24, Babysitter.MinBabysittingTime);  // EA's ParentsAreStillHome.Init
            else if (method == "TimeToHunkerDown") hours = Babysitter.ParentsAwayForRealTime;  // EA's ParentsHaveLeft.Init
            else if (kind == 3) hours = 0.05f;  // EA's 3-minute repeat
            else if (svc is BabysitterSituationBase && name == "WaitToRoute") hours = ((BabysitterSituationBase)svc).DelayBeforeArriving();  // EA: parent.DelayBeforeArriving() (Babysitter / Butler)
            else hours = SlDelay(state, svc.Service, name.StartsWith("WaitToRoute", StringComparison.Ordinal) ? "DelayBeforeArriving" : "DelayBeforeLeaving");
            if (hours < 0f) { PatchCounters()[2520]++; return 0; }
            AlarmManager.Global.RemoveAlarm(h);
            AlarmTimerCallback cb = (AlarmTimerCallback)Delegate.CreateDelegate(typeof(AlarmTimerCallback), state, m);
            if (kind == 3)
                f.SetValue(state, AlarmManager.Global.AddAlarmRepeating(3f, TimeUnit.Minutes, cb, 3f, TimeUnit.Minutes, "Babysitter check (re-armed after reset)", AlarmType.DeleteOnReset, svc.Worker));
            else
                f.SetValue(state, AlarmManager.Global.AddAlarm(hours, TimeUnit.Hours, cb, "Service Sim timeout (re-armed after reset)", AlarmType.DeleteOnReset, svc.Worker));
            return kind;
        }

        public class SlResetHook
        {
            public void ResetHook(Sim s)
            {
                ResetOrig(s);
                if (!ShortcutOn("serviceleave")) return;  // fast path: switch off
                try
                {
                    ServiceSituation svc = (object)this as ServiceSituation;
                    if (svc == null || s == null || s != svc.Worker || svc.SituationExited) return;
                    object[] states = new object[] { svc, svc.Child };
                    foreach (object st in states)
                    {
                        if (st == null) continue;
                        foreach (string[] fm in new string[][] { new string[] { "mAlarmHandle", "TimeToRoute" }, new string[] { "mAlarmHandle", "TimeToGo" },
                                                                 new string[] { "mTimeUpHandle", "TimeUp" }, new string[] { "mTimeToFinishHandle", "TimeToFinish" },
                                                                 new string[] { "mBabysitterShouldLeaveAlarm", "TimeToLeave" }, new string[] { "mBabysitterShouldHunkerDown", "TimeToHunkerDown" },
                                                                 new string[] { "mParentsLeaveAlarm", "CheckForParentsLeave" }, new string[] { "mParentsReturnAlarm", "CheckForParentsReturn" } })
                        {
                            int k = SlRearm(svc, st, fm[0], fm[1]);
                            if (k == 1) PatchCounters()[2517]++;
                            else if (k == 2) PatchCounters()[2518]++;
                            else if (k == 3) PatchCounters()[2522]++;
                        }
                    }
                }
                catch (Exception) { PatchCounters()[2521]++; }
            }
            public void ResetOrig(Sim s) { } // receives Situation.OnReset's original body
        }

        public class SlRepoHook
        {
            public void InitHook(RepomanSituation parent)
            {
                InitOrig(parent);
                if (!ShortcutOn("serviceleave")) return;  // fast path: switch off
                try
                {
                    object st = this;
                    Situation leave = (Situation)st;
                    if (parent == null || parent.Worker == null || leave.SituationExited || !object.ReferenceEquals(parent.Child, st)) return;
                    if (!SlRepoLeave(true, Mailbox.GetMailboxOnLot(leave.Lot) != null)) return;
                    PatchCounters()[2519]++;
                    sSlRepoOut.Invoke(st, new object[] { parent.Worker, 0f });
                }
                catch (Exception) { PatchCounters()[2521]++; }
            }
            public void InitOrig(RepomanSituation parent) { } // receives RepomanSituation.Leave.Init's original body
        }
    
        // (c) EA's RepoWhileRoutingThrough / RepoTeleport Init push the repossession only when the service is the repo man
        //     (RepoWhileRoutingThrough), the lot has a household and there is an object; otherwise nothing ever moves the
        //     situation on. After EA's Init, in exactly those cases, the repo man leaves (EA's SetToLeave).
        static void SlRepoIdle(object st, RepomanSituation parent, bool needRepoman, object target)
        {
            if (!ShortcutOn("serviceleave")) return;  // fast path: switch off
            try
            {
                Situation state = (Situation)st;
                if (parent == null || parent.Worker == null || state.SituationExited || !object.ReferenceEquals(parent.Child, st)) return;
                bool isRepo = !needRepoman || parent.Service is Repoman;
                if (!SlRepoIdleLeave(true, isRepo, parent.Lot != null && parent.Lot.Household != null, target != null)) return;
                PatchCounters()[2523]++;
                parent.SetToLeave();
            }
            catch (Exception) { PatchCounters()[2521]++; }
        }

        public class SlRepoThroughHook
        {
            public void InitHook(RepomanSituation parent)
            {
                InitOrig(parent);
                object st = this;
                SlRepoIdle(st, parent, true, ((RepomanSituation.RepoWhileRoutingThrough)st).mObjectToRepossess);
            }
            public void InitOrig(RepomanSituation parent) { } // receives RepomanSituation.RepoWhileRoutingThrough.Init's original body
        }

        public class SlRepoTeleportHook
        {
            public void InitHook(RepomanSituation parent)
            {
                InitOrig(parent);
                object st = this;
                SlRepoIdle(st, parent, false, ((RepomanSituation.RepoTeleport)st).mObjectToRepossess);
            }
            public void InitOrig(RepomanSituation parent) { } // receives RepomanSituation.RepoTeleport.Init's original body
        }
}
}
