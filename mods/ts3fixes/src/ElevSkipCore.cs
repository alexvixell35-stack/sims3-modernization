namespace Ts3Fixes
{
    // Game-independent rules of elevskip / elevskiphh / elevskiphhunseen (ElevSkip.cs, docs/686 §33). Offline selftest: tools/selftest/elevskip.
    //
    // A Sim the elevator doors refused stands in the doors' line loop (EA: AcquireLaneRouteAction.WaitInLine, asked again
    // every quarter sim-minute) until the one car of the column is free on its floor. The fix lets such a Sim go through the
    // elevator as a portal: it is set at the doors of the floor its route leaves the elevator on, and the game replans from
    // there (the sequence of EA's own teleport skip, ElevatorDoorsPortalComponent.TryTeleportSkip, which sets the Sim into
    // the car instead).
    public static class ElevSkipCore
    {
        public const int kNone = 0, kGo = 1, kWould = 2, kExempt = 3, kSeen = 4, kRepeat = 5, kWait = 6;

        // on: elevskip; hhOn: elevskiphh (household and selected Sims, and NPCs in view, after `wait` sim-minutes).
        // plain: a human child or older on foot, carrying no child, no exit reason, not riding the elevator for its own sake.
        // npc: not a household or selected Sim. unseen: the Sim and the doors it would be set at are off screen and away
        // from every viewer. waited: sim-minutes this Sim has stood at these doors. recent: this Sim was set through an
        // elevator less than kElevSkipRepeatMin sim-minutes ago (a replan that wants the elevator again keeps EA's wait).
        public static int Decide(bool on, bool hhOn, bool plain, bool npc, bool unseen, float waited, float wait, bool recent)
        {
            return Decide(on, hhOn, false, plain, npc, false, unseen, waited, wait, recent);
        }

        // hhUnseenOn: elevskiphhunseen (acts only with elevskip + elevskiphh). selected: the selected Sim, never unseen.
        // With it, an unseen household Sim goes at once like an unseen NPC; the caller then computes `unseen` for household
        // Sims too (the Sim and the far doors off screen, away from viewers).
        public static int Decide(bool on, bool hhOn, bool hhUnseenOn, bool plain, bool npc, bool selected, bool unseen, float waited, float wait, bool recent)
        {
            if (!plain) return kNone;
            if (!on) return kWould;
            if (recent) return kRepeat;
            if (GoesUnseen(hhOn, hhUnseenOn, npc, selected, unseen)) return kGo;
            if (!hhOn) return npc ? kSeen : kExempt;
            return waited >= wait ? kGo : kWait;
        }

        // The Sim goes at once with no fade: an unseen NPC, or with elevskiphh + elevskiphhunseen an unseen household Sim
        // that is not the selected Sim.
        public static bool GoesUnseen(bool hhOn, bool hhUnseenOn, bool npc, bool selected, bool unseen)
        {
            if (!unseen || selected) return false;
            return npc || (hhOn && hhUnseenOn);
        }

        // A Sim set through the elevator fades out and in (EA's skip does) unless nobody can see it.
        public static bool Fade(bool npc, bool unseen) { return !GoesUnseen(false, false, npc, false, unseen); }
        public static bool Fade(bool hhOn, bool hhUnseenOn, bool npc, bool selected, bool unseen) { return !GoesUnseen(hhOn, hhUnseenOn, npc, selected, unseen); }

        // The route's elevator part: kinds of the portal paths after the Sim's own doors, in order. 1 = a car (interior)
        // hop of this column, 2 = elevator doors, 0 = anything else (another portal or a walk). The ride ends at the first
        // doors after at least one car hop; returns its index, or -1 (no ride found: EA's wait).
        public const int kStepOther = 0, kStepCar = 1, kStepDoors = 2;
        public static int ExitDoors(int[] steps, int n)
        {
            bool car = false;
            for (int i = 0; i < n; i++)
            {
                if (steps[i] == kStepCar) { car = true; continue; }
                if (steps[i] == kStepDoors) return car ? i : -1;
                if (car) return -1;  // the ride is broken by another portal or a walk: not ours
            }
            return -1;
        }

        // One launch of a car (elevtriplog): the riders' returns from the car's wait belong to one launch when they come
        // within `gapMin` sim-minutes of each other at the same car.
        public static bool SameLaunch(bool sameCar, float minutesSinceLast, float gapMin) { return sameCar && minutesSinceLast >= 0f && minutesSinceLast < gapMin; }
    }
}
