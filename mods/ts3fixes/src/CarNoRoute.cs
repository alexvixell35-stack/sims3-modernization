using System;
using System.Reflection;
using Sims3.Gameplay.Objects.Vehicles;
using Sims3.Gameplay.Services;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // carnoroute (issue #166 build 33, docs/513): CarNpcManager.DriveFromPointToMatrix takes the car's CarRoutingComponent
    // and creates a route on it with no check. CarRoutingComponent is the car's routing component cast to the car kind, so
    // it is null once the car is destroyed, or for a car with another kind of routing component. The throw ends
    // DriveToLotInServiceCar with the service Sim already in the driver's seat, maybe off screen.
    // Fix, switch on: with no car or no car routing component, the method returns false without running EA's code, as for a
    // route that failed to plan. Both callers already handle false: DriveFromPointToMatrixWithinTime teleports the car to
    // the parking spot, DriveToLotInCar returns the value to its caller. [2039] drives with no car routing component (also with the switch
    // off), [2040] returned false.
    public static partial class Fixes
    {
        public class CarNoRouteHook
        {
            public bool DriveHook(Car car, Vector3 startPoint, Matrix44 targetMat)
            {
                bool cannot = false;
                try { cannot = CarNoRouteCore.CannotDrive(car == null, car == null || car.CarRoutingComponent == null); }
                catch (Exception) { } // the check must never stop EA's drive
                if (cannot)
                {
                    PatchCounters()[2039]++;
                    if (ShortcutOn("carnoroute"))
                    {
                        PatchCounters()[2040]++;
                        if (sCnLines++ < 5) ShortcutLog("carnoroute: " + (car != null ? car.GetType().Name : "no car") + " has no car routing component; the drive returns false (the caller teleports or gives up)");
                        return false;
                    }
                }
                return DriveOrig(car, startPoint, targetMat);
            }
            public bool DriveOrig(Car car, Vector3 startPoint, Matrix44 targetMat) { return false; } // receives CarNpcManager.DriveFromPointToMatrix's original header
        }
        static int sCnLines;

        delegate bool CnDriveFn(Car car, Vector3 startPoint, Matrix44 targetMat);

        // Agent checks for docs/513 (repro car-no-route-check):
        //   "make": creates a maid's service car out of the world and destroys it. The game disposes it a little later
        //           (Dispose drops its components; 2026-11-01: right after Destroy the car still had them).
        //   "expect thrown" (switch off) / "expect false" (switch on): throws when that car still has its routing component,
        //           then calls DriveFromPointToMatrix on it through a delegate (the hooked method, as EA's callers reach it).
        //           Off: throws unless EA's NullReferenceException came out, seen +1, returned +0. On: throws unless it
        //           returned false with no exception, seen +1, returned +1.
        static Car sCnCar;
        public static string CarNoRouteCheck(string what)
        {
            CarNpcManager m = CarNpcManager.Singleton;
            if (m == null) throw new InvalidOperationException("carnoroute " + what + ": no CarNpcManager");
            if (what == "make")
            {
                sCnCar = m.CreateServiceCar(ServiceType.Maid);
                if (sCnCar == null) throw new InvalidOperationException("carnoroute make: EA made no service car");
                sCnCar.Destroy();
                return sCnCar.GetType().Name + " made and destroyed; routing component now " + (sCnCar.CarRoutingComponent != null ? "still there" : "gone");
            }
            if (what != "expect thrown" && what != "expect false") return "unknown check " + what + " (make, expect thrown, expect false)";
            Car car = sCnCar;
            sCnCar = null;
            if (car == null) throw new InvalidOperationException("carnoroute " + what + ": make first");
            if (car.CarRoutingComponent != null) throw new InvalidOperationException("carnoroute " + what + ": the destroyed car still has its routing component (not disposed yet), so the case cannot be built");
            MethodInfo mi = typeof(CarNpcManager).GetMethod("DriveFromPointToMatrix", kInstAll, null, new Type[] { typeof(Car), typeof(Vector3), typeof(Matrix44) }, null);
            CnDriveFn drive = (CnDriveFn)Delegate.CreateDelegate(typeof(CnDriveFn), m, mi);
            int seen0 = PatchCounters()[2039], ret0 = PatchCounters()[2040];
            Exception got = null;
            bool r = true;
            try { r = drive(car, Vector3.Zero, new Matrix44()); }
            catch (Exception e) { got = e; }
            int seen = PatchCounters()[2039] - seen0, ret = PatchCounters()[2040] - ret0;
            bool on = ShortcutOn("carnoroute");
            bool ok = what == "expect thrown" ? !on && got is NullReferenceException && seen == 1 && ret == 0
                : on && got == null && !r && seen == 1 && ret == 1;
            string st = car.GetType().Name + " destroyed: " + (got != null ? got.GetType().Name : "returned " + r) + ", seen +" + seen + ", returned false +" + ret;
            if (!ok) throw new InvalidOperationException("carnoroute " + what + ": wrong outcome (switch " + on + "); " + st);
            return "as expected; " + st;
        }
    }
}
