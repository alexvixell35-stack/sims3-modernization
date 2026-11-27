// Offline self-check of the FarHold owner table (mods/ts3fixes/src/FarHoldCore.cs, docs/346 §3, docs/351 §2): owner
// rule, conflict counter, release paths, Hook A decision table, leak churn.
using System;
using Ts3Fixes;

static class FarHoldTest
{
    static int fails;
    static void Check(bool ok, string what) { if (!ok) { fails++; Console.WriteLine("FAIL " + what); } }
    const int FS = FarHoldCore.OwnerFS, FO = FarHoldCore.OwnerFO, LZ = FarHoldCore.OwnerLZ;

    static int Main()
    {
        FarHoldCore.Reset();
        // owner rule: only FO parks lots, only FS holds Sims, LZ neither
        Check(FarHoldCore.ParkLot(7, FO) && FarHoldCore.IsLotParked(7) && FarHoldCore.Lots == 1 && FarHoldCore.LotOwner(7) == FO, "FO parks a lot");
        Check(FarHoldCore.ParkLot(7, FO) && FarHoldCore.Lots == 1 && FarHoldCore.Parks == 1 && FarHoldCore.OwnerConflict == 0, "same owner again: true, one row, no conflict");
        Check(!FarHoldCore.ParkLot(8, FS) && !FarHoldCore.ParkLot(8, LZ) && !FarHoldCore.ParkLot(8, 0) && !FarHoldCore.IsLotParked(8) && FarHoldCore.OwnerConflict == 3, "FS / LZ / no tag cannot park a lot");
        Check(FarHoldCore.HoldSim(7, FS) && FarHoldCore.IsSimHeld(7) && FarHoldCore.Sims == 1, "FS holds a Sim; the id spaces are separate");
        Check(!FarHoldCore.HoldSim(9, FO) && !FarHoldCore.IsSimHeld(9) && FarHoldCore.OwnerConflict == 4, "FO cannot hold a Sim");
        Check(FarHoldCore.ReleaseLot(7) && !FarHoldCore.IsLotParked(7) && FarHoldCore.IsSimHeld(7) && !FarHoldCore.ReleaseLot(7) && FarHoldCore.Releases == 1, "release a lot once; the Sim row stays");
        Check(FarHoldCore.ReleaseSim(7) && FarHoldCore.Sims == 0 && FarHoldCore.Releases == 2, "release the Sim");

        // release all
        FarHoldCore.ParkLot(1, FO); FarHoldCore.ParkLot(2, FO); FarHoldCore.HoldSim(3, FS);
        Check(FarHoldCore.ReleaseAll("worldQuit") == 3 && FarHoldCore.Lots == 0 && FarHoldCore.Sims == 0 && FarHoldCore.LastReleaseAll == "worldQuit" && FarHoldCore.ReleaseAlls == 1, "release all empties both sets");
        Check(FarHoldCore.ReleaseAll("switchOff") == 0 && FarHoldCore.ReleaseAlls == 2, "release all on empty sets");
        Check(FarHoldCore.LotsHigh == 2 && FarHoldCore.SimsHigh == 1, "high-water marks");

        // Hook A decision: Decide(on, shadow, parked, exempt, frozen)
        int S = FarHoldCore.Simulate;
        Check(FarHoldCore.Decide(true, false, false, false, false) == S && FarHoldCore.Decide(false, false, true, false, false) == S, "not parked, or switch off and no shadow: simulate");
        Check(FarHoldCore.Skipped == 0 && FarHoldCore.WouldSkip == 0 && FarHoldCore.Evicted == 0, "no counter moved");
        Check(FarHoldCore.Decide(true, false, true, false, false) == FarHoldCore.Skip && FarHoldCore.Skipped == 1, "on, parked, not exempt, running: skip");
        Check(FarHoldCore.Decide(false, true, true, false, false) == FarHoldCore.Would && FarHoldCore.WouldSkip == 1, "shadow: would, simulated");
        Check(FarHoldCore.Decide(true, true, true, false, false) == FarHoldCore.Would && FarHoldCore.Skipped == 1, "shadow wins over on");
        Check(FarHoldCore.Decide(true, false, true, true, false) == FarHoldCore.Evict && FarHoldCore.Decide(false, true, true, true, false) == FarHoldCore.Evict && FarHoldCore.Evicted == 2, "exempt lot (household rows): evict, live or shadow");
        Check(FarHoldCore.Decide(true, false, true, true, true) == FarHoldCore.Evict, "exempt beats frozen");
        Check(FarHoldCore.Decide(true, false, true, false, true) == S && FarHoldCore.Skipped == 1, "frozen (pause, CAS, Build / Buy): never skip");
        FarHoldCore.PassThrough("Sims3.Gameplay.Objects.Foo", "Callback");
        Check(FarHoldCore.IsPassThrough("Sims3.Gameplay.Objects.Foo", "Callback") && !FarHoldCore.IsPassThrough("Sims3.Gameplay.Objects.Foo", "Other") && FarHoldCore.PassThroughs == 1, "pass-through set by callback");
        Check(FarHoldCore.Decide(true, false, true, false, false) == S && FarHoldCore.PassBlocked == 1 && FarHoldCore.Skipped == 1, "pass-through rows and no take mode: a parked lot is simulated");

        // leak churn: 10,000 lots parked and released over 100 load / quit cycles; sizes back to 0
        FarHoldCore.Reset();
        for (int cycle = 0; cycle < 100; cycle++)
        {
            for (ulong i = 1; i <= 100; i++) { FarHoldCore.ParkLot(i * 31 + (ulong)cycle, FO); FarHoldCore.HoldSim(i, FS); }
            for (ulong i = 1; i <= 50; i++) FarHoldCore.ReleaseLot(i * 31 + (ulong)cycle);
            FarHoldCore.ReleaseAll("worldQuit");
        }
        Check(FarHoldCore.Lots == 0 && FarHoldCore.Sims == 0 && FarHoldCore.LotsHigh == 100 && FarHoldCore.Parks == 20000 && FarHoldCore.Releases == 20000 && FarHoldCore.OwnerConflict == 0, "churn: sizes 0, every add released");
        FarHoldCore.Reset();
        Check(FarHoldCore.Parks == 0 && FarHoldCore.PassThroughs == 0, "reset");
        Console.WriteLine(fails == 0 ? "farhold selftest: ok" : "farhold selftest: " + fails + " failed");
        return fails == 0 ? 0 : 1;
    }
}
