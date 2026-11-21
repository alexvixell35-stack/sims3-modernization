// docs/671: [FglProf] the FindGoodLocation profile (measurement only, default off).
// The sim-cpu hitches of the Isla Paradiso storms (s143-s149: 25 frames, 9.8 s) sit in the script IC
// World_FindGoodLocation with no lock wait. The logs name neither the managed caller nor the number of tiles and placement
// tests of a slow search, nor the check that rejects them. This file adds those numbers: one line pair per search >= LogMs
// and one line a minute.
//
// 14 call-site retargets (bit = the site's bit in [FglProf] Sites):
//   0  0xB0E3C6 -> 0xB470C0  the search (record, out position, out facing index, out angle), called by 0xB0E130 only
//   1  0xB4756D -> 0x807B30, 2  0xB47579 -> 0x869360  "is this tile inside the lot" of one spiral step (STDCALL, ret 4)
//   3  0xB47DDF -> 0xB47040  the placement test of one facing (up to 8 per tile), 4  0xB4821B -> 0xB47070 the own-facing test
//   5  0xB468DD, 6  0xB46B0D -> 0xB463F0  one footprint part (0xB46800's loop) / the Sim octagon (0xB469C0)
//   7  0xB4677F -> 0xB46160  the seven checks; 8-13 six of its call sites (0xB462B7 -> 0xB41120 is [PlaceTestRO]'s, docs/670)
//
// The stubs know nothing of the callee's calling convention (run s150 crashed in the first search: the v1 stubs were cdecl C
// functions and 0x807B30 / 0x869360 pop their argument). Each site's stub is a naked trampoline: it saves every register,
// the flags and the x87 / SSE state, calls FglEnter, restores all of it and jumps to the game's function with the stack as
// the game left it. For a call it wants to time, FglEnter swaps the return address on the stack for FglAfter and keeps the
// real one on a shadow stack; the callee returns there by its own `ret` / `ret N`, FglAfter saves everything again, calls
// FglLeave (the result is the saved eax) and returns to the real address. The game's caller sees its callee's registers,
// flags and stack exactly as without the hook.
// The inner sites are shared with World_TestObjectPlacement* and the slot test: outside a profiled search FglEnter returns
// at once. One search is profiled at a time (the thread that entered first owns the tallies and the shadow stack; the IC
// runs on the sim thread); a nested search (the managed ValidatePlacementDelegate calling FindGoodLocation again) is not
// split from its parent: its tests count for the parent.
#include "fglprof-core.h"

enum { FGL_S_SEARCH, FGL_S_TILE_A, FGL_S_TILE_B, FGL_S_FACING, FGL_S_OWN, FGL_S_PART_LOOP, FGL_S_PART_OCT, FGL_S_CORE, FGL_S_CHECK0, FGL_SITES = 14 };

static const DWORD kFglSite[FGL_SITES] = {TS3A(0x00B0E3C6), TS3A(0x00B4756D), TS3A(0x00B47579), TS3A(0x00B47DDF), TS3A(0x00B4821B),
                                          TS3A(0x00B468DD), TS3A(0x00B46B0D), TS3A(0x00B4677F), TS3A(0x00B4617F), TS3A(0x00B461BE),
                                          TS3A(0x00B4620A), TS3A(0x00B46249), TS3A(0x00B462F6), TS3A(0x00B46322)};
static const DWORD kFglFn[FGL_SITES] = {TS3A(0x00B470C0), TS3A(0x00807B30), TS3A(0x00869360), TS3A(0x00B47040), TS3A(0x00B47070),
                                        TS3A(0x00B463F0), TS3A(0x00B463F0), TS3A(0x00B46160), TS3A(0x00B3ECE0), TS3A(0x00B45B90),
                                        TS3A(0x00B3DE50), TS3A(0x00B3F0A0), TS3A(0x00B3DCF0), TS3A(0x00B3DDC0)};
static const int kFglCheckId[6] = {FGC_B3ECE0, FGC_B45B90, FGC_B3DE50, FGC_B3F0A0, FGC_B3DCF0, FGC_B3DDC0};
static const char* const kFglCheckName[FGC_COUNT] = {"B3ECE0", "objects B45B90", "B3DE50", "box B3F0A0", "footprints B41120", "B3DCF0",
                                                    "B3DDC0", "part pre-checks", "outside the part"};

// the jump targets of the trampolines (the game's functions; the selftest points them at its fakes)
static void* g_fglTarget[FGL_SITES];

struct FglFrame { DWORD* slot; DWORD ret; int site; LARGE_INTEGER t0; };
enum { FGL_DEPTH = 48 };

