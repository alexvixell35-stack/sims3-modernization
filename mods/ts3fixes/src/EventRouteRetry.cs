using System;
using System.Reflection;
using System.Collections.Generic;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.ObjectComponents;
using Sims3.Gameplay.Objects.RabbitHoles;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;
using Sims3.SimIFace.RouteDestinations;

namespace Ts3Fixes
{
    // ceremonyspot + eventcrowdring + eventrouteretry (tunings kCeremonySpot / kEventCrowdRing / kEventRouteRetry, off by
    // default; docs/546 §11, docs/548). EA bug fixes for the City Hall award ceremony. Only NPCs are ever moved; the honoree (a household Sim) only walks.
    // Harness check: EventCeremonyCheck (EventChecks.cs).
    //
    // What EA does [verified in the decompiled game code unless marked]:
    // - CityHall.CreateCelebration makes the award jig (SocialJigTwoPerson, model hidden, footprint not) at the rabbit hole
    //   proxy's RoutingSlot_0 (the entrance), once at booking, 60 sim min before the ceremony; it stays until
    //   GreatSuccessCelebrationCleanup (up to 120 min more).
    // - The crowd is NPC only (CityHall.Celebrate tests IsNPC). It gathers with RouteToGatheringArea: a radial goal centred
    //   on that same RoutingSlot_0, radius 0-6 m (kMinProtestRadius 0), 45 degree cone, 0.5 m spacing, so crowd Sims stand
    //   on and around the jig. Once the honoree is on the lot each runs Sim.Celebrate at CriticalNPCBehavior, routing to
    //   1-3 m around the honoree.
    // - GiveAward.Run: each side plans RouteToJigA/B (ignores the jig and holds its lock key, no ignore id passed) and
    //   DoRoutes. DoRoute ends with SimRoutingComponent's CheckForFootprintsNearGoals block: a Sim on the end point gets
    //   PushSims, which a Sim in Celebrate refuses (not IBePushedCanCancel); the BlockedByPeople replan has no other goal
    //   (one slot per actor) and fails: get-away route, route-failure balloon (the "route tantrum"), DoRoute false.
    //   GiveAward.Run returns false and the master's Cleanup ends the ceremony (awardkeep turns that into a pickup).
    //
    // ceremonyspot (EA bug, verified in game 2026-11-14, Appaloosa Plains): worlds with the combined City Hall / police /
    // military building (ComboCityhallPoliceMilitary) book ceremonies on its inner CityHall (RewardsManager picks a random
    // RabbitHoleType.CityHall; the combo itself is another type and its own CreateCelebration returns false). That CityHall's
    // RabbitHoleProxy is the combo, whose model has no RoutingSlot_0, so GetPositionOfSlot gives the world origin: the jig
    // and the crowd's gathering goal land at (0, y, 0), the honoree's route to the jig fails (FailedBecauseNoPathToDestination,
    // blocker Terrain, goal 0.0,34.7,0.0 in the log) and the ceremony is lost every time. The fix anchors both on the
    // building's real entrance instead (EvrAnchor -> RhSlotOrEntrance, ComboRabbitHole.cs: the proxy's first EnterSlot,
    // kRhEntranceOut metres out) whenever RoutingSlot_0 reads as the origin; a City Hall with a real RoutingSlot_0 is
    // untouched. The same anchor serves comboprotest's protest routes (docs/551). NPC scope everywhere: IsNpcSim.
    //
    // eventcrowdring (prevention; changes where the NPC crowd stands at every ceremony): while a ceremony is booked
    // (IsCelebrationActive and CeremonyAwardInfo set; protests never overlap one, CanProtest tests !IsCelebrationActive),
    // RouteToGatheringArea for an NPC plans the same kind of radial goal from the same slot with a clear ring: min radius
    // = the jig's distance from the slot + its reach + kRingGap, depth kRingDepth, a kRingCone cone and kRingSpacing
    // spacing. A failed plan, or a household / unknown Sim, runs EA's route unchanged.
    //
    // eventrouteretry (failure path only): the hook runs EA's Run first and does nothing when it returns true or when the
    // Sim reached its slot (then the failure was the sync, not the route). Otherwise a bounded ladder runs inside the same
    // Run (before Cleanup), each rung followed by EA's Run again:
    //   rung 0  clear the goal slot: NPCs on it get EA's BePushed with push-out-of-interaction allowed (priority Fire,
    //           above the crowd's CriticalNPCBehavior), up to kEvrPushWaitMin; NPCs still there are moved once per
    //           ceremony to a SafeSpot (the trip-shortcut placement) clear of the jig, its slots, the entrance and every
    //           other moved Sim. Skipped when no NPC was there.
    //   rung 1  master (the NPC mayor): placed on slot A. Receiver (the honoree): EA's PlaceJig(mayor, honoree) re-places
    //           the jig so slot B is where the honoree stands; both new slots are cleared and the mayor, once no longer
    //           routing, is placed on the new slot A.
    //   rung 2  receiver only: EA's PlaceJigNearActorAFacingActorB(mayor, honoree), a new spot at the mayor.
    // Then EA's failure path (Cleanup; awardkeep's pickup when that switch is on). The ladder stops on a cancel exit
    // reason, when the partner's linked GiveAward is gone, when the ceremony closed, or kEvrDeadlineMin after the first
    // failure (the partner waits at most 15 + 30 sim min in StartSync). Re-placing the jig between RouteToJigA and B is
    // safe because the master enters the state machine without a position and the receiver re-routes after the move
    // [inferred: moving a Sim idling in a sync wait is safe, as the trip shortcuts move Sims about to route].
    //
    // Not done: placing the jig late (at ceremony start). A save made in the hours before a ceremony would hold an
    // out-of-world jig that vanilla cannot use if the mod is removed; the crowd ring removes the same cause without that.
    // Known gap: Sim.Celebrate brings the crowd to 1-3 m around the honoree, so it follows the honoree toward the jig;
    // rung 0 covers that case.
    //
    // Counters (registry fixes-2326): [2326] ladders entered, [2327] crowd ring routes, [2328] jig re-placed,
    // [2329] NPCs moved (mayor or crowd), [2330] Runs that succeeded after a retry, [2331] errors, [2332] NPCs pushed,
    // [2333] ceremony spots moved off the origin, [2334] crowd routes re-anchored without the ring.
    public static partial class Fixes
    {
        // ---- tuning ----
        const float kRingGap = 2.5f;          // clear metres between the jig's reach and the crowd
        const float kRingDepth = 6f;          // depth of the crowd ring
        const float kRingCone = 135f;         // degrees, centred on the entrance's forward (EA: 45)
        const float kRingSpacing = 1f;        // metres between crowd Sims (EA: 0.5)
        const float kEvrAtSlot = 0.5f;        // a Sim this close to its slot reached it: the failure was not the route
        const float kEvrClearRadius = 0.9f;   // NPCs this close to a goal slot block its end point
        const float kEvrKeepOut = 2.0f;       // a moved NPC lands at least this far from the jig, its slots and the entrance
        const float kEvrPushWaitMin = 2f;     // sim minutes for pushed NPCs to walk off
        const float kEvrMayorWaitMin = 5f;    // sim minutes for a routing mayor to arrive before being placed
        const float kEvrDeadlineMin = 20f;    // the whole ladder, inside the partner's 30 min sync wait
        const int kEvrRungs = 3;
        const ExitReason kEvrStop = ExitReason.Canceled | ExitReason.HigherPriorityNext | ExitReason.PriorityOverride | ExitReason.Finished | ExitReason.SynchronizationFailed;

