using System;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Objects.Vehicles;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // boatlandstuck (tuning kBoatLandStuck, EA bug fix, off by default). [verified, decompiled Boat.GetOutOfBoat.Run] With a
    // next interaction queued, Run first steers the boat to land (Boat.RouteBoatToLandOnWayToPosition: Lot.PlanToLot, then
    // PlanToPointRadialRange 50 m). When both plans fail it returns false and the Sim stays seated, so the next interaction
    // queues GetOutOfBoat again: a Twinbrook run (2026-11-17) had two water-taxi Sims stuck at one spot, 119 failed boat plans
    // in 26 min, 55 of them 2.1 s native jobs and 7 main-thread waits of 1.3-2.1 s. EA's own way out is Boat.GetOut(Sim),
    // which Run calls on success (shoreline spot, else GetAlternativeLandPositionForSim, else into the water, else a reset):
    // with the switch on, a failed Run whose Sim still sits in this boat ends with that call, and Run then reports what EA's
    // success path reports (true once the Sim is out). Known NPCs only (household rule: household, selected and
    // unknown-scope Sims keep EA's result). One call per failed Run, no retry.
    // Counters: [4077] got out after a failed Run. An error is logged once (VlThrew) and EA's false is kept.
    public static partial class Fixes
    {
        static void ApplyBoatLandStuck()
        {
            Patch(typeof(Boat.GetOutOfBoat), "Run", typeof(BoatLandStuckHook), "RunHook", "RunOrig");
        }

        public class BoatLandStuckHook : Boat.GetOutOfBoat
        {
            public bool RunHook()
            {
                bool ok = RunOrig();
                if (ok || !ShortcutOn("boatlandstuck")) return ok;
                try
                {
                    SittingInBoat sib = Actor != null ? Actor.Posture as SittingInBoat : null;
                    if (sib == null || sib.Container != Target || !IsNpcSim(Actor)) return false;
                    Target.GetOut(Actor);
                    PatchCounters()[4077]++;
                    return !(Actor.Posture is SittingInBoat);  // out = EA's success path (GetOut, then true), so the next interaction goes on
                }
                catch (ResetException) { throw; }  // GetOut yields: a task reset must pass through
                catch (Exception __swx) { VlThrew("boatlandstuck", __swx); }
                return false;
            }
            public bool RunOrig() { return false; } // receives GetOutOfBoat.Run's original header
        }
    }
}