static struct {
    volatile LONG owner;   // thread id inside the profiled search, 0 = none
    FglTally t;
    LONG checkUs[6], coreUs, partUs, testUs;
    int failCheck;         // first failing check of the test in progress, -1 = none yet
    int facingOnTile;      // facing tests run on the tile so far
    bool firstFacing;      // the test in progress is the tile's first facing test
    const u8* rec;         // the search's record (the caller's local, alive until the search returns)
    FglKey key;
    FglFrame frames[FGL_DEPTH];  // owner thread only
    int depth;
    // [FglFacing] (docs/671 §10): the test in progress and the tile's proof
    const u8* testRec;     // record of the placement test in progress
    long testOct, testFp;  // part counts when it began
    bool shadowTest;       // Shadow: this facing test runs on a proven tile
    bool memoTest;         // [FglMemo] Shadow: the memo holds this facing test as failed
    bool inFacing;         // a facing test (0xB47040) is in progress
    int testIdx, testPart; // its facing index (angle / 45 degrees) and the parts tested so far
    FglProof proof;
    FglLearn learn;        // this search: the disc about the position inside each part's polygon, per facing
    unsigned ignoreHash;
} g_fgl;

// [FglFacing]: mode 0 off / 1 skip, shadow 0 / 1 (run every facing and compare)
static struct {
    LONG mode, shadow, ready;
    LONG proven, skipped, rimOnly, noDisc, badStep, badSite, unlearned, shadowTests, shadowMismatch, shadowShape, noted;
    DWORD stepBits, flag;   // [0x11835D4], [0x11ED034] as last logged
} g_fglf;

// [FglMemo] (docs/671 §11): failed placement tests of the last search, for the next search of the same caller run
static struct {
    LONG mode, shadow, ready, gapMs, maxMs;
    LONG stored, hits, carried, dropSeq, dropGap, dropKey, shadowTests, shadowMismatch, noted;
    FglMemo m;
    FglMemoCtx ctx;
    bool live;
    DWORD first, lastEnd;
    LONG seq;
} g_fglm;

static struct {
    LONG logMs, linesPerMin, frames, ready, sites, prof;
    LONG searches, failed, slow, us, maxUs;
    LONG tests, tiles, rotTiles, rotExtra, rotExtraUs, rotFlip;
    LONG repeats, repeatSame, repeatUs, deep, stale;
    LONG fail[FGC_COUNT];
    DWORD minute, lines, dropped;
    FglPast ring[FGL_RING];
    unsigned next;
} g_fglk;

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

static void FglSearchBegin(const u8* rec) {
    FglTallyReset(&g_fgl.t);
    memset(g_fgl.checkUs, 0, sizeof(g_fgl.checkUs));
    g_fgl.coreUs = g_fgl.partUs = g_fgl.testUs = 0;
    g_fgl.failCheck = -1;
    g_fgl.facingOnTile = 0;
    g_fgl.firstFacing = false;
    g_fgl.rec = rec;
    g_fgl.proof.on = false;
    g_fgl.testRec = NULL;
    g_fgl.shadowTest = false;
    g_fgl.memoTest = false;
    g_fgl.inFacing = false;
    FglLearnReset(&g_fgl.learn);
    FglKey key = {};
    const u8* obj = *(const u8* const*)rec;   // the game's object, 0 = none (0xB470C0 then tests no placement)
    if (obj) { key.idLo = *(const DWORD*)(obj + 8); key.idHi = *(const DWORD*)(obj + 0xC); }  // docs/670 §2: the id words
    key.lot = *(const DWORD*)(rec + 4);
    memcpy(key.pos, rec + 8, sizeof(key.pos));
    key.bools = FglBools(rec);
    key.dirFacing = *(const DWORD*)(rec + 0x14) << 8 | rec[0x1C];
    key.room = *(const DWORD*)(rec + 0x20);
    key.radius = *(const DWORD*)(rec + 0x54);
    g_fgl.key = key;
    if (!g_fglm.ready) return;
    // the memo carries over only into a search of the same object, lot, constraints, room and ignore list that starts within
    // GapMs of the last search's end (the same managed caller going on to its next try), with no routing write in between
    // and no more than MaxMs after the memo's first search began
    FglMemoCtx c = {};
    c.idLo = key.idLo; c.idHi = key.idHi; c.lot = key.lot; c.bools = key.bools; c.room = key.room;
    const DWORD* ids = *(const DWORD* const*)(rec + 0x4C);
    const LONG nIds = *(const LONG*)(rec + 0x50);
    c.ignore = (unsigned)nIds;
    if (ids && nIds > 0 && nIds < 4096) for (LONG i = 0; i < nIds * 2; i++) c.ignore = c.ignore * 16777619u ^ ids[i];
    const DWORD now = GetTickCount();
    const LONG seq = g_snWriteSeq;
    bool keep = g_fglm.live;
    if (keep && !FglMemoCtxEq(g_fglm.ctx, c)) { keep = false; g_fglm.dropKey++; }
    if (keep && seq != g_fglm.seq) { keep = false; g_fglm.dropSeq++; }
    if (keep && (now - g_fglm.lastEnd > (DWORD)g_fglm.gapMs || now - g_fglm.first > (DWORD)g_fglm.maxMs)) { keep = false; g_fglm.dropGap++; }
    if (keep) g_fglm.carried++;
    else {
        FglMemoReset(&g_fglm.m);
        g_fglm.ctx = c;
        g_fglm.seq = seq;
        g_fglm.first = now;
        g_fglm.live = true;
    }
}

