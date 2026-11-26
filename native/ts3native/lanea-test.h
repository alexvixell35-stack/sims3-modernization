// docs/321 §10 [LaneA] (lanea.cpp): CP-3/CP-4 native consumers. Included by selftest.cpp after TestLotHub.
// Checks: off = no subscriber; drop keeps every other row findable (same probe as the slot finders); world reset leaves 0 rows;
// 50 load / quit cycles with lot add / remove churn keep every table flat (no leak, door slots reused with their buffers).
static void TestLaneA() {
    Ts3nCounters& k = ts3native_counters;
    LaRow* r = ts3native_lanea.r;
    // saved: other tests own these tables
    static AsTierEnt sTier[1024];
    static AsLotTier sLotTier[512];
    static AsLite sLite[1024];
    static SpDoorLot sDoor[kSpDoorLots];
    memcpy(sDoor, g_spDoorLots, sizeof(sDoor));
    memcpy(sTier, g_asTierTab, sizeof(sTier)), memcpy(sLotTier, g_asLotTier, sizeof(sLotTier)), memcpy(sLite, g_asLite, sizeof(sLite));
    const LONG tierN0 = g_asTierN, lotTierN0 = g_asLotTierN, spN0 = g_spDoorN, lhReady0 = k.lhReady, subN0 = g_lhSubN;
    const bool hadAs = g_as != NULL;
    assert(AsAlloc());
    static AsSet sSim;
    memcpy(&sSim, &g_as->simIds, sizeof(sSim));
    memset(g_asTierTab, 0, sizeof(g_asTierTab)), memset(g_asLotTier, 0, sizeof(g_asLotTier)), memset(g_asLite, 0, sizeof(g_asLite));
    g_asTierN = g_asLotTierN = 0;
    AsSetClear(g_as->simIds);
    memset(r, 0, sizeof(ts3native_lanea.r));

    // install: all keys 0 = no subscriber; a key on without the hub = SKIP; with the hub = one row, again = still one
    wchar_t tmp[MAX_PATH], ini[MAX_PATH];
    GetTempPathW(MAX_PATH, tmp);
    swprintf_s(ini, L"%sts3n-selftest-lanea-%lu.ini", tmp, GetCurrentProcessId());
    DeleteFileW(ini);
    ApplyLaneA(ini);
    assert(g_lhSubN == subN0 && !r[LA_SPDOOR].on);
    WritePrivateProfileStringW(L"LaneA", L"SpDoor", L"1", ini);
    WritePrivateProfileStringW(L"LaneA", L"EarlyOut", L"1", ini);
    k.lhReady = 0;
    ApplyLaneA(ini);
    assert(g_lhSubN == subN0 && r[LA_SPDOOR].on && !r[LA_EO].on && !g_laEo);  // EarlyOut needs [RouteEarlyOut]: refused
    k.lhReady = 3;
    ApplyLaneA(ini), ApplyLaneA(ini);
    assert(g_lhSubN == subN0 + 1 && g_lhSubs[subN0].fn == LaHub);
    g_lhSubN = subN0;
    DeleteFileW(ini);
    for (int t = 0; t < LA_N; t++) r[t].on = t != LA_EO;  // EarlyOut: g_eoCs only with [RouteEarlyOut]

    // lot rows: 300 lot tiers and lites, drop lot 7: every other row is still found by the slot finders, lot 7 is gone
    for (u64 l = 1; l <= 300; l++) AsLotTierSlot(l * 0x10001ull)->refCut = (LONG)l, AsLiteSlot(l * 0x10001ull, true)->cut[0] = (LONG)l;
    for (int i = 0; i < 20; i++) AsTierSlot(0x5000ull + i), AsSetAdd(g_as->simIds, 0x5000ull + i);
    assert(g_asLotTierN == 300 && LaLiteCount() == 300 && g_asTierN == 20 && g_as->simIds.n == 20);
    static u8 lot[0x60];
    *(u64*)(lot + 0x48) = 7 * 0x10001ull;
    LaHub(LH_REMOVE, lot, 1, NULL);
    assert(g_laAsGoneN == 1 && g_asLotTierN == 300);  // applied at the cut, not in the callback
    LaAsApply();
    assert(g_asLotTierN == 299 && LaLiteCount() == 299 && r[LA_ASLOTTIER].drops == 1 && r[LA_ASLITE].drops == 1 && g_laAsGoneN == 0);
    for (u64 l = 1; l <= 300; l++) {
        if (l == 7) continue;
        assert(AsLotTierSlot(l * 0x10001ull)->refCut == (LONG)l && AsLiteSlot(l * 0x10001ull, false)->cut[0] == (LONG)l);
    }
    assert(g_asLotTierN == 299 && !AsLiteSlot(7 * 0x10001ull, false) && g_asTierN == 20);  // Sim tiers stay on a lot remove
    // 65 removes before a cut: overflow clears the lot-keyed tables instead
    for (int i = 0; i < 65; i++) *(u64*)(lot + 0x48) = 0x900000ull + i, LaHub(LH_REMOVE, lot, 1, NULL);
    assert(g_laAsGoneOver);
    LaAsApply();
    assert(g_asLotTierN == 0 && LaLiteCount() == 0 && g_asTierN == 20 && g_as->simIds.n == 20);

    // door lots: a remove empties that slot, a world event empties all; slots and buffers are reused (g_spDoorN flat)
    g_spDoorN = 0;
    for (u64 l = 1; l <= 10; l++) {
        SpDoorLot* d = SpDoorLotFor(l, true);
        assert(d && SpGrow((void**)&d->d, &d->cap, 4, sizeof(SpDoor)));
        d->n = 2, d->valid = 1, d->epoch = g_spEpoch;
    }
    *(u64*)(lot + 0x48) = 4;
    LaHub(LH_REMOVE, lot, 1, NULL);
    assert(!SpDoorLotFor(4, false) && r[LA_SPDOOR].drops == 1 && LaSpDoorLive() == 9);
    SpDoorLot* reuse = SpDoorLotFor(11, true);
    assert(reuse == &g_spDoorLots[3] && reuse->d && reuse->cap >= 4 && !reuse->valid && !reuse->n && g_spDoorN == 10);
    void* buf0 = g_spDoorLots[0].d;

    // 50 world load / quit cycles with add / remove churn: every reset table reads 0 after the cut, door slots stay at 10
    LONG lotTierHw = 0;
    for (int c = 0; c < 50; c++) {
        LaHub(c & 1 ? LH_WORLD_LOAD : LH_WORLD_QUIT, NULL, 1, NULL);
        assert(LaSpDoorLive() == 0 && g_spDoorN == 10);
        LaAsApply();
        assert(g_as->simIds.n == 0 && g_asTierN == 0 && g_asLotTierN == 0 && LaLiteCount() == 0 && g_laAsWorld == 0);
        for (u64 l = 1; l <= 10; l++) {
            SpDoorLot* d = SpDoorLotFor(1000 * (c + 1) + l, true);
            assert(d && d->key == 1000 * (c + 1) + l);
            AsLotTierSlot(1000 * (c + 1) + l), AsTierSlot(0x7000ull + l), AsSetAdd(g_as->simIds, 0x7000ull + l);
        }
        *(u64*)(lot + 0x48) = 1000 * (c + 1) + 5;
        LaHub(LH_REMOVE, lot, 1, NULL);
        LaAsApply();
        assert(g_asLotTierN == 9 && LaSpDoorLive() == 9 && g_spDoorN == 10);
        if (g_asLotTierN > lotTierHw) lotTierHw = g_asLotTierN;
    }
    assert(g_spDoorN == 10 && g_spDoorLots[0].d == buf0 && lotTierHw == 9);  // flat: no slot, buffer or row grew with the cycles
    assert(r[LA_SPDOOR].hw == 9 && r[LA_ASLOTTIER].hw <= 300 && r[LA_ASTIER].clears == 50 && r[LA_ASSIMIDS].clears == 50);

    // DoorGen holds: cleared on world quit only (a load waits for DoorGen.cs's arm), with a generation bump
    g_dmxHold[0] = 0x11, g_dmxHold[5] = 0x22;
    LaHub(LH_WORLD_LOAD, NULL, 1, NULL);
    assert(LaDoorHeld() == 2);
    const LONG dgGen = ts3native_doorgen.gen;
    LaHub(LH_WORLD_QUIT, NULL, 1, NULL);
    assert(LaDoorHeld() == 0 && !g_dmxHoldAll && ts3native_doorgen.gen == dgGen + 1);
    *(u64*)(lot + 0x48) = 0x11;
    g_dmxHold[1] = 0x11;
    LaHub(LH_REMOVE, lot, 1, NULL);
    assert(g_dmxHold[1] == 0x11);  // a hold outlives streaming (the script lot keeps its handler)
    g_dmxHold[1] = 0;

    // ReplanDedupe rows: emptied on a world event
    g_rdTab[3].ms = 5, g_rdTab[9].ms = 6;
    assert(LaRdLive() == 2);
    LaHub(LH_WORLD_LOAD, NULL, 1, NULL);
    assert(LaRdLive() == 0 && r[LA_RD].hw >= 0);

    // switch off: nothing changes
    LaAsApply();  // the world events above, still on
    assert(g_asLotTierN == 0 && g_asTierN == 0 && LaSpDoorLive() == 0);
    for (int t = 0; t < LA_N; t++) r[t].on = 0;
    AsLotTierSlot(0x4242), AsTierSlot(0x4243);
    g_dmxHold[2] = 0x33;
    LaHub(LH_WORLD_QUIT, NULL, 1, NULL);
    LaAsApply();
    assert(g_asLotTierN == 1 && g_asTierN == 1 && LaDoorHeld() == 1 && LaSpDoorLive() == 0);
    g_dmxHold[2] = 0;

    // restore
    for (int i = 0; i < g_spDoorN; i++) HeapFree(g_spHeap, 0, g_spDoorLots[i].d), HeapFree(g_spHeap, 0, g_spDoorLots[i].ids);
    memcpy(g_spDoorLots, sDoor, sizeof(sDoor));
    g_spDoorN = spN0;
    memcpy(g_asTierTab, sTier, sizeof(sTier)), memcpy(g_asLotTier, sLotTier, sizeof(sLotTier)), memcpy(g_asLite, sLite, sizeof(sLite));
    g_asTierN = tierN0, g_asLotTierN = lotTierN0, k.lhReady = lhReady0;
    memcpy(&g_as->simIds, &sSim, sizeof(sSim));
    if (!hadAs) VirtualFree(g_as->ol, 0, MEM_RELEASE), VirtualFree(g_as, 0, MEM_RELEASE), g_as = NULL;  // later tests read "autshadow off" from it
    memset(r, 0, sizeof(ts3native_lanea.r));
    g_laAsWorld = 0, g_laAsGoneN = 0, g_laAsGoneOver = false;
    memset(g_rdTab, 0, sizeof(g_rdTab));
    printf("lane a consumers: ok\n");
}

