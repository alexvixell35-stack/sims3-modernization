using System;
using System.Collections;
using System.Reflection;
using Sims3.Gameplay;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.SimIFace;
using Sims3.SimIFace.CAS;

namespace Ts3Fixes
{
    // ageupdupinst (local 2026-11-18, default off; issue #553).
    // [verified, decompiled] SimDescription.Instantiate(Vector3, ResourceKey, bool, bool) returns CreatedSim when the Sim
    // exists, and only then calls AgingState.PreInstantiateSim for a deferred age-up. PreInstantiateSim starts (or joins)
    // the age-up's SimBuilderTask and sleeps until it is Done. Two instantiations of the same Sim that both pass the
    // CreatedSim test before the build ends (a mod's InstantiateOffScreen plus another caller, #553) wait on the same task.
    // The first to wake clears SimBuilderTaskDeferred (PreInstantiateSim is its only writer of false), ages the Sim, and
    // AgeTransitionCompleted nulls SimBuilderTask; it then creates the Sim. The second reads SimBuilderTask.Done on null:
    // NullReferenceException, EA's catch rethrows it, and so does ageupabort (deferred already cleared, docs/512 §5). The
    // caller's interaction ends in ScriptProxy's error path. EA's exception also keeps the second call from making a second
    // Sim object for the same SimDescription.
    // Fix: when that exact case comes out of Instantiate (AgeUpAbort.cs marks it: NullReferenceException, task null,
    // deferred cleared), the Sim the first call created is returned: what Instantiate returns to any call that comes a
    // moment later (its first line). The first call may still be finishing (CreatedSim not set yet): the second waits up to
    // 100 ticks in its own task (PreInstantiateSim already slept there). No Sim after that: EA's exception is rethrown.
    // Any other exception goes on unchanged. An EA bug fix: applies to every Sim.
    // Counters [3988] duplicate instantiations answered with the other call's Sim, [3989] the case with the switch off
    // (EA throws), [3990] no Sim created by the other call (EA's exception rethrown).
    public static partial class Fixes
    {
        internal static SimDescription sAuDupDesc;  // set by AgeUpAbortHook right before it rethrows the deferred-cleared case
        static int sAdLines;

        public class AgeUpDupInstHook
        {
            public Sim InstHook(Vector3 position, ResourceKey outfitKey, bool addInitialObjects, bool forceAlwaysAnimate)
            {
                try { return InstOrig(position, outfitKey, addInitialObjects, forceAlwaysAnimate); }
                catch (NullReferenceException)
                {
                    SimDescription sd = (SimDescription)(object)this;
                    if (sAuDupDesc != sd) throw;
                    sAuDupDesc = null;
                    if (!ShortcutOn("ageupdupinst")) { PatchCounters()[3989]++; throw; }
                    for (int i = 0; i < 100 && sd.CreatedSim == null; i++) Simulator.Sleep(1);
                    Sim made = sd.CreatedSim;
                    if (made == null) { PatchCounters()[3990]++; throw; }
                    PatchCounters()[3988]++;
                    if (sAdLines++ < 5) ShortcutLog("ageupdupinst: " + sd.FullName + ": instantiated twice during a deferred age-up; the second call got the first call's Sim");
                    return made;
                }
            }
            public Sim InstOrig(Vector3 position, ResourceKey outfitKey, bool addInitialObjects, bool forceAlwaysAnimate) { return null; } // receives SimDescription.Instantiate(Vector3, ResourceKey, bool, bool)'s original body
        }

        // Harness check (repro ageupdupinst-check; reflect_call_async Ts3Fixes.Fixes.AgeUpDupCheck "expect broken"|"expect
        // fixed", then reflect_call Ts3Fixes.Fixes.AgeUpDupResult): a townie that is not instantiated gets a deferred age-up,
        // then two tasks instantiate it off screen in the same tick (the #553 race). Off: one call returns the Sim, the other
        // throws EA's NullReferenceException. On: both return the same Sim. Either way the townie ages up once and stays
        // instantiated (test saves only).
        public static string sAdResult = "not run";
        static readonly FieldInfo sAdB35 = typeof(SimDescription).GetField("sB35PreInstantiating", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);  // refs: optional (only a Sims3Mods b35 core has it)

