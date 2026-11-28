// docs/667 floodmemo.cpp: [RouteSpeed] FloodMemo. Included by selftest.cpp.
// The table (floodmemo-core.h: store, find, keys, sequences, caps) and FmCellEnter / FmPop / FmCellLeave on fake nodes and
// cells laid out as the game's (node: links +0x08..+0x0C, cell +0x1C; cell: node +0x38): a failed flood is kept, a later
// search that starts inside it and has no goal on its cells leaves at the first pop, a goal on one of its cells, another
// context, a routing write, a table clear or a managed call runs the search, a cap / cut exit is never kept, a household
// router and a possible player command run the search, shadow compares and counts a mismatch, mode 0 passes through.

struct FmFakeNode { u8 b[0x40]; DWORD links[4]; };
struct FmFakeCell { u8 b[0x3C]; };
static FmFakeNode g_fmNodes[8], g_fmStarts[3], g_fmGoal;
static FmFakeCell g_fmCells[8];
static u8 g_fmQ[0x28], g_fmOpen[0x1C], g_fmX[0x58], g_fmOpts[0x38], g_fmSet[0x18], g_fmTree[0x14];

static void FmFakeUnlink(FmFakeNode* n) { *(DWORD*)(n->b + 0x08) = *(DWORD*)(n->b + 0x0C) = (DWORD)(uintptr_t)n->links; }
static void FmFakeLink(FmFakeNode* n, const void* to) {
    DWORD* e = (DWORD*)(uintptr_t)*(DWORD*)(n->b + 0x0C);
    *e = (DWORD)(uintptr_t)to;
    *(DWORD*)(n->b + 0x0C) = (DWORD)(uintptr_t)(e + 1);
}
static void FmFakeWorld() {
    memset(g_fmNodes, 0, sizeof(g_fmNodes)), memset(g_fmStarts, 0, sizeof(g_fmStarts)), memset(&g_fmGoal, 0, sizeof(g_fmGoal));
    memset(g_fmCells, 0, sizeof(g_fmCells)), memset(g_fmQ, 0, sizeof(g_fmQ)), memset(g_fmOpen, 0, sizeof(g_fmOpen));
    memset(g_fmX, 0, sizeof(g_fmX)), memset(g_fmOpts, 0, sizeof(g_fmOpts)), memset(g_fmSet, 0, sizeof(g_fmSet));
    for (int i = 0; i < 8; i++) {
        FmFakeUnlink(&g_fmNodes[i]);
        *(DWORD*)(g_fmNodes[i].b + 0x1C) = (DWORD)(uintptr_t)&g_fmCells[i];
        *(DWORD*)(g_fmCells[i].b + 0x38) = (DWORD)(uintptr_t)&g_fmNodes[i];
    }
    for (int i = 0; i < 3; i++) FmFakeUnlink(&g_fmStarts[i]);
    FmFakeUnlink(&g_fmGoal);
    *(DWORD*)(g_fmQ + 0x18) = (DWORD)(uintptr_t)g_fmOpen;
    *(LONG*)(g_fmOpen + 0x18) = -1;  // open list empty
    *(DWORD*)(g_fmX + 0x40) = (DWORD)(uintptr_t)g_fmOpts, *(DWORD*)(g_fmX + 0x50) = 0x10000, *(DWORD*)(g_fmX + 0x54) = 0x5000;
    *(DWORD*)(g_fmSet + 0x08) = (DWORD)(uintptr_t)g_fmTree, *(DWORD*)(g_fmSet + 0x14) = 1;
}
// the wrapper's part: start node `s` on cell node `at`, the goal on cell node `goalAt` (-1 = none linked)
static void FmFakeSetup(int s, int at, int goalAt) {
    for (int i = 0; i < 8; i++) FmFakeUnlink(&g_fmNodes[i]);
    FmFakeUnlink(&g_fmStarts[s]);
    FmFakeLink(&g_fmStarts[s], &g_fmNodes[at]);
    FmFakeLink(&g_fmNodes[at], &g_fmStarts[s]);
    if (goalAt >= 0) FmFakeLink(&g_fmNodes[goalAt], &g_fmGoal);
    *(DWORD*)(g_fmTree + 0x10) = (DWORD)(uintptr_t)&g_fmStarts[s];
}
// the search: pops the start node, then cell nodes first..last unless the stub says leave. Returns -1 served at the first
// pop, else the number of pops; `found` = the game's answer when it runs
static int FmFakeSearch(int s, int first, int last, DWORD found) {
    FmCellEnter(g_fmQ, (DWORD)(uintptr_t)g_fmX, (DWORD)(uintptr_t)g_fmSet);
    int pops = 0;
    if (FmPop((DWORD)(uintptr_t)&g_fmStarts[s])) { FmCellLeave(g_fmQ, 0); return -1; }
    pops++;
    for (int i = first; i <= last; i++, pops++) {
        const int r = FmPop((DWORD)(uintptr_t)&g_fmNodes[i]);
        assert(!r);
        (void)r;
    }
    FmCellLeave(g_fmQ, found);
    return pops;
}

