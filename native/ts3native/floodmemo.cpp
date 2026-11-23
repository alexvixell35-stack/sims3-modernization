// docs/667: [RouteSpeed] FloodMemo, a result-identical memo of failed in-room cell A* floods. Included by ts3native.cpp
// right after routespeed.cpp; installed after InstallRouteSpeed (shares its Q table clear stub). The hooks are plancand.cpp's:
// its cell A* entry hook calls FmCellEnter / FmCellLeave, its loop stub at 0x61E76E calls FmPop for every popped node.
//
// The cost it removes [measured, docs/667 §1; docs/663 §1]: a hierarchical plan that cannot reach its goal runs the cell A*
// 0x61E500 once or twice per candidate (lot legs from 0x65F582 and others), and every one of those searches floods the same
// closed region until its open list is empty: 95-136 searches and 290-500 ms per failed sync plan.
//
// Why a memo is exact [verified, docs/98 §5-§8 + disassembly of 0x61E500, 0x66BFD0, 0x61AF40, 0x61E360]:
//   - The search fails with an empty open list only after it has popped (closed) every node it could open. A node is opened
//     from a popped node P when the edge cost is below INF: the multiplier of both cells is finite and the type gate passes
//     (docs/98 §7.1). Both depend on the cells' footprint entries, the context flags X+0x50, X+0x4C and the agent (route
//     options X+0x40), never on the target, the threshold, the goals or the path so far. Path costs stay far below INF
//     (multipliers <= 100,000, lot-sized lengths), so the g >= INF rejects never fire.
//   - So the popped cell nodes of a failed flood are the closure C of its start cell under "edge is finite". A later search
//     with the same context and unchanged routing data whose start cell node is in C can open only nodes of C.
//   - Goals are extra nodes linked from the node of their cell (explicit neighbours, node +0x08..+0x0C; 0x61AF40 relaxes
//     them). If no node of C carries an explicit link other than the start node's own, the search cannot pop a goal: it
//     fails with an empty open list, as the game's own run would.
//   - A served failure is the game's own exit: the search starts as always (0x61E360: generation, target, start node
//     opened), pops the start node, and at the loop's node-cap test the stub jumps to 0x61E59C, the function's "open list
//     empty" exit (frees its two local vectors, releases the popped node, [Q+0x1C] = -1, AL 0, ret 0x18). With exactly one
//     start node the open list is empty at that point, as after the full flood. What differs is dead state: Q+0x24 (the
//     inserted-node count, zeroed at the next search's entry, no reader in between in the two wrappers) and the stamps /
//     parents of the flood's nodes, which every later search resets through the generation stamp before reading (0x61E030).
// What a record depends on, and what drops it (floodmemo-core.h FmSync):
//   - the cells and nodes of C live in Q's cell table until its clear 0x61C020 (docs/663 §4.2; a cell gets its node when it
//     is made, 0x61BEC0): the clear sequence g_nmClearSeq;
//   - the routing data: g_snWriteSeq, bumped by [RouteSnap] on every writer row except the two polygon cache "ensure" entries
//     (W6a / W6b, hit on every read), on Footprint_SetEnabled and on every routing IC except the four plan starts, any thread. The portal lock refresh of a
//     wrapper (0x632460) rewrites a footprint through 0x666C50 -> W1, so it counts;
//   - a managed call made on the planning thread (the door ask can run any script): t_fmHostSeq, unless FloodMemoHostKeep=1;
//   - the key: Q, the lot object, the options object and a hash of its first 0x38 bytes, X+0x50, X+0x4C.
// Kept only: a flood with one start node whose only link is its cell node, at most FM_MAXFLOOD cells, an empty open list at
// the exit (not the node cap, not a deadline cut), no sequence moved during it, no foreign link on any cell node.
//
// Household rule: the answer is the game's own failure, so it is exact for every router. Until a shadow run shows 0
// mismatches a served answer goes only to routers of known NPC scope (unseen 2, seen NPC 3). FloodMemoAll=1 (or
// [Experimental] HouseholdToo=1) adds household, selected and unknown routers, except a plan that may be a player command:
// those are served only while ts3fixes marks commands (NoPathMemo's op 39, switch npmhousehold) and this plan's route is not
// marked. A player-commanded Go Here always runs every search.
//
// Modes (ts3native_floodmemo.fmMode, runtime switch, native_set floodmemo): 0 off, 1 serve, 2 shadow (every search runs; a
// search the memo would have answered that then finds a path counts in fmMismatch and logs, 10 lines).
// Memory: one 0.6 MB block per planning thread (FmCtx), at most FM_THREADS.
#include "floodmemo-core.h"

