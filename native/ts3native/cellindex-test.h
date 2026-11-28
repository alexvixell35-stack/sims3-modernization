// docs/669 cellindex.cpp: [RouteSpeed] CellIndex. Included by selftest.cpp.
// The index (cellindex-core.h: store, find, sets, eviction, the clear sequence) and CiFindHook on a fake table with a fake
// game function written from the layout (10007 buckets, chain link at +0x2C to the next cell's +0x2C, key at +0x30 / +0x34,
// end slot 0xFFFFFFFF, element count after it): a served answer is the same cell, bucket address and return value as the
// game's call, "not found" always runs the game's function, a table clear drops the index, a cell whose key words changed
// is not served, a household router runs the game's function, shadow compares and counts a mismatch, mode 0 passes through.

struct CiFakeCell { u8 b[0x3C]; };
static CiFakeCell g_ciCells[8];
static DWORD g_ciTblA[CI_BUCKETS + 2], g_ciTblB[CI_BUCKETS + 2];
static int g_ciFakeRuns;

static void CiFakeInit(DWORD* t) {
    memset(t, 0, sizeof(g_ciTblA));
    t[CI_BUCKETS] = 0xFFFFFFFF;
}
// insert at the chain's head, as a table that only grows
static void CiFakeInsert(DWORD* t, CiFakeCell* c, DWORD k0, DWORD k1) {
    *(DWORD*)(c->b + 0x30) = k0, *(DWORD*)(c->b + 0x34) = k1;
    DWORD* const head = t + CiBucket(k0, k1);
    *(DWORD*)(c->b + 0x2C) = *head ? *head + 0x2C : 0;
    *head = (DWORD)(uintptr_t)c;
    t[CI_BUCKETS + 1]++;
}
// the "game": 0x664370 from its description
static DWORD* __fastcall CiFakeFind(void* table, void*, DWORD* out, const DWORD* key) {
    g_ciFakeRuns++;
    DWORD* const t = (DWORD*)table;
    DWORD* const head = t + CiBucket(key[0], key[1]);
    for (DWORD p = *head; p;) {
        const u8* c = (const u8*)(uintptr_t)p;
        if (*(const DWORD*)(c + 0x30) == key[0] && *(const DWORD*)(c + 0x34) == key[1]) {
            out[0] = p, out[1] = (DWORD)(uintptr_t)head;
            return out;
        }
        const DWORD next = *(const DWORD*)(c + 0x2C);
        if (!next) break;
        p = next - 0x2C;
    }
    out[0] = t[CI_BUCKETS], out[1] = (DWORD)(uintptr_t)(t + CI_BUCKETS);
    return out;
}

