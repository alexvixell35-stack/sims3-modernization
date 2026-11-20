// docs/669 §8-§10 cellspeed.cpp: [RouteSpeed] ClassMemo and OpenHint. Included by selftest.cpp and by the Linux host test.
// No timer decides anything here.
//
// ClassMemo: the sets and agent records (cellspeed-core.h), then KmRecalcHook on fake cells, contexts and agents with a
// fake recompute written from the layout: a served result is the same four writes (class, multiplier, X+0x4C, the key
// clear of a class 1 cell); another shape / flags / X+0x4C / agent word / ignore id is another result; a moved sequence
// drops the memo; a cell with no node, with key 0 or under the clearance test is never looked at; a household router
// runs the game's function; shadow compares and counts a mismatch.
// OpenHint: OhInsertHook against a reference sorted insert written from the description of 0x61B880 (walk from the head,
// link before the first node whose f is not lower), over random searches with ties, pops, moves inside a bucket and
// into another bucket, new generations and table clears: every insert must land before the node the reference names.

// ---- ClassMemo fakes ----
struct KmFakeLeaf { DWORD cls[4]; DWORD mult[4]; DWORD owner[2]; };
static int g_kmFakeRuns;
static DWORD g_kmFakeGlobal;
// the "game": class and multiplier of the leaf's sub-cell; 0 / 1.0 for the footprint's own agent and for an agent whose
// ignore list holds the footprint; flag 0x10 gives a walkable class 0 cell the multiplier 1.0; X+0x4C drops to the class
// when it is 2 or more, not 13 and not below it; a class 1 cell loses its key
static void __fastcall KmFakeRecalc(u8* cell, void*, u8* x) {
    g_kmFakeRuns++;
    DWORD k = 12, mult = 0x7F7FFFFF;
    const KmFakeLeaf* leaf = (const KmFakeLeaf*)(uintptr_t) * (const DWORD*)(cell + 0x0C);
    const DWORD shape = *(const DWORD*)(cell + 0x1C) & 0xFF;
    if (leaf) {
        k = leaf->cls[shape & 3], mult = leaf->mult[shape & 3];
        const u8* opts = x ? (const u8*)(uintptr_t) * (const DWORD*)(x + 0x40) : NULL;
        if (opts) {
            if (*(const DWORD*)(opts + 8) == leaf->owner[0] && *(const DWORD*)(opts + 0xC) == leaf->owner[1]) k = 0, mult = 0x3F800000;
            const u8* d = g_kmFakeGlobal ? (const u8*)(uintptr_t) * (const DWORD*)(opts + 0x28) : NULL;
            if (d)
                for (const DWORD *i = (const DWORD*)(uintptr_t) * (const DWORD*)(d + 0x38), *e = (const DWORD*)(uintptr_t) * (const DWORD*)(d + 0x3C); i < e; i += 2)
                    if (i[0] == leaf->owner[0] && i[1] == leaf->owner[1]) k = 0, mult = 0x3F800000;
        }
    }
    if (x) {
        if ((*(const DWORD*)(x + 0x50) & 0x10) && k == 0) mult = 0x3F800000;
        const DWORD cur = *(const DWORD*)(x + 0x4C);
        if (cur >= 2 && cur != 13 && cur >= k) *(DWORD*)(x + 0x4C) = k;
    }
    *(cell + 0x20) = (u8)k;
    *(DWORD*)(cell + 0x10) = mult;
    if (k == 1) *(DWORD*)(cell + 0x1C) &= 0xFF;
}

static void KmFakeCell(u8* cell, const KmFakeLeaf* leaf, DWORD tree, DWORD shape, const u8* x) {
    memset(cell, 0, 0x40);
    *(DWORD*)(cell + 0x04) = tree;
    *(DWORD*)(cell + 0x0C) = (DWORD)(uintptr_t)leaf;
    *(DWORD*)(cell + 0x1C) = shape | *(const DWORD*)(x + 0x50) << 8;  // as the getters 0x66B600 / 0x66B670 set the key
    *(cell + 0x20) = 0x55;
    *(DWORD*)(cell + 0x10) = 0xDEADBEEF;
}
// runs the hook on a fresh cell and the fake game on a twin; true = the same four writes
static bool KmSameAsGame(const KmFakeLeaf* leaf, DWORD tree, DWORD shape, u8* x) {
    u8 a[0x40], b[0x40], x2[0x60];
    memcpy(x2, x, sizeof(x2));
    KmFakeCell(a, leaf, tree, shape, x);
    KmFakeCell(b, leaf, tree, shape, x2);
    KmRecalcHook(a, NULL, x);
    const int runs = g_kmFakeRuns;
    KmFakeRecalc(b, NULL, x2);
    g_kmFakeRuns = runs;
    return a[0x20] == b[0x20] && *(const DWORD*)(a + 0x10) == *(const DWORD*)(b + 0x10) && *(const DWORD*)(a + 0x1C) == *(const DWORD*)(b + 0x1C) &&
           *(const DWORD*)(x + 0x4C) == *(const DWORD*)(x2 + 0x4C);
}

