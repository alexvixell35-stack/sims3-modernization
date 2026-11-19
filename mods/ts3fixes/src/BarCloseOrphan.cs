using System;
using System.Collections;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Skills;
using Sims3.Gameplay.Utilities;

namespace Ts3Fixes
{
    // barcloseorphan (tuning kBarCloseOrphan, default off; docs/661 §3.1). EA's Bartending.BartendingVenueHelper (one per
    // bar venue type: bars, lounges, nightclubs) runs OnClose at HourClose. When a bar of that type hosts a started
    // BachelorParty (IsClosingTimeExtended), OnClose re-arms itself every kPartyEndCheckInterval (30) sim-minutes until
    // kTimeOfPartyEnd, and each run ends with `mRemindVenueGuestsToLeave = AddAlarmRepeating(... RemindVenueGuestsToLeave,
    // every kExitVenueAlarmRepeatTime (5) min ...)` without removing the previous handle [verified IL OnClose IL_00d1-IL_00fa;
    // OnOpen and RemindVenueGuestsToLeave remove only the field's handle]. Every earlier repeating alarm is orphaned until
    // the world unloads (NeverPersisted): every 5 minutes, at any hour, it calls MakeSimLeaveVenueIfNecessary on every actor
    // in every lot of that bar type, so guests are pushed out to the yard (VisitSituation.MakeSimExitToYard) during open
    // hours: bars of that type look empty or keep emptying the day after a bachelor party.
    // Fix: EA's own legit pushes only run from the staggered exit (HourClose - kStaggeredExitTimeDelta) until OnOpen
    // removes the after-close alarm. MakeSimLeaveVenueIfNecessary is skipped (answers false, as for a Sim EA leaves alone)
    // for a non-exempt NPC while the venue is strictly open (LotGameplayCore.StrictlyOpen: after HourOpen, before the
    // staggered exit). Household, selected and unknown-scope Sims (IsPlayerSim) always run EA's body unchanged.
    // Counters [4230] NPC pushes skipped in open hours, [4231] exempt Sims let through in open hours (vanilla), [4232]
    // errors. Log "barcloseorphan: ..." capped at 10 a minute.
    public static partial class Fixes
    {
        static int sBcoMinute = -1, sBcoLogged;
        static float sBcoStagger = -1f;
        static bool sBcoProbe;                       // BarCloseOrphanCheck only: count EA's answers for NPCs
        static int sBcoProbePushed, sBcoProbeSkipped;

        static void ApplyBarCloseOrphan()
        {
            Patch(typeof(Bartending.BartendingVenueHelper), "MakeSimLeaveVenueIfNecessary", new Type[] { typeof(Sim), typeof(Lot) }, typeof(BarCloseOrphanHook), "LeaveHook", "LeaveOrig");
        }

        static void BcoLog(string s)
        {
            int minute = System.Environment.TickCount / 60000;
            if (minute != sBcoMinute) { sBcoMinute = minute; sBcoLogged = 0; }
            if (sBcoLogged++ < 10) DiagLine("barcloseorphan: " + s);
        }

