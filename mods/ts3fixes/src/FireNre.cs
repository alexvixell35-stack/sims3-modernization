using System;
using System.Reflection;
using Sims3.Gameplay.ActiveCareer.ActiveCareers;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Careers;
using Sims3.Gameplay.Controllers;
using Sims3.Gameplay.Situations;

namespace Ts3Fixes
{
    // firenre (tuning kFireNre, off by default; docs/867). EA bug fix: two NullReferenceExceptions in the firefighter's
    // emergencies (docs/570 §2 rows 4 and 6). Household firefighters are included: an EA crash fix that only skips the null
    // call (AGENTS.md household rule).
    //
    // (4) [verified, IL] FirefighterEmergencySituation.FirefighterAddedToLot ends its Sim's own job timer with
    // Occupation?.CurrentJob.RestartTimer; ActiveFireFighter.CurrentJob is null for a visibly pregnant owner, so the call throws.
    // OnSimLotChanged keeps pregnant firefighters out, the constructor (every selectable active firefighter on the lot at
    // setup) does not. The NRE aborts the situation's constructor inside FireFightingJob.SetupTasks after base.SetupTasks
    // lit the fires and trapped the Sims: the responding Sim is reset (script error in its go-to-job interaction), mSituation
    // stays null, and the half-built situation stays registered with the victims' roles (docs/867 §2).
    // Fix: FnSitHook runs EA's body; only where EA throws (the Sim has an occupation and no current job) and with the switch
    // on, it catches that NullReferenceException and runs the rest of EA's body (fire spread, window fires for LargeFires).
    // [inferred] an NRE from earlier in EA's body (the motive or the victims' interactions) would be caught the same way;
    // none is known.
    //
    // (6) [verified, decompiled] FireFightingJob.OnCompletion picks the owner when it is in the situation's Firefighters,
    // else a random one of them, shows the completion notice for that Sim, then on a 60% roll
    // (kChanceToReceiveThanksCall) calls (sim.Occupation as ActiveFireFighter).StartGratefulSimAlarm with no null test,
    // although its own loop above skips Sims that are not ActiveFireFighters. A Sim stays in Firefighters until it leaves the
    // lot, so one who quit the career (or died) there can be the pick. The NRE skips PlayGradeSting, ShutDown, CleanUpJob,
    // NumJobsCompleted++ (the earthquake chain) and the job-completed event, and EventTracker.ProcessEvent then removes the
    // owner's task listener for good (until the next load): pay and the notice came before it (docs/867 §3).
    // Fix: FnJobHook runs EA's whole body. Only when a Sim EA can pick has no fire career, it arms FnTnsHook
    // (Occupation.ShowOccupationTNS, which EA calls with its pick right before the roll): a pick with no fire career gets the
    // roll set to 0 (RandomChance(0) draws as before and is false), so EA's own code skips the null call and runs the rest;
    // the tuned chance is restored when OnCompletion returns. Not a copy of OnCompletion: a reflection call into its private
    // and protected parts fails once a mod swaps one of them (CleanUpJob is swapped by firedoorkeep; SwapInvokeDiag.cs).
    // Switch off: EA's code as is; both hooks count the cases EA throws on (row 6: the pick EA rolls for).
    // Counters: [2354] RestartTimer skipped for a firefighter with no current job, [2355] thank-you rolls set to 0 for a pick
    // with no fire career (EA throws on 60% of them), [2356] either case seen with the switch off, [2357] errors.
    // Check: FireNreCheck.
    public static partial class Fixes
    {
        static readonly FieldInfo sFnSituation = typeof(FireFightingJob).GetField("mSituation", kInstAll);
        static readonly FieldInfo sFnChance = typeof(FireFightingJob).GetField("kChanceToReceiveThanksCall", kStaticAll);
        // both internal to EA's assembly
        static readonly MethodInfo sFnSpread = typeof(FireManager).GetMethod("AllowFireToSpread", kInstAll, null, Type.EmptyTypes, null);
        static readonly MethodInfo sFnWindowSpread = typeof(FireManager).GetMethod("AllowWindowFireToSpread", kInstAll, null, Type.EmptyTypes, null);
        static readonly Type[] kFnTnsArgs = { typeof(string), typeof(Sim), typeof(bool), typeof(bool) };
        static FireFightingJob sFnJob;  // the completion running with a Sim of no fire career among EA's picks
        static int sFnChanceWas;        // EA's tuned roll, put back when that completion returns
        static bool sFnOn, sFnNoCareerPick;
        static bool sFnHooked;

