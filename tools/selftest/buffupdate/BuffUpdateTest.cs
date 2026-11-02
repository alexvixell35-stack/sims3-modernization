using System;
using System.Collections.Generic;
using Ts3Fixes;

// Offline self-check of BuffUpdateCore (issue #166 build 20, docs/510): the exceptions buffupdate may catch, a real
// dictionary walk whose dictionary changes, and the ones it must let pass.
static class BuffUpdateTest
{
    static int sFails;
    static void Check(bool ok, string what) { if (!ok) { sFails++; Console.WriteLine("FAIL " + what); } }

    static void Main()
    {
        Check(BuffUpdateCore.OutOfSync(new InvalidOperationException("out of sync")), "the game's Mono message");
        Check(BuffUpdateCore.OutOfSync(new InvalidOperationException("Collection was modified; enumeration operation may not execute.")), "the .NET message");
        Exception real = null;
        Dictionary<int, string> d = new Dictionary<int, string>();
        d[1] = "a"; d[2] = "b";
        try { foreach (string v in d.Values) d[3] = v; } catch (Exception e) { real = e; }
        Check(real != null && BuffUpdateCore.OutOfSync(real), "a dictionary changed during its walk (" + (real != null ? real.Message : "no exception") + ")");
        Check(!BuffUpdateCore.OutOfSync(new InvalidOperationException("Sequence contains no elements")), "another InvalidOperationException passes");
        Check(!BuffUpdateCore.OutOfSync(new NullReferenceException("out of sync")), "another exception type with the text passes");
        Check(!BuffUpdateCore.OutOfSync(new InvalidOperationException(null)), "an InvalidOperationException without a message of ours passes");
        Check(!BuffUpdateCore.OutOfSync(null), "no exception");
        Console.WriteLine(sFails == 0 ? "buffupdate selftest: ok" : "buffupdate selftest: " + sFails + " FAIL");
        Environment.Exit(sFails == 0 ? 0 : 1);
    }
}
