// --- [RouteAsync] PlanDeadlineMs / FailMemo (plandeadline.cpp, docs/164 §7): layout; the entry stubs on dummy cell A* and water
// functions with the game's prologue bytes (pass through outside a session, before the deadline, on another thread, with the
// switch off; past it the game's no-path exit: fields, AL, stack); sessions by reason (own task exempt), the non-IC session and
// its world A* cap through CallPlanBudget and the cost stub; the IC path through SyncIc; FailMemo on the sync Plan IC.
// Included by selftest.cpp after replandedupe-test.h (its route fakes); runs after TestRouteBudget (g_cost = a patched stub).
static volatile LONG g_pdtRuns[2];
static u8* g_pdtCell;
// calls a dummy (thiscall, n stack args, callee cleanup); returns AL, *bad = esp did not come back
static u8 PdtCall(const void* fn, void* self, int n, const DWORD* args, bool* bad) {
    DWORD before = 0, after = 0, res = 0;
    __asm {
        mov before, esp
        mov ecx, n
        mov edx, args
    next:
        test ecx, ecx
        jz go
        push dword ptr [edx + ecx * 4 - 4]
        dec ecx
        jmp next
    go:
        mov ecx, self
        call fn
        mov res, eax
        mov after, esp
        mov esp, before  // a wrong ret N must not wreck this frame
    }
    *bad = before != after;
    return (u8)res;
}
// a fake route IC: waits past the deadline, then one cell A* call through the stub; result type 16 (failed) unless it searched
static u8* __cdecl PdtIc(u8* out, DWORD) {
    Sleep(3);
    u8 self[0x40] = {};
    const DWORD a[6] = {};
    bool bad;
    const u8 r = PdtCall(g_pdtCell, self, 6, a, &bad);
    assert(!bad);
    memset(out, 0, 0x48);
    *(DWORD*)out = r ? 1 : 16;
    return out;
}

