// docs/676 §15 smoothcut.cpp / smoothcut-core.h: [WorldPrecompute] SmoothCut. Included by selftest.cpp and
// smoothcut-host-test.cpp (the same cases on Linux, 32-bit).
// A fake quadtree laid out as the game's (tree: root node +0x24, root size +0x38, pin count +0x48; cursor: tree +0x04,
// parent +0x08, node +0x0C, size +0x14, position word +0x18, shape +0x1C, quadrant +0x20; context: class word +0x4C,
// flags +0x50), fake cell functions with the game's shapes (constructor, root set-up, final test, child step, box test,
// callback with the game's count / least / largest rules), a fake 0x667350 written as a plain recursion (not as the
// loop under test), and a fake 0x6675E0 that keeps the two end multipliers where the game's frame keeps them.
// Cases: mode 0; serve against the game's query over random segments and fans (a start sliding along a line towards a
// fixed end, as pass 3's anchor does): the same answer every time, fewer cell evaluations, hints and cuts both used;
// shadow: the game's outputs returned bit for bit, no mismatch; a forced mismatch; the class word rule (2 or more and
// not 13: the game's query runs, the word moves as in the game); no context; a tree whose root is the only cell; the
// depth stop; hints of a tree that changed since; no hints; a pinned tree (no flush); NaN and FLT_MAX multipliers.
// Preconditions set by the test itself: mode, hint count, the originals. No timer decides a result.

struct SctCursor { DWORD vt, w04, w08, w0c, w10, w14, w18, w1c, w20; };
static u8 g_sctTree[2][0x50], g_sctCtx[0x60];
static DWORD g_sctGen[2];     // per tree: what its cells are made of (a routing write changes it)
static int g_sctKind[2];      // 0 random cells, 1 the root is the only cell, 2 a corner that never ends (the depth stop)
static LONG g_sctEvals, g_sctFlushes, g_sctOrigCalls, g_sctStops;
static DWORD g_sctSeed = 424243;
static DWORD SctRnd(DWORD n) { g_sctSeed = g_sctSeed * 1103515245 + 12345; return ((g_sctSeed >> 8) & 0xFFFFFF) % n; }
static DWORD SctBits(float f) { return *(const DWORD*)&f; }
static int SctTreeOf(const u8* tree) { return tree == g_sctTree[1] ? 1 : 0; }  // never an address in an answer
static DWORD SctHash(DWORD a, DWORD b, DWORD c) {
    DWORD h = a * 2654435761u ^ b * 0x85EBCA6Bu ^ c * 0xC2B2AE35u;
    h ^= h >> 15, h *= 0x2C1B3C6Du, h ^= h >> 12, h *= 0x297A2D39u, h ^= h >> 15;
    return h;
}
static bool SctLeaf(int tree, DWORD pos) {
    const DWORD depth = pos >> 28, x = pos & 0xFFF, z = (pos >> 16) & 0xFFF;
    if (g_sctKind[tree] == 2) return !(x == 0 && z == 0);  // the corner cell is never final: a walk through it reaches level 13
    if (depth >= 6) return true;
    return depth >= 2 && SctHash(g_sctGen[tree], pos, 11) % 5 == 0;
}
// multiplier and class of a final cell
static void SctCell(int tree, DWORD pos, DWORD flags, DWORD* mult, DWORD* cls) {
    const DWORD h = SctHash(g_sctGen[tree] ^ flags * 977, pos, 5);
    *cls = 0, *mult = 0x3F800000;                               // 1.0, walkable
    if (h % 29 == 0) *cls = 10, *mult = 0x41200000;              // an obstacle: class above 1 (the callback makes it FLT_MAX)
    else if (h % 7 == 1) *cls = 1, *mult = 0x40400000;           // a dearer cell: 3.0
    else if (h % 7 == 2) *cls = 0, *mult = 0x40000000;          // 2.0 (clear between a 1.0 end and a 3.0 end, whichever is which)
    else if (h % 211 == 3) *cls = 0, *mult = 0x7FC00000;         // a NaN
    else if (h % 31 == 4) *cls = 1, *mult = SC_FLT_MAX;          // FLT_MAX with a walkable class
}

