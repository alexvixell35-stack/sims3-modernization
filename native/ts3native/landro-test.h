// TestLandCheckRO ([LandCheckRO], landro.cpp, docs/670 §24): the hold's gate rule, its fallbacks, the hook's dispatch, the
// shadow compare and the area hand-over with another thread, all on fakes. Included by selftest.cpp. No step depends on a
// timer: threads meet on events or on a counter, and the worker's wait cap is set per sub-case. Every sub-case states the
// hold / job / switch state it starts from (Clean()) instead of relying on what the one before left.
namespace lct {
static volatile LONG origCalls;
static LONG lastHold, lastKind, n = 3;
static void* lastSim;
static void* lastWk;
static int tripLock, thr, skew, gate, gatePassed;
static u8 c_area[0x60], c_ctx[0x20], c_w[0x20], c_wm[0x200], c_wmw[0x200], c_cand[32 * 8];
static u8 c_p[0x200], c_act[0x60], c_node[0x1A0];
static DWORD c_bk[2], c_f[16], c_key[2];
static u8* c_wmSlot;
// shadow (docs/670 §26): what the game's own call saw, the live area as that call left it, the private copy's storage
static LONG gameOpen, gameHold, dryCalls, dryLiveSame, frees;
static u8 c_scr[32 * 8], c_areaAfter[sizeof(c_area)], c_candAfter[sizeof(c_cand)];
static void* dryArea;
static void* freed;
static void __cdecl Free(void* p) { frees++, freed = p; }

static bool G12(DWORD lo, DWORD ret) {
    DWORD* slot = c_f + 4;
    slot[-2] = (DWORD)(uintptr_t)c_p, slot[0] = ret, slot[1] = lo, slot[2] = 0;
    return SlGate(12, slot);
}
static bool G0(DWORD lo, DWORD ret) {
    DWORD* slot = c_f + 4;
    c_key[0] = lo, c_key[1] = 0;
    slot[-2] = (DWORD)(uintptr_t)(c_p + 0x48), slot[0] = ret, slot[1] = (DWORD)(uintptr_t)c_key;
    return SlGate(0, slot);
}
// the game's gather: n candidates of 32 bytes, flag 0x400 cleared, AL 1
static DWORD __fastcall Orig(u8* area, void*) {
    InterlockedIncrement(&origCalls);
    if (GetCurrentThreadId() == g_async.owner) {
        lastHold = t_slHold * 10 + t_qlShared;
        lastKind = t_slKind;
        lastSim = g_lcSim;
    } else {
        lastWk = g_lcWk;
    }
    if (tripLock) QlSharedTrip(SLD_LOCK);  // a Lock_Acquire inside the gather (the lot index rebuild)
    // one lot waypoint's lookup; RgEnter asks SlGate only with a live hold
    if (gate && t_slHold == 1) gatePassed = G12(0x12, LC_GIP_RET12) && G0(0x12, LC_GIP + LC_GIP_RET0_OFF);
    if (thr) RaiseException(0xE0005152, 0, 0, NULL);
    if (t_slDry) {  // shadow's second gather: never on the live area, which must be as the game's call left it
        dryCalls++;
        dryArea = area;
        dryLiveSame = !memcmp(c_area, c_areaAfter, sizeof(c_area)) && !memcmp(c_cand, c_candAfter, sizeof(c_cand));
    } else {
        gameOpen = g_rgOffSimOpen, gameHold = t_slHold * 10 + t_qlShared;
    }
    u8* b = *(u8**)(area + 0x30);
    if (!b || *(u8**)(area + 0x38) < b + n * 32) {  // the vector grows through the game's allocator
        b = c_scr;
        *(u8**)(area + 0x30) = b, *(u8**)(area + 0x38) = b + sizeof(c_scr);
    }
    for (LONG i = 0; i < n * 32; i++) b[i] = (u8)(i * 7 + 1);
    if (t_slDry && skew) b[5] ^= 1;
    *(u8**)(area + 0x34) = b + n * 32;
    *(DWORD*)(area + 0x2C) &= ~0x400u;
    if (!t_slDry) memcpy(c_areaAfter, c_area, sizeof(c_area)), memcpy(c_candAfter, c_cand, sizeof(c_cand));
    return 1;
}
// every sub-case starts here: no hold, no job state left over, switches as given, counters zero
static void Clean(LONG mode, LONG shadow, LONG state) {
    LandCheckRoCounters& k = ts3native_landcheckro;
    assert(t_slHold == 0 && t_qlShared == 0 && t_qlDepth == 0 && t_slKind == SLK_SLOT && t_slDry == 0 && t_lcArea == NULL);
    assert(g_lcSim == NULL && g_lcWk == NULL && g_rgOffSimOpen == 0 && t_rg.n == 0 && t_rg.readers == 0);
    assert(TryAcquireSRWLockExclusive(&g_ql));  // nobody holds QL
    ReleaseSRWLockExclusive(&g_ql);
    memset(&k, 0, sizeof(k));
    k.lcMode = mode, k.lcShadow = shadow;
    g_async.owner = GetCurrentThreadId();
    g_async.state = state;
    g_inInvoke = 0;
    g_steerBroken = 0;
    ts3native_counters.rgOwnerTid = 0;
    tripLock = thr = skew = gate = gatePassed = 0;
    origCalls = 0;
    lastHold = lastKind = -1;
    lastSim = lastWk = NULL;
    gameOpen = gameHold = -1, dryCalls = frees = 0, dryLiveSame = -1;
    dryArea = freed = NULL;
    assert(!*(u8**)(g_lcScratch + 0x30) && !*(u8**)(g_lcScratch + 0x34) && !*(u8**)(g_lcScratch + 0x38));  // no storage left over
    c_wm[0x1CC] = c_wmw[0x1CC] = 0;
    *(DWORD*)(c_p + 0x50) = 1;
    *(u8**)(c_area + 0x40) = c_ctx;
    *(DWORD*)(c_area + 0x24) = 0;
    *(DWORD*)(c_area + 0x2C) = 0x403;
    *(u8**)(c_area + 0x30) = *(u8**)(c_area + 0x34) = c_cand;
    *(u8**)(c_area + 0x38) = c_cand + sizeof(c_cand);
    g_lcWaitSpins = 4000, g_lcWaitSleeps = 0x7FFF0000;  // no cap unless the sub-case sets one
    g_lcNoted = g_lcCapNoted = 0;
}
static DWORD WINAPI OtherGather(void*) { return LcHook(c_area, NULL) == 1 ? 0 : 1; }
}  // namespace lct

