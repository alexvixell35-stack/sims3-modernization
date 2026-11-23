// docs/676 §14 smoothmemo.cpp / smoothmemo-core.h: [WorldPrecompute] SmoothMemo. Included by selftest.cpp and
// smoothmemo-host-test.cpp (the same cases on Linux, 32-bit).
// The table (store, find, a full probe run, the generation) and the three hooks on fake cursors and a fake context laid
// out as the game's (cursor: tree +0x04, parent +0x08, node +0x0C, multiplier +0x10, size +0x14, position +0x18, shape and
// key +0x1C, class and quadrant +0x20; context: options +0x40, class word +0x4C, flags +0x50, lot +0x54; tree: pending
// edits +0x4C). The fake readers do what 0x66B670 / 0x66B600 do around a fake evaluator whose answer depends on exactly
// the fields the game's evaluator reads, plus a per-node "footprint" word the tests change to stand for a routing write.
// Cases: no pass open; mode 0; a pass against a run of the same asks without the memo (every cursor word, context word
// and return value equal, fewer evaluator runs); class 1; the class word's replay; flags above bit 23; a new pass; the
// write and managed-call sequences; another context (flags, class word, lot, options, an option word) and another tree;
// a tree with pending edits; no context; the cursor's own cached answer; a pass left open by an unwind; shadow and a
// forced mismatch; scope and the command rule; a thread with no block.
// Preconditions set by the test itself: mode, scope, sequences, the reader and pass originals. No timer decides a result.

struct SmtCursor { DWORD vt, w04, w08, w0c, w10, w14, w18, w1c, w20; };
static u8 g_smtTree[2][0x50], g_smtCtx[0x60], g_smtOpts[2][0x38];
static DWORD g_smtFoot[64];  // per node: what the "footprints" make of it
static LONG g_smtRuns;       // fake evaluator runs
static DWORD g_smtSeed = 97531;
static DWORD SmtRnd(DWORD n) { g_smtSeed = g_smtSeed * 1103515245 + 12345; return ((g_smtSeed >> 8) & 0xFFFFFF) % n; }
static DWORD SmtW(const u8* p, int off) { return *(const DWORD*)(p + off); }

// the evaluator: sets +0x10 and the class byte, as 0x66B090; the class word rule of docs/98 §5.3 step 1
static void SmtEval(u8* cur, u8* ctx) {
    SmtCursor* c = (SmtCursor*)cur;
    g_smtRuns++;
    const DWORD opts = SmtW(ctx, 0x40);
    const DWORD tree = c->w04 == (DWORD)(uintptr_t)g_smtTree[1] ? 1 : 0;  // never an address: the answers must not depend on the layout
    DWORD h = g_smtFoot[c->w0c & 63] * 2654435761u ^ tree * 0x51ED27u ^ c->w08 * 31 ^ c->w14 * 131 ^ c->w18 * 65537 ^ (c->w1c & 0xFF) * 7 ^ ((c->w20 >> 8) & 3) * 1009;
    h ^= SmtW(ctx, 0x50) * 40503u;
    if (opts) h ^= SmtW((const u8*)(uintptr_t)opts, 0x28) * 977 + SmtW((const u8*)(uintptr_t)opts, 0x08);
    h ^= h >> 13, h *= 0x5BD1E995u, h ^= h >> 15;
    const DWORD cls = h % 14;             // 0..13
    c->w10 = 0x3F800000u + (h & 0xFFFF);  // a float's bits
    const DWORD xc = SmtW(ctx, 0x4C);
    if (!(xc < 2 || xc == 13 || (LONG)xc < (LONG)cls)) *(DWORD*)(ctx + 0x4C) = cls, c->w10 ^= 0x10000;
    *(u8*)&c->w20 = (u8)cls;
    if (cls == 1) c->w1c &= 0xFF;  // class 1: the key is cleared, every use evaluates again
}
static void SmtEnsure(u8* cur, u8* ctx) {
    SmtCursor* c = (SmtCursor*)cur;
    if ((c->w1c >> 8) == SmtW(ctx, 0x50)) return;
    c->w1c = (c->w1c & 0xFF) | (SmtW(ctx, 0x50) << 8);
    SmtEval(cur, ctx);
}
static float __fastcall SmtMult(u8* cur, void*, u8* ctx) {
    if (ctx) SmtEnsure(cur, ctx);
    return *(const float*)(cur + 0x10);
}
static LONG __fastcall SmtClass(u8* cur, void*, u8* ctx) {
    if (ctx) SmtEnsure(cur, ctx);
    return (LONG)(signed char)cur[0x20];
}