static void TestFmCore() {
    static FmMemo m;  // 0.5 MB: not on the stack
    memset(&m, 0, sizeof(m));
    assert(FmSync(&m, 1, 1, 1) && !FmSync(&m, 1, 1, 1) && FmSync(&m, 2, 1, 1) && FmSync(&m, 2, 2, 1) && FmSync(&m, 2, 2, 2));
    const FmKey k = {0x10, 0x20, 0x30, 0x10000, 0, 7};
    FmKey k2 = k;
    k2.flags = 0x90000;
    const FmPair p[3] = {{0x1000, 0x2000}, {0x1040, 0x2040}, {0x1080, 0x2080}};
    assert(FmStore(&m, k, p, 0) == FM_EMPTY && FmStore(&m, k, p, 3) == FM_STORED && m.nRecs == 1 && m.used == 3);
    assert(FmFind(&m, k, 0x1040) == 0 && FmFind(&m, k, 0x1100) < 0 && FmFind(&m, k2, 0x1040) < 0);
    assert(FmPairs(&m, 0)[2].node == 0x2080 && m.rec[0].n == 3);
    assert(FmStore(&m, k, p, 3) == FM_DUP && m.nRecs == 1);  // a start inside the record floods the same set
    assert(FmStore(&m, k2, p + 1, 2) == FM_STORED && FmFind(&m, k2, 0x1040) == 1 && FmFind(&m, k2, 0x1000) < 0);
    assert(FmMaskOf(&m, 0x1040) == 3 && FmMaskOf(&m, 0x1000) == 1);
    FmKey k3 = k;
    k3.optHash = 8;
    assert(FmFind(&m, k3, 0x1000) < 0);
    k3 = k, k3.q = 0x11;
    assert(FmFind(&m, k3, 0x1000) < 0);
    k3 = k, k3.lot = 0x21;
    assert(FmFind(&m, k3, 0x1000) < 0);
    k3 = k, k3.opts = 0x31;
    assert(FmFind(&m, k3, 0x1000) < 0);
    k3 = k, k3.cls = 1;
    assert(FmFind(&m, k3, 0x1000) < 0);
    // a sequence move drops every record (O(1)), also across a generation wrap
    assert(FmSync(&m, 3, 2, 2) && FmFind(&m, k, 0x1040) < 0 && m.nRecs == 0 && m.used == 0);
    m.gen = 0xFFFFFFFF;
    m.slot[5].gen = 1;
    FmReset(&m);
    assert(m.gen == 1 && m.slot[5].gen == 0);
    // caps: a long flood, the record table, the pool
    static FmPair big[FM_MAXFLOOD + 1];
    for (uint32_t i = 0; i <= FM_MAXFLOOD; i++) big[i].cell = 0x100000 + i * 0x40, big[i].node = 0x900000 + i * 0x40;
    assert(FmStore(&m, k, big, FM_MAXFLOOD + 1) == FM_LONG && FmStore(&m, k, big, FM_MAXFLOOD) == FM_STORED && m.used == FM_MAXFLOOD);
    assert(FmFind(&m, k, big[FM_MAXFLOOD - 1].cell) == 0);
    for (uint32_t i = 0; i < FM_MAXFLOOD; i++) big[i].cell += 0x4000000;
    assert(FmStore(&m, k2, big, FM_MAXFLOOD) == FM_STORED && m.used == FM_POOL);
    const FmPair one = {0x77000, 0x78000};
    assert(FmStore(&m, k, &one, 1) == FM_POOLFULL && m.nRecs == 2);
    memset(&m, 0, sizeof(m));
    FmSync(&m, 1, 1, 1);
    for (uint32_t r = 0; r < FM_RECS; r++) {
        const FmPair q = {0x1000 + r * 0x40, 0x2000 + r * 0x40};
        k3 = k, k3.flags = r;
        assert(FmStore(&m, k3, &q, 1) == FM_STORED);
    }
    k3.flags = 99;
    assert(FmStore(&m, k3, &one, 1) == FM_RECSFULL);
    // serve rule
    assert(FmServeRule(1, true, true, true) == 1 && FmServeRule(2, true, true, true) == 0 && FmServeRule(0, true, true, true) == 0);
    assert(FmServeRule(1, false, true, true) == 0 && FmServeRule(1, true, false, true) == 0 && FmServeRule(1, true, true, false) == 0);
}

