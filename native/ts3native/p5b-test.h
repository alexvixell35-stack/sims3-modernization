// P5b ([RouteSnap] ReleaseLock=1, p5b.cpp; docs/94 §22.9): selftest. Included by selftest.cpp after TestP5 (uses its fakes:
// P4AddPath, P4FakeListFree, kP4Fake, P5AnsStr). Threaded: the test thread is the sim (the jobs' owner), a second thread the
// route worker running each ReplanFromPosition job as ServeAsync does (its +0x918 hold for the whole job, A_RUNNING, the job's
// Route_PlanLocked through PlanLockedHook -> P4LivePlan -> P5Plan, the IC's tail after it), and +0x918 is a real
// CRITICAL_SECTION in a fake routing world with the game's own count at +0x20, behind the game's Lock_Acquire / Lock_Release
// conventions. While each job runs, the sim does what it does in the game during a job:
//   - steering calls (t_steerRet set, as rtsites.cpp's RT_STEER frame sets it): +0x918 through LockAcquireHook, a recursive
//     acquire inside, then writes the job would race with (a plain +1 / -1 counter, a cache freed and allocated again);
//   - gate-like waits and tries (no steering frame) through LockAcquireHook;
//   - gate waits as RgEnter makes them (t_waitKey, t_p5bSite, t_p5bSlot on a fake stack frame; 0.34.2): gate-12 reads (F-B with
//     its second frame, F-C, F-G), gate-12 writer families (F-A / F-D second frame, GetIfPresent), gate-0 hits and misses in
//     a fake lot table (LtFind), and writer acquires (no frame, or the goal step's gate) that write the live word;
//   - a destroy of the object the job's IC frame holds (a page the worker reads after Route_PlanLocked): RtWaitJob, then
//     VirtualFree (a use after free faults the selftest).
// Jobs 0-39 keep the hold, 40-199 lend to steering only, 200-359 also to the read-only gate leaves (p5bGates).
// Every 8th lent job's clone run also takes the live lock (the livelock tripwire: retaken, the game's plan answers).
static const DWORD kP5bInf = 0xFFFFFFFF, kP5bTry = 0;
static u8* g_p5bW;                         // fake routing world: +0x918 CRITICAL_SECTION, +0x938 the game's count
static volatile LONG g_p5bBody;            // the worker is inside a job body (between its acquire and its release)
static volatile LONG g_p5bTStop, g_p5bBad, g_p5bAsks, g_p5bLivePlans;
static HANDLE g_p5bGo;
static LONG g_p5bSpinUs = 3000;            // clone-run work before and after its door ask
static int* g_p5bArgs[4];
static int g_p5bVariant;                   // 1: the clone run takes the live lock (livelock tripwire)
static DWORD* volatile g_p5bFrame;         // the object the IC frame holds (one page; [0] = canary)
static volatile LONG g_p5bLive;            // a live routing word the sim's steering writes (+1 ... -1 inside its hold)
static int* volatile g_p5bPoly;            // a cache the steering rebuilds (old one freed)
static LONGLONG g_p5bBodyUs;
static u8 g_p5bR;
static volatile LONG g_p5bTailW;           // P5c test: g_spWrites at the IC's tail (the live world the answer must match)
static int g_p5bO2;

static LONG __fastcall P5bAcq(void* lock, void*, const DWORD* d) {  // Lock_Acquire 0x4E16F0: the count, or -2
    CRITICAL_SECTION* cs = (CRITICAL_SECTION*)lock;
    if (*d == 0xFFFFFFFF) EnterCriticalSection(cs);
    else
        while (!TryEnterCriticalSection(cs)) {
            if (*d == 0 || (LONG)(GetTickCount() - *d) >= 0) return -2;
            Sleep(0);
        }
    return ++*(LONG*)((u8*)lock + 0x20);
}
static LONG __fastcall P5bRel(void* lock, void*) {  // Lock_Release 0x4E17B0: count down, then leave
    const LONG n = --*(LONG*)((u8*)lock + 0x20);
    LeaveCriticalSection((CRITICAL_SECTION*)lock);
    return n;
}
static void P5bSpin(LONG us) {
    LARGE_INTEGER t0;
    QueryPerformanceCounter(&t0);
    while (UsSince(t0) < us) YieldProcessor();
}
static bool P5bMine() { return P5bOwner(g_p5bW + 0x918) == (LONG)GetCurrentThreadId(); }

