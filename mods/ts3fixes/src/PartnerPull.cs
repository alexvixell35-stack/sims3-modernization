using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Careers;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Objects.Beds;
using Sims3.Gameplay.Socializing;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // partnerpull (docs/686 §21, default off). A Sim stands and waits for a partner whose linked half sits at the end of
    // the partner's queue behind whatever the partner is doing.
    //
    // [verified, decompiled] Two such waits:
    //   Bed_ReadToSleep (parent): ReadToSleep queues the child's Bed_BeReadToSleep at the END of the child's queue with
    //     the parent's priority (InteractionQueue.Add), then the parent's Bed_ReadToSleep.Run loops (Sleep(0)) until the
    //     child's head is that half and the child is moving into the bed. A child busy with a long autonomous action
    //     (play, homework, TV) keeps the parent standing until it ends.
    //   SocialInteractionA (starter): PushSocialB adds the SocialInteractionB half at the end of the partner's queue
    //     (it only cancels other queued linked interactions of lower priority); the starter then waits in
    //     InteractionInstance.StartSync (up to kNumMinToWaitOnPreSync, 15 sim-min) and
    //     WaitForSynchronizationLevelWithSim (30 sim-min) for the partner to start it.
    // Fix: a repeating 0.5 sim-minute alarm looks at every Sim whose current interaction is one of those two with the
    // linked half still queued on the partner. After kGraceMinutes (1 sim-minute, about 1 s at speed 1) the half is
    // moved behind the partner's current interaction with EA's own InteractionQueue.PrioritizeInteraction (the call the
    // game makes when the player prioritises an action): the current interaction gets InterruptionRequested and ends
    // through its own exit (food put away, book closed); EA's own guard keeps a timed interaction with 10 sim-min or less
    // left. ShouldReenque is cleared on it, so it is cancelled rather than queued again behind the half.
    // Never: a head queued by the player, a queue where the player queued anything ahead of the half, a head the player
    // could not cancel or that must run, a partner in a posture transition, a head linked to the waiter, a head that
    // outranks the half.
    //
    // Scope: one switch for every Sim, household included (user-approved 2026-11-24). Off = compare mode: counted.
    // partnerpulllog writes one line per pull and per kept case. The waits are measured either way (first pass that
    // sees one to the first pass that does not), in tenths of a sim-minute, for the off/on A/B.
    // Counters 4582-4589. Check: PartnerPullCheck. No swap (alarm only).
    public static partial class Fixes
    {
        const int kPpSeen = 4582, kPpWould = 4583, kPpPullRts = 4584, kPpPullSoc = 4585, kPpKeptPlayer = 4586, kPpKeptBusy = 4587,
            kPpErrors = 4588, kPpRefused = 4589;
        // wait totals (not counter slots: kept here and printed by the check)
        static readonly int[] sPpWaits = new int[2], sPpWaitTenths = new int[2], sPpWaitMax = new int[2];

        sealed class PpWait { public Sim Waiter; public InteractionInstance Link; public int Kind; public long First; public bool Seen, Decided; }

        static readonly Type sPpRtsType = typeof(Bed).GetNestedType("Bed_ReadToSleep", BindingFlags.NonPublic);
        static readonly Dictionary<ulong, PpWait> sPpWait = new Dictionary<ulong, PpWait>();  // linked half's Id -> wait
        static readonly List<ulong> sPpGone = new List<ulong>();
        static readonly LineCapCore sPpCap = new LineCapCore(20, 60000, 300);
        static AlarmHandle sPpAlarm = AlarmHandle.kInvalidHandle;
        static string sPpLast = "", sPpLastError = "";

        static void ApplyPartnerPull() { AppliedLog().Add(sPpRtsType != null ? "partnerpull: alarm, started at world load" : "partnerpull: not found: Bed.Bed_ReadToSleep"); }

        // every world load (from UfTpCleanupWorldLoad, sim thread)
        static void PartnerPullWorldLoad()
        {
            sPpWait.Clear();
            if (AlarmManager.Global == null) return;
            if (sPpAlarm != AlarmHandle.kInvalidHandle) AlarmManager.Global.RemoveAlarm(sPpAlarm);
            sPpAlarm = AlarmManager.Global.AddAlarmRepeating(0.5f, TimeUnit.Minutes, delegate { try { PpPass(); } catch (ResetException) { throw; } catch (Exception e) { PpError(e); } },
                0.5f, TimeUnit.Minutes, "Ts3Fixes.PartnerPull", AlarmType.NeverPersisted, null);
        }

        static void PpError(Exception e)
        {
            int n = PatchCounters()[kPpErrors]++;
            sPpLastError = "partnerpull: error: " + e.GetType().Name + ": " + e.Message;
            if (n < 3) DiagLine(sPpLastError);
        }

        static void PpLine(string text)
        {
            sPpLast = text;
            if (ShortcutOn("partnerpulllog") && sPpCap.Take(System.Environment.TickCount)) DiagLine("partnerpull: " + text);
        }

        static void PpPass()
        {
            if (sPpRtsType == null || !GameStates.IsLiveState) return;
            long now = SimClock.CurrentTicks;
            foreach (PpWait w in sPpWait.Values) w.Seen = false;
            foreach (Sim s in LotManager.Actors)
            {
                if (s == null || s.HasBeenDestroyed || s.InteractionQueue == null) continue;
                InteractionInstance ii = s.InteractionQueue.GetCurrentInteraction();
                if (ii == null) continue;
                int kind;
                if (ii.GetType() == sPpRtsType) kind = PartnerPullCore.kReadToSleep;
                else if (ii is SocialInteractionA) kind = PartnerPullCore.kSocial;
                else continue;
                Sim partner = ii.Target as Sim;
                InteractionInstance link = ii.LinkedInteractionInstance;
                if (partner == null || link == null || partner == s || partner.HasBeenDestroyed || partner.InteractionQueue == null || s.HasExitReason(ExitReason.Default)) continue;
                InteractionQueue q = partner.InteractionQueue;
                InteractionInstance head = q.GetHeadInteraction();
                if (head == null || head == link || q.IsRunning(link, true) || !q.HasInteraction(link)) continue;  // not waiting on the queue
                PpWait w;
                if (!sPpWait.TryGetValue(link.Id, out w))
                {
                    if (sPpWait.Count >= 256) continue;
                    w = new PpWait { Waiter = s, Link = link, Kind = kind, First = now };
                    sPpWait[link.Id] = w;
                }
                w.Seen = true;
                if (!w.Decided) PpDecide(s, partner, q, head, link, w, now);
            }
            // waits that ended since the last pass: measured
            sPpGone.Clear();
            foreach (KeyValuePair<ulong, PpWait> kv in sPpWait) if (!kv.Value.Seen) sPpGone.Add(kv.Key);
            foreach (ulong id in sPpGone)
            {
                PpWait w = sPpWait[id];
                sPpWait.Remove(id);
                int tenths = (int)(SimClock.ConvertFromTicks(now - w.First, TimeUnit.Minutes) * 10f);
                sPpWaits[w.Kind]++; sPpWaitTenths[w.Kind] += tenths;
                if (tenths > sPpWaitMax[w.Kind]) sPpWaitMax[w.Kind] = tenths;
            }
        }

        static void PpDecide(Sim s, Sim partner, InteractionQueue q, InteractionInstance head, InteractionInstance link, PpWait w, long now)
        {
            float waited = SimClock.ConvertFromTicks(now - w.First, TimeUnit.Minutes);
            bool aheadAuto = true;
            foreach (InteractionInstance x in q.InteractionList)
            {
                if (x == link) break;
                if (x != head && !x.Autonomous) { aheadAuto = false; break; }
            }
            Sim headLinkSim = head.LinkedInteractionInstance != null ? head.LinkedInteractionInstance.InstanceActor : null;
            bool busy = !head.CancellableByPlayer || head.MustRun || q.TransitionInteraction != null || headLinkSim == s;
            bool trip = head.Target is Lot || head is ITakeSimToWorkLocation || PpTripName(head.GetType().Name);
            int step = PartnerPullCore.Step(waited, head.Autonomous, aheadAuto, busy, trip, head is SocialInteraction, link.GetPriority().CompareTo(head.GetPriority()));
            if (step == PartnerPullCore.kWaitGrace) return;
            w.Decided = true;
            int[] c = PatchCounters();
            c[kPpSeen]++;
            string what = (w.Kind == PartnerPullCore.kReadToSleep ? "read to sleep" : "social " + PpName(w.Waiter.CurrentInteraction)) + ": " + s.FullName + " waits on " + partner.FullName
                + " (" + PpName(head) + ")";
            if (step != PartnerPullCore.kPull)
            {
                c[step == PartnerPullCore.kKeepPlayer ? kPpKeptPlayer : kPpKeptBusy]++;
                PpLine("kept, " + (step == PartnerPullCore.kKeepPlayer ? "player's action" : step == PartnerPullCore.kKeepRank ? "head outranks" : step == PartnerPullCore.kKeepTrip ? "partner on a lot trip"
                    : step == PartnerPullCore.kKeepSocial ? "partner in a conversation" : "head busy") + ": " + what);
                return;
            }
            if (!PartnerPullCore.Act(ShortcutOn("partnerpull"))) { c[kPpWould]++; PpLine("would pull: " + what); return; }
            if (!q.PrioritizeInteraction(link)) { c[kPpRefused]++; PpLine("refused by the game (about to finish): " + what); return; }
            head.ShouldReenque = false;  // cancelled, not queued again behind the half
            c[w.Kind == PartnerPullCore.kReadToSleep ? kPpPullRts : kPpPullSoc]++;
            PpLine("pulled after " + waited.ToString("0.0") + " sim-min: " + what);
        }

        // lot trips and arrivals named by type (the ones not targeted at a Lot)
        static bool PpTripName(string n) { return n.IndexOf("GoToLot") >= 0 || n.IndexOf("Visit") >= 0 || n.IndexOf("Arrive") >= 0 || n.IndexOf("Travel") >= 0 || n.IndexOf("GoHome") >= 0; }

        static string PpName(InteractionInstance ii)
        {
            if (ii == null) return "none";
            try { string n = ii.GetInteractionName(); if (!string.IsNullOrEmpty(n)) return n; } catch (Exception) { }
            return ii.GetType().Name;
        }

        // ---- check ----

        static string PpCounters()
        {
            int[] c = PatchCounters();
            return "partnerpull: waits past the grace " + c[kPpSeen] + "; pulled: read to sleep " + c[kPpPullRts] + ", socials " + c[kPpPullSoc]
                + "; off: would " + c[kPpWould] + "; kept: player's action or queue " + c[kPpKeptPlayer] + ", busy (lot trip, conversation, must run, transition) or outranking head " + c[kPpKeptBusy]
                + ", refused by the game " + c[kPpRefused] + "; errors " + c[kPpErrors] + (sPpLastError.Length > 0 ? " (last: " + sPpLastError + ")" : "")
                + "; waits measured: read to sleep " + sPpWaits[0] + " (" + (sPpWaitTenths[0] / 10f).ToString("0.0") + " sim-min, max " + (sPpWaitMax[0] / 10f).ToString("0.0")
                + "), socials " + sPpWaits[1] + " (" + (sPpWaitTenths[1] / 10f).ToString("0.0") + " sim-min, max " + (sPpWaitMax[1] / 10f).ToString("0.0") + "), open " + sPpWait.Count
                + "; " + GlMembers(sPpRtsType == null ? "Bed.Bed_ReadToSleep" : "") + (sPpLast.Length > 0 ? "; last: " + sPpLast : "");
        }

        // PartnerPullCheck run: the rules with controls. Bug case: a parent stands by a child's bed while the child plays
        // (autonomous) with the story queued behind it. result: counters, waits and the last line.
        public static object PartnerPullCheck(string what)
        {
            if (what == "result") return PpCounters();
            if (what != "run") return "partnerpull <run|result>";
            bool on = ShortcutOn("partnerpull");
            const int Pull = PartnerPullCore.kPull;
            bool fixedNow = PartnerPullCore.Step(1.5f, true, true, false, false, false, 0) == Pull && PartnerPullCore.Step(3f, true, true, false, false, false, 1) == Pull && PartnerPullCore.Act(on);
            bool controls = PartnerPullCore.Step(0.5f, true, true, false, false, false, 0) == PartnerPullCore.kWaitGrace
                && PartnerPullCore.Step(2f, false, true, false, false, false, 0) == PartnerPullCore.kKeepPlayer && PartnerPullCore.Step(2f, true, false, false, false, false, 0) == PartnerPullCore.kKeepPlayer
                && PartnerPullCore.Step(2f, true, true, true, false, false, 0) == PartnerPullCore.kKeepBusy && PartnerPullCore.Step(2f, true, true, false, false, false, -1) == PartnerPullCore.kKeepRank
                && PartnerPullCore.Step(2f, true, true, false, true, false, 0) == PartnerPullCore.kKeepTrip && PartnerPullCore.Step(2f, true, true, false, false, true, 0) == PartnerPullCore.kKeepSocial;
            return "partnerpull check (switch " + (on ? "on" : "off") + "): an autonomous action of the partner ahead of a linked half is ended after the grace and the half moved up=" + fixedNow
                + "; control: grace, player's head or queue, lot trip, conversation, busy head, outranking head kept=" + controls + "; verdict: " + GlVerdict(on, fixedNow, controls, sPpRtsType != null) + "; " + PpCounters();
        }
    }
}
