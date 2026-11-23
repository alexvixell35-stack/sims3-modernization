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
    // [WaterGrid] Par (docs/100 §14): mode read-back (0 off, 1 check, 2 apply), passes run, check mismatches, fallbacks
    // to the serial loop, last pass's deferred samples, parallel us, serial us (check mode), workers
    volatile LONG parMode, parRuns, parMismatch, parFallback, parDeferred, parUs, parSerialUs, parWorkers;
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

// ---- [WaterGrid] Diff (docs/100 §14, default 0, needs Prof=1): which G bytes differ between two loads of one world ----
// Load 1 writes a sidecar dump (G's 0x7EC header bytes + the cell vector) next to the ini, keyed by the passability-map
// hash (equal across loads of one world, different between worlds: m1122 [measured]); load 2 of the same key diffs
// against it, names the differing header fields and cell parts, then overwrites the dump. Nothing in G is written.
static const DWORD WG_GBYTES = 0x7EC;
static wchar_t g_wgDir[MAX_PATH];  // ini folder with a trailing backslash; empty = Diff off
static int g_wgDiffMode;           // 1 = diff against the dump then rewrite it, 2 = rewrite only (a queue's first load)
struct WgDumpHdr { DWORD magic, key, w, h, gBytes, cellBytes; FILETIME when; DWORD pid; };
struct WgField { WORD off, len; const char* name; };
// docs/100 §2; pointer-holding fields are tagged "ptr" (a heap address can differ with equal content)
static const WgField kWgFields[] = {
    {0x00, 1, "built"},          {0x01, 1, "shoreBuilt"},        {0x02, 2, "pad02"},          {0x04, 4, "width"},
    {0x08, 4, "height"},         {0x0C, 4, "cellCount"},         {0x10, 4, "waterCells"},     {0x14, 4, "bodies"},
    {0x18, 4, "shoreCount"},     {0x1C, 0x10, "cells vec ptr"},  {0x2C, 0x14, "shoreMap ptr"}, {0x40, 4, "shoreMap size"},
    {0x44, 4, "shoreMap tail"},  {0x48, 0x14, "revShore ptr"},   {0x5C, 4, "revShore size"},  {0x60, 4, "revShore tail"},
    {0x64, 0x14, "lotShore ptr"}, {0x78, 4, "lotShore size"},    {0x7C, 4, "lotShore tail"},  {0x80, 0x2DC, "houseboat sets ptr"},
    {0x35C, 0x20, "blockedNodeMap ptr"}, {0x37C, 0x20, "nbr deltas"}, {0x39C, 0x400, "body table"}, {0x79C, 4, "P ptr"},
    {0x7A0, 0x10, "pass dims"},  {0x7B0, 0x10, "pass maps ptr"}, {0x7C0, 0x10, "high maps ptr"}, {0x7D0, 0x1C, "hcFootprints ptr"}};

