using System;
using Ts3Fixes;

// Offline self-check of mods/ts3fixes/src/HorseshoeSoloCore.cs (docs/865): HsCore.Cut / Remove for the bug layouts and controls.
static class HorseshoeSoloTest
{
    static int sFail;
    static void Check(bool ok, string what) { if (!ok) { sFail++; Console.WriteLine("FAIL " + what); } }

    static int Main()
    {
        const double cap = 2;
        // bug: an NPC alone in TakeTurn never stops; cut after the cap, or at once when a non-autonomous interaction waits
        Check(HsCore.Cut(1, true, false, 3, cap, false), "NPC alone 3 h: cut");
        Check(HsCore.Cut(1, true, false, 2, cap, false), "NPC alone exactly the cap: cut");
        Check(HsCore.Cut(1, true, false, 0.2, cap, true), "NPC alone 0.2 h, Go Home waiting: cut");
        Check(HsCore.Cut(1, true, false, 0, 0, true), "cap 0, something waiting: cut");
        // controls: kept
        Check(!HsCore.Cut(1, true, true, 50, cap, true), "household / selected / unknown scope alone 50 h: kept");
        Check(!HsCore.Cut(2, true, false, 50, cap, true), "two-player game: kept");
        Check(!HsCore.Cut(4, true, false, 50, cap, true), "four-player game: kept");
        Check(!HsCore.Cut(1, true, false, 1.9, cap, false), "short solo game: kept");
        Check(!HsCore.Cut(1, false, false, 50, cap, true), "solo Sim not in TakeTurn (WaitToThrow, transition): kept");
        Check(!HsCore.Cut(1, true, false, 50, 0, false), "cap 0 and nothing waiting: kept");
        Check(!HsCore.Cut(0, true, false, 50, cap, true), "empty queue: kept");

        // side gap: a lost player blocks the queue
        Check(HsCore.Remove(true, false, false), "deleted Sim: removed at once");
        Check(HsCore.Remove(false, false, true), "Sim not playing at two passes: removed");
        Check(!HsCore.Remove(false, false, false), "Sim not playing at one pass only (hand-over): kept");
        Check(!HsCore.Remove(false, true, true), "Sim playing: kept");
        Check(!HsCore.Remove(false, true, false), "Sim playing, first pass: kept");

        Console.WriteLine(sFail == 0 ? "horseshoesolo selftest: all passed" : "horseshoesolo selftest: " + sFail + " FAILED");
        return sFail == 0 ? 0 : 1;
    }
}
