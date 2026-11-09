// --- [IdleCheckRO] SiteC (idlero.cpp IcStub, docs/195 §4 C1): the IC's obstacle query at 0x75598B on GoalValidRO's fakes
// (gvro-test.h: the game's 0x82EDB0, the lot lookup and S2's point query replaced): layout, pass-through rules, empty
// answer and no-lot answer without the game's call, footprints = the game's call, a fallback, shadow compare. Included by
// selftest.cpp after gvro-test.h.
// SiteCLoop (C2) fakes: the clone's list, the class function, ObjectService_FindById and the flag mask
namespace icl {
static const u8* qObjs[4];
static int qn, clsVal = 5;
static bool clsOk = true, findHit;
static u8 found[0x40];
static DWORD fLo, fHi, mask = 0x100;
static const void* fSvc;
static int LQuery(SrCtx* c, const u8*, DWORD, DWORD, const SrPt&, float, const u8*) {
    c->n = qn, c->over = 0;
    for (int i = 0; i < qn; i++) c->objs[i] = qObjs[i];
    return SR_OK;
}
static bool Class(const u8*, int* cls) {
    if (!clsOk) return false;
    *cls = clsVal;
    return true;
}
static const u8* __fastcall Find(const void* svc, void*, DWORD lo, DWORD hi) {
    fSvc = svc, fLo = lo, fHi = hi;
    return findHit ? found : NULL;
}
}  // namespace icl

