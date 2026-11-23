using System;
using Sims3.Gameplay.Careers;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // Rabbit-hole careers (docs/574 §8-9), each off by default. EA bug fixes; no Sim is moved.
    //
    // promoteshift (kPromoteShift, docs/574 §8) [verified, Occupation.IsTimeDuringWorkDay / Career.IsRegularWorkTime /
    //   WorkInRabbitHole wait loop]: on the calendar day a new level starts, IsTimeDuringWorkDay allows a shift when the
    //   Sim was not hired today and more than MinHoursBetweenShiftsAfterPromotion hours lie before the start, but never
    //   checks the work days: a night-shift Sim promoted after midnight going into a day off is "missing work" that
    //   night (performance loss on a day off). IsRegularWorkTime meanwhile answers false for that whole day, so a Sim sent
    //   to the allowed shift waits inside the rabbit hole until the next day. Fix: the same-day rule also requires a
    //   work day; IsRegularWorkTime applies the same same-day rule instead of refusing the whole day. Counters [2666]
    //   answers corrected, [2667] errors.
    // gotoworkstale (kGoToWorkStale, docs/574 §9) [verified code, trigger: a save in the 1-2 h before the push]: the
    //   "time to go to work" alarm is persisted but its handle is not, so after a load nothing removes it; when the Sim
    //   quits, is fired or retires before it fires, the old career's callback builds a work interaction from a null
    //   CareerLoc and throws (after a promotion that moved the hours, a stale push at the old time). Fix: the callback
    //   does nothing when the career is no longer the owner's occupation or has no location or level. Counters [2672]
    //   stale pushes skipped, [2673] errors.
    // Checks: CareerLogicChecks.cs.
    public static partial class Fixes
    {
        static void ApplyCareerLogic()
        {
            Patch(typeof(Occupation), "IsTimeDuringWorkDay", typeof(PromoteShiftHook), "DuringHook", "DuringOrig");
            Patch(typeof(Career), "IsRegularWorkTime", typeof(PromoteShiftHook), "RegularHook", "RegularOrig");
            Patch(typeof(Career), "RegularWorkDayGoToWorkHandle", typeof(GoToWorkStaleHook), "PushHook", "PushOrig");
        }

        // ---- promoteshift ----
        // The same-day-as-level-start answer: EA = notHiredToday && hoursUntilStart > minHours (no work-day check); on =
        // also a work day.
        internal static bool PsSameDay(bool on, bool hiredToday, double hoursUntilStart, double minHours, bool workDay)
        {
            if (hiredToday) return false;
            return hoursUntilStart > minHours && (!on || workDay);
        }

        public class PromoteShiftHook
        {
            public bool DuringHook(DateAndTime curTime)
            {
                try
                {
                    Occupation o = (Occupation)(object)this;
                    if (ShortcutOn("promoteshift") && o.WaitOneDayBeforeStartingJob && SimClock.IsSameDay(o.WhenCurLevelStarted, curTime)
                        && !SimClock.IsTimeBetweenTimes(o.WhenCurLevelStarted.Hour, o.StartTime, o.FinishTime))
                    {
                        bool workDay = (o.DaysOfWeekToWork & curTime.DayOfWeek) != 0;
                        bool ea = PsSameDay(false, SimClock.IsSameDay(o.DateHired, curTime), SimClock.HoursUntil(o.WhenCurLevelStarted, o.StartTime), o.MinHoursBetweenShiftsAfterPromotion, workDay);
                        bool fixedAnswer = PsSameDay(true, SimClock.IsSameDay(o.DateHired, curTime), SimClock.HoursUntil(o.WhenCurLevelStarted, o.StartTime), o.MinHoursBetweenShiftsAfterPromotion, workDay);
                        if (ea != fixedAnswer) { PatchCounters()[2666]++; return fixedAnswer; }
                    }
                }
                catch (Exception) { PatchCounters()[2667]++; }
                return DuringOrig(curTime);
            }
            public bool DuringOrig(DateAndTime curTime) { return false; }

            public bool RegularHook()
            {
                try
                {
                    Career c = (Career)(object)this;
                    DateAndTime now = SimClock.CurrentTime();
                    if (ShortcutOn("promoteshift") && c.CurLevel != null && SimClock.IsSameDay(c.WhenCurLevelStarted, now) && c.WaitOneDayBeforeStartingJob
                        && !SimClock.IsTimeBetweenTimes(c.WhenCurLevelStarted.Hour, c.StartTime, c.FinishTime)
                        && PsSameDay(true, SimClock.IsSameDay(c.DateHired, now), SimClock.HoursUntil(c.WhenCurLevelStarted, c.StartTime), c.MinHoursBetweenShiftsAfterPromotion, (c.DaysOfWeekToWork & now.DayOfWeek) != 0))
                    {
                        // the same-day shift IsTimeDuringWorkDay allows: EA's own window test for the rest of the day
                        float finish = c.CurLevel.FinishTime();
                        bool r = SimClock.IsTimeBetweenTimes(SimClock.HoursPassedOfDay, c.StartTime, finish)
                            && (finish > c.StartTime || SimClock.HoursPassedOfDay >= c.StartTime
                                ? (c.CurLevel.DaysToWork & SimClock.CurrentDayOfWeek) != 0
                                : (c.CurLevel.DaysToWork & SimClock.DayBefore(SimClock.CurrentDayOfWeek)) != 0);
                        if (r) PatchCounters()[2666]++;
                        return r;
                    }
                }
                catch (Exception) { PatchCounters()[2667]++; }
                return RegularOrig();
            }
            public bool RegularOrig() { return false; }
        }

        // ---- gotoworkstale ----
        internal static bool GwSkip(bool on, bool stillOwnersJob, bool hasLocation, bool hasLevel)
        {
            return on && (!stillOwnersJob || !hasLocation || !hasLevel);
        }

        public class GoToWorkStaleHook
        {
            public void PushHook()
            {
                try
                {
                    Career c = (Career)(object)this;
                    if (ShortcutOn("gotoworkstale") && GwSkip(true, c.OwnerDescription != null && c.OwnerDescription.Occupation == c, c.CareerLoc != null, c.CurLevel != null))
                    {
                        PatchCounters()[2672]++;
                        return;
                    }
                }
                catch (Exception) { PatchCounters()[2673]++; }
                PushOrig();
            }
            public void PushOrig() { }
        }
    }
}
