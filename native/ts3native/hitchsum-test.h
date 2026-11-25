// TestStallWatchDeep ([StallWatch] deep stacks, stallwatch.cpp) and TestHitchSum ([HitchAttr] HitchMs / RouteLockMs /
// JobWaitLogMs, hitchsum.cpp; [RouteAsync] LogFallbackCaller), docs/666 §5. Included by selftest.cpp after stallwatch-test.h.
static bool SwdIsRet(DWORD v) { return (v >= 0x00500000 && v < 0x00600000) || (v > SS_SIM_LO && v < SS_SIM_HI); }
static void TestStallWatchDeep() {
    // 1. the name table: sorted, no overlap, a known row; the Simulate range by its own bounds
    const int nSym = (int)(sizeof(kSsSyms) / sizeof(kSsSyms[0]));
    assert(nSym > 1000);
    for (int i = 1; i < nSym; i++) assert(kSsSyms[i].lo >= kSsSyms[i - 1].lo + kSsSyms[i - 1].len && kSsSyms[i].len && kSsSyms[i].name[0]);
    const SsSym* la = SsSymOf(0x0E16F0);
    assert(la && !strcmp(la->name, "Lock_Acquire") && SsSymOf(0x0E16F0 + la->len - 1) == la && SsSymOf(0x0E16F0 + la->len) != la && !SsSymOf(0));
    const DWORD exeSave = g_ssExe, sizeSave = g_ssExeSize;
    const LONG namesSave = g_ssNames, framesSave = g_ssFrames, keepSave = g_ssKeepNoSim;
    g_ssExe = 0x400000, g_ssExeSize = 0x00E5A000;
    DWORD off = 0;
    bool approx = true;
    assert(!strcmp(SsNameOf(0x004E16F5, &off, &approx), "Lock_Acquire") && off == 5 && !approx);
    assert(!strcmp(SsNameOf(SS_SIM_LO + 0x20, &off, &approx), "MonoScriptHost_Simulate") && off == 0x20);
    assert(!SsNameOf(0x00400010, &off, &approx) && !SsNameOf(0x20000000, &off, &approx));
    char fr[200];
    g_ssNames = 0;
    SsFrameText(0x004E16F5, fr, sizeof(fr));
    assert(!strchr(fr, '['));
    g_ssNames = 1;
    SsFrameText(0x004E16F5, fr, sizeof(fr));
    assert(strstr(fr, "[Lock_Acquire+0x5]") && !strchr(fr, ' '));

    // 2. the scan: unique return addresses in stack order with counts, stack pointers skipped, the frame-pointer chain marked,
    //    the first Simulate hit by its index among all hits
    const DWORD esp = 0x01000000, rA = 0x00500010, rB = 0x00500020, rC = 0x00500030, rS = SS_SIM_LO + 0x20;  // 0x5000xx: no named row
    DWORD stk[64] = {};
    stk[0] = 5, stk[1] = rA, stk[2] = esp + 0x20, stk[3] = rA, stk[5] = rB;
    stk[8] = esp + 4 * 12, stk[9] = rC;  // ebp -> [saved ebp, return C]
    stk[12] = 0, stk[13] = rS;          // the outermost frame of the chain
    stk[20] = 0x00700000;               // not a return address
    static SsCap c;
    memset(&c, 0, sizeof(c));
    SsScan(stk, 64, esp, esp + 4 * 8, SwdIsRet, &c);
    assert(c.hits == 4 && c.total == 5 && c.simAt == 4);
    assert(c.h[0].a == rA && c.h[0].n == 2 && !c.h[0].fp && c.h[1].a == rB && c.h[2].a == rC && c.h[2].fp && c.h[3].a == rS && c.h[3].fp);
    SsScan(stk, 12, esp, 0, SwdIsRet, &c);  // a short copy: no Simulate frame, no chain
    assert(c.hits == 3 && c.simAt == -1 && !c.h[2].fp);
    // more unique addresses than the table holds: counted, the first 256 kept
    static DWORD big[400];
    for (int i = 0; i < 400; i++) big[i] = 0x00500000 + 0x10 * (DWORD)i;
    SsScan(big, 400, esp, 0, SwdIsRet, &c);
    assert(c.hits == SS_HITS && c.total == 400);

    // 3. the text: counts, marks, names; the innermost 3/4 and the outermost 1/4 past Frames; "more frames" lines when full
    SsScan(stk, 64, esp, esp + 4 * 8, SwdIsRet, &c);
    c.eip = 0x004E16F5, c.words = 64 * 256;
    char line[1400];
    g_ssMoreN = 0;
    SsText("sim", 7, c, line, sizeof(line));
    assert(strstr(line, "deep 64 KB, 5 hits, 4 unique, Simulate hit #5: eip ") && strstr(line, "[Lock_Acquire+0x5], stack scan (4): 0x00500010*2 0x00500020 0x00500030^ ") &&
           strstr(line, "[MonoScriptHost_Simulate+0x20]^") && g_ssMoreN == 0);
    g_ssFrames = 8;
    SsScan(big, 12, esp, 0, SwdIsRet, &c);
    SsText("sim", 7, c, line, sizeof(line));
    assert(strstr(line, "no Simulate hit: ") && strstr(line, "stack scan (8): 0x00500000 ") && strstr(line, " 0x00500050 ...(+4) 0x005000A0 0x005000B0") &&
           !strstr(line, "0x00500060"));
    SsText("main", 9, c, line, 160);  // a short line: the rest goes to "more frames" lines
    assert(g_ssMoreN >= 1 && !strncmp(g_ssMore[0], "stall watch: main thread 9 more frames (from #", 46) && strstr(g_ssMore[g_ssMoreN - 1], "0x005000B0"));
    g_ssMoreN = 0;

    // 4. the script frames of another thread through its TEB (plain reads: a bad pointer ends the walk)
    static DWORD teb[0x400], ctx[4], f0[2], f1[2], info0[4];
    const DWORD* const idxSave = g_lgTlsIdx;
    static DWORD idx = 3;
    g_lgTlsIdx = &idx;
    teb[(0xE10 + 3 * 4) / 4] = (DWORD)(uintptr_t)ctx;
    ctx[2] = (DWORD)(uintptr_t)f0;
    f0[0] = (DWORD)(uintptr_t)f1, f0[1] = (DWORD)(uintptr_t)info0, info0[2] = 0x1111;
    f1[0] = 0, f1[1] = 0;
    DWORD m[8] = {};
    assert(SsManaged((DWORD)(uintptr_t)teb, m, 8) == 2 && m[0] == 0x1111 && m[1] == 0);
    assert(SsManaged((DWORD)(uintptr_t)teb, m, 1) == 1 && SsManaged(0, m, 8) == 0);
    ctx[2] = 0x10;  // unreadable frame
    assert(SsManaged((DWORD)(uintptr_t)teb, m, 8) == 0);

    // 5. KeepNoSim: a sample without a Simulate frame is logged and counted
    Ts3nCounters& k = ts3native_counters;
    const LONG ln0 = k.ssLines, st0 = k.ssStalls, mx0 = k.ssMaxMs, ns0 = g_ssNoSimLines, calls0 = g_swFakeCalls;
    g_ssSt = {};
    g_ssTick = 100000;
    g_swFakeInSim = false;
    SsCheck(105000, SwFakeCapture);
    assert(g_swFakeCalls == calls0 + 1 && k.ssLines == ln0 && !g_ssSt.in);  // default: dropped
    g_ssKeepNoSim = 1;
    g_ssSt = {};
    SsCheck(105000, SwFakeCapture);
    assert(k.ssLines == ln0 + 1 && g_ssSt.in && g_ssNoSimLines == ns0 + 1);
    g_swFakeInSim = true;
    g_ssTick = 0, g_ssSt = {}, k.ssOpenMs = 0, k.ssLines = ln0, k.ssStalls = st0, k.ssMaxMs = mx0, g_ssNoSimLines = ns0;

    // 6. the real deep capture on a thread in Sleep, script frames asked
    DWORD tid = 0;
    HANDLE th = CreateThread(NULL, 0, SwSleeper, NULL, 0, &tid);
    Sleep(50);
    bool inSim = true;
    const LONG manSave = g_ssManaged;
    g_ssManaged = 4, g_ssFrames = 24;
    assert(th && SsCaptureDeep(tid, line, sizeof(line), &inSim) && !inSim && !strncmp(line, "deep ", 5) && strstr(line, ", stack scan ("));
    assert(g_ssCap.words > 0 && g_ssCap.nMeth >= 0 && g_ssCap.nMeth <= 4 && strlen(line) < 1240);
    WaitForSingleObject(th, INFINITE), CloseHandle(th);
    g_ssMoreN = 0;
    g_ssManaged = manSave, g_lgTlsIdx = idxSave;
    g_ssExe = exeSave, g_ssExeSize = sizeSave, g_ssNames = namesSave, g_ssFrames = framesSave, g_ssKeepNoSim = keepSave;
    printf("stall watch deep (docs/666): %d names, scan (unique, counts, frame-pointer marks, Simulate hit), text (names, gap, more "
           "frames), script frames by TEB, KeepNoSim, real capture: %s\n", nSym, line);
}

