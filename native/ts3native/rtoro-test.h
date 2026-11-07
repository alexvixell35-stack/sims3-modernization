// [RouteToObjRO] (rtoro.cpp, docs/140 §12): selftest. Included by selftest.cpp after tailunlock-test.h (P5bAcq, P5bRel,
// P5bMine, P5bSpin, g_p5bW from p5b-test.h). The three stubs run as 0x831530 calls them (R-acq through the naked stub, R-call,
// R-rel) on a fake world: +0x918 a real CRITICAL_SECTION (P5bAcq / P5bRel as Lock_Acquire / Lock_Release), the lot planner
// with one lot (rows: u = x + 10, v = z; back: x = u - 10), a cell graph whose maps hold the target's footprint, fake
// footprints with fake slots, the point query and BlockerCheck2d's gather as fakes that read the current fake world (and
// assert QL shared is held). The game's 0x830340 is a stub that must run under the lock and returns the expected answer of
// the case, hand-derived from the disassembly (docs/140 §12.1). Cases: every rule of the route part (code 4 at the point,
// target ignored, code 0 inside, code 8 slot missing, kinds 1 / 9 with the object-record flag and the shifted second check,
// BlockerCheck2d types, options, ignore ids, merge), the fallbacks (physics tail, option 0x100000, polygon cache), in Mode 1
// (a job "running": answered without the lock, or taken late and released once) and in shadow (the game's call compared).
// Threaded: a worker holds +0x918 per job and flips the fake world between two variants under QL exclusive; every answer
// must be one of the two, no torn read, footprint refcounts untouched, no hang.
namespace rto {
static __declspec(align(16)) u8 p[0x100], node[0x200], wmw[0x200], qlot[0x60], actLot[0x60], q[0x60], root[0x60];
static u8 map1[0x10], map2[0x10], map3[0x10], node3[0x20], holder[0x20], sim[0x300], target[0x40], tc[0x40], objrec[0x100];
static DWORD bk[2], mbk1[1], mbk2[1], mbk3[1];
static const u8* qvec[1];
static __declspec(align(16)) u8 fp[10][0x80], cache[10][0x24];
static u8 fpT[0x80], cacheT[0x24];
static const u8* qList[4];
static const u8* sList[6];
static int nq, ns;
static volatile LONG gen1, gen2, torn, gatherCalls, queryCalls, refCalls, refUnlocked;
static DWORD expCode, expLo, expHi;
static bool tcOn, hasSlot, slotCompOn;
static float lastU1[2];
static int gatherIdx;
static u8 opts[0x40];
static DWORD keyTc = 1, keySlot = 2;
static u8* tabPtr = objrec;
static float radius = 0.123f, segR = 0.5f, farThr = 0.9f, half = 0.5f, one = 1.0f;
static DWORD dl = 0xFFFFFFFF;

static DWORD __fastcall Levels(const u8* o, void*) { return o[0x50]; }
static int __fastcall Type(const u8* o, void*) { return (signed char)o[0x35]; }
static float __fastcall F44(const u8*, void*) { return 0.25f; }
static u64 __fastcall Id(const u8* o, void*) { return *(const DWORD*)(o + 0x14); }
static const u8* __fastcall Portal(const u8*, void*) { return NULL; }
static u8 __fastcall C0(const u8* o, void*) { return !o[0x3C]; }
static u8 __fastcall Seg(const u8* o, void*, DWORD lvl, float, float, float, float, float r) {
    assert(lvl == 0 && r == 0.25f && t_qlShared == 1);
    return o[0x36];
}
static u8 __fastcall Inside(const u8* o, void*, DWORD lvl, const float* v) {
    assert(lvl == 0 && v[0] == 13.0f && v[2] == 5.0f && t_qlShared == 1);  // the point (3, 0, 5) in lot coordinates
    return o[0x37];
}
static float __fastcall Near(const u8*, void*, DWORD lvl, const float* v, float* out) {
    assert(lvl == 0 && v[0] == 13.0f);
    out[0] = 11.0f, out[1] = 5.0f;  // world (1, 5)
    return 0.0f;
}
static u8 __fastcall C4(const u8* o, void*, DWORD) { return o[0x3D]; }
static u64 __fastcall SubId(const u8* s, void*) { return *(const DWORD*)(s + 0x10); }
static int __fastcall SubType(const u8* s, void*) { return (signed char)s[0x31]; }
static void* fvt[0x40];
static void* svt[4];
static void MakeFp(u8* f, u8* c, DWORD id, int type, u8 hit) {
    memset(f, 0, 0x80);
    *(void***)f = fvt, *(void***)(f + 4) = svt;
    *(DWORD*)(f + 8) = 1;  // refcount: the clone never touches it
    *(DWORD*)(f + 0x14) = id;
    f[0x35] = (u8)type, f[0x36] = hit, f[0x50] = 1;
    *(u8**)(f + 0x38) = c;
    c[0x20] = 1;
}

static int Query(SrCtx* c, const u8* pl, DWORD lo, DWORD, const SrPt& pt, float r, const u8* rec) {
    InterlockedIncrement(&queryCalls);
    assert(pl == p && lo == 0x30 && r == radius && *(const DWORD*)rec == 0x5A && rec[0x24] == 1 && pt.v[0] == 3.0f && t_qlShared == 1);
    const LONG g = gen1;
    c->n = nq;
    for (int i = 0; i < nq; i++) c->objs[i] = qList[i];
    if (gen2 != g) InterlockedIncrement(&torn);
    return SR_OK;
}
static int Gather(SrCtx* c, const u8* pl, DWORD lo, DWORD, LONG level, const u8* rec, float u0, float v0, float u1, float, float r) {
    InterlockedIncrement(&gatherCalls);
    assert(pl == p && lo == 0x30 && level == 0 && r == segR && u0 == 13.0f && v0 == 5.0f && t_qlShared == 1);
    assert(*(const DWORD*)rec == 0x5A && *(const DWORD*)(rec + 8) == *(const DWORD*)(sim + 0x200));  // option word, ignore id
    const u8* copy = *(const u8* const*)(rec + 0x28);
    assert(copy == g_rtoCopy[0] || copy == g_rtoCopy[1]);
    if (gatherIdx < 2) lastU1[gatherIdx] = u1;
    gatherIdx++;
    const LONG g = gen1;
    c->n = ns;
    for (int i = 0; i < ns; i++) c->objs[i] = sList[i];
    if (gen2 != g) InterlockedIncrement(&torn);
    return SR_OK;
}
// options: +0 bits of T(a, 0, 0), +4 bits of T(0, b, 0), +8 one id in the lists (0x81C970)
static u8 __fastcall OptTest(const u8* o, void*, DWORD a, DWORD b, DWORD) {
    return (a & *(const DWORD*)o) || (b & *(const DWORD*)(o + 4));
}
static u8 __fastcall OptId(const u8* o, void*, DWORD lo, DWORD) { return lo && lo == *(const DWORD*)(o + 8); }
static void __fastcall OptCopy(u8* d, void*, const u8* s) { memcpy(d, s, 0x10); }
static void __fastcall OptSet(u8* d, void*, DWORD a, DWORD b, DWORD c, DWORD e) {
    assert(a == 4 && b == 0 && c == 1 && e == 0);
    *(DWORD*)d |= 4;
}
static void __fastcall OptFree(u8* d, void*) { memset(d, 0xCD, 0x10); }
static u8* __fastcall Comp(const u8* o, void*, DWORD key) {
    assert(o == target);
    return key == 1 ? (tcOn ? tc : NULL) : key == 2 ? (slotCompOn ? tc : NULL) : NULL;
}
static u8* __fastcall SlotGet(const u8*, void*, DWORD key) { assert(key == 0x31229A4C); return NULL; }
static void __fastcall Pos(const u8* o, void*, float* out) { assert(o == tc); out[0] = 1.0f, out[1] = 0.0f, out[2] = 5.0f, out[3] = 1.0f; }
static u8 __cdecl HasSlot(const u8*) { return hasSlot; }
static u8* __fastcall ObjRec(const u8* t, void*, const u8* key) { assert(t == objrec && key == target + 0x18); return objrec; }
static u8 __fastcall SimList(const u8*, void*, DWORD, DWORD) { return 0; }
static u8 __fastcall PortalTest(const u8*, void*, const u8*) { return 1; }
// the game's 0x830340: must run under the lock, answers the case's expected result
static DWORD* __fastcall Route(u8*, void*, DWORD* out, u8*, u8*, const float*, u8*, DWORD, DWORD, DWORD, DWORD, DWORD) {
    InterlockedIncrement(&refCalls);
    if (!P5bMine()) InterlockedIncrement(&refUnlocked);
    out[0] = expCode, out[2] = expLo, out[3] = expHi;
    return out;
}
static u8 __fastcall FkSrQuery(u8*, void*, const void*, float r, void* vec, DWORD mode, DWORD opt) {  // shadow: 0x82EDB0
    assert(mode == 1 && opt == 0x5A && r == radius);
    DWORD* v = (DWORD*)vec;
    DWORD* b = (DWORD*)malloc(4 * (nq ? nq : 1));
    for (int i = 0; i < nq; i++) b[i] = (DWORD)(uintptr_t)(qList[i] + 4);
    v[0] = (DWORD)(uintptr_t)b, v[1] = (DWORD)(uintptr_t)(b + nq);
    return 1;
}
static void __fastcall FkVecRel(void*, void*, void*, void*) {}
static void __cdecl FkFree(void* x) { free(x); }

// one 0x831530 call: R-acq (naked stub), R-call, R-rel
static DWORD out[4];
static float pt[12];
static void Emu(DWORD a7 = 0, DWORD a10 = 1, bool route = true) {
    u8* world = g_p5bW;
    ((LockAcquireFn)(void*)RtoAcqStub)(world + 0x918, NULL, &dl);
    if (route) {
        out[0] = out[2] = out[3] = 0xEEEEEEEE;
        RtoCallStub(world, NULL, out, sim, target, pt, opts, 0x5A, a7, *(DWORD*)(sim + 0x200), 0, a10);
    }
    RtoRelStub(world + 0x918, NULL);
}
struct Case {
    const char* name;
    int qTypes[3];   // query footprints (0 = none); type 0x100 + t = the target's own id
    int sTypes[4];   // segment footprints (0 = none); +0x200 = id in the options list, +0x400 = +0xC0 false, +0x800 = no hit
    int fpKind;      // target footprint: 0 none, else its kind; +0x100 = point inside
    DWORD bitsA, bitsB, a7, a10, ign;
    bool rec10000, tc, slot, cacheBad;
    LONG code;       // expected: >= 0 code (id = expLo below), -1 - RTO_FB_* = that fallback
    DWORD lo;
};
}  // namespace rto

