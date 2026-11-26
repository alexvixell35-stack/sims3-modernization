using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Objects.Appliances;
using Sims3.Gameplay.Objects.Beds;
using Sims3.Gameplay.Objects.Plumbing;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // nextplan (docs/686 §38, default off). The "stand and think" between two actions of a Sim.
    //
    // [verified, decompiled Sim.DoInteraction / CanRunAutonomyImmediately, AutonomyManager.Simulate, Autonomy.RunAutonomy]
    // When a Sim's queue runs empty, its loop plays its posture idle (LoopIdle) and then adds itself to the autonomy manager's
    // queue: at once when its last action was autonomous and succeeded (or it is a non-selectable NPC), else only once
    // kAutonomyDelayNormal (5 sim-min; 2 after a player's cancel, 7 while talking with the active Sim) has passed since the
    // queue ran empty. The manager runs ONE search per frame, alternating its high-LOD and low-LOD queues, so a household Sim
    // waits behind every Sim queued before it; the search itself can yield (ScoreInteractionsOnObjects). The pick is added to
    // the queue and the Sim's loop wakes from its 10-tick sleep (queuewake shortens that one).
    //
    // Fix: at the start of LoopIdle (hook, before the posture idle), once per "queue ran empty", a Sim that EA would hand to
    // the manager (Decide in NextPlanCore.cs) runs EA's own pass, Autonomy.TryFindBestAction, on its own task at once. The
    // pick is EA's (same scoring, same probabilities, same queue Add); only when it runs changes. A pick in the queue skips
    // the posture idle, exactly like EA's loop with a head. Nothing picked: EA's path as before (idle, manager queue).
    //   - The 5 sim-min delay is kept for the active Sim (the player's window), for a Sim talking with it, after a player's
    //     cancel, while mounted, and for every NPC. A non-active household Sim whose last action was the player's starts its
    //     own next action at once (nextplanhh).
    //   - A player's order is never overridden: the pass is EA's, so EA's rules hold: a pick is added only to an empty queue
    //     (ConsiderPerformingAutonomousAction), and a user-directed add cancels a lower-priority autonomous entry
    //     (RemoveLowerPriorityInteractions). Counted as "dropped by a higher-priority add".
    //   - Walks stay walks: the route is EA's own plan inside the picked interaction's Run.
    //   - NPCs: high-LOD only, at most kNextPlanNpcPerTick passes per sim tick (EA's one-a-frame queue is the throttle).
    // Household-aware pick: every autonomous add of a Sim in scope claims its target when the target is a single-use object
    // (its type keeps GameObject's own GetInUseAutonomyMultiplier: no seat or viewer logic of its own). Until that entry
    // leaves the queue, or kNextPlanClaimMinutes, another Sim's search scores the object with EA's route-pending factor
    // (GetRoutePendingAutonomyMultiplier: what EA gives an object a Sim is walking to), so two Sims do not head for one
    // toilet or easel in the window between one Sim's pick and its first step. The facility planner (facilityplan, #743)
    // works inside the same search (its GetInUseAutonomyMultiplier hook): busy-until and booked uses count there.
    // Probe (both arms): per Sim the gap "queue ran empty -> autonomous pick in the queue", split into the wait before the
    // search (EA's delay class or not) and the search, in real ms and sim-min; same-target picks (claimed / in use by
    // another Sim); household autonomous actions done per sim-hour. actdelay measures pick -> move.
    // Switches: nextplan (NPCs), nextplanhh (household and selected Sims too), nextplanlog (one line per pass, capped).
    // Off = compare mode (would counts; claims tracked, not applied). Counters 4940-4959. Check: NextPlanCheck.
    // Swaps (stacked, with a stub): Sim.LoopIdle, GameObject.GetInUseAutonomyMultiplier, InteractionInstance.OnAddedToQueue,
    // InteractionInstance.OnRemovedFromQueue, Autonomy.RunAutonomy(bool).
    public static partial class Fixes
    {
        const int kNpPass = 4940, kNpWould = 4941, kNpQueued = 4942, kNpNothing = 4943, kNpDelaySkip = 4944, kNpKeptDelay = 4945,
            kNpBudget = 4946, kNpClaims = 4947, kNpClaimLow = 4948, kNpClaimWould = 4949, kNpSameClaimed = 4950, kNpSameInUse = 4951,
            kNpExpired = 4952, kNpErrors = 4953, kNpPicks = 4954, kNpDropped = 4955, kNpLines = 4956, kNpSearches = 4957,
            kNpSkipped = 4958, kNpHhDone = 4959;

        sealed class NpGap { public int Ms0, SearchAt = -1, SearchMs; public long T0; public bool Hh, Delayed, Passed; }
        static readonly NextPlanCore sNp = new NextPlanCore();
        static readonly Dictionary<ulong, NpGap> sNpGaps = new Dictionary<ulong, NpGap>();         // by Sim: open gap
        static readonly Dictionary<ulong, InteractionInstance> sNpOurs = new Dictionary<ulong, InteractionInstance>();  // by Sim
        static readonly Dictionary<Type, bool> sNpSingle = new Dictionary<Type, bool>();           // type -> single-use
        // [scope 0 household / 1 npc][0 total ms, 1 total sim-min x10, 2 wait ms (EA at once), 3 wait ms (EA delay class),
        // 4 wait sim-min x10 (delay class), 5 search ms]
        static readonly NextPlanCore.Hist[][] sNpHist = { NpHists(), NpHists() };
        static readonly LineCapCore sNpCap = new LineCapCore(20, 60000, 300);
        static FieldInfo sNpWasAuto, sNpOk, sNpExit;
        static MethodInfo sNpCanRun;
        static long sNpTick, sNpStart, sNpTicksPerMin = 1;
        static int sNpBudgetLeft;
        static IntPtr sNpBaseUse;
        // contention per lot (NextPlanCore.Ratio / Level): recounted once a sim-hour or when the residents change
        sealed class NpLot { public long At; public int Residents, Sims, Level; public float Ratio; public string Name; }
        static readonly Dictionary<ulong, NpLot> sNpLots = new Dictionary<ulong, NpLot>();
        static readonly int[] sNpLevelClaims = new int[3], sNpLevelDown = new int[3];
        static readonly int[] sNpSameHh = new int[2];  // same-target picks of household Sims: [0] claimed by another Sim, [1] in use by another Sim
        static int sNpResetDone;
        static string sNpLast = "", sNpLastError = "", sNpMissing = "";

        static NextPlanCore.Hist[] NpHists() { NextPlanCore.Hist[] h = new NextPlanCore.Hist[6]; for (int i = 0; i < h.Length; i++) h[i] = new NextPlanCore.Hist(); return h; }

        static void ApplyNextPlan()
        {
            sNpMissing = "";
            const BindingFlags inst = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            sNpWasAuto = typeof(Sim).GetField("mLastInteractionWasAutonomous", inst);
            sNpOk = typeof(Sim).GetField("mLastInteractionSucceeded", inst);
            sNpExit = typeof(Sim).GetField("mExitReason", inst);
            sNpCanRun = typeof(Sim).GetMethod("CanRunAutonomyImmediately", inst, null, Type.EmptyTypes, null);
            if (sNpWasAuto == null || sNpOk == null || sNpExit == null || sNpCanRun == null) sNpMissing += " Sim autonomy-delay members";
            if (!Patch(typeof(Sim), "LoopIdle", Type.EmptyTypes, typeof(NpIdleHook), "IdleHook", "IdleOrig")) sNpMissing += " Sim.LoopIdle";
            if (!Patch(typeof(GameObject), "GetInUseAutonomyMultiplier", new Type[] { typeof(Sim) }, typeof(NpPickHook), "PickHook", "PickOrig")) sNpMissing += " GameObject.GetInUseAutonomyMultiplier";
            if (!Patch(typeof(InteractionInstance), "OnAddedToQueue", Type.EmptyTypes, typeof(NpInstHook), "AddedHook", "AddedOrig")) sNpMissing += " InteractionInstance.OnAddedToQueue";
            if (!Patch(typeof(InteractionInstance), "OnRemovedFromQueue", new Type[] { typeof(bool) }, typeof(NpInstHook), "RemovedHook", "RemovedOrig")) sNpMissing += " InteractionInstance.OnRemovedFromQueue";
            if (!Patch(typeof(Autonomy), "RunAutonomy", new Type[] { typeof(bool) }, typeof(NpAutHook), "RunHook", "RunOrig")) sNpMissing += " Autonomy.RunAutonomy";
            sNpMissing = sNpMissing.Trim();
            if (sNpMissing.Length > 0) AppliedLog().Add("nextplan: not found: " + sNpMissing);
        }

        // world load: the old world's Sims, instances and objects are gone
        static void NextPlanWorldLoad() { sNpGaps.Clear(); sNpOurs.Clear(); sNp.Clear(); sNpLots.Clear(); sNpStart = 0; }

        static bool NpOn(Sim s, bool hh) { return ShortcutOn("nextplan") && (!hh || ShortcutOn("nextplanhh")); }

        static long NpNow()
        {
            long t = SimClock.CurrentTicks;
            if (sNpStart == 0) { sNpStart = t; long m = SimClock.ConvertToTicks(1f, TimeUnit.Minutes); sNpTicksPerMin = m > 0 ? m : 1; }
            return t;
        }

        // EA's own answer to "is this Sim handed to the autonomy manager now?" (Sim.DoInteraction's idle branch)
        static bool NpEaNow(Sim me, out bool keepWindow)
        {
            keepWindow = false;
            if ((bool)sNpCanRun.Invoke(me, null)) return true;
            Autonomy a = me.Autonomy;
            ExitReason exit = (ExitReason)sNpExit.GetValue(me);
            float since = a.TimeSinceInteractionQueueBecameEmpty, delay;
            bool mounted = me.BeingRiddenPosture != null || me.RidingPosture != null;
            bool talkActive = me.Conversation != null && (me.IsActiveSim || (me.Conversation.WhoTalkedLast != null && me.Conversation.WhoTalkedLast.IsActiveSim));
            if (mounted) delay = Autonomy.AutonomyDelayWhileMounted;
            else if (talkActive) delay = Autonomy.AutonomyDelayDuringSocializing;
            else delay = exit != ExitReason.UserCanceled ? Autonomy.AutonomyDelayNormal : Autonomy.AutonomyDelayAfterUserCancellation;
            keepWindow = me.IsActiveSim || talkActive || mounted || exit == ExitReason.UserCanceled;
            return since < 0f || since >= delay || me.Service != null || me.SimDescription.HasActiveRole;
        }

        // LoopIdle start: EA's pass at once. true = an action is in the queue now (skip the idle).
        static bool NpPass(Sim me)
        {
            NpGap g;
            if (!sNpGaps.TryGetValue(me.ObjectId.Value, out g) || g.Passed || sNpMissing.Length > 0) return false;
            g.Passed = true;  // one pass per "queue ran empty": no retry loop
            InteractionQueue q = me.InteractionQueue;
            bool idle = q != null && q.Count == 0 && q.GetCurrentInteraction() == null && q.TransitionInteraction == null && Simulator.CurrentTask == me.ObjectId && !me.HasBeenDestroyed;
            Autonomy a = me.Autonomy;
            bool autoOn = a != null && AutonomyRestrictions.IsAnyAutonomyEnabled(me);
            bool keepWindow = false, eaNow = idle && autoOn && NpEaNow(me, out keepWindow);
            g.Delayed = !eaNow;
            long now = NpNow();
            if (now != sNpTick) { sNpTick = now; sNpBudgetLeft = Math.Max(0, Instantiator.kNextPlanNpcPerTick); }
            int[] c = PatchCounters();
            int d = NextPlanCore.Decide(true, idle, autoOn, eaNow, keepWindow, g.Hh, autoOn && a.IsRunningHighLODSimulation, sNpBudgetLeft);
            if (d != NextPlanCore.kGo)
            {
                if (d == NextPlanCore.kActiveDelay) c[kNpKeptDelay]++; else if (d == NextPlanCore.kBudget) c[kNpBudget]++; else c[kNpSkipped]++;
                return false;
            }
            if (!NpOn(me, g.Hh)) { c[kNpWould]++; return false; }
            if (!g.Hh) sNpBudgetLeft--;
            c[kNpPass]++;
            a.TryFindBestAction();  // EA's pass: RunAutonomy(addToQueue) -> ConsiderPerformingAutonomousAction -> InteractionQueue.Add
            InteractionInstance h = q.Count > 0 ? q.GetHeadInteraction() : null;
            if (h == null) { c[kNpNothing]++; return false; }
            c[kNpQueued]++;
            if (!eaNow) c[kNpDelaySkip]++;
            sNpOurs[me.ObjectId.Value] = h;
            sNpLast = me.FullName + " -> " + h.GetType().Name + (h.Target != null ? " on " + h.Target.GetType().Name : "") + (eaNow ? "" : " (EA's delay skipped)")
                + " after " + (System.Environment.TickCount - g.Ms0) + " ms";
            if (ShortcutOn("nextplanlog") && sNpCap.Take(System.Environment.TickCount)) { c[kNpLines]++; DiagLine("nextplan: " + sNpLast); }
            return true;
        }

        static int NpLevel(Lot lot, long now)
        {
            if (lot == null || lot.IsWorldLot) return NextPlanCore.kLow;
            ulong id = lot.LotId;
            Household h = lot.Household;
            int residents = h != null && h.Sims != null ? h.Sims.Count : 0;
            NpLot r;
            if (sNpLots.TryGetValue(id, out r) && r.Residents == residents && now - r.At < 60 * sNpTicksPerMin) return r.Level;
            if (r == null) { if (sNpLots.Count >= 64) sNpLots.Clear(); r = new NpLot(); sNpLots[id] = r; }
            int[] n = new int[NextPlanCore.kKinds];
            n[NextPlanCore.kToilet] = lot.GetObjects<Toilet>().Length;
            n[NextPlanCore.kWash] = lot.GetObjects<Shower>().Length + lot.GetObjects<Bathtub>().Length;
            n[NextPlanCore.kSink] = lot.GetObjects<Sink>().Length;
            n[NextPlanCore.kFridge] = lot.GetObjects<Fridge>().Length;
            n[NextPlanCore.kBed] = lot.GetObjects<Bed>().Length;
            int sims = Math.Max(residents, lot.GetSimsCount());  // residents plus guests now on the lot
            r.At = now; r.Residents = residents; r.Sims = sims;
            r.Ratio = NextPlanCore.Ratio(sims, n);
            r.Level = NextPlanCore.Level(r.Ratio);
            r.Name = lot.Name + " (" + sims + " Sims; toilets " + n[0] + ", showers/baths " + n[1] + ", sinks " + n[2] + ", fridges " + n[3] + ", beds " + n[4] + ")";
            return r.Level;
        }

        // single-use: the object's type keeps GameObject's own in-use rule (no override for seats, viewers, groups)
        static bool NpSingleUse(GameObject o)
        {
            if (o == null || o is Sim) return false;
            Type t = o.GetType();
            bool s;
            if (sNpSingle.TryGetValue(t, out s)) return s;
            MethodInfo m = t.GetMethod("GetInUseAutonomyMultiplier", BindingFlags.Instance | BindingFlags.Public, null, new Type[] { typeof(Sim) }, null);
            // by handle: a swap moves the class word, so a swapped method's DeclaringType names the hook class (docs/576 §1)
            if (sNpBaseUse == IntPtr.Zero) sNpBaseUse = typeof(GameObject).GetMethod("GetInUseAutonomyMultiplier", BindingFlags.Instance | BindingFlags.Public, null, new Type[] { typeof(Sim) }, null).MethodHandle.Value;
            s = m != null && m.MethodHandle.Value == sNpBaseUse;
            if (sNpSingle.Count < 2048) sNpSingle[t] = s;
            return s;
        }

        static float NpPick(GameObject o, Sim actor, float m, float rp, float inUse)
        {
            if (sNp.Count == 0 || actor == null) return m;
            long now = NpNow();
            sNp.Sweep(now, false);
            ulong id = o.ObjectId.Value, who = actor.ObjectId.Value;
            if (!sNp.HeldByOther(id, who, now)) return m;
            int level = NpLevel(o.LotCurrent, now);
            float f = NextPlanCore.ClaimFactor(level, m, rp, inUse);
            if (f >= m) return m;  // already in use / routed to: EA's factor is as low
            int[] c = PatchCounters();
            if (!NpOn(actor, IsPlayerSim(actor))) { c[kNpClaimWould]++; return m; }
            c[kNpClaimLow]++; sNpLevelDown[level]++;
            return f;
        }

        static void NpAdded(InteractionInstance ii)
        {
            Sim s = ii.InstanceActor;
            if (s == null || !ii.Autonomous) return;
            InteractionQueue q = s.InteractionQueue;
            ulong who = s.ObjectId.Value;
            long now = NpNow();
            int[] c = PatchCounters();
            NpGap g;
            if (q != null && q.Count == 1 && sNpGaps.TryGetValue(who, out g))
            {
                sNpGaps.Remove(who);
                int ms = System.Environment.TickCount - g.Ms0;
                NextPlanCore.Hist[] h = sNpHist[g.Hh ? 0 : 1];
                h[0].Add(ms); h[1].Add((now - g.T0) * 10 / sNpTicksPerMin);
                if (g.SearchAt >= 0)
                {
                    int w = g.SearchAt - g.Ms0;
                    if (g.Delayed) { h[3].Add(w); } else h[2].Add(w);
                    h[5].Add(g.SearchMs);
                }
                if (g.Delayed) h[4].Add((now - g.T0) * 10 / sNpTicksPerMin);
                c[kNpPicks]++;
            }
            GameObject t = ii.Target as GameObject;
            if (!NpSingleUse(t)) return;
            ulong tid = t.ObjectId.Value;
            ulong holder = sNp.Holder(tid, now);
            bool phh = IsPlayerSim(s);
            if (holder != 0 && holder != who) { c[kNpSameClaimed]++; if (phh) sNpSameHh[0]++; }
            else if (t.InUse && !t.IsActorUsingMe(s)) { c[kNpSameInUse]++; if (phh) sNpSameHh[1]++; }
            int level = NpLevel(t.LotCurrent, now);
            long ttl = (long)(NextPlanCore.ClaimMinutes(level, Instantiator.kNextPlanClaimMinutes) * sNpTicksPerMin);
            int exp = sNp.Expired;
            if (sNp.Add(tid, who, now, ttl)) { c[kNpClaims]++; sNpLevelClaims[level]++; }
            c[kNpExpired] += sNp.Expired - exp;
        }

        static void NpRemoved(InteractionInstance ii, bool isHead)
        {
            Sim s = ii.InstanceActor;
            if (s == null) return;
            ulong who = s.ObjectId.Value;
            int[] c = PatchCounters();
            InteractionInstance ours;
            if (sNpOurs.TryGetValue(who, out ours) && ours == ii) { sNpOurs.Remove(who); if (ii.Cancelled) c[kNpDropped]++; }
            GameObject t = ii.Target as GameObject;
            if (t != null && sNp.TargetOf(who) == t.ObjectId.Value) sNp.Release(who);
            if (!isHead) return;
            bool hh = IsPlayerSim(s);
            if (hh && ii.Autonomous && !ii.Cancelled) c[kNpHhDone]++;  // every household autonomous head that ended
            InteractionQueue q = s.InteractionQueue;
            if (q == null || q.Count > 1) return;  // called before the list removal: 1 = empty after
            if (sNpGaps.Count >= 1024) sNpGaps.Clear();  // bounded; a cleared Sim just misses one pass
            NpGap g = new NpGap();
            g.Ms0 = System.Environment.TickCount; g.T0 = NpNow(); g.Hh = hh;
            sNpGaps[who] = g;
        }

        static void NpSearch(Autonomy a, int t0, bool end)
        {
            Sim s = a.Actor as Sim;
            NpGap g;
            if (s == null || !sNpGaps.TryGetValue(s.ObjectId.Value, out g)) return;
            if (!end) { if (g.SearchAt < 0) g.SearchAt = t0; return; }
            if (g.SearchAt == t0) { g.SearchMs = System.Environment.TickCount - t0; PatchCounters()[kNpSearches]++; }
        }

        static void NpError(Exception e)
        {
            int n = PatchCounters()[kNpErrors]++;
            sNpLastError = "nextplan: error: " + e.GetType().Name + ": " + e.Message;
            if (n < 3) DiagLine(sNpLastError);
        }

        public class NpIdleHook
        {
            public void IdleHook()
            {
                bool queued = false;
                try { if (sNpGaps.Count != 0) queued = NpPass((Sim)(object)this); }
                catch (ResetException) { throw; }
                catch (Exception e) { NpError(e); }
                if (!queued) IdleOrig();  // with a head, EA's loop never idles either
            }
            public void IdleOrig() { }  // receives Sim.LoopIdle's body (or the hook stacked before ours)
        }

        public abstract class NpPickHook : GameObject
        {
            public float PickHook(Sim actor)
            {
                float m = PickOrig(actor);
                try { return sNp.Count == 0 ? m : NpPick(this, actor, m, GetRoutePendingAutonomyMultiplier(), kAutonomyMultiplierForObjectSelectionWhenInUse); }
                catch (ResetException) { throw; }
                catch (Exception e) { NpError(e); return m; }
            }
            public float PickOrig(Sim actor) { return 1f; }  // receives GameObject.GetInUseAutonomyMultiplier (or the hook before ours)
        }

        public abstract class NpInstHook : InteractionInstance
        {
            public void AddedHook()
            {
                AddedOrig();
                try { NpAdded(this); }
                catch (ResetException) { throw; }
                catch (Exception e) { NpError(e); }
            }
            public void AddedOrig() { }
            public void RemovedHook(bool isHeadInteraction)
            {
                try { NpRemoved(this, isHeadInteraction); }
                catch (ResetException) { throw; }
                catch (Exception e) { NpError(e); }
                RemovedOrig(isHeadInteraction);
            }
            public void RemovedOrig(bool isHeadInteraction) { }
        }

        public class NpAutHook
        {
            public bool RunHook(bool addToQueue)
            {
                Autonomy a = (Autonomy)(object)this;
                int t0 = System.Environment.TickCount;
                if (sNpGaps.Count != 0) { try { NpSearch(a, t0, false); } catch (ResetException) { throw; } catch (Exception e) { NpError(e); } }
                bool r = RunOrig(addToQueue);
                if (sNpGaps.Count != 0) { try { NpSearch(a, t0, true); } catch (ResetException) { throw; } catch (Exception e) { NpError(e); } }
                return r;
            }
            public bool RunOrig(bool addToQueue) { return false; }  // receives Autonomy.RunAutonomy(bool)
        }

        // ---- check ----

        static string NpHistText(int scope)
        {
            NextPlanCore.Hist[] h = sNpHist[scope];
            return "pick gap ms " + h[0].Text() + "; sim-min x10 " + h[1].Text() + "; wait before the search ms (EA at once) " + h[2].Text()
                + "; (EA's delay class) " + h[3].Text() + " sim-min x10 " + h[4].Text() + "; search ms " + h[5].Text();
        }

        static string NpHousehold()
        {
            Sim act = Sim.ActiveActor;
            if (act == null || act.Household == null) return "no active household";
            float sum = 0f, min = 100f; int n = 0;
            CommodityKind[] kinds = { CommodityKind.Hunger, CommodityKind.Bladder, CommodityKind.Energy, CommodityKind.Social, CommodityKind.Fun, CommodityKind.Hygiene };
            foreach (Sim s in act.Household.Sims)
            {
                if (s == null || s.Motives == null) continue;
                foreach (CommodityKind k in kinds)
                {
                    Motive m = s.Motives.GetMotive(k);
                    if (m == null) continue;
                    sum += m.Value; n++; if (m.Value < min) min = m.Value;
                }
            }
            float hours = sNpStart == 0 ? 0f : (SimClock.CurrentTicks - sNpStart) / (float)(sNpTicksPerMin * 60);
            int done = PatchCounters()[kNpHhDone] - sNpResetDone;
            string lv = "";
            NpLot hl;
            if (act.LotHome != null && sNpLots.TryGetValue(act.LotHome.LotId, out hl)) lv = "home contention " + NextPlanCore.LevelNames[hl.Level] + " (ratio " + hl.Ratio.ToString("0.0") + ", " + hl.Name + "); ";
            lv += "household same-target picks: claimed by another Sim " + sNpSameHh[0] + ", in use by another Sim " + sNpSameHh[1] + "; claims by level low/mid/high " + sNpLevelClaims[0] + "/" + sNpLevelClaims[1] + "/" + sNpLevelClaims[2] + ", scored down " + sNpLevelDown[0] + "/" + sNpLevelDown[1] + "/" + sNpLevelDown[2] + "; ";
            return lv + "household motives mean " + (n > 0 ? (sum / n).ToString("0.0") : "?") + " min " + min.ToString("0") + " (" + n + " values); household autonomous actions done "
                + done + " in " + hours.ToString("0.00") + " sim-h (" + (hours > 0.01f ? (done / hours).ToString("0.0") : "?") + " per sim-h)";
        }

        static string NpCounters()
        {
            int[] c = PatchCounters();
            return "nextplan: passes " + c[kNpPass] + " (queued " + c[kNpQueued] + ", of them EA's delay skipped " + c[kNpDelaySkip] + "; nothing picked " + c[kNpNothing]
                + "; dropped by a higher-priority add " + c[kNpDropped] + "); off: would " + c[kNpWould] + "; kept EA's delay " + c[kNpKeptDelay] + ", NPC budget " + c[kNpBudget]
                + ", other skips " + c[kNpSkipped] + "; claims " + c[kNpClaims] + " (open " + sNp.Count + ", expired " + c[kNpExpired] + ", table full " + sNp.Full
                + "), claimed objects scored down " + c[kNpClaimLow] + " (would " + c[kNpClaimWould] + "); same-target picks: claimed by another Sim " + c[kNpSameClaimed]
                + ", in use by another Sim " + c[kNpSameInUse] + "; picks measured " + c[kNpPicks] + ", searches timed " + c[kNpSearches] + "; lines " + c[kNpLines]
                + "; errors " + c[kNpErrors] + (sNpLastError.Length > 0 ? " (last: " + sNpLastError + ")" : "") + "; " + GlMembers(sNpMissing)
                + (sNpLast.Length > 0 ? "; last: " + sNpLast : "");
        }

        // NextPlanCheck run: the rules with controls. result: counters + gap histograms + household needs. reset: histograms.
        public static object NextPlanCheck(string what)
        {
            if (what == "result")
                return NpCounters() + "\nhousehold: " + NpHistText(0) + "\nnpc: " + NpHistText(1) + "\n" + NpHousehold();
            if (what == "reset")
            {
                foreach (NextPlanCore.Hist[] hs in sNpHist) foreach (NextPlanCore.Hist h in hs) h.Reset();
                sNpStart = 0; sNpSameHh[0] = sNpSameHh[1] = 0; sNpResetDone = PatchCounters()[kNpHhDone];  // sim-hours and the per-hour rate from here
                return "nextplan: histograms reset";
            }
            if (what != "run") return "nextplan <run|result|reset>";
            bool on = ShortcutOn("nextplan") && ShortcutOn("nextplanhh");
            bool fixedNow = on && NextPlanCore.Decide(true, true, true, false, false, true, true, 0) == NextPlanCore.kGo
                && NextPlanCore.Decide(true, true, true, true, false, false, true, 1) == NextPlanCore.kGo;
            NextPlanCore k = new NextPlanCore();
            k.Add(7, 1, 100, 50);
            bool controls = NextPlanCore.Decide(true, true, true, false, true, true, true, 9) == NextPlanCore.kActiveDelay
                && NextPlanCore.Decide(true, true, true, false, false, false, true, 9) == NextPlanCore.kActiveDelay
                && NextPlanCore.Decide(true, false, true, true, false, true, true, 9) == NextPlanCore.kNotIdle
                && NextPlanCore.Decide(true, true, true, true, false, false, true, 0) == NextPlanCore.kBudget
                && k.HeldByOther(7, 2, 120) && !k.HeldByOther(7, 1, 120) && !k.HeldByOther(7, 2, 150);
            return "nextplan check (switches " + (on ? "on" : "off") + "): a household Sim's next pick runs at once, an NPC's within the budget=" + fixedNow
                + "; control: the active Sim's delay, NPC delay, not idle, budget, claim holder / expiry=" + controls + "; verdict: "
                + GlVerdict(on, fixedNow, controls, sNpMissing.Length == 0) + "; " + NpCounters();
        }
    }
}
