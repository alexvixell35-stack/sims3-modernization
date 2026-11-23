using System.Collections.Generic;

namespace Ts3Fixes
{
    // Game-independent part of blanktns (BlankTns.cs, docs/869): the blank-text test and the frame filter, kept free of game
    // types so the offline self-test (tools/selftest/blanktns) compiles it alone.
    public static class BtnCore
    {
        public const string kPrefix = "[blank notice] sent by ";

        // EA shows the notice's main text as is; null, empty or whitespace shows an empty box.
        public static bool IsBlank(string text)
        {
            return text == null || text.Trim().Length == 0;
        }

        // Notification and UI plumbing between the sender and the notice: StyledNotification (its Show overloads and nested
        // types), NotificationManager (the hooked Add), NotificationSystem, the Sim.ShowTNS* helpers, our own hook and its Btn*
        // helpers. A frame with no type names nothing and is skipped too.
        public static bool IsPlumbing(string type, string method)
        {
            if (type == null) return true;
            if (Is(type, "Sims3.UI.StyledNotification") || Is(type, "Sims3.UI.NotificationManager")
                || Is(type, "Sims3.Gameplay.Utilities.NotificationSystem")) return true;
            if (type == "Sims3.Gameplay.Actors.Sim") return method != null && method.StartsWith("ShowTNS");
            return Is(type, "Ts3Fixes.Fixes+BtnHook") || (type == "Ts3Fixes.Fixes" && method != null && method.StartsWith("Btn"));
        }

        static bool Is(string type, string name) { return type == name || type.StartsWith(name + "+"); }

        // The first n frames outside the plumbing, innermost first, as "Type.Method" ("Assembly:Type.Method" with asms).
        public static List<string> Pick(string[] types, string[] methods, string[] asms, int n)
        {
            List<string> o = new List<string>();
            for (int i = 0; i < types.Length && o.Count < n; i++)
                if (!IsPlumbing(types[i], methods[i])) o.Add((asms != null ? asms[i] + ":" : "") + types[i] + "." + methods[i]);
            return o;
        }

        // The text a blank notice gets: "[blank notice] sent by A.B < C.D < E.F".
        public static string Text(List<string> frames)
        {
            return kPrefix + (frames.Count > 0 ? string.Join(" < ", frames.ToArray()) : "?");
        }
    }
}
