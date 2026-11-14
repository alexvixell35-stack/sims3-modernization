using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.ActiveCareer.ActiveCareers;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Careers;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.EventSystem;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.MapTags;
using Sims3.Gameplay.Objects.Miscellaneous;
using Sims3.Gameplay.Objects.RabbitHoles;
using Sims3.Gameplay.Opportunities;
using Sims3.Gameplay.Situations;
using Sims3.Gameplay.UI;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;
using Sims3.SimIFace.CAS;
using Sims3.SimIFace.Enums;
using Sims3.UI;

namespace Ts3Fixes
{
    // Combined rabbit holes (docs/551): comboprotest, combovenue, combosituation, combojob, comboprotestlot, comboopp
    // (tunings kComboProtest / kComboVenue / kComboSituation / kComboJob / kComboProtestLot / kComboOpp, all off by
    // default), plus the shared
    // combo helpers that ceremonyspot (EventRouteRetry.cs) uses.
    // Harness checks: ComboRabbitHoleCheck / ComboFixCheck (ComboChecks.cs).
    //
    // What EA does [verified in the decompiled game code unless marked]:
    // - A combo building (ComboRabbitHole subclass, e.g. ComboCityhallPoliceMilitary, ComboStadiumTheatre) makes each piece
    //   with CreateObjectOutOfWorld and ComboInitParams; the piece's RabbitHoleProxy is the combo. The piece is never placed:
    //   its Position is Vector3.OutOfWorld (EA's own Teleporter.PreTeleport tests exactly that to swap in the combo), and its
    //   LotCurrent is the WORLD lot (lot id FFFFFFFFFFFFFFFF), not null [verified in game, ComboRabbitHoleCheck, Appaloosa
    //   Plains 2026-11-14: all 13 pieces]. Interactions on a combo come from its pieces, so Target is the piece.
    // - Alarms of a piece are therefore fine: the world lot's AlarmManager is AlarmManager.Global (AlarmManager.LotId setter,
    //   lot id ulong.MaxValue), so AddAlarm / RemoveAlarm / GetTimeLeft all use the same manager. (An earlier `comboalarm`
    //   assumed a null lot; ComboFixCheck read EA's path as working, so it was removed.)
    // - A ReactionBroadcaster on a piece is registered on the world lot and finds Sims around origin.Position = OutOfWorld:
    //   it reaches nobody.
    //
    // comboprotest (EA bug, verified in code): CityHall.CreateProtest builds its three broadcasters on `this` (the piece),
    // unlike CreateCelebration, which uses RabbitHoleProxy; in a combo the protest never draws anyone. The hook moves them
    // to the building (origin and lot; the Protest interaction still targets the City Hall piece). RouteToGatheringArea for
    // a protest re-anchors on the entrance when RoutingSlot_0 is missing (the ceremonyspot case), for every Sim: it only
    // corrects the goal, the Sim still walks.
    //
    // combovenue (EA bug, verified in code): Theatre.StartShowSoundBroadcaster puts the TheatreMusic broadcaster on the
    // piece, so nobody near a combo theatre (ComboStadiumTheatre, ComboCriminalTheater) gets the buff; it is moved to the
    // building. The player-show calls (StartPlayerConcert & co.) do not throw: they pass the world lot to the search-light
    // and sandwich-board helpers, so only the lights and boards at the building stay unchanged (cosmetic, not fixed, like
    // EA's daily light / board alarms).
    //
    // combosituation (EA bug, verified in code): AutographSessionSituation takes the rabbit hole it is given as its
    // broadcaster origin and lot; for a piece that is OutOfWorld on the world lot, so the autographer (sent to the
    // building's entrance) never enters the broadcaster and the session never starts (no fans, no pay) until the player
    // cancels it. The building is used instead. A field trip to ComboStadiumTheatre gets its theatre link set first: EA
    // sets mTheatre only in the chaperone's OnFieldtripStart, so a student whose loop ticks first throws in
    // OnFieldtripUpdate [likely]. (FieldTripSituation itself works for a piece: its lot is the world lot, not null, and
    // the rest of the situation uses RabbitHoleProxy.LotCurrent.)
    //
    // combojob (EA bug, verified in code): Occupation.InitializeBaseJobCreationSpec, RabbitHole destination, tests each
    // rabbit hole of the job's type with item.LotCurrent, the world lot for a piece, so IsJobValidForCreation fails and the
    // piece is skipped (the RabbitHoleLot branch and RemoveRabbitHolesThatCantHaveJobs use RabbitHoleProxy.LotCurrent).
    // A job type whose rabbit hole exists only inside combos is never offered: Firefighter Epic Hospital / Science /
    // Criminal rescues, Medical's ScienceLab / CityHall / Hideout jobs, Film's PickUpFoodForShoot (Restaurant) and
    // ResearchCompetingFilm (Theatre). When EA found no rabbit hole and no stand-alone one of the type is valid, the fix runs
    // EA's own selection over the pieces with the building's lot (EA's RabbitHoleLot rule): random order, first valid lot,
    // EA's household-job check. The job keeps the piece as its rabbit hole like EA's RabbitHoleLot jobs, so its
    // interactions show on the building's pie menu. Two OutOfWorld uses such a job then meets are moved to the building:
    // its ActiveCareerMapTag target (the piece has no map position [inferred]) and the rescue / ghost-cloud effect that
    // RabbitholeFirefightingJob.SetupRabbitholeRescue / GhostHunterJob.CreateRabbitHoleInvestigation put at the piece.
    //
    // comboprotestlot (EA bug, verified in code, EP9): ImprovedProtestSituation.ScheduleTodaysProtest adds
    // Queries.GetObjects<ICityHall>()[0].LotCurrent to the daily NPC protest's candidate lots; the City Hall piece comes
    // first [verified in game, Appaloosa Plains], so the candidate is the world lot and the City Hall never hosts one; and
    // ImprovedProtestHelperObject.FindMapTagPickerInfo (megaphone / podium "organize protest" picker) adds a world-lot
    // entry next to the building's. A protest created on the world lot moves to the combo City Hall's lot (only a piece
    // gives the world lot there), and the picker's world-lot entries are dropped.
    //
    // comboopp (EA bug, verified in code; docs/551 §5): an opportunity whose target is a rabbit hole type gets the BUILDING
    // as its TargetObject when that rabbit hole is a combo piece (OpportunityManager.SetupHelper returns RabbitHoleProxy), so
    // its turn-in interaction sits on the building and kVisitedRabbitHole carries the building: those steps work. Every
    // step that instead waits for the piece's own interaction or event compares the piece with the building and never
    // matches:
    //   - listeners made by GetTargetForEvent (EventListenerInfo steps such as kAteAtRestaurant) wait for the building,
    //     while the piece's interaction sends the event with the piece: the listener now waits for the piece of the
    //     opportunity's rabbit hole type (kVisitedRabbitHole / ...WithPet / kDeliveredToSim / kWentToWork keep EA's target);
    //   - kInteractionSuccess steps (Investigator case steps "eat at the restaurant" EP2_Case_CulinaryBackstab3 /
    //     SuperFruits2, EP3 RestaurantPromotion1, EP9 PhysEd_10 at the Stadium): FindValidTargets accepts only objects of
    //     the interaction's target type, which the building is not, so EA picks some other (random) target or none; the
    //     building now stands in for its piece (EA's own tag and effect code then marks the building), and an event the
    //     piece sends is matched through its building;
    //   - interaction tests that compare activeOpportunity.TargetObject with their target (School auditions and recitals,
    //     ScienceLab imaginary-friend potion steps): the interaction never shows at a combo school / science lab; the test
    //     now runs with the piece as the target when the opportunity's target is the piece's building.
    // Household Sims are what opportunities are for: the fix lets the step complete exactly as at a stand-alone rabbit
    // hole and changes nothing else. Opportunities at stand-alone rabbit holes never meet a piece and run EA's code.
    //
    // Non-combo rabbit holes are untouched by every switch: each path tests RhPiece first and runs EA's code unchanged.
    // No Sim is moved by any of these fixes.
    //
    // Counters (registry fixes-2335): [2337] broadcasters moved to the building, [2338] protest routes re-anchored at the
    // entrance, [2340] autograph sessions moved to the building, [2342] ComboStadiumTheatre theatre links set, [2343]
    // errors (every combo switch). [2335] [2336] [2339] [2341] retired (comboalarm, the venue lot lend and the field-trip lot
    // lend were removed); [2344] reserved. Registry fixes-2374: [2374] jobs created at a combo piece, [2375] job map tags
    // pointed at the building, [2376] job effects moved to the building (combojob); [2377] protests moved from the world
    // lot to the combo City Hall's lot, [2378] world-lot picker entries dropped (comboprotestlot). Registry fixes-2405
    // (comboopp): [2405] listener targets moved from the building to its piece, [2406] interaction-success targets where
    // the building stands in for its piece, [2407] piece events matched through the building, [2408] interaction tests
    // passed for a piece.
    public static partial class Fixes
    {
        // ---- tuning ----
        const float kRhEntranceOut = 2f;  // metres out from the first enter slot when a slot reads as the world origin