        // ---- state: NPCs moved per ceremony (keyed by its CeremonyAwardInfo object), one move each, and their spots ----
        static object sEvrCeremony;
        static readonly List<ulong> sEvrMoved = new List<ulong>();
        static readonly List<Vector3> sEvrSpots = new List<Vector3>();

        static void ApplyEventRouteRetry()
        {
            Patch(typeof(CityHall), "CreateCelebration", typeof(CeremonySpotHook), "CreateHook", "CreateOrig");
            Patch(typeof(CityHall), "RouteToGatheringArea", typeof(EventCrowdRingHook), "RouteToGatheringAreaHook", "RouteToGatheringAreaOrig");
            Patch(typeof(CityHall.GiveAward), "Run", typeof(EventRouteRetryHook), "RunHook", "RunOrig");
        }

        // ---- hooks ----

        public class CeremonySpotHook
        {
            public bool CreateHook(Sim guestOfHonor, uint guid)
            {
                bool r = CreateOrig(guestOfHonor, guid);
                if (r) EvrMoveSpot((CityHall)(object)this);
                return r;
            }
            public bool CreateOrig(Sim guestOfHonor, uint guid) { return false; } // receives CityHall.CreateCelebration's original body
        }

        public class EventCrowdRingHook
        {
            public bool RouteToGatheringAreaHook(Sim s)
            {
                Route r = EvrCrowdRoute((CityHall)(object)this, s);
                return r != null ? s.DoRoute(r) : RouteToGatheringAreaOrig(s);
            }
            public bool RouteToGatheringAreaOrig(Sim s) { return false; } // receives CityHall.RouteToGatheringArea's original body
        }

