using System;
using System.Text;

namespace Ts3Fixes
{
    // Shared check lines for the route-fail / line fixes (queueproximity, subroutefail, mopunreachable, petpuddletile).
    // Each Check prints its pure decision with constants (off BROKEN, on FIXED), the controls, the live-path state (the
    // hook classes really swapped in, the reflection handles found) and its counters. A check whose live path is missing
    // says UNEXPECTED, so a hook that never got in place cannot print FIXED.
    public static partial class Fixes
    {
        static string RlCounters(string name, int first, string[] what, string missing)
        {
            int[] c = PatchCounters();
            StringBuilder b = new StringBuilder(name + ":");
            for (int i = 0; i < what.Length; i++) b.Append(i == 0 ? " " : ", ").Append(what[i]).Append(' ').Append(first + i).Append('=').Append(c[first + i]);
            string live = RlJoin(missing, RlLive(name));
            return b.Append("; members found=").Append(live.Length == 0).Append(live.Length == 0 ? "" : " (missing: " + live + ")").ToString();
        }

        static string RlLine(string name, bool on, string fixedText, bool fixedNow, string ctlText, bool controls, string missing, string counters)
        {
            bool membersOk = RlJoin(missing, RlLive(name)).Length == 0;
            string verdict = !controls || !membersOk ? "UNEXPECTED" : !on ? (fixedNow ? "UNEXPECTED" : "BROKEN") : (fixedNow ? "FIXED" : "UNEXPECTED");
            return name + " check (switch " + (on ? "on" : "off") + "): " + fixedText + "=" + fixedNow + "; control: " + ctlText + "; controls ok=" + controls
                + "; verdict: " + verdict + "; " + counters;
        }

        // The hook classes each switch needs in place ("" when all are).
        static string RlLive(string name)
        {
            switch (name)
            {
                case "queueproximity": return RlSwapped(typeof(SqPlaceHook));
                case "subroutefail": return RlJoin(RlSwapped(typeof(SrfTryHook)), RlSwapped(typeof(SrfPlayHook)));
                case "mopunreachable": return RlJoin(RlSwapped(typeof(MopRunHook)), RlSwapped(typeof(MopFloorHook)));
                case "petpuddletile": return RlSwapped(typeof(PetPuddleHook));
            }
            return "";
        }

        // Fixes.Patch records each swap in sPatched (by method; a stacked swap keeps only its last hook), so one entry is enough.
        static string RlSwapped(Type hook)
        {
            foreach (Type h in sPatched.Values) if (h == hook) return "";
            return hook.Name + " not swapped";
        }

        static string RlJoin(string a, string b)
        {
            if (string.IsNullOrEmpty(a)) return b ?? "";
            if (string.IsNullOrEmpty(b)) return a;
            return a + ", " + b;
        }

        // Names of the null handles in (name, handle) pairs, space separated ("" when all were found).
        static string RlNames(params object[] pairs)
        {
            StringBuilder b = new StringBuilder();
            for (int i = 0; i + 1 < pairs.Length; i += 2) if (pairs[i + 1] == null) b.Append(b.Length == 0 ? "" : " ").Append(pairs[i]);
            return b.ToString();
        }
    }
}
