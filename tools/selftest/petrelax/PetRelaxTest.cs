// Usage: node tools/selftest/petrelax/run.ts  (compiles this with mods/ts3fixes/src/PetRelaxCore.cs and ShareLanesCore.cs)
// Offline self-check of PetRelaxCore.cs (docs/686 §11): who walks through whom, horses and objects kept, switch and
// scope, the lie-down rule, the lane-end distance test and the candidate spots. Exit code 1 on failure.
using System;
using Ts3Fixes;

static class PetRelaxTest
{
    static int sFails;
    static void Check(bool ok, string what) { if (!ok) { sFails++; Console.WriteLine("FAIL: " + what); } }

    const int N = PetRelaxCore.kNone, W = PetRelaxCore.kWould, X = PetRelaxCore.kExempt, G = PetRelaxCore.kGo;

    static PetRelaxCore.Case C(bool walkerPet, bool walkerLarge, int humans, int pets, int large, int others)
    {
        PetRelaxCore.Case k = new PetRelaxCore.Case();
        k.WalkerSmallPet = walkerPet; k.WalkerLarge = walkerLarge; k.Humans = humans; k.SmallPets = pets; k.Large = large; k.Others = others;
        k.WalkerInScope = true; k.AllInScope = true;
        return k;
    }

    static void Rules()
    {
        int kept;
        Check(PetRelaxCore.Pass(true, C(false, false, 0, 1, 0, 0), out kept) == G, "Sim, a cat in its way: walk through");
        Check(PetRelaxCore.Pass(true, C(false, false, 0, 3, 0, 0), out kept) == G, "Sim, three pets: walk through");
        Check(PetRelaxCore.Pass(true, C(true, false, 1, 0, 0, 0), out kept) == G, "pet, a Sim in its way: walk through");
        Check(PetRelaxCore.Pass(true, C(true, false, 2, 2, 0, 0), out kept) == G, "pet, Sims and pets: walk through");
        Check(PetRelaxCore.Pass(true, C(false, false, 1, 0, 0, 0), out kept) == N && kept == 0, "Sim and Sim: not ours");
        Check(PetRelaxCore.Pass(true, C(false, false, 1, 1, 0, 0), out kept) == N && kept == 0, "Sim with a pet and a Sim in its way: left to simpass / clipwait");
        Check(PetRelaxCore.Pass(true, C(true, false, 0, 0, 0, 0), out kept) == N && kept == 0, "nobody in the way: nothing");
        Check(PetRelaxCore.Pass(true, C(true, false, 0, 0, 0, 1), out kept) == N && kept == 0, "pet, only an object: nothing, not counted");
        Check(PetRelaxCore.Pass(true, C(true, false, 1, 0, 0, 1), out kept) == N && kept == PetRelaxCore.kKeptObject, "pet, Sim and object: kept");
        Check(PetRelaxCore.Pass(true, C(false, false, 0, 1, 0, 1), out kept) == N && kept == PetRelaxCore.kKeptObject, "Sim, pet and object: kept");
        Check(PetRelaxCore.Pass(true, C(true, false, 0, 0, 1, 0), out kept) == N && kept == PetRelaxCore.kKeptHorse, "pet, a horse in its way: kept");
        Check(PetRelaxCore.Pass(true, C(false, true, 0, 1, 0, 0), out kept) == N && kept == PetRelaxCore.kKeptHorse, "horse, a cat in its way: kept");
        Check(PetRelaxCore.Pass(true, C(false, false, 0, 1, 1, 0), out kept) == N && kept == PetRelaxCore.kKeptHorse, "Sim, a cat and a horse: kept");
        Check(PetRelaxCore.Pass(true, C(false, true, 1, 0, 0, 0), out kept) == N && kept == 0, "horse and Sim, no small pet: not ours");
        Check(PetRelaxCore.Pass(false, C(false, false, 0, 1, 0, 0), out kept) == W, "off: would");
        PetRelaxCore.Case k = C(false, false, 0, 1, 0, 0);
        k.AllInScope = false;
        Check(PetRelaxCore.Pass(true, k, out kept) == X, "household pet without its switch: exempt");
        k.AllInScope = true; k.WalkerInScope = false;
        Check(PetRelaxCore.Pass(true, k, out kept) == X, "household walker without its switch: exempt");
        Check(PetRelaxCore.Pass(false, k, out kept) == W, "off: would whatever the scope");

        Check(PetRelaxCore.LieDown(true, true, true) == G, "lie-down at a lane end: go");
        Check(PetRelaxCore.LieDown(true, true, false) == N, "lie-down elsewhere: nothing");
        Check(PetRelaxCore.LieDown(true, false, true) == X, "lie-down, household pet without its switch: exempt");
        Check(PetRelaxCore.LieDown(false, false, true) == W, "lie-down, off: would");
    }

