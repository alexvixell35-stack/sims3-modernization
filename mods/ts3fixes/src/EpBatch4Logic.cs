using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay;
using Sims3.Gameplay.Academics;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Careers;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.EventSystem;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.Objects.TombObjects;
using Sims3.Gameplay.Pools;
using Sims3.Gameplay.Situations;
using Sims3.Gameplay.Skills;
using Sims3.Gameplay.ThoughtBalloons;
using Sims3.Gameplay.TimeTravel;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;
using Sims3.SimIFace.RouteDestinations;
using Sims3.UI;

namespace Ts3Fixes
{
    // EP batch 4a (docs/587 §9): the docs/587 §8 leads that needed copies of yielding methods, plus FutureDescendantService.
    // A copy of a yielding Run is as save-safe as any yielding wrapper (docs/242 §2): EA's Run methods are plain methods
    // that yield through Simulator.Sleep in the Sim's own task (no iterator), a save made inside a copy resumes in the
    // copy under the same build, and a save made inside EA's method is pointed at the stub (EA's IL) by savedstacks; the
    // one cost is a single reset of a Sim saved inside the copy when a later build changes the copy's IL. Every copy below
    // is EA's body with only the marked lines changed; the switch off runs EA's IL. Every switch off by default.
    // Checks: EpBatch4Checks.cs. Slots 1521-1535 (block fixes-1500).
    public static partial class Fixes
    {
        // ---- EP4 §1 dogswimslots (tuning kDogSwimSlots) ---------------------------------------------------------------
        // [verified, decompiled] DoggySwimHere.RouteActorToDestination (Pets): a hydrophobic dog sent to swim runs to the
        // pool edge and then, kNumberOfTimesForHydrophobicDogToRouteToOtherSlots (3-5) times, routes to
        // RandomUtil.GetRandomObjectFromList(list) of the unobstructed outside slots. With every slot obstructed the list is
        // empty and GetRandomObjectFromList throws ArgumentException out of the route (the dog's interaction dies with a
        // script error). Fix (copy): the reroute loop runs only when a slot was free. Household-visible (household dogs).
        // RE, not changed: the fallback branch dereferences poolNearestPoint (null only without a pool; the Definition needs
        // one).
        // Counters: [1521] reroute loops skipped on no free slot, [1522] would throw with the switch off, [1523] errors.
        static readonly FieldInfo sDsTimes = typeof(DoggySwimHere).GetField("kNumberOfTimesForHydrophobicDogToRouteToOtherSlots", kEpStatic);
        static readonly FieldInfo sDsChance = typeof(DoggySwimHere).GetField("kChanceToShowHydrophobicThoughtBalloon", kEpStatic);

        // Pure: does the hydrophobic reroute loop run?
        internal static bool DsLoopRuns(bool on, int freeSlots) { return !on || freeSlots > 0; }

        static void ApplyDogSwimSlots()
        {
            string miss = EpMiss("kNumberOfTimesForHydrophobicDogToRouteToOtherSlots", sDsTimes, "kChanceToShowHydrophobicThoughtBalloon", sDsChance);
            if (miss.Length > 0) { AppliedLog().Add("dogswimslots: not found: " + miss); return; }
            Patch(typeof(DoggySwimHere), "RouteActorToDestination", Type.EmptyTypes, typeof(BEp4DsHook), "RouteHook", "RouteOrig");
        }

