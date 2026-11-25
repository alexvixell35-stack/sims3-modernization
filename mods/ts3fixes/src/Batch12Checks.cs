using System;
using System.Collections.Generic;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.ObjectComponents;
using Sims3.SimIFace;
using Sims3.Gameplay.Utilities;

namespace Ts3Fixes
{
    // Batch 12 checks (docs/590-592): each runs the hook's own pure decision with controls, the hook's members found at
    // Apply (same handles) and a live-path assert (the hook class is in sPatched). Read-only.
    public static partial class Fixes
    {
        public static object ProtestExitCheck(string what)
        {
            bool f = false, c = false;
            try {  f = PeExit(true, true); c = !PeExit(true, false) && !PeExit(false, true); } catch (Exception) { }
            return B12Check(what, "protestexit", 1730, new string[] { "exits", "root-only cancels (switch off)", "errors" }, "", "protest with a child exits", f, "no child / not a protest untouched", c);
        }

        public static object JogTerrainCheck(string what)
        {
            bool f = false, c = false;
            try {  f = JtStop(true, true); c = !JtStop(false, true) && JtStop(false, false); } catch (Exception) { }
            return B12Check(what, "jogterrain", 1733, new string[] { "walk-backs queued", "fatigue checks with the switch off", "errors" }, sJtM12, "fatigued jogger stops", f, "rested runner goes on, no fast run stops", c);
        }

        public static object LifeguardQuitCheck(string what)
        {
            bool f = false, c = false;
            try {  f = LqQuit(true, false); c = !LqQuit(true, true); } catch (Exception) { }
            return B12Check(what, "lifeguardquit", 1736, new string[] { "loops ended", "quit loops run on (switch off)", "errors" }, "", "quit lifeguard stops", f, "employed lifeguard goes on", c);
        }

        public static object FrolicExitCheck(string what)
        {
            bool f = false, c = false;
            try {  f = FeExit(true, new bool[] { true, false }); c = !FeExit(false, new bool[] { true }) && !FeExit(true, new bool[] { false, false }); } catch (Exception) { }
            return B12Check(what, "frolicexit", 1739, new string[] { "frolics ended", "missed with the switch off", "errors" }, sFeM12, "first critical motive ends", f, "before sustain / none critical go on", c);
        }

        public static object ShooDefCheck(string what)
        {
            bool f = false, c = false;
            try { Sim.TurnToFaceAndAskToLeave.Definition a = SdDefinition(), b = SdDefinition();  f = a != null && !ReferenceEquals(a, b); c = a != null; } catch (Exception) { }
            return B12Check(what, "shoodef", 1742, new string[] { "shoos with an own definition", "shared writes (switch off)", "errors" }, "", "own definition per shoo", f, "definition builds", c);
        }

        public static object IglooLookatsCheck(string what)
        {
            bool f = false, c = false;
            try {  f = IlRestore(true, false, false); c = !IlRestore(false, false, false) && !IlRestore(true, true, false) && !IlRestore(true, false, true); } catch (Exception) { }
            return B12Check(what, "igloolookats", 1745, new string[] { "look-ats restored", "left off (switch off)", "errors" }, sIlM12, "look-ats turned back on", f, "never on / still on / still building untouched", c);
        }

        public static object GoHereBoatListCheck(string what)
        {
            bool f = false, c = false;
            try {  f = GbRemove(true, false) && GbRemove(false, true); c = !GbRemove(false, false) && GbCleanCount(false, 2, 1) == 0; f = f && GbCleanCount(true, 2, 1) == 3; } catch (Exception) { }
            return B12Check(what, "gohereboatlist", 1748, new string[] { "Sims dropped", "left in a list (switch off)", "errors" }, sGbM12, "boat and train entries dropped", f, "not listed untouched", c);
        }

        public static object BouncerRoleCheck(string what)
        {
            bool f = false, c = false;
            try {  f = BrNotice(true, true, false, true, 4f, 3f) && !BrNotice(false, true, false, true, 4f, 3f); c = !BrNotice(true, false, true, true, 0f, 3f) && BrNotice(true, true, true, false, 100f, 3f) && !BrNotice(true, true, false, true, 16f, 3f); } catch (Exception) { }
            return B12Check(what, "bouncerrole", 1751, new string[] { "notice tests answered our way", "tests with the switch off", "null-role runs ended" }, sBrM12, "2 m sneaker inside a 3 m radius noticed", f, "no role / watching / far", c);
        }

