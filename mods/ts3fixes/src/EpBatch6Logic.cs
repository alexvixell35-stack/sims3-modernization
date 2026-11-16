using System;
using System.Reflection;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.EventSystem;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Objects;
using Sims3.Gameplay.Objects.FoodObjects;
using Sims3.Gameplay.Objects.Spawners;
using Sims3.Gameplay.Objects.TombObjects;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // EP batch 6 (docs/587 §12): tranche 5 (scanner rank, the next 120 unread EP types) and parked EP5 items. Every switch
    // off by default; all household-visible. Checks: EpBatch6Checks.cs. Three counter slots each: fixed / seen with the
    // switch off / errors. Slots from 1609 (block fixes-1600).
    //   floorswitchuselist   [1609] FloorSwitch+StandOn.Cleanup: base cleanup skipped, Sim left on the switch's use list
    //   donationbuffchance   [1612] BuffGiggleFits / BuffPlasmaticPlatitudes: inverted percent roll (85% instead of 15%)
    //   vampirefruitsnack    [1615] VampireFruit.EatEffects: Sanguine Snack needs > Outstanding instead of >=
    //   fallenrockcollected  [1618] FallenRock.RockTimeoutCallback: an imported meteorite's untracked timer destroys it
    public static partial class Fixes
    {
        static void ApplyEpBatch6()
        {
            ApplyFloorSwitchUseList(); ApplyDonationBuffChance(); ApplyVampireFruitSnack(); ApplyFallenRockCollected();
        }

        // ---- floorswitchuselist (docs/587 §12.1) [verified code; reach: a reset or exception while standing on it] ----
        // StandOn.Run routes with RouteToSlotAndCheckInUse (adds the Sim to the switch's use list) and its Cleanup override
        // releases the plate but never calls base.Cleanup, the step that takes the Sim off the use list when the
        // interaction ends early (same defect as rubbleuselist, chestuselist). The switch then stays in use and its
        // StandOn test passes for nobody else. Fix: after StandOn.Cleanup, the Sim is taken off when still on it.
        static void ApplyFloorSwitchUseList()
        {
            Patch(typeof(FloorSwitch).GetNestedType("StandOn", BindingFlags.Public | BindingFlags.NonPublic), "Cleanup", Type.EmptyTypes, typeof(BEp6FsHook), "CleanupHook", "CleanupOrig");
        }

        public class BEp6FsHook
        {
            public void CleanupHook()
            {
                CleanupOrig();
                try
                {
                    InteractionInstance ii = (InteractionInstance)(object)this;
                    Sim actor = ii.InstanceActor;
                    GameObject plate = ii.Target as GameObject;
                    if (actor != null && plate != null && !plate.HasBeenDestroyed && plate.IsActorUsingMe(actor))
                    {
                        if (!ShortcutOn("floorswitchuselist")) PatchCounters()[1610]++;
                        else if (CuRemove(true, true)) { plate.RemoveFromUseList(actor); PatchCounters()[1609]++; }
                    }
                }
                catch (ResetException) { throw; }
                catch (Exception) { PatchCounters()[1611]++; }
            }
            public void CleanupOrig() { } // FloorSwitch+StandOn.Cleanup
        }

        // ---- donationbuffchance (docs/587 §12.2) [verified, shipped tuning] ----
        // The College of Science donation buffs roll `RandomUtil.GetFloat(100f) >= PercentageChance...` before queuing
        // their reaction, so the tunable "Percentage chance of having a giggle fit / of fainting" (both ship 15) gives
        // the reaction 85% of the time. Fix: copies of DoGiggleFit and Faint (no yield) with `<`; the re-arm is EA's.
        internal static bool DbFires(bool on, float roll, float chance) { return on ? roll < chance : roll >= chance; }

        static void ApplyDonationBuffChance()
        {
            Patch(typeof(BuffGiggleFits.BuffInstanceGiggleFits), "DoGiggleFit", Type.EmptyTypes, typeof(BEp6DbHook), "GiggleHook", "GiggleOrig");
            Patch(typeof(BuffPlasmaticPlatitudes.BuffInstancePlasmaticPlatitudes), "Faint", Type.EmptyTypes, typeof(BEp6DbHook), "FaintHook", "FaintOrig");
        }

        static bool DbRoll(float chance)
        {
            float roll = RandomUtil.GetFloat(100f);
            bool fixedFires = DbFires(true, roll, chance), eaFires = DbFires(false, roll, chance);
            if (fixedFires != eaFires) PatchCounters()[1612]++;
            return fixedFires;
        }

        public class BEp6DbHook
        {
            public void GiggleHook()
            {
                if (!ShortcutOn("donationbuffchance")) { PatchCounters()[1613]++; GiggleOrig(); return; }
                BuffGiggleFits.BuffInstanceGiggleFits b = (BuffGiggleFits.BuffInstanceGiggleFits)(object)this;
                if (DbRoll(BuffGiggleFits.PercentageChanceOfFit))  // changed: < instead of >=
                {
                    b.mSim.InteractionQueue.AddNext(BuffGiggleFits.GiggleFit.Singleton.CreateInstance(b.mSim, b.mSim, new InteractionPriority(InteractionPriorityLevel.Autonomous), true, false));
                }
                b.mSim.RemoveAlarm(b.mFitAlarm);
                b.mFitAlarm = b.mSim.AddAlarm(RandomUtil.GetFloat(BuffGiggleFits.MaxTimeBeforePossibleFit - BuffGiggleFits.MinTimeBeforePossibleFit) + BuffGiggleFits.MinTimeBeforePossibleFit,
                    TimeUnit.Minutes, b.DoGiggleFit, "Buff Giggle Fits: Time until next giggle fit", AlarmType.DeleteOnReset);
            }
            public void GiggleOrig() { } // BuffGiggleFits+BuffInstanceGiggleFits.DoGiggleFit

            public void FaintHook()
            {
                if (!ShortcutOn("donationbuffchance")) { PatchCounters()[1613]++; FaintOrig(); return; }
                BuffPlasmaticPlatitudes.BuffInstancePlasmaticPlatitudes b = (BuffPlasmaticPlatitudes.BuffInstancePlasmaticPlatitudes)(object)this;
                if (DbRoll(BuffPlasmaticPlatitudes.PercentageChanceOfFainting))  // changed: < instead of >=
                {
                    b.mSim.InteractionQueue.AddNext(TraitFunctions.CowardTraitFaint.Singleton.CreateInstance(b.mSim, b.mSim, new InteractionPriority(InteractionPriorityLevel.Autonomous), false, false));
                }
                b.mSim.RemoveAlarm(b.mFaintAlarm);
                b.mFaintAlarm = b.mSim.AddAlarm(RandomUtil.GetFloat(BuffPlasmaticPlatitudes.MaxTimeBeforePossibleFaint - BuffPlasmaticPlatitudes.MinTimeBeforePossibleFaint) + BuffPlasmaticPlatitudes.MinTimeBeforePossibleFaint,
                    TimeUnit.Minutes, b.Faint, "Buff Plasmatic Platitudes: Time until faint", AlarmType.DeleteOnReset);
            }
            public void FaintOrig() { } // BuffPlasmaticPlatitudes+BuffInstancePlasmaticPlatitudes.Faint
        }

        // ---- vampirefruitsnack (docs/587 §12.3) [verified, shipped tuning] ----
        // kQualityForSanguineSnack's comment says ">= this value" (ships Outstanding) and the Sated test above it uses
        // >=, but EatEffects compares with >, so an Outstanding fruit gives Sated; only Perfect gives the Snack. Fix: copy
        // of the static EatEffects (no yield) with >=; the Nauseous branch and the event are EA's.
        static System.Reflection.FieldInfo sVfSated, sVfSnack;
        internal static bool VfSnack(bool on, int quality, int snackAt) { return on ? quality >= snackAt : quality > snackAt; }

        static void ApplyVampireFruitSnack()
        {
            sVfSated = typeof(VampireFruit).GetField("kQualityForSated", kEpStatic);
            sVfSnack = typeof(VampireFruit).GetField("kQualityForSanguineSnack", kEpStatic);
            if (sVfSated == null || sVfSnack == null) { AppliedLog().Add("vampirefruitsnack: not found: " + EpMiss("kQualityForSated", sVfSated, "kQualityForSanguineSnack", sVfSnack)); return; }
            Patch(typeof(VampireFruit), "EatEffects", new Type[] { typeof(Sim), typeof(Quality) }, typeof(BEp6VfHook), "EatHook", "EatOrig");
        }

        public class BEp6VfHook
        {
            public static void EatHook(Sim sim, Quality quality)
            {
                Quality sated, snack;
                try { sated = (Quality)sVfSated.GetValue(null); snack = (Quality)sVfSnack.GetValue(null); }
                catch (Exception) { PatchCounters()[1617]++; EatOrig(sim, quality); return; }
                if (quality != snack || !sim.SimDescription.IsVampire) { EatOrig(sim, quality); return; }  // only the == case differs
                if (!ShortcutOn("vampirefruitsnack")) { PatchCounters()[1616]++; EatOrig(sim, quality); return; }
                if (sim.SimDescription.IsVampire && sim.SimDescription.YoungAdultOrAbove)
                {
                    sim.Motives.SetMax(CommodityKind.VampireThirst);
                    if (sim.TraitManager.HasElement(TraitNames.Vegetarian) || quality >= sated)
                    {
                        bool snackBuff = VfSnack(true, (int)quality, (int)snack);  // changed: >= instead of >
                        sim.BuffManager.AddElement(snackBuff ? BuffNames.SanguineSnack : BuffNames.Sated, Origin.FromEatingVampireFruit);
                        if (snackBuff) PatchCounters()[1615]++;
                    }
                }
                else if (!sim.SimDescription.IsVampire)
                {
                    sim.BuffManager.AddElement(BuffNames.Nauseous, Origin.FromEatingVampireFruit);
                }
                EventTracker.SendEvent(EventTypeId.kEatVampireFruit, sim);
            }
            public static void EatOrig(Sim sim, Quality quality) { } // VampireFruit.EatEffects
        }

        // ---- fallenrockcollected (docs/587 §12.4) [verified code; reach: export/import (travel, moving) of a meteorite] ----
        // FallenRock.ImportContent re-adds the AlwaysPersisted timeout alarm but never stores its handle in
        // mTimeoutAlarm, so RockCollected cannot remove it. When it fires, RockTimeoutCallback fades and Destroy()s a
        // meteorite the household already collected. Fix: the callback leaves a collected rock alone (mCollected, set
        // by RockCollected). The untracked timer then ends harmlessly. Parked: ExportContent then writes no timer for an
        // uncollected imported rock (it never times out after a second export); needs the alarm handle.
        static System.Reflection.FieldInfo sFrCollected;
        internal static bool FrSkip(bool on, bool collected) { return on && collected; }

        static void ApplyFallenRockCollected()
        {
            sFrCollected = typeof(RockGemMetalBase).GetField("mCollected", kInstAll);
            if (sFrCollected == null) { AppliedLog().Add("fallenrockcollected: not found: RockGemMetalBase.mCollected"); return; }
            Patch(typeof(FallenRock), "RockTimeoutCallback", Type.EmptyTypes, typeof(BEp6FrHook), "TimeoutHook", "TimeoutOrig");
        }

        public class BEp6FrHook
        {
            public void TimeoutHook()
            {
                try
                {
                    if ((bool)sFrCollected.GetValue(this))
                    {
                        if (!ShortcutOn("fallenrockcollected")) PatchCounters()[1619]++;
                        else if (FrSkip(true, true)) { PatchCounters()[1618]++; return; }
                    }
                }
                catch (Exception) { PatchCounters()[1620]++; }
                TimeoutOrig();
            }
            public void TimeoutOrig() { } // FallenRock.RockTimeoutCallback
        }
    }
}
