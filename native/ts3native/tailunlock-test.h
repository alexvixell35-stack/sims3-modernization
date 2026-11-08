// [RouteAsync] TailUnlock (tailunlock.cpp, docs/150 §11): selftest. Included by selftest.cpp after p5c-test.h (P5bAcq, P5bRel,
// P5bSpin, P5bMine, g_p5bW from p5b-test.h). Threaded: the test thread is the sim (the jobs' owner), a second thread the route
// worker running each job as ServeAsync and Route_PlanLocked do (two +0x918 levels, A_RUNNING), then the post pass's tail
// through the four call-site stubs on fake passes 16-19 that spin and rewrite a job-private path. +0x918 is a real
// CRITICAL_SECTION with the game's own count at +0x20. While each job runs the sim makes steering acquires (t_steerRet, as
// the RT_STEER frame sets it: admitted while lent) and plain tries (held out). Checks: every answer equals the passes called
// directly, the worker owns both levels after every tail and never inside a window, no tripwire, no hang; a pass that fails
// (the hold back at its return), a pass that takes +0x918 itself (tripwire 0, the hold back first), refusals.
static int g_tuFail;           // the fake pass (16, 17, 18) that answers false; 0 = none
static bool g_tuTake;          // fake pass 17 takes +0x918 itself (the tripwire case)
static bool g_tuThrow;         // CE-4: fake pass 17 raises inside the window; caught in Route_PlanLocked (the worker below)
static volatile LONG g_tuThrew;
static volatile LONG g_tuBad, g_tuBody, g_tuStop;
static HANDLE g_tuGo, g_tuDone;
static DWORD g_tuPath[16];
static bool g_tuR;

static void TuFakeStep(void* pp, DWORD salt) {  // a pass's work on the job's own path; lent while the worker runs it
    if (GetCurrentThreadId() == g_workerTid && (P5bMine() != !t_tuOpen)) InterlockedIncrement(&g_tuBad);
    P5bSpin(150);
    DWORD* p = (DWORD*)pp;
    for (int i = 0; i < 16; i++) p[i] = (p[i] ^ (salt + i)) * 16777619u;
}
static bool __fastcall TuF16(void* pp, void*, DWORD a, DWORD b) { TuFakeStep(pp, a * 3 + b); return g_tuFail != 16; }
static bool __fastcall TuF17(void* pp, void*, DWORD a, DWORD b, DWORD c) {
    TuFakeStep(pp, a + b * 5 + c);
    if (g_tuThrow && t_tuOpen) RaiseException(0xE0000154, 0, 0, NULL);
    if (g_tuTake) {  // a guarded touch: the worker's acquire takes its hold back first, then nests
        static const DWORD kInf = 0xFFFFFFFF;
        LockAcquireHook(g_p5bW + 0x918, NULL, &kInf);
        if (!P5bMine() || t_tuOpen) InterlockedIncrement(&g_tuBad);
        LockReleaseHook(g_p5bW + 0x918, NULL);
    }
    return g_tuFail != 17;
}
static bool __fastcall TuF18(void* pp, void*, DWORD a) { TuFakeStep(pp, a); return g_tuFail != 18; }
static bool __fastcall TuF19(void* pp, void*, DWORD a) { TuFakeStep(pp, a + 99); return true; }
static bool TuTail(bool stubs) {  // 0x64E380's tail: each pass false = the post pass returns false at once
    if (stubs) return TuS16(g_tuPath, NULL, 1, 2) && TuS17(g_tuPath, NULL, 3, 4, 5) && TuS18(g_tuPath, NULL, 6) && TuS19(g_tuPath, NULL, 7);
    return TuF16(g_tuPath, NULL, 1, 2) && TuF17(g_tuPath, NULL, 3, 4, 5) && TuF18(g_tuPath, NULL, 6) && TuF19(g_tuPath, NULL, 7);
}

