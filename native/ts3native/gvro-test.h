// --- [GoalValidRO] (gvro.cpp, docs/178 §7 T6): layout; the call-site stub on fakes (the game's 0x82EDB0, the lot lookup and
// S2's point query replaced): pass-through rules, empty answers, footprints = the game's call, fallbacks, shadow compare.
// Included by selftest.cpp.
namespace gvt {
static LONG origCalls, origRet, origFill, qCalls, qWhy, qN, lookups;
static DWORD qLo, qHi, qOpt;
static LONG qLevel;
static float qX, qZ, qR, lkX, lkZ;
static u8 lot[0x50];
static bool noLot;
static DWORD fill[4];
static DWORD __fastcall Orig(u8*, void*, const float*, float, DWORD* vec, DWORD, DWORD) {
    origCalls++;
    if (origFill) vec[0] = (DWORD)(uintptr_t)fill, vec[1] = (DWORD)(uintptr_t)(fill + origFill);  // the game's copy
    return (DWORD)origRet | 0xABCD00;
}
static u8* __fastcall Lookup(void*, void*, float x, float z) {
    lookups++;
    lkX = x, lkZ = z;
    return noLot ? NULL : lot;
}
static int Query(SrCtx* c, const u8*, DWORD lo, DWORD hi, const SrPt& pt, float r, const u8* rec) {
    qCalls++;
    qLo = lo, qHi = hi, qLevel = pt.level, qX = pt.v[0], qZ = pt.v[2], qR = r, qOpt = *(const DWORD*)rec;
    assert(rec[0x24] == 1 && !*(const DWORD*)(rec + 8) && !*(const DWORD*)(rec + 0x28));  // 0x82EDB0's record
    c->n = qN;
    return qWhy;
}
}  // namespace gvt

