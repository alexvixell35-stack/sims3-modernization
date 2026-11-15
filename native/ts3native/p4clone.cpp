// ReplayCloneProvider: P4 part B (docs/94 §16.5, §21; 0.29.0). The P4CloneProvider of p4.cpp on top of replay.cpp's clone
// (Lots=replay) and snap.cpp's P3 version of the same cut. Included by ts3native.cpp after p4.cpp; registered by
// PcRegister when P4 (Mode=1) and the replay clone are both on.
//
// cut (sim, the async START, before the job is queued; AsyncBusy() must be false so +0x918 is free):
//   +0x918 taken; §8.5 steps 2-4: ApplyPendingUpdates 0x82C420 when P+0x10E, the deferred quadtree flush 0x666A40 on every
//   lot Q with +0x4C set, first use (RoomGraph_FirstUse 0x63D2F0) of every unbuilt lot LotTable_Contains accepts; then a
//   forced P3 cut (SpCut: images + the replay's RpCut, which publishes the ring position); then the snapshot of what the
//   clone does not hold: the routing world's bytes, P's bytes, the A* stamp word, the active lot id (I-94-87), a private
//   copy of P's footprint grid (+0xDC, when P3's grid image changed), the lock-key sets (fp+0x20, when a lock-key IC ran),
//   the live high-cost map headers and the blockedNodeMap sentinel. The consumer is held at that position (g_rpHoldAt).
// acquire (worker): waits (bounded) until the consumer reached the position and is idle, then takes g_rpUse (exclusive:
//   the consumer cannot drain while the clone run reads the clone) and builds the working copy: the world bytes with an
//   unlocked private +0x918, +0x95C = the clone's P (its own fields over the cut's P bytes: the map, the lot table), P's
//   grid -> the private copy, P+0xBC and every clone Q's +0x28 -> a private A* search object (0x61BF60), R (+0x54) from
//   the P3 R image, the 10 high-cost maps from the P3 tiles, blockedNodeMap rebuilt from the P3 image, every clone
//   footprint's fp+0x20 -> the private lock-key set. Refusals: P4_AB_EPOCH (an older cut, the epoch or active lot moved,
//   the consumer passed the position), P4_AB_MAT (no clone, timeout, a copy failed).
//   0.32.0 (I-94-220, I-94-221; docs/94 §13.3, §21.13): the copy's world planner [world+0x960] = a private W' (PcWp): the
//   live W's bytes from the cut with +8 = the clone's P, +0x10 / +0x14 = the copy's R / G, its own A* container (closed
//   list for the copy's R, own node pool), empty caches, its own FindClosest object, the lot-edge group hash copied at the
//   cut; and the copy's G water-link trees (+0x30, +0x4C) re-made node by node under the copy's anchors.
// release (worker): the lots the clone run wrote (or every lot) are rebased at the next cut; DISCARD (fault) rebuilds the
//   clone and drops the A* object and W'; WRAP drops the A* object (fresh stamps next time); the hold and g_rpUse are released.
// Left live (read-only in a search, load-time state; docs/94 §21): G cells, passability maps and body table (rebuilt only at
// a G build), lot-edge data, the world manager-like object [world+0x964] (= W+0xC), the non-routing roots of docs/101.

static const DWORD kPcWorldSz = 0xA00;   // the routing world object reaches past +0x994 (docs/101); 0xA00 covers it
static const DWORD kPcWSz = 0x198;       // PathPlannerWorld W (allocated with 0x198 at 0x82998B, ctor 0x657C50) [verified]
static const DWORD kPcEmptyBk = TS3A(0x0114C900);  // the shared empty bucket array an EASTL hash starts on (ctor 0x657CE7) [verified]
static const DWORD kPcGridRec = 0x50;    // footprint grid record (chain +0x48) [size inferred, I-94-131]
static const DWORD kPcHoldMs = 3000;
static const DWORD kPcWaitMs = 50;
static const int kPcKeyFailMax = 8;  // lock-key applies failed in a row before a clone rebuild is asked (0.35.9)
static const DWORD kPcWaitShadowMs = 250;  // Mode=1: the worker is idle after J_DONE (0.33.1)
static const DWORD kPcKeyMagic = 0x4B455953;  // "SEYK" in the 16-byte header of our lock-key set copies

struct PcBlocks { void* p[4096]; int n; PcBlocks* next; };
struct PcGrid { volatile LONG refs; u8* cells; DWORD nCells; PcBlocks* blk; const SpImg* img; };
struct PcKeys { volatile LONG refs; RpTab map; PcBlocks* blk; };  // live set -> private set (16-byte header: origin)
// G's two water-link trees (values in key order) and W's lot-edge group hash (entries bucket by bucket in chain order) at
// the cut; data follows the header: (nT[0] + nT[1]) x 16 bytes of node values, then gN x {bucket, key, value}
struct PcWx { volatile LONG refs; DWORD nT[2]; DWORD gCount, gEnd, gN, bytes; };
struct PcCut {
    DWORD ver; LONG epoch; LONG ok;
    SpVer* v;
    u8 world[kPcWorldSz];
    u8 p[0x110];
    DWORD stamp;
    u64 active;
    PcGrid* grid;
    PcKeys* keys;
    DWORD hcHdr[16 * 4]; DWORD nHc;  // live high-cost map vector entries (16 bytes each)
    DWORD bnmSentinel;
    const u8* liveW;                  // [world+0x960] at the cut (NULL: no world planner, no W')
    u8 wb[kPcWSz];                    // its bytes
    PcWx* wx;
    LONG w;               // g_spWrites at the cut (under its +0x918 hold)
    LONG dirtied;         // a shadow on this cut released with clone lots written: a second shadow on it is refused (0.35.6)
    LONGLONG qpc;         // P6 phase split (0.36.41): QPC when the cut was made (the job's cut age)
    LONG pend; ULONGLONG tick; DWORD seq;  // seq: cut order (tick has 15 ms steps)        // pend: a Mode=1 job's cut its shadow has not ended yet (acquire + release, refusal, drop)
};
// Two slots (0.33.1, docs/94 §21.14): the next job's START used to replace the cut of a job whose shadow had not acquired yet
// (409 "epoch" refusals on BP 0.32.8); now the newer cut goes to the other slot and the hold stays on the older one until its
// shadow ends, then moves on (g_pcHoldNext). Sim writes a slot at cut, the worker reads it after the hold.
// 0.34.1: three slots, the hold on the oldest pending cut (a parked shadow keeps its cut while the next job runs, docs/94 §21.16)
static const int kPcSlots = 3;
static PcCut g_pcCuts[kPcSlots];
static PcCut& g_pcCut = g_pcCuts[0];  // steerro-test K6 fills slot 0 directly
static DWORD g_pcCutSeq;
static DWORD g_pcHoldNext;            // the next pending cut after the held one (0 = none; diagnostics and tests)
static const DWORD kPcPendMs = 1000;  // an older cut keeps the hold only this long (a job that never reaches its shadow)
static SRWLOCK g_pcCutLock = SRWLOCK_INIT;
static PcGrid* g_pcGridLast;          // sim: the last grid copy (shared while P3's grid image is unchanged)
static PcKeys* g_pcKeysLast;
static PcWx* g_pcWxLast;              // sim: the last trees / group hash copy (shared while unchanged)

// game entry points (thiscall as fastcall; the selftest points them at fakes or NULL = skipped)
typedef void(__fastcall* PcThisFn)(const u8* self, void*);
static PcThisFn g_pcApplyPending = (PcThisFn)(uintptr_t)TS3A(0x0082C420);   // RoutingWorld_ApplyPendingUpdates (world)
static PcThisFn g_pcFlushDeferred = (PcThisFn)(uintptr_t)TS3A(0x00666A40);  // LotQuadTree_FlushDeferred (Q)
typedef void*(__fastcall* PcCtorFn)(void* self, void*);
static PcCtorFn g_pcAstarCtor = (PcCtorFn)(uintptr_t)TS3A(0x0061BF60);     // cAStarSearch ctor (0x9C90 bytes, docs/18)
// W' (docs/94 §21.13). Not the W ctor 0x657C50 / dtor 0x657A80: their container ctor 0x673C70 / dtor 0x674010 register and
// unregister the tuning variables by name (0x5A00A0), which would unbind the live container's [verified disassembly]
typedef void(__fastcall* PcFcInitFn)(u8* fc, void*, DWORD wm, u8* W, u8* P, const u8* R);  // ret 0x10
static PcFcInitFn g_pcFcInit = (PcFcInitFn)(uintptr_t)TS3A(0x00652C50);   // FindClosest object W+0xE0 init (as 0x654101)
typedef void(__fastcall* PcClosedFn)(u8* ctr, void*, const u8* R, DWORD);  // 0x6735C0 = add ecx,4; jmp 0x673500 (ret 8)
static PcClosedFn g_pcClosedInit = (PcClosedFn)(uintptr_t)TS3A(0x006735C0);   // frees and rebuilds the closed list for R
static PcThisFn g_pcFcReset = (PcThisFn)(uintptr_t)TS3A(0x00654110);          // W+0xE0: blocks, nodes freed, +0xC / +0x10 released
static PcThisFn g_pcCtrClear = (PcThisFn)(uintptr_t)TS3A(0x006741D0);         // W+0x18: pool blocks and closed list freed
typedef void(__fastcall* PcHashClrFn)(u8* h, void*, DWORD bk, DWORD n);  // ret 8 (the W dtor's calls at 0x657ADA, 0x657B47)
static PcHashClrFn g_pcClrC0 = (PcHashClrFn)(uintptr_t)TS3A(0x00657650), g_pcClrFC = (PcHashClrFn)(uintptr_t)TS3A(0x007F3790);
typedef void(__cdecl* PcGameFreeFn)(void*);
static PcGameFreeFn g_pcGameFree = (PcGameFreeFn)(uintptr_t)TS3A(0x004E3F70);  // NULL-safe (0x4E3F74)

