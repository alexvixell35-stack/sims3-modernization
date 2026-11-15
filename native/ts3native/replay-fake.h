// Fake routing world for replay.cpp (docs/94 §11 design B, §16): planner P with its footprint map M and lot table, lots
// with quadtrees, tree nodes, FootprintMapEntries and footprints in the layouts replay.cpp copies, plus C stand-ins for the
// game's ApplyInsert / ApplyRemove that keep history in the tree (leaf splits, collapse, final bits cleared and never set
// back, entry order, map chain order with rehash) the way the game's do. Every allocation has a random pad, so object
// addresses differ between the live world and the clone. Included by selftest.cpp (the stand-ins for both) and
// steerro-test.cpp (the game's own ApplyInsert / ApplyRemove on the mapped image, its own world).
// shortcut: the stand-ins are not the game's algorithms; steerro-test runs the game's code on its world.

// pools.cpp helpers for the stand-ins: a game-layout pool's push, a pool object, and the site stubs called as plain functions
// (the same selection a patched pop / push site makes)
static void PlPushTo(u8* pool, void* n) { *(u8**)n = *(u8**)(pool + 0x10); *(u8**)(pool + 0x10) = (u8*)n; }
static void PlInitPool(u8* p, DWORD elem, void* alloc) {
    memset(p, 0, 0x20);
    *(DWORD*)p = elem, *(DWORD*)(p + 4) = 8, *(DWORD*)(p + 8) = 0x80;
    *(void**)(p + 0x14) = alloc, *(void**)(p + 0x18) = (void*)PlBlockFree;
}
// the stubs as plain calls (the tests' stand-ins for the game's pop / push bodies use them: same selection as a patched site)
static u8* PlPickPop(int i) { return ((u8*(__cdecl*)())kPlPopStub[i])(); }
static u8* PlPickPush(int i, void* node) {
    void* s = kPlPushStub[i];
    u8* r;
    __asm { push esi
            mov esi, node
            mov ecx, node
            call s
            pop esi
            mov r, eax }
    return r;
}

static DWORD g_rfSeed = 1;
static DWORD RfRnd(DWORD n) { g_rfSeed = g_rfSeed * 1103515245 + 12345; return ((g_rfSeed >> 8) & 0xFFFFFF) % (n ? n : 1); }
static LONG g_rfLive;  // allocations alive
static const DWORD kRfMagic = 0x4B465246;  // "FRFK" at a[-2]: RfAlloc's block (RpAlloc keeps its size there)
static DWORD g_rfPad = 1;  // 0: no pad and no draw, so the step stream does not depend on how often the clone allocates
// steerro-test K8: while set, blocks come from this arena (the live world on pages a check can make no-access); freeing one
// only clears its magic (the arena goes as a whole)
static u8* g_rfArena;
static DWORD g_rfArenaUsed, g_rfArenaSz;
// steerro-test K8 with STEERRO_ALLOCSITES=1 (docs/150 §14): EA alloc / free calls by the game's return address (calls from our
// own code are not counted)
static bool g_rfSites;
static DWORD g_rfSiteAt[1024];
static LONG g_rfSiteN[1024], g_rfSiteBytes[1024], g_rfSiteFree[1024], g_rfSiteFull;
static void RfSiteCount(DWORD at, DWORD n, bool isFree) {
    if (at < 0x00400000 || at >= 0x01400000) return;  // only the game image
    for (DWORD i = 0, h = (at * 0x9E3779B1u) >> 22; i < 1024; i++, h = (h + 1) & 1023) {
        if (g_rfSiteAt[h] && g_rfSiteAt[h] != at) continue;
        g_rfSiteAt[h] = at;
        if (isFree) g_rfSiteFree[h]++;
        else g_rfSiteN[h]++, g_rfSiteBytes[h] += (LONG)n;
        return;
    }
    g_rfSiteFull++;
}
static void* __cdecl RfAlloc(DWORD n) {
    if (g_rfArena) {
        const DWORD at = (g_rfArenaUsed + 16 + (g_rfPad ? RfRnd(16) * 16 : 0) + 15) & ~15u;
        if (at + n > g_rfArenaSz) { printf("RfAlloc: arena full\n"); abort(); }
        g_rfArenaUsed = at + n;
        u8* a = g_rfArena + at;
        ((void**)a)[-1] = NULL;
        ((DWORD*)a)[-2] = kRfMagic;
        memset(a, 0xCD, n);
        g_rfLive++;
        return a;
    }
    u8* raw = (u8*)malloc(n + 16 + 256 + 16);
    const DWORD pad = g_rfPad ? RfRnd(16) * 16 : 0;  // random addresses: the clone's objects never mirror the live ones' spacing
    u8* a = (u8*)(((uintptr_t)raw + 16 + pad + 15) & ~(uintptr_t)15);
    ((void**)a)[-1] = raw;
    ((DWORD*)a)[-2] = kRfMagic;
    memset(a, 0xCD, n);
    g_rfLive++;
    return a;
}
// stands in for EA's free too: a block that is not ours (an RpAlloc block, crash H) is counted and left alone
static LONG g_rfBadFree;
static void __cdecl RfFreeP(void* a) {
    if (g_rfSites) RfSiteCount((DWORD)(uintptr_t)_ReturnAddress(), 0, true);
    if (!a) return;
    if (((DWORD*)a)[-2] != kRfMagic) { if (!g_rfBadFree++) printf("RfFreeP: %p is not a fake EA block (not freed), from %p\n", a, _ReturnAddress()); return; }
    ((DWORD*)a)[-2] = 0;
    if (((void**)a)[-1]) free(((void**)a)[-1]);  // NULL: an arena block
    g_rfLive--;
}

