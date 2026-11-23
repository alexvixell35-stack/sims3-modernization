using System;
using System.Collections.Generic;
using Sims3.Gameplay;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.Objects.Vehicles;
using Sims3.Gameplay.Socializing;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;
using Sims3.SimIFace.CAS;

namespace Ts3Fixes
{
    // actdelay (docs/684): read-only probe of the time between "an interaction is in a Sim's queue" and "the Sim visibly
    // acts". Switch actdelay (tuning kActDelay, default off). The hooks are pass-through and always in; while the switch
    // is off each costs one static bool test. Nothing here changes what the game does: every hook calls the original
    // with the caller's arguments, and an exception of ours is counted and swallowed.
    //
    // Hook points [verified, ilspycmd on the 1.67 Sims3GameplaySystems / SimIFace / ScriptCore]:
    //   InteractionInstance.OnAddedToQueue       called by InteractionQueue.InsertInteraction (the one funnel of Add / AddNext /
    //                                            PushAsContinuation) right after the list insert          -> queued
    //   InteractionInstance.OnRemovedFromQueue   called by InteractionQueue.RemoveInteraction(int, bool, bool), the one
    //                                            removal funnel (DeQueue, cancels), before the list removal -> removed / next head
    //   InteractionQueue.PushRunningInteraction  first statement of RunInteractionWithoutCleanup's try for every non-immediate
    //                                            interaction, just before Run(); also the posture transitions that
    //                                            ProcessOneInteraction runs before the head                -> wake, Run begins
    //   InteractionQueue.PopRunningInteraction   the finally of the same method                            -> Run ended
    //   ScriptCore.Route.Plan / Replan / PlanFromPoint / ReplanFromPoint  our existing hooks (RouteBackoff.cs): begin, end
    //                                            and result; RouteBackoffHook.AsyncWait gives the time the task slept on a
    //                                            ts3native route job                                       -> plan-cpu / plan-job
    //   SimRoutingComponent.OnAboutToStartFollowingRoute  called by RoutingComponent.DoRoute once the plan succeeded, before
    //                                            the route actions are pushed                              -> the Sim moves
    //   StateMachineClient.SetActor              every animation state machine names its actors first      -> animation set-up
    //   GlobalFunctions.FindGoodLocationNearby   stacked on RrNearbyHook                                   -> placement searches
    //   Sim.SwitchOutfitHelper.Wait              the one wait for the native outfit build (LoopIdle, then one-tick sleeps until
    //                                            the swap calls back); stacked on OutfitWaitHook            -> outfit waits
    //   InteractionInstance.StartSync (5 arguments, the funnel of its overloads), and from SocialReceiveFail.cs's hooks (the
    //   only swaps of those methods, first statements) Sim.WaitForSynchronizationLevelWithSim (7 arguments, the funnel) and
    //   SocialInteraction.ReceiveSocialInteraction (the receiver waits for the starter's first route)   -> sync waits
    //   Wait and StartSync always hold a sleeping frame (that is what they measure); each has a stub like every swap (docs/242).
    // Push / Pop / FindGoodLocationNearby have no path to a yield; the other four have one only in the conservative call graph
    // (docs/data/swap-stack-audit.csv) and, like every swap, a stub for the saved-stack remap (docs/242). The plan hooks
    // were there before. A Sim that rides a vehicle routes through the vehicle's component: no "moves" stamp for it.
    //
    // Counters (docs/registry.md): [4270] instances that acted, [4271] of them over kActDelayLogMs, [4272] ended without
    // acting, [4273] left the queue before Run, [4274] dropped (table full or stale), [4275] errors, [4276] lines written,
    // [4277] plans inside records, [4278] summed queued -> act ms, [4279] longest queued -> act ms.
    // docs/684 §8: [4280] outfit waits that slept, [4281] their summed ms, [4282] the longest ms, [4283] of them household or
    // selected Sims, [4284] instances with a sync wait, [4285] their summed sync ms, [4286] instances whose Run began in a
    // vehicle, [4287] inside a rabbit hole, [4288] outfit waits over kActDelayOutfitLogMs whose Sim was on screen at the end,
    // [4289] outfit waits over kActDelayOutfitLogMs.
    public static partial class Fixes
    {
        const int kAdActed = 4270, kAdOver = 4271, kAdNoAct = 4272, kAdCancelled = 4273, kAdDropped = 4274, kAdErrors = 4275, kAdLines = 4276,
            kAdPlans = 4277, kAdSumMs = 4278, kAdMaxMs = 4279, kAdOutfitN = 4280, kAdOutfitMs = 4281, kAdOutfitMax = 4282, kAdOutfitPlayer = 4283,
            kAdSyncN = 4284, kAdSyncMs = 4285, kAdRide = 4286, kAdHidden = 4287, kAdOutfitSeen = 4288, kAdOutfitOver = 4289;

