namespace Ts3Fixes
{
    // Game-independent rules of reactoverlay (ReactOverlay.cs, docs/686 §26). Offline self-check: tools/selftest/reactoverlay.
    public static class ReactOverlayCore
    {
        public const int kGo = 0, kWould = 1, kKeep = 2;
        public const int kWhyNone = 0, kWhyNotHuman = 1, kWhyRouting = 2, kWhyPosture = 3, kWhyTransition = 4, kWhyCarry = 5,
            kWhyPrivate = 6, kWhyConversation = 7, kWhyBusyParts = 8, kWhyNoClip = 9, kWhyExempt = 10, kWhyAsleep = 11, kWhyAgeUp = 12;
        public const int kWhyCount = 13;
        public const int kStaleMinMs = 500, kStaleMaxMs = 10000;

        // ReactionSpeed bits (EA's enum): Immediate 1, ImmediateWithoutOverlay 2, AfterInteraction 4, CriticalWithRoute 0xC.
        const int kImmediateWithoutOverlay = 2, kAfterInteraction = 4, kCriticalWithRoute = 0xC;

        // OverlayComponent.OverlayLevels: RArm 0x20 | LArm 0x40 | Head 0x80 | Face 0x100 = BothArmsAndHead.
        public const int kArmsAndHead = 0x1E0;

        // A reaction EA would queue behind the current action: NowOrLater or AfterInteraction, not a full-body one
        // (ImmediateWithoutOverlay), not one that runs to its target (CriticalWithRoute), not watch-until-exit.
        public static bool LateKind(int speed, bool watch)
        {
            if (watch || speed == kCriticalWithRoute) return false;
            return (speed & kAfterInteraction) != 0 && (speed & kImmediateWithoutOverlay) == 0;
        }

        // EA's overlay asks for both arms and the head first (OverlayComponent.RequestOverlay); the parts the current
        // animation holds (interaction + awareness flags) must leave them free, or the overlay would shrink to a nod.
        public static bool PartsFree(int usedFlags) { return (usedFlags & kArmsAndHead) == 0; }

        // The tunable stale time of a queued reaction, kept in a sane range (ms of real time).
        public static int StaleMs(int tuned)
        {
            if (tuned < kStaleMinMs) return kStaleMinMs;
            return tuned > kStaleMaxMs ? kStaleMaxMs : tuned;
        }

        public static bool Stale(int ageMs, int tuned) { return ageMs >= StaleMs(tuned); }

        // `blocker`: the first reason this moment is not safe for an overlay (kWhyNone = safe: standing or seated,
        // still, nothing carried, arms and head free, EA has an overlay clip). `inScope`: an NPC, or a household Sim with
        // reactoverlayhh on. Off = compare mode: would.
        public static int Decide(bool on, int blocker, bool inScope, out int why)
        {
            why = blocker;
            if (blocker != kWhyNone) return kKeep;
            if (!on) return kWould;
            if (!inScope) { why = kWhyExempt; return kKeep; }
            return kGo;
        }
    }
}
