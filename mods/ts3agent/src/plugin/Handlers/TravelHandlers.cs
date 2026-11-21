using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay;
using Sims3.Gameplay.Academics;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.EventSystem;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Moving;
using Sims3.Gameplay.Objects.Miscellaneous;
using Sims3.Gameplay.Situations;
using Sims3.Gameplay.TimeTravel;
using Sims3.Gameplay.Tutorial;
using Sims3.Gameplay.UI;
using Sims3.Gameplay.Utilities;
using Sims3.Gameplay.Visa;
using Sims3.SimIFace;

namespace Ts3Agent
{
    // Travel between worlds (docs/19): the game's own boarding interactions, queued without the trip planner / phone UI.
    // Every command returns at once; the world switch runs as game tasks and the harness (tools/harness/travel-trip.ts)
    // waits on travel_state / the mailbox world flag. The mailbox is a static of the loader, so it survives the switch.
    public static partial class Handlers
    {
        static void RegisterTravel()
        {
            Dispatcher.Register("travel_vacation", TravelVacation);
            Dispatcher.Register("travel_university", TravelUniversity);
            Dispatcher.Register("travel_future", TravelFuture);
            Dispatcher.Register("portal_prepare", PortalPrepare);
            Dispatcher.Register("travel_home", TravelHome);
            Dispatcher.Register("travel_state", TravelState);
            Dispatcher.Register("uni_move_in", UniMoveIn);
        }

        static bool AwayFromHome()
        {
            return GameUtils.IsAnyTravelBasedWorld() || GameUtils.IsFutureWorld();
        }

        static void CheckCanLeave()
        {
            if (GameStates.IsTravelling) throw new McpException("a world switch is already running");
            if (AwayFromHome()) throw new McpException("already away from the home world (" + GameUtils.GetCurrentWorld() + "); travel_home first");
        }

        // TravelUtil.CheckForReasonsToFailTravel is private; FinalBoardingCall cancels the whole trip if any traveller fails it.
        static string TravelFailReason(SimDescription sd, WorldName world)
        {
            MethodInfo m = typeof(TravelUtil).GetMethod("CheckForReasonsToFailTravel", BindingFlags.NonPublic | BindingFlags.Static, null,
                new Type[] { typeof(SimDescription), typeof(WorldName), typeof(bool) }, null);
            return (string)m.Invoke(null, new object[] { sd, world, false });
        }

        // sims: names/ids (default: every active-household Sim in the world; pets stay home). The lead (first) is the active Sim when it travels.
        static List<Sim> Travellers(Args a, WorldName world, List<object> skipped)
        {
            Sim active = Util.ActiveSim();
            List<Sim> wanted = new List<Sim>();
            List<object> names = a.List("sims");
            if (names != null)
            {
                foreach (object n in names)
                {
                    Dictionary<string, object> d = Util.Obj();
                    d["sim"] = n;
                    wanted.Add(Util.FindSim(new Args(d), "sim"));
                }
            }
            else
            {
                foreach (Sim s in active.Household.AllActors)
                    if (!Household.RoommateManager.IsNPCRoommate(s.SimDescription.SimDescriptionId)) wanted.Add(s);
            }
            List<Sim> list = new List<Sim>();
            // clear_recovery (STRESS shortcut, not a player flow): drop the WentToLocation buff that GameStates adds on the way
            // home and CheckForReasonsToFailTravel reads as "still recovering from the previous vacation", so back-to-back trips
            // can run (NRaas Traveler lets the player skip the same check: TravelUtilEx filter Type.Recovering)
            bool clear = a.Bool("clear_recovery", false);
            foreach (Sim s in wanted)
            {
                // A pet among the travellers drops the whole trip: the car interaction leaves the queue within a minute and the
                // world never switches, with no exception (2026-11-02, Baseline-BP-Spring's Roxy Dog, main and branch alike).
                // EA's trip planner offers no pets for a vacation; CheckForReasonsToFailTravel does not say so.
                if (!s.SimDescription.IsHuman) { skipped.Add(s.FullName + ": pets do not go on vacation"); continue; }
                if (clear && s.BuffManager.HasElement(BuffNames.WentToLocation)) s.BuffManager.RemoveElement(BuffNames.WentToLocation);
                string why = TravelFailReason(s.SimDescription, world);
                if (!string.IsNullOrEmpty(why)) { skipped.Add(s.FullName + ": " + why); continue; }
                if (s == active) list.Insert(0, s); else list.Add(s);
            }
            if (list.Count == 0) throw new McpException("nobody can travel: " + string.Join("; ", skipped.ConvertAll(o => (string)o).ToArray()));
            return list;
        }

        static WorldName VacationWorld(string name)
        {
            switch (name.Trim().ToLowerInvariant())
            {
                case "france": case "champs les sims": return WorldName.France;
                case "china": case "shang simla": return WorldName.China;
                case "egypt": case "al simhara": return WorldName.Egypt;
            }
            throw new McpException("world must be france, china or egypt");
        }

