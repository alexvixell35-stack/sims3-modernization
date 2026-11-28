using System;
using System.Collections.Generic;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Objects.Elevator;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // A car parked on the Sim's floor is "called" for 7 sim-minutes (docs/686 §34). Default off; rules in ElevParkedCore.cs.
    //
    // [verified, decompiled ElevatorDoors.ElevatorDoorsPortalComponent.OnApproachingPortalObject] A Sim boarding at closed
    // doors puts itself in the column's use lists, then sleeps kLongEffectsTime (7 sim-minutes) with the car's "arrive"
    // effects, so the car may come from another floor. EA keeps no car position: the doors close when the last rider steps
    // out (and 30 sim-minutes after a call with nobody inside), so the next Sim boarding on the floor where the car stands
    // waits the same 7 minutes for a car that is already there, and every other floor of the column waits with it (the
    // doors refuse their lane while the car is in use).
    //
    // elevparked (NPCs) / elevparkedhh (household and selected Sims too)
    //   Fix: the column's car is remembered at the doors where it last stopped (a ride ended there, or a boarding there
    //   was its last call). A Sim boarding there at closed doors with the car empty gets the doors opened first, with the
    //   game's own OpenElevatorDoors (its animation and muzak), so EA's approach takes its open-doors branch: no arrive
    //   sleep, the hold for more riders as always. A car called from any other floor keeps EA's 7 minutes. Nothing moves
    //   a Sim. After a load the position is unknown until the car's next stop (EA's wait).
    // Toddlers, kittens, puppies and WooHoo keep the game's code. Counters 4711-4715. Check: ElevParkedCheck.
    public static partial class Fixes
    {
        const int kEpkOpened = 4711, kEpkWould = 4712, kEpkExempt = 4713, kEpkStops = 4714, kEpkErrors = 4715;

        static readonly Dictionary<ulong, ulong> sEpkCarAt = new Dictionary<ulong, ulong>();  // column (its lowest car) -> doors of the car's last stop
        static string sEpkMissing = "", sEpkLast = "", sEpkLastError = "";

        static void ApplyElevParked()
        {
            sEpkMissing = "";
            if (!Patch(typeof(ElevatorDoors.ElevatorDoorsPortalComponent), "OnApproachingPortalObject", new Type[] { typeof(Sim) }, typeof(EpkApproachHook), "ApproachHook", "ApproachOrig"))
                sEpkMissing = "ElevatorDoorsPortalComponent.OnApproachingPortalObject";
            if (sEpkMissing.Length > 0) AppliedLog().Add("elevparked: not found: " + sEpkMissing);
        }

        static ulong EpkColumn(ElevatorInterior car)
        {
            ElevatorInterior low = car;
            for (int i = 0; i < 64 && low.ElevatorInteriorBelow != null; i++) low = low.ElevatorInteriorBelow;  // bounded: a broken link cannot loop
            return low.ObjectId.Value;
        }

        // `this` is the game's doors portal component.
        public class EpkApproachHook : ElevatorDoors.ElevatorDoorsPortalComponent
        {
            public bool ApproachHook(Sim sim)
            {
                ElevatorDoors d = null;
                ulong col = 0;
                try
                {
                    d = Owner as ElevatorDoors;
                    ElevatorInterior car = d == null ? null : d.InteriorObj;
                    ElevatorInterior.ElevatorPortalComponent ep = car == null ? null : car.ElevatorPortal;
                    if (ep != null && sim != null && sim.SimDescription != null)
                    {
                        col = EpkColumn(car);
                        if (!ep.IsSimInElevator(sim) && !sim.SimDescription.ToddlerOrBelow && !sim.IsKitten && !sim.IsPuppy && !(sim.CurrentInteraction is ElevatorDoors.WooHoo)
                            && !car.InUseIgnoringWooHoo(sim))
                        {
                            ulong at;
                            bool here = sEpkCarAt.TryGetValue(col, out at) && at == d.ObjectId.Value;
                            int r = ElevParkedCore.Decide(ShortcutOn("elevparked"), ShortcutOn("elevparkedhh") || !IsPlayerSim(sim), d.mIsOpen, here, ep.IsElevatorEmpty());
                            int[] c = PatchCounters();
                            if (r == ElevParkedCore.kWould) c[kEpkWould]++;
                            else if (r == ElevParkedCore.kExempt) c[kEpkExempt]++;
                            else if (r == ElevParkedCore.kGo)
                            {
                                d.OpenElevatorDoors();  // EA's own (sets the open flag, plays the door animation; yields like EA's sleep did)
                                c[kEpkOpened]++;
                                sEpkLast = "elevparked: " + sim.FullName + " " + ShlWho(sim) + " boards the car parked on level " + d.Level + " without the 7 sim-min call, at " + V(d.Position);
                                if (ShortcutOn("elevtriplog")) DiagLine(sEpkLast);
                            }
                        }
                    }
                }
                catch (ResetException) { throw; }
                catch (Exception e) { EpkError(e); }
                bool ok = ApproachOrig(sim);
                try
                {
                    if (ok && d != null && col != 0)
                    {
                        if (sEpkCarAt.Count > 256) sEpkCarAt.Clear();
                        sEpkCarAt[col] = d.ObjectId.Value;  // the car stops here: a rider stepped out, or a boarder called it
                        PatchCounters()[kEpkStops]++;
                    }
                }
                catch (ResetException) { throw; }
                catch (Exception e) { EpkError(e); }
                return ok;
            }
            public bool ApproachOrig(Sim sim) { return false; }  // receives OnApproachingPortalObject's original body
        }

        static void EpkError(Exception e)
        {
            PatchCounters()[kEpkErrors]++;
            sEpkLastError = "elevparked: error: " + e.GetType().Name + ": " + e.Message;
            DiagLine(sEpkLastError);
        }

        static string EpkCounters()
        {
            int[] c = PatchCounters();
            return "elevparked: opened at once " + c[kEpkOpened] + ", would, off " + c[kEpkWould] + ", household exempt " + c[kEpkExempt] + ", car stops seen " + c[kEpkStops]
                + ", errors " + c[kEpkErrors] + (sEpkMissing.Length > 0 ? "; not found: " + sEpkMissing : "") + (sEpkLast.Length > 0 ? "; last: " + sEpkLast : "")
                + (sEpkLastError.Length > 0 ? "; " + sEpkLastError : "");
        }

        // ElevParkedCheck result: counters and the last line. run: the rules with controls (read-only).
        public static object ElevParkedCheck(string what)
        {
            try
            {
                if (what == "run")
                {
                    bool fix = ElevParkedCore.Decide(true, true, false, true, true) == ElevParkedCore.kGo;
                    bool off = ElevParkedCore.Decide(false, true, false, true, true) == ElevParkedCore.kWould;
                    bool ctl = ElevParkedCore.Decide(true, true, true, true, true) == ElevParkedCore.kNone
                        && ElevParkedCore.Decide(true, true, false, false, true) == ElevParkedCore.kNone
                        && ElevParkedCore.Decide(true, true, false, true, false) == ElevParkedCore.kNone
                        && ElevParkedCore.Decide(true, false, false, true, true) == ElevParkedCore.kExempt;
                    bool on = ShortcutOn("elevparked");
                    return "ElevParkedCheck run: verdict " + (on ? (fix ? "FIXED" : "BROKEN") : (off ? "BROKEN" : "?")) + ", controls ok=" + ctl + "; " + EpkCounters();
                }
                return EpkCounters();
            }
            catch (Exception e) { return "ElevParkedCheck: " + e.GetType().Name + ": " + e.Message; }
        }
    }
}
