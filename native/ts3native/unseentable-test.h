// TestUnseenTable ([UnseenTable], unseentable.cpp, docs/227 §6). Included by selftest.cpp after unseenanim-test.h (uses
// unseen-test.h's fakes). Fakes only; no game code runs. Checks: lazy rows (one row per object at its first ask, later asks
// are lazy hits, an object off the agent list gets a row too), answers (class, kind, position, parent), mode 0 and another
// thread run the old rule, Check counts a stale row as an unsafe mismatch, the next tick starts a new generation, Fetch walks
// the fake agent list once (duplicate owner once) and returns the listed rows sorted by id with the documented bits, a broken
// list fetches nothing but keeps the native rows, Bits (§9) answers one listed row with the fetch's bits and computes only
// that row (not listed / broken list = -2, mode 0 / other thread = -1), RefreshTicks keeps rows for N pumps unless the camera moves or a world
// loads (utbForced) and the unseentablettl switch, PosUnseen answers as viewer codes 3-6.
static void TestUnseenTable() {
    Ts3nCounters& k = ts3native_counters;
    static_assert(offsetof(Ts3nCounters, utbReady) == 0x5690 && offsetof(Ts3nCounters, utbListCap) == 0x56CC &&
                  sizeof(Ts3nCounters) == 6074 * 4, "counters v149 + v152 layout (docs/registry.md)");
    static Ts3nCounters saved;
    memcpy(&saved, &k, sizeof(k));
    const UnCompFn compSave = g_unComp;
    const UnPosFn posSave = g_unPos;
    const UnFrustumFn frSave = g_unFrustum;
    const UnInFrustumFn inSave = g_unInFrustum;
    const UnCamFn cpSave = g_unCamPos, ctSave = g_unCamTgt;
    const DWORD *btSave = g_unBitTransform, *bsSave = g_unBitSim;
    u8* const* sysSave = g_unLocoSys;
    const auto viewSave = g_utView;
    const bool ucSave = g_ucOn;
    g_ucOn = false;
    g_unComp = UnFakeComp, g_unPos = UnFakePos, g_unFrustum = UnFakeFrustum, g_unInFrustum = UnFakeIn;
    g_unCamPos = UnFakeCamPos, g_unCamTgt = UnFakeCamTgt, g_unBitTransform = &kUnBitT, g_unBitSim = &kUnBitS;
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
    g_unFakeCam[0][0] = 0, g_unFakeCam[0][1] = 30, g_unFakeCam[0][2] = 0;  // reach 80 around the origin, screen |x| < 10
    memset(g_unFakeCam[1], 0, 12);
    // household Sim 50 m from the simF Sim, fresh table
    g_utView.n = 1, g_utView.pos[0][0] = 450, g_utView.pos[0][1] = 0, g_utView.pos[0][2] = 7, g_utView.at = (GetTickCount() - 1) | 1;

    // owner id at +8/+0xC = pos[2], pos[3] (float bits): simV id lo 0, simF id lo bits(7.0f)
    __declspec(align(16)) static UnFakeObj simV = {{2, 0, 0, 1}, true}, simF = {{500, 0, 7, 1}, true}, car = {{-300, 0, 3, 1}, false},
                                         lost = {{600, 0, 9, 1}, true};
    static u8 parent[4];
    car.parent = parent;
    // agent list: sentinel at sys+0x40; agents with owner at +4 and node at +0x40 (next pointer at node+0); simF twice
    static u8 sys[0x80], ag[4][0x50];
    static u8* sysPtr = sys;
    void* owners[4] = {&simV, &simF, &car, &simF};
    for (int i = 0; i < 4; i++) {
        *(void**)(ag[i] + 4) = owners[i];
        *(u8**)(ag[i] + 0x40) = i < 3 ? ag[i + 1] + 0x40 : sys + 0x40;
    }
    *(u8**)(sys + 0x40) = ag[0] + 0x40;
    g_unLocoSys = &sysPtr;
    g_utbRadius = 3.0f, g_utbTid = GetCurrentThreadId(), g_utbClass = UtbClass, g_utbBuilt = 0, g_utbTick = 5;
    k.utbMode = 1, k.utbCheck = 1, k.utbTtlMode = 1, g_utbRefreshTicks = 1;
    const LONG builds0 = k.utbBuilds, calc0 = k.utbRowCalc;

    int kind = -1;
    float pos[3] = {};
    bool par = true;
    assert(UnObjClass(&simF, &kind, pos, &par) == 1 && kind == 0 && pos[0] == 500 && !par && k.utbBuilds == builds0 + 1);
    assert(k.utbRows == 1 && k.utbRowCalc == calc0 + 1 && k.utbLazyHits == 0);  // lazy: only the asked row
    assert(UnObjClass(&simV, &kind) == 0 && kind == 0 && k.utbBuilds == builds0 + 1 && k.utbRows == 2);  // same tick: no rebuild
    assert(UnObjClass(&car, &kind, NULL, &par) == 1 && kind == 1 && par && k.utbHits == 3);
    const LONG miss0 = k.utbMiss;
    assert(UnObjClass(&lost, &kind) == 1 && k.utbMiss == miss0 && k.utbRows == 4);  // off the list: a row too
    assert(UnObjClass(&simV, &kind) == 0 && k.utbLazyHits == 1 && k.utbRowCalc == calc0 + 4);
    assert(k.utbMisUnsafe == 0 && k.utbMisSafe == 0);
    simF.pos[0] = 5;  // on screen later in the same tick: the row still says unseen, Check counts it
    assert(UnObjClass(&simF, &kind) == 1 && k.utbMisUnsafe == 1);
    g_utbTick++;  // next pump
    assert(UnObjClass(&simF, &kind) == 0 && k.utbBuilds == builds0 + 2 && k.utbMisUnsafe == 1);
    simF.pos[0] = 500;
    g_utbTick++;

    // the script fetch: MonoArray max_length +8, elements +16
    __declspec(align(16)) static struct { void* vt; void* sync; LONG max; LONG pad; unsigned __int64 v[8]; } ids;
    __declspec(align(16)) static struct { void* vt; void* sync; LONG max; LONG pad; LONG v[8]; } bits;
    ids.max = 8, bits.max = 8;
    const int n = UtbIcFetch(&ids, &bits);
    assert(n == 3 && ids.v[0] < ids.v[1] && ids.v[1] < ids.v[2] && k.utbFetches > 0);
    assert(UnObjClass(&car, &kind) == 1 && k.utbLazyHits == 3 && UtbIcFetch(&ids, &bits) == 3 && k.utbRows == 3);  // one walk
    float z7 = 7;
    DWORD z7b;
    memcpy(&z7b, &z7, 4);
    int iv = -1, ifar = -1;
    for (int i = 0; i < n; i++) {
        if ((DWORD)ids.v[i] == 0) iv = i;
        if ((DWORD)ids.v[i] == z7b) ifar = i;
    }
    assert(iv >= 0 && ifar >= 0);
    assert((bits.v[iv] & (UTB_VALID | UTB_SIM | UTB_ONR | UTB_REACH | UTB_UNSEEN)) == (UTB_VALID | UTB_SIM | UTB_ONR | UTB_REACH));
    assert((bits.v[ifar] & (UTB_VALID | UTB_SIM | UTB_ONR | UTB_REACH | UTB_UNSEEN | UTB_VIEWER | UTB_FRESH)) ==
           (UTB_VALID | UTB_SIM | UTB_UNSEEN | UTB_VIEWER | UTB_FRESH));
    // Bits (§9, no copy): a new generation walks the list but computes only the asked row; the same bits as the fetch
    const DWORD vLo = (DWORD)ids.v[iv], vHi = (DWORD)(ids.v[iv] >> 32), fLo = (DWORD)ids.v[ifar], fHi = (DWORD)(ids.v[ifar] >> 32);
    const LONG fBits = bits.v[ifar], vBits = bits.v[iv];
    g_utbTick++;
    assert(UtbIcBits(fLo, fHi) == fBits && k.utbRows == 1);
    assert(UtbIcBits(vLo, vHi) == vBits && k.utbRows == 2 && UtbIcBits(fLo, fHi) == fBits && k.utbRows == 2);
    assert(UtbIcBits(1234, 0) == -2 && UtbIcFetch(&ids, &bits) == 3 && k.utbRows == 3);  // not listed; the fetch adds car only
    // mode 0: no table, the old rule and no fetch
    k.utbMode = 0;
    const LONG hits0 = k.utbHits;
    assert(UnObjClass(&simF, &kind) == 1 && k.utbHits == hits0 && UtbIcFetch(&ids, &bits) == -1 && UtbIcBits(fLo, fHi) == -1);
    k.utbMode = 1;
    // another thread never reads the table
    g_utbTid = GetCurrentThreadId() + 1;
    assert(UnObjClass(&simF, &kind) == 1 && k.utbHits == hits0 && UtbIcFetch(&ids, &bits) == -1 && UtbIcBits(fLo, fHi) == -1);
    // RefreshTicks 4: rows live 4 pumps; a camera move > 1 m or a world load starts a generation early; switch off = per pump
    g_utbTid = GetCurrentThreadId();
    g_utbRefreshTicks = 4;
    g_utbWorlds++, g_utbTick++;  // a world load: a generation at once (1 pump after the last one)
    const LONG f = k.utbForced;
    LONG b = k.utbBuilds;
    UnObjClass(&simV, &kind);
    assert(k.utbForced == f + 1 && k.utbBuilds == b + 1);
    const LONG forced0 = k.utbForced;
    b = k.utbBuilds;
    g_utbTick++;
    assert(UnObjClass(&simV, &kind) == 0 && k.utbBuilds == b && k.utbForced == forced0);  // kept
    g_unFakeCam[0][0] = 0.5f, g_utbTick++;
    assert(UnObjClass(&simV, &kind) == 0 && k.utbBuilds == b);  // moved < 1 m: kept
    g_unFakeCam[0][0] = 5, g_utbTick++;
    assert(UnObjClass(&simV, &kind) == 0 && k.utbBuilds == b + 1 && k.utbForced == forced0 + 1);
    g_utbWorlds++, g_utbTick++;
    assert(UnObjClass(&simV, &kind) == 0 && k.utbBuilds == b + 2 && k.utbForced == forced0 + 2);
    for (int t = 0; t < 4; t++) g_utbTick++, UnObjClass(&simV, &kind);
    assert(k.utbBuilds == b + 3 && k.utbForced == forced0 + 2);  // due after 4 pumps
    k.utbTtlMode = 0, g_utbTick++;
    assert(UnObjClass(&simV, &kind) == 0 && k.utbBuilds == b + 4 && k.utbForced == forced0 + 2);
    g_unFakeCam[0][0] = 0, g_utbRefreshTicks = 1, k.utbTtlMode = 1;
    // a broken list (owner read faults): the fetch returns nothing, native rows still answer
    *(u8**)(ag[1] + 0x40) = (u8*)(uintptr_t)0x40;  // next node 0x40: its owner at 0x4 faults
    g_utbTick++;
    const LONG faults0 = k.utbFaults, hits1 = k.utbHits;
    assert(UtbIcFetch(&ids, &bits) == 0 && k.utbFaults == faults0 + 1 && UtbIcBits(vLo, vHi) == -2);
    assert(UnObjClass(&simV, &kind) == 0 && k.utbHits == hits1 + 1);
    assert(UtbReadyMsg(26) == 1);
    // PosUnseen: the same answer as viewer codes 3-6 (UfCamMsg), in one call; short array or bad radius = seen
    {
        g_unFakeCam[0][0] = 0, g_unFakeCam[0][1] = 30, g_unFakeCam[0][2] = 0;
        memset(g_unFakeCam[1], 0, 12);
        g_unCam.ok = false;
        auto F = [](float f) { DWORD d; memcpy(&d, &f, 4); return d; };
        __declspec(align(16)) static struct { void* vt; void* sync; LONG max; LONG pad; float v[4]; } q;
        const float cases[][4] = {{200, 0, 0, 3}, {5, 0, 300, 3}, {60, 0, 0, 3}, {200, 0, 0, 150}, {200, 0, 0, -1}};
        int ones = 0;
        for (const auto& c : cases) {
            UfCamMsg(F(c[0]), 0x83030000), UfCamMsg(F(c[1]), 0x83040000), UfCamMsg(F(c[2]), 0x83050000);
            const int old = UfCamMsg(F(c[3]), 0x83060000);
            q.max = 4, memcpy(q.v, c, 16);
            assert(UtbIcPosUnseen(&q) == old);
            ones += old;
        }
        assert(ones == 1);  // only the first case is unseen
        q.max = 3, q.v[0] = 200, q.v[1] = 0, q.v[2] = 0, q.v[3] = 3;
        assert(UtbIcPosUnseen(&q) == 0 && UtbIcPosUnseen(NULL) == 0);
    }

    g_utbClass = NULL, g_utbTid = 0, g_utbBuilt = 0, g_utbN = 0, g_utbGen++, g_utbGenTick = 0, g_utbRefreshTicks = 1;
    g_unLocoSys = sysSave;
    g_utView = viewSave;
    g_ucOn = ucSave;
    g_unComp = compSave, g_unPos = posSave, g_unFrustum = frSave, g_unInFrustum = inSave;
    g_unCamPos = cpSave, g_unCamTgt = ctSave, g_unBitTransform = btSave, g_unBitSim = bsSave;
    memcpy(&k, &saved, sizeof(k));
    printf("selftest unseen table ok\n");
}