static void FglSearchEnd(bool ok, LONG us) {
    const FglTally& t = g_fgl.t;
    g_fglm.lastEnd = GetTickCount();
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
    const long age = FglRepeat(g_fglk.ring, &g_fglk.next, g_fgl.key, now, ok, &same);
    if (age >= 0) { g_fglk.repeats++; g_fglk.repeatUs += us; if (same) g_fglk.repeatSame++; }
    if (us / 1000 < g_fglk.logMs) return;
    g_fglk.slow++;
    if (now - g_fglk.minute >= 60000) { g_fglk.minute = now; g_fglk.lines = 0; }
    if (g_fglk.lines >= (DWORD)g_fglk.linesPerMin) { g_fglk.dropped++; return; }
    g_fglk.lines++;
    FglLine(g_fgl.rec, g_fgl.key, ok, us, age, same);
    g_fglk.dropped = 0;
}

// ---- [FglFacing] (docs/671 §10, v2 §11) --------------------------------------------------------------------------------
// One placement test = the AND of its checks over the object's footprint parts; 0xB41120 (the routing footprint check)
// answers 0 when a blocking footprint of the lot level overlaps the part's polygon. Every facing test teaches, per facing
// index and part ordinal, the radius of the largest disc about the candidate position that lies inside that part's polygon
// (polygon minus position does not depend on the tile: 0xB0B470 is rotation by the angle + (x, z) + an offset of object,
// data and facing only). When a facing fails at 0xB41120 on part j, the check runs once more on a small regular octagon
// about the position, inside the smallest disc learned for part j. Blocked again = a blocker overlaps a region that lies
// inside part j's polygon for every learned facing, so each of those facings fails too (at part j at the latest), and its
// test is answered 0 without the call. A facing not learned yet in this search runs (and is learned). Not answered: the
// own-facing test 0xB47070, tiles that fail at another check first, parts the position is not inside of.
typedef DWORD(__cdecl* FglCheckFn)(u8* rec, DWORD verbose);
static const DWORD FGL_FOOT_SITE = TS3A(0x00B462B7), FGL_FACING_STEP = TS3A(0x011835D4), FGL_PIVOT_FLAG = TS3A(0x011ED034);
static FglCheckFn g_fglFootTest;   // selftest: the fake footprint check; NULL = whatever 0xB462B7 calls now
static float g_fglStepTest;        // selftest: the facing step; 0 = [0x11835D4]
static bool g_fglMemoHhTest;       // selftest: [FglMemo] answers without [Experimental] HouseholdToo

static float FglStep() {
    if (g_fglStepTest > 0) return g_fglStepTest;
    float step = 0;
    memcpy(&step, (const void*)(uintptr_t)FGL_FACING_STEP, sizeof(step));
    return step;
}

// the part polygon of rec (rec+0xA0 begin, +0xA4 end, 8 bytes a point): its disc about the position rec+8 / rec+0xC
static float FglPartDisc(const u8* rec) {
    const float* b = *(const float* const*)(rec + 0xA0);
    const float* e = *(const float* const*)(rec + 0xA4);
    const int n = b && e > b ? (int)(((const u8*)e - (const u8*)b) / 8) : 0;
    if (n < 3 || n > 64) return 0;
    float pos[2] = {0, 0};
    memcpy(pos, rec + 8, sizeof(pos));
    return FglInradius(b, n, pos);
}