        // Bartending's private tunable kStaggeredExitTimeDelta (EA default 2 h), read once
        static float BcoStagger()
        {
            if (sBcoStagger >= 0f) return sBcoStagger;
            sBcoStagger = 2f;
            try
            {
                FieldInfo f = typeof(Bartending).GetField("kStaggeredExitTimeDelta", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                if (f != null) sBcoStagger = (float)f.GetValue(null);
            }
            catch (Exception) { }
            return sBcoStagger;
        }

        // true = skip EA's push for this Sim (counted)
        static bool BcoSkip(Sim sim, Lot lot)
        {
            int[] c = PatchCounters();
            try
            {
                if (sim == null || lot == null) return false;
                float open = -1f, close = -1f;
                if (!Bartending.TryGetHoursOfOperation(lot, ref open, ref close)) return false;
                if (!LotGameplayCore.StrictlyOpen(SimClock.HoursPassedOfDay, open, close, BcoStagger())) return false;
                if (IsPlayerSim(sim)) { c[4231]++; return false; }
                c[4230]++;
                BcoLog(sim.FullName + " on " + lot.Name + " at " + SimClock.HoursPassedOfDay.ToString("0.0") + " h (open " + open + "-" + close
                    + "): leave push from an orphaned after-close alarm skipped");
                return true;
            }
            catch (Exception ex) { c[4232]++; BcoLog("error " + ex.GetType().Name); return false; }
        }

        public class BarCloseOrphanHook
        {
            public static bool LeaveHook(Sim sim, Lot lot)
            {
                if (ShortcutOn("barcloseorphan") && BcoSkip(sim, lot)) { if (sBcoProbe) sBcoProbeSkipped++; return false; }
                bool r = LeaveOrig(sim, lot);
                if (sBcoProbe && r && !IsPlayerSim(sim)) sBcoProbePushed++;
                return r;
            }
            public static bool LeaveOrig(Sim sim, Lot lot) { return false; } // receives BartendingVenueHelper.MakeSimLeaveVenueIfNecessary's original body
        }

        // Forced live check (repro barcloseorphan-check, agent reflect_call); returns a text with a "verdict:" line.
        // run: on a bar venue that is strictly open now with an NPC guest inside, call EA's private
        // BartendingVenueHelper.RemindVenueGuestsToLeave once, exactly what an alarm orphaned by OnClose does every 5 minutes.
        // Off: EA pushes the NPC guests to the yard (BROKEN). On: their pushes are skipped (FIXED). Household Sims on that
        // lot keep EA's push (vanilla), so the check picks a lot without one when it can.
        public static object BarCloseOrphanCheck(string what)
        {
            int[] c = PatchCounters();
            string counters = "4230 skipped " + c[4230] + ", 4231 exempt " + c[4231] + ", 4232 errors " + c[4232];
            if (what == "result") return counters;
            if (what != "run") return "barcloseorphan <run|result>";
            try
            {
                IList helpers = typeof(Bartending).GetField("sHelpers", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null) as IList;
                FieldInfo typeField = typeof(Bartending.BartendingVenueHelper).GetField("mLotType", BindingFlags.Instance | BindingFlags.NonPublic);
                MethodInfo remind = typeof(Bartending.BartendingVenueHelper).GetMethod("RemindVenueGuestsToLeave", BindingFlags.Instance | BindingFlags.NonPublic);
                if (helpers == null || typeField == null || remind == null) return "verdict: N/A (EA's BartendingVenueHelper members not found)\n" + counters;
                Lot best = null; int bestNpc = 0; bool bestHh = true;
                foreach (Lot lot in LotManager.AllLots)
                {
                    float open = -1f, close = -1f;
                    if (lot == null || !Bartending.TryGetHoursOfOperation(lot, ref open, ref close)) continue;
                    if (!LotGameplayCore.StrictlyOpen(SimClock.HoursPassedOfDay, open, close, BcoStagger())) continue;
                    int npc = 0; bool hh = false;
                    foreach (Sim s in lot.GetAllActors()) { if (IsPlayerSim(s)) hh = true; else npc++; }
                    if (npc == 0) continue;
                    if (best == null || (bestHh && !hh) || (hh == bestHh && npc > bestNpc)) { best = lot; bestNpc = npc; bestHh = hh; }
                }
                if (best == null) return "verdict: N/A (no bar venue strictly open now with an NPC inside; hour " + SimClock.HoursPassedOfDay.ToString("0.0") + ")\n" + counters;
                object helper = null;
                foreach (object h in helpers) if (h != null && (Lot.MetaAutonomyType)typeField.GetValue(h) == best.GetMetaAutonomyType) { helper = h; break; }
                if (helper == null) return "verdict: N/A (no helper for " + best.GetMetaAutonomyType + ")\n" + counters;
                sBcoProbe = true; sBcoProbePushed = 0; sBcoProbeSkipped = 0;
                try { remind.Invoke(helper, null); }
                finally { sBcoProbe = false; }
                bool on = ShortcutOn("barcloseorphan");
                string head = best.Name + " (" + best.GetMetaAutonomyType + "), " + bestNpc + " NPC inside" + (bestHh ? ", household Sim there too (EA's push kept for it)" : "")
                    + ": NPC pushes " + sBcoProbePushed + ", skipped " + sBcoProbeSkipped;
                string verdict = sBcoProbePushed > 0 ? "BROKEN (NPC guests pushed out while the venue is open)" : sBcoProbeSkipped > 0 ? "FIXED" : "N/A (no NPC needed a push: all outside or in a bar role)";
                return head + "\nswitch " + (on ? "on" : "off") + "\nverdict: " + verdict + "\n" + counters;
            }
            catch (Exception ex) { sBcoProbe = false; return "verdict: N/A (error " + ex.GetType().Name + ": " + ex.Message + ")\n" + counters; }
        }
    }
}
