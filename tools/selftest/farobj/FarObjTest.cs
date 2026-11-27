// Offline self-check of the far-object lot machine, FarLight part (mods/ts3fixes/src/FarObjModel.cs, docs/350 §4-§5,
// docs/358): dwell and hysteresis, never-far rows, the per-pulse household re-check, drops and leak churn.
using System;
using Ts3Fixes;

static class FarObjTest
{
    static int fails;
    static void Check(bool ok, string what) { if (!ok) { fails++; Console.WriteLine("FAIL " + what); } }
    const int L = FarObjModel.Live, P = FarObjModel.GoFarPending, F = FarObjModel.FarLight, X = FarObjModel.Excluded;
    const long kMin = 100, kDwell = 30 * kMin;  // test ticks per sim minute

    // Judge(lot, pass, now, dwell, edgeDist, excl, simNear, lightOn, x, y, z, r)
    static int J(ulong lot, int pass, long now, float dist, int excl, bool simNear, bool light) { return FarObjModel.Judge(lot, pass, now, kDwell, dist, excl, simNear, light, 1f, 2f, 3f, 20f); }
    // Pulse(lot, rec, now, frozen, scopeOk, hhLot, actors, edgeDist, live)
    static int Pu(ulong lot, long now, bool frozen, bool scopeOk, bool hhLot, int actors, float dist, bool live) { return FarObjModel.Pulse(lot, FarObjModel.Get(lot), now, frozen, scopeOk, hhLot, actors, dist, live); }

