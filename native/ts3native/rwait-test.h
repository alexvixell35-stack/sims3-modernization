// docs/203 §6 [RouteAsync] WorkerWait: the worker's job-start wait (RwStartWait) against droppers on other threads.
// Included by selftest.cpp; run after TestJobYield (the real worker is idle then: no queued job, so it never reads the
// blockers this test sets).
static volatile LONG g_rwtRound, g_rwtStop;
static volatile LONG g_rwtHoldUs[2];

static void RwtSpinUs(LONG us) {
    LARGE_INTEGER t0;
    QueryPerformanceCounter(&t0);
    while (UsSince(t0) < us) YieldProcessor();
}
// producer p (0, 1): each round raises g_rgOffSimOpen before the consumer starts waiting, holds it g_rwtHoldUs[p], drops it
static DWORD WINAPI RwtProducer(LPVOID arg) {
    const int p = (int)(uintptr_t)arg;
    for (LONG seen = 0;;) {
        while (g_rwtRound == seen && !g_rwtStop) YieldProcessor();
        if (g_rwtStop) return 0;
        seen = g_rwtRound;
        RwtSpinUs(g_rwtHoldUs[p]);
        RgOffSimDrop();
    }
}

static void TestRouteWorkerWait() {
    static_assert(offsetof(Ts3nCounters, caOn) == 0x5480 && offsetof(Ts3nCounters, tpNoSlot) == 0x54AC &&
                      offsetof(Ts3nCounters, rwMode) == 0x54B0 && offsetof(Ts3nCounters, rwMaxBlockUs) == 0x54D4 &&
                      offsetof(Ts3nCounters, rwWorkerCpuMs) == 0x54D8 && offsetof(Ts3nCounters, rwSpare) == 0x54DC && sizeof(Ts3nCounters) == 6323 * 4,
                  "counters v142 layout after v141 (docs/registry.md)");
    Ts3nCounters& k = ts3native_counters;
    assert(k.version == 912 && g_rgOffSimOpen == 0 && g_g0Win == 0);
    if (!g_rwWakeEv) g_rwWakeEv = CreateEventW(NULL, FALSE, FALSE, NULL);
    memset((void*)&k.rwMode, 0, sizeof(Ts3nCounters) - offsetof(Ts3nCounters, rwMode));

    // 1. no blocker: returns at once, nothing counted
    G0wHold h0 = {};
    RwStartWait(h0);
    assert(k.rwBlocked == 0 && k.rwSleep0 == 0);

    // 2. mode 0 (the old loop): Sleep(0) until the dropper, no event wait
    HANDLE p0 = CreateThread(NULL, 0, RwtProducer, (LPVOID)0, 0, NULL);
    HANDLE p1 = CreateThread(NULL, 0, RwtProducer, (LPVOID)1, 0, NULL);
    assert(p0 && p1);
    g_rwtHoldUs[0] = 2000, g_rwtHoldUs[1] = 0;
    InterlockedExchangeAdd(&g_rgOffSimOpen, 2);
    InterlockedIncrement(&g_rwtRound);
    G0wHold hm = {};
    RwStartWait(hm);
    assert(g_rgOffSimOpen == 0 && k.rwBlocked == 1 && k.rwSleep0 > 0 && k.rwWaits == 0 && k.rwBlockUs >= 1500);

    // 3. mode 1 stress, no lost wake-up: the safety timeout is 2 s here, so a missed signal shows as a timeout. Two droppers
    // per round; hold times mix spin exits (both < 50 us), event waits and drops racing the park (around the 50 us edge)
    k.rwMode = 1;
    g_rwTimeoutMs = 2000;
    const LONG spins0 = k.rwSpinExits, waits0 = k.rwWaits, blocked0 = k.rwBlocked;
    const int kRounds = 4000;
    unsigned seed = 12345;
    LARGE_INTEGER t0;
    QueryPerformanceCounter(&t0);
    for (int r = 0; r < kRounds; r++) {
        for (int p = 0; p < 2; p++) {
            seed = seed * 1103515245u + 12345u;
            const unsigned v = (seed >> 16) % 100;
            g_rwtHoldUs[p] = v < 40 ? (LONG)(v % 20) : v < 80 ? 40 + (LONG)(v % 30) : 100 + (LONG)(v * 3);
        }
        InterlockedExchangeAdd(&g_rgOffSimOpen, 2);
        InterlockedIncrement(&g_rwtRound);
        G0wHold h = {};
        RwStartWait(h);
        assert(g_rgOffSimOpen == 0);
    }
    const LONG stressUs = UsSince(t0);
    assert(k.rwTimeouts == 0);  // every park ended by a signal
    assert(k.rwBlocked - blocked0 <= kRounds && k.rwSpinExits > spins0 && k.rwWaits > waits0 && k.rwWakes >= k.rwWaits);
    assert(k.rwSignals >= k.rwWakes && k.rwMaxBlockUs < 1000000);
    const LONG blk = k.rwBlocked - blocked0, sx = k.rwSpinExits - spins0, wt = k.rwWaits - waits0, wk = k.rwWakes,
               sg = k.rwSignals, s0 = k.rwSleep0, mx = k.rwMaxBlockUs;
    InterlockedExchange(&g_rwtStop, 1);
    assert(WaitForSingleObject(p0, 5000) == WAIT_OBJECT_0 && WaitForSingleObject(p1, 5000) == WAIT_OBJECT_0);
    CloseHandle(p0), CloseHandle(p1);

    // 4. the H2 window as the blocker: a parked worker wakes on the window clear (G0wDrop / pass end / HxClose call RwWake)
    {
        g_rwtStop = 0;
        InterlockedExchange(&g_g0Win, 1);
        struct W { static DWORD WINAPI Run(LPVOID) { G0wHold h = {}; RwStartWait(h); return 0; } };
        const LONG waits1 = k.rwWaits, held0 = k.g0wJobsHeld;
        HANDLE w = CreateThread(NULL, 0, W::Run, NULL, 0, NULL);
        assert(w);
        while (!g_rwParked) Sleep(0);
        Sleep(5);
        InterlockedExchange(&g_g0Win, 0), RwWake();
        assert(WaitForSingleObject(w, 1000) == WAIT_OBJECT_0);
        CloseHandle(w);
        assert(k.rwWaits > waits1 && k.g0wJobsHeld == held0 + 1 && k.rwTimeouts == 0);
    }

    // 5. safety net and exit: a dropper that never signals (plain decrement) still lets the worker go at the 1 ms timeout,
    // so no blocker path can hang the worker (the worker is not joined at detach; process exit ends it as before)
    g_rwTimeoutMs = 1;
    {
        InterlockedIncrement(&g_rgOffSimOpen);
        struct W { static DWORD WINAPI Run(LPVOID) { G0wHold h = {}; RwStartWait(h); return 0; } };
        HANDLE w = CreateThread(NULL, 0, W::Run, NULL, 0, NULL);
        assert(w);
        Sleep(20);
        InterlockedDecrement(&g_rgOffSimOpen);  // no RwWake
        assert(WaitForSingleObject(w, 1000) == WAIT_OBJECT_0);
        CloseHandle(w);
        assert(k.rwTimeouts > 0);
    }
    assert(g_rgOffSimOpen == 0 && g_g0Win == 0 && !g_rwParked);
    memset((void*)&k.rwMode, 0, sizeof(Ts3nCounters) - offsetof(Ts3nCounters, rwMode));
    printf("route worker wait ([RouteAsync] WorkerWait, v142): %d rounds in %ld ms, 0 timeouts at 2 s: blocked %ld, spin exits %ld, "
           "waits %ld, wakes %ld, signals %ld, Sleep(0) %ld, max %ld us; H2 window wake, 1 ms safety net ok\n",
           kRounds, stressUs / 1000, blk, sx, wt, wk, sg, s0, mx);
}
