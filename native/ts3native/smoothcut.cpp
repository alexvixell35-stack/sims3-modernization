// docs/676 §15: [WorldPrecompute] SmoothCut. A blocked lot segment test of the path smoothing ends at the cell that
// blocks it. Included by ts3native.cpp after routespeed.cpp; default off.
//
// What is slow [verified: decompile + disassembly of 0x64DD40's loop, 0x6675E0, 0x667350, 0x663B20, 0x662D30; the s195
// probe]: pass 3 moves its anchor one sample (5 units at most) at a time and, where the path cannot be pulled straight,
// asks two segment tests per anchor stop that both fail. About 121 tests a pass, 26 us each. On a lot a test is the
// segment query 0x667350: its walk 0x663B20 visits every final quadtree cell the segment crosses (about 75) and the
// callback 0x662D30 always returns "go on", although 0x6675E0 only compares the largest cell value with the two end
// cells' multipliers. A blocked test therefore pays for the whole segment, and the next anchor stop pays again for a
// segment that the same cell blocks.
//
// What this does (smoothcut-core.h): the query is run by our copy of the walk's loop over the game's own functions. It
// stops at the first cell whose value is above both end multipliers ("cut"), and before walking it tries the last few
// cells that blocked a query of this tree on this thread ("hint": down from the root by child numbers, the game's box
// test at every level, then the game's callback on the cell). The answer of 0x6675E0 is the same in every case:
//   - the largest value never falls during a walk, and "blocked" is "largest above both end multipliers";
//   - a hint cell is evaluated only when every box from the root down passes, which is when the game's walk reaches it;
//   - the callback and the evaluator under it write only the stack cursor and, in one branch, the context's class word
//     (+0x4C). That branch needs the word at 2 or more and not 13 (docs/98 §5.3). A query with such a word is passed to
//     the game untouched, so skipping or reordering evaluations changes nothing that lasts;
//   - the deferred-edit flush and the pin count of 0x667350 are done here exactly as there.
// The least value is not kept exact on an early end: 0x6675E0 does not read it [verified, the three reads after its call].
//
// Site: one E8 retarget, 0x6676CE (0x6675E0's call of 0x667350; 0x6675E0 has one caller, the lot test 0x643E60, so only
// the smoother's lot tests come through). The two end multipliers are read from 0x6675E0's frame relative to the
// "largest" out pointer (m0 at -0x10, m1 at -0x60): the instructions that store and compare them are checked byte for
// byte at install, as are the walk's shape and every callee read from it. No address row beyond 0x663B20's.
// Stands alone: the other [WorldPrecompute] items of docs/676 (WaterScanTable, Probe, SmoothMemo) are not in this tree;
// the call site and everything read on the way are found from the post pass 0x64E380 here.
//
// Household rule: the answer is the game's own for every router; nothing is skipped that could change a result. Shadow
// first all the same (mode 2).
// Modes (ts3native_smoothcut.scMode, runtime switch, native_set smoothcut): 0 off (the game's query), 1 serve, 2 shadow
// (ours runs, then the game's; the game's result is returned; a whole walk must give the same count sign, least and
// largest bit for bit, an early end must be a game "blocked"; else scMismatch and SMOOTH CUT MISMATCH, 10 lines; both
// times are summed, so the line shows the saving before mode 1 is ever on).
#include "smoothcut-core.h"

