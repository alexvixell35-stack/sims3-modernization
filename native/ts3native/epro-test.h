// TestEditPathRO ([EditPathRO], epro.cpp, docs/670 §8): the stub at 0x85FFC2 on fakes. The gather, the walk and the consumer
// against the game's own code are steerro-test section EP; here: pass-through rules, the job answer, every fallback, shadow.
// Included by selftest.cpp.
namespace ept {
static LONG origCalls, origPush, origOpen, lookups, armAt, idleAt;  // origOpen: g_rgOffSimOpen seen by the game's call
static u8 g_world[0xA00], g_wm[0x200], g_wmw[0x200], g_planner[4], g_lot[0x50];
static bool g_lotOn;  // the routing world's manager finds a lot (key 0: Contains says no entry, the gather is empty)
struct IdVec { DWORD* b; DWORD* e; DWORD* c; };
static DWORD g_idBuf[64];
static void __cdecl Orig(u8*, u8*, u8*, u8* ids, const float*, const float*, DWORD, const void*, const void*, DWORD, const void*, DWORD) {
    origCalls++;
    origOpen = g_rgOffSimOpen;
    IdVec* v = (IdVec*)ids;
    for (LONG i = 0; i < origPush; i++) { *v->e++ = 0x7000 + (DWORD)i; *v->e++ = 0; }
}
static void __cdecl Conv(const void* src, void* out) {
    const float* s = (const float*)src;
    SrPt* o = (SrPt*)out;
    o->v[0] = s[0], o->v[1] = s[1], o->v[2] = s[2], o->v[3] = 1.0f;
    o->level = (LONG)s[3];
}
static u8* __fastcall Lookup(void* m, void*, float, float) {
    if (++lookups == armAt) g_twArmed = 1;                           // a new job armed while a rerun is on its way
    if (lookups == idleAt) InterlockedExchange(&g_async.state, A_IDLE);  // the job ends inside a pass
    return g_lotOn && m == g_wmw ? g_lot : NULL;
}
static void __fastcall Push(void* vec, void*, const DWORD* id) {
    IdVec* v = (IdVec*)vec;
    *v->e++ = id[0];
    *v->e++ = id[1];
}
}  // namespace ept

