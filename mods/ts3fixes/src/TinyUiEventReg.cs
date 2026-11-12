using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Sims3.SimIFace;
using Sims3.UI;

namespace Ts3Fixes
{
    // tinyuieventreg (issue #40; errortrapsims' hourly alarm, own switch; not save data): Tiny UI Fix (Harry Gillanders,
    // 1.5.3) patches UI.dll with a static Sims3.UI.TinyUIFixForTS3UIEventRegistrationEvents.eventRegistrationEventRegistry
    // (window handle -> handlers). Its ScaledScrollbarMimic and ScaledSliderMimic register one handler per scrollbar or
    // slider they take over (AttachEventsToActualScrollbar/Slider) and deregister it only in DetachEventsFromActual*, which
    // runs when the mod scrapes itself off, never when the window goes: HandleDetachOfActual* only clears actualParent. So
    // every closed CAS panel, dialog or list leaves its entry, and the handler's mimic keeps the scrollbar or slider
    // wrapper, the mimic window and its 2 to 4 buttons (al368: Button +5138, Window +5631, the mimics +700 over 11 CAS rounds).
    // The pass removes the entries of windows the native UI no longer has attached (IUIManager.IsWindowAttached). EA's own
    // detach (UIManager.OnWinDetach -> DeRegisterAllEvents) has already removed such a window's events, and Tiny UI Fix makes
    // a new mimic when the window comes back (reactToRetrievedWindowInstanceAddedToCache), so nothing it uses is lost.
    // Without Tiny UI Fix the type is missing and the pass does nothing. Counters: [2094] entries removed, [2095] handlers
    // removed, [2096] entries left after the last pass.
    // shortcut: a handle the UI gives to a new window keeps the old handlers next to the new ones (attached, so kept); compare
    // each handler's mimic with the window's current wrapper if a run shows such lists growing.
    public static partial class Fixes
    {
        static IDictionary sTuiReg;
        static IUIManager sTuiMgr;
        static bool sTuiTried, sTuiOk, sTuiThrew;

        static void TinyUiEventRegPass()
        {
            try
            {
                if (!ShortcutOn("tinyuieventreg")) return;
                if (!sTuiTried)
                {
                    sTuiTried = true;
                    const BindingFlags kS = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;
                    Type t = typeof(UIManager).Assembly.GetType("Sims3.UI.TinyUIFixForTS3UIEventRegistrationEvents");
                    FieldInfo reg = t == null ? null : t.GetField("eventRegistrationEventRegistry", kS);
                    FieldInfo mgr = typeof(UIManager).GetField("gUIMgr", kS);
                    sTuiReg = reg == null ? null : reg.GetValue(null) as IDictionary;
                    sTuiMgr = mgr == null ? null : mgr.GetValue(null) as IUIManager;
                    AppliedLog().Add("tinyuieventreg: " + (sTuiReg == null ? "no Tiny UI Fix registry (pass off)" : sTuiMgr == null ? "no UIManager.gUIMgr (pass off)" : "ok"));
                }
                if (sTuiReg == null || sTuiMgr == null) return;
                List<object> gone = new List<object>();
                int handlers = 0;
                foreach (DictionaryEntry e in sTuiReg)
                {
                    if (sTuiMgr.IsWindowAttached((uint)e.Key)) continue;
                    gone.Add(e.Key);
                    ICollection l = e.Value as ICollection;
                    handlers += l != null ? l.Count : 0;
                }
                foreach (object k in gone) sTuiReg.Remove(k);
                int[] c = PatchCounters();
                c[2094] += gone.Count;
                c[2095] += handlers;
                c[2096] = sTuiReg.Count;
                if (!sTuiOk && gone.Count > 0) { sTuiOk = true; AppliedLog().Add("tinyuieventreg: first pass removed " + gone.Count + " entries (" + handlers + " handlers), " + sTuiReg.Count + " left"); }
            }
            catch (Exception e)
            {
                if (!sTuiThrew) { sTuiThrew = true; AppliedLog().Add("tinyuieventreg: pass threw " + e.Message); }
            }
        }
    }
}