        public abstract class BEp4DsHook : Terrain.GoHere
        {
            public bool RouteHook()
            {
                if (!ShortcutOn("dogswimslots") || !Actor.HasTrait(TraitNames.HydrophobicPet) || Actor.Posture is SwimmingInPool) return RouteOrig();
                int[] times; float chance;
                try { times = (int[])sDsTimes.GetValue(null); chance = (float)sDsChance.GetValue(null); }
                catch (Exception) { PatchCounters()[1523]++; return RouteOrig(); }
                // EA's body from here (the hydrophobic branch is known to run)
                Route route = Actor.CreateRoute();
                Pool poolNearestPoint = Pool.GetPoolNearestPoint(Destination);
                bool flag2 = false;
                RequestWalkStyle(Sim.WalkStyle.PetRun);
                List<Slot> list = new List<Slot>();
                foreach (Slot slot in poolNearestPoint.PoolPortalComponent.GetRoutingSlotsForGroup(PoolPortalComponent.RoutingSlotGroup.Outside))
                    if (Route.IsSlotUnobstructed(Actor.ObjectId, poolNearestPoint.ObjectId, slot, false, Route.RouteOption.None, ObjectGuid.InvalidObjectGuid, Actor.SimDescription.AgeGenderSpecies, false) == BlockedStatus.Unobstructed)
                        list.Add(slot);
                route.SetOption(Route.RouteOption.IgnoreFaceToDestinationTestForPets, true);
                if (route.PlanToSlot(poolNearestPoint, list.ToArray(), null, true).Succeeded())
                {
                    if (Actor.RouteToPoint(poolNearestPoint.GetPositionOfSlot((Slot)route.PlanResult.mDestSlotNameHash, Actor.SimDescription.AgeGenderSpecies, false)))
                    {
                        ThoughtBalloonManager.BalloonData b = new ThoughtBalloonManager.BalloonData("balloon_swimmingpool");
                        b.LowAxis = ThoughtBalloonAxis.kDislike;
                        Actor.ThoughtBalloonManager.ShowBalloon(b);
                    }
                    Actor.PlaySoloAnimation("ad_idle_stand_bark_x", true, ProductVersion.EP5);
                }
                int num = RandomUtil.GetInt(times[0], times[1]);
                if (!DsLoopRuns(true, list.Count)) { PatchCounters()[1521]++; num = 0; }  // changed: EA picks from an empty list and throws
                for (int j = 0; j < num; j++)
                {
                    Slot pick = RandomUtil.GetRandomObjectFromList(list);
                    if (route.PlanToSlot(poolNearestPoint, pick, true).Succeeded() && Actor.DoRoute(route))
                    {
                        if (RandomUtil.RandomChance01(chance))
                        {
                            ThoughtBalloonManager.BalloonData b2 = new ThoughtBalloonManager.BalloonData("balloon_swimmingpool");
                            b2.LowAxis = ThoughtBalloonAxis.kDislike;
                            Actor.ThoughtBalloonManager.ShowBalloon(b2);
                        }
                        Actor.PlaySoloAnimation("ad_idle_stand_bark_x", true, ProductVersion.EP5);
                    }
                }
                UnrequestWalkStyle(Sim.WalkStyle.PetRun);
                route = Actor.CreateRoute();
                route.SetOption(Route.RouteOption.DoNotEmitDegenerateRoutesForRadialRangeGoals, true);
                route.SetOption(Route.RouteOption.DoLineOfSightCheckUserOverride, false);
                route.SetOption(Route.RouteOption.CheckForFootprintsNearGoals, true);
                RadialRangeDestination rd = new RadialRangeDestination();
                rd.mCenterPoint = Destination;
                rd.mfMinRadius = 0f;
                rd.mfMaxRadius = 1f;
                rd.mfPreferredSpacing = 0.15f;
                rd.ScoreFunctionWeights[2] = 1f;
                route.AddDestination(rd);
                if (route.Plan().Succeeded())
                {
                    flag2 = Actor.DoRoute(route);
                    if (flag2 && Actor.SimDescription.IsGhost && !(Actor.Posture is SwimmingInPool) && poolNearestPoint != null) poolNearestPoint.DropIn(Actor);
                }
                else
                {
                    if (!(Actor.Posture is SwimmingInPool))
                    {
                        List<Slot> list2 = new List<Slot>();
                        foreach (Slot slot2 in poolNearestPoint.PoolPortalComponent.GetRoutingSlotsForGroup(PoolPortalComponent.RoutingSlotGroup.Outside))
                            if (Route.IsSlotUnobstructed(Actor.ObjectId, poolNearestPoint.ObjectId, slot2, false, Route.RouteOption.None, ObjectGuid.InvalidObjectGuid, Actor.SimDescription.AgeGenderSpecies, false) == BlockedStatus.Unobstructed)
                                list2.Add(slot2);
                        route = Actor.CreateRoute();
                        if (route.PlanToSlot(poolNearestPoint, list2.ToArray(), null, true).Succeeded())
                            Actor.RouteToPoint(poolNearestPoint.GetPositionOfSlot((Slot)route.PlanResult.mDestSlotNameHash, Actor.SimDescription.AgeGenderSpecies, false));
                    }
                    Actor.PlayRouteFailure(poolNearestPoint.GetThoughtBalloonThumbnailKey());
                }
                return flag2;
            }
            public bool RouteOrig() { return false; } // DoggySwimHere.RouteActorToDestination
        }

