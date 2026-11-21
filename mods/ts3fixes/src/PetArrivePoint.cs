using System;
using System.Reflection;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // petarrivepoint (tuning kPetArrivePoint, default off; docs/656 §4.1). GoHome.Run (and RouteToFrontDoorOrMailbox when a
    // Sim wants to be inside) plans with Lot.PlanRouteIntoLot. Its first plan goes to the lot's cached best route-in point,
    // which on a lot without apartment slots is a spot just inside the front door, validated once for whoever asks first
    // [verified, decompiled Lot.PlanRouteIntoLot]. CommonDoor.IsAllowedThrough refuses horses at every non-barn door (and at
    // gates not made for large animals), strays at every door of a lot that has not greeted them, and pets at a door locked
    // for pets [verified, decompiled]. For those animals the first plan is a full-length plan that ends PortalLocked
    // (routecause: GoHome PortalLocked 65 lines, "locked doors 0", OFF runs 2026-11-15..19), and when the animal is the
    // first to ask on a lot, EA clears that lot's point for every Sim after it.
    // Fix: for a non-exempt NPC animal the lot's front door does not let through, PlanRouteIntoLot skips the inside point (and
    // the validation, which is left for the next Sim) and runs EA's own tail: SetValidRooms(lot, null), PlanToMailbox with
    // EA's distances (the horse ones for a horse), then PlanToLotEx when planToLotAsFallback. Apartment lots, world lots and
    // humans keep EA's body. Household, selected and unknown-scope animals (HouseholdExempt or IsPlayerSim) are never touched.
    // Rule: PetArrivePointCore. Counters [4170] inside points skipped, [4171] of those on a lot not validated yet (cache left
    // for the next Sim), [4172] exempt, [4173] errors. Log "petarrivepoint: ..." capped at 10 a minute.
    public static partial class Fixes
    {
        static int sPapMinute = -1, sPapLogged;
        static FieldInfo sPapLoc;
        static MethodInfo sPapMailbox;
        static bool sPapReflected, sPapReflectOk;

        static void ApplyPetArrivePoint()
        {
            Patch(typeof(Lot), "PlanRouteIntoLot", new Type[] { typeof(Route), typeof(bool), typeof(Sim) }, typeof(PetArriveHook), "PlanHook", "PlanOrig");
        }

        static void PapLog(string s)
        {
            int minute = System.Environment.TickCount / 60000;
            if (minute != sPapMinute) { sPapMinute = minute; sPapLogged = 0; }
            if (sPapLogged++ < 10) DiagLine("petarrivepoint: " + s);
        }

        static bool PapReflect()
        {
            if (sPapReflected) return sPapReflectOk;
            sPapReflected = true;
            const BindingFlags inst = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            sPapLoc = typeof(Lot).GetField("mBestRouteIntoLotLocation", inst);
            sPapMailbox = typeof(Lot).GetMethod("PlanToMailbox", inst, null, new Type[] { typeof(Route), typeof(float), typeof(float), typeof(RouteDistancePreference) }, null);
            sPapReflectOk = sPapLoc != null && sPapMailbox != null;
            if (!sPapReflectOk) AppliedLog().Add("petarrivepoint: Lot.mBestRouteIntoLotLocation or PlanToMailbox not found, switch inert");
            return sPapReflectOk;
        }

        // true = the call was answered here with EA's tail; false = EA's whole body runs
        static bool PapPlan(Lot lot, Route r, bool planToLotAsFallback, Sim actor)
        {
            int[] c = PatchCounters();
            try
            {
                if (actor.IsHuman || actor.SimDescription == null || !PapReflect()) return false;
                bool validated = lot.BestRouteIntoLotLocationValidated;
                Vector3 point = (Vector3)sPapLoc.GetValue(lot);
                bool hasPoint = point != Vector3.Invalid;
                Door door = (validated && !hasPoint) || lot.IsWorldLot || lot.HasVirtualResidentialSlots ? null : lot.FindFrontDoor();
                bool allowed = door == null || door.IsAllowedThrough(actor);
                int d = PetArrivePointCore.Decide(true, HouseholdExempt(actor) || IsPlayerSim(actor), lot.IsWorldLot, lot.HasVirtualResidentialSlots,
                    door != null, allowed, validated, hasPoint);
                if (d == PetArrivePointCore.kExempt) { c[4172]++; return false; }
                if (d != PetArrivePointCore.kSkip) return false;
                // EA's own tail of PlanRouteIntoLot, unchanged
                r.SetValidRooms(lot.LotId, null);
                RouteDistancePreference pref = RouteDistancePreference.PreferNearestToRouteOrigin;
                float min = Lot.kMailboxRouteMin, max = Lot.kMailboxRouteMax;
                if (actor.IsHorse) { pref = RouteDistancePreference.PreferFurthestFromRouteOrigin; min = Lot.kMailboxHorseRouteMin; max = Lot.kMailboxHorseRouteMax; }
                sPapMailbox.Invoke(lot, new object[] { r, min, max, pref });
                if (planToLotAsFallback && !r.PlanResult.Succeeded())
                {
                    r.SetValidRooms(lot.LotId, null);
                    lot.PlanToLotEx(r);
                }
                c[4170]++;
                if (!validated) c[4171]++;
                PapLog(actor.FullName + " (" + actor.SimDescription.Species + ") -> " + lot.Name + ": front door closed to it, inside point "
                    + (validated ? "skipped" : "not validated by it") + ", EA's mailbox / lot plan " + r.PlanResult.mType);
                return true;
            }
            catch (Exception ex) { c[4173]++; PapLog("error " + ex.GetType().Name); return false; }
        }

        public class PetArriveHook
        {
            public void PlanHook(Route r, bool planToLotAsFallback, Sim actor)
            {
                if (actor != null && r != null && ShortcutOn("petarrivepoint") && PapPlan((Lot)(object)this, r, planToLotAsFallback, actor)) return;
                PlanOrig(r, planToLotAsFallback, actor);
            }
            public void PlanOrig(Route r, bool planToLotAsFallback, Sim actor) { } // receives Lot.PlanRouteIntoLot's original body
        }
    }
}
