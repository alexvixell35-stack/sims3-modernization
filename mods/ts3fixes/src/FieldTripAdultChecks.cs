using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Situations;

namespace Ts3Fixes
{
    // fieldtripadult harness check (docs/565 §2). Read-only: nothing in the world changes.
    public static partial class Fixes
    {
        // FieldTripAdultCheck run: (a) no educator and no adult can pass -> the trip ends instead of EA's endless search;
        //   (b) an adult pet and a service NPC on the job fail the chaperone test. Controls: a free human NPC adult passes,
        //   one educator or one adult -> EA's search runs, a teen / a Sim at work fail in both arms.
        //   Live (read-only): instantiated Sims that would pass EA's adult test, and how many of them are pets or on a service.
        //   off BROKEN = search entered with no candidate (hang), pet / service NPC accepted; on FIXED = ended, refused.
        //   (d) carpool home with no selected Sim: wait while a selectable participant has a home, else skip the bus;
        //   controls: a selected Sim with a home runs EA's code in both arms.
        //   result  counters 2527-2529, 2533, 2540
        public static object FieldTripAdultCheck(string what)
        {
            int[] c = PatchCounters();
            string counters = "fieldtripadult: no chaperone (hang avoided) 2527=" + c[2527] + ", unfit chaperones skipped 2528=" + c[2528] + ", deleted/exited handled 2529=" + c[2529] + ", errors 2533=" + c[2533] + ", carpool home handled 2540=" + c[2540];
            if (what == "result") return counters;
            if (what != "run") return "fieldtripadult <run|result>";
            bool on = ShortcutOn("fieldtripadult");
            bool hang = FtaNoChaperone(on, 0, 0);
            bool unfit = !FtaAdultOk(on, true, false, false, false, false, false) && !FtaAdultOk(on, true, false, false, false, true, true);
            bool carpool = FtaCarpool(on, false, false, true) == 1 && FtaCarpool(on, false, false, false) == 2 && FtaCarpool(on, true, false, true) == 2;
            bool control = FtaCarpool(on, true, true, true) != 0 || !FtaAdultOk(on, true, false, false, false, true, false) || FtaAdultOk(on, true, true, false, false, true, false) || FtaAdultOk(on, true, false, true, false, true, false)
                || FtaAdultOk(on, false, false, false, false, true, false) || FtaNoChaperone(on, 1, 0) || FtaNoChaperone(on, 0, 1)
                || !FtaEducatorOk(true, false, false, true, true) || FtaEducatorOk(true, false, true, true, true);
            string live;
            try
            {
                int pass = 0, pets = 0, svc = 0;
                foreach (Sim s in LotManager.Actors)
                {
                    if (s == null || s.SimDescription == null || !FtaAdultOk(false, s.IsNPC, s.SimDescription.TeenOrBelow, s.IsAtWork, false, s.IsHuman, s.IsPerformingAService)) continue;
                    pass++;
                    if (!s.IsHuman) pets++;
                    else if (s.IsPerformingAService) svc++;
                }
                live = "EA adult test passes=" + pass + " (pets " + pets + ", service NPCs " + svc + "), field trips running=" + Sims3.Gameplay.Autonomy.Situation.GetSituations<FieldTripSituation>().Count;
            }
            catch (System.Exception e) { live = "live read threw " + e.GetType().Name; }
            string verdict = control ? "UNEXPECTED" : !on ? (hang || unfit || carpool ? "UNEXPECTED" : "BROKEN") : (hang && unfit && carpool ? "FIXED" : "UNEXPECTED");
            return "fieldtripadult check (switch " + (on ? "on" : "off") + "): trip ends with no chaperone=" + hang + ", pet/service chaperone refused=" + unfit + ", carpool with no selected Sim handled=" + carpool + ", controls=" + control
                + "; live: " + live + "; verdict: " + verdict + "; " + counters;
        }
    }
}
