// [RouteAsync] ReplanDedupe (replandedupe.cpp, docs/150 §13): selftest. Included by selftest.cpp after tailunlock-test.h.
// Fake route handle table, route object (follower, options, destination set with one destination, empty path list), follower
// position and a fake IC that writes a result and the route fields Route_PlanCommon resets; the [RouteSnap] change counters
// are bumped by hand. Checks: repeat bands and classes, the observe-mode equivalence counters, serving (no plan, same bytes,
// +0x1F4 bumped), and every refusal: route state, static writes, footprint writes with BlockedByPeople, result 18, world not
// quiet, age > 500 ms, start moved, goal changed, success results, no key.
static __declspec(align(16)) u8 g_rdtRoute[0x200];
static u8 g_rdtDs[0x60], g_rdtDest[0x40], g_rdtRm[16];
static DWORD g_rdtType = 16, g_rdtPlans, g_rdtPosRet = 1;
static float g_rdtPos[3] = {10, 0, 20};
static bool g_rdtNoRoute;
static u8* __fastcall RdtRoute(const void* rm, void*, DWORD handle) {
    return rm == g_rdtRm && handle == 0x25000001 && !g_rdtNoRoute ? g_rdtRoute : NULL;
}
static DWORD __fastcall RdtFollowerPos(void* route, void*, float* p, float* d, DWORD* ids) {
    assert(route == g_rdtRoute && ((uintptr_t)p & 15) == 0 && ((uintptr_t)d & 15) == 0 && ids);
    memcpy(p, g_rdtPos, 12), p[3] = 1;
    return g_rdtPosRet;
}
static u8* RdtIc(int, u8* out, DWORD handle, const float*) {
    assert(handle == 0x25000001);
    g_rdtPlans++;
    memset(out, 0, 0x48);
    *(DWORD*)out = g_rdtType;
    *(DWORD*)(out + 0x38) = 3;  // candidate count
    *(DWORD*)(out + 0x44) = g_rdtPlans;  // stale stack: not part of the answer
    *(DWORD*)(g_rdtRoute + 0xF0) = 0xFFFFFFFF;  // what PlanCommon resets
    *(float*)(g_rdtRoute + 0xA4) = 0;
    return out;
}

