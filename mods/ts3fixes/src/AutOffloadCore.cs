using System;
using System.Collections;
using System.Collections.Generic;

namespace Ts3Fixes
{
    // docs/208: game-free compare rule of AutOffload.cs, so tools/selftest/autoffload can run it offline.
    public static class AutOffloadCore
    {
        // Broadcaster candidate lists (Mode R): 0 = same ids, order and distance flags; 1 = same (id, flag) pairs in another
        // order; 2 = anything else. Ids are compared by reference (the Sims themselves).
        public static int SeqClass(object[] w, bool[] wn, int nw, object[] r, bool[] rn, int nr)
        {
            if (nw != nr) return 2;
            bool same = true;
            for (int i = 0; i < nw; i++) same &= w[i] == r[i] && wn[i] == rn[i];
            if (same) return 0;
            for (int i = 0; i < nw; i++)
            {
                int at = -1;
                for (int j = 0; j < nr && at < 0; j++) if (r[j] == w[i]) at = j;
                if (at < 0 || rn[at] != wn[i]) return 2;
            }
            return 1;
        }

        // docs/208 §8.1 due pick: keep the `max` largest keys in descending order (ties keep the earlier one); returns the
        // new count. items moves with keys.
        public static int InsertTop(float[] keys, object[] items, int n, int max, float f, object item)
        {
            int at = n < max ? n++ : max;
            if (at == max) { if (f <= keys[max - 1]) return n; at = max - 1; }
            while (at > 0 && keys[at - 1] < f) { keys[at] = keys[at - 1]; items[at] = items[at - 1]; at--; }
            keys[at] = f; items[at] = item;
            return n;
        }

        // docs/208 §9.2, Camera.GetClosestVisibleLot: where a camera target lies against a lot, in lot space. Cell 0-8 = row
        // (0 beyond the extent's z, 1 within, 2 before the origin's z) x 3 + column (0 before the origin's x, 1 within, 2 beyond
        // the extent's x); 4 = on the lot. pad widens the lot (a houseboat's 10 m).
        public static int LotCell(float tx, float tz, float ox, float oz, float ex, float ez, float pad)
        {
            int col = tx < ox - pad ? 0 : tx > ex + pad ? 2 : 1;
            int row = tz > ez + pad ? 0 : tz < oz - pad ? 2 : 1;
            return row * 3 + col;
        }

        // Squared distance to the lot for that cell: an edge cell uses the delta to that edge, a corner cell the corner's own
        // squared distance (tl, tr, bl, br). Cell 4 = 0.
        public static float LotCellDist(int cell, float dxo, float dzo, float dxe, float dze, float tl, float tr, float bl, float br)
        {
            switch (cell)
            {
                case 0: return tl;
                case 1: return dze * dze;
                case 2: return tr;
                case 3: return dxo * dxo;
                case 5: return dxe * dxe;
                case 6: return bl;
                case 7: return dzo * dzo;
                case 8: return br;
                default: return 0f;
            }
        }

        // One lot of the closest-lot walk: 0 keep the current pick, 1 new pick, 2 new pick and focus lot. On a lot (cell 4) the
        // first one wins unless a houseboat lot comes later; else the nearest wins, ties keep the earlier one. Cell -1 (a lot
        // with no corners) counts as distance d off the lot.
        public static int LotPickStep(int cell, float d, bool houseboat, ref float best)
        {
            if (cell == 4)
            {
                if (best <= 0f && !houseboat) return 0;
                best = 0f;
                return 2;
            }
            if (d >= best) return 0;
            best = d;
            return 1;
        }

        // docs/126 §8 IC dry compare: first slot where w and r differ, skipping slots whose group (slotGroup[i]) aborted on
        // either side (bit g of abMask); slotGroup.Length = no difference.
        public static int IcFirstDiff(long[] w, long[] r, byte[] slotGroup, int abMask)
        {
            int d = 0;
            while (d < slotGroup.Length && (((abMask >> slotGroup[d]) & 1) != 0 || w[d] == r[d])) d++;
            return d;
        }

        // docs/208 §10 autoffunseen: scope of one Sim from its UfWhy code (UnseenFallbackCore.k*; -1 = UfWhy threw).
        // Apply only for 0 (unseen, not household, not selected, not on the camera lot); household and selected Sims are
        // household; not-live, gone and failed reads are unknown (the game's own path, as household); the rest is seen.
        public const int kScopeApply = 0, kScopeHousehold = 1, kScopeSeen = 2, kScopeUnknown = 3;
        public static int Scope(int why)
        {
            if (why == UnseenFallbackCore.kSeen) return kScopeApply;
            if (why == UnseenFallbackCore.kHousehold || why == UnseenFallbackCore.kGoalHousehold) return kScopeHousehold;
            if (why < 0 || why == UnseenFallbackCore.kNotLive || why == UnseenFallbackCore.kGone) return kScopeUnknown;
            return kScopeSeen;
        }

