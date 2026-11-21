using System;
using System.Collections.Generic;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // Rabbit hole "portal locked" route fails and rabbit hole entry for Sims nobody watches (docs/627). All default off.
    public static partial class Fixes
    {
        // ---- docs/627 §2 routefailprobe -----------------------------------------------------------------------------------
        // [verified, decompiled + logs] The "routefail: <selected Sim> ... WorkInRabbitHole target DaySpa, PlanFromPoint sync
        // FailedBecauseDestinationPortalLocked" lines are not the Sim's own route. EA's rabbit hole entry plans with
        // Route.PlanToSlot (RabbitHole.RouteNearEntranceAndEnterRabbitHole IL_02bf, a "Plan" in our log). The failed
        // PlanFromPoint is Sim.IsPointInLotSafelyRoutable (spot -> lot corners) run on PlumbBob.SelectedActor /
        // Sim.ActiveActor as a stand-in for an NPC being placed (SimDescription.Instantiate(Lot, ResourceKey),
        // Service.CreateSim, Service.GetPositionNearLotCorners), with the selected Sim's door rights. RouteFailLog reads the
        // Sim from the route's follower and its CurrentInteraction, so the probe is written as that Sim's failure, counted in
        // [866] and as a failed "gohere" attempt. The placement itself is residentspawnslot / residentspawnwalk (docs/582).
        // Fix: a PlanFromPoint inside IsPointInLotSafelyRoutable whose start point is not where the follower stands is
        // logged as "spot probe" and kept out of the Sim's route counts. Logging only; no game state changes.
        // Counters: [3580] probe plans tagged (switch on), [3581] probe plans counted as the Sim's route (switch off),
        // [3582] errors, [3583] spare.
        const float kRfpSelfM = 1.5f;  // a check of the Sim's own spot (IsInSafeLocation, AttemptToFindSafeLocation) starts here
        static readonly Dictionary<ulong, Vector3> sRfpPos = new Dictionary<ulong, Vector3>();

        // From FewerPlans' IsPointInLotSafelyRoutable hook: the spot being checked, per sim task (the check can sleep on an
        // async job with routableasync; another task's plan must not read it).
        internal static bool RfpEnter(Vector3 pos, out Vector3 prev)
        {
            ulong task = Simulator.CurrentTask.Value;
            bool had = sRfpPos.TryGetValue(task, out prev);
            if (sRfpPos.Count >= 256) sRfpPos.Clear();
            sRfpPos[task] = pos;
            return had;
        }
        internal static void RfpLeave(bool had, Vector3 prev)
        {
            ulong task = Simulator.CurrentTask.Value;
            if (had) sRfpPos[task] = prev; else sRfpPos.Remove(task);
        }

        // Pure: is this plan a spot probe, not the follower's own route? kind 2 = PlanFromPoint.
        internal static bool RfpIsProbe(int kind, bool inCheck, float distFromSim)
        {
            return kind == 2 && inCheck && distFromSim > kRfpSelfM;
        }
        // Pure: tag it (keep it out of the Sim's counts)?
        internal static bool RfpTag(bool on, bool probe) { return on && probe; }

        // Wiring from RouteFailLog.RflPlan (failed plans only): true = written as a probe, the caller stops there.
        static bool RfpPlan(Sim sim, int kind, RoutePlanResult res, ScriptCore.Route r, uint t0)
        {
            try
            {
                if (kind != 2 || sRfpPos.Count == 0) return false;
                Vector3 spot;
                if (!sRfpPos.TryGetValue(Simulator.CurrentTask.Value, out spot)) return false;
                if (!RfpIsProbe(kind, true, (sim.Position - spot).Length())) return false;
                bool on = ShortcutOn("routefailprobe");
                int[] c = PatchCounters();
                if (!RfpTag(on, true)) { c[3581]++; return false; }
                c[3580]++;
                if (ShortcutOn("routefaillog") && sim.IsInActiveHousehold)
                    RflLine(false, sim, "spot probe at " + V(spot) + " (stand-in for a placed NPC, not this Sim's route), PlanFromPoint sync "
                        + res.mType + " blocker " + res.mBlockerType, r.GetApproximateOriginalDestination(), t0, r.Handle, null);
                return true;
            }
            catch (ResetException) { throw; }
            catch (Exception) { PatchCounters()[3582]++; return false; }
        }

        public static object RouteFailProbeCheck(string what)
        {
            string counters = B582Counters("routefailprobe", 3580, new[] { "probe plans tagged", "probe plans counted as the Sim's, switch off", "errors" }, "");
            if (what == "result") return counters;
            if (what != "run") return "routefailprobe <run|result>";
            bool on = ShortcutOn("routefailprobe");
            bool probe = RfpIsProbe(2, true, 40f);
            bool eaBug = probe && !RfpTag(false, probe);
            bool fixedPure = on && RfpTag(on, probe);
            // controls: the Sim's own spot (IsInSafeLocation), a Plan (the entry route itself), a PlanFromPoint outside the check
            bool controls = eaBug && !RfpIsProbe(2, true, 0.2f) && !RfpIsProbe(0, true, 40f) && !RfpIsProbe(2, false, 40f);
            return B582Line("routefailprobe", on, "an NPC spot probe run on the selected Sim is tagged, not counted as its route", fixedPure,
                "own-spot check / Plan / plain PlanFromPoint stay the Sim's", controls, "", counters);
        }

        // ---- docs/627 §4 rhentryrelax (unseen tier) + rhentryrelaxnear (middle tier) -------------------------------------
        // [verified, ilspycmd RabbitHole::RouteNearEntranceAndEnterRabbitHole] one PlanToSlot over all EnterSlots (IL_02bf);
        // a failed plan is walked as a route fail and the method returns false (no retry on a plan failure; the 3-attempt
        // loop with a 1-? sim-minute Sleep, IL_03bc..IL_03d6, only repeats a failed RouteOutside). The interaction then ends
        // (WorkInRabbitHole: the Sim never reaches work). Ranked rabbit hole fails in phase-2026111* (docs/627 §3): 2218 of
        // 2272 native fails are result 16 (NoPathToDestination) from our UnseenFallback cut of a long plan (unseen Sims),
        // 13 result 10, 1 result 18; 139 of the 153 "routefail" lines are the §2 probes.
        // EA's own relaxed entries:
        //  - unseen tier (rhentryrelax): route-less arrival. Subway.cs OnPortalStart / HoverTrainStation call
        //    AnimateEnterRabbitHole(s, slot, doRoute: false, RouteMetaType.None); HoverTrainStation.PlaceArrivingSim and
        //    AnimateExitRabbitHole put the Sim on the slot with SetPosition/SetForward first. Only for TownieUnseen Sims
        //    (not a player Sim, !UcOnScreen, FarFromViewers) with the door also off screen and far from viewers.
        //  - middle tier (rhentryrelaxnear, needs rhentryrelax too): a Sim that is not a player or selected Sim and not on
        //    screen (World.IsObjectOnScreen) but near viewers may walk to another routing slot of the same rabbit hole: the
        //    ExitSlots (AnimateExitRabbitHole's doors, all in SlotToSlotInfo) that are not EnterSlots, then EA's doRoute:false
        //    entry at that door. No teleport, no placement.
        // Both run only after EA's method failed by a route (EA's own retry rule: RemoveExitReason(RouteFailed), stop when
        // HasExitReason(Default)), never after beforeEntering ran (EA already acted), never with followers, horses or a carried
        // child. On-screen is re-checked right before the action. Household, selected and on-screen Sims keep EA's path.
        // Counters: [3584] unseen arrivals, [3585] unseen: kept EA (door seen / no slot / gate), [3586] beforeEntering refused,
        // [3587] errors; [3588] middle-tier alternate-slot entries, [3589] middle tier: no alternate slot or its plan failed,
        // [3590] on screen at the moment of action (kept EA), [3591] middle-tier route walked but entry refused, [3592] errors,
        // [3593-3599] spare.
        static string sRheMissing = "";

        static void ApplyRhEntryRelax()
        {
            sRheMissing = typeof(RabbitHole).GetMethod("AnimateEnterRabbitHole", kInstAll, null,
                new Type[] { typeof(Sim), typeof(Slot), typeof(bool), typeof(Route.RouteMetaType) }, null) == null ? "RabbitHole.AnimateEnterRabbitHole" : "";
            if (sRheMissing.Length > 0) { AppliedLog().Add("rhentryrelax: not found: " + sRheMissing); return; }
            Patch(typeof(RabbitHole), "RouteNearEntranceAndEnterRabbitHole", typeof(RheEnterHook), "EnterHook", "EnterOrig");
        }

        // Tiers: 0 = EA's (no relaxed entry), 1 = unseen (route-less arrival), 2 = middle (alternate slot, walked).
        internal const int kRheEa = 0, kRheUnseen = 1, kRheNear = 2;
        // Pure: which relaxed entry may follow EA's failed one?
        internal static int RheTier(bool on, bool nearOn, bool player, bool selected, bool onScreen, bool far, bool routeFailed, bool eaActed, bool alone)
        {
            if (!on || player || selected || onScreen || !routeFailed || eaActed || !alone) return kRheEa;
            if (far) return kRheUnseen;
            return nearOn ? kRheNear : kRheEa;
        }

        public class RheEnterHook : RabbitHole
        {
            public override RabbitHoleTuningClass RabbitHoleTuning { get { return null; } } // never instantiated; required by the base class

            public bool EnterHook(Sim a, IRabbitHoleFollowers inst, BeforeEnteringRabbitHoleDelegate beforeEntering, bool canUseCar, Route.RouteMetaType routeMetaType, bool playRouteFailure)
            {
                // switch off: EA's body only (no wrapper delegate, no extra game calls)
                if (sRheMissing.Length > 0 || a == null || !ShortcutOn("rhentryrelax"))
                    return EnterOrig(a, inst, beforeEntering, canUseCar, routeMetaType, playRouteFailure);
                bool acted = false;
                BeforeEnteringRabbitHoleDelegate wrapped = beforeEntering == null ? null
                    : (BeforeEnteringRabbitHoleDelegate)delegate { acted = true; return beforeEntering(); };
                if (EnterOrig(a, inst, wrapped, canUseCar, routeMetaType, playRouteFailure)) return true;
                try { return Relaxed(a, inst, beforeEntering, routeMetaType, acted); }
                catch (ResetException) { throw; }
                catch (Exception) { PatchCounters()[3587]++; return false; }
            }

            bool Relaxed(Sim a, IRabbitHoleFollowers inst, BeforeEnteringRabbitHoleDelegate beforeEntering, Route.RouteMetaType meta, bool acted)
            {
                RabbitHole proxy = RabbitHoleProxy;
                if (proxy == null || a.HasBeenDestroyed || a.Posture is InRabbitHolePosture) return false;
                bool alone = (inst == null || inst.SimFollowers == null || inst.SimFollowers.Count == 0)
                    && a.Posture == a.Standing && !a.IsInRidingPosture && !a.IsHorse;
                bool player = IsPlayerSim(a), selected = a == PlumbBob.SelectedActor;
                if (player || selected || !alone || acted) return false;
                // EA's retry rule (the RouteOutside loop): drop RouteFailed, give up on any other exit reason
                bool routeFailed = a.HasExitReason(ExitReason.RouteFailed);
                if (!routeFailed) return false;
                a.RemoveExitReason(ExitReason.RouteFailed);
                if (a.HasExitReason(ExitReason.Default)) { a.AddExitReason(ExitReason.RouteFailed); return false; }
                bool onScreen = UcOnScreen(a);
                bool far = !onScreen && TownieUnseen(a, null);
                // docs/631 zoomtier: on screen but too small (camera >= kZoomTierMeters or map view) counts as off screen for
                // the middle tier only; the unseen tier keeps the real on-screen test
                bool tier2Seen = onScreen && !ZoomHidden(a);
                int tier = RheTier(true, ShortcutOn("rhentryrelaxnear"), player, selected, tier2Seen, far, routeFailed, acted, alone);
                bool ok = tier == kRheUnseen ? Arrive(a, proxy, beforeEntering, meta) : tier == kRheNear && AltSlot(a, proxy, beforeEntering, meta);
                if (!ok && !a.HasExitReason(ExitReason.Default)) a.AddExitReason(ExitReason.RouteFailed);  // EA's failure as it was
                return ok;
            }

            // unseen tier: EA's route-less arrival (HoverTrainStation.PlaceArrivingSim + AnimateEnterRabbitHole doRoute:false)
            bool Arrive(Sim a, RabbitHole proxy, BeforeEnteringRabbitHoleDelegate beforeEntering, Route.RouteMetaType meta)
            {
                int[] c = PatchCounters();
                List<Slot> slots = new List<Slot>();
                foreach (Slot s in proxy.EnterSlots) if (proxy.SlotToSlotInfo.ContainsKey(s)) slots.Add(s);
                if (slots.Count == 0) { c[3585]++; return false; }
                Slot slot = RandomUtil.GetRandomObjectFromList(slots);  // EA's pick when it walks a Sim out (RouteNearEntranceAndEnterRabbitHole)
                Vector3 pos = proxy.GetPositionOfSlot(slot), fwd = proxy.GetForwardOfSlot(slot);
                if (UcOnScreen(proxy) || !FarFromViewers(pos)) { c[3585]++; return false; }
                if (beforeEntering != null && !beforeEntering()) { c[3586]++; return false; }
                // re-check at the moment of action (beforeEntering can wait)
                if (UcOnScreen(a) || !TownieUnseen(a, null) || UcOnScreen(proxy) || !FarFromViewers(pos)) { c[3590]++; return false; }
                a.SetPosition(pos);
                a.SetForward(fwd);
                if (!AnimateEnterRabbitHole(a, slot, false, meta)) { c[3585]++; return false; }
                c[3584]++;
                ShortcutLog(a.FullName + " => " + proxy.CatalogName + " (rhentryrelax: EA's entry route failed, arrived off screen)");
                return true;
            }

            // middle tier: walk to another routing slot of the same rabbit hole (its exit doors), then EA's doRoute:false entry
            bool AltSlot(Sim a, RabbitHole proxy, BeforeEnteringRabbitHoleDelegate beforeEntering, Route.RouteMetaType meta)
            {
                int[] c = PatchCounters();
                List<Slot> alt = new List<Slot>();
                foreach (Slot s in proxy.ExitSlots)
                    if (!proxy.EnterSlots.Contains(s) && !alt.Contains(s) && proxy.SlotToSlotInfo.ContainsKey(s)) alt.Add(s);
                if (alt.Count == 0) { c[3589]++; return false; }
                if (!ZoomHidden(a)) { c[3590]++; return false; }  // docs/631: the shared tier-2 test (off screen, or zoomtier)
                Route route = a.CreateRoute();
                route.SetRouteMetaType(meta);
                foreach (Slot s in proxy.EnterSlots) route.AddObjectToIgnoreForRoute(proxy.SlotToSlotInfo[s].Footprint.ObjectId);
                foreach (Slot s in alt) route.AddObjectToIgnoreForRoute(proxy.SlotToSlotInfo[s].Footprint.ObjectId);
                route.PlanToSlot(proxy, alt.ToArray());
                if (!route.PlanResult.Succeeded()) { c[3589]++; return false; }
                Slot slot = (Slot)route.PlanResult.mDestSlotNameHash;
                if (!proxy.SlotToSlotInfo.ContainsKey(slot)) { c[3589]++; return false; }
                bool walked;
                proxy.ActiveEntryRoutes.Add(route);
                try { route.DoRouteFail = false; walked = a.DoRoute(route); }
                finally { proxy.ActiveEntryRoutes.Remove(route); }
                if (!walked) { c[3589]++; return false; }
                if (beforeEntering != null && !beforeEntering()) { c[3591]++; return false; }
                if (!AnimateEnterRabbitHole(a, slot, false, meta)) { c[3591]++; return false; }
                c[3588]++;
                return true;
            }

            public bool EnterOrig(Sim a, IRabbitHoleFollowers inst, BeforeEnteringRabbitHoleDelegate beforeEntering, bool canUseCar, Route.RouteMetaType routeMetaType, bool playRouteFailure)
            { return false; } // receives RabbitHole.RouteNearEntranceAndEnterRabbitHole (after RabbitHoleHook's body)
        }

        // RhEntryRelaxCheck run: the tier decision with controls (off arm: EA only; household / selected / on screen / not a
        // route fail / EA already acted / followers keep EA; the middle tier needs its own sub-key). Live: counts Sims in
        // rabbit hole entry interactions by tier right now.
        public static object RhEntryRelaxCheck(string what)
        {
            string counters = B582Counters("rhentryrelax", 3584, new[] { "unseen arrivals", "unseen kept EA", "beforeEntering refused", "errors" }, sRheMissing);
            if (what == "result") return counters;
            if (what != "run") return "rhentryrelax <run|result>";
            bool on = ShortcutOn("rhentryrelax");
            bool eaBug = RheTier(false, false, false, false, false, true, true, false, true) == kRheEa;
            bool fixedPure = on && RheTier(on, false, false, false, false, true, true, false, true) == kRheUnseen;
            bool controls = eaBug
                && RheTier(true, true, true, false, false, true, true, false, true) == kRheEa     // household / player Sim
                && RheTier(true, true, false, true, false, true, true, false, true) == kRheEa     // selected
                && RheTier(true, true, false, false, true, false, true, false, true) == kRheEa    // on screen
                && RheTier(true, true, false, false, false, true, false, false, true) == kRheEa   // not a route failure
                && RheTier(true, true, false, false, false, true, true, true, true) == kRheEa     // beforeEntering already ran
                && RheTier(true, true, false, false, false, true, true, false, false) == kRheEa   // followers / horse
                && RheTier(true, false, false, false, false, false, true, false, true) == kRheEa; // near viewers, sub-key off
            return B582Line("rhentryrelax", on, "an unseen NPC whose rabbit hole entry route failed arrives by EA's route-less entry", fixedPure && sRheMissing.Length == 0,
                "player / selected / on-screen / non-route / followers keep EA", controls, sRheMissing, counters + "; " + RheLive());
        }

        public static object RhEntryRelaxNearCheck(string what)
        {
            string counters = B582Counters("rhentryrelaxnear", 3588, new[] { "alternate-slot entries", "no alternate slot or plan failed", "on screen at action", "entry refused", "errors" }, sRheMissing);
            if (what == "result") return counters;
            if (what != "run") return "rhentryrelaxnear <run|result>";
            bool on = ShortcutOn("rhentryrelax") && ShortcutOn("rhentryrelaxnear");
            bool eaBug = RheTier(true, false, false, false, false, false, true, false, true) == kRheEa;
            bool fixedPure = on && RheTier(true, true, false, false, false, false, true, false, true) == kRheNear;
            bool controls = eaBug
                && RheTier(true, true, false, false, true, false, true, false, true) == kRheEa    // on screen: never
                && RheTier(true, true, false, true, false, false, true, false, true) == kRheEa    // selected
                && RheTier(true, true, true, false, false, false, true, false, true) == kRheEa    // player Sim
                && RheTier(false, true, false, false, false, false, true, false, true) == kRheEa  // main switch off
                && RheTier(true, true, false, false, false, true, true, false, true) == kRheUnseen; // far stays the unseen tier
            return B582Line("rhentryrelaxnear", on, "a near-viewer, off-screen NPC whose entry route failed walks to another door of the same rabbit hole", fixedPure && sRheMissing.Length == 0,
                "on-screen / selected / player / main switch off keep EA; far Sims stay unseen-tier", controls, sRheMissing, counters + "; " + RheLive());
        }

        static string RheLive()
        {
            int unseen = 0, near = 0, seen = 0;
            try
            {
                foreach (Sim s in Sims3.Gameplay.Queries.GetObjects<Sim>())
                {
                    if (s == null || s.CurrentInteraction == null || !(s.CurrentInteraction.Target is RabbitHole) || s.Posture is InRabbitHolePosture) continue;
                    if (IsPlayerSim(s) || UcOnScreen(s)) seen++;
                    else if (TownieUnseen(s, null)) unseen++;
                    else near++;
                }
            }
            catch (ResetException) { throw; }
            catch (Exception) { return "live: error"; }
            return "live: heading to a rabbit hole now: unseen " + unseen + ", off-screen near viewers " + near + ", player or on screen " + seen;
        }
    }
}
