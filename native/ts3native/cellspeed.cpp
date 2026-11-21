// docs/669 §8-§10: [RouteSpeed] ClassMemo and OpenHint, two result-identical speed-ups inside the in-room cell A*
// 0x61E500. Included by ts3native.cpp after cellindex.cpp; installed after InstallRouteSpeed (shares its Q table clear
// stub, RsInstallClear). Both default off, each with its own key, a shadow mode and one 60 s counter line.
//
// ---- ClassMemo: the cell class recompute 0x66B090 ----
// QuadCell_Recompute 0x66B090 [verified decompile + disassembly]: thiscall on a cell position (ecx), one stack argument X
// (the lot search context), ret 4. Its four callers are the two cached getters QuadCell_Class 0x66B600 and
// QuadCell_Multiplier 0x66B670, which call it whenever the cell's key (cell+0x1C >> 8) is not X+0x50. It writes the
// class byte cell+0x20 and the multiplier cell+0x10, clears the key (cell+0x1C &= 0xFF) for a class 1 cell, and may
// lower X+0x4C; nothing else [verified: the function's own stores are 0x66B0B0..0x66B5A2 to esi+0x20, ebx (= esi+0x10),
// esi+0x1C, eax+0x1C (the same cell) and ebp+0x4C]. So a class 1 cell (a cell a Sim stands on, footprint type 0x14) is
// recomputed on every use: twice or more per edge that enters or leaves it.
// What the result depends on [verified, docs/669 §8.2]: the cell's tree (+0x04: default class), quadtree node (+0x0C: the
// leaf entries and their footprints) and shape byte; X+0x50, X+0x4C; the agent (cellspeed-core.h KmAgent). The footprint
// virtuals it calls (+0x00 count, +0x04 multiplier, +0x08 class, +0x0C type, +0x28 ignore, +0x60 applies, +0x84 / +0x7C
// object id; +0x90 / +0x94 add / release a reference around a temporary vector) store nothing but those reference
// counts, in all 11 footprint vtables. Not looked at: a cell with no node (the tree default, cheap), a cell whose key is
// 0, and a cell the small-cell clearance test can run for (flag 0x200000, sub-cell with a parent): that test reads the
// cell's rectangle and calls the rectangle slot +0x34, which fills the lazy polygon cache.
// Life of a kept result (KmSync): until the Q table clear sequence g_nmClearSeq (every plan), the routing write sequence
// g_snWriteSeq ([RouteSnap]: every writer row, Footprint_SetEnabled, every routing IC but the plan starts) or this
// thread's managed-call sequence t_fmHostSeq moves. That is one stretch of one plan, as [RouteSpeed] FloodMemo.
// A served result is the function's own four writes. Until a shadow run shows 0 mismatches it goes to NPC routers only.
//
// ---- OpenHint: the sorted insert 0x61B880 of the open list ----
// AStarBucket_InsertSorted 0x61B880 [verified, docs/98 §8.3]: thiscall on a 12-byte bucket {head, tail, end}, one stack
// argument (the node), ret 4; callers 0x61DF99 (insert) and 0x61E01A (update into another bucket), both after writing
// node+0x34. It walks from the head while node.f > cur.f and links the node before the first cur with cur.f >= node.f.
// The hook finds the same cur from a kept node of the same bucket instead of the head (cellspeed-core.h OhValid: the
// proof per use) and links the node with the game's own three cases (OhLink). The list, its order on ties and every pop
// are the game's. Buckets with fewer than OH_MIN inserts in this search, and every insert with no proven start, run the
// game's function.
// Kept nodes are only cells' own graph nodes (vtable 0xFEA080), which live until the Q table clear; a slot is emptied
// when the generation word 0x11580C4 or the clear sequence differs, before any kept pointer is read.
//
// Modes (runtime switches, native_set classmemo / openhint): 0 off (one compare), 1 serve, 2 shadow (the game's function
// always runs and the kept / hinted answer is compared: mismatch counters, 10 log lines each).
// Memory per planning thread (at most CS_THREADS): ClassMemo 170 KB, OpenHint 70 KB.
#include "cellspeed-core.h"

