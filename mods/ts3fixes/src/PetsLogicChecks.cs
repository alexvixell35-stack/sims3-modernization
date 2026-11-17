using System;
using Sims3.SimIFace.CAS;

namespace Ts3Fixes
{
    // Harness checks for PetsLogic.cs (docs/572 §13-15). Read-only decisions with controls.
    public static partial class Fixes
    {
        // HorseTraitScoreCheck run: whose traits feed the competition score.
        //   Controls: the scored trait list is EA's four (Fast, Agile, Lazy, Hates Jumping).
        public static object HorseTraitScoreCheck(string what)
        {
            int[] c = PatchCounters();
            string missing = HtMissing();
            string counters = "horsetraitscore: scores computed on the horse 2630=" + c[2630] + ", errors 2631=" + c[2631] + "; " + GlMembers(missing);
            if (what == "result") return counters;
            if (what != "run") return "horsetraitscore <run|result>";
            bool on = ShortcutOn("horsetraitscore");
            bool horse = HtUseHorse(on);
            bool ctlList = sHtTraits.Length == 4;
            return "horsetraitscore check (switch " + (on ? "on" : "off") + "): horse traits scored (not the rider's)=" + horse
                + "; control: EA's four traits=" + ctlList + "; controls ok=" + ctlList
                + "; verdict: " + GlVerdict(on, horse, ctlList, missing.Length == 0) + "; " + counters;
        }

        // WildHorseGenderCheck run: EA's roll said Male in the 80%-female case (a female lead exists).
        //   Controls: the lead-horse rule's Female is never swapped.
        public static object WildHorseGenderCheck(string what)
        {
            int[] c = PatchCounters();
            string counters = "wildhorsegender: genders swapped 2632=" + c[2632] + ", errors 2633=" + c[2633] + "; " + GlMembers("");
            if (what == "result") return counters;
            if (what != "run") return "wildhorsegender <run|result>";
            bool on = ShortcutOn("wildhorsegender");
            bool female = WgGender(on, false, CASAgeGenderFlags.Male) == CASAgeGenderFlags.Female;
            bool ctlLead = WgGender(on, true, CASAgeGenderFlags.Female) == CASAgeGenderFlags.Female;
            return "wildhorsegender check (switch " + (on ? "on" : "off") + "): the 80% roll gives a mare=" + female
                + "; control: lead-horse rule kept=" + ctlLead + "; controls ok=" + ctlLead
                + "; verdict: " + GlVerdict(on, female, ctlLead, true) + "; " + counters;
        }

        // LitterCapCheck run: 6 Sims + 2 pets; EA allows a litter of up to 4 (6 - 2 pets).
        //   Controls: 2 Sims + 1 pet keeps EA's 5.
        public static object LitterCapCheck(string what)
        {
            int[] c = PatchCounters();
            string counters = "littercap: caps lowered 2634=" + c[2634] + ", errors 2635=" + c[2635] + "; " + GlMembers("");
            if (what == "result") return counters;
            if (what != "run") return "littercap <run|result>";
            bool on = ShortcutOn("littercap");
            int cap = LcCap(on, 4, 6, 2);
            bool within = cap <= 2;
            bool ctlSmall = LcCap(on, 5, 2, 1) == 5;
            return "littercap check (switch " + (on ? "on" : "off") + "): litter cap for 6 Sims + 2 pets=" + cap + " (keeps 10 members=" + within + ")"
                + "; control: small household keeps 5=" + ctlSmall + "; controls ok=" + ctlSmall
                + "; verdict: " + GlVerdict(on, within, ctlSmall, true) + "; " + counters;
        }
    }
}
