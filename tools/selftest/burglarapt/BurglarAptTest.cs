using System;
using Ts3Fixes;

// Offline self-check of mods/ts3fixes/src/BurglarAptCore.cs (docs/866): BaCore for the bug layouts and controls.
static class BurglarAptTest
{
    static int sFail;
    static void Check(bool ok, string what) { if (!ok) { sFail++; Console.WriteLine("FAIL " + what); } }

    static int Main()
    {
        // Refuse(eaAccepts, aptLot, hiddenRoom, publicRoom)
        // the bug: EA accepts loot in a hidden neighbour unit or a public room of an apartment lot
        Check(BaCore.Refuse(true, true, true, false), "apartment, hidden unit: refused");
        Check(BaCore.Refuse(true, true, false, true), "apartment, public room: refused");
        Check(BaCore.Refuse(true, true, true, true), "apartment, hidden and public: refused");
        // controls: kept as EA has it
        Check(!BaCore.Refuse(true, true, false, false), "apartment, household's own unit: kept");
        Check(!BaCore.Refuse(true, false, false, false), "house lot: kept");
        Check(!BaCore.Refuse(true, false, true, true), "not an apartment lot (a tomb's hidden room): kept");
        Check(!BaCore.Refuse(false, true, true, true), "EA already refuses: nothing to turn");

        // Answer(on, ...): EA's answer with the switch off, the fix with it on
        foreach (bool on in new bool[] { false, true })
        {
            string s = on ? "on" : "off";
            Check(BaCore.Answer(on, true, true, true, false) == !on, s + ": hidden-unit object");
            Check(BaCore.Answer(on, true, true, false, true) == !on, s + ": public-room object");
            Check(BaCore.Answer(on, true, true, false, false), s + ": own-unit object kept");
            Check(BaCore.Answer(on, true, false, false, false), s + ": house-lot object kept");
            Check(BaCore.Answer(on, true, false, true, false), s + ": non-apartment hidden room kept");
            Check(!BaCore.Answer(on, false, true, true, true) && !BaCore.Answer(on, false, false, false, false), s + ": EA's no stays no");
        }

        Console.WriteLine(sFail == 0 ? "burglarapt selftest: all passed" : "burglarapt selftest: " + sFail + " FAILED");
        return sFail == 0 ? 0 : 1;
    }
}
