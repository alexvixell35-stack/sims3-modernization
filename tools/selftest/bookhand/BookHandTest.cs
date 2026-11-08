using System;
using Ts3Fixes;

// Offline self-check of BookHandCore (docs/251): the guard decision at EA's put-downs and the diag check.
static class BookHandTest
{
    static int sFails;
    static void Check(bool ok, string what) { if (!ok) { sFails++; Console.WriteLine("FAIL " + what); } }

    static void Main()
    {
        Check(BookHandCore.Stuck(true, true, true), "book in hand, parented, no carry machine: stuck");
        Check(!BookHandCore.Stuck(true, true, false), "carry machine still set (normal carry): not stuck");
        Check(!BookHandCore.Stuck(false, true, true), "not a book: left to EA");
        Check(!BookHandCore.Stuck(true, false, true), "book no longer parented to the Sim (handler already ran): not stuck");

        Check(BookHandCore.PutDownWouldThrow(true, false), "PutDown, stuck, on a lot: EA throws");
        Check(!BookHandCore.PutDownWouldThrow(true, true), "PutDown with no current lot: EA returns, nothing to do");
        Check(!BookHandCore.PutDownWouldThrow(false, false), "PutDown, not stuck: EA's own path");

        Check(BookHandCore.QueueWouldThrow(true, false, false), "PutDownCarriedObjects, stuck, other group, no carry posture: EA throws");
        Check(!BookHandCore.QueueWouldThrow(true, true, false), "same group continues: EA returns early, so do we");
        Check(!BookHandCore.QueueWouldThrow(true, false, true), "carrying posture: EA returns early, so do we");
        Check(!BookHandCore.QueueWouldThrow(false, false, false), "not stuck: EA's own path");

        Check(BookHandCore.Action(false, true) == 0, "no throw, switch on: nothing");
        Check(BookHandCore.Action(false, false) == 0, "no throw, switch off: nothing");
        Check(BookHandCore.Action(true, false) == 1, "would throw, switch off: count only (vanilla)");
        Check(BookHandCore.Action(true, true) == 2, "would throw, switch on: EA's handler");

        Check(!BookHandCore.DiagDue(BookHandCore.kDiagMs - 1), "diag: not before 10 s");
        Check(BookHandCore.DiagDue(BookHandCore.kDiagMs), "diag: due at 10 s");
        Check(BookHandCore.DiagLog(true, 0), "diag: stuck after 10 s logs");
        Check(!BookHandCore.DiagLog(false, 0), "diag: put away by then, no line");
        Check(!BookHandCore.DiagLog(true, BookHandCore.kDiagLines), "diag: cap reached, no line");
        Console.WriteLine(sFails == 0 ? "bookhand selftest: ok" : "bookhand selftest: " + sFails + " FAIL");
        Environment.Exit(sFails == 0 ? 0 : 1);
    }
}
