using System;
using Ts3Fixes;

// Offline self-check of TrashOutsideFallbackCore (docs/646 §2).
static class TrashOutsideFallbackTest
{
    static int sFails;
    static void Check(bool ok, string what) { if (!ok) { sFails++; if (sFails < 20) Console.WriteLine("FAIL " + what); } }

    static void Main()
    {
        TrashOutsideFallbackCore t = new TrashOutsideFallbackCore();
        const long span = 4000;
        const ulong lot = 0xF59000350AF186A0UL;

        // decisions
        Check(TrashOutsideFallbackCore.Decide(false, true, false, true, true) == TrashOutsideFallbackCore.kEa, "switch off: EA");
        Check(TrashOutsideFallbackCore.Decide(true, false, false, true, true) == TrashOutsideFallbackCore.kEa, "not DisposeOutside's forced call: EA");
        Check(TrashOutsideFallbackCore.Decide(true, true, true, true, true) == TrashOutsideFallbackCore.kExempt, "household, selected or unknown scope: exempt");
        Check(TrashOutsideFallbackCore.Decide(true, true, false, false, true) == TrashOutsideFallbackCore.kEa, "lot not remembered: EA's forced disposal first");
        Check(TrashOutsideFallbackCore.Decide(true, true, false, true, true) == TrashOutsideFallbackCore.kFallback, "remembered lot with a receptacle: non-forced disposal");
        Check(TrashOutsideFallbackCore.Decide(true, true, false, true, false) == TrashOutsideFallbackCore.kPutDown, "remembered lot, nothing else takes it: put down (never destroyed)");

        // recording
        Check(TrashOutsideFallbackCore.Record(true, true, false, false, true), "forced call failed with a no-path plan: record");
        Check(!TrashOutsideFallbackCore.Record(true, true, false, true, true), "forced call succeeded (a later can reached): no record");
        Check(!TrashOutsideFallbackCore.Record(true, true, false, false, false), "failed without a no-path plan (cancelled, can in use): no record");
        Check(!TrashOutsideFallbackCore.Record(true, true, true, false, true), "exempt: no record");
        Check(!TrashOutsideFallbackCore.Record(false, true, false, false, true), "switch off: no record");

        // refusal of EmptyTrash picks
        Check(TrashOutsideFallbackCore.Refuse(true, true, true, false, true, true), "autonomous NPC, indoor can, remembered lot: refused");
        Check(!TrashOutsideFallbackCore.Refuse(true, true, false, false, true, true), "user-directed: EA");
        Check(!TrashOutsideFallbackCore.Refuse(true, true, true, true, true, true), "exempt: EA");
        Check(!TrashOutsideFallbackCore.Refuse(true, true, true, false, false, true), "not an indoor can: EA");
        Check(!TrashOutsideFallbackCore.Refuse(true, true, true, false, true, false), "lot not remembered: EA");
        Check(!TrashOutsideFallbackCore.Refuse(true, false, true, false, true, true), "EA said false: stays false, never turned true");
        Check(!TrashOutsideFallbackCore.Refuse(false, true, true, false, true, true), "switch off: EA");

        // memo lifetime
        Check(!t.Known(lot, 0, span), "empty: not known");
        Check(t.Note(lot, 100, span), "first failure: new");
        Check(!t.Note(lot, 200, span), "second failure: not new");
        Check(t.Known(lot, 200 + span - 1, span), "one tick before the span ends (from the latest failure): known");
        Check(!t.Known(lot, 200 + span, span), "span over: forgotten");
        Check(t.Count == 0, "forgotten lot dropped");
        t.Note(lot, 1000, span);
        Check(!t.Known(lot, 900, span), "clock went back (another save): forgotten");
        Check(!t.Note(0, 10, span) && !t.Known(0, 10, span), "lot id 0 (no lot): never remembered");

        // bounded table
        t.Clear();
        for (ulong i = 1; i <= (ulong)TrashOutsideFallbackCore.kMax; i++) t.Note(i, i <= 10 ? 0 : 5000, span);
        Check(t.Count == TrashOutsideFallbackCore.kMax, "full table");
        t.Note(9999, 5000, span);
        Check(t.Count == TrashOutsideFallbackCore.kMax - 10 + 1 && t.Known(9999, 5000, span), "full: expired lots dropped first");
        for (ulong i = 20000; t.Count < TrashOutsideFallbackCore.kMax; i++) t.Note(i, 5000, span);
        t.Note(30000, 5000, span);
        Check(t.Count == 1 && t.Known(30000, 5000, span), "full of live lots: cleared, new one kept");

        Console.WriteLine(sFails == 0 ? "trashoutsidefallback selftest: ok" : "trashoutsidefallback selftest: " + sFails + " FAIL");
        Environment.Exit(sFails == 0 ? 0 : 1);
    }
}
