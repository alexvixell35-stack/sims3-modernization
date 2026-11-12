using System;
using System.Collections;
using System.Reflection;
using Sims3.SimIFace;
using Sims3.UI;
using Sims3.UI.CAS;

namespace Ts3Fixes
{
    // casrowleak (tuning kCasRowLeak, default off until measured; docs/191 "CAS clothing rows").
    //
    // EA bug: CASClothingCategory.AddGridItem (both branches: a CASPart, and a list of presets) loads a row layout with
    // UIManager.LoadLayout, wraps export 1 as a CASClothingRow (which registers it in UIManager.mCustomControlInstanceDict)
    // and calls CreateGridItems(true). When that returns no items (no preset of the part fits this Sim or the filter), the
    // method returns without adding the row to the grid and without destroying it [verified IL: `ArrayList.get_Count`,
    // `ldc.i4.0`, `ble` to the return at instruction 185, and 165 -> 171 in the preset branch]. The row is never attached,
    // so it never gets the detach that UIManager.OnCustomDetach needs, and it stays registered with its events for the
    // session. Measured 2026-11-11 (Wine, phase-20261111-1347): +45 CASClothingRow off the tree after one 5 min CAS phase.
    //
    // The pass: inside AddGridItem, a CASClothingRow whose CreateGridItems(bool) returned an empty list while the row had
    // no parent is remembered; when AddGridItem returns and that row still has no parent, it gets what OnCustomDetach does
    // for a detached custom control: removed from mCustomControlInstanceDict, UIManager.DeRegisterAllEvents, Dispose (EA's
    // window destroy). Its Layout is left to the finalizer, as for every catalogue cell (docs/193 L2). Rows the game added
    // to the grid, and rows outside AddGridItem, are not touched. UI only: no Sims, no save data.
    // Counters: [2220] empty unattached rows seen (also with the switch off), [2221] rows disposed, [2222] errors.
    public static partial class Fixes
    {
        static FieldInfo sCrlDict;
        static MethodInfo sCrlDeregister, sCrlBaseDispose;
        static FieldInfo sCrlGridWins;
        static PropertyInfo sCrlHandle;
        static int sCrlDepth;  // plain static: [ThreadStatic] crashes EA's Mono (no MonoThread on any thread, TS3W 0x00EBAB5B; docs/191)
        static WindowBase sCrlEmpty;
        static bool sCrlLogged;

