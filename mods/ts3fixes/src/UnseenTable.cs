using System;
using System.Runtime.CompilerServices;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Utilities;

namespace Ts3Fixes
{
    // ts3native's [UnseenTable] internal call (docs/227 §6, unseentable.cpp). ts3native registers it at its first sim pump and
    // answers viewer-channel code 25 with 1 from then on; ts3fixes calls Fetch only after that answer (no MissingMethodException).
    public static class UnseenTableNative
    {
        // the table's rows sorted by object id: ids[i] and bits[i] (unseentable.cpp UTB_*); -1 = no table (switch off)
        [MethodImpl(MethodImplOptions.InternalCall)] public static extern int Fetch(ulong[] ids, int[] bits);
        // one object's row bits without a copy (docs/227 §9, ts3native 0.36.159+); -1 = no table, -2 = no row
        [MethodImpl(MethodImplOptions.InternalCall)] public static extern int Bits(ulong id);
        // unseenfb.cpp's camera query in one call (q = x, y, z, radius): the answer of viewer codes 3-6 (1 = unseen for sure)
        [MethodImpl(MethodImplOptions.InternalCall)] public static extern int PosUnseen(float[] q);
    }

    // The central per-tick unseen table, script side (docs/227 §6, §9): one Bits IC per ask (default; "unseentablecopy" on or
    // an older ts3native = the §6 copy: one Fetch of every row per sim tick + a binary search here), and the Sim checks (SimUnseen,
    // TownieUnseen, UfWhy, DisposeSpreadUnseen) read the Sim's row instead of World.IsObjectOnScreen + FarFromViewers (+ the
    // UfCamAsk camera query). Each check keeps its own rules (household, posture, followers, lots, tomb rooms). No table (no
    // ts3native, [UnseenTable] off, --ab unseentable off), no row or a stale household table = the old rule. Position checks
    // (FarFromViewers of a lot or a door, UfPosUnseen of a goal) keep their point test.
    // When ts3native's Check is on (code 26), every table answer is also compared with the old answer.
    // Counters: [744] ticks with a table, [745] answers from it, [746] no row / stale (old rule), [747] compared: table unseen,
    // old seen (unsafe), [748] table seen, old unseen, [749] compared.
    public static partial class Fixes
    {
        const int kUtbOnScreen = 2, kUtbReach = 4, kUtbViewer = 8, kUtbFresh = 16, kUtbValid = 128;
        const int kUtbTicks = 744, kUtbAnswers = 745, kUtbNoRow = 746, kUtbMisUnsafe = 747, kUtbMisSafe = 748, kUtbCompared = 749;
        static readonly ulong[] sUtbIds = new ulong[1024];  // ts3native's row cap
        static readonly int[] sUtbBits = new int[1024];
        static int sUtbN = -1;        // rows this tick; -1 = no table
        static long sUtbTick = long.MinValue;
        static int sUtbState;         // 0 = not asked since the world load, 1 = ICs ready, -1 = none this load
        static bool sUtbCheck;

        internal static void UtbWorldLoad() { sUtbState = 0; sUtbTick = long.MinValue; sUtbN = -1; sUtbPosIc = 0; sUtbLookupIc = 0; sUfLotGeo.Clear(); }

        // ts3native's ICs are registered (code 25), asked once per world load
        static bool UtbIcReady()
        {
            if (sUtbState == 0)
            {
                sUtbState = ViewerSend(25, 0) ? 1 : -1;
                sUtbCheck = sUtbState == 1 && ViewerSend(26, 0);
            }
            return sUtbState == 1;
        }

        // UfPosUnseen's camera query: one PosUnseen IC instead of four UfCamAsk (codes 3-6), same answer. 0 / 1, or -1 = no IC
        // (no [UnseenTable], or an older ts3native that registers Fetch only: then the old four calls for the rest of the load)
        static readonly float[] sUtbQ = new float[4];
        static int sUtbPosIc;  // 0 = untried this load, 1 = works, -1 = missing
        static int UtbPosUnseen(float x, float y, float z, float r)
        {
            if (sUtbPosIc < 0 || !UtbIcReady()) return -1;
            sUtbQ[0] = x; sUtbQ[1] = y; sUtbQ[2] = z; sUtbQ[3] = r;
            try { int a = UtbPosIcCall(sUtbQ); sUtbPosIc = 1; return a != 0 ? 1 : 0; }
            catch (Exception) { sUtbPosIc = -1; return -1; }
        }
        // own method: a missing IC throws when this one is compiled, so the caller can catch it
        [MethodImpl(MethodImplOptions.NoInlining)] static int UtbPosIcCall(float[] q) { return UnseenTableNative.PosUnseen(q); }

        // switch for the per-tick answer caches that can differ from the old code within one tick (UfPosUnseen, UfGoalUnseen:
        // ts3native's camera may refresh mid-tick; UfLotSeen's lot shapes); read once per tick. "unseenposcache" off = old code
        static long sUpcTick = long.MinValue;
        static bool sUpcOn;
        static bool UpcOn(long now)
        {
            if (now != sUpcTick) { sUpcTick = now; sUpcOn = ShortcutOn("unseenposcache"); }
            return sUpcOn;
        }

