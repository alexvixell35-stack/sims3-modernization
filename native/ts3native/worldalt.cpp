// docs/676 §14: [WorldPrecompute] WorldAlt. Included by ts3native.cpp before worldpre.cpp (whose install calls ours).
//
// The world A* 0x676260 is close to Dijkstra: its heuristic is the straight line at 1 per metre against costs of 2-13 per
// metre, and with water links allowed a crossing costs 2 x 10 x body cells, so the whole start island is popped first.
// WorldAlt keeps the game's search, costs, walls, heap and tie rule and raises only the heuristic part of a pushed node's
// f: to max(game's value, landmark bound), the bound being |d(L, node) - d(L, goal)| over a few landmarks L on a
// lower-bound copy of the waypoint graph with one node per water body (worldalt-core.h has the graph and why every search
// input can only raise a cost above it). Same path cost; which of several equally cheap paths is found can differ.
//
// Sites, all inside 0x676260 (offsets from its address, bytes checked, no address row):
//   +0x128 (0x676388)  the first heuristic call of a search loop, `call [esp+0x80]`: WaBeginThunk. The game's value goes
//                      back untouched (it picks the SHORT or LONG cost set). Here the search is set up: is the bound
//                      allowed for it, is the world still the one the tables were built from, one build step if not done.
//   +0x44D, +0x674, +0x8EA (0x6766AD, 0x6768D4, 0x676B4A)  the heuristic call for a generated node: WaHeurThunk. The
//                      game's value goes back untouched too, so its "nearest node reached" bookkeeping (the blocker query
//                      after a failed search) is the game's; the excess of the bound over it is kept for the push.
//   +0x4B9, +0x6DC, +0x91D (0x676719, 0x67693C, 0x676B7D)  the open-list push 0x6761F0 of that node: WaPush adds the
//                      excess to the node's f (+4) and calls the game's push.
//   +0xA1B (0x676C7B)  the call that turns the goal node into the path (0x676130): WaPathOut reads the goal's g (the path
//                      cost as the game summed it), the node sequence and the closed count, then calls the game's.
// The cost function is called through the pointer of the A*'s own frame, so [RouteAsync]'s budget stubs stay in the chain.
//
// No bound for a search (it runs as the game's): cost mode 2 or 4; a virtual (water) start or goal node, whose links cost
// 10 x a distance; planner flags 1 / 2 (exact start or goal point instead of the hub's); mode 3 with water links when the
// start or goal point lies on a water lot (its lot-bound links cost the small water-lot value); a router that is not
// known NPC scope unless WorldAltAll=1 or [Experimental] HouseholdToo=1; a world or tuning that cannot be read.
//
// Tables: per cost mode (1, 3) one row of WorldAltLandmarks 32-bit distances per node, built lazily: the first search loop
// builds the graph, each later one adds one landmark (one or two shortest-path trees) until all are there; the bound uses
// what exists. The graph block is freed once no mode is being built. Invalidation without a writer hook: at every search
// loop the world (R, its table and record area, G, the twelve tuning values) and a hash of everything the link part reads
// (both shoreline trees, the linked waypoints' "type 0x15" bit, the body cell counts) are compared; every 256th also a
// hash of the land part. A difference drops the tables.
//
// Shadow (WorldAlt=2): at the first loop of a search the game's whole search runs twice more on the same inputs, once
// plain and once with the bound, and path cost, node sequence, generated nodes and closed count are compared; then the
// real search runs plain, so the route is the game's. The planner's context is saved and put back, the closed bits
// cleared again, [RouteAsync]'s node budget put back; a pair in which the budget cut a search is not compared.
// Runtime switch: export ts3native_worldalt +0x00 (native_set worldalt), only when installed (ini value not 0).
#include "worldalt-core.h"