    static int Main()
    {
        FarObjModel.Reset(); FarHoldCore.Reset();
        // L1, dwell, L4
        Check(J(1, 1, 0, 180f, 0, false, true) == L && FarObjModel.Records == 0, "between the edges with no record: Live, no record");
        Check(J(1, 1, 0, 200f, 0, false, true) == L && FarObjModel.Records == 0, "exactly on the park edge: Live");
        Check(J(1, 1, 0, 250f, 0, true, true) == L && FarObjModel.Records == 0, "far but a Sim on or near it: Live");
        Check(J(1, 1, 0, 250f, 0, false, true) == P && FarObjModel.Records == 1, "L1: past the park edge");
        Check(J(1, 2, kDwell - 1, 250f, 0, false, true) == P && FarObjModel.LightLots == 0, "dwell not met");
        Check(J(1, 3, kDwell - 1, 170f, 0, false, true) == P, "between the edges: the dwell keeps running (hysteresis)");
        Check(Pu(1, kDwell - 1, false, true, false, 0, 250f, true) == FarObjModel.Run, "a pending lot never skips a pulse");
        Check(J(1, 4, kDwell, 250f, 0, false, true) == F && FarObjModel.LightLots == 1 && FarObjModel.LightIn == 1, "L4: dwell met, FarLight");
        FarObjModel.Rec rec = FarObjModel.Get(1);
        Check(rec != null && rec.X == 1f && rec.Y == 2f && rec.Z == 3f && rec.R == 20f && rec.Pass == 4, "the record keeps the lot's circle and the pass");

        // EP4 pulses
        Check(Pu(1, 5000, false, true, false, 0, 250f, true) == FarObjModel.Skip, "FarLight, all checks pass: skip");
        Check(Pu(1, 5000, false, false, true, 5, 0f, false) == FarObjModel.Skip, "same tick: the lot's three pulses share one answer");
        Check(Pu(1, 5001, false, true, false, 0, 250f, false) == FarObjModel.Would, "shadow: would");
        Check(Pu(1, 5002, true, true, false, 0, 250f, true) == FarObjModel.Run && FarObjModel.State(1) == F, "frozen: body runs, nothing changes");
        Check(Pu(1, 5004, false, true, false, 0, 250f, true) == FarObjModel.Skip && Pu(1, 5004, true, true, false, 0, 250f, true) == FarObjModel.Run,
            "frozen is tested before the cached answer of the tick (the sim tick stands still while paused)");
        // L6: the skipped zombie time is owed and handed to the caller when the lot leaves FarLight
        FarObjModel.OweZombie(FarObjModel.Get(1), 1.5f); FarObjModel.OweZombie(FarObjModel.Get(1), 2f); FarObjModel.OweZombie(FarObjModel.Get(1), -1f); FarObjModel.OweZombie(null, 1f);
        Check(FarObjModel.Get(1).Skipped && FarObjModel.Get(1).ZombieOwed == 3.5f && FarObjModel.LeftCount == 0, "skipped zombie time adds up on the record");
        Check(Pu(1, 5003, false, true, false, 1, 250f, true) == FarObjModel.Run && FarObjModel.State(1) == L && FarObjModel.SimArrive == 1 && FarObjModel.GoNear == 1 && FarObjModel.LightLots == 0, "a Sim arrived: body runs, record dropped (L6)");
        System.Collections.Generic.List<ulong> li = new System.Collections.Generic.List<ulong>(); System.Collections.Generic.List<float> lo = new System.Collections.Generic.List<float>();
        FarObjModel.TakeLeft(li, lo);
        Check(li.Count == 1 && li[0] == 1 && lo[0] == 3.5f && FarObjModel.LeftCount == 0, "L6 row: the lot and its owed zombie minutes, taken once");
        Check(Pu(1, 5003, false, true, false, 0, 250f, true) == FarObjModel.Run, "after the drop: runs");

        // household rows at a pulse: X1-X3 lot, scope unknown, viewer near
        for (int k = 0; k < 3; k++)
        {
            J(2, 1, 0, 300f, 0, false, true); J(2, 2, kDwell, 300f, 0, false, true);
            Check(FarObjModel.State(2) == F, "lot 2 FarLight again");
            int r = k == 0 ? Pu(2, 9000 + k, false, true, true, 0, 300f, true) : k == 1 ? Pu(2, 9000 + k, false, false, false, 0, 300f, true) : Pu(2, 9000 + k, false, true, false, 0, 149f, true);
            Check(r == FarObjModel.Run && FarObjModel.State(2) == L, "pulse check " + k + " (household lot / unknown scope / viewer near): body runs, lot leaves FarLight");
        }
        Check(FarObjModel.HhCaught == 2 && FarObjModel.NearCall == 1 && FarObjModel.LightLots == 0 && FarObjModel.Records == 0, "pulse leave counters");
        Check(FarObjModel.LeftCount == 0, "a lot that left FarLight with no pulse skipped (shadow, or caught on its first pulse) has no L6 row: it is as EA left it");
        // shadow never owes: would-skips leave Skipped false
        J(2, 1, 0, 300f, 0, false, true); J(2, 2, kDwell, 300f, 0, false, true);
        Check(Pu(2, 9100, false, true, false, 0, 300f, false) == FarObjModel.Would && !FarObjModel.Get(2).Skipped, "shadow would-skip does not mark the record");
        J(2, 3, kDwell + 1, 100f, 0, false, true);
        Check(FarObjModel.LeftCount == 0 && FarObjModel.Records == 0, "shadow go-near: no L6 row");

        // never-far rows in the pass (L11), household and other
        J(3, 1, 0, 300f, 0, false, true); J(3, 2, kDwell, 300f, 0, false, true);
        int gn = FarObjModel.GoNear;
        Check(J(3, 3, kDwell + 1, 300f, FarObjModel.X3, false, true) == X && FarObjModel.Records == 0 && FarObjModel.GoNear == gn + 1 && FarObjModel.ExclHousehold == 1, "L11: a household Sim on a FarLight lot excludes it");
        Check(J(3, 4, kDwell + 2, 300f, FarObjModel.X9, false, true) == X && FarObjModel.ExclOther == 1 && FarObjModel.Records == 0, "world lot: excluded, no record");
        Check(J(3, 4, kDwell + 2, 300f, FarObjModel.X8, false, true) == X && FarObjModel.ExclHousehold == 2, "X8 unknown scope counts as a household row");
        Check(J(3, 5, kDwell + 3, 300f, 0, false, true) == P, "L12: the row is gone, judged again from Live");
        Check(J(3, 6, kDwell + 4, 300f, 0, true, true) == L && FarObjModel.BlockSim == 1 && FarObjModel.Records == 0, "L2: a Sim near a pending lot");

        // L6 in the pass, farobjlight off, sweep, lot gone, drop all
        J(4, 1, 0, 300f, 0, false, true); J(4, 2, kDwell, 300f, 0, false, true);
        Check(J(4, 3, kDwell + 1, 149f, 0, false, true) == L && FarObjModel.Records == 0, "L6: a viewer inside the near edge");
        J(5, 1, 0, 300f, 0, false, false);
        Check(J(5, 2, kDwell, 300f, 0, false, false) == P && FarObjModel.LightLots == 0, "farobjlight off: stays GoFarPending");
        Check(J(5, 3, kDwell, 300f, 0, false, true) == F && Pu(5, 7000, false, true, false, 0, 300f, true) == FarObjModel.Skip, "light on: FarLight, a pulse skipped");
        FarObjModel.OweZombie(FarObjModel.Get(5), 4f);
        Check(J(5, 4, kDwell, 300f, 0, false, false) == P && FarObjModel.LightLots == 0 && FarObjModel.LeftCount == 1 && FarObjModel.Records == 1, "light off (the off arm of an A/B): back to pending with its record and its dwell kept, L6 row queued");
        li.Clear(); lo.Clear(); FarObjModel.TakeLeft(li, lo);
        Check(li[0] == 5 && lo[0] == 4f && !FarObjModel.Get(5).Skipped && FarObjModel.Get(5).ZombieOwed == 0f, "the L6 row carries the owed time; the record starts clean");
        Check(J(5, 4, kDwell, 300f, 0, false, true) == F && FarObjModel.LightLots == 1 && J(5, 4, kDwell, 300f, 0, false, false) == P, "light on again: FarLight on the next pass, no new dwell");
        Check(FarObjModel.NeedsNear(5, kDwell, kDwell) && !FarObjModel.NeedsNear(5, kDwell - 1, kDwell + 5000) && !FarObjModel.NeedsNear(99, 0, 0), "the slow near test is asked only for a lot at or past its dwell");
        J(6, 4, 0, 300f, 0, false, true);
        Check(FarObjModel.Records == 2 && FarObjModel.Sweep(4) == 0 && FarObjModel.Sweep(5) == 2 && FarObjModel.Records == 0 && FarObjModel.Gone == 2, "sweep: records the pass did not touch go (L13)");
        J(7, 1, 0, 300f, 0, false, true); FarHoldCore.ParkLot(7, FarHoldCore.OwnerFO);
        FarObjModel.LotGone(7);
        Check(FarObjModel.Records == 0 && !FarHoldCore.IsLotParked(7) && FarObjModel.Gone == 3, "lot gone: record dropped and the lot leaves FarHold in the same call");
        for (ulong i = 10; i < 20; i++) { J(i, 1, 0, 300f, 0, false, true); J(i, 2, kDwell, 300f, 0, false, true); }
        Check(FarObjModel.LightLots == 10 && FarObjModel.DropAll() == 10 && FarObjModel.Records == 0 && FarObjModel.LightLots == 0 && FarObjModel.Drops == 10 && FarObjModel.DropAll() == 0, "L14: drop all");

        // bands (docs/322)
        Check(FarObjModel.Band(0f) == 0 && FarObjModel.Band(50f) == 1 && FarObjModel.Band(149f) == 1 && FarObjModel.Band(150f) == 2 && FarObjModel.Band(400f) == 3 && FarObjModel.Band(1000f) == 4 && FarObjModel.Band(-5f) == 0, "bands 50 / 150 / 400 / 1000 m");

        // leak churn (docs/350 §13.3): FarLight / go-near of 10,000 lots, 100 load / quit cycles, sizes back to 0
        FarObjModel.Reset();
        for (ulong i = 1; i <= 10000; i++) { J(i, 1, 0, 300f, 0, false, true); J(i, 2, kDwell, 300f, 0, false, true); }
        Check(FarObjModel.Records == 10000 && FarObjModel.LightLots == 10000 && FarObjModel.RecordsHigh == 10000, "10,000 lots FarLight");
        for (ulong i = 1; i <= 10000; i++) J(i, 3, kDwell + 1, 100f, 0, false, true);
        Check(FarObjModel.Records == 0 && FarObjModel.LightLots == 0 && FarObjModel.GoNear == 10000 && FarObjModel.LeftCount == 0, "10,000 go-nears: sizes 0");
        // L6 rows nobody takes are capped
        for (ulong i = 1; i <= (ulong)FarObjModel.kLeftMax + 10; i++) { J(i, 1, 0, 300f, 0, false, true); J(i, 2, kDwell, 300f, 0, false, true); Pu(i, 100, false, true, false, 0, 300f, true); J(i, 3, kDwell + 1, 100f, 0, false, true); }
        Check(FarObjModel.LeftCount == 10 && FarObjModel.LeftLost == FarObjModel.kLeftMax && FarObjModel.Records == 0, "L6 rows are capped at " + FarObjModel.kLeftMax + " (counted as lost)");
        FarObjModel.ClearLeft();
        for (int c = 0; c < 100; c++)
        {
            for (ulong i = 1; i <= 50; i++) { J(i, 1, 0, 300f, 0, false, true); J(i, 2, kDwell, 300f, 0, false, i % 2 == 0); }
            FarObjModel.DropAll();
        }
        Check(FarObjModel.Records == 0 && FarObjModel.LightLots == 0 && FarObjModel.Drops == 5000, "100 load / quit cycles: sizes 0");
        FarObjModel.Reset(); FarHoldCore.Reset();
        Console.WriteLine(fails == 0 ? "farobj selftest: ok" : "farobj selftest: " + fails + " failed");
        return fails == 0 ? 0 : 1;
    }
}
