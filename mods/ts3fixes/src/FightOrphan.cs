using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Objects;
using Sims3.Gameplay.Utilities;

namespace Ts3Fixes
{
    // fightorphanwait (tuning kFightOrphanWait, ON since the 2026-11-20 import). SnowWaterFightWait.Run idles in DoLoop(ExitReason.Default)
    // and nothing in Wait ends that loop itself: the exit reason comes from the fight's 1-minute ControllerCallback alarm
    // (Finished from DoEndOfGame, a pushed Throw) or from outside. ControllerCallback first runs ValidatePlayers (a slot whose
    // Sim's head interaction is not a fight interaction is cleared), then with NumPlayers == 0 and ReferenceList.Count > 0 it
    // returns without doing anything, every minute, for good. A Sim still in Wait that the fight no longer holds in
    // PlayerSlots is never given an exit reason by it, and while it references the fight the fight never ends either.
    // [verified: decompiled SnowWaterFight / SnowWaterFightWait.Run / ValidatePlayers / ControllerCallback / DoEndOfGame,
    // EA 1.67 and simler90 UPDATE242 bodies identical. Seen: townies Matthew Mango (etl13 af18-ip-31363) and Doug Rodel
    // (etl7 abip2-oo), Isla Paradiso beaches, in Wait to the end of the run. How they fell out of the slots is inferred
    // (likely IdleLoop's Move overwriting a slot TryAddPlayer had just handed to a joiner: GetGoodSlotToJoin does not look
    // at PlayersWhoShouldMove).] The same endless Wait follows when the controller alarm is gone (a repeating alarm whose
    // callback threw is removed for good by AlarmManager.Simulate) or the fight object was destroyed.
    // Fix (hourly pass, ErrorTrapSims' alarm): a non-arena Wait whose fight is destroyed, has no live controller alarm, or
    // does not hold the Sim in PlayerSlots is noted; if the same Wait instance is still in that state on the next pass
    // (>= 60 sim min, so no join, move or end-of-game transition is mid-way), it gets ExitReason.CanceledByScript and Wait
    // takes EA's own leave path (Exit animation, QuitFight, RouteAway). A cancel: household, selected and unknown-scope Sims
    // are never touched (IsPlayerSim, not the HouseholdToo-liftable HouseholdExempt). Arenas are left alone (EA's arena
    // game-over path differs). Counters [1345] Waits ended, [1346] orphaned Waits left in place per pass (switch off),
    // [1347] errors.
    public static partial class Fixes
    {
        static readonly FieldInfo sAlarmTimers = typeof(AlarmManager).GetField("mTimers", kInstAll);
        // Sim -> the Wait instance found orphaned at the previous pass
        static Dictionary<Sim, InteractionInstance> sFightOrphans = new Dictionary<Sim, InteractionInstance>();

        static void FightOrphanPass()
        {
            bool on = ShortcutOn("fightorphanwait");
            int[] c = PatchCounters();
            Dictionary<Sim, InteractionInstance> now = new Dictionary<Sim, InteractionInstance>();
            foreach (Sim s in new List<Sim>(LotManager.Actors))
            {
                try
                {
                    if (s == null || s.HasBeenDestroyed || s.InteractionQueue == null) continue;
                    SnowWaterFight.SnowWaterFightWait w = s.InteractionQueue.GetCurrentInteraction() as SnowWaterFight.SnowWaterFightWait;
                    if (w == null || IsPlayerSim(s) || !FightOrphaned(s, w.Target)) continue;
                    InteractionInstance was = null;
                    if (!sFightOrphans.TryGetValue(s, out was) || was != w) { now[s] = w; continue; }
                    if (on) { s.AddExitReason(ExitReason.CanceledByScript); c[1345]++; }
                    else { c[1346]++; now[s] = w; }
                }
                catch (Exception) { c[1347]++; }
            }
            sFightOrphans = now;
        }

        // true = EA's controller can no longer end this Sim's Wait: fight gone, controller alarm gone, or Sim not in its slots
        static bool FightOrphaned(Sim s, SnowWaterFight f)
        {
            if (f == null || f.HasBeenDestroyed) return true;
            if (f is SnowWaterFightArena) return false;
            if (!FightAlarmLive(f)) return true;
            Sim[] slots = sFightSlots == null ? null : sFightSlots.GetValue(f) as Sim[];
            return slots != null && Array.IndexOf(slots, s) < 0;
        }

        // the alarm sits in the fight lot's AlarmManager (GameObject.AddAlarmRepeating), or Global with no lot; unreadable = live
        static bool FightAlarmLive(SnowWaterFight f)
        {
            AlarmHandle h = f.ControllerRepeatingAlarm;
            if (h == AlarmHandle.kInvalidHandle) return false;
            if (sAlarmTimers == null) return true;
            Lot lot = f.LotCurrent;
            IDictionary a = lot != null && lot.AlarmManager != null ? sAlarmTimers.GetValue(lot.AlarmManager) as IDictionary : null;
            IDictionary g = AlarmManager.Global == null ? null : sAlarmTimers.GetValue(AlarmManager.Global) as IDictionary;
            if (a == null && g == null) return true;
            return a != null && a.Contains(h) || g != null && g.Contains(h);
        }
    }
}