        public class EventRouteRetryHook
        {
            public bool RunHook()
            {
                bool r = RunOrig();
                if (r || !ShortcutOn("eventrouteretry")) return r;
                CityHall.GiveAward ga = (CityHall.GiveAward)(object)this;
                DateAndTime start = SimClock.CurrentTime();
                int rung = 0;
                while (rung < kEvrRungs)
                {
                    rung = EvrRung(ga, rung, start);  // the next rung to try after this retry, or -1 to stop
                    if (rung < 0) break;
                    r = RunOrig();
                    if (r) { PatchCounters()[2330]++; break; }
                }
                return r;
            }
            public bool RunOrig() { return false; } // receives CityHall.GiveAward.Run's original body
        }

        // ---- ceremonyspot ----

        // The point the ceremony and its crowd are built around: EA's RoutingSlot_0, or, when fix is set and that slot reads
        // as the world origin (a combo rabbit hole's model has none), the building's entrance (RhSlotOrEntrance,
        // ComboRabbitHole.cs). False when there is no proxy. moved = the entrance was used instead of EA's slot.
        static bool EvrAnchor(CityHall hall, bool fix, out Vector3 c, out Vector3 fwd, out bool moved)
        {
            c = fwd = Vector3.Zero;
            moved = false;
            RabbitHole proxy = hall.RabbitHoleProxy;
            if (proxy == null) return false;
            moved = RhSlotOrEntrance(proxy, Slot.RoutingSlot_0, fix, out c, out fwd);
            return true;
        }

        // After CreateCelebration: a jig EA put at the world origin goes next to the real entrance (same search as EA's).
        static void EvrMoveSpot(CityHall hall)
        {
            try
            {
                SocialJigTwoPerson jig = hall.AwardJig;
                Vector3 c, fwd;
                bool moved;
                if (jig == null || !EvrAnchor(hall, ShortcutOn("ceremonyspot"), out c, out fwd, out moved) || !moved) return;
                GlobalFunctions.FindGoodLocationNearby(jig, ref c, ref fwd, true);  // as EA; on failure the entrance point itself still beats the origin
                jig.SetPosition(c);
                jig.SetForward(fwd);
                PatchCounters()[2333]++;
            }
            catch (Exception e) { EvrError("move spot", e); }
        }

        // ---- crowd: eventcrowdring, and the entrance anchor (ceremonyspot for the crowd, comboprotest for protesters) ----

