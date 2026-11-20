// docs/671: [FglProf] the FindGoodLocation profile (measurement only, default off).
// The sim-cpu hitches of the Isla Paradiso storms (s143-s149: 25 frames, 9.8 s) sit in the script IC
// World_FindGoodLocation with no lock wait. The logs name neither the managed caller nor the number of tiles and placement
// tests of a slow search, nor the check that rejects them. This file adds those numbers: one line per search >= LogMs and
// one line a minute. Every stub calls the game's function with the game's arguments and returns its answer unchanged.
//
// Call-site retargets (all cdecl, arguments on the stack; verified in the 1.67 disassembly):
//   0xB0E3C6 -> 0xB470C0  the search (record, out position, out facing index, out angle), called by 0xB0E130 only
//   0xB4756D -> 0x807B30, 0xB47579 -> 0x869360  "is this tile inside the lot" of one spiral step
//   0xB47DDF -> 0xB47040  the placement test of one facing (up to 8 per tile), 0xB4821B -> 0xB47070 the own-facing test
//   0xB468DD, 0xB46B0D -> 0xB463F0  one footprint part (0xB46800's loop) / the Sim octagon (0xB469C0)
//   0xB4677F -> 0xB46160  the seven checks; six of its call sites (0xB462B7 -> 0xB41120 is [PlaceTestRO]'s, docs/670)
// The inner sites are shared with World_TestObjectPlacement* and the slot test: outside a profiled search the stubs test
// one global and jump on. One search is profiled at a time (the thread that entered first owns the tallies; the IC runs
// on the sim thread); a nested search (the managed ValidatePlacementDelegate calling FindGoodLocation again) is not split
// from its parent: its tests count for the parent.
#include "fglprof-core.h"

typedef DWORD(__cdecl* FglSearchFn)(u8* rec, float* outPos, DWORD* outFacing, float* outAngle);
typedef DWORD(__cdecl* Fgl1Fn)(void* a);
typedef DWORD(__cdecl* Fgl2Fn)(u8* rec, DWORD verbose);
typedef DWORD(__cdecl* Fgl3Fn)(u8* rec, void* poly, DWORD verbose);

static const DWORD FGL_SEARCH = TS3A(0x00B470C0), FGL_TILE_A = TS3A(0x00807B30), FGL_TILE_B = TS3A(0x00869360), FGL_FACING = TS3A(0x00B47040),
                   FGL_OWN = TS3A(0x00B47070), FGL_PART = TS3A(0x00B463F0), FGL_CORE = TS3A(0x00B46160);
static const DWORD kFglCheckFn[6] = {TS3A(0x00B3ECE0), TS3A(0x00B45B90), TS3A(0x00B3DE50), TS3A(0x00B3F0A0), TS3A(0x00B3DCF0), TS3A(0x00B3DDC0)};
static const DWORD kFglCheckSite[6] = {TS3A(0x00B4617F), TS3A(0x00B461BE), TS3A(0x00B4620A), TS3A(0x00B46249), TS3A(0x00B462F6), TS3A(0x00B46322)};
static const int kFglCheckId[6] = {FGC_B3ECE0, FGC_B45B90, FGC_B3DE50, FGC_B3F0A0, FGC_B3DCF0, FGC_B3DDC0};
static const char* const kFglCheckName[FGC_COUNT] = {"B3ECE0", "objects B45B90", "B3DE50", "box B3F0A0", "footprints B41120", "B3DCF0",
                                                    "B3DDC0", "part pre-checks", "outside the part"};

static struct {
    volatile LONG owner;   // thread id inside the profiled search, 0 = none
    FglTally t;
    LONG checkUs[6], coreUs, partUs, testUs;
    int failCheck;         // first failing check of the test in progress, -1 = none yet
    int facingOnTile;      // facing tests run on the tile so far
} g_fgl;

