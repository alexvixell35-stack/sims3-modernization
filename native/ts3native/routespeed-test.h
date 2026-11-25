// docs/663 routespeed.cpp: [RouteSpeed] NeighbourMemo. Included by selftest.cpp.
// The memo table (routespeed-core.h: store, find, keys, sequences, caps) and NmNbHook on fake cells with a fake game
// function and a fake release: serve = the same cells in the same order with the same references as the game's call, a
// table clear or a tree write drops the memo, no room in the out vector or a household router runs the game's function,
// shadow compares and counts a mismatch, mode 0 passes through.

// fake cell: refcount at +0x28, key words at +0x30 / +0x34 (as the game's cell object)
struct NmFakeCell { u8 b[0x3C]; };
static NmFakeCell g_nmCells[8];
static int g_nmFakeRuns, g_nmFakeVariant;
static LONG NmRef(int i) { return *(LONG*)(g_nmCells[i].b + 0x28); }

static void __stdcall NmFakeRelease(DWORD begin, DWORD end) {
    for (DWORD* p = (DWORD*)(uintptr_t)begin; p < (DWORD*)(uintptr_t)end; p++)
        if (*p) (*(LONG*)((u8*)(uintptr_t)*p + 0x28))--;
}
// the "game": neighbours of cell 0 are cells 1, 2, 3 (variant 1: 1, 3, 2 = a changed tree); one reference per pushed cell
static void __fastcall NmFakeNb(void* cell, void*, void*, DWORD* out, void*, void*) {
    g_nmFakeRuns++;
    NmFakeRelease(out[0], out[1]);
    out[1] = out[0];
    const int order[2][3] = {{1, 2, 3}, {1, 3, 2}};
    if (cell != &g_nmCells[0]) return;
    DWORD* w = (DWORD*)(uintptr_t)out[0];
    for (int j = 0; j < 3; j++) {
        NmFakeCell* c = &g_nmCells[order[g_nmFakeVariant][j]];
        w[j] = (DWORD)(uintptr_t)c;
        (*(LONG*)(c->b + 0x28))++;
    }
    out[1] = out[0] + 12;
}

static void TestNmCore() {
    static NmMemo m;  // 246 KB: not on the stack
    memset(&m, 0, sizeof(m));
    assert(NmSync(&m, 1, 1) && !NmSync(&m, 1, 1) && NmSync(&m, 2, 1) && NmSync(&m, 2, 2));
    const uintptr_t l[3] = {0x100, 0x200, 0x300};
    assert(NmStore(&m, 0x10, 0x1000, 7, 8, l, 3) == NM_STORED && m.entries == 1 && m.used == 3);
    const NmSlot* s = NmFind(&m, 0x10, 0x1000, 7, 8);
    assert(s && s->n == 3 && NmSame(&m, s, l, 3) && NmList(&m, s)[2] == 0x300);
    assert(!NmFind(&m, 0x11, 0x1000, 7, 8) && !NmFind(&m, 0x10, 0x1000, 7, 9) && !NmFind(&m, 0x10, 0x1004, 7, 8));
    const uintptr_t l2[3] = {0x100, 0x300, 0x200};
    assert(!NmSame(&m, s, l2, 3) && !NmSame(&m, s, l, 2));
    assert(NmStore(&m, 0x10, 0x1000, 7, 8, l, 3) == NM_STORED && m.entries == 1);  // a second store of the same key keeps one
    const uintptr_t withNull[2] = {0x100, 0};
    assert(NmStore(&m, 0x10, 0x2000, 1, 1, withNull, 2) == NM_NULLCELL);
    static uintptr_t big[NM_MAXN + 1];
    for (int i = 0; i <= NM_MAXN; i++) big[i] = 0x10 + i;
    assert(NmStore(&m, 0x10, 0x3000, 1, 1, big, NM_MAXN + 1) == NM_LONG && NmStore(&m, 0x10, 0x3000, 1, 1, big, NM_MAXN) == NM_STORED);
    // a sequence move drops every entry (O(1)), also across a generation wrap
    assert(NmSync(&m, 3, 2) && !NmFind(&m, 0x10, 0x1000, 7, 8) && m.used == 0);
    m.gen = 0xFFFFFFFF;
    m.slot[5].gen = 1;
    NmReset(&m);
    assert(m.gen == 1 && m.slot[5].gen == 0);
    // pool cap: no store past NM_POOL words; probe cap: a full run stores nothing
    memset(&m, 0, sizeof(m));
    NmSync(&m, 1, 1);
    int stored = 0, full = 0;
    for (uint32_t c = 1; c <= NM_POOL / NM_MAXN + 4; c++) {
        const int r = NmStore(&m, 0x10, c * 0x40, c, c, big, NM_MAXN);
        if (r == NM_STORED) stored++; else if (r == NM_POOLFULL) full++;
    }
    assert(stored == NM_POOL / NM_MAXN && full == 4 && m.used == NM_POOL);
    // serve rule
    assert(NmServeRule(1, true, true, 4, 3) == 1 && NmServeRule(2, true, true, 4, 3) == 0 && NmServeRule(1, false, true, 4, 3) == 0);
    assert(NmServeRule(1, true, false, 4, 3) == 0 && NmServeRule(1, true, true, 2, 3) == 0 && NmServeRule(0, true, true, 4, 3) == 0);
}