static void TestEditPathRO() {
    using namespace ept;
    Ts3nCounters& kc = ts3native_counters;
    EditPathRoCounters& k = ts3native_editpathro;
    static_assert(offsetof(EditPathRoCounters, epMode) == 0 && offsetof(EditPathRoCounters, epShadow) == 4 &&
                      offsetof(EditPathRoCounters, epFb) == 0x28 && offsetof(EditPathRoCounters, epShadowMismatch) == 0x4C &&
                      offsetof(EditPathRoCounters, epOtherForm) == 0x58 && offsetof(EditPathRoCounters, epGuard) == 0x5C &&
                      offsetof(EditPathRoCounters, epRetryOk) == 0x70 && sizeof(EditPathRoCounters) == 0x74,
                  "ts3native_editpathro layout (docs/registry.md)");
    assert(kEpSite.site == 0x0085FFC2 && kEpSite.target == 0x0085F5A0);
    memset(&k, 0, sizeof(k));
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
    const EpFn savedOrig = g_epOrig;
    const SrConvFn savedConv = g_srConv;
    const SrLotLookupFn savedLookup = g_srLotLookup;
    const SrIdPushFn savedPush = g_srIdPush;
    const float* const savedOne = g_srOne;
    static const float one = 1.0f;
    g_srOne = &one;  // the game constant 0x107A538 does not exist here
    const AsyncSlot savedAsync = g_async;
    const LONG savedArmed = g_twArmed, savedOwner = kc.rgOwnerTid, savedActive = g_twActive;
    g_epOrig = Orig, g_srConv = Conv, g_srLotLookup = Lookup, g_srIdPush = Push;
    const DWORD me = GetCurrentThreadId();
    g_async.owner = me, g_async.state = A_IDLE;
    kc.rgOwnerTid = 0;
    memset(g_world, 0, sizeof(g_world));
    memset(g_wm, 0, sizeof(g_wm));
    memset(g_wmw, 0, sizeof(g_wmw));
    memset(g_lot, 0, sizeof(g_lot));
    g_world[0x974] = 1;
    *(u8**)(g_world + 0x95C) = g_planner;
    *(u8**)(g_world + 0x964) = g_wmw;
    alignas(16) static float start[4] = {1, 0, 2, 0}, goal[4] = {5, 0, 2, 0};
    static IdVec ids;
    ids.b = ids.e = g_idBuf, ids.c = g_idBuf + 64;
    static const void* excl;
    excl = NULL;
    auto call = []() { EpStub(g_wm, g_world, g_planner, (u8*)&ids, start, goal, 0x55, excl, NULL, 0, NULL, 0); };
    auto count = []() { return (LONG)(ids.e - ids.b) / 2; };

    // switch off / another thread / an exclude vector: the game's call
    origCalls = 0, origPush = 0, g_lotOn = false;
    call();
    assert(origCalls == 1 && k.epCalls == 0);
    k.epMode = 1, g_async.owner = me + 1, g_async.state = A_RUNNING;
    call();
    assert(origCalls == 2 && k.epCalls == 0);
    g_async.owner = me;
    static DWORD vec[4];
    excl = vec;
    call();
    assert(origCalls == 3 && k.epCalls == 0 && k.epOtherForm == 1);
    excl = NULL;
    // no job, no shadow: the game's call
    g_async.state = A_IDLE;
    call();
    assert(origCalls == 4 && k.epCalls == 1 && k.epJob == 0);

    // a job runs, neither point on a lot: answered with nothing appended, no game call, no QL
    g_async.state = A_RUNNING, g_twArmed = 1;
    call();
    assert(origCalls == 4 && k.epTaken == 1 && k.epNoLot == 1 && k.epJob == 1 && count() == 0);
    // a lot whose key Contains refuses: an empty gather under QL, answered
    g_lotOn = true;
    call();
    assert(origCalls == 4 && k.epTaken == 2 && k.epNoLot == 1 && count() == 0);

    // fallbacks: the game's call, counted by reason
    {
        const LONG o0 = origCalls;
        kc.rgOwnerTid = (LONG)me;  // the sim owns +0x918 (lent or recursive): the game's path
        call();
        assert(origCalls == o0 + 1 && k.epFb[EP_FB_MAYNOT] == 1);
        kc.rgOwnerTid = 0;
        g_twArmed = 0;  // the job ended before the clone (tripwires disarmed)
        call();
        assert(origCalls == o0 + 2 && k.epFb[EP_FB_JOBEND] == 1);
        g_twArmed = 1;
        AcquireSRWLockExclusive(&g_ql);  // a writer holds QL: try only
        call();
        assert(origCalls == o0 + 3 && k.epFb[EP_FB_TRY] == 1);
        ReleaseSRWLockExclusive(&g_ql);
        InterlockedExchange(&g_twActive, 1);  // a tripwire writer runs
        call();
        assert(origCalls == o0 + 4 && k.epFb[EP_FB_TRIP] == 1);
        InterlockedExchange(&g_twActive, savedActive);
        *(u8**)(g_world + 0x95C) = NULL;  // no planner
        call();
        assert(origCalls == o0 + 5 && k.epFb[EP_FB_SHAPE] == 1);
        *(u8**)(g_world + 0x95C) = g_planner;
        g_wm[0x1CC] = 1;  // the lot index is dirty: World_LotLookup would rebuild it
        call();
        assert(origCalls == o0 + 6 && k.epFb[EP_FB_LOT] == 1);
        g_wm[0x1CC] = 0;
        k.epMode = 0;  // switch off with a job: the game's call, nothing counted
        const LONG t0 = k.epTaken;
        call();
        assert(origCalls == o0 + 7 && k.epTaken == t0 && k.epJob == 8);
        assert(count() == 0 && t_qlShared == 0 && !t_inRo && g_srInClone == 0);
    }

    // docs/670 §14 StartGuard: with no job the game's call runs inside a g_rgOffSimOpen frame (no job can start under it)
    {
        k.epMode = 1, g_async.state = A_IDLE, g_twArmed = 0, g_lotOn = true;
        LONG o0 = origCalls;
        origOpen = -1;
        call();
        assert(origCalls == o0 + 1 && origOpen == 0 && k.epGuardCalls == 0);  // switch off: no frame
        k.epGuard = 1;
        call();
        assert(origCalls == o0 + 2 && origOpen == 1 && k.epGuardCalls == 1 && g_rgOffSimOpen == 0 && k.epGuardRace == 0);
        // Retry: a job-end drop is run again; the tripwires stay disarmed: kEpTries passes, then the game's call
        k.epRetry = 1, g_async.state = A_RUNNING;
        LONG fb0 = k.epFb[EP_FB_JOBEND];
        lookups = 0, armAt = idleAt = 0;
        o0 = origCalls;
        call();
        assert(origCalls == o0 + 1 && k.epRetryN == 3 && k.epRetryOk == 0 && k.epFb[EP_FB_JOBEND] == fb0 + 1 && origOpen == 0);
        // the next job is armed during the first pass: the second pass answers, no game call
        lookups = 0, armAt = 4;  // three lookups a pass
        LONG t0 = k.epTaken;
        call();
        assert(origCalls == o0 + 1 && k.epRetryN == 4 && k.epRetryOk == 1 && k.epTaken == t0 + 1 && k.epFb[EP_FB_JOBEND] == fb0 + 1);
        // the job ends during the first pass and none follows: the rerun is the game's call inside the guard frame, no wait booked
        g_twArmed = 0, lookups = 0, armAt = 0, idleAt = 1;
        const LONG g0 = k.epGameUs;
        call();
        assert(origCalls == o0 + 2 && origOpen == 1 && k.epRetryN == 5 && k.epRetryOk == 2 && k.epGuardCalls == 2 && k.epGameUs == g0 &&
               k.epFb[EP_FB_JOBEND] == fb0 + 1 && g_rgOffSimOpen == 0);
        // QL busy with Retry: reruns, then the game's call, counted once
        g_async.state = A_RUNNING, g_twArmed = 1, idleAt = 0;
        fb0 = k.epFb[EP_FB_TRY];
        AcquireSRWLockExclusive(&g_ql);
        call();
        ReleaseSRWLockExclusive(&g_ql);
        assert(origCalls == o0 + 3 && k.epFb[EP_FB_TRY] == fb0 + 1 && k.epRetryN >= 5 && k.epRetryN <= 8 && t_qlShared == 0 && !t_inRo);
        k.epGuard = k.epRetry = 0, g_lotOn = true, lookups = armAt = idleAt = 0;
    }

    // shadow with no job: the game's ids are kept, the clone compared; a skew is one mismatch
    k.epShadow = 1, g_async.state = A_IDLE, g_twArmed = 0;
    const LONG o0 = origCalls;
    call();
    assert(origCalls == o0 + 1 && k.epShadowRuns == 1 && k.epShadowMismatch == 0 && g_rgOffSimOpen == 0);
    origPush = 2;  // the game appends two ids the clone does not
    call();
    assert(origCalls == o0 + 2 && k.epShadowRuns == 2 && k.epShadowMismatch == 1 && count() == 2 && g_rgOffSimOpen == 0);
    printf("EditPathRO ([EditPathRO] Enabled=1): export layout; switch off / other thread / exclude vector / no job pass; job: no-lot "
           "and empty-gather answers without the game's call; fallbacks (+0x918 owner, job end, QL busy, tripwire, no planner, dirty "
           "lot index, switch off) call the game; StartGuard frame; Retry (job end, next job armed, job gone, QL busy); shadow compare + skew\n");

    memset(&k, 0, sizeof(k));
    g_epOrig = savedOrig, g_srConv = savedConv, g_srLotLookup = savedLookup, g_srIdPush = savedPush, g_srOne = savedOne;
    g_async = savedAsync, g_twArmed = savedArmed, kc.rgOwnerTid = savedOwner;
}
