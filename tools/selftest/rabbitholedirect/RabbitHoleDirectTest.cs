using System;
using Ts3Fixes;

// Offline self-check of RabbitHoleDirectCore (rabbitholedirect).
static class RabbitHoleDirectTest
{
    static int sFails;
    static void Check(bool ok, string what) { if (!ok) { sFails++; Console.WriteLine("FAIL " + what); } }

    static void Main()
    {
        Check(RabbitHoleDirectCore.DirectGate(false, false) == RabbitHoleDirectCore.kRhdGo && RabbitHoleDirectCore.DirectGate(false, true) == RabbitHoleDirectCore.kRhdGo, "door unseen: go");
        Check(RabbitHoleDirectCore.DirectGate(true, true) == RabbitHoleDirectCore.kRhdFade, "door seen, nothing before the animation: faded");
        Check(RabbitHoleDirectCore.DirectGate(true, false) == RabbitHoleDirectCore.kRhdRefuse, "door seen, custom beforeEntering: old path");
        Check(!RabbitHoleDirectCore.DoRoute(0f) && !RabbitHoleDirectCore.DoRoute(RabbitHoleDirectCore.kSlotNearM), "on the slot: animation in place");
        Check(RabbitHoleDirectCore.DoRoute(0.51f) && RabbitHoleDirectCore.DoRoute(float.NaN), "moved (or unknown): EA's short route back");
        Console.WriteLine(sFails == 0 ? "rabbitholedirect selftest: ok" : "rabbitholedirect selftest: " + sFails + " FAIL");
        Environment.Exit(sFails == 0 ? 0 : 1);
    }
}
