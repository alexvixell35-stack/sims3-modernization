// docs/663: [RouteSpeed] NeighbourMemo, a result-identical memo of the in-room cell A*'s neighbour generation. Included by
// ts3native.cpp after cellmemo.cpp; installed after [RouteSnap] (its tree-write sequence) and [RouteJobPhases].
//
// QuadCell_Neighbours 0x66B9C0 [verified decompile + disassembly, docs/98 §6.2]: thiscall on a cell object C (the 0x24-byte
// position record at +0), stack args (Q, out, path, records), ret 0x10. It releases out's elements (0x61BCE0, stdcall
// (begin, end), ret 8), empties it, then for the four sides climbs the quadtree from C's record (parent links, quadrant bits),
// mirrors down and collects the final cells, finding each in Q's cell table (0x664590) or creating it there (0x61C2C0), and
// appends it to out with one reference (+0x28). What it reads: C's record (+0x08 parent node, +0x0C node, +0x1C shape,
// +0x20 quadrant bits), the quadtree nodes' kind / child / parent / final-mask words, and Q's table. It never reads the agent,
// the context X, costs, the target or the open list. So within one Q table generation and one quadtree state the answer for
// (C, Q) is the same list of the same cell objects, in the same order:
//   - cells live in Q's table, which holds a reference on each (0x61C270 +1) and drops them only in its clear 0x61C020 (the Q
//     reset 0x61E320 at the start of every hierarchical plan, 0x65E345, and Q's init 0x61E420) [verified callers];
//   - every cell the list names already exists on a second call (made on the first), so a repeat call creates nothing.
// The memo serves a repeat (same C, same Q, no table clear and no quadtree write since it was filled): it releases out's
// elements with the game's 0x61BCE0, writes the kept list and adds one reference per cell, as the game's push does. The two
// scratch vectors (path, records) keep what the last real call left in them: the game clears both at its own start and the
// search frees them at its exit (0x61E59C); nothing else reads them [verified: their only users are 0x66B8D0 / 0x66AC60 /
// 0x66B6E0 inside this call]. The out vector must already have the room (capacity >= list), else the game's function runs
// (the push could reallocate through EA's allocator).
// Callers: 0x66BFD0 (QuadCell_ExpandNeighbours, from the cell A* loop 0x61E7B9 and 0x61EA85) and 0x61C780 (stamp reader,
// unused by the in-room wrappers) [verified CallerTree]. Both take the answer the same way.
//
// Invalidation (routespeed-core.h): Q table clear (own 5-byte stub on 0x61C020, or after [RouteGate]'s site-9 stub through
// g_rgTramp[9]) and g_snTreeSeq, bumped by [RouteSnap] SnEnter on the quadtree / lot-structure writer rows (W1 ApplyInsert,
// W2 ApplyRemove, W3 QuadTreeBuild, W4 LotGraph_Build, W5 LotTable_Insert, RemoveLot, ApplyLotUpdate), any thread, clone and
// replay writes included. So NeighbourMemo needs [RouteSnap]'s counting (routesnap.cpp turns it on for this key) with no
// skipped writer row.
//
// Household rule: the answer is the game's own list, so it is exact for every router; still, until a shadow run shows 0
// mismatches, a served answer goes only to routers of known NPC scope (unseen 2, seen NPC 3; NmScopeNow), unless
// [RouteSpeed] NeighbourMemoAll=1 or [Experimental] HouseholdToo=1 (realistic-only: a cached exact answer). Other routers
// run the game's function (their calls still fill the memo).
//
// Modes (ts3native_routespeed.nmMode, runtime switch, native_set neighbourmemo): 0 off (one compare), 1 serve, 2 shadow (the
// game's function always runs; a kept list that differs from its answer counts in nmMismatch and logs, 10 lines).
// Memory: one 246 KB memo per planning thread, at most NM_THREADS (4); a fifth thread runs the game's function.
#include "routespeed-core.h"

