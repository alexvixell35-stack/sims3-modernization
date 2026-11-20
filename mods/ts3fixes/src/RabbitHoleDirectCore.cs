using System;

namespace Ts3Fixes
{
    // Game-independent decisions of rabbitholedirect (RabbitHoleDirect.cs). Self-check: tools/selftest/rabbitholedirect.
    public static class RabbitHoleDirectCore
    {
        public const int kRhdRefuse = -1, kRhdGo = 0, kRhdFade = 1;
        // the Sim's start is unseen (checked before). A seen door: only when nothing runs between the move and EA's enter
        // animation (no beforeEntering, or the base no-op), and then the Sim is faded out first; else the old path.
        public static int DirectGate(bool doorSeen, bool beforeIsNoOp)
        {
            if (!doorSeen) return kRhdGo;
            return beforeIsNoOp ? kRhdFade : kRhdRefuse;
        }
        // beforeEntering may move the Sim (Career.WaitForWork routes outside and idles): then EA's enter animation routes back
        // to the slot itself (its doRoute path), else it plays in place as the subway portal calls it
        public const float kSlotNearM = 0.5f;
        public static bool DoRoute(float movedM) { return !(movedM <= kSlotNearM); }
    }
}
