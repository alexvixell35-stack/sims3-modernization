using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Sims3.UI;
using Sims3.UI.CAS;

namespace Ts3Fixes
{
    // caspuckevents (tuning kCasPuckEvents, default off until measured; docs/545 "HUD/CAS").
    //
    // EA bug [verified IL, UI.dll]: CASPuck.Init subscribes OnDashboardStateUpdated to the static event
    // DownloadDashboard.DashboardStateUpdate and OnOptionsChanged to Responder.OptionsModel.OptionsChanged (Init IL
    // 543-553); CASPuck.Dispose removes neither. The options model is created once per game, the dashboard event is
    // static, so every CAS visit leaves a dead CASPuck (and what it references) reachable for the session, and an
    // options change or dashboard update runs the handler on it. PuckController.Init / Dispose add and remove the same
    // two events, which is EA's intent. The pass: after CASPuck.Dispose, both handlers of that puck are removed.
    // Counters: [2284] Dispose calls that left the dashboard handler attached (also off), [2285] handler pairs removed,
    // [2286] errors.
    //
    // famcursorleak (tuning kFamCursorLeak, default off until measured).
    // EA bug [verified IL]: CASFamilyScreen.Init loads layout "CAFThumbItem" and keeps two windows from it as drag
    // cursors (mDragCursorThumb, mDragInvalidCursorThumb), sets their DisposeOnDetach false and registers them as window
    // cursors; they are never attached, and nothing disposes them: CASFamilyScreen.Unload shuts the screen's layout down
    // without touching them (CASFamilyScreen.Dispose only deregisters the two cursor ids). EA's own pattern for drag
    // cursors (SceneMgrWindow.UnregisterDragCursors) deregisters the ids and disposes both windows. The pass: just before
    // CASFamilyScreen.Unload, for each of the two windows not disposed yet: the cursor ids are deregistered (as Dispose
    // does), its custom-control entry is removed with UIManager.DeRegisterAllEvents when it has one, and it is disposed.
    // Counters: [2293] cursor windows found at Unload (also off), [2294] disposed, [2295] errors.
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
                    c[2284]++;
                    if (!ShortcutOn("caspuckevents")) return;
                    sCplDashRemove.Invoke(null, new object[] { Delegate.CreateDelegate(sCplDashType, this, sCplDashHandler) });
                    IOptionsModel om = Responder.Instance != null ? Responder.Instance.OptionsModel : null;
                    if (om != null) om.OptionsChanged -= (OptionsChangedHandler)Delegate.CreateDelegate(typeof(OptionsChangedHandler), this, sCplOptHandler);
                    c[2285]++;
                }
                catch (Exception) { c[2286]++; }
            }
            public void DisposeOrig() { } // receives CASPuck.Dispose()'s original header

            public static void FamUnloadHook()
            {
                int[] c = PatchCounters();
                FamCursorPass(c);
                FamUnloadOrig();
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
                            c[2293]++;
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
                                c[2294]++;
                            }
                            catch (Exception) { c[2295]++; }
                        }
                    }
                }
                catch (Exception) { c[2295]++; }
            }
            public static void FamUnloadOrig() { } // receives CASFamilyScreen.Unload()'s original header
        }
    }
}