// exported block, not in the ts3fixes slot range (docs/registry.md "native exports"); the harness flips nmMode by export name
struct RouteSpeedCounters {
    volatile LONG nmMode;      // +0x00 runtime switch: 0 off, 1 serve, 2 shadow
    volatile LONG nmReady;     // +0x04 bit 0 neighbour hook, bit 1 table-clear hook, bit 2 tree-write sequence (RouteSnap)
    volatile LONG nmCalls;     // +0x08 calls looked at (mode 1 / 2)
    volatile LONG nmHits;      // +0x0C answers served from the memo
    volatile LONG nmServed;    // +0x10 neighbour cells written by served answers
    volatile LONG nmStores;    // +0x14 lists kept
    volatile LONG nmResets;    // +0x18 memo cleared (a table clear or a tree write since the fill)
    volatile LONG nmCapMiss;   // +0x1C found, but the out vector had no room: the game's function ran
    volatile LONG nmScopeMiss; // +0x20 found, but the router is household / selected / unknown: the game's function ran
    volatile LONG nmNoStore;   // +0x24 not kept: long list, pool or probe run full, a null cell
    volatile LONG nmChecks;    // +0x28 shadow: kept lists compared with the game's answer
    volatile LONG nmMismatch;  // +0x2C shadow: lists that differed (must stay 0)
    volatile LONG nmNoMemo;    // +0x30 calls on a thread past NM_THREADS
    volatile LONG nmMissUs;    // +0x34 sampled time of the game's function (every 64th uncached call), us
    volatile LONG nmMissN;     // +0x38 calls in that sample
    volatile LONG nmLines;     // +0x3C log lines written
};
extern "C" __declspec(dllexport) RouteSpeedCounters ts3native_routespeed = {};

static const DWORD NM_NB = TS3A(0x0066B9C0), NM_CLEAR = TS3A(0x0061C020), NM_RELEASE = TS3A(0x0061BCE0);
static const u8 kNmNbPro[5] = {0x83, 0xEC, 0x24, 0x56, 0x57};     // sub esp,24h; push esi; push edi
static const u8 kNmClearPro[5] = {0xB8, 0x70, 0x9C, 0x00, 0x00};  // mov eax,9C70h (the stack-probe call at +5 stays; = kRgSites[9])
enum { NM_THREADS = 4 };

typedef void(__fastcall* NmNbFn)(void* cell, void* edx, void* q, DWORD* out, void* path, void* recs);
typedef void(__stdcall* NmRelFn)(DWORD begin, DWORD end);
static NmNbFn g_nmNbNext;
static void* g_nmClearNext;
static NmRelFn g_nmRel = (NmRelFn)(uintptr_t)NM_RELEASE;  // the selftest points it at its own
static volatile LONG g_nmClearSeq;
static volatile LONG g_nmThreads;
static bool g_nmAll;  // NeighbourMemoAll=1 (or HouseholdToo): every router takes served answers
static __declspec(thread) NmMemo* t_nm;
static __declspec(thread) bool t_nmNone;  // this thread got no memo (cap reached or no memory)
static __declspec(thread) LONG t_nmTick;

static NmMemo* NmThreadMemo() {
    if (t_nm) return t_nm;
    if (t_nmNone) return NULL;
    if (InterlockedIncrement(&g_nmThreads) > NM_THREADS) { t_nmNone = true; return NULL; }
    t_nm = (NmMemo*)VirtualAlloc(NULL, sizeof(NmMemo), MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);  // zeroed: gen 0 = not synced
    if (!t_nm) t_nmNone = true;
    return t_nm;
}

// the router scope of the plan running on this thread: the sim session's (plandeadline.cpp t_pd.scope, read at its open) or
// the worker job's (t_hcScope, set by ServeAsync for every job); anything else = 0 (unknown, treated as household)
static LONG NmScopeNow() {
    const DWORD tid = GetCurrentThreadId();
    if (t_pd.open && tid == g_pdTid) return t_pd.scope;
    if (tid == g_pdWkTid) return t_hcScope;
    return t_jobT0 != 0 ? t_hcScope : 0;
}
static bool NmScopeOk() {
    if (g_nmAll) return true;
    const LONG s = NmScopeNow();
    return s == 2 || s == 3;
}

