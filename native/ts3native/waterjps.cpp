// Phase 9: faster water-grid jump-point search (docs/15 phase 9). Included by ts3native.cpp (one translation unit).
//
// The water leg of the route planner (0x671930 "Routing/waterPath") runs a JPS A* 0x620560 on a byte grid (w x h cells).
// Its jump 0x61FE10 (cdecl, 9 args) walks one cell per call and recurses for the next cell; every cell also calls
// 0x61F750 (step), the forced-neighbour test 0x61FA10 (a vtable call into the route's "near start/goal" callback 0x66DC50,
// then up to eight 0x61F890 + 0x61F9A0 calls) and 0x61F9E0. In Isla Paradiso samples ~90% of the JPS time is in that
// per-cell chain (docs/20). The search itself (open list, closed set) is cheap.
//
// WaterJump below is the same jump as one loop with everything inlined and no divisions per cell. Results are identical,
// including the original's quirks: the step is index arithmetic (a step east off the last column lands on the next row),
// while the forced-neighbour tests use x/y coordinates with bounds checks (no wrap). The callback is called once per forced
// test like the original; the game's own callback 0x66DC50 (pure) is inlined. waterjps-test.cpp runs the game's code
// against this one on random grids. The game's jump uses a stack frame per cell scanned; this loop uses a fixed stack.
// Not cached or precomputed (JPS+): a jump depends on the goal and on the callback's start/goal boxes, and the grids
// (+0x7B0/+0x7C0 of the routing world) would need an invalidation hook we have not found.
//
//   args: g1, g2 = EASTL vector<u8>* (data pointer first): g1 != 0 is water the route may use, g2 != 0 marks cells that
//         are always jump points (the cost layer); w, h; goal and cell = y * w + x; dir 0..7 (0 = y-1, 2 = x+1, clockwise);
//         flag != 0 = the start cell is not water (one step out, no scan); cb = route callback object (vtable, start x,y at
//         +0x10, goal x,y at +0x18) or NULL. Returns the jump point's index or 0xFFFFFFFF.

static const DWORD WJ_JUMP = 0x0061FE10, WJ_CALL = 0x006207FB, WJ_NEAR_CB = 0x0066DC50;
static const DWORD WJ_CODE_LO = 0x0061F750, WJ_CODE_HI = 0x0061FF93;  // 0x61F750 .. end of 0x61FE10: every function the jump runs
static const DWORD WJ_CODE_FNV = 0x5A2C1E2B, WJ_NEAR_FNV = 0x585FD97D;   // FNV-1a of those bytes and of 0x66DC50 (0x60 bytes), Steam 1.67

typedef DWORD(__cdecl* WjJumpFn)(const u8* const* g1, const u8* const* g2, DWORD w, DWORD h, DWORD goal, DWORD cell, DWORD dir, DWORD flag, void* cb);
typedef u8(__fastcall* WjCbFn)(void* self, void* edx, const DWORD* xy);  // thiscall, ret 4
static WjJumpFn g_wjOrig = (WjJumpFn)(uintptr_t)WJ_JUMP;
static bool g_wjNearOk;  // 0x66DC50 holds the expected bytes: inline it
static volatile LONG64 g_wjCalls, g_wjCells;
static volatile LONG g_wjLogged;

static DWORD WjFnv(DWORD lo, DWORD hi) {
    DWORD h = 0x811C9DC5;
    for (const u8* p = (const u8*)(uintptr_t)lo; p < (const u8*)(uintptr_t)hi; p++) h = (h ^ *p) * 0x01000193;
    return h;
}

static const int kWjDx[8] = {0, 1, 1, 1, 0, -1, -1, -1};
static const int kWjDy[8] = {-1, -1, 0, 1, 1, 1, 0, -1};

struct WjCtx {
    const u8* g1;
    const u8* g2;
    DWORD w, h;
    void* cb;
    bool nearCb;  // cb is the game's 0x66DC50: inlined, start x,y and goal x,y copied to o[4..7]
    DWORD cells;
    int step[8];  // index delta per direction (0x61F750)
    int o[8];
};

static inline bool WjCallback(const WjCtx& c, DWORD x, DWORD y) {
    if (!c.cb) return false;
    if (c.nearCb) {
        // 0x66DC50: |x - sx| <= 3 && |y - sy| <= 3 for the start (+0x10) or the goal (+0x18); cdq/xor/sub abs, signed compare
        const int* o = c.o;
        int a = (int)(x - (DWORD)o[4]), b = (int)(y - (DWORD)o[5]);
        if (!(3 < (int)((DWORD)(a ^ (a >> 31)) - (DWORD)(a >> 31))) && !(3 < (int)((DWORD)(b ^ (b >> 31)) - (DWORD)(b >> 31)))) return true;
        a = (int)(x - (DWORD)o[6]), b = (int)(y - (DWORD)o[7]);
        return !(3 < (int)((DWORD)(a ^ (a >> 31)) - (DWORD)(a >> 31))) && !(3 < (int)((DWORD)(b ^ (b >> 31)) - (DWORD)(b >> 31)));
    }
    const DWORD xy[2] = {x, y};
    return ((WjCbFn)(*(void***)c.cb)[1])(c.cb, NULL, xy) != 0;
}

