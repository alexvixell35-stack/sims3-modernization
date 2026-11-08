namespace Ts3Fixes
{
    // Game-independent decisions of bookhand (BookHand.cs, docs/251). Self-check: tools/selftest/bookhand.
    public static class BookHandCore
    {
        // EA's state after Book.StopUsingBook (standing or sitting read, book from the inventory) when the read clip never
        // reached event 100: a book in the right hand, still parented to this Sim, and Sim.CarryStateMachine null. EA's own
        // event 100 handler (Book.PutBookInInventoryHandler) would have put that book in the inventory.
        public static bool Stuck(bool rightHandIsBook, bool parentIsSim, bool carryNull)
        {
            return rightHandIsBook && parentIsSim && carryNull;
        }

        // CarrySystem.PutDown(Sim, SurfaceType, bool) with a stuck book: EA returns at once only when the Sim has no current
        // lot; otherwise every branch (surface, nearest surface, floor) reads the null carry machine and throws.
        public static bool PutDownWouldThrow(bool stuck, bool lotCurrentNull)
        {
            return stuck && !lotCurrentNull;
        }

        // InteractionQueue.PutDownCarriedObjects with a stuck book throws "left an object in the Sim's hand with no carry state
        // machine" only past its own early returns: the next queued interaction is of another group, and the posture is not a
        // carrying one.
        public static bool QueueWouldThrow(bool stuck, bool sameGroupNext, bool carryingPosture)
        {
            return stuck && !sameGroupNext && !carryingPosture;
        }

        // What a hook does: 0 = nothing (EA would not throw), 1 = count only (switch off), 2 = run EA's handler (switch on).
        public static int Action(bool wouldThrow, bool on)
        {
            return !wouldThrow ? 0 : on ? 2 : 1;
        }

        // The diag check armed by StopUsingBook: logs when, kDiagMs after the call, the book is still stuck (a normal exit
        // clip moves it within about 2 s), at most kDiagLines lines per session.
        public const int kDiagMs = 10000, kDiagLines = 50;
        public static bool DiagDue(int elapsedMs) { return elapsedMs >= kDiagMs; }
        public static bool DiagLog(bool stuckNow, int linesSoFar) { return stuckNow && linesSoFar < kDiagLines; }
    }
}