// a fresh stack cursor on cell `cell` of tree `tree`, as the walk builds one (garbage in +0x10 and above bit 9 of +0x20)
static void SmtCursorAt(SmtCursor* c, int tree, DWORD cell, DWORD garbage) {
    c->vt = 0x0107396C, c->w04 = (DWORD)(uintptr_t)g_smtTree[tree], c->w08 = 0x1000 + (cell >> 2), c->w0c = 0x2000 + cell * 4 + (DWORD)tree * 2;
    c->w10 = garbage, c->w14 = 0x40000000, c->w18 = cell * 0x10001, c->w1c = cell % 21, c->w20 = ((cell & 3) << 8) | (garbage << 10);
}
static DWORD SmtBits(float f) { return *(const DWORD*)&f; }

// What a pass does: g_smtN asks in the order g_smtSeedPass gives (multiplier, and for two in three the class too), each on
// a fresh cursor. g_smtRef: the fake readers alone, else through the hooks. Every word an ask leaves goes into g_smtSum.
static int g_smtN;
static DWORD g_smtSeedPass, g_smtSum;
static bool g_smtRef;
static int g_smtMid;  // halfway: 1 a routing write with the sequence moved, 2 a write without it, 3 a managed call
static DWORD SmtFold(DWORD sum, DWORD ret, const SmtCursor& a) {
    return (sum * 31 + ret) * 31 + a.w10 * 3 + a.w1c * 5 + (a.w20 & 0x3FF) * 7 + SmtW(g_smtCtx, 0x4C) * 11;
}
static DWORD __fastcall SmtPass(void* smoother, void*, void* path) {
    const DWORD saved = g_smtSeed;
    g_smtSeed = g_smtSeedPass;
    DWORD sum = 0;
    for (int i = 0; i < g_smtN; i++) {
        if (i == g_smtN / 2 && g_smtMid == 3) t_fmHostSeq++;
        if (i == g_smtN / 2 && (g_smtMid == 1 || g_smtMid == 2)) {
            for (int j = 0; j < 64; j++) g_smtFoot[j] += 77;
            if (g_smtMid == 1) InterlockedIncrement(&g_snWriteSeq);
        }
        const DWORD cell = SmtRnd(24);
        const int tree = (int)SmtRnd(2);
        SmtCursor a;
        SmtCursorAt(&a, tree, cell, SmtRnd(1000));
        const bool both = SmtRnd(3) != 0;
        const float m = g_smtRef ? SmtMult((u8*)&a, NULL, g_smtCtx) : SmMultHook((u8*)&a, NULL, g_smtCtx);
        sum = SmtFold(sum, SmtBits(m), a);
        if (both) sum = SmtFold(sum, (DWORD)(g_smtRef ? SmtClass((u8*)&a, NULL, g_smtCtx) : SmClassHook((u8*)&a, NULL, g_smtCtx)), a);
    }
    g_smtSeed = saved;
    g_smtSum = sum;
    return ((DWORD)(uintptr_t)smoother & 0xFFFFFF00) | ((DWORD)(uintptr_t)path & 0xFF);  // the whole of eax must come back
}
static void SmtWorld() {
    memset(g_smtTree, 0, sizeof(g_smtTree)), memset(g_smtCtx, 0, sizeof(g_smtCtx)), memset(g_smtOpts, 0, sizeof(g_smtOpts));
    for (int i = 0; i < 64; i++) g_smtFoot[i] = 1000 + (DWORD)i * 17;
    *(DWORD*)(g_smtCtx + 0x40) = (DWORD)(uintptr_t)g_smtOpts[0], *(DWORD*)(g_smtCtx + 0x50) = 0x210010, *(DWORD*)(g_smtCtx + 0x54) = 0x5000;
    *(DWORD*)(g_smtOpts[0] + 0x08) = 0x1234, *(DWORD*)(g_smtOpts[1] + 0x08) = 0x1234;
}
// one pass through the pass hook with the memo as set: the sum; *runs = evaluator runs
static DWORD SmtRun(int n, DWORD seed, LONG* runs) {
    g_smtN = n, g_smtSeedPass = seed, g_smtRef = false;
    const LONG r0 = g_smtRuns;
    const DWORD r = SmPassHook((void*)(uintptr_t)0xABCDEF00u, NULL, (void*)(uintptr_t)0x11111101u);
    assert(r == 0xABCDEF01u);
    *runs = g_smtRuns - r0;
    return g_smtSum;
}
// the same asks with the fake readers alone
static DWORD SmtRefRun(int n, DWORD seed, LONG* runs) {
    g_smtN = n, g_smtSeedPass = seed, g_smtRef = true;
    const LONG r0 = g_smtRuns;
    SmtPass(NULL, NULL, NULL);
    *runs = g_smtRuns - r0;
    return g_smtSum;
}
static void SmtUndoWrite() {
    for (int j = 0; j < 64; j++) g_smtFoot[j] -= 77;
}