static void __fastcall SctCtor(u8* cur) {
    SctCursor* c = (SctCursor*)cur;
    c->vt = 0x0107396C, c->w04 = 0, c->w08 = 0, c->w0c = 0, c->w10 = 0, c->w14 = 0, c->w18 = 0x4000, c->w1c = 0, c->w20 = 0;
}
static void __fastcall SctRoot(u8* cur, void*, const u8* tree, unsigned int, unsigned int, unsigned int size, unsigned int, unsigned int,
                               unsigned int, unsigned int, unsigned int node, unsigned int) {
    SctCursor* c = (SctCursor*)cur;
    c->w04 = (DWORD)(uintptr_t)tree, c->w08 = 0, c->w0c = node, c->w14 = size, c->w18 = 0, c->w1c = 0, c->w20 &= ~0x300u;
}
static unsigned int __fastcall SctFinal(const u8* cur) {
    const SctCursor* c = (const SctCursor*)cur;
    return c->w0c == 0 || (c->w1c & 0xFF) >= 0x15 ? 0xAB01u : 0xAB00u;  // only al counts
}
static void __fastcall SctDown(u8* parent, void*, unsigned int q, u8* out) {
    const SctCursor* p = (const SctCursor*)parent;
    SctCursor* c = (SctCursor*)out;
    const int tree = SctTreeOf((const u8*)(uintptr_t)p->w04);
    const DWORD x = ((p->w18 & 0xFFF) * 2 + (q & 1)) & 0xFFF, z = (((p->w18 >> 16) & 0xFFF) * 2 + ((q >> 1) & 1)) & 0xFFF;
    c->w18 = x | z << 16 | ((p->w18 >> 28) + 1) << 28;
    c->w14 = SctBits(ScF(p->w14) * 0.5f);
    c->w04 = p->w04, c->w08 = p->w0c;
    c->w0c = SctLeaf(tree, c->w18) ? 0 : p->w0c * 4 + q;
    c->w1c = 0, c->w20 = (c->w20 & ~0x300u) | q << 8;
}
// does the segment, grown by the radius, touch the cell's square
static unsigned int __fastcall SctBox(u8* cur, void*, unsigned int x0b, unsigned int z0b, unsigned int x1b, unsigned int z1b, unsigned int rb) {
    const SctCursor* c = (const SctCursor*)cur;
    const float s = ScF(c->w14), r = ScF(rb);
    const float lox = (float)(c->w18 & 0xFFF) * s - r, loz = (float)((c->w18 >> 16) & 0xFFF) * s - r, hix = lox + s + 2 * r, hiz = loz + s + 2 * r;
    const float x0 = ScF(x0b), z0 = ScF(z0b), dx = ScF(x1b) - x0, dz = ScF(z1b) - z0;
    float t0 = 0.0f, t1 = 1.0f;
    const float o[2] = {x0, z0}, d[2] = {dx, dz}, lo[2] = {lox, loz}, hi[2] = {hix, hiz};
    for (int a = 0; a < 2; a++) {
        if (d[a] == 0.0f) {
            if (o[a] < lo[a] || o[a] > hi[a]) return 0x5500u;
            continue;
        }
        float ta = (lo[a] - o[a]) / d[a], tb = (hi[a] - o[a]) / d[a];
        if (ta > tb) { const float t = ta; ta = tb, tb = t; }
        if (ta > t0) t0 = ta;
        if (tb < t1) t1 = tb;
        if (t0 > t1) return 0x5500u;
    }
    return 0x5501u;
}
// 0x662D30: the cell's multiplier, FLT_MAX when the flag byte is 0 and the class is above 1; count, least, largest
static unsigned int __fastcall SctCb(u8* tree, void*, u8* ctx, u8* cur, void* state) {
    const SctCursor* c = (const SctCursor*)cur;
    ScState* s = (ScState*)state;
    DWORD mult = 0, cls = 0;
    g_sctEvals++;
    SctCell(SctTreeOf(tree), c->w18, ctx ? ScRd(ctx, 0x50) : 0, &mult, &cls);
    if (ctx) {  // the evaluator's one lasting write (docs/98 §5.3 step 1)
        const DWORD xc = ScRd(ctx, 0x4C);
        if (!(xc < 2 || xc == 13 || (LONG)xc < (LONG)cls)) *(DWORD*)(ctx + 0x4C) = cls, mult ^= 0x00100000;
    }
    if (!(s->flag & 0xFF) && cls > 1) mult = SC_FLT_MAX;
    const float v = ScF(mult);
    if (++s->count == 1) { s->minBits = mult, s->maxBits = mult; return 0x7701u; }
    if (ScF(s->minBits) > v) s->minBits = mult;
    if (v > ScF(s->maxBits)) s->maxBits = mult;
    return 0x7701u;
}
static const ScFns kSctFns = {SctCtor, SctRoot, SctFinal, SctDown, SctBox, SctCb};
static void __fastcall SctFlush(u8*) { g_sctFlushes++; }

