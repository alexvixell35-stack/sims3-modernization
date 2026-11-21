// docs/676 §14: [WorldPrecompute] SmoothMemo, an exact memo of quadtree cell evaluations inside one path smoothing pass.
// Included by ts3native.cpp after floodmemo.cpp (worldpre.cpp: WX_POST, WxCallAt, WxRetarget, the probe's saved targets;
// plancand.cpp: PccScopeNow; nopathmemo.cpp: NpmCmdHandle; routesnap.cpp: g_snWriteSeq). Installed after InstallWorldPre.
//
// What it removes [verified: decompile + disassembly of 0x643E60, 0x6675E0, 0x667350, 0x662D30, 0x66B670, 0x66B600,
// 0x66B090 and its callees; docs/98 §5]. The smoothing pass 0x64DD40 tests 2-3 segments per 5-unit sample. On a lot, with
// the smoother's byte +0x72 clear, a test is 0x6675E0: the multiplier of the start cell and of the end cell (0x66B670 on
// a cursor that 0x663900 walked down to the point), then a segment query 0x667350 whose callback 0x662D30 asks 0x66B670
// and 0x66B600 for the multiplier and class of every cell the segment crosses. The cursors are stack objects, fresh in
// every query, so their cache key never matches the context's flags and every ask runs the evaluator 0x66B090: a scan of
// the leaf's footprint entries with three or more virtual calls per entry (and twice per cell when the flags have a bit
// above 23, docs/98 §5.4). Neighbouring tests of one pass cross mostly the same cells: same anchor and the next sample,
// or the next anchor and the same sample.
//
// Why a memo is exact:
//   - The evaluator's result is a function of the cell (tree, parent, node, size, position, shape, quadrant: the cursor
//     words in the key), the node's footprint entries, the context's flags +0x50, class word +0x4C and route options
//     +0x40 [verified: the only context reads of 0x66B090, 0x6681E0, 0x668C40, 0x667DB0, 0x669530, 0x669850, 0x669B90;
//     the same list FloodMemo rests on, docs/667]. It writes the cursor's +0x10, +0x1C and class byte, and the context's
//     +0x4C in one branch. A record holds exactly those writes and replays them.
//   - The game keeps the same answer itself: an in-room search stores multiplier and class in the cell and reuses them
//     for the whole hierarchical plan while the context's flags match (docs/98 §5.4). The memo is that cache with a
//     stricter key (class word, options, lot) and a shorter life (one pass 3 call of one thread).
//   - A cell of class 1 is never kept: the game clears such a cell's key so that every use evaluates again; so do we.
//   - The tree cannot change inside a pass: the pass runs inside a plan, under the routing lock from start to end, and
//     tree writers run under that lock [docs/81, docs/156]. Kept as checks all the same: a tree with deferred edits
//     pending (byte +0x4C) is never read from or written to the memo (0x6675E0 asks for its two end cells before
//     0x667350 flushes); [RouteSnap]'s write sequence and the thread's managed-call sequence drop every record when
//     they move; the table is emptied at every pass start.
//   - On a miss the game's own function runs on the game's own cursor; the memo copies what it left.
// Not touched: the descent 0x663900, the tree walk 0x663B20 and its box tests, the world (off-lot) test 0x658A00, and
// tests with +0x72 set (they collect footprints into a set and never ask for a multiplier).
//
// Sites (all E8 rel32 retargets, each checked at install; no address row: every site is an offset from the post pass
// 0x64E380 and from targets read on the way, anchored on the row of 0x663B20):
//   0x64E3C8  the post pass's call of pass 3: SmPassHook opens the memo for this thread and calls on (into the probe's
//             stub when [WorldPrecompute] Probe retargeted the site first);
//   0x667642, 0x667650  0x6675E0's two calls of 0x66B670 (start cell, end cell);
//   0x662D3E, 0x662D54  the segment callback's calls of 0x66B670 and 0x66B600.
// The four reader hooks are plain thiscall-shaped functions (ecx = cursor, one stack argument, ret 4, float in ST0 /
// int in eax, as the game's). Outside a pass 3 call of the thread they call the game's function at once.
//
// Household rule (as FloodMemo): the answer is the game's own, so it is exact for every router. Until a shadow run shows
// 0 mismatches a served answer goes only to routers of known NPC scope (unseen 2, seen NPC 3); a pass of another router
// runs as shadow. SmoothMemoAll=1 (or [Experimental] HouseholdToo=1) adds the others except a plan that may be a player
// command (no command marks, no route handle, or a marked route).
//
// Modes (ts3native_smoothmemo.smMode, runtime switch, native_set smoothmemo): 0 off, 1 serve, 2 shadow (every evaluation
// runs; a record that differs from what the game's function left counts smMismatch and logs SMOOTH MEMO MISMATCH, 10
// lines; the time of the evaluations a record would have replaced is summed: what mode 1 saves before its own cost).
// Memory: one 280 KB block per planning thread, at most SM_THREADS.
#include "smoothmemo-core.h"