        // the other Sims a scan or broadcast touches: seen non-household Sims are fine for the env scan (only the actor's
        // reactions run), household and unknown are not
        public static int OtherScope(int why)
        {
            int s = Scope(why);
            return s == kScopeSeen ? kScopeApply : s;
        }

        // docs/274 R2 bcposdry: the ground-plane range scan of the per-tick Sim position table. Writes the indices of the
        // entries within rq of (bx, bz) into at, in table order; returns the count (at most at.Length).
        public static int Scan(float[] x, float[] z, int n, float bx, float bz, float rq, int[] at)
        {
            float rq2 = rq * rq;
            int k = 0;
            for (int i = 0; i < n && k < at.Length; i++)
            {
                float dx = x[i] - bx, dz = z[i] - bz;
                if (dx * dx + dz * dz <= rq2) at[k++] = i;
            }
            return k;
        }

        // docs/274 R2: mine (the table answer) against the game's set. 0 = same members in the same order, 1 = same members
        // in another order, 2 = members differ; missing = in the game's set only, extra = in mine only.
        public static int SetDiff(object[] mine, int nm, object[] game, int ng, out int missing, out int extra)
        {
            missing = extra = 0;
            for (int i = 0; i < ng; i++) if (Array.IndexOf(mine, game[i], 0, nm) < 0) missing++;
            for (int i = 0; i < nm; i++) if (Array.IndexOf(game, mine[i], 0, ng) < 0) extra++;
            if (missing + extra > 0 || nm != ng) return 2;
            for (int i = 0; i < nm; i++) if (mine[i] != game[i]) return 1;
            return 0;
        }
    }

    // docs/274 R1 envlistmemo (dry): the validity rule of the env list memo, game-free. A lot is any object (the game's Lot);
    // a list is the game's own result (List<T> or T[]), stored as a copy. An entry is valid while its lot's generation and,
    // for a room list, its room's generation are unchanged. Check classifies each call against the stored entry and then
    // stores the game's result, so the counts say what an apply would have done: hit (valid and equal), hit_wrong (valid but
    // the game's list differs: the invalidation missed a change), miss_same / miss_diff (invalidated; the list was or was not
    // the same, i.e. the oracle's repeat share).
    public sealed class ListMemo
    {
        public const int kFirst = 0, kHit = 1, kHitWrong = 2, kMissSame = 3, kMissDiff = 4;
        const int kMaxLots = 1024, kMaxAgain = 4096;
        sealed class LotGen { public int Gen, Ev; public readonly Dictionary<int, int> Room = new Dictionary<int, int>(); public readonly Dictionary<int, Entry> Memo = new Dictionary<int, Entry>(); }
        sealed class Entry { public object[] Items; public int Gen, RoomGen, Ev; public long Tick; public ulong Print; }
        struct Again { public object Lot; public int Room; public long Tick; }
        readonly Dictionary<object, LotGen> mLots = new Dictionary<object, LotGen>();
        readonly List<Again> mAgain = new List<Again>();
        public int Lots { get { return mLots.Count; } }
        public object[] WrongOld;  // set by Check on kHitWrong
        public int WrongEv; public long WrongAge;  // kHitWrong: Bump calls on the lot / ticks since the list was last seen equal (docs/274 §8.6)

        public void Clear() { mLots.Clear(); mAgain.Clear(); }

        // a membership change on the lot (room -1) or of one room: bump now, and once more at the first call of a later tick
        // (the native lot list may change after the managed event within the same tick, docs/274 §5.1 [inferred])
        public void Bump(object lot, int room, long tick)
        {
            if (lot == null) return;
            LotGen lg;
            if (mLots.TryGetValue(lot, out lg)) lg.Ev++;  // events only, not the next-tick repeats
            BumpNow(lot, room);
            if (mAgain.Count >= kMaxAgain) { Clear(); return; }
            Again a; a.Lot = lot; a.Room = room; a.Tick = tick;
            mAgain.Add(a);
        }

        void BumpNow(object lot, int room)
        {
            LotGen g;
            if (!mLots.TryGetValue(lot, out g)) return;  // no entries yet: a later Again still covers the tick
            if (room < 0) { g.Gen++; return; }
            int r;
            g.Room.TryGetValue(room, out r);
            g.Room[room] = r + 1;
        }