static struct {
    LONG logMs, linesPerMin, frames, ready;
    LONG searches, failed, slow, us, maxUs;
    LONG tests, tiles, rotTiles, rotExtra, rotExtraUs, rotFlip;
    LONG repeats, repeatSame, repeatUs;
    LONG fail[FGC_COUNT];
    DWORD minute, lines, dropped;
    FglPast ring[FGL_RING];
    unsigned next;
} g_fglk;

static bool FglMine() { return g_fgl.owner && (DWORD)g_fgl.owner == GetCurrentThreadId(); }

static DWORD __cdecl FglTileA(void* a) {
    const DWORD r = ((Fgl1Fn)(uintptr_t)FGL_TILE_A)(a);
    if (FglMine()) { g_fgl.t.tilesVisited++; if ((u8)r) g_fgl.t.tilesInLot++; }
    return r;
}
static DWORD __cdecl FglTileB(void* a) {
    const DWORD r = ((Fgl1Fn)(uintptr_t)FGL_TILE_B)(a);
    if (FglMine()) { g_fgl.t.tilesVisited++; if ((u8)r) g_fgl.t.tilesInLot++; }
    return r;
}

static DWORD FglCheck(int i, u8* rec, DWORD verbose) {
    const Fgl2Fn fn = (Fgl2Fn)(uintptr_t)kFglCheckFn[i];
    if (!FglMine()) return fn(rec, verbose);
    LARGE_INTEGER t0;
    QueryPerformanceCounter(&t0);
    const DWORD r = fn(rec, verbose);
    g_fgl.checkUs[i] += UsSince(t0);
    if (!(u8)r && g_fgl.failCheck < 0) g_fgl.failCheck = kFglCheckId[i];
    return r;
}
static DWORD __cdecl FglCheck0(u8* rec, DWORD v) { return FglCheck(0, rec, v); }
static DWORD __cdecl FglCheck1(u8* rec, DWORD v) { return FglCheck(1, rec, v); }
static DWORD __cdecl FglCheck2(u8* rec, DWORD v) { return FglCheck(2, rec, v); }
static DWORD __cdecl FglCheck3(u8* rec, DWORD v) { return FglCheck(3, rec, v); }
static DWORD __cdecl FglCheck4(u8* rec, DWORD v) { return FglCheck(4, rec, v); }
static DWORD __cdecl FglCheck5(u8* rec, DWORD v) { return FglCheck(5, rec, v); }

static DWORD __cdecl FglCore(u8* rec, DWORD verbose) {
    const Fgl2Fn fn = (Fgl2Fn)(uintptr_t)FGL_CORE;
    if (!FglMine()) return fn(rec, verbose);
    LARGE_INTEGER t0;
    QueryPerformanceCounter(&t0);
    const DWORD r = fn(rec, verbose);
    g_fgl.coreUs += UsSince(t0);
    if (!(u8)r && g_fgl.failCheck < 0) g_fgl.failCheck = FGC_B41120;  // the one check of 0xB46160 with no stub here
    return r;
}

static DWORD FglPart(bool octagon, u8* rec, void* poly, DWORD verbose) {
    const Fgl3Fn fn = (Fgl3Fn)(uintptr_t)FGL_PART;
    if (!FglMine()) return fn(rec, poly, verbose);
    LARGE_INTEGER t0;
    QueryPerformanceCounter(&t0);
    const DWORD r = fn(rec, poly, verbose);
    g_fgl.partUs += UsSince(t0);
    if (octagon) g_fgl.t.octTests++; else g_fgl.t.fpTests++;
    if (!(u8)r && g_fgl.failCheck < 0) g_fgl.failCheck = FGC_PRE;
    return r;
}
static DWORD __cdecl FglPartLoop(u8* rec, void* poly, DWORD v) { return FglPart(false, rec, poly, v); }
static DWORD __cdecl FglPartOct(u8* rec, void* poly, DWORD v) { return FglPart(true, rec, poly, v); }