static void TestIdleCheckC() {
    using namespace gvt;
    Ts3nCounters& k = ts3native_counters;
    static_assert(offsetof(Ts3nCounters, icReady) == 0x5B84 && offsetof(Ts3nCounters, icMode) == 0x5B88 &&
                      offsetof(Ts3nCounters, icShadow) == 0x5B8C && offsetof(Ts3nCounters, icFb) == 0x5BA4 &&
                      offsetof(Ts3nCounters, icShadowMismatch) == 0x5BC8 && sizeof(Ts3nCounters) == 6112 * 4,
                  "counters v903 layout after v902 (docs/registry.md)");
    assert(k.version == 912);
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

    // SiteCLoop (C2, docs/195 §8): the IC's loop on fake footprints (interface table at +4) and a fake IC frame
    {
        static DWORD ids[4] = {0x55, 0, 0x66, 0};
        alignas(16) static u8 fr[0x60];
        memset(fr, 0, sizeof(fr));
        *(DWORD**)(fr + 0x18) = ids, *(DWORD*)(fr + 0x1C) = 2, *(DWORD**)(fr + 0x34) = ids + 4, *(DWORD*)(fr + 0x4C) = 0x5EC;
        static DWORD vtA[4], svK5[9], svK7[9];
        static u8 a[0x40], b[0x40], c5[0x40];
        svK7[2] = 0x00624ED0, svK7[3] = 0x00620E00, svK7[7] = 0x00620D20;  // kind 7: class slot, type byte
        svK5[1] = 0x00620E60, svK5[3] = 0x00624E60;                         // kind 5: the id slot
        *(DWORD**)a = vtA, *(DWORD**)(a + 4) = svK7;
        *(DWORD**)b = vtA, *(DWORD**)(b + 4) = svK7;
        *(DWORD**)c5 = vtA, *(DWORD**)(c5 + 4) = svK5;
        *(DWORD*)(c5 + 0x14) = 0x66;  // in the frame's id list
        const IcClassFn savedClass = g_icClass;
        const IcFindFn savedFind = g_icFind;
        const DWORD* savedMask = g_icMask;
        g_icClass = icl::Class, g_icFind = icl::Find, g_icMask = &icl::mask;
        a[0x35] = 3, b[0x35] = 1;
        assert(IcLoopOne(a, fr) == 0 && IcLoopOne(b, fr) == 0x10);
        svK7[3] = 0x00624E00;  // kind 9 (0xFEBA74): the class path too
        assert(IcLoopOne(b, fr) == 0x10);
        svK7[3] = 0x00620E00;
        b[0x35] = 12;
        assert(IcLoopOne(b, fr) == 8);
        b[0x35] = 23;
        assert(IcLoopOne(b, fr) == 8);
        icl::clsVal = 1;  // class below 2 or 13: IsKind false
        assert(IcLoopOne(b, fr) == 0);
        icl::clsVal = 0xD;
        assert(IcLoopOne(b, fr) == 0);
        icl::clsVal = 5;
        svK7[2] = 0x00620E50;  // type 10
        assert(IcLoopOne(b, fr) == 0);
        svK7[2] = 0x00012345;
        assert(IcLoopOne(b, fr) < 0);
        svK7[2] = 0x00624ED0, svK7[7] = 0x00624F70;  // the member footprint: IsKind false
        assert(IcLoopOne(b, fr) == 0);
        svK7[7] = 0x00620D20, icl::clsOk = false;
        assert(IcLoopOne(b, fr) < 0);
        icl::clsOk = true, svK7[3] = 0x00999999;
        assert(IcLoopOne(b, fr) < 0);
        svK7[3] = 0x00620E00;
        assert(IcLoopOne(c5, fr) == 0);  // kind 5, id in the list
        *(DWORD*)(c5 + 0x14) = 0x77;
        assert(IcLoopOne(c5, fr) == 0x20 && icl::fLo == 0x77 && icl::fHi == 0 && icl::fSvc == (const void*)(uintptr_t)0x5EC);
        icl::findHit = true, *(DWORD*)(icl::found + 0x2C) = 0x100;
        assert(IcLoopOne(c5, fr) == 4);
        *(DWORD*)(icl::found + 0x2C) = 0x200;
        assert(IcLoopOne(c5, fr) == 0x20);
        *(DWORD*)(fr + 0x1C) = 0;  // no ids: 0x20 before the lookup
        assert(IcLoopOne(c5, fr) == 0x20);
        *(DWORD*)(fr + 0x1C) = 2, svK5[1] = 0;
        assert(IcLoopOne(c5, fr) == -3 && g_icDoubt.why == 3 && g_icDoubt.sv3 == 0x00624E60);
        svK5[1] = 0x00620E60, *(DWORD*)(c5 + 0x14) = 0x66, b[0x35] = 1;
        const u8* l0[3] = {a, NULL, b};  // first decisive answer, NULL skipped
        assert(IcLoopRun(l0, 3, 0, fr) == 0x10 && IcLoopRun(l0, 1, 0, fr) == 0);
        const u8* l4[2] = {a + 4, b + 4};  // the game's vector: interface pointers
        assert(IcLoopRun(l4, 2, 4, fr) == 0x10);
        g_icTestFrame = fr;  // the frame guard: the vector at +0x38, ids end = ids + count * 8
        assert(IcFrameOf(NULL, (DWORD*)(fr + 0x38)) == fr && !IcFrameOf(NULL, (DWORD*)(fr + 0x3C)));
        *(DWORD*)(fr + 0x1C) = 3;
        assert(!IcFrameOf(NULL, (DWORD*)(fr + 0x38)));
        *(DWORD*)(fr + 0x1C) = 2;
        // IcStub in a job: no footprint decides -> 1 with the vector empty and no game call; one decides or a doubt -> the game
        DWORD* const fv = (DWORD*)(fr + 0x38);
        auto lcall = [&]() { fv[0] = fv[1] = 0; return IcStub(world, NULL, pt, 0.3f, fv, 0, 0x41); };
        g_gvQuery = icl::LQuery;
        k.icShadow = 0, k.icMode = 1, k.icLoopMode = 1, g_async.state = A_RUNNING, g_twArmed = 1, origFill = 0;
        icl::qObjs[0] = a, icl::qObjs[1] = c5, icl::qn = 2;
        const LONG o0 = origCalls, hits0 = k.icFb[GV_FB_HITS];
        assert(lcall() == 1 && fv[0] == fv[1] && origCalls == o0 && k.icLoopRuns == 1 && k.icLoopTaken == 1);
        icl::qObjs[1] = b;  // type byte 1: 0x10
        assert(lcall() == 0xABCD01 && origCalls == o0 + 1 && k.icLoopCode[2] == 1 && k.icLoopRuns == 2);
        svK7[3] = 0x00999999;
        assert(lcall() == 0xABCD01 && origCalls == o0 + 2 && k.icLoopDoubt == 1 && k.icLoopRuns == 3);
        svK7[3] = 0x00620E00, k.icLoopMode = 0;  // switch off: footprints call the game as in C1
        assert(lcall() == 0xABCD01 && origCalls == o0 + 3 && k.icFb[GV_FB_HITS] == hits0 + 1 && k.icLoopRuns == 3);
        assert(k.icLoopTaken == 1 && !t_qlShared && !t_inRo && !g_srInClone);
        {  // SiteCLoopJump (C2b): a deciding footprint returns to the IC's exit for its answer (the test records the exit)
            k.icLoopMode = 1, k.icJumpMode = 1, g_icJumpOk = true, g_icJumpTo = 0;
            const LONG o1 = origCalls, j0 = k.icJumped, c0 = k.icLoopCode[2];
            assert(lcall() == 1 && fv[0] == fv[1] && origCalls == o1 && g_icJumpTo == 0x00755A97 && k.icJumped == j0 + 1 &&
                   k.icLoopCode[2] == c0 + 1);
            b[0x35] = 12;
            assert(lcall() == 1 && g_icJumpTo == 0x00755A82 && origCalls == o1);
            b[0x35] = 1, svK7[3] = 0x00999999, g_icJumpTo = 0;  // a doubt still calls the game
            assert(lcall() == 0xABCD01 && origCalls == o1 + 1 && !g_icJumpTo);
            svK7[3] = 0x00620E00, g_icJumpOk = false;  // exits not verified: the game
            assert(lcall() == 0xABCD01 && origCalls == o1 + 2 && !g_icJumpTo);
            g_icJumpOk = true, k.icJumpMode = 0;  // switch off: the game, as in C2
            assert(lcall() == 0xABCD01 && origCalls == o1 + 3 && !g_icJumpTo && k.icJumped == j0 + 2);
            assert(IcExit(4) == 0x00755A12 && IcExit(0x20) == 0x00755A6D && !IcExit(0) && !IcExit(-1));
            k.icLoopMode = 0, g_icJumpOk = false;
        }
        // shadow, no job: the loop on the game's vector (fill = interface pointers) and on the clone, compared
        k.icShadow = 1, g_async.state = A_IDLE, g_twArmed = 0;
        icl::qObjs[0] = a, icl::qObjs[1] = c5, icl::qn = 2;
        fill[0] = (DWORD)(uintptr_t)(a + 4), fill[1] = (DWORD)(uintptr_t)(c5 + 4), origFill = 2;
        lcall();
        assert(k.icLoopShadowRuns == 1 && k.icLoopShadowZero == 1 && k.icLoopShadowMismatch == 0 && k.icLoopShadowCodeDiff == 0);
        fill[1] = (DWORD)(uintptr_t)(b + 4);  // the game's list decides (0x10), the clone's does not
        lcall();
        assert(k.icLoopShadowRuns == 2 && k.icLoopShadowZero == 1 && k.icLoopShadowMismatch == 1);
        fill[0] = (DWORD)(uintptr_t)(b + 4), origFill = 1;  // both decide, differently: 0x10 vs 4
        icl::qObjs[0] = c5, icl::qn = 1, *(DWORD*)(c5 + 0x14) = 0x77, *(DWORD*)(icl::found + 0x2C) = 0x100;
        lcall();
        assert(k.icLoopShadowRuns == 3 && k.icLoopShadowMismatch == 1 && k.icLoopShadowCodeDiff == 1);
        assert(g_rgOffSimOpen == 0 && !t_qlShared && g_async.state == A_IDLE);
        g_icClass = savedClass, g_icFind = savedFind, g_icMask = savedMask, g_icTestFrame = NULL;
        k.icLoopMode = 0, origFill = 0, fill[0] = fill[1] = 0, icl::findHit = false;
        puts("IdleCheckRO SiteCLoop: counters v905; the IC's loop: kinds by slot target, type bytes 1 / 12 / 23, class 2..12, "
             "member false, kind 5 id list / lookup / flag mask, doubts; first decisive answer; frame guard; in a job answer 0 "
             "without the game's call, an answer or a doubt calls it, switch off = C1; shadow zero share, mismatch, code diff; "
             "SiteCLoopJump (v906): an answer returns to the IC's exit for it, a doubt / unverified exits / switch off call the game");
    }

    k.icMode = k.icShadow = 0;
    g_icOrig = savedOrig, g_gvQuery = savedQuery, g_srLotLookup = savedLookup, g_srOne = savedOne;
    g_async = savedAsync, g_twArmed = savedArmed, k.rgOwnerTid = savedOwner;
    origFill = 0;
}
