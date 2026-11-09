using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.CAS;
using Sims3.UI;
using Sims3.UI.CAS;

namespace Ts3Fixes
{
    // casuihandlers (issue #40; errortrapsims' hourly alarm, own switch; not save data): Create-a-Sim screens subscribe to
    // events that outlive them and never unsubscribe. CASPuck.Init adds the puck to the static
    // DownloadDashboard.DashboardStateUpdate, to OptionsModel.OptionsChanged (Responder keeps one OptionsModel all session)
    // and to CASLogic's OnSimSaved and OnDynamicUpdateCurrentSimThumbnail; CASPuck.Dispose removes none of them (it does
    // remove InGameBrowser.IGSClosed, the store callback and its other CASLogic events). CAF.Init (OnHouseholdSaved),
    // CASHair.Init (OnSimOutfitCategoryChanged) and CABPartSelection.Init (four part and undo events) do the same. Each of
    // these windows is made anew per visit (Load/Unload, static gSingleton), so every closed one stays alive with its window
    // wrappers and what they hold. CASController is one object for the session, and LoadFullCASMode adds its ClosingDown and
    // ShowUI handlers on each full CAS visit with no remove, so while CASLogic lives they stack and run once per visit made.
    // The pass removes the handlers of closed windows (not their class's current gSingleton; null after Unload), as Dispose
    // would have, and collapses CASController's duplicates (same target and method) to one. No change in play: a closed
    // window's controls are gone, the open window keeps its handlers, and CASController keeps one of each.
    // Counters: [2076] closed windows' handlers removed, [2077] CASController duplicates removed.
    public static partial class Fixes
    {
        static readonly string[] kCasLogicEvents = { "OnSimSaved", "OnDynamicUpdateCurrentSimThumbnail", "OnHouseholdSaved", "OnSimOutfitCategoryChanged", "OnCASPartAdded", "OnCASPartRemoved", "RedoSelected", "UndoSelected", "ClosingDown", "ShowUI" };
        static readonly string[] kCasWindows = { "Sims3.UI.CAS.CASPuck", "Sims3.UI.CAS.CAF", "Sims3.UI.CAS.CASHair", "Sims3.UI.CAS.CAB.CABPartSelection" };
        static FieldInfo sCasDashEv, sCasOptEv, sCasLogicOne;
        static List<FieldInfo> sCasLogicEv;
        static Type[] sCasWinTypes;
        static FieldInfo[] sCasWinOne;
        static object[] sCasWinOpen;
        static bool sCasUiOk, sCasUiThrew;

        static void CasUiHandlersPass()
        {
            try
            {
                if (!ShortcutOn("casuihandlers")) return;
                const BindingFlags kS = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;
                const BindingFlags kI = BindingFlags.NonPublic | BindingFlags.Instance;
                if (sCasWinTypes == null)
                {
                    sCasDashEv = typeof(DownloadDashboard).GetField("DashboardStateUpdate", kS);
                    sCasLogicOne = typeof(CASLogic).GetField("sSingleton", kS);
                    sCasLogicEv = new List<FieldInfo>();
                    foreach (string n in kCasLogicEvents) { FieldInfo f = typeof(CASLogic).GetField(n, kI); if (f != null) sCasLogicEv.Add(f); }
                    Type[] t = new Type[kCasWindows.Length];
                    FieldInfo[] one = new FieldInfo[kCasWindows.Length];
                    for (int i = 0; i < t.Length; i++)
                    {
                        t[i] = typeof(CASPuck).Assembly.GetType(kCasWindows[i]);
                        one[i] = t[i] == null ? null : t[i].GetField("gSingleton", kS);
                        if (one[i] == null) throw new MissingFieldException(kCasWindows[i] + ".gSingleton");
                    }
                    if (sCasDashEv == null || sCasLogicOne == null) throw new MissingFieldException("DownloadDashboard.DashboardStateUpdate or CASLogic.sSingleton");
                    sCasWinOne = one; sCasWinOpen = new object[t.Length]; sCasWinTypes = t;
                }
                for (int i = 0; i < sCasWinTypes.Length; i++) sCasWinOpen[i] = sCasWinOne[i].GetValue(null);
                int[] c = PatchCounters();
                CasUiDrop(sCasDashEv, null, c);
                object opt = Responder.Instance == null ? null : Responder.Instance.OptionsModel;
                if (opt != null)
                {
                    if (sCasOptEv == null) sCasOptEv = opt.GetType().GetField("OptionsChanged", kI);
                    if (sCasOptEv != null) CasUiDrop(sCasOptEv, opt, c);
                }
                object logic = sCasLogicOne.GetValue(null);
                if (logic != null) foreach (FieldInfo f in sCasLogicEv) CasUiDrop(f, logic, c);
                if (!sCasUiOk) { sCasUiOk = true; AppliedLog().Add("casuihandlers: ok (" + sCasLogicEv.Count + " of " + kCasLogicEvents.Length + " CASLogic events" + (sCasOptEv == null ? ", no OptionsModel.OptionsChanged" : "") + ")"); }
            }
            catch (Exception e)
            {
                if (!sCasUiThrew) { sCasUiThrew = true; AppliedLog().Add("casuihandlers: pass threw " + e.Message); }
            }
        }

        // The delegate in field f (of owner; null for a static field) without closed CAS windows' handlers and with one of
        // each CASController handler. Delegate equality is same target and method.
        static void CasUiDrop(FieldInfo f, object owner, int[] c)
        {
            Delegate ev = f.GetValue(owner) as Delegate;
            if (ev == null) return;
            List<Delegate> keep = new List<Delegate>();
            int closed = 0, dup = 0;
            foreach (Delegate d in ev.GetInvocationList())
            {
                object t = d.Target;
                bool gone = false;
                if (t != null)
                    for (int i = 0; i < sCasWinTypes.Length && !gone; i++)
                        gone = sCasWinTypes[i].IsInstanceOfType(t) && !ReferenceEquals(t, sCasWinOpen[i]);
                if (gone) closed++;
                else if (t is CASController && keep.Contains(d)) dup++;
                else keep.Add(d);
            }
            if (closed + dup == 0) return;
            f.SetValue(owner, keep.Count == 0 ? null : Delegate.Combine(keep.ToArray()));
            c[2076] += closed;
            c[2077] += dup;
        }
    }
}
