using System;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Objects.Gardening;

namespace Ts3Fixes
{
    // Batch 13 checks (docs/593): the hook's own pure decision with controls, the members found at Apply and the live path
    // (B582Counters / B582Line add CkLive: swaps in place, handles usable). Read-only.
    public static partial class Fixes
    {
        public static object NullSituationCheck(string what)
        {
            bool f = false, c = false;
            try { f = NsAction(true, true, true, false) == 2; c = NsAction(true, true, true, true) == 1 && NsAction(true, false, true, false) == 0 && NsAction(false, true, true, false) == 0; } catch (Exception) { }
            string miss = sNsM13;
            return B12Check(what, "nullsituation", 1862, new string[] { "null assignments dropped or errors contained", "seen with the switch off", "errors" }, miss, "null situation dropped, Sim sent home", f, "household Sim kept, real situation added", c);
        }

        public static object LightningWeightsCheck(string what)
        {
            bool f = false, c = false;
            try { float[] live = new float[] { 0f, 1f, 1f }; float[] same = new float[] { 1f, 1f, 1f }; f = LwRestore(live, new float[] { 1f, 1f, 1f }) && live[0] == 1f; c = !LwRestore(same, new float[] { 1f, 1f, 1f }) && !LwRestore(same, new float[] { 1f }); } catch (Exception) { }
            string miss = sLwM13;
            return B12Check(what, "lightningweights", 1865, new string[] { "weights restored", "left zeroed (switch off)", "errors" }, miss, "zeroed weight restored", f, "unchanged / mismatched left", c);
        }

        public static object PlantDormantFlipCheck(string what)
        {
            bool f = false, c = false;
            try { f = PfTarget(true, true, false) && PfTarget(false, true, true); c = !PfTarget(false, true, false) && !PfTarget(false, false, true); } catch (Exception) { }
            string miss = sPfM13;
            return B12Check(what, "plantdormantflip", 1868, new string[] { "flips undone", "flips seen (switch off)", "errors" }, miss, "frost or leafless makes dormant", f, "no frost, leaves on / not leafless-capable", c);
        }

        public static object PlantDormantTimeCheck(string what)
        {
            bool f = false, c = false;
            try { f = PtRefresh(true, true) && PtRefresh(false, false); c = !PtRefresh(false, true); } catch (Exception) { }
            string miss = sPtM13;
            return B12Check(what, "plantdormanttime", 1871, new string[] { "times refreshed", "spans left to drain (switch off)", "errors" }, miss, "dormant or out of world refreshed", f, "growing plant keeps EA's drain", c);
        }

        public static object HarvestExtrasCheck(string what)
        {
            bool f = false, c = false;
            try { Sim s = PlumbBob.SelectedActor; f = HeExtra(false, true) == 1 && HeExtra(true, false) == 2 && (s == null || ReferenceEquals(HeCredit(true, s, null), s)); c = HeExtra(false, false) == 0 && HeCredit(false, s, null) == null; } catch (Exception) { }
            string miss = sHeM13;
            return B12Check(what, "harvestextras", 1874, new string[] { "extras kept and journals to the harvester", "extras lost (switch off)", "errors" }, miss, "extra to inventory, harvester credited", f, "full inventory destroys, EA credits the selected Sim", c);
        }

        public static object ShroomSlotsCheck(string what)
        {
            bool f = false, c = false;
            try { f = SsIndex(true, 2) == 0; c = SsIndex(false, 2) == 2; } catch (Exception) { }
            string miss = sSsM13;
            return B12Check(what, "shroomslots", 1877, new string[] { "mushroom loops our way", "EA's loops (switch off)", "errors" }, miss, "first remaining slot taken", f, "EA's counter index", c);
        }

        public static object HarvestRegionCheck(string what)
        {
            bool f = false, c = false;
            try { f = HrRegion(true, 10f, 20f, 50f) == GrowthPerformanceRegion.Average && HrRegion(false, 10f, 20f, 50f) == GrowthPerformanceRegion.Good; c = HrRegion(true, 30f, 20f, 50f) == GrowthPerformanceRegion.Good && HrRegion(true, 60f, 20f, 50f) == GrowthPerformanceRegion.Great; } catch (Exception) { }
            string miss = sHrM13;
            return B12Check(what, "harvestregion", 1880, new string[] { "regions our way", "Average answered as Good (switch off)", "errors" }, miss, "below the first threshold Average", f, "middle Good, top Great", c);
        }

