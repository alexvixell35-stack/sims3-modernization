using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.ActiveCareer.ActiveCareers;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.Careers;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Objects.CookingObjects;
using Sims3.Gameplay.Objects.Vehicles;
using Sims3.Gameplay.Situations;
using Sims3.Gameplay.Socializing;
using Sims3.Gameplay.TuningValues;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // Batch 11 (docs/589 usage sweep, U1-U5): EA bug fixes in the code Sims use most. All default off. These paths are
    // shared by played Sims and NPCs, so every fix here reaches the household (the user: "as EA intended, household
    // included"). simler90 UPDATE242 changes EatHeldFood.Run and some SocialInteractionA tests, none of the hooked
    // methods (EatHeldFood.Cleanup, Food.PreEat/PostEat, Motive.UpdateMotiveWithDecay, SocialInteractionA.Cleanup,
    // ReactionInteraction.OneShotReaction, the carpool pickup Run are the same in both builds).
    public static partial class Fixes
    {
        const BindingFlags kB11All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        // ---- B11 §1 eatpostonce (U1) ------------------------------------------------------------------------------------
        // [verified, decompiled] EatHeldFood.Run calls Food.PostEat at its end (no return between PreEat and that call),
        // and Cleanup calls it again unguarded: kEatIEdible (wishes, opportunities) is sent twice, Stuffed / Nauseous are
        // added twice and the food-truck GreasyFood / FoodPoisoning chance is rolled twice. A queued eat cancelled before
        // Run still posts an eat in Cleanup. EA's Cleanup call is the safety net for an eat that began (PreEat) and was cut
        // off (reset, exception) before Run's own PostEat. Fix: Food.PreEat marks the Sim's eat pending, Food.PostEat clears
        // it; Cleanup lets its PostEat through only while that eat is still pending. The other PreEat / PostEat pairs
        // (picnic basket, restaurants, eating contest) only clear the mark. Counters: [2986] Cleanup posts skipped,
        // [2987] doubled posts (switch off), [2988] cut-off eats posted by Cleanup.
        static readonly Dictionary<Sim, GameObject> sEpPending = new Dictionary<Sim, GameObject>();
        static readonly Dictionary<Sim, bool> sEpSkip = new Dictionary<Sim, bool>();

        static void ApplyEatPostOnce()
        {
            Type rb = typeof(bool).MakeByRefType();
            Patch(typeof(Food), "PreEat", new Type[] { typeof(Sim), typeof(GameObject), rb, rb }, typeof(BEpHook), "PreHook", "PreOrig");
            Patch(typeof(Food), "PostEat", new Type[] { typeof(Sim), typeof(GameObject), typeof(bool), typeof(bool), typeof(bool) }, typeof(BEpHook), "PostHook", "PostOrig");
            Patch(typeof(EatHeldFood), "Cleanup", Type.EmptyTypes, typeof(BEpHook), "CleanupHook", "CleanupOrig");
        }

        // Pure: does EatHeldFood.Cleanup post the eat? EA always; the fix only for an eat begun and not yet posted.
        internal static bool EpCleanupPosts(bool on, bool pending) { return !on || pending; }

        public class BEpHook
        {
            public static void PreHook(Sim actor, GameObject target, ref bool full, ref bool fat)
            {
                PreOrig(actor, target, ref full, ref fat);
                if (actor != null) sEpPending[actor] = target;
            }

            public static void PreOrig(Sim actor, GameObject target, ref bool full, ref bool fat) { }

            public static void PostHook(Sim actor, GameObject target, bool full, bool bite, bool fat)
            {
                if (actor != null)
                {
                    if (sEpSkip.Remove(actor)) { PatchCounters()[2986]++; return; }
                    sEpPending.Remove(actor);
                }
                PostOrig(actor, target, full, bite, fat);
            }

            public static void PostOrig(Sim actor, GameObject target, bool full, bool bite, bool fat) { }

            public void CleanupHook()
            {
                Sim a = null;
                GameObject t = null;
                try
                {
                    EatHeldFood e = (EatHeldFood)(object)this;
                    a = e.Actor;
                    t = e.Target as GameObject;
                    if (a != null)
                    {
                        GameObject p;
                        bool pending = sEpPending.TryGetValue(a, out p) && p == t;
                        bool on = ShortcutOn("eatpostonce");
                        if (!EpCleanupPosts(on, pending)) sEpSkip[a] = true;
                        else if (pending) PatchCounters()[2988]++;
                        else PatchCounters()[2987]++;
                    }
                }
                catch (Exception) { }
                try { CleanupOrig(); }
                finally
                {
                    if (a != null)
                    {
                        sEpSkip.Remove(a);
                        GameObject p;
                        if (sEpPending.TryGetValue(a, out p) && p == t) sEpPending.Remove(a);
                    }
                }
            }

            public void CleanupOrig() { }
        }

        // World quit: batch 11's per-Sim and per-object state (the transient Cleanup marks clear in their finally blocks;
        // a Sim whose eat never posted, a mood id or a photo note would otherwise outlive the world).
        internal static void Batch589WorldQuit()
        {
            sEpPending.Clear(); sEpSkip.Clear(); sMtMay.Clear(); sBcKeep.Clear(); sTwSkip.Clear(); sSuKeep.Clear();
            sPhCost = 0; sPhSim = null; sLtForced = null;
        }

        // ---- B11 §2 motivemultonce (U2) ---------------------------------------------------------------------------------
        // [verified, decompiled] Motive.UpdateMotiveWithDecay applies the Carefree fun-gain multiplier (x1.25) to the
        // positive pre-loop fun sources, then, inside the loop over the current interaction's motive deltas, applies the
        // Carefree / LaidbackLemon (fun loss x0.25) / EfficientChip (battery gain x1.2) multiplier to the whole running
        // total once per matching delta, enabled or not: the pre-loop part is multiplied twice and every extra matching
        // delta multiplies everything again. Fix: for those Sims and commodities with a matching delta our copy sums the
        // pre-loop sources and the deltas (EA's DecayToZero clamps kept) and applies each multiplier once to the total
        // (EA's sign tests); the riding benefit is added after, unmultiplied, as EA. With a matching delta EA never decays
        // the motive, so the rest of the method is only its clamp. Every other Sim, commodity and horses run EA's code.
        // Counters: [2989] updates with the multiplier applied once, [2990] stacked updates (switch off), [2991] errors.
        static readonly FieldInfo sMmRelax = typeof(Motive).GetField("kRelaxingDestressAmount", kB11All);
        static readonly FieldInfo sMmSwim = typeof(Motive).GetField("kSwimmingInPoolFunAmount", kB11All);
        static readonly FieldInfo sMmSoothe = typeof(Motive).GetField("kSoothingSceneryFunAmount", kB11All);
        static readonly FieldInfo sMmSootheMax = typeof(Motive).GetField("kSoothingSceneryFunGainDisablePoint", kB11All);
        static string sMmMissing = "";

        static void ApplyMotiveMultOnce()
        {
            sMmMissing = SpNames("Motive.kRelaxingDestressAmount", sMmRelax, "Motive.kSwimmingInPoolFunAmount", sMmSwim, "Motive.kSoothingSceneryFunAmount", sMmSoothe, "Motive.kSoothingSceneryFunGainDisablePoint", sMmSootheMax);
            if (sMmMissing.Length > 0) { AppliedLog().Add("motivemultonce: not found: " + sMmMissing); return; }
            Patch(typeof(Motive), "UpdateMotiveWithDecay", new Type[] { typeof(float), typeof(float), typeof(Sim) }, typeof(BMmHook), "DecayHook", "DecayOrig");
        }

        // Pure: the multiplier step after the deltas, applied once to the total (EA's sign tests).
        internal static float MmApplyOnce(float num, bool fun, bool carefree, bool lemon, bool chip, float mCare, float mLemon, float mChip)
        {
            if (fun)
            {
                if (num > 0f && carefree) return num * mCare;
                if (num < 0f && lemon) return num * mLemon;
                return num;
            }
            return (num > 0f && chip) ? num * mChip : num;
        }

        // Pure: EA's in-loop stacking, for the check: per matching delta add it, then multiply the running total.
        internal static float MmEaStacked(float pre, float[] deltas, float m)
        {
            float num = pre > 0f ? pre * m : pre;
            for (int i = 0; i < deltas.Length; i++) { num += deltas[i]; if (num > 0f) num *= m; }
            return num;
        }

        public class BMmHook
        {
            public float DecayHook(float hoursPassed, float currentHours24, Sim sim)
            {
                Motive m = (Motive)(object)this;
                CommodityKind c = m.Commodity;
                if ((c != CommodityKind.Fun && c != CommodityKind.BatteryPower) || sim == null || sim.IsHorse) return DecayOrig(hoursPassed, currentHours24, sim);
                bool fun = c == CommodityKind.Fun;
                bool carefree = false, lemon = false, chip = false;
                if (fun) { carefree = sim.HasTrait(TraitNames.Carefree); lemon = sim.BuffManager.HasElement(BuffNames.LaidbackLemon); }
                else chip = sim.HasTrait(TraitNames.EfficientChip);
                if (!carefree && !lemon && !chip) return DecayOrig(hoursPassed, currentHours24, sim);
                InteractionInstance cur = sim.CurrentInteraction;
                List<MotiveDelta> deltas = cur != null ? cur.MotiveDeltas : null;
                bool matched = false;
                if (deltas != null) foreach (MotiveDelta d in deltas) if (d.Motive == c) { matched = true; break; }
                if (!matched) return DecayOrig(hoursPassed, currentHours24, sim);
                if (!ShortcutOn("motivemultonce")) { PatchCounters()[2990]++; return DecayOrig(hoursPassed, currentHours24, sim); }
                try
                {
                    float v = MmOnce(m, c, hoursPassed, sim, deltas, carefree, lemon, chip);
                    PatchCounters()[2989]++;
                    return v;
                }
                catch (Exception) { PatchCounters()[2991]++; return DecayOrig(hoursPassed, currentHours24, sim); }
            }

            public float DecayOrig(float hoursPassed, float currentHours24, Sim sim) { return 0f; }

            // EA's UpdateMotiveWithDecay for Fun / BatteryPower with a matching delta (no FrostyFace, no temperature caps:
            // those are Temperature only), the per-delta multipliers moved after the loop.
            static float MmOnce(Motive m, CommodityKind c, float hoursPassed, Sim sim, List<MotiveDelta> deltas, bool carefree, bool lemon, bool chip)
            {
                float currentValue = m.Value;
                float num = 0f;
                if (sim.Posture != null) num += sim.Posture.GetPostureMotiveDecayUpdate(currentValue, c, hoursPassed);
                if (DaycareSituation.GetDaycareSituationForChild(sim) != null)
                {
                    Daycare dc = Daycare.GetDaycareActiveCareer();
                    if (dc != null) num += dc.GetMotiveDecayUpdate(currentValue, c, hoursPassed, sim);
                }
                BuffManager bm = sim.BuffManager;
                if (c == CommodityKind.Fun)
                {
                    if (sim.Occupation != null && bm.HasElement(BuffNames.MissingWork)) num += (float)Career.WorkaholicTraitMissingWorkStressPerHour * hoursPassed;
                    if (bm.HasElement(BuffNames.TooManyPeople)) num += BuffTooManyPeople.TooManyPeopleFunPenalty * hoursPassed;
                    if (bm.HasElement(BuffNames.GoingWild)) num += BuffGoingWild.kFunMultiplier * hoursPassed;
                    if (bm.HasElement(BuffNames.CryingBaby)) num += BuffCryingBaby.StressPerHour * hoursPassed;
                    if (sim.Posture != null && currentValue < 0f && (sim.Posture.Satisfies(CommodityKind.Relaxing, sim.Posture.Container) || sim.Posture.Satisfies(CommodityKind.Lounging, sim.Posture.Container)))
                        num += (float)sMmRelax.GetValue(null) * hoursPassed;
                    if (sim.Posture != null && sim.Posture.Satisfies(CommodityKind.SwimmingInPool, sim.Posture.Container))
                    {
                        float swim = (float)sMmSwim.GetValue(null) * hoursPassed;
                        num = sim.HasTrait(TraitNames.Hydrophobic) ? num - swim : num + swim;
                    }
                    if (bm.HasElement(BuffNames.SoothingScenery) && m.Value < (float)sMmSootheMax.GetValue(null)) num += (float)sMmSoothe.GetValue(null) * hoursPassed;
                    // EA multiplied the positive pre-loop sources here and again per delta; the one multiply is after the loop.
                }
                else if (bm.HasElement(BuffNames.AbsorbingSunlight)) num += TraitChip.kAbsorbingSunlightBatteryPowerMotivePerHour * hoursPassed;
                bool toZero = m.Tuning.DecayToZero;
                foreach (MotiveDelta d in deltas)
                {
                    if (d.Motive != c || !d.Enabled) continue;
                    if (d.EnabledAboveZero && currentValue > 0f)
                    {
                        num += d.ChangePerHour * d.Multiplier * hoursPassed;
                        if (toZero && !d.EnabledBelowZero) num = Math.Max(num, 0f - currentValue);
                    }
                    else if (d.EnabledBelowZero && currentValue <= 0f)
                    {
                        num += d.ChangePerHour * d.Multiplier * hoursPassed;
                        if (toZero && !d.EnabledAboveZero) num = Math.Min(num, 0f - currentValue);
                    }
                }
                num = MmApplyOnce(num, c == CommodityKind.Fun, carefree, lemon, chip, TraitTuning.CarefreeFunGainMultiplier, BuffLaidbackLemon.LaidbackLemonFunLossMultiplier, TraitTuning.EfficientChipBatteryPowerGainMultiplier);
                if (sim.RidingPosture != null)
                {
                    for (int j = 0; j < BeingRiddenPosture.kBenefitSimCommoditiesToUpdate.Length; j++)
                        if (BeingRiddenPosture.kBenefitSimCommoditiesToUpdate[j] == c) { num += BeingRiddenPosture.kBenefitSimCommoditiesToUpdateDeltas[j]; break; }
                }
                currentValue += num;
                return MathUtils.Clamp(currentValue, m.Tuning.Min, m.Tuning.Max);
            }
        }

        // ---- B11 §3 fightbroadcastend (U3) ------------------------------------------------------------------------------
        // [verified, decompiled] DecideWinnerOfFight starts the fight's ReactionBroadcaster on the SocialInteractionA and
        // only AfterFight disposes it. A fight that is cancelled, fails, loses its target or is reset never reaches
        // AfterFight: the broadcaster (no end time) keeps making nearby Sims react and wakes sleepers. Fix: after EA's
        // SocialInteractionA.Cleanup a broadcaster still set is disposed and cleared (the interaction is over; EA's own
        // cleanup callbacks, such as the sketch's, run first). Counters: [2992] broadcasters ended at Cleanup,
        // [2993] left running (switch off), [2994] errors.
        static void ApplyFightBroadcastEnd()
        {
            Patch(typeof(SocialInteractionA), "Cleanup", Type.EmptyTypes, typeof(BFbHook), "CleanupHook", "CleanupOrig");
        }

        public class BFbHook
        {
            public void CleanupHook()
            {
                CleanupOrig();
                try
                {
                    SocialInteractionA s = (SocialInteractionA)(object)this;
                    if (s.SocialBroadcaster == null) return;
                    if (!ShortcutOn("fightbroadcastend")) { PatchCounters()[2993]++; return; }
                    ReactionBroadcaster b = s.SocialBroadcaster;
                    s.SocialBroadcaster = null;
                    b.Dispose();
                    PatchCounters()[2992]++;
                }
                catch (Exception) { PatchCounters()[2994]++; }
            }

            public void CleanupOrig() { }
        }

        // ---- B11 §4 reactioncopy (U4) -----------------------------------------------------------------------------------
        // [verified, decompiled] ReactionInteraction.OneShotReaction hands its reaction parameter by ref to ReactionByTrait
        // for each nearby Sim, so the first Sim's trait reaction (Coward, Evil, PointLaugh, ...) replaces the caller's
        // reaction for every later Sim in the loop. Fix: our copy of the loop gives each Sim its own copy of the caller's
        // reaction; the rest is EA's. With the switch off the copy carries the reaction over exactly as EA. Counters:
        // [2995] Sims given the caller's reaction back, [2996] Sims given an earlier Sim's trait reaction (switch off),
        // [2997] errors.
        delegate void RcByTraitFn(Sim sim, Sim target, bool isNegative, ref ReactionTypes reaction);
        static RcByTraitFn sRcByTrait;
        static Sim.PlayReactionCallback sRcBefore;
        static string sRcoMissing = "";

        static void ApplyReactionCopy()
        {
            MethodInfo bt = typeof(ReactionInteraction).GetMethod("ReactionByTrait", kB11All);
            MethodInfo bf = typeof(ReactionInteraction).GetMethod("BeforeReactionCallback", kB11All);
            sRcoMissing = SpNames("ReactionInteraction.ReactionByTrait", bt, "ReactionInteraction.BeforeReactionCallback", bf);
            if (sRcoMissing.Length > 0) { AppliedLog().Add("reactioncopy: not found: " + sRcoMissing); return; }
            sRcByTrait = (RcByTraitFn)Delegate.CreateDelegate(typeof(RcByTraitFn), bt);
            sRcBefore = (Sim.PlayReactionCallback)Delegate.CreateDelegate(typeof(Sim.PlayReactionCallback), bf);
            Patch(typeof(ReactionInteraction), "OneShotReaction", new Type[] { typeof(GameObject), typeof(float), typeof(ReactionTypes), typeof(ReactionSpeed), typeof(bool), typeof(List<Sim>), typeof(string), typeof(InteractionPriorityLevel) }, typeof(BRcHook), "ShotHook", "ShotOrig");
        }

        // Pure: the reaction a Sim starts from: the caller's (fix) or whatever the previous Sim left (EA).
        internal static ReactionTypes RcStart(bool on, ReactionTypes caller, ReactionTypes carried) { return on ? caller : carried; }

        public class BRcHook
        {
            public static void ShotHook(GameObject obj, float distance, ReactionTypes reaction, ReactionSpeed reactionSpeed, bool isNegative, List<Sim> ignoredSims, string thoughtBalloon, InteractionPriorityLevel priorityLevel)
            {
                Sim[] objects = Sims3.Gameplay.Queries.GetObjects<Sim>(obj.Position, distance);
                if (objects.Length == 0) return;
                bool on = ShortcutOn("reactioncopy");
                Sim targetSim = obj as Sim;
                ReactionTypes carried = reaction;
                foreach (Sim sim in objects)
                {
                    if (ignoredSims.Contains(sim) || sim.RoomId != obj.RoomId) continue;
                    ReactionTypes r = RcStart(on, reaction, carried);
                    if (r != reaction) PatchCounters()[2996]++;
                    else if (on && carried != reaction) PatchCounters()[2995]++;
                    sRcByTrait(sim, targetSim, isNegative, ref r);
                    carried = r;
                    if (sim.InheritedPriority().Level < InteractionPriorityLevel.UserDirected)
                    {
                        if (!string.IsNullOrEmpty(thoughtBalloon))
                        {
                            Sims3.Gameplay.ThoughtBalloons.ThoughtBalloonManager.BalloonData bd = new Sims3.Gameplay.ThoughtBalloons.ThoughtBalloonManager.DoubleBalloonData(obj.GetThumbnailKey(), thoughtBalloon);
                            sim.ThoughtBalloonManager.ShowBalloon(bd);
                        }
                        sim.PlayReaction(r, new InteractionPriority(priorityLevel), obj, string.Empty, ResourceKey.kInvalidResourceKey, Sims3.Gameplay.ThoughtBalloons.ThoughtBalloonAxis.kNeutral, reactionSpeed, sRcBefore, null, false, sim.ReactionDefaultTimeout);
                    }
                }
            }

            public static void ShotOrig(GameObject obj, float distance, ReactionTypes reaction, ReactionSpeed reactionSpeed, bool isNegative, List<Sim> ignoredSims, string thoughtBalloon, InteractionPriorityLevel priorityLevel) { }
        }

        // ---- B11 §5 carpoolschoolskip (U5) ------------------------------------------------------------------------------
        // [verified, decompiled] CarpoolData.DoCarpoolPickupAndDropoff.Run: a rider on the pickup lot who rides a horse
        // or whose lot skips the carpool is taken off the list and given mRider.Occupation.GetInteractionToTakeSimToWork().
        // A school child has a School and no Occupation: NullReferenceException, the Run dies with the bus parked and
        // GetIn offered to nobody; a teen with a job is sent to work instead of school by the school bus. The GetIn
        // definition also reads Sim.ActiveActor.LotHome with no active Sim. Fix: our copy of Run sends a school-bus rider
        // with EA's own School.CreateWorkInteractionInstance (as Run does further down for a rider off the lot; not
        // twice), any other car's rider with the Occupation's ride only when it has one, and tests ActiveActor for
        // null. A skipping rider RiderDelegate did not list is sent too (EA left it with a GetIn it could not take).
        // The rest is EA's Run. Counters: [2998] skipping riders sent on their own, [2999] Runs that threw on a
        // skipping rider (switch off), [3000] errors.
        static Type sCsType;
        static FieldInfo sCsData, sCsRiders, sCsTimeToLeave, sCsStart, sCsHonkAlarm, sCsGetIn, sCsArrived;
        static MethodInfo sCsHonk, sCsLoop, sCsSkip;
        static string sCpsMissing = "";

        static void ApplyCarpoolSchoolSkip()
        {
            sCsType = typeof(CarpoolManager.CarpoolData).GetNestedType("DoCarpoolPickupAndDropoff", BindingFlags.Public | BindingFlags.NonPublic);
            if (sCsType != null)
            {
                sCsData = sCsType.GetField("mData", kB11All);
                sCsRiders = sCsType.GetField("mRiderList", kB11All);
                sCsTimeToLeave = sCsType.GetField("mTimeToLeave", kB11All);
                sCsStart = sCsType.GetField("mStartTime", kB11All);
                sCsHonkAlarm = sCsType.GetField("mHonkReminder", kB11All);
                sCsGetIn = sCsType.GetField("mGetInDefinition", kB11All);
                sCsHonk = sCsType.GetMethod("Honk", kB11All);
                sCsLoop = sCsType.GetMethod("LoopUpdate", kB11All);
                sCsSkip = sCsType.GetMethod("CheckIfSimShouldSkipCarpool", kB11All);
            }
            sCsArrived = typeof(CarpoolManager.CarpoolData).GetField("mArrivedDelegate", kB11All);
            sCpsMissing = SpNames("CarpoolData+DoCarpoolPickupAndDropoff", sCsType, "mData", sCsData, "mRiderList", sCsRiders, "mTimeToLeave", sCsTimeToLeave, "mStartTime", sCsStart,
                "mHonkReminder", sCsHonkAlarm, "mGetInDefinition", sCsGetIn, "Honk", sCsHonk, "LoopUpdate", sCsLoop, "CheckIfSimShouldSkipCarpool", sCsSkip, "CarpoolData.mArrivedDelegate", sCsArrived);
            if (sCpsMissing.Length > 0) { AppliedLog().Add("carpoolschoolskip: not found: " + sCpsMissing); return; }
            Patch(sCsType, "Run", Type.EmptyTypes, typeof(BCsHook), "RunHook", "RunOrig");
        }

        // Pure: which ride a skipping rider gets: 1 school, 2 the occupation's, 0 none (EA: always the occupation's).
        internal static int CsRide(bool schoolBus, bool hasSchool, bool hasOccupation)
        {
            if (schoolBus) return hasSchool ? 1 : (hasOccupation ? 2 : 0);
            return hasOccupation ? 2 : 0;
        }

        delegate bool CsSkipFn(Sim sim, Lot startLot, Lot destLot);
        delegate void CsVoidFn();

        public class BCsHook
        {
            public bool RunHook()
            {
                if (!ShortcutOn("carpoolschoolskip"))
                {
                    try { return RunOrig(); }
                    catch (NullReferenceException) { PatchCounters()[2999]++; throw; }
                }
                return CsRun(this);
            }

            public bool RunOrig() { return false; }

            static bool CsRun(object me)
            {
                Interaction<Sim, CarpoolWithDriver> self = (Interaction<Sim, CarpoolWithDriver>)me;
                Sim actor = self.Actor;
                CarpoolWithDriver car = self.Target;
                CarpoolManager.CarpoolData data = (CarpoolManager.CarpoolData)sCsData.GetValue(me);
                List<Sim> riders = (List<Sim>)sCsRiders.GetValue(me);
                CsVoidFn honk = (CsVoidFn)Fixes.Bind(typeof(CsVoidFn), me, sCsHonk);
                CsSkipFn skip = (CsSkipFn)Fixes.Bind(typeof(CsSkipFn), me, sCsSkip);
                InteractionInstance.InsideLoopFunction loop = (InteractionInstance.InsideLoopFunction)Fixes.Bind(typeof(InteractionInstance.InsideLoopFunction), me, sCsLoop);
                bool schoolBus = data.CarType == CarNpcManager.NpcCars.CarBusSchool;
                Lot lot = LotManager.GetLot(data.StartLotId);
                Lot lot2 = LotManager.GetLot(data.DestLotId);
                if (!CarNpcManager.Singleton.DriveToLotInServiceCarWithinTime(car, actor, lot, data.TravelTimeInMinutes, true)) return false;
                honk();
                List<Sim> list = data.RiderDelegate(lot, lot2, false);
                List<Sim> skipped = null;
                foreach (Sim rider in riders)
                {
                    bool onLot = rider.LotCurrent == lot;
                    bool skips = RidingPosture.SimIsRidingHorseAndCanGallop(rider) && lot == rider.LotHome && onLot;
                    if (!skips && onLot) skips = skip(rider, lot, lot2);
                    if (!rider.HasBeenDestroyed && !list.Contains(rider) && !skips) list.Add(rider);
                    // EA handled a skipping rider only when RiderDelegate had listed it; one it had not was neither sent nor
                    // skipped, and the GetIn loop below pushed a GetIn its definition (built from list) refuses: no ride at all
                    if (!skips) continue;
                    list.Remove(rider);
                    Lazy.Add(ref skipped, rider);
                    if (rider.HasBeenDestroyed) continue;
                    InteractionInstance ride = null;
                    switch (CsRide(schoolBus, rider.School != null, rider.Occupation != null))
                    {
                        case 1:
                            if (!rider.InteractionQueue.HasInteractionOfType(GoToSchoolInRabbitHole.Singleton)) ride = rider.School.CreateWorkInteractionInstance();
                            break;
                        case 2:
                            ride = rider.Occupation.GetInteractionToTakeSimToWork();
                            break;
                    }
                    if (ride != null) rider.InteractionQueue.AddNext(ride);
                    PatchCounters()[2998]++;
                }
                if (list.Count == 0)
                {
                    car.DriveOffscreenAndRelease(lot);
                    return true;
                }
                Sim active = Sim.ActiveActor;
                InteractionDefinition getIn = new CarpoolManager.GetInCarpool.Definition(list, data.InteractionNameKey, active != null && lot == active.LotHome);
                sCsGetIn.SetValue(me, getIn);
                car.AddInteraction(getIn);
                car.SetOnCarEnteredDelegate(data.SimEnteredCarpool);
                foreach (Sim r in riders)
                {
                    if (r.HasBeenDestroyed || Lazy.Contains(skipped, r)) continue;
                    bool away = lot != r.LotHome;
                    InteractionInstance ii = getIn.CreateInstance(car, r, new InteractionPriority(InteractionPriorityLevel.UserDirected), false, true);
                    if (schoolBus)
                    {
                        (ii as CarpoolManager.GetInCarpool).SetTakingSimToWork();
                        if (FieldTripSituation.GetFieldTripSituation(r) != null)
                        {
                            r.AddExitReason(ExitReason.StageComplete);
                            ii.CancellableByPlayer = false;
                            r.InteractionQueue.AddNext(ii);
                            continue;
                        }
                        if (r.School != null && (r.School.SimToVisit != null || r.School.SimToBringOver != null))
                        {
                            r.InteractionQueue.AddNext(ii);
                            continue;
                        }
                    }
                    if (away) ii.LowerPriority();
                    if (!away)
                    {
                        if (r.LotCurrent == lot) r.InteractionQueue.AddNext(ii);
                        else if (schoolBus)
                        {
                            if (!r.InteractionQueue.HasInteractionOfType(GoToSchoolInRabbitHole.Singleton) && r.School != null) r.InteractionQueue.Add(r.School.CreateWorkInteractionInstance());
                        }
                        else if (!r.InteractionQueue.HasInteractionOfType(WorkInRabbitHole.Singleton) && r.OccupationAsCareer != null) r.InteractionQueue.Add(r.OccupationAsCareer.CreateWorkInteractionInstance());
                        continue;
                    }
                    if (r.OccupationAsActiveCareer != null)
                    {
                        InteractionInstance head = null;
                        switch (r.InteractionQueue.GetNumAndHeadVisibleInteraction(out head))
                        {
                            case 0: r.InteractionQueue.Add(ii); break;
                            case 1: if (head.GetPriority().Level == InteractionPriorityLevel.Autonomous) r.InteractionQueue.Add(ii); break;
                        }
                    }
                    else if (r.InteractionQueue.GetNumVisibleInteractions() <= 1) r.InteractionQueue.Add(ii);
                }
                float timeToLeave = SimClock.HoursUntil(data.DropoffTime) * 60f;
                sCsTimeToLeave.SetValue(me, timeToLeave);
                sCsHonkAlarm.SetValue(me, actor.AddAlarm(timeToLeave / 2f, TimeUnit.Minutes, (AlarmTimerCallback)Fixes.Bind(typeof(AlarmTimerCallback), me, sCsHonk), "Carpool honk reminder", AlarmType.DeleteOnReset));
                sCsStart.SetValue(me, SimClock.ElapsedTime(TimeUnit.Minutes));
                self.DoLoop(ExitReason.Default, loop, null);
                float start = SimClock.ElapsedTime(TimeUnit.Minutes);
                sCsStart.SetValue(me, start);
                bool waiting;
                do
                {
                    waiting = false;
                    foreach (Sim s in car.ReferenceList)
                        if (s.CurrentInteraction is CarpoolManager.GetInCarpool) { waiting = true; break; }
                    Simulator.Sleep(1u);
                }
                while (!(SimClock.ElapsedTime(TimeUnit.Minutes) - start > CarpoolManager.kExtraMinutesToWait) && waiting);
                actor.RemoveAlarm((AlarmHandle)sCsHonkAlarm.GetValue(me));
                sCsHonkAlarm.SetValue(me, AlarmHandle.kInvalidHandle);
                car.RemoveInteractionByType(getIn);
                sCsGetIn.SetValue(me, null);
                List<Sim> left = new List<Sim>(car.GetUniqueReferenceList());
                foreach (Sim o in car.Occupants) left.Remove(o);
                left.RemoveAll(delegate (Sim s) { return s == actor; });
                OnArrivedDelegate arrived = (OnArrivedDelegate)sCsArrived.GetValue(data);
                foreach (Sim s in left)
                {
                    bool gettingIn = s.CurrentInteraction is CarpoolManager.GetInCarpool;
                    s.InteractionQueue.PurgeInteractions(car);
                    if (gettingIn) s.AddExitReason(ExitReason.Canceled);
                    arrived(s, lot2, false);
                }
                start = SimClock.ElapsedTime(TimeUnit.Minutes);
                sCsStart.SetValue(me, start);
                while (car.GetNumUniqueReferences() > 1)
                {
                    Simulator.Sleep(1u);
                    if (SimClock.ElapsedTime(TimeUnit.Minutes) - start > CarpoolManager.kExtraMinutesToWait) break;
                }
                foreach (Sim p in car.Passengers) if (p.InteractionQueue.Count == 0) CarpoolManager.PushGetOut(p);
                actor.ClearExitReasons();
                bool drove = false;
                if (car.Passengers.Length == 0) car.DriveOffscreenAndRelease(lot);
                else if (CarNpcManager.Singleton.DriveToLotInCar(car, actor, lot2, false)) drove = true;
                else { CarNpcManager.Singleton.TeleportCarToLot(car, actor, lot2); drove = true; }
                if (drove)
                {
                    if (lot.StreetParking != null) lot.StreetParking.FreeParkingSpotForCar(car);
                    foreach (Sim p in car.Passengers)
                    {
                        if (p.InteractionQueue.Count != 0) p.AddExitReason(ExitReason.Finished);
                        else CarpoolManager.PushGetOut(p);
                    }
                }
                return true;
            }
        }
    }
}