// docs/197 §7 KdCutCamRecheck: a writer publishing all-equal snapshots while the reader looks for a torn one
static volatile LONG g_kdCamStop;
static DWORD WINAPI KdCamWriter(void*) {
    for (float v = 1; !g_kdCamStop; v += 1) {
        const float p[3] = {v, v, v};
        KdCamPublish(p, p, v, (LONGLONG)v);
    }
    return 0;
}
static void KdCamTest() {
    Ts3nCounters& k = ts3native_counters;
    const LONG scan0 = g_kdCamSeq, mode0 = k.kdCamMode, ceil0 = k.kdNpcCeilUs;
    const KdCamSnap save0 = g_kdCam;
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
    // reach math: reach + 3 m from either camera point, xz only, wide = all seen
    const float cam[3] = {100, 50, 100}, tgt[3] = {200, 0, 200};
    KdCamSnap c = {};
    memcpy(c.pos, cam, 12), memcpy(c.tgt, tgt, 12), c.reach = 100;
    float p[2][2] = {{100 + 102, 100}, {1000, 1000}};
    assert(KdCamInView(c, p, 1) && !KdCamInView(c, p + 1, 1) && KdCamInView(c, p, 2));
    p[0][0] = 100 + 104, p[0][1] = 100 - 104;  // 147 m from the camera, 300 m from the target
    assert(!KdCamInView(c, p, 1));
    p[0][0] = 200 + 102, p[0][1] = 200;  // the target's reach counts too
    assert(KdCamInView(c, p, 1));
    p[0][0] = 1000, p[1][0] = 1000;
    p[0][1] = 1000;
    assert(!KdCamInView(c, p, 2));
    c.reach = 240;
    assert(KdCamInView(c, p, 2));
    // nothing published, a writer inside: no read
    g_kdCamSeq = 0;
    KdCamSnap r;
    assert(!KdCamRead(&r));
    g_kdCamSeq = 7;
    assert(!KdCamRead(&r));
    KdCamPublish(cam, tgt, 80, 5);  // a busy writer's publish is dropped
    assert(g_kdCamSeq == 7);
    // torn reads: every accepted snapshot is whole
    g_kdCamSeq = 0, g_kdCamStop = 0;
    HANDLE w = CreateThread(NULL, 0, KdCamWriter, NULL, 0, NULL);
    assert(w);
    LONG ok = 0;
    for (int i = 0; i < 2000000; i++)
        if (KdCamRead(&r)) {
            ok++;
            assert(r.pos[0] == r.pos[1] && r.pos[0] == r.tgt[2] && r.pos[0] == r.reach && (LONGLONG)r.reach == r.at);
        }
    g_kdCamStop = 1;
    WaitForSingleObject(w, INFINITE);
    CloseHandle(w);
    assert(ok > 0);
    // arming: only scope 2 with a deadline; household, unknown, seen NPC never
    const LONGLONG fb0 = t_fbDeadline, t00 = t_jobT0;
    const LONG cap0 = t_fbCapUs, sc0 = t_kdJobScope, ex0 = t_kdExemptUs;
    LARGE_INTEGER q;
    QueryPerformanceCounter(&q);
    t_fbDeadline = q.QuadPart, t_jobT0 = q.QuadPart - g_qpf.QuadPart / 4, t_fbCapUs = 250000;
    for (LONG s = 0; s < 4; s++) {
        t_kdJobScope = s;
        KdCamJobArm();
        assert(t_kdCam == (s == 2 ? 1 : 0));
        assert(s == 2 || !KdCamHold(q.QuadPart, &t_fbDeadline));  // not armed: the cut stands
    }
    t_kdJobScope = 2;
    const int noStarts[2] = {0, 0};
    u8 rec[2][64] = {};
    *(float*)rec[0] = 100, *(float*)(rec[0] + 8) = 100;
    *(float*)rec[1] = 5000, *(float*)(rec[1] + 8) = 5000;
    int startV[2] = {(int)(uintptr_t)rec[1], (int)(uintptr_t)(rec[1] + 64)};  // start far
    int goalV[2] = {(int)(uintptr_t)rec[0], (int)(uintptr_t)(rec[0] + 64)};   // goal in view
    const auto reset = [&](LONG mode) {
        k.kdCamMode = mode, k.kdCamChecks = k.kdCamHeld = k.kdCamStale = k.kdCamWould = 0, k.kdNpcCeilUs = 2000000;
        t_fbDeadline = q.QuadPart, t_fbCapUs = 250000, t_kdExemptUs = 0;
        KdCamJobArm();
    };
    g_kdCamSeq = 0;
    KdCamPublish(cam, tgt, 80, q.QuadPart);
    // no plan yet: the switch holds without a verdict, off cuts as ever
    reset(1);
    LONGLONG dl = q.QuadPart;
    assert(KdCamHold(q.QuadPart, &dl) && t_kdCam == 1 && !k.kdCamChecks);
    reset(0);
    assert(!KdCamHold(q.QuadPart, &dl) && !t_kdCam && !k.kdCamChecks);
    // switch off, in view: the cut stands, counted
    reset(0);
    KdCamPlanArgs(startV, goalV);
    assert(t_kdCamN == 2 && !KdCamHold(q.QuadPart, &dl) && k.kdCamChecks == 1 && k.kdCamWould == 1 && !k.kdCamHeld && t_fbDeadline == q.QuadPart && t_kdCamNote == 3);
    // on, in view: held to the NPC ceiling from the job's start, every deadline moved, no second test
    reset(1);
    KdCamPlanArgs(startV, goalV);
    dl = q.QuadPart;
    assert(KdCamHold(q.QuadPart, &dl) && k.kdCamHeld == 1 && t_kdCam == 2 && t_kdExemptUs == 250000 && t_fbCapUs == 2000000);
    const LONGLONG want = t_jobT0 + 2 * g_qpf.QuadPart;
    assert(dl == want && t_fbDeadline == want && t_kdCamDl == want);
    LONGLONG outer = q.QuadPart;  // an outer plan's Budget still has the old deadline
    assert(KdCamHold(q.QuadPart, &outer) && outer == want && k.kdCamChecks == 1);
    assert(!KdCamHold(want, &outer) && t_kdCam == 2);  // past the ceiling: cut
    // BudgetStep goes through it: a held plan is not capped
    reset(1);
    KdCamPlanArgs(startV, goalV);
    const Budget b0 = t_budget;
    const Budget bd = {1, 0, {}, 0, 0, 0, q.QuadPart, 0};
    t_budget = bd;
    assert(BudgetStep(1) == 0 && !t_budget.capped && t_budget.dl == want);
    t_budget = b0;
    // on, out of view: cut exactly as before, nothing moved
    reset(1);
    goalV[0] = (int)(uintptr_t)rec[1], goalV[1] = (int)(uintptr_t)(rec[1] + 64);
    KdCamPlanArgs(startV, goalV);
    dl = q.QuadPart;
    assert(!KdCamHold(q.QuadPart, &dl) && dl == q.QuadPart && t_fbDeadline == q.QuadPart && !t_kdExemptUs && t_fbCapUs == 250000 && !t_kdCam && !t_kdCamNote && k.kdCamChecks == 1 && !k.kdCamHeld);
    // the start in view is enough
    reset(1);
    KdCamPlanArgs(goalV, noStarts);
    t_kdCamP[0][0] = 100, t_kdCamP[0][1] = 100;
    assert(t_kdCamN == 1 && KdCamHold(q.QuadPart, &dl) && k.kdCamHeld == 1);
    // a stale snapshot, or none: in view on, cut off
    KdCamPublish(cam, tgt, 80, q.QuadPart - 2 * g_qpf.QuadPart);
    reset(1);
    KdCamPlanArgs(startV, goalV);
    assert(KdCamHold(q.QuadPart, &dl) && k.kdCamStale == 1 && !k.kdCamHeld && t_kdCamNote == 2);
    reset(0);
    KdCamPlanArgs(startV, goalV);
    assert(!KdCamHold(q.QuadPart, &dl) && k.kdCamStale == 1 && !k.kdCamWould);
    g_kdCamSeq = 0;
    reset(1);
    KdCamPlanArgs(startV, goalV);
    assert(KdCamHold(q.QuadPart, &dl) && k.kdCamStale == 1);
    // a sync session: armed for scope 2 only, PdSkip holds it, its session reads as a ceiling plan
    {
        const LONG pm0 = k.pdMode, ps0 = k.pdSessions[0], ex1 = k.kdExempt;
        k.pdMode = 1, k.kdCamMode = 1, k.kdNpcCeilUs = 2000000, t_fbDeadline = 0;
        g_unCam.ok = true;
        QueryPerformanceCounter(&g_unCam.at);
        KdCamPublish(cam, tgt, 80, g_unCam.at.QuadPart);
        goalV[0] = (int)(uintptr_t)rec[0], goalV[1] = (int)(uintptr_t)(rec[0] + 64);
        KdScopeSet(0x1234, 1);
        PdOpen(0, 50);
        KdCamSyncArm(0x1234);
        assert(!t_kdCam);  // household side: never armed
        PdClose(true);
        KdScopeSet(0x1234, 2);
        PdOpen(0, 50);
        KdCamSyncArm(0x1235);
        assert(!t_kdCam);  // another route's answer
        t_pd.handle = 0x1234;
        KdCamSyncArm(0x1234);
        assert(t_kdCam == 1);
        KdCamPlanArgs(startV, goalV);
        const LONGLONG t0 = t_pd.t0;
        Sleep(1);
        assert(!PdSkip(0) && t_kdCam == 2 && t_pd.ceil && t_pd.dl == t0 + 2 * g_qpf.QuadPart && t_fbDeadline == t_pd.dl && k.kdCamHeld >= 1 && t_pd.wouldUs == 50);
        PdClose(true);
        assert(!t_kdCam && !t_pd.open && !t_kdCamNote);
        KdScopeClear(0x1234);
        k.pdMode = pm0, k.pdSessions[0] = ps0, k.kdExempt = ex1, t_kdNpc = false, g_unCam.ok = false, g_unCam.at.QuadPart = 0;
    }
    t_kdCam = t_kdCamN = t_kdCamNote = 0;
    t_fbDeadline = fb0, t_jobT0 = t00, t_fbCapUs = cap0, t_kdJobScope = sc0, t_kdExemptUs = ex0;
    k.kdCamMode = mode0, k.kdNpcCeilUs = ceil0, k.kdCamChecks = k.kdCamHeld = k.kdCamStale = k.kdCamWould = 0;
    g_kdCamSeq = scan0, g_kdCam = save0;
}

// docs/178 T1: one cell and one water call on a fresh thread acting as the route worker inside a foreign job (g_pdWkTid = its
// id, its own t_fbDeadline = dl, t_jobT0 set); what came back and what the job's thread-locals say afterwards
struct PdtWk {
    u8 *cell, *water;
    LONGLONG dl;
    u8 rc, rw;
    bool bad;
    LONG cut[3], runs;
    LONGLONG capAt;
    DWORD self24, vecBegin, vecEnd;
};
static DWORD WINAPI PdtWorker(LPVOID p) {
    PdtWk& w = *(PdtWk*)p;
    InterlockedExchange((volatile LONG*)&g_pdWkTid, (LONG)GetCurrentThreadId());
    t_fbDeadline = w.dl, t_jobT0 = 1, t_jobCapAt = 0;
    t_fbjdCut[0] = t_fbjdCut[1] = t_fbjdCut[2] = 0;
    u8 self[0x40];
    memset(self, 0x77, sizeof(self));
    DWORD vec[3] = {0x1000, 0x1040, 0x1080};
    DWORD ca[6] = {}, wa[9] = {};
    wa[8] = (DWORD)(uintptr_t)vec;
    const LONG r0 = g_pdtRuns[0] + g_pdtRuns[1];
    bool b1, b2;
    w.rc = PdtCall(w.cell, self, 6, ca, &b1);
    w.rw = PdtCall(w.water, self, 9, wa, &b2);
    w.runs = g_pdtRuns[0] + g_pdtRuns[1] - r0;
    w.bad = b1 || b2;
    w.self24 = *(DWORD*)(self + 0x24);
    w.vecBegin = vec[0], w.vecEnd = vec[1];
    memcpy(w.cut, t_fbjdCut, sizeof(w.cut));
    w.capAt = t_jobCapAt;
    InterlockedExchange((volatile LONG*)&g_pdWkTid, 0);
    t_fbDeadline = t_jobT0 = t_jobCapAt = 0;
    return 0;
}
static void PdtRunWorker(PdtWk& w) {
    HANDLE h = CreateThread(NULL, 0, PdtWorker, &w, 0, NULL);
    assert(h);
    WaitForSingleObject(h, INFINITE);
    CloseHandle(h);
}

