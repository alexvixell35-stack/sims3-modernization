using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.EventSystem;
using Sims3.Gameplay.Utilities;

namespace Ts3Fixes
{
    // buffupdate (issue #166 build 20, docs/510): BuffManager.UpdateBuffs walks the Sim's live buff table and, inside the walk,
    // calls each timed buff's OnTimeOutUpdated and AutoSolveRecord.AttemptToPrioritize. When one of them adds or removes a buff
    // (BuffSuntan's sends kBronzed, and EventTracker runs every listener at once), the walk's next step throws
    // InvalidOperationException ("out of sync"). Nothing on the way up catches it: Autonomy.Update has no handler,
    // SimUpdate.Simulate catches only ResetException, and ScriptCore's proxy rethrows after an error window that the release
    // game never shows, so the Sim's update task ends.
    // Fix, switch on: an out-of-sync exception from UpdateBuffs is caught, and EA's method runs once more with no time
    // passed, so the walk reaches every buff this tick: each one's OnTimeOutUpdated (time-gated in EA's overrides) and the
    // removal of buffs that timed out. The buffs the first walk did not reach lose this tick's countdown. The Sim's update
    // goes on. Other exceptions pass. [2033] out-of-sync exceptions from UpdateBuffs (also with the switch off), [2034] caught.
    // The second walk calls AutoSolveRecord.AttemptToPrioritize again for the buffs the first walk reached; EA's record
    // attempts once only (mbAttempted) and only with auto-solve enabled, so the repeat does nothing for a record already
    // attempted [verified: decompile].
    // shortcut: when the second walk also throws out of sync (a buff that changes the table on every call), the rest of
    // this tick's walk is skipped; the player's core build walks a copy instead, which a hook cannot add without
    // rewriting EA's method.
    public static partial class Fixes
    {
        public class BuffUpdateHook
        {
            public void UpdateHook(float simMinutesPassed)
            {
                try { UpdateOrig(simMinutesPassed); }
                catch (Exception e)
                {
                    if (!BuffUpdateCore.OutOfSync(e)) throw;
                    PatchCounters()[2033]++;
                    if (!ShortcutOn("buffupdate")) throw;
                    PatchCounters()[2034]++;
                    bool done = false;
                    try { UpdateOrig(0f); done = true; }
                    catch (Exception e2) { if (!BuffUpdateCore.OutOfSync(e2)) throw; }
                    if (done) sBuWalked++;
                    BuffUpdateCaught((BuffManager)(object)this, done);
                }
            }
            public void UpdateOrig(float simMinutesPassed) { } // receives BuffManager.UpdateBuffs's original header
        }
        static int sBuLines;

        static int sBuWalked; // second walks that finished (read by the agent check)

        static void BuffUpdateCaught(BuffManager bm, bool done)
        {
            if (sBuLines++ >= 5) return;
            Sim s = null;
            try { s = bm.Actor; } catch (Exception) { }
            ShortcutLog("buffupdate: " + (s != null ? s.FullName : "a Sim") + ": the buff table changed during UpdateBuffs; " + (done ? "walked again with no time passed" : "the second walk broke too, rest of this tick's walk skipped"));
        }

        // Agent checks for docs/510 (repro buff-update-check). Each arm uses its own Sim of the active household, because with
        // the switch off EA's exception may end that Sim's update task.
        //   "pick":  the next Sim of the active household (teen or older, not picked before); throws when there is none.
        //   "arm":   a listener on kBronzed adds the Inspired buff to that Sim once (a mod reacting to the event); the Sim
        //            gets the Suntanned buff, acquired kDaysForBronzed + 1 days ago, its event not yet sent, its timeout
        //            running. Its next UpdateBuffs then sends kBronzed inside the walk and the table changes under it.
        //   "expect thrown" (switch off): throws unless the listener ran, an out-of-sync exception was seen and none caught.
        //   "expect caught" (switch on): throws unless the listener ran, one was seen and caught, the second walk finished,
        //            and Inspired is present.
        //   "expect alive" (switch on): throws unless the Sim's motives moved since the expect step (its update goes on).
        //   "state": whether the Sim object is the same (2026-11-01: after EA's exception the picked Sim object was gone, so a
        //            reset made a new one), whether its motives moved since the expect step, counters.
        //   "stop":  removes the listener.
        static Sim sBuSim;
        static SimDescription sBuSd;
        static readonly List<Sim> sBuUsed = new List<Sim>();
        static EventListener sBuListener;
        static int sBuFired, sBuSeen0, sBuCaught0, sBuWalked0;
        static float[] sBuMotives;
        static readonly CommodityKind[] kBuMotives = { CommodityKind.Hunger, CommodityKind.Energy, CommodityKind.Bladder, CommodityKind.Hygiene };

