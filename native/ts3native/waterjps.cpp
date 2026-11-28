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

static const DWORD WJ_JUMP = TS3A(0x0061FE10), WJ_CALL = TS3A(0x006207FB), WJ_NEAR_CB = TS3A(0x0066DC50);
static const DWORD WJ_CODE_LO = TS3A(0x0061F750), WJ_CODE_HI = TS3A(0x0061FF93);  // 0x61F750 .. end of 0x61FE10: every function the jump runs
// FNV-1a of those bytes and of 0x66DC50 (0x60 bytes), Steam 1.67. EA 1.69.47 (docs/232 "WaterJps on 1.69") and retail 1.67
// (docs/273): the same code moved. The Steam range also holds a heap helper (0x61F830, 0x60 bytes, not on the jump's path)
// that both link elsewhere, so their range is 0x60 shorter and hashes differently; 0x66DC50 hashes the same.
static const DWORD WJ_CODE_FNV = TS3A_PICK(0x5A2C1E2B, 0x201847A4, 0x040DBD38);
static const DWORD WJ_NEAR_FNV = TS3A_PICK(0x585FD97D, 0x585FD97D, 0x585FD97D);

typedef DWORD(__cdecl* WjJumpFn)(const u8* const* g1, const u8* const* g2, DWORD w, DWORD h, DWORD goal, DWORD cell, DWORD dir, DWORD flag, void* cb);
static WjJumpFn g_wjOrig = (WjJumpFn)(uintptr_t)WJ_JUMP;
static bool g_wjNearOk;  // 0x66DC50 holds the expected bytes: inline it
static volatile LONG64 g_wjCalls, g_wjCells;
static volatile LONG g_wjLogged;

static DWORD WjFnv(DWORD lo, DWORD hi) {
    DWORD h = 0x811C9DC5;
    for (const u8* p = (const u8*)(uintptr_t)lo; p < (const u8*)(uintptr_t)hi; p++) h = (h ^ *p) * 0x01000193;
    return h;
}

#include "waterjps-core.h"  // kWjDx, WjCtx, WjForced, WjStep, WjScan, WaterJump; the scan table (docs/676)

static void WjPublish(DWORD cells) {
    Ts3nCounters& k = ts3native_counters;
    k.waterJpsCallsK = (LONG)(InterlockedIncrement64(&g_wjCalls) >> 10);
    k.waterJpsCellsK = (LONG)(InterlockedAdd64(&g_wjCells, cells) >> 10);
}

// docs/676 (worldpre.cpp): the scan table of the running water search, if it has one
static int WxJumpTab(WjCtx& c, DWORD goal);
static void WxJumpDone(const WjCtx& c);
static void WxShadowNote(DWORD walk, DWORD tab, const WjCtx& c, DWORD goal, DWORD cell, DWORD dir, DWORD flag);

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
    WjCtx c = {*g1, *g2, w, h, cb, false, 0, {-(int)w, 1 - (int)w, 1, (int)w + 1, (int)w, (int)w - 1, -1, -(int)w - 1}, {0}, NULL, 0, 0, 0, 0};
    if (cb && g_wjNearOk && (*(void***)cb)[1] == (void*)(uintptr_t)WJ_NEAR_CB) {
        c.nearCb = true;
        memcpy(c.o + 4, (const int*)cb + 4, 16);
    }
    // docs/676 [WorldPrecompute] WaterScanTable: inside a search whose grids were compared with the table's copies, the
    // straight rays come from the table (c.tab). Mode 2 also walks and counts a difference; the walk's result is used.
    const int tabMode = WxJumpTab(c, goal);
    DWORD r = 0;
    if (tabMode == 2) {
        WjCtx walk = c;
        walk.tab = NULL;
        r = WaterJump(walk, goal, cell, (int)(dir & 7), (u8)flag != 0);
        WxShadowNote(r, WaterJump(c, goal, cell, (int)(dir & 7), (u8)flag != 0), c, goal, cell, dir, flag);
    } else {
        r = WaterJump(c, goal, cell, (int)(dir & 7), (u8)flag != 0);
    }
    if (tabMode) WxJumpDone(c);
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
