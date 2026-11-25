using System;
using System.Collections.Generic;

namespace Ts3Fixes
{
    // Game-independent decisions of docs/330 (our own loop cost): when deadrefsweep's phase groups run, when the viewer
    // table is re-sent, when LotManager.Actors is walked for held riders. Self-check: tools/selftest/loopcost.
    public static class LoopCostCore
    {
        // A deadrefsweep phase group runs on a pass when an event that can make its entries dead fired since its last start,
        // its last run removed something (a trickle keeps it hourly), or capHours sim hours passed since its last finished run.
        public static bool GroupDue(bool dirty, bool foundLast, float hoursSince, float capHours)
        {
            return dirty || foundLast || hoursSince >= capHours;
        }

        // The viewer table (x, y, z per viewer, in order) is sent again when the viewer count changed, any viewer moved more
        // than moveM from where it was last sent, or beatMs passed since the last send (ts3native drops a table older than
        // 2000 ms). sent == null or msSince < 0 = never sent.
        public static bool ViewersDue(List<float> sent, List<float> cur, float moveM, long msSince, long beatMs)
        {
            if (sent == null || msSince < 0 || msSince >= beatMs || sent.Count != cur.Count) return true;
            float m2 = moveM * moveM;
            for (int i = 0; i + 2 < cur.Count; i += 3)
            {
                float dx = cur[i] - sent[i], dy = cur[i + 1] - sent[i + 1], dz = cur[i + 2] - sent[i + 2];
                if (dx * dx + dy * dy + dz * dz > m2) return true;
            }
            return false;
        }

        // LotManager.Actors is walked for rides on every wake while the last walk (or the household) had a candidate, else
        // on every sparse-th wake: a ride that starts with none in progress is found within sparse wakes.
        public static bool HeldWalkDue(bool sawAny, int wake, int sparse)
        {
            return sawAny || sparse <= 1 || wake % sparse == 0;
        }

        // docs/334 (LC-1 a, deadrefexact): one pass over the world snapshot fills every typed list the separate filters
        // filled. An item goes to each list whose bit its mask has, in snapshot order, so each list holds the same items in
        // the same order as its own filter pass did.
        public static void PartitionAdd(object o, int mask, List<object>[] outs)
        {
            for (int i = 0; mask != 0; i++, mask >>= 1) if ((mask & 1) != 0) outs[i].Add(o);
        }

        // the same items (by reference) in the same order
        public static bool SameItems(List<object> a, List<object> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++) if (!ReferenceEquals(a[i], b[i])) return false;
            return true;
        }

        // docs/334 (LC-1 b, deadrefexact): one object's ObjectDisposed handlers of two kinds from one read of the field.
        // Before, the handler body and the cooking body each read the field and built their own invocation list. Here the
        // handler part (gone) runs first and sets the field once if it dropped anything; only then is the field read again
        // for the cooking part (stale), else that part walks the same list. Each part keeps its own try, as each body had: a
        // throw counts errSlot and the cooking part reads afresh. The cooking part re-reads the field at each removal, as
        // CookingBody did. gone == null: no handler part; stale == null: no cooking part (switch off).
        public delegate Delegate EvGet(object holder);
        public delegate void EvSet(object holder, Delegate v);
        public delegate bool EvTest(Delegate d, object holder);
        public delegate void EvDropped(Delegate d, object holder);

        public static void SharedDrop(object h, EvGet get, EvSet set, EvTest gone, EvTest stale, EvDropped dropped, int[] c,
            int goneSlot, int staleSlot, int errSlot, int sharedSlot, int rereadSlot)
        {
            Delegate[] list = null;
            bool have = false; // list (null = no handler) is the field's current value
            if (gone != null)
            {
                try
                {
                    Delegate ev = get(h);
                    have = true;
                    if (ev != null)
                    {
                        list = ev.GetInvocationList();
                        bool changed = false;
                        foreach (Delegate d in list) { if (!gone(d, h)) continue; ev = Delegate.Remove(ev, d); c[goneSlot]++; changed = true; }
                        if (changed) { set(h, ev); have = false; c[rereadSlot]++; }
                    }
                }
                catch (Exception) { c[errSlot]++; have = false; }
            }
            if (stale == null) return;
            try
            {
                if (have) { if (list != null) c[sharedSlot]++; }
                else { Delegate ev = get(h); list = ev != null ? ev.GetInvocationList() : null; }
                if (list == null) return;
                foreach (Delegate d in list)
                {
                    if (!stale(d, h)) continue;
                    set(h, Delegate.Remove(get(h), d));
                    dropped(d, h);
                    c[staleSlot]++;
                }
            }
            catch (Exception) { c[errSlot]++; }
        }

        // Same ids in the same order (the native table is replaced whole).
        public static bool SameIds(List<ulong> a, List<ulong> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++) if (a[i] != b[i]) return false;
            return true;
        }
    }
}
