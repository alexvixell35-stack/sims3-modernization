// [AdaptDiag] pure parts (adaptdiag.cpp, docs/316): allow list, state-file entries, noise grade, known-issue rows and match,
// dominant activity, drill-down pick (slots + budget), ts3fixes failed-patch allow list. Included by selftest.cpp.
static void TestAdaptDiag() {
    // allow list: next-session keys only
    assert(AdAllowed("CrashForensics.Enabled") && AdAllowed("crashforensics.vbguard") && AdAllowed("Diag.ModTimeHz") && AdAllowed("AdaptDiag.SwapInvokeDiag"));
    assert(!AdAllowed("RouteAsync.Enabled") && !AdAllowed("EaCrashFixes.VbFreeDelayMs") && !AdAllowed("") && !AdAllowed("Diag.ModTime2"));
    // "Section.Key=Value"
    char s[32], k[40], v[24];
    assert(AdSplitEntry("StallWatch.StallMs=1000", s, sizeof(s), k, sizeof(k), v, sizeof(v)) && !strcmp(s, "StallWatch") && !strcmp(k, "StallMs") && !strcmp(v, "1000"));
    assert(!AdSplitEntry("StallWatch=1", s, sizeof(s), k, sizeof(k), v, sizeof(v)));     // no key part
    assert(!AdSplitEntry(".Key=1", s, sizeof(s), k, sizeof(k), v, sizeof(v)));          // no section
    assert(!AdSplitEntry("A.B=", s, sizeof(s), k, sizeof(k), v, sizeof(v)));            // no value
    assert(!AdSplitEntry("A=1.5", s, sizeof(s), k, sizeof(k), v, sizeof(v)));           // the dot is in the value
    // noise grade
    assert(AdNoiseGrade(false, 0, 0, 0, false) == '?');
    assert(AdNoiseGrade(true, 2, 0.99, 0, false) == 'A');
    assert(AdNoiseGrade(true, 6, 0.99, 0, false) == 'B' && AdNoiseGrade(true, 2, 0.90, 0, false) == 'B' && AdNoiseGrade(true, 1, 1, 0, true) == 'B');
    assert(AdNoiseGrade(true, 12, 1, 0, false) == 'C' && AdNoiseGrade(true, 1, 0.80, 0, false) == 'C' && AdNoiseGrade(true, 1, 1, 0.6, false) == 'C');
    assert(AdNoiseGrade(true, 1, 0, 0, false) == 'A');  // no PDH clock reading: other CPU decides
    // known-issue rows (the kit's ts3native.adaptdiag-known.tsv) and their match
    AdSig g;
    assert(AdSigParse("crash-x\tcrash\tsteam\tTS3W.exe+0x1C6E07\tCAS mesh bone index past its list\tdocs/67 §28\tv0.2.5\t[EaCrashFixes] GeomBoneIndex", &g));
    assert(!strcmp(g.id, "crash-x") && !strcmp(g.kind, "crash") && !strcmp(g.match, "TS3W.exe+0x1C6E07") && !strcmp(g.sw, "[EaCrashFixes] GeomBoneIndex"));
    assert(AdSigMatch(g, "crash", "steam", "fault: ts3w.exe+0x1c6e07\r\nthread 5") && !AdSigMatch(g, "crash", "ea", "fault: TS3W.exe+0x1C6E07"));
    assert(!AdSigMatch(g, "script", "steam", "fault: TS3W.exe+0x1C6E07") && !AdSigMatch(g, "crash", "steam", "fault: TS3W.exe+0x1C6E08"));
    char lab[200];
    AdSigLabel(g, lab, sizeof(lab));
    assert(!strcmp(lab, "known: CAS mesh bone index past its list (docs/67 §28), fixed in v0.2.5 / switch [EaCrashFixes] GeomBoneIndex"));
    AdSig a;
    assert(AdSigParse("cas-target\tany\tany\tUnable to invoke an invalid target\tCAS TargetException via a swapped method", &a) && !a.doc[0] && !a.sw[0]);
    assert(AdSigMatch(a, "script", "ea", "<Exception>Unable to invoke an invalid target.</Exception>") && AdSigMatch(a, "hitch", "steam", "x unable to invoke an invalid target"));
    AdSigLabel(a, lab, sizeof(lab));
    assert(!strcmp(lab, "known: CAS TargetException via a swapped method (-)"));
    assert(!AdSigParse("# id\tkind\tedition", &a) && !AdSigParse("", &a) && !AdSigParse("only\ttwo", &a) && !AdSigParse("a\tb\tc\t\tlabel", &a));
    // dominant activity
    LONG secs[AD_ACTS] = {10, 40, 0, 0, 0, 10, 0};
    assert(AdDominant(secs) == AD_LIVE);
    LONG tie[AD_ACTS] = {0, 0, 30, 30, 0, 0, 0};
    assert(AdDominant(tie) == AD_CAS);
    // pick: rank order, MaxActive, budget (a cheaper lower rank still fits), rank 0 never
    LONG rank[4] = {6, 0, 12, 4}, cost[4] = {3500, 50, 3500, 50};
    int out[4];
    int n = AdPick(rank, cost, 4, 3, 5000, out);
    assert(n == 2 && out[0] == 2 && out[1] == 3);  // 2 (12) fits, 0 (6) does not fit the budget next to it, 3 (4) does
    n = AdPick(rank, cost, 4, 1, 100000, out);
    assert(n == 1 && out[0] == 2);
    n = AdPick(rank, cost, 4, 3, 0, out);
    assert(n == 0);
    // ts3fixes failed patches against the allow list
    char allow[2][64] = {"AutShadowCore.", "Lot.UpdateDoorPrivacy"};
    char miss[200] = "";
    assert(AdFixMissing("AutShadowCore.Run: REFUSED: inlined; Sim.Foo: not found; Lot.UpdateDoorPrivacy: x", allow, 2, miss, sizeof(miss)) == 1);
    assert(!strcmp(miss, "ts3fixes Sim.Foo: not found"));
    miss[0] = 0;
    assert(AdFixMissing("", allow, 2, miss, sizeof(miss)) == 0 && !miss[0]);
    // FNV: stable (the state file and sessions csv compare these across sessions)
    assert(AdFnv(kAdFnv0, "a", 1) == 0xaf63dc4c8601ec8cull);
}