static void TestCmCore() {
    static KmMemo m;  // 170 KB: not on the stack
    memset(&m, 0, sizeof(m));
    assert(KmSync(&m, 1, 1, 1) && !KmSync(&m, 1, 1, 1));
    assert(!KmFind(&m, 0x100, 0x200, 3, 0x10000, 0, 0));
    assert(KmStore(&m, 0x100, 0x200, 3, 0x10000, 0, 0, 1, 0x47C35000, 0, true) == KM_NEW);
    const KmSlot* s = KmFind(&m, 0x100, 0x200, 3, 0x10000, 0, 0);
    assert(s && s->klass == 1 && s->mult == 0x47C35000 && s->postCls == 0 && s->keyClear == 1);
    // every key word matters
    assert(!KmFind(&m, 0x104, 0x200, 3, 0x10000, 0, 0) && !KmFind(&m, 0x100, 0x204, 3, 0x10000, 0, 0) && !KmFind(&m, 0x100, 0x200, 4, 0x10000, 0, 0));
    assert(!KmFind(&m, 0x100, 0x200, 3, 0x90000, 0, 0) && !KmFind(&m, 0x100, 0x200, 3, 0x10000, 5, 0) && !KmFind(&m, 0x100, 0x200, 3, 0x10000, 0, 1));
    assert(KmStore(&m, 0x100, 0x200, 3, 0x10000, 0, 0, 0, 0x3F800000, 0, false) == KM_SAME && KmFind(&m, 0x100, 0x200, 3, 0x10000, 0, 0)->klass == 0);
    // a set holds KM_WAYS keys; one more evicts exactly one and every kept key still answers with its own result
    const uint32_t home = KmHash(0x100, 1 * 16, 0, 0, 0, 0);
    uint32_t same[KM_WAYS + 1] = {0}, n = 0;
    for (uint32_t k = 2; n < KM_WAYS + 1; k++)
        if (KmHash(0x100, k * 16, 0, 0, 0, 0) == home) same[n++] = k * 16;
    for (uint32_t i = 0; i < KM_WAYS; i++) assert(KmStore(&m, 0x100, same[i], 0, 0, 0, 0, (uint8_t)(i + 2), i, 0, false) == KM_NEW);
    assert(KmStore(&m, 0x100, same[KM_WAYS], 0, 0, 0, 0, (uint8_t)(KM_WAYS + 2), KM_WAYS, 0, false) == KM_EVICT);
    uint32_t kept = 0;
    for (uint32_t i = 0; i <= KM_WAYS; i++) {
        const KmSlot* f = KmFind(&m, 0x100, same[i], 0, 0, 0, 0);
        if (f) kept++, assert(f->klass == i + 2 && f->mult == i);
    }
    assert(kept == KM_WAYS);
    // two keys of one set that differ in a single word are two keys (the hash alone does not tell them apart)
    for (int word = 0; word < 6; word++) {
        uint32_t k[6] = {0x100, 0x200, 3, 0x10000, 0, 0}, other = 0;
        const uint32_t set = KmHash(k[0], k[1], k[2], k[3], k[4], k[5]);
        for (uint32_t v = 1; !other; v++) {
            uint32_t t[6] = {k[0], k[1], k[2], k[3], k[4], k[5]};
            t[word] += v * (word < 2 ? 4 : 1);
            if (KmHash(t[0], t[1], t[2], t[3], t[4], t[5]) == set) other = t[word];
        }
        KmStore(&m, k[0], k[1], k[2], k[3], k[4], k[5], 9, 9, 9, false);
        k[word] = other;
        assert(!KmFind(&m, k[0], k[1], k[2], k[3], k[4], k[5]));
    }
    // each sequence drops everything (O(1)), agents too; a generation wrap zeroes the slots
    m.nAgents = 3;
    assert(KmSync(&m, 2, 1, 1) && !KmFind(&m, 0x100, 0x200, 3, 0x10000, 0, 0) && m.nAgents == 0);
    KmStore(&m, 0x100, 0x200, 3, 0x10000, 0, 0, 1, 0, 0, true);
    assert(KmSync(&m, 2, 2, 1) && !KmFind(&m, 0x100, 0x200, 3, 0x10000, 0, 0));
    KmStore(&m, 0x100, 0x200, 3, 0x10000, 0, 0, 1, 0, 0, true);
    assert(KmSync(&m, 2, 2, 2) && !KmFind(&m, 0x100, 0x200, 3, 0x10000, 0, 0));
    m.gen = 0xFFFFFFFF, m.slot[7].gen = 1;
    KmReset(&m);
    assert(m.gen == 1 && m.slot[7].gen == 0);
    // id lists
    assert(KmListLen(0, 0) == 0 && KmListLen(0x1000, 0x1000) == 0 && KmListLen(0x1000, 0x1010) == 2 && KmListLen(0x1000, 0x1000 + 8 * KM_IDS) == KM_IDS);
    assert(KmListLen(0x1000, 0x1008 + 8 * KM_IDS) == -1 && KmListLen(0x1000, 0x1004) == -1 && KmListLen(0x1010, 0x1000) == -1 && KmListLen(0, 8) == -1);
    // the rules
    assert(KmEligible(1, 1, 1, false) && !KmEligible(0, 1, 1, false) && !KmEligible(1, 0, 1, false) && !KmEligible(1, 1, 0, false) && !KmEligible(1, 1, 1, true));
    assert(KmServeRule(1, true, true) == 1 && KmServeRule(2, true, true) == 0 && KmServeRule(0, true, true) == 0 && KmServeRule(1, false, true) == 0 &&
           KmServeRule(1, true, false) == 0);
}

