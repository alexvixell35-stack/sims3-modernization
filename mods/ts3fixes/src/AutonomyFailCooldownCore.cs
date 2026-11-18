using System;

namespace Ts3Fixes
{
    // Game-independent part of autonomyfailcooldown (AutonomyFailCooldown.cs), kept free of game types so the offline
    // self-test (tools/selftest/autonomyfailcooldown) compiles it alone. Times are sim ticks.
    //
    // One row per Sim, interaction definition type and target object: the earlier route failure still inside the window.
    // The second route failure inside the window starts a cooldown of len; a run of it that succeeds clears the row.
    public sealed class AfcRow
    {
        public ulong Target;  // target object id
        public object Def;    // interaction definition type (object: no game types here)
        public long F0;       // the earlier failure, valid when N == 1
        public int N;
        public long Until;    // end of the cooldown, 0 = none

        // a route failure at now; true = this failure started the cooldown
        public bool Fail(long now, long win, long len)
        {
            if (N == 1 && now - F0 > win) N = 0;
            if (N == 1) { Until = now + len; N = 0; return true; }
            F0 = now;
            N = 1;
            return false;
        }

        // a run that succeeded: the target works again
        public void Ok() { N = 0; Until = 0; }

        // inside the cooldown, and it ends no more than len ahead (a clock that went back never keeps a Sim out longer)
        public bool Cooling(long now, long len) { return Until != 0 && now < Until && Until - now <= len; }

        // the head interaction left the queue unsucceeded: does it count as a route failure? Only autonomous runs, never a
        // cancel (the player, a higher-priority push or a script: Cancelled or an ExitReason.CancelExternal bit), and only
        // when a route failed: EA's ExitReason.RouteFailed (SimRoutingComponent.DoRoute with a failed plan) or the Sim's last
        // plan, made while this interaction was the head, failed (Runs that check the plan and return without DoRoute).
        public static bool CountsAsRouteFailure(bool autonomous, bool cancelled, bool cancelReason, bool routeFailedReason, bool lastPlanFailed)
        {
            return CountsAsRouteFailure(autonomous, cancelled, cancelReason, routeFailedReason, lastPlanFailed, false);
        }

        // recentLock: the Sim had a FailedBecauseDestinationPortalLocked plan in the last kAfcLockMin sim-minutes (a bathroom
        // door locked for privacy while another Sim is inside). That clears on its own; cooling down the only toilet would
        // leave the Sim without one for the whole cooldown, so it never counts (EA's own retry behaviour stays).
        public static bool CountsAsRouteFailure(bool autonomous, bool cancelled, bool cancelReason, bool routeFailedReason, bool lastPlanFailed, bool recentLock)
        {
            return autonomous && !cancelled && !cancelReason && !recentLock && (routeFailedReason || lastPlanFailed);
        }

        // which row a head leaving the queue counts on: none, its own (definition, target), its pusher's (a continuation of the
        // group whose autonomous pusher was recorded), or wait (a success with a continuation of its group queued behind it:
        // the continuation's end decides, so EA's MopBurntTiles "success" that only pushed MopBurnt clears nothing)
        public const int kNone = 0, kOwn = 1, kPusher = 2, kWait = 3;
        public static int Row(bool ok, bool autonomous, bool ofPusher, bool continuationQueued, bool terrainTarget)
        {
            if (ok && continuationQueued) return kWait;
            if (ofPusher) return kPusher;
            return autonomous && !terrainTarget ? kOwn : kNone;
        }

        // may the head's own (definition, target) row change? Autonomous only. A Hidden head was pushed by script code, never
        // picked from a scored offer: no Sims3.Gameplay.Autonomy.Autonomy method sets InteractionInstance.Hidden [verified, EA
        // callgraph; simler90 UPDATE242 inferred the same, fail-safe: a missed cooldown], and SwimmingInPool.SwimAroundCallback
        // pushes DoggySwimAround Hidden from a posture alarm [verified, decompiled EA and simler90]. A cooldown has nothing to
        // refuse there: its failure is not recorded [4016]. Its success still clears (the target works).
        public static bool OwnRow(bool autonomous, bool hidden, bool ok) { return autonomous && (ok || !hidden); }

        // an autonomy scoring call may be refused: the search's flags say autonomous and not user-directed (EA:
        // Autonomy.GetDefaultSearchFlags gives PieMenuChoices UserDirected, every other search Autonomous)
        public static bool AutonomousOffer(bool autonomousFlag, bool userDirectedFlag) { return autonomousFlag && !userDirectedFlag; }
    }

    // One row per Sim and interaction definition type (2026-11-18): the distinct targets whose route failed inside the window,
    // newest failure each. kDistinct of them start a cooldown of the definition on every target (a Sim hopping from one
    // unreachable object to the next). Its motivating case was wrong: etl14f af18-bp-31361-m shows Roxy Dog's LookOutWindow
    // "failures" are plans of the window's outside paws spot that EA rejects in a run that then reaches the inside spot, or runs
    // cancelled by HigherPriorityNext (AutonomyFailCooldown.cs header), so no real failure of hers is missed.
    public sealed class AfcDefRow
    {
        public const int kDistinct = 3;
        public object Def;
        public readonly ulong[] T = new ulong[kDistinct];  // target ids, 0 = free
        public readonly long[] At = new long[kDistinct];   // that target's last failure
        public long Until;                                 // end of the all-targets cooldown, 0 = none

        // a route failure on target at now; true = this failure started the all-targets cooldown
        public bool Fail(ulong target, long now, long win, long len)
        {
            int slot = -1, n = 0;
            for (int i = 0; i < kDistinct; i++)
            {
                if (T[i] != 0 && (now - At[i] > win || now < At[i])) T[i] = 0;  // out of the window (or a clock that went back)
                if (T[i] == 0) { if (slot < 0) slot = i; continue; }
                if (T[i] == target) { At[i] = now; return false; }
                n++;
            }
            T[slot] = target;  // a free slot exists: a full row of kDistinct already started the cooldown and was emptied
            At[slot] = now;
            if (n + 1 < kDistinct) return false;
            for (int i = 0; i < kDistinct; i++) T[i] = 0;  // counting starts over after the cooldown
            Until = now + len;
            return true;
        }

        // a run of the definition that succeeded: forget the failed targets and any cooldown
        public void Ok() { for (int i = 0; i < kDistinct; i++) T[i] = 0; Until = 0; }

        public bool Cooling(long now, long len) { return Until != 0 && now < Until && Until - now <= len; }
    }
}
