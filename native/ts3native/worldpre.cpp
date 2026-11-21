// docs/676: [WorldPrecompute]. Included by ts3native.cpp after workerhold.cpp (waterjps.cpp's WaterJumpHook and plancand.cpp's
// frames call into it through forward declarations).
//
// 1. WaterScanTable (0 off, 1 use, 2 shadow): the water plan's search 0x620560 is a jump-point search on two byte grids of
//    the routing world (a passability map and a high-cost map, 4 m cells). Its jump walks straight rays cell by cell, and a
//    diagonal jump walks two such rays from every cell it passes: in open sea that is the whole cost. Where a ray stops
//    depends on the two grids only, apart from the goal cell and the start / goal boxes of the search's callback. So per
//    (passability map, high-cost map) pair we keep, for every cell and straight direction, how far the ray runs and how it
//    ends (waterjps-core.h); the jump then answers a ray with one read plus arithmetic for the goal and the boxes. Results
//    are identical to the walk (and so to the game's jump).
//    Invalidation needs no writer hook: at the start of every search the two live grids are compared byte by byte with our
//    copies (two memcmp passes over w * h bytes); changed cells update the table in place, many changes rebuild it. The
//    search's one call site is retargeted to do that; a jump takes the table only inside such a search, on the same thread,
//    for the same grids. One SRW lock guards the tables; a second thread's search that finds it taken walks as before.
// 2. Probe (0 off, 1 count): what a memo or a table of world-level legs could hit. Per water search and per outermost world
//    A* search: has the same query been asked before in this session, and had the data it reads changed in between?
//    Water key: start cell, goal cell, both maps. World keys: A = start waypoint, goal waypoint, cost mode, planner flags,
//    route option class; B = A plus the two lot hubs and the exact start and goal points (what the search really reads from
//    its context). "Unchanged" = our own grid compare for water (with the table on), [RouteSnap]'s write sequence else.
//    Plus the post pass 0x64E380 split by its 19 node passes (call sites retargeted to a timing stub; a pass keeps every
//    register it was given). Observe only.
// Runtime switch: export ts3native_worldpre +0x00 (native_set waterscantable); +0x04 is the probe (start value from the ini).
// Nothing is written to a save or a file; all state is rebuilt from the live grids.
#include "worldpre-core.h"

// exported block, not in the ts3fixes slot range (docs/registry.md "native exports")
enum { WX_POSTN = 19, WX_SLOTS = 4, WX_MAXCELLS = 1 << 20, WX_P3SITES = 12, WX_P3CATS = 8 };
struct WorldPreCounters {
    volatile LONG wxScanMode;        // +0x00 runtime switch: 0 off, 1 the jump's straight rays come from the table, 2 shadow
    volatile LONG wxProbe;           // +0x04 runtime switch: 1 = count
    volatile LONG wxReady;           // +0x08 bit 0 search call site, 1 [WaterJps] jump hook, 2 world frames (CandidateSplit), 3 post pass sites
    volatile LONG wxSlots;           // +0x0C tables kept at most
    volatile LONG wxSearches;        // +0x10 water searches seen with wxScanMode != 0
    volatile LONG wxTabSearches;     // +0x14 of them: ran with a table
    volatile LONG wxBusy;            // +0x18 not: another thread's search held the tables
    volatile LONG wxNoSlot;          // +0x1C not: grid too large or no memory
    volatile LONG wxScope;           // +0x20 not: router scope (household / selected / unknown) with WaterScanAll=0
    volatile LONG wxClean;           // +0x24 refresh: both grids as at the last search
    volatile LONG wxInc;             // +0x28 refresh: changed cells updated in place
    volatile LONG wxIncCells;        // +0x2C
    volatile LONG wxFull;            // +0x30 refresh: table built from scratch
    volatile LONG wxShadow;          // +0x34 jumps computed both ways (mode 2)
    volatile LONG wxMismatch;        // +0x38 must stay 0
    volatile LONG wxJumpNoTab;       // +0x3C jumps of a table search that walked: another callback, other grids
    volatile LONG wxMemKb;           // +0x40
    volatile LONG wxLines;           // +0x44 mismatch lines written
    volatile LONGLONG wxRefreshUs;   // +0x48 compare, update and build time
    volatile LONGLONG wxTabJumps;    // +0x50 jumps that had the table
    volatile LONGLONG wxTabScans;    // +0x58 straight rays answered from it
    volatile LONGLONG wxTabSlow;     // +0x60 straight rays walked because the entry was "unknown" (run too long for an entry)
    // probe: water searches
    volatile LONG wxWaterN;          // +0x68
    volatile LONG wxWaterFound;      // +0x6C
    volatile LONG wxWaterRep;        // +0x70 same key as an earlier search
    volatile LONG wxWaterSame;       // +0x74 ... and nothing changed since that key's last search
    volatile LONGLONG wxWaterUs;     // +0x78
    volatile LONGLONG wxWaterRepUs;  // +0x80
    volatile LONGLONG wxWaterSameUs; // +0x88
    // probe: outermost world A* searches; [0] key A, [1] key B
    volatile LONG wxWorldN;          // +0x90
    volatile LONG wxWorldFail;       // +0x94
    volatile LONG wxWorldRep[2];     // +0x98
    volatile LONG wxWorldSame[2];    // +0xA0
    volatile LONGLONG wxWorldUs;     // +0xA8
    volatile LONGLONG wxWorldFailUs; // +0xB0
    volatile LONGLONG wxWorldRepUs[2];   // +0xB8
    volatile LONGLONG wxWorldSameUs[2];  // +0xC8
    volatile LONG wxDistinct[3];     // +0xD8 keys kept: water, world A, world B
    volatile LONG wxRepFull[3];      // +0xE4 sightings not kept (table full)
    // probe: the post pass by node pass
    volatile LONG wxPostN[WX_POSTN];
    volatile LONGLONG wxPostUs[WX_POSTN];
    // probe: world searches by kind (appended)
    volatile LONG wxWorldWaterN;     // water links allowed (planner flag 0x80)
    volatile LONG wxWorldCrossN;     // start and goal waypoint in different land components (the game's colours)
    volatile LONGLONG wxWorldWaterUs;
    volatile LONGLONG wxWorldCrossUs;
    // scan table builds by cause (appended; wxFull is their sum plus the first build of a slot)
    volatile LONG wxFullEvict;       // the pair's table had been replaced by another pair's (more pairs in use than WaterScanSlots)
    volatile LONG wxFullOver;        // more than WS_INCMAX cells changed since the pair's last search
    volatile LONG wxIncMax;          // most cells updated in place in one refresh
    volatile LONG wxPad;
    // probe: inside pass 3 (0x64DD40), by callee (appended; docs/676 §13). Times include the callees below each one.
    volatile LONG wxP3N[WX_P3CATS];      // 0 nudge 642420, 1 corners 644630, 2 segment test 644560, 3 its lot walk 644080,
                                         // 4 next lot C69790, 5 world test 658A00, 6 lot test 643E60, 7 sample predicate B87D30
    volatile LONG wxP3Flag72;            // lot walks (cat 3) whose smoother has byte +0x72 set
    volatile LONG wxP3Pad;
    volatile LONGLONG wxP3Us[WX_P3CATS];
};
extern "C" __declspec(dllexport) WorldPreCounters ts3native_worldpre = {};

