using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay;
using Sims3.SimIFace;
using Sims3.UI;

namespace Ts3Fixes
{
    // customctlsweep (issue #40; errortrapsims' hourly alarm, own switch; not save data): UIManager.WrapperFromHandle puts
    // every custom control's script wrapper in the static UIManager.mCustomControlInstanceDict (window handle -> wrapper),
    // and only UIManager.OnCustomDetach takes it out: when the window gets the detach message while DisposeOnDetach is set.
    // A window destroyed inside a parent that was taken off first gets no detach message (docs/193 §3.1: the message goes out
    // only while the parent has a window manager), so its wrapper stays for the session. CAS shows it: each clothing row
    // (Sims3.UI.CAS.CASClothingRow) of a closed CAS stays in the dictionary, and through its mRowController the whole
    // CASClothingCategory with mPartPresetsList, one CASPartPreset per preset with its ~6,400-character preset XML.
    // al370 (CAS every 2 min, 18 rounds): 1,971 such strings (21.4 MiB) still live 10 min after the last CAS round; the
    // heap path of all 40 samples was CASPartPreset <- mPartPresetsList <- CASClothingRow.mRowController <- this dictionary.
    // The pass removes an entry whose window is not attached and either is gone from the native UI (GetClassID 0) or is a
    // CAS control (namespace Sims3.UI.CAS) while the game is in live mode, where no CAS screen exists. For each it also does
    // what OnCustomDetach does first, UIManager.DeRegisterAllEvents (only when the event registry still has the handle), so
    // the event registry stops holding the wrapper's handlers. It does not call Dispose: the native window may be gone.
    // Counters: [2097] entries removed, [2098] not-attached entries kept (native window alive, not CAS), [2099] entries
    // left after the last pass. The first three passes that remove something log the removed types and how many of them
    // the native UI no longer has. al371's scan also found the rows in UIManager.mEventRegistry (WindowEventData.Window),
    // so their native windows got no shutdown callback either.
    // shortcut: a handle the UI gives to a new window while the old wrapper is still listed keeps the old wrapper (attached,
    // so kept), as in EA's code; compare the wrapper's type with the handle's custom control id if a run shows that.
    public static partial class Fixes
    {
        static Dictionary<uint, WindowBase> sCcsDict;
        static IDictionary sCcsEvents;  // Dictionary<uint, WindowEventData>; the value type is not needed
        static MethodInfo sCcsDeReg;
        static IUIManager sCcsMgr;
        static int sCcsLogs;
        static bool sCcsTried, sCcsThrew;

        static void CustomCtlSweepPass()
        {
            try
            {
                if (!ShortcutOn("customctlsweep")) return;
                if (!sCcsTried)
                {
                    sCcsTried = true;
                    const BindingFlags kS = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;
                    FieldInfo d = typeof(UIManager).GetField("mCustomControlInstanceDict", kS);
                    FieldInfo ev = typeof(UIManager).GetField("mEventRegistry", kS);
                    FieldInfo mgr = typeof(UIManager).GetField("gUIMgr", kS);
                    sCcsDeReg = typeof(UIManager).GetMethod("DeRegisterAllEvents", kS, null, new Type[] { typeof(WindowBase) }, null);
                    sCcsDict = d == null ? null : d.GetValue(null) as Dictionary<uint, WindowBase>;
                    sCcsEvents = ev == null ? null : ev.GetValue(null) as IDictionary;
                    sCcsMgr = mgr == null ? null : mgr.GetValue(null) as IUIManager;
                    bool ok = sCcsDict != null && sCcsEvents != null && sCcsMgr != null && sCcsDeReg != null;
                    AppliedLog().Add("customctlsweep: " + (ok ? "ok" : "UIManager fields missing (pass off)"));
                    if (!ok) sCcsDict = null;
                }
                if (sCcsDict == null) return;
                bool live = GameStates.IsLiveState;
                List<uint> gone = new List<uint>();
                Dictionary<string, int> types = new Dictionary<string, int>();
                int kept = 0, dead = 0;
                foreach (KeyValuePair<uint, WindowBase> e in sCcsDict)
                {
                    if (sCcsMgr.IsWindowAttached(e.Key)) continue;
                    string ns = e.Value == null ? null : e.Value.GetType().Namespace;
                    bool cas = live && ns != null && ns.StartsWith("Sims3.UI.CAS");
                    bool gone0 = sCcsMgr.GetClassID(e.Key) == 0;
                    if (!cas && !gone0) { kept++; continue; }
                    if (gone0) dead++;
                    gone.Add(e.Key);
                    string tn = e.Value == null ? "(null)" : e.Value.GetType().Name;
                    int n;
                    types.TryGetValue(tn, out n);
                    types[tn] = n + 1;
                }
                foreach (uint h in gone)
                {
                    WindowBase w = sCcsDict[h];
                    sCcsDict.Remove(h);
                    if (w != null && sCcsEvents.Contains(h)) sCcsDeReg.Invoke(null, new object[] { w });
                }
                int[] c = PatchCounters();
                c[2097] += gone.Count;
                c[2098] = kept;
                c[2099] = sCcsDict.Count;
                if (gone.Count > 0 && sCcsLogs < 3)
                {
                    sCcsLogs++;
                    List<string> parts = new List<string>();
                    foreach (KeyValuePair<string, int> t in types) parts.Add(t.Key + " " + t.Value);
                    parts.Sort(delegate (string a, string b) { return string.CompareOrdinal(a, b); });
                    AppliedLog().Add("customctlsweep: removed " + gone.Count + " (" + dead + " gone from the native UI; " + string.Join(", ", parts.ToArray()) + "), kept not attached " + kept + ", left " + sCcsDict.Count + (live ? "" : " (not live mode)"));
                }
            }
            catch (Exception e)
            {
                if (!sCcsThrew) { sCcsThrew = true; AppliedLog().Add("customctlsweep: pass threw " + e.Message); }
            }
        }
    }
}