static void NmMismatchLog(const void* cell, const void* q, uint32_t want, uint32_t got) {
    RouteSpeedCounters& c = ts3native_routespeed;
    if (InterlockedIncrement(&c.nmLines) > 10) return;
    Log("NEIGHBOUR MEMO MISMATCH: cell 0x%08lX Q 0x%08lX kept %lu cells, the game's answer %lu (or another order); tree seq %ld, "
        "clear seq %ld", (DWORD)(uintptr_t)cell, (DWORD)(uintptr_t)q, want, got, g_snTreeSeq, g_nmClearSeq);
}

// shortcut: the hot counters are plain increments (the planner runs on one or two threads; a lost count only skews the stats)
static void __fastcall NmNbHook(void* cell, void* edx, void* q, DWORD* out, void* path, void* recs) {
    RouteSpeedCounters& c = ts3native_routespeed;
    const LONG mode = c.nmMode;
    if (mode != 1 && mode != 2) { g_nmNbNext(cell, edx, q, out, path, recs); return; }
    NmMemo* m = NmThreadMemo();
    if (!m) { c.nmNoMemo++; g_nmNbNext(cell, edx, q, out, path, recs); return; }
    c.nmCalls++;
    if (NmSync(m, (uint32_t)g_nmClearSeq, (uint32_t)g_snTreeSeq)) c.nmResets++;
    const u8* cp = (const u8*)cell;
    const uint32_t k0 = *(const DWORD*)(cp + 0x30), k1 = *(const DWORD*)(cp + 0x34);
    const uintptr_t qk = (uintptr_t)q, ck = (uintptr_t)cell;
    const NmSlot* s = NmFind(m, qk, ck, k0, k1);
    if (s && mode == 1) {
        const bool scope = NmScopeOk();
        const uint32_t cap = (out[2] - out[0]) / 4;
        if (NmServeRule(mode, true, scope, cap, s->n)) {
            g_nmRel(out[0], out[1]);  // as the game's first act: release what out holds, then empty it
            out[1] = out[0];
            DWORD* w = (DWORD*)(uintptr_t)out[0];
            const uintptr_t* l = NmList(m, s);
            for (uint32_t j = 0; j < s->n; j++) {
                w[j] = (DWORD)l[j];
                (*(LONG*)((u8*)l[j] + 0x28))++;  // the out vector's reference, as the game's push takes it
            }
            out[1] = out[0] + s->n * 4;
            c.nmHits++;
            c.nmServed += (LONG)s->n;
            return;
        }
        if (!scope) c.nmScopeMiss++; else c.nmCapMiss++;
    }
    LARGE_INTEGER t0 = {}, t1;
    const bool timed = !s && (++t_nmTick & 63) == 0;
    if (timed) QueryPerformanceCounter(&t0);
    g_nmNbNext(cell, edx, q, out, path, recs);
    if (timed) {
        QueryPerformanceCounter(&t1);
        c.nmMissUs += (LONG)((t1.QuadPart - t0.QuadPart) * 1000000 / g_qpf.QuadPart);
        c.nmMissN++;
    }
    const uint32_t n = (out[1] - out[0]) / 4;
    const uintptr_t* got = (const uintptr_t*)(uintptr_t)out[0];
    if (s) {
        if (mode == 2) {
            c.nmChecks++;
            if (!NmSame(m, s, got, n)) { InterlockedIncrement(&c.nmMismatch); NmMismatchLog(cell, q, s->n, n); }
        }
        return;
    }
    if (NmStore(m, qk, ck, k0, k1, got, n) == NM_STORED) c.nmStores++; else c.nmNoStore++;
}

// 0x61C020 entry (Q's cell table clear): count, then the game's code (own detour, or after [RouteGate]'s stub). eax/ecx are
// the function's own (mov eax,imm is its first instruction; ecx = Q passes through); flags are dead at a call.
static __declspec(naked) void NmClearStub() {
    __asm {
        lock inc dword ptr [g_nmClearSeq]
        jmp dword ptr [g_nmClearNext]
    }
}

