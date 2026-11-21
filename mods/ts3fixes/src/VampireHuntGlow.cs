using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Interactions;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // vampirehuntglow (tuning kVampireHuntGlow, off by default; docs/561 §7). EA bug fix, exact for every Sim.
    //
    // OccultVampire+VampireHunt [verified, decompiled]: Run gives every huntable Sim on the lot the HeatVision visual
    // override (World.ObjectSetVisualOverride), then LoopDel (every kRemoveSimFromListEveryXMinutes) drops one candidate
    // and clears its override until one is left. Before that, LoopDel removes every candidate whose LotCurrent is no
    // longer the vampire's lot, without clearing its override, and Cleanup only clears the Sims still in the list. A
    // patron who walks out of the club (or off a residential lot) during the hunt keeps glowing in heat vision for the
    // rest of the session, wherever it goes, until the Sim object is reset or uninstantiated.
    // Fix: before EA's LoopDel body, each candidate that left the lot and still exists gets its override set back to
    // None, as EA does for every other dropped candidate. The hunt's choice and timing are unchanged.
    // Counters: [2489] glows cleared, [2490] errors. Check: VampireHuntGlowChecks.cs.
    public static partial class Fixes
    {
        static readonly FieldInfo sVhList = typeof(OccultVampire.VampireHunt).GetField("mHuntableSims", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        static void ApplyVampireHuntGlow()
        {
            if (sVhList == null) { AppliedLog().Add("vampirehuntglow: VampireHunt.mHuntableSims not found, not patched"); return; }
            Patch(typeof(OccultVampire.VampireHunt), "LoopDel", typeof(VhHook), "LoopHook", "LoopOrig");
        }

        // The decision (also used by the check): a candidate that left the vampire's lot and still exists.
        internal static bool VhClearGlow(bool on, bool leftLot, bool destroyed)
        {
            return on && leftLot && !destroyed;
        }

        public class VhHook
        {
            public void LoopHook(StateMachineClient smc, InteractionInstance.LoopData loopData)
            {
                if (ShortcutOn("vampirehuntglow"))
                {
                    try { VhClearLeavers((object)this as OccultVampire.VampireHunt); }
                    catch (Exception) { PatchCounters()[2490]++; }
                }
                LoopOrig(smc, loopData);
            }
            public void LoopOrig(StateMachineClient smc, InteractionInstance.LoopData loopData) { } // receives VampireHunt.LoopDel's original body
        }

        static void VhClearLeavers(OccultVampire.VampireHunt hunt)
        {
            if (hunt == null || hunt.Actor == null) return;
            List<Sim> list = sVhList.GetValue(hunt) as List<Sim>;
            if (list == null) return;
            foreach (Sim s in list)
            {
                if (s == null) continue;
                if (!VhClearGlow(true, s.LotCurrent != hunt.Actor.LotCurrent, s.HasBeenDestroyed)) continue;
                World.ObjectSetVisualOverride(s.ObjectId, eVisualOverrideTypes.None, null);
                PatchCounters()[2489]++;
            }
        }
    }
}
