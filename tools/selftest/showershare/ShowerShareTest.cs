using System;
using Ts3Fixes;

// Offline checks of ShowerShareCore (showertogether, showerpee, docs/686 §15).
static class ShowerShareTest
{
    static int sFail, sRun;

    static void Check(bool ok, string what)
    {
        sRun++;
        if (!ok) { sFail++; Console.WriteLine("FAIL " + what); }
    }

    static int Main()
    {
        const int N = ShowerShareCore.kNone, Go = ShowerShareCore.kGo, W = ShowerShareCore.kWould, Ex = ShowerShareCore.kExempt,
            Rom = ShowerShareCore.kKeepRomance, Age = ShowerShareCore.kKeepAge, Other = ShowerShareCore.kKeepOther, NoSh = ShowerShareCore.kNoShower;

        // showertogether: on, others, otherShowering, simPlain, bothAdult, romantic, willing, simScope, otherScope
        Check(ShowerShareCore.Join(true, 1, true, true, true, true, true, true, true) == Go, "a partner in the shower: join");
        Check(ShowerShareCore.Join(false, 1, true, true, true, true, true, true, true) == W, "off: would");
        Check(ShowerShareCore.Join(true, 0, false, true, true, false, false, true, false) == N && ShowerShareCore.Join(false, 0, false, true, false, false, false, false, false) == N, "free shower: the game's code");
        Check(ShowerShareCore.Join(true, 2, true, true, true, true, true, true, true) == Other, "two other Sims: the line");
        Check(ShowerShareCore.Join(true, 1, false, true, true, true, true, true, true) == Other, "the partner bathes, repairs or has WooHoo: the line");
        Check(ShowerShareCore.Join(true, 1, true, false, true, true, true, true, true) == Other, "a robot or pet: the line");
        Check(ShowerShareCore.Join(true, 1, true, true, false, true, true, true, true) == Age, "a teen or child never joins");
        Check(ShowerShareCore.Join(false, 1, true, true, false, true, true, true, true) == Age, "age is kept with the switch off too (no would)");
        Check(ShowerShareCore.Join(true, 1, true, true, true, false, true, true, true) == Rom, "not romantic: the line");
        Check(ShowerShareCore.Join(true, 1, true, true, true, true, false, true, true) == Rom, "romantic but disliked: the line");
        Check(ShowerShareCore.Join(true, 1, true, true, true, true, true, false, true) == Ex, "household Sim without its switch");
        Check(ShowerShareCore.Join(true, 1, true, true, true, true, true, true, false) == Ex, "household partner without its switch");
        Check(ShowerShareCore.Join(true, 1, true, true, false, false, true, false, false) == Age, "age wins over romance and scope");
        Check(ShowerShareCore.Willing(0f) && ShowerShareCore.Willing(60f) && !ShowerShareCore.Willing(-0.1f), "willing: liking not below zero");

        // the exit, one at a time
        Check(ShowerShareCore.ExitWait(ShowerShareCore.kExiting, true, 0f) && ShowerShareCore.ExitWait(ShowerShareCore.kExiting, true, 9.9f), "partner plays its exit: wait");
        Check(!ShowerShareCore.ExitWait(ShowerShareCore.kJoined, true, 0f), "partner still showers: go");
        Check(!ShowerShareCore.ExitWait(ShowerShareCore.kLeaving, true, 0f), "partner asked to leave but has not begun: go (no deadlock of two)");
        Check(!ShowerShareCore.ExitWait(ShowerShareCore.kExiting, false, 1f), "partner is out: go");
        Check(!ShowerShareCore.ExitWait(ShowerShareCore.kExiting, true, ShowerShareCore.kExitCapMinutes), "an exit that never ends stops holding");
        Check(ShowerShareCore.UseOk(0, 0) && ShowerShareCore.UseOk(1, 1) && ShowerShareCore.UseOk(2, 2), "use list holds only the pair");
        Check(!ShowerShareCore.UseOk(1, 0) && !ShowerShareCore.UseOk(2, 1) && !ShowerShareCore.UseOk(3, 2), "a stranger in the use list: in use");

        // showerpee: toilets and showers
        Check(ShowerShareCore.ToiletUsable(false, false, 0, true), "a free toilet");
        Check(!ShowerShareCore.ToiletUsable(true, false, 0, true), "broken");
        Check(!ShowerShareCore.ToiletUsable(false, true, 0, true), "in use by another Sim");
        Check(!ShowerShareCore.ToiletUsable(false, false, 2, true), "queued for");
        Check(!ShowerShareCore.ToiletUsable(false, false, 0, false), "a urinal for a female Sim");
        Check(ShowerShareCore.ShowerUsable(false, 0, N), "a free shower");
        Check(!ShowerShareCore.ShowerUsable(true, 0, N), "a broken shower");
        Check(ShowerShareCore.ShowerUsable(false, 1, Go), "a partner's shower with showertogether");
        Check(!ShowerShareCore.ShowerUsable(false, 1, W) && !ShowerShareCore.ShowerUsable(false, 1, Rom) && !ShowerShareCore.ShowerUsable(false, 1, Age) && !ShowerShareCore.ShowerUsable(false, 2, Other)
            && !ShowerShareCore.ShowerUsable(false, 1, Ex), "a shower with a Sim in it that may not be joined: privacy kept");

        // showerpee: on, simOk, toiletUsable, showerUsable, simScope
        Check(ShowerShareCore.Pee(true, true, false, true, true) == Go, "no usable toilet, a shower: shower");
        Check(ShowerShareCore.Pee(false, true, false, true, true) == W, "off: would");
        Check(ShowerShareCore.Pee(true, true, true, true, true) == N && ShowerShareCore.Pee(false, true, true, false, true) == N, "a usable toilet: the game's way");
        Check(ShowerShareCore.Pee(true, false, false, true, true) == N, "a pet, toddler, robot or swimmer: the game's way");
        Check(ShowerShareCore.Pee(true, true, false, false, true) == NoSh && ShowerShareCore.Pee(false, true, false, false, false) == NoSh, "no shower either: the game's failure");
        Check(ShowerShareCore.Pee(true, true, false, true, false) == Ex, "household without its switch");

        // the failure is held back only while the shower can still happen
        Check(ShowerShareCore.PeeHolds(true, false, 0f) && ShowerShareCore.PeeHolds(true, false, 29.9f), "queued and young: held");
        Check(!ShowerShareCore.PeeHolds(true, false, ShowerShareCore.kPeeGiveUpMinutes), "queued too long: given back");
        Check(!ShowerShareCore.PeeHolds(false, false, 1f) && !ShowerShareCore.PeeHolds(false, true, 1f), "the shower left the queue: given back");
        Check(ShowerShareCore.PeeHolds(true, true, 120f), "filling: held however long the shower takes");
        Check(ShowerShareCore.kPeeFillMinutes > 0f && ShowerShareCore.kExitCapMinutes > 0f, "times are positive");

        Console.WriteLine(sFail == 0 ? "showershare selftest: " + sRun + " checks ok" : "showershare selftest: " + sFail + " of " + sRun + " FAILED");
        return sFail == 0 ? 0 : 1;
    }
}