        static bool sActOn;
        // entries per hook since the start, counted with the switch off too (ActDelayCore.HookNames order): a dead hook shows at once
        static readonly int[] sActIn = new int[ActDelayCore.kHooks];
        static int sActPoll, sActErrors, sActLines, sActLinesCut, sActInMinute;
        static uint sActLastUs;
        static long sActNow = 1, sActMinuteAt;
        static ActDelayCore sAct;
        static readonly Dictionary<string, string> sActSocial = new Dictionary<string, string>();
        static readonly ActDelayCore.Namer sActNamer = ActTypeName;

        static void ApplyActDelay()
        {
            Patch(typeof(InteractionInstance), "OnAddedToQueue", Type.EmptyTypes, typeof(AdInstHook), "AddedHook", "AddedOrig");
            Patch(typeof(InteractionInstance), "OnRemovedFromQueue", new Type[] { typeof(bool) }, typeof(AdInstHook), "RemovedHook", "RemovedOrig");
            Patch(typeof(InteractionQueue), "PushRunningInteraction", new Type[] { typeof(InteractionInstance) }, typeof(AdQueueHook), "PushHook", "PushOrig");
            Patch(typeof(InteractionQueue), "PopRunningInteraction", new Type[] { typeof(InteractionInstance) }, typeof(AdQueueHook), "PopHook", "PopOrig");
            Patch(typeof(SimRoutingComponent), "OnAboutToStartFollowingRoute", new Type[] { typeof(Route) }, typeof(AdRouteHook), "FollowHook", "FollowOrig");
            Patch(typeof(StateMachineClient), "SetActor", new Type[] { typeof(string), typeof(IHasScriptProxy) }, typeof(AdSmcHook), "SetActorHook", "SetActorOrig");
            Patch(typeof(GlobalFunctions), "FindGoodLocationNearby", sFibNearbyArgs, typeof(AdFglHook), "FglHook", "FglOrig");
            Patch(typeof(Sim.SwitchOutfitHelper), "Wait", new Type[] { typeof(bool) }, typeof(AdOutfitHook), "WaitHook", "WaitOrig");
            Patch(typeof(InteractionInstance), "StartSync", new Type[] { typeof(bool), typeof(bool), typeof(SyncLoopCallbackFunction), typeof(float), typeof(bool) }, typeof(AdStartSyncHook), "StartSyncHook", "StartSyncOrig");
        }

        // world load: instances and Sim ids belong to the world just left; the totals stay
        public static void ActDelayWorldLoad() { if (sAct != null) sAct.ClearOpen(); }

        // The cached switch, re-read every 256 hook calls (the switches can arrive after the hooks went in).
        static bool ActOn()
        {
            if ((++sActPoll & 255) != 1) return sActOn;
            bool on = ShortcutOn("actdelay");
            if (on != sActOn)
            {
                if (on && sAct == null) sAct = new ActDelayCore();
                if (sAct != null) sAct.ClearOpen();
                sActOn = on;
            }
            if (on) sAct.LogMs = Math.Max(1, Instantiator.kActDelayLogMs);
            return sActOn;
        }