static void TestCmHook() {
    ClassMemoCounters& c = ts3native_classmemo;
    const ClassMemoCounters saved = c;
    const KmRecalcFn savedNext = g_kmNext;
    const bool savedAll = g_kmAll;
    const volatile DWORD* const savedGlobal = g_csOptGlobal;
    const LONG savedWrite = g_snWriteSeq, savedHost = t_fmHostSeq;
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
    memset((void*)&c, 0, sizeof(c));
    g_kmNext = KmFakeRecalc, g_kmFakeRuns = 0, g_kmFakeGlobal = 1, g_csOptGlobal = &g_kmFakeGlobal;
    static KmFakeLeaf leafA, leafB;
    static DWORD ids[4 + KM_IDS * 2];
    static u8 opts[0x40], optsB[0x40], dblk[0x40], x[0x60];
    memset(opts, 0, sizeof(opts)), memset(optsB, 0, sizeof(optsB)), memset(dblk, 0, sizeof(dblk)), memset(x, 0, sizeof(x));
    memset(ids, 0, sizeof(ids));
    const DWORD F1 = 0x47C35000;  // 100000.0f
    leafA.cls[0] = 0, leafA.cls[1] = 1, leafA.cls[2] = 10, leafA.cls[3] = 6;
    leafA.mult[0] = 0x40000000, leafA.mult[1] = F1, leafA.mult[2] = 0x7F7FFFFF, leafA.mult[3] = 0x7F7FFFFF;
    leafA.owner[0] = 0x111, leafA.owner[1] = 0x222;
    leafB = leafA, leafB.cls[1] = 0, leafB.owner[0] = 0x333;
    *(DWORD*)(opts + 8) = 0x900, *(DWORD*)(opts + 0x28) = (DWORD)(uintptr_t)dblk;
    *(DWORD*)(optsB + 8) = 0x111, *(DWORD*)(optsB + 0xC) = 0x222;  // leaf A's own agent
    *(DWORD*)(x + 0x40) = (DWORD)(uintptr_t)opts, *(DWORD*)(x + 0x50) = 0x10000;
    const DWORD T = 0x7000;
    u8 cell[0x40];
    // mode 0: the game's function, nothing counted
    c.kmMode = 0;
    KmFakeCell(cell, &leafA, T, 1, x);
    KmRecalcHook(cell, NULL, x);
    assert(g_kmFakeRuns == 1 && c.kmCalls == 0 && cell[0x20] == 1 && *(DWORD*)(cell + 0x1C) == 1);
    // mode 1, every router: first call runs the game's function, the repeat is served with the same writes. Sub-cell 1 is
    // class 1: multiplier 100000 and the key cleared, both on the served call too
    c.kmMode = 1, g_kmAll = true;
    for (DWORD shape = 0; shape < 4; shape++) {
        const int r0 = g_kmFakeRuns;
        assert(KmSameAsGame(&leafA, T, shape, x) && g_kmFakeRuns == r0 + 1);
        assert(KmSameAsGame(&leafA, T, shape, x) && g_kmFakeRuns == r0 + 1);
    }
    assert(c.kmStores == 4 && c.kmHits == 4 && c.kmClass1 == 1 && c.kmMismatch == 0);
    KmFakeCell(cell, &leafA, T, 1, x);
    KmRecalcHook(cell, NULL, x);
    assert(cell[0x20] == 1 && *(DWORD*)(cell + 0x10) == F1 && *(DWORD*)(cell + 0x1C) == 1 && c.kmHits == 5);
    KmFakeCell(cell, &leafA, T, 3, x);
    KmRecalcHook(cell, NULL, x);
    assert(cell[0x20] == 6 && *(DWORD*)(cell + 0x1C) == (3 | 0x10000u << 8) && c.kmHits == 6);  // a cached class keeps its key
    // another node, tree, flags: each its own first call
    int r = g_kmFakeRuns;
    assert(KmSameAsGame(&leafB, T, 1, x) && g_kmFakeRuns == r + 1 && KmSameAsGame(&leafB, T, 1, x) && g_kmFakeRuns == r + 1);
    assert(KmSameAsGame(&leafA, T + 4, 1, x) && g_kmFakeRuns == r + 2);
    *(DWORD*)(x + 0x50) = 0x10010;  // flag 0x10: class 0 gets multiplier 1.0
    assert(KmSameAsGame(&leafA, T, 0, x) && g_kmFakeRuns == r + 3 && KmSameAsGame(&leafA, T, 0, x) && g_kmFakeRuns == r + 3);
    KmFakeCell(cell, &leafA, T, 0, x);
    KmRecalcHook(cell, NULL, x);
    assert(*(DWORD*)(cell + 0x10) == 0x3F800000);
    *(DWORD*)(x + 0x50) = 0x10000;
    KmFakeCell(cell, &leafA, T, 0, x);
    KmRecalcHook(cell, NULL, x);
    assert(*(DWORD*)(cell + 0x10) == 0x40000000 && g_kmFakeRuns == r + 3);  // the first flags' result is still its own
    // X+0x4C is part of the key and of the result: 9 with class 6 becomes 6, on the served call as well
    r = g_kmFakeRuns;
    *(DWORD*)(x + 0x4C) = 9;
    assert(KmSameAsGame(&leafA, T, 3, x) && g_kmFakeRuns == r + 1 && *(DWORD*)(x + 0x4C) == 6);
    *(DWORD*)(x + 0x4C) = 9;
    assert(KmSameAsGame(&leafA, T, 3, x) && g_kmFakeRuns == r + 1 && *(DWORD*)(x + 0x4C) == 6);
    *(DWORD*)(x + 0x4C) = 0;
    // the agent: leaf A's own agent sees class 0 on sub-cell 2; the first agent still sees 10
    r = g_kmFakeRuns;
    *(DWORD*)(x + 0x40) = (DWORD)(uintptr_t)optsB;
    KmFakeCell(cell, &leafA, T, 2, x);
    KmRecalcHook(cell, NULL, x);
    assert(cell[0x20] == 0 && g_kmFakeRuns == r + 1);
    *(DWORD*)(x + 0x40) = (DWORD)(uintptr_t)opts;
    KmFakeCell(cell, &leafA, T, 2, x);
    KmRecalcHook(cell, NULL, x);
    assert(cell[0x20] == 10 && g_kmFakeRuns == r + 1);
    // the same options object with another word (own id) or another ignore list is another agent
    *(DWORD*)(opts + 8) = 0x111, *(DWORD*)(opts + 0xC) = 0x222;
    KmFakeCell(cell, &leafA, T, 2, x);
    KmRecalcHook(cell, NULL, x);
    assert(cell[0x20] == 0 && g_kmFakeRuns == r + 2);
    *(DWORD*)(opts + 8) = 0x900, *(DWORD*)(opts + 0xC) = 0;
    ids[0] = 0x111, ids[1] = 0x222;
    *(DWORD*)(dblk + 0x38) = (DWORD)(uintptr_t)ids, *(DWORD*)(dblk + 0x3C) = (DWORD)(uintptr_t)(ids + 2);
    KmFakeCell(cell, &leafA, T, 2, x);
    KmRecalcHook(cell, NULL, x);
    assert(cell[0x20] == 0 && g_kmFakeRuns == r + 3);  // ignored through the list
    ids[0] = 0x112;                                    // the list's content changed in place: another agent again
    KmFakeCell(cell, &leafA, T, 2, x);
    KmRecalcHook(cell, NULL, x);
    assert(cell[0x20] == 10 && g_kmFakeRuns == r + 4);
    // the global that gates the data block is read every call
    ids[0] = 0x111, g_kmFakeGlobal = 0;
    KmFakeCell(cell, &leafA, T, 2, x);
    KmRecalcHook(cell, NULL, x);
    assert(cell[0x20] == 10);
    g_kmFakeGlobal = 1;
    // a list longer than KM_IDS: never looked at, and more agents than KM_AGENTS: the game's function
    *(DWORD*)(dblk + 0x3C) = (DWORD)(uintptr_t)(ids + 2 + KM_IDS * 2);
    LONG na = c.kmNoAgent;
    r = g_kmFakeRuns;
    KmFakeCell(cell, &leafA, T, 2, x);
    KmRecalcHook(cell, NULL, x);
    KmFakeCell(cell, &leafA, T, 2, x);
    KmRecalcHook(cell, NULL, x);
    assert(c.kmNoAgent == na + 2 && g_kmFakeRuns == r + 2 && cell[0x20] == 0);
    *(DWORD*)(dblk + 0x38) = *(DWORD*)(dblk + 0x3C) = 0;
    InterlockedIncrement(&g_snWriteSeq);
    na = c.kmNoAgent;
    for (DWORD i = 0; i < KM_AGENTS + 2; i++) {
        *(DWORD*)(opts + 0x10) = 0x5000 + i;
        assert(KmSameAsGame(&leafA, T, 2, x));
    }
    assert(c.kmNoAgent == na + 2);
    *(DWORD*)(opts + 0x10) = 0;
    InterlockedIncrement(&g_snWriteSeq);  // the agent records go with the memo
    // each sequence drops the memo: a routing write, a table clear, a managed call
    for (int q = 0; q < 3; q++) {
        KmFakeCell(cell, &leafA, T, 1, x);
        KmRecalcHook(cell, NULL, x);
        r = g_kmFakeRuns;
        KmFakeCell(cell, &leafA, T, 1, x);
        KmRecalcHook(cell, NULL, x);
        assert(g_kmFakeRuns == r);
        const LONG rs = c.kmResets;
        if (q == 0) InterlockedIncrement(&g_snWriteSeq); else if (q == 1) InterlockedIncrement(&g_nmClearSeq); else t_fmHostSeq++;
        leafA.cls[1] = (DWORD)(4 + q);  // the data really changed
        KmFakeCell(cell, &leafA, T, 1, x);
        KmRecalcHook(cell, NULL, x);
        assert(g_kmFakeRuns == r + 1 && c.kmResets == rs + 1 && cell[0x20] == 4 + q);
    }
    leafA.cls[1] = 1;
    InterlockedIncrement(&g_snWriteSeq);
    // never looked at: no node, no context, key 0, the clearance case (flag 0x200000 on a sub-cell with a parent)
    const LONG calls = c.kmCalls;
    r = g_kmFakeRuns;
    for (int i = 0; i < 2; i++) {
        KmFakeCell(cell, NULL, T, 1, x);
        KmRecalcHook(cell, NULL, x);
        assert(cell[0x20] == 12);
        KmFakeCell(cell, &leafA, T, 3, x);
        KmRecalcHook(cell, NULL, NULL);
        KmFakeCell(cell, &leafA, T, 3, x);
        *(DWORD*)(cell + 0x1C) = 3;
        KmRecalcHook(cell, NULL, x);
        *(DWORD*)(x + 0x50) = 0x210000;
        KmFakeCell(cell, &leafA, T, 3, x);
        *(DWORD*)(cell + 0x08) = 0x1234;
        KmRecalcHook(cell, NULL, x);
        *(DWORD*)(x + 0x50) = 0x10000;
    }
    assert(c.kmCalls == calls && g_kmFakeRuns == r + 8 && c.kmNoNode == 6 && c.kmClearance == 2);
    // flag 0x200000 on a whole cell (shape 0) or a cell with no parent is looked at
    *(DWORD*)(x + 0x50) = 0x210000;
    r = g_kmFakeRuns;
    assert(KmSameAsGame(&leafA, T, 0, x) && KmSameAsGame(&leafA, T, 0, x) && g_kmFakeRuns == r + 1);
    assert(KmSameAsGame(&leafA, T, 3, x) && KmSameAsGame(&leafA, T, 3, x) && g_kmFakeRuns == r + 2);
    *(DWORD*)(x + 0x50) = 0x10000;
    // household / unknown router (no session on this thread: scope 0) with ClassMemoAll=0: the game's function, compared
    assert(KmSameAsGame(&leafA, T, 1, x) && KmSameAsGame(&leafA, T, 1, x));
    g_kmAll = false;
    const LONG s0 = c.kmScopeMiss, k0 = c.kmChecks;
    r = g_kmFakeRuns;
    assert(KmSameAsGame(&leafA, T, 1, x) && g_kmFakeRuns == r + 1 && c.kmScopeMiss == s0 + 1 && c.kmChecks == k0 + 1 && c.kmMismatch == 0);
    // shadow: always the game's function; data changed without a sequence counts a mismatch and the game's result is kept
    c.kmMode = 2, g_kmAll = true;
    r = g_kmFakeRuns;
    assert(KmSameAsGame(&leafA, T, 1, x) && g_kmFakeRuns == r + 1 && c.kmMismatch == 0);
    leafA.cls[1] = 7;
    assert(KmSameAsGame(&leafA, T, 1, x) && g_kmFakeRuns == r + 2 && c.kmMismatch == 1);
    assert(KmSameAsGame(&leafA, T, 1, x) && c.kmMismatch == 1);
    leafA.cls[1] = 1;
    g_kmNext = savedNext, g_kmAll = savedAll, g_csOptGlobal = savedGlobal;
    g_snWriteSeq = savedWrite, t_fmHostSeq = savedHost;
    c = saved;
}

