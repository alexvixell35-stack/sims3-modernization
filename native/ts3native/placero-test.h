// docs/670 placero.cpp: [PlaceTestRO]. Included by selftest.cpp (both tests) and placero-host-test.cpp (TestPlaceCore).
// TestPlaceCore (placero-core.h): the type rule against the list read from the game's switch; PtWalk against a recursive
// walk of the same fake quadtree (same cells, same order) for many polygons, the visitor's stop, a root-only tree, the
// 13-deep case. The fakes follow the game's iterator rules: terminal = no node or a sub-cell code >= 0x15; a child halves
// the cell size and doubles the cell position.
// TestPlaceTestRO: the call-site stub on fakes (the game's 0xB41120, the routing world and the gather replaced): pass-through
// rules, the clone's answer from fake footprints (own id skipped, blocking types, the two flags), fallbacks, shadow compare.

namespace ptt {
struct Node { Node* ch[4]; bool leaf; };
struct Rect { float x0, y0, x1, y1; };
struct Seen { DWORD pos; float size; const u8* node; };
enum { MAXSEEN = 4096, MAXNODES = 4096 };
static Node g_nodes[MAXNODES];
static int g_nNodes;
static Seen g_walk[MAXSEEN], g_ref[MAXSEEN];
static int g_nWalk, g_nRef, g_stopAt, g_rootLevel;
static unsigned g_ptRnd;
static unsigned PtRnd() { return (g_ptRnd = g_ptRnd * 1664525u + 1013904223u) >> 8; }

static void __fastcall Ctor(PtIter* it) {
    memset(it, 0, sizeof(*it));
    it->vt = 0x669BE0;
}
static void __fastcall Root(PtIter* it, void*, const u8* q, DWORD parent, DWORD child, float size, DWORD x, DWORD y, DWORD level, DWORD z,
                            const u8* node, DWORD code) {
    assert(it->vt == 0x669BE0 && !parent && !child && !x && !y && !z && !code);
    g_rootLevel = (int)level;
    it->q = q, it->node = node, it->size = size, it->pos = 0, it->code = 0;
}
static DWORD __fastcall Term(const PtIter* it) { return it->node && (it->code & 0xFF) < 0x15 ? 0xAB00 : 0xAB01; }  // the game sets eax, callers read al
static void __fastcall Child(PtIter* it, void*, DWORD idx, PtIter* out) {
    const Node* n = (const Node*)it->node;
    assert(n && idx < 4 && out != it && out->vt == 0x669BE0);
    out->q = it->q, out->parent = it->node, out->node = (const u8*)n->ch[idx];
    out->size = it->size * 0.5f;
    out->pos = ((it->pos & 0xFFF) * 2 + (idx & 1)) | ((it->pos >> 16 & 0xFFF) * 2 + (idx >> 1)) << 16;
    out->code = n->ch[idx] && n->ch[idx]->leaf ? 0x15 : 0;
    out->w20 = idx << 8;
}
static const PtWalkFns kFns = {Ctor, Root, Term, Child};
static bool Clip(const PtIter* it, const void* shape) {
    const Rect& r = *(const Rect*)shape;
    const float x0 = (float)(it->pos & 0xFFF) * it->size, y0 = (float)(it->pos >> 16 & 0xFFF) * it->size;
    return x0 < r.x1 && r.x0 < x0 + it->size && y0 < r.y1 && r.y0 < y0 + it->size;
}
static bool Visit(void* ctx, const PtIter* it) {
    assert(ctx == (void*)g_walk && g_nWalk < MAXSEEN);
    g_walk[g_nWalk].pos = it->pos, g_walk[g_nWalk].size = it->size, g_walk[g_nWalk].node = it->node;
    return ++g_nWalk != g_stopAt;
}
// the same walk, written as the recursion the game's loop unrolls
static void RefNote(const PtIter& it) {
    assert(g_nRef < MAXSEEN);
    g_ref[g_nRef].pos = it.pos, g_ref[g_nRef].size = it.size, g_ref[g_nRef].node = it.node;
    g_nRef++;
}
static void RefRec(PtIter& cur, const Rect& r) {
    for (DWORD i = 0; i < 4; i++) {
        PtIter ch;
        Ctor(&ch);
        Child(&cur, NULL, i, &ch);
        if (!Clip(&ch, &r)) continue;
        if ((u8)Term(&ch)) RefNote(ch);
        else RefRec(ch, r);
    }
}
static void Ref(const u8* q, const Rect& r) {
    PtIter root;
    Ctor(&root);
    Root(&root, NULL, q, 0, 0, *(const float*)(q + 0x38), 0, 0, *(const DWORD*)(q + 0x44), 0, *(const u8* const*)(q + 0x24), 0);
    g_nRef = 0;
    if ((u8)Term(&root)) { if (Clip(&root, &r)) RefNote(root); return; }
    RefRec(root, r);
}
static Node* Build(int depth) {  // an inner node: each child absent, a leaf or (above the depth limit) another inner node
    assert(g_nNodes < MAXNODES);
    Node* n = &g_nodes[g_nNodes++];
    memset(n, 0, sizeof(*n));
    for (int i = 0; i < 4; i++) {
        const unsigned k = PtRnd() % 10;
        if (k < 2) continue;
        if (k < 6 || depth >= 5) {
            if (g_nNodes == MAXNODES) continue;
            Node* l = &g_nodes[g_nNodes++];
            memset(l, 0, sizeof(*l));
            l->leaf = true;
            n->ch[i] = l;
        } else if (g_nNodes < MAXNODES - 64) n->ch[i] = Build(depth + 1);
    }
    return n;
}
static void SetQ(u8* q, const Node* root) {
    memset(q, 0, 0x50);
    *(float*)(q + 0x38) = 64.0f;
    *(DWORD*)(q + 0x44) = 7;
    *(const Node**)(q + 0x24) = root;
}
static bool Same() {
    if (g_nWalk != g_nRef) return false;
    for (int i = 0; i < g_nRef; i++)
        if (g_walk[i].pos != g_ref[i].pos || g_walk[i].size != g_ref[i].size || g_walk[i].node != g_ref[i].node) return false;
    return true;
}
}  // namespace ptt

