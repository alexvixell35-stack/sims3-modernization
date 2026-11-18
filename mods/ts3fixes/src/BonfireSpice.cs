using System;
using System.Reflection;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Objects.Environment;
using Sims3.Gameplay.Objects.FoodObjects;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // bonfirespice (tuning kBonfireSpice, off by default; docs/561 §6). EA bug fixes, exact for every Sim.
    //
    // BonFire+SpiceItUp.Run [verified, decompiled BonFire.cs]:
    //  (a) takes the herb out of the Sim's inventory (TryToRemove) BEFORE FindOpenSlotAndRoute; a failed route returns
    //      false with the herb neither back in the inventory nor destroyed: the player loses it (it stays out of the
    //      world, saved with the game). On success the removed herb is never destroyed either (Fireplace's BurnHerb does
    //      DestroyObject): one leaked out-of-world herb per spice.
    //  (b) the "already burning" test is only in the Definition's Test, and BonFire.mHerbToAdd is one field. Two Sims
    //      spicing at once (NPC guests at a bonfire party do it autonomously) both pass; the first SpiceHitFire event
    //      consumes mHerbToAdd and sets it null, the second throws in HerbData.Initialize(null) inside the animation event,
    //      and a second herb that does land overwrites mHerbEffectBroadcast / mHerbFX without disposing them: the first
    //      haze broadcaster and effect stay around the bonfire for good (RemoveHerb only disposes the current one).
    // Fix: (a) after Run, a herb this Run removed that is neither in the world nor in an inventory goes back to the
    // Sim's inventory on failure and is destroyed on success; (b) SpiceHitFire with no herb to add returns (EA would
    // throw), and with a herb already burning EA's RemoveHerb runs first, so the new herb replaces it cleanly.
    // Counters: [2483] herbs returned or destroyed, [2484] overlapping spice events fixed, [2485] errors.
    // Harness check: BonfireSpiceChecks.cs.
    public static partial class Fixes
    {
        static FieldInfo sBfHerb, sBfBurnout;

        static void ApplyBonfireSpice()
        {
            sBfHerb = typeof(BonFire.SpiceItUp).GetField("mHerb", kInstAll);
            sBfBurnout = typeof(BonFire).GetField("mHerbBurnoutHandle", kInstAll);
            if (sBfHerb == null || sBfBurnout == null) { AppliedLog().Add("bonfirespice: BonFire members not found, not patched"); return; }
            Patch(typeof(BonFire.SpiceItUp), "Run", typeof(BfRunHook), "RunHook", "RunOrig");
            Patch(typeof(BonFire), "SpiceHitFire", typeof(BfFireHook), "HitHook", "HitOrig");
        }

        public class BfRunHook
        {
            public bool RunHook()
            {
                bool r = RunOrig();
                if (!ShortcutOn("bonfirespice")) return r;
                try { BfAfterRun((object)this as BonFire.SpiceItUp, r); } catch (Exception) { PatchCounters()[2485]++; }
                return r;
            }
            public bool RunOrig() { return false; } // receives BonFire+SpiceItUp.Run's original body
        }

        public class BfFireHook
        {
            public void HitHook(StateMachineClient smc, IEvent evt)
            {
                if (ShortcutOn("bonfirespice"))
                {
                    bool skip = false;
                    try { skip = BfBeforeHit((object)this as BonFire); } catch (Exception) { PatchCounters()[2485]++; }
                    if (skip) return;
                }
                HitOrig(smc, evt);
            }
            public void HitOrig(StateMachineClient smc, IEvent evt) { } // receives BonFire.SpiceHitFire's original body
        }

        // What happens to the herb a finished Run removed (also used by the check): 0 leave, 1 back to inventory, 2 destroy.
        internal static int BfHerbAction(bool on, bool runOk, bool herbExists, bool inWorld, bool inInventory)
        {
            if (!on || !herbExists || inWorld || inInventory) return 0;
            return runOk ? 2 : 1;
        }

        static void BfAfterRun(BonFire.SpiceItUp ia, bool ok)
        {
            if (ia == null) return;
            Herb herb = sBfHerb.GetValue(ia) as Herb;
            Sim sim = ia.Actor;
            if (herb == null || sim == null || sim.Inventory == null) return;
            int act = BfHerbAction(true, ok, !herb.HasBeenDestroyed, herb.InWorld, herb.InInventory);
            if (act == 0) return;
            if (act == 1) { if (!sim.Inventory.TryToAdd(herb)) return; }
            else herb.Destroy();
            PatchCounters()[2483]++;
        }

        // True = skip EA's body (no herb to add). A herb already burning is removed first (EA's RemoveHerb).
        static bool BfBeforeHit(BonFire fire)
        {
            if (fire == null) return false;
            if (fire.mHerbToAdd == null) { PatchCounters()[2484]++; return true; }
            if ((AlarmHandle)sBfBurnout.GetValue(fire) != AlarmHandle.kInvalidHandle) { fire.RemoveHerb(); PatchCounters()[2484]++; }
            return false;
        }
    }
}