        // ---- state (reflection, looked up once) ----
        static readonly FieldInfo sRhBroadcastOrigin = typeof(ReactionBroadcaster).GetField("mBroadcastingObject", kInstAll);
        static readonly MethodInfo sRhLotAddReaction = typeof(Lot).GetMethod("AddReaction", kInstAll);
        static readonly MethodInfo sRhLotRemoveReaction = typeof(Lot).GetMethod("RemoveReaction", kInstAll);
        static readonly FieldInfo sRhComboTheatre = typeof(ComboStadiumTheatre).GetField("mTheatre", kInstAll);
        static readonly FieldInfo[] sRhProtestBroadcasters = {
            typeof(CityHall).GetField("mGatheringBroadcaster", kInstAll),
            typeof(CityHall).GetField("mProtestReactionBroadcaster", kInstAll),
            typeof(CityHall).GetField("mProtestPetReactionBroadcaster", kInstAll) };
        static readonly FieldInfo sRhTheatreBroadcaster = typeof(Theatre).GetField("mTheatreBroadcaster", kInstAll);
        static readonly Type[] sRhInitSpecArgs = { typeof(JobCreationSpec).MakeByRefType(), typeof(JobId), typeof(JobStaticData), typeof(bool) };
        static readonly MethodInfo sRhJobValid = typeof(Occupation).GetMethod("IsJobValidForCreation", kInstAll);
        static readonly MethodInfo sRhJobDestType = typeof(Occupation).GetMethod("GetJobDestinationType", kInstAll);
        static readonly MethodInfo sRhHouseholdJobOnLot = typeof(Occupation).GetMethod("AnySimInHouseholdHasJobOnLot", kInstAll);
        static readonly FieldInfo sRhRescueEffect = typeof(RabbitholeFirefightingJob).GetField("mRabbitholeEffect", kInstAll);
        static readonly FieldInfo sRhGhostEffect = typeof(GhostHunter.GhostHunterJob).GetField("mVisualEffect", kInstAll);
        static readonly FieldInfo sRhJobSpec = typeof(Job).GetField("Specification", kInstAll);  // internal
        static readonly FieldInfo sRhEventTarget = typeof(Sims3.Gameplay.EventSystem.Event).GetField("mTargetObject", kInstAll);
        static readonly FieldInfo sRhListenerTargets = typeof(OpportunityInteractionCompleteListener).GetField("mTargets", kInstAll);
        static readonly Type[] sRhTargetForEventArgs = { typeof(Opportunity), typeof(Opportunity.OpportunitySharedData.EventInfo) };

