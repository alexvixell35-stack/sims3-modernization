using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.EventSystem;
using Sims3.Gameplay.Objects;
using Sims3.Gameplay.Objects.Miscellaneous;
using Sims3.Gameplay.Seasons;
using Sims3.Gameplay.Situations;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // Seasons logic fixes (docs/571 §5-7), each off by default.
    //
    // trickortreatalarm (kTrickOrTreatAlarm, docs/571 §5) [verified, HolidayManager.cs / TrickOrTreatSituation.cs]: on the
    //   Fall holiday HolidayManager adds the 19:00 "NPC Trick Or Treat Alarm" with AlarmManager.AddAlarmDay, a WEEKLY
    //   repeating, always-persisted alarm, and keeps its handle in the plain static TrickOrTreatSituation.NPCTrickOrTreatAlarm;
    //   only SendNPCTrickOrTreaters removes it, through that static. After a save/load on Spooky Day (before 19:00) the
    //   alarm is back from the save but the static is invalid: it is never removed, and NPC children go trick-or-treating
    //   every week on that weekday, in every season. Quitting that day keeps a stale handle instead: the next world's
    //   Spooky Day sees a "valid" handle and adds no alarm (no NPC trick-or-treaters), and the removal can hit an unrelated
    //   alarm. Fix: SendNPCTrickOrTreaters does nothing unless today is the Fall holiday (so orphaned alarms, including
    //   those already in saves, have no effect), and the static is set back to invalid on world quit.
    //   Counters [3969] handles reset at quit, [3970] off-holiday runs skipped, [3971] errors.
    // snowfightjoinwait (kSnowFightJoinWait, docs/571 §6) [verified, SnowWaterFight.cs / SnowWaterFightArena.cs]: the
    //   controller ends a game once mWaitForJoinsCounter (minutes with one player while invitees are still walking over)
    //   reaches 40. Only DoReset clears the counter; the festival arena is permanent and its DoEndOfGame does not, so the
    //   minutes add up over the arena's life and later games end the first minute only one player has arrived. The
    //   targeted-player state is also left over. Fix: after the arena's DoEndOfGame the counter and targeted-player state
    //   are cleared, as DoReset does. Counters [2591] arenas reset at game end, [2592] errors.
    // hauntedumbrella (kHauntedUmbrella, docs/571 §7) [verified, HauntedHouse.cs]: HauntedHouse.Enter.CanStartUsingUmbrella
    //   returns !mCanRouteUsingUmbrella. The flag starts true and is set false only for the panic, vomit and wet-self exits,
    //   so Sims queuing for and leaving the haunted house in rain never open an umbrella, while the panicking ones may.
    //   Fix: return the flag. Counters [2593] answers changed, [2594] errors.
    // npcseasonalparty (kNpcSeasonalParty, docs/572 §3) [verified, NpcSeasonalPartyManager.cs]: at holiday midnight
    //   OnHolidayDayStarted registers a static "player scheduled a party" listener (costume / house / pool / feast party
    //   event by season) and an always-persisted alarm that schedules the NPC holiday party; the listener cancels that
    //   alarm when the player throws their own party. Neither static is saved and nothing re-registers the listener, so
    //   after a save/load on the holiday the player's party no longer cancels the NPC one and the household is invited to
    //   both. Fix: at world load on a holiday, a listener for the season's party event is registered again; when the NPC
    //   party alarm fires on a day the player threw their party, the NPC party is not scheduled (EA's intended outcome).
    //   Counters [2609] listeners registered at load, [2610] NPC parties skipped, [2611] errors.
    // Checks: SeasonsLogicChecks.cs.
    public static partial class Fixes
    {
        const BindingFlags kSlInst = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        static readonly FieldInfo sSfCounter = typeof(SnowWaterFight).GetField("mWaitForJoinsCounter", kSlInst);
        static readonly FieldInfo sSfTargeted = typeof(SnowWaterFight).GetField("TargetedPlayer", kSlInst);
        static readonly FieldInfo sSfThrows = typeof(SnowWaterFight).GetField("mTargetedThrows", kSlInst);
        static readonly Type sHuEnter = typeof(HauntedHouse).GetNestedType("Enter", BindingFlags.NonPublic | BindingFlags.Public);
        static readonly FieldInfo sHuFlag = sHuEnter == null ? null : sHuEnter.GetField("mCanRouteUsingUmbrella", kSlInst);
        static bool sTtHooked;
        static readonly FieldInfo sSpEaListener = typeof(NpcSeasonalPartyManager).GetField("sPlayerScheduledPartyEvent", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        static EventListener sSpListener;
        static DateAndTime sSpThrew = DateAndTime.Invalid;

        internal static string SfMissing()
        {
            List<string> m = new List<string>();
            if (sSfCounter == null) m.Add("SnowWaterFight.mWaitForJoinsCounter");
            if (sSfTargeted == null) m.Add("SnowWaterFight.TargetedPlayer");
            if (sSfThrows == null) m.Add("SnowWaterFight.mTargetedThrows");
            return string.Join(", ", m.ToArray());
        }

        internal static string HuMissing()
        {
            if (sHuEnter == null) return "HauntedHouse.Enter";
            return sHuFlag == null ? "HauntedHouse.Enter.mCanRouteUsingUmbrella" : "";
        }

        static void ApplySeasonsLogic()
        {
            Patch(typeof(TrickOrTreatSituation), "SendNPCTrickOrTreaters", typeof(TrickOrTreatHook), "SendHook", "SendOrig");
            Patch(typeof(NpcSeasonalPartyManager), "ScheduleNpcParty", typeof(SeasonalPartyHook), "ScheduleHook", "ScheduleOrig");
            if (!sTtHooked)
            {
                sTtHooked = true;
                World.OnWorldQuitEventHandler += delegate { TrickOrTreatQuit(); sSpListener = null; sSpThrew = DateAndTime.Invalid; };
            }
            if (SfMissing().Length > 0) AppliedLog().Add("snowfightjoinwait: members not found (" + SfMissing() + "), not patched");
            else Patch(typeof(SnowWaterFightArena), "DoEndOfGame", typeof(SnowFightHook), "EndHook", "EndOrig");
            if (HuMissing().Length > 0) AppliedLog().Add("hauntedumbrella: members not found (" + HuMissing() + "), not patched");
            else Patch(sHuEnter, "CanStartUsingUmbrella", typeof(HauntedUmbrellaHook), "UmbrellaHook", "UmbrellaOrig");
        }

        // ---- trickortreatalarm ----
        internal static bool TtSkip(bool on, bool fallHolidayToday)
        {
            return on && !fallHolidayToday;
        }

        static bool TtFallHolidayToday()
        {
            HolidayManager hm = HolidayManager.Instance;
            return hm != null && hm.IsFallHoliday;
        }

        public class TrickOrTreatHook
        {
            public static void SendHook()
            {
                try
                {
                    if (TtSkip(ShortcutOn("trickortreatalarm"), TtFallHolidayToday())) { PatchCounters()[3970]++; return; }
                }
                catch (Exception) { PatchCounters()[3971]++; }
                SendOrig();
            }
            public static void SendOrig() { }
        }

        static void TrickOrTreatQuit()
        {
            try
            {
                if (!ShortcutOn("trickortreatalarm") || TrickOrTreatSituation.NPCTrickOrTreatAlarm == AlarmHandle.kInvalidHandle) return;
                TrickOrTreatSituation.NPCTrickOrTreatAlarm = AlarmHandle.kInvalidHandle;
                PatchCounters()[3969]++;
            }
            catch (Exception) { PatchCounters()[3971]++; }
        }

        // ---- npcseasonalparty ----
        internal static bool SpSkip(bool on, bool playerThrewToday)
        {
            return on && playerThrewToday;
        }

        internal static bool SpRegister(bool on, bool holidayToday, bool eaListenerLive)
        {
            return on && holidayToday && !eaListenerLive;
        }

        // The event NpcSeasonalPartyManager.OnHolidayDayStarted listens to in each season.
        internal static EventTypeId SpEvent(Sims3.SimIFace.Enums.Season s)
        {
            switch (s)
            {
                case Sims3.SimIFace.Enums.Season.Fall: return EventTypeId.kCostumePartyScheduled;
                case Sims3.SimIFace.Enums.Season.Spring: return EventTypeId.kThrewParty;
                case Sims3.SimIFace.Enums.Season.Summer: return EventTypeId.kSchedulePoolParty;
                case Sims3.SimIFace.Enums.Season.Winter: return EventTypeId.kFeastPartyScheduled;
            }
            return EventTypeId.kEventNone;
        }

        static ListenerAction SpPlayerThrew(Sims3.Gameplay.EventSystem.Event e)
        {
            sSpThrew = SimClock.CurrentTime();
            sSpListener = null;
            return ListenerAction.Remove;
        }

        // World load finished (Instantiator).
        public static void NpcSeasonalPartyWorldLoad()
        {
            try
            {
                HolidayManager hm = HolidayManager.Instance;
                bool holiday = hm != null && SeasonsManager.Enabled && hm.IsThisDayAHoliday(SimClock.CurrentTime());
                bool eaLive = sSpEaListener != null && sSpEaListener.GetValue(null) != null;
                if (sSpEaListener == null || sSpListener != null || !SpRegister(ShortcutOn("npcseasonalparty"), holiday, eaLive)) return;
                EventTypeId id = SpEvent(SeasonsManager.CurrentSeason);
                if (id == EventTypeId.kEventNone) return;
                sSpListener = EventTracker.AddListener(id, SpPlayerThrew);
                PatchCounters()[2609]++;
            }
            catch (Exception) { PatchCounters()[2611]++; }
        }

        public class SeasonalPartyHook
        {
            public static void ScheduleHook()
            {
                try
                {
                    bool threw = sSpThrew != DateAndTime.Invalid && SimClock.IsSameDay(sSpThrew, SimClock.CurrentTime());
                    if (sSpListener != null) { EventTracker.RemoveListener(sSpListener); sSpListener = null; }
                    if (SpSkip(ShortcutOn("npcseasonalparty"), threw))
                    {
                        NpcSeasonalPartyManager.sStartNpcSesonalPartyAlarm = AlarmHandle.kInvalidHandle;  // the alarm is one-shot and firing now
                        PatchCounters()[2610]++;
                        return;
                    }
                }
                catch (Exception) { PatchCounters()[2611]++; }
                ScheduleOrig();
            }
            public static void ScheduleOrig() { }
        }

        // ---- snowfightjoinwait ----
        // Clear the arena's per-game state after a game ended (what DoReset clears). Returns true when anything was set.
        internal static bool SfClear(object arena, bool on)
        {
            if (!on || arena == null) return false;
            bool any = (int)sSfCounter.GetValue(arena) != 0 || sSfTargeted.GetValue(arena) != null || (int)sSfThrows.GetValue(arena) != 0;
            sSfCounter.SetValue(arena, 0);
            sSfTargeted.SetValue(arena, null);
            sSfThrows.SetValue(arena, 0);
            return any;
        }

        public class SnowFightHook
        {
            public void EndHook()
            {
                EndOrig();
                try { if (SfClear((object)this, ShortcutOn("snowfightjoinwait"))) PatchCounters()[2591]++; }
                catch (Exception) { PatchCounters()[2592]++; }
            }
            public void EndOrig() { }
        }

        // ---- hauntedumbrella ----
        internal static bool HuAnswer(bool on, bool canRouteUsingUmbrella)
        {
            return on ? canRouteUsingUmbrella : !canRouteUsingUmbrella;
        }

        public class HauntedUmbrellaHook
        {
            public bool UmbrellaHook()
            {
                bool ea = UmbrellaOrig();
                try
                {
                    bool r = HuAnswer(ShortcutOn("hauntedumbrella"), (bool)sHuFlag.GetValue((object)this));
                    if (r != ea) PatchCounters()[2593]++;
                    return r;
                }
                catch (Exception) { PatchCounters()[2594]++; return ea; }
            }
            public bool UmbrellaOrig() { return false; }
        }
    }
}