static LONGLONG WxNowQ() {
    LARGE_INTEGER q;
    QueryPerformanceCounter(&q);
    return q.QuadPart;
}
static LONGLONG WxUs(LONGLONG ticks) { return g_qpf.QuadPart ? ticks * 1000000 / g_qpf.QuadPart : 0; }

// ---- 1. the scan tables ----
struct WxSlot {
    WsSlot s;
    u8* mem;      // one block: copy of g1, copy of g2, the table
    DWORD cells;  // what mem was sized for
    DWORD use;    // g_wxUse at its last search
};
static WxSlot g_wxSlot[WX_SLOTS];
static DWORD g_wxUse;
static SRWLOCK g_wxLock = SRWLOCK_INIT;  // the slots; held by a search for its whole run
static volatile LONG g_wxGridGen;        // moved by every refresh that found a change (the probe's "unchanged" for water)
static bool g_wxAll;                     // WaterScanAll=1 (or HouseholdToo): every router's searches take the table

struct WxTh {
    const unsigned short* tab;  // the running search's table, NULL outside one
    const u8* g1;
    const u8* g2;
    DWORD w, h;
    LONG mode;
    bool held;                  // this thread holds g_wxLock (also after an unwind out of a search: the next search keeps it)
};
static __declspec(thread) WxTh t_wx;

