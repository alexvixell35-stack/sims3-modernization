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
    // observatorymsg: docs/555 script logic audit. Off by default. Harness check: ObservatoryMsgChecks.cs.
    // observatorymsg (tuning kObservatoryMsg; docs/555 §6). StellarObservatory+TravelToSimulonV tests `num3 > 0 || num3 > 0`
    // (dark matter twice) before its summary notice, so a trip that found only stardust shows "found nothing" although the
    // stardust is in the inventory [verified, decompiled]. The fix: Sim.ShowTNSIfSelectable(TNSNames, actor, target,
    // params) turns SummaryOfNothingFoundOnSimulonV into SummaryOfEverythingFoundOnSimulonV when its stardust argument is
    // above 0 (the observatory is the only caller of that notice). Items, events and skill are untouched.
    // Counters [2424] notices corrected (counted with the switch off too), [2425] errors.
    public static partial class Fixes
    {
        static void ApplyObservatoryMsg()
        {
            Patch(typeof(Sim), "ShowTNSIfSelectable", new Type[] { typeof(TNSNames), typeof(object), typeof(object), typeof(object[]) },
                typeof(ObsHook), "TnsHook", "TnsOrig");
        }

        public class ObsHook
        {
            public StyledNotification TnsHook(TNSNames notificationId, object actor, object target, params object[] parameters)
            {
                if (notificationId == TNSNames.SummaryOfNothingFoundOnSimulonV)
                {
                    try { notificationId = ObsPick(notificationId, parameters); } catch (Exception) { PatchCounters()[2425]++; }
                }
                return TnsOrig(notificationId, actor, target, parameters);
            }
            public StyledNotification TnsOrig(TNSNames notificationId, object actor, object target, params object[] parameters) { return null; } // receives the original body
        }

        // EA passes (actor, stardust, dark matter): stardust above 0 means something was found.
        static TNSNames ObsPick(TNSNames id, object[] p)
        {
            if (id != TNSNames.SummaryOfNothingFoundOnSimulonV || p == null || p.Length < 2 || !(p[1] is int) || (int)p[1] <= 0) return id;
            PatchCounters()[2424]++;
            return ShortcutOn("observatorymsg") ? TNSNames.SummaryOfEverythingFoundOnSimulonV : id;
        }
    }
}
