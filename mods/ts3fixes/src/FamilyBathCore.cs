namespace Ts3Fixes
{
    // Game-independent rules of FamilyBath.cs (familybath / familybathhh / familybathlog, docs/686 §44), checked offline
    // by tools/selftest/familybath.
    //
    // EA's automatic privacy (GenericPrivacySituation: the temporary rule a Sim gets while it showers, bathes or uses the
    // toilet) keeps everyone out of the room except toddlers and the Sim's romantic partner. The rule here adds members
    // of the same household who come to use a different fixture. It is NOT the door's lock: a door the player locked
    // (CommonDoor lock type: nobody, selected Sim, household, owner list ...) is another test of the game, is never
    // changed, and a Sim it refuses is never granted anything here.
    public static class FamilyBathCore
    {
        public const int kUserNone = 0, kUserToilet = 1, kUserWash = 2;       // what the Sim with privacy does
        public const int kTargetOther = 0, kTargetToilet = 1, kTargetSink = 2; // what the entering Sim comes for
        public const int kNone = 0, kGrant = 1, kWould = 2, kModest = 3, kNotFamily = 4, kDoorLocked = 5, kExempt = 6;
        public static readonly string[] Names = { "EA's rule", "let in", "would let in", "modest: EA's rule", "not family: EA's rule", "door locked by the player: EA's rule", "out of scope" };

        // The fixtures that go together: the toilet or the sink while someone showers or bathes; the sink while someone
        // uses the toilet. Never the fixture the Sim with privacy uses.
        public static bool Pair(int userKind, int targetKind, bool sameFixture)
        {
            if (sameFixture || userKind == kUserNone || targetKind == kTargetOther) return false;
            return userKind == kUserWash || targetKind == kTargetSink;
        }

        // doorLocked: a door of the room is locked by the player against the entering Sim (the game's own
        // CommonDoor.IsAllowedThrough says no). sameHousehold: both Sims live in one household (guests, service Sims and
        // visitors never). humans: both human. modest: either Sim is Never Nude or Shy. scope: HhSharedCore.Scope.
        public static int Decide(int userKind, int targetKind, bool sameFixture, bool doorLocked, bool sameHousehold, bool humans, bool modest, int scope)
        {
            if (!Pair(userKind, targetKind, sameFixture)) return kNone;
            if (doorLocked) return kDoorLocked;
            if (!sameHousehold || !humans) return kNotFamily;
            if (modest) return kModest;
            if (scope == HhSharedCore.kGo) return kGrant;
            return scope == HhSharedCore.kWould ? kWould : kExempt;
        }

        // A Sim let in stays let in for a short while after its fixture action (it washes its hands, walks out); the
        // pass is renewed while the action runs.
        public const float kPassMinutes = 5f;
        public static bool PassLive(long now, long until) { return until > now; }
    }
}
