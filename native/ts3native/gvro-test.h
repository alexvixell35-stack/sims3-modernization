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
                      offsetof(Ts3nCounters, gvShadowEmpty) == 0x4820 && sizeof(Ts3nCounters) == 6866 * 4,
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
                      sizeof(Ts3nCounters) == 6866 * 4 && GVA_WHY_NONE == 19 &&
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

// --- [ObstructAtRO] (gvro.cpp, docs/670 §21-§22): OaRun and OaHook on fakes (world, lot lookups, level, S2's point query, the
// footprints' slots +0xC / +0x84, the route table, the game's 0x655880). The lot lookup of the transform's gate (self+0xC) and
// the query's own (world+0x964) hand out different lots, so each id is seen where it is used. No timing assert; every
// global it reads is set here and put back. steerro-test section OA runs the same code against the game's function.
namespace oat {
static LONG origCalls, qCalls, qWhy, qN, levels, lookA, lookB;
static u64 origRet;
static DWORD qLo, qHi, qOpt;
static LONG qLevel;
static float qR;
static const u8* qObjs[6];
static u8 lot[0x50], qlot[0x50], wm[0x200], wmw[0x200];
static u8 route[0x1000];  // the job's route: OaRun takes a list within 0x1000 bytes of it as the route's own
static bool noLot, noQlot;
static u64 __fastcall Orig(u8*, void*, const u8*, float, float, float, DWORD) {
    origCalls++;
    return origRet;
}
static u8* __fastcall Lookup(void* m, void*, float x, float z) {
    assert(x == 3.0f && z == 5.0f && (m == wm || m == wmw));
    if (m == wm) { lookA++; return noLot ? NULL : lot; }
    lookB++;
    return noQlot ? NULL : qlot;
}
static void __cdecl Level(const u8* l, const float* pt, float* io) {
    assert(l == lot && ((uintptr_t)pt & 15) == 0 && pt[0] == 3.0f && pt[1] == 9.0f && pt[2] == 5.0f && pt[3] == 3.0f);  // {x, y, z, x}
    levels++;
    *(LONG*)&io[2] = 2;
}
static int Query(SrCtx* c, const u8*, DWORD lo, DWORD hi, const SrPt& pt, float r, const u8* rec) {
    qCalls++;
    qLo = lo, qHi = hi, qLevel = pt.level, qR = r, qOpt = *(const DWORD*)rec;
    assert(pt.v[0] == 3.0f && pt.v[2] == 5.0f && pt.v[3] == 3.0f && pt.one == 1.0f);
    assert(rec[0x24] == 1 && !*(const DWORD*)(rec + 8) && !*(const DWORD*)(rec + 0xC) && !*(const DWORD*)(rec + 0x28));  // 0x82EDB0's record
    c->n = qN;
    for (int i = 0; i < qN; i++) c->objs[i] = qObjs[i];
    return qWhy;
}
static int __fastcall Type(const u8* o, void*) { return o[0x35]; }
static unsigned __int64 __fastcall Id(const u8* o, void*) { return (u64)*(const DWORD*)(o + 0x18) << 32 | *(const DWORD*)(o + 0x14); }
static u8* __fastcall Route(const void*, void*, DWORD handle) { return handle == 0x25000021 ? route : NULL; }
}  // namespace oat

