using System;
using Ts3Fixes;

// Offline self-check of PetLongHomeCore (docs/645 §4).
static class PetLongHomeTest
{
    static int sFails;
    static void Check(bool ok, string what) { if (!ok) { sFails++; if (sFails < 20) Console.WriteLine("FAIL " + what); } }

    static int Pre(bool pet, bool exempt, bool held, bool fol, bool onLot, float dist, double min)
    {
        return PetLongHomeCore.Pre(pet, exempt, held, fol, onLot, dist, min, 150f, 120f);
    }

    static void Main()
    {
        const int Ok = PetLongHomeCore.kOk;
        // Pre(pet, exempt, held, followers, onLot, distM, tripMin) with 150 m / 120 sim-min
        Check(Pre(true, false, false, false, false, 400f, 0) == Ok, "NPC pet, 400 m away: long trip");
        Check(Pre(true, false, false, false, false, 40f, 130) == Ok, "NPC pet, 40 m but walking 130 sim-min: long trip");
        Check(Pre(true, false, false, false, false, 150f, 120) == PetLongHomeCore.kShort, "exactly 150 m and 120 min: short");
        Check(Pre(true, false, false, false, false, 40f, 10) == PetLongHomeCore.kShort, "short and new: EA's walk");
        Check(Pre(false, false, false, false, false, 400f, 999) == PetLongHomeCore.kNotPet, "a human: never");
        Check(Pre(true, true, false, false, false, 400f, 999) == PetLongHomeCore.kExempt, "household / selected / unknown pet: never");
        Check(Pre(true, true, true, true, true, 400f, 999) == PetLongHomeCore.kExempt, "exemption wins over every other gate");
        Check(Pre(true, false, true, false, false, 400f, 999) == PetLongHomeCore.kHeld, "carried, ridden, leashed: never");
        Check(Pre(true, false, false, true, false, 400f, 999) == PetLongHomeCore.kFollowers, "with followers: never");
        Check(Pre(true, false, false, false, true, 400f, 999) == PetLongHomeCore.kOnLot, "already on the lot: nothing to do");
        Check(PetLongHomeCore.Pre(true, false, false, false, false, 40f, 9999, 150f, 0f) == PetLongHomeCore.kShort, "time rule off (0 h): distance only");

        // Post(petUnseen, goalUnseen, spot)
        Check(PetLongHomeCore.Post(true, true, true) == Ok, "unseen pet, unseen lot, safe spot: placed");
        Check(PetLongHomeCore.Post(false, true, true) == PetLongHomeCore.kSeen, "pet seen: refused");
        Check(PetLongHomeCore.Post(true, false, true) == PetLongHomeCore.kGoalSeen, "lot seen: refused");
        Check(PetLongHomeCore.Post(true, true, false) == PetLongHomeCore.kNoSpot, "no safe spot: refused");
        int places = 0;
        for (int m = 0; m < 8; m++) if (PetLongHomeCore.Post((m & 1) != 0, (m & 2) != 0, (m & 4) != 0) == Ok) places++;
        Check(places == 1, "only all-unseen with a spot places");

        // trip table: per pet and lot, a new lot restarts the clock, Done ends it
        PetLongHomeCore t = new PetLongHomeCore();
        const long perMin = 10;
        Check(t.TripMinutes(1, 50, 1000, perMin) == 0, "first run: new trip");
        Check(Math.Abs(t.TripMinutes(1, 50, 1000 + 130 * perMin, perMin) - 130) < 1e-9, "same lot 130 min later: 130");
        Check(t.TripMinutes(1, 51, 3000, perMin) == 0, "another lot: new trip");
        Check(Math.Abs(t.TripMinutes(1, 51, 3600, perMin) - 60) < 1e-9, "the new trip's clock");
        Check(t.TripMinutes(1, 51, 2000, perMin) == 0, "clock went back: new trip");
        t.Done(1);
        Check(t.Count == 0 && t.TripMinutes(1, 51, 5000, perMin) == 0, "done: trip gone");
        t.TripMinutes(2, 50, 0, perMin);
        Check(t.Count == 2, "one entry per pet");
        for (ulong i = 10; t.Count < PetLongHomeCore.kMax; i++) t.TripMinutes(i, 50, 0, perMin);
        t.TripMinutes(9999, 50, 0, perMin);
        Check(t.Count == 1, "full table: cleared, new trip kept");

        Console.WriteLine(sFails == 0 ? "petlonghome selftest: ok" : "petlonghome selftest: " + sFails + " FAIL");
        Environment.Exit(sFails == 0 ? 0 : 1);
    }
}