        // The gathering route to use instead of EA's, or null for EA's own route. With eventcrowdring and a booked ceremony:
        // the clear ring (NPCs only). Otherwise, when the anchor moved to the entrance: EA's own goal shape (0-6 m,
        // 45 degrees, 0.5 m) around the entrance instead of the origin; for a ceremony crowd (ceremonyspot) NPCs only, for
        // a protest (comboprotest) every Sim, since only the goal changes and the Sim still walks there.
        static Route EvrCrowdRoute(CityHall hall, Sim s)
        {
            try
            {
                bool npc = IsNpcSim(s);
                bool protest = hall.IsProtestActive && ShortcutOn("comboprotest");
                if (!npc && !protest) return null;
                Vector3 c, fwd;
                bool moved;
                if (!EvrAnchor(hall, protest || ShortcutOn("ceremonyspot"), out c, out fwd, out moved)) return null;
                bool ring = npc && ShortcutOn("eventcrowdring") && hall.IsCelebrationActive && hall.CeremonyAwardInfo != null;
                if (!ring && !moved) return null;
                if (!moved) c.y = World.GetTerrainHeight(c.x, c.z);
                RadialRangeDestination d = new RadialRangeDestination();
                d.mCenterPoint = c;
                d.mConeVector = fwd;
                d.mFacingPreference = RouteOrientationPreference.TowardsObject;
                d.ScoreFunctionWeights[6] = 1f;  // as EA's gathering goal
                if (ring)
                {
                    d.mfMinRadius = EvrJigReach(hall.AwardJig, c) + kRingGap;
                    d.mfMaxRadius = d.mfMinRadius + kRingDepth;
                    d.mfConeAngle = kRingCone * (float)Math.PI / 180f;
                    d.mfPreferredSpacing = kRingSpacing;
                }
                else
                {
                    d.mfMinRadius = EvrTunable("kMinProtestRadius", 0f);
                    d.mfMaxRadius = EvrTunable("kMaxProtestRadius", 6f);
                    d.mfConeAngle = EvrTunable("kProtestRouteConeAngle", 45f) * (float)Math.PI / 180f;
                    d.mfPreferredSpacing = 0.5f;
                }
                Route r = s.CreateRoute();
                r.AddDestination(d);
                r.Plan();
                if (!r.PlanResult.Succeeded()) return null;
                PatchCounters()[ring ? 2327 : protest ? 2338 : 2334]++;
                return r;
            }
            catch (Exception e) { EvrError("crowd route", e); return null; }
        }

        // Counts an error ([2331]) and keeps the first five in the applied log, with where and what, for the harness to read.
        static void EvrError(string where, Exception e)
        {
            if (++PatchCounters()[2331] <= 5) AppliedLog().Add("eventrouteretry error in " + where + ": " + e.GetType().Name + ": " + e.Message);
        }

        // EA's private CityHall tunable (the gathering goal's shape), or its shipped default when it can't be read.
        static float EvrTunable(string name, float fallback)
        {
            FieldInfo f = typeof(CityHall).GetField(name, BindingFlags.NonPublic | BindingFlags.Static);
            return f != null && f.FieldType == typeof(float) ? (float)f.GetValue(null) : fallback;
        }

        // How far the jig and its two slots reach from the entrance slot c (1 m when there is no jig in the world).
        static float EvrJigReach(SocialJigTwoPerson jig, Vector3 c)
        {
            if (jig == null || !jig.InWorld) return 1f;
            Vector3 j = jig.Position;
            j.y = c.y;
            float da = (jig.GetPositionOfSlot(SocialJigTwoPerson.RoutingSlots.SimA) - jig.Position).Length();
            float db = (jig.GetPositionOfSlot(SocialJigTwoPerson.RoutingSlots.SimB) - jig.Position).Length();
            return (j - c).Length() + Math.Max(da, db);
        }

        // ---- eventrouteretry ----