static DWORD WINAPI TuWorker(LPVOID) {
    static const DWORD kInf = 0xFFFFFFFF;
    for (;;) {
        WaitForSingleObject(g_tuGo, INFINITE);
        if (g_tuStop) return 0;
        void* lock = g_p5bW + 0x918;
        LockAcquireHook(lock, NULL, &kInf);  // ServeAsync's hold
        g_async.lock = lock;
        InterlockedExchange(&g_async.state, A_RUNNING);
        LockAcquireHook(lock, NULL, &kInf);  // Route_PlanLocked 0x82C733
        InterlockedExchange(&g_tuBody, 1);
        const CRITICAL_SECTION* cs = (const CRITICAL_SECTION*)lock;
        if (g_tuThrow) {  // CE-4: the unwind skips TuClose; Route_PlanLocked's own release then meets the open window
            __try { g_tuR = TuTail(true); } __except (EXCEPTION_EXECUTE_HANDLER) { g_tuThrew = t_tuOpen; }
            InterlockedExchange(&g_tuBody, 0);
            LockReleaseHook(lock, NULL);  // backstop: every level back first, then this release is the worker's own
            if (t_tuOpen || g_p5bWin || !P5bMine() || cs->RecursionCount != 1) InterlockedIncrement(&g_tuBad);
            InterlockedExchange(&g_async.state, A_IDLE);
            LockReleaseHook(lock, NULL);
            SetEvent(g_asyncDone);
            SetEvent(g_tuDone);
            continue;
        }
        g_tuR = TuTail(true);
        if (t_tuOpen || g_p5bWin || !P5bMine() || cs->RecursionCount != 2 || *(LONG*)((u8*)lock + 0x20) != 2)
            InterlockedIncrement(&g_tuBad);
        InterlockedExchange(&g_tuBody, 0);
        LockReleaseHook(lock, NULL);
        InterlockedExchange(&g_async.state, A_IDLE);
        LockReleaseHook(lock, NULL);
        SetEvent(g_asyncDone);
        SetEvent(g_tuDone);
    }
}

