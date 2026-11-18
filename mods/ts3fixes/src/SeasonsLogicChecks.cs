using System;
using System.Reflection;
using System.Runtime.Serialization;
using Sims3.Gameplay.Objects;
using Sims3.Gameplay.Situations;
using Sims3.Gameplay.Utilities;

namespace Ts3Fixes
{
    // Harness checks for SeasonsLogic.cs (docs/571 §5-7). Read-only for the world: the snowball and haunted-house checks
    // use stand-in objects built without their constructors (never placed or registered).
    public static partial class Fixes
    {
        delegate bool SlBoolFn();

        // TrickOrTreatAlarmCheck run: the weekly NPC trick-or-treat alarm fires on a day that is not the Fall holiday.
        //   Controls: on the Fall holiday it runs (both switch states).
        public static object TrickOrTreatAlarmCheck(string what)
        {
            int[] c = PatchCounters();
            string today = "?", handle = "?";
            try { today = TtFallHolidayToday() ? "Fall holiday" : "not the Fall holiday"; handle = TrickOrTreatSituation.NPCTrickOrTreatAlarm == AlarmHandle.kInvalidHandle ? "invalid" : "set"; } catch (Exception) { }
            string counters = "trickortreatalarm: handles reset at quit 2588=" + c[3969] + ", off-holiday runs skipped 2589=" + c[3970] + ", errors 2590=" + c[3971]
                + "; today " + today + ", handle " + handle + "; " + GlMembers("");
            if (what == "result") return counters;
            if (what != "run") return "trickortreatalarm <run|result>";
            bool on = ShortcutOn("trickortreatalarm");
            bool skipped = TtSkip(on, false);
            bool ctlHoliday = !TtSkip(on, true);
            return "trickortreatalarm check (switch " + (on ? "on" : "off") + "): off-holiday trick-or-treat run skipped=" + skipped
                + "; control: Fall holiday run kept=" + ctlHoliday + "; controls ok=" + ctlHoliday
                + "; verdict: " + GlVerdict(on, skipped, ctlHoliday, true) + "; " + counters;
        }

        // SnowFightJoinWaitCheck run: a stand-in arena that has counted 40 one-player minutes over earlier games ends a game.
        //   Controls: a clean arena stays clean (nothing counted); switch off leaves the counter alone.
        public static object SnowFightJoinWaitCheck(string what)
        {
            int[] c = PatchCounters();
            string missing = SfMissing();
            string counters = "snowfightjoinwait: arenas reset at game end 2591=" + c[2591] + ", errors 2592=" + c[2592] + "; " + GlMembers(missing);
            if (what == "result") return counters;
            if (what != "run") return "snowfightjoinwait <run|result>";
            bool on = ShortcutOn("snowfightjoinwait");
            if (missing.Length > 0) return "snowfightjoinwait check: verdict: UNEXPECTED; " + counters;
            object used = FormatterServices.GetUninitializedObject(typeof(SnowWaterFightArena));
            sSfCounter.SetValue(used, 40);
            sSfThrows.SetValue(used, 3);
            SfClear(used, on);
            int left = (int)sSfCounter.GetValue(used);
            bool cleared = left == 0 && (int)sSfThrows.GetValue(used) == 0;
            object clean = FormatterServices.GetUninitializedObject(typeof(SnowWaterFightArena));
            bool ctlClean = !SfClear(clean, on) && (int)sSfCounter.GetValue(clean) == 0;
            object offArena = FormatterServices.GetUninitializedObject(typeof(SnowWaterFightArena));
            sSfCounter.SetValue(offArena, 40);
            SfClear(offArena, false);
            bool ctlOff = (int)sSfCounter.GetValue(offArena) == 40;
            bool controls = ctlClean && ctlOff;
            return "snowfightjoinwait check (switch " + (on ? "on" : "off") + "): join-wait counter after game end=" + left + " (cleared=" + cleared + ")"
                + "; control: clean arena untouched=" + ctlClean + ", switch off leaves counter=" + ctlOff + "; controls ok=" + controls
                + "; verdict: " + GlVerdict(on, cleared, controls, true) + "; " + counters;
        }

