using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Sims3.UI;
using Sims3.UI.CAS;

namespace Ts3Fixes
{
    // caspuckevents (tuning kCasPuckEvents, default off until measured; docs/257 "HUD/CAS").
    //
    // EA bug [verified IL, UI.dll]: CASPuck.Init subscribes OnDashboardStateUpdated to the static event
    // DownloadDashboard.DashboardStateUpdate and OnOptionsChanged to Responder.OptionsModel.OptionsChanged (Init IL
    // 543-553); CASPuck.Dispose removes neither. The options model is created once per game, the dashboard event is
    // static, so every CAS visit leaves a dead CASPuck (and what it references) reachable for the session, and an
    // options change or dashboard update runs the handler on it. PuckController.Init / Dispose add and remove the same
    // two events, which is EA's intent. The pass: after CASPuck.Dispose, both handlers of that puck are removed.
    // Counters: [2178] Dispose calls that left the dashboard handler attached (also off), [2179] handler pairs removed,
    // [2180] errors.
    //
    // famcursorleak (tuning kFamCursorLeak, default off until measured).
    // EA bug [verified IL]: CASFamilyScreen.Init loads layout "CAFThumbItem" and keeps two windows from it as drag
    // cursors (mDragCursorThumb, mDragInvalidCursorThumb), sets their DisposeOnDetach false and registers them as window
    // cursors; they are never attached, and nothing disposes them: CASFamilyScreen.Unload shuts the screen's layout down
    // without touching them (CASFamilyScreen.Dispose only deregisters the two cursor ids). EA's own pattern for drag
    // cursors (SceneMgrWindow.UnregisterDragCursors) deregisters the ids and disposes both windows. The pass: just before
    // CASFamilyScreen.Unload, for each of the two windows not disposed yet: the cursor ids are deregistered (as Dispose
    // does), its custom-control entry is removed with UIManager.DeRegisterAllEvents when it has one, and it is disposed.
    // v2: the screen's other custom controls (one CAFThumb per Sim in the tree, created by CreateCAFThumb under
    // mFamilyTopLevelWin) are noted before Unload; after it, each one EA left in the dict whose window is gone or is alive
    // with no parent gets OnCustomDetach's cleanup (DeRegisterAllEvents, remove, then WindowBase.Dispose(false) for a live
    // window, or a zeroed handle and suppressed finalizer for a dead one). Measured off: +21 CAFThumb entries per open.
    // Counters: [2187] cursor windows found at Unload (also off), [2188] disposed, [2189] errors.
    public static partial class Fixes
    {
        static MethodInfo sCplDashRemove, sCplDashHandler, sCplOptHandler;
        static Type sCplDashType;
        static FieldInfo sCplDashField, sFclSingleton, sFclThumb, sFclInvalid, sFclCursorId, sFclInvalidId;

        static void ApplyCasPuckLeak()
        {
            const BindingFlags st = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            Type dd = typeof(DownloadDashboard);
            sCplDashRemove = dd.GetMethod("remove_DashboardStateUpdate", st);
            sCplDashField = dd.GetField("DashboardStateUpdate", st);
            sCplDashType = sCplDashRemove != null ? sCplDashRemove.GetParameters()[0].ParameterType : null;
            sCplDashHandler = typeof(CASPuck).GetMethod("OnDashboardStateUpdated", inst, null, Type.EmptyTypes, null);
            sCplOptHandler = typeof(CASPuck).GetMethod("OnOptionsChanged", inst, null, Type.EmptyTypes, null);
            if (sCplDashRemove != null && sCplDashType != null && sCplDashHandler != null && sCplOptHandler != null)
                Patch(typeof(CASPuck), "Dispose", Type.EmptyTypes, typeof(CplHook), "DisposeHook", "DisposeOrig");
            else AppliedLog().Add("caspuckevents: members not found, not patched");

            Type fs = typeof(CASFamilyScreen);
            sFclSingleton = fs.GetField("gSingleton", st);
            sFclThumb = fs.GetField("mDragCursorThumb", inst);
            sFclInvalid = fs.GetField("mDragInvalidCursorThumb", inst);
            sFclCursorId = fs.GetField("kDragCursorID", st);
            sFclInvalidId = fs.GetField("kInvalidDragCursorID", st);
            if (sFclSingleton != null && sFclThumb != null && sFclInvalid != null && sFclCursorId != null && sFclInvalidId != null && sBilDict != null)
                Patch(fs, "Unload", Type.EmptyTypes, typeof(CplHook), "FamUnloadHook", "FamUnloadOrig");
            else AppliedLog().Add("famcursorleak: members not found, not patched");
        }

        static bool CplDashAttached(object puck)
        {
            Delegate d = sCplDashField != null ? sCplDashField.GetValue(null) as Delegate : null;
            if (d == null) return false;
            foreach (Delegate x in d.GetInvocationList()) if (object.ReferenceEquals(x.Target, puck)) return true;
            return false;
        }

