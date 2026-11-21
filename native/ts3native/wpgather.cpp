// docs/679 §5: [WaypointSnap] Gather. A closest-waypoint snap (0x6595B0) that misses the game's list of ten recent
// candidate areas (0x652D80) gathers the area again in 0x654790: every waypoint of 1-36 chunks, a link walk each, a lot
// lookup per lot waypoint. The game never invalidates that list (an entry lives until its slot is recycled or the planner
// is torn down, 0x654110), so its hits are the game's own business; this file only makes the misses cheap.
//  - The gather's one call site 0x659787 goes to WpgGatherHook. What a chunk contributes to an area depends only on the
//    chunk, the filter bits of the area's flags and the routing data (wpgather-core.h), so the hook keeps each chunk's
//    rows per thread and builds the area's list from them, in the game's order, through the game's own append 0x654240.
//    A chunk that is not stored is gathered by the game itself on a one-chunk area of ours. Which areas the game gathers,
//    its list of ten and the scoring that follows are untouched: with the same rows the snap is the game's.
//  - Stored rows are dropped by g_snGatherSeq ([RouteSnap]'s writer rows that can change what the gather reads: lot table,
//    lot graph, blocked bits, G build, terrain reload, the non-footprint routing ICs), by any change of the routing objects
//    or the world geometry, and optionally by age (GatherMs). A row holds a record offset, not a pointer, and is only used
//    with the record area it was read from. A chunk's offset word and waypoint count are compared at every use.
//  - Not stored, the game's gather runs: areas with the water point test (flag 0x800), areas within 0.01 of a chunk border
//    or world bound (the game's x87 sums could round the other way), a missing sequence, a write during the call.
//  - Mode 2 (shadow): the game's gather runs first; the list built from the stored rows is compared with it row by row
//    (position bits, waypoint, chunk, record, type, order). Equal candidate lists give equal chosen waypoints and an equal
//    answer, since the scoring is the game's code on that list. A difference counts, logs and empties the table.
//  - Scope: only snaps under World_ArePointsConnectedByLand (the IC's call site 0x787282, chained over wpsnap.cpp's stub)
//    unless GatherAll=1 (plans too; the route worker then has a table of its own).
//  - Installed once: the originals come from WpgChainPlan (wpgather-core.h), which refuses a second pass and a site that
//    already calls one of these hooks (docs/679 §8).
// Threads: everything is per thread (the table, the scratch rows, the one-chunk area); no lock. Counters interlocked.
#include "wpgather-core.h"

struct WpGatherCounters {
    volatile LONG wgMode;      // +0x00 runtime switch: 0 off, 1 serve, 2 shadow
    volatile LONG wgAll;       // +0x04 1 = every snap, 0 = only under ArePointsConnectedByLand
    volatile LONG wgReady;     // +0x08 bit 0 the gather call site, bit 1 the IC's call site, bit 2 the write sequence is complete
    volatile LONG wgCapMs;     // +0x0C a stored chunk's longest life (0 = no limit)
    volatile LONG wgCalls;     // +0x10 gathers the game asked for in scope
    volatile LONG wgAvoided;   // +0x14 of them built from stored rows alone (no game gather ran)
    volatile LONG wgPartly;    // +0x18 built with at least one chunk gathered by the game
    volatile LONG wgHits;      // +0x1C chunks taken from the table
    volatile LONG wgFills;     // +0x20 chunks gathered by the game on the one-chunk area
    volatile LONG wgWater;     // +0x24 not served: water point test
    volatile LONG wgEdge;      // +0x28 not served: near a chunk border / world bound, or outside the model
    volatile LONG wgOther;     // +0x2C not served: no sequence, no memory, a fill failed, too many rows, a write during the call
    volatile LONG wgSeqDrop;   // +0x30 table emptied: write sequence
    volatile LONG wgCtxDrop;   // +0x34 table emptied: routing objects / geometry
    volatile LONG wgFull;      // +0x38 table emptied: full
    volatile LONG wgStale;     // +0x3C table emptied: a chunk header changed under an unchanged sequence (must stay 0)
    volatile LONG wgShadow;    // +0x40 shadow compares
    volatile LONG wgMismatch;  // +0x44 of them different (must stay 0)
    volatile LONG wgRows;      // +0x48 rows handed to the game
    volatile LONG wgOutScope;  // +0x4C gathers outside the scope (the game's, untouched)
    volatile LONGLONG wgUs;       // +0x50 time in the hook, in scope
    volatile LONGLONG wgGameUs;   // +0x58 of it inside the game's gather (fills, not-served areas, shadow)
    volatile LONGLONG wgAvoidUs;  // +0x60 time of the avoided gathers
    volatile LONG wgGameCalls; // +0x68 game gathers behind wgGameUs
    volatile LONG wgPad;       // +0x6C
};
extern "C" __declspec(dllexport) WpGatherCounters ts3native_wpgather = {};

