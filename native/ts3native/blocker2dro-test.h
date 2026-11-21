// TestBlocker2dRO ([Blocker2dRO], rtoro.cpp, docs/670 §24): the stub at 0x74E261 (the IC's call of RoutingWorld_BlockerCheck2d
// 0x82E340) on fakes. Included by selftest.cpp after rtoro-test.h: it uses that file's fake footprints, options, gather and
// lot table (namespace rto) and rtotail-test.h's physics stubs (namespace rtt), and sets every one of them up itself, so it
// does not depend on TestRouteToObjRO having run. No thread and no timer: the worker side of the protocol (QL exclusive,
// tripwires, job end) is driven by setting its state words. Cases hand-derived from the disassembly 0x82E340..0x82ED9E:
// the byte arguments b24 / b28 / b2c / b30 / b34 against the option tests, the ignore ids (*ign, the target, option
// 0x80000's parent, no target), every type rule and the merge, the physics tail with Tail 1 and 0, and each give-back
// (option 0x100000 with a slot component, debug draw, lot table miss, polygon cache, QL busy, tripwire, job end, may not,
// not the routing world), in Mode 1 with a job "running" and in shadow with none; out[1] is never written.
namespace b2t {
static LONG gameCalls, gameOpen;
static DWORD expCode, expLo, expHi;
static u8 dbg;
static u8 parent[0x20];
static __declspec(align(16)) float start[12];
static float goal[3];
static __declspec(align(16)) u8 world[0xA00];
static DWORD* __fastcall Game(u8*, void*, DWORD* out, u8*, const float*, const float*, u8*, u8*, const DWORD*, DWORD, DWORD, DWORD, DWORD,
                              DWORD, DWORD) {
    gameCalls++;
    gameOpen = g_rgOffSimOpen;
    out[0] = expCode, out[2] = expLo, out[3] = expHi;
    return out;
}
enum { X_CACHE = 1, X_SLOTCOMP = 2, X_DEBUG = 4, X_LOTMISS = 8, X_PARENT = 0x10, X_TAILOFF = 0x20, X_PHYSHIT = 0x40, X_TAIL = 0x80,
       X_OTHERWORLD = 0x100 };
struct Case {
    const char* name;
    int sTypes[3];  // gathered footprints (0 = none), ids 0x600 + i; +0x200 = id in the options' lists, +0x400 = +0xC0 false, +0x800 = no hit
    DWORD bitsA, bitsB;
    u8 b24, b28, b2c, b30, b34;
    DWORD ign;      // *ign (low word)
    int tgt;        // 0 = no target, else the target's id
    int x;          // X_*
    LONG code;      // >= 0: the answer's code; < 0: -1 - B2_FB_*
    DWORD lo;
};
}  // namespace b2t