// ---- OpenHint fakes ----
enum { OHT_NODES = 1500, OHT_BUCKETS = 12 };
struct OhFakeNode { u8 b[0x40]; };
static OhFakeNode g_ohNodes[OHT_NODES];
static DWORD g_ohBuckets[OHT_BUCKETS][3];
static unsigned short g_ohGenWord;
static int g_ohFakeRuns;
static DWORD g_ohRng = 2463534242u;
static DWORD OhRnd() { g_ohRng ^= g_ohRng << 13, g_ohRng ^= g_ohRng >> 17, g_ohRng ^= g_ohRng << 5; return g_ohRng; }
#define OHT_F(n) (*(float*)((u8*)(uintptr_t)(n) + 0x24))
#define OHT_W(n, off) (*(DWORD*)((u8*)(uintptr_t)(n) + (off)))

// the reference: where the game's walk ends for a node of this f (0 = the tail)
static DWORD OhRefSucc(const DWORD* b, float f) {
    DWORD cur = b[0];
    while (cur != b[2] && f > OHT_F(cur)) cur = OHT_W(cur, 0x3C);
    return cur;
}
// the "game": 0x61B880 from its description
static void __fastcall OhFakeInsert(DWORD* b, void*, DWORD n) {
    g_ohFakeRuns++;
    const DWORD cur = OhRefSucc(b, OHT_F(n));
    if (cur == b[2]) {
        if (b[1] != b[2]) OHT_W(b[1], 0x3C) = n, OHT_W(n, 0x38) = b[1], OHT_W(n, 0x3C) = b[2], b[1] = n;
        else b[0] = b[1] = n, OHT_W(n, 0x38) = b[2], OHT_W(n, 0x3C) = b[2];
        return;
    }
    const DWORD prev = OHT_W(cur, 0x38);
    OHT_W(n, 0x38) = prev, OHT_W(n, 0x3C) = cur, OHT_W(cur, 0x38) = n;
    if (cur == b[0]) b[0] = n; else OHT_W(prev, 0x3C) = n;
}
// unlink (the pop's and 0x61B990's effect on the list)
static void OhFakeUnlink(DWORD* b, DWORD n) {
    const DWORD prev = OHT_W(n, 0x38), next = OHT_W(n, 0x3C);
    if (prev == b[2]) b[0] = next; else OHT_W(prev, 0x3C) = next;
    if (next == b[2]) b[1] = prev; else OHT_W(next, 0x38) = prev;
}
// 0x61B910: a node whose f dropped inside its bucket moves towards the head, behind the first node whose f is not above
static void OhFakeMoveUp(DWORD* b, DWORD n) {
    if (n == b[0]) return;
    DWORD p = OHT_W(n, 0x38);
    if (!(OHT_F(p) > OHT_F(n))) return;
    OhFakeUnlink(b, n);
    while (p != b[2] && OHT_F(p) > OHT_F(n)) p = OHT_W(p, 0x38);
    const DWORD next = p == b[2] ? b[0] : OHT_W(p, 0x3C);
    OHT_W(n, 0x38) = p, OHT_W(n, 0x3C) = next;
    if (p == b[2]) b[0] = n; else OHT_W(p, 0x3C) = n;
    if (next == b[2]) b[1] = n; else OHT_W(next, 0x38) = n;
}
// the bucket is a well-formed list in non-decreasing f whose members are open and carry the bucket's index
static bool OhListOk(const DWORD* b, DWORD idx, int* count) {
    DWORD prev = b[2];
    int n = 0;
    for (DWORD cur = b[0]; cur != b[2]; prev = cur, cur = OHT_W(cur, 0x3C), n++) {
        if (OHT_W(cur, 0x38) != prev || OHT_W(cur, 0x34) != idx || (OHT_W(cur, 0x30) >> 20 & 3) != 1) return false;
        if (prev != b[2] && OHT_F(prev) > OHT_F(cur)) return false;
        if (n > OHT_NODES) return false;
    }
    *count = n;
    return b[1] == prev;
}