// exported block, not in the ts3fixes slot range (docs/registry.md "native exports")
struct SmoothCutCounters {
    volatile LONG scMode;         // +0x00 runtime switch: 0 off, 1 serve, 2 shadow
    volatile LONG scReady;        // +0x04 bit 0 the call site is ours
    volatile LONG scQueries;      // +0x08 segment queries run by our walk (serve or shadow)
    volatile LONG scNoCut;        // +0x0C passed to the game untouched: no context, or its class word is 2 or more and not 13
    volatile LONG scHintHit;      // +0x10 blocked by a remembered cell (no walk)
    volatile LONG scCut;          // +0x14 blocked at a cell inside the walk (the walk ended there)
    volatile LONG scWholeBlocked; // +0x18 walked whole: blocked (no cell above both ends; the largest equals neither bound) or no cell
    volatile LONG scWholeClear;   // +0x1C walked whole: clear
    volatile LONG scHintTries;    // +0x20 remembered cells tried
    volatile LONG scHintCells;    // +0x24 of those: reached and evaluated
    volatile LONG scCells;        // +0x28 cells evaluated inside walks
    volatile LONG scChecks;       // +0x2C shadow: queries compared with the game's
    volatile LONG scMismatch;     // +0x30 of those: different (must stay 0)
    volatile LONG scLines;        // +0x34 mismatch lines written
    volatile LONG scOff;          // +0x38 queries with mode 0
    volatile LONG scPad;          // +0x3C
    volatile LONGLONG scOurTicks;   // +0x40 timer ticks inside our walk (scQueries)
    volatile LONGLONG scGameTicks;  // +0x48 timer ticks inside the game's query of the scChecks
};
extern "C" __declspec(dllexport) SmoothCutCounters ts3native_smoothcut = {};

// 0x667350: thiscall (ecx = the level's tree), nine stack arguments, ret 0x24, result 0 / 1 in eax
typedef DWORD(__fastcall* ScQueryFn)(u8* tree, void* edx, u8* ctx, DWORD x0, DWORD z0, DWORD x1, DWORD z1, DWORD radius, DWORD* outMin, DWORD* outMax,
                                     DWORD flag);
typedef void(__fastcall* ScFlushFn)(u8* tree);  // 0x666A40: thiscall, no stack argument
static ScQueryFn g_scQueryOrig;
static ScFlushFn g_scFlush;
static ScFns g_scFns;
static int g_scHintN = SC_HINTS;
static __declspec(thread) ScHints t_scHints;

static LONGLONG ScNowQ() {
    LARGE_INTEGER q;
    QueryPerformanceCounter(&q);
    return q.QuadPart;
}

static void ScMismatchLog(const u8* tree, const ScSeg& g, DWORD m0, DWORD m1, const ScOut& o, const ScState& s, DWORD al, DWORD gMin, DWORD gMax) {
    SmoothCutCounters& k = ts3native_smoothcut;
    if (InterlockedIncrement(&k.scLines) > 10) return;
    Log("SMOOTH CUT MISMATCH: tree 0x%08lX segment 0x%08lX 0x%08lX -> 0x%08lX 0x%08lX radius 0x%08lX, end multipliers 0x%08lX 0x%08lX: ours %s "
        "count %ld least 0x%08lX largest 0x%08lX (%lu cells, %lu hint tries), the game's result %lu least 0x%08lX largest 0x%08lX; the game's is used",
        (DWORD)(uintptr_t)tree, (DWORD)g.x0, (DWORD)g.z0, (DWORD)g.x1, (DWORD)g.z1, (DWORD)g.radius, m0, m1,
        o.how == SC_HINT ? "remembered cell" : o.how == SC_CUT ? "cut" : "whole walk", (LONG)s.count, (DWORD)s.minBits, (DWORD)s.maxBits,
        (DWORD)o.cells, (DWORD)o.tries, al, gMin, gMax);
}