        // travel_vacation {world: france|china|egypt, days? (2), sims?, cost? (0), clear_recovery? (false), alt? (0)}: SitInCarToTriggerTravel, the interaction
        // TravelUtil.TriggerTravelToVacationWorld queues after the trip planner. Targeting the world lot skips the walk to the
        // farthest lot, so the switch starts on the next tick (same FinalBoardingCall + GameStates.TravelToVacationWorld).
        // Skips the planner's visa/duration limits and the ITF "travel with" warning.
        static object TravelVacation(Args a)
        {
            CheckCanLeave();
            WorldName world = VacationWorld(a.Str("world"));
            List<object> skipped = new List<object>();
            List<Sim> sims = Travellers(a, world, skipped);
            // al357: trip 2 was led by Felicity MacDuff (first in household order) and the trip home from France never queued
            // (travel_home triggers it with the active Sim, the lead); trip 1, led by Joe, went. Adults lead first. Cause not
            // confirmed: in al358 the trip home triggered with Felicity went.
            List<Sim> byAge = sims.FindAll(s => s.SimDescription.YoungAdultOrAbove);
            byAge.AddRange(sims.FindAll(s => !s.SimDescription.YoungAdultOrAbove));
            sims = byAge;
            List<ulong> ids = new List<ulong>();
            foreach (Sim s in sims) ids.Add(s.SimDescription.SimDescriptionId);
            // The queue can refuse the lead (al354, al356: the active Sim UWU MacDuff queued false both times; al352, led by Joe
            // MacDuff, went): the next traveller leads instead, the rest follow as before.
            Dictionary<string, object> r = Util.Obj();
            List<object> refused = new List<object>();
            TravelUtil.SitInCarToTriggerTravel trip = null;
            bool queued = false;
            // alt: N starts the lead search N places further down the list. travel-trip.ts sends alt 1 when the trip has not
            // started after ~1 min: in runs of 2026-11-12 the trip queued for one adult was dropped, and asked again through
            // the next adult it went.
            int alt = Math.Max(0, (int)a.Long("alt", 0));
            for (int k = 0; k < sims.Count && !queued; k++)
            {
                int i = (k + alt) % sims.Count;
                Sim lead = sims[i];
                List<Sim> followers = new List<Sim>(sims);
                followers.RemoveAt(i);
                trip = TravelUtil.SitInCarToTriggerTravel.Singleton.CreateInstance(LotManager.GetWorldLot(), lead,
                    new InteractionPriority(InteractionPriorityLevel.High), false, false) as TravelUtil.SitInCarToTriggerTravel;
                trip.IsGoingToUniversity = false;
                trip.Followers = followers;
                trip.TravelingSimGuids = ids;
                trip.TravelCost = (int)a.Double("cost", 0);
                trip.TravelDuration = (int)a.Double("days", 2);
                trip.DestinationWorldName = world;
                lead.InteractionQueue.CancelAllInteractions();
                queued = lead.InteractionQueue.Add(trip);
                if (queued) r["lead"] = lead.FullName; else refused.Add(lead.FullName);
            }
            r["queued"] = queued;
            if (refused.Count > 0) r["lead_refused"] = refused;
            sTripSaveUntil = System.Environment.TickCount + 15 * 60000;  // the travel save's name dialog is accepted (HarnessHandlers RuleFor)
            r["world"] = world.ToString();
            r["days"] = trip.TravelDuration;
            r["sims"] = sims.ConvertAll(s => (object)s.FullName);
            r["skipped"] = skipped;
            return r;
        }

        static AcademicDegreeNames DegreeArg(string name)
        {
            string n = name.Replace("_", "").Replace(" ", "");
            foreach (AcademicDegreeNames d in Enum.GetValues(typeof(AcademicDegreeNames)))
                if (d > AcademicDegreeNames.Undefined && d < AcademicDegreeNames.MaxDegreeNames && string.Equals(d.ToString(), n, StringComparison.OrdinalIgnoreCase))
                    return d;
            throw new McpException("degree must be business, technology, science, fine_arts, communications or phys_ed");
        }

        // EnrollmentDialog.UpdateFunds for one Sim: what the term costs minus scholarship / grant (negative = money back).
        static int TuitionFor(SimDescription sd, AcademicDegreeNames degree, uint courseLoad, int terms)
        {
            AcademicDegreeManager dm = sd.CareerManager.DegreeManager;
            float cost = AcademicDegreeManager.GetStaticElement(degree).CostPerCredit * courseLoad * terms;
            FinancialAidtype aid = dm.GetFinacialAidType();
            bool grant = dm.DoesQualifyForGrant(sd);
            int aidAmount = 0;
            if (aid == FinancialAidtype.HonorsScholarship) aidAmount = AcademicDegreeManager.HonorsScholarshipAmount() + (int)cost;
            else if (aid == FinancialAidtype.FullScholarship || aid == FinancialAidtype.PartialScholarship)
            {
                aidAmount = (aid == FinancialAidtype.FullScholarship ? AcademicDegreeManager.FullScholarshipAmount() : AcademicDegreeManager.PartialScholarshipAmount()) * terms;
                if (grant && AcademicDegreeManager.NeedBasedGrantAmount() > aidAmount) aidAmount = AcademicDegreeManager.NeedBasedGrantAmount();
            }
            else if (grant) aidAmount = AcademicDegreeManager.NeedBasedGrantAmount();
            return (int)cost - aidAmount;
        }

