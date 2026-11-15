using System;
using System.Reflection;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // shelllighting (tuning kShellLighting, off by default; docs/561 §5). EA bug fix, exact for every shell.
    //
    // BuildableShell.OnStartup calls UpdateLightAlarms, which schedules today's lighting changes (Default / Few / Max at
    // sunrise and sunset offsets from the shell's Tuning) through AddAlarmWithRandomDelta. That helper drops every time
    // already past today (`if (!(num < 0f))`) [verified, decompiled BuildableShell.cs], and nothing applies the lighting
    // that should be showing now. So after a world load (or a shell placed in build mode) the shell keeps its Default
    // material until the next lighting time: load at 22:00 and the towers stay unlit until the next alarm, hours later.
    // Fix: after EA's OnStartup, the lighting of the most recent lighting time that has passed (today's, or yesterday's
    // when none passed today yet) is applied through EA's own DefaultLighting / FewLighting / MaxLighting, which keep
    // EA's Dystopia handling. EA's alarms are unchanged; with no passed time, nothing changes.
    // Counters: [2481] shells set to the current lighting, [2482] errors. Harness check: ShellLightingChecks.cs.
    public static partial class Fixes
    {
        static MethodInfo[] sShLight;  // DefaultLighting, FewLighting, MaxLighting (private EA methods)
        static PropertyInfo sShTuning;

        static void ApplyShellLighting()
        {
            const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            Type t = typeof(BuildableShell);
            sShLight = new MethodInfo[] { t.GetMethod("DefaultLighting", inst), t.GetMethod("FewLighting", inst), t.GetMethod("MaxLighting", inst) };
            sShTuning = t.GetProperty("Tuning", inst);
            if (sShLight[0] == null || sShLight[1] == null || sShLight[2] == null || sShTuning == null)
                AppliedLog().Add("shelllighting: BuildableShell lighting members not found, not patched");
            else Patch(typeof(BuildableShell), "OnStartup", typeof(ShHook), "StartupHook", "StartupOrig");
        }

        public class ShHook
        {
            public void StartupHook()
            {
                StartupOrig();
                if (!ShortcutOn("shelllighting")) return;  // fast path: switch off (runs once per shell per load)
                try { ShApplyCurrent((object)this as BuildableShell); } catch (Exception) { PatchCounters()[2482]++; }
            }
            public void StartupOrig() { } // receives BuildableShell.OnStartup's original body
        }

        // The lighting kind (0 Default, 1 Few, 2 Max) of the latest lighting time at or before `now` (hours of the day),
        // looking back into yesterday when nothing passed today yet; -1 when the tuning has no times at all.
        internal static int ShPickKind(float now, float sunrise, float sunset, float[][] fromSunrise, float[][] fromSunset)
        {
            int kind = -1;
            float best = float.NegativeInfinity;
            for (int k = 0; k < 3; k++)
            {
                for (int pass = 0; pass < 2; pass++)
                {
                    float[] times = pass == 0 ? fromSunrise[k] : fromSunset[k];
                    float baseHour = pass == 0 ? sunrise : sunset;
                    if (times == null) continue;
                    foreach (float off in times)
                    {
                        float at = baseHour + off;
                        if (at > now) at -= 24f;  // not reached today: yesterday's
                        if (at <= now && at > best) { best = at; kind = k; }
                    }
                }
            }
            return kind;
        }

        static int ShKindFor(BuildableShell shell, float now)
        {
            BuildableShell.BuildableShellTuning tu = sShTuning.GetValue(shell, null) as BuildableShell.BuildableShellTuning;
            if (tu == null) return -1;
            return ShPickKind(now, World.GetSunriseTime(), World.GetSunsetTime(),
                new float[][] { tu.kDefaultTimesFromSunrise, tu.kLowTimesFromSunrise, tu.kHighTimesFromSunrise },
                new float[][] { tu.kDefaultTimesFromSunset, tu.kLowTimesFromSunset, tu.kHighTimesFromSunset });
        }

        static void ShApplyCurrent(BuildableShell shell)
        {
            if (shell == null || shell.HasBeenDestroyed) return;
            int kind = ShKindFor(shell, SimClock.HoursPassedOfDay);
            if (kind < 0) return;
            sShLight[kind].Invoke(shell, null);
            PatchCounters()[2481]++;
        }
    }
}