        public static object TrickSocialsCheck(string what)
        {
            bool f = false, c = false;
            try {  f = MtRestore(true, false); c = !MtRestore(false, false) && !MtRestore(true, true); } catch (Exception) { }
            return B12Check(what, "tricksocials", 1754, new string[] { "restored", "left off (switch off)", "errors" }, "", "socials turned back on", f, "off before / still on untouched", c);
        }

        public static object FearFaintUndoCheck(string what)
        {
            bool f = false, c = false;
            try {  f = FfUndo(false, true, false) && FfUndo(false, false, true); c = !FfUndo(true, true, false) && !FfUndo(false, false, false); } catch (Exception) { }
            return B12Check(what, "fearfaintundo", 1757, new string[] { "undone", "left (switch off)", "errors" }, sFfM12, "shut-down robot and left jig undone", f, "already shut / clean untouched", c);
        }

        public static object LotPruneCheck(string what)
        {
            bool f = false, c = false;
            try {  f = LpPrune(true, true, 5) && LpPrune(false, false, 5) && LpPrune(false, true, 0); c = !LpPrune(false, true, 3); } catch (Exception) { }
            return B12Check(what, "lotprune", 1760, new string[] { "lots pruned", "lots kept (switch off)", "errors" }, sLpM12, "missing, residential, unvisited lots pruned", f, "visited community lot kept", c);
        }

        public static object WerewolfFindCheck(string what)
        {
            bool f = false, c = false;
            try {  f = WhValue(5, 3, true) == 8; c = WhValue(5, 3, false) == 3; } catch (Exception) { }
            return B12Check(what, "werewolffind", 1763, new string[] { "null finds and insect sums fixed", "seen with the switch off", "errors" }, sWhM12, "insect value added", f, "other finds keep EA's value", c);
        }

        public static object CopyHomeworkFxCheck(string what)
        {
            bool f = false, c = false;
            try {  f = ChStale(true); c = !ChStale(false); } catch (Exception) { }
            return B12Check(what, "copyhomeworkfx", 1766, new string[] { "cleared", "left (switch off)", "errors" }, sChM12, "copier's broadcaster cleared", f, "other actor untouched", c);
        }

        public static object SpellTargetEndCheck(string what)
        {
            bool f = false, c = false;
            try {  f = StEnd(true, true) == 1 && StEnd(true, false) == 2; c = StEnd(false, true) == 0; } catch (Exception) { }
            return B12Check(what, "spelltargetend", 1769, new string[] { "targets released", "left waiting (switch off)", "errors" }, sStM12, "running target finished, queued removed", f, "unlinked target untouched", c);
        }

        public static object FoodTrayGrillCheck(string what)
        {
            bool f = false, c = false;
            try {  f = FtgPutDown(false, true); c = !FtgPutDown(true, true) && !FtgPutDown(false, false); } catch (Exception) { }
            return B12Check(what, "foodtraygrill", 1772, new string[] { "fixed returns", "seen with the switch off", "errors" }, "", "held tray put down on failure", f, "success / empty hands untouched", c);
        }

        public static object CompositionNullCheck(string what)
        {
            bool f = false, c = false;
            try { List<string> l = new List<string>(new string[] { "a", null, "b", null }); List<string> k = new List<string>(new string[] { null, "x" });  f = CnDrop(l, 0) == 2 && l.Count == 2; c = CnDrop(k, 1) == 0 && k.Count == 2; } catch (Exception) { }
            return B12Check(what, "compositionnull", 1775, new string[] { "imports cleaned", "nulls kept (switch off)", "errors" }, "", "nulls dropped", f, "entries before the import kept", c);
        }

        public static object PetDreamTimerCheck(string what)
        {
            bool f = false, c = false;
            try {  f = PdReset(0f) && PdReset(-1f); c = !PdReset(1f); } catch (Exception) { }
            return B12Check(what, "petdreamtimer", 1778, new string[] { "timers reset", "left due (switch off)", "errors" }, sPdM12, "due timer reset", f, "running timer untouched", c);
        }