// ---- docs/321 §11.1 GhostSweepLot on a fake routing world: lot A's cells of each ghost go, nothing else; the game-state gate;
// busy ticks skipped; a rehash mid-job; queue overflow = the whole-map pass; 50 world cycles keep the queue flat ----
struct GsFNode { DWORD lo, hi; u8* fme; DWORD pad; GsFNode* next; };  // key +0, FME +8, next +0x10 (0x635700's map layout)
static GsFNode* g_gsfBk[64];
static GsFNode g_gsfN[6000];
static u8 g_gsfFme[6000][0x14], g_gsfFp[6000][0x40];
static int g_gsfNN;
static DWORD g_gsfCount = 61;
static u8 g_gsfQ[6][4];                             // quadtrees: lot A = 0..2, lot B = 3..5
static const u8* g_gsfRecA[0x80 / 4], *g_gsfRecB[0x80 / 4];
static DWORD g_gsfLive[2], g_gsf3C[2], g_gsfRemoves, g_gsfAcqFail;
static struct { DWORD pad; const u8* const* bk; DWORD count; } g_gsfM, g_gsfM3;  // buckets +4, count +8
static GsFNode g_gsf3CNode;
static GsFNode* g_gsf3CBk[1];
static const DWORD kGsA = 0xA001, kGsB = 0xB002;

