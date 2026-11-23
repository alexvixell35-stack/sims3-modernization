using System;
using System.Reflection;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.Situations;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // visitgreetalarm (tuning kVisitGreetAlarm, off by default; docs/565 §1). EA bug fix in the visit greeting state.
    //
    // [verified, decompiled] VisitSituation.VisitorWantsToComeIn.Init arms mWaitForGreetingHandle (sMaxWaitForGreeting =
    // 1 h, AlarmType.AlwaysPersisted, owned by the guest) whenever a host is asked to invite the guest in. When the host's
    // InviteInOrRefuseEntrance fails, InvitedInFailed retries Init up to kInviteInNumRetries times (2 sim-minutes apart),
    // and each Init overwrites the handle with a new alarm. CleanUp removes only the last one; the earlier alarms belong to
    // the guest, not to the situation, so AlarmManager.OnDispose(situation) does not remove them either. One hour after the
    // first try a leaked alarm calls WaitedTooLongForGreeting on the old state object, whose readonly Parent still points
    // at the live visit: the visit that is now Socializing is switched to GuestLeavesAfterWaitingTooLong (an NPC guest who
    // was let in walks home; the hosts drop their wish to talk to a selectable guest), or a visit that already ended is
    // given a new state and its NPC guest is sent home from wherever it is now. The leaked alarms are saved with the game.
    // Hosts are NPCs on this path (a selectable host never takes the retry), so it hits the player's Sim visiting an NPC
    // home and the NPC guests there.
    //
    // Fix: (a) a re-armed Init keeps the alarm EA armed first (the deadline that fires first in vanilla too) and drops the
    // new one, so nothing leaks; (b) WaitedTooLongForGreeting does nothing when its state is no longer the visit's current
    // state or the visit has ended (also covers alarms already leaked into saves). Same timing as vanilla in the live state.
    //
    // Counters: [2524] re-armed wait alarms dropped, [2525] stale callbacks ignored, [2526] errors. Check: VisitGreetAlarmChecks.cs.
    public static partial class Fixes
    {
        static readonly FieldInfo sVgaWait = typeof(VisitSituation.VisitorWantsToComeIn).GetField("mWaitForGreetingHandle", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        static void ApplyVisitGreetAlarm()
        {
            if (sVgaWait == null || sVgaWait.FieldType != typeof(AlarmHandle))
            {
                AppliedLog().Add("visitgreetalarm: VisitorWantsToComeIn.mWaitForGreetingHandle not found, not patched");
                return;
            }
            Patch(typeof(VisitSituation.VisitorWantsToComeIn), "Init", typeof(VgaInitHook), "InitHook", "InitOrig");
            Patch(typeof(VisitSituation.VisitorWantsToComeIn), "WaitedTooLongForGreeting", typeof(VgaWaitHook), "WaitHook", "WaitOrig");
        }

        // ---- decisions (also used by the check) ----
        // (a) after a re-run Init: drop the new wait alarm when EA's earlier one is still pending
        internal static bool VgaDropNew(bool on, bool hadOld, bool oldPending, bool replaced)
        {
            return on && hadOld && oldPending && replaced;
        }
        // (b) the timeout belongs to a state that is no longer current (or a visit that has ended)
        internal static bool VgaStale(bool on, bool isCurrentChild, bool visitExited)
        {
            return on && (!isCurrentChild || visitExited);
        }

        public class VgaInitHook
        {
            public void InitHook(VisitSituation parent)
            {
                if (!ShortcutOn("visitgreetalarm")) { InitOrig(parent); return; }  // fast path: switch off
                object st = this;
                AlarmHandle before = AlarmHandle.kInvalidHandle;
                try { before = (AlarmHandle)sVgaWait.GetValue(st); }
                catch (Exception) { PatchCounters()[2526]++; }
                InitOrig(parent);
                try
                {
                    AlarmHandle after = (AlarmHandle)sVgaWait.GetValue(st);
                    bool hadOld = before != AlarmHandle.kInvalidHandle;
                    bool pending = hadOld && AlarmManager.Global.GetTimeLeft(before, TimeUnit.Minutes) > 0f;
                    if (!VgaDropNew(true, hadOld, pending, after != before)) return;
                    AlarmManager.Global.RemoveAlarm(after);
                    sVgaWait.SetValue(st, before);
                    PatchCounters()[2524]++;
                }
                catch (Exception) { PatchCounters()[2526]++; }
            }
            public void InitOrig(VisitSituation parent) { } // receives VisitSituation.VisitorWantsToComeIn.Init's original body
        }

        public class VgaWaitHook
        {
            public void WaitHook()
            {
                if (ShortcutOn("visitgreetalarm"))
                {
                    try
                    {
                        object st = this;
                        VisitSituation.VisitorWantsToComeIn state = (VisitSituation.VisitorWantsToComeIn)st;
                        VisitSituation visit = state.Parent;
                        if (VgaStale(true, visit != null && object.ReferenceEquals(visit.Child, state), visit == null || visit.SituationExited))
                        {
                            PatchCounters()[2525]++;
                            return;
                        }
                    }
                    catch (Exception) { PatchCounters()[2526]++; }
                }
                WaitOrig();
            }
            public void WaitOrig() { } // receives VisitSituation.VisitorWantsToComeIn.WaitedTooLongForGreeting's original body
        }
    }
}