        // travel_university {sims?, degree?, course_load? (1), terms? (1), days?, dialog?}: enrols without the enrolment dialog,
        // the way its accept path does (AcademicCareer.EnrollInAcademicCareer after EnrollmentDialog.Show): per Sim
        // DegreeManager.SetEnrollmentData + kEnrolledInUniversity event, GlobalTermLength, PlayerMadeTravelRequest, then
        // TravelUtil.TriggerTravelToUniversityWorld (moving van) with the dialog's tuition. sims default: the active Sim alone
        // (the dialog's preselection); degree default: each Sim's last studied degree, else Business. dialog: true = old path.
        // direct: true = no moving van: after the enrolment, UniversityDirect starts the world switch at once.
        static object TravelUniversity(Args a)
        {
            if (a.Bool("dialog", false)) return TravelUniversityDialog(a);
            if (GameUtils.IsUniversityWorld()) throw new McpException("already at University; travel_home first");
            CheckCanLeave();
            if (!GameUtils.IsInstalled(ProductVersion.EP9)) throw new McpException("University Life is not installed");
            if (TravelUtil.PlayerMadeTravelRequest) throw new McpException("another trip is already requested");
            if (MovingSituation.MovingInProgress) throw new McpException("a household move is in progress");
            if (IntroTutorial.IsRunning) throw new McpException("the intro tutorial is running");
            Sim active = Util.ActiveSim();
            Household hh = active.Household;
            if (hh == null || hh != Household.ActiveHousehold || hh.LotHome == null)
                throw new McpException("the active household must live on a home lot");
            int terms = (int)a.Double("terms", 1);
            if (terms < 1 || terms > 2) throw new McpException("terms must be 1 or 2 (the dialog's choices)");
            int courseLoadArg = (int)a.Double("course_load", 1);
            if (courseLoadArg < 1 || courseLoadArg > 4) throw new McpException("course_load must be 1 to 4");
            uint courseLoad = (uint)courseLoadArg;
            bool fixedDegree = a.Has("degree");
            AcademicDegreeNames degreeArg = fixedDegree ? DegreeArg(a.Str("degree")) : AcademicDegreeNames.Undefined;
            int days = a.Has("days") ? (int)a.Double("days") : 0;
            bool direct = a.Bool("direct", false);
            sMoveInHousehold = ulong.MaxValue; // uni_move_in: a new trip, a new arrival

            List<Sim> wanted = new List<Sim>();
            List<object> names = a.List("sims");
            if (names == null) wanted.Add(active);
            else foreach (object n in names)
            {
                Dictionary<string, object> d = Util.Obj();
                d["sim"] = n;
                Sim s = Util.FindSim(new Args(d), "sim");
                if (!wanted.Contains(s)) wanted.Add(s);
            }
            List<SimDescription> students = new List<SimDescription>();
            List<AcademicDegreeNames> degrees = new List<AcademicDegreeNames>();
            List<object> plan = new List<object>();
            int tuition = 0;
            foreach (Sim s in wanted)
            {
                SimDescription sd = s.SimDescription;
                if (s.Household != hh || Household.RoommateManager.IsNPCRoommate(sd)) throw new McpException(s.FullName + " is not in the active household");
                if (!sd.YoungAdultOrAdult) throw new McpException(s.FullName + " must be a young adult or adult");
                if (sd.CareerManager == null || sd.CareerManager.DegreeManager == null) throw new McpException(s.FullName + " has no degree manager");
                GreyedOutTooltipCallback why = null;
                if (!TravelUtil.CanSimTriggerTravelToUniversityWorld(s, ref why))
                    throw new McpException(s.FullName + " cannot go to University now" + (why != null ? ": " + why() : ""));
                AcademicDegreeNames degree = degreeArg;
                if (!fixedDegree)
                {
                    degree = (AcademicDegreeNames)sd.LastStudiedDegree;
                    if (degree <= AcademicDegreeNames.Undefined || degree >= AcademicDegreeNames.MaxDegreeNames) degree = AcademicDegreeNames.Business;
                }
                if (AcademicDegreeManager.GetStaticElement(degree) == null) throw new McpException("no degree data for " + degree);
                if (sd.CareerManager.DegreeManager.HasCompletedDegree(degree)) throw new McpException(s.FullName + " already has the " + degree + " degree");
                int cost = TuitionFor(sd, degree, courseLoad, terms);
                tuition += cost;
                students.Add(sd);
                degrees.Add(degree);
                plan.Add(s.FullName + ": " + degree + ", tuition " + cost);
            }
            if (hh.FamilyFunds < tuition) throw new McpException("household funds " + hh.FamilyFunds + " < tuition " + tuition);

            // Same steps as the accept path, minus the modal dialogs (EnrollmentDialog, its AcceptCancelDialog, the SUAT note).
            RunAsync("travel_university", delegate
            {
                TravelUtil.PlayerMadeTravelRequest = true;
                bool queued = false;
                try
                {
                    for (int i = 0; i < students.Count; i++)
                    {
                        students[i].CareerManager.DegreeManager.SetEnrollmentData(degrees[i], courseLoad);
                        EventTracker.SendEvent(new AcademicEvent(EventTypeId.kEnrolledInUniversity, students[i].CreatedSim, degrees[i]));
                    }
                    AcademicCareer.GlobalTermLength = (AcademicCareer.TermLength)terms;
                    // queued stays false, so the finally clears PlayerMadeTravelRequest the way the van's Run does
                    if (direct) { UniversityDirect(students, tuition, days > 0 ? days : terms * 7); return; }
                    TravelUtil.TriggerTravelToUniversityWorld(students, tuition);
                    foreach (SimDescription sd in students)
                    {
                        if (sd.CreatedSim == null) continue;
                        foreach (InteractionInstance i in sd.CreatedSim.InteractionQueue.InteractionList)
                        {
                            TravelUtil.SitInMovingVanToTriggerTravelToUniversity van = i as TravelUtil.SitInMovingVanToTriggerTravelToUniversity;
                            if (van == null) continue;
                            queued = true;
                            if (days > 0) van.TravelDuration = days;
                        }
                    }
                    if (!queued) throw new Exception("TriggerTravelToUniversityWorld queued no moving van");
                }
                finally
                {
                    // the van's Run/Cleanup clears it; without a van nothing would, and every later trip is greyed out
                    if (!queued) TravelUtil.PlayerMadeTravelRequest = false;
                }
            });
            Dictionary<string, object> r = Util.Obj();
            r["enrolling"] = plan;
            r["terms"] = terms;
            r["course_load"] = courseLoadArg;
            r["tuition"] = tuition;
            r["days"] = days > 0 ? (object)days : terms * 7;
            r["direct"] = direct;
            return r;
        }

