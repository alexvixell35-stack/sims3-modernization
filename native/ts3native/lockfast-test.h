// TestLockFastPath (docs/165 F1): LockAcquireHook / LockReleaseHook pass a lock other than +0x918 from a thread that is
// neither the job owner nor the open sync plan session straight to the original; +0x918 and owner paths unchanged.
// Benchmark: ns per acquire+release pair through the hooks vs the originals (fake Lock_Acquire on a CRITICAL_SECTION).
// Included by selftest.cpp.
struct LfLock { CRITICAL_SECTION cs; u8 pad[0x20 - sizeof(CRITICAL_SECTION)]; LONG n; LONG pad2; };  // count at +0x20 as the game's
static_assert(offsetof(LfLock, n) == 0x20, "recursion count at +0x20 (LockReleaseHook reads it)");
static volatile LONG g_lfAcq, g_lfRel;
static LONG __fastcall LfAcquire(void* lock, void*, const DWORD*) {
    EnterCriticalSection(&((LfLock*)lock)->cs);
    return ++((LfLock*)lock)->n;
}
static LONG __fastcall LfRelease(void* lock, void*) {
    const LONG r = --((LfLock*)lock)->n;
    LeaveCriticalSection(&((LfLock*)lock)->cs);
    return r;
}
static LONG __fastcall LfAcquireCount(void* lock, void* edx, const DWORD* d) { g_lfAcq++; return LfAcquire(lock, edx, d); }
static LONG __fastcall LfReleaseCount(void* lock, void* edx) { g_lfRel++; return LfRelease(lock, edx); }

static LfLock g_lfBench;
static volatile LONG g_lfThroughHook;
static const int kLfThreadPairs = 250000;
static DWORD WINAPI LfBenchThread(void*) {
    const DWORD inf = 0xFFFFFFFF;
    if (g_lfThroughHook)
        for (int i = 0; i < kLfThreadPairs; i++) { LockAcquireHook(&g_lfBench, NULL, &inf); LockReleaseHook(&g_lfBench, NULL); }
    else
        for (int i = 0; i < kLfThreadPairs; i++) { g_lockOrig(&g_lfBench, NULL, &inf); g_rgRelOrig(&g_lfBench, NULL); }
    return 0;
}
// ns per pair: 4 threads on one lock, kLfThreadPairs each
static double LfContended(bool hook) {
    g_lfThroughHook = hook;
    LARGE_INTEGER t0, t1, fq;
    QueryPerformanceFrequency(&fq);
    HANDLE th[4];
    QueryPerformanceCounter(&t0);
    for (int i = 0; i < 4; i++) th[i] = CreateThread(NULL, 0, LfBenchThread, NULL, 0, NULL);
    WaitForMultipleObjects(4, th, TRUE, INFINITE);
    QueryPerformanceCounter(&t1);
    for (int i = 0; i < 4; i++) CloseHandle(th[i]);
    return (t1.QuadPart - t0.QuadPart) * 1e9 / fq.QuadPart / (4.0 * kLfThreadPairs);
}

