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
    }
}