static void TestSmCore() {
    static SmTab tab;
    memset(&tab, 0, sizeof(tab));
    SmKey k;
    memset(&k, 0, sizeof(k));
    SmVal v = {1, 2, 3, 4};
    SmClear(&tab);  // as every pass does before its first ask
    assert(tab.gen == 1 && !SmFind(&tab, k));
    for (unsigned int i = 0; i < 300; i++) {
        k.w0c = 0x1000 + i * 4, k.w18 = i * 0x10001, v.w10 = i;
        assert(!SmFind(&tab, k));
        assert(SmStore(&tab, k, v));
    }
    assert(tab.used == 300);
    for (unsigned int i = 0; i < 300; i++) {
        k.w0c = 0x1000 + i * 4, k.w18 = i * 0x10001;
        const SmEntry* e = SmFind(&tab, k);
        assert(e && e->v.w10 == i && e->v.ctxCls == 4);
    }
    // every key word separates records
    k.w0c = 0x1000, k.w18 = 0;
    assert(SmFind(&tab, k));
    unsigned int* const words = (unsigned int*)&k;
    for (int w = 0; w < (int)(sizeof(SmKey) / 4); w++) {
        const SmKey same = k;
        assert(SmKeyEq(same, k));
        words[w] ^= 0x40;
        assert(!SmFind(&tab, k) && !SmKeyEq(same, k));  // the compare itself, not only the slot the hash picks
        words[w] ^= 0x40;
    }
    unsigned int* const vw = (unsigned int*)&v;
    for (int w = 0; w < (int)(sizeof(SmVal) / 4); w++) {
        const SmVal same = v;
        assert(SmValEq(same, v));
        vw[w] ^= 0x40;
        assert(!SmValEq(same, v));
        vw[w] ^= 0x40;
    }
    // a run of SM_PROBE taken slots: the next key that lands on it is not kept, and not found
    SmClear(&tab);
    assert(tab.used == 0 && !SmFind(&tab, k));
    int refused = 0, kept = 0;
    for (unsigned int i = 0; i < 20000; i++) {
        k.w0c = 0x9000 + i * 4, k.w18 = i;
        if (SmFind(&tab, k)) continue;
        if (SmStore(&tab, k, v)) kept++;
        else { refused++; assert(!SmFind(&tab, k)); }
    }
    assert(refused > 0 && kept == (int)tab.used && kept <= SM_SLOTS);
    // the generation wraps: old slots must not come back to life
    tab.gen = 0xFFFFFFFFu;
    for (int i = 0; i < SM_SLOTS; i++) tab.e[i].gen = 1;
    SmClear(&tab);
    assert(tab.gen == 1 && tab.used == 0);
    k.w0c = 0x9000, k.w18 = 0;
    assert(!SmFind(&tab, k));
    // the option hash reads 0x38 bytes
    u8 o[0x3C];
    memset(o, 0, sizeof(o));
    const unsigned int h0 = SmOptHash(o);
    for (int b = 0; b < 0x38; b++) {
        o[b] = 1;
        assert(SmOptHash(o) != h0);
        o[b] = 0;
    }
    o[0x38] = 1;
    assert(SmOptHash(o) == h0 && SmOptHash(NULL) != h0);
}

