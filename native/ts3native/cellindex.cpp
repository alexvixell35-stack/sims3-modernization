// docs/669: [RouteSpeed] CellIndex, a result-identical side index for the cell table lookup of the in-room cell A*. Included
// by ts3native.cpp after floodmemo.cpp; installed after InstallRouteSpeed (shares its Q table clear stub, RsInstallClear).
//
// AStarCellTable_Find 0x664370 [verified decompile + disassembly, docs/98 §4, docs/669 §3]: thiscall on the bucket array
// T (= Q+0x28, 10007 dwords), stack args (out, key), ret 8, returns out in eax. bucket = (key[0] xor key[1]) mod 10007; it
// walks the chain (link at cell+0x2C, pointing at the next cell's +0x2C) for a cell with +0x30 == key[0] and +0x34 == key[1]
// and writes out = {cell, &T[bucket]}, or out = {T[10007], &T[10007]} (the end slot, 0xFFFFFFFF since the constructor
// 0x61BF60) when the chain has none. It reads T and the chained cells' +0x2C / +0x30 / +0x34 only, and writes only out.
// Callers: 0x664590 (find, from QuadCell_CollectSideCells) and 0x6645D0 (find or create) [verified CallerTree]; both read
// out[0] back from their stack and use no register the call leaves.
// The table only grows between clears [verified: the element count T+0x9C60 is written by the insert 0x61BC66 (add), the
// constructor 0x61BFB4 and the clear 0x61C020 only; no other code touches T+0x9C5C / +0x9C60 / Q+0x9C84 / +0x9C88], and
// keys are unique (every create, 0x61C2C0 from 0x66B819 and 0x664613, follows a failed find of the same key). So once
// Find(T, key) returned cell C, it returns C with the same bucket address until T's next clear.
// The index keeps that answer per thread and serves a repeat: out = {C, bucket}, eax = out, after checking that C still
// carries the key words. "Not found" is never kept (the caller creates the cell next), so such a lookup always runs the
// game's function, as does everything on a thread past CI_THREADS.
//
// Invalidation (cellindex-core.h): the Q table clear sequence g_nmClearSeq (routespeed.cpp's stub on 0x61C020; every
// caller of the clear, the Q reset 0x61E320 and the destructor 0x61E420, goes through it). Quadtree writes do not matter:
// the answer is a function of the table's content only.
//
// Household rule: the answer is the game's own cell and bucket, so it is exact for every router; still, as NeighbourMemo,
// a served answer goes only to routers of known NPC scope until a shadow run shows 0 mismatches, unless [RouteSpeed]
// CellIndexAll=1 or [Experimental] HouseholdToo=1 (a cached exact answer).
//
// Modes (ts3native_cellindex.ciMode, runtime switch, native_set cellindex): 0 off (one compare), 1 serve, 2 shadow (the
// game's function always runs; a kept answer that differs counts ciMismatch and logs, 10 lines). Every 64th call that
// runs the game's function is timed and its chain is walked once more to count the cells on it (ciWalks / ciSteps /
// ciStepMax) and to read the table's element count (ciCountMax): the measured load of the game's table.
// Memory: one 393 KB index per planning thread, at most CI_THREADS (4).
#include "cellindex-core.h"

// exported block, not in the ts3fixes slot range (docs/registry.md "native exports"); the harness flips ciMode by export name
struct CellIndexCounters {
    volatile LONG ciMode;      // +0x00 runtime switch: 0 off, 1 serve, 2 shadow
    volatile LONG ciReady;     // +0x04 bit 0 find hook, bit 1 table-clear hook
    volatile LONG ciCalls;     // +0x08 lookups looked at (mode 1 / 2)
    volatile LONG ciHits;      // +0x0C answers served from the index
    volatile LONG ciStores;    // +0x10 answers of the game's function kept
    volatile LONG ciResets;    // +0x14 index cleared (a table clear since the fill)
    volatile LONG ciNotFound;  // +0x18 the game's function found no cell (never kept)
    volatile LONG ciStale;     // +0x1C kept cell no longer carries the key: not served (must stay 0)
    volatile LONG ciScopeMiss; // +0x20 kept, but the router is household / selected / unknown: the game's function ran
    volatile LONG ciEvict;     // +0x24 a full set lost an entry
    volatile LONG ciChecks;    // +0x28 shadow: kept answers compared with the game's
    volatile LONG ciMismatch;  // +0x2C shadow: answers that differed (must stay 0)
    volatile LONG ciNoIndex;   // +0x30 lookups on a thread past CI_THREADS
    volatile LONG ciWalks;     // +0x34 sampled chain walks (every 64th call of the game's function)
    volatile LONG ciSteps;     // +0x38 cells visited on those chains
    volatile LONG ciStepMax;   // +0x3C longest sampled walk
    volatile LONG ciCountMax;  // +0x40 most cells seen in one table (T+0x9C60)
    volatile LONG ciMissUs;    // +0x44 sampled time of the game's function, us
    volatile LONG ciMissN;     // +0x48 calls in that sample
    volatile LONG ciLines;     // +0x4C log lines written
};
extern "C" __declspec(dllexport) CellIndexCounters ts3native_cellindex = {};

