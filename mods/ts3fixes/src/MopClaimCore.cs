namespace Ts3Fixes
{
    // Game-independent rules of MopClaim.cs (mopclaim / mopclaimhh / mopclaimlog, docs/686 §43), checked offline by
    // tools/selftest/mopclaim.
    //
    // EA's mopping (Terrain.Mopping.Run) takes the nearest puddle of the room, walks there and only then asks whether the
    // tile can be mopped with Sims counted (PuddleManager.CanMopPuddle, ignoreSims false): a second Sim sent to the same
    // puddle walks over, plays the route-fail shrug at the Sim already mopping and gives up. A puddle has no use list and
    // no reservation. The fix reserves the tile in nextplan's claim table (HhHold) when a Sim takes it.
    public static class MopClaimCore
    {
        public const int kKeep = 0, kOrdered = 1, kWould = 2, kExempt = 3, kMove = 4;

        // heldByOther: another Sim that is mopping holds EA's pick. ordered: the action is not the Sim's own pick (the
        // player's click on that puddle, a script's push): never moved. scope: HhSharedCore.Scope for this Sim.
        public static int Decide(bool heldByOther, bool ordered, int scope)
        {
            if (!heldByOther) return kKeep;
            if (ordered) return kOrdered;
            if (scope == HhSharedCore.kGo) return kMove;
            return scope == HhSharedCore.kExempt ? kExempt : kWould;
        }

        // The tile's key in the claim table: bit 63 set (never an object id), then lot, level, x, z.
        public static ulong Key(ulong lotId, int level, int x, int z)
        {
            return 0x8000000000000000UL | ((lotId & 0x7FFFFFUL) << 40) | (((ulong)(uint)(level & 0xFF)) << 32) | (((ulong)(uint)(x & 0xFFFF)) << 16) | ((ulong)(uint)(z & 0xFFFF));
        }

        // The nearest tile no other Sim holds; -1 when every puddle of the room is held (the Sim's mopping ends quietly:
        // no walk, no shrug).
        public static int Nearest(float[] distSq, bool[] ok, int n)
        {
            int best = -1;
            for (int i = 0; i < n; i++) if (ok[i] && (best < 0 || distSq[i] < distSq[best])) best = i;
            return best;
        }

        // The hold's life in sim-min: the mop itself plus the walk allowance (nextplan's claim minutes).
        public static float HoldMinutes(float mopMin, float claimMin) { return (mopMin > 0f ? mopMin : 0f) + (claimMin > 0.5f ? claimMin : 0.5f); }
    }
}
