using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Sims3.UI;
using Sims3.UI.CAS;

namespace Ts3Fixes
{
    // compositorleak (tuning kCompositorLeak, default off until measured; docs/191 "CAS design tool").
    //
    // EA bug: CASCompositorController.Unload (from CASPuck.Dispose on every CAS exit, and BBCompositorManager.Unload) calls
    // sController.Dispose() and sLayout.Shutdown()/Dispose() [verified IL, instructions 56-67]. WindowBase.Dispose destroys
    // the native window and returns the wrapper to the pool, but never removes it from the static
    // UIManager.mCustomControlInstanceDict; only UIManager.OnCustomDetach does that, and a layout shutdown gives the tool's
    // custom-control children (the tab holder, the material and preset ItemGrids) no detach either. So every CAS visit
    // leaves the whole design-tool tree registered, with its events in mEventRegistry, under handles the engine may reuse.
    // Measured 2026-11-11 (Wine, phase-20261111-1347): 36 CASCompositorController, 18 ItemGrid and 4 TabControl entries
    // off the tree after the Buy, CAS and Build phases.
    //
    // v1 (collect sController's tree before Unload, unregister after) found nothing to remove and threw (measured
    // 2026-11-11, phase-20261111-1528: 0 removed, 4 errors, 4 extra CASPuck entries). Measured per CAS visit: one
    // disposed CASCompositorController entry (the one Unload disposed) and one live, parentless one (cause open).
    // v2: after Unload, buyiconleak's SweepStaleCustomControls removes the entries whose window is disposed or whose
    // handle no longer matches (UIManager.DeRegisterAllEvents for each, as OnCustomDetach does short of Dispose). Live
    // windows are not touched. UI only. Counters: [2117] Unloads (also with the switch off), [2118] stale entries
    // removed, [2119] errors.
    // Probe (read only, always on while compositorleak is patched): just before Unload, the CASCompositorController
    // entries in the dict: [2178] the current sController, [2179] other live entries (not disposed, handle == key),
    // [2180] other entries already disposed or with a reused handle. Measured off: one live parentless controller per
    // Buy or Build visit (phase-20261111-2047); the probe tells whether it already exists before Unload.
    public static partial class Fixes
    {
        static FieldInfo sCmlController, sCmlDict;

        static void CmlProbe()
        {
            int[] c = PatchCounters();
            try
            {
                if (sBilDict == null || sCmlController == null) return;
                IDictionary dict = sBilDict.GetValue(null) as IDictionary;
                object cur = sCmlController.GetValue(null);
                if (dict == null) return;
                foreach (DictionaryEntry e in dict)
                {
                    WindowBase w = e.Value as WindowBase;
                    if (w == null || !(w is CASCompositorController)) continue;
                    if (object.ReferenceEquals(w, cur)) c[2178]++;
                    else if (!w.Disposed && Convert.ToUInt32(sBilHandle.GetValue(w, null)) == Convert.ToUInt32(e.Key)) c[2179]++;
                    else c[2180]++;
                }
            }
            catch (Exception) { }
        }
        static MethodInfo sCmlDeregister;

        static void ApplyCompositorLeak()
        {
            const BindingFlags st = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            sCmlController = typeof(CASCompositorController).GetField("sController", st);
            sCmlDict = typeof(UIManager).GetField("mCustomControlInstanceDict", st);
            sCmlDeregister = typeof(UIManager).GetMethod("DeRegisterAllEvents", st, null, new Type[] { typeof(WindowBase) }, null);
            if (sCmlController == null || sCmlDict == null || sCmlDeregister == null)
            {
                AppliedLog().Add("compositorleak: members not found (controller " + (sCmlController != null) + ", dict " + (sCmlDict != null)
                    + ", deregister " + (sCmlDeregister != null) + "), not patched");
                return;
            }
            Patch(typeof(CASCompositorController), "Unload", Type.EmptyTypes, typeof(CmlHook), "UnloadHook", "UnloadOrig");
        }

        public class CmlHook
        {
            public static void UnloadHook()
            {
                CmlProbe();
                UnloadOrig();
                int[] c = PatchCounters();
                c[2117]++;
                if (ShortcutOn("compositorleak") && sBilDict != null) c[2118] += SweepStaleCustomControls(c, 2119);
            }
            public static void UnloadOrig() { } // receives CASCompositorController.Unload's original header
        }
    }
}
