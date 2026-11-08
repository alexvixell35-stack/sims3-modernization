namespace Ts3Fixes
{
    // Game-independent part of dancefloorlistdiag (DanceFloorDiag.cs, docs/253 §7). Self-check: tools/selftest/dancefloorlistdiag.
    public static class DanceFloorDiagCore
    {
        public const int kSame = 0, kFrame = 1, kFreed = 2, kWrite = 3, kItems = 4;

        // What one route step left behind, in docs/253 §2 terms, from what the hook can read safely:
        //   enumOk     the boxed copy of our enumerator still names the list and the version it started with (else (b): the
        //              frame's enumerator words changed)
        //   alive      the WeakReference taken before the loop still has the list (else (a): the GC freed a list our frame
        //              still holds)
        //   ver0/ver1, cnt0/cnt1, sameItems: the live list's _version, Count and items before / after (items only read while alive)
        // kWrite = version moved but count and items are the same ((c): a write into the live list's version word);
        // kItems = the live list's count or items changed (a managed write no EA code can make, docs/253 §2).
        public static int Classify(bool enumOk, bool alive, int ver0, int ver1, int cnt0, int cnt1, bool sameItems)
        {
            if (!enumOk) return kFrame;
            if (!alive) return kFreed;
            if (cnt0 != cnt1 || !sameItems) return kItems;
            if (ver0 != ver1) return kWrite;
            return kSame;
        }

        public static string Name(int k)
        {
            switch (k)
            {
                case kSame: return "same";
                case kFrame: return "frame (enumerator words changed)";
                case kFreed: return "freed (weak reference cleared while held)";
                case kWrite: return "version write (same items)";
                case kItems: return "items changed";
                default: return "?";
            }
        }
    }
}
