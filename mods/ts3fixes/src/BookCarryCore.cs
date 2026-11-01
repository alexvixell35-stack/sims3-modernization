namespace Ts3Fixes
{
    // Game-independent decision of bookcarry (BookCarry.cs, docs/515). Self-check: tools/selftest/bookcarry.
    public static class BookCarryCore
    {
        // Whether Book.StopUsingBook dropped the Sim's carry state machine: there was one before the call, it was not the
        // machine the book was read with, and the field is empty after it. EA then never asks that machine to exit.
        public static bool Dropped(object carryBefore, object readMachine, object carryAfter)
        {
            return carryBefore != null && carryBefore != readMachine && carryAfter == null;
        }
    }
}