static void* (*g_wxAlloc)(size_t) = NULL;  // selftest: an allocator that can fail
static u8* WxMem(size_t bytes) {
    if (g_wxAlloc) return (u8*)g_wxAlloc(bytes);
#ifdef WORLDPRE_HOST_TEST
    return NULL;
#else
    return (u8*)VirtualAlloc(NULL, bytes, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
#endif
}
static void WxMemFree(u8* p) {
#ifndef WORLDPRE_HOST_TEST
    if (p && !g_wxAlloc) VirtualFree(p, 0, MEM_RELEASE);
#else
    (void)p;
#endif
}

// The slot for these grids: the one built from them, else a free one, else the least recently used. Caller holds g_wxLock.
static WxSlot* WxSlotFor(const u8* g1, const u8* g2, DWORD w, DWORD h) {
    WorldPreCounters& k = ts3native_worldpre;
    const int n = Clamp(k.wxSlots, 1, WX_SLOTS);
    const DWORD cells = w * h;
    WxSlot* pick = NULL;
    for (int i = 0; i < n; i++) {
        WxSlot& p = g_wxSlot[i];
        if (p.mem && p.s.g1 == g1 && p.s.g2 == g2 && p.s.w == w && p.s.h == h) return p.use = ++g_wxUse, &p;
        if (!pick || (!p.mem && pick->mem) || (!p.mem == !pick->mem && p.use < pick->use)) pick = &p;
    }
    if (!pick) return NULL;
    if (pick->cells != cells || !pick->mem) {
        WxMemFree(pick->mem);
        pick->mem = WxMem((size_t)cells * 10);
        pick->cells = pick->mem ? cells : 0;
        LONG kb = 0;
        for (int i = 0; i < WX_SLOTS; i++) kb += (LONG)(g_wxSlot[i].cells / 1024 * 10);
        k.wxMemKb = kb;
        if (!pick->mem) return NULL;
    }
    if (pick->s.built) InterlockedIncrement(&k.wxFullEvict);  // it held another pair's table
    pick->s.g1 = g1, pick->s.g2 = g2, pick->s.w = w, pick->s.h = h;
    pick->s.c1 = pick->mem, pick->s.c2 = pick->mem + cells, pick->s.tab = (unsigned short*)(pick->mem + 2 * (size_t)cells);
    pick->s.built = false;
    pick->use = ++g_wxUse;
    return pick;
}

static LONG (*g_wxScope)(bool*) = PccScopeNow;  // plancand.cpp: the sim session's or the worker job's router scope (a test swaps it)
static bool WxScopeOk() {
    if (g_wxAll) return true;
    bool worker = false;
    const LONG s = g_wxScope(&worker);
    return s == 2 || s == 3;
}

// Start of a water search on this thread: true = t_wx holds a table that matches the two grids as they are now.
static bool WxTableBegin(const u8* g1, const u8* g2, DWORD w, DWORD h, LONG mode) {
    WorldPreCounters& k = ts3native_worldpre;
    WxTh& t = t_wx;
    t.tab = NULL;
    InterlockedIncrement(&k.wxSearches);
    if (!g1 || !g2 || w < 2 || h < 1 || w > 0x8000 || h > 0x8000 || (unsigned long long)w * h > (unsigned long long)WX_MAXCELLS) {
        InterlockedIncrement(&k.wxNoSlot);
        return false;
    }
    if (!WxScopeOk()) {
        InterlockedIncrement(&k.wxScope);
        return false;
    }
    if (!t.held) {
        if (!TryAcquireSRWLockExclusive(&g_wxLock)) {
            InterlockedIncrement(&k.wxBusy);
            return false;
        }
        t.held = true;
    }
    WxSlot* const p = WxSlotFor(g1, g2, w, h);
    if (!p) {
        InterlockedIncrement(&k.wxNoSlot);
        t.held = false;
        ReleaseSRWLockExclusive(&g_wxLock);
        return false;
    }
    const LONGLONG q0 = WxNowQ();
    DWORD changed = 0;
    const int what = WsRefresh(&p->s, WS_INCMAX, &changed);
    InterlockedExchangeAdd64(&k.wxRefreshUs, WxUs(WxNowQ() - q0));
    if (what == WS_CLEAN) InterlockedIncrement(&k.wxClean);
    else {
        InterlockedIncrement(&g_wxGridGen);
        if (what == WS_FULL || what == WS_FULLOVER) {
            InterlockedIncrement(&k.wxFull);
            if (what == WS_FULLOVER) InterlockedIncrement(&k.wxFullOver);
        } else {
            InterlockedIncrement(&k.wxInc), InterlockedExchangeAdd(&k.wxIncCells, (LONG)changed);
            if ((LONG)changed > k.wxIncMax) k.wxIncMax = (LONG)changed;  // under g_wxLock
        }
    }
    t.tab = p->s.tab, t.g1 = g1, t.g2 = g2, t.w = w, t.h = h, t.mode = mode;
    InterlockedIncrement(&k.wxTabSearches);
    return true;
}
static void WxTableEnd() {
    WxTh& t = t_wx;
    t.tab = NULL;
    if (!t.held) return;
    t.held = false;
    ReleaseSRWLockExclusive(&g_wxLock);
}

// WaterJumpHook, after it built its context: 0 = walk; 1 = c.tab set; 2 = c.tab set, and the hook also walks and compares.
static int WxJumpTab(WjCtx& c, DWORD goal) {
    const WxTh& t = t_wx;
    if (!t.tab) return 0;
    if (t.g1 != c.g1 || t.g2 != c.g2 || t.w != c.w || t.h != c.h || !WjTabOk(c)) {
        InterlockedIncrement(&ts3native_worldpre.wxJumpNoTab);
        return 0;
    }
    c.tab = t.tab;
    if (goal < c.w * c.h) c.goalX = goal % c.w, c.goalY = goal / c.w;
    return t.mode == 2 ? 2 : 1;
}
static void WxJumpDone(const WjCtx& c) {
    WorldPreCounters& k = ts3native_worldpre;
    InterlockedIncrement64(&k.wxTabJumps);
    if (c.tabScans) InterlockedExchangeAdd64(&k.wxTabScans, c.tabScans);
    if (c.tabSlow) InterlockedExchangeAdd64(&k.wxTabSlow, c.tabSlow);
}
static void WxShadowNote(DWORD walk, DWORD tab, const WjCtx& c, DWORD goal, DWORD cell, DWORD dir, DWORD flag) {
    WorldPreCounters& k = ts3native_worldpre;
    InterlockedIncrement(&k.wxShadow);
    if (walk == tab) return;
    InterlockedIncrement(&k.wxMismatch);
    if (InterlockedIncrement(&k.wxLines) <= 10)
        Log("WATER SCAN TABLE MISMATCH: w %lu h %lu goal %lu cell %lu dir %lu flag %lu boxes %d,%d %d,%d -> table %lu, walk %lu (the walk's result is used)",
            c.w, c.h, goal, cell, dir, flag & 0xFF, c.o[4], c.o[5], c.o[6], c.o[7], tab, walk);
}

// ---- 2. the probe ----
static SRWLOCK g_wxRepLock = SRWLOCK_INIT;
static WxRep g_wxRep[3];  // water, world key A, world key B

static int WxRepNoteLocked(int which, unsigned long long key, DWORD gen) {
    WorldPreCounters& k = ts3native_worldpre;
    AcquireSRWLockExclusive(&g_wxRepLock);
    const int r = WxRepNote(&g_wxRep[which], key, gen);
    k.wxDistinct[which] = (LONG)g_wxRep[which].used, k.wxRepFull[which] = (LONG)g_wxRep[which].full;
    ReleaseSRWLockExclusive(&g_wxRepLock);
    return r;
}

// the water search's call in WaterPath_Plan (0x671AFD: its only caller), cdecl, 9 arguments
typedef DWORD(__cdecl* WxSearchFn)(const u8* const* g1, const u8* const* g2, DWORD w, DWORD h, DWORD scale, const DWORD* start, const DWORD* goal,
                                   void* out, void* cb);
static WxSearchFn g_wxSearchOrig;

static void WxWaterNote(const u8* g1, const u8* g2, DWORD w, const DWORD* start, const DWORD* goal, bool tabOn, DWORD r, LONGLONG us) {
    WorldPreCounters& k = ts3native_worldpre;
    InterlockedIncrement(&k.wxWaterN);
    InterlockedExchangeAdd64(&k.wxWaterUs, us);
    if (r & 0xFF) InterlockedIncrement(&k.wxWaterFound);
    unsigned long long key = WxMix(671930, (uintptr_t)g1);
    key = WxMix(key, (uintptr_t)g2);
    key = WxMix(key, (unsigned long long)start[1] * w + start[0]);
    key = WxMix(key, (unsigned long long)goal[1] * w + goal[0]);
    // with a table the grid compare just ran: g_wxGridGen moves exactly when one of the two grids changed
    // (the two counters never compare equal: bit 31 marks the write sequence; with neither, "seen before" only)
    const DWORD gen = tabOn ? (DWORD)g_wxGridGen & 0x7FFFFFFF : 0x80000000 | (DWORD)g_snWriteSeq;
    int seen = WxRepNoteLocked(0, key, gen);
    if (seen == 2 && !tabOn && !g_snOn) seen = 1;
    if (seen >= 1) InterlockedIncrement(&k.wxWaterRep), InterlockedExchangeAdd64(&k.wxWaterRepUs, us);
    if (seen == 2) InterlockedIncrement(&k.wxWaterSame), InterlockedExchangeAdd64(&k.wxWaterSameUs, us);
}

static DWORD __cdecl WxSearchHook(const u8* const* g1, const u8* const* g2, DWORD w, DWORD h, DWORD scale, const DWORD* start, const DWORD* goal,
                                  void* out, void* cb) {
    WorldPreCounters& k = ts3native_worldpre;
    const LONG mode = k.wxScanMode, probe = k.wxProbe;
    if (mode != 1 && mode != 2 && probe != 1) {
        WxTableEnd();  // also gives back the lock an unwind out of an earlier search left with this thread
        return g_wxSearchOrig(g1, g2, w, h, scale, start, goal, out, cb);
    }
    const bool tabOn = (mode == 1 || mode == 2) && g1 && g2 && WxTableBegin(*g1, *g2, w, h, mode);
    const LONGLONG q0 = probe == 1 ? WxNowQ() : 0;
    const DWORD r = g_wxSearchOrig(g1, g2, w, h, scale, start, goal, out, cb);
    if (probe == 1 && g1 && g2 && start && goal) WxWaterNote(*g1, *g2, w, start, goal, tabOn, r, WxUs(WxNowQ() - q0));
    WxTableEnd();
    return r;
}

// plancand.cpp's world A* frame (0x676260; it calls itself once for the circle pass): the outermost call is one search
struct WxWorldTh {
    const void* mark;  // the outermost frame, NULL = none open
    unsigned long long key[2];
    LONGLONG q0;
    bool water, cross;
};
static __declspec(thread) WxWorldTh t_wxWorld;

static void WxWorldEnter(const void* mark, const u8* graph, const DWORD* start, const DWORD* goal, DWORD mode, const DWORD* opts) {
    if (ts3native_worldpre.wxProbe != 1) return;
    WxWorldTh& t = t_wxWorld;
    if (t.mark && t.mark > mark) return;  // inside an open search (the stack grows down); a mark at or below ours was unwound
    if (!start || !goal || !opts) { t.mark = NULL; return; }
    // opts (docs/99 §2.3): [0] planner flags, [1] start lot hub, [2] [3] start x z, [0xD] goal lot hub, [0xE] [0xF] goal x z,
    // [0x23] the job's parameter block (first dword: the route option word)
    const DWORD* const job = (const DWORD*)(uintptr_t)opts[0x23];
    unsigned long long a = WxMix(676260, *start);
    a = WxMix(a, *goal);
    a = WxMix(a, ((unsigned long long)mode << 32) | opts[0]);
    a = WxMix(a, job ? *job & 0xCF00 : 0xFFFFFFFF);
    unsigned long long b = WxMix(a, ((unsigned long long)opts[1] << 32) | opts[0xD]);
    b = WxMix(b, ((unsigned long long)opts[2] << 32) | opts[3]);
    b = WxMix(b, ((unsigned long long)opts[0xE] << 32) | opts[0xF]);
    const int cs = WxLandColour(graph, *start), cg = WxLandColour(graph, *goal);
    t.water = (opts[0] & 0x80) != 0, t.cross = cs != -3 && cg != -3 && cs != cg;
    t.mark = mark, t.key[0] = a, t.key[1] = b, t.q0 = WxNowQ();
}
static void WxWorldLeave(const void* mark, DWORD r) {
    WxWorldTh& t = t_wxWorld;
    if (t.mark != mark) return;
    t.mark = NULL;
    WorldPreCounters& k = ts3native_worldpre;
    const LONGLONG us = WxUs(WxNowQ() - t.q0);
    InterlockedIncrement(&k.wxWorldN);
    InterlockedExchangeAdd64(&k.wxWorldUs, us);
    if (!r) InterlockedIncrement(&k.wxWorldFail), InterlockedExchangeAdd64(&k.wxWorldFailUs, us);
    if (t.water) InterlockedIncrement(&k.wxWorldWaterN), InterlockedExchangeAdd64(&k.wxWorldWaterUs, us);
    if (t.cross) InterlockedIncrement(&k.wxWorldCrossN), InterlockedExchangeAdd64(&k.wxWorldCrossUs, us);
    const DWORD gen = (DWORD)g_snWriteSeq;
    for (int i = 0; i < 2; i++) {
        int seen = WxRepNoteLocked(1 + i, t.key[i], gen);
        if (seen == 2 && !g_snOn) seen = 1;  // no write sequence: "seen before" only
        if (seen >= 1) InterlockedIncrement(&k.wxWorldRep[i]), InterlockedExchangeAdd64(&k.wxWorldRepUs[i], us);
        if (seen == 2) InterlockedIncrement(&k.wxWorldSame[i]), InterlockedExchangeAdd64(&k.wxWorldSameUs[i], us);
    }
}

// the post pass 0x64E380: its 19 node passes, by call site (offset of the E8 from the function's start, Steam 1.67 layout;
// the two sea-height reads between them are not passes). The targets are read from the calls at install, so a site another
// feature retargeted first ([RouteAsync] TailUnlock: passes 16-19) chains into that feature's hook.
static const unsigned short kWxPostOff[WX_POSTN] = {0x26, 0x3C, 0x48, 0x54, 0x73, 0x8A, 0x96, 0xAF, 0xC0, 0xE0, 0xF0, 0x120, 0x130, 0x140, 0x158, 0x16D, 0x18B, 0x1A3, 0x1B3};
static const char* const kWxPostName[WX_POSTN] = {"1 644FD0", "2 641BF0", "3 64DD40 smooth", "4 6426B0", "5 64C170", "6 645310", "7 642880",
                                                  "8 643210", "9 647EB0", "10 64B430", "11 64BBD0", "12 648BE0", "13 6495E0", "14 649890",
                                                  "15 64A950", "16 64C7C0 heights", "17 64D5B0", "18 64A690", "19 641DF0"};
// call sites inside pass 3 and its callees, timed only while a pass 3 call of this thread is open. base: 0 pass 3 itself,
// 1 the segment test (target of the call at pass 3 + 0x426), 2 its lot walk (target of the call at the segment test +
// 0xB8), 3 the cursor advance (target of the call at pass 3 + 0x3AE). Offsets: Steam 1.67.
struct WxP3Site { unsigned char base; unsigned short off; unsigned char cat; };
static const WxP3Site kWxP3Site[WX_P3SITES] = {{0, 0x29E, 0}, {0, 0x2D8, 1}, {0, 0x426, 2}, {0, 0x53F, 2}, {1, 0xB8, 3},  {2, 0x72, 4},
                                               {2, 0xE8, 5},  {2, 0x308, 5}, {2, 0x189, 6}, {2, 0x26C, 6}, {2, 0x2A7, 6}, {3, 0x83, 7}};
static const char* const kWxP3Name[WX_P3CATS] = {"nudge 642420", "corners 644630", "segment test 644560", "lot walk 644080",
                                                 "next lot C69790", "world test 658A00", "lot test 643E60", "sample predicate B87D30"};
static void* g_wxPostTarget[WX_POSTN + WX_P3SITES];
static void* g_wxPostExitPtr;
struct WxPostTh {
    const DWORD* sp[4];  // where the pass's return address lies on the stack
    DWORD ret[4];
    LONGLONG q0[4];
    int site[4];
    int n;
};
static __declspec(thread) WxPostTh t_wxPost;

// A pass is about to run; sp = the slot of its return address. 1 = time this call: the stub then lets the pass return
// through WxPostExit. Entries whose slot is at or below sp belong to frames an unwind left behind (the stack grows down, so
// a pass that is still running lies above): dropped. Passes may nest (a pass that plans again); past four deep: untimed.
static DWORD __cdecl WxPostEnter(DWORD site, const DWORD* sp) {
    if (ts3native_worldpre.wxProbe != 1 || site >= (DWORD)(WX_POSTN + WX_P3SITES)) return 0;
    WxPostTh& t = t_wxPost;
    while (t.n > 0 && t.sp[t.n - 1] <= sp) t.n--;
    if (t.n < 0 || t.n >= 4) return 0;
    if (site >= (DWORD)WX_POSTN) {  // a site inside pass 3's callees: other passes reach the same code, count pass 3 only
        bool in3 = false;
        for (int i = 0; i < t.n && !in3; i++) in3 = t.site[i] == 2;
        if (!in3) return 0;
        if (kWxP3Site[site - WX_POSTN].cat == 3) {
            // thiscall: ECX = the smoother, three slots below the return address in the stub's pushad frame. The game
            // reads [ecx + 0x71] itself in this call.
            const u8* const smoother = (const u8*)(uintptr_t)sp[-3];
            if (smoother && smoother[0x72]) InterlockedIncrement(&ts3native_worldpre.wxP3Flag72);
        }
    }
    t.sp[t.n] = sp, t.ret[t.n] = *sp, t.site[t.n] = (int)site, t.q0[t.n] = WxNowQ();
    t.n++;
    return 1;
}
// the pass returned: the address it was called from. sp = the stack pointer the exit stub was entered with (just above the
// returning call's return slot and whatever its `ret n` popped). Entries that lie deeper than the returning one were left by
// an unwind inside it that no later entry dropped (an exception caught within the pass): they are dropped here, else the
// exit would pop one of them and return to its dead address.
static DWORD __cdecl WxPostLeave(const DWORD* sp) {
    WxPostTh& t = t_wxPost;
    while (t.n > 1 && t.sp[t.n - 2] < sp) t.n--;
    if (t.n <= 0) return 0;  // cannot happen: an exit runs only for an entry of this thread that is still on the stack
    t.n--;
    WorldPreCounters& k = ts3native_worldpre;
    const LONGLONG us = WxUs(WxNowQ() - t.q0[t.n]);
    if (t.site[t.n] < WX_POSTN) {
        InterlockedIncrement(&k.wxPostN[t.site[t.n]]);
        InterlockedExchangeAdd64(&k.wxPostUs[t.site[t.n]], us);
    } else {
        const int cat = kWxP3Site[t.site[t.n] - WX_POSTN].cat;
        InterlockedIncrement(&k.wxP3N[cat]);
        InterlockedExchangeAdd64(&k.wxP3Us[cat], us);
    }
    return t.ret[t.n];
}
#ifndef WORLDPRE_HOST_TEST
static __declspec(naked) void WxPostExit() {
    __asm {
        push 0                      // room for the return address
        pushfd
        pushad
        lea eax, [esp + 40]         // the stack pointer this exit was entered with
        push eax
        call WxPostLeave
        add esp, 4
        mov dword ptr [esp + 36], eax
        popad
        popfd
        ret
    }
}
// every register and the flags reach the pass as the game set them; [esp + 36] after pushfd + pushad = the return address
#define WX_POST_STUB(i) static __declspec(naked) void WxPostStub##i() { __asm pushfd __asm pushad __asm lea eax, [esp + 36] \
    __asm push eax __asm push i __asm call WxPostEnter __asm add esp, 8 __asm test eax, eax __asm jz pass##i \
    __asm mov eax, g_wxPostExitPtr __asm mov dword ptr [esp + 36], eax __asm pass##i: __asm popad __asm popfd \
    __asm jmp dword ptr [g_wxPostTarget + i * 4] }