        // monotonic microseconds (SrcNowUs wraps after ~71 min; the probe calls it far more often)
        static long ActNow()
        {
            uint u = SrcNowUs();
            sActNow += unchecked(u - sActLastUs);
            sActLastUs = u;
            return sActNow;
        }

        static void ActError() { sActErrors++; PatchCounters()[kAdErrors]++; }

        static int ActKind(Sim s)
        {
            try
            {
                if (s == null || s.HasBeenDestroyed) return ActDelayCore.kHousehold;   // unknown counts as household
                if (s == PlumbBob.SelectedActor) return ActDelayCore.kSelected;
                return s.IsSelectable ? ActDelayCore.kHousehold : ActDelayCore.kNpc;
            }
            catch (ResetException) { throw; }
            catch (Exception) { return ActDelayCore.kHousehold; }
        }

        // The aggregation key: the instance's class; socials (one class for all) by their action key.
        static object ActTypeKey(InteractionInstance ii)
        {
            SocialInteractionA.Definition def = ii.InteractionDefinition as SocialInteractionA.Definition;
            if (def == null || def.ActionKey == null) return ii.GetType();
            string k;
            if (!sActSocial.TryGetValue(def.ActionKey, out k))
            {
                if (sActSocial.Count >= 2048) sActSocial.Clear();
                k = ii.GetType().Name + ":" + def.ActionKey;
                sActSocial[def.ActionKey] = k;
            }
            return k;
        }

        static string ActTypeName(object key)
        {
            Type t = key as Type;
            if (t == null) return key != null ? key.ToString() : "?";
            string n = t.FullName ?? t.Name;
            return n.StartsWith("Sims3.Gameplay.") ? n.Substring(15) : n;
        }

        static void ActQueued(InteractionInstance ii)
        {
            try
            {
                Sim sim = ii.InstanceActor;
                if (sim == null) return;
                InteractionQueue q = sim.InteractionQueue;
                if (q == null) return;
                bool head = q.GetHeadInteraction() == ii;
                sAct.Queued(ii, ActTypeKey(ii), sim.ObjectId.Value, ActKind(sim), ii.Autonomous, head, head && q.Count == 1, ActNow(), SimClock.CurrentTicks);
                ActTick();
            }
            catch (ResetException) { throw; }
            catch (Exception) { ActError(); }
        }

        static void ActRemoved(InteractionInstance ii, bool isHead)
        {
            try
            {
                Sim sim = ii.InstanceActor;
                if (sim == null) return;
                InteractionQueue q = sim.InteractionQueue;
                ActDone(sAct.Removed(ii, sim.ObjectId.Value, isHead, q == null || q.Count <= 1, ActNow(), SimClock.CurrentTicks));
            }
            catch (ResetException) { throw; }
            catch (Exception) { ActError(); }
        }

        static void ActRun(InteractionQueue q, InteractionInstance ii, bool push)
        {
            try
            {
                if (ii == null) return;
                Sim sim = ii.InstanceActor;
                if (sim == null) return;
                bool transition = q.TransitionInteraction == ii;
                if (push)
                {
                    InteractionInstance head = q.GetHeadInteraction();
                    int away = ActDelayCore.kAwayNone;
                    if (head == ii)
                    {
                        // riding (the carpool of GoToSchool / GoHome, docs/684 §8) or inside a rabbit hole: not a Sim standing idle
                        Posture p = sim.Posture;
                        if (p is SittingInVehicle) away = ActDelayCore.kAwayRide;
                        else if (p is InRabbitHolePosture) away = ActDelayCore.kAwayHidden;
                    }
                    ActDone(sAct.Push(ii, head, sim.ObjectId.Value, transition, ActNow(), SimClock.CurrentTicks, ii.GetType(), away));
                }
                else ActDone(sAct.Pop(ii, sim.ObjectId.Value, transition, ActNow(), SimClock.CurrentTicks));
                ActTick();
            }
            catch (ResetException) { throw; }
            catch (Exception) { ActError(); }
        }

