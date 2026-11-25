// TestConnRoomsRO ([ConnRoomsRO], connro.cpp, docs/670 §15): the gate ask and the call-site stub on fakes. The answer against
// the game's own IC and 0x63DCB0 is steerro-test section CN; here: the room index, the set's order, the read's doubts, every
// gate refusal, the parked answer's key, the vector the stub builds, shadow. Included by selftest.cpp.
namespace cnt {
static LONG origCalls, news;
static DWORD g_origVec[4];
static u8 c_world[0xA00], c_planner[0x100], c_act[0x60], c_node[0x200], c_rooms[4 * 0x44], c_pt[4][0x60];
static const u8* c_pv[4];
static DWORD c_bk[2];
static LONG c_blk[kCnCap];
static u8* __cdecl World() { return c_world; }
static const u8* __cdecl Active() { return c_act; }
static void __fastcall Find(const void*, void*, DWORD* out, const DWORD* key) {
    out[0] = key[0] == 0x10 && key[1] == 0 ? (DWORD)(uintptr_t)c_node : c_bk[1];
}
static DWORD __fastcall Orig(u8*, void*, DWORD, DWORD, DWORD, DWORD, DWORD* vec) {
    origCalls++;
    static LONG two[2] = {3, 9};
    vec[0] = (DWORD)(uintptr_t)two, vec[1] = vec[2] = (DWORD)(uintptr_t)(two + 2);
    memcpy(g_origVec, vec, sizeof(g_origVec));
    return 1;
}
static void* __cdecl New(DWORD size, DWORD tag, DWORD, DWORD, const char*, DWORD) {
    news++;
    assert(size <= sizeof(c_blk) && tag == 0x7A6);
    return c_blk;
}
static bool g_fix;  // the parked answer is re-keyed to the call below, as if CnGate had run on this "IC's" slot
static void __cdecl OnSlot(const DWORD* stubSlot) {
    if (g_fix && g_cnA.set) g_cnA.slot = stubSlot + kCnSlotGap;
}
// CnStub called with its return slot known before the call (the compiler may place each call at another depth)
static __declspec(naked) DWORD __cdecl CallStub(u8* /*p*/, DWORD /*lo*/, DWORD /*hi*/, DWORD /*room*/, DWORD /*level*/, DWORD* /*vec*/) {
    __asm {
        push dword ptr [esp + 0x18]
        push dword ptr [esp + 0x18]
        push dword ptr [esp + 0x18]
        push dword ptr [esp + 0x18]
        push dword ptr [esp + 0x18]
        lea eax, [esp - 4]
        push eax
        call OnSlot
        add esp, 4
        mov ecx, [esp + 0x18]
        call CnStub
        ret
    }
}
}  // namespace cnt

