// --- [RouteSnap] StaleLot (stalelot.cpp, docs/189 phase 2): the write ring, the per-lot verdict, the read-set note, the
// classifier, and the two arms on the P5 fake world (TestPcMode2's clone life). Included by selftest.cpp after TestP6Early.

static const u64 kStlLotA = 0x7777000011112222ull, kStlLotB = 0x7777000033334444ull;
static void StlTW(int kind, u64 key) { SpLockD(); SpWrite(kind, key); SpUnlockD(); }
static void StlTWLotA() { StlTW(SLW_LOT, kStlLotA); }       // a lot-keyed write on a lot no fake run reads
static void StlTWWorld() { StlTW(SLW_FP, 0); }             // a footprint-object write: world-level
static void StlTWRaw() { InterlockedIncrement(&g_spWrites); }  // an increment outside the ring (replay lock-key cover, ...)

static void TestStaleLotUnit() {
    static_assert(offsetof(Ts3nCounters, stlMode) == 0x4F3C && offsetof(Ts3nCounters, stlMat) == 0x4F48 &&
                  offsetof(Ts3nCounters, stlWhy) == 0x4F68 && offsetof(Ts3nCounters, stlWorld) == 0x4F80 &&
                  offsetof(Ts3nCounters, stlWrites) == 0x4FB0 && offsetof(Ts3nCounters, stlReadHist) == 0x4FB8 &&
                  offsetof(Ts3nCounters, stlVer) == 0x4FD8 && offsetof(Ts3nCounters, stlActOff) == 0x4FF4 &&
                  offsetof(Ts3nCounters, stlSpare) == 0x4FF8 && sizeof(Ts3nCounters) == 5817 * 4,
                  "counters v125 layout after reserved v123 / v124 (docs/registry.md)");
    const LONG on0 = g_stlOn;
    g_stlOn = 1;
    int why, wk;
    const u64 rdB[1] = {kStlLotB}, rdA[1] = {kStlLotA};
    // (u1) a write on an unread lot: clean; on a read lot: stale (why 0); no write: clean under both
    LONG w0 = g_spWrites;
    assert(!StlStaleOf(w0, g_spWrites, rdB, 1, false, true, 0, &why, &wk) && why == -1);
    StlTWLotA();
    assert(!StlStaleOf(w0, g_spWrites, rdB, 1, false, true, 0, &why, &wk));
    assert(StlStaleOf(w0, g_spWrites, rdA, 1, false, true, 0, &why, &wk) && why == 0);
    // (u2) the active lot is always read
    assert(StlStaleOf(w0, g_spWrites, rdB, 1, false, true, kStlLotA, &why, &wk) && why == 0);
    // (u3) window cases: writes at or before the cut do not count; the first one after it does
    w0 = g_spWrites;
    assert(!StlStaleOf(w0, g_spWrites, rdA, 1, false, true, 0, &why, &wk));
    StlTW(SLW_LOT, kStlLotB);
    assert(StlStaleOf(w0, g_spWrites, rdB, 1, false, true, 0, &why, &wk) && why == 0);
    assert(!StlStaleOf(w0, g_spWrites, rdA, 1, false, true, 0, &why, &wk));
    // (u4) world-level writes (and lot 0) are stale whatever the read set, with their kind
    w0 = g_spWrites;
    StlTWLotA();
    StlTW(SLW_R, 0);
    assert(StlStaleOf(w0, g_spWrites, rdB, 1, false, true, 0, &why, &wk) && why == 1 && wk == SLW_R);
    w0 = g_spWrites;
    StlTW(SLW_LOT, 0);
    assert(StlStaleOf(w0, g_spWrites, rdB, 1, false, true, 0, &why, &wk) && why == 1 && wk == SLW_LOT0);
    // (u5) an increment outside the ring, and a slot still being written: unknown (why 2)
    w0 = g_spWrites;
    StlTWRaw();
    assert(StlStaleOf(w0, g_spWrites, rdB, 1, false, true, 0, &why, &wk) && why == 2);
    w0 = g_spWrites;
    StlTWLotA();
    StlEnt& e = g_stlRing[g_spWrites & (kStlRing - 1)];
    const LONG sv = e.seq;
    e.seq = 0;
    assert(StlStaleOf(w0, g_spWrites, rdB, 1, false, true, 0, &why, &wk) && why == 2);
    e.seq = sv;
    // (u6) a window wider than the ring (why 3); a read set over its cap (why 4); no read set (why 5); counts that went back
    w0 = g_spWrites;
    for (int i = 0; i <= kStlRing; i++) StlTWLotA();
    assert(StlStaleOf(w0, g_spWrites, rdB, 1, false, true, 0, &why, &wk) && why == 3);
    w0 = g_spWrites;
    StlTWLotA();
    assert(StlStaleOf(w0, g_spWrites, rdB, 1, true, true, 0, &why, &wk) && why == 4);
    assert(StlStaleOf(w0, g_spWrites, rdB, 1, false, false, 0, &why, &wk) && why == 5);
    assert(StlStaleOf(g_spWrites, w0, rdB, 1, false, true, 0, &why, &wk) && why == 3);
    // (u7) ring off: nothing stored, so a later reader of that window sees "unknown"
    g_stlOn = 0;
    w0 = g_spWrites;
    StlTWLotA();
    g_stlOn = 1;
    assert(StlStaleOf(w0, g_spWrites, rdB, 1, false, true, 0, &why, &wk) && why == 2);
    // (u8) the classifier: lot keys from the Q (+0x18/+0x1C), W3's arguments, W4's lot graph; world kinds for the rest
    {
        DWORD q[16] = {}, gr[4] = {0x33334444, 0x77770000};
        q[6] = 0x11112222, q[7] = 0x77770000;
        DWORD slot[8] = {(DWORD)(uintptr_t)q, 0, 0, 0x11112222, 0x77770000, 0, 0, 0};  // slot = &slot[2]: [-2] = Q, [1..2] args
        int leafRow[SL_N];
        for (int i = 0; i < SL_N; i++) leafRow[i] = -1;
        for (int i = 0; i < SN_N; i++) if (kSn[i].leaf < SL_N && leafRow[kSn[i].leaf] < 0) leafRow[kSn[i].leaf] = i;
        u64 key;
        assert(StlClassify(kSn[leafRow[SL_W1]], slot + 2, &key) == SLW_LOT && key == kStlLotA);
        assert(StlClassify(kSn[leafRow[SL_W2]], slot + 2, &key) == SLW_LOT && key == kStlLotA);
        slot[3] = 0x11112222, slot[4] = 0x77770000;
        assert(StlClassify(kSn[leafRow[SL_W3]], slot + 2, &key) == SLW_LOT && key == kStlLotA);
        slot[3] = (DWORD)(uintptr_t)gr;
        assert(StlClassify(kSn[leafRow[SL_W4]], slot + 2, &key) == SLW_LOT && key == kStlLotB);
        assert(StlClassify(kSn[leafRow[SL_W5]], slot + 2, &key) == SLW_TABLE);
        assert(StlClassify(kSn[leafRow[SL_REMOVELOT]], slot + 2, &key) == SLW_TABLE);
        assert(StlClassify(kSn[leafRow[SL_W6]], slot + 2, &key) == (SLW_W6 | SLW_COV));  // covered (docs/189 §9)
        assert(StlClassify(kSn[leafRow[SL_FPCORE]], slot + 2, &key) == (SLW_FP | SLW_COV) &&
               StlClassify(kSn[leafRow[SL_FPREMOVE]], slot + 2, &key) == (SLW_FP | SLW_COV));
        assert(StlClassify(kSn[leafRow[SL_APPLYLOT]], slot + 2, &key) == SLW_R);
        assert(StlClassify(kSn[leafRow[SL_GRIDADD]], slot + 2, &key) == SLW_GRID);
        assert(StlClassify(kSn[leafRow[SL_HIGHCOST]], slot + 2, &key) == SLW_TILES);
        assert(StlClassify(kSn[leafRow[SL_GBUILD]], slot + 2, &key) == SLW_GBUILD);
        slot[0] = 0x10;  // an unreadable Q: lot 0 (world-level once stored), no fault out
        assert(StlClassify(kSn[leafRow[SL_W1]], slot + 2, &key) == SLW_NOKEY || key == 0);
        // SpNote wiring: a W3 hit stores the lot in the ring at its own seq
        const LONG spOn0 = g_spOn;
        const SpLotStrategy* lot0 = g_spLot;
        g_spOn = 1, g_spLot = &kSpLotNone;
        slot[3] = 0x33334444, slot[4] = 0x77770000;
        SpNote(leafRow[SL_W3], slot + 2);
        const StlEnt& f = g_stlRing[g_spWrites & (kStlRing - 1)];
        assert(f.seq == g_spWrites && f.kind == SLW_LOT && f.lo == 0x33334444 && f.hi == 0x77770000);
        g_spOn = spOn0, g_spLot = lot0;
    }
    // (u9) the read-set note: only inside a run, de-duplicated, over the cap = over
    {
        u64 kk = kStlLotA;
        DWORD slot[3] = {0, 0, (DWORD)(uintptr_t)&kk};
        StlRunEnd();
        StlFindEnter(slot);
        StlRunBegin(2);
        assert(t_stlN == 0);
        StlFindEnter(slot), StlFindEnter(slot);
        assert(t_stlN == 1 && t_stlKeys[0] == kStlLotA && !t_stlOver);
        for (int i = 0; i < kStlKeys + 1; i++) { kk = 0x100 + i; StlFindEnter(slot); }
        assert(t_stlN == kStlKeys && t_stlOver);
        StlRunEnd();
        StlRunBegin(0);
        assert(!t_stlRun);
    }
    g_stlOn = on0;
}