// one run of searches. mode 1: every insert through the hook must land before the reference's node. mode 2: the game's
// function links every node and the hinted position is compared by the hook itself. kinds: bit 0 = graph nodes (kept as
// hints), bit 1 = other nodes (never kept)
static void OhModel(int mode, int rounds, int kinds, float spread) {
    OpenHintCounters& c = ts3native_openhint;
    c.ohMode = mode;
    static DWORD openSet[OHT_NODES];
    for (int round = 0; round < rounds; round++) {
        g_ohGenWord++;                                            // 0x61E360: a new search
        if (round % 3 == 2) InterlockedIncrement(&g_nmClearSeq);  // every third search follows a table clear
        memset(g_ohBuckets, 0, sizeof(g_ohBuckets));
        int used = 0, nOpen = 0;
        while (used < OHT_NODES) {
            const DWORD op = OhRnd() % 100;
            if (op < 62 || nOpen == 0) {  // insert a new node; f in steps of 1/64 so ties are common
                const DWORD n = (DWORD)(uintptr_t)&g_ohNodes[used++];
                const float f = (float)(OhRnd() % (DWORD)(spread * 64.0f)) / 64.0f;
                memset((void*)(uintptr_t)n, 0, 0x40);
                OHT_W(n, 0) = (kinds == 3 ? (OhRnd() & 3) != 0 : kinds == 1) ? g_csVtGraph : 0x777;
                OHT_F(n) = f;
                const DWORD idx = (DWORD)f;
                OHT_W(n, 0x30) = g_ohGenWord | 1u << 20, OHT_W(n, 0x34) = idx;
                DWORD* const b = g_ohBuckets[idx];
                const DWORD want = OhRefSucc(b, f);
                OhInsertHook(b, NULL, n);
                assert(OHT_W(n, 0x3C) == want);
                openSet[nOpen++] = n;
            } else if (op < 85) {  // pop: the head of the lowest bucket leaves and is closed; its own links stay
                for (DWORD i = 0; i < OHT_BUCKETS; i++) {
                    DWORD* const b = g_ohBuckets[i];
                    if (b[0] == b[2]) continue;
                    const DWORD n = b[0];
                    b[0] = OHT_W(n, 0x3C);
                    if (b[0] == b[2]) b[1] = b[2]; else OHT_W(b[0], 0x38) = b[2];
                    OHT_W(n, 0x30) = g_ohGenWord | 2u << 20;
                    for (int j = 0; j < nOpen; j++)
                        if (openSet[j] == n) { openSet[j] = openSet[--nOpen]; break; }
                    break;
                }
            } else {  // update: an open node's f drops; inside its bucket it moves up, else it is unlinked and inserted
                const DWORD n = openSet[OhRnd() % (DWORD)nOpen];
                const DWORD oldIdx = OHT_W(n, 0x34);
                float f = OHT_F(n) - (float)(OhRnd() % 96) / 64.0f;
                if (f < 0) f = 0;
                OHT_F(n) = f;
                const DWORD idx = (DWORD)f;
                if (idx == oldIdx) { OhFakeMoveUp(g_ohBuckets[idx], n); continue; }
                OhFakeUnlink(g_ohBuckets[oldIdx], n);
                OHT_W(n, 0x34) = idx;
                DWORD* const b = g_ohBuckets[idx];
                const DWORD want = OhRefSucc(b, f);
                OhInsertHook(b, NULL, n);
                assert(OHT_W(n, 0x3C) == want);
            }
            if ((used & 31) == 0) {
                int total = 0, cnt = 0;
                for (DWORD i = 0; i < OHT_BUCKETS; i++) assert(OhListOk(g_ohBuckets[i], i, &cnt)), total += cnt;
                assert(total == nOpen);
            }
        }
    }
}