        public static object AwkwardScienceCheck(string what)
        {
            bool f = false, c = false;
            try {  f = AsGain(1.5f, 0.5f, true, false) == 1f; c = AsGain(1.5f, 0.5f, true, true) == 1.5f && AsGain(1.5f, 0.5f, false, false) == 1.5f; } catch (Exception) { }
            return B12Check(what, "awkwardscience", 1781, new string[] { "gains corrected", "bonus on another skill (switch off)", "errors" }, sAsM12, "bonus off a non-science skill", f, "science / not awkward keep EA's", c);
        }

        public static object ChessDefeatedCheck(string what)
        {
            if (what == "live") return ChessDefeatedLive();
            bool f = false, c = false;
            try {  f = CdBetter(3, 3) && CdBetter(3, 5); c = !CdBetter(5, 3); } catch (Exception) { }
            return B12Check(what, "chessdefeated", 1784, new string[] { "wins recorded our way", "EA's record (switch off)", "errors" }, sCdM12, "equal or stronger opponent stored", f, "weaker opponent not stored", c);
        }

        // live: EA's public UpdateChessTournamentStatistics on the selected Sim's LogicSkill with another Sim as the beaten
        // opponent, so the hook runs end to end, its FindSuitableChessRankOpponent call included (v0.3.2 reached it through
        // MethodInfo.Invoke: TargetException, counted [3120]). Writes that Sim's tournament record and gives both Sims the
        // Logic skill if they lack it: test saves only.
        static object ChessDefeatedLive()
        {
            Sim s = Sims3.Gameplay.Core.PlumbBob.SelectedActor;
            if (s == null || s.SkillManager == null) return "chessdefeated live: no selected Sim";
            Sim opp = null;
            foreach (Sim o in Sims3.Gameplay.Queries.GetObjects<Sim>())
                if (o != s && o.SkillManager != null && o.SimDescription != null && !o.HasBeenDestroyed) { opp = o; break; }
            if (opp == null) return "chessdefeated live: no other Sim in the world";
            Sims3.Gameplay.Skills.LogicSkill mine = s.SkillManager.GetSkill<Sims3.Gameplay.Skills.LogicSkill>(Sims3.Gameplay.Skills.SkillNames.Logic) ?? s.SkillManager.AddElement(Sims3.Gameplay.Skills.SkillNames.Logic) as Sims3.Gameplay.Skills.LogicSkill;
            if (opp.SkillManager.GetSkill<Sims3.Gameplay.Skills.LogicSkill>(Sims3.Gameplay.Skills.SkillNames.Logic) == null) opp.SkillManager.AddElement(Sims3.Gameplay.Skills.SkillNames.Logic);
            if (mine == null) return "chessdefeated live: no LogicSkill on " + s.FullName;
            System.Reflection.FieldInfo next = typeof(Sims3.Gameplay.Skills.LogicSkill).GetField("mChessRankingNextChallenger", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            int[] pc = PatchCounters();
            int ours = pc[3118], ea = pc[3119], err = pc[3120];
            mine.UpdateChessTournamentStatistics(opp.SimDescription);
            Sims3.Gameplay.CAS.SimDescription ch = next == null ? null : next.GetValue(mine) as Sims3.Gameplay.CAS.SimDescription;
            return "chessdefeated live: switch " + (ShortcutOn("chessdefeated") ? "on" : "off") + ", beat " + opp.FullName + ": ours +" + (pc[3118] - ours)
                + ", EA's +" + (pc[3119] - ea) + ", errors +" + (pc[3120] - err) + ", next challenger " + (ch == null ? "none" : ch.FullName);
        }

        public static object PrivacyOthersCheck(string what)
        {
            bool f = false, c = false;
            try {  f = PoGone(false); c = !PoGone(true); } catch (Exception) { }
            return B12Check(what, "privacyothers", 1787, new string[] { "Sims dropped", "kept (switch off)", "errors" }, sPoM12, "Sim off the lot dropped", f, "Sim on the lot kept", c);
        }