// exported blocks, not in the ts3fixes slot range (docs/registry.md "native exports"); the harness flips the modes by name
struct ClassMemoCounters {
    volatile LONG kmMode;       // +0x00 runtime switch: 0 off, 1 serve, 2 shadow
    volatile LONG kmReady;      // +0x04 bit 0 recompute hook, 1 table-clear hook, 2 write sequence, 3 managed-call sequence
    volatile LONG kmCalls;      // +0x08 recomputes looked at
    volatile LONG kmHits;       // +0x0C results served
    volatile LONG kmStores;     // +0x10 results kept
    volatile LONG kmResets;     // +0x14 memo cleared (a sequence moved)
    volatile LONG kmNoNode;     // +0x18 passed by: no quadtree node or no context or key 0
    volatile LONG kmClearance;  // +0x1C passed by: the small-cell clearance test can run
    volatile LONG kmNoAgent;    // +0x20 passed by: agent not usable (long id list) or more than KM_AGENTS agents
    volatile LONG kmScopeMiss;  // +0x24 kept, but the router is household / selected / unknown
    volatile LONG kmEvict;      // +0x28 a full set lost an entry
    volatile LONG kmChecks;     // +0x2C kept results compared with the game's (shadow, scope)
    volatile LONG kmMismatch;   // +0x30 of those: different (must stay 0)
    volatile LONG kmNoMemo;     // +0x34 calls on a thread past CS_THREADS
    volatile LONG kmClass1;     // +0x38 recomputes that ended in class 1 or a cleared key (the game caches none of them)
    volatile LONG kmMoved;      // +0x3C results not kept: a sequence moved during the call
    volatile LONG kmMissUs;     // +0x40 sampled time of the game's function, us
    volatile LONG kmMissN;      // +0x44 calls in that sample
    volatile LONG kmLines;      // +0x48 log lines written
};
struct OpenHintCounters {
    volatile LONG ohMode;       // +0x00 runtime switch: 0 off, 1 serve, 2 shadow
    volatile LONG ohReady;      // +0x04 bit 0 insert hook, 1 table-clear hook
    volatile LONG ohCalls;      // +0x08 inserts looked at
    volatile LONG ohHits;       // +0x0C inserts linked from a proven start
    volatile LONG ohSteps;      // +0x10 nodes passed on those walks
    volatile LONG ohShort;      // +0x14 game's function: bucket with fewer than OH_MIN inserts or no kept node
    volatile LONG ohNoStart;    // +0x18 game's function: no kept node passed the proof
    volatile LONG ohOdd;        // +0x1C game's function: end mark not 0
    volatile LONG ohScopeMiss;  // +0x20 game's function: router is household / selected / unknown
    volatile LONG ohChecks;     // +0x24 shadow: hinted position compared with the game's
    volatile LONG ohMismatch;   // +0x28 of those: different (must stay 0)
    volatile LONG ohNoCtx;      // +0x2C calls on a thread past CS_THREADS
    volatile LONG ohWalkN;      // +0x30 inserts whose walk from the head was counted (shadow: all; serve: every 64th hit)
    volatile LONG ohWalkSteps;  // +0x34 nodes the game's walk passes on those
    volatile LONG ohWalkMax;    // +0x38 longest such walk
    volatile LONG ohWalk8;      // +0x3C of ohWalkN: walks of 8 nodes or more
    volatile LONG ohHintSteps;  // +0x40 shadow: nodes the hinted walk would pass on the checked inserts
    volatile LONG ohLines;      // +0x44 log lines written
};
extern "C" __declspec(dllexport) ClassMemoCounters ts3native_classmemo = {};
extern "C" __declspec(dllexport) OpenHintCounters ts3native_openhint = {};

