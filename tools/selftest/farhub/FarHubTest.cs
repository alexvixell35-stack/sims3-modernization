// Offline self-check of FarHub (mods/ts3fixes/src/FarHubCore.cs, docs/351 §4): lane order FS, FO, LZ on every event,
// quit = flush, release all, reset; pause gate; long-frame rule; shared 2 ms budget; a throwing lane does not block the others.
using System;
using System.Collections.Generic;
using Ts3Fixes;

static class FarHubTest
{
    static int fails;
    static void Check(bool ok, string what) { if (!ok) { fails++; Console.WriteLine("FAIL " + what); } }
    static readonly List<string> log = new List<string>();
    static string Log() { string s = string.Join(",", log.ToArray()); log.Clear(); return s; }
    static uint now;
    static uint Clock() { return now; }

    static void Reg(int owner, string n, uint stepUs, bool bad)
    {
        FarHubCore.Register(owner,
            delegate { log.Add(n + "load"); },
            delegate { log.Add(n + "flush(" + FarHoldCore.Lots + ")"); if (bad) throw new InvalidOperationException(); },
            delegate { log.Add(n + "reset(" + FarHoldCore.Lots + ")"); },
            delegate(FarHubCore.More more) { int k = 0; while (k < 3 && more()) { k++; now += stepUs; } log.Add(n + "step" + k); if (bad) throw new InvalidOperationException(); },
            delegate(ulong id) { log.Add(n + "gone" + id + "(" + (FarHoldCore.IsLotParked(id) ? "parked" : "free") + ")"); });
    }

