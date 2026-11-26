using System;
using System.Reflection;
using Sims3.Gameplay;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Objects.Vehicles;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // icecreamdirect (ON 2026-11-20; counting always on). IceCreamTruck.OneShotRouteToLot plans a full car route on
    // the sim thread (PlanToMatrixFromPoint from the truck to a street parking matrix of mDestinationLot), drives it and then parks
    // (a community lot's parking space, else PlaceAt + TruckParked), or, ending service (no destination set: FindLotToEndService),
    // fades out and destroys the truck. Measured (10 min, Isla Paradiso): 15 plans, 4.3 s of sim thread, all for trucks nobody sees.
    // With the switch on, a truck that is not seen where it stands, whose parking matrix and lot are not seen either, and whose
    // destination is not the active household's lot, skips the plan and the drive and runs EA's own after-drive branch at once
    // (the same reservation handling, parking / PlaceAt + TruckParked, or FadeOut + destroy when ending service, the same field resets).
    // Where the hook sits: OneShotRouteToLot is the single place every trip of the truck goes through (IceCreamTruckManager spawns a
    // truck off screen and BeginRoute / RouteToNewLot / EndService all queue a OneShotFunction on it), so nothing else needs a hook.
    // Seen = the same test as rabbitholedirect's door: on screen, camera reach or near the household (FarFromViewers), the goal's
    // lot or point seen (UfGoalUnseen), or the lot the camera or the household is on (UfLotSeen). Only the two ends are tested, not
    // the road between them [shortcut: a truck that drives past the camera between two unseen ends is not caught; upgrade: add the
    // midpoint, or the road cells, if a run shows trucks popping in].
    // Any doubt (a routing truck, no street parking, a thrown error before the decision) runs EA's method unchanged. The decision
    // reserves the street spot the way EA's first lines do (free the old one, take the destination's); when EA's method then runs
    // it frees and takes it again, the same state as if it had run alone. A destination picked here for an ending-service truck is
    // dropped again when EA runs (EA picks its own at random; the pick is uniform either way).
    // Counters: [1421] calls, [1422] skipped (switch on), [1423] would skip (switch off: set destinations only, judged by the lot),
    // [1424] truck seen at its start, [1425] parking spot or lot seen, [1426] destination is the active household's lot, [1427] fell
    // back to EA after an error or no street parking / no sound field, [1428] of the skipped ones, ending service (no lot set),
    // [1429] already routing (EA returns), [1430] skipped with a community-lot parking space taken.
    // With the switch off the hook only counts (IcWould): no lot pick, no parking reservation, so an off game is EA's game.
    public static partial class Fixes
    {
        static void ApplyIceCreamDirect()
        {
            Patch(typeof(IceCreamTruck), "OneShotRouteToLot", typeof(IceCreamHook), "OneShotHook", "OneShotOrig");
        }

        // never instantiated: the subclass gives the hook the truck's protected members (mDestinationLot, mRouteToLot, TruckParked)
        public class IceCreamHook : IceCreamTruck
        {
            static FieldInfo sSoundField;
            static bool sSoundLooked;

            public void OneShotHook()
            {
                int[] c = PatchCounters();
                c[1421]++;
                // switch off: EA's method exactly as without the hook. The decision below picks an ending-service lot
                // (FoodTruckBase.FindLotToEndService draws from the game's RandomUtil) and reserves street parking, so running it
                // here shifted every later random roll of an "off" game (review 2026-11-14); the would-skip count is side-effect free
                if (!ShortcutOn("icecreamdirect"))
                {
                    IcWould(c);
                    OneShotOrig();
                    return;
                }
                bool wasNull = mDestinationLot == null;
                Matrix44 mat = default(Matrix44);
                bool direct = false;
                try { direct = IcDecide(c, wasNull, ref mat); }
                catch (Exception)
                {
                    
                    c[1427]++;
                    direct = false;
                }
                if (!direct)
                {
                    if (wasNull) mDestinationLot = null;  // EA picks its own ending-service lot
                    OneShotOrig();
                    return;
                }
                c[1422]++;
                if (wasNull) c[1428]++;
                IcFinish(c, wasNull, mat);
            }
            public void OneShotOrig() { }  // receives IceCreamTruck.OneShotRouteToLot's original header

            // switch off: would the truck skip? Reads only: an ending-service truck (no lot yet) is not judged, a set destination is
            // judged by its lot (UfLotSeen), not by a parking matrix (taking one reserves a spot) [shortcut: would_skip may count a
            // truck whose parking spot alone is in view; upgrade: read the spot without reserving it if on/off counts disagree]
            void IcWould(int[] c)
            {
                try
                {
                    if (RoutingComponent != null && RoutingComponent.IsRouting) { c[1429]++; return; }
                    Lot dl = mDestinationLot;
                    if (dl == null || dl.StreetParking == null) return;
                    bool hh = dl == Sims3.Gameplay.CAS.Household.ActiveHouseholdLot || dl == LotManager.ActiveLot;
                    bool startSeen = !hh && (UcOnScreen(this) || !FarFromViewers(Position) || !UfGoalUnseen(Position) || UfLotSeen(LotCurrent));
                    int why = TripStageCore.IcDecide(hh, startSeen, UfLotSeen(dl));
                    if (why == TripStageCore.kIcHhLot) c[1426]++;
                    else if (why == TripStageCore.kIcSeenStart) c[1424]++;
                    else if (why == TripStageCore.kIcSeenGoal) c[1425]++;
                    else c[1423]++;
                }
                catch (Exception) {  c[1427]++; }
            }

            // true = skip the plan and the drive. mat is the parking matrix reserved for the truck (as EA's third line takes it)
            bool IcDecide(int[] c, bool ending, ref Matrix44 mat)
            {
                if (RoutingComponent != null && RoutingComponent.IsRouting) { c[1429]++; return false; }  // EA returns at once
                if (!SoundFieldOk()) return false;
                if (ending)
                {
                    FindLotToEndService();
                    if (mDestinationLot == null) return false;  // EA fades the truck out itself
                }
                Lot dl = mDestinationLot;
                if (dl.StreetParking == null) return false;
                bool hh = dl == Sims3.Gameplay.CAS.Household.ActiveHouseholdLot || dl == LotManager.ActiveLot;
                bool startSeen = !hh && (UcOnScreen(this) || !FarFromViewers(Position) || !UfGoalUnseen(Position) || UfLotSeen(LotCurrent));
                int why = TripStageCore.IcDecide(hh, startSeen, false);
                if (why == TripStageCore.kIcGo)
                {
                    // the destination's parking matrix, taken as EA does (it needs the spot to be known to judge it)
                    if (StreetParkingBeingUsed != null) StreetParkingBeingUsed.FreeParkingSpotForCar(this);
                    StreetParkingBeingUsed = dl.StreetParking;
                    StreetParkingBeingUsed.GetParkingSpotForCar(this, ref mat);
                    Vector3 p = mat.pos.V3;
                    bool goalSeen = UfLotSeen(dl) || !FarFromViewers(p) || !UfGoalUnseen(p);
                    why = TripStageCore.IcDecide(false, false, goalSeen);
                }
                if (why == TripStageCore.kIcHhLot) c[1426]++;
                else if (why == TripStageCore.kIcSeenStart) c[1424]++;
                else if (why == TripStageCore.kIcSeenGoal) c[1425]++;
                return why == TripStageCore.kIcGo;
            }

            // EA's after-drive code for a drive that succeeded (the failure branch's TruckParked is not run)
            void IcFinish(int[] c, bool ending, Matrix44 mat)
            {
                StopMusic();
                if (!ending)
                {
                    if (mDestinationLot.IsCommunityLot)
                    {
                        ParkingSpace space = Vehicle.GetClosestUnreservedParkingSpace(mDestinationLot.GetObjects<ParkingSpace>(), this);
                        if (space != null)
                        {
                            space.ParkVehicle(this);
                            c[1430]++;
                            mDestinationLot = null;
                            mRouteToLot = null;
                            TsLog(4, "icecreamdirect parked on community lot");
                            return;
                        }
                    }
                    PlaceAt(mat.pos.V3, mat.at.V3, null);
                    TruckParked();
                }
                else FadeOut(false, true);
                TsLog(4, "icecreamdirect " + (ending ? "ended service" : "parked") + " without a drive");
                mDestinationLot = null;
                mRouteToLot = null;
            }

            // EA's drive starts the music loop and stops it after; the truck is unseen, so the music is only left stopped
            void StopMusic()
            {
                object s = sSoundField.GetValue(this);
                if (s != null) ((ObjectSound)s).Stop();
            }
            static bool SoundFieldOk()
            {
                if (!sSoundLooked)
                {
                    sSoundLooked = true;
                    sSoundField = typeof(IceCreamTruck).GetField("mSoundHandle", BindingFlags.Instance | BindingFlags.NonPublic);
                    if (sSoundField != null && sSoundField.FieldType != typeof(ObjectSound)) sSoundField = null;
                }
                return sSoundField != null;
            }
        }
    }
}