        // RouteBackoff.cs, the four plan hooks: a plan begins / ended in the running task
        internal static void ActPlanBegin()
        {
            sActIn[ActDelayCore.kInPlanBegin]++;
            if (!sActOn) return;
            try { sAct.PlanBegin(Simulator.CurrentTask.Value, ActNow(), SimClock.CurrentTicks); }
            catch (ResetException) { throw; }
            catch (Exception) { ActError(); }
        }
        internal static void ActPlanEnd(bool ok)
        {
            sActIn[ActDelayCore.kInPlanEnd]++;
            if (!sActOn) return;
            try { sAct.PlanEnd(Simulator.CurrentTask.Value, ok, ActNow()); }
            catch (ResetException) { throw; }
            catch (Exception) { ActError(); }
        }
        // RouteBackoffHook.AsyncWait: the running task slept from t0 (SrcNowUs) until its route job was ready
        internal static void ActJobWaited(uint t0)
        {
            sActIn[ActDelayCore.kInJobWait]++;
            if (!sActOn) return;
            try { sAct.JobWaited(Simulator.CurrentTask.Value, unchecked(SrcNowUs() - t0)); }
            catch (ResetException) { throw; }
            catch (Exception) { ActError(); }
        }

        static void ActMove(Sim sim)
        {
            try { if (sim != null) ActDone(sAct.Move(sim.ObjectId.Value, ActNow(), SimClock.CurrentTicks)); }
            catch (ResetException) { throw; }
            catch (Exception) { ActError(); }
        }

        static void ActAnim(IHasScriptProxy actor)
        {
            try
            {
                Sim sim = actor as Sim;
                if (sim != null) sAct.Anim(sim.ObjectId.Value, ActNow(), SimClock.CurrentTicks);
            }
            catch (ResetException) { throw; }
            catch (Exception) { ActError(); }
        }

        // The Sim of helper left SwitchOutfitHelper.Wait, entered at t0 (ActNow). Runs in a finally: nothing here may throw.
        static void ActOutfitWaited(object helper, long t0)
        {
            try
            {
                long us = ActNow() - t0;
                Sim sim = sSohSim != null ? sSohSim.GetValue(helper) as Sim : null;
                if (sim == null || us < 1000) return;   // under a millisecond: the outfit was ready, the task did not sleep
                int kind = ActKind(sim);
                sAct.OutfitWait(sim.ObjectId.Value, kind, us);
                int ms = (int)Math.Min(int.MaxValue, us / 1000);
                int[] c = PatchCounters();
                c[kAdOutfitN]++;
                c[kAdOutfitMs] = unchecked(c[kAdOutfitMs] + ms);
                if (ms > c[kAdOutfitMax]) c[kAdOutfitMax] = ms;
                if (kind != ActDelayCore.kNpc) c[kAdOutfitPlayer]++;
                if (ms < Instantiator.kActDelayOutfitLogMs) return;
                c[kAdOutfitOver]++;
                string view = sim.HasBeenDestroyed ? "Sim gone" : ViewText(sim, kAdOutfitSeen);
                if (sActInMinute >= Instantiator.kActDelayLinesPerMin) { sActLinesCut++; return; }
                sActInMinute++;
                InteractionInstance cur = sim.HasBeenDestroyed ? null : sim.CurrentInteraction;
                string line = "actdelay outfit: " + ms + " ms in SwitchOutfitHelper.Wait, " + ActDelayCore.KindNames[kind] + ", to "
                    + (sSohCategory != null ? sSohCategory.GetValue(helper) : "?") + ", " + view + ", in "
                    + (cur != null ? ActTypeName(ActTypeKey(cur)) : "no interaction") + ", " + HcSimTime() + ", Sim " + (sim.HasBeenDestroyed ? "?" : sim.FullName);
                sActLines++; c[kAdLines]++;
                DiagLine(line);
            }
            catch (Exception) { ActError(); }
        }

