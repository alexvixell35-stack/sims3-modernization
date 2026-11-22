using System;
using System.Collections.Generic;
using Ts3Fixes;

// Offline self-check of mods/ts3fixes/src/SimbotWorldCore.cs (docs/870): EA's shipped shape test, the widen / undo list
// logic and EA's world test before and after, for Bridgeport (Downtown) and the controls.
static class SimbotWorldTest
{
    enum Wt { Undefined, Base, Vacation, Downtown, University, Future }
    enum Wn { Undefined, SunsetValley, NewDowntownWorld }

    static int sFail;
    static void Check(bool ok, string what) { if (!ok) { sFail++; Console.WriteLine("FAIL " + what); } }
    static List<Wt> L(params Wt[] a) { return new List<Wt>(a); }
    static string S(List<Wt> l) { return string.Join(",", Array.ConvertAll(l.ToArray(), x => x.ToString())); }

    static int Main()
    {
        List<Wn> none = new List<Wn>();
        Wt[] add = { Wt.Downtown, Wt.Future };

        // EaShape
        Check(SbwCore.EaShape(true, L(Wt.Base), Wt.Base, 0), "EA's shape: Allow [Base], no names");
        Check(!SbwCore.EaShape(false, L(Wt.Base), Wt.Base, 0), "not Allow");
        Check(!SbwCore.EaShape(true, L(Wt.Base, Wt.Downtown), Wt.Base, 0), "already widened (e.g. the MTS override)");
        Check(!SbwCore.EaShape(true, L(Wt.Downtown), Wt.Base, 0), "other single type");
        Check(!SbwCore.EaShape(true, L(), Wt.Base, 0), "no types");
        Check(!SbwCore.EaShape(true, null, Wt.Base, 0), "null types");
        Check(!SbwCore.EaShape(true, L(Wt.Base), Wt.Base, 1), "a world name listed");
        Check(!SbwCore.EaShape(true, L(Wt.Base), Wt.Base, -1), "null names");

        // the bug: EA's tuning refuses Make Simbot in Bridgeport, allows it in a Base world
        List<Wt> types = L(Wt.Base);
        Check(SbwCore.Blocks(true, false, types, none, Wt.Downtown, Wn.NewDowntownWorld), "off: refused in Bridgeport");
        Check(!SbwCore.Blocks(true, false, types, none, Wt.Base, Wn.SunsetValley), "off: allowed in a Base world");

        // on: widened, allowed in Downtown and Future, Base unchanged
        List<Wt> added = SbwCore.Widen(types, add);
        Check(S(types) == "Base,Downtown,Future" && S(added) == "Downtown,Future", "widen adds Downtown, Future: " + S(types));
        Check(!SbwCore.Blocks(true, false, types, none, Wt.Downtown, Wn.NewDowntownWorld), "on: allowed in Bridgeport");
        Check(!SbwCore.Blocks(true, false, types, none, Wt.Future, Wn.Undefined), "on: allowed in a Future world");
        Check(!SbwCore.Blocks(true, false, types, none, Wt.Base, Wn.SunsetValley), "on: still allowed in a Base world");
        Check(SbwCore.Blocks(true, false, types, none, Wt.Vacation, Wn.Undefined), "on: still refused on vacation");
        Check(!SbwCore.EaShape(true, types, Wt.Base, 0), "widened tuning is no longer EA's shape (no second widen)");

        // off again: exactly what was added goes
        Check(SbwCore.Unwiden(types, added) == 2 && S(types) == "Base", "undo removes exactly what was added: " + S(types));
        Check(SbwCore.Blocks(true, false, types, none, Wt.Downtown, Wn.NewDowntownWorld), "undone: refused in Bridgeport again");

        // a list that already had one of them keeps it on undo
        List<Wt> part = L(Wt.Base, Wt.Downtown);
        List<Wt> partAdded = SbwCore.Widen(part, add);
        Check(S(partAdded) == "Future" && SbwCore.Unwiden(part, partAdded) == 1 && S(part) == "Base,Downtown", "undo keeps a type we did not add");

        // controls: None never blocks; Disallow; a listed world name allows
        Check(!SbwCore.Blocks(false, false, null, null, Wt.Downtown, Wn.NewDowntownWorld), "None (MakeInvention) never refused");
        Check(SbwCore.Blocks(false, true, L(Wt.Downtown), none, Wt.Downtown, Wn.NewDowntownWorld), "Disallow [Downtown] refused in Downtown");
        Check(!SbwCore.Blocks(false, true, L(Wt.Downtown), none, Wt.Base, Wn.SunsetValley), "Disallow [Downtown] allowed in Base");
        Check(!SbwCore.Blocks(true, false, L(Wt.Base), new List<Wn> { Wn.NewDowntownWorld }, Wt.Downtown, Wn.NewDowntownWorld), "Allow by world name");

        Console.WriteLine(sFail == 0 ? "simbotworld selftest: all passed" : "simbotworld selftest: " + sFail + " FAILED");
        return sFail == 0 ? 0 : 1;
    }
}