    // every combination: go only with a small pet in the case, no large body, no object, on and in scope
    static void Sweep()
    {
        int kept, n = 0;
        for (int m = 0; m < 2 * 2 * 3 * 3 * 2 * 2 * 2 * 2 * 2; m++)
        {
            int v = m;
            bool wp = v % 2 == 1; v /= 2; bool wl = v % 2 == 1; v /= 2;
            int humans = v % 3; v /= 3; int pets = v % 3; v /= 3; int large = v % 2; v /= 2; int others = v % 2; v /= 2;
            bool on = v % 2 == 1; v /= 2; bool ws = v % 2 == 1; v /= 2; bool all = v % 2 == 1;
            if (wp && wl) continue;
            PetRelaxCore.Case k = C(wp, wl, humans, pets, large, others);
            k.WalkerInScope = ws; k.AllInScope = all;
            int d = PetRelaxCore.Pass(on, k, out kept);
            n++;
            bool petCase = humans + pets + large > 0 && (wp || pets > 0);
            if (d == G) Check(on && ws && all && petCase && !wl && large == 0 && others == 0 && (wp || humans == 0), "sweep: go only inside the rule (" + m + ")");
            if (d != N) Check(kept == 0, "sweep: a judged case has no kept reason (" + m + ")");
            if (!petCase) Check(d == N && kept == 0, "sweep: no pet, untouched (" + m + ")");
            if (petCase && (wl || large > 0)) Check(d == N && kept == PetRelaxCore.kKeptHorse, "sweep: large body kept (" + m + ")");
            if (!on) Check(d == N || d == W, "sweep: off never acts (" + m + ")");
        }
        Check(n > 500, "sweep ran");
    }

    static void Spots()
    {
        Check(PetRelaxCore.Near(10f, 0f, 10f, 9.5f, 0f, 10f, 1.25f), "0.5 m from a lane end: near");
        Check(!PetRelaxCore.Near(10f, 0f, 10f, 8.5f, 0f, 10f, 1.25f), "1.5 m: not near");
        Check(!PetRelaxCore.Near(10f, 3f, 10f, 9.5f, 0f, 10f, 1.25f), "the floor above: not near");
        Check(!PetRelaxCore.Near(10f, 0f, 10f, 10f, 0f, 10f, 0f), "clearance 0: never");
        Check(!PetRelaxCore.Near(10f, 0f, 10f, 10f, 0f, 10f, float.NaN), "clearance NaN: never");
        float x, z;
        Check(PetRelaxCore.Candidate(0, 10f, 10f, 9.5f, 10f, out x, out z) && Math.Abs(x - 12f) < 0.001f && Math.Abs(z - 10f) < 0.001f, "first candidate: 2 m straight away from the end");
        Check(PetRelaxCore.Candidate(7, 10f, 10f, 9.5f, 10f, out x, out z) && Math.Abs(x - 8f) < 0.001f && Math.Abs(z - 10f) < 0.001f, "eighth: 2 m towards it");
        Check(PetRelaxCore.Candidate(8, 10f, 10f, 9.5f, 10f, out x, out z) && Math.Abs(x - 13f) < 0.001f, "second ring: 3 m");
        Check(!PetRelaxCore.Candidate(-1, 0f, 0f, 1f, 1f, out x, out z) && !PetRelaxCore.Candidate(PetRelaxCore.kCandidates, 0f, 0f, 1f, 1f, out x, out z), "out of range: none");
        Check(PetRelaxCore.Candidate(0, 5f, 5f, 5f, 5f, out x, out z) && Math.Abs(x - 7f) < 0.001f && !float.IsNaN(z), "standing on the end itself: a direction is picked");
        // every candidate lies on its ring, and the 16 are distinct
        for (int i = 0; i < PetRelaxCore.kCandidates; i++)
        {
            PetRelaxCore.Candidate(i, 3f, -4f, 2.2f, -4.6f, out x, out z);
            float r = (float)Math.Sqrt((x - 3f) * (x - 3f) + (z + 4f) * (z + 4f));
            Check(Math.Abs(r - (i < 8 ? 2f : 3f)) < 0.001f, "candidate " + i + " on its ring");
            for (int j = 0; j < i; j++)
            {
                float x2, z2;
                PetRelaxCore.Candidate(j, 3f, -4f, 2.2f, -4.6f, out x2, out z2);
                Check(Math.Abs(x - x2) + Math.Abs(z - z2) > 0.1f, "candidates " + j + " and " + i + " differ");
            }
        }
    }