// Canonical cell hash (the exactness key a cache needs): the value fields of the header (no padding, no pointers), the cell
// array and the body table slots in use: 1..bodies, and the overflow slot 0x7F only once 126 ids are used (no store
// reaches slot 0, slots above the count or 0x7F without overflow: ctor 0x671E30, clear 0x6714A0, label 0x6718C2 [verified],
// so they hold whatever the allocation left; 0x7F cells also come from 0x66FD00's shoreline marks, not only overflow).
static bool WgCanon(const u8* g, DWORD* out) {
    __try {
        DWORD h = 2166136261u;
        WgFnv(h, g, 2);
        WgFnv(h, g + 4, 0x18);
        const u8* cb;
        DWORD cn;
        if (!WgVec(g + 0x1C, &cb, &cn, 512u << 20)) return false;
        WgFnv(h, cb, cn);
        const DWORD bodies = *(const DWORD*)(g + 0x14);
        WgFnv(h, g + 0x39C + 8, 8 * (bodies > 0x7E ? 0x7E : bodies));
        if (bodies >= 0x7E) WgFnv(h, g + 0x39C + 8 * 0x7F, 8);  // [inferred] overflow in use once the ids run out
        *out = h;
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}

// Diff of two dumps of one world: a = earlier load, b = this load (G bytes then cells). Logs and returns the counts
// (selftest reads them): differing header fields, cells, cells with only the low byte (body id / 0x7F) or only the flags.
struct WgDiffOut { DWORD fields, ptrFields, cells, lowOnly, highOnly, flagXor, bodySlots, bodySlotsUnused; };
static WgDiffOut WgDiffDumps(const u8* ga, const u8* ca, const u8* gb, const u8* cb, DWORD cn, DWORD w) {
    WgDiffOut o = {};
    const DWORD bodies = *(const DWORD*)(gb + 0x14);
    char line[512];
    int len = 0;
    for (const WgField& f : kWgFields) {
        if (!memcmp(ga + f.off, gb + f.off, f.len)) continue;
        o.fields++;
        if (strstr(f.name, "ptr")) o.ptrFields++;
        if (f.off == 0x39C) {  // body table: per slot, in use or not
            for (DWORD id = 0; id < 0x80; id++)
                if (memcmp(ga + 0x39C + 8 * id, gb + 0x39C + 8 * id, 8)) {
                    o.bodySlots++;
                    if (id == 0 || (id > bodies && id != 0x7F)) o.bodySlotsUnused++;
                }
        }
        if (len < 400) len += sprintf_s(line + len, sizeof(line) - len, "%s%s", len ? ", " : "", f.name);
    }
    DWORD shown = 0;
    char ex[512];
    int el = 0;
    for (DWORD i = 0; i + 1 < cn; i += 2) {
        const bool lo = ca[i] != cb[i], hi = ca[i + 1] != cb[i + 1];
        if (!lo && !hi) continue;
        o.cells++;
        if (lo && !hi) o.lowOnly++;
        if (hi && !lo) o.highOnly++;
        o.flagXor |= (DWORD)(ca[i + 1] ^ cb[i + 1]);
        if (shown++ < 8 && el < 400) {
            const DWORD c = i / 2;
            el += sprintf_s(ex + el, sizeof(ex) - el, " (%lu,%lu) %02X%02X->%02X%02X", c % (w ? w : 1), c / (w ? w : 1), ca[i + 1], ca[i],
                            cb[i + 1], cb[i]);
        }
    }
    ex[el] = 0;
    line[len] = 0;
    Log("water grid diff: %lu header fields differ (%lu pointer-only)%s%s; body slots %lu (%lu unused); cells %lu (low byte only %lu, "
        "flags only %lu, flag bits 0x%02lX)%s",
        o.fields, o.ptrFields, o.fields ? ": " : "", line, o.bodySlots, o.bodySlotsUnused, o.cells, o.lowOnly, o.highOnly, o.flagXor,
        o.cells ? ";" : "");
    if (o.cells) Log("water grid diff cells (x,z) flags|low before->after:%s", ex);
    return o;
}

static void WgDumpPath(wchar_t* p, DWORD key) { swprintf_s(p, MAX_PATH, L"%sts3native-wgdiff-%08lX.bin", g_wgDir, key); }

// key = passability hash; returns false when nothing could be read or written (logged)
static bool WgDiffAndDump(const u8* g, DWORD key) {
    static u8 gb[WG_GBYTES];
    const u8* cb;
    DWORD cn, w;
    __try {
        memcpy(gb, g, WG_GBYTES);
        w = *(const DWORD*)(g + 4);
        if (!WgVec(g + 0x1C, &cb, &cn, 512u << 20)) { Log("water grid diff: cell vector unreadable"); return false; }
    } __except (EXCEPTION_EXECUTE_HANDLER) { Log("water grid diff: G unreadable"); return false; }
    DWORD canon = 0;
    WgCanon(g, &canon);
    wchar_t path[MAX_PATH];
    WgDumpPath(path, key);
    HANDLE f = g_wgDiffMode == 2 ? INVALID_HANDLE_VALUE : CreateFileW(path, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, 0, NULL);
    if (f != INVALID_HANDLE_VALUE) {
        WgDumpHdr d = {};
        DWORD got = 0;
        u8* old = NULL;
        if (ReadFile(f, &d, sizeof(d), &got, NULL) && got == sizeof(d) && d.magic == 'WGD1' && d.gBytes == WG_GBYTES && d.cellBytes == cn) {
            old = (u8*)malloc(WG_GBYTES + cn);
            if (old && !(ReadFile(f, old, WG_GBYTES + cn, &got, NULL) && got == WG_GBYTES + cn)) free(old), old = NULL;
        }
        CloseHandle(f);
        if (old) {
            DWORD oldCanon = 0;  // canonical hash of the earlier load, recomputed from its dump (vector pointer fixed up)
            static u8 ga[WG_GBYTES];
            memcpy(ga, old, WG_GBYTES);
            *(const u8**)(ga + 0x1C) = old + WG_GBYTES, *(const u8**)(ga + 0x20) = old + WG_GBYTES + cn;
            WgCanon(ga, &oldCanon);
            Log("water grid diff vs load of pid %lu: canon 0x%08lX -> 0x%08lX (%s)", d.pid, oldCanon, canon, oldCanon == canon ? "equal" : "differ");
            WgDiffDumps(old, old + WG_GBYTES, gb, cb, cn, w);
            free(old);
        } else
            Log("water grid diff: dump %08lX unusable (other size or version), rewritten", key);
    } else
        Log("water grid diff: first load of key %08lX (canon 0x%08lX), dump written for the next load", key, canon);
    f = CreateFileW(path, GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, 0, NULL);
    if (f == INVALID_HANDLE_VALUE) { Log("water grid diff: cannot write dump (error %lu)", GetLastError()); return false; }
    WgDumpHdr d = {'WGD1', key, w, *(const DWORD*)(gb + 8), WG_GBYTES, cn, {}, GetCurrentProcessId()};
    GetSystemTimeAsFileTime(&d.when);
    DWORD put = 0;
    bool ok = WriteFile(f, &d, sizeof(d), &put, NULL) && WriteFile(f, gb, WG_GBYTES, &put, NULL);
    __try { ok = ok && WriteFile(f, cb, cn, &put, NULL); } __except (EXCEPTION_EXECUTE_HANDLER) { ok = false; }
    CloseHandle(f);
    return ok;
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
    if (ok && g_wgDir[0]) WgDiffAndDump(g, h[1]);  // [WaterGrid] Diff=1
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

// ---- [WaterGrid] Par (docs/100 §14, default 0): the 1 m sample loop of the G build on worker threads ----
// The loop 0x672B75..0x672F54 (inside 0x6729D0, sim thread, docs/100 §14.2 [verified]) visits every 1 m sample (x outer,
// z inner, from the world minimum + 0.5f in steps of 1.0f), writes only its own cell (WORD at (trunc z >> 2) * w +
// (trunc x >> 2)), the water count G+0x10, and that cell's 5 passability bytes (0x66E040, a function of the cell alone).
// Each cell's end value is order-free (flags OR-ed, a land sample zeroes the low byte), so column bands with disjoint
// cells can run on workers. The per-sample callees are read-only except two rare paths (docs/100 §14.2): a pond polygon
// hit (0xB5FC40 -> 0xC6A250) and a low-bridge box hit (0xB74D10 -> 0xB74A20). Workers run only the read-only parts and
// hand a sample with either hit back to the sim thread, which runs the game's full calls for it after the join.
//   Par=1 check: the game's serial loop runs and its result is used; at its end our pass runs into a scratch copy of the
//                cells as they were at the loop's entry, and every cell plus the water-count delta is compared (log line).
//   Par=2 apply: our pass runs into the scratch copy; on success it is copied into G, the water count is adjusted and
//                0x66E040 runs once per written cell on the sim thread, then the game resumes after the loop (0x672F5A);
//                on any failure (exception, timeout, a column outside the grid, constants differ) the game's own loop runs.
// Changes no Sim: load-time water grid only, so no household / selected-Sim exemption applies; the output must equal
// the serial loop's for every Sim alike (check mode is the proof).
typedef u8(__fastcall* WgRoutableFn)(void* wm, void* edx, float x, float z);                                   // 0xC6BBC0, ret 8
typedef void*(__fastcall* WgPondFn)(void* wm, void* edx, float x, float z, DWORD flag);                        // 0xC60E70, ret 0xC
typedef u8(__fastcall* WgPolyFn)(void* poly, void* edx, float x, float z);                                     // 0xB5FAB0, ret 8
typedef u8(__fastcall* WgBridgeFn)(void* b, void* edx, float x, float z, DWORD a, DWORD b2, DWORD c, DWORD d); // 0xB74D10, ret 0x18
typedef void*(__fastcall* WgLotFn)(void* wm, void* edx, float x, float z);                                     // 0xC68990, ret 8
typedef void(__fastcall* WgPassFn)(void* g, void* edx, DWORD idx);                                             // 0x66E040, ret 4
static WgRoutableFn g_wgRoutable = (WgRoutableFn)(uintptr_t)TS3A(0x00C6BBC0);
static WgPondFn g_wgPond = (WgPondFn)(uintptr_t)TS3A(0x00C60E70);
static WgPolyFn g_wgPoly = (WgPolyFn)(uintptr_t)TS3A(0x00B5FAB0);
static WgBridgeFn g_wgBridge = (WgBridgeFn)(uintptr_t)TS3A(0x00B74D10);
static WgLotFn g_wgLot = (WgLotFn)(uintptr_t)TS3A(0x00C68990);
static WgPassFn g_wgPass = (WgPassFn)(uintptr_t)TS3A(0x0066E040);
static DWORD g_wgHeightFn = TS3A(0x00C29EE0);  // thiscall(WM+0x54 object; x, z, hm), ret 0xC, height in ST0

// The game's compare (0x672CB4..0x672CC2): h stored as a float for the depth path, then the unrounded ST0 against the
// float sea height; above = h > sea (an unordered compare counts as not above, as the game's jbe).
static bool WgHeightAboveAsm(void* obj, float x, float z, void* hm, float sea, float* hf) {
    const DWORD fn = g_wgHeightFn;
    float h;
    u8 above;
    __asm {
        push hm
        push z
        push x
        mov ecx, obj
        call fn
        fst h
        fld sea
        fxch st(1)
        fcomip st, st(1)
        fstp st(0)
        seta above
    }
    *hf = h;
    return above != 0;
}
// Called through a volatile pointer: the compiler must then assume the standard x86 ABI (xmm0-7 clobbered) instead of
// its own view of the asm block, which does not see the game callee's SSE use (it clobbered a caller's z in xmm).
typedef bool (*WgHeightAboveFn)(void*, float, float, void*, float, float*);
static WgHeightAboveFn volatile g_wgHeightAbove = WgHeightAboveAsm;
#define WgHeightAbove g_wgHeightAbove

struct WgParCtx {
    u8* wm;
    u8* g;
    void* hmObj;          // [WM+0x54]
    void* hm;             // loop [esp+0x18]
    float sea;            // loop [esp+0x24]
    void* const* br0;     // low-bridge pointers, loop [esp+0x80] / [esp+0x84]
    void* const* br1;
    float* xs;            // x per column, the game's float accumulation (sized at use)
    DWORD* cxs;           // trunc(x) >> 2 per column
    DWORD nx;
    float* zs;            // z per row of samples
    DWORD nz;
    DWORD w, count;       // G+0x04, G+0x0C read at the loop's entry
    WORD* cells;           // the scratch cell array the pass writes
    u8* written;          // 1 per cell written at least once (0x66E040 runs for those)
};
enum { WGS_DONE, WGS_DEFER };

static inline void WgLand(const WgParCtx& c, DWORD idx, LONG* dec) {
    if (idx >= c.count) return;
    if (c.cells[idx] & 0xFF) (*dec)++;
    c.cells[idx] &= 0xFF00;
    c.written[idx] = 1;
}
static inline void WgOr(const WgParCtx& c, DWORD idx, u8 f) {
    if (idx >= c.count) return;
    c.cells[idx] |= (WORD)(f << 8);
    c.written[idx] = 1;
}

// One sample, the game's branch order (docs/100 §14.2). worker: never call the two non-read-only paths; return WGS_DEFER
// before any write when a pond polygon or a bridge box is hit (the sim thread then runs this sample with full = the
// game's own calls).
static int WgSample(const WgParCtx& c, float x, DWORD cx, float z, bool worker, LONG* dec) {
    const DWORD idx = ((DWORD)(LONG)z >> 2) * c.w + cx;
    if (!g_wgRoutable(c.wm, NULL, x, z)) {
        WgLand(c, idx, dec);
        return WGS_DONE;  // the land path skips the lot lookup
    }
    float hf;
    if (WgHeightAbove(c.hmObj, x, z, c.hm, c.sea, &hf)) {
        void* pond = NULL;
        if (worker) {
            void* const* p0 = *(void* const* const*)(c.wm + 0x100);
            void* const* p1 = *(void* const* const*)(c.wm + 0x104);
            for (void* const* p = p0; p < p1; p++)
                if (g_wgPoly((u8*)*p + 0x480, NULL, x, z)) return WGS_DEFER;
        } else
            pond = g_wgPond(c.wm, NULL, x, z, 0);
        if (!pond) WgLand(c, idx, dec);  // then the lot lookup, as the game (0x672EBB falls into 0x672EC3)
    } else {
        const float d = c.sea - hf;
        u8 f = 0;
        if (0.75f > d)
            f = 0x1E;  // 0x02, then 0x04|0x08|0x10 (0.75 > d implies 1.5 > d)
        else if (1.5f > d)
            f = 0x1C;
        else
            for (void* const* b = c.br0; b < c.br1; b++) {
                if (worker) {
                    const u8* box = *(const u8* const*)((const u8*)*b + 0x38);  // 0xB74D1D: null = false
                    if (box && x >= *(const float*)(box + 0x10) && z >= *(const float*)(box + 0x14) && *(const float*)(box + 0x18) > x &&
                        *(const float*)(box + 0x1C) > z)
                        return WGS_DEFER;
                } else if (g_wgBridge(*b, NULL, x, z, 1, 0, 0, 0)) {
                    f = 0x1C;
                    break;
                }
            }
        if (f) WgOr(c, idx, f);
    }
    if (g_wgLot(c.wm, NULL, x, z)) WgOr(c, idx, 0x01);
    return WGS_DONE;
}

struct WgWorker {
    const WgParCtx* c;
    DWORD i0, i1;          // column range
    DWORD* defer;          // (column << 16 | row) would cap a 65,536 m world: two DWORDs per entry instead
    DWORD nDefer, capDefer;
    LONG dec;
    volatile LONG failed;
    WORD cw;
    DWORD mxcsr;
};

static DWORD WINAPI WgWorkerMain(void* arg) {
    WgWorker& k = *(WgWorker*)arg;
    const WORD cw = k.cw;
    const DWORD mx = k.mxcsr;
    __asm {
        fldcw cw
        ldmxcsr mx
    }
    __try {
        const WgParCtx& c = *k.c;
        for (DWORD i = k.i0; i < k.i1; i++)
            for (DWORD j = 0; j < c.nz; j++)
                if (WgSample(c, c.xs[i], c.cxs[i], c.zs[j], true, &k.dec) == WGS_DEFER) {
                    if (k.nDefer + 2 > k.capDefer) {
                        const DWORD cap = k.capDefer ? 2 * k.capDefer : 1024;
                        DWORD* p = (DWORD*)realloc(k.defer, cap * sizeof(DWORD));
                        if (!p) { k.failed = 1; return 0; }
                        k.defer = p, k.capDefer = cap;
                    }
                    k.defer[k.nDefer++] = i, k.defer[k.nDefer++] = j;
                }
    } __except (EXCEPTION_EXECUTE_HANDLER) { k.failed = 1; }
    return 0;
}

static int g_wgParWorkers = 4;  // [WaterGrid] ParWorkers (0 = auto at apply)
static const char kWgTimeout[] = "worker timeout";  // WgParRun's why when workers may still run (scratch left to them)
static DWORD g_wgParTimeoutMs = 30000;

// The whole pass into c.cells / c.written: column bands on n workers (band edges on cell-column boundaries so no cell
// is shared), then the deferred samples on this thread with the game's own calls. False = no usable result (logged by
// the caller); the scratch arrays may then be left to a timed-out worker (leaked on purpose).
static bool WgParRun(const WgParCtx& c, int n, LONG* dec, DWORD* deferred, const char** why) {
    *dec = 0, *deferred = 0;
    if (n < 1) n = 1;
    if (n > 64) n = 64;
    WgWorker* k = (WgWorker*)calloc(n, sizeof(WgWorker));
    HANDLE* th = (HANDLE*)calloc(n, sizeof(HANDLE));
    if (!k || !th) { free(k), free(th); *why = "no memory"; return false; }
    WORD cw;
    DWORD mx;
    __asm {
        fnstcw cw
        stmxcsr mx
    }
    DWORD at = 0;
    int started = 0;
    for (int t = 0; t < n; t++) {
        DWORD end = t == n - 1 ? c.nx : (DWORD)((ULONGLONG)c.nx * (t + 1) / n);
        while (end > at && end < c.nx && c.cxs[end] == c.cxs[end - 1]) end++;  // keep a cell column in one band
        k[t].c = &c, k[t].i0 = at, k[t].i1 = end < at ? at : end, k[t].cw = cw, k[t].mxcsr = mx;
        at = k[t].i1;
        th[t] = CreateThread(NULL, 256 << 10, WgWorkerMain, &k[t], STACK_SIZE_PARAM_IS_A_RESERVATION, NULL);
        if (!th[t]) break;
        started++;
    }
    bool ok = started == n;
    if (started) {
        const DWORD r = WaitForMultipleObjects((DWORD)started, th, TRUE, g_wgParTimeoutMs);
        if (r == WAIT_TIMEOUT) { *why = kWgTimeout; return false; }  // shortcut: leaks k / th / scratch; never seen
    }
    for (int t = 0; t < started; t++) CloseHandle(th[t]);
    if (!ok) *why = "thread start failed";
    for (int t = 0; ok && t < n; t++)
        if (k[t].failed) ok = false, *why = "worker exception or no memory";
    if (ok) {
        __try {
            for (int t = 0; t < n; t++) {
                *dec += k[t].dec;
                for (DWORD e = 0; e + 1 < k[t].nDefer; e += 2) {
                    const DWORD i = k[t].defer[e], j = k[t].defer[e + 1];
                    WgSample(c, c.xs[i], c.cxs[i], c.zs[j], false, dec);
                    (*deferred)++;
                }
            }
        } __except (EXCEPTION_EXECUTE_HANDLER) { ok = false, *why = "exception in a deferred sample"; }
    }
    for (int t = 0; t < n; t++) free(k[t].defer);
    free(k), free(th);
    return ok;
}

// Column / row coordinates as the game accumulates them (SSE float: start + 0.5f, += 1.0f while max > v).
static float* WgAxis(float lo, float hi, DWORD* n) {
    DWORD cnt = 0;
    for (float v = lo + 0.5f; hi > v && cnt < (64u << 20); v += 1.0f) cnt++;
    float* a = (float*)malloc((cnt ? cnt : 1) * sizeof(float));
    if (!a) return NULL;
    DWORD i = 0;
    for (float v = lo + 0.5f; i < cnt; v += 1.0f) a[i++] = v;
    *n = cnt;
    return a;
}

static int g_wgParMode;  // 0 off, 1 check, 2 apply
static struct {
    WgParCtx c;
    WORD* pre;              // check mode: the cells at the loop's entry
    LONG preWater;
    LONGLONG t0;
    bool armed;
} g_wgPar;
static volatile u8 g_wgParTook;  // entry stub: 1 = our pass replaced the loop
static void WgParFree(WgParCtx& c) {
    free(c.xs), free(c.cxs), free(c.zs), free(c.cells), free(c.written);
    memset(&c, 0, sizeof(c));
}

// Sizes and inputs read at use (no world-size constant): bounds WM+0xB4..+0xC0, grid G+0x04 / +0x0C, the cell vector.
static bool WgParSetup(WgParCtx& c, u8* wm, u8* g, const u8* sp, const char** why) {
    memset(&c, 0, sizeof(c));
    c.wm = wm, c.g = g;
    c.hmObj = *(void* const*)(wm + 0x54), c.hm = *(void* const*)(sp + 0x18), c.sea = *(const float*)(sp + 0x24);
    c.br0 = *(void* const* const*)(sp + 0x80), c.br1 = *(void* const* const*)(sp + 0x84);
    c.w = *(const DWORD*)(g + 4), c.count = *(const DWORD*)(g + 0x0C);
    const WORD* cells = *(const WORD* const*)(g + 0x1C);
    if (!cells || !c.w || (DWORD)((*(const u8* const*)(g + 0x20)) - (const u8*)cells) < 2 * c.count) { *why = "cell vector"; return false; }
    c.xs = WgAxis(*(const float*)(wm + 0xB4), *(const float*)(wm + 0xBC), &c.nx);
    c.zs = WgAxis(*(const float*)(wm + 0xB8), *(const float*)(wm + 0xC0), &c.nz);
    c.cxs = (DWORD*)malloc((c.nx ? c.nx : 1) * sizeof(DWORD));
    c.cells = (WORD*)malloc(2 * (size_t)c.count + 2);
    c.written = (u8*)calloc((size_t)c.count + 1, 1);
    if (!c.xs || !c.zs || !c.cxs || !c.cells || !c.written) { *why = "no memory"; return false; }
    for (DWORD i = 0; i < c.nx; i++) {
        c.cxs[i] = (DWORD)(LONG)c.xs[i] >> 2;
        if (c.cxs[i] >= c.w) { *why = "a sample column outside the grid width"; return false; }  // bands would share cells
    }
    memcpy(c.cells, cells, 2 * (size_t)c.count);
    return true;
}

static LONG WgParNext() { return InterlockedIncrement(&ts3native_watergrid.parRuns); }

// Entry stub target (sim thread, loop entry 0x672B75): check mode snapshots, apply mode runs the pass. Returns via
// g_wgParTook: 1 = the loop is done (resume at 0x672F5A), 0 = run the game's loop.
static void __cdecl WgParEntry(u8* wm, u8* g, const u8* sp) {
    g_wgParTook = 0;
    WaterGridCounters& k = ts3native_watergrid;
    const char* why = "?";
    WgParCtx& c = g_wgPar.c;
    WgParFree(c);
    g_wgPar.armed = false;
    bool ok = false;
    __try { ok = WgParSetup(c, wm, g, sp, &why); } __except (EXCEPTION_EXECUTE_HANDLER) { why = "inputs unreadable"; }
    if (!ok) {
        InterlockedIncrement(&k.parFallback);
        Log("water grid par build %ld: fallback to the serial loop (%s)", WgParNext(), why);
        WgParFree(c);
        return;
    }
    g_wgPar.t0 = WgNow();
    if (g_wgParMode == 1) {
        free(g_wgPar.pre);
        g_wgPar.pre = (WORD*)malloc(2 * (size_t)c.count + 2);
        if (!g_wgPar.pre) { WgParFree(c); return; }
        memcpy(g_wgPar.pre, c.cells, 2 * (size_t)c.count);
        g_wgPar.preWater = *(const LONG*)(g + 0x10);
        g_wgPar.armed = true;
        return;
    }
    // apply: the lot grid must be clean before workers read it (0xC68990 rebuilds it under a lock when WM+0x1CC is set);
    // one lookup here does the rebuild the game's first lookup would do
    __try { g_wgLot(wm, NULL, c.nx ? c.xs[0] : 0.5f, c.nz ? c.zs[0] : 0.5f); } __except (EXCEPTION_EXECUTE_HANDLER) {}
    LONG dec = 0;
    DWORD deferred = 0;
    if (wm[0x1CC]) ok = false, why = "lot grid still dirty";
    else ok = WgParRun(c, g_wgParWorkers, &dec, &deferred, &why);
    const LONG us = WgUs(g_wgPar.t0);
    const LONG n = WgParNext();
    if (!ok || wm[0x1CC]) {
        InterlockedIncrement(&k.parFallback);
        Log("water grid par build %ld: fallback to the serial loop (%s)", n, ok ? "lot grid dirtied during the pass" : why);
        if (why == kWgTimeout) memset(&c, 0, sizeof(c));  // a timed-out worker may still write the scratch: leave it
        else WgParFree(c);
        return;
    }
    WORD* cells = *(WORD**)(g + 0x1C);
    memcpy(cells, c.cells, 2 * (size_t)c.count);
    *(LONG*)(g + 0x10) -= dec;
    for (DWORD i = 0; i < c.count; i++)
        if (c.written[i]) g_wgPass(g, NULL, i);
    k.parDeferred = (LONG)deferred, k.parUs = us;
    Log("water grid par build %ld: apply, %lu cells, parallel %ld ms (%d workers), %lu deferred, water -%ld", n, c.count, us / 1000,
        g_wgParWorkers, deferred, dec);
    WgParFree(c);
    g_wgParTook = 1;
}

// End stub target (sim thread, 0x672F5A, check mode only): the game's serial result is in G; run our pass from the
// entry snapshot and compare. Nothing in G is written.
static void __cdecl WgParEnd(u8* g, const u8* sp) {
    (void)sp;
    if (!g_wgPar.armed) return;
    g_wgPar.armed = false;
    WaterGridCounters& k = ts3native_watergrid;
    WgParCtx& c = g_wgPar.c;
    const LONG serialUs = WgUs(g_wgPar.t0);
    const LONGLONG p0 = WgNow();
    LONG dec = 0;
    DWORD deferred = 0;
    const char* why = "?";
    const bool ok = WgParRun(c, g_wgParWorkers, &dec, &deferred, &why);
    const LONG us = WgUs(p0);
    const LONG n = WgParNext();
    if (!ok) {
        InterlockedIncrement(&k.parFallback);
        Log("water grid par build %ld: check not run (%s)", n, why);
        if (why == kWgTimeout) memset(&c, 0, sizeof(c)); else WgParFree(c);
        return;
    }
    DWORD bad = 0, first = 0;
    const WORD* game = *(const WORD* const*)(g + 0x1C);
    __try {
        for (DWORD i = 0; i < c.count; i++)
            if (game[i] != c.cells[i] && !bad++) first = i;
    } __except (EXCEPTION_EXECUTE_HANDLER) { bad = 0xFFFFFFFF; }
    const LONG gameDec = g_wgPar.preWater - *(const LONG*)(g + 0x10);
    k.parDeferred = (LONG)deferred, k.parUs = us, k.parSerialUs = serialUs;
    if (!bad && gameDec == dec)
        Log("water grid par build %ld: check equal, %lu cells, serial %ld ms, parallel %ld ms (%d workers), 0 mismatches, %lu deferred, "
            "water -%ld", n, c.count, serialUs / 1000, us / 1000, g_wgParWorkers, deferred, dec);
    else {
        InterlockedIncrement(&k.parMismatch);
        Log("water grid par build %ld: check MISMATCH, %lu cells, serial %ld ms, parallel %ld ms (%d workers), %lu mismatches, first "
            "(%lu,%lu) %04X->%04X, water -%ld vs -%ld, %lu deferred",
            n, c.count, serialUs / 1000, us / 1000, g_wgParWorkers, bad, first % c.w, first / c.w, bad ? game[first] : 0,
            bad ? c.cells[first] : 0, gameDec, dec, deferred);
    }
    WgParFree(c);
    free(g_wgPar.pre), g_wgPar.pre = NULL;
}

static DWORD g_wgParBack = TS3A(0x00672B7D), g_wgParResume = TS3A(0x00672F5A), g_wgParEndBack = TS3A(0x00672F60);
// 0x672B75: esi = WM, edi = G, ebp = &G+0x1C, loop locals at esp; the displaced 8-byte movss xmm0,[esi+0xB4] is replayed.
// xmm0-7 are saved around our C code (the loop reloads them, but nothing proves none is live across the entry).
static __declspec(naked) void WgParEntryStub() {
    __asm {
        pushad
        sub esp, 128
        movups [esp], xmm0
        movups [esp + 16], xmm1
        movups [esp + 32], xmm2
        movups [esp + 48], xmm3
        movups [esp + 64], xmm4
        movups [esp + 80], xmm5
        movups [esp + 96], xmm6
        movups [esp + 112], xmm7
        lea eax, [esp + 160]
        push eax
        push edi
        push esi
        call WgParEntry
        add esp, 12
        movups xmm0, [esp]
        movups xmm1, [esp + 16]
        movups xmm2, [esp + 32]
        movups xmm3, [esp + 48]
        movups xmm4, [esp + 64]
        movups xmm5, [esp + 80]
        movups xmm6, [esp + 96]
        movups xmm7, [esp + 112]
        add esp, 128
        popad
        cmp byte ptr [g_wgParTook], 0
        jne done
        movss xmm0, dword ptr [esi + 0xB4]
        jmp dword ptr [g_wgParBack]
    done:
        jmp dword ptr [g_wgParResume]
    }
}
// 0x672F5A (check mode only): reached from the loop's end and from the empty-range jbe at 0x672B9C; replays
// xor eax,eax / mov [esp+0x60],eax (6 bytes).
static __declspec(naked) void WgParEndStub() {
    __asm {
        pushad
        sub esp, 128
        movups [esp], xmm0
        movups [esp + 16], xmm1
        movups [esp + 32], xmm2
        movups [esp + 48], xmm3
        movups [esp + 64], xmm4
        movups [esp + 80], xmm5
        movups [esp + 96], xmm6
        movups [esp + 112], xmm7
        lea eax, [esp + 160]
        push eax
        push edi
        call WgParEnd
        add esp, 8
        movups xmm0, [esp]
        movups xmm1, [esp + 16]
        movups xmm2, [esp + 32]
        movups xmm3, [esp + 48]
        movups xmm4, [esp + 64]
        movups xmm5, [esp + 80]
        movups xmm6, [esp + 96]
        movups xmm7, [esp + 112]
        add esp, 128
        popad
        xor eax, eax
        mov dword ptr [esp + 0x60], eax
        jmp dword ptr [g_wgParEndBack]
    }
}

static const u8 kWgParEntryOld[8] = {0xF3, 0x0F, 0x10, 0x86, 0xB4, 0x00, 0x00, 0x00};  // movss xmm0,[esi+0xB4]
static const u8 kWgParEndOld[6] = {0x33, 0xC0, 0x89, 0x44, 0x24, 0x60};                // xor eax,eax; mov [esp+0x60],eax

// Every callee the pass calls must be the one the loop calls: E8 targets inside 0x672B75..0x672F5A, plus the pond
// walk's polygon test (0xB5FC40 -> 0xB5FAB0) and the four float constants the loop reads.
static bool WgParCheckSite(const char** why) {
    const DWORD need[6] = {TS3A(0x00C6BBC0), TS3A(0x00C29EE0), TS3A(0x00C60E70), TS3A(0x00B74D10), TS3A(0x00C68990), TS3A(0x0066E040)};
    DWORD seen = 0;
    const u8* p = (const u8*)(uintptr_t)TS3A(0x00672B75);
    if (!Readable(p, 0x3E5)) { *why = "loop unreadable"; return false; }
    for (DWORD i = 0; i + 5 <= 0x3E5; i++)
        if (p[i] == 0xE8) {
            const DWORD t = TS3A(0x00672B75) + i + 5 + *(const LONG*)(p + i + 1);
            for (int n = 0; n < 6; n++)
                if (t == need[n]) seen |= 1u << n;
        }
    if (seen != 0x3F) { *why = "a loop callee differs"; return false; }
    const u8* pc = (const u8*)(uintptr_t)TS3A(0x00B5FC5B);
    if (!Readable(pc, 5) || pc[0] != 0xE8 || TS3A(0x00B5FC60) + *(const LONG*)(pc + 1) != TS3A(0x00B5FAB0)) { *why = "pond polygon call differs"; return false; }
    const float* f05 = (const float*)(uintptr_t)TS3A(0x00F9A5AC), *f1 = (const float*)(uintptr_t)TS3A(0x0107A538);
    const float* f075 = (const float*)(uintptr_t)TS3A(0x01183538), *f15 = (const float*)(uintptr_t)TS3A(0x0118353C);
    if (!Readable(f05, 4) || !Readable(f1, 4) || !Readable(f075, 4) || !Readable(f15, 4) || *f05 != 0.5f || *f1 != 1.0f || *f075 != 0.75f ||
        *f15 != 1.5f) {
        *why = "loop constants differ";
        return false;
    }
    return true;
}

static bool WgParJmp(DWORD at, const u8* old, size_t len, void* to) {
    if (!Matches((const void*)(uintptr_t)at, old, len)) return false;
    u8 j[8];
    memset(j, 0x90, sizeof(j));
    j[0] = 0xE9, *(LONG*)(j + 1) = (LONG)((DWORD)(uintptr_t)to - (at + 5));
    return Write((void*)(uintptr_t)at, j, len);
}

static bool ApplyWaterGridPar(const wchar_t* ini) {
    const int mode = (int)GetPrivateProfileIntW(L"WaterGrid", L"Par", 0, ini);
    if (mode != 1 && mode != 2) { Log("water grid par off ([WaterGrid] Par=0)"); return false; }
    int workers = (int)GetPrivateProfileIntW(L"WaterGrid", L"ParWorkers", 0, ini);
    if (workers <= 0) {
        DWORD_PTR mask = 0, sys = 0;  // the CPUs this process may use (the harness pins the game to a subset)
        GetProcessAffinityMask(GetCurrentProcess(), &mask, &sys);
        int cpus = 0;
        for (; mask; mask &= mask - 1) cpus++;
        workers = cpus - 2;  // leave the sim thread and one more core
        if (workers > 8) workers = 8;
    }
    if (workers < 1) workers = 1;
    if (workers > 64) workers = 64;
    g_wgParWorkers = workers;
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
    const char* why = "";
    if (!WgParCheckSite(&why)) { Log("SKIP water grid par: %s, nothing written", why); return false; }
    // end stub first (check mode), so an entry stub never arms a check nobody runs
    if (mode == 1 && !WgParJmp(TS3A(0x00672F5A), kWgParEndOld, sizeof(kWgParEndOld), (void*)WgParEndStub)) {
        Log("SKIP water grid par: loop end bytes at 0x00672F5A differ, nothing written");
        return false;
    }
    if (!WgParJmp(TS3A(0x00672B75), kWgParEntryOld, sizeof(kWgParEntryOld), (void*)WgParEntryStub)) {
        if (mode == 1) Write((void*)(uintptr_t)TS3A(0x00672F5A), kWgParEndOld, sizeof(kWgParEndOld));
        Log("SKIP water grid par: loop bytes at 0x00672B75 differ, nothing written");
        return false;
    }
    g_wgParMode = mode;
    ts3native_watergrid.parMode = mode, ts3native_watergrid.parWorkers = workers;
    Log("OK   water grid par: %s mode, %d workers (Par=%d, docs/100 §14)", mode == 1 ? "check" : "apply", workers, mode);
    return true;
}

#endif

// [WaterGrid] Prof=1: the eight sites are byte-checked (E8 to the expected target) and retargeted; Prof=0 writes nothing.
// `on` is also the runtime switch for the report (export +0x00); the timers keep running while it is 0.
static bool ApplyWaterGridProf(const wchar_t* ini);
// [WaterGrid] Par (parallel sample pass, docs/100 §14) is independent of Prof; both default 0.
static bool ApplyWaterGrid(const wchar_t* ini) {
#ifdef TS3A_TABLE
    const bool par = false;
    if (GetPrivateProfileIntW(L"WaterGrid", L"Par", 0, ini)) Log("SKIP water grid par: Steam 1.67 only");
#else
    const bool par = ApplyWaterGridPar(ini);
#endif
    return ApplyWaterGridProf(ini) || par;
}
static bool ApplyWaterGridProf(const wchar_t* ini) {
    if (!GetPrivateProfileIntW(L"WaterGrid", L"Prof", 0, ini)) {
        Log("water grid prof off ([WaterGrid] Prof=0)%s", GetPrivateProfileIntW(L"WaterGrid", L"Diff", 0, ini) ? "; SKIP water grid diff: needs Prof=1" : "");
        return false;
    }
#ifdef TS3A_TABLE
    Log("SKIP water grid prof: Steam 1.67 only (no EA 1.69 / retail rows for the inner call sites)");
    return false;
#else
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
    if (!PatchCalls("water grid prof (G build phases)", kWgSites, WG_N)) return false;
    ts3native_watergrid.ready = 1;
    ts3native_watergrid.on = 1;
    Log("OK   water grid prof: G build phase split + output hash per build (Prof=1, docs/100 §13)");
    g_wgDiffMode = (int)GetPrivateProfileIntW(L"WaterGrid", L"Diff", 0, ini);
    if (g_wgDiffMode == 1 || g_wgDiffMode == 2) {
        wcscpy_s(g_wgDir, ini);
        wchar_t* slash = wcsrchr(g_wgDir, L'\\');
        if (slash) slash[1] = 0; else wcscpy_s(g_wgDir, L".\\");
        Log("OK   water grid diff: per-load G dump%s (Diff=%d, docs/100 §14)",
            g_wgDiffMode == 1 ? " + diff against the previous load of the same world" : " only, no diff (first load)", g_wgDiffMode);
    }
    return true;
#endif
}
