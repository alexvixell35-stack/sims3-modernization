using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Careers;
using Sims3.Gameplay.EventSystem;
using Sims3.Gameplay.Opportunities;
using Sims3.Gameplay.Skills;
using Sims3.Gameplay.Socializing;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;
using Sims3.SimIFace.CustomContent;
using Sims3.UI;
using Sims3.UI.Hud;

namespace Ts3Fixes
{
    // Batch 8b (docs/582 B8b; coverage sweep docs/639 §4-§5). All default off. simler90 changes none of these methods
    // and none was hooked before. BuffImminentNemesis (coverage #5) needs an EA-intent decision and is not built.
    public static partial class Fixes
    {
        // ---- B8b §1 midlifewishsave (tuning kMidlifeWishSave) ------------------------------------------------------------
        // [verified omission; travel effect inferred] MidlifeCrisisManager.ExportContent / ImportContent (the travel and
        // world-move path: SimDescription.ExportContent writes it as a child) save 8 fields but not mWishesFulfilled or
        // mLastUpdatedDate. A Sim in a midlife crisis who travels comes back with 0 wishes counted (the "Unfulfilled" ending,
        // no lifetime-happiness reward) and a zero update date (GetMinutesLeft's day test). Fix: both are written after EA's
        // fields under our own keys and read back after EA's import; a stream without them (vanilla, or switch off at export)
        // leaves EA's values. Household-visible: yes (the crisis ending after a trip).
        // Counters: [3651] values saved or restored, [3652] exports with wishes that EA drops (switch off), [3653] errors.
        const uint kMcWishKey = 0x7F3F1A01u, kMcDateKey = 0x7F3F1A02u;
        static readonly FieldInfo sMcWishes = typeof(MidlifeCrisisManager).GetField("mWishesFulfilled", kInstAll);
        static readonly FieldInfo sMcDate = typeof(MidlifeCrisisManager).GetField("mLastUpdatedDate", kInstAll);

        // Pure: restore a read value? (uint.MaxValue = key missing)
        internal static bool McRestore(bool on, uint read) { return on && read != uint.MaxValue; }

        static void ApplyMidlifeWishSave()
        {
            if (sMcWishes == null || sMcDate == null) { AppliedLog().Add("midlifewishsave: not found: MidlifeCrisisManager.mWishesFulfilled / mLastUpdatedDate"); return; }
            Type[] ex = new Type[] { typeof(ResKeyTable), typeof(ObjectIdTable), typeof(IPropertyStreamWriter) };
            Type[] im = new Type[] { typeof(ResKeyTable), typeof(ObjectIdTable), typeof(IPropertyStreamReader) };
            Patch(typeof(MidlifeCrisisManager), "ExportContent", ex, typeof(BMcHook), "ExportHook", "ExportOrig");
            Patch(typeof(MidlifeCrisisManager), "ImportContent", im, typeof(BMcHook), "ImportHook", "ImportOrig");
        }

        public class BMcHook
        {
            public bool ExportHook(ResKeyTable r, ObjectIdTable o, IPropertyStreamWriter w)
            {
                bool ok = ExportOrig(r, o, w);
                try
                {
                    uint wishes = (uint)sMcWishes.GetValue(this);
                    if (!ShortcutOn("midlifewishsave")) { if (wishes > 0) PatchCounters()[3652]++; return ok; }
                    w.WriteUint32(kMcWishKey, wishes);
                    w.WriteInt64(kMcDateKey, ((DateAndTime)sMcDate.GetValue(this)).Ticks);
                    PatchCounters()[3651]++;
                }
                catch (Exception) { PatchCounters()[3653]++; }
                return ok;
            }
            public bool ExportOrig(ResKeyTable r, ObjectIdTable o, IPropertyStreamWriter w) { return false; } // receives MidlifeCrisisManager.ExportContent

            public bool ImportHook(ResKeyTable r, ObjectIdTable o, IPropertyStreamReader rd)
            {
                bool ok = ImportOrig(r, o, rd);
                try
                {
                    uint wishes;
                    long ticks;
                    rd.ReadUint32(kMcWishKey, out wishes, uint.MaxValue);
                    if (McRestore(ShortcutOn("midlifewishsave"), wishes))
                    {
                        sMcWishes.SetValue(this, wishes);
                        if (rd.ReadInt64(kMcDateKey, out ticks, long.MinValue) && ticks != long.MinValue) sMcDate.SetValue(this, new DateAndTime(ticks));
                        PatchCounters()[3651]++;
                    }
                }
                catch (Exception) { PatchCounters()[3653]++; }
                return ok;
            }
            public bool ImportOrig(ResKeyTable r, ObjectIdTable o, IPropertyStreamReader rd) { return false; } // receives MidlifeCrisisManager.ImportContent
        }