// --- vtables (fastcall stand-ins for the thiscall slots replay.cpp and the stand-ins call) ---
static DWORD g_rfFmeVt[1], g_rfLeafVt[1], g_rfInnerVt[1], g_rfQVt[3], g_rfFpVt[5][0x80];  // 3 classes, the child class, one class no table knows
static void __fastcall RfFmeDtor(u8* f, void*, DWORD flags) {  // as 0x61ED10: footprint release (+0x94), pool
    u8* fp = *(u8**)(f + 8);
    if (fp) (*(LONG*)(fp + 8))--;
    if (flags & 1) RfFreeP(f);
}
static void __fastcall RfLeafDtor(u8* n, void*, DWORD flags) {  // as 0x668920: heap list, pool (push site 0x668962's choice)
    if ((n[8] & 1) && *(DWORD*)(n + 0xC)) RfFreeP((void*)(uintptr_t)(*(DWORD*)(n + 8) & ~1u));
    if (flags & 1) PlPushTo(PlPickPush(PL_LEAF, n), n);
}
static void RfNodeRel(u8* n) {
    if (n && --n[7] == 0) { n[7] = 1; ((RpDtorFn)(uintptr_t)**(DWORD**)n)(n, NULL, 1); }
}
static void __fastcall RfInnerDtor(u8* n, void*, DWORD flags) {  // as 0x628A40: children released, pool
    for (int i = 0; i < 4; i++) { u8* c = *(u8**)(n + 0xC + 4 * i); *(u8**)(n + 0xC + 4 * i) = NULL; RfNodeRel(c); }
    if (flags & 1) PlPushTo(PlPickPush(PL_INNER, n), n);
}
static DWORD __fastcall RfLevels1(const u8*, void*) { return 1; }
static DWORD __fastcall RfLevels3(const u8*, void*) { return 3; }
static LONG g_rfEnsure;
static DWORD __fastcall RfEnsure(const u8* fp, void*) {  // as 0x80E610: every level valid
    g_rfEnsure++;
    const DWORD lv = ((RpVoidFn)(uintptr_t)**(DWORD**)fp)(fp, NULL);
    for (DWORD l = 0; l < lv; l++) (*(u8**)(fp + 0x38))[l * 0x24 + 0x20] = 1;
    return 0;
}
static RpCls g_rfCls[4];  // [3]: the child class (0x28 bytes, no list, +0xC parent)
static DWORD __fastcall RfNoop(const u8*, void*) { return 0; }
static volatile DWORD g_rfFlatId;  // as the bounds slot +0x2C: a unit box, or flat bounds for this id (selftest, 0.35.14)
static const u8* volatile g_rfFarFp;  // this one footprint object answers bounds outside the unit rect (selftest, 0.35.18)
static float* __fastcall RfBounds(const u8* fp, void*, float* out, DWORD, float) {
    const bool flat = g_rfFlatId && *(const DWORD*)(fp + 0x14) == g_rfFlatId;
    const float o = fp == g_rfFarFp ? 5.f : 0.f;
    out[0] = o, out[1] = o, out[2] = o + (flat ? 0.f : 1.f), out[3] = o + 1.f;
    return out;
}
// as 0x666010's rect test (0x6660CF..0x6660FC): bounds that miss the Q's rect (the unit rect here) remove no leaf entry
static bool RfReaches(const u8* f) {
    const u8* fp = RpP(f + 8);
    float b[4];
    return !fp || ((RpBoundsFn)(uintptr_t)RpD(RpP(fp) + 0x2C))((void*)fp, NULL, b, f[0x10], 0.f)[0] < 1.5f;
}

static void RfVtInit() {
    g_rfFmeVt[0] = (DWORD)(uintptr_t)RfFmeDtor;
    g_rfLeafVt[0] = (DWORD)(uintptr_t)RfLeafDtor;
    g_rfInnerVt[0] = (DWORD)(uintptr_t)RfInnerDtor;
    for (int c = 0; c < 3; c++) {
        g_rfFpVt[c][0] = (DWORD)(uintptr_t)(c == 2 ? RfLevels3 : RfLevels1);
        g_rfFpVt[c][0x70 / 4] = (DWORD)(uintptr_t)RfEnsure;
        g_rfCls[c] = {(DWORD)(uintptr_t)g_rfFpVt[c], c == 1 ? 0x48u : 0x44u, 1, 0};
    }
    for (int c = 0; c < 3; c++) g_rfFpVt[c][0x7C / 4] = TS3A(0x00806FE0);  // id slot as the game's (compared, never called)
    g_rfFpVt[3][0] = (DWORD)(uintptr_t)RfLevels1, g_rfFpVt[3][0x70 / 4] = (DWORD)(uintptr_t)RfNoop;  // child class
    g_rfFpVt[3][0x7C / 4] = TS3A(0x006262B0);
    g_rfCls[3] = {(DWORD)(uintptr_t)g_rfFpVt[3], 0x28, 0, 1};
    for (int c = 0; c < 4; c++) g_rfFpVt[c][0x2C / 4] = (DWORD)(uintptr_t)RfBounds;
    memcpy(g_rfFpVt[4], g_rfFpVt[0], sizeof(g_rfFpVt[0]));  // a class the copy does not know
}

// --- the world ---
struct RfWorld {
    u8* world; u8* p; u8* m;
    u8* lots[16]; int nLots;
    u8* fps[256]; int nFps;
    DWORD nextKey;
};
static RfWorld g_rf;
static DWORD g_rfOneBucket[2] = {0, 0xFFFFFFFF};  // the empty map's shared bucket

