using System;
using Ts3Fixes;

// Offline self-check of CarNoRouteCore (issue #166 build 33, docs/513): the four input combinations.
static class CarNoRouteTest
{
    static int sFails;
    static void Check(bool ok, string what) { if (!ok) { sFails++; Console.WriteLine("FAIL " + what); } }

    static void Main()
    {
        Check(!CarNoRouteCore.CannotDrive(false, false), "a car with its routing component drives");
        Check(CarNoRouteCore.CannotDrive(false, true), "a car with no car routing component cannot");
        Check(CarNoRouteCore.CannotDrive(true, false), "no car cannot");
        Check(CarNoRouteCore.CannotDrive(true, true), "no car, no component cannot");
        Console.WriteLine(sFails == 0 ? "carnoroute selftest: ok" : "carnoroute selftest: " + sFails + " FAIL");
        Environment.Exit(sFails == 0 ? 0 : 1);
    }
}
