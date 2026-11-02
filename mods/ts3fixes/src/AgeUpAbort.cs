using System;
using System.Reflection;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.CAS;
using Sims3.SimIFace;
using Sims3.SimIFace.CAS;

namespace Ts3Fixes
{
    // ageupabort (issue #166 build 32, docs/512): for a Sim whose age-up was deferred, SimDescription.Instantiate calls
    // AgingState.PreInstantiateSim, which starts the next-age outfit build and waits for it (Simulator.Sleep(0) until the
    // builder task is Done). While it sleeps, AbortSimBuilderTask (aging turned off, or AgingManager.CancelAgingAlarmsForSim)
    // can stop the builder and null the task field. The next Done read throws NullReferenceException; EA's catch only cleans
    // up and rethrows, and Instantiate never creates the Sim (seen: a taxi driver for an NPC, Taxi.CreateDriver).
    // Fix, switch on: that exception is caught when the task field is null and the age-up is still deferred. The method
    // then ends as it does for a Sim with no deferred age-up: the outfit key is unchanged, the age is unchanged, and the
    // age-up stays deferred for a later instantiation or the regular age-up. Instantiate goes on with the current outfit.
    // Other exceptions pass. [2037] seen (also with the switch off), [2038] caught.
    public static partial class Fixes
    {
        static readonly FieldInfo sAuTask = typeof(AgingState).GetField("SimBuilderTask", kInstAll);
        static readonly FieldInfo sAuDeferred = typeof(AgingState).GetField("SimBuilderTaskDeferred", kInstAll);
        static readonly FieldInfo sAuDesc = typeof(AgingState).GetField("SimDescription", kInstAll);
        static readonly FieldInfo sAuState = typeof(SimDescription).GetField("AgingState", kInstAll);
        static readonly MethodInfo sAuAbort = typeof(AgingState).GetMethod("AbortSimBuilderTask", kInstAll, null, Type.EmptyTypes, null);

        public class AgeUpAbortHook
        {
            public void PreHook(ref ResourceKey outfitKey)
            {
                try { PreOrig(ref outfitKey); }
                catch (Exception e)
                {
                    AgingState st = (AgingState)(object)this;
                    if (sAuTask == null || sAuDeferred == null) throw; // field not found: EA's exception goes on unchanged
                    int d = AgeUpAbortCore.Decide(e, sAuTask.GetValue(st) == null, (bool)sAuDeferred.GetValue(st), ShortcutOn("ageupabort"));
                    if (d == AgeUpAbortCore.kPass) throw;
                    PatchCounters()[2037]++;
                    if (d != AgeUpAbortCore.kCaught) throw;
                    PatchCounters()[2038]++;
                    SimDescription sd = sAuDesc != null ? sAuDesc.GetValue(st) as SimDescription : null;
                    if (sAuLines++ < 5) ShortcutLog("ageupabort: " + (sd != null ? sd.FullName : "a Sim") + ": the deferred age-up's outfit build was aborted while it was instantiated; created with its current outfit, age-up still deferred");
                }
            }
            public void PreOrig(ref ResourceKey outfitKey) { } // receives AgingState.PreInstantiateSim's original header
        }
        static int sAuLines;

        // Agent checks for docs/512 (repro age-up-abort-check). The race of the report, built on a townie that is not
        // instantiated (so a lost race cannot age an instantiated Sim):
        //   "arm":  in a game task, marks the townie's age-up deferred, queues a one-shot task that calls EA's
        //           AbortSimBuilderTask, then calls PreInstantiateSim, which starts the build and sleeps; the abort runs in
        //           that sleep. Records the outcome and puts the deferred flag back. Throws when there is no such townie.
        //   "expect thrown" (switch off): throws unless EA's NullReferenceException came out, seen +1, caught +0, age same.
        //   "expect caught" (switch on): throws unless nothing came out, seen +1, caught +1, age and outfit key unchanged.
        static string sAuSim, sAuOutcome;
        static Exception sAuGot;
        static bool sAuDone, sAuAgeSame, sAuKeySame;
        static int sAuSeen0, sAuCaught0;

        public static string AgeUpAbortCheck(string what)
        {
            if (what == "arm")
            {
                SimDescription sd = AuTownie();
                if (sd == null) throw new InvalidOperationException("ageupabort arm: no human townie from child to adult that is not instantiated and has an aging state");
                AgingState st = (AgingState)sAuState.GetValue(sd);
                sAuSim = sd.FullName; sAuDone = false; sAuGot = null; sAuOutcome = "not run";
                sAuSeen0 = PatchCounters()[2037]; sAuCaught0 = PatchCounters()[2038];
                Simulator.AddObject(new LoopTask(delegate
                {
                    bool was = (bool)sAuDeferred.GetValue(st);
                    CASAgeGenderFlags age = sd.Age;
                    ResourceKey key0 = sd.GetOutfit(OutfitCategories.Everyday, 0) != null ? sd.GetOutfit(OutfitCategories.Everyday, 0).Key : ResourceKey.kInvalidResourceKey;
                    ResourceKey key = key0;
                    try
                    {
                        sAuDeferred.SetValue(st, true);
                        Simulator.AddObject(new LoopTask(delegate { sAuAbort.Invoke(st, null); }));
                        st.PreInstantiateSim(ref key);
                        sAuOutcome = "returned";
                    }
                    catch (Exception e) { sAuGot = e; sAuOutcome = "threw " + e.GetType().Name; }
                    finally
                    {
                        sAuDeferred.SetValue(st, was);
                        sAuAgeSame = sd.Age == age; sAuKeySame = key == key0;
                        sAuDone = true;
                    }
                }));
                return sAuSim + " (" + sd.Age + "): armed; switch " + ShortcutOn("ageupabort");
            }
            if (what != "expect thrown" && what != "expect caught")
                return "unknown check " + what + " (arm, expect thrown, expect caught)";
            if (!sAuDone) throw new InvalidOperationException("ageupabort " + what + ": the armed call has not finished (" + sAuSim + ")");
            int seen = PatchCounters()[2037] - sAuSeen0, caught = PatchCounters()[2038] - sAuCaught0;
            bool on = ShortcutOn("ageupabort");
            bool ok = what == "expect thrown" ? !on && sAuGot is NullReferenceException && seen == 1 && caught == 0 && sAuAgeSame
                : on && sAuGot == null && seen == 1 && caught == 1 && sAuAgeSame && sAuKeySame;
            string r = sAuSim + ": " + sAuOutcome + ", seen +" + seen + ", caught +" + caught + ", age same " + sAuAgeSame + ", outfit key same " + sAuKeySame;
            if (!ok) throw new InvalidOperationException("ageupabort " + what + ": wrong outcome (switch " + on + "); " + r);
            return "as expected; " + r;
        }

        static SimDescription AuTownie()
        {
            if (Household.sHouseholdList == null) return null;
            foreach (Household h in Household.sHouseholdList)
            {
                if (h == null || h == Household.ActiveHousehold) continue;
                foreach (SimDescription sd in h.SimDescriptions)
                {
                    if (sd == null || sd.CreatedSim != null || !sd.IsHuman || sd.GetOutfitCount(OutfitCategories.Everyday) == 0) continue;
                    if (sd.Age != CASAgeGenderFlags.Child && sd.Age != CASAgeGenderFlags.Teen && sd.Age != CASAgeGenderFlags.YoungAdult && sd.Age != CASAgeGenderFlags.Adult) continue;
                    AgingState st = sAuState.GetValue(sd) as AgingState;
                    if (st == null || sAuTask.GetValue(st) != null) continue;
                    return sd;
                }
            }
            return null;
        }
    }
}