static u8* RfVec(u8* at, DWORD bytes) {
    u8* b = (u8*)RfAlloc(bytes ? bytes : 4);
    *(u8**)at = b, *(u8**)(at + 4) = b + bytes, *(u8**)(at + 8) = b + bytes;
    return b;
}
static u8* RfFootprintFake(DWORD id) {
    const int c = (int)RfRnd(3);
    u8* f = (u8*)RfAlloc(0x50);
    memset(f, 0, 0x50);
    *(DWORD*)f = g_rfCls[c].vt;
    *(LONG*)(f + 8) = 1000;  // held by its object
    *(DWORD*)(f + 0x14) = id, *(DWORD*)(f + 0x18) = RfRnd(3) ? 0 : 5;
    *(u8**)(f + 0x24) = f + 0x24, *(u8**)(f + 0x28) = f + 0x24;
    *(DWORD*)(f + 0x34) = 3 + RfRnd(4);
    const DWORD lv = c == 2 ? 3 : 1;
    u8* cache = (u8*)RfAlloc(lv * 0x24);
    memset(cache, 0, lv * 0x24);
    for (DWORD l = 0; l < lv; l++) {
        const int np = 1 + (int)RfRnd(2);
        u8* polys = RfVec(cache + l * 0x24, np * 16);
        for (int i = 0; i < np; i++) {
            float* v = (float*)RfVec(polys + i * 16, 4 * 8);
            *(DWORD*)(polys + i * 16 + 12) = 0;
            for (int j = 0; j < 8; j++) v[j] = (float)RfRnd(64);
        }
        cache[l * 0x24 + 0x20] = (u8)RfRnd(2);  // some levels lazy: the copy forces them (EnsureAllPolygons)
    }
    *(u8**)(f + 0x38) = cache;
    return f;
}
static u8* RfQFake(u64 key) {
    u8* q = (u8*)RfAlloc(kRpQSz);
    memset(q, 0, kRpQSz);
    *(DWORD*)q = (DWORD)(uintptr_t)g_rfQVt;
    *(DWORD*)(q + 0x18) = (DWORD)key, *(DWORD*)(q + 0x1C) = (DWORD)(key >> 32);
    *(u8**)(q + 0x20) = g_rf.p;
    *(LONG*)(q + 0x34) = 1;
    *(float*)(q + 0x38) = 64.0f;
    return q;
}
// steerro-test builds the same world from game objects (its Q constructor, game footprint classes, identity transforms)
static u8* (*g_rfMakeQ)(u64 key) = RfQFake;
static u8* (*g_rfMakeFp)(DWORD id) = RfFootprintFake;
static bool g_rfIdentity, g_rfGame;
static DWORD g_rfOp;  // the last step's kind and lot (diagnostics)
static int g_rfOpLot;
static u8* RfFootprint(DWORD id) { return g_rfMakeFp(id); }
static u8* RfQ(u64 key) { return g_rfMakeQ(key); }
// portals (H20, docs/109 §8.9): with g_rfMakePortal set, each new lot's portal list +0x44 holds 3 counted portal objects and
// its 2 room records +0x90 hold them again in their portal vectors +4 / +0x14 (one count per element, as the game's room copy
// 0x6223C0); g_rfPortalHold is the live world's share of each count, so a portal's count must be that plus the clone's
static u8* (*g_rfMakePortal)() = NULL;
static u8* g_rfPortals[1024];
static LONG g_rfPortalHold[1024];
static int g_rfNPortals;
static void RfPortalHold(u8* pt) {
    (*(LONG*)(pt + 0x70))++;
    for (int i = 0; i < g_rfNPortals; i++) if (g_rfPortals[i] == pt) { g_rfPortalHold[i]++; return; }
    assert(g_rfNPortals < 1024);
    g_rfPortals[g_rfNPortals] = pt, g_rfPortalHold[g_rfNPortals++] = 1;
}
static void RfPortalLot(u8* g, DWORD* pv) {
    for (int i = 0; i < 3; i++) RfPortalHold((u8*)(uintptr_t)(pv[i] = (DWORD)(uintptr_t)g_rfMakePortal()));
    u8* rooms = RfVec(g + 0x90, 2 * 0x44);
    memset(rooms, 0, 2 * 0x44);
    for (int r = 0; r < 2; r++) {
        u8* rr = rooms + r * 0x44;
        DWORD* a = (DWORD*)RfVec(rr + 4, 8);
        DWORD* b = (DWORD*)RfVec(rr + 0x14, 4);
        a[0] = pv[r], a[1] = pv[2], b[0] = pv[(r + 1) % 3];
        RfPortalHold((u8*)(uintptr_t)a[0]), RfPortalHold((u8*)(uintptr_t)a[1]), RfPortalHold((u8*)(uintptr_t)b[0]);
        RfVec(rr + 0x24, 0), RfVec(rr + 0x34, 0);
    }
}
static int RfPortalCheck() {  // every portal's count = live holds + the references the clone's lot copies hold; returns the clone's
    int clone = 0;
    for (int i = 0; i < g_rfNPortals; i++) {
        LONG want = g_rfPortalHold[i];
        for (int l = 0; g_rpClone && l < g_rpClone->nLots; l++)
            for (int j = 0; j < g_rpClone->lots[l].nPrefs; j++) if (g_rpClone->lots[l].prefs[j] == g_rfPortals[i]) want++, clone++;
        if (*(LONG*)(g_rfPortals[i] + 0x70) != want) printf("portal %d: count %ld, expected %ld\n", i, *(LONG*)(g_rfPortals[i] + 0x70), want);
        assert(*(LONG*)(g_rfPortals[i] + 0x70) == want);
    }
    return clone;
}

static u8* RfLot(DWORD key) {
    u8* node = (u8*)RfAlloc(0x200);
    memset(node, 0, 0x200);
    *(DWORD*)node = key;
    u8* g = node + 0x10;
    *(DWORD*)g = key;
    *(LONG*)(g + 0x40) = 0;
    DWORD* pv = (DWORD*)RfVec(g + 0x44, 4 * 3);
    if (g_rfMakePortal) RfPortalLot(g, pv);
    else for (int i = 0; i < 3; i++) pv[i] = 0x7000 + RfRnd(100);
    RfVec(g + 0x54, 0), RfVec(g + 0x64, 0);
    const DWORD nq = 1 + RfRnd(3);
    u8** qv = (u8**)RfVec(g + 0x74, 4 * nq);
    for (DWORD i = 0; i < nq; i++) qv[i] = RfQ(key);
    g[0x8C] = 1;
    for (int i = 0; i < 32; i++) ((float*)(g + 0xA0))[i] = g_rfIdentity ? (i % 16 % 5 == 0 ? 1.0f : 0.0f) : (float)RfRnd(100);
    return node;
}
static void RfRelink() {  // lot table: 7 buckets, chain +0x190, sentinel
    DWORD* bk = *(DWORD**)(g_rf.p + 0x4C);
    memset(bk, 0, 7 * 4);
    for (int i = g_rf.nLots - 1; i >= 0; i--) {
        u8* nd = g_rf.lots[i];
        const DWORD b = *(DWORD*)nd % 7;
        *(DWORD*)(nd + 0x190) = bk[b];
        bk[b] = (DWORD)(uintptr_t)nd;
    }
}

