using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Careers;
using Sims3.Gameplay.EventSystem;
using Sims3.Gameplay.Opportunities;
using Sims3.Gameplay.Skills;
using Sims3.Gameplay.Socializing;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;
using Sims3.SimIFace.CustomContent;
using Sims3.UI;
using Sims3.UI.Hud;

namespace Ts3Fixes
{
    // Batch 8b (docs/582 B8b; coverage sweep docs/586 §4-§5). All default off. simler90 changes none of these methods
    // and none was hooked before. BuffImminentNemesis (coverage #5) needs an EA-intent decision and is not built.
    public static partial class Fixes
    {
        // ---- B8b §2 innerbeautyonce (tuning kInnerBeautyOnce) ------------------------------------------------------------
        // [verified] Each Inner Beauty buff instance adds its own kDidMeanSocial listener (unfiltered), each bound to
        // that instance's OnDidMeanSocial, which acts on e.Actor, not on its own Sim. With N Inner Beauty Sims one mean social runs N
        // times: N "MeanSocialTNS" notices and N timeout resets. Fix: each event (EventTracker makes a new Event per send)
        // is handled once; the other listeners' calls for the same event return Keep. Household-visible: yes (one notice).
        // Also (review 2026-11-15): EA shows the notice outside its `if (buff != null)`, so every selectable Sim's mean
        // social says "Inner Beauty" whether or not that Sim has the buff; with the switch on an actor without the buff gets
        // nothing (EA's reset does nothing for it either). Counted in [2923] / [2924].
        // Counters: [2923] duplicate calls skipped, [2924] duplicates seen with the switch off, [2925] errors.
        static Event sIbLast;
        // Pure: skip this call? same: the event was handled by another listener already; hasBuff: the actor has Inner Beauty.
        internal static bool IbSkip(bool on, bool same, bool hasBuff) { return on && (same || !hasBuff); }

        static void ApplyInnerBeautyOnce()
        {
            Patch(typeof(InnerBeauty.BuffInstanceInnerBeauty), "OnDidMeanSocial", new Type[] { typeof(Event) }, typeof(BIbHook), "MeanHook", "MeanOrig");
        }

        public class BIbHook
        {
            public ListenerAction MeanHook(Event e)
            {
                bool same = e != null && ReferenceEquals(e, sIbLast);
                sIbLast = e;
                bool hasBuff = true;
                try
                {
                    Sim a = e == null ? null : e.Actor as Sim;
                    hasBuff = a == null || a.BuffManager == null || a.BuffManager.HasElement(BuffNames.InnerBeauty);
                }
                catch (Exception) { PatchCounters()[2925]++; }
                if (same || !hasBuff)
                {
                    if (IbSkip(ShortcutOn("innerbeautyonce"), same, hasBuff)) { PatchCounters()[2923]++; return ListenerAction.Keep; }
                    PatchCounters()[2924]++;
                }
                return MeanOrig(e);
            }
            public ListenerAction MeanOrig(Event e) { return ListenerAction.Keep; } // receives InnerBeauty.BuffInstanceInnerBeauty.OnDidMeanSocial
        }

    }
}