        public void Tick(long tick)
        {
            if (mAgain.Count == 0) return;
            int keep = 0;
            for (int i = 0; i < mAgain.Count; i++)
            {
                if (mAgain[i].Tick < tick) BumpNow(mAgain[i].Lot, mAgain[i].Room);
                else mAgain[keep++] = mAgain[i];
            }
            mAgain.RemoveRange(keep, mAgain.Count - keep);
        }

        // kind: which list (the caller's numbering, 0-3); room -1 = a lot-wide list (lot generation only)
        public int Check(object lot, int room, int kind, IList now, long tick)
        {
            Tick(tick);
            LotGen g;
            if (!mLots.TryGetValue(lot, out g))
            {
                if (mLots.Count >= kMaxLots) Clear();
                mLots[lot] = g = new LotGen();
            }
            int rg = 0;
            if (room >= 0) g.Room.TryGetValue(room, out rg);
            int key = room * 4 + kind;
            Entry e;
            int cls;
            if (!g.Memo.TryGetValue(key, out e)) { g.Memo[key] = e = new Entry(); cls = kFirst; }
            else
            {
                bool same = Same(e.Items, now), valid = e.Gen == g.Gen && e.RoomGen == rg;
                cls = valid ? (same ? kHit : kHitWrong) : (same ? kMissSame : kMissDiff);
                if (same) { e.Gen = g.Gen; e.RoomGen = rg; e.Ev = g.Ev; e.Tick = tick; return cls; }
                if (cls == kHitWrong) { WrongOld = e.Items; WrongEv = g.Ev - e.Ev; WrongAge = tick - e.Tick; }  // the memo's list, so the caller can name the missed change
            }
            object[] items = new object[now.Count];
            now.CopyTo(items, 0);
            e.Items = items; e.Gen = g.Gen; e.RoomGen = rg; e.Ev = g.Ev; e.Tick = tick; e.Print = 0;  // no print: never an exact hit
            return cls;
        }

        // docs/274 R1 apply (envlistmemoapply): the memo's list when it is still valid (no bump since it was stored or last
        // seen equal), else null; the caller then lists live and stores through Check
        public object[] Get(object lot, int room, int kind, long tick)
        {
            Tick(tick);
            LotGen g;
            Entry e;
            if (!mLots.TryGetValue(lot, out g) || !g.Memo.TryGetValue(room * 4 + kind, out e) || e.Items == null) return null;
            int rg = 0;
            if (room >= 0) g.Room.TryGetValue(room, out rg);
            return e.Gen == g.Gen && e.RoomGen == rg ? e.Items : null;
        }

        // docs/274 §11 exact reuse (envlistmemoexact): as Get, and the lot's native order print (the ids the game's lot walk
        // would push now, in order; 0 = none) must equal the print stored with the list. The generations cover the managed
        // inputs (room ids, lot membership events); the print covers the native order, whatever changed it.
        public object[] GetExact(object lot, int room, int kind, long tick, ulong print)
        {
            Tick(tick);
            LotGen g;
            Entry e;
            if (print == 0 || !mLots.TryGetValue(lot, out g) || !g.Memo.TryGetValue(room * 4 + kind, out e) || e.Items == null || e.Print != print) return null;
            int rg = 0;
            if (room >= 0) g.Room.TryGetValue(room, out rg);
            return e.Gen == g.Gen && e.RoomGen == rg ? e.Items : null;
        }

        // the game's own list, just built, with the print read before it was built
        public void Put(object lot, int room, int kind, IList now, long tick, ulong print)
        {
            Tick(tick);
            LotGen g;
            if (!mLots.TryGetValue(lot, out g))
            {
                if (mLots.Count >= kMaxLots) Clear();
                mLots[lot] = g = new LotGen();
            }
            int rg = 0;
            if (room >= 0) g.Room.TryGetValue(room, out rg);
            int key = room * 4 + kind;
            Entry e;
            if (!g.Memo.TryGetValue(key, out e)) g.Memo[key] = e = new Entry();
            object[] items = new object[now.Count];
            now.CopyTo(items, 0);
            e.Items = items; e.Gen = g.Gen; e.RoomGen = rg; e.Ev = g.Ev; e.Tick = tick; e.Print = print;
        }

        // same objects, same count, other order (a reorder, docs/274 §8.5-§8.6)
        public static bool SameSet(object[] a, IList b)
        {
            if (a == null || b == null || a.Length != b.Count) return false;
            foreach (object o in a) if (!b.Contains(o)) return false;
            foreach (object o in b) if (Array.IndexOf(a, o) < 0) return false;  // shortcut: set, not multiset; the game's lists hold each object once
            return true;
        }

        public static bool Same(object[] a, IList b)
        {
            if (a == null || b == null || a.Length != b.Count) return false;
            for (int i = 0; i < a.Length; i++) if (!ReferenceEquals(a[i], b[i])) return false;
            return true;
        }
    }
}