static bool FglProve(u8* rec, int part, float* radius) {
    const float r = FglLearnMin(g_fgl.learn, part) - FGL_MARGIN;
    if (!(r >= FGL_MIN_DISC)) { g_fglf.noDisc++; return false; }
    FglCheckFn fn = g_fglFootTest;
    if (!fn) {
        const u8* site = (const u8*)(uintptr_t)FGL_FOOT_SITE;   // the game's 0xB41120 or [PlaceTestRO]'s stub
        if (site[0] != 0xE8) { g_fglf.badSite++; return false; }
        fn = (FglCheckFn)(uintptr_t)(FGL_FOOT_SITE + 5 + *(const LONG*)(site + 1));
    }
    static float oct[16];   // owner thread only
    float pos[2] = {0, 0};
    memcpy(pos, rec + 8, sizeof(pos));
    FglDiscOctagon(pos, r, oct);
    float* const keepB = *(float**)(rec + 0xA0);
    float* const keepE = *(float**)(rec + 0xA4);
    *(float**)(rec + 0xA0) = oct;
    *(float**)(rec + 0xA4) = oct + 16;
    const DWORD a = fn(rec, 0);
    *(float**)(rec + 0xA0) = keepB;
    *(float**)(rec + 0xA4) = keepE;
    if ((u8)a) { g_fglf.rimOnly++; return false; }   // the blocker does not reach the inner disc: not proven
    *radius = r;
    return true;
}

static void FglMemoNote(const u8* rec) {
    if (InterlockedIncrement(&g_fglm.noted) > 16) return;
    float pos[2] = {0, 0}, ang = 0;
    memcpy(pos, rec + 8, sizeof(pos));
    memcpy(&ang, rec + 0x14, sizeof(ang));
    Log("fgl memo: SHADOW MISMATCH at %.2f %.2f level word %08lX angle %.4f: failed in the search before, passes now", pos[0], pos[1],
        *(const DWORD*)(rec + 0x10), ang);
}

static void FglFacingNote(const u8* rec, int check) {
    if (InterlockedIncrement(&g_fglf.noted) > 16) return;
    float pos[2] = {0, 0}, ang = 0;
    memcpy(pos, rec + 8, sizeof(pos));
    memcpy(&ang, rec + 0x14, sizeof(ang));
    Log("fgl facing: SHADOW MISMATCH at %.2f %.2f level word %08lX angle %.4f: the first facing failed at the footprint check with the "
        "small octagon inside every learned facing's part, this facing passed (its first failing check: %d)", pos[0], pos[1], *(const DWORD*)(rec + 0x10),
        ang, check);
}

static DWORD __cdecl FglRet0() { return 0; }

