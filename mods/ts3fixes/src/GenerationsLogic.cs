using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.ChildAndTeenUpdates;
using Sims3.Gameplay.EventSystem;
using Sims3.Gameplay.Objects.Vehicles;
using Sims3.Gameplay.Situations;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // Generations logic fixes (docs/571 §1-4), each off by default. EA bug fixes: household outcomes change only where
    // EA's own code does something other than what it plainly means to do.
    //
    // birthdayagers (kBirthdayAgers, docs/571 §1) [verified, BirthdayParty.cs]: the party's kSimAgeTransition listener
    //   records the ager and returns ListenerAction.Remove, so only the FIRST guest/host who ages up at the party is
    //   recorded; DidSimAgeUpAtParty (wish "age up at a party", challenge feedback) is false for every later ager (twins,
    //   several birthdays at one party). Fix: the listener keeps listening (Keep) while the party runs, and the party's
    //   listeners are removed when it cleans up (EA never removes them; with Remove gone they would outlive the party).
    //   Counters [3960] later agers recorded, [3961] listeners removed at cleanup, [3962] errors.
    // sneakoutlistener (kSneakOutListener, docs/571 §2) [verified, Punishment.cs]: a grounded teen sneaking out gets a
    //   kEventSimMovedToLot listener (SneakOutSuccess). When the teen is caught (SneakOutFailure) or the outing is given up,
    //   nothing removes it, so the teen's NEXT lot change - the school carpool, or any trip after the grounding ended -
    //   applies Snuck Out + Fight the Power. ApplySnuckOutMoodlets also tests mSneakingOnHomeLotListener but removes
    //   mSneakingFromHomeLotListener (copy-paste), so return-home listeners pile up. Fix: SneakOutSuccess applies the
    //   sneak-out only while the teen is still grounded and is not going to school/work/travelling or riding a carpool
    //   (EA's own ShouldApplySnuckOutMoodlet tests); ApplySnuckOutMoodlets first drops an older return-home listener and a
    //   left-over sneak listener. Counters [3963] stale sneak-outs skipped, [3964] duplicate return-home listeners
    //   removed, [3965] left-over sneak listeners removed, [3966] errors.
    // promcarpool (kPromCarpool, docs/571 §3) [verified, PromSituation.cs / CarpoolManager.cs]: the prom schedules a weekly
    //   limo carpool (AddAlarmDay on the prom weekday, rider list = every teen on the active home lot) and CleanUp never
    //   calls RemoveCarpool, so every week until the game is restarted a white limo picks up the household's teens at
    //   prom time and drives them to the old prom venue (the arrival callback belongs to the finished prom). Fix: CleanUp
    //   releases the carpool as careers do (CarpoolManager.RemoveCarpool). Counters [3618] carpools removed, [3619] errors.
    // curfewalarm (kCurfewAlarm, docs/571 §4) [verified, SimUpdate.cs]: SimUpdate.sCurfewCheckPoliceAlarmHandle is a plain
    //   static (Persistable(false)) holding the never-persisted "Police curfew check" alarm. Quitting during curfew keeps
    //   the handle; in the next world it is "valid", so no curfew check alarm is created that night (no police bring
    //   teens home), and when curfew ends RemoveAlarm runs on a handle that may now name an unrelated alarm. Fix: on world
    //   quit the handle is set back to invalid. Counters [3967] handles reset at quit, [3968] errors.
    // Checks: GenerationsLogicChecks.cs.
    public static partial class Fixes
    {
        const BindingFlags kGenInst = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        static readonly FieldInfo sBaListeners = typeof(BirthdayParty).GetField("mEventListeners", kGenInst);
        static readonly MethodInfo sBaAge = typeof(BirthdayParty).GetMethod("OnAgeTransition", kGenInst);
        static readonly FieldInfo sSoOwner = typeof(Punishment).GetField("mOwner", kGenInst);
        static readonly FieldInfo sSoFrom = typeof(Punishment).GetField("mSneakingFromHomeLotListener", kGenInst);
        static readonly FieldInfo sSoOn = typeof(Punishment).GetField("mSneakingOnHomeLotListener", kGenInst);
        static readonly MethodInfo sSoGoing = typeof(Punishment).GetMethod("IsSimGoingToSchoolOrWorkOrTraveling", kGenInst, null, Type.EmptyTypes, null);
        static readonly FieldInfo sPcCarpool = typeof(PromSituation).GetField("mCarpool", kGenInst);
        static readonly FieldInfo sCaHandle = typeof(SimUpdate).GetField("sCurfewCheckPoliceAlarmHandle", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        static bool sCaHooked;

        internal static string BaMissing()
        {
            List<string> m = new List<string>();
            if (sBaListeners == null) m.Add("BirthdayParty.mEventListeners");
            if (sBaAge == null) m.Add("BirthdayParty.OnAgeTransition");
            return string.Join(", ", m.ToArray());
        }

        internal static string SoMissing()
        {
            List<string> m = new List<string>();
            if (sSoOwner == null) m.Add("Punishment.mOwner");
            if (sSoFrom == null) m.Add("Punishment.mSneakingFromHomeLotListener");
            if (sSoOn == null) m.Add("Punishment.mSneakingOnHomeLotListener");
            if (sSoGoing == null) m.Add("Punishment.IsSimGoingToSchoolOrWorkOrTraveling");
            return string.Join(", ", m.ToArray());
        }

        static void ApplyGenerationsLogic()
        {
            if (BaMissing().Length > 0) AppliedLog().Add("birthdayagers: members not found (" + BaMissing() + "), not patched");
            else
            {
                Patch(typeof(BirthdayParty), "OnAgeTransition", typeof(BirthdayAgeHook), "AgeHook", "AgeOrig");
                Patch(typeof(HouseParty), "CleanUp", typeof(BirthdayCleanHook), "CleanHook", "CleanOrig");
            }
            if (SoMissing().Length > 0) AppliedLog().Add("sneakoutlistener: members not found (" + SoMissing() + "), not patched");
            else
            {
                Patch(typeof(Punishment), "SneakOutSuccess", typeof(SneakOutHook), "SuccessHook", "SuccessOrig");
                Patch(typeof(Punishment), "ApplySnuckOutMoodlets", typeof(SneakOutHook), "MoodletsHook", "MoodletsOrig");
            }
            if (sPcCarpool == null) AppliedLog().Add("promcarpool: PromSituation.mCarpool not found, not patched");
            else Patch(typeof(PromSituation), "CleanUp", typeof(PromCarpoolHook), "CleanHook", "CleanOrig");
            if (!sCaHooked)
            {
                sCaHooked = true;
                World.OnWorldQuitEventHandler += delegate { CurfewAlarmQuit(); };
            }
        }

        // ---- birthdayagers ----
        // The listener's answer: EA's, except Remove becomes Keep while the switch is on (the party's CleanUp removes it).
        internal static ListenerAction BaAnswer(bool on, ListenerAction ea)
        {
            return on && ea == ListenerAction.Remove ? ListenerAction.Keep : ea;
        }

        public class BirthdayAgeHook
        {
            public ListenerAction AgeHook(Sims3.Gameplay.EventSystem.Event e)
            {
                ListenerAction r = AgeOrig(e);
                try
                {
                    ListenerAction f = BaAnswer(ShortcutOn("birthdayagers"), r);
                    if (f != r) PatchCounters()[3960]++;
                    return f;
                }
                catch (Exception) { PatchCounters()[3962]++; return r; }
            }
            public ListenerAction AgeOrig(Sims3.Gameplay.EventSystem.Event e) { return ListenerAction.Keep; }
        }

        public class BirthdayCleanHook
        {
            public void CleanHook()
            {
                CleanOrig();
                try
                {
                    BirthdayParty bp = (object)this as BirthdayParty;
                    if (bp == null || !ShortcutOn("birthdayagers")) return;
                    List<EventListener> ls = sBaListeners.GetValue(bp) as List<EventListener>;
                    if (ls == null) return;
                    foreach (EventListener l in ls)
                    {
                        if (l == null) continue;
                        EventTracker.RemoveListener(l);
                        PatchCounters()[3961]++;
                    }
                    ls.Clear();
                }
                catch (Exception) { PatchCounters()[3962]++; }
            }
            public void CleanOrig() { }
        }

        // ---- sneakoutlistener ----
        // Skip EA's sneak-out success? Only a stale listener: the teen is no longer grounded, or is on the way to
        // school/work/travel or riding a carpool (EA's own ShouldApplySnuckOutMoodlet tests).
        internal static bool SoSkip(bool on, bool grounded, bool goingToSchoolWorkTravel, bool inCarpool)
        {
            return on && (!grounded || goingToSchoolWorkTravel || inCarpool);
        }

        public class SneakOutHook
        {
            public ListenerAction SuccessHook(Sims3.Gameplay.EventSystem.Event e)
            {
                Punishment me = (Punishment)(object)this;
                try
                {
                    Sim owner = sSoOwner.GetValue(me) as Sim;
                    if (owner != null && ShortcutOn("sneakoutlistener"))
                    {
                        bool grounded = owner.BuffManager != null && owner.BuffManager.HasElement(BuffNames.Grounded);
                        bool going = (bool)sSoGoing.Invoke(me, null);
                        bool carpool = owner.CurrentInteraction is CarpoolManager.GetInCarpool;
                        if (SoSkip(true, grounded, going, carpool))
                        {
                            me.StopSimSneaking(true);   // as EA's success path, without the moodlets and the event
                            sSoFrom.SetValue(me, null);
                            PatchCounters()[3963]++;
                            return ListenerAction.Remove;
                        }
                    }
                }
                catch (Exception) { PatchCounters()[3966]++; }
                return SuccessOrig(e);
            }
            public ListenerAction SuccessOrig(Sims3.Gameplay.EventSystem.Event e) { return ListenerAction.Remove; }

            public void MoodletsHook()
            {
                Punishment me = (Punishment)(object)this;
                try
                {
                    if (ShortcutOn("sneakoutlistener"))
                    {
                        EventListener on = sSoOn.GetValue(me) as EventListener, from = sSoFrom.GetValue(me) as EventListener;
                        if (on != null) { EventTracker.RemoveListener(on); sSoOn.SetValue(me, null); PatchCounters()[3964]++; }
                        if (from != null) { EventTracker.RemoveListener(from); sSoFrom.SetValue(me, null); PatchCounters()[3965]++; }
                    }
                }
                catch (Exception) { PatchCounters()[3966]++; }
                MoodletsOrig();
            }
            public void MoodletsOrig() { }
        }

        // ---- promcarpool ----
        internal static bool PcRemove(bool on, bool hasCarpool, bool stillScheduled)
        {
            return on && hasCarpool && stillScheduled;
        }

        public class PromCarpoolHook
        {
            public void CleanHook()
            {
                CarpoolManager.CarpoolData cp = null;
                try { cp = sPcCarpool.GetValue((object)this) as CarpoolManager.CarpoolData; } catch (Exception) { PatchCounters()[3619]++; }
                CleanOrig();
                try
                {
                    if (!PcRemove(ShortcutOn("promcarpool"), cp != null, cp != null && CarpoolManager.sScheduledCarpools.Contains(cp))) return;
                    CarpoolManager.Singleton.RemoveCarpool(cp, null);
                    sPcCarpool.SetValue((object)this, null);
                    PatchCounters()[3618]++;
                }
                catch (Exception) { PatchCounters()[3619]++; }
            }
            public void CleanOrig() { }
        }

        // ---- curfewalarm ----
        internal static bool CaReset(bool on, bool handleValid)
        {
            return on && handleValid;
        }

        static void CurfewAlarmQuit()
        {
            try
            {
                if (sCaHandle == null) return;
                AlarmHandle h = (AlarmHandle)sCaHandle.GetValue(null);
                if (!CaReset(ShortcutOn("curfewalarm"), h != AlarmHandle.kInvalidHandle)) return;
                sCaHandle.SetValue(null, AlarmHandle.kInvalidHandle);
                PatchCounters()[3967]++;
            }
            catch (Exception) { PatchCounters()[3968]++; }
        }
    }
}