static const DWORD CI_FIND = TS3A(0x00664370);
static const u8 kCiFindPro[8] = {0x83, 0xEC, 0x08, 0x56, 0x8B, 0x74, 0x24, 0x14};  // sub esp,8; push esi; mov esi,[esp+14h]
enum { CI_THREADS = 4, CI_END_OFF = CI_BUCKETS * 4, CI_COUNT_OFF = CI_BUCKETS * 4 + 4 };

typedef DWORD*(__fastcall* CiFindFn)(void* table, void* edx, DWORD* out, const DWORD* key);
static CiFindFn g_ciFindNext;
static volatile LONG g_ciThreads;
static bool g_ciAll;  // CellIndexAll=1 (or HouseholdToo): every router takes served answers
static __declspec(thread) CiIndex* t_ci;
static __declspec(thread) bool t_ciNone;  // this thread got no index (cap reached or no memory)
static __declspec(thread) LONG t_ciTick;

static CiIndex* CiThreadIndex() {
    if (t_ci) return t_ci;
    if (t_ciNone) return NULL;
    if (InterlockedIncrement(&g_ciThreads) > CI_THREADS) { t_ciNone = true; return NULL; }
    t_ci = (CiIndex*)VirtualAlloc(NULL, sizeof(CiIndex), MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);  // zeroed: gen 0 = not synced
    if (!t_ci) t_ciNone = true;
    return t_ci;
}

static bool CiScopeOk() {
    if (g_ciAll) return true;
    bool worker = false;
    const LONG s = PccScopeNow(&worker);  // plancand.cpp: the sim session's or the worker job's router scope
    return s == 2 || s == 3;
}

// cells the game's walk visits for this key: up to and with `found`, or the whole chain when found is 0. The same reads
// as 0x664396-0x6643AA; capped, so a damaged chain cannot hold the thread here
static LONG CiChainSteps(const void* table, uint32_t k0, uint32_t k1, uintptr_t found) {
    uintptr_t p = ((const DWORD*)table)[CiBucket(k0, k1)];
    LONG n = 0;
    while (p && n < 4096) {
        n++;
        if (p == found) break;
        const uintptr_t next = *(const DWORD*)(p + 0x2C);
        if (!next) break;
        p = next - 0x2C;
    }
    return n;
}

static void CiMismatchLog(const void* table, uint32_t k0, uint32_t k1, uintptr_t kept, uintptr_t keptBucket, DWORD got, DWORD gotBucket) {
    CellIndexCounters& c = ts3native_cellindex;
    if (InterlockedIncrement(&c.ciLines) > 10) return;
    Log("CELL INDEX MISMATCH: table 0x%08lX key %08lX %08lX kept cell 0x%08lX bucket 0x%08lX, the game's answer 0x%08lX bucket 0x%08lX; "
        "clear seq %ld", (DWORD)(uintptr_t)table, k0, k1, (DWORD)kept, (DWORD)keptBucket, got, gotBucket, g_nmClearSeq);
}

// shortcut: the hot counters are plain increments (the planner runs on one or two threads; a lost count only skews the stats)
static DWORD* __fastcall CiFindHook(void* table, void* edx, DWORD* out, const DWORD* key) {
    CellIndexCounters& c = ts3native_cellindex;
    const LONG mode = c.ciMode;
    if (mode != 1 && mode != 2) return g_ciFindNext(table, edx, out, key);
    CiIndex* x = CiThreadIndex();
    if (!x) { c.ciNoIndex++; return g_ciFindNext(table, edx, out, key); }
    c.ciCalls++;
    if (CiSync(x, (uint32_t)g_nmClearSeq)) c.ciResets++;
    const uint32_t k0 = key[0], k1 = key[1];
    const uintptr_t tk = (uintptr_t)table;
    CiSlot* s = CiFind(x, tk, k0, k1);
    if (s && mode == 1) {
        const u8* cp = (const u8*)s->cell;
        const bool keyOk = *(const DWORD*)(cp + 0x30) == k0 && *(const DWORD*)(cp + 0x34) == k1;
        const bool scope = keyOk && CiScopeOk();
        if (CiServeRule(mode, true, keyOk, scope)) {
            out[0] = (DWORD)s->cell;  // as 0x6643D6-0x6643CC: the cell, then its bucket's address
            out[1] = (DWORD)s->bucket;
            c.ciHits++;
            return out;
        }
        if (!keyOk) { c.ciStale++; CiDrop(s); s = NULL; } else c.ciScopeMiss++;
    }
    LARGE_INTEGER t0 = {}, t1 = {};
    const bool timed = (++t_ciTick & 63) == 0;
    if (timed) QueryPerformanceCounter(&t0);
    DWORD* const r = g_ciFindNext(table, edx, out, key);
    if (timed) QueryPerformanceCounter(&t1);
    const DWORD end = (DWORD)(tk + CI_END_OFF);
    const bool found = out[1] != end;
    if (timed) {
        c.ciMissUs += (LONG)((t1.QuadPart - t0.QuadPart) * 1000000 / g_qpf.QuadPart);
        c.ciMissN++;
        const LONG steps = CiChainSteps(table, k0, k1, found ? out[0] : 0);
        c.ciWalks++;
        c.ciSteps += steps;
        if (steps > c.ciStepMax) c.ciStepMax = steps;
        const LONG count = *(const LONG*)((const u8*)table + CI_COUNT_OFF);
        if (count > c.ciCountMax) c.ciCountMax = count;
    }
    if (s) {
        if (mode == 2) {
            c.ciChecks++;
            if (!found || out[0] != (DWORD)s->cell || out[1] != (DWORD)s->bucket) {
                InterlockedIncrement(&c.ciMismatch);
                CiMismatchLog(table, k0, k1, s->cell, s->bucket, out[0], out[1]);
            }
        }
        if (found && out[0] == (DWORD)s->cell && out[1] == (DWORD)s->bucket) return r;
        if (!found) { CiDrop(s); c.ciNotFound++; return r; }
    }
    if (!found) { c.ciNotFound++; return r; }
    const int st = CiStore(x, tk, k0, k1, out[0], out[1]);
    if (st == CI_EVICT) c.ciEvict++;
    if (st != CI_SAME) c.ciStores++;
    return r;
}

