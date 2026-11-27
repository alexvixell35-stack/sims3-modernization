using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Utilities;

namespace Ts3Fixes
{
    // simresetalarms (tuning kSimResetAlarms, off by default; mode in kSimResetAlarmsMode) and its probe
    // simresetalarmprobe (tuning kSimResetAlarmProbe, off, log only). Design docs/352, build docs/357.
    //
    // EA bug [verified, decompiled]: a Sim's alarms always sit on the global AlarmManager, but GameObject.DoReset asks
    // only the manager of the lot the Sim stands on (AlarmManager.OnReset, one caller). On a normal lot that manager holds
    // none of the Sim's alarms, so all survive. On the world lot (street) it is the global manager, which clears every
    // DeleteOnReset and NeverPersisted alarm of the Sim, among them repeating NeverPersisted alarms EA arms only at load
    // or when their system is created (vampire sunset / sunrise): they stay gone until the save is loaded again.
    // Mode 1 (keep): when the global manager is asked for a Sim, its repeating NeverPersisted timers are kept, exactly
    //   the timers vanilla keeps for the same Sim on any normal lot. EA's own loop does the work: the kept alarms'
    //   handles are taken out of the manager's owner index for the length of that one call (EA's loop walks that index)
    //   and put back after it. The timers themselves are never changed (type, time, callback), so nothing a save could
    //   write differs at any point.
    // Mode 2 (keep + drop list): when a lot's own manager is asked, the Sim's DeleteOnReset timers whose callback is on
    //   the reviewed list sSraDropList are removed from the global manager (EA's RemoveAlarm). The list ships empty.
    //   NPCs only: household, selected and unknown-scope Sims keep what vanilla keeps (IsPlayerSim).
    // Household: mode 1 applies to every Sim, household and selected included. It is an EA bug fix whose result is one
    //   vanilla already produces on the other lot kind, not a throttle, cap or shortcut (CLAUDE.md "EA bug fixes stay").
    // The probe changes nothing: per Sim reset one head line and one line per reset-scoped timer (kept or dropped).
    // Counters: [1828] Sim resets that asked the global manager (world lot), [1829] that asked a lot's manager,
    // [1830] timers kept by mode 1, [1831] alarms removed by the drop list, [1832] serviceleave re-arms skipped (under serviceleave itself) because
    // the timer was still live (ServiceLeave.cs, docs/357 §4), [1833] errors. Check: SimResetAlarmsCheck.
    public static partial class Fixes
    {
        static FieldInfo sSraIndex, sSraTimers, sSraCallBack;
        static PropertyInfo sSraType, sSraRepeating;
        // Mode 2: "<declaring type full name>.<method>" of DeleteOnReset callbacks that must die on any lot. Reviewed
        // entries only (probe data plus a read of the site, docs/352 §5); empty until then.
        static readonly string[] sSraDropList = new string[0];
        const int kSraLineBudget = 600;  // probe lines per session
        static int sSraLines;
        static string sSraLast = "none";

        static void ApplySimResetAlarms()
        {
            Type timer = typeof(AlarmManager).GetNestedType("Timer", BindingFlags.NonPublic);
            sSraIndex = typeof(AlarmManager).GetField("mGameObjectIndex", kInstAll);
            sSraTimers = typeof(AlarmManager).GetField("mTimers", kInstAll);
            if (timer != null)
            {
                sSraCallBack = timer.GetField("CallBack");
                sSraType = timer.GetProperty("MyAlarmType");
                sSraRepeating = timer.GetProperty("Repeating");
            }
            if (sSraIndex == null || sSraTimers == null || sSraCallBack == null || sSraType == null || sSraRepeating == null)
            {
                AppliedLog().Add("simresetalarms: AlarmManager members not found, not patched");
                return;
            }
            Patch(typeof(AlarmManager), "OnReset", typeof(SraHook), "ResetHook", "ResetOrig");
        }

        // 0 off, 1 keep, 2 keep + drop list
        static int SraMode() { return !ShortcutOn("simresetalarms") ? 0 : Instantiator.kSimResetAlarmsMode >= 2 ? 2 : 1; }

