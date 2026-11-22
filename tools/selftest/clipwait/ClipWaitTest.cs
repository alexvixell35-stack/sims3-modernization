// Usage: node tools/selftest/clipwait/run.ts  (compiles this with mods/ts3fixes/src/ClipWaitCore.cs)
// Offline self-check of ClipWaitCore.cs (docs/686 §9): the grace, what may be walked through, where a walk may end,
// switch and scope, and the rule against every combination of its inputs. Exit code 1 on failure.
using System;
using Ts3Fixes;

static class ClipWaitTest
{
    static int sFails;
    static void Check(bool ok, string what) { if (!ok) { sFails++; Console.WriteLine("FAIL: " + what); } }

    const int N = ClipWaitCore.kNone, W = ClipWaitCore.kWould, X = ClipWaitCore.kExempt, G = ClipWaitCore.kGo;

    static ClipWaitCore.Case C(float elapsed, int sims, int others, bool atDest, int destKind, bool inUse)
    {
        ClipWaitCore.Case k = new ClipWaitCore.Case();
        k.ElapsedMin = elapsed; k.GraceMin = 2.5f; k.Sims = sims; k.Others = others; k.SimAtDest = atDest; k.DestKind = destKind; k.DestInUse = inUse;
        k.SimsOnWay = atDest ? 0 : sims; k.ObjectDestAllowed = true; k.SimInScope = true; k.AllInScope = true;
        return k;
    }

    static int D(bool on, ClipWaitCore.Case k, out int kept, out bool dest) { return ClipWaitCore.Decide(on, k, out kept, out dest); }

    static void Rules()
    {
        int kept; bool dest;
        Check(D(true, C(3f, 1, 0, false, ClipWaitCore.kDestObject, false), out kept, out dest) == G && !dest, "busy Sim in the way of a route to an object: walk through");
        Check(D(true, C(1.9f, 1, 0, false, ClipWaitCore.kDestFloor, false), out kept, out dest) == N && kept == 0, "before the grace: nothing");
        Check(D(true, C(2.5f, 1, 0, false, ClipWaitCore.kDestFloor, false), out kept, out dest) == G, "at the grace (the shared kClipGraceMinutes, 2.5): go");
        Check(D(true, C(2f, 1, 0, false, ClipWaitCore.kDestFloor, false), out kept, out dest) == N && kept == ClipWaitCore.kKeptNot, "2 sim-minutes: still the game's wait");
        Check(D(true, C(9f, 0, 0, false, ClipWaitCore.kDestFloor, false), out kept, out dest) == N && kept == 0, "no Sim in the way: nothing");
        Check(D(true, C(9f, 0, 1, false, ClipWaitCore.kDestFloor, false), out kept, out dest) == N && kept == 0, "only an object in the way: not ours, not counted");
        Check(D(true, C(9f, 2, 1, false, ClipWaitCore.kDestFloor, false), out kept, out dest) == N && kept == ClipWaitCore.kKeptObject, "Sims and an object: kept");
        Check(D(true, C(3f, 1, 0, true, ClipWaitCore.kDestFloor, false), out kept, out dest) == G && dest, "floor spot taken by a Sim: accepted");
        Check(D(true, C(3f, 1, 0, true, ClipWaitCore.kDestObject, true), out kept, out dest) == N && kept == ClipWaitCore.kKeptInUse && !dest, "the Sim at the end uses the object: kept");
        Check(D(true, C(3f, 1, 0, true, ClipWaitCore.kDestObject, false), out kept, out dest) == G && dest, "the Sim at the object's spot does not use it: accepted");
        ClipWaitCore.Case k = C(3f, 1, 0, true, ClipWaitCore.kDestObject, false);
        k.ObjectDestAllowed = false;
        Check(D(true, k, out kept, out dest) == N && kept == ClipWaitCore.kKeptInUse, "object ends switched off: kept");
        Check(D(true, C(3f, 1, 0, true, ClipWaitCore.kDestSim, false), out kept, out dest) == N && kept == ClipWaitCore.kKeptSocial, "route to a Sim with a Sim at its end: kept");
        Check(D(true, C(3f, 1, 0, false, ClipWaitCore.kDestSim, false), out kept, out dest) == G && !dest, "route to a Sim, somebody in the way far from the end: walk through");
        k = C(3f, 1, 0, true, ClipWaitCore.kDestFloor, false); k.Social = true;
        Check(D(true, k, out kept, out dest) == N && kept == ClipWaitCore.kKeptSocial, "social with a Sim at the end: kept");
        k.SimAtDest = false;
        Check(D(true, k, out kept, out dest) == G, "social, way blocked: walk through");
        k = C(3f, 1, 0, true, ClipWaitCore.kDestFloor, false); k.PlayerGoHere = true;
        Check(D(true, k, out kept, out dest) == N && kept == ClipWaitCore.kKeptGoHere, "player Go Here ending in a Sim: kept");
        k.SimAtDest = false;
        Check(D(true, k, out kept, out dest) == G, "player Go Here, way blocked: walk through");
        k = C(3f, 2, 0, true, ClipWaitCore.kDestObject, true); k.SimsOnWay = 1;
        Check(D(true, k, out kept, out dest) == G && !dest && kept == 0, "a Sim on the way and another using the object at the end: pass the first, the end stays the game's");
        k = C(3f, 1, 0, false, ClipWaitCore.kDestFloor, false);
        Check(D(false, k, out kept, out dest) == W, "off: would");
        k.SimInScope = false;
        Check(D(true, k, out kept, out dest) == X && D(false, k, out kept, out dest) == W, "walker out of scope: exempt (off: would)");
        k.SimInScope = true; k.AllInScope = false;
        Check(D(true, k, out kept, out dest) == X, "a Sim in the way out of scope: exempt");
        k = C(3f, 1, 0, false, ClipWaitCore.kDestFloor, false); k.GraceMin = float.NaN;
        Check(D(true, k, out kept, out dest) == G, "a broken grace counts as 0");
        k.GraceMin = -5f;
        Check(D(true, k, out kept, out dest) == G, "a negative grace counts as 0");
    }