static void TestConnRoomsRO() {
    using namespace cnt;
    Ts3nCounters& kc = ts3native_counters;
    ConnRoomsRoCounters& k = ts3native_connroomsro;
    static_assert(offsetof(ConnRoomsRoCounters, cnMode) == 0 && offsetof(ConnRoomsRoCounters, cnShadow) == 4 &&
                      offsetof(ConnRoomsRoCounters, cnFb) == 0x24 && offsetof(ConnRoomsRoCounters, cnShadowMismatch) == 0x50 &&
                      sizeof(ConnRoomsRoCounters) == 0x54,
                  "ts3native_connroomsro layout (docs/registry.md)");
    assert(kCnSite.site == 0x00792A55 && kCnSite.target == 0x0063DCB0 && kCnSlotGap == 13);
    memset(&k, 0, sizeof(k));
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
    // the room index: a positive room, room 0 by level, a negative room; only the low word of the room counts
    assert(CnRoomIndex(1, 0) == 0x11 && CnRoomIndex(0x7FFF, 0) == 0x800F && CnRoomIndex(0, 0) == 4 && CnRoomIndex(0, (DWORD)-4) == 0 &&
           CnRoomIndex(0, (DWORD)-5) == 0xFFFF && CnRoomIndex(0xFFFF, 3) == 0xFFFF && CnRoomIndex(0x8000, 3) == 0xFFFF &&
           CnRoomIndex(0xABCD0002, 9) == 0x12 && CnRoomIndex(0x12340000, 1) == 5);
    // the set: unique, signed order, a full list refuses
    {
        static CnAnswer a;
        a.n = 0;
        for (short v : {(short)5, (short)-3, (short)5, (short)0, (short)0x7FFF, (short)-0x8000, (short)-3}) assert(CnInsert(&a, v));
        assert(a.n == 5 && a.v[0] == -0x8000 && a.v[1] == -3 && a.v[2] == 0 && a.v[3] == 5 && a.v[4] == 0x7FFF);
        a.n = 0;
        for (int i = 0; i < kCnCap; i++) assert(CnInsert(&a, (short)(i * 2)));
        assert(!CnInsert(&a, 1) && CnInsert(&a, 4) && a.n == kCnCap);
    }

    const SrWorldFn savedWorld = g_srWorldGet;
    const SrActiveLotFn savedActive = g_srActiveLot;
    const SrLotFindFn savedFind = g_srLotFind;
    const CnFn savedOrig = g_cnOrig;
    const CnNewFn savedNew = g_cnNew;
    const AsyncSlot savedAsync = g_async;
    const LONG savedArmed = g_twArmed, savedOwner = kc.rgOwnerTid, savedTw = g_twActive;
    g_srWorldGet = World, g_srActiveLot = Active, g_srLotFind = Find, g_cnOrig = Orig, g_cnNew = New;
    const DWORD me = GetCurrentThreadId();
    g_async.owner = me, g_async.state = A_IDLE;
    kc.rgOwnerTid = 0;
    memset(c_world, 0, sizeof(c_world));
    memset(c_planner, 0, sizeof(c_planner));
    memset(c_act, 0, sizeof(c_act));
    memset(c_node, 0, sizeof(c_node));
    memset(c_rooms, 0, sizeof(c_rooms));
    memset(c_pt, 0, sizeof(c_pt));
    c_world[0x974] = 1;
    *(u8**)(c_world + 0x95C) = c_planner;
    *(DWORD*)(c_act + 0x48) = 0x99;
    c_bk[0] = (DWORD)(uintptr_t)c_node, c_bk[1] = 0xEEEE0000;  // one bucket and the end slot
    *(DWORD**)(c_planner + 0x4C) = c_bk;
    *(DWORD*)(c_planner + 0x50) = 1;
    u8* e = c_node + 0x10;
    *(DWORD*)(c_node + 0x10) = 0x10;
    e[0x8C] = 1;
    *(u8**)(e + 0x90) = c_rooms;
    *(u8**)(e + 0x94) = c_rooms + sizeof(c_rooms);
    // room index 1 (level -3): portals to rooms 0x30 / 0x12, 0xFFFF / 1 (itself), a skipped one (bit 1), 5 / 0x8011
    u8* rec = c_rooms + 0x44;
    rec[0] = 1;
    const WORD words[4][2] = {{0x30, 0x12}, {0xFFFF, 1}, {0x40, 0x41}, {5, 0x8011}};
    for (int i = 0; i < 4; i++) {
        *(WORD*)(c_pt[i] + 0x48) = words[i][0], *(WORD*)(c_pt[i] + 0x4A) = words[i][1];
        c_pv[i] = c_pt[i];
    }
    c_pt[2][0x58] = 2;
    *(const u8***)(rec + 4) = c_pv;
    *(const u8***)(rec + 8) = c_pv + 4;
    static CnAnswer a;
    const DWORD lvl = (DWORD)-3;
    // the read: 0x30 - 0x10, 0x12 - 0x10, 5 -> 0, 0x8011 - 0x10 = 0x8001 (negative as a signed word, so first)
    assert(CnRead(c_planner, 0x10, 0, 0, lvl, &a) == SR_OK && a.n == 4 && a.v[0] == -0x7FFF && a.v[1] == 0 && a.v[2] == 2 && a.v[3] == 0x20);
    assert(CnRead(c_planner, 0x11, 0, 0, lvl, &a) == SR_OK && a.n == 0);  // not in the table: Contains false
    assert(CnRead(c_planner, 0, 0, 0, lvl, &a) == SR_OK && a.n == 0 && CnRead(c_planner, 0x99, 0, 0, lvl, &a) == SR_OK && a.n == 0);
    assert(CnRead(c_planner, 0x10, 0, 0, 0, &a) == SR_OK && a.n == 0);       // index 4 of 4 rooms: past the vector, before any doubt
    assert(CnRead(c_planner, 0x10, 0, 0xFFFF, lvl, &a) == SR_OK && a.n == 0);
    assert(CnRead(c_planner, 0x10, 0, 0, (DWORD)-4, &a) == CN_WHY_UNFILLED);  // room index 0: its record is not filled
    e[0x8C] = 0;
    assert(CnRead(c_planner, 0x10, 0, 0, lvl, &a) == CN_WHY_UNBUILT && CnRead(c_planner, 0x10, 0, 0, 0, &a) == SR_OK);
    e[0x8C] = 1;
    c_pv[3] = NULL;
    assert(CnRead(c_planner, 0x10, 0, 0, lvl, &a) == CN_WHY_SHAPE);
    c_pv[3] = c_pt[3];

    // the gate ask: refusals leave nothing parked
    DWORD slot[5] = {0, 0x10, 0, 0, lvl};
    assert(!CnGate(slot, me) && k.cnJob == 0);  // switch off
    k.cnMode = 1;
    assert(!CnGate(slot, me) && k.cnJob == 0);  // no job: the gate's own path
    g_async.state = A_RUNNING, g_twArmed = 1;
    assert(!CnGate(slot, me + 1) && k.cnJob == 0);  // not the owner thread
    kc.rgOwnerTid = (LONG)me;
    assert(!CnGate(slot, me) && k.cnJob == 1 && k.cnFb[CN_FB_MAYNOT] == 1);
    kc.rgOwnerTid = 0;
    g_twArmed = 0;
    assert(!CnGate(slot, me) && k.cnFb[CN_FB_JOBEND] == 1);
    g_twArmed = 1;
    AcquireSRWLockExclusive(&g_ql);
    assert(!CnGate(slot, me) && k.cnFb[CN_FB_TRY] == 1);
    ReleaseSRWLockExclusive(&g_ql);
    InterlockedExchange(&g_twActive, 1);
    assert(!CnGate(slot, me) && k.cnFb[CN_FB_TRIP] == 1);
    InterlockedExchange(&g_twActive, savedTw);
    e[0x8C] = 0;
    assert(!CnGate(slot, me) && k.cnFb[CN_FB_UNBUILT] == 1);
    e[0x8C] = 1;
    slot[4] = (DWORD)-4;
    assert(!CnGate(slot, me) && k.cnFb[CN_FB_UNFILLED] == 1);
    slot[4] = lvl;
    *(u8**)(c_world + 0x95C) = NULL;
    assert(!CnGate(slot, me) && k.cnFb[CN_FB_SHAPE] == 1);
    *(u8**)(c_world + 0x95C) = c_planner;
    assert(!g_cnA.set && k.cnTaken == 0 && t_qlShared == 0 && !t_inRo && g_srInClone == 0);

    // an answer parked for the stub: used by the call with the same slot, planner and arguments into an empty vector
    DWORD vec[4] = {0, 0, 0, 0x7A6};
    origCalls = news = 0;
    {
        g_fix = false;
        assert(CallStub(c_planner, 0x10, 0, 0, lvl, vec) == 1 && origCalls == 1);  // nothing parked: the game's call
        memset(vec, 0, 12);
        // CnGate reads slot[1..4] of an array here; OnSlot moves the key to the stub call's own frame
        assert(CnGate(slot, me) && g_cnA.set && k.cnTaken == 1 && k.cnRooms == 4 && g_cnA.slot == slot);
        g_fix = true;
        assert((u8)CallStub(c_planner, 0x10, 0, 0, lvl, vec) == 1 && origCalls == 1 && news == 1 && k.cnUsed == 1 && !g_cnA.set);
        assert(vec[0] == (DWORD)(uintptr_t)c_blk && vec[1] == vec[0] + 16 && vec[2] == vec[1] && vec[3] == 0x7A6 && c_blk[0] == -0x7FFF &&
               c_blk[1] == 0 && c_blk[2] == 2 && c_blk[3] == 0x20);
        // an empty answer: al 0, no block, the vector stays empty
        memset(vec, 0, 12);
        slot[3] = 0xFFFF;
        assert(CnGate(slot, me) && k.cnEmpty == 1);
        assert((u8)CallStub(c_planner, 0x10, 0, 0xFFFF, lvl, vec) == 0 && news == 1 && !vec[0] && !vec[1] && !vec[2] && k.cnUsed == 2);
        slot[3] = 0;
        // not the call it was read for: another slot, another argument, a vector that is not empty -> the game's call, once each
        const LONG o0 = origCalls;
        assert(CnGate(slot, me));
        g_fix = false;
        assert(CallStub(c_planner, 0x10, 0, 0, lvl, vec) == 1 && origCalls == o0 + 1 && k.cnStale == 1 && !g_cnA.set);  // slot
        g_fix = true;
        memset(vec, 0, 12);
        assert(CnGate(slot, me));
        assert(CallStub(c_planner, 0x10, 0, 1, lvl, vec) == 1 && origCalls == o0 + 2 && k.cnStale == 2);  // room
        assert(CnGate(slot, me));
        assert(CallStub(c_planner, 0x10, 0, 0, lvl, vec) == 1 && origCalls == o0 + 3 && k.cnStale == 3 && news == 1);  // vector not empty
        // nothing parked, shadow off: the game's call
        memset(vec, 0, 12);
        assert(CallStub(c_planner, 0x10, 0, 0, lvl, vec) == 1 && origCalls == o0 + 4 && k.cnStale == 3 && k.cnShadowRuns == 0);
    }
    // shadow, no job: the game's vector (3, 9) against the clone's four rooms = one mismatch; then equal
    {
        g_async.state = A_IDLE, g_twArmed = 0;
        k.cnShadow = 1;
        memset(vec, 0, 12);
        const LONG o0 = origCalls;
        assert(CallStub(c_planner, 0x10, 0, 0, lvl, vec) == 1 && origCalls == o0 + 1 && k.cnShadowRuns == 1 && k.cnShadowMismatch == 1 &&
               g_rgOffSimOpen == 0 && memcmp(vec, g_origVec, sizeof(vec)) == 0);
        const WORD w2[4][2] = {{0x13, 0x19}, {0xFFFF, 0xFFFF}, {0xFFFF, 0xFFFF}, {0xFFFF, 0xFFFF}};  // rooms 3 and 9
        for (int i = 0; i < 4; i++) *(WORD*)(c_pt[i] + 0x48) = w2[i][0], *(WORD*)(c_pt[i] + 0x4A) = w2[i][1];
        memset(vec, 0, 12);
        assert(CallStub(c_planner, 0x10, 0, 0, lvl, vec) == 1 && k.cnShadowRuns == 2 && k.cnShadowMismatch == 1);
        g_async.state = A_RUNNING;  // a job runs: no compare
        memset(vec, 0, 12);
        assert(CallStub(c_planner, 0x10, 0, 0, lvl, vec) == 1 && k.cnShadowRuns == 2);
    }
    printf("ConnRoomsRO ([ConnRoomsRO] Enabled=1): export layout; room index; signed set order and cap; the read (rooms, not in the "
           "table, past the vector, not built, not filled, null portal); gate refusals (off, no job, other thread, +0x918 owner, job end, QL "
           "busy, tripwire, no planner); parked answer handed out through the allocator, empty answer, three stale keys; shadow compare\n");

    memset(&k, 0, sizeof(k));
    g_cnA.set = false;
    g_srWorldGet = savedWorld, g_srActiveLot = savedActive, g_srLotFind = savedFind, g_cnOrig = savedOrig, g_cnNew = savedNew;
    g_async = savedAsync, g_twArmed = savedArmed, kc.rgOwnerTid = savedOwner;
}
