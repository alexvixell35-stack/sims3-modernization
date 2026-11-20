// --- [GoalValidRO] Hits (gvhits.cpp, docs/670 §11): the validator's hit loop on the clone's footprints, on fakes (the game's
// 0x82EDB0, S2's point query, the lot lookup, the object service, the lot transform and the footprint slots replaced): every
// branch of the loop, the writes replayed only after a whole loop, LEAVE's return address, every doubt, the frame checks,
// the shadow prediction and its observer. Included by selftest.cpp after gvro-test.h (uses its fakes).
namespace gvht {
// a fake footprint: +0 vtable, +4 interface vtable, +0x14 id, +0x18/+0x1C the mask of the solid types, +0x35 type, +0x38 the
// polygon cache; the test's own fields from +0x40: class, slot +0x5C, slot +0xC0, the point test's answer
struct Fp { const void* vt; const void* svt; DWORD pad[3]; DWORD id[2]; DWORD mask2; DWORD pad2[5]; u8 b34, type, b36, b37; const u8* cache; int cls; u8 s5c, sc0, hit, pad3; };
static_assert(offsetof(Fp, id) == 0x14 && offsetof(Fp, type) == 0x35 && offsetof(Fp, cache) == 0x38, "fake footprint layout");
static LONG classCalls, ptCalls, finds, toLots;
static const u8* classRec;
static float ptU, ptV, ptR;
static u64 __fastcall Id(const u8* o, void*) { return ((u64)((const Fp*)o)->id[1] << 32) | ((const Fp*)o)->id[0]; }
static int __fastcall Class(const u8* o, void*, const void* rec, DWORD b) { classCalls++, classRec = (const u8*)rec; assert(!b); return ((const Fp*)o)->cls; }
static u8 __fastcall S5c(const u8* o, void*) { return ((const Fp*)o)->s5c; }
static u8 __fastcall Sc0(const u8* o, void*) { return ((const Fp*)o)->sc0; }
static u8 __fastcall Pt(const u8* o, void*, DWORD level, float u, float v, float r) { ptCalls++, ptU = u, ptV = v, ptR = r; assert(!level); return ((const Fp*)o)->hit; }
static int __fastcall Type(const u8* sub, void*) { return ((const Fp*)(sub - 4))->type; }
static const void* vt[0x40];
static const void* svt[4];
static u8 cacheOk[0x24], cacheBad[0x24];
static Fp fps[4];
static const u8* list[4];
static int nList, over;
static int Query(SrCtx* c, const u8*, DWORD, DWORD, const SrPt&, float, const u8*) {
    c->n = nList, c->over = over, c->fail = SR_OK;
    for (int i = 0; i < nList; i++) c->objs[i] = list[i];
    return SR_OK;
}
static u8 objs[2][0x10];  // object service objects: +4 the count
static const u8* __fastcall Find(const void*, void*, DWORD lo, DWORD hi) { finds++; return hi == 0 && (lo == 0x501 || lo == 0x502) ? objs[lo - 0x501] : NULL; }
static void __fastcall ToLot(const u8*, void*, const float* pt, float* out) { toLots++; out[0] = pt[0] + 100.0f, out[1] = 0, out[2] = pt[2] + 200.0f, out[3] = 0; }
static u8 __fastcall OptId(const u8*, void*, DWORD lo, DWORD hi) { return lo == 0x777 && hi == 0; }
static void Set(int i, DWORD id, int type, int cls, int s5c, int sc0, int hit) {
    Fp& f = fps[i];
    memset(&f, 0, sizeof(f));
    f.vt = vt, f.svt = svt, f.id[0] = id, f.type = (u8)type, f.cls = cls, f.s5c = (u8)s5c, f.sc0 = (u8)sc0, f.hit = (u8)hit, f.cache = cacheOk;
    list[i] = (const u8*)&f;
}
}  // namespace gvht

