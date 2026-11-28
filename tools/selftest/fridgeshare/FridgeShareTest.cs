using System;

namespace Ts3Fixes
{
    // Offline checks of FridgeShareCore (docs/686 §46).
    public static class FridgeShareTest
    {
        static int sFail, sRun;
        static void Check(string name, bool ok) { sRun++; if (!ok) { sFail++; Console.WriteLine("FAIL " + name); } }

        public static int Main()
        {
            // rule 1: two short users at most, a long one alone
            Check("admit free", FridgeShareCore.Admit(0, 0));
            Check("admit one short", FridgeShareCore.Admit(1, 0));
            Check("admit cap", !FridgeShareCore.Admit(2, 0));
            Check("admit long", !FridgeShareCore.Admit(0, 1));

            // share at the ask
            Check("share free = EA", FridgeShareCore.Share(true, true, true, 0, 0, 0, true) == FridgeShareCore.kNone);
            Check("share long = EA", FridgeShareCore.Share(true, true, true, 0, 1, 0, true) == FridgeShareCore.kNone);
            Check("share line first", FridgeShareCore.Share(true, true, true, 1, 0, 1, true) == FridgeShareCore.kNone);
            Check("share capped", FridgeShareCore.Share(true, true, true, 2, 0, 0, true) == FridgeShareCore.kCapped);
            Check("share exempt", FridgeShareCore.Share(true, false, true, 1, 0, 0, true) == FridgeShareCore.kExempt);
            Check("share other exempt", FridgeShareCore.Share(true, true, false, 1, 0, 0, true) == FridgeShareCore.kExempt);
            Check("share would", FridgeShareCore.Share(false, true, true, 1, 0, 0, true) == FridgeShareCore.kWould);
            Check("share at once unseen", FridgeShareCore.Share(true, true, true, 1, 0, 0, true) == FridgeShareCore.kAtOnce);
            Check("share beside seen", FridgeShareCore.Share(true, true, true, 1, 0, 0, false) == FridgeShareCore.kBeside);

            // at the fridge
            Check("ready side at once", FridgeShareCore.Ready(true, true, 1, 0) == FridgeShareCore.kReady);
            Check("ready side at once capped", FridgeShareCore.Ready(true, true, 2, 0) == FridgeShareCore.kWait);
            Check("beside waits for user", FridgeShareCore.Ready(true, false, 1, 0) == FridgeShareCore.kWait);
            Check("beside steps in past a stander (EA's walk moves it)", FridgeShareCore.Ready(true, false, 0, 0) == FridgeShareCore.kStepIn);
            Check("beside steps in", FridgeShareCore.Ready(true, false, 0, 0) == FridgeShareCore.kStepIn);
            Check("slot free", FridgeShareCore.Ready(false, false, 0, 0) == FridgeShareCore.kReady);
            Check("slot busy seen", FridgeShareCore.Ready(false, false, 1, 0) == FridgeShareCore.kWait);
            Check("slot with unseen sharer", FridgeShareCore.Ready(false, true, 1, 0) == FridgeShareCore.kReady);
            Check("slot long", FridgeShareCore.Ready(false, true, 0, 1) == FridgeShareCore.kWait);

            // rule 3: head away
            float g = 2.5f;
            Check("yield go", FridgeShareCore.Yield(true, true, true, false, false, 6f, 3f, true, 2f, false, g) == FridgeShareCore.kGo);
            Check("yield would", FridgeShareCore.Yield(false, true, true, false, false, 6f, 3f, true, 2f, false, g) == FridgeShareCore.kWould);
            Check("yield in grace", FridgeShareCore.Yield(true, true, true, false, false, 6f, 1f, true, 2f, false, g) == FridgeShareCore.kNone);
            Check("yield head there", FridgeShareCore.Yield(true, true, true, false, false, 1.2f, 3f, true, 2f, false, g) == FridgeShareCore.kNone);
            Check("yield head nearer", FridgeShareCore.Yield(true, true, true, false, false, 2f, 3f, true, 3f, false, g) == FridgeShareCore.kNone);
            Check("yield waiter not there", FridgeShareCore.Yield(true, true, true, false, false, 6f, 3f, true, 4f, false, g) == FridgeShareCore.kNone);
            Check("yield waiter walking", FridgeShareCore.Yield(true, true, true, false, false, 6f, 3f, true, 2f, true, g) == FridgeShareCore.kNone);
            Check("yield not next", FridgeShareCore.Yield(true, true, true, false, false, 6f, 3f, false, 2f, false, g) == FridgeShareCore.kNone);
            Check("yield head not walking", FridgeShareCore.Yield(true, true, false, false, false, 6f, 3f, true, 2f, false, g) == FridgeShareCore.kNone);
            Check("yield once", FridgeShareCore.Yield(true, true, true, false, true, 6f, 3f, true, 2f, false, g) == FridgeShareCore.kNone);
            Check("yield player order kept", FridgeShareCore.Yield(true, true, true, true, false, 6f, 3f, true, 2f, false, g) == FridgeShareCore.kKept);
            Check("yield exempt", FridgeShareCore.Yield(true, false, true, false, false, 6f, 3f, true, 2f, false, g) == FridgeShareCore.kExempt);

            // clean-out ordering
            Check("clean order go", FridgeShareCore.CleanOrder(true, true, false, true, false) == FridgeShareCore.kGo);
            Check("clean order would", FridgeShareCore.CleanOrder(false, true, false, true, false) == FridgeShareCore.kWould);
            Check("clean order long waiter", FridgeShareCore.CleanOrder(true, true, false, false, false) == FridgeShareCore.kNone);
            Check("clean order player cleaner kept", FridgeShareCore.CleanOrder(true, true, true, true, false) == FridgeShareCore.kKept);
            Check("clean order player both", FridgeShareCore.CleanOrder(true, true, true, true, true) == FridgeShareCore.kGo);
            Check("clean order exempt", FridgeShareCore.CleanOrder(true, false, false, true, false) == FridgeShareCore.kExempt);

            // spots and room
            Check("side right", FridgeShareCore.FirstSide(1f, 2f) == 1);
            Check("side left", FridgeShareCore.FirstSide(2f, 1f) == -1);
            Check("slot clear none", FridgeShareCore.SlotClear(-1f));
            Check("slot clear far", FridgeShareCore.SlotClear(0.8f));
            Check("slot taken", !FridgeShareCore.SlotClear(0.3f));
            Check("room free", FridgeShareCore.Room(0, 0, 0));
            Check("room one", FridgeShareCore.Room(1, 0, 0));
            Check("room line", !FridgeShareCore.Room(1, 0, 1));
            Check("room long", !FridgeShareCore.Room(0, 1, 0));
            Check("room full", !FridgeShareCore.Room(2, 0, 0));

            Console.WriteLine("fridgeshare selftest: " + (sRun - sFail) + "/" + sRun + " passed");
            return sFail == 0 ? 0 : 1;
        }
    }
}
