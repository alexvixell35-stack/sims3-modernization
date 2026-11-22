namespace Ts3Fixes
{
    // Game-independent rules of ShareLanes.cs (sharelanes, laddershare, simpass, angleentry; docs/686 §8), checked offline by
    // tools/selftest/sharelanes.
    //
    // The game keeps one table per portal object: which Sim holds which lane, entered from which end. A lane held by a Sim
    // that entered from the same end is given to the next Sim as well; a lane held from the other end is refused, and when
    // every lane is refused the Sim walks to a waiting spot and asks again for up to 20 sim-minutes. The rules here say
    // when such a refusal may be lifted, when a Sim may walk through Sims instead of standing, when the ladder's
    // one-climber line may be skipped and when the approach to a stair or ladder may drop its facing.
    public static class ShareLanesCore
    {
        // portal kinds the lane sharing covers; anything else keeps the game's table (elevators, turnstiles, velvet ropes,
        // pools: their lane hooks keep per-Sim state or play one animation at a time). Spiral stairs: switch spiralshare.
        public const int kOther = 0, kStairs = 1, kDoor = 2, kGate = 3, kLadder = 4, kSpiral = 5;
        public static readonly string[] kKindNames = { "other", "stairs", "door", "gate", "ladder", "spiral" };

        public const int kNone = 0, kWould = 1, kExempt = 2, kGo = 3;

        public struct Holder
        {
            public uint Lane, StartSlot;
            public bool InScope, Elevator;
        }

        // lane classes for a Sim that wants to enter `lane` at `slot`
        public const int kLaneFree = 0, kLaneOpposite = 1, kLaneClosed = 2;

        // The game's own answer of the table: it looks at the first holder of the lane only.
        public static bool EaFree(Holder[] h, int n, uint lane, uint slot)
        {
            for (int i = 0; i < n; i++)
                if (h[i].Lane == lane) return h[i].StartSlot == slot && !h[i].Elevator;
            return true;
        }

        // Ours: every holder of the lane counts. Free = nobody, or only Sims that entered at the same end. Opposite = held
        // from the other end by Sims that are all in scope. Closed = an elevator lane, or a holder out of scope (a
        // household Sim without the household switch keeps the lane to itself, as the game has it).
        public static int Lane(Holder[] h, int n, uint lane, uint slot)
        {
            int cls = kLaneFree;
            for (int i = 0; i < n; i++)
            {
                if (h[i].Lane != lane) continue;
                if (h[i].Elevator) return kLaneClosed;
                if (h[i].StartSlot == slot) continue;
                if (!h[i].InScope) return kLaneClosed;
                cls = kLaneOpposite;
            }
            return cls;
        }

        // A lock the game refused. sharable = at least one start lane is held only from the other end.
        public static int Decide(bool on, bool simInScope, bool kindShares, bool sharable)
        {
            if (!kindShares || !sharable) return kNone;
            if (!on) return kWould;
            return simInScope ? kGo : kExempt;
        }

        // The game's choice among lanes that passed the table, kept as it is: a lane whose extra checks and obstruction
        // test pass is taken; the first lane that failed only the obstruction test (or the extra checks, when the lane
        // says they may be skipped) is the fallback when no lane passes. Returns true = take this lane now.
        public static bool Take(bool extraOk, bool unobstructed, bool extraMayBeSkipped, ref bool haveFallback, out bool becomesFallback)
        {
            becomesFallback = false;
            if (extraOk && unobstructed) return true;
            if (!haveFallback && (extraOk || extraMayBeSkipped)) { haveFallback = true; becomesFallback = true; }
            return false;
        }

        // sharelanes, stand-and-wait: a blocked cycle whose plan answered "destination portal locked". The plan is made
        // once without "blocked by people" on the first such cycle and every `every` cycles after it; a plan that then
        // succeeds proves only standing Sims closed the portal.
        public static int PortalPass(bool on, bool simInScope, bool portalLocked, int blockedCycle, int every)
        {
            if (!portalLocked || blockedCycle < 1) return kNone;
            if (every < 1) every = 1;
            if ((blockedCycle - 1) % every != 0) return kNone;
            if (!on) return kWould;
            return simInScope ? kGo : kExempt;
        }

        // laddershare, spiralshare: the climb's wait for its turn in the ladder's (the spiral staircase's) own line.
        public static int LadderLine(bool on, bool isLadderLine, bool simInScope, bool othersInScope)
        {
            if (!isLadderLine) return kNone;
            if (!on) return kWould;
            return simInScope && othersInScope ? kGo : kExempt;
        }

        // simpass: a walking Sim met obstacles. Only Sims in the way, none of them at the route's end (the game's own
        // "destination obstructed" handling stays), everybody in scope: walk through. kept = a case left to the game.
        public static int Pass(bool on, bool simInScope, int sims, int others, bool allInScope, bool simAtDestination, out bool kept)
        {
            kept = false;
            if (sims <= 0) return kNone;
            if (others > 0 || simAtDestination) { kept = true; return kNone; }
            if (!on) return kWould;
            return simInScope && allInScope ? kGo : kExempt;
        }

        // angleentry, stairs: the game keeps the planned approach when the Sim got the lane it planned for; only then is
        // there an approach of ours to make. Ladder: only the climb up (the game places and turns the Sim itself there;
        // the climb down plays from where the Sim stands, so it keeps the slot's facing).
        public static int AngleStairs(bool on, bool simInScope, bool isStairs, bool preferredLane, bool forcedPlan, int paths)
        {
            if (!isStairs || !preferredLane || forcedPlan || paths < 1) return kNone;
            if (!on) return kWould;
            return simInScope ? kGo : kExempt;
        }

        public static int AngleLadder(bool on, bool simInScope, bool isLadder, bool startsAtBottom, bool planToSlot)
        {
            if (!isLadder || !startsAtBottom || !planToSlot) return kNone;
            if (!on) return kWould;
            return simInScope ? kGo : kExempt;
        }

        // sim-minutes to tenths for the wait counters (never negative, capped so a broken clock cannot flood a counter)
        public static int Tenths(float minutes)
        {
            if (!(minutes > 0f)) return 0;
            if (minutes > 600f) minutes = 600f;
            return (int)(minutes * 10f + 0.5f);
        }
    }
}