        public static object GigMasterCheck(string what)
        {
            bool f = false, c = false;
            try {  f = GmDone(true, 15, 15) && !GmDone(false, 15, 15); c = GmDone(false, 16, 15) && !GmDone(true, 14, 15); } catch (Exception) { }
            return B12Check(what, "gigmaster", 1790, new string[] { "answers our way", "off by one (switch off)", "errors" }, sGmM12, "15 of 15 gigs completes", f, "16 (EA) / 14 (ours) unchanged", c);
        }

        public static object IntMultipleCheck(string what)
        {
            bool f = false, c = false;
            try { int lo, hi, lo2, hi2, lo3, hi3; ImRange(10, 80, 5, out lo, out hi); ImRange(12, 80, 5, out lo2, out hi2); ImRange(0, 1500, 100, out lo3, out hi3);  f = lo == 2 && hi == 16 && lo2 == 3 && hi2 == 16; c = lo3 == 0 && hi3 == 15; } catch (Exception) { }
            return B12Check(what, "intmultiple", 1793, new string[] { "draws our way", "EA's draws (switch off)", "errors" }, "", "10..80 step 5 reaches 16, 12 rounds up", f, "0..1500 step 100 = 0..15", c);
        }

        public static object DoorPrivacyNullCheck(string what)
        {
            bool f = false, c = false;
            try { bool n1, n2; Dictionary<int, bool> a = DpRooms(new List<int?>(new int?[] { 1, null, 2 }), out n1); Dictionary<int, bool> b = DpRooms(new List<int?>(new int?[] { 3, 3 }), out n2);  f = n1 && a.Count == 2 && a.ContainsKey(2); c = !n2 && b.Count == 1; } catch (Exception) { }
            return B12Check(what, "doorprivacynull", 1796, new string[] { "updates with a null target skipped", "updates that threw (switch off)", "errors" }, sDpM12, "rooms after a null kept", f, "no null: one room, no flag", c);
        }

        public static object LeafBurnFailCheck(string what)
        {
            bool f = false, c = false;
            try {  f = LbClear(false, true, false, false); c = !LbClear(true, true, false, false) && !LbClear(false, true, true, false) && !LbClear(false, true, false, true); } catch (Exception) { }
            return B12Check(what, "leafburnfail", 1799, new string[] { "piles freed", "left blocked (switch off)", "errors" }, sLbM12, "unlit pile freed", f, "flag before / destroyed / burning untouched", c);
        }

        public static object CatchPreyOddsCheck(string what)
        {
            bool f = false, c = false;
            try {  f = !CpSure(true, false, false, true, false) && CpSure(false, false, false, true, false); c = CpSure(true, true, true, false, false) && CpSure(true, true, false, true, false) && !CpSure(true, false, true, false, false); } catch (Exception) { }
            return B12Check(what, "catchpreyodds", 1802, new string[] { "rolls taken", "sure catches (switch off)", "errors" }, sCpM12, "snake without the opportunity rolls", f, "opportunity reptiles sure, plain lizard rolls", c);
        }

        public static object PicnicAntsCheck(string what)
        {
            bool f = false, c = false;
            try {  f = PaAlarmLive(AlarmHandle.kInvalidHandle) == false; c = sPaFx != null && sPaAlarm != null; } catch (Exception) { }
            return B12Check(what, "picnicants", 1805, new string[] { "cleanups done", "left (switch off)", "errors" }, sPaM12, "set alarm removed", f, "handles found", c);
        }

        public static object LoyalDogLayCheck(string what)
        {
            bool f = false, c = false;
            try { Sim s = Sims3.Gameplay.Core.PlumbBob.SelectedActor;  f = s == null || ReferenceEquals(LdTarget(true, s, null), s); c = s == null || LdTarget(false, s, null) == null; } catch (Exception) { }
            return B12Check(what, "loyaldoglay", 1808, new string[] { "lay-downs queued", "refused targets (switch off)", "errors" }, sLdM12, "the dog is the target", f, "EA targets the human", c);
        }

        public static object VisitListenersCheck(string what)
        {
            bool f = false, c = false;
            try {  f = VlDrop(true); c = !VlDrop(false); } catch (Exception) { }
            return B12Check(what, "visitlisteners", 1811, new string[] { "entries dropped", "kept (switch off)", "errors" }, sVlM12, "entry dropped", f, "no entry untouched", c);
        }