// one placement test of the search: rec+8 / +0xC / +0x10 are the candidate's position words (0xB470C0 copies the tile's
// x, z and level there before each test; 0xB46B20 reads them back against the lot's bounds)
static DWORD FglTest(bool facing, u8* rec, DWORD verbose) {
    const Fgl2Fn fn = (Fgl2Fn)(uintptr_t)(facing ? FGL_FACING : FGL_OWN);
    if (!FglMine()) return fn(rec, verbose);
    unsigned key[3] = {0, 0, 0};
    memcpy(key, rec + 8, sizeof(key));
    const long before = g_fgl.t.tilesTested;
    FglTileBegin(&g_fgl.t, key);
    if (g_fgl.t.tilesTested != before) g_fgl.facingOnTile = 0;
    g_fgl.failCheck = -1;
    LARGE_INTEGER t0;
    QueryPerformanceCounter(&t0);
    const DWORD r = fn(rec, verbose);
    const LONG us = UsSince(t0);
    g_fgl.testUs += us;
    const bool pass = (u8)r != 0;
    FglTestEnd(&g_fgl.t, facing, facing && !g_fgl.facingOnTile, pass, pass ? -1 : g_fgl.failCheck < 0 ? FGC_OTHER : g_fgl.failCheck, us);
    if (facing) g_fgl.facingOnTile++;
    return r;
}
static DWORD __cdecl FglFacing(u8* rec, DWORD v) { return FglTest(true, rec, v); }
static DWORD __cdecl FglOwn(u8* rec, DWORD v) { return FglTest(false, rec, v); }

static void FglLine(const u8* rec, const FglKey& key, bool ok, LONG us, long repeatAge, bool repeatSame) {
    const FglTally& t = g_fgl.t;
    const LONG checks = g_fgl.checkUs[0] + g_fgl.checkUs[1] + g_fgl.checkUs[2] + g_fgl.checkUs[3] + g_fgl.checkUs[4] + g_fgl.checkUs[5];
    char rep[64] = "no repeat";
    if (repeatAge >= 0) sprintf_s(rep, "repeat of a search %ld ms ago (%s result)", repeatAge, repeatSame ? "same" : "other");
    float pos[2] = {0, 0}, radius = 0;
    memcpy(pos, rec + 8, sizeof(pos));
    memcpy(&radius, rec + 0x54, sizeof(radius));
    Log("fgl prof: search %ld ms %s, object %08X%08X lot %08X start %.1f %.1f %08lX, dir %ld facing %02X bools %08X room %ld radius %.0f "
        "scale %ld; tiles visited %ld in lot %ld tested %ld, tests %ld (pass %ld; octagon %ld, footprint parts %ld); first facing "
        "failed on %ld tiles: %ld more tests there %ld ms, a later facing passed on %ld; ms: tests %ld = B3ECE0 %ld + objects B45B90 "
        "%ld + B3DE50 %ld + box B3F0A0 %ld + footprints B41120 ~%ld + B3DCF0 %ld + B3DDC0 %ld + part rest %ld + test rest %ld, "
        "search rest %ld; %s (%lu lines rate-limited before)",
        us / 1000, ok ? "ok" : "FAILED", key.idHi, key.idLo, key.lot, pos[0], pos[1], *(const DWORD*)(rec + 0x10), *(const LONG*)(rec + 0x14),
        (unsigned)rec[0x1C], key.bools, *(const LONG*)(rec + 0x20), radius, *(const LONG*)(rec + 0x48), t.tilesVisited, t.tilesInLot,
        t.tilesTested, t.tests, t.passes, t.octTests, t.fpTests, t.rotTiles, t.rotExtra, t.rotExtraUs / 1000, t.rotFlip, g_fgl.testUs / 1000,
        g_fgl.checkUs[0] / 1000, g_fgl.checkUs[1] / 1000, g_fgl.checkUs[2] / 1000, g_fgl.checkUs[3] / 1000, (g_fgl.coreUs - checks) / 1000,
        g_fgl.checkUs[4] / 1000, g_fgl.checkUs[5] / 1000, (g_fgl.partUs - g_fgl.coreUs) / 1000, (g_fgl.testUs - g_fgl.partUs) / 1000,
        (us - g_fgl.testUs) / 1000, rep, g_fglk.dropped);
    char st[900] = "?";
    if (g_fglk.frames <= 0 || FbStackText(st, sizeof(st), g_fglk.frames) <= 0) strcpy_s(st, "?");
    Log("fgl prof: that search's failed tests by first failing check: %s %ld, %s %ld, %s %ld, %s %ld, %s %ld, %s %ld, %s %ld, %s %ld, "
        "%s %ld; managed caller (innermost first): %s",
        kFglCheckName[0], t.fail[0], kFglCheckName[1], t.fail[1], kFglCheckName[2], t.fail[2], kFglCheckName[3], t.fail[3], kFglCheckName[4],
        t.fail[4], kFglCheckName[5], t.fail[5], kFglCheckName[6], t.fail[6], kFglCheckName[7], t.fail[7], kFglCheckName[8], t.fail[8], st);
}

