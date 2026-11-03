using System;
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
    public static partial class Fixes
    {
        const ulong kViewerBits = 0x8200000000000000UL;
        const int kViewerMax = 32;           // ts3native's table size
        const uint kViewerPushTicks = 5;      // a walking Sim moves well under a metre between pushes
        static int sViewerGen;

        internal static void StartViewerPush()
        {
            UtbWorldLoad();  // docs/227 §6: ask ts3native for the unseen table again this load
            int gen = ++sViewerGen;  // a loop left from an earlier load ends at its next wake-up
            Simulator.AddObject(new LoopTask(delegate { ViewerPushLoop(gen); }));
        }

        static void ViewerPushLoop(int gen)
        {
            while (gen == sViewerGen)
            {
                try { if (!PushViewers()) return; } catch (Exception) { return; }
                Simulator.Sleep(kViewerPushTicks);
            }
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
            return true;
        }

        // Ridden horses and their riders, and both sides of a Mount / Dismount / BeingDismounted, held at full rate by
        // ts3native's throttle (docs/143 §8.10): codes 12 BEGIN, 13 id low, 14 id high (appends), 15 END with the count.
        const int kHeldMax = 32;  // ts3native's table size
        static readonly System.Collections.Generic.List<ulong> sHeld = new System.Collections.Generic.List<ulong>();
        static void HoldSim(Sim s) { if (s != null && sHeld.Count < kHeldMax && !sHeld.Contains(s.ObjectId.Value)) sHeld.Add(s.ObjectId.Value); }
        static void PushHeld()
        {
            sHeld.Clear();
            // LotManager.Actors: EA's list of Sims in the world (added on a lot, removed when destroyed). Queries.GetObjects<Sim>()
            // walked every world object every 5 ticks: ~5% of the sim thread on a 14,500-object save (issue #156 sampler).
            foreach (Sim s in Sims3.Gameplay.Core.LotManager.Actors)
            {
                if (s == null || s.HasBeenDestroyed) continue;
                Sims3.Gameplay.ActorSystems.BeingRiddenPosture ridden = s.Posture as Sims3.Gameplay.ActorSystems.BeingRiddenPosture;
                if (ridden != null) { HoldSim(s); HoldSim(ridden.Rider); }
                Sims3.Gameplay.Interactions.InteractionInstance i = s.CurrentInteraction;
                if (i is Sim.Mount || i is Sim.Dismount || i is Sim.BeingDismounted) { HoldSim(s); HoldSim(i.Target as Sim); }
            }
            if (!ViewerSend(12, 0)) return;
            foreach (ulong id in sHeld)
                if (!ViewerSend(13, (uint)id) || !ViewerSend(14, (uint)(id >> 32))) return;  // refused: the old table goes stale
            ViewerSend(15, (uint)sHeld.Count);
        }
    }
}
