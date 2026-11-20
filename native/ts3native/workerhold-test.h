// docs/673 workerhold.cpp: [RouteJobPhases] HoldProbe. Included by selftest.cpp and workerhold-host-test.cpp.
// The buckets, the per-job boundary record (first boundary with the sim waiting), the duplicate ring, and the hooks on
// counters: a job with plans, boundaries and a sim wait; a failed plan; a sync plan; a repeat; mode 0 counts nothing.

static void TestWhCore() {
    assert(WhJobBucket(0) == 0 && WhJobBucket(4999) == 0 && WhJobBucket(5000) == 1 && WhJobBucket(19999) == 1 && WhJobBucket(20000) == 2);
    assert(WhJobBucket(99999) == 3 && WhJobBucket(100000) == 4 && WhJobBucket(249999) == 4 && WhJobBucket(250000) == 5 && WhJobBucket(9000000) == 5);
    assert(WhCandBucket(0) == 0 && WhCandBucket(1) == 0 && WhCandBucket(2) == 1 && WhCandBucket(3) == 2 && WhCandBucket(9) == 2 && WhCandBucket(10) == 3);
    WhJob j;
    WhJobReset(&j, false);
    assert(!WhBoundary(&j, WH_WORLD, true, 5) && j.seen[WH_WORLD] == 0);  // no job: nothing
    WhJobReset(&j, true);
    assert(!WhBoundary(&j, WH_PLAN, false, 10) && j.seen[WH_PLAN] == 1 && j.firstKind == -1);
    assert(!WhBoundary(&j, -1, true, 10) && !WhBoundary(&j, WH_KINDS, true, 10) && j.firstKind == -1);
    assert(WhBoundary(&j, WH_WATER, true, 20) == 1 && j.firstKind == WH_WATER && j.firstQ == 20 && j.waiting[WH_WATER] == 1);
    assert(!WhBoundary(&j, WH_POST, true, 30) && j.firstKind == WH_WATER && j.firstQ == 20 && j.waiting[WH_POST] == 1 && j.seen[WH_POST] == 1);
    static WhDupRing r;
    memset(&r, 0, sizeof(r));
    long prev = 0;
    assert(!WhDupNote(&r, 0x25000001, 0, 1000, 1030, 30000, 2000, &prev) && r.n == 1);
    assert(!WhDupNote(&r, 0x25000001, 1, 1040, 1050, 10000, 2000, &prev) && r.n == 2);           // another IC kind: not a repeat
    assert(WhDupNote(&r, 0x25000001, 0, 3030, 3040, 10000, 2000, &prev) == 1 && prev == 30000);  // exactly at the window
    assert(!WhDupNote(&r, 0x25000001, 0, 5041, 5050, 10000, 2000, &prev) && r.n == 2);           // 1 ms past it; the slot moves on
    assert(WhDupNote(&r, 0x25000001, 0, 5060, 5070, 7000, 2000, &prev) == 1 && prev == 10000);
    assert(WhDupNote(&r, 0x25000001, 0, 0xFFFFFFF0u, 0xFFFFFFF8u, 1, 2000, &prev) == 0);         // the clock far away
    assert(WhDupNote(&r, 0x25000001, 0, 5, 9, 1, 2000, &prev) == 1);                             // GetTickCount wrap: 13 ms later
    for (uint32_t i = 0; i < WH_DUPS + 4; i++) assert(!WhDupNote(&r, 0x26000000 + i, 0, 100, 110, 1, 2000, &prev));
    assert(r.n == WH_DUPS && WhDupFind(&r, 0x25000001, 0) < 0 && WhDupFind(&r, 0x26000000 + WH_DUPS + 3, 0) >= 0);  // oldest replaced
}

