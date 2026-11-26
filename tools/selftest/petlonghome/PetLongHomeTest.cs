using System;
using Ts3Fixes;

// Offline self-check of PetLongHomeCore (docs/656 §8.2).
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

        // docs/656 §4.3 no-spot memo: per (lot, species class), live from noted to until, clock back drops it, bounded
        PetLongHomeCore n = new PetLongHomeCore();
        ulong horseLot = PetLongHomeCore.NoSpotKey(77, 1), catLot = PetLongHomeCore.NoSpotKey(77, 2);
        Check(horseLot != catLot && PetLongHomeCore.NoSpotKey(78, 1) != horseLot, "keys: lot and species class apart");
        Check(!n.NoSpotLive(horseLot, 100), "nothing noted: search");
        n.NoSpotNote(horseLot, 100, 0);
        Check(n.NoSpotCount == 0, "life 0 (switch value 0): nothing noted, as before");
        n.NoSpotNote(horseLot, 100, 600);
        Check(n.NoSpotLive(horseLot, 100) && n.NoSpotLive(horseLot, 699), "noted: refused without a search inside the window");
        Check(!n.NoSpotLive(catLot, 200), "a cat at the same lot still searches");
        Check(!n.NoSpotLive(horseLot, 700) && n.NoSpotCount == 0, "window over: entry gone, search again");
        n.NoSpotNote(horseLot, 1000, 600);
        Check(!n.NoSpotLive(horseLot, 900) && n.NoSpotCount == 0, "clock went back: dropped");
        for (ulong i = 0; n.NoSpotCount < PetLongHomeCore.kMax; i++) n.NoSpotNote(PetLongHomeCore.NoSpotKey(1000 + i, 1), 0, 10);
        n.NoSpotNote(PetLongHomeCore.NoSpotKey(5000, 1), 0, 10);
        Check(n.NoSpotCount == 1, "full memo: cleared, new entry kept");

        // herd spot share (docs/656 §4.5)
        PetLongHomeCore g = new PetLongHomeCore();
        ulong hk = PetLongHomeCore.NoSpotKey(90, PetLongHomeCore.SpeciesClass(true, false, false));
        float[] v = new float[6];
        Check(!g.GoodGet(hk, 0, v), "no spot shared yet");
        g.GoodNote(hk, 100, 0, 1, 2, 3, 0, 0, 1);
        Check(!g.GoodGet(hk, 100, v) && g.GoodCount == 0, "life 0: nothing shared, as before");
        g.NoSpotNote(hk, 90, 600);
        g.GoodNote(hk, 100, 600, 1, 2, 3, 0, 0, 1);
        Check(g.GoodGet(hk, 150, v) && v[0] == 1 && v[2] == 3 && v[5] == 1, "next herd member gets the verified spot and facing");
        Check(!g.NoSpotLive(hk, 150), "a found spot clears the no-spot entry");
        g.NoSpotNote(hk, 160, 600);
        Check(!g.NoSpotLive(hk, 170), "a late 'no spot' from a parallel search loses to the shared spot");
        Check(!g.GoodGet(hk, 700, v) && g.GoodCount == 0, "shared spot expires");
        g.GoodNote(hk, 1000, 600, 1, 2, 3, 0, 0, 1);
        Check(!g.GoodGet(hk, 900, v), "clock went back: dropped");
        g.GoodNote(hk, 0, 600, 1, 2, 3, 0, 0, 1); g.GoodDrop(hk);
        Check(!g.GoodGet(hk, 1, v), "dropped spot (failed the cheap checks) is gone");
        Check(PetLongHomeCore.SpeciesClass(true, false, false) != PetLongHomeCore.SpeciesClass(false, true, true)
            && PetLongHomeCore.SpeciesClass(false, true, true) != PetLongHomeCore.SpeciesClass(false, false, true)
            && PetLongHomeCore.SpeciesClass(false, false, true) != PetLongHomeCore.SpeciesClass(false, false, false), "horse, little dog, dog, cat apart");
        // a search in flight
        Check(!g.Busy(hk, 0, 100), "nothing running");
        g.Begin(hk, 10);
        Check(g.Busy(hk, 50, 100), "running in another task: wait");
        Check(!g.Busy(hk, 200, 100), "stale (task killed mid-search): not waited on, entry gone");
        g.Begin(hk, 300); g.End(hk);
        Check(!g.Busy(hk, 310, 100), "ended: not running");
        g.Begin(hk, 500);
        Check(!g.Busy(hk, 400, 100), "clock went back: not running");

        Console.WriteLine(sFails == 0 ? "petlonghome selftest: ok" : "petlonghome selftest: " + sFails + " FAIL");
        Environment.Exit(sFails == 0 ? 0 : 1);
    }
}