// exported block, not in the ts3fixes slot range (docs/registry.md "native exports")
struct SmoothMemoCounters {
    volatile LONG smMode;         // +0x00 runtime switch: 0 off, 1 serve, 2 shadow
    volatile LONG smReady;        // +0x04 bit 0 pass 3 call site, 1 the four reader sites, 2 write sequence (RouteSnap), 3 managed-call sequence
    volatile LONG smPasses;       // +0x08 pass 3 calls with the memo open (serve or shadow)
    volatile LONG smPassesOff;    // +0x0C pass 3 calls with the memo closed (mode 0, no block)
    volatile LONG smScopePasses;  // +0x10 mode 1 passes run as shadow: household / selected / unknown router or a possible command
    volatile LONG smEvals;        // +0x14 evaluations asked inside an open pass (cursor key != context flags)
    volatile LONG smHits;         // +0x18 answered from a record
    volatile LONG smMiss;         // +0x1C the game's function ran: no record
    volatile LONG smStores;       // +0x20 records kept
    volatile LONG smClass1;       // +0x24 not kept: class 1 (the game evaluates these every time)
    volatile LONG smDirty;        // +0x28 passed by: the tree has deferred edits pending
    volatile LONG smFull;         // +0x2C not kept: the key's slots are taken
    volatile LONG smResets;       // +0x30 records dropped inside a pass: the write or managed-call sequence moved
    volatile LONG smChecks;       // +0x34 records compared with the game's function (shadow, scope)
    volatile LONG smMismatch;     // +0x38 of those: different (must stay 0)
    volatile LONG smNoTab;        // +0x3C passes on a thread past SM_THREADS or with no memory
    volatile LONG smLines;        // +0x40 mismatch lines written
    volatile LONG smNoCtx;        // +0x44 asked with no context or after a sequence moved inside the call (never kept)
    volatile LONGLONG smPassUs;     // +0x48 time of the smPasses calls
    volatile LONGLONG smPassOffUs;  // +0x50 time of the smPassesOff calls
    volatile LONGLONG smCheckUs;    // +0x58 time of the game's evaluations counted in smChecks
};
extern "C" __declspec(dllexport) SmoothMemoCounters ts3native_smoothmemo = {};

typedef float(__fastcall* SmMultFn)(u8* cursor, void* edx, u8* ctx);  // 0x66B670: thiscall, ret 4, float in ST0
typedef LONG(__fastcall* SmClassFn)(u8* cursor, void* edx, u8* ctx);  // 0x66B600: thiscall, ret 4
typedef DWORD(__fastcall* SmPassFn)(void* smoother, void* edx, void* path);  // 0x64DD40: thiscall, ret 4, result in al
static SmMultFn g_smMultOrig;
static SmClassFn g_smClassOrig;
static SmPassFn g_smPassOrig;
static bool g_smAll;  // SmoothMemoAll=1 (or HouseholdToo): household / selected / unknown routers too, never a possible command

struct SmTh {
    SmTab* tab;
    const void* sp;  // a local of the open pass's hook frame: every evaluation of the pass runs below it
    bool open;       // a pass 3 call of this thread has the memo open
    bool serve;      // records answer (else they are only compared)
    bool none;       // this thread got no block
    unsigned int seqWrite, seqHost;
};
static __declspec(thread) SmTh t_sm;
static volatile LONG g_smThreads;
static volatile LONGLONG g_smCheckTicks;  // timer ticks of the game's evaluations counted in smChecks

