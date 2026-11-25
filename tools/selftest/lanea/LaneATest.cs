// Offline self-check of the lane A managed consumers (mods/ts3fixes/src/LaneACore.cs, docs/321 §10): drop by lot low dword,
// overflow clears, other worlds' rows pruned, high water kept, and 50 world load / quit cycles with lot churn stay flat.
using System;
using System.Collections.Generic;
using Ts3Fixes;

static class LaneATest
{
    static int fails;
    static void Check(bool ok, string what) { if (!ok) { fails++; Console.WriteLine("FAIL " + what); } }

    struct Row : LaneACore.IWorldRow { public string W; public string WorldName { get { return W; } } }

    static readonly Dictionary<ulong, bool> env = new Dictionary<ulong, bool>();
    static readonly Dictionary<ulong, long> loop = new Dictionary<ulong, long>();
    static readonly LotHubCore.LotFn lotFn = delegate(int kind, uint lot) { if (kind != LotHubCore.Add) LaneACore.DropLot(env, kind == LotHubCore.Overflow, lot); };
    static readonly LotHubCore.WorldFn worldFn = delegate(bool load) { loop.Clear(); };

    static int Main()
    {
        // drop: only keys whose low dword is the lot's; a full 64-bit id with another high dword matches too (op 49 sends 32 bits)
        env[0x0000000100000010UL] = true; env[0x0000000200000010UL] = true; env[0x11UL] = true;
        Check(LaneACore.DropLot(env, false, 0x10) == 2 && env.Count == 1 && env.ContainsKey(0x11UL), "drop by low dword");
        Check(LaneACore.DropLot(env, false, 0x99) == 0 && env.Count == 1, "no row for that lot");
        env[0x12UL] = true;
        Check(LaneACore.DropLot(env, true, 0) == 2 && env.Count == 0, "overflow clears");

        // other worlds first
        Dictionary<ulong, Row> tp = new Dictionary<ulong, Row>();
        for (ulong i = 0; i < 10; i++) tp[i] = new Row { W = i < 4 ? "home" : "france" };
        Check(LaneACore.DropOtherWorlds(tp, "home") == 6 && tp.Count == 4, "rows of other worlds pruned, the current world's kept");

        // high water
        int[] c = new int[4];
        LaneACore.See(c, 0, 5); LaneACore.See(c, 0, 2);
        Check(c[0] == 2 && c[1] == 5, "size now and high water");

        // 50 load / quit cycles: re-subscribe each load (static delegates: one row each), 20 lots + 20 Sims per world, 5 lots unloaded
        LotHubCore.ResetForTest();
        int hw = 0;
        for (int cyc = 0; cyc < 50; cyc++)
        {
            LotHubCore.SubscribeLot(lotFn); LotHubCore.SubscribeWorld(worldFn);
            LotHubCore.World(true);
            for (uint l = 0; l < 20; l++) env[(ulong)(cyc * 100 + l)] = true;
            for (uint s = 0; s < 20; s++) loop[(ulong)(cyc * 1000 + s)] = cyc;
            for (uint l = 0; l < 5; l++) lotFn(LotHubCore.Remove, (uint)(cyc * 100 + l));
            if (env.Count > hw) hw = env.Count;
            LotHubCore.World(false);
            env.Clear();  // world quit (WorldQuitLeaks.cs L19)
            Check(loop.Count == 0 && env.Count == 0, "world quit leaves 0 rows, cycle " + cyc);
            Check(LotHubCore.Subscribers == 2, "subscribers flat, cycle " + cyc);
        }
        Check(hw == 15, "high water = one world's live rows (15), not the sum of 50 worlds");
        LotHubCore.ResetForTest();
        Console.WriteLine(fails == 0 ? "lanea selftest: ok" : "lanea selftest: " + fails + " failed");
        return fails == 0 ? 0 : 1;
    }
}
