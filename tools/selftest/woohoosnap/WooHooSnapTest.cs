using System;
using Ts3Fixes;

// Offline checks of WooHooSnapCore (woohoosnap / woohoosnaphh, pairsnap / pairsnaphh, docs/686 §20).
static class WooHooSnapTest
{
    static int sFail, sRun;

    static void Check(bool ok, string what)
    {
        sRun++;
        if (!ok) { sFail++; Console.WriteLine("FAIL " + what); }
    }

    static int Main()
    {
        const int Go = BlockedUseCore.kGo, Would = BlockedUseCore.kWould, Exempt = BlockedUseCore.kExempt;
        const int Keep = WooHooSnapCore.kKeep, Slot = WooHooSnapCore.kSlot, Point = WooHooSnapCore.kPoint;
        int why;

        // which interaction classes are part of a WooHoo
        Check(WooHooSnapCore.WooHooName("Sims3.Gameplay.Objects.Plumbing.Shower+WooHoo"), "shower WooHoo");
        Check(WooHooSnapCore.WooHooName("Sims3.Gameplay.Objects.ShelvesStorage.Wardrobe+WooHooInWardrobeB"), "wardrobe, partner's side");
        Check(WooHooSnapCore.WooHooName("Sims3.Gameplay.Objects.Plumbing.AllInOneBathroom+WoohooInAllInOneBathroom"), "lower-case h (all-in-one bathroom, leaf pile, cave)");
        Check(WooHooSnapCore.WooHooName("Sims3.Gameplay.Objects.Beds.TryForBaby") && WooHooSnapCore.WooHooName("Sims3.Gameplay.Objects.TombObjects.Sarcophagus+TryForBaby"), "try for baby");
        Check(WooHooSnapCore.WooHooName("Sims3.Gameplay.Objects.Elevator.ElevatorDoors+WooHoo"), "elevator: the object's class with the nested name");
        Check(!WooHooSnapCore.WooHooName("Sims3.Gameplay.Objects.Beds.EnterRelaxing") && !WooHooSnapCore.WooHooName("Sims3.Gameplay.Objects.Plumbing.Shower+TakeShower"), "plain bed and shower use is not a WooHoo by name");
        Check(!WooHooSnapCore.WooHooName(null) && !WooHooSnapCore.WooHooName(""), "no name");

        // failed plans that count
        Check(WooHooSnapCore.PlanKind(16) && WooHooSnapCore.PlanKind(10) && WooHooSnapCore.PlanKind(14) && WooHooSnapCore.PlanKind(15), "no path, no destination, slots or points obstructed");
        Check(WooHooSnapCore.PlanKind(17) && WooHooSnapCore.PlanKind(9) && WooHooSnapCore.PlanKind(8), "a Sim on the place, the start closed");
        Check(!WooHooSnapCore.PlanKind(18), "a locked door keeps the game's answer");
        Check(!WooHooSnapCore.PlanKind(3) && !WooHooSnapCore.PlanKind(1) && !WooHooSnapCore.PlanKind(0) && !WooHooSnapCore.PlanKind(2), "aborted, succeeded, unknown");
        Check(!WooHooSnapCore.PlanKind(11) && !WooHooSnapCore.PlanKind(12) && !WooHooSnapCore.PlanKind(13) && !WooHooSnapCore.PlanKind(5), "missing object, object without such slots");

        // scope
        Check(WooHooSnapCore.Decide(false, false, true, false) == Go, "two NPCs, switch on");
        Check(WooHooSnapCore.Decide(true, false, true, false) == Exempt && WooHooSnapCore.Decide(false, true, true, false) == Exempt, "a household Sim on either side needs the household switch");
        Check(WooHooSnapCore.Decide(true, false, false, true) == Go && WooHooSnapCore.Decide(true, true, true, true) == Go, "household switch");
        Check(WooHooSnapCore.Decide(false, false, false, true) == Would, "NPC pair with only the household switch");
        Check(WooHooSnapCore.Decide(false, false, false, false) == Would && WooHooSnapCore.Decide(true, true, false, false) == Would, "both off: would");

        // object groups
        Check(WooHooSnapCore.Group(true, "Sims3.Gameplay.Objects.Beds", "BedDouble") == WooHooSnapCore.kBed && WooHooSnapCore.Group(true, "x", "Tent") == WooHooSnapCore.kBed, "beds and tents");
        Check(WooHooSnapCore.Group(false, "Sims3.Gameplay.Objects.Plumbing", "Shower") == WooHooSnapCore.kPlumbing && WooHooSnapCore.Group(false, "Sims3.Gameplay.Objects.Plumbing.Mimics", "ShowerBasic") == WooHooSnapCore.kPlumbing
            && WooHooSnapCore.Group(false, "Sims3.Gameplay.Objects.PlumbingX", "Thing") == WooHooSnapCore.kOtherObject, "plumbing by namespace, Mimics classes with it");
        Check(WooHooSnapCore.Group(false, "Sims3.Store.Objects", "SaunaClassic") == WooHooSnapCore.kPlumbing, "sauna");
        Check(WooHooSnapCore.Group(false, "Sims3.Gameplay.Objects.Elevator", "ElevatorDoors") == WooHooSnapCore.kOtherObject && WooHooSnapCore.Group(false, null, null) == WooHooSnapCore.kOtherObject, "anything else");

        // where the Sim is put
        Check(WooHooSnapCore.Step(true, true, true, 0, false, false, out why) == Slot && why == WooHooSnapCore.kWhyNone, "a slot route: the slot");
        Check(WooHooSnapCore.Step(true, true, true, 2, true, false, out why) == Slot, "a slot wins over a place");
        Check(WooHooSnapCore.Step(true, true, true, 0, false, true, out why) == Slot, "a WooHoo social's slot route at an object");
        Check(WooHooSnapCore.Step(true, true, true, -1, true, false, out why) == Point, "a ring or point route: a place");
        Check(WooHooSnapCore.Step(true, true, true, -1, true, true, out why) == Keep && why == WooHooSnapCore.kWhySocial, "a social's walk to the partner is not ours");
        Check(WooHooSnapCore.Step(true, true, true, -1, false, false, out why) == Keep && why == WooHooSnapCore.kWhySpot, "nothing to stand on");
        Check(WooHooSnapCore.Step(true, true, false, 0, true, false, out why) == Keep && why == WooHooSnapCore.kWhyLot, "another lot");
        Check(WooHooSnapCore.Step(true, false, true, 0, true, false, out why) == Keep && why == WooHooSnapCore.kWhyPlan, "not a route failure");
        Check(WooHooSnapCore.Step(false, true, true, 0, true, false, out why) == Keep && why == WooHooSnapCore.kWhySim, "pets, riders");

        // paired socials: cover
        int contact = SocialReachCore.Cover(true, true, 1, false, true, false, false, false), talk = SocialReachCore.Cover(true, true, 0, false, true, false, false, false);
        Check(contact == SocialReachCore.kContact && WooHooSnapCore.PairCover(contact, true, false, false, false), "a paired social of two humans");
        Check(WooHooSnapCore.PairCover(SocialReachCore.Cover(true, true, 0, true, true, false, false, false), true, false, false, false), "a social with its own jig");
        Check(!WooHooSnapCore.PairCover(talk, true, false, false, false), "talk socials are socialreach's");
        Check(!WooHooSnapCore.PairCover(SocialReachCore.Cover(false, true, 1, false, true, false, false, false), true, false, false, false), "subclasses with their own steps");
        Check(!WooHooSnapCore.PairCover(contact, false, false, false, false) && !WooHooSnapCore.PairCover(contact, true, true, false, false), "pets, babies");
        Check(!WooHooSnapCore.PairCover(contact, true, false, true, false) && !WooHooSnapCore.PairCover(contact, true, false, false, true), "riders, swimmers");

        // paired socials: step
        const int PKeep = WooHooSnapCore.kPairKeep, PSnap = WooHooSnapCore.kPairSnap, PTurn = WooHooSnapCore.kPairTurn;
        Check(WooHooSnapCore.PairStep(1.5f, 0f, true, false, false, false, out why) == PSnap && why == WooHooSnapCore.kWhyNone, "1.5 m apart: snap");
        Check(WooHooSnapCore.PairStep(3f, 0f, true, false, false, false, out why) == PSnap, "3 m is in");
        Check(WooHooSnapCore.PairStep(3.01f, 0f, true, false, false, false, out why) == PKeep && why == WooHooSnapCore.kPairWhyFar, "beyond 3 m");
        Check(WooHooSnapCore.PairStep(1f, 0f, false, false, false, false, out why) == PKeep && why == WooHooSnapCore.kPairWhyLevel, "another level");
        Check(WooHooSnapCore.PairStep(1f, -0.2f, true, false, false, false, out why) == PKeep && why == WooHooSnapCore.kPairWhyLevel, "a step in height the game's own test refuses");
        Check(WooHooSnapCore.PairStep(1f, 0.13f, true, false, false, false, out why) == PSnap, "within the game's height tolerance");
        Check(WooHooSnapCore.PairStep(1f, 0f, true, true, false, false, out why) == PKeep && why == WooHooSnapCore.kPairWhyPosture, "sitting or lying");
        Check(WooHooSnapCore.PairStep(1f, 0f, true, false, true, false, out why) == PKeep && why == WooHooSnapCore.kPairWhyMoving, "the mover is on a walk");
        Check(WooHooSnapCore.PairStep(1f, 0f, true, false, false, true, out why) == PTurn, "the other still walks: only a turn");

        // the ring
        float x, z, fx, fz;
        WooHooSnapCore.Ring(10f, 20f, 12f, 20f, 0.7f, 0, out x, out z, out fx, out fz);
        Check(Math.Abs(x - 10.7f) < 1e-4f && Math.Abs(z - 20f) < 1e-4f && Math.Abs(fx + 1f) < 1e-4f && Math.Abs(fz) < 1e-4f, "first place: on the line to the mover, facing the Sim that stays");
        bool all = true, distinct = true;
        float[] xs = new float[WooHooSnapCore.kRing], zs = new float[WooHooSnapCore.kRing];
        for (int i = 0; i < WooHooSnapCore.kRing; i++)
        {
            WooHooSnapCore.Ring(10f, 20f, 11f, 21.5f, 0.6f, i, out x, out z, out fx, out fz);
            xs[i] = x; zs[i] = z;
            all &= WooHooSnapCore.Aligned(x, z, fx, fz, 10f, 20f, -fx, -fz, 0f, 0.6f);
            for (int j = 0; j < i; j++) distinct &= Math.Abs(xs[j] - x) + Math.Abs(zs[j] - z) > 0.1f;
        }
        Check(all, "every ring place passes the game's own alignment test");
        Check(distinct, "eight different places");
        WooHooSnapCore.Ring(10f, 20f, 12f, 20f, 1f, 7, out x, out z, out fx, out fz);
        Check(Math.Abs(x - 9f) < 1e-4f && Math.Abs(z - 20f) < 1e-4f, "last place: straight behind");
        WooHooSnapCore.Ring(10f, 20f, 10f, 20f, 1f, 0, out x, out z, out fx, out fz);
        Check(WooHooSnapCore.Aligned(x, z, fx, fz, 10f, 20f, -fx, -fz, 0f, 1f), "two Sims on one spot still get a place");

        // the game's alignment test
        Check(WooHooSnapCore.Aligned(0f, 0f, 1f, 0f, 0.7f, 0f, -1f, 0f, 0f, 0.7f), "face to face at the distance");
        Check(!WooHooSnapCore.Aligned(0f, 0f, 1f, 0f, 0.71f, 0f, -1f, 0f, 0f, 0.7f), "1 cm off");
        Check(!WooHooSnapCore.Aligned(0f, 0f, 1f, 0f, 0.7f, 0f, -0.99f, 0.14f, 0f, 0.7f), "not facing");
        Check(!WooHooSnapCore.Aligned(0f, 0f, 1f, 0f, 0.7f, 0f, -1f, 0f, 0.2f, 0.7f), "a step between");

        // jig plans through objects
        Check(WooHooSnapCore.ClipWalk(true, 2f, 6f) && WooHooSnapCore.ClipWalk(true, 6f, 6f), "a short plan through objects is walked");
        Check(!WooHooSnapCore.ClipWalk(true, 6.5f, 6f) && !WooHooSnapCore.ClipWalk(false, 0f, 6f), "a long one, or none (a wall)");

        Console.WriteLine(sFail == 0 ? "woohoosnap rules: " + sRun + " checks OK" : "woohoosnap rules: " + sFail + " of " + sRun + " FAILED");
        return sFail == 0 ? 0 : 1;
    }
}
