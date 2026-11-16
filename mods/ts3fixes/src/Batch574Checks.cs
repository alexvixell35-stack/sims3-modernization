using System;
using System.Collections.Generic;
using Sims3.Gameplay.Careers;
using Sims3.Gameplay.Objects.HobbiesSkills;
using Sims3.Gameplay.Actors;

namespace Ts3Fixes
{
    // Harness checks for HouseholdLogic.cs, WaSkillsLogic.cs and CareerLogic.cs (docs/574). Pure decisions with
    // controls; nothing in the world is touched.
    public static partial class Fixes
    {
        static string B4Pick(string all, params string[] names)
        {
            string m = "";
            foreach (string n in names) if (all.IndexOf(n) >= 0) m += n + " ";
            return m.Trim();
        }

        // Counters and verdict carry the switch's live-path problems (CkLive: swaps in place, handles usable).
        static string B4Counters(string name, int a, string an, string missing) { return PhCounters(name, a, an, EpJoin(missing, CkLive(name))); }

        static string B4Line(string sw, string what, bool fixedNow, string ctlText, bool controls, string missing, string counters)
        {
            bool on = ShortcutOn(sw);
            missing = EpJoin(missing, CkLive(sw));
            return sw + " check (switch " + (on ? "on" : "off") + "): " + what + "=" + fixedNow + "; control: " + ctlText + "; controls ok=" + controls
                + "; verdict: " + GlVerdict(on, fixedNow, controls, missing.Length == 0) + "; " + counters;
        }

        // WildHerdMotherCheck run: the herd mother with two foals leaves the pool (EA's own loop shape on a stand-in list).
        //   Control: a mother with no foals works either way.
        public static object WildHerdMotherCheck(string what)
        {
            string missing = B4Pick(HlMissing(), "WildHorsePool.mHerdMotherId");
            string counters = B4Counters("wildherdmother", 2654, "mothers replaced", missing);
            if (what == "result") return counters;
            if (what != "run") return "wildherdmother <run|result>";
            bool on = ShortcutOn("wildherdmother"), threw, threw0;
            int moved = WhRelink(on, new List<int>(new int[] { 1, 2 }), out threw);
            bool ok = !threw && moved == 2;
            WhRelink(on, new List<int>(), out threw0);
            return B4Line("wildherdmother", "both foals re-parented without an exception", ok, "no foals fine=" + !threw0, !threw0, missing, counters);
        }

        // SplitPartnerCheck run: the leaver's Partner was cleared by EA and the stayer still points at the leaver; a teen leaver.
        //   Controls: a stayer pointing elsewhere is untouched; a young adult leaver stays valid.
        public static object SplitPartnerCheck(string what)
        {
            string missing = B4Pick(HlMissing(), "SplitHousehold.mHouseholdId", "SplitHousehold.mSimToLeaveId");
            string counters = B4Counters("splitpartner", 2656, "partners cleared / splits refused", missing);
            if (what == "result") return counters;
            if (what != "run") return "splitpartner <run|result>";
            bool on = ShortcutOn("splitpartner");
            bool ok = SpClearStayer(on, true, true) && !SpValid(on, true, false);
            bool ctlOther = !SpClearStayer(on, false, true), ctlYa = SpValid(on, true, true);
            return B4Line("splitpartner", "stayer's partner cleared and teen leaver refused", ok, "other partner untouched=" + ctlOther + ", young adult valid=" + ctlYa, ctlOther && ctlYa, missing, counters);
        }

        // MovingFlagCheck run: the flag is set at load and no moving situation exists.
        //   Control: a live moving situation keeps it.
        public static object MovingFlagCheck(string what)
        {
            string missing = B4Pick(HlMissing(), "MovingSituation.sbMovingInProgress");
            string counters = B4Counters("movingflag", 2658, "flags cleared", missing);
            if (what == "result") return counters;
            if (what != "run") return "movingflag <run|result>";
            bool on = ShortcutOn("movingflag");
            bool ok = MvClear(on, true, false), ctl = !MvClear(on, true, true);
            return B4Line("movingflag", "stale move flag cleared", ok, "live move kept=" + ctl, ctl, missing, counters);
        }

        // SandsCoinsCheck run: a chest with Sands of Understanding is opened.
        //   Control: a chest without sands gives no bonus pile either way.
        public static object SandsCoinsCheck(string what)
        {
            string missing = B4Pick(WsMissing(), "TreasureChest.OpenWithAnimation", "TreasureChest.mSandsOfUnderstandingCanAddCoins", "TreasureChest.kSandsOfUnderstandingCoinPileSizeWeightedChances", "OpenWithAnimation.LocalizeString");
            string counters = B4Counters("sandscoins", 2660, "piles given", missing);
            if (what == "result") return counters;
            if (what != "run") return "sandscoins <run|result>";
            bool on = ShortcutOn("sandscoins");
            bool ok = ScPileGoes(on, true, true) == 1, ctl = ScPileGoes(on, false, true) == 0;
            return B4Line("sandscoins", "bonus coins reach the opener", ok, "no sands no pile=" + ctl, ctl, missing, counters);
        }

