// docs/644 plancand.cpp: the scope rule, the call-site gate, the water goal check on hand-made grids (pure, plancand-rule.h),
// WgcHook on a fake water map with a fake next (modes off / reject / shadow, out vector, counters), and the split frames
// (self time, nesting, unwound frames). Included by selftest.cpp.
static int g_wgcFakeRuns;
static u8 g_wgcFakeAnswer;
static DWORD __fastcall WgcFakeNext(void*, void*, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD out) {
    g_wgcFakeRuns++;
    DWORD* v = (DWORD*)(uintptr_t)out;
    v[1] = v[0];
    return g_wgcFakeAnswer;
}

static void TestPlanCandRules() {
    // scope: only unseen (2) and seen NPC (3); unknown (0) and household-or-seen (1) never
    assert(!PccScopeCuttable(0) && !PccScopeCuttable(1) && PccScopeCuttable(2) && PccScopeCuttable(3) && !PccScopeCuttable(4));
    assert(PccRule(1, true, true, 2) == PCC_CUT && PccRule(1, true, true, 3) == PCC_CUT);
    assert(PccRule(1, true, true, 0) == PCC_EXEMPT && PccRule(1, true, true, 1) == PCC_EXEMPT);
    assert(PccRule(0, true, true, 2) == PCC_PASS && PccRule(1, false, true, 2) == PCC_PASS && PccRule(1, true, false, 2) == PCC_PASS);
    const uint32_t rets[4] = {0x65F736, 0x65FA13, 0, 0};
    assert(PccRetIn(0x65F736, rets, 4) && PccRetIn(0x65FA13, rets, 4) && !PccRetIn(0x65FB5A, rets, 4) && !PccRetIn(0, rets, 4));
    assert(WgcTier(0x4100) == 2 && WgcTier(0x4200) == 3 && WgcTier(0x4300 | 0x30FF) == 4 && WgcTier(0xD00) == 1 && WgcTier(0x4600) == 1);
    assert(WgcTier(0x100) == 0 && WgcTier(0x4400) == 0);
}

// 8 x 4 grid, cell index = y * 8 + x (body width = grid width); body 1 everywhere; tier 2 grid: water except column 5
struct WgcFake {
    u8 body[64];
    u8 g0[32], g2[32];
    const u8* grid[5];
};
static WgcView WgcFakeView(WgcFake& f, uint32_t flags) {
    memset(f.body, 0, sizeof(f.body));
    for (int i = 0; i < 32; i++) f.body[i * 2] = 1, f.g0[i] = 1, f.g2[i] = (i % 8) != 5;
    f.grid[0] = f.g0, f.grid[1] = f.g0, f.grid[2] = f.g2, f.grid[3] = f.g2, f.grid[4] = f.g2;
    WgcView v = {31, f.body, 8, 8, 4, f.grid, flags};
    return v;
}

static void TestWaterGoalRule() {
    WgcFake f;
    WgcView v = WgcFakeView(f, 0x4100);  // tier 2
    assert(WgcWouldReject(v, 0, 5));     // water start, land goal (column 5): the search cannot reach it
    assert(!WgcWouldReject(v, 0, 6));    // water goal
    assert(!WgcWouldReject(v, 5, 5));    // start == goal: found at once
    assert(!WgcWouldReject(v, 5, 13));   // start on land in tiers 2-4 -> tier 1 (all water) decides: goal water
    v.routeFlags = 0x100;                // tier 0 grid: all water, so no reject
    assert(!WgcWouldReject(v, 0, 5));
    v.routeFlags = 0x4100;
    f.body[5 * 2] = 2;                   // another water body: the game's own exit, not ours
    assert(!WgcWouldReject(v, 0, 5));
    f.body[5 * 2] = 1;
    assert(!WgcWouldReject(v, 0, 32) && !WgcWouldReject(v, 40, 5));  // past +0x0C: the game's own exit
    // the tier walk: tier 4 start cell on land, tier 3 water -> tier 3 decides
    u8 g4[32];
    memset(g4, 0, sizeof(g4));
    f.grid[4] = g4;
    v.routeFlags = 0x4300;
    assert(WgcWouldReject(v, 0, 5) && !WgcWouldReject(v, 0, 6));
    // start on land in the decided grid (tier 0 reached, its cell land): never rejected (one-step land moves)
    f.g0[0] = 0;
    f.grid[1] = f.grid[2] = f.grid[3] = f.grid[4] = g4;
    assert(!WgcWouldReject(v, 0, 5));
}

