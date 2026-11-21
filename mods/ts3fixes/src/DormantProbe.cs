using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // dormantprobe (docs/630 §2, default off; log and counters only, changes nothing): how much sim-thread work the
    // "dormant residential lot" idea could remove, before anything is built. A lot is a dormant candidate at a moment when
    //   - it is a residential lot with a household that is not the active one, and not the active lot;
    //   - no player Sim (IsPlayerSim) and no visitor is on it: every Sim on it lives there (LotHome == the lot);
    //   - the lot itself is unseen (UfLotSeen: camera lot, active household lot, lot sphere near the viewers) and every
    //     Sim on it is off screen (UcOnScreen) and far from the viewers (FarFromViewers), the camera part of TownieUnseen
    //     without its standing-posture test (a dormant resident may sit or sleep).
    // The answer is cached per lot per sim tick. Unseen Sim = not a player Sim, off screen, far from the viewers, and its
    // lot not seen. Counted while the switch is on (cumulative, slots below), plus one "dormantprobe:" line per sim-hour
    // with the hour's deltas, per sim-hour rates and a census of the candidate lots at that moment:
    //   plans: every route plan through UfPlan (RouteBackoff.cs; Plan / Replan / PlanFromPoint / ReplanFromPoint), wall ms;
    //   starts: InteractionQueue.DeQueue (AibHook), called once at the end of every ProcessOneInteraction (EA: four call
    //     sites in ProcessOneInteraction, none elsewhere), so one per head interaction the queue tried to run;
    //   pulses: ReactionBroadcaster.PulseSims (BroadcasterPulseHook) by the broadcasting object's lot;
    //   census: candidate lots, NPC residential lots with a household, residents present on candidates, objects on
    //     candidates, alarms owned by those lots' objects and Sims (AlarmManager.mGameObjectIndex) and the fires per
    //     sim-hour their repeat lengths imply (one-shot alarms count once).
    // Slots (fixes-3620, 3620-3659): [3620] plans by unseen Sims, [3621] their wall ms, [3622] plans by candidate residents
    // on their home lot, [3623] their wall ms, [3624] plans total, [3625] starts by unseen Sims, [3626] starts by candidate
    // residents, [3627] starts total, [3628] pulses on candidate lots, [3629] pulses total, [3630] census passes,
    // [3631] candidate lots (last census), [3632] NPC residential lots (last), [3633] residents on candidates (last),
    // [3634] objects on candidates (last), [3635] alarms on candidates (last), [3636] their fires per sim-hour (last),
    // [3637] errors, [3638] autonomous starts by candidate residents. 3639-3659 kept for dormantlots (docs/630 §6).
    public static partial class Fixes
    {
        const int kDp = 3620;
        static int sDpSlot = -1;
        static bool DpOn()
        {
            if (sDpSlot < 0) sDpSlot = SwSlot("dormantprobe");
            return SwOn(sDpSlot);
        }

        // ---- candidate test, cached per lot per tick ------------------------------------------------------------------
        static long sDpTick = -1;
        static readonly Dictionary<ulong, bool> sDpLots = new Dictionary<ulong, bool>();

        static bool DpLotDormant(Lot lot)
        {
            if (lot == null || lot.IsWorldLot) return false;
            long now = SimClock.CurrentTicks;
            if (now != sDpTick) { sDpTick = now; sDpLots.Clear(); }
            bool r;
            if (sDpLots.TryGetValue(lot.LotId, out r)) return r;
            r = DpLotDormantBody(lot);
            sDpLots[lot.LotId] = r;
            return r;
        }

        static bool DpLotDormantBody(Lot lot)
        {
            if (!lot.IsResidentialLot || lot.IsActive) return false;
            Household h = lot.Household, act = Household.ActiveHousehold;
            if (h == null || act == null || h == act || lot == act.LotHome) return false;
            if (UfLotSeen(lot)) return false;
            List<Sim> sims = lot.GetAllActors();
            for (int i = 0; i < sims.Count; i++)
            {
                Sim s = sims[i];
                if (s == null) continue;
                if (IsPlayerSim(s) || s.LotHome != lot) return false;  // player Sim or visitor
                if (UcOnScreen(s) || !FarFromViewers(s.Position)) return false;
            }
            return true;
        }

        // 0 = seen (or a player Sim), 1 = unseen, 2 = unseen resident on its own candidate home lot
        static int DpClass(Sim s)
        {
            if (s == null || IsPlayerSim(s)) return 0;
            Lot lot = s.LotCurrent;
            if (lot != null && !lot.IsWorldLot && UfLotSeen(lot)) return 0;
            if (UcOnScreen(s) || !FarFromViewers(s.Position)) return 0;
            return lot != null && s.LotHome == lot && DpLotDormant(lot) ? 2 : 1;
        }

        // ---- live counts --------------------------------------------------------------------------------------------
        // UfPlan (RouteBackoff.cs route hook), after the plan: proxy = the route's follower
        static void DpPlanNote(IScriptProxy proxy, int ms)
        {
            try
            {
                int[] c = PatchCounters();
                c[kDp + 4]++;
                Sim s = proxy != null ? proxy.Target as Sim : null;
                int k = DpClass(s);
                if (k == 0) return;
                c[kDp + 0]++; c[kDp + 1] += ms;
                if (k == 2) { c[kDp + 2]++; c[kDp + 3] += ms; }
            }
            catch (Exception) { PatchCounters()[kDp + 17]++; }
        }

        // AibHook.DeQueueHook (AutonomyInUseBlock.cs): the head interaction the queue is finishing
        static void DpStartNote(InteractionInstance head)
        {
            try
            {
                int[] c = PatchCounters();
                c[kDp + 7]++;
                Sim s = head != null ? head.InstanceActor : null;
                int k = DpClass(s);
                if (k == 0) return;
                c[kDp + 5]++;
                if (k == 2) { c[kDp + 6]++; if (head.Autonomous) c[kDp + 18]++; }
            }
            catch (Exception) { PatchCounters()[kDp + 17]++; }
        }

        // BroadcasterPulseHook (Fixes.cs): one pulse of a broadcaster on object o
        static void DpPulseNote(IGameObject o)
        {
            try
            {
                int[] c = PatchCounters();
                c[kDp + 9]++;
                if (o != null && DpLotDormant(o.LotCurrent)) c[kDp + 8]++;
            }
            catch (Exception) { PatchCounters()[kDp + 17]++; }
        }

        // ---- hourly census and log line -----------------------------------------------------------------------------
        static readonly FieldInfo sDpIndex = typeof(AlarmManager).GetField("mGameObjectIndex", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo sDpTimers = typeof(AlarmManager).GetField("mTimers", BindingFlags.Instance | BindingFlags.NonPublic);
        static PropertyInfo sDpRepeating, sDpUnit;
        static FieldInfo sDpLen;
        static AlarmHandle sDpAlarm = AlarmHandle.kInvalidHandle;
        static int[] sDpLast;           // the counters at the previous line
        static long sDpLastTicks = -1;
        static int sDpLines;

        // every world load (UfTpCleanupWorldLoad, sim thread)
        static void DormantProbeWorldLoad()
        {
            sDpLast = null; sDpLastTicks = -1; sDpLots.Clear(); sDpTick = -1;
            if (AlarmManager.Global == null) return;
            if (sDpAlarm != AlarmHandle.kInvalidHandle) AlarmManager.Global.RemoveAlarm(sDpAlarm);
            sDpAlarm = AlarmManager.Global.AddAlarmRepeating(1f, TimeUnit.Hours, delegate { try { DpHour(); } catch (Exception) { PatchCounters()[kDp + 17]++; } },
                1f, TimeUnit.Hours, "Ts3Fixes.DormantProbe", AlarmType.NeverPersisted, null);
        }

        static void DpHour()
        {
            if (!DpOn() || sDpLines >= 2000) { sDpLast = null; return; }
            int[] c = PatchCounters();
            DpCensus(c);
            long now = SimClock.CurrentTicks;
            if (sDpLast == null) { sDpLast = new int[19]; Array.Copy(c, kDp, sDpLast, 0, 19); sDpLastTicks = now; return; }  // first hour: baseline only
            float h = SimClock.ConvertFromTicks(now - sDpLastTicks, TimeUnit.Hours);
            int[] d = new int[19];
            for (int i = 0; i < 19; i++) d[i] = unchecked(c[kDp + i] - sDpLast[i]);
            Array.Copy(c, kDp, sDpLast, 0, 19); sDpLastTicks = now;
            sDpLines++;
            DiagLine("dormantprobe: " + h.ToString("0.00") + " sim-h, plans all " + d[4] + " unseen " + d[0] + " (" + d[1] + " ms) dormant " + d[2] + " (" + d[3] + " ms)"
                + ", starts all " + d[7] + " unseen " + d[5] + " dormant " + d[6] + " (autonomous " + d[18] + ")"
                + ", pulses all " + d[9] + " dormant " + d[8]
                + ", census lots " + c[kDp + 11] + " of " + c[kDp + 12] + " npc-res, residents " + c[kDp + 13] + ", objects " + c[kDp + 14]
                + ", alarms " + c[kDp + 15] + " (" + c[kDp + 16] + " fires/sim-h), errors " + d[17]
                + ", day " + SimClock.ElapsedTime(TimeUnit.Days).ToString("0") + " hour " + SimClock.HoursPassedOfDay.ToString("0"));
        }

        static void DpCensus(int[] c)
        {
            c[kDp + 10]++;
            int lots = 0, npcRes = 0, residents = 0, objects = 0;
            Dictionary<ulong, bool> cand = new Dictionary<ulong, bool>();
            foreach (Lot lot in LotManager.AllLots)
            {
                if (lot == null || lot.IsWorldLot || !lot.IsResidentialLot || lot.Household == null || lot.Household == Household.ActiveHousehold) continue;
                npcRes++;
                if (!DpLotDormant(lot)) continue;
                lots++;
                cand[lot.LotId] = true;
                residents += lot.GetAllActorsCount();
                objects += lot.GetObjects<GameObject>().Length;
            }
            c[kDp + 11] = lots; c[kDp + 12] = npcRes; c[kDp + 13] = residents; c[kDp + 14] = objects;
            int alarms = 0; double fires = 0;
            AlarmManager am = AlarmManager.Global;
            IDictionary index = am != null && sDpIndex != null ? sDpIndex.GetValue(am) as IDictionary : null;
            IDictionary timers = am != null && sDpTimers != null ? sDpTimers.GetValue(am) as IDictionary : null;
            if (index != null && timers != null && cand.Count > 0)
            {
                foreach (DictionaryEntry e in index)
                {
                    IGameObject go = e.Key as IGameObject;
                    Lot l = go != null ? go.LotCurrent : null;
                    if (l == null || !cand.ContainsKey(l.LotId)) continue;
                    IEnumerable handles = e.Value as IEnumerable;
                    if (handles == null) continue;
                    foreach (object hd in handles)
                    {
                        IEnumerable list = timers.Contains(hd) ? timers[hd] as IEnumerable : null;
                        if (list == null) continue;
                        foreach (object t in list) { alarms++; fires += DpFiresPerHour(t); }
                    }
                }
            }
            c[kDp + 15] = alarms; c[kDp + 16] = (int)Math.Round(fires);
        }

        static double DpFiresPerHour(object t)
        {
            if (sDpRepeating == null)
            {
                Type tt = t.GetType();
                sDpRepeating = tt.GetProperty("Repeating"); sDpUnit = tt.GetProperty("UnitOfTime"); sDpLen = tt.GetField("RepeatLength");
            }
            if (sDpRepeating == null || sDpUnit == null || sDpLen == null || !(bool)sDpRepeating.GetValue(t, null)) return 1.0;
            float len = (float)sDpLen.GetValue(t);
            long ticks = SimClock.ConvertToTicks(len, (TimeUnit)sDpUnit.GetValue(t, null));
            float min = SimClock.ConvertFromTicks(ticks, TimeUnit.Minutes);
            return min > 0f ? 60.0 / min : 1.0;
        }
    }
}