// [HitchAttr] Holders=1 (docs/165 §8 gap #2): a real two-thread case. The holder (the "route worker") takes the lock through
// the hook and keeps it 40 ms; the "main" thread's acquire waits, and the record names the holder, its role and its site.
static LfLock g_hoLock;
static HANDLE g_hoHeld, g_hoDone;
static volatile DWORD g_hoHolderSite;
// docs/165 §8 zero-site causes, one holder mode each: 0 plain; 1 the holder's entry is written only after the waiter's wait
// began (cause A: the write after HoNote's log line; cause B: a hand-off read before the new holder's write); 2 the holder
// releases through the hook and takes the lock again before writing (cause C: the old clear-on-release)
static volatile LONG g_hoMode;
static const DWORD kHoLateSite = 0x00ABC123;
static __declspec(noinline) DWORD WINAPI HoHolderThread(void*) {
    const DWORD inf = 0xFFFFFFFF, me = GetCurrentThreadId();
    if (g_hoMode == 1) {
        g_lockOrig(&g_hoLock, NULL, &inf);  // held, not yet in the table
        g_hoHolderSite = kHoLateSite;
        SetEvent(g_hoHeld);
        Sleep(15);  // the waiter reads the table now: nothing for this thread
        HoPut(&g_hoLock, me, kHoLateSite);
        Sleep(25);
        LockReleaseHook(&g_hoLock, NULL);
    } else {
        LockAcquireHook(&g_hoLock, NULL, &inf);
        g_hoHolderSite = HoFind(&g_hoLock, me);
        if (g_hoMode == 2) {
            LockReleaseHook(&g_hoLock, NULL);
            g_lockOrig(&g_hoLock, NULL, &inf);  // the same caller again, its write still pending
        }
        SetEvent(g_hoHeld);
        Sleep(40);
        LockReleaseHook(&g_hoLock, NULL);
    }
    SetEvent(g_hoDone);
    return 0;
}
// main waits on a holder in mode `mode`; returns the record
static const HoRec& HoContended(LONG mode, DWORD* htidOut) {
    HoRing& g = ts3native_lockholders;
    const DWORD inf = 0xFFFFFFFF;
    const LONG head0 = g.head;
    memset((void*)g_hoTab, 0, sizeof(g_hoTab));  // no entry left from an earlier thread with a reused tid
    g_hoMode = mode;
    ResetEvent(g_hoHeld), ResetEvent(g_hoDone);
    DWORD htid = 0;
    HANDLE th = CreateThread(NULL, 0, HoHolderThread, NULL, CREATE_SUSPENDED, &htid);
    g_workerTid = htid;
    ResumeThread(th);
    WaitForSingleObject(g_hoHeld, INFINITE);
    assert(g_hoHolderSite != 0);
    assert(LockAcquireHook(&g_hoLock, NULL, &inf) == 1);
    const DWORD mySite = HoFind(&g_hoLock, GetCurrentThreadId());  // written before the record (cause A)
    assert(LockReleaseHook(&g_hoLock, NULL) == 0);
    WaitForSingleObject(g_hoDone, INFINITE);
    WaitForSingleObject(th, INFINITE);
    CloseHandle(th);
    assert(g.head == head0 + 1 && mySite != 0 && g.r[head0 & 63].waiterSite == mySite);
    *htidOut = htid;
    return g.r[head0 & 63];
}
static void TestLockHolders() {
    static_assert(offsetof(Ts3nCounters, hoReady) == 0x4214 && offsetof(Ts3nCounters, hoByRole) == 0x422C &&
                  offsetof(Ts3nCounters, hoNoSite) == 0x4240 && sizeof(Ts3nCounters) == 6030 * 4, "counters v98 layout (docs/registry.md)");
    Ts3nCounters& k = ts3native_counters;
    assert(k.version == 912);
    HoRing& g = ts3native_lockholders;
    const DWORD savedMain = g_mainTid, savedWorker = g_workerTid, me = GetCurrentThreadId(), inf = 0xFFFFFFFF;
    const LONG head0 = g.head, w0 = k.hoWaits[1], role0 = k.hoByRole[HO_WORKER], sim0 = k.hoWaits[0];
    InitializeCriticalSection(&g_hoLock.cs);
    g_hoHeld = CreateEventW(NULL, TRUE, FALSE, NULL);
    g_hoDone = CreateEventW(NULL, TRUE, FALSE, NULL);
    g_hoOn = 1;
    g_mainTid = me;  // this thread waits as the main thread
    // uncontended: the site is set on the outermost acquire only and stays after the release (never cleared), no record
    assert(LockAcquireHook(&g_hoLock, NULL, &inf) == 1 && HoFind(&g_hoLock, me) != 0);
    const DWORD mySite = HoFind(&g_hoLock, me);
    assert(LockAcquireHook(&g_hoLock, NULL, &inf) == 2 && HoFind(&g_hoLock, me) == mySite);  // recursive: not rewritten
    assert(LockReleaseHook(&g_hoLock, NULL) == 1 && LockReleaseHook(&g_hoLock, NULL) == 0);
    assert(HoFind(&g_hoLock, me) == mySite && HoFind(&g_hoLock, me + 4) == 0 && g.head == head0);
    // contended: holder thread keeps it 40 ms
    DWORD htid = 0;
    const HoRec& r = HoContended(0, &htid);
    assert(k.hoWaits[1] == w0 + 1 && k.hoWaits[0] == sim0 && k.hoByRole[HO_WORKER] == role0 + 1);
    assert(r.seq == head0 + 1 && r.waiter == 1 && r.lock == (DWORD)(uintptr_t)&g_hoLock && r.us >= 20000 && r.holderTid == htid &&
           r.holderRole == HO_WORKER && r.holderSite == g_hoHolderSite && k.hoMaxUs >= r.us);
    // causes A/B: the holder's entry appears only after main's wait began; 0.36.25 read it once at wait start (site 0)
    const LONG noSite0 = k.hoNoSite;
    const HoRec& rLate = HoContended(1, &htid);
    assert(rLate.holderTid == htid && rLate.holderSite == kHoLateSite && rLate.us >= 20000 && k.hoNoSite == noSite0);
    // cause C: the holder released (0.36.25 cleared its entry) and took the lock again with its write pending
    const HoRec& rAgain = HoContended(2, &htid);
    assert(rAgain.holderTid == htid && rAgain.holderSite == g_hoHolderSite && rAgain.holderSite != 0 && k.hoNoSite == noSite0);
    // cause D: set collisions. 4 locks in one set all stay; a 5th evicts the oldest (FIFO), the others stay
    {
        void* p[5];
        for (int i = 0; i < 5; i++) p[i] = (void*)(uintptr_t)(0x100000u * (i + 1) + 0xDF00);  // bits >= 20 only: one set
        for (int i = 1; i < 5; i++) assert(HoSetIx(p[i]) == HoSetIx(p[0]));
        memset((void*)g_hoTab, 0, sizeof(g_hoTab));
        g_hoNext[HoSetIx(p[0])] = 0;
        for (int i = 0; i < 4; i++) HoPut(p[i], 100 + i, 0x1000 + i);
        for (int i = 0; i < 4; i++) assert(HoFind(p[i], 100 + i) == (DWORD)(0x1000 + i));
        HoPut(p[1], 101, 0x2001);  // a re-acquire reuses its own way
        HoPut(p[4], 104, 0x1004);
        assert(HoFind(p[0], 100) == 0 && HoFind(p[1], 101) == 0x2001 && HoFind(p[2], 102) == 0x1002 && HoFind(p[3], 103) == 0x1003 &&
               HoFind(p[4], 104) == 0x1004 && HoFind(p[4], 105) == 0);
        memset((void*)g_hoTab, 0, sizeof(g_hoTab));
    }
    // a thread that is neither sim nor main: no record
    const LONG head1 = g.head;
    g_mainTid = savedMain;
    g_hoMode = 0;
    ResetEvent(g_hoHeld), ResetEvent(g_hoDone);
    HANDLE th = CreateThread(NULL, 0, HoHolderThread, NULL, 0, NULL);
    WaitForSingleObject(g_hoHeld, INFINITE);
    assert(LockAcquireHook(&g_hoLock, NULL, &inf) == 1 && LockReleaseHook(&g_hoLock, NULL) == 0);
    WaitForSingleObject(th, INFINITE);
    CloseHandle(th);
    assert(g.head == head1);
    g_hoOn = 0;
    g_workerTid = savedWorker;
    CloseHandle(g_hoHeld), CloseHandle(g_hoDone);
    DeleteCriticalSection(&g_hoLock.cs);
    printf("lock holders ([HitchAttr] Holders=1): main waited %ld ms on a lock held by the route worker %lu from 0x%08lX: ok\n",
           r.us / 1000, htid, r.holderSite);
}