static void FglAfter();
// Called by every trampoline with everything saved. slot = the stack word that holds the call's return address; slot[1..]
// are the callee's stack arguments.
static void __cdecl FglEnter(int site, DWORD* slot, DWORD* go) {
    const DWORD tid = GetCurrentThreadId();
    if (go) *go = (DWORD)(uintptr_t)g_fglTarget[site];
    if (site == FGL_S_SEARCH) {
        if ((DWORD)g_fgl.owner == tid) {
            // our own thread is inside a search: nested (deeper on the stack), or the outer search was unwound by an exception
            if (g_fgl.depth && g_fgl.frames[0].slot > slot) return;
            g_fgl.depth = 0;
            g_fglk.stale++;
        } else if (InterlockedCompareExchange(&g_fgl.owner, (LONG)tid, 0) != 0) return;
        const u8* rec = (const u8*)(uintptr_t)slot[1];
        if (!rec) { InterlockedExchange(&g_fgl.owner, 0); return; }
        g_fgl.depth = 0;
        FglSearchBegin(rec);
    } else {
        if (!g_fgl.owner || (DWORD)g_fgl.owner != tid) return;
        // frames at or below this call's stack position are gone (an exception unwound them): their returns never came
        while (g_fgl.depth && g_fgl.frames[g_fgl.depth - 1].slot <= slot) { g_fgl.depth--; g_fglk.stale++; }
        if (!g_fgl.depth) return;  // the search's own frame is gone: FglLeave of the search never ran; the next search resets
        if (site == FGL_S_FACING || site == FGL_S_OWN) {
            const u8* rec = (const u8*)(uintptr_t)slot[1];
            unsigned key[3] = {0, 0, 0};
            if (rec) memcpy(key, rec + 8, sizeof(key));   // the candidate's position words (0xB470C0 writes them before each test)
            const long before = g_fgl.t.tilesTested;
            FglTileBegin(&g_fgl.t, key);
            if (g_fgl.t.tilesTested != before) g_fgl.facingOnTile = 0;
            g_fgl.firstFacing = site == FGL_S_FACING && !g_fgl.facingOnTile;
            g_fgl.failCheck = -1;
            g_fgl.testRec = rec;
            g_fgl.testOct = g_fgl.t.octTests;
            g_fgl.testFp = g_fgl.t.fpTests;
            g_fgl.shadowTest = false;
            g_fgl.memoTest = false;
            g_fgl.inFacing = site == FGL_S_FACING && rec != NULL;
            g_fgl.testPart = 0;
            if (rec && !FglProofSame(g_fgl.proof, rec, *(const DWORD*)rec, key)) g_fgl.proof.on = false;
            if (g_fgl.inFacing && (g_fglf.ready || g_fglm.ready)) {
                float ang = 0;
                memcpy(&ang, rec + 0x14, sizeof(ang));
                g_fgl.testIdx = g_fglf.ready ? FglFacingIdx(ang, FglStep()) : -1;
                bool answer = false;
                if (g_fgl.proof.on && g_fgl.testIdx >= 0) {
                    if (!FglLearnCovers(g_fgl.learn, g_fgl.testIdx, g_fgl.proof.part, g_fgl.proof.radius)) g_fglf.unlearned++;
                    else if (g_fglf.mode && !g_fglf.shadow) { answer = true; g_fglf.skipped++; }
                    else g_fgl.shadowTest = true;
                }
                if (!answer && g_fglm.ready && g_fglm.live && FglMemoHas(g_fglm.m, key, FglBits(ang))) {
                    // not provably the game's answer (§11.3): the search's Sim is of unknown scope, so it counts as household
                    if (g_fglm.mode && !g_fglm.shadow && (g_fglMemoHhTest || !HhExempt(true))) { answer = true; g_fglm.hits++; }
                    else g_fgl.memoTest = true;
                }
                if (answer) {
                    // answered 0: the game's test does not run (no frame, no return swap)
                    g_fgl.inFacing = false;
                    if (go) *go = (DWORD)(uintptr_t)FglRet0;
                    return;
                }
            }
        }
    }
    if (g_fgl.depth >= FGL_DEPTH) {  // not timed; the search itself always has frame 0
        g_fglk.deep++;
        return;
    }
    FglFrame& f = g_fgl.frames[g_fgl.depth++];
    f.slot = slot;
    f.ret = *slot;
    f.site = site;
    *slot = (DWORD)(uintptr_t)FglAfter;   // the callee returns into FglAfter, which returns to f.ret
    if (g_fglk.prof) QueryPerformanceCounter(&f.t0);
}