        // ---- decisions (also used by the check) ----
        // One timer of a reset Sim. type: 0 AlwaysPersisted, 1 DeleteOnReset, 2 NeverPersisted. globalAsked: the reset
        // asked the global manager (Sim on the world lot). 0 = EA's way, 1 = keep (EA would drop), 2 = drop (EA would keep).
        // npc: not a household, selected or unknown-scope Sim. The drop takes an alarm vanilla keeps, so it is NPC only.
        internal static int SraDecide(int mode, bool globalAsked, int type, bool repeating, bool listed, bool npc)
        {
            if (mode >= 1 && globalAsked && type == 2 && repeating) return 1;
            if (mode >= 2 && !globalAsked && type == 1 && listed && npc) return 2;
            return 0;
        }

        static string SraKey(Delegate cb)
        {
            return (cb.Method.DeclaringType != null ? cb.Method.DeclaringType.FullName + "." : "") + cb.Method.Name;
        }

        // The owner's live timers on the global manager as { handle, timer } pairs, in the order EA's OnReset walks them.
        static List<object[]> SraTimers(object owner)
        {
            List<object[]> l = new List<object[]>();
            AlarmManager g = AlarmManager.Global;
            if (g == null || owner == null) return l;
            IDictionary idx = (IDictionary)sSraIndex.GetValue(g), tm = (IDictionary)sSraTimers.GetValue(g);
            if (!idx.Contains(owner)) return l;
            foreach (object h in new ArrayList((ICollection)idx[owner]))
            {
                if (!tm.Contains(h)) continue;
                foreach (object t in (IEnumerable)tm[h]) if (sSraCallBack.GetValue(t) != null) l.Add(new object[] { h, t });
            }
            return l;
        }

        // Is this handle still known to the global manager? (also asked by serviceleave before a re-arm, ServiceLeave.cs)
        static bool SraLive(AlarmHandle h)
        {
            try
            {
                AlarmManager g = AlarmManager.Global;
                return g != null && sSraTimers != null && ((IDictionary)sSraTimers.GetValue(g)).Contains(h);
            }
            catch (Exception) { PatchCounters()[1833]++; return false; }
        }

        // reset-scoped timers / repeating NeverPersisted timers / vampire day alarms of the Sim, read from the manager
        static int[] SraCount(Sim sim)
        {
            int[] n = new int[3];
            foreach (object[] ht in SraTimers(sim))
            {
                int type = (int)(AlarmType)sSraType.GetValue(ht[1], null);
                if (type == 0) continue;
                n[0]++;
                if (type == 2 && (bool)sSraRepeating.GetValue(ht[1], null)) n[1]++;
                if (SraKey((Delegate)sSraCallBack.GetValue(ht[1])).IndexOf("OccultVampire", StringComparison.Ordinal) >= 0) n[2]++;
            }
            return n;
        }

        static void SraLine(string s)
        {
            if (sSraLines >= kSraLineBudget) return;
            sSraLines++;
            DiagLine(sSraLines == kSraLineBudget ? "simresetalarm: line budget reached, probe silent from here" : s);
        }

