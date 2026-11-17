using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Sims3.UI;

namespace Ts3Fixes
{
    // sellcursorleak (tuning kSellCursorLeak, default off until measured; docs/191 "inventory hunt" B).
    //
    // EA bug: BBSellPanelController.RegisterDragCursors (run each time Buy, Build or Blueprint mode opens) loads the
    // "HudInventoryCursors" layout and wraps its two InventoryItemWin windows (export ids 108125440/108125441) in locals
    // only, registering their cursor children as drag cursors [verified IL]. UnregisterDragCursors, from Dispose, only
    // deregisters the two cursor ids: the windows are never destroyed, so two live InventoryItemWin wrappers stay in
    // UIManager.mCustomControlInstanceDict per mode entry (measured: +12 after 6 Buy entries, 2026-11-11).
    // SceneMgrWindow does the same job right: UnregisterDragCursors deregisters the cursors, then disposes both windows.
    //
    // The pass (switch on, UI only): while RegisterDragCursors runs, the dict entries it adds are noted; after EA's
    // Dispose has deregistered the cursors, those windows are disposed (WindowBase.Dispose, as SceneMgrWindow does) and
    // their dict entries removed. Counters: [2242] sell panels closed with windows noted (also with the switch off),
    // [2243] windows disposed, [2244] errors.
    public static partial class Fixes
    {
        static List<WindowBase> sSclWins = new List<WindowBase>();

        static void ApplySellCursorLeak()
        {
            if (sBilDict == null) { AppliedLog().Add("sellcursorleak: needs buyiconleak's reflection, not patched"); return; }
            Type t = typeof(UIManager).Assembly.GetType("Sims3.UI.BBSellPanelController");
            if (t == null) { AppliedLog().Add("sellcursorleak: BBSellPanelController not found, not patched"); return; }
            Patch(t, "RegisterDragCursors", Type.EmptyTypes, typeof(SclHook), "RegisterHook", "RegisterOrig");
            Patch(t, "Dispose", Type.EmptyTypes, typeof(SclHook), "DisposeHook", "DisposeOrig");
        }

        public class SclHook
        {
            public void RegisterHook()
            {
                IDictionary dict = null;
                Dictionary<object, bool> before = null;
                try
                {
                    dict = sBilDict.GetValue(null) as IDictionary;
                    if (dict != null) { before = new Dictionary<object, bool>(); foreach (object k in dict.Keys) before[k] = true; }
                }
                catch (Exception) { PatchCounters()[2244]++; }
                RegisterOrig();
                try
                {
                    if (dict != null && before != null)
                        foreach (DictionaryEntry e in dict)
                        {
                            if (before.ContainsKey(e.Key)) continue;
                            WindowBase w = e.Value as WindowBase;
                            if (w != null && w.GetType().Name == "InventoryItemWin") sSclWins.Add(w);
                        }
                }
                catch (Exception) { PatchCounters()[2244]++; }
            }
            public void RegisterOrig() { }

            public void DisposeHook()
            {
                DisposeOrig();
                if (sSclWins.Count == 0) return;
                int[] c = PatchCounters();
                c[2242]++;
                List<WindowBase> wins = new List<WindowBase>(sSclWins);
                sSclWins.Clear();
                if (!ShortcutOn("sellcursorleak")) return;
                try
                {
                    IDictionary dict = sBilDict.GetValue(null) as IDictionary;
                    foreach (WindowBase w in wins)
                    {
                        try
                        {
                            if ((bool)sBilDisposed.GetValue(w, null)) continue;
                            object key = sBilHandle.GetValue(w, null);
                            w.Dispose();
                            if (dict != null)
                            {
                                if (dict.Contains(key) && object.ReferenceEquals(dict[key], w)) dict.Remove(key);
                                else
                                {
                                    object found = null;
                                    foreach (DictionaryEntry e in dict) if (object.ReferenceEquals(e.Value, w)) { found = e.Key; break; }
                                    if (found != null) dict.Remove(found);
                                }
                            }
                            c[2243]++;
                        }
                        catch (Exception) { c[2244]++; }
                    }
                }
                catch (Exception) { c[2244]++; }
            }
            public void DisposeOrig() { }
        }
    }
}