static void TestBlocker2dRO() {
    using namespace b2t;
    Ts3nCounters& kc = ts3native_counters;
    Blocker2dRoCounters& k = ts3native_blocker2dro;
    static_assert(offsetof(Blocker2dRoCounters, b2Mode) == 0 && offsetof(Blocker2dRoCounters, b2Shadow) == 4 &&
                      offsetof(Blocker2dRoCounters, b2Tail) == 0xC && offsetof(Blocker2dRoCounters, b2Code) == 0x1C &&
                      offsetof(Blocker2dRoCounters, b2TailRuns) == 0x3C && offsetof(Blocker2dRoCounters, b2Fb) == 0x4C &&
                      offsetof(Blocker2dRoCounters, b2GameUs) == 0x74 && offsetof(Blocker2dRoCounters, b2ShadowMismatch) == 0x80 &&
                      sizeof(Blocker2dRoCounters) == 0x88,
                  "ts3native_blocker2dro layout (docs/registry.md)");
    assert(kB2Site.site == 0x0074E261 && kB2Site.target == 0x0082E340);
    memset(&k, 0, sizeof(k));
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
    const DWORD me = GetCurrentThreadId();
    // preconditions: this thread is the owner, holds no QL and no gate frame, serves no job callback; nothing holds job starts off
    assert(!t_qlDepth && !t_qlShared && !g_inInvoke && t_rg.n == t_rg.readers && !g_rgOffSimOpen);
    // saved globals
    const AsyncSlot savedAsync = g_async;
    const LONG savedArmed = g_twArmed, savedActive = g_twActive, savedOwner = kc.rgOwnerTid, savedBroken = g_steerBroken;
    const SrWorldFn savedWorld = g_srWorldGet;
    const SrLotFindFn savedFind = g_srLotFind;
    const SrActiveLotFn savedAct = g_srActiveLot;
    const float* savedOne = g_srOne;
    const SrOptIdFn savedOptId = g_srOptId;
    const SrOptTestFn savedOptTest = g_srOptTest;
    const bool savedCheck = g_srCheckSlots;
    const B2Fn savedOrig = g_b2Orig;
    const u8* const savedDbg = g_b2DebugDraw;
    const RtoCompFn savedComp = g_rtoComp;
    const RtoOptCopyFn savedCopy = g_rtoOptCopy;
    const RtoOptSetFn savedSet = g_rtoOptSet;
    const RtoOptFreeFn savedFree = g_rtoOptFree;
    const RtoListFn savedList = g_rtoSimList;
    const RtoPortalFn savedPortal = g_rtoPortal;
    const RtoGatherFn savedGather = g_rtoGather;
    const DWORD* const savedKeyTc = g_rtoKeyTc;
    const DWORD* const savedKeySlot = g_rtoKeySlot;
    const float* const savedSegR = g_rtoSegR;
    g_async.owner = me, g_async.state = A_IDLE;
    kc.rgOwnerTid = 0;
    g_twArmed = 0, g_twActive = 0, g_steerBroken = 0;
    g_srWorldGet = []() -> u8* { return world; };
    g_srLotFind = [](const void* tbl, void*, DWORD* o, const DWORD* key) {
        const DWORD* b = *(const DWORD* const*)((const u8*)tbl + 4);
        o[0] = key[0] == *(const DWORD*)(uintptr_t)b[0] ? b[0] : b[1];
    };
    g_srActiveLot = []() -> const u8* { return rto::actLot; };
    g_srOne = &rto::one;
    g_srOptId = rto::OptId, g_srOptTest = rto::OptTest;
    g_srCheckSlots = false;
    g_b2Orig = Game, g_b2DebugDraw = &dbg;
    g_rtoComp = rto::Comp, g_rtoOptCopy = rto::OptCopy, g_rtoOptSet = rto::OptSet, g_rtoOptFree = rto::OptFree;
    g_rtoSimList = rto::SimList, g_rtoPortal = rto::PortalTest, g_rtoGather = rto::Gather;
    g_rtoKeyTc = &rto::keyTc, g_rtoKeySlot = &rto::keySlot, g_rtoSegR = &rto::segR;

    // the fake world: a planner with one lot (key 0x30; u = x + 10, v = z), the lot object (rto::sim: id +0x48, ignore id +0x200)
    using rto::p; using rto::node; using rto::sim; using rto::target; using rto::tc; using rto::opts; using rto::fp; using rto::cache;
    memset(world, 0, sizeof(world));
    memset(p, 0, sizeof(rto::p));
    memset(node, 0, sizeof(rto::node));
    memset(sim, 0, sizeof(rto::sim));
    memset(target, 0, sizeof(rto::target));
    memset(tc, 0, sizeof(rto::tc));
    memset(opts, 0, sizeof(rto::opts));
    memset(parent, 0, sizeof(parent));
    memset(rto::actLot, 0, sizeof(rto::actLot));
    world[0x974] = 1;
    *(u8**)(world + 0x95C) = p;
    *(DWORD*)(rto::actLot + 0x48) = 0x77;
    *(DWORD*)node = 0x30;
    u8* entry = node + 0x10;
    *(DWORD*)entry = 0x30;
    float* rw = (float*)(entry + 0xA0);
    rw[0] = rw[5] = rw[10] = rw[15] = 1.0f, rw[12] = 10.0f;
    rto::bk[0] = (DWORD)(uintptr_t)node, rto::bk[1] = 0x5E117E1;
    *(DWORD**)(p + 0x4C) = rto::bk, *(DWORD*)(p + 0x50) = 1;
    *(DWORD*)(parent + 8) = 0x600;
    memset(start, 0, sizeof(start));
    start[0] = 3.0f, start[1] = 2.0f, start[2] = 5.0f, start[3] = 3.0f;  // level (+0x24) 0
    goal[0] = 7.0f, goal[1] = 5.0f, goal[2] = 0.0f;
    memset(rto::fvt, 0, sizeof(rto::fvt));
    rto::fvt[0] = (void*)rto::Levels, rto::fvt[3] = (void*)rto::Type, rto::fvt[0x44 / 4] = (void*)rto::F44;
    rto::fvt[0x7C / 4] = (void*)rto::Id, rto::fvt[0x84 / 4] = (void*)rto::Id, rto::fvt[0x88 / 4] = (void*)rto::Portal;
    rto::fvt[0xC0 / 4] = (void*)rto::C0, rto::fvt[0xD8 / 4] = (void*)rto::Seg;
    rto::torn = 0, rto::gen1 = rto::gen2 = 0;

    static DWORD out[4];
    static const Case* cur;
    auto call = []() -> DWORD* {
        const Case& c = *cur;
        out[0] = out[1] = out[2] = out[3] = 0xEEEEEEEE;
        return B2Stub(c.x & X_OTHERWORLD ? world + 0x10 : world, NULL, out, sim, start, goal, opts, c.tgt ? target : NULL,
                      (const DWORD*)(sim + 0x200), c.b24, c.b28, c.b2c, c.b30, c.b34, 0x5A);
    };

    const LONG F = -1;
    static const Case cases[] = {
        {"nothing gathered", {0}, 0, 0, 0, 0, 0, 0, 0, 0, 0x900, 0, 0, 0},
        {"no target, nothing gathered", {0}, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0},
        {"0x14 with option 4 (code 1, no id)", {0x14}, 4, 0, 0, 0, 0, 0, 0, 0, 0x900, 0, 1, 0},
        {"0x14 with b2c", {0x14}, 0, 0, 0, 0, 1, 0, 0, 0, 0x900, 0, 1, 0},
        {"0x14 without option 4", {0x14}, 0, 0, 0, 0, 0, 0, 0, 0, 0x900, 0, 0, 0},
        {"portal (code 3, its id)", {1}, 0, 0, 0, 0, 0, 0, 0, 0, 0x900, 0, 3, 0x600},
        {"portal with b24", {1}, 0, 0, 1, 0, 0, 0, 0, 0, 0x900, 0, 0, 0},
        {"portal with option 2", {1}, 2, 0, 0, 0, 0, 0, 0, 0, 0x900, 0, 0, 0},
        {"0xC (code 4)", {0xC}, 0, 0, 0, 0, 0, 0, 0, 0, 0x900, 0, 4, 0},
        {"0x17 with b28", {0x17}, 0, 0, 0, 1, 0, 0, 0, 0, 0x900, 0, 0, 0},
        {"0xC with option 1", {0xC}, 1, 0, 0, 0, 0, 0, 0, 0, 0x900, 0, 0, 0},
        {"9 (code 6, its id)", {9}, 0, 0, 0, 0, 0, 0, 0, 0, 0x900, 0, 6, 0x600},
        {"0x10 without option 0x800", {0x10}, 0, 0, 0, 0, 0, 0, 0, 0, 0x900, 0, 0, 0},
        {"0x1D with option 0x800 (code 7)", {0x1D}, 0, 0x800, 0, 0, 0, 0, 0, 0, 0x900, 0, 7, 0},
        {"0x13 with b30", {0x13}, 0, 0, 0, 0, 0, 1, 0, 0, 0x900, 0, 0, 0},
        {"0x18 without b30 (code 7)", {0x18}, 0, 0, 0, 0, 0, 0, 0, 0, 0x900, 0, 7, 0},
        {"another type (code 7)", {3}, 0, 0, 0, 0, 0, 0, 0, 0, 0x900, 0, 7, 0},
        {"b34: 0xC only (code 4)", {9, 0xC}, 0, 0, 0, 0, 0, 0, 1, 0, 0x900, 0, 4, 0},
        {"b34: 9 and portal are nothing", {9, 1}, 0, 0, 0, 0, 0, 0, 1, 0, 0x900, 0, 0, 0},
        {"b34 with option 4: no physics tail", {0}, 4, 0, 0, 0, 0, 0, 1, 0, 0x900, 0, 0, 0},
        {"merge: 0xC, 9, 3 -> 7", {0xC, 9, 3}, 0, 0, 0, 0, 0, 0, 0, 0, 0x900, 0, 7, 0},
        {"merge: 0xC then 9 -> 6 with the second id", {0xC, 9}, 0, 0, 0, 0, 0, 0, 0, 0, 0x900, 0, 6, 0x601},
        {"*ign is the footprint's id", {9}, 0, 0, 0, 0, 0, 0, 0, 0x600, 0x900, 0, 0, 0},
        {"the target's id is the footprint's", {9}, 0, 0, 0, 0, 0, 0, 0, 0, 0x600, 0, 0, 0},
        {"no target: nothing but *ign ignored", {9}, 0, 0, 0, 0, 0, 0, 0, 0x777, 0, 0, 6, 0x600},
        {"option 0x80000: the target's parent ignored", {9}, 0x80000, 0, 0, 0, 0, 0, 0, 0, 0x900, X_PARENT, 0, 0},
        {"no option 0x80000: the parent is not ignored", {9}, 0, 0, 0, 0, 0, 0, 0, 0, 0x900, X_PARENT, 6, 0x600},
        {"id in the options' lists", {0x209}, 0, 0, 0, 0, 0, 0, 0, 0, 0x900, 0, 0, 0},
        {"+0xC0 false", {0x409}, 0, 0, 0, 0, 0, 0, 0, 0, 0x900, 0, 0, 0},
        {"no segment hit", {0x809}, 0, 0, 0, 0, 0, 0, 0, 0, 0x900, 0, 0, 0},
        {"option 0x100000, no slot component", {9}, 0x100000, 0, 0, 0, 0, 0, 0, 0, 0x900, 0, 6, 0x600},
        {"option 0x100000, no target", {9}, 0x100000, 0, 0, 0, 0, 0, 0, 0, 0, X_SLOTCOMP, 6, 0x600},
        {"option 0x100000 with a slot component", {9}, 0x100000, 0, 0, 0, 0, 0, 0, 0, 0x900, X_SLOTCOMP, F - B2_FB_OPT, 0},
        {"debug draw on", {9}, 0, 0, 0, 0, 0, 0, 0, 0, 0x900, X_DEBUG, F - B2_FB_OPT, 0},
        {"lot not in the table", {9}, 0, 0, 0, 0, 0, 0, 0, 0, 0x900, X_LOTMISS, F - B2_FB_LOT, 0},
        {"polygon cache invalid", {9}, 0, 0, 0, 0, 0, 0, 0, 0, 0x900, X_CACHE, F - B2_FB_CACHE, 0},
        {"not the routing world", {9}, 0, 0, 0, 0, 0, 0, 0, 0, 0x900, X_OTHERWORLD, F - B2_FB_SHAPE, 0},
        {"tail: nothing hit, option 4, no physics blocker", {0x814}, 4, 0, 0, 0, 0, 0, 0, 0, 0x900, X_TAIL, 0, 0},
        {"tail: b2c, a physics blocker (code 1, its id)", {0}, 0, 0, 0, 0, 1, 0, 0, 0, 0x900, X_TAIL | X_PHYSHIT, 1, 0xABC},
        {"tail: no target, a physics blocker", {0}, 4, 0, 0, 0, 0, 0, 0, 0, 0, X_TAIL | X_PHYSHIT, 1, 0xABC},
        {"tail off: given back", {0}, 4, 0, 0, 0, 0, 0, 0, 0, 0x900, X_TAILOFF, F - B2_FB_TAIL, 0},
        {"tail off, a footprint answers: no tail needed", {9}, 4, 0, 0, 0, 0, 0, 0, 0, 0x900, X_TAILOFF, 6, 0x600},
    };
    const int nCases = (int)(sizeof(cases) / sizeof(cases[0]));
    rtt::Use();
    for (int ci = 0; ci < nCases; ci++) {
        const Case& c = cases[ci];
        cur = &c;
        rto::ns = 0;
        for (int i = 0; i < 3 && c.sTypes[i]; i++) {
            const int t = c.sTypes[i];
            rto::MakeFp(fp[4 + i], cache[4 + i], 0x600 + i, t & 0xFF, !(t & 0x800));
            fp[4 + i][0x3C] = (t & 0x400) != 0;
            if (c.x & X_CACHE) cache[4 + i][0x20] = 0;
            rto::sList[rto::ns++] = fp[4 + i];
        }
        *(DWORD*)opts = c.bitsA, *(DWORD*)(opts + 4) = c.bitsB, *(DWORD*)(opts + 8) = c.sTypes[0] & 0x200 ? 0x600 : 0;
        *(DWORD*)(sim + 0x200) = c.ign;
        *(DWORD*)(sim + 0x48) = c.x & X_LOTMISS ? 0x31 : 0x30;
        *(DWORD*)(target + 8) = (DWORD)c.tgt;
        rto::tcOn = true, rto::slotCompOn = (c.x & X_SLOTCOMP) != 0;
        *(u8**)(tc + 0x30) = c.x & X_PARENT ? parent : NULL;
        dbg = (c.x & X_DEBUG) != 0;
        const bool fb = c.code < 0;
        const int fbi = fb ? (int)(F - c.code) : 0;
        // the gather's own asserts read the lot key 0x30: a case that gives back before the gather never reaches it
        for (int mode = 0; mode < 2; mode++) {  // 0: Mode 1 with a job "running", 1: shadow with no job
            k.b2Mode = mode == 0, k.b2Shadow = mode == 1, k.b2Tail = !(c.x & X_TAILOFF);
            g_async.state = mode == 0 ? A_RUNNING : A_IDLE;
            g_twArmed = mode == 0;
            expCode = fb ? 0x77 : (DWORD)c.code, expLo = fb ? 0x7777 : c.lo, expHi = 0;
            rtt::Reset();
            rtt::ids[0][0] = 0xABC, rtt::ids[0][1] = 0, rtt::nIds = 1;
            rtt::blockers[0][0] = 0xABC, rtt::blockers[0][1] = 0, rtt::nBlockers = 1;
            rtt::hitAt = c.x & X_PHYSHIT ? 1 : -1;
            const LONG game0 = gameCalls, taken0 = k.b2Taken, fb0 = k.b2Fb[fbi], runs0 = k.b2ShadowRuns, mm0 = k.b2ShadowMismatch;
            const LONG sfb0 = k.b2ShadowFb, code0 = fb ? 0 : k.b2Code[c.code], tails0 = k.b2TailRuns, hits0 = k.b2TailHits;
            const LONG gath0 = rto::gatherCalls, rtoTails0 = kc.rtoTailRuns;
            gameOpen = -1;
            DWORD* r = call();
            bool ok = r == out && out[1] == 0xEEEEEEEE && out[0] == expCode && out[2] == expLo && out[3] == expHi && !g_rgOffSimOpen &&
                      !t_qlShared && kc.rtoTailRuns == rtoTails0;
            if (mode == 0) {
                ok = ok && (fb ? gameCalls == game0 + 1 && k.b2Fb[fbi] == fb0 + 1 && k.b2Taken == taken0 && gameOpen == 0
                               : gameCalls == game0 && k.b2Taken == taken0 + 1 && k.b2Code[c.code] == code0 + 1);
            } else {
                // docs/670 §26: the game's call runs as with the switch off (no start hold-off open around it)
                ok = ok && gameCalls == game0 + 1 && gameOpen == 0 && k.b2ShadowRuns == runs0 + 1 && k.b2ShadowMismatch == mm0 &&
                     k.b2ShadowFb == sfb0 + (fb ? 1 : 0) && k.b2Taken == taken0;
            }
            if (c.x & X_TAIL)
                ok = ok && k.b2TailRuns == tails0 + 1 && k.b2TailHits == hits0 + (c.x & X_PHYSHIT ? 1 : 0) &&
                     rtt::ranges == (c.x & X_PHYSHIT ? 2 : 5) && rtt::frees == (c.x & X_PHYSHIT ? 1 : 0);
            else
                ok = ok && k.b2TailRuns == tails0 && rtt::ranges == 0;
            if (!fb) ok = ok && rto::gatherCalls == gath0 + 1;
            if (!ok) {
                printf("Blocker2dRO case '%s' %s: got %ld %08lX:%08lX, want %ld %08lX:%08lX (game calls %ld, taken %ld, fb %ld, tails %ld, "
                       "ranges %d)\n", c.name, mode ? "shadow" : "mode 1", out[0], out[3], out[2], expCode, expHi, expLo, gameCalls - game0,
                       k.b2Taken - taken0, k.b2Fb[fbi] - fb0, k.b2TailRuns - tails0, rtt::ranges);
                assert(false);
            }
            for (int i = 4; i < 7; i++) assert(*(const DWORD*)(fp[i] + 8) == 1 || !*(const void* const*)fp[i]);  // no refcount write
        }
    }
    rtt::Restore();

    // one plain world for the state cases: a footprint of type 9 answers 6 / 0x600
    static const Case plain = {"plain", {9}, 0, 0, 0, 0, 0, 0, 0, 0, 0x900, 0, 6, 0x600};
    cur = &plain;
    rto::ns = 0;
    rto::MakeFp(fp[4], cache[4], 0x600, 9, 1);
    rto::sList[rto::ns++] = fp[4];
    *(DWORD*)opts = *(DWORD*)(opts + 4) = *(DWORD*)(opts + 8) = 0;
    *(DWORD*)(sim + 0x200) = 0, *(DWORD*)(sim + 0x48) = 0x30, *(DWORD*)(target + 8) = 0x900;
    *(u8**)(tc + 0x30) = NULL;
    rto::slotCompOn = false, dbg = 0;
    expCode = 0x77, expLo = 0x7777, expHi = 0;
    k.b2Tail = 1;
    // both switches off, and another thread: the game's call, nothing counted
    {
        k.b2Mode = k.b2Shadow = 0, g_async.state = A_RUNNING, g_twArmed = 1;
        const LONG game0 = gameCalls, calls0 = k.b2Calls;
        call();
        assert(gameCalls == game0 + 1 && k.b2Calls == calls0 && out[0] == 0x77);
        k.b2Mode = 1, g_async.owner = me + 1;
        call();
        assert(gameCalls == game0 + 2 && k.b2Calls == calls0 && out[0] == 0x77);
        g_async.owner = me;
        // Mode 0 with a job and no shadow: counted as a job call, the game's call
        k.b2Mode = 0, k.b2Shadow = 1;
        const LONG job0 = k.b2Job, runs0 = k.b2ShadowRuns;
        call();
        assert(gameCalls == game0 + 3 && k.b2Job == job0 + 1 && k.b2ShadowRuns == runs0 && out[0] == 0x77);
        // Mode 1, no job, no shadow: the game's call
        k.b2Mode = 1, k.b2Shadow = 0, g_async.state = A_IDLE;
        call();
        assert(gameCalls == game0 + 4 && k.b2Job == job0 + 1 && out[0] == 0x77);
    }
    // the +0x918 owner may not; QL held exclusive (a worker writer); a tripwire call in flight; the job's writers disarmed
    {
        k.b2Mode = 1, k.b2Shadow = 0, g_async.state = A_RUNNING, g_twArmed = 1;
        LONG game0 = gameCalls;
        kc.rgOwnerTid = (LONG)me;
        const LONG may0 = k.b2Fb[B2_FB_MAYNOT];
        call();
        assert(gameCalls == game0 + 1 && k.b2Fb[B2_FB_MAYNOT] == may0 + 1 && out[0] == 0x77);
        kc.rgOwnerTid = 0;
        const LONG try0 = k.b2Fb[B2_FB_TRY];
        AcquireSRWLockExclusive(&g_ql);
        call();
        ReleaseSRWLockExclusive(&g_ql);
        assert(gameCalls == game0 + 2 && k.b2Fb[B2_FB_TRY] == try0 + 1 && out[0] == 0x77);
        const LONG trip0 = k.b2Fb[B2_FB_TRIP];
        g_twActive = 1;
        call();
        g_twActive = 0;
        assert(gameCalls == game0 + 3 && k.b2Fb[B2_FB_TRIP] == trip0 + 1 && out[0] == 0x77);
        const LONG end0 = k.b2Fb[B2_FB_JOBEND];
        g_twArmed = 0;
        call();
        g_twArmed = 1;
        assert(gameCalls == game0 + 4 && k.b2Fb[B2_FB_JOBEND] == end0 + 1 && out[0] == 0x77);
        // and the same world answers once the state is clean again
        const LONG taken0 = k.b2Taken;
        call();
        assert(gameCalls == game0 + 4 && k.b2Taken == taken0 + 1 && out[0] == 6 && out[2] == 0x600 && out[3] == 0 && out[1] == 0xEEEEEEEE);
    }
    // shadow with a skewed game answer: one mismatch, the game's answer returned
    {
        k.b2Mode = 0, k.b2Shadow = 1, g_async.state = A_IDLE, g_twArmed = 0;
        expCode = 7, expLo = 0, expHi = 0;
        const LONG mm0 = k.b2ShadowMismatch, runs0 = k.b2ShadowRuns;
        call();
        assert(k.b2ShadowMismatch == mm0 + 1 && k.b2ShadowRuns == runs0 + 1 && out[0] == 7 && out[2] == 0 && !g_rgOffSimOpen);
        k.b2ShadowMismatch = mm0;
    }
    assert(rto::torn == 0 && !t_qlShared && !t_qlDepth && !g_rgOffSimOpen);
    k.b2Ready = 1;
    B2LogCounters();
    printf("Blocker2dRO ([Blocker2dRO] Enabled=1): %d hand-derived cases x (Mode 1 job, shadow) equal incl. byte arguments, ignore ids, "
           "merge, physics tail on / off and 5 give-backs; switches off / other thread / no job; may not, QL busy, tripwire, job end; "
           "shadow skew counted; out[1] and refcounts untouched\n", nCases);

    memset(&k, 0, sizeof(k));
    g_async = savedAsync;
    g_twArmed = savedArmed, g_twActive = savedActive, kc.rgOwnerTid = savedOwner, g_steerBroken = savedBroken;
    g_srWorldGet = savedWorld, g_srLotFind = savedFind, g_srActiveLot = savedAct, g_srOne = savedOne;
    g_srOptId = savedOptId, g_srOptTest = savedOptTest, g_srCheckSlots = savedCheck;
    g_b2Orig = savedOrig, g_b2DebugDraw = savedDbg;
    g_rtoComp = savedComp, g_rtoOptCopy = savedCopy, g_rtoOptSet = savedSet, g_rtoOptFree = savedFree;
    g_rtoSimList = savedList, g_rtoPortal = savedPortal, g_rtoGather = savedGather;
    g_rtoKeyTc = savedKeyTc, g_rtoKeySlot = savedKeySlot, g_rtoSegR = savedSegR;
}
