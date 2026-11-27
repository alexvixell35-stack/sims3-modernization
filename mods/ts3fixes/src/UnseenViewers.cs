using System;
using System.Collections.Generic;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // Active-household positions for ts3native's [UnseenThrottle] (docs/143 §6, decoder UtViewerMsg in
    // native/ts3native/unseen.cpp): a walking Sim within 80 m of one of these never has its steering obstacle list skipped,
    // the household part of FarFromViewers that native code cannot know. One Route.GetOption2 call per message,
    // RouteOption2 bits 63 + 57 set, the code in bits 48-55, a 32-bit value in bits 0-31: 1 BEGIN, 2/3/4 x/y/z float bits
    // (z adds one viewer), 5 END with the count. ts3native answers BEGIN with false when the throttle is not installed (or
    // there is no ts3native, or a build without the channel: the game answers false); then the push stops for this world
    // load. ts3native treats a table older than 2 s as missing, and with NeedViewers=1 then skips nothing.
    //
    // docs/330 viewerpushdelta (default ON; off = both tables sent on every wake, as before 2026-11-22, for A/B). The loop
    // still wakes every kViewerPushTicks and reads the household positions; it sends
    //  - the viewer table only when the count changed, a viewer moved more than kViewerMoveM from its last sent position, or
    //    kViewerBeatMs passed since the last send (ts3native drops tables older than 2000 ms);
    //  - the held table only when its ids changed or kViewerBeatMs passed;
    // and walks LotManager.Actors for rides on every wake while the last walk or the household had a ride candidate, else
    // every kHeldSparseWakes-th wake. The household's own Mount / Dismount / BeingDismounted / ridden horse are read from
    // the household every wake, so a household ride is held at once. Staleness bounds: docs/330 §3.
    // [1577] wakes, [1578] ms in wakes, [1579] longest wake us, [1580] viewer table sends, [1581] Actors walks,
    // [1582] held table sends, [1583] Sims walked in Actors, [1584] Actors walks skipped.
    public static partial class Fixes
    {
        const ulong kViewerBits = 0x8200000000000000UL;
        const int kViewerMax = 32;           // ts3native's table size
        const uint kViewerPushTicks = 5;      // a walking Sim moves well under a metre between pushes
        const float kViewerMoveM = 0.5f;
        const long kViewerBeatMs = 500;       // a quarter of ts3native's 2000 ms table age
        const int kHeldSparseWakes = 4;
        static int sViewerGen;

        internal static void StartViewerPush()
        {
            UtbWorldLoad();  // docs/227 §6: ask ts3native for the unseen table again this load
            sUbOff = false;  // docs/322: ask ts3native's band split again this load
            sNsOff = false; sNsPushes = 0;  // docs/329: ask ts3native's non-Sim split for the lot table again this load
            int gen = ++sViewerGen;  // a loop left from an earlier load ends at its next wake-up
            sViewerSent = null; sHeldSent.Clear(); sHeldSawAny = true; sHeldWake = 0;  // the first wake sends both tables
            sRides.Clear(); sRideSeeded = false; sRideFallback = false;  // docs/335: seed the ride set with one walk this load
            Simulator.AddObject(new LoopTask(delegate { ViewerPushLoop(gen); }));
        }

        static void ViewerPushLoop(int gen)
        {
            while (gen == sViewerGen)
            {
                bool go;
                ViewerWatch().Restart();
                try { go = ShortcutOn("viewerpushdelta") ? PushViewersDelta() : PushViewers(); } catch (Exception) { return; }
                ViewerWakeDone();
                if (!go) return;
                Simulator.Sleep(kViewerPushTicks);
            }
        }

        static StopWatch sViewerWatch, sViewerClock;  // us in this wake; ms since the first wake (uint: wraps after 49 days)
        static StopWatch ViewerWatch()
        {
            if (sViewerWatch == null) sViewerWatch = StopWatch.Create(StopWatch.TickStyles.Microseconds);
            return sViewerWatch;
        }
        static uint ViewerNowMs()
        {
            if (sViewerClock == null) { sViewerClock = StopWatch.Create(StopWatch.TickStyles.Milliseconds); sViewerClock.Start(); }
            return sViewerClock.GetElapsedTime();
        }

        static long sViewerWakeUs;
        static void ViewerWakeDone()
        {
            long us = ViewerWatch().GetElapsedTime();
            int[] c = PatchCounters();
            c[1577]++;
            sViewerWakeUs += us;
            c[1578] = (int)(sViewerWakeUs / 1000);
            if (us > c[1579]) c[1579] = (int)Math.Min(us, int.MaxValue);
        }

        static bool ViewerSend(int code, uint value)
        {
            return sYieldProbe.GetOption2((Sims3.SimIFace.Route.RouteOption2)(kViewerBits | (ulong)(uint)code << 48 | value));
        }

        static uint Bits(float f) { FloatBits b = new FloatBits(); b.F = f; return b.U; }  // RouteBackoff.cs union

        // false = BEGIN refused: no throttle this load
        static bool PushViewers()
        {
            if (!ViewerSend(1, 0)) return false;
            PatchCounters()[1580]++;
            uint n = 0;
            Household h = Household.ActiveHousehold;
            if (h != null)
                foreach (Sim s in h.AllActors)
                {
                    if (s == null) continue;
                    if (n == kViewerMax) break;
                    Vector3 p = s.Position;
                    // a refused field leaves the native table as it was (it goes stale, so nothing is skipped)
                    if (!ViewerSend(2, Bits(p.x)) || !ViewerSend(3, Bits(p.y)) || !ViewerSend(4, Bits(p.z))) return true;
                    n++;
                }
            ViewerSend(5, n);
            PushHeld();
            if (!sNsOff && sNsPushes++ % kLotPushEvery == 0) PushLots();
            return true;
        }

        // docs/330: the same tables, sent when they changed or are due (see the header)
        static List<float> sViewerSent;
        static readonly List<float> sViewerCur = new List<float>();
        static uint sViewerSentMs, sHeldSentMs;
        static bool PushViewersDelta()
        {
            Household h = Household.ActiveHousehold;
            List<Sim> actors = h != null ? h.AllActors : null;  // a new list per call: read once per wake
            sViewerCur.Clear();
            if (actors != null)
                foreach (Sim s in actors)
                {
                    if (s == null) continue;
                    if (sViewerCur.Count == kViewerMax * 3) break;
                    Vector3 p = s.Position;
                    sViewerCur.Add(p.x); sViewerCur.Add(p.y); sViewerCur.Add(p.z);
                }
            uint now = ViewerNowMs();
            if (LoopCostCore.ViewersDue(sViewerSent, sViewerCur, kViewerMoveM, sViewerSent == null ? -1 : (long)unchecked(now - sViewerSentMs), kViewerBeatMs))
            {
                if (!ViewerSend(1, 0)) return false;
                PatchCounters()[1580]++;
                bool all = true;
                for (int i = 0; i < sViewerCur.Count; i += 3)
                    if (!ViewerSend(2, Bits(sViewerCur[i])) || !ViewerSend(3, Bits(sViewerCur[i + 1])) || !ViewerSend(4, Bits(sViewerCur[i + 2]))) { all = false; break; }
                // refused part-way: the native table is left as it was and goes stale; sent again next wake
                if (all && ViewerSend(5, (uint)(sViewerCur.Count / 3))) { sViewerSent = new List<float>(sViewerCur); sViewerSentMs = now; }
                else sViewerSent = null;
            }
            PushHeldDelta(actors, now);
            // docs/329 lot table on the same wake count as PushViewers (it was left out here, so lots stayed 0 with this path ON)
            if (!sNsOff && sNsPushes++ % kLotPushEvery == 0) PushLots();
            return true;
        }

        // docs/322 [UnseenProbe] Bands (measurement only): the actor of each autonomy search (B7 entry, sim thread), so ts3native
        // can put the AutonomyManager task's run time in that Sim's distance band. Codes 27 id low, 28 id high (commits). Refused
        // (bands off, no TaskSeam, no ts3native) = no more sends this load. Reads the id only; changes nothing.
        static bool sUbOff;
        internal static void UbAutActor(Sims3.Gameplay.Autonomy.Autonomy a)
        {
            if (sUbOff || OnWorker()) return;  // sim thread only (the viewer channel is)
            try
            {
                Sim s = a != null ? a.Actor : null;
                if (s == null) return;
                ulong id = s.ObjectId.Value;
                if (!ViewerSend(27, (uint)id) || !ViewerSend(28, (uint)(id >> 32))) sUbOff = true;
            }
            catch (Exception) { sUbOff = true; }
        }

        // docs/329 [Diag] NonSimSplit (measurement only): every lot's ObjectId, centre, half diagonal and flags (1 the active
        // household's home lot, 2 the world lot), so ts3native can put a Lot task's run time in the lot's distance band and keep
        // household lots and the objects on them in the household row (always live). Codes 29 BEGIN, 30/31 id low/high, 32-34
        // centre x/y/z, 35 flags, 36 radius (appends), 37 END with the count. At load and every kLotPushEvery viewer pushes
        // (~1000 ticks: lots never move, the home lot changes on a move). Refused (split off, no ts3native) = no more sends this
        // load. Reads only.
        const int kLotPushEvery = 200;
        static bool sNsOff;
        static int sNsPushes;
        static void PushLots()
        {
            if (!ViewerSend(29, 0)) { sNsOff = true; return; }
            Household h = Household.ActiveHousehold;
            Sims3.Gameplay.Core.Lot home = h != null ? h.LotHome : null;
            uint n = 0;
            foreach (object o in Sims3.Gameplay.Core.LotManager.AllLots)
            {
                Sims3.Gameplay.Core.Lot lot = o as Sims3.Gameplay.Core.Lot;
                if (lot == null) continue;
                ulong id = lot.ObjectId.Value;
                bool world = lot.IsWorldLot;
                Vector3 c = world ? Vector3.Empty : lot.GetCenterPosition();
                Vector3 corner;
                float r = world ? 0f : (float)Math.Sqrt(lot.GetClosestCornerToPoint(c, out corner));  // returns the squared distance
                uint flags = (lot == home ? 1u : 0u) | (world ? 2u : 0u);
                if (!ViewerSend(30, (uint)id) || !ViewerSend(31, (uint)(id >> 32)) || !ViewerSend(32, Bits(c.x)) || !ViewerSend(33, Bits(c.y))
                    || !ViewerSend(34, Bits(c.z)) || !ViewerSend(35, flags) || !ViewerSend(36, Bits(r))) { sNsOff = true; return; }
                n++;
            }
            ViewerSend(37, n);
        }

        // The active household, ridden horses and their riders, and both sides of a Mount / Dismount / BeingDismounted, held at full rate by
        // ts3native's throttle (docs/143 §8.10): codes 12 BEGIN, 13 id low, 14 id high (appends), 15 END with the count.
        const int kHeldMax = 32;  // ts3native's table size
        static readonly List<ulong> sHeld = new List<ulong>();
        static void HoldSim(Sim s) { if (s != null && sHeld.Count < kHeldMax && !sHeld.Contains(s.ObjectId.Value)) sHeld.Add(s.ObjectId.Value); }
        static void PushHeld()
        {
            sRideSeeded = false;  // docs/335: the hooks' set is not kept up on this path; walk again if it comes back
            sHeld.Clear();
            Household h = Household.ActiveHousehold;  // docs/249: first, the table holds 32
            if (h != null) foreach (Sim s in h.AllActors) HoldSim(s);
            // LotManager.Actors: EA's list of Sims in the world (added on a lot, removed when destroyed). Queries.GetObjects<Sim>()
            // walked every world object every 5 ticks: ~5% of the sim thread on a 14,500-object save (issue #156 sampler).
            PatchCounters()[1581]++;
            foreach (Sim s in Sims3.Gameplay.Core.LotManager.Actors)
            {
                PatchCounters()[1583]++;
                if (s == null || s.HasBeenDestroyed) continue;
                HoldRide(s);
            }
            SendHeld();
        }

        // a ride candidate (s not destroyed): a ridden horse and its rider, or both sides of a running Mount / Dismount /
        // BeingDismounted; true when s is one
        static bool HoldRide(Sim s)
        {
            Sims3.Gameplay.ActorSystems.BeingRiddenPosture ridden = s.Posture as Sims3.Gameplay.ActorSystems.BeingRiddenPosture;
            Sims3.Gameplay.Interactions.InteractionInstance i = s.CurrentInteraction;
            bool mount = i is Sim.Mount || i is Sim.Dismount || i is Sim.BeingDismounted;
            if (ridden != null) { HoldSim(s); HoldSim(ridden.Rider); }
            if (mount) { HoldSim(s); HoldSim(i.Target as Sim); }
            return ridden != null || mount;
        }

        static bool SendHeld()
        {
            if (!ViewerSend(12, 0)) return false;
            foreach (ulong id in sHeld)
                if (!ViewerSend(13, (uint)id) || !ViewerSend(14, (uint)(id >> 32))) return false;  // refused: the old table goes stale
            if (!ViewerSend(15, (uint)sHeld.Count)) return false;
            PatchCounters()[1582]++;
            return true;
        }

        // docs/330: the same held table as PushHeld (household first, then rides in LotManager.Actors order), Actors walked
        // per LoopCostCore.HeldWalkDue, sent when the ids changed or kViewerBeatMs passed
        static readonly List<ulong> sHeldSent = new List<ulong>();
        static bool sHeldSawAny = true;
        static int sHeldWake;
        static void PushHeldDelta(List<Sim> actors, uint now)
        {
            int[] c = PatchCounters();
            sHeld.Clear();
            bool hhRide = false;
            if (actors != null) foreach (Sim s in actors) HoldSim(s);
            // the household's own rides every wake (their other side may be an NPC horse); the ids join the walk's below
            // docs/335 viewerridehooks: the household's own rides are held from this list (not only from the hook set)
            bool hooks = sRideHooksIn && ShortcutOn("viewerridehooks");
            if (!hooks) { sRides.Clear(); sRideSeeded = false; }  // the hook set is not kept up while off (no stale Sims held): walk again when it comes on
            if (actors != null) foreach (Sim s in actors) if (s != null && !s.HasBeenDestroyed && RideCandidate(s)) { hhRide = true; if (hooks) HoldRide(s); }
            // and the horse under a household rider (the walk holds it as a ridden horse), without needing the hooks
            if (hooks && actors != null) foreach (Sim s in actors) if (s != null) HoldHorseUnder(s);
            sHeldWake++;
            if (hooks)
            {
                HeldRides(c, now);
                sHeldSawAny = true;  // switched off later: walk at once, as after a wake that saw a ride
            }
            else if (LoopCostCore.HeldWalkDue(sHeldSawAny || hhRide, sHeldWake, kHeldSparseWakes))
            {
                c[1581]++;
                bool any = false;
                foreach (Sim s in Sims3.Gameplay.Core.LotManager.Actors)
                {
                    c[1583]++;
                    if (s == null) continue;
                    // the ride test before HasBeenDestroyed (a field and a list read; HasBeenDestroyed asks the native
                    // proxy table): the same Sims are held, destroyed ones still skipped
                    if (!RideCandidate(s) || s.HasBeenDestroyed) continue;
                    HoldRide(s);
                    any = true;
                }
                sHeldSawAny = any;
            }
            else
            {
                c[1584]++;
                // no ride anywhere at the last walk: only the household's own (none, or hhRide had walked) are held
            }
            if (!LoopCostCore.SameIds(sHeld, sHeldSent) || (long)unchecked(now - sHeldSentMs) >= kViewerBeatMs)
            {
                if (SendHeld()) { sHeldSent.Clear(); sHeldSent.AddRange(sHeld); sHeldSentMs = now; }
                else sHeldSent.Clear();  // refused: send again next wake
            }
        }

        // docs/335 viewerridehooks (default off, needs viewerpushdelta): the held table's rides without a LotManager.Actors walk
        // per wake. A Sim passes RideCandidate only in BeingRiddenPosture (set only in RidingPosture.StartRiding) or while its
        // current interaction is a Mount / Dismount / BeingDismounted (current from just before Run is entered until the queue
        // dequeues it, no yield in between) [verified decompiled scripts]; hooks on those four bodies note the Sims into
        // sRides, and each wake keeps the ones still passing (HoldRide, as the walk). The full walk stays for the seed (load,
        // switch turned on, a full set) and every kRideFloorMs (rides a mod starts past the hooks: one found = walk every wake
        // for the rest of the load). Shadow (viewerridehookshadow, diagnostic, off) compares with the walk every wake.
        // [1662] Sims noted by the hooks, [1663] wakes from the set, [1664] set Sims tested, [1665] seed / floor walks,
        // [1666] floor walks that found a ride missing from the set (walk every wake from then), [1667] shadow wakes,
        // [1668] shadow wakes whose sets differ (must stay 0).
        const long kRideFloorMs = 10000;
        const int kRideSetMax = 256;   // past this the set is dropped and walked again (bounds the references it keeps)
        static bool sRideHooksIn;      // all four hooks went in at startup
        static readonly List<Sim> sRides = new List<Sim>();
        static bool sRideSeeded, sRideFallback;
        static uint sRideWalkMs;
        static readonly List<ulong> sRideShadow = new List<ulong>();

        internal static void ApplyViewerRideHooks()
        {
            bool ok = Patch(typeof(Sims3.Gameplay.ActorSystems.RidingPosture), "StartRiding", new Type[] { typeof(Sim), typeof(Sim), typeof(StateMachineClient) }, typeof(RideStartHook), "StartHook", "StartOrig");
            ok &= Patch(typeof(Sim.Mount), "Run", Type.EmptyTypes, typeof(RideRunHook), "MountHook", "MountOrig");
            ok &= Patch(typeof(Sim.Dismount), "Run", Type.EmptyTypes, typeof(RideRunHook), "DismountHook", "DismountOrig");
            ok &= Patch(typeof(Sim.BeingDismounted), "Run", Type.EmptyTypes, typeof(RideRunHook), "BeingDismountedHook", "BeingDismountedOrig");
            sRideHooksIn = ok;  // one refused (another mod compiled or swapped it first): the old walk stays
            Sims3.SimIFace.World.OnWorldQuitEventHandler += delegate { sRides.Clear(); sRideSeeded = false; };  // no Sim of the old world kept past quit
        }

        public class RideStartHook
        {
            public static bool StartHook(Sim rider, Sim horse, StateMachineClient smc)
            {
                bool r = StartOrig(rider, horse, smc);
                if (r) { RideNote(rider); RideNote(horse); }
                return r;
            }
            public static bool StartOrig(Sim rider, Sim horse, StateMachineClient smc) { return false; }  // receives RidingPosture.StartRiding
        }

        public class RideRunHook
        {
            public bool MountHook() { RideNoteRun(this); return MountOrig(); }
            public bool MountOrig() { return false; }  // receives Sim.Mount.Run
            public bool DismountHook() { RideNoteRun(this); return DismountOrig(); }
            public bool DismountOrig() { return false; }  // receives Sim.Dismount.Run
            public bool BeingDismountedHook() { RideNoteRun(this); return BeingDismountedOrig(); }
            public bool BeingDismountedOrig() { return false; }  // receives Sim.BeingDismounted.Run
        }

        static void RideNoteRun(object o)
        {
            try
            {
                Sims3.Gameplay.Interactions.InteractionInstance i = o as Sims3.Gameplay.Interactions.InteractionInstance;
                if (i == null) return;
                RideNote(i.InstanceActor as Sim);
                RideNote(i.Target as Sim);
            }
            catch (Exception) { sRideSeeded = false; }  // never in EA's way; the next wake walks
        }

        static void RideNote(Sim s)
        {
            try
            {
                if (s == null || !ShortcutOn("viewerridehooks") || sRides.Contains(s)) return;
                if (sRides.Count >= kRideSetMax) { sRides.Clear(); sRideSeeded = false; return; }
                sRides.Add(s);
                PatchCounters()[1662]++;
            }
            catch (Exception) { sRideSeeded = false; }
        }

        // Adds the rides to sHeld (after the household): from a LotManager.Actors walk when due, else from sRides.
        static void HeldRides(int[] c, uint now)
        {
            if (sRideFallback || LoopCostCore.RideWalkDue(sRideSeeded, (long)unchecked(now - sRideWalkMs), kRideFloorMs))
            {
                c[1581]++; c[1665]++;
                bool miss = false;
                List<Sim> found = new List<Sim>();
                foreach (Sim s in Sims3.Gameplay.Core.LotManager.Actors)
                {
                    c[1583]++;
                    if (s == null) continue;
                    if (!RideCandidate(s) || s.HasBeenDestroyed) continue;  // the same test and order as the delta walk
                    HoldRide(s);
                    found.Add(s);
                    if (sRideSeeded && !sRides.Contains(s)) miss = true;
                }
                if (miss && !sRideFallback) { c[1666]++; sRideFallback = true; }
                sRides.Clear(); sRides.AddRange(found);
                sRideSeeded = true; sRideWalkMs = now;
            }
            else
            {
                c[1663]++;
                for (int k = sRides.Count - 1; k >= 0; k--)
                {
                    Sim s = sRides[k];
                    c[1664]++;
                    if (s == null || !RideCandidate(s) || s.HasBeenDestroyed) sRides.RemoveAt(k);
                }
                foreach (Sim s in sRides) HoldRide(s);
            }
            if (!ShortcutOn("viewerridehookshadow")) return;
            // diagnostic: the walk-every-wake held table (household, then every ride in Actors) against the one just built
            c[1667]++;
            sRideShadow.Clear(); sRideShadow.AddRange(sHeld);
            try
            {
                sHeld.Clear();
                Household h = Household.ActiveHousehold;
                if (h != null) foreach (Sim s in h.AllActors) HoldSim(s);
                foreach (Sim s in Sims3.Gameplay.Core.LotManager.Actors)
                    if (s != null && RideCandidate(s) && !s.HasBeenDestroyed) HoldRide(s);
                // a set compare below the table cap; at the cap the order picks who is cut, so it must match too
                bool same = sHeld.Count < kHeldMax && sRideShadow.Count < kHeldMax ? LoopCostCore.SameIdSet(sRideShadow, sHeld) : LoopCostCore.SameIds(sRideShadow, sHeld);
                if (!same) c[1668]++;
            }
            catch (Exception) { c[1668]++; }
            finally { sHeld.Clear(); sHeld.AddRange(sRideShadow); }  // the hook set's table is the one sent
        }

        static void HoldHorseUnder(Sim s)
        {
            Sims3.Gameplay.ActorSystems.RidingPosture rp = s.Posture as Sims3.Gameplay.ActorSystems.RidingPosture;
            Sim horse = rp != null ? rp.Mount : null;
            if (horse != null && !horse.HasBeenDestroyed && RideCandidate(horse)) HoldRide(horse);
        }

        static bool RideCandidate(Sim s)
        {
            if (s.Posture is Sims3.Gameplay.ActorSystems.BeingRiddenPosture) return true;
            Sims3.Gameplay.Interactions.InteractionInstance i = s.CurrentInteraction;
            return i is Sim.Mount || i is Sim.Dismount || i is Sim.BeingDismounted;
        }
    }
}
