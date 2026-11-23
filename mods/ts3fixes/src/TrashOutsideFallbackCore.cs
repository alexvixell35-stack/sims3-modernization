using System.Collections.Generic;

namespace Ts3Fixes
{
    // Game-independent rule of trashoutsidefallback (TrashOutsideFallback.cs, docs/646 §2). Self-check:
    // tools/selftest/trashoutsidefallback. A lot (lot id) is remembered for `span` sim ticks after an NPC's forced outdoor
    // disposal (TrashPile.DisposeOutside -> PutHeldTrashInCan(forceOutdoor: true)) failed with no path to the outdoor can.
    // While it is remembered, a non-exempt NPC's DisposeOutside on that lot runs EA's non-forced disposal (indoor can, chute,
    // recycle bin) instead, or puts the pile down as EA does on failure when the lot has nothing else to take it; and NPC
    // autonomy does not pick EmptyTrash on an indoor can there (the pick that pushes DisposeOutside). Exempt Sims always get EA.
    public sealed class TrashOutsideFallbackCore
    {
        public const int kMax = 128;

        // Disposal decisions
        public const int kEa = 0, kExempt = 1, kFallback = 2, kPutDown = 3;

        readonly Dictionary<ulong, long> mFrom = new Dictionary<ulong, long>();  // lot -> sim tick of the failure

        public int Count { get { return mFrom.Count; } }
        public void Clear() { mFrom.Clear(); }

        // remembered: recorded at most span ticks ago (a clock that went back, another save, forgets the lot)
        public bool Known(ulong lot, long now, long span)
        {
            long from;
            if (lot == 0 || !mFrom.TryGetValue(lot, out from)) return false;
            if (now >= from && now - from < span) return true;
            mFrom.Remove(lot);
            return false;
        }

        // the forced disposal failed on this lot; true when the lot was not remembered before (a new log line)
        public bool Note(ulong lot, long now, long span)
        {
            if (lot == 0) return false;
            bool fresh = !Known(lot, now, span);
            if (fresh && mFrom.Count >= kMax) Prune(now, span);
            mFrom[lot] = now;
            return fresh;
        }

        // full: drop the expired lots; still full = clear (a bounded table; the lots fail again and come back)
        void Prune(long now, long span)
        {
            List<ulong> old = new List<ulong>();
            foreach (KeyValuePair<ulong, long> e in mFrom) if (now < e.Value || now - e.Value >= span) old.Add(e.Key);
            for (int i = 0; i < old.Count; i++) mFrom.Remove(old[i]);
            if (mFrom.Count >= kMax) mFrom.Clear();
        }

        // PutHeldTrashInCan: what runs. disposeOutside = the call comes from TrashPile.DisposeOutside with forceOutdoor;
        // receptacle = EA's non-forced search finds a chute, a non-full trash can or (recyclable pile) a recycle bin.
        public static int Decide(bool on, bool disposeOutside, bool exempt, bool known, bool receptacle)
        {
            if (!on || !disposeOutside) return kEa;
            if (exempt) return kExempt;
            if (!known) return kEa;
            return receptacle ? kFallback : kPutDown;
        }

        // after EA's forced disposal: remember the lot when it failed and one of its plans found no path
        public static bool Record(bool on, bool disposeOutside, bool exempt, bool ok, bool sawNoPath)
        {
            return on && disposeOutside && !exempt && !ok && sawNoPath;
        }

        // EmptyTrash.Definition.Test: refuse EA's true for an autonomous non-exempt NPC on an indoor can of a remembered lot
        public static bool Refuse(bool on, bool eaTrue, bool autonomous, bool exempt, bool indoorCan, bool known)
        {
            return on && eaTrue && autonomous && !exempt && indoorCan && known;
        }
    }
}
