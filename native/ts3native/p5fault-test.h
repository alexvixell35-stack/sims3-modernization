// docs/157: P5 / P5b / P5c fault injection (TS3N_FAULTINJ; selftest.cpp defines it, release builds have none of it). Included
// by selftest.cpp after p5c-test.h and run from TestP5b in its fake world: +0x918 a real CRITICAL_SECTION with the game's count
// at +0x20, the route worker P5bWorker running each ReplanFromPosition job as ServeAsync does. Each case injects one fault into
// a job whose clone run lends +0x918 (P5b, or P5c with FreeLock) while the sim (this thread) takes the lent lock as steering,
// and asserts the recovery: the job ends (no hang), the window is closed and the worker owns +0x918 at the IC's tail
// (P5bWorker counts g_p5bBad otherwise), the game's own plan answered, and the feature state docs/157 names (off or on).
static LONG g_p5fSteerIn;  // steering acquires granted inside an open window

// one armed job through the worker; holdMs: the first steering call inside the window keeps the lent lock that long
static void P5fJob(void* lock, int* list, int seq, DWORD holdMs = 0) {
    AsyncJob& j = g_jobs[0];
    memset(&j, 0, sizeof(j));
    j.kind = 3, j.seq = seq, j.handle = 0x25F00000 + seq, j.lock = lock, j.x87 = GetX87(), j.mxcsr = _mm_getcsr();
    DWORD* fo = (DWORD*)VirtualAlloc(NULL, 4096, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    fo[0] = 0x0B1EC7;
    g_p5bFrame = fo;
    P4OnStart(&j);
    InterlockedExchange(&g_p5bWaitSteer, holdMs ? 1 : 0);  // #530: the clone run waits for the holder below
    InterlockedExchange(&j.state, J_QUEUED);
    SetEvent(g_p5bGo);
    bool held = false;
    const ULONGLONG t0 = GetTickCount64();
    while (j.state != J_DONE) {
        if (GetTickCount64() - t0 > 8000) {
            printf("P5 fault HANG at job %d: async %ld, window %ld, P5c %ld, waiters %ld, lock owner %ld\n", seq, g_async.state,
                   g_p5bWin, g_p5cJob, g_p5bIn, P5bOwner(lock));
            assert(false);
        }
        if (g_p5bWin && g_p5bBody) {  // a steering call (RT_STEER frame) while the window is open
            t_steerRet = 0x0085FE10;
            const LONG r = LockAcquireHook(lock, NULL, &kP5bInf);
            assert(r > 0 && P5bMine());
            if (g_p5bWin) g_p5fSteerIn++;
            InterlockedExchange(&g_p5bWaitSteer, 0);  // the holder has the lock: the clone run may end
            if (holdMs && !held) held = true, Sleep(holdMs);  // a holder that does not give the lock back for a while
            LockReleaseHook(lock, NULL);
            t_steerRet = 0;
        }
        Sleep(0);
    }
    VirtualFree(fo, 0, MEM_RELEASE);
    assert(g_p5bR == 1 && g_p5bO2 == 1 && list[0] != (int)(uintptr_t)list);  // an answer, the clone's or the game's
    P4FakeListFree(list, NULL);
    InterlockedExchange(&j.state, J_FREE);
    assert(!g_p5bWin && g_p5bIn == 0 && !g_p5cJob && t_p5b == P5B_CLOSED);  // lending off cleanly (this thread never lent)
}

static void P5fOn() {  // P4 + Mode=2 on again after a case turned them off
    Ts3nCounters& k = ts3native_counters;
    g_p4On = 1, k.snP4Ready = 1 | 16, g_p5On = 1, k.m2Off = 0, k.m2Ready |= 1, k.m2Trips = 0;
}

static void TestP5Fault(void* lock, int* list) {
    Ts3nCounters& k = ts3native_counters;
    g_p5bGo = CreateEventW(NULL, FALSE, FALSE, NULL);
    g_p5bTStop = 0;
    HANDLE work = CreateThread(NULL, 0, P5bWorker, NULL, 0, NULL);
    g_planOrig = P5bFakePlan;
    g_p5bVariant = 0;
    const LONG spinSave = g_p5bSpinUs;
    g_p5bSpinUs = 20000;  // a clone run long enough for the sim to get into its window
    g_p5bOn = 1, k.p5bRelease = 1, k.p5bGates = 0;
    g_fiEvery = 1, g_fiN = 0, g_fiTurn = 0;
    const LONG trips0 = k.m2Trips, bad0 = g_p5bBad;
    P5fOn();
    static u8 fkHeap[0x4E8];
    static DWORD fkHeapSlot;
    InitializeCriticalSection(&g_fkEaLock.cs);
    g_fkEaLock.depth = 0;
    *(void**)(fkHeap + 0x4E4) = &g_fkEaLock, fkHeapSlot = (DWORD)(uintptr_t)fkHeap;
    DWORD* const savedEaHeap = g_p4EaHeap;
    g_p4EaHeap = &fkHeapSlot;
    int seq = 7000;

    // F1: access violation in a P5b clone run (window open): retaken, P4 and Mode=2 off, the game's plan answers
    {
        g_fiMask = FI_AV, k.p5cFree = 0, g_p5cOn = 0;
        const LONG live = g_p5bLivePlans, fault = k.snShadowFault, fb = k.m2Fb[P4_AB_FAULT], wins = k.p5bWins;
        P5fJob(lock, list, seq++);
        assert(g_p5bLivePlans == live + 1 && k.snShadowFault == fault + 1 && k.m2Fb[P4_AB_FAULT] == fb + 1 && k.p5bWins == wins + 1);
        assert(!g_p4On && !g_p5On && k.m2Off == 3);
        const LONG plans = k.m2Plans;  // the next job is a plain A10 job
        P5fJob(lock, list, seq++);
        assert(k.m2Plans == plans && g_p5bLivePlans == live + 2);
        P5fOn();
    }
    // F2: the same with the EA heap lock held at the fault (crash H), in a P5c window: the orphaned hold released
    {
        g_fiMask = FI_AV | FI_EAHEAP, k.p5cFree = 1, k.p5cOff = 0, g_p5cOn = 1;
        const LONG live = g_p5bLivePlans, jobs = k.p5cJobs;
        P5fJob(lock, list, seq++);
        assert(g_p5bLivePlans == live + 1 && k.p5cJobs == jobs + 1 && !g_p4On && !g_p5On && k.m2Off == 3);
        assert(!g_fkEaLock.cs.OwningThread && !g_fkEaLock.cs.RecursionCount && g_fkEaLock.depth == 0);
        P5fOn();
    }
    // F3: managed-call tripwires in P5c windows: each retakes the lock at once, the game answers; the third turns Mode=2 off
    {
        g_fiMask = FI_MANAGED;
        const LONG live = g_p5bLivePlans, fb = k.m2Fb[P4_AB_MANAGED], trip = k.p5bRetakeTrip;
        for (int i = 0; i < 3; i++) {
            assert(g_p5On);
            P5fJob(lock, list, seq++);
            assert(g_p5bLivePlans == live + i + 1 && k.m2Fb[P4_AB_MANAGED] == fb + i + 1 && k.p5bRetakeTrip == trip + i + 1);
        }
        assert(!g_p5On && k.m2Off == 2 && g_p4On);  // P4 stays on: a tripwire is not a fault
        P5fOn();
    }
    // F4: the world closes during a lent run (rtClosing): epoch, the game answers, Mode=2 stays on
    {
        g_fiMask = FI_CLOSE;
        const LONG live = g_p5bLivePlans, fb = k.m2Fb[P4_AB_EPOCH];
        P5fJob(lock, list, seq++);
        assert(k.rtClosing == 1 && g_p5bLivePlans == live + 1 && k.m2Fb[P4_AB_EPOCH] == fb + 1 && g_p5On && g_p4On);
        k.rtClosing = 0;
    }
    // F5: a steering holder keeps the lent lock 2.3 s: the retake waits it out (watchdog counted and logged), no hang, and the
    // clone answer is still taken (nothing was wrong with it)
    {
        g_fiMask = 0, k.p5cFree = 0;
        const LONG wd = k.p5bWatchdog, taken = k.m2Taken, live = g_p5bLivePlans;
        P5fJob(lock, list, seq++, 2300);
        assert(k.p5bWatchdog == wd + 1 && k.m2Taken == taken + 1 && g_p5bLivePlans == live && k.p5bRetakeMaxUs >= 2000000);
        assert(k.p5bWdEscalate == 0 && k.p5bRelease == 1);  // under RetakeEscalateMs (4000): no escalation
    }
    // F6 (0.36.35, docs/157 §4.3): the holder keeps it past RetakeEscalateMs (1000 here): escalation once (P5b + P5c off for
    // the session), the retake still waits it out and the answer is taken; the next job opens no window and is answered
    {
        const LONG escSave = g_p5bEscMs;
        g_p5bEscMs = 1000, g_fiMask = 0, k.p5cFree = 0;
        const LONG esc = k.p5bWdEscalate, taken = k.m2Taken, wins = k.p5bWins, ref0 = k.p5bRefused[0];
        P5fJob(lock, list, seq++, 1500);
        assert(k.p5bWdEscalate == esc + 1 && k.p5bRelease == 0 && k.p5cOff == 1 && k.m2Taken == taken + 1 && k.p5bWins == wins + 1);
        P5fJob(lock, list, seq++);
        assert(k.p5bWins == wins + 1 && k.p5bRefused[0] > ref0 && k.m2Taken == taken + 2 && k.p5bWdEscalate == esc + 1);
        g_p5bEscMs = escSave, k.p5bRelease = 1, k.p5cOff = 0;
    }
    // F7 (0.36.35, docs/157 §4.4): a refusal storm. StormRefusals 4 here: four FI_OOM refusals in a row pause Mode=2 (the
    // next job answers with no acquire, p5StormSkips); a taken job resets the run; the third storm turns Mode=2 off (m2Off 4)
    {
        const LONG nSave = g_p5StormN, pSave = g_p5StormPauseMs;
        g_p5StormN = 4, g_p5StormPauseMs = 60000, g_p5StormRun = 0, g_p5StormUntil = 0;
        const LONG trips = k.p5StormTrips, skips = k.p5StormSkips;
        for (int storm = 1; storm <= 3; storm++) {
            g_fiMask = FI_OOM;
            for (int i = 0; i < 3; i++) P5fJob(lock, list, seq++);
            assert(k.p5StormTrips == trips + storm - 1 && g_p5StormRun == 3);
            g_fiMask = 0;
            if (storm == 1) {  // a taken job in the middle ends the run
                const LONG taken = k.m2Taken;
                P5fJob(lock, list, seq++);
                assert(k.m2Taken == taken + 1 && g_p5StormRun == 0);
                g_fiMask = FI_OOM;
                for (int i = 0; i < 3; i++) P5fJob(lock, list, seq++);
                g_fiMask = 0;
            }
            g_fiMask = FI_OOM;
            P5fJob(lock, list, seq++);  // the 4th in a row: the storm
            g_fiMask = 0;
            assert(k.p5StormTrips == trips + storm && g_p5StormUntil != 0 && k.p5StormRunMax >= 4);
            if (storm < 3) {
                const LONG taken = k.m2Taken;
                P5fJob(lock, list, seq++);  // paused: the game's plan, no acquire
                assert(k.p5StormSkips == skips + storm && k.m2Taken == taken && g_p5On);
                g_p5StormUntil = 1;  // the pause ends (tick 1 is long past)
            }
        }
        assert(!g_p5On && k.m2Off == 4);
        g_p5StormN = nSave, g_p5StormPauseMs = pSave, g_p5StormRun = 0, g_p5StormUntil = 0;
        P5fOn();
    }
    assert(g_p5bBad == bad0);

    g_fiMask = 0, g_fiPlan = 0;
    g_p4EaHeap = savedEaHeap;
    DeleteCriticalSection(&g_fkEaLock.cs);
    k.p5cFree = 0, k.p5cOff = 0, g_p5cOn = 0, k.m2Trips = trips0;
    g_p5bSpinUs = spinSave;
    g_p5bTStop = 1;
    SetEvent(g_p5bGo);
    WaitForSingleObject(work, 5000);
    CloseHandle(work), CloseHandle(g_p5bGo);
    printf("P5 fault injection (docs/157, TS3N_FAULTINJ): threaded, real +0x918 lent: AV in a P5b window (P4 + Mode=2 off, the "
           "next job plain A10), AV holding the EA heap lock in a P5c window (released), 3 managed tripwires in P5c windows (each "
           "retaken at once, third = Mode=2 off), world close mid-run (epoch), a steering holder 2.3 s (watchdog, answer taken); "
           "every job answered, window closed, worker owns +0x918 at the tail, no hang (%ld steering grants in windows)\n",
           g_p5fSteerIn);
}