        public static object ScienceFxStopCheck(string what)
        {
            bool f = false, c = false;
            try {  f = SfStopFirst(true); c = !SfStopFirst(false); } catch (Exception) { }
            return B12Check(what, "sciencefxstop", 1814, new string[] { "old sets stopped", "leaked (switch off)", "errors" }, sSfM12, "running set stopped first", f, "no set untouched", c);
        }

        public static object DaycareLikingCheck(string what)
        {
            bool f = false, c = false;
            try {  f = DlXp(10f, 5f, 1.5f, 1f) == 12.5f; c = DlXp(10f, 5f, 1f, 1f) == 10f; } catch (Exception) { }
            return B12Check(what, "daycareliking", 1817, new string[] { "payouts corrected", "payouts at liking 0 (switch off)", "errors" }, "", "real liking moves the payout", f, "same multiplier keeps EA's", c);
        }

        public static object KegBreakageCheck(string what)
        {
            bool f = false, c = false;
            try {  f = KbParty(1f, true, false, true, true, false, 2f, 3f) == 1f && KbParty(1f, false, false, true, true, false, 2f, 3f) == 3f; c = KbParty(1f, true, true, true, true, true, 2f, 3f) == 6f; } catch (Exception) { }
            return B12Check(what, "kegbreakage", 1820, new string[] { "chances computed our way", "kegger rate on a non-kegger party (switch off)", "errors" }, sKbM12, "plain party without the kegger rate", f, "kegger party with EP4 both rates", c);
        }

        public static object RomanceCooldownCheck(string what)
        {
            bool f = false, c = false;
            try {  f = RcBlocked(true, 1f, 6f) && !RcBlocked(false, 1f, 6f); c = !RcBlocked(true, 7f, 6f) && RcBlocked(false, 0.05f, 6f); } catch (Exception) { }
            return B12Check(what, "romancecooldown", 1823, new string[] { "blocked our way", "let through (switch off)", "errors" }, sRcM12, "1 h after a bad date blocked", f, "7 h free, EA's minute rule", c);
        }

        public static object LightCustomCheck(string what)
        {
            bool f = false, c = false;
            try {  f = LcValue(true, 0.3f, 0.8f) == 0.3f; c = LcValue(false, 0.3f, 0.8f) == 0.8f; } catch (Exception) { }
            return B12Check(what, "lightcustom", 1826, new string[] { "lights set our way", "EA's copies (switch off)", "errors" }, sLcM12, "chosen level copied", f, "EA copies the current level", c);
        }

        public static object EatPreyCancelCheck(string what)
        {
            bool f = false, c = false;
            try {  f = EpKeepPrey(false) && EpReenable(false, false); c = !EpKeepPrey(true) && !EpReenable(true, false) && !EpReenable(false, true); } catch (Exception) { }
            return B12Check(what, "eatpreycancel", 1829, new string[] { "prey kept or re-enabled", "seen with the switch off", "errors" }, sEpM12, "cancelled eat keeps, failed run re-enables", f, "eaten / success / destroyed untouched", c);
        }

        public static object MeltAlarmCheck(string what)
        {
            bool f = false, c = false;
            try {  f = MaMelts(false, false); c = !MaMelts(true, true) && MaMelts(true, false); } catch (Exception) { }
            return B12Check(what, "meltalarm", 1832, new string[] { "starts our way", "starts that threw or doubled (switch off)", "errors" }, sMaM12, "no artist melts", f, "Ice Personality keeps, plain artist melts", c);
        }

        public static object NewspaperDefCheck(string what)
        {
            bool f = false, c = false;
            try { InteractionDefinition d = NdDefinition();  f = d != null && !ReferenceEquals(d, Sims3.Gameplay.Objects.Miscellaneous.GetNewspaperChooser.Singleton); c = Sims3.Gameplay.Objects.Miscellaneous.GetNewspaperChooser.Singleton != null; } catch (Exception) { }
            return B12Check(what, "newspaperdef", 1835, new string[] { "reads with an own definition", "shared writes (switch off)", "errors" }, "", "own definition", f, "root singleton present", c);
        }