static void TestHitchSum() {
    Ts3nCounters& k = ts3native_counters;
    HsTotals& z = ts3native_hitchsum;
    assert(z.version == 1 && z.hitchUs == 0 && !g_hsOn && !g_hsLockLogUs && g_hsJobLogUs == 300000 && g_hsJobLinesPerMin == 20);
    const DWORD me = GetCurrentThreadId(), mainSave = g_mainTid, workerSave = g_workerTid, simSave2 = g_hsSimTid;
    const DWORD* const simSave = g_rtSimTidSlot;
    static DWORD simTid;
    simTid = me, g_rtSimTidSlot = &simTid, g_mainTid = me + 1, g_workerTid = 4321;
    HsFrame& f = g_hsF;
    memset(&f, 0, sizeof(f));
    memset(&z.frames, 0, sizeof(z) - offsetof(HsTotals, frames));

    // 1. off (default): nothing is summed
    g_hsSimTid = me;
    LONG ph[RJP_N] = {};
    ph[RJP_WORLD] = 600000, ph[RJP_LOTLEG] = 30000, ph[RJP_PORTAL] = 10000, ph[RJP_LOCK] = 10000, ph[RJP_PLAN] = 5000;
    HsJob(SW_DISC, 80000, 0x1A2B), HsOtherLock(5000), HsGc(3000), HsSync(ph, 700000), HsSyncMs(40);
    assert(!f.jobN && !f.olockN && !f.gcUs && !f.syncN);

    // 2. on: the frame's sums, the sim thread only
    g_hsOn = 1;
    g_hsSimTid = me + 5;
    HsJob(SW_DISC, 80000, 0x1A2B);
    assert(!f.jobN);  // not the sim thread
    g_hsSimTid = me;
    HsJob(SW_DISC, 80000, 0x1A2B), HsJob(SW_RTWAIT, 20000, 5), HsJob(SW_RTWAIT, 0, 5);
    assert(f.jobN == 2 && f.jobUs == 100000 && f.jobMaxUs == 80000 && f.jobMaxSite == SW_DISC && f.jobMaxH == 0x1A2B && !f.jobInSyncUs);
    HsSync(ph, 700000);
    assert(f.syncN == 1 && f.syncUs == 700000 && f.syncPh[HSP_WORLD] == 600000 && f.syncPh[HSP_LOT] == 40000 && f.syncPh[HSP_LOCK] == 10000 &&
           f.syncPh[HSP_OTHER] == 50000);
    const bool sypSave = g_sypOn;
    g_sypOn = true;
    HsSyncMs(40);  // a SyncPlans session splits IC plans already
    assert(f.syncN == 1);
    g_sypOn = false;

    // 3. the line and the cause
    HsView v = {1000000, 800000, 16000, 1, f};
    char line[900];
    assert(HsLine(line, sizeof(line), v, 3, 5000) == HSC_SYNC);
    assert(!strcmp(line, "hitch: frame 1000 ms (#3), cause sync-plan/world: sync 700 (n=1 world=600 water=0 post=0 insert=0 cell=0 lot=40 lock=10 other=50), "
                         "jobwait 100 (n=2 max=80 at disc route 00001A2B insync=0), lock918 0 (n=0 max=0 key 00000000 holder - 0 job250=0 insync=0), "
                         "otherlock 0 (n=0 insync=0), gc 0 (+1), limiter 16, cpu 800, unknown 184, at 5000 ms"));
    LONG unk = 0;
    int sub = 0;
    HsView e = {1000000, 900000, -1, 0, {}};
    assert(HsClassify(e, &unk, &sub) == HSC_CPU && unk == 1000000);
    e.cpuUs = 50000;
    assert(HsClassify(e, &unk, &sub) == HSC_OFFCPU);
    e.cpuUs = -1;
    assert(HsClassify(e, &unk, &sub) == HSC_UNKNOWN);
    e.cpuUs = 450000, e.f.jobUs = 200000;
    assert(HsClassify(e, &unk, &sub) == HSC_MIXED && unk == 800000);
    e.f.jobUs = 500000;
    assert(HsClassify(e, &unk, &sub) == HSC_JOBWAIT);
    e.f.lockUs = 800000, e.f.jobUs = 0;
    assert(HsClassify(e, &unk, &sub) == HSC_LOCK918 && unk == 200000);
    e.f.lockUs = 0, e.f.olockUs = 600000;
    assert(HsClassify(e, &unk, &sub) == HSC_OTHERLOCK);
    e.f.olockUs = 0, e.f.gcUs = 600000;
    assert(HsClassify(e, &unk, &sub) == HSC_GC);
    e.f.gcUs = 0, e.limiterUs = 700000;
    assert(HsClassify(e, &unk, &sub) == HSC_LIMITER && unk == 300000);
    // a sync plan that only waited on its lock is a lock wait, and its wait is not taken out of the frame twice
    HsView w = {1000000, 20000, -1, 0, {}};
    w.f.syncN = 1, w.f.syncUs = 900000, w.f.syncPh[HSP_LOCK] = 880000, w.f.syncPh[HSP_OTHER] = 20000;
    w.f.lockN = 1, w.f.lockUs = 870000, w.f.lockInSyncUs = 870000;
    assert(HsClassify(w, &unk, &sub) == HSC_LOCK918 && unk == 100000);

    // 4. +0x918 waits: by holder, the worker's job that ended inside the wait, >= 250 ms; the line only with RouteLockMs
    memset(&f, 0, sizeof(f));
    const HsJobEndRec jeSave = g_hsJe;
    LARGE_INTEGER t0;
    QueryPerformanceCounter(&t0);
    HsJobEnd(0x1A2B3C4D, 77, 410000, 0, 1);
    assert(g_hsJe.endQ >= t0.QuadPart && g_hsJe.seq == 77);
    HsLock(0x0085FE10, 999, t0.QuadPart, 4321, true);  // under 1 ms: not a wait
    assert(!z.lkWaits && !f.lockN);
    HsLock(0x0085FE10, 312000, t0.QuadPart, 4321, true);
    assert(z.lkWaits == 1 && z.lkUs == 312000 && z.lkWorkerN == 1 && z.lkJobEndN == 1 && z.lkJob250N == 1 && z.lkJob250Us == 312000 && !z.lkOtherN &&
           !z.lkLines && !z.lkMainN);
    assert(f.lockN == 1 && f.lockUs == 312000 && f.lockJob250Us == 312000 && f.lockMaxKey == 0x0085FE10 && f.lockMaxTid == 4321 && f.lockMaxRole == HO_WORKER);
    HsLock(0x0062EF7A, 40000, g_hsJe.endQ + 1, 99, false);  // another holder, no job ended since
    assert(z.lkWaits == 2 && z.lkOtherN == 1 && z.lkOtherUs == 40000 && z.lkJobEndN == 1 && f.lockJob250Us == 312000 && f.lockMaxUs == 312000);
    g_hsJe.us = 100000;  // a short job: behind the worker, not the >= 250 ms case
    g_hsLockLogUs = 20000;
    HsLock(0x0085FE10, 30000, t0.QuadPart, 0, false);  // nobody held it at the start, the job ended in the wait: the lent window
    assert(z.lkWorkerN == 2 && z.lkJobEndN == 2 && z.lkJob250N == 1 && z.lkLines == 1);
    HsLock(0x0085FE10, 19000, t0.QuadPart, 4321, true);  // under RouteLockMs: counted, no line
    assert(z.lkWaits == 4 && z.lkLines == 1);
    g_mainTid = me, simTid = me + 9, g_hsSimTid = me + 9;  // the main thread waits: counted, not in the sim frame
    const LONG fl = f.lockN;
    HsLock(0x0085FE10, 25000, t0.QuadPart, 4321, false);
    assert(z.lkMainN == 1 && z.lkMainUs == 25000 && f.lockN == fl && z.lkLines == 2);
    g_mainTid = me + 1, simTid = me, g_hsSimTid = me;
    g_hsLockLogUs = 0;
    char b[640];
    const HsJobEndRec je = {1, 0x1A2B3C4D, 77, 410000, 0, 1};
    HsLockText(b, sizeof(b), false, 312400, 0x0085FE10, true, 4321, HO_WORKER, true, je, "-", 9000);
    assert(!strcmp(b, "route lock wait: sim waited 312 ms on +0x918 (key 0085FE10, job pump), held by route worker 4321; job route 1A2B3C4D seq 77 "
                      "foreign IC kind 0 ended in the wait after 410 ms (>= 250 ms: yes), at 9000 ms"));
    HsLockText(b, sizeof(b), true, 50000, 0x0062EF7A, false, 0, HO_OTHER, false, je, "no job running", 9001);
    assert(!strcmp(b, "route lock wait: main waited 50 ms on +0x918 (key 0062EF7A, plain), held by nobody at the start (lent window or a race); "
                      "no job ended in the wait, now: no job running, at 9001 ms"));
    g_hsJe = jeSave;

    // 5. the frame end: frames under HitchMs only count; a hitch is counted by cause, and by the docs/666 §2 hypothesis
    z.hitchUs = 100000;
    HsView s = {50000, 1000, -1, 0, {}};
    HsFrameEnd(s, 1000);
    assert(z.frames == 1 && !z.hitches);
    s.frameUs = 1000000, s.f.lockN = 1, s.f.lockUs = 700000, s.f.lockJob250Us = 600000;
    HsFrameEnd(s, 2000);
    assert(z.frames == 2 && z.hitches == 1 && z.hitchMs == 1000 && z.causeN[HSC_LOCK918] == 1 && z.causeMs[HSC_LOCK918] == 1000 && z.hitchLock250 == 1 && z.lines == 1);
    s.f.lockJob250Us = 0, s.f.lockUs = 0, s.cpuUs = 990000;
    HsFrameEnd(s, 3000);
    assert(z.hitches == 2 && z.causeN[HSC_CPU] == 1 && z.hitchLock250 == 1);
    static char tot[1100];
    HsTotalsText(tot, sizeof(tot), z, 3, 4, 6000);
    assert(strstr(tot, "hitch totals: frames 3, hitches 2 (2000 ms) by cause lock918=1/1000 sim-cpu=1/1000; +0x918 waits >= 1 ms n=5 ms=426 max=312 (main thread n=1 ms=25): "
                       "behind the route worker n=4 ms=386, its job ended in the wait n=4 ms=386, that job >= 250 ms n=1 ms=312, other holder n=1 ms=40; "
                       "hitches >= 50% on +0x918 behind a job >= 250 ms: 1 of 2; stall watch lines without a Simulate frame 3, main samples 4, at 6000 ms"));

    // 6. JobWaitLogMs: SwNote's line threshold, and its wait in the frame's sum
    const LONG swN = k.swN[SW_OTHER], swUs = k.swUs[SW_OTHER], swMax = k.swMaxUs, swLines = k.swLines;
    memset(&f, 0, sizeof(f));
    SwNote(SW_OTHER, 25000, false, 0x77, true);
    assert(k.swLines == swLines && f.jobN == 1 && f.jobUs == 25000 && f.jobMaxSite == SW_OTHER);
    g_hsJobLogUs = 20000;
    SwNote(SW_OTHER, 25000, false, 0x77, true);
    assert(k.swLines == swLines + 1 && f.jobN == 2);
    g_hsJobLogUs = 300000;
    k.swN[SW_OTHER] = swN, k.swUs[SW_OTHER] = swUs, k.swMaxUs = swMax, k.swLines = swLines;

    // 7. LogFallbackCaller: off = no text; on = the innermost script frames (selftest.cpp's fake frames of the LogFallbackStack case)
    char mc[420] = "x";
    RfbCallerText(mc, sizeof(mc), NULL);
    assert(!mc[0]);
    const u8* (__cdecl* ctxSave)() = g_lgCtx;
    g_lgCtx = FbFakeCtx;
    g_fbCaller = 2;
    RfbCallerText(mc, sizeof(mc), NULL);
    assert(!strcmp(mc, ", managed caller ScriptCore.Route.Plan < Sims3.Gameplay.Actors.Sim+GroupChatB.RouteToGroupTalk"));
    char jm[112];
    assert(!FbJitName(0x10, jm, sizeof(jm)));
    g_fbCaller = 0, g_lgCtx = ctxSave;

    g_hsOn = 0, g_sypOn = sypSave;
    memset(&f, 0, sizeof(f));
    memset(&z.frames, 0, sizeof(z) - offsetof(HsTotals, frames));
    z.hitchUs = 0;
    g_rtSimTidSlot = simSave, g_mainTid = mainSave, g_workerTid = workerSave, g_hsSimTid = simSave2;
    printf("hitch summary (docs/666): off by default, frame sums (job waits, sync phases, locks, gc), cause classes, +0x918 waits by holder "
           "and job, lines, totals, JobWaitLogMs, LogFallbackCaller: %s\n", line);
}
