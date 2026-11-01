using System;
using Ts3Fixes;

// Offline self-check of BookCarryCore (docs/515): when Book.StopUsingBook dropped the carry state machine.
static class BookCarryTest
{
    static int sFails;
    static void Check(bool ok, string what) { if (!ok) { sFails++; Console.WriteLine("FAIL " + what); } }

    static void Main()
    {
        object carry = new object(), read = new object(), other = new object();
        Check(BookCarryCore.Dropped(carry, read, null), "a carry set before and cleared by the call was dropped");
        Check(!BookCarryCore.Dropped(null, read, null), "no carry before: nothing dropped");
        Check(!BookCarryCore.Dropped(carry, read, carry), "the carry still set after the call (relaxing, rocking, lying): not dropped here");
        Check(!BookCarryCore.Dropped(carry, read, other), "another carry set after the call: not dropped");
        Check(!BookCarryCore.Dropped(read, read, null), "the read machine itself was the carry: EA asked it to exit");
        Console.WriteLine(sFails == 0 ? "bookcarry selftest: ok" : "bookcarry selftest: " + sFails + " FAIL");
        Environment.Exit(sFails == 0 ? 0 : 1);
    }
}
