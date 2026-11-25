using System;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // NRaas Overwatch's nightly Stuck Check (docs/04 F18, docs/07 §2.2) plans a route per standing-still Sim
    // (SimEx.IsPointInLotSafelyRoutable -> Route.PlanFromPoint), all in one FunctionTask tick: ~300 ms per sim night.
    // The hook runs NRaas's own check unchanged, then yields one sim tick when the caller may yield (the alarm's
    // FunctionTask or the menu interaction), so each plan lands in its own tick. The loop, its order and every decision
    // stay NRaas's: the plan and the reset choice for a Sim happen in the same tick as before; only later Sims and the
    // other nightly cleanups run a few ticks later. Not cached: the route depends on the whole Sim (species, posture,
    // ghost buff, boat, room limits in SetRouteOptions), so another Sim's or another tick's verdict is not exact.
    // Overwatch is a separate assembly, maybe loaded after us: tried at startup, again at world load (the check only
    // runs from the nightly alarm, which starts after world load, so the method is not compiled yet).
    // Switch "overwatchstuck" (default on). Counters: [44] checks seen, [45] yields.
    // Switch "overwatchstuckslice" (tuning kOverwatchStuckSlice, default off; docs/145 §12): the Stuck Check's walk over
    // every Sim between two route checks is one tick too (57 ms with no plan, 100-336 ms between two plans in SV). A hook
    // on Overwatch's SimTypes.IsSelectable(Sim), the first call the walk makes per Sim, takes a tick there once the
    // stretch reached OverwatchSpreadCore.kStuckSliceUs, only while the PerformAlarm hook (OverwatchSpread.cs) marks the
    // Stuck Check as running and a yield is allowed. A Sim that left the world during the tick gets the answer that puts
    // it in Overwatch's not-in-world branch. The stretches are measured with the switch off too.
    // Counters: [255] ticks taken, [256] Sims walked, [257] longest stretch without a tick (us), [258] Stuck Check runs,
    // [259] stretch time summed (ms), [302] longest stretch seen at the IsSelectable hook (us; the slice check's own view).
    public static partial class Fixes
    {
        static bool sOverwatchDone, sOwNoYieldLogged;
        static bool sOwInStuck;          // Overwatch's Stuck Check is running (PerformAlarm hook)
        static StopWatch sOwStuckWatch;  // its current stretch: since its start or the last tick taken inside it
        static long sOwStuckUs;          // [259] source

        internal static void OwStuckBegin()
        {
            if (sOwStuckWatch == null) sOwStuckWatch = StopWatch.Create(StopWatch.TickStyles.Microseconds);
            sOwStuckWatch.Restart();
            sOwInStuck = true;
            PatchCounters()[258]++;
        }

        internal static void OwStuckEnd()
        {
            if (!sOwInStuck) return;
            OwStuckStretch();
            sOwInStuck = false;
        }

        static void OwStuckStretch()
        {
            long us = sOwStuckWatch.GetElapsedTime();
            int[] c = PatchCounters();
            if (us > c[257]) c[257] = (int)Math.Min(us, int.MaxValue);
            sOwStuckUs += us;
            c[259] = (int)Math.Min(sOwStuckUs / 1000, int.MaxValue);
        }

        internal static void ApplyOverwatchStuck(bool lastTry)
        {
            if (sOverwatchDone) return;
            Type simEx = null;
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                if (a.GetName().Name == "NRaasOverwatch") { simEx = a.GetType("NRaas.CommonSpace.Helpers.SimEx"); break; }
            if (simEx == null)
            {
                if (lastTry) { sOverwatchDone = true; AppliedLog().Add("Overwatch SimEx: not loaded, skipped"); }
                return;
            }
            sOverwatchDone = true;
            MethodInfo m = simEx.GetMethod("IsPointInLotSafelyRoutable", BindingFlags.Static | BindingFlags.Public, null,
                new Type[] { typeof(Sim), typeof(Lot), typeof(Vector3) }, null);
            if (m == null || m.ReturnType != typeof(bool)) { AppliedLog().Add("Overwatch SimEx: signature differs, skipped"); return; }
            Patch(simEx, "IsPointInLotSafelyRoutable", typeof(OverwatchStuckHook), "RoutableHook", "RoutableOrig");
            Type simTypes = simEx.Assembly.GetType("NRaas.CommonSpace.Helpers.SimTypes");
            MethodInfo sel = simTypes == null ? null : simTypes.GetMethod("IsSelectable", BindingFlags.Static | BindingFlags.Public, null, new Type[] { typeof(Sim) }, null);
            if (sel == null || sel.ReturnType != typeof(bool)) AppliedLog().Add("Overwatch SimTypes.IsSelectable: signature differs, overwatchstuckslice off");
            else Patch(simTypes, "IsSelectable", new Type[] { typeof(Sim) }, typeof(OverwatchSelectHook), "SelectHook", "SelectOrig");
        }

        // Overwatch's own SimTypes.IsSelectable(Sim) (its copy of NRaas Common; other NRaas mods have their own). Outside a
        // Stuck Check run it is the original.
        public static class OverwatchSelectHook
        {
            public static bool SelectHook(Sim sim)
            {
                if (sOwInStuck)
                {
                    int[] c = PatchCounters();
                    c[256]++;
                    long us = sOwStuckWatch.GetElapsedTime();
                    if (us > c[302]) c[302] = (int)Math.Min(us, int.MaxValue); // [257] also counts stretches that end in a route check; this is what the slice check sees
                    if (OverwatchSpreadCore.SliceDue(us) && ShortcutOn("overwatchstuckslice")
                        && OverwatchSpreadCore.OurStuckSlice(sOwUpstreamVersion) && MayYield())
                    {
                        OwStuckStretch();
                        Simulator.Sleep(0);
                        WaitWhileFrozen(1731, NotLiveOrGone); // paused / clock stopped in live mode: the walk waits (docs/205 §2.3d)
                        c[255]++;
                        sOwStuckWatch.Restart();
                        if (OverwatchSpreadCore.SkipAfterSlice(sim == null || sim.HasBeenDestroyed, sim != null && sim.InWorld, sim != null && sim.Proxy != null)) return true;
                    }
                }
                return SelectOrig(sim);
            }
            public static bool SelectOrig(Sim sim) { return false; } // receives SimTypes.IsSelectable(Sim)'s original header
        }

        public static class OverwatchStuckHook
        {
            public static bool RoutableHook(Sim ths, Lot lot, Vector3 pos)
            {
                bool ok;
                sInOwnCheck = true; // not counted as the Sim's own route failures (watchdog route-fail case)
                try { ok = RoutableOrig(ths, lot, pos); } finally { sInOwnCheck = false; }
                int[] c = PatchCounters();
                c[44]++;
                if (!ShortcutOn("overwatchstuck")) return ok;
                if (!OverwatchSpreadCore.OurStuckYield(sOwUpstreamVersion)) return ok; // Overwatch v2+ sleeps after each check itself
                if (MayYield()) // not inside a native->managed call (RouteBackoff.cs MayYield)
                {
                    c[45]++;
                    if (sOwInStuck) OwStuckStretch();
                    Simulator.Sleep(0);
                    WaitWhileFrozen(1731, NotLiveOrGone); // paused / clock stopped in live mode: the walk waits (docs/205 §2.3d)
                    if (sOwInStuck) sOwStuckWatch.Restart();
                }
                else if (!sOwNoYieldLogged)
                {
                    // which gate refused (docs/145 §11): EA's may-yield test 0xE53550 / yielding-disabled flag, or ts3native's root-state answer
                    sOwNoYieldLogged = true;
                    OwLog("StuckCheck plan without a yield: context " + Simulator.CheckYieldingContext(false) + ", yielding disabled " + Simulator.YieldingDisabled
                        + ", ts3native no-yield " + sYieldProbe.GetOption2(kNoYield));
                }
                return ok;
            }
            public static bool RoutableOrig(Sim ths, Lot lot, Vector3 pos) { return false; } // receives SimEx.IsPointInLotSafelyRoutable's original header
        }
    }
}
