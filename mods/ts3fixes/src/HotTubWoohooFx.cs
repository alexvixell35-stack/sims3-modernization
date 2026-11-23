using System;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Objects.Plumbing;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // hottubwoohoofx (tuning kHotTubWoohooFx, off by default; docs/561 §7). EA bug fix, exact for every Sim.
    //
    // HotTubBase+CuddleSeatedWooHoo [verified, decompiled]: the woohoo animation's event 110 calls StartWoohooFX and event
    // 111 calls StopWoohooFX; the only other StopWoohooFX caller is the tub's CleanupFromDisposeOrReset. The
    // interaction's Cleanup clears the tub's mSimsAreWooHooing flag but leaves the effect. A Sim reset (or the woohoo cut
    // off) between events 110 and 111 runs Cleanup (the queue resets before the posture, so the Sim is still in the tub)
    // and the woohoo effect stays on the tub until the tub itself is reset.
    // Fix: when that Cleanup ran for a Sim in a hot tub and left mSimsAreWooHooing false, the tub's StopWoohooFX runs too
    // (a no-op after a normal woohoo, where event 111 already stopped it).
    // Counters: [2491] effects stopped, [2492] errors. Check: HotTubWoohooFxChecks.cs.
    public static partial class Fixes
    {
        static readonly FieldInfo sHtFx = typeof(HotTubBase).GetField("mWooHooFX", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        static readonly FieldInfo sHtFlag = typeof(HotTubBase).GetField("mSimsAreWooHooing", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        static void ApplyHotTubWoohooFx()
        {
            if (sHtFx == null || sHtFlag == null) { AppliedLog().Add("hottubwoohoofx: HotTubBase woohoo fields not found, not patched"); return; }
            Patch(typeof(HotTubBase.CuddleSeatedWooHoo), "Cleanup", typeof(HtWoohooHook), "CleanupHook", "CleanupOrig");
        }

        // The decision (also used by the check): the woohoo is over (flag cleared) and its effect is still on.
        internal static bool HtStopFx(bool on, bool inTub, bool stillWoohooing, bool fxOn)
        {
            return on && inTub && !stillWoohooing && fxOn;
        }

        public class HtWoohooHook
        {
            public void CleanupHook()
            {
                if (!ShortcutOn("hottubwoohoofx")) { CleanupOrig(); return; }  // fast path: switch off
                HotTubBase tub = null;
                try
                {
                    HotTubBase.CuddleSeatedWooHoo ia = (object)this as HotTubBase.CuddleSeatedWooHoo;
                    Sim actor = ia == null ? null : ia.Actor;
                    if (actor != null && actor.Posture != null) tub = actor.Posture.Container as HotTubBase;
                }
                catch (Exception) { PatchCounters()[2492]++; }
                CleanupOrig();
                if (tub == null) return;
                try
                {
                    if (!HtStopFx(true, true, (bool)sHtFlag.GetValue(tub), sHtFx.GetValue(tub) != null)) return;
                    tub.StopWoohooFX();
                    PatchCounters()[2491]++;
                }
                catch (Exception) { PatchCounters()[2492]++; }
            }
            public void CleanupOrig() { } // receives HotTubBase.CuddleSeatedWooHoo.Cleanup's original body
        }
    }
}
