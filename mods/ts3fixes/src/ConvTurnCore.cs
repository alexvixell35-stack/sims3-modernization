namespace Ts3Fixes
{
    // Game-independent decision of convturn (ConvTurn.cs, issue #166 build 19, docs/509). Self-check: tools/selftest/convturn.
    public static class ConvTurnCore
    {
        public const float kSilentMinutes = 15f;

        // Whether the turn of a conversation is stuck for a member who is asking (a local Sim scoring socials): the holder is
        // nobody, gone, has no autonomy, runs no local autonomy (EA's flag, refreshed by every high-LOD run), has no
        // queue, is busy with something that is not a social, or nobody has spoken for kSilentMinutes sim minutes.
        public static bool Stuck(bool holderNull, bool holderIsAsker, bool holderDestroyed, bool autonomyNull, bool runsLocal,
            bool queueNull, bool headNull, bool headIsSocial, float minutesSinceTalk)
        {
            if (holderNull) return true;
            if (holderIsAsker) return false;
            if (holderDestroyed || autonomyNull || !runsLocal || queueNull) return true;
            if (!headNull && !headIsSocial) return true;
            return minutesSinceTalk > kSilentMinutes;
        }
    }
}