static void TestWaterGoalHook() {
    PlanCandCounters& c = ts3native_plancand;
    const PlanCandCounters saved = c;
    void* const savedNext = g_wgcNext;
    g_wgcNext = (void*)WgcFakeNext;
    // fake water map: +0x04 body width, +0x0C last cell, +0x1C body, +0x7A0/+0x7A4 grid size, +0x7B0 descriptors
    static u8 map[0x800];
    static u8 desc[5 * 16];
    WgcFake f;
    WgcFakeView(f, 0x4100);
    memset(map, 0, sizeof(map));
    *(DWORD*)(map + 0x04) = 8, *(DWORD*)(map + 0x0C) = 31, *(const u8**)(map + 0x1C) = f.body;
    *(DWORD*)(map + 0x7A0) = 8, *(DWORD*)(map + 0x7A4) = 4, *(u8**)(map + 0x7B0) = desc;
    for (int i = 0; i < 5; i++) *(const u8**)(desc + i * 16) = f.grid[i];
    DWORD flags = 0x4100;
    DWORD* pflags = &flags;
    static u8 route[0x100];
    *(DWORD**)(route + 0x8C) = pflags;
    DWORD out[4] = {0x1000, 0x1010, 0x1020, 0};
    const DWORD r0 = (DWORD)(uintptr_t)route, o = (DWORD)(uintptr_t)out;
    g_wgcFakeRuns = 0;
    c.wgcMode = 0, c.wgcReject = c.wgcMismatch = 0;
    g_wgcFakeAnswer = 0;
    assert(WgcHook(map, NULL, 0, r0, 0, 0, 0, 5, 0, 0, o) == 0 && g_wgcFakeRuns == 1 && c.wgcReject == 0);  // off: the search runs
    c.wgcMode = 1;
    out[1] = 0x1010;
    assert(WgcHook(map, NULL, 0, r0, 0, 0, 0, 5, 0, 0, o) == 0 && g_wgcFakeRuns == 1 && c.wgcReject == 1 && out[1] == out[0]);
    g_wgcFakeAnswer = 1;
    assert(WgcHook(map, NULL, 0, r0, 0, 0, 0, 6, 0, 0, o) == 1 && g_wgcFakeRuns == 2 && c.wgcReject == 1);  // water goal: runs
    c.wgcMode = 2;  // shadow: runs, counts; a found path on a would-reject is a mismatch
    assert(WgcHook(map, NULL, 0, r0, 0, 0, 0, 5, 0, 0, o) == 1 && g_wgcFakeRuns == 3 && c.wgcReject == 2 && c.wgcMismatch == 1);
    g_wgcFakeAnswer = 0;
    assert(WgcHook(map, NULL, 0, r0, 0, 0, 0, 5, 0, 0, o) == 0 && g_wgcFakeRuns == 4 && c.wgcReject == 3 && c.wgcMismatch == 1);
    assert(WgcHook(map, NULL, 0, 0, 0, 0, 0, 5, 0, 0, o) == 0 && g_wgcFakeRuns == 5 && c.wgcReject == 3);  // no route: runs
    g_wgcNext = savedNext;
    c = saved;
}