// the game's walk as a recursion: every child in order, the depth stop ends everything
static bool SctRefVisit(u8* tree, u8* ctx, const ScSeg& g, SctCursor* parent, unsigned int lvl, ScState* s) {
    for (unsigned int q = 0; q < 4; q++) {
        SctCursor c;
        SctCtor((u8*)&c);
        SctDown((u8*)parent, NULL, q, (u8*)&c);
        if (!(SctBox((u8*)&c, NULL, g.x0, g.z0, g.x1, g.z1, g.radius) & 0xFF)) continue;
        if (SctFinal((const u8*)&c) & 0xFF) { SctCb(tree, NULL, ctx, (u8*)&c, s); continue; }
        if (lvl >= 13) { g_sctStops++; return false; }
        if (!SctRefVisit(tree, ctx, g, &c, lvl + 1, s)) return false;
    }
    return true;
}
// 0x667350
static DWORD __fastcall SctQueryOrig(u8* tree, void*, u8* ctx, DWORD x0, DWORD z0, DWORD x1, DWORD z1, DWORD radius, DWORD* outMin, DWORD* outMax,
                                     DWORD flag) {
    g_sctOrigCalls++;
    LONG* const pin = (LONG*)(tree + 0x48);
    if (*pin < 1) SctFlush(tree);
    ++*pin;
    *outMin = SC_FLT_MAX, *outMax = SC_FLT_MAX;
    ScState s = {0, 0x0BADF00D, 0x0BADF00D, flag & 0xFF};  // the game leaves least and largest unset until the first cell
    const ScSeg g = {x0, z0, x1, z1, radius};
    SctCursor root;
    SctCtor((u8*)&root);
    SctRoot((u8*)&root, NULL, tree, 0, 0, ScRd(tree, 0x38), 0, 0, ScRd(tree, 0x44), 0, ScRd(tree, 0x24), 0);
    if (SctFinal((const u8*)&root) & 0xFF) {
        if (SctBox((u8*)&root, NULL, x0, z0, x1, z1, radius) & 0xFF) SctCb(tree, NULL, ctx, (u8*)&root, &s);
    } else {
        SctRefVisit(tree, ctx, g, &root, 1, &s);
    }
    *outMin = s.minBits, *outMax = s.maxBits;
    --*pin;
    return s.count > 0 ? 1u : 0u;
}
static DWORD g_sctLieMax;  // the forced mismatch: an "original" that saw one cell of this value
static DWORD __fastcall SctQueryLie(u8*, void*, u8*, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD* outMin, DWORD* outMax, DWORD) {
    *outMin = g_sctLieMax, *outMax = g_sctLieMax;
    return 1;
}