#ifndef CELLINDEX_HOST_TEST  // the install and the log line need the rest of ts3native.cpp
static void InstallCellIndex(const wchar_t* ini) {
    CellIndexCounters& c = ts3native_cellindex;
    const int ci = Clamp(GetPrivateProfileIntW(L"RouteSpeed", L"CellIndex", 0, ini), 0, 2);
    if (!ci) { Log("route speed cell index off ([RouteSpeed] CellIndex=0)"); return; }
#ifdef TS3A_TABLE
    if (ts3a_noaddr) { Log("SKIP route speed cell index: no EA 1.69 address, docs/669"); return; }
#endif
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
    g_ciAll = GetPrivateProfileIntW(L"RouteSpeed", L"CellIndexAll", 0, ini) != 0 || g_hhToo == 1;
    // the clear hook first: the index never serves without it
    if (RsInstallClear()) c.ciReady |= 2;
    if (!(c.ciReady & 2)) { Log("FAIL route speed cell index: 0x%08lX differs and no known owner", NM_CLEAR); return; }
    g_ciFindNext = (CiFindFn)InstallDetour("AStarCellTable_Find (cell index)", CI_FIND, kCiFindPro, sizeof(kCiFindPro), (void*)CiFindHook, &g_ciFindNext);
    if (!g_ciFindNext) return;  // the clear stub stays: it only counts
    c.ciReady |= 1;
    c.ciMode = ci;
    Log("OK   route speed cell index: 0x%08lX, clear 0x%08lX, CellIndex=%d (%s), served to %s; %d threads x %lu KB",
        CI_FIND, NM_CLEAR, ci, ci == 2 ? "shadow: the game's function always runs" : "serve", g_ciAll ? "every router" : "NPC routers only",
        (int)CI_THREADS, (DWORD)(sizeof(CiIndex) / 1024));
}

// the 60 s counter line (ts3native.cpp MaybeLogCounters)
static void CiLogCounters() {
    const CellIndexCounters& c = ts3native_cellindex;
    if (!c.ciReady) return;
    const LONG avg10 = c.ciMissN ? (LONG)((LONGLONG)c.ciMissUs * 10 / c.ciMissN) : 0;       // 0.1 us units
    const LONG steps10 = c.ciWalks ? (LONG)((LONGLONG)c.ciSteps * 10 / c.ciWalks) : 0;      // 0.1 cells
    Log("route speed: cell index mode %ld ready 0x%lX calls %ld hits %ld stores %ld resets %ld; not served: not found %ld, stale %ld, "
        "scope %ld, no index %ld; evicted %ld; shadow checks %ld mismatch %ld; the game's chain %ld.%ld cells a walk (max %ld, n %ld), "
        "most cells in a table %ld (%ld buckets); the game's find %ld.%ld us (n %ld), saved ~%ld ms",
        c.ciMode, c.ciReady, c.ciCalls, c.ciHits, c.ciStores, c.ciResets, c.ciNotFound, c.ciStale, c.ciScopeMiss, c.ciNoIndex, c.ciEvict,
        c.ciChecks, c.ciMismatch, steps10 / 10, steps10 % 10, c.ciStepMax, c.ciWalks, c.ciCountMax, (LONG)CI_BUCKETS, avg10 / 10,
        avg10 % 10, c.ciMissN, (LONG)((LONGLONG)c.ciHits * avg10 / 10000));
}
#endif