// a gate's +0x918 wait as RgEnter makes it: slot[-2] = ecx (lot planner + 0x48 for gate 0), slot[1] = the key pointer,
// slot[7] = PortalsForObject's own return (P1b's second frame)
static u8* g_p5bLp;  // fake lot planner P: one bucket, one lot node
static LONG P5bGateWait(int site, DWORD key, DWORD second, const DWORD* lotKey) {
    DWORD fs[12] = {};
    DWORD* slot = fs + 2;
    slot[-2] = (DWORD)(uintptr_t)(g_p5bLp + 0x48), slot[0] = key, slot[1] = (DWORD)(uintptr_t)lotKey, slot[7] = second;
    t_waitKey = key, t_p5bSite = site + 1, t_p5bSlot = slot;
    const LONG r = LockAcquireHook(g_p5bW + 0x918, NULL, &kP5bInf);
    t_waitKey = 0, t_p5bSite = 0;
    return r;
}

static DWORD __cdecl P5bFakeDoor(DWORD a1, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD a9) {
    if (!P5bMine() || t_snapRun || g_p5bWin) InterlockedIncrement(&g_p5bBad);  // the ask runs under the job's hold, disarmed
    InterlockedIncrement(&g_p5bAsks);
    return a1 * 10 + (a9 & 0xFF);
}
static u8 __fastcall P5bFakePlan(void* world, void*, int* outList, int*, int*, DWORD agent, DWORD, u8* opts, int* out1, int* out2,
                                 DWORD, DWORD) {
    assert(agent == 0xA6E && *(const DWORD*)(opts + 0x78) == 0x7777);
    if (!t_snapRun) {  // the game's own plan (a fallback): on the worker, under the job's hold
        if (!P5bMine() || g_p5bWin || world != g_p5bW) InterlockedIncrement(&g_p5bBad);
        InterlockedIncrement(&g_p5bLivePlans);
    } else {
        assert(world == g_p4CloneWorld);
        if (g_p5bWin && P5bMine()) InterlockedIncrement(&g_p5bBad);  // lent out: the clone run runs without it
        P5bSpin(g_p5bSpinUs);
        if (g_p5bVariant == 1) {  // clone-run code reaching the live lock: tripwire, retaken first, then nested
            void* lock = g_p5bW + 0x918;
            LockAcquireHook(lock, NULL, &kP5bInf);
            if (!P5bMine() || g_p5bWin) InterlockedIncrement(&g_p5bBad);
            LockReleaseHook(lock, NULL);
        }
        const DWORD door = ((P4DoorFn)P4DoorStub)(3, 0, 0, 0, 0, 0, 0, 0, 2);
        assert(door == 32 && t_snapRun);
        P5bSpin(g_p5bSpinUs);
    }
    P4AddPath(outList, 2, 3, -100.0f);
    *out1 = 0;
    *out2 = 1;
    return 1;
}

static DWORD WINAPI P5bWorker(LPVOID) {
    for (;;) {
        WaitForSingleObject(g_p5bGo, INFINITE);
        if (g_p5bTStop) return 0;
        AsyncJob& j = g_jobs[0];
        InterlockedExchange(&j.state, J_RUNNING);
        void* lock = g_p5bW + 0x918;
        for (;;) {  // ServeAsync: no start while a guarded sim call is open (Dekker on g_rgOffSimOpen), then the job's hold
            while (g_rgOffSimOpen) Sleep(0);
            LockAcquireHook(lock, NULL, &kP5bInf);
            g_async.handle = j.handle;
            g_async.lock = lock;
            InterlockedExchange(&g_async.state, A_RUNNING);
            if (!g_rgOffSimOpen) break;
            InterlockedExchange(&g_async.state, A_IDLE);
            LockReleaseHook(lock, NULL);
            SetEvent(g_asyncDone);
        }
        InterlockedExchange(&g_p5bBody, 1);
        LARGE_INTEGER t0;
        QueryPerformanceCounter(&t0);
        DWORD* fo = g_p5bFrame;  // the IC frame holds the object across Route_PlanLocked
        P4LiveBegin(&j);
        int* list = g_p5bArgs[0];
        list[0] = list[1] = (int)(uintptr_t)list;
        int o1 = -1;
        g_p5bO2 = -1;
        g_p5bR = PlanLockedHook(g_p5bW, NULL, list, g_p5bArgs[1], g_p5bArgs[2], 0xA6E, 1, (u8*)g_p5bArgs[3], &o1, &g_p5bO2, 5, 0);
        P4LiveEnd(&j);
        // the IC's tail, under the job's hold again: the frame's object alive (a freed page faults here), no steering hold open
        if (fo[0] != 0x0B1EC7 || g_p5bLive != 0 || !P5bMine() || g_p5bWin) InterlockedIncrement(&g_p5bBad);
        g_p5bTailW = g_spWrites;
        g_p5bBodyUs += UsSince(t0);
        InterlockedExchange(&g_p5bBody, 0);
        LockReleaseHook(lock, NULL);
        InterlockedExchange(&g_async.state, A_IDLE);
        InterlockedExchange(&j.state, J_DONE);
        SetEvent(g_asyncDone);
    }
}