// exported block, not in the ts3fixes slot range (docs/registry.md "native exports")
struct WorldAltCounters {
    volatile LONG waMode;         // +0x00 runtime switch: 0 off, 1 use, 2 shadow
    volatile LONG waReady;        // +0x04 bit 0 the eight sites, 1 shadow helpers (the search, the path free)
    volatile LONG waLandmarks;    // +0x08 landmarks per cost mode
    volatile LONG waLoops;        // +0x0C search loops seen, any mode (the circle pass and its rerun are two)
    volatile LONG waBound;        // +0x10 of them: ran with the bound
    volatile LONG waSkip[8];      // +0x14 not: WAS_* - 1 (cost mode, virtual end, flags, water lot, scope, world, busy, no table yet)
    volatile LONG waGraphs;       // +0x34 graphs built
    volatile LONG waTrees;        // +0x38 landmarks built
    volatile LONG waDrops;        // +0x3C tables dropped: the world, the links or the tuning changed
    volatile LONG waMemKb;        // +0x40
    volatile LONG waFound[2];     // +0x44 searches that found a path: [0] without the bound, [1] with it
    volatile LONG waLines;        // +0x4C mismatch lines written
    volatile LONGLONG waBuildUs;  // +0x50 graph and landmark builds
    volatile LONGLONG waCheckUs;  // +0x58 the per-loop world check
    volatile LONGLONG waGen[2];   // +0x60 nodes generated (heuristic calls): [0] loops without the bound, [1] with it
    volatile LONGLONG waRaised;   // +0x70 of [1]: the bound was above the game's heuristic
    volatile LONGLONG waClosed[2];  // +0x78 waypoints expanded (closed bits at the goal), searches that found a path
    // shadow
    volatile LONG waShadow;       // +0x88 searches run both ways and compared
    volatile LONG waShSame;       // +0x8C both found, same cost bits, same node sequence
    volatile LONG waShTie;        // +0x90 same cost bits, another node sequence
    volatile LONG waShRound;      // +0x94 cost within 1e-5 of each other (float sums along different paths)
    volatile LONG waShDearer;     // +0x98 the bound's path costs more
    volatile LONG waShCheaper;    // +0x9C the bound's path costs less
    volatile LONG waShNone;       // +0xA0 neither found a path
    volatile LONG waShFound;      // +0xA4 one found a path, the other did not
    volatile LONG waShCapped;     // +0xA8 not compared: the route budget cut one of the two
    volatile LONG waShNoBound;    // +0xAC not compared: the second run took no bound after all
    volatile LONG waShGameOver;   // +0xB0 of dearer + cheaper: the plain path costs less than the game's own first heuristic value
    volatile LONG waPad;
    volatile LONGLONG waShGen[2];     // +0xB8 nodes generated in compared pairs: [0] plain, [1] with the bound
    volatile LONGLONG waShClosed[2];  // +0xC8 waypoints expanded in pairs where both found a path
    volatile LONGLONG waShGenMode[2][2];  // +0xD8 waShGen by cost mode: [0] mode 1, [1] mode 3; each plain, bound
    volatile LONG waByMode[2];    // +0xF8 search loops of cost mode 1, mode 3 (the rest of waLoops: modes 2, 4, other)
};
extern "C" __declspec(dllexport) WorldAltCounters ts3native_worldalt = {};

enum { WAS_OK, WAS_MODE, WAS_VIRTUAL, WAS_FLAGS, WAS_WATERLOT, WAS_SCOPE, WAS_WORLD, WAS_BUSY, WAS_NOTAB };
enum { WA_OPTS = 0x31 };  // the planner's context in DWORDs (docs/99 §2.3: through the goal point at +0xB8..+0xC0)

typedef float(__cdecl* WaCostFn)(const u8* R, const u8* G, const WORD* from, const WORD* to, DWORD isEdge, DWORD edgeByte, DWORD* opts, void* parent);
typedef DWORD*(__fastcall* WaSearchFn)(u8* ctr, void* edx, const u8* R, const u8* G, const WORD* s, const WORD* g, DWORD mode, DWORD* opts, DWORD* best);
typedef void(__fastcall* WaPushFn)(void* vec, void* edx, DWORD** node);
typedef DWORD*(__fastcall* WaPathFn)(u8* ctr, void* edx, DWORD* node);
typedef void(__fastcall* WaPathFreeFn)(void* ecx, void* edx, DWORD* path);
typedef const u8*(__fastcall* WaLotAtFn)(const u8* wm, void* edx, float x, float z);  // 0xC68990, ret 8

static const void* g_waCost[WA_MODES];  // the game's cost functions of modes 1 and 3
static WaSearchFn g_waSearch;           // 0x676260 (through every detour on it)
static WaPushFn g_waPushOrig;           // 0x6761F0
static WaPathFn g_waPathOrig;           // 0x676130
static WaPathFreeFn g_waPathFree;       // 0x674220
static WaLotAtFn g_waLotAt;
static const u8* const* g_waWm;         // the world manager slot 0x11ECBC4
static bool g_waAll;                    // WorldAltAll=1 (or HouseholdToo): every router
static void* (*g_waAlloc)(size_t) = NULL;  // selftest: its own allocator

// ---- the world's tables ----
struct WaWorld {
    bool valid, bad, haveGraph;
    bool done[WA_MODES];        // nothing left to add
    const u8* R;
    const u8* G;
    WaR rv;
    WaTune tune;
    DWORD linkHash, landHash, checks;
    DWORD n, nodes, chunks;
    DWORD* base;                // chunks + 1, our own copy of the graph's
    DWORD baseCap;
    WaGraph g;
    DWORD* gmem;
    DWORD gmemWords;
    WaTab tab[WA_MODES];
    DWORD tabCap[WA_MODES];     // DWORDs allocated at tab[m].d
};
static WaWorld g_waW;
static SRWLOCK g_waLock = SRWLOCK_INIT;  // g_waW; held for a loop's set-up only
static volatile LONG g_waGen;            // moved by every drop: a search set up before it stops using its query
static DWORD g_waWords;                  // DWORDs we hold