// Called by FglAfter (the swapped return address) with everything saved: result = the callee's eax; *out = where to return.
// Only the thread that owns the profile reaches this (FglEnter swaps return addresses for no other).
static void __cdecl FglLeave(DWORD result, DWORD* out) {
    const FglFrame f = g_fgl.frames[--g_fgl.depth];
    *out = f.ret;
    const LONG us = g_fglk.prof ? UsSince(f.t0) : 0;
    const bool yes = (u8)result != 0;
    switch (f.site) {
    case FGL_S_SEARCH:
        FglSearchEnd(yes, us);
        g_fgl.depth = 0;
        InterlockedExchange(&g_fgl.owner, 0);
        break;
    case FGL_S_TILE_A: case FGL_S_TILE_B:
        g_fgl.t.tilesVisited++;
        if (yes) g_fgl.t.tilesInLot++;
        break;
    case FGL_S_FACING: case FGL_S_OWN: {
        const bool facing = f.site == FGL_S_FACING;
        g_fgl.testUs += us;
        FglTestEnd(&g_fgl.t, facing, g_fgl.firstFacing, yes, yes ? -1 : g_fgl.failCheck < 0 ? FGC_OTHER : g_fgl.failCheck, us);
        if (facing) g_fgl.facingOnTile++;
        if (!facing || !g_fgl.testRec) break;
        g_fgl.inFacing = false;
        if (g_fglm.ready) {
            if (g_fgl.memoTest) {
                g_fglm.shadowTests++;
                if (yes) { g_fglm.shadowMismatch++; FglMemoNote(g_fgl.testRec); }
            } else if (!yes && g_fglm.live) {
                unsigned key[3] = {0, 0, 0};
                DWORD ang = 0;
                memcpy(key, g_fgl.testRec + 8, sizeof(key));
                memcpy(&ang, g_fgl.testRec + 0x14, sizeof(ang));
                if (FglMemoPut(&g_fglm.m, key, ang)) g_fglm.stored++;
            }
        }
        if (!g_fglf.ready) break;
        if (g_fgl.shadowTest) {
            g_fglf.shadowTests++;
            if (yes) { g_fglf.shadowMismatch++; FglFacingNote(g_fgl.testRec, g_fgl.failCheck); }
            // the premise: this facing's part holds the disc the proof used (learned again just now)
            if (!FglLearnCovers(g_fgl.learn, g_fgl.testIdx, g_fgl.proof.part, g_fgl.proof.radius)) g_fglf.shadowShape++;
        } else if (!yes && g_fgl.failCheck == FGC_B41120 && g_fgl.testPart > 0 && g_fgl.testIdx >= 0 && !g_fgl.proof.on &&
                   (g_fglf.mode || g_fglf.shadow)) {
            // the footprint check rejected the last part tested
            u8* rec = (u8*)(uintptr_t)g_fgl.testRec;
            float radius = 0;
            if (FglProve(rec, g_fgl.testPart - 1, &radius)) {
                unsigned key[3] = {0, 0, 0};
                memcpy(key, rec + 8, sizeof(key));
                FglProofSet(&g_fgl.proof, rec, *(const DWORD*)rec, key);
                g_fgl.proof.part = g_fgl.testPart - 1;
                g_fgl.proof.radius = radius;
                g_fglf.proven++;
            }
        }
        break;
    }
    case FGL_S_PART_LOOP: case FGL_S_PART_OCT:
        g_fgl.partUs += us;
        if (f.site == FGL_S_PART_OCT) g_fgl.t.octTests++; else g_fgl.t.fpTests++;
        if (g_fgl.inFacing && g_fgl.testRec && g_fglf.ready) {
            // the record still holds this part's polygon (the next part, or the next test, rebuilds it)
            if (g_fgl.testIdx >= 0) FglLearnPut(&g_fgl.learn, g_fgl.testIdx, g_fgl.testPart, FglPartDisc(g_fgl.testRec));
            g_fgl.testPart++;
        }
        if (!yes && g_fgl.failCheck < 0) g_fgl.failCheck = FGC_PRE;
        break;
    case FGL_S_CORE:
        g_fgl.coreUs += us;
        if (!yes && g_fgl.failCheck < 0) g_fgl.failCheck = FGC_B41120;  // the one check of 0xB46160 with no stub here
        break;
    default:
        g_fgl.checkUs[f.site - FGL_S_CHECK0] += us;
        if (!yes && g_fgl.failCheck < 0) g_fgl.failCheck = kFglCheckId[f.site - FGL_S_CHECK0];
        break;
    }
}

// pushad + pushfd = 36 bytes: [ebp+36] is the return address slot (enter) or the word FglAfter pushed for it (leave);
// [ebp+32] is the saved eax. The 512-byte fxsave area sits below, 16-byte aligned.
static __declspec(naked) void FglAfter() {
    __asm push eax __asm pushad __asm pushfd __asm mov ebp, esp __asm sub esp, 528 __asm and esp, 0xFFFFFFF0 __asm fxsave [esp]
    __asm lea eax, [ebp + 36] __asm push eax __asm push dword ptr [ebp + 32] __asm call FglLeave __asm add esp, 8
    __asm fxrstor [esp] __asm mov esp, ebp __asm popfd __asm popad __asm ret
}
#define FGL_STUB(i) static __declspec(naked) void FglStub##i() { __asm pushad __asm pushfd __asm mov ebp, esp __asm sub esp, 528 \
    __asm and esp, 0xFFFFFFF0 __asm fxsave [esp] __asm push 0 __asm lea eax, [ebp + 36] __asm push eax __asm push i __asm call FglEnter \
    __asm add esp, 12 __asm fxrstor [esp] __asm mov esp, ebp __asm popfd __asm popad __asm jmp dword ptr [g_fglTarget + i * 4] }
// the facing test's stub: one more word under the saved registers holds where to go (the game's test, or FglRet0 for a
// facing [FglFacing] answers); `ret` takes it, so the callee starts with the caller's stack and registers as always
static __declspec(naked) void FglStub3() {
    __asm push eax __asm pushad __asm pushfd __asm mov ebp, esp __asm sub esp, 528 __asm and esp, 0xFFFFFFF0 __asm fxsave [esp]
    __asm lea eax, [ebp + 36] __asm push eax __asm lea eax, [ebp + 40] __asm push eax __asm push 3 __asm call FglEnter
    __asm add esp, 12 __asm fxrstor [esp] __asm mov esp, ebp __asm popfd __asm popad __asm ret
}
FGL_STUB(0) FGL_STUB(1) FGL_STUB(2) FGL_STUB(4) FGL_STUB(5) FGL_STUB(6) FGL_STUB(7) FGL_STUB(8) FGL_STUB(9) FGL_STUB(10)
FGL_STUB(11) FGL_STUB(12) FGL_STUB(13)
static void (*const kFglStubs[FGL_SITES])() = {FglStub0, FglStub1, FglStub2, FglStub3, FglStub4, FglStub5, FglStub6, FglStub7, FglStub8,
                                                FglStub9, FglStub10, FglStub11, FglStub12, FglStub13};