// exported block, not in the ts3fixes slot range (docs/registry.md "native exports")
struct FloodMemoCounters {
    volatile LONG fmMode;        // +0x00 runtime switch: 0 off, 1 serve, 2 shadow
    volatile LONG fmReady;       // +0x04 bit 0 cell entry hook, 1 loop stub, 2 table-clear hook, 3 write sequence (RouteSnap)
    volatile LONG fmCalls;       // +0x08 searches looked at (mode 1 / 2)
    volatile LONG fmHits;        // +0x0C searches answered from a record
    volatile LONG fmStores;      // +0x10 floods kept
    volatile LONG fmResets;      // +0x14 memo cleared with records in it (a sequence moved)
    volatile LONG fmHostResets;  // +0x18 of those: a managed call on the planning thread
    volatile LONG fmLinkMiss;    // +0x1C start inside a record, but a goal (or another foreign link) hangs on one of its cells
    volatile LONG fmScopeMiss;   // +0x20 would serve, but the router is household / selected / unknown
    volatile LONG fmCmdMiss;     // +0x24 would serve (FloodMemoAll), but the plan may be a player command
    volatile LONG fmNoStore;     // +0x28 failed search not kept (cap or cut exit, long flood, full memo, odd links, a moved sequence)
    volatile LONG fmChecks;      // +0x2C predictions checked against the search that ran anyway (shadow, scope, command)
    volatile LONG fmMismatch;    // +0x30 of those: the search found a path (must stay 0)
    volatile LONG fmNoMemo;      // +0x34 searches on a thread past FM_THREADS
    volatile LONG fmFailUs;      // +0x38 time of the kept floods, us
    volatile LONG fmFailN;       // +0x3C kept floods timed
    volatile LONG fmSavedUs;     // +0x40 estimate: per hit, the mean time of a kept flood
    volatile LONG fmLines;       // +0x44 log lines written
    volatile LONG fmShape;       // +0x48 searches passed by: no context, not exactly one start node, an odd start link
    volatile LONG fmCells;       // +0x4C cells in kept floods
    // docs/673: how every search that ran ended, and how often a search that found a goal started where an earlier one did
    volatile LONG fmFound;       // +0x50 searches that ran and found a goal (AL 1)
    volatile LONG fmFoundPops;   // +0x54 nodes they popped
    volatile LONG fmFail;        // +0x58 searches that ran and failed without a deadline cut
    volatile LONG fmFailCap;     // +0x5C of those: the open list was not empty at the exit (the node cap)
    volatile LONG fmAborts;      // +0x60 searches a deadline cut ended
    volatile LONG fmRepStart;    // +0x64 found: same start cell and context as an earlier found search, no sequence moved since
    volatile LONG fmRepPops;     // +0x68 nodes those popped (what a kept search tree could have served)
    volatile LONG fmRepSame;     // +0x6C of fmRepStart: the goal index (Q+0x1C) also equals that start's last one
    volatile LONG fmFailUsAll;   // +0x70 time of all failed searches that ran, us
    volatile LONG fmFoundUs;     // +0x74 time of the found searches, us
    volatile LONG fmRepUs;       // +0x78 time of the fmRepStart searches, us
};
extern "C" __declspec(dllexport) FloodMemoCounters ts3native_floodmemo = {};

