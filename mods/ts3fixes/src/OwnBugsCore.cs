namespace Ts3Fixes
{
    // Game-independent rules of the docs/650 fixes (EaAlarmGuard.cs, UnseenService88.cs, Alloc87Comparers.cs,
    // StreetParkingListen.cs). Self-check: tools/selftest/ownbugs.
    public static class OwnBugsCore
    {
        // ---- §1 EA alarm guard ----
        public const int kFrameCap = 160;

        // The first frame of a stack trace ("  at A.B (x) [0x0001] in <file>"), trimmed, without the leading "at ",
        // capped at kFrameCap characters; "?" when there is none.
        public static string FirstFrame(string stack)
        {
            if (string.IsNullOrEmpty(stack)) return "?";
            string[] lines = stack.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string l = lines[i].Trim();
                if (l.Length == 0) continue;
                if (l.StartsWith("at ")) l = l.Substring(3).Trim();
                return l.Length > kFrameCap ? l.Substring(0, kFrameCap) : l;
            }
            return "?";
        }

        // "<type> at <first frame>" for a log line.
        public static string Brief(string typeName, string stack)
        {
            return (string.IsNullOrEmpty(typeName) ? "?" : typeName) + " at " + FirstFrame(stack);
        }

        // Log the n-th line (0-based) of a capped log?
        public static bool LogMore(int n, int cap) { return n >= 0 && n < cap; }

        // ---- §3 alarmcmpnobox: rehome the live AlarmManagers now? Only with a world (no world: at world load) ----
        public static bool A87RehomeNow(bool worldUp) { return worldUp; }

        // ---- §2 unseenservice: steps of one instant service, in order ----
        public const int kSvcStepStart = 0, kSvcStepOrder = 1, kSvcStepCommodity = 2, kSvcStepCreate = 3, kSvcStepPlace = 4, kSvcStepPour = 5,
            kSvcStepBehavior = 6, kSvcStepDone = 7, kSvcStepScope = 8;
        static readonly string[] kSvcStepNames = { "start", "RunOrderBehavior", "commodity updates", "create", "ParentToSlot", "pour", "RunBehavior", "done", "scope or preconditions" };

        public static string SvcStepName(int step)
        {
            return step >= 0 && step < kSvcStepNames.Length ? kSvcStepNames[step] : "step " + step;
        }

        // End commodity updates in the finally? Only those begun and not ended (EA's own End ran on the normal path).
        public static bool SvcEndUpdates(bool begunNotEnded) { return begunNotEnded; }

        // Destroy the made glass or plate in the finally? A failure before the customer's RunBehavior took over leaves
        // nothing behind (placed or not: EA's hand-off follows and needs the slot empty); once RunBehavior ran, the object
        // is EA's (picked up, eaten, faded).
        public static bool SvcDestroyMade(bool made, bool placed, int step)
        {
            if (!made || step == kSvcStepDone) return false;
            return !placed || step < kSvcStepBehavior;
        }
    }
}
