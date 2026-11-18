using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.EventSystem;
using Sims3.Gameplay.Objects.FoodObjects;
using Sims3.UI;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.ObjectComponents;
using Sims3.Gameplay.Objects.Fishing;
using Sims3.Gameplay.Objects.Gardening;
using Sims3.Gameplay.Opportunities;
using Sims3.Gameplay.Seasons;
using Sims3.Gameplay.Services;
using Sims3.Gameplay.Situations;
using Sims3.Gameplay.Skills;
using Sims3.Gameplay.TuningValues;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // Batch 13 (docs/593 usage sweep 5: situations, services, world systems): EA bug fixes as EA intended, household
    // included. All default off. Slots 3196-3240 (3 each: fixed / seen with the switch off / errors).
    // Not built: G3 (false positive: PropertyStreamWriter XORs each key with a per-type hash, Int32 0x415642B and Float
    // 0x4EDCD7A9, so mFertilizerDaysLeft and mWaterLevel under 101511562 do not collide; the same as docs/590 B7),
    // B2a (inside BlackjackTable's 200-line Run loop: IL patch candidate), V4, V5, V7, V10, V11, V12, V15 (inferred),
    // V6 (the follow alarm only re-pushes a route on a leaving cop; no household effect, needs a per-state CleanUp the
    // child does not have), V13 (when eating "has started" is not defined by EA), V14 (intent unclear: Init restores
    // autonomy on purpose), the minor list.
    public static partial class Fixes
    {
        // ---- B13 §1 nullsituation (docs/593 V2) ----------------------------------------------------------------------
        // [verified, decompiled; trigger inferred] Service.FindAssignmentOnLot returns true when CreateSituation gave null
        // (SocialWorker pet adoption / put-up / child abuse, the bachelor party dancer, the university mascot can).
        // CheckForAssignment then calls AddAssignment, which writes the null into mSituationsAssignedToSims and throws on
        // `assignment.OnExit +=`; Services.Simulate catches only ResetException, so its loop dies and no service (maid,
        // repairman, firefighter, police, ...) is assigned again this session. Fix: a null assignment is not added (the
        // Sim created for it is sent home unless it is in the active household), and one service's failure no longer ends
        // the loop (CheckForAssignment's error is caught; ResetException passes). Counters: [3196] null assignments
        // dropped or errors contained, [3197] seen with the switch off, [3198] errors in our code.
        static readonly MethodInfo sNsAdd = B12M(typeof(Service), "AddAssignment", typeof(Sim), typeof(Lot), typeof(ServiceSituation));
        static readonly MethodInfo sNsCheck = B12M(typeof(Service), "CheckForAssignment");
        static string sNsM13 = "";

        static void ApplyNullSituation()
        {
            sNsM13 = SpNames("Service.AddAssignment", sNsAdd, "Service.CheckForAssignment", sNsCheck);
            if (sNsM13.Length > 0) { AppliedLog().Add("nullsituation: not found: " + sNsM13); return; }
            Patch(typeof(Service), "AddAssignment", new Type[] { typeof(Sim), typeof(Lot), typeof(ServiceSituation) }, typeof(B13NsHook), "AddHook", "AddOrig");
            Patch(typeof(Service), "CheckForAssignment", Type.EmptyTypes, typeof(B13NsHook), "CheckHook", "CheckOrig");
        }

        // Pure: 0 = EA's add, 1 = drop it, 2 = drop it and send the Sim home.
        internal static int NsAction(bool on, bool nullSituation, bool simKnown, bool activeHousehold)
        {
            if (!on || !nullSituation) return 0;
            return simKnown && !activeHousehold ? 2 : 1;
        }

        public class B13NsHook
        {
            public void AddHook(Sim sim, Lot lot, ServiceSituation assignment)
            {
                if (assignment != null) { AddOrig(sim, lot, assignment); return; }
                bool on = ShortcutOn("nullsituation");
                if (!on) { PatchCounters()[3197]++; AddOrig(sim, lot, assignment); return; }
                try
                {
                    bool hh = sim != null && sim.Household != null && sim.Household == Household.ActiveHousehold;
                    if (NsAction(true, true, sim != null && !sim.HasBeenDestroyed, hh) == 2) Sim.MakeSimGoHome(sim, false);
                    PatchCounters()[3196]++;
                }
                catch (Exception) { PatchCounters()[3198]++; }
            }

            public void CheckHook()
            {
                if (!ShortcutOn("nullsituation")) { CheckOrig(); return; }
                try { CheckOrig(); }
                catch (ResetException) { throw; }
                catch (Exception) { PatchCounters()[3196]++; }
            }

            public void AddOrig(Sim sim, Lot lot, ServiceSituation assignment) { }
            public void CheckOrig() { }
        }

        // ---- B13 §2 lightningweights (docs/593 L1) -------------------------------------------------------------------
        // [verified, decompiled] SeasonsManager.WeatherManager.RainEvent.DoLightning writes 0 into the static tunable
        // kLightningStrikeTypeWeights itself (`float[] array = kLightningStrikeTypeWeights; array[0] = 0f` when no Sim is
        // outside on the focus lot, array[1] when no object): after one such strike Sims (or objects) are never struck
        // again this session. Fix: the weights are put back right after (ScheduleLightning follows every DoLightning) from
        // the values held at Apply. Counters: [3199] weights restored, [3200] left zeroed (switch off), [3201] errors.
        static readonly Type sLwRain = B13Nested(typeof(SeasonsManager), "WeatherManager", "RainEvent");
        static readonly FieldInfo sLwWeights = sLwRain == null ? null : B12F(sLwRain, "kLightningStrikeTypeWeights");
        static float[] sLwPristine;
        static string sLwM13 = "";

        static Type B13Nested(Type t, params string[] names)
        {
            foreach (string n in names) { if (t == null) return null; t = t.GetNestedType(n, BindingFlags.Public | BindingFlags.NonPublic); }
            return t;
        }

        static void ApplyLightningWeights()
        {
            sLwM13 = SpNames("SeasonsManager+WeatherManager+RainEvent", sLwRain, "kLightningStrikeTypeWeights", sLwWeights);
            if (sLwM13.Length > 0) { AppliedLog().Add("lightningweights: not found: " + sLwM13); return; }
            float[] w = sLwWeights.GetValue(null) as float[];
            if (w != null) sLwPristine = (float[])w.Clone();
            Patch(sLwRain, "ScheduleLightning", new Type[] { typeof(DateAndTime) }, typeof(B13LwHook), "ScheduleHook", "ScheduleOrig");
        }

        // Pure: put the pristine weights back into the live array; true when one had changed.
        internal static bool LwRestore(float[] live, float[] pristine)
        {
            if (live == null || pristine == null || live.Length != pristine.Length) return false;
            bool changed = false;
            for (int i = 0; i < live.Length; i++) if (live[i] != pristine[i]) { live[i] = pristine[i]; changed = true; }
            return changed;
        }

        public class B13LwHook
        {
            public void ScheduleHook(DateAndTime now)
            {
                try
                {
                    float[] live = sLwWeights.GetValue(null) as float[];
                    if (ShortcutOn("lightningweights")) { if (LwRestore(live, sLwPristine)) PatchCounters()[3199]++; }
                    else if (live != null && sLwPristine != null && Array.IndexOf(live, 0f) >= 0 && Array.IndexOf(sLwPristine, 0f) < 0) PatchCounters()[3200]++;
                }
                catch (Exception) { PatchCounters()[3201]++; }
                ScheduleOrig(now);
            }

            public void ScheduleOrig(DateAndTime now) { }
        }

        // ---- B13 §3 plantdormantflip (docs/593 G1) -------------------------------------------------------------------
        // [verified, decompiled] Plant.UpdateSeasonalState: `if (frost != mDormant || (leafless-capable && leafless !=
        // mDormant)) mDormant = !mDormant`. When frost and leafless disagree (late autumn, early spring) one of them always
        // differs, so a fruit tree flips dormant on every evaluation and grows on alternate days. Fix: the target is
        // frost || (leafless-capable && leafless), set only when it differs (EA's visual-change flag kept). Counters:
        // [3202] flips undone, [3203] flips seen (switch off), [3204] errors.
        static readonly FieldInfo sPfDormant = B12F(typeof(Plant), "mDormant");
        static readonly FieldInfo sPfFrost = B12F(typeof(Plant), "kDormantFrostThreshold");
        static readonly FieldInfo sPfLeaves = B12F(typeof(Plant), "kLeavesCoverageThreshold");
        static readonly MethodInfo sPfLeafless = B12M(typeof(Plant), "HasLeaflessVisualState");
        static readonly MethodInfo sPfFrosted = B12M(typeof(Plant), "HasFrostedVisualState");
        static readonly MethodInfo sPfDelete = B12M(typeof(Plant), "CheckForDeleteHarvestables");
        static string sPfM13 = "";

        static void ApplyPlantDormantFlip()
        {
            sPfM13 = SpNames("Plant.mDormant", sPfDormant, "kDormantFrostThreshold", sPfFrost, "kLeavesCoverageThreshold", sPfLeaves,
                "HasLeaflessVisualState", sPfLeafless, "HasFrostedVisualState", sPfFrosted, "CheckForDeleteHarvestables", sPfDelete);
            if (sPfM13.Length > 0) { AppliedLog().Add("plantdormantflip: not found: " + sPfM13); return; }
            Patch(typeof(Plant), "UpdateSeasonalState", Type.EmptyTypes, typeof(B13PfHook), "SeasonHook", "SeasonOrig");
        }

        // Pure: the dormant state the plant should have.
        internal static bool PfTarget(bool frost, bool leaflessCapable, bool leafless) { return frost || (leaflessCapable && leafless); }

        public class B13PfHook
        {
            public bool SeasonHook()
            {
                bool r = SeasonOrig();
                try
                {
                    Plant p = (Plant)(object)this;
                    if (!SeasonsManager.Enabled) return r;
                    if (World.IsPosInsideGreenhouse(p.Position) || (!p.IsOutside && SeasonsManager.IsShelteredFromPrecipitation(p))) return r;
                    bool capable = (bool)sPfLeafless.Invoke(p, null);
                    bool target = PfTarget(SeasonsManager.FrostLevel > (float)sPfFrost.GetValue(null), capable, SeasonsManager.TreeLeavesCoverage < (float)sPfLeaves.GetValue(null));
                    if ((bool)sPfDormant.GetValue(p) == target) return r;
                    if (!ShortcutOn("plantdormantflip")) { PatchCounters()[3203]++; return r; }
                    sPfDormant.SetValue(p, target);
                    bool visual = (bool)sPfFrosted.Invoke(p, null);
                    if (capable && target) sPfDelete.Invoke(p, null);
                    PatchCounters()[3202]++;
                    return r || visual;
                }
                catch (Exception) { PatchCounters()[3204]++; return r; }
            }

            public bool SeasonOrig() { return false; }
        }

        // ---- B13 §4 plantdormanttime (docs/593 G2) -------------------------------------------------------------------
        // [verified, decompiled; impact inferred] Plant.EvaluatePlantGrowth returns early while dormant (or out of the
        // world) before `SimClock.ElapsedTimeHours(ref mLastEvaluationTime)`, so the first evaluation after the dormant
        // span drains WaterDecay x the whole span at once (a garden comes out of winter dry). Fix: an evaluation that
        // returned early refreshes mLastEvaluationTime (EA's AutoGrow path already does). Counters: [3205] times
        // refreshed, [3206] spans left to drain (switch off), [3207] errors.
        static readonly FieldInfo sPtLast = B12F(typeof(Plant), "mLastEvaluationTime");
        static string sPtM13 = "";

        static void ApplyPlantDormantTime()
        {
            sPtM13 = SpNames("Plant.mLastEvaluationTime", sPtLast, "Plant.mDormant", sPfDormant);
            if (sPtM13.Length > 0) { AppliedLog().Add("plantdormanttime: not found: " + sPtM13); return; }
            Patch(typeof(Plant), "EvaluatePlantGrowth", Type.EmptyTypes, typeof(B13PtHook), "EvalHook", "EvalOrig");
        }

        internal static bool PtRefresh(bool dormant, bool inWorld) { return dormant || !inWorld; }

        public class B13PtHook
        {
            public void EvalHook()
            {
                EvalOrig();
                try
                {
                    Plant p = (Plant)(object)this;
                    if (p.HasBeenDestroyed || !PtRefresh((bool)sPfDormant.GetValue(p), p.InWorld)) return;
                    if (!ShortcutOn("plantdormanttime")) { PatchCounters()[3206]++; return; }
                    sPtLast.SetValue(p, SimClock.ElapsedTime(TimeUnit.Hours));
                    PatchCounters()[3205]++;
                }
                catch (Exception) { PatchCounters()[3207]++; }
            }

            public void EvalOrig() { }
        }

        // ---- B13 §5 harvestextras (docs/593 H2, H5) ------------------------------------------------------------------
        // [verified, decompiled] HarvestPlant.DoHarvest makes the Gatherer and Science extra harvestables with Seed.Copy
        // (out of world) and only adds them to the harvest list: they never reach an inventory (lost, and leaked as
        // objects). HarvestPlant.UpdateGardeningSkillJournal credits mushroom collecting to PlumbBob.SelectedActor, not
        // the harvester (an NPC harvest credits the player's Sim; nobody selected throws). Fix: our DoHarvest copy puts
        // each extra in the harvester's inventory (or the burglar's loot, as HarvestHarvestable does), else destroys it,
        // and the journal (base copy) credits the harvester. The rest is EA's order. Counters: [3208] extras kept / journals
        // credited to the harvester, [3209] extras lost (switch off), [3210] errors.
        static readonly MethodInfo sHeHarvest = B12M(typeof(HarvestPlant), "HarvestHarvestable", typeof(GameObject), typeof(Sim), typeof(BurglarSituation));
        static readonly MethodInfo sHeManip = B12M(typeof(HarvestPlant), "ManipulateHarvestable", typeof(Sim), typeof(GameObject));
        static readonly MethodInfo sHeJournal = B12M(typeof(HarvestPlant), "UpdateGardeningSkillJournal", typeof(Sims3.Gameplay.Skills.Gardening), typeof(PlantDefinition), typeof(List<GameObject>));
        static Sim sHeActor;  // the harvester while our DoHarvest runs (sim thread only)
        static string sHeM13 = "";

        static void ApplyHarvestExtras()
        {
            sHeM13 = SpNames("HarvestPlant.HarvestHarvestable", sHeHarvest, "ManipulateHarvestable", sHeManip, "UpdateGardeningSkillJournal", sHeJournal);
            if (sHeM13.Length > 0) { AppliedLog().Add("harvestextras: not found: " + sHeM13); return; }
            Patch(typeof(HarvestPlant), "DoHarvest", new Type[] { typeof(Sim), typeof(bool), typeof(BurglarSituation) }, typeof(B13HeHook), "HarvestHook", "HarvestOrig");
            Patch(typeof(HarvestPlant), "UpdateGardeningSkillJournal", new Type[] { typeof(Sims3.Gameplay.Skills.Gardening), typeof(PlantDefinition), typeof(List<GameObject>) }, typeof(B13HeHook), "JournalHook", "JournalOrig");
        }

        // Pure: where an extra goes: 1 inventory, 2 burglar loot, 0 destroyed.
        internal static int HeExtra(bool burglar, bool added) { return burglar ? 2 : added ? 1 : 0; }
        // Pure: whom the journal credits.
        internal static Sim HeCredit(bool fixedRule, Sim harvester, Sim selected) { return fixedRule && harvester != null ? harvester : selected; }

        static void HeAddExtras(HarvestPlant hp, Sim actor, BurglarSituation burglar, int n, List<GameObject> list)
        {
            for (int j = 0; j < n; j++)
            {
                GameObject o = hp.Seed.Copy(false) as GameObject;
                if (o == null) continue;
                o.SetOwnerLot(actor.LotHome);
                int where = HeExtra(burglar != null, burglar == null && actor.Inventory.TryToAdd(o));
                if (where == 2) { burglar.StolenObjects.Add(o); burglar.CurrentValue += o.Value; }
                if (where == 0) { o.Destroy(); continue; }
                list.Add(o);
            }
        }

        public class B13HeHook
        {
            public bool HarvestHook(Sim actor, bool hasHarvested, BurglarSituation burglarSituation)
            {
                if (!ShortcutOn("harvestextras")) { PatchCounters()[3209]++; return HarvestOrig(actor, hasHarvested, burglarSituation); }
                HarvestPlant hp = (HarvestPlant)(object)this;
                List<GameObject> list = new List<GameObject>();
                foreach (Slot slot in hp.GetContainmentSlots())
                {
                    GameObject g = hp.GetContainedObject(slot) as GameObject;
                    if (g != null && (bool)sHeHarvest.Invoke(hp, new object[] { g, actor, burglarSituation })) list.Add(g);
                }
                if (actor.TraitManager.HasElement(TraitNames.GathererTrait) && RandomUtil.RandomChance01(TraitTuning.GathererTraitExtraHarvestablesChance))
                    HeAddExtras(hp, actor, burglarSituation, TraitTuning.GathererTraitNumberOfExtraHarvestables, list);
                ScienceSkill science = (ScienceSkill)actor.SkillManager.GetElement(SkillNames.Science);
                if (science != null && RandomUtil.RandomChance01(ScienceSkill.kSuccessRateBonusesGardening[science.SkillLevel]))
                    HeAddExtras(hp, actor, burglarSituation, ScienceSkill.kNumExtraHarvestables[science.SkillLevel], list);
                if (list.Count == 0) return false;
                foreach (GameObject g in list) sHeManip.Invoke(hp, new object[] { actor, g });
                Sims3.Gameplay.Skills.Gardening skill = actor.SkillManager.GetSkill<Sims3.Gameplay.Skills.Gardening>(SkillNames.Gardening);
                Collecting collecting = actor.SkillManager.GetSkill<Collecting>(SkillNames.Collecting);
                int difficulty = hp.PlantDef.GetSkillDifficulty();
                if (skill != null)
                {
                    if (skill.SkillLevel <= difficulty)
                    {
                        if (actor.SimDescription.IsFairy && skill.IsFairySkill()) skill.AddPoints(hp.PlantDef.SkillPointsHarvest * (float)Skill.SkillLevelBumpMultiplierForFairies);
                        else skill.AddPoints(hp.PlantDef.SkillPointsHarvest);
                    }
                    Sim was = sHeActor;
                    sHeActor = actor;
                    try { sHeJournal.Invoke(hp, new object[] { skill, hp.PlantDef, list }); }  // virtual: OmniPlant / MoneyTree keep theirs
                    finally { sHeActor = was; }
                }
                if (!hasHarvested)
                    actor.ShowTNSIfSelectable(Localization.LocalizeString(actor.IsFemale, "Gameplay/Objects/Gardening/HarvestPlant/Harvest:FirstHarvest", actor, hp.PlantDef.Name),
                        StyledNotification.NotificationStyle.kGameMessagePositive, hp.ObjectId, actor.ObjectId);
                if (collecting == null) collecting = (Collecting)actor.SkillManager.AddElement(SkillNames.Collecting);
                collecting.CollectedFromHarvest(list);
                hp.PostHarvest();
                PatchCounters()[3208]++;
                return true;
            }

            public void JournalHook(Sims3.Gameplay.Skills.Gardening gardeningSkill, PlantDefinition def, List<GameObject> harvested)
            {
                if (!ShortcutOn("harvestextras")) { JournalOrig(gardeningSkill, def, harvested); return; }
                HarvestPlant hp = (HarvestPlant)(object)this;
                Sim credit = HeCredit(true, sHeActor, PlumbBob.SelectedActor);
                foreach (GameObject item in harvested)
                {
                    gardeningSkill.Harvested(Plant.GetQuality(item.Plantable.QualityLevel), def);
                    if (Plant.IsMushroom(hp.Seed) && credit != null)
                    {
                        Collecting c = credit.SkillManager.AddElement(SkillNames.Collecting) as Collecting;
                        bool unused;
                        if (c != null) c.Collected(item as Ingredient, out unused);
                    }
                }
            }

            public bool HarvestOrig(Sim actor, bool hasHarvested, BurglarSituation burglarSituation) { return false; }
            public void JournalOrig(Sims3.Gameplay.Skills.Gardening gardeningSkill, PlantDefinition def, List<GameObject> harvested) { }
        }

        // ---- B13 §6 shroomslots (docs/593 H3) ------------------------------------------------------------------------
        // [verified, decompiled] HarvestPlant.CreateHarvestablesLoop takes a mushroom's slot as emptyContainmentSlots[num++]
        // while it also removes each used slot from that list: slots are skipped (0, 2, 4, ...) and with fewer than
        // 2 x yield slots it throws ArgumentOutOfRange before AddHarvestStateTimeoutAlarm, so the mushrooms never time out.
        // Fix: our copy takes the first remaining slot (the same order EA meant). Counters: [3211] mushroom loops our way,
        // [3212] EA's loops (switch off), [3213] errors.
        static readonly FieldInfo sSsShrooms = B12F(typeof(HarvestPlant), "mNumberOfShroomsPerPlant");
        static readonly FieldInfo sSsYielded = B12F(typeof(HarvestPlant), "mLifetimeHarvestablesYielded");
        static readonly FieldInfo sSsHelped = B12F(typeof(Plant), "mSimsWhoHelpedGrow");
        static readonly MethodInfo sSsPost = B12M(typeof(HarvestPlant), "PostCreateHarvestable", typeof(IGameObject));
        static readonly MethodInfo sSsTimeout = B12M(typeof(HarvestPlant), "AddHarvestStateTimeoutAlarm", typeof(float), typeof(TimeUnit));
        static string sSsM13 = "";

        static void ApplyShroomSlots()
        {
            sSsM13 = SpNames("HarvestPlant.mNumberOfShroomsPerPlant", sSsShrooms, "mLifetimeHarvestablesYielded", sSsYielded, "Plant.mSimsWhoHelpedGrow", sSsHelped,
                "PostCreateHarvestable", sSsPost, "AddHarvestStateTimeoutAlarm", sSsTimeout);
            if (sSsM13.Length > 0) { AppliedLog().Add("shroomslots: not found: " + sSsM13); return; }
            Patch(typeof(HarvestPlant), "CreateHarvestablesLoop", new Type[] { typeof(int), typeof(List<Slot>).MakeByRefType() }, typeof(B13SsHook), "LoopHook", "LoopOrig");
        }

        // Pure: the index of the next mushroom slot (EA: the loop counter).
        internal static int SsIndex(bool fixedRule, int counter) { return fixedRule ? 0 : counter; }

        public class B13SsHook
        {
            public void LoopHook(int yield, ref List<Slot> empty)
            {
                HarvestPlant hp = (HarvestPlant)(object)this;
                if (!Plant.IsMushroom(hp.Seed)) { LoopOrig(yield, ref empty); return; }
                if (!ShortcutOn("shroomslots")) { PatchCounters()[3212]++; LoopOrig(yield, ref empty); return; }
                PatchCounters()[3211]++;
                if (GameUtils.IsInstalled(ProductVersion.EP7))
                {
                    yield = (int)sSsShrooms.GetValue(hp);
                    if (yield == 0) yield = 3;
                }
                bool made = false;
                List<SimDescription> helped = (List<SimDescription>)sSsHelped.GetValue(hp);
                while (empty.Count > 0 && yield > 0)
                {
                    Slot slot = empty[SsIndex(true, 0)];
                    GameObject g = hp.CreateHarvestableAndParentToSelf(slot) as GameObject;
                    if (g == null) break;
                    g.AddComponent<HarvestableComponent>(new object[0]);
                    sSsPost.Invoke(hp, new object[] { g });
                    foreach (SimDescription sd in helped)
                        if (sd.CreatedSim != null) EventTracker.SendEvent(EventTypeId.kGrewHarvestable, sd.CreatedSim, g);
                    made = true;
                    empty.Remove(slot);
                    yield--;
                    if (!PlantHelper.IsLotAutoPlantGrow(hp.LotCurrent)) sSsYielded.SetValue(hp, (int)sSsYielded.GetValue(hp) + 1);
                }
                if (made) sSsTimeout.Invoke(hp, new object[] { RandomUtil.GetFloat(hp.PlantDef.HarvestStateDurationMin, hp.PlantDef.HarvestStateDurationMax), TimeUnit.Days });
            }

            public void LoopOrig(int yield, ref List<Slot> empty) { }
        }

        // ---- B13 §7 harvestregion (docs/593 H4) ----------------------------------------------------------------------
        // [verified, decompiled; tuning read] HarvestPlant.GetGrowthPerformanceRegion compares the growth performance with
        // PerformanceThresholds[1] only: below it Good, else Great, so Average (bottom region, no bonus) never comes and
        // every harvestable gets at least kHarvestableQualityBoost[Good] (+0.035). PlantDefinition holds two thresholds.
        // Fix: below [0] Average, below [1] Good, else Great. Counters: [3214] regions our way, [3215] Average answered as
        // Good (switch off), [3216] errors.
        static readonly MethodInfo sHrPerf = B12M(typeof(Plant), "CalculateGrowthPerformance");
        static string sHrM13 = "";

        static void ApplyHarvestRegion()
        {
            sHrM13 = SpNames("Plant.CalculateGrowthPerformance", sHrPerf);
            if (sHrM13.Length > 0) { AppliedLog().Add("harvestregion: not found: " + sHrM13); return; }
            Patch(typeof(HarvestPlant), "GetGrowthPerformanceRegion", Type.EmptyTypes, typeof(B13HrHook), "RegionHook", "RegionOrig");
        }

        internal static GrowthPerformanceRegion HrRegion(bool fixedRule, float perf, float t0, float t1)
        {
            if (fixedRule && perf < t0) return GrowthPerformanceRegion.Average;
            return perf < t1 ? GrowthPerformanceRegion.Good : GrowthPerformanceRegion.Great;
        }

        public class B13HrHook
        {
            public GrowthPerformanceRegion RegionHook()
            {
                HarvestPlant hp = (HarvestPlant)(object)this;
                try
                {
                    if (hp.PlantDef == null || hp.PlantDef.PerformanceThresholds == null || hp.PlantDef.PerformanceThresholds.Length < 2) return RegionOrig();
                    float perf = hp.CalculateGrowthPerformance(), t0 = hp.PlantDef.PerformanceThresholds[0], t1 = hp.PlantDef.PerformanceThresholds[1];
                    bool on = ShortcutOn("harvestregion");
                    if (perf < t0) PatchCounters()[on ? 3214 : 3215]++;
                    return HrRegion(on, perf, t0, t1);
                }
                catch (Exception) { PatchCounters()[3216]++; return RegionOrig(); }
            }

            public GrowthPerformanceRegion RegionOrig() { return GrowthPerformanceRegion.Average; }
        }

        // ---- B13 §8 wellstealcooldown (docs/593 B2b) -----------------------------------------------------------------
        // [verified, decompiled] WishingWell.StealCoins.Definition.Test computes `mLastWishMade + cooldown < now` and drops
        // the result (`_ =`), so coins can be stolen right after every wish; it also builds the cooldown with the
        // two-int DateAndTime constructor (an absolute time). Fix: within kStealCoinsCooldownMinutes of the last wish
        // (SimClock.Add) the test fails. Counters: [3217] tests refused in the cooldown, [3218] passed in it (switch off),
        // [3219] errors.
        static readonly Type sWsWell = B13StoreType("Sims3.Store.Objects.WishingWell");
        static readonly Type sWsDef = B13Nested(sWsWell, "StealCoins", "Definition");
        static readonly FieldInfo sWsLast = sWsWell == null ? null : B12F(sWsWell, "mLastWishMade");
        static readonly FieldInfo sWsCool = sWsWell == null ? null : B12F(sWsWell, "kStealCoinsCooldownMinutes");
        static string sWsM13 = "";

        static Type B13StoreType(string name)
        {
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies()) { Type t = a.GetType(name); if (t != null) return t; }
            return null;
        }

        static void ApplyWellStealCooldown()
        {
            sWsM13 = SpNames("WishingWell", sWsWell, "StealCoins+Definition", sWsDef, "mLastWishMade", sWsLast, "kStealCoinsCooldownMinutes", sWsCool);
            if (sWsM13.Length > 0) { AppliedLog().Add("wellstealcooldown: not found: " + sWsM13); return; }
            Patch(sWsDef, "Test", new Type[] { typeof(Sim), sWsWell, typeof(bool), typeof(GreyedOutTooltipCallback).MakeByRefType() }, typeof(B13WsHook), "TestHook", "TestOrig");
        }

        internal static bool WsCooling(bool everWished, long lastTicks, long nowTicks, long coolTicks) { return everWished && nowTicks <= lastTicks + coolTicks; }

        public class B13WsHook
        {
            public bool TestHook(Sim a, GameObject target, bool isAutonomous, ref GreyedOutTooltipCallback cb)
            {
                bool r = TestOrig(a, target, isAutonomous, ref cb);
                if (!r) return r;
                try
                {
                    DateAndTime last = (DateAndTime)sWsLast.GetValue(target);
                    long cool = SimClock.ConvertToTicks((float)sWsCool.GetValue(null), TimeUnit.Minutes);
                    if (!WsCooling(last != default(DateAndTime), last.Ticks, SimClock.CurrentTicks, cool)) return r;
                    if (!ShortcutOn("wellstealcooldown")) { PatchCounters()[3218]++; return r; }
                    PatchCounters()[3217]++;
                    return false;
                }
                catch (Exception) { PatchCounters()[3219]++; return r; }
            }

            public bool TestOrig(Sim a, GameObject target, bool isAutonomous, ref GreyedOutTooltipCallback cb) { return false; }
        }

        // ---- B13 §9 oppfailcharge (docs/593 O1) ----------------------------------------------------------------------
        // [verified, decompiled; tuning inferred] Opportunity.IsFailed charges the SetupObjectDeleted penalty
        // (ModifyFunds(-TimeoutData)) as a side effect and OpportunityManager calls it twice for the same opportunity in
        // one pass (once to fail it, once more while removing it), so the household pays twice. EA clears SetupObject
        // after the charge. Fix: the charge is made only while SetupObject is still set (once); the answer is EA's.
        // Counters: [3220] second charges skipped, [3221] charged twice (switch off), [3222] errors.
        static void ApplyOppFailCharge()
        {
            Patch(typeof(Opportunity), "IsFailed", Type.EmptyTypes, typeof(B13OfHook), "FailHook", "FailOrig");
        }

        internal static bool OfCharge(bool fixedRule, bool setupObjectSet) { return !fixedRule || setupObjectSet; }

        public class B13OfHook
        {
            public bool FailHook()
            {
                Opportunity o = (Opportunity)(object)this;
                bool deleted;
                try { deleted = o.Timeout == Opportunity.OpportunitySharedData.TimeoutCondition.SetupObjectDeleted && o.SetupObject == null && o.IsTimeout(); }
                catch (Exception) { PatchCounters()[3222]++; return FailOrig(); }
                if (!deleted) return FailOrig();
                // a second call: EA charges again
                if (OfCharge(ShortcutOn("oppfailcharge"), false)) { PatchCounters()[3221]++; return FailOrig(); }
                PatchCounters()[3220]++;
                return true;
            }

            public bool FailOrig() { return false; }
        }

        // ---- B13 §10 tempforecast (docs/593 T1) ----------------------------------------------------------------------
        // [verified, decompiled] SeasonsManager.TemperatureManager.GetMinMaxTemperature (r44 looked on WeatherManager: not found) interpolates `w * min + (1 - w) * max`,
        // the inverse of GetTargetTemperature's `min + w * (max - min)`: the forecast range shown is the mirror of the
        // temperatures that come. Fix: GetTargetTemperature's formula. Counters: [3223] forecasts our way, [3224]
        // mirrored (switch off), [3225] errors.
        static readonly Type sTfMgr = B13Nested(typeof(SeasonsManager), "TemperatureManager");
        static readonly FieldInfo sTfMin = sTfMgr == null ? null : B12F(sTfMgr, "sMinCurve");
        static readonly FieldInfo sTfMax = sTfMgr == null ? null : B12F(sTfMgr, "sMaxCurve");
        static readonly MethodInfo sTfWeight = sTfMgr == null ? null : B12M(sTfMgr, "GetVariationWeight", typeof(DateAndTime));
        static MethodInfo sTfGet;
        static string sTfM13 = "";

        static void ApplyTempForecast()
        {
            MethodInfo target = null;
            if (sTfMgr != null) foreach (MethodInfo m in sTfMgr.GetMethods(kB11All | BindingFlags.DeclaredOnly)) if (m.Name == "GetMinMaxTemperature" && m.GetParameters().Length == 4) target = m;
            if (sTfMin != null) foreach (MethodInfo m in sTfMin.FieldType.GetMethods(kB11All)) if (m.Name == "GetValue" && m.GetParameters().Length == 2 && m.GetParameters()[1].ParameterType == typeof(float)) sTfGet = m;
            sTfM13 = SpNames("SeasonsManager+TemperatureManager", sTfMgr, "sMinCurve", sTfMin, "sMaxCurve", sTfMax, "GetVariationWeight", sTfWeight, "GetMinMaxTemperature(4)", target, "curve.GetValue(weights, float)", sTfGet);
            if (sTfM13.Length > 0) { AppliedLog().Add("tempforecast: not found: " + sTfM13); return; }
            ParameterInfo[] ps = target.GetParameters();
            Patch(sTfMgr, "GetMinMaxTemperature", new Type[] { ps[0].ParameterType, ps[1].ParameterType, ps[2].ParameterType, ps[3].ParameterType }, typeof(B13TfHook), "MinMaxHook", "MinMaxOrig");
        }

        internal static float TfAt(bool fixedRule, float w, float lo, float hi) { return fixedRule ? lo + w * (hi - lo) : w * lo + (1f - w) * hi; }

        public class B13TfHook
        {
            // (DateAndTime time, SeasonVariable<float> weights, out float min, out float max); SeasonVariable is private
            public void MinMaxHook(DateAndTime time, object weights, out float min, out float max)
            {
                if (!ShortcutOn("tempforecast")) { PatchCounters()[3224]++; MinMaxOrig(time, weights, out min, out max); return; }
                try
                {
                    object lo = sTfMin.GetValue(null), hi = sTfMax.GetValue(null);
                    float w = (float)sTfWeight.Invoke(this, new object[] { time });
                    min = float.MaxValue;
                    max = float.MinValue;
                    for (int i = 0; i < 24; i++)
                    {
                        float v = TfAt(true, w, (float)sTfGet.Invoke(lo, new object[] { weights, (float)i }), (float)sTfGet.Invoke(hi, new object[] { weights, (float)i }));
                        if (v < min) min = v;
                        if (v > max) max = v;
                    }
                    PatchCounters()[3223]++;
                }
                catch (Exception) { PatchCounters()[3225]++; MinMaxOrig(time, weights, out min, out max); }
            }

            public void MinMaxOrig(DateAndTime time, object weights, out float min, out float max) { min = 0f; max = 0f; }
        }

        // ---- B13 §11 mountfishcost (docs/593 F1) ---------------------------------------------------------------------
        // [verified, decompiled] HaveFishMounted.Definition keeps mCost on the shared Singleton: GetInteractionName and Test
        // write it for every fish shown, and RunFromInventory charges whatever fish was tested last. Fix: right before Run
        // reads it, the Definition gets the cost of this interaction's own fish (no yield between the write and EA's read).
        // Counters: [3226] costs set to the run's fish, [3227] another fish's cost charged (switch off), [3228] errors.
        static readonly Type sMfType = typeof(Fish).Assembly.GetType("Sims3.Gameplay.Objects.Fishing.HaveFishMounted");
        static readonly Type sMfDef = sMfType == null ? null : sMfType.GetNestedType("Definition", BindingFlags.NonPublic);
        static readonly FieldInfo sMfCost = sMfDef == null ? null : B12F(sMfDef, "mCost");
        static string sMfM13 = "";

        static void ApplyMountFishCost()
        {
            sMfM13 = SpNames("HaveFishMounted", sMfType, "+Definition", sMfDef, "mCost", sMfCost);
            if (sMfM13.Length > 0) { AppliedLog().Add("mountfishcost: not found: " + sMfM13); return; }
            Patch(sMfType, "RunFromInventory", Type.EmptyTypes, typeof(B13MfHook), "RunHook", "RunOrig");
        }

        internal static bool MfStale(int stored, int own) { return stored != own; }

        public class B13MfHook
        {
            public bool RunHook()
            {
                try
                {
                    InteractionInstance ii = (InteractionInstance)(object)this;
                    Fish fish = ii.Target as Fish;
                    object def = ii.InteractionDefinition;
                    if (fish != null && def != null && sMfDef.IsInstanceOfType(def))
                    {
                        int own = MountedFish.GetCostToMountFish(fish);
                        if (MfStale((int)sMfCost.GetValue(def), own))
                        {
                            if (ShortcutOn("mountfishcost")) { sMfCost.SetValue(def, own); PatchCounters()[3226]++; }
                            else PatchCounters()[3227]++;
                        }
                    }
                }
                catch (Exception) { PatchCounters()[3228]++; }
                return RunOrig();
            }

            public bool RunOrig() { return false; }
        }

        // ---- B13 §12 fishscoremounted (docs/593 F2): withdrawn after s58, a false positive -------------------------------
        // Fish.ScoreFish's `Arg1.Contains("Mounted")` is ordinal and case-sensitive, so "Unmounted" does not match it; Arg0
        // comes from PhotographySubjects.LoadSubjectDefinitionArg (never null, "" for empty), so "Any" / "" add a key object
        // once and Contains never throws. s58 showed it: EA's own rule gave the "fixed" answer. Slots 1895-1897 stay unused.

        // ---- B13 §13 firefightercount (docs/593 V8) ------------------------------------------------------------------
        // [verified, decompiled] Firefighter.NeedsAssignment: `assigned <= GetMaxNumNPCsInPool()` lets one more firefighter
        // come while the pool's maximum is already on the lot. Fix: `<`. Counters: [3229] extra firefighters not sent,
        // [3230] sent (switch off), [3231] errors.
        static readonly MethodInfo sFcMax = B12M(typeof(Service), "GetMaxNumNPCsInPool");
        static string sFcM13 = "";

        static void ApplyFirefighterCount()
        {
            sFcM13 = SpNames("Service.GetMaxNumNPCsInPool", sFcMax);
            if (sFcM13.Length > 0) { AppliedLog().Add("firefightercount: not found: " + sFcM13); return; }
            Patch(typeof(Firefighter), "NeedsAssignment", new Type[] { typeof(Lot) }, typeof(B13FcHook), "NeedsHook", "NeedsOrig");
        }

        internal static bool FcRoom(bool fixedRule, int assigned, int max) { return fixedRule ? assigned < max : assigned <= max; }

        public class B13FcHook
        {
            public bool NeedsHook(Lot lot)
            {
                bool r = NeedsOrig(lot);
                if (!r) return r;
                try
                {
                    Service s = (Service)(object)this;
                    int assigned = s.NumSimsAssignedToLot(lot), max = (int)sFcMax.Invoke(s, null);
                    if (FcRoom(true, assigned, max)) return r;
                    if (!ShortcutOn("firefightercount")) { PatchCounters()[3230]++; return r; }
                    PatchCounters()[3229]++;
                    return false;
                }
                catch (Exception) { PatchCounters()[3231]++; return r; }
            }

            public bool NeedsOrig(Lot lot) { return false; }
        }

        // ---- B13 §14 repairmanskill (docs/593 V9) --------------------------------------------------------------------
        // [verified, decompiled] Repairman.UpdateCreatedSim (every time the repairman is created for a job) adds
        // RandomUtil.GetInt(kMinHandinessSkill, kMaxHandinessSkill) levels on top of what the Sim has, so after one or two
        // visits every repairman is at the cap and the tuned 5-10 spread is gone. Fix: the Sim is levelled up to the rolled
        // level (never down). Counters: [3232] levels set to the roll, [3233] stacked (switch off), [3234] errors.
        static readonly FieldInfo sRsMin = B12F(typeof(Repairman), "kMinHandinessSkill");
        static readonly FieldInfo sRsMax = B12F(typeof(Repairman), "kMaxHandinessSkill");
        static string sRsM13 = "";

        static void ApplyRepairmanSkill()
        {
            sRsM13 = SpNames("Repairman.kMinHandinessSkill", sRsMin, "kMaxHandinessSkill", sRsMax);
            if (sRsM13.Length > 0) { AppliedLog().Add("repairmanskill: not found: " + sRsM13); return; }
            Patch(typeof(Repairman), "UpdateCreatedSim", new Type[] { typeof(Sim) }, typeof(B13RsHook), "UpdateHook", "UpdateOrig");
        }

        // Pure: how many level-ups to force (EA: the roll every time).
        internal static int RsLevelUps(bool fixedRule, int level, int roll, int cap) { return fixedRule ? Math.Max(0, Math.Min(roll, cap) - level) : roll; }

        public class B13RsHook
        {
            public void UpdateHook(Sim sim)
            {
                if (!ShortcutOn("repairmanskill")) { PatchCounters()[3233]++; UpdateOrig(sim); return; }
                try
                {
                    Skill skill = sim.SkillManager.AddElement(SkillNames.Handiness);
                    int roll = RandomUtil.GetInt((int)sRsMin.GetValue(null), (int)sRsMax.GetValue(null));
                    int n = RsLevelUps(true, skill.SkillLevel, roll, skill.MaxSkillLevel);
                    for (int i = 0; i < n; i++) skill.ForceGainPointsForLevelUp();
                    PatchCounters()[3232]++;
                }
                catch (Exception) { PatchCounters()[3234]++; }
            }

            public void UpdateOrig(Sim sim) { }
        }

        // ---- B13 §15 roundupreturn (docs/593 V3) ---------------------------------------------------------------------
        // [verified, decompiled] PoliceSituation.RoundUpTeens.Init: with no active actor it sets the Leave state and then
        // goes on (no return), reading RequestingSim / the teen lists of a situation that is leaving: NRE in Init. Fix:
        // return after EA's SetState(Leave). Counters: [3235] inits ended, [3236] run on (switch off), [3237] errors.
        static void ApplyRoundUpReturn()
        {
            Patch(typeof(PoliceSituation.RoundUpTeens), "Init", new Type[] { typeof(PoliceSituation) }, typeof(B13RuHook), "InitHook", "InitOrig");
        }

        internal static bool RuLeaveOnly(bool noActiveActor) { return noActiveActor; }

        public class B13RuHook
        {
            public void InitHook(PoliceSituation parent)
            {
                if (!RuLeaveOnly(Sim.ActiveActor == null)) { InitOrig(parent); return; }
                if (!ShortcutOn("roundupreturn")) { PatchCounters()[3236]++; InitOrig(parent); return; }
                try { parent.SetState(new PoliceSituation.Leave(parent)); PatchCounters()[3235]++; }
                catch (Exception) { PatchCounters()[3237]++; }
            }

            public void InitOrig(PoliceSituation parent) { }
        }

        // ---- B13 §16 butlerpayalarm (docs/593 V1) --------------------------------------------------------------------
        // [verified, decompiled; trigger inferred] ButlerSituation.OnArriveOnLot adds a persisted weekly PayButler alarm
        // owned by the butler; only EndService removes it. A butler situation that ends another way (fired, quit, the
        // situation exited) keeps the alarm on a Sim with a virtual home, and it charges the household every week. Fix:
        // ServiceSituation.CleanUp (the serviceshowalarm swap, which calls ButlerCleanUp after EA's CleanUp) removes it.
        // Counters: [2926] alarms removed, [2927] left (switch off), [2928] errors.
        static readonly FieldInfo sBpAlarm = B12F(typeof(ButlerSituation), "mPayButlerAlarm");
        static string sBpM13 = "";

        static void ApplyButlerPayAlarm()
        {
            sBpM13 = SpNames("ButlerSituation.mPayButlerAlarm", sBpAlarm);
            if (sBpM13.Length > 0) AppliedLog().Add("butlerpayalarm: not found: " + sBpM13);
        }

        internal static bool BpRemove(bool isButler, bool armed) { return isButler && armed; }

        // Called by BSsHook.CleanHook (Batch582Logic.cs) after ServiceSituation.CleanUp.
        static void ButlerCleanUp(ServiceSituation ss)
        {
            if (sBpAlarm == null || !(ss is ButlerSituation)) return;
            try
            {
                AlarmHandle h = (AlarmHandle)sBpAlarm.GetValue(ss);
                if (!BpRemove(true, h != AlarmHandle.kInvalidHandle)) return;
                if (!ShortcutOn("butlerpayalarm")) { PatchCounters()[2927]++; return; }
                if (ss.Worker != null) ss.Worker.RemoveAlarm(h); else AlarmManager.Global.RemoveAlarm(h);
                sBpAlarm.SetValue(ss, AlarmHandle.kInvalidHandle);
                PatchCounters()[2926]++;
            }
            catch (Exception) { PatchCounters()[2928]++; }
        }

        // ---- B13 §17 petharvestvfx (docs/593 H1) ---------------------------------------------------------------------
        // [verified, decompiled] HarvestPlant.EatFromHarvestPlantPet.StartEatingVfx creates the eating effect into a local
        // (`visualEffect = onGround ? mEatGroundVfx : mEatFallVfx` then `visualEffect = VisualEffect.Create(...)`), never
        // into the field, so StopEatingVfx finds null and the effect runs on (and a new one starts every bite). The
        // FoodObjects twin is harvesteatvfx (EP agent). Fix: our copies store the effect in its field and clear it on stop.
        // Counters: [3238] effects kept and stopped, [3239] leaked (switch off), [3240] errors.
        static readonly Type sPvType = typeof(HarvestPlant).GetNestedType("EatFromHarvestPlantPet", BindingFlags.Public | BindingFlags.NonPublic);
        static readonly FieldInfo sPvGround = sPvType == null ? null : B12F(sPvType, "mEatGroundVfx");
        static readonly FieldInfo sPvFall = sPvType == null ? null : B12F(sPvType, "mEatFallVfx");
        static string sPvM13 = "";

        static void ApplyPetHarvestVfx()
        {
            sPvM13 = SpNames("HarvestPlant+EatFromHarvestPlantPet", sPvType, "mEatGroundVfx", sPvGround, "mEatFallVfx", sPvFall);
            if (sPvM13.Length > 0) { AppliedLog().Add("petharvestvfx: not found: " + sPvM13); return; }
            Patch(sPvType, "StartEatingVfx", new Type[] { typeof(bool) }, typeof(B13PvHook), "StartHook", "StartOrig");
            Patch(sPvType, "StopEatingVfx", new Type[] { typeof(bool) }, typeof(B13PvHook), "StopHook", "StopOrig");
        }

        // Pure: EA's effect name for the pet (empty: none).
        internal static string PvName(bool onGround, bool horse, bool bigDog, bool littleDog, bool piggy)
        {
            string t = "";
            if (horse) t = onGround ? "ep5HarvestBitsGroundEat" : "ep5HarvestBitsFallEat";
            else if (bigDog) t = onGround ? "ep5HarvestBitsGroundEatDeer" : "ep5HarvestBitsFallEatDeer";
            else if (littleDog) t = onGround ? "ep5HarvestBitsGroundEatSmallDog" : "ep5HarvestBitsFallEatSmallDog";
            if (piggy) t += "Piggy";
            return t;
        }

        public class B13PvHook
        {
            public void StartHook(bool onGround)
            {
                if (!ShortcutOn("petharvestvfx")) { PatchCounters()[3239]++; StartOrig(onGround); return; }
                try
                {
                    FieldInfo f = onGround ? sPvGround : sPvFall;
                    if (f.GetValue(this) != null) return;
                    Sim a = ((InteractionInstance)(object)this).InstanceActor;
                    string name = PvName(onGround, a.IsHorse, a.IsFullSizeDog, a.IsLittleDog, a.TraitManager.HasElement(TraitNames.PiggyPet));
                    if (name.Length == 0) return;
                    VisualEffect v = VisualEffect.Create(name);
                    if (v == null) return;
                    v.ParentTo(a, Sim.FXJoints.Mouth);
                    v.Start();
                    f.SetValue(this, v);
                    PatchCounters()[3238]++;
                }
                catch (Exception) { PatchCounters()[3240]++; }
            }

            public void StopHook(bool onGround)
            {
                if (!ShortcutOn("petharvestvfx")) { StopOrig(onGround); return; }
                try
                {
                    FieldInfo f = onGround ? sPvGround : sPvFall;
                    VisualEffect v = f.GetValue(this) as VisualEffect;
                    if (v == null) return;
                    v.Stop(VisualEffect.TransitionType.HardTransition);
                    v.Dispose();
                    f.SetValue(this, null);
                }
                catch (Exception) { PatchCounters()[3240]++; }
            }

            public void StartOrig(bool onGround) { }
            public void StopOrig(bool onGround) { }
        }
    }
}
