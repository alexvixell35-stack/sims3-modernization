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
    // windows are not touched. UI only. Counters: [2223] Unloads (also with the switch off), [2224] stale entries
    // removed, [2225] errors.
    // compositorghost (tuning kCompositorGhost, default off until measured) [verified IL + measured 2026-11-11]:
    // each Buy, Build or CAS visit leaves one CASCompositorController entry in the dict that is not the controller Load
    // returned: invisible, no parent, handle == key, and the engine still reports a compositor custom control under that
    // handle, so the native window is left behind too (phase-20261111-2216 / -2234: 10 visits, 10 entries; the probes
    // that showed this were removed after the measurement). UIManager.WrapperFromHandle registers a wrapper for any
    // custom-control handle without an entry, and no detach reaches this one. After Unload (sController is null then):
    // an entry whose handle is still a compositor window with no parent and not visible gets OnCustomDetach's steps
    // (UIManager.DeRegisterAllEvents, remove, then WindowBase.Dispose(false), not the controller's override, which acts on
    // the state of a loaded tool); an entry whose handle is no longer a compositor window (IUIManager.GetCustomControlID
    // differs from the id Load saw) is unregistered and removed, and its wrapper's handle is zeroed and its finalizer
    // suppressed, so WindowBase.Finalize never calls DestroyWindow on a handle the engine may have reused. A compositor
    // window that has a parent or is visible is left alone.
    // Counters: [2296] entries seen after Unload (also off; every Unload counts all of them), [2297] removed, [2298]
    // left (parented or visible compositor window) or errors, [2299] of [2297], live windows disposed.
    public static partial class Fixes
    {
        static FieldInfo sCmlController, sCmlDict, sCmlGui, sCmlWinHandle;
        static uint sCmlCustomId;
        static MethodInfo sCmlBaseDispose;

        static uint CmlCustomId(uint h)
        {
            try
            {
                if (sCmlGui == null) return 0;
                Sims3.SimIFace.IUIManager g = sCmlGui.GetValue(null) as Sims3.SimIFace.IUIManager;
                return g != null && h != 0 ? g.GetCustomControlID(h) : 0;
            }
            catch (Exception) { return 0; }
        }

        static void CmlGhostSweep()
        {
            int[] c = PatchCounters();
            try
            {
                if (sBilDict == null || sCmlController == null || sCmlController.GetValue(null) != null) return;
                IDictionary dict = sBilDict.GetValue(null) as IDictionary;
                if (dict == null) return;
                bool on = ShortcutOn("compositorghost");
                List<object> keys = new List<object>();
                List<bool> live = new List<bool>();
                foreach (DictionaryEntry e in dict)
                {
                    WindowBase w = e.Value as CASCompositorController;
                    if (w == null || w.Disposed || Convert.ToUInt32(sBilHandle.GetValue(w, null)) != Convert.ToUInt32(e.Key)) continue;
                    c[2296]++;
                    bool native = sCmlCustomId != 0 && CmlCustomId(Convert.ToUInt32(e.Key)) == sCmlCustomId;
                    // a live compositor window with no parent and not visible, while no compositor is loaded, is an orphan;
                    // anything else that still is a compositor window is left alone
                    if (native && (w.Parent != null || w.Visible)) { c[2298]++; continue; }
                    if (on) { keys.Add(e.Key); live.Add(native); }
                }
                for (int i = 0; i < keys.Count; i++)
                {
                    object k = keys[i];
                    try
                    {
                        WindowBase w = dict[k] as WindowBase;
                        sBilDeregister.Invoke(null, new object[] { w });
                        dict.Remove(k);
                        if (live[i])
                        {
                            // EA's OnCustomDetach steps (unregister, remove, dispose), with WindowBase's own Dispose: the
                            // controller's override acts on tool state that belongs to a loaded compositor
                            if (sCmlBaseDispose != null) ((Action<bool>)Delegate.CreateDelegate(typeof(Action<bool>), w, sCmlBaseDispose))(false);
                            c[2299]++;
                        }
                        else
                        {
                            if (sCmlWinHandle != null) sCmlWinHandle.SetValue(w, 0u);
                            GC.SuppressFinalize(w);
                        }
                        c[2297]++;
                    }
                    catch (Exception) { c[2298]++; }
                }
            }
            catch (Exception) { c[2298]++; }
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
            sCmlGui = typeof(UIManager).GetField("gUIMgr", st);
            MethodInfo bd = typeof(WindowBase).GetMethod("Dispose", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new Type[] { typeof(bool) }, null);
            sCmlBaseDispose = bd != null && bd.DeclaringType == typeof(WindowBase) ? bd : null;
            sCmlWinHandle = typeof(WindowBase).GetField("mWinHandle", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (sCmlGui == null || sCmlWinHandle == null) AppliedLog().Add("compositorghost: gUIMgr / mWinHandle not found, ghost sweep counts only");
            Patch(typeof(CASCompositorController), "Unload", Type.EmptyTypes, typeof(CmlHook), "UnloadHook", "UnloadOrig");
            Patch(typeof(CASCompositorController), "Load", Type.EmptyTypes, typeof(CmlHook), "LoadHook", "LoadOrig");
        }

        public class CmlHook
        {
            public static void UnloadHook()
            {
                UnloadOrig();
                int[] c = PatchCounters();
                c[2223]++;
                if (ShortcutOn("compositorleak") && sBilDict != null) c[2224] += SweepStaleCustomControls(c, 2225);
                CmlGhostSweep();
            }
            // remembers the compositor's custom-control id (compositorghost's check)
            public static void LoadHook()
            {
                LoadOrig();
                try { WindowBase cw = sCmlController != null ? sCmlController.GetValue(null) as WindowBase : null; if (cw != null) { uint id = CmlCustomId(Convert.ToUInt32(sBilHandle.GetValue(cw, null))); if (id != 0) sCmlCustomId = id; } } catch (Exception) { }
            }
            public static void LoadOrig() { } // receives CASCompositorController.Load()'s original header
            public static void UnloadOrig() { } // receives CASCompositorController.Unload's original header
        }
    }
}