        public static object AgeUpDupResult()
        {
            if (!sAdResult.StartsWith("as expected")) throw new InvalidOperationException("ageupdupinst check: " + sAdResult);
            return sAdResult;
        }

        public static void AgeUpDupCheck(string what)
        {
            sAdResult = "running";
            Simulator.AddObject(new LoopTask(delegate { AgeUpDupRun(what); }));
        }

        static void AgeUpDupRun(string what)
        {
            try
            {
                SimDescription sd = AuTownie();
                Sim active = Sim.ActiveActor;
                if (sd == null || active == null || active.LotCurrent == null) { sAdResult = "no townie to age (or no active Sim on a lot)"; return; }
                AgingState st = (AgingState)sAuState.GetValue(sd);
                CASAgeGenderFlags age0 = sd.Age;
                int ok0 = PatchCounters()[3988], seen0 = PatchCounters()[3989], none0 = PatchCounters()[3990];
                Lot lot = active.LotCurrent;
                Sim r1 = null, r2 = null; string e1 = "none", e2 = "none"; bool d1 = false, d2 = false;
                sAuDeferred.SetValue(st, true);
                // A core with Sims3Mods patch b35 (SimDescription.sB35PreInstantiating) makes the second call wait for the first,
                // so the race cannot happen there. The second task drops this townie's in-progress mark once the first has set it:
                // that one call then takes EA's path, as on an unpatched core (b35 removes the mark in a finally either way).
                IDictionary b35 = sAdB35 == null ? null : sAdB35.GetValue(null) as IDictionary;
                ulong id = sd.SimDescriptionId;
                bool bypassed = false;
                Simulator.AddObject(new LoopTask(delegate { try { r1 = sd.InstantiateOffScreen(lot); } catch (Exception e) { e1 = e.GetType().Name; } d1 = true; }));
                Simulator.AddObject(new LoopTask(delegate
                {
                    if (sAdB35 != null)
                    {
                        for (int i = 0; i < 300 && (b35 = sAdB35.GetValue(null) as IDictionary) != null && !b35.Contains(id); i++) Simulator.Sleep(0);
                        if (b35 != null && b35.Contains(id)) { b35.Remove(id); bypassed = true; }
                    }
                    try { r2 = sd.InstantiateOffScreen(lot); } catch (Exception e) { e2 = e.GetType().Name; }
                    d2 = true;
                }));
                for (int i = 0; i < 3000 && !(d1 && d2); i++) Simulator.Sleep(1);
                bool on = ShortcutOn("ageupdupinst");
                int dOk = PatchCounters()[3988] - ok0, dSeen = PatchCounters()[3989] - seen0, dNone = PatchCounters()[3990] - none0;
                Sim made = sd.CreatedSim;
                bool aged = sd.Age != age0, oneThrew = (e1 == "NullReferenceException") != (e2 == "NullReferenceException");
                bool ok = d1 && d2 && made != null && aged && (sAdB35 == null || bypassed) && (what == "expect broken"
                    ? !on && oneThrew && (r1 == made || r2 == made) && dSeen == 1 && dOk == 0
                    : on && e1 == "none" && e2 == "none" && r1 == made && r2 == made && dOk == 1 && dSeen == 0 && dNone == 0);
                sAdResult = (ok ? "as expected (" : "WRONG (") + what + "); switch " + on + ", done " + d1 + "/" + d2 + ", thrown " + e1 + "/" + e2
                    + ", same Sim " + (r1 == made) + "/" + (r2 == made) + ", aged " + age0 + " -> " + sd.Age + ", answered " + dOk + ", EA throws (off) " + dSeen
                    + ", no Sim " + dNone + ", core b35 " + (sAdB35 == null ? "absent" : bypassed ? "bypassed" : "NOT bypassed")
                    + ", townie " + sd.FullName;
            }
            catch (ResetException) { sAdResult = "check task reset"; throw; }
            catch (Exception e) { sAdResult = "error " + e.GetType().Name + ": " + e.Message; }
        }
    }
}
