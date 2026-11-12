using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Sims3.UI;
using Sims3.UI.CAS;

namespace Ts3Fixes
{
    // casrowicons (tuning kCasRowIcons, default off until measured; docs/257 "CAS").
    //
    // EA bug [verified IL, UI.dll]: CASClothingRow.SetWindowData takes each grid window's icon (GetChildByID(35, true) as
    // CustomContentIcon, IL 70 / 184 / 203), which registers it in UIManager.mCustomControlInstanceDict
    // (WrapperFromHandle). CASClothingRow.Dispose disposes the thumbnails, unhooks the grid windows' events and calls
    // WindowBase.Dispose, but never touches the icons, and no detach reaches them, so every row CAS shows leaves up to
    // five icon entries for the session (measured 2026-11-11: +90 live, parentless "CustomContentIcon id 35" entries per
    // CAS visit that browses clothing categories, 0 for a visit that does not).
    // The pass: just before the row's Dispose, its icons that are in the dict under their own handle are noted (with the
    // engine's custom-control id for that handle); after it, each one EA left there gets the cleanup OnCustomDetach would
    // have done: when the engine still reports the same custom control under the handle, UIManager.DeRegisterAllEvents,
    // remove, WindowBase.Dispose(false); when the window is gone (destroyed with the row), DeRegisterAllEvents and remove,
    // and the wrapper's handle is zeroed and its finalizer suppressed, so WindowBase.Finalize never destroys a handle the
    // engine may have reused. Icons are never disposed before the row (that order re-registers them on the detach),
    // and an icon whose window is alive and still has a parent is watched instead (v3): it is checked again after the
    // next CASPuck.Unload / CASFamilyScreen.Unload, and cleaned up then if EA left it registered with its window gone or
    // parentless (measured: none is orphaned at the row's Dispose; the leftovers appear after the layouts shut down).
    // Counters: [2194] icons left in the dict after a row's Dispose (also off), [2195] removed with a live window,
    // [2196] removed with a dead handle, [2197] errors.
    //
    // casfiltergrid (tuning kCasFilterGrid, default off until measured).
    // EA bug [verified IL]: CASPuck.Init keeps its CatalogProductFilter (child 153931569) in mContentTypeFilter, and
    // CatalogProductFilter.Init turns DisposeOnDetach off on its child ItemGrid 189775728 (see FilterGridLeak.cs), so
    // every CAS exit (CASPuck.Unload: Layout.Shutdown + Dispose) leaves that grid registered (measured: +1 per CAS exit).
    // The pass: just before CASPuck.Unload (which nulls gSingleton first), the grid gets DisposeOnDetach back on, so
    // EA's own OnCustomDetach handles it, as filtergridleak does for Buy, Build and Blueprint.
    // Counters: [2181] filter grids found at Unload (also off), [2182] restored, [2183] errors.
    public static partial class Fixes
    {
        static FieldInfo sCriGridWins, sCfgSingleton, sCfgFilter;

        // v3 (deferred): controls still attached and alive when their row / screen closes, checked again at the next CAS or
        // family-screen exit; kind 0 = row icon (casrowicons, slots 2194-2196), 1 = family-screen control (famcursorleak,
        // slots 2187-2189)
        struct CriWatch { public WindowBase W; public uint H, Id; public int Kind; }
        static readonly List<CriWatch> sCriWatch = new List<CriWatch>();
        const int kCriWatchMax = 20000;

        internal static void CriWatchAdd(WindowBase w, uint h, uint id, int kind)
        {
            if (sCriWatch.Count >= kCriWatchMax) return;
            CriWatch x; x.W = w; x.H = h; x.Id = id; x.Kind = kind; sCriWatch.Add(x);
        }

        // each watched control EA left in the dict whose window is gone or alive with no parent gets OnCustomDetach's
        // cleanup; one still attached stays watched
        internal static void CriWatchSweep()
        {
            if (sCriWatch.Count == 0) return;
            int[] c = PatchCounters();
            IDictionary dict = null;
            try { dict = sBilDict.GetValue(null) as IDictionary; } catch (Exception) { }
            if (dict == null) return;
            List<CriWatch> keep = new List<CriWatch>();
            foreach (CriWatch x in sCriWatch)
            {
                int b = x.Kind == 0 ? 2194 : 2187;
                try
                {
                    if (x.W.Disposed || !dict.Contains(x.H) || !object.ReferenceEquals(dict[x.H], x.W)) continue;  // EA cleaned it
                    bool live = x.Id != 0 && CmlCustomId(x.H) == x.Id;
                    if (live && x.W.Parent != null) { keep.Add(x); continue; }
                    c[b]++;
                    if (!ShortcutOn(x.Kind == 0 ? "casrowicons" : "famcursorleak")) continue;
                    sBilDeregister.Invoke(null, new object[] { x.W });
                    dict.Remove(x.H);
                    if (live && sCriBaseDispose != null) ((Action<bool>)Delegate.CreateDelegate(typeof(Action<bool>), x.W, sCriBaseDispose))(false);
                    else { sCmlWinHandle.SetValue(x.W, 0u); GC.SuppressFinalize(x.W); }
                    c[x.Kind == 0 ? 2195 : 2188]++;
                }
                catch (Exception) { c[x.Kind == 0 ? 2197 : 2189]++; }
            }
            sCriWatch.Clear(); sCriWatch.AddRange(keep);
        }
        static MethodInfo sCriBaseDispose;