// the arms on the P5 fake world (P5c on, Route.Plan taken). The fake clone run calls no LotTable_Find: its read set is empty
// (plus the fake active lot, never kStlLotA)
static void TestStaleLotFp(int* list, int* s, int* g, u8* opts, DWORD& seq);
static void TestStaleLot(int* list, int* s, int* g, u8* opts, DWORD& seq) {
    TestStaleLotUnit();
    Ts3nCounters& k = ts3native_counters;
    void* const tr0 = g_stlFindTramp;
    const LONG on0 = g_stlOn, rd0 = k.stlReady, m0 = k.stlMode, pl0 = k.p6Plan;
    g_stlOn = 1, k.stlReady = 3, g_stlFindTramp = (void*)1, k.p6Plan = 1, k.p6Early = 0;
    // (a1) acting: a write on an unread lot after the cut: the early refusal is deferred, the run's verdict takes it
    {
        k.stlMode = 1;
        const LONG tk = k.m2Taken, at = k.stlActTaken, mat = k.stlMat[1][1], st = k.p5cStale[0] + k.p5cStale[1];
        AsyncJob a = PcArmM2(seq++, 0);
        PcPlanM2(a, list, s, g, opts, StlTWLotA);
        assert(k.m2Taken == tk + 1 && k.stlActTaken == at + 1 && k.stlMat[1][1] == mat + 1 && !g_rpHoldAt);
        (void)st;
        P6eUseFree();
    }
    // (a2) acting: a world-level write after the cut: refused before the acquire as today
    {
        const LONG tk = k.m2Taken, ep = k.m2Fb[P4_AB_EPOCH], st = k.p5cStale[0];
        AsyncJob a = PcArmM2(seq++, 0);
        PcPlanM2(a, list, s, g, opts, StlTWWorld);
        assert(k.m2Taken == tk && k.m2Fb[P4_AB_EPOCH] == ep + 1 && k.p5cStale[0] == st + 1 && !g_rpHoldAt);
        P6eUseFree();
    }
    // (a3) acting: an increment outside the ring: refused
    {
        const LONG tk = k.m2Taken, ep = k.m2Fb[P4_AB_EPOCH];
        AsyncJob a = PcArmM2(seq++, 0);
        PcPlanM2(a, list, s, g, opts, StlTWRaw);
        assert(k.m2Taken == tk && k.m2Fb[P4_AB_EPOCH] == ep + 1 && !g_rpHoldAt);
        P6eUseFree();
    }
    // (a4) acting, no read set (the detour missing): a lot write still refuses (why 5) at the early step's place
    {
        g_stlFindTramp = NULL;
        const LONG tk = k.m2Taken;
        AsyncJob a = PcArmM2(seq++, 0);
        PcPlanM2(a, list, s, g, opts, StlTWLotA);
        assert(k.m2Taken == tk && !g_rpHoldAt);
        g_stlFindTramp = (void*)1;
        P6eUseFree();
    }
    // (s1) shadow: the same unread-lot write is refused (the global rule acts), counted per-lot-only in arm 0
    {
        k.stlMode = 2;
        const LONG tk = k.m2Taken, at = k.stlActTaken, nr = k.stlNoRun, mat = k.stlMat[0][1];
        AsyncJob a = PcArmM2(seq++, 0);
        PcPlanM2(a, list, s, g, opts, StlTWLotA);
        // the early refusal skips the run, so shadow has no verdict for it (slNoRun); acting is what runs it
        assert(k.m2Taken == tk && k.stlActTaken == at && k.stlNoRun == nr + 1 && k.stlMat[0][1] == mat && !g_rpHoldAt);
        P6eUseFree();
        PcRunM2(seq++, list, s, g, opts, false, 0);  // no write: taken, both clean
        assert(k.m2Taken == tk + 1 && k.stlMat[0][0] > 0);
    }
    // (v1) verify job under shadow with an unread-lot write: per-lot-only, the fake answers are equal (slVer 0)
    {
        const LONG ver0 = k.stlVer[0], v = g_p5Verify, vl = g_p5VerLend;
        g_p5Verify = 1, g_p5VerLend = 1;
        AsyncJob a = PcArmM2(seq++, 0);
        assert(a.p4 == 3);
        PcPlanM2(a, list, s, g, opts, StlTWLotA);
        g_p5Verify = v, g_p5VerLend = vl;
        assert(k.stlVer[0] == ver0 + 1 && !k.stlActOff && !g_rpHoldAt);
        P6eUseFree();
    }
    // (v2) tripwire: a per-lot-only verify that differs turns acting into shadow for the session
    {
        k.stlMode = 1;
        const LONG w0 = g_spWrites, d0 = k.stlVer[1];
        StlTWLotA();
        StlVerify(1, w0, 1);
        assert(k.stlVer[1] == d0 + 1 && k.stlActOff == 1 && StlModeNow() == 2);
        const LONG tk = k.m2Taken;
        AsyncJob a = PcArmM2(seq++, 0);
        PcPlanM2(a, list, s, g, opts, StlTWLotA);  // acting is off: refused as the global rule
        assert(k.m2Taken == tk && !g_rpHoldAt);
        P6eUseFree();
        k.stlActOff = 0;
    }
    // (o1) off: no verdicts
    {
        k.stlMode = 0;
        const LONG j0 = k.stlJobs;
        PcRunM2(seq++, list, s, g, opts, false, 0);
        assert(k.stlJobs == j0 && StlModeNow() == 0);
    }
    g_stlOn = on0, k.stlReady = rd0, k.stlMode = m0, g_stlFindTramp = tr0, k.p6Plan = pl0;
    printf("StaleLot (docs/189 phase 2): ring + per-lot verdict (unread lot clean, read / active lot, world kinds, lot 0, unknown, "
           "in-flight, ring overflow, set cap, no set); classifier; read-set note; acting takes an unread-lot write, refuses world / "
           "unknown / no set; shadow acts as the global rule; verify per-lot-only equal; tripwire to shadow; off = no verdicts\n");
    TestStaleLotFp(list, s, g, opts, seq);
}

