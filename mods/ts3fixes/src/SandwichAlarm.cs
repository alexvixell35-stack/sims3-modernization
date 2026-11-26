using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Objects.RabbitHoles;
using Sims3.Gameplay.Utilities;

namespace Ts3Fixes
{
    // sandwichalarm (tuning kSandwichAlarm, off by default; docs/580 §16). EA bug fix: a theatre (MovieCineplex too) gains a
    // daily "Reset sandwich boards" alarm that is never removed.
    //
    // [verified, decompiled] Theatre.OnCreation adds the daily alarm mEveningShowSandwichResetBoardAlarm.
    // StartPlayerConcert / StartPlayerConvention remove it; EndPlayerConcert / EndPlayerConvention add a one-shot
    // (TimeAfterShowToReset hours) that runs ResetSandwichBoardFromPlayerConcert, which adds a NEW daily alarm and
    // overwrites the handle without removing the one it holds. When a second show starts before the first show's one-shot
    // fires (back-to-back gigs), that one-shot re-adds the daily alarm after the start removed it, and the second show's
    // one-shot adds another: the first is orphaned and repeats every day, saved with the lot (AlwaysPersisted).
    // Measured (phase-20261115-1506): duplicate Theatre.ResetSandwichBoards (MovieCineplex) 0 -> 17 in one run, +2 per
    // snapshot. Each one only resets the board graphic (idempotent), so the cost is alarm-queue growth.
    //
    // Fix: before EA's ResetSandwichBoardFromPlayerConcert, the handle it is about to overwrite is removed (EA's own
    // RemoveAlarm, as the Start* paths do). The board and the one daily alarm stay EA's. Duplicates already in a save:
    // at world load (switch on) each theatre keeps the ResetSandwichBoards alarm whose handle it holds and its other
    // ResetSandwichBoards alarms are removed (SandwichAlarmWorldLoad; other callbacks and other theatres untouched).
    // Also (batch 9, 2026-11-15 review): the one-shot's handle is never stored, so a second player concert or convention
    // that starts before the first show's one-shot fires cannot cancel it: it resets the board in the middle of the second
    // show and re-adds the daily alarm that show's Start removed. With the switch on, the one-shot does nothing while a
    // player show (concert, psychic or game convention) is running; that show's own End adds the next one-shot.
    // Counted in [2800] / [2801].
    // Counters: [2800] stale handles removed or mid-show one-shots skipped (on), [2801] the same seen with the switch
    // off, [2802] errors,
    // [2803] saved duplicates removed at load.
    // Check: SandwichAlarmChecks.cs.
    public static partial class Fixes
    {
        static readonly FieldInfo sSaHandle = typeof(Theatre).GetField("mEveningShowSandwichResetBoardAlarm", kInstAll);
        static string sSaMissing = "";

        static void ApplySandwichAlarm()
        {
            sSaMissing = (sSaHandle == null ? "Theatre.mEveningShowSandwichResetBoardAlarm " : "")
                + (typeof(Theatre).GetMethod("ResetSandwichBoardFromPlayerConcert", kInstAll, null, Type.EmptyTypes, null) == null ? "Theatre.ResetSandwichBoardFromPlayerConcert" : "");
            sSaMissing = sSaMissing.Trim();
            if (sSaMissing.Length > 0) { AppliedLog().Add("sandwichalarm: not found: " + sSaMissing); return; }
            Patch(typeof(Theatre), "ResetSandwichBoardFromPlayerConcert", Type.EmptyTypes, typeof(SaHook), "ResetHook", "ResetOrig");
        }

        // Pure: remove the held handle before EA overwrites it?
        internal static bool SaRemoveFirst(bool on, bool handleValid)
        {
            return on && handleValid;
        }

        static readonly FieldInfo sSaShow = typeof(Theatre).GetField("mCurShowType", kInstAll);

        // Pure: skip EA's one-shot because a player show is running?
        internal static bool SaSkipMidShow(bool on, ShowVenue.ShowTypes cur)
        {
            return on && (cur == ShowVenue.ShowTypes.kPlayerConcert || cur == ShowVenue.ShowTypes.kPsychicConvention || cur == ShowVenue.ShowTypes.kGameConvention);
        }

