using System;
using Ts3Fixes;

namespace Sims3.SimIFace
{
    // stand-in with the game type's full name (the rule matches by name, so it needs no game DLL)
    class SacsErrorException : Exception { public SacsErrorException(string m) : base(m) { } }
}

// Offline self-check of RabbitHoleDoorCore (docs/505): the swallow rule, and a model of EA's door pattern (count raised or
// lowered first, the request only at 0 -> 1 "Open" and 1 -> 0 "Closed") with the hooks' try/catch around it.
static class RabbitHoleDoorTest
{
    static int sFails;
    static void Check(bool ok, string what) { if (!ok) { sFails++; Console.WriteLine("FAIL " + what); } }

    static int[] sCounts = new int[10];
    static int sRequests, sSkipped;
    static bool sFail;
    static void Request() { sRequests++; if (sFail) throw new Sims3.SimIFace.SacsErrorException("SACS failed"); }
    static void EaOpen(int d) { sCounts[d]++; if (sCounts[d] == 1) Request(); }
    static void EaClose(int d) { sCounts[d]--; if (sCounts[d] == 0) Request(); }
    static void Hooked(Action<int> ea, int d, bool on)
    {
        try { ea(d); }
        catch (Exception e) { if (!RabbitHoleDoorCore.Swallow(e, on)) throw; sSkipped++; }
    }

    static void Main()
    {
        Exception sacs = new Sims3.SimIFace.SacsErrorException("x");
        Check(RabbitHoleDoorCore.Swallow(sacs, true), "on + SACS: skipped");
        Check(!RabbitHoleDoorCore.Swallow(sacs, false), "off + SACS: passes (EA)");
        Check(!RabbitHoleDoorCore.Swallow(new InvalidOperationException("x"), true), "on + other exception: passes");
        Check(!RabbitHoleDoorCore.Swallow(new NullReferenceException(), true), "on + NRE: passes");
        Check(!RabbitHoleDoorCore.Swallow(null, true), "null: passes");

        // switch on, every request fails: two Sims in and out by door 2; the count ends at 0 and nothing reaches the caller
        sFail = true;
        Hooked(EaOpen, 2, true); Hooked(EaOpen, 2, true); Hooked(EaClose, 2, true); Hooked(EaClose, 2, true);
        Check(sCounts[2] == 0, "on: count paired, got " + sCounts[2]);
        Check(sRequests == 2 && sSkipped == 2, "on: one open and one close request, both skipped (" + sRequests + "/" + sSkipped + ")");

        // switch off: the first open throws at the caller and the count stays raised (EA's leak: the Sim never closes it)
        sRequests = sSkipped = 0;
        bool threw = false;
        try { Hooked(EaOpen, 3, false); } catch (Sims3.SimIFace.SacsErrorException) { threw = true; }
        Check(threw, "off: the open threw at the caller");
        Check(sCounts[3] == 1, "off: the count stays raised, got " + sCounts[3]);
        Hooked(EaOpen, 3, false);  // the next Sim: count 2, no request, no throw
        Check(sRequests == 1, "off: the second open makes no request");

        // a request that works: nothing skipped, counts paired
        sFail = false; sRequests = sSkipped = 0; sCounts[4] = 0;
        Hooked(EaOpen, 4, true); Hooked(EaClose, 4, true);
        Check(sCounts[4] == 0 && sRequests == 2 && sSkipped == 0, "working door: two requests, none skipped");

        if (sFails > 0) { Console.WriteLine("rabbitholedoor selftest: " + sFails + " FAIL"); Environment.Exit(1); }
        Console.WriteLine("rabbitholedoor selftest: ok");
    }
}
