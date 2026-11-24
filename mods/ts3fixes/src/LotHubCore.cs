using System;
using System.Collections.Generic;

namespace Ts3Fixes
{
    // Game-independent part of the managed lot hub (LotHub.cs, docs/321 §9, CP-2), checked offline by tools/selftest/lothub.
    // Consumers (CP-4, each behind its own default-off switch) subscribe; the hub only dispatches, in subscribe order.
    // A delegate subscribed twice is called once. A throwing consumer is counted and skipped; the others still run.
    public static class LotHubCore
    {
        public const int Overflow = 0, Add = 1, Remove = 2;  // Overflow: events were dropped, treat every lot as changed (lotLow 0)

        // kind Add / Remove: lotLow = the low dword of the lot's id (ts3native [lot+0x48]; == (uint)Lot.LotId inferred, docs/51)
        public delegate void LotFn(int kind, uint lotLow);
        public delegate void WorldFn(bool load);  // true: world load finished; false: world quit (menu, travel, another save, exit)

        // The native queue (UserDiag op 49) as the poll sees it; the selftest passes a fake.
        public delegate bool Pop();
        public delegate bool Ask();
        public delegate bool Bit(int bit);

        static readonly List<LotFn> sLot = new List<LotFn>();
        static readonly List<WorldFn> sWorld = new List<WorldFn>();
        public static int WorldGen, LotEvents, Faults, ModeReturns, Rearms;

        public static bool HasLotSubscribers { get { return sLot.Count > 0; } }
        public static int Subscribers { get { return sLot.Count + sWorld.Count; } }  // the leak check: flat across world loads

        // No leak across worlds (docs/321 §9.3): the lists live for the session and only hold distinct delegates (equal
        // method + target = one row, so a consumer that subscribes again after every world load keeps one row), at most
        // kMax each; nothing per lot is kept here. A consumer whose switch goes off unsubscribes.
        public const int kMax = 16;
        public static bool SubscribeLot(LotFn f) { if (f == null || sLot.Count >= kMax || sLot.Contains(f)) return false; sLot.Add(f); return true; }
        public static bool SubscribeWorld(WorldFn f) { if (f == null || sWorld.Count >= kMax || sWorld.Contains(f)) return false; sWorld.Add(f); return true; }
        public static bool UnsubscribeLot(LotFn f) { return sLot.Remove(f); }
        public static bool UnsubscribeWorld(WorldFn f) { return sWorld.Remove(f); }

        // Once per world event (Instantiator's handlers call it once each). Lot events still queued natively are dropped there.
        public static void World(bool load)
        {
            WorldGen++;
            foreach (WorldFn f in sWorld.ToArray()) { try { f(load); } catch (Exception) { Faults++; } }
        }

        // One sim frame: drain the queue, one dispatch per event in arrival order. Returns the events dispatched.
        // shortcut: at most max events per frame (the rest wait a frame); the native queue holds 64 and overflows to one event.
        public static int Poll(Pop pop, Ask isOverflow, Ask isRemove, Bit idBit, int max)
        {
            int n = 0;
            while (n < max && pop())
            {
                int kind;
                uint id = 0;
                if (isOverflow()) kind = Overflow;
                else
                {
                    kind = isRemove() ? Remove : Add;
                    for (int b = 0; b < 32; b++) if (idBit(b)) id |= 1u << b;
                }
                n++;
                LotEvents++;
                foreach (LotFn f in sLot.ToArray()) { try { f(kind, id); } catch (Exception) { Faults++; } }
            }
            return n;
        }

        // Poll task lifetime (docs/321 §9): it ends only when the world goes (quit / reload / main menu), it is replaced
        // (current false) or the last lot subscriber leaves. Outside live mode (Edit Town, CAS, Build / Buy, any substate) it
        // waits without popping, so the native queue keeps the events (overflow beyond 64) and the first live frame drains them.
        public const int StepEnd = 0, StepPoll = 1, StepWait = 2;
        public static int TaskStep(bool current, bool world, bool live)
        {
            if (!current || !world || !HasLotSubscribers) return StepEnd;
            return live ? StepPoll : StepWait;
        }
        // Arm a task when none runs, or when the armed one is gone (taskAlive false: a mode change destroyed its Simulator object).
        public static bool ShouldArm(bool world, bool native, bool taskOn, bool taskAlive)
        {
            return world && native && HasLotSubscribers && (!taskOn || !taskAlive);
        }

        public static void ResetForTest() { sLot.Clear(); sWorld.Clear(); WorldGen = LotEvents = Faults = ModeReturns = Rearms = 0; }
    }
}