static void TestPlaceCore() {
    using namespace ptt;
    // the type rule: 0xB41120's switch as read from its tables
    for (int t = -3; t < 0x30; t++) {
        const bool always = t == 2 || t == 4 || t == 9 || t == 0xC || t == 0xD || t == 0x11 || t == 0x15 || t == 0x17;
        for (int f = 0; f < 4; f++) {
            const bool world = (f & 1) != 0, rec = (f & 2) != 0;
            const bool want = always || (t == 0x12 && world) || ((t == 0x13 || t == 0x18) && !rec);
            assert(PtBlocks(t, world, rec) == want);
        }
    }
    static u8 q[0x50];
    // random trees, random polygons: the same cells in the same order as the recursion
    g_ptRnd = 670;
    int visits = 0;
    float smallest = 64.0f;
    for (int tree = 0; tree < 24; tree++) {
        g_nNodes = 0;
        SetQ(q, Build(0));
        for (int i = 0; i < 40; i++) {
            Rect r;
            r.x0 = (float)(PtRnd() % 6400) / 100.0f, r.y0 = (float)(PtRnd() % 6400) / 100.0f;
            r.x1 = r.x0 + (float)(PtRnd() % 3200) / 100.0f, r.y1 = r.y0 + (float)(PtRnd() % 3200) / 100.0f;
            if (i == 0) r.x0 = r.y0 = -1.0f, r.x1 = r.y1 = 65.0f;  // the whole tree
            if (i == 1) r.x0 = r.y0 = 100.0f, r.x1 = r.y1 = 101.0f;  // outside it
            Ref(q, r);
            g_nWalk = 0, g_stopAt = 0, g_rootLevel = 0;
            assert(PtWalk(kFns, q, &r, Clip, Visit, g_walk) == PT_WALK_OK && g_rootLevel == 7 && Same());
            assert(i != 1 || g_nRef == 0);
            visits += g_nRef;
            for (int k = 0; k < g_nRef; k++)
                if (g_ref[k].size < smallest) smallest = g_ref[k].size;
            if (g_nRef >= 3) {  // the visitor's 0 ends the walk right there
                g_nWalk = 0, g_stopAt = 2;
                assert(PtWalk(kFns, q, &r, Clip, Visit, g_walk) == PT_WALK_OK && g_nWalk == 2);
                g_nRef = 2;
                assert(Same());
            }
        }
    }
    assert(visits > 2000 && smallest <= 2.0f);  // cells five and more levels down were reached
    // a tree that is one terminal root: visited once when the polygon overlaps it (the visitor's answer is not read)
    const Rect all = {-1.0f, -1.0f, 65.0f, 65.0f}, none = {100.0f, 100.0f, 101.0f, 101.0f};
    SetQ(q, NULL);
    g_nWalk = 0, g_stopAt = 1;
    assert(PtWalk(kFns, q, &all, Clip, Visit, g_walk) == PT_WALK_OK && g_nWalk == 1 && !g_walk[0].node && g_walk[0].size == 64.0f);
    g_nWalk = 0;
    assert(PtWalk(kFns, q, &none, Clip, Visit, g_walk) == PT_WALK_OK && g_nWalk == 0);
    // depth: a chain of 12 non-terminal cells (the root included) that ends in a leaf fills the game's 13 iterators: walked;
    // a 13th non-terminal cell would need its 14th iterator: PT_WALK_DEEP, nothing visited
    for (int inner = 12; inner <= 13; inner++) {
        g_nNodes = 0;
        memset(g_nodes, 0, sizeof(Node) * 16);
        for (int i = 0; i < inner; i++) g_nodes[i].ch[0] = &g_nodes[i + 1];  // nodes 0 .. inner-1 are inner cells
        g_nodes[inner].leaf = true;
        SetQ(q, &g_nodes[0]);
        const Rect corner = {0.0f, 0.0f, 0.001f, 0.001f};
        g_nWalk = 0, g_stopAt = 0;
        const int r = PtWalk(kFns, q, &corner, Clip, Visit, g_walk);
        if (inner == 12) {
            Ref(q, corner);
            assert(r == PT_WALK_OK && g_nRef == 1 && Same() && g_walk[0].node == (const u8*)&g_nodes[12]);
        } else assert(r == PT_WALK_DEEP && g_nWalk == 0);
    }
    printf("PlaceTestRO core: type rule (0xB41120's switch) for every type and both flags; polygon walk = the recursive walk on 24 "
           "random trees x 40 polygons (%d cells, same order), visitor stop, terminal root, 12 nested cells walked / 13 refused\n", visits);
}