static const DWORD KM_RECALC = TS3A(0x0066B090), OH_INSERT = TS3A(0x0061B880);
static const DWORD CS_VT_GRAPH = TS3A(0x00FEA080);    // vtable of a cell's graph node (docs/98 §3.3)
static const DWORD CS_GEN = TS3A(0x011580C4);         // u16 search generation (0x61E360 bumps it)
static const DWORD CS_OPT_GLOBAL = TS3A(0x011D9128);  // 0x836200 returns it; non-zero = agents have a data block
// sub esp,68h; movss xmm0,[1158568h] (an absolute operand: copied as it is)
static const u8 kCmRecalcPro[11] = {0x83, 0xEC, 0x68, 0xF3, 0x0F, 0x10, 0x05, 0x68, 0x85, 0x15, 0x01};
// mov eax,[esp+4]; movss xmm0,[eax+24h]
static const u8 kOhInsertPro[9] = {0x8B, 0x44, 0x24, 0x04, 0xF3, 0x0F, 0x10, 0x40, 0x24};
enum { CS_THREADS = 4 };

typedef void(__fastcall* KmRecalcFn)(u8* cell, void* edx, u8* x);
typedef void(__fastcall* OhInsertFn)(DWORD* bucket, void* edx, DWORD node);
static KmRecalcFn g_kmNext;
static OhInsertFn g_ohNext;
static bool g_kmAll, g_ohAll;  // ClassMemoAll / OpenHintAll=1 (or HouseholdToo): every router takes served answers
// the selftest points these at its own words
static const volatile unsigned short* g_csGen = (const volatile unsigned short*)(uintptr_t)CS_GEN;
static const volatile DWORD* g_csOptGlobal = (const volatile DWORD*)(uintptr_t)CS_OPT_GLOBAL;
static DWORD g_csVtGraph = CS_VT_GRAPH;
static volatile LONG g_kmThreads, g_ohThreads;
static __declspec(thread) KmMemo* t_km;
static __declspec(thread) OhCtx* t_oh;
static __declspec(thread) bool t_kmNone, t_ohNone;  // this thread got no block (cap reached or no memory)
static __declspec(thread) LONG t_kmTick, t_ohTick;

static KmMemo* KmThreadMemo() {
    if (t_km) return t_km;
    if (t_kmNone) return NULL;
    if (InterlockedIncrement(&g_kmThreads) > CS_THREADS) { t_kmNone = true; return NULL; }
    t_km = (KmMemo*)VirtualAlloc(NULL, sizeof(KmMemo), MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);  // zeroed: gen 0 = not synced
    if (!t_km) t_kmNone = true;
    return t_km;
}
static OhCtx* OhThreadCtx() {
    if (t_oh) return t_oh;
    if (t_ohNone) return NULL;
    if (InterlockedIncrement(&g_ohThreads) > CS_THREADS) { t_ohNone = true; return NULL; }
    t_oh = (OhCtx*)VirtualAlloc(NULL, sizeof(OhCtx), MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);  // zeroed: slot gen 0 = never used
    if (!t_oh) t_ohNone = true;
    return t_oh;
}

static bool CsScopeOk(bool all) {
    if (all) return true;
    bool worker = false;
    const LONG s = PccScopeNow(&worker);  // plancand.cpp: the sim session's or the worker job's router scope
    return s == 2 || s == 3;
}