        // ---- EP4 §2 fragmentreconstruct (tuning kFragmentReconstruct) -------------------------------------------------
        // [verified, decompiled; how often the clicked fragment is not in the first N is inferred] FragmentedObject+
        // Reconstruct.RunFromInventory (World Adventures, Pets) picks the first RequiredNumberOfItems free fragments of the
        // type from the inventory, destroys the picked ones except the clicked Target, then destroys the Target too
        // (DestroyObject). When the Target is not among the first N picked (a stack of more than N), N+1 fragments are used.
        // RE: when fewer than N fragments are free (one in use since the Test), mToRemoveObj stays null and the trailing
        // mToRemoveObj.Clear() throws. Fix (copy): the Target is picked first, then N-1 others; the Clear is null-guarded.
        // Household-visible (a household Sim or dog reassembling fragments).
        // Counters: [1524] Target picked first (extra fragment kept), [1525] extra fragment used with the switch off,
        // [1526] errors.
        static readonly Type sFrType = typeof(FragmentedObject).GetNestedType("Reconstruct", BindingFlags.NonPublic | BindingFlags.Public);
        static readonly FieldInfo sFrRemove = sFrType == null ? null : sFrType.GetField("mToRemoveObj", kInstAll);
        static readonly FieldInfo sFrCreated = sFrType == null ? null : sFrType.GetField("mCreatedObject", kInstAll);
        static readonly PropertyInfo sFrInfo = typeof(FragmentedObject).GetProperty("FragmentInfo", kInstAll);
        static readonly MethodInfo sFrTest = typeof(FragmentedObject).GetMethod("FragmentInventoryTest", kInstAll, null, new Type[] { typeof(IGameObject), typeof(object) }, null);
        static readonly MethodInfo sFrLoc = typeof(FragmentedObject).GetMethod("LocalizeString", kEpStatic, null, new Type[] { typeof(string), typeof(object[]) }, null);

        // Pure: fragments used up for a stack of `free` with the Target at `targetIndex` (EA: N + 1 when it is not picked).
        internal static int FrUsed(bool on, int required, int free, int targetIndex)
        {
            if (free < required) return 0;
            return on || targetIndex < required ? required : required + 1;
        }

        static void ApplyFragmentReconstruct()
        {
            string miss = EpMiss("FragmentedObject+Reconstruct", sFrType, "mToRemoveObj", sFrRemove, "mCreatedObject", sFrCreated, "FragmentInfo", sFrInfo, "FragmentInventoryTest", sFrTest, "LocalizeString", sFrLoc);
            if (miss.Length > 0) { AppliedLog().Add("fragmentreconstruct: not found: " + miss); return; }
            Patch(sFrType, "RunFromInventory", Type.EmptyTypes, typeof(BEp4FrHook), "RunHook", "RunOrig");
        }

        public abstract class BEp4FrHook : Interaction<Sim, FragmentedObject>
        {
            public bool RunHook()
            {
                if (!ShortcutOn("fragmentreconstruct")) { FrCountOff(); return RunOrig(); }
                object info; ItemTestFunction test;
                try { info = sFrInfo.GetValue(Target, null); test = (ItemTestFunction)Delegate.CreateDelegate(typeof(ItemTestFunction), Target, sFrTest); }
                catch (Exception) { PatchCounters()[1526]++; return RunOrig(); }
                int required = (int)info.GetType().GetField("RequiredNumberOfItems").GetValue(info);
                float dropDistance = (float)info.GetType().GetField("RouteDropDistance").GetValue(info);
                // EA's body from here
                bool flag = false;
                StandardEntry();
                BeginCommodityUpdates();
                List<FragmentedObject> list = Actor.Inventory.FindAll<FragmentedObject>(true, test);
                List<FragmentedObject> toRemove = null;
                IGameObject created = null;
                if (list.Count >= required)
                {
                    toRemove = new List<FragmentedObject>();
                    int t = list.IndexOf(Target);
                    if (t > 0) { list.RemoveAt(t); list.Insert(0, Target); }  // changed: the clicked fragment is picked first
                    if (t >= required) PatchCounters()[1524]++;
                    foreach (FragmentedObject item in list)
                    {
                        if (!Actor.Inventory.GetInUse(item) && Actor.Inventory.SetInUse(item))
                        {
                            toRemove.Add(item);
                            if (toRemove.Count >= required) break;
                        }
                    }
                }
                sFrRemove.SetValue(this, toRemove);
                if (toRemove != null && toRemove.Count >= required)
                {
                    created = GlobalFunctions.CreateObjectOutOfWorld(Target.GetObjectToCreate(Actor), Target.GetProductVersion());
                    sFrCreated.SetValue(this, created);
                    created.SetHiddenFlags(HiddenFlags.Model);
                    created.AddToWorld();
                    if (GlobalFunctions.PlaceAtGoodLocation(created, new World.FindGoodLocationParams(Actor.Position), true))
                    {
                        if (Actor.RouteToObjectRadius(created, dropDistance))
                        {
                            foreach (FragmentedObject item2 in toRemove)
                            {
                                if (item2 != Target)
                                {
                                    Actor.Inventory.RemoveByForce(item2);
                                    item2.Destroy();
                                }
                            }
                            if (!Actor.IsPet)
                            {
                                EnterStateMachine("solo_generic", "Enter", "x");
                                SetParameter("AnimationName", "a_fragment_pieceTogether_x");
                                AddOneShotScriptEventHandler(1001u, FrSoundHandler());
                                AnimateSim("Play Animation");
                                AnimateSim("Exit");
                            }
                            else if (Actor.IsADogSpecies && Target.Huntable)
                            {
                                DogHuntingSkill dh = Actor.SkillManager.GetSkill<DogHuntingSkill>(SkillNames.DogHunting);
                                if (dh != null) dh.FragmentedObjectAssembled();
                            }
                            created.SetHiddenFlags(HiddenFlags.Nothing);
                            flag = true;
                        }
                    }
                    else
                    {
                        StyledNotification.Format format = new StyledNotification.Format((string)sFrLoc.Invoke(null, new object[] { "UnableToPlace", new object[0] }), Target.ObjectId, ObjectGuid.InvalidObjectGuid, StyledNotification.NotificationStyle.kGameMessageNegative);
                        StyledNotification.Show(format);
                    }
                }
                if (!flag)
                {
                    created = sFrCreated.GetValue(this) as IGameObject;
                    if (created != null) { created.Destroy(); sFrCreated.SetValue(this, null); }
                    if (toRemove != null) foreach (FragmentedObject item3 in toRemove) Actor.Inventory.SetNotInUse(item3);
                }
                else
                {
                    DestroyObject(Target);
                }
                if (toRemove != null) toRemove.Clear();  // changed: EA clears unconditionally (null when too few were free)
                sFrCreated.SetValue(this, null);
                EndCommodityUpdates(flag);
                StandardExit();
                return flag;
            }
            public bool RunOrig() { return false; } // FragmentedObject+Reconstruct.RunFromInventory