struct WaRes { bool found, bound, capped; float cost, h0; DWORD len, hash, closed; LONG gen; };
struct WaTh {
    bool active;          // this loop takes the bound
    bool bound;           // shadow: a loop of the running nested search took it
    bool found;           // the last search on this thread reached its goal (WaPathOut)
    int nested;           // 0 the game's own search, 1 shadow run without the bound, 2 with it
    uintptr_t nestSp;     // a stack address inside the WaShadow call that set `nested` (its runs are deeper)
    const DWORD* nestOpts;  // and the planner context it runs them on
    int kind;             // tally of the running loop: 0 without the bound, 1 with it
    float delta;          // excess of the bound over the game's heuristic for the node about to be pushed
    float cost, h0;
    LONG gen, calls, raised;
    DWORD len, hash, closed;
    const DWORD* base;
    DWORD chunks, n;
    const DWORD* pendOpts;  // shadow: the search whose circle loop was compared (its rerun is not compared again)
    DWORD pendS, pendG;
    WaQuery q;
};
static __declspec(thread) WaTh t_wa;

static LONGLONG WaNowQ() {
    LARGE_INTEGER q;
    QueryPerformanceCounter(&q);
    return q.QuadPart;
}
static LONGLONG WaUs(LONGLONG ticks) { return g_qpf.QuadPart ? ticks * 1000000 / g_qpf.QuadPart : 0; }