static void TestGoalValidRO() {
    using namespace gvt;
    Ts3nCounters& k = ts3native_counters;
    static_assert(offsetof(Ts3nCounters, gvReady) == 0x47DC && offsetof(Ts3nCounters, gvMode) == 0x47E0 &&
                      offsetof(Ts3nCounters, gvShadow) == 0x47E4 && offsetof(Ts3nCounters, gvFb) == 0x47FC &&
                      offsetof(Ts3nCounters, gvShadowEmpty) == 0x4820 && sizeof(Ts3nCounters) == 5941 * 4,
                  "counters v118 layout after v117 (docs/registry.md)");
    assert(k.version == 912);
    assert(kGvSite.site == 0x0075A647 && kGvSite.target == 0x0082EDB0);
    memset(&k.gvReady, 0, sizeof(Ts3nCounters) - offsetof(Ts3nCounters, gvReady));
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
    const GvFn savedOrig = g_gvOrig;
    const GvQueryFn savedQuery = g_gvQuery;
    const SrLotLookupFn savedLookup = g_srLotLookup;
    const AsyncSlot savedAsync = g_async;
    const LONG savedArmed = g_twArmed, savedOwner = k.rgOwnerTid;
    const float* savedOne = g_srOne;
    static const float one = 1.0f;
    g_gvOrig = Orig, g_gvQuery = Query, g_srLotLookup = Lookup, g_srOne = &one;
    const DWORD me = GetCurrentThreadId();
    g_async.owner = me;
    k.rgOwnerTid = 0;
    // the world: planner at +0x95C, world manager at +0x964 (+0x1CC = lot index dirty); the point: x, y, z, ..., level at +0x24
    static u8 world[0x980], planner[0x10], wm[0x200];
    *(u8**)(world + 0x95C) = planner, *(u8**)(world + 0x964) = wm;
    *(DWORD*)(lot + 0x48) = 0x30, *(DWORD*)(lot + 0x4C) = 0x1;
    __declspec(align(16)) float pt[13] = {3.0f, 9.0f, 5.0f};
    *(LONG*)&pt[9] = 2;
    DWORD vec[4] = {0, 0, 0, 0xFED2FC};
    auto call = [&](DWORD mode = 0) {
        vec[0] = vec[1] = 0;
        return GvStub(world, NULL, pt, 0.1f, vec, mode, 0xE00);
    };

    // switches off / another thread: the game's call, nothing counted
    origCalls = 0, origRet = 1;
    assert((u8)call() == 1 && origCalls == 1 && k.gvCalls == 0);
    k.gvMode = 1, g_async.owner = me + 1, g_async.state = A_RUNNING;
    assert((u8)call() == 1 && origCalls == 2 && k.gvCalls == 0);
    g_async.owner = me;

    // a job runs, nothing near the point: al 1, the vector untouched, no game call; the query sees the frame's inputs
    g_twArmed = 1, qWhy = SR_OK, qN = 0, qCalls = 0;
    assert(call() == 1 && vec[0] == 0 && vec[1] == 0 && origCalls == 2 && k.gvTaken == 1 && k.gvNoLot == 0);
    assert(lookups == 1 && lkX == 3.0f && lkZ == 5.0f);
    assert(qCalls == 1 && qLo == 0x30 && qHi == 1 && qLevel == 2 && qX == 3.0f && qZ == 5.0f && qR == 0.1f && qOpt == 0xE00);
    noLot = true;  // no lot at the point: al 0 (0x82EE4D), no query
    assert(call() == 0 && qCalls == 1 && k.gvTaken == 2 && k.gvNoLot == 1 && origCalls == 2);
    noLot = false;

    // footprints near the point: the hit loop needs the game's refs
    qN = 2;
    assert(call() == 0xABCD01 && origCalls == 3 && k.gvFb[GV_FB_HITS] == 1 && k.gvTaken == 2);
    qN = 0;

    // fallbacks: the game's call, counted by reason
    struct Fb { const char* name; int why; int idx; };
    static const Fb fbs[] = {{"deferred flush", SR_FB_DEFERRED, GV_FB_LOT}, {"lot miss", SR_FB_LOTMISS, GV_FB_LOT},
                             {"shape", SR_FB_SHAPE, GV_FB_SHAPE}, {"tripwire", SR_FB_TRIP, GV_FB_TRIP}};
    origRet = 0;
    for (const Fb& f : fbs) {
        qWhy = f.why;
        const LONG o0 = origCalls, f0 = k.gvFb[f.idx];
        if (call() != 0xABCD00 || origCalls != o0 + 1 || k.gvFb[f.idx] != f0 + 1) {
            printf("GoalValidRO fallback '%s' not taken\n", f.name);
            assert(false);
        }
    }
    qWhy = SR_OK;
    {
        const LONG o0 = origCalls, s0 = k.gvFb[GV_FB_STATE];
        wm[0x1CC] = 1;  // dirty lot index: the lookup would rebuild it
        assert(call() == 0xABCD00 && origCalls == o0 + 1 && k.gvFb[GV_FB_LOT] == 3);
        wm[0x1CC] = 0;
        assert(call(1) == 0xABCD00 && origCalls == o0 + 2 && k.gvFb[GV_FB_STATE] == s0 + 1);  // mode 1: not this call site
        vec[0] = 0x10, vec[1] = 0x14;  // a vector with a range: the game would release it first (a write)
        assert(GvStub(world, NULL, pt, 0.1f, vec, 0, 0xE00) == 0xABCD00 && origCalls == o0 + 3 && k.gvFb[GV_FB_STATE] == s0 + 2);
        k.rgOwnerTid = (LONG)me;  // the sim owns +0x918 (lent or recursive): the game's path
        assert(call() == 0xABCD00 && origCalls == o0 + 4 && k.gvFb[GV_FB_STATE] == s0 + 3);
        k.rgOwnerTid = 0;
        g_twArmed = 0;  // the job ended before the clone (tripwires disarmed)
        assert(call() == 0xABCD00 && origCalls == o0 + 5 && k.gvFb[GV_FB_JOBEND] == 1);
        g_twArmed = 1;
        AcquireSRWLockExclusive(&g_ql);  // a worker W frame holds QL: try only
        assert(call() == 0xABCD00 && origCalls == o0 + 6 && k.gvFb[GV_FB_TRY] == 1);
        ReleaseSRWLockExclusive(&g_ql);
        k.gvMode = 0;  // switch off with a job: the game's call
        assert(call() == 0xABCD00 && origCalls == o0 + 7);
    }
    assert(!t_qlShared && !t_inRo && !g_srInClone);

    // shadow with no job: the game's answer is returned, the clone compared (al and footprint count); skews count
    k.gvShadow = 1, g_async.state = A_IDLE, g_twArmed = 0;
    origRet = 1, origFill = 2, qN = 2;
    const LONG o0 = origCalls;
    assert(call() == 0xABCD01 && origCalls == o0 + 1 && k.gvShadowRuns == 1 && k.gvShadowMismatch == 0 && k.gvShadowEmpty == 0 &&
           g_rgOffSimOpen == 0);
    qN = 1;  // count skew
    assert(call() == 0xABCD01 && k.gvShadowRuns == 2 && k.gvShadowMismatch == 1);
    origFill = 0, qN = 0;  // both empty
    assert(call() == 0xABCD01 && k.gvShadowRuns == 3 && k.gvShadowMismatch == 1 && k.gvShadowEmpty == 1);
    origRet = 0, noLot = true;  // both: no lot
    assert(call() == 0xABCD00 && k.gvShadowRuns == 4 && k.gvShadowMismatch == 1 && k.gvShadowEmpty == 2);
    noLot = false;  // game al 0, clone found a lot: skew
    assert(call() == 0xABCD00 && k.gvShadowRuns == 5 && k.gvShadowMismatch == 2);
    // shortcut: no threaded case here; the QL protocol is S2's (steerro-test, obstructro and rtoro threaded runs)
    printf("GoalValidRO ([GoalValidRO] Enabled=1): counters v118; switch off / other thread pass; job: empty answer (al 1, vector "
           "untouched) and no-lot answer (al 0) with the frame's point, level, radius and option word, no game call; footprints "
           "found, fallbacks (deferred, lot miss, shape, tripwire, dirty lot index, mode, non-empty vector, +0x918 owner, job end, "
           "QL busy, switch off) call the game; shadow compare + count and al skews\n");

    k.gvMode = k.gvShadow = 0;
    g_gvOrig = savedOrig, g_gvQuery = savedQuery, g_srLotLookup = savedLookup, g_srOne = savedOne;
    g_async = savedAsync, g_twArmed = savedArmed, k.rgOwnerTid = savedOwner;
}