static void TestCiCore() {
    static CiIndex x;  // 393 KB: not on the stack
    memset(&x, 0, sizeof(x));
    assert(CiBucket(0x12345678, 0) == 0x12345678u % 10007 && CiBucket(5, 5) == 0 && CiBucket(5 ^ 3, 0) == CiBucket(5, 3));
    assert(CiSync(&x, 1) && !CiSync(&x, 1) && CiSync(&x, 2));
    assert(!CiFind(&x, 0x1000, 7, 8));
    assert(CiStore(&x, 0x1000, 7, 8, 0xC0, 0xB0) == CI_NEW);
    CiSlot* s = CiFind(&x, 0x1000, 7, 8);
    assert(s && s->cell == 0xC0 && s->bucket == 0xB0);
    assert(!CiFind(&x, 0x1004, 7, 8) && !CiFind(&x, 0x1000, 7, 9) && !CiFind(&x, 0x1000, 6, 8));  // table and both key words
    assert(CiStore(&x, 0x1000, 7, 8, 0xC4, 0xB4) == CI_SAME && CiFind(&x, 0x1000, 7, 8)->cell == 0xC4);
    CiDrop(CiFind(&x, 0x1000, 7, 8));
    assert(!CiFind(&x, 0x1000, 7, 8));
    // a set holds CI_WAYS keys; one more evicts exactly one of them and every kept key still answers with its own cell
    memset(&x, 0, sizeof(x));
    CiSync(&x, 1);
    const uint32_t home = CiHash(0x1000, 1, 0);
    uint32_t same[CI_WAYS + 1] = {0}, n = 0;
    for (uint32_t k = 1; n < CI_WAYS + 1; k++)
        if (CiHash(0x1000, k, 0) == home) same[n++] = k;
    for (uint32_t i = 0; i < CI_WAYS; i++) assert(CiStore(&x, 0x1000, same[i], 0, 0x100 + i, 0x200 + i) == CI_NEW);
    assert(CiStore(&x, 0x1000, same[CI_WAYS], 0, 0x100 + CI_WAYS, 0x200 + CI_WAYS) == CI_EVICT);
    uint32_t kept = 0;
    for (uint32_t i = 0; i <= CI_WAYS; i++) {
        const CiSlot* f = CiFind(&x, 0x1000, same[i], 0);
        if (f) kept++, assert(f->cell == 0x100 + i && f->bucket == 0x200 + i);
    }
    assert(kept == CI_WAYS && CiFind(&x, 0x1000, same[CI_WAYS], 0));
    // many keys of the game's worst shape (x index xor shape collide in its table): each one found with its own cell or not at all
    memset(&x, 0, sizeof(x));
    CiSync(&x, 1);
    for (uint32_t ix = 0; ix < 64; ix++)
        for (uint32_t shape = 0; shape < 64; shape++) CiStore(&x, 0x1000, ix | 7u << 16, shape | 4u << 8, ix * 64 + shape + 1, 0);
    uint32_t got = 0;
    for (uint32_t ix = 0; ix < 64; ix++)
        for (uint32_t shape = 0; shape < 64; shape++) {
            const CiSlot* f = CiFind(&x, 0x1000, ix | 7u << 16, shape | 4u << 8);
            if (f) got++, assert(f->cell == ix * 64 + shape + 1);
        }
    assert(got > 4096 * 9 / 10);  // 4096 keys in 16384 slots: almost none evicted (the game's table puts them in 64 buckets)
    // the clear sequence drops every entry (O(1)), also across a generation wrap
    assert(CiSync(&x, 2) && !CiFind(&x, 0x1000, 7u << 16, 4u << 8));
    x.gen = 0xFFFFFFFF;
    x.slot[5].gen = 1;
    CiReset(&x);
    assert(x.gen == 1 && x.slot[5].gen == 0);
    // serve rule
    assert(CiServeRule(1, true, true, true) == 1 && CiServeRule(2, true, true, true) == 0 && CiServeRule(0, true, true, true) == 0);
    assert(CiServeRule(1, false, true, true) == 0 && CiServeRule(1, true, false, true) == 0 && CiServeRule(1, true, true, false) == 0);
}