static void TestOhCore() {
    assert(OhSub(3.0f, 3) == 0 && OhSub(3.5f, 3) == 16 && OhSub(3.999f, 3) == 31 && OhSub(9.0f, 3) == 31 && OhSub(2.0f, 3) == 0);
    static OhCtx x;
    memset(&x, 0, sizeof(x));
    OhSlot* s = OhSlotFor(&x, 0x1000, 5, 7, 1);
    assert(s == &x.slot[5] && s->mask == 0 && s->count == 0);
    s->mask = 5, s->count = 9;
    assert(OhSlotFor(&x, 0x1000, 5, 7, 1)->mask == 5);
    assert(OhSlotFor(&x, 0x1000, 5, 8, 1)->mask == 0);  // another search
    s->mask = 5;
    assert(OhSlotFor(&x, 0x1000, 5, 8, 2)->mask == 0);  // a table clear
    s->mask = 5;
    assert(OhSlotFor(&x, 0x100C, 5 + OH_SLOTS, 8, 2)->mask == 0);  // another bucket in the same slot
    s->mask = 5;
    assert(OhSlotFor(&x, 0x100C, 5 + OH_SLOTS, 0, 2)->mask == 0 && s->gen == 0x10000);  // generation 0 is a generation too
    // the proof on a three-node list a(1.0) b(2.0) c(2.0)
    static OhFakeNode nd[5];
    memset(nd, 0, sizeof(nd));
    DWORD b[3] = {0, 0, 0};
    const DWORD A = (DWORD)(uintptr_t)&nd[0], B = (DWORD)(uintptr_t)&nd[1], C = (DWORD)(uintptr_t)&nd[2], N = (DWORD)(uintptr_t)&nd[3];
    const float fs[3] = {1.0f, 2.0f, 2.0f};
    for (int i = 2; i >= 0; i--) {  // c, then b before it (newest first on a tie), then a
        const DWORD n = (DWORD)(uintptr_t)&nd[i];
        OHT_F(n) = fs[i], OHT_W(n, 0x30) = 9 | 1u << 20, OHT_W(n, 0x34) = 4;
        OhFakeInsert(b, NULL, n);
    }
    assert(b[0] == A && OHT_W(A, 0x3C) == B && OHT_W(B, 0x3C) == C && b[1] == C);
    OHT_W(N, 0x30) = 9 | 1u << 20, OHT_W(N, 0x34) = 4;
    assert(OhValid((const uint32_t*)b, A, N, 0.5f, 9, 4));                                  // the head always may start
    assert(OhValid((const uint32_t*)b, B, N, 1.5f, 9, 4) && OhValid((const uint32_t*)b, B, N, 2.0f, 9, 4));
    assert(!OhValid((const uint32_t*)b, B, N, 1.0f, 9, 4) && !OhValid((const uint32_t*)b, B, N, 0.5f, 9, 4));  // a is not below 1.0
    assert(!OhValid((const uint32_t*)b, C, N, 2.0f, 9, 4) && OhValid((const uint32_t*)b, C, N, 2.5f, 9, 4));   // b ties with 2.0
    assert(!OhValid((const uint32_t*)b, B, N, 1.5f, 10, 4) && !OhValid((const uint32_t*)b, B, N, 1.5f, 9, 5)); // other search / bucket
    assert(!OhValid((const uint32_t*)b, 0, N, 1.5f, 9, 4) && !OhValid((const uint32_t*)b, N, N, 1.5f, 9, 4));
    OHT_W(B, 0x30) = 9 | 2u << 20;
    assert(!OhValid((const uint32_t*)b, B, N, 1.5f, 9, 4));  // closed
    OHT_W(B, 0x30) = 9 | 1u << 20;
    const DWORD nanBits = 0x7FC00000;
    float notNum = 0;
    memcpy(&notNum, &nanBits, sizeof(notNum));
    assert(!OhValid((const uint32_t*)b, B, N, notNum, 9, 4));  // a NaN proves nothing
    // a popped head: closed, and the list's head no longer is it
    b[0] = B, OHT_W(B, 0x38) = 0, OHT_W(A, 0x30) = 9 | 2u << 20;
    assert(!OhValid((const uint32_t*)b, A, N, 2.5f, 9, 4));
    OHT_W(A, 0x30) = 9 | 1u << 20;  // even if it still looked open: its links do not agree with the list
    assert(!OhValid((const uint32_t*)b, A, N, 2.5f, 9, 4));
    // walk and link from a start give the reference's list
    uint32_t steps = 0;
    OHT_F(N) = 2.0f;
    assert(OhWalk(B, 0, 2.0f, &steps) == B && steps == 0 && OhWalk(B, 0, 2.5f, &steps) == 0 && steps == 2);
    OhLink((uint32_t*)b, N, B);
    assert(b[0] == N && OHT_W(N, 0x3C) == B && OHT_W(B, 0x38) == N && OHT_W(N, 0x38) == 0);
    const DWORD M = (DWORD)(uintptr_t)&nd[4];
    OhLink((uint32_t*)b, M, 0);
    assert(b[1] == M && OHT_W(C, 0x3C) == M && OHT_W(M, 0x38) == C && OHT_W(M, 0x3C) == 0);
    DWORD e[3] = {0, 0, 0};
    OhLink((uint32_t*)e, A, 0);
    assert(e[0] == A && e[1] == A && OHT_W(A, 0x38) == 0 && OHT_W(A, 0x3C) == 0);
    OhLink((uint32_t*)e, B, A);
    OhLink((uint32_t*)e, C, A);
    assert(e[0] == B && OHT_W(B, 0x3C) == C && OHT_W(C, 0x3C) == A && OHT_W(A, 0x38) == C && OHT_W(C, 0x38) == B && e[1] == A);
}

