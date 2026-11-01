namespace Ts3Fixes
{
    // Game-independent decision of carnoroute (CarNoRoute.cs, issue #166 build 33, docs/513). Self-check: tools/selftest/carnoroute.
    public static class CarNoRouteCore
    {
        // Whether a drive cannot start: no car, or a car with no car routing component (destroyed, or another kind of
        // routing component). EA's method dereferences both without a check.
        public static bool CannotDrive(bool carNull, bool carRoutingNull)
        {
            return carNull || carRoutingNull;
        }
    }
}