// --- the stand-ins for ApplyInsert / ApplyRemove (also run on the clone by replay.cpp) ---
static DWORD RfRoute(const u8* f) { return RpD(RpP(f + 8) + 0x14) * 2654435761u + RpD(f + 0x10) * 97u; }
static u8* RfNewLeaf() {
    u8* n = (u8*)PlPopFrom(PlPickPop(PL_LEAF));  // as 0x667920 with its pop site redirected (pools.cpp)
    memset(n, 0, 0x14);
    *(DWORD*)n = (DWORD)(uintptr_t)g_rfLeafVt;
    *(DWORD*)(n + 4) = 0x00FFFF00;
    n[7] = 1;
    *(DWORD*)(n + 8) = 1;
    return n;
}
static void RfLeafAdd(u8* n, const u8* e) {
    const DWORD cnt = *(DWORD*)(n + 0xC);
    u8* a = (u8*)g_rpNew((cnt + 1) * 12, *g_rpNewFlags, 0, 0, 0, 0);
    if (cnt) { memcpy(a, (void*)(uintptr_t)(*(DWORD*)(n + 8) & ~1u), cnt * 12); RfFreeP((void*)(uintptr_t)(*(DWORD*)(n + 8) & ~1u)); }
    memcpy(a + cnt * 12, e, 12);
    *(DWORD*)(n + 8) = (DWORD)(uintptr_t)a | 1, *(DWORD*)(n + 0xC) = cnt + 1;
    *(DWORD*)(n + 4) &= ~(1u << (8 + (RpD(RpP(RpP(e) + 8) + 0x14) & 15)));  // final bits: cleared, never set back
}
static void RfTreeAdd(u8** slot, const u8* e, int depth) {
    if (!*slot) *slot = RfNewLeaf();
    u8* n = *slot;
    if (n[4] & 1) { RfTreeAdd((u8**)(n + 0xC + 4 * ((RfRoute(RpP(e)) >> (2 * depth)) & 3)), e, depth + 1); return; }
    RfLeafAdd(n, e);
    if (*(DWORD*)(n + 0xC) <= 3 || depth >= 3) return;
    u8* in = (u8*)PlPopFrom(PlPickPop(PL_INNER));  // split: entries go down in their order
    memset(in, 0, 0x1C);
    *(DWORD*)in = (DWORD)(uintptr_t)g_rfInnerVt;
    *(DWORD*)(in + 4) = 1;
    in[7] = 1;
    const u8* a = (const u8*)(uintptr_t)(*(DWORD*)(n + 8) & ~1u);
    for (DWORD i = 0; i < *(DWORD*)(n + 0xC); i++) RfTreeAdd((u8**)(in + 0xC + 4 * ((RfRoute(RpP(a + i * 12)) >> (2 * depth)) & 3)), a + i * 12, depth + 1);
    *slot = in;
    RfNodeRel(n);
}
static bool RfTreeDrop(u8** slot, const u8* f) {  // removes f's entries; true = subtree now empty (collapsed to NULL)
    u8* n = *slot;
    if (!n) return true;
    if (n[4] & 1) {
        bool empty = true;
        for (int i = 0; i < 4; i++) empty &= RfTreeDrop((u8**)(n + 0xC + 4 * i), f);
        if (empty) { *slot = NULL; RfNodeRel(n); }
        return empty;
    }
    u8* a = (u8*)(uintptr_t)(*(DWORD*)(n + 8) & ~1u);
    DWORD cnt = *(DWORD*)(n + 0xC), o = 0;
    for (DWORD i = 0; i < cnt; i++) if (RpP(a + i * 12) != f) memmove(a + (o++) * 12, a + i * 12, 12);
    if (o != cnt) { *(DWORD*)(n + 0xC) = o; if (!o) { RfFreeP(a); *(DWORD*)(n + 8) = 1; } }
    if (!o && !(n[4] & 2)) { *slot = NULL; RfNodeRel(n); return true; }
    return !o;
}
// 0.36.2 (I-94-508): leaf entries of Q q whose FME no node of map m holds (orphans: the game freed that FME; never read),
// and, when fp is given, the held entries whose FME's footprint is fp
static void RfLeafScan(const u8* n, int depth, const RpTab& held, const u8* fp, int* orphans, int* ofFp) {
    if (!n || depth > 40) return;
    if (n[4] & 1) { for (int i = 0; i < 4; i++) RfLeafScan(RpP(n + 0xC + 4 * i), depth + 1, held, fp, orphans, ofFp); return; }
    const u8* a = n + 8;
    DWORD cnt = 1;
    if (n[8] & 1) cnt = RpD(n + 0xC), a = (const u8*)(uintptr_t)(RpD(n + 8) & ~1u);
    for (DWORD j = 0; j < cnt; j++) {
        const DWORD f = RpD(a + j * 12);
        if (!f) continue;
        if (!RpTabFind(held, f)) (*orphans)++;
        else if (fp && RpP((const u8*)(uintptr_t)f + 8) == fp) (*ofFp)++;
    }
}
static int RfOrphans(const u8* m, const u8* q, const u8* fp = NULL, int* ofFp = NULL) {
    RpTab held = {};
    const DWORD nb = RpD(m + 8);
    const DWORD* bk = (const DWORD*)RpP(m + 4);
    for (DWORD i = 0; nb > 1 && i < nb; i++)
        for (const u8* nd = (const u8*)(uintptr_t)bk[i]; nd; nd = RpP(nd + 0x10)) RpTabPut(held, RpD(nd + 8), 1);
    int orphans = 0, dummy = 0;
    RfLeafScan(RpP(q + 0x24), 0, held, fp, &orphans, ofFp ? ofFp : &dummy);
    RpTabFree(held);
    return orphans;
}
static void RfRehash(u8* m) {  // as 0x6644E0: nodes moved bucket head first
    const DWORD nb = *(DWORD*)(m + 8), nn = nb * 2 + 1;
    DWORD* ob = *(DWORD**)(m + 4);
    DWORD* bk = (DWORD*)g_rpMapAlloc(m + 0x1C, NULL, nn * 4 + 4, 0x400000);
    memset(bk, 0, nn * 4);
    bk[nn] = 0xFFFFFFFF;
    for (DWORD i = 0; i < nb; i++)
        while (ob[i]) {
            u8* x = (u8*)(uintptr_t)ob[i];
            ob[i] = *(DWORD*)(x + 0x10);
            *(DWORD*)(x + 0x10) = bk[*(DWORD*)x % nn];
            bk[*(DWORD*)x % nn] = (DWORD)(uintptr_t)x;
        }
    if (nb > 1) g_rpMapFree(m + 0x1C, NULL, ob, nb * 4 + 4);
    *(DWORD**)(m + 4) = bk, *(DWORD*)(m + 8) = nn;
}
static void RfMapIns(u8* m, DWORD lo, DWORD hi, u8* f) {  // as 0x628220: after the first equal key, else the bucket head
    if (*(DWORD*)(m + 8) <= 1 || *(DWORD*)(m + 0xC) + 1 > *(DWORD*)(m + 8)) RfRehash(m);  // never into the shared empty bucket
    u8* nd = (u8*)g_rpMapAlloc(m + 0x1C, NULL, 0x18, 0);
    memset(nd, 0, 0x18);
    *(DWORD*)nd = lo, *(DWORD*)(nd + 4) = hi, *(u8**)(nd + 8) = f;
    (*(LONG*)(f + 4))++;
    DWORD* b = &(*(DWORD**)(m + 4))[lo % *(DWORD*)(m + 8)];
    u8* eq = NULL;
    for (u8* x = (u8*)(uintptr_t)*b; x && !eq; x = RpP(x + 0x10)) if (RpD(x) == lo && RpD(x + 4) == hi) eq = x;
    DWORD* link = eq ? (DWORD*)(eq + 0x10) : b;
    *(DWORD*)(nd + 0x10) = *link;
    *link = (DWORD)(uintptr_t)nd;
    (*(DWORD*)(m + 0xC))++;
}
static char __fastcall RfApplyInsert(u8* q, void*, u8* fp, DWORD flag) {
    u8* m = *(u8**)(*(u8**)(q + 0x20) + 0x24);
    const DWORD lo = *(DWORD*)(fp + 0x14), hi = *(DWORD*)(fp + 0x18);
    if (!flag && *(DWORD*)(m + 8) > 1)
        for (u8* x = (u8*)(uintptr_t)(*(DWORD**)(m + 4))[lo % *(DWORD*)(m + 8)]; x; x = RpP(x + 0x10))
            if (RpD(x) == lo && RpD(x + 4) == hi && RpD(RpP(x + 8) + 0xC) == (DWORD)(uintptr_t)q) return 1;  // already in this Q
    ((RpVoidFn)(uintptr_t)RpD(RpP(fp) + 0x70))(fp, NULL);
    const DWORD lv = ((RpVoidFn)(uintptr_t)**(DWORD**)fp)(fp, NULL);
    for (DWORD l = 0; l < lv; l++) {
        u8* f = (u8*)g_rpFmePool(0x14, 0, 0, 0, 0, 0);
        memset(f, 0, 0x14);
        *(DWORD*)f = (DWORD)(uintptr_t)g_rfFmeVt;
        *(LONG*)(f + 4) = 1;
        *(u8**)(f + 8) = fp, (*(LONG*)(fp + 8))++;
        *(u8**)(f + 0xC) = q;
        // as W1 0x665E7B: bit 0x200 = leaves inserted (flat bounds insert none in the game; the stand-in keeps its leaf)
        *(DWORD*)(f + 0x10) = l | (flag ? 0x400 : 0) | (g_rfFlatId && *(DWORD*)(fp + 0x14) == g_rfFlatId ? 0 : 0x200);
        if (!RpIsChild(fp)) *(DWORD*)(fp + 0x34) += 1u << 26;  // the 0x28-byte child class has no +0x34
        RfMapIns(m, lo, hi, f);
        u8 e[12];
        *(u8**)e = f, *(DWORD*)(e + 4) = lo ^ (l * 0x1111), *(DWORD*)(e + 8) = hi | l << 8;
        RfTreeAdd((u8**)(q + 0x24), e, 0);
        if (--*(LONG*)(f + 4) == 0) ((RpDtorFn)(uintptr_t)**(DWORD**)f)(f, NULL, 1);
    }
    if (!RpIsChild(fp)) for (u8* l = *(u8**)(fp + 0x24); l != fp + 0x24; l = *(u8**)l) RfApplyInsert(q, NULL, *(u8**)(l + 8), 1);
    return 1;
}
static void __fastcall RfMapNodeFree(u8* m, void*, u8* nd) {  // as 0x6281A0
    u8* f = *(u8**)(nd + 8);
    if (f && --*(LONG*)(f + 4) == 0) { *(LONG*)(f + 4) = 1; ((RpDtorFn)(uintptr_t)**(DWORD**)f)(f, NULL, 1); }
    PlPushTo(*(u8**)(m + 0x1C), nd);  // the map's node pool (the clone's is private)
}
static char __fastcall RfApplyRemove(u8* q, void*, DWORD lo0, DWORD hi0) {
    u8* m = *(u8**)(*(u8**)(q + 0x20) + 0x24);
    for (int pass = 0; pass < 2; pass++) {
        const DWORD lo = pass ? lo0 : ~lo0, hi = pass ? hi0 : ~hi0;
        if (*(DWORD*)(m + 8) <= 1) return 0;
        DWORD* at = &(*(DWORD**)(m + 4))[lo % *(DWORD*)(m + 8)];
        while (*at) {
            u8* x = (u8*)(uintptr_t)*at;
            u8* f = RpP(x + 8);
            if (RpD(x) == lo && RpD(x + 4) == hi && RpD(f + 0xC) == (DWORD)(uintptr_t)q) {
                if (RfReaches(f)) RfTreeDrop((u8**)(q + 0x24), f);
                else (*(LONG*)(f + 4))++;  // shortcut: the kept entries pin their FME (leaked; only the 0.35.18 test reaches it)
                if (!RpIsChild(RpP(f + 8))) *(DWORD*)(RpP(f + 8) + 0x34) -= 1u << 26;
                *at = *(DWORD*)(x + 0x10);
                RfMapNodeFree(m, NULL, x);
                (*(DWORD*)(m + 0xC))--;
            } else at = (DWORD*)(x + 0x10);
        }
    }
    return 1;
}
static void* __cdecl RfFmePool(DWORD, DWORD, DWORD, DWORD, DWORD, DWORD) { return RfAlloc(0x14); }
static void* __cdecl RfNew(DWORD n, DWORD, DWORD, DWORD, DWORD, DWORD) {
    if (g_rfSites) RfSiteCount((DWORD)(uintptr_t)_ReturnAddress(), n, false);
    return RfAlloc(n);
}
// as 0x624A10 / 0x624AB0 (this = M+0x1C, pool at +0): 0x18-byte nodes from the pool, other sizes from the heap
static void* __fastcall RfMapAlloc(void* a, void*, DWORD n, DWORD) { return n == 0x18 ? PlPopFrom(*(u8**)a) : RfAlloc(n); }
static void __fastcall RfMapFree(void* a, void*, void* p, DWORD n) { if (n == 0x18) PlPushTo(*(u8**)a, p); else RfFreeP(p); }
// the game's own pools in the fake world: leaf, inner (selftest: globals of its own; steerro-test: the image's 0x11CF42C /
// 0x11CF430) and the planner's map node pool (P+0x28 = M+0x1C), blocks from RfAlloc
static __declspec(align(16)) u8 g_rfPools[3][0x20];
static u8* g_rfPoolSlot[2];
static DWORD g_rfSub[4];  // M+0x20: the bucket sub-allocator (0x624A10 reads its +0 for the heap call)
static void* __cdecl RfBlock(DWORD n, DWORD) { return RfAlloc(n); }
static void RfPoolsSetup() {
    if (!g_rfGame) g_plGlobal[PL_LEAF] = (DWORD)(uintptr_t)&g_rfPoolSlot[0], g_plGlobal[PL_INNER] = (DWORD)(uintptr_t)&g_rfPoolSlot[1];
    PlInitPool(g_rfPools[0], 0x14, (void*)RfBlock), PlInitPool(g_rfPools[1], 0x1C, (void*)RfBlock), PlInitPool(g_rfPools[2], 0x18, (void*)RfBlock);
    *(u8**)(uintptr_t)g_plGlobal[PL_LEAF] = g_rfPools[0], *(u8**)(uintptr_t)g_plGlobal[PL_INNER] = g_rfPools[1];
    assert(PlRegion());
    g_plOn = 1;
}
static DWORD g_rfZero;
static LONG __fastcall RfAcq(void*, void*, const DWORD*) { return 1; }
static LONG __fastcall RfRel(void*, void*) { return 0; }
static const u8* g_rfWorldSlot;

