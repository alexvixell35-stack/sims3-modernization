// --- [IdleCheckRO] SiteC (idlero.cpp IcStub, docs/195 §4 C1): the IC's obstacle query at 0x75598B on GoalValidRO's fakes
// (gvro-test.h: the game's 0x82EDB0, the lot lookup and S2's point query replaced): layout, pass-through rules, empty
// answer and no-lot answer without the game's call, footprints = the game's call, a fallback, shadow compare. Included by
// selftest.cpp after gvro-test.h.
static void TestIdleCheckC() {
    using namespace gvt;
    Ts3nCounters& k = ts3native_counters;
    static_assert(offsetof(Ts3nCounters, icReady) == 0x5B84 && offsetof(Ts3nCounters, icMode) == 0x5B88 &&
                      offsetof(Ts3nCounters, icShadow) == 0x5B8C && offsetof(Ts3nCounters, icFb) == 0x5BA4 &&
                      offsetof(Ts3nCounters, icShadowMismatch) == 0x5BC8 && sizeof(Ts3nCounters) == 5878 * 4,
                  "counters v903 layout after v902 (docs/registry.md)");
    assert(k.version == 904);
    assert(kIcSite.site == 0x0075598B && kIcSite.target == 0x0082EDB0);
    memset(&k.icReady, 0, sizeof(Ts3nCounters) - offsetof(Ts3nCounters, icReady));
    const LONG gvTaken0 = k.gvTaken, gvCalls0 = k.gvCalls, gvShadow0 = k.gvShadowRuns;  // GoalValidRO's own, untouched here
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
    const GvFn savedOrig = g_icOrig;
    const GvQueryFn savedQuery = g_gvQuery;
    const SrLotLookupFn savedLookup = g_srLotLookup;
    const AsyncSlot savedAsync = g_async;
    const LONG savedArmed = g_twArmed, savedOwner = k.rgOwnerTid;
    const float* savedOne = g_srOne;
    static const float one = 1.0f;
    g_icOrig = Orig, g_gvQuery = Query, g_srLotLookup = Lookup, g_srOne = &one;
    const DWORD me = GetCurrentThreadId();
    g_async.owner = me;
    k.rgOwnerTid = 0;
    static u8 world[0x980], planner[0x10], wm[0x200];
    *(u8**)(world + 0x95C) = planner, *(u8**)(world + 0x964) = wm;
    *(DWORD*)(lot + 0x48) = 0x31, *(DWORD*)(lot + 0x4C) = 0x2;
    __declspec(align(16)) float pt[13] = {4.0f, 1.0f, 6.0f};  // the 0x6508F0 point: xyz, level at +0x24
    *(LONG*)&pt[9] = 1;
    DWORD vec[4] = {0, 0, 0, 0x1004514};  // "Routing/Host"
    auto call = [&](DWORD mode = 0) {
        vec[0] = vec[1] = 0;
        return IcStub(world, NULL, pt, 0.3f, vec, mode, 0x41);
    };
    origCalls = 0, origRet = 1, origFill = 0, noLot = false, qWhy = SR_OK, qN = 0, qCalls = 0, lookups = 0;

    // switches off / another thread: the game's call, nothing counted
    g_async.state = A_RUNNING;
    assert((u8)call() == 1 && origCalls == 1 && k.icCalls == 0);
    k.icMode = 1, g_async.owner = me + 1;
    assert((u8)call() == 1 && origCalls == 2 && k.icCalls == 0);
    g_async.owner = me;

    // a job runs, nothing near the point: al 1, the vector untouched (the IC then frees it and returns 0), no game call
    g_twArmed = 1;
    assert(call() == 1 && vec[0] == 0 && vec[1] == 0 && origCalls == 2 && k.icTaken == 1 && k.icNoLot == 0);
    assert(lookups == 1 && lkX == 4.0f && lkZ == 6.0f);
    assert(qCalls == 1 && qLo == 0x31 && qHi == 2 && qLevel == 1 && qX == 4.0f && qZ == 6.0f && qR == 0.3f && qOpt == 0x41);
    noLot = true;  // no lot at the point: al 0, no query
    assert(call() == 0 && qCalls == 1 && k.icTaken == 2 && k.icNoLot == 1 && origCalls == 2);
    noLot = false;
    assert(k.gvTaken == gvTaken0 && k.gvCalls == gvCalls0);  // GoalValidRO's counters stay its own

    // footprints near the point: the IC's loop needs the game's refs
    qN = 3;
    assert(call() == 0xABCD01 && origCalls == 3 && k.icFb[GV_FB_HITS] == 1 && k.icTaken == 2);
    qN = 0;
    // a fallback (tripwire) and the +0x918 owner: the game's call, counted by reason
    qWhy = SR_FB_TRIP;
    assert(call() == 0xABCD01 && origCalls == 4 && k.icFb[GV_FB_TRIP] == 1);
    qWhy = SR_OK;
    k.rgOwnerTid = (LONG)me;
    assert(call() == 0xABCD01 && origCalls == 5 && k.icFb[GV_FB_STATE] == 1);
    k.rgOwnerTid = 0;
    assert(call(1) == 0xABCD01 && origCalls == 6 && k.icFb[GV_FB_STATE] == 2);  // mode 1: not this call site
    k.icMode = 0;  // switch off with a job: the game's call
    assert(call() == 0xABCD01 && origCalls == 7);
    assert(!t_qlShared && !t_inRo && !g_srInClone);

    // shadow with no job: the game's answer is returned, the clone compared (al and footprint count)
    k.icShadow = 1, g_async.state = A_IDLE, g_twArmed = 0;
    origFill = 2, qN = 2;
    assert(call() == 0xABCD01 && origCalls == 8 && k.icShadowRuns == 1 && k.icShadowMismatch == 0 && k.icShadowEmpty == 0 &&
           g_rgOffSimOpen == 0);
    qN = 1;  // count skew
    assert(call() == 0xABCD01 && k.icShadowRuns == 2 && k.icShadowMismatch == 1);
    origFill = 0, qN = 0;  // both empty
    assert(call() == 0xABCD01 && k.icShadowRuns == 3 && k.icShadowMismatch == 1 && k.icShadowEmpty == 1);
    assert(k.gvShadowRuns == gvShadow0);
    puts("IdleCheckRO SiteC: counters v903; switch off / other thread pass; job: empty and no-lot answers without the game's "
         "call, with the frame's point, level, radius and option word; footprints, tripwire, +0x918 owner, mode 1 and switch off "
         "call the game; shadow compare, count skew, empty share");

    k.icMode = k.icShadow = 0;
    g_icOrig = savedOrig, g_gvQuery = savedQuery, g_srLotLookup = savedLookup, g_srOne = savedOne;
    g_async = savedAsync, g_twArmed = savedArmed, k.rgOwnerTid = savedOwner;
    origFill = 0;
}