#ifndef PLACERO_HOST_TEST
namespace ptt {
static LONG origCalls, origRet, qCalls, qWhy, qN;
static const u8 *qP, *qLot, *qRec;
static const void* qShape;
static LONG qLevel;
static DWORD qOpt;
static u8 qRec24;
// fake footprints: object {vt, interface vt at +4, id, type}; the interface slots +4 (id) and +8 (type) as the game's
struct Fp { const void* vt; const void* const* svt; unsigned __int64 id; int type; };
static unsigned __int64 __fastcall FpId(const u8* sub, void*) { return ((const Fp*)(sub - 4))->id; }
static int __fastcall FpType(const u8* sub, void*) { return ((const Fp*)(sub - 4))->type; }
static const void* const kFpSvt[3] = {NULL, (const void*)FpId, (const void*)FpType};
static Fp g_fp[4];
static DWORD __cdecl Orig(const u8*, void*) { origCalls++; return (DWORD)origRet | 0xABCD00; }
static int Query(SrCtx* c, const u8* p, const u8* lot, LONG level, const void* shape, const u8* rec) {
    qCalls++;
    qP = p, qLot = lot, qLevel = level, qShape = shape, qRec = rec, qOpt = *(const DWORD*)rec, qRec24 = rec[0x24];
    c->n = qN;
    for (int i = 0; i < qN; i++) c->objs[i] = (const u8*)&g_fp[i];
    return qWhy;
}
static u8 g_world[0x960];
static u8* __cdecl World() { return g_world; }
}  // namespace ptt