        static void ApplyComboRabbitHole()
        {
            Patch(typeof(CityHall), "CreateProtest", typeof(ComboProtestHook), "CreateProtestHook", "CreateProtestOrig");
            Patch(typeof(Theatre), "StartShowSoundBroadcaster", typeof(ComboTheatreHook), "BroadcasterHook", "BroadcasterOrig");
            Patch(typeof(AutographSessionSituation), "Create", typeof(ComboSituationHook), "AutographHook", "AutographOrig");
            Patch(typeof(FieldTripSituation), "Create", typeof(ComboSituationHook), "FieldTripHook", "FieldTripOrig");
            Patch(typeof(Occupation), "InitializeBaseJobCreationSpec", sRhInitSpecArgs, typeof(ComboJobHook), "InitSpecHook", "InitSpecOrig");
            Patch(typeof(ActiveCareerMapTag), "get_Target", typeof(ComboJobMapTagHook), "TargetHook", "TargetOrig");
            Patch(typeof(RabbitholeFirefightingJob), "SetupRabbitholeRescue", typeof(ComboRescueHook), "SetupHook", "SetupOrig");
            Patch(typeof(GhostHunter.GhostHunterJob), "CreateRabbitHoleInvestigation", typeof(ComboGhostHook), "CreateHook", "CreateOrig");
            Patch(typeof(ImprovedProtestSituation), "Create", typeof(ComboProtestLotHook), "CreateHook", "CreateOrig");
            Patch(typeof(ImprovedProtestHelperObject), "FindMapTagPickerInfo", typeof(ComboProtestLotHook), "PickerHook", "PickerOrig");
            ApplyComboOpp();
        }

        static void ApplyComboOpp()
        {
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic;
            Patch(typeof(OpportunityManager), "GetTargetForEvent", sRhTargetForEventArgs, typeof(ComboOppHook), "TargetHook", "TargetOrig");
            Patch(typeof(OpportunityManager), "FindValidTargets", typeof(ComboOppHook), "FindHook", "FindOrig");
            Patch(typeof(OpportunityInteractionCompleteListener), "ProcessEvent", typeof(ComboOppListenerHook), "ProcessHook", "ProcessOrig");
            Type audition = typeof(SchoolRabbitHole.AttendAudition).GetNestedType("Definition", all);
            Type recital = RhNestedDefinition(typeof(SchoolRabbitHole), "AttendRecital");
            Type potionBuy = RhNestedDefinition(typeof(ScienceLab), "PurchaseImaginaryFriendPotion");
            Type potionMail = RhNestedDefinition(typeof(ScienceLab), "DeliverImaginaryFriendRgm");
            if (audition == null || recital == null || potionBuy == null || potionMail == null) { AppliedLog().Add("comboopp: a School / ScienceLab Definition type was not found, tests not patched"); return; }
            Patch(audition, "Test", typeof(ComboOppTestHook), "AuditionHook", "AuditionOrig");
            Patch(recital, "Test", typeof(ComboOppTestHook), "RecitalHook", "RecitalOrig");
            Patch(potionBuy, "Test", typeof(ComboOppTestHook), "PotionBuyHook", "PotionBuyOrig");
            Patch(potionMail, "Test", typeof(ComboOppTestHook), "PotionMailHook", "PotionMailOrig");
        }