// [FglProf] Enabled=1: needs no other switch. Sites = bit mask of the 14 sites (bit 0 the search: without it nothing is
// tallied, the other stubs then only pass through)
static void InstallFglProf(const wchar_t* ini) {
    const bool prof = GetPrivateProfileIntW(L"FglProf", L"Enabled", 0, ini) != 0;
    const bool facing = GetPrivateProfileIntW(L"FglFacing", L"Enabled", 0, ini) != 0;
    const bool memo = GetPrivateProfileIntW(L"FglMemo", L"Enabled", 0, ini) != 0;
    if (!prof && !facing && !memo) { Log("fgl prof off, fgl facing off, fgl memo off (Enabled=0)"); return; }
#ifdef TS3A_TABLE
    if (ts3a_noaddr) { Log("SKIP fgl prof: no EA 1.69 address, docs/671"); return; }
#endif
    g_fglk.sites = (LONG)(GetPrivateProfileIntW(L"FglProf", L"Sites", (1 << FGL_SITES) - 1, ini) & ((1 << FGL_SITES) - 1));
    const LONG kNeed = (1 << FGL_SITES) - 1 - (1 << FGL_S_TILE_A) - (1 << FGL_S_TILE_B) - (1 << FGL_S_OWN);  // what the proof reads
    if (!prof) g_fglk.sites = kNeed;
    CallSite sites[FGL_SITES];
    int n = 0;
    for (int i = 0; i < FGL_SITES; i++) {
        g_fglTarget[i] = (void*)(uintptr_t)kFglFn[i];
        if (!(g_fglk.sites >> i & 1)) continue;
        sites[n].site = kFglSite[i];
        sites[n].target = kFglFn[i];
        sites[n].hook = (void*)kFglStubs[i];
        n++;
    }
    if (!n) { Log("fgl prof off (Sites=0)"); return; }
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
    g_fglk.logMs = Clamp(GetPrivateProfileIntW(L"FglProf", L"LogMs", 50, ini), 0, 60000);
    g_fglk.linesPerMin = Clamp(GetPrivateProfileIntW(L"FglProf", L"LinesPerMin", 30, ini), 1, 600);
    g_fglk.frames = Clamp(GetPrivateProfileIntW(L"FglProf", L"Frames", 8, ini), 0, 16);
    if (!PatchCalls("fgl prof (FindGoodLocation search, tile, test and check sites)", sites, n)) return;
    g_fglk.prof = prof;
    if (prof) {
        g_fglk.ready = 1;
        Log("OK   fgl prof: %d call sites, Sites=0x%04lX (LogMs=%ld LinesPerMin=%ld Frames=%ld)", n, g_fglk.sites, g_fglk.logMs,
            g_fglk.linesPerMin, g_fglk.frames);
    } else g_fglk.logMs = 0x7FFFFFFF;   // no per-search line, no timers
    const bool sitesOk = (g_fglk.sites & kNeed) == kNeed;
    if ((facing || memo) && !sitesOk) Log("SKIP fgl facing / memo: [FglProf] Sites=0x%04lX leaves out a site they read (needs 0x%04lX)", g_fglk.sites, kNeed);
    if (facing && sitesOk) {
        g_fglf.mode = Clamp(GetPrivateProfileIntW(L"FglFacing", L"Mode", 1, ini), 0, 1);
        g_fglf.shadow = Clamp(GetPrivateProfileIntW(L"FglFacing", L"Shadow", 0, ini), 0, 1);
        g_fglf.ready = 1;
        float step = 0;
        memcpy(&step, (const void*)(uintptr_t)FGL_FACING_STEP, sizeof(step));
        memcpy(&g_fglf.stepBits, &step, sizeof(step));
        g_fglf.flag = *(const DWORD*)(uintptr_t)FGL_PIVOT_FLAG;
        Log("OK   fgl facing: Mode=%ld Shadow=%ld (%d call sites%s); facing step [0x11835D4] = %.7f (%08lX), [0x11ED034] = %08lX", g_fglf.mode,
            g_fglf.shadow, n, prof ? ", with the profile" : "", step, g_fglf.stepBits, g_fglf.flag);
    } else if (!facing) Log("fgl facing off (Enabled=0)");
    if (memo && sitesOk) {
        g_fglm.mode = Clamp(GetPrivateProfileIntW(L"FglMemo", L"Mode", 1, ini), 0, 1);
        g_fglm.shadow = Clamp(GetPrivateProfileIntW(L"FglMemo", L"Shadow", 0, ini), 0, 1);
        g_fglm.gapMs = Clamp(GetPrivateProfileIntW(L"FglMemo", L"GapMs", 20, ini), 0, 1000);
        g_fglm.maxMs = Clamp(GetPrivateProfileIntW(L"FglMemo", L"MaxMs", 3000, ini), 0, 60000);
        g_fglm.ready = 1;
        Log("OK   fgl memo: Mode=%ld Shadow=%ld GapMs=%ld MaxMs=%ld (answers only with [Experimental] HouseholdToo=1; else it compares, as Shadow)",
            g_fglm.mode, g_fglm.shadow, g_fglm.gapMs, g_fglm.maxMs);
    } else if (!memo) Log("fgl memo off (Enabled=0)");
}