static void TestNmHook() {
    RouteSpeedCounters& c = ts3native_routespeed;
    const RouteSpeedCounters saved = c;
    const NmNbFn savedNext = g_nmNbNext;
    const NmRelFn savedRel = g_nmRel;
    const bool savedAll = g_nmAll;
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
    memset((void*)&c, 0, sizeof(c));
    memset(g_nmCells, 0, sizeof(g_nmCells));
    for (int i = 0; i < 8; i++) *(DWORD*)(g_nmCells[i].b + 0x30) = 0x100 + i, *(DWORD*)(g_nmCells[i].b + 0x34) = 0x200 + i,
                                *(LONG*)(g_nmCells[i].b + 0x28) = 1;  // the table's reference
    g_nmNbNext = NmFakeNb, g_nmRel = NmFakeRelease, g_nmFakeRuns = 0, g_nmFakeVariant = 0;
    u8 q[16];
    DWORD buf[8] = {0}, small[4] = {0};  // tiny reports room for 2; the fake game writes 3
    DWORD out[3] = {(DWORD)(uintptr_t)buf, (DWORD)(uintptr_t)buf, (DWORD)(uintptr_t)(buf + 8)};
    DWORD tiny[3] = {(DWORD)(uintptr_t)small, (DWORD)(uintptr_t)small, (DWORD)(uintptr_t)(small + 2)};
    void* const c0 = &g_nmCells[0];
    // mode 0: the game's function, nothing counted
    c.nmMode = 0;
    NmNbHook(c0, NULL, q, out, NULL, NULL);
    assert(g_nmFakeRuns == 1 && c.nmCalls == 0 && out[1] - out[0] == 12);
    // mode 1, every router: the first call fills, the second is served with the same cells, order and references
    c.nmMode = 1, g_nmAll = true;
    NmNbHook(c0, NULL, q, out, NULL, NULL);
    assert(g_nmFakeRuns == 2 && c.nmStores == 1 && NmRef(1) == 2 && NmRef(2) == 2 && NmRef(3) == 2);
    NmNbHook(c0, NULL, q, out, NULL, NULL);
    assert(g_nmFakeRuns == 2 && c.nmHits == 1 && c.nmServed == 3 && out[1] - out[0] == 12);
    assert(buf[0] == (DWORD)(uintptr_t)&g_nmCells[1] && buf[1] == (DWORD)(uintptr_t)&g_nmCells[2] && buf[2] == (DWORD)(uintptr_t)&g_nmCells[3]);
    assert(NmRef(1) == 2 && NmRef(2) == 2 && NmRef(3) == 2);  // released the old list, took one reference each, as the game
    // another Q misses (its own entry); a cell whose key words changed (freed and reused) misses
    u8 q2[16];
    NmNbHook(c0, NULL, q2, out, NULL, NULL);
    assert(g_nmFakeRuns == 3 && c.nmStores == 2);
    *(DWORD*)(g_nmCells[0].b + 0x30) ^= 1;
    NmNbHook(c0, NULL, q, out, NULL, NULL);
    assert(g_nmFakeRuns == 4);
    *(DWORD*)(g_nmCells[0].b + 0x30) ^= 1;
    // no room in the out vector: the game's function runs
    NmNbHook(c0, NULL, q, tiny, NULL, NULL);
    assert(c.nmCapMiss == 1 && g_nmFakeRuns == 5);
    NmFakeRelease(tiny[0], tiny[1]), tiny[1] = tiny[0];
    // a table clear and a tree write each drop the memo
    const LONG r0 = c.nmResets;
    InterlockedIncrement(&g_nmClearSeq);
    NmNbHook(c0, NULL, q, out, NULL, NULL);
    assert(c.nmResets == r0 + 1 && g_nmFakeRuns == 6);
    NmNbHook(c0, NULL, q, out, NULL, NULL);
    assert(g_nmFakeRuns == 6);  // refilled, served
    InterlockedIncrement(&g_snTreeSeq);
    NmNbHook(c0, NULL, q, out, NULL, NULL);
    assert(c.nmResets == r0 + 2 && g_nmFakeRuns == 7);
    // household / unknown router (no session on this thread: scope 0) with NeighbourMemoAll=0: the game's function
    g_nmAll = false;
    const LONG s0 = c.nmScopeMiss;
    NmNbHook(c0, NULL, q, out, NULL, NULL);
    assert(c.nmScopeMiss == s0 + 1 && g_nmFakeRuns == 8);
    // shadow: always the game's function; the same answer matches, a changed tree without a write counts a mismatch
    c.nmMode = 2, g_nmAll = true;
    NmNbHook(c0, NULL, q, out, NULL, NULL);
    assert(g_nmFakeRuns == 9 && c.nmChecks == 1 && c.nmMismatch == 0);
    g_nmFakeVariant = 1;
    NmNbHook(c0, NULL, q, out, NULL, NULL);
    assert(g_nmFakeRuns == 10 && c.nmChecks == 2 && c.nmMismatch == 1);
    NmFakeRelease(out[0], out[1]);
    for (int i = 1; i < 4; i++) assert(NmRef(i) == 1);  // every reference the out vector took is back
    g_nmNbNext = savedNext, g_nmRel = savedRel, g_nmAll = savedAll;
    c = saved;
}

static void TestRouteSpeed() {
    TestNmCore();
    TestNmHook();
    printf("route speed: OK (neighbour memo table, serve / clear / tree write / scope / room / shadow)\n");
}