        internal static string FnMissing()
        {
            string m = "";
            if (sFnSituation == null) m = "FireFightingJob.mSituation";
            if (sFnChance == null || sFnChance.FieldType != typeof(int)) m += (m.Length > 0 ? ", " : "") + "FireFightingJob.kChanceToReceiveThanksCall";
            if (typeof(Occupation).GetMethod("ShowOccupationTNS", kStaticAll, null, kFnTnsArgs, null) == null) m += (m.Length > 0 ? ", " : "") + "Occupation.ShowOccupationTNS";
            if (sFnSpread == null || sFnWindowSpread == null) m += (m.Length > 0 ? ", " : "") + "FireManager.AllowFireToSpread / AllowWindowFireToSpread";
            return m;
        }

        static void ApplyFireNre()
        {
            if (sFnHooked) return;
            sFnHooked = true;
            if (FnMissing().Length > 0) { AppliedLog().Add("firenre: members not found (" + FnMissing() + "), not patched"); return; }
            Patch(typeof(FirefighterEmergencySituation), "FirefighterAddedToLot", typeof(FnSitHook), "AddHook", "AddOrig");
            Patch(typeof(FireFightingJob), "OnCompletion", typeof(FnJobHook), "CompleteHook", "CompleteOrig");
            Patch(typeof(Occupation), "ShowOccupationTNS", kFnTnsArgs, typeof(FnTnsHook), "TnsHook", "TnsOrig");
        }

        // A Sim EA's OnCompletion can hand the thank-you call to has no fire career (FnCore.GratefulMayThrow).
        static bool FnRisky(FireFightingJob job)
        {
            FirefighterEmergencySituation sit = sFnSituation.GetValue(job) as FirefighterEmergencySituation;
            if (sit == null || sit.Firefighters == null) return false;
            Occupation occ = job.Occupation;
            Sim owner = occ == null || occ.OwnerDescription == null ? null : occ.OwnerDescription.CreatedSim;
            bool listed = owner != null && sit.Firefighters.Contains(owner);
            int without = 0;
            foreach (Sim f in sit.Firefighters) if (f == null || !(f.Occupation is ActiveFireFighter)) without++;
            return FnCore.GratefulMayThrow(sit.Firefighters.Count, listed, listed && owner.Occupation is ActiveFireFighter, without);
        }

        // `this` is the FirefighterEmergencySituation at run time (method swap).
        public class FnSitHook
        {
            public void AddHook(Sim s)
            {
                bool nre = false;
                try
                {
                    Occupation o = s.SimDescription.Occupation;
                    nre = FnCore.AddThrows(o != null, o != null && o.CurrentJob != null);
                }
                catch (Exception) { PatchCounters()[2357]++; }
                if (!nre) { AddOrig(s); return; }
                if (!ShortcutOn("firenre")) { PatchCounters()[2356]++; AddOrig(s); return; }  // EA throws
                try { AddOrig(s); return; }
                catch (NullReferenceException) { }  // EA's RestartTimer on the missing current job; the rest of EA's body follows
                PatchCounters()[2354]++;
                FirefighterEmergencySituation me = (FirefighterEmergencySituation)(object)this;
                try
                {
                    sFnSpread.Invoke(me.Lot.FireManager, null);
                    if (me.Job.Id == JobId.LargeFires) sFnWindowSpread.Invoke(me.Lot.FireManager, null);
                }
                catch (Exception) { PatchCounters()[2357]++; }  // e.g. TargetException once a mod swaps either (SwapInvokeDiag.cs)
            }
            public void AddOrig(Sim s) { }  // receives FirefighterEmergencySituation.FirefighterAddedToLot's original body
        }

        // `this` is the FireFightingJob at run time (method swap).
        public class FnJobHook
        {
            public void CompleteHook()
            {
                FireFightingJob job = (FireFightingJob)(object)this;
                bool risky = false;
                int was = 0;
                try
                {
                    risky = sFnJob == null && FnRisky(job);
                    if (risky) was = (int)sFnChance.GetValue(null);
                }
                catch (Exception) { PatchCounters()[2357]++; risky = false; }
                if (!risky) { CompleteOrig(); return; }
                // shortcut: the roll stays 0 until OnCompletion returns, also across a yield in ShutDown / CleanUpJob after the
                // roll; only another firefighter completion in that window would read it (one emergency at a time).
                sFnJob = job;
                sFnOn = ShortcutOn("firenre");
                sFnNoCareerPick = false;
                sFnChanceWas = was;
                try { CompleteOrig(); }
                finally
                {
                    sFnJob = null;
                    try { sFnChance.SetValue(null, sFnChanceWas); } catch (Exception) { PatchCounters()[2357]++; }
                    if (sFnNoCareerPick) PatchCounters()[sFnOn ? 2355 : 2356]++;
                }
            }
            public void CompleteOrig() { }  // receives FireFightingJob.OnCompletion's original body
        }

