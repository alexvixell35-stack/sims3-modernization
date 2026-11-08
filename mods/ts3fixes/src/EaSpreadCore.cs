using System;
using System.Collections.Generic;

namespace Ts3Fixes
{
    // The game's mscorlib (.NET 2.0) has no Func or plain Action.
    public delegate long ElapsedUs();
    public delegate void Job();
    public delegate bool Gate();
    public delegate int ListCount();
    public delegate T ListItem<T>(int index);
    public delegate void ItemCheck(int index);

    // Game-independent parts of EaSpread.cs (docs/147), kept apart for the offline self-check (tools/selftest/easpread).
    public static class EaSpreadCore
    {
        public const int kSliceUs = 2000;    // a slice ends after this much work...
        public const int kMaxActors = 16;    // ...or after this many Sims (nightly NPC inventory seeding + stuck-check alarms),
        public const int kMaxDecayPairs = 96; // or relationships stepped, whichever comes first

        public const int kMaxLotUnits = 64;  // venue cleanup: objects tested or acted on (plus a few per-lot steps) per tick

        // Units for the game's two-step "find every object that needs it, then act on each" loop over a list read at the
        // owner's turn: one unit per object to test, then one per object to act on (only the found ones act, in list
        // order), so one big lot continues mid-list in the next tick. An object gone by its act unit is skipped.
        public static void AddFindThenAct<T>(List<Job> into, IList<T> items, Predicate<T> find, Action<T> act, Predicate<T> stillThere)
        {
            bool[] found = new bool[items.Count];
            for (int i = 0; i < items.Count; i++)
            {
                int k = i;
                into.Add(delegate { found[k] = find(items[k]); });
            }
            for (int i = 0; i < items.Count; i++)
            {
                int k = i;
                into.Add(delegate { if (found[k] && stillThere(items[k])) act(items[k]); });
            }
        }

        // Units for a loop over a list's items from the last to the first (NRaas' inventory check order): each unit finds
        // its item again (at its old index first, else by a search; the list may have changed while we slept) and checks
        // it there; an item gone, or all of them once stop() says so, is skipped.
        public static void AddItemUnits<T>(List<Job> into, ListCount count, ListItem<T> itemAt, ItemCheck check, Gate stop) where T : class
        {
            for (int i = count() - 1; i >= 0; i--)
            {
                int old = i;
                T item = itemAt(i);
                into.Add(delegate
                {
                    if (stop()) return;
                    int at = FindAgain(count(), itemAt, item, old);
                    if (at >= 0) check(at);
                });
            }
        }

        public static int FindAgain<T>(int count, ListItem<T> itemAt, T item, int old) where T : class
        {
            if (old < count && (object)itemAt(old) == (object)item) return old;
            for (int i = count - 1; i >= 0; i--) if ((object)itemAt(i) == (object)item) return i;
            return -1;
        }

        public static bool SliceFull(int items, long elapsedUs, int maxItems, long maxUs) { return items >= maxItems || elapsedUs >= maxUs; }

        // The pet pool keeps one alarm handle. EA's callback clears it without removing a pending alarm, so a fire while the
        // stored alarm is still due later comes from a second chain; running it would keep both chains alive (docs/147 §3.1).
        public static bool ExtraPoolFire(float storedAlarmMinutesLeft) { return storedAlarmMinutesLeft > 0f; }

        // EA's Relationship.DecayAll handles each pair once, under the Sim with the lower id.
        public static bool OwnsPair(ulong keyId, ulong otherId) { return keyId < otherId; }
    }

    public delegate List<P> PartnersOf<K, P>(K key);       // a Sim's relationship partners, read when its turn comes (null = none)
    public delegate bool PairStep<K, P>(K key, P partner); // true when the pair was stepped (counts toward the slice budget)

    // The daily relationship decay a relationship at a time: the Sims in alarm order, each Sim's partners read when its turn
    // comes, as DecayAll reads them. A slice ends after maxPairs stepped pairs or maxUs and the next one resumes at the next
    // partner of the same Sim. A pass still running at the next alarm is finished first (one step per pair per day).
    public sealed class DailyPairPass<K, P>
    {
        List<K> mKeys;
        int mNextKey, mPos;
        K mKey;
        List<P> mPartners;
        PartnersOf<K, P> mPartnersOf;
        PairStep<K, P> mStep;
        Job mEnd;

