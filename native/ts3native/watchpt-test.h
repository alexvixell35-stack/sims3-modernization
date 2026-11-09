// [Watchpoint] (watchpt.cpp, docs/157 §8): selftest. Included by selftest.cpp. A test thread arms DR on its own local,
// writes it (caught), reads it (not caught: write-only), lets the main thread write it (caught: every thread is armed),
// disarms and writes again (not caught).
static volatile DWORD* g_wpTv;
static HANDLE g_wpEvArmed, g_wpEvMain;
static int g_wpTSlot = -1;

static DWORD WINAPI WpTestProc(LPVOID) {
    volatile DWORD v = 0;
    g_wpTv = &v;
    const int s = g_wpTSlot = WpArm((const void*)&v, 4, true);
    assert(s >= 0);
    const LONG h0 = g_wpHits[s];
    const DWORD r = v;  // a read: no hit
    assert(r == 0 && g_wpHits[s] == h0);
    v = 1;
    assert(g_wpHits[s] == h0 + 1 && g_wpLastEip[s] != 0);
    SetEvent(g_wpEvArmed);
    WaitForSingleObject(g_wpEvMain, INFINITE);  // the main thread wrote v = 2
    assert(v == 2 && g_wpHits[s] == h0 + 2);
    WpDisarm(s);
    v = 3;
    assert(g_wpHits[s] == h0 + 2);
    return 0;
}

// docs/157 §8.1: the Lot= arm site and the Writers= stubs on fake footprints. The stubs jump to copies of the game's two setter
// bodies (movss xmm0,[esp+4]; movss [ecx+off],xmm0; ret 4) in place of the trampolines.
static __declspec(naked) void FakeSetZ() { __asm movss xmm0, dword ptr [esp + 4] __asm movss dword ptr [ecx + 0x10], xmm0 __asm ret 4 }
static __declspec(naked) void FakeSetP30() { __asm movss xmm0, dword ptr [esp + 4] __asm movss dword ptr [ecx + 0x30], xmm0 __asm ret 4 }
typedef void(__fastcall* WrSetFn)(void* self, void* edx, float v);
static void TestWatchpointLot() {
    static_assert(offsetof(Ts3nCounters, wpArms) == 0x4628 && offsetof(Ts3nCounters, wpHits) == 0x462C && offsetof(Ts3nCounters, wrZ) == 0x4630 &&
                      offsetof(Ts3nCounters, wrP30) == 0x463C && sizeof(Ts3nCounters) == 6112 * 4,
                  "counters v110 layout after v109 (docs/registry.md)");
    static_assert(offsetof(Ts3nCounters, wpCtor) == 0x4648 && offsetof(Ts3nCounters, wpFollow) == 0x464C && offsetof(Ts3nCounters, rpHold) == 0x4654 &&
                      sizeof(Ts3nCounters) == 6112 * 4,
                  "counters v111 layout after v110 (docs/registry.md)");
    Ts3nCounters& k = ts3native_counters;
    assert(k.version == 912);
    const DWORD wt0 = g_workerTid, ct0 = g_callerTid;
    WpInstall();
    DWORD child[16] = {kWpChildVt}, child2[16] = {kWpChildVt}, parent[16] = {0xFEBA98}, other[16] = {kWpChildVt};
    const DWORD q = 0x1234;
    const LONG a0 = k.wpArms, h0 = k.wpHits;
    g_wpLot = 0x08F3005353628460ull, g_wpArmMax = 1, g_workerTid = 0;
    WpLotNoteQ(q), WpLotNoteQ(q);
    assert(g_wpLotQ[0] == q && !g_wpLotQ[1]);
    WpLotInsert(q, (u8*)child);  // no route worker yet: noted, not armed
    assert(k.wpArms == a0 && g_wpNLotFp == 1);
    g_workerTid = 1;
    WpLotInsert(0x9999, (u8*)other);  // another lot's Q: ignored
    WpLotInsert(q, (u8*)parent);      // the lot, not a child: noted only
    assert(k.wpArms == a0 && g_wpNLotFp == 2);
    WpLotInsert(q, (u8*)child);  // armed now, noted once
    assert(k.wpArms == a0 + 1 && g_wpNLotFp == 2 && g_wpArmed == 1);
    WpLotInsert(q, (u8*)child2);  // Arms=1 reached: noted, not armed
    assert(k.wpArms == a0 + 1 && g_wpNLotFp == 3);
    int slot = -1;
    for (int i = 0; i < 4; i++) if (g_wp[i].addr == (DWORD)(uintptr_t)&child[4]) slot = i;
    assert(slot >= 0 && k.wpHits == h0);
    // Writers: count all / during a job / on a lot footprint; the setter still writes; the watched write is caught
    const LONG z0[3] = {k.wrZ[0], k.wrZ[1], k.wrZ[2]}, p0[3] = {k.wrP30[0], k.wrP30[1], k.wrP30[2]};
    g_wrZTramp = (void*)FakeSetZ, g_wrPTramp = (void*)FakeSetP30;
    ((WrSetFn)(void*)WrZStub)(child, NULL, 2.0f);
    assert(*(float*)&child[4] == 2.0f && k.wrZ[0] == z0[0] + 1 && k.wrZ[1] == z0[1] && k.wrZ[2] == z0[2] + 1 && k.wpHits == h0 + 1);
    g_callerTid = 1;  // a route job runs (RgJobLive)
    ((WrSetFn)(void*)WrZStub)(other, NULL, 3.0f);
    assert(*(float*)&other[4] == 3.0f && k.wrZ[0] == z0[0] + 2 && k.wrZ[1] == z0[1] + 1 && k.wrZ[2] == z0[2] + 1 && k.wpHits == h0 + 1);
    ((WrSetFn)(void*)WrPStub)(parent, NULL, 4.0f);
    assert(*(float*)&parent[12] == 4.0f && k.wrP30[0] == p0[0] + 1 && k.wrP30[1] == p0[1] + 1 && k.wrP30[2] == p0[2] + 1);
    child[4] = 5;  // a plain write is caught too
    assert(k.wpHits == h0 + 2);
    // docs/157 §10: the child is removed (W2 of its parent's id) with Follow off: still armed (0.36.39 behaviour)
    DWORD par[16] = {0xFEBA98, 0, 0, 0, 0, 0x1605, 0x08F30000};  // parent id at +0x14 / +0x18
    child[3] = (DWORD)(uintptr_t)par, child[5] = 0, child[6] = 0x780;
    WpLotRemove(0x1605, 0x08F30000);
    assert(g_wp[slot].addr && k.wpFollow[0] == 0);
    // Follow=1: removed with its parent: disarmed, the slot kept; a different child does not take it; the same parent's
    // child (a new block: the rebuilt footprint) is re-armed, the old block is not watched any more
    g_wpFollow = 1;
    const LONG f0[2] = {k.wpFollow[0], k.wpFollow[1]}, a1 = k.wpArms;
    WpLotRemove(0x1605, 0x08F30000);
    assert(!g_wp[slot].addr && k.wpFollow[0] == f0[0] + 1 && g_wpArmed == 1);
    child[4] = 7;
    assert(k.wpHits == h0 + 2);
    DWORD child3[16] = {kWpChildVt, 0, 0, (DWORD)(uintptr_t)child2, 0, 0, 0x780}, child4[16] = {kWpChildVt, 0, 0, (DWORD)(uintptr_t)par, 0, 0, 0x780};
    WpLotInsert(q, (u8*)child3);  // another parent: Arms=1 is used up by the followed slot
    assert(k.wpArms == a1 && k.wpFollow[1] == f0[1]);
    WpLotInsert(q, (u8*)child4);
    int slot4 = -1;
    for (int i = 0; i < 4; i++) if (g_wp[i].addr == (DWORD)(uintptr_t)&child4[4]) slot4 = i;
    assert(slot4 >= 0 && g_wp[slot4].obj == (DWORD)(uintptr_t)child4 && k.wpFollow[1] == f0[1] + 1 && k.wpArms == a1);
    child4[4] = 8;  // the followed child's write is caught
    assert(k.wpHits == h0 + 3);
    WpLotRemove(0, 0x780);  // its own id removes it too
    assert(!g_wp[slot4].addr && k.wpFollow[0] == f0[0] + 2);
    memset(g_wpFol, 0, sizeof(g_wpFol)), g_wpFollow = 0;
    // constructor pairs (docs/157 §9.2): eip 0x80750D, or 0x62627B with [esp] = 0x807557; anything else is logged
    DWORD stk[2] = {0x807557, 0x83237B};
    assert(WpCtorHit(0x80750D, 0) && WpCtorHit(0x62627B, (DWORD)(uintptr_t)stk) && !WpCtorHit(0x62627B, (DWORD)(uintptr_t)&stk[1]) &&
           !WpCtorHit(0x62627B, 0) && !WpCtorHit(0x807508, (DWORD)(uintptr_t)stk));
    g_workerTid = wt0, g_callerTid = ct0, g_wpLot = 0, g_wpArmed = 0, g_wpNLotFp = 0, g_wrZTramp = g_wrPTramp = NULL;
    memset(g_wpLotQ, 0, sizeof(g_wpLotQ));
    WpUninstall();
    printf("watchpoint Lot=: child +0x10 armed at its lot insert (not before the route worker, not other lots / classes, Arms cap), "
           "writer probes count all / job / lot and still write; Follow disarms at the child's remove and re-arms the same parent's "
           "child; constructor pairs classified: ok\n");
}