static void TestObstructAtRO() {
    using namespace oat;
    ObstructAtCounters& k = ts3native_obstructat;
    static_assert(offsetof(ObstructAtCounters, oaShadow) == 4 && offsetof(ObstructAtCounters, oaTaken) == 0x10 &&
                      offsetof(ObstructAtCounters, oaFb) == 0x20 && offsetof(ObstructAtCounters, oaShadowRuns) == 0x40 &&
                      sizeof(ObstructAtCounters) == 0x48,
                  "ts3native_obstructat layout (docs/registry.md)");
    assert(OA_FN == TS3A(0x00655880));
    Ts3nCounters& kc = ts3native_counters;
    const ObstructAtCounters savedK = k;
    memset(&k, 0, sizeof(k));
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
    const OaFn savedOrig = g_oaOrig;
    const OrQueryFn savedQuery = g_orQuery;
    const OrLevelFn savedLevel = g_orLevel;
    const OrRouteFn savedRoute = g_orRoute;
    u8* const* savedRm = g_orRmSlot;
    const float* savedRadius = g_orRadius;
    const SrWorldFn savedWorld = g_srWorldGet;
    const SrLotLookupFn savedLookup = g_srLotLookup;
    const SrLotFindFn savedFind = g_srLotFind;
    const float* savedOne = g_srOne;
    const AsyncSlot savedAsync = g_async;
    const LONG savedArmed = g_twArmed, savedOwner = kc.rgOwnerTid, savedActive = g_twActive, savedBroken = g_steerBroken;
    const LONG savedRgReaders = t_rg.readers;
    const int savedInvoke = g_inInvoke, savedRgN = t_rg.n;
    assert(!t_qlDepth && !t_qlShared && !t_inRo && !g_srInClone && !g_rgOffSimOpen);  // no clone or shadow open from an earlier case
    static const float one = 1.0f, radius = 0.25f;
    static u8 world[0x980], planner[0x200], self[0x10], rm[0x10];
    static __declspec(align(16)) u8 node[0x200];
    static DWORD bk[2];
    static u8* rmSlot;
    rmSlot = rm;
    g_oaOrig = Orig, g_orQuery = Query, g_orLevel = Level, g_orRoute = Route, g_orRmSlot = &rmSlot, g_orRadius = &radius;
    g_srWorldGet = []() -> u8* { return world; };
    g_srLotLookup = Lookup;
    g_srLotFind = [](const void* tbl, void*, DWORD* o, const DWORD* key) {
        const DWORD* b = *(const DWORD* const*)((const u8*)tbl + 4);
        o[0] = key[0] == *(const DWORD*)(uintptr_t)b[0] ? b[0] : b[1];
    };
    g_srOne = &one;
    g_twActive = 0, g_steerBroken = 0, g_inInvoke = 0, t_rg.n = 0, t_rg.readers = 0;
    const DWORD me = GetCurrentThreadId();
    g_async.owner = me, g_async.handle = 0x25000021, g_async.state = A_IDLE;
    g_twArmed = 0;
    kc.rgOwnerTid = 0;
    // the world: planner +0x95C with lot 0x30 in a 1-bucket table, the query's world manager +0x964; self: +8 planner, +0xC its own
    // world manager. The gate's lot is 0x30:1, the query's 0x31:2.
    memset(world, 0, sizeof(world)), memset(wm, 0, sizeof(wm)), memset(wmw, 0, sizeof(wmw)), memset(planner, 0, sizeof(planner));
    memset(node, 0, sizeof(node)), memset(route, 0, sizeof(route));
    *(u8**)(world + 0x95C) = planner, *(u8**)(world + 0x964) = wmw;
    *(u8**)(self + 8) = planner, *(u8**)(self + 0xC) = wm;
    *(DWORD*)(lot + 0x48) = 0x30, *(DWORD*)(lot + 0x4C) = 1;
    *(DWORD*)(qlot + 0x48) = 0x31, *(DWORD*)(qlot + 0x4C) = 2;
    *(DWORD*)node = 0x30, *(DWORD*)(node + 4) = 1;
    *(DWORD*)(node + 0x10) = 0x30, *(DWORD*)(node + 0x14) = 1;
    bk[0] = (DWORD)(uintptr_t)node, bk[1] = 0x5E117E1;
    *(DWORD**)(planner + 0x4C) = bk;
    *(DWORD*)(planner + 0x50) = 1;
    noLot = noQlot = false;
    // footprints in the query's order: a wall (type 3), the caller's own Sim, an ignored Sim, a type 1 object, a Sim
    static DWORD oaVt[0x40];
    oaVt[3] = (DWORD)(uintptr_t)Type, oaVt[0x84 / 4] = (DWORD)(uintptr_t)Id;
    static u8 oaFps[5][0x40];
    static const DWORD kIds[5][3] = {{0x700, 0, 3}, {0x500, 9, 0x14}, {0x600, 0, 0x14}, {0x701, 4, 1}, {0x702, 0, 0x14}};
    for (int i = 0; i < 5; i++) {
        *(DWORD**)oaFps[i] = oaVt;
        *(DWORD*)(oaFps[i] + 0x14) = kIds[i][0], *(DWORD*)(oaFps[i] + 0x18) = kIds[i][1];
        oaFps[i][0x35] = (u8)kIds[i][2];
        qObjs[i] = oaFps[i];
    }
    // the caller's context as the IC builds it: +0 option word, +8 / +0xC the own id, +0x28 the ignore list (ids in [+0x38, +0x3C))
    static u8 oaCtx[0x30], oaList[0x40];
    static DWORD oaIgn[4];
    oaIgn[0] = 0x600, oaIgn[1] = 0, oaIgn[2] = 0x701, oaIgn[3] = 5;  // 0x701:5 is not 0x701:4
    memset(oaCtx, 0, sizeof(oaCtx)), memset(oaList, 0, sizeof(oaList));
    *(DWORD*)oaCtx = 0xABCD, *(DWORD*)(oaCtx + 8) = 0x500, *(DWORD*)(oaCtx + 0xC) = 9, oaCtx[0x24] = 1;
    *(DWORD**)(oaList + 0x38) = oaIgn, *(DWORD**)(oaList + 0x3C) = oaIgn + 4;
    *(u8**)(oaCtx + 0x28) = oaList;
    const u64 kSim701 = 0x400000000ull | 0x701, kSim500 = 0x900000000ull | 0x500;
    u64 r = 0;
    auto run = [&](const u8* c, bool flag, bool job) {
        r = 0x5555;
        const int why = OaRun(self, c, 3.0f, 9.0f, 5.0f, flag, job, &r);
        assert(!t_qlShared && !t_inRo && !g_srInClone && (!why || r == 0));
        return why;
    };

    // the clone: the first footprint of type 0x14 or 1 that is not the caller's own and not ignored
    qCalls = levels = lookA = lookB = 0, qWhy = SR_OK, qN = 5;
    assert(run(oaCtx, false, false) == SR_OK && r == kSim701);
    assert(qCalls == 1 && levels == 1 && lookA == 1 && lookB == 1 && qLo == 0x31 && qHi == 2 && qLevel == 2 && qR == 0.25f && qOpt == 0xABCD);
    assert(run(NULL, false, false) == SR_OK && r == kSim500 && qOpt == 0);  // no ctx: own id 0:0, no list, option word 0
    *(DWORD*)(oaCtx + 0xC) = 8;  // the own id's high word differs: that footprint is the answer
    assert(run(oaCtx, false, false) == SR_OK && r == kSim500);
    *(DWORD*)(oaCtx + 0xC) = 9;
    *(u8**)(oaCtx + 0x28) = NULL;  // no ignore list
    assert(run(oaCtx, false, false) == SR_OK && r == 0x600);
    *(u8**)(oaCtx + 0x28) = oaList;
    oaIgn[3] = 4;  // 0x701:4 ignored too
    assert(run(oaCtx, false, false) == SR_OK && r == 0x702);
    qN = 4;
    assert(run(oaCtx, false, false) == SR_OK && r == 0);  // nothing left
    oaIgn[3] = 5, qN = 5;
    *(DWORD*)(oaFps[1] + 0x14) = 0, *(DWORD*)(oaFps[1] + 0x18) = 0;  // id 0:0 is the own id of a call without ctx
    assert(run(NULL, false, false) == SR_OK && r == 0x600);
    *(DWORD*)(oaFps[1] + 0x14) = 0x500, *(DWORD*)(oaFps[1] + 0x18) = 9;
    // the class flag: nothing near the point is 0, footprints are the game's (slot +8 with ctx)
    qN = 0;
    assert(run(oaCtx, true, false) == SR_OK && r == 0);
    qN = 5;
    assert(run(oaCtx, true, false) == OA_WHY_HITS);
    // no lot at the point: the game's other path, before the level and the query; no lot for the query: 0
    const LONG q0 = qCalls, l0 = levels;
    noLot = true;
    assert(run(oaCtx, false, false) == OA_WHY_NOLOT && qCalls == q0 && levels == l0);
    noLot = false, noQlot = true;
    assert(run(oaCtx, false, false) == SR_OK && r == 0 && qCalls == q0 && levels == l0 + 1);
    noQlot = false;
    // doubts: lot not in the table (the transform would insert), dirty lot index (either manager), not the world's planner,
    // no self, the query's own, tripwire, a broken clone, a writer frame of this thread
    *(DWORD*)(lot + 0x48) = 0x32;
    assert(run(oaCtx, false, false) == SR_FB_LOTMISS && qCalls == q0);
    noQlot = true;  // even when the query would find no lot
    assert(run(oaCtx, false, false) == SR_FB_LOTMISS);
    noQlot = false;
    *(DWORD*)(lot + 0x48) = 0x30;
    const LONG a0 = lookA;
    wm[0x1CC] = 1;
    assert(run(oaCtx, false, false) == SR_FB_DIRTY && lookA == a0);  // before the lookup that would rebuild it
    wm[0x1CC] = 0, wmw[0x1CC] = 1;
    assert(run(oaCtx, false, false) == SR_FB_DIRTY && lookA == a0);
    wmw[0x1CC] = 0;
    *(u8**)(self + 8) = planner + 4;
    assert(run(oaCtx, false, false) == SR_FB_SHAPE);
    *(u8**)(self + 8) = planner;
    r = 0x5555;
    assert(OaRun(NULL, oaCtx, 3.0f, 9.0f, 5.0f, false, false, &r) == SR_FB_STATE && r == 0);
    qWhy = SR_FB_DEFERRED;
    assert(run(oaCtx, false, false) == SR_FB_DEFERRED);
    qWhy = SR_OK;
    g_twActive = 1;
    assert(run(oaCtx, false, false) == SR_FB_TRIP);
    g_twActive = 0;
    g_steerBroken = 1;
    assert(run(oaCtx, false, false) == SR_FB_STATE);
    g_steerBroken = 0;
    t_qlDepth = 1;
    assert(run(oaCtx, false, false) == SR_FB_STATE);
    t_qlDepth = 0;
    // with a job: the tripwires must be armed; an ignore list inside the job's route (or no job route) is the game's
    assert(run(oaCtx, false, true) == SR_FB_STATE);
    g_twArmed = 1;
    assert(run(oaCtx, false, true) == SR_OK && r == kSim701);
    *(DWORD**)(route + 0x158 + 0x38) = oaIgn, *(DWORD**)(route + 0x158 + 0x3C) = oaIgn + 4;
    *(u8**)(oaCtx + 0x28) = route + 0x158;
    assert(run(oaCtx, false, true) == OA_WHY_ROUTE);
    assert(run(oaCtx, false, false) == SR_OK && r == kSim701);  // no job: nobody else reads that route
    *(u8**)(oaCtx + 0x28) = oaList;
    g_async.handle = 0x25000022;  // the job's route is not in the table: whose list it is cannot be told
    assert(run(oaCtx, false, true) == OA_WHY_ROUTE);
    assert(run(NULL, false, true) == SR_OK && r == kSim500);  // no list: no route read
    g_async.handle = 0x25000021;

    // the hook: switches off, another thread, a job (answer / given back by reason), no job, shadow
    auto hookAs = [&](DWORD flag) { return OaHook(self, NULL, oaCtx, 3.0f, 9.0f, 5.0f, flag); };  // the flag is the low byte
    auto hook = [&]() { return hookAs(0x7700); };
    origCalls = 0, origRet = 0x111;
    g_async.state = A_RUNNING;
    assert(hook() == 0x111 && origCalls == 1 && k.oaCalls == 0);  // both switches off
    k.oaMode = 1;
    g_async.owner = me + 1;
    assert(hook() == 0x111 && origCalls == 2 && k.oaCalls == 0);
    g_async.owner = me;
    assert(hook() == kSim701 && origCalls == 2 && k.oaCalls == 1 && k.oaTaken == 1 && k.oaHits == 1);
    qN = 0;
    assert(hook() == 0 && origCalls == 2 && k.oaTaken == 2 && k.oaHits == 1);
    assert(hookAs(0x7701) == 0 && origCalls == 2 && k.oaTaken == 3 && k.oaHits == 1);  // class flag, nothing near
    qN = 5;
    assert(hookAs(0x7701) == 0x111 && origCalls == 3 && k.oaFb[OA_FB_HITS] == 1 && k.oaTaken == 3);
    struct Fb { const char* name; int why; int idx; };
    static const Fb fbs[] = {{"deferred flush", SR_FB_DEFERRED, OA_FB_LOT}, {"lot miss", SR_FB_LOTMISS, OA_FB_LOT}, {"shape", SR_FB_SHAPE, OA_FB_SHAPE},
                             {"polygon cache", SR_FB_CACHE, OA_FB_SHAPE}, {"tripwire", SR_FB_TRIP, OA_FB_TRIP}, {"state", SR_FB_STATE, OA_FB_STATE}};
    for (const Fb& f : fbs) {
        qWhy = f.why;
        const LONG o0 = origCalls, f0 = k.oaFb[f.idx];
        if (hook() != 0x111 || origCalls != o0 + 1 || k.oaFb[f.idx] != f0 + 1 || k.oaTaken != 3) {
            printf("ObstructAtRO fallback '%s' not taken\n", f.name);
            assert(false);
        }
    }
    qWhy = SR_OK;
    {
        const LONG o0 = origCalls, s0 = k.oaFb[OA_FB_STATE];
        noLot = true;
        assert(hook() == 0x111 && origCalls == o0 + 1 && k.oaFb[OA_FB_NOLOT] == 1);
        noLot = false;
        wm[0x1CC] = 1;
        assert(hook() == 0x111 && origCalls == o0 + 2 && k.oaFb[OA_FB_LOT] == 3);
        wm[0x1CC] = 0;
        *(u8**)(oaCtx + 0x28) = route + 0x158;
        assert(hook() == 0x111 && origCalls == o0 + 3 && k.oaFb[OA_FB_ROUTE] == 1);
        *(u8**)(oaCtx + 0x28) = oaList;
        g_inInvoke = 1;  // serving the job's callback
        assert(hook() == 0x111 && origCalls == o0 + 4 && k.oaFb[OA_FB_STATE] == s0 + 1);
        g_inInvoke = 0;
        kc.rgOwnerTid = (LONG)me;  // the sim owns +0x918
        assert(hook() == 0x111 && origCalls == o0 + 5 && k.oaFb[OA_FB_STATE] == s0 + 2);
        kc.rgOwnerTid = 0;
        t_rg.n = 1;  // a gate frame open
        assert(hook() == 0x111 && origCalls == o0 + 6 && k.oaFb[OA_FB_STATE] == s0 + 3);
        t_rg.n = 0;
        g_twArmed = 0;  // the job ended before the clone
        assert(hook() == 0x111 && origCalls == o0 + 7 && k.oaFb[OA_FB_STATE] == s0 + 4);
        g_twArmed = 1;
        AcquireSRWLockExclusive(&g_ql);  // a worker W frame holds QL: try only
        assert(hook() == 0x111 && origCalls == o0 + 8 && k.oaFb[OA_FB_TRY] == 1);
        ReleaseSRWLockExclusive(&g_ql);
        k.oaMode = 0;  // switch off with a job: the game's call, not counted
        const LONG c0 = k.oaCalls;
        assert(hook() == 0x111 && origCalls == o0 + 9 && k.oaCalls == c0);
        k.oaMode = 1;
        assert(k.oaTaken == 3 && hook() == kSim701 && origCalls == o0 + 9 && k.oaTaken == 4 && k.oaHits == 2);
    }
    // no job: the game's call (Mode 1 alone); shadow: the game's answer is returned, the clone compared
    g_async.state = A_IDLE, g_twArmed = 0;
    const LONG o1 = origCalls;
    assert(hook() == 0x111 && origCalls == o1 + 1 && k.oaShadowRuns == 0 && k.oaTaken == 4);
    k.oaShadow = 1, k.oaMode = 0;
    origRet = kSim701;
    assert(hook() == kSim701 && origCalls == o1 + 2 && k.oaShadowRuns == 1 && k.oaShadowMismatch == 0);
    origRet = 0x702;
    assert(hook() == 0x702 && origCalls == o1 + 3 && k.oaShadowRuns == 2 && k.oaShadowMismatch == 1);
    const LONG h0 = k.oaFb[OA_FB_HITS];
    assert(hookAs(1) == 0x702 && k.oaShadowRuns == 3 && k.oaShadowMismatch == 1 && k.oaFb[OA_FB_HITS] == h0 + 1);  // given up: counted, not compared
    g_async.state = A_RUNNING;  // shadow alone with a job: the game's call
    assert(hook() == 0x702 && origCalls == o1 + 5 && k.oaShadowRuns == 3 && k.oaTaken == 4);
    g_async.state = A_IDLE;
    assert(!t_qlShared && !t_inRo && !g_srInClone && g_rgOffSimOpen == 0);
    printf("ObstructAtRO ([ObstructAtRO] Enabled=1): clone on fakes: first footprint of type 0x14 / 1 not the caller's own and not "
           "ignored (no ctx, own id high word, no list), class flag with nothing near, the gate's lot and the query's lot apart; given "
           "back: no lot, class flag with footprints, lot miss, dirty lot index, planner, deferred, tripwire, state, the job's own route; "
           "hook: switches, other thread, job answers %ld (obstructed %ld), +0x918 owner, callback, gate frame, job end, QL busy, shadow "
           "%ld runs / %ld mismatch\n", k.oaTaken, k.oaHits, k.oaShadowRuns, k.oaShadowMismatch);

    k = savedK;
    g_oaOrig = savedOrig, g_orQuery = savedQuery, g_orLevel = savedLevel, g_orRoute = savedRoute, g_orRmSlot = savedRm, g_orRadius = savedRadius;
    g_srWorldGet = savedWorld, g_srLotLookup = savedLookup, g_srLotFind = savedFind, g_srOne = savedOne;
    g_async = savedAsync, g_twArmed = savedArmed, kc.rgOwnerTid = savedOwner, g_twActive = savedActive, g_steerBroken = savedBroken;
    g_inInvoke = savedInvoke, t_rg.n = savedRgN, t_rg.readers = savedRgReaders;
}