static DWORD* WaMem(DWORD words) {
    DWORD* p = NULL;
    if (g_waAlloc) p = (DWORD*)g_waAlloc((size_t)words * 4);
#ifndef WORLDALT_HOST_TEST
    else p = (DWORD*)VirtualAlloc(NULL, (size_t)words * 4, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
#endif
    if (p) g_waWords += words, ts3native_worldalt.waMemKb = (LONG)(g_waWords / 256);
    return p;
}
static void WaMemFree(DWORD* p, DWORD words) {
    if (!p) return;
    g_waWords -= words, ts3native_worldalt.waMemKb = (LONG)(g_waWords / 256);
#ifndef WORLDALT_HOST_TEST
    if (!g_waAlloc) VirtualFree(p, 0, MEM_RELEASE);
#endif
}
static void WaGraphFree(WaWorld& w) {
    WaMemFree(w.gmem, w.gmemWords);
    w.gmem = NULL, w.gmemWords = 0, w.haveGraph = false;
}
// Forget what was built (the table blocks stay for the next build). Caller holds g_waLock.
static void WaDrop(WaWorld& w) {
    InterlockedIncrement(&g_waGen);
    WaGraphFree(w);
    for (int m = 0; m < WA_MODES; m++) w.tab[m].k = 0, w.done[m] = false;
    w.valid = w.bad = false;
}
static bool WaGraphUp(WaWorld& w, const u8* G) {
    WaSizes sz = {};
    if (!WaMeasure(w.rv, G, &sz)) return false;
    const DWORD words = WaGraphWords(sz);
    DWORD* mem = WaMem(words);
    if (!mem) return false;
    w.gmem = mem, w.gmemWords = words;
    if (!WaBuildGraph(w.rv, G, w.tune, sz, mem, &w.g)) { WaGraphFree(w); return false; }
    if (w.baseCap < sz.chunks + 1) {
        WaMemFree(w.base, w.baseCap);
        w.base = WaMem(sz.chunks + 1), w.baseCap = w.base ? sz.chunks + 1 : 0;
        if (!w.base) { WaGraphFree(w); return false; }
    }
    memcpy(w.base, w.g.base, (sz.chunks + 1) * sizeof(DWORD));
    w.n = sz.n, w.nodes = w.g.nodes, w.chunks = sz.chunks, w.haveGraph = true;
    InterlockedIncrement(&ts3native_worldalt.waGraphs);
    return true;
}
// Is the world the one the tables were built from, and one build step for cost mode `mi` if it is not finished.
// Caller holds g_waLock. 0 = tab[mi] has at least one landmark.
static int WaWorldReady(const u8* ctr, const u8* R, const u8* G, int mi) {
    WorldAltCounters& c = ts3native_worldalt;
    WaWorld& w = g_waW;
    const LONGLONG t0 = WaNowQ();
    WaR rv = {};
    WaTune tune = {};
    memcpy(tune.v[0], ctr + 0x28, sizeof(tune.v[0]));
    memcpy(tune.v[1], ctr + 0x4C, sizeof(tune.v[1]));
    const bool view = WaRView(R, &rv);
    DWORD lh = 0, links = 0;
    const bool linkOk = view && WaLinkHash(rv, G, &lh, &links);
    bool keep = w.valid && linkOk && w.R == R && w.G == G && w.rv.w == rv.w && w.rv.h == rv.h && w.rv.off == rv.off && w.rv.raw == rv.raw &&
                w.rv.size == rv.size && !memcmp(&w.tune, &tune, sizeof(tune)) && w.linkHash == lh;
    if (keep && !w.bad && (++w.checks & 255) == 0 && WaLandHash(rv) != w.landHash) keep = false;
    if (!keep) {
        if (w.valid && !w.bad) InterlockedIncrement(&c.waDrops);
        WaDrop(w);
        w.valid = true, w.bad = !linkOk, w.R = R, w.G = G, w.rv = rv, w.tune = tune, w.linkHash = lh, w.checks = 0;
        w.landHash = linkOk ? WaLandHash(rv) : 0;
    }
    const LONGLONG t1 = WaNowQ();
    InterlockedExchangeAdd64(&c.waCheckUs, WaUs(t1 - t0));
    if (w.bad) return WAS_WORLD;
    WaTab& tab = w.tab[mi];
    if (tab.k < tab.kmax && !w.done[mi]) {
        if (!w.haveGraph) {
            if (!WaGraphUp(w, G)) { w.bad = true; return WAS_WORLD; }
        } else {
            const DWORD need = w.nodes * tab.kmax;
            if (w.tabCap[mi] < need) {
                WaMemFree(tab.d, w.tabCap[mi]);
                tab.d = WaMem(need), w.tabCap[mi] = tab.d ? need : 0, tab.k = 0;
            }
            if (!tab.d) { w.bad = true; return WAS_WORLD; }
            if (WaAddLandmark(w.g, mi, &tab)) InterlockedIncrement(&c.waTrees);
            else w.done[mi] = true;
        }
        bool building = false;
        for (int m = 0; m < WA_MODES; m++) building = building || (m == mi ? tab.k < tab.kmax && !w.done[m] : w.tab[m].k != 0 && w.tab[m].k < w.tab[m].kmax && !w.done[m]);
        if (!building && w.haveGraph) WaGraphFree(w);
        InterlockedExchangeAdd64(&c.waBuildUs, WaUs(WaNowQ() - t1));
    }
    return tab.k ? WAS_OK : WAS_NOTAB;
}

// 0 = cost mode 1, 1 = cost mode 3, -1 any other. `fn` is what the A* calls: the game's function or [RouteAsync]'s
// budget stub in front of it (`push orig; jmp CostGate`, ts3native.cpp PatchCostSites).
static int WaModeOf(const void* fn) {
    const u8* p = (const u8*)fn;
    const void* f = fn;
    if (f != g_waCost[0] && f != g_waCost[1] && p[0] == 0x68 && p[5] == 0xE9) memcpy(&f, p + 1, sizeof(f));
    return f == g_waCost[0] ? 0 : f == g_waCost[1] ? 1 : -1;
}
// mode 3: a start or goal point on a water lot (lot +0x4B0) gives that lot's type-0x15 links the small water-lot cost
// (0x674250's two lot tests; the further conditions there only narrow it). Cannot tell = yes.
static bool WaWaterLot(const DWORD* opts) {
    const u8* wm = g_waWm ? *g_waWm : NULL;
    if (!wm || !g_waLotAt) return true;
    for (int i = 0; i < 2; i++) {
        float p[2] = {};
        memcpy(p, opts + (i ? 0x2E : 0x2A), sizeof(p));
        const u8* lot = g_waLotAt(wm, NULL, p[0], p[1]);
        if (lot && lot[0x4B0]) return true;
    }
    return false;
}
static bool WaScopeOk() {
    if (g_waAll) return true;
    bool worker = false;
    const LONG s = PccScopeNow(&worker);  // plancand.cpp: the sim session's or the worker job's router scope
    return s == 2 || s == 3;
}
// Sets this thread's query up for the loop start -> goal. WAS_OK or why the loop takes no bound.
static int WaPrepare(WaTh& t, int mi, const u8* ctr, const u8* R, const u8* G, const WORD* s, const WORD* g, const DWORD* opts) {
    if (mi < 0) return WAS_MODE;
    if (!R || !G) return WAS_WORLD;
    if (s[0] == 0xFFFF || g[0] == 0xFFFF) return WAS_VIRTUAL;
    if (opts[0] & 3) return WAS_FLAGS;
    if (!WaScopeOk()) return WAS_SCOPE;
    if (mi == 1 && (opts[0] & 0x80) && WaWaterLot(opts)) return WAS_WATERLOT;
    if (!TryAcquireSRWLockExclusive(&g_waLock)) return WAS_BUSY;
    int why = WaWorldReady(ctr, R, G, mi);
    if (why == WAS_OK) {
        const WaWorld& w = g_waW;
        const DWORD sn = WaNodeOf(w.base, w.chunks, w.n, s[0], s[1]), gn = WaNodeOf(w.base, w.chunks, w.n, g[0], g[1]);
        if (sn == WA_INF || gn == WA_INF) why = WAS_VIRTUAL;
        else if (!WaQueryInit(w.tab[mi], sn, gn, &t.q)) why = WAS_NOTAB;
        else t.base = w.base, t.chunks = w.chunks, t.n = w.n, t.gen = g_waGen;
    }
    ReleaseSRWLockExclusive(&g_waLock);
    return why;
}

static DWORD WaClosedCount(const u8* ctr) {
    const DWORD bytes = *(const DWORD*)(ctr + 8);
    const u8* bits = *(const u8* const*)(ctr + 0x10);
    DWORD n = 0;
    if (!bits) return 0;
    for (DWORD i = 0; i < bytes; i++) {
        DWORD v = bits[i];
        v = v - ((v >> 1) & 0x55), v = (v & 0x33) + ((v >> 2) & 0x33);
        n += (v + (v >> 4)) & 0x0F;
    }
    return n;
}
static void WaFlush(WaTh& t) {
    WorldAltCounters& c = ts3native_worldalt;
    if (t.calls) InterlockedExchangeAdd64(&c.waGen[t.kind], t.calls);
    if (t.raised) InterlockedExchangeAdd64(&c.waRaised, t.raised);
    t.calls = t.raised = 0;
}

static void WaMismatchLine(const char* what, const WORD* s, const WORD* g, int mi, const DWORD* opts, const WaRes* r) {
    WorldAltCounters& c = ts3native_worldalt;
    if (InterlockedIncrement(&c.waLines) > 10) return;
    Log("WORLD ALT MISMATCH %s: start %u:%u goal %u:%u cost mode %d flags 0x%lX: plain %s cost %.3f nodes %lu (first heuristic %.3f, generated %ld), "
        "with the bound %s cost %.3f nodes %lu (generated %ld)", what, (unsigned)s[0], (unsigned)s[1], (unsigned)g[0], (unsigned)g[1], mi ? 3 : 1, opts[0],
        r[0].found ? "found" : "no path", (double)r[0].cost, r[0].len, (double)r[0].h0, r[0].gen, r[1].found ? "found" : "no path", (double)r[1].cost,
        r[1].len, r[1].gen);
}
// Shadow: the whole search twice on the same inputs, without and with the bound; nothing of it reaches the game's search.
// Not inlined: its frame lies below the begin hook's that calls it and above every loop of its two runs.
static __declspec(noinline) void WaShadow(WaTh& t, u8* ctr, const u8* R, const u8* G, const WORD* s, const WORD* g, int mi, DWORD* opts) {
    WorldAltCounters& c = ts3native_worldalt;
    DWORD save[WA_OPTS] = {};
    memcpy(save, opts, sizeof(save));
    const Budget budget = t_budget;
    WaRes r[2] = {};
    t.nestSp = (uintptr_t)save, t.nestOpts = opts;
    for (int pass = 0; pass < 2; pass++) {
        memcpy(opts, save, sizeof(save));
        opts[0x28] = 0;  // circle radius^2 0.0: the run decides its own circle pass, as the game's outermost call does
        t_budget = budget;
        t.nested = pass + 1, t.found = t.bound = false, t.calls = t.raised = 0, t.cost = t.h0 = 0, t.len = t.hash = t.closed = 0;
        DWORD best = 0;
        DWORD* path = g_waSearch(ctr, NULL, R, G, s, g, mi ? 3 : 1, opts, &best);
        r[pass].found = path != NULL, r[pass].bound = t.bound, r[pass].capped = t_budget.capped != budget.capped;
        r[pass].cost = t.cost, r[pass].h0 = t.h0, r[pass].len = t.len, r[pass].hash = t.hash, r[pass].closed = t.closed, r[pass].gen = t.calls;
        if (path) g_waPathFree(NULL, NULL, path);
    }
    t.nested = 0, t.active = false, t.found = false, t.delta = 0, t.calls = t.raised = 0;
    memcpy(opts, save, sizeof(save));
    t_budget = budget;
    const DWORD bytes = *(const DWORD*)(ctr + 8);  // the loop we are in cleared them before its first heuristic call
    if (bytes && *(u8**)(ctr + 0x10)) memset(*(u8**)(ctr + 0x10), 0, bytes);
    if (r[0].capped || r[1].capped) { InterlockedIncrement(&c.waShCapped); return; }
    if (!r[1].bound) { InterlockedIncrement(&c.waShNoBound); return; }
    InterlockedIncrement(&c.waShadow);
    InterlockedExchangeAdd64(&c.waShGen[0], r[0].gen), InterlockedExchangeAdd64(&c.waShGen[1], r[1].gen);
    InterlockedExchangeAdd64(&c.waShGenMode[mi][0], r[0].gen), InterlockedExchangeAdd64(&c.waShGenMode[mi][1], r[1].gen);
    if (r[0].found != r[1].found) {
        InterlockedIncrement(&c.waShFound);
        WaMismatchLine("found", s, g, mi, save, r);
        return;
    }
    if (!r[0].found) { InterlockedIncrement(&c.waShNone); return; }
    InterlockedExchangeAdd64(&c.waShClosed[0], r[0].closed), InterlockedExchangeAdd64(&c.waShClosed[1], r[1].closed);
    const float a = r[0].cost, b = r[1].cost, hi = a > b ? a : b, lo = a > b ? b : a;
    if (!memcmp(&a, &b, 4)) InterlockedIncrement(r[0].len == r[1].len && r[0].hash == r[1].hash ? &c.waShSame : &c.waShTie);
    else if (hi - lo <= hi * 1e-5f) InterlockedIncrement(&c.waShRound);
    else {
        InterlockedIncrement(b > a ? &c.waShDearer : &c.waShCheaper);
        // the game's own heuristic was above the cost of the path it found: its search is not exact for this pair either
        if (a < r[0].h0 * (1.0f - 1e-5f)) InterlockedIncrement(&c.waShGameOver);
        WaMismatchLine(b > a ? "dearer" : "cheaper", s, g, mi, save, r);
    }
}

// ---- the four hook bodies ----
// +0x128: the first heuristic call of a search loop (start -> goal). The game's value is returned as it is.
static float __cdecl WaBegin(WaCostFn fn, u8* ctr, const u8* R, const u8* G, const WORD* s, const WORD* g, DWORD isEdge, DWORD edgeByte, DWORD* opts, void* parent) {
    const float h0 = fn(R, G, s, g, isEdge, edgeByte, opts, parent);
    WorldAltCounters& c = ts3native_worldalt;
    WaTh& t = t_wa;
    t.active = false, t.delta = 0;
    const LONG mode = c.waMode;
    if (t.nested) {  // a shadow run that an unwind left open: this loop is not below that call, or not on its context
        const char here = 0;
        if ((uintptr_t)&here >= t.nestSp || opts != t.nestOpts) t.nested = 0;
    }
    if (t.nested) {  // a shadow run's loop
        if (t.h0 == 0) t.h0 = h0;
        if (t.nested == 2 && WaPrepare(t, WaModeOf((const void*)fn), ctr, R, G, s, g, opts) == WAS_OK) t.active = t.bound = true;
        return h0;
    }
    WaFlush(t);
    t.kind = 0, t.found = false;
    InterlockedIncrement(&c.waLoops);
    const int mi = WaModeOf((const void*)fn);
    if (mi >= 0) InterlockedIncrement(&c.waByMode[mi]);
    if (mode != 1 && mode != 2) return h0;
    const int why = WaPrepare(t, mi, ctr, R, G, s, g, opts);
    if (why != WAS_OK) {
        InterlockedIncrement(&c.waSkip[why - 1]);
        t.pendOpts = NULL;
        return h0;
    }
    if (mode == 1) {
        InterlockedIncrement(&c.waBound);
        t.active = true, t.kind = 1;
        return h0;
    }
    if (!g_waSearch || !g_waPathFree) return h0;
    // the rerun without the circle of a search whose circle loop was compared: the pair covered it
    DWORD sr = 0, gr = 0, r2 = opts[0x28];
    memcpy(&sr, s, 4), memcpy(&gr, g, 4);
    const bool rerun = !(r2 << 1) && t.pendOpts == opts && t.pendS == sr && t.pendG == gr;
    t.pendOpts = NULL;
    if (rerun) return h0;
    WaShadow(t, ctr, R, G, s, g, mi, opts);
    if (r2 << 1) t.pendOpts = opts, t.pendS = sr, t.pendG = gr;
    return h0;
}
// +0x44D, +0x674, +0x8EA: the heuristic call for a node about to be pushed. Returns the game's value.
static float __cdecl WaHeur(WaCostFn fn, const u8* R, const u8* G, const WORD* from, const WORD* to, DWORD isEdge, DWORD edgeByte, DWORD* opts, void* parent) {
    const float h = fn(R, G, from, to, isEdge, edgeByte, opts, parent);
    WaTh& t = t_wa;
    t.calls++;
    t.delta = 0;
    if (!t.active) return h;
    if (t.gen != g_waGen) { t.active = false; return h; }  // the tables were dropped under this search
    const DWORD node = WaNodeOf(t.base, t.chunks, t.n, from[0], from[1]);
    if (node == WA_INF || !(h < 1e8f)) return h;  // a wall (or NaN) stays what the game said
    const float b = WaUnitsToCost(WaBoundUnits(t.q, node));
    if (b > h) t.delta = b - h, t.raised++;
    return h;
}
// +0x4B9, +0x6DC, +0x91D: the open-list push of that node (thiscall on the heap vector, one argument, ret 4)
static void __fastcall WaPush(void* vec, void*, DWORD** node) {
    WaTh& t = t_wa;
    if (t.delta > 0 && *node) {
        float f = 0;
        memcpy(&f, *node + 1, 4);
        f += t.delta;
        memcpy(*node + 1, &f, 4);
    }
    t.delta = 0;
    g_waPushOrig(vec, NULL, node);
}
// +0xA1B: the goal node becomes the path (thiscall on the container, one argument, ret 4)
static DWORD* __fastcall WaPathOut(u8* ctr, void*, DWORD* node) {
    WorldAltCounters& c = ts3native_worldalt;
    WaTh& t = t_wa;
    if (node) {
        t.found = true;
        memcpy(&t.cost, node, 4);
        DWORD len = 0, h = 0x811C9DC5;
        for (const DWORD* n = node; n && len < 1000000; n = (const DWORD*)(uintptr_t)n[3]) h = WaMix(h, n[2]), len++;
        t.len = len, t.hash = h, t.closed = WaClosedCount(ctr);
        if (!t.nested) {
            WaFlush(t);
            InterlockedIncrement(&c.waFound[t.kind]);
            InterlockedExchangeAdd64(&c.waClosed[t.kind], t.closed);
            t.pendOpts = NULL;
        }
    }
    return g_waPathOrig(ctr, NULL, node);
}

#ifndef WORLDALT_HOST_TEST
// The two thunks take the place of `call dword ptr [esp+0x80]` (cdecl, 8 arguments, float in ST0; the caller pops). At
// entry [esp] = return address, [esp+4..+0x20] = the arguments, [esp+0x84] = the A* frame's cost function pointer, ebp =
// the A* container. They pass the pointer (and ebp) in front of a copy of the arguments; eax, ecx, edx are free at a call.
static __declspec(naked) void WaBeginThunk() {
    __asm {
        mov eax, dword ptr [esp + 0x84]
        push dword ptr [esp + 0x20]
        push dword ptr [esp + 0x20]
        push dword ptr [esp + 0x20]
        push dword ptr [esp + 0x20]
        push dword ptr [esp + 0x20]
        push dword ptr [esp + 0x20]
        push dword ptr [esp + 0x20]
        push dword ptr [esp + 0x20]
        push ebp
        push eax
        call WaBegin
        add esp, 0x28
        ret
    }
}
static __declspec(naked) void WaHeurThunk() {
    __asm {
        mov eax, dword ptr [esp + 0x84]
        push dword ptr [esp + 0x20]
        push dword ptr [esp + 0x20]
        push dword ptr [esp + 0x20]
        push dword ptr [esp + 0x20]
        push dword ptr [esp + 0x20]
        push dword ptr [esp + 0x20]
        push dword ptr [esp + 0x20]
        push dword ptr [esp + 0x20]
        push eax
        call WaHeur
        add esp, 0x24
        ret
    }
}

static const DWORD WA_ASTAR = TS3A(0x00676260), WA_PLANNER = TS3A(0x0065E0D0);
enum { WA_OFF_BEGIN = 0x128, WA_OFF_PUSH0 = 0x222, WA_OFF_PATH = 0xA1B, WA_OFF_FREE = 0x20E0 };
static const DWORD kWaHeurOff[3] = {0x44D, 0x674, 0x8EA}, kWaPushOff[3] = {0x4B9, 0x6DC, 0x91D};

static bool WaCallAt(DWORD site, DWORD* target) {
    const u8* p = (const u8*)(uintptr_t)site;
    if (!Readable(p, 5) || p[0] != 0xE8) return false;
    *target = (DWORD)(site + 5 + *(const LONG*)(p + 1));
    return true;
}
static bool WaRetarget(DWORD site, void* hook) {
    const LONG rel = (LONG)((uintptr_t)hook - (site + 5));
    return Write((u8*)(uintptr_t)site + 1, &rel, 4);
}
static bool WaCallInto(DWORD site, void* thunk) {  // FF 94 24 80 00 00 00 -> E8 rel32, 66 90
    u8 b[7] = {0xE8, 0, 0, 0, 0, 0x66, 0x90};
    const LONG rel = (LONG)((uintptr_t)thunk - (site + 5));
    memcpy(b + 1, &rel, 4);
    return Write((u8*)(uintptr_t)site, b, 7);
}
// Checks every site of the A* at `astar` and the path free behind the planner's call at `planner` + 0x20E0, then writes.
// Returns the ready bits (0 = nothing written). The selftest runs it on its own bytes.
static LONG WaPatch(DWORD astar, DWORD planner) {
    static const u8 kInd[7] = {0xFF, 0x94, 0x24, 0x80, 0x00, 0x00, 0x00};
    static const u8 kFree[7] = {0x56, 0x8B, 0x74, 0x24, 0x08, 0x85, 0xF6};  // push esi; mov esi,[esp+8]; test esi,esi
    DWORD push0 = 0, path = 0, pf = 0;
    bool ok = Matches((const void*)(uintptr_t)(astar + WA_OFF_BEGIN), kInd, 7) && WaCallAt(astar + WA_OFF_PUSH0, &push0) && WaCallAt(astar + WA_OFF_PATH, &path);
    for (int i = 0; i < 3 && ok; i++) {
        DWORD t = 0;
        ok = Matches((const void*)(uintptr_t)(astar + kWaHeurOff[i]), kInd, 7) && WaCallAt(astar + kWaPushOff[i], &t) && t == push0;
    }
    // both helpers sit just below the A* (0x6761F0, 0x676130 in Steam 1.67)
    ok = ok && push0 != path && push0 < astar && astar - push0 <= 0x400 && path < astar && astar - path <= 0x400;
    if (!ok) {
        Log("SKIP world alt: 0x%08lX does not hold the world A*'s heuristic calls, pushes and path call of Steam 1.67, nothing written", astar);
        return 0;
    }
    g_waPushOrig = (WaPushFn)(uintptr_t)push0, g_waPathOrig = (WaPathFn)(uintptr_t)path;
    int done = 0;
    for (int i = 0; i < 3; i++) done += WaRetarget(astar + kWaPushOff[i], (void*)WaPush) ? 1 : 0;
    done += WaRetarget(astar + WA_OFF_PATH, (void*)WaPathOut) ? 1 : 0;
    for (int i = 0; i < 3; i++) done += WaCallInto(astar + kWaHeurOff[i], (void*)WaHeurThunk) ? 1 : 0;
    // last: until this one is in, no loop is set up and the others only pass the game's values through
    if (done != 7 || !WaCallInto(astar + WA_OFF_BEGIN, (void*)WaBeginThunk)) {
        Log("FAIL world alt: %d of 8 sites written (err %lu); without the loop set-up the written ones pass the game's values through", done, GetLastError());
        return 0;
    }
    LONG ready = 1;
    if (WaCallAt(planner + WA_OFF_FREE, &pf) && Matches((const void*)(uintptr_t)pf, kFree, 7)) {
        g_waPathFree = (WaPathFreeFn)(uintptr_t)pf, g_waSearch = (WaSearchFn)(uintptr_t)astar;
        ready |= 2;
    }
    return ready;
}

static void InstallWorldAlt(const wchar_t* ini) {
    WorldAltCounters& c = ts3native_worldalt;
    int mode = Clamp(GetPrivateProfileIntW(L"WorldPrecompute", L"WorldAlt", 0, ini), 0, 2);
    if (!mode) { Log("world alt off ([WorldPrecompute] WorldAlt=0)"); return; }
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
    c.waLandmarks = Clamp(GetPrivateProfileIntW(L"WorldPrecompute", L"WorldAltLandmarks", 32, ini), 4, WA_MAXK);
    for (int m = 0; m < WA_MODES; m++) g_waW.tab[m].kmax = (DWORD)c.waLandmarks;
    g_waAll = GetPrivateProfileIntW(L"WorldPrecompute", L"WorldAltAll", 0, ini) != 0 || g_hhToo == 1;
    g_waCost[0] = (const void*)(uintptr_t)TS3A(0x00674E40), g_waCost[1] = (const void*)(uintptr_t)TS3A(0x00674250);
    g_waLotAt = (WaLotAtFn)(uintptr_t)TS3A(0x00C68990);
    g_waWm = (const u8* const*)(uintptr_t)TS3A(0x011ECBC4);
    c.waReady = WaPatch(WA_ASTAR, WA_PLANNER);
    if (!c.waReady) return;
    if (mode == 2 && !(c.waReady & 2)) {
        Log("SKIP world alt shadow: the planner's path free was not found at 0x%08lX; WorldAlt stays off", WA_PLANNER + WA_OFF_FREE);
        mode = 0;
    }
    c.waMode = mode;
    Log("OK   world alt: ready 0x%lX, WorldAlt=%d (%s), %ld landmarks per cost mode (1 and 3), %s", c.waReady, mode,
        mode == 2 ? "shadow: every search also runs plain and with the bound, the game's route is used" : mode ? "the landmark bound raises the heuristic" : "off",
        c.waLandmarks, g_waAll ? "every router" : "NPC routers only");
}
#endif  // WORLDALT_HOST_TEST

// the 60 s counter line (ts3native.cpp MaybeLogCounters)
static void WaLogCounters() {
    const WorldAltCounters& c = ts3native_worldalt;
    if (!c.waReady) return;
    Log("world alt: mode %ld ready 0x%lX: search loops %ld (cost mode 1 %ld, mode 3 %ld), with the bound %ld (not: cost mode %ld, water start or goal %ld, flags %ld, water lot %ld, "
        "scope %ld, world %ld, busy %ld, no table yet %ld); found a path %ld plain / %ld bound; nodes generated %lld plain / %lld bound (bound above the "
        "game's heuristic %lld); waypoints expanded %lld plain / %lld bound; graphs %ld, landmarks %ld, dropped %ld, build %ld ms, checks %ld ms, %ld KB",
        c.waMode, c.waReady, c.waLoops, c.waByMode[0], c.waByMode[1], c.waBound, c.waSkip[0], c.waSkip[1], c.waSkip[2], c.waSkip[3], c.waSkip[4], c.waSkip[5], c.waSkip[6], c.waSkip[7],
        c.waFound[0], c.waFound[1], (long long)c.waGen[0], (long long)c.waGen[1], (long long)c.waRaised, (long long)c.waClosed[0], (long long)c.waClosed[1],
        c.waGraphs, c.waTrees, c.waDrops, (LONG)(c.waBuildUs / 1000), (LONG)(c.waCheckUs / 1000), c.waMemKb);
    if (!c.waShadow && !c.waShCapped && !c.waShNoBound) return;
    Log("world alt shadow: searches compared %ld: same cost and nodes %ld, same cost other nodes %ld, cost within 1e-5 %ld, bound dearer %ld, bound "
        "cheaper %ld (of both: plain cost below the game's own first heuristic %ld), neither found %ld, one found %ld; not compared: budget cut %ld, no "
        "bound %ld; nodes generated %lld plain / %lld bound (cost mode 1: %lld / %lld, mode 3: %lld / %lld); waypoints expanded (both found) %lld plain / "
        "%lld bound",
        c.waShadow, c.waShSame, c.waShTie, c.waShRound, c.waShDearer, c.waShCheaper, c.waShGameOver, c.waShNone, c.waShFound, c.waShCapped, c.waShNoBound,
        (long long)c.waShGen[0], (long long)c.waShGen[1], (long long)c.waShGenMode[0][0], (long long)c.waShGenMode[0][1], (long long)c.waShGenMode[1][0],
        (long long)c.waShGenMode[1][1], (long long)c.waShClosed[0], (long long)c.waShClosed[1]);
}