// --- small arena (our heap; freed as a whole) ---
static void* PcAlloc(PcBlocks*& b, DWORD n) {
    if (!b || b->n == 4096) {
        PcBlocks* nb = (PcBlocks*)RpAlloc(sizeof(PcBlocks));
        if (!nb) return NULL;
        nb->n = 0, nb->next = b, b = nb;
    }
    void* p = RpAlloc(n ? n : 4);
    if (p) b->p[b->n++] = p;
    return p;
}
static void PcFreeBlocks(PcBlocks* b) {
    while (b) { for (int i = 0; i < b->n; i++) RpFree(b->p[i]); PcBlocks* nx = b->next; RpFree(b); b = nx; }
}
static void PcGridRel(PcGrid* g) { if (g && !InterlockedDecrement(&g->refs)) { PcFreeBlocks(g->blk); RpFree(g); } }
static void PcKeysRel(PcKeys* k) { if (k && !InterlockedDecrement(&k->refs)) { PcFreeBlocks(k->blk); RpTabFree(k->map); RpFree(k); } }

// A grid record's polygon set at +8 (EASTL vector of 16-byte polygons {begin, end, cap, allocator} of 8-byte 2D points; the
// segment test 0x627780 reads it through PolySet_SegmentIntersects 0x67B190 -> 0x7CDAA0) is owned by the record and freed
// with it, so a flat record copy points into live memory the sim frees after the cut (0.32.8: C0000005 at 0x7CDAA8 read a
// polygon {0, garbage} in a clone run; docs/94 §21.14). Both levels deep-copied into the grid's arena (0.33.1).
static bool PcPolyCopy(PcBlocks*& blk, u8* rec) {
    const u8* b = RpP(rec + 8);
    const u8* e = RpP(rec + 0xC);
    if (!b || e == b) return true;  // empty: never dereferenced (0x67B1E9 compares begin and end first)
    const DWORD n = (DWORD)(e - b);
    if (e < b || n % 16 || n > 4096 * 16) return false;
    u8* nb = (u8*)PcAlloc(blk, n);
    if (!nb) return false;
    memcpy(nb, b, n);
    for (u8* p = nb; p < nb + n; p += 16) {
        const u8* pb = RpP(p);
        const u8* pe = RpP(p + 4);
        const DWORD m = pb && pe > pb ? (DWORD)(pe - pb) : 0;
        if ((pb && pe < pb) || m % 8 || m > 65536 * 8) return false;
        u8* np = m ? (u8*)PcAlloc(blk, m) : NULL;
        if (m && !np) return false;
        if (m) memcpy(np, pb, m);
        *(u8**)p = np, *(u8**)(p + 4) = *(u8**)(p + 8) = np ? np + m : NULL;
    }
    *(u8**)(rec + 8) = nb, *(u8**)(rec + 0xC) = *(u8**)(rec + 0x10) = nb + n;
    return true;
}