static void KmMismatchLog(const u8* cell, const u8* x, const KmSlot* s, u8 klass, DWORD mult, DWORD postCls, bool keyClear) {
    ClassMemoCounters& c = ts3native_classmemo;
    if (InterlockedIncrement(&c.kmLines) > 10) return;
    Log("CLASS MEMO MISMATCH: cell 0x%08lX node 0x%08lX shape %lu X 0x%08lX flags 0x%08lX agent %lu: kept class %d mult %08lX X+4C %lu "
        "key clear %d, the game's class %d mult %08lX X+4C %lu key clear %d; write seq %ld, clear seq %ld, host seq %ld",
        (DWORD)(uintptr_t)cell, (DWORD)s->node, (DWORD)s->shape, (DWORD)(uintptr_t)x, (DWORD)s->flags, (DWORD)s->agent, (int)(signed char)s->klass,
        (DWORD)s->mult, (DWORD)s->postCls, (int)s->keyClear, (int)(signed char)klass, mult, postCls, (int)keyClear, g_snWriteSeq, g_nmClearSeq,
        t_fmHostSeq);
}

// shortcut: the hot counters are plain increments (one or two planning threads; a lost count only skews the stats)
static void __fastcall KmRecalcHook(u8* cell, void* edx, u8* x) {
    ClassMemoCounters& c = ts3native_classmemo;
    const LONG mode = c.kmMode;
    if (mode != 1 && mode != 2) { g_kmNext(cell, edx, x); return; }
    const uintptr_t node = *(const DWORD*)(cell + 0x0C);
    const DWORD shapeWord = *(const DWORD*)(cell + 0x1C);
    const DWORD shape = shapeWord & 0xFF;
    const DWORD flags = x ? *(const DWORD*)(x + 0x50) : 0;
    const bool clearance = (flags & 0x200000) != 0 && shape != 0 && *(const DWORD*)(cell + 0x08) != 0;
    if (!KmEligible((uintptr_t)x, node, shapeWord >> 8, clearance)) {
        if (clearance) c.kmClearance++; else c.kmNoNode++;
        g_kmNext(cell, edx, x);
        return;
    }
    KmMemo* m = KmThreadMemo();
    if (!m) { c.kmNoMemo++; g_kmNext(cell, edx, x); return; }
    c.kmCalls++;
    const uint32_t sc = (uint32_t)g_nmClearSeq, sw = (uint32_t)g_snWriteSeq, sh = (uint32_t)t_fmHostSeq;
    if (KmSync(m, sc, sw, sh)) c.kmResets++;
    const uintptr_t opts = *(const DWORD*)(x + 0x40);
    const bool og = *g_csOptGlobal != 0;
    const int agent = KmAgentIndex(m, opts, og);
    if (agent < 0) { c.kmNoAgent++; g_kmNext(cell, edx, x); return; }
    const uintptr_t tree = *(const DWORD*)(cell + 0x04);
    const DWORD cls = *(const DWORD*)(x + 0x4C);
    KmSlot* s = KmFind(m, tree, node, shape, flags, cls, (uint32_t)agent);
    if (s && mode == 1) {
        if (KmServeRule(mode, true, CsScopeOk(g_kmAll))) {
            // the function's own writes, in its order: class and multiplier, X+0x4C, the key
            *(cell + 0x20) = s->klass;
            *(DWORD*)(cell + 0x10) = s->mult;
            if (s->postCls != cls) *(DWORD*)(x + 0x4C) = s->postCls;
            if (s->keyClear) *(DWORD*)(cell + 0x1C) = shapeWord & 0xFF;
            c.kmHits++;
            return;
        }
        c.kmScopeMiss++;
    }
    LARGE_INTEGER t0 = {}, t1 = {};
    const bool timed = (++t_kmTick & 63) == 0;
    if (timed) QueryPerformanceCounter(&t0);
    g_kmNext(cell, edx, x);
    if (timed) {
        QueryPerformanceCounter(&t1);
        c.kmMissUs += (LONG)((t1.QuadPart - t0.QuadPart) * 1000000 / g_qpf.QuadPart);
        c.kmMissN++;
    }
    const u8 klass = *(cell + 0x20);
    const DWORD mult = *(const DWORD*)(cell + 0x10), postCls = *(const DWORD*)(x + 0x4C);
    const bool keyClear = (*(const DWORD*)(cell + 0x1C) >> 8) == 0;
    if (keyClear) c.kmClass1++;
    // a result is kept (and a kept one compared) only when nothing it depends on moved while the function ran
    if (sc != (uint32_t)g_nmClearSeq || sw != (uint32_t)g_snWriteSeq || sh != (uint32_t)t_fmHostSeq ||
        !KmAgentSame(&m->agent[agent], opts, *g_csOptGlobal != 0) || *(const DWORD*)(x + 0x50) != flags) {
        c.kmMoved++;
        return;
    }
    if (s) {
        c.kmChecks++;
        if (s->klass != klass || s->mult != mult || s->postCls != postCls || (s->keyClear != 0) != keyClear) {
            InterlockedIncrement(&c.kmMismatch);
            KmMismatchLog(cell, x, s, klass, mult, postCls, keyClear);
        }
    }
    const int st = KmStore(m, tree, node, shape, flags, cls, (uint32_t)agent, klass, mult, postCls, keyClear);
    if (st == KM_EVICT) c.kmEvict++;
    if (st != KM_SAME) c.kmStores++;
}

