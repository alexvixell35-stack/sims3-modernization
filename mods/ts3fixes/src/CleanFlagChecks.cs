using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Objects.CookingObjects;
using Sims3.Gameplay.Objects.Electronics;
using Sims3.Gameplay.Objects.Environment;
using Sims3.Gameplay.Objects.HobbiesSkills;
using Sims3.Gameplay.Objects.MinorPets;
using Sims3.Gameplay.Objects.Misc;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // cleanflag harness check (docs/559 §8). Throwaway objects, never added to the world; nothing in the world changes.
    public static partial class Fixes
    {
        // CleanFlagCheck run: a throwaway PicnicBasket (uninitialized, never placed): the end-of-run rule for a run that
        //   set BeingCleaned and left it set. off BROKEN = flag kept (basket unusable); on FIXED = flag cleared; a flag
        //   already set at entry (another cleaner) is never touched.
        public static object CleanFlagCheck(string what)
        {
            int[] c = PatchCounters();
            string counters = "cleanflag: flags cleared/seen 2463=" + c[2463] + ", errors 2464=" + c[2464];
            if (what == "result") return counters;
            if (what != "run") return "cleanflag <run|result>";
            bool on = ShortcutOn("cleanflag");
            PicnicBasket basket = (PicnicBasket)FormatterServices.GetUninitializedObject(typeof(PicnicBasket));
            basket.BeingCleaned = true;
            if (CfLeftSet(false, basket.BeingCleaned)) basket.BeingCleaned = false;
            bool otherKept = !CfLeftSet(true, true);
            string verdict = !otherKept ? "UNEXPECTED" : basket.BeingCleaned ? "BROKEN" : "FIXED";
            return "cleanflag check (switch " + (on ? "on" : "off") + "): flag after an already-cleaned exit=" + basket.BeingCleaned + ", other cleaner's flag kept=" + otherKept + "; verdict: " + verdict + "; " + counters;
        }
    }
}
