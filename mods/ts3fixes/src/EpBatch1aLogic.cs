using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Academics;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Controllers;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.Objects;
using Sims3.Gameplay.Objects.CookingObjects;
using Sims3.Gameplay.Objects.Decorations;
using Sims3.Gameplay.Objects.FoodObjects;
using Sims3.Gameplay.Objects.HobbiesSkills;
using Sims3.Gameplay.Objects.Insect;
using Sims3.Gameplay.Objects.Miscellaneous;
using Sims3.Gameplay.Objects.Resort;
using Sims3.Gameplay.Objects.Vehicles;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // EP batch 1, part a (docs/587 leads 1-10; batch EP1). Every switch off by default. Each lead was diffed against simler90
    // Gameplay Core Mod UPDATE242 (docs/587 §1.6): none of the methods below is changed there. Shipped tuning was read
    // where a lead depends on it (tools/static/tuning-xml-dump.py). Checks: EpBatch1Checks.cs. Counter slots 1400-1499
    // (pinned block fixes-1400, docs/registry.md).
    public static partial class Fixes
    {
        const BindingFlags kEpStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        // ---- EP1 §1 displaycasereset (tuning kDisplayCaseReset) -------------------------------------------------------
        // [verified, decompiled] MetalMineralPotionDisplayCase.DoReset calls Cleanup(), and Cleanup outside the owner's
        // build mode runs Inventory.DestroyItems(): any reset of the case (a Sim reset mid PutAway resets the object it
        // uses, a lot reset, the reset cheat) deletes every stored gem, metal and potion. Cleanup is meant for Dispose
        // (sibling display cases empty only there). Fix at the source: while the case resets, Cleanup keeps the items and
        // only stops the shelf effects (DoReset then restarts them with TestForVFX); EA's build-mode move branch and the
        // Dispose path are unchanged. Household-visible: stored collectibles survive a reset.
        // Counters: [1400] resets that kept the contents, [1401] resets that destroyed contents with the switch off, [1402] errors.
        static readonly Type sDcResetInfo = typeof(GameObject).GetNestedType("ResetInformation", BindingFlags.NonPublic | BindingFlags.Public);
        static int sDcResetDepth;

        // Pure: does this Cleanup keep the items? (inside DoReset, EA's destroy branch, something stored)
        internal static bool DcKeep(bool on, bool inReset, bool buildModeMove, int items)
        {
            return on && inReset && !buildModeMove && items > 0;
        }

        static void ApplyDisplayCaseReset()
        {
            if (sDcResetInfo == null) { AppliedLog().Add("displaycasereset: not found: GameObject+ResetInformation"); return; }
            Patch(typeof(MetalMineralPotionDisplayCase), "DoReset", new Type[] { sDcResetInfo }, typeof(BDcHook), "ResetHook", "ResetOrig");
            Patch(typeof(MetalMineralPotionDisplayCase), "Cleanup", Type.EmptyTypes, typeof(BDcHook), "CleanupHook", "CleanupOrig");
        }

        public class BDcHook
        {
            public void ResetHook(object resetInformation)
            {
                sDcResetDepth++;
                try { ResetOrig(resetInformation); }
                finally { sDcResetDepth--; }
            }
            public void ResetOrig(object resetInformation) { } // MetalMineralPotionDisplayCase.DoReset

            public void CleanupHook()
            {
                MetalMineralPotionDisplayCase c = (MetalMineralPotionDisplayCase)(object)this;
                bool keep = false;
                try
                {
                    if (sDcResetDepth > 0 && c.Inventory != null)
                    {
                        Sim sel = PlumbBob.SelectedActor;
                        bool move = LotManager.sIsBuildBuyModeOn && sel != null && c.GetOwnerLot() == sel.LotHome;  // EA's build-mode branch
                        int items = c.Inventory.AmountIn<GameObject>();
                        if (!move && items > 0)
                        {
                            if (!ShortcutOn("displaycasereset")) PatchCounters()[1401]++;
                            else keep = DcKeep(true, true, false, items);
                        }
                    }
                }
                catch (Exception) { PatchCounters()[1402]++; keep = false; }
                if (!keep) { CleanupOrig(); return; }
                PatchCounters()[1400]++;
                c.StopVFX();  // EA's last step; the items stay
            }
            public void CleanupOrig() { } // MetalMineralPotionDisplayCase.Cleanup
        }

        // ---- EP1 §2 suatgradebonus (tuning kSuatGradeBonus) ------------------------------------------------------------
        // [verified, decompiled + shipped tuning] UniversityWelcomeKit+TakeAptitudeTest.GetSUATScore scales the score by
        // kGradeBonus (shipped 1.3, 1.15, 1, 0.75, 0.5: best grade first). The school path indexes it with School.Grade
        // (A = 0 .. F = 4), which matches, but gates on `CurrentGrade() > Grade.A`, so A students get no bonus while B
        // students get x1.15. The academic path indexes the same array with AcademicsUtility.AcademicGrade (F = 0 .. A = 4),
        // the reverse order: an A GPA gets x0.5, a D GPA x0.75 instead of x1.15. The two paths cannot both be right, the
        // values only make sense best-first, and the gate skips exactly the top grade, so this is a code bug, not design
        // (the tunable comment "F,E,D,C,B,A" lists six letters for five values and is not usable as intent).
        // Fix: academic path indexed best-first (4 - grade; F still skipped as EA does); school path includes A.
        // Household-visible: SUAT scores, scholarships and credits for good students.
        // Counters: [1403] scores corrected, [1404] scores that differ with the switch off, [1405] errors.
        static Type sSuTest;
        static FieldInfo sSuBonus;

        // Pure: kGradeBonus index EA uses (-1: no multiplier). academic: AcademicGrade value; school: School.Grade value.
        internal static int SuEaIndex(bool academic, int grade) { return grade > 0 ? grade : -1; }
        // Pure: the index the tuning means (best grade first).
        internal static int SuFixIndex(bool academic, int grade)
        {
            if (academic) return grade > 0 && grade <= 4 ? 4 - grade : -1;
            return grade >= 0 && grade <= 4 ? grade : -1;
        }
        // Pure: the index used with the switch on / off.
        internal static int SuIndex(bool on, bool academic, int grade) { return on ? SuFixIndex(academic, grade) : SuEaIndex(academic, grade); }
        // Pure: EA's clamp of the scaled score.
        internal static uint SuClamp(int num) { num = num > 400 ? 400 : num; return num < 100 ? 100u : (uint)num; }

        static void ApplySuatGradeBonus()
        {
            sSuTest = typeof(UniversityWelcomeKit).GetNestedType("TakeAptitudeTest", BindingFlags.NonPublic | BindingFlags.Public);
            sSuBonus = sSuTest == null ? null : sSuTest.GetField("kGradeBonus", kEpStatic);
            if (sSuBonus == null) { AppliedLog().Add("suatgradebonus: not found: UniversityWelcomeKit+TakeAptitudeTest.kGradeBonus"); return; }
            Patch(sSuTest, "GetSUATScore", new Type[] { typeof(int) }, typeof(BSuHook), "ScoreHook", "ScoreOrig");
        }

        public class BSuHook
        {
            public uint ScoreHook(int degreeName)
            {
                bool academic = false; int grade = -1; float[] bonus = null;
                try
                {
                    Sim a = ((InteractionInstance)(object)this).InstanceActor;
                    bonus = sSuBonus.GetValue(null) as float[];
                    AcademicDegree el = a == null || a.DegreeManager == null ? null : a.DegreeManager.GetElement((AcademicDegreeNames)degreeName);
                    if (el != null && el.CurrentGpaAsLetterGrade > AcademicsUtility.AcademicGrade.F) { academic = true; grade = (int)el.CurrentGpaAsLetterGrade; }
                    else if (a != null && a.School != null) grade = (int)a.School.CurrentGrade();
                }
                catch (Exception) { PatchCounters()[1405]++; return ScoreOrig(degreeName); }
                int ea = SuEaIndex(academic, grade), fix = SuFixIndex(academic, grade);
                if (bonus == null || bonus.Length < 5 || ea == fix) return ScoreOrig(degreeName);
                if (!ShortcutOn("suatgradebonus")) { PatchCounters()[1404]++; return ScoreOrig(degreeName); }
                PatchCounters()[1403]++;
                if (academic)
                {
                    // EA reads kGradeBonus[grade]: hand it the best-first values at that index for this one call (no yield inside)
                    float[] flipped = new float[bonus.Length];
                    for (int i = 0; i < bonus.Length; i++) flipped[i] = bonus[i];
                    flipped[ea] = bonus[fix];
                    sSuBonus.SetValue(null, flipped);
                    try { return ScoreOrig(degreeName); }
                    finally { sSuBonus.SetValue(null, bonus); }
                }
                // school A: EA applied no multiplier; its score starts at 100 and only grows, so below the 400 cap the
                // returned value is the unscaled score itself
                uint r = ScoreOrig(degreeName);
                return r >= 400 ? r : SuClamp((int)((float)(int)r * bonus[fix]));
            }
            public uint ScoreOrig(int degreeName) { return 0; } // UniversityWelcomeKit+TakeAptitudeTest.GetSUATScore
        }

        // ---- EP1 §3 mermaidsharknull (tuning kMermaidSharkNull) --------------------------------------------------------
        // [verified, decompiled] OccultMermaid+SummonShark.Run: when TryToPlaceAndFadeIn fails, the new shark is destroyed
        // and mMarineLifeObject set to null, then RequestState(NewlySpawned) is called on it: NullReferenceException, and
        // EA's fallback (use an existing shark within kMaxDistanceForExistingShark) is never reached. Fix: our copy of Run
        // with RequestState inside the success branch; everything else as EA (same calls, same order). Household-visible:
        // Evil / MeanSpirited mermaids' Summon Shark works or ends quietly.
        // Counters: [1406] failed placements that took EA's fallback, [1407] NullReferenceExceptions in Run with the switch off, [1408] errors.
        static readonly FieldInfo sMsMax = typeof(OccultMermaid.SummonShark).GetField("kMaxSharksOnLot", kEpStatic);
        static readonly FieldInfo sMsDist = typeof(OccultMermaid.SummonShark).GetField("kMaxDistanceForExistingShark", kEpStatic);

        // Pure: does EA reach the null RequestState? (it spawns, and the placement fails)
        internal static bool MsThrows(int sharksOnLot, int max, bool made, bool placed) { return sharksOnLot < max && made && !placed; }
        // Pure: does Run still reach it? (the fix moves RequestState into the success branch)
        internal static bool MsReachesNull(bool on, int sharksOnLot, int max, bool made, bool placed) { return !on && MsThrows(sharksOnLot, max, made, placed); }

        static void ApplyMermaidSharkNull()
        {
            if (sMsMax == null || sMsDist == null) { AppliedLog().Add("mermaidsharknull: not found: SummonShark.kMaxSharksOnLot/kMaxDistanceForExistingShark"); return; }
            Patch(typeof(OccultMermaid.SummonShark), "Run", Type.EmptyTypes, typeof(BMsHook), "RunHook", "RunOrig");
        }

        public class BMsHook
        {
            public bool RunHook()
            {
                if (!ShortcutOn("mermaidsharknull"))
                {
                    try { return RunOrig(); }
                    catch (NullReferenceException) { PatchCounters()[1407]++; throw; }
                }
                OccultMermaid.SummonShark s = (OccultMermaid.SummonShark)(object)this;
                int max; float dist;
                try { max = (int)sMsMax.GetValue(null); dist = (float)sMsDist.GetValue(null); }
                catch (Exception) { PatchCounters()[1408]++; return RunOrig(); }
                Sim actor = s.Actor;
                MarineLifeBase[] objects = actor.LotCurrent.GetObjects<MarineLifeBase>();
                s.mMarineLifeObject = null;
                if (objects.Length < max)
                {
                    s.mMarineLifeObject = s.mSpeciesToSpawn == MarineLifeSpecies.Undefined ? MarineLifeBase.MakeRandomShark() : MarineLifeBase.Make(s.mSpeciesToSpawn);
                    if (s.mMarineLifeObject == null) return false;
                    s.mMarineLifeObject.mSpawnerParent = null;
                    if (!s.mMarineLifeObject.TryToPlaceAndFadeIn(actor))
                    {
                        s.mMarineLifeObject.Destroy();
                        s.mMarineLifeObject = null;
                        PatchCounters()[1406]++;
                    }
                    else s.mMarineLifeObject.RequestState(MarineLifeStateType.NewlySpawned);  // EA: called even after a failed placement
                }
                if (s.mMarineLifeObject == null)
                {
                    MarineLifeBase best = null;
                    float bestD = 0f, maxD = dist * dist;
                    foreach (MarineLifeBase m in objects)
                    {
                        float d = (m.Position - actor.Position).LengthSqr();
                        if (d < maxD && (best == null || d < bestD)) { bestD = d; best = m; }
                    }
                    s.mMarineLifeObject = best;
                }
                if (s.mMarineLifeObject == null) return false;
                s.StandardEntry();
                s.EnterStateMachine("MermaidSummonShark", "Enter", "x");
                s.AnimateSim("Summon");
                s.AnimateSim("Exit");
                if (s.mMarineLifeObject.CurrentStateType != MarineLifeStateType.Investigate)
                {
                    s.mMarineLifeObject.RequestState(MarineLifeStateType.Investigate, actor.SimDescription);
                    s.mMarineLifeObject.CancelCurrentState();
                }
                s.StandardExit();
                return true;
            }
            public bool RunOrig() { return false; } // OccultMermaid+SummonShark.Run
        }

        // ---- EP1 §4 jetpackflyherecleanup (tuning kJetpackFlyHereCleanup) ----------------------------------------------
        // [verified, decompiled] Jetpack+FlyHere.Cleanup calls GetActiveJetpack().StopFX() with no null check (the sibling
        // FlyAround / JetPackWoohoo cleanups check). Cleanup runs for every removal from the queue, so a queued Fly Here
        // whose jetpack posture ended first (stop jetpack queued before it, a transform, a posture pop) throws in Cleanup
        // and its jigs are left. ts3fixes FutureLogic patches FlyAround only (docs/572 §12). Fix: with no active jetpack,
        // EA's Cleanup without the StopFX call (jigs destroyed, base Cleanup). Household-visible: no script error.
        // Counters: [1409] cleanups without a jetpack handled, [1410] seen with the switch off, [1411] errors.
        static readonly FieldInfo sJfTakeOff = typeof(Jetpack.FlyHere).GetField("mTakeOffJig", kInstAll);
        static readonly FieldInfo sJfLanding = typeof(Jetpack.FlyHere).GetField("mLandingJig", kInstAll);

        // Pure: skip EA's StopFX? (no active jetpack: EA throws)
        internal static bool JfSkip(bool on, bool hasJetpack) { return on && !hasJetpack; }

        static void ApplyJetpackFlyHereCleanup()
        {
            if (sJfTakeOff == null || sJfLanding == null) { AppliedLog().Add("jetpackflyherecleanup: not found: Jetpack+FlyHere.mTakeOffJig/mLandingJig"); return; }
            Patch(typeof(Jetpack.FlyHere), "Cleanup", Type.EmptyTypes, typeof(BJfHook), "CleanupHook", "CleanupOrig");
        }

        // Derives from FlyHere's base so `base.Cleanup()` below is the non-virtual call EA's Cleanup makes.
        public abstract class BJfHook : TerrainInteraction
        {
            public void CleanupHook()
            {
                bool skip = false;
                try
                {
                    Sim a = ((Jetpack.FlyHere)(object)this).Actor;
                    if (a != null && a.GetActiveJetpack() == null)
                    {
                        if (!ShortcutOn("jetpackflyherecleanup")) PatchCounters()[1410]++;
                        else skip = JfSkip(true, false);
                    }
                }
                catch (Exception) { PatchCounters()[1411]++; skip = false; }
                if (!skip) { CleanupOrig(); return; }
                PatchCounters()[1409]++;
                foreach (FieldInfo f in new FieldInfo[] { sJfTakeOff, sJfLanding })
                {
                    GameObject jig = f.GetValue(this) as GameObject;
                    if (jig != null) { jig.Destroy(); f.SetValue(this, null); }
                }
                base.Cleanup();
            }
            public void CleanupOrig() { } // Jetpack+FlyHere.Cleanup
        }

        // ---- EP1 §5 naniteroutefail (tuning kNaniteRouteFail) ----------------------------------------------------------
        // [verified, decompiled] NaniteTerrarium+ReverseEngineer.Cleanup destroys the target whenever the actor has an exit
        // reason other than Canceled. A failed route to a placed terrarium (RouteFailed) or a higher-priority interrupt
        // before it was picked up therefore destroys the terrarium and its nanite with no reverse-engineering result.
        // Fix: the target is destroyed only once it is in the actor's inventory (Run's pickup succeeded, or it started
        // there), which is when EA's run can have consumed it; before that, Cleanup leaves it. Household-visible.
        // Counters: [1412] terrariums kept, [1413] destroyed un-picked-up with the switch off, [1414] errors.
        static readonly FieldInfo sNrProp = typeof(NaniteTerrarium.ReverseEngineer).GetField("mProp", kInstAll);

        // Pure: does EA's Cleanup destroy a target the actor never got? (exit reason not Canceled, not in the inventory)
        internal static bool NrKeep(bool on, bool hasExit, bool canceled, bool inInventory) { return on && hasExit && !canceled && !inInventory; }

        static void ApplyNaniteRouteFail()
        {
            if (sNrProp == null) { AppliedLog().Add("naniteroutefail: not found: NaniteTerrarium+ReverseEngineer.mProp"); return; }
            Patch(typeof(NaniteTerrarium.ReverseEngineer), "Cleanup", Type.EmptyTypes, typeof(BNrHook), "CleanupHook", "CleanupOrig");
        }

        public abstract class BNrHook : Interaction<Sim, NaniteTerrarium>
        {
            public void CleanupHook()
            {
                bool keep = false;
                try
                {
                    NaniteTerrarium.ReverseEngineer r = (NaniteTerrarium.ReverseEngineer)(object)this;
                    Sim a = r.Actor; NaniteTerrarium t = r.Target;
                    if (a != null && t != null && a.HasExitReason() && !a.HasExitReason(ExitReason.Canceled) && (a.Inventory == null || !a.Inventory.Contains(t)))
                    {
                        if (!ShortcutOn("naniteroutefail")) PatchCounters()[1413]++;
                        else keep = NrKeep(true, true, false, false);
                    }
                }
                catch (Exception) { PatchCounters()[1414]++; keep = false; }
                if (!keep) { CleanupOrig(); return; }
                PatchCounters()[1412]++;
                base.Cleanup();
                GameObject prop = sNrProp.GetValue(this) as GameObject;
                if (prop != null) { prop.Destroy(); sNrProp.SetValue(this, null); }
            }
            public void CleanupOrig() { } // NaniteTerrarium+ReverseEngineer.Cleanup
        }

        // ---- EP1 §6 springdanceclear (tuning kSpringDanceClear) --------------------------------------------------------
        // [verified, decompiled] SpringDanceFloor.SimDancers (dance minutes per Sim) is only ever added to; nothing clears
        // it after CheckForDanceWinners (the last-day-of-spring judging). Minutes from earlier festivals and years pass the
        // 3 h threshold, so a king or queen can be crowned without dancing this year, and the list keeps dead or moved-out
        // Sims. Fix: after the judging (also when EA returns early because no household Sim danced, or throws), the
        // floor's tally is cleared. Household-visible: the dance king / queen award counts this festival only.
        // Found by the RE pass (not fixed): the queen branch keeps the sculpture in the mailbox AND tries the inventory
        // when the ticket grant fails after AddMail succeeded; king and queen mail differ in isImmediatelyVisible.
        // Counters: [1415] tallies cleared, [1416] tallies kept with the switch off, [1417] errors.
        static readonly FieldInfo sSdDancers = typeof(SpringDanceFloor).GetField("SimDancers", kInstAll);

        // Pure: clear the tally after judging?
        internal static bool SdClear(bool on, int entries) { return on && entries > 0; }

        static void ApplySpringDanceClear()
        {
            if (sSdDancers == null) { AppliedLog().Add("springdanceclear: not found: SpringDanceFloor.SimDancers"); return; }
            Patch(typeof(SpringDanceFloor), "CheckForDanceWinners", Type.EmptyTypes, typeof(BSpdHook), "CheckHook", "CheckOrig");
        }

        public class BSpdHook
        {
            public void CheckHook()
            {
                try { CheckOrig(); }
                finally
                {
                    try
                    {
                        PairedListDictionary<Sim, float> d = sSdDancers.GetValue(this) as PairedListDictionary<Sim, float>;
                        int n = d == null ? 0 : d.Count;
                        if (SdClear(ShortcutOn("springdanceclear"), n)) { d.Clear(); PatchCounters()[1415]++; }
                        else if (n > 0) PatchCounters()[1416]++;
                    }
                    catch (Exception) { PatchCounters()[1417]++; }
                }
            }
            public void CheckOrig() { } // SpringDanceFloor.CheckForDanceWinners
        }

        // ---- EP1 §7 nectarcopylevel10 (tuning kNectarCopyLevel10) ------------------------------------------------------
        // [verified, decompiled] NectarBottle.DuplicateData copies name, fruit, ingredients, dates, value, creator and
        // quality but not mBottleInfo.MadeByLevel10Sim, and never runs UpdateVisualState / CheckStartVFx. Bottles bought
        // or tasted from a buyable / tasteable NectarRack (lines 278, 371) or bought at a NectarRegister lose the level-10
        // effects (FlameOn, FullOfLife, de-age) and get the default bottle material. Fix: after EA's copy, the flag is
        // copied and the visual state and effects are refreshed as OnCreation / OnStartup do. Household-visible.
        // Found by the RE pass (not fixed, no trigger): NectarRegister.CreateBottleObjectCallBack calls DuplicateData on
        // the created bottle before its `?.` null check.
        // Counters: [1418] copies completed, [1419] copies left incomplete with the switch off, [1420] errors.
        static readonly FieldInfo sNcInfo = typeof(NectarBottle).GetField("mBottleInfo", kInstAll | BindingFlags.DeclaredOnly);
        static readonly MethodInfo sNcVisual = typeof(NectarBottle).GetMethod("UpdateVisualState", kInstAll | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
        static readonly MethodInfo sNcFx = typeof(NectarBottle).GetMethod("CheckStartVFx", kInstAll | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);

        // Pure: the copy's MadeByLevel10Sim.
        internal static bool NcCopied(bool on, bool srcFlag) { return on && srcFlag; }

        static void ApplyNectarCopyLevel10()
        {
            if (sNcInfo == null || sNcVisual == null || sNcFx == null) { AppliedLog().Add("nectarcopylevel10: not found: NectarBottle.mBottleInfo/UpdateVisualState/CheckStartVFx"); return; }
            Patch(typeof(NectarBottle), "DuplicateData", new Type[] { typeof(NectarBottle) }, typeof(BNcbHook), "DupHook", "DupOrig");
        }

        public class BNcbHook
        {
            public void DupHook(NectarBottle src)
            {
                DupOrig(src);
                if (src == null) return;
                try
                {
                    if (!ShortcutOn("nectarcopylevel10")) { PatchCounters()[1419]++; return; }
                    NectarBottle b = (NectarBottle)(object)this;
                    NectarBottle.NectarBottleGlassInfo si = (NectarBottle.NectarBottleGlassInfo)sNcInfo.GetValue(src);
                    NectarBottle.NectarBottleGlassInfo bi = (NectarBottle.NectarBottleGlassInfo)sNcInfo.GetValue(b);
                    bi.MadeByLevel10Sim = NcCopied(true, si.MadeByLevel10Sim);
                    sNcInfo.SetValue(b, bi);
                    sNcVisual.Invoke(b, null);
                    sNcFx.Invoke(b, null);
                    PatchCounters()[1418]++;
                }
                catch (Exception) { PatchCounters()[1420]++; }
            }
            public void DupOrig(NectarBottle src) { } // NectarBottle.DuplicateData
        }

        // ---- EP1 §8 harvesteatvfx (tuning kHarvestEatVfx) --------------------------------------------------------------
        // [verified, decompiled] EatHarvestablePet.StartEatingVfx creates the effect into a local copy of mEatGroundVfx /
        // mEatFallVfx and never writes it back: the fields stay null, StopEatingVfx and Cleanup stop nothing, and every
        // start event (6 persistent handlers) leaves a started native effect on the pet's mouth. StopEatingVfx has the same
        // shape (nulls only its local). Fix: our copies keep the effect in the field and stop, dispose and clear it.
        // Found by the RE pass, folded in: species without an effect (cats, deer, raccoons) got Create("") or
        // Create("Piggy"); no effect is created for them. Household-visible (horses and dogs eating harvestables).
        // Counters: [1421] effects started and kept, [1422] start events leaking with the switch off, [1423] errors.
        static readonly Type sHeType = typeof(Ingredient).Assembly.GetType("Sims3.Gameplay.Objects.FoodObjects.EatHarvestablePet");
        static readonly FieldInfo sHeGround = sHeType == null ? null : sHeType.GetField("mEatGroundVfx", kInstAll);
        static readonly FieldInfo sHeFall = sHeType == null ? null : sHeType.GetField("mEatFallVfx", kInstAll);

        // Pure: EA's effect name ("" = none: the fix creates nothing then).
        internal static string HeName(bool horse, bool fullDog, bool littleDog, bool piggy, bool ground)
        {
            string t = horse ? (ground ? "ep5HarvestBitsGroundEat" : "ep5HarvestBitsFallEat")
                : fullDog ? (ground ? "ep5HarvestBitsGroundEatDeer" : "ep5HarvestBitsFallEatDeer")
                : littleDog ? (ground ? "ep5HarvestBitsGroundEatSmallDog" : "ep5HarvestBitsFallEatSmallDog") : "";
            return t.Length == 0 ? "" : piggy ? t + "Piggy" : t;
        }

        // Pure: is a started effect kept where Stop / Cleanup find it? Is one created for this name?
        internal static bool HeKept(bool on, bool created) { return on && created; }
        internal static bool HeCreates(bool on, string name) { return !on || name.Length > 0; }

        static void ApplyHarvestEatVfx()
        {
            if (sHeGround == null || sHeFall == null) { AppliedLog().Add("harvesteatvfx: not found: EatHarvestablePet.mEatGroundVfx/mEatFallVfx"); return; }
            Patch(sHeType, "StartEatingVfx", new Type[] { typeof(bool) }, typeof(BHeHook), "StartHook", "StartOrig");
            Patch(sHeType, "StopEatingVfx", new Type[] { typeof(bool) }, typeof(BHeHook), "StopHook", "StopOrig");
        }

        public class BHeHook
        {
            public void StartHook(bool onGround)
            {
                if (!ShortcutOn("harvesteatvfx")) { PatchCounters()[1422]++; StartOrig(onGround); return; }
                try
                {
                    FieldInfo f = onGround ? sHeGround : sHeFall;
                    if (f.GetValue(this) != null) return;  // EA's `if (visualEffect == null)`
                    Sim a = ((InteractionInstance)(object)this).InstanceActor;
                    string name = HeName(a.IsHorse, a.IsFullSizeDog, a.IsLittleDog, a.TraitManager.HasElement(TraitNames.PiggyPet), onGround);
                    if (!HeCreates(true, name)) return;
                    VisualEffect fx = VisualEffect.Create(name);
                    if (fx == null) return;
                    fx.ParentTo(a, Sim.FXJoints.Mouth);
                    fx.Start();
                    f.SetValue(this, fx);
                    PatchCounters()[1421]++;
                }
                catch (Exception) { PatchCounters()[1423]++; }
            }
            public void StartOrig(bool onGround) { } // EatHarvestablePet.StartEatingVfx

            public void StopHook(bool onGround)
            {
                if (!ShortcutOn("harvesteatvfx")) { StopOrig(onGround); return; }
                try
                {
                    FieldInfo f = onGround ? sHeGround : sHeFall;
                    VisualEffect fx = f.GetValue(this) as VisualEffect;
                    if (fx == null) return;
                    fx.Stop(VisualEffect.TransitionType.HardTransition);
                    fx.Dispose();
                    f.SetValue(this, null);
                }
                catch (Exception) { PatchCounters()[1423]++; }
            }
            public void StopOrig(bool onGround) { } // EatHarvestablePet.StopEatingVfx
        }

        // ---- EP1 §9 buffetemptypick (tuning kBuffetEmptyPick) ----------------------------------------------------------
        // [verified code, frequency inferred] ResortBuffetTable.ConsumeMoreFood (after a buffet meal, hunger <= 95) picks a
        // random buffet table in the room, else on the lot, including tables set to FoodType.None or whose food was
        // cleaned up, then GetRandomObjectFromList on that table's empty ServingContainerGroup list throws ArgumentException
        // out of EatHeldFood.Run. Fix: our copy picks among the tables that hold servings (room first, then lot, as EA);
        // none: no second helping (EA's outcome without the throw). With food on every candidate table the same list is
        // drawn from as EA. Household-visible (resort guests and household Sims eating at a buffet).
        // Counters: [1424] picks limited to tables with food, [1425] picks that could throw with the switch off, [1426] errors.
        static readonly FieldInfo sBfSlots = typeof(ResortBuffetTable).GetField("kFoodSlots", kEpStatic);

        // Pure: which list EA draws from (true: room) and which the fix draws from (1 room, 2 lot, 0 none).
        internal static int BfFixList(int roomWithFood, int lotWithFood) { return roomWithFood > 0 ? 1 : lotWithFood > 0 ? 2 : 0; }
        internal static bool BfEaMayThrow(int roomTables, int roomWithFood, int lotTables, int lotWithFood)
        {
            return roomTables > 0 ? roomWithFood < roomTables : lotTables > 0 && lotWithFood < lotTables;
        }

        // Pure: can the pick land on a table without servings (EA: ArgumentException)?
        internal static bool BfMayPickEmpty(bool on, int roomTables, int roomWithFood, int lotTables, int lotWithFood)
        {
            return !on && BfEaMayThrow(roomTables, roomWithFood, lotTables, lotWithFood);
        }

        static void ApplyBuffetEmptyPick()
        {
            if (sBfSlots == null) { AppliedLog().Add("buffetemptypick: not found: ResortBuffetTable.kFoodSlots"); return; }
            Patch(typeof(ResortBuffetTable), "ConsumeMoreFood", new Type[] { typeof(Sim) }, typeof(BBfHook), "ConsumeHook", "ConsumeOrig");
        }

        static List<ResortBuffetTable> BfWithFood(IEnumerable<ResortBuffetTable> tables, Slot[] slots, out int total)
        {
            List<ResortBuffetTable> r = new List<ResortBuffetTable>(); total = 0;
            foreach (ResortBuffetTable t in tables)
            {
                total++;
                if (t.GetContainedObjectList<ServingContainerGroup>(slots).Count > 0) r.Add(t);
            }
            return r;
        }

        public class BBfHook
        {
            public static void ConsumeHook(Sim sim)
            {
                List<ResortBuffetTable> pick = null;
                try
                {
                    Slot[] slots = sBfSlots.GetValue(null) as Slot[];
                    int roomN, lotN;
                    List<ResortBuffetTable> room = BfWithFood(sim.LotCurrent.GetObjectsInRoom<ResortBuffetTable>(sim.RoomId), slots, out roomN);
                    List<ResortBuffetTable> lot = BfWithFood(sim.LotCurrent.GetObjects<ResortBuffetTable>(), slots, out lotN);
                    if (BfEaMayThrow(roomN, room.Count, lotN, lot.Count))
                    {
                        if (!ShortcutOn("buffetemptypick")) { PatchCounters()[1425]++; ConsumeOrig(sim); return; }
                        PatchCounters()[1424]++;
                        int which = BfFixList(room.Count, lot.Count);
                        pick = which == 1 ? room : which == 2 ? lot : new List<ResortBuffetTable>();
                    }
                }
                catch (Exception) { PatchCounters()[1426]++; pick = null; }
                if (pick == null) { ConsumeOrig(sim); return; }
                if (pick.Count == 0) return;
                ResortBuffetTable t = RandomUtil.GetRandomObjectFromList(pick);
                ServingContainerGroup g = RandomUtil.GetRandomObjectFromList(t.GetContainedObjectList<ServingContainerGroup>(sBfSlots.GetValue(null) as Slot[]));
                sim.InteractionQueue.PushAsContinuation(ServingContainerGroup.ServingContainerGroup_GrabAServing.Singleton, g, false);
            }
            public static void ConsumeOrig(Sim sim) { } // ResortBuffetTable.ConsumeMoreFood
        }

        // ---- EP1 §10 krakenfriendsailor (tuning kKrakenFriendSailor) ---------------------------------------------------
        // [verified, decompiled] KrakenManager.KrakenAttackEndAlarmCallback scans a boat's occupants and `break`s on the
        // first Sailor before it tests FriendOfTheKraken, so with a Sailor seated before the friend (or one Sim with both
        // traits) the friend's guaranteed immunity is lost and the boat is attacked whenever the 50% sailor roll fails
        // (Boat+RewardReleaseTheKraken tests the friend separately). Fix: during the alarm, the boats where EA's scan misses
        // a friend are noted; the attack one-shot EA schedules for such a boat (the next frame) does nothing. Deliberate
        // releases call the one-shot outside the alarm and are untouched (the note lives until that one-shot only).
        // Deviation: EA also cleared its boat tracking list when it scheduled the attack (as in vanilla).
        // Found by the RE pass (not fixed: needs a copy of the private KrakenHelper): KrakenAttackOneShot leaks the
        // out-of-world kraken jig when FindGoodLocationOnWorldAtCustomDepth fails or the boat has no driver.
        // Counters: [1427] attacks on immune boats dropped, [1428] scheduled with the switch off, [1429] errors.
        static readonly FieldInfo sKrList = typeof(KrakenManager).GetField("mBoatList", kInstAll);
        static readonly Dictionary<ObjectGuid, bool> sKrImmune = new Dictionary<ObjectGuid, bool>();

        // Pure: does EA's occupant scan miss a FriendOfTheKraken? (it stops at the first Sailor or friend)
        internal static bool KrMisses(bool[] sailor, bool[] friend)
        {
            bool anyFriend = false;
            for (int i = 0; i < friend.Length; i++) anyFriend |= friend[i];
            for (int i = 0; i < sailor.Length; i++)
            {
                if (sailor[i]) return anyFriend;
                if (friend[i]) return false;
            }
            return false;
        }

        // Pure: is the friend's immunity lost?
        internal static bool KrImmunityLost(bool on, bool[] sailor, bool[] friend) { return !on && KrMisses(sailor, friend); }

        static void ApplyKrakenFriendSailor()
        {
            if (sKrList == null) { AppliedLog().Add("krakenfriendsailor: not found: KrakenManager.mBoatList"); return; }
            Patch(typeof(KrakenManager), "KrakenAttackEndAlarmCallback", Type.EmptyTypes, typeof(BKrHook), "AlarmHook", "AlarmOrig");
            Patch(typeof(KrakenManager), "KrakenAttackOneShot", new Type[] { typeof(object) }, typeof(BKrHook), "ShotHook", "ShotOrig");
        }

        public class BKrHook
        {
            public void AlarmHook()
            {
                sKrImmune.Clear();
                try
                {
                    foreach (IBoat b in Sims3.Gameplay.Queries.GetObjects<IBoat>())
                    {
                        if (!(b is Boat) || !b.InWorld) continue;
                        Sim[] occ = b.GetOccupants();
                        if (occ == null) continue;
                        bool[] sailor = new bool[occ.Length], friend = new bool[occ.Length];
                        for (int i = 0; i < occ.Length; i++)
                            if (occ[i] != null) { sailor[i] = occ[i].TraitManager.HasElement(TraitNames.Sailor); friend[i] = occ[i].TraitManager.HasElement(TraitNames.FriendOfTheKraken); }
                        if (KrMisses(sailor, friend)) sKrImmune[b.ObjectId] = true;
                    }
                }
                catch (Exception) { PatchCounters()[1429]++; sKrImmune.Clear(); }
                int before = -1;
                try { System.Collections.ICollection l = sKrList.GetValue(this) as System.Collections.ICollection; before = l == null ? -1 : l.Count; } catch (Exception) { }
                AlarmOrig();
                try
                {
                    System.Collections.ICollection l = sKrList.GetValue(this) as System.Collections.ICollection;
                    if (!(before > 0 && l != null && l.Count == 0)) sKrImmune.Clear();  // EA clears its list only when it schedules an attack
                }
                catch (Exception) { sKrImmune.Clear(); }
            }
            public void AlarmOrig() { } // KrakenManager.KrakenAttackEndAlarmCallback

            public static void ShotHook(object obj)
            {
                bool drop = false;
                try
                {
                    KrakenAttackParams p = obj as KrakenAttackParams;
                    if (p != null && p.TargetBoat != null && sKrImmune.ContainsKey(p.TargetBoat.ObjectId))
                    {
                        if (!ShortcutOn("krakenfriendsailor")) PatchCounters()[1428]++;
                        else { drop = true; p.AttackSucceeded = false; PatchCounters()[1427]++; }
                    }
                }
                catch (Exception) { PatchCounters()[1429]++; drop = false; }
                sKrImmune.Clear();  // one alarm, one one-shot
                if (!drop) ShotOrig(obj);
            }
            public static void ShotOrig(object obj) { } // KrakenManager.KrakenAttackOneShot
        }
    }
}
