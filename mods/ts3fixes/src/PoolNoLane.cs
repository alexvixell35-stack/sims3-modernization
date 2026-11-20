using System;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Pools;
using Sims3.SimIFace;
using Sims3.Gameplay.Interactions;

namespace Ts3Fixes
{
    // poolnolane (tuning kPoolNoLane, ON since the 2026-11-20 import; an EA bug fix, NPCs only). GetInPool.Run and DoggyGetInPool.Run plan
    // only PlanToSlot(pool, PoolPortalComponent.GetRoutingSlotsForGroup(Inside)), the Daredevil diving-board tries with that
    // same array; no plan, no route = Run returns false. GetRoutingSlotsForGroup hands back one entry per lane and puts
    // Slot.None for every lane whose slot is disabled (Slots.IsSlotEnabled). [verified: decompiled GetInPool, DoggyGetInPool,
    // PoolPortalComponent, SimIFace Route.PlanToSlot; simler90 UPDATE242 plans the same way (its Run follows the plan with
    // DoRoute instead of RouteToPoint).] The native slot-array generator makes a candidate only from a slot of the target
    // object and returns false with no blocker when it made none, which is status 10, FailedBecauseNoDestinationSpecified
    // (docs/101 §4.1). [inferred for Slot.None.] Not the cause of the BP home pool failures ('Air Spray', 869 GetInPool + 976
    // DoggyGetInPool Plan failures in 11-17/18 runs, all result 10, blocker None, nodes 0 in 0 plans): those are dynamic. In
    // etl14b af18-bp-31361-allb the same Sims fail and succeed interleaved on the same lane from the same room (77 failed,
    // 82 good).
    // The lane list is the same each time, so the native per-slot check rejects the slot at plan time (docs/548 §2: a slot
    // rejected without a blocker id gives result 10, blocker None) [inferred]; autonomyfailcooldown covers those repeats.
    // Fix: an autonomous offer of either interaction whose pool has no enabled Inside lane at all (every entry Slot.None, or
    // no lane) is refused after EA's own Test passed. Exactness: with every entry Slot.None, each PlanToSlot of Run has no slot
    // to generate from, so Run's plans cannot succeed and Run cannot route; the refusal only spares the failed plans, and
    // autonomy scores something else. Household rule: the refusal is a skip of an autonomous pick, so household, selected and
    // unknown-scope Sims (IsPlayerSim, never lifted by HouseholdToo) keep EA's offer. A pool with one enabled lane is EA's
    // as before; so are player-directed picks (the pie
    // menu does not change) and DoggySwimAround / RunAround (their wander and radial plans pick random points: no exact
    // guard). Lane state can change only when the lot is edited, between this Test and Run. Counters [1348] autonomous offers
    // refused, [1349] would have been refused (switch off), [1350] errors.
    public static partial class Fixes
    {
        static void ApplyPoolNoLane()
        {
            const BindingFlags nested = BindingFlags.Public | BindingFlags.NonPublic;
            Type[] args = { typeof(Sim), typeof(Pool), typeof(bool), typeof(GreyedOutTooltipCallback).MakeByRefType() };
            Type gd = typeof(GetInPool).GetNestedType("Definition", nested);
            Type dg = typeof(Pool).Assembly.GetType("Sims3.Gameplay.Pools.DoggyGetInPool");
            Type dd = dg == null ? null : dg.GetNestedType("Definition", nested);
            if (gd != null) Patch(gd, "Test", args, typeof(PoolNoLaneHook), "TestHook", "TestOrig");
            else AppliedLog().Add("GetInPool+Definition: not found");
            if (dd != null) Patch(dd, "Test", args, typeof(PoolNoLaneHook), "DTestHook", "DTestOrig");
            else AppliedLog().Add("DoggyGetInPool+Definition: not found");
        }

        public class PoolNoLaneHook
        {
            public bool TestHook(Sim a, Pool target, bool isAutonomous, ref GreyedOutTooltipCallback greyedOutTooltipCallback)
            {
                return TestOrig(a, target, isAutonomous, ref greyedOutTooltipCallback) && !(isAutonomous && !IsPlayerSim(a) && PoolNoLaneRefuse(target));
            }
            public bool TestOrig(Sim a, Pool target, bool isAutonomous, ref GreyedOutTooltipCallback greyedOutTooltipCallback) { return false; } // GetInPool+Definition.Test
            public bool DTestHook(Sim a, Pool target, bool isAutonomous, ref GreyedOutTooltipCallback greyedOutTooltipCallback)
            {
                return DTestOrig(a, target, isAutonomous, ref greyedOutTooltipCallback) && !(isAutonomous && !IsPlayerSim(a) && PoolNoLaneRefuse(target));
            }
            public bool DTestOrig(Sim a, Pool target, bool isAutonomous, ref GreyedOutTooltipCallback greyedOutTooltipCallback) { return false; } // DoggyGetInPool+Definition.Test
        }

        // true = refuse: switch on and the pool has no enabled Inside lane
        static bool PoolNoLaneRefuse(Pool p)
        {
            try
            {
                PoolPortalComponent pc = p == null ? null : p.PoolPortalComponent;
                if (pc == null) return false;  // EA's Run meets the same null: its outcome, not ours
                Slot[] s = pc.GetRoutingSlotsForGroup(PoolPortalComponent.RoutingSlotGroup.Inside);
                for (int i = 0; i < s.Length; i++) if (s[i] != Slot.None) return false;
                if (!ShortcutOn("poolnolane")) { PatchCounters()[1349]++; return false; }
                PatchCounters()[1348]++;
                return true;
            }
            catch (Exception) { PatchCounters()[1350]++; return false; }
        }
    }
}
