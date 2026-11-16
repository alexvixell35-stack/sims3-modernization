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
    // Batch 12b checks (docs/591): each runs the hook's own pure decision with controls, the hook's members found at
    // Apply (same handles) and a live-path assert (the hook class is in sPatched). Read-only.
    public static partial class Fixes
    {
        public static object AwkwardScienceCheck(string what)
        {
            bool f = false, c = false;
            try {  f = AsGain(1.5f, 0.5f, true, false) == 1f; c = AsGain(1.5f, 0.5f, true, true) == 1.5f && AsGain(1.5f, 0.5f, false, false) == 1.5f; } catch (Exception) { }
            return B12Check(what, "awkwardscience", 3115, new string[] { "gains corrected", "bonus on another skill (switch off)", "errors" }, sAsM12, "bonus off a non-science skill", f, "science / not awkward keep EA's", c);
        }

        public static object ChessDefeatedCheck(string what)
        {
            bool f = false, c = false;
            try {  f = CdBetter(3, 3) && CdBetter(3, 5); c = !CdBetter(5, 3); } catch (Exception) { }
            return B12Check(what, "chessdefeated", 3118, new string[] { "wins recorded our way", "EA's record (switch off)", "errors" }, sCdM12, "equal or stronger opponent stored", f, "weaker opponent not stored", c);
        }

        public static object PrivacyOthersCheck(string what)
        {
            bool f = false, c = false;
            try {  f = PoGone(false); c = !PoGone(true); } catch (Exception) { }
            return B12Check(what, "privacyothers", 3121, new string[] { "Sims dropped", "kept (switch off)", "errors" }, sPoM12, "Sim off the lot dropped", f, "Sim on the lot kept", c);
        }

        public static object GigMasterCheck(string what)
        {
            bool f = false, c = false;
            try {  f = GmDone(true, 15, 15) && !GmDone(false, 15, 15); c = GmDone(false, 16, 15) && !GmDone(true, 14, 15); } catch (Exception) { }
            return B12Check(what, "gigmaster", 3124, new string[] { "answers our way", "off by one (switch off)", "errors" }, sGmM12, "15 of 15 gigs completes", f, "16 (EA) / 14 (ours) unchanged", c);
        }

        public static object IntMultipleCheck(string what)
        {
            bool f = false, c = false;
            try { int lo, hi, lo2, hi2, lo3, hi3; ImRange(10, 80, 5, out lo, out hi); ImRange(12, 80, 5, out lo2, out hi2); ImRange(0, 1500, 100, out lo3, out hi3);  f = lo == 2 && hi == 16 && lo2 == 3 && hi2 == 16; c = lo3 == 0 && hi3 == 15; } catch (Exception) { }
            return B12Check(what, "intmultiple", 3127, new string[] { "draws our way", "EA's draws (switch off)", "errors" }, "", "10..80 step 5 reaches 16, 12 rounds up", f, "0..1500 step 100 = 0..15", c);
        }

        public static object DoorPrivacyNullCheck(string what)
        {
            bool f = false, c = false;
            try { bool n1, n2; Dictionary<int, bool> a = DpRooms(new List<int?>(new int?[] { 1, null, 2 }), out n1); Dictionary<int, bool> b = DpRooms(new List<int?>(new int?[] { 3, 3 }), out n2);  f = n1 && a.Count == 2 && a.ContainsKey(2); c = !n2 && b.Count == 1; } catch (Exception) { }
            return B12Check(what, "doorprivacynull", 3130, new string[] { "updates with a null target skipped", "updates that threw (switch off)", "errors" }, sDpM12, "rooms after a null kept", f, "no null: one room, no flag", c);
        }

        public static object LeafBurnFailCheck(string what)
        {
            bool f = false, c = false;
            try {  f = LbClear(false, true, false, false); c = !LbClear(true, true, false, false) && !LbClear(false, true, true, false) && !LbClear(false, true, false, true); } catch (Exception) { }
            return B12Check(what, "leafburnfail", 3133, new string[] { "piles freed", "left blocked (switch off)", "errors" }, sLbM12, "unlit pile freed", f, "flag before / destroyed / burning untouched", c);
        }

        public static object CatchPreyOddsCheck(string what)
        {
            bool f = false, c = false;
            try {  f = !CpSure(true, false, false, true, false) && CpSure(false, false, false, true, false); c = CpSure(true, true, true, false, false) && CpSure(true, true, false, true, false) && !CpSure(true, false, true, false, false); } catch (Exception) { }
            return B12Check(what, "catchpreyodds", 3136, new string[] { "rolls taken", "sure catches (switch off)", "errors" }, sCpM12, "snake without the opportunity rolls", f, "opportunity reptiles sure, plain lizard rolls", c);
        }

        public static object PicnicAntsCheck(string what)
        {
            bool f = false, c = false;
            try {  f = PaAlarmLive(AlarmHandle.kInvalidHandle) == false; c = sPaFx != null && sPaAlarm != null; } catch (Exception) { }
            return B12Check(what, "picnicants", 3139, new string[] { "cleanups done", "left (switch off)", "errors" }, sPaM12, "set alarm removed", f, "handles found", c);
        }

        public static object LoyalDogLayCheck(string what)
        {
            bool f = false, c = false;
            try { Sim s = Sims3.Gameplay.Core.PlumbBob.SelectedActor;  f = s == null || ReferenceEquals(LdTarget(true, s, null), s); c = s == null || LdTarget(false, s, null) == null; } catch (Exception) { }
            return B12Check(what, "loyaldoglay", 3142, new string[] { "lay-downs queued", "refused targets (switch off)", "errors" }, sLdM12, "the dog is the target", f, "EA targets the human", c);
        }

        public static object VisitListenersCheck(string what)
        {
            bool f = false, c = false;
            try {  f = VlDrop(true); c = !VlDrop(false); } catch (Exception) { }
            return B12Check(what, "visitlisteners", 3145, new string[] { "entries dropped", "kept (switch off)", "errors" }, sVlM12, "entry dropped", f, "no entry untouched", c);
        }

        public static object ScienceFxStopCheck(string what)
        {
            bool f = false, c = false;
            try {  f = SfStopFirst(true); c = !SfStopFirst(false); } catch (Exception) { }
            return B12Check(what, "sciencefxstop", 3148, new string[] { "old sets stopped", "leaked (switch off)", "errors" }, sSfM12, "running set stopped first", f, "no set untouched", c);
        }

        public static object DaycareLikingCheck(string what)
        {
            bool f = false, c = false;
            try {  f = DlXp(10f, 5f, 1.5f, 1f) == 12.5f; c = DlXp(10f, 5f, 1f, 1f) == 10f; } catch (Exception) { }
            return B12Check(what, "daycareliking", 3151, new string[] { "payouts corrected", "payouts at liking 0 (switch off)", "errors" }, "", "real liking moves the payout", f, "same multiplier keeps EA's", c);
        }

        public static object KegBreakageCheck(string what)
        {
            bool f = false, c = false;
            try {  f = KbParty(1f, true, false, true, true, false, 2f, 3f) == 1f && KbParty(1f, false, false, true, true, false, 2f, 3f) == 3f; c = KbParty(1f, true, true, true, true, true, 2f, 3f) == 6f; } catch (Exception) { }
            return B12Check(what, "kegbreakage", 3154, new string[] { "chances computed our way", "kegger rate on a non-kegger party (switch off)", "errors" }, sKbM12, "plain party without the kegger rate", f, "kegger party with EP4 both rates", c);
        }

        public static object RomanceCooldownCheck(string what)
        {
            bool f = false, c = false;
            try {  f = RcBlocked(true, 1f, 6f) && !RcBlocked(false, 1f, 6f); c = !RcBlocked(true, 7f, 6f) && RcBlocked(false, 0.05f, 6f); } catch (Exception) { }
            return B12Check(what, "romancecooldown", 3157, new string[] { "blocked our way", "let through (switch off)", "errors" }, sRcM12, "1 h after a bad date blocked", f, "7 h free, EA's minute rule", c);
        }

        public static object LightCustomCheck(string what)
        {
            bool f = false, c = false;
            try {  f = LcValue(true, 0.3f, 0.8f) == 0.3f; c = LcValue(false, 0.3f, 0.8f) == 0.8f; } catch (Exception) { }
            return B12Check(what, "lightcustom", 3160, new string[] { "lights set our way", "EA's copies (switch off)", "errors" }, sLcM12, "chosen level copied", f, "EA copies the current level", c);
        }

        public static object EatPreyCancelCheck(string what)
        {
            bool f = false, c = false;
            try {  f = EpKeepPrey(false) && EpReenable(false, false); c = !EpKeepPrey(true) && !EpReenable(true, false) && !EpReenable(false, true); } catch (Exception) { }
            return B12Check(what, "eatpreycancel", 3163, new string[] { "prey kept or re-enabled", "seen with the switch off", "errors" }, sEpM12, "cancelled eat keeps, failed run re-enables", f, "eaten / success / destroyed untouched", c);
        }

        public static object MeltAlarmCheck(string what)
        {
            bool f = false, c = false;
            try {  f = MaMelts(false, false); c = !MaMelts(true, true) && MaMelts(true, false); } catch (Exception) { }
            return B12Check(what, "meltalarm", 3166, new string[] { "starts our way", "starts that threw or doubled (switch off)", "errors" }, sMaM12, "no artist melts", f, "Ice Personality keeps, plain artist melts", c);
        }

        public static object NewspaperDefCheck(string what)
        {
            bool f = false, c = false;
            try { InteractionDefinition d = NdDefinition();  f = d != null && !ReferenceEquals(d, Sims3.Gameplay.Objects.Miscellaneous.GetNewspaperChooser.Singleton); c = Sims3.Gameplay.Objects.Miscellaneous.GetNewspaperChooser.Singleton != null; } catch (Exception) { }
            return B12Check(what, "newspaperdef", 3169, new string[] { "reads with an own definition", "shared writes (switch off)", "errors" }, "", "own definition", f, "root singleton present", c);
        }

    }
}
