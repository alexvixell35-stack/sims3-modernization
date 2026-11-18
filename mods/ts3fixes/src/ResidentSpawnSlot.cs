using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.ObjectComponents;
using Sims3.Gameplay.Objects.Door;
using Sims3.Gameplay.Objects.Elevator;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // residentspawnslot (tuning kResidentSpawnSlot, default off; docs/582 B5 §8). NPC spawn placement only.
    // [verified, decompiled + routecause logs] SimDescription.Instantiate(Lot, ResourceKey) for a Sim with a VirtualLotHome
    // (apartment residents, Late Night) and Service.CreateSim for a service NPC living in an apartment test the resident's
    // NPC-door slot (and the elevator fallback spot) with PlumbBob.SelectedActor.IsPointInLotSafelyRoutable: a full plan
    // from the slot to the lot's corners, with the SELECTED Sim's door permissions. A door the resident may pass but the
    // selected Sim may not (a lock to that lot's people) fails every test (FailedBecauseDestinationPortalLocked, ~2 ms), and
    // the resident is placed at an elevator or the lot corner instead of its own door: 115 failed plans for Apollo Bloom in
    // phase-20261115-1831, none of them his own route.
    // Fix: while one of those two EA methods runs (wrapper hook on Instantiate; one line in HiddenRoomSpawn's CreateSim
    // hook), a failed IsPointInLotSafelyRoutable by the selected Sim (FewerPlans' hook, one line) is re-judged with the
    // resident's own permissions: if every door the plan found locked (EA's SimRoutingComponent.LockedDoorsDuringPlan,
    // cleared at each plan) is one the resident passes by CommonDoor's lock rules, the spot counts as routable. Hidden-room
    // doors, one-way doors, Nobody / VIPRoom locks and door types with their own rules (not a plain CommonDoor lock) count
    // as blocking. Any other failure (no path at all) stays a failure. The selected Sim itself is never routed, moved or
    // changed; only where the NPC appears. Counters: [2882] spots accepted for the resident, [2883] re-judged with the
    // switch off (would accept), [2884] failures kept (a door the resident cannot pass either), [2885] errors.
    public static partial class Fixes
    {
        static readonly FieldInfo sRsOwnerList = typeof(CommonDoor).GetField("mOwnerList", kInstAll);
        static readonly FieldInfo sRsOneWay = typeof(CommonDoor).GetField("mOneWayDestinationRoomId", kInstAll);
        static string sRsMissing = "";

        static void ApplyResidentSpawnSlot()
        {
            sRsMissing = ((sRsOwnerList == null ? "CommonDoor.mOwnerList " : "") + (sRsOneWay == null ? "CommonDoor.mOneWayDestinationRoomId " : "")
                + (typeof(SimDescription).GetMethod("Instantiate", kInstAll, null, new Type[] { typeof(Lot), typeof(ResourceKey) }, null) == null ? "SimDescription.Instantiate(Lot,ResourceKey)" : "")).Trim();
            if (sRsMissing.Length > 0) { AppliedLog().Add("residentspawnslot: not found: " + sRsMissing); return; }
            Patch(typeof(SimDescription), "Instantiate", new Type[] { typeof(Lot), typeof(ResourceKey) }, typeof(BRsHook), "InstHook", "InstOrig");
        }

        // Context for HiddenRoomSpawn's Service.CreateSim hook (wiring) and the Instantiate hook: the resident being placed,
        // per sim-thread task. Both EA methods yield (FadeOut, Sleep), so another task's IsPointInLotSafelyRoutable can run
        // in between; keyed by task, that check (the selected Sim's own, say) is never judged for the resident.
        static readonly Dictionary<ulong, SimDescription> sRsByTask = new Dictionary<ulong, SimDescription>();
        internal static SimDescription RsEnter(SimDescription d)
        {
            ulong task = Simulator.CurrentTask.Value;
            SimDescription prev;
            sRsByTask.TryGetValue(task, out prev);
            if (d != null && d.VirtualLotHome != null) sRsByTask[task] = d;
            return prev;
        }
        internal static void RsLeave(SimDescription prev)
        {
            ulong task = Simulator.CurrentTask.Value;
            if (prev == null) sRsByTask.Remove(task); else sRsByTask[task] = prev;
        }
        static SimDescription RsResident()
        {
            SimDescription d;
            return sRsByTask.Count > 0 && sRsByTask.TryGetValue(Simulator.CurrentTask.Value, out d) ? d : null;
        }

        // Pure: does the resident pass this locked door? kind: 0 plain CommonDoor lock, 1 a door with its own rules.
        internal static bool RsPasses(CommonDoor.tLock t, int kind, bool hidden, bool oneWay, bool ownerIsResident, bool sameHousehold, bool inList, bool isPet)
        {
            if (kind != 0 || hidden || oneWay) return false;
            switch (t)
            {
                case CommonDoor.tLock.Anybody: return true;
                case CommonDoor.tLock.Pets: return !isPet;
                case CommonDoor.tLock.SelectedActor: return ownerIsResident;
                case CommonDoor.tLock.SelectedHousehold: return sameHousehold;
                case CommonDoor.tLock.OwnerList: return inList;
                default: return false;  // Nobody, VIPRoom
            }
        }

        static bool RsDoorPasses(Door door, SimDescription d)
        {
            Lot lot = door.LotCurrent;
            int[] rooms = door.GetRoomConnections();
            bool hidden = lot != null && rooms != null && rooms.Length >= 2 && (lot.IsRoomHidden(rooms[0]) || lot.IsRoomHidden(rooms[1]));
            // EA door types that override CommonDoor.IsAllowedThrough keep their own rules: judged blocking (SecretDoor and NonAnimatedDoor are no Door)
            int kind = door is DoorSingleGender || door.GetType().Name == "Turnstile" ? 1 : 0;
            if (d.IsHorse && !(door is IBarnDoor)) kind = 1;  // EA's horse rule (gates aside): judged blocking
            bool oneWay = (int)sRsOneWay.GetValue(door) != 36863;
            SimDescription owner = door.LockOwner;
            List<ulong> list = sRsOwnerList.GetValue(door) as List<ulong>;
            return RsPasses(door.LockType, kind, hidden, oneWay, owner == d, owner != null && owner.Household != null && owner.Household == d.Household,
                list != null && list.Contains(d.SimDescriptionId), d.IsPet);
        }

        // Pure: the verdict for the resident. ok: the selected Sim's verdict; locked: doors the plan found locked; allPass: the
        // resident passes every one of them.
        internal static bool RsDecide(bool on, bool ok, int locked, bool allPass) { return ok || (on && locked > 0 && allPass); }

        // Wiring from FewerPlans' IsPointInLotSafelyRoutable hook: the selected Sim's verdict, re-judged for a resident:
        // locked doors by residentspawnslot, any other failure by residentspawnwalk (below).
        internal static bool RsAdjust(Sim sim, bool ok, Lot lot, Vector3 pos)
        {
            SimDescription d = RsResident();
            if (ok || d == null || sim == null || sim != PlumbBob.SelectedActor || sRsMissing.Length > 0) return ok;
            int n = 0;
            try
            {
                List<Door> locked = sim.SimRoutingComponent == null ? null : sim.SimRoutingComponent.LockedDoorsDuringPlan;
                n = locked == null ? 0 : locked.Count;
                if (n > 0)
                {
                    bool allPass = true;
                    foreach (Door door in locked) if (door == null || !RsDoorPasses(door, d)) { allPass = false; break; }
                    bool on = ShortcutOn("residentspawnslot");
                    PatchCounters()[!allPass ? 2884 : on ? 2882 : 2883]++;
                    if (RsDecide(on, ok, n, allPass)) return true;
                }
            }
            catch (Exception) { PatchCounters()[2885]++; }
            return RsWalk(sim, d, lot, pos);
        }

        // ---- residentspawnwalk (tuning kResidentSpawnWalk, default off; docs/582 B8 §8) ----
        // [verified, decompiled + live HiddenRoomCheck reach on CD30 (Founders Peace), 2026-11-15] The other way EA's
        // spawn-slot test fails for a resident: the plan from the slot (on an upper level) to the lot's corners rides the
        // elevator, and ElevatorDoors.IsAllowedThrough asks CanTravelInElevator of the plan's Sim, the SELECTED Sim: not on
        // its home lot, not greeted, not an NPC, so only ground-level elevators are open to it (DoorRestriction, not a door
        // lock: LockedDoorsDuringPlan stays empty and residentspawnslot never sees it). The resident, an NPC of that lot,
        // may ride any elevator in a public residential room (EA's NPC branch). Live: room 10 on level 1 of CD30 reaches
        // outside for the resident and an NPC, not for the selected Sim; EA put the resident at the lot corner.
        // Fix: a failed check is re-judged by the room walk (HiddenRoom.cs HrWalkOut) from the point's room with the
        // resident's rules: a portal the selected Sim may pass is open; an ElevatorDoors is open when EA's NPC branch
        // opens it (public residential room, adult human); a Door is open when the resident passes its lock
        // (RsDoorPasses: hidden rooms, one-way, Nobody / VIPRoom and own-rule doors block); any other portal is closed.
        // Reaching an outside room accepts the point; EA's own public-residential test on the slot still follows. Only
        // where the NPC appears changes; never for a resident of the active household. Counters: [3648] points accepted by
        // the walk, [3649] would accept with the switch off, [3650] errors.
        // Pure: is an elevator open to the resident? (EA's CanTravelInElevator for a non-service NPC of a virtual lot)
        internal static bool RsElevatorOpen(bool virtualLot, bool publicResidentialRoom, bool adultHuman)
        {
            return adultHuman && (!virtualLot || publicResidentialRoom);
        }
        // Pure: the walk's verdict. ok: EA's; reaches: the walk reached outside for the resident.
        internal static bool RsWalkDecide(bool on, bool ok, bool household, bool reaches)
        {
            return ok || (on && !household && reaches);
        }

        static bool RsWalk(Sim sel, SimDescription d, Lot lot, Vector3 pos)
        {
            try
            {
                if (lot == null || lot.IsWorldLot) return false;
                bool household = d.Household != null && d.Household == Household.ActiveHousehold;
                if (household) return false;
                bool adultHuman = d.IsHuman && d.TeenOrAbove;
                int rooms;
                bool? reaches = HrWalkOut(lot, World.GetRoomId(pos), delegate (IPortalConnectionObject p, int from, int to)
                {
                    if (p.IsAllowedThrough(from, to, sel) == PortalRestriction.None) return true;
                    ElevatorDoors e = p as ElevatorDoors;
                    if (e != null) return RsElevatorOpen(e.LotCurrent != null && e.LotCurrent.HasVirtualResidentialSlots, e.IsInPublicResidentialRoom, adultHuman);
                    Door door = p as Door;
                    return door != null && RsDoorPasses(door, d);
                }, out rooms);
                if (reaches == null) { PatchCounters()[3650]++; return false; }
                if (!reaches.Value) return false;
                bool on = ShortcutOn("residentspawnwalk");
                PatchCounters()[on ? 3648 : 3649]++;
                return RsWalkDecide(on, false, false, true);
            }
            catch (Exception) { PatchCounters()[3650]++; return false; }
        }

        // Wiring from FewerPlans: with the switch on and a resident being placed, the selected Sim's check skips the verdict
        // cache (a cached "false" has no locked-door list to judge) and starts from an empty list (EA clears it at every plan
        // start too). With the switch off nothing is touched.
        internal static bool RsOwnPlan(Sim sim)
        {
            if (sim == null || RsResident() == null || sim != PlumbBob.SelectedActor || sRsMissing.Length > 0 || !ShortcutOn("residentspawnslot")) return false;
            if (sim.SimRoutingComponent != null) sim.SimRoutingComponent.LockedDoorsDuringPlan.Clear();
            return true;
        }

        // Review 2026-11-15: EA tests the resident's own door slot with `lot.LotLocationIsPublicResidential`, the CALLER's
        // lot (a phone invite passes the inviter's lot, also Annex / School / ShowStage), although the slot is on the
        // resident's VirtualLotHome, so an invited resident never spawns at its own door. `lot` is used nowhere else once
        // VirtualLotHome is set, so with the switch on the method receives VirtualLotHome. Never for the active household.
        // Counted in [2882] / [2883].
        internal static bool RsUseHome(bool on, bool hasHome, bool sameLot, bool household) { return on && hasHome && !sameLot && !household; }
        static Lot RsTestLot(SimDescription d, Lot lot)
        {
            try
            {
                Lot home = d == null ? null : d.VirtualLotHome;
                bool household = d != null && d.Household != null && d.Household == Household.ActiveHousehold;
                if (home == null || home == lot || household || sRsMissing.Length > 0) return lot;
                bool on = ShortcutOn("residentspawnslot");
                PatchCounters()[on ? 2882 : 2883]++;
                return RsUseHome(on, true, false, false) ? home : lot;
            }
            catch (Exception) { PatchCounters()[2885]++; return lot; }
        }

        public class BRsHook
        {
            public Sim InstHook(Lot lot, ResourceKey outfitKey)
            {
                SimDescription d = (SimDescription)(object)this;
                SimDescription prev = RsEnter(d);
                try { return InstOrig(RsTestLot(d, lot), outfitKey); }
                finally { RsLeave(prev); }
            }
            public Sim InstOrig(Lot lot, ResourceKey outfitKey) { return null; } // receives SimDescription.Instantiate(Lot,ResourceKey)
        }
    }
}
