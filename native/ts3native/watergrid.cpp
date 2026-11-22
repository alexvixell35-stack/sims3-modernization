// [WaterGrid] Prof (docs/100 §13, default 0), measurement only: where the load-time water grid G build spends its ~1 s on
// the sim thread (docs/39 §10.1: 0.92-1.07 s per load, 85-99% of the load's routing time), and whether two builds of the
// same world give the same G (the precondition of a G cache across loads). Nothing is changed or skipped: each hook times
// one call and runs the game's own function with the same arguments. Included by ts3native.cpp after values.cpp (PvRSum)
// and savewrite.cpp (PatchCalls). Steam 1.67 only: the inner call sites have no EA 1.69 / retail rows (SKIP there).
//
// Call sites (capstone on the Steam 1.67 exe, 2026-11-22 [verified]; every one an E8 to the named function, the next
// instruction reloads ecx or only tests al, so a hook that clobbers ecx/edx is safe):
//   0x6734A3 WaterGrid_Clear 0x6714A0          thiscall(G), ret        (in WaterGrid_Build 0x6734A0)
//   0x6734B9 0x6729D0 cells                     thiscall(G, P), ret 4   sample pass, shore pass, lot outlines, cuts (§3.2 1-6)
//   0x6734C4 WaterGrid_LabelBodies 0x671580     thiscall(G), ret        flood fill (§3.2 step 7)
//   0x82A0B0 WaterGrid_BuildShorelines 0x672440 thiscall(G, R, P), ret 8 (in WaterGrid_BuildFromWorld 0x82A030; step 8)
//   0x67244E WaterGrid_BuildShorelineMap 0x66FD00 thiscall(G, R, P), ret 8 (R type-bit rewrites)
//   0x67245A WaterGrid_BuildLotInternalShorelines 0x672110 thiscall(G, P), ret 4
//   0x673316 WaterGrid_CutWorldObjects 0x671000 cdecl(3), once per WM+0x9C root (loop 0x673313..0x67332B)
//   0x673330 WaterGrid_CutHardcodedPatches 0x670F50 thiscall(?, G), ret 4
// The samples share = cells - objects - patches (the inline loop 0x672B34..0x673236 plus the lot outline loop).
//
// After the shorelines (the message path's last step) one log line per build with the phase split, the grid size and an
// output fingerprint read at use (no fixed sizes, so a 4x or streamed world is covered): FNV over the header and cell
// array, over the 5 passability maps, over the 10 high-cost maps (history-dependent, docs/100 §12.1: kept apart), the
// body table, R's record sums with and without the type bits (PvRSum, values.cpp) and the three tree sizes.
// shortcut: a BuildWorldRoutingData build (0x834A82, world save / Edit Town) gets the clear/cells/label rows but no line
// when it skips 0x82A0B0; upgrade when Edit Town loads matter.

struct WaterGridCounters {
    volatile LONG on, ready, builds;  // Prof read-back, sites hooked, builds logged
    LONG us[8];                       // last build, per WG_* phase (inclusive)
    LONG hashCells, hashPass, hashHigh, rSumA, rSumB, hashUs;  // last build's fingerprint
};
extern "C" __declspec(dllexport) WaterGridCounters ts3native_watergrid = {};

#ifndef TS3A_TABLE  // Steam 1.67 only: no table rows for these sites
enum { WG_CLEAR, WG_CELLS, WG_LABEL, WG_SHORE, WG_SHOREMAP, WG_LOTSHORE, WG_OBJECTS, WG_PATCHES, WG_N };
static LONG g_wgUs[WG_N];  // the build in progress (sim thread only; reset at the clear)

static LONGLONG WgNow() { LARGE_INTEGER q; QueryPerformanceCounter(&q); return q.QuadPart; }
static LONG WgUs(LONGLONG t0) { return (LONG)((WgNow() - t0) * 1000000 / g_qpf.QuadPart); }

static inline void WgFnv(DWORD& h, const u8* p, DWORD n) {
    const DWORD* d = (const DWORD*)p;
    for (DWORD i = 0; i < n / 4; i++) { h ^= d[i]; h *= 16777619u; }
    for (DWORD i = n & ~3u; i < n; i++) { h ^= p[i]; h *= 16777619u; }
}

// EASTL vector at v: begin, end (cap, allocator follow). False when unreadable or larger than lim bytes.
static bool WgVec(const u8* v, const u8** b, DWORD* n, DWORD lim) {
    const u8* begin = *(const u8* const*)v;
    const u8* end = *(const u8* const*)(v + 4);
    if (end < begin || (DWORD)(end - begin) > lim || (begin && !Readable(begin, (size_t)(end - begin)))) return false;
    *b = begin;
    *n = (DWORD)(end - begin);
    return true;
}