enum { FM_THREADS = 4, FM_REPS = 64 };
struct FmRep { uintptr_t cell; uint32_t keyHash; LONG goal; };  // docs/673: a start cell of a found search and its last goal index
struct FmCtx {
    bool active, serve, served, pred, abort;
    uint32_t n;                      // popped nodes of the running search (start node first)
    uintptr_t q, x, start, cell0;
    FmKey key;
    uint32_t seqClear, seqWrite, seqHost;
    LARGE_INTEGER t0;
    uintptr_t pop[FM_MAXFLOOD + 2];
    FmPair pairs[FM_MAXFLOOD];
    FmMemo m;
    FmRep rep[FM_REPS];  // docs/673, counting only: emptied with the memo (any sequence moved); the oldest slot is replaced
    uint32_t repN, repAt;
};
static __declspec(thread) FmCtx* t_fmc;
static __declspec(thread) bool t_fmNone;  // this thread got no block (cap reached or no memory)
static volatile LONG g_fmThreads;
static bool g_fmAll;       // FloodMemoAll=1 (or HouseholdToo): household / selected / unknown routers too, never a player command
static bool g_fmHostKeep;  // FloodMemoHostKeep=1: a managed call does not drop the records

static inline DWORD FmRd(uintptr_t a, int off) { return *(const DWORD*)(a + off); }

static FmCtx* FmThreadCtx() {
    if (t_fmc) return t_fmc;
    if (t_fmNone) return NULL;
    if (InterlockedIncrement(&g_fmThreads) > FM_THREADS) { t_fmNone = true; return NULL; }
    t_fmc = (FmCtx*)VirtualAlloc(NULL, sizeof(FmCtx), MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);  // zeroed: gen 0 = not synced
    if (!t_fmc) t_fmNone = true;
    return t_fmc;
}

// 1 = served to this router. NPC scope always; other scopes only with FloodMemoAll and a plan known not to be a command
static bool FmScopeOk() {
    FloodMemoCounters& c = ts3native_floodmemo;
    bool worker = false;
    const LONG s = PccScopeNow(&worker);  // plancand.cpp: the sim session's or the worker job's router scope
    if (s == 2 || s == 3) return true;
    if (!g_fmAll) { c.fmScopeMiss++; return false; }
    const DWORD h = worker ? g_async.handle : (t_pd.open ? t_pd.handle : 0);
    bool tracked = false;
    const bool cmd = NpmCmdHandle(h, &tracked);  // nopathmemo.cpp: ts3fixes' op 39 marks
    if (!h || !tracked || cmd) { c.fmCmdMiss++; return false; }
    return true;
}

static uint32_t FmOptHash(uintptr_t opts) {  // FNV-1a over the option words and ids (+0x00..+0x37; 0x81C880 reads up to +0x34)
    uint32_t h = 0x811C9DC5u;
    if (!opts) return h;
    for (const u8 *b = (const u8*)opts, *e = b + 0x38; b < e; b++) h = (h ^ *b) * 0x01000193u;
    return h;
}

// every cell of the flood still owns the node it had, and no node carries an explicit link other than the start node
static bool FmLinksOk(const FmPair* p, uint32_t n, uintptr_t start) {
    for (uint32_t i = 0; i < n; i++) {
        if (FmRd(p[i].cell, 0x38) != (DWORD)p[i].node) return false;
        for (const DWORD *l = (const DWORD*)(uintptr_t)FmRd(p[i].node, 0x08), *e = (const DWORD*)(uintptr_t)FmRd(p[i].node, 0x0C); l < e; l++)
            if (*l != (DWORD)start) return false;
    }
    return true;
}

static void FmMismatchLog(const FmCtx* t) {
    FloodMemoCounters& c = ts3native_floodmemo;
    if (InterlockedIncrement(&c.fmLines) > 10) return;
    Log("FLOOD MEMO MISMATCH: a search the memo would have answered found a path; Q 0x%08lX lot 0x%08lX flags 0x%08lX, %lu records, "
        "write seq %ld, clear seq %ld, host seq %ld", (DWORD)t->key.q, (DWORD)t->key.lot, (DWORD)t->key.flags, (DWORD)t->m.nRecs, g_snWriteSeq,
        g_nmClearSeq, t_fmHostSeq);
}