static void TestPlanDeadline() {
    static_assert(offsetof(Ts3nCounters, fbjdMode) == 0x440C && offsetof(Ts3nCounters, fbjdCapped) == 0x441C &&
                      offsetof(Ts3nCounters, fbjdLines) == 0x4438 && offsetof(Ts3nCounters, owReady) == 0x443C &&
                      offsetof(Ts3nCounters, owWaitHist) == 0x444C && offsetof(Ts3nCounters, gsReady) == 0x4468 &&
                      offsetof(Ts3nCounters, gsMaxUs) == 0x44AC && offsetof(Ts3nCounters, gsLong) == 0x44D0,
                  "counters v103 layout after v102 (docs/registry.md)");
    static_assert(offsetof(Ts3nCounters, pdMode) == 0x4244 && offsetof(Ts3nCounters, pdSessions) == 0x4254 &&
                      offsetof(Ts3nCounters, pdLines) == 0x427C && offsetof(Ts3nCounters, fmMode) == 0x4280 &&
                      offsetof(Ts3nCounters, fmInval) == 0x4298 && offsetof(Ts3nCounters, fmServedUs) == 0x42C4 &&
                      sizeof(Ts3nCounters) == 6314 * 4,
                  "counters v99 layout after v98 (docs/registry.md)");
    Ts3nCounters& k = ts3native_counters;
    assert(k.version == 912);
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
    memset(&k.pdMode, 0, sizeof(Ts3nCounters) - offsetof(Ts3nCounters, pdMode));
    u8* code = (u8*)VirtualAlloc(NULL, 4096, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
    assert(code);
    // cell A*: the game's prologue (push ebp; mov ebp,esp; and esp,-16), inc [runs0], mov esp,ebp; pop ebp; mov al,1; ret 18h
    u8* cell = code;
    static const u8 kC[] = {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF0, 0xFF, 0x05, 0, 0, 0, 0, 0x8B, 0xE5, 0x5D, 0xB0, 0x01, 0xC2, 0x18, 0x00};
    memcpy(cell, kC, sizeof(kC));
    *(volatile LONG**)(cell + 8) = &g_pdtRuns[0];
    // water: the game's prologue (mov eax,[esp+24h]; sub esp,50h), inc [runs1], add esp,50h; mov al,1; ret 24h
    u8* water = code + 64;
    static const u8 kW[] = {0x8B, 0x44, 0x24, 0x24, 0x83, 0xEC, 0x50, 0xFF, 0x05, 0, 0, 0, 0, 0x83, 0xC4, 0x50, 0xB0, 0x01, 0xC2, 0x24, 0x00};
    memcpy(water, kW, sizeof(kW));
    *(volatile LONG**)(water + 9) = &g_pdtRuns[1];
    g_pdtCell = cell;
    const LONG ready0 = k.budgetReady;
    k.budgetReady = 1;
    PdInstallAt((DWORD)(uintptr_t)cell, (DWORD)(uintptr_t)water);
    assert(g_pdOn && g_pdOrig[0] && g_pdOrig[1] && k.pdReady == 7);
    DWORD simTid = GetCurrentThreadId();
    const DWORD* savedSim = g_rtSimTidSlot;
    g_rtSimTidSlot = &simTid;
    u8 self[0x40];
    DWORD vec[3] = {0x1000, 0x1040, 0x1080};  // begin, end, capacity
    DWORD ca[6] = {}, wa[9] = {};
    wa[8] = (DWORD)(uintptr_t)vec;
    bool bad;
    auto both = [&](u8 want, LONG runs) {
        memset(self, 0x77, sizeof(self));
        vec[1] = 0x1040;
        const LONG r0 = g_pdtRuns[0], r1 = g_pdtRuns[1];
        assert(PdtCall(cell, self, 6, ca, &bad) == want && !bad && g_pdtRuns[0] == r0 + runs);
        assert(PdtCall(water, self, 9, wa, &bad) == want && !bad && g_pdtRuns[1] == r1 + runs);
    };
    // 1. no session: the dummies run
    both(1, 1);
    // 2. a non-IC session on the sim thread (pdUs 1 s): before its deadline they run; t_fbDeadline is the session's
    k.pdUs = 1000000, k.pdMode = 1;
    assert(PdPlanBegin() && t_pd.open && t_pd.entry == 1 && t_fbDeadline == t_pd.dl && g_pdTid == simTid && k.pdSessions[1] == 1);
    assert(!PdPlanBegin());  // nested: one session
    both(1, 1);
    // 3. past it: the game's no-path exits, nothing searched; cell [this+0x24] = 0, water out end = begin, AL 0, stack right
    t_pd.dl = 1;
    both(0, 0);
    assert(*(DWORD*)(self + 0x24) == 0 && *(DWORD*)(self + 0x28) == 0x77777777 && vec[1] == vec[0] && vec[2] == 0x1080);
    assert(k.pdSkipCell == 1 && k.pdSkipWater == 1 && t_pd.skip[0] == 1 && t_pd.skip[1] == 1);
    // 4. switch off mid-session, then another thread's session: they run
    k.pdMode = 0;
    both(1, 1);
    k.pdMode = 1;
    g_pdTid = 1;
    both(1, 1);
    g_pdTid = simTid;
    // 5. the end: counted as a hit (failed), t_fbDeadline back, the stubs pass again
    PdPlanEnd(0);
    assert(!t_pd.open && t_fbDeadline == 0 && !g_pdTid && k.pdHit == 1 && k.pdHitOk == 0 && k.pdLines == 1);
    both(1, 1);
    // 6. not the sim thread: no session; pdUs 0: no non-IC session
    simTid = 1;
    assert(!PdPlanBegin());
    simTid = GetCurrentThreadId();
    k.pdUs = 0;
    assert(!PdPlanBegin());
    // 7. IC deadlines by reason: NoStart / Foreign = pdUs, own task = pdOwnUs (0 = exempt, counted)
    k.pdUs = 200000;
    assert(PdIcUs(RFB_NOSTART, false) == 200000 && PdIcUs(RFB_FOREIGN, false) == 200000 && k.pdOwnExempt == 0);
    assert(PdIcUs(RFB_QFULL, false) == -1 && PdIcUs(RFB_NOYIELD, false) == -1 && k.pdOwnExempt == 2);
    k.pdOwnUs = 3000000;
    assert(PdIcUs(RFB_QFULL, false) == 3000000 && k.pdOwnExempt == 2);
    assert(PdIcUs(RFB_QFULL, true) == -1 && k.pdExemptSc == 1);  // §7.8: a stuck-check last round beats pdOwnUs
    k.pdExemptSc = 0;
    k.pdMode = 0;
    assert(PdIcUs(RFB_NOSTART, true) == 0 && k.pdExemptSc == 0);
    k.pdMode = 1, k.pdOwnUs = 0;
    // 8. D1 through CallPlanBudget: a sim-thread plan opens its session; its search sleeps past 2 ms, the next cost call
    //    with the clock read caps it (every edge a wall), the plan fails through its own exit; counted, deadline cleared
    const PlanLockedFn plan0 = g_planOrig;
    const LONG bmode0 = k.budgetMode;
    k.budgetMode = 0;
    k.pdUs = 2000;
    g_planOrig = [](void*, void*, int*, int*, int*, DWORD, DWORD, u8*, int*, int*, DWORD, DWORD) -> u8 {
        assert(t_pd.open && t_fbDeadline && t_budget.on && t_budget.dl == t_fbDeadline);
        g_cost(0, 0, 0, 0, 0, 0, 0, 0);
        for (;;) {
            if (g_cost(0, 0, 0, 0, 1, 0, 0, 0) >= 1e8f) return 0;
            g_cost(0, 0, 0, 0, 0, 0, 0, 0);
            Sleep(1);
        }
    };
    u8 opts[0x40] = {};
    int list[4] = {};
    assert(CallPlanBudget(NULL, NULL, list, NULL, NULL, 0, 1, opts, NULL, NULL, 0, 0) == 0);
    assert(k.pdSessions[1] == 2 && k.pdWorldCapped == 1 && k.pdHit == 2 && !t_pd.open && !t_fbDeadline && !t_budget.on);
    //    a plan under its deadline: unchanged, no hit
    g_planOrig = [](void*, void*, int*, int*, int*, DWORD, DWORD, u8*, int*, int*, DWORD, DWORD) -> u8 {
        for (int i = 0; i < 8; i++) assert(g_cost(0, 0, 0, 0, i & 1, 0, 0, 0) < 1e8f);
        return 1;
    };
    k.pdUs = 1000000;
    assert(CallPlanBudget(NULL, NULL, list, NULL, NULL, 0, 1, opts, NULL, NULL, 0, 0) == 1 && k.pdSessions[1] == 3 && k.pdHit == 2);
    //    pdMode 0: no session at all
    k.pdMode = 0;
    assert(CallPlanBudget(NULL, NULL, list, NULL, NULL, 0, 1, opts, NULL, NULL, 0, 0) == 1 && k.pdSessions[1] == 3);
    k.pdMode = 1;
    // 9. the IC path: SyncIc opens an IC session (reason NoStart, 2 ms); the IC's cell call after 3 ms is skipped, the plan
    //    fails; nested Route_PlanLocked would not open its own; own-task reasons run without a deadline (exempt)
    const IcPlanFn ic0 = g_icPlanOrig;
    g_icPlanOrig = PdtIc;
    const LONG am0 = k.asyncMode, log0 = k.rfbLogMs;
    k.asyncMode = 0, k.rfbLogMs = 0, k.pdUs = 2000;
    u8 out[0x48];
    const LONG c0 = g_pdtRuns[0];
    assert(SyncIc(0, out, 0x1234, NULL, RFB_NOSTART, "nojob", NULL) == out && *(DWORD*)out == 16 && g_pdtRuns[0] == c0);
    assert(k.pdSessions[0] == 1 && k.pdHit == 3 && k.pdSkipCell == 2 && !t_pd.open && !g_pdTid && !t_fbDeadline);
    assert(SyncIc(0, out, 0x1234, NULL, RFB_QFULL, "nojob", NULL) == out && *(DWORD*)out == 1 && g_pdtRuns[0] == c0 + 1);
    assert(k.pdSessions[0] == 1 && k.pdOwnExempt == 3);
    // 9b. docs/164 §7.8: an exempt IC's nested Route_PlanLocked opens no session (own task, pdOwnUs 0; before 0.36.78 it
    //     opened a pdUs one through CallPlanBudget); a stuck-check last round (IcCollect's mark from a refused START) is
    //     exempt even as NoStart, once; nested in an open session it cannot be: a cut there counts pdCutSc
    {
        static_assert(offsetof(Ts3nCounters, pdExemptSc) == 0x52C0 && offsetof(Ts3nCounters, pdCutSc) == 0x52C4 &&
                          offsetof(Ts3nCounters, pdSpare) == 0x52C8 && sizeof(Ts3nCounters) == 6314 * 4,
                      "counters v135 layout after v134 (docs/registry.md)");
        g_icPlanOrig = [](u8* o, DWORD) -> u8* {
            assert(t_pdNoCut && !PdPlanBegin() && !t_pd.open && !g_pdTid);
            *(DWORD*)o = 1;
            return o;
        };
        const LONG s0 = k.pdSessions[0] + k.pdSessions[1];
        g_rfbHandle = 0, g_collect = 0;
        assert(SyncIc(0, out, 0x1234, NULL, RFB_QFULL, "nojob", NULL) == out && !t_pdNoCut && k.pdOwnExempt == 4);
        t_pdScH = 0x1234;
        assert(SyncIc(0, out, 0x1234, NULL, RFB_NOSTART, "nojob", NULL) == out && !t_pdNoCut && !t_pdScH && k.pdExemptSc == 1);
        g_scSyncH = 0x1234;
        assert(IcCollect(0, out, 0x1234, NULL, NULL) == out && !g_scSyncH && k.pdExemptSc == 2);
        assert(k.pdSessions[0] + k.pdSessions[1] == s0);
        g_scSyncH = 0x1235;  // another route's mark: not this IC's, and gone after it
        g_icPlanOrig = PdtIc;
        assert(IcCollect(0, out, 0x1234, NULL, NULL) == out && !g_scSyncH && k.pdExemptSc == 2 && k.pdSessions[0] + k.pdSessions[1] == s0 + 1);
        assert(PdPlanBegin());  // nested: the open session keeps its deadline, the last round only marks it
        assert(PdIcUs(RFB_NOSTART, true) == 0 && t_pd.scLast && k.pdExemptSc == 2);
        t_pd.dl = 1;
        assert(PdSkip(0) == 1);
        PdPlanEnd(0);
        assert(k.pdCutSc == 1 && !t_pd.open);
        k.pdCutSc = 0, k.pdExemptSc = 0;
    }
    // 9c. docs/197 §7: a seen or unknown Sim's sync plan runs under the ceiling (no pd cap, no SyncCapMs, also where pd exempts it); unseen keeps both
    {
        static int mode;
        g_icPlanOrig = [](u8* o, DWORD) -> u8* {
            *(DWORD*)o = 1;
            if (mode == 2) {
                assert(!t_kdCeil && t_fbCapUs == 900000 && t_pd.open && !t_pd.ceil);
                return o;
            }
            assert(t_pd.open && t_pd.ceil && !t_fbCapUs && t_pd.wouldUs == 2000);
            if (mode == 1) Sleep(5);
            if (mode == 3) t_pd.dl = 1, *(DWORD*)o = 16;
            return o;
        };
        const LONG s0 = k.pdSessions[0], s1 = k.pdSessions[1], h0 = k.pdHit, hu0 = k.pdHitUs, l0 = k.pdLines, m0 = k.pdLateMaxUs, am = k.asyncMode, cap0 = k.fbCapUs, ex0 = k.kdExempt;
        k.kdExMode = 1, k.kdCeilUs = 8000000, k.asyncMode = 1, k.fbCapUs = 900000;
        mode = 0;
        KdScopeSet(0x1234, 1);
        assert(SyncIc(0, out, 0x1234, NULL, RFB_NOSTART, "nojob", NULL) == out && !t_kdCeil && !t_pd.open && t_kdSeen && !k.kdCeilCut);
        KdScopeClear(0x1234);  // no answer: unknown = exempt
        assert(SyncIc(0, out, 0x1234, NULL, RFB_NOSTART, "nojob", NULL) == out && !t_kdSeen && k.kdExempt == ex0 + 2);
        mode = 1;
        const LONG w0 = k.kdWouldCut;
        KdScopeSet(0x1234, 1);
        k.pdUs = 2000;
        assert(SyncIc(0, out, 0x1234, NULL, RFB_FOREIGN, "nojob", NULL) == out && k.kdWouldCut == w0 + 1 && k.kdExemptMaxUs >= 2000);
        mode = 3;  // the ceiling still cuts, loudly
        assert(SyncIc(0, out, 0x1234, NULL, RFB_NOSTART, "nojob", NULL) == out && k.kdCeilCut == 1);
        // a seen NPC: its own ceiling and counter, not the loud household line
        const LONG cn0 = k.kdCeilNpc;
        k.kdNpcCeilUs = 2000000;
        KdScopeSet(0x1234, 3);
        assert(KdSyncCeilUs(0x1234) == 2000000 && t_kdNpc && t_kdSeen);
        assert(SyncIc(0, out, 0x1234, NULL, RFB_NOSTART, "nojob", NULL) == out && k.kdCeilCut == 2 && k.kdCeilNpc == cn0 + 1);
        KdScopeSet(0x1234, 1);
        assert(KdSyncCeilUs(0x1234) == 8000000 && !t_kdNpc);
        k.kdCeilCut = 1, k.kdCeilNpc = cn0;
        mode = 2;
        KdScopeSet(0x1234, 2);
        assert(SyncIc(0, out, 0x1234, NULL, RFB_NOSTART, "nojob", NULL) == out);
        KdScopeSet(0x1235, 2);  // the plan ts3fixes runs, for a non-IC plan; another route's IC does not read it
        assert(!KdSyncCeilUs(0) && KdSyncCeilUs(0x1234) == 8000000);
        KdScopeClear(0x1234);  // not the pending route: stays
        assert(!KdSyncCeilUs(0));
        KdScopeSet(0x1235, 1);
        assert(PdPlanBegin() && t_pd.ceil && t_pd.wouldUs == 2000 && t_pd.handle == 0x1235 && t_pd.dl - t_pd.t0 > g_qpf.QuadPart);
        PdPlanEnd(1);
        KdScopeClear(0x1235);
        assert(!g_kdScopeH);
        // a job the ceiling cuts: exempt jobs get it as their deadline; the loud record path is RfJobNote's
        k.kdExMode = 0, k.kdCeilUs = 0, k.kdCeilCut = 0, k.kdWouldCut = 0, k.kdExemptMaxUs = 0, k.kdExempt = ex0;
        k.asyncMode = am, k.fbCapUs = cap0;
        k.pdSessions[0] = s0, k.pdSessions[1] = s1, k.pdHit = h0, k.pdHitUs = hu0, k.pdLines = l0, k.pdLateMaxUs = m0;
        t_kdSeen = false;
        g_icPlanOrig = PdtIc;
    }
    // 10. FailMemo on the sync Plan IC (ReplanDedupe's fakes): observe, then serve a failed repeat; a worker-side CallIc (no
    //     t_fmSync) never goes through it
    u8* const* rmSlot = g_orRmSlot;
    const OrRouteFn route0 = g_orRoute;
    const RdFollowerPosFn pos0 = g_rdFollowerPos;
    static u8* rmPtr = g_rdtRm;
    g_orRmSlot = &rmPtr, g_orRoute = RdtRoute, g_rdFollowerPos = RdtFollowerPos;
    static u8 world[0xA00], planner[0x200], wm[0x200];
    *(u8**)(world + 0x95C) = planner;
    *(u8**)(world + 0x964) = wm;
    void* const lock0 = g_rgLock;
    const LONG snReady0 = k.snReady;
    g_rgLock = world + 0x918, k.snReady = 5;
    *(DWORD*)(g_rdtRoute + 0x40) = 7;
    memset(g_rdTab, 0, sizeof(g_rdTab));
    k.pdMode = 0;
    const LONG rdc0 = k.rdCalls[0], rdc1 = k.rdCalls[1], rds0 = k.rdServed;
    g_fmOn = 1, k.fmMode = 0;
    g_icPlanOrig = [](u8* o, DWORD h) -> u8* { return RdtIc(0, o, h, NULL); };
    g_rdtType = 16;
    const DWORD p0 = g_rdtPlans, cnt = *(DWORD*)(g_rdtRoute + 0x1F4);
    SyncIc(0, out, 0x25000001, NULL, RFB_NOSTART, "nojob", NULL);
    SyncIc(0, out, 0x25000001, NULL, RFB_NOSTART, "nojob", NULL);
    assert(g_rdtPlans == p0 + 2 && k.fmCalls == 2 && k.fmFails == 2 && k.fmRep == 1 && k.fmWould[0] == 1 && k.fmWouldSame[0] == 1);
    k.fmMode = 1;
    memset(out, 0xCC, sizeof(out));
    SyncIc(0, out, 0x25000001, NULL, RFB_NOSTART, "nojob", NULL);
    assert(g_rdtPlans == p0 + 2 && k.fmServed == 1 && *(DWORD*)out == 16 && *(DWORD*)(g_rdtRoute + 0x1F4) == cnt);  // no +0x1F4 bump
    assert(k.rdCalls[0] == rdc0 && k.rdCalls[1] == rdc1 && k.rdServed == rds0);  // ReplanDedupe's own rows untouched
    CallIc(0, out, 0x25000001, NULL);  // not SyncIc's: planned
    assert(g_rdtPlans == p0 + 3 && k.fmCalls == 3);
    for (RdEntry& e : g_rdTab) e.ms = e.ms ? e.ms - 2000 : 0;  // out of every window
    g_rdtType = 1;  // a success is never served
    SyncIc(0, out, 0x25000001, NULL, RFB_NOSTART, "nojob", NULL);
    SyncIc(0, out, 0x25000001, NULL, RFB_NOSTART, "nojob", NULL);
    assert(g_rdtPlans == p0 + 5 && k.fmServed == 1);
    g_rdtType = 16;
    // 11. docs/178 T1, the worker slot: on another thread (the "worker") with g_pdWkTid = its id and its own t_fbDeadline
    //     past, both stubs take the no-path exits (fields, AL, stack), counted per search and per job (t_fbjdCut), the first
    //     cut is the job's capAt; with fbjdMode 0 or the deadline ahead they run; this thread (not g_pdWkTid, no session) runs
    //     them even with its own t_fbDeadline past
    k.fbjdMode = 1;
    PdtWk w = {cell, water, 1};
    PdtRunWorker(w);
    assert(!w.bad && w.rc == 0 && w.rw == 0 && w.cut[1] == 1 && w.cut[2] == 1 && w.cut[0] == 0 && w.capAt && w.runs == 0);
    assert(w.self24 == 0 && w.vecEnd == w.vecBegin && k.fbjdSkip[0] == 1 && k.fbjdSkip[1] == 1 && !g_pdWkTid);
    w.dl = PdNow() + g_qpf.QuadPart * 10;  // ahead
    PdtRunWorker(w);
    assert(!w.bad && w.rc == 1 && w.rw == 1 && w.runs == 2 && !w.cut[1] && !w.capAt && k.fbjdSkip[0] == 1);
    w.dl = 1;
    k.fbjdMode = 0;
    PdtRunWorker(w);
    assert(!w.bad && w.rc == 1 && w.rw == 1 && w.runs == 2 && k.fbjdSkip[0] == 1);
    k.fbjdMode = 1;
    g_pdWkTid = 1;  // another thread's slot; this thread has a deadline past but is not the worker
    t_fbDeadline = 1;
    both(1, 1);
    t_fbDeadline = 0;
    g_pdWkTid = 0;
    //     job end: >= 300 / >= 1000 ms in either mode; a cut job by phase and its time past the deadline
    t_fbjdCut[0] = 0, t_fbjdCut[1] = 3, t_fbjdCut[2] = 0;
    FbjdJobEnd(400000, g_qpf.QuadPart * 3, g_qpf.QuadPart * 2);  // ended 1 s after its deadline
    assert(k.fbjdLong[0] == 1 && k.fbjdLong[1] == 0 && k.fbjdCapped[1] == 1 && k.fbjdCapped[0] == 0 && k.fbjdLateMaxUs == 1000000);
    t_fbjdCut[1] = 0;
    FbjdJobEnd(1200000, g_qpf.QuadPart * 3, g_qpf.QuadPart * 2);  // not cut: long, nothing else
    assert(k.fbjdLong[0] == 2 && k.fbjdLong[1] == 1 && k.fbjdCapped[1] == 1 && k.fbjdLateUs == 1000000);
    // 12. docs/178 T4: sim waits behind an own job: counted with owReady only, buckets and max, >= 300 ms logged
    OwSimWait(400000, true, 0x25000001);
    assert(k.owWaits == 0);
    k.owReady = 1;
    OwSimWait(50000, true, 0x25000001), OwSimWait(400000, true, 0x25000001), OwSimWait(9000000, false, 0x25000002);
    assert(k.owWaits == 2 && k.owWaitUs == 450000 && k.owWaitMaxUs == 400000 && k.owWaitHist[0] == 1 && k.owWaitHist[2] == 1 &&
           k.owLines == 1);
    // 13. docs/197 KindDeadlines: off = the old rules (-1); kind per job; the stuck-check last round exempt; the follower's
    //     backoff (doubled, then uncut, a success resets); outcomes after a cut; the probe deadline; stuck-check marks
    {
        static_assert(offsetof(Ts3nCounters, kdMode) == 0x521C && offsetof(Ts3nCounters, kdUs) == 0x5224 &&
                          offsetof(Ts3nCounters, kdCut) == 0x525C && offsetof(Ts3nCounters, kdLateMaxUs) == 0x5294 &&
                          offsetof(Ts3nCounters, kdCutOk) == 0x52B4 && offsetof(Ts3nCounters, kdSpare) == 0x52B8,
                      "counters v134 layout after v133 (docs/registry.md)");
        memset(g_kdTab, 0, sizeof(g_kdTab));
        for (int i = 0; i < KD_N; i++) k.kdUs[i] = kKdDefMs[i] * 1000;
        AsyncJob j = {};
        j.handle = 0x25000001;
        k.kdMode = 1, k.kdReady = 0;
        assert(KdJobBegin(&j) == -1 && !t_kdKind && ScProbeUs() == k.pdUs);  // not ready: off
        k.kdReady = 1;
        assert(ScProbeUs() == 180000);
        *(DWORD*)(g_rdtRoute + 0x44) = 0;
        j.kdId = KdFollower(0x25000001);
        assert(j.kdId == 7 && KdFollower(0x25000002) == 0);
        // kinds
        AsyncJob f = {}, a = {}, p = {}, r = {};
        f.fb = 1, a.af = 1, a.kind = 3, p.probe = 1, p.sc = 0x10101, r.kind = 1;
        assert(KdJobBegin(&f) == 200000 && t_kdKind == KD_FGN + 1 && KdJobBegin(&a) == 200000 && t_kdKind == KD_A10F + 1);
        assert(KdJobBegin(&p) == 180000 && t_kdKind == KD_PROBE + 1 && KdJobBegin(&r) == 250000 && t_kdKind == KD_REPLAN + 1);
        AsyncJob last = {};
        last.sc = 0x10302;  // tagged, no probe mark: the last round
        assert(KdJobBegin(&last) == 0 && k.kdExemptSc == 1);
        // backoff: cut -> doubled -> cut -> uncut; the uncut job succeeds: counted, level reset
        *(DWORD*)j.result = 16;
        assert(KdJobBegin(&j) == 250000);
        KdJobEnd(&j, true, 260000, 0, 0);
        assert(k.kdCut[KD_PLAN] == 1 && k.kdAfterOk + k.kdAfterFail == 0);
        assert(KdJobBegin(&j) == 500000 && k.kdBackoff[0] == 1);
        KdJobEnd(&j, true, 510000, 0, 0);
        assert(k.kdCut[KD_PLAN] == 2 && k.kdAfterFail == 1 && k.kdLong[KD_PLAN] == 1);
        assert(KdJobBegin(&j) == 0 && k.kdBackoff[1] == 1);
        *(DWORD*)j.result = 1;
        KdJobEnd(&j, false, 900000, 0, 0);
        assert(k.kdAfterOk == 1 && k.kdUncutOk == 1 && k.kdLong[KD_PLAN] == 2);
        assert(KdJobBegin(&j) == 250000);  // reset by the success
        KdJobEnd(&j, false, 1000, 0, 0);
        assert(k.kdAfterOk == 1);  // nothing pending
        // a cut that still succeeded; late time past the deadline
        KdJobBegin(&j);
        KdJobEnd(&j, true, 300000, g_qpf.QuadPart * 3, g_qpf.QuadPart * 2);
        assert(k.kdCutOk == 1 && k.kdLateMaxUs == 1000000);
        // stuck-check mark on the cut follower; unknown handle ignored
        KdStuckMark(0x25000001);
        KdStuckMark(0x25000002);
        assert(k.kdAfterStuckCheck == 1);
        // a kind at 0 ms is never cut; the probe has no backoff row
        k.kdUs[KD_REPLAN] = 0;
        assert(KdJobBegin(&r) == 0);
        p.kdId = 9;
        KdJobBegin(&p);
        KdJobEnd(&p, true, 200000, 0, 0);
        assert(k.kdCut[KD_PROBE] == 1 && KdFind(9, false) == NULL);
        // eviction: 64 rows, the oldest cut makes room
        memset(g_kdTab, 0, sizeof(g_kdTab));
        for (int i = 0; i < kKdRows; i++) KdFind(100 + i, true)->cutTick = 1000 + i;
        assert(KdFind(200, true) == &g_kdTab[0] && KdFind(101, false) && !KdFind(100, false));
        // docs/197 §7 KdExemptSeen: own unknown or seen = exempt, own unseen or marked keeps its deadline; foreign only when known seen; probe never
        k.kdExMode = 1;
        AsyncJob s = {};
        assert(KdJobBegin(&s) == 0 && t_kdExemptUs == 250000);
        s.kdScope = 1;
        assert(KdJobBegin(&s) == 0);
        k.kdCeilUs = 10000000;
        assert(KdJobBegin(&s) == 10000000 && t_kdExemptUs == 250000);  // the ceiling is the exempt job's deadline
        k.kdNpcCeilUs = 2000000;
        s.kdScope = 3;  // a seen NPC: exempt the same, its own ceiling; every other scope keeps the household one
        assert(KdJobBegin(&s) == 2000000 && t_kdExemptUs == 250000 && t_kdJobScope == 3);
        s.kdScope = 0;
        assert(KdJobBegin(&s) == 10000000);
        s.kdScope = 1;
        k.kdCeilUs = 0;
        s.kdScope = 2;
        assert(KdJobBegin(&s) == 250000 && !t_kdExemptUs);
        s.kdScope = 0, s.uf = 1;
        assert(KdJobBegin(&s) == 250000);
        assert(KdJobBegin(&f) == 0 && KdJobBegin(&a) == 0);  // docs/249: unknown = household, whatever the kind
        f.kdScope = a.kdScope = 1, p.kdScope = 1;
        assert(KdJobBegin(&f) == 0 && KdJobBegin(&a) == 0 && KdJobBegin(&p) == 0);
        f.kdScope = a.kdScope = 2, p.kdScope = 2;
        assert(KdJobBegin(&f) == 200000 && KdJobBegin(&a) == 200000 && KdJobBegin(&p) == 180000);
        f.kdScope = a.kdScope = p.kdScope = 0;
        {   // KdExempt: no scope answer yet (session start, older ts3fixes) = unknown = household: exempt; a ts3fixes MARK still cuts
            const LONG n0 = ts3native_counters.kdScopeN;
            AsyncJob h = {}, u = {};
            h.kdScope = 1, u.uf = 1;
            ts3native_counters.kdScopeN = 0;
            assert(KdExempt(&f) && KdJobBegin(&f) == 0 && t_kdExemptUs == 200000);
            assert(KdExempt(&h) && !KdExempt(&u));
            h.kdScope = 2;
            assert(!KdExempt(&h));
            k.kdExMode = 0;  // KdExemptSeen=0: the old-behaviour arm, nothing exempt with or without answers
            assert(!KdExempt(&f) && KdJobBegin(&f) == 200000 && !t_kdExemptUs);
            ts3native_counters.kdScopeN = n0 ? n0 : 1;
            h.kdScope = 1;
            assert(!KdExempt(&f) && !KdExempt(&h));
            k.kdExMode = 1;
            assert(KdExempt(&f) && KdExempt(&h) && !KdExempt(&u));  // answers: unknown and household exempt
            h.kdScope = 2;
            assert(!KdExempt(&h));  // known unseen: cut
            ts3native_counters.kdScopeN = n0;
        }
        {   // rc1110c, the sync side: no scope answer yet = household too: sync plans get the household ceiling, ReplanDedupe holds
            const LONG n0 = k.kdScopeN, pm0 = k.pdMode, sc0 = k.kdCeilUs, nc0 = k.kdNpcCeilUs, js0 = t_kdJobScope;
            const DWORD sh0 = g_kdScopeH;
            k.kdExMode = 1, k.pdMode = 1, k.kdCeilUs = 10000000, k.kdNpcCeilUs = 2000000, t_kdJobScope = 0, g_kdScopeH = 0;
            k.kdScopeN = 0;
            assert(KdSyncCeilUs(0x1234) == 10000000 && KdSyncCeilUs(0) == 10000000 && !t_kdNpc && !t_kdSeen);
            assert(!KdKnownUnseen(0x1234) && !KdKnownUnseen(0));
            t_kdJobScope = 2;  // a ts3fixes mark (uf) on the running job: still known unseen
            assert(KdKnownUnseen(0x1234));
            t_kdJobScope = 0;
            KdScopeSet(0x1234, 1);  // household answer: the same
            assert(KdSyncCeilUs(0x1234) == 10000000 && t_kdSeen && !KdKnownUnseen(0x1234));
            KdScopeSet(0x1234, 2);  // unseen answer: usual caps, known unseen
            assert(!KdSyncCeilUs(0x1234) && KdKnownUnseen(0x1234));
            KdScopeSet(0x1234, 3);  // seen NPC: its own ceiling, not known unseen
            assert(KdSyncCeilUs(0x1234) == 2000000 && t_kdNpc && !KdKnownUnseen(0x1234));
            KdScopeClear(0x1234);
            k.kdExMode = 0;  // KdExemptSeen=0: unchanged old arm, with or without answers
            assert(!KdSyncCeilUs(0x1234) && KdKnownUnseen(0x1234));
            k.kdScopeN = 0;
            assert(!KdSyncCeilUs(0x1234) && KdKnownUnseen(0x1234));
            k.kdExMode = 1, k.kdScopeN = n0, k.pdMode = pm0, k.kdCeilUs = sc0, k.kdNpcCeilUs = nc0, t_kdJobScope = js0, g_kdScopeH = sh0;
            t_kdSeen = t_kdNpc = false;
        }
        // KindDeadlines off: FbJobCapMs and the probe's PlanDeadlineMs keep to the same rule; an own job stays as it was
        {
            const LONG md0 = k.kdMode, fm0 = k.fbjcMode, fc0 = k.fbjcCapUs, pu0 = k.pdUs;
            k.kdMode = 0, k.fbjcMode = 1, k.fbjcCapUs = 300000, k.pdUs = 300000, ts3native_counters.kdCeilUs = 10000000;
            AsyncJob pj = {};
            pj.probe = 1;
            assert(KdJobBegin(&f) == 10000000 && t_kdExemptUs == 300000 && KdJobBegin(&pj) == 10000000);
            f.kdScope = 3, k.kdNpcCeilUs = 2000000;
            assert(KdJobBegin(&f) == 2000000);
            f.kdScope = pj.kdScope = 2;
            assert(KdJobBegin(&f) == -1 && KdJobBegin(&pj) == -1);
            f.kdScope = 0;
            AsyncJob own = {};
            assert(KdJobBegin(&own) == -1);
            k.kdMode = md0, k.fbjcMode = fm0, k.fbjcCapUs = fc0, k.pdUs = pu0, ts3native_counters.kdCeilUs = 0;
        }
        // ReplanDedupe's scope
        KdScopeSet(0x25000009, 2);
        t_kdJobScope = 0;
        assert(KdKnownUnseen(0x25000009) && !KdKnownUnseen(0x25000008));
        KdScopeSet(0x25000009, 1);
        assert(!KdKnownUnseen(0x25000009));
        t_kdJobScope = 2;
        assert(KdKnownUnseen(0x25000008));
        t_kdJobScope = 0;
        KdScopeClear(0x25000009);
        KdScopeSet(0x25000009, 1);
        assert(KdScopeTake(0x25000008) == 0 && KdScopeTake(0x25000009) == 0);  // another route's START used it up
        KdScopeSet(0x25000009, 2);
        assert(KdScopeTake(0x25000009) == 2);
        // the job record behind a ts3fixes line: by route id, not for a sync plan
        AsyncJob e = {};
        e.handle = 0x25ABCDEF, e.seq = 9;
        *(LONG*)e.result = 16;
        const JobSplit sp = {-1, -1, 100000, 2000};
        t_kdKind = 1, t_kdExemptUs = 250000;
        RfJobNote(&e, false, 300000, sp, 0, 0);
        assert(!t_kdExemptUs);
        char rf[320];
        RfAppend("routefail: x, Plan async No, route 25ABCDEF", rf, sizeof(rf));
        assert(strstr(rf, "uncut (exempt)") && strstr(rf, "300 ms result 16") && strstr(rf, "deadline 250 ms") && strstr(rf, "scope unknown"));
        RfAppend("gohere plan: Plan sync No, route 25ABCDEF", rf, sizeof(rf));
        assert(!rf[0]);
        RfAppend("routefail: x, route 25ABCDE0", rf, sizeof(rf));
        assert(!rf[0]);
        t_kdKind = 1, t_kdExemptUs = 250000;
        RfJobNote(&e, true, 12000000, sp, 10000000, 1);
        assert(k.kdCeilCut == 1);
        RfAppend("routefail: x, Plan async No, route 25ABCDEF", rf, sizeof(rf));
        assert(strstr(rf, "cut (KdSeenCeilMs"));
        e.kdScope = 3, e.kdId = 0x1122334455667788ull;  // the NPC side: its own counters, the job line says "seen npc"
        const LONG cu0 = k.kdCutUnk, cs0 = k.kdCutSeen, cn1 = k.kdCutNpc, ce0 = k.kdCeilNpc;
        t_kdKind = 1, t_kdExemptUs = 250000, t_kdJobScope = 3;
        KdJobEnd(&e, true, 2100000, 0, 0);
        assert(k.kdCutNpc == cn1 + 1 && k.kdCutUnk == cu0 && k.kdCutSeen == cs0);
        RfJobNote(&e, true, 2100000, sp, 2000000, 1);
        assert(k.kdCeilCut == 2 && k.kdCeilNpc == ce0 + 1);
        RfAppend("routefail: x, Plan async No, route 25ABCDEF", rf, sizeof(rf));
        assert(strstr(rf, "cut (KdSeenCeilMs") && strstr(rf, "deadline 2000 ms") && strstr(rf, "seen npc"));
        { int n = 0; for (int i = 0; i < 40; i++) n += KdNpcLineOk(); assert(n <= 30); }
        t_kdJobScope = 0, k.kdCutNpc = cn1, k.kdCeilNpc = ce0, k.kdNpcCeilUs = 0;
        KdCamTest();
        k.kdCeilCut = 0, k.kdWouldCut = 1, k.kdExemptMaxUs = 300000;
        assert(k.kdWouldCut == 1 && k.kdExemptMaxUs == 300000 && k.kdExempt >= 4);
        k.kdExMode = 0;
        t_kdKind = 0, k.kdMode = 0, k.kdReady = 0;
        memset(g_kdTab, 0, sizeof(g_kdTab));
        printf("KindDeadlines (docs/197): counters v134; kinds, last-round exemption, follower backoff doubled / uncut / reset, "
               "outcomes after a cut, cut ok, late time, stuck-check marks, probe without backoff, row eviction ok\n");
    }
    // restore
    g_fmOn = 0;
    g_orRmSlot = rmSlot, g_orRoute = route0, g_rdFollowerPos = pos0;
    g_rgLock = lock0, k.snReady = snReady0;
    g_icPlanOrig = ic0, g_planOrig = plan0;
    k.asyncMode = am0, k.rfbLogMs = log0, k.budgetMode = bmode0, k.budgetReady = ready0;
    g_rtSimTidSlot = savedSim;
    g_pdOn = 0;
    VirtualFree(code, 0, MEM_RELEASE);
    memset(&k.pdMode, 0, sizeof(Ts3nCounters) - offsetof(Ts3nCounters, pdMode));
    printf("PlanDeadline / FailMemo (docs/164 §7): counters v99; cell / water entry stubs (pass outside a session, before the "
           "deadline, switch off, other thread; past it the game's no-path exits, stack right), non-IC session + world A* cap "
           "through the cost stub, IC sessions by reason (own task exempt), FailMemo observe / serve / not on worker calls ok\n");
    printf("FbJobDeadline / OwnJobWait (docs/178 T1, T4): counters v103; worker slot of the cell / water stubs (cut past the "
           "foreign job's deadline on its thread only, switch off and deadline ahead pass), per-job cuts, capAt, late time, "
           ">= 300 / 1000 ms jobs; own-job sim waits ok\n");
}