// the multiplier of the final cell that holds a point (0x663900 + 0x66B670)
static DWORD SctPointMult(u8* tree, u8* ctx, float x, float z) {
    SctCursor c[16];
    SctCtor((u8*)&c[0]);
    SctRoot((u8*)&c[0], NULL, tree, 0, 0, ScRd(tree, 0x38), 0, 0, ScRd(tree, 0x44), 0, ScRd(tree, 0x24), 0);
    int k = 0;
    while (!(SctFinal((const u8*)&c[k]) & 0xFF) && k < 14) {
        const float half = ScF(c[k].w14) * 0.5f;
        const float cx = (float)(c[k].w18 & 0xFFF) * ScF(c[k].w14) + half, cz = (float)((c[k].w18 >> 16) & 0xFFF) * ScF(c[k].w14) + half;
        SctCtor((u8*)&c[k + 1]);
        SctDown((u8*)&c[k], NULL, (x >= cx ? 1u : 0u) | (z >= cz ? 2u : 0u), (u8*)&c[k + 1]);
        k++;
    }
    DWORD mult = 0, cls = 0;
    SctCell(SctTreeOf(tree), c[k].w18, ctx ? ScRd(ctx, 0x50) : 0, &mult, &cls);
    return mult;
}

struct SctAnswer { int verdict; DWORD al, minBits, maxBits; };  // verdict: 0 blocked, 1 clear, 2 an end cell is FLT_MAX (no query)
// 0x6675E0 around `query`: m0 at frame +0x58, m1 at +0x08, least to +0x64, largest to +0x68
static SctAnswer SctLotTest(ScQueryFn query, u8* tree, u8* ctx, float x0, float z0, float x1, float z1) {
    SctAnswer a = {2, 0, 0, 0};
    DWORD frame[0x70 / 4];
    for (int i = 0; i < 0x70 / 4; i++) frame[i] = 0xCCCCCCCC;
    const DWORD m0 = SctPointMult(tree, ctx, x0, z0), m1 = SctPointMult(tree, ctx, x1, z1);
    if (m0 == SC_FLT_MAX || m1 == SC_FLT_MAX) return a;
    frame[0x58 / 4] = m0, frame[0x08 / 4] = m1;
    a.al = query(tree, NULL, ctx, SctBits(x0), SctBits(z0), SctBits(x1), SctBits(z1), SctBits(0.25f), &frame[0x64 / 4], &frame[0x68 / 4], 0);
    a.minBits = frame[0x64 / 4], a.maxBits = frame[0x68 / 4];
    assert(frame[0x58 / 4] == m0 && frame[0x08 / 4] == m1 && frame[0x60 / 4] == 0xCCCCCCCC && frame[0x6C / 4] == 0xCCCCCCCC);
    a.verdict = (a.al & 0xFF) && (ScF(m0) >= ScF(a.maxBits) || ScF(m1) >= ScF(a.maxBits)) ? 1 : 0;
    return a;
}

static void SctWorld() {
    memset(g_sctTree, 0, sizeof(g_sctTree));
    memset(g_sctCtx, 0, sizeof(g_sctCtx));
    for (int t = 0; t < 2; t++) {
        *(DWORD*)(g_sctTree[t] + 0x24) = 1;  // the root node
        *(DWORD*)(g_sctTree[t] + 0x38) = SctBits(64.0f);
        g_sctGen[t] = 7 + (DWORD)t, g_sctKind[t] = 0;
    }
    *(DWORD*)(g_sctCtx + 0x50) = 0x21;
    memset(&t_scHints, 0, sizeof(t_scHints));
}
static float SctCoord() { return (float)SctRnd(6300) * 0.01f + 0.5f; }

