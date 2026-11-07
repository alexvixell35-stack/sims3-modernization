using System;
using System.Collections.Generic;
using System.Globalization;

namespace Ts3Agent
{
    // The pure rules of the stove-wall-repeat run (StoveRepeatHandlers.cs), offline-testable (tools/harness/stove-repeat-core.ts): what a plan line says,
    // when a failed plan is a stop, what counts as a bake. Plain C# 2 so the game's csc and the self-check both build it.
    public static class StoveRepeatCore
    {
        // "candidates 0/0/0" from a plan line: generated, accepted, rejected; null if the line has none
        public static int[] Candidates(string line)
        {
            if (line == null) return null;
            int at = line.IndexOf("candidates ", StringComparison.Ordinal);
            if (at < 0) return null;
            string[] p = line.Substring(at + 11).Split(new char[] { ' ', ',', ';' })[0].Split('/');
            if (p.Length != 3) return null;
            int[] r = new int[3];
            for (int i = 0; i < 3; i++) if (!int.TryParse(p[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out r[i])) return null;
            return r;
        }

        // the transient split of the end line: "0/0/0" (the planner found no point at all), "1/1/0" (a point, no path), anything else
        public static string Split(string line)
        {
            int[] c = Candidates(line);
            if (c == null) return "other";
            if (c[0] == 0 && c[1] == 0 && c[2] == 0) return "0/0/0";
            if (c[0] == 1 && c[1] == 1 && c[2] == 0) return "1/1/0";
            return "other";
        }

        // a blocker that is not an object any more is a leftover, not a live region: that is a stop at once
        public static bool NoObject(string line) { return line != null && line.IndexOf("no object", StringComparison.Ordinal) >= 0; }

        // a good plan with a wide candidate search, the 60-candidate walk-away of the player's report
        public static bool WalkAway(string line)
        {
            int[] c = Candidates(line);
            return line != null && line.IndexOf("Succeeded", StringComparison.Ordinal) >= 0 && c != null && c[0] >= 50;
        }

        // a plan that failed for real: not Succeeded and not a Sim as the blocker (EA replans those)
        public static bool Failed(string line)
        {
            return line != null && line.IndexOf("Failed", StringComparison.Ordinal) >= 0 && line.IndexOf("blocker Sim", StringComparison.Ordinal) < 0;
        }

        // a bake counts when the chain reached the oven; "ate" when the meal steps ran too (the chain skips Eat when the Sim has no plate to eat from)
        public static bool Baked(IList<string> steps) { return steps.Contains("BakingDish_CookInOven"); }
        public static bool Ate(IList<string> steps) { return steps.Contains("Eat") && steps.Contains("ServingContainer_CleanUp"); }

        // "no use" (the Sim never used the target) is a stop only after kNoUseRow cycles in a row with a plan failure among them
        public const int kNoUseRow = 5;
        public static bool NoUseStop(int inARow, int planFailsInTheRow) { return inARow >= kNoUseRow && planFailsInTheRow > 0; }

        // the id of an "Object 0x<hex> no object" blocker in a plan line, else null
        public static string NoObjectId(string line)
        {
            if (line == null) return null;
            int at = line.IndexOf("blocker Object 0x", StringComparison.Ordinal);
            if (at < 0) return null;
            int s = at + 17, e = s;
            while (e < line.Length && Uri.IsHexDigit(line[e])) e++;
            if (e == s) return null;
            return line.Substring(e).StartsWith(" no object", StringComparison.Ordinal) ? line.Substring(s, e - s) : null;
        }

        // the rechecks of a failed plan (in order) and whether the Sim was free (no interaction) at each: permanent when every recheck failed and at least two of them were with the Sim free,
        // or every one of two or more failed with the same "no object" blocker id. A recheck that passes clears it; a fail while the Sim is in a social is not counted
        public static bool PermanentFail(string[] lines, bool[] simFree)
        {
            int freeFails = 0;
            string id = null;
            bool same = lines.Length >= 2;
            for (int i = 0; i < lines.Length; i++)
            {
                if (!Failed(lines[i])) return false;
                if (simFree[i]) freeFails++;
                string x = NoObjectId(lines[i]);
                if (x == null || (id != null && x != id)) same = false;
                id = x;
            }
            return freeFails >= 2 || same;
        }

        // a failed plan is a permanent fail when it is the same at +5 s and at +30 s, or its blocker is no object at all (okAt5/okAt30: the recheck plan was fine)
        public static bool Permanent(string firstLine, bool okAt5, bool okAt30)
        {
            if (NoObject(firstLine)) return true;
            return !okAt5 && !okAt30;
        }
    }
}