        // Prepares one rung; returns the next rung to try after EA's Run runs again, or -1 to stop (EA's failure path runs).
        static int EvrRung(CityHall.GiveAward ga, int rung, DateAndTime start)
        {
            try
            {
                Sim me = ga.Actor, partner = ga.Target;
                CityHall hall = ga.RabbitHoleToRouteTo;
                SocialJigTwoPerson jig = hall == null ? null : hall.AwardJig;
                if (jig == null || !jig.InWorld || hall.CeremonyAwardInfo == null || !hall.CanAttendAwardCeremony) return -1;
                if (me == null || partner == null || me.HasExitReason(kEvrStop)) return -1;
                if (SimClock.ElapsedTime(TimeUnit.Minutes, start) > kEvrDeadlineMin || !EvrPartnerWaiting(ga, partner)) return -1;
                Slot mine = ga.IsMaster ? SocialJigTwoPerson.RoutingSlots.SimA : SocialJigTwoPerson.RoutingSlots.SimB;
                if ((me.Position - jig.GetPositionOfSlot(mine)).Length() < kEvrAtSlot) return -1;  // routed fine: the sync failed
                EvrCeremony(hall);

                if (rung == 0)
                {
                    PatchCounters()[2326]++;
                    int cleared = EvrClear(me, hall, jig, jig.GetPositionOfSlot(mine), me, partner);
                    if (cleared < 0) return -1;
                    if (cleared > 0) return 1;  // retry the same goal: its blockers left
                    rung = 1;                   // nothing to clear: go straight to rung 1
                }
                if (ga.IsMaster)  // the mayor's side: only rung 1, and only for an NPC
                {
                    if (rung != 1 || !IsNpcSim(me)) return -1;
                    PlaceSim(me, jig.GetPositionOfSlot(mine), jig.GetForwardOfSlot(mine), "eventrouteretry: mayor to slot A");
                    PatchCounters()[2329]++;
                    return kEvrRungs;  // one retry, then stop
                }
                if (!IsNpcSim(partner)) return -1;  // the jig is re-placed only around an NPC mayor
                bool placed = rung == 1 && jig.PlaceJig(partner, me);
                if (!placed) { rung = 2; placed = jig.PlaceJigNearActorAFacingActorB(partner, me); }
                if (!placed) return -1;
                jig.SetHiddenFlags(HiddenFlags.Model);  // as CreateCelebration left it (PlaceJig also sets opacity 0)
                PatchCounters()[2328]++;
                Vector3 a = jig.GetPositionOfSlot(SocialJigTwoPerson.RoutingSlots.SimA), b = jig.GetPositionOfSlot(SocialJigTwoPerson.RoutingSlots.SimB);
                if (EvrClear(me, hall, jig, b, me, partner) < 0 || EvrClear(me, hall, jig, a, me, partner) < 0) return -1;
                EvrMayorToSlot(me, partner, ga.LinkedInteractionInstance, jig);
                return rung + 1;
            }
            catch (Exception e) { EvrError("retry rung", e); return -1; }
        }

        static bool EvrPartnerWaiting(InteractionInstance me, Sim partner)
        {
            InteractionInstance link = me.LinkedInteractionInstance;
            if (link == null || partner.HasBeenDestroyed || partner.InteractionQueue == null) return false;
            return partner.InteractionQueue.IsRunning(link, true) || partner.InteractionQueue.HasInteraction(link);
        }

        // The mayor stays in the GiveAward; once no longer routing (at most kEvrMayorWaitMin) the mayor is placed on the
        // new slot A. A mayor still walking out of the rabbit hole plans to the new jig on its own.
        static void EvrMayorToSlot(Sim me, Sim mayor, InteractionInstance link, SocialJigTwoPerson jig)
        {
            DateAndTime t0 = SimClock.CurrentTime();
            while (mayor.IsRouting && SimClock.ElapsedTime(TimeUnit.Minutes, t0) < kEvrMayorWaitMin)
                if (me.WaitForExitReason(0.25f, ExitReason.Canceled)) return;
            if (mayor.HasBeenDestroyed || mayor.IsRouting || mayor.CurrentInteraction != link || !IsNpcSim(mayor)) return;
            Vector3 a = jig.GetPositionOfSlot(SocialJigTwoPerson.RoutingSlots.SimA);
            if ((mayor.Position - a).Length() < kEvrAtSlot) return;
            PlaceSim(mayor, a, jig.GetForwardOfSlot(SocialJigTwoPerson.RoutingSlots.SimA), "eventrouteretry: mayor to new slot A");
            PatchCounters()[2329]++;
        }

        // ---- helpers ----