// n queries through the hook and through the game's query alone, each on its own copy of the context. Returns how many
// answers differ (verdict; in shadow also the outputs). Every third run of 8 is a fan: the start slides, the end stays.
static int SctCompare(int n, int tree, LONG* clear, LONG* blocked) {
    int diff = 0;
    float x0 = 0, z0 = 0, x1 = 0, z1 = 0, sx = 0, sz = 0;
    u8 ctxRef[sizeof(g_sctCtx)];
    memcpy(ctxRef, g_sctCtx, sizeof(ctxRef));
    for (int i = 0; i < n; i++) {
        const bool fan = (i / 8) % 3 != 0;
        if (!fan || i % 8 == 0) {
            x0 = SctCoord(), z0 = SctCoord();
            const float len = fan ? 30.0f : (float)(3 + SctRnd(30));
            x1 = x0 + ((float)SctRnd(2001) * 0.001f - 1.0f) * len, z1 = z0 + ((float)SctRnd(2001) * 0.001f - 1.0f) * len;
            if (x1 < 0.5f) x1 = 0.5f;
            if (x1 > 63.5f) x1 = 63.5f;
            if (z1 < 0.5f) z1 = 0.5f;
            if (z1 > 63.5f) z1 = 63.5f;
            sx = (x1 - x0) / 12.0f, sz = (z1 - z0) / 12.0f;
        } else {
            x0 += sx, z0 += sz;  // the anchor's next sample
        }
        const SctAnswer ref = SctLotTest(SctQueryOrig, g_sctTree[tree], ctxRef, x0, z0, x1, z1);
        const SctAnswer got = SctLotTest(ScQueryHook, g_sctTree[tree], g_sctCtx, x0, z0, x1, z1);
        if (got.verdict != ref.verdict) diff++;
        if (ts3native_smoothcut.scMode != 1 && (got.al != ref.al || (ref.al && (got.minBits != ref.minBits || got.maxBits != ref.maxBits)))) diff++;
        if (ScRd(ctxRef, 0x4C) != ScRd(g_sctCtx, 0x4C)) diff++;
        if (ref.verdict == 1) ++*clear;
        if (ref.verdict == 0) ++*blocked;
    }
    return diff;
}

