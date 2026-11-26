using System;
using System.Collections.Generic;
using System.Text;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.DreamsAndPromises;

namespace Ts3Fixes
{
    // dnpcostlog (docs/690 §3, diagnostics only, switch off by default): times the wish managers' Updates that
    // EventTracker.SendEvent drains (sNeedToUpdateList, every manager queued since the last event, back to back) and the
    // ActiveDreamNode.Test calls inside them, by check function. Updates closer than 5 ms together are one burst; a burst
    // of 50 ms or more is one "dnpcost:" line (managers, full / partial updates, tests and their ms, the top check
    // functions, the slowest manager's Sim, the managed callers of the first Update). Off: one slot read per Update and
    // per Test. Reads game state only (names, counts); the originals run exactly as before.
    // Counters: [4900] bursts logged, [4901] Updates timed, [4902] Tests timed, [4903] errors.
    public static partial class Fixes
    {
        static readonly int kSwDnpCost = SwSlot("dnpcostlog");
        static uint sDcEnd, sDcT0, sDcUs, sDcTestUs, sDcMaxUs;
        static int sDcMgrs, sDcFull, sDcTests, sDcNew, sDcLines = 300;
        static string sDcMaxSim, sDcCallers;
        static Dictionary<string, long> sDcFnUs;
        static Dictionary<string, int> sDcFnN;
        static int sDcDepth;  // Update nesting (an Update that sends an event drains nothing: sCurrentlyUpdating...)

        internal static bool DcProbeOn() { return SwOn(kSwDnpCost); }

        static System.Reflection.FieldInfo sDcFullFi;
        static bool sDcFullFiRead;
        internal static bool DcFullField(object mgr)
        {
            try
            {
                if (!sDcFullFiRead) { sDcFullFiRead = true; sDcFullFi = typeof(DreamsAndPromisesManager).GetField("mFullUpdate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic); }
                return sDcFullFi != null && (bool)sDcFullFi.GetValue(mgr);
            }
            catch (Exception) { PatchCounters()[4903]++; return false; }
        }

        // WlMgr.UpdateHook (WishListenerFixes.cs) calls these around UpdateOrig when the switch is on
        internal static uint DcUpdateBegin(object mgr)
        {
            uint now = SrcNowUs();
            try
            {
                if (sDcDepth++ > 0) return now;
                if (sDcMgrs == 0 || unchecked(now - sDcEnd) > 5000u)
                {
                    DcFlush();
                    sDcT0 = now; sDcCallers = DcCallers(4, 6);
                }
            }
            catch (Exception) { PatchCounters()[4903]++; }
            return now;
        }

        internal static void DcUpdateEnd(object mgr, uint t0, bool full)
        {
            uint now = SrcNowUs();
            try
            {
                if (--sDcDepth > 0) return;
                uint us = unchecked(now - t0);
                sDcEnd = now; sDcMgrs++; sDcUs += us;
                if (full) sDcFull++;
                PatchCounters()[4901]++;
                if (us >= sDcMaxUs)
                {
                    sDcMaxUs = us;
                    DreamsAndPromisesManager m = mgr as DreamsAndPromisesManager;
                    Sim a = m == null ? null : m.Actor;
                    sDcMaxSim = a == null || a.SimDescription == null ? "?" : a.SimDescription.FullName + (a.IsSelectable ? " (selectable" : " (not selectable") + ", nodes " + (m.ActiveNodes == null ? -1 : m.ActiveNodes.Count) + ")";
                }
            }
            catch (Exception) { PatchCounters()[4903]++; }
        }

        // DreamsAndPromisesManager.CreateAndIntitForSim (ListenerFixes.cs LfDnp.InitHook): SetupForNewSim, every tree activated
        internal static void DcNewSim(uint t0)
        {
            try { sDcNew++; sDcUs += unchecked(SrcNowUs() - t0); sDcEnd = SrcNowUs(); if (sDcMgrs == 0) { sDcT0 = t0; sDcCallers = DcCallers(4, 6); } sDcMgrs++; }
            catch (Exception) { PatchCounters()[4903]++; }
        }

        // WlNode.TestHook (WishListenerFixes.cs) around TestOrig when the switch is on
        internal static void DcTest(ActiveDreamNode n, uint t0)
        {
            uint us = unchecked(SrcNowUs() - t0);
            try
            {
                sDcTests++; sDcTestUs += us;
                PatchCounters()[4902]++;
                if (sDcFnUs == null) { sDcFnUs = new Dictionary<string, long>(); sDcFnN = new Dictionary<string, int>(); }
                string fn = "?";
                DreamNodeInstance ni = n == null ? null : n.NodeInstance;
                if (ni != null && ni.CheckFunction != null) fn = ni.CheckFunction.Method.Name;
                long v; sDcFnUs.TryGetValue(fn, out v); sDcFnUs[fn] = v + us;
                int k; sDcFnN.TryGetValue(fn, out k); sDcFnN[fn] = k + 1;
            }
            catch (Exception) { PatchCounters()[4903]++; }
        }

        static void DcFlush()
        {
            if (sDcMgrs > 0 && unchecked(sDcEnd - sDcT0) >= 50000u && sDcLines > 0)
            {
                sDcLines--;
                PatchCounters()[4900]++;
                StringBuilder b = new StringBuilder();
                b.Append("dnpcost: burst ").Append(unchecked(sDcEnd - sDcT0) / 1000u).Append(" ms (in Update ").Append(sDcUs / 1000u)
                 .Append(" ms), managers ").Append(sDcMgrs).Append(" (full ").Append(sDcFull).Append(", new ").Append(sDcNew)
                 .Append("), tests ").Append(sDcTests).Append(" (").Append(sDcTestUs / 1000u).Append(" ms), slowest ")
                 .Append(sDcMaxUs / 1000u).Append(" ms ").Append(sDcMaxSim ?? "-").Append("; top checks:");
                if (sDcFnUs != null)
                {
                    List<KeyValuePair<string, long>> l = new List<KeyValuePair<string, long>>(sDcFnUs);
                    l.Sort(delegate (KeyValuePair<string, long> x, KeyValuePair<string, long> y) { return y.Value.CompareTo(x.Value); });
                    for (int i = 0; i < l.Count && i < 5; i++) b.Append(' ').Append(l[i].Key).Append(' ').Append(l[i].Value / 1000).Append(" ms/").Append(sDcFnN[l[i].Key]);
                }
                b.Append("; at ").Append(Sims3.Gameplay.Utilities.SimClock.CurrentTime().ToString()).Append(", by ").Append(sDcCallers ?? "?");
                DiagLine(b.ToString());
            }
            sDcMgrs = sDcFull = sDcTests = sDcNew = 0; sDcUs = sDcTestUs = sDcMaxUs = 0; sDcMaxSim = null;
            if (sDcFnUs != null) { sDcFnUs.Clear(); sDcFnN.Clear(); }
        }

        static string DcCallers(int skip, int depth)
        {
            try
            {
                System.Diagnostics.StackTrace st = new System.Diagnostics.StackTrace(skip, false);
                StringBuilder b = new StringBuilder();
                for (int i = 0; i < st.FrameCount && i < depth; i++)
                {
                    System.Reflection.MethodBase m = st.GetFrame(i).GetMethod();
                    if (m == null) continue;
                    if (b.Length > 0) b.Append(" < ");
                    b.Append(m.DeclaringType != null ? m.DeclaringType.Name : "?").Append('.').Append(m.Name);
                }
                return b.ToString();
            }
            catch (Exception e) { return "stack unreadable: " + e.GetType().Name; }
        }
    }
}
