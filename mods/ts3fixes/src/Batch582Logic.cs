using System;
using System.Collections.Generic;
using System.Collections;
using System.Reflection;
using Sims3.Gameplay;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.ActiveCareer.ActiveCareers;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.Careers;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.EventSystem;
using Sims3.Gameplay.Objects.Vehicles;
using Sims3.Gameplay.Seasons;
using Sims3.Gameplay.Services;
using Sims3.Gameplay.Skills;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // docs/582: broad RE batch 3 (EA bug fixes, every switch off by default). Checks: Batch582Checks.cs.
    public static partial class Fixes
    {
        // ---- §1 vehiclefxhelper (tuning kVehicleFxHelper) ----------------------------------------------------------------
        // [verified, decompiled] Vehicle.OnRouteStarted (called by Vehicle/Boat/HoverTrain script adaptors at every
        // OnAboutToStartFollowingRoute) runs CreateTireEffectHelper and CreateHoverEffectHelper, which assign a NEW helper to
        // mTireEffectHelper / mHoverEffectHelper without destroying the one held. Only PostScriptAdaptorRoute, DoReset and
        // Dispose destroy the held helper. When a vehicle starts a second route before the adaptor's PostRoute ran (a route
        // that is killed or replanned, a leg restarted), the old helper is orphaned: its two EventTracker listeners
        // (kSnowLevelChanged, kRoadWetnessChanged -> TireEffectHelper.UpdateEffects) stay registered forever, keep the
        // Vehicle reachable after Dispose, and its tire / hover visual effects are never stopped.
        // Measured: log-issues growth leak.events.by_delegate.TireEffectHelper.UpdateEffects 0 -> 10 in 34 runs.
        // Fix (at creation): before EA's Create*EffectHelper, a held helper is destroyed with EA's own Destroy*EffectHelper.
        // The new helper is EA's, so effects and listeners are the same as a vehicle that ran PostRoute first.
        // Counters: [2808] helpers destroyed before re-create (on), [2809] the same seen with the switch off, [2810] errors.
        static readonly FieldInfo sVfTire = typeof(Vehicle).GetField("mTireEffectHelper", kInstAll);
        static readonly FieldInfo sVfHover = typeof(Vehicle).GetField("mHoverEffectHelper", kInstAll);
        static readonly MethodInfo sVfTireDestroy = typeof(Vehicle).GetMethod("DestroyTireEffectHelper", kInstAll, null, Type.EmptyTypes, null);
        static readonly MethodInfo sVfHoverDestroy = typeof(Vehicle).GetMethod("DestroyHoverEffectHelper", kInstAll, null, Type.EmptyTypes, null);
        static string sVfMissing = "";

        static void ApplyVehicleFxHelper()
        {
            sVfMissing = ((sVfTire == null ? "Vehicle.mTireEffectHelper " : "") + (sVfHover == null ? "Vehicle.mHoverEffectHelper " : "")
                + (sVfTireDestroy == null ? "Vehicle.DestroyTireEffectHelper " : "") + (sVfHoverDestroy == null ? "Vehicle.DestroyHoverEffectHelper " : "")
                + (typeof(Vehicle).GetMethod("CreateTireEffectHelper", kInstAll, null, Type.EmptyTypes, null) == null ? "Vehicle.CreateTireEffectHelper " : "")
                + (typeof(Vehicle).GetMethod("CreateHoverEffectHelper", kInstAll, null, Type.EmptyTypes, null) == null ? "Vehicle.CreateHoverEffectHelper" : "")).Trim();
            if (sVfMissing.Length > 0) { AppliedLog().Add("vehiclefxhelper: not found: " + sVfMissing); return; }
            Patch(typeof(Vehicle), "CreateTireEffectHelper", Type.EmptyTypes, typeof(BVfHook), "TireHook", "TireOrig");
            Patch(typeof(Vehicle), "CreateHoverEffectHelper", Type.EmptyTypes, typeof(BVfHook), "HoverHook", "HoverOrig");
        }

        // Pure: destroy the held helper before EA creates a new one?
        internal static bool VfDestroyFirst(bool on, bool held)
        {
            return on && held;
        }

        static void VfBefore(Vehicle v, FieldInfo f, MethodInfo destroy)
        {
            try
            {
                if (f.GetValue(v) == null) return;
                if (!ShortcutOn("vehiclefxhelper")) { PatchCounters()[2809]++; return; }
                if (VfDestroyFirst(true, true)) { destroy.Invoke(v, null); PatchCounters()[2808]++; }
            }
            catch (Exception) { PatchCounters()[2810]++; }
        }

        public class BVfHook
        {
            public void TireHook() { VfBefore((Vehicle)(object)this, sVfTire, sVfTireDestroy); TireOrig(); }
            public void TireOrig() { } // receives Vehicle.CreateTireEffectHelper's original body
            public void HoverHook() { VfBefore((Vehicle)(object)this, sVfHover, sVfHoverDestroy); HoverOrig(); }
            public void HoverOrig() { } // receives Vehicle.CreateHoverEffectHelper's original body
        }

        // Shared by the load sweeps below: one alarm manager's alarms as (handle, callback) pairs, read through mTimers
        // (the same read SandwichAlarmWorldLoad uses). Read only.
        static void B582Alarms(AlarmManager m, List<AlarmHandle> keys, List<Delegate> cbs)
        {
            IDictionary timers = sSaTimers != null ? sSaTimers.GetValue(m) as IDictionary : null;
            if (timers == null) return;
            foreach (DictionaryEntry e in timers)
            {
                IList l = e.Value as IList;
                object first = l != null && l.Count > 0 ? l[0] : null;
                FieldInfo cbf = first != null ? first.GetType().GetField("CallBack", kInstAll) : null;
                Delegate cb = cbf != null ? cbf.GetValue(first) as Delegate : null;
                if (cb == null) continue;
                keys.Add((AlarmHandle)e.Key); cbs.Add(cb);
            }
        }

        // ---- §2 prankditchempty (tuning kPrankDitchEmpty) ----------------------------------------------------------------
        // [verified, decompiled] Door.PrankDoorbellDitch.Run rings the bell, wanders off, then builds the prankee list from
        // the lot household's Sims with IsAtHome and calls RandomUtil.GetRandomObjectFromList on it. That call throws
        // ArgumentException("The list is empty.") on an empty list (RandomUtil.cs:139), and Run only handles a null pick.
        // The test (GenericPrankTest requiresResidentAtHome) runs before the prankster's route to the lot, so everyone can
        // have left meanwhile (work, school): the prankster's Run throws and the Sim is reset. The sibling pranks
        // (FlamingBag, ThrowingEggs) test the count.
        // Fix: that one exception, when the lot household really has nobody at home, ends Run the way EA's own null branch
        // does (AnimateSim("Exit"), return true). Any other exception is rethrown unchanged.
        // Counters: [2811] empty-list ends handled (on), [2812] the same thrown with the switch off, [2813] errors.
        static readonly FieldInfo sPdDoor = typeof(Door.PrankDoorbellDitch).GetField("mFrontDoor", kInstAll);
        static string sPdMissing = "";

        static void ApplyPrankDitchEmpty()
        {
            sPdMissing = ((sPdDoor == null ? "Door.PrankDoorbellDitch.mFrontDoor " : "")
                + (typeof(Door.PrankDoorbellDitch).GetMethod("Run", kInstAll, null, Type.EmptyTypes, null) == null ? "Door.PrankDoorbellDitch.Run" : "")).Trim();
            if (sPdMissing.Length > 0) { AppliedLog().Add("prankditchempty: not found: " + sPdMissing); return; }
            Patch(typeof(Door).GetNestedType("PrankDoorbellDitch", BindingFlags.Public | BindingFlags.NonPublic), "Run", Type.EmptyTypes, typeof(BPdHook), "RunHook", "RunOrig");
        }

        // Pure: end Run as EA's null-pick branch does?
        internal static bool PdAbsorb(bool on, bool emptyListError, bool nobodyHome)
        {
            return on && emptyListError && nobodyHome;
        }

        static bool PdNobodyHome(Door door)
        {
            Lot lot = door != null ? door.LotCurrent : null;
            if (lot == null || lot.Household == null) return false;
            foreach (Sim s in lot.Household.Sims) if (s != null && s.IsAtHome) return false;
            return true;
        }

        public class BPdHook
        {
            public bool RunHook()
            {
                try { return RunOrig(); }
                catch (ArgumentException e)
                {
                    InteractionInstanceShim me = new InteractionInstanceShim(this);
                    bool empty = e.Message == "The list is empty.";
                    bool nobody = false;
                    try { nobody = PdNobodyHome(sPdDoor.GetValue(this) as Door); } catch (Exception) { PatchCounters()[2813]++; }
                    if (!empty || !nobody) throw;
                    if (!ShortcutOn("prankditchempty")) { PatchCounters()[2812]++; throw; }
                    if (!PdAbsorb(true, empty, nobody)) throw;
                    PatchCounters()[2811]++;
                    me.Exit();
                    return true;
                }
            }
            public bool RunOrig() { return false; } // receives Door.PrankDoorbellDitch.Run's original body
        }

        // EA's "Exit" state of the prank's state machine, as the null-pick branch plays it.
        struct InteractionInstanceShim
        {
            readonly Sims3.Gameplay.Interactions.InteractionInstance mI;
            public InteractionInstanceShim(object o) { mI = o as Sims3.Gameplay.Interactions.InteractionInstance; }
            public void Exit() { try { if (mI != null) mI.AnimateSim("Exit"); } catch (Exception) { PatchCounters()[2813]++; } }
        }

        // ---- §3 serviceassignexit (tuning kServiceAssignExit) ------------------------------------------------------------
        // [verified, decompiled] "Ask to Hang Out" with a service Sim (maid, mail carrier, paperboy, pizza, repair...) runs
        // ServiceSituation.SetToSocializingState: IsSocializing = true, then EndService -> Exit -> OnExit ->
        // Service.OnAssignmentFinished. That method skips every entry whose situation IsSocializing, so the Sim stays in
        // mSituationsAssignedToSims (and mCreatedSims, mSimsAssignedToLots) pointing at the exited situation. Nothing else
        // removes it (mCreatedSims is written only there and in AssignSim) and Service is [Persistable], so it is saved.
        // IsSimAssignedTask stays true: that Sim is never sent again (a household's preferred maid never returns with
        // kAlwaysTryToSendSameSim), FindSimForAssignment makes new pool NPCs, NumAssignedSims stays inflated.
        // Fix (at creation): after EA's OnAssignmentFinished, the entries of this (exiting) situation that EA skipped are
        // removed from the three tables. EA's skip of the DestroyMyself push stays (the Sim is hanging out). A socializing
        // Sim cannot be sent meanwhile: FindSimForAssignment requires CreatedSim == null. Saved stale entries: at world load
        // (switch on) entries whose situation has exited are removed (SituationExited; live situations untouched).
        // Counters: [2814] entries removed at the exit (on), [2815] the same seen with the switch off, [2816] saved stale
        // entries removed at load, [2817] errors.
        static readonly FieldInfo sSaAssigned = typeof(Service).GetField("mSituationsAssignedToSims", kInstAll);
        static readonly FieldInfo sSaCreated = typeof(Service).GetField("mCreatedSims", kInstAll);
        static readonly FieldInfo sSaLots = typeof(Service).GetField("mSimsAssignedToLots", kInstAll);
        static string sSeMissing = "";

        static void ApplyServiceAssignExit()
        {
            sSeMissing = ((sSaAssigned == null ? "Service.mSituationsAssignedToSims " : "") + (sSaCreated == null ? "Service.mCreatedSims " : "")
                + (sSaLots == null ? "Service.mSimsAssignedToLots " : "")
                + (typeof(Service).GetMethod("OnAssignmentFinished", kInstAll, null, new Type[] { typeof(Situation) }, null) == null ? "Service.OnAssignmentFinished" : "")).Trim();
            if (sSeMissing.Length > 0) { AppliedLog().Add("serviceassignexit: not found: " + sSeMissing); return; }
            Patch(typeof(Service), "OnAssignmentFinished", new Type[] { typeof(Situation) }, typeof(BSeHook), "FinishedHook", "FinishedOrig");
        }

        // Pure: drop this table entry? at the exit: it names the exiting situation; at load: its situation has exited.
        internal static bool SeDrop(bool on, bool namesExiting, bool exited)
        {
            return on && (namesExiting || exited);
        }

        // Removes the entries SeDrop picks (no DestroyMyself push). Returns how many.
        static int SeSweep(Service svc, Situation exiting, bool on)
        {
            Dictionary<SimDescription, ServiceSituation> assigned = sSaAssigned.GetValue(svc) as Dictionary<SimDescription, ServiceSituation>;
            Dictionary<SimDescription, Sim> created = sSaCreated.GetValue(svc) as Dictionary<SimDescription, Sim>;
            Dictionary<ulong, List<Sim>> lots = sSaLots.GetValue(svc) as Dictionary<ulong, List<Sim>>;
            if (assigned == null) return 0;
            List<SimDescription> drop = new List<SimDescription>();
            foreach (KeyValuePair<SimDescription, ServiceSituation> kv in assigned)
            {
                bool names = exiting != null && object.ReferenceEquals(kv.Value, exiting);
                bool exited = exiting == null && (kv.Value == null || kv.Value.SituationExited);
                if (SeDrop(true, names, exited)) drop.Add(kv.Key);
            }
            if (!on) return drop.Count;
            foreach (SimDescription d in drop)
            {
                assigned.Remove(d);
                Sim sim;
                if (created != null && created.TryGetValue(d, out sim))
                {
                    if (lots != null) foreach (List<Sim> l in lots.Values) if (l != null) l.Remove(sim);
                    created.Remove(d);
                }
            }
            return drop.Count;
        }

        // World load (from Instantiator): saved entries whose situation has exited.
        internal static void ServiceAssignExitWorldLoad()
        {
            if (!ShortcutOn("serviceassignexit") || sSeMissing.Length > 0) return;
            try { foreach (Service s in Services.AllServices) if (s != null) PatchCounters()[2816] += SeSweep(s, null, true); }
            catch (Exception) { PatchCounters()[2817]++; }
        }

        public class BSeHook
        {
            public void FinishedHook(Situation assignment)
            {
                try { FinishedOrig(assignment); }
                finally { After((Service)(object)this, assignment); }  // also when EA's OnAssignmentFinished throws (review 2026-11-15)
            }
            static void After(Service svc, Situation assignment)
            {
                try
                {
                    if (assignment == null) return;
                    bool on = ShortcutOn("serviceassignexit");
                    int n = SeSweep(svc, assignment, on);
                    PatchCounters()[on ? 2814 : 2815] += n;
                }
                catch (Exception) { PatchCounters()[2817]++; }
            }
            public void FinishedOrig(Situation assignment) { } // receives Service.OnAssignmentFinished's original body
        }

        // ---- §4 serviceshowalarm (tuning kServiceShowAlarm) --------------------------------------------------------------
        // [verified, decompiled] ServiceSituation.RouteToLot.Init calls parent.OnServiceStarting, and OnRouteFailed retries
        // (up to 3 times) by calling Init again. OnServiceStarting adds the one-shot "Make Service Sim Visible" alarm and
        // overwrites mAlarmShowSim without removing the pending one, so MakeServiceSimVisible runs up to 4 times: each one
        // calls Autonomy.DecrementAutonomyDisabled (eating disable counts other systems hold), EnableSocialsOnSim and
        // FadeIn; CleanUp removes only the last handle.
        // Fix (at creation): when the held show alarm is still pending, EA's OnServiceStarting is not run again (the Sim is
        // shown once, at the first alarm's time, as EA's first call set it). A fired or missing alarm runs EA's code.
        // Overrides (BurglarSituation) are untouched.
        // Also (review 2026-11-15): ServiceSituation.CleanUp removes a still-pending show alarm without undoing what CreateSim
        // did (hidden, autonomy disabled, socials off). A worker that survives CleanUp (VirtualLotHome: apartment residents
        // go home instead of being destroyed) stays invisible, non-autonomous and unsociable. With the switch on, a pending
        // show alarm on a surviving worker runs EA's MakeServiceSimVisible before CleanUp. NPC-only (service workers).
        // Counters: [2818] re-arms skipped and workers shown at CleanUp (on), [2819] the same seen with the switch off,
        // [2820] errors.
        static readonly FieldInfo sSsShow = typeof(ServiceSituation).GetField("mAlarmShowSim", kInstAll);
        static readonly FieldInfo sSsDestroy = typeof(ServiceSituation).GetField("mDestroyWorkerOnExit", kInstAll);
        static readonly MethodInfo sSsMakeVisible = typeof(ServiceSituation).GetMethod("MakeServiceSimVisible", kInstAll, null, Type.EmptyTypes, null);
        static string sSsMissing = "";

        static void ApplyServiceShowAlarm()
        {
            sSsMissing = ((sSsShow == null ? "ServiceSituation.mAlarmShowSim " : "")
                + (typeof(ServiceSituation).GetMethod("OnServiceStarting", kInstAll, null, Type.EmptyTypes, null) == null ? "ServiceSituation.OnServiceStarting" : "")).Trim();
            if (sSsMissing.Length > 0) { AppliedLog().Add("serviceshowalarm: not found: " + sSsMissing); return; }
            Patch(typeof(ServiceSituation), "OnServiceStarting", Type.EmptyTypes, typeof(BSsHook), "StartHook", "StartOrig");
            if (sSsDestroy != null && sSsMakeVisible != null)
                Patch(typeof(ServiceSituation), "CleanUp", Type.EmptyTypes, typeof(BSsHook), "CleanHook", "CleanOrig");
            else AppliedLog().Add("serviceshowalarm: CleanUp part not found: ServiceSituation.mDestroyWorkerOnExit / MakeServiceSimVisible");
        }

        // Pure: show the worker before CleanUp? (EA's destroy test inverted: destroy, alive, service household, no virtual home)
        internal static bool SsShowAtCleanUp(bool on, bool pending, bool destroy, bool alive, bool serviceHousehold, bool virtualHome)
        {
            bool destroyed = destroy && alive && serviceHousehold && !virtualHome;
            return on && pending && alive && !destroyed;
        }

        // Pure: skip EA's re-arm?
        internal static bool SsSkip(bool on, bool handleValid, bool pending)
        {
            return on && handleValid && pending;
        }

        public class BSsHook
        {
            public void StartHook()
            {
                try
                {
                    AlarmHandle h = (AlarmHandle)sSsShow.GetValue(this);
                    bool valid = h != AlarmHandle.kInvalidHandle;
                    bool pending = valid && AlarmManager.Global.GetTimeLeft(h, TimeUnit.Minutes) > 0f;
                    if (valid && pending)
                    {
                        if (SsSkip(ShortcutOn("serviceshowalarm"), valid, pending)) { PatchCounters()[2818]++; return; }
                        PatchCounters()[2819]++;
                    }
                }
                catch (Exception) { PatchCounters()[2820]++; }
                StartOrig();
            }
            public void StartOrig() { } // receives ServiceSituation.OnServiceStarting's original body
            public void CleanHook()
            {
                try
                {
                    ServiceSituation ss = (ServiceSituation)(object)this;
                    Sim w = ss.Worker;
                    AlarmHandle h = (AlarmHandle)sSsShow.GetValue(ss);
                    bool pending = h != AlarmHandle.kInvalidHandle && AlarmManager.Global.GetTimeLeft(h, TimeUnit.Minutes) > 0f;
                    if (pending && w != null)
                    {
                        bool alive = !w.HasBeenDestroyed;
                        bool svcHh = w.Household != null && w.Household.IsServiceNpcHousehold;
                        bool vh = w.SimDescription != null && w.SimDescription.VirtualLotHome != null;
                        bool hh = w.Household != null && w.Household == Household.ActiveHousehold;
                        bool show = !hh && SsShowAtCleanUp(true, true, (bool)sSsDestroy.GetValue(ss), alive, svcHh, vh);
                        if (show)
                        {
                            if (ShortcutOn("serviceshowalarm"))
                            {
                                AlarmManager.Global.RemoveAlarm(h);
                                sSsShow.SetValue(ss, AlarmHandle.kInvalidHandle);
                                sSsMakeVisible.Invoke(ss, null);  // not swapped: Invoke is fine
                                PatchCounters()[2818]++;
                            }
                            else PatchCounters()[2819]++;
                        }
                    }
                }
                catch (Exception) { PatchCounters()[2820]++; }
                CleanOrig();
                ButlerCleanUp((ServiceSituation)(object)this);  // butlerpayalarm (Batch593Logic.cs), switch tested inside
            }
            public void CleanOrig() { } // receives ServiceSituation.CleanUp
        }

        // ---- §6 maildogchaser (tuning kMailDogChaser) --------------------------------------------------------------------
        // [verified, decompiled] Mailbox.PutMail.OnEnterDogChaseBroadcaster rolls once per delivery and stores the dog in
        // MailCarrier.DogChaser (one field on the single mail service, reached here through IMailCarrier). Nothing ever sets it back to null (the getter only
        // hides a destroyed dog), so after the first chase every later delivery, on any lot, goes RouteToLot.OnDelivery ->
        // DogBark -> DogChase with that same dog: it is forced into "Growl At", the carrier runs off, every day.
        // Fix (at creation of the next state): when a chase ends (MailCarrierSituation.DogChase.Finished), the service's
        // DogChaser is cleared if it is still this chase's dog. A dog set meanwhile by another lot's delivery is kept.
        // Counters: [2821] chasers cleared (on), [2822] the same seen with the switch off, [2823] errors.
        static readonly Type sMdChase = typeof(MailCarrierSituation).GetNestedType("DogChase", BindingFlags.Public | BindingFlags.NonPublic);
        static readonly FieldInfo sMdDog = sMdChase != null ? sMdChase.GetField("mDogChaser", kInstAll) : null;
        static string sMdMissing = "";

        static void ApplyMailDogChaser()
        {
            sMdMissing = ((sMdChase == null ? "MailCarrierSituation.DogChase " : "") + (sMdDog == null ? "DogChase.mDogChaser " : "")
                + (sMdChase != null && sMdChase.GetMethod("Finished", kInstAll, null, new Type[] { typeof(Sim), typeof(float) }, null) == null ? "DogChase.Finished" : "")).Trim();
            if (sMdMissing.Length > 0) { AppliedLog().Add("maildogchaser: not found: " + sMdMissing); return; }
            Patch(typeof(MailCarrierSituation).GetNestedType("DogChase", BindingFlags.Public | BindingFlags.NonPublic), "Finished", new Type[] { typeof(Sim), typeof(float) }, typeof(BMdHook), "FinishedHook", "FinishedOrig");
        }

        // Pure: clear the service's chaser?
        internal static bool MdClear(bool on, bool sameDog)
        {
            return on && sameDog;
        }

        public class BMdHook
        {
            public void FinishedHook(Sim actor, float x)
            {
                try
                {
                    Sim dog = sMdDog.GetValue(this) as Sim;
                    Sims3.Gameplay.Interfaces.IMailCarrier mc = null;
                    foreach (Service svc in Services.AllServices) { mc = svc as Sims3.Gameplay.Interfaces.IMailCarrier; if (mc != null) break; }
                    if (mc != null && dog != null && object.ReferenceEquals(mc.DogChaser, dog))
                    {
                        if (MdClear(ShortcutOn("maildogchaser"), true)) { mc.DogChaser = null; PatchCounters()[2821]++; }
                        else PatchCounters()[2822]++;
                    }
                }
                catch (Exception) { PatchCounters()[2823]++; }
                FinishedOrig(actor, x);
            }
            public void FinishedOrig(Sim actor, float x) { } // receives MailCarrierSituation.DogChase.Finished's original body
        }

        // ---- §7 lifeguardsurvey (tuning kLifeguardSurvey) ----------------------------------------------------------------
        // [verified, decompiled] Lifeguard.BeachSurvey adds the repeating, AlwaysPersisted "Lifeguard - Check Beach Events"
        // alarm (mEventTriggerAlarm, owner the lifeguard); only BeachSurvey.Cleanup removes it. When the last task is done,
        // Job.OnCompletion calls ShutDown, not Cleanup, and ShutDown removes the timeout alarm too, so the event alarm
        // repeats for good (saved with the Sim). Each fire: mPendingTasks is empty, SetupTasks does not rebuild it (only
        // when null) but re-adds tasks to mTasks, restarts the job music and the camera callback ShutDown removed, and
        // RandomUtil.GetRandomObjectFromList(mPendingTasks) throws "The list is empty".
        // Fix (at creation): after EA's Job.OnCompletion of a BeachSurvey, its event alarm is removed with EA's own
        // RemoveAlarm. Saved orphans: at world load (switch on) CheckLifeguardEvents alarms whose job is no longer its
        // occupation's CurrentJob are removed.
        // Counters: [2824] alarms removed at completion (on), [2825] the same seen with the switch off,
        // [2826] errors. Load removals count in [2824].
        static readonly Type sLgSurvey = typeof(Lifeguard).GetNestedType("BeachSurvey", BindingFlags.Public | BindingFlags.NonPublic);
        static readonly FieldInfo sLgAlarm = sLgSurvey != null ? sLgSurvey.GetField("mEventTriggerAlarm", kInstAll) : null;
        static readonly MethodInfo sLgRemove = sLgSurvey != null ? sLgSurvey.GetMethod("RemoveAlarm", kInstAll, null, Type.EmptyTypes, null) : null;
        static string sLgMissing = "";

        static void ApplyLifeguardSurvey()
        {
            sLgMissing = ((sLgSurvey == null ? "Lifeguard.BeachSurvey " : "") + (sLgAlarm == null ? "BeachSurvey.mEventTriggerAlarm " : "")
                + (sLgRemove == null ? "BeachSurvey.RemoveAlarm " : "")
                + (typeof(Job).GetMethod("OnCompletion", kInstAll, null, Type.EmptyTypes, null) == null ? "Job.OnCompletion" : "")).Trim();
            if (sLgMissing.Length > 0) { AppliedLog().Add("lifeguardsurvey: not found: " + sLgMissing); return; }
            Patch(typeof(Job), "OnCompletion", Type.EmptyTypes, typeof(BLgHook), "CompleteHook", "CompleteOrig");
        }

        // Pure: remove the survey's event alarm? (a BeachSurvey whose job ended, or whose job is no longer current at load)
        internal static bool LgRemove(bool on, bool isSurvey, bool alarmHeld, bool jobOver)
        {
            return on && isSurvey && alarmHeld && jobOver;
        }

        internal static void LifeguardSurveyWorldLoad()
        {
            if (!ShortcutOn("lifeguardsurvey") || sLgMissing.Length > 0) return;
            try
            {
                List<AlarmHandle> keys = new List<AlarmHandle>(); List<Delegate> cbs = new List<Delegate>();
                B582Alarms(AlarmManager.Global, keys, cbs);
                for (int i = 0; i < keys.Count; i++)
                {
                    Job j = cbs[i].Target as Job;
                    if (cbs[i].Method.Name != "CheckLifeguardEvents" || j == null || !sLgSurvey.IsInstanceOfType(j)) continue;
                    bool over = j.Occupation == null || !object.ReferenceEquals(j.Occupation.CurrentJob, j);
                    if (LgRemove(true, true, true, over)) { AlarmManager.Global.RemoveAlarm(keys[i]); PatchCounters()[2824]++; }
                }
            }
            catch (Exception) { PatchCounters()[2826]++; }
        }

        public class BLgHook
        {
            public void CompleteHook()
            {
                try { CompleteOrig(); }
                finally { After(this); }  // also when EA's OnCompletion throws (review 2026-11-15)
            }
            static void After(object job)
            {
                try
                {
                    if (!sLgSurvey.IsInstanceOfType(job)) return;
                    bool held = (AlarmHandle)sLgAlarm.GetValue(job) != AlarmHandle.kInvalidHandle;
                    if (!held) return;
                    if (LgRemove(ShortcutOn("lifeguardsurvey"), true, held, true)) { sLgRemove.Invoke(job, null); PatchCounters()[2824]++; }
                    else PatchCounters()[2825]++;
                }
                catch (Exception) { PatchCounters()[2826]++; }
            }
            public void CompleteOrig() { } // receives Job.OnCompletion's original body
        }

        // ---- §8 moonriselistener (tuning kMoonRiseListener) --------------------------------------------------------------
        // [verified, decompiled] OccultFairy.SetupSimHandlers and OccultWerewolf.SetupSimHandlers add a kMoonRise listener
        // (EventTracker.AddListener, no owner: never removed with the Sim) into mEventLunarCycleEvent without removing the
        // one held. SetupSimHandlers runs at every instantiation (SimDescription.Instantiate -> OccultManager
        // .SetupForInstantiatedSim -> SetupInstantiatedSim); only OnRemoval (losing the occult) removes the held one. Every
        // load of a fairy or werewolf leaves one more listener, and each moonrise runs all of them (on a destroyed
        // mOwningSim between loads, where the werewolf handler adds alarms to that dead Sim).
        // Fix (at creation): before EA's SetupSimHandlers, a held listener is removed (EA's RemoveListener) and cleared.
        // Counters: [2827] listeners removed before re-add (on), [2828] the same seen with the switch off, [2829] errors.
        static readonly FieldInfo sMrFairy = typeof(OccultFairy).GetField("mEventLunarCycleEvent", kInstAll);
        static readonly FieldInfo sMrWolf = typeof(OccultWerewolf).GetField("mEventLunarCycleEvent", kInstAll);
        static string sMrMissing = "";

        static void ApplyMoonRiseListener()
        {
            sMrMissing = ((sMrFairy == null ? "OccultFairy.mEventLunarCycleEvent " : "") + (sMrWolf == null ? "OccultWerewolf.mEventLunarCycleEvent " : "")
                + (typeof(OccultFairy).GetMethod("SetupSimHandlers", kInstAll | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null) == null ? "OccultFairy.SetupSimHandlers " : "")
                + (typeof(OccultWerewolf).GetMethod("SetupSimHandlers", kInstAll | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null) == null ? "OccultWerewolf.SetupSimHandlers" : "")).Trim();
            if (sMrMissing.Length > 0) { AppliedLog().Add("moonriselistener: not found: " + sMrMissing); return; }
            Patch(typeof(OccultFairy), "SetupSimHandlers", Type.EmptyTypes, typeof(BMrFairyHook), "SetupHook", "SetupOrig");
            Patch(typeof(OccultWerewolf), "SetupSimHandlers", Type.EmptyTypes, typeof(BMrWolfHook), "SetupHook", "SetupOrig");
        }

        // Pure: remove the held listener before EA adds the next?
        internal static bool MrRemoveFirst(bool on, bool held)
        {
            return on && held;
        }

        static void MrBefore(object occult, FieldInfo f)
        {
            try
            {
                EventListener l = f.GetValue(occult) as EventListener;
                if (l == null) return;
                if (!MrRemoveFirst(ShortcutOn("moonriselistener"), true)) { PatchCounters()[2828]++; return; }
                EventTracker.RemoveListener(l);
                f.SetValue(occult, null);
                PatchCounters()[2827]++;
            }
            catch (Exception) { PatchCounters()[2829]++; }
        }

        public class BMrFairyHook
        {
            public void SetupHook() { MrBefore(this, sMrFairy); SetupOrig(); }
            public void SetupOrig() { } // receives OccultFairy.SetupSimHandlers's original body
        }

        public class BMrWolfHook
        {
            public void SetupHook() { MrBefore(this, sMrWolf); SetupOrig(); }
            public void SetupOrig() { } // receives OccultWerewolf.SetupSimHandlers's original body
        }

        // ---- §9 holidaylights (tuning kHolidayLights) --------------------------------------------------------------------
        // [verified, decompiled] HolidayHouseLights.ApplyLightsToWorld (winter starts) picks a share of NPC homes and adds the
        // hourly AlwaysPersisted "Trick On Holiday House Lights" alarm; TrickleOnHouseLights lights
        // sNumLightsToApplyPerPulse lots per hour and removes sTurnOnLightsAlarm when the list is empty. Defects:
        //  (a) sNumLightsToApplyPerPulse = (int)(picked / 12 + 0.5) is 0 for 1-5 picked lots: no light is ever placed, the
        //      list never empties and the alarm repeats for good (small worlds, Low/Medium processor setting);
        //  (b) the add overwrites sTurnOnLightsAlarm without removing the held one;
        //  (c) [corrected 2026-11-15, batch 9 review] the first build said the statics are not saved. [PersistableStatic(false)]
        //      means "not main-menu-only": they ARE saved with the alarm (PostLoadInit reads sCustomLightColors after a load),
        //      so a saved trickle resumes. Making the alarm NeverPersisted (first build) left a saved handle pointing at no
        //      alarm after a load, which stranded the trickle; that part is removed.
        // Fix (at creation): before EA's ApplyLightsToWorld a held trickle alarm is removed, but only when EA will rebuild
        // the lists (sLotsWithLights null; EA returns at once otherwise and a running trickle must keep its alarm, review
        // 2026-11-15: the cheat or a second call mid-winter stranded the remaining lots); after it, a 0 pulse with lots
        // waiting becomes 1. At world load (switch on) TrickleOnHouseLights alarms other than the held handle (left over
        // from (b) in a save) are removed.
        // Counters: [2830] pulses raised from 0 (on), [2831] held alarms removed before the re-add, [2832] saved orphans
        // removed at load, [2833] errors (and the 0 pulse seen with the switch off is not counted separately).
        static readonly FieldInfo sHlAlarm = typeof(HolidayHouseLights).GetField("sTurnOnLightsAlarm", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        static readonly FieldInfo sHlLots = typeof(HolidayHouseLights).GetField("sLotsThatNeedLights", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        static readonly FieldInfo sHlPulse = typeof(HolidayHouseLights).GetField("sNumLightsToApplyPerPulse", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        static readonly FieldInfo sHlWith = typeof(HolidayHouseLights).GetField("sLotsWithLights", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        static string sHlMissing = "";

        static void ApplyHolidayLights()
        {
            sHlMissing = ((sHlAlarm == null ? "HolidayHouseLights.sTurnOnLightsAlarm " : "") + (sHlLots == null ? "HolidayHouseLights.sLotsThatNeedLights " : "")
                + (sHlPulse == null ? "HolidayHouseLights.sNumLightsToApplyPerPulse " : "")
                + (typeof(HolidayHouseLights).GetMethod("ApplyLightsToWorld", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null) == null ? "HolidayHouseLights.ApplyLightsToWorld" : "")).Trim();
            if (sHlMissing.Length > 0) { AppliedLog().Add("holidaylights: not found: " + sHlMissing); return; }
            Patch(typeof(HolidayHouseLights), "ApplyLightsToWorld", Type.EmptyTypes, typeof(BHlHook), "ApplyHook", "ApplyOrig");
        }

        // Pure: the pulse to store (EA's when on is false or lots are not waiting).
        internal static int HlPulse(bool on, int pulse, int waiting)
        {
            return on && pulse <= 0 && waiting > 0 ? 1 : pulse;
        }

        // Pure: which trickle alarms the load sweep removes: every one but the held handle.
        internal static List<int> HlPick(string[] callback, bool[] held)
        {
            List<int> r = new List<int>();
            for (int i = 0; i < callback.Length; i++) if (callback[i] == "TrickleOnHouseLights" && !held[i]) r.Add(i);
            return r;
        }

        internal static void HolidayLightsWorldLoad()
        {
            if (!ShortcutOn("holidaylights") || sHlMissing.Length > 0) return;
            try
            {
                AlarmHandle held = (AlarmHandle)sHlAlarm.GetValue(null);
                List<AlarmHandle> keys = new List<AlarmHandle>(); List<Delegate> cbs = new List<Delegate>();
                B582Alarms(AlarmManager.Global, keys, cbs);
                string[] names = new string[keys.Count]; bool[] isHeld = new bool[keys.Count];
                for (int i = 0; i < keys.Count; i++)
                {
                    names[i] = cbs[i].Method.DeclaringType == typeof(HolidayHouseLights) ? cbs[i].Method.Name : "";
                    isHeld[i] = held != AlarmHandle.kInvalidHandle && keys[i] == held;
                }
                foreach (int i in HlPick(names, isHeld)) { AlarmManager.Global.RemoveAlarm(keys[i]); PatchCounters()[2832]++; }
            }
            catch (Exception) { PatchCounters()[2833]++; }
        }

        public class BHlHook
        {
            public static void ApplyHook()
            {
                bool on = ShortcutOn("holidaylights");
                try
                {
                    AlarmHandle h = (AlarmHandle)sHlAlarm.GetValue(null);
                    if (on && h != AlarmHandle.kInvalidHandle && (sHlWith == null || sHlWith.GetValue(null) == null))
                    {
                        AlarmManager.Global.RemoveAlarm(h);
                        sHlAlarm.SetValue(null, AlarmHandle.kInvalidHandle);
                        PatchCounters()[2831]++;
                    }
                }
                catch (Exception) { PatchCounters()[2833]++; }
                ApplyOrig();
                if (!on) return;
                try
                {
                    List<ulong> lots = sHlLots.GetValue(null) as List<ulong>;
                    int pulse = (int)sHlPulse.GetValue(null);
                    int put = HlPulse(true, pulse, lots != null ? lots.Count : 0);
                    if (put != pulse) { sHlPulse.SetValue(null, put); PatchCounters()[2830]++; }
                }
                catch (Exception) { PatchCounters()[2833]++; }
            }
            public static void ApplyOrig() { } // receives HolidayHouseLights.ApplyLightsToWorld's original body
        }
    }
}