static LONGLONG SmNowQ() {
    LARGE_INTEGER q;
    QueryPerformanceCounter(&q);
    return q.QuadPart;
}
static LONGLONG SmUs(LONGLONG ticks) { return g_qpf.QuadPart ? ticks * 1000000 / g_qpf.QuadPart : 0; }

static SmTab* SmThreadTab(SmTh& t) {
    if (t.tab || t.none) return t.tab;
    if (InterlockedIncrement(&g_smThreads) > SM_THREADS) { t.none = true; return NULL; }
    t.tab = (SmTab*)VirtualAlloc(NULL, sizeof(SmTab), MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);  // zeroed; every pass clears it before use
    if (!t.tab) t.none = true;
    return t.tab;
}

// true = records answer this pass. NPC scope always; other scopes only with SmoothMemoAll and a plan known not to be a command
static bool SmScopeOk() {
    bool worker = false;
    const LONG s = PccScopeNow(&worker);  // plancand.cpp: the sim session's or the worker job's router scope
    if (s == 2 || s == 3) return true;
    if (!g_smAll) return false;
    const DWORD h = worker ? g_async.handle : (t_pd.open ? t_pd.handle : 0);
    bool tracked = false;
    const bool cmd = NpmCmdHandle(h, &tracked);  // nopathmemo.cpp: ts3fixes' op 39 marks
    return h && tracked && !cmd;
}

static inline unsigned int SmSeqWrite() { return g_snOn ? (unsigned int)g_snWriteSeq : 0; }

// one evaluation, around the game's function
struct SmCall {
    SmTh* t;       // NULL: not looked at (no open pass, no context, the game's cached answer, a dirty tree)
    bool hit;      // a record exists (and was not served: shadow)
    SmKey k;
    SmVal v;       // the record's value when hit
    LONGLONG q0;
    SmCall() : t(NULL), hit(false), q0(0) {}  // k and v are written before they are read (SmPre)
};

static void SmMismatchLog(const SmCall& c, const SmVal& now) {
    SmoothMemoCounters& k = ts3native_smoothmemo;
    if (InterlockedIncrement(&k.smLines) > 10) return;
    Log("SMOOTH MEMO MISMATCH: tree 0x%08lX node 0x%08lX pos 0x%08lX shape %lu, context flags 0x%lX class %lu options 0x%08lX lot 0x%08lX: "
        "record mult 0x%08lX key 0x%08lX class %lu ctx class %lu, the game's mult 0x%08lX key 0x%08lX class %lu ctx class %lu "
        "(write seq %lu, managed-call seq %lu); the game's result is used and the records are dropped",
        (DWORD)c.k.w04, (DWORD)c.k.w0c, (DWORD)c.k.w18, (DWORD)c.k.shape, (DWORD)c.k.flags, (DWORD)c.k.cls, (DWORD)c.k.opts, (DWORD)c.k.lot,
        (DWORD)c.v.w10, (DWORD)c.v.w1c, (DWORD)c.v.cls8, (DWORD)c.v.ctxCls, (DWORD)now.w10, (DWORD)now.w1c, (DWORD)now.cls8, (DWORD)now.ctxCls,
        (DWORD)SmSeqWrite(), (DWORD)t_fmHostSeq);
}