// P's footprint grid +0xDC (32-byte cells: buckets +4, count +8, records chained at +0x48), deep-copied in chain order
static PcGrid* PcGridCopy(const u8* P) {
    PcGrid* g = (PcGrid*)RpAlloc(sizeof(PcGrid));
    if (!g) return NULL;
    memset(g, 0, sizeof(*g));
    g->refs = 1;
    bool ok = true;
    __try {
        const u8* cb = RpP(P + 0xDC);
        const u8* ce = RpP(P + 0xE0);
        const DWORD n = cb && ce > cb ? (DWORD)(ce - cb) / 32 : 0;
        if (n > (1u << 20)) ok = false;
        g->nCells = n;
        g->cells = n && ok ? (u8*)PcAlloc(g->blk, n * 32) : NULL;
        if (n && !g->cells) ok = false;
        for (DWORD c = 0; ok && c < n; c++) {
            const u8* lc = cb + c * 32;
            u8* pc = g->cells + c * 32;
            memcpy(pc, lc, 32);
            const DWORD* bk = (const DWORD*)RpP(lc + 4);
            const DWORD nb = RpD(lc + 8);
            if (!bk || !nb || nb > (1u << 20)) continue;
            DWORD* nbk = (DWORD*)PcAlloc(g->blk, (nb + 1) * 4);
            if (!nbk) { ok = false; break; }
            nbk[nb] = bk[nb];  // the end bucket keeps its value
            for (DWORD i = 0; i < nb && ok; i++) {
                DWORD* link = &nbk[i];
                *link = 0;
                for (const u8* r = (const u8*)(uintptr_t)bk[i]; r; r = RpP(r + 0x48)) {
                    u8* nr = (u8*)PcAlloc(g->blk, kPcGridRec);
                    if (!nr) { ok = false; break; }
                    memcpy(nr, r, kPcGridRec);
                    *(DWORD*)(nr + 0x48) = 0;
                    if (!PcPolyCopy(g->blk, nr)) { ok = false; break; }
                    *link = (DWORD)(uintptr_t)nr;
                    link = (DWORD*)(nr + 0x48);
                }
            }
            *(DWORD**)(pc + 4) = nbk;
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) { ok = false; }
    if (!ok) { PcGridRel(g); return NULL; }
    return g;
}

// lock-key sets (docs/101 §6: object 0x30, vtable 0x1043918, ref +4, id vectors +8..+0x10 and +0x1C..+0x24 of u64)
// reached through every live footprint in the planner's map; one private copy per set
static u8* PcKeyCopy(PcKeys* k, const u8* set) {
    if (DWORD* v = RpTabFind(k->map, (DWORD)(uintptr_t)set)) return (u8*)(uintptr_t)*v;
    u8* h = (u8*)PcAlloc(k->blk, 16 + 0x30);
    if (!h) return NULL;
    *(const u8**)h = set, *(DWORD*)(h + 4) = kPcKeyMagic, *(DWORD*)(h + 8) = *(DWORD*)(h + 12) = 0;
    u8* c = h + 16;
    memcpy(c, set, 0x30);
    *(LONG*)(c + 4) = RP_BIAS;  // nobody's release deletes it
    for (DWORD off = 8; off <= 0x1C; off += 0x14) {
        const u8* b = RpP(set + off);
        const u8* e = RpP(set + off + 4);
        const DWORD n = b && e > b && (DWORD)(e - b) <= (1u << 16) * 8 ? (DWORD)(e - b) : 0;
        u8* d = (u8*)PcAlloc(k->blk, n);
        if (!d) return NULL;
        if (n) memcpy(d, b, n);
        *(u8**)(c + off) = d, *(u8**)(c + off + 4) = d + n, *(u8**)(c + off + 8) = d + n;
    }
    if (!RpTabPut(k->map, (DWORD)(uintptr_t)set, (DWORD)(uintptr_t)c)) return NULL;
    return c;
}
static PcKeys* PcKeysCopy(const u8* P) {
    PcKeys* k = (PcKeys*)RpAlloc(sizeof(PcKeys));
    if (!k) return NULL;
    memset(k, 0, sizeof(*k));
    k->refs = 1;
    bool ok = true;
    __try {
        const u8* M = RpP(P + 0x24);
        const DWORD mb = M ? RpD(M + 8) : 0;
        const DWORD* lb = M ? (const DWORD*)RpP(M + 4) : NULL;
        for (DWORD i = 0; ok && mb > 1 && i < mb; i++)
            for (const u8* nd = (const u8*)(uintptr_t)lb[i]; nd && ok; nd = RpP(nd + 0x10)) {
                const u8* fp = RpP(nd + 8) ? RpP(RpP(nd + 8) + 8) : NULL;
                const RpCls* c = fp ? RpClass(RpD(fp)) : NULL;
                if (!c || c->child) continue;  // the child class has no +0x20 set
                const u8* set = RpP(fp + 0x20);
                if (set && !PcKeyCopy(k, set)) ok = false;
            }
    } __except (EXCEPTION_EXECUTE_HANDLER) { ok = false; }
    if (!ok) { PcKeysRel(k); return NULL; }
    return k;
}

// I-94-221: G holds its two water-link trees inline (anchors G+0x30, G+0x4C: EASTL rbtree, anchor right = last, left =
// first, parent = root, size +0x10; node 0x20 bytes: right +0, left +4, parent +8, value +0x10..+0x1F; docs/94 §13.3 K6), so
// a byte copy of G links into the live tree. Read here in key order; PcWxApply re-makes them under the copy's anchors.
// W+0xA0: the lot-edge group hash (buckets +4, count +8, size +0xC; node key, value, next +8 as find 0xD38B00 reads it); its
// only writers are ApplyLotUpdate 0x82B590 (0x656120 at 0x82B703) and LoadLotData 0x82CA10 (0x656150 at 0x82CBAA), both on
// the sim thread [verified xrefs]; the planner only finds in it (0x653214, 0x65694F, 0x6574EA). Both read at the cut (sim,
// +0x918 held). Returns the count or -1 (a walk that does not end at the anchor after `size` nodes)
static int PcTreeWalk(const u8* anc, u8* out) {
    const DWORD n = RpD(anc + 0x10);
    if (!n) return 0;
    DWORD i = 0;
    for (const u8* x = RpP(anc + 4); x != anc; i++) {
        if (!x || i == n) return -1;
        if (out) memcpy(out + 16 * i, x + 0x10, 16);
        if (RpP(x)) { x = RpP(x); while (RpP(x + 4)) x = RpP(x + 4); }
        else { const u8* p = RpP(x + 8); while (p != anc && x == RpP(p)) x = p, p = RpP(p + 8); x = p; }
    }
    return i == n ? (int)n : -1;
}
static void PcWxRel(PcWx* x) { if (x && !InterlockedDecrement(&x->refs)) RpFree(x); }
static PcWx* PcWxGather(const u8* G, const u8* W) {
    PcWx* x = NULL;
    __try {
        const DWORD n0 = RpD(G + 0x30 + 0x10), n1 = RpD(G + 0x4C + 0x10), cnt = RpD(W + 0xA8), gn = RpD(W + 0xAC);
        const DWORD* bk = (const DWORD*)RpP(W + 0xA4);
        if (n0 > (1u << 16) || n1 > (1u << 16) || !cnt || cnt > (1u << 20) || gn > (1u << 20) || !bk) return NULL;
        const DWORD bytes = (n0 + n1) * 16 + gn * 12;
        if (!(x = (PcWx*)RpAlloc(sizeof(PcWx) + bytes))) return NULL;
        x->refs = 1, x->bytes = bytes, x->gCount = cnt, x->gEnd = bk[cnt], x->gN = gn;
        u8* d = (u8*)(x + 1);
        const int a = PcTreeWalk(G + 0x30, d), b = a < 0 ? -1 : PcTreeWalk(G + 0x4C, d + n0 * 16);
        DWORD* e = (DWORD*)(d + (n0 + n1) * 16);
        DWORD m = 0;
        for (DWORD i = 0; i < cnt && m <= gn; i++)
            for (const u8* nd = (const u8*)(uintptr_t)bk[i]; nd && m <= gn; nd = RpP(nd + 8), m++)
                if (m < gn) e[3 * m] = i, e[3 * m + 1] = RpD(nd), e[3 * m + 2] = RpD(nd + 4);
        if (a < 0 || b < 0 || m != gn) { RpFree(x); return NULL; }
        x->nT[0] = n0, x->nT[1] = n1;
    } __except (EXCEPTION_EXECUTE_HANDLER) { RpFree(x); return NULL; }
    // unchanged since the last cut (the usual case: they change at a G build or a lot update): share that copy
    PcWx* l = g_pcWxLast;
    if (l && l->bytes == x->bytes && !memcmp(l->nT, x->nT, sizeof(PcWx) - offsetof(PcWx, nT)) && !memcmp(l + 1, x + 1, x->bytes)) {
        RpFree(x);
        InterlockedIncrement(&l->refs);
        return l;
    }
    PcWxRel(l);
    g_pcWxLast = x;
    InterlockedIncrement(&x->refs);
    return x;
}

static u64 PcActiveLot() {
    __try {
        const u8* wm = *g_spWmSlot;
        const u8* a = wm ? RpP(wm + 0xB0) : NULL;
        return a ? ((u64)RpD(a + 0x4C) << 32) | RpD(a + 0x48) : 0;
    } __except (EXCEPTION_EXECUTE_HANDLER) { return 0; }
}

// H20c (docs/109 §8.9, 0.30.0): a lot graph whose pending portal adds +0x54 or pending removes +0x64 (8-byte ids) are not
// empty. A clone run's quadtree unpin (0x639A80, H13) would move +0x54 into +0x44 through the game's grow 0x8B70E0, which
// EA-frees the old buffer (ours, from RpAlloc: heap corruption), and apply +0x64 through 0x635EE0. Filled only while a plan
// has the lot pinned (LotRouting_AddPortal 0x6379E0 branch at 0x637BAF on P+0xC8) [verified disassembly]; expected never at
// a START (I-109-37), so such a cut or clone is refused, not rebased
static bool PcPortalPending(const u8* g) { return RpD(g + 0x58) != RpD(g + 0x54) || RpD(g + 0x68) != RpD(g + 0x64); }

// §8.5 steps 2-4 at the async START (sim, +0x918 held): the game's own writers, so P1's leaves record them for the clone.
// Returns the lots left with pending portal changes (H20c: the cut is refused)
static int PcPrepare(const u8* world, const u8* P) {
    Ts3nCounters& k = ts3native_counters;
    int pend = 0;
    __try {
        if (P[0x10E] && g_pcApplyPending) { g_pcApplyPending(world, NULL); k.snPcPending++; }
        const int n = RpLiveLots(P, g_rpLive, kRpMaxLots);
        for (int i = 0; i < n; i++) {
            u8* g = g_rpLive[i].node + 0x10;
            for (u8* const* qp = *(u8* const* const*)(g + 0x74); qp && qp < *(u8* const* const*)(g + 0x78); qp++)
                if (*qp && (*qp)[0x4C] && g_pcFlushDeferred) { g_pcFlushDeferred(*qp, NULL); k.snPcFlushed++; }
            if (!g[0x8C] && g_rpContains(P, NULL, (DWORD)g_rpLive[i].key, (DWORD)(g_rpLive[i].key >> 32)) &&
                g_rpFirstUse(P, NULL, g, *(const WORD*)(g + 0x16C)))
                k.snPcFirstUse++;
            if (PcPortalPending(g)) pend++;
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) { k.snPcFaults++; }
    return pend;
}

// the snapshot of what the clone does not hold (sim, +0x918 held): world and P bytes, the A* stamp, the high-cost map headers,
// the blockedNodeMap sentinel, the world planner W's bytes, G's water-link trees and W's group hash
static bool PcSnap(PcCut& c, const u8* world, const u8* P) {
    bool ok = true;
    __try {
        memcpy(c.world, world, kPcWorldSz);
        memcpy(c.p, P, sizeof(c.p));
        c.stamp = *g_p4StampLive;
        c.nHc = 0;
        const DWORD nh = RpD(world + 0x8C + 0x7AC);
        const u8* hv = RpP(world + 0x8C + 0x7C0);
        if (hv && nh <= 16) { memcpy(c.hcHdr, hv, nh * 16); c.nHc = nh; }
        const DWORD* bk = (const DWORD*)RpP(world + 0x8C + 0x360);
        c.bnmSentinel = bk ? bk[RpD(world + 0x8C + 0x364)] : 0;
        c.liveW = RpP(world + 0x960);
        if (c.liveW) memcpy(c.wb, c.liveW, kPcWSz);
    } __except (EXCEPTION_EXECUTE_HANDLER) { ok = false; }
    PcWx* x = ok && c.liveW ? PcWxGather(world + 0x8C, c.liveW) : NULL;
    if (c.liveW && !x) ok = false;
    PcWxRel(c.wx);
    c.wx = x;
    return ok;
}

// under g_pcCutLock (exclusive): a pending cut older than kPcPendMs no longer counts (its job never reached the shadow); the
// hold goes to the oldest pending cut (a new holder gets a fresh kPcHoldMs), g_pcHoldNext names the next one
static void PcHoldPick(ULONGLONG now) {
    int a = -1, b = -1;
    for (int i = 0; i < kPcSlots; i++) {
        PcCut& h = g_pcCuts[i];
        if (h.pend && now - h.tick >= kPcPendMs) h.pend = 0;
        if (!h.pend || !h.ok || !h.ver) continue;
        if (a < 0 || (LONG)(h.seq - g_pcCuts[a].seq) < 0) b = a, a = i;
        else if (b < 0 || (LONG)(h.seq - g_pcCuts[b].seq) < 0) b = i;
    }
    const DWORD at = a >= 0 ? g_pcCuts[a].ver : 0;
    if (at && at != g_rpHoldAt) g_rpHoldUntil = now + kPcHoldMs;
    g_rpHoldAt = at;
    g_pcHoldNext = b >= 0 ? g_pcCuts[b].ver : 0;
}
// the job of cut `ver` is done with it (released, refused or dropped): the hold moves to the next pending cut, if any
static void PcHoldEnd(DWORD ver) {
    AcquireSRWLockExclusive(&g_pcCutLock);
    for (int i = 0; i < kPcSlots; i++) if (g_pcCuts[i].ver == ver && g_pcCuts[i].pend > 0) g_pcCuts[i].pend--;  // a shared cut: one per shadow
    PcHoldPick(GetTickCount64());
    ReleaseSRWLockExclusive(&g_pcCutLock);
    if (g_rpThreaded) SetEvent(g_rpEvent);
}
static void PcDrop(DWORD ver) { PcHoldEnd(ver); }

// 0.35.6 (docs/94 §21.21): a Mode=1 START while a job holds +0x918 shares the newest pending cut instead of a "busy"
// refusal: no sim wait and no new cut. The job's w0 is that cut's write count, so any recorded routing write after it (the
// running job's own, the sim's) makes the record stale (M5), as for a cut behind queued jobs (0.35.2). The slot holds the
// consumer until both shadows end (pend counts them); a shadow that wrote clone lots refuses the other one (PcAcquire)
// 0.36.41 (docs/94 §24, P6): Mode=2 with ShareCuts borrows too, but only a cut no recorded routing write has followed and
// only with no lot updates pending on the live world, so the clone answers the world this START sees (as a fresh cut would)
static bool g_pcBorrowed;  // sim: the last PcCutFn result was a borrowed cut
static LONGLONG g_pcCutQpc;  // sim: QPC of the cut the last PcCutFn returned (P6 phase split)
static LONG g_pcLastCopyUs;  // worker: the copy time of the last PcAcquire (0 = no copy; P6 phase split)
static u64 g_pcLastActive;   // worker: the active lot id of the cut the last successful PcAcquire copied (P6EarlyCopy recheck)
static DWORD PcBorrow() {
    Ts3nCounters& k = ts3native_counters;
    const bool m2 = k.snP4Mode == 2 && k.p6Share;
    if ((k.snP4Mode != 1 && !m2) || !g_rpOn || !g_spOn) return 0;
    int miss = -1;  // p6BorrowMiss index, -1 = borrowed
    if (m2) {
        const u8* world = *g_spWorldSlot;
        const u8* P = world ? RpP(world + 0x95C) : NULL;
        if (!P || P[0x10E]) miss = 2;  // a fresh cut would apply them first (PcPrepare); unlocked byte read: a stale 1 only misses
    }
    AcquireSRWLockExclusive(&g_pcCutLock);
    const ULONGLONG now = GetTickCount64();
    int b = -1;
    for (int i = 0; i < kPcSlots; i++) {
        const PcCut& h = g_pcCuts[i];
        if (h.pend > 0 && h.ok && h.ver && !h.dirtied && now - h.tick < kPcPendMs && (b < 0 || (LONG)(h.seq - g_pcCuts[b].seq) > 0)) b = i;
    }
    if (miss < 0 && b < 0) miss = 0;
    if (miss < 0 && m2 && g_pcCuts[b].w != g_spWrites) miss = 1;
    DWORD ver = 0;
    if (miss < 0) g_pcCuts[b].pend++, ver = g_pcCuts[b].ver, g_pcCutW = g_pcCuts[b].w, g_pcCutQpc = g_pcCuts[b].qpc;
    ReleaseSRWLockExclusive(&g_pcCutLock);
    if (ver) k.p4Borrow++, g_pcCutWhy = PS_NONE, g_pcBorrowed = true;
    else {
        k.p4BorrowMiss++;
        if (m2) k.p6BorrowMiss[miss]++;
    }
    return ver;
}

static DWORD PcCutFn() {
    Ts3nCounters& k = ts3native_counters;
    g_pcCutWhy = PS_OFF;
    g_pcBorrowed = false;
    if (!g_rpOn || !g_spOn) { k.snPcRefused[0]++; return 0; }
    // a job queued or running holds or will take +0x918: the cut would wait for it on the sim, and its own lazy writes
    // (first use, pending updates) would land between this START and its live plan (kept: docs/94 §21.14)
    // 0.35.2 (docs/94 §21.17): Mode=1 with jobs only queued (none running, so the worker holds no +0x918, e.g. it runs a
    // shadow): the lock is tried once, no sim wait. The queued jobs' writes before this job's live plan count in g_spWrites
    // after this cut, so such a record is stale (M5) instead of a refusal. A running job, or P5 (Mode=2), refuses as before
    // (0.36.41: and borrows a pending cut, Mode=2 only with ShareCuts and no write since it; PcBorrow)
    bool qcut = false;
    if (AsyncBusy()) {
        bool running = g_async.state == A_RUNNING;
        for (int i = 0; i < kJobs && !running; i++) running = g_jobs[i].state == J_RUNNING;
        if (running || k.snP4Mode != 1) { g_pcCutWhy = PS_BUSY; k.snPcRefused[0]++; return PcBorrow(); }
        qcut = true;
    }
    const u8* world = *g_spWorldSlot;
    const u8* P = world ? RpP(world + 0x95C) : NULL;
    if (!P) { k.snPcRefused[0]++; return 0; }
    static const DWORD kInf = 0xFFFFFFFF, kTry = 0;
    void* lk = (void*)(world + 0x918);
    if (g_spAcq(lk, NULL, qcut ? &kTry : &kInf) <= 0) {
        k.snPcRefused[0]++;
        if (qcut) { g_pcCutWhy = PS_BUSY, k.p4QCutMiss++; return PcBorrow(); }
        return 0;
    }
    if (qcut) k.p4QCut++;
    LARGE_INTEGER t0;
    QueryPerformanceCounter(&t0);
    const int pend = PcPrepare(world, P);
    if (pend) k.snPcPortalPend[0]++;
    g_spLastCut = GetTickCount64();
    const bool cut = SpCut(world, true) && SpTilesAll(g_spCur);  // P3 images (every tile: PcHighCost reads them all) + RpCut
    AcquireSRWLockExclusive(&g_pcCutLock);
    // the slot: one no Mode=1 job still waits on (0.33.1 two slots, 0.34.1 three: a parked shadow keeps its cut); none free =
    // the newest pending one is replaced (its shadow is refused "replaced"). Mode 2 (P5): one cut at a time, as before
    const ULONGLONG now = GetTickCount64();
    for (int i = 0; i < kPcSlots; i++) if (g_pcCuts[i].pend && (now - g_pcCuts[i].tick >= kPcPendMs || k.snP4Mode != 1)) g_pcCuts[i].pend = 0;
    int si = -1;
    for (int i = 0; i < kPcSlots && si < 0; i++) if (!g_pcCuts[i].pend) si = i;
    if (si < 0) {
        si = 0;
        for (int i = 1; i < kPcSlots; i++) if ((LONG)(g_pcCuts[i].seq - g_pcCuts[si].seq) > 0) si = i;
    }
    PcCut& c = g_pcCuts[si];
    c.pend = 0;
    SpVerRel(c.v);
    c.v = NULL, c.ok = 0, c.ver = 0, c.pend = 0;
    g_pcCutWhy = !cut || !g_spCur ? PS_P3 : pend ? PS_PORTAL : !g_rpHave || g_rpNeedEpoch || !g_rpPub || g_rpCutDeferred ? PS_NOCLONE : PS_COPY;
    if (cut && !pend && g_spCur && g_rpHave && !g_rpNeedEpoch && g_rpPub && !g_rpCutDeferred) {  // deferred: nothing published (0.35.12)
        c.ok = PcSnap(c, world, P);
        c.active = PcActiveLot();
        c.epoch = g_spEpoch;
        // grid: a new copy only when P3's grid image changed (else shared)
        const SpImg* gi = g_spCur->w[SP_GRID];
        if (!g_pcGridLast || g_pcGridLast->img != gi) {
            PcGrid* ng = PcGridCopy(P);
            if (ng) { ng->img = gi; PcGridRel(g_pcGridLast); g_pcGridLast = ng; } else c.ok = 0;
        }
        if (g_spKeysDirty || !g_pcKeysLast) {
            PcKeys* nk = PcKeysCopy(P);
            if (nk) { PcKeysRel(g_pcKeysLast); g_pcKeysLast = nk; g_spKeysDirty = 0; } else c.ok = 0;
        }
        PcGridRel(c.grid), PcKeysRel(c.keys);
        c.grid = g_pcGridLast, c.keys = g_pcKeysLast;
        if (c.grid) InterlockedIncrement(&c.grid->refs);
        if (c.keys) InterlockedIncrement(&c.keys->refs);
        if (c.ok) {
            c.v = SpVerRef(g_spCur);
            c.ver = g_rpPub;
            c.pend = 1, c.tick = now, c.seq = ++g_pcCutSeq;
            c.w = g_pcCutW = g_spWrites, c.dirtied = 0;
            LARGE_INTEGER q;
            QueryPerformanceCounter(&q);
            c.qpc = g_pcCutQpc = q.QuadPart;  // +0x918 still held: no write of a worker job lands before this read
            g_pcCutWhy = PS_NONE;
        }
    }
    PcHoldPick(now);  // the consumer stays at the oldest pending cut until its shadow ends
    const DWORD ver = c.ok ? c.ver : 0;
    ReleaseSRWLockExclusive(&g_pcCutLock);
    g_spRel(lk, NULL);
    const LONG us = UsSince(t0);
    InterlockedExchangeAdd(&k.snPcCutUs, us);
    Max(&k.snPcCutMaxUs, us);
    if (!ver) k.snPcRefused[1]++; else k.snPcCuts++;
    if (g_rpThreaded) SetEvent(g_rpEvent);
    return ver;
}

// --- the working copy (worker only) ---
struct PcWork {
    u8* world;           // kPcWorldSz, 16-aligned
    CRITICAL_SECTION cs; bool csInit;
    u8* astar;           // private cAStarSearch (NULL: none made, e.g. offline tests)
    LONG fresh;
    const SpImg* rImg; u8* r;  DWORD rCap;   // R: table + records from the P3 image
    u8* hc[16]; DWORD hcCap; const SpImg** tileImg; int nTiles, tw, th;  // high-cost maps from the P3 tiles
    DWORD hcVec[16 * 4];
    const SpImg* bnmImg; u8* bnm; DWORD bnmCap;  // blockedNodeMap buckets + nodes
    PcGrid* grid;        // held for the run
    PcKeys* keysAll[64]; int nKeys; const RpClone* clone;  // every lock-key table this clone's footprints may point into
    int keyFails;        // lock-key applies failed in a row on this clone (0.35.9)
    SpVer* v;
    DWORD ver;
    bool held;           // g_rpUse taken
    u8* wp; const u8* wpLive;            // W' (private world planner) and the live W it was made from
    const SpImg* wpRImg; bool wpClosed;  // the R image its closed list was built for
    u8* wx; DWORD wxCap;                 // the copy's G tree nodes and W' group hash (buckets + nodes)
};
static PcWork g_pcWork;

static u8* PcGrow(u8*& buf, DWORD& cap, DWORD need) {
    if (need <= cap && buf) return buf;
    RpFree(buf);
    buf = (u8*)RpAlloc(need);
    cap = buf ? need : 0;
    return buf;
}

static bool PcAstar(PcWork& w) {
    if (w.astar || !g_pcAstarCtor) return true;
    u8* a = (u8*)g_rpNew(0x9C90, TS3A(0x00FEC594), 0, 0, 0, 0);  // as the P init 0x62E9F1
    if (!a) return false;
    CfRec(CF_ASTAR_NEW, a, 0x9C90);
    __try { g_pcAstarCtor(a, NULL); } __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
    *(LONG*)(a + 4) = RP_BIAS;  // ours: no game release deletes it
    w.astar = a;
    w.fresh = 1;
    return true;
}
// 0.35.22 (docs/94 §22.25): the clone's Qs keep +0x28 = the dropped object until the next acquire re-points them; that test
// knew only the live P's object and the current one, so after a drop that kept the clone (P4REL_LAZY, and WRAP since 0.29)
// every Q kept the freed object: BP 0.35.21 0419 faulted in its node hash (0x664396) and corrupted the EA heap (0x4E5EC6)
static const u8* g_pcAstarDead;
static void PcAstarDrop(PcWork& w) {
    u8* a = w.astar;
    w.astar = NULL;
    if (!a) return;
    g_pcAstarDead = a;
    // its nodes are private pool nodes: pushed as P4 job work (else each is a plForeign push leaked to the sink, crash G log)
    if (g_p4PoolsEnterJob) g_p4PoolsEnterJob();
    __try { *(LONG*)(a + 4) = 1; ((RpDtorFn)(uintptr_t)RpD(RpP(a)))(a, NULL, 1); }  // deleting dtor 0x61EB20: nodes back to the pools
    __except (EXCEPTION_EXECUTE_HANDLER) { ts3native_counters.snPcFaults++; }
    if (g_p4PoolsLeaveJob) g_p4PoolsLeaveJob();
}

// R (+0x54: w, h, table +8, records +0xC, size +0x10) from the P3 image [w, h, size][table w*h*4][records]
static bool PcR(PcWork& w, const SpImg* img) {
    u8* r = w.world + 0x54;
    if (!img || img->size < 12) return true;  // no image (no routing world data): the copy keeps the cut's words
    const DWORD rw = *(const DWORD*)img->data, rh = *(const DWORD*)(img->data + 4), size = *(const DWORD*)(img->data + 8);
    if (!rw || !rh) return true;
    const DWORD n = img->size - 12;
    if (n != rw * rh * 4 + size) return false;
    if (w.rImg != img) {
        if (!PcGrow(w.r, w.rCap, n)) return false;
        memcpy(w.r, img->data + 12, n);
        w.rImg = img;
    }
    *(u8**)(r + 8) = w.r, *(u8**)(r + 0xC) = w.r + rw * rh * 4;
    return true;
}

// the 10 high-cost maps from the P3 tiles (only tiles whose image changed are copied)
static bool PcHighCost(PcWork& w, const PcCut& c, const SpVer* v) {
    u8* g = w.world + 0x8C;
    const DWORD gw = RpD(g + 0x7A0), gh = RpD(g + 0x7A4), nh = c.nHc;
    if (!v->tiles || !nh || !gw || !gh) return true;
    if (gw > 65536 || gh > 65536 || nh > 16) return false;
    const DWORD cells = gw * gh;
    if (w.tw != v->tw || w.th != v->th || w.hcCap != cells) {  // shape changed: everything again
        for (int m = 0; m < 16; m++) { RpFree(w.hc[m]); w.hc[m] = NULL; }
        RpFree((void*)w.tileImg);
        w.tileImg = (const SpImg**)RpAlloc(sizeof(SpImg*) * v->tw * v->th);
        if (!w.tileImg) return false;
        memset((void*)w.tileImg, 0, sizeof(SpImg*) * v->tw * v->th);
        w.tw = v->tw, w.th = v->th, w.hcCap = cells;
        for (DWORD m = 0; m < nh; m++) if (!(w.hc[m] = (u8*)RpAlloc(cells))) return false; else memset(w.hc[m], 0, cells);
    }
    for (int t = 0; t < v->tw * v->th; t++) {
        const SpImg* ti = v->tiles[t];
        if (!ti || w.tileImg[t] == ti) continue;
        const DWORD tx = t % v->tw, tz = t / v->tw;
        const DWORD x0 = tx * kSpTile, z0 = tz * kSpTile;
        const DWORD x1 = x0 + kSpTile < gw ? x0 + kSpTile : gw, z1 = z0 + kSpTile < gh ? z0 + kSpTile : gh;
        const u8* p = ti->data;
        const u8* e = ti->data + ti->size;
        for (DWORD m = 0; m < nh; m++) {
            if (p + 4 > e) return false;
            const DWORD has = *(const DWORD*)p;
            p += 4;
            if (!has) continue;
            for (DWORD z = z0; z < z1; z++) {
                if (p + (x1 - x0) > e) return false;
                memcpy(w.hc[m] + z * gw + x0, p, x1 - x0);
                p += x1 - x0;
            }
        }
        w.tileImg[t] = ti;
    }
    memcpy(w.hcVec, c.hcHdr, nh * 16);
    for (DWORD m = 0; m < nh; m++) {
        DWORD* hv = w.hcVec + m * 4;
        if (!hv[0]) continue;  // the live map is empty: so is ours
        hv[0] = (DWORD)(uintptr_t)w.hc[m], hv[1] = hv[2] = (DWORD)(uintptr_t)(w.hc[m] + cells);
    }
    *(DWORD**)(g + 0x7C0) = w.hcVec, *(DWORD**)(g + 0x7C4) = w.hcVec + nh * 4, *(DWORD**)(g + 0x7C8) = w.hcVec + nh * 4;
    return true;
}

// blockedNodeMap (G+0x35C: buckets +0x360, count +0x364, size +0x368; node {key, byte, next}; bucket key % count) from the
// P3 image (sorted (key << 32 | byte) pairs)
static bool PcBnm(PcWork& w, const PcCut& c, const SpImg* img) {
    u8* g = w.world + 0x8C;
    const DWORD nb = RpD(g + 0x364);
    if (!img || !nb || nb > (1u << 24)) return true;
    const DWORD n = img->size / 8;
    const DWORD need = (nb + 1) * 4 + n * 12;
    if (w.bnmImg != img || RpD(g + 0x360) != (DWORD)(uintptr_t)w.bnm) {
        if (!PcGrow(w.bnm, w.bnmCap, need)) return false;
        DWORD* bk = (DWORD*)w.bnm;
        memset(bk, 0, nb * 4);
        bk[nb] = c.bnmSentinel;
        u8* nodes = w.bnm + (nb + 1) * 4;
        const u64* e = (const u64*)img->data;
        for (DWORD i = 0; i < n; i++) {
            u8* nd = nodes + i * 12;
            const DWORD key = (DWORD)(e[i] >> 32);
            *(DWORD*)nd = key, *(DWORD*)(nd + 4) = (DWORD)e[i] & 0xFF, *(DWORD*)(nd + 8) = bk[key % nb];
            bk[key % nb] = (DWORD)(uintptr_t)nd;
        }
        w.bnmImg = img;
    }
    *(u8**)(g + 0x360) = w.bnm;
    *(DWORD*)(g + 0x368) = n;
    return true;
}

// every clone footprint's fp+0x20 -> the private set of the live set it stood for. A footprint copied from live holds the
// live set; one re-pointed at an earlier acquire holds our copy, whose 16-byte header names the live set (origin, magic).
// The tables of earlier cuts stay alive while this clone lives (PcWork.keysAll), so those headers can be read.

static bool PcKeysApply(const PcKeys* k, const RpTab& t) {
    if (!k) return true;  // no table (no lock-key set anywhere at the cut)
    for (DWORD i = 0; i < t.cap; i++) {
        if (t.k[i] <= 1) continue;
        u8* fp = (u8*)(uintptr_t)t.v[i];
        const RpCls* c = RpClass(*(const DWORD*)fp);
        if (!c || c->child) continue;
        const u8* set = *(u8**)(fp + 0x20);
        if (!set) continue;
        const u8* origin = *(const DWORD*)(set - 12) == kPcKeyMagic ? *(const u8* const*)(set - 16) : set;
        const DWORD* v = RpTabFind(k->map, (DWORD)(uintptr_t)origin);
        if (!v) return false;  // a set the cut did not see (a lock-key IC after the cut): refused, never the live one
        *(u8**)(fp + 0x20) = (u8*)(uintptr_t)*v;
    }
    return true;
}

// --- W' (I-94-220; docs/94 §21.13). RoutingJob_Execute takes W from the world it runs on through 0x829C00 (0x81B3E7), and
// the hierarchical planner 0x65E0D0 reads P [W+8] (LotPlan_Inner, LotSearch_Entry, ...), R [W+0x10] and G [W+0x14], and
// writes the container W+0x18 and the caches. Layout from the ctor 0x657C50 and dtor 0x657A80 [verified]: +4 refs, +0xC
// [world+0x964], +0x18 container (0x70: vtable, +4 R, +8 closed size, +0xC offsets, +0x10 bits, +0x14 pool list, +0x1C name,
// +0x20 free list, +0x24.. tuning), +0x88 R-usable byte, +0x8C..+0x98 tuning, +0x9C RNG word (0x4FA800), +0xA0 group hash,
// +0xC0 "ObjectFootprints" hash (filled lazily from R by 0x6580F0), +0xE0 FindClosest object (list of 10 blocks; +0xEC P,
// +0xF0 W, +0xF4 [world+0x964], +0xF8 R), +0xFC "GoalPtSet" hash (cleared per plan, 0x65FBD6), +0x11C..+0x127 lot-exit masks,
// +0x128 and +0x160 the BestLotEdgePortals sets (vectors at +0x128 / +0x160)

// the copy's G trees under its own anchors (a balanced tree; the copy is read only: no insert rebalances it), then the group
// hash in its own buckets, chain order kept
static u8* PcTreeLink(u8* nodes, int lo, int hi, u8* parent) {
    if (lo > hi) return NULL;
    const int m = (lo + hi) / 2;
    u8* n = nodes + m * 0x20;
    *(u8**)(n + 8) = parent;
    *(u8**)(n + 4) = PcTreeLink(nodes, lo, m - 1, n);
    *(u8**)n = PcTreeLink(nodes, m + 1, hi, n);
    return n;
}
static bool PcWxApply(PcWork& w, const PcWx* x, u8* W) {
    const DWORD nt = x->nT[0] + x->nT[1], cnt = x->gCount;
    if (!PcGrow(w.wx, w.wxCap, nt * 0x20 + (cnt + 1) * 4 + x->gN * 12)) return false;
    const u8* d = (const u8*)(x + 1);
    u8* nodes = w.wx;
    memset(nodes, 0, nt * 0x20);
    for (int t = 0; t < 2; t++) {
        u8* anc = w.world + 0x8C + (t ? 0x4C : 0x30);
        const DWORD n = x->nT[t];
        for (DWORD i = 0; i < n; i++) memcpy(nodes + i * 0x20 + 0x10, d + 16 * i, 16);
        if (!n) *(u8**)anc = *(u8**)(anc + 4) = anc, *(u8**)(anc + 8) = NULL;
        else {
            *(u8**)(anc + 8) = PcTreeLink(nodes, 0, (int)n - 1, anc);
            *(u8**)anc = nodes + (n - 1) * 0x20, *(u8**)(anc + 4) = nodes;
        }
        *(DWORD*)(anc + 0x10) = n;
        d += n * 16, nodes += n * 0x20;
    }
    // I-94-231: G's highCostFootprints tree (anchor G+0x7D4, size +0x7E4; ctor 0x671F97) is a byte copy of live nodes, and its
    // only reader 0x670510 erases and EA-frees the node it finds. The copy gets it empty (SnEnter also stops a clone run there)
    u8* hca = w.world + 0x8C + 0x7D4;
    *(u8**)hca =*(u8**)(hca + 4) = hca, *(u8**)(hca + 8) = NULL, *(DWORD*)(hca + 0x10) = 0;
    DWORD* bk = (DWORD*)nodes;
    memset(bk, 0, cnt * 4);
    bk[cnt] = x->gEnd;  // the end bucket keeps its value (find compares against it, 0xD38B2C)
    u8* gn = nodes + (cnt + 1) * 4;
    DWORD* link = NULL;
    DWORD cur = 0xFFFFFFFF;
    for (DWORD i = 0; i < x->gN; i++, d += 12) {
        const DWORD b = *(const DWORD*)d;
        u8* nd = gn + i * 12;
        *(DWORD*)nd = *(const DWORD*)(d + 4), *(DWORD*)(nd + 4) = *(const DWORD*)(d + 8), *(DWORD*)(nd + 8) = 0;
        if (b != cur) link = &bk[b], cur = b;
        *link = (DWORD)(uintptr_t)nd;
        link = (DWORD*)(nd + 8);
    }
    *(DWORD**)(W + 0xA4) = bk, *(DWORD*)(W + 0xA8) = cnt, *(DWORD*)(W + 0xAC) = x->gN;
    return true;
}

// W' freed: what the game allocated in it (FindClosest blocks, cache nodes and buckets, vectors, pool blocks, closed list)
// through the game's own clear functions, as the dtor 0x657A80 does minus its tuning unregistration; the group hash is ours
static void PcWpDrop(PcWork& w) {
    u8* W = w.wp;
    w.wp = NULL, w.wpLive = NULL, w.wpRImg = NULL, w.wpClosed = false;
    if (!W) return;
    __try {
        *(DWORD*)(W + 0xEC) = 0, *(DWORD*)(W + 0xF0) = 0;  // no release of the clone's P or of W' itself
        if (g_pcFcReset) g_pcFcReset(W + 0xE0, NULL);
        if (g_pcClrC0) g_pcClrC0(W + 0xC0, NULL, RpD(W + 0xC4), RpD(W + 0xC8));
        if (g_pcClrFC) g_pcClrFC(W + 0xFC, NULL, RpD(W + 0x100), RpD(W + 0x104));
        if (g_pcGameFree) {
            if (RpD(W + 0xC8) > 1) CfFree(CF_WP_FREE, RpP(W + 0xC4)), g_pcGameFree(RpP(W + 0xC4));
            if (RpD(W + 0x104) > 1) CfFree(CF_WP_FREE, RpP(W + 0x100)), g_pcGameFree(RpP(W + 0x100));
            CfFree(CF_WP_FREE, RpP(W + 0x128)), g_pcGameFree(RpP(W + 0x128)), CfFree(CF_WP_FREE, RpP(W + 0x160)), g_pcGameFree(RpP(W + 0x160));
        }
        if (g_pcCtrClear) g_pcCtrClear(W + 0x18, NULL);
    } __except (EXCEPTION_EXECUTE_HANDLER) { ts3native_counters.snPcFaults++; }  // leaked, never freed twice
    RpFree(W);
}

// W' for this acquire: made once from the cut's W bytes (again after a DISCARD or for a new live W), then per acquire the
// live scalars of the cut and the copy's P, R, G
static bool PcWp(PcWork& w, const PcCut& c, u8* cp) {
    if (w.wp && w.wpLive != c.liveW) PcWpDrop(w);  // another world's W (a world load): its caches belong to that world
    u8* W = w.wp;
    u8* const R = w.world + 0x54;
    if (!W) {
        if (!(W = (u8*)RpAlloc(kPcWSz))) return false;  // 16-aligned like the game's
        memcpy(W, c.wb, kPcWSz);
        *(LONG*)(W + 4) = RP_BIAS;  // nobody's release deletes it
        u8* ctr = W + 0x18;         // container: vtable, name, tuning as live; closed list, pool, free list ours (empty)
        memset(ctr + 4, 0, 0x10);
        *(u8**)(ctr + 0x14) = *(u8**)(ctr + 0x18) = ctr + 0x14, *(DWORD*)(ctr + 0x20) = 0;
        // caches empty as the ctor leaves them, next-resize words (+0xD8, +0x114) included: with the live W's threshold the
        // first insert (0x657800 -> rehash check 0x4E09B0) skips the rehash and links its node into the game's shared empty
        // bucket array 0x114C900, which every empty hash then sees (crash G, docs/67 §9)
        *(DWORD*)(W + 0xC4) = kPcEmptyBk, *(DWORD*)(W + 0xC8) = 1, *(DWORD*)(W + 0xCC) = 0, *(DWORD*)(W + 0xD8) = 0;
        *(DWORD*)(W + 0x100) = kPcEmptyBk, *(DWORD*)(W + 0x104) = 1, *(DWORD*)(W + 0x108) = 0, *(DWORD*)(W + 0x114) = 0;
        static const WORD kZero[] = {0x128, 0x12C, 0x130, 0x138, 0x13C, 0x150, 0x154, 0x158,
                                     0x160, 0x164, 0x168, 0x170, 0x174, 0x188, 0x18C, 0x190};  // as the ctor zeroes them
        for (int i = 0; i < (int)(sizeof(kZero) / sizeof(kZero[0])); i++) *(DWORD*)(W + kZero[i]) = 0;
        // FindClosest: an empty list, then the game's init; +0xC / +0x10 already equal the P and W it is handed, so it takes no
        // reference (0x652C66, 0x652C8E) and appends its 10 blocks to our list
        *(u8**)(W + 0xE0) = *(u8**)(W + 0xE4) = W + 0xE0;
        *(u8**)(W + 0xEC) = cp, *(u8**)(W + 0xF0) = W, *(DWORD*)(W + 0xF4) = 0, *(DWORD*)(W + 0xF8) = 0;
        if (g_pcFcInit) g_pcFcInit(W + 0xE0, NULL, RpD(c.wb + 0xC), W, cp, R);
        w.wp = W, w.wpLive = c.liveW;
    }
    memcpy(W, c.wb, 4);                      // vtable
    memcpy(W + 0xC, c.wb + 0xC, 4);          // [world+0x964] (live: the world manager-like object, docs/99 §5)
    memcpy(W + 0x88, c.wb + 0x88, 0x18);     // R-usable byte, tuning floats, the RNG word +0x9C as live at the cut
    memcpy(W + 0x18 + 0x24, c.wb + 0x18 + 0x24, 0x4C);  // container tuning: threshold, SHORT and LONG sets
    memcpy(W + 0x11C, c.wb + 0x11C, 0xC);    // lot-exit masks and +0x124 (rebuilt per plan)
    *(u8**)(W + 8) = cp, *(u8**)(W + 0x10) = R, *(u8**)(W + 0x14) = w.world + 0x8C;
    *(u8**)(W + 0xEC) = cp, *(u8**)(W + 0xF0) = W, *(DWORD*)(W + 0xF4) = RpD(c.wb + 0xC), *(u8**)(W + 0xF8) = R;
    if (!w.wpClosed || w.wpRImg != w.rImg) {  // the closed list follows R's shape: rebuilt with each new R image
        if (g_pcClosedInit) g_pcClosedInit(W + 0x18, NULL, R, 0);
        w.wpClosed = true, w.wpRImg = w.rImg;
    }
    if (!PcWxApply(w, c.wx, W)) return false;
    *(u8**)(w.world + 0x960) = W;
    return true;
}

// 0.35.26 (docs/94 §22.30): true = PcAcquire for cut `ver` makes no game call (no new cAStarSearch, no W' made or dropped, no
// FindClosest or closed-list init, i.e. the same R image as the last acquire). It then reads only the cut's copies and the P3
// images and writes only the worker's copy and the clone (under g_rpUse), so P5 runs it inside the lent +0x918 window instead
// of under the job's hold (5-6 ms per acquire on BP). Worker only: g_pcWork is the worker's
static bool PcAcquireOwnOnly(DWORD ver) {
    const PcWork& w = g_pcWork;
    if (!w.astar && g_pcAstarCtor) return false;
    bool own = false;
    AcquireSRWLockShared(&g_pcCutLock);
    int ci = -1;
    for (int i = 0; i < kPcSlots; i++) if (g_pcCuts[i].ver == ver && g_pcCuts[i].ok) ci = i;  // PcAcquire's pick
    if (ci >= 0) {
        const PcCut& c = g_pcCuts[ci];
        const SpImg* r = c.v ? c.v->w[SP_R] : NULL;
        const bool rNew = r && r->size >= 12 && *(const DWORD*)r->data && *(const DWORD*)(r->data + 4) && r != w.rImg;  // PcR copies it
        own = !c.liveW || (w.wp && w.wpLive == c.liveW && w.wpClosed && !rNew && w.wpRImg == w.rImg);
    }
    ReleaseSRWLockShared(&g_pcCutLock);
    return own;
}

static int PcAcquireBody(DWORD ver, P4Clone* out);
static int PcAcquire(DWORD ver, P4Clone* out) {  // the worker's phase marker back to the caller's (P5 acquires inside its job)
    const LONG phase = g_p4Phase;
    const int r = PcAcquireBody(ver, out);
    g_p4Phase = phase;
    return r;
}
static int PcAcquireBody(DWORD ver, P4Clone* out) {
    Ts3nCounters& k = ts3native_counters;
    g_pcLastCopyUs = 0;
    PcWork& w = g_pcWork;
    if (!g_rpOn || !g_rpHave) { k.snPcRefused[2]++; g_pcAcqWhy = PS_NOCLONE; PcHoldEnd(ver); return P4_AB_MAT; }
    // the consumer reaches the position (it stops there) and is idle; offline tests drain here. A shadow (Mode=1) waits up to
    // kPcWaitShadowMs unless a live job queues meanwhile (0.33.1: 50 ms refused BP shadows while the consumer applied a rebase
    // batch); a P5 acquire runs inside the live job and keeps 50 ms
    const ULONGLONG until0 = GetTickCount64() + (k.snP4Mode == 1 ? kPcWaitShadowMs : kPcWaitMs);
    ULONGLONG until = until0;
    // 0.35.2 (docs/94 §21.17): a shadow waits on, up to 900 ms after its cut (the hold lasts kPcPendMs = 1 s), while nothing is
    // queued: the worker is idle and holds no game lock here, so a longer wait costs the sim nothing ("mat"/"timeout" was the
    // consumer still applying a batch or a clone free when 250 ms ran out)
    if (k.snP4Mode == 1) {
        AcquireSRWLockShared(&g_pcCutLock);
        for (int i = 0; i < kPcSlots; i++)
            if (g_pcCuts[i].ver == ver && g_pcCuts[i].ok && g_pcCuts[i].tick + kPcPendMs - 100 > until) until = g_pcCuts[i].tick + kPcPendMs - 100;
        ReleaseSRWLockShared(&g_pcCutLock);
    }
    g_p4Phase = 3;
    for (;;) {
        if (!g_rpThreaded) RpDrain(false);
        if (g_rpTail == ver) {
            if (GetTickCount64() > until0) k.p4WaitExt++;
            break;
        }
        if ((DWORD)(ver - g_rpTail) > (DWORD)(g_rpHead - g_rpTail)) {  // passed it
            k.snPcRefused[3]++, g_pcAcqWhy = PS_PASSED;
            PcHoldEnd(ver);
            return P4_AB_EPOCH;
        }
        bool queued = false;
        for (int i = 0; i < kJobs && k.snP4Mode == 1; i++) queued |= g_jobs[i].state == J_QUEUED;
        if (GetTickCount64() > until || queued) {
            k.snPcRefused[4]++, g_pcAcqWhy = queued ? PS_QUEUED : PS_TIMEOUT;
            if (!queued) PcHoldEnd(ver);  // queued: P4Shadow parks the shadow (keeps the cut) or drops it (0.34.1)
            return P4_AB_MAT;
        }
        if (g_rpThreaded) SetEvent(g_rpEvent);
        Sleep(0);
    }
    // 0.33.2 (docs/94 §21.15): never an unbounded wait here. P5 acquires inside its job with the routing lock held, and a
    // consumer that waited for that lock while holding g_rpUse made both wait for ever (the 0.33.1 hang shape); both locks are
    // tried until the same deadline, then the shadow is refused
    g_p4Phase = 4;
    while (!TryAcquireSRWLockExclusive(&g_rpUse)) {
        if (GetTickCount64() > until) { k.snPcRefused[4]++, g_pcAcqWhy = PS_LOCK; PcHoldEnd(ver); return P4_AB_MAT; }
        Sleep(0);
    }
    w.held = true;
    CfUse(true);
    while (!TryAcquireSRWLockShared(&g_pcCutLock)) {
        if (GetTickCount64() > until) {
            w.held = false;
            CfUse(false);
            ReleaseSRWLockExclusive(&g_rpUse);
            k.snPcRefused[4]++, g_pcAcqWhy = PS_LOCK;
            PcHoldEnd(ver);
            return P4_AB_MAT;
        }
        Sleep(0);
    }
    int ci = 0;
    for (int i = 1; i < kPcSlots; i++) if (g_pcCuts[i].ver == ver && g_pcCuts[i].ok) ci = i;
    const PcCut& c = g_pcCuts[ci];
    int why = -1;
    if (g_rpTail != ver || c.ver != ver || !c.ok) why = P4_AB_EPOCH, k.snPcRefused[3]++, g_pcAcqWhy = PS_REPLACED;
    else if (c.dirtied) why = P4_AB_MAT, k.p4BorrowDirty++, g_pcAcqWhy = PS_STALE;  // a shared cut's clone was written by its other shadow
    else if (c.epoch != g_spEpoch || PcActiveLot() != c.active) why = P4_AB_EPOCH, k.snPcRefused[5]++, g_pcAcqWhy = PS_MOVED;  // I-94-87
    else if (!g_rpClone || g_rpNeedEpoch || g_rpBad) {  // g_rpBad alone: a rebuild cleared the ask, its EPOCH is after this cut
        why = P4_AB_MAT, k.snPcRefused[2]++, g_pcAcqWhy = PS_NOCLONE;
        if (g_rpClone && !g_rpNeedEpoch) k.p4AcqBad++;
    }
    else if (g_rpStale) why = P4_AB_MAT, k.snPcRefused[7]++, g_pcAcqWhy = PS_STALE;  // a lot the consumer forced is not rebased yet
    else
        for (int i = 0; i < g_rpClone->nLots; i++)  // H20c on the clone itself (what the run reads)
            if (g_rpClone->lots[i].node && PcPortalPending(RpGraph(g_rpClone->lots[i].node))) {
                why = P4_AB_MAT, k.snPcPortalPend[1]++, g_pcAcqWhy = PS_PORTAL;
                break;
            }
    if (why < 0) {
        LARGE_INTEGER t0;
        QueryPerformanceCounter(&t0);
        bool ok = true;
        __try {
            if (!w.world) w.world = (u8*)RpAlloc(kPcWorldSz);
            if (!w.world) ok = false;
            else {
                memcpy(w.world, c.world, kPcWorldSz);
                // +0x918: our own unlocked lock (CRITICAL_SECTION at +0, count +0x20, flag +0x24 = CS kind; 0x4E16F0)
                if (!w.csInit) { InitializeCriticalSection(&w.cs); w.csInit = true; }
                memcpy(w.world + 0x918, &w.cs, sizeof(w.cs));
                *(LONG*)(w.world + 0x918 + 0x20) = 0, w.world[0x918 + 0x24] = 1;
                // the clone's P: the cut's P bytes under the fields the clone owns (map +0x24, lot table +0x48..+0x67)
                u8* cp = g_rpClone->p;
                u8 own[0x48];
                memcpy(own, cp + 0x24, 4), memcpy(own + 4, cp + 0x48, 0x20);
                memcpy(cp, c.p, 0x110);
                memcpy(cp + 0x24, own, 4), memcpy(cp + 0x48, own + 4, 0x20);
                if (c.grid) *(u8**)(cp + 0xDC) = c.grid->cells, *(u8**)(cp + 0xE0) = *(u8**)(cp + 0xE4) = c.grid->cells + c.grid->nCells * 32;
                if (!PcAstar(w)) ok = false;
                const DWORD liveA = RpD(c.p + 0xBC);
                if (w.astar) {
                    *(u8**)(cp + 0xBC) = w.astar;
                    for (int i = 0; i < g_rpClone->nLots; i++)
                        for (int j = 0; j < g_rpClone->lots[i].nQ; j++) {
                            u8* q = g_rpClone->lots[i].q[j];
                            const DWORD qa = q ? RpD(q + 0x28) : 0;
                            if (q && (qa == liveA || qa == (DWORD)(uintptr_t)w.astar || (qa && qa == (DWORD)(uintptr_t)g_pcAstarDead)))
                                *(u8**)(q + 0x28) = w.astar;
                            else if (qa) k.snPcOddQ++;
                        }
                    g_pcAstarDead = NULL;  // every clone Q points at w.astar now
                }
                *(u8**)(w.world + 0x95C) = cp;
                ok = ok && PcR(w, c.v->w[SP_R]) && PcHighCost(w, c, c.v) && PcBnm(w, c, c.v->w[SP_BNM]);
                ok = ok && (!c.liveW || (c.wx && PcWp(w, c, cp)));  // I-94-220 / I-94-221: [copy+0x960] = W'
                if (w.clone != g_rpClone) {  // a new clone: its footprints hold live sets only
                    for (int i = 0; i < w.nKeys; i++) PcKeysRel(w.keysAll[i]);
                    w.nKeys = 0, w.keyFails = 0, w.clone = g_rpClone;
                }
                bool kok = true;
                if (c.keys && (!w.nKeys || w.keysAll[w.nKeys - 1] != c.keys)) {
                    if (w.nKeys == 64) kok = false;  // 64 failed applies in a row in one clone's life (kPcKeyFailMax asks a rebuild first)
                    else { InterlockedIncrement(&c.keys->refs); w.keysAll[w.nKeys++] = c.keys; }
                }
                if (ok) {
                    kok = kok && PcKeysApply(c.keys, g_rpClone->fps) && PcKeysApply(c.keys, g_rpClone->kids);
                    // 0.35.9 (docs/94 §22.13, I-94-450): a clone lives the session since soft resync (0.35.5), so an apply that
                    // keeps failing (an origin no later table has, or the cap above) refused every acquire, Mode=2 included, until
                    // an epoch that no longer comes; kPcKeyFailMax failures in a row ask one (the new clone holds live sets only)
                    if (kok) w.keyFails = 0;
                    else if (++w.keyFails >= kPcKeyFailMax) w.keyFails = 0, g_rpEpochWhy = -__LINE__, g_rpNeedEpoch = 1;  // the build log names this line
                    ok = kok;
                }
                // 0.35.7 (I-94-430): a full apply re-pointed every clone footprint set into c.keys (an origin it lacks fails
                // the apply), so the older tables are unreferenced; a partial apply keeps them all (mixed pointers)
                if (ok && c.keys && w.nKeys > 1 && w.keysAll[w.nKeys - 1] == c.keys) {
                    for (int i = 0; i < w.nKeys - 1; i++) PcKeysRel(w.keysAll[i]);
                    w.keysAll[0] = c.keys, w.nKeys = 1;
                }
            }
        } __except (EXCEPTION_EXECUTE_HANDLER) { ok = false; k.snPcFaults++; }
        const LONG us = UsSince(t0);
        InterlockedExchangeAdd(&k.snPcMatUs, us);
        g_pcLastCopyUs = ok ? us : 0;
        if (ok && w.ver == ver) { k.p6ReAcq++; InterlockedExchangeAdd(&k.p6ReAcqUs, us); }  // P6: a shared cut's second copy
        if (!ok) why = P4_AB_MAT, k.snPcRefused[6]++, g_pcAcqWhy = PS_COPY;
    }
    if (why < 0) {
        PcGridRel(w.grid), SpVerRel(w.v);
        w.grid = c.grid, w.v = c.v;
        if (w.grid) InterlockedIncrement(&w.grid->refs);
        SpVerRef(w.v);
        w.ver = ver;
        out->world = w.world, out->ver = ver, out->epoch = (DWORD)c.epoch, out->fresh = w.fresh;
        g_pcLastActive = c.active;
        if (w.fresh) g_p4Work.stamp = c.stamp;  // a new A* object has no node with any stamp: start where the game is
        w.fresh = 0;
        k.snPcAcquired++;
    }
    ReleaseSRWLockShared(&g_pcCutLock);
    if (why >= 0) {
        w.held = false;
        CfUse(false);
        ReleaseSRWLockExclusive(&g_rpUse);
        PcHoldEnd(ver);
    }
    return why;
}

// 0.35.21 (docs/94 §22.24.1, I-94-484): after a lazy stop (P4LazyKeep) the unwind skipped the unpins and iteration ends of the
// frames above the stop, so clone Qs can keep +0x48 > 0; the consumer's next apply on such a Q would defer into the LIVE
// pending maps (the clone P's +0x30 / +0x3C are live, see PcDeferStop). Cleared here (g_rpUse held: nobody else touches
// the clone) and their lots go to the dirty list with the run's own (rebased at the next cut). Returns the dirty count, -1 = all
static volatile LONG g_pcLazyKeep, g_pcLazyPins;  // log only (no counter change)
static int PcLazyPins(unsigned __int64* lots, int n, int cap) {
    RpClone* c = g_rpClone;
    for (int i = 0; c && i < c->nLots; i++) {
        bool hit = c->lots[i].node && PcPortalPending(RpGraph(c->lots[i].node));
        for (int j = 0; j < c->lots[i].nQ; j++) {
            u8* q = c->lots[i].q[j];
            if (q && *(LONG*)(q + 0x48)) *(LONG*)(q + 0x48) = 0, hit = true, InterlockedIncrement(&g_pcLazyPins);
        }
        if (!hit || n < 0) continue;
        int j = 0;
        while (j < n && lots[j] != c->lots[i].key) j++;
        if (j == n) { if (n < cap) lots[n++] = c->lots[i].key; else n = -1; }
    }
    return n;
}
static void PcRelease(const P4Clone* cl, LONG flags, const unsigned __int64* dirty, int nDirty) {
    Ts3nCounters& k = ts3native_counters;
    PcWork& w = g_pcWork;
    if (!w.held) return;
    unsigned __int64 lazy[64];
    if (flags & P4REL_LAZY) {
        const int n0 = nDirty < 0 ? -1 : nDirty < 64 ? nDirty : -1;
        if (n0 > 0) memcpy(lazy, dirty, n0 * sizeof(lazy[0]));
        nDirty = PcLazyPins(lazy, n0, 64), dirty = lazy;
        if (!nDirty) nDirty = -1;  // shortcut: a keep-stop always names its lot; none = something unexpected, rebase all
        const LONG n = InterlockedIncrement(&g_pcLazyKeep);
        LONG p = 1;
        while (p < n && p < 100000000) p *= 10;
        if (p == n) Log("P4: lazy stop released without a clone rebuild: %d lot(s) rebased at the next cut (%s), %ld so far, %ld pins cleared",
                        nDirty < 0 ? -1 : nDirty, nDirty < 0 ? "all" : "dirty list", n, g_pcLazyPins);
        flags |= P4REL_WRAP;  // the A* object and W' are dropped below (the stopped search left them mid-plan)
        PcWpDrop(w);
    }
    // lots the clone run wrote: rebased from live at the next cut (their clone copy is no longer the live one)
    if (nDirty || (flags & P4REL_DISCARD)) {
        while (InterlockedExchange(&g_rpRingLock, 1)) YieldProcessor();
        if (nDirty < 0 || (flags & P4REL_DISCARD)) g_rpForceAll = 1;
        else for (int i = 0; i < nDirty; i++) {
            int j = 0;
            while (j < g_rpNForce && g_rpForce[j] != dirty[i]) j++;
            if (j == g_rpNForce) { if (g_rpNForce < 64) g_rpForce[g_rpNForce++] = dirty[i]; else g_rpForceAll = 1; }
        }
        InterlockedExchange(&g_rpRingLock, 0);
        k.snPcDirtyLots += nDirty < 0 ? 1 : nDirty;
    }
    if (nDirty || (flags & P4REL_DISCARD)) {  // a second shadow on this cut (shared, 0.35.6) would read those lots
        AcquireSRWLockExclusive(&g_pcCutLock);
        // 0.35.22: after a lazy stop every pending cut (a parked or second-slot shadow's, made before this release) is refused
        // too: the stopped lots are half-written until the next cut's rebase, and cuts made meanwhile are deferred (refused).
        // BP 0.35.21 0419: a run at log line 230 read such a clone while every cut since the release deferred
        for (int i = 0; i < kPcSlots; i++) if (g_pcCuts[i].ver == cl->ver || (flags & P4REL_LAZY)) g_pcCuts[i].dirtied = 1;
        ReleaseSRWLockExclusive(&g_pcCutLock);
    }
    if (flags & P4REL_DISCARD) { g_rpEpochWhy = -__LINE__, g_rpNeedEpoch = 1, g_rpBad = 1; PcAstarDrop(w); PcWpDrop(w); k.snPcDiscard++; }  // the clone (and W') may hold half-done writes
    else if (flags & P4REL_WRAP) { PcAstarDrop(w); w.fresh = 1; }  // stamps wrapped: a new A* object, fresh stamps
    w.held = false;
    CfUse(false);
    ReleaseSRWLockExclusive(&g_rpUse);
    PcHoldEnd(cl->ver);
}

// 0.31.4 (docs/94 §21.11): the clone's P is a copy of the live P, so its pending-insert / pending-remove maps P+0x30 / P+0x3C
// (and their node allocators) are the LIVE ones. A quadtree insert 0x666C50 or remove 0x666E30 on a clone Q while that Q is
// being iterated (+0x48 > 0) would defer into them from the worker, and a later flush 0x666A40 of the clone Q would erase live
// nodes there, unlocked against the sim [verified disassembly 0x666C84..0x666D6D, 0x666E5D..0x666EBF, 0x666A59..0x666B50;
// the clone P copy: replay.cpp RpBuild, PcAcquire]. Every other path of the two wrappers writes only the Q itself. So a clone run
// stops (D4 lazy stop, the clone is discarded) before such a deferral. Live calls pass through untouched.
typedef DWORD(__fastcall* PcQInsFn)(void* q, void* edx, void* fp);            // thiscall(Q; footprint), ret 4
typedef DWORD(__fastcall* PcQRemFn)(void* q, void* edx, DWORD lo, DWORD hi);  // thiscall(Q; id lo, id hi), ret 8
static PcQInsFn g_pcQIns;
static PcQRemFn g_pcQRem;
static void PcDeferStop(const void* q) {
    if (!t_snapRun || !q || *(const LONG*)((const u8*)q + 0x48) <= 0) return;
    if (InterlockedIncrement(&ts3native_counters.pcDeferStop) <= 8)
        Log("P4: quadtree deferral on clone Q %p in a clone run (would write the live pending maps): run stopped", q);
    // 0.35.21 (docs/94 §22.24.1): the stop is at the wrapper's entry; its 8 callers write before it only hooked leaves (W3,
    // W6f, the lot-0 remove), new footprint objects or this Q's lot [verified disassembly], and the unwind leaves this Q's
    // iteration count +0x48 (PcRelease clears it). So this Q's lot is rebased instead of a clone rebuild
    const u8* b = (const u8*)q;
    P4WorkerDirty(true, ((unsigned __int64)*(const DWORD*)(b + 0x1C) << 32) | *(const DWORD*)(b + 0x18));
    P4LazyKeep();
}
static DWORD __fastcall PcQInsHook(void* q, void* edx, void* fp) { PcDeferStop(q); return g_pcQIns(q, edx, fp); }
static DWORD __fastcall PcQRemHook(void* q, void* edx, DWORD lo, DWORD hi) { PcDeferStop(q); return g_pcQRem(q, edx, lo, hi); }

static const P4CloneProvider kPcProvider = {PcCutFn, PcAcquire, PcRelease, PcDrop};
static void PcRegister() {
    if (!g_p4On) return;
    if (!g_rpOn) { Log("route snapshot P4: no clone provider (needs [RouteSnap] Lots=replay): every shadow refused \"clone\""); return; }
    static const u8 kIns[] = {0x83, 0xEC, 0x18, 0x53, 0x55}, kRem[] = {0x83, 0xEC, 0x10, 0x53, 0x56};  // sub esp,n; push x2
    g_pcQIns = (PcQInsFn)InstallDetour("LotQuadTree_Insert (P4 deferral stop)", TS3A(0x00666C50), kIns, sizeof(kIns), (void*)PcQInsHook, &g_pcQIns);
    g_pcQRem = g_pcQIns ? (PcQRemFn)InstallDetour("LotQuadTree_Remove (P4 deferral stop)", TS3A(0x00666E30), kRem, sizeof(kRem),
                                                 (void*)PcQRemHook, &g_pcQRem) : NULL;
    if (!g_pcQRem) { Log("FAIL route snapshot P4: deferral stop not installed: no clone provider (every shadow refused \"clone\")"); return; }
    P4SetCloneProvider(&kPcProvider);
    ts3native_counters.snPcReady = 1;
    Log("OK   route snapshot P4 clone provider: replay clone + P3 images (docs/94 §21)");
}
