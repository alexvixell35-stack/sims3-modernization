using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.EventSystem;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;
using Sims3.Store.Objects;

namespace Ts3Fixes
{
    // buffstate harness check (docs/557). Production code: BuffSharedState.cs.
    public static partial class Fixes
    {
        // BuffStateCheck (repro buff-shared-state)
        //   run     two instantiated NPC Sims (not in the active household, not selectable) A and B: BuffDowntrodden's
        //           OnAddition for A then B, OnRemoval for A, through the patch (the buff is not added to their buff
        //           managers: its ShouldAdd wants a nightlife venue; the methods are what the bug is in). Then counts
        //           each Sim's PushDowntrodden alarms. Off arm: BROKEN = A keeps 1 alarm, B has 0 (EA removed B's).
        //           On arm: FIXED = A 0, B 1. Cleanup in both arms: OnRemoval for B, any alarm left on A or B removed.
        //   result  counters 2428-2434
        public static object BuffStateCheck(string what)
        {
            int[] c = PatchCounters();
            string counters = "buffstate: down leaks 2428=" + c[2428] + ", down removed 2429=" + c[2429] + ", curse wrong 2430=" + c[2430] + ", curse fixed 2431=" + c[2431]
                + ", fertility 2432=" + c[2432] + ", handlers 2433=" + c[2433] + ", errors 2434=" + c[2434];
            if (what == "result") return counters;
            if (what != "run") return "buffstate <run|result>";
            Sim a = null, b = null;
            foreach (Sim s in LotManager.Actors)
            {
                if (s == null || s.HasBeenDestroyed || s.IsSelectable || s.Household == Household.ActiveHousehold || s.BuffManager == null || s.SimDescription == null) continue;
                if (s.BuffManager.HasElement(BuffNames.Downtrodden) || BsDownAlarms(s).Count > 0) continue;
                if (a == null) a = s; else { b = s; break; }
            }
            if (a == null || b == null) return "buffstate check: needs two NPC Sims in the world (N/A)";
            BuffInstance proto;
            if (!BuffManager.BuffDictionary.TryGetValue((ulong)BuffNames.Downtrodden, out proto) || !(proto.mBuff is BuffDowntrodden)) return "buffstate check: no Downtrodden buff (N/A)";
            Buff buff = proto.mBuff;
            string thrown = "none";
            int aLeft = -1, bLeft = -1;
            try
            {
                buff.OnAddition(a.BuffManager, null, false);
                buff.OnAddition(b.BuffManager, null, false);
                buff.OnRemoval(a.BuffManager, null);
                aLeft = BsDownAlarms(a).Count; bLeft = BsDownAlarms(b).Count;
            }
            catch (Exception e) { thrown = e.GetType().Name + ": " + e.Message; }
            try
            {
                buff.OnRemoval(b.BuffManager, null);
                foreach (Sim s in new Sim[] { a, b }) foreach (AlarmHandle h in BsDownAlarms(s)) s.RemoveAlarm(h);
            }
            catch (Exception e) { thrown += "; cleanup " + e.GetType().Name; }
            bool on = ShortcutOn("buffstate");
            string verdict = thrown != "none" ? "UNEXPECTED" : aLeft == 0 && bLeft == 1 ? "FIXED" : aLeft == 1 && bLeft == 0 ? "BROKEN" : "UNEXPECTED";
            return "buffstate check (switch " + (on ? "on" : "off") + "): " + a.FullName + " alarms left " + aLeft + " (after its removal), " + b.FullName + " " + bLeft
                + " (buff still up); exception " + thrown + "; verdict: " + verdict + "; " + counters;
        }
    }
}