        public class SraHook
        {
            public void ResetHook(GameObject o)
            {
                Sim sim = o as Sim;
                bool probe = sim != null && ShortcutOn("simresetalarmprobe");
                int mode = sim == null || !ShortcutOn("simresetalarms") ? 0 : SraMode();
                if (!probe && mode == 0) { ResetOrig(o); return; }  // fast path: not a Sim, or both switches off
                int[] c = PatchCounters();
                bool global = false;
                List<AlarmHandle> keep = new List<AlarmHandle>(), drop = new List<AlarmHandle>(), stay = new List<AlarmHandle>();
                int kept = 0;
                List<string> lines = probe ? new List<string>() : null;
                int[] before = null;
                try
                {
                    global = object.ReferenceEquals(this, AlarmManager.Global);
                    c[global ? 1828 : 1829]++;
                    before = SraCount(sim);
                    bool npc = !IsPlayerSim(sim);  // unknown scope counts as household
                    foreach (object[] ht in SraTimers(sim))
                    {
                        int type = (int)(AlarmType)sSraType.GetValue(ht[1], null);
                        if (type == 0) continue;
                        bool rep = (bool)sSraRepeating.GetValue(ht[1], null);
                        string key = SraKey((Delegate)sSraCallBack.GetValue(ht[1]));
                        int d = SraDecide(mode, global, type, rep, Array.IndexOf(sSraDropList, key) >= 0, npc);
                        AlarmHandle h = (AlarmHandle)ht[0];
                        if (d == 1) { kept++; if (!keep.Contains(h)) keep.Add(h); }
                        else if (d == 2) drop.Add(h);
                        else stay.Add(h);
                        if (lines != null && lines.Count < 48)
                            lines.Add("simresetalarm:   " + key + " type=" + (type == 1 ? "DeleteOnReset" : "NeverPersisted") + " rep=" + (rep ? 1 : 0)
                                + " -> " + (d == 1 ? "kept (fix)" : d == 2 ? "dropped (list)" : global ? "dropped" : "kept"));
                    }
                }
                catch (Exception) { c[1833]++; keep.Clear(); drop.Clear(); }
                // One Add call makes every timer under a handle with one type and one repeat flag; a handle that still
                // mixes decisions goes EA's way.
                keep.RemoveAll(delegate (AlarmHandle h) { return stay.Contains(h) || drop.Contains(h); });
                IDictionary idx = null;
                try
                {
                    if (keep.Count > 0)
                    {
                        idx = (IDictionary)sSraIndex.GetValue(AlarmManager.Global);
                        List<AlarmHandle> rest = new List<AlarmHandle>();
                        foreach (AlarmHandle h in (IEnumerable)idx[sim]) if (!keep.Contains(h)) rest.Add(h);
                        if (rest.Count > 0) idx[sim] = rest; else idx.Remove(sim);
                    }
                }
                catch (Exception) { c[1833]++; }
                try { ResetOrig(o); }
                finally
                {
                    try
                    {
                        if (idx != null && sim.HasBeenDestroyed)
                        {
                            // the reset destroyed the Sim: dispose could not see the handles we held back, drop them now
                            foreach (AlarmHandle h in keep) AlarmManager.Global.RemoveAlarm(h);
                        }
                        else if (idx != null)
                        {
                            // back into the index EA left (a handle removed meanwhile stays out)
                            IDictionary tm = (IDictionary)sSraTimers.GetValue(AlarmManager.Global);
                            List<AlarmHandle> now = idx.Contains(sim) ? (List<AlarmHandle>)idx[sim] : new List<AlarmHandle>();
                            foreach (AlarmHandle h in keep) if (tm.Contains(h) && !now.Contains(h)) now.Add(h);
                            if (now.Count > 0) idx[sim] = now;
                            c[1830] += kept;
                        }
                    }
                    catch (Exception) { c[1833]++; }
                }
                try
                {
                    // a handle is removed only when every timer under it is on the list
                    foreach (AlarmHandle h in drop)
                    {
                        if (stay.Contains(h) || !SraLive(h)) continue;
                        AlarmManager.Global.RemoveAlarm(h);
                        c[1831]++;
                    }
                    if (before == null) return;
                    int[] after = SraCount(sim);
                    string head = "sim=\"" + sim.FullName + "\" lot=" + (global ? "world" : "other") + " hh=" + (IsPlayerSim(sim) ? 1 : 0) + " mode=" + mode
                        + " scoped=" + before[0] + ">" + after[0] + " npr=" + before[1] + ">" + after[1] + " vampire=" + before[2] + ">" + after[2];
                    sSraLast = head;
                    if (lines == null) return;
                    SraLine("simresetalarm: reset " + head);
                    foreach (string l in lines) SraLine(l);
                }
                catch (Exception) { c[1833]++; }
            }
            public void ResetOrig(GameObject o) { } // receives AlarmManager.OnReset's original body
        }
    }
}