    // docs/686 §8.8: the walk-through is at once only near a portal, elsewhere after the shared grace
    static void Timing()
    {
        const int D = ShareLanesCore.kDefer;
        int kept;
        int cat = PetRelaxCore.Pass(true, C(false, false, 0, 1, 0, 0), out kept);    // Sim, a cat in its way
        int dog = PetRelaxCore.Pass(true, C(true, false, 1, 0, 0, 0), out kept);     // dog, a Sim in its way
        Check(ShareLanesCore.Timed(cat, true, 0f, 2.5f) == G && ShareLanesCore.Timed(dog, true, 0f, 2.5f) == G, "cat in a doorway, dog on the stairs: at once");
        Check(ShareLanesCore.Timed(cat, false, 0f, 2.5f) == D && ShareLanesCore.Timed(dog, false, 1f, 2.5f) == D, "open space or hallway before the grace: the wait goes on");
        Check(ShareLanesCore.Timed(cat, false, 2.5f, 2.5f) == G && ShareLanesCore.Timed(dog, false, 4f, 2.5f) == G, "from the grace on: walk through");
        Check(ShareLanesCore.Timed(PetRelaxCore.Pass(false, C(false, false, 0, 1, 0, 0), out kept), false, 0f, 2.5f) == D, "off, before the grace: not counted as would yet");
        Check(ShareLanesCore.Timed(PetRelaxCore.Pass(false, C(false, false, 0, 1, 0, 0), out kept), false, 3f, 2.5f) == W, "off, after the grace: would");
        Check(ShareLanesCore.Timed(PetRelaxCore.Pass(true, C(false, true, 0, 1, 0, 0), out kept), true, 9f, 2.5f) == N && kept == PetRelaxCore.kKeptHorse, "a horse stays kept at a portal and after the grace");
        Check(PetRelaxCore.kNone == ShareLanesCore.kNone && PetRelaxCore.kWould == ShareLanesCore.kWould && PetRelaxCore.kExempt == ShareLanesCore.kExempt && PetRelaxCore.kGo == ShareLanesCore.kGo,
            "the two rule files share their answer codes");
        // a plan that failed on a lying pet: the walker stands the grace, then plans through it
        int jig = PetRelaxCore.PlanJig(true, true, false, true, true, out kept);
        Check(jig == G && ShareLanesCore.Timed(jig, false, 0f, 2.5f) == D && ShareLanesCore.Timed(jig, false, 2.5f, 2.5f) == G, "pet across a hallway: not a failed route, the grace and then through");
        Check(ShareLanesCore.Timed(jig, true, 0f, 2.5f) == G, "pet lying down at a doorway after the route was made: at once");
        Check(PetRelaxCore.PlanJig(true, false, false, true, true, out kept) == N && kept == 0, "another blocker: not ours");
        Check(PetRelaxCore.PlanJig(true, true, true, true, true, out kept) == N && kept == PetRelaxCore.kKeptHorse, "a horse does not walk through a lying pet");
        Check(PetRelaxCore.PlanJig(false, true, false, false, false, out kept) == W && PetRelaxCore.PlanJig(true, true, false, false, true, out kept) == X
            && PetRelaxCore.PlanJig(true, true, false, true, false, out kept) == X, "failed plan on a pet: off would, walker or pet out of scope exempt");
        // a lying pet's jig is passed by routes only near a portal lane (door lane (0,0,0)-(0,0,2), 1.5 m)
        Check(ShareLanesCore.NearLane(0.5f, 0f, -1f, 0f, 0f, 0f, 0f, 0f, 2f, 1.5f) && !ShareLanesCore.NearLane(4f, 0f, 1f, 0f, 0f, 0f, 0f, 0f, 2f, 1.5f), "pet lying at a doorway: passed; in the middle of the room: walked around");
    }

    static int Main()
    {
        Rules(); Sweep(); Spots(); Timing();
        Console.WriteLine(sFails == 0 ? "petrelax selftest: all checks passed" : "petrelax selftest: " + sFails + " FAILED");
        return sFails == 0 ? 0 : 1;
    }
}