static DWORD __cdecl FglSearch(u8* rec, float* outPos, DWORD* outFacing, float* outAngle) {
    const FglSearchFn fn = (FglSearchFn)(uintptr_t)FGL_SEARCH;
    const DWORD tid = GetCurrentThreadId();
    if (!rec || InterlockedCompareExchange(&g_fgl.owner, (LONG)tid, 0) != 0) return fn(rec, outPos, outFacing, outAngle);
    FglTallyReset(&g_fgl.t);
    memset(g_fgl.checkUs, 0, sizeof(g_fgl.checkUs));
    g_fgl.coreUs = g_fgl.partUs = g_fgl.testUs = 0;
    g_fgl.failCheck = -1;
    g_fgl.facingOnTile = 0;
    FglKey key = {};
    const u8* obj = *(const u8* const*)rec;   // the game's object, 0 = none (0xB470C0 then tests no placement)
    if (obj) { key.idLo = *(const DWORD*)(obj + 8); key.idHi = *(const DWORD*)(obj + 0xC); }  // docs/670 §2: the id words
    key.lot = *(const DWORD*)(rec + 4);
    memcpy(key.pos, rec + 8, sizeof(key.pos));
    key.bools = FglBools(rec);
    key.dirFacing = *(const DWORD*)(rec + 0x14) << 8 | rec[0x1C];
    key.room = *(const DWORD*)(rec + 0x20);
    key.radius = *(const DWORD*)(rec + 0x54);
    LARGE_INTEGER t0;
    QueryPerformanceCounter(&t0);
    const DWORD r = fn(rec, outPos, outFacing, outAngle);
    const LONG us = UsSince(t0);
    const bool ok = (u8)r != 0;
    const FglTally& t = g_fgl.t;
    g_fglk.searches++;
    if (!ok) g_fglk.failed++;
    g_fglk.us += us;
    if (us > g_fglk.maxUs) g_fglk.maxUs = us;
    g_fglk.tests += t.tests;
    g_fglk.tiles += t.tilesVisited;
    g_fglk.rotTiles += t.rotTiles;
    g_fglk.rotExtra += t.rotExtra;
    g_fglk.rotExtraUs += t.rotExtraUs;
    g_fglk.rotFlip += t.rotFlip;
    for (int i = 0; i < FGC_COUNT; i++) g_fglk.fail[i] += t.fail[i];
    const DWORD now = GetTickCount();
    bool same = false;
    const long age = FglRepeat(g_fglk.ring, &g_fglk.next, key, now, ok, &same);
    if (age >= 0) { g_fglk.repeats++; g_fglk.repeatUs += us; if (same) g_fglk.repeatSame++; }
    if (us / 1000 >= g_fglk.logMs) {
        g_fglk.slow++;
        if (now - g_fglk.minute >= 60000) { g_fglk.minute = now; g_fglk.lines = 0; }
        if (g_fglk.lines >= (DWORD)g_fglk.linesPerMin) g_fglk.dropped++;
        else { g_fglk.lines++; FglLine(rec, key, ok, us, age, same); g_fglk.dropped = 0; }
    }
    InterlockedExchange(&g_fgl.owner, 0);
    return r;
}