        // ---- hooks ----

        public class ComboProtestHook
        {
            public void CreateProtestHook()
            {
                CreateProtestOrig();
                CityHall hall = (CityHall)(object)this;
                if (!RhPiece(hall) || !ShortcutOn("comboprotest")) return;
                foreach (FieldInfo f in sRhProtestBroadcasters) RhMoveBroadcaster(hall, f);
            }
            public void CreateProtestOrig() { } // receives CityHall.CreateProtest's original body
        }

        // combovenue: the TheatreMusic broadcaster moves from the piece to the building.
        public class ComboTheatreHook
        {
            public void BroadcasterHook(ShowVenue.ShowTypes type)
            {
                BroadcasterOrig(type);
                Theatre t = (Theatre)(object)this;
                if (RhPiece(t) && ShortcutOn("combovenue")) RhMoveBroadcaster(t, sRhTheatreBroadcaster);
            }
            public void BroadcasterOrig(ShowVenue.ShowTypes type) { } // receives Theatre.StartShowSoundBroadcaster's original body
        }

        public static class ComboSituationHook
        {
            public static AutographSessionSituation AutographHook(Sim autographer, RabbitHole rabbitHole)
            {
                if (rabbitHole != null && RhPiece(rabbitHole) && ShortcutOn("combosituation"))
                {
                    rabbitHole = rabbitHole.RabbitHoleProxy;
                    PatchCounters()[2340]++;
                }
                return AutographOrig(autographer, rabbitHole);
            }
            public static AutographSessionSituation AutographOrig(Sim autographer, RabbitHole rabbitHole) { return null; } // receives AutographSessionSituation.Create's original body

            public static FieldTripSituation FieldTripHook(List<SimDescription> primaryStudents, CASAgeGenderFlags age, School school, RabbitHole rabbitHole)
            {
                if (rabbitHole != null && ShortcutOn("combosituation")) RhLinkComboTheatre(rabbitHole);
                return FieldTripOrig(primaryStudents, age, school, rabbitHole);
            }
            public static FieldTripSituation FieldTripOrig(List<SimDescription> primaryStudents, CASAgeGenderFlags age, School school, RabbitHole rabbitHole) { return null; } // receives FieldTripSituation.Create's original body
        }

        // combojob: EA found no rabbit hole for a RabbitHole-destination job; offer it at a combo piece (RhJobAtPiece).
        public class ComboJobHook
        {
            public bool InitSpecHook(ref JobCreationSpec jobCreationSpec, JobId jobId, JobStaticData jobStaticData, bool enableValidationOnJob)
            {
                if (InitSpecOrig(ref jobCreationSpec, jobId, jobStaticData, enableValidationOnJob)) return true;
                if (!ShortcutOn("combojob")) return false;
                JobCreationSpec spec = RhJobAtPiece((Occupation)(object)this, jobId, jobStaticData, enableValidationOnJob);
                if (spec == null) return false;
                jobCreationSpec = spec;
                return true;
            }
            public bool InitSpecOrig(ref JobCreationSpec jobCreationSpec, JobId jobId, JobStaticData jobStaticData, bool enableValidationOnJob) { return false; } // receives Occupation.InitializeBaseJobCreationSpec's original body
        }

        // combojob: the map tag of a job at a piece shows on the building.
        public class ComboJobMapTagHook
        {
            public GameObject TargetHook()
            {
                GameObject target = TargetOrig();
                RabbitHole rh = target as RabbitHole;
                if (rh == null || !RhPiece(rh) || !ShortcutOn("combojob")) return target;
                PatchCounters()[2375]++;
                return rh.RabbitHoleProxy;
            }
            public GameObject TargetOrig() { return null; } // receives ActiveCareerMapTag.get_Target's original body
        }

        // combojob: the rescue effect of a firefighter job at a piece plays on the building.
        public class ComboRescueHook
        {
            public bool SetupHook()
            {
                bool ok = SetupOrig();
                Job job = (Job)(object)this;
                RhMoveJobEffect(job, sRhRescueEffect);
                return ok;
            }
            public bool SetupOrig() { return false; } // receives RabbitholeFirefightingJob.SetupRabbitholeRescue's original body
        }

        // combojob: the ghost cloud of a ghost-hunter investigation at a piece plays on the building.
        public class ComboGhostHook
        {
            public bool CreateHook()
            {
                bool ok = CreateOrig();
                Job job = (Job)(object)this;
                RhMoveJobEffect(job, sRhGhostEffect);
                return ok;
            }
            public bool CreateOrig() { return false; } // receives GhostHunter.GhostHunterJob.CreateRabbitHoleInvestigation's original body
        }

