using System;
using System.Reflection;

namespace Ts3Fixes
{
    // overwatchrelretry (local 2026-11-18, default off): NRaas Overwatch (V123) CleanupRelationships.OnDelayedWorldLoadFinished
    // walks Relationship.sAllRelationships with foreach and, for a live Sim whose table has a dead Sim, removes that dead
    // Sim from the OUTER dictionary inside the walk (upstream source NRaasOverwatch/OverwatchSpace/Loadup/
    // CleanupRelationships.cs; it means the inner table). The next MoveNext throws InvalidOperationException "out of sync"
    // and the rest of the load cleanup never runs (Sunset Valley 2026-11-18 07:24). Each such run has removed at least one
    // outer key before it throws (the Remove that broke the walk), so running it again makes progress, and a run that finds
    // only keys already gone completes. Fix: that exact exception runs Overwatch's method again, at most 50 times. With
    // Overwatch not installed nothing is hooked. The upstream fix (remove from relationsA.Value) is for its authors.
    // Counters [3991] re-runs, [3992] seen with the switch off (Overwatch's exception goes on), [3993] still failing after
    // 50 runs (rethrown).
    public static partial class Fixes
    {
        static bool sOrrTried;

        // called with ApplyOverwatchNotice (startup and first world load, before Overwatch's delayed load functions run)
        internal static void ApplyOwRelRetry(bool lastTry)
        {
            if (sOrrTried) return;
            Assembly ow = null;
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                if (a.GetName().Name == "NRaasOverwatch") { ow = a; break; }
            if (ow == null) { if (lastTry) sOrrTried = true; return; }
            sOrrTried = true;
            Type t = ow.GetType("NRaas.OverwatchSpace.Loadup.CleanupRelationships");
            if (t == null) { AppliedLog().Add("overwatchrelretry: CleanupRelationships not found, off"); return; }
            Patch(t, "OnDelayedWorldLoadFinished", Type.EmptyTypes, typeof(OwRelRetryHook), "LoadHook", "LoadOrig");
        }

        public class OwRelRetryHook
        {
            public void LoadHook()
            {
                for (int i = 0; ; i++)
                {
                    try { LoadOrig(); return; }
                    catch (InvalidOperationException e)
                    {
                        if (e.Message != "out of sync") throw;
                        if (!ShortcutOn("overwatchrelretry")) { PatchCounters()[3992]++; throw; }
                        if (i >= 50) { PatchCounters()[3993]++; throw; }
                        PatchCounters()[3991]++;
                    }
                }
            }
            public void LoadOrig() { } // receives CleanupRelationships.OnDelayedWorldLoadFinished's original body
        }
    }
}