        // direct: the end of SitInMovingVanToTriggerTravelToUniversity.Run (its private TriggerTravelToUniversityWorld) without
        // the walk to the gathering point, the moving van and the suitcase: FinalBoardingCall, tuition, kill the visitor
        // situations, map view, GameStates.TravelToVacationWorld(University). Also TriggerTravelToUniversityWorld's EP11 step.
        // Skips the see-off interactions of the Sims who stay home.
        static void UniversityDirect(List<SimDescription> students, int tuition, int days)
        {
            Household hh = Household.ActiveHousehold;
            List<Sim> sims = new List<Sim>();
            List<ulong> ids = new List<ulong>();
            foreach (SimDescription sd in students)
            {
                if (sd.CreatedSim == null) throw new Exception(sd.FullName + " is not in the world");
                sims.Add(sd.CreatedSim);
                ids.Add(sd.SimDescriptionId);
            }
            if (GameUtils.IsInstalled(ProductVersion.EP11) && CauseEffectService.OpportunityPortal != null)
                CauseEffectService.OpportunityPortal.RemoveBannedOpportunities(sims);
            if (!TravelUtil.FinalBoardingCall(hh, sims, WorldName.University, false))
            {
                List<string> why = new List<string>();
                foreach (Sim s in sims) why.Add(s.FullName + ": " + TravelFailReason(s.SimDescription, WorldName.University));
                throw new Exception("FinalBoardingCall refused the trip (" + string.Join("; ", why.ToArray()) + ")");
            }
            sims[0].ModifyFunds(-tuition);
            ForeignVisitorsSituation.ForceKillForeignVisitorsSituation();
            HolographicProjectionSituation.ForceKillHolographicVisitorsSituation();
            Sims3.Gameplay.Core.Camera.SetView(Sims3.UI.CameraView.MapView, false, true);
            int last = int.MinValue;
            foreach (Sim s in sims) if (s.DegreeManager != null && s.DegreeManager.LastTimeAtUniversity > last) last = s.DegreeManager.LastTimeAtUniversity;
            int daysSince = last >= 0 ? (int)SimClock.ElapsedCalendarDays() - last : -1;
            GameStates.TravelToVacationWorld(WorldName.University, ids, TravelUtil.sOverriddenTripLength > 0 ? TravelUtil.sOverriddenTripLength : days, daysSince);
        }

        // travel_university {dialog: true, days?}: the phone's "Enroll for University" path: AcademicCareer.EnrollInAcademicCareer
        // (opens the enrolment dialog: answer it with auto_dialog / respond_dialog), then TravelUtil.TriggerTravelToUniversityWorld
        // (moving van). days overrides the van's trip length (default: the chosen term length x 7). Runs as a game task (modal
        // dialog). Crashed once 6 s after the dialog (PLAN.md, 2026-10-15 15:38 PHT).
        static object TravelUniversityDialog(Args a)
        {
            CheckCanLeave();
            Sim lead = Util.ActiveSim();
            GreyedOutTooltipCallback why = null;
            if (!TravelUtil.CanSimTriggerTravelToUniversityWorld(lead, ref why))
                throw new McpException(lead.FullName + " cannot go to University now" + (why != null ? ": " + why() : ""));
            int days = a.Has("days") ? (int)a.Double("days") : 0;
            RunAsync("travel_university", delegate
            {
                List<SimDescription> enrolled;
                int tuition;
                if (!AcademicCareer.EnrollInAcademicCareer(lead, out enrolled, out tuition) || enrolled == null)
                    throw new Exception("enrolment cancelled");
                TravelUtil.TriggerTravelToUniversityWorld(enrolled, tuition);
                if (days <= 0) return;
                foreach (SimDescription sd in enrolled)
                {
                    if (sd.CreatedSim == null) continue;
                    foreach (InteractionInstance i in sd.CreatedSim.InteractionQueue.InteractionList)
                    {
                        TravelUtil.SitInMovingVanToTriggerTravelToUniversity van = i as TravelUtil.SitInMovingVanToTriggerTravelToUniversity;
                        if (van != null) van.TravelDuration = days;
                    }
                }
            });
            Dictionary<string, object> r = Util.Obj();
            r["enrolling"] = lead.FullName;
            r["days"] = days > 0 ? (object)days : "term";
            return r;
        }

        // A placed portal (not one in an inventory), the active household's own first.
        static TimePortal FindTimePortal()
        {
            if (!GameUtils.IsInstalled(ProductVersion.EP11)) throw new McpException("Into the Future is not installed");
            TimePortal found = null;
            Household hh = Household.ActiveHousehold;
            foreach (TimePortal p in Sims3.Gameplay.Queries.GetObjects<TimePortal>())
            {
                if (!p.InWorld || p.InInventory) continue;
                if (hh != null && hh.LotHome != null && p.LotCurrent == hh.LotHome) return p;
                if (found == null) found = p;
            }
            if (found == null) throw new McpException("no time portal placed in this world (the Into the Future intro spawns one, or place it from an inventory)");
            return found;
        }