WX_POST_STUB(0) WX_POST_STUB(1) WX_POST_STUB(2) WX_POST_STUB(3) WX_POST_STUB(4) WX_POST_STUB(5) WX_POST_STUB(6) WX_POST_STUB(7)
WX_POST_STUB(8) WX_POST_STUB(9) WX_POST_STUB(10) WX_POST_STUB(11) WX_POST_STUB(12) WX_POST_STUB(13) WX_POST_STUB(14) WX_POST_STUB(15)
WX_POST_STUB(16) WX_POST_STUB(17) WX_POST_STUB(18)
WX_POST_STUB(19) WX_POST_STUB(20) WX_POST_STUB(21) WX_POST_STUB(22) WX_POST_STUB(23) WX_POST_STUB(24)
WX_POST_STUB(25) WX_POST_STUB(26) WX_POST_STUB(27) WX_POST_STUB(28) WX_POST_STUB(29) WX_POST_STUB(30)
static void (*const kWxP3Stubs[WX_P3SITES])() = {WxPostStub19, WxPostStub20, WxPostStub21, WxPostStub22, WxPostStub23, WxPostStub24,
                                                 WxPostStub25, WxPostStub26, WxPostStub27, WxPostStub28, WxPostStub29, WxPostStub30};
static void (*const kWxPostStubs[WX_POSTN])() = {WxPostStub0,  WxPostStub1,  WxPostStub2,  WxPostStub3,  WxPostStub4,  WxPostStub5,  WxPostStub6,
                                                 WxPostStub7,  WxPostStub8,  WxPostStub9,  WxPostStub10, WxPostStub11, WxPostStub12, WxPostStub13,
                                                 WxPostStub14, WxPostStub15, WxPostStub16, WxPostStub17, WxPostStub18};

