using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Objects.HobbiesSkills;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // horseshoesolo (tunings kHorseshoeSolo, off by default, and kHorseshoeSoloHours; docs/865). EA bug fix: a Sim playing
    // horseshoes alone never stops, and a lost player blocks the court's queue.
    //
    // [verified, decompiled] HorseshoeCourt+TakeTurn.Run: the throw loop runs while the Sim has no exit reason and adds
    // ExitReason.Finished after kNumThrowsPerTurn throws. After it, DoEndOfTurnChecks returns before any scoring while
    // mPlayerQueue holds one Sim (unless mShouldReset), so mGameOver is never set; with only Finished pending the Sim is queued
    // again and the next TakeTurn pushed as a continuation, ahead of the rest of its interaction queue. Any other exit reason
    // ends the game: no re-push, StandardExit, RouteAway. [verified in our test logs] festival NPCs (NpcParty) threw alone for
    // 17-53 game hours with Go Home queued behind.
    // [verified, decompiled] TakeTurn.Cleanup never takes the Sim out of mPlayerQueue (WaitToThrow.Cleanup does unless it
    // finished), so a Sim reset or deleted mid-turn stays at the front and every later player loops in WaitToThrow (it waits
    // for Peek() == itself). DeadRefs' HorseshoeHolders clears mPlayersWhoHaveThrown and mScores only.
    // Fix, a pass every kHsPassMinutes sim minutes over every court:
    //   (1) a solo player (queue of one, running TakeTurn on this court) that is not household, selected or of unknown scope
    //       (IsPlayerSim: the shared HouseholdExempt without its HouseholdToo lift, since a cut is never lifted, CLAUDE.md) and
    //       has played alone kHorseshoeSoloHours, or has a non-autonomous interaction queued behind the game, gets
    //       ExitReason.CanceledByScript (what InteractionQueue.CancelInteraction(inst, false) adds to a running head):
    //       TakeTurn ends the throw and takes EA's own leave path above.
    //   (2) a queued Sim that is destroyed, or not playing on this court (no TakeTurn / WaitToThrow for it) at two passes in a
    //       row (reset, cancelled before its turn ran, out of the world), leaves the queue through EA's private
    //       RemoveSimFromQueue (rebuilds the queue without it, nothing else). Any court, household Sims too: no Sim changes.
    // With the switch off the pass only counts (2193). Counters: [2191] solo turns ended, [2192] players removed from
    // queues, [2193] seen with the switch off, [2194] errors. Check: HorseshoeSoloCheck.
    public static partial class Fixes
    {
        const float kHsPassMinutes = 10f;
        static readonly FieldInfo sHsQueue = typeof(HorseshoeCourt).GetField("mPlayerQueue", kInstAll);
        static readonly MethodInfo sHsRemove = typeof(HorseshoeCourt).GetMethod("RemoveSimFromQueue", kInstAll, null, new Type[] { typeof(Sim) }, null);
        static readonly Type sHsTurn = typeof(HorseshoeCourt).GetNestedType("TakeTurn", BindingFlags.Public | BindingFlags.NonPublic);
        static readonly Type sHsWait = typeof(HorseshoeCourt).GetNestedType("WaitToThrow", BindingFlags.Public | BindingFlags.NonPublic);
        static readonly Dictionary<Sim, float> sHsSoloSince = new Dictionary<Sim, float>();  // sim-clock hour first seen playing alone
        static List<Sim> sHsIdle = new List<Sim>();  // queued Sims not playing at the last pass
        static AlarmHandle sHsAlarm = AlarmHandle.kInvalidHandle;

        internal static string HsMissing()
        {
            string m = "";
            if (sHsQueue == null) m = "mPlayerQueue";
            if (sHsRemove == null) m += (m.Length > 0 ? ", " : "") + "RemoveSimFromQueue";
            if (sHsTurn == null) m += (m.Length > 0 ? ", " : "") + "TakeTurn";
            if (sHsWait == null) m += (m.Length > 0 ? ", " : "") + "WaitToThrow";
            return m;
        }

        // World load finished (Instantiator): forget the old world's Sims, (re)start the pass alarm (never saved; the switch is
        // read on every pass).
        public static void HorseshoeSoloWorldLoad()
        {
            try
            {
                sHsSoloSince.Clear();
                sHsIdle = new List<Sim>();
                if (AlarmManager.Global == null) return;
                if (sHsAlarm != AlarmHandle.kInvalidHandle) AlarmManager.Global.RemoveAlarm(sHsAlarm);
                sHsAlarm = AlarmManager.Global.AddAlarmRepeating(kHsPassMinutes, TimeUnit.Minutes, delegate { HsPass(); }, kHsPassMinutes, TimeUnit.Minutes, "Ts3Fixes.HorseshoeSolo", AlarmType.NeverPersisted, null);
            }
            catch (Exception) { PatchCounters()[2194]++; }
        }

        static void HsPass()
        {
            try
            {
                if (HsMissing().Length > 0) return;
                bool on = ShortcutOn("horseshoesolo");
                float now = SimClock.ElapsedTime(TimeUnit.Hours);
                List<Sim> idle = new List<Sim>(), solo = new List<Sim>();
                foreach (HorseshoeCourt court in Sims3.Gameplay.Queries.GetObjects<HorseshoeCourt>())
                {
                    try { HsCourt(court, on, now, idle, solo); }
                    catch (Exception) { PatchCounters()[2194]++; }
                }
                List<Sim> drop = new List<Sim>();
                foreach (Sim s in sHsSoloSince.Keys) if (!solo.Contains(s)) drop.Add(s);
                foreach (Sim s in drop) sHsSoloSince.Remove(s);  // a new solo game starts a new clock
                sHsIdle = idle;
            }
            catch (Exception) { PatchCounters()[2194]++; }
        }

        static void HsCourt(HorseshoeCourt court, bool on, float now, List<Sim> idle, List<Sim> solo)
        {
            Queue<Sim> q = court == null || court.HasBeenDestroyed ? null : sHsQueue.GetValue(court) as Queue<Sim>;
            if (q == null || q.Count == 0) return;
            int[] c = PatchCounters();
            foreach (Sim s in new List<Sim>(q))
            {
                bool gone = SimGone(s), playing = !gone && HsPlaying(s, court);
                if (!gone && !playing) idle.Add(s);
                if (!HsCore.Remove(gone, playing, !gone && sHsIdle.Contains(s))) continue;
                if (!on) { c[2193]++; continue; }
                sHsRemove.Invoke(court, new object[] { s });
                c[2192]++;
            }
            q = sHsQueue.GetValue(court) as Queue<Sim>;  // RemoveSimFromQueue may have put a new queue in the field
            if (q == null || q.Count != 1) return;
            Sim p = q.Peek();
            if (SimGone(p) || !HsPlaying(p, court)) return;
            solo.Add(p);
            float since;
            if (!sHsSoloSince.TryGetValue(p, out since)) { since = now; sHsSoloSince[p] = now; }
            InteractionInstance cur = p.InteractionQueue.GetCurrentInteraction();
            bool turn = cur != null && cur.GetType() == sHsTurn && ReferenceEquals(cur.Target, court);
            if (!HsCore.Cut(q.Count, turn, IsPlayerSim(p), now - since, Instantiator.kHorseshoeSoloHours, HsWaiting(p))) return;
            if (p.HasExitReason() && !p.OnlyHasExitReason(ExitReason.Finished)) return;  // already leaving: EA ends the game
            if (!on) { c[2193]++; return; }
            p.AddExitReason(ExitReason.CanceledByScript);  // with Finished too, OnlyHasExitReason(Finished) is false: no re-push
            sHsSoloSince.Remove(p);
            c[2191]++;
        }

        // A TakeTurn or WaitToThrow on this court anywhere in the Sim's interaction queue (running or waiting).
        static bool HsPlaying(Sim s, HorseshoeCourt court)
        {
            if (s.InteractionQueue == null) return false;
            foreach (InteractionInstance i in s.InteractionQueue.InteractionList)
                if (i != null && (i.GetType() == sHsTurn || i.GetType() == sHsWait) && ReferenceEquals(i.Target, court)) return true;
            return false;
        }

        // A non-autonomous interaction (player command, situation push such as Go Home [inferred: pushed non-autonomous])
        // queued behind the running one, other than the game's own continuations.
        static bool HsWaiting(Sim s)
        {
            bool head = true;
            foreach (InteractionInstance i in s.InteractionQueue.InteractionList)
            {
                if (head) { head = false; continue; }
                if (i != null && i.GetType() != sHsTurn && i.GetType() != sHsWait && !i.Autonomous) return true;
            }
            return false;
        }

        // Live courts: queue sizes, who is queued (household, gone, not playing), solo players and their hours alone.
        static string HsScan()
        {
            string r = "";
            int courts = 0, queued = 0, lost = 0;
            float now = SimClock.ElapsedTime(TimeUnit.Hours);
            foreach (HorseshoeCourt court in Sims3.Gameplay.Queries.GetObjects<HorseshoeCourt>())
            {
                courts++;
                Queue<Sim> q = court == null ? null : sHsQueue.GetValue(court) as Queue<Sim>;
                if (q == null || q.Count == 0) continue;
                string names = "";
                foreach (Sim s in q)
                {
                    queued++;
                    string n;
                    float since;
                    if (SimGone(s)) { n = "GONE"; lost++; }
                    else
                    {
                        n = s.FullName + (IsPlayerSim(s) ? " (household)" : "");
                        if (!HsPlaying(s, court)) { n += " NOT PLAYING"; lost++; }
                        if (sHsSoloSince.TryGetValue(s, out since)) n += " alone " + Math.Round(now - since, 1) + " h";
                    }
                    names += (names.Length > 0 ? ", " : "") + n;
                }
                r += " | " + (court.LotCurrent == null ? "no lot" : court.LotCurrent.Name) + ": " + q.Count + " queued (" + names + ")";
            }
            return courts + " courts, " + queued + " queued, " + lost + " gone or not playing" + r;
        }

        // HorseshoeSoloCheck run: HsCore for the reported layouts with controls; read-only.
        //   off BROKEN = EA's outcome everywhere (nothing ends, nobody leaves a queue); on FIXED = (a) an NPC alone 3 h is cut,
        //   (b) an NPC alone 0.5 h with Go Home queued behind is cut, (c) a deleted Sim leaves the queue, (d) a queued Sim not
        //   playing at two passes leaves it. Controls (kept in both): a household Sim alone 10 h with a command waiting, a
        //   two-player game, a short solo game, a solo Sim not in TakeTurn, a Sim not playing at one pass only, a Sim playing.
        //   scan    live courts, queues, solo hours, gone / not playing Sims; result  counters 2191-2194
        public static object HorseshoeSoloCheck(string what)
        {
            int[] c = PatchCounters();
            string missing = HsMissing();
            string counters = "horseshoesolo: solo turns ended 2191=" + c[2191] + ", players removed from queues 2192=" + c[2192]
                + ", seen off 2193=" + c[2193] + ", errors 2194=" + c[2194] + "; solo players tracked " + sHsSoloSince.Count
                + ", cap " + Instantiator.kHorseshoeSoloHours + " h, alarm=" + (sHsAlarm != AlarmHandle.kInvalidHandle)
                + "; members missing: " + (missing.Length == 0 ? "none" : missing);
            if (what == "result") return counters;
            if (what == "scan") return "horseshoesolo scan: " + (missing.Length > 0 ? "members missing" : HsScan()) + "; " + counters;
            if (what != "run") return "horseshoesolo <run|scan|result>";
            bool on = ShortcutOn("horseshoesolo");
            bool a = HsCutAct(on, 1, true, false, 3, false), b = HsCutAct(on, 1, true, false, 0.5, true);
            bool cc = HsRemoveAct(on, true, false, false), d = HsRemoveAct(on, false, false, true);
            int fixedN = (a ? 1 : 0) + (b ? 1 : 0) + (cc ? 1 : 0) + (d ? 1 : 0);
            bool controlsOk = !HsCutAct(on, 1, true, true, 10, true) && !HsCutAct(on, 2, true, false, 10, true)
                && !HsCutAct(on, 1, true, false, 0.5, false) && !HsCutAct(on, 1, false, false, 10, true)
                && !HsRemoveAct(on, false, false, false) && !HsRemoveAct(on, false, true, true);
            string v = !on ? (fixedN == 0 ? "BROKEN" : "UNEXPECTED") : (fixedN == 4 ? "FIXED" : "UNEXPECTED");
            string verdict = !controlsOk || missing.Length > 0 ? "UNEXPECTED" : v;
            return "horseshoesolo check (" + (on ? "on" : "off") + "): NPC alone 3 h cut=" + a + ", NPC alone with Go Home waiting cut=" + b
                + ", deleted Sim out of the queue=" + cc + ", idle Sim out after two passes=" + d + " (" + v + "); controls ok=" + controlsOk
                + " (household solo player, two-player game, short solo game, not in TakeTurn, idle once, playing kept); verdict: " + verdict + "; " + counters;
        }

        // The pass's outcome: EA's (nothing) with the switch off. Cap 2 h, the shipped default.
        static bool HsCutAct(bool on, int queued, bool turn, bool player, double hours, bool waiting)
        {
            return on && HsCore.Cut(queued, turn, player, hours, 2, waiting);
        }

        static bool HsRemoveAct(bool on, bool gone, bool playing, bool idleBefore)
        {
            return on && HsCore.Remove(gone, playing, idleBefore);
        }
    }
}