// 0x6675E0's call of 0x667350
static DWORD __fastcall ScQueryHook(u8* tree, void*, u8* ctx, DWORD x0, DWORD z0, DWORD x1, DWORD z1, DWORD radius, DWORD* outMin, DWORD* outMax,
                                    DWORD flag) {
    SmoothCutCounters& k = ts3native_smoothcut;
    const LONG mode = k.scMode;
    if (mode != 1 && mode != 2) {
        k.scOff++;
        return g_scQueryOrig(tree, NULL, ctx, x0, z0, x1, z1, radius, outMin, outMax, flag);
    }
    const DWORD cls = ctx ? ScRd(ctx, 0x4C) : 2;
    if (cls >= 2 && cls != 13) {  // the evaluator may write the class word: every evaluation and their order matter
        k.scNoCut++;
        return g_scQueryOrig(tree, NULL, ctx, x0, z0, x1, z1, radius, outMin, outMax, flag);
    }
    // the two end cells' multipliers in 0x6675E0's frame (fstp [esp+0x58] / [esp+0x08]; the largest goes to [esp+0x68])
    const DWORD m0 = ScRd((const u8*)outMax, -0x10), m1 = ScRd((const u8*)outMax, -0x60);
    const ScSeg g = {x0, z0, x1, z1, radius};
    ScState st = {0, SC_FLT_MAX, SC_FLT_MAX, 0};
    ScOut o = {SC_WHOLE, 0, 0, 0};
    const LONGLONG q0 = ScNowQ();
    LONG* const pin = (LONG*)(tree + 0x48);
    if (*pin < 1) g_scFlush(tree);
    ++*pin;
    ScRun(g_scFns, tree, ctx, g, m0, m1, flag, true, &t_scHints, g_scHintN, &st, &o);
    --*pin;
    const LONGLONG q1 = ScNowQ();
    InterlockedExchangeAdd64(&k.scOurTicks, q1 - q0);
    k.scQueries++;
    k.scHintTries += (LONG)o.tries, k.scHintCells += (LONG)o.hintCells, k.scCells += (LONG)o.cells;
    if (o.how == SC_HINT) k.scHintHit++;
    else if (o.how == SC_CUT) k.scCut++;
    else if (ScClear(st, m0, m1)) k.scWholeClear++;
    else k.scWholeBlocked++;
    if (mode == 2) {
        const DWORD al = g_scQueryOrig(tree, NULL, ctx, x0, z0, x1, z1, radius, outMin, outMax, flag);
        InterlockedExchangeAdd64(&k.scGameTicks, ScNowQ() - q1);
        const DWORD gMin = *outMin, gMax = *outMax;
        k.scChecks++;
        const bool same = o.how == SC_WHOLE ? ((st.count > 0) == ((al & 0xFF) != 0) && (st.count <= 0 || (st.minBits == gMin && st.maxBits == gMax)))
                                            : ((al & 0xFF) != 0 && ScAbove(gMax, m0, m1));
        if (!same) {
            InterlockedIncrement(&k.scMismatch);
            ScMismatchLog(tree, g, m0, m1, o, st, al, gMin, gMax);
            for (int i = 0; i < SC_HINTS; i++) t_scHints.h[i].len = 0;
        }
        return al;
    }
    // as 0x667350 leaves them; with no cell the game returns 0 and its caller reads neither
    *outMin = st.minBits, *outMax = st.maxBits;
    return st.count > 0 ? 1u : 0u;
}

#ifndef SMOOTHCUT_HOST_TEST  // smoothcut-host-test.cpp runs the hook above on a fake tree without the game
static bool ScBytes(DWORD at, const u8* want, size_t n) {
    const u8* const p = (const u8*)(uintptr_t)at;
    return Readable(p, n) && !memcmp(p, want, n);
}

static const DWORD SC_POST = TS3A(0x0064E380);  // the node post pass; +0x48 is its call of pass 3 0x64DD40

// the target of the E8 at site, false when it is no E8
static bool ScCallAt(DWORD site, DWORD* target) {
    const u8* p = (const u8*)(uintptr_t)site;
    if (!Readable(p, 5) || p[0] != 0xE8) return false;
    *target = (DWORD)(site + 5 + *(const LONG*)(p + 1));
    return true;
}
static bool ScRetarget(DWORD site, void* hook) {
    const LONG rel = (LONG)((uintptr_t)hook - (site + 5));
    return Write((u8*)(uintptr_t)site + 1, &rel, 4);
}