static void TestCiHook() {
    CellIndexCounters& c = ts3native_cellindex;
    const CellIndexCounters saved = c;
    const CiFindFn savedNext = g_ciFindNext;
    const bool savedAll = g_ciAll;
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
    memset((void*)&c, 0, sizeof(c));
    memset(g_ciCells, 0, sizeof(g_ciCells));
    CiFakeInit(g_ciTblA), CiFakeInit(g_ciTblB);
    g_ciFindNext = CiFakeFind, g_ciFakeRuns = 0;
    // cells 0-2 share one bucket of table A (the game's xor: 5^0 = 4^1 = 7^2); cell 3 has cell 0's key in table B
    const DWORD K[3][2] = {{5, 0}, {4, 1}, {7, 2}};
    assert(CiBucket(K[0][0], K[0][1]) == CiBucket(K[1][0], K[1][1]) && CiBucket(K[0][0], K[0][1]) == CiBucket(K[2][0], K[2][1]));
    for (int i = 0; i < 3; i++) CiFakeInsert(g_ciTblA, &g_ciCells[i], K[i][0], K[i][1]);
    CiFakeInsert(g_ciTblB, &g_ciCells[3], K[0][0], K[0][1]);
    const DWORD bucketA = (DWORD)(uintptr_t)(g_ciTblA + CiBucket(5, 0)), bucketB = (DWORD)(uintptr_t)(g_ciTblB + CiBucket(5, 0));
    const DWORD endA = (DWORD)(uintptr_t)(g_ciTblA + CI_BUCKETS);
    assert(CiChainSteps(g_ciTblA, 5, 0, (uintptr_t)&g_ciCells[0]) == 3 && CiChainSteps(g_ciTblA, 7, 2, (uintptr_t)&g_ciCells[2]) == 1);
    assert(CiChainSteps(g_ciTblA, 6, 3, 0) == 3 && CiChainSteps(g_ciTblA, 100, 0, 0) == 0);  // a miss walks the whole chain
    DWORD out[2] = {0, 0}, ref[2];
    // mode 0: the game's function, nothing counted
    c.ciMode = 0;
    assert(CiFindHook(g_ciTblA, NULL, out, K[0]) == out && g_ciFakeRuns == 1 && c.ciCalls == 0);
    assert(out[0] == (DWORD)(uintptr_t)&g_ciCells[0] && out[1] == bucketA);
    // mode 1, every router: the first lookup of a key runs the game's function, the repeat is served: same cell, same
    // bucket address, out returned
    c.ciMode = 1, g_ciAll = true;
    for (int i = 0; i < 3; i++) {
        CiFakeFind(g_ciTblA, NULL, ref, K[i]);
        g_ciFakeRuns--;
        out[0] = out[1] = 0;
        assert(CiFindHook(g_ciTblA, NULL, out, K[i]) == out && out[0] == ref[0] && out[1] == ref[1]);
        const int runs = g_ciFakeRuns;
        out[0] = out[1] = 0;
        assert(CiFindHook(g_ciTblA, NULL, out, K[i]) == out && out[0] == ref[0] && out[1] == ref[1] && g_ciFakeRuns == runs);
        assert(out[0] == (DWORD)(uintptr_t)&g_ciCells[i] && out[1] == bucketA);
    }
    assert(c.ciStores == 3 && c.ciHits == 3 && g_ciFakeRuns == 4);
    // the same key in another table is that table's own cell
    CiFindHook(g_ciTblB, NULL, out, K[0]);
    assert(out[0] == (DWORD)(uintptr_t)&g_ciCells[3] && out[1] == bucketB && g_ciFakeRuns == 5);
    CiFindHook(g_ciTblB, NULL, out, K[0]);
    assert(out[0] == (DWORD)(uintptr_t)&g_ciCells[3] && out[1] == bucketB && g_ciFakeRuns == 5);
    CiFindHook(g_ciTblA, NULL, out, K[0]);
    assert(out[0] == (DWORD)(uintptr_t)&g_ciCells[0] && out[1] == bucketA && g_ciFakeRuns == 5);
    // not found: the end slot, never kept, so the game's function runs every time; after the insert the cell is found
    const DWORD kNew[2] = {6, 3};  // the same bucket again
    for (int i = 0; i < 2; i++) {
        CiFindHook(g_ciTblA, NULL, out, kNew);
        assert(out[0] == 0xFFFFFFFF && out[1] == endA && g_ciFakeRuns == 6 + i && c.ciNotFound == i + 1);
    }
    CiFakeInsert(g_ciTblA, &g_ciCells[4], kNew[0], kNew[1]);
    CiFindHook(g_ciTblA, NULL, out, kNew);
    assert(out[0] == (DWORD)(uintptr_t)&g_ciCells[4] && out[1] == bucketA && g_ciFakeRuns == 8);
    CiFindHook(g_ciTblA, NULL, out, kNew);
    assert(out[0] == (DWORD)(uintptr_t)&g_ciCells[4] && g_ciFakeRuns == 8);
    // the insert did not disturb the kept answers of the older cells
    CiFindHook(g_ciTblA, NULL, out, K[1]);
    assert(out[0] == (DWORD)(uintptr_t)&g_ciCells[1] && g_ciFakeRuns == 8);
    // a kept cell whose key words changed (must not happen: counted) is not served; the game's answer is taken and kept
    *(DWORD*)(g_ciCells[1].b + 0x30) = 0x77;
    CiFindHook(g_ciTblA, NULL, out, K[1]);
    assert(c.ciStale == 1 && g_ciFakeRuns == 9 && out[0] == 0xFFFFFFFF && out[1] == endA);
    *(DWORD*)(g_ciCells[1].b + 0x30) = K[1][0];
    CiFindHook(g_ciTblA, NULL, out, K[1]);
    assert(g_ciFakeRuns == 10 && out[0] == (DWORD)(uintptr_t)&g_ciCells[1]);
    // a table clear drops the index: the emptied table answers "not found" through the game's function
    const LONG r0 = c.ciResets;
    CiFakeInit(g_ciTblA);
    InterlockedIncrement(&g_nmClearSeq);
    CiFindHook(g_ciTblA, NULL, out, K[0]);
    assert(c.ciResets == r0 + 1 && g_ciFakeRuns == 11 && out[0] == 0xFFFFFFFF && out[1] == endA);
    for (int i = 0; i < 3; i++) CiFakeInsert(g_ciTblA, &g_ciCells[i], K[i][0], K[i][1]);
    CiFindHook(g_ciTblA, NULL, out, K[0]);
    CiFindHook(g_ciTblA, NULL, out, K[0]);
    assert(g_ciFakeRuns == 12 && out[0] == (DWORD)(uintptr_t)&g_ciCells[0]);
    // household / unknown router (no session on this thread: scope 0) with CellIndexAll=0: the game's function
    g_ciAll = false;
    const LONG s0 = c.ciScopeMiss;
    CiFindHook(g_ciTblA, NULL, out, K[0]);
    assert(c.ciScopeMiss == s0 + 1 && g_ciFakeRuns == 13 && out[0] == (DWORD)(uintptr_t)&g_ciCells[0] && out[1] == bucketA);
    // shadow: always the game's function; the same answer matches, a table changed without a clear counts a mismatch
    c.ciMode = 2, g_ciAll = true;
    CiFindHook(g_ciTblA, NULL, out, K[0]);
    assert(g_ciFakeRuns == 14 && c.ciChecks == 1 && c.ciMismatch == 0);
    CiFakeInit(g_ciTblA);
    CiFakeInsert(g_ciTblA, &g_ciCells[5], K[0][0], K[0][1]);  // another cell object under the kept key
    CiFindHook(g_ciTblA, NULL, out, K[0]);
    assert(g_ciFakeRuns == 15 && c.ciChecks == 2 && c.ciMismatch == 1 && out[0] == (DWORD)(uintptr_t)&g_ciCells[5]);
    CiFindHook(g_ciTblA, NULL, out, K[0]);
    assert(c.ciChecks == 3 && c.ciMismatch == 1);  // the game's answer replaced the kept one
    // the sampled walk (every 64th call of the game's function): chain length and the table's element count
    c.ciWalks = c.ciSteps = c.ciStepMax = c.ciCountMax = 0;
    for (int i = 0; i < 64; i++) CiFindHook(g_ciTblA, NULL, out, K[0]);
    assert(c.ciWalks == 1 && c.ciSteps == 1 && c.ciStepMax == 1 && c.ciCountMax == 1 && c.ciMissN >= 1);
    g_ciFindNext = savedNext, g_ciAll = savedAll;
    c = saved;
}

static void TestCellIndex() {
    TestCiCore();
    TestCiHook();
    printf("cell index: OK (index sets / eviction / clear, serve = the game's cell, bucket and return, not found, stale key, scope, shadow)\n");
}