static const DWORD WX_SEARCH_SITE = TS3A(0x00671930) + 0x1CD;  // WaterPath_Plan's call of the search (0x671AFD)
static const DWORD WX_POST = TS3A(0x0064E380);

static bool WxCallAt(DWORD site, DWORD* target) {
    const u8* p = (const u8*)(uintptr_t)site;
    if (!Readable(p, 5) || p[0] != 0xE8) return false;
    *target = (DWORD)(site + 5 + *(const LONG*)(p + 1));
    return true;
}
static bool WxRetarget(DWORD site, void* hook) {
    const LONG rel = (LONG)((uintptr_t)hook - (site + 5));
    return Write((u8*)(uintptr_t)site + 1, &rel, 4);
}

// The callees of pass 3 (docs/676 §13). p3 = pass 3's address as read from its call. Every site must be an E8, and two
// sites agree on their target exactly when they are the same callee; else nothing is written. No address row: all sites
// are offsets from p3 and from targets read here.
static void InstallWxPass3(DWORD p3) {
    DWORD base[4] = {p3, 0, 0, 0};
    DWORD t[WX_P3SITES] = {};
    bool ok = WxCallAt(p3 + 0x426, &base[1]) && WxCallAt(base[1] + 0xB8, &base[2]) && WxCallAt(p3 + 0x3AE, &base[3]);
    for (int i = 0; i < WX_P3SITES && ok; i++) ok = WxCallAt(base[kWxP3Site[i].base] + kWxP3Site[i].off, &t[i]);
    for (int i = 1; i < WX_P3SITES && ok; i++)
        for (int j = 0; j < i && ok; j++) ok = (kWxP3Site[i].cat == kWxP3Site[j].cat) == (t[i] == t[j]);
    if (!ok) {
        Log("SKIP world precompute pass 3 split: the calls under 0x%08lX are not those of Steam 1.67, nothing written", p3);
        return;
    }
    for (int i = 0; i < WX_P3SITES; i++) g_wxPostTarget[WX_POSTN + i] = (void*)(uintptr_t)t[i];
    int done = 0;
    for (int i = 0; i < WX_P3SITES; i++) done += WxRetarget(base[kWxP3Site[i].base] + kWxP3Site[i].off, (void*)kWxP3Stubs[i]) ? 1 : 0;
    if (done == WX_P3SITES) ts3native_worldpre.wxReady |= 16;
    else Log("FAIL world precompute pass 3 split: %d of %d call sites written (each stub only times and calls its target)", done, (int)WX_P3SITES);
}

