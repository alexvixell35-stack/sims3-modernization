using System;
using Ts3Fixes;

// Offline self-check of AutLocalProbeCore (tier-2 L0): actor class from UfWhy codes (unknown = household) and the B8 reason order.
static class AutLocalProbeTest
{
    static int sFails;
    static void Check(int got, int want, string what) { if (got != want) { sFails++; Console.WriteLine("FAIL " + what + ": " + got + ", want " + want); } }

    static void Main()
    {
        Check(AutLocalProbeCore.Class(UnseenFallbackCore.kSeen), AutLocalProbeCore.kUnseen, "UfWhy 0 = unseen");
        Check(AutLocalProbeCore.Class(UnseenFallbackCore.kHousehold), AutLocalProbeCore.kHh, "household");
        Check(AutLocalProbeCore.Class(UnseenFallbackCore.kGone), AutLocalProbeCore.kHh, "gone counts as household");
        Check(AutLocalProbeCore.Class(UnseenFallbackCore.kNotLive), AutLocalProbeCore.kHh, "not live counts as household");
        Check(AutLocalProbeCore.Class(-1), AutLocalProbeCore.kHh, "failed check (-1) = unknown = household");
        Check(AutLocalProbeCore.Class(UnseenFallbackCore.kActiveLot), AutLocalProbeCore.kSeen, "active lot = seen");
        Check(AutLocalProbeCore.Class(UnseenFallbackCore.kOnScreen), AutLocalProbeCore.kSeen, "on screen = seen");
        Check(AutLocalProbeCore.Class(UnseenFallbackCore.kSocialSeen), AutLocalProbeCore.kSeen, "social seen = seen");
        Check(AutLocalProbeCore.Reason(true, true, true, true, true), AutLocalProbeCore.kRHousehold, "household branch first");
        Check(AutLocalProbeCore.Reason(false, true, true, true, true), AutLocalProbeCore.kRActiveLot, "active lot before same lot");
        Check(AutLocalProbeCore.Reason(false, false, true, true, true), AutLocalProbeCore.kRSameLot, "same lot before daycare");
        Check(AutLocalProbeCore.Reason(false, false, false, true, true), AutLocalProbeCore.kRDaycare, "daycare before resort");
        Check(AutLocalProbeCore.Reason(false, false, false, false, true), AutLocalProbeCore.kRResort, "resort");
        Check(AutLocalProbeCore.Reason(false, false, false, false, false), AutLocalProbeCore.kROther, "none = on screen / hosted / cached");
        Check(AutLocalProbeCore.kReason + AutLocalProbeCore.kROther, 924, "last reason slot");
        Check(AutLocalProbeCore.kExc, AutLocalProbeCore.kLast, "slots end at the reservation's last slot");
        Console.WriteLine(sFails == 0 ? "autlocalprobe selftest: ok" : "autlocalprobe selftest: " + sFails + " FAIL");
        Environment.Exit(sFails == 0 ? 0 : 1);
    }
}