// --- [RouteSnap] StaleLotFp (docs/189 §9, counters v129): covered ring entries through the real SpNote wiring on fake
// objects (Qs, planner grid), both verdicts, the grid set, the tripwire, and the arms on the P5 fake world
static int g_stlFpRow[SL_N];
static void StlFpNote(int leaf, DWORD ecx, DWORD a1, DWORD a2, DWORD a3, DWORD a4) {
    DWORD st[8] = {ecx, 0, 0, a1, a2, a3, a4, 0};  // slot = &st[2]: [-2] = ecx, [1..4] args
    SpNote(g_stlFpRow[leaf], st + 2);
}
static void StlFpCovOnly() { StlTW(SLW_W6 | SLW_COV, 0); StlTW(SLW_FP | SLW_COV, 0); StlTW(SLW_GRID | SLW_COV, 0); }
static void StlFpWorld() { StlTW(SLW_GRID, 0); }
static void TestStaleLotFp(int* list, int* s, int* g, u8* opts, DWORD& seq) {
    static_assert(offsetof(Ts3nCounters, fplMode) == 0x50FC && offsetof(Ts3nCounters, fplCov) == 0x5104 &&
                  offsetof(Ts3nCounters, fplGrid) == 0x5114 && offsetof(Ts3nCounters, fplMat) == 0x5120 &&
                  offsetof(Ts3nCounters, fplVer) == 0x5130 && offsetof(Ts3nCounters, fplFb) == 0x513C &&
                  offsetof(Ts3nCounters, fplOff) == 0x5154 && offsetof(Ts3nCounters, fplSpare) == 0x5158 &&
                  sizeof(Ts3nCounters) == 5817 * 4, "counters v129 layout after reserved v126-v128 (docs/registry.md)");
    Ts3nCounters& k = ts3native_counters;
    for (int i = 0; i < SL_N; i++) g_stlFpRow[i] = -1;
    for (int i = 0; i < SN_N; i++) if (kSn[i].leaf < SL_N && g_stlFpRow[kSn[i].leaf] < 0) g_stlFpRow[kSn[i].leaf] = i;
    const LONG on0 = g_stlOn, spOn0 = g_spOn, fon0 = g_fplOn, rd0 = k.fplReady, fm0 = k.fplMode;
    const SpLotStrategy* lot0 = g_spLot;
    g_stlOn = 1, g_spOn = 1, g_spLot = &kSpLotNone, g_fplOn = 1, g_fplGridOff = 0, g_fplGridN = -1, g_fplGridP = 0;
    k.fplReady = 1;
    // fake Qs of lots A and B (key +0x18/+0x1C); a planner whose 2-cell grid holds one record (key 0x5555 as its complement)
    static DWORD qA[16], qB[16];
    qA[6] = 0x11112222, qA[7] = 0x77770000, qB[6] = 0x33334444, qB[7] = 0x77770000;  // kStlLotA, kStlLotB
    static u8 P[0x100], cells[64], rec[0x50];
    static DWORD bk[2];
    memset(P, 0, sizeof(P)), memset(cells, 0, sizeof(cells)), memset(rec, 0, sizeof(rec));
    *(u64*)rec = ~0x5555ull;
    bk[0] = 0, bk[1] = (DWORD)(uintptr_t)rec;
    *(DWORD*)(cells + 32 + 4) = (DWORD)(uintptr_t)bk, *(DWORD*)(cells + 32 + 8) = 2;
    *(u8**)(P + 0xDC) = cells, *(u8**)(P + 0xE0) = cells + 64;
    const DWORD pp = (DWORD)(uintptr_t)P, qa = (DWORD)(uintptr_t)qA, qb = (DWORD)(uintptr_t)qB;
    const DWORD aLo = 0x11112222, aHi = 0x77770000;  // kStlLotA
    int why, wk;
    const u64 rdA[1] = {kStlLotA}, rdB[1] = {kStlLotB}, rdAB[2] = {kStlLotA, kStlLotB};
    // (f1) a footprint update on lot A (FPCORE, W6 fills, remove-all on A = W2 on A's Q + a grid remove that finds nothing,
    // insert W1 on A's Q): a lot-B reader is clean with fp keys, stale (world) without; a lot-A reader is stale with both
    LONG w0 = g_spWrites;
    const LONG gr0 = k.fplGrid[0], c1 = k.fplCov[1], c0 = k.fplCov[0], rd = k.fplGridReads;
    StlFpNote(SL_FPCORE, 0, 0x1234, 0, 0, 0);
    StlFpNote(SL_W6, 0, 0, 0, 0, 0);
    StlFpNote(SL_LOTREMOVEFP, pp, aLo, aHi, 0x7777, 0);
    StlFpNote(SL_W2, qa, 0, 0, 0, 0);
    StlFpNote(SL_GRIDREMOVE, pp, aLo, aHi, 0x7777, 0);
    StlFpNote(SL_W6, 0, 0, 0, 0, 0);
    StlFpNote(SL_W1, qa, 0, 0, 0, 0);
    assert(k.fplGrid[0] == gr0 + 1 && k.fplCov[1] == c1 + 1 && k.fplCov[0] == c0 + 2 && k.fplGridReads == rd + 1 && k.fplGridRecs == 1);
    assert(!StlStaleOf(w0, g_spWrites, rdB, 1, false, true, 0, &why, &wk, true));
    assert(StlStaleOf(w0, g_spWrites, rdB, 1, false, true, 0, &why, &wk, false) && why == 1 && wk == SLW_FP);
    assert(StlStaleOf(w0, g_spWrites, rdA, 1, false, true, 0, &why, &wk, true) && why == 0);
    // (f2) a footprint in both lots' Qs (W1 on A and on B): stale for a reader of either lot, clean for a reader of neither
    w0 = g_spWrites;
    StlFpNote(SL_W1, qa, 0, 0, 0, 0), StlFpNote(SL_W1, qb, 0, 0, 0, 0);
    assert(StlStaleOf(w0, g_spWrites, rdA, 1, false, true, 0, &why, &wk, true) && why == 0);
    assert(StlStaleOf(w0, g_spWrites, rdB, 1, false, true, 0, &why, &wk, true) && why == 0);
    const u64 rdC[1] = {0x1234ull};
    assert(!StlStaleOf(w0, g_spWrites, rdC, 1, false, true, 0, &why, &wk, true));
    // (f3) a world-level footprint (no lot: remove-all on lot 0; grid add): stale for every reader with fp keys too
    w0 = g_spWrites;
    StlFpNote(SL_LOTREMOVEFP, pp, 0, 0, 0x7777, 0);
    assert(StlStaleOf(w0, g_spWrites, rdAB, 2, false, true, 0, &why, &wk, true) && why == 1 && wk == SLW_LOT0);
    w0 = g_spWrites;
    StlFpNote(SL_GRIDADD, pp, 0, 0, 0, 0);
    assert(StlStaleOf(w0, g_spWrites, rdAB, 2, false, true, 0, &why, &wk, true) && why == 1 && wk == SLW_GRID && g_fplGridN == -1);
    // (f4) a W6 polygon refill alone: covered (clean with fp keys, world-level without)
    w0 = g_spWrites;
    StlFpNote(SL_W6, 0, 0, 0, 0, 0);
    assert(!StlStaleOf(w0, g_spWrites, rdA, 1, false, true, kStlLotA, &why, &wk, true));
    assert(StlStaleOf(w0, g_spWrites, rdA, 1, false, true, 0, &why, &wk, false) && wk == SLW_W6);
    // (f5) a grid remove whose key a record holds (as its complement): real, world-level; the set is re-read next time
    w0 = g_spWrites;
    const LONG gr1 = k.fplGrid[1];
    StlFpNote(SL_GRIDREMOVE, pp, aLo, aHi, 0x5555, 0);
    assert(k.fplGrid[1] == gr1 + 1 && g_fplGridN == -1);
    assert(StlStaleOf(w0, g_spWrites, rdB, 1, false, true, 0, &why, &wk, true) && wk == SLW_GRID);
    // (f6) mapping failures -> global: no planner, an unreadable grid, a set over its cap
    const LONG fb1 = k.fplFb[1], fb0 = k.fplFb[0], gr2 = k.fplGrid[2];
    w0 = g_spWrites;
    StlFpNote(SL_GRIDREMOVE, 0, aLo, aHi, 0x7777, 0);
    static u8 badP[0x100];
    *(DWORD*)(badP + 0xDC) = 0x10, *(DWORD*)(badP + 0xE0) = 0x50;  // cells at an unreadable address
    StlFpNote(SL_GRIDREMOVE, (DWORD)(uintptr_t)badP, aLo, aHi, 0x7777, 0);
    assert(k.fplFb[1] == fb1 + 2 && k.fplGrid[2] == gr2 + 2);
    assert(StlStaleOf(w0, g_spWrites, rdB, 1, false, true, 0, &why, &wk, true) && wk == SLW_GRID);
    g_fplGridN = -2, g_fplGridP = pp;
    StlFpNote(SL_GRIDREMOVE, pp, aLo, aHi, 0x7777, 0);
    assert(k.fplFb[0] == fb0 + 1);
    StlFpNote(SL_GRIDADD, pp, 0, 0, 0, 0);  // an add resets the over-cap state: the next remove re-reads
    assert(g_fplGridN == -1);
    // (f7) tripwire: a no-op-judged remove that reaches 0x670510 turns the grid check off; later removes are world-level
    StlFpNote(SL_GRIDREMOVE, pp, aLo, aHi, 0x7777, 0);
    assert(t_fplNoop);
    const LONG tr = k.fplGridTrip;
    StlFpNote(SL_MARKREMOVE, 0, 0x7777, 0, 0, 0);
    assert(k.fplGridTrip == tr + 1 && g_fplGridOff == 1);
    w0 = g_spWrites;
    StlFpNote(SL_GRIDREMOVE, pp, aLo, aHi, 0x7777, 0);
    assert(StlStaleOf(w0, g_spWrites, rdB, 1, false, true, 0, &why, &wk, true) && wk == SLW_GRID);
    g_fplGridOff = 0;
    StlFpNote(SL_W6, 0, 0, 0, 0, 0);
    assert(!t_fplNoop);  // any other leaf clears the no-op mark
    // (p1) P5 fake world, acting: covered-only writes after the cut are taken with fp keys, refused without
    void* const tr0 = g_stlFindTramp;
    const LONG srd0 = k.stlReady, m0 = k.stlMode, pl0 = k.p6Plan;
    k.stlReady = 3, g_stlFindTramp = (void*)1, k.p6Plan = 1, k.p6Early = 0, k.stlMode = 1, k.fplOff = 0;
    {
        k.fplMode = 1;
        const LONG tk = k.m2Taken, at = k.stlActTaken, fm = k.fplMat[1];
        AsyncJob a = PcArmM2(seq++, 0);
        PcPlanM2(a, list, s, g, opts, StlFpCovOnly);
        assert(k.m2Taken == tk + 1 && k.stlActTaken == at + 1 && k.fplMat[1] == fm + 1 && !g_rpHoldAt);
        P6eUseFree();
    }
    {
        k.fplMode = 0;
        const LONG tk = k.m2Taken;
        AsyncJob a = PcArmM2(seq++, 0);
        PcPlanM2(a, list, s, g, opts, StlFpCovOnly);
        assert(k.m2Taken == tk && !g_rpHoldAt);
        P6eUseFree();
        k.fplMode = 1;
        AsyncJob b = PcArmM2(seq++, 0);  // fp keys on, a real world-level write: refused
        PcPlanM2(b, list, s, g, opts, StlFpWorld);
        assert(k.m2Taken == tk && !g_rpHoldAt);
        P6eUseFree();
    }
    // (p2) an fp-only verify that differs: fp keys off for the session (acting off too, as the per-lot-only tripwire)
    {
        StlModeNow();
        const LONG w1 = g_spWrites, d0 = k.fplVer[1];
        StlFpCovOnly();
        StlVerify(1, w1, 1);
        assert(k.fplVer[1] == d0 + 1 && k.fplOff == 1);
        StlModeNow();
        assert(!t_fpl);
        k.fplOff = 0, k.stlActOff = 0;
    }
    k.stlReady = srd0, k.stlMode = m0, g_stlFindTramp = tr0, k.p6Plan = pl0;
    g_stlOn = on0, g_spOn = spOn0, g_spLot = lot0, g_fplOn = fon0, k.fplReady = rd0, k.fplMode = fm0, g_fplGridN = -1, g_fplGridP = 0;
    printf("StaleLotFp (docs/189 §9): footprint on lot A clean for a lot-B reader, stale for A; footprint in A and B stales both; "
           "world-level footprint (lot 0, grid add) stales everyone; W6 refill covered; real grid remove world-level; no planner / "
           "bad grid / set cap / tripwire -> global; acting takes covered-only windows with fp keys, refuses without; fp-only "
           "verify mismatch -> fpl off\n");
}

