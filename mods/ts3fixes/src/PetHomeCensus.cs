using System;
using System.Collections.Generic;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.Objects.Elevator;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // Harness census for the NPC-horse-in-an-apartment lead (docs/580 §11). Read-only: plans routes, never runs them.
    // [verified, decompiled] Stairs.IsAllowedThrough refuses horses, puppies, kittens and wild animals; ElevatorDoors
    // refuses horses, toddlers and wild animals (and, on a lot with virtual residential slots, Sims CanTravelInElevator
    // rejects); ElevatorInterior refuses toddlers only. So a horse can never leave the floor it stands on.
    //   list   every instantiated pet: species, NPC or not, home lot (real or virtual), the home lot's box stalls,
    //          elevators, shells and front-door level, where the pet stands (lot, level), and the pet's own GoHome plans
    //          (PlanRouteIntoLot, then PlanToLot), as EA's GoHome makes them.
    public static partial class Fixes
    {
        public static object PetHomeCensus(string what)
        {
            if (what != "list") return "pethomecensus <list>";
            List<string> rows = new List<string>();
            int pets = 0, horses = 0, failHome = 0, horseUpper = 0;
            foreach (Sim s in new List<Sim>(Sims3.Gameplay.Queries.GetObjects<Sim>()))
            {
                try
                {
                    if (s == null || s.HasBeenDestroyed || s.SimDescription == null || s.IsHuman) continue;
                    pets++;
                    bool horse = s.IsHorse;
                    if (horse) horses++;
                    Lot home = s.LotHome, vhome = s.SimDescription.VirtualLotHome, h = home ?? vhome;
                    string hs = h == null ? "none" : (home != null ? "" : "virtual ") + h.Name + " 0x" + h.LotId.ToString("X")
                        + " stalls " + h.CountObjects<IBoxStall>() + " elevators " + h.CountObjects<ElevatorDoors>()
                        + " shells " + h.BuildableShells.Length + " door level " + h.GetLevelOfOutsideDoor() + (h.HasVirtualResidentialSlots ? " virtual slots" : "");
                    string plan = "";
                    if (h != null && s.LotCurrent != h)
                    {
                        Route r = s.CreateRoute();
                        h.PlanRouteIntoLot(r, true, s);
                        RoutePlanResult a = r.PlanResult;
                        Route r2 = s.CreateRoute();
                        RoutePlanResult b = h.PlanToLot(r2);
                        plan = ", plan into lot " + a.mType + ", plan to lot " + b.mType;
                        if (a.mType != RoutePlanResultType.Succeeded && b.mType != RoutePlanResultType.Succeeded) failHome++;
                    }
                    if (horse && s.Level > 0) horseUpper++;
                    Lot cur = s.LotCurrent;
                    rows.Add(s.FullName + " " + s.SimDescription.Species + " " + s.SimDescription.Age + (IsNpcSim(s) ? " npc" : " household")
                        + ", home " + hs + ", at " + (cur == null ? "none" : cur.IsWorldLot ? "street" : cur.Name) + " level " + s.Level + " room " + s.RoomId
                        + ", doing " + (s.CurrentInteraction != null ? s.CurrentInteraction.GetType().Name : "nothing") + plan);
                }
                catch (Exception e) { rows.Add("error " + e.GetType().Name); }
            }
            return "pethomecensus: " + pets + " pets, " + horses + " horses, " + horseUpper + " horses above ground level, " + failHome
                + " pets whose GoHome plans both fail; " + string.Join(" | ", rows.ToArray());
        }
    }
}