static void TestP5cIn(void* lock, const DWORD* hitKey, const DWORD* missKey, int* list);  // p5c-test.h
static void TestP5Fault(void* lock, int* list);  // p5fault-test.h

static void TestP5b() {
    Ts3nCounters& k = ts3native_counters;
    static_assert(offsetof(Ts3nCounters, p5bReady) == 0x3028 && offsetof(Ts3nCounters, p5bRelease) == 0x302C &&
                  offsetof(Ts3nCounters, p5bRefused) == 0x3034 && offsetof(Ts3nCounters, p5bWinUs) == 0x3044 &&
                  offsetof(Ts3nCounters, p5bSimAcq) == 0x3058 && offsetof(Ts3nCounters, p5bRecMax) == 0x3064 &&
                  offsetof(Ts3nCounters, p5bGates) == 0x3068 && offsetof(Ts3nCounters, p5bGateAcq) == 0x306C &&
                  offsetof(Ts3nCounters, p5bGateMiss) == 0x3074 && sizeof(Ts3nCounters) == 5817 * 4,
                  "counters v62 + v63 + v65 + v66 layout (docs/registry.md)");
    assert(k.version == 159);
    // the fake world and its lock (the game's lock object: CRITICAL_SECTION at +0, its own count at +0x20)
    g_p5bW = (u8*)VirtualAlloc(NULL, 0x1000, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    void* lock = g_p5bW + 0x918;
    InitializeCriticalSection((CRITICAL_SECTION*)lock);
    const LockAcquireFn savedAcq = g_lockOrig;
    const LockReleaseFn savedRel = g_rgRelOrig;
    void* const savedRgLock = g_rgLock;
    const AsyncSlot savedAsync = g_async;
    const DWORD* savedSim = g_rtSimTidSlot;
    const PlanLockedFn planSave = g_planOrig;
    CacheEntry* tab = g_tab;
    const LONG workerMode = k.workerMode, budgetMode = k.budgetMode;
    void* const doorSave = g_p4DoorJmp;
    const DWORD guardsSave[4] = {g_p4Guards[0], g_p4Guards[1], g_p4Guards[2], g_p4Guards[3]};
    static DWORD simTid;
    simTid = GetCurrentThreadId();
    g_rtSimTidSlot = &simTid;
    g_lockOrig = P5bAcq, g_rgRelOrig = P5bRel;
    g_rgLock = lock;
    g_async.owner = simTid, g_async.lock = NULL, g_async.state = A_IDLE;
    g_tab = NULL;
    k.workerMode = 0, k.budgetMode = 0;
    g_planOrig = P5bFakePlan;
    g_p4ListFree = P4FakeListFree;
    g_p4DoorJmp = (void*)P5bFakeDoor;
    void (*poolIn)() = g_p4PoolsEnterJob;
    void (*poolOut)() = g_p4PoolsLeaveJob;
    g_p4PoolsEnterJob = g_p4PoolsLeaveJob = NULL;
    static u8 guard12 = 1, guard27 = 1;
    static DWORD guard14[2] = {0, 3};
    g_p4Guards[0] = (DWORD)(uintptr_t)&guard12, g_p4Guards[1] = (DWORD)(uintptr_t)&guard14[0];
    g_p4Guards[2] = (DWORD)(uintptr_t)&guard14[1], g_p4Guards[3] = (DWORD)(uintptr_t)&guard27;
    k.snP4Ready = 1, k.snP4Mode = 2;
    g_p4On = 1, g_p4Kinds = 15;
    P4SetCloneProvider(&kP4Fake);
    g_p5On = 1, k.m2Take = 1, k.m2Ready = 1, k.m2Off = 0, g_p5Verify = 0, g_p5NSites = 0;
    g_p5GuardLock = lock;  // L2 already read for this "world"
    static float sp[16] = {1.5f, 2.0f, 3.0f}, gp[32] = {0};
    gp[16] = 3.5f, gp[17] = 4.0f, gp[18] = 5.0f;
    int s[4] = {(int)(uintptr_t)sp, (int)(uintptr_t)(sp + 16), (int)(uintptr_t)(sp + 16), 0};
    int g[4] = {(int)(uintptr_t)gp, (int)(uintptr_t)(gp + 32), (int)(uintptr_t)(gp + 32), 0};
    static unsigned __int64 ign[2] = {0x123, 0x456}, idset[3] = {0x11, 0x22, 0x33};
    static u8 opts[0x100];
    *(DWORD*)(opts + 0x38) = (DWORD)(uintptr_t)ign, *(DWORD*)(opts + 0x3C) = (DWORD)(uintptr_t)(ign + 2);
    *(DWORD*)(opts + 0x08) = (DWORD)(uintptr_t)idset, *(DWORD*)(opts + 0x0C) = (DWORD)(uintptr_t)(idset + 3);
    g_p4IdSet = idset;
    *(DWORD*)(opts + 0x78) = 0x7777;
    static int list[4] = {0, 0, 0x5A5A, 0x6B6B};
    g_p5bArgs[0] = list, g_p5bArgs[1] = s, g_p5bArgs[2] = g, g_p5bArgs[3] = (int*)opts;
    // the gates' fake lot table (LtFind: P+0x4C buckets, P+0x50 count, chain +0x190, key again at +0x10) and active lot
    static u8 lp[0x60], node[0x200], wm[0x100], actLot[0x60];
    static DWORD bk[2], hitKey[2] = {0x1234, 0x5678}, missKey[2] = {0x1235, 0x5678};
    static u8* wmSlot;
    *(DWORD*)node = *(DWORD*)(node + 0x10) = hitKey[0], *(DWORD*)(node + 4) = *(DWORD*)(node + 0x14) = hitKey[1];
    bk[0] = (DWORD)(uintptr_t)node, bk[1] = 0xFFFF0000;
    *(DWORD**)(lp + 0x4C) = bk, *(DWORD*)(lp + 0x50) = 1;
    *(DWORD*)(actLot + 0x48) = *(DWORD*)(actLot + 0x4C) = 0x9999;
    *(u8**)(wm + 0xB0) = actLot;
    wmSlot = wm;
    u8* const* savedWm = g_srWmSlot;
    g_srWmSlot = &wmSlot;
    g_p5bLp = lp;
    // gate classes (P5bGate): reads admitted only with the switch on; writer families and other sites never
    {
        DWORD fs[12] = {};
        t_p5bSlot = fs + 2;
        const DWORD cases[][4] = {{13, 0x0062F782, 0x0076F15D, P5B_G12}, {13, 0x0062F782, 0x0080CF36, P5B_NO},
                                  {13, 0x0062FB74, 0, P5B_NO},           {13, 0x00631814, 0, P5B_G12},
                                  {13, 0x00631F40, 0, P5B_NO},           {1, 0x0062EF7A, 0, P5B_G0},  // F-G: docs/155
                                  {11, 0x00760BFF, 0, P5B_NO},           {13, 0, 0, P5B_NO}};
        for (const auto& c : cases) {
            t_p5bSite = (int)c[0], t_waitKey = c[1], t_p5bSlot[7] = c[2];
            k.p5bGates = 0;
            assert(P5bGate() == P5B_NO);
            k.p5bGates = 1;
            assert(P5bGate() == (int)c[3]);
        }
        t_p5bSite = 0, t_waitKey = 0, t_p5bSlot = NULL, k.p5bGates = 0;
    }

    // window refusals on this thread (no job body): switch off / verify, not the owner, an acquire waiting, another lock
    g_p5bOn = 1;
    k.p5bRelease = 0;
    const LONG rf0 = k.p5bRefused[0], rf1 = k.p5bRefused[1], rf2 = k.p5bRefused[2], rf3 = k.p5bRefused[3];
    P5bBegin(lock, false);
    assert(t_p5b == P5B_DONE && k.p5bRefused[0] == rf0 + 1);
    k.p5bRelease = 1;
    P5bBegin(lock, true);  // a verify job keeps the hold (G4 compares against the held run)
    assert(k.p5bRefused[0] == rf0 + 2);
    P5bBegin(lock, false);  // no job running
    assert(k.p5bRefused[3] == rf3 + 1);
    g_async.lock = lock, g_async.state = A_RUNNING;
    P5bBegin(lock, false);  // the lock is not this thread's
    assert(k.p5bRefused[1] == rf1 + 1);
    P5bAcq(lock, NULL, &kP5bInf);
    InterlockedIncrement(&g_p5bIn);  // an acquire inside the game's Lock_Acquire: it would get the lock as soon as we let go
    P5bBegin(lock, false);
    assert(k.p5bRefused[2] == rf2 + 1 && !g_p5bWin && P5bMine());
    InterlockedDecrement(&g_p5bIn);
    P5bAcq(lock, NULL, &kP5bInf);  // a recursive hold: both levels lent out, both taken back
    const LONG wins0 = k.p5bWins;
    P5bBegin(lock, false);
    assert(t_p5b == P5B_OPEN && g_p5bWin && !P5bMine() && *(LONG*)(g_p5bW + 0x938) == 0 && k.p5bWins == wins0 + 1 && k.p5bRecMax >= 2);
    assert(P5bLock(lock, NULL, &kP5bTry) == -2);  // a try with no steering frame sees the worker's hold
    {  // gates while lent: a gate-0 hit gets the lock, a miss gets it, finds no entry and gives it back (-2 to a try)
        DWORD fs[12] = {};
        DWORD* slot = fs + 2;
        slot[-2] = (DWORD)(uintptr_t)(lp + 0x48);
        t_p5bSlot = slot, t_p5bSite = 1, t_waitKey = 0x0062EF7A;
        const LONG acq0 = k.p5bGateAcq[1], miss0 = k.p5bGateMiss;
        slot[1] = (DWORD)(uintptr_t)hitKey;
        assert(P5bLock(lock, NULL, &kP5bTry) == -2);  // switch off: as before
        k.p5bGates = 1;
        assert(P5bLock(lock, NULL, &kP5bTry) == 1 && P5bMine() && k.p5bGateAcq[1] == acq0 + 1);
        P5bRel(lock, NULL);
        slot[1] = (DWORD)(uintptr_t)missKey;
        assert(P5bLock(lock, NULL, &kP5bTry) == -2 && !P5bMine() && k.p5bGateMiss == miss0 + 1 && t_p5bSite == 0);
        assert(*(LONG*)(g_p5bW + 0x938) == 0);
        // docs/155 §8: gate-12 F-G (0x631F40) is not admitted: held out like a writer and counted; F-C still gets the lock
        t_p5bSite = 13, t_waitKey = 0x00631F40;
        const LONG fg0 = k.p5bFgHeldOut, ho0 = k.p5bHeldOut, g12a = k.p5bGateAcq[0];
        assert(P5bLock(lock, NULL, &kP5bTry) == -2 && !P5bMine() && k.p5bFgHeldOut == fg0 + 1 && k.p5bHeldOut == ho0 + 1);
        t_waitKey = 0x00631814;
        assert(P5bLock(lock, NULL, &kP5bTry) == 1 && P5bMine() && k.p5bGateAcq[0] == g12a + 1 && k.p5bFgHeldOut == fg0 + 1);
        P5bRel(lock, NULL);
        t_p5bSlot = NULL, t_waitKey = 0, t_p5bSite = 0, k.p5bGates = 0;
    }
    P5bEnd();
    assert(t_p5b == P5B_CLOSED && !g_p5bWin && P5bMine() && *(LONG*)(g_p5bW + 0x938) == 2);
    P5bRel(lock, NULL), P5bRel(lock, NULL);
    g_async.lock = NULL, g_async.state = A_IDLE;

    // threaded: 40 jobs with the lock kept (ReleaseLock off: the baseline), 160 lent to steering, 160 lent to the gates too
    g_p5bGo = CreateEventW(NULL, FALSE, FALSE, NULL);
    g_p5bTStop = 0;
    HANDLE work = CreateThread(NULL, 0, P5bWorker, NULL, 0, NULL);
    static char ref[8192], got[8192];
    ref[0] = 0;
    // sim acquires by class, per phase: started during a job body (all), got the lock inside it (job), their wait (us)
    enum { C_STEER, C_G12R, C_G0H, C_G12W, C_G0M, C_WR, C_N };
    static const char* const kCls[C_N] = {"steering", "gate-12 reads", "gate-0 hits", "gate-12 writer families", "gate-0 misses",
                                          "writers"};
    LONG all[3][C_N] = {}, inJ[3][C_N] = {}, destroysLent = 0, triesHeldOut = 0, jobs[3] = {};
    LONG lent0[3][C_N] = {}, lentGot[3][C_N] = {};  // started while the window was open; of those, granted inside the job
    LONGLONG waitUs[3][C_N] = {}, bodyUs[3] = {}, lentUs[3] = {};
    g_rfSeed = 5150;
    const LONG taken0 = k.m2Taken, live0 = k.m2Fb[P4_AB_LIVELOCK], trip0 = k.p5bRetakeTrip, ask0 = k.p5bRetakeAsk;
    const LONG simAcq0 = k.p5bSimAcq, wd0 = k.p5bWatchdog, g12a0 = k.p5bGateAcq[0], g0a0 = k.p5bGateAcq[1], miss0 = k.p5bGateMiss;
    for (int i = 0; i < 360; i++) {
        const int ph = i < 40 ? 0 : i < 200 ? 1 : 2;
        k.p5bRelease = ph > 0, k.p5bGates = ph == 2;
        g_p5bVariant = ph && i % 8 == 0 ? 1 : 0;
        DWORD* fo = (DWORD*)VirtualAlloc(NULL, 4096, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
        fo[0] = 0x0B1EC7;
        g_p5bFrame = fo;
        AsyncJob& j = g_jobs[0];
        memset(&j, 0, sizeof(j));
        j.kind = 3, j.seq = 5000 + i, j.handle = 0x25000500 + i, j.lock = lock, j.x87 = GetX87(), j.mxcsr = _mm_getcsr();
        P4OnStart(&j);  // P5OnStart: armed to take
        assert(j.p4 == 2);
        const LONGLONG b0 = g_p5bBodyUs;
        const LONG w0 = k.p5bWinUs;
        InterlockedExchange(&j.state, J_QUEUED);
        SetEvent(g_p5bGo);
        bool destroyed = false;
        const ULONGLONG t0 = GetTickCount64();
        while (j.state != J_DONE) {
            if (GetTickCount64() - t0 > 5000) {
                printf("P5b threaded HANG at job %d: job state %ld, async %ld, window %ld, waiters %ld, lock owner %ld (sim %lu)\n", i,
                       j.state, g_async.state, g_p5bWin, g_p5bIn, P5bOwner(lock), simTid);
                assert(false);
            }
            DWORD act = RfRnd(32);
            // 0.35.11: a class not yet started during a body in this phase goes first (under CPU load few sim steps land in
            // bodies, and the rare classes, 1-2 in 32, then had none: all[p][c] > 0 failed)
            static const DWORD kFirst[C_N] = {0, 16, 22, 20, 25, 26};  // one act per class, in enum order
            for (int c = 0; g_p5bBody && c < C_N; c++) if (!all[ph][c]) { act = kFirst[c]; break; }
            if (act >= 14 && act < 16) {  // a try outside a steering frame: the worker's hold, lent out or not
                const bool lent = g_p5bWin != 0;
                const LONG r = LockAcquireHook(lock, NULL, &kP5bTry);
                if (r > 0) {
                    if (g_p5bBody) InterlockedIncrement(&g_p5bBad);
                    LockReleaseHook(lock, NULL);
                } else if (lent) triesHeldOut++;
            } else if (act < 31 || destroyed || !g_p5bBody) {
                // (a destroy only once the job body runs: a destroy before the start keeps the start out in the game, g_rgOffSimOpen)
                const bool started = g_p5bBody != 0, lentAt0 = g_p5bWin != 0;
                LARGE_INTEGER s0;
                QueryPerformanceCounter(&s0);
                int c;
                LONG r;
                if (act < 14) {  // a steering call (rtsites.cpp RT_STEER frame: t_steerRet = its caller)
                    c = C_STEER;
                    t_steerRet = 0x0085FE10;
                    r = LockAcquireHook(lock, NULL, &kP5bInf);
                } else if (act < 20) {  // gate-12 reads: F-B (second frame in the slot position helper), F-C; F-G (docs/155: a
                                        // read leaf with an unlocked writer tail, not admitted) counts with the writer families
                    static const DWORD kKey[3] = {0x0062F782, 0x00631814, 0x00631F40};
                    const int f = (int)(act % 3);
                    c = f == 2 ? C_G12W : C_G12R;
                    r = P5bGateWait(12, kKey[f], f ? 0 : 0x0076F15D, hitKey);
                } else if (act < 22) {  // gate-12 writer families: F-A / F-D through PortalsForObject, GetIfPresent
                    c = C_G12W;
                    r = act & 1 ? P5bGateWait(12, 0x0062F782, 0x0080CF36, hitKey) : P5bGateWait(12, 0x0062FB74, 0, hitKey);
                } else if (act < 26) {  // gate 0 (transform wrapper): hits, every 4th a miss
                    c = act == 25 ? C_G0M : C_G0H;
                    r = P5bGateWait(0, 0x0062EF7A, 0, act == 25 ? missKey : hitKey);
                } else {  // writers: footprint updates (plain acquire, no frame) and the goal step's gate
                    c = C_WR;
                    r = act < 29 ? LockAcquireHook(lock, NULL, &kP5bInf) : P5bGateWait(10, 0x00760BFF, 0, hitKey);
                }
                assert(r > 0 && P5bMine());
                const bool inJob = g_p5bBody != 0;
                // only a lent-out lock can be ours during a job, and only for an admitted class with its switch on
                const bool may = c == C_STEER ? ph > 0 : (c == C_G12R || c == C_G0H) && ph == 2;
                if (inJob && (!g_p5bWin || !may)) InterlockedIncrement(&g_p5bBad);
                if (started) {
                    all[ph][c]++;
                    waitUs[ph][c] += UsSince(s0);
                    if (inJob) inJ[ph][c]++;
                    if (lentAt0) lent0[ph][c]++, lentGot[ph][c] += inJob;
                }
                if (c == C_STEER) {
                    assert(LockAcquireHook(lock, NULL, &kP5bInf) == r + 1);  // nested acquire of the holder
                    int* old = g_p5bPoly;  // a cache rebuilt (old one freed)
                    g_p5bPoly = (int*)malloc(64);
                    free(old);
                }
                if (c == C_G0H) {  // the hit's entry, read under the lock as GetOrInsert's hit path does
                    const u8* e;
                    if (LtFind(lp, hitKey[0], hitKey[1], &e) != LT_OK || e != node + 0x10) InterlockedIncrement(&g_p5bBad);
                }
                if (c == C_STEER || c == C_WR) g_p5bLive++;  // a write the job would race with (+1 ... -1 inside the hold)
                P5bSpin(c == C_STEER || c == C_WR ? 100 : 20);
                if (c == C_STEER || c == C_WR) g_p5bLive--;
                if (c == C_STEER) LockReleaseHook(lock, NULL);
                LockReleaseHook(lock, NULL);
                t_steerRet = 0;
            } else {  // a destroy of the frame's object (closure #4): waits for the job, then frees the page
                if (g_p5bWin) destroysLent++;
                static LONG us;
                RtWaitJob(&us, 0x02000000 | RT_DESTROY);
                if (g_p5bBody) InterlockedIncrement(&g_p5bBad);
                VirtualFree(fo, 0, MEM_RELEASE);
                destroyed = true;
            }
            Sleep(0);
        }
        if (!destroyed) VirtualFree(fo, 0, MEM_RELEASE);
        jobs[ph]++;
        bodyUs[ph] += g_p5bBodyUs - b0;
        lentUs[ph] += k.p5bWinUs - w0;
        // the answer: the clone run's (or, after the livelock tripwire, the game's), equal to the held baseline's
        static P4Ans a;
        assert(g_p5bR == 1 && g_p5bO2 == 1 && P4CaptureAnswerRaw(list, g_p5bR, 0, g_p5bO2, &a));
        P5AnsStr(a, got, sizeof(got));
        if (!ref[0]) strcpy_s(ref, got);
        assert(!strcmp(ref, got));
        P4FakeListFree(list, NULL);
        InterlockedExchange(&j.state, J_FREE);
    }
    g_p5bTStop = 1;
    SetEvent(g_p5bGo);
    WaitForSingleObject(work, 5000);
    CloseHandle(work), CloseHandle(g_p5bGo);
    free(g_p5bPoly);
    g_p5bPoly = NULL;
    k.p5bGates = 0;
    const LONG livelocks = k.m2Fb[P4_AB_LIVELOCK] - live0, taken = k.m2Taken - taken0;
    assert(g_p5bBad == 0 && !g_p5bWin && g_p5bIn == 0 && k.p5bWatchdog == wd0);
    assert(lentUs[0] == 0 && k.p5bSimAcq > simAcq0);
    TIMING_ASSERT("P5b destroys and time lent", destroysLent > 0 && lentUs[1] > 0 && lentUs[2] > 0);
    // admitted classes inside a job only when lent (steering) and with the gate switch (gate reads); the rest never
    assert(inJ[0][C_STEER] == 0);
    TIMING_ASSERT("P5b steering inside lent bodies", inJ[1][C_STEER] > 0 && inJ[2][C_STEER] > 0);
    for (int c = C_G12R; c < C_N; c++) {
        assert(inJ[0][c] == 0 && inJ[1][c] == 0 && (c <= C_G0H || inJ[2][c] == 0));
        TIMING_ASSERT("P5b gate reads inside gated bodies", c > C_G0H || inJ[2][c] > 0);
    }
    for (int p = 1; p < 3; p++)
        for (int c = 0; c < C_N; c++) assert(all[p][c] > 0);
    assert(all[0][C_STEER] > 0);
    assert(k.p5bGateAcq[0] > g12a0 && k.p5bGateAcq[1] > g0a0 && k.p5bGateMiss > miss0);
    // a window refused at its open (a waiter in flight, rare) has no retake to count
    const LONG refusedWaiter = k.p5bRefused[2] - rf2 - 1;
    assert(livelocks == 40 && taken == 360 - livelocks && g_p5bLivePlans == livelocks);
    assert(k.p5bRetakeTrip - trip0 + refusedWaiter >= livelocks && k.p5bRetakeAsk - ask0 + refusedWaiter >= 280 && g_p5bAsks == 360);
    auto mean = [&](int p, int c) { return all[p][c] ? waitUs[p][c] / 1000.0 / all[p][c] : 0.0; };
    auto perJob = [&](int p, int c0, int c1) {
        LONGLONG us = 0;
        for (int c = c0; c < c1; c++) us += waitUs[p][c];
        return us / 1000.0 / jobs[p];
    };
    TIMING_ASSERT("P5b mean waits drop per mode",
                  mean(1, C_STEER) < mean(0, C_STEER) && mean(2, C_G12R) < mean(1, C_G12R) && mean(2, C_G0H) < mean(1, C_G0H));
    // 0.35.11: most admitted waits that start while the window is open are granted inside it. Was: of all waits that start
    // during a body (held parts too), more than half; under CPU load the held parts stretch and that fell to 38 of 76
    if (!(2 * lentGot[1][C_STEER] > lent0[1][C_STEER] && 2 * lentGot[2][C_G12R] > lent0[2][C_G12R]))
        printf("P5b FAIL: steering granted lent %ld of %ld started lent (%ld of %ld in bodies), gate-12 reads %ld of %ld (%ld of %ld)\n",
               lentGot[1][C_STEER], lent0[1][C_STEER], inJ[1][C_STEER], all[1][C_STEER], lentGot[2][C_G12R], lent0[2][C_G12R],
               inJ[2][C_G12R], all[2][C_G12R]);
    TIMING_ASSERT("P5b most lent waits granted in the window",
                  2 * lentGot[1][C_STEER] > lent0[1][C_STEER] && 2 * lentGot[2][C_G12R] > lent0[2][C_G12R]);
    // the admitted classes' own wait per job drops; the total need not (the sim reaches the next writer wait sooner)
    const double sum1 = perJob(1, 0, C_N), sum2 = perJob(2, 0, C_N), adm1 = perJob(1, C_G12R, C_G12W), adm2 = perJob(2, C_G12R, C_G12W);
    TIMING_ASSERT("P5b admitted wait per job drops", adm2 < adm1);
    printf("P5b (ReleaseLock=1, ReleaseGates=1): counters v63; refusals switch / verify / no job / not owner / waiter, recursive "
           "hold lent and taken back, gate classes, a gate-0 hit lent and a miss given back; threaded (sim + worker, real +0x918), "
           "%ld held / %ld steering-lent / %ld gate-lent jobs (lent %.1f%% / %.1f%% of job time), sim acquires started during a "
           "job body, got inside the job / all (mean wait ms, held -> steering -> gates):\n",
           jobs[0], jobs[1], jobs[2], bodyUs[1] ? 100.0 * lentUs[1] / bodyUs[1] : 0.0, bodyUs[2] ? 100.0 * lentUs[2] / bodyUs[2] : 0.0);
    for (int c = 0; c < C_N; c++)
        printf("  %-24s %ld/%ld (%.2f)  %ld/%ld (%.2f)  %ld/%ld (%.2f)\n", kCls[c], inJ[0][c], all[0][c], mean(0, c), inJ[1][c],
               all[1][c], mean(1, c), inJ[2][c], all[2][c], mean(2, c));
    printf("  sim wait per job during job bodies: held %.2f ms, steering-lent %.2f ms (gate reads %.2f ms = %.0f%% of it), gate-lent "
           "%.2f ms (gate reads %.2f ms, -%.0f%%; the rest moves to writer waits); granted while lent: steering %ld, gate-12 %ld, "
           "gate-0 hits %ld, gate-0 misses given back %ld; %ld tries "
           "held out, every destroy (%ld while lent) and writer waited for the job, frame objects intact, answers equal (%ld taken, "
           "%ld livelock tripwires answered by the game's plan), door asks under the hold, no hang\n",
           perJob(0, 0, C_N), sum1, adm1, 100.0 * adm1 / sum1, sum2, adm2, 100.0 * (adm1 - adm2) / adm1, k.p5bSimAcq - simAcq0,
           k.p5bGateAcq[0] - g12a0, k.p5bGateAcq[1] - g0a0, k.p5bGateMiss - miss0, triesHeldOut, destroysLent, taken, livelocks);

    TestP5cIn(lock, hitKey, missKey, list);  // P5c (FreeLock=1) in the same fake world
    TestP5Fault(lock, list);                 // docs/157 fault injection in the same fake world
    k.p5bRelease = 0;
    g_p5bOn = 0;
    DeleteCriticalSection((CRITICAL_SECTION*)lock);
    VirtualFree(g_p5bW, 0, MEM_RELEASE);
    g_p5bW = NULL;
    for (int i = 0; i < 4; i++) g_p4Guards[i] = guardsSave[i];
    P4SetCloneProvider(NULL);
    g_p4PoolsEnterJob = poolIn, g_p4PoolsLeaveJob = poolOut;
    g_p4DoorJmp = doorSave;
    g_planOrig = planSave;
    g_tab = tab;
    k.workerMode = workerMode, k.budgetMode = budgetMode;
    k.snP4Mode = 0, k.snP4Ready = 0;
    g_p4On = 0, g_p5On = 0;
    k.rgOwnerTid = 0;
    g_async = savedAsync;
    g_rgLock = savedRgLock;
    g_lockOrig = savedAcq, g_rgRelOrig = savedRel;
    g_rtSimTidSlot = savedSim;
    g_srWmSlot = savedWm;
    g_p5bLp = NULL;
}