        // Occupation.ShowOccupationTNS(string, Sim, bool, bool) is static. While FnJobHook is armed, a notice for one of the
        // job's Firefighters sets the roll for that Sim; EA's completion notice names its pick last, right before the roll.
        public class FnTnsHook
        {
            public static void TnsHook(string displayText, Sim sim, bool isSchool, bool showSecondIcon)
            {
                FireFightingJob job = sFnJob;
                if (job != null && sim != null)
                {
                    try
                    {
                        FirefighterEmergencySituation sit = sFnSituation.GetValue(job) as FirefighterEmergencySituation;
                        if (sit != null && sit.Firefighters != null && sit.Firefighters.Contains(sim))
                        {
                            bool ff = sim.Occupation is ActiveFireFighter;
                            sFnNoCareerPick = !ff;
                            sFnChance.SetValue(null, FnCore.ThanksChance(sFnOn, ff, sFnChanceWas));
                        }
                    }
                    catch (Exception) { PatchCounters()[2357]++; }
                }
                TnsOrig(displayText, sim, isSchool, showSecondIcon);
            }
            public static void TnsOrig(string displayText, Sim sim, bool isSchool, bool showSecondIcon) { }  // receives EA's body
        }

        // FireNreCheck run: FnCore for the bug layouts and controls; read-only.
        //   off BROKEN = EA's path: (4) a firefighter with an occupation and no current job (visibly pregnant) reaches
        //   RestartTimer; (6) a thank-you pick with no fire career keeps EA's roll. on FIXED = (4) skipped, (6) roll 0.
        //   Controls (EA's path on and off): a firefighter with a current job, a Sim with no occupation (EA's ?. skips), a pick
        //   still in the fire career, the owner listed and in the career while another listed Sim quit (EA picks the owner),
        //   nobody listed, every listed Sim in the career.
        //   result  counters 2354-2357
        public static object FireNreCheck(string what)
        {
            int[] c = PatchCounters();
            string live = CkLive("firenre");  // "" when the three swaps are in (CheckLiveMap.cs)
            string missing = FnMissing();
            string counters = "firenre: RestartTimer skipped 2354=" + c[2354] + ", thank-you rolls set to 0 2355=" + c[2355]
                + ", seen with the switch off 2356=" + c[2356] + ", errors 2357=" + c[2357] + "; members missing: "
                + (missing.Length == 0 ? "none" : missing) + "; live path: " + (live.Length == 0 ? "ok" : live);
            if (what == "result") return counters;
            if (what != "run") return "firenre <run|result>";
            bool on = ShortcutOn("firenre");
            const int ea = 60;
            bool add = FnCore.AddThrows(true, false) && FnCore.AddSkips(on, true, false);
            bool thanks = FnCore.GratefulMayThrow(2, false, false, 1) && FnCore.ThanksChance(on, false, ea) == 0;
            bool controlsOk = !FnCore.AddThrows(true, true) && !FnCore.AddSkips(on, true, true)   // firefighter with a current job
                && !FnCore.AddThrows(false, false) && !FnCore.AddSkips(on, false, false)        // no occupation: EA's ?. skips
                && FnCore.ThanksChance(on, true, ea) == ea                                      // pick still in the career
                && !FnCore.GratefulMayThrow(2, true, true, 1)                                   // owner listed and in the career
                && !FnCore.GratefulMayThrow(0, false, false, 0)                                 // nobody listed
                && !FnCore.GratefulMayThrow(2, false, false, 0);                                // every listed Sim in the career
            int fixedN = (add ? 1 : 0) + (thanks ? 1 : 0);
            string v = !on ? (fixedN == 0 ? "BROKEN" : "UNEXPECTED") : (fixedN == 2 ? "FIXED" : "UNEXPECTED");
            string verdict = !controlsOk || live.Length > 0 || missing.Length > 0 ? "UNEXPECTED" : v;
            return "firenre check (" + (on ? "on" : "off") + "): pregnant firefighter's RestartTimer skipped=" + add
                + ", thank-you roll 0 for a pick out of the career=" + thanks + " (" + v + "); controls ok=" + controlsOk
                + "; verdict: " + verdict + "; " + counters;
        }
    }
}