    static int Main()
    {
        FarHubCore.Reset(); FarHoldCore.Reset();
        Check(!FarHubCore.Register(0, null, null, null, null, null) && !FarHubCore.Register(4, null, null, null, null, null), "owner tag out of range refused");
        // registered out of order: called in tag order
        Reg(FarHoldCore.OwnerLZ, "lz", 100, false); Reg(FarHoldCore.OwnerFO, "fo", 100, false); Reg(FarHoldCore.OwnerFS, "fs", 100, false);
        Check(FarHubCore.Owners == 3 && FarHubCore.Replaced == 0, "three lanes");
        FarHubCore.World(true);
        Check(Log() == "fsload,foload,lzload" && FarHubCore.World_ && FarHubCore.Loads == 1, "world load: FS, FO, LZ");

        // want flags
        Check(!FarHubCore.Wanted, "nothing wanted at start");
        FarHubCore.Want(FarHoldCore.OwnerFO, true);
        Check(FarHubCore.Wanted, "one lane wants the task");
        FarHubCore.Want(FarHoldCore.OwnerFO, false); FarHubCore.Want(0, true);
        Check(FarHubCore.Wanted, "farhold itself (owner 0) wants it");
        FarHubCore.Want(0, false);
        Check(!FarHubCore.Wanted, "all off");

        // frame: paused, long, normal
        FarHubCore.Frame(true, 16, Clock);
        Check(Log() == "" && FarHubCore.PausedFrames == 1 && FarHubCore.Steps == 0, "paused: no lane step");
        FarHubCore.Frame(false, 40, Clock);
        Check(Log() == "" && FarHubCore.OverFrames == 1 && FarHubCore.Steps == 0, "frame over 25 ms: no lane step");
        FarHubCore.Frame(false, 16, Clock);
        Check(Log() == "fsstep3,fostep3,lzstep3" && FarHubCore.Steps == 1 && FarHubCore.BudgetOut == 0, "normal frame: FS, FO, LZ inside the budget");
        for (int i = 0; i < FarHubCore.kOverRunEvery - 1; i++) FarHubCore.Frame(false, 40, Clock);
        Check(Log() == "" && FarHubCore.OverFrames == FarHubCore.kOverRunEvery, "long frames in a row are skipped");
        FarHubCore.Frame(false, 40, Clock);
        Check(Log() == "fsstep3,fostep2,lzstep0" && FarHubCore.Steps == 2 && FarHubCore.ForcedSteps == 1 && FarHubCore.BudgetOut == 1,
            "the " + FarHubCore.kOverRunEvery + "th long frame in a row steps, with the forced budget of " + FarHubCore.kForcedBudgetUs + " us");
        // the run of long frames restarts on pause, on a normal frame and on world quit
        int over = FarHubCore.OverFrames;
        for (int i = 0; i < FarHubCore.kOverRunEvery - 1; i++) FarHubCore.Frame(false, 40, Clock);
        FarHubCore.Frame(true, 40, Clock);
        for (int i = 0; i < FarHubCore.kOverRunEvery - 1; i++) FarHubCore.Frame(false, 40, Clock);
        FarHubCore.Frame(false, 16, Clock);
        Check(Log() == "fsstep3,fostep3,lzstep3" && FarHubCore.ForcedSteps == 1 && FarHubCore.OverFrames == over + 2 * (FarHubCore.kOverRunEvery - 1), "pause restarts the count: no forced step; the normal frame has the full budget");
        for (int i = 0; i < FarHubCore.kOverRunEvery - 1; i++) FarHubCore.Frame(false, 40, Clock);
        FarHubCore.World(false); FarHubCore.World(true); Log();
        for (int i = 0; i < FarHubCore.kOverRunEvery - 1; i++) FarHubCore.Frame(false, 40, Clock);
        Check(Log() == "" && FarHubCore.ForcedSteps == 1, "world quit restarts the count");
        FarHubCore.Frame(false, 16, Clock); Log();

        // shared budget: FS uses it all, the later lanes get no unit of work
        Reg(FarHoldCore.OwnerFS, "fs", 1000, false);
        Check(FarHubCore.Replaced == 1 && FarHubCore.Owners == 3, "second register for a tag replaces the first");
        FarHubCore.Frame(false, 16, Clock);
        Check(Log() == "fsstep2,fostep0,lzstep0" && FarHubCore.BudgetOut == 2, "2 ms budget is shared, FS first");
        now = 0xFFFFFF00u;  // clock wrap inside a frame
        Reg(FarHoldCore.OwnerFS, "fs", 100, false);
        FarHubCore.Frame(false, 16, Clock);
        Check(Log() == "fsstep3,fostep3,lzstep3", "clock wrap");

        // a lot goes: lanes first (they still see it parked), then the release
        FarHoldCore.ParkLot(5, FarHoldCore.OwnerFO); FarHoldCore.ParkLot(6, FarHoldCore.OwnerFO);
        FarHubCore.LotGone(5);
        Check(Log() == "fsgone5(parked),fogone5(parked),lzgone5(parked)" && !FarHoldCore.IsLotParked(5) && FarHoldCore.IsLotParked(6), "lot gone: lanes, then FarReleaseLot");

        // a throwing lane is counted; the others and the release still run
        Reg(FarHoldCore.OwnerFS, "fs", 100, true);
        FarHubCore.Frame(false, 16, Clock);
        Check(Log() == "fsstep3,fostep3,lzstep3" && FarHubCore.Errors == 1, "a lane that throws in its step does not stop the others");
        FarHubCore.World(false);
        Check(Log() == "fsflush(1),foflush(1),lzflush(1),fsreset(0),foreset(0),lzreset(0)" && FarHubCore.Errors == 2 && FarHoldCore.Lots == 0 && FarHoldCore.LastReleaseAll == "worldQuit" && !FarHubCore.World_ && FarHubCore.Quits == 2,
            "world quit: every flush, release all, every reset, also when a flush throws");

        // switch off / stand-down path
        FarHoldCore.ParkLot(9, FarHoldCore.OwnerFO);
        FarHubCore.Drop("switchOff");
        Check(FarHoldCore.Lots == 0 && FarHoldCore.LastReleaseAll == "switchOff", "drop: release all with its reason");
        Log();

        // 100 load / quit cycles: nothing accumulates
        for (int i = 0; i < 100; i++) { FarHubCore.World(true); FarHoldCore.ParkLot((ulong)i, FarHoldCore.OwnerFO); FarHubCore.World(false); }
        Log();
        Check(FarHubCore.Owners == 3 && FarHoldCore.Lots == 0 && FarHubCore.Loads == 102 && FarHubCore.Quits == 102, "load / quit cycles: tables empty, one entry per tag");
        FarHubCore.Reset(); FarHoldCore.Reset();
        Check(FarHubCore.Owners == 0 && !FarHubCore.Wanted, "reset");
        Console.WriteLine(fails == 0 ? "farhub selftest: ok" : "farhub selftest: " + fails + " failed");
        return fails == 0 ? 0 : 1;
    }
}