// 0x61F9A0 at neighbour k of (x,y): water in g1 and not marked in g2
static inline bool WjOpen(const WjCtx& c, DWORD x, DWORD y, int k) {
    const DWORD nx = x + kWjDx[k & 7], ny = y + kWjDy[k & 7];
    if (nx >= c.w || ny >= c.h) return false;
    const DWORD i = ny * c.w + nx;
    return c.g1[i] && !c.g2[i];
}

// g2 at neighbour k of (x,y), 0 outside
static inline bool WjMark(const WjCtx& c, DWORD x, DWORD y, int k) {
    const DWORD nx = x + kWjDx[k & 7], ny = y + kWjDy[k & 7];
    return nx < c.w && ny < c.h && c.g2[ny * c.w + nx];
}

// 0x61FA10 != 0 for an in-bounds cell n = (x,y) entered in direction d (0..7)
static inline bool WjForced(const WjCtx& c, DWORD n, DWORD x, DWORD y, int d) {
    if (WjCallback(c, x, y)) return true;
    if (x - 1 < c.w - 2 && y - 1 < c.h - 2) {
        // not on the border: all eight neighbours are inside, so plain index offsets and no bounds checks (same tests as below)
        const u8* a = c.g1 + n;
        const u8* b = c.g2 + n;
        const int* s = c.step;
        const int o = (d & 1) + 1;
#define WJ_OPEN(k) ((a[s[(k) & 7]] != 0) & (b[s[(k) & 7]] == 0))
        const int here = b[0] != 0;
        return (WJ_OPEN(d - o) & !WJ_OPEN(d - o - 1)) | (WJ_OPEN(d + o) & !WJ_OPEN(d + o + 1)) | ((b[s[d]] != 0) != here) |
               ((b[s[(d - 1) & 7]] != 0) != here) | ((b[s[(d + 1) & 7]] != 0) != here);
#undef WJ_OPEN
    }
    if (d & 1) {
        if (WjOpen(c, x, y, d - 2) && !WjOpen(c, x, y, d - 3)) return true;
        if (WjOpen(c, x, y, d + 2) && !WjOpen(c, x, y, d + 3)) return true;
    } else {
        if (WjOpen(c, x, y, d - 1) && !WjOpen(c, x, y, d - 2)) return true;
        if (WjOpen(c, x, y, d + 1) && !WjOpen(c, x, y, d + 2)) return true;
    }
    const bool here = c.g2[y * c.w + x] != 0;
    return WjMark(c, x, y, d) != here || WjMark(c, x, y, d - 1) != here || WjMark(c, x, y, d + 1) != here;
}

// One step from index n at (x,y) in direction d; false when the new cell is outside the grid or not water.
// Equals the original's index step + n / w, n % w: w * (h + 2) < 2^32 is checked by the caller.
static inline bool WjStep(WjCtx& c, DWORD& n, DWORD& x, DWORD& y, int d) {
    n += (DWORD)c.step[d];
    x += kWjDx[d];
    y += kWjDy[d];
    if (x == c.w) x = 0, y++;
    else if (x == 0xFFFFFFFF) x = c.w - 1, y--;
    c.cells++;
    return y < c.h && c.g1[n];
}

// Straight scan (even d) from n: the jump point or 0xFFFFFFFF. A straight jump never branches.
static DWORD WjScan(WjCtx& c, DWORD goal, DWORD n, DWORD x, DWORD y, int d) {
    for (;;) {
        if (!WjStep(c, n, x, y, d)) return 0xFFFFFFFF;
        if (n == goal || WjForced(c, n, x, y, d) || c.g2[n]) return n;
    }
}

static DWORD WaterJump(WjCtx& c, DWORD goal, DWORD cell, int d, bool flag) {
    DWORD n = cell, x = cell % c.w, y = cell / c.w;
    if (flag) {
        // start cell on land: one step out, water or not, while the start cell itself is not water
        WjStep(c, n, x, y, d);
        return y < c.h && !c.g1[cell] ? n : 0xFFFFFFFF;
    }
    if (!(d & 1)) return WjScan(c, goal, n, x, y, d);
    for (;;) {
        if (!WjStep(c, n, x, y, d)) return 0xFFFFFFFF;
        if (n == goal || WjForced(c, n, x, y, d) || c.g2[n]) return n;
        if ((int)WjScan(c, goal, n, x, y, (d - 1) & 7) >= 0 || (int)WjScan(c, goal, n, x, y, (d + 1) & 7) >= 0) return n;
    }
}