static void RfBuild(DWORD seed) {
    g_rfSeed = seed;
    memset(&g_rf, 0, sizeof(g_rf));
    g_rfNPortals = 0;
    g_rf.world = (u8*)RfAlloc(0xA00);
    memset(g_rf.world, 0, 0xA00);
    g_rf.p = (u8*)RfAlloc(0x200);
    memset(g_rf.p, 0, 0x200);
    *(u8**)(g_rf.world + 0x95C) = g_rf.p;
    g_rf.m = (u8*)RfAlloc(0x40);
    memset(g_rf.m, 0, 0x40);
    *(DWORD**)(g_rf.m + 4) = g_rfOneBucket, *(DWORD*)(g_rf.m + 8) = 1;
    *(float*)(g_rf.m + 0x10) = 1.0f, *(float*)(g_rf.m + 0x14) = 2.0f, *(DWORD*)(g_rf.m + 0x18) = 0;
    *(u8**)(g_rf.m + 0x1C) = g_rfPools[2], *(DWORD**)(g_rf.m + 0x20) = g_rfSub, *(u8**)(g_rf.p + 0x28) = g_rfPools[2];
    *(u8**)(g_rf.p + 0x24) = g_rf.m;
    DWORD* lbk = (DWORD*)RfAlloc(8 * 4);
    lbk[7] = 0x05E117E1;
    *(DWORD**)(g_rf.p + 0x4C) = lbk, *(DWORD*)(g_rf.p + 0x50) = 7;
    g_rf.nLots = 5;
    for (int i = 0; i < g_rf.nLots; i++) g_rf.lots[i] = RfLot(0x100 + i);
    g_rf.nextKey = 0x200;
    RfRelink();
    g_rf.nFps = 60;
    for (int i = 0; i < g_rf.nFps; i++) g_rf.fps[i] = RfFootprint(0x3000 + RfRnd(45));  // ids collide on purpose
    for (int i = 0; i < 6; i++) {  // a few footprints with children (inserted by the parent's apply)
        u8* par = g_rf.fps[i];
        u8* ch = RfFootprint(~*(DWORD*)(par + 0x14));
        *(DWORD*)(ch + 0x18) = ~*(DWORD*)(par + 0x18);  // key = ~parent key (ApplyRemove's first pass)
        u8* ln = (u8*)RfAlloc(16);
        *(u8**)ln = par + 0x24, *(u8**)(ln + 4) = par + 0x24, *(u8**)(ln + 8) = ch;
        *(u8**)(par + 0x24) = ln, *(u8**)(par + 0x28) = ln;
    }
}
static u8* RfQOf(int lot, int i) { u8** qv = *(u8***)(g_rf.lots[lot] + 0x10 + 0x74); return qv[i % (*(u8***)(g_rf.lots[lot] + 0x10 + 0x78) - qv)]; }

