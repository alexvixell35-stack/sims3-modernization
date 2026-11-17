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
        // hhsearchprobe record (docs/276 §2.3 A, docs/277 §5): outcome, scope, enqueue reason, gaps, loop signs
        int[] c = new int[1139];
        HhSearchRec h = new HhSearchRec("A", false, true);
        h.Note(c, HhSearchRec.kScopeHh, true, "Eat", true, HhSearchRec.kBusyNone, HhSearchRec.kRImmOk, "user/other|ok", null, 7UL, 0, true, 1000, 0f);
        h.Note(c, HhSearchRec.kScopeHh, true, "Eat", false, HhSearchRec.kBusyUserHead, HhSearchRec.kR5to10, "Eat|ok", null, 7UL, 0, true, 1300, 1f);
        h.Note(c, HhSearchRec.kScopeUnknown, false, null, false, HhSearchRec.kBusyNone, HhSearchRec.kROver60, "Eat|fail", "Hygiene<=-100", 7UL, 1, false, 2800, 1.5f);
        h.Note(c, HhSearchRec.kScopeHh, true, "Sleep", false, HhSearchRec.kBusyAutoHead, HhSearchRec.kRNone, null, null, 7UL, 0, true, 30000, 10f);
        Check(c[HhSearchRec.kN], 4, "hh searches");
        Check(c[HhSearchRec.kLocal], 3, "hh local");
        Check(c[HhSearchRec.kPush], 1, "hh pushed");
        Check(c[HhSearchRec.kBusyUser], 1, "hh not queued, user head");
        Check(c[HhSearchRec.kNoPick], 1, "hh no pick");
        Check(c[HhSearchRec.kBusyAuto], 1, "hh not queued, autonomous head");
        Check(c[HhSearchRec.kSameDef], 1, "hh same pick");
        Check(c[HhSearchRec.kShortGap], 2, "hh short gap (sim-min)");
        Check(c[HhSearchRec.kNoPushRun], 2, "hh no-push runs");
        Check(c[HhSearchRec.kMaxPerSim], 4, "hh max per Sim");
        Check(c[HhSearchRec.kScope + HhSearchRec.kScopeUnknown], 1, "hh unknown scope");
        Check(c[HhSearchRec.kReason + HhSearchRec.kROver60], 1, "hh reason > 60");
        Check(c[HhSearchRec.kNoPick60], 1, "hh no pick after > 60 sim-min empty");
        Check(c[HhSearchRec.kGap + 0], 1, "gap 300 ms bin");
        Check(c[HhSearchRec.kGap + 1], 1, "gap 1500 ms bin");
        Check(c[HhSearchRec.kGap + 3], 1, "gap 27 s bin");
        Check(c[HhSearchRec.kActiveLot], 3, "hh on active lot");
        string line = h.Line(0x99UL);
        Check(line.StartsWith("A desc=99 sel scope=0: n=4 local=3 push=1 nopick=1 notQueuedUser=1 notQueuedAuto=1") ? 1 : 0, 1, "hh line: " + line);
        Check(line.Contains("pushed=[Eat(1)]") && line.Contains("lowestAtNoPick=[Hygiene<=-100(1)]") && line.Contains(">60=1") ? 1 : 0, 1, "hh line parts: " + line);
        // EA's idle branch order (Sim.CanRunAutonomyImmediately, then the delay test)
        Check(HhSearchRec.Reason(true, false, true, true, true, 0f), HhSearchRec.kRImmOk, "auto ok: immediate");
        Check(HhSearchRec.Reason(true, false, true, false, false, 0f), HhSearchRec.kRImmFail, "auto failed, NPC: immediate");
        Check(HhSearchRec.Reason(true, false, true, false, true, 70f), HhSearchRec.kROver60, "auto failed, selectable: delay path");
        Check(HhSearchRec.Reason(false, false, true, true, true, 7f), HhSearchRec.kR5to10, "no local autonomy: delay path");
        Check(HhSearchRec.Reason(true, true, false, true, true, 30f), HhSearchRec.kR10to60, "active Sim talking after a user action: delay path");
        Check(HhSearchRec.Reason(true, false, false, true, true, -1f), HhSearchRec.kRLt5, "negative empty time");
        Check(HhSearchRec.Scope(UnseenFallbackCore.kGone), HhSearchRec.kScopeGone, "scope gone");
        Check(HhSearchRec.Scope(-1), HhSearchRec.kScopeUnknown, "scope -1 unknown");
        Check(HhSearchRec.kLast, 1134, "hh slots end at the fixes-1115 reservation's last slot");
        // autt2lcov slots (fixes-1145..1157): 12 distinct class slots, then the live slot
        System.Collections.Generic.HashSet<int> cov = new System.Collections.Generic.HashSet<int>();
        for (int k = 0; k < 3; k++) for (int o = 0; o < 2; o++) for (int u = 0; u < 2; u++) cov.Add(AutLocalProbeCore.CovSlot(o == 1, u == 1, k));
        Check(cov.Count, 12, "cov slots distinct");
        Check(AutLocalProbeCore.CovSlot(true, false, AutLocalProbeCore.kHh), 1145, "cov first slot");
        Check(AutLocalProbeCore.CovSlot(false, true, AutLocalProbeCore.kUnseen), 1156, "cov last class slot");
        Check(cov.Contains(AutLocalProbeCore.kCovLive) ? 1 : 0, 0, "live slot apart");
        Check(AutLocalProbeCore.kCovLast, 1157, "cov slots end at the fixes-1145 reservation's last slot");
        Console.WriteLine(sFails == 0 ? "autlocalprobe selftest: ok" : "autlocalprobe selftest: " + sFails + " FAIL");
        Environment.Exit(sFails == 0 ? 0 : 1);
    }
}