static void TestWorkerHold() {
    TestWhCore();
    WorkerHoldCounters& c = ts3native_workerhold;
    static WorkerHoldCounters saved;
    static WhDupRing savedRing;
    memcpy((void*)&saved, (const void*)&c, sizeof(c));
    memcpy(&savedRing, &g_whDups, sizeof(savedRing));
    const LONG savedWait = g_whSimWait;
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
    memset((void*)&c, 0, sizeof(c));
    memset(&g_whDups, 0, sizeof(g_whDups));
    g_whSimWait = 0;
    // mode 0: nothing counted, no job opened
    WhJobBegin();
    WhEnter(PCC_PH);
    WhPlanEnd(1, 100, 1000, 1000, 0);
    WhJobEnd(0x25000001, 0, 30000);
    assert(!t_whJob.open && c.whJobN[0][2] == 0 && c.whPlanN[1][0][0] == 0 && c.whSeen[WH_PLAN] == 0);
    // a job: plan, candidate, lot leg, world; the sim starts to wait; water is the first boundary in the wait, post the second
    c.whMode = 1, c.whDupMs = 2000;
    WhJobBegin();
    assert(t_whJob.open);
    WhEnter(PCC_PH), WhEnter(PCC_CHOOSE), WhEnter(PCC_LOTPLAN), WhEnter(PCC_CELL), WhEnter(PCC_WORLD);
    g_whSimWait = 1;
    WhEnter(PCC_HOST);  // not a boundary
    assert(t_whJob.firstKind == -1);
    WhEnter(PCC_WATER), WhEnter(PCC_POST);
    assert(t_whJob.firstKind == WH_WATER && t_whJob.waiting[WH_POST] == 1);
    WhPlanEnd(1, 2000, 25000, g_qpf.QuadPart * 25 / 1000, 0);
    g_whSimWait = 0;
    WhJobEnd(0x25000001, 0, 30000);
    assert(c.whJobN[0][2] == 1 && c.whJobUs[0][2] == 30000 && c.whJobN[1][2] == 0 && c.whOutUs == 5000);
    assert(c.whPlanN[0][0][0] == 1 && c.whPlanUs[0][0][0] == 25000 && c.whPlanCellUs[0][0] == 2000);
    assert(c.whSeen[WH_PLAN] == 1 && c.whSeen[WH_CAND] == 1 && c.whSeen[WH_LOTLEG] == 1 && c.whSeen[WH_WORLD] == 1 && c.whSeen[WH_WATER] == 1);
    assert(c.whWaiting[WH_WORLD] == 0 && c.whWaiting[WH_WATER] == 1 && c.whWaiting[WH_POST] == 1);
    assert(c.whFirstN[WH_WATER] == 1 && c.whFirstN[WH_POST] == 0 && c.whFirstRemUs[WH_WATER] >= 0 && c.whEndWait == 0 && c.whDupN == 0);
    // the same route and kind again at once: a repeat; its plan failed with 52 candidates; the sim waits at the end only
    WhJobBegin();
    WhEnter(PCC_PH);
    for (int i = 0; i < 52; i++) WhEnter(PCC_CHOOSE);
    WhPlanEnd(52, 212000, 229000, g_qpf.QuadPart * 229 / 1000, 5);
    g_whSimWait = 1;
    WhJobEnd(0x25000001, 0, 231000);
    g_whSimWait = 0;
    assert(c.whJobN[1][4] == 1 && c.whJobUs[1][4] == 231000 && c.whPlanN[0][1][3] == 1 && c.whPlanCellUs[0][1] == 212000);
    assert(c.whSeen[WH_CAND] == 53 && c.whEndWait == 1 && c.whDupN == 1 && c.whDupUs == 231000 && c.whOutUs == 7000);
    // a plan outside a job (the sim thread's sync plan): counted as sync, no boundary
    WhEnter(PCC_PH);
    WhPlanEnd(2, 0, 120000, 0, 0);
    assert(c.whPlanN[1][0][1] == 1 && c.whPlanUs[1][0][1] == 120000 && c.whSeen[WH_PLAN] == 2);
    // another IC kind of the route: no repeat
    WhJobBegin();
    WhJobEnd(0x25000001, 2, 1000);
    assert(c.whDupN == 1 && c.whJobN[0][0] == 1);
    memcpy((void*)&c, (const void*)&saved, sizeof(c));
    memcpy(&g_whDups, &savedRing, sizeof(savedRing));
    g_whSimWait = savedWait;
    printf("worker hold probe: OK (buckets, first boundary in a sim wait, plans by candidates and result, repeats, mode 0)\n");
}