        // HauntedUmbrellaCheck run: EA's HauntedHouse.Enter.CanStartUsingUmbrella is called (through the swapped body) on a
        //   stand-in Enter whose flag is true (a Sim walking to/queuing for the haunted house).
        //   Controls: flag false (panic/vomit/wet-self exit) -> no umbrella in both states.
        public static object HauntedUmbrellaCheck(string what)
        {
            int[] c = PatchCounters();
            string missing = HuMissing();
            MethodInfo m = sHuEnter == null ? null : sHuEnter.GetMethod("CanStartUsingUmbrella", kSlInst);
            if (missing.Length == 0 && m == null) missing = "HauntedHouse.Enter.CanStartUsingUmbrella";
            string counters = "hauntedumbrella: answers changed 2593=" + c[2593] + ", errors 2594=" + c[2594] + "; " + GlMembers(missing);
            if (what == "result") return counters;
            if (what != "run") return "hauntedumbrella <run|result>";
            bool on = ShortcutOn("hauntedumbrella");
            if (missing.Length > 0) return "hauntedumbrella check: verdict: UNEXPECTED; " + counters;
            object walking = FormatterServices.GetUninitializedObject(sHuEnter);
            sHuFlag.SetValue(walking, true);
            bool umbrella = ((SlBoolFn)Delegate.CreateDelegate(typeof(SlBoolFn), walking, m))();
            object panic = FormatterServices.GetUninitializedObject(sHuEnter);
            sHuFlag.SetValue(panic, false);
            bool panicUmbrella = ((SlBoolFn)Delegate.CreateDelegate(typeof(SlBoolFn), panic, m))();
            bool ctlPanic = on ? !panicUmbrella : panicUmbrella;  // off: EA's inverted answer lets the panicking Sim open one
            bool ctlDecision = !HuAnswer(true, false);
            bool controls = ctlPanic && ctlDecision;
            return "hauntedumbrella check (switch " + (on ? "on" : "off") + "): Sim walking to the haunted house may open an umbrella=" + umbrella
                + "; control: panicking Sim gets EA/fixed answer as expected=" + ctlPanic + " (umbrella=" + panicUmbrella + "), fixed rule refuses panic=" + ctlDecision
                + "; controls ok=" + controls + "; verdict: " + GlVerdict(on, umbrella, controls, true) + "; " + counters;
        }

        // NpcSeasonalPartyCheck run: after a holiday reload the player throws the season's party, then the NPC party
        //   alarm fires.
        //   Controls: no player party -> NPC party scheduled; at load, EA's live listener (no reload) -> nothing added;
        //   not a holiday -> nothing added.
        public static object NpcSeasonalPartyCheck(string what)
        {
            int[] c = PatchCounters();
            string missing = sSpEaListener == null ? "NpcSeasonalPartyManager.sPlayerScheduledPartyEvent" : "";
            string counters = "npcseasonalparty: listeners registered at load 2609=" + c[2609] + ", NPC parties skipped 2610=" + c[2610] + ", errors 2611=" + c[2611]
                + "; our listener " + (sSpListener != null ? "live" : "none") + ", player party today=" + (sSpThrew != DateAndTime.Invalid && SimClock.IsSameDay(sSpThrew, SimClock.CurrentTime())) + "; " + GlMembers(missing);
            if (what == "result") return counters;
            if (what != "run") return "npcseasonalparty <run|result>";
            bool on = ShortcutOn("npcseasonalparty");
            bool fixedNow = SpRegister(on, true, false) && SpSkip(on, true);
            bool ctlNoParty = !SpSkip(on, false), ctlEaLive = !SpRegister(on, true, true), ctlNoHoliday = !SpRegister(on, false, false);
            bool controls = ctlNoParty && ctlEaLive && ctlNoHoliday;
            return "npcseasonalparty check (switch " + (on ? "on" : "off") + "): listener back after reload and player party cancels the NPC one=" + fixedNow
                + "; control: no player party -> NPC party kept=" + ctlNoParty + ", EA listener live -> none added=" + ctlEaLive + ", no holiday -> none added=" + ctlNoHoliday
                + "; controls ok=" + controls + "; verdict: " + GlVerdict(on, fixedNow, controls, missing.Length == 0) + "; " + counters;
        }
    }
}
