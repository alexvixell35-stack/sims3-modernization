using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Sims3.UI;
using Sims3.UI.CAS;

namespace Ts3Fixes
{
    // casexitsweep (tuning kCasExitSweep, default off until measured; docs/545 "CAS").
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
    // a dead window (no custom control under the handle) is left to compositorghost / the finalizer. v2: entries skipped
    // because they still had a native parent are checked again at the next CASPuck.Load (their layouts are gone by then)
    // and get the same cleanup if they are now natively detached. UI only.
    // Counters: [2300] detached entries found at CAS exit (also off), [2301] cleaned up, [2302] skipped (still parented,
    // a registered class the engine no longer reports), [2303] errors.
    //
    // casfiltergrid (tuning kCasFilterGrid). EA bug [verified IL]: CASPuck.Init keeps its CatalogProductFilter (child
    // 153931569) in mContentTypeFilter, and CatalogProductFilter.Init turns DisposeOnDetach off on its child ItemGrid
    // 189775728 (see FilterGridLeak.cs), so every CAS exit leaves that grid registered (measured: +1 per CAS exit). The
    // pass: just before CASPuck.Unload (which nulls gSingleton first) the grid gets DisposeOnDetach back on, so EA's own
    // OnCustomDetach handles it. Counters: [2287] filter grids found at Unload (also off), [2288] restored, [2289] errors.
    public static partial class Fixes
    {
        static FieldInfo sCfgSingleton, sCfgFilter;
        static MethodInfo sCesBaseDispose;
        static Dictionary<uint, bool> sCesBefore;
        // entries registered during a CAS visit that still had a native parent at its exit (most belong to layouts EA
        // shuts down right after); checked again at the next CASPuck.Load (v2)
        static Dictionary<uint, WindowBase> sCesLater;

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
            Dictionary<uint, WindowBase> later = new Dictionary<uint, WindowBase>();
            foreach (uint h in keys)
            {
                try
                {
                    WindowBase w = dict[h] as WindowBase;
                    if (w == null || w.Disposed || Convert.ToUInt32(sBilHandle.GetValue(w, null)) != h) continue;
                    if (gui.GetCustomControlID(h) == 0) continue;   // dead: not ours (compositorghost / finalizer)
                    if (gui.GetParentWindow(h) != 0) { c[2302]++; if (later.Count < 5000) later[h] = w; continue; }
                    c[2300]++;
                    if (!on) continue;
                    sBilDeregister.Invoke(null, new object[] { w });
                    dict.Remove(h);
                    ((Action<bool>)Delegate.CreateDelegate(typeof(Action<bool>), w, sCesBaseDispose))(false);
                    c[2301]++;
                }
                catch (Exception) { c[2303]++; }
            }
            sCesLater = later;
        }

        // v2: the previous visit's still-parented entries, now that their layouts are gone: the same cleanup for each one
        // that is still registered as the same wrapper, live, and natively detached
        static void CesSweepLater()
        {
            Dictionary<uint, WindowBase> later = sCesLater;
            sCesLater = null;
            if (later == null || later.Count == 0) return;
            int[] c = PatchCounters();
            IDictionary dict = sBilDict.GetValue(null) as IDictionary;
            Sims3.SimIFace.IUIManager gui = sCmlGui.GetValue(null) as Sims3.SimIFace.IUIManager;
            if (dict == null || gui == null) return;
            bool on = ShortcutOn("casexitsweep");
            foreach (KeyValuePair<uint, WindowBase> kv in later)
            {
                try
                {
                    uint h = kv.Key; WindowBase w = kv.Value;
                    if (w == null || w.Disposed || !dict.Contains(h) || !object.ReferenceEquals(dict[h], w)) continue;
                    if (gui.GetCustomControlID(h) == 0 || gui.GetParentWindow(h) != 0) continue;
                    c[2300]++;
                    if (!on) continue;
                    sBilDeregister.Invoke(null, new object[] { w });
                    dict.Remove(h);
                    ((Action<bool>)Delegate.CreateDelegate(typeof(Action<bool>), w, sCesBaseDispose))(false);
                    c[2301]++;
                }
                catch (Exception) { c[2303]++; }
            }
        }

        public class CesHook
        {
            public static void PuckLoadHook()
            {
                try { CesSweepLater(); } catch (Exception) { PatchCounters()[2303]++; }
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
                catch (Exception) { sCesBefore = null; PatchCounters()[2303]++; }
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
                        c[2287]++;
                        if (ShortcutOn("casfiltergrid")) { g.DisposeOnDetach = true; c[2288]++; }
                    }
                }
                catch (Exception) { c[2289]++; }
                PuckUnloadOrig();
                try { CesSweep(); } catch (Exception) { c[2303]++; }
            }
            public static void PuckUnloadOrig() { } // receives CASPuck.Unload()'s original header
        }
    }
}