        // ---- B8b §2 innerbeautyonce (tuning kInnerBeautyOnce) ------------------------------------------------------------
        // [verified] Each Inner Beauty buff instance adds its own kDidMeanSocial listener (unfiltered), each bound to
        // that instance's OnDidMeanSocial, which acts on e.Actor, not on its own Sim. With N Inner Beauty Sims one mean social runs N
        // times: N "MeanSocialTNS" notices and N timeout resets. Fix: each event (EventTracker makes a new Event per send)
        // is handled once; the other listeners' calls for the same event return Keep. Household-visible: yes (one notice).
        // Also (review 2026-11-15): EA shows the notice outside its `if (buff != null)`, so every selectable Sim's mean
        // social says "Inner Beauty" whether or not that Sim has the buff; with the switch on an actor without the buff gets
        // nothing (EA's reset does nothing for it either). Counted in [2923] / [2924].
        // Counters: [2923] duplicate calls skipped, [2924] duplicates seen with the switch off, [2925] errors.
        static Event sIbLast;
        // Pure: skip this call? same: the event was handled by another listener already; hasBuff: the actor has Inner Beauty.
        internal static bool IbSkip(bool on, bool same, bool hasBuff) { return on && (same || !hasBuff); }

        static void ApplyInnerBeautyOnce()
        {
            Patch(typeof(InnerBeauty.BuffInstanceInnerBeauty), "OnDidMeanSocial", new Type[] { typeof(Event) }, typeof(BIbHook), "MeanHook", "MeanOrig");
        }

        public class BIbHook
        {
            public ListenerAction MeanHook(Event e)
            {
                bool same = e != null && ReferenceEquals(e, sIbLast);
                sIbLast = e;
                bool hasBuff = true;
                try
                {
                    Sim a = e == null ? null : e.Actor as Sim;
                    hasBuff = a == null || a.BuffManager == null || a.BuffManager.HasElement(BuffNames.InnerBeauty);
                }
                catch (Exception) { PatchCounters()[2925]++; }
                if (same || !hasBuff)
                {
                    if (IbSkip(ShortcutOn("innerbeautyonce"), same, hasBuff)) { PatchCounters()[2923]++; return ListenerAction.Keep; }
                    PatchCounters()[2924]++;
                }
                return MeanOrig(e);
            }
            public ListenerAction MeanOrig(Event e) { return ListenerAction.Keep; } // receives InnerBeauty.BuffInstanceInnerBeauty.OnDidMeanSocial
        }

        // ---- B8b §3 betrayednullsim (tuning kBetrayedNullSim) ------------------------------------------------------------
        // [verified null path; reach inferred low] BuffInstanceBetrayed.ShowTNSAndBreakUp shows the notice when
        // `(createdSim != null && createdSim.IsSelectable) || victimSim.IsSelectable`, then reads createdSim.IsFemale: with a
        // selectable victim and a betrayer who is not instantiated it throws, and BreakUpWithBetrayer never runs. Fix: in that
        // case the notice takes the betrayer's sex from its SimDescription (what IsFemale returns for a live Sim), then EA's
        // break-up runs. Household-visible: yes (the victim is selectable).
        // Counters: [3654] notices shown and break-ups done, [3655] seen with the switch off, [3656] errors.
        delegate string BtLocFn(bool[] isFemales, string key, object[] args);
        // by its argument types: LocalizeReputationKey has a (bool, string, params object[]) overload too, and a lookup by name
        // alone throws AmbiguousMatchException, which in a static initializer kills the whole Fixes class (r43 load hang)
        static readonly MethodInfo sBtLoc = typeof(RomanceVisibilityState).GetMethod("LocalizeReputationKey", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, new Type[] { typeof(bool[]), typeof(string), typeof(object[]) }, null);

        // Pure: does EA's method throw? (selectable victim, betrayer not instantiated)
        internal static bool BtThrows(bool betrayerLive, bool victimSelectable) { return !betrayerLive && victimSelectable; }

        static void ApplyBetrayedNullSim()
        {
            if (sBtLoc == null) { AppliedLog().Add("betrayednullsim: not found: RomanceVisibilityState.LocalizeReputationKey"); return; }
            Patch(typeof(BuffBetrayed.BuffInstanceBetrayed), "ShowTNSAndBreakUp", new Type[] { typeof(Sim), typeof(SimDescription) }, typeof(BBtHook), "ShowHook", "ShowOrig");
        }

