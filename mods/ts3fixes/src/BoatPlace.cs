using System;
using System.Globalization;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Objects.Vehicles;
using Sims3.Gameplay.Routing;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    public static partial class Fixes
    {
        // ---- boatplace (tuning kBoatPlace, ON by default since the 2026-11-20 import, townie boats only) ----
        // Where EA puts a boat for a Sim: GetInBoatRouteAction.PerformAction calls boat.PlaceAt(pt) with pt = the first node of
        // the first boat leg of the Sim's own plan (mOriginalRoute.GetSegmentStartPoint(0)), seats the Sim and returns
        // ContinueAndPopPathAndReplan; RoutingComponent.DoRoute then replans from the boat's PositionOnFloor with the boat as
        // follower. When pt is water inside a real lot, that replan needs a lot leg for the boat (start -> the one best lot
        // exit, docs/99 2.8 step 6) and fails often: 10 Isla Paradiso games, first replan of a placed boat failed 53 of 129
        // times from one residential lot and 13 of 1,593 times from world water. The blockers named by the failed plans are
        // dynamic footprints of boats (the complement of the boat's own id, or of another boat stuck on the same water); the
        // boat then stays there and retries (hundreds of plans a game).
        //
        // With the switch, a boat placed by GetInBoatRouteAction for a townie on water inside a lot is placed on the nearest
        // boat-passable water cell that no lot covers instead (ts3native boatprobe.cpp, channel 0x89: same water body when the
        // start cell has one, within kBoatPlaceMaxM). The Sim is faded out while it is seated, so nothing moves in view, and
        // EA's replan then starts on world water. Nothing else changes; a ts3native without the channel, no cell in range or a
        // throw keeps EA's point.
        // Scope = BoatTownie plus the boarding Sim: any household, selected or no-household Sim boarding, in or on the boat, or
        // a boat owned by the active household or with no known owner household, keeps EA's point.
        // shortcut: the cell is not checked for another boat on it (EA checks only its own point); boattrip (BoatTrip.cs)
        // adds that spreading and takes over the placement while it is on.
        // Counters: [4075] boats placed on another cell.
        const int kBplMaxLog = 40;
        static int sBplLogged;

        public class BoatPlaceHook
        {
            public void PlaceAtHook(Vector3 carPos, Vector3 carFwd, Sim sim)
            {
                PlaceAtOrig(BplSpot((object)this as Boat, carPos, sim), carFwd, sim);
            }
            public void PlaceAtOrig(Vector3 carPos, Vector3 carFwd, Sim sim) { } // receives Vehicle.PlaceAt's original header
        }

        // the scope rule for a boat (boatplace, boattrip): false = EA's behaviour
        static bool BoatTownie(Boat boat)
        {
            // Owned boats: 10 IP games, 95% of failed boat plan time was townie-driven owned boats (speed, fishing, sail,
            // scooter) retrying one goal up to 238 times. An owned boat counts only when its owner lot is known and belongs to
            // another household (unknown owner = household rule). GetOwnerLot is GameObject.mOwnerLot, set by purchase /
            // household inventory (SetLotOwner): the owner's home lot, not where it is moored.
            if (boat is IOwnableVehicle)
            {
                Lot ol = boat.GetOwnerLot();
                Sims3.Gameplay.CAS.Household oh = ol != null ? ol.Household : null;
                if (oh == null || oh == Sims3.Gameplay.CAS.Household.ActiveHousehold) return false;
            }
            bool hh, other;
            VlWho(boat, null, out hh, out other);
            return !hh;  // household rule: any household, selected or unknown-scope Sim in or on the boat
        }

        // ts3native boatprobe.cpp (Route.GetOption2 channel 0x89); the game and an older ts3native answer false
        static bool BplAsk(int code, uint payload)
        {
            return sYieldProbe.GetOption2(unchecked((Sims3.SimIFace.Route.RouteOption2)(0x8900000000000000UL | (ulong)code << 48 | payload)));
        }

        // the point Vehicle.PlaceAt gets: pos, or the nearest startable cell for a townie boat on lot water
        static Vector3 BplSpot(Boat boat, Vector3 pos, Sim sim)
        {
            if (boat == null) return pos;
            if (ShortcutOn("boattrip")) return BtSpot(boat, pos, sim);  // BoatTrip.cs: placement is one step of the trip there
            if (!ShortcutOn("boatplace")) return pos;
            try
            {
                RouteAction ra = sim != null && sim.RoutingComponent != null ? sim.RoutingComponent.GetCurrentRouteAction() : null;
                if (ra == null || ra.GetType() != sVlGiBoatType) return pos;
                if (IsPlayerSim(sim) || sim.Household == null || !BoatTownie(boat)) return pos;
                Lot lot = LotManager.GetLotAtPoint(pos);
                if (lot == null || lot.IsWorldLot || lot.IsHouseboatLot()) return pos;
                bool log = sBplLogged < kBplMaxLog;
                FloatBits b = default(FloatBits);
                b.F = pos.x;
                if (!BplAsk(1, b.U)) return pos;  // ts3native without the channel
                b.F = pos.z;
                BplAsk(2, b.U);
                BplAsk(3, (log ? 1u : 0u) | (uint)(boat.ObjectId.Value & 0xFFFFFFUL) << 8);  // answer unused: a start cell without a body counts too
                if (!BplAsk(4, 0)) return pos;
                bool same = BplAsk(4, 1);
                if (!same && BplAsk(4, 20)) return pos;  // the start cell has a body: only a cell of that body is connected
                int ox = 0, oz = 0;
                for (int i = 0; i < 8; i++)
                {
                    if (BplAsk(4, (uint)(2 + i))) ox |= 1 << i;
                    if (BplAsk(4, (uint)(10 + i))) oz |= 1 << i;
                }
                float dx = unchecked((sbyte)ox) * 0.5f, dz = unchecked((sbyte)oz) * 0.5f;
                float d = (float)Math.Sqrt(dx * dx + dz * dz);
                Vector3 target = new Vector3(pos.x + dx, pos.y, pos.z + dz);
                Lot tl = LotManager.GetLotAtPoint(target);
                if (d > Instantiator.kBoatPlaceMaxM || (tl != null && !tl.IsWorldLot)) return pos;
                PatchCounters()[4075]++;
                if (log)
                {
                    sBplLogged++;
                    DiagLine("boatplace: " + boat.GetType().Name + " from " + V(pos) + " to " + V(target) + " (" + d.ToString("0.0", CultureInfo.InvariantCulture)
                        + " m, " + (same ? "same body" : "start cell has no body") + "), boat " + (boat.ObjectId.Value & 0xFFFFFFUL).ToString("X6"));
                }
                return target;
            }
            catch (Exception __swx)
            {
                VlThrew("boatplace", __swx);
                return pos;
            }
        }
    }
}
