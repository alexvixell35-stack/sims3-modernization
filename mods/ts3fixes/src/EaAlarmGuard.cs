using System;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // EA alarm guard (docs/651 §1). Several of our hooks sit on a method EA runs from a repeating alarm (AddAlarmDay /
    // AddAlarmRepeating) and call EA's original body unguarded. When EA's body throws, AlarmManager removes the
    // repeating alarm for good (alarmkilllog names our hook type, e.g. Ts3Fixes.Fixes+NpcShowHook.CreateNPCShow), so the
    // daily NPC show, plant growth, venue cleanup ... stop for the rest of the session. The hooks now run EA's body in
    // try/catch: a throw is counted, its type and first frame are logged (EaAlarmThrew, capped), and the alarm stays.
    // ResetException passes through (EA's own reset unwinding).
    // Counters [4110] NPC show alarm (NPCShowManager.CreateNPCShow) throws caught, [4111] other guarded alarm callbacks'
    // throws caught (plant growth, venue cleanup, relationship decay, consignment magic hour, Elvis gnome).
    public static partial class Fixes
    {
        static int sEagLines;

        internal static void EaAlarmThrew(string site, int counter, Exception e)
        {
            try
            {
                PatchCounters()[counter]++;
                if (!OwnBugsCore.LogMore(sEagLines, 24)) return;
                sEagLines++;
                AppliedLog().Add("eaalarmguard: " + site + " threw " + OwnBugsCore.Brief(e == null ? null : e.GetType().Name, e == null ? null : e.StackTrace)
                    + (e != null && e.InnerException != null ? ", inner " + OwnBugsCore.Brief(e.InnerException.GetType().Name, e.InnerException.StackTrace) : "")
                    + ", alarm kept, day " + SimClock.ElapsedTime(TimeUnit.Days).ToString("0") + " hour " + SimClock.HoursPassedOfDay.ToString("0.00"));
            }
            catch (Exception) { }
        }
    }
}