        // comboprotestlot: a protest on the world lot (only a City Hall piece gives that lot) moves to the combo City Hall's
        // lot; the protest picker loses its world-lot entries.
        public static class ComboProtestLotHook
        {
            public static ImprovedProtestSituation CreateHook(Sim organizer, Lot lot, DateAndTime toStart, int initialScore, ImprovedProtestSituationType protestType, ImprovedProtestCauses cause)
            {
                if (lot != null && lot.IsWorldLot && ShortcutOn("comboprotestlot"))
                {
                    Lot hall = RhCityHallLot();
                    if (hall != null)
                    {
                        lot = hall;
                        PatchCounters()[2377]++;
                    }
                }
                return CreateOrig(organizer, lot, toStart, initialScore, protestType, cause);
            }
            public static ImprovedProtestSituation CreateOrig(Sim organizer, Lot lot, DateAndTime toStart, int initialScore, ImprovedProtestSituationType protestType, ImprovedProtestCauses cause) { return null; } // receives ImprovedProtestSituation.Create's original body

            public static List<IMapTagPickerInfo> PickerHook()
            {
                List<IMapTagPickerInfo> list = PickerOrig();
                if (list == null || !ShortcutOn("comboprotestlot") || RhCityHallLot() == null) return list;
                int dropped = list.RemoveAll(delegate (IMapTagPickerInfo i)
                {
                    MapTagPickerLotInfo li = i as MapTagPickerLotInfo;
                    return li != null && li.Lot != null && li.Lot.IsWorldLot;
                });
                PatchCounters()[2378] += dropped;
                return list;
            }
            public static List<IMapTagPickerInfo> PickerOrig() { return null; } // receives ImprovedProtestHelperObject.FindMapTagPickerInfo's original body
        }

        // ---- shared combo helpers (ceremonyspot uses RhSlotOrEntrance too) ----

        // A piece of a combo building: its RabbitHoleProxy is another object (the combo). A stand-alone rabbit hole and the
        // combo itself are their own proxy.
        // comboopp: listeners EA aims at the building (GetTargetForEvent) wait for the piece of the opportunity's rabbit hole
        // type instead, except the events the building itself carries (turn-in, pet visit, delivery) and kWentToWork.
        public class ComboOppHook
        {
            public GameObject TargetHook(Opportunity op, Opportunity.OpportunitySharedData.EventInfo eventInfo)
            {
                GameObject result = TargetOrig(op, eventInfo);
                try
                {
                    RabbitHole piece = RhOppListenerPiece(op, eventInfo, result);
                    if (piece != null) { PatchCounters()[2405]++; return piece; }
                }
                catch (Exception) { PatchCounters()[2343]++; }
                return result;
            }
            public GameObject TargetOrig(Opportunity op, Opportunity.OpportunitySharedData.EventInfo eventInfo) { return null; } // receives OpportunityManager.GetTargetForEvent

            public static void FindHook(Sim actor, Opportunity toAdd, Opportunity.OpportunitySharedData.InteractionCompleteEventInfo intei, int numTimesRequired, List<IGameObject> targets, bool lotsOnly)
            {
                try { if (RhOppAddBuilding(toAdd, intei, numTimesRequired, targets)) PatchCounters()[2406]++; }
                catch (Exception) { PatchCounters()[2343]++; }
                FindOrig(actor, toAdd, intei, numTimesRequired, targets, lotsOnly);
            }
            public static void FindOrig(Sim actor, Opportunity toAdd, Opportunity.OpportunitySharedData.InteractionCompleteEventInfo intei, int numTimesRequired, List<IGameObject> targets, bool lotsOnly) { } // receives OpportunityManager.FindValidTargets
        }

        // comboopp: an interaction-success event a piece sends counts for the building in the listener's target list.
        public class ComboOppListenerHook
        {
            public ListenerAction ProcessHook(Sims3.Gameplay.EventSystem.Event e)
            {
                RabbitHole piece = e == null ? null : e.TargetObject as RabbitHole;
                if (piece == null || !RhPiece(piece)) return ProcessOrig(e);
                bool swapped = false;
                try
                {
                    List<IGameObject> targets = sRhListenerTargets == null ? null : sRhListenerTargets.GetValue(this) as List<IGameObject>;
                    if (targets != null && !targets.Contains(piece) && targets.Contains(piece.RabbitHoleProxy) && sRhEventTarget != null && ShortcutOn("comboopp"))
                    {
                        sRhEventTarget.SetValue(e, piece.RabbitHoleProxy);
                        swapped = true;
                        PatchCounters()[2407]++;
                    }
                }
                catch (Exception) { PatchCounters()[2343]++; }
                if (!swapped) return ProcessOrig(e);
                try { return ProcessOrig(e); }
                finally { sRhEventTarget.SetValue(e, piece); }
            }
            public ListenerAction ProcessOrig(Sims3.Gameplay.EventSystem.Event e) { return ListenerAction.Keep; } // receives OpportunityInteractionCompleteListener.ProcessEvent
        }

