// --- [RouteSnap] AskLent and DoorMemoX (docs/189 phase 3, counters v126): selftest. Included by selftest.cpp after
// hostcalls-test.h (its fake door, HcAsk, HcFakeHost) and p5b-test.h (P5bAcq / P5bRel on a real CRITICAL_SECTION).
// AskLent, threaded: a worker thread in a P5c window posts a door ask with the lock lent (P5bAsk -> Proxy), this thread (the
// sim, the jobs' owner) serves it; the callback takes the real lent lock (no borrow), writes a recorded routing write, and the
// job's answer is refused stale. Then: admission rules, the retake path without a P5c window, a tripwire during an ask.
// DoorMemoX: hit / miss / "locked" not kept / invalidation by portal IC, world event, delegate and age / shadow mismatch / flip.
static void* g_auLock;
static volatile LONG g_auCbRet, g_auCbMine, g_auStale;
static void* __fastcall AuFakeHost(void*, void*, void* out, void*, void*, void*) {  // the event 59 handler on the sim
    static const DWORD inf = 0xFFFFFFFF;
    g_auCbRet = LockAcquireHook(g_auLock, NULL, &inf);
    g_auCbMine = P5bOwner(g_auLock) == (LONG)GetCurrentThreadId();
    InterlockedIncrement(&g_spWrites);  // a nested plan's recorded routing write
    LockReleaseHook(g_auLock, NULL);
    return out;
}
static DWORD WINAPI AuWorker(void*) {
    t_p5b = P5B_OPEN;  // inside the clone run's lent window
    t_auJob = false;
    t_p5cRan = true;
    const DWORD seq = 77;
    g_p5cCut[seq & 63].w = g_spWrites;
    InterlockedExchange(&g_p5cCut[seq & 63].seq, (LONG)seq);
    P5bAsk(true);
    const bool lentAsk = g_auAsk == 1 && t_p5b == P5B_OPEN;
    DWORD out = 0;
    InvokeJob v = {{&out, NULL, NULL, NULL}, (void*)0x10, NULL};
    Proxy(v);
    P5bAsk(false);
    g_auStale = lentAsk && !g_auAsk && t_p5b == P5B_OPEN && P5cStale(seq) ? 1 : 0;
    t_p5b = P5B_CLOSED;
    return 0;
}