        // The portal conditions TimePortal.Travel's Test checks (besides PlayerMadeTravelRequest and the Sim's own state).
        static Dictionary<string, object> PortalReport(TimePortal p)
        {
            Dictionary<string, object> r = Util.Obj();
            FieldInfo st = typeof(TimePortal).GetField("mCurState", BindingFlags.NonPublic | BindingFlags.Instance);
            r["state"] = st != null ? st.GetValue(p).ToString() : null;
            r["portal_active"] = p.Active;
            r["portal_broken"] = p.IsBroken;
            r["traveler_summoned"] = p.HasTimeTravelerBeenSummoned();
            CauseEffectService ce = CauseEffectService.GetInstance();
            r["almanac"] = ce != null ? (object)ce.ShouldShowTimeAlmanacButton() : null;
            r["in_use"] = p.InUse;
            return r;
        }

        // portal_prepare (test saves): puts the placed time portal into the state TimePortal.Travel needs, using the end
        // effects of the game's own chain (SummonTimeTraveler -> ActivateDeactivate -> TimeTravelerSituation -> GiveAlmanac)
        // without the traveler's visit or the animations. Persists into the next save (sTimeTravelerHasBeenSummoned is a
        // PersistableStatic, the almanac flag is CauseEffectService persistable data).
        static object PortalPrepare(Args a)
        {
            if (GameStates.IsTravelling) throw new McpException("a world switch is already running");
            TimePortal p = FindTimePortal();
            if (p.InUse) throw new McpException("the time portal is in use; wait for that interaction to end");
            CauseEffectService ce = CauseEffectService.GetInstance();
            if (ce == null) throw new McpException("no CauseEffectService in this world (Into the Future not running)");
            Dictionary<string, object> r = Util.Obj();
            r["portal"] = p.ObjectId.ToString();
            r["lot"] = p.LotCurrent != null ? p.LotCurrent.Name : null;
            r["before"] = PortalReport(p);
            List<object> did = new List<object>();
            // repair: what SocialCallback.PushTimeTravelerRepair and HelpTimeTravelerOpportunityCommon's cleanup do
            if (p.IsBroken) { p.IsBroken = false; did.Add("repaired"); }
            // summon: TimeTravelerSituation.OnAppearComplete; ActivateDeactivate.Run links the portal for the opportunities
            if (!p.HasTimeTravelerBeenSummoned()) { p.SetTimeTravelerSummoned(); did.Add("summoned"); }
            if (CauseEffectService.OpportunityPortal == null) CauseEffectService.OpportunityPortal = p;
            // almanac: GiveAlmanac.Cleanup (the traveler hands it over after the summon); Travel's Test needs the button
            if (!ce.ShouldShowTimeAlmanacButton())
            {
                ce.EnableTimeAlmanacButton();
                HudModel hud = Sims3.UI.Responder.Instance.HudModel as HudModel;
                if (hud != null) hud.RequestUiTimeAlmanacUpdate();
                did.Add("almanac");
            }
            // activate: ActivateDeactivate's anim event 101 -> private SwitchActiveState (Active, or Rainbow/Distopia by world state)
            if (!p.Active)
            {
                typeof(TimePortal).GetMethod("SwitchActiveState", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(p, null);
                did.Add("activated");
            }
            r["changed"] = did;
            r["after"] = PortalReport(p);
            return r;
        }

        // The portal's own "Travel" interaction (private TimePortal.Travel): confirm dialog (auto_dialog), walk in, then
        // TimePortal.TravelToFuture -> GameStates.TravelToVacationWorld(FutureWorld); in the future world it returns home.
        static object QueuePortalTravel(Sim s)
        {
            TimePortal portal = FindTimePortal();
            Type travel = typeof(TimePortal).GetNestedType("Travel", BindingFlags.NonPublic);
            InteractionDefinition def = (InteractionDefinition)travel.GetField("Singleton", BindingFlags.Public | BindingFlags.Static).GetValue(null);
            InteractionInstance i = def.CreateInstance(portal, s, new InteractionPriority(InteractionPriorityLevel.High), false, true);
            s.InteractionQueue.CancelAllInteractions();
            Dictionary<string, object> r = Util.Obj();
            r["queued"] = s.InteractionQueue.Add(i);
            r["sim"] = s.FullName;
            // Travel.Run returns false at once (interaction gone, no message) while this is false or the tutorial runs
            r["modal_ok"] = Sims3.UI.UIUtils.IsOkayToStartModalDialog();
            // the interaction's Test (not run here) also needs these; portal_prepare sets them
            foreach (KeyValuePair<string, object> kv in PortalReport(portal)) r[kv.Key] = kv.Value;
            return r;
        }

        // travel_future {sim?, prepare?, direct?}: the Sim (default the active Sim) alone through the time portal (TravelWith, the group version, is not
        // wired). prepare: true runs portal_prepare first (test saves without a summoned traveler or an active portal).
        // direct: true skips the queued interaction (confirm dialog, walk, animations) and starts the switch from a fresh
        // one-shot task in the order of docs/77 §4.2 (FutureDirect). Default: the queued portal Travel interaction.
        static object TravelFuture(Args a)
        {
            CheckCanLeave();
            object prepared = a.Bool("prepare", false) ? PortalPrepare(a) : null;
            Sim s = a.Has("sim") ? Util.FindSim(a, "sim") : Util.ActiveSim();  // the story's traveller role (docs/171 §16)
            GreyedOutTooltipCallback why = null;
            if (!TravelUtil.CanSimTriggerTravelToFutureWorld(s, ref why))
                throw new McpException(s.FullName + " cannot travel to the future now" + (why != null ? ": " + why() : ""));
            Dictionary<string, object> r;
            if (a.Bool("direct", false))
            {
                TimePortal portal = FindTimePortal();
                if (portal.IsBroken || !portal.Active) throw new McpException("the time portal is broken or inactive; pass prepare: true");
                if (TravelUtil.PlayerMadeTravelRequest) throw new McpException("another trip is already requested");
                FutureSwitch sw = new FutureSwitch(); // resolve every private member now: a game update fails the command, not the trip
                RunAsync("travel_future direct", delegate { FutureDirect(s, portal, sw); });
                r = PortalReport(portal);
                r["sim"] = s.FullName;
                r["direct"] = true;
            }
            else r = (Dictionary<string, object>)QueuePortalTravel(s);
            if (prepared != null) r["prepare"] = prepared;
            return r;
        }

