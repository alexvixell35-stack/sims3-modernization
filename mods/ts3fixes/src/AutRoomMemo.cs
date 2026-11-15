using System;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.Core;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // docs/97 §7 AM-1 H1-H3 + H5 (H4 shipped as venuememo). Switches autroommemo (+ autroommemoverify) and autslicetime,
    // tunings kAutonomyRoomMemo / kAutonomySliceTime, all off. Exact for every Sim (household and selected included): the
    // memo answers only what the game's own per-Sim room cache would answer at that moment, so no scope gate.
    //  H1 Autonomy.CalculateScoreAndAddToCandidates (per pair): opens a segment per (Autonomy, sim tick, script task) and
    //     marks "inside a pair"; the room memo lives only inside pairs of the open segment (PairScope.Inside also checks
    //     tick and task on every lookup, so a yield never carries it to another task or tick).
    //  H2 Sim.IsAllowedInRoom(ulong, int): [AutShadow]'s AutRoomHook already owns this swap; its sim-thread branch asks
    //     ArmLookup / ArmStore. Key (Sim, lot id, room id) + the Sim's mLotCurrent at store time (the game reads it).
    //     The game's answer is a read of mAllowedRooms[lot] while the lot's portal cache version matches; the memo is
    //     dropped (Bump) whenever either can change: H3 below (every write of mAllowedRooms goes through the private
    //     RecalculateAllowedRooms), Lot.RefreshObjectCacheUsers and Lot.RefreshObjectCaches (the only non-constructor
    //     writers of a cache version), and every new segment. Left out [inferred, verified by grep of the 1.67 code]:
    //     Lot.CacheRoomObjects<T> (generic, lot setup only, never inside an autonomy pair).
    //  H3 Sim.RecalculateAllowedRooms(ulong, int) (private): counts floods inside a pair, drops the memo, runs the game's.
    //  H5 segment scoring time (segment open to last pair exit, one SimIFace StopWatch) into buckets.
    // Off the sim thread every hook runs the game's method and writes nothing (docs/128 G-M3).
    // Counters: [1024] pairs under H1, [1025] segments opened, [1026] room memo hits, [1027] misses, [1028] verify
    // mismatches, [1029] room calls outside a pair (memo on), [1030] floods inside a pair, [1031] memo drops by H3 or a
    // cache refresh inside a pair, [1032-1036] segment time < 1, 1-2, 2-5, 5-10, >= 10 ms, [1037] max segment us.
    public static partial class Fixes
    {
        static bool sArmPairReady, sArmRoomReady;
        static readonly PairScope sArmScope = new PairScope();
        static readonly RoomMemo sArmRooms = new RoomMemo();
        static bool sArmMemoOn, sArmSliceOn, sArmTimed;
        static int sArmSwitchCalls;
        static StopWatch sArmWatch;
        static uint sArmStartUs, sArmLastExitUs;

        // after ApplyAutShadow: the room memo rides on its IsAllowedInRoom swap
        static void ApplyAutRoomMemo()
        {
            MethodInfo allowed = typeof(Sim).GetMethod("IsAllowedInRoom", BindingFlags.Instance | BindingFlags.Public, null, new Type[] { typeof(ulong), typeof(int) }, null);
            Type owner;
            sArmRoomReady = allowed != null && sPatched.TryGetValue(allowed.MethodHandle.Value, out owner) && owner == typeof(AutRoomHook);
            if (!sArmRoomReady) AppliedLog().Add("autroommemo: Sim.IsAllowedInRoom is not AutShadow's swap, room memo off");
            bool drops = Patch(typeof(Sim), "RecalculateAllowedRooms", new Type[] { typeof(ulong), typeof(int) }, typeof(ArmFloodHook), "FloodHook", "FloodOrig")
                & Patch(typeof(Lot), "RefreshObjectCacheUsers", Type.EmptyTypes, typeof(ArmLotCacheHook), "UsersHook", "UsersOrig")
                & Patch(typeof(Lot), "RefreshObjectCaches", Type.EmptyTypes, typeof(ArmLotCacheHook), "CachesHook", "CachesOrig");
            if (!drops && sArmRoomReady) { sArmRoomReady = false; AppliedLog().Add("autroommemo: a memo drop hook failed, room memo off"); }
            sArmPairReady = Patch(typeof(Sims3.Gameplay.Autonomy.Autonomy), "CalculateScoreAndAddToCandidates", new Type[] { typeof(InteractionObjectPair) }, typeof(ArmPairHook), "PairHook", "PairOrig");
            World.OnWorldQuitEventHandler += delegate { sArmRooms.Wipe(); sArmScope.Reset(); sArmTimed = false; };
        }

        // switches re-read every 256 pairs: no string lookup per pair while both are off (the default)
        static void ArmSwitches()
        {
            if ((sArmSwitchCalls++ & 255) != 0) return;
            sArmMemoOn = sArmRoomReady && ShortcutOn("autroommemo");
            sArmSliceOn = ShortcutOn("autslicetime");
        }

        static void ArmOpen(int[] c)
        {
            c[1025]++;
            sArmRooms.Bump();
            if (sArmTimed) SliceHistogram.Add(c, 1032, unchecked(sArmLastExitUs - sArmStartUs));
            sArmTimed = false;
            if (!sArmSliceOn) return;
            if (sArmWatch == null) { sArmWatch = StopWatch.Create(StopWatch.TickStyles.Microseconds); if (sArmWatch != null) sArmWatch.Start(); }
            if (sArmWatch == null) return; // the engine has none
            sArmStartUs = sArmLastExitUs = sArmWatch.GetElapsedTime();
            sArmTimed = true;
        }

        static void ArmDrop()
        {
            sArmRooms.Bump();
            if (sArmScope.Depth > 0 && sArmMemoOn) PatchCounters()[1031]++;
        }

        // AutRoomHook (sim thread, outside the AutShadow reference): -1 not in scope (game's method, keep nothing),
        // 0 miss (game's method, then ArmStore), 1 hit (ans).
        internal static int ArmLookup(Sim s, ulong lotId, int roomId, out bool ans)
        {
            ans = false;
            if (!sArmMemoOn) return -1;
            int[] c = PatchCounters();
            if (sArmScope.Depth == 0 || !sArmScope.Inside(Simulator.TicksElapsed(), Simulator.CurrentTask.Value)) { c[1029]++; return -1; }
            Lot cur = s.InternalLotCurrent;
            if (cur == null) return -1; // the game throws; nothing kept
            if (sArmRooms.TryGet(s, s.ObjectId.Value, cur, lotId, roomId, out ans)) { c[1026]++; return 1; }
            c[1027]++;
            return 0;
        }

        internal static void ArmStore(Sim s, ulong lotId, int roomId, bool ans)
        {
            Lot cur = s.InternalLotCurrent;
            if (cur != null) sArmRooms.Store(s, s.ObjectId.Value, cur, lotId, roomId, ans);
        }

        public class ArmPairHook
        {
            public void PairHook(InteractionObjectPair iop)
            {
                if (!sArmPairReady || OnWorker()) { PairOrig(iop); return; }
                ArmSwitches();
                if (!sArmMemoOn && !sArmSliceOn) { PairOrig(iop); return; }
                int[] c = PatchCounters();
                c[1024]++;
                // a new segment starts at depth 0 (a pair whose task died inside a yield never ran its finally)
                if (sArmScope.Renew(this, Simulator.TicksElapsed(), Simulator.CurrentTask.Value)) { ArmOpen(c); sArmScope.Depth = 0; }
                int before = sArmScope.Depth;
                sArmScope.Depth = before + 1;
                try { PairOrig(iop); }
                finally
                {
                    sArmScope.Depth = before;
                    if (sArmTimed) sArmLastExitUs = sArmWatch.GetElapsedTime();
                }
            }
            public void PairOrig(InteractionObjectPair iop) { } // receives Autonomy.CalculateScoreAndAddToCandidates's original header
        }

        public class ArmFloodHook
        {
            public void FloodHook(ulong lotId, int initialRoomId)
            {
                if (!OnWorker())
                {
                    ArmDrop();
                    if (sArmScope.Depth > 0 && sArmMemoOn) PatchCounters()[1030]++;
                }
                try { FloodOrig(lotId, initialRoomId); }
                finally { if (!OnWorker()) sArmRooms.Bump(); } // a lookup during the flood read a half-built table
            }
            public void FloodOrig(ulong lotId, int initialRoomId) { } // receives Sim.RecalculateAllowedRooms(ulong, int)'s original header
        }

        public class ArmLotCacheHook
        {
            public void UsersHook() { if (OnWorker()) { UsersOrig(); return; } ArmDrop(); try { UsersOrig(); } finally { sArmRooms.Bump(); } }
            public void UsersOrig() { } // receives Lot.RefreshObjectCacheUsers's original header
            public void CachesHook() { if (OnWorker()) { CachesOrig(); return; } ArmDrop(); try { CachesOrig(); } finally { sArmRooms.Bump(); } }
            public void CachesOrig() { } // receives Lot.RefreshObjectCaches's original header
        }
    }
}