static void TestRouteToObjRO() {
    using namespace rto;
    Ts3nCounters& k = ts3native_counters;
    static_assert(offsetof(Ts3nCounters, rtoReady) == 0x3FF0 && offsetof(Ts3nCounters, rtoMode) == 0x3FF4 &&
                      offsetof(Ts3nCounters, rtoFb) == 0x4010 && offsetof(Ts3nCounters, rtoCode) == 0x4048 &&
                      offsetof(Ts3nCounters, rtoShadowRuns) == 0x406C && offsetof(Ts3nCounters, rtoNested) == 0x4084 &&
                      sizeof(Ts3nCounters) == 6030 * 4,
                  "counters v93 layout after v95 (docs/registry.md)");
    assert(k.version == 912);
    assert(kRtoSites[0].site == 0x008316FF && kRtoSites[0].target == 0x004E17B0 && kRtoSites[1].site == 0x00831633 &&
           kRtoSites[1].target == 0x00830340 && kRtoSites[2].site == 0x00831568 && kRtoSites[2].target == 0x004E16F0);
    memset(&k.rtoReady, 0, sizeof(Ts3nCounters) - offsetof(Ts3nCounters, rtoReady));
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
    u8* const savedW = g_p5bW;
    g_p5bW = (u8*)VirtualAlloc(NULL, 0x1000, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    u8* world = g_p5bW;
    void* lock = world + 0x918;
    InitializeCriticalSection((CRITICAL_SECTION*)lock);
    const DWORD me = GetCurrentThreadId();
    static DWORD simTid;
    simTid = me;
    // saved globals
    const DWORD* savedSim = g_rtSimTidSlot;
    const DWORD savedWorker = g_workerTid;
    void* const savedRgLock = g_rgLock;
    const AsyncSlot savedAsync = g_async;
    const LONG savedArmed = g_twArmed, savedOwner = k.rgOwnerTid;
    const SrWorldFn savedWorld = g_srWorldGet;
    const SrLotLookupFn savedLook = g_srLotLookup;
    const SrLotFindFn savedFind = g_srLotFind;
    const SrActiveLotFn savedAct = g_srActiveLot;
    const SrQueryFn savedQuery = g_srQuery;
    const SrVecRelFn savedRel = g_srVecRel;
    const SrFreeFn savedFree = g_srFree;
    const float* savedOne = g_srOne;
    const SrOptIdFn savedOptId = g_srOptId;
    const SrOptTestFn savedOptTest = g_srOptTest;
    const bool savedCheck = g_srCheckSlots;
    const LONG savedSlReady = k.slReady, savedSlMode = k.slMode;
    g_rtSimTidSlot = &simTid;
    g_workerTid = me + 7;
    g_rgLock = lock;
    g_async.owner = me, g_async.lock = lock, g_async.state = A_IDLE;
    k.rgOwnerTid = 0;
    g_srWorldGet = []() -> u8* { return g_p5bW; };
    g_srLotLookup = [](void* m, void*, float, float) -> u8* { assert(m == wmw); return qlot; };
    g_srLotFind = [](const void* tbl, void*, DWORD* o, const DWORD* key) {
        const DWORD* b = *(const DWORD* const*)((const u8*)tbl + 4);
        o[0] = key[0] == *(const DWORD*)(uintptr_t)b[0] ? b[0] : b[1];
    };
    g_srActiveLot = []() -> const u8* { return actLot; };
    g_srQuery = FkSrQuery, g_srVecRel = FkVecRel, g_srFree = FkFree;
    g_srOne = &one;
    g_srOptId = OptId, g_srOptTest = OptTest;
    g_srCheckSlots = false;
    g_rtoAcq = P5bAcq, g_rtoRel = P5bRel, g_rtoRoute = Route, g_rtoDeadline = &dl;
    g_rtoComp = Comp, g_rtoSlotGet = SlotGet, g_rtoPos = Pos, g_rtoMat = Pos, g_rtoHasSlot = HasSlot, g_rtoObjRec = ObjRec;
    g_rtoOptCopy = OptCopy, g_rtoOptSet = OptSet, g_rtoOptFree = OptFree, g_rtoSimList = SimList, g_rtoPortal = PortalTest;
    g_rtoQuery = Query, g_rtoGather = Gather;
    g_rtoKeyTc = &keyTc, g_rtoKeySlot = &keySlot, g_rtoObjTab = &tabPtr;
    g_rtoRadius = &radius, g_rtoSegR = &segR, g_rtoFar = &farThr, g_rtoHalf = &half;
    k.slReady = 1, k.slMode = 1;

    // fake world: planner with one lot (key 0x30), lot entry rows, the start level's cell graph and its three maps
    world[0x974] = 1;
    *(u8**)(world + 0x95C) = p;
    *(u8**)(world + 0x964) = wmw;
    *(DWORD*)(qlot + 0x48) = 0x30;
    *(DWORD*)(actLot + 0x48) = 0x77;
    *(DWORD*)node = 0x30;
    u8* entry = node + 0x10;
    *(DWORD*)entry = 0x30;
    float* rw = (float*)(entry + 0xA0);  // A0 B0 C0 D0
    rw[0] = rw[5] = rw[10] = rw[15] = 1.0f, rw[12] = 10.0f;
    float* bw = (float*)(entry + 0xE0);  // E0 F0 0x100 0x110
    bw[0] = bw[5] = bw[10] = bw[15] = 1.0f, bw[12] = -10.0f;
    qvec[0] = q;
    *(const u8***)(entry + 0x74) = qvec, *(const u8***)(entry + 0x78) = qvec + 1;
    bk[0] = (DWORD)(uintptr_t)node, bk[1] = 0x5E117E1;
    *(DWORD**)(p + 0x4C) = bk, *(DWORD*)(p + 0x50) = 1;
    *(u8**)(q + 0x20) = root;
    *(u8**)(root + 0x3C) = map1, *(u8**)(root + 0x30) = map2, *(u8**)(root + 0x24) = map3;
    *(DWORD**)(map1 + 4) = mbk1, *(DWORD*)(map1 + 8) = 1;
    *(DWORD**)(map2 + 4) = mbk2, *(DWORD*)(map2 + 8) = 1;
    *(DWORD**)(map3 + 4) = mbk3, *(DWORD*)(map3 + 8) = 1;
    *(DWORD*)node3 = 0x900, *(u8**)(node3 + 8) = holder;
    *(u8**)(holder + 8) = fpT, *(u8**)(holder + 0xC) = q;
    *(DWORD*)(sim + 0x48) = 0x30;
    *(DWORD*)(target + 8) = 0x900;
    pt[0] = 3.0f, pt[2] = 5.0f, pt[3] = 1.0f;  // level (+0x24) 0
    fvt[0] = (void*)Levels, fvt[3] = (void*)Type, fvt[0x44 / 4] = (void*)F44, fvt[0x7C / 4] = (void*)Id, fvt[0x84 / 4] = (void*)Id;
    fvt[0x88 / 4] = (void*)Portal, fvt[0xC0 / 4] = (void*)C0, fvt[0xD8 / 4] = (void*)Seg, fvt[0xB8 / 4] = (void*)Inside;
    fvt[0xBC / 4] = (void*)Near, fvt[0xC4 / 4] = (void*)C4;
    svt[1] = (void*)SubId, svt[2] = (void*)SubType;

    // hand-derived cases (docs/140 §12.1): segment ids 0x600 + i, query ids 0x500 + i; options bit 4 = "option 4"
    const int F = -1;  // fallback marker base: code = F - RTO_FB_*
    static const Case cases[] = {
        {"nothing", {3}, {0}, 0, 0, 0, 0, 1, 0, false, true, false, false, 0, 0},
        {"query: blocking type 0x11", {3, 0x11}, {9}, 0, 0, 0, 0, 1, 0, false, true, false, false, 4, 0},
        {"query: the target's own blocking footprint skipped", {0x10C}, {9}, 0, 0, 0, 0, 1, 0, false, true, false, false, 6, 0x600},
        {"no transform component", {3}, {9}, 0, 0, 0, 0, 1, 0, false, false, false, false, 0, 0},
        {"slot component missing", {3}, {9}, 0, 0, 0, 0, 1, 0, false, true, true, false, 8, 0},
        {"segment: 0x14 with option 4", {0}, {0x14}, 0, 4, 0, 0, 1, 0, false, true, false, false, 1, 0},
        {"segment: 0x14 without option 4", {0}, {0x14}, 0, 0, 0, 0, 1, 0, false, true, false, false, 0, 0},
        {"segment: nothing hit, option 4 (physics tail)", {0}, {0x814}, 0, 0, 0, 1, 1, 0, false, true, false, false, F - RTO_FB_TAIL, 0},
        {"segment: 0x10 without option 0x800", {0}, {0x10}, 0, 0, 0, 0, 1, 0, false, true, false, false, 0, 0},
        {"segment: 0x10 with option 0x800", {0}, {0x10}, 0, 0, 0x800, 0, 1, 0, false, true, false, false, 7, 0},
        {"segment: 0x13 with a10", {0}, {0x13}, 0, 0, 0, 0, 1, 0, false, true, false, false, 0, 0},
        {"segment: 0x13 without a10", {0}, {0x13}, 0, 0, 0, 0, 0, 0, false, true, false, false, 7, 0},
        {"segment: 0xC", {0}, {0xC}, 0, 0, 0, 0, 1, 0, false, true, false, false, 4, 0},
        {"segment: 0xC with option 1", {0}, {0xC}, 0, 1, 0, 0, 1, 0, false, true, false, false, 0, 0},
        {"segment: portal", {0}, {1}, 0, 0, 0, 0, 1, 0, false, true, false, false, 3, 0x600},
        {"segment: portal with option 2", {0}, {1}, 0, 2, 0, 0, 1, 0, false, true, false, false, 0, 0},
        {"segment: merge 9 then 0xC then 7", {0}, {0xC, 9, 0x13}, 0, 0, 0, 0, 0, 0, false, true, false, false, 7, 0},
        {"segment: 9 beats 4", {0}, {0xC, 9}, 0, 0, 0, 0, 1, 0, false, true, false, false, 6, 0x601},
        {"segment: caller's ignore id", {0}, {9}, 0, 0, 0, 0, 1, 0x600, false, true, false, false, 0, 0},
        {"segment: id in the options' lists", {0}, {0x209}, 0, 0, 0, 0, 1, 0, false, true, false, false, 0, 0},
        {"segment: +0xC0 false", {0}, {0x409}, 0, 0, 0, 0, 1, 0, false, true, false, false, 0, 0},
        {"segment: no segment hit", {0}, {0x809}, 0, 0, 0, 0, 1, 0, false, true, false, false, 0, 0},
        {"target footprint: point inside", {0}, {9}, 0x101, 0, 0, 0, 1, 0, false, true, false, false, 0, 0},
        {"target footprint kind 3", {0}, {0xC}, 3, 0, 0, 0, 1, 0, false, true, false, false, 4, 0},
        {"target footprint kind 1, record flag off", {0}, {0xC}, 1, 0, 0, 0, 1, 0, false, true, false, false, 4, 0},
        {"target footprint kind 1, record flag on (option 1 forced)", {0}, {0xC}, 1, 0, 0, 0, 1, 0, true, true, false, false, 0, 0},
        {"target footprint kind 9: shifted second check", {0}, {0x17}, 9, 0, 0, 0, 1, 0, false, true, false, false, 4, 0},
        {"option 0x100000", {0}, {9}, 0, 0x100000, 0, 0, 1, 0, false, true, false, false, F - RTO_FB_OPT, 0},
        {"polygon cache invalid", {0}, {9}, 0, 0, 0, 0, 1, 0, false, true, false, true, F - RTO_FB_CACHE, 0},
    };
    const int nCases = (int)(sizeof(cases) / sizeof(cases[0]));
    for (int ci = 0; ci < nCases; ci++) {
        const Case& c = cases[ci];
        nq = ns = 0;
        for (int i = 0; i < 3 && c.qTypes[i]; i++) {
            const int t = c.qTypes[i];
            MakeFp(fp[i], cache[i], t & 0x100 ? 0x900 : 0x500 + i, t & 0xFF, 1);
            qList[nq++] = fp[i];
        }
        for (int i = 0; i < 4 && c.sTypes[i]; i++) {
            const int t = c.sTypes[i];
            MakeFp(fp[4 + i], cache[4 + i], 0x600 + i, t & 0xFF, !(t & 0x800));
            fp[4 + i][0x3C] = (t & 0x400) != 0;
            if (c.cacheBad) cache[4 + i][0x20] = 0;
            sList[ns++] = fp[4 + i];
        }
        *(DWORD*)(opts + 8) = c.sTypes[0] & 0x200 ? 0x600 : 0;
        *(DWORD*)opts = c.bitsA, *(DWORD*)(opts + 4) = c.bitsB;
        *(DWORD*)(sim + 0x200) = c.ign;
        mbk3[0] = c.fpKind ? (DWORD)(uintptr_t)node3 : 0;
        MakeFp(fpT, cacheT, 0x900, c.fpKind & 0xFF, 0);
        fpT[0x37] = (c.fpKind & 0x100) != 0;
        *(DWORD*)(objrec + 0xD0) = c.rec10000 ? 0x10000 : 0;
        tcOn = c.tc, hasSlot = c.slot, slotCompOn = false;
        const bool fb = c.code < 0;
        expCode = fb ? 0x77 : (DWORD)c.code, expLo = fb ? 0x7777 : c.lo, expHi = 0;
        for (int mode = 0; mode < 2; mode++) {  // 0: Mode 1 with a job "running", 1: shadow with no job
            k.rtoMode = mode == 0, k.rtoShadow = mode == 1;
            g_async.state = mode == 0 ? A_RUNNING : A_IDLE;
            g_twArmed = mode == 0;
            const LONG taken0 = k.rtoTaken, ref0 = refCalls, sr0 = k.rtoShadowRuns, mm0 = k.rtoShadowMismatch + k.rtoShadowQueryMismatch;
            const LONG fb0 = fb ? k.rtoFb[F - c.code] : 0, sfb0 = k.rtoShadowFb;
            gatherIdx = 0;
            Emu(c.a7, c.a10);
            const bool ok = out[0] == expCode && out[2] == expLo && out[3] == expHi && t_rtoN == 0 &&
                            ((CRITICAL_SECTION*)lock)->RecursionCount == 0 && refUnlocked == 0 && g_rgOffSimOpen == 0;
            bool ok2;
            if (mode == 0) ok2 = fb ? refCalls == ref0 + 1 && k.rtoFb[F - c.code] == fb0 + 1 : refCalls == ref0 && k.rtoTaken == taken0 + 1;
            else ok2 = refCalls == ref0 + 1 && k.rtoShadowRuns == sr0 + 1 && k.rtoShadowMismatch + k.rtoShadowQueryMismatch == mm0 &&
                       k.rtoShadowFb == sfb0 + (fb ? 1 : 0);
            if (!ok || !ok2) {
                printf("RouteToObjRO case '%s' %s: got %ld %08lX:%08lX, want %ld %08lX:%08lX (refs %ld, taken %ld, fb %ld)\n", c.name,
                       mode ? "shadow" : "mode 1", out[0], out[3], out[2], expCode, expHi, expLo, refCalls - ref0, k.rtoTaken - taken0,
                       fb ? k.rtoFb[F - c.code] - fb0 : 0);
                assert(false);
            }
            if (c.fpKind == 9 && !fb) assert(gatherIdx == 2 && lastU1[0] == 11.0f && lastU1[1] == 11.5f);  // goal 1, shifted 1.5
            for (int i = 0; i < 8; i++) assert(*(const DWORD*)(fp[i] + 8) == 1 || !*(const void* const*)fp[i]);
        }
        if (c.code == F - RTO_FB_TAIL) {  // [RouteToObjRO] Tail (rtotail-test.h stubs): the clone runs the tail, no fallback
            const LONG runs0 = k.rtoTailRuns;
            rtt::Use(), k.rtoTailMode = 1;
            for (int pass = 0; pass < 4; pass++) {  // bit 0: shadow, bit 1: a physics blocker at sample 1
                const bool sh = (pass & 1) != 0, hit = (pass & 2) != 0;
                rtt::Reset();
                rtt::ids[0][0] = 0xABC, rtt::ids[0][1] = 0, rtt::nIds = 1;
                rtt::blockers[0][0] = 0xABC, rtt::blockers[0][1] = 0, rtt::nBlockers = 1;
                rtt::hitAt = hit ? 1 : -1;
                expCode = hit ? 1 : 0, expLo = hit ? 0xABC : 0, expHi = 0;
                k.rtoMode = !sh, k.rtoShadow = sh;
                g_async.state = sh ? A_IDLE : A_RUNNING;
                g_twArmed = !sh;
                const LONG taken0 = k.rtoTaken, ref0 = refCalls, mm0 = k.rtoShadowMismatch + k.rtoShadowQueryMismatch;
                const LONG sfb0 = k.rtoShadowFb, tfb0 = k.rtoFb[RTO_FB_TAIL];
                gatherIdx = 0;
                Emu(c.a7, c.a10);
                assert(out[0] == expCode && out[2] == expLo && out[3] == 0 && t_rtoN == 0 &&
                       ((CRITICAL_SECTION*)lock)->RecursionCount == 0 && refUnlocked == 0 && g_rgOffSimOpen == 0);
                assert(rtt::ranges == (hit ? 2 : 5) && rtt::frees == (hit ? 1 : 0) && k.rtoFb[RTO_FB_TAIL] == tfb0);
                if (!sh) assert(refCalls == ref0 && k.rtoTaken == taken0 + 1);
                else assert(refCalls == ref0 + 1 && k.rtoShadowMismatch + k.rtoShadowQueryMismatch == mm0 && k.rtoShadowFb == sfb0);
            }
            assert(k.rtoTailRuns == runs0 + 4);
            k.rtoTailMode = 0, rtt::Restore();
        }
    }
    // a skewed clone in shadow (the game answers otherwise): one mismatch
    {
        nq = ns = 0;
        MakeFp(fp[4], cache[4], 0x600, 9, 1);
        sList[ns++] = fp[4];
        *(DWORD*)opts = *(DWORD*)(opts + 4) = *(DWORD*)(opts + 8) = 0;
        *(DWORD*)(sim + 0x200) = 0;
        mbk3[0] = 0, tcOn = true, hasSlot = false;
        expCode = 7, expLo = 0;
        k.rtoMode = 0, k.rtoShadow = 1, g_async.state = A_IDLE;
        const LONG mm0 = k.rtoShadowMismatch;
        Emu();
        assert(k.rtoShadowMismatch == mm0 + 1 && out[0] == 7);
        k.rtoShadowMismatch = mm0;
    }
    // state machine: no route part (no lock, counted), switch off (the game's path), +0x918 owner (no skip), nesting
    {
        expCode = 6, expLo = 0x600;
        k.rtoMode = 1, k.rtoShadow = 0, g_async.state = A_RUNNING, g_twArmed = 1;
        const LONG nr0 = k.rtoNoRoute, ref0 = refCalls, calls0 = k.rtoCalls;
        Emu(0, 1, false);
        assert(k.rtoNoRoute == nr0 + 1 && refCalls == ref0 && k.rtoCalls == calls0 + 1 && t_rtoN == 0 &&
               ((CRITICAL_SECTION*)lock)->RecursionCount == 0);
        k.rtoMode = 0;
        Emu();
        assert(refCalls == ref0 + 1 && k.rtoCalls == calls0 + 1 && out[0] == 6);
        k.rtoMode = 1, k.rgOwnerTid = (LONG)me;  // the sim owns +0x918 (a lent or recursive hold): the game's path
        Emu();
        assert(refCalls == ref0 + 2 && k.rtoCalls == calls0 + 1);
        k.rgOwnerTid = 0;
        // nested 0x831530 inside a skipped one: the inner takes the game's path, the outer still answers
        ((LockAcquireFn)(void*)RtoAcqStub)(lock, NULL, &dl);
        assert(t_rtoN == 1 && t_rtoSt[0] == RTO_SKIP);
        const LONG n0 = k.rtoNested;
        Emu();
        assert(k.rtoNested == n0 + 1 && refCalls == ref0 + 3 && t_rtoN == 1);
        RtoCallStub(world, NULL, out, sim, target, pt, opts, 0x5A, 0, 0, 0, 1);
        RtoRelStub(lock, NULL);
        assert(t_rtoN == 0 && out[0] == 6 && refCalls == ref0 + 3 && ((CRITICAL_SECTION*)lock)->RecursionCount == 0);
        // no Sim (the game's 0x830352: code 0, nothing read): answered without the clone, the game or the lock
        ((LockAcquireFn)(void*)RtoAcqStub)(lock, NULL, &dl);
        assert(t_rtoN == 1 && t_rtoSt[0] == RTO_SKIP);
        out[0] = out[2] = out[3] = 0xEEEEEEEE;
        const LONG taken0n = k.rtoTaken;
        assert(RtoCallStub(world, NULL, out, NULL, target, pt, opts, 0x5A, 0, 0, 0, 1) == out && !out[0] && !out[2] && !out[3]);
        assert(t_rtoSt[0] == RTO_ANSWERED && refCalls == ref0 + 3 && k.rtoTaken == taken0n);
        RtoRelStub(lock, NULL);
        assert(t_rtoN == 0 && ((CRITICAL_SECTION*)lock)->RecursionCount == 0);
    }

    // threaded: the worker holds +0x918 per job and flips the world between A (segment 9: 6, 0x600) and B (query 0x11: 4)
    // under QL exclusive; each sim answer is one of the two (clone or late lock), never torn
    {
        static u8 fA[0x80], fB[0x80], cA[0x24], cB[0x24];
        MakeFp(fA, cA, 0x600, 9, 1);
        MakeFp(fB, cB, 0x501, 0x11, 1);
        static LONG taken0;
        struct W {
            static void Set(bool b) {  // under QL exclusive (worker) or with no worker (setup)
                InterlockedIncrement(&gen1);
                nq = 0, ns = 0;
                if (b) qList[nq++] = fB;
                else sList[ns++] = fA;
                P5bSpin(5);
                InterlockedIncrement(&gen2);
            }
            static DWORD WINAPI Worker(LPVOID) {
                // 120 jobs, then more until the sim has 64 answers (a loaded machine can starve the sim thread through all 120
                // jobs: 0 answered in 2 of 6 busy runs); bounded below the sim side's 20 s HANG
                const ULONGLONG t0 = GetTickCount64();
                for (int j = 0; j < 120 || (ts3native_counters.rtoTaken - taken0 < 64 && GetTickCount64() - t0 < 15000); j++) {
                    P5bAcq(g_p5bW + 0x918, NULL, &dl);
                    g_async.lock = g_p5bW + 0x918;
                    TwArm(true);
                    InterlockedExchange(&g_async.state, A_RUNNING);
                    for (int m = 0; m < 6; m++) {
                        QlAcquireEx();
                        Set(((j + m) & 1) != 0);
                        QlReleaseEx();
                        P5bSpin(60);
                    }
                    InterlockedExchange(&g_async.state, A_IDLE);
                    TwArm(false);
                    P5bRel(g_p5bW + 0x918, NULL);
                    P5bSpin(30);
                }
                return 0;
            }
        };
        *(DWORD*)opts = *(DWORD*)(opts + 4) = *(DWORD*)(opts + 8) = 0;
        *(DWORD*)(sim + 0x200) = 0;
        mbk3[0] = 0, tcOn = true, hasSlot = false;
        W::Set(false);
        k.rtoMode = 1, k.rtoShadow = 0;
        g_twArmed = 0;
        taken0 = k.rtoTaken;
        const LONG ref0 = refCalls;
        DWORD wtid = 0;
        HANDLE w = CreateThread(NULL, 0, W::Worker, NULL, 0, &wtid);
        g_workerTid = wtid;
        LONG calls = 0, bad = 0;
        // the game's answer for a late take: the world as it is under the lock (the worker flips only inside its hold)
        g_rtoRoute = [](u8*, void*, DWORD* o, u8*, u8*, const float*, u8*, DWORD, DWORD, DWORD, DWORD, DWORD) -> DWORD* {
            InterlockedIncrement(&refCalls);
            if (!P5bMine()) InterlockedIncrement(&refUnlocked);
            const bool b = nq != 0;
            o[0] = b ? 4 : 6, o[2] = b ? 0 : 0x600, o[3] = 0;
            return o;
        };
        const ULONGLONG t0 = GetTickCount64();
        while (WaitForSingleObject(w, 0) != WAIT_OBJECT_0) {
            Emu();
            calls++;
            if (!((out[0] == 6 && out[2] == 0x600) || (out[0] == 4 && out[2] == 0)) || out[3]) bad++;
            if (GetTickCount64() - t0 > 20000) { printf("RouteToObjRO threaded HANG\n"); assert(false); }
        }
        CloseHandle(w);
        g_rtoRoute = Route;
        const LONG taken = k.rtoTaken - taken0, late = refCalls - ref0;
        if (bad || torn || refUnlocked || !taken)
            printf("RouteToObjRO threaded FAIL: %ld calls, %ld bad, %ld torn, %ld unlocked game calls, %ld answered\n", calls, bad, torn,
                   refUnlocked, taken);
        assert(bad == 0 && torn == 0 && refUnlocked == 0 && taken > 0 && t_rtoN == 0);
        assert(*(const DWORD*)(fA + 8) == 1 && *(const DWORD*)(fB + 8) == 1);
        printf("RouteToObjRO ([RouteToObjRO] Enabled=1): counters v93; %d hand-derived cases x (Mode 1 job, shadow) equal incl. "
               "kind 9's shifted check and 3 fallbacks; shadow skew counted; no-route skip / switch off / +0x918 owner / nesting; "
               "threaded (real +0x918 + QL, worker flipping the world): %ld calls, %ld answered read-only, %ld by the game's call (between "
               "jobs, QL busy or late), 0 torn, "
               "0 unlocked game calls, no write\n",
               nCases, calls, taken, late);
    }

    k.rtoMode = k.rtoShadow = 0;
    g_rtoAcq = (LockAcquireFn)(uintptr_t)RTO_LOCK_ACQ, g_rtoRel = (LockReleaseFn)(uintptr_t)RTO_LOCK_REL;
    g_rtoRoute = (RtoRouteFn)(uintptr_t)RTO_ROUTE, g_rtoDeadline = (const DWORD*)(uintptr_t)0x01021CF0;
    g_rtoQuery = RtoQueryGame, g_rtoGather = RtoGatherGame;
    g_srWorldGet = savedWorld, g_srLotLookup = savedLook, g_srLotFind = savedFind, g_srActiveLot = savedAct;
    g_srQuery = savedQuery, g_srVecRel = savedRel, g_srFree = savedFree, g_srOne = savedOne;
    g_srOptId = savedOptId, g_srOptTest = savedOptTest, g_srCheckSlots = savedCheck;
    k.slReady = savedSlReady, k.slMode = savedSlMode;
    DeleteCriticalSection((CRITICAL_SECTION*)lock);
    VirtualFree(g_p5bW, 0, MEM_RELEASE);
    g_p5bW = savedW;
    g_rtSimTidSlot = savedSim, g_workerTid = savedWorker, g_rgLock = savedRgLock, g_async = savedAsync;
    g_twArmed = savedArmed, k.rgOwnerTid = savedOwner;
}