        public class CplHook
        {
            public void DisposeHook()
            {
                DisposeOrig();
                int[] c = PatchCounters();
                try
                {
                    if (!CplDashAttached(this)) return;
                    c[2178]++;
                    if (!ShortcutOn("caspuckevents")) return;
                    sCplDashRemove.Invoke(null, new object[] { Delegate.CreateDelegate(sCplDashType, this, sCplDashHandler) });
                    IOptionsModel om = Responder.Instance != null ? Responder.Instance.OptionsModel : null;
                    if (om != null) om.OptionsChanged -= (OptionsChangedHandler)Delegate.CreateDelegate(typeof(OptionsChangedHandler), this, sCplOptHandler);
                    c[2179]++;
                }
                catch (Exception) { c[2180]++; }
            }
            public void DisposeOrig() { } // receives CASPuck.Dispose()'s original header

            public static void FamUnloadHook()
            {
                int[] c = PatchCounters();
                // v2: also the screen's other custom controls (the CAFThumb per Sim under mFamilyTopLevelWin): noted before
                // Unload, and after it each one EA left in the dict gets OnCustomDetach's cleanup (measured: ~15 CAFThumb
                // entries per family-tree open stay after the layout shutdown)
                List<WindowBase> tree = null; List<uint> ths = null, tids = null; IDictionary tdict = null;
                try
                {
                    WindowBase scr0 = sFclSingleton.GetValue(null) as WindowBase;
                    tdict = sBilDict.GetValue(null) as IDictionary;
                    if (scr0 != null && tdict != null)
                    {
                        tree = new List<WindowBase>();
                        DclCollect(scr0, tdict, tree, 0);
                        ths = new List<uint>(); tids = new List<uint>();
                        foreach (WindowBase w in tree) { uint h = Convert.ToUInt32(sBilHandle.GetValue(w, null)); ths.Add(h); tids.Add(CmlCustomId(h)); }
                    }
                }
                catch (Exception) { tree = null; c[2189]++; }
                CriWatchSweep();
                FamCursorPass(c);
                FamUnloadOrig();
                if (tree == null) return;
                bool on2 = ShortcutOn("famcursorleak");
                for (int i = 0; i < tree.Count; i++)
                {
                    try
                    {
                        WindowBase w = tree[i];
                        if (w.Disposed || !tdict.Contains(ths[i]) || !object.ReferenceEquals(tdict[ths[i]], w)) continue;
                        bool live = tids[i] != 0 && CmlCustomId(ths[i]) == tids[i];
                        if (live && w.Parent != null) { CriWatchAdd(w, ths[i], tids[i], 1); continue; }  // still attached: checked again later (v3)
                        c[2187]++;
                        if (!on2) continue;
                        sBilDeregister.Invoke(null, new object[] { w });
                        tdict.Remove(ths[i]);
                        if (live && sCmlBaseDispose != null) ((Action<bool>)Delegate.CreateDelegate(typeof(Action<bool>), w, sCmlBaseDispose))(false);
                        else { sCmlWinHandle.SetValue(w, 0u); GC.SuppressFinalize(w); }
                        c[2188]++;
                    }
                    catch (Exception) { c[2189]++; }
                }
            }

            static void FamCursorPass(int[] c)
            {
                try
                {
                    object scr = sFclSingleton.GetValue(null);
                    if (scr != null)
                    {
                        bool on = ShortcutOn("famcursorleak"), ids = false;
                        foreach (FieldInfo f in new FieldInfo[] { sFclThumb, sFclInvalid })
                        {
                            WindowBase w = f.GetValue(scr) as WindowBase;
                            if (w == null || w.Disposed) continue;
                            c[2187]++;
                            if (!on) continue;
                            try
                            {
                                if (!ids)
                                {
                                    ids = true;
                                    UIManager.DeRegisterWindowCursor(Convert.ToUInt32(sFclCursorId.GetValue(null)));
                                    UIManager.DeRegisterWindowCursor(Convert.ToUInt32(sFclInvalidId.GetValue(null)));
                                }
                                IDictionary dict = sBilDict.GetValue(null) as IDictionary;
                                object key = Convert.ToUInt32(sBilHandle.GetValue(w, null));
                                if (dict != null && dict.Contains(key) && object.ReferenceEquals(dict[key], w))
                                {
                                    sBilDeregister.Invoke(null, new object[] { w });
                                    dict.Remove(key);
                                }
                                w.Dispose();
                                c[2188]++;
                            }
                            catch (Exception) { c[2189]++; }
                        }
                    }
                }
                catch (Exception) { c[2189]++; }
            }
            public static void FamUnloadOrig() { } // receives CASFamilyScreen.Unload()'s original header
        }
    }
}