// vector of byte vectors at v (16-byte elements, G+0x7B0 / G+0x7C0): one hash over every map's bytes in order
static bool WgMaps(const u8* v, DWORD* h, DWORD* count) {
    const u8* b;
    DWORD n;
    if (!WgVec(v, &b, &n, 64 * 16) || n % 16) return false;
    *count = n / 16;
    for (DWORD i = 0; i < n / 16; i++) {
        const u8* mb;
        DWORD mn;
        if (!WgVec(b + 16 * i, &mb, &mn, 256u << 20)) return false;
        WgFnv(*h, (const u8*)&mn, 4);
        WgFnv(*h, mb, mn);
    }
    return true;
}

// out: [0] header + cells + body table, [1] passability maps, [2] high-cost maps, [3] cell bytes, [4] maps (pass << 16 | high),
// [5..7] tree sizes (shorelineMap +0x40, reverse +0x5C, lot-internal +0x78 [inferred: same layout as the first two])
static bool WgHash(const u8* g, DWORD* out) {
    __try {
        DWORD h0 = 2166136261u, h1 = 2166136261u, h2 = 2166136261u, np = 0, nh = 0;
        WgFnv(h0, g, 0x1C);
        const u8* cb;
        DWORD cn;
        if (!WgVec(g + 0x1C, &cb, &cn, 512u << 20)) return false;
        WgFnv(h0, cb, cn);
        WgFnv(h0, g + 0x39C, 0x400);
        if (!WgMaps(g + 0x7B0, &h1, &np) || !WgMaps(g + 0x7C0, &h2, &nh)) return false;
        out[0] = h0, out[1] = h1, out[2] = h2, out[3] = cn, out[4] = np << 16 | nh;
        out[5] = *(const DWORD*)(g + 0x40), out[6] = *(const DWORD*)(g + 0x5C), out[7] = *(const DWORD*)(g + 0x78);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}

static void WgReport(const u8* g, const u8* r) {
    WaterGridCounters& w = ts3native_watergrid;
    const LONGLONG t0 = WgNow();
    DWORD h[8] = {}, rs[5] = {};
    const bool ok = WgHash(g, h);
    const bool rok = r && PvRSum(r, rs);
    const LONG hus = WgUs(t0);
    const LONG* u = g_wgUs;
    const LONG total = u[WG_CLEAR] + u[WG_CELLS] + u[WG_LABEL] + u[WG_SHORE];
    const LONG samples = u[WG_CELLS] - u[WG_OBJECTS] - u[WG_PATCHES];
    memcpy(w.us, u, sizeof(w.us));
    w.hashCells = (LONG)h[0], w.hashPass = (LONG)h[1], w.hashHigh = (LONG)h[2], w.rSumA = (LONG)rs[0], w.rSumB = (LONG)rs[1];
    w.hashUs = hus;
    const LONG n = InterlockedIncrement(&w.builds);
    DWORD gw = 0, gh = 0, water = 0, bodies = 0;
    __try { gw = *(const DWORD*)(g + 4), gh = *(const DWORD*)(g + 8), water = *(const DWORD*)(g + 0x10), bodies = *(const DWORD*)(g + 0x14); }
    __except (EXCEPTION_EXECUTE_HANDLER) {}
    Log("water grid build %ld: %ld ms = clear %ld + cells %ld (samples %ld, objects %ld, patches %ld) + label %ld + shorelines %ld "
        "(map %ld, lot %ld) ms; grid %lux%lu water %lu bodies %lu",
        n, total / 1000, u[WG_CLEAR] / 1000, u[WG_CELLS] / 1000, samples / 1000, u[WG_OBJECTS] / 1000, u[WG_PATCHES] / 1000,
        u[WG_LABEL] / 1000, u[WG_SHORE] / 1000, u[WG_SHOREMAP] / 1000, u[WG_LOTSHORE] / 1000, gw, gh, water, bodies);
    if (ok)
        Log("water grid build %ld hash: cells 0x%08lX pass 0x%08lX high 0x%08lX (%lu cell bytes, %lu/%lu maps), R 0x%08lX/0x%08lX%s, "
            "trees %lu/%lu/%lu, %ld us",
            n, h[0], h[1], h[2], h[3], h[4] >> 16, h[4] & 0xFFFF, rs[0], rs[1], rok ? "" : " (unread)", h[5], h[6], h[7], hus);
    else
        Log("water grid build %ld hash: G at %p unreadable", n, g);
}

typedef DWORD(__fastcall* WgThis0)(void* g, void* edx);
typedef DWORD(__fastcall* WgThis1)(void* g, void* edx, DWORD a);
typedef DWORD(__fastcall* WgThis2)(void* g, void* edx, DWORD a, DWORD b);
typedef DWORD(__cdecl* WgCdecl3)(DWORD a, DWORD b, DWORD c);
static WgThis0 g_wgClear = (WgThis0)(uintptr_t)TS3A(0x006714A0), g_wgLabel = (WgThis0)(uintptr_t)TS3A(0x00671580);
static WgThis1 g_wgCells = (WgThis1)(uintptr_t)TS3A(0x006729D0), g_wgLotShore = (WgThis1)(uintptr_t)TS3A(0x00672110),
               g_wgPatches = (WgThis1)(uintptr_t)TS3A(0x00670F50);
static WgThis2 g_wgShore = (WgThis2)(uintptr_t)TS3A(0x00672440), g_wgShoreMap = (WgThis2)(uintptr_t)TS3A(0x0066FD00);
static WgCdecl3 g_wgObjects = (WgCdecl3)(uintptr_t)TS3A(0x00671000);

#define WG_TIMED(id, call)              \
    const LONGLONG t0 = WgNow();        \
    const DWORD r = call;               \
    g_wgUs[id] += WgUs(t0);             \
    return r
static DWORD __fastcall WgClearHook(void* g, void* edx) {
    memset(g_wgUs, 0, sizeof(g_wgUs));
    WG_TIMED(WG_CLEAR, g_wgClear(g, edx));
}
static DWORD __fastcall WgCellsHook(void* g, void* edx, DWORD p) { WG_TIMED(WG_CELLS, g_wgCells(g, edx, p)); }
static DWORD __fastcall WgLabelHook(void* g, void* edx) { WG_TIMED(WG_LABEL, g_wgLabel(g, edx)); }
static DWORD __fastcall WgShoreMapHook(void* g, void* edx, DWORD a, DWORD b) { WG_TIMED(WG_SHOREMAP, g_wgShoreMap(g, edx, a, b)); }
static DWORD __fastcall WgLotShoreHook(void* g, void* edx, DWORD p) { WG_TIMED(WG_LOTSHORE, g_wgLotShore(g, edx, p)); }
static DWORD __fastcall WgPatchesHook(void* x, void* edx, DWORD g) { WG_TIMED(WG_PATCHES, g_wgPatches(x, edx, g)); }
static DWORD __cdecl WgObjectsHook(DWORD a, DWORD b, DWORD c) { WG_TIMED(WG_OBJECTS, g_wgObjects(a, b, c)); }
#undef WG_TIMED
static DWORD __fastcall WgShoreHook(void* g, void* edx, DWORD r, DWORD p) {
    const LONGLONG t0 = WgNow();
    const DWORD res = g_wgShore(g, edx, r, p);
    g_wgUs[WG_SHORE] += WgUs(t0);
    if (ts3native_watergrid.on) WgReport((const u8*)g, (const u8*)(uintptr_t)r);  // r = world+0x54 (0x82A0A9 [verified])
    return res;
}

static const CallSite kWgSites[WG_N] = {
    {TS3A(0x006734A3), TS3A(0x006714A0), (void*)WgClearHook},    {TS3A(0x006734B9), TS3A(0x006729D0), (void*)WgCellsHook},
    {TS3A(0x006734C4), TS3A(0x00671580), (void*)WgLabelHook},    {TS3A(0x0082A0B0), TS3A(0x00672440), (void*)WgShoreHook},
    {TS3A(0x0067244E), TS3A(0x0066FD00), (void*)WgShoreMapHook}, {TS3A(0x0067245A), TS3A(0x00672110), (void*)WgLotShoreHook},
    {TS3A(0x00673316), TS3A(0x00671000), (void*)WgObjectsHook},  {TS3A(0x00673330), TS3A(0x00670F50), (void*)WgPatchesHook}};

#endif

// [WaterGrid] Prof=1: the eight sites are byte-checked (E8 to the expected target) and retargeted; Prof=0 writes nothing.
// `on` is also the runtime switch for the report (export +0x00); the timers keep running while it is 0.
static bool ApplyWaterGrid(const wchar_t* ini) {
    if (!GetPrivateProfileIntW(L"WaterGrid", L"Prof", 0, ini)) { Log("water grid prof off ([WaterGrid] Prof=0)"); return false; }
#ifdef TS3A_TABLE
    Log("SKIP water grid prof: Steam 1.67 only (no EA 1.69 / retail rows for the inner call sites)");
    return false;
#else
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
    if (!PatchCalls("water grid prof (G build phases)", kWgSites, WG_N)) return false;
    ts3native_watergrid.ready = 1;
    ts3native_watergrid.on = 1;
    Log("OK   water grid prof: G build phase split + output hash per build (Prof=1, docs/100 §13)");
    return true;
#endif
}
