// --- [PlacementRO] (fgro.cpp, docs/174): layout; the call-site stub on fakes (the game's 0x631F10 and S2's point query
// replaced): pass-through rules, the clone's answer, fallbacks, shadow compare. Included by selftest.cpp.
namespace fgt {
static LONG origCalls, origRet, qCalls, qWhy, qN;
static DWORD qLo, qHi;
static LONG qLevel;
static float qX, qZ, qR;
static const u8* qRec;
static DWORD __fastcall Orig(u8*, void*, const u8*, const float*, float, void*, DWORD) { origCalls++; return (DWORD)origRet | 0xABCD00; }
static int Query(SrCtx* c, const u8*, DWORD lo, DWORD hi, const SrPt& pt, float r, const u8* rec) {
    qCalls++;
    qLo = lo, qHi = hi, qLevel = pt.level, qX = pt.v[0], qZ = pt.v[2], qR = r, qRec = rec;
    c->n = qN;
    return qWhy;
}
}  // namespace fgt

static void TestPlacementRO() {
    using namespace fgt;
    Ts3nCounters& k = ts3native_counters;
    static_assert(offsetof(Ts3nCounters, fgReady) == 0x42C8 && offsetof(Ts3nCounters, fgMode) == 0x42CC &&
                      offsetof(Ts3nCounters, fgShadow) == 0x42D0 && offsetof(Ts3nCounters, fgFb) == 0x42E8 &&
                      offsetof(Ts3nCounters, fgShadowMismatch) == 0x4304 && sizeof(Ts3nCounters) == 5817 * 4,
                  "counters v100 layout after v99 (docs/registry.md)");
    assert(k.version == 159);
    assert(kFgSite.site == 0x00635013 && kFgSite.target == 0x00631F10);
    memset(&k.fgReady, 0, sizeof(Ts3nCounters) - offsetof(Ts3nCounters, fgReady));
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
    const FgFn savedOrig = g_fgOrig;
    const FgQueryFn savedQuery = g_fgQuery;
    const AsyncSlot savedAsync = g_async;
    const LONG savedArmed = g_twArmed, savedOwner = k.rgOwnerTid;
    const float* savedOne = g_srOne;
    static const float one = 1.0f;
    g_fgOrig = Orig, g_fgQuery = Query, g_srOne = &one;
    const DWORD me = GetCurrentThreadId();
    g_async.owner = me;
    k.rgOwnerTid = 0;
    // 0x634F10's frame: lot at ctx+0x54 (id +0x48/+0x4C), record at ctx+0x40; pt = {x, z, level}
    static u8 ctx[0x60], lot[0x50], rec[0x30];
    *(u8**)(ctx + 0x54) = lot, *(u8**)(ctx + 0x40) = rec;
    *(DWORD*)(lot + 0x48) = 0x30, *(DWORD*)(lot + 0x4C) = 0x1;
    float pt[3] = {3.0f, 5.0f, 0.0f};
    *(LONG*)&pt[2] = 2;
    u8* const P = lot;  // the planner pointer only passes through to the query
    auto call = [&](DWORD mode = 1) { return FgStub(P, NULL, ctx, pt, 0.25f, NULL, mode); };

    // switches off / another thread: the game's call, nothing counted
    origCalls = 0;
    origRet = 1;
    assert((u8)call() == 1 && origCalls == 1 && k.fgCalls == 0);
    k.fgMode = 1, g_async.owner = me + 1, g_async.state = A_RUNNING;
    assert((u8)call() == 1 && origCalls == 2 && k.fgCalls == 0);
    g_async.owner = me;

    // a job runs: the clone answers with the query's arguments; no game call
    g_twArmed = 1, qWhy = SR_OK, qN = 0, qCalls = 0;
    assert(call() == 0 && origCalls == 2 && k.fgTaken == 1 && k.fgHits == 0);
    assert(qCalls == 1 && qLo == 0x30 && qHi == 1 && qLevel == 2 && qX == 3.0f && qZ == 5.0f && qR == 0.25f && qRec == rec);
    qN = 2;
    assert(call() == 1 && origCalls == 2 && k.fgTaken == 2 && k.fgHits == 1);
    *(DWORD*)(lot + 0x48) = 0, *(DWORD*)(lot + 0x4C) = 0;  // lot id 0: 0 before any routing touch (0x631F31)
    assert(call() == 0 && qCalls == 2 && k.fgTaken == 3);
    *(DWORD*)(lot + 0x48) = 0x30, *(DWORD*)(lot + 0x4C) = 1;
    *(u8**)(ctx + 0x54) = NULL;  // no lot (0x631F21)
    assert(call() == 0 && qCalls == 2 && k.fgTaken == 4);
    *(u8**)(ctx + 0x54) = lot;

    // fallbacks: the game's call, counted by reason
    struct Fb { const char* name; int why; int idx; };
    static const Fb fbs[] = {{"deferred flush", SR_FB_DEFERRED, FG_FB_LOT}, {"lot miss", SR_FB_LOTMISS, FG_FB_LOT},
                             {"shape", SR_FB_SHAPE, FG_FB_SHAPE}, {"tripwire", SR_FB_TRIP, FG_FB_TRIP}};
    for (const Fb& f : fbs) {
        qWhy = f.why;
        const LONG o0 = origCalls, f0 = k.fgFb[f.idx];
        origRet = 0;
        if (call() != 0xABCD00 || origCalls != o0 + 1 || k.fgFb[f.idx] != f0 + 1) {
            printf("PlacementRO fallback '%s' not taken\n", f.name);
            assert(false);
        }
    }
    qWhy = SR_OK;
    {
        const LONG o0 = origCalls, s0 = k.fgFb[FG_FB_STATE];
        assert(call(0) == 0xABCD00 && origCalls == o0 + 1 && k.fgFb[FG_FB_STATE] == s0 + 1);  // mode 0: not modelled
        k.rgOwnerTid = (LONG)me;  // the sim owns +0x918 (lent or recursive): the game's path
        assert(call() == 0xABCD00 && origCalls == o0 + 2 && k.fgFb[FG_FB_STATE] == s0 + 2);
        k.rgOwnerTid = 0;
        g_twArmed = 0;  // the job ended before the clone (tripwires disarmed)
        assert(call() == 0xABCD00 && origCalls == o0 + 3 && k.fgFb[FG_FB_JOBEND] == 1);
        g_twArmed = 1;
        AcquireSRWLockExclusive(&g_ql);  // a writer holds QL: try only
        assert(call() == 0xABCD00 && origCalls == o0 + 4 && k.fgFb[FG_FB_TRY] == 1);
        ReleaseSRWLockExclusive(&g_ql);
        k.fgMode = 0;  // switch off with a job: the game's call
        assert(call() == 0xABCD00 && origCalls == o0 + 5);
    }

    // shadow with no job: the game's answer is returned, the clone compared; a skew is one mismatch
    k.fgShadow = 1, g_async.state = A_IDLE, g_twArmed = 0;
    origRet = 1, qN = 1;
    const LONG o0 = origCalls;
    assert(call() == (0xABCD00 | 1) && origCalls == o0 + 1 && k.fgShadowRuns == 1 && k.fgShadowMismatch == 0 && g_rgOffSimOpen == 0);
    qN = 0;
    assert(call() == (0xABCD00 | 1) && k.fgShadowRuns == 2 && k.fgShadowMismatch == 1 && g_rgOffSimOpen == 0);
    origRet = 0;
    assert(call() == 0xABCD00 && k.fgShadowRuns == 3 && k.fgShadowMismatch == 1);
    // shortcut: no threaded case here; the QL protocol is S2's (steerro-test, obstructro and rtoro threaded runs)
    printf("PlacementRO ([PlacementRO] Enabled=1): counters v100; switch off / other thread pass; job: clone answers 0/1 with "
           "the frame's lot, level, point, radius and record, no game call; no lot / lot id 0 answer 0; fallbacks (deferred, lot "
           "miss, shape, tripwire, mode 0, +0x918 owner, job end, QL busy, switch off) call the game; shadow compare + skew\n");

    k.fgMode = k.fgShadow = 0;
    g_fgOrig = savedOrig, g_fgQuery = savedQuery, g_srOne = savedOne;
    g_async = savedAsync, g_twArmed = savedArmed, k.rgOwnerTid = savedOwner;
}
