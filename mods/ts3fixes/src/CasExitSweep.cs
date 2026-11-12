using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Sims3.UI;
using Sims3.UI.CAS;

namespace Ts3Fixes
{
    // casexitsweep (tuning kCasExitSweep, default off until measured; docs/257 "CAS").
    //
    // EA bug [measured 2026-11-12, census with native parent handles]: every CAS visit that browses clothing categories
    // leaves about 90 CustomContentIcon entries (control id 35, the small content icon of a part cell) in
    // UIManager.mCustomControlInstanceDict whose native windows are alive, visible and have no native parent
    // (IUIManager.GetParentWindow == 0): the engine detached them from their cells and nothing destroys them, so window
    // and wrapper stay for the session. The CAS code never reparents them [verified IL: no AddChild / RemoveChild on
    // them in Sims3.UI.CAS]; the detach happens natively when the rows' cells go away. CASClothingRow.Dispose finds
    // them still attached (casrowicons, removed: it found none orphaned at that point).
    // The pass: CASPuck.Load notes the handles already registered; just after CASPuck.Unload (CAS is gone), each entry
    // registered since then that is still a live custom control under its own handle with no native parent gets what
    // OnCustomDetach does: UIManager.DeRegisterAllEvents, remove from the dict, WindowBase.Dispose(false) (EA's
    // DestroyWindow, then the handle is zeroed). An entry with a native parent, or one from before CAS, is not touched;
    // a dead window (no custom control under the handle) is left to compositorghost / the finalizer. UI only.
    // Counters: [2194] detached entries found at CAS exit (also off), [2195] cleaned up, [2196] skipped (still parented,
    // a registered class the engine no longer reports), [2197] errors.
    //
    // casfiltergrid (tuning kCasFilterGrid). EA bug [verified IL]: CASPuck.Init keeps its CatalogProductFilter (child
    // 153931569) in mContentTypeFilter, and CatalogProductFilter.Init turns DisposeOnDetach off on its child ItemGrid
    // 189775728 (see FilterGridLeak.cs), so every CAS exit leaves that grid registered (measured: +1 per CAS exit). The
    // pass: just before CASPuck.Unload (which nulls gSingleton first) the grid gets DisposeOnDetach back on, so EA's own
    // OnCustomDetach handles it. Counters: [2181] filter grids found at Unload (also off), [2182] restored, [2183] errors.
    public static partial class Fixes
    {
        static FieldInfo sCfgSingleton, sCfgFilter;
        static MethodInfo sCesBaseDispose;
        static Dictionary<uint, bool> sCesBefore;

        static void ApplyCasExitSweep()
        {
            const BindingFlags st = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            MethodInfo bd = typeof(WindowBase).GetMethod("Dispose", inst, null, new Type[] { typeof(bool) }, null);
            sCesBaseDispose = bd != null && bd.DeclaringType == typeof(WindowBase) ? bd : null;
            sCfgSingleton = typeof(CASPuck).GetField("gSingleton", st);
            sCfgFilter = typeof(CASPuck).GetField("mContentTypeFilter", inst);
            if (sCfgSingleton != null && sCfgFilter != null)
                Patch(typeof(CASPuck), "Unload", Type.EmptyTypes, typeof(CesHook), "PuckUnloadHook", "PuckUnloadOrig");
            else AppliedLog().Add("casfiltergrid: CASPuck members not found, not patched");
            if (sBilDict != null && sBilDeregister != null && sCmlGui != null && sCesBaseDispose != null)
                Patch(typeof(CASPuck), "Load", Type.EmptyTypes, typeof(CesHook), "PuckLoadHook", "PuckLoadOrig");
            else AppliedLog().Add("casexitsweep: members not found, not patched");
        }

        static void CesSweep()
        {
            Dictionary<uint, bool> before = sCesBefore;
            sCesBefore = null;
            if (before == null) return;
            int[] c = PatchCounters();
            IDictionary dict = sBilDict.GetValue(null) as IDictionary;
            Sims3.SimIFace.IUIManager gui = sCmlGui.GetValue(null) as Sims3.SimIFace.IUIManager;
            if (dict == null || gui == null) return;
            List<uint> keys = new List<uint>();
            foreach (object k in dict.Keys) { uint h = Convert.ToUInt32(k); if (!before.ContainsKey(h)) keys.Add(h); }
            bool on = ShortcutOn("casexitsweep");
            foreach (uint h in keys)
            {
                try
                {
                    WindowBase w = dict[h] as WindowBase;
                    if (w == null || w.Disposed || Convert.ToUInt32(sBilHandle.GetValue(w, null)) != h) continue;
                    if (gui.GetCustomControlID(h) == 0) continue;   // dead: not ours (compositorghost / finalizer)
                    if (gui.GetParentWindow(h) != 0) { c[2196]++; continue; }
                    c[2194]++;
                    if (!on) continue;
                    sBilDeregister.Invoke(null, new object[] { w });
                    dict.Remove(h);
                    ((Action<bool>)Delegate.CreateDelegate(typeof(Action<bool>), w, sCesBaseDispose))(false);
                    c[2195]++;
                }
                catch (Exception) { c[2197]++; }
            }
        }

        public class CesHook
        {
            public static void PuckLoadHook()
            {
                try
                {
                    IDictionary dict = sBilDict.GetValue(null) as IDictionary;
                    if (dict != null)
                    {
                        Dictionary<uint, bool> b = new Dictionary<uint, bool>();
                        foreach (object k in dict.Keys) b[Convert.ToUInt32(k)] = true;
                        sCesBefore = b;
                    }
                }
                catch (Exception) { sCesBefore = null; PatchCounters()[2197]++; }
                PuckLoadOrig();
            }
            public static void PuckLoadOrig() { } // receives CASPuck.Load()'s original header

            public static void PuckUnloadHook()
            {
                int[] c = PatchCounters();
                try
                {
                    object puck = sCfgSingleton.GetValue(null);
                    WindowBase filter = puck != null ? sCfgFilter.GetValue(puck) as WindowBase : null;
                    ItemGrid g = filter != null && !filter.Disposed ? filter.GetChildByID(kFglGridId, true) as ItemGrid : null;
                    if (g != null && !g.Disposed && !g.DisposeOnDetach)
                    {
                        c[2181]++;
                        if (ShortcutOn("casfiltergrid")) { g.DisposeOnDetach = true; c[2182]++; }
                    }
                }
                catch (Exception) { c[2183]++; }
                PuckUnloadOrig();
                try { CesSweep(); } catch (Exception) { c[2197]++; }
            }
            public static void PuckUnloadOrig() { } // receives CASPuck.Unload()'s original header
        }
    }
}
