using System.Collections.Generic;

namespace Ts3Fixes
{
    // Game-independent rule of strayleave (StrayLeave.cs, docs/656 §4.2). Self-check: tools/selftest/strayleave.
    // A stray's visit ends only when its StrayPet buff times out (BuffStrayPet.OnTimeout -> Sim.MakeSimGoHome); meta-autonomy's
    // own go-home runs only when autonomy found nothing, and a pet always finds Sit On Ground. Step, once per scan per
    // instantiated stray: exempt -> forget; buff on, or leaving / busy (go-home, a lot trip, a social, held) -> forget;
    // first seen without the buff -> start its clock (kNew); clock older than limit -> kSend (forget, so a failed walk
    // home waits a whole limit again); else kWait. A clock that went back starts over. Bounded (kMax).
    public sealed class StrayLeaveCore
    {
        public const int kMax = 128;
        public const int kClear = 0, kNew = 1, kWait = 2, kSend = 3, kExempt = 4;

        readonly Dictionary<ulong, long> mSince = new Dictionary<ulong, long>();
        public int Count { get { return mSince.Count; } }

        public int Step(ulong id, bool exempt, bool buff, bool busy, long now, long limit)
        {
            if (exempt) { mSince.Remove(id); return kExempt; }
            if (buff || busy || limit <= 0) { mSince.Remove(id); return kClear; }
            long since;
            if (!mSince.TryGetValue(id, out since) || since > now)
            {
                if (mSince.Count >= kMax) mSince.Clear();
                mSince[id] = now;
                return kNew;
            }
            if (now - since >= limit) { mSince.Remove(id); return kSend; }
            return kWait;
        }

        public void Forget(ulong id) { mSince.Remove(id); }

        public static string Name(int d)
        {
            switch (d)
            {
                case kClear: return "clear";
                case kNew: return "new";
                case kWait: return "wait";
                case kSend: return "send";
                case kExempt: return "exempt";
                default: return "?";
            }
        }
    }
}