        static void ApplyCasRowIcons()
        {
            const BindingFlags st = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            sCriGridWins = typeof(CASClothingRow).GetField("mGridWindows", inst);
            MethodInfo bd = typeof(WindowBase).GetMethod("Dispose", inst, null, new Type[] { typeof(bool) }, null);
            sCriBaseDispose = bd != null && bd.DeclaringType == typeof(WindowBase) ? bd : null;
            if (sCriGridWins != null && sBilDict != null && sBilDeregister != null && sCmlWinHandle != null)
                Patch(typeof(CASClothingRow), "Dispose", Type.EmptyTypes, typeof(CriHook), "DisposeHook", "DisposeOrig");
            else AppliedLog().Add("casrowicons: members not found, not patched");

            sCfgSingleton = typeof(CASPuck).GetField("gSingleton", st);
            sCfgFilter = typeof(CASPuck).GetField("mContentTypeFilter", inst);
            if (sCfgSingleton != null && sCfgFilter != null)
                Patch(typeof(CASPuck), "Unload", Type.EmptyTypes, typeof(CriHook), "PuckUnloadHook", "PuckUnloadOrig");
            else AppliedLog().Add("casfiltergrid: CASPuck members not found, not patched");
        }

        public class CriHook
        {
            public void DisposeHook()
            {
                List<uint> hs = null; List<CustomContentIcon> icons = null; List<uint> ids = null;
                IDictionary dict = null;
                try
                {
                    dict = sBilDict.GetValue(null) as IDictionary;
                    WindowBase[] gw = sCriGridWins.GetValue(this) as WindowBase[];
                    if (dict != null && gw != null)
                        foreach (WindowBase g in gw)
                        {
                            if (g == null || g.Disposed) continue;
                            CustomContentIcon icon = g.GetChildByID(35u, true) as CustomContentIcon;
                            if (icon == null || icon.Disposed) continue;
                            uint h = Convert.ToUInt32(sBilHandle.GetValue(icon, null));
                            if (h == 0 || !dict.Contains(h) || !object.ReferenceEquals(dict[h], icon)) continue;
                            if (hs == null) { hs = new List<uint>(); icons = new List<CustomContentIcon>(); ids = new List<uint>(); }
                            hs.Add(h); icons.Add(icon); ids.Add(CmlCustomId(h));
                        }
                }
                catch (Exception) { PatchCounters()[2197]++; }
                DisposeOrig();
                if (hs == null) return;
                int[] c = PatchCounters();
                bool on = ShortcutOn("casrowicons");
                for (int i = 0; i < hs.Count; i++)
                {
                    try
                    {
                        CustomContentIcon icon = icons[i];
                        if (icon.Disposed || !dict.Contains(hs[i]) || !object.ReferenceEquals(dict[hs[i]], icon)) continue;
                        bool live = ids[i] != 0 && CmlCustomId(hs[i]) == ids[i];
                        // v2/v3: an icon whose window is alive and still attached is watched and checked again at the next
                        // CAS exit (v1 removed 1830 such icons per visit and the leftovers did not move; v2 found none
                        // orphaned at this point: they lose their parent later, when the category layout shuts down)
                        if (live && icon.Parent != null) { CriWatchAdd(icon, hs[i], ids[i], 0); continue; }
                        c[2194]++;
                        if (!on) continue;
                        sBilDeregister.Invoke(null, new object[] { icon });
                        dict.Remove(hs[i]);
                        if (live && sCriBaseDispose != null)
                        {
                            ((Action<bool>)Delegate.CreateDelegate(typeof(Action<bool>), icon, sCriBaseDispose))(false);
                            c[2195]++;
                        }
                        else
                        {
                            sCmlWinHandle.SetValue(icon, 0u);
                            GC.SuppressFinalize(icon);
                            c[2196]++;
                        }
                    }
                    catch (Exception) { c[2197]++; }
                }
            }
            public void DisposeOrig() { } // receives CASClothingRow.Dispose()'s original header

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
                CriWatchSweep();
            }
            public static void PuckUnloadOrig() { } // receives CASPuck.Unload()'s original header
        }
    }
}