        public class BBtHook
        {
            public void ShowHook(Sim victimSim, SimDescription naughtySimDesc)
            {
                string text = null;
                try
                {
                    if (victimSim != null && naughtySimDesc != null && BtThrows(naughtySimDesc.CreatedSim != null, victimSim.IsSelectable))
                    {
                        if (!ShortcutOn("betrayednullsim")) PatchCounters()[3655]++;
                        else
                        {
                            BtLocFn loc = (BtLocFn)Delegate.CreateDelegate(typeof(BtLocFn), sBtLoc);
                            text = loc(new bool[] { naughtySimDesc.IsFemale, victimSim.IsFemale }, "BetrayalFail", new object[] { naughtySimDesc, victimSim.SimDescription });
                        }
                    }
                }
                catch (Exception) { PatchCounters()[3656]++; text = null; }
                if (text == null) { ShowOrig(victimSim, naughtySimDesc); return; }
                PatchCounters()[3654]++;
                StyledNotification.Show(new StyledNotification.Format(text, StyledNotification.NotificationStyle.kGameMessagePositive));
                object o = this;
                ((BuffBetrayed.BuffInstanceBetrayed)o).BreakUpWithBetrayer(naughtySimDesc, true);
            }
            public void ShowOrig(Sim victimSim, SimDescription naughtySimDesc) { } // receives BuffInstanceBetrayed.ShowTNSAndBreakUp
        }