        public static string BuffUpdateCheck(string what)
        {
            if (what == "pick")
            {
                sBuSim = null;
                if (Household.ActiveHousehold != null)
                    foreach (Sim s in Household.ActiveHousehold.AllActors)
                        if (!s.HasBeenDestroyed && s.BuffManager != null && s.SimDescription.TeenOrAbove && !sBuUsed.Contains(s)) { sBuSim = s; break; }
                if (sBuSim == null) throw new InvalidOperationException("buffupdate pick: no Sim of the active household left (teen or older, not picked before)");
                sBuUsed.Add(sBuSim);
                sBuSd = sBuSim.SimDescription; sBuMotives = null;
                return sBuSim.FullName + "; switch " + ShortcutOn("buffupdate");
            }
            if (what == "stop")
            {
                if (sBuListener != null) EventTracker.RemoveListener(sBuListener);
                sBuListener = null; sBuSim = null;
                return "stopped";
            }
            if (sBuSim == null) throw new InvalidOperationException("buffupdate: pick first");
            if (what == "arm")
            {
                BuffManager bm = sBuSim.BuffManager;
                if (bm == null || sBuSim.HasBeenDestroyed) throw new InvalidOperationException("buffupdate arm: " + sBuSim.FullName + " has no buff manager");
                if (sBuListener == null) sBuListener = EventTracker.AddListener(EventTypeId.kBronzed, OnBuBronzed);
                sBuFired = 0;
                if (bm.HasElement(BuffNames.Inspired)) bm.RemoveElement(BuffNames.Inspired);
                if (!bm.HasElement(BuffNames.Suntanned) && !bm.AddElement(BuffNames.Suntanned, Origin.None))
                    throw new InvalidOperationException("buffupdate arm: " + sBuSim.FullName + " did not take the Suntanned buff");
                BuffSuntan.BuffInstanceSuntan tan = bm.GetElement(BuffNames.Suntanned) as BuffSuntan.BuffInstanceSuntan;
                if (tan == null) throw new InvalidOperationException("buffupdate arm: the Suntanned buff is not a BuffInstanceSuntan");
                FieldInfo days = typeof(BuffSuntan).GetField("kDaysForBronzed", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                int d = days != null ? (int)days.GetValue(null) : 3;
                tan.TimeAcquired = SimClock.Subtract(SimClock.CurrentTime(), TimeUnit.Days, d + 1);
                tan.BronzedEventSent = false;
                tan.mTimeoutPaused = false;
                if (tan.mTimeoutCount == -1f || tan.mTimeoutCount < 60f) tan.mTimeoutCount = 600f;
                sBuSeen0 = PatchCounters()[2033]; sBuCaught0 = PatchCounters()[2034]; sBuWalked0 = sBuWalked;
                return sBuSim.FullName + ": Suntanned acquired " + (d + 1) + " days ago, its event not sent; listener on; switch " + ShortcutOn("buffupdate");
            }
            int seen = PatchCounters()[2033] - sBuSeen0, caught = PatchCounters()[2034] - sBuCaught0, walked = sBuWalked - sBuWalked0;
            bool on = ShortcutOn("buffupdate");
            Sim cur = sBuSd != null ? sBuSd.CreatedSim : null;
            bool same = cur != null && cur == sBuSim && !sBuSim.HasBeenDestroyed;
            bool inspired = cur != null && cur.BuffManager != null && cur.BuffManager.HasElement(BuffNames.Inspired);
            string st = "listener ran " + sBuFired + ", seen +" + seen + ", caught +" + caught + ", walked again +" + walked + ", Sim object "
                + (same ? "same" : cur != null ? "replaced (the picked one destroyed " + sBuSim.HasBeenDestroyed + ")" : "none") + ", Inspired " + inspired;
            if (what == "expect thrown" || what == "expect caught")
            {
                bool ok = what == "expect thrown" ? !on && sBuFired == 1 && seen >= 1 && caught == 0
                    : on && sBuFired == 1 && seen >= 1 && caught == seen && walked == caught && same && inspired;
                if (!ok) throw new InvalidOperationException("buffupdate " + what + ": wrong outcome (switch " + on + ", " + sBuSd.FullName + "); " + st);
                sBuMotives = cur != null && cur.Motives != null ? BuMotives(cur) : null;
                return "as expected; " + sBuSd.FullName + ": " + st;
            }
            if (what == "state" || what == "expect alive")
            {
                bool moved = cur != null && cur.Motives != null && sBuMotives != null && BuMoved(cur);
                string s2 = sBuSd.FullName + ": motives " + (moved ? "moved" : "did not move") + " since the expect step; " + st;
                if (what == "expect alive" && !(on && same && moved)) throw new InvalidOperationException("buffupdate expect alive: wrong outcome (switch " + on + "); " + s2);
                return (what == "expect alive" ? "as expected; " : "") + s2;
            }
            return "unknown check " + what + " (pick, arm, expect thrown, expect caught, expect alive, state, stop)";
        }

        static ListenerAction OnBuBronzed(Event e)
        {
            try
            {
                Sim s = e.Actor as Sim;
                if (s != null && s == sBuSim && sBuFired++ == 0) s.BuffManager.AddElement(BuffNames.Inspired, Origin.None);
            }
            catch (Exception) { }
            return ListenerAction.Keep;
        }

        static float[] BuMotives(Sim s)
        {
            float[] v = new float[kBuMotives.Length];
            for (int i = 0; i < v.Length; i++) v[i] = s.Motives.GetValue(kBuMotives[i]);
            return v;
        }

        static bool BuMoved(Sim s)
        {
            if (sBuMotives == null) throw new InvalidOperationException("buffupdate: run an expect step first");
            float[] v = BuMotives(s);
            for (int i = 0; i < v.Length; i++) if (v[i] != sBuMotives[i]) return true;
            return false;
        }
    }
}