// [WorldPrecompute] (after ApplyWaterJps, InstallPlanCand and InstallTailUnlock)
static void InstallWorldPre(const wchar_t* ini) {
    InstallWorldAlt(ini);  // worldalt.cpp (docs/676 §14): its own key, independent of the two below
    WorldPreCounters& c = ts3native_worldpre;
    const int scan = Clamp(GetPrivateProfileIntW(L"WorldPrecompute", L"WaterScanTable", 0, ini), 0, 2);
    const int probe = GetPrivateProfileIntW(L"WorldPrecompute", L"Probe", 0, ini) ? 1 : 0;
    if (!scan && !probe) { Log("world precompute off ([WorldPrecompute] WaterScanTable=0, Probe=0)"); return; }
    // EA 1.69: both function addresses have table rows; the call sites inside them are offsets and are accepted only by
    // the checks below (an E8 whose target holds the jump call; 19 E8 calls), so another layout logs SKIP
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
    c.wxSlots = Clamp(GetPrivateProfileIntW(L"WorldPrecompute", L"WaterScanSlots", 4, ini), 1, WX_SLOTS);
    g_wxAll = GetPrivateProfileIntW(L"WorldPrecompute", L"WaterScanAll", 0, ini) != 0 || g_hhToo == 1;
    // the search's call site: an E8 whose target is the function that holds [WaterJps]'s jump call (0x6207FB in 0x620560)
    DWORD target = 0;
    if (!WxCallAt(WX_SEARCH_SITE, &target) || WJ_CALL < target || WJ_CALL - target > 0x800) {
        Log("SKIP world precompute water: 0x%08lX is not the water plan's call of its search, nothing written", WX_SEARCH_SITE);
    } else {
        g_wxSearchOrig = (WxSearchFn)(uintptr_t)target;
        if (WxRetarget(WX_SEARCH_SITE, (void*)WxSearchHook)) c.wxReady |= 1;
        else Log("FAIL world precompute water: VirtualProtect at 0x%08lX (err %lu)", WX_SEARCH_SITE, GetLastError());
    }
    if (ts3native_counters.waterJpsReady) c.wxReady |= 2;
    if (g_pccSplit) c.wxReady |= 4;
    if (probe) {
        DWORD t[WX_POSTN] = {};
        bool ok = true;
        for (int i = 0; i < WX_POSTN && ok; i++) ok = WxCallAt(WX_POST + kWxPostOff[i], &t[i]);
        if (!ok) Log("SKIP world precompute post pass split: 0x%08lX does not hold the 19 pass calls of Steam 1.67, nothing written", WX_POST);
        else {
            g_wxPostExitPtr = (void*)WxPostExit;
            for (int i = 0; i < WX_POSTN; i++) g_wxPostTarget[i] = (void*)(uintptr_t)t[i];
            int done = 0;
            for (int i = 0; i < WX_POSTN; i++) done += WxRetarget(WX_POST + kWxPostOff[i], (void*)kWxPostStubs[i]) ? 1 : 0;
            if (done == WX_POSTN) c.wxReady |= 8;
            else Log("FAIL world precompute post pass split: %d of %d call sites written (each stub only times and calls its pass)", done, (int)WX_POSTN);
            if (done == WX_POSTN) InstallWxPass3(t[2]);
        }
    }
    const bool scanOk = scan && (c.wxReady & 3) == 3;
    if (scan && !scanOk) Log("SKIP world precompute water scan table: needs the search call site and [WaterJps] Enabled=1 (ready 0x%lX)", c.wxReady);
    c.wxScanMode = scanOk ? scan : 0;
    c.wxProbe = probe;
    Log("OK   world precompute: ready 0x%lX; WaterScanTable=%ld (%s, %s, at most %ld tables), Probe=%d (water searches%s%s; unchanged = %s)",
        c.wxReady, c.wxScanMode, c.wxScanMode == 2 ? "shadow: the walk's result is used" : c.wxScanMode ? "straight rays from the table" : "off",
        g_wxAll ? "every router" : "NPC routers only", c.wxSlots, probe, (c.wxReady & 4) ? ", world A* searches" : "; world A* needs CandidateSplit=1",
        (c.wxReady & 16) ? ", post pass by node pass and pass 3 by callee" : (c.wxReady & 8) ? ", post pass by node pass" : "", g_snOn ? "[RouteSnap] write sequence, our grid compare for water with the table on" : "[RouteSnap] counting is off: only water with the table on");
}