        // comboopp: School audition / recital and ScienceLab potion tests compare the active special opportunity's
        // TargetObject (the building) with their target (the piece); they run with the piece as the opportunity's target.
        public class ComboOppTestHook
        {
            public bool AuditionHook(Sim a, SchoolRabbitHole target, bool isAutonomous, ref GreyedOutTooltipCallback greyedOutTooltipCallback)
            {
                Opportunity op = RhOppForPiece(a, target);
                if (op == null) return AuditionOrig(a, target, isAutonomous, ref greyedOutTooltipCallback);
                op.TargetObject = target;
                try { return RhOppCount(AuditionOrig(a, target, isAutonomous, ref greyedOutTooltipCallback)); }
                finally { op.TargetObject = target.RabbitHoleProxy; }
            }
            public bool AuditionOrig(Sim a, SchoolRabbitHole target, bool isAutonomous, ref GreyedOutTooltipCallback greyedOutTooltipCallback) { return false; } // receives the original

            public bool RecitalHook(Sim a, RabbitHole target, bool isAutonomous, ref GreyedOutTooltipCallback greyedOutTooltipCallback)
            {
                Opportunity op = RhOppForPiece(a, target);
                if (op == null) return RecitalOrig(a, target, isAutonomous, ref greyedOutTooltipCallback);
                op.TargetObject = target;
                try { return RhOppCount(RecitalOrig(a, target, isAutonomous, ref greyedOutTooltipCallback)); }
                finally { op.TargetObject = target.RabbitHoleProxy; }
            }
            public bool RecitalOrig(Sim a, RabbitHole target, bool isAutonomous, ref GreyedOutTooltipCallback greyedOutTooltipCallback) { return false; } // receives the original

            public bool PotionBuyHook(Sim a, ScienceLab target, bool isAutonomous, ref GreyedOutTooltipCallback greyedOutTooltipCallback)
            {
                Opportunity op = RhOppForPiece(a, target);
                if (op == null) return PotionBuyOrig(a, target, isAutonomous, ref greyedOutTooltipCallback);
                op.TargetObject = target;
                try { return RhOppCount(PotionBuyOrig(a, target, isAutonomous, ref greyedOutTooltipCallback)); }
                finally { op.TargetObject = target.RabbitHoleProxy; }
            }
            public bool PotionBuyOrig(Sim a, ScienceLab target, bool isAutonomous, ref GreyedOutTooltipCallback greyedOutTooltipCallback) { return false; } // receives the original

            public bool PotionMailHook(Sim a, ScienceLab target, bool isAutonomous, ref GreyedOutTooltipCallback greyedOutTooltipCallback)
            {
                Opportunity op = RhOppForPiece(a, target);
                if (op == null) return PotionMailOrig(a, target, isAutonomous, ref greyedOutTooltipCallback);
                op.TargetObject = target;
                try { return RhOppCount(PotionMailOrig(a, target, isAutonomous, ref greyedOutTooltipCallback)); }
                finally { op.TargetObject = target.RabbitHoleProxy; }
            }
            public bool PotionMailOrig(Sim a, ScienceLab target, bool isAutonomous, ref GreyedOutTooltipCallback greyedOutTooltipCallback) { return false; } // receives the original
        }

        // owner+name+Definition (private nested interaction classes), or null.
        static Type RhNestedDefinition(Type owner, string name)
        {
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic;
            Type inter = owner.GetNestedType(name, all);
            return inter == null ? null : inter.GetNestedType("Definition", all);
        }

        static bool RhOppCount(bool passed)
        {
            if (passed) PatchCounters()[2408]++;
            return passed;
        }

        // The actor's active special opportunity when its target is this piece's building (comboopp on), else null.
        static Opportunity RhOppForPiece(Sim a, RabbitHole piece)
        {
            try
            {
                if (a == null || piece == null || !RhPiece(piece) || a.OpportunityManager == null || !ShortcutOn("comboopp")) return null;
                Opportunity op = a.OpportunityManager.GetActiveOpportunity(OpportunityCategory.Special);
                return op != null && op.TargetObject == piece.RabbitHoleProxy ? op : null;
            }
            catch (Exception) { PatchCounters()[2343]++; return null; }
        }

        // The piece a GetTargetForEvent listener should wait for: EA's target is the opportunity's building, the event is
        // one a piece's own interaction sends, and the building holds a piece of a type the opportunity names.
        static RabbitHole RhOppListenerPiece(Opportunity op, Opportunity.OpportunitySharedData.EventInfo eventInfo, GameObject result)
        {
            ComboRabbitHole combo = result as ComboRabbitHole;
            if (combo == null || op == null || eventInfo == null || result != op.TargetObject) return null;
            EventTypeId id = eventInfo.mEventId;
            if (id == EventTypeId.kVisitedRabbitHole || id == EventTypeId.kVisitedRabbitHoleWithPet || id == EventTypeId.kDeliveredToSim || id == EventTypeId.kWentToWork) return null;
            if (!ShortcutOn("comboopp")) return null;
            return RhComboPieceOfTypes(combo, op.TargetData);
        }

