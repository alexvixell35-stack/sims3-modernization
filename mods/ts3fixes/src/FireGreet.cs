using System;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActiveCareer.ActiveCareers;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // firegreet (tuning kFireGreet, off by default; docs/681). EA bug fix in the Ambitions firefighter career: the emergency's
    // lot locks the firefighter out ("visit household" was needed to get in).
    //
    // [verified, decompiled] A household's greeting is one list of lots (Household.mGreetedLots, saved). EA greets for an
    // emergency only in three places: the Respond to Emergency interaction, FireFightingJob setup for large fires and
    // earthquakes (Sim.ActiveActor, the selected Sim), and FindIsolatedFirePosition (small fires). And Sim's
    // UpdateVisibleLotsForActiveHousehold / UngreetLotIfNoOneIsInside drop the greeting as soon as no Sim of the household
    // is left inside the lot. So the firefighter is locked out of the burning house when they:
    //   - arrive another way than Respond to Emergency (walk or drive there, a save loaded mid-emergency), or
    //   - step out once (carry a crying child out of the front door, go around to a hose or a back door) while no other
    //     family Sim is inside: the greeting is gone and the doors stay locked for the rest of the emergency.
    // Any residential lot, not apartments only (apartments only add a HUD refresh).
    // Fix: once a minute while an emergency is set up (ActiveFireFighter.sActiveJob, HasBeenSetup), the firefighter who
    // holds it is greeted on its lot when standing on it or within kFgNearM of its edge and not greeted. EA's own call
    // (Sim.GreetSimOnLot), the same one Respond to Emergency makes. Nothing outside an emergency is touched.
    // Counters: [1494] greetings given, [1495] errors. Check: FireGreetCheck.
    public static partial class Fixes
    {
        const float kFgNearM = 8f;
        static readonly PropertyInfo sFgJobLot = typeof(Sims3.Gameplay.Careers.Job).GetProperty("Lot", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        static AlarmHandle sFgAlarm = AlarmHandle.kInvalidHandle;

        // ---- decision (also used by the check): greet this firefighter on the emergency lot now? ----
        internal static bool FgGreet(bool on, bool jobSetUp, bool simInWorld, bool greeted, bool onOrNear, bool isHomeLot)
        {
            return on && jobSetUp && simInWorld && !greeted && onOrNear && !isHomeLot;
        }

        // World load finished (Instantiator): the minute alarm (never saved; the switch is read on every tick).
        public static void FireGreetWorldLoad()
        {
            try
            {
                if (AlarmManager.Global == null) return;
                if (sFgAlarm != AlarmHandle.kInvalidHandle) AlarmManager.Global.RemoveAlarm(sFgAlarm);
                sFgAlarm = AlarmManager.Global.AddAlarmRepeating(1f, TimeUnit.Minutes, delegate { FireGreetTick(); }, 1f, TimeUnit.Minutes, "Ts3Fixes.FireGreet", AlarmType.NeverPersisted, null);
            }
            catch (Exception) { PatchCounters()[1495]++; }
        }

        // The emergency's lot and its firefighter, or false.
        static bool FgJob(out FireFightingJob job, out Lot lot, out Sim sim)
        {
            job = ActiveFireFighter.sActiveJob;
            lot = null; sim = null;
            if (job == null || sFgJobLot == null) return false;
            lot = sFgJobLot.GetValue(job, null) as Lot;
            sim = job.Occupation == null || job.Occupation.OwnerDescription == null ? null : job.Occupation.OwnerDescription.CreatedSim;
            return lot != null && !lot.IsWorldLot && sim != null;
        }

        static bool FgOnOrNear(Sim sim, Lot lot)
        {
            return sim.LotCurrent == lot || lot.GetDistanceToLotBoundary(sim.Position) <= kFgNearM;
        }

        static void FireGreetTick()
        {
            try
            {
                if (!ShortcutOn("firegreet")) return;
                FireFightingJob job; Lot lot; Sim sim;
                if (!FgJob(out job, out lot, out sim)) return;
                bool inWorld = sim.InWorld && !sim.HasBeenDestroyed && sim.Household != null;
                if (!FgGreet(true, job.HasBeenSetup, inWorld, inWorld && sim.IsGreetedOnLot(lot), inWorld && FgOnOrNear(sim, lot), lot == sim.LotHome)) return;
                sim.GreetSimOnLot(lot);
                PatchCounters()[1494]++;
            }
            catch (Exception) { PatchCounters()[1495]++; }
        }

        // FireGreetCheck run: the decision for the reported case (firefighter on or next to the emergency lot, greeting
        // dropped) with controls; then the live emergency, if any. Read-only.
        //   off BROKEN = not greeted; on FIXED = greeted. Controls (must be false in both): no emergency set up, already
        //   greeted, far from the lot, the firefighter's own home lot.
        //   result  counters 1494-1495
        public static object FireGreetCheck(string what)
        {
            int[] c = PatchCounters();
            string counters = "firegreet: greeted 1494=" + c[1494] + ", errors 1495=" + c[1495] + "; members found=" + (sFgJobLot != null) + ", alarm=" + (sFgAlarm != AlarmHandle.kInvalidHandle);
            if (what == "result") return counters;
            if (what != "run") return "firegreet <run|result>";
            bool on = ShortcutOn("firegreet");
            bool greet = FgGreet(on, true, true, false, true, false);
            bool control = FgGreet(on, false, true, false, true, false) || FgGreet(on, true, true, true, true, false)
                || FgGreet(on, true, true, false, false, false) || FgGreet(on, true, true, false, true, true) || FgGreet(on, true, false, false, true, false);
            string verdict = control ? "UNEXPECTED" : on ? (greet ? "FIXED" : "UNEXPECTED") : (greet ? "UNEXPECTED" : "BROKEN");
            string live = "no emergency";
            FireFightingJob job; Lot lot; Sim sim;
            if (FgJob(out job, out lot, out sim))
                live = "emergency on " + lot.Name + " (set up=" + job.HasBeenSetup + "), firefighter " + sim.FullName + " greeted=" + sim.IsGreetedOnLot(lot)
                    + ", on lot=" + (sim.LotCurrent == lot) + ", edge distance=" + Math.Round(lot.GetDistanceToLotBoundary(sim.Position), 1);
            return "firegreet check (switch " + (on ? "on" : "off") + "): firefighter next to the emergency lot, greeting dropped -> greeted=" + greet
                + "; controls greeted=" + control + "; " + live + "; verdict: " + verdict + "; " + counters;
        }
    }
}
