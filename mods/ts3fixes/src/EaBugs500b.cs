using System;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.ObjectComponents;
using Sims3.Gameplay.Objects;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // docs/500 §6 (2026-11-19): horse routing. Off by default. Pure decisions in EaBugs500Core.cs (self-test
    // tools/selftest/eabugs500).
    //
    // horselotentry [4030-4035] Lot.PlanRouteIntoLot plans to the lot's door spot first and falls back to the mailbox when that
    //   fails [verified, decompiled]. For a horse on the ground the door spot of a house on a foundation (or any upper or
    //   basement floor) cannot be reached: Stairs.IsAllowedThrough refuses horses, CommonDoor.IsAllowedThrough refuses them
    //   except at barn doors and large-animal gates, ElevatorDoors refuse them [verified, decompiled]. Appaloosa Plains logs:
    //   0 of 57 horse plans to a goal off the ground succeeded, 32 were GoHome door plans to one house [measured]. So the horse
    //   pays a failed door plan (10-30 ms async) on every GoHome. The skip runs EA's own fallback (mailbox, then PlanToLotEx)
    //   at once when: the horse plans its own route from level 0, the door spot is on another level, every portal object of
    //   the lot whose openings span two levels answers DoorRestriction for the horse both ways, and the lot has a mailbox
    //   (then the mailbox plan overwrites everything the failed plan left on the route). Site 2 (cache not validated yet)
    //   leaves the cache as EA's failure does (validated, no spot), so lotentrypet's tail sees the same state. Proven exact
    //   for the portal types the planner asks script about (Door, ElevatorDoors, pole, stall, jump); for Stairs, ladders and
    //   the other native-only portals it rests on EA's managed answer matching the native species mask [inferred, measured],
    //   so player Sims are left out (IsPlayerSim) and the off arm counts every prediction EA's plan proved wrong (4032).
    //   Counters: skipped / seen with the switch off / mispredicted (off arm) / errors / ms in PlanRouteIntoLot off / on.
    public static partial class Fixes
    {
        static ulong sHlOffUs, sHlOnUs;
        static int sHlMissLines;

        // horselotentry: 0 = EA's body runs; 1 = validated cache with a door spot; 2 = unvalidated cache, the front door's first
        // inside slot (EA's choice). mailbox is the lot's mailbox when the site is not 0. Never throws.
        static int HlSite(Lot lot, Route r, Sim actor, bool validated, out GameObject mailbox)
        {
            mailbox = null;
            try
            {
                if (actor == null || !actor.IsHorse || sE5LotSpot == null) return 0;
                bool player = IsPlayerSim(actor);
                bool own = r != null && r.Follower != null && r.Follower.Target == actor;
                if (player || !own) return 0;
                LotLocation at = LotLocation.Invalid;
                World.GetLotLocation(actor.Position, ref at);
                if (at.mLevel != 0) return 0;
                int site = 0;
                Vector3 goal = Vector3.Invalid;
                if (validated)
                {
                    goal = (Vector3)sE5LotSpot.GetValue(lot);
                    if (goal == Vector3.Invalid) return 0;   // EA goes straight to the mailbox: nothing to skip
                    site = 1;
                }
                else
                {
                    if (lot.HasVirtualResidentialSlots) return 0;   // the apartment door search stays EA's
                    Door door = lot.FindFrontDoor();
                    if (door == null) return 0;
                    foreach (Slot s in door.GetRoutingSlots())
                    {
                        Vector3 p = door.GetSlotPosition(s);
                        LotLocation l = LotLocation.Invalid;
                        World.GetLotLocation(p, ref l);
                        if (!LotManager.RoomIdIsOutside(l.mRoom)) { goal = p; break; }
                    }
                    if (goal == Vector3.Invalid) return 0;   // EA plans no door route either
                    site = 2;
                }
                LotLocation g = LotLocation.Invalid;
                World.GetLotLocation(goal, ref g);
                if (g.mLevel == 0) return 0;
                GameObject mb = lot.FindMailbox();
                bool refuse = mb != null && HlPortalsRefuse(lot, actor);
                if (!Eb500Core.HlDoomed(actor.IsHorse, player, own, at.mLevel, g.mLevel, mb != null, refuse)) return 0;
                mailbox = mb;
                return site;
            }
            catch (Exception) { PatchCounters()[4033]++; mailbox = null; return 0; }
        }

        // Every portal object of the lot that links two levels refuses the horse both ways (the routing code's own answer:
        // Sim.OnPortalPathPlan locks on DoorRestriction). A one-level portal (door, arch, gate) cannot lift a horse off the
        // ground; a level change with no portal at all is assumed not to exist [inferred].
        static bool HlPortalsRefuse(Lot lot, Sim horse)
        {
            IPortalConnectionObject[] ps = lot.GetObjects<IPortalConnectionObject>();
            if (ps == null) return false;
            foreach (IPortalConnectionObject p in ps)
            {
                if (p == null) return false;
                Vector3[] op = p.GetPortalOpenings();
                int[] lv = new int[op == null ? 0 : op.Length];
                for (int i = 0; i < lv.Length; i++)
                {
                    LotLocation l = LotLocation.Invalid;
                    World.GetLotLocation(op[i], ref l);
                    lv[i] = l.mLevel;
                }
                if (Eb500Core.HlOneLevel(lv)) continue;
                int[] rooms = p.GetRoomConnections();
                if (rooms == null || rooms.Length < 2) return false;   // cannot ask it: not proven
                for (int i = 0; i < rooms.Length; i++)
                    for (int j = 0; j < rooms.Length; j++)
                        if (i != j && p.IsAllowedThrough(rooms[i], rooms[j], horse) != PortalRestriction.DoorRestriction) return false;
            }
            return true;
        }

        // EA's tail of Lot.PlanRouteIntoLot after a failed door plan, without that plan. Plan exceptions (resets) pass through
        // as from EA's body.
        static void HlFallback(Lot lot, Route r, bool planToLotAsFallback, Sim actor, int site, GameObject mailbox)
        {
            if (site == 2) { lot.BestRouteIntoLotLocationValidated = true; sE5LotSpot.SetValue(lot, Vector3.Invalid); }
            r.SetValidRooms(lot.LotId, null);
            RouteDistancePreference pref = RouteDistancePreference.PreferNearestToRouteOrigin;
            float min = Lot.kMailboxRouteMin, max = Lot.kMailboxRouteMax;
            if (actor.IsHorse) { pref = RouteDistancePreference.PreferFurthestFromRouteOrigin; min = Lot.kMailboxHorseRouteMin; max = Lot.kMailboxHorseRouteMax; }
            // Lot.PlanToMailbox (private), same calls
            r.SetOption2(Route.RouteOption2.DestinationMustBeOnLand, true);
            r.PlanToPointRadialRange(mailbox.Position, min, max, pref, RouteOrientationPreference.NoPreference, mailbox.LotCurrent.LotId, new int[1]);
            if (planToLotAsFallback && !r.PlanResult.Succeeded())
            {
                r.SetValidRooms(lot.LotId, null);
                lot.PlanToLotEx(r);
            }
        }

        static void HlTime(int slot, ref ulong acc, uint t0)
        {
            acc += unchecked(SrcNowUs() - t0);
            PatchCounters()[slot] = (int)(acc / 1000UL);
        }

        // Off arm: did EA's plan prove the prediction wrong?
        static void HlCheckOff(Lot lot, Route r, int site)
        {
            try
            {
                bool kept = (Vector3)sE5LotSpot.GetValue(lot) != Vector3.Invalid;
                bool ok = r.PlanResult.Succeeded();
                Vector3 d = ok ? r.GetDestPoint() : Vector3.Invalid;
                LotLocation l = LotLocation.Invalid;
                ulong id = ok ? World.GetLotLocation(d, ref l) : 0UL;
                if (!Eb500Core.HlMispredict(site, kept, ok, l.mLevel, ok && id == lot.LotId)) return;
                PatchCounters()[4032]++;
                if (sHlMissLines++ < 20)
                    DiagLine("horselotentry: mispredict site " + site + " lot 0x" + lot.LotId.ToString("X") + " '" + lot.Name + "' spot kept " + kept + " dest " + (ok ? V(d) + " level " + l.mLevel : "none"));
            }
            catch (Exception) { PatchCounters()[4033]++; }
        }
    }
}