static void TestGoalHitsRO() {
    using namespace gvht;
    Ts3nCounters& k = ts3native_counters;
    GoalHitsRoCounters& h = ts3native_goalhitsro;
    static_assert(offsetof(GoalHitsRoCounters, gvhMode) == 0 && offsetof(GoalHitsRoCounters, gvhReady) == 8 && offsetof(GoalHitsRoCounters, gvhTaken) == 0x10 &&
                      offsetof(GoalHitsRoCounters, gvhFb) == 0x28 && offsetof(GoalHitsRoCounters, gvhShadowRuns) == 0x48 &&
                      offsetof(GoalHitsRoCounters, gvhShadowMismatch) == 0x4C && sizeof(GoalHitsRoCounters) == 0x60,
                  "ts3native_goalhitsro layout (docs/registry.md)");
    assert(kGvThunkSite.site == 0x0075A647 && kGvThunkSite.target == 0x0082EDB0 && GVH_RET == 0x0075A64C && GVH_LEAVE_AT == 0x0075AAC4 &&
           kGvhObservers[0].site == 0x0075A950 && kGvhObservers[0].target == 0x00AAC090 && kGvhObservers[1].site == 0x0075AACB &&
           kGvhObservers[1].target == 0x00652470);
    memset(&k.gvReady, 0, sizeof(Ts3nCounters) - offsetof(Ts3nCounters, gvReady));
    memset(&h, 0, sizeof(h));
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
    const GvFn savedOrig = g_gvOrig;
    const GvQueryFn savedQuery = g_gvQuery;
    const SrLotLookupFn savedLookup = g_srLotLookup;
    const AsyncSlot savedAsync = g_async;
    const LONG savedArmed = g_twArmed, savedOwner = k.rgOwnerTid;
    const float* savedOne = g_srOne;
    const float* savedRadius = g_gvhRadius;
    const DWORD* savedCost = g_gvhCostBits;
    const GvhFindFn savedFind = g_gvhFind;
    const GvhToLotFn savedToLot = g_gvhToLot;
    const SrOptIdFn savedOptId = g_srOptId;
    const bool savedCheck = g_srCheckSlots;
    static const float one = 1.0f, radius = 0.125f;
    static const DWORD costBits = 0x3A83126F;
    g_gvOrig = gvt::Orig, g_gvQuery = gvht::Query, g_srLotLookup = gvt::Lookup, g_srOne = &one, g_gvhRadius = &radius, g_gvhCostBits = &costBits;
    g_gvhFind = Find, g_gvhToLot = ToLot, g_srOptId = OptId, g_srCheckSlots = false;
    vt[2] = (const void*)Class, vt[0x5C / 4] = (const void*)S5c, vt[0x84 / 4] = (const void*)Id, vt[0xC0 / 4] = (const void*)Sc0, vt[0xD4 / 4] = (const void*)Pt;
    svt[2] = (const void*)Type;
    cacheOk[0x20] = 1;
    *(LONG*)(objs[0] + 4) = 3, *(LONG*)(objs[1] + 4) = 0;  // 0x502's count is 0: the game's smart pointer would destroy it
    const DWORD me = GetCurrentThreadId();
    g_async.owner = me, g_async.state = A_RUNNING;
    k.rgOwnerTid = 0, k.gvMode = 1, g_twArmed = 1;
    gvt::noLot = false, gvt::origCalls = 0, gvt::origRet = 1, gvt::origFill = 0;
    static u8 world[0x980], planner[0x10], wm[0x200], wm2[0x200], opts[0x60], self[0x40], out[0x40], owner[0x80], svc[0x10];
    *(u8**)(world + 0x95C) = planner, *(u8**)(world + 0x964) = wm;
    *(DWORD*)(gvt::lot + 0x48) = 0x30, *(DWORD*)(gvt::lot + 0x4C) = 0x1;
    __declspec(align(16)) static float pt[13] = {3.0f, 9.0f, 5.0f};
    *(DWORD*)(self + 0x20) = 0x111, *(DWORD*)(owner + 0x50) = 0x222;
    // the validator's frame: the stub's return slot, five popped arguments, then esp; ebp with (ebp & ~15) == esp + 0x100
    __declspec(align(16)) static u8 fr[0x180];
    DWORD* const slot = (DWORD*)(fr + 8);
    u8* const f = fr + 0x20;
    u8* const ebp = f + 0x108;
    DWORD* const vec = (DWORD*)(f + 0x84);
    DWORD cost = 0;
    auto frame = [&]() {
        memset(fr, 0, sizeof(fr));
        *slot = GVH_RET;
        vec[3] = 0xFED2FC;
        *(DWORD*)(f + 0xC0) = 0xE00, f[0xE4] = 1, *(u8**)(f + 0xE8) = opts;
        *(u8**)(f + 0x80) = owner, *(u8**)(f + 0x58) = wm2, *(u8**)(f + 0x9C) = svc;
        *(const float**)(ebp + 0x10) = pt, *(u8**)(ebp + 0x14) = opts, *(u8**)(ebp + 0x18) = self, *(u8**)(ebp + 0x20) = out, *(DWORD**)(ebp + 0x24) = &cost;
    };
    auto call = [&](DWORD blkLo = 0) {
        *(DWORD*)(out + 0x20) = blkLo, *(DWORD*)(out + 0x24) = 0;
        cost = 0x11111111;
        *slot = GVH_RET;
        vec[0] = vec[1] = 0;  // as the call site passes it
        return GvStub(world, ebp, pt, 0.1f, vec, 0, 0xE00);
    };
    auto blk = [&]() { return *(DWORD*)(out + 0x20); };
    frame();
    g_gvhTestSlot = slot;
    nList = 1, over = 0;

    // Hits not installed: footprints found = the game's call, as before
    Set(0, 0x600, 3, 3, 0, 0, 0);
    assert(call() == 0xABCD01 && gvt::origCalls == 1 && k.gvFb[GV_FB_HITS] == 1 && h.gvhRuns == 0);
    h.gvhReady = 1, h.gvhMode = 1;

    // a type the loop does not look at: the loop ends, nothing written, al 1, the vector empty, the return address kept
    assert(call(0x9) == 1 && gvt::origCalls == 1 && h.gvhTaken == 1 && h.gvhLeave == 0 && blk() == 0x9 && cost == 0x11111111 && *slot == GVH_RET &&
           vec[0] == 0 && vec[1] == 0 && h.gvhFootprints == 1 && k.gvFb[GV_FB_HITS] == 1);
    // the three id skips come before the type: a solid footprint with each id does not reject the point
    for (DWORD id : {(DWORD)0x111, (DWORD)0x222, (DWORD)0x777}) {
        Set(0, id, 2, 3, 0, 0, 0);
        assert(call() == 1 && *slot == GVH_RET);
    }
    // solid types (2, 0xC, 0xD, 0x11, 0x17): LEAVE unless [esp+0x13], ![esp+0x17], slot +0x5C and a mask that is not -1
    for (int type : {2, 0xC, 0xD, 0x11, 0x17}) {
        Set(0, 0x600, type, 3, 1, 0, 0);
        assert(call(0x9) == 1 && *slot == GVH_LEAVE_AT && blk() == 0x9);  // [esp+0x13] is 0
        f[0x13] = 1;
        assert(call(0x9) == 1 && *slot == GVH_RET && blk() == 0);  // passes: the blocker id is cleared, next
        f[0x17] = 1;
        assert(call(0x9) == 1 && *slot == GVH_LEAVE_AT && blk() == 0x9);
        f[0x17] = 0;
        fps[0].s5c = 0;
        assert(call(0x9) == 1 && *slot == GVH_LEAVE_AT);
        fps[0].s5c = 1, fps[0].id[1] = 0xFFFFFFFF, fps[0].mask2 = 0xFFFFFFFF;  // obj+0x18 & +0x1C == -1 (the id's high word is +0x18)
        assert(call(0x9) == 1 && *slot == GVH_LEAVE_AT);
        f[0x13] = 0;
    }
    const LONG leave0 = h.gvhLeave;
    assert(leave0 == 5 * 4 && h.gvhBlocker == 5);
    // types 9 and 0x14: the blocker id when none is set and the object exists; then the solid rule
    for (int type : {9, 0x14}) {
        Set(0, 0x501, type, 3, 1, 0, 0);
        assert(call() == 1 && *slot == GVH_LEAVE_AT && blk() == 0x501);
        assert(call(0x9) == 1 && *slot == GVH_LEAVE_AT && blk() == 0x9);  // a blocker is set already
        fps[0].id[0] = 0x600;  // not in the object service
        assert(call() == 1 && *slot == GVH_LEAVE_AT && blk() == 0);
        f[0x13] = 1, fps[0].id[0] = 0x501;
        assert(call() == 1 && *slot == GVH_RET && blk() == 0);  // set, then cleared by the solid rule
        f[0x13] = 0;
    }
    // type 1 (a Sim): id 0 and an unknown object are skipped before any slot
    Set(0, 0, 1, 3, 1, 1, 1);
    classCalls = ptCalls = 0;
    assert(call() == 1 && *slot == GVH_RET && cost == 0x11111111 && classCalls == 0);
    fps[0].id[0] = 0x600;
    assert(call() == 1 && *slot == GVH_RET && cost == 0x11111111 && classCalls == 0);
    // slot +0xC0 true: class 0 / 1 / 0xD skipped; else the lot, the point through its transform, the point test
    fps[0].id[0] = 0x501;
    for (int cls : {0, 1, 0xD}) {
        fps[0].cls = cls;
        assert(call() == 1 && *slot == GVH_RET && cost == 0x11111111 && ptCalls == 0);
    }
    assert(classRec == f + 0xC0);  // the validator's record
    fps[0].cls = 3;
    gvt::lookups = 0, toLots = 0;
    assert(call() == 1 && *slot == GVH_LEAVE_AT && blk() == 0x501 && cost == 0x11111111 && ptCalls == 1 && ptU == 103.0f && ptV == 205.0f && ptR == 0.125f);
    assert(toLots == 1 && gvt::lookups == 2);  // the query's lot lookup and the loop's own
    assert(call(0x9) == 1 && *slot == GVH_LEAVE_AT && blk() == 0x9);  // covered, a blocker already set: kept
    fps[0].hit = 0;
    assert(call() == 1 && *slot == GVH_RET && blk() == 0 && cost == costBits);  // not covered: the cost, next
    gvt::noLot = true;  // (the query finds no lot either: [GoalValidRO]'s own answer, al 0)
    assert(call() == 0 && cost == 0x11111111);
    gvt::noLot = false;
    // slot +0xC0 false: slot +0x5C false, or class 0 / 1 / 0xD: the cost; else LEAVE without a blocker id
    fps[0].sc0 = 0, fps[0].s5c = 0;
    assert(call() == 1 && *slot == GVH_RET && cost == costBits);
    fps[0].s5c = 1;
    assert(call() == 1 && *slot == GVH_LEAVE_AT && blk() == 0 && cost == 0x11111111);
    fps[0].cls = 0xD;
    assert(call() == 1 && *slot == GVH_RET && cost == costBits);
    // order: the list is walked front to back and the loop stops at the first LEAVE; earlier writes stay
    Set(0, 0x501, 1, 3, 0, 0, 0);   // a Sim: the cost
    Set(1, 0x501, 9, 3, 1, 0, 0);   // the blocker id, then LEAVE ([esp+0x13] is 0)
    Set(2, 0x600, 2, 3, 1, 0, 0);
    nList = 3;
    assert(call() == 1 && *slot == GVH_LEAVE_AT && blk() == 0x501 && cost == costBits && h.gvhMaxN == 3);
    // doubts: nothing is written, the game's call answers
    const LONG orig0 = gvt::origCalls;
    Set(2, 0x502, 1, 3, 0, 0, 0);  // a Sim whose object has a count of 0, after two footprints that write
    f[0x13] = 1;
    assert(call() == 0xABCD01 && gvt::origCalls == orig0 + 1 && h.gvhFb[GVH_FB_REF] == 1 && blk() == 0 && cost == 0x11111111 && *slot == GVH_RET);
    f[0x13] = 0;
    Set(0, 0x501, 1, 3, 0, 1, 1);
    fps[0].cache = cacheBad;  // the level-0 polygon cache is not valid: the game would rebuild it
    nList = 1;
    assert(call() == 0xABCD01 && h.gvhFb[GVH_FB_CACHE] == 1 && ptCalls == 3);
    fps[0].cache = cacheOk;
    over = 1;  // the clone's list is full
    assert(call() == 0xABCD01 && h.gvhFb[GVH_FB_OVER] == 1);
    over = 0;
    g_srCheckSlots = true;  // an unknown slot +0x5C target
    assert(call() == 0xABCD01 && h.gvhFb[GVH_FB_SLOT] == 1);
    g_srCheckSlots = false;
    wm2[0x1CC] = 1;  // the loop's lot lookup would rebuild the lot index
    assert(call() == 0xABCD01 && h.gvhFb[GVH_FB_LOTIDX] == 1);
    wm2[0x1CC] = 0;
    *(u8**)(ebp + 0x20) = self;  // the blocker id would be written where the loop reads the caller's id
    assert(GvStub(world, ebp, pt, 0.1f, vec, 0, 0xE00) == 0xABCD01 && h.gvhFb[GVH_FB_ALIAS] == 1);
    *(u8**)(ebp + 0x20) = out;
    // the frame checks: another return address, ebp off the frame, another vector, a record that is not the validator's
    const LONG fr0 = h.gvhFb[GVH_FB_FRAME], taken0 = h.gvhTaken;
    *slot = GVH_RET + 1;
    assert(GvStub(world, ebp, pt, 0.1f, vec, 0, 0xE00) == 0xABCD01 && h.gvhFb[GVH_FB_FRAME] == fr0 + 1);
    *slot = GVH_RET;
    assert(GvStub(world, ebp + 0x10, pt, 0.1f, vec, 0, 0xE00) == 0xABCD01 && GvStub(world, NULL, pt, 0.1f, vec, 0, 0xE00) == 0xABCD01);
    DWORD vec2[4] = {0, 0, 0, 0xFED2FC};
    assert(GvStub(world, ebp, pt, 0.1f, vec2, 0, 0xE00) == 0xABCD01 && GvStub(world, ebp, pt, 0.1f, vec, 0, 0xE01) == 0xABCD01);
    f[0xE4] = 0;
    assert(GvStub(world, ebp, pt, 0.1f, vec, 0, 0xE00) == 0xABCD01);
    f[0xE4] = 1;
    assert(h.gvhFb[GVH_FB_FRAME] == fr0 + 6 && h.gvhTaken == taken0 && *slot == GVH_RET);
    // the runtime switch off: the game's call, the loop not run
    h.gvhMode = 0;
    const LONG runs0 = h.gvhRuns;
    assert(call() == 0xABCD01 && h.gvhRuns == runs0);
    h.gvhMode = 1;
    assert(call() == 1 && *slot == GVH_LEAVE_AT && h.gvhRuns == runs0 + 1);

    // HitsShadow, no job: the game's call fills its vector, the loop on the clone is the prediction, the observer on the way
    // out compares what the game's loop did
    g_async.state = A_IDLE;
    h.gvhReady = 2, h.gvhShadow = 1, h.gvhMode = 0, k.gvMode = 0;
    gvt::fill[0] = 0, gvt::origFill = 1;  // the game's vector: one footprint
    Set(0, 0x501, 9, 3, 1, 0, 0);         // prediction: blocker 0x501, LEAVE
    assert(call() == 0xABCD01 && g_gvhPend.valid && g_gvhPend.ans == GVH_LEAVE && blk() == 0 && *slot == GVH_RET);
    *(DWORD*)(out + 0x20) = 0x501;        // what the game's loop does
    GvhObserve(GVH_LEAVE, f + 4);         // another frame: not this prediction
    assert(g_gvhPend.valid && h.gvhShadowRuns == 0);
    GvhObserve(GVH_LEAVE, f);
    assert(!g_gvhPend.valid && h.gvhShadowRuns == 1 && h.gvhShadowLeave == 1 && h.gvhShadowMismatch == 0);
    assert(call() == 0xABCD01 && g_gvhPend.valid);
    GvhObserve(GVH_END, f);               // the game's loop ended instead and wrote nothing
    assert(h.gvhShadowRuns == 2 && h.gvhShadowMismatch == 1);
    Set(0, 0x501, 1, 3, 0, 0, 0);         // prediction: the cost, the loop's end
    assert(call() == 0xABCD01 && g_gvhPend.valid && g_gvhPend.ans == GVH_END);
    cost = costBits;
    GvhObserve(GVH_END, f);
    assert(h.gvhShadowRuns == 3 && h.gvhShadowMismatch == 1);
    assert(call() == 0xABCD01);           // the game's loop does not write the cost: a mismatch
    GvhObserve(GVH_END, f);
    assert(h.gvhShadowMismatch == 2 && h.gvhShadowLost == 0);
    assert(call() == 0xABCD01 && call() == 0xABCD01 && h.gvhShadowLost == 1);  // a prediction nobody observed
    g_gvhPend.valid = false;
    gvt::origFill = 0;                    // an empty game vector: no loop, no prediction
    assert(call() == 0xABCD01 && !g_gvhPend.valid);

    printf("GoalValidRO Hits: export layout; the loop on fakes (id skips, solid types, 9 / 0x14 blocker, a Sim with and without slot "
           "+0xC0, order, first LEAVE); writes only after a whole loop; LEAVE moves the return address to 0x75AAC4; doubts (object "
           "count, polygon cache, full list, slot, lot index, alias) and 6 frame checks call the game; shadow prediction + observer\n");
    g_gvhTestSlot = NULL;
    memset(&h, 0, sizeof(h));
    memset(&k.gvReady, 0, sizeof(Ts3nCounters) - offsetof(Ts3nCounters, gvReady));
    g_async = savedAsync;
    g_twArmed = savedArmed, k.rgOwnerTid = savedOwner;
    g_gvOrig = savedOrig, g_gvQuery = savedQuery, g_srLotLookup = savedLookup, g_srOne = savedOne, g_gvhRadius = savedRadius, g_gvhCostBits = savedCost;
    g_gvhFind = savedFind, g_gvhToLot = savedToLot, g_srOptId = savedOptId, g_srCheckSlots = savedCheck;
}
