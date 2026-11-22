namespace Ts3Fixes
{
    // Game-independent decisions of horseshoesolo (HorseshoeSolo.cs, docs/865), kept free of game types so the offline
    // self-test (tools/selftest/horseshoesolo) compiles it alone.
    public static class HsCore
    {
        // End this Sim's solo turn now?
        //   queued: Sims in the court's mPlayerQueue; turn: its running interaction is TakeTurn on this court; player: household,
        //   selected or unknown scope (never cut); hours: sim-hours seen playing alone; capHours: kHorseshoeSoloHours (0 = no time
        //   cap); waiting: a non-autonomous interaction is queued behind the game.
        public static bool Cut(int queued, bool turn, bool player, double hours, double capHours, bool waiting)
        {
            return queued == 1 && turn && !player && (capHours > 0 && hours >= capHours || waiting);
        }

        // Take this Sim out of the court's queue?
        //   gone: null or destroyed; playing: a TakeTurn / WaitToThrow on this court in its interaction queue;
        //   idleBefore: it was queued and not playing at the previous pass too (two passes, so a hand-over between EA's
        //   interactions is never mistaken for a lost player).
        public static bool Remove(bool gone, bool playing, bool idleBefore)
        {
            return gone || !playing && idleBefore;
        }
    }
}