static void TestLandCheckRO() {
    using namespace lct;
    LandCheckRoCounters& k = ts3native_landcheckro;
    Ts3nCounters& kc = ts3native_counters;
    static_assert(offsetof(LandCheckRoCounters, lcMode) == 0 && offsetof(LandCheckRoCounters, lcShadow) == 4 &&
                      offsetof(LandCheckRoCounters, lcTaken) == 0x14 && offsetof(LandCheckRoCounters, lcDrop) == 0x28 &&
                      offsetof(LandCheckRoCounters, lcFb) == 0x48 && offsetof(LandCheckRoCounters, lcShadowMismatch) == 0x6C &&
                      offsetof(LandCheckRoCounters, lcWkGiveUp) == 0x7C && sizeof(LandCheckRoCounters) == 0x88,
                  "ts3native_landcheckro layout (docs/registry.md)");
    assert(LC_GATHER == TS3A(0x00654790) && LC_GIP_RET12 - LC_GIP == 0x14 && LC_GIP_RET0_OFF == 0x2B);
    const DWORD me = GetCurrentThreadId();
    const DWORD ret12 = LC_GIP_RET12, ret0 = LC_GIP + LC_GIP_RET0_OFF;
    // saved globals, put back at the end
    u8* const* const savedWm = g_srWmSlot;
    const LcFn savedOrig = g_lcOrig;
    const LcFreeFn savedFree = g_lcFree;
    const LONG savedRuns = g_asyncRuns;
    const DWORD savedOwner = g_async.owner;
    const LONG savedState = g_async.state, savedSpins = g_lcWaitSpins, savedSleeps = g_lcWaitSleeps;
    const LONG savedOwnerTid = kc.rgOwnerTid, savedBroken = g_steerBroken, savedOffSim = g_rgOffSimOpen;
    const int savedInvoke = g_inInvoke, savedRgN = t_rg.n;
    const LONG savedRgReaders = t_rg.readers;
    // earlier cases may leave these set; this case states its own (Clean() asserts them before each sub-case)
    g_rgOffSimOpen = 0;
    t_rg.n = 0, t_rg.readers = 0;
    // the fakes: a lot table with lot 0x12 (active lot 0x99), an area of the FindClosest object, its two world manager words
    memset(c_area, 0, sizeof(c_area)), memset(c_ctx, 0, sizeof(c_ctx)), memset(c_w, 0, sizeof(c_w));
    memset(c_wm, 0, sizeof(c_wm)), memset(c_wmw, 0, sizeof(c_wmw)), memset(c_p, 0, sizeof(c_p));
    memset(c_act, 0, sizeof(c_act)), memset(c_node, 0, sizeof(c_node)), memset(c_f, 0, sizeof(c_f));
    c_wmSlot = c_wm;
    g_srWmSlot = &c_wmSlot;
    *(u8**)(c_wm + 0xB0) = c_act;
    *(DWORD*)(c_act + 0x48) = 0x99;
    *(DWORD*)c_node = 0x12;
    *(DWORD*)(c_node + 0x10) = 0x12;
    c_bk[0] = (DWORD)(uintptr_t)c_node, c_bk[1] = 0x5E117E1;
    *(DWORD**)(c_p + 0x4C) = c_bk;
    *(u8**)(c_ctx + 0xC) = c_p, *(u8**)(c_ctx + 0x10) = c_w, *(u8**)(c_ctx + 0x14) = c_wm, *(u8**)(c_ctx + 0x18) = c_node;
    *(u8**)(c_w + 0xC) = c_wmw;
    g_lcOrig = Orig;
    g_lcFree = Free;
    const LONG sl12 = kc.slPass12, sl0 = kc.slPass0, slDrops = kc.slDrop[SLD_GATE] + kc.slDrop[SLD_LOCK] + kc.slDrop[SLD_MISS];

    // 1. the gate rule inside a hold: only 0x62FB60's two calls pass
    Clean(1, 0, A_RUNNING);
    assert(LcBegin(c_area) == 0 && t_slHold == 1 && t_qlShared == 1 && t_slKind == SLK_LAND && g_lcSim == c_area && t_lcArea == c_area);
    assert(G12(0x12, ret12) && G12(0x44, ret12) && G0(0x12, ret0) && t_slHold == 1 && k.lcPass12 == 2 && k.lcPass0 == 1);
    assert(!G12(0x12, SL_PFO_RET) && k.lcDrop[LCD_CALLER] == 1 && t_slHold == 2 && t_qlShared == 0 && g_lcSim == NULL);  // another caller of Contains
    assert(!SlEnd() && t_slHold == 0 && t_slKind == SLK_SLOT && t_lcArea == NULL);
    Clean(1, 0, A_RUNNING);
    assert(LcBegin(c_area) == 0 && !G0(0x12, 0x0062F7A2) && k.lcDrop[LCD_CALLER] == 1 && !SlEnd());  // another caller of GetOrInsert
    Clean(1, 0, A_RUNNING);
    assert(LcBegin(c_area) == 0 && !G0(0x44, ret0) && k.lcDrop[SLD_MISS] == 1 && g_lcSim == NULL && !SlEnd());  // a miss would insert
    Clean(1, 0, A_RUNNING);
    assert(LcBegin(c_area) == 0 && !G0(0x99, ret0) && k.lcDrop[SLD_MISS] == 1 && !SlEnd());  // the active lot: no entry
    Clean(1, 0, A_RUNNING);
    *(DWORD*)(c_p + 0x50) = 0;
    assert(LcBegin(c_area) == 0 && !G0(0x12, ret0) && k.lcDrop[SLD_TABLE] == 1 && !SlEnd());  // the original would divide by 0
    Clean(1, 0, A_RUNNING);
    assert(LcBegin(c_area) == 0 && !SlGate(3, c_f + 4) && k.lcDrop[SLD_GATE] == 1 && !SlEnd());  // any other gate site
    Clean(1, 0, A_RUNNING);
    {
        const LONG tl2 = kc.ltQlHeldBlock;
        assert(LcBegin(c_area) == 0);
        QlSharedTrip(SLD_LOCK);  // LockAcquireFull's first line
        assert(t_slHold == 2 && t_qlShared == 0 && k.lcDrop[SLD_LOCK] == 1 && g_lcSim == NULL && kc.ltQlHeldBlock == tl2 && !SlEnd());
    }
    Clean(1, 0, A_RUNNING);
    assert(LcBegin(c_area) == 0);
    QlSharedTrip(SLD_MANAGED);
    assert(k.lcDrop[SLD_MANAGED] == 1 && !SlEnd());
    Clean(1, 0, A_RUNNING);
    assert(LcBegin(c_area) == 0 && G12(0x12, ret12));
    g_lcWk = c_area;  // the worker arrived at this area's gather: the next gate hands it over
    assert(!G12(0x12, ret12) && k.lcDrop[LCD_WORKER] == 1 && k.lcPass12 == 1 && g_lcSim == NULL && !SlEnd());
    g_lcWk = NULL;
    Clean(1, 0, A_RUNNING);
    assert(LcBegin(c_area) == 0 && G12(0x12, ret12) && SlEnd() && g_lcSim == NULL && t_slKind == SLK_SLOT);  // kept to the end
    assert(kc.slPass12 == sl12 && kc.slPass0 == sl0 && kc.slDrop[SLD_GATE] + kc.slDrop[SLD_LOCK] + kc.slDrop[SLD_MISS] == slDrops);  // not [SlotEnabledRO]'s

    // 2. LcBegin's refusals: nothing held, nothing published
    Clean(1, 0, A_RUNNING);
    c_wm[0x1CC] = 1;
    assert(LcBegin(c_area) == LC_FB_DIRTY && t_slHold == 0 && g_lcSim == NULL);
    c_wm[0x1CC] = 0, c_wmw[0x1CC] = 1;
    assert(LcBegin(c_area) == LC_FB_DIRTY && t_slHold == 0 && g_lcSim == NULL);
    c_wmw[0x1CC] = 0;
    g_lcWk = c_area;
    assert(LcBegin(c_area) == LC_FB_AREA && t_slHold == 0 && t_qlShared == 0 && g_lcSim == NULL);
    g_lcWk = c_area + 4;  // the worker on another area
    assert(LcBegin(c_area) == 0 && SlEnd());
    g_lcWk = NULL;
    t_qlDepth = 1;
    assert(LcBegin(c_area) == LC_FB_NESTED && g_lcSim == NULL);
    t_qlDepth = 0;
    g_steerBroken = 1;
    assert(LcBegin(c_area) == LC_FB_BROKEN && g_lcSim == NULL);
    g_steerBroken = 0;

    // 3. the hook: both switches off, shape, the hold in a job, a drop inside, state fallbacks, QL busy, unwind
    Clean(0, 0, A_RUNNING);
    assert(LcHook(c_area, NULL) == 1 && lastHold == 0 && k.lcCalls == 0 && origCalls == 1);
    Clean(1, 0, A_RUNNING);
    *(u8**)(c_area + 0x40) = NULL;  // the game returns before any routing read
    assert(LcHook(c_area, NULL) == 1 && lastHold == 0 && k.lcCalls == 0);
    *(u8**)(c_area + 0x40) = c_ctx;
    *(DWORD*)(c_area + 0x24) = 3;
    assert(LcHook(c_area, NULL) == 1 && lastHold == 0 && k.lcCalls == 0);
    Clean(1, 0, A_RUNNING);
    gate = 1;
    assert(LcHook(c_area, NULL) == 1 && lastHold == 11 && lastKind == SLK_LAND && lastSim == c_area && gatePassed == 1);
    assert(k.lcCalls == 1 && k.lcInJob == 1 && k.lcTaken == 1 && k.lcPass12 == 1 && k.lcPass0 == 1 && k.lcCands == n && t_slHold == 0 && g_lcSim == NULL);
    assert(TryAcquireSRWLockExclusive(&g_ql));  // released after the call
    ReleaseSRWLockExclusive(&g_ql);
    Clean(1, 0, A_RUNNING);
    tripLock = 1;
    assert(LcHook(c_area, NULL) == 1 && lastHold == 11 && k.lcTaken == 0 && k.lcDrop[SLD_LOCK] == 1 && t_slHold == 0 && t_slKind == SLK_SLOT);
    Clean(1, 0, A_RUNNING);
    c_wm[0x1CC] = 1;
    assert(LcHook(c_area, NULL) == 1 && lastHold == 0 && k.lcFb[LC_FB_DIRTY - 1] == 1 && k.lcInJob == 1 && k.lcTaken == 0);
    Clean(1, 0, A_RUNNING);
    g_inInvoke = 1;
    assert(LcHook(c_area, NULL) == 1 && lastHold == 0 && k.lcFb[LC_FB_STATE - 1] == 1);
    g_inInvoke = 0;
    kc.rgOwnerTid = (LONG)me;
    assert(LcHook(c_area, NULL) == 1 && lastHold == 0 && k.lcFb[LC_FB_STATE - 1] == 2);
    kc.rgOwnerTid = 0;
    t_rg.n = 1;
    assert(LcHook(c_area, NULL) == 1 && lastHold == 0 && k.lcFb[LC_FB_STATE - 1] == 3);
    t_rg.n = 0;
    Clean(1, 0, A_RUNNING);
    {
        static HANDLE ev[2];
        ev[0] = CreateEventW(NULL, FALSE, FALSE, NULL), ev[1] = CreateEventW(NULL, FALSE, FALSE, NULL);
        HANDLE qh = CreateThread(NULL, 0, [](void*) -> DWORD {
            AcquireSRWLockExclusive(&g_ql);
            SetEvent(ev[0]);
            WaitForSingleObject(ev[1], INFINITE);
            ReleaseSRWLockExclusive(&g_ql);
            return 0;
        }, NULL, 0, NULL);
        WaitForSingleObject(ev[0], INFINITE);
        assert(LcHook(c_area, NULL) == 1 && lastHold == 0 && k.lcFb[LC_FB_TRY - 1] == 1 && g_lcSim == NULL);  // a worker writer holds QL
        SetEvent(ev[1]);
        WaitForSingleObject(qh, INFINITE);
        CloseHandle(qh), CloseHandle(ev[0]), CloseHandle(ev[1]);
    }
    Clean(1, 0, A_RUNNING);
    thr = 1;
    __try {
        LcHook(c_area, NULL);
        assert(!"no exception");
    } __except (GetExceptionCode() == 0xE0005152 ? EXCEPTION_EXECUTE_HANDLER : EXCEPTION_CONTINUE_SEARCH) {
    }
    thr = 0;
    assert(lastHold == 11 && t_slHold == 0 && t_qlShared == 0 && t_slKind == SLK_SLOT && g_lcSim == NULL && t_lcArea == NULL);

    // 4. shadow (no job, docs/670 §26): the game's gather with nothing of ours open, then the dry hold on the private copy;
    // the live area and its candidates are never written by the second gather; equal, skewed, a dry drop, a job in between
    Clean(1, 0, A_IDLE);
    assert(LcHook(c_area, NULL) == 1 && lastHold == 0 && origCalls == 1 && k.lcShadowRuns == 0 && k.lcCalls == 1 && k.lcInJob == 0);
    Clean(0, 1, A_IDLE);
    gate = 1;
    assert(LcHook(c_area, NULL) == 1 && origCalls == 2 && lastHold == 11 && k.lcShadowRuns == 1 && k.lcShadowMismatch == 0);
    assert(gameOpen == 0 && gameHold == 0);                                    // the game's call: no start hold-off, no hold
    assert(dryCalls == 1 && dryArea == (void*)g_lcScratch && dryLiveSame == 1);  // the dry gather: on the copy
    assert(!memcmp(c_area, c_areaAfter, sizeof(c_area)) && !memcmp(c_cand, c_candAfter, sizeof(c_cand)));  // and after it
    assert(*(u8**)(c_area + 0x30) == c_cand && *(u8**)(c_area + 0x34) == c_cand + n * 32 && *(DWORD*)(c_area + 0x2C) == 3);
    assert(frees == 1 && freed == (void*)c_scr && gatePassed == 1 && k.lcPass12 == 1 && k.lcPass0 == 1);
    assert(g_rgOffSimOpen == 0 && t_slHold == 0 && g_lcSim == NULL && k.lcTaken == 0 && k.lcFb[LC_FB_JOB - 1] == 0);
    Clean(0, 1, A_IDLE);
    skew = 1;
    assert(LcHook(c_area, NULL) == 1 && k.lcShadowRuns == 1 && k.lcShadowMismatch == 1 && dryLiveSame == 1 && frees == 1);
    Clean(0, 1, A_IDLE);
    tripLock = 1;
    assert(LcHook(c_area, NULL) == 1 && k.lcShadowRuns == 1 && k.lcShadowDrop == 1 && k.lcDrop[SLD_LOCK] == 1 && k.lcShadowMismatch == 0);
    Clean(0, 1, A_IDLE);
    c_wm[0x1CC] = 1;  // a dirty lot index: the game's gather only
    assert(LcHook(c_area, NULL) == 1 && origCalls == 1 && dryCalls == 0 && k.lcShadowRuns == 0 && k.lcFb[LC_FB_DIRTY - 1] == 1 && frees == 0);
    Clean(0, 1, A_IDLE);
    {
        // a job ran while the game's gather did (the worker counts it in g_asyncRuns): not compared, no second gather
        const LcFn orig = g_lcOrig;
        g_lcOrig = [](u8* area, void* edx) -> DWORD {
            InterlockedIncrement(&g_asyncRuns);
            return Orig(area, edx);
        };
        assert(LcHook(c_area, NULL) == 1 && origCalls == 1 && dryCalls == 0 && k.lcShadowRuns == 0 && k.lcFb[LC_FB_JOB - 1] == 1);
        assert(g_rgOffSimOpen == 0 && gameOpen == 0 && frees == 0);
        g_lcOrig = orig;
    }
    Clean(0, 1, A_RUNNING);  // shadow only, a job runs: the game's gather, no hold
    assert(LcHook(c_area, NULL) == 1 && origCalls == 1 && lastHold == 0 && k.lcShadowRuns == 0);

    // 5. another thread's gather (the route worker): published, waits for the sim's hold on the same area, never for another
    Clean(1, 0, A_RUNNING);
    {
        HANDLE th = CreateThread(NULL, 0, OtherGather, NULL, 0, NULL);
        WaitForSingleObject(th, INFINITE);
        DWORD rc = 1;
        assert(GetExitCodeThread(th, &rc) && rc == 0);
        CloseHandle(th);
        assert(origCalls == 1 && lastWk == c_area && g_lcWk == NULL && k.lcWkGathers == 1 && k.lcWkWaits == 0 && k.lcCalls == 0);
    }
    Clean(1, 0, A_RUNNING);
    {
        assert(LcBegin(c_area) == 0);  // the sim's hold is on the area
        HANDLE th = CreateThread(NULL, 0, OtherGather, NULL, 0, NULL);
        while (k.lcWkWaits != 1) Sleep(1);  // the other thread reached its wait
        assert(origCalls == 0 && g_lcWk == c_area);  // and has not gathered
        assert(SlEnd());                              // the hold ends: the area is free
        WaitForSingleObject(th, INFINITE);
        CloseHandle(th);
        assert(origCalls == 1 && k.lcWkGiveUp == 0 && k.lcMode == 1 && g_lcWk == NULL && g_lcSim == NULL);
    }
    Clean(1, 0, A_RUNNING);
    {
        assert(LcBegin(c_area) == 0);
        HANDLE th = CreateThread(NULL, 0, OtherGather, NULL, 0, NULL);
        while (k.lcWkWaits != 1) Sleep(1);
        assert(!G12(0x12, ret12) && k.lcDrop[LCD_WORKER] == 1);  // the sim's next gate hands over (and would wait for the job)
        WaitForSingleObject(th, INFINITE);
        CloseHandle(th);
        assert(origCalls == 1 && k.lcWkGiveUp == 0 && !SlEnd());
    }
    Clean(1, 0, A_RUNNING);
    {
        g_lcWaitSpins = 2, g_lcWaitSleeps = 0;  // the cap at once: the switch goes off, the gather goes on
        assert(LcBegin(c_area) == 0);
        HANDLE th = CreateThread(NULL, 0, OtherGather, NULL, 0, NULL);
        WaitForSingleObject(th, INFINITE);
        CloseHandle(th);
        assert(origCalls == 1 && k.lcWkWaits == 1 && k.lcWkGiveUp == 1 && k.lcMode == 0 && SlEnd());
    }

    Clean(0, 0, A_IDLE);
    g_srWmSlot = savedWm;
    g_lcOrig = savedOrig;
    g_lcFree = savedFree;
    g_asyncRuns = savedRuns;
    g_async.owner = savedOwner;
    g_async.state = savedState;
    g_lcWaitSpins = savedSpins, g_lcWaitSleeps = savedSleeps;
    kc.rgOwnerTid = savedOwnerTid, g_steerBroken = savedBroken, g_rgOffSimOpen = savedOffSim;
    g_inInvoke = savedInvoke, t_rg.n = savedRgN, t_rg.readers = savedRgReaders;
    memset(&k, 0, sizeof(k));
    puts("land check RO (SH): gate rule, refusals, hook, shadow, area hand-over ok");
}
