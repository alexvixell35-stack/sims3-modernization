namespace Ts3Fixes
{
    // Game-independent rules of FridgeShare.cs (fridgeshare / fridgesharehh, docs/686 §41), checked offline by
    // tools/selftest/fridgeshare.
    //
    // EA's fridge has one line (Fridge.Line, a SimQueue of 3, 15 sim-min) and one routing slot. The Sim at the head keeps the
    // turn from the moment WaitForTurn answers (before its walk) until its StandardExit, so a head that asked from 5-8 m away
    // holds the fridge for its whole walk while the Sim already standing at the fridge waits; after a grab the user stands on
    // the slot 1-2.4 sim-min (EA's start of the pushed continuation), and the next Sim stands behind it.
    // Rules:
    //   1. Short uses (Fridge_Have and its subclasses, Fridge_EatLeftovers, putting leftovers away) may overlap, two Sims at
    //      most. A long use (clean out bad food, anything else) never shares.
    //   2. Immersion first: off screen (camsafe: nobody sees the Sim, the fridge or the spots) the second Sim uses the fridge
    //      at once from a spot beside the slot. On screen it walks to the spot beside the slot, waits there facing the fridge
    //      and steps onto the slot with a real walk once the user is done and the slot is clear (no line spot 1.2-3 m away,
    //      no 1 sim-min line check, no standing behind the post-use stand). Nobody walks through a standing Sim.
    //   3. Line head away: a head that walks over (its fridge action runs, it is routing) and is still farther than
    //      kAtFridgeM from the slot after the shared grace (clipwait's kClipGraceMinutes) gives the turn to the Sim next in line
    //      when that Sim already stands at the fridge (within kThereM, not walking). The head keeps the place behind it. A head
    //      the player ordered keeps its turn (the player's order wins); a head gives way once per turn.
    public static class FridgeShareCore
    {
        public const int kNone = 0, kWould = 1, kExempt = 2, kCapped = 3, kAtOnce = 4, kBeside = 5, kGo = 6, kKept = 7;
        public const int kReady = 10, kStepIn = 11, kWait = 12;
        public const int kMaxUsers = 2;
        public const float kAtFridgeM = 1.5f, kThereM = 3.5f, kSideM = 0.75f, kSlotClearM = 0.5f, kSideArriveM = 0.4f;

        // Rule 1: may this Sim use the fridge next to the others? shortOthers / longOthers: other Sims using it (and Sims
        // let in that have not begun yet) by kind of use.
        public static bool Admit(int shortOthers, int longOthers)
        {
            return longOthers == 0 && shortOthers < kMaxUsers;
        }

        // Rules 1-2, when a Sim asks while the fridge is busy. waitingAhead: Sims in the line that do not use it yet (the
        // line goes first: no share past them). othersInScope: every other user is one the switches cover.
        public static int Share(bool on, bool inScope, bool othersInScope, int shortOthers, int longOthers, int waitingAhead, bool unseen)
        {
            if (shortOthers + longOthers == 0 || longOthers > 0 || waitingAhead > 0) return kNone;  // free, or EA's line
            if (!Admit(shortOthers, longOthers)) return kCapped;
            if (!inScope || !othersInScope) return kExempt;
            if (!on) return kWould;
            return unseen ? kAtOnce : kBeside;
        }

        // At the fridge (rule 2): atSide = the Sim stands beside the slot; atOnce = it may use the fridge from there.
        public static int Ready(bool atSide, bool atOnce, int shortOthers, int longOthers, bool slotClear)
        {
            if (atSide && atOnce) return Admit(shortOthers, longOthers) ? kReady : kWait;
            if (atSide) return shortOthers + longOthers == 0 && slotClear ? kStepIn : kWait;
            // on the slot: EA's rule (nobody else uses it), or one Sim that uses it at once from beside
            if (longOthers > 0) return kWait;
            if (shortOthers == 0) return kReady;
            return atOnce && Admit(shortOthers, longOthers) ? kReady : kWait;
        }

        // Rule 3, a waiter's per-minute check. waiterNext: the waiter is right behind the head.
        public static int Yield(bool on, bool inScope, bool headWalks, bool headPlayerOrder, bool headGaveWay, float headDistM, float heldMin,
            bool waiterNext, float waiterDistM, bool waiterRouting, float graceMin)
        {
            if (!waiterNext || waiterRouting || waiterDistM > kThereM || !headWalks || headDistM <= kAtFridgeM || heldMin < graceMin) return kNone;
            if (headGaveWay) return kNone;
            if (headDistM <= waiterDistM) return kNone;  // the head is not farther than the waiter: nothing to gain
            if (headPlayerOrder) return kKept;
            if (!inScope) return kExempt;
            return on ? kGo : kWould;
        }

        // The spot beside the slot: the side nearer to the Sim first (+1 = right of the slot's facing, -1 = left).
        public static int FirstSide(float distRight, float distLeft) { return distLeft < distRight ? -1 : 1; }

        // The slot is clear when no other Sim stands within kSlotClearM of it.
        public static bool SlotClear(float nearestOtherM) { return nearestOtherM < 0f || nearestOtherM > kSlotClearM; }

        // Room for a second Sim (the pick hooks of nextplan / facilityplan): nobody else at it or let in, or one short user
        // and nobody in the line.
        public static bool Room(int shortOthers, int longOthers, int waiting)
        {
            return longOthers == 0 && waiting == 0 && shortOthers < kMaxUsers;
        }
    }
}
