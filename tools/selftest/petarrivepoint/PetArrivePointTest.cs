using System;
using Ts3Fixes;

// Offline self-check of PetArrivePointCore (docs/656 §4.1): every input combination against the written rule.
static class PetArrivePointTest
{
    static int sFails;
    static void Check(bool ok, string what) { if (!ok) { sFails++; if (sFails < 20) Console.WriteLine("FAIL " + what); } }

    static void Main()
    {
        const int Ea = PetArrivePointCore.kEa, Skip = PetArrivePointCore.kSkip, Ex = PetArrivePointCore.kExempt, No = PetArrivePointCore.kNothing;
        // the cases from the doc
        Check(PetArrivePointCore.Decide(true, false, false, false, true, false, true, true) == Skip, "NPC horse, front door refuses it, cached inside point: skipped");
        Check(PetArrivePointCore.Decide(true, false, false, false, true, false, false, false) == Skip, "same, lot not validated yet: skipped, cache left for the next Sim");
        Check(PetArrivePointCore.Decide(false, false, false, false, true, false, true, true) == Ea, "a human: EA");
        Check(PetArrivePointCore.Decide(true, true, false, false, true, false, true, true) == Ex, "household / selected / unknown scope: EA, counted exempt");
        Check(PetArrivePointCore.Decide(true, false, false, false, true, true, true, true) == Ea, "door lets the animal through (cat, open door): EA");
        Check(PetArrivePointCore.Decide(true, false, false, false, false, false, true, true) == Ea, "no front door: EA");
        Check(PetArrivePointCore.Decide(true, false, false, true, true, false, true, true) == Ea, "apartment lot (portal-based point): EA");
        Check(PetArrivePointCore.Decide(true, false, true, false, true, false, true, true) == Ea, "world lot: EA");
        Check(PetArrivePointCore.Decide(true, false, false, false, true, false, true, false) == No, "validated with no point: EA already skips");

        // exhaustive: Skip only for an animal, not exempt, not world, not apartments, with a front door that refuses it,
        // and something to skip (not validated, or a cached point)
        for (int m = 0; m < 256; m++)
        {
            bool animal = (m & 1) != 0, exempt = (m & 2) != 0, world = (m & 4) != 0, apt = (m & 8) != 0, door = (m & 16) != 0,
                allowed = (m & 32) != 0, validated = (m & 64) != 0, point = (m & 128) != 0;
            int d = PetArrivePointCore.Decide(animal, exempt, world, apt, door, allowed, validated, point);
            bool target = animal && !world && !apt && !(validated && !point) && door && !allowed;
            Check((d == Skip) == (target && !exempt), "skip iff target and not exempt, mask " + m);
            Check((d == Ex) == (target && exempt), "exempt iff target and exempt, mask " + m);
            Check(PetArrivePointCore.Name(d) != "?", "named, mask " + m);
        }

        Console.WriteLine(sFails == 0 ? "petarrivepoint selftest: ok" : "petarrivepoint selftest: " + sFails + " FAIL");
        Environment.Exit(sFails == 0 ? 0 : 1);
    }
}