// plancand.cpp PccCellHook, before the search. q = the A* object, x = the lot search context X, startSet = arg 2
// shortcut: the hot counters are plain increments (one or two planning threads; a lost count only skews the stats)
static void FmCellEnter(void* q, DWORD x, DWORD startSet) {
    FloodMemoCounters& c = ts3native_floodmemo;
    FmCtx* t = t_fmc;
    if (t) t->active = false;
    const LONG mode = c.fmMode;
    if (mode != 1 && mode != 2) return;
    if (!t) t = FmThreadCtx();
    if (!t) { c.fmNoMemo++; return; }
    c.fmCalls++;
    const uint32_t sc = (uint32_t)g_nmClearSeq, sw = (uint32_t)g_snWriteSeq, sh = g_fmHostKeep ? 0 : (uint32_t)t_fmHostSeq;
    const bool had = t->m.nRecs != 0, hostMoved = t->m.seqHost != sh;
    if (FmSync(&t->m, sc, sw, sh)) {
        t->repN = t->repAt = 0;
        if (had) {
            c.fmResets++;
            if (hostMoved) c.fmHostResets++;
        }
    }
    if (!x || !startSet || FmRd(startSet, 0x14) != 1) { c.fmShape++; return; }  // exactly one start node (the wrappers' shape)
    const uintptr_t start = FmRd(FmRd(startSet, 0x08), 0x10);                 // the set's first tree node, its value
    if (!start) { c.fmShape++; return; }
    const DWORD lb = FmRd(start, 0x08), le = FmRd(start, 0x0C);
    if (le - lb != 4) { c.fmShape++; return; }  // the start node links its cell node and nothing else
    const uintptr_t node0 = *(const DWORD*)(uintptr_t)lb;
    const uintptr_t cell0 = node0 ? FmRd(node0, 0x1C) : 0;
    if (!cell0 || FmRd(cell0, 0x38) != (DWORD)node0) { c.fmShape++; return; }
    t->key.q = (uintptr_t)q, t->key.lot = FmRd(x, 0x54), t->key.opts = FmRd(x, 0x40);
    t->key.flags = FmRd(x, 0x50), t->key.cls = FmRd(x, 0x4C), t->key.optHash = FmOptHash(t->key.opts);
    t->q = (uintptr_t)q, t->x = x, t->start = start, t->cell0 = cell0;
    t->n = 0, t->serve = t->served = t->pred = t->abort = false;
    t->seqClear = sc, t->seqWrite = sw, t->seqHost = sh;
    t->active = true;
    QueryPerformanceCounter(&t->t0);
    const int rec = FmFind(&t->m, t->key, cell0);
    if (rec < 0) return;
    if (!FmLinksOk(FmPairs(&t->m, rec), t->m.rec[rec].n, start)) { c.fmLinkMiss++; return; }
    t->pred = true;  // this search must fail
    if (mode == 1 && FmServeRule(mode, true, true, FmScopeOk())) t->serve = true;
}

// plancand.cpp's loop stub, every popped node (the node-cap test passed). 1 = leave through the failure exit now
static int FmPop(DWORD node) {
    FmCtx* t = t_fmc;
    if (!t || !t->active) return 0;
    if (t->serve) { t->served = true; return 1; }  // the first pop: the start node, nothing expanded yet
    if (t->n < FM_MAXFLOOD + 2) t->pop[t->n] = node;
    t->n++;
    return 0;
}
// docs/673, counting only: a found search of `us` microseconds; was its start cell the start of an earlier found search under the same
// context and sequences, and did it end on the same goal index?
static uint32_t FmKeyHash(const FmKey& k) {
    uint32_t h = 0x811C9DC5u;
    const uint32_t w[6] = {(uint32_t)k.q, (uint32_t)k.lot, (uint32_t)k.opts, k.flags, k.cls, k.optHash};
    for (int i = 0; i < 6; i++) h = (h ^ w[i]) * 0x01000193u;
    return h;
}
static void FmFoundNote(FmCtx* t, void* q, LONG us) {
    FloodMemoCounters& c = ts3native_floodmemo;
    c.fmFound++;
    c.fmFoundPops += (LONG)t->n;
    c.fmFoundUs += us;
    const uint32_t kh = FmKeyHash(t->key);
    const LONG goal = (LONG)FmRd((uintptr_t)q, 0x1C);
    for (uint32_t i = 0; i < t->repN; i++) {
        FmRep& r = t->rep[i];
        if (r.cell != t->cell0 || r.keyHash != kh) continue;
        c.fmRepStart++;
        c.fmRepPops += (LONG)t->n;
        c.fmRepUs += us;
        if (r.goal == goal) c.fmRepSame++;
        r.goal = goal;
        return;
    }
    uint32_t at = t->repN;
    if (at < FM_REPS) t->repN++;
    else at = t->repAt, t->repAt = (t->repAt + 1) % FM_REPS;
    t->rep[at].cell = t->cell0, t->rep[at].keyHash = kh, t->rep[at].goal = goal;
}
static void FmAbort() {  // a deadline cut ends this search early: its failure proves nothing
    FmCtx* t = t_fmc;
    if (t) t->abort = true;
}