        // Entry of a sync wait hook (SocialReceiveFail.cs's two, which run first of their methods' swaps): counts the entry and,
        // with the probe on, opens the wait; true = the caller closes it with ActSync(sim, false) in a finally.
        internal static bool ActSyncIn(Sim sim, int hook)
        {
            sActIn[hook]++;
            if (!sActOn) return false;
            ActSync(sim, true);
            return true;
        }

        // A sync wait of the Sim begins / ends. The end runs in a finally: nothing here may throw.
        static void ActSync(Sim sim, bool begin)
        {
            try
            {
                if (sim == null || sAct == null) return;
                if (begin) sAct.SyncBegin(sim.ObjectId.Value, ActNow());
                else sAct.SyncEnd(sim.ObjectId.Value, ActNow());
            }
            catch (Exception) { ActError(); }
        }

        // A finished instance: counters, and its line when over the threshold (at most kActDelayLinesPerMin a minute).
        static void ActDone(ActDelayCore.Result r)
        {
            if (r == null) return;
            int[] c = PatchCounters();
            if (r.NoAct) c[kAdNoAct]++;
            else
            {
                int ms = r.Ms(ActDelayCore.kTotal);
                c[kAdActed]++;
                c[kAdSumMs] = unchecked(c[kAdSumMs] + ms);
                if (ms > c[kAdMaxMs]) c[kAdMaxMs] = ms;
                if (r.Over) c[kAdOver]++;
            }
            c[kAdPlans] = sAct.PlansSeen;
            c[kAdCancelled] = sAct.Cancelled;
            c[kAdDropped] = sAct.Dropped + sAct.Stale;
            c[kAdSyncN] = sAct.SyncWaits;
            c[kAdSyncMs] = (int)Math.Min(int.MaxValue, sAct.SyncSumMs);
            c[kAdRide] = sAct.Rides;
            c[kAdHidden] = sAct.Hidden;
            if (!r.Over) return;
            if (sActInMinute >= Instantiator.kActDelayLinesPerMin) { sActLinesCut++; return; }
            sActInMinute++;
            string name = null;
            InteractionInstance ii = r.Key as InteractionInstance;
            try { if (ii != null && ii.InstanceActor != null) name = ii.InstanceActor.FullName; }
            catch (ResetException) { throw; }
            catch (Exception) { }
            string line = ActDelayCore.Line(r, ActTypeName(r.Type), name, HcSimTime(), sActNamer);
            sAct.RingAdd(line);
            sActLines++; c[kAdLines]++;
            DiagLine(line);
        }

        // Once a minute of real time, from whatever hook runs next: the sweep and the summary line.
        static void ActTick()
        {
            long now = sActNow;
            if (now - sActMinuteAt < 60000000L) return;
            sActMinuteAt = now;
            ActDelayCore.Result r;
            while ((r = sAct.SweepNext(now)) != null) ActDone(r);
            sActInMinute = 0;
            string dead = ActDelayCore.HookHealth(sActIn);
            if (dead.Length > 0) { PatchCounters()[kAdLines]++; DiagLine("actdelay: " + dead + "; " + ActDelayCore.HookLine(sActIn)); }
            string line = sAct.MinuteLine(sActNamer);
            if (line == null) return;
            if (sActLinesCut > 0) { line += ", " + sActLinesCut + " lines over the cap not written"; sActLinesCut = 0; }
            PatchCounters()[kAdLines]++;
            DiagLine(line);
        }

        public abstract class AdInstHook : InteractionInstance
        {
            public void AddedHook()
            {
                AddedOrig();
                sActIn[ActDelayCore.kInQueued]++;
                if (ActOn()) ActQueued(this);
            }
            public void AddedOrig() { }   // receives InteractionInstance.OnAddedToQueue's body (or the hook stacked before ours)
            public void RemovedHook(bool isHeadInteraction)
            {
                sActIn[ActDelayCore.kInRemoved]++;
                if (ActOn()) ActRemoved(this, isHeadInteraction);
                RemovedOrig(isHeadInteraction);
            }
            public void RemovedOrig(bool isHeadInteraction) { }
        }