static void GsfAdd(DWORD lo, DWORD hi, DWORD cls, int q) {
    const int i = g_gsfNN++;
    GsFNode& n = g_gsfN[i];
    n.lo = lo, n.hi = hi, n.fme = g_gsfFme[i];
    *(u8**)(g_gsfFme[i] + 8) = g_gsfFp[i];
    *(u8**)(g_gsfFme[i] + 0xC) = q >= 0 ? g_gsfQ[q] : NULL;
    *(DWORD*)g_gsfFp[i] = cls, *(DWORD*)(g_gsfFp[i] + 0x14) = lo, *(DWORD*)(g_gsfFp[i] + 0x18) = hi, g_gsfFp[i][0x35] = 1;
    n.next = g_gsfBk[lo % g_gsfCount], g_gsfBk[lo % g_gsfCount] = &n;
}
static void GsfRehash(DWORD count) {  // same nodes, another bucket count (the map grew)
    memset(g_gsfBk, 0, sizeof(g_gsfBk));
    g_gsfCount = count;
    for (int i = 0; i < g_gsfNN; i++) {
        GsFNode& n = g_gsfN[i];
        if (n.pad == 0xDEAD) continue;  // unlinked
        n.next = g_gsfBk[n.lo % count], g_gsfBk[n.lo % count] = &n;
    }
    g_gsfM.count = count;
}
static int GsfNodes(DWORD lo, DWORD hi, int qFrom, int qTo) {  // linked nodes of the id on quadtrees qFrom..qTo
    int n = 0;
    for (GsFNode* nd = g_gsfBk[lo % g_gsfCount]; nd; nd = nd->next) {
        if (nd->lo != lo || nd->hi != hi) continue;
        const u8* q = *(const u8* const*)(nd->fme + 0xC);
        for (int i = qFrom; i <= qTo; i++) n += q == g_gsfQ[i];
    }
    return n;
}
static void* __cdecl GsfSvc() { return (void*)1; }
static u8 __fastcall GsfFind(void*, void*, DWORD lo, DWORD hi, void** out) {  // 1 + object = live; 0 = no object
    *out = NULL;
    if (lo != g_gsfLive[0] || hi != g_gsfLive[1]) return 0;
    *out = (void*)g_gsfLive;
    return 1;
}
static void __fastcall GsfRelease(void**, void*) {}  // as LrFind's test: the pointer stays (FpgNoObject reads it after)
static u8 __fastcall GsfLotHas(void*, void*, DWORD lo, DWORD hi) { return !hi && (lo == kGsA || lo == kGsB); }
static u8* __fastcall GsfLotRec(void*, void*, const DWORD* id) { return (u8*)(id[0] == kGsA ? g_gsfRecA : g_gsfRecB); }
// 0x635700 as disassembled: lot 0 = each node with a Q not listed in [P+0x3C] for the id; a lot = each of its Qs
static u8 __fastcall GsfLotRemove(void*, void*, DWORD lotLo, DWORD, DWORD lo, DWORD hi) {
    const int from = lotLo == kGsA ? 0 : lotLo == kGsB ? 3 : 0, to = lotLo == kGsA ? 2 : 5;
    u8 any = 0;
    for (GsFNode** pp = &g_gsfBk[lo % g_gsfCount]; *pp;) {
        GsFNode* nd = *pp;
        const u8* q = *(const u8* const*)(nd->fme + 0xC);
        bool hit = nd->lo == lo && nd->hi == hi && q;
        if (hit && lotLo) { hit = false; for (int i = from; i <= to; i++) hit |= q == g_gsfQ[i]; }
        if (hit && !lotLo && lo == g_gsf3C[0] && hi == g_gsf3C[1] && q == g_gsf3CNode.fme) hit = false;  // its Q listed in [P+0x3C]
        if (hit) { *pp = nd->next, nd->pad = 0xDEAD, any = 1, g_gsfRemoves++; continue; }
        pp = &nd->next;
    }
    return any;
}
static unsigned __int64 __cdecl GsfPfKey(int, int, DWORD lo, DWORD hi) { return ((unsigned __int64)hi << 32 | lo) ^ 0x9; }  // never in the map
static LONG __fastcall GsfAcq(void*, void*, const DWORD* d) { assert(d && *d == 0); return g_gsfAcqFail ? 0 : 1; }
static LONG __fastcall GsfRel(void*, void*) { return 1; }
static void GsfRun(int maxTicks) {  // ticks until idle
    for (int i = 0; i < maxTicks && (g_gsJob.active || g_gsQN || ts3native_lanea.gs.queueOver); i++) GsTick();
}