    // every combination: go never with an object, never before the grace, never out of scope, and a walk that ends in a
    // Sim only on a floor spot or at an object not in use, never for a social or a player's Go Here
    static void Sweep()
    {
        int cases = 0, go = 0;
        for (int m = 0; m < (1 << 12); m++)
            for (int destKind = 0; destKind < 3; destKind++)
            {
                ClipWaitCore.Case k = new ClipWaitCore.Case();
                k.ElapsedMin = (m & 1) != 0 ? 5f : 0.5f; k.GraceMin = 2.5f;
                k.Sims = (m & 2) != 0 ? 2 : 0; k.Others = (m & 4) != 0 ? 1 : 0;
                k.SimAtDest = (m & 8) != 0; k.DestKind = destKind; k.DestInUse = (m & 16) != 0; k.ObjectDestAllowed = (m & 32) != 0;
                k.Social = (m & 64) != 0; k.PlayerGoHere = (m & 128) != 0; k.SimInScope = (m & 256) != 0; k.AllInScope = (m & 512) != 0;
                bool on = (m & 1024) != 0;
                k.SimsOnWay = k.Sims > 0 && (!k.SimAtDest || (m & 2048) != 0) ? 1 : 0;
                int kept; bool dest;
                int d = ClipWaitCore.Decide(on, k, out kept, out dest);
                cases++;
                if (d == G) go++;
                Check(d != G || (on && k.Sims > 0 && k.Others == 0 && k.ElapsedMin >= k.GraceMin && k.SimInScope && k.AllInScope), "sweep " + m + "/" + destKind + ": go only on, Sims only, past the grace, in scope");
                Check(!dest || (k.SimAtDest && !k.Social && !k.PlayerGoHere && destKind != ClipWaitCore.kDestSim
                    && (destKind == ClipWaitCore.kDestFloor || (!k.DestInUse && k.ObjectDestAllowed))), "sweep " + m + "/" + destKind + ": where a walk may end");
                Check(d == N || dest || !k.SimAtDest || k.SimsOnWay > 0, "sweep " + m + "/" + destKind + ": a Sim at the end that may not be walked into is passed up to only with Sims on the way");
                Check(kept == 0 || d == N, "sweep " + m + "/" + destKind + ": kept means left to the game");
                Check(on || d != X, "sweep " + m + "/" + destKind + ": off never says exempt");
            }
        Check(go > 0 && go < cases, "sweep has both answers");
    }

    static int Main()
    {
        Rules();
        Sweep();
        Check(ClipWaitCore.Tenths(-1f) == 0 && ClipWaitCore.Tenths(float.NaN) == 0 && ClipWaitCore.Tenths(1.26f) == 13 && ClipWaitCore.Tenths(9999f) == 6000, "tenths");
        Console.WriteLine(sFails == 0 ? "clipwait selftest: ok" : "clipwait selftest: " + sFails + " FAILED");
        return sFails == 0 ? 0 : 1;
    }
}
