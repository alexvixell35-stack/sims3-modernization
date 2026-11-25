using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Controllers;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Objects.Electronics;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // Phone logic fixes (docs/573 §1-6), each off by default. EA bug fixes, every Sim; no Sim is moved.
    //
    // phoneloadreset (kPhoneLoadReset, docs/573 §1) [code verified, in-game trigger inferred]: a phone keeps its call
    //   (mIncomingCall) and HandsetPickedUp in the save. Interactions are not saved, so a save made during a call (or the
    //   home-world save when part of the household leaves for university while one Sim is on the phone) loads a phone that
    //   still holds an answered call or a lifted handset, and nothing clears either: Phone.OnLoad is base.OnLoad only
    //   (PhoneSmart.OnLoad recomputes the handset but keeps the call). Every later PlaceCall on that phone is refused
    //   (IsTiedUp), and PhoneService.PlaceCallInternal aborts any incoming call for that household as LineBusy. Fix: at
    //   world load, on each registered phone a call that is no longer pending is dropped and a lifted handset (not on a
    //   smartphone, whose OnLoad already sets it from its hand object) is put back the way Phone.StopCall does. Pending
    //   (ringing) calls are left to EA's persisted timeout. Counters [2636] phones cleared, [2637] errors.
    // phonecallschedule (kPhoneCallSchedule, docs/573 §2) [verified, Phone.ScheduleNextCall]: the random-call alarm is set
    //   at world load with interval 0; before 7:00 EA's else branch gives 24 - hour + 7, so a load or travel arrival at
    //   3:00 waits until 7:00 the next day and the whole first day gets no random calls. Fix: when the next allowed time is
    //   still before today's 7:00 the window starts at today's 7:00 (and ends at today's 22:00), as EA's own branch does
    //   from 7:00 on. Counters [2638] windows corrected, [2639] errors.
    // phonecallrearm (kPhoneCallRearm, docs/573 §3) [verified, Phone.FindCallParticipants / Sim.IsRelated]: while a prom
    //   exists the ask-to-prom branch calls sim.IsRelated(caller.CreatedSim), which throws for a teen caller who is not
    //   instantiated (most townie teens). The exception leaves ConsiderGeneratingPhoneCall before ScheduleNextCall, so the
    //   random-call alarm is never set again: no more random chats, invitations, prom or bachelor-party calls until the
    //   next world load. Fix: when ConsiderGeneratingPhoneCall throws, the alarm is set again exactly as its last line
    //   would (ScheduleNextCall(kMinimumCallInterval)). Counters [2640] alarms re-armed, [2641] errors.
    // woohootywait (kWooHootyWait, docs/573 §4) [verified, Phone.SendWooHootyText.GoToLotSuccess]: when the invitee
    //   arrives, the texter's queue is cancelled and the invitee's own interaction thread waits in
    //   while (Actor.CurrentInteraction != null) Sleep(5) with no exit: an uncancellable current interaction, autonomy
    //   refilling the queue between polls, or a texter who left or was destroyed keeps the invitee frozen there. Fix: the
    //   same steps with the wait ended when either Sim is gone or after kWwCap polls; then no WooHoo is pushed (EA never
    //   reached that point either). A wait that ends as EA's does runs EA's steps unchanged. Counters [2642] waits ended
    //   early, [2643] errors.
    // invitecallreply (kInviteCallReply, docs/573 §5) [verified, Phone.RandomInvitationCall]: GetQueryResponse returns
    //   RespondPositively for both buttons (copy-paste), so "No" plays the accept reply; OnAnswered reads
    //   mCaller.Household.LotHome before its own mCaller null check, so a caller who moved out of every household during the
    //   ring throws inside the Answer interaction (the answering Sim is reset). Fix: "No" returns RespondNegatively; the
    //   null-household case ends after EA's relationship and event steps instead of throwing. Counters [2644] replies
    //   corrected, [2645] errors.
    // gradvisitors (kGradVisitors, docs/573 §6) [verified, Phone.Call.GetAllValidCallees(Sim, float), only caller
    //   CallInviteOverVisitorsToGraduation; spawn at Annex inferred]: the graduation visitor list adds every Sim with
    //   enough liking, with none of CanCallSim's first checks, so dead Sims, toddlers, babies, pets, uncontactable Sims and
    //   Sims on a free vacation are offered and the ceremony instantiates the ones picked. Fix: those entries are dropped
    //   with CanCallSim's own rules (a playable ghost stays). Counters [2646] entries dropped, [2647] errors.
    // Checks: PhoneLogicChecks.cs.
    public static partial class Fixes
    {
        const BindingFlags kPhAll = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
        const int kWwCap = HhAuditCore.kWwCap;  // polls of 5 ticks (EA's own sleep) before the WooHooty wait gives up, for every Sim (docs/289)
        static readonly FieldInfo sPhIncoming = typeof(Phone).GetField("mIncomingCall", kPhAll);
        static readonly FieldInfo sPhHandset = typeof(Phone).GetField("HandsetPickedUp", kPhAll);
        static readonly MethodInfo sPhSetState = typeof(Phone).GetMethod("SetHandsetState", kPhAll);
        static readonly FieldInfo sPhService = typeof(PhoneService).GetField("sPhoneService", kPhAll);
        static readonly FieldInfo sPhPhones = typeof(PhoneService).GetField("mPhones", kPhAll);
        static readonly FieldInfo sPhAlarm = typeof(Phone).GetField("mPhoneCallAttemptAlarm", kPhAll);
        static readonly FieldInfo sPhInterval = typeof(Phone).GetField("kMinimumCallInterval", kPhAll);
        static readonly FieldInfo sPhWindow = typeof(Phone).GetField("kMinimumCallWindow", kPhAll);
        static readonly MethodInfo sPhConsider = typeof(Phone).GetMethod("ConsiderGeneratingPhoneCall", kPhAll);
        static readonly MethodInfo sPhSchedule = typeof(Phone).GetMethod("ScheduleNextCall", kPhAll);
        static readonly Type sPiType = typeof(Phone).GetNestedType("RandomInvitationCall", kPhAll);
        static readonly FieldInfo sPiAccepted = sPiType == null ? null : sPiType.GetField("mbInvitationAccepted", kPhAll);
        static readonly FieldInfo sPiCaller = sPiType == null ? null : sPiType.GetField("mCaller", kPhAll);
        static readonly MethodInfo sGvCallees = typeof(Phone.Call).GetMethod("GetAllValidCallees", kPhAll, null, new Type[] { typeof(Sim), typeof(float) }, null);
        static readonly MethodInfo sWwSuccess = typeof(Phone.SendWooHootyText).GetMethod("GoToLotSuccess", kPhAll);

        delegate void PhIntFn(int interval);
        static PhIntFn sPhScheduleFn;            // bound once: ScheduleNextCall may be swapped (phonecallschedule)
        static AlarmTimerCallback sPhConsiderFn;

        internal static string PhMissing()
        {
            string m = "";
            if (sPhIncoming == null) m += "Phone.mIncomingCall ";
            if (sPhHandset == null) m += "Phone.HandsetPickedUp ";
            if (sPhSetState == null) m += "Phone.SetHandsetState ";
            if (sPhService == null) m += "PhoneService.sPhoneService ";
            if (sPhPhones == null) m += "PhoneService.mPhones ";
            if (sPhAlarm == null) m += "Phone.mPhoneCallAttemptAlarm ";
            if (sPhInterval == null) m += "Phone.kMinimumCallInterval ";
            if (sPhWindow == null) m += "Phone.kMinimumCallWindow ";
            if (sPhConsider == null) m += "Phone.ConsiderGeneratingPhoneCall ";
            if (sPhSchedule == null) m += "Phone.ScheduleNextCall ";
            if (sPiAccepted == null) m += "Phone.RandomInvitationCall.mbInvitationAccepted ";
            if (sPiCaller == null) m += "Phone.RandomInvitationCall.mCaller ";
            if (sGvCallees == null) m += "Phone.Call.GetAllValidCallees(Sim,float) ";
            if (sWwSuccess == null) m += "Phone.SendWooHootyText.GoToLotSuccess ";
            return m.Trim();
        }

        static void ApplyPhoneLogic()
        {
            if (sPhAlarm != null && sPhWindow != null && sPhConsider != null)
                Patch(typeof(Phone), "ScheduleNextCall", typeof(PhCallScheduleHook), "SchedHook", "SchedOrig");
            if (sPhInterval != null && sPhSchedule != null)
                Patch(typeof(Phone), "ConsiderGeneratingPhoneCall", typeof(PhCallRearmHook), "ConsiderHook", "ConsiderOrig");
            if (sWwSuccess != null) Patch(typeof(Phone.SendWooHootyText), "GoToLotSuccess", typeof(WooHootyWaitHook), "WaitHook", "WaitOrig");
            if (sPiAccepted != null && sPiCaller != null)
            {
                Patch(sPiType, "GetQueryResponse", typeof(InviteReplyHook), "ReplyHook", "ReplyOrig");
                Patch(sPiType, "OnAnswered", typeof(InviteReplyHook), "AnsweredHook", "AnsweredOrig");
            }
            if (sGvCallees != null)
                Patch(typeof(Phone.Call), "GetAllValidCallees", new Type[] { typeof(Sim), typeof(float) }, typeof(GradVisitorHook), "CalleesHook", "CalleesOrig");
            if (PhMissing().Length > 0) AppliedLog().Add("phone logic: members not found (" + PhMissing() + ")");
        }

        // ---- phoneloadreset ----
        // What a loaded phone needs: dropCall = a call that is no longer pending (nobody can be on it after a load);
        // dropHandset = a lifted handset (a smartphone's is set from its hand object by EA's own OnLoad).
        internal static void PhLoadAction(bool on, bool hasCall, bool pending, bool handsetUp, bool isSmart, out bool dropCall, out bool dropHandset)
        {
            dropCall = on && hasCall && !pending;
            dropHandset = on && handsetUp && !isSmart;
        }

        // World load finished (Instantiator): every phone PhoneService knows (the set PlaceCallInternal asks).
        public static void PhoneLoadResetWorldLoad()
        {
            try
            {
                if (!ShortcutOn("phoneloadreset") || sPhIncoming == null || sPhHandset == null || sPhSetState == null || sPhService == null || sPhPhones == null) return;
                object service = sPhService.GetValue(null);
                List<IPhone> phones = service == null ? null : sPhPhones.GetValue(service) as List<IPhone>;
                if (phones == null) return;
                foreach (IPhone ip in phones.ToArray())  // copy: SetHandsetState runs object code
                {
                    Phone p = ip as Phone;
                    if (p == null) continue;
                    try
                    {
                        PhoneCall call = sPhIncoming.GetValue(p) as PhoneCall;
                        bool up = (bool)sPhHandset.GetValue(p), dropCall, dropHandset;
                        PhLoadAction(true, call != null, call != null && call.IsPending, up, p is PhoneSmart, out dropCall, out dropHandset);
                        if (dropCall) sPhIncoming.SetValue(p, null);
                        if (dropHandset) { sPhSetState.Invoke(p, new object[] { false }); p.SetHandsetInUse(false); }
                        if (dropCall || dropHandset) PatchCounters()[2636]++;
                    }
                    catch (Exception) { PatchCounters()[2637]++; }
                }
            }
            catch (Exception) { PatchCounters()[2637]++; }
        }

        // ---- phonecallschedule ----
        // EA's window (hours from now) for the next random-call try; true when the fix moved it to today's allowed hours.
        internal static bool PhWindow(bool on, int hours, int interval, int okay, int late, int minWindow, out int from, out int to)
        {
            if (hours + interval >= okay && hours + interval + minWindow < late) { from = interval; to = late - hours; return false; }
            if (on && hours + interval < okay) { from = okay - hours; to = late - hours; return true; }
            from = 24 - hours + okay; to = 24 - hours + late;
            return false;
        }

        public class PhCallScheduleHook
        {
            public static void SchedHook(int interval)
            {
                try
                {
                    if (ShortcutOn("phonecallschedule"))
                    {
                        int from, to;
                        if (PhWindow(true, SimClock.Hours24, interval, PhoneService.HourItsOkayToCall, PhoneService.HourItsTooLateToCall, (int)sPhWindow.GetValue(null), out from, out to))
                        {
                            if (sPhConsiderFn == null) sPhConsiderFn = (AlarmTimerCallback)Delegate.CreateDelegate(typeof(AlarmTimerCallback), sPhConsider);
                            float time = RandomUtil.GetFloat((float)from * 60f, (float)to * 60f - 1f);
                            sPhAlarm.SetValue(null, AlarmManager.Global.AddAlarm(time, TimeUnit.Minutes, sPhConsiderFn, "PhoneCall", AlarmType.NeverPersisted, null));
                            PatchCounters()[2638]++;
                            return;
                        }
                    }
                }
                catch (Exception) { PatchCounters()[2639]++; }
                SchedOrig(interval);
            }
            public static void SchedOrig(int interval) { }
        }

        // ---- phonecallrearm ----
        // Runs body; when it throws, on = rearm and return true (the alarm is set again), off = EA (the exception goes on).
        static bool PhRunGuarded(bool on, SlBoolFn body, SlBoolFn rearm)
        {
            try { body(); return false; }
            catch (Exception)
            {
                if (!on) throw;
                rearm();
                return true;
            }
        }

        public class PhCallRearmHook
        {
            public static void ConsiderHook()
            {
                if (!ShortcutOn("phonecallrearm")) { ConsiderOrig(); return; }
                try
                {
                    if (PhRunGuarded(true, delegate { ConsiderOrig(); return true; }, delegate
                    {
                        if (sPhScheduleFn == null) sPhScheduleFn = (PhIntFn)Delegate.CreateDelegate(typeof(PhIntFn), sPhSchedule);
                        sPhScheduleFn((int)sPhInterval.GetValue(null));
                        return true;
                    })) PatchCounters()[2640]++;
                }
                catch (Exception) { PatchCounters()[2641]++; }
            }
            public static void ConsiderOrig() { }
        }

        // ---- woohootywait ----
        // One poll of the wait for the texter to be free: 1 = keep waiting, 0 = free (go on as EA), 2 = give up.
        internal static int WwPoll(bool on, int polls, bool actorGone, bool simGone, bool actorBusy)
        {
            return HhAuditCore.WwPoll(on, polls, actorGone, simGone, actorBusy);
        }

        public class WooHootyWaitHook
        {
            public void WaitHook(Sim sim, float f)
            {
                if (!ShortcutOn("woohootywait")) { WaitOrig(sim, f); return; }
                try
                {
                    Phone.SendWooHootyText me = (Phone.SendWooHootyText)(object)this;
                    Sim actor = me.Actor;
                    if (actor == null) return;
                    Sims3.Gameplay.Socializing.Relationship rel = Sims3.Gameplay.Socializing.Relationship.Get(actor.SimDescription, me.mOtherSimDesc as SimDescription, false);
                    if (rel == null) return;
                    rel.STC.Set(actor, sim, Sims3.Gameplay.Socializing.CommodityTypes.Amorous, 500f);
                    actor.InteractionQueue.CancelAllInteractions();
                    for (int polls = 0; ; polls++)
                    {
                        int r = WwPoll(true, polls, actor.HasBeenDestroyed, sim == null || sim.HasBeenDestroyed, actor.CurrentInteraction != null);
                        if (r == 2) { PatchCounters()[2642]++; return; }
                        if (r == 0) break;
                        Simulator.Sleep(5u);
                    }
                    Sims3.Gameplay.Interfaces.IBed bed = Sims3.Gameplay.Socializing.SocialCallback.FindNearestBed(actor, sim);
                    if (bed != null) bed.MakeSimsWooHootyOnMe(actor, sim);
                }
                catch (ResetException) { throw; }
                catch (Exception) { PatchCounters()[2643]++; }
            }
            public void WaitOrig(Sim sim, float f) { }
        }

        // ---- invitecallreply ----
        internal static PhoneCall.QueryResponse PiResponse(bool on, bool accepted, PhoneCall.QueryResponse ea)
        {
            return on && !accepted ? PhoneCall.QueryResponse.RespondNegatively : ea;
        }

        // True when EA's OnAnswered would throw at mCaller.Household.LotHome and the fix ends it there instead.
        internal static bool PiEndEarly(bool on, bool accepted, bool callerNull, bool householdNull)
        {
            return on && accepted && !callerNull && householdNull;
        }

        public class InviteReplyHook
        {
            public PhoneCall.QueryResponse ReplyHook(Sim actor)
            {
                PhoneCall.QueryResponse r = ReplyOrig(actor);
                try
                {
                    if (ShortcutOn("invitecallreply"))
                    {
                        PhoneCall.QueryResponse f = PiResponse(true, (bool)sPiAccepted.GetValue((object)this), r);
                        if (f != r) { PatchCounters()[2644]++; r = f; }
                    }
                }
                catch (Exception) { PatchCounters()[2645]++; }
                return r;
            }
            public PhoneCall.QueryResponse ReplyOrig(Sim actor) { return PhoneCall.QueryResponse.JustHangUp; }

            public void AnsweredHook()
            {
                try
                {
                    if (ShortcutOn("invitecallreply"))
                    {
                        SimDescription caller = sPiCaller.GetValue((object)this) as SimDescription;
                        PhoneCall call = (PhoneCall)(object)this;
                        Sim callee = call.TargetSim;
                        if (PiEndEarly(true, (bool)sPiAccepted.GetValue((object)this), caller == null, caller != null && caller.Household == null) && callee != null)
                        {
                            // EA's steps before the line that throws
                            Sims3.Gameplay.Socializing.Relationship.Get(callee.SimDescription, caller, true);
                            Sims3.Gameplay.EventSystem.EventTracker.SendEvent(new Sims3.Gameplay.EventSystem.SimDescriptionTargetEvent(Sims3.Gameplay.EventSystem.EventTypeId.kWasInvitedOver, callee, caller));
                            PatchCounters()[2644]++;
                            return;
                        }
                    }
                }
                catch (Exception) { PatchCounters()[2645]++; }
                AnsweredOrig();
            }
            public void AnsweredOrig() { }
        }

        // ---- gradvisitors ----
        // CanCallSim's first checks for a receiver (dead but not a playable ghost, uncontactable, toddler or below, pet,
        // on a free vacation).
        internal static bool GvDrop(bool on, bool dead, bool playableGhost, bool contactable, bool toddlerOrBelow, bool pet, bool freeVacation)
        {
            return on && ((dead && !playableGhost) || !contactable || toddlerOrBelow || pet || freeVacation);
        }

        public class GradVisitorHook
        {
            public static List<Sims3.UI.PhoneSimPicker.SimPickerInfo> CalleesHook(Sim actor, float ltrThreshold)
            {
                List<Sims3.UI.PhoneSimPicker.SimPickerInfo> list = CalleesOrig(actor, ltrThreshold);
                try
                {
                    if (list != null && ShortcutOn("gradvisitors"))
                    {
                        for (int i = list.Count - 1; i >= 0; i--)
                        {
                            Sims3.UI.CAS.IMiniSimDescription d = list[i].SimDescription as Sims3.UI.CAS.IMiniSimDescription;
                            if (d == null) continue;
                            if (GvDrop(true, d.IsDead, d.IsPlayableGhost, d.IsContactable, d.ToddlerOrBelow, d.IsPet,
                                Sims3.Gameplay.Situations.ParentsLeavingTownSituation.IsSimOnFreeVacation(d.SimDescriptionId)))
                            {
                                list.RemoveAt(i);
                                PatchCounters()[2646]++;
                            }
                        }
                    }
                }
                catch (Exception) { PatchCounters()[2647]++; }
                return list;
            }
            public static List<Sims3.UI.PhoneSimPicker.SimPickerInfo> CalleesOrig(Sim actor, float ltrThreshold) { return null; }
        }
    }
}
