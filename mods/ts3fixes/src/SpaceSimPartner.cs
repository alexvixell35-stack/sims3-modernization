using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using Sims3.Gameplay;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.EventSystem;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Objects.Entertainment;
using Sims3.Gameplay.Socializing;
using Sims3.SimIFace;
using Sims3.UI.Hud;

namespace Ts3Fixes
{
    // spacesimpartner: found by the docs/559 rule scan. Off by default. Harness check: SpaceSimPartnerChecks.cs.
    // spacesimpartner (tuning kSpaceSimPartner; docs/559 §5). SpaceSimulator+RideWith.Run pushes DoRideB on the partner,
    // then may return before the ride starts (rider cancelled while waiting, partner failed route) [verified]. DoRideB
    // waits `while (!SimAInRide)` and then `while (!RideOver)`, leaving only when the rider still has a cancel exit reason;
    // once the rider's interaction is over that reason is gone, so the partner stands in the simulator waiting for good
    // (NPCs forever; a household Sim until the player cancels). Fix (RideWith.Run hook, after EA's Run): when the ride
    // never started (RideOver false) and the rider did not already signal a failed route, the partner's DoRideB for this
    // simulator is cancelled (EA's CancelInteraction) and SimAFailedToRoute set. Counters [2455] partners released,
    // [2456] partners left waiting (counted with the switch off too), [2457] errors.
    // Same switch, second flaw (docs/559 §10): RideWith.Run sets RideOver = true right before DoTimedLoop (the ride), so
    // the partner's DoRideB leaves its `while (!RideOver)` wait, plays its end reaction and finishes at the START of the
    // ride while the rider rides on [verified order; the effect on the partner's animation is inferred: see the in-game
    // counters]. Fix: the RideOver getter reads false while the rider's RideWith.Run is still running, so the partner's
    // wait ends with the ride. Counters (always on, they measure the flaw in game): [2467] rides whose partner finished
    // before the rider's ride ended, [2468] rides whose partner was still riding at the end.
    public static partial class Fixes
    {
        static void ApplySpaceSimPartner()
        {
            Patch(typeof(SpaceSimulator.RideWith), "Run", typeof(SsHook), "RunHook", "RunOrig");
            Patch(typeof(SpaceSimulator), "get_RideOver", typeof(SsSimHook), "RideOverHook", "RideOverOrig");
        }

        public class SsHook
        {
            public bool RunHook()
            {
                SpaceSimulator.RideWith rw = (object)this as SpaceSimulator.RideWith;
                SpaceSimulator ss = rw == null ? null : rw.Target;
                if (ss != null) SsRiding(ss, +1);
                try { return RunOrig(); }
                finally
                {
                    if (ss != null) SsRiding(ss, -1);
                    try { SsRideEnd(rw); SsAfterRun(rw); } catch (Exception) { PatchCounters()[2457]++; }
                }
            }
            public bool RunOrig() { return false; } // receives SpaceSimulator+RideWith.Run's original body
        }

        // spacesimpartner (§10): RideOver reads false while the rider still rides
        public class SsSimHook
        {
            public bool RideOverHook()
            {
                bool v = RideOverOrig();
                return v && !(ShortcutOn("spacesimpartner") && SsIsRiding((object)this as SpaceSimulator));
            }
            public bool RideOverOrig() { return false; } // receives SpaceSimulator.get_RideOver's original body
        }

        // Simulators whose RideWith.Run is running (a Dictionary: no HashSet in the game's mscorlib/System).
        static readonly Dictionary<SpaceSimulator, int> sSsRiding = new Dictionary<SpaceSimulator, int>();

        static void SsRiding(SpaceSimulator ss, int delta)
        {
            int n;
            sSsRiding.TryGetValue(ss, out n);
            n += delta;
            if (n <= 0) sSsRiding.Remove(ss); else sSsRiding[ss] = n;
        }

        static bool SsIsRiding(SpaceSimulator ss)
        {
            return ss != null && sSsRiding.ContainsKey(ss);
        }

        static DoRideBState SsPartnerRide(SpaceSimulator ss)
        {
            Sim b = ss.SimB;
            if (ReferenceEquals(b, null) || b.HasBeenDestroyed || b.InteractionQueue == null) return DoRideBState.Gone;
            foreach (InteractionInstance i in b.InteractionQueue.InteractionList)
            {
                SpaceSimulator.DoRideB dr = i as SpaceSimulator.DoRideB;
                if (dr != null && ReferenceEquals(dr.spaceSim, ss)) return DoRideBState.Riding;
            }
            return DoRideBState.Gone;
        }

        enum DoRideBState { Gone, Riding }

        // After a ride that ran (RideOver set by this rider, both Sims in): was the partner still in its DoRideB?
        static void SsRideEnd(SpaceSimulator.RideWith rw)
        {
            if (rw == null) return;
            SpaceSimulator ss = rw.Target;
            if (ss == null || !ReferenceEquals(ss.SimA, rw.Actor) || !ss.RideOver || !ss.SimAInRide || !ss.SimBInRide) return;
            if (SsPartnerRide(ss) == DoRideBState.Riding) PatchCounters()[2468]++; else PatchCounters()[2467]++;
        }

        // True when this rider's ride never started and its partner has not been told: DoRideB would wait for good.
        static bool SsPartnerStranded(SpaceSimulator ss, Sim rider)
        {
            return ss != null && rider != null && ReferenceEquals(ss.SimA, rider) && !ReferenceEquals(ss.SimB, null) && !ReferenceEquals(ss.SimB, rider) && !ss.RideOver && !ss.SimAFailedToRoute;
        }

        static void SsAfterRun(SpaceSimulator.RideWith rw)
        {
            if (rw == null) return;
            SpaceSimulator ss = rw.Target;
            if (!SsPartnerStranded(ss, rw.Actor)) return;
            Sim b = ss.SimB;
            if (b.HasBeenDestroyed || b.InteractionQueue == null) return;
            List<InteractionInstance> waiting = new List<InteractionInstance>();
            foreach (InteractionInstance i in b.InteractionQueue.InteractionList)
            {
                SpaceSimulator.DoRideB dr = i as SpaceSimulator.DoRideB;
                if (dr != null && ReferenceEquals(dr.spaceSim, ss)) waiting.Add(dr);
            }
            if (waiting.Count == 0) return;
            PatchCounters()[2456]++;
            if (!ShortcutOn("spacesimpartner")) return;
            ss.SimAFailedToRoute = true;  // DoRideB's first wait leaves on this flag
            foreach (InteractionInstance i in waiting) b.InteractionQueue.CancelInteraction(i, false);
            PatchCounters()[2455]++;
        }
    }
}