// --- writer steps: the signal the real site gives (P1's SnEnter), then the change ---
static DWORD g_rfStk[64];
static DWORD* RfSlot() { memset(g_rfStk, 0, sizeof(g_rfStk)); return &g_rfStk[16]; }
// hooked mode (0.32.1): the stand-ins run behind a P1 stub as the patched game entries do, so every caller signals, the
// replay consumer too (the live 0.31.6 run fed its own applies back as records); RfIns / RfRem then do not signal themselves
static bool g_rfHooked;
static char __fastcall RfHookIns(u8* q, void*, u8* fp, DWORD flag) {
    DWORD stk[32] = {};
    DWORD* s = &stk[16];
    s[-2] = (DWORD)(uintptr_t)q, s[1] = (DWORD)(uintptr_t)fp, s[2] = flag, s[0] = TS3A(0x00665CEF);
    SnEnter(0, s);
    return RfApplyInsert(q, NULL, fp, flag);
}
static char __fastcall RfHookRem(u8* q, void*, DWORD lo, DWORD hi) {
    DWORD stk[32] = {};
    DWORD* s = &stk[16];
    s[-2] = (DWORD)(uintptr_t)q, s[1] = lo, s[2] = hi;
    SnEnter(1, s);
    return RfApplyRemove(q, NULL, lo, hi);
}
static void RfIns(u8* q, u8* fp, DWORD flag, bool signal) {
    DWORD* s = RfSlot();
    s[-2] = (DWORD)(uintptr_t)q, s[1] = (DWORD)(uintptr_t)fp, s[2] = flag, s[0] = TS3A(0x00665CEF);
    if (signal && !g_rfHooked) SnEnter(0, s);
    g_rpIns(q, NULL, fp, flag);
}
static void RfRem(u8* q, const u8* fp, bool signal) {
    DWORD* s = RfSlot();
    s[-2] = (DWORD)(uintptr_t)q, s[1] = *(DWORD*)(fp + 0x14), s[2] = *(DWORD*)(fp + 0x18);
    if (signal && !g_rfHooked) SnEnter(1, s);
    g_rpRem(q, NULL, s[1], s[2]);
}
// every footprint with an FME on q removed from q (through W2): a Q the game drops (replaced, lot removed) leaves no map
// node behind [inferred from BP 0.32.1: a clone that kept such nodes had 6 entries more than live at every SYNC]
static void RfRemAll(u8* q) {
    for (int guard = 0;; guard++) {
        assert(guard < 100000);  // a node RfRem cannot remove
        const u8* fp = NULL;
        u8* m = g_rf.m;
        const DWORD nb = *(DWORD*)(m + 8);
        for (DWORD i = 0; nb > 1 && i < nb && !fp; i++)
            for (const u8* nd = (const u8*)(uintptr_t)(*(DWORD**)(m + 4))[i]; nd && !fp; nd = RpP(nd + 0x10))
                if (RpP(nd + 8) && RpP(RpP(nd + 8) + 0xC) == q) fp = RpP(RpP(nd + 8) + 8);
        if (!fp) return;
        RfRem(q, fp, true);
    }
}
// LotGraph_Build 0x63CB90 (W4, arg 1 = the lot graph) on a lot a footprint change unbuilt (0x630F70 clears +0x8C): node
// bytes change (portal list here; rooms in the game), its footprint adds go through W1. 0.35.17: a shell refresh at the cut
static void RfW4(int lot) {
    u8* g = g_rf.lots[lot] + 0x10;
    g[0x8C] = 0;
    DWORD* s = RfSlot();
    s[1] = (DWORD)(uintptr_t)g;
    SnEnter(12, s);
    DWORD* pv = *(DWORD**)(g + 0x44);
    if (g_rfMakePortal) { const DWORD t = pv[0]; pv[0] = pv[2], pv[2] = t; }  // real portals: reordered, counts unchanged
    else pv[2] ^= 0x20;
    g[0x8C] = 1;
    for (int i = 0; i < 2; i++) RfIns(RfQOf(lot, RfRnd(4)), g_rf.fps[RfRnd(g_rf.nFps)], 0, true);
}
static void RfStep() {
    const int lot = (int)RfRnd(g_rf.nLots);
    u8* q = RfQOf(lot, RfRnd(4));
    switch (g_rfOp = RfRnd(15), g_rfOpLot = lot, g_rfOp) {
        case 0: case 1: case 2: case 3: case 4: RfIns(q, g_rf.fps[RfRnd(g_rf.nFps)], RfRnd(5) == 0, true); break;
        case 5: case 6: case 7: case 8: RfRem(q, g_rf.fps[RfRnd(g_rf.nFps)], true); break;
        case 9: {  // a footprint object replaced: a new footprint with the same id (address reuse is the allocator's)
            const int i = (int)RfRnd(g_rf.nFps);
            for (int l = 0; l < g_rf.nLots; l++) for (int j = 0; j < 3; j++) RfRem(RfQOf(l, j), g_rf.fps[i], true);
            g_rf.fps[i] = RfFootprint(*(DWORD*)(g_rf.fps[i] + 0x14));
            RfIns(q, g_rf.fps[i], 0, true);
            break;
        }
        case 10: {  // QuadTreeBuild 0x62F8A0 (W3; lot id args 1-2, level 3, create 4) is get-or-create: a get of an existing
            // Q changes nothing (every footprint add does one); else the level 0 Q goes (its entries through W2) and the
            // call creates a new one, filled through W1 (skipped till the rebase)
            u8* g = g_rf.lots[lot] + 0x10;
            DWORD* s = RfSlot();
            s[1] = *(DWORD*)g, s[2] = 0, s[3] = 0, s[4] = 1;
            if (RfRnd(2)) { SnEnter(2, s); break; }
            u8** qv = *(u8***)(g + 0x74);
            const u64 key = *(DWORD*)g;
            u8* old = qv[0];
            RfRemAll(old);  // the old tree's entries go with it (through W2)
            qv[0] = NULL;
            s = RfSlot();  // RfRem reused the slot buffer
            s[1] = *(DWORD*)g, s[2] = 0, s[3] = 0, s[4] = 1;
            SnEnter(2, s);
            qv[0] = RfQ(key);
            for (int i = 0; i < 4; i++) RfIns(qv[0], g_rf.fps[RfRnd(g_rf.nFps)], 0, true);
            break;
        }
        case 11: {  // a portal list change (an IC, no leaf record): found by the cut's signature compare
            u8* g = g_rf.lots[lot] + 0x10;
            DWORD* pv = *(DWORD**)(g + 0x44);
            if (g_rfMakePortal) { const DWORD t = pv[0]; pv[0] = pv[1], pv[1] = t; }  // real portals: reordered, counts unchanged
            else pv[0] ^= 0x10;
            break;
        }
        case 12: {  // a lot added (W5: the table compare finds it) or removed
            if (g_rf.nLots > 3 && RfRnd(2)) {
                u8* gone = g_rf.lots[lot];
                u8** qv = *(u8***)(gone + 0x10 + 0x74);
                for (u8** qp = qv; qp < *(u8***)(gone + 0x10 + 0x78); qp++)
                    RfRemAll(*qp);
                g_rf.lots[lot] = g_rf.lots[--g_rf.nLots];
            } else if (g_rf.nLots < 16) g_rf.lots[g_rf.nLots++] = RfLot(g_rf.nextKey++);
            RfRelink();
            break;
        }
        case 13: RfW4(lot); break;
        default: {  // lazy state no audit reads: a polygon cache invalidated (W6, not recorded)
            u8* fp = g_rf.fps[RfRnd(g_rf.nFps)];
            if (!g_rfGame) (*(u8**)(fp + 0x38))[0x20] = 0;  // game classes would rebuild from live world objects there
            break;
        }
    }
}