static const DWORD WPG_APPEND = TS3A(0x00654240);  // candidate vector append: thiscall (ECX the vector at area +0x30), one row pointer, ret 4
typedef void(__fastcall* WpgAppendFn)(void* vec, void* edx, const WpgGameRow* row);

static WpsSameFn g_wpgSameOrig;
static WpsGatherFn g_wpgGatherOrig;
static WpgChain g_wpgChain;  // the originals, decided once (WpgChainPlan)

struct WpgTh {
    WpgTab tab;
    WpgRow out[WPG_OUT];
    WpgRow fill[1024];  // one chunk: the waypoint index is 10 bits
    DWORD area[17];     // the one-chunk area (0x44 bytes); its candidate vector is the game's and stays with the thread
};
static __declspec(thread) WpgTh* t_wpg;
static __declspec(thread) int t_wpgDepth;  // inside the IC's same-land call
static __declspec(thread) int t_wpgNoMem;

struct WpgUser {
    WpgTh* th;
    const DWORD* a;  // the game's area
    const DWORD* r;  // R
    LONGLONG gameUs;
    LONG gameCalls;
};

// 0x787282: the IC's call of 0x65DF90 (marks the scope; wpsnap.cpp's stub, when installed, is what this calls)
static bool __fastcall WpgSameHook(void* self, void* edx, DWORD ax, DWORD az, DWORD bx, DWORD bz) {
    t_wpgDepth++;
    const bool r = g_wpgSameOrig(self, edx, ax, az, bx, bz);
    t_wpgDepth--;
    return r;
}

static bool WpgGame(WpgUser* u, void* area) {
    const LONGLONG t0 = WpsNowUs();
    const bool ok = g_wpgGatherOrig(area);
    u->gameUs += WpsNowUs() - t0;
    u->gameCalls++;
    return ok;
}

static void WpgHead(void* user, unsigned int chunk, unsigned int* chunkOff, unsigned int* wpCount) {
    const WpgUser* u = (const WpgUser*)user;
    const DWORD off = ((const DWORD*)(uintptr_t)u->r[2])[chunk];
    *chunkOff = off;
    *wpCount = u->r[4] >= 2 && off <= u->r[4] - 2 ? *(const unsigned short*)(uintptr_t)(u->r[3] + off) : 0xFFFFFFFFu;
}

// one chunk through the game's gather: an area of class 1, extent 1, try 0 at a point of that chunk visits that chunk only
static int WpgFill(void* user, unsigned int chunk, float px, float pz, const WpgRow** rows, unsigned int* n, unsigned int* chunkOff, unsigned int* wpCount) {
    WpgUser* u = (WpgUser*)user;
    DWORD* t = u->th->area;
    const LONG seq = g_snGatherSeq;
    memcpy(&t[0], &px, 4);
    memcpy(&t[1], &pz, 4);
    t[2] = t[0], t[3] = t[1], t[4] = 0;
    t[5] = 1, t[6] = 1, t[7] = 1, t[8] = 1, t[9] = 0, t[10] = chunk;
    t[11] = (u->a[11] & ~0x800u) | 0x400;
    t[15] = u->a[15];  // the vector's allocator name
    t[16] = u->a[16];
    WpgHead(user, chunk, chunkOff, wpCount);
    if (!WpgGame(u, t)) return 0;
    const WpgGameRow* g = (const WpgGameRow*)(uintptr_t)t[12];
    const unsigned int cnt = (t[13] - t[12]) >> 5;
    if (cnt > 1024) return 0;
    const DWORD base = u->r[3], size = u->r[4];
    for (unsigned int i = 0; i < cnt; i++) {
        if (g[i].chunk != chunk || g[i].rec < base || g[i].rec - base >= size) return 0;  // not the chunk the plan named
        WpgRow& w = u->th->fill[i];
        w.x = g[i].x, w.z = g[i].z, w.wp = g[i].wp, w.chunk = g[i].chunk, w.rec = g[i].rec - base, w.type = g[i].type;
    }
    *rows = u->th->fill;
    *n = cnt;
    unsigned int off2 = 0, cnt2 = 0;
    WpgHead(user, chunk, &off2, &cnt2);
    return seq == g_snGatherSeq && off2 == *chunkOff && cnt2 == *wpCount ? 1 : 2;
}

