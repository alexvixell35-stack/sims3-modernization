using System;
using Ts3Fixes;

// Offline checks of ObjShareCore (linewaitlog, rhshare, servingshare, docs/686 §14).
static class ObjShareTest
{
    static int sFail, sRun;

    static void Check(bool ok, string what)
    {
        sRun++;
        if (!ok) { sFail++; Console.WriteLine("FAIL " + what); }
    }

    static int Main()
    {
        const int N = ObjShareCore.kNone, Go = ObjShareCore.kGo, W = ObjShareCore.kWould, Ex = ObjShareCore.kExempt, Keep = ObjShareCore.kKeep, Stale = ObjShareCore.kStale;

        // linewaitlog
        Check(ObjShareCore.LineOutcome(true, 0.7f, 10f) == ObjShareCore.kGotTurn, "got the turn");
        Check(ObjShareCore.LineOutcome(true, 12f, 10f) == ObjShareCore.kGotTurn, "got the turn on the last check");
        Check(ObjShareCore.LineOutcome(false, 10f, 10f) == ObjShareCore.kTimedOut && ObjShareCore.LineOutcome(false, 10.4f, 10f) == ObjShareCore.kTimedOut, "refused after the whole timeout");
        Check(ObjShareCore.LineOutcome(false, 4f, 10f) == ObjShareCore.kCancelled, "refused earlier: cancelled");
        Check(ObjShareCore.LineOutcome(false, 4f, 0f) == ObjShareCore.kCancelled, "no timeout given: cancelled");
        Check(!ObjShareCore.LineLogged(0f) && !ObjShareCore.LineLogged(0.49f) && ObjShareCore.LineLogged(0.5f) && ObjShareCore.LineLogged(9f), "half a sim-minute floor");
        Check(ObjShareCore.RhOutcome(0.2f) == ObjShareCore.kGotTurn && ObjShareCore.RhOutcome(14.9f) == ObjShareCore.kGotTurn && ObjShareCore.RhOutcome(15f) == ObjShareCore.kTimedOut, "enter slot: broken reservation after the stale age");
        Check(ObjShareCore.kOutcomeNames.Length == 4 && ObjShareCore.kOutcomeNames[ObjShareCore.kShared] == "shared" && ObjShareCore.kOutcomeNames[ObjShareCore.kTimedOut] == "timed out", "outcome names");

        // rhshare: on, held, simScope, simPlain, holderKnown, holderScope, holderPlain
        Check(ObjShareCore.Rh(true, true, true, true, true, true, true) == Go, "held by a Sim in scope: walk now");
        Check(ObjShareCore.Rh(false, true, true, true, true, true, true) == W, "off: would");
        Check(ObjShareCore.Rh(true, false, true, true, true, true, true) == N && ObjShareCore.Rh(false, false, true, true, false, false, false) == N, "free slot or own: the game's code");
        Check(ObjShareCore.Rh(true, true, false, true, true, true, true) == Ex, "household without its switch");
        Check(ObjShareCore.Rh(true, true, true, false, true, true, true) == N, "horse, rider, carrier, toddler: the game's wait");
        Check(ObjShareCore.Rh(true, true, true, true, true, false, true) == Keep, "holder out of scope: wait");
        Check(ObjShareCore.Rh(true, true, true, true, true, true, false) == Keep, "holder is a horse or rider: wait");
        Check(ObjShareCore.Rh(true, true, true, true, false, false, false) == Stale, "holder gone: reservation removed");
        Check(ObjShareCore.Rh(true, true, false, true, false, false, false) == Ex, "out of scope wins over a dead holder");
        Check(!ObjShareCore.RhBreak(0f) && !ObjShareCore.RhBreak(15f) && ObjShareCore.RhBreak(15.01f), "our wait breaks a reservation after the game's age");

        // servingshare: on, inInventory, simScope, othersScope
        Check(ObjShareCore.Serve(true, false, true, true) == Go, "in scope, others in scope");
        Check(ObjShareCore.Serve(false, false, true, true) == W, "off: would");
        Check(ObjShareCore.Serve(true, true, true, true) == N && ObjShareCore.Serve(false, true, true, true) == N, "from an inventory: no line there");
        Check(ObjShareCore.Serve(true, false, false, true) == Ex, "household without its switch");
        Check(ObjShareCore.Serve(true, false, true, false) == Keep, "a Sim out of scope in the line or using the platter");

        // the last serving
        Check(ObjShareCore.ServeLeft(1, 0, false), "one left, nobody taking");
        Check(!ObjShareCore.ServeLeft(1, 1, false), "one left, one being taken: refused");
        Check(ObjShareCore.ServeLeft(3, 2, false) && !ObjShareCore.ServeLeft(3, 3, false) && !ObjShareCore.ServeLeft(2, 5, false), "n left, m being taken");
        Check(!ObjShareCore.ServeLeft(0, 0, false) && !ObjShareCore.ServeLeft(-1, 0, false), "empty");
        Check(ObjShareCore.ServeLeft(1, -2, false), "a bad count is no reservation");
        Check(ObjShareCore.ServeLeft(0, 4, true), "a buffet never runs out");

        Console.WriteLine(sFail == 0 ? "objshare selftest: " + sRun + " checks ok" : "objshare selftest: " + sFail + " of " + sRun + " FAILED");
        return sFail == 0 ? 0 : 1;
    }
}