static void TestWatchpoint() {
    assert(WpArm((const void*)&g_wpHits[0], 4, true) == -1);  // off: no handler, nothing armed
    WpInstall();
    DWORD d[2] = {};
    assert(WpArm((const u8*)d + 1, 4, true) == -1 && WpArm(d, 3, true) == -1);  // unaligned, bad length
    g_wpEvArmed = CreateEventW(NULL, FALSE, FALSE, NULL), g_wpEvMain = CreateEventW(NULL, FALSE, FALSE, NULL);
    HANDLE t = CreateThread(NULL, 0, WpTestProc, NULL, 0, NULL);
    assert(t && WaitForSingleObject(g_wpEvArmed, 10000) == WAIT_OBJECT_0);
    *g_wpTv = 2;  // another thread's write is caught too
    assert(g_wpHits[g_wpTSlot] >= 2);
    SetEvent(g_wpEvMain);
    assert(WaitForSingleObject(t, 10000) == WAIT_OBJECT_0);
    DWORD code = 1;
    GetExitCodeThread(t, &code);
    assert(code == 0 && !g_wp[g_wpTSlot].addr);
    CloseHandle(t), CloseHandle(g_wpEvArmed), CloseHandle(g_wpEvMain);
    WpUninstall();
    printf("watchpoint: %ld hits on the test local (write-only DR%d, 2 threads), 0 after disarm, %ld thread(s) not set\n",
           g_wpHits[g_wpTSlot], g_wpTSlot, g_wpSetFail);
    TestWatchpointLot();
}