        // ---- B8b §4 topictestnull (tuning kTopicTestNull) ----------------------------------------------------------------
        // [verified code; reach inferred] ActiveTopicTestFunctions run through ActiveTopic.PassesTestFunction
        // (MethodInfo.Invoke) while topic lists are built: HasGettingMarriedTopic reads active.CreatedSim.Partner before its
        // null test; CanTrainSim reads GetElement(Athletic).SkillLevel for a Sim without the Athletic skill; HasJournalismCareer,
        // HasLawEnforcementCareer and IsCurrentlyScuba dereference parameter.CreatedSim. Each throws a
        // TargetInvocationException out of the social topic code. These run through MethodInfo.Invoke, which refuses a swapped
        // method in the game's Mono (ComboChecks.cs), so the methods are not swapped: at world load each topic's TestFunction
        // is re-pointed (reflection on the readonly field) to a wrapper here. With the switch off the wrapper calls EA's method
        // unchanged; on, it answers EA's question where EA would throw (no Sim: false; no Athletic skill: false; no Sim for a
        // career test: the SimDescription's occupation) and calls EA's method otherwise. Household-visible: yes (topics).
        // Counters: [3657] throws avoided, [3658] seen with the switch off, [3659] errors (also: rebinding failed).
        static readonly FieldInfo sTtData = typeof(ActiveTopicData).GetField("sData", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        static readonly FieldInfo sTtFn = typeof(ActiveTopicData).GetField("TestFunction", kInstAll);
        static readonly string[] kTtNames = { "HasGettingMarriedTopic", "CanTrainSim", "HasJournalismCareer", "HasLawEnforcementCareer", "IsCurrentlyScuba" };

        // Pure: would EA throw? kind: 0 married (active Sim), 1 train (Athletic skill), 2-4 career/scuba (parameter Sim).
        internal static bool TtThrows(int kind, bool simLive, bool hasAthletic)
        {
            return kind == 1 ? !hasAthletic : !simLive;
        }

        // Review 2026-11-15: static references to Sims / events / objects held by batch 5-8b hooks are dropped at world
        // load (a world switch would otherwise keep the old world's objects alive and match them).
        static void B582StaticsWorldLoad()
        {
            sIbLast = null;
            sLbBySim.Clear(); sLbSeen.Clear();
            sDsEmptyRun.Clear();
        }

        internal static void TopicTestNullWorldLoad()
        {
            try { B582StaticsWorldLoad(); } catch (Exception) { }
            try
            {
                IDictionary<string, ActiveTopicData> data = sTtData == null ? null : sTtData.GetValue(null) as IDictionary<string, ActiveTopicData>;
                if (data == null || sTtFn == null) { PatchCounters()[3659]++; return; }
                foreach (ActiveTopicData d in data.Values)
                {
                    MethodInfo m = d == null ? null : sTtFn.GetValue(d) as MethodInfo;
                    if (m == null || m.DeclaringType != typeof(ActiveTopicTestFunctions) || Array.IndexOf(kTtNames, m.Name) < 0) continue;
                    MethodInfo w = typeof(TopicTests).GetMethod(m.Name, BindingFlags.Static | BindingFlags.Public);
                    if (w != null) sTtFn.SetValue(d, w);
                }
            }
            catch (Exception) { PatchCounters()[3659]++; }
        }

        static bool TtGuard(int kind, bool simLive, bool hasAthletic)
        {
            if (!TtThrows(kind, simLive, hasAthletic)) return false;
            if (ShortcutOn("topictestnull")) { PatchCounters()[3657]++; return true; }
            PatchCounters()[3658]++;
            return false;
        }

        // Wrappers the topic data points at (same signature as EA's test functions).
        public static class TopicTests
        {
            public static bool HasGettingMarriedTopic(SimDescription parameter, SimDescription active)
            {
                if (TtGuard(0, active != null && active.CreatedSim != null, true)) return false;
                return ActiveTopicTestFunctions.HasGettingMarriedTopic(parameter, active);
            }
            public static bool CanTrainSim(SimDescription parameter, SimDescription active)
            {
                if (TtGuard(1, true, active != null && active.SkillManager != null && active.SkillManager.GetElement(SkillNames.Athletic) != null)) return false;
                return ActiveTopicTestFunctions.CanTrainSim(parameter, active);
            }
            public static bool HasJournalismCareer(SimDescription parameter, SimDescription active)
            {
                if (TtGuard(2, parameter != null && parameter.CreatedSim != null && parameter.CreatedSim.CareerManager != null, true)) return parameter != null && parameter.Occupation is Journalism;
                return ActiveTopicTestFunctions.HasJournalismCareer(parameter, active);
            }
            public static bool HasLawEnforcementCareer(SimDescription parameter, SimDescription active)
            {
                if (TtGuard(3, parameter != null && parameter.CreatedSim != null && parameter.CreatedSim.CareerManager != null, true)) return parameter != null && parameter.Occupation is LawEnforcement;
                return ActiveTopicTestFunctions.HasLawEnforcementCareer(parameter, active);
            }
            public static bool IsCurrentlyScuba(SimDescription parameter, SimDescription active)
            {
                if (TtGuard(4, parameter != null && parameter.CreatedSim != null, true)) return false;
                return ActiveTopicTestFunctions.IsCurrentlyScuba(parameter, active);
            }
        }

        // ---- B8b §5 opptrackerrelisten (tuning kOppTrackerRelisten) ------------------------------------------------------
        // [verified double add; effect inferred] OpportunityTrackerModel.TriggerInteraction (the player clicks an
        // opportunity): for an opportunity with a target SimDescription and no target object yet, once that Sim is
        // instantiated, it sets the target and calls EventTracker.AddListener(completionListener) again. The listener was
        // registered at accept, its key (the actor's object id) does not change, and AddListener does not dedupe: the
        // completion listener sits twice in the same list. Fix: just before EA's method, on exactly EA's path, the existing
        // registration is removed, so EA's AddListener leaves one. Household-visible: the active Sim's opportunity
        // completion is processed once.
        // Counters: [3690] duplicate registrations avoided, [3691] seen with the switch off, [3692] errors.
        // Pure: EA's re-add path with the listener already registered?
        internal static bool OtDuplicate(bool canQueue, bool hasTargetObject, bool targetDesc, bool listener, bool targetLive, bool registered)
        {
            return canQueue && !hasTargetObject && targetDesc && listener && targetLive && registered;
        }

        static void ApplyOppTrackerRelisten()
        {
            Patch(typeof(OpportunityTrackerModel), "TriggerInteraction", new Type[] { typeof(int) }, typeof(BOtHook), "TriggerHook", "TriggerOrig");
        }

        public class BOtHook
        {
            public PushInteractionErrorCode TriggerHook(int n)
            {
                try
                {
                    Sim a = Sim.ActiveActor;
                    Opportunity op = a != null && a.OpportunityManager != null ? a.OpportunityManager.GetElement((ulong)n) : null;
                    if (op != null)
                    {
                        EventListener l = op.CompletionListener;
                        SimDescription t = op.TargetSimDescription;
                        if (OtDuplicate(a.InteractionQueue.CanPlayerQueue(), op.TargetObject != null, t != null, l != null, t != null && t.CreatedSim != null, l != null && EventTracker.ContainsListener(l)))
                        {
                            if (ShortcutOn("opptrackerrelisten")) { EventTracker.RemoveListener(l); PatchCounters()[3690]++; }
                            else PatchCounters()[3691]++;
                        }
                    }
                }
                catch (Exception) { PatchCounters()[3692]++; }
                return TriggerOrig(n);
            }
            public PushInteractionErrorCode TriggerOrig(int n) { return PushInteractionErrorCode.NoError; } // receives OpportunityTrackerModel.TriggerInteraction
        }
    }
}