// plancand.cpp PccCellHook, after the search; al = its answer (low byte: 1 found, 0 failed)
static void FmCellLeave(void* q, DWORD al) {
    FloodMemoCounters& c = ts3native_floodmemo;
    FmCtx* t = t_fmc;
    if (!t || !t->active) return;
    t->active = false;
    const bool found = (u8)al != 0;
    if (t->served) {
        c.fmHits++;
        if (c.fmFailN) c.fmSavedUs += c.fmFailUs / c.fmFailN;
        return;
    }
    if (t->pred) {  // answered by the game although the memo knew: compare
        c.fmChecks++;
        if (found) { InterlockedIncrement(&c.fmMismatch); FmMismatchLog(t); }
        return;
    }
    LARGE_INTEGER t1;
    QueryPerformanceCounter(&t1);
    const LONG us = (LONG)((t1.QuadPart - t->t0.QuadPart) * 1000000 / g_qpf.QuadPart);
    if (t->abort) { c.fmAborts++; return; }
    if (found) { FmFoundNote(t, q, us); return; }
    // a real failure: keep its closed set when the flood ran to its end under one state
    const uintptr_t openList = FmRd((uintptr_t)q, 0x18);
    c.fmFail++;
    c.fmFailUsAll += us;
    if (!openList || (LONG)FmRd(openList, 0x18) >= 0) c.fmFailCap++;
    const bool whole = t->n >= 2 && t->n <= FM_MAXFLOOD + 1 && t->pop[0] == t->start && openList && (LONG)FmRd(openList, 0x18) < 0;
    const bool same = t->seqClear == (uint32_t)g_nmClearSeq && t->seqWrite == (uint32_t)g_snWriteSeq &&
                      t->seqHost == (g_fmHostKeep ? 0 : (uint32_t)t_fmHostSeq) && FmRd(t->x, 0x50) == t->key.flags &&
                      FmRd(t->x, 0x4C) == t->key.cls && FmOptHash(t->key.opts) == t->key.optHash;
    if (!whole || !same) { c.fmNoStore++; return; }
    const uint32_t n = t->n - 1;
    for (uint32_t i = 0; i < n; i++) {
        const uintptr_t node = t->pop[i + 1];
        const uintptr_t cell = FmRd(node, 0x1C);
        if (!cell || FmRd(cell, 0x38) != (DWORD)node) { c.fmNoStore++; return; }  // a node with no cell of its own: not a quadtree cell
        t->pairs[i].cell = cell, t->pairs[i].node = node;
    }
    if (!FmLinksOk(t->pairs, n, t->start)) { c.fmNoStore++; return; }
    if (FmStore(&t->m, t->key, t->pairs, n) != FM_STORED) { c.fmNoStore++; return; }
    c.fmStores++;
    c.fmCells += (LONG)n;
    c.fmFailUs += us;
    c.fmFailN++;
}