        static readonly FieldInfo sSaTimers = typeof(AlarmManager).GetField("mTimers", kInstAll);

        // Pure: which handles to remove, given each handle's callback name, whether its callback targets this theatre and
        // whether it is the handle the theatre holds. Only this theatre's ResetSandwichBoards alarms other than the held one;
        // when none is held (a stale handle), the first is kept and becomes the held one.
        internal static List<int> SaPick(string[] callback, bool[] mine, bool[] held)
        {
            List<int> r = new List<int>();
            bool heldSeen = false;
            for (int i = 0; i < callback.Length; i++)
                if (callback[i] == "ResetSandwichBoards" && mine[i]) { if (held[i]) heldSeen = true; else r.Add(i); }
            if (!heldSeen && r.Count > 0) r.RemoveAt(0);  // the held handle is stale: keep one daily alarm (SaKept)
            return r;
        }

        // World load (from Instantiator): remove saved duplicates. One pass over each theatre's alarm manager.
        internal static void SandwichAlarmWorldLoad()
        {
            if (!ShortcutOn("sandwichalarm") || sSaHandle == null || sSaTimers == null) return;
            try
            {
                foreach (Theatre t in Sims3.Gameplay.Queries.GetObjects<Theatre>())
                {
                    AlarmManager m = t.LotCurrent != null && t.LotCurrent.AlarmManager != null ? t.LotCurrent.AlarmManager : AlarmManager.Global;
                    IDictionary timers = sSaTimers.GetValue(m) as IDictionary;
                    if (timers == null) continue;
                    AlarmHandle held = (AlarmHandle)sSaHandle.GetValue(t);
                    List<AlarmHandle> keys = new List<AlarmHandle>();
                    List<string> cbs = new List<string>(); List<bool> mine = new List<bool>(), isHeld = new List<bool>();
                    foreach (DictionaryEntry e in timers)
                    {
                        IList l = e.Value as IList;
                        object first = l != null && l.Count > 0 ? l[0] : null;
                        FieldInfo cbf = first != null ? first.GetType().GetField("CallBack", kInstAll) : null;
                        Delegate cb = cbf != null ? cbf.GetValue(first) as Delegate : null;
                        if (cb == null) continue;
                        keys.Add((AlarmHandle)e.Key); cbs.Add(cb.Method.Name); mine.Add(object.ReferenceEquals(cb.Target, t));
                        isHeld.Add((AlarmHandle)e.Key == held);
                    }
                    List<int> drop = SaPick(cbs.ToArray(), mine.ToArray(), isHeld.ToArray());
                    if (drop.Count > 0 && !isHeld.Contains(true))
                        for (int k = 0; k < keys.Count; k++)
                            if (cbs[k] == "ResetSandwichBoards" && mine[k] && !drop.Contains(k)) { sSaHandle.SetValue(t, keys[k]); break; }
                    foreach (int i in drop)
                    {
                        m.RemoveAlarm(keys[i]);
                        PatchCounters()[2803]++;
                    }
                }
            }
            catch (Exception) { PatchCounters()[2802]++; }
        }

        public class SaHook
        {
            public void ResetHook()
            {
                try
                {
                    Theatre t = (Theatre)(object)this;
                    if (sSaShow != null)
                    {
                        ShowVenue.ShowTypes cur = (ShowVenue.ShowTypes)sSaShow.GetValue(t);
                        if (SaSkipMidShow(true, cur))
                        {
                            if (ShortcutOn("sandwichalarm")) { PatchCounters()[2800]++; return; }
                            PatchCounters()[2801]++;
                        }
                    }
                    AlarmHandle h = (AlarmHandle)sSaHandle.GetValue(t);
                    bool valid = h != AlarmHandle.kInvalidHandle;
                    if (SaRemoveFirst(true, valid))
                    {
                        if (ShortcutOn("sandwichalarm"))
                        {
                            PatchCounters()[2800]++;
                            t.RemoveAlarm(h);
                            sSaHandle.SetValue(t, AlarmHandle.kInvalidHandle);
                        }
                        else PatchCounters()[2801]++;
                    }
                }
                catch (Exception) { PatchCounters()[2802]++; }
                ResetOrig();
            }
            public void ResetOrig() { } // receives Theatre.ResetSandwichBoardFromPlayerConcert's original body
        }
    }
}