        // The building's first piece whose Guid is one of the comma-separated rabbit hole types (an opportunity's TargetData).
        static RabbitHole RhComboPieceOfTypes(ComboRabbitHole combo, string types)
        {
            if (string.IsNullOrEmpty(types)) return null;
            List<RabbitHoleType> wanted = new List<RabbitHoleType>();
            foreach (string s in types.Split(','))
            {
                RabbitHoleType t;
                if (ParserFunctions.TryParseEnum<RabbitHoleType>(s.Trim(), out t, RabbitHoleType.None) && t != RabbitHoleType.None) wanted.Add(t);
            }
            foreach (RabbitHole piece in combo.ContainedRabbitholes.Keys) if (piece != null && wanted.Contains(piece.Guid)) return piece;
            return null;
        }

        // kInteractionSuccess targets: the opportunity's building stands in for its piece when the interaction targets the
        // piece's type, so EA marks the building and the listener matches the piece through it.
        static bool RhOppAddBuilding(Opportunity toAdd, Opportunity.OpportunitySharedData.InteractionCompleteEventInfo intei, int numTimesRequired, List<IGameObject> targets)
        {
            ComboRabbitHole combo = toAdd == null ? null : toAdd.TargetObject as ComboRabbitHole;
            if (combo == null || intei == null || intei.mAnyTargetValid || targets == null || targets.Count >= numTimesRequired || targets.Contains(combo)) return false;
            Type def = intei.mInteractionDefinitionType;
            if (def == null || Opportunity.OpportunitySharedData.InteractionCompleteEventInfo.IsObjectOfTypeForInteraction(def, combo) || !ShortcutOn("comboopp")) return false;
            foreach (RabbitHole piece in combo.ContainedRabbitholes.Keys)
            {
                if (piece == null || !Opportunity.OpportunitySharedData.InteractionCompleteEventInfo.IsObjectOfTypeForInteraction(def, piece)) continue;
                targets.Add(combo);
                return true;
            }
            return false;
        }

        static bool RhPiece(RabbitHole rh)
        {
            RabbitHole proxy = rh.RabbitHoleProxy;
            return proxy != null && proxy != rh;
        }

        // EA's slot on the building, or, when fix is set and the slot reads as the world origin (the combo model has no such
        // slot), kRhEntranceOut metres out from the first enter slot (away from the building's centre) on terrain height. True when the entrance was used.
        static bool RhSlotOrEntrance(RabbitHole building, Slot slot, bool fix, out Vector3 pos, out Vector3 fwd)
        {
            pos = building.GetPositionOfSlot(slot);
            fwd = building.GetForwardOfSlot(slot);
            if (!fix || Math.Abs(pos.x) + Math.Abs(pos.z) > 1f || building.EnterSlots == null || building.EnterSlots.Count == 0) return false;
            // Outward = from the building's centre through the door. The enter slot's own forward faces the door (a Sim
            // entering looks at it), so it points into the footprint: a goal there has no path (measured 2026-11-14,
            // FailedBecauseNoPathToDestination for the protest crowd).
            Vector3 door = building.GetPositionOfSlot(building.EnterSlots[0]);
            Vector3 outward = door - building.Position;
            outward.y = 0f;
            if (outward.LengthSqr() < 0.01f) outward = -building.GetForwardOfSlot(building.EnterSlots[0]);
            fwd = outward.Normalize();
            pos = door + fwd * kRhEntranceOut;
            pos.y = World.GetTerrainHeight(pos.x, pos.z);
            return true;
        }

