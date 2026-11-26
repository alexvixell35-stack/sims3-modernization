using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.ActiveCareer.ActiveCareers;
using Sims3.Gameplay.Careers;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.UI;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // fireactivejob (tuning kFireActiveJob, off by default; docs/570 §1). EA bug fix in the Ambitions firefighter career.
    //
    // [verified, decompiled] ActiveFireFighter.sActiveJob is a plain static (not [PersistableStatic]) holding the
    // emergency that is on now. FireFightingJob.MakeMapTagForRandomJobVisible sets it when an emergency is announced;
    // only that job's Cleanup / OnCompletion set it back to null. Nothing clears it when the world is quit (load another
    // save, quit to the menu, travel), and the script assemblies stay loaded, so the value survives into the next world
    // [inferred: docs/192 §3; EA marks the statics it wants reset or saved with PersistableStatic]. In the new world:
    //   - FireFightingJob.PersistPostLoad adopts a loaded emergency only "if sActiveJob == null", so a saved emergency is
    //     not adopted and its OnLoadFixup does not subscribe the job tracker;
    //   - ActiveFireFighter.CurrentJob returns the dead job for every household firefighter, so IsWorkHour is always true
    //     ("on the job" around the clock) and Respond to Emergency / extinguish priorities read a lot from the old world;
    //   - MakeMapTagForRandomJobVisible starts with "if (sActiveJob != null) LeaveJob": every new emergency is dropped,
    //     so the firefighter gets no more calls until the game is restarted.
    // Reach: an emergency announced (alarm ringing or on the way) when the player saves and loads, loads another save, or
    // travels.
    // Fix: on world quit, sActiveJob is set back to null (as the job's own Cleanup does). At world load, a value that is not
    // a job of any Sim in this world is dropped and EA's PersistPostLoad rule is replayed (the loaded FireFightingJob
    // with its map tag on; its tracker is subscribed as EA's OnLoadFixup does). A live emergency is never touched.
    // Counters: [3953] cleared at quit, [3954] stale value dropped at load, [3955] errors. Check: FireActiveJobChecks.cs.
    public static partial class Fixes
    {
        static readonly FieldInfo sFajMapTag = typeof(Sims3.Gameplay.Careers.Job).GetField("bMapTagEnabled", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        static readonly MethodInfo sFajMinute = typeof(FireFightingJob).GetMethod("SimMinuteChanged", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, Type.EmptyTypes, null);
        static bool sFajHooked;

        static void ApplyFireActiveJob()
        {
            if (sFajHooked) return;
            sFajHooked = true;
            World.OnWorldQuitEventHandler += delegate { FireActiveJobQuit(); };
        }

        // ---- decision (also used by the check): drop the static's value? ----
        internal static bool FajDrop(bool on, bool hasValue, bool liveInThisWorld)
        {
            return on && hasValue && !liveInThisWorld;
        }

        static void FireActiveJobQuit()
        {
            try
            {
                if (!ShortcutOn("fireactivejob") || ActiveFireFighter.sActiveJob == null) return;
                ActiveFireFighter.sActiveJob = null;
                PatchCounters()[3953]++;
            }
            catch (Exception) { PatchCounters()[3955]++; }
        }

        // Every FireFightingJob held by a Sim of this world (the jobs PersistPostLoad ran on).
        internal static List<FireFightingJob> FajLiveJobs()
        {
            List<FireFightingJob> r = new List<FireFightingJob>();
            foreach (SimDescription d in Household.EveryHumanSimDescription())
            {
                Occupation o = d == null ? null : d.Occupation;
                if (!(o is ActiveFireFighter) || o.Jobs == null) continue;
                foreach (Sims3.Gameplay.Careers.Job j in o.Jobs)
                {
                    FireFightingJob f = j as FireFightingJob;
                    if (f != null && !r.Contains(f)) r.Add(f);
                }
            }
            return r;
        }

        // World load finished (Instantiator).
        public static void FireActiveJobWorldLoad()
        {
            try
            {
                if (!ShortcutOn("fireactivejob")) return;
                FireFightingJob cur = ActiveFireFighter.sActiveJob;
                if (cur == null) return;
                List<FireFightingJob> live = FajLiveJobs();
                if (!FajDrop(true, true, live.Contains(cur))) return;
                ActiveFireFighter.sActiveJob = null;
                PatchCounters()[3954]++;
                // EA's FireFightingJob.PersistPostLoad / OnLoadFixup, for the job that was skipped
                foreach (FireFightingJob j in live)
                {
                    if (sFajMapTag == null || !(bool)sFajMapTag.GetValue(j)) continue;
                    ActiveFireFighter.sActiveJob = j;
                    if (j.HasBeenSetup && sFajMinute != null)
                        Sims3.Gameplay.UI.Responder.Instance.SimMinuteChanged += (VoidEventHandler)Fixes.Bind(typeof(VoidEventHandler), j, sFajMinute);
                    break;
                }
            }
            catch (Exception) { PatchCounters()[3955]++; }
        }
    }
}
