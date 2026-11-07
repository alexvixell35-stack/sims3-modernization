// Usage: node tools/selftest/autoffload/run.ts  (compiles this with mods/ts3fixes/src/AutOffloadCore.cs)
// Offline self-check of AutOffloadCore.SeqClass (docs/208 broadcaster Mode R classes) and InsertTop (the env due pick,
// docs/208 §8.1). Exit code 1 on failure.
using System;
using Ts3Fixes;

static class AutOffloadTest
{
    static int sFail;
    static void Check(string what, int got, int want) { if (got != want) { sFail++; Console.WriteLine("FAIL " + what + ": " + got + " want " + want); } }

    // docs/208 §9.2 closest-lot rule on a lot from (0,0) to (10,20)
    static void LotChecks()
    {
        Check("on the lot", AutOffloadCore.LotCell(5, 5, 0, 0, 10, 20, 0), 4);
        Check("on the edge counts as on", AutOffloadCore.LotCell(10, 20, 0, 0, 10, 20, 0), 4);
        Check("top left", AutOffloadCore.LotCell(-1, 21, 0, 0, 10, 20, 0), 0);
        Check("top centre", AutOffloadCore.LotCell(5, 21, 0, 0, 10, 20, 0), 1);
        Check("top right", AutOffloadCore.LotCell(11, 21, 0, 0, 10, 20, 0), 2);
        Check("left", AutOffloadCore.LotCell(-1, 5, 0, 0, 10, 20, 0), 3);
        Check("right", AutOffloadCore.LotCell(11, 5, 0, 0, 10, 20, 0), 5);
        Check("bottom left", AutOffloadCore.LotCell(-1, -1, 0, 0, 10, 20, 0), 6);
        Check("bottom centre", AutOffloadCore.LotCell(5, -1, 0, 0, 10, 20, 0), 7);
        Check("bottom right", AutOffloadCore.LotCell(11, -1, 0, 0, 10, 20, 0), 8);
        Check("houseboat pad", AutOffloadCore.LotCell(-5, 25, 0, 0, 10, 20, 10), 4);
        int[] want = { 100, 16, 200, 1, 0, 9, 300, 4, 400 };  // cells 0-8 with dxo 1, dzo 2, dxe 3, dze 4
        for (int i = 0; i < 9; i++) Check("dist cell " + i, (int)AutOffloadCore.LotCellDist(i, 1, 2, 3, 4, 100, 200, 300, 400), want[i]);
        float best = float.MaxValue;
        Check("first lot taken", AutOffloadCore.LotPickStep(5, 9f, false, ref best), 1);
        Check("farther kept out", AutOffloadCore.LotPickStep(3, 16f, false, ref best), 0);
        Check("a tie keeps the earlier", AutOffloadCore.LotPickStep(3, 9f, false, ref best), 0);
        Check("nearer taken", AutOffloadCore.LotPickStep(3, 4f, false, ref best), 1);
        Check("on a lot: pick and focus", AutOffloadCore.LotPickStep(4, 0f, false, ref best), 2);
        Check("second lot under the target keeps the first", AutOffloadCore.LotPickStep(4, 0f, false, ref best), 0);
        Check("a houseboat lot under the target wins", AutOffloadCore.LotPickStep(4, 0f, true, ref best), 2);
        Check("nothing beats distance 0", AutOffloadCore.LotPickStep(3, 0f, false, ref best), 0);
        best = 9f;
        Check("no corners counts as distance 0", AutOffloadCore.LotPickStep(-1, 0f, false, ref best), 1);
    }

    static int Main()
    {
        LotChecks();
        object a = new object(), b = new object(), c = new object();
        object[] w = { a, b, c }, r = { a, b, c }, swapped = { b, a, c }, other = { a, b, a };
        bool[] f = { true, false, true }, fs = { false, true, true }, g = { true, true, true };
        Check("equal", AutOffloadCore.SeqClass(w, f, 3, r, f, 3), 0);
        Check("order only", AutOffloadCore.SeqClass(w, f, 3, swapped, fs, 3), 1);
        Check("order with a flag moved", AutOffloadCore.SeqClass(w, f, 3, swapped, f, 3), 2);
        Check("flag differs", AutOffloadCore.SeqClass(w, f, 3, r, g, 3), 2);
        Check("count differs", AutOffloadCore.SeqClass(w, f, 3, r, f, 2), 2);
        Check("other member", AutOffloadCore.SeqClass(w, f, 3, other, f, 3), 2);
        Check("both empty", AutOffloadCore.SeqClass(w, f, 0, r, f, 0), 0);
        // due pick (docs/208 §8.1): the 3 largest of 5 keys, descending; a key equal to the smallest kept does not replace it
        float[] k = new float[3];
        object[] it = new object[3];
        object d = new object(), e = new object();
        int n = 0;
        n = AutOffloadCore.InsertTop(k, it, n, 3, 0.6f, a);
        n = AutOffloadCore.InsertTop(k, it, n, 3, 0.9f, b);
        n = AutOffloadCore.InsertTop(k, it, n, 3, 0.7f, c);
        Check("pick fills", n, 3);
        n = AutOffloadCore.InsertTop(k, it, n, 3, 0.6f, d);
        n = AutOffloadCore.InsertTop(k, it, n, 3, 1.2f, e);
        Check("pick count", n, 3);
        Check("pick order", it[0] == e && it[1] == b && it[2] == c && k[0] == 1.2f && k[2] == 0.7f ? 1 : 0, 1);
        Console.WriteLine(sFail == 0 ? "autoffload selftest: ok (39 cases)" : "autoffload selftest: " + sFail + " failed");
        return sFail == 0 ? 0 : 1;
    }
}
