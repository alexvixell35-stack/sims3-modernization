namespace Ts3Fixes
{
    // Game-independent rules of CrowdPort.cs (crowdport / crowdporthh / crowdportlog, docs/686 §19), checked offline
    // by tools/selftest/crowdport.
    public static class CrowdPortCore
    {
        public const int kNone = 0, kGo = 1, kWould = 2, kExempt = 3, kKeep = 4, kWait = 5;
        public const int kMinFloor = 2;            // a crowd is never one Sim
        public const float kWaitCeiling = 15f;     // the game's own line and reservation age
        public const float kLineFloor = 0.25f;     // shortest line timeout handed to the game (its own check period at a portal)
        public const float kEpisodeGap = 2f;       // sim-minutes without a call after which a Sim's wait at an elevator counts from zero
        public const float kRhMaxDistance = 40f;   // a Sim farther than this from its enter slot keeps the game's wait
        public const float kRhHolderClear = 6f;    // the holder must still be this far from the slot (the Sim set on it is through the door before the holder comes)
        public const float kRhAgain = 15f;         // sim-minutes after a Sim was set on a slot before it is set on one again

        public static int Min(int tuned) { return tuned < kMinFloor ? kMinFloor : tuned; }

        public static float Wait(float tuned) { return tuned < 0f ? 0f : tuned > kWaitCeiling ? kWaitCeiling : tuned; }

        // there: Sims waiting for or using the portal, this one included.
        public static bool Crowd(int there, int min) { return there >= Min(min); }

        // A crowd, or this Sim's own wait has passed the short time.
        public static bool Trigger(int there, int min, float waited, float wait) { return Crowd(there, min) || (there >= 2 && waited >= Wait(wait)); }

        // ---- elevator ----

        // A Sim asks for its place in an elevator's line (the game asks again every quarter sim-minute while the Sim
        // stands at the doors).
        //   plain    a walking human, child or older, not riding, not carrying a child (others: kNone, the game's line)
        //   canSkip  the game's teleport skip would be taken now: doors open here, car not in use, same direction
        //   kGo    the wait is answered "timed out" now; the game's caller runs its own teleport skip
        //   kWait  a crowd or a long wait, but no skip now: the line, with a short timeout (the game tries its skip then)
        //   kKeep  no crowd and a short wait so far: the line, timeout cut to what is left of the short wait
        public static int Elevator(bool on, bool scope, bool plain, int there, int min, float waited, float wait, bool canSkip)
        {
            if (!plain) return kNone;
            bool trigger = Trigger(there, min, waited, wait);
            if (!on) return trigger ? kWould : kNone;
            if (!scope) return trigger ? kExempt : kNone;
            if (!trigger) return kKeep;
            return canSkip ? kGo : kWait;
        }

        // The line's timeout for kWait / kKeep. Never longer than the game's.
        public static float ElevatorTimeout(float ea, int decision, float waited, float wait)
        {
            if (decision != kWait && decision != kKeep) return ea;
            float t = decision == kWait ? Wait(wait) : Wait(wait) - waited;
            if (t < kLineFloor) t = kLineFloor;
            return t < ea ? t : ea;
        }

        // A Sim's wait at one elevator goes on (same doors, asked again within the gap); else it starts at zero.
        public static bool SameEpisode(bool sameDoors, float minutesSinceLast) { return sameDoors && minutesSinceLast >= 0f && minutesSinceLast <= kEpisodeGap; }

        // ---- rabbit hole, going in ----

        // A Sim asks for an enter slot.
        //   held        another Sim's reservation is on the slot (free or its own: kNone)
        //   holderKnown / holderScope  the reservation names a living Sim / one in scope
        //   near        the Sim is within kRhMaxDistance of the slot, and RhClear
        //   kGo    the Sim is set on the slot now (no reservation taken); the game's own walk and entry follow from there
        //   kWait  in scope, no crowd yet: our short wait (a cancel, a free slot or a hand-over ends it)
        //   kKeep  the holder is out of scope or the Sim is far away: the game's wait
        public static int RhIn(bool on, bool held, bool scope, bool plain, bool holderKnown, bool holderScope, bool near, int there, int min, float waited, float wait)
        {
            if (!held || !plain) return kNone;
            bool trigger = Trigger(there, min, waited, wait);
            if (!on) return trigger ? kWould : kNone;
            if (!scope) return trigger ? kExempt : kNone;
            if (!holderKnown) return kNone;  // a dead reservation: rhshare or the game's age rule removes it
            if (!holderScope || !near) return kKeep;
            return trigger ? kGo : kWait;
        }

        // ---- rabbit hole, coming out ----

        // May a Sim be set on a held slot now? It then stands where the holder's walk ends. Runs cp-r1 and cp-r3
        // (docs/686 §19.6): the holder's walk failed on the Sim standing there, or the Sim's own route failed on the
        // holder's door jig and it came back for its next try, was set again and failed again (three times, then the
        // route-fail balloon). So only while the holder is kRhHolderClear metres from the slot, and one set per Sim
        // and kRhAgain sim-minutes (sinceSet below 0 = never set). Not clear = the game's wait (kKeep).
        public static bool RhClear(float holderToSlot, float sinceSet) { return holderToSlot >= kRhHolderClear && (sinceSet < 0f || sinceSet >= kRhAgain); }

        // Off (compare mode), at the end of the game's wait: a wait behind one Sim that the first look did not count
        // (no crowd then) and that lasted the short wait would have ended on the slot. Seen in run cp-r1 (docs/686
        // §19.6): two Sims waited 2.2 and 4.8 sim-minutes with the switch off and "would" stayed 0.
        public static bool WouldLate(bool on, bool openAtFirst, float waited, float wait) { return !on && openAtFirst && waited >= Wait(wait); }

        // An NPC begins the game's exit pacing wait (eaSkip: the game lets this Sim out at once anyway).
        //   kGo  our wait, which is the game's with one more exit: a burst when enough Sims are waiting
        public static int RhOut(bool on, bool eaSkip, bool scope, bool plain)
        {
            if (eaSkip || !plain) return kNone;
            if (!on) return kWould;
            return scope ? kGo : kExempt;
        }

        // Enough Sims wait at this rabbit hole's exit: all of them go now.
        public static bool Burst(int pending, int min) { return pending >= Min(min); }

        // A waiting Sim is part of a burst stamped at or after its wait began.
        public static bool Released(long burstStamp, long waitStart) { return burstStamp != 0 && burstStamp >= waitStart; }
    }
}