static void TestGsLot() {
    Ts3nCounters& k = ts3native_counters;
    Ts3nGsLot& g = ts3native_lanea.gs;
    LaRow* r = ts3native_lanea.r;
    // saved globals
    const SvcFn svc0 = g_objSvc;
    const FindRefFn find0 = g_findRef;
    const ReleaseFn release0 = g_objRelease;
    const LotRemoveFn rem0 = g_lotRemove;
    const PfKeyFn pf0 = g_pfKey;
    const LockAcquireFn acq0 = g_fbAcq;
    const FbRelFn rel0 = g_fbRel;
    u8* const* slot0 = g_fpgWorldSlot;
    void* const lock0 = g_rgLock;
    const LONG asState0 = g_async.state;
    const DWORD asOwner0 = g_async.owner;
    void* const asLock0 = g_async.lock;
    const LONG rgOwner0 = k.rgOwnerTid, closing0 = k.rtClosing;
    const int disc0 = g_discCount;

    static u8 world[0xA00], P[0x100];
    u8* worldPtr = world;
    *(u8**)(world + 0x95C) = P;
    world[0x974] = 1;
    *(void**)(P + 0x24) = &g_gsfM;  // map: +4 buckets, +8 count
    g_gsfM.bk = (const u8* const*)g_gsfBk, g_gsfM.count = g_gsfCount;
    *(void**)(P + 0x3C) = &g_gsfM3;
    g_gsf3CBk[0] = &g_gsf3CNode;
    g_gsfM3.bk = (const u8* const*)g_gsf3CBk, g_gsfM3.count = 1;
    g_gsQN = 0, g_gsJob = GsJob();
    g_gsfNN = 0, g_gsfCount = 61, g_gsfM.count = 61, g_gsfRemoves = 0, g_gsfAcqFail = 0;  // the selftest runs this twice (both editions)
    memset(g_gsfBk, 0, sizeof(g_gsfBk)), memset(g_gsfN, 0, sizeof(g_gsfN));
    static const u8* qa[3] = {g_gsfQ[0], g_gsfQ[1], g_gsfQ[2]}, *qb[3] = {g_gsfQ[3], g_gsfQ[4], g_gsfQ[5]};
    g_gsfRecA[0x74 / 4] = (const u8*)qa, g_gsfRecA[0x78 / 4] = (const u8*)(qa + 3);
    g_gsfRecB[0x74 / 4] = (const u8*)qb, g_gsfRecB[0x78 / 4] = (const u8*)(qb + 3);
    g_objRelease = GsfRelease;
    g_objSvc = GsfSvc, g_findRef = GsfFind, g_lotRemove = GsfLotRemove, g_pfKey = GsfPfKey, g_fbAcq = GsfAcq, g_fbRel = GsfRel;
    g_gsLotHas = GsfLotHas, g_gsLotRec = GsfLotRec;
    g_fpgWorldSlot = (u8* const*)&worldPtr;
    g_rgLock = world + 0x918;
    g_async.state = A_IDLE, g_async.owner = 0, g_async.lock = world + 0x918;
    k.rgOwnerTid = 0, k.rtClosing = 0, g_discCount = 0;

    const DWORD H = 0x38B400F5, G1 = 0x1D3272D0, G2 = 0x1D3272E0, G3 = 0x1D3272F0, L1 = 0x1D327300;
    GsfAdd(G1, H, kFpgFpClass, 0), GsfAdd(G1, H, kFpgFpClass, 4);  // ghost on lot A (level 0) and lot B (level 1)
    GsfAdd(G2, H, kFpgFpClass, 5);                                  // ghost on lot B only
    GsfAdd(G3, H, kFpgFpClass, 1);                                  // ghost on lot A with a [P+0x3C] row: left to the load pass
    GsfAdd(L1, H, kFpgFpClass, 2);                                  // a live object on lot A: never
    g_gsfLive[0] = L1, g_gsfLive[1] = H, g_gsf3C[0] = G3, g_gsf3C[1] = H;
    g_gsf3CNode.lo = G3, g_gsf3CNode.hi = H, g_gsf3CNode.fme = g_gsfQ[1], g_gsf3CNode.next = NULL;  // [P+0x3C] value = G3's Q
    for (int i = 0; i < 3000; i++) GsfAdd(0x2E31605 + 0x10 * i, 0, 0x00FEBA98, i % 6);  // non-object ids (clean Baseline shape)

    // install: GhostSweepLot=2 with the hub; SimLoopAdd needs the Simulate site, so the tick is driven by hand here
    memset(r, 0, sizeof(ts3native_lanea.r));
    memset(&g, 0, sizeof(g));
    g.mode = 2, g.verify = 1, g.nodesPerTick = 256;
    r[LA_GS].on = 1;
    static u8 lot[0x60];
    *(u64*)(lot + 0x48) = kGsA;
    // not live: the lot is queued (bookkeeping), nothing walks
    LhOp(5 | SS_PAUSED << 8);
    LaHub(LH_REMOVE, lot, 1, NULL);
    LaHub(LH_REMOVE, lot, 1, NULL);  // twice: one queue row
    assert(g_gsQN == 1 && r[LA_GS].cur == 1);
    assert(LaGateOp(0) == 1 && LaGateOp(3) == 0);  // the queue's window: mark, work waiting
    for (int i = 0; i < 20; i++) GsTick();
    assert(g.skipNotLive == 20 && g.slices == 0 && g.jobs == 0 && g_gsQN == 1);
    assert(LaGateOp(1) == 1 && LaGateOp(2) == 1 && LaGateOp(4) == 1);  // nothing moved, the gate turned work away, verify clean
    LhOp(5 | SS_CAS << 8);
    GsTick();
    LhOp(5 | SS_LIVE << 8);
    world[0x97B] = 1;  // Build/Buy byte of the routing world: not live even with a stale live push
    GsTick();
    assert(g.skipNotLive == 22 && g.slices == 0);
    world[0x97B] = 0;
    // busy: a job runs, the lock try fails, a discourage record is queued: skipped, never waited
    g_async.state = A_RUNNING;
    GsTick();
    g_async.state = A_IDLE;
    g_gsfAcqFail = 1;
    GsTick();
    g_gsfAcqFail = 0;
    g_discCount = 1;
    GsTick();
    g_discCount = 0;
    assert(g.skipBusy == 3 && g.slices == 0 && g_rgOffSimOpen == 0 && g_gsQN == 1);
    // live: sliced; lot A's cells of G1 go, G1 on lot B, G2, G3 (3C) and L1 stay; verify finds nothing else moved
    GsTick();
    assert(g.slices == 1 && g_gsJob.active && g_gsJob.pos > 0 && g_gsJob.pos < g_gsfCount);
    GsfRehash(53);  // the map rehashed between slices: the walk restarts
    GsfRun(1000);
    assert(g.restarts == 1 && !g.gaveUp && g.jobs == 1 && g.jobsDone == 1 && g.verifyJobs == 1);
    assert(LaGateOp(1) == 0 && LaGateOp(3) == 1 && LaGateOp(4) == 1);  // live: the queued work drained
    assert(GsfNodes(G1, H, 0, 2) == 0 && GsfNodes(G1, H, 3, 5) == 1 && GsfNodes(G2, H, 3, 5) == 1 && GsfNodes(G3, H, 0, 2) == 1 &&
           GsfNodes(L1, H, 0, 5) == 1);
    assert(g.removed == 1 && g.kept3C >= 1 && g.verifyLeft == 0 && g.verifyOther == 0 && g.workNotLive == 0 && g.keptRecheck == 0);
    assert(g_gsfRemoves == 1 && g_rgOffSimOpen == 0 && g.slices > 3 && r[LA_GS].drops == 1);  // kept3C: G3 (again if seen before the restart)
    {   // the verify counters catch a removal that is not "this lot only": lot 0 also takes G4's lot B node
        const DWORD G4 = 0x1D327310;
        GsfAdd(G4, H, kFpgFpClass, 0), GsfAdd(G4, H, kFpgFpClass, 3);
        const LONG vo = g.verifyOther;
        const DWORD rm = g_gsfRemoves;
        assert(GsRemoveLot(world, 0, G4, H, qa, 3) && g.verifyOther == vo + 1 && g_gsfRemoves == rm + 2);
        g.removed--;
        assert(LaGateOp(4) == 0);  // the queue's verify check sees it
        g.verifyOther = vo;
    }
    assert(LaGateOp(5) == 1 && g.queueOver && LaGateOp(3) == 0);  // requeue: one whole-map pass waiting
    g.queueOver = 0;
    // count only: lot B found, nothing removed
    g.mode = 1;
    *(u64*)(lot + 0x48) = kGsB;
    LaHub(LH_REMOVE, lot, 1, NULL);
    const LONG gh0 = g.ghosts;
    GsfRun(1000);
    assert(g.ghosts == gh0 + 2 && g_gsfRemoves == 3 && g.verifyJobs == 1);  // G1 and G2 on lot B; no verify pass in count mode
    // a lot the routing table does not know: counted, nothing walked
    g.mode = 2;
    *(u64*)(lot + 0x48) = 0xC003;
    LaHub(LH_REMOVE, lot, 1, NULL);
    GsfRun(10);
    assert(g.lotUnknown == 1 && g_gsfRemoves == 3);
    // overflow: 65 lots = one whole-map pass (the load-screen pass, sliced): G1 on B and G2 go, G3 (3C) and L1 stay
    g.verify = 0;
    for (DWORD i = 0; i < 65; i++) *(u64*)(lot + 0x48) = 0x5000 + i, LaHub(LH_REMOVE, lot, 1, NULL);
    assert(g.queueOver && g_gsQN == 64);
    GsfRun(1000);
    assert(!g_gsQN && !g.queueOver && GsfNodes(G1, H, 0, 5) == 0 && GsfNodes(G2, H, 0, 5) == 0 && GsfNodes(G3, H, 0, 5) == 1 &&
           GsfNodes(L1, H, 0, 5) == 1 && g_gsfRemoves == 5);
    // 50 world load / quit cycles with queue churn: the queue empties at each world event and never passes 64 (+1 job)
    for (int c = 0; c < 50; c++) {
        for (DWORD i = 0; i < 40; i++) *(u64*)(lot + 0x48) = 0x9000 + 100 * c + i, LaHub(LH_REMOVE, lot, 1, NULL);
        GsTick();  // one job starts (unknown lot: ends at once)
        LaHub(c & 1 ? LH_WORLD_LOAD : LH_WORLD_QUIT, NULL, 1, NULL);
        assert(!g_gsQN && !g_gsJob.active && !g.queueOver && r[LA_GS].cur == 0);
    }
    assert(r[LA_GS].hw <= 65 && r[LA_GS].clears == 50);
    // the state pushes (op 49 sub 5 / 6); LotHubFire resets the state to unknown at world events (code read: one line)
    g_simState = SS_UNKNOWN;
    assert(!SimLiveNow());
    assert(LhOp(5 | SS_LIVE << 8) == 1 && SimLiveNow() && LhOp(5 | 9 << 8) == 0 && g_simState == SS_LIVE);
    g_simStateAt = GetTickCount() - 3000;  // the managed task stopped (scripts paused in Build/Buy): a stale "live" is not live
    assert(!SimLiveNow());
    g_simStateWant = 0;
    assert(LhOp(6) == 0);
    g_simStateWant = 1;
    assert(LhOp(6) == 1);
    g_simStateWant = 0;

    {   // M8 design A vs B counters: m8LotDrops / m8RowsDropped at the lot event, m8WouldHit at the refused lookup
        const LONG ready0 = k.lhReady, gen0 = k.lhLotGen, would0 = g_laRdLotDrops, notLive0 = g_laRdLotNotLive;
        k.lhReady = 3;
        memset(g_rdTab, 0, sizeof(g_rdTab));
        g_laRdLot = 1;
        RdEntry prev = {};
        prev.lotGen = k.lhLotGen;
        assert(!RdLotStale(prev) && g_laRdLotDrops == would0);  // no lot event since the plan: served (design A keeps it)
        const ULONGLONG now = GetTickCount64();
        g_rdTab[1].ms = now, g_rdTab[1].fail = true, g_rdTab[1].lotGen = k.lhLotGen;   // live failed rows of this generation
        g_rdTab[2].ms = now, g_rdTab[2].fail = true, g_rdTab[2].lotGen = k.lhLotGen;
        g_rdTab[3].ms = now, g_rdTab[3].fail = false, g_rdTab[3].lotGen = k.lhLotGen;  // a success row is never served
        g_rdTab[4].ms = now - 2000, g_rdTab[4].fail = true, g_rdTab[4].lotGen = k.lhLotGen;  // past its 500 ms window
        LaHub(LH_ADD, lot, ++k.lhLotGen, NULL);
        assert(g.m8LotDrops == 1 && g.m8RowsDropped == 2);
        *(u64*)(lot + 0x48) = 0x7777;
        LaHub(LH_REMOVE, lot, ++k.lhLotGen, NULL);  // the same rows again: already refused by the first event, not counted twice
        assert(g.m8LotDrops == 1 && g.m8RowsDropped == 2);
        assert(RdLotStale(prev) && g_laRdLotDrops == would0 + 1);  // the lookup that B might have served
        LaGsSync();
        assert(g.m8WouldHit == would0 + 1);
        assert(LaGateOp(6) == 1);  // the "m8:" / "m8 B-gain:" lines
        g_laRdLot = 0;
        assert(!RdLotStale(prev) && g_laRdLotDrops == would0 + 1);  // switch off: today's rule, nothing counted
        memset(g_rdTab, 0, sizeof(g_rdTab));
        k.lhReady = ready0, k.lhLotGen = gen0, g_gsQN = 0, g_laRdLotNotLive = notLive0;  // the refusal above ran outside live mode
    }

    // restore
    g_objSvc = svc0, g_findRef = find0, g_objRelease = release0, g_lotRemove = rem0, g_pfKey = pf0, g_fbAcq = acq0, g_fbRel = rel0;
    g_gsLotHas = (GsLotHasFn)(uintptr_t)TS3A(0x00628A90), g_gsLotRec = (GsLotRecFn)(uintptr_t)TS3A(0x0062EAB0);
    g_async.state = asState0, g_async.owner = asOwner0, g_async.lock = asLock0, g_laAsWorld = 0;
    g_fpgWorldSlot = slot0, g_rgLock = lock0, k.rgOwnerTid = rgOwner0, k.rtClosing = closing0, g_discCount = disc0;
    g_simState = SS_UNKNOWN, g_gsQN = 0, g_gsJob = GsJob();
    memset(r, 0, sizeof(ts3native_lanea.r));
    memset(&g, 0, sizeof(g));
    printf("lane a ghost sweep per lot: ok\n");
}