static void TestCandidateSplit() {
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
    const bool saved = g_pccSplit;
    const LONG savedLog = g_pccLogUs, savedPh = g_pccPh;
    g_pccSplit = true;
    g_pccLogUs = 0x7FFFFFFF;  // no line
    PccTh& t = t_pcc;
    t.n = t.ph = 0;
    char frame[64];  // marks: lower address = deeper frame
    assert(!PccEnter(PCC_CLOSEST, frame + 40));  // outside a planner call: untimed
    assert(PccEnter(PCC_PH, frame + 60) && t.ph == 1);
    assert(PccEnter(PCC_CHOOSE, frame + 40));
    assert(PccEnter(PCC_CLOSEST, frame + 20));
    Sleep(2);
    PccLeave(frame + 20, 0);
    assert(t.calls[PCC_CLOSEST] == 1 && t.n == 2);
    PccLeave(frame + 40, 0);
    assert(t.calls[PCC_CHOOSE] == 1 && t.self[PCC_CHOOSE] < t.self[PCC_CLOSEST]);  // the nested time is the inner frame's
    // an unwound frame (no leave) is dropped by the next entry at its level or above
    assert(PccEnter(PCC_GATHER, frame + 40));
    assert(PccEnter(PCC_SAMELAND, frame + 40) && t.n == 2 && t.f[1].site == PCC_SAMELAND);
    PccLeave(frame + 40, 0);
    assert(t.calls[PCC_GATHER] == 0 && t.calls[PCC_SAMELAND] == 1);
    PccLeave(frame + 60, 5);
    assert(t.ph == 0 && t.n == 0 && g_pccPh == savedPh + 1);
    // docs/644 §8: a lot leg's inclusive time goes to its call-site slot, the cell A* under it to cell's self time; every
    // call >= the log setting is kept in the top list (8 at most), which the counter flush empties
    g_pccLogUs = 1;
    g_pccTopN = 0;
    for (int k = 0; k < 10; k++) {
        assert(PccEnter(PCC_PH, frame + 60));
        assert(PccEnter(PCC_LOTPLAN, frame + 40, 1));
        assert(PccEnter(PCC_CELL, frame + 20));
        Sleep(1);
        PccLeave(frame + 20, 0);
        PccLeave(frame + 40, 0);
        assert(t.lpN[1] == 1 && t.calls[PCC_CELL] == 1 && t.lp[1] >= t.self[PCC_CELL] && t.self[PCC_LOTPLAN] < t.lp[1]);
        PccLeave(frame + 60, 5);
        assert(g_pccTopN == (k < PCC_TOPN ? k + 1 : PCC_TOPN));
    }
    for (int i = 0; i < g_pccTopN; i++) assert(g_pccTop[i].lpN[1] == 1 && g_pccTop[i].calls[PCC_CELL] == 1 && g_pccTop[i].code == 5);
    PccTopFlush();
    assert(g_pccTopN == 0);
    // docs/663 §3: world / water / post / insert are timed sites of their own (self time, nested under the planner frame)
    g_pccLogUs = 0x7FFFFFFF;
    assert(PccEnter(PCC_PH, frame + 60));
    const int extra[4] = {PCC_WORLD, PCC_WATER, PCC_POST, PCC_INSERT};
    for (int i = 0; i < 4; i++) {
        assert(PccEnter(extra[i], frame + 40));
        PccLeave(frame + 40, 0);
        assert(t.calls[extra[i]] == 1);
    }
    PccLeave(frame + 60, 0);
    {
        char b[700];
        LONG zu[PCC_N] = {0}, zn[PCC_N] = {0}, lu[PCC_LPN + 1] = {0}, ln[PCC_LPN + 1] = {0};
        PccFmt(b, sizeof(b), zu, zn, lu, ln);
        assert(strstr(b, "world 0/0") && strstr(b, "water 0/0") && strstr(b, "post 0/0") && strstr(b, "insert 0/0"));
        assert(strstr(b, "insert 0/0, host 0/0"));  // docs/667 §2: the last column
    }
    // docs/667 §2: a managed call under lotSearch is the "host" frame: its time leaves lotSearch's self time; outside a
    // planner call the host-call hook times nothing
    assert(!PccHostEnter(frame + 10));
    assert(PccEnter(PCC_PH, frame + 60) && PccEnter(PCC_LOTSEARCH, frame + 40) && PccHostEnter(frame + 20));
    Sleep(2);
    PccHostLeave(frame + 20);
    PccLeave(frame + 40, 0);
    assert(t.calls[PCC_HOST] == 1 && t.calls[PCC_LOTSEARCH] == 1 && t.self[PCC_HOST] > t.self[PCC_LOTSEARCH]);
    PccLeave(frame + 60, 0);
    assert(kPccSites[PCC_HOST].at == 0 && !kPccHooks[PCC_HOST] && !PccOwnerAt(0));
    g_pccSplit = saved, g_pccLogUs = savedLog;
}

static void TestPlanCand() {
    TestPlanCandRules();
    TestWaterGoalRule();
    TestWaterGoalHook();
    TestCandidateSplit();
    printf("plan cand: OK (scope rule, call sites, water goal check, split frames, host frame)\n");
}