        // the copy path ("unseentablecopy" on, docs/227 §6, the A/B baseline): every listed row copied once per tick
        static bool UtbFetch()
        {
            long t = UcBegin(kUcUtFetch, 0);  // docs/227 §9 probe
            sUtbN = -1;
            try { sUtbN = UnseenTableNative.Fetch(sUtbIds, sUtbBits); }
            catch (Exception) { sUtbState = -1; }
            UcEnd(kUcUtFetch, t);
            return sUtbN >= 0;
        }

        // the lookup path (default, docs/227 §9): one Bits IC per ask, ts3native binary-searches its listed rows (no copy).
        // 1 = works, -1 = missing (older ts3native: the copy path for the rest of the load), 0 = untried this load
        static int sUtbLookupIc;
        static bool sUtbCopy, sUtbTicked;
        static int UtbLookup(ulong id)
        {
            if (sUtbLookupIc < 0) return -3;
            try { int b = UtbBitsIcCall(id); sUtbLookupIc = 1; return b; }
            catch (Exception) { sUtbLookupIc = -1; return -3; }
        }
        // own method: a missing IC throws when this one is compiled, so the caller can catch it
        [MethodImpl(MethodImplOptions.NoInlining)] static int UtbBitsIcCall(ulong id) { return UnseenTableNative.Bits(id); }

        // per tick: the copy switch and (copy path) the fetch; false = no table this tick
        static bool UtbTick()
        {
            long now = SimClock.CurrentTicks;
            if (now == sUtbTick) return sUtbN >= 0;
            sUtbTick = now;
            sUtbN = -1;
            if (!UtbIcReady()) return false;
            sUtbCopy = sUtbLookupIc < 0 || ShortcutOn("unseentablecopy");
            if (sUtbCopy && !UtbFetch()) return false;
            sUtbTicked = sUtbCopy;
            if (sUtbCopy) PatchCounters()[kUtbTicks]++;
            else sUtbN = 0;  // the lookup path: table on until a Bits answer says none (counted at the first answer)
            return true;
        }

        // the object's row bits, or -1: no table, no row, or the household part unknown (stale viewer push)
        static int UtbBits(GameObject o)
        {
            if (o == null || !UtbTick()) return -1;
            long t = UcBegin(kUcUtLookup, 0);  // docs/227 §9 probe
            int b;
            if (sUtbCopy)
            {
                int i = Array.BinarySearch<ulong>(sUtbIds, 0, sUtbN, o.ObjectId.Value);
                b = i < 0 ? -2 : sUtbBits[i];
            }
            else
            {
                b = UtbLookup(o.ObjectId.Value);
                if (b == -3) { sUtbTick = long.MinValue; UcEnd(kUcUtLookup, t); return UtbBits(o); }  // no Bits IC: copy path now
                if (b == -1) sUtbN = -1;  // no table (mode 0 or another thread): the rest of this tick too
                else if (!sUtbTicked) { sUtbTicked = true; PatchCounters()[kUtbTicks]++; }
            }
            UcEnd(kUcUtLookup, t);
            if (b == -1) return -1;
            int[] c = PatchCounters();
            if (b < 0 || (b & (kUtbValid | kUtbFresh)) != (kUtbValid | kUtbFresh)) { c[kUtbNoRow]++; return -1; }
            c[kUtbAnswers]++;
            return b;
        }

        // FarFromViewers(position) && !IsObjectOnScreen, from the row
        static bool UtbUnseen(int b) { return (b & (kUtbOnScreen | kUtbReach | kUtbViewer)) == 0; }

        // Check on: count a different old answer (unseen = true); returns the table's answer
        static bool UtbCompare(bool table, bool old)
        {
            int[] c = PatchCounters();
            c[kUtbCompared]++;
            if (table != old) c[table ? kUtbMisUnsafe : kUtbMisSafe]++;
            return table;
        }
    }

    // docs/227: per-tick answers of a position test, direct-mapped by the exact float bits (a hit = the same x, y, z, r this
    // tick; another point in the slot is a miss and overwrites it). Entries carry their tick, so a new tick empties it.
    sealed class UcPosCache
    {
        const int kN = 256;
        readonly long[] mTick = new long[kN];
        readonly uint[] mKey = new uint[kN * 4];
        readonly bool[] mVal = new bool[kN];
        int mAt;

        public UcPosCache() { for (int i = 0; i < kN; i++) mTick[i] = long.MinValue; }

        public bool Get(long now, uint x, uint y, uint z, uint r, out bool v)
        {
            int i = mAt = (int)(unchecked(x * 73856093u ^ y * 19349663u ^ z * 83492791u ^ r * 2654435761u) >> 24);
            int k = i * 4;
            v = mVal[i];
            return mTick[i] == now && mKey[k] == x && mKey[k + 1] == y && mKey[k + 2] == z && mKey[k + 3] == r;
        }

        // right after a Get miss with the same key
        public void Put(long now, uint x, uint y, uint z, uint r, bool v)
        {
            int i = mAt, k = i * 4;
            mTick[i] = now; mKey[k] = x; mKey[k + 1] = y; mKey[k + 2] = z; mKey[k + 3] = r; mVal[i] = v;
        }
    }
}