        public static object LeaveConvNullCheck(string what)
        {
            bool f = false, c = false;
            try {  f = LvSkip(false); c = !LvSkip(true); } catch (Exception) { }
            return B12Check(what, "leaveconvnull", 1838, new string[] { "empty leaves skipped", "counted (switch off)", "errors" }, "", "no conversation skipped", f, "conversation leaves as EA", c);
        }

        public static object VacationAlarmsCheck(string what)
        {
            bool f = false, c = false;
            try {  f = VaFixTicks(true, 100, 40) == 100; c = VaFixTicks(false, 100, 40) == 140; } catch (Exception) { }
            return B12Check(what, "vacationalarms", 1841, new string[] { "arrivals fixed", "arrivals with the double shift (switch off)", "errors" }, sVaM12, "arrival fixed at the current tick", f, "EA adds the advance again", c);
        }

        public static object CleanupVfxCheck(string what)
        {
            bool f = false, c = false;
            try {  f = CvDispose(true, false); c = !CvDispose(true, true) && CvDispose(false, true) && !CvDispose(false, false); } catch (Exception) { }
            return B12Check(what, "cleanupvfx", 1844, new string[] { "effect lists disposed", "left to the finalizer (switch off)", "errors" }, sCvM12, "live game disposes", f, "shutdown / EA rule", c);
        }

        public static object LookAtCopyCheck(string what)
        {
            bool f = false, c = false;
            try { List<GameObject> live = new List<GameObject>(); live.Add(null); List<GameObject> lf = LaFilter(live);  f = lf != null && !ReferenceEquals(lf, live) && live.Count == 1; c = LaFilter(null) == null && LaFilter(new List<GameObject>()) == null; } catch (Exception) { }
            return B12Check(what, "lookatcopy", 1847, new string[] { "copies filtered", "live lists cut (switch off)", "errors" }, "", "filtered copy, live list kept", f, "null / empty answer null", c);
        }

        public static object LottoTooltipCheck(string what)
        {
            bool f = false, c = false;
            try {  f = LtClosed(19f, 20f, 18f) && LtTicks(20.5f) == SimClock.ConvertToTicks(20, TimeUnit.Hours) + SimClock.ConvertToTicks(30, TimeUnit.Minutes); c = !LtClosed(21f, 20f, 18f) && !LtClosed(10f, 20f, 18f); } catch (Exception) { }
            return B12Check(what, "lottotooltip", 1850, new string[] { "tooltips fixed", "shifted tooltips (switch off)", "errors" }, sLtM12, "19:00 closed, 20:30 as a time of day", f, "21:00 / 10:00 open", c);
        }

        public static object TraitIconDislikeCheck(string what)
        {
            bool f = false, c = false;
            try {  f = TiIcon(true, true, -1f) == 2 && TiIcon(false, true, -1f) == 0; c = TiIcon(true, true, 1f) == 1 && TiIcon(true, false, -1f) == 0; } catch (Exception) { }
            return B12Check(what, "traiticondislike", 1853, new string[] { "dislike icons shown", "hidden (switch off)", "errors" }, "", "lowered trait motive shows dislike", f, "raised motive trait icon, no motive skipped", c);
        }

        public static object RoleUnprotectCheck(string what)
        {
            bool f = false, c = false;
            try {  f = RuUnprotect(true, false); c = !RuUnprotect(true, true) && !RuUnprotect(false, false); } catch (Exception) { }
            return B12Check(what, "roleunprotect", 1856, new string[] { "protections removed", "left (switch off)", "errors" }, sRuM12, "held protection removed", f, "shutdown / not protected untouched", c);
        }

        public static object PutDownPlacedCheck(string what)
        {
            bool f = false, c = false;
            try {  f = PpAction(true, true, SurfaceAddOn.Action.ItemRemoved) == SurfaceAddOn.Action.ItemPlaced; c = PpAction(false, true, SurfaceAddOn.Action.ItemRemoved) == SurfaceAddOn.Action.ItemRemoved && PpAction(true, false, SurfaceAddOn.Action.ItemRemoved) == SurfaceAddOn.Action.ItemRemoved; } catch (Exception) { }
            return B12Check(what, "putdownplaced", 1859, new string[] { "placements fired as placed", "fired as removed (switch off)", "errors" }, "", "own removal fired as placed", f, "EA rule / other events unchanged", c);
        }
    }
}
