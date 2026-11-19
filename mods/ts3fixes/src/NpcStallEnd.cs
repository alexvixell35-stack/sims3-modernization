using System;
using System.Collections.Generic;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // npcstallend (tuning kNpcStallEnd, default off; kNpcStallEndHours 3; docs/649).
    // [measured, health.jsonl, OFF/sweep runs phase-20261115..19] NPCs stand for 6-23 sim-hours in one interaction instance
    // that never ends: PlayCatch (two children, 23 h overnight), WorkOut (18-24 h), Take Out Trash, Get Bills, Switch To
    // Outfit, Visit Community Lot (still at the venue), Dance Together. The watchdog's [48] stationary rule resets such a Sim
    // only after a whole sim-day (SetObjectToReset).
    // [verified, decompiled] PlayCatchObject.PlayCatchInteraction.RunPostRoute loops `while (!HasExitReason(Default))
    // PlayCatchLoop()` and never calls InteractionInstance.UpdateLoop, so EA's fun exit, stress exit and the 3 sim-hour
    // TimeoutForFunOrStressInteractions never apply, and the low-LOD cap of Interaction.DoLoop is not reached either: only a
    // cancel or a higher-priority push ends it. WorkOut, Get Bills and Take Out Trash have the same frozen motives as the
    // docs/601 outfit waits: their task sits in Sim.SwitchOutfitHelper.Wait (WorkOut's GoingToWorkOut change; the route-time
    // GoingOutside change), which ignores exit reasons, so a cancel cannot reach it; outfitwaitend ends those (own cause) and,
    // with this switch on, the "cleared" cause it left alone (OutfitWait.cs OutfitWaitEndTry, docs/649 §3).
    // This fix: every 10 sim-minutes a pass over LotManager.Actors tracks each Sim's head interaction instance and position.
    // A Sim that kept the same instance within 1 m for more than kNpcStallEndHours gets one EA cancel of that instance
    // (InteractionQueue.CancelInteraction(head, false): CanceledByScript on the actor, the interaction's own exit runs, so
    // postures, animations, linked partners (FinishLinkedInteraction) and commodity updates clean up as for a player cancel).
    // Never twice on one instance: a cancel that did not take is counted and left to the watchdog's sim-day reset.
    // Left alone: the allowed kinds of NpcStallEndCore.Kind (sleep, work or school, performing, service roles, fishing,
    // reading, watching), Sims routing, in a rabbit hole posture, parented (vehicle, boat, helm, carried), with a role, babies
    // and toddlers, and Sims whose task is inside an outfit wait (outfitwaitend's).
    // Household rule: HouseholdExempt and IsPlayerSim (household, selected, selectable, unknown scope) are never touched.
    // Counters [4100] ended, [4101] exempt, [4102] skipped (allowed kind or busy), [4103] errors, [4104] cancels that did not
    // take, [4105] left to outfitwaitend (outfit wait), [4106] cleared outfit waits ended (OutfitWait.cs), [4107] passes.
    // Each stall is counted once. Log "npcstallend: <name> <definition> stuck H sim-h, ended", capped.
    public static partial class Fixes
    {
        const int kNse = 4100;
        sealed class NseTrack { public WeakReference Head; public Vector3 Pos; public long Since; public int Verdict = -1; public bool Tried; }
        static readonly Dictionary<ulong, NseTrack> sNse = new Dictionary<ulong, NseTrack>();
        static readonly Dictionary<ulong, int> sNseOutfitWait = new Dictionary<ulong, int>();  // Sim object id -> outfit waits in progress (OutfitWait.cs)
        static AlarmHandle sNseAlarm = AlarmHandle.kInvalidHandle;
        static int sNseMinute = -1, sNseLogged, sNseLines;

        static void NseLog(string s)
        {
            int minute = System.Environment.TickCount / 60000;
            if (minute != sNseMinute) { sNseMinute = minute; sNseLogged = 0; }
            if (sNseLogged++ < 10 && sNseLines++ < 300) DiagLine("npcstallend: " + s);
        }

        // OutfitWait.cs WaitHook: a Sim's task enters / leaves SwitchOutfitHelper.Wait (sim thread)
        static void NseOutfitWait(Sim s, bool enter)
        {
            if (s == null) return;
            ulong id = s.ObjectId.Value;
            int n;
            sNseOutfitWait.TryGetValue(id, out n);
            n += enter ? 1 : -1;
            if (n > 0) sNseOutfitWait[id] = n; else sNseOutfitWait.Remove(id);
        }

        // every world load (from UfTpCleanupWorldLoad, sim thread)
        static void NpcStallWorldLoad()
        {
            sNse.Clear(); sNseOutfitWait.Clear();
            if (AlarmManager.Global == null) return;
            if (sNseAlarm != AlarmHandle.kInvalidHandle) AlarmManager.Global.RemoveAlarm(sNseAlarm);
            sNseAlarm = AlarmManager.Global.AddAlarmRepeating(10f, TimeUnit.Minutes, delegate { try { NsePass(); } catch (Exception) { PatchCounters()[kNse + 3]++; } },
                10f, TimeUnit.Minutes, "Ts3Fixes.NpcStallEnd", AlarmType.NeverPersisted, null);
        }

        static void NsePass()
        {
            if (!ShortcutOn("npcstallend")) { if (sNse.Count > 0) sNse.Clear(); return; }
            int[] c = PatchCounters();
            c[kNse + 7]++;
            long now = SimClock.CurrentTicks;
            double limit = NpcStallEndCore.Limit(Instantiator.kNpcStallEndHours);
            Dictionary<ulong, bool> live = new Dictionary<ulong, bool>();
            foreach (Sim s in new List<Sim>(LotManager.Actors))
            {
                try { if (s != null && !s.HasBeenDestroyed && s.SimDescription != null) { live[s.ObjectId.Value] = true; NseSim(s, now, limit, c); } }
                catch (Exception) { c[kNse + 3]++; }
            }
            List<ulong> gone = null;
            foreach (ulong id in sNse.Keys) if (!live.ContainsKey(id)) (gone ?? (gone = new List<ulong>())).Add(id);
            if (gone != null) foreach (ulong id in gone) sNse.Remove(id);
        }

        static void NseSim(Sim s, long now, double limit, int[] c)
        {
            ulong id = s.ObjectId.Value;
            InteractionInstance head = s.InteractionQueue != null ? s.InteractionQueue.GetHeadInteraction() : null;
            NseTrack t;
            if (head == null) { sNse.Remove(id); return; }
            bool known = sNse.TryGetValue(id, out t);
            bool same = known && t.Head != null && object.ReferenceEquals(t.Head.Target, head);
            if (!known || NpcStallEndCore.Progress(same, (s.Position - t.Pos).Length()) || t.Since > now)
            {
                sNse[id] = new NseTrack { Head = new WeakReference(head), Pos = s.Position, Since = now };
                return;
            }
            double hours = SimClock.ConvertFromTicks(now - t.Since, TimeUnit.Hours);
            if (hours <= limit) return;
            bool exempt = HouseholdExempt(s) || IsPlayerSim(s);  // household rule: unknown scope counts as household
            string def = NseDefName(head);
            bool busy = false;
            if (!exempt)
            {
                SimDescription d = s.SimDescription;
                busy = s.IsRouting || s.SimInRabbitHolePosture || s.Parent != null || d.AssignedRole != null || d.ToddlerOrBelow;
            }
            bool outfit = sNseOutfitWait.ContainsKey(id);
            int v = NpcStallEndCore.Decide(exempt, hours, limit, exempt ? null : NpcStallEndCore.Kind(def), busy, outfit, t.Tried);
            if (v == NpcStallEndCore.kWait) return;
            if (v == NpcStallEndCore.kEnd)
            {
                t.Tried = true;
                t.Verdict = v;
                s.InteractionQueue.CancelInteraction(head, false);  // EA's cancel: CanceledByScript, the interaction's own exit
                c[kNse]++;
                NseLog(s.FullName + " " + def + " stuck " + hours.ToString("0.0") + " sim-h, ended");
                return;
            }
            if (t.Verdict == v) return;  // each stall counted once per verdict
            t.Verdict = v;
            switch (v)
            {
                case NpcStallEndCore.kExempt: c[kNse + 1]++; break;
                case NpcStallEndCore.kSkipKind: c[kNse + 2]++; break;
                case NpcStallEndCore.kSkipOutfit: c[kNse + 5]++; break;
                case NpcStallEndCore.kTried:
                    c[kNse + 4]++;
                    NseLog(s.FullName + " " + def + " stuck " + hours.ToString("0.0") + " sim-h, cancel did not take (left to the watchdog)");
                    break;
            }
        }

        static string NseDefName(InteractionInstance head)
        {
            try
            {
                if (head.InteractionDefinition != null) return head.InteractionDefinition.GetType().FullName;
            }
            catch (Exception) { }
            return head.GetType().FullName;
        }
    }
}