            SacsEventHandler FrSoundHandler()
            {
                MethodInfo m = sFrType.GetMethod("EventCallbackPlaySound", kInstAll);
                return (SacsEventHandler)Delegate.CreateDelegate(typeof(SacsEventHandler), this, m);
            }

            void FrCountOff()
            {
                try
                {
                    object info = sFrInfo.GetValue(Target, null);
                    int required = (int)info.GetType().GetField("RequiredNumberOfItems").GetValue(info);
                    List<FragmentedObject> list = Actor.Inventory.FindAll<FragmentedObject>(true, (ItemTestFunction)Delegate.CreateDelegate(typeof(ItemTestFunction), Target, sFrTest));
                    if (FrUsed(false, required, list.Count, list.IndexOf(Target)) > required) PatchCounters()[1525]++;
                }
                catch (Exception) { PatchCounters()[1526]++; }
            }
        }

        // ---- EP4 §3 labobjectpush (tuning kLabObjectPush) -------------------------------------------------------------
        // [verified, decompiled; intent inferred] AcademicLabSituation.PushLabSituationInteraction (University) collects the
        // lab lot's IAcademicLabObjects at start (mAcademicLabObjects) but adds them to the pick list only when the student
        // has a lab object in the inventory; a student with none gets nothing to do and idles through the lab. Fix: with an
        // empty inventory list, a live lab object of the lot is picked and EA's own push is made (same definition, callbacks
        // and priority). Household-visible (university students).
        // Counters: [1527] lot objects pushed, [1528] idle students with the switch off, [1529] errors.
        static readonly FieldInfo sLbObjects = typeof(AcademicLabSituation).GetField("mAcademicLabObjects", kInstAll);
        static readonly MethodInfo sLbDone = typeof(AcademicLabSituation).GetMethod("LabSituationFinished", kInstAll);
        static readonly MethodInfo sLbFail = typeof(AcademicLabSituation).GetMethod("LabSituationFailed", kInstAll);

        // Pure: does the student get a lot object?
        internal static bool LbUseLot(bool on, int inventory, int lotLive) { return on && inventory == 0 && lotLive > 0; }

        static void ApplyLabObjectPush()
        {
            string miss = EpMiss("mAcademicLabObjects", sLbObjects, "LabSituationFinished", sLbDone, "LabSituationFailed", sLbFail);
            if (miss.Length > 0) { AppliedLog().Add("labobjectpush: not found: " + miss); return; }
            Patch(typeof(AcademicLabSituation), "PushLabSituationInteraction", new Type[] { typeof(Sim) }, typeof(BEp4LbHook), "PushHook", "PushOrig");
        }