// the 60 s counter lines (ts3native.cpp MaybeLogCounters)
static void WxLogCounters() {
    const WorldPreCounters& c = ts3native_worldpre;
    if (!c.wxReady) return;
    if (c.wxScanMode || c.wxSearches)
        Log("world precompute: water scan table mode %ld ready 0x%lX: searches %ld (with a table %ld; not: busy %ld, no table %ld, scope %ld); "
            "grids at a search: unchanged %ld, cells updated %ld in %ld refreshes (most %ld), built %ld (pair replaced %ld, too many changes %ld); compare + update %ld ms; jumps with the table %ld "
            "(walked instead %ld), straight rays from it %ld, walked (run too long) %ld; shadow checks %ld mismatch %ld; %ld KB",
            c.wxScanMode, c.wxReady, c.wxSearches, c.wxTabSearches, c.wxBusy, c.wxNoSlot, c.wxScope, c.wxClean, c.wxIncCells, c.wxInc, c.wxIncMax, c.wxFull, c.wxFullEvict, c.wxFullOver,
            (LONG)(c.wxRefreshUs / 1000), (LONG)c.wxTabJumps, c.wxJumpNoTab, (LONG)c.wxTabScans, (LONG)c.wxTabSlow, c.wxShadow, c.wxMismatch,
            c.wxMemKb);
    if (!c.wxProbe && !c.wxWaterN && !c.wxWorldN) return;
    Log("world precompute probe water: searches %ld (%ld ms), found %ld; same start cell, goal cell and maps as an earlier search %ld (%ld ms), "
        "of them nothing changed since %ld (%ld ms); distinct %ld (not kept %ld)",
        c.wxWaterN, (LONG)(c.wxWaterUs / 1000), c.wxWaterFound, c.wxWaterRep, (LONG)(c.wxWaterRepUs / 1000), c.wxWaterSame,
        (LONG)(c.wxWaterSameUs / 1000), c.wxDistinct[0], c.wxRepFull[0]);
    Log("world precompute probe world kinds: water links allowed %ld (%ld ms); start and goal waypoint in different land components %ld (%ld ms)",
        c.wxWorldWaterN, (LONG)(c.wxWorldWaterUs / 1000), c.wxWorldCrossN, (LONG)(c.wxWorldCrossUs / 1000));
    Log("world precompute probe world: searches %ld (%ld ms), failed %ld (%ld ms); key A (start waypoint, goal waypoint, mode, flags, option "
        "class) seen before %ld (%ld ms), no routing write since %ld (%ld ms), distinct %ld (not kept %ld); key B (A + lot hubs + exact "
        "points) seen before %ld (%ld ms), no routing write since %ld (%ld ms), distinct %ld (not kept %ld); write sequence %ld",
        c.wxWorldN, (LONG)(c.wxWorldUs / 1000), c.wxWorldFail, (LONG)(c.wxWorldFailUs / 1000), c.wxWorldRep[0], (LONG)(c.wxWorldRepUs[0] / 1000),
        c.wxWorldSame[0], (LONG)(c.wxWorldSameUs[0] / 1000), c.wxDistinct[1], c.wxRepFull[1], c.wxWorldRep[1], (LONG)(c.wxWorldRepUs[1] / 1000),
        c.wxWorldSame[1], (LONG)(c.wxWorldSameUs[1] / 1000), c.wxDistinct[2], c.wxRepFull[2], g_snWriteSeq);
    if (!(c.wxReady & 8)) return;
    char b[900];
    int at = 0;
    for (int i = 0; i < WX_POSTN && at < (int)sizeof(b); i++)
        at += sprintf_s(b + at, sizeof(b) - at, "%s%s %ld/%ld", i ? ", " : "", kWxPostName[i], (LONG)(c.wxPostUs[i] / 1000), c.wxPostN[i]);
    Log("world precompute probe post passes (ms/calls, pass and its Steam address): %s", b);
    if (!(c.wxReady & 16)) return;
    at = 0;
    for (int i = 0; i < WX_P3CATS && at < (int)sizeof(b); i++)
        at += sprintf_s(b + at, sizeof(b) - at, "%s%s %ld/%ld", i ? ", " : "", kWxP3Name[i], (LONG)(c.wxP3Us[i] / 1000), c.wxP3N[i]);
    Log("world precompute probe pass 3 callees (ms/calls, each includes the ones it calls; the probe's own stubs add time): %s; lot walks "
        "with smoother +0x72 set %ld",
        b, c.wxP3Flag72);
}
#endif