        public static object WellStealCooldownCheck(string what)
        {
            bool f = false, c = false;
            try { f = WsCooling(true, 100, 150, 100); c = !WsCooling(true, 100, 250, 100) && !WsCooling(false, 0, 10, 100); } catch (Exception) { }
            string miss = sWsM13;
            return B12Check(what, "wellstealcooldown", 1883, new string[] { "tests refused in the cooldown", "passed in it (switch off)", "errors" }, miss, "inside the cooldown refused", f, "after it / never wished allowed", c);
        }

        public static object OppFailChargeCheck(string what)
        {
            bool f = false, c = false;
            try { f = !OfCharge(true, false); c = OfCharge(true, true) && OfCharge(false, false); } catch (Exception) { }
            string miss = "";
            return B12Check(what, "oppfailcharge", 1886, new string[] { "second charges skipped", "charged twice (switch off)", "errors" }, miss, "second call skips the charge", f, "first call charges, EA charges again", c);
        }

        public static object TempForecastCheck(string what)
        {
            bool f = false, c = false;
            try { f = TfAt(true, 0.25f, 0f, 8f) == 2f; c = TfAt(false, 0.25f, 0f, 8f) == 6f; } catch (Exception) { }
            string miss = sTfM13;
            return B12Check(what, "tempforecast", 1889, new string[] { "forecasts our way", "mirrored (switch off)", "errors" }, miss, "w=0.25 gives min + quarter", f, "EA's mirror gives 6", c);
        }

        public static object MountFishCostCheck(string what)
        {
            bool f = false, c = false;
            try { f = MfStale(50, 80); c = !MfStale(80, 80); } catch (Exception) { }
            string miss = sMfM13;
            return B12Check(what, "mountfishcost", 1892, new string[] { "costs set to the run's fish", "another fish's cost charged (switch off)", "errors" }, miss, "other fish's cost replaced", f, "own cost untouched", c);
        }

        public static object FirefighterCountCheck(string what)
        {
            bool f = false, c = false;
            try { f = !FcRoom(true, 3, 3) && FcRoom(false, 3, 3); c = FcRoom(true, 2, 3); } catch (Exception) { }
            string miss = sFcM13;
            return B12Check(what, "firefightercount", 1898, new string[] { "extra firefighters not sent", "sent (switch off)", "errors" }, miss, "at the maximum none more", f, "below the maximum sent", c);
        }

        public static object RepairmanSkillCheck(string what)
        {
            bool f = false, c = false;
            try { f = RsLevelUps(true, 6, 8, 10) == 2 && RsLevelUps(true, 10, 7, 10) == 0; c = RsLevelUps(false, 6, 8, 10) == 8; } catch (Exception) { }
            string miss = sRsM13;
            return B12Check(what, "repairmanskill", 1901, new string[] { "levels set to the roll", "stacked (switch off)", "errors" }, miss, "level 6, roll 8: two level-ups", f, "EA adds the roll", c);
        }

        public static object RoundUpReturnCheck(string what)
        {
            bool f = false, c = false;
            try { f = RuLeaveOnly(true); c = !RuLeaveOnly(false); } catch (Exception) { }
            string miss = "";
            return B12Check(what, "roundupreturn", 1904, new string[] { "inits ended", "run on (switch off)", "errors" }, miss, "no active Sim: leave only", f, "active Sim: EA's Init", c);
        }

        public static object ButlerPayAlarmCheck(string what)
        {
            bool f = false, c = false;
            try { f = BpRemove(true, true); c = !BpRemove(false, true) && !BpRemove(true, false); } catch (Exception) { }
            string miss = sBpM13; miss = EpJoin(miss, EpLive(typeof(BSsHook), 2));
            return B12Check(what, "butlerpayalarm", 1907, new string[] { "alarms removed", "left (switch off)", "errors" }, miss, "armed butler alarm removed", f, "other services / no alarm untouched", c);
        }

        public static object PetHarvestVfxCheck(string what)
        {
            bool f = false, c = false;
            try { f = PvName(true, true, false, false, false) == "ep5HarvestBitsGroundEat" && PvName(false, false, true, false, true) == "ep5HarvestBitsFallEatDeerPiggy"; c = PvName(true, false, false, false, false).Length == 0; } catch (Exception) { }
            string miss = sPvM13;
            return B12Check(what, "petharvestvfx", 1910, new string[] { "effects kept and stopped", "leaked (switch off)", "errors" }, miss, "horse effect named", f, "cat has no effect", c);
        }
    }
}
