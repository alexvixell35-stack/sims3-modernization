using System;
using System.Collections.Generic;
using Sims3.Gameplay.Academics;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.Objects.TombObjects;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // Harness checks for EpBatch4Logic.cs and EpBatch4bLogic.cs (docs/587 §9-§10, batch EP4). Pure decisions with
    // controls (the hooks call the same functions with their live values); nothing in the world is touched. The members
    // verdict also asserts the live path (EpLive): the hook's swaps are in place and every reflection handle it uses is
    // usable the way it uses it.
    public static partial class Fixes
    {
        static object Ep4Check(string sw, int first, string[] what, string miss, string what2, string fixedText, bool fixedNow, string ctlText, bool controls)
        {
            string counters = B582Counters(sw, first, what, miss);
            if (what2 == "result") return counters;
            if (what2 != "run") return sw + " <run|result>";
            return B582Line(sw, ShortcutOn(sw), fixedText, fixedNow, ctlText + "=" + controls, controls, miss, counters);
        }

        // DogSwimSlotsCheck run: no free slot skips the reroute loop. Control: free slots loop as EA.
        public static object DogSwimSlotsCheck(string what)
        {
            bool on = ShortcutOn("dogswimslots");
            return Ep4Check("dogswimslots", 3247, new[] { "loops skipped, no free slot", "would throw with switch off", "errors" },
                EpJoin(EpMiss("kNumberOfTimesForHydrophobicDogToRouteToOtherSlots", sDsTimes, "kChanceToShowHydrophobicThoughtBalloon", sDsChance),
                    EpLive(typeof(BEp4DsHook), 1, "kNumberOfTimesForHydrophobicDogToRouteToOtherSlots", sDsTimes, typeof(int[]), "kChanceToShowHydrophobicThoughtBalloon", sDsChance, typeof(float))), what,
                "empty slot list skipped", !DsLoopRuns(on, 0), "free slots loop", DsLoopRuns(on, 3));
        }

        // FragmentReconstructCheck run: Target 4th of 5 with 3 needed uses 3. Controls: Target first, too few free.
        public static object FragmentReconstructCheck(string what)
        {
            bool on = ShortcutOn("fragmentreconstruct");
            return Ep4Check("fragmentreconstruct", 3250, new[] { "Target picked first", "extra fragment used with switch off", "errors" },
                EpJoin(EpMiss("FragmentedObject+Reconstruct", sFrType, "mToRemoveObj", sFrRemove, "mCreatedObject", sFrCreated, "FragmentInfo", sFrInfo, "FragmentInventoryTest", sFrTest, "LocalizeString", sFrLoc,
                    "EventCallbackPlaySound", sFrSound, "FragmentData.RequiredNumberOfItems", sFrRequired, "FragmentData.RouteDropDistance", sFrDrop),
                    EpLive(typeof(BEp4FrHook), 1, "FragmentedObject+Reconstruct", sFrType, typeof(InteractionInstance), "mToRemoveObj", sFrRemove, typeof(List<FragmentedObject>),
                        "mCreatedObject", sFrCreated, typeof(IGameObject), "FragmentInfo", sFrInfo, "get", "FragmentInventoryTest", sFrTest, typeof(bool), "LocalizeString", sFrLoc, 2,
                        "EventCallbackPlaySound", sFrSound, 2, "FragmentData.RequiredNumberOfItems", sFrRequired, typeof(int), "FragmentData.RouteDropDistance", sFrDrop, typeof(float))), what,
                "3 used, not 4", FrUsed(on, 3, 5, 3) == 3, "Target first uses 3, too few uses 0", FrUsed(on, 3, 5, 0) == 3 && FrUsed(on, 3, 2, 0) == 0);
        }

        // LabObjectPushCheck run: an empty inventory uses a lot object. Controls: inventory objects or no lot objects as EA.
        public static object LabObjectPushCheck(string what)
        {
            bool on = ShortcutOn("labobjectpush");
            return Ep4Check("labobjectpush", 3253, new[] { "lot objects pushed", "idle students with switch off", "errors" },
                EpJoin(EpMiss("mAcademicLabObjects", sLbObjects, "LabSituationFinished", sLbDone, "LabSituationFailed", sLbFail),
                    EpLive(typeof(BEp4LbHook), 1, "mAcademicLabObjects", sLbObjects, typeof(List<IAcademicLabObject>), "LabSituationFinished", sLbDone, 2, "LabSituationFailed", sLbFail, 2)), what,
                "lot object used", LbUseLot(on, 0, 2), "inventory / no lot objects as EA", !LbUseLot(on, 1, 2) && !LbUseLot(on, 0, 0));
        }

        // BusinessPlannerRunCheck run: no pocketing and no shared write. Controls: from-inventory failure returns it.
        public static object BusinessPlannerRunCheck(string what)
        {
            bool on = ShortcutOn("businessplannerrun");
            Type use = typeof(AcademicBusinessPlanner.UseBusinessPlanner.BusinessPlanning);
            List<object> h = new List<object>(new object[] { "UseBusinessPlanner+Definition", sBpDef, typeof(InteractionDefinition), "mUsageType", sBpUsage, use,
                "mRunFromInventory", sBpFromInv, typeof(bool), "mWaitTime", sBpWait, typeof(float), "mCurrentBusinessPlanningType", sBpCur, use,
                "LocalizeString", sBpLoc, 2, "BroadcastCallback", sBpBroadcast, 2, "kPresentationBroadcastParams", sBpParams, typeof(ReactionBroadcasterParams) });
            for (int i = 0; i < kBpTunables.Length; i++) { h.Add(kBpTunables[i]); h.Add(sBpTunables[i]); h.Add(typeof(float)); }
            return Ep4Check("businessplannerrun", 3256, new[] { "runs fixed", "shared roll or pocketing with switch off", "errors" },
                EpJoin(EpMiss("UseBusinessPlanner+Definition.mUsageType", sBpUsage, "mRunFromInventory", sBpFromInv, "mWaitTime", sBpWait, "mCurrentBusinessPlanningType", sBpCur, "LocalizeString", sBpLoc, "BroadcastCallback", sBpBroadcast, "kPresentationBroadcastParams", sBpParams),
                    EpLive(typeof(BEp4BpHook), 1, h.ToArray())), what,
                "no pocket, no shared write", !BpPockets(on, false) && !BpWritesShared(on, true, true), "own planner returned, set use kept", BpPockets(on, true) && !BpWritesShared(on, true, false));
        }

        // FutureDescendantSize0Check run: size 0 does not instantiate, a stuck loop stops. Controls: size 2, a loop that moves.
        public static object FutureDescendantSize0Check(string what)
        {
            bool on = ShortcutOn("futuredescendantsize0");
            return Ep4Check("futuredescendantsize0", 3259, new[] { "unpacks / renames / stuck loops skipped", "seen with switch off", "errors" },
                EpJoin(EpMiss("FutureDescendantService.sPersistableData", sFdData, "ActiveDescendantHouseholdsInfo", sFdInfos),
                    EpJoin(EpLive(typeof(BEp4FdInfoHook), 1), EpLive(typeof(BEp4FdHook), 1, "sPersistableData", sFdData, "static", "ActiveDescendantHouseholdsInfo", sFdInfos, typeof(System.Collections.IList)))), what,
                "size 0 skipped, stuck loop left", !FdInstantiates(on, 0) && !FdLoopGoesOn(on, 3, 3), "size 2 and a moving loop as EA", FdInstantiates(on, 2) && FdLoopGoesOn(on, 3, 2));
        }

        // JungleGymExitQueueCheck run: a gone Sim's entry is dropped. Controls: a Sim on the tower or with a pending alarm stays.
        public static object JungleGymExitQueueCheck(string what)
        {
            bool on = ShortcutOn("junglegymexitqueue");
            return Ep4Check("junglegymexitqueue", 3262, new[] { "stale entries dropped", "stale heads with switch off", "errors" },
                EpJoin(EpMiss("mExitQueue", sJgQueue, "kTimeToWaitForNextExit", sJgNext, "kTimeMaxToWait", sJgMax),
                    EpLive(typeof(BEp4JgHook), 2, "mExitQueue", sJgQueue, typeof(Queue<Sim>), "kTimeToWaitForNextExit", sJgNext, typeof(float), "kTimeMaxToWait", sJgMax, typeof(float))), what,
                "gone / off-tower entries dropped, empty-queue reset skipped", !JgKeep(on, false, false, false) && !JgKeep(on, true, false, false) && JgSafeSkips(on, 0) && JgGone(null),
                "on tower / alarm pending kept, queued reset as EA", JgKeep(on, true, true, false) && JgKeep(on, true, false, true) && !JgSafeSkips(on, 1));
        }

        // WildHorseLotCheck run: the big park (index 0) gets weight 2. Controls: index 1 and not preferred as EA.
        public static object WildHorseLotCheck(string what)
        {
            bool on = ShortcutOn("wildhorselot");
            float[] w = { 2f, 2f, 16f, 24f };
            return Ep4Check("wildhorselot", 3265, new[] { "picks with corrected weights", "big parks under-weighted with switch off", "errors" }, EpLive(typeof(BEp4WhHook), 1), what,
                "big park x2", WhMult(on, 0, w, 1f) == 2f, "beach x16, other x1", WhMult(on, 2, w, 1f) == 16f && WhMult(on, -1, w, 1f) == 1f);
        }

        // HarvesterUnripeCheck run: an unripe plant is not harvested. Control: a ripe one is.
        public static object HarvesterUnripeCheck(string what)
        {
            bool on = ShortcutOn("harvesterunripe");
            return Ep4Check("harvesterunripe", 3268, new[] { "unripe suctions skipped", "harvested unripe with switch off", "errors" }, EpLive(typeof(BEp4HuHook), 1), what,
                "unripe skipped", !HuHarvests(on, false), "ripe harvested", HuHarvests(on, true));
        }

        // HotTubSeatPostureCheck run: a pending sore alarm skips the re-init. Control: no pending alarm inits as EA.
        public static object HotTubSeatPostureCheck(string what)
        {
            bool on = ShortcutOn("hottubseatposture");
            return Ep4Check("hottubseatposture", 3271, new[] { "duplicate inits skipped", "duplicate alarms with switch off", "errors" },
                EpJoin(EpMiss("HotTubPosture.mFirstIdle", sHtFirst, "HotTubPosture.mSoreBuffTimer", sHtTimer),
                    EpLive(typeof(BEp4HtHook), 1, "HotTubPosture.mFirstIdle", sHtFirst, typeof(bool), "HotTubPosture.mSoreBuffTimer", sHtTimer, typeof(AlarmHandle))), what,
                "pending alarm, no re-init", !HtInits(on, true), "no alarm inits", HtInits(on, false));
        }

        // TeenPartyBreakUpCheck run: a gone party does not throw. Control: a running party.
        public static object TeenPartyBreakUpCheck(string what)
        {
            bool on = ShortcutOn("teenpartybreakup");
            return Ep4Check("teenpartybreakup", 3274, new[] { "runs past a missing party", "(not counted with switch off: EA routes first)", "errors" }, EpLive(typeof(BEp4TpHook), 1), what,
                "gone party, no throw", !TpThrows(on, true), "running party", !TpThrows(on, false));
        }

        // DogSwimAroundListCheck run: a swimming dog off the list is put back. Controls: on the list, left the pool.
        public static object DogSwimAroundListCheck(string what)
        {
            bool on = ShortcutOn("dogswimaroundlist");
            return Ep4Check("dogswimaroundlist", 3277, new[] { "dogs put back", "left off with switch off", "errors" },
                EpJoin(EpMiss("Sims3.Gameplay.Pools.DoggySwimAround", sDaType), EpLive(typeof(BEp4DaHook), 1, "DoggySwimAround", sDaType, typeof(InteractionInstance))), what,
                "dog back on the use list", DaRestore(on, true, false), "on list / out of pool untouched", !DaRestore(on, true, true) && !DaRestore(on, false, false));
        }

        // TutorBalloonAlarmCheck run: creating the interaction starts no alarm. Control: the members were found.
        public static object TutorBalloonAlarmCheck(string what)
        {
            bool on = ShortcutOn("tutorballoonalarm");
            string miss = EpJoin(EpMiss("TutorSkillInteraction.ShowBalloon", sTuShow, "kTimeToFirstBalloon", sTuFirst, "kTimeBetweenThoughtBalloons", sTuEvery),
                EpLive(typeof(BEp4TuHook), 2, "TutorSkillInteraction.ShowBalloon", sTuShow, 0, "kTimeToFirstBalloon", sTuFirst, typeof(float), "kTimeBetweenThoughtBalloons", sTuEvery, typeof(float)));
            return Ep4Check("tutorballoonalarm", 3280, new[] { "alarms moved to tutoring start", "started at creation with switch off", "errors" }, miss, what,
                "no alarm at creation", !TuAlarmAtCreate(on), "members and live path found", miss.Length == 0);
        }
    }
}
