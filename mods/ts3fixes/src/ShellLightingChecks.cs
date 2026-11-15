using System.Collections.Generic;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // shelllighting harness check (docs/561 §5). Read-only: no material is set, no alarm is added.
    public static partial class Fixes
    {
        // ShellLightingCheck run: EA's default tuning (Default at sunrise-1, Few at sunrise-3 and sunset-2, Max at
        //   sunset+3), loaded at 22:00 with sunrise 6 / sunset 20. EA applies nothing (Default stays). The latest passed
        //   time is Few at 18:00 (Max at 23:00 not reached yet). off BROKEN = nothing applied; on FIXED = Few. Also lists the world's shells with the kind the fix would apply now (read only).
        //   result  counters 2481-2482
        public static object ShellLightingCheck(string what)
        {
            int[] c = PatchCounters();
            string counters = "shelllighting: shells set 2481=" + c[2481] + ", errors 2482=" + c[2482];
            if (what == "result") return counters;
            if (what != "run") return "shelllighting <run|result>";
            if (sShTuning == null) return "shelllighting check: not patched (N/A)";
            bool on = ShortcutOn("shelllighting");
            int k = ShPickKind(22f, 6f, 20f,
                new float[][] { new float[] { -1f }, new float[] { -3f }, new float[0] },
                new float[][] { new float[0], new float[] { -2f }, new float[] { 3f } });
            int applied = on ? k : -1;
            string verdict = !on ? (applied < 0 ? "BROKEN" : "UNEXPECTED") : (applied == 1 ? "FIXED" : "UNEXPECTED");
            string[] names = { "Default", "Few", "Max" };
            int n = 0;
            List<string> sample = new List<string>();
            foreach (BuildableShell s in Sims3.Gameplay.Queries.GetObjects<BuildableShell>())
            {
                n++;
                if (sample.Count < 5) { int kk = ShKindFor(s, SimClock.HoursPassedOfDay); sample.Add(s.GetType().Name + "=" + (kk < 0 ? "none" : names[kk])); }
            }
            return "shelllighting check (switch " + (on ? "on" : "off") + "): 22:00 load -> applied " + (applied < 0 ? "nothing (Default kept)" : names[applied])
                + "; verdict: " + verdict + "; world shells " + n + " now " + string.Join(", ", sample.ToArray()) + "; " + counters;
        }
    }
}
