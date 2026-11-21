using System;

namespace Ts3Fixes
{
    // Game-independent decisions of StandWait.cs (docs/686), checked offline by tools/selftest/standwait.
    //
    // A "blocked cycle" is a cycle of the game's obstacle stand-and-wait in which the replan of the interrupted route
    // failed and the game's handler returned before its own exit checks and pushes. The rules, in order:
    //   1. yield:  a push request or an outside cancel that arrived for the waiter is honoured (the game's own exit);
    //   2. pass:   from blocked cycle passCycle on (one cycle later when a push of ours was accepted the cycle before),
    //              and then every passEvery cycles, the route is planned once without "blocked by people"; a plan that
    //              succeeds is followed with the game's ignore-obstacles timer. Never when a Sim that is not the waiter's
    //              own partner stands at the destination (the waiter would end inside it);
    //   3. push:   the Sims named as blockers are pushed with the game's own push, except the waiter's partner, Sims
    //              out of scope, and, in a mutual wait (the blocker waits blocked and named the waiter), a blocker that
    //              outranks the waiter: then the blocker pushes and the waiter yields.
    public static class StandWaitCore
    {
        public struct Blocker
        {
            public ulong Id;
            public int Rank;
            public bool Pushable;     // the game's IsSimPushable(waiter, blocker)
            public bool InScope;      // the fix may act on this Sim (household rule)
            public bool Partner;      // target or actor of the waiter's own interaction
            public bool Waiting;      // itself in a run of blocked cycles
            public bool NamedMe;      // ... and the waiter was among the Sims it named in its last blocked cycle
            public bool AtDest;       // within the game's 0.6 m of the waiter's destination point
        }

        public struct Decision
        {
            public bool Yield, YieldTerminate, PassAllowed;
            public int Pushes, Mutual, Withheld;
        }

        public const int kEndNone = 0, kEndYield = 1, kEndPass = 2, kEndPush = 3, kEndTimeout = 4, kEndOther = 5, kEndGaveUp = 6;
        public static readonly string[] kEndNames = { "none", "yield", "pass", "replan ok after push", "timeout", "other", "gave up (privacy lock)" };

        // Higher priority first, then the selected Sim, then the lower object id: one fixed order for any pair.
        public static int Rank(int priorityLevel, bool selected) { return priorityLevel * 2 + (selected ? 1 : 0); }

        public static bool Outranks(int rankA, ulong idA, int rankB, ulong idB)
        {
            if (rankA != rankB) return rankA > rankB;
            return idA < idB;
        }

        // push[i] is set for each blocker to push this cycle (push.Length >= n).
        public static Decision Decide(bool pushRequested, bool cancelExternal, int myRank, ulong myId, Blocker[] b, int n, bool[] push)
        {
            Decision d = new Decision();
            for (int i = 0; i < n; i++) push[i] = false;
            if (pushRequested || cancelExternal)
            {
                d.Yield = true;
                d.YieldTerminate = !pushRequested;  // the game's rule: a cancel without a push ends the route
                return d;
            }
            d.PassAllowed = true;
            for (int i = 0; i < n; i++)
            {
                if (b[i].Id == myId) continue;
                if (b[i].AtDest && !b[i].Partner) d.PassAllowed = false;
                bool mutual = b[i].Waiting && b[i].NamedMe;
                if (mutual) d.Mutual++;
                if (b[i].Partner || !b[i].Pushable || !b[i].InScope) continue;
                if (mutual && Outranks(b[i].Rank, b[i].Id, myRank, myId)) { d.Withheld++; continue; }
                push[i] = true;
                d.Pushes++;
            }
            return d;
        }

        // True when the people-free plan is due in this blocked cycle. A push accepted in the cycle before buys the pushed
        // Sim one cycle to step aside, once per wait.
        public static bool PassDue(int blockedCycle, ref int nextPass, ref int delays, bool pushAcceptedLast)
        {
            if (nextPass <= 0 || blockedCycle < nextPass) return false;
            if (pushAcceptedLast && delays < 1) { delays++; nextPass = blockedCycle + 1; return false; }
            return true;
        }

        public static void PassFailed(int blockedCycle, ref int nextPass, int passEvery)
        {
            nextPass = blockedCycle + (passEvery < 1 ? 1 : passEvery);
        }

        // How a wait with blocked cycles ended. gaveUp: StandWaitLocked.cs ended it at a privacy lock (docs/686 §7).
        public static int EndKind(int blockedCycles, bool yielded, bool passed, bool planOkAtEnd, int lastPushCycle, float elapsedMin, float durationMin, bool gaveUp)
        {
            if (blockedCycles <= 0) return kEndNone;
            if (gaveUp) return kEndGaveUp;
            if (yielded) return kEndYield;
            if (passed) return kEndPass;
            if (planOkAtEnd && lastPushCycle > 0 && blockedCycles - lastPushCycle <= 2) return kEndPush;
            if (durationMin > 0f && elapsedMin >= 0.9f * durationMin) return kEndTimeout;
            return kEndOther;
        }

        public static int EndKind(int blockedCycles, bool yielded, bool passed, bool planOkAtEnd, int lastPushCycle, float elapsedMin, float durationMin)
        {
            return EndKind(blockedCycles, yielded, passed, planOkAtEnd, lastPushCycle, elapsedMin, durationMin, false);
        }

        // Cycles the wait had left until the game's own timeout, at the wait's own pace: the most a resolved wait can
        // have saved (the blocker might have left on its own earlier).
        public static int TimeoutCyclesLeft(int cycles, float elapsedMin, float durationMin)
        {
            if (cycles <= 0 || elapsedMin <= 0f || durationMin <= elapsedMin) return 0;
            float per = elapsedMin / cycles;
            float left = (durationMin - elapsedMin) / per;
            if (left > 30f) left = 30f;
            return (int)left;
        }
    }
}