static const CallSite kFglSites[] = {
    {TS3A(0x00B0E3C6), FGL_SEARCH, (void*)FglSearch},
    {TS3A(0x00B4756D), FGL_TILE_A, (void*)FglTileA},
    {TS3A(0x00B47579), FGL_TILE_B, (void*)FglTileB},
    {TS3A(0x00B47DDF), FGL_FACING, (void*)FglFacing},
    {TS3A(0x00B4821B), FGL_OWN, (void*)FglOwn},
    {TS3A(0x00B468DD), FGL_PART, (void*)FglPartLoop},
    {TS3A(0x00B46B0D), FGL_PART, (void*)FglPartOct},
    {TS3A(0x00B4677F), FGL_CORE, (void*)FglCore},
    {kFglCheckSite[0], kFglCheckFn[0], (void*)FglCheck0},
    {kFglCheckSite[1], kFglCheckFn[1], (void*)FglCheck1},
    {kFglCheckSite[2], kFglCheckFn[2], (void*)FglCheck2},
    {kFglCheckSite[3], kFglCheckFn[3], (void*)FglCheck3},
    {kFglCheckSite[4], kFglCheckFn[4], (void*)FglCheck4},
    {kFglCheckSite[5], kFglCheckFn[5], (void*)FglCheck5},
};

// [FglProf] Enabled=1: needs no other switch
static void InstallFglProf(const wchar_t* ini) {
    if (!GetPrivateProfileIntW(L"FglProf", L"Enabled", 0, ini)) { Log("fgl prof off (Enabled=0)"); return; }
#ifdef TS3A_TABLE
    if (ts3a_noaddr) { Log("SKIP fgl prof: no EA 1.69 address, docs/671"); return; }
#endif
    if (!PatchCalls("fgl prof (FindGoodLocation search, tile, test and check sites)", kFglSites, (int)(sizeof(kFglSites) / sizeof(kFglSites[0])))) return;
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
    g_fglk.logMs = Clamp(GetPrivateProfileIntW(L"FglProf", L"LogMs", 50, ini), 0, 60000);
    g_fglk.linesPerMin = Clamp(GetPrivateProfileIntW(L"FglProf", L"LinesPerMin", 30, ini), 1, 600);
    g_fglk.frames = Clamp(GetPrivateProfileIntW(L"FglProf", L"Frames", 8, ini), 0, 16);
    g_fglk.ready = 1;
    Log("OK   fgl prof: 14 call sites (LogMs=%ld LinesPerMin=%ld Frames=%ld)", g_fglk.logMs, g_fglk.linesPerMin, g_fglk.frames);
}

// the 60 s counter line (ts3native.cpp MaybeLogCounters); totals since the start
static void FglLogCounters() {
    if (!g_fglk.ready) return;
    Log("fgl prof: searches %ld (failed %ld) %ld ms, max %ld ms, >= %ld ms: %ld; tiles %ld tests %ld; first facing failed on %ld tiles: "
        "%ld more tests there %ld ms, a later facing passed on %ld; repeats within 10 s %ld (same result %ld) %ld ms; failed tests by "
        "check: %ld %ld %ld %ld %ld %ld %ld, part pre-checks %ld, outside the part %ld",
        g_fglk.searches, g_fglk.failed, g_fglk.us / 1000, g_fglk.maxUs / 1000, g_fglk.logMs, g_fglk.slow, g_fglk.tiles, g_fglk.tests, g_fglk.rotTiles,
        g_fglk.rotExtra, g_fglk.rotExtraUs / 1000, g_fglk.rotFlip, g_fglk.repeats, g_fglk.repeatSame, g_fglk.repeatUs / 1000, g_fglk.fail[0],
        g_fglk.fail[1], g_fglk.fail[2], g_fglk.fail[3], g_fglk.fail[4], g_fglk.fail[5], g_fglk.fail[6], g_fglk.fail[7], g_fglk.fail[8]);
}
