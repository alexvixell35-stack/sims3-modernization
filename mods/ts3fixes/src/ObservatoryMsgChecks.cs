using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Academics;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Careers;
using Sims3.Gameplay.EventSystem;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Objects.Miscellaneous;
using Sims3.SimIFace;
using Sims3.UI;

namespace Ts3Fixes
{
    // observatorymsg harness check (docs/555 §6). Throwaway objects; nothing in the world changes.
    public static partial class Fixes
    {
        // ObservatoryMsgCheck run: the notice choice for a stardust-only trip (3 stardust, 0 dark matter) and an empty trip.
        //   off BROKEN = "nothing found" for the stardust trip; on FIXED = "everything found"; the empty trip keeps "nothing".
        public static object ObservatoryMsgCheck(string what)
        {
            int[] c = PatchCounters();
            string counters = "observatorymsg: corrected 2424=" + c[2424] + ", errors 2425=" + c[2425];
            if (what == "result") return counters;
            if (what != "run") return "observatorymsg <run|result>";
            TNSNames dust = ObsPick(TNSNames.SummaryOfNothingFoundOnSimulonV, new object[] { null, 3, 0 });
            TNSNames none = ObsPick(TNSNames.SummaryOfNothingFoundOnSimulonV, new object[] { null, 0, 0 });
            bool fixedDust = dust == TNSNames.SummaryOfEverythingFoundOnSimulonV, emptyOk = none == TNSNames.SummaryOfNothingFoundOnSimulonV;
            string verdict = !emptyOk ? "UNEXPECTED" : fixedDust ? "FIXED" : "BROKEN";
            return "observatorymsg check (switch " + (ShortcutOn("observatorymsg") ? "on" : "off") + "): stardust-only " + dust + ", empty " + none + "; verdict: " + verdict + "; " + counters;
        }
    }
}
