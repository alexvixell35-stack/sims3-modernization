using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // alarmkilllog (tuning kAlarmKillLog, on: log only, changes nothing; docs/546 row 5).
    //
    // When a repeating alarm's callback throws, AlarmManager removes that alarm for the rest of the session, with no log
    // and no retry [verified, decompiled AlarmManager]: Simulate's catch (Exception) { if (repeating) InternalRemoveAlarm
    // (timer, true) } and, for yielding alarms, AlarmOneShot.HandleException does the same. Those two are the only callers
    // that pass bRemoveFromTimerQueue: true (RemoveAlarm and Simulate's own clean-ups do not use it that way), so a call
    // with true is exactly "a repeating alarm killed by an exception". One throw stops the feature behind it (a career's
    // work-day alarm, stray pet visits, a spawner, a mod's sweep) until the game restarts, and nobody sees why.
    // The hook writes one fix_applied line per callback method (the first 64 methods): method, target, owner, sim time.
    // Counters: [2308] repeating alarms killed by an exception, [2309] errors in the hook.
    public static partial class Fixes
    {
        static FieldInfo sAkCallBack, sAkOwner;
        static Dictionary<string, int> sAkSeen = new Dictionary<string, int>();

        static void ApplyAlarmKillLog()
        {
            Type timer = typeof(AlarmManager).GetNestedType("Timer", BindingFlags.NonPublic);
            if (timer == null) { AppliedLog().Add("alarmkilllog: Timer type not found"); return; }
            sAkCallBack = timer.GetField("CallBack");
            sAkOwner = timer.GetField("ObjectRef");
            Patch(typeof(AlarmManager), "InternalRemoveAlarm", new Type[] { timer, typeof(bool) }, typeof(AkHook), "RemoveHook", "RemoveOrig");
        }

        public class AkHook
        {
            public void RemoveHook(object t, bool bRemoveFromTimerQueue)
            {
                if (bRemoveFromTimerQueue && ShortcutOn("alarmkilllog"))
                {
                    try
                    {
                        PatchCounters()[2308]++;
                        Delegate cb = sAkCallBack != null ? sAkCallBack.GetValue(t) as Delegate : null;
                        object owner = sAkOwner != null ? sAkOwner.GetValue(t) : null;
                        string m = cb == null ? "null" : (cb.Method.DeclaringType != null ? cb.Method.DeclaringType.FullName + "." : "") + cb.Method.Name;
                        int n;
                        sAkSeen.TryGetValue(m, out n);
                        sAkSeen[m] = n + 1;
                        if (n == 0 && sAkSeen.Count <= 64)
                        {
                            DateAndTime now = SimClock.CurrentTime();
                            AppliedLog().Add("alarmkilllog: repeating alarm removed after its callback threw: " + m
                                + ", target " + (cb != null && cb.Target != null ? cb.Target.GetType().Name : "static")
                                + ", owner " + (owner != null ? owner.GetType().Name : "none")
                                + ", day " + SimClock.ElapsedTime(TimeUnit.Days).ToString("0") + " hour " + SimClock.HoursPassedOfDay.ToString("0.00"));
                        }
                    }
                    catch (Exception) { PatchCounters()[2309]++; }
                }
                RemoveOrig(t, bRemoveFromTimerQueue);
            }
            public void RemoveOrig(object t, bool bRemoveFromTimerQueue) { } // receives the original
        }
    }
}
