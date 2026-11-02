using System;
using System.Reflection;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;

namespace Ts3Fixes
{
    // jazzparam (issue #166 build 21, docs/511): Sim.UpdateJazzParameter sets an animation parameter on the current
    // interaction's state machine (null-checked) and then on the posture's (Posture.CurrentStateMachine, with no check of
    // Posture). A Sim with no posture at that moment throws NullReferenceException after the interaction half ran. Callers:
    // BuffEnjoyingMusic.OnAddition and OnRemoval (so the exception leaves BuffManager.AddElement or RemoveElement, seen from
    // MusicalInstrument.StartBroadcast inside another Sim's skill level-up) and Sim.PlayFacialIdle.
    // Fix, switch on: a NullReferenceException from the method while the Sim has no posture is caught: the interaction half
    // already ran and there is no posture to set the parameter on, which is what the player's core build does with a check.
    // Other exceptions, and any exception while the Sim has a posture, pass. [2035] seen (also with the switch off),
    // [2036] caught.
    public static partial class Fixes
    {
        static readonly FieldInfo sJzPosture = typeof(Sim).GetField("mPosture", kInstAll);

        public class JazzParamHook
        {
            public void UpdateHook(string parameterName, bool value)
            {
                try { UpdateOrig(parameterName, value); }
                catch (Exception e)
                {
                    int d = JazzParamCore.Decide(e, ((Sim)(object)this).Posture == null, ShortcutOn("jazzparam"));
                    if (d == JazzParamCore.kPass) throw;
                    PatchCounters()[2035]++;
                    if (d != JazzParamCore.kCaught) throw;
                    PatchCounters()[2036]++;
                    if (sJzLines++ < 5) ShortcutLog("jazzparam: " + ((Sim)(object)this).FullName + ": no posture for jazz parameter " + parameterName + ", skipped");
                }
            }
            public void UpdateOrig(string parameterName, bool value) { } // receives Sim.UpdateJazzParameter's original header
        }
        static int sJzLines;

        // Agent checks for docs/511 (repro jazz-param-check), on a Sim of the active household, each in one call:
        //   "expect thrown" (switch off): takes the Sim's posture away, calls UpdateJazzParameter, puts the posture back.
        //            Throws unless EA's NullReferenceException came out, seen +1, caught +0.
        //   "expect caught" (switch on): the same; throws unless nothing came out, seen +1, caught +1.
        //   "expect control" (switch on): the call with the posture in place; throws unless nothing came out and nothing
        //            was counted.
        public static string JazzParamCheck(string what)
        {
            if (what != "expect thrown" && what != "expect caught" && what != "expect control")
                return "unknown check " + what + " (expect thrown, expect caught, expect control)";
            Sim sim = null;
            if (Household.ActiveHousehold != null)
                foreach (Sim s in Household.ActiveHousehold.AllActors)
                    if (!s.HasBeenDestroyed && s.Posture != null) { sim = s; break; }
            if (sim == null) throw new InvalidOperationException("jazzparam " + what + ": no Sim of the active household with a posture");
            bool control = what == "expect control";
            int seen0 = PatchCounters()[2035], caught0 = PatchCounters()[2036];
            object posture = sJzPosture.GetValue(sim);
            Exception got = null;
            try
            {
                if (!control) sJzPosture.SetValue(sim, null);
                sim.UpdateJazzParameter("ts3fixesJazzCheck", false); // a name no state machine uses
            }
            catch (Exception e) { got = e; }
            finally { sJzPosture.SetValue(sim, posture); }
            int seen = PatchCounters()[2035] - seen0, caught = PatchCounters()[2036] - caught0;
            bool on = ShortcutOn("jazzparam");
            bool ok = what == "expect thrown" ? !on && got is NullReferenceException && seen == 1 && caught == 0
                : what == "expect caught" ? on && got == null && seen == 1 && caught == 1
                : on && got == null && seen == 0 && caught == 0;
            string st = sim.FullName + ": " + (got != null ? got.GetType().Name : "no exception") + ", seen +" + seen + ", caught +" + caught
                + ", posture back " + (sim.Posture != null);
            if (!ok) throw new InvalidOperationException("jazzparam " + what + ": wrong outcome (switch " + on + "); " + st);
            return "as expected; " + st;
        }
    }
}