        public class AdQueueHook : InteractionQueue
        {
            public void PushHook(InteractionInstance ii)
            {
                PushOrig(ii);
                sActIn[ActDelayCore.kInPush]++;
                if (ActOn()) ActRun(this, ii, true);
            }
            public void PushOrig(InteractionInstance ii) { }
            public void PopHook(InteractionInstance ii)
            {
                sActIn[ActDelayCore.kInPop]++;
                if (ActOn()) ActRun(this, ii, false);
                PopOrig(ii);
            }
            public void PopOrig(InteractionInstance ii) { }
        }

        public class AdRouteHook : SimRoutingComponent
        {
            public void FollowHook(Route r)
            {
                sActIn[ActDelayCore.kInMove]++;
                if (sActOn) ActMove(OwnerSim);
                FollowOrig(r);
            }
            public void FollowOrig(Route r) { }
        }

        public class AdSmcHook
        {
            public void SetActorHook(string actorName, IHasScriptProxy actor)
            {
                sActIn[ActDelayCore.kInSetActor]++;
                if (sActOn && actor != null) ActAnim(actor);
                SetActorOrig(actorName, actor);
            }
            public void SetActorOrig(string actorName, IHasScriptProxy actor) { }
        }

        public class AdFglHook
        {
            public static bool FglHook(IGameObject obj, ObjectGuid[] objIdsToIgnore, ref Vector3 position, ref Vector3 forward, float offset,
                GlobalFunctions.FindGoodLocationStrategies searches, FindGoodLocationBooleans fglBools)
            {
                sActIn[ActDelayCore.kInFgl]++;
                if (!sActOn) return FglOrig(obj, objIdsToIgnore, ref position, ref forward, offset, searches, fglBools);
                uint t0 = SrcNowUs();
                bool found = FglOrig(obj, objIdsToIgnore, ref position, ref forward, offset, searches, fglBools);
                try { sAct.Fgl(Simulator.CurrentTask.Value, unchecked(SrcNowUs() - t0)); }
                catch (ResetException) { throw; }
                catch (Exception) { ActError(); }
                return found;
            }
            public static bool FglOrig(IGameObject obj, ObjectGuid[] objIdsToIgnore, ref Vector3 position, ref Vector3 forward, float offset,
                GlobalFunctions.FindGoodLocationStrategies searches, FindGoodLocationBooleans fglBools) { return false; }
        }

        // The trigger: EA's Sim.SwitchToOutfit interaction (its Run is one SwitchToOutfitWithSpin), queued as the player would.
        static string ActOutfitPush()
        {
            Household h = Household.ActiveHousehold;
            if (h == null) return "actdelay outfit: no active household";
            int n = 0;
            string names = "";
            foreach (Sim s in new List<Sim>(h.Sims))
            {
                if (s == null || s.HasBeenDestroyed || s.InteractionQueue == null || !s.IsHuman || !s.SimDescription.ChildOrAbove) continue;
                Sim.SwitchToOutfit ii = Sim.SwitchToOutfit.Singleton.CreateInstance(s, s, new InteractionPriority(InteractionPriorityLevel.UserDirected), false, true) as Sim.SwitchToOutfit;
                if (ii == null) continue;
                ii.Reason = Sim.ClothesChangeReason.Force;
                ii.Outfit = s.CurrentOutfitCategory == OutfitCategories.Everyday ? OutfitCategories.Formalwear : OutfitCategories.Everyday;
                if (!s.InteractionQueue.Add(ii)) continue;
                n++;
                names += (names.Length > 0 ? ", " : "") + s.FullName + " -> " + ii.Outfit;
            }
            return "actdelay outfit: Switch To Outfit queued for " + n + " household Sims (" + names + "); then read the 'actdelay outfit:' lines, "
                + "ActDelayCheck result and ts3native's 'outfitfirst:' lines";
        }

