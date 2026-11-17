using System;
using System.Reflection;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.EventSystem;
using Sims3.Gameplay.Objects.CookingObjects;
using Sims3.Gameplay.Objects.Environment;
using Sims3.Gameplay.Situations;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // Scan triage 1117 pass 2 real hits (docs/615). Two of the five pass-2 hits were false in the IL check
    // (SnakeCharmingBasket: Charm.Cleanup re-adds the basket at opacity 1; RideBroomArena: BroomStand.ParkBroom fades the
    // broom in and Inventory.TryToAdd sets opacity 1), see docs/615 §4. All default off.
    public static partial class Fixes
    {
        // ---- docs/615 §1 spoilbroadcast ------------------------------------------------------------------------------------
        // [verified, ilspycmd PreparedFood::ForceSpoil IL_00fb/IL_0100] `newobj ReactionBroadcaster` / `stfld
        // mDisgustedBroadcaster` with no load or Dispose of the old field first; the method re-arms itself (IL_0066 `ldftn
        // ForceSpoil` -> AddAlarm -> mSpoilageAlarm) at every spoilage stage. Only Dispose and Unspoil dispose the field, so
        // each earlier stage's broadcaster stays in the lot's reaction list (ticked by Lot.UpdateReactions, still pushing
        // Disgusted) until the food is destroyed. Its sibling in the same method, ServingContainer.CreateDirtyReactionBroadcaster,
        // guards both creations with `if (mDirtyBroadcaster == null)` [verified, IL_0006/IL_002b brtrue]. Fix as EA does there
        // and in Dispose/Unspoil: after EA's body, dispose the broadcaster it replaced. All households (food object, no Sim scope).
        // Counters: [3476] broadcasters disposed, [3477] leaked with the switch off, [3478] errors.
        static readonly FieldInfo sSpbField = typeof(PreparedFood).GetField("mDisgustedBroadcaster", kInstAll);
        static string sSpbMissing = "";

        static void ApplySpoilBroadcast()
        {
            sSpbMissing = sSpbField == null ? "PreparedFood.mDisgustedBroadcaster" : "";
            if (sSpbMissing.Length > 0) { AppliedLog().Add("spoilbroadcast: not found: " + sSpbMissing); return; }
            Patch(typeof(PreparedFood), "ForceSpoil", Type.EmptyTypes, typeof(SpbSpoilHook), "SpoilHook", "SpoilOrig");
        }

        // Pure: dispose the old broadcaster after EA's body? (EA: never)
        internal static bool SpbDispose(bool on, bool hadOld, bool replaced)
        {
            return on && hadOld && replaced;
        }

        public class SpbSpoilHook
        {
            public void SpoilHook()
            {
                object self = this;
                ReactionBroadcaster old = null;
                try { if (sSpbMissing.Length == 0) old = sSpbField.GetValue(self) as ReactionBroadcaster; }
                catch (ResetException) { throw; }
                catch (Exception) { PatchCounters()[3478]++; }
                SpoilOrig();
                if (old == null) return;
                try
                {
                    bool replaced = !ReferenceEquals(sSpbField.GetValue(self), old);
                    bool on = ShortcutOn("spoilbroadcast");
                    if (SpbDispose(on, true, replaced)) { old.Dispose(); PatchCounters()[3476]++; }
                    else if (!on && replaced) PatchCounters()[3477]++;
                }
                catch (ResetException) { throw; }
                catch (Exception) { PatchCounters()[3478]++; }
            }
            public void SpoilOrig() { } // receives PreparedFood.ForceSpoil
        }

        public static object SpoilBroadcastCheck(string what)
        {
            string counters = B582Counters("spoilbroadcast", 3476, new[] { "broadcasters disposed", "leaked, switch off", "errors" }, sSpbMissing);
            if (what == "result") return counters;
            if (what != "run") return "spoilbroadcast <run|result>";
            bool on = ShortcutOn("spoilbroadcast");
            bool eaBug = !SpbDispose(false, true, true);
            bool fixedPure = on && SpbDispose(on, true, true);
            bool controls = eaBug && !SpbDispose(true, false, true) && !SpbDispose(true, true, false);
            return B582Line("spoilbroadcast", on, "a spoil stage disposes the broadcaster it replaces", fixedPure && sSpbMissing.Length == 0,
                "EA leaks one per stage (" + eaBug + "), first stage / not replaced kept", controls, sSpbMissing, counters);
        }

        // ---- docs/615 §2 weatherstonerelisten ------------------------------------------------------------------------------
        // [verified, ilspycmd WeatherStone::AnimateChangeWeatherStoneState IL_017d/IL_0182] the AfterSuccessfulSummon case
        // stores `EventTracker.AddListener(kWeatherStarted, OnWeatherStarted)` into mWeatherChangedEventListener without
        // removing a listener still held there. Path [verified, decompiled DoReset / OnMidnightReset]: a reset while the stone
        // waits for its weather (AfterSuccessfulSummon) maps the state to Resting with the listener still registered, midnight
        // makes it ReadyForSummoning, and the next successful summon adds a second one: both OnWeatherStarted run, two
        // kWeatherStopped listeners, OnWeatherStopped twice. Not covered by weatherstonelistener (docs/594 §2: Dispose only).
        // Fix as EA does in its own re-registration sites (remove the held listener first): before EA's body for
        // AfterSuccessfulSummon, RemoveListener the held one and null the field. Player summons, all households: EA intends
        // one listener per summon.
        // Counters: [3479] stale listeners removed, [3480] stacked with the switch off, [3481] errors.
        static string sWsrMissing = "";

        static void ApplyWeatherStoneRelisten()
        {
            sWsrMissing = sWslListener == null ? "WeatherStone.mWeatherChangedEventListener" : "";
            if (sWsrMissing.Length > 0) { AppliedLog().Add("weatherstonerelisten: not found: " + sWsrMissing); return; }
            Patch(typeof(WeatherStone), "AnimateChangeWeatherStoneState", new Type[] { typeof(bool), typeof(WeatherStone.WeatherStoneState) },
                typeof(WsrHook), "StateHook", "StateOrig");
        }

        internal static bool WsrRemove(bool on, bool successState, bool held)
        {
            return on && successState && held;
        }

        public class WsrHook
        {
            public void StateHook(bool yield, WeatherStone.WeatherStoneState state)
            {
                object self = this;
                try
                {
                    if (sWsrMissing.Length == 0 && state == WeatherStone.WeatherStoneState.AfterSuccessfulSummon)
                    {
                        EventListener held = sWslListener.GetValue(self) as EventListener;
                        bool on = ShortcutOn("weatherstonerelisten");
                        if (WsrRemove(on, true, held != null)) { EventTracker.RemoveListener(held); sWslListener.SetValue(self, null); PatchCounters()[3479]++; }
                        else if (!on && held != null) PatchCounters()[3480]++;
                    }
                }
                catch (ResetException) { throw; }
                catch (Exception) { PatchCounters()[3481]++; }
                StateOrig(yield, state);
            }
            public void StateOrig(bool yield, WeatherStone.WeatherStoneState state) { } // receives WeatherStone.AnimateChangeWeatherStoneState
        }

        public static object WeatherStoneRelistenCheck(string what)
        {
            string counters = B582Counters("weatherstonerelisten", 3479, new[] { "stale listeners removed", "stacked, switch off", "errors" }, sWsrMissing);
            if (what == "result") return counters;
            if (what != "run") return "weatherstonerelisten <run|result>";
            bool on = ShortcutOn("weatherstonerelisten");
            bool eaBug = !WsrRemove(false, true, true);
            bool fixedPure = on && WsrRemove(on, true, true);
            bool controls = eaBug && !WsrRemove(true, false, true) && !WsrRemove(true, true, false);
            return B582Line("weatherstonerelisten", on, "a new successful summon drops the stale weather listener", fixedPure && sWsrMissing.Length == 0,
                "EA stacks it (" + eaBug + "), other states / none held kept", controls, sWsrMissing, counters);
        }

        // ---- docs/615 §3 birthdayagerecord ---------------------------------------------------------------------------------
        // [verified, ilspycmd BirthdayParty::OnAgeTransition IL_003b..IL_0041] after `mSimsWhoAgedUp.Add(sim)` it returns
        // `ldc.i4.1` (ListenerAction.Remove): the kSimAgeTransition listener is gone after the first guest, so a second guest
        // or host aging up at the same party is never in the list. The list and its per-Sim reader DidSimAgeUpAtParty (wish
        // and challenge feedback) show EA meant every Sim. BirthdayParty has no CleanUp override that removes the listener
        // (mEventListeners is only re-added in PostLoad), so the fix keeps the listener while the party runs and removes it
        // on the first event after the party has exited (Situation.SituationExited). Household Sims get the fix: EA's own
        // per-Sim record, no skip or cut.
        // Counters: [3482] later age-ups recorded (listener kept), [3483] dropped with the switch off, [3484] errors.
        static void ApplyBirthdayAgeRecord()
        {
            Patch(typeof(BirthdayParty), "OnAgeTransition", new Type[] { typeof(Event) }, typeof(BapAgeHook), "AgeHook", "AgeOrig");
        }

        // Pure: the listener's answer. EA: eaAnswer. Fixed: Remove once the party has exited, else Keep.
        internal static ListenerAction BapAnswer(bool on, bool exited, ListenerAction eaAnswer)
        {
            if (!on) return eaAnswer;
            return exited ? ListenerAction.Remove : ListenerAction.Keep;
        }

        public class BapAgeHook
        {
            public ListenerAction AgeHook(Event e)
            {
                BirthdayParty self = (object)this as BirthdayParty;
                bool on = false, exited = false;
                try { on = ShortcutOn("birthdayagerecord"); exited = self != null && self.SituationExited; }
                catch (ResetException) { throw; }
                catch (Exception) { PatchCounters()[3484]++; on = false; }
                if (on && exited) return ListenerAction.Remove;
                ListenerAction ea = AgeOrig(e);
                ListenerAction r = BapAnswer(on, exited, ea);
                if (ea == ListenerAction.Remove) PatchCounters()[on ? 3482 : 3483]++;
                return r;
            }
            public ListenerAction AgeOrig(Event e) { return ListenerAction.Keep; } // receives BirthdayParty.OnAgeTransition
        }

        public static object BirthdayAgeRecordCheck(string what)
        {
            string counters = B582Counters("birthdayagerecord", 3482, new[] { "age-ups recorded, listener kept", "listener dropped, switch off", "errors" }, "");
            if (what == "result") return counters;
            if (what != "run") return "birthdayagerecord <run|result>";
            bool on = ShortcutOn("birthdayagerecord");
            // first guest ages up: EA drops the listener, so the second is never recorded
            bool eaBug = BapAnswer(false, false, ListenerAction.Remove) == ListenerAction.Remove;
            bool fixedPure = on && BapAnswer(on, false, ListenerAction.Remove) == ListenerAction.Keep;
            bool controls = eaBug && BapAnswer(true, false, ListenerAction.Keep) == ListenerAction.Keep
                && BapAnswer(true, true, ListenerAction.Keep) == ListenerAction.Remove
                && BapAnswer(false, true, ListenerAction.Keep) == ListenerAction.Keep;
            return B582Line("birthdayagerecord", on, "the listener stays for every guest who ages up", fixedPure,
                "EA drops it after the first (" + eaBug + "), non-guest keeps, ended party releases it", controls, "", counters);
        }
    
        static readonly int sLive_birthdayagerecord = LiveAdd("birthdayagerecord", () => EpLive(typeof(BapAgeHook), 1));
        static readonly int sLive_spoilbroadcast = LiveAdd("spoilbroadcast", () => EpLive(typeof(SpbSpoilHook), 1, "sSpbField", sSpbField, null));
    }
}