static void OhMismatchLog(const DWORD* bucket, DWORD node, uintptr_t st, uintptr_t want, DWORD got) {
    OpenHintCounters& c = ts3native_openhint;
    if (InterlockedIncrement(&c.ohLines) > 10) return;
    Log("OPEN HINT MISMATCH: bucket 0x%08lX (index %lu) node 0x%08lX f %08lX start 0x%08lX: hinted successor 0x%08lX, the game's 0x%08lX; "
        "generation %u, clear seq %ld", (DWORD)(uintptr_t)bucket, *(const DWORD*)((uintptr_t)node + 0x34), node,
        *(const DWORD*)((uintptr_t)node + 0x24), (DWORD)st, (DWORD)want, got, (unsigned)*g_csGen, g_nmClearSeq);
}

// nodes the game's walk passes before `node` (which is linked in the bucket now)
static LONG OhGameSteps(const DWORD* bucket, DWORD node) {
    LONG n = 0;
    for (uintptr_t p = bucket[0]; p != bucket[2] && p != node && n < 100000; p = *(const DWORD*)(p + 0x3C)) n++;
    return n;
}
static void OhWalkNote(const DWORD* bucket, DWORD node) {
    OpenHintCounters& c = ts3native_openhint;
    const LONG g = OhGameSteps(bucket, node);
    c.ohWalkN++;
    c.ohWalkSteps += g;
    if (g > c.ohWalkMax) c.ohWalkMax = g;
    if (g >= 8) c.ohWalk8++;
}

