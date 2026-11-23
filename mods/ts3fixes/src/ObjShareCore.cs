namespace Ts3Fixes
{
    // Game-independent rules of ObjShare.cs (linewaitlog, rhshare / rhsharehh, servingshare / servingsharehh,
    // docs/686 §14), checked offline by tools/selftest/objshare.
    public static class ObjShareCore
    {
        public const int kNone = 0, kGo = 1, kWould = 2, kExempt = 3, kKeep = 4, kStale = 5;
        public const int kGotTurn = 0, kTimedOut = 1, kCancelled = 2, kShared = 3;
        public static readonly string[] kOutcomeNames = { "got turn", "timed out", "cancelled", "shared" };
        public const float kLogMinutes = 0.5f;      // a line wait shorter than this is counted as short and not written
        public const float kRhStaleMinutes = 15f;   // the game's own age for a reservation nobody uses

        // ---- linewaitlog ----

        // A finished wait in a line. got = the game's answer. A refusal after the whole timeout is "timed out"; any other
        // refusal (exit reason, pushed out of the line, another object tried) is "cancelled".
        public static int LineOutcome(bool got, float waitedMinutes, float timeoutMinutes)
        {
            if (got) return kGotTurn;
            return timeoutMinutes > 0f && waitedMinutes >= timeoutMinutes ? kTimedOut : kCancelled;
        }

        public static bool LineLogged(float waitedMinutes) { return waitedMinutes >= kLogMinutes; }

        // A wait for a rabbit hole's enter slot that the game ended itself: after the stale age it broke the reservation.
        public static int RhOutcome(float waitedMinutes) { return waitedMinutes >= kRhStaleMinutes ? kTimedOut : kGotTurn; }

        // ---- rhshare ----

        // A Sim asks for an enter slot.
        //   held        another Sim's reservation is on the slot (free slot, or the Sim's own: kNone, the game's code)
        //   simPlain    a walking human, not riding, not carrying or carried (others keep the game's wait)
        //   holderKnown the reservation names a Sim that still exists; holderScope / holderPlain: that Sim's
        //   kGo     walk to the slot now, reservation untouched
        //   kStale  the reservation names nobody alive: it is removed and the game's code reserves for this Sim
        //   kKeep   wait (our loop, which a cancel ends) because the holder is out of scope or not plain
        //   kExempt this Sim is out of scope (the game's wait), kWould = off
        public static int Rh(bool on, bool held, bool simScope, bool simPlain, bool holderKnown, bool holderScope, bool holderPlain)
        {
            if (!held) return kNone;
            if (!on) return kWould;
            if (!simScope) return kExempt;
            if (!simPlain) return kNone;
            if (!holderKnown) return kStale;
            return holderScope && holderPlain ? kGo : kKeep;
        }

        // Our wait: the reservation is old enough to break when the same holder kept it for the whole stale age
        // (a new holder starts the age again).
        public static bool RhBreak(float minutesWithThisHolder) { return minutesWithThisHolder > kRhStaleMinutes; }

        // ---- servingshare ----

        // A Sim starts Grab a Serving at the platter (not from an inventory). othersScope: every other Sim in the
        // platter's line or using it is in scope. kGo = no line, and the Sim leaves the use list while it takes.
        public static int Serve(bool on, bool inInventory, bool simScope, bool othersScope)
        {
            if (inInventory) return kNone;
            if (!on) return kWould;
            if (!simScope) return kExempt;
            return othersScope ? kGo : kKeep;
        }

        // The check right before a take: servings other Sims are taking now are spoken for. A buffet never runs out.
        public static bool ServeLeft(int servings, int takenByOthersNow, bool buffet)
        {
            if (buffet) return true;
            if (takenByOthersNow < 0) takenByOthersNow = 0;
            return servings - takenByOthersNow > 0;
        }
    }
}