        public bool Running { get { return mKeys != null; } }

        // True when the previous day's pass had to be finished first.
        public bool Begin(IEnumerable<K> keys, PartnersOf<K, P> partnersOf, PairStep<K, P> step, Job end)
        {
            bool finishedOld = Running;
            Finish();
            mKeys = new List<K>(keys);
            mNextKey = 0;
            mPartners = null;
            mPos = 0;
            mPartnersOf = partnersOf;
            mStep = step;
            mEnd = end;
            return finishedOld;
        }

        // One relationship entry; false when the pass has none left.
        bool Next(ref int stepped)
        {
            while (mPartners == null || mPos >= mPartners.Count)
            {
                if (mNextKey >= mKeys.Count) return false;
                mKey = mKeys[mNextKey++];
                mPartners = mPartnersOf(mKey) ?? new List<P>();
                mPos = 0;
            }
            if (mStep(mKey, mPartners[mPos++])) stepped++;
            return true;
        }

        // One slice (at least one entry); true when the pass is over (its end step has run).
        public bool Slice(int maxPairs, long maxUs, ElapsedUs elapsedUs, out int stepped)
        {
            stepped = 0;
            if (mKeys == null) return true;
            while (Next(ref stepped))
                if (stepped >= maxPairs || elapsedUs() >= maxUs) return false;
            End();
            return true;
        }

        public void Finish()
        {
            if (mKeys == null) return;
            int stepped = 0;
            while (Next(ref stepped)) { }
            End();
        }

        void End()
        {
            mKeys = null;
            mPartners = null;
            mKey = default(K); // issue #40: the last key (a Sim) stayed reachable
            mEnd();
        }

        // The world the pass belongs to is gone: drop it without running the rest.
        public void Abandon() { mKeys = null; mPartners = null; mKey = default(K); }
    }

    // A one-shot pass over a snapshot of items, a slice at a time. Each item is handed out exactly once, in order; a slice
    // runs at least one item, so every pass ends.
    public sealed class SlicedPass<T>
    {
        readonly List<T> mItems;
        int mNext;

        public SlicedPass(IEnumerable<T> items) { mItems = new List<T>(items); }

        public int Count { get { return mItems.Count; } }
        public bool Done { get { return mNext >= mItems.Count; } }

        // elapsedUs: microseconds since the slice started.
        public int RunSlice(Action<T> work, int maxItems, long maxUs, ElapsedUs elapsedUs)
        {
            int n = 0;
            while (mNext < mItems.Count && (n == 0 || !EaSpreadCore.SliceFull(n, elapsedUs(), maxItems, maxUs)))
            {
                work(mItems[mNext++]);
                n++;
            }
            return n;
        }

        public int RunRest(Action<T> work)
        {
            int n = 0;
            while (mNext < mItems.Count) { work(mItems[mNext++]); n++; }
            return n;
        }
    }

    // A daily pass (one step per item per alarm) spread over ticks. A pass still running when the next alarm fires is
    // finished first, so no item misses a day or gets two steps for one day.
    public sealed class DailyPass<T>
    {
        SlicedPass<T> mPass;
        Action<T> mWork;
        Job mEnd;

        public bool Running { get { return mPass != null; } }

        // True when the previous day's pass had to be finished first.
        public bool Begin(IEnumerable<T> items, Action<T> work, Job end)
        {
            bool finishedOld = Running;
            Finish();
            mPass = new SlicedPass<T>(items);
            mWork = work;
            mEnd = end;
            return finishedOld;
        }

        // One slice; true when the pass is over (its end step has run).
        public bool Slice(int maxItems, long maxUs, ElapsedUs elapsedUs)
        {
            if (mPass == null) return true;
            mPass.RunSlice(mWork, maxItems, maxUs, elapsedUs);
            if (!mPass.Done) return false;
            Finish();
            return true;
        }

        public void Finish()
        {
            if (mPass == null) return;
            SlicedPass<T> pass = mPass;
            mPass = null;
            pass.RunRest(mWork);
            mEnd();
        }

        // The world the pass belongs to is gone: drop it without running the rest.
        public void Abandon() { mPass = null; }
    }
}
