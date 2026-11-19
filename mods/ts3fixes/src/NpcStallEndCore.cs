namespace Ts3Fixes
{
    // Game-independent rule of npcstallend (NpcStallEnd.cs, docs/649). Self-check: tools/selftest/npcstallend.
    public static class NpcStallEndCore
    {
        public const int kWait = 0, kEnd = 1, kExempt = 2, kSkipKind = 3, kSkipOutfit = 4, kTried = 5;

        // Interaction kinds that legitimately run for hours in one spot; matched against the definition's (or the
        // interaction's) full type name. Order matters only for the name reported.
        static readonly string[][] kKinds = {
            new string[] { "sleep", "Sleep", "TakeNap", "Coffin" },
            new string[] { "work or school", "WorkInRabbitHole", "SchoolInRabbitHole", "Careers.", "ActiveCareer", "RabbitHole", "Daycare", "HomeWork", "Homework" },
            new string[] { "performing", "Perform", "Concert", "Busk", "Stage" },
            new string[] { "service role", "Tending", "TendBar", "Bartend", "Barista", "WaitTables", "WatchDoor", "VelvetRopes", "Bouncer", "Register", "Proprietor", "Butler", "Maid", "Service" },
            new string[] { "fishing", "Fish" },
            new string[] { "reading", "Read", "Newspaper", "Magazine" },
            new string[] { "watching", "Watch", "AttendShow", "ShowVenue", "Audience", "Applaud", "Cheer", "ThrowRice", "Wedding" },
        };

        // The allowed kind a definition name falls under, null when none (an empty or unknown name is no kind).
        public static string Kind(string defName)
        {
            if (string.IsNullOrEmpty(defName)) return null;
            for (int i = 0; i < kKinds.Length; i++)
                for (int j = 1; j < kKinds[i].Length; j++)
                    if (defName.IndexOf(kKinds[i][j], System.StringComparison.Ordinal) >= 0) return kKinds[i][0];
            return null;
        }

        // Progress: the head interaction changed (another instance or none) or the Sim moved more than 1 m.
        public static bool Progress(bool sameHead, float movedMeters)
        {
            return !sameHead || movedMeters > 1f;
        }

        // One verdict per stalled head interaction. exempt: household, selected, selectable or unknown scope (always wins);
        // busy: routing, in a rabbit hole posture, parented (vehicle, boat, carried), a role Sim, a baby or toddler.
        // outfitWait: the Sim's task sits in SwitchOutfitHelper.Wait (left to outfitwaitend, docs/601 §1); tried: this head
        // was already cancelled once by us (never twice; the watchdog's sim-day reset stays the backstop).
        public static int Decide(bool exempt, double stillHours, double limitHours, string kind, bool busy, bool outfitWait, bool tried)
        {
            if (exempt) return stillHours > Limit(limitHours) ? kExempt : kWait;
            if (stillHours <= Limit(limitHours)) return kWait;
            if (tried) return kTried;
            if (busy || kind != null) return kSkipKind;
            if (outfitWait) return kSkipOutfit;
            return kEnd;
        }

        // a zero, negative or absurd tuning falls back to 3 sim-hours; never below 1 sim-hour
        public static double Limit(double limitHours)
        {
            if (!(limitHours > 0) || limitHours > 1000) return 3;
            return limitHours < 1 ? 1 : limitHours;
        }

        // outfitwaitend extension (docs/649 §3): with npcstallend on, a "cleared" outfit wait (the Sim's pending outfit is
        // none: its delayed swap finished or was cancelled and the helper's ready callback never came) of a live off-screen
        // NPC past the end mark, not inside the helper's ChangeOutfit, ends the same way outfitwaitend ends an own-cause wait.
        public static bool EndClearedWait(bool on, long waited, long endAfter, bool liveNpc, bool onScreen, int cause, int causeCleared, bool inChangeOutfit)
        {
            return on && waited >= endAfter && liveNpc && !onScreen && cause == causeCleared && !inChangeOutfit;
        }

        public static string Name(int verdict)
        {
            switch (verdict)
            {
                case kEnd: return "ended";
                case kExempt: return "exempt";
                case kSkipKind: return "allowed kind";
                case kSkipOutfit: return "outfit wait";
                case kTried: return "cancel did not take";
                default: return "wait";
            }
        }
    }
}