static void TestOhHook() {
    OpenHintCounters& c = ts3native_openhint;
    const OpenHintCounters saved = c;
    const OhInsertFn savedNext = g_ohNext;
    const bool savedAll = g_ohAll;
    const volatile unsigned short* const savedGen = g_csGen;
    const DWORD savedVt = g_csVtGraph;
    memset((void*)&c, 0, sizeof(c));
    g_ohNext = OhFakeInsert, g_ohFakeRuns = 0, g_csGen = &g_ohGenWord, g_csVtGraph = 0x00FEA080, g_ohAll = true;
    // mode 0: the game's function, nothing counted
    c.ohMode = 0;
    memset(g_ohBuckets, 0, sizeof(g_ohBuckets));
    memset(g_ohNodes, 0, sizeof(OhFakeNode));
    OhInsertHook(g_ohBuckets[0], NULL, (DWORD)(uintptr_t)&g_ohNodes[0]);
    assert(g_ohFakeRuns == 1 && c.ohCalls == 0 && g_ohBuckets[0][0] == (DWORD)(uintptr_t)&g_ohNodes[0]);
    // serve: crowded buckets (f over 2 units), then sparse ones (12 units); most inserts of a crowded bucket start at a hint
    OhModel(1, 6, 1, 2.0f);
    assert(c.ohMismatch == 0 && c.ohHits > c.ohCalls / 2 && c.ohHits + g_ohFakeRuns - 1 == c.ohCalls);
    assert(c.ohSteps < c.ohHits * 8);  // a hinted walk passes a few nodes, where the walk from the head passes tens
    assert(c.ohWalkN > 0 && c.ohWalkSteps > c.ohWalkN * 20);
    OhModel(1, 6, 1, 12.0f);
    OhModel(1, 6, 3, 4.0f);  // start / goal-like nodes mixed in: linked as any node, never kept
    assert(c.ohMismatch == 0);
    // only nodes that are no cell graph node: nothing is ever kept, so every insert is the game's
    LONG h0 = c.ohHits;
    OhModel(1, 3, 2, 2.0f);
    assert(c.ohHits == h0);
    // an end mark that is not 0: the game's function
    static OhFakeNode odd[2];
    memset(odd, 0, sizeof(odd));
    DWORD ob[3] = {0x10, 0x10, 0x10};
    const int r0 = g_ohFakeRuns;
    OhInsertHook(ob, NULL, (DWORD)(uintptr_t)&odd[0]);
    assert(c.ohOdd == 1 && g_ohFakeRuns == r0 + 1 && ob[0] == (DWORD)(uintptr_t)&odd[0]);
    // household / unknown router (no session on this thread: scope 0) with OpenHintAll=0: the game's function every time
    g_ohAll = false;
    h0 = c.ohHits;
    const LONG calls0 = c.ohCalls;
    const int r1 = g_ohFakeRuns;
    OhModel(1, 3, 1, 2.0f);
    assert(c.ohHits == h0 && c.ohScopeMiss > 0 && g_ohFakeRuns - r1 == c.ohCalls - calls0);
    // shadow: the game links every node; the hinted position equals it
    g_ohAll = true;
    h0 = c.ohHits;
    const LONG calls1 = c.ohCalls;
    const int r2 = g_ohFakeRuns;
    OhModel(2, 6, 1, 2.0f);
    assert(c.ohHits == h0 && c.ohChecks > 1000 && c.ohMismatch == 0 && g_ohFakeRuns - r2 == c.ohCalls - calls1);
    // a "game" that links somewhere else is caught: here one that always appends
    struct L { static void __fastcall Append(DWORD* b, void*, DWORD n) {
        if (b[1] != b[2]) OHT_W(b[1], 0x3C) = n, OHT_W(n, 0x38) = b[1], OHT_W(n, 0x3C) = b[2], b[1] = n;
        else b[0] = b[1] = n, OHT_W(n, 0x38) = b[2], OHT_W(n, 0x3C) = b[2];
    } };
    g_ohNext = L::Append;
    g_ohGenWord++;
    memset(g_ohBuckets, 0, sizeof(g_ohBuckets));
    for (int i = 0; i < 64; i++) {  // falling f: the reference puts each node at the head, the appender at the tail
        const DWORD n = (DWORD)(uintptr_t)&g_ohNodes[i];
        memset((void*)(uintptr_t)n, 0, 0x40);
        OHT_W(n, 0) = g_csVtGraph, OHT_F(n) = i < 32 ? 0.25f + (float)i / 64.0f : 0.5f - (float)(i - 32) / 256.0f;
        OHT_W(n, 0x30) = g_ohGenWord | 1u << 20;
        OhInsertHook(g_ohBuckets[0], NULL, n);
    }
    assert(c.ohMismatch > 0);
    g_ohNext = savedNext, g_ohAll = savedAll, g_csGen = savedGen, g_csVtGraph = savedVt;
    c = saved;
}

static void TestCellSpeed() {
    TestCmCore();
    TestCmHook();
    TestOhCore();
    TestOhHook();
    printf("cell speed: OK (class memo: sets / agents / sequences, serve = the game's four writes, key clear, X+4C, scope, shadow; "
           "open hint: proof, walk, link, model runs against the reference insert, shadow)\n");
}
