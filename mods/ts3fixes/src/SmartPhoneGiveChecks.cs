using System;
using Sims3.Gameplay.Actors;

namespace Ts3Fixes
{
    // Harness checks for SmartPhoneGive.cs (docs/573 §10-11). Pure decisions with controls; nothing in the world is touched.
    public static partial class Fixes
    {
        static string SpgMembers(string method)
        {
            return typeof(Sim).GetMethod(method) == null ? "Sim." + method : "";
        }

        // PhoneFixUpCheck run: a teen just aged up (or a bin household placed) keeps an old basic cell phone.
        //   Controls: a toddler (no phone, EA's grant already removed it) and a pet are left to EA.
        public static object PhoneFixUpCheck(string what)
        {
            string missing = (SpgMembers("FixUpCellPhone") + " " + SpgMembers("GrantSpecialObjects")).Trim();
            string counters = PhCounters("phonefixup", 2668, "phones checked after a grant", missing);
            if (what == "result") return counters;
            if (what != "run") return "phonefixup <run|result>";
            bool on = ShortcutOn("phonefixup");
            bool checkedNow = PfRunFixUp(on, true);
            bool ctlToddler = !PfRunFixUp(on, false);
            return "phonefixup check (switch " + (on ? "on" : "off") + "): phone type checked after the grant=" + checkedNow
                + "; control: toddler or pet left to EA=" + ctlToddler + "; controls ok=" + ctlToddler
                + "; verdict: " + GlVerdict(on, checkedNow, ctlToddler, missing.Length == 0) + "; " + counters;
        }

        // PhoneGiveCheck run: the Phone Settings switch for a Sim holding two phones.
        //   Controls: one phone is replaced as EA does; no phone gets a new one as EA does.
        public static object PhoneGiveCheck(string what)
        {
            string missing = SpgMembers("GivePhoneOfType");
            string counters = PhCounters("phonegive", 2670, "phones given with a correction", missing);
            if (what == "result") return counters;
            if (what != "run") return "phonegive <run|result>";
            bool on = ShortcutOn("phonegive");
            int removed; bool proceed;
            PgPlan(on, 2, out removed, out proceed);
            bool replaced = removed == 2 && proceed;
            PgPlan(on, 1, out removed, out proceed);
            bool ctlOne = removed == 1 && proceed;
            PgPlan(on, 0, out removed, out proceed);
            bool ctlNone = removed == 0 && proceed;
            bool controls = ctlOne && ctlNone;
            return "phonegive check (switch " + (on ? "on" : "off") + "): two phones replaced by the new one=" + replaced
                + "; control: one phone replaced=" + ctlOne + ", no phone given one=" + ctlNone + "; controls ok=" + controls
                + "; verdict: " + GlVerdict(on, replaced, controls, missing.Length == 0) + "; " + counters;
        }
    }
}
