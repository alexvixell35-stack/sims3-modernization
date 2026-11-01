// TestP6Waits ([P6Waits], p6wait.cpp, docs/189 phase 0) on dummy jobs: no game code runs. Included by selftest.cpp.
static void TestP6Waits() {
    Ts3nCounters& k = ts3native_counters;
    static_assert(offsetof(Ts3nCounters, p6wReady) == 0x4BBC && offsetof(Ts3nCounters, p6wN) == 0x4BC0 &&
                      offsetof(Ts3nCounters, p6wUs) == 0x4CB0 && offsetof(Ts3nCounters, p6wJobN) == 0x4DA0 &&
                      offsetof(Ts3nCounters, p6wSpanUs) == 0x4DC8 && offsetof(Ts3nCounters, p6wGateUs) == 0x4DF0 &&
                      offsetof(Ts3nCounters, p6wLost) == 0x4E70 && offsetof(Ts3nCounters, p6wServeUs) == 0x4E78 &&
                      offsetof(Ts3nCounters, p6wArmSet) == 0x51BC &&
                      sizeof(Ts3nCounters) == 5468 * 4,
                  "counters v123 layout after v122 (docs/registry.md)");
    static_assert(P6O_N * P6W_N == 30 && P6O_NONE == 5, "p6wN / p6wUs are [2 * 6 * 5], p6wJobN [2 * 5]");
    assert(k.version == 145);
    const size_t blk = offsetof(Ts3nCounters, p6wServeUs) + sizeof(k.p6wServeUs) - offsetof(Ts3nCounters, p6wReady);
    static u8 save[0x400];
    memcpy(save, &k.p6wReady, blk);
    const AsyncSlot as = g_async;
    const int outSave = t_p6wOut, armSave = t_p6wArm;
    const LONG planSave = k.p6Plan;
    memset(&k.p6wReady, 0, blk);
    memset(g_p6wDone, 0, sizeof(g_p6wDone));
    memset(g_p6wPend, 0, sizeof(g_p6wPend));
    g_async.owner = GetCurrentThreadId();
    const int A1 = P6O_N * P6W_N;  // arm 1 offset
    const int TK = A1 + P6O_TAKEN * P6W_N, ST = A1 + P6O_STALE * P6W_N, PL0 = P6O_PLAIN * P6W_N;

    P6wWait(5, P6W_GATE, 11, 700);  // off: nothing
    t_p6wOut = P6O_TAKEN, t_p6wArm = 1, P6wJobEnd(5, 900);
    assert(k.p6wN[TK + P6W_GATE] == 0 && k.p6wJobN[P6O_NONE + P6O_TAKEN] == 0);
    k.p6wReady = 1;
    // (a) on arm: a gate wait that ends mid-job (lent lock) stays pending until the worker publishes, booked at the next wait,
    // in the job's arm even though the switch flipped meanwhile
    P6wWait(5, P6W_GATE, 11, 700);
    P6wWait(5, P6W_LOCK, 0, 300);
    assert(k.p6wN[TK + P6W_GATE] == 0 && g_p6wPend[0].used && g_p6wPend[1].used);
    t_p6wOut = P6O_TAKEN, t_p6wArm = 1, P6wJobEnd(5, 900);
    assert(k.p6wJobN[P6O_NONE + P6O_TAKEN] == 1 && k.p6wSpanUs[P6O_NONE + P6O_TAKEN] == 900);
    k.p6Plan = 0;
    P6wWait(0, P6W_RESULT, 0, 50);  // no running job: books itself in the current arm (0) and flushes the two
    assert(k.p6wN[TK + P6W_GATE] == 1 && k.p6wUs[TK + P6W_GATE] == 700 && k.p6wN[TK + P6W_LOCK] == 1 &&
           k.p6wN[P6O_NONE * P6W_N + P6W_RESULT] == 1 && !g_p6wPend[0].used && !g_p6wPend[1].used);
    assert(k.p6wGateUs[16 + 10] == 700 && k.p6wGateUs[10] == 0);
    // (b) a wait that ends after the job (RtWaitJob, collect): booked at once
    t_p6wOut = P6O_STALE, t_p6wArm = 1, P6wJobEnd(6, 1200);
    P6wWait(6, P6W_RTWAIT, 0, 400), P6wWait(6, P6W_GATE, 1, 200);
    assert(k.p6wN[ST + P6W_RTWAIT] == 1 && k.p6wUs[ST + P6W_RTWAIT] == 400 && k.p6wGateUs[16] == 200);
    // (c) off arm, foreign bit masked; an out-of-range outcome publishes as plain
    t_p6wOut = 99, t_p6wArm = 0, P6wJobEnd(7, 100);
    P6wWait(7 | 0x80000000, P6W_RESULT, 0, 10);
    assert(k.p6wN[PL0 + P6W_RESULT] == 1 && k.p6wJobN[P6O_PLAIN] == 1);
    // (d) the outcome slot reused 16 jobs later: lost, never booked as the newer job's outcome
    P6wWait(8, P6W_HOST, 0, 10);  // pending
    t_p6wOut = P6O_REFUSED, t_p6wArm = 0, P6wJobEnd(24, 100);
    P6wWait(0, P6W_LOCK, 0, 1);
    assert(k.p6wLost == 1 && k.p6wN[P6O_REFUSED * P6W_N + P6W_HOST] == 0 && !g_p6wPend[0].used);
    // (e) pending ring full: the oldest goes to lost
    for (int i = 0; i < 33; i++) P6wWait(100, P6W_LOCK, 0, 1);
    assert(k.p6wLost == 2);
    // (f) only the owner thread books (another thread's wait is ignored)
    HANDLE th = CreateThread(NULL, 0, [](LPVOID) -> DWORD { P6wWait(0, P6W_LOCK, 0, 5); return 0; }, NULL, 0, NULL);
    WaitForSingleObject(th, INFINITE), CloseHandle(th);
    assert(k.p6wN[P6O_NONE * P6W_N + P6W_LOCK] == 1);  // (d)'s one, not the thread's
    P6wLogCounters();
    // (g) arm at the job's start (v131): the loop's p6warm wins over the P6 switch; 0 = the switch (as before)
    const LONG armSetSave = k.p6wArmSet;
    k.p6wArmSet = 0, k.p6Plan = 1; assert(P6wArmNow() == 1);
    k.p6Plan = 0; assert(P6wArmNow() == 0);
    k.p6wArmSet = 1, k.p6Plan = 1; assert(P6wArmNow() == 0);  // ladder off arm: P6 on in both arms
    k.p6wArmSet = 2, k.p6Plan = 0; assert(P6wArmNow() == 1);
    k.p6wArmSet = armSetSave;

    memcpy(&k.p6wReady, save, blk);
    memset(g_p6wPend, 0, sizeof(g_p6wPend));
    memset(g_p6wDone, 0, sizeof(g_p6wDone));
    g_async = as;
    t_p6wOut = outSave, t_p6wArm = armSave;
    k.p6Plan = planSave;
    printf("P6Waits: arm x outcome x class split, pending ring, slot reuse, owner-only, loop arm ok\n");
}