static void RfSetup(DWORD seed, bool game = false) {
    RfVtInit();
    g_rfGame = game;
    if (!game) {  // stand-ins; with game = true the game's functions run (steerro-test redirects their pool sites, pools.cpp)
        g_rpIns = g_rfHooked ? RfHookIns : RfApplyInsert, g_rpRem = g_rfHooked ? RfHookRem : RfApplyRemove;
        g_rpMapAlloc = RfMapAlloc, g_rpMapFree = RfMapFree, g_rpMapNodeFree = RfMapNodeFree;
        g_rpFmePool = RfFmePool, g_rpNew = RfNew, g_rpNewFlags = &g_rfZero, g_rpDel = RfFreeP;
        g_rpCls = g_rfCls, g_rpNCls = 4;
    }
    g_rpLogged = 0;
    RfPoolsSetup();
    RfBuild(seed);
    for (int i = 0; i < 120; i++) RfIns(RfQOf((int)RfRnd(g_rf.nLots), RfRnd(4)), g_rf.fps[RfRnd(g_rf.nFps)], 0, false);  // the loaded world
    g_rfWorldSlot = g_rf.world;
    g_spWorldSlot = &g_rfWorldSlot;
    g_spAcq = RfAcq, g_spRel = (SpRelFn)RfRel;
    g_spLot = &kSpLotReplay;
    g_spOn = 1;
    g_rpOn = 1, g_rpThreaded = false, g_rpAuditEvery = 1, g_rpAuditLots = 0, g_rpLogMis = 4, g_rpHave = false, g_rpNeedEpoch = 0, g_rpBad = 0;
    if (!g_rpRing) g_rpRing = (RpRec*)VirtualAlloc(NULL, kRpRing * sizeof(RpRec), MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    g_rpHead = g_rpTail = g_rpPub = 0;
}
static void RfCut() { RpCut(g_rf.world); RpDrain(false); }
static void RfTeardown() {
    RpCut(g_rf.world);  // nothing new: publishes what is left
    RpDrain(false);
    const LONG op = RpOpEnter();  // as the consumer's drop: clone work (pools.cpp counts a private push from anyone else)
    RpFreeClone(g_rpClone);
    RpOpLeave(op);
    g_rpClone = NULL;
    RpTabFree(g_rpKnownQ);
    g_rpHave = false, g_rpOn = 0, g_spOn = 0;
    g_spLot = &kSpLotNone;
    g_rpIns = (RpInsFn)(uintptr_t)TS3A(0x00665C50), g_rpRem = (RpRemFn)(uintptr_t)TS3A(0x00666750);
    g_rpCls = kRpClsGame, g_rpNCls = kRpNClsGame;
}

// pools.cpp check: every clone tree node and map node is private, every live one the game's; returns the nodes looked at
static int RfTreeOwn(const u8* n, bool priv) {
    if (!n) return 0;
    assert(PlOwns(n) == priv);
    int c = 1;
    if (n[4] & 1) for (int i = 0; i < 4; i++) c += RfTreeOwn(RpP(n + 0xC + 4 * i), priv);
    return c;
}
static int RfMapOwn(const u8* m, bool priv) {
    int c = 0;
    for (DWORD i = 0; RpD(m + 8) > 1 && i < RpD(m + 8); i++)
        for (const u8* nd = RpP(RpP(m + 4) + 4 * i); nd; nd = RpP(nd + 0x10)) { assert(PlOwns(nd) == priv); c++; }
    return c;
}
static int RfPoolCheck() {
    int c = RfMapOwn(g_rf.m, false) + (g_rpClone ? RfMapOwn(g_rpClone->m, true) : 0);
    for (int l = 0; l < g_rf.nLots; l++) {
        const u8* g = g_rf.lots[l] + 0x10;
        for (u8** q = *(u8***)(g + 0x74); q < *(u8***)(g + 0x78); q++) c += RfTreeOwn(RpP(*q + 0x24), false);
    }
    for (int i = 0; g_rpClone && i < g_rpClone->nLots; i++)
        for (int j = 0; j < g_rpClone->lots[i].nQ; j++) if (g_rpClone->lots[i].q[j]) c += RfTreeOwn(RpP(g_rpClone->lots[i].q[j] + 0x24), true);
    assert(!g_rpClone || *(u8**)(g_rpClone->m + 0x1C) == g_plPool[PL_MAP]);
    return c;
}

// diagnostics for a failed check: the first word where a live lot's tree stream and the clone's differ
static int RfStream(const u8* n, DWORD* out, int at, int cap) {
    if (at + 8 > cap) return at;
    if (!n) { out[at++] = 0xEEEEEEEE; return at; }
    out[at++] = RpD(n + 4) & 0x00FFFFFF;
    if (n[4] & 1) { for (int i = 0; i < 4; i++) at = RfStream(RpP(n + 0xC + 4 * i), out, at, cap); return at; }
    const bool heap = n[8] & 1;
    const DWORD cnt = heap ? RpD(n + 0xC) : 1;
    const u8* a = heap ? (const u8*)(uintptr_t)(RpD(n + 8) & ~1u) : n + 8;
    out[at++] = heap ? cnt : TS3A(0xC0FFEE);
    for (DWORD j = 0; j < cnt && at + 5 < cap; j++) {
        const u8* e = a + j * 12;
        const u8* f = RpP(e);
        out[at++] = RpD(e + 4), out[at++] = RpD(e + 8);
        if (f) { const u8* fp = RpP(f + 8); out[at++] = RpD(f + 0x10), out[at++] = RpD(fp + 0x14), out[at++] = RpD(fp + 0x34); }
    }
    return at;
}
static void RfDiffLot(u64 key) {
    static DWORD a[16384], b[16384];
    int na = 0, nb = 0;
    for (int i = 0; i < g_rf.nLots; i++)
        if (*(DWORD*)g_rf.lots[i] == (DWORD)key) {
            const u8* g = g_rf.lots[i] + 0x10;
            for (u8** q = *(u8***)(g + 0x74); q < *(u8***)(g + 0x78); q++) na = RfStream(RpP(*q + 0x24), a, na, 16384);
        }
    if (const RpLot* l = g_rpClone ? RpFindLot(g_rpClone, key) : NULL)
        for (int j = 0; j < l->nQ; j++) nb = RfStream(RpP(l->q[j] + 0x24), b, nb, 16384);
    int d = 0;
    while (d < na && d < nb && a[d] == b[d]) d++;
    printf("lot %lX: live %d words, clone %d, first difference at %d:", (DWORD)key, na, nb, d);
    for (int i = d < 6 ? 0 : d - 6; i < d + 8 && i < na; i++) printf(" %lX/%lX", a[i], i < nb ? b[i] : 0xBAD);
    printf("\n");
}

// the sim's side of the race (H20a/b): AddRef / Release pairs on random portals through their own vtables while the clone takes
// and drops references on the calling thread (cuts, rebases, the consumer's frees)
static volatile LONG g_rfRaceStop, g_rfRaceN;
static int g_rfRacePortals;
static DWORD WINAPI RfPortalRace(LPVOID) {
    DWORD r = 7;
    while (!g_rfRaceStop) {
        r = r * 1103515245 + 12345;
        u8* pt = g_rfPortals[(r >> 8) % g_rfRacePortals];
        ((RpRefFn)(uintptr_t)RpD(RpP(pt)))(pt, NULL);
        ((RpRefFn)(uintptr_t)RpD(RpP(pt) + 4))(pt, NULL);
        InterlockedIncrement(&g_rfRaceN);
    }
    return 0;
}
// H20b with g_rfMakePortal set: `setup` builds the world and its first clone; every lot copy holds 9 references (3 in +0x44,
// 2 rooms x 3 in +4 / +0x14); after `steps` writes with cuts (rebases, lot adds and removals) raced by the sim, every count is
// exact; after the teardown only the live holds are left. Returns the race's AddRef/Release pairs
static LONG RfPortalRefsRun(void (*setup)(DWORD), DWORD seed, int steps) {
    Ts3nCounters& k = ts3native_counters;
    g_rpPortalRefs = true;
    setup(seed);
    assert(g_rpClone && g_rpClone->nLots == g_rf.nLots);
    for (int l = 0; l < g_rpClone->nLots; l++) assert(g_rpClone->lots[l].rooms && g_rpClone->lots[l].nPrefs == 9);
    assert(RfPortalCheck() == 9 * g_rpClone->nLots && k.rpPortalRefs == 9 * g_rpClone->nLots);
    // H20 tripwire (0.31.3, docs/94 §21.8.1): the AddPortalLocationVariation slot shim (IC 5) counts a call during a clone
    // run on a portal the clone holds; not outside a run, not another portal, not an unknown handle. Count only
    {
        static u8* s_pt;
        static u8 other[0x80];
        struct V { static u8* Look(DWORD h) { return h == 0x28000001 ? s_pt : NULL; } };
        u8* (*const saved)(DWORD) = g_p4VarPortal;
        const LONG trip = k.p4VarTrip, ic5 = k.snIc[5], off = k.snIcOffSim;
        g_p4VarPortal = V::Look;
        s_pt = g_rpClone->lots[g_rpClone->nLots - 1].prefs[8];
        P4VarTrip(0x28000001);
        assert(k.p4VarTrip == trip);  // no clone run
        InterlockedExchange(&g_snapRunTid, 1);  // a clone run on another thread
        DWORD stk[8] = {0x1111, 0, 0, 0x28000001};
        SnIcEnter(5, stk);
        if (stk[0] != 0x1111) SnLeave(stk);  // on the sim thread the shim opened an IC frame
        assert(k.p4VarTrip == trip + 1 && k.snIc[5] == ic5 + 1);
        P4VarTrip(0x28000002);
        s_pt = other;
        P4VarTrip(0x28000001);
        assert(k.p4VarTrip == trip + 1);
        InterlockedExchange(&g_snapRunTid, 0);
        g_p4VarPortal = saved;
        k.p4VarTrip = trip, k.snIc[5] = ic5, k.snIcOffSim = off;
    }
    const LONG mis0 = k.rpMis, faults0 = k.rpFaults;
    g_rfRaceStop = 0, g_rfRaceN = 0, g_rfRacePortals = g_rfNPortals;
    HANDLE th = CreateThread(NULL, 0, RfPortalRace, NULL, 0, NULL);
    assert(th);
    for (int i = 0; i < steps; i++) {
        RfStep();
        if (RfRnd(3) == 0) RfCut();
    }
    RfCut();
    while (g_rfRaceN < 1000) Sleep(1);
    g_rfRaceStop = 1;
    WaitForSingleObject(th, INFINITE);
    CloseHandle(th);
    const int held = RfPortalCheck();
    assert(held == k.rpPortalRefs && held > 0 && k.rpMis == mis0 && k.rpFaults == faults0);
    RfTeardown();
    assert(RfPortalCheck() == 0 && k.rpPortalRefs == 0);
    g_rpPortalRefs = false;
    return g_rfRaceN;
}
