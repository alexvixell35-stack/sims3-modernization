namespace Ts3Fixes
{
    // Game-independent rules of BlockedUse.cs (blockeduse / blockedusehh, docs/686 §10), checked offline by
    // tools/selftest/blockeduse.
    //
    // A Sim's route to an object's routing slot fails when furniture or a wall covers the slot or the last stretch to it,
    // and the Sim gives up with the route-fail balloon. The rules here say which failed plans count, who is in scope,
    // when the Sim still has to walk up to the object, when the last stretch is walked through objects, when the Sim is
    // put on the slot instead, and when a Sim that was let onto a closed slot is put back where it came from.
    public static class BlockedUseCore
    {
        // RoutePlanResultType values the rules read (the game's enum, by number so the rules stay game-independent)
        public const int kSucceeded = 1, kNoDestination = 10, kNoUnobstructedSlots = 14, kPointsObstructed = 15, kNoPath = 16;

        // A failed slot plan of this kind: the slot or the way to it is closed, and not by a Sim. "Blocked by a Sim",
        // "portal locked", an aborted plan and a missing object keep the game's behaviour.
        public static bool Kind(int resultType, bool blockerIsSim)
        {
            if (blockerIsSim) return false;
            return resultType == kNoDestination || resultType == kNoUnobstructedSlots || resultType == kPointsObstructed || resultType == kNoPath;
        }

        public const int kNone = 0, kWould = 1, kExempt = 2, kGo = 3;

        // Scope: blockeduse covers Sims the player does not control, blockedusehh household and selected Sims. Each
        // switch stands alone. With both off every case counts as "would".
        public static int Decide(bool kind, bool player, bool on, bool hhOn)
        {
            if (!kind) return kNone;
            if (!on && !hhOn) return kWould;
            if (player) return hhOn ? kGo : kExempt;
            return on ? kGo : kWould;
        }

        // The Sim must stand in the object's room, on its level and lot, within nearMax of a wanted slot before the last
        // stretch is relaxed; otherwise it first walks up to the object with a normal plan (and when that plan fails the
        // object is out of reach: the game's behaviour stays).
        public static bool NeedNear(bool sameLot, bool sameLevel, bool sameRoom, float dist, float nearMax)
        {
            return !(sameLot && sameLevel && sameRoom && dist <= nearMax);
        }

        // The same failed case is not tried again before the cooldown ran out (a Sim's autonomy asks again and again).
        public static bool Cooling(long now, long last, long cooldown)
        {
            long d = now - last;
            return d >= 0 && d < cooldown;
        }

        public const int kKeep = 0, kClip = 1, kSnap = 2;

        // After the walk up: a plan that may pass through objects (walls still count) and ends on a free slot within
        // clipMax is walked. Otherwise the Sim is put on the nearest wanted slot within snapMax, if there is one.
        public static int Step(bool sameLotAndLevel, bool clipPlanOk, float clipLen, float clipMax, bool simAtGoal, int snapSlot)
        {
            if (!sameLotAndLevel) return kKeep;
            if (clipPlanOk && clipLen <= clipMax && !simAtGoal) return kClip;
            return snapSlot >= 0 ? kSnap : kKeep;
        }

        // The nearest usable slot within snapMax (flat distance) and maxDy (height). usable[i] false = the plan does not
        // want this slot (weight 0, a part in use), its place is unknown, or a Sim stands on it. -1 = none.
        public static int PickSlot(float[] dist, float[] dy, bool[] usable, int n, float snapMax, float maxDy)
        {
            int best = -1;
            for (int i = 0; i < n; i++)
            {
                if (!usable[i] || dist[i] > snapMax) continue;
                float h = dy[i] < 0f ? -dy[i] : dy[i];
                if (h > maxDy) continue;
                if (best < 0 || dist[i] < dist[best]) best = i;
            }
            return best;
        }

        public const int kExitWait = 0, kExitGone = 1, kExitCheck = 2;

        // A Sim that was let onto a slot is looked at when it plans a route. While the interaction that brought it there
        // runs, or it sits or lies in something, nothing is done. A Sim that is no longer at the slot, or whose entry is
        // older than maxAge, is forgotten. Otherwise the plan decides (ExitSnap).
        public static int ExitLook(bool sameInteraction, bool inPosture, float distFromSlot, float leftDist, long age, long maxAge)
        {
            if (age < 0 || age > maxAge) return kExitGone;
            if (sameInteraction || inPosture) return kExitWait;
            return distFromSlot > leftDist ? kExitGone : kExitCheck;
        }

        // The Sim is put back only when its plan failed, the plan started at the Sim (not at a point the caller named)
        // and a plan from where it stands to the spot it came from fails as well: then it cannot walk away.
        public static bool ExitSnap(bool planOk, bool fromSim, bool probeOk)
        {
            return !planOk && fromSim && !probeOk;
        }
    }
}