        // TombMusicCheck run: the world is quit with a tomb music type still set. Control: nothing set stays untouched.
        public static object TombMusicCheck(string what)
        {
            string missing = B4Pick(WsMissing(), "TombRoomManager.sMusicType", "TombRoomManager.sTombMusicHandle", "TombRoomManager.sTombPlaylistHandle");
            string counters = B4Counters("tombmusic", 2674, "resets", missing);
            if (what == "result") return counters;
            if (what != "run") return "tombmusic <run|result>";
            bool on = ShortcutOn("tombmusic");
            bool ok = TmReset(on, true), ctl = !TmReset(on, false);
            return B4Line("tombmusic", "stale tomb music state reset at quit", ok, "unset untouched=" + ctl, ctl, missing, counters);
        }

        // ArtSellNoArtistCheck run: a painting whose painter no longer exists is sold.
        //   Control: a painting with its artist completes either way.
        public static object ArtSellNoArtistCheck(string what)
        {
            string missing = typeof(ImageObject).GetMethod("SellImageObject", new Type[] { typeof(Sim), typeof(int), typeof(bool) }) == null ? "ImageObject.SellImageObject(Sim,int,bool)" : "";
            string counters = B4Counters("artsellnoartist", 2662, "sales completed without an artist", missing);
            if (what == "result") return counters;
            if (what != "run") return "artsellnoartist <run|result>";
            bool on = ShortcutOn("artsellnoartist");
            bool ok = AsCompletes(on, true), ctl = AsCompletes(on, false);
            return B4Line("artsellnoartist", "sale completes (item removed) without an artist", ok, "sale with artist completes=" + ctl, ctl, missing, counters);
        }

        // RoyaltyOrphanCheck run: a writer returns from travel with royalties (imported alarm, new home alarm).
        //   Controls: no imported alarm; the same alarm object is never removed.
        public static object RoyaltyOrphanCheck(string what)
        {
            string missing = B4Pick(WsMissing(), "Writing.mRoyaltyAlarm");
            string counters = B4Counters("royaltyorphan", 2664, "alarms removed", missing);
            if (what == "result") return counters;
            if (what != "run") return "royaltyorphan <run|result>";
            bool on = ShortcutOn("royaltyorphan");
            bool ok = RoRemoveImported(on, true, false);
            bool ctlNone = !RoRemoveImported(on, false, false), ctlSame = !RoRemoveImported(on, true, true);
            return B4Line("royaltyorphan", "imported royalty alarm removed", ok, "no alarm=" + ctlNone + ", same alarm kept=" + ctlSame, ctlNone && ctlSame, missing, counters);
        }

        // The startup patch log line for one hooked method ("Type.Method: ok" or the refusal), or "not in log".
        static string PsPatchLog(string method)
        {
            List<object> log = AppliedLog();
            if (log == null) return method + ": no log";
            foreach (object o in log)
            {
                string line = o as string;
                if (line != null && line.IndexOf("." + method + ": ", StringComparison.Ordinal) >= 0) return line;
            }
            return method + ": not in log";
        }

        // PromoteShiftCheck run: promoted at 03:00 into a day off, shift 21:00 (18 h ahead, minimum 8 h).
        //   Controls: the same on a work day is allowed; hired today is refused (both EA).
        public static object PromoteShiftCheck(string what)
        {
            // the patcher's own lookup (Patch: all flags, DeclaredOnly), each member named, and the patch log line of each
            // hook so a swap refused at startup ("already compiled", signature) shows instead of a silently dead hook
            const System.Reflection.BindingFlags psAll = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static
                | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly;
            string missing = ((typeof(Occupation).GetMethod("IsTimeDuringWorkDay", psAll) == null ? "Occupation.IsTimeDuringWorkDay " : "")
                + (typeof(Career).GetMethod("IsRegularWorkTime", psAll) == null ? "Career.IsRegularWorkTime" : "")).Trim();
            string counters = B4Counters("promoteshift", 2666, "answers corrected", missing) + "; patch log: " + PsPatchLog("IsTimeDuringWorkDay") + " | " + PsPatchLog("IsRegularWorkTime");
            if (what == "result") return counters;
            if (what != "run") return "promoteshift <run|result>";
            bool on = ShortcutOn("promoteshift");
            bool ok = !PsSameDay(on, false, 18, 8, false);
            bool ctlWork = PsSameDay(on, false, 18, 8, true), ctlHired = !PsSameDay(on, true, 18, 8, true);
            return B4Line("promoteshift", "no shift on a day off after a promotion", ok, "work day allowed=" + ctlWork + ", hired today refused=" + ctlHired, ctlWork && ctlHired, missing, counters);
        }

        // GoToWorkStaleCheck run: the persisted go-to-work alarm fires after the Sim quit the job.
        //   Control: the current job with location and level pushes as EA.
        public static object GoToWorkStaleCheck(string what)
        {
            bool members = typeof(Career).GetMethod("RegularWorkDayGoToWorkHandle", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public) != null;
            string missing = members ? "" : "Career.RegularWorkDayGoToWorkHandle";
            string counters = B4Counters("gotoworkstale", 2672, "stale pushes skipped", missing);
            if (what == "result") return counters;
            if (what != "run") return "gotoworkstale <run|result>";
            bool on = ShortcutOn("gotoworkstale");
            bool ok = GwSkip(on, false, false, true), ctl = !GwSkip(on, true, true, true);
            return B4Line("gotoworkstale", "stale push for a left job skipped", ok, "current job pushes=" + ctl, ctl, missing, counters);
        }
    }
}
