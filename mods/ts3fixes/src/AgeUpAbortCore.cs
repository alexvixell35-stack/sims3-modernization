using System;

namespace Ts3Fixes
{
    // Game-independent decision of ageupabort (AgeUpAbort.cs, issue #166 build 32, docs/512). Self-check: tools/selftest/ageupabort.
    public static class AgeUpAbortCore
    {
        public const int kPass = 0, kSeen = 1, kCaught = 2;

        // What the hook does with an exception from AgingState.PreInstantiateSim: only a NullReferenceException after the
        // builder was aborted (the task field is null) while the age-up is still deferred is ours. kPass: rethrow, not
        // counted. kSeen: counted, rethrown (switch off). kCaught: counted, caught.
        public static int Decide(Exception e, bool taskNull, bool stillDeferred, bool on)
        {
            if (!(e is NullReferenceException) || !taskNull || !stillDeferred) return kPass;
            return on ? kCaught : kSeen;
        }
    }
}
