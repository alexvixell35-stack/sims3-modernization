using System;
using Sims3.Gameplay;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Objects.Vehicles;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // Live-mode fallback for unseen Sims (docs/202, "hitch guard"): no freeze >= 300 ms from routing work for a Sim the player
    // cannot see. All switches default off.
    //   unseenfbstuck    (phase B, docs/202 §3.3): the stuck check plans nothing for an unseen Sim and re-arms itself at the
    //                    same round (the game's own CreateAlarm); a Sim that moves ends it, a seen Sim gets the game's check.
    //   unseenfbplan     (phase A, §3.1-§3.2): a plan whose follower is unseen is marked for ts3native ([UnseenFallback]),
    //                    which cuts that sync plan or route job at DeadlineMs through the existing no-path exits; a cut plan
    //                    returns as an ordinary failed plan. Then a no-retry window (same Sim, same 1 m goal cell, 1.5 s) and
    //                    a per-Sim cap (4 cuts in 10 s, then uncut) (UfLimiter).
    //   unseenfbteleport (§3.2 item 1, needs unseenfbplan): a cut own-task Route.Plan of a standing unseen townie places it at
    //                    a point-routable, unseen route goal (the trip shortcuts' PlaceSim) and plans once more from there.
    // Counters: [303] stuck checks deferred, [304] stuck checks run (Sim seen), [305] plans marked, [306] marked plans cut,
    // [307] teleports, [308] teleports refused (no spot), [309] no-retry answers, [310] plans checked but seen,
    // [311] marks refused (no ts3native, [UnseenFallback] off or not installed), [312] longest marked plan (ms, wall).
    //   unseenroute      (docs/202 §3.9, Rodney 2026-10-31): D without the exactness proofs for unseen Sims (UfPre with relaxed =
    //                    true): no verdict or unknown colour still acts, drive/boat/subway-capable routes get a fast-leg travel
    //                    time, any unlocked lot or the street, same-lot outdoor trips; a hold that ends because the goal turned
    //                    seen places the still-unseen Sim at a plausible point of the trip and plans exactly from there.
    //                    Safety stays: own task, yielding context, follow-helper callers, routable outdoor ground, no vehicle
    //                    starters, no household or seen Sim, proven-different colours. Counters [641-646].
    public static partial class Fixes
    {
        static readonly UfLimiter sUf = new UfLimiter();
        static bool sInRoutableCheck;  // inside Sim.IsPointInLotSafelyRoutable (RoutableCacheHook): never marked
        static bool sUfInLeg;          // D-leave walks its visible leg (UfLeave): plans inside are left to the game

        // docs/202 §5.6 (5): plans of >= kUfSlowPlanMs (wall, the hook's whole call: sync search, or the task's sleep for an async
        // job) are named in ts3native.log with the scope verdict, so long routing hitches can be split into seen Sims (accepted),
        // marked-but-not-cut and never-asked (why -1: no Sim follower, stuck-check probe, own check, pet, D leg, switches off)
        // 2026-11-08: no total cap (the 200 left the second half of a gate2 job unlogged); a rate cap of kUfSlowPlanPerMin a wall
        // minute instead, plus ts3native's own disk guard for ufplan: lines (userdiag.cpp kUdUfPlanLines). Arm + wall stamp: ts3native.
        const int kUfSlowPlanMs = 150, kUfSlowPlanPerMin = 120;
        static int sUfSlowLogged, sUfSlowMinute;
        // per plan, not static (PR #306, Dalexlol): async plans of two Sims overlap (each task sleeps on its route job), and shared fields printed one
        // plan's time with another plan's verdict and Sim (e.g. "why 3 cut" on a household Sim: a cut needs a mark, a mark why 0)
        struct UfLast { public int Why; public bool Marked, Cut, ViewerOnly; public Sim Sim; }
        // Scope-gap counters (counters only, no behaviour change). [854-857, 861] plans whose follower is not a Sim (who == null) that
        // reach UfPlanBody: Vehicle and IOwnableVehicle / Vehicle not ownable / Vehicle with a household or selectable Sim in
        // Occupants / not a Vehicle (or no proxy), and the total. [858-860] Sim plans whose why is kNearViewers only through the
        // viewer bit (not on screen, not reach; household Sims never get here): plans, plans >= 250 ms, total ms. Map view and Sims
        // without a table row do not count.
        const int kUfViewerSlowMs = 250, kUfDriverlessLogMax = 50;
        static bool sUfWhyViewerOnly;
        static int sUfDriverlessLogged;
        const int kUfGoalLogMax = 100;  // docs/197 §7 goalhh lines per load
        static int sUfGoalLogged;
        static void UfDriverless(IScriptProxy proxy)
        {
            int[] c = PatchCounters();
            c[861]++;
            Vehicle v = proxy != null ? proxy.Target as Vehicle : null;
            if (v == null) { c[857]++; return; }
            bool own = v is IOwnableVehicle;
            c[own ? 854 : 855]++;
            bool hh = false;
            Sim[] occ = v.Occupants;
            if (occ != null)
                for (int i = 0; i < occ.Length; i++)
                    if (occ[i] != null && (occ[i].IsSelectable || occ[i].Household == Household.ActiveHousehold)) { hh = true; break; }
            if (hh) c[856]++;
            if (sUfDriverlessLogged < kUfDriverlessLogMax)
            {
                sUfDriverlessLogged++;
                Lot ol = v.GetOwnerLot();
                DiagLine("ufdriverless: " + v.GetType().Name + (own ? " ownable" : " not ownable") + (hh ? " household occupant" : "") + (ol != null ? " ownerlot " + ol.LotId : ""));
            }
        }
        // docs/249 household rule, driverless routes (Rodney 2026-11-07): unseen (0) only for a Vehicle that is not an IOwnableVehicle and has no household
        // occupant (ambient traffic, NPC boats); anything else without a Sim (an ownable vehicle, a vehicle with a household occupant, no vehicle at all)
        // is household (UnseenFallbackCore.kHousehold) and keeps its cuts off
        static int UfDriverlessWhy(IScriptProxy proxy)
        {
            try
            {
                Vehicle v = proxy != null ? proxy.Target as Vehicle : null;
                if (v == null || v is IOwnableVehicle) return UnseenFallbackCore.kHousehold;
                Sim[] occ = v.Occupants;
                if (occ != null)
                    for (int i = 0; i < occ.Length; i++)
                        if (occ[i] != null && (occ[i].IsSelectable || occ[i].Household == Household.ActiveHousehold)) return UnseenFallbackCore.kHousehold;
                return 0;
            }
            catch (Exception) { return UnseenFallbackCore.kHousehold; }
        }
        static void UfSlowPlanLog(int kind, int ms, UfLast l)
        {
            if (l.ViewerOnly)
            {
                int[] vc = PatchCounters();
                vc[858]++;
                if (ms >= kUfViewerSlowMs) vc[859]++;
                if (ms > 0) vc[860] += ms;
            }
            if (ms < kUfSlowPlanMs) return;
            int minute = System.Environment.TickCount / 60000;
            if (minute != sUfSlowMinute) { sUfSlowMinute = minute; sUfSlowLogged = 0; }
            if (sUfSlowLogged++ >= kUfSlowPlanPerMin) return;
            Sim s = l.Sim;
            DiagLine("ufplan: kind " + kind + " " + ms + " ms why " + l.Why + (l.Marked ? " marked" : "") + (l.Cut ? " cut" : "")
                + (s != null ? " " + s.FullName + (Simulator.CurrentTask == s.ObjectId ? " own task" : " other task") : "")
                + ", tick " + Sims3.Gameplay.Utilities.SimClock.CurrentTicks);
        }

        // docs/202 §2; 0 = unseen. GameStates.IsLiveState = in-world substate id 0 (LiveModeState): Build, Buy, CAS and Edit
        // Town are other substates [verified, decompiled].
        // Same order and codes as UnseenFallbackCore.Why, but each test runs only when the ones before it passed: the camera
        // query (4 GetOption2 ICs per position), the on-screen IC and the posture checks are skipped for Sims an earlier test
        // already calls seen (docs/202 §5.4b).
        static int UfWhy(Sim s)
        {
            long t = UcBegin(kUcUfWhy, s != null ? s.ObjectId.Value : 0);  // docs/227 probe
            int r = UfWhyBody(s);
            UcEnd(kUcUfWhy, t);
            return r;
        }
        static int UfWhyBody(Sim s)
        {
            sUfWhyViewerOnly = false;
            if (!GameStates.IsLiveState || GameStates.IsTravelling || GameStates.IsWorldShuttingDown) return UnseenFallbackCore.kNotLive;
            if (s == null || s.HasBeenDestroyed || s.SimDescription == null) return UnseenFallbackCore.kGone;
            if (s.IsSelectable || s.Household == Household.ActiveHousehold) return UnseenFallbackCore.kHousehold;
            if (UfLotSeen(s.LotCurrent)) return UnseenFallbackCore.kActiveLot;
            // docs/227 §6 central table: on screen (bit 1) = kOnScreen, reach or household (bits 2, 3) = kNearViewers; the map
            // view (all seen) and a Sim without a row keep the old tests
            int b = CameraController.IsMapViewModeEnabled() ? -1 : UtbBits(s);
            if (b >= 0)
            {
                int w = (b & kUtbOnScreen) != 0 ? UnseenFallbackCore.kOnScreen : (b & (kUtbReach | kUtbViewer)) != 0 ? UnseenFallbackCore.kNearViewers : -1;
                if (sUtbCheck) UtbCompare(w < 0, !UcOnScreen(s) && UfPosUnseen(s.Position, kUfSimRadiusM));
                sUfWhyViewerOnly = w == UnseenFallbackCore.kNearViewers && (b & (kUtbOnScreen | kUtbReach)) == 0;
                if (w >= 0) return w;
            }
            else
            {
                if (UcOnScreen(s)) return UnseenFallbackCore.kOnScreen;
                if (!UfPosUnseen(s.Position, kUfSimRadiusM)) return UnseenFallbackCore.kNearViewers;
            }
            if (UfHeld(s)) return UnseenFallbackCore.kHeld;
            Sim t = s.CurrentInteraction != null ? s.CurrentInteraction.Target as Sim : null;
            return t != null && t != s && (t.IsSelectable || t.Household == Household.ActiveHousehold || UfSimSeen(t))
                ? UnseenFallbackCore.kSocialSeen : UnseenFallbackCore.kSeen;
        }

        // on screen or near the viewers: the Sim's table row (docs/227 §6, as UfWhy's own Sim above), else the old tests
        static bool UfSimSeen(Sim t)
        {
            int b = CameraController.IsMapViewModeEnabled() ? -1 : UtbBits(t);
            if (b < 0) return UcOnScreen(t) || !UfPosUnseen(t.Position, kUfSimRadiusM);
            bool seen = (b & (kUtbOnScreen | kUtbReach | kUtbViewer)) != 0;
            if (sUtbCheck) UtbCompare(!seen, !UcOnScreen(t) && UfPosUnseen(t.Position, kUfSimRadiusM));
            return seen;
        }

        // docs/202 §2.1 camera checks. Seen unless ts3native's camera query (unseenfb.cpp UfCamMsg: the game's camera frustum
        // and the reach rule max(80 m, 2 |cam y - target y|), wide camera = all seen) says unseen; the map view is all seen; plus
        // our 80 m household rule (FarFromViewers). Without ts3native 0.36.89+ the query answers 0: everything is seen, nothing acts.
        const float kUfSimRadiusM = 3f;  // EA's per-agent on-screen byte tests 3x the model bounds (docs/143 §3)
        static bool UfPosUnseen(Vector3 p, float r)
        {
            long t = UcBegin(kUcPos, 0);  // docs/227 probe
            bool res = UfPosUnseenBody(p, r);
            UcEnd(kUcPos, t);
            return res;
        }
        // probe run 2: 6.0 us, 4 UfCamAsk ICs each. The camera query is now one PosUnseen IC (same answer; old ts3native = the
        // four calls), and with "unseenposcache" the whole answer is kept per tick by the exact point and radius
        static bool UfPosUnseenBody(Vector3 p, float r)
        {
            long now = Sims3.Gameplay.Utilities.SimClock.CurrentTicks;
            if (!UpcOn(now)) return UfPosUnseenCalc(p, r);
            bool v;
            uint bx = Bits(p.x), by = Bits(p.y), bz = Bits(p.z), br = Bits(r);
            if (sUfPosCache.Get(now, bx, by, bz, br, out v)) return v;
            v = UfPosUnseenCalc(p, r);
            sUfPosCache.Put(now, bx, by, bz, br, v);
            return v;
        }
        static bool UfPosUnseenCalc(Vector3 p, float r)
        {
            if (CameraController.IsMapViewModeEnabled() || !FarFromViewers(p)) return false;
            int a = UtbPosUnseen(p.x, p.y, p.z, r);
            if (a >= 0) { UcCount(kUcCamAsk); return a == 1; }
            return UfCamAsk(3, p.x) && UfCamAsk(4, p.y) && UfCamAsk(5, p.z) && UfCamAsk(6, r);
        }
        static readonly UcPosCache sUfPosCache = new UcPosCache(), sUfGoalCache = new UcPosCache();
        static bool UfCamAsk(int code, float f) { UcCount(kUcCamAsk); return sYieldProbe.GetOption2((Sims3.SimIFace.Route.RouteOption2)(0x8300000000000000UL | (ulong)(uint)code << 48 | Bits(f))); }

        // the camera's lot (the lot under the camera target, LotManager.GetLotAtPoint), the active household's lot, or a lot whose
        // bounding circle reaches into the view (frustum or reach); the world lot (streets) is judged by position only
        static bool UfLotSeen(Lot lot)
        {
            long t = UcBegin(kUcLot, 0);  // docs/227 probe
            bool r = UfLotSeenBody(lot);
            UcEnd(kUcLot, t);
            return r;
        }
        static bool UfLotSeenBody(Lot lot)
        {
            if (lot == null || lot.IsWorldLot) return false;
            if (lot == Household.ActiveHouseholdLot || lot == UfCameraLot()) return true;
            long now = Sims3.Gameplay.Utilities.SimClock.CurrentTicks;
            if (now != sUfLotTick) { sUfLotTick = now; sUfLotSeen.Clear(); }
            bool seen;
            if (sUfLotSeen.TryGetValue(lot.LotId, out seen)) return seen;
            Vector3 c;
            float r;
            UfLotShape(lot, now, out c, out r);
            seen = !UfPosUnseen(c, r);
            sUfLotSeen[lot.LotId] = seen;
            return seen;
        }
        // the lot's centre (GetCenterPosition: a terrain-height IC) and bounding radius (4 sqrt); with "unseenposcache" kept per
        // lot while its corners stay put (a moved lot gets new corners), cleared per world load.
        // shortcut: a build-mode terraform under the centre keeps the old height (a few m on a 10 m+ sphere); key on terrain edits if it matters
        static void UfLotShape(Lot lot, long now, out Vector3 c, out float r)
        {
            Corners k = lot.Corners;
            bool cache = k != null && UpcOn(now);
            UfLotGeo g;
            if (cache && sUfLotGeo.TryGetValue(lot.LotId, out g) && g.Same(k)) { c = g.C; r = g.R; return; }
            c = lot.GetCenterPosition();
            r = 0f;
            if (k != null) for (int i = 0; i < 4; i++) r = Math.Max(r, (k[i] - c).Length());
            if (cache) sUfLotGeo[lot.LotId] = new UfLotGeo(k, c, r);
        }
        sealed class UfLotGeo
        {
            readonly Vector3 m0, m1, m2, m3;
            public readonly Vector3 C;
            public readonly float R;
            public UfLotGeo(Corners k, Vector3 c, float r) { m0 = k[0]; m1 = k[1]; m2 = k[2]; m3 = k[3]; C = c; R = r; }
            public bool Same(Corners k) { return k[0] == m0 && k[1] == m1 && k[2] == m2 && k[3] == m3; }
        }
        static readonly System.Collections.Generic.Dictionary<ulong, UfLotGeo> sUfLotGeo = new System.Collections.Generic.Dictionary<ulong, UfLotGeo>();
        static long sUfLotTick = -1, sUfCamLotTick = -1;
        static readonly System.Collections.Generic.Dictionary<ulong, bool> sUfLotSeen = new System.Collections.Generic.Dictionary<ulong, bool>();
        static Lot sUfCamLot;
        static Lot UfCameraLot()
        {
            long now = Sims3.Gameplay.Utilities.SimClock.CurrentTicks;
            if (now != sUfCamLotTick) { sUfCamLotTick = now; sUfCamLot = LotManager.GetLotAtPoint(CameraController.GetTarget()); }
            return sUfCamLot;
        }

        // §3.2 / §2.1 (2): a teleport goal must be out of view: the point (Sim-sized), its lot (not the camera's, not in view)
        static bool UfGoalUnseen(Vector3 goal)
        {
            long t = UcBegin(kUcGoal, 0);  // docs/227 probe
            bool r = UfGoalUnseenBody(goal);
            UcEnd(kUcGoal, t);
            return r;
        }
        // probe run 2: 20.7 us; with "unseenposcache" the answer is kept per tick by the exact goal (inferred: a goal is asked
        // again by the next plan of the same trip)
        static bool UfGoalUnseenBody(Vector3 goal)
        {
            long now = Sims3.Gameplay.Utilities.SimClock.CurrentTicks;
            if (!UpcOn(now)) return UfGoalUnseenCalc(goal);
            bool v;
            uint bx = Bits(goal.x), by = Bits(goal.y), bz = Bits(goal.z);
            if (sUfGoalCache.Get(now, bx, by, bz, 0, out v)) return v;
            v = UfGoalUnseenCalc(goal);
            sUfGoalCache.Put(now, bx, by, bz, 0, v);
            return v;
        }
        static bool UfGoalUnseenCalc(Vector3 goal)
        {
            if (!UfPosUnseen(goal, kUfSimRadiusM)) return false;
            Lot lot = LotManager.GetLotAtPoint(goal);
            return !UfLotSeen(lot);
        }

        // riding, ridden, in a vehicle, carried, or carrying a child, pet or object (their routes move another object too).
        // Issue #89: the posture chain is walked here, bounded, instead of through Posture.Satisfies, which recurses down
        // PreviousPosture with no end check: EA's Sim.set_Posture can leave two postures pointing at each other on a pop
        // (PosturePopHook below), and Satisfies then overflows the interpreter stack. Same answer as the four Satisfies calls
        // (Satisfaction > 0 at any posture of the chain, Satisfies is not virtual) [verified, decompiled]; a chain that does not end
        // within kPostureChainCap postures is a broken state: held, so nothing of ours moves or fakes this Sim. Counter [371].
        static bool UfHeld(Sim s)
        {
            if (s.IsInRidingPosture || s.IsInBeingRiddenPosture || s.Parent is Vehicle || s.Parent is Sim) return true;
            int n = 0;
            for (Sims3.Gameplay.ActorSystems.Posture p = s.Posture; p != null; p = p.PreviousPosture)
            {
                if (!UnseenFallbackCore.ChainStepOk(++n)) { PatchCounters()[371]++; return true; }
                if (p.Satisfaction(CommodityKind.CarryingChild, null) > 0f || p.Satisfaction(CommodityKind.CarryingPet, null) > 0f
                    || p.Satisfaction(CommodityKind.CarryingObject, null) > 0f || p.Satisfaction(CommodityKind.BeingRidden, null) > 0f) return true;
            }
            return false;
        }

        // docs/202 §3.7: a cut plan (or a no-retry answer) is not a route failure of the Sim: our watchdog's route-fail count
        // (NotePlanResult, read by PlanHook through UfTakeNotFail), the backoff entry (30 sim-minutes of instant failures) and
        // failrepeat's entry for this goal are dropped; Overwatch V2 never saw it (sOwProbe above)
        static bool sUfNotFail;
        static bool UfTakeNotFail() { bool v = sUfNotFail; sUfNotFail = false; return v; }
        static void UfNotAFailure(ulong id, Vector3 dest)
        {
            sUfNotFail = true;
            BackoffKey k = new BackoffKey(id, dest);
            sBackoff.Remove(k);
            sFailRepeat.Remove(k);
        }

        // ---- docs/202 §3.5 D helpers ----

        // adult-like human townie on its own: teen to elder, not a pet, no group (GroupingSituation walks members together), not
        // riding, carried or carrying (UfHeld); the interaction it runs is not one whose point is the walk, nor a social (jigs,
        // two-Sim routes)
        // §3.9.6 relaxed (unseenroute): children too, and a grouping situation's leader when every member is unseen (the group
        // moves together: the members are placed by the goal with the leader, UfMoveGroup); a member's own plan never acts
        static bool UfPreSim(Sim s, bool relaxed)
        {
            SimDescription d = s.SimDescription;
            if (d == null || d.IsPet || !(relaxed ? d.ChildOrAbove : d.TeenOrAbove) || !s.IsHuman || UfHeld(s)) return false;
            Sims3.Gameplay.Situations.GroupingSituation g = s.GetSituationOfType<Sims3.Gameplay.Situations.GroupingSituation>();
            if (g != null && !(relaxed && UfGroupOk(s, g))) { if (relaxed) PatchCounters()[650]++; return false; }
            Sims3.Gameplay.Interactions.InteractionInstance cur = s.CurrentInteraction;
            return cur != null && !(cur is Sims3.Gameplay.Socializing.SocialInteraction) && !UfPreCore.Denied(cur.GetType().FullName);
        }

        static bool UfGroupOk(Sim s, Sims3.Gameplay.Situations.GroupingSituation g)
        {
            int n = 0, ok = 0;
            foreach (Sim m in g.Participants)
            {
                n++;
                if (m == s || (m != null && !m.HasBeenDestroyed && m.SimDescription != null && m.SimDescription.ChildOrAbove && m.IsHuman
                    && !UfHeld(m) && UfWhy(m) == 0)) ok++;
            }
            return UrCore.GroupOk(g.Leader == s, n, ok);
        }

        // §3.5 (1) door and lot rules: the cached verdicts are keyed by route profile, not by household or Sim, so per-Sim door
        // permissions are not in them; D refuses every lot with a door or gate whose lock is not "anybody", a single-gender door
        // or a one-way room (the per-lot scan cached for one sim-hour). Only community lots and the Sim's own home; no apartments,
        // shells, moving houseboats, resorts, diving lots.
        static bool UfPreLot(Sim sim, Lot lot)
        {
            if (lot == null || lot.IsWorldLot || lot == Household.ActiveHouseholdLot) return false;
            if (!lot.IsCommunityLot && lot != sim.LotHome) return false;
            return UfLotOpen(lot);
        }
        // docs/202 §3.9 relaxed: the street (world lot) or any lot but the active household's that passes the special-lot and
        // door rules (an outdoor goal needs no door, but a fenced yard's gate does)
        // 2026-11-01 (docs/202 §3.9.7b): the relaxed goal needs no door at all (outdoor ground), so only a lock refuses its lot
        // (Rodney: locked lots refuse); single-gender doors (beach changing rooms, gym and pool locker rooms) and one-way rooms
        // refused every beach and park goal of sv-ur-car-loop. The start lot counts only when the Sim stands indoors (it may sit
        // behind a lock); outdoors it walks off the lot as it walked on. null = ok, else the refusal reason (gate 334 detail).
        static string UfRelaxedLotWhy(Lot lot, string side, bool check)
        {
            if (!check || lot == null || lot.IsWorldLot) return null;
            if (lot == Household.ActiveHouseholdLot) return side + ":household lot";
            if (lot.IsApartmentLot) return side + ":apartment";
            if (lot.BuildableShells.Length > 0) return side + ":shell";
            if (!HouseboatOk(lot)) return side + ":houseboat";
            if (lot.CommercialLotSubType == CommercialLotSubType.kEP10_Resort || lot.CommercialLotSubType == CommercialLotSubType.kEP10_Diving) return side + ":resort/diving";
            return UfLotLocked(lot) ? side + ":locked door" : null;  // start side: asked only when the Sim stands indoors (UrCore.StartLotCounts)
        }
        static bool UfLotLocked(Lot lot)
        {
            long now = Sims3.Gameplay.Utilities.SimClock.CurrentTicks;
            UfDoors e;
            if (sUrLocks.TryGetValue(lot.LotId, out e) && e.At <= now && now - e.At < kUfDoorTicks) return !e.Open;
            e.At = now;
            e.Open = true;
            foreach (CommonDoor d in lot.GetObjects<CommonDoor>())
                if (d == null || UrCore.DoorRefuses(d.LockType != CommonDoor.tLock.Anybody, d is DoorSingleGender, d.OneWayRoomDestinationId != 0)) { e.Open = false; break; }
            if (sUrLocks.Count >= 256) sUrLocks.Clear();
            sUrLocks[lot.LotId] = e;
            return !e.Open;
        }
        static readonly System.Collections.Generic.Dictionary<ulong, UfDoors> sUrLocks = new System.Collections.Generic.Dictionary<ulong, UfDoors>();

        // relaxed refusal details "gate:reason" (AppDomain ts3fixes.unseenroute.rj, agent unseenroute_rj_detail; 64 keys): which
        // rule keeps relaxed D from acting, so a failed run names it (2026-11-01: sv-ur-car-loop had only per-gate totals)
        static int UrRj(int slot, string why)
        {
            var d = AppDomain.CurrentDomain.GetData("ts3fixes.unseenroute.rj") as System.Collections.Generic.Dictionary<string, int>;
            if (d == null) { d = new System.Collections.Generic.Dictionary<string, int>(); AppDomain.CurrentDomain.SetData("ts3fixes.unseenroute.rj", d); }
            string k = slot + ":" + (why ?? "?");
            int n;
            if (d.TryGetValue(k, out n) || d.Count < 64) d[k] = n + 1;
            return UfRj(slot);
        }
        // why UfPreSim refused (only asked after a refusal)
        static string UfPreSimWhy(Sim s, bool relaxed)
        {
            SimDescription d = s.SimDescription;
            if (d == null) return "no description";
            if (d.IsPet) return "pet";
            if (!(relaxed ? d.ChildOrAbove : d.TeenOrAbove)) return "age";
            if (!s.IsHuman) return "not human";
            if (UfHeld(s)) return "held";
            if (s.GetSituationOfType<Sims3.Gameplay.Situations.GroupingSituation>() != null) return "group";
            Sims3.Gameplay.Interactions.InteractionInstance cur = s.CurrentInteraction;
            if (cur == null) return "no interaction";
            if (cur is Sims3.Gameplay.Socializing.SocialInteraction) return "social";
            return "deny:" + cur.GetType().Name;
        }
        static bool UfLotOpen(Lot lot)
        {
            if (lot.IsApartmentLot || lot.BuildableShells.Length > 0 || !HouseboatOk(lot)) return false;
            if (lot.CommercialLotSubType == CommercialLotSubType.kEP10_Resort || lot.CommercialLotSubType == CommercialLotSubType.kEP10_Diving) return false;
            long now = Sims3.Gameplay.Utilities.SimClock.CurrentTicks;
            UfDoors e;
            if (sUfDoors.TryGetValue(lot.LotId, out e) && e.At <= now && now - e.At < kUfDoorTicks) return e.Open;
            e.At = now;
            e.Open = true;
            foreach (CommonDoor d in lot.GetObjects<CommonDoor>())
                if (d == null || d.LockType != CommonDoor.tLock.Anybody || d is DoorSingleGender || d.OneWayRoomDestinationId != 0) { e.Open = false; break; }
            if (sUfDoors.Count >= 256) sUfDoors.Clear();
            sUfDoors[lot.LotId] = e;
            return e.Open;
        }
        struct UfDoors { public long At; public bool Open; }
        static readonly System.Collections.Generic.Dictionary<ulong, UfDoors> sUfDoors = new System.Collections.Generic.Dictionary<ulong, UfDoors>();
        static readonly long kUfDoorTicks = Sims3.Gameplay.Utilities.SimClock.ConvertToTicks(1f, Sims3.Gameplay.Utilities.TimeUnit.Hours);

        // outdoors (room 0) and on the ground (within 0.5 m of the terrain): no stairs, elevators or rooms behind doors
        static bool UfOutdoorGround(Vector3 p)
        {
            return World.GetRoomId(p) == 0 && Math.Abs(p.y - World.GetTerrainHeight(p.x, p.z)) < 0.5f;
        }

        // first managed frame above the route funnels and our hooks ("FullType.Method"), as routesrc reads it; Lot.PlanToLot(Ex)
        // is skipped too, so RoutingComponent.RouteToLot (plans through Lot.PlanToLot) is seen as the caller
        static string UfCaller(out string above)
        {
            above = null;
            for (int i = 1; i < 32; i++)
            {
                System.Reflection.MethodBase m = new System.Diagnostics.StackFrame(i, false).GetMethod();
                if (m == null) return null;
                string ft = m.DeclaringType != null ? m.DeclaringType.FullName : null;
                if (RouteSrcCore.IsPassThrough(ft, m.Name) || UfPreCore.PlanToLotFrame(ft, m.Name) || UfPreCore.OwnFrame(ft, m.Name)) continue;
                System.Reflection.MethodBase n = new System.Diagnostics.StackFrame(i + 1, false).GetMethod();
                if (n != null) above = (n.DeclaringType != null ? n.DeclaringType.FullName : null) + "." + n.Name;
                return ft + "." + m.Name;
            }
            return null;
        }

        // the caller follows the route (UfPreCore.FollowsRoute); a refused caller frame is counted by name (AppDomain
        // ts3fixes.unseenfb.callers, read by the agent as unseenfbpre_callers; 64 names at most) to show what a wider allow-list
        // would reach
        static bool UfFollows()
        {
            string above, f = UfCaller(out above);
            if (UfPreCore.FollowsRoute(f, above)) return true;
            var d = AppDomain.CurrentDomain.GetData("ts3fixes.unseenfb.callers") as System.Collections.Generic.Dictionary<string, int>;
            if (d == null) { d = new System.Collections.Generic.Dictionary<string, int>(); AppDomain.CurrentDomain.SetData("ts3fixes.unseenfb.callers", d); }
            int n;
            if (d.TryGetValue(f ?? "?", out n) || d.Count < 64) d[f ?? "?"] = n + 1;
            return false;
        }

        // docs/202 §3.5.1 reject gate: counts slot (328-340, Fixes.cs) and answers "not ours"
        static int UfRj(int slot) { PatchCounters()[slot]++; return 0; }
        static bool UfNo(int slot) { PatchCounters()[slot]++; return false; }

        // the game's flood colours (ts3native UfColourMsg, codes 11-13 after x / z; 7-10 = the C1-C4 list): 1 = start and goal in one world component,
        // -1 = different components (no land path), 0 = unknown (no waypoint near, several colours near, older ts3native). A point
        // on a lot with no waypoint near is asked again at its lot's entry point (the lot's street side).
        static int UfSameComponent(Sim sim, Vector3 start, Vector3 dest, Lot goalLot)
        {
            return UfColourA(start, sim.LotCurrent) ? UfColourB(dest, goalLot) : 0;
        }
        // A = the colour at p (or at its lot's entry point); true = known
        static bool UfColourA(Vector3 p, Lot lot) { return UfColourAsk(11, p) || (lot != null && !lot.IsWorldLot && UfColourAsk(11, lot.EntryPoint())); }
        // 1 = p (or its lot's entry point) has A's colour, -1 = proven different, 0 = unknown
        static int UfColourB(Vector3 p, Lot lot)
        {
            for (int k = 0; k < 2; k++)
            {
                if (k == 1 && (lot == null || lot.IsWorldLot)) break;
                Vector3 g = k == 0 ? p : lot.EntryPoint();
                if (UfColourAsk(12, g)) return 1;
                if (UfColourAsk(13, g)) return -1;
            }
            return 0;
        }
        static bool UfColourAsk(int code, Vector3 p) { return UfCamAsk(3, p.x) && UfCamAsk(5, p.z) && UfCamAsk(code, 0f); }

        // risk 10c fix: D never runs a sim-thread routability plan to fill a verdict. A cell is proven by the SafeSpot cache
        // (filled by SafeSpot / the watchdog as before) or by D's own list (SpotVerdicts.PlanOk): a normal plan of a D-eligible Sim
        // that succeeded from off the goal lot proves the goal cell joins its lot's edge, and the start cell (on its lot) can leave
        // it [inferred: a path that crosses the lot edge]. PlanOk is read by D only, never by SafeSpot or the watchdog.
        static bool? UfVerdict(Sim sim, Lot lot, RouteProfile prof, Vector3 pos)
        {
            SpotVerdicts v = Verdicts(lot);
            bool? ok = SpotVerdict(sim, lot, v, prof, pos, false, false, false);
            if (ok.HasValue) return ok;
            return v.PlanOk.ContainsKey(KeyOf(prof, pos)) ? (bool?)true : null;
        }
        // a D path skipped for a missing verdict: fill it from this route's own plan (UfPlan, after the plan)
        struct UfFill { public object Route; public Lot GoalLot, StartLot; public Vector3 Goal, Start; public RouteProfile Prof; }
        static UfFill sUfFill;
        static void UfFillNote()
        {
            UfFill f = sUfFill;
            int[] c = PatchCounters();
            for (int k = 0; k < 2; k++)
            {
                Lot lot = k == 0 ? f.GoalLot : f.StartLot;
                if (lot == null || lot.IsWorldLot || (k == 1 && lot == f.GoalLot)) continue;
                SpotVerdicts v = Verdicts(lot);
                if (v.PlanOk.Count >= 256) continue;  // shortcut: a full lot list stops growing until its verdicts expire (a sim-day)
                v.PlanOk[KeyOf(f.Prof, k == 0 ? f.Goal : f.Start)] = true;
                c[327]++;
            }
        }

        // ts3fixes.unseenfb.holding: Sim object id -> hold end (sim ticks), read by our watchdogs and the agent (in_transit)
        static System.Collections.Generic.Dictionary<ulong, long> UfHolds()
        {
            var d = AppDomain.CurrentDomain.GetData("ts3fixes.unseenfb.holding") as System.Collections.Generic.Dictionary<ulong, long>;
            if (d == null) { d = new System.Collections.Generic.Dictionary<ulong, long>(); AppDomain.CurrentDomain.SetData("ts3fixes.unseenfb.holding", d); }
            return d;
        }
        static bool UfInTransit(Sim s) { return UfPreCore.InTransit(sUfHoldsCache ?? (sUfHoldsCache = UfHolds()), s.ObjectId.Value, Sims3.Gameplay.Utilities.SimClock.CurrentTicks); }
        static System.Collections.Generic.Dictionary<ulong, long> sUfHoldsCache;

        // §3.5 (2)-(3) walk-time hold in the Sim's own task (a yielding context: MayYield checked by the caller), one sim-minute at a
        // time; the Sim stands, its interaction runs on and its time passes as for a walk. Every wake-up re-checks the camera rule
        // for the Sim and the goal. false = given up: seen, moved, reset, exit reason or another interaction (the caller plans
        // normally). Never pushes an interaction of its own (§3.7: the queue head stays what the game and mods read).
        // ---- docs/202 §3.9.5-§3.9.7 unseenroute2 state (memory only, never in saves) ----
        // plans in progress per script task (§3.9.6 nested-plan test). Per task, not one static count: a plan's task sleeps inside
        // UfPlan (route job AsyncWait, D's hold), other tasks plan meanwhile, and a global count called all of them nested
        // (2026-10-31 sv-ur-car-loop: unseenroute_rj_nested 371-1060 per arm, 0 acts)
        static readonly UrTaskDepth sUfPlanDepth = new UrTaskDepth();
        struct UrLearn { public object Route; public ulong G, S; }
        static readonly System.Collections.Generic.Dictionary<object, UrLearn> sUrLearn = new System.Collections.Generic.Dictionary<object, UrLearn>();  // per route: other tasks plan while one waits
        static readonly UrCCache sUrC = new UrCCache();
        // Sims this switch placed (§3.9.7 D), AppDomain "ts3fixes.unseenroute.placed" so the agent's fenced_place repro can add one
        static System.Collections.Generic.Dictionary<ulong, bool> sUrPlaced
        {
            get
            {
                var d = AppDomain.CurrentDomain.GetData("ts3fixes.unseenroute.placed") as System.Collections.Generic.Dictionary<ulong, bool>;
                if (d == null) { d = new System.Collections.Generic.Dictionary<ulong, bool>(); AppDomain.CurrentDomain.SetData("ts3fixes.unseenroute.placed", d); }
                return d;
            }
        }
        static readonly UrGhostLedger sUrGhosts = new UrGhostLedger();
        static readonly UrEvLog sUrEv = new UrEvLog();
        static int sUrLoadMs;
        // §3.9.9: one rate-limited line per event for ts3native.log (DiagLine), sim-minute + wall ms since load + Sim id hash
        static void UrEv(string kind, ulong simId)
        {
            try
            {
                int now = System.Environment.TickCount;
                if (!sUrEv.May(kind, now)) return;
                double min = Sims3.Gameplay.Utilities.SimClock.CurrentTicks / (double)Sims3.Gameplay.Utilities.SimClock.ConvertToTicks(1f, Sims3.Gameplay.Utilities.TimeUnit.Minutes);
                DiagLine(UrEvLog.Format(kind, min, unchecked(now - sUrLoadMs), simId));
            }
            catch (Exception) { }
        }
        static bool UrNative(int code, uint v) { return sYieldProbe.GetOption2((Sims3.SimIFace.Route.RouteOption2)(0x8300000000000000UL | (ulong)(uint)code << 48 | v)); }

        // §3.9.5 ghost start: ts3native [UnseenGhost] takes the id (codes 21 + 22; 0 = not installed or off: no ghost), then the
        // Sim's dynamic footprint is removed once through the game's own path
        static bool UrGhostStart(Sim sim)
        {
            ulong id = sim.ObjectId.Value;
            if (sim.SimRoutingComponent == null || !UrNative(21, (uint)id) || !UrNative(22, (uint)(id >> 32))) return false;
            if (!sUrGhosts.Start(id)) return false;
            // §3.9.9: the native skip is active here (UrNative above is false otherwise), so the footprint toggles are not called:
            // they can wait on the +0x918 lock while a route job runs, and nothing is removed, so nothing needs a re-insert
            PatchCounters()[657]++;
            UrEv("ghost-start", id);
            return true;
        }
        // every ghost exit: off the native set first (code 23); the footprint was never toggled (§3.9.9)
        static void UrGhostEnd(Sim sim)
        {
            ulong id = sim.ObjectId.Value;
            UrNative(21, (uint)id);
            UrNative(23, (uint)(id >> 32));
            if (!sUrGhosts.End(id)) return;
            PatchCounters()[658]++;
            UrEv("ghost-exit", id);
        }

        // §3.9.7 (C) cache upkeep: cleared at world load; a lot edited in Build/Buy loses its entries, Edit Town clears all. Polled
        // every frame while unseenroute is on (Sleep(0) also wakes while the clock is paused in Build/Buy)
        static int sUrEditGen;
        internal static void StartUrEditWatch()
        {
            int gen = ++sUrEditGen;
            sUrLoadMs = System.Environment.TickCount;
            sUrC.Clear();
            sUrPlaced.Clear();
            Simulator.AddObject(new LoopTask(delegate { UrEditLoop(gen); }));
        }
        static void UrEditLoop(int gen)
        {
            bool inEdit = false, town = false;
            ulong lot = 0;
            while (gen == sUrEditGen)
            {
                try
                {
                    bool bb = LotManager.sIsBuildBuyModeOn, et = GameStates.IsEditTownState;
                    if (bb || et)
                    {
                        inEdit = true;
                        town |= et;
                        if (bb && LotManager.ActiveLot != null) lot = LotManager.ActiveLot.LotId;
                    }
                    else if (inEdit)
                    {
                        if (town) sUrC.Clear();
                        else if (lot != 0) sUrC.ClearLot(lot);
                        inEdit = town = false;
                        lot = 0;
                    }
                }
                catch (Exception) { }
                try
                {
                    int now = System.Environment.TickCount;
                    if (!inEdit && unchecked(now - sUrSweepAt) >= 1000) { sUrSweepAt = now; UrPlacedSweep(); }  // §3.9.7 (D) on becoming visible
                }
                catch (Exception) { }
                // §3.9.9: about 1 s between checks while the switch is on (was every frame); the sweep itself already ran once a second
                Simulator.Sleep(inEdit ? 0u : ShortcutOn("unseenroute") || sUrPlaced.Count > 0 ? 1000u : 60u);
            }
        }

        // §3.9.7 (D) safety net: a Sim this switch placed is seen again: on a lot, its spot must join the lot's edge (the SafeSpot
        // check, one real check if not cached); not joined = move it to the lot's street entry point, its next plans run exactly
        // from there. Run when it becomes visible (UrPlacedSweep, about once a second) and, before that, at its next plan while
        // seen (UfPlanBody). 2026-11-01 sv-ur-fenced-repair: the plan-only trigger never fired (unseenroute_d_check 0: the fenced
        // Sims made no Route.Plan while seen).
        // true = done (drop it from the list); false = no verdict this tick, keep it listed (2026-11-01 unseenroute5 rerun:
        // d_check 12, d_repair 0: the sweep dropped every listed Sim of one pass although only one check per tick may run)
        static bool UrRepairSim(Sim sim)
        {
            int[] c = PatchCounters();
            Lot lot = sim.LotCurrent;
            if (lot == null || lot.IsWorldLot || sim.HasBeenDestroyed) { c[655]++; return true; }  // street placements passed B's colour test
            RouteProfile prof = ProfileOf(sim);
            int act = prof.Ok ? UrCore.PlacedVerdict(SpotVerdict(sim, lot, Verdicts(lot), prof, sim.Position, true, false, false)) : UrCore.kPlacedDrop;
            if (act == UrCore.kPlacedKeep) return false;
            c[655]++;
            UrEv("sweep-check", sim.ObjectId.Value);
            if (act == UrCore.kPlacedDrop) return true;
            Vector3 e = lot.EntryPoint();
            e.y = World.GetTerrainHeight(e.x, e.z);
            if (!sim.CreateRoute().IsPointRoutable(e)) return true;
            PlaceSim(sim, e, sim.ForwardVector, "unseenroute repair");
            c[656]++;
            UrEv("placement", sim.ObjectId.Value);
            ShortcutLog(sim.FullName + " => repair (unseenroute, unreachable spot on " + lot.Name + ")");
            return true;
        }
        static int sUrSweepAt;
        static void UrPlacedSweep()
        {
            var placed = sUrPlaced;
            if (placed.Count == 0 || !GameStates.IsLiveState) return;
            System.Collections.Generic.List<ulong> ids = new System.Collections.Generic.List<ulong>(placed.Keys);
            foreach (ulong id in ids)
            {
                Sim s = GameObject.GetObject(new ObjectGuid(id)) as Sim;
                bool exists = s != null && !s.HasBeenDestroyed && s.InWorld;
                int act = UrCore.PlacedCheck(exists, exists && UfWhy(s) != 0);
                if (act == UrCore.kPlacedKeep || (act == UrCore.kPlacedCheck && !UrRepairSim(s))) continue;
                placed.Remove(id);
            }
        }

        static string sUfHoldWhy;  // why the last UfHold ended (docs/202 §3.9 snap-back reads "seen or moved")
        static bool UfHold(Sim sim, Vector3 dest, float minutes)
        {
            int[] c = PatchCounters();
            long until = Sims3.Gameplay.Utilities.SimClock.CurrentTicks + Sims3.Gameplay.Utilities.SimClock.ConvertToTicks(minutes, Sims3.Gameplay.Utilities.TimeUnit.Minutes);
            uint step = (uint)Sims3.Gameplay.Utilities.SimClock.ConvertToTicks(1f, Sims3.Gameplay.Utilities.TimeUnit.Minutes);
            Sims3.Gameplay.Interactions.InteractionInstance cur = sim.CurrentInteraction;
            Vector3 at = sim.Position;
            ulong id = sim.ObjectId.Value;
            var holds = UfHolds();
            holds[id] = until;
            c[317]++;
            if ((int)minutes > c[318]) c[318] = (int)minutes;
            int t0 = System.Environment.TickCount;
            string why = "time up";
            UfHoldLog(sim, cur, "start, " + minutes.ToString("0") + " sim-min, live " + GameStates.IsLiveState, false);
            try
            {
                // a tick sleep never wakes while the clock is paused (family picker, Build/Buy), and the game then waits on this
                // interaction forever ("Play" arrow stays gray, v0.2.0 reports): yield per frame, cheap exits every frame
                long nextCheck = Sims3.Gameplay.Utilities.SimClock.CurrentTicks + step;
                while (Sims3.Gameplay.Utilities.SimClock.CurrentTicks < until)
                {
                    Simulator.Sleep(0);
                    why = sim.HasBeenDestroyed || !sim.InWorld ? "gone" : sim.CurrentInteraction != cur || (cur != null && cur.Cancelled) ? "interaction ended"
                        : sim.HasExitReason() ? "exit reason" : !GameStates.IsLiveState ? "not live" : sim.IsSelectable || sim.Household == Household.ActiveHousehold ? "family started" : null;
                    if (why != null) return false;
                    if (Sims3.Gameplay.Utilities.SimClock.CurrentTicks < nextCheck) continue;
                    nextCheck = Sims3.Gameplay.Utilities.SimClock.CurrentTicks + step;
                    if ((sim.Position - at).LengthSqr() > 0.01f || UfWhy(sim) != 0 || !TownieUnseen(sim, null) || !UfGoalUnseen(dest)) { why = "seen or moved"; return false; }
                }
                why = "time up";
                return true;
            }
            finally
            {
                sUfHoldWhy = why;
                holds.Remove(id);
                // pauses and family starts are what hung the Play step on v0.2.0/v0.2.1: always log them
                UfHoldLog(sim, cur, "end: " + why + " after " + (System.Environment.TickCount - t0) + " ms", why == "not live" || why == "family started");
            }
        }

        // docs/202 §3.6.4 lot trips (unseenfbtrip / unseenfbtriphold): D's rules on top of the trip shortcut's own (townie, lot rules,
        // SafeSpot verdict for `spot`): the Sim's own task in a yielding context, Sim and spot unseen by the camera checks (§2), start
        // and the destination lot not proven in different flood colours (unknown = allowed, as the shortcuts always were), then the
        // walk-time hold (UfHold: in transit for our watchdogs and the harness, camera re-checked every sim-minute) and the same
        // checks again right before the caller places the Sim. false = the caller runs the game's own trip from where the Sim stands.
        static bool UfTripHold(Sim sim, Lot lot, Vector3 spot)
        {
            int[] c = PatchCounters();
            if (Simulator.CurrentTask != sim.ObjectId || !MayYield()) { c[347]++; return false; }
            if (UfWhy(sim) != 0 || !UfGoalUnseen(spot)) { c[345]++; return false; }
            if (UfSameComponent(sim, sim.Position, spot, lot) < 0) { c[346]++; return false; }
            if (!UfHold(sim, spot, UfPreCore.HoldMinutes((spot - sim.Position).Length())) || sim.HasBeenDestroyed || UfWhy(sim) != 0
                || !TownieUnseen(sim, null) || !UfGoalUnseen(spot) || LotManager.GetLotAtPoint(spot) != lot) { c[345]++; return false; }
            c[344]++;
            return true;
        }

        // phase B (StuckCheckRun): true = deferred (re-armed at the same round, nothing planned)
        static bool UfStuckDefer(object helper, Sim sim)
        {
            if (sim == null || !StuckNearReflect(helper.GetType())) return false;
            Lot lot = sim.LotCurrent;
            // the game's own early return and its world-lot branch (plans only for selectable Sims) plan nothing: leave them to it
            if (lot == null || lot.IsWorldLot || lot == Household.ActiveHouseholdLot || sim.Position != (Vector3)sStuckLoc.GetValue(helper) || sim.SimInRabbitHolePosture) return false;
            int[] c = PatchCounters();
            if (UfWhy(sim) != 0) { c[304]++; return false; }
            sStuckAlarm.Invoke(null, new object[] { sim, (uint)sStuckAttempt.GetValue(helper) });
            c[303]++;
            return true;
        }

        public partial class RouteBackoffHook
        {
            // docs/202 §3.1 channel (ts3native unseenfb.cpp UfMsg): GetOption2 with optHi top byte 0x83 (bits 63 + 57 + 56, no
            // RouteOption2 name), optLo 1 = MARK this route's next plan or job, 2 = CUT? (answers once, clears the mark).
            // The game and older ts3native answer false.
            const ulong kUfBits = 0x8300000000000000UL;
            bool UfSend(uint code) { return GetOption2((Sims3.SimIFace.Route.RouteOption2)(kUfBits | code)); }

            RoutePlanResult UfInner(int kind, Vector3 p)
            {
                switch (kind)
                {
                    case 0: return PlanBackoff(true);  // only UfPlanBody's marked plan comes here
                    case 1: return ReplanAsync();
                    case 2: return PlanFromPointAsync(p);
                    default: return ReplanFromPointAsync(p);
                }
            }

            // kind 0 Plan, 1 Replan, 2 PlanFromPoint, 3 ReplanFromPoint (the A10 kinds)
            // Issue #92 (docs/51 §386): every frame above the original plan counts against Mono's 63-frame limit for whatever the
            // plan calls back into (AboutToPlan / PostPlan callbacks, door asks, mod handlers). UfPlanBody only decides and returns
            // false when the plan is not ours, so the common plan runs from here: UfPlanBody and UfInner are off the stack then.
            RoutePlanResult UfPlan(int kind, Vector3 p)
            {
                int t0 = System.Environment.TickCount;
                UfLast last = new UfLast();
                last.Why = -1;
                RoutePlanResult r;
                ulong task = Simulator.CurrentTask.Value;
                sUfPlanDepth.Enter(task);  // §3.9.6: > 1 in this task = a plan inside another plan's callback (PostPlan / AboutToPlan)
                try
                {
                    if (!UfPlanBody(kind, p, ref last, out r))
                        switch (kind)  // UfInner, inlined
                        {
                            case 0: r = PlanBackoff(); break;
                            case 1: r = ReplanAsync(); break;
                            case 2: r = PlanFromPointAsync(p); break;
                            default: r = ReplanFromPointAsync(p); break;
                        }
                }
                finally { sUfPlanDepth.Exit(task); DiagSend(12, Handle); }  // docs/197 §7: this plan's scope ends here
                UrLearn ln;
                if (sUrLearn.Count > 0 && sUrLearn.TryGetValue(this, out ln))  // §3.9.7 (C): this plan's result is the lesson
                {
                    sUrLearn.Remove(this);
                    if (r.mType == RoutePlanResultType.Succeeded) sUrC.Set(ln.G, ln.S, true);
                    else if (r.mType == RoutePlanResultType.FailedBecauseNoPathToDestination && !last.Cut) sUrC.Set(ln.G, ln.S, false);
                }
                UfSlowPlanLog(kind, unchecked(System.Environment.TickCount - t0), last);
                UfLoopNote(Follower, r);  // docs/202 §5.7 E (unseenfbloopdet)
                if (sUfFill.Route != null && ReferenceEquals(sUfFill.Route, this))
                {
                    if (r.mType == RoutePlanResultType.Succeeded) UfFillNote();
                    sUfFill = default(UfFill);
                }
                return r;
            }

            // false = not ours: the caller (UfPlan) runs the plan; true = r is the plan's result
            bool UfPlanBody(int kind, Vector3 p, ref UfLast last, out RoutePlanResult r)
            {
                r = default(RoutePlanResult);
                bool pre = kind == 0 && (ShortcutOn("unseenfbpre") || ShortcutOn("unseenfbarrive") || ShortcutOn("unseenroute"));
                bool leave = kind == 0 && ShortcutOn("unseenfbleave");
                IScriptProxy proxy = Follower;
                Sim sim = proxy != null ? proxy.Target as Sim : null;
                bool ownCheck = sStuckIn || sInOwnCheck || sInRoutableCheck || sUfInLeg;
                Sim who = sim;  // docs/249: a vehicle by its driver, no driver = unseen (traffic, NPC boats); a pet or a stuck check by its Sim
                if (who == null) { Vehicle v = proxy != null ? proxy.Target as Vehicle : null; who = v != null ? v.Driver : null; }
                int driverlessWhy = 0;
                if (who == null && !ownCheck) { UfDriverless(proxy); driverlessWhy = UfDriverlessWhy(proxy); }  // scope-gap counters; the household rule for a route without a driver
                int why = who != null ? SafeUfWhy(who) : driverlessWhy;
                if (why == 0 && who != null && !ownCheck && ShortcutOn("goalhhexempt")) why = UfGoalWhy(who, kind);  // docs/197 §7
                if (sim != null && !ownCheck && sim.SimDescription != null && !sim.SimDescription.IsPet) last.ViewerOnly = sUfWhyViewerOnly;  // scope-gap counters: viewer-only 80 m
                DiagSend(why == 0 ? 11 : UnseenFallbackCore.SeenNpc(why) ? 13 : 10, Handle);  // docs/197 §7 [RouteAsync] KdExemptSeen: this route's scope for its START or sync plan (13 = seen NPC, KdSeenNpcCeilMs); first thing in every plan, these switches or not
                if (!pre && !leave && !ShortcutOn("unseenfbplan")) return false;
                // never: stuck-check probes (phase B), our own route checks, IsPointInLotSafelyRoutable, non-Sims, pets, D-leave's own leg
                if (sim == null || ownCheck || sim.SimDescription == null || sim.SimDescription.IsPet)
                    return false;
                int[] c = PatchCounters();
                last.Why = why; last.Sim = sim;
                if (why != 0 && why != UnseenFallbackCore.kGoalHousehold && sUrPlaced.Count > 0 && sUrPlaced.ContainsKey(sim.ObjectId.Value) && UrRepairSim(sim)) sUrPlaced.Remove(sim.ObjectId.Value);  // §3.9.7 (D)
                if (why != 0)
                {
                    if (leave && UnseenFallbackCore.SeenButMovable(why) && UfLeave(sim, GetApproximateOriginalDestination(), out r)) return true;
                    c[310]++;
                    return false;
                }
                if (!pre && !ShortcutOn("unseenfbplan")) return false;
                ulong id = sim.ObjectId.Value;
                Vector3 dest = GetApproximateOriginalDestination();
                int x = UnseenFallbackCore.Cell(dest.x), y = UnseenFallbackCore.Cell(dest.y), z = UnseenFallbackCore.Cell(dest.z);
                int t0 = System.Environment.TickCount;
                object prev;
                if (sUf.NoRetry(id, x, y, z, t0, out prev)) { c[309]++; sUfNotFail = kind == 0; r = FailFast((RoutePlanResult)prev); return true; }
                if (!sUf.MayMark(id, t0)) return false;
                if (pre)
                {
                    int v = UfPre(sim, dest, out r);
                    if (v == 2) { sUf.Cut(id, x, y, z, System.Environment.TickCount, r); return true; }  // proven unreachable: no-retry window
                    if (v == 1) return true;
                }
                if (!ShortcutOn("unseenfbplan")) return false;
                if (!UfSend(1)) { c[311]++; return false; }
                c[305]++;
                last.Marked = true;
                bool cut = false;
                // docs/202 §3.7: Overwatch's Stuck Check V2 (OnPostPlan, inside the original plan) must not count a cut plan as the
                // Sim's route failure: the same (Sim, task) hide owprobeplans uses for EA's stuck-check probes (OverwatchProbe.cs)
                ulong task = Simulator.CurrentTask.Value;
                sOwProbe.Enter(id, task);
                try { r = UfInner(kind, p); }
                finally { cut = UfSend(2); sOwProbe.Exit(id, task); }
                int ms = unchecked(System.Environment.TickCount - t0);
                if (ms > c[312]) c[312] = ms;
                if (!cut || r.mType == RoutePlanResultType.Succeeded) return true;
                c[306]++;
                last.Cut = true;
                UfGuardNote(id, "cut (kind " + kind + ")");
                UfNotAFailure(id, dest);
                if (kind != 0) sUfNotFail = false;  // only Route.Plan (PlanHook) reads it
                RoutePlanResult tr;
                if (kind == 0 && ShortcutOn("unseenfbteleport") && Simulator.CurrentTask == sim.ObjectId)
                {
                    // the teleport's plan sleeps this task on its route job: a set flag would go to the first other Sim's PlanHook
                    // that returns meanwhile (household too), whose result the watchdog then never sees, and this cut would count
                    sUfNotFail = false;
                    bool placed = UfTeleport(sim, dest, out tr);
                    sUfNotFail = true;
                    if (placed) { r = tr; return true; }
                }
                sUf.Cut(id, x, y, z, System.Environment.TickCount, r);
                return true;
            }

            // docs/197 §7 goalhhexempt: an unseen NPC whose route works for the household (the household lot as goal, a household
            // Sim as the target at its queue head, or a household Sim's current target) is not unseen for this plan: kGoalHousehold,
            // so no unseen feature takes the route and native gets the seen NPC scope. Only why 0 reaches here, so a household
            // target of the current interaction never does (UfWhy: kSocialSeen). A check that throws exempts.
            int UfGoalWhy(Sim who, int kind)
            {
                bool ok = false, lot = false, target = false, wanted = false;
                try
                {
                    Household h = Household.ActiveHousehold;
                    Lot home = Household.ActiveHouseholdLot;
                    Vector3 dest = GetApproximateOriginalDestination();
                    lot = home != null && dest != Vector3.Origin && dest != Vector3.Invalid && LotManager.GetLotAtPoint(dest) == home;
                    target = !lot && h != null && UfHeadHousehold(who, h);
                    wanted = !lot && !target && h != null && UfHouseholdWants(who, h);
                    ok = true;
                }
                catch (Exception) { }
                int reason = UnseenFallbackCore.GoalReason(ok, lot, target, wanted);
                if (reason == UnseenFallbackCore.kGoalNone) return 0;
                PatchCounters()[UnseenFallbackCore.kGoalCounter + reason]++;
                if (sUfGoalLogged < kUfGoalLogMax)
                {
                    sUfGoalLogged++;
                    DiagLine("goalhh: " + who.FullName + " kind " + kind + " exempt, " + (reason == UnseenFallbackCore.kGoalLot ? "household lot" : reason == UnseenFallbackCore.kGoalTarget ? "household target" : reason == UnseenFallbackCore.kGoalWanted ? "household Sim's target" : "check failed"));
                }
                return UnseenFallbackCore.kGoalHousehold;
            }

            // shortcut: the head only, not the rest of the queue (InteractionList is a boxed enumerator); walk it if a missed case shows up
            static bool UfHeadHousehold(Sim s, Household h)
            {
                var q = s.InteractionQueue;
                var i = q != null ? q.GetHeadInteraction() : null;
                Sim t = i != null ? i.Target as Sim : null;
                return t != null && t != s && (t.IsSelectable || t.Household == h);
            }

            // only the household's Sims, not the town; AllActors builds a new list, paid only by plans of Sims already why 0
            static bool UfHouseholdWants(Sim s, Household h)
            {
                foreach (Sim m in h.AllActors)
                {
                    var i = m != null ? m.CurrentInteraction : null;
                    if (i != null && ReferenceEquals(i.Target, s)) return true;
                }
                return false;
            }

            // docs/202 §3.5 D (switch unseenfbpre): an unseen townie's own Route.Plan, made by a RoutingComponent helper that follows
            // the route right after, to an unseen outdoor ground-level goal on another lot (community lot or its own home, no door
            // with a lock) plans nothing. Proofs first: the game's flood colours (ts3native) put the start and the goal in one
            // world component, and the cached SafeSpot verdicts (the game's IsPointInLotSafelyRoutable for this route profile)
            // say the goal, and a start on a lot, connect to their lot's edge. Then the Sim stands where it is for the walk time
            // (UfHold, in its own task, listed as in transit), is placed at the goal and plans the last metres from there: the
            // caller follows that short route as usual. 0 = not ours (plan normally), 1 = done (r = the short plan), 2 = proven
            // unreachable on land (r = no path, nothing searched; the caller's own failure branch runs).
            int UfPre(Sim sim, Vector3 dest, out RoutePlanResult r)
            {
                r = default(RoutePlanResult);
                int[] c = PatchCounters();
                // reject gates count 328-339 (UfRj) so a run shows which rule keeps D from acting
                // §3.9 unseenroute: the exactness proofs (verdict, known colour, lot kind, walk-only) are dropped; safety rules stay
                bool relaxed = ShortcutOn("unseenroute");
                // §3.9.6: another task's plan (a situation, alarm or another Sim's interaction routing this Sim through a follow
                // helper) acts when relaxed: the hold sleeps that task as the real DoRoute would; never inside another plan
                bool other = Simulator.CurrentTask != sim.ObjectId;
                if (dest == Vector3.Origin || dest == Vector3.Invalid || (other && !relaxed)) return UfRj(328);
                if (sUfPlanDepth.Depth(Simulator.CurrentTask.Value) > 1) { c[648]++; return UfRj(328); }
                Vector3 start = sim.Position;
                float dist = (dest - start).Length();
                if (dist < UfPreCore.kMinDistM) return UfRj(329);
                if (!UfPreSim(sim, relaxed)) return relaxed ? UrRj(330, UfPreSimWhy(sim, true)) : UfRj(330);
                if (!TownieUnseen(sim, null)) return UfRj(331);
                if (UfCarOrBoat(relaxed)) return UfRj(332);
                // goal in view: D-arrive skips only the hidden leg (§3.6)
                if (!UfGoalUnseen(dest)) return ShortcutOn("unseenfbarrive") ? UfArrive(sim, start, dest, dist, out r) : 0;
                if (!relaxed && !ShortcutOn("unseenfbpre")) return 0;
                Lot goalLot = LotManager.GetLotAtPoint(dest);
                bool startOut = UfOutdoorGround(start);
                if (goalLot == sim.LotCurrent && !(relaxed && startOut)) return relaxed ? UrRj(333, "start indoors") : UfRj(333);
                if (relaxed)
                {
                    string lw = UfRelaxedLotWhy(goalLot, "goal", true) ?? UfRelaxedLotWhy(sim.LotCurrent, "start", UrCore.StartLotCounts(startOut));
                    if (lw != null) return UrRj(334, lw);
                }
                else if (!UfPreLot(sim, goalLot)) return UfRj(334);
                if (!UfOutdoorGround(dest)) return relaxed ? UrRj(335, "goal indoors or raised") : UfRj(335);
                if (!UfFollows()) return UfRj(336);
                if (!MayYield()) return relaxed ? UrRj(337, "no yield") : UfRj(337);
                int same = UfSameComponent(sim, start, dest, goalLot);
                if (same < 0 && !GetOption(RouteOption.EnableWaterPlanning) && !GetOption2(RouteOption2.EnablePlanningAsBoat))
                {
                    c[315]++;
                    UfNotAFailure(sim.ObjectId.Value, dest);
                    r = FailFast(RoutePlanResultType.FailedBecauseNoPathToDestination);
                    return 2;
                }
                RouteProfile prof = ProfileOf(sim);
                Lot startLot = sim.LotCurrent;
                if (same < 0 || (same == 0 && !relaxed)) { c[314]++; return UfRj(338); }
                // §3.9.7b (2026-11-01): a goal on an object (View on a statue or tree: RouteToPointRadialRange's original destination
                // is the object's own position, inside its footprint) is not routable itself; relaxed places the Sim on the first
                // routable outdoor point on the line back towards the start (1-8 m) and the short plan from there does the last
                // metres. sv-ur-car-loop (unseenroute3): every View plan stopped here (339 +21, 314 +19 in the on arms, 0 acts).
                Vector3 place = dest;
                if (!prof.Ok || !(IsPointRoutable(dest) || (relaxed && UfNearGoal(dest, start, out place))))
                {
                    c[314]++;
                    return relaxed ? UrRj(339, prof.Ok ? "goal unroutable, no spot near" : "no route profile") : UfRj(339);
                }
                bool? gv = goalLot != null && !goalLot.IsWorldLot ? UfVerdict(sim, goalLot, prof, dest) : true;
                bool? sv = startLot != null && !startLot.IsWorldLot ? UfVerdict(sim, startLot, prof, start) : true;
                if (gv == false || sv == false) { c[314]++; return 0; }
                if (relaxed && !UrReachable(sim, goalLot, startLot, dest, same, gv)) return 0;  // §3.9.7 B / C
                if ((!gv.HasValue || !sv.HasValue) && !relaxed)
                {
                    // no verdict: no sim-thread routability plan; the normal plan runs and its success fills D's list
                    c[326]++;
                    sUfFill = new UfFill { Route = this, GoalLot = gv.HasValue ? null : goalLot, StartLot = sv.HasValue ? null : startLot, Goal = dest, Start = start, Prof = prof };
                    return 0;
                }
                float minutes = relaxed ? UfPreCore.TravelMinutes(dist, UfFastLegs()) : UfPreCore.HoldMinutes(dist);
                if (relaxed)
                {
                    if (minutes < UfPreCore.HoldMinutes(dist)) c[644]++;
                    // agent camera_to_tagged (snap-back repro): the held Sim, and the goal as a point
                    AppDomain.CurrentDomain.SetData("ts3fixes.unseenroute.tagged", sim.ObjectId.Value);
                    AppDomain.CurrentDomain.SetData("ts3fixes.unseenroute.goal", new float[] { dest.x, dest.y, dest.z });
                }
                long held0 = Sims3.Gameplay.Utilities.SimClock.CurrentTicks;
                bool ghost = relaxed && UrGhostStart(sim), held;  // §3.9.5: no +0x918 while it travels unseen (ts3native [UnseenGhost])
                try { held = UfHold(sim, dest, minutes); }
                finally { if (ghost) UrGhostEnd(sim); }  // every exit: off the native set first, then the footprint back once
                // after the hold, right before the move: the Sim and the goal still unseen (same tick), the goal still free ground
                if (!held || sim.HasBeenDestroyed || UfWhy(sim) != 0 || !TownieUnseen(sim, null) || !UfGoalUnseen(dest) || !IsPointRoutable(place))
                {
                    c[316]++;
                    if (relaxed && sUfHoldWhy == "time up" && !UfGoalUnseen(dest)) sUfHoldWhy = "seen or moved";  // the goal turned seen at the last check
                    if (relaxed)
                        return UfSnap(sim, start, dest, dist, UfPreCore.TripShare(Sims3.Gameplay.Utilities.SimClock.CurrentTicks - held0,
                            Sims3.Gameplay.Utilities.SimClock.ConvertToTicks(minutes, Sims3.Gameplay.Utilities.TimeUnit.Minutes)), out r);
                    return 0;  // plans normally from where it stands: it lost only the hold, which the walk would have used too
                }
                if (relaxed)
                {
                    c[641]++;
                    if (!gv.HasValue || !sv.HasValue) c[642]++;
                    if (same == 0) c[643]++;
                    if (other) c[647]++;
                    if (!sim.SimDescription.TeenOrAbove) c[651]++;
                }
                Vector3 fwd = dest - start;
                fwd.y = 0f;
                fwd = fwd.LengthSqr() > 0.01f ? fwd / fwd.Length() : sim.ForwardVector;
                PlaceSim(sim, place, fwd, "unseenfbpre route goal");
                if (relaxed)
                {
                    UfMoveGroup(sim, place, fwd);
                    if (sUrPlaced.Count >= 256) sUrPlaced.Clear();
                    sUrPlaced[sim.ObjectId.Value] = true;  // §3.9.7 (D): checked when it is seen and plans again
                    UrEv("placement", sim.ObjectId.Value);
                }
                c[313]++;
                if (relaxed) UrEv("act", sim.ObjectId.Value);
                ShortcutLog(sim.FullName + " => route goal (unseenfbpre, " + (int)dist + " m, held " + (int)minutes + " sim-min)");
                r = PlanBackoff();  // the last metres (slot facing, radial range) from the goal: a short plan the caller follows
                return 1;
            }

            // §3.9.7 (Rodney 2026-10-31, B + C): relaxed without a SafeSpot verdict for the goal only when (B) the goal is on the
            // street or a public lot in the start's flood colour, else (C) the learned answer for (goal lot or street area,
            // start side): unknown = this trip plans for real and its result is learned (UfPlan), unreachable = plan normally.
            // A goal with a verdict (true here; false refused earlier) acts. false = plan normally.
            bool UrReachable(Sim sim, Lot goalLot, Lot startLot, Vector3 dest, int same, bool? gv)
            {
                int[] c = PatchCounters();
                bool street = goalLot == null || goalLot.IsWorldLot;
                if (UrCore.BPass(same, street, !street && goalLot.IsCommunityLot)) { c[652]++; return true; }
                if (gv == true) return true;
                bool sStreet = startLot == null || startLot.IsWorldLot;
                ulong gk = UrCore.GoalKey(street ? 0UL : goalLot.LotId, street, dest.x, dest.z), sk = UrCore.StartKey(sStreet ? 0UL : startLot.LotId, sStreet);
                bool? cv = sUrC.Get(gk, sk);
                if (cv == true) { c[654]++; return true; }
                if (cv == false) { c[314]++; return false; }
                c[653]++;
                if (sUrLearn.Count >= 256) sUrLearn.Clear();
                sUrLearn[this] = new UrLearn { Route = this, G = gk, S = sk };
                return false;
            }

            // §3.9.6: the members of the leader's grouping situation are placed around the goal with it (1.5 m ring, routable
            // points only; a member with no point follows from where it stands, exactly)
            void UfMoveGroup(Sim sim, Vector3 dest, Vector3 fwd)
            {
                Sims3.Gameplay.Situations.GroupingSituation g = sim.GetSituationOfType<Sims3.Gameplay.Situations.GroupingSituation>();
                if (g == null) return;
                int i = 0;
                foreach (Sim m in g.Participants)
                {
                    if (m == null || m == sim || m.HasBeenDestroyed) continue;
                    double a = ++i * 1.2566;  // 72 degrees apart
                    Vector3 q = new Vector3(dest.x + 1.5f * (float)Math.Cos(a), dest.y, dest.z + 1.5f * (float)Math.Sin(a));
                    q.y = World.GetTerrainHeight(q.x, q.z);
                    if (IsPointRoutable(q)) PlaceSim(m, q, fwd, "unseenroute group");
                }
                if (i > 0) PatchCounters()[649]++;
            }

            // §3.9.7b: the first routable outdoor point 1-8 m from an unroutable goal, on the line towards the start
            bool UfNearGoal(Vector3 dest, Vector3 start, out Vector3 p)
            {
                p = dest;
                Vector3 dir = start - dest;
                dir.y = 0f;
                float len = dir.Length();
                if (len < 0.01f) return false;
                dir = dir / len;
                for (int i = 0; ; i++)
                {
                    float d = UrCore.NearGoalAt(i);
                    if (d < 0f) break;
                    Vector3 q = dest + dir * d;
                    q.y = World.GetTerrainHeight(q.x, q.z);
                    if (IsPointRoutable(q) && UfOutdoorGround(q)) { p = q; return true; }
                }
                return false;
            }

            bool UfCarOrBoat(bool relaxed)
            {
                return UfPreCore.VehicleRoute(GetOption(RouteOption.BeginAsCar), GetOption(RouteOption.DisablePlanningAsPedestrian),
                    GetOption2(RouteOption2.BeginAsBoat), relaxed);
            }
            // §3.9: the planner may pick a drive, a subway / hover-train leg or a boat for this route
            bool UfFastLegs()
            {
                return GetOption(RouteOption.EnablePlanningAsCar) || GetOption(RouteOption.EnableSubwayPlanning)
                    || GetOption2(RouteOption2.EnableHoverTrainPlanning) || GetOption2(RouteOption2.EnablePlanningAsBoat);
            }

            // §3.9 snap-back: a relaxed hold ended because the goal turned seen while the Sim itself is still unseen. Place it at the
            // first point from the share of the trip done back towards the start that is unseen, on the street, outdoor ground,
            // routable and in the start's colour, then plan exactly from there (the player sees it walk in). 1 = placed (r = the
            // plan from there), 0 = no such point or the Sim is seen: it plans from where it stands, as before (counted).
            int UfSnap(Sim sim, Vector3 start, Vector3 dest, float dist, float share, out RoutePlanResult r)
            {
                r = default(RoutePlanResult);
                int[] c = PatchCounters();
                if (sUfHoldWhy == "seen or moved" && !sim.HasBeenDestroyed && (sim.Position - start).LengthSqr() <= 0.01f && UfWhy(sim) == 0
                    && UfColourA(start, sim.LotCurrent))
                    for (int i = 0; ; i++)
                    {
                        float d = UfPreCore.SnapAt(i, share, dist);
                        if (d < 0f) break;
                        Vector3 h = start + (dest - start) * (d / dist);
                        h.y = World.GetTerrainHeight(h.x, h.z);
                        Lot hl = LotManager.GetLotAtPoint(h);
                        if ((hl != null && !hl.IsWorldLot) || !UfGoalUnseen(h) || !UfOutdoorGround(h) || !IsPointRoutable(h) || UfColourB(h, null) != 1) continue;
                        Vector3 fwd = dest - h;
                        fwd.y = 0f;
                        fwd = fwd.LengthSqr() > 0.01f ? fwd / fwd.Length() : sim.ForwardVector;
                        PlaceSim(sim, h, fwd, "unseenroute snap-back");
                        c[645]++;
                        ShortcutLog(sim.FullName + " => snap-back (unseenroute, " + (int)d + " of " + (int)dist + " m)");
                        r = PlanBackoff();
                        return 1;
                    }
                c[646]++;
                return 0;
            }

            // docs/202 §3.6 hand-off point H between `from` and `to` (dist apart): 10 m steps from `from`, the first point that is
            // out of view with its lot (UfGoalUnseen), on the street (world lot: never inside a lot), outdoors on the ground,
            // routable for this route and in colour A (set by the caller). shortcut: straight-line candidates only, no R-node snap
            // or lot entry points; upgrade if unseenfbarrive/leave_no_spot stays high in game.
            bool UfHandOff(Vector3 from, Vector3 to, float dist, out Vector3 h)
            {
                for (int i = 0; i < UfPreCore.HandOffSteps(dist); i++)
                {
                    float s = UfPreCore.HandOffAt(i);
                    h = from + (to - from) * (s / dist);
                    h.y = World.GetTerrainHeight(h.x, h.z);
                    Lot hl = LotManager.GetLotAtPoint(h);
                    if ((hl == null || hl.IsWorldLot) && UfGoalUnseen(h) && UfOutdoorGround(h) && IsPointRoutable(h) && UfColourB(h, null) == 1) return true;
                }
                h = Vector3.Invalid;
                return false;
            }

            // §3.6 D-arrive (switch unseenfbarrive): the Sim is unseen, the goal is in view. Hold for the walk to H (the first unseen
            // street point from the goal towards the Sim), place the Sim at H facing the goal, then the real plan H -> goal: the
            // player sees it walk in. Proofs for the skipped leg: same colour start / H / goal, and a start on a lot has a verdict.
            int UfArrive(Sim sim, Vector3 start, Vector3 dest, float dist, out RoutePlanResult r)
            {
                r = default(RoutePlanResult);
                int[] c = PatchCounters();
                if (dist < UfPreCore.kSplitMinDistM) return UfRj(329);
                if (!UfFollows()) return UfRj(336);
                if (!MayYield()) return UfRj(337);
                RouteProfile prof = ProfileOf(sim);
                Lot sl = sim.LotCurrent;
                if (!prof.Ok) return UfRj(339);
                if (sl != null && !sl.IsWorldLot)
                {
                    bool? sv = UfVerdict(sim, sl, prof, start);
                    if (sv == false) { c[320]++; return 0; }
                    if (!sv.HasValue) { c[326]++; sUfFill = new UfFill { Route = this, StartLot = sl, Start = start, Prof = prof }; return 0; }
                }
                Vector3 h;
                if (!UfColourA(start, sl) || UfColourB(dest, LotManager.GetLotAtPoint(dest)) != 1) { c[320]++; return UfRj(338); }
                if (!UfHandOff(dest, start, dist, out h)) { c[320]++; return 0; }
                float minutes = UfPreCore.HoldMinutes((h - start).Length());
                if (!UfHold(sim, h, minutes) || sim.HasBeenDestroyed || UfWhy(sim) != 0 || !TownieUnseen(sim, null) || !UfGoalUnseen(h) || !IsPointRoutable(h))
                {
                    c[316]++;
                    return 0;
                }
                Vector3 fwd = dest - h;
                fwd.y = 0f;
                fwd = fwd.LengthSqr() > 0.01f ? fwd / fwd.Length() : sim.ForwardVector;
                PlaceSim(sim, h, fwd, "unseenfbarrive hand-off");
                c[319]++;
                ShortcutLog(sim.FullName + " => hand-off (unseenfbarrive, " + (int)(h - start).Length() + " m skipped, held " + (int)minutes + " sim-min)");
                r = PlanBackoff();  // the visible last leg H -> goal, planned and walked as usual
                return 1;
            }

            // §3.6 D-leave (switch unseenfbleave): the Sim starts in view (not the household, not held), the goal qualifies for D
            // (unseen, community lot or own home without locks, outdoor ground, verdict known). The Sim walks a normal short leg to
            // H (the first unseen street point towards the goal; its own route, planned and followed here in its own task), then,
            // still unseen at H, is held for the rest of the walk and placed at the goal; the real plan from the goal is returned.
            // false = plan normally from where the Sim stands (at the start, or at H after the visible leg: nothing visible lost).
            bool UfLeave(Sim sim, Vector3 dest, out RoutePlanResult r)
            {
                r = default(RoutePlanResult);
                int[] c = PatchCounters();
                if (dest == Vector3.Origin || dest == Vector3.Invalid || Simulator.CurrentTask != sim.ObjectId) return UfNo(328);
                if (sim.Posture != sim.Standing) return UfNo(331);
                if (!UfPreSim(sim, false)) return UfNo(330);
                if (UfCarOrBoat(false)) return UfNo(332);
                Vector3 start = sim.Position;
                float dist = (dest - start).Length();
                Lot goalLot = LotManager.GetLotAtPoint(dest);
                if (dist < UfPreCore.kSplitMinDistM) return UfNo(329);
                if (goalLot == sim.LotCurrent) return UfNo(333);
                if (!UfPreLot(sim, goalLot)) return UfNo(334);
                if (!UfGoalUnseen(dest)) return UfNo(340);
                if (!UfOutdoorGround(dest)) return UfNo(335);
                if (!UfFollows()) return UfNo(336);
                if (!MayYield()) return UfNo(337);
                RouteProfile prof = ProfileOf(sim);
                if (!IsPointRoutable(dest) || !prof.Ok) return UfNo(339);
                bool? gv = UfVerdict(sim, goalLot, prof, dest);
                if (gv == false) { c[322]++; return false; }
                if (!gv.HasValue) { c[326]++; sUfFill = new UfFill { Route = this, GoalLot = goalLot, Goal = dest, Prof = prof }; return false; }
                Vector3 h;
                if (!UfColourA(start, sim.LotCurrent) || UfColourB(dest, goalLot) != 1) { c[322]++; return UfNo(338); }
                if (!UfHandOff(start, dest, dist, out h)) { c[322]++; return false; }
                bool walked = false;
                sUfInLeg = true;
                try
                {
                    Route leg = sim.CreateRoute();
                    leg.PlanToPoint(h);
                    walked = leg.PlanResult.Succeeded() && sim.DoRoute(leg) && (sim.Position - h).Length() < 2f;
                }
                finally { sUfInLeg = false; }
                if (!walked || sim.HasBeenDestroyed || UfWhy(sim) != 0 || !TownieUnseen(sim, null) || !UfGoalUnseen(dest)) { c[316]++; return false; }
                float minutes = UfPreCore.HoldMinutes((dest - sim.Position).Length());
                if (!UfHold(sim, dest, minutes) || sim.HasBeenDestroyed || UfWhy(sim) != 0 || !TownieUnseen(sim, null) || !UfGoalUnseen(dest) || !IsPointRoutable(dest))
                {
                    c[316]++;
                    return false;
                }
                Vector3 fwd = dest - h;
                fwd.y = 0f;
                fwd = fwd.LengthSqr() > 0.01f ? fwd / fwd.Length() : sim.ForwardVector;
                PlaceSim(sim, dest, fwd, "unseenfbleave route goal");
                c[321]++;
                ShortcutLog(sim.FullName + " => route goal (unseenfbleave, " + (int)(dest - h).Length() + " m skipped, held " + (int)minutes + " sim-min)");
                r = PlanBackoff();
                return true;
            }

            // §3.2 item 1: own-task Route.Plan only (the caller follows this route next)
            bool UfTeleport(Sim sim, Vector3 dest, out RoutePlanResult r)
            {
                r = default(RoutePlanResult);
                int[] c = PatchCounters();
                if (dest == Vector3.Origin || dest == Vector3.Invalid || (sim.Position - dest).Length() < kBackoffMinDistance
                    || UfWhy(sim) != 0 || !TownieUnseen(sim, null) || !UfGoalUnseen(dest) || !IsPointRoutable(dest))  // checked again here, right before the move
                {
                    c[308]++;
                    return false;
                }
                PlaceSim(sim, dest, sim.ForwardVector, "unseenfb route goal");
                c[307]++;
                ShortcutLog(sim.FullName + " => route goal (unseenfb, cut plan)");
                bool marked = UfSend(1);
                try { r = PlanBackoff(marked); }
                finally { if (marked) UfSend(2); }
                UfGuardNote(sim.ObjectId.Value, "teleport, next plan " + r.mType);  // §5.7: the post-teleport plan result
                return true;
            }
        }
    }
}