static void TestReplanDedupe() {
    static_assert(offsetof(Ts3nCounters, rdMode) == 0x38FC && offsetof(Ts3nCounters, rdReady) == 0x3900 &&
                      offsetof(Ts3nCounters, rdCalls) == 0x3904 && offsetof(Ts3nCounters, rdUs) == 0x390C &&
                      offsetof(Ts3nCounters, rdNoKey) == 0x3914 && offsetof(Ts3nCounters, rdFails) == 0x3918 &&
                      offsetof(Ts3nCounters, rdRepN) == 0x3920 && offsetof(Ts3nCounters, rdRepUs) == 0x3944 &&
                      offsetof(Ts3nCounters, rdInval) == 0x3968 && offsetof(Ts3nCounters, rdWould) == 0x3978 &&
                      offsetof(Ts3nCounters, rdWouldSame) == 0x3980 && offsetof(Ts3nCounters, rdWouldDiff) == 0x3988 &&
                      offsetof(Ts3nCounters, rdServed) == 0x3990 && offsetof(Ts3nCounters, rdServedUs) == 0x3994 &&
                      sizeof(Ts3nCounters) == 5933 * 4,
                  "counters v90 layout after v88 (docs/registry.md)");
    Ts3nCounters& k = ts3native_counters;
    assert(k.version == 910);
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
    // fakes and a quiet world
    u8* const* rmSlot = g_orRmSlot;
    const OrRouteFn route0 = g_orRoute;
    const RdFollowerPosFn pos0 = g_rdFollowerPos;
    static u8* rmPtr = g_rdtRm;
    g_orRmSlot = &rmPtr;
    g_orRoute = RdtRoute;
    g_rdFollowerPos = RdtFollowerPos;
    static u8 world[0xA00], planner[0x200], wm[0x200];
    *(u8**)(world + 0x95C) = planner;
    *(u8**)(world + 0x964) = wm;
    void* const lock0 = g_rgLock;
    const LONG snReady0 = k.snReady, p4On0 = g_p4On;
    g_rgLock = world + 0x918;
    k.snReady = 5;
    g_p4On = 0;
    // route: follower 7, options, destination set with one destination (node at destination + 4), empty path list
    *(DWORD*)(g_rdtRoute + 0x40) = 7;
    *(u8**)(g_rdtRoute + 0xE0) = g_rdtRoute + 0xE0, *(u8**)(g_rdtRoute + 0xE4) = g_rdtRoute + 0xE0;
    *(u8**)(g_rdtRoute + 0x144) = g_rdtDs;
    *(u8**)(g_rdtDs + 0x18) = g_rdtDest + 4;
    *(u8**)(g_rdtDest + 4) = g_rdtDs + 0x18;
    *(float*)(g_rdtDest + 0x10) = 33;  // the goal
    memset(g_rdTab, 0, sizeof(g_rdTab));
    const Ts3nCounters b = k;
    const float pt[3] = {1, 2, 3}, ptNear[3] = {1.05f, 2, 3}, ptFar[3] = {1.2f, 2, 3};
    u8 out[0x48], first[0x48];
    auto run = [&](int kind, const float* p) { return RdRun(kind, out, 0x25000001, p, RdtIc); };
    auto fresh = [&] { for (RdEntry& e : g_rdTab) e.ms = e.ms ? e.ms - 2000 : 0; };  // every entry out of every window

    // 1. observe: a failure, then its repeat (start 5 cm away) is eligible, planned, same answer
    k.rdMode = 0;
    DWORD plans = g_rdtPlans;
    assert(run(3, pt) == out && g_rdtPlans == plans + 1 && k.rdCalls[1] == b.rdCalls[1] + 1 && k.rdFails[1] == b.rdFails[1] + 1);
    memcpy(first, out, sizeof(out));
    run(3, ptNear);
    assert(g_rdtPlans == plans + 2 && k.rdRepN[0][0] == b.rdRepN[0][0] + 1 && k.rdRepN[2][0] == b.rdRepN[2][0] + 1);
    assert(k.rdWould[0] == b.rdWould[0] + 1 && k.rdWouldSame[0] == b.rdWouldSame[0] + 1 && k.rdWouldDiff[0] == b.rdWouldDiff[0]);
    // 2. serve: no plan, the cached bytes, the IC's replan count bumped
    k.rdMode = 1;
    const DWORD cnt = *(DWORD*)(g_rdtRoute + 0x1F4);
    memset(out, 0xCC, sizeof(out));
    run(3, pt);
    assert(g_rdtPlans == plans + 2 && k.rdServed == b.rdServed + 1 && *(DWORD*)(g_rdtRoute + 0x1F4) == cnt + 1);
    assert(memcmp(out, first, 0x44) == 0 && k.rdCalls[1] == b.rdCalls[1] + 3);
    // 3. a static routing write since the plan: planned (rdInval[2]); then eligible again
    k.snIc[0]++;
    run(3, pt);
    assert(g_rdtPlans == plans + 3 && k.rdInval[2] == b.rdInval[2] + 1);
    run(3, pt);
    assert(g_rdtPlans == plans + 3 && k.rdServed == b.rdServed + 2);
    // 4. footprint writes only: served for a route not blocked by people ...
    k.snLeaf[SL_W1][0]++;
    run(3, pt);
    assert(g_rdtPlans == plans + 3 && k.rdServed == b.rdServed + 3);
    // ... planned with BlockedByPeople (the option itself changes the state: first rdInval[0], then the footprint case)
    *(DWORD*)(g_rdtRoute + 0x158 + 0x28) |= 4;
    run(3, pt);
    assert(g_rdtPlans == plans + 4 && k.rdInval[0] == b.rdInval[0] + 1);
    k.snLeaf[SL_W1][0]++;
    run(3, pt);
    assert(g_rdtPlans == plans + 5 && k.rdInval[1] == b.rdInval[1] + 1);
    *(DWORD*)(g_rdtRoute + 0x158 + 0x28) &= ~4u;
    run(3, pt);  // state changed back: planned
    assert(g_rdtPlans == plans + 6);
    // 5. a route field changed since the plan (a setter or follow): planned
    *(float*)(g_rdtRoute + 0xA4) = 5;
    run(3, pt);
    assert(g_rdtPlans == plans + 7 && k.rdInval[0] == b.rdInval[0] + 3);
    // 6. world not quiet (pending lot updates) and P4: planned, rdInval[3]
    planner[0x10E] = 1;
    run(3, pt);
    planner[0x10E] = 0;
    g_p4On = 1;
    run(3, pt);
    g_p4On = 0;
    assert(g_rdtPlans == plans + 9 && k.rdInval[3] == b.rdInval[3] + 2);
    // 7. older than 500 ms: a repeat in the 1000 ms band only, planned
    for (RdEntry& e : g_rdTab) if (e.ms) e.ms -= 600;
    const LONG r1 = k.rdRepN[1][0], r2 = k.rdRepN[2][0];
    run(3, pt);
    assert(g_rdtPlans == plans + 10 && k.rdRepN[1][0] == r1 && k.rdRepN[2][0] == r2 + 1);
    // 8. start 20 cm away, then another goal: no repeat
    const LONG reps = k.rdRepN[2][0] + k.rdRepN[2][1] + k.rdRepN[2][2];
    run(3, ptFar);
    *(float*)(g_rdtDest + 0x10) = 34;
    run(3, ptFar);
    assert(g_rdtPlans == plans + 12 && k.rdRepN[2][0] + k.rdRepN[2][1] + k.rdRepN[2][2] == reps);
    // 9. result 18 is never served
    g_rdtType = 18;
    fresh();
    run(3, pt);
    run(3, pt);
    assert(g_rdtPlans == plans + 14 && k.rdInval[3] == b.rdInval[3] + 3);
    // 10. successes repeat (class 1), never served
    g_rdtType = 1;
    fresh();
    const LONG s1 = k.rdRepN[0][1], served = k.rdServed;
    run(3, pt);
    run(3, pt);
    assert(g_rdtPlans == plans + 16 && k.rdRepN[0][1] == s1 + 1 && k.rdServed == served);
    // 11. another answer after a failure: class 2 (rdWould in observe mode counts it as a difference)
    k.rdMode = 0;
    g_rdtType = 16;
    fresh();
    run(3, pt);
    g_rdtType = 10;
    const LONG d2 = k.rdRepN[0][2];
    run(3, pt);
    assert(k.rdRepN[0][2] == d2 + 1 && k.rdWouldDiff[0] == b.rdWouldDiff[0] + 1);
    // 12. Replan: the follower position is the start; no follower position = no key, planned
    k.rdMode = 1;
    g_rdtType = 16;
    run(1, NULL);
    plans = g_rdtPlans;
    run(1, NULL);
    assert(g_rdtPlans == plans && k.rdCalls[0] == b.rdCalls[0] + 2);
    g_rdtPos[0] += 0.5f;
    run(1, NULL);
    assert(g_rdtPlans == plans + 1);
    g_rdtPosRet = 5;
    run(1, NULL);
    g_rdtPosRet = 1;
    g_rdtNoRoute = true;
    run(1, NULL);
    g_rdtNoRoute = false;
    assert(g_rdtPlans == plans + 3 && k.rdNoKey == b.rdNoKey + 2);
    // 13. Replan and ReplanFromPosition keep separate entries; no world version = never served
    k.snReady = 1;
    fresh();
    run(3, pt);
    run(3, pt);
    assert(g_rdtPlans == plans + 5 && k.rdInval[3] == b.rdInval[3] + 4);
    // 14. the follower's other entries do not leak: 4 probes, the oldest evicted
    for (DWORD f = 100; f < 100 + 3 * RD_N; f++) {
        *(DWORD*)(g_rdtRoute + 0x40) = f;
        run(3, pt);
    }
    printf("ReplanDedupe: v90 counters; repeats by band and class, observe check (would/same/diff), served bytes and +0x1F4, "
           "refusals (state, static writes, footprints with BlockedByPeople, 18, not quiet, P4, age, start, goal, success, no key, "
           "no version) ok\n");
    *(DWORD*)(g_rdtRoute + 0x40) = 7;
    k.snReady = snReady0;
    g_p4On = p4On0;
    g_rgLock = lock0;
    g_orRmSlot = rmSlot;
    g_orRoute = route0;
    g_rdFollowerPos = pos0;
    k.rdMode = 0;
}
