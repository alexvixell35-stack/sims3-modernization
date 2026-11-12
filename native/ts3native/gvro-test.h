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
                      offsetof(Ts3nCounters, gvShadowEmpty) == 0x4820 && sizeof(Ts3nCounters) == 6294 * 4,
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

// --- [GoalValidRO] Attr (docs/250 §7.0, counters v174): source / scope split of the job-time calls and their fallback waits,
// the moved-wait meter from the last fallback to the plan's end (UserDiag op 12), calls inside AsyncStart, worker calls ignored
static void TestGvAttr() {
    using namespace gvt;
    Ts3nCounters& k = ts3native_counters;
    static_assert(offsetof(Ts3nCounters, gvaMode) == 0x5E4C && offsetof(Ts3nCounters, gvaSrcN) == 0x5E50 &&
                      offsetof(Ts3nCounters, gvaSrcWaitUs) == 0x5E68 && offsetof(Ts3nCounters, gvaScopeWaitUs) == 0x5E80 &&
                      offsetof(Ts3nCounters, gvaAtStart) == 0x5E8C && offsetof(Ts3nCounters, gvaLaterTakeUs) == 0x5E90,
                  "counters v174 layout after v173 (docs/registry.md)");
    memset((void*)&k.gvaMode, 0, offsetof(Ts3nCounters, gvaLaterTakeUs) + 4 - offsetof(Ts3nCounters, gvaMode));
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
    const GvFn savedOrig = g_gvOrig;
    const AsyncSlot savedAsync = g_async;
    const LONG w0 = g_w918Us, gvM = k.gvMode, gvS = k.gvShadow;
    g_gvOrig = Orig;
    const DWORD me = GetCurrentThreadId();
    static u8 world[0x980];
    __declspec(align(16)) float pt[13] = {1.0f, 0.0f, 2.0f};
    DWORD vec[4] = {0, 0, 0, 0xFED2FC};
    k.gvMode = k.gvShadow = 0;  // the RO answer off: every job-time call is the game's (the wait)
    g_async.owner = me, g_async.state = A_RUNNING;
    origFill = 0, origCalls = 0;
    // off: nothing counted, the op 35 query says off
    assert(GvaSrcMsg(0xFFFF) == 0);
    GvStub(world, NULL, pt, 0.1f, vec, 0, 0);
    assert(origCalls == 1 && k.gvaSrcN[0] == 0 && k.gvaSrcWaitUs[0] == 0);
    k.gvaMode = 1;
    assert(GvaSrcMsg(0xFFFF) == 1);
    // a watchdog check of an unseen NPC (class 1, scope 2): n and wait by class / scope; job waits after it go to the meter
    assert(GvaSrcMsg(1 | 2 << 8) == 1);
    GvStub(world, NULL, pt, 0.1f, vec, 0, 0);
    assert(origCalls == 2 && k.gvaSrcN[1] == 1 && k.gvaSrcWaitUs[1] >= 0 && g_gvaOpen);
    g_w918Us += 5000;  // the same plan waits 5 ms later (gather, gate 10, RunPlan)
    GvaPlanEnd();
    assert(k.gvaLaterTakeUs == 5000 && !g_gvaOpen && g_gvaSrc == 0 && g_gvaScope == 0);
    // a household plan (scope 0, the reset after op 12) and an own-task plan: separate rows; no fallback = no meter
    assert(GvaSrcMsg(3) == 1);
    GvStub(world, NULL, pt, 0.1f, vec, 0, 0);
    assert(k.gvaSrcN[3] == 1 && k.gvaSrcN[1] == 1);
    GvaPlanEnd();
    assert(k.gvaLaterTakeUs == 5000);  // no later wait: nothing added
    // out-of-range class / scope fall to 0 (none / household): never an unseen row by accident
    assert(GvaSrcMsg(9 | 7 << 8) == 1 && g_gvaSrc == 0 && g_gvaScope == 0);
    // no job: not counted (no wait there); inside AsyncStart: counted as at-start
    g_async.state = A_IDLE;
    g_asInStart = true;
    GvStub(world, NULL, pt, 0.1f, vec, 0, 0);
    g_asInStart = false;
    assert(k.gvaAtStart == 1 && k.gvaSrcN[0] == 0);
    // another thread (the worker inside its job): ignored
    g_async.state = A_RUNNING, g_async.owner = me + 1;
    GvStub(world, NULL, pt, 0.1f, vec, 0, 0);
    assert(k.gvaSrcN[0] == 0 && !g_gvaOpen);
    printf("GoalValidRO Attr (gvattr): counters v174; off counts nothing; class / scope rows and fallback waits; the later-wait "
           "meter closes at the plan's end; bad codes fall to none / household; at-start; no job or the worker: not counted\n");
    GvaPlanEnd();
    k.gvaMode = 0, k.gvMode = gvM, k.gvShadow = gvS;
    g_w918Us = w0;
    g_gvOrig = savedOrig, g_async = savedAsync;
}