static void TestSmoothCut() {
    SmoothCutCounters& c = ts3native_smoothcut;
    memset((void*)&c, 0, sizeof(c));
    g_scQueryOrig = SctQueryOrig, g_scFlush = SctFlush, g_scFns = kSctFns, g_scHintN = SC_HINTS;
    SctWorld();
    LONG clear = 0, blocked = 0;

    // the hint list: newest first, a repeat moves up, the oldest drops out
    {
        ScHints hs;
        memset(&hs, 0, sizeof(hs));
        for (unsigned int i = 1; i <= 5; i++) ScHintAdd(&hs, g_sctTree[0], i, 3);
        assert(hs.h[0].path == 5 && hs.h[3].path == 2);
        ScHintAdd(&hs, g_sctTree[0], 3, 3);
        assert(hs.h[0].path == 3 && hs.h[1].path == 5 && hs.h[2].path == 4 && hs.h[3].path == 2);
        ScHintAdd(&hs, g_sctTree[1], 3, 3);  // another tree is another cell
        assert(hs.h[0].tree == g_sctTree[1] && hs.h[1].path == 3 && hs.h[3].path == 4);
    }

    // mode 0: the game's query, nothing looked at
    c.scMode = 0;
    assert(SctCompare(200, 0, &clear, &blocked) == 0 && c.scOff > 0 && c.scQueries == 0 && c.scNoCut == 0);

    // serve: the same answer for every segment, with fewer evaluations
    c.scMode = 1;
    clear = 0, blocked = 0;
    g_sctEvals = 0, g_sctOrigCalls = 0, g_sctFlushes = 0;
    {
        // the game's share of the evaluations: run the reference alone first on the same random stream
        const DWORD seed = g_sctSeed;
        c.scMode = 0;
        const LONG off0 = c.scOff;
        assert(SctCompare(4000, 0, &clear, &blocked) == 0);
        const LONG refEvals = g_sctEvals / 2;  // mode 0 runs the game's query on both sides
        const LONG asked = (c.scOff - off0);
        g_sctSeed = seed, c.scMode = 1, clear = 0, blocked = 0, g_sctEvals = 0, g_sctOrigCalls = 0, g_sctFlushes = 0;
        assert(SctCompare(4000, 0, &clear, &blocked) == 0);
        assert(clear > 100 && blocked > 100);
        assert(c.scQueries == asked && c.scQueries == clear + blocked);
        assert(c.scHintHit > 50 && c.scCut > 50 && c.scWholeClear == clear && c.scHintHit + c.scCut + c.scWholeBlocked == blocked);
        assert(g_sctOrigCalls == c.scQueries);           // the reference side only
        assert(g_sctFlushes == 2 * c.scQueries);         // one flush a query on each side: the tree is not pinned
        assert(g_sctEvals - refEvals < refEvals * 9 / 10);  // ours evaluated clearly fewer cells than the game's
        assert(c.scCells + c.scHintCells == g_sctEvals - refEvals);
        assert(*(LONG*)(g_sctTree[0] + 0x48) == 0 && c.scChecks == 0 && c.scMismatch == 0);
    }

    // shadow: the game's outputs come back bit for bit, every query is compared, nothing differs
    c.scMode = 2;
    {
        const LONG q0 = c.scQueries;
        assert(SctCompare(3000, 0, &clear, &blocked) == 0);
        assert(c.scChecks == c.scQueries - q0 && c.scChecks > 1000 && c.scMismatch == 0 && c.scLines == 0);
    }
    // a forced mismatch: an "original" that calls a blocked segment clear, then one that saw no cell
    {
        memset(&t_scHints, 0, sizeof(t_scHints));
        g_scQueryOrig = SctQueryLie, g_sctLieMax = 0x3F800000;
        const LONG m = c.scMismatch;
        LONG cuts = c.scCut + c.scHintHit, whole = c.scWholeClear + c.scWholeBlocked;
        for (int i = 0; i < 400; i++) SctLotTest(ScQueryHook, g_sctTree[0], g_sctCtx, SctCoord(), SctCoord(), SctCoord(), SctCoord());
        cuts = c.scCut + c.scHintHit - cuts, whole = c.scWholeClear + c.scWholeBlocked - whole;
        assert(cuts > 0 && whole > 0 && c.scMismatch - m >= cuts && c.scLines > 0 && c.scLines <= c.scMismatch);
        g_scQueryOrig = SctQueryOrig;
    }
    memset((void*)&c, 0, sizeof(c));

    // the class word rule: 2 or more and not 13 goes to the game untouched (the word moves there); 0, 1 and 13 are ours
    c.scMode = 1;
    *(DWORD*)(g_sctCtx + 0x4C) = 12;
    {
        float x = 2.0f;
        SctAnswer a = {2, 0, 0, 0};
        while (a.verdict == 2 && x < 60.0f) a = SctLotTest(ScQueryHook, g_sctTree[0], g_sctCtx, x, 3.0f, x + 2.0f, 40.0f), x += 1.0f;
        assert(a.verdict != 2 && c.scNoCut == 1 && c.scQueries == 0);
    }
    *(DWORD*)(g_sctCtx + 0x4C) = 12;
    assert(SctCompare(300, 0, &clear, &blocked) == 0 && c.scNoCut > 1);
    assert(ScRd(g_sctCtx, 0x4C) < 12);  // the fake evaluator lowered it, on both sides alike (SctCompare); from 0 or 1 on the queries are ours
    *(DWORD*)(g_sctCtx + 0x4C) = 13;
    assert(SctCompare(300, 0, &clear, &blocked) == 0 && c.scQueries > 0 && ScRd(g_sctCtx, 0x4C) == 13);
    *(DWORD*)(g_sctCtx + 0x4C) = 1;
    {
        const LONG q0 = c.scQueries;
        assert(SctCompare(300, 0, &clear, &blocked) == 0 && c.scQueries > q0 && ScRd(g_sctCtx, 0x4C) == 1);
    }
    *(DWORD*)(g_sctCtx + 0x4C) = 0;
    // no context: the game's
    {
        const LONG n0 = c.scNoCut, q0 = c.scQueries;
        const SctAnswer a = SctLotTest(ScQueryHook, g_sctTree[0], NULL, 3.0f, 3.0f, 40.0f, 41.0f);
        const SctAnswer b = SctLotTest(SctQueryOrig, g_sctTree[0], NULL, 3.0f, 3.0f, 40.0f, 41.0f);
        assert(a.verdict == b.verdict && a.al == b.al && (a.verdict == 2 || (c.scNoCut == n0 + 1 && c.scQueries == q0)));
    }

    // other flags are other cells: still the game's answers
    *(DWORD*)(g_sctCtx + 0x50) = 0x200021;
    assert(SctCompare(1000, 0, &clear, &blocked) == 0);
    *(DWORD*)(g_sctCtx + 0x50) = 0x21;

    // hints of a tree that changed since (a routing write): they are child numbers only, the answers stay the game's
    for (int round = 0; round < 6; round++) {
        g_sctGen[0] += 1000;
        assert(SctCompare(500, 0, &clear, &blocked) == 0);
    }
    // two trees in turn
    for (int round = 0; round < 6; round++) assert(SctCompare(64, round & 1, &clear, &blocked) == 0);

    // no hints: cuts only
    {
        g_scHintN = 0;
        memset(&t_scHints, 0, sizeof(t_scHints));
        const LONG h0 = c.scHintHit, t0 = c.scHintTries, k0 = c.scCut;
        assert(SctCompare(1000, 0, &clear, &blocked) == 0 && c.scHintHit == h0 && c.scHintTries == t0 && c.scCut > k0);
        g_scHintN = SC_HINTS;
    }

    // a pinned tree: no flush, the pin count comes back
    {
        *(LONG*)(g_sctTree[0] + 0x48) = 1;
        g_sctFlushes = 0;
        assert(SctCompare(200, 0, &clear, &blocked) == 0 && g_sctFlushes == 0 && *(LONG*)(g_sctTree[0] + 0x48) == 1);
        *(LONG*)(g_sctTree[0] + 0x48) = 0;
    }

    // a tree whose root is the only cell
    g_sctKind[1] = 1, *(DWORD*)(g_sctTree[1] + 0x24) = 0;
    c.scMode = 2;
    assert(SctCompare(100, 1, &clear, &blocked) == 0 && c.scMismatch == 0);
    c.scMode = 1;
    assert(SctCompare(100, 1, &clear, &blocked) == 0);

    // the depth stop: a corner cell that is never final ends the game's walk at level 13; ours ends there too
    g_sctKind[1] = 2, *(DWORD*)(g_sctTree[1] + 0x24) = 1;
    {
        c.scMode = 2;
        const LONG k0 = c.scChecks;
        int diff = 0;
        g_sctStops = 0;
        for (int i = 0; i < 200; i++) {
            const float z1 = 0.001f + (float)SctRnd(60) * 0.003f;  // within the radius of the corner
            u8 ctxRef[sizeof(g_sctCtx)];
            memcpy(ctxRef, g_sctCtx, sizeof(ctxRef));
            const SctAnswer ref = SctLotTest(SctQueryOrig, g_sctTree[1], ctxRef, 40.0f, 0.001f, 0.001f, z1);
            const SctAnswer got = SctLotTest(ScQueryHook, g_sctTree[1], g_sctCtx, 40.0f, 0.001f, 0.001f, z1);
            if (ref.verdict != got.verdict || ref.al != got.al || (ref.al && (ref.minBits != got.minBits || ref.maxBits != got.maxBits))) diff++;
        }
        assert(diff == 0 && c.scChecks > k0 && c.scMismatch == 0 && g_sctStops > 0);
    }
    assert(c.scMismatch == 0 && c.scLines == 0);
    c.scMode = 0;
    g_sctKind[1] = 0;
}
