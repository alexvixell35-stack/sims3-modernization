using System;
using System.Collections;
using System.Collections.Generic;
using Sims3.UI;

namespace Ts3Fixes
{
    // destroychildren (tuning kDestroyChildren, default off until measured; docs/257 "CAS / HUD").
    //
    // EA bug [verified IL, UI.dll]: WindowBase.DestroyAllChildren calls UIManager.DestroyAllChildren, which goes straight
    // to IUIManager.DestroyAllChildren(handle): the native children are destroyed and no OnCustomDetach runs, so every
    // custom control under the window keeps its UIManager.mCustomControlInstanceDict entry and its events, pointing at a
    // destroyed window, for the session. Callers: CASFamilyScreen.RefreshFamily (the family tree's CAFThumb per Sim,
    // measured 2026-11-12: about +15 CAFThumb entries per family-tree open), FamilyTreeDialog.RefreshTree (the live-mode
    // family tree) and MapTagController.Dispose.
    // The pass: before the call, the custom controls under the window that are in the dict under their own handle are
    // noted with the engine's custom-control id for that handle; after it, each one still there whose window is gone
    // (the engine reports another or no custom control under the handle) gets OnCustomDetach's bookkeeping:
    // UIManager.DeRegisterAllEvents and remove; its window is already destroyed, so instead of Dispose its handle is
    // zeroed and its finalizer suppressed (WindowBase.Finalize would call DestroyWindow on a handle the engine may have
    // reused). A control whose window is still alive is left alone. UI only.
    // Counters: [2184] entries left by DestroyAllChildren (also off), [2185] removed, [2186] left alive or errors.
    public static partial class Fixes
    {
        static void ApplyDestroyChildren()
        {
            if (sBilDict == null || sBilDeregister == null || sCmlWinHandle == null) { AppliedLog().Add("destroychildren: members not found, not patched"); return; }
            Patch(typeof(WindowBase), "DestroyAllChildren", Type.EmptyTypes, typeof(DclHook), "DestroyHook", "DestroyOrig");
        }

        static void DclCollect(WindowBase w, IDictionary dict, List<WindowBase> found, int depth)
        {
            if (w == null || depth > 16 || found.Count > 4000) return;
            for (uint i = 0; i < 2000; i++)
            {
                WindowBase ch;
                try { ch = w.GetChildByIndex(i); } catch (Exception) { break; }
                if (ch == null) break;
                try
                {
                    uint h = Convert.ToUInt32(sBilHandle.GetValue(ch, null));
                    if (h != 0 && dict.Contains(h) && object.ReferenceEquals(dict[h], ch)) found.Add(ch);
                }
                catch (Exception) { }
                DclCollect(ch, dict, found, depth + 1);
            }
        }

        public class DclHook
        {
            public void DestroyHook()
            {
                List<WindowBase> found = null; List<uint> hs = null, ids = null;
                IDictionary dict = null;
                try
                {
                    dict = sBilDict.GetValue(null) as IDictionary;
                    if (dict != null && dict.Count > 0)
                    {
                        found = new List<WindowBase>();
                        DclCollect((WindowBase)(object)this, dict, found, 0);
                        hs = new List<uint>(); ids = new List<uint>();
                        foreach (WindowBase w in found) { uint h = Convert.ToUInt32(sBilHandle.GetValue(w, null)); hs.Add(h); ids.Add(CmlCustomId(h)); }
                    }
                }
                catch (Exception) { found = null; PatchCounters()[2186]++; }
                DestroyOrig();
                if (found == null || found.Count == 0) return;
                int[] c = PatchCounters();
                bool on = ShortcutOn("destroychildren");
                for (int i = 0; i < found.Count; i++)
                {
                    try
                    {
                        WindowBase w = found[i];
                        if (w.Disposed || !dict.Contains(hs[i]) || !object.ReferenceEquals(dict[hs[i]], w)) continue;
                        if (ids[i] != 0 && CmlCustomId(hs[i]) == ids[i]) { c[2186]++; continue; }  // window still alive
                        c[2184]++;
                        if (!on) continue;
                        sBilDeregister.Invoke(null, new object[] { w });
                        dict.Remove(hs[i]);
                        sCmlWinHandle.SetValue(w, 0u);
                        GC.SuppressFinalize(w);
                        c[2185]++;
                    }
                    catch (Exception) { c[2186]++; }
                }
            }
            public void DestroyOrig() { } // receives WindowBase.DestroyAllChildren()'s original header
        }
    }
}