static void TestSmHooks() {
    SmoothMemoCounters& c = ts3native_smoothmemo;
    static u8 savedC[sizeof(SmoothMemoCounters)];
    memcpy(savedC, (const void*)&c, sizeof(savedC));
    const SmMultFn savedM = g_smMultOrig;
    const SmClassFn savedCl = g_smClassOrig;
    const SmPassFn savedP = g_smPassOrig;
    const bool savedAll = g_smAll, savedOpen = t_pd.open, savedMemoOpen = t_sm.open;
    const LONG savedScope = t_pd.scope, savedHost = t_fmHostSeq, savedSnOn = g_snOn;
    const DWORD savedTid = g_pdTid, savedHandle = t_pd.handle;
    memset((void*)&c, 0, sizeof(c));
    g_smMultOrig = SmtMult, g_smClassOrig = SmtClass, g_smPassOrig = SmtPass;
    g_smAll = false, g_snOn = 1;
    t_pd.open = true, t_pd.scope = 2, g_pdTid = GetCurrentThreadId();  // a sync plan of an unseen router
    t_sm.open = false;
    SmtWorld();
    LONG runs = 0, refRuns = 0, h0 = 0;
    SmtCursor a, b;

    // no pass open: the hooks are the game's functions
    SmtCursorAt(&a, 0, 5, 3), SmtCursorAt(&b, 0, 5, 3);
    c.smMode = 1;
    assert(SmtBits(SmMultHook((u8*)&a, NULL, g_smtCtx)) == SmtBits(SmtMult((u8*)&b, NULL, g_smtCtx)) && a.w10 == b.w10 && a.w1c == b.w1c);
    assert(SmClassHook((u8*)&a, NULL, g_smtCtx) == SmtClass((u8*)&b, NULL, g_smtCtx) && c.smEvals == 0 && c.smHits == 0 && c.smStores == 0);
    // mode 0: the pass runs, nothing is looked at
    c.smMode = 0;
    DWORD ref = SmtRefRun(300, 11, &refRuns);
    assert(SmtRun(300, 11, &runs) == ref && runs == refRuns && c.smPassesOff == 1 && c.smPasses == 0 && c.smEvals == 0);

    // mode 1, NPC router: the same words as the readers alone, with fewer evaluator runs
    c.smMode = 1;
    assert(SmtRun(300, 11, &runs) == ref && c.smPasses == 1 && c.smHits > 100 && runs < refRuns && runs == c.smMiss);
    // class 1 cells exist in this world and are never kept: every ask of one ran the evaluator
    assert(c.smStores + c.smClass1 + c.smFull + c.smNoCtx == c.smMiss && c.smClass1 > 0 && c.smMismatch == 0 && c.smChecks == 0);
    assert(c.smEvals == c.smHits + c.smMiss && !t_sm.open && c.smFull == 0);
    // a class word the evaluator rewrites (>= 2, not 13): the write is replayed from the record, at the same ask
    for (DWORD cls = 2; cls < 16; cls += 3) {
        *(DWORD*)(g_smtCtx + 0x4C) = cls;
        ref = SmtRefRun(400, 100 + cls, &refRuns);
        const DWORD after = SmtW(g_smtCtx, 0x4C);
        *(DWORD*)(g_smtCtx + 0x4C) = cls;
        assert(SmtRun(400, 100 + cls, &runs) == ref && SmtW(g_smtCtx, 0x4C) == after && after < cls && runs < refRuns);
    }
    *(DWORD*)(g_smtCtx + 0x4C) = 0;
    // flags above bit 23: the cursor's 24-bit key never matches, so the class ask evaluates again; a record answers it
    *(DWORD*)(g_smtCtx + 0x50) = 0x2210010;
    ref = SmtRefRun(300, 12, &refRuns);
    h0 = c.smHits;
    assert(refRuns > 450 && SmtRun(300, 12, &runs) == ref && runs * 2 < refRuns && c.smHits - h0 > 250);
    *(DWORD*)(g_smtCtx + 0x50) = 0x210010;

    // a new pass starts empty: a routing write between two passes is seen with no sequence at all
    g_snOn = 0;
    for (int j = 0; j < 64; j++) g_smtFoot[j] += 5;
    ref = SmtRefRun(300, 11, &refRuns);
    assert(SmtRun(300, 11, &runs) == ref);
    g_snOn = 1;
    // inside a pass: the write sequence moves with the write, the records go
    g_smtMid = 1;
    ref = SmtRefRun(300, 13, &refRuns);
    SmtUndoWrite();
    h0 = c.smResets;
    assert(SmtRun(300, 13, &runs) == ref && c.smResets == h0 + 1);
    // a managed call on the thread: the records go
    g_smtMid = 3;
    ref = SmtRefRun(300, 14, &refRuns);
    h0 = c.smResets;
    assert(SmtRun(300, 14, &runs) == ref && c.smResets == h0 + 1);
    g_smtMid = 0;

    // By hand inside an open pass. The hooks' frames may be inlined into this one, so the pass's stack mark is put well
    // above (open) or well below (left by an unwind) this frame instead of at one of its locals.
    {
        SmTh& t = t_sm;
        t.tab = SmThreadTab(t);
        assert(t.tab);
        SmClear(t.tab);
        t.seqWrite = SmSeqWrite(), t.seqHost = (unsigned int)t_fmHostSeq, t.serve = true, t.open = true;
        t.sp = (const void*)((const u8*)&a + 4096);
        DWORD cell = 0;
        for (;; cell++) {  // a cell that is kept (not class 1)
            assert(cell < 24);
            SmtCursorAt(&a, 0, cell, 0);
            if (SmtClass((u8*)&a, NULL, g_smtCtx) != 1) break;
        }
        LONG s0 = c.smStores;
        SmtCursorAt(&a, 0, cell, 0);
        SmMultHook((u8*)&a, NULL, g_smtCtx);
        assert(c.smStores == s0 + 1);
        h0 = c.smHits;
        SmtCursorAt(&a, 0, cell, 9), SmtCursorAt(&b, 0, cell, 9);
        assert(SmtBits(SmMultHook((u8*)&a, NULL, g_smtCtx)) == SmtBits(SmtMult((u8*)&b, NULL, g_smtCtx)) && c.smHits == h0 + 1 && c.smStores == s0 + 1);
        assert(a.w10 == b.w10 && a.w1c == b.w1c && a.w20 == b.w20);  // the garbage above bit 9 of +0x20 stays the cursor's own
        // each context word changed: no hit (the fake's class may be 1 there, so only "no hit" is asserted)
        const int offs[4] = {0x50, 0x4C, 0x54, 0x40};
        const DWORD vals[4] = {0x210011, 13, 0x6000, (DWORD)(uintptr_t)g_smtOpts[1]};
        for (int i = 0; i < 4; i++) {
            const DWORD old = SmtW(g_smtCtx, offs[i]);
            *(DWORD*)(g_smtCtx + offs[i]) = vals[i];
            SmtCursorAt(&a, 0, cell, 0);
            h0 = c.smHits;
            SmMultHook((u8*)&a, NULL, g_smtCtx);
            assert(c.smHits == h0);
            *(DWORD*)(g_smtCtx + offs[i]) = old;
        }
        *(DWORD*)(g_smtOpts[0] + 0x28) = 0x77;  // an option word of the same options object
        SmtCursorAt(&a, 0, cell, 0);
        h0 = c.smHits;
        SmMultHook((u8*)&a, NULL, g_smtCtx);
        assert(c.smHits == h0);
        *(DWORD*)(g_smtOpts[0] + 0x28) = 0;
        SmtCursorAt(&a, 0, cell, 0);
        SmMultHook((u8*)&a, NULL, g_smtCtx);
        assert(c.smHits == h0 + 1);  // the first context again: its record is still there
        // a record whose evaluation lowered the context's class word: asked again from the same class word (the game never
        // raises it inside a pass; done by hand here), the write is replayed with the cursor's words
        {
            DWORD cw = 0, clsW = 0;
            for (;; cw++) {  // a cell of class 3..11
                assert(cw < 24);
                SmtCursorAt(&a, 0, cw, 0);
                clsW = (DWORD)SmtClass((u8*)&a, NULL, g_smtCtx);
                if (clsW >= 3 && clsW < 12) break;
            }
            *(DWORD*)(g_smtCtx + 0x4C) = 12;
            SmtCursorAt(&a, 0, cw, 0);
            s0 = c.smStores;
            SmMultHook((u8*)&a, NULL, g_smtCtx);
            assert(c.smStores == s0 + 1 && SmtW(g_smtCtx, 0x4C) == clsW);
            *(DWORD*)(g_smtCtx + 0x4C) = 12;
            SmtCursorAt(&b, 0, cw, 5);
            h0 = c.smHits;
            SmMultHook((u8*)&b, NULL, g_smtCtx);
            assert(c.smHits == h0 + 1 && SmtW(g_smtCtx, 0x4C) == clsW && a.w10 == b.w10 && a.w1c == b.w1c && (u8)a.w20 == (u8)b.w20);
            // from a class word below the cell's class the evaluator takes its other branch: that record must not answer
            *(DWORD*)(g_smtCtx + 0x4C) = 2;
            SmtCursorAt(&b, 0, cw, 5);
            SmMultHook((u8*)&b, NULL, g_smtCtx);
            assert(c.smHits == h0 + 1 && SmtW(g_smtCtx, 0x4C) == 2 && a.w10 != b.w10);
            *(DWORD*)(g_smtCtx + 0x4C) = 0;
        }
        // another tree, the same node words otherwise: no hit
        SmtCursorAt(&a, 0, cell, 0);
        a.w04 = (DWORD)(uintptr_t)g_smtTree[1];
        h0 = c.smHits;
        SmMultHook((u8*)&a, NULL, g_smtCtx);
        assert(c.smHits == h0);
        // a tree with deferred edits pending: neither read nor kept
        g_smtTree[0][0x4C] = 1;
        SmtCursorAt(&a, 0, cell, 0);
        LONG d0 = c.smDirty, r0 = g_smtRuns;
        h0 = c.smHits, s0 = c.smStores;
        SmMultHook((u8*)&a, NULL, g_smtCtx);
        assert(c.smDirty == d0 + 1 && c.smHits == h0 && c.smStores == s0 && g_smtRuns == r0 + 1);
        g_smtTree[0][0x4C] = 0;
        // no context: the game's other branch, not ours
        SmtCursorAt(&a, 0, cell, 0);
        r0 = g_smtRuns;
        SmMultHook((u8*)&a, NULL, NULL);
        assert(c.smHits == h0 && g_smtRuns == r0);
        // the cursor's key already equals the flags: the function only reads the cursor, the memo is not asked
        SmtCursorAt(&a, 0, cell, 0);
        a.w1c |= SmtW(g_smtCtx, 0x50) << 8, a.w10 = 0x40490FDB;
        LONG e0 = c.smEvals;
        assert(SmtBits(SmMultHook((u8*)&a, NULL, g_smtCtx)) == 0x40490FDB && c.smEvals == e0);
        // a pass left open by an unwind: an ask that is not below its frame closes it and runs the game's function
        t.sp = (const void*)((const u8*)&a - 65536);
        SmtCursorAt(&a, 0, cell, 0);
        h0 = c.smHits, e0 = c.smEvals;
        SmMultHook((u8*)&a, NULL, g_smtCtx);
        assert(!t.open && c.smHits == h0 && c.smEvals == e0);
    }

    // shadow: every evaluation runs, the records are compared
    c.smMode = 2;
    ref = SmtRefRun(300, 15, &refRuns);
    h0 = c.smHits;
    assert(c.smChecks == 0 && SmtRun(300, 15, &runs) == ref && runs == refRuns && c.smHits == h0 && c.smChecks > 100 && c.smMismatch == 0);
    // a write the sequence did not see: shadow reports it, drops the records and goes on with the game's results
    g_smtMid = 2;
    ref = SmtRefRun(300, 16, &refRuns);
    SmtUndoWrite();
    assert(SmtRun(300, 16, &runs) == ref && runs == refRuns && c.smMismatch >= 1 && c.smLines == c.smMismatch);
    g_smtMid = 0;
    c.smMismatch = 0, c.smLines = 0;

    // scope: a household (1) or unknown (0) router's pass runs as shadow in mode 1
    c.smMode = 1;
    ref = SmtRefRun(200, 17, &refRuns);
    for (LONG scope = 0; scope < 2; scope++) {
        t_pd.scope = scope;
        h0 = c.smHits;
        const LONG sp0 = c.smScopePasses;
        assert(SmtRun(200, 17, &runs) == ref && runs == refRuns && c.smHits == h0 && c.smScopePasses == sp0 + 1);
    }
    // a seen NPC (3): served
    t_pd.scope = 3;
    h0 = c.smHits;
    assert(SmtRun(200, 17, &runs) == ref && c.smHits > h0);
    // SmoothMemoAll: a household plan with no route handle may be a command, not served
    t_pd.scope = 1, t_pd.handle = 0, g_smAll = true;
    h0 = c.smHits;
    assert(SmtRun(200, 17, &runs) == ref && c.smHits == h0 && c.smMismatch == 0);
    g_smAll = false, t_pd.scope = 2;

    // a thread with no block: the pass runs as the game's
    {
        SmTh& t = t_sm;
        SmTab* const keep = t.tab;
        t.tab = NULL, t.none = true;
        const LONG n0 = c.smNoTab, off0 = c.smPassesOff;
        assert(SmtRun(200, 17, &runs) == ref && runs == refRuns && c.smNoTab == n0 + 1 && c.smPassesOff == off0 + 1);
        t.tab = keep, t.none = false;
    }

    t_sm.open = savedMemoOpen;  // the thread keeps its block
    memcpy((void*)&c, savedC, sizeof(savedC));
    g_smMultOrig = savedM, g_smClassOrig = savedCl, g_smPassOrig = savedP;
    g_smAll = savedAll, g_snOn = savedSnOn;
    t_pd.open = savedOpen, t_pd.scope = savedScope, t_pd.handle = savedHandle, g_pdTid = savedTid, t_fmHostSeq = savedHost;
}

static void TestSmoothMemo() {
    TestSmCore();
    TestSmHooks();
    printf("smooth memo: OK (table, pass against the readers alone, class 1, class word replay, high flags, new pass, write / managed call, "
           "contexts, pending edits, unwind, shadow + mismatch, scope, no block)\n");
}