        public class BEp4LbHook
        {
            public void PushHook(Sim sim)
            {
                try
                {
                    AcademicLabSituation s = (AcademicLabSituation)(object)this;
                    if (Simulator.CheckYieldingContext(false) && sim != null && !sim.HasBeenDestroyed && sim.LotCurrent != null && s.Lot != null
                        && sim.LotCurrent.LotId == s.Lot.LotId && sim.Inventory != null)
                    {
                        List<IAcademicLabObject> inv = sim.Inventory.FindAll<IAcademicLabObject>(true, new ItemTestFunction(s.ItemTestFunction));
                        List<IAcademicLabObject> lot = new List<IAcademicLabObject>();
                        List<IAcademicLabObject> all = sLbObjects.GetValue(s) as List<IAcademicLabObject>;
                        if (all != null) foreach (IAcademicLabObject o in all) if (o != null && !o.HasBeenDestroyed && o.InWorld) lot.Add(o);
                        int n = inv == null ? 0 : inv.Count;
                        if (n == 0 && lot.Count > 0)
                        {
                            if (!ShortcutOn("labobjectpush")) PatchCounters()[1528]++;
                            else if (LbUseLot(true, n, lot.Count))
                            {
                                IAcademicLabObject pick = RandomUtil.GetRandomObjectFromList(lot);
                                InteractionDefinition def = pick.GetRandomLabInteractionForAssociatedDegree(s.Major);
                                if (def != null)
                                {
                                    Callback done = (Callback)Delegate.CreateDelegate(typeof(Callback), s, sLbDone);
                                    Callback fail = (Callback)Delegate.CreateDelegate(typeof(Callback), s, sLbFail);
                                    s.ForceSituationSpecificInteraction(pick, sim, def, null, done, fail, new InteractionPriority(InteractionPriorityLevel.UserDirected, 1f));
                                    PatchCounters()[1527]++;
                                }
                                return;
                            }
                        }
                    }
                }
                catch (Exception) { PatchCounters()[1529]++; }
                PushOrig(sim);
            }
            public void PushOrig(Sim sim) { } // AcademicLabSituation.PushLabSituationInteraction
        }

        // ---- EP4 §4 businessplannerrun (tuning kBusinessPlannerRun) ---------------------------------------------------
        // [verified, decompiled] AcademicBusinessPlanner+UseBusinessPlanner.Run (University):
        // (a) an autonomous run with no chosen use writes a random use into definition.mUsageType, and that definition is
        //     the shared Singleton (autonomy and the lab push), so the first roll sticks for every later run of the save
        //     session: autonomous Sims always present, or always work;
        // (b) when WaitForTurn (another Sim is at the planner) or the route fails, it calls Actor.Inventory.TryToAdd(Target)
        //     even when the planner was not taken from this Sim's inventory, so a Sim who gave up waiting pockets the planner
        //     another Sim is using (CanAddToInventory has no in-use test).
        // Fix (copy): the roll is kept in a local (and the caption shows it); on those two failures the planner goes back
        // only when this run took it out of the inventory. The success path, which puts a closed planner in the
        // inventory, is EA's. Household-visible (university business students).
        // Counters: [1530] runs fixed (local roll or no pocketing), [1531] shared roll or pocketing with the switch off,
        // [1532] errors.
        static readonly Type sBpDef = typeof(AcademicBusinessPlanner.UseBusinessPlanner).GetNestedType("Definition", BindingFlags.NonPublic | BindingFlags.Public);
        static readonly FieldInfo sBpUsage = sBpDef == null ? null : sBpDef.GetField("mUsageType", kInstAll);
        static readonly FieldInfo sBpFromInv = typeof(AcademicBusinessPlanner.UseBusinessPlanner).GetField("mRunFromInventory", kInstAll);
        static readonly FieldInfo sBpWait = typeof(AcademicBusinessPlanner.UseBusinessPlanner).GetField("mWaitTime", kInstAll);
        static readonly FieldInfo sBpCur = typeof(AcademicBusinessPlanner).GetField("mCurrentBusinessPlanningType", kInstAll);
        static readonly MethodInfo sBpLoc = typeof(AcademicBusinessPlanner.UseBusinessPlanner).GetMethod("LocalizeString", kEpStatic, null, new Type[] { typeof(string), typeof(object[]) }, null);
        static readonly MethodInfo sBpBroadcast = typeof(AcademicBusinessPlanner.UseBusinessPlanner).GetMethod("BroadcastCallback", kInstAll);
        static readonly FieldInfo sBpParams = typeof(AcademicBusinessPlanner.UseBusinessPlanner).GetField("kPresentationBroadcastParams", kEpStatic);
        static readonly string[] kBpTunables = { "kMinWorkLength", "kMaxWorkLength", "kXPIncreasePerWorking", "kMinPresentationLength", "kMaxPresentationLength", "kXPIncreasePerPresentation" };

        // Pure: is the planner put in the actor's inventory after a failed wait or route? Does the roll change the definition?
        internal static bool BpPockets(bool on, bool fromInventory) { return !on || fromInventory; }
        internal static bool BpWritesShared(bool on, bool autonomous, bool unset) { return !on && autonomous && unset; }

        static float BpTune(int i) { return (float)typeof(AcademicBusinessPlanner.UseBusinessPlanner).GetField(kBpTunables[i], kEpStatic).GetValue(null); }

