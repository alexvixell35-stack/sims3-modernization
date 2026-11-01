// --- [RouteJobPhases] SyncPlans (syncphases.cpp, docs/164): layout, sessions on a dummy Route_PlanLocked that calls a dummy world
// A* from two registered loop sites, the IC path through SyncIc, the lock frame, busy / stale / non-sim threads, the log line.
// Included by selftest.cpp after routephases-test.h.
static volatile LONG g_sypTestRes;
static u8* g_sypTestPlan;
static u8* __cdecl SypTestIc(u8* out, DWORD) {
    ((void(__cdecl*)())g_sypTestPlan)();
    return out;
}
static void TestSyncPhases() {
    static_assert(offsetof(Ts3nCounters, sypMode) == 0x4120 && offsetof(Ts3nCounters, sypPhaseUs) == 0x4150 &&
                  offsetof(Ts3nCounters, sypLongN) == 0x418C && offsetof(Ts3nCounters, sypLongPhaseUs) == 0x4190 &&
                  offsetof(Ts3nCounters, sypLoopN) == 0x41CC && offsetof(Ts3nCounters, sypLongLoopUs) == 0x41FC &&
                  offsetof(Ts3nCounters, sypLinesDropped) == 0x4210 && sizeof(Ts3nCounters) == 5468 * 4,
                  "counters v97 layout after v96 (docs/registry.md)");
    Ts3nCounters& k = ts3native_counters;
    memset(&k.sypMode, 0, sizeof(Ts3nCounters) - offsetof(Ts3nCounters, sypMode));
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
    u8* code = (u8*)VirtualAlloc(NULL, 4096, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
    assert(code);
    // world A*: its prologue (sub esp,0x3C; push ebx; mov ebx,[esp+0x58]), Sleep(2), pop ebx; add esp,0x3C; ret
    u8* w = code;
    static const u8 kW[] = {0x83, 0xEC, 0x3C, 0x53, 0x8B, 0x5C, 0x24, 0x58, 0x6A, 0x02, 0xB8, 0, 0, 0, 0, 0xFF, 0xD0,
                            0x5B, 0x83, 0xC4, 0x3C, 0xC3};
    memcpy(w, kW, sizeof(kW));
    *(void**)(w + 11) = (void*)&Sleep;
    // Route_PlanLocked: its prologue (sub esp,0xD8), call world (entry loop site), call world (open-ground site), add esp,0xD8,
    // mov eax,[g_sypTestRes], ret
    u8* p = code + 64;
    static const u8 kP[] = {0x81, 0xEC, 0xD8, 0x00, 0x00, 0x00, 0xE8, 0, 0, 0, 0, 0xE8, 0, 0, 0, 0, 0x81, 0xC4, 0xD8, 0x00, 0x00, 0x00,
                            0xA1, 0, 0, 0, 0, 0xC3};
    memcpy(p, kP, sizeof(kP));
    *(LONG*)(p + 7) = (LONG)(w - (p + 11));
    *(LONG*)(p + 12) = (LONG)(w - (p + 16));
    *(volatile LONG**)(p + 23) = &g_sypTestRes;
    g_sypTestPlan = p;
    const DWORD savedRet[4] = {g_sypLoopRet[0], g_sypLoopRet[1], g_sypLoopRet[2], g_sypLoopRet[3]};
    g_sypLoopRet[0] = (DWORD)(uintptr_t)(p + 11), g_sypLoopRet[1] = (DWORD)(uintptr_t)(p + 16), g_sypLoopRet[2] = g_sypLoopRet[3] = 1;
    assert(RjpInstallAt(RJS_WORLD, (DWORD)(uintptr_t)w, NULL) && RjpInstallAt(RJS_PLAN, (DWORD)(uintptr_t)p, NULL));
    typedef LONG(__cdecl * PlanFn)();
    const PlanFn plan = (PlanFn)p;
    DWORD simTid = 0;
    const DWORD* savedSim = g_rtSimTidSlot;
    g_rtSimTidSlot = &simTid;
    const LONG savedLog = k.rfbLogMs, savedMode = k.asyncMode;
    k.rfbLogMs = 3;
    // off: straight through, nothing counted
    g_sypTestRes = 5;
    assert(plan() == 5 && k.sypEntry[1] == 0);
    SypArm();
    assert(g_sypOn && k.sypMode == 1 && g_rjpAny[RJS_PLAN] == 1);
    // 1. not the sim thread: passes (one compare in RjpEnter), nothing counted
    assert(plan() == 5 && k.sypEntry[1] == 0 && k.sypBusy == 0 && !g_rjpSyncTid);
    // 2. a sim-thread Route_PlanLocked that fails: a session of its own, both world calls filed by loop, a line (>= 3 ms)
    simTid = GetCurrentThreadId();
    g_sypTestRes = 0;
    assert(plan() == 0 && !g_rjpSyncTid && t_rjp.n == 0 && !t_syp.open);
    assert(k.sypEntry[1] == 1 && k.sypPlans[1] == 1 && k.sypPlans[0] == 0 && k.sypLoopN[0] == 1 && k.sypLoopN[1] == 1 &&
           k.sypLoopN[3] == 0 && k.sypLongN == 1 && k.sypLines == 1);
    LONG sum = 0;
    for (int i = 0; i < RJP_N; i++) sum += k.sypPhaseUs[i];
    assert(k.sypPhaseUs[RJP_WORLD] >= 3000 && k.sypPhaseUs[RJP_WORLD] <= k.sypLoopUs[0] + k.sypLoopUs[1] + 2 &&  // self carries sub-us
           sum >= k.sypUs[1] - RJP_N && sum <= k.sypUs[1]);  // phases + other = the plan
    // the worker rows stay untouched
    for (int i = 0; i < RJP_N; i++) assert(k.rjpN[0][i] == 0 && k.rjpN[1][i] == 0 && k.rjpN[2][i] == 0);
    // 3. the IC path: SyncIc opens the session (the IC's goal step etc. included), the plan inside is nested; a lock frame
    g_sypTestRes = 1;
    k.asyncMode = 0;
    const IcPlanFn savedIc = g_icPlanOrig;
    g_icPlanOrig = SypTestIc;
    u8 out[64];
    assert(SyncIc(0, out, 0x1234, NULL, RFB_NOSTART, "nojob", NULL) == out && !g_rjpSyncTid && !t_sypIcIn);
    assert(k.sypEntry[0] == 1 && k.sypEntry[1] == 1 && k.sypPlans[0] == 1 && k.sypLoopN[0] == 2 && k.sypLines == 2);
    DWORD here = 0, lk = 0;
    assert(SypBegin(SYP_IC, &here) && g_rjpSyncTid == GetCurrentThreadId());
    assert(plan() == 1 && plan() == 1 && t_syp.planN == 2 && t_syp.open);  // nested Route_PlanLocked: the IC session stays open
    assert(SypLockBegin(&lk));
    Sleep(2);
    SypLockEnd(&lk);
    assert(t_syp.us[RJP_LOCK] >= 1500);
    SypEnd();
    assert(k.sypPlans[0] == 2 && k.sypEntry[1] == 1 && k.sypPhaseUs[RJP_LOCK] >= 1500 && !SypLockBegin(&lk));
    g_icPlanOrig = savedIc;
    // 4. another thread's session open: not timed
    g_rjpSyncTid = 1;
    assert(plan() == 1 && k.sypBusy == 1 && k.sypPlans[0] == 2);
    g_rjpSyncTid = 0;
    // 5. a session whose opening frame was unwound: ended by the next entry, which opens its own
    assert(SypBegin(SYP_PLANLOCKED, (DWORD*)(uintptr_t)4));
    assert(plan() == 1 && k.sypStale == 1 && k.sypPlans[2] == 1 && k.sypPlans[0] == 3 && !g_rjpSyncTid);
    // 6. a worker job on this thread: Route_PlanLocked goes to the job's rows, no session
    k.rjpReady = 1;
    RjpJobBegin(0);
    assert(plan() == 1 && k.rjpN[0][RJP_PLAN] == 1 && k.rjpN[0][RJP_WORLD] == 2 && k.sypEntry[1] == 3);
    RjpJobEnd(10);
    // the line
    SypPlan sp = {};
    sp.ent = "IC Plan/NoStart/nojob", sp.handle = 0xABCD, sp.planN = 3, sp.loopN[0] = 5, sp.loopUs[0] = 640000, sp.us[RJP_CELL] = 4100000;
    char line[480];
    SypLine(line, sizeof(line), sp, 7361, 1);
    assert(strstr(line, "sync plan 7361 ms failed, entry IC Plan/NoStart/nojob, route 0000ABCD, planlocked 3; loops entry=5/640 ") &&
           strstr(line, " cell=4100 ") && strstr(line, " other=0") && !strstr(line, "host="));
    // restore
    InterlockedExchange(&g_rjpAny[RJS_PLAN], 0);
    g_sypOn = false;
    k.rfbLogMs = savedLog, k.asyncMode = savedMode;
    g_rtSimTidSlot = savedSim;
    memcpy(g_sypLoopRet, savedRet, sizeof(savedRet));
    VirtualFree(code, 0, MEM_RELEASE);
    memset(&k.rjpReady, 0, offsetof(Ts3nCounters, rjpMaxUs) + sizeof(k.rjpMaxUs) - offsetof(Ts3nCounters, rjpReady));
    memset(&k.raAllocN, 0, offsetof(Ts3nCounters, raBytes) + sizeof(k.raBytes) - offsetof(Ts3nCounters, raAllocN));
    memset(&k.sypMode, 0, sizeof(Ts3nCounters) - offsetof(Ts3nCounters, sypMode));
    printf("route phases SyncPlans: layout, sim-only sessions (Route_PlanLocked and IC entry), loop split by caller, lock frame, "
           "busy / stale / worker job, line ok\n");
}
