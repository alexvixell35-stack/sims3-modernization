// Usage: node tools/selftest/autoffload/run.ts  (compiles this with mods/ts3fixes/src/AutOffloadCore.cs)
// Offline self-check of AutOffloadCore.SeqClass (docs/208 broadcaster Mode R classes) and InsertTop (the env due pick,
// docs/208 §8.1). Exit code 1 on failure.
using System;
using Ts3Fixes;

static class AutOffloadTest
{
    static int sFail;
    static void Check(string what, int got, int want) { if (got != want) { sFail++; Console.WriteLine("FAIL " + what + ": " + got + " want " + want); } }

    static int Main()
    {
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
        Console.WriteLine(sFail == 0 ? "autoffload selftest: ok (10 cases)" : "autoffload selftest: " + sFail + " failed");
        return sFail == 0 ? 0 : 1;
    }
}