static void TestLockFastPath() {
    Ts3nCounters& k = ts3native_counters;
    const LockAcquireFn savedAcq = g_lockOrig;
    const LockReleaseFn savedRel = g_rgRelOrig;
    void* const savedRg = g_rgLock;
    const DWORD savedOwner = g_async.owner, savedSync = g_rjpSyncTid;
    void* const savedAsyncLock = g_async.lock;
    const LONG savedOwnerTid = k.rgOwnerTid;
    const DWORD me = GetCurrentThreadId(), inf = 0xFFFFFFFF;
    static LfLock rg, other;
    InitializeCriticalSection(&rg.cs);
    InitializeCriticalSection(&other.cs);
    InitializeCriticalSectionAndSpinCount(&g_lfBench.cs, 0);
    g_lockOrig = LfAcquireCount;
    g_rgRelOrig = LfReleaseCount;
    g_rgLock = &rg;
    g_async.lock = &rg;
    g_async.owner = me + 1;  // this thread is not the job owner (the main thread / a loader in the game)
    g_rjpSyncTid = 0;

    // fast path: the original once each, no counter touched
    const LONG acq0 = k.rgAcquires, ql0 = k.ltQlHeldBlock, pump0 = k.asyncPumpWaits, bor0 = k.rgBorrows;
    g_lfAcq = g_lfRel = 0;
    assert(LockAcquireHook(&other, NULL, &inf) == 1 && LockAcquireHook(&other, NULL, &inf) == 2);
    assert(LockReleaseHook(&other, NULL) == 1 && LockReleaseHook(&other, NULL) == 0);
    assert(g_lfAcq == 2 && g_lfRel == 2 && k.rgAcquires == acq0 && k.ltQlHeldBlock == ql0 && k.asyncPumpWaits == pump0 &&
           k.rgBorrows == bor0 && k.rgOwnerTid == savedOwnerTid);
    // a QL-shared hold on this thread: the full path, QlSharedTrip counts
    t_qlShared = 1;
    assert(LockAcquireHook(&other, NULL, &inf) == 1 && k.ltQlHeldBlock == ql0 + 1);
    t_qlShared = 0;
    assert(LockReleaseHook(&other, NULL) == 0);
    // +0x918: the full path (counted acquire, owner tid set and cleared)
    assert(LockAcquireHook(&rg, NULL, &inf) == 1 && k.rgAcquires == acq0 + 1 && k.rgOwnerTid == (LONG)me);
    assert(LockReleaseHook(&rg, NULL) == 0 && k.rgOwnerTid == 0);
    // the job owner, another lock: the full path (no job busy: still the original once)
    g_async.owner = me;
    g_lfAcq = 0;
    assert(LockAcquireHook(&other, NULL, &inf) == 1 && g_lfAcq == 1 && LockReleaseHook(&other, NULL) == 0);
    // owner borrow (H6): the job's lock while the owner serves the job's callback
    const LONG savedState = g_async.state;
    const int savedInv = g_inInvoke;
    g_async.state = A_RUNNING, g_inInvoke = 1;
    g_lfAcq = g_lfRel = 0;
    assert(LockAcquireHook(&rg, NULL, &inf) == 2 && k.rgBorrows == bor0 + 1 && LockReleaseHook(&rg, NULL) == 1);
    assert(g_lfAcq == 0 && g_lfRel == 0);
    g_async.state = savedState, g_inInvoke = savedInv;
    g_async.owner = me + 1;

    // benchmark: uncontended pairs from a non-owner thread, then 4 threads on one lock
    g_lockOrig = LfAcquire;
    g_rgRelOrig = LfRelease;
    LARGE_INTEGER t0, t1, t2, fq;
    QueryPerformanceFrequency(&fq);
    const int N = 1000000;
    double best[2] = {1e9, 1e9};
    for (int rep = 0; rep < 3; rep++) {
        QueryPerformanceCounter(&t0);
        for (int i = 0; i < N; i++) { LockAcquireHook(&g_lfBench, NULL, &inf); LockReleaseHook(&g_lfBench, NULL); }
        QueryPerformanceCounter(&t1);
        for (int i = 0; i < N; i++) { g_lockOrig(&g_lfBench, NULL, &inf); g_rgRelOrig(&g_lfBench, NULL); }
        QueryPerformanceCounter(&t2);
        best[0] = min(best[0], (t1.QuadPart - t0.QuadPart) * 1e9 / fq.QuadPart / N);
        best[1] = min(best[1], (t2.QuadPart - t1.QuadPart) * 1e9 / fq.QuadPart / N);
    }
    const double cHook = LfContended(true), cOrig = LfContended(false);
    // the same pairs with [HitchAttr] Holders=1 (side table set + clear on every outermost acquire / release)
    g_hoOn = 1;
    double bestHo = 1e9;
    for (int rep = 0; rep < 3; rep++) {
        QueryPerformanceCounter(&t0);
        for (int i = 0; i < N; i++) { LockAcquireHook(&g_lfBench, NULL, &inf); LockReleaseHook(&g_lfBench, NULL); }
        QueryPerformanceCounter(&t1);
        bestHo = min(bestHo, (t1.QuadPart - t0.QuadPart) * 1e9 / fq.QuadPart / N);
    }
    g_hoOn = 0;
    assert(g_lfBench.n == 0 && HoFind(&g_lfBench, GetCurrentThreadId()) != 0);  // entries stay after the release (docs/165 §8)
    printf("lock fast path (docs/165 F1): non-918 acquire+release, non-owner: hook %.1f ns, original %.1f ns (+%.1f ns per pair); "
           "4 threads on one lock: hook %.1f ns, original %.1f ns per pair; with [HitchAttr] Holders=1: %.1f ns\n",
           best[0], best[1], best[0] - best[1], cHook, cOrig, bestHo);

    TestLockHolders();

    g_lockOrig = savedAcq;
    g_rgRelOrig = savedRel;
    g_rgLock = savedRg;
    g_async.owner = savedOwner;
    g_async.lock = savedAsyncLock;
    g_rjpSyncTid = savedSync;
    k.rgOwnerTid = savedOwnerTid;
    DeleteCriticalSection(&rg.cs);
    DeleteCriticalSection(&other.cs);
    DeleteCriticalSection(&g_lfBench.cs);
}