        // The two hooks below keep a frame on the task's stack while it sleeps: the time inside the original is what they measure.
        public class AdOutfitHook
        {
            public void WaitHook(bool idleIfNecessary)   // `this` is really a Sim.SwitchOutfitHelper
            {
                sActIn[ActDelayCore.kInOutfit]++;
                if (!sActOn) { WaitOrig(idleIfNecessary); return; }
                long t0 = ActNow();
                try { WaitOrig(idleIfNecessary); }
                finally { ActOutfitWaited(this, t0); }
            }
            public void WaitOrig(bool idleIfNecessary) { }   // receives OutfitWaitHook.WaitHook (EaFixes.cs), which runs EA's Wait
        }

        public abstract class AdStartSyncHook : InteractionInstance
        {
            public bool StartSyncHook(bool shouldBeMaster, bool ignoreExitReasons, SyncLoopCallbackFunction loopCallback, float notifySimMinutes, bool performSocializeWithTest)
            {
                sActIn[ActDelayCore.kInStartSync]++;
                if (!sActOn) return StartSyncOrig(shouldBeMaster, ignoreExitReasons, loopCallback, notifySimMinutes, performSocializeWithTest);
                Sim me = InstanceActor;
                ActSync(me, true);
                try { return StartSyncOrig(shouldBeMaster, ignoreExitReasons, loopCallback, notifySimMinutes, performSocializeWithTest); }
                finally { ActSync(me, false); }
            }
            public bool StartSyncOrig(bool shouldBeMaster, bool ignoreExitReasons, SyncLoopCallbackFunction loopCallback, float notifySimMinutes, bool performSocializeWithTest) { return false; }
        }

        // ActDelayCheck("result"): the aggregate since the switch went on (or the last "reset"): p50 / p90 / max ms per
        //   bucket and Sim kind, the top 15 interaction types by summed queued -> act ms, the last lines over the threshold.
        // ActDelayCheck("run"): the bucket logic on a fixed scenario, in the game's own Mono (no Sim needed).
        // ActDelayCheck("live"): the hooks' swaps.  ActDelayCheck("reset"): totals back to zero.
        // ActDelayCheck("outfit"): every Sim of the active household gets EA's own "Switch To Outfit" (Everyday <-> Formalwear):
        //   several live outfit swaps at once, the trigger for the outfit stamp and ts3native [OutfitFirst] (docs/684 §9).
        public static object ActDelayCheck(string what)
        {
            try
            {
                if (what == "run")
                {
                    string bad = ActDelayCore.Model();
                    return "actdelay model: " + (bad.Length == 0 ? "verdict: ok (8 buckets add up to queued -> act)" : "verdict: BROKEN (" + bad + ")");
                }
                if (what == "live")
                {
                    string d = ActDelayCore.HookHealth(sActIn);
                    return "actdelay live: switch " + (ShortcutOn("actdelay") ? "on" : "off") + ", " + GlMembers(CkLive("actdelay")) + "; " + ActDelayCore.HookLine(sActIn)
                        + "; verdict: " + (d.Length == 0 ? "ok" : d);
                }
                if (what == "reset") { if (sAct != null) sAct.Reset(); return "actdelay: totals cleared"; }
                if (what == "outfit") return ActOutfitPush();
                if (what != "result") return "actdelay <result|run|live|reset|outfit>";
                string head = "actdelay switch " + (ShortcutOn("actdelay") ? "on" : "off") + ", threshold " + Instantiator.kActDelayLogMs + " ms, lines " + sActLines
                    + ", errors " + kAdErrors + "=" + sActErrors + ", " + GlMembers(CkLive("actdelay")) + "\n"
                    + ActDelayCore.HookLine(sActIn) + "\n";
                string dead = ActDelayCore.HookHealth(sActIn);
                if (dead.Length > 0) head += "actdelay: " + dead + "\n";
                if (sAct == null) return head + "actdelay: no data (the switch was never on)";
                ActDelayCore.Result r;
                long now = ActNow();
                while ((r = sAct.SweepNext(now)) != null) ActDone(r);
                return head + sAct.Report(sActNamer, 15);
            }
            catch (ResetException) { throw; }
            catch (Exception e) { return "actdelay check threw " + e.GetType().Name + " " + e.Message; }
        }
    }
}
