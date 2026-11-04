// TestRouteToObjTail ([RouteToObjRO] Tail, rtoro.cpp RtoTail, docs/140 §5.3 phase P): BlockerCheck2d's physics tail with stub
// physics calls: the sample points (the game's SSE lanes), radius and flags, the vector handed in empty with its allocator
// name, the skip rules (no blocker, the target's id, the ignore id), the first hit answers code 1 and ends the samples, a
// range result other than 1 is no hit, every vector freed, no scene -> no call. Included by selftest.cpp before rtoro-test.h,
// whose physics-tail case uses these stubs to run the tail through the clone (Mode 1 and shadow).
namespace rtt {
static __declspec(align(16)) u8 phys[16];
static int ranges, frees, blockerCalls, hitAt = -1;
static u8 ret = 1;
static __declspec(align(16)) float pts[8][4];
static float radius;
static DWORD flags, freed[8];
static DWORD ids[8][2];
static int nIds;
static DWORD blockers[8][2];
static int nBlockers;
static RtoPhysFn sPhys;
static RtoRangeFn sRange;
static RtoBlockerFn sBlocker;
static RtoMemFreeFn sMemFree;
static void* __cdecl Phys() { return phys; }
static u8 __fastcall Range(void* scene, void*, const float* pt, float r, DWORD fl, void* vec) {
    assert(scene == (void*)(uintptr_t)0x1234 && ranges < 8);
    RtoVec* v = (RtoVec*)vec;
    assert(!v->b && !v->e && !v->cap && v->name == RTO_RANGE_NAME);
    memcpy(pts[ranges], pt, 16), radius = r, flags = fl;
    if (ranges++ == hitAt) v->b = &ids[0][0], v->e = &ids[nIds][0], v->cap = v->e;
    return ret;
}
static u8 __cdecl Blocker(DWORD lo, DWORD hi) {
    blockerCalls++;
    for (int i = 0; i < nBlockers; i++)
        if (blockers[i][0] == lo && blockers[i][1] == hi) return 1;
    return 0;
}
static void __cdecl MemFree(void* p) {
    assert(frees < 8);
    freed[frees++] = (DWORD)(uintptr_t)p;
}
static void Use() {
    sPhys = g_rtoPhys, sRange = g_rtoRange, sBlocker = g_rtoBlocker, sMemFree = g_rtoMemFree;
    g_rtoPhys = Phys, g_rtoRange = Range, g_rtoBlocker = Blocker, g_rtoMemFree = MemFree;
    *(void**)(phys + 8) = (void*)(uintptr_t)0x1234;
}
static void Restore() { g_rtoPhys = sPhys, g_rtoRange = sRange, g_rtoBlocker = sBlocker, g_rtoMemFree = sMemFree; }
static void Reset() { ranges = frees = blockerCalls = nIds = nBlockers = 0, hitAt = -1, ret = 1; }
}  // namespace rtt

static void TestRouteToObjTail() {
    using namespace rtt;
    Ts3nCounters& k = ts3native_counters;
    const LONG runs0 = k.rtoTailRuns, hits0 = k.rtoTailHits;
    Use();
    static RtoIn in;
    static __declspec(align(16)) float point[4] = {1.0f, 2.0f, 3.0f, 4.0f};
    static u8 target[16];
    *(DWORD*)(target + 8) = 0x900, *(DWORD*)(target + 0xC) = 9;
    in.point = point, in.target = target, in.a8 = 0x700, in.a9 = 7;
    g_rtoTailGoal[0] = 9.0f, g_rtoTailGoal[1] = 7.0f;
    DWORD res[4] = {};
    // no hit: five samples at i * ((9, 2, 7, 9) - start) * 0.25 + start, radius 0.15, flags 0x10060; code 0
    Reset();
    RtoTail(in, res);
    assert(ranges == 5 && frees == 0 && res[0] == 0 && k.rtoTailRuns == runs0 + 1 && k.rtoTailHits == hits0);
    for (int i = 0; i < 5; i++)
        assert(pts[i][0] == 1.0f + 2.0f * i && pts[i][1] == 2.0f && pts[i][2] == 3.0f + i && pts[i][3] == 4.0f + 1.25f * i);
    assert(radius == 0.15f && flags == 0x10060);
    // a hit at sample 2: not a blocker, the target's id and the ignore id are skipped, the first blocker answers, the samples
    // end, the vector is freed
    static const DWORD kIds[5][2] = {{0x100, 1}, {0x900, 9}, {0x700, 7}, {0x200, 2}, {0x300, 3}};
    static const DWORD kBl[4][2] = {{0x900, 9}, {0x700, 7}, {0x200, 2}, {0x300, 3}};
    Reset();
    memcpy(ids, kIds, sizeof(kIds)), nIds = 5, memcpy(blockers, kBl, sizeof(kBl)), nBlockers = 4, hitAt = 2;
    RtoTail(in, res);
    assert(ranges == 3 && frees == 1 && freed[0] == (DWORD)(uintptr_t)&ids[0][0] && blockerCalls == 4);
    assert(res[0] == 1 && res[2] == 0x200 && res[3] == 2 && k.rtoTailHits == hits0 + 1);
    // the target's low word with another high word is not the target (both words compared)
    Reset(), res[0] = res[2] = res[3] = 0;
    ids[0][0] = 0x900, ids[0][1] = 8, nIds = 1, blockers[0][0] = 0x900, blockers[0][1] = 8, nBlockers = 1, hitAt = 0;
    RtoTail(in, res);
    assert(ranges == 1 && frees == 1 && res[0] == 1 && res[2] == 0x900 && res[3] == 8);
    // a range result other than 1: no hit, the vector still freed, all five samples
    Reset(), res[0] = res[2] = res[3] = 0;
    memcpy(ids, kIds, sizeof(kIds)), nIds = 5, memcpy(blockers, kBl, sizeof(kBl)), nBlockers = 4, hitAt = 0, ret = 0;
    RtoTail(in, res);
    assert(ranges == 5 && frees == 1 && blockerCalls == 0 && res[0] == 0);
    // blockers that are only the target or the ignore id: no hit
    Reset();
    memcpy(ids, kIds + 1, 2 * sizeof(kIds[0])), nIds = 2, memcpy(blockers, kBl, sizeof(kBl)), nBlockers = 4, hitAt = 1;
    RtoTail(in, res);
    assert(ranges == 5 && frees == 1 && blockerCalls == 2 && res[0] == 0);
    // no scene: no call
    Reset();
    *(void**)(phys + 8) = NULL;
    RtoTail(in, res);
    assert(ranges == 0 && res[0] == 0);
    Restore();
    k.rtoTailRuns = runs0, k.rtoTailHits = hits0;
    puts("TestRouteToObjTail: sample points, radius, flags, skip rules, first hit, result 1 only, vectors freed, no scene");
}