static void TestPlaceTestRO() {
    using namespace ptt;
    Ts3nCounters& kc = ts3native_counters;
    PlaceTestRoCounters& k = ts3native_placetestro;
    static_assert(offsetof(PlaceTestRoCounters, ptMode) == 0 && offsetof(PlaceTestRoCounters, ptShadow) == 4 &&
                      offsetof(PlaceTestRoCounters, ptFb) == 0x28 && offsetof(PlaceTestRoCounters, ptShadowMismatch) == 0x4C &&
                      sizeof(PlaceTestRoCounters) == 0x58, "ts3native_placetestro layout (docs/registry.md)");
    static_assert(offsetof(Fp, svt) == 4, "the interface sits at footprint + 4");
    assert(kPtSite.site == 0x00B462B7 && kPtSite.target == 0x00B41120);
    memset(&k, 0, sizeof(k));
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
    const PtFn savedOrig = g_ptOrig;
    const PtQueryFn savedQuery = g_ptQuery;
    const SrWorldFn savedWorld = g_srWorldGet;
    const AsyncSlot savedAsync = g_async;
    const LONG savedArmed = g_twArmed, savedOwner = kc.rgOwnerTid;
    const bool savedScratch = g_ptScratchSet;
    g_ptOrig = Orig, g_ptQuery = Query, g_srWorldGet = World, g_ptScratchSet = true;  // no read of the game's scratch here
    const DWORD me = GetCurrentThreadId();
    g_async.owner = me;
    kc.rgOwnerTid = 0;
    // 0xB41120's record: +0 the placed object (id at +8 / +0xC), +4 the lot, +0x24 level, +0x30 "check" byte, +0x84 flag, +0xA0 polygon
    static u8 rec[0xB0], obj[0x10], lot[0x50], planner[4];
    memset(rec, 0, sizeof(rec));
    memset(g_world, 0, sizeof(g_world));
    *(u8**)rec = obj, *(u8**)(rec + 4) = lot, *(LONG*)(rec + 0x24) = 3, rec[0x30] = 1;
    *(DWORD*)(obj + 8) = 0x1111, *(DWORD*)(obj + 0xC) = 0x2222;
    *(u8**)(g_world + 0x95C) = planner;
    for (Fp& f : g_fp) f.vt = NULL, f.svt = kFpSvt, f.id = 0x99, f.type = 3;
    auto call = []() { return PtStub(rec, NULL); };

    // switch off / another thread / the record's early outs: the game's call, nothing counted
    origCalls = 0, origRet = 1;
    assert((u8)call() == 1 && origCalls == 1 && k.ptCalls == 0);
    k.ptMode = 1, g_async.owner = me + 1, g_async.state = A_RUNNING;
    assert((u8)call() == 1 && origCalls == 2 && k.ptCalls == 0);
    g_async.owner = me;
    g_twArmed = 1, qWhy = SR_OK, qN = 0, qCalls = 0;
    rec[0x30] = 0;
    assert(call() == (0xABCD00 | 1) && origCalls == 3 && k.ptCalls == 0 && qCalls == 0);
    rec[0x30] = 1, *(u8**)(rec + 4) = NULL;
    assert(call() == (0xABCD00 | 1) && origCalls == 4 && k.ptCalls == 0);
    *(u8**)(rec + 4) = lot, *(u8**)rec = NULL;
    assert(call() == (0xABCD00 | 1) && origCalls == 5 && k.ptCalls == 0 && qCalls == 0);
    *(u8**)rec = obj;

    // a job runs: the clone answers with the record's lot, level and polygon and 0x82F470's query record; no game call
    assert(call() == 1 && origCalls == 5 && k.ptTaken == 1 && k.ptEmpty == 1 && k.ptBlocked == 0 && k.ptJob == 1);
    assert(qCalls == 1 && qP == planner && qLot == lot && qLevel == 3 && qShape == rec + 0xA0 && qOpt == 0x100 && qRec24 == 1);
    qN = 3;  // three footprints of passing types
    assert(call() == 1 && k.ptTaken == 2 && k.ptEmpty == 1 && k.ptBlocked == 0);
    g_fp[2].type = 9;  // a blocking type
    assert(call() == 0 && k.ptTaken == 3 && k.ptBlocked == 1 && origCalls == 5);
    g_fp[2].id = 0x0000222200001111ull;  // ... of the placed object itself: skipped
    assert(call() == 1 && k.ptBlocked == 1);
    g_fp[2].id = 0x0000222200001112ull;  // one id word differs: not the object
    assert(call() == 0 && k.ptBlocked == 2);
    g_fp[2].type = 0x12;  // blocks only with the routing world's byte +0x30
    assert(call() == 1);
    g_world[0x30] = 1;
    assert(call() == 0);
    g_world[0x30] = 0;
    g_fp[2].type = 0x13;  // blocks unless the record's byte +0x84 is set
    assert(call() == 0);
    rec[0x84] = 1;
    assert(call() == 1);
    g_fp[2].type = 0x18;
    assert(call() == 1);
    rec[0x84] = 0;
    assert(call() == 0 && origCalls == 5);
    g_fp[2].type = 3;

    // fallbacks: the game's call, counted by reason
    struct Fb { const char* name; int why; int idx; };
    static const Fb fbs[] = {{"deferred flush", SR_FB_DEFERRED, PT_FB_LOT}, {"lot miss", SR_FB_LOTMISS, PT_FB_LOT}, {"shape", SR_FB_SHAPE, PT_FB_SHAPE},
                             {"tripwire", SR_FB_TRIP, PT_FB_TRIP}, {"deep walk", PT_WHY_DEEP, PT_FB_DEEP}};
    for (const Fb& f : fbs) {
        qWhy = f.why;
        const LONG o0 = origCalls, f0 = k.ptFb[f.idx];
        origRet = 0;
        if (call() != 0xABCD00 || origCalls != o0 + 1 || k.ptFb[f.idx] != f0 + 1) {
            printf("PlaceTestRO fallback '%s' not taken\n", f.name);
            assert(false);
        }
    }
    qWhy = SR_OK;
    {
        const LONG o0 = origCalls;
        kc.rgOwnerTid = (LONG)me;  // the sim owns +0x918 (lent or recursive): the game's path
        assert(call() == 0xABCD00 && origCalls == o0 + 1 && k.ptFb[PT_FB_MAYNOT] == 1);
        kc.rgOwnerTid = 0;
        g_twArmed = 0;  // the job ended before the clone (tripwires disarmed)
        assert(call() == 0xABCD00 && origCalls == o0 + 2 && k.ptFb[PT_FB_JOBEND] == 1);
        g_twArmed = 1;
        AcquireSRWLockExclusive(&g_ql);  // a writer holds QL: try only
        assert(call() == 0xABCD00 && origCalls == o0 + 3 && k.ptFb[PT_FB_TRY] == 1);
        ReleaseSRWLockExclusive(&g_ql);
        *(u8**)(g_world + 0x95C) = NULL;  // no planner
        assert(call() == 0xABCD00 && origCalls == o0 + 4 && k.ptFb[PT_FB_SHAPE] == 2);
        *(u8**)(g_world + 0x95C) = planner;
        k.ptMode = 0;  // switch off with a job: the game's call
        const LONG t0 = k.ptTaken;
        assert(call() == 0xABCD00 && origCalls == o0 + 5 && k.ptTaken == t0);
    }

    // shadow with no job: the game's answer is returned, the clone compared; a skew is one mismatch
    k.ptShadow = 1, g_async.state = A_IDLE, g_twArmed = 0;
    origRet = 1, qN = 1;
    const LONG o0 = origCalls;
    assert(call() == (0xABCD00 | 1) && origCalls == o0 + 1 && k.ptShadowRuns == 1 && k.ptShadowMismatch == 0 && g_rgOffSimOpen == 0);
    g_fp[0].type = 2;
    assert(call() == (0xABCD00 | 1) && k.ptShadowRuns == 2 && k.ptShadowMismatch == 1 && g_rgOffSimOpen == 0);
    origRet = 0;
    assert(call() == 0xABCD00 && k.ptShadowRuns == 3 && k.ptShadowMismatch == 1);
    g_fp[0].type = 3;
    // shortcut: no threaded case here; the QL protocol is S2's (steerro-test, obstructro and rtoro threaded runs)
    printf("PlaceTestRO ([PlaceTestRO] Enabled=1): export layout; switch off / other thread / the record's three early outs pass; job: "
           "clone answers from the record's lot, level, polygon and the 0x100 query record, own id skipped, blocking types and both "
           "flags, no game call; fallbacks (deferred, lot miss, shape, tripwire, deep walk, +0x918 owner, job end, QL busy, no "
           "planner, switch off) call the game; shadow compare + skew\n");

    memset(&k, 0, sizeof(k));
    g_ptOrig = savedOrig, g_ptQuery = savedQuery, g_srWorldGet = savedWorld, g_ptScratchSet = savedScratch;
    g_async = savedAsync, g_twArmed = savedArmed, kc.rgOwnerTid = savedOwner;
}
#endif
