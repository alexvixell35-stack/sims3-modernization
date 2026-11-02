using System;

namespace Ts3Fixes
{
    // Game-independent decision of jazzparam (JazzParam.cs, issue #166 build 21, docs/511). Self-check: tools/selftest/jazzparam.
    public static class JazzParamCore
    {
        public const int kPass = 0, kSeen = 1, kCaught = 2;

        // What the hook does with an exception from Sim.UpdateJazzParameter: only a NullReferenceException while the Sim has
        // no posture is ours. kPass: rethrow, not counted. kSeen: counted, rethrown (switch off). kCaught: counted, caught.
        public static int Decide(Exception e, bool postureNull, bool on)
        {
            if (!(e is NullReferenceException) || !postureNull) return kPass;
            return on ? kCaught : kSeen;
        }
    }
}