        // The private members of GameStates.TravelToVacationWorld (data, flag, switch task) and TimePortal's boarding helpers.
        class FutureSwitch
        {
            const BindingFlags SP = BindingFlags.NonPublic | BindingFlags.Static, IP = BindingFlags.NonPublic | BindingFlags.Instance;
            public readonly FieldInfo Data = Need(typeof(GameStates).GetField("sTravelData", SP), "GameStates.sTravelData");
            public readonly FieldInfo Flag = Need(typeof(GameStates).GetField("sIsChangingWorlds", SP), "GameStates.sIsChangingWorlds");
            public readonly MethodInfo StopOps = Need(typeof(GameStates).GetMethod("StopOperationsThatCannotTravel", SP), "GameStates.StopOperationsThatCannotTravel");
            public readonly MethodInfo Switch = Need(typeof(GameStates).GetMethod("SwitchWorlds", SP), "GameStates.SwitchWorlds");
            public readonly Type DataType = Need(typeof(GameStates).GetNestedType("TravelData", BindingFlags.NonPublic), "GameStates.TravelData");
            public readonly MethodInfo Vehicles = Need(typeof(TimePortal).GetMethod("CleanUpReservedVehicles", IP), "TimePortal.CleanUpReservedVehicles");
            public readonly MethodInfo StopFx = Need(typeof(TimePortal).GetMethod("StopActiveFX", IP), "TimePortal.StopActiveFX");

            static T Need<T>(T m, string name) where T : class
            {
                if (m == null) throw new McpException(name + " not found (game version?)");
                return m;
            }
        }

        static void SetField(object o, string name, object v)
        {
            FieldInfo f = o.GetType().GetField(name);
            if (f == null) throw new Exception("GameStates.TravelData." + name + " not found");
            f.SetValue(o, v);
        }

        // Direct future trip (docs/77 §4.2, Traveler's order in our own code): EA's TravelToVacationWorld sets
        // sIsChangingWorlds before the unguarded descendant build, so a throw there leaves the game "travelling" with no
        // switch queued. Here: boarding call, then the future-world preparation in a try/catch with no flag set yet, then
        // the trip data, the flag and EA's own SwitchWorlds task. Any failure after the flag clears it again.
        // Runs on its own one-shot task (RunAsync), never inside an interaction.
        static void FutureDirect(Sim s, TimePortal portal, FutureSwitch sw)
        {
            TravelUtil.PlayerMadeTravelRequest = true;
            bool flagged = false;
            try
            {
                EventTracker.SendEvent(EventTypeId.kTravelToFuture, s);
                Household hh = s.Household;
                List<Sim> followers = new List<Sim>(); // travel_future takes the active Sim alone
                if (!TravelUtil.FinalBoardingCall(hh, followers, WorldName.FutureWorld, false))
                    throw new Exception("FinalBoardingCall refused the trip: " + TravelFailReason(s.SimDescription, WorldName.FutureWorld));
                try
                {
                    CauseEffectService.GetInstance().PreFutureWorldLoadProcess();
                    FutureDescendantService.GetInstance().BuildDescendantHouseholdSpecs();
                }
                catch (Exception e)
                {
                    throw new Exception("future-world preparation threw (PreFutureWorldLoadProcess / BuildDescendantHouseholdSpecs); trip stopped before sIsChangingWorlds", e);
                }
                // TimePortal.TravelToFuture's boarding side effects
                ForeignVisitorsSituation.ForceKillForeignVisitorsSituation();
                HolographicProjectionSituation.ForceKillHolographicVisitorsSituation();
                Sims3.Gameplay.Core.Camera.SetView(Sims3.UI.CameraView.MapView, false, true);
                sw.Vehicles.Invoke(portal, new object[] { s, followers });
                sw.StopFx.Invoke(portal, null);
                CauseEffectService.GetInstance().SetDepartureTimePortal(portal.ObjectId);
                // GameStates.TravelToVacationWorld(FutureWorld, ids, 0, 0) minus the future-world step done above
                sw.StopOps.Invoke(null, null);
                object data = Activator.CreateInstance(sw.DataType, true);
                Type state = sw.DataType.GetNestedType("TravelState");
                SetField(data, "mState", Enum.Parse(state, "StartVacation"));
                SetField(data, "mTravelerIds", new List<ulong>(new ulong[] { s.SimDescription.SimDescriptionId }));
                SetField(data, "mHomeWorld", GameStates.GetCurrentWorldName(true));
                SetField(data, "mHomeWorldMetadataName", GameStates.GetCurrentWorldName(false));
                SetField(data, "mDestWorld", WorldName.FutureWorld);
                SetField(data, "mTripLength", 0);
                SetField(data, "mCurrentDayOfTrip", 0);
                SetField(data, "mTimeInHomeworld", SimClock.CurrentTime());
                SetField(data, "mNumDaysSinceLastInDestWorld", 0);
                SetField(data, "mNumberAgingYearsElapsed", AgingManager.NumberAgingYearsElapsed != -1f ? AgingManager.NumberAgingYearsElapsed : 0f);
                SetField(data, "mRealEstateManager", hh.RealEstateManager);
                sw.Data.SetValue(null, data);
                sw.Flag.SetValue(null, true);
                flagged = true;
                hh.RealEstateManager.SavePropertyNamesForTravel();
                // a delegate, not Invoke: SwitchWorlds yields through the loads and saves
                Sims3.Gameplay.Function f = (Sims3.Gameplay.Function)Delegate.CreateDelegate(typeof(Sims3.Gameplay.Function), sw.Switch);
                Simulator.AddObject(new Sims3.Gameplay.OneShotFunctionTask(f));
                flagged = false; // queued: SwitchWorlds owns the flag from here
            }
            finally
            {
                if (flagged) { sw.Data.SetValue(null, null); sw.Flag.SetValue(null, false); }
                TravelUtil.PlayerMadeTravelRequest = false;
            }
        }

