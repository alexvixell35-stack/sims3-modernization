using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.ActiveCareer.ActiveCareers;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.EventSystem;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.InteractionsShared;
using Sims3.Gameplay.Objects;
using Sims3.Gameplay.Objects.Alchemy;
using Sims3.Gameplay.Objects.CookingObjects;
using Sims3.Gameplay.Objects.Miscellaneous;
using Sims3.Gameplay.Objects.Vehicles;
using Sims3.Gameplay.Situations;
using Sims3.Gameplay.Roles;
using Sims3.Gameplay.Skills;
using Sims3.Gameplay.Socializing;
using Sims3.Gameplay.ObjectComponents;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // Batch 12a (docs/590 usage sweep 2, outings, venues and home objects): EA bug fixes as EA intended, household included
    // (user rule "fix all"). All default off. Slots 3064-3114. simler90 UPDATE242 leaves every hooked method as EA.
    // Not built: B1/B16 (generic class, needs an IL patch), B7 (false positive: the property stream tags each key with its
    // type, so Uint32 and Int32 under 177446789 are two entries), B11 (money flow spans several sites, needs its own pass),
    // A1 and A12 WildHorses (EP branch dogswimaroundlist / wildhorselot), A4 (re-roll is inside a 300-line Run, needs an IL
    // patch), A5 (Plant.GetStateMachine never returns null, the branch is dead), A8 (pick rule unspecified), A9 (inferred),
    // A11 (TendGarden<T> is generic), B17/A12 rest (one-line leads without notes).
    public static partial class Fixes
    {
        delegate bool B12BoolFn();

        static FieldInfo B12F(Type t, string n) { return t == null ? null : t.GetField(n, kB11All); }
        static MethodInfo B12M(Type t, string n, params Type[] a) { return t == null ? null : t.GetMethod(n, kB11All, null, a, null); }

        // Live-path assert for the checks: the hook's own swap is in place (Fixes.Patch recorded it in sPatched).

        // A check line from the hook's own decision, with the live-path assert folded into "members found".
        static object B12Check(string what, string name, int first, string[] cw, string missing, string fixedText, bool fixedPure, string ctlText, bool controls)
        {
            string counters = B582Counters(name, first, cw, missing);
            if (what == "result") return counters;
            if (what != "run") return name + " <run|result>";
            bool on = ShortcutOn(name);
            return B582Line(name, on, fixedText, on && fixedPure, ctlText, controls, missing, counters);
        }

        // ---- B12 §1 protestexit (docs/590 B2) ------------------------------------------------------------------------
        // [verified, decompiled] ImprovedProtestSituation.CancelNPCProtest calls CleanUp + Dispose on the root only; the
        // child BaseProtest keeps its static Sim.OnLotChangedEvent handler (removed only in its own Dispose), so every later
        // lot change of any Sim runs a disposed protest's handler. Fix: a protest with a child is ended with Exit(), which
        // cleans and disposes leaf to root as EA does everywhere else. Counters: [3064] exits, [3065] root-only cancels
        // (switch off), [3066] errors.
        static void ApplyProtestExit()
        {
            Patch(typeof(ImprovedProtestSituation), "CancelNPCProtest", new Type[] { typeof(Lot) }, typeof(B12PeHook), "CancelHook", "CancelOrig");
        }

        internal static bool PeExit(bool protest, bool child) { return protest && child; }

        public class B12PeHook
        {
            public static void CancelHook(Lot onLot)
            {
                ImprovedProtestSituation p = null;
                try { if (onLot != null) p = ImprovedProtestSituation.FindProtest(onLot); } catch (Exception) { PatchCounters()[3066]++; }
                if (p == null || !PeExit(true, p.Child != null)) { CancelOrig(onLot); return; }
                if (!ShortcutOn("protestexit")) { PatchCounters()[3065]++; CancelOrig(onLot); return; }
                try { p.Exit(); PatchCounters()[3064]++; }
                catch (Exception) { PatchCounters()[3066]++; }  // no second EA pass: CleanUp already ran on part of the chain
            }

            public static void CancelOrig(Lot onLot) { }
        }

        // ---- B12 §2 jogterrain (docs/590 B3) -------------------------------------------------------------------------
        // [verified, decompiled] Terrain.GoJogging is Interaction<Sim, Sim> (its Target is the jogger); JogLoop, the 1-minute
        // fatigue alarm, makes GoHere (a TerrainInteraction) with that Sim as target, which throws in CreateInstance: the Sim
        // never walks back to where the jog began and that minute's jog / cardio / workout events are lost. Fix: our copy of
        // JogLoop, EA's order, with Terrain.Singleton as GoHere's target. Counters: [3067] walk-backs queued, [3068] fatigue
        // checks with the switch off, [3069] errors.
        static readonly FieldInfo sJtDest = B12F(typeof(Terrain.GoJogging), "Destination");
        static readonly FieldInfo sJtRoad = B12F(typeof(Terrain.GoJogging), "DestinationIsOnRoad");
        static readonly FieldInfo sJtLast = B12F(typeof(Terrain.GoJogging), "mLastTime");
        static readonly FieldInfo sJtTime = B12F(typeof(Terrain.GoJogging), "mTimeJogging");
        static string sJtM12 = "";

        static void ApplyJogTerrain()
        {
            sJtM12 = SpNames("GoJogging.Destination", sJtDest, "GoJogging.DestinationIsOnRoad", sJtRoad, "GoJogging.mLastTime", sJtLast, "GoJogging.mTimeJogging", sJtTime);
            if (sJtM12.Length > 0) { AppliedLog().Add("jogterrain: not found: " + sJtM12); return; }
            Patch(typeof(Terrain.GoJogging), "JogLoop", Type.EmptyTypes, typeof(B12JtHook), "LoopHook", "LoopOrig");
        }

        // Pure: does this minute end the jog (EA's test)?
        internal static bool JtStop(bool fatigued, bool fastRunAllowed) { return fatigued || !fastRunAllowed; }

        public class B12JtHook
        {
            public void LoopHook()
            {
                Terrain.GoJogging j = (Terrain.GoJogging)(object)this;
                if (!ShortcutOn("jogterrain")) { LoopOrig(); return; }
                try
                {
                    Sim a = j.Actor;
                    if (JtStop(a.BuffManager.HasElement(BuffNames.Fatigued), OccultVampire.IsNotVampireOrAllowedToFastRun(a)))
                    {
                        a.AddExitReason(ExitReason.CanceledByScript);
                        Vector3 dest = (Vector3)sJtDest.GetValue(j);
                        Terrain.GoHere goHere = Terrain.GoHere.GetSingleton(a, dest).CreateInstance(Terrain.Singleton, a, j.GetPriority(), false, true) as Terrain.GoHere;
                        if (goHere != null)
                        {
                            goHere.SetDestination(dest, (bool)sJtRoad.GetValue(j));
                            a.InteractionQueue.AddNext(goHere);
                            PatchCounters()[3067]++;
                        }
                    }
                    float last = (float)sJtLast.GetValue(j);
                    float num = SimClock.ElapsedTimeInMinutes(ref last);
                    sJtLast.SetValue(j, last);
                    sJtTime.SetValue(j, (float)sJtTime.GetValue(j) + num);
                    EventTracker.SendEvent(new TimePassedEvent(EventTypeId.kJogged, a, null, num));
                    EventTracker.SendEvent(new TimePassedEvent(EventTypeId.kCardioTimeAdded, a, null, num));
                    EventTracker.SendEvent(new TimePassedEvent(EventTypeId.kWorkedOut, a, null, num));
                }
                catch (Exception) { PatchCounters()[3069]++; }
            }

            public void LoopOrig() { }
        }

        // ---- B12 §3 lifeguardquit (docs/590 B4) ----------------------------------------------------------------------
        // [verified, decompiled] Terrain.SurveyBeach.SurveyLot (the DoLoop body) adds ExitReason.Finished when the Sim is no
        // longer a lifeguard with a job, then goes on: three route tries, then RescueCheck reads Actor.Occupation.CurrentJob
        // (NRE once the Sim quit). Fix: return right after the exit reason, as EA meant. Counters: [3070] loops ended,
        // [3071] quit loops run on (switch off), [3072] errors.
        static void ApplyLifeguardQuit()
        {
            Patch(typeof(Terrain.SurveyBeach), "SurveyLot", new Type[] { typeof(StateMachineClient), typeof(InteractionInstance.LoopData) }, typeof(B12LqHook), "LoopHook", "LoopOrig");
        }

        internal static bool LqQuit(bool isLifeguard, bool hasJob) { return !(isLifeguard && hasJob); }

        public class B12LqHook
        {
            public void LoopHook(StateMachineClient smc, InteractionInstance.LoopData ld)
            {
                InteractionInstance ii = (InteractionInstance)(object)this;
                Sim a = ii.InstanceActor as Sim;
                bool quit = false;
                try { quit = a != null && LqQuit(a.Occupation is Lifeguard, a.Occupation != null && a.Occupation.CurrentJob != null); } catch (Exception) { PatchCounters()[3072]++; }
                if (quit)
                {
                    if (ShortcutOn("lifeguardquit")) { a.AddExitReason(ExitReason.Finished); PatchCounters()[3070]++; return; }
                    PatchCounters()[3071]++;
                }
                LoopOrig(smc, ld);
            }

            public void LoopOrig(StateMachineClient smc, InteractionInstance.LoopData ld) { }
        }

        // ---- B12 §4 frolicexit (docs/590 B5) -------------------------------------------------------------------------
        // [verified, decompiled] OccultFairy.FairySocialFrolic.FrolicLoop tests each critical motive with `flag2 = ...`
        // instead of `|=`: only the last one in kCriticalMotiveTypes can end the frolic, so a fairy whose first critical
        // motive (hunger) bottoms out keeps frolicking. Fix: after EA's loop body, a frolic past its sustained time whose
        // actor or target has any critical motive at or under its value gets EA's Finished on both. Counters: [3073] frolics
        // ended, [3074] missed with the switch off, [3075] errors.
        static readonly FieldInfo sFeSustained = B12F(typeof(OccultFairy.FairySocialFrolic), "sustainedTicks");
        static string sFeM12 = "";

        static void ApplyFrolicExit()
        {
            sFeM12 = SpNames("FairySocialFrolic.sustainedTicks", sFeSustained);
            if (sFeM12.Length > 0) { AppliedLog().Add("frolicexit: not found: " + sFeM12); return; }
            Patch(typeof(OccultFairy.FairySocialFrolic), "FrolicLoop", new Type[] { typeof(StateMachineClient), typeof(InteractionInstance.LoopData) }, typeof(B12FeHook), "LoopHook", "LoopOrig");
        }

        // Pure: EA's intended stop (any critical motive), given the sustained time has passed.
        internal static bool FeExit(bool sustained, bool[] critical)
        {
            if (!sustained || critical == null) return false;
            foreach (bool c in critical) if (c) return true;
            return false;
        }

        static bool[] FeCritical(Sim a, Sim t)
        {
            CommodityKind[] k = OccultFairy.FairySocialFrolic.kCriticalMotiveTypes;
            float[] v = OccultFairy.FairySocialFrolic.kCriticalMotiveValues;
            int n = Math.Min(k.Length, v.Length);
            bool[] r = new bool[n];
            for (int i = 0; i < n; i++) r[i] = a.Motives.GetValue(k[i]) <= v[i] || t.Motives.GetValue(k[i]) <= v[i];
            return r;
        }

        public class B12FeHook
        {
            public void LoopHook(StateMachineClient smc, InteractionInstance.LoopData ld)
            {
                LoopOrig(smc, ld);
                try
                {
                    SocialInteraction si = (SocialInteraction)(object)this;
                    Sim a = si.Actor, t = si.Target;
                    if (a == null || t == null) return;
                    if (!FeExit(SimClock.CurrentTicks > (long)sFeSustained.GetValue(si), FeCritical(a, t))) return;
                    if (a.HasExitReason(ExitReason.Finished)) return;  // EA's own test caught it
                    if (!ShortcutOn("frolicexit")) { PatchCounters()[3074]++; return; }
                    a.AddExitReason(ExitReason.Finished);
                    t.AddExitReason(ExitReason.Finished);
                    PatchCounters()[3073]++;
                }
                catch (Exception) { PatchCounters()[3075]++; }
            }

            public void LoopOrig(StateMachineClient smc, InteractionInstance.LoopData ld) { }
        }

        // ---- B12 §5 shoodef (docs/590 B6, docs/591 R21 VisitSituation half) ------------------------------------------
        // [verified, decompiled] TraitFunctions.DislikesChildrenTraitShoo.Run makes its instance from Sim.TurnToFaceAndAskToLeave.Singleton
        // and writes AnimationName / GoingWhere into that shared definition; VisitSituation's guest shoo writes Reason,
        // GoingWhere and TnsText into the same Singleton. A trait shoo after a guest shoo keeps the guest's TnsText and
        // Reason; a guest shoo label after a trait shoo reads "next room". Fix: the trait shoo uses its own Definition, as
        // PrivacySituation does (VisitSituation sets every field it reads, so it is right once nobody else writes the
        // Singleton). Counters: [3076] shoos with an own definition, [3077] shared writes (switch off), [3078] errors.
        static void ApplyShooDef()
        {
            Patch(typeof(TraitFunctions.DislikesChildrenTraitShoo), "Run", Type.EmptyTypes, typeof(B12SdHook), "RunHook", "RunOrig");
        }

        // The definition the fixed Run uses (the check reads the same one).
        internal static Sim.TurnToFaceAndAskToLeave.Definition SdDefinition()
        {
            Sim.TurnToFaceAndAskToLeave.Definition d = new Sim.TurnToFaceAndAskToLeave.Definition();
            d.AnimationName = "a_neutral_shoo_x";
            d.GoingWhere = Sim.TurnToFaceAndAskToLeave.Definition.GoWhere.LeaveRoom;
            return d;
        }

        public class B12SdHook
        {
            public bool RunHook()
            {
                if (!ShortcutOn("shoodef")) { PatchCounters()[3077]++; return RunOrig(); }
                try
                {
                    TraitFunctions.DislikesChildrenTraitShoo s = (TraitFunctions.DislikesChildrenTraitShoo)(object)this;
                    InteractionInstance ii = SdDefinition().CreateInstance(s.Target, s.Actor, s.Actor.InheritedPriority(), false, false);
                    s.Actor.InteractionQueue.PushAsContinuation(ii, true);
                    PatchCounters()[3076]++;
                    return true;
                }
                catch (Exception) { PatchCounters()[3078]++; return false; }
            }

            public bool RunOrig() { return false; }
        }

        // ---- B12 §6 igloolookats (docs/590 B8) -----------------------------------------------------------------------
        // [verified, decompiled] Terrain.MakeIgloo.Run disables look-ats, then on its normal end takes the Sim off the
        // igloo's use list; Cleanup re-enables look-ats only while the Sim is still on that list, so every finished or
        // cancelled build leaves the builder's look-ats off for good. Fix: when this Run turned look-ats off and left the
        // use list, they are turned back on. Counters: [3079] look-ats restored, [3080] left off (switch off), [3081] errors.
        static readonly FieldInfo sIlIgloo = B12F(typeof(Terrain.MakeIgloo), "mIgloo");
        static string sIlM12 = "";

        static void ApplyIglooLookats()
        {
            sIlM12 = SpNames("MakeIgloo.mIgloo", sIlIgloo);
            if (sIlM12.Length > 0) { AppliedLog().Add("igloolookats: not found: " + sIlM12); return; }
            Patch(typeof(Terrain.MakeIgloo), "Run", Type.EmptyTypes, typeof(B12IlHook), "RunHook", "RunOrig");
        }

        internal static bool IlRestore(bool wasEnabled, bool nowEnabled, bool stillUsing) { return wasEnabled && !nowEnabled && !stillUsing; }

        public class B12IlHook
        {
            public bool RunHook()
            {
                InteractionInstance ii = (InteractionInstance)(object)this;
                Sim a = ii.InstanceActor as Sim;
                bool was = a != null && a.LookAtManager != null && a.LookAtManager.IsEnabled;
                bool r = RunOrig();
                try
                {
                    if (a == null || a.LookAtManager == null) return r;
                    GameObject igloo = sIlIgloo.GetValue(ii) as GameObject;
                    if (!IlRestore(was, a.LookAtManager.IsEnabled, igloo != null && igloo.ActorsUsingMe.Contains(a))) return r;
                    if (ShortcutOn("igloolookats")) { a.LookAtManager.EnableLookAts(); PatchCounters()[3079]++; }
                    else PatchCounters()[3080]++;
                }
                catch (Exception) { PatchCounters()[3081]++; }
                return r;
            }

            public bool RunOrig() { return false; }
        }

        // ---- B12 §7 gohereboatlist (docs/590 B10) --------------------------------------------------------------------
        // [verified, decompiled] GoHereWithSituation.GoToPointState.OnParticipantDeleted drops a deleted Sim from
        // mSimsRouting and mSimsToPutInCar, not from mSimsToPutInBoat / mSimsToPutInHoverTrain; the boat and hover-train
        // callbacks then push GetIn interactions on the deleted Sim. Fix: it leaves both lists too. Counters: [3082] Sims
        // dropped, [3083] left in a list (switch off), [3084] errors.
        static readonly Type sGbState = typeof(GoHereWithSituation).GetNestedType("GoToPointState", BindingFlags.Public | BindingFlags.NonPublic);
        static readonly FieldInfo sGbBoat = B12F(sGbState, "mSimsToPutInBoat");
        static readonly FieldInfo sGbTrain = B12F(sGbState, "mSimsToPutInHoverTrain");
        static string sGbM12 = "";

        static void ApplyGoHereBoatList()
        {
            sGbM12 = SpNames("GoHereWithSituation+GoToPointState", sGbState, "mSimsToPutInBoat", sGbBoat, "mSimsToPutInHoverTrain", sGbTrain);
            if (sGbM12.Length > 0) { AppliedLog().Add("gohereboatlist: not found: " + sGbM12); return; }
            Patch(sGbState, "OnParticipantDeleted", new Type[] { typeof(Sim) }, typeof(B12GbHook), "DeletedHook", "DeletedOrig");
        }

        internal static bool GbRemove(bool inBoat, bool inTrain) { return inBoat || inTrain; }

        public class B12GbHook
        {
            public void DeletedHook(Sim participant)
            {
                DeletedOrig(participant);
                try
                {
                    List<Sim> boat = sGbBoat.GetValue(this) as List<Sim>, train = sGbTrain.GetValue(this) as List<Sim>;
                    if (!GbRemove(boat != null && boat.Contains(participant), train != null && train.Contains(participant))) return;
                    if (!ShortcutOn("gohereboatlist")) { PatchCounters()[3083]++; return; }
                    if (boat != null) boat.Remove(participant);
                    if (train != null) train.Remove(participant);
                    PatchCounters()[3082]++;
                }
                catch (Exception) { PatchCounters()[3084]++; }
            }

            public void DeletedOrig(Sim participant) { }
        }

        // ---- B12 §8 bouncerrole (docs/590 B12) -----------------------------------------------------------------------
        // [verified, decompiled] VelvetRopes.ShouldBouncerNoticeSneaker compares GetDistanceToObjectSquared with the plain
        // 10 m tunable kSneakInDistanceToBouncer (a 3.2 m radius), and reads mCurrentRole.IsActive with no null test;
        // SneakIn, AskToGetIn and BribeBouncer read CurrentRole.SimInRole unguarded too (ropes with no bouncer role yet).
        // Fix: the radius is squared, a null role means "no bouncer", and the two interactions end (false) on a null role.
        // Counters: [3085] notice tests answered our way, [3086] tests with the switch off, [3087] null-role runs ended.
        static readonly FieldInfo sBrRole = B12F(typeof(VelvetRopes), "mCurrentRole");
        static readonly FieldInfo sBrDist = B12F(typeof(VelvetRopes), "kSneakInDistanceToBouncer");
        static readonly Type sBrAsk = typeof(VelvetRopes).GetNestedType("AskToGetIn", BindingFlags.NonPublic);
        static readonly Type sBrBribe = typeof(VelvetRopes).GetNestedType("BribeBouncer", BindingFlags.NonPublic);
        static readonly Type sBrWatch = typeof(VelvetRopes).GetNestedType("WatchDoor", BindingFlags.NonPublic);
        static string sBrM12 = "";

        static void ApplyBouncerRole()
        {
            sBrM12 = SpNames("VelvetRopes.mCurrentRole", sBrRole, "kSneakInDistanceToBouncer", sBrDist, "AskToGetIn", sBrAsk, "BribeBouncer", sBrBribe, "WatchDoor", sBrWatch);
            if (sBrM12.Length > 0) { AppliedLog().Add("bouncerrole: not found: " + sBrM12); return; }
            Patch(typeof(VelvetRopes), "ShouldBouncerNoticeSneaker", new Type[] { typeof(Sim) }, typeof(B12BrHook), "NoticeHook", "NoticeOrig");
            Patch(sBrAsk, "Run", Type.EmptyTypes, typeof(B12BrHook), "AskHook", "AskOrig");
            Patch(sBrBribe, "Run", Type.EmptyTypes, typeof(B12BrHook), "BribeHook", "BribeOrig");
        }

        // Pure: does the bouncer notice the sneaker? squared = the fix (EA compares the squared distance with the radius).
        internal static bool BrNotice(bool squared, bool roleActive, bool watching, bool idleSameRoom, float distSq, float radius)
        {
            if (!roleActive) return false;
            if (watching) return true;
            return idleSameRoom && distSq <= (squared ? radius * radius : radius);
        }

        static Role BrRoleOf(object ropes) { return ropes == null ? null : sBrRole.GetValue(ropes) as Role; }

        public class B12BrHook
        {
            public bool NoticeHook(Sim sneaker)
            {
                if (!ShortcutOn("bouncerrole")) { PatchCounters()[3086]++; return NoticeOrig(sneaker); }
                Role role = BrRoleOf(this);
                Sim b = role != null && role.IsActive ? role.SimInRole : null;
                PatchCounters()[3085]++;
                if (b == null) return false;
                return BrNotice(true, true, b.CurrentInteraction != null && sBrWatch.IsInstanceOfType(b.CurrentInteraction),
                    b.CurrentInteraction == null && Lot.AreInSameRoom(b, sneaker), b.GetDistanceToObjectSquared(sneaker), (float)sBrDist.GetValue(null));
            }

            public bool AskHook()
            {
                InteractionInstance ii = (InteractionInstance)(object)this;
                if (ShortcutOn("bouncerrole") && BrRoleOf(ii.Target) == null) { PatchCounters()[3087]++; return false; }
                return AskOrig();
            }

            public bool BribeHook()
            {
                InteractionInstance ii = (InteractionInstance)(object)this;
                if (ShortcutOn("bouncerrole"))
                {
                    object ropes = ii.Target as VelvetRopes;
                    Sim s = ii.Target as Sim;
                    if (ropes == null && s != null) ropes = s.SimDescription.AssignedRole == null ? null : s.SimDescription.AssignedRole.RoleGivingObject as VelvetRopes;
                    if ((ropes != null || s != null) && BrRoleOf(ropes) == null) { PatchCounters()[3087]++; return false; }
                }
                return BribeOrig();
            }

            public bool NoticeOrig(Sim sneaker) { return false; }
            public bool AskOrig() { return false; }
            public bool BribeOrig() { return false; }
        }

        // ---- B12 §9 tricksocials (docs/590 B13 first half) -----------------------------------------------------------
        // [verified, decompiled] MagicianSocialTricks.Run calls Target.DisableSocialsOnSim() and undoes it only on its normal
        // end: a Run that throws or is reset after that point leaves the watcher unable to socialise for good. Fix: a Run
        // that leaves the target's socials off when they were on before turns them back on. Counters: [3088] restored,
        // [3089] left off (switch off), [3090] errors.
        static void ApplyTrickSocials()
        {
            Patch(typeof(MagicianCareer.MagicianSocialTricks), "Run", Type.EmptyTypes, typeof(B12MtHook), "RunHook", "RunOrig");
        }

        internal static bool MtRestore(bool before, bool after) { return before && !after; }

        public class B12MtHook
        {
            public bool RunHook()
            {
                SocialInteraction si = (SocialInteraction)(object)this;
                Sim t = si.Target;
                bool before = t != null && t.SimDescription != null && t.SimDescription.ShowSocialsOnSim;
                try { return RunOrig(); }
                finally
                {
                    try
                    {
                        if (t != null && t.SimDescription != null && MtRestore(before, t.SimDescription.ShowSocialsOnSim))
                        {
                            if (ShortcutOn("tricksocials")) { t.EnableSocialsOnSim(); PatchCounters()[3088]++; }
                            else PatchCounters()[3089]++;
                        }
                    }
                    catch (Exception) { PatchCounters()[3090]++; }
                }
            }

            public bool RunOrig() { return false; }
        }

        // ---- B12 §10 fearfaintundo (docs/590 B13 second half) --------------------------------------------------------
        // [verified, decompiled] TraitFunctions.FearOfHumansFaint.Run (robot chip) creates a jig, then SetShutDown(true) and SetIsSleeping
        // and undoes them only on its normal end; a failed route returns with the jig left in the world, and a throw or
        // reset after the shutdown leaves the robot shut down. Fix: after Run, a robot it shut down is started again, the
        // Sim is woken and a jig still in the world is destroyed. Counters: [3091] undone, [3092] left (switch off),
        // [3093] errors.
        static readonly FieldInfo sFfJig = B12F(typeof(TraitFunctions.FearOfHumansFaint), "mJig");
        static string sFfM12 = "";

        static void ApplyFearFaintUndo()
        {
            sFfM12 = SpNames("TraitFunctions.FearOfHumansFaint.mJig", sFfJig);
            if (sFfM12.Length > 0) { AppliedLog().Add("fearfaintundo: not found: " + sFfM12); return; }
            Patch(typeof(TraitFunctions.FearOfHumansFaint), "Run", Type.EmptyTypes, typeof(B12FfHook), "RunHook", "RunOrig");
        }

        internal static bool FfUndo(bool shutBefore, bool shutAfter, bool jigLeft) { return (!shutBefore && shutAfter) || jigLeft; }

        public class B12FfHook
        {
            public bool RunHook()
            {
                InteractionInstance ii = (InteractionInstance)(object)this;
                Sim a = ii.InstanceActor as Sim;
                OccultRobot robot = a == null || a.OccultManager == null ? null : a.OccultManager.GetOccultType(Sims3.UI.Hud.OccultTypes.Robot) as OccultRobot;
                bool shutBefore = robot != null && robot.IsShutdown;
                try { return RunOrig(); }
                finally
                {
                    try
                    {
                        GameObject jig = sFfJig.GetValue(ii) as GameObject;
                        bool jigLeft = jig != null && !jig.HasBeenDestroyed;
                        bool shutAfter = robot != null && robot.IsShutdown;
                        if (FfUndo(shutBefore, shutAfter, jigLeft))
                        {
                            if (ShortcutOn("fearfaintundo"))
                            {
                                if (!shutBefore && shutAfter) { robot.SetShutDown(false); a.SetIsSleeping(false); }
                                if (jigLeft) { jig.Destroy(); sFfJig.SetValue(ii, null); }
                                PatchCounters()[3091]++;
                            }
                            else PatchCounters()[3092]++;
                        }
                    }
                    catch (Exception) { PatchCounters()[3093]++; }
                }
            }

            public bool RunOrig() { return false; }
        }

        // ---- B12 §11 lotprune (docs/590 B14) -------------------------------------------------------------------------
        // [verified, decompiled] MetaAutonomyManager.UpdateTimeStampsAndCollectLotsToPrune passes its null list to
        // UpdateLotsToPrune, which assigns its own parameter: the list never reaches the caller, PruneLotsThatAreNotPlayerPreferred
        // always gets null, and the player's lot-popularity map keeps every deleted, residential or no-longer-visited lot.
        // Fix: when Prune is handed null, the prune list is rebuilt with EA's two rules (lot gone or not community/dorm;
        // no visit left in the window, the window trim having just run) and EA's prune runs on it. Counters: [3094] lots
        // pruned, [3095] lots kept (switch off), [3096] errors.
        static readonly FieldInfo sLpMap = B12F(typeof(MetaAutonomyManager), "sLotPopularityMapForPlayer");
        static readonly Type sLpVisit = typeof(MetaAutonomyManager).GetNestedType("LotVisitFrequency", BindingFlags.Public | BindingFlags.NonPublic);
        static readonly PropertyInfo sLpCount = sLpVisit == null ? null : sLpVisit.GetProperty("TimeStampCount", kB11All);
        static string sLpM12 = "";

        static void ApplyLotPrune()
        {
            sLpM12 = SpNames("MetaAutonomyManager.sLotPopularityMapForPlayer", sLpMap, "LotVisitFrequency", sLpVisit, "TimeStampCount", sLpCount);
            if (sLpM12.Length > 0) { AppliedLog().Add("lotprune: not found: " + sLpM12); return; }
            Patch(typeof(MetaAutonomyManager), "PruneLotsThatAreNotPlayerPreferred", new Type[] { typeof(List<ulong>) }, typeof(B12LpHook), "PruneHook", "PruneOrig");
        }

        internal static bool LpPrune(bool lotMissing, bool communityOrDorm, int stamps) { return lotMissing || !communityOrDorm || stamps == 0; }

        public class B12LpHook
        {
            public static void PruneHook(List<ulong> lotsToPrune)
            {
                if (lotsToPrune == null)
                {
                    try
                    {
                        System.Collections.IDictionary map = sLpMap.GetValue(null) as System.Collections.IDictionary;
                        List<ulong> list = new List<ulong>();
                        if (map != null)
                            foreach (System.Collections.DictionaryEntry e in map)
                            {
                                Lot lot = LotManager.GetLot((ulong)e.Key);
                                if (LpPrune(lot == null, lot != null && (lot.IsCommunityLot || lot.IsDormitoryLot), (int)sLpCount.GetValue(e.Value, null))) list.Add((ulong)e.Key);
                            }
                        if (list.Count > 0)
                        {
                            if (ShortcutOn("lotprune")) { PatchCounters()[3094] += list.Count; lotsToPrune = list; }
                            else PatchCounters()[3095] += list.Count;
                        }
                    }
                    catch (Exception) { PatchCounters()[3096]++; }
                }
                PruneOrig(lotsToPrune);
            }

            public static void PruneOrig(List<ulong> lotsToPrune) { }
        }

        // ---- B12 §12 werewolffind (docs/590 B15) ---------------------------------------------------------------------
        // [verified, decompiled] Terrain.WerewolfHuntTerrain: FoundMetal takes the "added" branch when no metal of the price
        // range exists (metal null, NRE at .Value) and FoundGem calls AddCollectibleToInventory(null) then null.Destroy();
        // either throw ends the hunt. FoundInsect writes `mCollectableValue =` where the other finds add, so an insect wipes
        // the value of everything found before it (the hunt's TNS total). Fix: a null find returns -1 (nothing found, EA's own
        // answer for a failed add) and an insect's value is added. Counters: [3097] null finds and insect sums fixed,
        // [3098] seen with the switch off, [3099] errors.
        static readonly FieldInfo sWhValue = B12F(typeof(Terrain.WerewolfHuntTerrain), "mCollectableValue");
        static readonly FieldInfo sWhFound = B12F(typeof(Terrain.WerewolfHuntTerrain), "mCollectablesFound");
        static string sWhM12 = "";

        static void ApplyWerewolfFind()
        {
            sWhM12 = SpNames("WerewolfHuntTerrain.mCollectableValue", sWhValue, "mCollectablesFound", sWhFound);
            if (sWhM12.Length > 0) { AppliedLog().Add("werewolffind: not found: " + sWhM12); return; }
            Patch(typeof(Terrain.WerewolfHuntTerrain), "FoundMetal", Type.EmptyTypes, typeof(B12WhHook), "MetalHook", "MetalOrig");
            Patch(typeof(Terrain.WerewolfHuntTerrain), "FoundGem", Type.EmptyTypes, typeof(B12WhHook), "GemHook", "GemOrig");
            Patch(typeof(Terrain.WerewolfHuntTerrain), "FoundInsect", Type.EmptyTypes, typeof(B12WhHook), "InsectHook", "InsectOrig");
        }

        // Pure: the hunt's value after an insect find (added = EA's else branch ran and wrote the insect's value).
        internal static int WhValue(int before, int after, bool added) { return added ? before + after : after; }

        static int WhCount(object hunt) { System.Collections.ICollection c = sWhFound.GetValue(hunt) as System.Collections.ICollection; return c == null ? 0 : c.Count; }

        public class B12WhHook
        {
            public int MetalHook()
            {
                int n = WhCount(this);
                try { return MetalOrig(); }
                catch (NullReferenceException)
                {
                    if (WhCount(this) != n || !ShortcutOn("werewolffind")) { PatchCounters()[3098]++; throw; }
                    PatchCounters()[3097]++;
                    return -1;
                }
            }

            public int GemHook()
            {
                int n = WhCount(this);
                try { return GemOrig(); }
                catch (NullReferenceException)
                {
                    if (WhCount(this) != n || !ShortcutOn("werewolffind")) { PatchCounters()[3098]++; throw; }
                    PatchCounters()[3097]++;
                    return -1;
                }
            }

            public int InsectHook()
            {
                int before = 0, n = 0;
                try { before = (int)sWhValue.GetValue(this); n = WhCount(this); } catch (Exception) { PatchCounters()[3099]++; }
                int r = InsectOrig();
                try
                {
                    bool added = WhCount(this) > n;
                    if (!added || before == 0) return r;
                    if (!ShortcutOn("werewolffind")) { PatchCounters()[3098]++; return r; }
                    sWhValue.SetValue(this, WhValue(before, (int)sWhValue.GetValue(this), true));
                    PatchCounters()[3097]++;
                }
                catch (Exception) { PatchCounters()[3099]++; }
                return r;
            }

            public int MetalOrig() { return 0; }
            public int GemOrig() { return 0; }
            public int InsectOrig() { return 0; }
        }

        // ---- B12 §13 copyhomeworkfx (docs/590 A2) --------------------------------------------------------------------
        // [verified, decompiled] Homework.CopyHomework.Run creates the copying broadcaster and sets CopyingSim, and destroys
        // / clears them only after its DoLoop; a Run that throws or is reset in the loop leaves the broadcaster live (Sims
        // keep reacting to cheating that stopped) and CopyingSim set. Cleanup (CommonCleanup) does not touch either. Fix:
        // Cleanup of a copy whose actor is still the CopyingSim destroys the broadcaster and clears it. Counters: [3100]
        // cleared, [3101] left (switch off), [3102] errors.
        static readonly FieldInfo sChCast = B12F(typeof(Homework), "mCopyingBroadcaster");
        static readonly MethodInfo sChDestroy = B12M(typeof(Homework), "CopyingBroadcasterDestroy");
        static string sChM12 = "";

        static readonly Type sChType = typeof(Homework).GetNestedType("CopyHomework", BindingFlags.NonPublic);

        static void ApplyCopyHomeworkFx()
        {
            sChM12 = SpNames("Homework.mCopyingBroadcaster", sChCast, "CopyingBroadcasterDestroy", sChDestroy);
            if (sChM12.Length > 0) { AppliedLog().Add("copyhomeworkfx: not found: " + sChM12); return; }
            if (sChType == null) { AppliedLog().Add("copyhomeworkfx: not found: Homework+CopyHomework"); return; }
            Patch(sChType, "Cleanup", Type.EmptyTypes, typeof(B12ChHook), "CleanupHook", "CleanupOrig");
        }

        internal static bool ChStale(bool copierIsActor) { return copierIsActor; }

        public class B12ChHook
        {
            public void CleanupHook()
            {
                try
                {
                    InteractionInstance ii = (InteractionInstance)(object)this;
                    Homework hw = ii.Target as Homework;
                    if (hw != null && ii.InstanceActor != null && ChStale(hw.CopyingSim == ii.InstanceActor))
                    {
                        if (ShortcutOn("copyhomeworkfx"))
                        {
                            if (sChCast.GetValue(hw) != null) sChDestroy.Invoke(hw, null);
                            hw.CopyingSim = null;
                            PatchCounters()[3100]++;
                        }
                        else PatchCounters()[3101]++;
                    }
                }
                catch (Exception) { PatchCounters()[3102]++; }
                CleanupOrig();
            }

            public void CleanupOrig() { }
        }

        // ---- B12 §14 spelltargetend (docs/590 A3) --------------------------------------------------------------------
        // [verified, decompiled] MagicWand.CastSpell.Run queues BeTargetOfSpell on the target (linked to the cast) before it
        // routes; every early return after that (route, jig spot, AdditionalInteractionPreparation) and even the normal end
        // leave the target waiting: BeTargetOfSpell loops on `mJig == null` or sits in DoLoop(Default) until something else
        // cancels it. Fix: the cast's Cleanup ends the target's BeTargetOfSpell linked to it: Finished on the running one,
        // removed from the queue when not started yet. Counters: [3103] targets released, [3104] left waiting (switch off),
        // [3105] errors.
        static readonly Type sStBe = typeof(MagicWand).GetNestedType("BeTargetOfSpell", BindingFlags.NonPublic);
        static string sStM12 = "";

        static void ApplySpellTargetEnd()
        {
            sStM12 = SpNames("MagicWand+BeTargetOfSpell", sStBe);
            if (sStM12.Length > 0) { AppliedLog().Add("spelltargetend: not found: " + sStM12); return; }
            Patch(typeof(MagicWand.CastSpell), "Cleanup", Type.EmptyTypes, typeof(B12StHook), "CleanupHook", "CleanupOrig");
        }

        // Pure: 0 = nothing to do, 1 = end the running one, 2 = remove the queued one.
        internal static int StEnd(bool linkedToThis, bool running) { return !linkedToThis ? 0 : running ? 1 : 2; }

        public class B12StHook
        {
            public void CleanupHook()
            {
                try
                {
                    InteractionInstance ii = (InteractionInstance)(object)this;
                    Sim t = ii.Target as Sim;
                    if (t != null && t != ii.InstanceActor && t.InteractionQueue != null)
                    {
                        InteractionInstance found = null;
                        foreach (InteractionInstance q in t.InteractionQueue.InteractionList)
                            if (q != null && sStBe.IsInstanceOfType(q) && q.LinkedInteractionInstance == ii) { found = q; break; }
                        int act = found == null ? 0 : StEnd(true, t.InteractionQueue.GetCurrentInteraction() == found);
                        if (act != 0)
                        {
                            if (!ShortcutOn("spelltargetend")) PatchCounters()[3104]++;
                            else
                            {
                                if (act == 1) t.AddExitReason(ExitReason.Finished);
                                else t.InteractionQueue.RemoveInteractionByRef(found);
                                PatchCounters()[3103]++;
                            }
                        }
                    }
                }
                catch (Exception) { PatchCounters()[3105]++; }
                CleanupOrig();
            }

            public void CleanupOrig() { }
        }

        // ---- B12 §15 foodtraygrill (docs/590 A6) ---------------------------------------------------------------------
        // [verified, decompiled] FoodTray_Prepare.BringTrayToGrill: PushAsContinuation(Grillable_Grill) returns null when
        // the push fails and `.WasInterrupted = false` throws (the tray is already destroyed and the grill cooking, so the
        // throw is the only difference); and the "no free grill / grill in use" return keeps the tray in the Sim's hands,
        // where EA's own route-fail branch puts it down. Fix: the failed push ends as "not continued" (false), and a false
        // return with the tray still held puts it down as the route-fail branch does. Counters: [3106] fixed returns,
        // [3107] seen with the switch off, [3108] errors.
        static readonly Type sFtgType = typeof(FoodTray).Assembly.GetType("Sims3.Gameplay.Objects.CookingObjects.FoodTray_Prepare");

        static void ApplyFoodTrayGrill()
        {
            if (sFtgType == null) { AppliedLog().Add("foodtraygrill: not found: FoodTray_Prepare"); return; }
            Patch(sFtgType, "BringTrayToGrill", Type.EmptyTypes, typeof(B12FtgHook), "GrillHook", "GrillOrig");
        }

        internal static bool FtgPutDown(bool result, bool holdingTray) { return !result && holdingTray; }

        public class B12FtgHook
        {
            public bool GrillHook()
            {
                InteractionInstance ii = (InteractionInstance)(object)this;
                Sim a = ii.InstanceActor as Sim;
                bool r;
                try { r = GrillOrig(); }
                catch (NullReferenceException)
                {
                    if (!ShortcutOn("foodtraygrill") || a == null || a.InteractionQueue == null) { PatchCounters()[3107]++; throw; }
                    PatchCounters()[3106]++;
                    return false;
                }
                try
                {
                    if (a != null && FtgPutDown(r, a.GetObjectInRightHand() == ii.Target))
                    {
                        if (ShortcutOn("foodtraygrill")) { Food.PutHeldObjectDownOnCounterTableOrFloor(a, SurfaceType.Normal); PatchCounters()[3106]++; }
                        else PatchCounters()[3107]++;
                    }
                }
                catch (Exception) { PatchCounters()[3108]++; }
                return r;
            }

            public bool GrillOrig() { return false; }
        }

        // ---- B12 §16 compositionnull (docs/590 A7, docs/591 C8) ------------------------------------------------------
        // [verified, decompiled] SingingInfo.ImportCompositions and BandSkill.ImportCompositions read a string[] key and add
        // GetComposition(name) for each entry: a missing key (null array) throws, and a name with no composition (pack gone,
        // renamed) adds null, which the composition UI and the "known" counts then dereference. Fix: a null array imports
        // nothing and null compositions are not kept. Counters: [3109] imports cleaned, [3110] nulls kept (switch off),
        // [3111] errors.
        static void ApplyCompositionNull()
        {
            Patch(typeof(SingingInfo), "ImportCompositions", new Type[] { typeof(IPropertyStreamReader), typeof(List<SingingInfo.SingingComposition>), typeof(uint) }, typeof(B12CnHook), "SingHook", "SingOrig");
            Patch(typeof(BandSkill), "ImportCompositions", new Type[] { typeof(IPropertyStreamReader), typeof(List<Sims3.Gameplay.Skills.Guitar.Composition>), typeof(uint) }, typeof(B12CnHook), "BandHook", "BandOrig");
        }

        // Pure: entries the import keeps (EA keeps all).
        internal static int CnDrop<T>(List<T> list, int from) where T : class
        {
            int n = 0;
            for (int i = list.Count - 1; i >= from; i--) if (list[i] == null) { list.RemoveAt(i); n++; }
            return n;
        }

        static bool CnImport<T>(IPropertyStreamReader reader, List<T> list, uint key, B12BoolFn orig) where T : class
        {
            if (!ShortcutOn("compositionnull")) { bool r = orig(); if (list != null && list.Contains(null)) PatchCounters()[3110]++; return r; }
            try
            {
                string[] names;
                reader.ReadString(key, out names);
                if (names == null) { PatchCounters()[3109]++; return true; }
                int from = list.Count;
                bool r = orig();
                if (CnDrop(list, from) > 0) PatchCounters()[3109]++;
                return r;
            }
            catch (Exception) { PatchCounters()[3111]++; return false; }
        }

        public class B12CnHook
        {
            public bool SingHook(IPropertyStreamReader reader, List<SingingInfo.SingingComposition> c, uint key) { return CnImport(reader, c, key, () => SingOrig(reader, c, key)); }
            public bool BandHook(IPropertyStreamReader reader, List<Sims3.Gameplay.Skills.Guitar.Composition> c, uint key) { return CnImport(reader, c, key, () => BandOrig(reader, c, key)); }
            public bool SingOrig(IPropertyStreamReader reader, List<SingingInfo.SingingComposition> c, uint key) { return false; }
            public bool BandOrig(IPropertyStreamReader reader, List<Sims3.Gameplay.Skills.Guitar.Composition> c, uint key) { return false; }
        }

        // ---- B12 §17 petdreamtimer (docs/590 A10) --------------------------------------------------------------------
        // [verified, decompiled] SleepAndNapOnObject.SleepLoop resets mTimeUntilNextBalloon only when TryDreamAboutHuman
        // fails: once the timer is due, a sleeping pet that dreams of its human rolls (15%) and shows that balloon on every
        // loop tick. Fix: a due timer is reset after either balloon. Counters: [3112] timers reset, [3113] left due
        // (switch off), [3114] errors.
        static readonly FieldInfo sPdTimer = B12F(typeof(SleepAndNapOnObject), "mTimeUntilNextBalloon");
        static readonly FieldInfo sPdFreq = B12F(typeof(SleepAndNapOnObject), "kFrequencyOfThoughtBalloon");
        static string sPdM12 = "";

        static void ApplyPetDreamTimer()
        {
            sPdM12 = SpNames("SleepAndNapOnObject.mTimeUntilNextBalloon", sPdTimer, "kFrequencyOfThoughtBalloon", sPdFreq);
            if (sPdM12.Length > 0) { AppliedLog().Add("petdreamtimer: not found: " + sPdM12); return; }
            Patch(typeof(SleepAndNapOnObject), "SleepLoop", new Type[] { typeof(StateMachineClient), typeof(InteractionInstance.LoopData) }, typeof(B12PdHook), "LoopHook", "LoopOrig");
        }

        internal static bool PdReset(float timer) { return timer <= 0f; }

        public class B12PdHook
        {
            public void LoopHook(StateMachineClient smc, InteractionInstance.LoopData ld)
            {
                LoopOrig(smc, ld);
                try
                {
                    if (!PdReset((float)sPdTimer.GetValue(this))) return;
                    if (ShortcutOn("petdreamtimer")) { sPdTimer.SetValue(this, (float)sPdFreq.GetValue(null)); PatchCounters()[3112]++; }
                    else PatchCounters()[3113]++;
                }
                catch (Exception) { PatchCounters()[3114]++; }
            }

            public void LoopOrig(StateMachineClient smc, InteractionInstance.LoopData ld) { }
        }
    }
}