// --- docs/250 §13.3 / §12.3 (counters v189): gvaNoLater* (no routing-lock acquire after the plan's last validator fallback)
// and gvaWhyWaitUs (the waits by the SyncIc reason in force)
static void TestGvAttrNoLater() {
    using namespace gvt;
    Ts3nCounters& k = ts3native_counters;
    static_assert(offsetof(Ts3nCounters, sbVtGiveUp) == 0x5F80 && offsetof(Ts3nCounters, gvuMode) == 0x6018 &&
                      offsetof(Ts3nCounters, gvuMaxMs) == 0x601C && offsetof(Ts3nCounters, gvuAsks) == 0x6020 &&
                      offsetof(Ts3nCounters, gvuN) == 0x6024 && offsetof(Ts3nCounters, gvuNo) == 0x6028 &&
                      offsetof(Ts3nCounters, gvuKind) == 0x6038 && offsetof(Ts3nCounters, gvaPlansN) == 0x6048 &&
                      offsetof(Ts3nCounters, gvaNoLaterN) == 0x604C && offsetof(Ts3nCounters, gvaNoLaterUs) == 0x6050 &&
                      offsetof(Ts3nCounters, gvaWhyWaitUs) == 0x6054 && offsetof(Ts3nCounters, gvuSpare) == 0x60A4 &&
                      sizeof(Ts3nCounters) == 6294 * 4 && GVA_WHY_NONE == 19 &&
                      sizeof(kRfbName) / sizeof(kRfbName[0]) == GVA_WHY_NONE,
                  "counters v189 layout after v188 (docs/registry.md)");
    k.gvaPlansN = k.gvaNoLaterN = k.gvaNoLaterUs = 0;
    memset(k.gvaWhyWaitUs, 0, sizeof(k.gvaWhyWaitUs));
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
    const GvFn savedOrig = g_gvOrig;
    const AsyncSlot savedAsync = g_async;
    const LONG gvM = k.gvMode, gvS = k.gvShadow, gvA = k.gvaMode, why0 = g_gvaWhy;
    g_gvOrig = Orig;
    static u8 world[0x980];
    __declspec(align(16)) float pt[13] = {1.0f, 0.0f, 2.0f};
    DWORD vec[4] = {0, 0, 0, 0xFED2FC};
    k.gvMode = k.gvShadow = 0, k.gvaMode = 1;
    g_async.owner = GetCurrentThreadId(), g_async.state = A_RUNNING;
    origFill = 0;
    // plan 1, under SyncIc reason NoYield: two validator waits, then no later acquire -> a no-later plan with both waits
    assert(GvaSrcMsg(3 | 2 << 8) == 1 && !g_gvaWatch && g_gvaPlanUs == 0);
    g_gvaWhy = RFB_NOYIELD;
    GvStub(world, NULL, pt, 0.1f, vec, 0, 0);
    GvStub(world, NULL, pt, 0.1f, vec, 0, 0);
    assert(g_gvaWatch && g_gvaOpen && g_gvaTakes0 == g_gvaTakes);
    const LONG planUs = g_gvaPlanUs;
    GvaPlanEnd();
    assert(k.gvaPlansN == 1 && k.gvaNoLaterN == 1 && k.gvaNoLaterUs == planUs && !g_gvaWatch && g_gvaPlanUs == 0 &&
           k.gvaWhyWaitUs[RFB_NOYIELD] == planUs);
    // plan 2, not under SyncIc: a later routing-lock acquire on the owner thread (gather, gate 10, PlanLocked; what
    // LockAcquireHook adds while g_gvaWatch is set): a plan, not a no-later one
    assert(GvaSrcMsg(3) == 1);
    g_gvaWhy = GVA_WHY_NONE;
    GvStub(world, NULL, pt, 0.1f, vec, 0, 0);
    g_gvaTakes++;
    GvaPlanEnd();
    assert(k.gvaPlansN == 2 && k.gvaNoLaterN == 1 && k.gvaNoLaterUs == planUs && k.gvaWhyWaitUs[RFB_NOYIELD] == planUs);
    // a plan with no fallback: not counted
    assert(GvaSrcMsg(3) == 1);
    GvaPlanEnd();
    assert(k.gvaPlansN == 2 && !g_gvaWatch);
    // an out-of-range reason books as "none"
    assert(GvaSrcMsg(3) == 1);
    const LONG none0 = k.gvaWhyWaitUs[GVA_WHY_NONE];
    g_gvaWhy = 77;
    GvStub(world, NULL, pt, 0.1f, vec, 0, 0);
    assert(k.gvaWhyWaitUs[GVA_WHY_NONE] >= none0 && g_gvaOpen);
    GvaPlanEnd();
    printf("GoalValidRO Attr no-later / why (gvattr, counters v189): a plan with no routing-lock acquire after its last "
           "fallback books its validator waits as no-later, one with a later acquire only as a plan, none without a fallback; "
           "waits split by the SyncIc reason (19 = not under SyncIc, also for a bad reason)\n");
    k.gvMode = gvM, k.gvShadow = gvS, k.gvaMode = gvA, g_gvaWhy = why0;
    g_gvOrig = savedOrig, g_async = savedAsync;
}

