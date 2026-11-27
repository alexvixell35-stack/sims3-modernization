namespace Ts3Fixes
{
    // Game-independent part of the ShortcutOn answer cache (docs/605 §2). One table holds the answers computed while the
    // switch table had one generation and one key count; a new generation or count means a new table (Fixes.SwCurrent).
    // Keys are compared by reference: a call site's literal is one interned string object, so a hit costs a few character
    // reads and one reference compare instead of two string hashes (the switch dictionary and the compiler's string switch).
    // A name built at run time (another object with the same text) just misses and takes the slow path; Put caps the table
    // at half full, so such names can never fill it.
    // Thread use: an entry is one immutable object published by one reference store, so a reader on another thread sees
    // either no entry or a whole one. Two threads storing into the same free slot can lose one entry (it is looked up the
    // slow way again), never pair a key with another key's answer. No locks (the game's Mono has no MonoThread on workers).
    public sealed class SwitchCache
    {
        public const int Size = 4096;           // power of two, about 4x the ~1010 switch names (2048 was half full at 1012: probe 16)
        const int kMask = Size - 1;

        public const int Off = 0, On = 1, Live = 2;  // Live: the name has a switch-table entry, read it on every call

        sealed class Entry
        {
            public readonly string Key;
            public readonly int State;
            public Entry(string key, int state) { Key = key; State = state; }
        }

        readonly Entry[] mSlots = new Entry[Size];
        int mUsed;
        public readonly int Gen;     // switch-table generation this table was built for
        public readonly int Count;   // switch-table key count this table was built for

        public SwitchCache(int gen, int count) { Gen = gen; Count = count; }

        public int Used { get { return mUsed; } }

        // A few characters only: the names are short and differ early and late; collisions cost one more probe.
        public static int Hash(string s, int n)
        {
            return (n * 0x9E5 ^ s[0] * 0x3B ^ s[n - 1] * 0x2A1 ^ s[n >> 1] * 0x10F ^ s[(n * 3) >> 2] * 0x6D) & kMask;
        }

        // Off / On / Live, or -1 = not stored (null or empty names are never stored: the slow path keeps its exceptions).
        public int Get(string name)
        {
            if (name == null) return -1;
            int n = name.Length;
            if (n == 0) return -1;
            int i = Hash(name, n);
            for (int probe = 0; probe < Size; probe++)
            {
                Entry e = mSlots[i];
                if (e == null) return -1;
                if ((object)e.Key == (object)name) return e.State;
                i = (i + 1) & kMask;
            }
            return -1;
        }

        public void Put(string name, int state)
        {
            if (name == null || name.Length == 0 || mUsed >= Size / 2) return;
            int i = Hash(name, name.Length);
            for (int probe = 0; probe < Size; probe++)
            {
                Entry e = mSlots[i];
                if (e == null) { mSlots[i] = new Entry(name, state); mUsed++; return; }
                if ((object)e.Key == (object)name) return;  // stored by another caller meanwhile (same generation and count: same state)
                i = (i + 1) & kMask;
            }
        }

        public string[] Keys()
        {
            System.Collections.Generic.List<string> l = new System.Collections.Generic.List<string>();
            for (int s = 0; s < Size; s++) { Entry e = mSlots[s]; if (e != null) l.Add(e.Key); }
            return l.ToArray();
        }

        // Longest probe run of the stored names (self-check: the hash spreads the real names).
        public int MaxProbe()
        {
            int worst = 0;
            for (int s = 0; s < Size; s++)
            {
                Entry e = mSlots[s];
                if (e == null) continue;
                int i = Hash(e.Key, e.Key.Length), d = 1;
                while ((object)mSlots[i] != (object)e) { i = (i + 1) & kMask; d++; }
                if (d > worst) worst = d;
            }
            return worst;
        }
    }
}