// the 60 s counter line (ts3native.cpp MaybeLogCounters); totals since the start
static void FglLogCounters() {
    if (g_fglf.ready) {
        DWORD bits = 0;
        memcpy(&bits, (const void*)(uintptr_t)FGL_FACING_STEP, sizeof(bits));
        const DWORD flag = *(const DWORD*)(uintptr_t)FGL_PIVOT_FLAG;
        if (bits != g_fglf.stepBits || flag != g_fglf.flag) {
            Log("fgl facing: [0x11835D4] %08lX -> %08lX, [0x11ED034] %08lX -> %08lX", g_fglf.stepBits, bits, g_fglf.flag, flag);
            g_fglf.stepBits = bits;
            g_fglf.flag = flag;
        }
        Log("fgl facing: mode %ld shadow %ld, searches %ld (failed %ld); tiles proven %ld (a facing rejected by the footprint check, again "
            "on the inner octagon), facing tests answered without the call %ld; not proven: blocker outside the inner disc %ld, no disc "
            "(position not inside the part) %ld, site %ld; facings not learned yet on a proven tile %ld; shadow: facing tests run on "
            "proven tiles %ld, passed %ld (mismatch, must stay 0), part without the disc %ld (must stay 0)",
            g_fglf.mode, g_fglf.shadow, g_fglk.searches, g_fglk.failed, g_fglf.proven, g_fglf.skipped, g_fglf.rimOnly, g_fglf.noDisc,
            g_fglf.badSite, g_fglf.unlearned, g_fglf.shadowTests, g_fglf.shadowMismatch, g_fglf.shadowShape);
    }
    if (g_fglm.ready)
        Log("fgl memo: mode %ld shadow %ld, failed tests stored %ld, facing tests answered from the memo %ld, searches that carried the "
            "memo on %ld; memo dropped: other search key %ld, routing write %ld, gap or age %ld; shadow: tests run that the memo held %ld, "
            "passed %ld (mismatch)",
            g_fglm.mode, g_fglm.shadow, g_fglm.stored, g_fglm.hits, g_fglm.carried, g_fglm.dropKey, g_fglm.dropSeq, g_fglm.dropGap,
            g_fglm.shadowTests, g_fglm.shadowMismatch);
    if (!g_fglk.ready) return;
    Log("fgl prof: searches %ld (failed %ld) %ld ms, max %ld ms, >= %ld ms: %ld; tiles %ld tests %ld; first facing failed on %ld tiles: "
        "%ld more tests there %ld ms, a later facing passed on %ld; repeats within 10 s %ld (same result %ld) %ld ms; failed tests by "
        "check: %ld %ld %ld %ld %ld %ld %ld, part pre-checks %ld, outside the part %ld; untimed (deep) %ld, unwound frames %ld",
        g_fglk.searches, g_fglk.failed, g_fglk.us / 1000, g_fglk.maxUs / 1000, g_fglk.logMs, g_fglk.slow, g_fglk.tiles, g_fglk.tests,
        g_fglk.rotTiles, g_fglk.rotExtra, g_fglk.rotExtraUs / 1000, g_fglk.rotFlip, g_fglk.repeats, g_fglk.repeatSame, g_fglk.repeatUs / 1000,
        g_fglk.fail[0], g_fglk.fail[1], g_fglk.fail[2], g_fglk.fail[3], g_fglk.fail[4], g_fglk.fail[5], g_fglk.fail[6], g_fglk.fail[7],
        g_fglk.fail[8], g_fglk.deep, g_fglk.stale);
}