        static void ApplyCasRowLeak()
        {
            const BindingFlags st = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            sCrlDict = typeof(UIManager).GetField("mCustomControlInstanceDict", st);
            sCrlDeregister = typeof(UIManager).GetMethod("DeRegisterAllEvents", st, null, new Type[] { typeof(WindowBase) }, null);
            sCrlGridWins = typeof(Sims3.UI.CAS.CASClothingRow).GetField("mGridWindows", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            // WindowBase.Dispose(bool), bound as a delegate (not MethodInfo.Invoke: tools/selftest/swapinvoke). Only when no
            // class between CASClothingRow and WindowBase overrides it, so the bound call is exactly EA's base step.
            MethodInfo bd = typeof(Sims3.UI.CAS.CASClothingRow).GetMethod("Dispose", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new Type[] { typeof(bool) }, null);
            sCrlBaseDispose = bd != null && bd.DeclaringType == typeof(WindowBase) ? bd : null;
            if (sCrlBaseDispose == null) AppliedLog().Add("casrowleak: WindowBase.Dispose(bool) not found or overridden, rows never shown are left as before");
            sCrlHandle = typeof(WindowBase).GetProperty("WinHandle", inst);
            if (sCrlDict == null || sCrlDeregister == null || sCrlHandle == null)
            {
                AppliedLog().Add("casrowleak: members not found (dict " + (sCrlDict != null) + ", deregister " + (sCrlDeregister != null)
                    + ", handle " + (sCrlHandle != null) + "), not patched");
                return;
            }
            // both or neither: the row hook only records, the AddGridItem hook acts
            if (Patch(typeof(CASClothingRow), "CreateGridItems", new Type[] { typeof(bool) }, typeof(CrlRowHook), "CreateHook", "CreateOrig"))
                Patch(typeof(CASClothingCategory), "AddGridItem", new Type[] { typeof(ItemGrid), typeof(object), typeof(ResourceKey), typeof(object) },
                    typeof(CrlCategoryHook), "AddHook", "AddOrig");
        }

        public class CrlRowHook
        {
            public ArrayList CreateHook(bool b)
            {
                ArrayList r = CreateOrig(b);
                if (sCrlDepth > 0 && (r == null || r.Count == 0))
                {
                    WindowBase row = (object)this as WindowBase;
                    if (row != null && row.Parent == null) sCrlEmpty = row;
                }
                return r;
            }
            public ArrayList CreateOrig(bool b) { return null; } // receives CASClothingRow.CreateGridItems(bool)'s original header
        }

        public class CrlCategoryHook
        {
            public bool AddHook(ItemGrid grid, object current, ResourceKey itemKey, object context)
            {
                sCrlEmpty = null;
                sCrlDepth++;
                bool r;
                try { r = AddOrig(grid, current, itemKey, context); }
                finally { sCrlDepth--; }
                WindowBase row = sCrlEmpty;
                sCrlEmpty = null;
                if (row != null && row.Parent == null)
                {
                    int[] c = PatchCounters();
                    c[2220]++;
                    if (ShortcutOn("casrowleak"))
                    {
                        try
                        {
                            IDictionary dict = sCrlDict.GetValue(null) as IDictionary;
                            uint h = Convert.ToUInt32(sCrlHandle.GetValue(row, null));
                            Array gw0 = sCrlGridWins != null ? sCrlGridWins.GetValue(row) as Array : null;
                            bool neverShown = gw0 != null && gw0.Length > 0 && gw0.GetValue(0) == null;
                            if (neverShown && sCrlBaseDispose == null) { }  // EA's override would throw: leave the row as EA does
                            else if (dict != null && h != 0 && dict.Contains(h) && dict[h] == (object)row)
                            {
                                dict.Remove(h);
                                string step = "deregister";
                                try
                                {
                                    sCrlDeregister.Invoke(null, new object[] { row });
                                    step = "dispose";
                                    // CASClothingRow.Dispose unhooks mGridWindows[i], which only Init fills; these rows were
                                    // never attached, so Init never ran and the override throws. For them, do WindowBase's own
                                    // Dispose step (Dispose(false): DestroyWindow, handle 0, back to the pool), which is
                                    // what the override ends with after its unhooking.
                                    Array gw = sCrlGridWins != null ? sCrlGridWins.GetValue(row) as Array : null;
                                    if (gw != null && gw.Length > 0 && gw.GetValue(0) == null && sCrlBaseDispose != null)
                                        ((Action<bool>)Delegate.CreateDelegate(typeof(Action<bool>), row, sCrlBaseDispose))(false);
                                    else row.Dispose();
                                    c[2221]++;
                                }
                                catch (Exception e)
                                {
                                    c[2222]++;
                                    if (!sCrlLogged)
                                    {
                                        sCrlLogged = true;
                                        Exception x = e is TargetInvocationException && e.InnerException != null ? e.InnerException : e;
                                        AppliedLog().Add("casrowleak: " + step + " threw " + x.GetType().Name + ": " + x.Message);
                                    }
                                }
                            }
                        }
                        catch (Exception e)
                        {
                            c[2222]++;
                            if (!sCrlLogged) { sCrlLogged = true; AppliedLog().Add("casrowleak: lookup threw " + e.GetType().Name + ": " + e.Message); }
                        }
                    }
                }
                return r;
            }
            public bool AddOrig(ItemGrid grid, object current, ResourceKey itemKey, object context) { return false; } // receives CASClothingCategory.AddGridItem's original header
        }
    }
}
