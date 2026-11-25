using System;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Objects;
using Sims3.Gameplay.Services;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // burglaridle (tuning kBurglarIdle, off by default; docs/568 §1). EA bug fix in BurglarSituation.Stealing.Init.
    //
    // [verified, decompiled] Stealing.Init has two branches that start nothing that ever moves the burglar on:
    //  - fire on the lot: it only pushes FireSource.ReactToFire and relies on that interaction's tail calling
    //    BurglarSituation.SetToLeave. ReactToFire returns before that tail for a Coward/Mummy burglar (CowardReaction),
    //    for a witch burglar at a witch-started fire, and never reaches it when the push is skipped, the interaction is
    //    cancelled or the Sim is reset. No alarm and no callback is set, so the burglar stays in the Stealing state.
    //  - the lot's EffectiveHousehold is gone (household moved out while the burglar was routing): Init returns at once.
    // Either way the burglar stands in the house until the player resets it. Other services (repoman, firefighter) have
    // OnReset/fallback paths; the burglar has none.
    // Fix, after EA's Init and only while that Stealing state is still current:
    //  - no household and nothing stolen yet: EA's SetToLeave at once (LeaveSuccessful; with stolen goods both EA leave
    //    states read the household, so that rare case is left to EA, docs/568 §1);
    //  - fire: a non-persisted watch every 3 sim-minutes (EA's own CheckForSteal interval). While ReactToFire is still
    //    queued or running it waits; once it is gone and the state has not changed, EA's SetToLeave runs, which is what
    //    ReactToFire's tail would have done. If EA already left, the watch sees another state and stops.
    //
    // Counters: [2556] leave with no household, [2557] leave after a fire reaction that did not leave, [2558] errors.
    // Check: BurglarIdleChecks.cs.
    public static partial class Fixes
    {
        internal const float kBiWatchMinutes = 3f;

        static void ApplyBurglarIdle()
        {
            Patch(typeof(BurglarSituation.Stealing), "Init", typeof(BiHook), "InitHook", "InitOrig");
        }

        // ---- decisions (also used by the check) ----
        // After Stealing.Init: 0 = EA path, 1 = leave now (no household, nothing stolen), 2 = start the fire watch.
        internal static int BiAfterInit(bool on, bool stateCurrent, bool noHousehold, int stolenCount, bool fire)
        {
            if (!on || !stateCurrent) return 0;
            if (noHousehold) return stolenCount == 0 ? 1 : 0;
            return fire ? 2 : 0;
        }

        // Fire watch tick: 0 = stop (state changed), 1 = wait (ReactToFire still queued/running), 2 = leave.
        internal static int BiWatch(bool stateCurrent, bool reactQueued)
        {
            if (!stateCurrent) return 0;
            return reactQueued ? 1 : 2;
        }

        static void BiArm(BurglarSituation.Stealing state, BurglarSituation parent)
        {
            Sim w = parent.Worker;
            if (w == null || w.HasBeenDestroyed) return;
            AlarmManager.Global.AddAlarm(kBiWatchMinutes, TimeUnit.Minutes, delegate { BiTick(state, parent); },
                "Ts3Fixes burglaridle fire watch", AlarmType.NeverPersisted, w);
        }

        static void BiTick(BurglarSituation.Stealing state, BurglarSituation parent)
        {
            try
            {
                Sim w = parent.Worker;
                bool current = w != null && !w.HasBeenDestroyed && parent.Child == state;
                bool react = current && w.InteractionQueue.HasInteractionOfType(FireSource.ReactToFire.Singleton);
                switch (BiWatch(current, react))
                {
                    case 1: BiArm(state, parent); break;
                    case 2: PatchCounters()[2557]++; parent.SetToLeave(); break;
                }
            }
            catch (Exception) { PatchCounters()[2558]++; }
        }

        public class BiHook
        {
            public void InitHook(BurglarSituation parent)
            {
                InitOrig(parent);
                if (!ShortcutOn("burglaridle")) return;
                try
                {
                    object st = this;
                    BurglarSituation.Stealing state = (BurglarSituation.Stealing)st;
                    if (parent == null || parent.Lot == null) return;
                    bool noHh = parent.Lot.EffectiveHousehold == null;
                    bool fire = !noHh && parent.Lot.FireManager != null && !parent.Lot.FireManager.NoFire;
                    int stolen = parent.StolenObjects == null ? 0 : parent.StolenObjects.Count;
                    switch (BiAfterInit(true, parent.Child == state, noHh, stolen, fire))
                    {
                        case 1: PatchCounters()[2556]++; parent.SetToLeave(); break;
                        case 2: BiArm(state, parent); break;
                    }
                }
                catch (Exception) { PatchCounters()[2558]++; }
            }
            public void InitOrig(BurglarSituation parent) { } // receives BurglarSituation.Stealing.Init's original body
        }
    }
}