static void __fastcall OhInsertHook(DWORD* bucket, void* edx, DWORD node) {
    OpenHintCounters& c = ts3native_openhint;
    const LONG mode = c.ohMode;
    if (mode != 1 && mode != 2) { g_ohNext(bucket, edx, node); return; }
    OhCtx* x = OhThreadCtx();
    if (!x) { c.ohNoCtx++; g_ohNext(bucket, edx, node); return; }
    c.ohCalls++;
    if (bucket[2] != 0) { c.ohOdd++; g_ohNext(bucket, edx, node); return; }
    const uintptr_t n = node;
    uint32_t* const b = (uint32_t*)bucket;  // DWORD and uint32_t are the same 32 bits
    const uint32_t idx = *(const DWORD*)(n + 0x34), gen16 = *g_csGen;
    const float nf = *(const float*)(n + 0x24);
    OhSlot* s = OhSlotFor(x, (uintptr_t)bucket, idx, gen16, (uint32_t)g_nmClearSeq);
    const uint32_t sub = OhSub(nf, idx);
    const bool keep = *(const DWORD*)n == g_csVtGraph;
    s->count++;
    uintptr_t st = 0;
    if (s->count > OH_MIN && s->mask) st = OhStart(s, b, n, nf, sub, gen16, idx);
    else c.ohShort++;
    if (st && mode == 1 && !CsScopeOk(g_ohAll)) { c.ohScopeMiss++; st = 0; }
    else if (!st && s->count > OH_MIN && s->mask) c.ohNoStart++;
    if (!st) {
        g_ohNext(bucket, edx, node);
        if (mode == 2) OhWalkNote(bucket, node);
        OhNote(s, n, nf, sub, keep);
        return;
    }
    uint32_t steps = 0;
    const uintptr_t cur = OhWalk(st, 0, nf, &steps);
    if (mode == 2) {  // shadow: the game links the node; its successor must be the hinted one
        g_ohNext(bucket, edx, node);
        c.ohChecks++;
        c.ohHintSteps += (LONG)steps;
        const DWORD got = *(const DWORD*)(n + 0x3C);
        if (got != (DWORD)cur) { InterlockedIncrement(&c.ohMismatch); OhMismatchLog(bucket, node, st, cur, got); }
        OhWalkNote(bucket, node);
        OhNote(s, n, nf, sub, keep);
        return;
    }
    OhLink(b, n, cur);
    c.ohHits++;
    c.ohSteps += (LONG)steps;
    if ((++t_ohTick & 63) == 0) OhWalkNote(bucket, node);
    OhNote(s, n, nf, sub, keep);
}

#ifndef CELLSPEED_HOST_TEST  // the install and the log lines need the rest of ts3native.cpp
static void InstallCellSpeed(const wchar_t* ini) {
    ClassMemoCounters& c = ts3native_classmemo;
    OpenHintCounters& o = ts3native_openhint;
    const int cm = Clamp(GetPrivateProfileIntW(L"RouteSpeed", L"ClassMemo", 0, ini), 0, 2);
    const int oh = Clamp(GetPrivateProfileIntW(L"RouteSpeed", L"OpenHint", 0, ini), 0, 2);
    if (!cm && !oh) { Log("route speed class memo / open hint off ([RouteSpeed] ClassMemo=0 OpenHint=0)"); return; }
#ifdef TS3A_TABLE
    if (ts3a_noaddr) { Log("SKIP route speed class memo / open hint: no EA 1.69 address, docs/669"); return; }
#endif
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
    const bool clear = RsInstallClear();  // both drop what they keep at the Q table clear
    if (!clear) { Log("FAIL route speed class memo / open hint: Q table clear 0x%08lX differs and no known owner", NM_CLEAR); return; }
    if (cm) {
        c.kmReady |= 2;
        const Ts3nCounters& k = ts3native_counters;
        if (!g_snOn || k.snSkipped || k.snIcSlots != SN_IC_N) {  // the write sequence must see every writer row and routing IC
            Log("SKIP route speed class memo: needs [RouteSnap] counting with every writer row and IC slot (on %ld, skipped %ld, IC slots %ld of %d)",
                g_snOn, k.snSkipped, k.snIcSlots, (int)SN_IC_N);
        } else if (!g_hostCallOrig) {  // t_fmHostSeq moves in the host-call hook: without it a managed call is not seen
            c.kmReady |= 4;
            Log("SKIP route speed class memo: needs the host call detour ([RouteAsync] Enabled=1) to see managed calls");
        } else {
            c.kmReady |= 4 | 8;
            g_kmAll = GetPrivateProfileIntW(L"RouteSpeed", L"ClassMemoAll", 0, ini) != 0 || g_hhToo == 1;
            g_kmNext = (KmRecalcFn)InstallDetour("QuadCell_Recompute (class memo)", KM_RECALC, kCmRecalcPro, sizeof(kCmRecalcPro), (void*)KmRecalcHook, &g_kmNext);
            if (g_kmNext) {
                c.kmReady |= 1;
                c.kmMode = cm;
                Log("OK   route speed class memo: 0x%08lX, ClassMemo=%d (%s), served to %s; %d threads x %lu KB",
                    KM_RECALC, cm, cm == 2 ? "shadow: the game's function always runs" : "serve", g_kmAll ? "every router" : "NPC routers only",
                    (int)CS_THREADS, (DWORD)(sizeof(KmMemo) / 1024));
            }
        }
    }
    if (oh) {
        o.ohReady |= 2;
        g_ohAll = GetPrivateProfileIntW(L"RouteSpeed", L"OpenHintAll", 0, ini) != 0 || g_hhToo == 1;
        g_ohNext = (OhInsertFn)InstallDetour("AStarBucket_InsertSorted (open hint)", OH_INSERT, kOhInsertPro, sizeof(kOhInsertPro), (void*)OhInsertHook, &g_ohNext);
        if (g_ohNext) {
            o.ohReady |= 1;
            o.ohMode = oh;
            Log("OK   route speed open hint: 0x%08lX, OpenHint=%d (%s), served to %s; %d threads x %lu KB",
                OH_INSERT, oh, oh == 2 ? "shadow: the game's function always runs" : "serve", g_ohAll ? "every router" : "NPC routers only",
                (int)CS_THREADS, (DWORD)(sizeof(OhCtx) / 1024));
        }
    }
}