        // combojob: EA's RabbitHole-destination selection (Occupation.InitializeBaseJobCreationSpec) over the pieces of the
        // job's rabbit hole type, with the building's lot, as EA's RabbitHoleLot branch does. Runs only when EA found
        // nothing and no stand-alone rabbit hole of the type is valid, so EA's own pick and its household-job stop stand.
        // Null = no job, as EA.
        static JobCreationSpec RhJobAtPiece(Occupation occ, JobId jobId, JobStaticData data, bool validate)
        {
            try
            {
                if (occ == null || data == null || data.RabbitHoleType == RabbitHoleType.None || sRhJobValid == null || sRhJobDestType == null || sRhHouseholdJobOnLot == null) return null;
                if (validate && (data.DaysAvailable & SimClock.CurrentDayOfWeek) != SimClock.CurrentDayOfWeek) return null;
                if ((JobDestinaitonType)sRhJobDestType.Invoke(occ, new object[] { jobId, data }) != JobDestinaitonType.RabbitHole) return null;
                List<RabbitHole> pieces = new List<RabbitHole>();
                foreach (RabbitHole rh in RabbitHole.GetRabbitHolesOfType(data.RabbitHoleType))
                {
                    Lot lot = rh.RabbitHoleProxy == null ? null : rh.RabbitHoleProxy.LotCurrent;
                    if (lot == null || lot.IsWorldLot || rh.Guid == RabbitHoleType.Subway) continue;  // RemoveRabbitHolesThatCantHaveJobs
                    if (!RhPiece(rh)) { if (RhJobValid(occ, jobId, lot)) return null; continue; }  // EA had a stand-alone: its result stands
                    pieces.Add(rh);
                }
                RandomUtil.RandomizeListOfObjects(pieces);
                foreach (RabbitHole piece in pieces)
                {
                    Lot lot = piece.RabbitHoleProxy.LotCurrent;
                    if (!RhJobValid(occ, jobId, lot)) continue;
                    if (validate && (bool)sRhHouseholdJobOnLot.Invoke(occ, new object[] { Household.ActiveHousehold, lot })) return null;  // EA stops at the first valid lot
                    JobCreationSpec spec = new JobCreationSpec(occ, jobId);
                    spec.mLot = lot;
                    spec.mRabbitHole = piece;
                    if (data.JobStartTime != -1f) spec.mStartTime = SimClock.Add(SimClock.CurrentTime(), TimeUnit.Hours, SimClock.HoursUntil(data.JobStartTime));
                    PatchCounters()[2374]++;
                    return spec;
                }
            }
            catch (Exception) { PatchCounters()[2343]++; }
            return null;
        }

        static bool RhJobValid(Occupation occ, JobId jobId, Lot lot)
        {
            return (bool)sRhJobValid.Invoke(occ, new object[] { jobId, lot });
        }

        // combojob: an effect EA started at the job's rabbit hole (a piece: OutOfWorld) moves to the building.
        static void RhMoveJobEffect(Job job, FieldInfo effectField)
        {
            try
            {
                JobCreationSpec spec = sRhJobSpec == null ? null : sRhJobSpec.GetValue(job) as JobCreationSpec;
                RabbitHole rh = spec == null ? null : spec.mRabbitHole;
                VisualEffect fx = effectField == null ? null : effectField.GetValue(job) as VisualEffect;
                if (rh == null || fx == null || !RhPiece(rh) || !ShortcutOn("combojob")) return;
                RabbitHole building = rh.RabbitHoleProxy;
                fx.SetPosAndOrient(building.Position, building.ForwardVector, building.UpVector);
                PatchCounters()[2376]++;
            }
            catch (Exception) { PatchCounters()[2343]++; }
        }

        // The lot of the combo building that holds a City Hall piece, or null (no combo City Hall in this world).
        static Lot RhCityHallLot()
        {
            CityHall hall = FirstPiece<CityHall>();
            return hall == null ? null : hall.RabbitHoleProxy.LotCurrent;
        }

        // The first piece of type T in the world, or null.
        static T FirstPiece<T>() where T : RabbitHole
        {
            foreach (T rh in Sims3.Gameplay.Queries.GetObjects<T>()) if (RhPiece(rh)) return rh;
            return null;
        }

        // Moves the broadcaster held in piece.field from the piece (OutOfWorld, on the world lot) to the building:
        // the building is the origin and its lot runs it. The broadcaster's interaction still targets the piece.
        static void RhMoveBroadcaster(RabbitHole piece, FieldInfo field)
        {
            try
            {
                ReactionBroadcaster b = field == null ? null : field.GetValue(piece) as ReactionBroadcaster;
                RabbitHole building = piece.RabbitHoleProxy;
                Lot lot = building.LotCurrent;
                if (b == null || lot == null || b.BroadcastingObject != piece || sRhBroadcastOrigin == null || sRhLotAddReaction == null || sRhLotRemoveReaction == null) return;
                Lot world = LotManager.GetWorldLot();
                if (world != null) sRhLotRemoveReaction.Invoke(world, new object[] { b });
                sRhBroadcastOrigin.SetValue(b, building);
                sRhLotAddReaction.Invoke(lot, new object[] { b });
                PatchCounters()[2337]++;
            }
            catch (Exception) { PatchCounters()[2343]++; }
        }

        // A field trip to ComboStadiumTheatre: EA's mTheatre is set only in the chaperone's OnFieldtripStart.
        static void RhLinkComboTheatre(RabbitHole rh)
        {
            try
            {
                ComboStadiumTheatre combo = rh as ComboStadiumTheatre;
                if (combo == null || sRhComboTheatre == null || sRhComboTheatre.GetValue(combo) != null) return;
                Theatre t = combo.GetTheatre();
                if (t == null) return;
                sRhComboTheatre.SetValue(combo, t);
                PatchCounters()[2342]++;
            }
            catch (Exception) { PatchCounters()[2343]++; }
        }
    }
}