        static void ApplyBusinessPlannerRun()
        {
            string miss = EpMiss("UseBusinessPlanner+Definition.mUsageType", sBpUsage, "mRunFromInventory", sBpFromInv, "mWaitTime", sBpWait, "mCurrentBusinessPlanningType", sBpCur,
                "LocalizeString", sBpLoc, "BroadcastCallback", sBpBroadcast, "kPresentationBroadcastParams", sBpParams);
            foreach (string k in kBpTunables) if (typeof(AcademicBusinessPlanner.UseBusinessPlanner).GetField(k, kEpStatic) == null) miss += (miss.Length > 0 ? ", " : "") + k;
            if (miss.Length > 0) { AppliedLog().Add("businessplannerrun: not found: " + miss); return; }
            Patch(typeof(AcademicBusinessPlanner.UseBusinessPlanner), "Run", Type.EmptyTypes, typeof(BEp4BpHook), "RunHook", "RunOrig");
        }

        public abstract class BEp4BpHook : Interaction<Sim, AcademicBusinessPlanner>
        {
            public bool RunHook()
            {
                object def = InteractionDefinition;
                if (def == null || !sBpDef.IsInstanceOfType(def)) return RunOrig();
                if (!ShortcutOn("businessplannerrun")) { BpCountOff(def); return RunOrig(); }
                const AcademicBusinessPlanner.UseBusinessPlanner.BusinessPlanning None = AcademicBusinessPlanner.UseBusinessPlanner.BusinessPlanning.None;
                const AcademicBusinessPlanner.UseBusinessPlanner.BusinessPlanning Work = AcademicBusinessPlanner.UseBusinessPlanner.BusinessPlanning.WorkOnBusinessPlan;
                const AcademicBusinessPlanner.UseBusinessPlanner.BusinessPlanning Present = AcademicBusinessPlanner.UseBusinessPlanner.BusinessPlanning.PresentBusinessPlan;
                AcademicBusinessPlanner.UseBusinessPlanner.BusinessPlanning usage = (AcademicBusinessPlanner.UseBusinessPlanner.BusinessPlanning)sBpUsage.GetValue(def);
                bool fromInv = (bool)sBpFromInv.GetValue(this);
                if (Autonomous && usage == None) { usage = (AcademicBusinessPlanner.UseBusinessPlanner.BusinessPlanning)RandomUtil.GetInt(0, 1); PatchCounters()[1530]++; }  // changed: local roll
                string caption = usage == Present ? "PresentBusinessPlan" : usage == Work ? "WorkOnBusinessPlan" : "StartPlanning";
                ((IInteractionNameCanBeOverriden)(object)this).SetInteractionName((string)sBpLoc.Invoke(null, new object[] { caption, new object[0] }));
                UpdateCaption = true;
                // EA's body from here
                if (fromInv && Actor.Inventory.Contains(Target))
                {
                    Vector3 pos, fwd;
                    if (!GlobalFunctions.FindGoodLocation(Target, new World.FindGoodLocationParams(Actor.Position, FindGoodLocationBooleans.PreferEmptyTiles | FindGoodLocationBooleans.Routable), out pos, out fwd)) return false;
                    Actor.Inventory.TryToRemove(Target);
                    Target.SetPosition(pos);
                    Target.SetForward(fwd);
                    Target.AddToWorld();
                    Target.SetOpacity(0f, 0f);
                }
                if (!Target.Line.WaitForTurn(this, SimQueue.WaitBehavior.CutAheadOfLowerPrioritySims, ExitReason.Default, AcademicBusinessPlanner.kTimeToWaitInLine))
                {
                    BpFailAdd(fromInv);  // changed: EA always TryToAdd
                    return false;
                }
                if (!Actor.RouteToSlotAndCheckInUse(Target, Slot.RoutingSlot_0))
                {
                    BpFailAdd(fromInv);  // changed: EA always TryToAdd
                    return false;
                }
                StandardEntry();
                Target.EnableFootprintAndPushSims(AcademicBusinessPlanner.sFootprintHash, Actor);
                if (fromInv)
                {
                    Actor.PlaySoloAnimation("a2o_object_genericSwipe_x", true);
                    Target.SetOpacityTransition(0f, 1f, 1f);
                }
                AcquireStateMachine("BusinessPlanner");
                SetActor("x", Actor);
                SetActor("planner", Target);
                List<AcademicDegreeNames> list = new List<AcademicDegreeNames>();
                list.Add(AcademicDegreeNames.Business);
                BeginCommodityUpdatesAccountingForAcademicPerformance(list);
                sBpCur.SetValue(Target, usage);
                mCurrentStateMachine.EnterState("x", "Enter");
                if (!Target.PlannerIsOpen)
                {
                    AnimateSim("Open");
                    Target.PlannerIsOpen = true;
                }
                float num;
                ReactionBroadcaster rb = null;
                float wait;
                if (usage == Work)
                {
                    AnimateSim("Work");
                    wait = RandomUtil.GetFloat(BpTune(0), BpTune(1));
                    num = BpTune(2);
                }
                else
                {
                    AnimateSim("Practice");
                    wait = RandomUtil.GetFloat(BpTune(3), BpTune(4));
                    num = BpTune(5);
                    rb = new ReactionBroadcaster(Target, (ReactionBroadcasterParams)sBpParams.GetValue(null),
                        (ReactionBroadcaster.BroadcastCallback)Delegate.CreateDelegate(typeof(ReactionBroadcaster.BroadcastCallback), this, sBpBroadcast));
                }
                sBpWait.SetValue(this, wait);
                DoTimedLoop(wait, ExitReason.Default);
                bool other = false;
                foreach (InteractionInstance ii in Actor.InteractionQueue.InteractionList)
                    if (ii != (object)this && ii is AcademicBusinessPlanner.UseBusinessPlanner) { other = true; break; }
                if (!other && Target.PlannerIsOpen)
                {
                    AnimateSim("Close");
                    Target.PlannerIsOpen = false;
                }
                AnimateSim("Exit");
                Target.DisableFootprint(AcademicBusinessPlanner.sFootprintHash);
                EndCommodityUpdates(true);
                StandardExit();
                if (!Actor.HasExitReason(ExitReason.Canceled))
                {
                    if (usage == Present) EventTracker.SendEvent(EventTypeId.kPresentedBusinessPlan, Actor, Target.LotCurrent);
                    else if (usage == Work) EventTracker.SendEvent(EventTypeId.kWorkedOnBusinessPlan, Actor, Target.LotCurrent);
                }
                if (rb != null) { rb.EndBroadcast(); rb.Dispose(); rb = null; }
                if (!GameUtils.IsUniversityWorld())
                {
                    Sims3.Gameplay.Careers.Business business = Actor.OccupationAsCareer as Sims3.Gameplay.Careers.Business;
                    if (business != null) business.UpdatePerformanceOrExperience(num);
                }
                else
                {
                    AcademicCareer ac = Actor.OccupationAsAcademicCareer;
                    if (ac != null && ac.DegreeInformation != null && ac.DegreeInformation.AcademicDegreeName == AcademicDegreeNames.Business) ac.UpdatePerformanceOrExperience(num);
                }
                if (!Target.PlannerIsOpen)
                {
                    Actor.WaitForExitReason(2f, ExitReason.Default);
                    if (!Actor.Inventory.Contains(Target))
                    {
                        Actor.PlaySoloAnimation("a2o_object_genericSwipe_x", true);
                        Actor.Inventory.TryToAdd(Target);
                    }
                }
                return true;
            }
            public bool RunOrig() { return false; } // AcademicBusinessPlanner+UseBusinessPlanner.Run