static void InstallRouteSpeed(const wchar_t* ini) {
    RouteSpeedCounters& c = ts3native_routespeed;
    const int nm = Clamp(GetPrivateProfileIntW(L"RouteSpeed", L"NeighbourMemo", 0, ini), 0, 2);
    if (!nm) { Log("route speed off ([RouteSpeed] NeighbourMemo=0)"); return; }
#ifdef TS3A_TABLE
    if (ts3a_noaddr) { Log("SKIP route speed: no EA 1.69 address, docs/663"); return; }
#endif
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
    const Ts3nCounters& k = ts3native_counters;
    if (!g_snOn || k.snSkipped || !g_snTreeRows) {  // the tree-write sequence must see every writer row
        Log("SKIP route speed neighbour memo: needs [RouteSnap] counting with every writer row (on %ld, skipped %ld, tree rows %ld)",
            g_snOn, k.snSkipped, g_snTreeRows);
        return;
    }
    c.nmReady |= 4;
    g_nmAll = GetPrivateProfileIntW(L"RouteSpeed", L"NeighbourMemoAll", 0, ini) != 0 || g_hhToo == 1;
    // the clear hook first: the memo never serves without it
    if (Matches((const void*)(uintptr_t)NM_CLEAR, kNmClearPro, sizeof(kNmClearPro))) {
        if (InstallDetour("Q cell table clear (neighbour memo)", NM_CLEAR, kNmClearPro, sizeof(kNmClearPro), (void*)NmClearStub, &g_nmClearNext))
            c.nmReady |= 2;
    } else if (g_rgTramp[9] && kRgSites[9].addr == NM_CLEAR) {  // [RouteGate] owns the entry: we run after its stub
        g_nmClearNext = g_rgTramp[9];
        InterlockedExchangePointer(&g_rgTramp[9], (void*)NmClearStub);
        c.nmReady |= 2;
        Log("OK   Q cell table clear (neighbour memo): after the [RouteGate] stub");
    }
    if (!(c.nmReady & 2)) { Log("FAIL route speed neighbour memo: 0x%08lX differs and no known owner", NM_CLEAR); return; }
    g_nmNbNext = (NmNbFn)InstallDetour("QuadCell_Neighbours (neighbour memo)", NM_NB, kNmNbPro, sizeof(kNmNbPro), (void*)NmNbHook, &g_nmNbNext);
    if (!g_nmNbNext) return;  // the clear stub stays: it only counts
    c.nmReady |= 1;
    c.nmMode = nm;
    Log("OK   route speed neighbour memo: 0x%08lX, clear 0x%08lX, NeighbourMemo=%d (%s), served to %s; %d threads x %lu KB",
        NM_NB, NM_CLEAR, nm, nm == 2 ? "shadow: the game's function always runs" : "serve", g_nmAll ? "every router" : "NPC routers only",
        (int)NM_THREADS, (DWORD)(sizeof(NmMemo) / 1024));
}

// the 60 s counter line (ts3native.cpp MaybeLogCounters)
static void RsLogCounters() {
    const RouteSpeedCounters& c = ts3native_routespeed;
    if (!c.nmReady) return;
    const LONG avg10 = c.nmMissN ? (LONG)((LONGLONG)c.nmMissUs * 10 / c.nmMissN) : 0;  // 0.1 us units
    Log("route speed: neighbour memo mode %ld ready 0x%lX calls %ld hits %ld (cells %ld) stores %ld resets %ld; not served: no room %ld, "
        "scope %ld, not kept %ld, no memo %ld; shadow checks %ld mismatch %ld; uncached call %ld.%ld us (n %ld), saved ~%ld ms",
        c.nmMode, c.nmReady, c.nmCalls, c.nmHits, c.nmServed, c.nmStores, c.nmResets, c.nmCapMiss, c.nmScopeMiss, c.nmNoStore,
        c.nmNoMemo, c.nmChecks, c.nmMismatch, avg10 / 10, avg10 % 10, c.nmMissN, (LONG)((LONGLONG)c.nmHits * avg10 / 10000));
}