static void TestAskLent() {
    static_assert(offsetof(Ts3nCounters, auMode) == 0x4FFC && offsetof(Ts3nCounters, auStaleAfter) == 0x500C &&
                      offsetof(Ts3nCounters, dmxMode) == 0x5014 && offsetof(Ts3nCounters, dmxMismatch) == 0x5028 &&
                      offsetof(Ts3nCounters, auSpare) == 0x5030 && sizeof(Ts3nCounters) == 6314 * 4,
                  "counters v126 layout (docs/registry.md)");
    Ts3nCounters& k = ts3native_counters;
    assert(k.version == 912);
    const size_t v126 = offsetof(Ts3nCounters, auMode);
    memset((u8*)&k + v126, 0, sizeof(Ts3nCounters) - v126);
    const DWORD me = GetCurrentThreadId();
    u8* w = (u8*)VirtualAlloc(NULL, 0x1000, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    void* lock = w + 0x918;
    InitializeCriticalSection((CRITICAL_SECTION*)lock);
    g_auLock = lock;
    const LockAcquireFn sAcq = g_lockOrig;
    const LockReleaseFn sRel = g_rgRelOrig;
    void* const sRgLock = g_rgLock;
    const AsyncSlot sAsync = g_async;
    const LONG sP5bOn = g_p5bOn, sRelease = k.p5bRelease, sTrip = k.p5bRetakeTrip, sAskRt = k.p5bRetakeAsk, sAuAcq = k.p5cAcq;
    g_lockOrig = P5bAcq, g_rgRelOrig = P5bRel, g_rgLock = lock;
    g_async.owner = me, g_async.lock = lock, g_async.state = A_RUNNING;
    g_p5bOn = 1, k.p5bRelease = 1;

    // admission: inside a job callback only an AskLent ask admits, also under an outer gate wait's key
    InterlockedExchange(&g_p5bWin, 1);
    g_inInvoke = 1;
    assert(P5bAllowed(lock) == P5B_NO);
    g_auAsk = 1;
    assert(P5bAllowed(lock) == P5B_AU);
    t_waitKey = 0x00760BFF;
    assert(P5bAllowed(lock) == P5B_AU);
    t_waitKey = 0;
    g_auAsk = 0, g_inInvoke = 0;
    InterlockedExchange(&g_p5bWin, 0);

    // threaded: the ask goes out lent, the callback takes the real lock, the answer is stale
    const HANDLE sReady = g_invReady, sDone = g_invDone, sJob = g_jobReady;
    const HostCallFn sHost = g_hostCallOrig;
    DWORD* const sHostPtr = g_hostPtr;
    static DWORD noHost = 0;
    g_hostPtr = &noHost;
    g_invReady = CreateEventW(NULL, FALSE, FALSE, NULL);
    g_invDone = CreateEventW(NULL, FALSE, FALSE, NULL);
    g_jobReady = CreateEventW(NULL, FALSE, FALSE, NULL);
    g_hostCallOrig = AuFakeHost;
    k.auMode = 1;
    g_p5cJob = 1;
    g_p5bLock = lock, g_p5bN = 1;
    InterlockedExchange(&g_p5bWin, 1);  // the worker's window: the lock is free (lent)
    const LONG bor0 = k.rgBorrows;
    const HANDLE th = CreateThread(NULL, 0, AuWorker, NULL, 0, NULL);
    assert(WaitForSingleObject(g_invReady, 5000) == WAIT_OBJECT_0);
    assert(g_auAsk == 1 && k.auAsks == 1 && k.p5bRetakeAsk == sAskRt);  // posted with no retake
    ServeInvoke(HC_READY);
    assert(WaitForSingleObject(th, 5000) == WAIT_OBJECT_0);
    CloseHandle(th);
    assert(g_auCbRet == 1 && g_auCbMine && k.rgBorrows == bor0 && k.auAcq == 1);  // a real acquire, not a borrow
    assert(g_auStale == 1 && k.auStaleAfter == 1 && !g_auAsk);
    assert(P5bOwner(lock) == 0);  // the callback gave it back: the window's lock is free again
    CloseHandle(g_invReady), CloseHandle(g_invDone), CloseHandle(g_jobReady);
    g_invReady = sReady, g_invDone = sDone, g_jobReady = sJob, g_hostCallOrig = sHost, g_hostPtr = sHostPtr;

    // no P5c window (P5b alone): retaken as before, counted; the reopen is refused here (switch off), the hold stays ours
    g_p5cJob = 0;
    t_p5b = P5B_OPEN;
    P5bAsk(true);
    assert(k.auSkip == 1 && k.p5bRetakeAsk == sAskRt + 1 && t_p5b == P5B_ASK && !g_auAsk && P5bOwner(lock) == (LONG)me);
    k.p5bRelease = 0;
    P5bAsk(false);
    assert(t_p5b == P5B_DONE);
    P5bRel(lock, NULL);
    k.p5bRelease = 1;
    // switch off: retake as before, nothing of ours counted
    k.auMode = 0, g_p5cJob = 1;
    InterlockedExchange(&g_p5bWin, 1);
    t_p5b = P5B_OPEN;
    P5bAsk(true);
    assert(k.auSkip == 1 && k.auAsks == 1 && t_p5b == P5B_ASK && !g_auAsk);
    P5bRel(lock, NULL);
    // a tripwire during a lent ask: the flag closes, the hold comes back for the rest of the run
    k.auMode = 1;
    InterlockedExchange(&g_p5bWin, 1);
    t_p5b = P5B_OPEN;
    P5bAsk(true);
    assert(g_auAsk == 1 && t_p5b == P5B_OPEN);
    P5bAbort();
    assert(!g_auAsk && t_p5b == P5B_DONE && !g_p5bWin && P5bOwner(lock) == (LONG)me && k.p5bRetakeTrip == sTrip + 1);
    P5bRel(lock, NULL);
    t_p5b = P5B_CLOSED;
    g_p5cJob = 0, k.auMode = 0;
    g_p5bLock = NULL, g_p5bN = 0;

    g_lockOrig = sAcq, g_rgRelOrig = sRel, g_rgLock = sRgLock, g_async = sAsync;
    g_p5bOn = sP5bOn, k.p5bRelease = sRelease, k.p5cAcq = sAuAcq;
    k.rgOwnerTid = 0;
    DeleteCriticalSection((CRITICAL_SECTION*)lock);
    VirtualFree(w, 0, MEM_RELEASE);

    // --- DoorMemoX on a dummy call site ---
    u8* code = (u8*)VirtualAlloc(NULL, 4096, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
    assert(code);
    code[0] = 0xE8;
    *(LONG*)(code + 1) = (LONG)((u8*)HcFakeDoor - (code + 5));
    void* const sDoorJmp = g_hcDoorJmp;
    assert(HcInstallDoor((DWORD)(uintptr_t)code, (DWORD)(uintptr_t)HcFakeDoor) == 3);
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
    const DWORD sWorker = g_workerTid;
    InvokeJob* const sInv = g_invJob;
    g_workerTid = me, g_invJob = NULL;
    const LONG sDm = k.dmMode, sOn = g_hcOn;
    k.dmMode = 0, g_hcOn = 0;
    g_dmxTicks = g_qpf.QuadPart / 4;  // 250 ms
    g_dmxDelegate = NULL;
    k.dmxMode = 1, g_dmxLast = 0;
    LONG c0 = g_hcFakeCalls;
    t_hcJob = 10, g_hcFakeAns = 2;
    assert(HcAsk(400) == 2 && k.dmxClear == 1 && k.dmxMiss == 1 && g_hcFakeCalls == c0 + 1);  // flip clears, miss, kept
    t_hcJob = 11, g_hcFakeAns = 1;
    assert(HcAsk(400) == 2 && HcAsk(400, 0x77770000) == 2 && k.dmxHit == 2 && g_hcFakeCalls == c0 + 1);  // other job: memo
    t_hcJob = 12;
    assert(HcAsk(401) == 1 && k.dmxMiss == 2);  // "locked": asked, not kept
    t_hcJob = 13;
    assert(HcAsk(401) == 1 && k.dmxMiss == 3 && g_hcFakeCalls == c0 + 3);
    k.snIc[14]++;  // SetPortalLockStatus: the row no longer holds
    t_hcJob = 14;
    assert(HcAsk(400) == 1 && k.dmxInval == 1 && g_hcFakeCalls == c0 + 4);
    g_hcFakeAns = 2;
    t_hcJob = 15;
    assert(HcAsk(400) == 2 && k.dmxInval == 2);  // the old row seen again (newest for the key), then kept under the new generation
    t_hcJob = 16, g_hcFakeAns = 1;
    assert(HcAsk(400) == 2 && k.dmxHit == 3);
    k.snWev[0x0E]++;  // a lot world event
    assert(HcAsk(400) == 1 && k.dmxInval == 3);
    static DWORD deleg = 0x1000;  // a mod's += re-registers the event 59 delegate
    g_dmxDelegate = &deleg;
    g_hcFakeAns = 2;
    t_hcJob = 17;
    assert(HcAsk(402) == 2);
    deleg = 0x2000;
    t_hcJob = 18, g_hcFakeAns = 1;
    assert(HcAsk(402) == 1 && k.dmxInval == 4);
    g_dmxDelegate = NULL;
    g_hcFakeAns = 2;
    assert(HcAsk(403) == 2);
    g_dmxTicks = 0;  // older than the age cap
    Sleep(1);
    t_hcJob = 19;
    assert(HcAsk(403) == 2 && k.dmxInval == 5);
    g_dmxTicks = g_qpf.QuadPart / 4;
    // shadow: the flip clears, valid rows asked anyway and compared
    k.dmxMode = 2;
    t_hcJob = 20, g_hcFakeAns = 2;
    c0 = g_hcFakeCalls;
    assert(HcAsk(500) == 2 && k.dmxClear == 2 && g_hcFakeCalls == c0 + 1);
    t_hcJob = 21, g_hcFakeAns = 1;
    assert(HcAsk(500) == 1 && k.dmxShadow == 1 && k.dmxMismatch == 1 && g_hcFakeCalls == c0 + 2);
    // back to 1: rows from shadow mode are dropped; off: straight through, nothing counted
    k.dmxMode = 1, g_hcFakeAns = 2;
    t_hcJob = 22;
    assert(HcAsk(500) == 2 && k.dmxClear == 3 && k.dmxHit == 3);
    k.dmxMode = 0;
    const LONG miss = k.dmxMiss;
    assert(HcAsk(500) == 2 && k.dmxMiss == miss);
    // pass-through rules as DoorMemo: a clone run, another thread
    k.dmxMode = 1;
    t_snapRun = 1;
    c0 = g_hcFakeCalls;
    assert(HcAsk(500) == 2 && g_hcFakeCalls == c0 + 1);
    t_snapRun = 0;
    g_workerTid = me + 7;
    assert(HcAsk(500) == 2 && g_hcFakeCalls == c0 + 2);
    k.dmxMode = 0;
    t_hcJob = 0;
    g_workerTid = sWorker, g_invJob = sInv, k.dmMode = sDm, g_hcOn = sOn, g_hcDoorJmp = sDoorJmp;
    memset(g_dmx, 0, sizeof(g_dmx));
    g_dmxLast = 0;
    VirtualFree(code, 0, MEM_RELEASE);
    memset((u8*)&k + v126, 0, sizeof(Ts3nCounters) - v126);
    printf("AskLent + DoorMemoX (v126): lent ask served on a second thread (real acquire, no borrow, stale refused), admission, "
           "retake without a P5c window / switch off, tripwire mid-ask; memo hit / miss / locked not kept / portal IC, world event, "
           "delegate, age invalidation / shadow mismatch / flip clears ok\n");
}