            void BpFailAdd(bool fromInv)
            {
                if (BpPockets(true, fromInv)) Actor.Inventory.TryToAdd(Target);
                else PatchCounters()[1530]++;
            }

            void BpCountOff(object def)
            {
                try
                {
                    bool unset = (AcademicBusinessPlanner.UseBusinessPlanner.BusinessPlanning)sBpUsage.GetValue(def) == AcademicBusinessPlanner.UseBusinessPlanner.BusinessPlanning.None;
                    if (BpWritesShared(false, Autonomous, unset)) PatchCounters()[1531]++;
                }
                catch (Exception) { PatchCounters()[1532]++; }
            }
        }

        // ---- EP4 §5 futuredescendantsize0 (tuning kFutureDescendantSize0) ---------------------------------------------
        // [verified, decompiled + shipped kTraitsScoreToHouseholdSizeMapping] FutureDescendantService (Into the Future),
        // run at the future world load (PostFutureWorldLoadProcess, inside a try/catch):
        // (a) a descendant household whose desired size is 0 (trait score <= -2000 with shipped tuning, e.g. Commitment
        //     Issues + Dislikes Children) makes Instantiate unpack the progenitors into the tourist household and return null
        //     without packing them back (the pack-up is inside `if (num > 0)`), on every trip;
        // (b) ProcessDescendantHouseholds then dereferences DescendantHousehold (null) and SimDescription.Find(id) for a
        //     progenitor in Household.mDirtyNameSimIds (a name change, e.g. marriage): the NRE skips the rest of the load
        //     step for every household (lot lists, home lots, the "descendants located" notice, map tags, removals);
        // (c) RE: `while (NumMembers > desired) RemoveDescendant()` and `while (NumMembers < desired) CreateAndAddDescendant()`
        //     cannot exit when the call adds or removes nobody (ChooseSimToFadeFromTimeLine can return null; RemoveADescendant
        //     then returns true without removing), which hangs the load thread.
        // Fix: Instantiate does nothing for size 0 (EA's result, null, without the unpack); ProcessDescendantHouseholds
        // (copy, non-iterator, yields only inside EA's Instantiate) skips the dirty-name rename for a household that does
        // not exist and a progenitor not found, and leaves a resize loop that makes no progress. Load-safe: the copy writes
        // nothing EA would not write; it only skips the throwing statements and the stuck loop iterations. Household-visible.
        // Counters: [1533] size-0 unpacks skipped / null renames skipped / stuck loops left, [1534] seen with the switch off,
        // [1535] errors.
        static readonly FieldInfo sFdData = typeof(FutureDescendantService).GetField("sPersistableData", kEpStatic);