// the 60 s counter lines (ts3native.cpp MaybeLogCounters)
static void CsLogCounters() {
    const ClassMemoCounters& c = ts3native_classmemo;
    if (c.kmReady & 1) {
        const LONG avg100 = c.kmMissN ? (LONG)((LONGLONG)c.kmMissUs * 100 / c.kmMissN) : 0;  // 0.01 us units
        Log("route speed: class memo mode %ld ready 0x%lX calls %ld hits %ld stores %ld resets %ld; uncached by the game (class 1 or key "
            "cleared) %ld; not looked at: no node %ld, clearance %ld, agent %ld, no memo %ld; not served: scope %ld; not kept: moved %ld; "
            "evicted %ld; checks %ld mismatch %ld; the game's recompute %ld.%02ld us (n %ld), saved ~%ld ms",
            c.kmMode, c.kmReady, c.kmCalls, c.kmHits, c.kmStores, c.kmResets, c.kmClass1, c.kmNoNode, c.kmClearance, c.kmNoAgent, c.kmNoMemo,
            c.kmScopeMiss, c.kmMoved, c.kmEvict, c.kmChecks, c.kmMismatch, avg100 / 100, avg100 % 100, c.kmMissN,
            (LONG)((LONGLONG)c.kmHits * avg100 / 100000));
    }
    const OpenHintCounters& o = ts3native_openhint;
    if (o.ohReady & 1) {
        const LONG walk10 = o.ohWalkN ? (LONG)((LONGLONG)o.ohWalkSteps * 10 / o.ohWalkN) : 0;                         // 0.1 nodes
        const LONG hint10 = o.ohHits ? (LONG)((LONGLONG)o.ohSteps * 10 / o.ohHits)
                                     : o.ohChecks ? (LONG)((LONGLONG)o.ohHintSteps * 10 / o.ohChecks) : 0;
        Log("route speed: open hint mode %ld ready 0x%lX inserts %ld from a hint %ld (%ld.%ld nodes a walk); the game's function: short "
            "bucket %ld, no proven start %ld, scope %ld, end mark %ld, no memo %ld; shadow checks %ld mismatch %ld; the game's walk "
            "%ld.%ld nodes (n %ld, max %ld, 8 or more %ld)",
            o.ohMode, o.ohReady, o.ohCalls, o.ohHits, hint10 / 10, hint10 % 10, o.ohShort, o.ohNoStart, o.ohScopeMiss, o.ohOdd, o.ohNoCtx,
            o.ohChecks, o.ohMismatch, walk10 / 10, walk10 % 10, o.ohWalkN, o.ohWalkMax, o.ohWalk8);
    }
}
#endif