static void WjPublish(DWORD cells) {
    Ts3nCounters& k = ts3native_counters;
    k.waterJpsCallsK = (LONG)(InterlockedIncrement64(&g_wjCalls) >> 10);
    k.waterJpsCellsK = (LONG)(InterlockedAdd64(&g_wjCells, cells) >> 10);
}

// Replaces the call at 0x6207FB (the search's only outside call of the jump; the jump's own recursion stays in the original).
static DWORD g_wjLastW, g_wjLastH;  // docs/247 §9: grid of the latest water search, for the sync plan line; shortcut: a worker job can overwrite it, the line is a hint
static DWORD __cdecl WaterJumpHook(const u8* const* g1, const u8* const* g2, DWORD w, DWORD h, DWORD goal, DWORD cell, DWORD dir, DWORD flag, void* cb) {
    g_wjLastW = w, g_wjLastH = h;
    Ts3nCounters& k = ts3native_counters;
    // outside what the loop handles: dir byte > 7 (0x61FA10 treats 0xFF specially), w = 1 (a diagonal step is +0 there: the
    // game recurses until its stack overflows), a grid whose index step could wrap
    if (!k.waterJpsMode || (dir & 0xFF) > 7 || w < 2 || h == 0 || (unsigned __int64)w * (h + 2) >= 0x80000000ull || cell >= w * h) {
        if (k.waterJpsMode) InterlockedIncrement(&k.waterJpsFallbacks);
        return g_wjOrig(g1, g2, w, h, goal, cell, dir, flag, cb);
    }
    WjCtx c = {*g1, *g2, w, h, cb, false, 0, {-(int)w, 1 - (int)w, 1, (int)w + 1, (int)w, (int)w - 1, -1, -(int)w - 1}, {0}};
    if (cb && g_wjNearOk && (*(void***)cb)[1] == (void*)(uintptr_t)WJ_NEAR_CB) {
        c.nearCb = true;
        memcpy(c.o + 4, (const int*)cb + 4, 16);
    }
    const DWORD r = WaterJump(c, goal, cell, (int)(dir & 7), (u8)flag != 0);
    WjPublish(c.cells);
    if (k.waterJpsVerify) {
        const DWORD o = g_wjOrig(g1, g2, w, h, goal, cell, dir, flag, cb);
        if (o != r) {
            InterlockedIncrement(&k.waterJpsMismatches);
            if (InterlockedIncrement(&g_wjLogged) <= 20)
                Log("water jps MISMATCH: w %lu h %lu goal %lu cell %lu dir %lu flag %lu -> fast %lu, game %lu (game's result used)", w, h, goal, cell, dir,
                    flag & 0xFF, r, o);
            return o;
        }
    }
    return r;
}

// [WaterJps]: returns true when the call is retargeted (the counters then stay live).
static bool ApplyWaterJps(const wchar_t* ini) {
    if (!GetPrivateProfileIntW(L"WaterJps", L"Enabled", 0, ini)) { Log("water jps off (Enabled=0)"); return false; }
    Ts3nCounters& c = ts3native_counters;
    c.waterJpsMode = GetPrivateProfileIntW(L"WaterJps", L"Mode", 1, ini) ? 1 : 0;
    c.waterJpsVerify = GetPrivateProfileIntW(L"WaterJps", L"Verify", 0, ini) ? 1 : 0;
    // the loop reproduces exactly this code; anything else there (another patch) and we leave it alone
    if (!Readable((void*)(uintptr_t)WJ_CODE_LO, WJ_CODE_HI - WJ_CODE_LO) || WjFnv(WJ_CODE_LO, WJ_CODE_HI) != WJ_CODE_FNV) {
        Log("SKIP water jps: code at 0x%08X..0x%08X differs from Steam 1.67, nothing written", WJ_CODE_LO, WJ_CODE_HI);
        return false;
    }
    g_wjNearOk = Readable((void*)(uintptr_t)WJ_NEAR_CB, 0x60) && WjFnv(WJ_NEAR_CB, WJ_NEAR_CB + 0x60) == WJ_NEAR_FNV;
    const CallSite site = {WJ_CALL, WJ_JUMP, (void*)WaterJumpHook};
    if (!PatchCalls("water jps", &site, 1)) return false;
    c.waterJpsReady = 1;
    Log("OK   water jps: call 0x%08X -> WaterJumpHook, mode %ld verify %ld, callback 0x%08X %s", WJ_CALL, c.waterJpsMode, c.waterJpsVerify,
        WJ_NEAR_CB, g_wjNearOk ? "inlined" : "differs, called through the vtable");
    return true;
}