static void TestFmHook() {
    FloodMemoCounters& c = ts3native_floodmemo;
    const FloodMemoCounters saved = c;
    const bool savedAll = g_fmAll, savedKeep = g_fmHostKeep, savedOpen = t_pd.open;
    const LONG savedScope = t_pd.scope, savedHost = t_fmHostSeq;
    const DWORD savedTid = g_pdTid;
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
    memset((void*)&c, 0, sizeof(c));
    g_fmAll = g_fmHostKeep = false;
    t_pd.open = true, t_pd.scope = 2, g_pdTid = GetCurrentThreadId();  // a sync plan of an unseen router
    FmFakeWorld();
    // mode 0: nothing looked at, nothing popped into a record
    c.fmMode = 0;
    FmFakeSetup(0, 0, -1);
    assert(FmFakeSearch(0, 0, 2, 0) == 4 && c.fmCalls == 0 && c.fmStores == 0);
    // mode 1: a failed flood over cells 0-2 is kept
    c.fmMode = 1;
    assert(FmFakeSearch(0, 0, 2, 0) == 4 && c.fmCalls == 1 && c.fmStores == 1 && c.fmCells == 3 && c.fmHits == 0);
    // another start inside it (cell 1), the goal outside (cell 5): leaves at the first pop
    FmFakeSetup(1, 1, 5);
    assert(FmFakeSearch(1, 1, 2, 0) == -1 && c.fmHits == 1 && c.fmStores == 1 && c.fmChecks == 0);
    // no goal linked at all (its cell is not walkable): the same
    FmFakeSetup(1, 2, -1);
    assert(FmFakeSearch(1, 2, 2, 0) == -1 && c.fmHits == 2);
    // the goal on a cell of the record: the search runs (and finds it), nothing predicted
    FmFakeSetup(1, 1, 2);
    assert(FmFakeSearch(1, 1, 2, 1) == 3 && c.fmHits == 2 && c.fmLinkMiss == 1 && c.fmChecks == 0 && c.fmMismatch == 0);
    // a start outside the record: runs; its own failed flood (cells 4-5) becomes a second record
    FmFakeSetup(2, 4, -1);
    assert(FmFakeSearch(2, 4, 5, 0) == 3 && c.fmStores == 2 && t_fmc->m.nRecs == 2);
    FmFakeSetup(1, 5, 0);
    assert(FmFakeSearch(1, 5, 5, 0) == -1 && c.fmHits == 3);
    // a found path is never kept; a start inside a record with a moved cell -> node link is not served
    FmFakeSetup(2, 6, 7);
    assert(FmFakeSearch(2, 6, 7, 1) == 3 && c.fmStores == 2);
    FmFakeSetup(1, 1, -1);
    *(DWORD*)(g_fmCells[2].b + 0x38) = 0;
    assert(FmFakeSearch(1, 1, 1, 0) == 2 && c.fmLinkMiss == 2 && c.fmHits == 3);
    *(DWORD*)(g_fmCells[2].b + 0x38) = (DWORD)(uintptr_t)&g_fmNodes[2];
    // another context (retry flag, options bytes, X+0x4C): no record of that key
    *(DWORD*)(g_fmX + 0x50) = 0x90000;
    assert(FmFakeSearch(1, 1, 2, 0) == 3 && c.fmHits == 3 && c.fmStores == 3);
    *(DWORD*)(g_fmX + 0x50) = 0x10000;
    g_fmOpts[0x28] = 1;
    LONG stores = c.fmStores;
    assert(FmFakeSearch(1, 1, 2, 0) == 3 && c.fmHits == 3 && c.fmStores == stores + 1);
    g_fmOpts[0x28] = 0;
    assert(FmFakeSearch(1, 1, 2, 0) == -1 && c.fmHits == 4);
    // a routing write, a table clear, a managed call: every record gone
    LONG resets = c.fmResets;
    InterlockedIncrement(&g_snWriteSeq);
    assert(FmFakeSearch(1, 1, 2, 0) == 3 && c.fmResets == resets + 1 && c.fmHits == 4 && t_fmc->m.nRecs == 1);
    InterlockedIncrement(&g_nmClearSeq);
    assert(FmFakeSearch(1, 1, 2, 0) == 3 && c.fmResets == resets + 2 && c.fmHits == 4);
    t_fmHostSeq++;
    assert(FmFakeSearch(1, 1, 2, 0) == 3 && c.fmResets == resets + 3 && c.fmHostResets == 1 && c.fmHits == 4);
    assert(FmFakeSearch(1, 1, 2, 0) == -1 && c.fmHits == 5);
    g_fmHostKeep = true;  // FloodMemoHostKeep=1: the host sequence is not looked at (the flip itself clears once)
    assert(FmFakeSearch(1, 1, 2, 0) == 3 && FmFakeSearch(1, 1, 2, 0) == -1);
    t_fmHostSeq++;
    assert(FmFakeSearch(1, 1, 2, 0) == -1 && c.fmHits == 7);
    g_fmHostKeep = false;
    // never kept: the node cap or a cut left the open list non-empty; a deadline cut; a write during the flood
    InterlockedIncrement(&g_snWriteSeq);
    stores = c.fmStores;
    LONG noStore = c.fmNoStore;
    *(LONG*)(g_fmOpen + 0x18) = 3;
    assert(FmFakeSearch(1, 1, 2, 0) == 3 && c.fmStores == stores && c.fmNoStore == noStore + 1);
    *(LONG*)(g_fmOpen + 0x18) = -1;
    FmCellEnter(g_fmQ, (DWORD)(uintptr_t)g_fmX, (DWORD)(uintptr_t)g_fmSet);
    FmPop((DWORD)(uintptr_t)&g_fmStarts[1]), FmPop((DWORD)(uintptr_t)&g_fmNodes[1]);
    FmAbort();
    FmCellLeave(g_fmQ, 0);
    assert(c.fmStores == stores && c.fmNoStore == noStore + 1);
    FmCellEnter(g_fmQ, (DWORD)(uintptr_t)g_fmX, (DWORD)(uintptr_t)g_fmSet);
    FmPop((DWORD)(uintptr_t)&g_fmStarts[1]), FmPop((DWORD)(uintptr_t)&g_fmNodes[1]);
    InterlockedIncrement(&g_snWriteSeq);
    FmCellLeave(g_fmQ, 0);
    assert(c.fmStores == stores && c.fmNoStore == noStore + 2);
    // passed by: two start nodes, a start node with two links
    LONG shape = c.fmShape;
    *(DWORD*)(g_fmSet + 0x14) = 2;
    assert(FmFakeSearch(1, 1, 2, 0) == 3 && c.fmShape == shape + 1 && c.fmStores == stores);
    *(DWORD*)(g_fmSet + 0x14) = 1;
    FmFakeLink(&g_fmStarts[1], &g_fmNodes[3]);
    assert(FmFakeSearch(1, 1, 2, 0) == 3 && c.fmShape == shape + 2 && c.fmStores == stores);
    // household / unknown router: the record is made and checked, the search always runs
    FmFakeSetup(0, 0, -1);
    assert(FmFakeSearch(0, 0, 2, 0) == 4 && c.fmStores == stores + 1);
    t_pd.scope = 1;
    LONG hits = c.fmHits;
    FmFakeSetup(1, 1, -1);
    assert(FmFakeSearch(1, 1, 2, 0) == 3 && c.fmHits == hits && c.fmScopeMiss == 1 && c.fmChecks == 1 && c.fmMismatch == 0);
    // FloodMemoAll: still not served while no command marks exist (the plan may be a player command)
    g_fmAll = true;
    assert(FmFakeSearch(1, 1, 2, 0) == 3 && c.fmHits == hits && c.fmCmdMiss == 1 && c.fmChecks == 2);
    g_fmAll = false;
    // a prediction the game's search contradicts counts as a mismatch
    assert(FmFakeSearch(1, 1, 2, 1) == 3 && c.fmChecks == 3 && c.fmMismatch == 1);
    // shadow: an NPC router's search runs too and is compared
    t_pd.scope = 2;
    c.fmMode = 2;
    assert(FmFakeSearch(1, 1, 2, 0) == 3 && c.fmHits == hits && c.fmChecks == 4 && c.fmMismatch == 1);
    c.fmMode = 1;
    assert(FmFakeSearch(1, 1, 2, 0) == -1 && c.fmHits == hits + 1);
    // a new start node object for every search (the wrappers'): the record does not depend on it
    FmFakeSetup(2, 0, 6);
    assert(FmFakeSearch(2, 0, 2, 0) == -1 && c.fmHits == hits + 2);
    // docs/673, exit classes: a found search counts its pops; a second one from the same start cell is a repeat, with the
    // same goal index "same"; another start cell is not; a write empties the list; a failure and a cut count on their own
    InterlockedIncrement(&g_snWriteSeq);
    const LONG found = c.fmFound, fail = c.fmFail, cap = c.fmFailCap, cuts = c.fmAborts, repN = c.fmRepStart, same = c.fmRepSame;
    const LONG pops = c.fmFoundPops, repPops = c.fmRepPops;
    FmFakeSetup(0, 4, 6);
    *(LONG*)(g_fmQ + 0x1C) = 7;
    assert(FmFakeSearch(0, 4, 6, 1) == 4 && c.fmFound == found + 1 && c.fmFoundPops == pops + 4 && c.fmRepStart == repN);
    assert(FmFakeSearch(0, 4, 5, 1) == 3 && c.fmFound == found + 2 && c.fmRepStart == repN + 1 && c.fmRepSame == same + 1 &&
           c.fmRepPops == repPops + 3);
    *(LONG*)(g_fmQ + 0x1C) = 9;
    assert(FmFakeSearch(0, 4, 5, 1) == 3 && c.fmRepStart == repN + 2 && c.fmRepSame == same + 1);
    assert(FmFakeSearch(0, 4, 5, 1) == 3 && c.fmRepStart == repN + 3 && c.fmRepSame == same + 2);
    FmFakeSetup(1, 5, 6);
    assert(FmFakeSearch(1, 5, 6, 1) == 3 && c.fmFound == found + 5 && c.fmRepStart == repN + 3);
    InterlockedIncrement(&g_snWriteSeq);
    assert(FmFakeSearch(1, 5, 6, 1) == 3 && c.fmRepStart == repN + 3);  // the list went with the write
    assert(FmFakeSearch(1, 5, 6, 1) == 3 && c.fmRepStart == repN + 4);
    *(LONG*)(g_fmOpen + 0x18) = 3;
    assert(FmFakeSearch(1, 5, 6, 0) == 3 && c.fmFail == fail + 1 && c.fmFailCap == cap + 1 && c.fmFound == found + 7);
    *(LONG*)(g_fmOpen + 0x18) = -1;
    FmCellEnter(g_fmQ, (DWORD)(uintptr_t)g_fmX, (DWORD)(uintptr_t)g_fmSet);
    FmPop((DWORD)(uintptr_t)&g_fmStarts[1]), FmPop((DWORD)(uintptr_t)&g_fmNodes[5]);
    FmAbort();
    FmCellLeave(g_fmQ, 1);
    assert(c.fmAborts == cuts + 1 && c.fmFound == found + 7 && c.fmFail == fail + 1);
    t_pd.open = savedOpen, t_pd.scope = savedScope, g_pdTid = savedTid, t_fmHostSeq = savedHost;
    g_fmAll = savedAll, g_fmHostKeep = savedKeep;
    c = saved;
}

static void TestFloodMemo() {
    TestFmCore();
    TestFmHook();
    printf("flood memo: OK (table, serve at the first pop, goal link, context, write / clear / managed call, cap and cut exits, scope, command, shadow, exit classes)\n");
}