        // Clears NPCs off one goal slot: EA's push first, then a one-time move to a SafeSpot for those still there.
        // Returns the number found there (0 = nothing to do), or -1 when the waiting Sim was cancelled.
        static int EvrClear(Sim me, CityHall hall, SocialJigTwoPerson jig, Vector3 spot, Sim keepA, Sim keepB)
        {
            int found = EvrPush(me, spot, keepA, keepB);
            if (found == 0) return 0;
            me.LoopIdle();
            DateAndTime t0 = SimClock.CurrentTime();
            while (SimClock.ElapsedTime(TimeUnit.Minutes, t0) < kEvrPushWaitMin && EvrNpcsNear(spot, keepA, keepB).Count > 0)
                if (me.WaitForExitReason(0.25f, ExitReason.Canceled)) return -1;
            foreach (Sim s in EvrNpcsNear(spot, keepA, keepB)) EvrStepAside(s, hall, jig);
            return found;
        }

        static int EvrPush(Sim me, Vector3 spot, Sim keepA, Sim keepB)
        {
            List<Sim> list = EvrNpcsNear(spot, keepA, keepB);
            if (list.Count == 0) return 0;
            RoutingComponent.PushSimsStatic(me, list, SimRoutingComponent.PushSimsAwayDistanceMin, SimRoutingComponent.PushSimsAwayDistanceMax,
                true, false, new InteractionPriority(InteractionPriorityLevel.Fire), true);
            PatchCounters()[2332] += list.Count;
            return list.Count;
        }

        static List<Sim> EvrNpcsNear(Vector3 spot, Sim keepA, Sim keepB)
        {
            List<Sim> list = new List<Sim>();
            foreach (Sim s in Sims3.Gameplay.Queries.GetObjects<Sim>(spot, kEvrClearRadius))
                if (s != keepA && s != keepB && IsNpcSim(s)) list.Add(s);
            return list;
        }

        // Moves an NPC once per ceremony onto a SafeSpot on the City Hall lot (lot-routable, the game walks on from there)
        // at least kEvrKeepOut from the jig, both slots and the entrance, and not on another moved Sim's spot.
        static void EvrStepAside(Sim s, CityHall hall, SocialJigTwoPerson jig)
        {
            ulong id = s.SimDescription.SimDescriptionId;
            if (sEvrMoved.Contains(id) || s.IsRouting || s.Posture != s.Standing) return;
            Lot lot = jig.LotCurrent;
            if (lot == null || hall.RabbitHoleProxy == null) return;
            Vector3 away = s.Position - jig.Position;
            away.y = 0f;
            if (away.LengthSqr() < 0.0001f) away = -jig.ForwardVector;
            Vector3 pos, fwd;
            if (!SafeSpot(s, lot, jig.Position + away.Normalize() * (kEvrKeepOut + 1f), out pos, out fwd)) return;
            Vector3 anchor, anchorFwd;
            bool anchorMoved;
            if (!EvrAnchor(hall, ShortcutOn("ceremonyspot"), out anchor, out anchorFwd, out anchorMoved)) anchor = jig.Position;
            Vector3[] keep = new Vector3[] { jig.Position, jig.GetPositionOfSlot(SocialJigTwoPerson.RoutingSlots.SimA),
                jig.GetPositionOfSlot(SocialJigTwoPerson.RoutingSlots.SimB), anchor };
            foreach (Vector3 k in keep) if ((pos - k).Length() < kEvrKeepOut) return;
            foreach (Vector3 k in sEvrSpots) if ((pos - k).Length() < 1f) return;
            sEvrMoved.Add(id);
            sEvrSpots.Add(pos);
            PlaceSim(s, pos, (jig.Position - pos).Normalize(), "eventrouteretry: off the award jig");
            PatchCounters()[2329]++;
        }

        static void EvrCeremony(CityHall hall)
        {
            if (sEvrCeremony == hall.CeremonyAwardInfo) return;
            sEvrCeremony = hall.CeremonyAwardInfo;
            sEvrMoved.Clear();
            sEvrSpots.Clear();
        }

    }
}