static void WpgAdd(WpGatherCounters& c, const WpgUser& u, LONGLONG t0) {
    InterlockedExchangeAdd64(&c.wgUs, WpsNowUs() - t0);
    InterlockedExchangeAdd64(&c.wgGameUs, u.gameUs);
    InterlockedExchangeAdd(&c.wgGameCalls, u.gameCalls);
}

// 0x659787: 0x6595B0's call of the gather 0x654790 (ECX = the candidate area), AL
static bool __fastcall WpgGatherHook(void* area) {
    WpGatherCounters& c = ts3native_wpgather;
    const LONG mode = c.wgMode;
    if (!mode) return g_wpgGatherOrig(area);
    if (!c.wgAll && !t_wpgDepth) {
        InterlockedIncrement(&c.wgOutScope);
        return g_wpgGatherOrig(area);
    }
    const LONGLONG t0 = WpsNowUs();
    InterlockedIncrement(&c.wgCalls);
    DWORD* a = (DWORD*)area;
    WpgUser u = {};
    u.a = a;
    volatile LONG* why = &c.wgOther;
    bool planned = false;
    WpgPlan plan = {};
    WpgCtx ctx = {};
    const DWORD* fc = (const DWORD*)(uintptr_t)a[16];
    const LONG seq = g_snGatherSeq;
    // the gather's own preconditions first (it returns 0 without them: the game's call does that)
    if (fc && fc[5] && fc[3] && fc[6] && a[9] <= 2 && (c.wgReady & 4)) {
        if (a[11] & 0x800) why = &c.wgWater;
        else {
            const DWORD* r = (const DWORD*)(uintptr_t)fc[6];
            const float* wm = (const float*)(uintptr_t)(fc[5] + 0xB4);
            u.r = r;
            WpgGeom g = {};
            g.orgX = wm[0], g.orgZ = wm[1], g.maxX = wm[2], g.maxZ = wm[3];
            g.w = r[0], g.h = r[1];
            WpgArea ar = {};
            memcpy(&ar.x, &a[0], 4);
            memcpy(&ar.z, &a[1], 4);
            ar.cx = a[5], ar.ex = a[6], ar.cz = a[7], ar.ez = a[8], ar.tryN = a[9];
            if (r[2] && r[3] && r[4] >= 2 && WpgPlanArea(g, ar, &plan)) {
                planned = true;
                ctx.w[0] = a[16], ctx.w[1] = fc[3], ctx.w[2] = fc[4], ctx.w[3] = fc[5], ctx.w[4] = fc[6];
                ctx.w[5] = r[0], ctx.w[6] = r[1], ctx.w[7] = r[2], ctx.w[8] = r[3], ctx.w[9] = r[4];
                memcpy(&ctx.w[10], wm, 16);
            } else why = &c.wgEdge;
        }
    }
    if (planned && !t_wpg && !t_wpgNoMem) {
        t_wpg = (WpgTh*)VirtualAlloc(NULL, sizeof(WpgTh), MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);  // zeroed
        if (!t_wpg) t_wpgNoMem = 1;
    }
    WpgTh* th = t_wpg;
    if (!planned || !th) {
        InterlockedIncrement(why);
        const bool ok = WpgGame(&u, area);
        WpgAdd(c, u, t0);
        return ok;
    }
    u.th = th;
    bool gameOk = false;
    if (mode == 2) {  // the truth first; the compare below reads it
        gameOk = WpgGame(&u, area);
        if (!gameOk || seq != g_snGatherSeq) {
            InterlockedIncrement(&c.wgOther);
            WpgAdd(c, u, t0);
            return gameOk;
        }
    }
    const int dropped = WpgValidate(&th->tab, ctx, (unsigned int)seq);
    if (dropped == 1) InterlockedIncrement(&c.wgCtxDrop);
    if (dropped == 2) InterlockedIncrement(&c.wgSeqDrop);
    WpgStats st = {};
    const LONG gameBefore = u.gameCalls;
    const int n = WpgBuild(&th->tab, plan, WpgFilterIx(a[11]), (unsigned int)GetTickCount(), (unsigned int)c.wgCapMs, WpgFill, WpgHead, &u, th->out, WPG_OUT, &st);
    InterlockedExchangeAdd(&c.wgHits, (LONG)st.hits);
    InterlockedExchangeAdd(&c.wgFills, (LONG)st.fills);
    if (st.stale) InterlockedIncrement(&c.wgStale);
    if (st.full) InterlockedIncrement(&c.wgFull);
    const bool wrote = seq != g_snGatherSeq;  // a write while the chunks were fetched: the list may mix two states
    if (wrote) WpgReset(&th->tab);
    if (mode == 2) {
        if (n >= 0 && !wrote) {
            const WpgGameRow* g = (const WpgGameRow*)(uintptr_t)a[12];
            const unsigned int cnt = (a[13] - a[12]) >> 5;
            bool same = cnt == (unsigned int)n;
            unsigned int at = 0;
            for (; same && at < cnt; at++) same = WpgRowSame(g[at], th->out[at], u.r[3]);
            InterlockedIncrement(&c.wgShadow);
            if (!same) {
                if (InterlockedIncrement(&c.wgMismatch) <= 10)
                    Log("WAYPOINT GATHER MISMATCH: game %lu rows, stored %d, first difference at row %lu; area (%08lX %08lX) class %lu/%lu extent %lu/%lu try %lu flags 0x%lX, "
                        "%d chunks from 0x%lX, %lu from the table, gather seq %ld",
                        (DWORD)cnt, n, (DWORD)(at ? at - 1 : 0), a[0], a[1], a[5], a[7], a[6], a[8], a[9], a[11], plan.n, (DWORD)plan.chunk[0], (DWORD)st.hits, seq);
                WpgReset(&th->tab);
            }
        } else InterlockedIncrement(&c.wgOther);
        WpgAdd(c, u, t0);
        return gameOk;
    }
    if (n < 0 || wrote) {
        InterlockedIncrement(&c.wgOther);
        const bool ok = WpgGame(&u, area);
        WpgAdd(c, u, t0);
        return ok;
    }
    // the gather's own effect: the vector emptied, the rows appended in order, flag 0x400 cleared, AL = 1
    a[13] = a[12];
    const WpgAppendFn append = (WpgAppendFn)(uintptr_t)WPG_APPEND;
    const DWORD base = u.r[3];
    for (int i = 0; i < n; i++) {
        const WpgRow& w = th->out[i];
        WpgGameRow g = {};
        g.x = w.x, g.z = w.z, g.wp = w.wp, g.chunk = w.chunk, g.rec = base + w.rec, g.type = w.type;
        append(a + 12, NULL, &g);
    }
    a[11] &= ~0x400u;
    InterlockedExchangeAdd(&c.wgRows, n);
    if (u.gameCalls == gameBefore) {
        InterlockedIncrement(&c.wgAvoided);
        InterlockedExchangeAdd64(&c.wgAvoidUs, WpsNowUs() - t0);
    } else InterlockedIncrement(&c.wgPartly);
    WpgAdd(c, u, t0);
    return true;
}

