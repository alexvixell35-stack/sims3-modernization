using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;
using Sims3.UI;

namespace Ts3Fixes
{
    // NRaas Overwatch's Stuck Check V2 shows one notice per reset Sim ("StuckCheck:Unroutable"), from
    // StuckCheck.ResetTask.OnPerform after the reset (docs/145 §13). Switch "overwatchstucknotice" (tuning
    // kOverwatchStuckNoticeSummary, default off): the reset runs exactly as before; only that notice is held, and one summary
    // notice per 2 sim-hours names the Sims ("Overwatch reset N stuck Sims in the last 2 hours: A, B, C (+M more)").
    // How the notice is caught without touching NRaas's Notify: Overwatch's Common.Notify queues into the static list
    // Common.sNotices whenever it is not null (its load-up buffering). The OnPerform hook puts a list of ours there for the
    // length of the original call, then puts the old value back (only if nobody replaced ours meanwhile). Of what was
    // queued, the notice whose text equals Overwatch's own Localize("StuckCheck:" + mSuffix, ...) for that Sim is held;
    // everything else goes on as Overwatch would have sent it (into the old list, or shown through Common.Notify(StoredNotice)).
    // Other Overwatch notices (ResetCheck's "...One"/"...Two", cleanup reports) never pass through ResetTask and stay untouched.
    // Overwatch absent, or ResetTask / Common shaped differently: logged once, nothing patched.
    // Window: opens at the first held notice, summary at +2 sim-hours, nothing held = no notice; world quit clears it unshown.
    // Trigger: OverwatchStuckNoticeNow (repros.json owstucknotice-*). Counters: [290] notices held, [291] summaries shown,
    // [292] Sims named in summaries.
    public static partial class Fixes
    {
        static bool sOwNoticeTried, sOwNoticeReady;
        static FieldInfo sOwnSim, sOwnSuffix, sOwnNotices, sOwnText; // ResetTask.mSim / mSuffix, Common.sNotices, StoredNotice.mText
        static MethodInfo sOwnLocalize, sOwnNotify, sOwnPerform;     // Common.Localize(string, bool, object[]), Common.Notify(StoredNotice), ResetTask.Perform
        static readonly OverwatchNoticeCore sOwNotice = new OverwatchNoticeCore();

        internal static void ApplyOverwatchNotice(bool lastTry)
        {
            if (sOwNoticeTried) return;
            Assembly ow = null;
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                if (a.GetName().Name == "NRaasOverwatch") { ow = a; break; }
            if (ow == null)
            {
                if (lastTry) { sOwNoticeTried = true; AppliedLog().Add("overwatchstucknotice: Overwatch not loaded, off"); }
                return;
            }
            sOwNoticeTried = true;
            const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            const BindingFlags st = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            Type reset = ow.GetType("NRaas.OverwatchSpace.Alarms.StuckCheck+ResetTask"), common = ow.GetType("NRaas.Common"),
                stored = ow.GetType("NRaas.Common+StoredNotice");
            sOwnSim = reset == null ? null : reset.GetField("mSim", inst);
            sOwnSuffix = reset == null ? null : reset.GetField("mSuffix", inst);
            sOwnPerform = reset == null ? null : reset.GetMethod("Perform", st, null, new Type[] { typeof(Sim), typeof(Vector3), typeof(string) }, null);
            sOwnNotices = common == null ? null : common.GetField("sNotices", st);
            sOwnText = stored == null ? null : stored.GetField("mText");
            sOwnLocalize = common == null ? null : common.GetMethod("Localize", st, null, new Type[] { typeof(string), typeof(bool), typeof(object[]) }, null);
            sOwnNotify = common == null || stored == null ? null : common.GetMethod("Notify", st, null, new Type[] { stored }, null);
            if (sOwnSim == null || sOwnSim.FieldType != typeof(Sim) || sOwnSuffix == null || sOwnSuffix.FieldType != typeof(string)
                || sOwnNotices == null || stored == null || sOwnNotices.FieldType != typeof(List<>).MakeGenericType(stored)
                || sOwnText == null || sOwnText.FieldType != typeof(string) || sOwnLocalize == null || sOwnLocalize.ReturnType != typeof(string)
                || sOwnNotify == null)
            {
                AppliedLog().Add("overwatchstucknotice: StuckCheck.ResetTask or Common.sNotices differs from V123, off");
                return;
            }
            sOwNoticeReady = Instantiator.kHitchCauseHooks
                ? Patch(reset, "OnPerform", Type.EmptyTypes, typeof(HcOnPerformHook), "PerformHook", "PerformOrig")
                : Patch(reset, "OnPerform", Type.EmptyTypes, typeof(OwResetHook), "PerformHook", "PerformOrig");
            if (sOwNoticeReady) World.OnWorldQuitEventHandler += delegate { sOwNotice.Clear(); }; // flushed unshown
        }

        internal static void OverwatchNoticeWorldLoad() { sOwNotice.Clear(); }

