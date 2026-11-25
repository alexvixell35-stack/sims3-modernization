using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using Sims3.Gameplay;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.EventSystem;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Objects.Entertainment;
using Sims3.Gameplay.Socializing;
using Sims3.SimIFace;
using Sims3.UI.Hud;

namespace Ts3Fixes
{
    // geniebanish harness check (docs/559 §4). // Harness checks for this family: throwaway objects, never added to the world; nothing in the world changes.
    public static partial class Fixes
    {
        // GenieBanishCheck run: the lot index for NextDouble() = 0.9999 over the world's community lots (read only).
        //   off BROKEN = EA's pick lands on Count (out of range); on FIXED = the fixed pick stays below Count. Nothing is cast.
        public static object GenieBanishCheck(string what)
        {
            int[] c = PatchCounters();
            string counters = "geniebanish: fixed runs 2453=" + c[2453] + ", errors 2454=" + c[2454];
            if (what == "result") return counters;
            if (what != "run") return "geniebanish <run|result>";
            if (sGbCost == null) return "geniebanish check: not patched (N/A)";
            bool on = ShortcutOn("geniebanish");
            List<Lot> lots = LotManager.GetAllCommunityLots();
            int n = lots == null ? 0 : lots.Count;
            if (n == 0) return "geniebanish check: no community lots in this world (N/A)";
            int idx = GbPickIndex(n, 0.9999, on);
            string verdict = idx >= n ? "BROKEN" : idx >= 0 ? "FIXED" : "UNEXPECTED";
            return "geniebanish check (switch " + (on ? "on" : "off") + "): " + n + " community lots, pick at r=0.9999 -> index " + idx + "; verdict: " + verdict + "; " + counters;
        }
    }
}