// after InstallWpSnap: both sites are E8 retargets that call whatever the site called
static void InstallWpGather(const wchar_t* ini) {
    WpGatherCounters& c = ts3native_wpgather;
    const int mode = Clamp(GetPrivateProfileIntW(L"WaypointSnap", L"Gather", 0, ini), 0, 2);
    if (!mode) { Log("waypoint gather off ([WaypointSnap] Gather=0)"); return; }
    if (!WPS_IC || !WPS_TRY || !WPG_APPEND) { Log("SKIP waypoint gather: no address for this build, nothing written"); return; }
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
    c.wgCapMs = Clamp(GetPrivateProfileIntW(L"WaypointSnap", L"GatherMs", 0, ini), 0, 600000);
    c.wgAll = GetPrivateProfileIntW(L"WaypointSnap", L"GatherAll", 0, ini) ? 1 : 0;
    static const u8 kAppend[] = {0x57, 0x8B, 0x79, 0x04, 0x3B, 0x79, 0x08};  // push edi; mov edi, [ecx+4]; cmp edi, [ecx+8]
    const DWORD gs = WPS_TRY + WPS_TRY_OFF, is = WPS_IC + WPS_IC_OFF;
    const u8* g = (const u8*)(uintptr_t)(gs - 2);
    const u8* tail = (const u8*)(uintptr_t)(is + 5);
    DWORD gt = 0, it = 0;
    if (!Matches((const u8*)(uintptr_t)WPG_APPEND, kAppend, sizeof(kAppend))) {
        Log("SKIP waypoint gather: 0x%08lX is not the candidate append, nothing written", WPG_APPEND);
        return;
    }
    // the gather call: mov ecx, esi; call; test al, al
    if (!Readable(g, 9) || g[0] != 0x8B || g[1] != 0xCE || !WpsCallAt(gs, &gt) || g[7] != 0x84 || g[8] != 0xC0 || !Readable((const void*)(uintptr_t)gt, 5)) {
        Log("SKIP waypoint gather: 0x%08lX is not the try's call of the gather, nothing written", gs);
        return;
    }
    // the IC's call: an E8 followed by pop esi; ret
    if (!WpsCallAt(is, &it) || !Readable(tail, 2) || tail[0] != 0x5E || tail[1] != 0xC3 || !Readable((const void*)(uintptr_t)it, 5)) {
        Log("SKIP waypoint gather: 0x%08lX is not the IC's call of the same-land test, nothing written", is);
        return;
    }
    // once, and never over our own hooks: a hook whose original is itself calls itself until the stack ends (docs/679 §8)
    if (!WpgChainPlan(&g_wpgChain, it, gt, (DWORD)(uintptr_t)WpgSameHook, (DWORD)(uintptr_t)WpgGatherHook)) {
        Log("SKIP waypoint gather: %s (0x%08lX calls 0x%08lX, 0x%08lX calls 0x%08lX), nothing written",
            g_wpgChain.done ? "installed already, a second install call" : "a call site already calls one of this feature's hooks", is, it, gs, gt);
        return;
    }
    g_wpgSameOrig = (WpsSameFn)(uintptr_t)g_wpgChain.sameOrig;
    g_wpgGatherOrig = (WpsGatherFn)(uintptr_t)g_wpgChain.gatherOrig;
    if (!WpsRetarget(is, (void*)WpgSameHook)) { Log("FAIL waypoint gather: VirtualProtect at 0x%08lX (err %lu)", is, GetLastError()); return; }
    c.wgReady |= 2;
    if (!WpsRetarget(gs, (void*)WpgGatherHook)) { Log("FAIL waypoint gather: VirtualProtect at 0x%08lX (err %lu)", gs, GetLastError()); return; }
    c.wgReady |= 1;
    if (g_snOn && !g_snGatherMiss) c.wgReady |= 4;
    c.wgMode = mode;
    Log("%s waypoint gather: ready 0x%lX; Gather=%d (%s), scope %s, stored chunks live %s%ld ms; %s", (c.wgReady & 4) ? "OK  " : "PART", c.wgReady, mode,
        mode == 2 ? "shadow: the game's gather runs and is compared" : "areas built from stored chunk rows",
        c.wgAll ? "every snap" : "ArePointsConnectedByLand only", c.wgCapMs ? "at most " : "without a limit, ", c.wgCapMs,
        (c.wgReady & 4) ? "dropped by the gather write sequence"
                        : g_snOn ? "a [RouteSnap] writer row or IC slot the gather depends on is not hooked: nothing is stored, the game's gather runs"
                                 : "[RouteSnap] counters are off: nothing is stored, the game's gather runs");
}