static void TestTailUnlock() {
    Ts3nCounters& k = ts3native_counters;
    static_assert(offsetof(Ts3nCounters, tuMode) == 0x3878 && offsetof(Ts3nCounters, tuRefused) == 0x3884 &&
                  offsetof(Ts3nCounters, tuReleasedUs) == 0x3894 && offsetof(Ts3nCounters, tuTrip) == 0x38A0 &&
                  sizeof(Ts3nCounters) == 6066 * 4, "counters v89 layout after v87 (docs/registry.md)");
    assert(k.version == 912);
    // the call sites as InstallTailUnlock patches them (the bytes themselves: steerro-test on the image)
    assert(kTuSites[0].site == 0x0064E4ED && kTuSites[0].target == 0x0064C7C0 && kTuSites[3].site == 0x0064E533 &&
           kTuSites[3].target == 0x00641DF0 && kTuSites[1].hook == (void*)TuS17 && kTuSites[2].hook == (void*)TuS18);
    g_p5bW = (u8*)VirtualAlloc(NULL, 0x1000, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    void* lock = g_p5bW + 0x918;
    InitializeCriticalSection((CRITICAL_SECTION*)lock);
    const LockAcquireFn savedAcq = g_lockOrig;
    const LockReleaseFn savedRel = g_rgRelOrig;
    void* const savedRgLock = g_rgLock;
    const AsyncSlot savedAsync = g_async;
    const DWORD* savedSim = g_rtSimTidSlot;
    const DWORD savedWorker = g_workerTid;
    const HANDLE savedDone = g_asyncDone, savedInv = g_invReady;
    const TuFn2 s16 = g_tuP16;
    const TuFn3 s17 = g_tuP17;
    const TuFn1 s18 = g_tuP18, s19 = g_tuP19;
    for (int i = 0; i < kJobs; i++) assert(g_jobs[i].state != J_QUEUED && g_jobs[i].state != J_RUNNING);
    static DWORD simTid;
    simTid = GetCurrentThreadId();
    g_rtSimTidSlot = &simTid;
    g_lockOrig = P5bAcq, g_rgRelOrig = P5bRel;
    g_rgLock = lock;
    g_async.owner = simTid, g_async.lock = NULL, g_async.state = A_IDLE;
    g_asyncDone = CreateEventW(NULL, FALSE, FALSE, NULL);
    g_invReady = CreateEventW(NULL, FALSE, FALSE, NULL);
    g_tuP16 = TuF16, g_tuP17 = TuF17, g_tuP18 = TuF18, g_tuP19 = TuF19;
    g_tuOn = 1, k.tuReady = 1, k.tuMode = 1;
    const LONG r0 = k.tuRefused[0], r1 = k.tuRefused[1], r2 = k.tuRefused[2], r3 = k.tuRefused[3], wins0 = k.tuWins;
    const LONG trip0 = k.tuTrip[0], trip1 = k.tuTrip[1] + k.tuTrip[2], rel0 = k.tuReleasedUs, wd0 = k.p5bWatchdog;

    // refusals on this thread: not the worker (no job, then a job on the "sim"), a P5b window already open
    g_workerTid = simTid + 7;
    TuOpen();
    assert(!t_tuOpen && k.tuRefused[1] == r1 + 1);
    g_p5bWin = 1;
    TuOpen();
    assert(!t_tuOpen && k.tuRefused[3] == r3 + 1);
    g_p5bWin = 0;
    k.tuMode = 0;
    TuOpen();
    assert(!t_tuOpen && k.tuRefused[0] == r0 + 1);
    TuTrip(1);  // outside a window: nothing
    assert(k.tuTrip[1] + k.tuTrip[2] == trip1);

    // threaded: 40 jobs kept (switch off), 260 lent; every 13th lent job fails at a pass, every 20th takes +0x918 in pass 17
    g_tuGo = CreateEventW(NULL, FALSE, FALSE, NULL);
    g_tuDone = CreateEventW(NULL, FALSE, FALSE, NULL);
    g_tuStop = 0;
    DWORD wtid = 0;
    HANDLE work = CreateThread(NULL, 0, TuWorker, NULL, 0, &wtid);
    g_workerTid = wtid;
    LONG steerIn[2] = {}, steerAll[2] = {}, triesOut = 0, fails = 0, takes = 0, lentJobs = 0;
    g_rfSeed = 4242;
    static const DWORD kTry = 0, kInf = 0xFFFFFFFF;
    for (int i = 0; i < 300; i++) {
        const int ph = i < 40 ? 0 : 1;
        k.tuMode = ph;
        g_tuFail = ph && i % 13 == 0 ? 16 + (i / 13) % 3 : 0;
        g_tuTake = ph && i % 20 == 0;
        fails += g_tuFail != 0, takes += g_tuTake && g_tuFail != 16, lentJobs += ph;
        DWORD want[16];
        for (int j = 0; j < 16; j++) g_tuPath[j] = 0x1000 * i + j;
        const bool wantR = TuTail(false);  // the reference: the passes called directly, no job
        memcpy(want, g_tuPath, sizeof(want));
        for (int j = 0; j < 16; j++) g_tuPath[j] = 0x1000 * i + j;
        SetEvent(g_tuGo);
        const ULONGLONG t0 = GetTickCount64();
        while (WaitForSingleObject(g_tuDone, 0) != WAIT_OBJECT_0) {
            if (GetTickCount64() - t0 > 5000) {
                printf("TailUnlock threaded HANG at job %d: async %ld, window %ld, waiters %ld, lock owner %ld (sim %lu, worker %lu)\n",
                       i, g_async.state, g_p5bWin, g_p5bIn, P5bOwner(lock), simTid, wtid);
                assert(false);
            }
            if (RfRnd(4)) {  // a steering call (rtsites.cpp RT_STEER frame)
                const bool inBody = g_tuBody != 0;
                t_steerRet = 0x0085FE10;
                const LONG r = LockAcquireHook(lock, NULL, &kInf);
                t_steerRet = 0;
                if (r <= 0) InterlockedIncrement(&g_tuBad);
                if (inBody) steerAll[ph]++;
                if (g_tuBody) steerIn[ph]++;  // granted while the job body still runs: only inside a window
                P5bSpin(20);
                LockReleaseHook(lock, NULL);
            } else {  // a plain try (a gate or writer): the worker's hold, lent out or not
                const LONG r = LockAcquireHook(lock, NULL, &kTry);
                if (r > 0) {
                    if (g_tuBody) InterlockedIncrement(&g_tuBad);
                    LockReleaseHook(lock, NULL);
                } else if (g_p5bWin) triesOut++;
            }
        }
        if (g_tuR != wantR || memcmp(g_tuPath, want, sizeof(want))) {
            printf("TailUnlock: job %d answer differs (%d vs %d)\n", i, g_tuR, wantR);
            InterlockedIncrement(&g_tuBad);
        }
    }
    // CE-4: an exception inside the window, caught in Route_PlanLocked: no release of a lock the worker does not own
    k.tuMode = 1, g_tuFail = 0, g_tuTake = false, g_tuThrow = true, g_tuThrew = 0;
    LONG ce4Jobs = 0;
    for (; ce4Jobs < 5 && !g_tuThrew; ce4Jobs++) {  // a window refused for a waiter does not throw: again
        SetEvent(g_tuGo);
        assert(WaitForSingleObject(g_tuDone, 5000) == WAIT_OBJECT_0);
    }
    g_tuThrow = false;
    if (!g_tuThrew || g_tuBad) printf("TailUnlock CE-4 FAIL: threw in a window %ld, bad %ld\n", g_tuThrew, g_tuBad);
    assert(g_tuThrew == 1 && g_tuBad == 0 && !g_p5bWin && TryEnterCriticalSection((CRITICAL_SECTION*)lock));
    LeaveCriticalSection((CRITICAL_SECTION*)lock);
    g_tuStop = 1;
    SetEvent(g_tuGo);
    WaitForSingleObject(work, INFINITE);
    CloseHandle(work);
    const LONG wins = k.tuWins - wins0 - ce4Jobs, refusedWaiter = k.tuRefused[2] - r2;
    if (g_tuBad || wins + refusedWaiter != lentJobs || k.tuRefused[0] - r0 != 41 || steerIn[0] || !steerIn[1])
        printf("TailUnlock FAIL: bad %ld, windows %ld + refused (waiter) %ld of %ld lent jobs, refused (switch) %ld, steering "
               "granted in a body: kept %ld, lent %ld\n", g_tuBad, wins, refusedWaiter, lentJobs, k.tuRefused[0] - r0, steerIn[0], steerIn[1]);
    assert(g_tuBad == 0 && !g_p5bWin && g_p5bIn == 0 && !t_tuOpen && k.p5bWatchdog == wd0);
    assert(wins + refusedWaiter == lentJobs && k.tuRefused[0] - r0 == 41);  // 40 kept jobs + the direct refusal above
    assert(steerIn[0] == 0);  // steering got the lock inside a job body only when lent
    TIMING_ASSERT("TailUnlock steering granted in a lent body", steerIn[1] > 0);
    assert(k.tuTrip[0] - trip0 + refusedWaiter >= takes && k.tuTrip[1] + k.tuTrip[2] == trip1);
    assert(k.tuReleasedUs > rel0);
    TIMING_ASSERT("TailUnlock retake < 1 s", k.tuRetakeMaxUs < 1000000);
    printf("TailUnlock ([RouteAsync] TailUnlock=1): counters v89; refusals switch / not the worker / P5b open; threaded (sim + "
           "worker, real +0x918, 2 levels): %ld kept and %ld lent jobs (%ld windows, %ld refused for a waiter), %ld failing at a pass "
           "and %ld taking +0x918 in pass 17 (tripwires %ld, retaken first), answers equal, steering acquires granted while a job "
           "body ran: kept %ld, lent %ld (%ld started inside one), %ld tries held out, retake max %ld us, no hang\n",
           40L, lentJobs, wins, refusedWaiter, fails, takes, k.tuTrip[0] - trip0, steerIn[0], steerIn[1], steerAll[1], triesOut,
           k.tuRetakeMaxUs);

    CloseHandle(g_tuGo), CloseHandle(g_tuDone);
    CloseHandle(g_asyncDone), CloseHandle(g_invReady);
    g_tuOn = 0, k.tuReady = 0, k.tuMode = 0;
    g_tuP16 = s16, g_tuP17 = s17, g_tuP18 = s18, g_tuP19 = s19;
    DeleteCriticalSection((CRITICAL_SECTION*)lock);
    VirtualFree(g_p5bW, 0, MEM_RELEASE);
    g_p5bW = NULL;
    k.rgOwnerTid = 0;
    g_async = savedAsync;
    g_rgLock = savedRgLock;
    g_lockOrig = savedAcq, g_rgRelOrig = savedRel;
    g_rtSimTidSlot = savedSim;
    g_workerTid = savedWorker;
    g_asyncDone = savedDone, g_invReady = savedInv;
}