// --- docs/250 §7.3 B2 [GoalValidRO] UnseenDefer (switch gvunseen, counters v189): DEFER? from ts3fixes' marked sync plans
namespace gvut {
static u8 g_route[0x200];
static u8* __fastcall Route(const void*, void*, DWORD handle) { return handle == 0x25000011 ? g_route : NULL; }
static DWORD g_now;
static DWORD WINAPI Now() { return g_now; }
static void Unseen(DWORD h) { g_ufH = h, g_ufCutH = 0, g_kdScopeH = h, g_kdScope = 2; }  // MARKed, scope op 11 (unseen)
}  // namespace gvut
static void TestGvUnseen() {
    using namespace gvut;
    Ts3nCounters& k = ts3native_counters;
    const AsyncSlot savedAsync = g_async;
    const DWORD ufH0 = g_ufH, cutH0 = g_ufCutH, kdH0 = g_kdScopeH;
    const LONG kd0 = g_kdScope, ufMode0 = k.ufMode, ufReady0 = k.ufReady;
    const OrRouteFn route0 = g_orRoute;
    u8* const* const rmSlot0 = g_orRmSlot;
    static u8* rmPtr = (u8*)1;  // any non-null table: the fake lookup ignores it
    g_orRmSlot = &rmPtr, g_orRoute = Route, g_gvuNow = Now;
    k.gvuMode = 0, k.gvuMaxMs = 2000, k.gvuAsks = k.gvuN = 0;
    memset(k.gvuNo, 0, sizeof(k.gvuNo));
    memset(k.gvuKind, 0, sizeof(k.gvuKind));
    memset(g_gvu, 0, sizeof(g_gvu));
    const DWORD me = GetCurrentThreadId(), hU = 0x25000001, hJob = 0x25000099;
    k.ufMode = 1, k.ufReady = 15;
    g_async.owner = me, g_async.state = A_RUNNING, g_async.lock = (void*)0x1918, g_async.handle = hJob;
    g_now = 1000;
    // the switch off: nothing counted, nothing touched
    Unseen(hU);
    assert(GvuMsg(hU, 0) == 0 && k.gvuAsks == 0 && g_ufH == hU && !g_ufCutH);
    k.gvuMode = 1;
    // unseen + a job of another route holds +0x918 (lock held): deferred; the mark used up, CUT? answers 1 once
    assert(GvuMsg(hU, 0) == 1 && k.gvuN == 1 && k.gvuKind[0] == 1 && k.gvuAsks == 1 && !g_ufH && g_ufCutH == hU);
    assert(UfMsg(hU, 2) == 1 && UfMsg(hU, 2) == 0);
    // household / selected: never marked by ts3fixes, scope op 10 -> 1: not deferred, the plan runs as today
    g_ufH = 0, g_kdScopeH = hU, g_kdScope = 1;
    assert(GvuMsg(hU, 0) == 0 && k.gvuNo[GVU_SCOPE] == 1 && k.gvuN == 1);
    // unknown scope (no scope message) even with a mark: household rules
    g_ufH = hU, g_kdScopeH = 0, g_kdScope = 0;
    assert(GvuMsg(hU, 0) == 0 && k.gvuNo[GVU_SCOPE] == 2 && g_ufH == hU);
    // a seen NPC (scope 3, no mark): not deferred
    g_ufH = 0, g_kdScopeH = hU, g_kdScope = 3;
    assert(GvuMsg(hU, 0) == 0 && k.gvuNo[GVU_SCOPE] == 3);
    // the mark or the scope of another route: not deferred
    Unseen(hU), g_ufH = 0x25000002;
    assert(GvuMsg(hU, 0) == 0 && k.gvuNo[GVU_SCOPE] == 4);
    Unseen(hU), g_kdScopeH = 0x25000002;
    assert(GvuMsg(hU, 0) == 0 && k.gvuNo[GVU_SCOPE] == 5);
    // a bad kind, no handle
    Unseen(hU);
    assert(GvuMsg(hU, 4) == 0 && GvuMsg(0, 0) == 0 && k.gvuNo[GVU_SCOPE] == 7);
    // the unseen fallback off: nothing would answer the plan
    k.ufMode = 0;
    assert(GvuMsg(hU, 0) == 0 && k.gvuNo[GVU_NOFB] == 1 && g_ufH == hU);
    k.ufMode = 1;
    // lock free: no job, the job is this route's own, another thread, no lock: the plan runs (no wait to save)
    g_async.state = A_IDLE;
    assert(GvuMsg(hU, 0) == 0 && k.gvuNo[GVU_FREE] == 1 && g_ufH == hU && !g_ufCutH);
    g_async.state = A_RUNNING, g_async.handle = hU;
    assert(GvuMsg(hU, 0) == 0 && k.gvuNo[GVU_FREE] == 2);
    g_async.handle = hJob, g_async.owner = me + 1;
    assert(GvuMsg(hU, 0) == 0 && k.gvuNo[GVU_FREE] == 3);
    g_async.owner = me, g_async.lock = NULL;
    assert(GvuMsg(hU, 0) == 0 && k.gvuNo[GVU_FREE] == 4);
    g_async.lock = (void*)0x1918;
    // the four plan ICs: each kind deferred and counted; Replan bumps the IC's own replan count (route+0x1F4) once
    const DWORD hR = 0x25000011;
    *(DWORD*)(g_route + 0x1F4) = 7;
    Unseen(hR);
    assert(GvuMsg(hR, 1) == 1 && k.gvuKind[1] == 1 && *(DWORD*)(g_route + 0x1F4) == 8);
    Unseen(hR);
    assert(GvuMsg(hR, 2) == 1 && k.gvuKind[2] == 1 && *(DWORD*)(g_route + 0x1F4) == 8);
    Unseen(hR);
    assert(GvuMsg(hR, 3) == 1 && k.gvuKind[3] == 1 && *(DWORD*)(g_route + 0x1F4) == 8);
    // timeout: a streak of 2000 ms, then the plan runs as vanilla (not deferred, the mark stays for its sync IC)
    memset(g_gvu, 0, sizeof(g_gvu));
    g_now = 5000;
    Unseen(hU);
    assert(GvuMsg(hU, 0) == 1);
    g_now = 6500;
    Unseen(hU);
    assert(GvuMsg(hU, 0) == 1);  // 1.5 s into the streak
    g_now = 7000;
    Unseen(hU);
    assert(GvuMsg(hU, 0) == 0 && k.gvuNo[GVU_STREAK] == 1 && g_ufH == hU && !g_ufCutH);
    // the next ask starts a new streak (one vanilla plan between streaks)
    Unseen(hU);
    assert(GvuMsg(hU, 0) == 1);
    // a streak whose last deferral is gvuMaxMs old is a new streak
    g_now = 7100;
    Unseen(hU);
    assert(GvuMsg(hU, 0) == 1);
    g_now = 7100 + 2500;
    Unseen(hU);
    assert(GvuMsg(hU, 0) == 1);
    // a harness-written gvuMaxMs of 0 is clamped at use to 100 ms
    k.gvuMaxMs = 0;
    g_now += 50;
    Unseen(hU);
    assert(GvuMsg(hU, 0) == 1);
    g_now += 60;
    Unseen(hU);
    assert(GvuMsg(hU, 0) == 0 && k.gvuNo[GVU_STREAK] == 2);
    k.gvuMaxMs = 2000;
    // 33 routes in 32 slots: the stalest slot is reused
    for (DWORD i = 0; i < GVU_SLOTS + 1; i++) {
        g_now += 1;
        Unseen(0x25001000 + i);
        assert(GvuMsg(0x25001000 + i, 0) == 1);
    }
    bool found = false;
    for (int i = 0; i < GVU_SLOTS; i++) found |= g_gvu[i].h == 0x25001000 + GVU_SLOTS;
    assert(found);
    printf("GoalValidRO UnseenDefer (gvunseen, counters v189): off counts nothing; an unseen marked plan of each plan IC is "
           "deferred while another job holds +0x918 (CUT? 1, Replan's count bumped); household, selected, unknown and seen-NPC "
           "scopes, the fallback off, a free lock / own job / other thread: not deferred; a streak past UnseenDeferMaxMs runs "
           "as vanilla, then a new streak; 33 routes in 32 slots\n");
    k.gvuMode = 0, k.ufMode = ufMode0, k.ufReady = ufReady0;
    g_async = savedAsync, g_ufH = ufH0, g_ufCutH = cutH0, g_kdScopeH = kdH0, g_kdScope = kd0;
    g_orRoute = route0, g_orRmSlot = rmSlot0, g_gvuNow = GetTickCount;
    memset(g_gvu, 0, sizeof(g_gvu));
    g_gvuLines = 0;
}