static void InstallSmoothCut(const wchar_t* ini) {
    SmoothCutCounters& c = ts3native_smoothcut;
    const int mode = Clamp(GetPrivateProfileIntW(L"WorldPrecompute", L"SmoothCut", 0, ini), 0, 2);
    if (!mode) { Log("world precompute smooth cut off ([WorldPrecompute] SmoothCut=0)"); return; }
#ifdef TS3A_TABLE
    if (ts3a_noaddr) { Log("SKIP world precompute smooth cut: no EA 1.69 address, docs/676"); return; }
#endif
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
    // post pass -> pass 3 -> segment test -> lot walk -> lot test -> 0x6675E0
    DWORD p3 = 0, seg = 0, walk = 0, lot = 0, q = 0, sq = 0, flush = 0, tw = 0, cb = 0;
    DWORD ctor = 0, root = 0, fin = 0, fin2 = 0, fin3 = 0, down = 0, box = 0, box2 = 0;
    bool ok = ScCallAt(SC_POST + 0x48, &p3) && ScCallAt(p3 + 0x426, &seg) && ScCallAt(seg + 0xB8, &walk) && ScCallAt(walk + 0x189, &lot);
    // 0x6675E0: where it keeps the two end multipliers, how it passes the out pointers, how it compares afterwards
    static const u8 kM0[] = {0xD9, 0x5C, 0x24, 0x58}, kM1[] = {0xD9, 0x5C, 0x24, 0x08};
    static const u8 kOuts[] = {0x53, 0x6A, 0x00, 0x8D, 0x54, 0x24, 0x70, 0x52, 0x8D, 0x44, 0x24, 0x70, 0x50};
    static const u8 kCmp[] = {0xF3, 0x0F, 0x10, 0x4C, 0x24, 0x5C, 0xF3, 0x0F, 0x10, 0x44, 0x24, 0x6C, 0x0F, 0x2F,
                              0xC8, 0x73, 0x0F, 0xF3, 0x0F, 0x10, 0x4C, 0x24, 0x0C, 0x0F, 0x2F, 0xC8, 0x73, 0x04};
    ok = ok && ScCallAt(lot + 0x1D9, &q) && ScBytes(q + 0x67, kM0, sizeof(kM0)) && ScBytes(q + 0x75, kM1, sizeof(kM1)) &&
         ScBytes(q + 0xAC, kOuts, sizeof(kOuts)) && ScBytes(q + 0xF9, kCmp, sizeof(kCmp)) && ScCallAt(q + 0xEE, &sq);
    // 0x667350: flush when not pinned, pin, the callback pushed, the walk called, ret 0x24
    static const u8 kSqHead[] = {0x83, 0xEC, 0x10, 0x53, 0x56, 0x8B, 0xF1, 0x83, 0x7E, 0x48, 0x00, 0x57, 0x7F, 0x05};
    static const u8 kRet24[] = {0xC2, 0x24, 0x00}, kRetC[] = {0xC2, 0x0C, 0x00};
    ok = ok && ScBytes(sq, kSqHead, sizeof(kSqHead)) && ScCallAt(sq + 0x0E, &flush) && ScBytes(sq + 0xA8, kRet24, sizeof(kRet24)) &&
         ScCallAt(sq + 0x7C, &tw) && tw == TS3A(0x00663B20) && Readable((const u8*)(uintptr_t)(sq + 0x3A), 5) &&
         *(const u8*)(uintptr_t)(sq + 0x3A) == 0x68;
    if (ok) cb = *(const DWORD*)(uintptr_t)(sq + 0x3B);
    // the walk 0x663B20: root set-up from tree +0x24 / +0x38 / +0x44, its callees, the depth stop, ret 0x24
    static const u8 kRootArgs[] = {0x8B, 0x47, 0x24, 0xD9, 0x47, 0x38, 0x8B, 0x4F, 0x44};
    static const u8 kDepth[] = {0x83, 0xFE, 0x0D, 0x73};
    static const u8 kFinal[] = {0x83, 0x79, 0x0C, 0x00, 0x74, 0x0C, 0x0F, 0xB6, 0x41, 0x1C, 0x83, 0xF8,
                                0x15, 0x73, 0x03, 0x33, 0xC0, 0xC3, 0xB8, 0x01, 0x00, 0x00, 0x00, 0xC3};
    ok = ok && ScBytes(tw + 0x27, kRootArgs, sizeof(kRootArgs)) && ScBytes(tw + 0x2A5, kDepth, sizeof(kDepth)) &&
         ScBytes(tw + 0x2F8, kRet24, sizeof(kRet24)) && ScCallAt(tw + 0x1A, &ctor) && ScCallAt(tw + 0x4A, &root) && ScCallAt(tw + 0x68, &fin) &&
         ScCallAt(tw + 0x18F, &fin2) && ScCallAt(tw + 0x27A, &fin3) && fin == fin2 && fin == fin3 && ScCallAt(tw + 0x1A0, &down) &&
         ScCallAt(tw + 0x133, &box) && ScCallAt(tw + 0x26F, &box2) && box == box2 && ScBytes(fin, kFinal, sizeof(kFinal)) &&
         ScBytes(cb + 0x55, kRetC, sizeof(kRetC)) && ScBytes(cb + 0x77, kRetC, sizeof(kRetC));
    if (!ok) {
        Log("SKIP world precompute smooth cut: the code under the lot test 0x%08lX is not that of Steam 1.67 (0x6675E0 at 0x%08lX, query "
            "0x%08lX, walk 0x%08lX, callback 0x%08lX, final test 0x%08lX), nothing written",
            lot, q, sq, tw, cb, fin);
        return;
    }
    g_scQueryOrig = (ScQueryFn)(uintptr_t)sq, g_scFlush = (ScFlushFn)(uintptr_t)flush;
    g_scFns.ctor = (ScCtorFn)(uintptr_t)ctor, g_scFns.root = (ScRootFn)(uintptr_t)root, g_scFns.fin = (ScFinalFn)(uintptr_t)fin;
    g_scFns.down = (ScDownFn)(uintptr_t)down, g_scFns.box = (ScBoxFn)(uintptr_t)box, g_scFns.cb = (ScCbFn)(uintptr_t)cb;
    g_scHintN = Clamp(GetPrivateProfileIntW(L"WorldPrecompute", L"SmoothCutHints", SC_HINTS, ini), 0, SC_HINTS);
    c.scMode = mode;  // before the site: the hook reads it at every call
    if (!ScRetarget(q + 0xEE, (void*)ScQueryHook)) {
        c.scMode = 0;
        Log("FAIL world precompute smooth cut: VirtualProtect at 0x%08lX (err %lu)", q + 0xEE, GetLastError());
        return;
    }
    c.scReady |= 1;
    Log("OK   world precompute smooth cut: SmoothCut=%d (%s), %d remembered cells tried per query; ready 0x%lX (0x%08lX: call 0x%08lX -> ours; "
        "walk 0x%08lX, callback 0x%08lX, child step 0x%08lX, box test 0x%08lX, flush 0x%08lX)",
        mode, mode == 2 ? "shadow: the game's query runs too and its result is used" : "serve", g_scHintN, c.scReady, q + 0xEE, sq, tw, cb, down, box,
        flush);
}

// the 60 s counter line (ts3native.cpp MaybeLogCounters)
static void ScLogCounters() {
    const SmoothCutCounters& c = ts3native_smoothcut;
    if (!c.scReady) return;
    const LONGLONG f = g_qpf.QuadPart ? g_qpf.QuadPart : 1;
    Log("world precompute: smooth cut mode %ld ready 0x%lX: lot segment queries by our walk %ld (%ld ms): blocked by a remembered cell %ld "
        "(tries %ld, cells reached %ld), blocked at a cell in the walk %ld, walked whole %ld blocked + %ld clear (cells evaluated in walks "
        "%ld); passed to the game %ld (class word in use or no context), with mode 0 %ld; shadow checks %ld mismatch %ld (the game's "
        "queries %ld ms)",
        c.scMode, c.scReady, c.scQueries, (LONG)(c.scOurTicks * 1000 / f), c.scHintHit, c.scHintTries, c.scHintCells, c.scCut, c.scWholeBlocked,
        c.scWholeClear, c.scCells, c.scNoCut, c.scOff, c.scChecks, c.scMismatch, (LONG)(c.scGameTicks * 1000 / f));
}
#endif
