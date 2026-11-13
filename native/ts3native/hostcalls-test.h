// --- host-call split and door memo (hostcalls.cpp, docs/159): layout, door stub on a dummy call site and in both chains, memo /
// shadow / pass-through paths, per-site stamps, a real two-thread proxy round trip, limiter overlap. Included by selftest.cpp.
static volatile DWORD g_hcFakeAns;
static volatile LONG g_hcFakeCalls;
static DWORD __cdecl HcFakeDoor(DWORD, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD) {
    InterlockedIncrement(&g_hcFakeCalls);
    return g_hcFakeAns;
}
static DWORD HcAsk(DWORD lot, DWORD roomJunk = 0) {
    return ((P4DoorFn)HcDoorStub)(lot, 0, 7, 0, 9, 0, roomJunk | 3, roomJunk | 4, roomJunk | 1);
}
static void* __fastcall HcFakeHost(void*, void*, void* out, void*, void*, void*) { return out; }
static DWORD WINAPI HcWorkerThread(void* p) {
    t_hcJob = 1;
    InvokeJob v = {{p, NULL, NULL, NULL}, (void*)0x10, NULL};
    Proxy(v);
    t_hcJob = 0;
    return 0;
}

static void TestHostCalls() {
    static_assert(offsetof(Ts3nCounters, hcReady) == 0x4088 && offsetof(Ts3nCounters, dmMode) == 0x408C &&
                  offsetof(Ts3nCounters, hcN) == 0x4090 && offsetof(Ts3nCounters, hcQueueUs) == 0x40A8 &&
                  offsetof(Ts3nCounters, hcQueueMaxUs) == 0x40C0 && offsetof(Ts3nCounters, hcQueueSleepUs) == 0x40D4 &&
                  offsetof(Ts3nCounters, hcOtherCalls) == 0x40DC && offsetof(Ts3nCounters, hcDoorQ) == 0x40E0 &&
                  offsetof(Ts3nCounters, hcDoorRepeatJobWaitUs) == 0x40E8 && offsetof(Ts3nCounters, hcDoorFull) == 0x40F8 &&
                  offsetof(Ts3nCounters, dmHits) == 0x4100 && offsetof(Ts3nCounters, dmFb) == 0x4108 &&
                  offsetof(Ts3nCounters, dmMismatch) == 0x411C && sizeof(Ts3nCounters) == 6323 * 4,
                  "counters v96 layout after v93 (docs/registry.md)");
    Ts3nCounters& k = ts3native_counters;
    assert(k.version == 912);
    const size_t v96 = offsetof(Ts3nCounters, hcReady);
    memset((u8*)&k + v96, 0, sizeof(Ts3nCounters) - v96);
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
    const LONGLONG ms = g_qpf.QuadPart / 1000;
    const DWORD me = GetCurrentThreadId(), savedWorker = g_workerTid;
    InvokeJob* const savedInv = g_invJob;
    g_invJob = NULL;

    // install on a dummy call site: `call fake` -> rel32 to our stub; other bytes refused
    u8* code = (u8*)VirtualAlloc(NULL, 4096, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
    assert(code);
    code[0] = 0xE8;
    *(LONG*)(code + 1) = (LONG)((u8*)HcFakeDoor - (code + 5));
    assert(HcInstallDoor((DWORD)(uintptr_t)code + 1, (DWORD)(uintptr_t)HcFakeDoor) == 0);
    assert(HcInstallDoor((DWORD)(uintptr_t)code, (DWORD)(uintptr_t)HcFakeDoor) == 3 && code + 5 + *(LONG*)(code + 1) == (u8*)HcDoorStub &&
           g_hcDoorJmp == (void*)HcFakeDoor);

    // both off: straight through
    g_hcFakeAns = 1;
    g_workerTid = me;
    t_hcJob = 1;
    assert(HcAsk(100) == 1 && g_hcFakeCalls == 1 && k.hcDoorQ == 0);
    // HostSplit only: repeats counted and still asked; an answer change on a repeat is counted; junk above the room words ignored
    g_hcOn = 1;
    assert(HcAsk(100) == 1 && HcAsk(100, 0x55550000) == 1 && g_hcFakeCalls == 3);
    assert(k.hcDoorQ == 2 && k.hcDoorRepeatJob == 1 && k.hcDoorRepeatDiff == 0 && k.dmHits == 0);
    g_hcFakeAns = 2;
    assert(HcAsk(100) == 2 && k.hcDoorRepeatJob == 2 && k.hcDoorRepeatDiff == 1 && g_hcFakeCalls == 4);
    // not the worker / no job: pass through, nothing counted
    t_hcJob = 0;
    assert(HcAsk(100) == 2 && g_hcFakeCalls == 5 && k.hcDoorQ == 3);
    // DoorMemo=1: job 2 asks B (miss, asked), repeats come from the memo even though the sim's answer changed
    k.dmMode = 1;
    t_hcJob = 2;
    g_hcFakeAns = 3;
    assert(HcAsk(200) == 3 && k.dmMisses == 1 && g_hcFakeCalls == 6);
    g_hcFakeAns = 0;
    assert(HcAsk(200) == 3 && HcAsk(200, 0x12340000) == 3 && k.dmHits == 2 && g_hcFakeCalls == 6);
    assert(HcAsk(201) == 0 && k.dmMisses == 2 && g_hcFakeCalls == 7);  // another key: asked
    // memo fall-through: another thread, a plan nested in a managed call, a clone run
    g_workerTid = me + 7;
    assert(HcAsk(200) == 0 && k.dmFb[0] == 1 && g_hcFakeCalls == 8);
    g_workerTid = me;
    InvokeJob nest = {};
    g_invJob = &nest;
    assert(HcAsk(200) == 0 && k.dmFb[1] == 1 && g_hcFakeCalls == 9);
    g_invJob = NULL;
    t_snapRun = 1;
    assert(HcAsk(200) == 0 && k.dmFb[3] == 1 && g_hcFakeCalls == 10);
    t_snapRun = 0;
    // a new job forgets job 2's answers
    t_hcJob = 3;
    assert(HcAsk(200) == 0 && k.dmMisses == 3 && g_hcFakeCalls == 11);
    // the cross-job ring saw key 200 from job 2 within 1 s with another answer
    assert(k.hcDoorRepeatXJob >= 1 && k.hcDoorRepeatXJobDiff >= 1);
    // DoorMemo=2 shadow: repeats asked, compared
    k.dmMode = 2;
    t_hcJob = 4;
    g_hcFakeAns = 5;
    assert(HcAsk(300) == 5);
    g_hcFakeAns = 6;
    const LONG calls = g_hcFakeCalls;
    assert(HcAsk(300) == 6 && k.dmShadow == 1 && k.dmMismatch == 1 && g_hcFakeCalls == calls + 1);
    g_hcFakeAns = 6;
    assert(HcAsk(300) == 6 && k.dmShadow == 2 && k.dmMismatch == 2);  // compared with the first ask (5), not the last
    // a full table: the 65th key is asked and not kept
    k.dmMode = 1;
    t_hcJob = 5;
    for (DWORD i = 0; i < 65; i++) HcAsk(1000 + i);
    assert(k.dmFb[2] == 1 && k.hcDoorFull == 1 && g_hcTab.n == 64);
    const LONG c2 = g_hcFakeCalls;
    assert(HcAsk(1064) == 6 && g_hcFakeCalls == c2 + 1 && HcAsk(1000) == 6 && g_hcFakeCalls == c2 + 1);  // 1064 untracked, 1000 memo
    assert(k.hcDoorWaitUs >= 0 && k.hcDoorRepeatJobWaitUs >= 0);
    // chains: behind P4's stub (its call site or [ProbeValues]' slot), and in [ProbeValues]' slot
    void* const p4Save = g_p4DoorJmp;
    const LONG pvOn = g_pvOn;
    void* const pvSave = g_pvJmp[PV_QUERY];
    u8* site = code + 64;
    site[0] = 0xE8;
    *(LONG*)(site + 1) = (LONG)((u8*)P4DoorStub - (site + 5));
    g_p4DoorJmp = (void*)HcFakeDoor;
    assert(HcInstallDoor((DWORD)(uintptr_t)site, (DWORD)(uintptr_t)HcFakeDoor) == 1 && g_p4DoorJmp == (void*)HcDoorStub &&
           g_hcDoorJmp == (void*)HcFakeDoor);
    const LONG hits = k.dmHits;
    assert(((P4DoorFn)P4DoorStub)(1000, 0, 7, 0, 9, 0, 3, 4, 1) == 6 && k.dmHits == hits + 1);  // P4 passes through to us: memo hit
    g_p4DoorJmp = NULL;
    g_pvOn = 1;
    g_pvJmp[PV_QUERY] = (void*)HcFakeDoor;
    assert(HcInstallDoor(kPv[PV_QUERY].at, (DWORD)(uintptr_t)HcFakeDoor) == 2 && g_pvJmp[PV_QUERY] == (void*)HcDoorStub &&
           g_hcDoorJmp == (void*)HcFakeDoor);
    g_pvJmp[PV_QUERY] = (void*)P4DoorStub;  // P4 already in [ProbeValues]' slot: behind P4
    g_p4DoorJmp = (void*)HcFakeDoor;
    assert(HcInstallDoor(kPv[PV_QUERY].at, (DWORD)(uintptr_t)HcFakeDoor) == 1 && g_p4DoorJmp == (void*)HcDoorStub);
    g_p4DoorJmp = p4Save, g_pvOn = pvOn, g_pvJmp[PV_QUERY] = pvSave;
    k.dmMode = 0;

    // stamps per serve site (fake times): queue, service minus nested, return, nested, other calls
    for (int s = 0; s < HC_SITES; s++) {
        InvokeJob v = {};
        t_hcJob = 1;
        HcPost(v);
        assert(v.tPost && g_hcPending == 1);
        v.tPost -= 2 * ms;
        HcPick(v, s);
        assert(g_hcPending == 0 && k.hcN[s] == 1 && k.hcQueueUs[s] >= 2000 && k.hcQueueUs[s] < 50000);
        v.tDone = v.tPick + 3 * ms, v.nested = ms;
        HcWake(v);
    }
    assert(k.hcServeUs >= 6 * 1990 && k.hcServeUs <= 6 * 2010 && k.hcNestedUs >= 6 * 990 && k.hcOtherCalls == 6);
    t_hcJob = 0;
    InvokeJob off = {};
    HcPost(off);
    assert(!off.tPost);  // no job: not stamped
    // limiter window: overlap of [post, pick] with the ring's intervals; a pass that ends with a call pending
    g_hcRingOn = 1;
    HcLimiterIn();
    g_hcPending = 1;
    HcLimiterOut();
    assert(k.hcSleepN == 1 && g_hcRingI == 1);
    g_hcPending = 0;
    const LONGLONG p0 = HcNow();
    memset(g_hcRing, 0, sizeof(g_hcRing));
    g_hcRing[0] = {p0 + ms, p0 + 3 * ms};    // inside: 2 ms
    g_hcRing[1] = {p0 - 5 * ms, p0 + ms};    // started before the post: 1 ms
    g_hcRing[2] = {p0 + 4 * ms, 0};          // still open at the pick-up: 1 ms
    assert(HcSleepOverlap(p0, p0 + 5 * ms) == 4 * ms);
    memset(g_hcRing, 0, sizeof(g_hcRing));
    g_hcRingI = 0, g_hcRingOn = 0;

    // a real round trip: a worker thread proxies a host call, this thread serves it at the READY poll ~3 ms later
    const HANDLE sReady = g_invReady, sDone = g_invDone, sJob = g_jobReady;
    const HostCallFn sHost = g_hostCallOrig;
    DWORD* const sHostPtr = g_hostPtr;
    static DWORD noHost = 0;  // no MonoScriptHost: ServeInvoke skips the task id pair
    g_hostPtr = &noHost;
    g_invReady = CreateEventW(NULL, FALSE, FALSE, NULL);
    g_invDone = CreateEventW(NULL, FALSE, FALSE, NULL);
    g_jobReady = CreateEventW(NULL, FALSE, FALSE, NULL);
    g_hostCallOrig = HcFakeHost;
    const LONG n2 = k.hcN[HC_READY], other = k.hcOtherCalls;
    DWORD out = 0;
    const HANDLE th = CreateThread(NULL, 0, HcWorkerThread, &out, 0, NULL);
    assert(WaitForSingleObject(g_invReady, 5000) == WAIT_OBJECT_0);
    Sleep(3);
    ServeInvoke(HC_READY);
    assert(WaitForSingleObject(th, 5000) == WAIT_OBJECT_0);
    CloseHandle(th);
    assert(k.hcN[HC_READY] == n2 + 1 && k.hcQueueUs[HC_READY] >= 2000 + 2500 && k.hcOtherCalls == other + 1);
    CloseHandle(g_invReady), CloseHandle(g_invDone), CloseHandle(g_jobReady);
    g_invReady = sReady, g_invDone = sDone, g_jobReady = sJob, g_hostCallOrig = sHost, g_hostPtr = sHostPtr;

    g_hcOn = 0;
    g_workerTid = savedWorker;
    g_invJob = savedInv;
    VirtualFree(code, 0, MEM_RELEASE);
    memset((u8*)&k + v96, 0, sizeof(Ts3nCounters) - v96);
    printf("host-call split + door memo (v96): layout, door stub (rel32, behind P4, [ProbeValues] slot), memo / shadow / fall-through, "
           "per-job table and full, cross-job ring, stamps per site, limiter overlap, real proxy round trip ok\n");
}