#ifndef FLOODMEMO_HOST_TEST  // floodmemo-host-test.cpp runs the search-side logic above without the game
static void InstallFloodMemo(const wchar_t* ini) {
    FloodMemoCounters& c = ts3native_floodmemo;
    const int fm = Clamp(GetPrivateProfileIntW(L"RouteSpeed", L"FloodMemo", 0, ini), 0, 2);
    if (!fm) { Log("route speed flood memo off ([RouteSpeed] FloodMemo=0)"); return; }
#ifdef TS3A_TABLE
    if (ts3a_noaddr) { Log("SKIP route speed flood memo: no EA 1.69 address, docs/667"); return; }
#endif
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
    const Ts3nCounters& k = ts3native_counters;
    if (!g_snOn || k.snSkipped || k.snIcSlots != SN_IC_N) {  // the write sequence must see every writer row and routing IC
        Log("SKIP route speed flood memo: needs [RouteSnap] counting with every writer row and IC slot (on %ld, skipped %ld, IC slots %ld of %d)",
            g_snOn, k.snSkipped, k.snIcSlots, (int)SN_IC_N);
        return;
    }
    c.fmReady |= 8;
    g_fmHostKeep = GetPrivateProfileIntW(L"RouteSpeed", L"FloodMemoHostKeep", 0, ini) != 0;
    if (!g_fmHostKeep && !g_hostCallOrig) {  // t_fmHostSeq moves in the host-call hook ([RouteAsync] worker): without it a managed call is not seen
        Log("SKIP route speed flood memo: needs the host call detour ([RouteAsync] Enabled=1) to see managed calls, or FloodMemoHostKeep=1");
        return;
    }
    if (!RsInstallClear()) { Log("FAIL route speed flood memo: Q table clear 0x%08lX differs and no known owner", NM_CLEAR); return; }
    c.fmReady |= 4;
    if (g_pccNext[PCC_CELL]) c.fmReady |= 1;
    if (g_pccCellLoop) c.fmReady |= 2;
    if ((c.fmReady & 3) != 3) {
        Log("FAIL route speed flood memo: the cell A* %s is not installed (plancand.cpp)", c.fmReady & 1 ? "loop stub" : "entry hook");
        return;
    }
    g_fmAll = GetPrivateProfileIntW(L"RouteSpeed", L"FloodMemoAll", 0, ini) != 0 || g_hhToo == 1;
    c.fmMode = fm;
    Log("OK   route speed flood memo: FloodMemo=%d (%s), served to %s; a managed call %s the records; %d threads x %lu KB",
        fm, fm == 2 ? "shadow: every search runs" : "serve",
        g_fmAll ? "every router except possible player commands" : "NPC routers only", g_fmHostKeep ? "keeps" : "drops",
        (int)FM_THREADS, (DWORD)(sizeof(FmCtx) / 1024));
}

// the 60 s counter line (ts3native.cpp MaybeLogCounters)
static void FmLogCounters() {
    const FloodMemoCounters& c = ts3native_floodmemo;
    if (!c.fmReady) return;
    Log("route speed: flood memo mode %ld ready 0x%lX searches %ld hits %ld stores %ld (cells %ld) resets %ld (managed call %ld); "
        "not served: goal link %ld, scope %ld, command %ld; not kept %ld, shape %ld, no memo %ld; checks %ld mismatch %ld; "
        "kept flood %ld us (n %ld), saved ~%ld ms",
        c.fmMode, c.fmReady, c.fmCalls, c.fmHits, c.fmStores, c.fmCells, c.fmResets, c.fmHostResets, c.fmLinkMiss, c.fmScopeMiss,
        c.fmCmdMiss, c.fmNoStore, c.fmShape, c.fmNoMemo, c.fmChecks, c.fmMismatch, c.fmFailN ? c.fmFailUs / c.fmFailN : 0, c.fmFailN,
        c.fmSavedUs / 1000);
    // docs/673: what the searches that ran did (the memo can only ever answer the "failed" class)
    Log("route speed: flood exits: found %ld (%ld ms, %ld pops), failed %ld (%ld ms; open list not empty %ld), cut %ld; found again "
        "from a start cell of an earlier found search, nothing written since: %ld (%ld ms, %ld pops), same goal index %ld",
        c.fmFound, c.fmFoundUs / 1000, c.fmFoundPops, c.fmFail, c.fmFailUsAll / 1000, c.fmFailCap, c.fmAborts, c.fmRepStart,
        c.fmRepUs / 1000, c.fmRepPops, c.fmRepSame);
}
#endif