// Before the game's function. true = the cursor and the context now hold what the function would have left: do not call it.
// here = a local of the calling hook (the stack position of this evaluation).
static bool SmPre(SmCall& c, u8* cur, u8* ctx, const void* here) {
    SmTh& t = t_sm;
    if (!t.open) return false;
    if (here >= t.sp) { t.open = false; return false; }  // an unwind left the pass open: this call is not below its frame
    SmoothMemoCounters& k = ts3native_smoothmemo;
    if (!ctx) { k.smNoCtx++; return false; }  // no context: the function's other branch (key 0x2000000)
    if ((SmRd(cur, 0x1C) >> 8) == SmRd(ctx, 0x50)) return false;  // the cursor's own cached answer: the function only reads it
    k.smEvals++;
    const u8* const tree = (const u8*)(uintptr_t)SmRd(cur, 0x04);
    if (tree && tree[0x4C]) { k.smDirty++; return false; }  // deferred edits pending: the next unpinned query changes the tree
    const unsigned int sw = SmSeqWrite(), sh = (unsigned int)t_fmHostSeq;
    if (sw != t.seqWrite || sh != t.seqHost) {
        if (t.tab->used) k.smResets++;
        SmClear(t.tab);
        t.seqWrite = sw, t.seqHost = sh;
    }
    SmKeyOf(&c.k, cur, ctx);
    c.t = &t;
    const SmEntry* const e = SmFind(t.tab, c.k);
    if (!e) return false;
    if (t.serve) {
        SmApply(e->v, cur, ctx);
        k.smHits++;
        c.t = NULL;
        return true;
    }
    c.hit = true, c.v = e->v;
    c.q0 = SmNowQ();
    return false;
}
// After the game's function ran on (cur, ctx).
static void SmPost(SmCall& c, const u8* cur, const u8* ctx) {
    if (!c.t) return;
    SmoothMemoCounters& k = ts3native_smoothmemo;
    SmTh& t = *c.t;
    SmVal v;
    SmValOf(&v, cur, ctx);
    if (c.hit) {
        const LONGLONG dq = SmNowQ() - c.q0;
        // ticks are summed and converted as a total: one evaluation is well under a microsecond, so the sum of whole
        // microseconds per evaluation read as next to nothing (s212: 4.3 M evaluations, "10 ms")
        k.smCheckUs = SmUs(InterlockedExchangeAdd64(&g_smCheckTicks, dq) + dq);
        k.smChecks++;
        if (!SmValEq(c.v, v)) {
            InterlockedIncrement(&k.smMismatch);
            SmMismatchLog(c, v);
            SmClear(t.tab);
        }
        return;
    }
    k.smMiss++;
    if (v.cls8 == 1) { k.smClass1++; return; }
    if (SmSeqWrite() != t.seqWrite || (unsigned int)t_fmHostSeq != t.seqHost) { k.smNoCtx++; return; }  // moved inside the call: the next ask drops the records
    if (SmStore(t.tab, c.k, v)) k.smStores++;
    else k.smFull++;
}

static float __fastcall SmMultHook(u8* cur, void*, u8* ctx) {
    SmCall c;
    if (SmPre(c, cur, ctx, &c)) return *(const float*)(cur + 0x10);  // as the game: fld dword ptr [cursor + 0x10]
    const float r = g_smMultOrig(cur, NULL, ctx);
    SmPost(c, cur, ctx);
    return r;
}
static LONG __fastcall SmClassHook(u8* cur, void*, u8* ctx) {
    SmCall c;
    if (SmPre(c, cur, ctx, &c)) return (LONG)(signed char)cur[0x20];  // as the game: movsx eax, byte ptr [cursor + 0x20]
    const LONG r = g_smClassOrig(cur, NULL, ctx);
    SmPost(c, cur, ctx);
    return r;
}

// the post pass's call of pass 3. Every call opens the memo anew and closes it at its end (also when an exception leaves the
// pass): a pass inside a pass (not seen: pass 3 plans nothing) would close the outer one's memo, which then runs the game's
// evaluations.
#ifdef SMOOTHMEMO_HOST_TEST
#define SM_TRY
#define SM_FINALLY
#else
#define SM_TRY __try
#define SM_FINALLY __finally
#endif
static DWORD __fastcall SmPassHook(void* smoother, void*, void* path) {
    SmoothMemoCounters& k = ts3native_smoothmemo;
    SmTh& t = t_sm;
    const LONG mode = k.smMode;
    const LONGLONG q0 = SmNowQ();
    SmTab* const tab = mode == 1 || mode == 2 ? SmThreadTab(t) : NULL;
    DWORD r = 0;
    if (!tab) {
        if (mode == 1 || mode == 2) k.smNoTab++;
        t.open = false;
        r = g_smPassOrig(smoother, NULL, path);
        InterlockedIncrement(&k.smPassesOff);
        InterlockedExchangeAdd64(&k.smPassOffUs, SmUs(SmNowQ() - q0));
        return r;
    }
    t.serve = mode == 1 && SmScopeOk();
    if (mode == 1 && !t.serve) k.smScopePasses++;
    SmClear(tab);
    t.seqWrite = SmSeqWrite(), t.seqHost = (unsigned int)t_fmHostSeq;
    t.sp = &q0;
    t.open = true;
    SM_TRY { r = g_smPassOrig(smoother, NULL, path); }
    SM_FINALLY { t.open = false; }
    InterlockedIncrement(&k.smPasses);
    InterlockedExchangeAdd64(&k.smPassUs, SmUs(SmNowQ() - q0));
    return r;
}