// the 60 s counter line (ts3native.cpp MaybeLogCounters)
static void WpgLogCounters() {
    const WpGatherCounters& c = ts3native_wpgather;
    if (!c.wgReady || !(c.wgCalls + c.wgOutScope)) return;
    Log("waypoint gather: mode %ld all %ld ready 0x%lX: gathers asked %ld, avoided %ld in %ld ms, partly %ld, game gathers %ld in %ld ms, hook %ld ms; chunks from the table %ld, "
        "gathered %ld, rows served %ld; not served: water %ld, edge %ld, other %ld; table emptied: write seq %ld, objects %ld, full %ld, stale %ld; "
        "shadow checks %ld mismatch %ld; out of scope %ld; gather seq %ld",
        c.wgMode, c.wgAll, c.wgReady, c.wgCalls, c.wgAvoided, (LONG)(c.wgAvoidUs / 1000), c.wgPartly, c.wgGameCalls, (LONG)(c.wgGameUs / 1000), (LONG)(c.wgUs / 1000), c.wgHits,
        c.wgFills, c.wgRows, c.wgWater, c.wgEdge, c.wgOther, c.wgSeqDrop, c.wgCtxDrop, c.wgFull, c.wgStale, c.wgShadow, c.wgMismatch, c.wgOutScope, g_snGatherSeq);
}
