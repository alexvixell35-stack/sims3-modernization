using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.ObjectComponents;
using Sims3.Gameplay.Objects.Beds;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // docs/597 §3: need-object lines. Off by default; acts on household Sims too (EA bug fix, no skip or throttle).
    //
    // subroutefail (kSubRouteFail; docs/597 §3). [verified, decompiled] SimQueue.WaitForTurn: when TryReplacementInteraction
    // pushes another object of the same need (another toilet, urinal, shower...) as a continuation (WasInUse, FailedToStart),
    // the wait loop breaks with num != 0 and falls into its timeout tail, which calls PlayRouteFail: a route-fail balloon
    // with the busy object's user plus Sim.RouteFailure (a blocking animation) before the Sim walks to the substitute it just
    // found. Nothing yields between the push and that call. Fix: TryReplacementInteraction's hook stamps (Sim, tick) when it
    // pushed; PlayRouteFail skips the route fail for that Sim on that tick. Exit reasons, queue removal and every other
    // route fail stay EA's. Counters: [2763] route fails skipped, [2764] would be skipped (switch off), [2765] errors.
    public static partial class Fixes
    {
        // ---- subroutefail ----

        static Sim sSrfSim;
        static long sSrfTick;

        static void ApplySubRouteFail()
        {
            Patch(typeof(SimQueue), "TryReplacementInteraction", typeof(SrfTryHook), "TryHook", "TryOrig");
            Patch(typeof(SimQueue), "PlayRouteFail", typeof(SrfPlayHook), "PlayHook", "PlayOrig");
        }

        // Pure: skip this route fail? Only when a replacement was pushed for this Sim on this tick and EA would play one.
        internal static bool SrfSkip(bool pushed, bool sameSim, bool sameTick, bool dontPlayFlag)
        {
            return pushed && sameSim && sameTick && !dontPlayFlag;
        }

        public class SrfTryHook
        {
            public bool TryHook(InteractionInstance instance)
            {
                bool r = TryOrig(instance);
                if (r && instance != null)
                {
                    sSrfSim = instance.InstanceActor;
                    sSrfTick = SimClock.CurrentTicks;
                }
                return r;
            }
            public bool TryOrig(InteractionInstance instance) { return false; } // SimQueue.TryReplacementInteraction
        }

        public class SrfPlayHook
        {
            public void PlayHook(SimQueue.WaitBehavior behavior, IGameObject target, Sim sim)
            {
                if (sim != null && sim == sSrfSim)
                {
                    bool sameTick = sSrfTick == SimClock.CurrentTicks;
                    sSrfSim = null;
                    try
                    {
                        if (SrfSkip(true, true, sameTick, (behavior & SimQueue.WaitBehavior.DontPlayRouteFail) != 0))
                        {
                            if (ShortcutOn("subroutefail")) { PatchCounters()[2763]++; return; }
                            PatchCounters()[2764]++;
                        }
                    }
                    catch (Exception) { PatchCounters()[2765]++; }
                }
                PlayOrig(behavior, target, sim);
            }
            public void PlayOrig(SimQueue.WaitBehavior behavior, IGameObject target, Sim sim) { } // SimQueue.PlayRouteFail
        }

        public static object SubRouteFailCheck(string what)
        {
            string counters = RlCounters("subroutefail", 2763, new[] { "route fails skipped (replacement pushed)", "would be skipped (switch off)", "errors" }, "");
            if (what == "result") return counters;
            if (what != "run") return "subroutefail <run|result>";
            bool on = ShortcutOn("subroutefail");
            bool fixedNow = on && SrfSkip(true, true, true, false);
            bool controls = !SrfSkip(false, true, true, false) && !SrfSkip(true, false, true, false) && !SrfSkip(true, true, false, false) && !SrfSkip(true, true, true, true);
            return RlLine("subroutefail", on, "no route fail after the substitute was pushed", fixedNow,
                "no push / other Sim / later tick / DontPlayRouteFail left to EA", controls, "", counters);
        }
    }
}