#ifndef SMOOTHMEMO_HOST_TEST  // smoothmemo-host-test.cpp runs the hooks above on fake cursors without the game
// both readers start alike: mov edx, [esp + 4]; test edx, edx; push esi; mov esi, ecx (thiscall, one stack argument)
static const u8 kSmReaderHead[9] = {0x8B, 0x54, 0x24, 0x04, 0x85, 0xD2, 0x56, 0x8B, 0xF1};

static void InstallSmoothMemo(const wchar_t* ini) {
    SmoothMemoCounters& c = ts3native_smoothmemo;
    const int mode = Clamp(GetPrivateProfileIntW(L"WorldPrecompute", L"SmoothMemo", 0, ini), 0, 2);
    if (!mode) { Log("world precompute smooth memo off ([WorldPrecompute] SmoothMemo=0)"); return; }
#ifdef TS3A_TABLE
    if (ts3a_noaddr) { Log("SKIP world precompute smooth memo: no EA 1.69 address, docs/676"); return; }
#endif
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
    // pass 3 -> segment test -> lot walk -> lot test -> 0x6675E0 -> its two multiplier calls and the segment query ->
    // the query's callback (a push imm32 before its call of the tree walk 0x663B20) -> the callback's two reader calls.
    // Where [WorldPrecompute] Probe put its stubs on these calls, its saved targets are the game's functions.
    const LONG wr = ts3native_worldpre.wxReady;
    const DWORD site3 = WX_POST + kWxPostOff[2];
    DWORD now3 = 0, p3 = 0, seg = 0, walk = 0, lot = 0, q = 0, m = 0, m2 = 0, sq = 0, treeWalk = 0, cb = 0, cbM = 0, cls = 0, ev1 = 0, ev2 = 0;
    bool ok = WxCallAt(site3, &now3);
    p3 = (wr & 8) ? (DWORD)(uintptr_t)g_wxPostTarget[2] : now3;
    if (wr & 16) {
        seg = (DWORD)(uintptr_t)g_wxPostTarget[WX_POSTN + 2], walk = (DWORD)(uintptr_t)g_wxPostTarget[WX_POSTN + 4];
        lot = (DWORD)(uintptr_t)g_wxPostTarget[WX_POSTN + 8];
    } else {
        ok = ok && WxCallAt(p3 + 0x426, &seg) && WxCallAt(seg + 0xB8, &walk) && WxCallAt(walk + 0x189, &lot);
    }
    ok = ok && WxCallAt(lot + 0x1D9, &q) && WxCallAt(q + 0x62, &m) && WxCallAt(q + 0x70, &m2) && m == m2 && WxCallAt(q + 0xEE, &sq);
    ok = ok && WxCallAt(sq + 0x7C, &treeWalk) && treeWalk == TS3A(0x00663B20) && Readable((const u8*)(uintptr_t)(sq + 0x3A), 5) &&
         *(const u8*)(uintptr_t)(sq + 0x3A) == 0x68;
    if (ok) cb = *(const DWORD*)(uintptr_t)(sq + 0x3B);
    ok = ok && WxCallAt(cb + 0x0E, &cbM) && cbM == m && WxCallAt(cb + 0x24, &cls) && cls != m && WxCallAt(m + 0x2A, &ev1) &&
         WxCallAt(cls + 0x2A, &ev2) && ev1 == ev2 && Readable((const u8*)(uintptr_t)m, sizeof(kSmReaderHead)) &&
         Readable((const u8*)(uintptr_t)cls, sizeof(kSmReaderHead)) && !memcmp((const void*)(uintptr_t)m, kSmReaderHead, sizeof(kSmReaderHead)) &&
         !memcmp((const void*)(uintptr_t)cls, kSmReaderHead, sizeof(kSmReaderHead));
    if (!ok) {
        Log("SKIP world precompute smooth memo: the calls under the post pass 0x%08lX are not those of Steam 1.67 (pass 3 0x%08lX, lot test "
            "0x%08lX, query 0x%08lX, callback 0x%08lX, readers 0x%08lX / 0x%08lX), nothing written",
            WX_POST, p3, lot, q, cb, m, cls);
        return;
    }
    g_smMultOrig = (SmMultFn)(uintptr_t)m, g_smClassOrig = (SmClassFn)(uintptr_t)cls, g_smPassOrig = (SmPassFn)(uintptr_t)now3;
    // the readers first: until the pass site is written they only call the game's functions
    int done = 0;
    done += WxRetarget(q + 0x62, (void*)SmMultHook) ? 1 : 0;
    done += WxRetarget(q + 0x70, (void*)SmMultHook) ? 1 : 0;
    done += WxRetarget(cb + 0x0E, (void*)SmMultHook) ? 1 : 0;
    done += WxRetarget(cb + 0x24, (void*)SmClassHook) ? 1 : 0;
    if (done != 4) {
        Log("FAIL world precompute smooth memo: %d of 4 reader call sites written (each hook calls the game's function while no pass is open)", done);
        return;
    }
    c.smReady |= 2;
    if (!WxRetarget(site3, (void*)SmPassHook)) {
        Log("FAIL world precompute smooth memo: VirtualProtect at 0x%08lX (err %lu)", site3, GetLastError());
        return;
    }
    c.smReady |= 1;
    if (g_snOn) c.smReady |= 4;
    if (g_hostCallOrig) c.smReady |= 8;
    g_smAll = GetPrivateProfileIntW(L"WorldPrecompute", L"SmoothMemoAll", 0, ini) != 0 || g_hhToo == 1;
    c.smMode = mode;
    Log("OK   world precompute smooth memo: SmoothMemo=%d (%s), served to %s; ready 0x%lX (pass 3 call 0x%08lX -> 0x%08lX, readers 0x%08lX / "
        "0x%08lX at 0x%08lX, 0x%08lX, 0x%08lX, 0x%08lX); %s; %d threads x %lu KB",
        mode, mode == 2 ? "shadow: every evaluation runs" : "serve", g_smAll ? "every router except possible player commands" : "NPC routers only",
        c.smReady, site3, now3, m, cls, q + 0x62, q + 0x70, cb + 0x0E, cb + 0x24,
        g_snOn ? "[RouteSnap]'s write sequence drops the records" : "[RouteSnap] counting is off: the routing lock alone keeps the trees still",
        (int)SM_THREADS, (DWORD)(sizeof(SmTab) / 1024));
}

// the 60 s counter line (ts3native.cpp MaybeLogCounters)
static void SmLogCounters() {
    const SmoothMemoCounters& c = ts3native_smoothmemo;
    if (!c.smReady) return;
    Log("world precompute: smooth memo mode %ld ready 0x%lX: pass 3 calls with the memo %ld (%ld ms; of them shadow by scope %ld), without %ld "
        "(%ld ms; no block %ld); cell evaluations asked %ld: from a record %ld, the game's %ld (kept %ld; not kept: class 1 %ld, slots "
        "taken %ld, other %ld), tree with pending edits %ld; records dropped in a pass %ld; shadow checks %ld mismatch %ld (the game's "
        "evaluations a record would have replaced %ld ms)",
        c.smMode, c.smReady, c.smPasses, (LONG)(c.smPassUs / 1000), c.smScopePasses, c.smPassesOff, (LONG)(c.smPassOffUs / 1000), c.smNoTab,
        c.smEvals, c.smHits, c.smMiss, c.smStores, c.smClass1, c.smFull, c.smNoCtx, c.smDirty, c.smResets, c.smChecks, c.smMismatch,
        (LONG)(c.smCheckUs / 1000));
}
#endif