        // uni_move_in {lot?}: the University arrival step (docs/77 §2.1). After a University trip the game waits in the
        // play-flow "choose a lot" map; this does what confirming a lot there does: PlayFlowModel.PlayWithTravelHousehold
        // on a game task. Dorm only (Lot.IsDormitoryLot; a rented house has a rent step we have not traced). lot: a dorm lot
        // id, default the first dorm. Poll it: answers {done} once live mode has the household, {waiting: why} until the
        // map is ready (fly-through, open dialog: answer it with auto_dialog), {started, lot} once, {started_before} after.
        static ulong sMoveInHousehold = ulong.MaxValue;
        static object UniMoveIn(Args a)
        {
            Dictionary<string, object> r = Util.Obj();
            if (!GameUtils.IsUniversityWorld()) throw new McpException("not in the University world (" + GameUtils.GetCurrentWorld() + ")");
            if (!GameStates.IsTravelling && Household.ActiveHousehold != null && !GameStates.IsPlayFlowState)
            {
                sMoveInHousehold = ulong.MaxValue;
                r["done"] = true;
                r["household"] = Household.ActiveHousehold.Name;
                return r;
            }
            string waiting = null;
            PlayFlowModel model = PlayFlowModel.Singleton;
            Household travel = GameStates.TravelHousehold;
            if (!GameStates.IsPlayFlowState) waiting = "not in the play-flow state yet";
            else if (model == null || !model.IsInitialized) waiting = "play-flow model not ready (fly-through)";
            else if (travel == null) waiting = "no travel household";
            else if (Sims3.UI.UIManager.GetModalWindow() != null) waiting = "a dialog is open";
            if (waiting != null) { r["waiting"] = waiting; return r; }
            if (sMoveInHousehold == travel.HouseholdId) { r["started_before"] = true; return r; }
            Lot lot = null;
            if (a.Has("lot"))
            {
                lot = LotManager.GetLot(a.ULong("lot"));
                if (lot == null || !lot.IsDormitoryLot) throw new McpException("lot " + a.Str("lot") + " is not a dorm lot (dorm only)");
            }
            else foreach (Lot l in LotManager.AllLots) if (l.IsDormitoryLot) { lot = l; break; }
            if (lot == null) throw new McpException("no dorm lot in this world");
            sMoveInHousehold = travel.HouseholdId;
            ulong id = lot.LotId;
            RunAsync("uni_move_in " + id, delegate { model.PlayWithTravelHousehold(id); });
            r["started"] = true;
            r["lot"] = id.ToString();
            r["lot_name"] = lot.Name;
            r["household"] = travel.Name;
            return r;
        }

