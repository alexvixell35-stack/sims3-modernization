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
    // Batch 12a checks (docs/590): each runs the hook's own pure decision with controls, the hook's members found at
    // Apply (same handles) and a live-path assert (the hook class is in sPatched). Read-only.
    public static partial class Fixes
    {
        public static object ProtestExitCheck(string what)
        {
            bool f = false, c = false;
            try {  f = PeExit(true, true); c = !PeExit(true, false) && !PeExit(false, true); } catch (Exception) { }
            return B12Check(what, "protestexit", 3064, new string[] { "exits", "root-only cancels (switch off)", "errors" }, "", "protest with a child exits", f, "no child / not a protest untouched", c);
        }

        public static object JogTerrainCheck(string what)
        {
            bool f = false, c = false;
            try {  f = JtStop(true, true); c = !JtStop(false, true) && JtStop(false, false); } catch (Exception) { }
            return B12Check(what, "jogterrain", 3067, new string[] { "walk-backs queued", "fatigue checks with the switch off", "errors" }, sJtM12, "fatigued jogger stops", f, "rested runner goes on, no fast run stops", c);
        }

        public static object LifeguardQuitCheck(string what)
        {
            bool f = false, c = false;
            try {  f = LqQuit(true, false); c = !LqQuit(true, true); } catch (Exception) { }
            return B12Check(what, "lifeguardquit", 3070, new string[] { "loops ended", "quit loops run on (switch off)", "errors" }, "", "quit lifeguard stops", f, "employed lifeguard goes on", c);
        }

        public static object FrolicExitCheck(string what)
        {
            bool f = false, c = false;
            try {  f = FeExit(true, new bool[] { true, false }); c = !FeExit(false, new bool[] { true }) && !FeExit(true, new bool[] { false, false }); } catch (Exception) { }
            return B12Check(what, "frolicexit", 3073, new string[] { "frolics ended", "missed with the switch off", "errors" }, sFeM12, "first critical motive ends", f, "before sustain / none critical go on", c);
        }

        public static object ShooDefCheck(string what)
        {
            bool f = false, c = false;
            try { Sim.TurnToFaceAndAskToLeave.Definition a = SdDefinition(), b = SdDefinition();  f = a != null && !ReferenceEquals(a, b); c = a != null; } catch (Exception) { }
            return B12Check(what, "shoodef", 3076, new string[] { "shoos with an own definition", "shared writes (switch off)", "errors" }, "", "own definition per shoo", f, "definition builds", c);
        }

        public static object IglooLookatsCheck(string what)
        {
            bool f = false, c = false;
            try {  f = IlRestore(true, false, false); c = !IlRestore(false, false, false) && !IlRestore(true, true, false) && !IlRestore(true, false, true); } catch (Exception) { }
            return B12Check(what, "igloolookats", 3079, new string[] { "look-ats restored", "left off (switch off)", "errors" }, sIlM12, "look-ats turned back on", f, "never on / still on / still building untouched", c);
        }

        public static object GoHereBoatListCheck(string what)
        {
            bool f = false, c = false;
            try {  f = GbRemove(true, false) && GbRemove(false, true); c = !GbRemove(false, false); } catch (Exception) { }
            return B12Check(what, "gohereboatlist", 3082, new string[] { "Sims dropped", "left in a list (switch off)", "errors" }, sGbM12, "boat and train entries dropped", f, "not listed untouched", c);
        }

        public static object BouncerRoleCheck(string what)
        {
            bool f = false, c = false;
            try {  f = BrNotice(true, true, false, true, 4f, 3f) && !BrNotice(false, true, false, true, 4f, 3f); c = !BrNotice(true, false, true, true, 0f, 3f) && BrNotice(true, true, true, false, 100f, 3f) && !BrNotice(true, true, false, true, 16f, 3f); } catch (Exception) { }
            return B12Check(what, "bouncerrole", 3085, new string[] { "notice tests answered our way", "tests with the switch off", "null-role runs ended" }, sBrM12, "2 m sneaker inside a 3 m radius noticed", f, "no role / watching / far", c);
        }

        public static object TrickSocialsCheck(string what)
        {
            bool f = false, c = false;
            try {  f = MtRestore(true, false); c = !MtRestore(false, false) && !MtRestore(true, true); } catch (Exception) { }
            return B12Check(what, "tricksocials", 3088, new string[] { "restored", "left off (switch off)", "errors" }, "", "socials turned back on", f, "off before / still on untouched", c);
        }

        public static object FearFaintUndoCheck(string what)
        {
            bool f = false, c = false;
            try {  f = FfUndo(false, true, false) && FfUndo(false, false, true); c = !FfUndo(true, true, false) && !FfUndo(false, false, false); } catch (Exception) { }
            return B12Check(what, "fearfaintundo", 3091, new string[] { "undone", "left (switch off)", "errors" }, sFfM12, "shut-down robot and left jig undone", f, "already shut / clean untouched", c);
        }

        public static object LotPruneCheck(string what)
        {
            bool f = false, c = false;
            try {  f = LpPrune(true, true, 5) && LpPrune(false, false, 5) && LpPrune(false, true, 0); c = !LpPrune(false, true, 3); } catch (Exception) { }
            return B12Check(what, "lotprune", 3094, new string[] { "lots pruned", "lots kept (switch off)", "errors" }, sLpM12, "missing, residential, unvisited lots pruned", f, "visited community lot kept", c);
        }

        public static object WerewolfFindCheck(string what)
        {
            bool f = false, c = false;
            try {  f = WhValue(5, 3, true) == 8; c = WhValue(5, 3, false) == 3; } catch (Exception) { }
            return B12Check(what, "werewolffind", 3097, new string[] { "null finds and insect sums fixed", "seen with the switch off", "errors" }, sWhM12, "insect value added", f, "other finds keep EA's value", c);
        }

        public static object CopyHomeworkFxCheck(string what)
        {
            bool f = false, c = false;
            try {  f = ChStale(true); c = !ChStale(false); } catch (Exception) { }
            return B12Check(what, "copyhomeworkfx", 3100, new string[] { "cleared", "left (switch off)", "errors" }, sChM12, "copier's broadcaster cleared", f, "other actor untouched", c);
        }

        public static object SpellTargetEndCheck(string what)
        {
            bool f = false, c = false;
            try {  f = StEnd(true, true) == 1 && StEnd(true, false) == 2; c = StEnd(false, true) == 0; } catch (Exception) { }
            return B12Check(what, "spelltargetend", 3103, new string[] { "targets released", "left waiting (switch off)", "errors" }, sStM12, "running target finished, queued removed", f, "unlinked target untouched", c);
        }

        public static object FoodTrayGrillCheck(string what)
        {
            bool f = false, c = false;
            try {  f = FtgPutDown(false, true); c = !FtgPutDown(true, true) && !FtgPutDown(false, false); } catch (Exception) { }
            return B12Check(what, "foodtraygrill", 3106, new string[] { "fixed returns", "seen with the switch off", "errors" }, "", "held tray put down on failure", f, "success / empty hands untouched", c);
        }

        public static object CompositionNullCheck(string what)
        {
            bool f = false, c = false;
            try { List<string> l = new List<string>(new string[] { "a", null, "b", null }); List<string> k = new List<string>(new string[] { null, "x" });  f = CnDrop(l, 0) == 2 && l.Count == 2; c = CnDrop(k, 1) == 0 && k.Count == 2; } catch (Exception) { }
            return B12Check(what, "compositionnull", 3109, new string[] { "imports cleaned", "nulls kept (switch off)", "errors" }, "", "nulls dropped", f, "entries before the import kept", c);
        }

        public static object PetDreamTimerCheck(string what)
        {
            bool f = false, c = false;
            try {  f = PdReset(0f) && PdReset(-1f); c = !PdReset(1f); } catch (Exception) { }
            return B12Check(what, "petdreamtimer", 3112, new string[] { "timers reset", "left due (switch off)", "errors" }, sPdM12, "due timer reset", f, "running timer untouched", c);
        }

    }
}