        // Pure: does Instantiate run? Does a resize loop continue after a call that changed the count from `before` to `after`?
        internal static bool FdInstantiates(bool on, int desired) { return !on || desired > 0; }
        internal static bool FdLoopGoesOn(bool on, int before, int after) { return !on || before != after; }

        static void ApplyFutureDescendantSize0()
        {
            if (sFdData == null) { AppliedLog().Add("futuredescendantsize0: not found: FutureDescendantService.sPersistableData"); return; }
            Patch(typeof(FutureDescendantService.FutureDescendantHouseholdInfo), "Instantiate", Type.EmptyTypes, typeof(BEp4FdInfoHook), "InstHook", "InstOrig");
            Patch(typeof(FutureDescendantService), "ProcessDescendantHouseholds", Type.EmptyTypes, typeof(BEp4FdHook), "ProcHook", "ProcOrig");
        }

        public class BEp4FdInfoHook
        {
            public Household InstHook()
            {
                FutureDescendantService.FutureDescendantHouseholdInfo info = (FutureDescendantService.FutureDescendantHouseholdInfo)(object)this;
                if (info.mCurrentDesiredHouseholdSize <= 0)
                {
                    if (!ShortcutOn("futuredescendantsize0")) PatchCounters()[1534]++;
                    else if (!FdInstantiates(true, info.mCurrentDesiredHouseholdSize)) { PatchCounters()[1533]++; return null; }
                }
                return InstOrig();
            }
            public Household InstOrig() { return null; } // FutureDescendantService+FutureDescendantHouseholdInfo.Instantiate
        }

        public class BEp4FdHook
        {
            public bool ProcHook()
            {
                if (!ShortcutOn("futuredescendantsize0")) return ProcOrig();
                IList infos;
                try
                {
                    object data = sFdData.GetValue(null);
                    infos = data == null ? null : data.GetType().GetField("ActiveDescendantHouseholdsInfo").GetValue(data) as IList;
                }
                catch (Exception) { PatchCounters()[1535]++; return ProcOrig(); }
                if (infos == null) return ProcOrig();
                // EA's body from here
                for (int i = 0; i < infos.Count; i++)
                {
                    FutureDescendantService.FutureDescendantHouseholdInfo info = (FutureDescendantService.FutureDescendantHouseholdInfo)infos[i];
                    if (info == null) { PatchCounters()[1533]++; continue; }  // changed: EA dereferences
                    Household hh = info.DescendantHousehold;
                    if (hh == null) info.Instantiate();
                    else
                    {
                        while (hh.NumMembers > info.mCurrentDesiredHouseholdSize)
                        {
                            int before = hh.NumMembers;
                            info.RemoveDescendant();
                            if (!FdLoopGoesOn(true, before, hh.NumMembers)) { PatchCounters()[1533]++; break; }  // changed: EA loops forever
                        }
                        while (hh.NumMembers < info.mCurrentDesiredHouseholdSize)
                        {
                            int before = hh.NumMembers;
                            info.CreateAndAddDescendant();
                            if (!FdLoopGoesOn(true, before, hh.NumMembers)) { PatchCounters()[1533]++; break; }  // changed: EA loops forever
                        }
                    }
                    foreach (ulong id in Household.mDirtyNameSimIds)
                    {
                        if (!info.IsSimAProgenitor(id)) continue;
                        SimDescription sd = SimDescription.Find(id);
                        Household dh = info.DescendantHousehold;
                        if (sd == null || dh == null) { PatchCounters()[1533]++; continue; }  // changed: EA dereferences both
                        foreach (SimDescription d in dh.SimDescriptions) d.LastName = sd.LastName;
                    }
                }
                Household.ClearDirtyNameSimIDs();
                return false;
            }
            public bool ProcOrig() { return false; } // FutureDescendantService.ProcessDescendantHouseholds
        }
    }
}