        // travel_home {direct?, alt? (0)}: everyone returns early, the way the game's own UI does it per world type. alt: N (vacation
        // worlds only) asks through the N-th other young-adult-or-older household Sim instead.
        // Future world: the portal's Travel ("Travel Home") needs an Active portal, which only the arrival animation
        // (TimePortal.Arrive, anim event 201 -> SwitchActiveState) sets. [inferred] Arrive never ran or was cancelled: it
        // is pushed 1 Sim minute after arrival only when the household lot has a portal (else a fade-in), and the harness
        // cancels all actions before the trip back. Seen: portal Inactive, Travel Test refused (2026-10-20 release-validate,
        // queued false). So portal_prepare does that activation first. direct: true skips the interaction and the portal and
        // runs Travel.Run's future-world tail (kTravelToPresent, GameStates.ReturnFromVacationWorld) on a one-shot task.
        static object TravelHome(Args a)
        {
            if (GameStates.IsTravelling) throw new McpException("a world switch is already running");
            sTripSaveUntil = System.Environment.TickCount + 15 * 60000;  // as travel_vacation: the trip's save name dialog is accepted
            Sim s = Util.ActiveSim();
            if (GameUtils.IsFutureWorld())
            {
                if (TravelUtil.PlayerMadeTravelRequest) throw new McpException("another trip is already requested");
                if (!a.Bool("direct", false))
                {
                    object prepared = PortalPrepare(a);
                    Dictionary<string, object> q = (Dictionary<string, object>)QueuePortalTravel(s);
                    q["prepare"] = prepared;
                    return q;
                }
                s.InteractionQueue.CancelAllInteractions();
                RunAsync("travel_home future direct", delegate
                {
                    EventTracker.SendEvent(EventTypeId.kTravelToPresent, s);
                    GameStates.ReturnFromVacationWorld();
                });
                Dictionary<string, object> d = Util.Obj();
                d["sim"] = s.FullName;
                d["direct"] = true;
                d["via"] = "ReturnFromVacationWorld (TimePortal.Travel future-world tail)";
                return d;
            }
            Dictionary<string, object> r = Util.Obj();
            if (GameUtils.IsUniversityWorld())
            {
                // The phone/computer "Leave University" call (Phone.CallLeaveUniversity.OnPhoneFinished), minus its yes/no
                // dialog: drop every student out, then travellers = the household minus NPC dorm roommates, who stay and see
                // them off. Without the drop-out the moving van takes the early-single-departure path (a student still in
                // an academic career leaves alone: removed from the household, no world switch) and the game sits with no
                // active household (docs/77 §2.3, 2026-10-18 run).
                Household hh = Household.ActiveHousehold;
                if (hh == null) throw new McpException("no active household");
                List<Sim> travellers = new List<Sim>(), stay = new List<Sim>();
                foreach (Sim m in hh.AllActors)
                    if (Household.RoommateManager.IsNPCRoommate(m)) stay.Add(m); else travellers.Add(m);
                if (travellers.Count == 0) throw new McpException("only NPC roommates in the active household");
                RunAsync("travel_home university", delegate
                {
                    HudModel hud = Sims3.UI.Responder.Instance.HudModel as HudModel;
                    foreach (Sim m in travellers)
                    {
                        if (m.OccupationAsAcademicCareer == null) continue;
                        AcademicDegreeManager.DropOut(m);
                        if (hud != null) hud.OnSimCurrentWorldChanged(false, m.SimDescription);
                    }
                    foreach (Sim m in travellers) if (m.OccupationAsAcademicCareer != null) throw new Exception(m.FullName + " still enrolled after DropOut");
                    TravelUtil.TriggerTravelHomeFromUniversity(travellers, stay);
                });
                r["via"] = "LeaveUniversity (DropOut + TriggerTravelHomeFromUniversity)";
                r["travellers"] = travellers.ConvertAll(m => (object)m.FullName);
                r["staying"] = stay.ConvertAll(m => (object)m.FullName);
            }
            else if (GameUtils.IsOnVacation())
            {
                GreyedOutTooltipCallback why = null;
                // al357 round trip 2: triggered with Felicity MacDuff, the trip home never reached the queue; an adult triggers it
                if (!s.SimDescription.YoungAdultOrAbove && Household.ActiveHousehold != null)
                    foreach (Sim m in Household.ActiveHousehold.AllActors)
                        if (m.SimDescription.YoungAdultOrAbove && TravelUtil.CanSimTriggerTravelToHomeWorld(m, ref why)) { s = m; break; }
                // The trip home sometimes never starts for the Sim that asked. travel-trip.ts then asks again with alt 1,
                // through another young-adult-or-older household Sim (the game's check below still applies to that Sim).
                int alt = (int)a.Long("alt", 0);
                if (alt > 0 && Household.ActiveHousehold != null)
                {
                    List<Sim> others = new List<Sim>();
                    foreach (Sim m in Household.ActiveHousehold.AllActors)
                        if (m != s && m.SimDescription.YoungAdultOrAbove) others.Add(m);
                    if (others.Count > 0) s = others[(alt - 1) % others.Count];
                }
                why = null;
                if (!TravelUtil.CanSimTriggerTravelToHomeWorld(s, ref why))
                    throw new McpException("game refuses the trip home" + (why != null ? ": " + why() : ""));
                s.InteractionQueue.CancelAllInteractions();
                TravelUtil.TriggerTravelToHomeWorld(s, true, null);
                r["via"] = "TriggerTravelToHomeWorld";
            }
            else throw new McpException("not away from home (" + GameUtils.GetCurrentWorld() + ")");
            r["sim"] = s.FullName;
            return r;
        }

        static int MiniSimCount()
        {
            FieldInfo f = typeof(MiniSimDescription).GetField("sMiniSims", BindingFlags.NonPublic | BindingFlags.Static);
            ICollection d = f != null ? f.GetValue(null) as ICollection : null;
            return d != null ? d.Count : 0;
        }

        // travel_state: where the game is and how the trip stands; readable during most of a switch (check in_travel).
        static object TravelState(Args a)
        {
            Dictionary<string, object> r = Util.Obj();
            r["in_world"] = GameStates.IsInWorld();
            r["in_travel"] = GameStates.IsTravelling;
            r["world"] = GameUtils.GetCurrentWorld().ToString();
            r["world_type"] = GameUtils.GetCurrentWorldType().ToString();
            r["world_file"] = World.GetWorldFileName();
            r["away"] = AwayFromHome();
            r["destination"] = GameStates.DestinationTravelWorld.ToString();
            r["on_vacation"] = GameStates.IsOnVacation;
            int len = GameStates.TripLength, day = GameStates.CurrentDayOfTrip;
            r["trip_length"] = len;
            r["day_of_trip"] = day;
            r["days_left"] = len > 0 ? (object)Math.Max(0, len - day) : null;
            r["minisims"] = MiniSimCount();
            r["sims_in_world"] = SimDescription.GetSimDescriptionsInWorld().Count;
            Household hh = Household.ActiveHousehold;
            r["household"] = hh != null ? hh.Name : null;
            if (hh != null)
            {
                int inWorld = 0;
                foreach (SimDescription sd in hh.AllSimDescriptions) if (sd.CreatedSim != null) inWorld++;
                r["household_members"] = hh.AllSimDescriptions.Count;
                r["household_in_world"] = inWorld;
            }
            List<ulong> ids = GameStates.TravelerIds;
            r["travelers"] = ids != null ? ids.Count : 0;
            return r;
        }
    }
}