        // ResetTask.OnPerform runs with the ResetTask as this; static helpers only (see OwAlarmHook).
        public class OwResetHook
        {
            public void PerformHook()
            {
                if (!ShortcutOn("overwatchstucknotice")) { PerformOrig(); return; }
                Sim sim = null;
                string suffix = null, name = null;
                ulong id = 0;
                IList prev = null, ours = null;
                try
                {
                    sim = (Sim)sOwnSim.GetValue(this);
                    suffix = (string)sOwnSuffix.GetValue(this);
                    if (sim != null && sim.SimDescription != null) { id = sim.SimDescription.SimDescriptionId; name = sim.FullName; } // before the reset
                    prev = (IList)sOwnNotices.GetValue(null);
                    ours = (IList)Activator.CreateInstance(sOwnNotices.FieldType);
                    sOwnNotices.SetValue(null, ours);
                }
                catch (Exception e) { OwNoticeFailed(e); ours = null; }
                try { PerformOrig(); }
                finally { if (ours != null) OwNoticeSort(ours, prev, sim, suffix, id, name); }
            }
            public void PerformOrig() { } // receives StuckCheck.ResetTask.OnPerform's original header
        }

        static bool sOwNoticeFailLogged;
        static void OwNoticeFailed(Exception e)
        {
            if (sOwNoticeFailLogged) return;
            sOwNoticeFailLogged = true;
            AppliedLog().Add("overwatchstucknotice: " + e.GetType().Name + " " + e.Message + " (notices left to Overwatch)");
        }

        // Puts Overwatch's notice list back and sorts what OnPerform queued: its stuck notice held, the rest sent on.
        static void OwNoticeSort(IList ours, IList prev, Sim sim, string suffix, ulong id, string name)
        {
            uint hcT0 = SrcNowUs(); // owresettime (HitchCause.cs)
            IList now = prev;
            try
            {
                object cur = sOwnNotices.GetValue(null);
                if (ReferenceEquals(cur, ours)) sOwnNotices.SetValue(null, prev);
                else now = (IList)cur; // Overwatch replaced the list meanwhile (world quit or load-up flush): keep theirs
            }
            catch (Exception e) { OwNoticeFailed(e); }
            if (ours.Count == 0) return;
            string expected = null;
            try
            {
                if (sim != null && suffix != null)
                    expected = (string)sOwnLocalize.Invoke(null, new object[] { "StuckCheck:" + suffix, sim.IsFemale, new object[] { sim } });
            }
            catch (Exception e) { OwNoticeFailed(e); }
            foreach (object notice in ours)
            {
                if (expected != null && expected == (string)sOwnText.GetValue(notice)) { OwNoticeHold(id, name); expected = null; continue; } // the one notice per reset
                try { if (now != null) now.Add(notice); else sOwnNotify.Invoke(null, new object[] { notice }); }
                catch (Exception e) { OwNoticeFailed(e); }
            }
            if (ShortcutOn("owresettime")) HcLine("owreset: notice sort " + name + ", " + (unchecked(SrcNowUs() - hcT0) / 1000) + " ms");
        }

        static void OwNoticeHold(ulong id, string name)
        {
            PatchCounters()[290]++;
            if (sOwNotice.Hold(id, name))
                AlarmManager.Global.AddAlarm(OverwatchNoticeCore.kWindowMinutes, TimeUnit.Minutes, OwNoticeFlush, "Ts3Fixes.OverwatchStuckNotice", AlarmType.NeverPersisted, null);
        }

        static void OwNoticeFlush()
        {
            int named; // NeverPersisted alarm: fires only in the world that opened the window (quit/load clear it)
            string text = sOwNotice.Take(out named);
            if (text == null) return;
            int[] c = PatchCounters();
            c[291]++;
            c[292] += named;
            StyledNotification.Show(new StyledNotification.Format(text, StyledNotification.NotificationStyle.kGameMessagePositive));
        }

        // Agent trigger (repros.json owstucknotice-*): Overwatch's own ResetTask.Perform(sim, Invalid, "Unroutable") for up to
        // n Sims that are not in the active household, as its Stuck Check does for an unroutable Sim (forced: these Sims are not
        // stuck, Overwatch resets them anyway). Returns the Sims started.
        public static string OverwatchStuckNoticeNow(int n)
        {
            if (!sOwNoticeReady || sOwnPerform == null) return "hook not in place (see applied log)";
            List<string> started = new List<string>();
            foreach (Sim s in new List<Sim>(LotManager.Actors))
            {
                if (started.Count >= n) break;
                if (s == null || !s.InWorld || s.IsSelectable || s.SimDescription == null) continue;
                sOwnPerform.Invoke(null, new object[] { s, Vector3.Invalid, "Unroutable" });
                started.Add(s.FullName);
            }
            return started.Count + " ResetTasks: " + string.Join(", ", started.ToArray());
        }
    }
}
