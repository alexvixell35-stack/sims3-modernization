using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Careers;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.EventSystem;
using Sims3.Gameplay.Objects.Miscellaneous;
using Sims3.Gameplay.Objects.Pets;
using Sims3.Gameplay.Situations;
using Sims3.Gameplay.Socializing;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;
using Sims3.UI.Controller;

namespace Ts3Fixes
{
    // Batch 8 (docs/582 B8; REA tranche 4, docs/584 §7, plus the coverage sweep, docs/586). All default off. simler90
    // changes none of the methods below (docs/data/mod-method-diff.csv; its Secrets.cs has the same key bug), and none
    // was hooked before (docs/data/swap-stack-audit.csv). The other tranche-4 leads are already covered: R2 and R5(b) by
    // fieldtripadult (FieldTripAdult.cs), R1 by prankditchempty (Batch582Logic.cs); R3 and R5(a) are not reachable with
    // the shipped game data (docs/582 B8 notes).
    public static partial class Fixes
    {
        // ---- B8 §5 weddinglistener (tuning kWeddingListener) -------------------------------------------------------------
        // [verified, decompiled] WeddingParty's constructor adds a world-wide kMarriage listener (OnMarriage) to
        // mEventListeners, PostLoad re-adds it, and CleanUp never removes it; OnMarriage returns Remove only when someone in
        // the party marries. A party that ends without a wedding leaves the listener holding the dead situation, and a later
        // marriage of a former guest runs RemoveGuest / OtherHosts.Add on it. Fix after EA's CleanUp: the party's listeners
        // are removed from EventTracker. Household-visible: player-hosted wedding parties (no visible change; stale state).
        // Counters: [2920] listeners removed, [2921] left with the switch off, [2922] errors.
        static readonly FieldInfo sWlList = typeof(WeddingParty).GetField("mEventListeners", kInstAll);

        // Pure: how many listeners to remove (the ones still registered), 0 with the switch off.
        internal static int WlRemove(bool on, int stillRegistered) { return on ? stillRegistered : 0; }

        static void ApplyWeddingListener()
        {
            if (sWlList == null) { AppliedLog().Add("weddinglistener: not found: WeddingParty.mEventListeners"); return; }
            Patch(typeof(WeddingParty), "CleanUp", Type.EmptyTypes, typeof(BWlHook), "CleanHook", "CleanOrig");
        }

        public class BWlHook : HouseParty  // derives from EA's base so `base.CleanUp()` is EA's non-virtual base call
        {
            public void CleanHook()
            {
                try
                {
                    CleanOrig();
                }
                finally { After(this); }  // EA's CleanUp can throw on a null host/guest (review 2026-11-15); listeners still go
            }
            static void After(object party)
            {
                try
                {
                    List<EventListener> list = sWlList.GetValue(party) as List<EventListener>;
                    if (list == null) return;
                    int live = 0;
                    foreach (EventListener l in list) if (l != null && EventTracker.ContainsListener(l)) live++;
                    if (live == 0) return;
                    if (WlRemove(ShortcutOn("weddinglistener"), live) == 0) { PatchCounters()[2921] += live; return; }
                    foreach (EventListener l in list) if (l != null) EventTracker.RemoveListener(l);
                    PatchCounters()[2920] += live;
                }
                catch (Exception) { PatchCounters()[2922]++; }
            }
            public void CleanOrig() { } // receives WeddingParty.CleanUp
        }
    }
}
