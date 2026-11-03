// [RouteSnap] Lots=replay (docs/94 §11 design B, §16 status; 0.28.0, counters v49): per-lot footprints and quadtrees by
// ordered leaf replay into a private clone, audit only (no search reads the clone; that is P4). Included by ts3native.cpp
// after snap.cpp, plugged into its SpLotStrategy. Off unless [RouteSnap] Enabled=1 and Lots=replay.
//
// Clone (game layout, docs/94 §11.4 step 1 and §12.8): planner P (0x110 bytes) with its own footprint map M = [P+0x24]
// (EASTL hash map: buckets +4, count +8, size +0xC, rehash policy +0x10, node pool +0x1C; 0x18-byte nodes key +0, FME +8,
// next +0x10), its own lot table (P+0x4C buckets, P+0x50 count, 0x194-byte nodes chained at +0x190, lot graph at +0x10),
// per lot the quadtrees Q (0x58 bytes: P +0x20, root +0x24, refcount +0x34), tree nodes (inner 0x1C from pool 0x667990,
// leaf 0x14 from pool 0x667920; flags +4, refcount byte +7; leaf entry list +8: one inline 12-byte entry or a heap array
// | 1 with count +0xC), FootprintMapEntry FME (0x14: vtable 0xFEAC7C, ref +4, footprint +8, Q +0xC, level/flags +0x10;
// pool 0x61ED80; the map node holds its only reference) and footprints (9 classes, 0x40-0x4C bytes; per-level polygon
// cache [fp+0x38] of 0x24-byte levels with 16-byte polygon records of 8-byte points; children list +0x24). Insertion
// order is kept (bucket chains, leaf lists); caches are copied valid (EnsureAllPolygons +0x70 on the live footprint
// first); node stamps do not exist at this level. Objects the game's code may free come from the game's allocators: tree
// nodes and map nodes from PRIVATE pools (pools.cpp, docs/94 §16.7: the game's pools are unlocked, §16.6), leaf arrays,
// buckets and FMEs from the locked EA heap / FME pool; the rest is ours (P, M, lot nodes, Q, footprints).
// Clone Qs and footprints carry RP_BIAS in their refcount so the game's release never deletes them; we free them.
// Pointers left live (opaque, never followed by the replay): P fields other than +0x24/+0x48..+0x50, lot graph fields
// other than the portal / add / remove / Q vectors, footprint +0x20 lock-key set and owner fields (P4 copies those).
//
// Recorder (§11.4 step 2): routesnap.cpp's P1 stubs call SpNote; before taking the dirty lock it calls RpPre, which turns
// W1 apply insert (Q, footprint, flag) into an INS record carrying a fresh footprint copy made right there on the writing
// thread (caches forced first), W2 apply remove (Q, key) into a REM record, and W3 QuadTreeBuild / W4 LotGraph_Build
// into a STALE mark for their lot (rebase at the next cut). W1's own child recursion (return 0x665F87) is not recorded:
// the replayed parent inserts its children. W5, lot removal, portal and lock ICs, and lot graph builds are found by the
// cut's lot signature compare and rebased (step 3). W6 cache fills are not recorded (§11.6).
// Ring: one record per logical change, appended under a spin lock by any writing thread (the sim and the game's own route
// job, both under +0x918), read by one consumer. Full ring = records dropped, clone rebuilt at the next cut.
// Cut (§11.4 step 4, sim at a route START under +0x918, from SpBuildVersion): lot signatures compared with the sim's
// mirror of what the clone was built from; changed / new / removed lots go out as one LOTS rebase (whole-lot copies made
// here); then a SYNC record (live map size and lot count) and, every AuditEvery cuts, an AUDIT record with a hash of every
// live lot; then the ring position is published. §8.5 steps 2-4 (forced first use, ApplyPendingUpdates, deferred flushes)
// are not forced: records are taken at the apply leaves, so live state and clone stay equal at the published position
// without them; P4 adds them for the search.
// Consumer (§11.4 step 5): the "snap replay" thread applies records up to the published position with the game's own
// ApplyInsert 0x665C50 / ApplyRemove 0x666750 on the clone, holding +0x918 through the game's Lock_Acquire (it serialises
// the consumer with the sim's cut and builder, which also write the clone; NOT the game's pools: those are unlocked and the
// clone uses private ones, pools.cpp, docs/94 §16.6-§16.7; the thread is the replay op thread inside each hold), at most ~1 ms per hold. AUDIT: clone lots hashed the same way and compared (rpAudits / rpMis, first LogMismatch logged). SYNC: map
// size or lot count different = divergence, rebuilt at the next cut. Footprint identity (§11.4 step 6): live pointer ->
// clone block; an INS reuses the clone footprint while clone FMEs still hold it, else takes the record's copy; blocks no
// FME holds are freed after each batch.
// Epoch (§11.4 step 1): at world event 7 (sim, +0x918 taken here) the whole clone is built; world events 5, 9, 0x0A, a new
// routing world, a ring overflow, a divergence or a fault rebuild it at the next cut.

static const LONG RP_BIAS = 0x20000000;
enum { RP_INS = 1, RP_REM, RP_STALE, RP_LOTS, RP_EPOCH, RP_AUDIT, RP_SYNC, RP_NTYPES };
enum { RPS_NOQ, RPS_STALE, RPS_NOCOPY, RPS_NOCLONE };  // RPS_STALE counts records applied to a lot waiting for its rebase
static const DWORD kRpNodeSz = 0x194, kRpQSz = 0x58, kRpPSz = 0x110, kRpMSz = 0x40, kRpRet665F87 = 0x00665F87;

// game entry points (thiscall as fastcall with a dummy edx; the tests point them at fakes)
typedef char(__fastcall* RpInsFn)(u8* q, void*, u8* fp, DWORD flag);                        // 0x665C50, ret 8
typedef char(__fastcall* RpRemFn)(u8* q, void*, DWORD lo, DWORD hi);                        // 0x666750, ret 8
typedef void*(__fastcall* RpMapAllocFn)(void* alloc, void*, DWORD size, DWORD flags);       // 0x624A10 (M+0x1C)
typedef void(__fastcall* RpMapFreeFn)(void* alloc, void*, void* p, DWORD size);             // 0x624AB0 (M+0x1C)
typedef void(__fastcall* RpMapNodeFreeFn)(u8* m, void*, u8* node);                          // 0x6281A0: FME release + pool
typedef void*(__cdecl* RpFmePoolFn)(DWORD, DWORD, DWORD, DWORD, DWORD, DWORD);               // 0x61ED80 (locked pool)
typedef void*(__cdecl* RpNewFn)(DWORD size, DWORD flags, DWORD, DWORD, DWORD name, DWORD line);  // 0x4E3F90
typedef DWORD(__fastcall* RpVoidFn)(const u8* self, void*);                                 // footprint slots +0, +0x70
typedef void(__fastcall* RpDtorFn)(u8* self, void*, DWORD flags);                           // deleting destructors
static RpInsFn g_rpIns = (RpInsFn)(uintptr_t)0x00665C50;
static RpRemFn g_rpRem = (RpRemFn)(uintptr_t)0x00666750;
static RpMapAllocFn g_rpMapAlloc = (RpMapAllocFn)(uintptr_t)0x00624A10;
static RpMapFreeFn g_rpMapFree = (RpMapFreeFn)(uintptr_t)0x00624AB0;
static RpMapNodeFreeFn g_rpMapNodeFree = (RpMapNodeFreeFn)(uintptr_t)0x006281A0;
static RpFmePoolFn g_rpFmePool = (RpFmePoolFn)(uintptr_t)0x0061ED80;
static RpNewFn g_rpNew = (RpNewFn)(uintptr_t)0x004E3F90;
typedef void(__cdecl* RpDelFn)(void* p);                                                    // EA_Free 0x4E3F70
static RpDelFn g_rpDel = (RpDelFn)(uintptr_t)0x004E3F70;
static const DWORD* g_rpNewFlags = (const DWORD*)(uintptr_t)0x01158060;  // the leaf array resize 0x663766 passes it

// footprint classes (docs/82 §3.1; sizes from the factory 0x62CD40): cache = [fp+0x38] is a polygon cache. child = a child
// sub-footprint that lives only in a parent's child list (+0x24 nodes): 0xFEBF90, 0x28 bytes from the pool 0x1165B80, made
// by 0x807400 on the parent (+0xC = the parent, a back pointer; +8 refcount as the others, slots +0x90/+0x94; +0x10 float,
// +0x18/+0x1C key, +0x20 byte; slot 0 forwards to the parent's; no child list of its own: +0x24 is the object's last word)
// [verified disassembly, Steam 1.67; P3-B live run 2026-10-18: every clone build failed on it]. Copied flat inside the
// parent's block with +0xC re-pointed at the parent's copy; never copied on its own.
// middle = 0x1020AE8, objects of the middle constructor 0x80EED0 itself: "Routing/LotEdgeFootprint" (0x63A1C0, every lot)
// and "Routing/PolygonFootprint" (factory 0x62CD40), both 0x40 bytes from 0x4E3F90, levels 1 (slot 0 = 0x619EF0), cache
// at +0x38 / count +0x3C, slot +0x70 = 0x80E610 [verified disassembly, Steam 1.67; P3-B live run 2026-10-18 on 0.29.0:
// left out of the table, every clone build failed on it].
struct RpCls { DWORD vt, size, cache, child; };
static const RpCls kRpClsGame[] = {{0xFEBBC0, 0x48, 1, 0}, {0xFEBA98, 0x44, 1, 0}, {0xFEB848, 0x44, 1, 0}, {0xFEB970, 0x44, 1, 0},
                                   {0xFEBE38, 0x48, 1, 0}, {0xFEBD10, 0x44, 1, 0}, {0xFEB5F8, 0x40, 1, 0}, {0xFEB720, 0x40, 1, 0},
                                   {0x1020AE8, 0x40, 1, 0}, {0xFEAE08, 0x4C, 0, 0}, {0xFEBF90, 0x28, 0, 1}};
static const int kRpNClsGame = sizeof(kRpClsGame) / sizeof(kRpClsGame[0]);
static const RpCls* g_rpCls = kRpClsGame;
static int g_rpNCls = kRpNClsGame;

// --- our memory (16-aligned: the lot transforms are read with movaps) ---
static HANDLE g_rpHeap;
static volatile LONG g_rpBytes;
static LONG g_rpCapBytes = 64 << 20;
// [RouteSnap] EpochHeap (0.36.73, docs/66 §7 A3): the blocks of each clone build come from that build's own heap (on the
// building thread only: t_rpGen); the heap is destroyed when its last block is freed (the grave's last slice, a failed
// build at once), so a rebuild's 2x peak and its segments go back to the free address space. Every other block stays on
// g_rpHeap. One reference per block plus the build's own until the build ends. Header: a[-1] raw, a[-2] size, a[-3] generation.
struct RpGen { HANDLE h; volatile LONG refs, bytes; LONG peak; };
static bool g_rpEpochHeap;
static __declspec(thread) RpGen* t_rpGen;
// EpochHeap also builds the clone once per load (docs/66 §7.1, docs/194 §9 S2): world events 9 / 0x0A (world file, save
// loaded) start a wait in which the cut builds no clone; event 7 (after the load's G build, docs/94 §9.1) builds it and ends
// the wait. Before, the cut built one between the two and event 7 a second (both alive: the 2x load peak). kRpLoadWaitMs
// bounds the wait if event 7 never comes (then the cut builds as before).
static ULONGLONG g_rpLoadWaitUntil;  // sim
static const DWORD kRpLoadWaitMs = 60000;
static void RpGenRel(RpGen* g) {
    if (InterlockedDecrement(&g->refs)) return;
    Ts3nCounters& k = ts3native_counters;
    InterlockedExchangeAdd(&k.rpGenRelKB, g->peak >> 10);  // lower bound of the reservation handed back
    LARGE_INTEGER t0;
    QueryPerformanceCounter(&t0);
    HeapDestroy(g->h);  // usually in the consumer's grave slice (+0x918 held): its cost is that hold's
    Max(&k.rpGenDestroyMaxUs, UsSince(t0));
    HeapFree(GetProcessHeap(), 0, g);
    InterlockedDecrement(&k.rpGenLive);
    InterlockedIncrement(&k.rpGenFreed);
}
static RpGen* RpGenNew() {  // NULL: the build uses g_rpHeap as before
    RpGen* g = (RpGen*)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, sizeof(RpGen));
    if (g && !(g->h = HeapCreate(0, 0, 0))) HeapFree(GetProcessHeap(), 0, g), g = NULL;
    if (!g) return NULL;
    g->refs = 1;
    InterlockedIncrement(&ts3native_counters.rpGenLive);
    return g;
}
static void* RpAlloc(DWORD n) {
    if (!g_rpHeap) g_rpHeap = HeapCreate(0, 0, 0);
    RpGen* g = t_rpGen;
    u8* raw = (u8*)HeapAlloc(g ? g->h : g_rpHeap, HEAP_ZERO_MEMORY, n + 28);
    if (!raw) return NULL;
    u8* a = (u8*)(((uintptr_t)raw + 12 + 15) & ~(uintptr_t)15);
    ((void**)a)[-1] = raw;
    ((DWORD*)a)[-2] = n;
    ((RpGen**)a)[-3] = g;
    if (g) {
        InterlockedIncrement(&g->refs);
        Max(&g->peak, InterlockedExchangeAdd(&g->bytes, (LONG)n) + (LONG)n);
    }
    Ts3nCounters& k = ts3native_counters;
    k.rpKB = (InterlockedExchangeAdd(&g_rpBytes, (LONG)n) + (LONG)n) >> 10;
    Max(&k.rpKBMax, k.rpKB);
    return a;
}
static void RpFree(void* a) {
    if (!a) return;
    const LONG n = (LONG)((DWORD*)a)[-2];
    ts3native_counters.rpKB = (InterlockedExchangeAdd(&g_rpBytes, -n) - n) >> 10;
    RpGen* g = ((RpGen**)a)[-3];
    HeapFree(g ? g->h : g_rpHeap, 0, ((void**)a)[-1]);
    if (g) InterlockedExchangeAdd(&g->bytes, -n), RpGenRel(g);
}
static inline DWORD A16(DWORD n) { return (n + 15) & ~15u; }
static inline DWORD RpD(const u8* p) { return *(volatile const DWORD*)p; }
static inline u8* RpP(const u8* p) { return (u8*)(uintptr_t)*(volatile const DWORD*)p; }

// pointer -> pointer table (open addressing; key 1 = erased)
struct RpTab { DWORD* k; DWORD* v; DWORD cap, used; };
static DWORD RpTabH(DWORD key, DWORD cap) { return (key * 0x9E3779B1u >> 4) & (cap - 1); }
static void RpTabFree(RpTab& t) { RpFree(t.k); RpFree(t.v); t.k = t.v = NULL; t.cap = t.used = 0; }
static bool RpTabPut(RpTab& t, DWORD key, DWORD v);
// 0.35.12 (docs/94 §22.15, I-94-458): `used` counts erased slots too (a put reuses one only on its own probe path), so a
// table with churning keys (live footprint addresses: BP 0.35.9 freed 233k blocks) reached the grow limit with few live
// entries and doubled anyway: caps grew for the whole session and every scan (GC, repin, unify, map resync) with them.
// Now the rehash drops the erased slots and sizes the table to its live entries (4x, at least 1024 slots): it may shrink.
static DWORD RpTabLive(const RpTab& t) {
    DWORD n = 0;
    for (DWORD i = 0; i < t.cap; i++) n += t.k[i] > 1;
    return n;
}
static bool RpTabGrow(RpTab& t, DWORD more = 0) {  // more: entries about to be put (an empty table sized once)
    DWORD cap = 1024;
    for (const DWORD want = (RpTabLive(t) + more + 1) * 4; cap < want;) cap *= 2;
    RpTab n = {NULL, NULL, cap, 0};
    n.k = (DWORD*)RpAlloc(n.cap * 4), n.v = (DWORD*)RpAlloc(n.cap * 4);
    if (!n.k || !n.v) { RpFree(n.k); RpFree(n.v); return false; }
    for (DWORD i = 0; i < t.cap; i++) if (t.k[i] > 1) RpTabPut(n, t.k[i], t.v[i]);
    RpTabFree(t);
    t = n;
    return true;
}
// keys 0 (empty) and 1 (erased) are never stored: 0.29.2 looked up key 0 (an FME without a Q, "lot 0"), matched an empty
// slot and read its value 0 as "no copy" (BP 2026-10-19: 6 builds failed)
static bool RpTabPut(RpTab& t, DWORD key, DWORD v) {
    if (key <= 1) return false;
    if ((t.used + 1) * 2 > t.cap && !RpTabGrow(t)) return false;
    DWORD h = RpTabH(key, t.cap), at = 0xFFFFFFFF;
    for (;; h = (h + 1) & (t.cap - 1)) {
        if (t.k[h] == key) { t.v[h] = v; return true; }
        if (t.k[h] == 1 && at == 0xFFFFFFFF) at = h;
        if (!t.k[h]) break;
    }
    if (at == 0xFFFFFFFF) { at = h; t.used++; }
    t.k[at] = key, t.v[at] = v;
    return true;
}
static DWORD* RpTabFind(const RpTab& t, DWORD key) {
    if (!t.cap || key <= 1) return NULL;
    for (DWORD h = RpTabH(key, t.cap);; h = (h + 1) & (t.cap - 1)) {
        if (t.k[h] == key) return &t.v[h];
        if (!t.k[h]) return NULL;
    }
}

// --- footprint copies: one block per footprint and its children, header 16 bytes before the footprint ---
struct RpFpHdr { u8* live; DWORD bytes, nKids, pad; };
static const RpCls* RpClass(DWORD vt) {
    for (int i = 0; i < g_rpNCls; i++) if (g_rpCls[i].vt == vt) return &g_rpCls[i];
    return NULL;
}
static bool RpIsChild(const u8* f) { const RpCls* c = RpClass(RpD(f)); return c && c->child; }
static bool RpFpHasCache(DWORD vt) { const RpCls* c = RpClass(vt); return c && c->cache; }
static inline bool RpHasList(const u8* f) { return !RpIsChild(f); }  // the child class has no child list at +0x24
// unknown footprint classes met by a copy: first 4 vtables with counts (rpUnkClsVt / rpUnkClsN), each logged once
static void RpUnknownClass(DWORD vt) {
    Ts3nCounters& k = ts3native_counters;
    k.rpUnknownVt = (LONG)vt;
    for (int i = 0; i < 4; i++) {
        if ((DWORD)k.rpUnkClsVt[i] == vt) { InterlockedIncrement(&k.rpUnkClsN[i]); return; }
        if (!k.rpUnkClsVt[i] && !InterlockedCompareExchange(&k.rpUnkClsVt[i], (LONG)vt, 0)) {
            InterlockedIncrement(&k.rpUnkClsN[i]);
            Log("snap replay: footprint class 0x%08lX unknown: lots holding it are left out of the clone (rpExclLots)", vt);
            return;
        }
    }
}
static DWORD RpLevels(const u8* fp) { return ((RpVoidFn)(uintptr_t)RpD(RpP(fp)))(fp, NULL); }  // slot +0 (1 or 3)

// why a clone build failed or left a lot out: the first cause, logged by RpEpochBody (0.29.2; the BP run on 0.29.1 failed
// 6 builds with no fault, no refused copy and no unknown class, and the old log line could not say which check fired)
enum { RW_NONE, RW_FAULT, RW_ALLOC, RW_MAPPOOL, RW_LOTTABLE, RW_NQ, RW_ROOMS, RW_MAP1, RW_EXCL64, RW_LEAFONLY, RW_DEPTH,
       RW_LEAFCNT, RW_FMEQ, RW_UNKNOWN, RW_LEVELS, RW_INVALID, RW_POLYS, RW_VERTS, RW_KIDS, RW_CHILDALONE, RW_NOPARENT, RW_N };
static const char* const kRwName[RW_N] = {
    "none", "fault (a = exception code)", "allocation", "no private map pool", "lot table walk (a = lots, -1 = over 1024 or fault)",
    "lot with more than 16 Qs (a = Qs)", "room vectors not copied", "one-bucket map with entries (a = entries)",
    "more than 64 lots left out", "refused copy met only in a tree leaf (a = Qs added)", "tree deeper than 40",
    "leaf list over 65536 (a = count)", "FME of a rebase leaf on another lot's Q", "unknown footprint class",
    "level count or cache pointer (a = levels, b = cache)", "level not valid after the forced build (a = level)",
    "polygon vector shape (a = level, b = bytes)", "vertex vector shape (a = level, b = bytes)",
    "child list over 64 or deeper than 3 (a = children)", "child footprint copied on its own", "child whose +0xC parent is a child (b = its class)"};
struct RpWhy { int code; u64 lot; DWORD fp, vt, a, b; };
static RpWhy g_rpCopyWhy;  // the last refused footprint copy (any writing thread; diagnostics only)
static void RpCopyRefused(int code, const u8* f, DWORD a = 0, DWORD b = 0) {
    RpWhy w = {code, 0, (DWORD)(uintptr_t)f, 0, a, b};
    __try { w.vt = RpD(f); } __except (EXCEPTION_EXECUTE_HANDLER) {}
    g_rpCopyWhy = w;
}
static u64 RpQKey(DWORD q) {  // a live Q's lot id (+0x18 / +0x1C); 0 when unreadable
    // a freed Q (crash G log: 8 first-chance AVs on the consumer) is checked first, so it uses no first-chance log slot
    if (q && !Readable((const u8*)(uintptr_t)q + 0x18, 8)) return 0;
    __try { return q ? ((u64)RpD((const u8*)(uintptr_t)q + 0x1C) << 32) | RpD((const u8*)(uintptr_t)q + 0x18) : 0; }
    __except (EXCEPTION_EXECUTE_HANDLER) { return 0; }
}
static void RpWhyLog(const char* what, const RpWhy& w) {
    Log("snap replay: %s: %s (lot 0x%08lX%08lX, footprint 0x%08lX class 0x%08lX, a %ld, b %ld)", what,
        kRwName[w.code >= 0 && w.code < RW_N ? w.code : 0], (DWORD)(w.lot >> 32), (DWORD)w.lot, w.fp, w.vt, (LONG)w.a, (LONG)w.b);
}
// bytes of the block for fp and its children (footprints, level arrays, child list nodes; polygon and vertex arrays come
// from the game's allocator, as the game's own cache clear 0x61EE20 frees them); 0 = not copyable (unknown class, a level
// not valid after the forced build, shape caps). Forces the caches first, on the writing thread (never on the consumer):
// slot +0x70 is EnsureAllPolygons 0x80E610 exactly for the classes with a cache (docs/94 §15.1); the box class 0xFEAE08 has
// box floats at +0x38..+0x44 and is copied flat.
static DWORD RpFpMeasure(const u8* f, int depth, DWORD* kids) {
    const RpCls* c = RpClass(RpD(f));
    if (!c) { RpUnknownClass(RpD(f)); RpCopyRefused(RW_UNKNOWN, f); return 0; }
    if (c->child) {  // only inside its parent's block (a standalone copy is refused: rebase)
        if (!depth) RpCopyRefused(RW_CHILDALONE, f);
        return depth ? A16(c->size) : 0;
    }
    DWORD n = A16(c->size);
    if (c->cache) {
        ((RpVoidFn)(uintptr_t)RpD(RpP(f) + 0x70))(f, NULL);  // EnsureAllPolygons 0x80E610 on the live footprint (a W6 fill)
        const DWORD lv = RpLevels(f);
        const u8* lc = RpP(f + 0x38);
        if (!lc || lv == 0 || lv > 8) { RpCopyRefused(RW_LEVELS, f, lv, (DWORD)(uintptr_t)lc); return 0; }
        n += A16(lv * 0x24);
        for (DWORD l = 0; l < lv; l++) {
            const u8* e = lc + l * 0x24;
            if (!e[0x20]) { RpCopyRefused(RW_INVALID, f, l); return 0; }  // still invalid: a clone run would rebuild it (docs/82)
            const u8* pb = RpP(e);
            const u8* pe = RpP(e + 4);
            if (pe < pb || pe - pb > 16 * 4096 || (pe - pb) % 16) { RpCopyRefused(RW_POLYS, f, l, (DWORD)(pe - pb)); return 0; }
            for (const u8* r = pb; r < pe; r += 16) {
                const u8* vb = RpP(r);
                const u8* ve = RpP(r + 4);
                if (ve < vb || ve - vb > (1 << 20) || (ve - vb) % 8) { RpCopyRefused(RW_VERTS, f, l, (DWORD)(ve - vb)); return 0; }
            }
        }
    }
    const u8* head = f + 0x24;
    int nk = 0;
    for (const u8* l = RpP(head); l != head; l = RpP(l)) {
        if (!l || ++nk > 64 || depth >= 3) { RpCopyRefused(RW_KIDS, f, nk); return 0; }
        const DWORD m = RpFpMeasure(RpP(l + 8), depth + 1, kids);
        if (!m) return 0;
        n += 16 + 16 + m;  // list node, child header slot, child
        (*kids)++;
    }
    return n;
}
static u8* RpGameCopy(const u8* src, DWORD n, bool* ok) {  // a polygon or vertex array in the game's allocator (+0xC words kept)
    if (!n) return NULL;
    u8* d = (u8*)g_rpNew(n, *g_rpNewFlags, 0, 0, 0xF98980, 0xE7);
    if (!d) { *ok = false; return NULL; }
    CfRec(CF_COPY_NEW, d, n);
    memcpy(d, src, n);
    InterlockedExchangeAdd(&g_rpBytes, (LONG)n);  // counted with ours (ReplayCapMB)
    return d;
}
static void RpGameFree(u8* b, u8* e) { if (b) { InterlockedExchangeAdd(&g_rpBytes, -(LONG)(e - b)); CfFree(CF_COPY_FREE, b); g_rpDel(b); } }
static void RpFpArrays(u8* g) {  // frees the game-allocated arrays of one copied footprint (not its children)
    const RpCls* c = RpClass(*(const DWORD*)g);
    u8* nc = c && c->cache ? *(u8**)(g + 0x38) : NULL;
    if (!nc) return;
    const DWORD lv = RpLevels(g);
    for (DWORD l = 0; l < lv; l++) {
        u8* e = nc + l * 0x24;
        for (u8* r = *(u8**)e; r && r < *(u8**)(e + 4); r += 16) RpGameFree(*(u8**)r, *(u8**)(r + 4));
        RpGameFree(*(u8**)e, *(u8**)(e + 4));
        *(u8**)e = *(u8**)(e + 4) = *(u8**)(e + 8) = NULL;
    }
}
static u8* RpFpCopyInto(const u8* f, u8*& at, bool* ok) {
    const RpCls* c = RpClass(RpD(f));
    u8* g = at;
    at += A16(c->size);
    memcpy(g, f, c->size);
    *(LONG*)(g + 8) = RP_BIAS;
    if (c->child) return g;  // flat; the parent's loop re-points +0xC
    if (c->cache) {
        const DWORD lv = RpLevels(f);
        const u8* lc = RpP(f + 0x38);
        u8* nc = at;
        at += A16(lv * 0x24);
        memcpy(nc, lc, lv * 0x24);
        for (DWORD l = 0; l < lv; l++) { u8* e = nc + l * 0x24; *(u8**)e = *(u8**)(e + 4) = *(u8**)(e + 8) = NULL; }
        *(u8**)(g + 0x38) = nc;
        for (DWORD l = 0; l < lv && *ok; l++) {
            u8* e = nc + l * 0x24;
            const u8* le = lc + l * 0x24;
            const u8* pb = RpP(le);
            const DWORD pn = (DWORD)(RpP(le + 4) - pb);
            u8* np = RpGameCopy(pb, pn, ok);
            if (!np) continue;
            for (DWORD r = 0; r < pn; r += 16) *(u8**)(np + r) = *(u8**)(np + r + 4) = *(u8**)(np + r + 8) = NULL;
            *(u8**)e = np, *(u8**)(e + 4) = np + pn, *(u8**)(e + 8) = np + pn;
            for (DWORD r = 0; r < pn && *ok; r += 16) {
                const u8* vb = RpP(pb + r);
                const DWORD vn = (DWORD)(RpP(pb + r + 4) - vb);
                u8* nv = RpGameCopy(vb, vn, ok);
                *(u8**)(np + r) = nv, *(u8**)(np + r + 4) = nv + vn, *(u8**)(np + r + 8) = nv + vn;
            }
        }
    }
    u8* head = g + 0x24;  // children list {next, prev}; nodes {next, prev, footprint} (0x8075F0)
    *(u8**)head = head, *(u8**)(head + 4) = head;
    for (const u8* l = RpP(f + 0x24); l != f + 0x24; l = RpP(l)) {
        u8* ln = at;
        at += 16;
        at += 16;  // keeps the child 16-aligned
        u8* ch = RpFpCopyInto(RpP(l + 8), at, ok);
        if (RpIsChild(ch) && RpP(ch + 0xC) == f) *(u8**)(ch + 0xC) = g;  // the back pointer: the parent's copy
        u8* prev = *(u8**)(head + 4);
        *(u8**)ln = head, *(u8**)(ln + 4) = prev, *(u8**)(ln + 8) = ch, *(u8**)(ln + 12) = RpP(l + 8);  // +12: the live child
        *(u8**)prev = ln, *(u8**)(head + 4) = ln;
    }
    return g;
}
static void RpFpArraysDeep(u8* g) {
    RpFpArrays(g);
    if (!RpHasList(g)) return;
    for (u8* l = *(u8**)(g + 0x24); l && l != g + 0x24; l = *(u8**)l) if (*(u8**)(l + 8)) RpFpArraysDeep(*(u8**)(l + 8));
}
static void RpFpFreeBlock(u8* fp) { if (fp) { RpFpArraysDeep(fp); RpFree(fp - 16); } }
// fresh clone of a live footprint (any thread; the writer's own thread at an INS); NULL = not copyable (counted)
static u8* RpFpCopy(u8* live) {
    Ts3nCounters& k = ts3native_counters;
    LARGE_INTEGER t0;
    QueryPerformanceCounter(&t0);
    const LONG64 cy0 = CycNow();
    u8* fp = NULL;
    RpCopyRefused(RW_FAULT, live);  // replaced by RpFpMeasure's reason, or kept when the copy faults
    __try {
        DWORD kids = 0;
        const DWORD n = live ? RpFpMeasure(live, 0, &kids) : 0;
        RpFpHdr* h = n ? (RpFpHdr*)RpAlloc(16 + n) : NULL;
        if (h) {
            u8* at = (u8*)h + 16;
            bool ok = true;
            fp = RpFpCopyInto(live, at, &ok);
            h->live = live, h->bytes = n, h->nKids = kids;
            if (!ok) { RpFpFreeBlock(fp); fp = NULL; RpCopyRefused(RW_ALLOC, live, 1); }
        } else if (n) RpCopyRefused(RW_ALLOC, live, 0, n);
    } __except (EXCEPTION_EXECUTE_HANDLER) { fp = NULL; }  // shortcut: a fault inside the copy leaks that block (counted as a refused copy)
    if (!fp) InterlockedIncrement(&k.rpCopyFail); else InterlockedIncrement(&k.rpFps);
    InterlockedExchangeAdd(&k.rpCopyUs, UsSince(t0));
    InterlockedExchangeAdd64(&g_cyCopy, CycNow() - cy0);
    return fp;
}
// A child footprint (0xFEBF90) that its parent's list does not hold gets a block of its own, +0xC = the parent's copy, which
// it pins (+8) so the GC keeps the parent while the child lives (hdr.pad = 1). Its slots forward to +0xC unchecked
// (0x626210 and 21 others [verified]), so the parent must stay. Mass frees (whole clone, whole batch) skip the unpin.
static __declspec(thread) bool g_rpMassFree;  // per thread: the consumer's GC and the sim's batch drop may overlap
static void RpFpFree(u8* fp) {
    if (!fp) return;
    if (((RpFpHdr*)(fp - 16))->pad == 1 && !g_rpMassFree)
        if (u8* par = *(u8**)(fp + 0xC)) (*(LONG*)(par + 8))--;
    RpFpFreeBlock(fp);
    InterlockedDecrement(&ts3native_counters.rpFps);
}
static u8* RpFpCopyChild(const u8* live, u8* parCopy) {
    const RpCls* c = RpClass(RpD(live));
    RpFpHdr* h = (RpFpHdr*)RpAlloc(16 + A16(c->size));
    if (!h) { RpCopyRefused(RW_ALLOC, live, 2); return NULL; }
    u8* g = (u8*)h + 16;
    memcpy(g, live, c->size);
    *(LONG*)(g + 8) = RP_BIAS;
    *(u8**)(g + 0xC) = parCopy;
    if (parCopy) (*(LONG*)(parCopy + 8))++;
    h->live = (u8*)live, h->bytes = A16(c->size), h->nKids = 0, h->pad = 1;
    InterlockedIncrement(&ts3native_counters.rpFps);
    return g;
}
// children of a copy (list node +12 holds the live child) in or out of a live -> clone table. Out only when the entry is this
// copy's child (0.35.8, I-94-440): a live child freed after the copy was made and its address reused by a new child of
// another parent (the 0x28-byte pool 0x1165B80) maps to that parent's copy now; retiring or freeing the old block deleted
// that entry, and the new child's FMEs lost their clone partner ("clone copy: none", BP 0.35.6: 692 fresh soft misses)
static void RpKids(RpTab& kids, const u8* fp, bool put) {
    if (!RpHasList(fp)) return;
    for (const u8* l = *(u8* const*)(fp + 0x24); l != fp + 0x24; l = *(u8* const*)l) {
        u8* ch = *(u8* const*)(l + 8);
        if (put) RpTabPut(kids, *(const DWORD*)(l + 12), (DWORD)(uintptr_t)ch);
        else if (DWORD* v = RpTabFind(kids, *(const DWORD*)(l + 12))) {
            if (*v == (DWORD)(uintptr_t)ch) kids.k[v - kids.v] = 1;
            else ts3native_counters.rpKidsKept++;
        }
        RpKids(kids, ch, put);
    }
}
// references the clone holds on fp and its children (FMEs; the bias excluded)
static LONG RpFpRefs(const u8* fp) {
    LONG r = *(const LONG*)(fp + 8) - RP_BIAS;
    if (!RpHasList(fp)) return r;
    for (const u8* l = *(u8* const*)(fp + 0x24); l != fp + 0x24; l = *(u8* const*)l) r += RpFpRefs(*(u8* const*)(l + 8));
    return r;
}

// --- the clone ---
// prefs: the portal references this lot copy holds (H20b, 0.30.0): one per non-null element of the lot graph's portal list +0x44,
// its pending adds +0x54 and every room record's portal vectors +4 / +0x14, taken at the copy and released at RpLotFree
struct RpLot { u64 key; u8* node; int nQ; u8* q[16]; DWORD liveQ[16]; bool stale, rooms; u8** prefs; int nPrefs; u8 hashes; };
struct RpClone {
    u8* p; u8* m; u8* dummyQ;
    DWORD* lotBk; DWORD lotN;
    RpLot* lots; int nLots, capLots;
    RpTab fps;   // live footprint -> clone footprint block (top level; a block holds its children)
    RpTab kids;  // live child footprint -> its clone inside the parent's block (children are inserted by their parent)
    RpTab old;   // replaced blocks some FMEs still hold (a dropped child's): freed by the GC at no reference, or with the clone
    RpTab oldLive;  // the live footprints those blocks copy (a fresh copy of one of them unifies the FMEs)
    u64 ex[64]; int nEx;  // lots left out at the build (a footprint the copy refuses, e.g. an unknown class): rpExclLots
    PlSeen seen;          // the game's pool objects at the build (pools.cpp): a change drops this clone at the drain
};

static u8* RpGraph(u8* node) { return node + 0x10; }

// H20 (docs/109 §8.9, docs/94 §21.8): the lot graph and room vectors hold counted pointers to LIVE portal objects (vtable
// 0xFED048: AddRef slot 0 = 0x64E8E0, Release slot +4 = 0x64E8F0, count +0x70, deleting slot +8 at a count <= 1; verified
// disassembly, Steam 1.67). With g_rpPortalRefs (set by P4 once the interlocked pair is in the vtable, H20a) each lot copy
// takes one reference per element it holds, so a sim-side Release (RemovePortals, a lot rebuild) never deletes a portal the
// clone points at. Without it (P3-B audit only: nothing follows the pointers) no reference is taken. The calls go through
// the element's own vtable, as the game's vector copy 0x9EC420 and release 0x5BF1E0 do.
static bool g_rpPortalRefs;
typedef LONG(__fastcall* RpRefFn)(u8* self, void*);
static void RpPortalRefsTake(RpLot* l, u8* g) {
    int n = 0;
    const DWORD vecs[2] = {0x44, 0x54};
    for (int v = 0; v < 2; v++) n += (int)((RpP(g + vecs[v] + 4) - RpP(g + vecs[v])) / 4);
    for (u8* r = RpP(g + 0x90); r && r + 0x44 <= RpP(g + 0x94); r += 0x44) n += (int)((RpP(r + 8) - RpP(r + 4)) / 4 + (RpP(r + 0x18) - RpP(r + 0x14)) / 4);
    l->prefs = n ? (u8**)RpAlloc(n * 4) : NULL;
    if (!l->prefs) return;
    auto take = [&](const u8* b, const u8* e) {
        for (const u8* p = b; p && p < e; p += 4)
            if (u8* pt = RpP(p)) {
                ((RpRefFn)(uintptr_t)RpD(RpP(pt)))(pt, NULL);
                l->prefs[l->nPrefs++] = pt;
                InterlockedIncrement(&ts3native_counters.rpPortalRefs);  // per reference: a fault mid-way leaves the gauge right
            }
    };
    for (int v = 0; v < 2; v++) take(RpP(g + vecs[v]), RpP(g + vecs[v] + 4));
    for (u8* r = RpP(g + 0x90); r && r + 0x44 <= RpP(g + 0x94); r += 0x44) take(RpP(r + 4), RpP(r + 8)), take(RpP(r + 0x14), RpP(r + 0x18));
}
static void RpPortalRefsDrop(RpLot* l) {
    __try {
        for (int i = 0; i < l->nPrefs; i++) {
            ((RpRefFn)(uintptr_t)RpD(RpP(l->prefs[i]) + 4))(l->prefs[i], NULL);
            InterlockedDecrement(&ts3native_counters.rpPortalRefs);
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) { ts3native_counters.rpFaults++; }
    RpFree(l->prefs);
    l->prefs = NULL, l->nPrefs = 0;
}
static bool RpVecCopy(u8* g, DWORD off, DWORD elem) {  // vector begin/end/cap at g+off: our own copy of begin..end
    const u8* b = RpP(g + off);
    const u8* e = RpP(g + off + 4);
    const DWORD n = b && e > b && (DWORD)(e - b) <= elem * 65536 ? (DWORD)(e - b) : 0;
    u8* c = (u8*)RpAlloc(n ? n : 4);
    if (!c) return false;
    if (n) memcpy(c, b, n);
    *(u8**)(g + off) = c, *(u8**)(g + off + 4) = c + n, *(u8**)(g + off + 8) = c + n;
    return true;
}
// crash H (docs/67 §10, 0.32.6): a vector the game itself grows or clears in a clone run must be an EA block, as the game's
// vector grow allocates it (0x4E3F90 with the vector's own +0xC word, 0x5D1DCD); an RpAlloc block there reaches EA's free
// (0x4E3F70) with no EA chunk header. Empty: NULL (the game's grow and clear accept it, 0x5D1D83 / 0x61EE2A). Freed with g_rpDel
static bool RpVecCopyEA(u8* g, DWORD off, DWORD elem) {
    const u8* b = RpP(g + off);
    const u8* e = RpP(g + off + 4);
    const DWORD n = b && e > b && (DWORD)(e - b) <= elem * 65536 ? (DWORD)(e - b) : 0;
    u8* c = n ? (u8*)g_rpNew(n, RpD(g + off + 0xC), 0, 0, 0xF98980, 0xE7) : NULL;
    if (n && !c) return false;
    if (n) CfRec(CF_VEC_NEW, c, n), memcpy(c, b, n);
    *(u8**)(g + off) = c, *(u8**)(g + off + 4) = c + n, *(u8**)(g + off + 8) = c + n;
    return true;
}
// a room record's +0x24 (u16 portal index list) and +0x34 (one 16-byte vector of 8-byte entries per portal). RoomGraph_FirstUse
// 0x63D2F0 on an unbuilt room (never built, or reset by 0x630F70, which clears only the built byte) empties +0x24, EA-frees every
// +0x34 element's buffer (0x61EE20), resizes +0x34 (0x622A30) and pushes into both (0x5D1D60, 0x6202E0) [verified disassembly].
// So both levels are private EA blocks: the flat 0.29.0 copy left the inner buffers live (I-94-133: a clone run freed live
// blocks) and the outer ones in our heap (crash H)
static bool RpRoomListsCopy(u8* r) {
    if (!RpVecCopyEA(r, 0x24, 2) || !RpVecCopyEA(r, 0x34, 16)) return false;
    for (u8* e = RpP(r + 0x34); e && e < RpP(r + 0x38); e += 16)
        if (!RpVecCopyEA(e, 0, 8)) return false;  // the rest stay live: l->rooms false, the build fails, nothing is freed
    return true;
}
static void RpRoomListsFree(u8* r) {
    for (u8* e = RpP(r + 0x34); e && e < RpP(r + 0x38); e += 16) CfFree(CF_ROOM_FREE, RpP(e)), g_rpDel(RpP(e));
    CfFree(CF_ROOM_FREE, RpP(r + 0x34)), g_rpDel(RpP(r + 0x34)), CfFree(CF_ROOM_FREE, RpP(r + 0x24)), g_rpDel(RpP(r + 0x24));
}
// I-94-321 (0.33.4, steerro-test K8, docs/94 §13.3): the lot graph's two hashes g+0x120 (lot-edge waypoint key -> exit portal
// index: LotEdge_WaypointPosition 0x653020, the closest-waypoint gather 0x654C04) and g+0x140 (0x63C326) are 0x20-byte EASTL
// hashes (+4 buckets, +8 count, +0xC size, +0x1C allocator tag; 12-byte nodes key, value, next +8) that the game's own lot graph
// copy 0x62DE40 deep-copies through 0x6267E0. The shell's byte copy kept the live bucket array, so a clone run read live nodes,
// which the sim thread clears and refills (0x656250 at a lot build, LoadLotData, a build/buy exit) [verified disassembly]. Our
// copy: EA blocks as 0x6267E0 makes them (buckets with flag 0x400000, the end marker kept, chain order kept). A NULL bucket
// array (the tests' fake lots) is left as it is
static bool RpHashCopyEA(u8* h) {
    const DWORD* bk = (const DWORD*)RpP(h + 4);
    const DWORD n = RpD(h + 8);
    if (!bk) return true;
    if (!n || n > (1u << 20)) return false;
    DWORD* c = (DWORD*)g_rpNew((n + 1) * 4, RpD(h + 0x1C), 0x400000, 0, 0xF98980, 0xE7);
    if (!c) return false;
    CfRec(CF_HASH_NEW, c, (n + 1) * 4);
    memset(c, 0, n * 4);
    c[n] = bk[n];
    *(DWORD**)(h + 4) = c;
    for (DWORD i = 0; i < n; i++) {
        DWORD* link = &c[i];
        for (const u8* nd = (const u8*)(uintptr_t)bk[i]; nd; nd = RpP(nd + 8)) {
            u8* m = (u8*)g_rpNew(12, RpD(h + 0x1C), 0, 0, 0xF98980, 0xE7);
            if (!m) return false;  // the chain ends here: private, short (the caller fails the build)
            CfRec(CF_HNODE_NEW, m, 12);
            memcpy(m, nd, 8);
            *(DWORD*)(m + 8) = 0;
            *link = (DWORD)(uintptr_t)m;
            link = (DWORD*)(m + 8);
        }
    }
    return true;
}
static void RpHashFreeEA(u8* h) {
    DWORD* bk = (DWORD*)RpP(h + 4);
    if (!bk) return;
    for (DWORD i = 0; i < RpD(h + 8); i++)
        for (u8* nd = (u8*)(uintptr_t)bk[i]; nd;) { u8* nx = RpP(nd + 8); CfFree(CF_HASH_FREE, nd), g_rpDel(nd); nd = nx; }
    CfFree(CF_HASH_FREE, bk), g_rpDel(bk);
}

// build context: an epoch build (whole clone) or a rebase batch (some lots against the clone's P)
struct RpCtx {
    u8* cp; u8* dummyQ;
    RpTab qMap;    // live Q -> clone Q
    RpTab fmeMap;  // live FME -> clone FME
    RpTab fps;     // live footprint -> clone footprint block (epoch: the clone's table; batch: its own)
    RpTab kids;    // live child -> clone child inside a block
    bool fail;
    bool excl;     // a refused footprint copy leaves its FME's lot out (exQ: live Qs) instead of failing (epoch build, soft refresh)
    RpTab exQ;
    RpTab bad;     // live footprint -> why its copy was refused (RW_*): refused once per build or batch, not once per FME / leaf
    RpWhy why;     // first cause of x.fail
    RpWhy exWhy;   // first cause of a lot left out (exQ)
    u64 lot;       // the lot being shelled or filled (for RpWhy)
    int nAlone;    // child footprints copied in a block of their own (not in their parent's list)
    int nNoQ;      // FMEs without a Q (world-level hanging footprints, 0x6377F0) copied
};
static void RpFailW(RpCtx& x, int code, u64 lot = 0, DWORD a = 0, DWORD b = 0) {
    if (!x.why.code) x.why = {code, lot, 0, 0, a, b};
    x.fail = true;
}
// a refused footprint (g_rpCopyWhy holds the reason) leaves the lot of live Q q out, or fails a build that does not exclude
static void RpRefused(RpCtx& x, DWORD q) {
    RpWhy w = g_rpCopyWhy;
    w.lot = RpQKey(q);
    if (x.excl && q) {  // an FME without a Q (world-level) has no lot to leave out: the build fails, named
        if (!x.exWhy.code) x.exWhy = w;
        RpTabPut(x.exQ, q, 1);
    } else {
        if (!x.why.code) x.why = w;
        x.fail = true;
    }
}
static u8* RpFpFor(RpCtx& x, u8* live, DWORD q) {
    if (!live) return NULL;  // key 0 would match an empty table slot
    if (DWORD* v = RpTabFind(x.kids, (DWORD)(uintptr_t)live)) return (u8*)(uintptr_t)*v;
    if (DWORD* v = RpTabFind(x.fps, (DWORD)(uintptr_t)live)) return (u8*)(uintptr_t)*v;
    if (RpIsChild(live)) {  // inside its parent's block when the parent's list holds it (+0xC is the parent)
        u8* par = RpP(live + 0xC);
        u8* pc = par && !RpIsChild(par) ? RpFpFor(x, par, q) : NULL;
        if (pc)
            if (DWORD* v = RpTabFind(x.kids, (DWORD)(uintptr_t)live)) return (u8*)(uintptr_t)*v;
        if (par && !pc && !RpIsChild(par)) return NULL;  // a refused parent has left q's lot out already
        if (par && !pc) { RpCopyRefused(RW_NOPARENT, live, 1, RpD(par)); RpRefused(x, q); return NULL; }  // parent is a child
        // not in its parent's list (BP 0.29.2: 209 lots left out; e.g. children linked by the load fix-up 0x806ED0, which
        // sets +0xC from an id lookup, or created alone by the factory, "Routing/ExtensionFootprint" 0x6261F0): own block
        u8* c = RpFpCopyChild(live, pc);
        if (!c) { RpRefused(x, q); return NULL; }
        if (!RpTabPut(x.fps, (DWORD)(uintptr_t)live, (DWORD)(uintptr_t)c)) { RpFpFree(c); RpFailW(x, RW_ALLOC); return NULL; }
        x.nAlone++;
        return c;
    }
    if (DWORD* b = RpTabFind(x.bad, (DWORD)(uintptr_t)live)) {  // refused before in this build: same reason, no second copy
        RpCopyRefused((int)*b, live);
        RpRefused(x, q);
        return NULL;
    }
    u8* c = RpFpCopy(live);
    if (!c) {
        RpTabPut(x.bad, (DWORD)(uintptr_t)live, (DWORD)g_rpCopyWhy.code);
        RpRefused(x, q);
        return NULL;
    }
    if (!RpTabPut(x.fps, (DWORD)(uintptr_t)live, (DWORD)(uintptr_t)c)) { RpFpFree(c); RpFailW(x, RW_ALLOC); return NULL; }
    RpKids(x.kids, c, true);
    return c;
}
// parents before their children: a child met first would get a block of its own besides the parent's copy
static void RpPreParent(RpCtx& x, const u8* liveFme) {
    u8* fp = liveFme ? RpP(liveFme + 8) : NULL;
    if (!fp) return;
    u8* top = RpIsChild(fp) ? RpP(fp + 0xC) : RpP(fp + 0x24) != fp + 0x24 ? fp : NULL;
    if (top) RpFpFor(x, top, RpD(liveFme + 0xC));
}
// clone FME of a live one (reference count 0; the map node adds its one). An FME without a footprint (+8 = 0) is copied as
// it is (0.29.2): before, the table lookup of key 0 hit an empty slot and returned "no copy", which left its lot out, and in
// a tree leaf only failed the whole build
static u8* RpFme(RpCtx& x, const u8* live) {
    if (DWORD* v = RpTabFind(x.fmeMap, (DWORD)(uintptr_t)live)) return (u8*)(uintptr_t)*v;
    u8* lfp = RpP(live + 8);
    u8* fp = lfp ? RpFpFor(x, lfp, RpD(live + 0xC)) : NULL;
    if (lfp && !fp) return NULL;
    u8* f = (u8*)g_rpFmePool(0x14, 0xFEAC80, 0, 0, 0, 0);
    if (!f) { RpFailW(x, RW_ALLOC, RpQKey(RpD(live + 0xC))); return NULL; }
    memcpy(f, live, 0x14);
    *(LONG*)(f + 4) = 0;
    *(u8**)(f + 8) = fp;
    if (fp) (*(LONG*)(fp + 8))++;  // as the game's +0x90 (0x915880)
    const DWORD lq = RpD(live + 0xC);  // 0: a world-level hanging footprint's FME (0x6377F0 clears it) [verified]: kept 0
    const DWORD* q = RpTabFind(x.qMap, lq);
    *(u8**)(f + 0xC) = !lq ? NULL : q ? (u8*)(uintptr_t)*q : x.dummyQ;
    if (!lq) x.nNoQ++;
    else if (!RpP(f + 0xC)) RpFailW(x, RW_FMEQ, RpQKey(RpD(live + 0xC)));  // a rebase leaf that holds another lot's FME
    if (!RpTabPut(x.fmeMap, (DWORD)(uintptr_t)live, (DWORD)(uintptr_t)f)) RpFailW(x, RW_ALLOC);
    return f;
}
// footprint copies for every FME in a live tree before the lots are filled, so a refusal met only in a leaf (an FME that no
// map node holds) leaves its lot out like one met in the map, instead of failing the whole build (0.29.2)
static void RpPreTree(RpCtx& x, const u8* n, int depth) {
    if (!n || x.fail || depth > 40) return;
    if (n[4] & 1) { for (int i = 0; i < 4; i++) RpPreTree(x, RpP(n + 0xC + 4 * i), depth + 1); return; }
    const bool heap = (n[8] & 1) != 0;
    const DWORD cnt = heap ? RpD(n + 0xC) : 1;
    const u8* a = heap ? (const u8*)(uintptr_t)(RpD(n + 8) & ~1u) : n + 8;
    for (DWORD j = 0; j < cnt && j <= 65536; j++) {
        const u8* f = RpP(a + j * 12);
        if (!f || !RpP(f + 8) || RpTabFind(x.fmeMap, (DWORD)(uintptr_t)f)) continue;
        RpPreParent(x, f);
        RpFpFor(x, RpP(f + 8), RpD(f + 0xC));
    }
}
static u8* RpTree(RpCtx& x, const u8* n, int depth, u8* parent = NULL) {
    if (!n || x.fail) return NULL;
    if (depth > 40) { RpFailW(x, RW_DEPTH, x.lot, depth); return NULL; }
    if (n[4] & 1) {  // inner (0x628B70): children +0xC..+0x18
        u8* m = (u8*)PlAlloc(PL_INNER);  // private pool (pools.cpp; the pop site 0x667990 would pick it on this thread too)
        if (!m) { RpFailW(x, RW_ALLOC, x.lot, 1); return NULL; }
        memcpy(m, n, 0x1C);
        *(u8**)(m + 8) = parent;  // the clone's parent, not the live one (inner +8; the root's is 0), I-94-193
        m[7] = 1;
        for (int i = 0; i < 4; i++) *(u8**)(m + 0xC + 4 * i) = RpTree(x, RpP(n + 0xC + 4 * i), depth + 1, m);
        return m;
    }
    u8* m = (u8*)PlAlloc(PL_LEAF);  // leaf (0x667B10), private pool
    if (!m) { RpFailW(x, RW_ALLOC, x.lot, 2); return NULL; }
    memcpy(m, n, 0x14);
    m[7] = 1;
    if (m[8] & 1) {  // heap list (ptr | 1, count +0xC), exact size as 0x6636B0 allocates it
        const DWORD cnt = RpD(n + 0xC);
        const u8* src = (const u8*)(uintptr_t)(RpD(n + 8) & ~1u);
        if (cnt) {
            if (cnt > 65536) { RpFailW(x, RW_LEAFCNT, x.lot, cnt); return m; }
            u8* arr = (u8*)g_rpNew(cnt * 12, *g_rpNewFlags, 0, 0, 0xF98980, 0xE7);
            if (!arr) { *(DWORD*)(m + 8) = 1, *(DWORD*)(m + 0xC) = 0; RpFailW(x, RW_ALLOC, x.lot, 3); return m; }
            CfRec(CF_LEAF_NEW, arr, cnt * 12);
            for (DWORD j = 0; j < cnt; j++) {
                memcpy(arr + j * 12, src + j * 12, 12);
                const u8* f = RpP(src + j * 12);
                if (f) {
                    u8* cf = RpFme(x, f);
                    if (cf && !*(LONG*)(cf + 4)) { *(LONG*)(cf + 4) = 1; ts3native_counters.rpOrphanFme++; }  // not in the map yet
                    *(u8**)(arr + j * 12) = cf;
                }
            }
            *(DWORD*)(m + 8) = (DWORD)(uintptr_t)arr | 1;
        }
    } else if (const u8* f = RpP(n + 8)) {
        u8* cf = RpFme(x, f);
        if (cf && !*(LONG*)(cf + 4)) { *(LONG*)(cf + 4) = 1; ts3native_counters.rpOrphanFme++; }
        *(u8**)(m + 8) = cf;
    }
    return m;
}
// tree release as 0x664CE0: the root's reference, its deleting destructor frees the subtree into the pools
static void RpTreeRel(u8* q) {
    u8* r = *(u8**)(q + 0x24);
    *(u8**)(q + 0x24) = NULL;
    if (r && --r[7] == 0) { r[7] = 1; ((RpDtorFn)(uintptr_t)RpD(RpP(r)))(r, NULL, 1); }
}
// lot node copy with Q shells (trees filled after the map, so FMEs found in the map get their map reference first)
// qs = false (a shell refresh, 0.35.17): no Q copies; l->q stays empty and the node's Q vector keeps the live Qs until the
// consumer puts the clone's own Qs of that lot in it (RpApplyBatch)
static u8* RpLotShell(RpCtx& x, const u8* live, RpLot* l, bool qs = true) {
    x.lot = ((u64)RpD(live + 4) << 32) | RpD(live);
    u8* node = (u8*)RpAlloc(kRpNodeSz);
    if (!node) { RpFailW(x, RW_ALLOC, x.lot, 4); return NULL; }
    memcpy(node, live, kRpNodeSz);
    *(DWORD*)(node + 0x190) = 0;
    u8* g = RpGraph(node);
    // +0x64 holds 8-byte portal ids (pending removes, 0x639A80 walks it with sar 3), not pointers [verified disassembly]
    const bool pv = RpVecCopy(g, 0x44, 4) && RpVecCopy(g, 0x54, 4);
    if (!pv || !RpVecCopy(g, 0x64, 4) || !RpVecCopy(g, 0x74, 4)) RpFailW(x, RW_ALLOC, x.lot, 5);
    // rooms (P4, 0.29.0): the 0x44-byte room records +0x90 with their portal index list +0x24 and connectivity lists +0x34,
    // so a clone search reads no live room vector (docs/94 §16.5). +0x34 is a vector of vectors: deep-copied, both lists as EA
    // blocks since 0.32.6 (I-94-133 refuted, crash H: RpRoomListsCopy).
    // H20b (0.30.0): the portal vectors +4 and +0x14 (counted portal pointers; the room copy 0x6223C0 AddRefs each element
    // through 0x9EC420, the room destroy 0x621D20 releases them through 0x5BF1E0 [verified disassembly]) are copied too
    bool rooms = RpVecCopy(g, 0x90, 0x44);
    for (u8* r = rooms ? RpP(g + 0x90) : NULL; r && r + 0x44 <= RpP(g + 0x94); r += 0x44)
        rooms = rooms && RpVecCopy(r, 4, 4) && RpVecCopy(r, 0x14, 4) && RpRoomListsCopy(r);  // 0.32.6: +0x24 / +0x34 EA, deep
    l->rooms = rooms;  // false: some room vectors stay live (leaked copies, never a free of a live pointer)
    if (!rooms) RpFailW(x, RW_ROOMS, x.lot, (DWORD)((RpP(g + 0x94) - RpP(g + 0x90)) / 0x44));
    // I-94-321: the two lot hashes (bit set = ours to free)
    l->hashes = 0;
    for (int h = 0; h < 2; h++) {
        const DWORD liveBk = RpD(g + 0x124 + 0x20 * h);
        if (!RpHashCopyEA(g + 0x120 + 0x20 * h)) RpFailW(x, RW_ALLOC, x.lot, 8);
        if (RpD(g + 0x124 + 0x20 * h) != liveBk) l->hashes |= (u8)(1 << h);  // our bucket array (even a short one)
    }
    l->key = ((u64)RpD(live + 4) << 32) | RpD(live);
    l->node = node, l->stale = false;
    l->prefs = NULL, l->nPrefs = 0;
    if (g_rpPortalRefs && pv && rooms) RpPortalRefsTake(l, g);  // only private vectors: a live one is never ours to count
    u8** qv = *(u8***)(g + 0x74);
    l->nQ = (int)(*(u8***)(g + 0x78) - qv);
    if (l->nQ > 16) { RpFailW(x, RW_NQ, x.lot, l->nQ); l->nQ = 0; }
    for (int i = 0; i < l->nQ; i++) {
        const u8* lq = qv[i];
        l->liveQ[i] = (DWORD)(uintptr_t)lq;
        l->q[i] = NULL;
        if (!lq || !qs) continue;
        u8* q = (u8*)RpAlloc(kRpQSz);
        if (!q) { RpFailW(x, RW_ALLOC, x.lot, 6); continue; }
        memcpy(q, lq, kRpQSz);
        *(u8**)(q + 0x20) = x.cp, *(u8**)(q + 0x24) = NULL, *(LONG*)(q + 0x34) = RP_BIAS, *(LONG*)(q + 0x48) = 0, q[0x4C] = 0;
        qv[i] = q;
        l->q[i] = q;
        if (!RpTabPut(x.qMap, (DWORD)(uintptr_t)lq, (DWORD)(uintptr_t)q)) RpFailW(x, RW_ALLOC, x.lot, 7);
    }
    return node;
}
static void RpLotPre(RpCtx& x, const RpLot* l) {  // RpPreTree over the lot's live trees
    for (int i = 0; i < l->nQ; i++) if (l->liveQ[i]) RpPreTree(x, RpP((const u8*)(uintptr_t)l->liveQ[i] + 0x24), 0);
}
static void RpLotFill(RpCtx& x, RpLot* l) {
    x.lot = l->key;
    for (int i = 0; i < l->nQ; i++)
        if (l->q[i]) *(u8**)(l->q[i] + 0x24) = RpTree(x, RpP((const u8*)(uintptr_t)l->liveQ[i] + 0x24), 0);
}
static void RpLotFree(RpLot* l) {  // trees released first; the caller has dropped the map nodes that reference its Qs
    for (int i = 0; i < l->nQ; i++) if (l->q[i]) RpTreeRel(l->q[i]);
    for (int i = 0; i < l->nQ; i++) RpFree(l->q[i]);
    if (l->prefs) RpPortalRefsDrop(l);
    if (l->node) {
        u8* g = RpGraph(l->node);
        RpFree(RpP(g + 0x44)), RpFree(RpP(g + 0x54)), RpFree(RpP(g + 0x64)), RpFree(RpP(g + 0x74));
        if (l->rooms) {  // ours only when the shell copied them (RpVecCopy failures leave live pointers: never freed)
            for (u8* r = RpP(g + 0x90); r && r + 0x44 <= RpP(g + 0x94); r += 0x44)
                RpFree(RpP(r + 4)), RpFree(RpP(r + 0x14)), RpRoomListsFree(r);
            RpFree(RpP(g + 0x90));
        }
        for (int h = 0; h < 2; h++) if (l->hashes & (1 << h)) RpHashFreeEA(g + 0x120 + 0x20 * h);
        RpFree(l->node);
    }
    l->node = NULL, l->nQ = 0;
}

// lot table P+0x48: buckets +4 (P+0x4C), count +8 (P+0x50), sentinel buckets[count], chain +0x190
struct RpLiveLot { u64 key; u8* node; };
static int RpLiveLots(const u8* p, RpLiveLot* out, int cap) {
    int n = 0;
    __try {
        const DWORD* bk = (const DWORD*)RpP(p + 0x4C);
        const DWORD nb = RpD(p + 0x50);
        if (!bk || nb > (1u << 20)) return 0;
        const DWORD sentinel = bk[nb];
        for (DWORD i = 0; i < nb; i++)
            for (u8* nd = (u8*)(uintptr_t)bk[i]; nd && (DWORD)(uintptr_t)nd != sentinel; nd = RpP(nd + 0x190)) {
                if (n == cap) return -1;
                out[n].key = ((u64)RpD(nd + 4) << 32) | RpD(nd), out[n].node = nd;
                n++;
            }
    } __except (EXCEPTION_EXECUTE_HANDLER) { return -1; }
    return n;
}
static const int kRpMaxLots = 1024;
static RpLiveLot g_rpLive[kRpMaxLots];  // sim thread (cuts)

static void RpTableLink(RpClone* c, RpLot* l) {
    const DWORD b = (DWORD)l->key % (c->lotN ? c->lotN : 1);
    *(DWORD*)(l->node + 0x190) = c->lotBk[b];
    c->lotBk[b] = (DWORD)(uintptr_t)l->node;
}
static void RpTableUnlink(RpClone* c, RpLot* l) {
    DWORD* at = &c->lotBk[(DWORD)l->key % (c->lotN ? c->lotN : 1)];
    while (*at && *at != (DWORD)(uintptr_t)l->node) at = (DWORD*)(uintptr_t)(*at + 0x190);
    if (*at) *at = *(DWORD*)(l->node + 0x190);
}
static RpLot* RpLotAdd(RpClone* c) {
    if (c->nLots == c->capLots) {
        const int cap = c->capLots ? c->capLots * 2 : 128;
        RpLot* n = (RpLot*)RpAlloc(cap * sizeof(RpLot));
        if (!n) return NULL;
        if (c->lots) { memcpy(n, c->lots, c->nLots * sizeof(RpLot)); RpFree(c->lots); }
        c->lots = n, c->capLots = cap;
    }
    RpLot* l = &c->lots[c->nLots++];
    memset(l, 0, sizeof(*l));
    return l;
}

// map M: our walk; nodes unlinked by us go through the game's node free (FME release, node pool)
static void RpMapDrop(RpClone* c, const RpTab& qs) {  // every node whose FME's Q is in qs
    u8* m = c->m;
    DWORD* bk = (DWORD*)RpP(m + 4);
    const DWORD nb = RpD(m + 8);
    for (DWORD i = 0; nb > 1 && i < nb; i++)
        for (DWORD* at = &bk[i]; *at;) {
            u8* nd = (u8*)(uintptr_t)*at;
            const u8* f = RpP(nd + 8);
            if (f && RpTabFind(qs, RpD(f + 0xC))) {
                *at = RpD(nd + 0x10);
                g_rpMapNodeFree(m, NULL, nd);
                (*(DWORD*)(m + 0xC))--;
            } else at = (DWORD*)(nd + 0x10);
        }
}
static bool RpMapAdd(RpClone* c, DWORD lo, DWORD hi, u8* f) {  // after the run of the same key, else at the bucket head
    u8* m = c->m;
    const DWORD nb = RpD(m + 8);
    if (nb <= 1) return false;  // the empty map's shared bucket: the next game insert rehashes; a rebase does not
    u8* nd = (u8*)g_rpMapAlloc(m + 0x1C, NULL, 0x18, 0);
    if (!nd) return false;
    *(DWORD*)nd = lo, *(DWORD*)(nd + 4) = hi, *(u8**)(nd + 8) = f, *(DWORD*)(nd + 0xC) = 0, *(DWORD*)(nd + 0x10) = 0;
    (*(LONG*)(f + 4))++;
    DWORD* at = &((DWORD*)RpP(m + 4))[lo % nb];
    DWORD* after = NULL;
    for (DWORD* p = at; *p; p = (DWORD*)(uintptr_t)(*p + 0x10)) {
        const u8* x = (const u8*)(uintptr_t)*p;
        if (RpD(x) == lo && RpD(x + 4) == hi) after = (DWORD*)(x + 0x10);
        else if (after) break;
    }
    DWORD* link = after ? after : at;
    *(DWORD*)(nd + 0x10) = *link;
    *link = (DWORD)(uintptr_t)nd;
    (*(DWORD*)(m + 0xC))++;
    return true;
}

static void RpFreeClone(RpClone* c) {  // caller holds +0x918 (the clone's writers); nodes go back to the private pools by address
    if (!c) return;
    __try {
        for (int i = 0; i < c->nLots; i++) for (int j = 0; j < c->lots[i].nQ; j++) if (c->lots[i].q[j]) RpTreeRel(c->lots[i].q[j]);
        if (u8* m = c->m) {
            DWORD* bk = (DWORD*)RpP(m + 4);
            const DWORD nb = RpD(m + 8);
            if (nb > 1) {
                for (DWORD i = 0; i < nb; i++)
                    for (u8* nd = (u8*)(uintptr_t)bk[i]; nd;) { u8* nx = RpP(nd + 0x10); g_rpMapNodeFree(m, NULL, nd); nd = nx; }
                g_rpMapFree(m + 0x1C, NULL, bk, nb * 4 + 4);
            }
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) { ts3native_counters.rpFaults++; }
    for (int i = 0; i < c->nLots; i++) RpLotFree(&c->lots[i]);
    g_rpMassFree = true;  // every block goes: no unpin into a parent freed before its child
    for (DWORD i = 0; i < c->fps.cap; i++) if (c->fps.k[i] > 1) RpFpFree((u8*)(uintptr_t)c->fps.v[i]);
    for (DWORD i = 0; i < c->old.cap; i++) if (c->old.k[i] > 1) RpFpFree((u8*)(uintptr_t)c->old.k[i]);
    g_rpMassFree = false;
    RpTabFree(c->fps);
    RpTabFree(c->kids);
    RpTabFree(c->old), RpTabFree(c->oldLive);
    RpFree(c->lots), RpFree(c->lotBk), RpFree(c->dummyQ), RpFree(c->m), RpFree(c->p), RpFree(c);
}
// 0.35.13 (docs/94 §22.16, I-94-466): replaced clone memory is freed in slices. 0.35.12's consumer freed the old clone at an
// EPOCH (115-130 ms) and a batch's replaced lots (a 119-lot batch: 180-206 ms) inside one +0x918 hold. Now the EPOCH and the
// batch move them to the grave (portal references dropped at once: live objects); each consumer hold frees about 1 ms of it,
// with +0x918 and the pool set held as before, in RpFreeClone's order (trees, map nodes, lots, blocks, tables)
struct RpGrave { RpLot* lots; int n, cap, at; RpClone* c; int phase; DWORD i; };
static RpGrave g_rpGrave;
static bool RpGraveBusy() { return g_rpGrave.at < g_rpGrave.n || g_rpGrave.c; }
static void RpGraveLot(RpLot* l) {  // a lot the clone dropped (its map nodes already gone)
    RpGrave& g = g_rpGrave;
    if (l->prefs) RpPortalRefsDrop(l);
    if (g.n == g.cap) {
        const int cap = g.cap ? g.cap * 2 : 256;
        RpLot* nl = (RpLot*)RpAlloc(cap * sizeof(RpLot));
        if (!nl) { RpLotFree(l); return; }
        if (g.n) memcpy(nl, g.lots, g.n * sizeof(RpLot));
        RpFree(g.lots);
        g.lots = nl, g.cap = cap;
    }
    g.lots[g.n++] = *l;
}
static void RpGraveStep(LONG budgetUs);
static void RpGraveClone(RpClone* c) {  // a whole clone (an EPOCH replaced it)
    if (!c) return;
    if (g_rpGrave.c) RpGraveStep(-1);
    for (int i = 0; i < c->nLots; i++) if (c->lots[i].prefs) RpPortalRefsDrop(&c->lots[i]);
    g_rpGrave.c = c, g_rpGrave.phase = 0, g_rpGrave.i = 0;
}
static void RpGraveCloneStep(RpGrave& g) {  // one slice of RpFreeClone
    RpClone* c = g.c;
    switch (g.phase) {
        case 0:  // trees
            if (g.i < (DWORD)c->nLots) {
                RpLot& l = c->lots[g.i++];
                __try { for (int j = 0; j < l.nQ; j++) if (l.q[j]) RpTreeRel(l.q[j]); } __except (EXCEPTION_EXECUTE_HANDLER) { ts3native_counters.rpFaults++; }
                return;
            }
            break;
        case 1:  // map nodes, 512 buckets a slice, then the bucket array
            if (u8* m = c->m) {
                const DWORD nb = RpD(m + 8);
                DWORD* bk = (DWORD*)RpP(m + 4);
                __try {
                    if (nb > 1 && g.i < nb) {
                        for (const DWORD e = min(nb, g.i + 512); g.i < e; g.i++)
                            for (u8* nd = (u8*)(uintptr_t)bk[g.i]; nd;) { u8* nx = RpP(nd + 0x10); g_rpMapNodeFree(m, NULL, nd); nd = nx; }
                        return;
                    }
                    if (nb > 1) g_rpMapFree(m + 0x1C, NULL, bk, nb * 4 + 4);
                } __except (EXCEPTION_EXECUTE_HANDLER) { ts3native_counters.rpFaults++; }
            }
            break;
        case 2:  // lots
            if (g.i < (DWORD)c->nLots) { RpLotFree(&c->lots[g.i++]); return; }
            break;
        case 3: case 4: {  // footprint blocks, then replaced blocks; 1024 slots a slice
            const RpTab& t = g.phase == 3 ? c->fps : c->old;
            if (g.i < t.cap) {
                g_rpMassFree = true;  // every block goes: no unpin into a parent freed before its child
                for (const DWORD e = min(t.cap, g.i + 1024); g.i < e; g.i++)
                    if (t.k[g.i] > 1) RpFpFree((u8*)(uintptr_t)(g.phase == 3 ? t.v[g.i] : t.k[g.i]));
                g_rpMassFree = false;
                return;
            }
            break;
        }
        default:
            RpTabFree(c->fps), RpTabFree(c->kids), RpTabFree(c->old), RpTabFree(c->oldLive);
            RpFree(c->lots), RpFree(c->lotBk), RpFree(c->dummyQ), RpFree(c->m), RpFree(c->p), RpFree(c);
            g.c = NULL, g.phase = 0, g.i = 0;
            return;
    }
    g.phase++, g.i = 0;
}
static void RpGraveStep(LONG budgetUs) {  // consumer: +0x918 and the pool set held; budgetUs < 0 = all of it
    LARGE_INTEGER t0;
    QueryPerformanceCounter(&t0);
    RpGrave& g = g_rpGrave;
    __try {
        while (RpGraveBusy() && (budgetUs < 0 || UsSince(t0) < budgetUs)) {
            if (g.at < g.n) {
                RpLotFree(&g.lots[g.at++]);
                if (g.at == g.n) g.at = g.n = 0;
            } else RpGraveCloneStep(g);
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {  // shortcut: what is left is leaked, as the drain's own fault path does
        ts3native_counters.rpFaults++;
        g.at = g.n = 0, g.c = NULL, g.phase = 0, g.i = 0, g_rpMassFree = false;
    }
    if (!RpGraveBusy() && g.lots) RpFree(g.lots), g.lots = NULL, g.cap = 0;
}
static void RpCtxDone(RpCtx& x) { RpTabFree(x.qMap); RpTabFree(x.fmeMap); RpTabFree(x.fps); RpTabFree(x.kids); RpTabFree(x.exQ); RpTabFree(x.bad); }

// lots of the build whose live Qs are in exQ: map nodes dropped, lot unlinked and freed (no trees yet); false = over 64
static bool RpExclude(RpClone* c, const RpTab& exQ) {
    RpTab qs = {};
    for (int i = 0; i < c->nLots; i++) {
        bool hit = false;
        for (int j = 0; j < c->lots[i].nQ && !hit; j++) hit = c->lots[i].liveQ[j] && RpTabFind(exQ, c->lots[i].liveQ[j]);
        if (!hit) continue;
        if (c->nEx == 64) { RpTabFree(qs); return false; }
        c->ex[c->nEx++] = c->lots[i].key;
        for (int j = 0; j < c->lots[i].nQ; j++) if (c->lots[i].q[j]) RpTabPut(qs, (DWORD)(uintptr_t)c->lots[i].q[j], 1);
    }
    if (c->nEx) RpMapDrop(c, qs);
    RpTabFree(qs);
    for (int e = 0; e < c->nEx; e++)
        for (int i = 0; i < c->nLots; i++)
            if (c->lots[i].key == c->ex[e]) { RpTableUnlink(c, &c->lots[i]); RpLotFree(&c->lots[i]); c->lots[i] = c->lots[--c->nLots]; break; }
    return true;
}

// --- the whole clone (sim, +0x918 held) ---
static RpWhy g_rpBuildWhy, g_rpBuildExWhy;
static int g_rpBuildAlone, g_rpBuildNoQ;  // the last build's child footprints in their own block, FMEs without a Q  // the last build's first failure / left-out cause (sim; RpEpochBody logs them)
static int RpFaultW(RpWhy& w, DWORD code) { w = {RW_FAULT, 0, 0, 0, code, 0}; return EXCEPTION_EXECUTE_HANDLER; }
static RpClone* RpBuild(const u8* world) {
    const u8* P = RpP(world + 0x95C);
    const u8* M = P ? RpP(P + 0x24) : NULL;
    if (!P || !M) return NULL;
    RpClone* c = (RpClone*)RpAlloc(sizeof(RpClone));
    RpCtx x;
    memset(&x, 0, sizeof(x));
    if (!c) return NULL;
    __try {
        c->p = (u8*)RpAlloc(kRpPSz), c->m = (u8*)RpAlloc(kRpMSz), c->dummyQ = (u8*)RpAlloc(kRpQSz);
        if (!c->p || !c->m || !c->dummyQ) RpFailW(x, RW_ALLOC, 0, 8);
        else {
            memcpy(c->p, P, kRpPSz);
            memcpy(c->m, M, kRpMSz);
            // the map's node pool: the allocator object is inline at M+0x1C with the pool at +0; ours, not the live planner's
            // [P+0x28] (docs/94 §16.6 step 1). +4 (bucket sub-allocator, the locked EA heap) stays as copied
            if (u8* pool = PlMapPool(RpP(M + 0x1C))) *(u8**)(c->m + 0x1C) = pool; else RpFailW(x, RW_MAPPOOL);
            c->seen = PlSeenNow(P);
            *(u8**)(c->p + 0x24) = c->m;
            x.cp = c->p, x.dummyQ = c->dummyQ;
            x.excl = true;  // a footprint the copy refuses leaves its lot out (rpExclLots) instead of failing the whole build
            // lots and Q shells, table in the live chain order
            const int n = RpLiveLots(P, g_rpLive, kRpMaxLots);
            const DWORD nb = RpD(P + 0x50);
            c->lotN = nb;
            c->lotBk = (DWORD*)RpAlloc((nb + 1) * 4);
            if (n < 0 || !c->lotBk) RpFailW(x, n < 0 ? RW_LOTTABLE : RW_ALLOC, 0, (DWORD)n, 9);
            else {
                c->lotBk[nb] = ((const DWORD*)RpP(P + 0x4C))[nb];
                *(DWORD**)(c->p + 0x4C) = c->lotBk;
                for (int i = 0; i < n && !x.fail; i++) {
                    RpLot* l = RpLotAdd(c);
                    if (!l || !RpLotShell(x, g_rpLive[i].node, l)) RpFailW(x, RW_ALLOC, g_rpLive[i].key, 10);
                }
                for (int i = n - 1; i >= 0 && !x.fail; i--) RpTableLink(c, &c->lots[i]);  // head inserts in reverse = live order within a bucket
                for (int i = 0; i < c->nLots; i++) if (c->lots[i].nQ && c->lots[i].q[0]) { memcpy(c->dummyQ, c->lots[i].q[0], kRpQSz); break; }
                *(u8**)(c->dummyQ + 0x24) = NULL, *(LONG*)(c->dummyQ + 0x34) = RP_BIAS;
            }
            // map M: same bucket count and chain order; FMEs and footprints cloned as met
            const DWORD mb = RpD(M + 8);
            if (!x.fail && mb > 1) {
                DWORD* bk = (DWORD*)g_rpMapAlloc(c->m + 0x1C, NULL, mb * 4 + 4, 0x400000);
                const DWORD* lb = (const DWORD*)RpP(M + 4);
                if (!bk) RpFailW(x, RW_ALLOC, 0, 11);
                else {
                    memset(bk, 0, mb * 4);
                    bk[mb] = lb[mb];
                    *(DWORD**)(c->m + 4) = bk;
                    DWORD cnt = 0;
                    for (DWORD i = 0; i < mb && !x.fail; i++)
                        for (const u8* nd = (const u8*)(uintptr_t)lb[i]; nd && !x.fail; nd = RpP(nd + 0x10)) RpPreParent(x, RpP(nd + 8));
                    for (DWORD i = 0; i < mb && !x.fail; i++) {
                        DWORD* tail = &bk[i];
                        for (const u8* nd = (const u8*)(uintptr_t)lb[i]; nd && !x.fail; nd = RpP(nd + 0x10)) {
                            u8* f = RpP(nd + 8) ? RpFme(x, RpP(nd + 8)) : NULL;
                            if (RpP(nd + 8) && !f) continue;  // its lot is left out (exQ): no node
                            u8* cn = (u8*)g_rpMapAlloc(c->m + 0x1C, NULL, 0x18, 0);
                            if (!cn) { RpFailW(x, RW_ALLOC, 0, 12); break; }
                            memcpy(cn, nd, 0x18);
                            *(u8**)(cn + 8) = f;
                            *(DWORD*)(cn + 0x10) = 0;
                            if (f) (*(LONG*)(f + 4))++;
                            *tail = (DWORD)(uintptr_t)cn;
                            tail = (DWORD*)(cn + 0x10);
                            cnt++;
                        }
                    }
                    *(DWORD*)(c->m + 0xC) = cnt;
                }
            } else if (!x.fail && RpD(M + 0xC)) RpFailW(x, RW_MAP1, 0, RpD(M + 0xC));  // a one-bucket map with entries: not a shape we know
            for (int i = 0; i < c->nLots && !x.fail; i++) RpLotPre(x, &c->lots[i]);  // leaf-only refusals: left out too
            if (!x.fail && x.exQ.used && !RpExclude(c, x.exQ)) RpFailW(x, RW_EXCL64, x.exWhy.lot, x.exQ.used);
            const DWORD exBefore = x.exQ.used;
            for (int i = 0; i < c->nLots && !x.fail; i++) RpLotFill(x, &c->lots[i]);
            if (x.exQ.used != exBefore) RpFailW(x, RW_LEAFONLY, x.lot, x.exQ.used - exBefore);  // RpLotPre should have met it
        }
    } __except (RpFaultW(x.why, GetExceptionCode())) { x.fail = true; ts3native_counters.rpFaults++; }
    g_rpBuildWhy = x.fail ? x.why : RpWhy{}, g_rpBuildExWhy = x.exWhy;
    g_rpBuildAlone = x.nAlone, g_rpBuildNoQ = x.nNoQ;
    c->fps = x.fps, c->kids = x.kids;
    x.fps.k = x.fps.v = NULL, x.kids.k = x.kids.v = NULL;
    RpCtxDone(x);
    if (x.fail) { ts3native_counters.rpEpochFails++; RpFreeClone(c); return NULL; }
    return c;
}

// --- ring ---
struct RpRec { DWORD type, a, b, c; void* p; DWORD pad; };
static const DWORD kRpRing = 1 << 16;
static RpRec* g_rpRing;
static volatile LONG g_rpRingLock;
static volatile DWORD g_rpEpochAt;  // ring position after the last EPOCH (sim)
static DWORD g_rpLotsAt;             // ring position after the last LOTS with map work since that EPOCH (sim; g_rpLotsPend: there is one)
static bool g_rpLotsPend;
static LONG g_rpLotsWait;            // cuts whose rebase waited for that batch (logged)
static volatile DWORD g_rpHead, g_rpTail, g_rpPub;  // head: next position written; tail: next read; pub: published
static volatile LONG g_rpOn, g_rpNeedEpoch;
// 0.33.1 (docs/94 §21.14): a map resync that fails only on live nodes of lots the clone holds forces those lots' rebase at
// the next cut instead of a whole rebuild (BP 0.32.8: 60 rebuilds of ~170 ms on the sim, each followed by ~25 refused
// shadows). Until a later batch resyncs, the clone is stale: P4 refuses every acquire ("mat", sub "stale"). After
// kRpSoftMax soft tries in a row, or a miss on a node of no clone lot, the rebuild as before.
static volatile LONG g_rpStale;
static int g_rpSoftRun;  // consumer
static const int kRpSoftMax = 3;
// 0.35.5 (docs/94 §21.20): kRpSoftMax counted every failed batch, but a force only reaches the batch of the NEXT cut. With
// the consumer behind (BP lag up to 756 records), the batches already cut before the force miss the same lot again and used
// the tries up (BP 76, IP 21 rebuilds of ~170 ms in a sim cut, "soft tries used up"); so did a new unrecorded change on a lot
// rebased since. Now only a miss on a lot this very batch rebased counts as a try (kRpSoftMax); a miss on a lot whose force
// is still pending (g_rpSoftPend) is in flight (not forced again), any other one is fresh (forced); a failed batch without
// such a lot counts in g_rpSoftFresh (kRpSoftFreshMax in a row, then the rebuild as before). The clone stays stale (P4 refuses) until the whole map matches.
static u64 g_rpSoftPend[64];  // lots forced by a soft resync, not yet in an applied batch (consumer; the sim reads them under the ring lock)
static int g_rpNSoftPend;
static int g_rpSoftFresh;  // consumer
static const int kRpSoftFreshMax = 32;
// what asked for the next clone rebuild (0.31.4, log only): the source line in replay.cpp, negative for p4clone.cpp
static volatile LONG g_rpEpochWhy;
// 0.35.3 (docs/94 §21.18): the clone the consumer applies to missed a record; P4 refuses acquires until an EPOCH replaces it
// (consumer and P4 discard set it, the consumer's EPOCH clears it). Before, a later cut's rebuild cleared g_rpNeedEpoch and a
// shadow of an older cut ran on the missed clone
static volatile LONG g_rpBad;
// a consumer miss on a clone that a newer EPOCH already replaces (the sim built it after these records) asks no rebuild: it
// only marks that clone bad. Before, each such miss (consumer lag behind a rebuild) asked a second rebuild and the cuts until
// the consumer applied the first EPOCH were refused "nover"/"noclone" (BP 0.34.4: rebuilds in pairs, bursts of up to 25)
static void RpAskEpoch(LONG line) {
    if (t_rpReplay) {
        g_rpBad = 1;
        if ((LONG)(g_rpEpochAt - 1 - g_rpTail) > 0) { InterlockedIncrement(&ts3native_counters.rpOldAsk); return; }
    }
    g_rpEpochWhy = line, g_rpNeedEpoch = 1;
}
#define RP_NEED_EPOCH() RpAskEpoch(__LINE__)
#define RP_REBUILD(w) (g_rpEpochWhy = __LINE__, RpEpoch(w))
static HANDLE g_rpEvent, g_rpThread;
static bool g_rpThreaded = true;  // selftest drains on its own thread
static void RpPayloadFree(const RpRec& r);
static void RpPush(DWORD type, DWORD a, DWORD b, DWORD c, void* p, DWORD pad = 0) {  // pad: an INS record's RpInsLive (0.36.35)
    Ts3nCounters& k = ts3native_counters;
    while (InterlockedExchange(&g_rpRingLock, 1)) YieldProcessor();
    const bool full = g_rpHead - g_rpTail >= kRpRing;
    if (!full) {
        RpRec& r = g_rpRing[g_rpHead & (kRpRing - 1)];
        r.type = type, r.a = a, r.b = b, r.c = c, r.p = p, r.pad = pad;
        g_rpHead++;
    }
    InterlockedExchange(&g_rpRingLock, 0);
    if (full) {
        RpRec r = {type, a, b, c, p, pad};
        RpPayloadFree(r);
        InterlockedIncrement(&k.rpDrop);
        RP_NEED_EPOCH();
        return;
    }
    InterlockedIncrement(&k.rpRec[type]);
}

// --- payloads ---
// shell (0.35.17, docs/94 §22.20): only the lot node, portals, rooms and hashes were copied; the consumer keeps the clone's
// Qs, trees and map nodes of that lot (the records kept them in step) and swaps the shell in
struct RpBatchLot { u64 key; bool removed, shell; RpLot lot; };
struct RpBatch {
    int n; RpBatchLot* lots;
    DWORD* map; int nMap;  // (lo, hi, FME) triples in the live chain order
    RpTab fps, kids;  // its footprint blocks (live -> clone) and their children
    // the whole live map at the cut, chain by chain: (key lo, key hi, live Q, FME +0x10, live footprint) per node, the bucket
    // count and the rehash policy; the consumer re-chains its map to it after the rebase (equal-key order and rehash timing)
    DWORD* order; DWORD nOrder, mapN, policy[3];
    u64* lotKeys; int nLotKeys;  // the live lot table at the cut in chain order: the consumer re-links its table to it
};
struct RpAuditRec { int n; u64* keys; u64* hash; DWORD* ents; DWORD mapN, mapCount; int nAll; DWORD* perQ; DWORD seq; LONG cut; };  // perQ: 16 per lot  // nAll: live lots (n may be a subset)  // seq: ring position of this record (docs/157 §7)
struct RpSyncRec { DWORD mapN, mapCount, lots; };
static void RpBatchFree(RpBatch* b, bool owned) {  // owned: lots / FMEs not taken by the clone (needs +0x918)
    if (!b) return;
    if (owned) {
        __try {
            for (int i = 0; i < b->nMap; i++) {  // FMEs: their only reference would have been the map node
                u8* f = (u8*)(uintptr_t)b->map[i * 3 + 2];
                if (f && --*(LONG*)(f + 4) <= 0) { *(LONG*)(f + 4) = 1; ((RpDtorFn)(uintptr_t)RpD(RpP(f)))(f, NULL, 1); }
            }
            for (int i = 0; i < b->n; i++) if (!b->lots[i].removed) RpLotFree(&b->lots[i].lot);
        } __except (EXCEPTION_EXECUTE_HANDLER) { ts3native_counters.rpFaults++; }
        g_rpMassFree = true;
        for (DWORD i = 0; i < b->fps.cap; i++) if (b->fps.k[i] > 1) RpFpFree((u8*)(uintptr_t)b->fps.v[i]);
        g_rpMassFree = false;
    }
    RpTabFree(b->fps), RpTabFree(b->kids);
    RpFree(b->lotKeys), RpFree(b->order), RpFree(b->map), RpFree(b->lots), RpFree(b);
}
static void RpAuditFree(RpAuditRec* a) { if (a) { RpFree(a->keys), RpFree(a->hash), RpFree(a->ents), RpFree(a->perQ), RpFree(a); } }
// FMEs without a Q (0.30.2): a footprint with no lot goes into map M straight from 0x6376D0 (FME at 0x637809, Q cleared,
// map insert 0x628220 at 0x63787E, after PathPlanner_AddHangingFootprint 0x636BF0 with lot pointer 0) and leaves it in
// LotRouting_RemoveObjectFootprints 0x635700 with lot id 0 [verified]. No W1/W2 runs, so 0.30.1 had no record: the clone's
// map lost count at the next SYNC and was rebuilt (BP: 872 divergences, 486 builds in 10 min). The writer notes the key; the
// cut sends the live Q-less nodes of each noted key (RP_NOQ), the consumer swaps its own for them.
static const DWORD RP_NOQ = 0;  // record type (rpRec[0] / rpApplied[0], unused before)
struct RpNoQEnt { DWORD lo, hi; DWORD fme[5]; u8* liveFp; u8* copy; };
struct RpNoQRec { u64 key; bool all; int n; RpNoQEnt e[1]; };
static void RpNoQFree(RpNoQRec* r) {
    if (!r) return;
    for (int i = 0; i < r->n; i++) RpFpFree(r->e[i].copy);
    RpFree(r);
}
static void RpPayloadFree(const RpRec& r) {
    switch (r.type) {
        case RP_NOQ: RpNoQFree((RpNoQRec*)r.p); break;
        case RP_INS: RpFpFree((u8*)r.p); RpFree((void*)(uintptr_t)r.pad); break;
        case RP_LOTS: RpBatchFree((RpBatch*)r.p, true); break;  // dropped before the clone took it (the sim's cut, +0x918 held)
        case RP_EPOCH: RpFreeClone((RpClone*)r.p); break;
        case RP_AUDIT: RpAuditFree((RpAuditRec*)r.p); break;
        case RP_SYNC: case RP_REM: RpFree(r.p); break;
        default: break;
    }
}

// --- lot hash (the audit; same walk on live and clone memory; no pointers) ---
static u64 RpMix(u64 h, DWORD v) { for (int i = 0; i < 4; i++) { h ^= (v >> (i * 8)) & 0xFF; h *= 0x100000001B3ull; } return h; }
static u64 RpEntry(u64 h, const u8* e) {  // 12-byte leaf entry: FME, mask
    const u8* f = RpP(e);
    h = RpMix(RpMix(h, RpD(e + 4)), RpD(e + 8));
    if (!f) return RpMix(h, 0);
    const u8* fp = RpP(f + 8);
    h = RpMix(h, RpD(f + 0x10));
    if (fp) {
        const RpCls* c = RpClass(RpD(fp));
        const DWORD w34 = !c || c->size > 0x34 ? RpD(fp + 0x34) : 0;  // the 0x28-byte child class ends before +0x34
        h = RpMix(RpMix(RpMix(RpMix(h, RpD(fp)), RpD(fp + 0x14)), RpD(fp + 0x18)), w34);
    }
    return h;
}
static u64 RpTreeHash(u64 h, const u8* n, int depth, DWORD* ents) {
    if (!n) return RpMix(h, 0xEEEEEEEE);
    if (depth > 40) return RpMix(h, 0xDDDDDDDD);
    h = RpMix(h, RpD(n + 4) & 0x00FFFFFF);
    if (n[4] & 1) { for (int i = 0; i < 4; i++) h = RpTreeHash(h, RpP(n + 0xC + 4 * i), depth + 1, ents); return h; }
    if (n[8] & 1) {
        const DWORD cnt = RpD(n + 0xC);
        const u8* a = (const u8*)(uintptr_t)(RpD(n + 8) & ~1u);
        h = RpMix(h, cnt);
        for (DWORD j = 0; j < cnt && j < 65536; j++) h = RpEntry(h, a + j * 12);
        *ents += cnt;
        return h;
    }
    (*ents)++;
    return RpEntry(RpMix(h, 0xFFFFFFFF), n + 8);
}
static u64 RpLotHash(const u8* node, DWORD* ents, DWORD* perQ = NULL) {  // perQ: 16 entry counts, one per Q (0.32.8)
    u64 h = 0xCBF29CE484222325ull;
    *ents = 0;
    if (perQ) memset(perQ, 0, 16 * 4);
    __try {
        const u8* g = node + 0x10;
        h = RpMix(RpMix(RpMix(RpMix(h, RpD(node)), RpD(node + 4)), g[0x8C]), RpD(g + 0x40));
        for (DWORD o = 0xA0; o < 0x120; o += 4) h = RpMix(h, RpD(g + o));
        const u8* pb = RpP(g + 0x44);
        const u8* pe = RpP(g + 0x48);
        h = RpMix(h, (DWORD)(pe - pb));
        for (const u8* p = pb; p && p < pe && p < pb + 4 * 4096; p += 4) h = RpMix(h, RpD(p));
        h = RpMix(RpMix(h, RpD(g + 0x58) - RpD(g + 0x54)), RpD(g + 0x68) - RpD(g + 0x64));
        const u8* qb = RpP(g + 0x74);
        const u8* qe = RpP(g + 0x78);
        h = RpMix(h, (DWORD)(qe - qb));
        for (const u8* qp = qb; qp && qp < qe && qp < qb + 64; qp += 4) {
            const u8* q = RpP(qp);
            if (!q) { h = RpMix(h, 0); continue; }
            for (DWORD o = 0x18; o <= 0x1C; o += 4) h = RpMix(h, RpD(q + o));
            for (DWORD o = 0x38; o <= 0x44; o += 4) h = RpMix(h, RpD(q + o));
            const DWORD e0 = *ents;
            h = RpTreeHash(h, RpP(q + 0x24), 0, ents);
            if (perQ) perQ[(qp - qb) / 4] = *ents - e0;
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) { h = 0; }
    return h;
}
// signature mix (0.35.20, docs/94 §22.23): two 32-bit lanes, one multiply each per DWORD. RpMix's byte-wise 64-bit FNV cost
// ~13 ns per DWORD on x86 (4 64-bit multiplies): 82 BP lots x ~70 DWORDs = 72-325 us per cut by portal count (bench), the
// largest steady share of the cut. Each lane step is a bijection of the lane for a fixed input, so a change of one input
// DWORD always changes both lanes; only the sim's mirror compares these values (no stored or cross-thread hash)
static inline u64 RpSigMix(u64 h, DWORD v) {
    DWORD a = (DWORD)h, b = (DWORD)(h >> 32);
    a = (a ^ v) * 0x01000193u;
    b = _rotl(b + v * 0x85EBCA6Bu, 13) * 5u + 0xE6546B64u;
    return ((u64)b << 32) | a;
}
// lot signature (sim mirror): what forces a rebase. Node, built byte, door-ask byte, level base, Q pointers, transforms,
// portals, lists. 0.35.24 (docs/94 §22.28, I-94-499): +0x8F (Portal_LockState 0x64F2C0 asks the script only while it is set;
// SetPortalLockStatus 0x74B490 sets it, a 0 / > 3 answer clears it at 0x64F4EE) is in the signature, so a flip alone
// refreshes the lot's shell (RpLotShell copies the whole node) instead of leaving the clone on the old byte
static u64 RpSig(const u8* node) {
    u64 h = RpSigMix(0xCBF29CE484222325ull, (DWORD)(uintptr_t)node);
    __try {
        const u8* g = node + 0x10;
        h = RpSigMix(RpSigMix(h, g[0x8C] | (DWORD)g[0x8F] << 8), RpD(g + 0x40));
        for (DWORD o = 0xA0; o < 0x120; o += 4) h = RpSigMix(h, RpD(g + o));
        for (DWORD v = 0x44; v <= 0x74; v += 0x10) {
            const u8* b = RpP(g + v);
            const u8* e = RpP(g + v + 4);
            h = RpSigMix(h, (DWORD)(e - b));
            if (v == 0x44 || v == 0x74) for (const u8* p = b; p && p < e && p < b + 4 * 4096; p += 4) h = RpSigMix(h, RpD(p));
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) { h = 0; }
    return h;
}

static u64 RpQSig(const u8* node) {  // 0.35.5: the Q vector +0x74 alone (what a shell-only rebase would keep)
    u64 h = 0xCBF29CE484222325ull;
    __try {
        const u8* b = RpP(node + 0x10 + 0x74);
        const u8* e = RpP(node + 0x10 + 0x78);
        for (const u8* p = b; p && p < e && p < b + 64; p += 4) h = RpSigMix(h, RpD(p));
    } __except (EXCEPTION_EXECUTE_HANDLER) { h = 0; }
    return h;
}
// --- the sim's mirror of what the clone was built from (sim thread only) ---
struct RpMirror { u64 key, sig, qsig; };  // qsig: the lot's Q pointer list only (rpRebaseQSame)
static RpMirror g_rpMir[kRpMaxLots];
static int g_rpNMir;
static u8* g_rpSimP;                 // the clone's P (stable until the next epoch); rebase shells point their Qs at it
static const u8* g_rpWorld;
static bool g_rpHave;                // an epoch clone was pushed
static u64 g_rpForce[64];            // W3 / W4 lots: rebase at the next cut (any thread, under the ring lock)
static int g_rpNForce;
static LONG g_rpForceAll;
// 0.36.55 (docs/94 §24.13.5): audit-forced lots, so the log shows their repair: state 1 = forced by an audit MISMATCH, 2 =
// rebased at a cut ("rebased at cut N" line), then "equal again" at its first equal audit (entry dropped). Ring lock.
static u64 g_rpAudF[32];
static BYTE g_rpAudFSt[32];
static int g_rpNAudF;
static LONG g_rpAudFLog, g_rpAudFReb, g_rpAudFRep;  // log lines; audit-forced lots rebased, then equal again (selftest)
static int RpAudFFind(u64 key) {  // under the ring lock; -1 none
    for (int i = 0; i < g_rpNAudF; i++) if (g_rpAudF[i] == key) return i;
    return -1;
}
// docs/157 §10 ([RouteSnap] AuditHoldRebase=1, default 0, I-157-11): an audit mismatch whose diff is pending holds that
// lot's rebase (forced, signature or shell) until the diff has run on the drifted clone, at most one cut; then the cut
// releases it (timeout). One hold per lot in a row (g_rpHoldLast), so the hold's own repeat mismatch rebases at once.
// Ring lock for all three
static LONG g_rpAuditHold;
static u64 g_rpHoldKey, g_rpHoldLast;
static LONG g_rpHoldCuts;
// 0.35.17 (docs/94 §22.20, I-94-476): W4 lots (LotGraph_Build). The build rewrites the lot node, rooms and portals; its
// quadtree changes go through W1 / W2 (0x635700 removes, 0x666C50 queues inserts) and are replayed, and a new Q is a W3
// build (a full force). So the cut refreshes only the shell while the lot's Q list is unchanged (BP 0.35.16: 389 of 446
// rebased lots were W4 forces from PcPrepare's first use, IP 573 of 638). Same lock and overflow rule as g_rpForce
static u64 g_rpShellF[64];
static int g_rpNShellF;
static DWORD g_rpBadQ[16];          // Qs that got an insert whose footprint copy was refused (under the ring lock)
static int g_rpNBadQ;
static DWORD g_rpAuditEvery = 4, g_rpLogMis = 32;
// lots hashed per audit cut, rotating (0 = all). 0.30.1 hashed all 82 BP lots on the sim at every 4th cut: ~97 ms each, 38 ms/s
static DWORD g_rpAuditLots = 2, g_rpAuditAt;
static LONG g_rpLogged;
// Qs the clone knows (sim mirror). A record on another Q is skipped by the consumer, so the footprints it touched (their
// insert counters, polygon transform state) are refreshed from live at the next cut: their keys and ~keys are kept here
// (any writing thread under the ring lock; the writers and the cut both hold +0x918, so the table does not move under them)
static RpTab g_rpKnownQ;
// [Watchpoint] Lot= (watchpt.cpp, docs/157 §8.1): that lot's Qs, noted at each mirror pass; W1 inserts on them go to WpLotInsert
static u64 g_wpLot;
static DWORD g_wpLotQ[16];
static void WpLotNoteQ(DWORD q);
static void WpLotInsert(DWORD q, u8* fp);
static void WpLotRemove(DWORD lo, DWORD hi);
static u64 g_rpUnk[256];
static int g_rpNUnk;
static LONG g_rpUnkAll;

// lots the epoch build left out (sim): never rebased, not in the SYNC / audit / map order, their Qs unknown (records on them
// are skipped and the footprints they touch refreshed); the next epoch build tries them again
static u64 g_rpEx[64];
static int g_rpNEx;
static bool RpIsEx(u64 key) {
    for (int i = 0; i < g_rpNEx; i++) if (g_rpEx[i] == key) return true;
    return false;
}
// 0.36.23 (docs/94 §22.32, I-94-452): a soft force on a lot the clone left out (or one no longer live) reaches no batch, so its
// g_rpSoftPend entry stayed until an epoch and every later miss there counted "in flight" (not forced again) until 32 failed
// batches asked for a rebuild. The cut drops such entries (sim, ring lock held); a later miss is then fresh. Returns the count
static LONG g_rpSoftPruned;
static int RpSoftPendPrune(const RpLiveLot* live, int n) {
    int dropped = 0;
    for (int j = 0; j < g_rpNSoftPend; j++) {
        const u64 key = g_rpSoftPend[j];
        int i = 0;
        while (i < n && live[i].key != key) i++;
        if (i < n && !RpIsEx(key)) continue;
        g_rpSoftPend[j--] = g_rpSoftPend[--g_rpNSoftPend];
        dropped++;
    }
    g_rpSoftPruned += dropped;
    return dropped;
}
// live Qs of the left-out lots in `live` (sim, at a cut)
static void RpExQs(const RpLiveLot* live, int n, RpTab& out) {
    for (int i = 0; i < n && g_rpNEx; i++) {
        if (!RpIsEx(live[i].key)) continue;
        __try {
            const u8* g = live[i].node + 0x10;
            for (const u8* qp = RpP(g + 0x74); qp && qp < RpP(g + 0x78) && qp < RpP(g + 0x74) + 64; qp += 4)
                if (RpD(qp)) RpTabPut(out, RpD(qp), 1);
        } __except (EXCEPTION_EXECUTE_HANDLER) {}
    }
}
// keep (0.35.23, ReplaySigMs): a cut that did not scan signatures; every lot not in keepA / keepB (the lots this batch
// rebases) keeps its old signatures, so a node change no hook saw is still a difference at the next scan
static void RpMirrorSet(const RpLiveLot* live, int n, const RpLiveLot* keepA = NULL, int nA = 0, const RpLiveLot* keepB = NULL, int nB = 0,
                        bool keep = false) {
    static RpMirror old[kRpMaxLots];
    const int nOld = keep ? g_rpNMir : 0;
    if (nOld) memcpy(old, g_rpMir, nOld * sizeof(RpMirror));
    g_rpNMir = 0;
    RpTabFree(g_rpKnownQ);
    for (int i = 0; i < n && g_rpNMir < kRpMaxLots; i++) {
        const RpMirror* o = NULL;
        if (nOld) {
            bool reb = false;
            for (int j = 0; j < nA && !reb; j++) reb = keepA[j].key == live[i].key;
            for (int j = 0; j < nB && !reb; j++) reb = keepB[j].key == live[i].key;
            if (!reb) {
                if (i < nOld && old[i].key == live[i].key) o = &old[i];
                for (int j = 0; !o && j < nOld; j++) if (old[j].key == live[i].key) o = &old[j];
            }
        }
        g_rpMir[g_rpNMir++] = o ? *o : RpMirror{live[i].key, RpSig(live[i].node), RpQSig(live[i].node)};
        if (RpIsEx(live[i].key)) continue;
        __try {
            const u8* g = live[i].node + 0x10;
            for (const u8* qp = RpP(g + 0x74); qp && qp < RpP(g + 0x78) && qp < RpP(g + 0x74) + 64; qp += 4)
                if (RpD(qp)) {
                    RpTabPut(g_rpKnownQ, RpD(qp), 1);
                    if (g_wpLot && live[i].key == g_wpLot) WpLotNoteQ(RpD(qp));
                }
        } __except (EXCEPTION_EXECUTE_HANDLER) {}
    }
}
static void RpUnkAdd(u64 key) {  // under the ring lock
    for (int i = 0; i < g_rpNUnk; i++) if (g_rpUnk[i] == key) return;
    if (g_rpNUnk < 256) g_rpUnk[g_rpNUnk++] = key; else g_rpUnkAll = 1;
}
static bool RpUnkHas(const u64* set, int n, u64 key) {
    for (int i = 0; i < n; i++) if (set[i] == key) return true;
    return false;
}
static RpMirror* RpMirrorFind(u64 key) {
    for (int i = 0; i < g_rpNMir; i++) if (g_rpMir[i].key == key) return &g_rpMir[i];
    return NULL;
}

// Q-less keys noted by the writers (under the ring lock); cleared by an epoch build
static u64 g_rpNoQ[256];
static int g_rpNNoQ;
static LONG g_rpNoQAll;
static void RpNoQNote(u64 key, bool all) {
    while (InterlockedExchange(&g_rpRingLock, 1)) YieldProcessor();
    if (all) g_rpNoQAll = 1;
    else {
        int i = 0;
        while (i < g_rpNNoQ && g_rpNoQ[i] != key) i++;
        if (i == g_rpNNoQ) { if (g_rpNNoQ < 256) g_rpNoQ[g_rpNNoQ++] = key; else g_rpNoQAll = 1; }
    }
    InterlockedExchange(&g_rpRingLock, 0);
}
// a footprint's map key: its id slot +0x7C, 0x806FE0 (+0x14 / +0x18) or the child's 0x6262B0 (~ its parent's) [verified];
// false = another slot (the cut then resyncs every Q-less node)
static bool RpFpKey(const u8* fp, u64* key) {
    const DWORD s = RpD(RpP(fp) + 0x7C);
    if (s == 0x00806FE0) { *key = ((u64)RpD(fp + 0x18) << 32) | RpD(fp + 0x14); return true; }
    if (s == 0x006262B0 && RpP(fp + 0xC) && RpD(RpP(RpP(fp + 0xC)) + 0x7C) == 0x00806FE0) {
        const u8* p = RpP(fp + 0xC);
        *key = ~(((u64)RpD(p + 0x18) << 32) | RpD(p + 0x14));
        return true;
    }
    return false;
}
// LotRouting_QuadTreeBuild 0x62F8A0 (thiscall P; lot lo, lot hi, level, create) is get-or-create [verified 0x62F8B5..0x62F901]:
// an existing Q is returned as it is. Only a missing Q with create != 0 on a lot in the table builds anything. 0.30.1 took
// every call as a rebuild (0x6376D0 calls it for each footprint add): 11,712 STALE records and 919 rebases in 10 min on BP
static bool RpW3Builds(const DWORD* slot) {
    __try {
        const u8* world = *g_spWorldSlot;
        const u8* P = world ? RpP(world + 0x95C) : NULL;
        if (!P || !slot[4]) return false;
        const int level = (int)slot[3];
        if (level + 4 > 0x10 || level < -4) return false;  // 0x62F8C6: refused
        const DWORD* bk = (const DWORD*)RpP(P + 0x4C);
        const DWORD nb = RpD(P + 0x50);
        if (!bk || !nb) return false;
        const DWORD sentinel = bk[nb];
        for (const u8* nd = (const u8*)(uintptr_t)bk[slot[1] % nb]; nd && (DWORD)(uintptr_t)nd != sentinel; nd = RpP(nd + 0x190)) {
            if (RpD(nd) != slot[1] || RpD(nd + 4) != slot[2]) continue;
            const u8* g = nd + 0x10;
            const DWORD idx = (DWORD)(level - (int)RpD(g + 0x40));
            const u8* qv = RpP(g + 0x74);
            return !(qv && idx < (DWORD)((RpP(g + 0x78) - qv) / 4) && RpD(qv + idx * 4));
        }
        return false;  // not in the table: 0x628A90 refuses
    } __except (EXCEPTION_EXECUTE_HANDLER) { return true; }
}
// back-off after a failed build (sim): no new build before g_rpRetryAt on the same world, except world event 7 (its own
// call). 0.28.0 counted cuts but never set the world it compared with, so a failing build ran at every cut (P3-B live run:
// 1,978 failed builds of up to 10.5 ms in 10 min). 0.29.1: the wait doubles per failure on the same world, 10 s to 160 s
// (0.29.0 BP: 60 failed builds of ~80 ms each, 8 of the 10.8 ms/s cut cost).
static ULONGLONG g_rpRetryAt;
static const u8* g_rpFailWorld;
static int g_rpFailRun;  // failed builds in a row on g_rpFailWorld
static int g_rpLastWhy;  // the cause last logged (a new cause is logged past the first 8 failures too)
static const DWORD kRpRetryMs = 10000;
static PlSeen g_rpSimSeen;  // the game's pool objects at the last clone build (sim)
static void RpEpochBody(const u8* world);
static void RpEpoch(const u8* world) {  // sim, +0x918 held; clone work on private pools (pools.cpp)
    LONG op;
    if (!RpOpTry(&op)) { RP_NEED_EPOCH(); return; }  // a P4 run owns the pool set: the next cut builds (0.35.12)
    RpEpochBody(world);
    RpOpLeave(op);
}
static void RpEpochBody(const u8* world) {
    Ts3nCounters& k = ts3native_counters;
    LARGE_INTEGER t0;
    QueryPerformanceCounter(&t0);
    RpGen* gen = g_rpEpochHeap ? RpGenNew() : NULL;  // EpochHeap: this build's blocks on their own heap
    t_rpGen = gen;
    RpClone* c = RpBuild(world);
    t_rpGen = NULL;
    if (gen) RpGenRel(gen);  // seal: the heap goes when the clone's last block does (now, if the build failed)
    const LONG us = UsSince(t0);
    k.rpEpochUs += us;
    Max(&k.rpEpochMaxUs, us);
    if (!c) {
        RP_NEED_EPOCH();
        g_rpFailRun = world == g_rpFailWorld ? g_rpFailRun + 1 : 0;
        g_rpRetryAt = GetTickCount64() + ((ULONGLONG)kRpRetryMs << (g_rpFailRun < 4 ? g_rpFailRun : 4));
        g_rpFailWorld = world;
        if (k.rpEpochFails <= 8 || g_rpBuildWhy.code != g_rpLastWhy) RpWhyLog("clone build failed", g_rpBuildWhy);
        if (g_rpBuildExWhy.code && (k.rpEpochFails <= 8 || g_rpBuildWhy.code != g_rpLastWhy)) RpWhyLog("clone build: first lot left out", g_rpBuildExWhy);
        g_rpLastWhy = g_rpBuildWhy.code;
        return;
    }
    k.rpEpochs++;
    g_rpRetryAt = 0, g_rpFailWorld = NULL, g_rpFailRun = 0;
    g_rpSimSeen = c->seen;
    g_rpNEx = c->nEx;
    memcpy(g_rpEx, c->ex, c->nEx * sizeof(u64));
    k.rpExclLots = c->nEx;
    if (c->nEx) {
        Log("snap replay: %d lot(s) left out of the clone (a footprint the copy refuses; first 0x%08lX%08lX)", c->nEx,
            (DWORD)(c->ex[0] >> 32), (DWORD)c->ex[0]);
        RpWhyLog("first lot left out", g_rpBuildExWhy);
    }
    g_rpSimP = c->p;
    g_rpWorld = world;
    g_rpHave = true;
    g_rpNeedEpoch = 0;
    const int n = RpLiveLots(RpP(world + 0x95C), g_rpLive, kRpMaxLots);
    RpMirrorSet(g_rpLive, n < 0 ? 0 : n);
    while (InterlockedExchange(&g_rpRingLock, 1)) YieldProcessor();
    g_rpNForce = 0, g_rpForceAll = 0, g_rpNShellF = 0, g_rpNUnk = 0, g_rpUnkAll = 0, g_rpNNoQ = 0, g_rpNoQAll = 0;
    int nAudEp = 0;  // 0.36.55: audit-forced lots repaired by this full build (their "equal again" line follows)
    for (int i = 0; i < g_rpNAudF; i++) if (g_rpAudFSt[i] == 1) g_rpAudFSt[i] = 2, g_rpAudFReb++, nAudEp++;
    InterlockedExchange(&g_rpRingLock, 0);
    if (nAudEp && InterlockedIncrement(&g_rpAudFLog) <= 128)
        Log("snap replay audit: %d audit-forced lot(s) rebuilt by an epoch build at cut %ld (first 0x%08lX%08lX)", nAudEp,
            ts3native_counters.spCuts, (DWORD)(g_rpAudF[0] >> 32), (DWORD)g_rpAudF[0]);
    RpPush(RP_EPOCH, 0, 0, 0, c);
    g_rpEpochAt = g_rpHead;
    g_rpLotsPend = false;  // older batches go to the old clone
    const LONG why = g_rpEpochWhy;
    g_rpEpochWhy = 0;
    Log("snap replay: clone built (%d lots, %lu map entries; %d child footprints outside their parent's list, %d FMEs without a Q) in %ld us; "
        "asked by %s:%ld", c->nLots, RpD(c->m + 0xC), g_rpBuildAlone, g_rpBuildNoQ, us, why < 0 ? "p4clone.cpp" : "replay.cpp", why < 0 ? -why : why);
}

// rebase of the lots in `set` (keys), removal of `gone`, and fresh copies of the live footprints whose key is in `unk`
// (touched by records the consumer skips) (sim, +0x918 held)
// the cut's Q-less records: the live map's Q-less nodes for each noted key (or all of them), in chain order, with a fresh
// footprint copy each (sim, +0x918 held); false = the clone must be rebuilt
static bool RpNoQOne(const u8* M, u64 key, bool all) {
    const DWORD mb = RpD(M + 8);
    const DWORD* lb = (const DWORD*)RpP(M + 4);
    if (mb <= 1 || !lb) return true;
    int n = 0;
    const DWORD b0 = all ? 0 : (DWORD)key % mb, b1 = all ? mb : b0 + 1;
    for (int pass = 0; pass < 2; pass++) {
        RpNoQRec* r = NULL;
        if (pass) {
            r = (RpNoQRec*)RpAlloc(sizeof(RpNoQRec) + (n ? n - 1 : 0) * sizeof(RpNoQEnt));
            if (!r) return false;
            r->key = key, r->all = all, r->n = 0;
        }
        for (DWORD i = b0; i < b1; i++)
            for (const u8* nd = (const u8*)(uintptr_t)lb[i]; nd; nd = RpP(nd + 0x10)) {
                const u8* f = RpP(nd + 8);
                if (!f || RpD(f + 0xC) || (!all && (RpD(nd) != (DWORD)key || RpD(nd + 4) != (DWORD)(key >> 32)))) continue;
                if (!pass) { n++; continue; }
                if (r->n == n) break;  // shortcut: nodes added between the passes cannot happen (+0x918 held)
                RpNoQEnt& e = r->e[r->n++];
                e.lo = RpD(nd), e.hi = RpD(nd + 4);
                memcpy(e.fme, f, 0x14);
                e.liveFp = RpP(f + 8);
                e.copy = e.liveFp ? RpFpCopy(e.liveFp) : NULL;
            }
        if (pass) RpPush(RP_NOQ, 0, 0, 0, r);
    }
    return true;
}
static bool RpNoQCut(const u8* P) {
    u64 keys[256];
    int nk;
    bool all;
    while (InterlockedExchange(&g_rpRingLock, 1)) YieldProcessor();
    nk = g_rpNNoQ, all = g_rpNoQAll != 0;
    memcpy(keys, g_rpNoQ, nk * 8);
    g_rpNNoQ = 0, g_rpNoQAll = 0;
    InterlockedExchange(&g_rpRingLock, 0);
    if (!nk && !all) return true;
    __try {
        const u8* M = RpP(P + 0x24);
        if (all) return RpNoQOne(M, 0, true);
        for (int i = 0; i < nk; i++) if (!RpNoQOne(M, keys[i], false)) return false;
    } __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
    return true;
}
// shells (0.35.17, docs/94 §22.20, I-94-476): lots whose Q list is unchanged and that only a lot graph build (W4) or a node
// change (signature) touched get a shell refresh; a batch of shells only walks no map and carries no map order
static RpBatch* RpRebase(const u8* world, const RpLiveLot* set, int n, const u64* gone, int nGone, const u64* unk, int nUnk,
                         const RpTab& exQ, const RpLiveLot* shells = NULL, int nShell = 0) {
    RpBatch* b = (RpBatch*)RpAlloc(sizeof(RpBatch));
    if (!b) return NULL;
    b->lots = (RpBatchLot*)RpAlloc((n + nGone + nShell) * sizeof(RpBatchLot) + 4);
    if (!b->lots) { RpFree(b); return NULL; }
    RpCtx x;
    memset(&x, 0, sizeof(x));
    x.cp = g_rpSimP;
    x.dummyQ = NULL;  // FMEs of other lots are not copied (the map walk below takes only these lots' Qs)
    const bool mapWork = n || nGone || nUnk;
    __try {
        for (int i = 0; i < nGone; i++) { b->lots[b->n].key = gone[i]; b->lots[b->n].removed = true; b->lots[b->n].shell = false; b->n++; }
        for (int i = 0; i < n && !x.fail; i++) {
            RpBatchLot& bl = b->lots[b->n++];
            bl.key = set[i].key, bl.removed = false, bl.shell = false;
            if (!RpLotShell(x, set[i].node, &bl.lot)) x.fail = true;
        }
        for (int i = 0; i < nShell && !x.fail; i++) {
            RpBatchLot& bl = b->lots[b->n++];
            bl.key = shells[i].key, bl.removed = false, bl.shell = true;
            if (!RpLotShell(x, shells[i].node, &bl.lot, false)) x.fail = true;
        }
        if (mapWork) {
        const u8* M = RpP(RpP(world + 0x95C) + 0x24);
        const DWORD mb = RpD(M + 8);
        const DWORD* lb = (const DWORD*)RpP(M + 4);
        int cap = 0;
        // 0.35.12 (I-94-459): the nodes on these lots' Qs are kept from this walk in chain order (the live map does not change
        // under the sim's +0x918), so the FME pass below walks them, not the whole map again (BP: 126k nodes per batch)
        DWORD* mine = NULL;
        DWORD nMine = 0, capMine = 0;
        for (DWORD i = 0; mb > 1 && i < mb && !x.fail; i++)
            for (const u8* nd = (const u8*)(uintptr_t)lb[i]; nd && !x.fail; nd = RpP(nd + 0x10)) {
                if (!RpP(nd + 8)) continue;
                const bool on = RpTabFind(x.qMap, RpD(RpP(nd + 8) + 0xC)) != NULL;
                if (on || RpUnkHas(unk, nUnk, ((u64)RpD(nd + 4) << 32) | RpD(nd))) RpPreParent(x, RpP(nd + 8));
                if (!on) continue;
                if (nMine == capMine) {
                    capMine = capMine ? capMine * 2 : 1024;
                    DWORD* m = (DWORD*)RpAlloc(capMine * 4);
                    if (!m) { x.fail = true; break; }
                    if (mine) { memcpy(m, mine, nMine * 4); RpFree(mine); }
                    mine = m;
                }
                mine[nMine++] = (DWORD)(uintptr_t)nd;
            }
        x.excl = true;  // a refused refresh (a left-out lot's footprint) is skipped, not a failed batch
        for (DWORD i = 0; mb > 1 && i < mb && !x.fail; i++)  // footprints touched on unknown Qs: fresh copies (swapped in on apply)
            for (const u8* nd = (const u8*)(uintptr_t)lb[i]; nd && !x.fail && nUnk; nd = RpP(nd + 0x10))
                if (RpP(nd + 8) && RpUnkHas(unk, nUnk, ((u64)RpD(nd + 4) << 32) | RpD(nd))) RpFpFor(x, RpP(RpP(nd + 8) + 8), RpD(RpP(nd + 8) + 0xC));
        x.excl = false;
        for (DWORD mi = 0; mi < nMine && !x.fail; mi++) {
            {
                const u8* nd = (const u8*)(uintptr_t)mine[mi];
                const u8* f = RpP(nd + 8);
                u8* cf = RpFme(x, f);
                if (!cf) continue;  // (before 0.35.12 the rest of that bucket's chain was skipped too)
                if (b->nMap == cap) {
                    cap = cap ? cap * 2 : 1024;
                    DWORD* m = (DWORD*)RpAlloc(cap * 12);
                    if (!m) { x.fail = true; break; }
                    if (b->map) { memcpy(m, b->map, b->nMap * 12); RpFree(b->map); }
                    b->map = m;
                }
                b->map[b->nMap * 3] = RpD(nd), b->map[b->nMap * 3 + 1] = RpD(nd + 4), b->map[b->nMap * 3 + 2] = (DWORD)(uintptr_t)cf;
                b->nMap++;
                (*(LONG*)(cf + 4))++;  // the batch holds it for its map node (taken over on apply); before the trees, so no orphan
            }
        }
        RpFree(mine);  // shortcut: a fault above leaks it (counted in rpFaults; the batch is dropped and the clone rebuilt)
        for (int i = 0; i < b->n && !x.fail; i++) if (!b->lots[i].removed && !b->lots[i].shell) RpLotFill(x, &b->lots[i].lot);
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) { x.fail = true; ts3native_counters.rpFaults++; }
    b->fps = x.fps, b->kids = x.kids;
    x.fps.k = x.fps.v = NULL, x.kids.k = x.kids.v = NULL;
    RpCtxDone(x);
    if (!x.fail && mapWork) {  // a shells-only batch leaves the map as it is: no order, no resync (RpApplyBatch)
        __try {
            const u8* M = RpP(RpP(world + 0x95C) + 0x24);
            const DWORD mb = RpD(M + 8), cnt = RpD(M + 0xC);
            const DWORD* lb = (const DWORD*)RpP(M + 4);
            b->mapN = mb;
            memcpy(b->policy, M + 0x10, 12);
            b->order = (DWORD*)RpAlloc((cnt + 1) * 20);
            if (!b->order) x.fail = true;
            DWORD skipped = 0;
            for (DWORD i = 0; mb > 1 && i < mb && !x.fail; i++)
                for (const u8* nd = (const u8*)(uintptr_t)lb[i]; nd; nd = RpP(nd + 0x10)) {
                    if (b->nOrder > cnt) { x.fail = true; break; }
                    const u8* f = RpP(nd + 8);
                    if (f && exQ.used && RpTabFind(exQ, RpD(f + 0xC))) { skipped++; continue; }  // a left-out lot's node
                    DWORD* o = b->order + b->nOrder++ * 5;
                    o[0] = RpD(nd), o[1] = RpD(nd + 4);
                    o[2] = f && RpTabFind(g_rpKnownQ, RpD(f + 0xC)) ? RpD(f + 0xC) : 0, o[3] = f ? RpD(f + 0x10) : 0, o[4] = f ? RpD(f + 8) : 0;
                }
            if (b->nOrder + skipped != cnt) x.fail = true;
        } __except (EXCEPTION_EXECUTE_HANDLER) { x.fail = true; }
    }
    if (x.fail) { RpBatchFree(b, true); return NULL; }
    return b;
}

// --- recorder (any writing thread; from SpNote before its dirty lock) ---
#include <psapi.h>  // K32GetProcessMemoryInfo (kernel32 on Windows 7+)
// Prebuild=1 (Rodney 2026-10-18): every lot's graph built on the loading screen, so no lazy first use (LotGraph_Build and the
// clone's rebase copy of that lot) happens in play. The game's own path, as LotSearch_Entry 0x640E66 runs it: for each lot in
// the planner's table that LotTable_Contains 0x628A90 accepts and whose built byte +0x8C is 0, RoomGraph_FirstUse 0x63D2F0
// (thiscall P, entry, outside room word +0x16C) -> LotGraph_Build 0x63CB90 -> LotRouting_FinishBuild 0x63CAF0 ->
// RestoreLotBlockedBits 0x82A190. Sim thread, +0x918 held, before the clone build.
// shortcut: lots not yet in the planner's lot table at world event 7 are not built (the table walk is the only lot list
// used here); rpRebaseLots in play shows what is left.
typedef char(__fastcall* RpFirstUseFn)(const u8* p, void*, u8* entry, DWORD room);  // 0x63D2F0, ret 8
typedef char(__fastcall* RpContainsFn)(const u8* p, void*, DWORD lo, DWORD hi);     // 0x628A90, ret 8
static RpFirstUseFn g_rpFirstUse = (RpFirstUseFn)(uintptr_t)0x0063D2F0;
static RpContainsFn g_rpContains = (RpContainsFn)(uintptr_t)0x00628A90;
static bool g_rpPrebuild;
static LONG RpPrivateKB() {
    PROCESS_MEMORY_COUNTERS_EX m = {};
    m.cb = sizeof(m);
    return K32GetProcessMemoryInfo(GetCurrentProcess(), (PROCESS_MEMORY_COUNTERS*)&m, sizeof(m)) ? (LONG)(m.PrivateUsage >> 10) : 0;
}
static void RpPrebuild(const u8* world) {
    Ts3nCounters& k = ts3native_counters;
    const u8* P = RpP(world + 0x95C);
    const int n = P ? RpLiveLots(P, g_rpLive, kRpMaxLots) : 0;
    LARGE_INTEGER t0;
    QueryPerformanceCounter(&t0);
    const LONG kb0 = RpPrivateKB();
    int built = 0, failed = 0, already = 0;
    for (int i = 0; i < n; i++) {
        u8* g = g_rpLive[i].node + 0x10;
        if (g[0x8C]) { already++; continue; }
        __try {
            if (!g_rpContains(P, NULL, (DWORD)g_rpLive[i].key, (DWORD)(g_rpLive[i].key >> 32))) continue;  // the active lot: plans skip it too
            if (g_rpFirstUse(P, NULL, g, *(const WORD*)(g + 0x16C))) built++; else failed++;
        } __except (EXCEPTION_EXECUTE_HANDLER) { failed++; }
    }
    const LONG us = UsSince(t0), kb = RpPrivateKB() - kb0;
    k.rpPrebuilt += built, k.rpPrebuildFail += failed, k.rpPrebuildUs += us, k.rpPrebuildKB = kb;
    Log("snap replay: prebuilt %d lot graphs (%d already built, %d failed, %d lots in the table) in %ld ms; process private +%ld KB",
        built, already, failed, n, us / 1000, kb);
}
// 0.35.18 (docs/94 §22.21, I-94-478): the leaf remove 0x666010 skips an FME's leaves when the footprint's bounds (vtable
// +0x2C) miss the Q's rect (0x6660CF..0x6660FC) [verified disassembly]. The REM record carries the live answers of the
// removed id's / ~id's 0x200 FMEs on that Q, asked here just before the game asks them itself; the replay compares them
// with the clone copy's (RpRemDiff). shortcut: kRpRemLiveMax FMEs per remove (a lot box + floor + child is 9), the rest unchecked
typedef float* (__fastcall* RpBoundsFn)(void* fp, void* edx, float* out, DWORD level, float z);
static const int kRpRemLiveMax = 16;
// 0.35.21 (docs/94 §22.24.5): the inputs of the level-box answer 0x80EF80 (classes 0xFEB848 / 0xFEBD10 / ..., and the child
// 0x806C50 through its parent with z = the child's +0x10) [verified disassembly]: the level entry's box +0x10..+0x1C, the
// widening (+0x34 bit 0x2000000: an empty child list widens by +0x30 (0x8072F0), else by z). IP 0.35.18: 256 REM differences,
// child copies widened by 0.2-2.0 more than live, 0xFEB848 copies off by ~0.001: inputs that change in live without a record
// (W6 transforms and the child's +0x10 are not records). The consumer copies them into the clone copy before the remove
struct RpRemPatch { float box[4]; float w30, z; DWORD f34; u8 has, empty, child, pad; };
static DWORD g_rpLevelBox = 0x0080EF80;  // the selftest points it at its stand-in
struct RpRemLiveE { DWORD cls, lo, hi, lvl; float b[4]; RpRemPatch p; };
struct RpRemLive { int n; RpRemLiveE e[kRpRemLiveMax]; };
// the footprint that answers through 0x80EF80 (fp, or a child's parent), NULL = another answer (left as before)
static const u8* RpBoxFp(const u8* fp) {
    const u8* a = RpIsChild(fp) ? RpP(fp + 0xC) : fp;
    return a && RpD(RpP(a) + 0x2C) == g_rpLevelBox ? a : NULL;
}
static void RpRemPatchTake(RpRemPatch& p, const u8* fp, DWORD lvl) {  // after the live +0x2C: the level entry is valid
    memset(&p, 0, sizeof(p));
    const u8* a = RpBoxFp(fp);
    if (!a || lvl >= RpLevels(a) || !RpP(a + 0x38)) return;
    memcpy(p.box, RpP(a + 0x38) + lvl * 0x24 + 0x10, 16);
    p.w30 = *(const float*)(a + 0x30), p.f34 = RpD(a + 0x34), p.empty = RpP(a + 0x24) == a + 0x24;
    p.child = a != fp, p.z = p.child ? *(const float*)(fp + 0x10) : 0.f;
    p.has = 1;
}
// the copy takes the live inputs (same shape: class, level count, child list empty or not); false = not patchable
static bool RpRemPatchIn(const RpRemPatch& p, u8* fp, DWORD lvl) {
    u8* a = p.has ? (u8*)RpBoxFp(fp) : NULL;
    if (!a || (a != fp) != (p.child != 0) || lvl >= RpLevels(a) || !RpP(a + 0x38) || (RpP(a + 0x24) == a + 0x24) != (p.empty != 0)) return false;
    memcpy(RpP(a + 0x38) + lvl * 0x24 + 0x10, p.box, 16);
    *(float*)(a + 0x30) = p.w30;
    *(DWORD*)(a + 0x34) = (RpD(a + 0x34) & ~0x2000000u) | (p.f34 & 0x2000000u);
    if (p.child) *(float*)(fp + 0x10) = p.z;
    return true;
}
static RpRemLive* RpRemLiveTake(u8* q, DWORD lo, DWORD hi) {
    RpRemLive t;
    t.n = 0;
    __try {
        const u8* m = RpP(RpP(q + 0x20) + 0x24);
        const DWORD nb = m ? RpD(m + 8) : 0;
        const DWORD* bk = nb > 1 ? (const DWORD*)RpP(m + 4) : NULL;  // 1 bucket = the empty map's shared one
        for (int pass = 0; bk && pass < 2; pass++) {
            const DWORD klo = pass ? ~lo : lo, khi = pass ? ~hi : hi;
            for (const u8* nd = (const u8*)(uintptr_t)bk[klo % nb]; nd && t.n < kRpRemLiveMax; nd = RpP(nd + 0x10)) {
                if (RpD(nd) != klo || RpD(nd + 4) != khi) continue;
                const u8* f = RpP(nd + 8);
                u8* fp = f && RpP(f + 0xC) == q && (RpD(f + 0x10) & 0x200) ? RpP(f + 8) : NULL;
                if (!fp) continue;
                RpRemLiveE& e = t.e[t.n];
                memset(e.b, 0, sizeof(e.b));
                const float* r = ((RpBoundsFn)(uintptr_t)RpD(RpP(fp) + 0x2C))(fp, NULL, e.b, f[0x10], 0.0f);
                if (r != e.b) { if (!r) continue; memcpy(e.b, r, sizeof(e.b)); }
                e.cls = RpD(fp), e.lo = RpD(fp + 0x14), e.hi = RpD(fp + 0x18), e.lvl = f[0x10];
                RpRemPatchTake(e.p, fp, e.lvl);
                t.n++;
            }
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) { return NULL; }  // no live answers: the replay checks only flat copies
    if (!t.n) return NULL;
    RpRemLive* p = (RpRemLive*)RpAlloc(sizeof(RpRemLive));
    if (p) memcpy(p, &t, sizeof(t));
    return p;
}
static void RpForceLot(u64 key) {  // the lot rebases at the next cut (any thread)
    while (InterlockedExchange(&g_rpRingLock, 1)) YieldProcessor();
    int j = 0;
    while (j < g_rpNForce && g_rpForce[j] != key) j++;
    if (j == g_rpNForce) { if (g_rpNForce < 64) g_rpForce[g_rpNForce++] = key; else g_rpForceAll = 1; }
    InterlockedExchange(&g_rpRingLock, 0);
}

// 0.36.35 (docs/157 §4.2, docs/156 F1, I-156-1): replayed-insert answer check. The INS record copies the footprint at the
// game's ApplyInsert entry, but the replay inserts whichever copy RpFpUse picks (an older copy refreshed in place keeps a
// child's parent pointer; an own-block child answers through its pinned parent copy), and the leaf insert 0x6653A0 uses that
// copy's +0x2C answer. BP 0.36.13 / 0.36.33 audits found clone Q 1 trees short of live by 30-603 full-mask entries of child
// footprints 0xFEBF90 while the whole map counts matched (FMEs present, fewer leaves) [measured, logs]: a copy answering a
// smaller rect than live at its insert [inferred, I-157-6]. The record now carries live's answers (the footprint and its
// list children, levels 0..3, asked here just before the game asks them itself); the consumer compares them with the
// answers of the FMEs that insert made and forces the lot on a difference (logged with both rects).
// [RouteSnap] InsCheck: 0 off, 1 log only (default: the record's live answer is asked before the game's insert, which may
// transform a first-inserted footprint's points, W6d via 0x80E0E0; a difference there would be ours, not the copy's), 2 log + force
static LONG g_rpInsCheck = 1;
struct RpInsLiveE { DWORD live, lvl; float b[4]; };
struct RpInsLive { int n; RpInsLiveE e[kRpRemLiveMax]; };
static void RpInsLiveOne(RpInsLive& t, u8* fp) {
    const RpCls* c = RpClass(RpD(fp));
    if (!c) return;
    const DWORD nl = c->cache ? RpLevels(fp) : 1;  // slot +0 is the level count only on the cache classes
    for (DWORD l = 0; l < nl && l < 4 && t.n < kRpRemLiveMax; l++) {
        RpInsLiveE& e = t.e[t.n];
        memset(e.b, 0, sizeof(e.b));
        const float* r = ((RpBoundsFn)(uintptr_t)RpD(RpP(fp) + 0x2C))(fp, NULL, e.b, l, 0.0f);
        if (!r) continue;
        if (r != e.b) memcpy(e.b, r, sizeof(e.b));
        e.live = (DWORD)(uintptr_t)fp, e.lvl = l;
        t.n++;
    }
}
static RpInsLive* RpInsLiveTake(u8* fp) {
    RpInsLive t;
    t.n = 0;
    __try {
        RpInsLiveOne(t, fp);
        if (RpHasList(fp))
            for (u8* l = RpP(fp + 0x24); l && l != fp + 0x24 && t.n < kRpRemLiveMax; l = RpP(l)) if (u8* ch = RpP(l + 8)) RpInsLiveOne(t, ch);
    } __except (EXCEPTION_EXECUTE_HANDLER) { return NULL; }
    if (!t.n) return NULL;
    RpInsLive* p = (RpInsLive*)RpAlloc(sizeof(RpInsLive));
    if (p) memcpy(p, &t, sizeof(t));
    return p;
}

// 0.36.35 (docs/157 §4.1, docs/156 F1, U1): Footprint_SetLockKeyObject 0x807110 swaps fp+0x20 of an inserted footprint with
// no W1/W2 record, so the clone kept the set it had at the INS (NULL: "no keys") until a re-insert, rebase or epoch [verified,
// docs/156 §1]. 0x807110 is T-S7's site (steerro.cpp, one owner per docs/68), so the cover hooks its only IC caller instead:
// the call site in RouteManager_FootprintLockKeyApply 0x82B2B0, `mov edx,[edi]; mov eax,[edx+0x8C]` at 0x82B3A1 (8 bytes,
// no relative operand; the only branch to the range is 0x82B349's jne to 0x82B3A1 itself [verified, capstone + raw scan of
// .text]). There edi = the routing footprint, ebp = the new set, ebx = the routing world. A change on an inserted footprint
// forces every live lot that holds an FME of its key / ~key (sim thread, +0x918 not needed: only the sim writes the live map)
// and counts as a routing write (P5c's stale test); Q-less FMEs are refreshed through the cut's Q-less pass. The other writer
// of fp+0x20, slot +0xA8 0x80C8C0 from the lot rebuild path, ends in a W3/W4 (I-156-6, open).
static const DWORD kRpLkAt = 0x0082B3A1;
static void* g_rpLkTramp;
static volatile LONG g_rpLkLog;
static void __cdecl RpLockKeyEnter(DWORD* slot) {
    if (!g_rpOn || !g_rpHave || t_rpReplay || !RgIsSim(GetCurrentThreadId())) return;
    Ts3nCounters& k = ts3native_counters;
    const u8* fp = (const u8*)(uintptr_t)slot[-8];
    const DWORD set = slot[-6];
    const u8* world = (const u8*)(uintptr_t)slot[-4];
    u64 lots[8];
    int n = 0;
    bool noq = false, over = false;
    u64 key = 0;
    __try {
        if (!fp || RpD(fp + 0x20) == set || !(RpD(fp + 0x34) & 0x7C000000u) || !RpFpKey(fp, &key)) return;
        const u8* m = RpP(RpP(world + 0x95C) + 0x24);
        const DWORD nb = m ? RpD(m + 8) : 0;
        const DWORD* bk = nb > 1 ? (const DWORD*)RpP(m + 4) : NULL;
        for (int pass = 0; bk && pass < 2; pass++) {
            const u64 kk = pass ? ~key : key;
            for (const u8* nd = (const u8*)(uintptr_t)bk[(DWORD)kk % nb]; nd; nd = RpP(nd + 0x10)) {
                if (RpD(nd) != (DWORD)kk || RpD(nd + 4) != (DWORD)(kk >> 32) || !RpP(nd + 8)) continue;
                const u8* q = RpP(RpP(nd + 8) + 0xC);
                if (!q) { noq = true; continue; }
                const u64 lot = ((u64)RpD(q + 0x1C) << 32) | RpD(q + 0x18);
                int i = 0;
                while (i < n && lots[i] != lot) i++;
                if (i == n) { if (n < 8) lots[n++] = lot; else over = true; }
            }
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) { over = true; }
    InterlockedIncrement(&g_spWrites);  // a routing input changed: a P5c answer cut before it is stale
    if (over) { while (InterlockedExchange(&g_rpRingLock, 1)) YieldProcessor(); g_rpForceAll = 1; InterlockedExchange(&g_rpRingLock, 0); }
    for (int i = 0; i < n; i++) RpForceLot(lots[i]);
    if (noq || (!n && !over)) { RpNoQNote(key, false); RpNoQNote(~key, false); InterlockedIncrement(&k.rpLockKeyNoLot); }
    if (n || over) InterlockedIncrement(&k.rpLockKeyForce);
    const LONG c = InterlockedIncrement(&g_rpLkLog);
    if (c <= 8 || !(c & (c - 1)))
        Log("snap replay: lock-key set of footprint 0x%08lX%08lX (class 0x%08lX) changed in place: %d lots forced%s%s (%ld so far)",
            (DWORD)(key >> 32), (DWORD)key, RpD(fp), n, over ? ", all lots" : "", noq ? ", Q-less nodes refreshed" : "", c);
}
static __declspec(naked) void RpLockKeyStub() {
    __asm pushad __asm pushfd __asm lea eax, [esp + 36] __asm push eax __asm call RpLockKeyEnter __asm add esp, 4
    __asm popfd __asm popad __asm jmp dword ptr [g_rpLkTramp]
}
static void RpLockKeyInstall() {
    static const u8 kPro[] = {0x8B, 0x17, 0x8B, 0x82, 0x8C, 0x00, 0x00, 0x00};  // mov edx,[edi]; mov eax,[edx+0x8C]
    g_rpLkTramp = InstallDetour("RouteManager_FootprintLockKeyApply set call (lock-key cover)", kRpLkAt, kPro, sizeof(kPro), (void*)RpLockKeyStub, &g_rpLkTramp);
    if (!g_rpLkTramp) Log("route snapshot replay: lock-key cover not installed: P5 answers may read a stale lock-key set (docs/156 U1)");
}
static void RpPreBody(int site, const DWORD* slot);
static void RpPre(int site, const DWORD* slot) {
    if (!g_rpOn) return;
    __try { RpPreBody(site, slot); } __except (EXCEPTION_EXECUTE_HANDLER) { InterlockedIncrement(&ts3native_counters.rpFaults); RP_NEED_EPOCH(); }
}
static void RpPreBody(int site, const DWORD* slot) {
    const SnSite& s = kSn[site];
    if (site == SN_WEV) {
        const DWORD id = slot[2];
        if (id == 5 || id == 9 || id == 0x0A) RP_NEED_EPOCH();
        if ((id == 9 || id == 0x0A) && g_rpEpochHeap) g_rpLoadWaitUntil = GetTickCount64() + kRpLoadWaitMs;  // one build per load
        if (id == 7) g_rpLoadWaitUntil = 0;
        if (id == 7 && RgIsSim(GetCurrentThreadId())) {  // the load's G build has run (docs/94 §9.1): build the clone now
            const u8* world = *g_spWorldSlot;
            static const DWORD kInf = 0xFFFFFFFF;
            if (world && RpP(world + 0x95C) && g_spAcq((void*)(world + 0x918), NULL, &kInf) > 0) {
                if (g_rpPrebuild) RpPrebuild(world);
                RP_REBUILD(world);
                g_spRel((void*)(world + 0x918), NULL);
                g_rpPub = g_rpHead;
                if (g_rpThreaded) SetEvent(g_rpEvent);
            } else RP_NEED_EPOCH();
        }
        return;
    }
    if (!g_rpHave) return;
    switch (s.leaf) {
        case SL_W1: case SL_W2: {
            if (s.leaf == SL_W1 && g_wpLot) WpLotInsert(slot[-2], (u8*)(uintptr_t)slot[1]);  // children too (their own W1 call)
            if (s.leaf == SL_W2 && g_wpLot) WpLotRemove(slot[1], slot[2]);  // [Watchpoint] Follow: a watched child's remove
            if (s.leaf == SL_W1 && slot[0] == kRpRet665F87) return;  // W1's own child recursion: the replayed parent inserts them
            u8* fp = s.leaf == SL_W1 ? (u8*)(uintptr_t)slot[1] : NULL;
            if (!RpTabFind(g_rpKnownQ, slot[-2])) {  // the consumer will skip it: refresh what it touches at the next cut
                u64 key = ((u64)slot[2] << 32) | slot[1];
                __try { if (fp) key = ((u64)RpD(fp + 0x18) << 32) | RpD(fp + 0x14); } __except (EXCEPTION_EXECUTE_HANDLER) {}
                while (InterlockedExchange(&g_rpRingLock, 1)) YieldProcessor();
                RpUnkAdd(key), RpUnkAdd(~key);
                InterlockedExchange(&g_rpRingLock, 0);
            }
            if (s.leaf == SL_W1) {
                u8* cp = fp ? RpFpCopy(fp) : NULL;
                if (fp && !cp) {  // refused copy (unknown class, ...): the cut rebases that Q's lot before its SYNC and audit
                    while (InterlockedExchange(&g_rpRingLock, 1)) YieldProcessor();
                    int i = 0;
                    while (i < g_rpNBadQ && g_rpBadQ[i] != slot[-2]) i++;
                    if (i == g_rpNBadQ) { if (g_rpNBadQ < 16) g_rpBadQ[g_rpNBadQ++] = slot[-2]; else g_rpForceAll = 1; }
                    InterlockedExchange(&g_rpRingLock, 0);
                }
                RpPush(RP_INS, slot[-2], slot[1], slot[2], cp, cp && g_rpInsCheck ? (DWORD)(uintptr_t)RpInsLiveTake(fp) : 0);
            }
            else RpPush(RP_REM, slot[-2], slot[1], slot[2], RpRemLiveTake((u8*)(uintptr_t)slot[-2], slot[1], slot[2]));
            break;
        }
        case SL_GRIDADD: case SL_LOTREMOVEFP: {  // Q-less map nodes may change for this key (lot 0 only)
            u64 key = 0;
            if (s.leaf == SL_LOTREMOVEFP) { if (slot[1] | slot[2]) break; key = ((u64)slot[4] << 32) | slot[3]; RpNoQNote(key, false); }
            else if (!slot[2] && slot[1]) { const bool ok = RpFpKey((const u8*)(uintptr_t)slot[1], &key); RpNoQNote(key, !ok); }
            break;
        }
        case SL_W3: case SL_W4: {  // a tree rebuilt or a lot graph built: rebase at the next cut, skip its records till then
            if (s.leaf == SL_W3 && !RpW3Builds(slot)) break;  // a get of an existing Q (0.30.2)
            const u64 key = s.leaf == SL_W3 ? ((u64)slot[2] << 32) | slot[1] : ((u64)RpD((const u8*)(uintptr_t)slot[1] + 4) << 32) | RpD((const u8*)(uintptr_t)slot[1]);
            u64* list = s.leaf == SL_W3 ? g_rpForce : g_rpShellF;  // W4: a shell refresh unless the cut finds more (0.35.17)
            int& nl = s.leaf == SL_W3 ? g_rpNForce : g_rpNShellF;
            while (InterlockedExchange(&g_rpRingLock, 1)) YieldProcessor();
            int i = 0;
            while (i < nl && list[i] != key) i++;
            if (i == nl) { if (nl < 64) list[nl++] = key; else g_rpForceAll = 1; }
            InterlockedExchange(&g_rpRingLock, 0);
            RpPush(RP_STALE, (DWORD)key, (DWORD)(key >> 32), 0, NULL);
            break;
        }
        default: break;
    }
}

// rebase split by kind (sim; no counters, docs/94 §22.20): batches with map work (full lots, gone lots, unknown keys) and
// shells-only batches, logged every 5 min; the totals stay in rpRebaseLots / rpRebaseUs
static LONG g_rpSplit[2][3];  // [full, shells only][batches, lots, us]; full batches count their shells as lots too
static LONG g_rpShellLots, g_rpMapBatches;  // all shell refreshes, all batches with map work (selftest)
static ULONGLONG g_rpSplitAt;
static void RpRebaseSplit(int full, int shells, bool mapWork, LONG us) {
    LONG* s = g_rpSplit[mapWork ? 0 : 1];
    s[0]++, s[1] += full + shells, s[2] += us;
    g_rpShellLots += shells, g_rpMapBatches += mapWork;
    const ULONGLONG now = GetTickCount64();
    if (!g_rpSplitAt) g_rpSplitAt = now;
    if (now - g_rpSplitAt < 300000) return;
    Log("snap replay rebases, last %llu s: with map work %ld batches, %ld lots, %.2f ms/s; shells only %ld batches, %ld lots, "
        "%.2f ms/s", (now - g_rpSplitAt) / 1000, g_rpSplit[0][0], g_rpSplit[0][1], g_rpSplit[0][2] / (double)(now - g_rpSplitAt),
        g_rpSplit[1][0], g_rpSplit[1][1], g_rpSplit[1][2] / (double)(now - g_rpSplitAt));
    memset(g_rpSplit, 0, sizeof(g_rpSplit));
    g_rpSplitAt = now;
}

// --- cut (sim, +0x918 held; from SpBuildVersion through the strategy's begin) ---
// 0.35.12 (docs/94 §22.15, I-94-460): the whole cut owns the private pool set (rebase, clone build, a full ring's batch
// free); while a P4 run owns it the cut is deferred: nothing taken, nothing published (P4's cut is refused "noclone"), the
// forces and records wait for the next cut
static volatile bool g_rpCutDeferred;  // the last cut was deferred (sim; PcCutFn reads it right after)
static LONG g_rpCutDeferN;
// 0.35.20 (docs/94 §22.23): the cut's own split, logged every 5 min (the "lots (replay)" share of the snap cut split). Each
// step's time runs from the previous mark; log only, no counters (sim thread)
enum { RC_PRE, RC_LIVE, RC_NOQ, RC_SIG, RC_EPOCH, RC_REBASE, RC_DEFER, RC_EXN, RC_SYNC, RC_AUDIT, RC_QUIET, RC_N };
static const char* const kRcName[RC_N] = {"pre", "live lots", "records/noq", "signatures", "epoch builds", "rebases", "deferred",
                                          "ex nodes", "sync", "audit", "quiet reused"};
static LONGLONG g_rcTicks[RC_N], g_rcT;
static LONG g_rcN[RC_N], g_rcCuts, g_rcQuiet, g_rcLots, g_rcLateScans, g_rcLateLots;
static LONG64 g_rcCy, g_rcCyReb;  // thread CPU cycles of the replay cut and of its rebases in this window (0.35.21)
static ULONGLONG g_rcAt;

// 0.35.21 (docs/94 §22.24.3, I-94-485): quiet-cut reuse, [RouteSnap] ReplayQuietMs (0 = off, the default). A cut with no ring
// record, no recorded routing write (g_spWrites) and no routing IC (g_spIcN) since the last full cut, no force / shell /
// unknown / Q-less / bad-Q note pending and no audit due skips the live lot walk and the signature scan: the clone at the
// previous position is the live state as far as every hook knows. The signature scan is the only catch for node changes no
// hook sees (BP 0303: 58 signature rebases in ~12 min), so a full cut still runs once ReplayQuietMs passed since the last one;
// a change no hook saw waits at most that long. The split line counts the full scans after reuses and the lots they rebased
// by signature (the exposure)
static DWORD g_rpQuietMs;
static DWORD g_rpSigMs;           // [RouteSnap] ReplaySigMs (0.35.23, docs/94 §22.26.4), 0 = a signature scan every cut
static ULONGLONG g_rpSigAt;       // the last scan (sim)
static bool g_rpSigSkipped;       // a cut skipped the scan since then
static LONG g_rcSigScans, g_rcSigLate;  // this window: scans; lots a scan after skipped cuts rebased by signature (exposure)
static LONG g_rpLazyKeep;  // [RouteSnap] LazyKeep (p4.cpp P4LazyKeep; docs/94 §22.24.1, §22.25), 0 = every lazy stop rebuilds
static LONG g_rpQW, g_rpQIc, g_rpQSkipped;
static bool g_rpQOk;  // the last cut was a full one that published SYNC (not deferred, no rebuild)
static ULONGLONG g_rpQFullAt;
static DWORD g_rpQLots, g_rpQExN;  // that cut's live lot count (minus left-out lots) and left-out map entries
static void RcMark(int step, bool count = true) {
    LARGE_INTEGER t;
    QueryPerformanceCounter(&t);
    g_rcTicks[step] += t.QuadPart - g_rcT, g_rcN[step] += count;
    g_rcT = t.QuadPart;
}
static void RcLog() {
    const ULONGLONG now = GetTickCount64();
    if (!g_rcAt) g_rcAt = now;
    if (now - g_rcAt < 300000) return;
    LARGE_INTEGER f;
    QueryPerformanceFrequency(&f);
    const double ms = (double)(now - g_rcAt);
    char line[1536];
    int at = _snprintf_s(line, sizeof(line), _TRUNCATE, "snap replay cut split, last %.0f s, %ld cuts (%ld quiet: no record since the last cut), %.1f lots/cut:",
                         ms / 1000, g_rcCuts, g_rcQuiet, g_rcCuts ? (double)g_rcLots / g_rcCuts : 0.0);
    for (int i = 0; i < RC_N && at > 0; i++) {
        const int w = _snprintf_s(line + at, sizeof(line) - at, _TRUNCATE, "%s %s %ld %.2f ms/s", i ? "," : "", kRcName[i], g_rcN[i],
                                  g_rcTicks[i] * 1000.0 / f.QuadPart / ms * 1000.0);
        at = w < 0 ? -1 : at + w;
    }
    Log("%s", line);  // 0.35.23: the rest on its own line (Log cuts at ~510 characters: the 0.35.21 tail never reached the log)
    Log("snap replay cut split (2): thread CPU (2.3 GHz nominal) replay cut %.2f, rebases %.2f cyc-ms/s; ReplayQuietMs %lu: full "
        "scans after a reuse %ld, lots they rebased by signature %ld; ReplaySigMs %lu: signature scans %ld, lots rebased late by "
        "signature %ld", CycMsPerS(g_rcCy, ms / 1000), CycMsPerS(g_rcCyReb, ms / 1000), g_rpQuietMs, g_rcLateScans, g_rcLateLots,
        g_rpSigMs, g_rcSigScans, g_rcSigLate);
    memset(g_rcTicks, 0, sizeof(g_rcTicks)), memset(g_rcN, 0, sizeof(g_rcN));
    g_rcCuts = g_rcQuiet = g_rcLots = g_rcLateScans = g_rcLateLots = g_rcSigScans = g_rcSigLate = 0, g_rcCy = g_rcCyReb = 0;
    g_rcAt = now;
}
static void RpCutBody(const u8* world);
// 0.35.13 (docs/94 §22.16, I-94-465): the set is taken only where pool work happens (RpEpoch tries it, the rebase tries it);
// a cut with nothing to rebase (most cuts) publishes its records, SYNC and AUDIT while a P4 run owns the set, so P4's cut is
// no longer refused "noclone" for that (0.35.12 deferred every such cut)
static void RpCut(const u8* world) {
    g_rpCutDeferred = false;
    if (!g_rpOn) return;
    const LONG me = (LONG)GetCurrentThreadId();
    const bool nested = g_rpOpTid == me;
    LARGE_INTEGER t0;
    QueryPerformanceCounter(&t0);
    g_rcT = t0.QuadPart;
    const LONG64 cy0 = CycNow();
    g_rcCuts++, g_rcQuiet += g_rpHead == g_rpPub;
    __try { RpCutBody(world); } __finally { if (!nested && g_rpOpTid == me) RpOpLeave(0); }  // a fault unwinding through here must not keep the set
    g_rcCy += CycNow() - cy0;
    RcLog();
}
static bool RpQuietCut(const u8* world, bool qok, LONG w, LONG ic) {
    if (!g_rpQuietMs || !qok || w != g_rpQW || ic != g_rpQIc || g_rpHead != g_rpPub) return false;
    if (GetTickCount64() - g_rpQFullAt >= g_rpQuietMs) return false;
    if (g_rpAuditEvery && (ts3native_counters.spCuts + 1) % (LONG)g_rpAuditEvery == 0) return false;  // an audit hashes live lots
    while (InterlockedExchange(&g_rpRingLock, 1)) YieldProcessor();
    const bool q = !g_rpNForce && !g_rpForceAll && !g_rpNShellF && !g_rpNUnk && !g_rpUnkAll && !g_rpNNoQ && !g_rpNoQAll && !g_rpNBadQ;
    InterlockedExchange(&g_rpRingLock, 0);
    if (!q) return false;
    const u8* M = RpP(RpP(world + 0x95C) + 0x24);
    RpSyncRec* sy = (RpSyncRec*)RpAlloc(sizeof(RpSyncRec));  // a SYNC per cut as before: each P4 cut keeps its own position
    if (sy) { sy->mapN = RpD(M + 8), sy->mapCount = RpD(M + 0xC) - g_rpQExN, sy->lots = g_rpQLots; RpPush(RP_SYNC, 0, 0, 0, sy); }
    g_rpQOk = true, g_rpQSkipped++;
    RcMark(RC_QUIET);
    return true;
}
static void RpCutBody(const u8* world) {
    Ts3nCounters& k = ts3native_counters;
    const bool qok = g_rpQOk;
    g_rpQOk = false;  // set again only by a full cut that published its SYNC, or a reuse
    if (g_rpBytes > g_rpCapBytes) {
        g_rpOn = 0;
        k.rpCapOff = 1;
        k.rpReady &= ~1;
        Log("snap replay: %ld KB, above CapMB: switched off", g_rpBytes >> 10);
        if (g_rpThreaded) SetEvent(g_rpEvent);
        return;
    }
    if (g_rpNeedEpoch && world == g_rpFailWorld && GetTickCount64() < g_rpRetryAt) goto publish;  // backing off; no audit of a stale clone
    if (g_rpHave && !g_rpNeedEpoch && world == g_rpWorld && PlMoved(g_rpSimSeen, RpP(world + 0x95C))) {
        k.plEpochs++;  // PathPlanner_Reset recreated the game's pools: new epoch (docs/94 §16.6 step 3)
        RP_NEED_EPOCH();
    }
    // a rebuild waits while the last EPOCH is not applied yet: each clone is ~40 MB on BP, and 0.32.4 had up to three
    // alive (126 MB of 128) when the consumer lagged behind rebuilds (0.32.8)
    if (g_rpNeedEpoch && g_rpHave && world == g_rpWorld && (LONG)(g_rpEpochAt - g_rpTail) > 0) goto publish;
    if (g_rpLoadWaitUntil && (!g_rpHave || g_rpNeedEpoch || world != g_rpWorld)) {  // EpochHeap: the load's build waits for event 7
        if (GetTickCount64() < g_rpLoadWaitUntil) { k.rpLoadWaitCuts++; goto publish; }
        g_rpLoadWaitUntil = 0;
        k.rpLoadWaitTimeouts++;
    }
    if (!g_rpHave || g_rpNeedEpoch || world != g_rpWorld) {
        if (!g_rpNeedEpoch) g_rpEpochWhy = __LINE__;
        RcMark(RC_PRE);
        RpEpoch(world);
        RcMark(RC_EPOCH);
        goto publish;
    }
// a rebuild in the cut: the time so far goes to `step`, the build to RC_EPOCH
#define RC_REBUILD(step) { RcMark(step); RP_REBUILD(world); RcMark(RC_EPOCH); goto publish; }
    {
        const LONG qw = g_spWrites, qic = g_spIcN;  // +0x918 held: no recorded write lands during this cut
        if (RpQuietCut(world, qok, qw, qic)) goto publish;
        g_rpQW = qw, g_rpQIc = qic;
        RcMark(RC_PRE);
        const u8* P = RpP(world + 0x95C);
        const int n = RpLiveLots(P, g_rpLive, kRpMaxLots);
        RcMark(RC_LIVE);
        if (n < 0) { RP_NEED_EPOCH(); return; }
        g_rcLots += n;
        u64 force[64];
        static u64 unk[256];
        int nForce, nUnk;
        bool all, unkAll;
        while (InterlockedExchange(&g_rpRingLock, 1)) YieldProcessor();
        nForce = g_rpNForce, all = g_rpForceAll != 0, nUnk = g_rpNUnk, unkAll = g_rpUnkAll != 0;
        memcpy(force, g_rpForce, nForce * 8);
        u64 shellF[64];
        const int nShellF = g_rpNShellF;
        memcpy(shellF, g_rpShellF, nShellF * 8);
        u64 softP[64];  // the soft resync's share of the forces (counted only)
        const int nSoftP = g_rpNSoftPend;
        memcpy(softP, g_rpSoftPend, nSoftP * 8);
        const int pruned = nSoftP ? RpSoftPendPrune(g_rpLive, n) : 0;
        memcpy(unk, g_rpUnk, nUnk * 8);
        DWORD badQ[16];
        const int nBad = g_rpNBadQ;
        memcpy(badQ, g_rpBadQ, nBad * 4);
        g_rpNForce = 0, g_rpForceAll = 0, g_rpNShellF = 0, g_rpNUnk = 0, g_rpUnkAll = 0, g_rpNBadQ = 0;
        u64 held = g_rpHoldKey;  // AuditHoldRebase: this lot is not rebased in this cut, at most one cut
        if (held && g_rpHoldCuts++ >= 1) g_rpHoldLast = held, g_rpHoldKey = 0, k.rpHold[2]++;
        const u64 heldOut = held && !g_rpHoldKey ? held : 0;
        if (heldOut) held = 0;
        InterlockedExchange(&g_rpRingLock, 0);
        if (heldOut)
            Log("snap replay audit: lot 0x%08lX%08lX rebase hold released by timeout (the diff did not run within one cut; %ld so far)",
                (DWORD)(heldOut >> 32), (DWORD)heldOut, k.rpHold[2]);
        bool heldSkip = false;
        if (pruned && g_rpSoftPruned <= 16)
            Log("snap replay: %d soft-forced lot(s) left out of the clone or gone dropped from the pending forces (%ld so far, I-94-452)",
                pruned, g_rpSoftPruned);
        if (unkAll) RC_REBUILD(RC_NOQ)  // too many keys touched on unknown Qs: rebuild
        if (!RpNoQCut(P)) RC_REBUILD(RC_NOQ)  // Q-less nodes first: a batch below re-chains the map to live order
        // an insert the copy refused on a lot the clone holds: rebuild now (that lot is left out), before this cut's SYNC/audit
        bool badIn = false;
        for (int i = 0; i < n && nBad && !badIn; i++) {
            if (g_rpNEx && RpIsEx(g_rpLive[i].key)) continue;
            __try {
                const u8* g = g_rpLive[i].node + 0x10;
                for (const u8* qp = RpP(g + 0x74); qp && qp < RpP(g + 0x78) && qp < RpP(g + 0x74) + 64 && !badIn; qp += 4)
                    for (int b = 0; b < nBad; b++) badIn |= RpD(qp) == badQ[b];
            } __except (EXCEPTION_EXECUTE_HANDLER) { badIn = true; }
        }
        if (badIn) RC_REBUILD(RC_NOQ)
        RcMark(RC_NOQ);
        static RpLiveLot set[kRpMaxLots], shl[kRpMaxLots];
        static u64 gone[kRpMaxLots];
        int ns = 0, ng = 0, nsh = 0;
        RpTab exLive = {};  // live Qs of the left-out lots (they stay out until the next epoch build)
        RpExQs(g_rpLive, n, exLive);
        int nExLive = 0;
        LONG why0[sizeof(k.rpRebaseWhy) / sizeof(LONG)];
        memcpy(why0, k.rpRebaseWhy, sizeof(why0));
        const LONG qsame0 = k.rpRebaseQSame;
        // 0.35.20 (docs/94 §22.23): the mirror is the live table in its last order; while that order holds (the usual cut) each
        // lot finds its mirror by index and no lot is gone (both were linear searches per lot)
        bool inPlace = n == g_rpNMir;
        // 0.35.23 (docs/94 §22.26.4): [RouteSnap] ReplaySigMs (0 = every cut, the default): the signature scan, the one catch
        // for node changes no hook sees, at most every N ms; forces, shells, new and gone lots stay per cut. The mirror keeps
        // the unscanned lots' old signatures (RpMirrorSet below), so a change found late is still found
        const ULONGLONG sigT = GetTickCount64();
        const bool sigNow = !g_rpSigMs || sigT - g_rpSigAt >= g_rpSigMs;
        const bool sigLate = sigNow && g_rpSigSkipped;  // the first scan after skipped cuts: its signature lots count as late
        if (sigNow) g_rpSigAt = sigT, g_rcSigScans++, g_rpSigSkipped = false;
        else g_rpSigSkipped = true;
        int nSigLots = 0;
        for (int i = 0; i < n; i++) {
            const bool at = i < g_rpNMir && g_rpMir[i].key == g_rpLive[i].key;
            inPlace &= at;
            if (g_rpNEx && RpIsEx(g_rpLive[i].key)) { nExLive++; continue; }
            const RpMirror* m = at ? &g_rpMir[i] : RpMirrorFind(g_rpLive[i].key);
            bool f = all;
            for (int j = 0; !f && j < nForce; j++) f = force[j] == g_rpLive[i].key;
            int why = 0;  // rpRebaseWhy (0.35.5): forced (by a write or a soft resync), signature, new
            bool sh = false;  // 0.35.17: a shell refresh (W4 or a node change, the Q list as the clone holds it)
            if (f) {
                for (int j = 0; j < nSoftP; j++) if (softP[j] == g_rpLive[i].key) why = 1;
            } else if (!m) f = true, why = 3;
            else {
                for (int j = 0; !f && j < nShellF; j++) f = shellF[j] == g_rpLive[i].key;
                const bool sig = !f && sigNow && m->sig != RpSig(g_rpLive[i].node);
                if (sig) f = true, why = 2, nSigLots++;
                const bool qsame = f && m->qsig == RpQSig(g_rpLive[i].node);  // only a rebase reads it (0.35.20)
                if (sig && qsame) k.rpRebaseQSame++;
                sh = qsame;
            }
            if (f && held && !all && m && g_rpLive[i].key == held) f = false, heldSkip = true;  // kept drifted for the diff
            if (f && sh) shl[nsh++] = g_rpLive[i], k.rpRebaseWhy[why]++;
            else if (f) set[ns++] = g_rpLive[i], k.rpRebaseWhy[why]++;
        }
        for (int i = 0; i < g_rpNMir && !inPlace; i++) {
            int j = 0;
            while (j < n && g_rpLive[j].key != g_rpMir[i].key) j++;
            if (j == n) gone[ng++] = g_rpMir[i].key, k.rpRebaseWhy[3]++;
        }
        if (heldSkip) {  // the held lot's force stays for the cut after the release
            while (InterlockedExchange(&g_rpRingLock, 1)) YieldProcessor();
            int j = 0;
            while (j < g_rpNForce && g_rpForce[j] != held) j++;
            if (j == g_rpNForce) { if (g_rpNForce < 64) g_rpForce[g_rpNForce++] = held; else g_rpForceAll = 1; }
            InterlockedExchange(&g_rpRingLock, 0);
        }
        RcMark(RC_SIG);
        // 0.35.12 (docs/94 §22.15, I-94-459): one LOTS batch in flight. Each batch carries the whole live map order (BP: 126k
        // nodes, 2.5 MB) and costs the consumer a whole-map resync under +0x918; with the consumer behind (BP 0.35.9: lag 7131
        // records, 179 batches) the queued batches held the replay memory to ReplayCapMB. While the last batch is not applied
        // this cut's lots wait (forces and unknown keys put back, signature changes found again) and go in one later batch;
        // the cut publishes the records but no SYNC / AUDIT, and P4 refuses it (g_rpCutDeferred). Only with the consumer
        // thread: without it (tests) the callers drain when they choose
        // 0.35.23 (docs/94 §22.26.3): a shells-only rebase (W4 / signature lots, Q list unchanged) neither waits for the batch
        // in flight nor needs the pool set: it carries no map order (the consumer does no map work for it) and RpLotShell(qs
        // = false) copies no Q or tree (RpAlloc and EA blocks only). BP 0444: 173 deferred cuts, each a P4 "noclone"
        const bool mapNeed = ns || ng || nUnk;
        const bool need = mapNeed || nsh;
        const bool inFlight = mapNeed && g_rpThreaded && g_rpLotsPend && (LONG)(g_rpLotsAt - g_rpTail) > 0;
        LONG op = 0;
        const bool poolsBusy = mapNeed && !inFlight && !RpOpTry(&op);  // a P4 run owns the private pools: the rebase waits too
        if (inFlight || poolsBusy) {
            while (InterlockedExchange(&g_rpRingLock, 1)) YieldProcessor();
            if (all) g_rpForceAll = 1;
            for (int j = 0; j < nForce; j++) {
                int i = 0;
                while (i < g_rpNForce && g_rpForce[i] != force[j]) i++;
                if (i == g_rpNForce) { if (g_rpNForce < 64) g_rpForce[g_rpNForce++] = force[j]; else g_rpForceAll = 1; }
            }
            for (int j = 0; j < nShellF; j++) {
                int i = 0;
                while (i < g_rpNShellF && g_rpShellF[i] != shellF[j]) i++;
                if (i == g_rpNShellF) { if (g_rpNShellF < 64) g_rpShellF[g_rpNShellF++] = shellF[j]; else g_rpForceAll = 1; }
            }
            for (int j = 0; j < nUnk; j++) RpUnkAdd(unk[j]);
            InterlockedExchange(&g_rpRingLock, 0);
            memcpy(k.rpRebaseWhy, why0, sizeof(why0));
            k.rpRebaseQSame = qsame0;
            RpTabFree(exLive);
            if (sigNow) g_rpSigAt = 0, g_rpSigSkipped = sigLate;  // the signature changes found here are found again by the next cut's scan
            g_rpCutDeferred = true;
            if (poolsBusy) {
                if (++g_rpCutDeferN <= 8 || (g_rpCutDeferN & 1023) == 0)
                    Log("snap replay: rebase of %d lot(s) waits, a P4 run owns the private pools (%ld cuts so far)", ns + ng + nsh, g_rpCutDeferN);
            } else if (++g_rpLotsWait <= 8 || (g_rpLotsWait & 1023) == 0)
                Log("snap replay: rebase of %d lot(s) waits for the batch in flight (%ld cuts so far; consumer lag %lu records)", ns + ng + nsh,
                    g_rpLotsWait, g_rpHead - g_rpTail);
            RcMark(RC_DEFER);
            goto publish;
        }
        if (need) {
            LARGE_INTEGER t0;
            QueryPerformanceCounter(&t0);
            const LONG64 cy0 = CycNow();
            RpMirrorSet(g_rpLive, n, set, ns, shl, nsh, !sigNow);  // first: the batch's map order names Qs of the lots as they are now
            // op: private pools, taken above; the push too (a full ring frees the batch here)
            RpBatch* b = RpRebase(world, set, ns, gone, ng, unk, nUnk, exLive, shl, nsh);
            const LONG us = UsSince(t0);
            const LONG64 cy = CycNow() - cy0;
            g_cyRebase += cy, g_rcCyReb += cy;
            k.rpRebaseUs += us;
            Max(&k.rpRebaseMaxUs, us);
            RpRebaseSplit(ns + ng, nsh, ns || ng || nUnk, us);
            if (b && (b->lotKeys = (u64*)RpAlloc(n * 8 + 8)) != NULL) {
                for (int i = 0; i < n; i++) b->lotKeys[i] = g_rpLive[i].key;
                b->nLotKeys = n;
            }
            if (b) {  // the in-flight rule follows map batches only (0.35.23): a shells batch never makes a later map batch wait
                RpPush(RP_LOTS, 0, 0, 0, b);
                if (mapNeed) g_rpLotsAt = g_rpHead, g_rpLotsPend = true;
                for (int i = 0; g_rpNAudF && i < ns + nsh; i++) {  // 0.36.55 (docs/94 §24.13.5): audit-forced lots in this batch
                    const RpLiveLot& r = i < ns ? set[i] : shl[i - ns];
                    while (InterlockedExchange(&g_rpRingLock, 1)) YieldProcessor();
                    const int f = RpAudFFind(r.key);
                    const bool hit = f >= 0 && g_rpAudFSt[f] == 1;
                    if (hit) g_rpAudFSt[f] = 2, g_rpAudFReb++;
                    InterlockedExchange(&g_rpRingLock, 0);
                    if (!hit || InterlockedIncrement(&g_rpAudFLog) > 128) continue;
                    DWORD ents = 0;
                    RpLotHash(r.node, &ents);
                    Log("snap replay audit: lot 0x%08lX%08lX rebased at cut %ld (ring %lu, %s), entries live %lu (the clone copies them)",
                        (DWORD)(r.key >> 32), (DWORD)r.key, k.spCuts, g_rpHead, i < ns ? "full" : "shell", ents);
                }
            }
            if (mapNeed) RpOpLeave(op);  // taken only for map work (a shells-only cut never took the set)
            if (!b) { RpTabFree(exLive); RC_REBUILD(RC_REBASE) }
            k.rpRebaseLots += ns + ng + nsh;
            if (sigLate) g_rcSigLate += nSigLots;
            RcMark(RC_REBASE);
        }
        const u8* M = RpP(P + 0x24);
        DWORD exNodes = 0;  // map entries of the left-out lots (the clone does not hold them)
        if (exLive.used) {
            __try {
                const DWORD mb = RpD(M + 8);
                const DWORD* lb = (const DWORD*)RpP(M + 4);
                for (DWORD i = 0; mb > 1 && i < mb; i++)
                    for (const u8* nd = (const u8*)(uintptr_t)lb[i]; nd; nd = RpP(nd + 0x10))
                        if (RpP(nd + 8) && RpTabFind(exLive, RpD(RpP(nd + 8) + 0xC))) exNodes++;
            } __except (EXCEPTION_EXECUTE_HANDLER) { RP_NEED_EPOCH(); }
        }
        RcMark(RC_EXN, exLive.used != 0);
        RpTabFree(exLive);
        RpSyncRec* sy = (RpSyncRec*)RpAlloc(sizeof(RpSyncRec));
        if (sy) { sy->mapN = RpD(M + 8), sy->mapCount = RpD(M + 0xC) - exNodes, sy->lots = (DWORD)(n - nExLive); RpPush(RP_SYNC, 0, 0, 0, sy); }
        g_rpQOk = true, g_rpQFullAt = GetTickCount64(), g_rpQLots = (DWORD)(n - nExLive), g_rpQExN = exNodes;
        if (g_rpQSkipped) g_rcLateScans++, g_rcLateLots += k.rpRebaseWhy[2] - why0[2], g_rpQSkipped = 0;
        RcMark(RC_SYNC);
        if (g_rpAuditEvery && (k.spCuts + 1) % (LONG)g_rpAuditEvery == 0 && n > 0) {
            LARGE_INTEGER t0;
            QueryPerformanceCounter(&t0);
            const LONG64 cy0 = CycNow();
            RpAuditRec* a = (RpAuditRec*)RpAlloc(sizeof(RpAuditRec));
            if (a && (a->keys = (u64*)RpAlloc(n * 8)) && (a->hash = (u64*)RpAlloc(n * 8)) && (a->ents = (DWORD*)RpAlloc(n * 4))) {
                a->n = 0, a->nAll = n - nExLive;
                const DWORD from = g_rpAuditAt;
                if (g_rpAuditLots) g_rpAuditAt = (g_rpAuditAt + g_rpAuditLots) % (DWORD)n;
                for (int j = 0; j < n; j++) {
                    const int i = (int)((from + (DWORD)j) % (DWORD)n);
                    if (g_rpAuditLots && j >= (int)g_rpAuditLots) break;
                    if (g_rpNEx && RpIsEx(g_rpLive[i].key)) continue;
                    a->keys[a->n] = g_rpLive[i].key;
                    if (!a->perQ) a->perQ = (DWORD*)RpAlloc(n * 16 * 4);
                    a->hash[a->n] = RpLotHash(g_rpLive[i].node, &a->ents[a->n], a->perQ ? a->perQ + a->n * 16 : NULL);
                    a->n++;
                }
                a->mapN = RpD(M + 8), a->mapCount = RpD(M + 0xC) - exNodes;
                a->seq = g_rpHead, a->cut = k.spCuts;  // the slot RpPush takes; records before it are what the clone saw
                RpPush(RP_AUDIT, 0, 0, 0, a);
            } else RpAuditFree(a);
            const LONG us = UsSince(t0);
            g_cyRpAudit += CycNow() - cy0;
            k.rpAuditUs += us;
            Max(&k.rpAuditMaxUs, us);
            RcMark(RC_AUDIT);
        }
    }
#undef RC_REBUILD
publish:
    RcMark(RC_SYNC, false);  // the publish itself
    {
        const DWORD depth = g_rpHead - g_rpTail;
        Max(&k.rpRingMax, (LONG)depth);
    }
    g_rpPub = g_rpHead;
    if (g_rpThreaded) SetEvent(g_rpEvent);
}

// --- consumer (one thread; the clone and its footprint table are its own) ---
static RpClone* g_rpClone;
static RpLot* RpFindQ(RpClone* c, DWORD liveQ, u8** q) {
    for (int i = 0; i < c->nLots; i++) {
        RpLot& l = c->lots[i];
        for (int j = 0; j < l.nQ; j++) if (l.liveQ[j] == liveQ && l.q[j]) { *q = l.q[j]; return &l; }
    }
    return NULL;
}
static RpLot* RpFindLot(RpClone* c, u64 key) {
    for (int i = 0; i < c->nLots; i++) if (c->lots[i].key == key) return &c->lots[i];
    return NULL;
}
// 0.36.35 (docs/182 §8, live p5c-site-0340): ApplyInsert 0x665C50 (call at 0x665D1E) and ApplyRemove 0x666750 (at 0x666796)
// ask LotTable_GetIfPresent 0x62FB60 (thiscall P, lot key lo / hi: LotTable_Contains 0x628A90, then GetOrInsert 0x62EAB0 on
// the hit) for the Q's lot node (Q+0x20 = the planner P, Q+0x18 / +0x1C = the key) and skip the whole record on 0; the node
// is read at +0xA0 (insert, footprint slot +0x68) and +0xE0 (remove, slot +0x6C) [verified disassembly]. On a clone Q that
// P is the LIVE planner: gate sites 12 and 0 (RgEnter), the table's window-slot write on every hit and, inside a
// RecsUnlocked round, a +0x918 wait when a job runs (ulLockTrip 4 per run, copy mismatches 7 / 30). Both call sites go to
// RpGetIfPresent: outside a round the game's call on the caller's P, unchanged; inside one the same game body on the
// clone's P copy (c->p: its own bucket array c->lotBk of the clone's lot node copies, RpTableLink / RpTableUnlink, the
// table the P4 clone runs already plan on), so the answer is the clone's node and nothing live is read or written (the
// active-lot id pair Contains reads through 0xC88FD0 excepted: the sim writes it at a lot switch only; a torn read skips
// the record and forces the lot). A miss on the clone table (the lot left since the cut) forces the lot and lets the
// game's body skip the record, as the locked path's no-copy branch does. RgEnter passes sites 0 / 12 through under t_rpUl.
typedef u8*(__fastcall* RpGetIfPresentFn)(const u8* p, void*, DWORD lo, DWORD hi);  // 0x62FB60, ret 8
static RpGetIfPresentFn g_rpGetIfPresent = (RpGetIfPresentFn)(uintptr_t)0x0062FB60;
static LONG g_rpUlLotMiss;  // misses on the clone table inside a round (log + force; no counter slot reserved)
static u8* __fastcall RpGetIfPresent(const u8* p, void* edx, DWORD lo, DWORD hi) {
    RpClone* c = t_rpUl ? g_rpClone : NULL;
    if (!c) return g_rpGetIfPresent(p, edx, lo, hi);
    u8* r = g_rpGetIfPresent(c->p, edx, lo, hi);
    if (!r) {
        if (InterlockedIncrement(&g_rpUlLotMiss) <= 8)
            Log("snap replay: lot 0x%08lX%08lX not in the clone's lot table inside a RecsUnlocked round: record skipped, lot forced", hi, lo);
        RpForceLot(((u64)hi << 32) | lo);
    }
    return r;
}
static const CallSite kRpGetIfPresentSites[2] = {{0x00665D1E, 0x0062FB60, (void*)RpGetIfPresent}, {0x00666796, 0x0062FB60, (void*)RpGetIfPresent}};
// 0.35.21 (docs/94 §22.24.2): the lots of a clone footprint, for a W6b (Footprint_EnsureAllPolygons, ecx = the footprint) in a
// clone run, which made every release rebase all lots (BP 0303: 2 force-all batches, ~180 ms each). The clone map's nodes with
// the footprint's key whose FME holds it name its Qs (lot id Q+0x18/+0x1C). Worker, g_rpUse held (the clone is stable).
// 0 = not found (an FME without a Q, too many lots): the caller keeps the all-lot rebase.
// 0.35.23 (docs/94 §22.26.2): -1 = fp is a live footprint (refcount +8 far below RP_BIAS: every clone copy, children
// included, starts at RP_BIAS, RpFpCopyInto / RpFpCopyChild), so the write changed no clone memory and no lot needs a
// rebase (BP 0444: the one W6b "no lot" made an 82-lot batch, a 181 ms consumer hold and most of the 450 noclone refusals).
// A clone copy the map does not hold under that pointer (an older copy) names the lots of every FME with its key
static int RpCloneFpLots(const u8* fp, u64* out, int cap) {
    const RpClone* c = g_rpClone;
    int n = 0;
    __try {
        u64 key;
        if (fp && RpD(fp + 8) < (DWORD)(RP_BIAS / 2)) return -1;
        if (!c || !c->m || !fp || !RpFpKey(fp, &key)) return 0;
        const DWORD nb = RpD(c->m + 8);
        const DWORD* bk = (const DWORD*)RpP(c->m + 4);
        if (nb <= 1 || !bk) return 0;
        for (int pass = 0; pass < 2 && !n; pass++)  // pass 1: same key, any copy
            for (const u8* nd = (const u8*)(uintptr_t)bk[(DWORD)key % nb]; nd; nd = RpP(nd + 0x10)) {
                const u8* f = RpP(nd + 8);
                if (RpD(nd) != (DWORD)key || RpD(nd + 4) != (DWORD)(key >> 32) || !f || (!pass && RpP(f + 8) != fp)) continue;
                const u8* q = RpP(f + 0xC);
                if (!q) return 0;
                const u64 lot = ((u64)RpD(q + 0x1C) << 32) | RpD(q + 0x18);
                int i = 0;
                while (i < n && out[i] != lot) i++;
                if (i == n) { if (n == cap) return 0; out[n++] = lot; }
            }
    } __except (EXCEPTION_EXECUTE_HANDLER) { return 0; }
    return n;
}
// the clone footprint for a live one: the one FMEs still hold, else the record's copy (registered)
static void RpRepin(RpClone* c, u8* old, u8* fresh);
static void RpRevOld(RpTab& rev, const u8* blk, const u8* live);
// live -> its copy, for blk and its children (list node +12 names the live child)
static void RpWant(RpTab& want, u8* blk, DWORD live) {
    RpTabPut(want, live, (DWORD)(uintptr_t)blk);
    if (!RpHasList(blk)) return;
    for (u8* l = *(u8**)(blk + 0x24); l != blk + 0x24; l = *(u8**)l) RpWant(want, *(u8**)(l + 8), *(DWORD*)(l + 12));
}
// replaced blocks (c->old) and the live footprints they copy (c->oldLive); freed once no FME holds them
static void RpOldLiveAdd(RpTab& t, const u8* blk, DWORD live) {
    RpTabPut(t, live, 1);
    if (!RpHasList(blk)) return;
    for (const u8* l = RpP(blk + 0x24); l != blk + 0x24; l = RpP(l)) RpOldLiveAdd(t, RpP(l + 8), RpD(l + 12));
}
static void RpOldGc(RpClone* c) {
    if (!c->old.used) return;
    bool freed = false;
    for (DWORD i = 0; i < c->old.cap; i++)
        if (c->old.k[i] > 1 && RpFpRefs((u8*)(uintptr_t)c->old.k[i]) <= 0) {
            RpFpFree((u8*)(uintptr_t)c->old.k[i]);
            c->old.k[i] = 1;
            freed = true;
            ts3native_counters.rpGcFreed++;
        }
    if (!freed) return;
    RpTabFree(c->oldLive);
    for (DWORD i = 0; i < c->old.cap; i++)
        if (c->old.k[i] > 1) RpOldLiveAdd(c->oldLive, (const u8*)(uintptr_t)c->old.k[i], (DWORD)(uintptr_t)((const RpFpHdr*)(uintptr_t)(c->old.k[i] - 16))->live);
}
// old leaves the tables (its own-block children move to fresh) and waits in c->old until no FME holds it. repin false: the
// caller re-pointed the own-block children already in one pass (RpApplyBatch). 0.34.1 (docs/94 §21.16): RpRepin walks every
// clone footprint, so a batch with F replaced footprints walked F x ~130k slots on the consumer, holding +0x918: the 8-28 s
// holds of the 0.33.1 / 0.33.3 runs (replay phase 0x402 = an RP_LOTS apply) after soft resyncs forced whole lots
static volatile LONG g_rpRepinScans;  // tests
static void RpRetire(RpClone* c, u8* old, u8* fresh, bool repin = true) {
    if (repin) RpRepin(c, old, fresh);
    RpKids(c->kids, old, false);
    RpTabPut(c->old, (DWORD)(uintptr_t)old, 1);
    RpOldLiveAdd(c->oldLive, old, (DWORD)(uintptr_t)((const RpFpHdr*)(old - 16))->live);
}
// every clone FME on another copy of a live footprint in want moves to want's copy: one clone object per live footprint,
// as the game has one (0.32.4; 0.32.1 kept the old block for FMEs of other lots, so a later insert with a changed child list,
// or a child seen again on its own, gave the clone two copies whose insert counters then differed)
// 0.35.12 (I-94-459): rev holds only the copies of want's live footprints (it held every clone copy: ~130k puts per call
// on BP, under +0x918); the map walk looks up only those
static void RpRevOldIf(RpTab& rev, const RpTab& want, const u8* blk, const u8* live) {
    if (RpTabFind(want, (DWORD)(uintptr_t)live)) RpTabPut(rev, (DWORD)(uintptr_t)blk, (DWORD)(uintptr_t)live);
    if (!RpHasList(blk)) return;
    for (const u8* l = RpP(blk + 0x24); l != blk + 0x24; l = RpP(l)) RpRevOldIf(rev, want, RpP(l + 8), RpP(l + 12));
}
// 0.35.14 (docs/94 §22.17, I-94-473): fresh != NULL (one insert's copy) walks only the two map buckets its FMEs can be in:
// the map is keyed by footprint vtable +0x7C, which is +0x14/+0x18 for all 10 parent classes and ~parent's for the child
// class 0xFEBF90 (0x6262B0) [verified on the Steam 1.67 vtables], bucket = key lo % bucket count (0x6643E0) [verified]. Each
// such call walked the whole map (~122k nodes) under +0x918: IP 0.35.13 spent 18-41 s per 5 min on INS records, ~2-3 ms each
static void RpUnifyNode(u8* nd, const RpTab& rev, const RpTab& want) {
    u8* f = RpP(nd + 8);
    u8* x = f ? RpP(f + 8) : NULL;
    const DWORD* lv = x ? RpTabFind(rev, (DWORD)(uintptr_t)x) : NULL;
    const DWORD* to = lv ? RpTabFind(want, *lv) : NULL;
    if (!to || (u8*)(uintptr_t)*to == x) return;
    (*(LONG*)(x + 8))--, (*(LONG*)((u8*)(uintptr_t)*to + 8))++;
    *(u8**)(f + 8) = (u8*)(uintptr_t)*to;
}
static void RpUnify(RpClone* c, const RpTab& want, const u8* fresh = NULL) {
    RpTab rev = {};
    for (DWORD i = 0; i < c->fps.cap; i++) if (c->fps.k[i] > 1 && RpTabFind(want, c->fps.k[i])) RpTabPut(rev, c->fps.v[i], c->fps.k[i]);
    for (DWORD i = 0; i < c->kids.cap; i++) if (c->kids.k[i] > 1 && RpTabFind(want, c->kids.k[i])) RpTabPut(rev, c->kids.v[i], c->kids.k[i]);
    for (DWORD i = 0; i < c->old.cap; i++)
        if (c->old.k[i] > 1) RpRevOldIf(rev, want, (const u8*)(uintptr_t)c->old.k[i], ((const RpFpHdr*)(uintptr_t)(c->old.k[i] - 16))->live);
    if (!rev.used) return;
    u8* m = c->m;
    DWORD* bk = (DWORD*)RpP(m + 4);
    const DWORD nb = RpD(m + 8);
    const RpCls* fc = fresh ? RpClass(RpD(fresh)) : NULL;
    const u8* kf = fc && fc->child ? RpP(fresh + 0xC) : fresh;  // the child class keys by ~parent's id
    if (fc && kf && nb > 1) {
        const DWORD lo = RpD(kf + 0x14), b0 = lo % nb, b1 = ~lo % nb;
        for (u8* nd = (u8*)(uintptr_t)bk[b0]; nd; nd = RpP(nd + 0x10)) RpUnifyNode(nd, rev, want);
        if (b1 != b0) for (u8* nd = (u8*)(uintptr_t)bk[b1]; nd; nd = RpP(nd + 0x10)) RpUnifyNode(nd, rev, want);
    } else
        for (DWORD i = 0; nb > 1 && i < nb; i++)
            for (u8* nd = (u8*)(uintptr_t)bk[i]; nd; nd = RpP(nd + 0x10)) RpUnifyNode(nd, rev, want);
    RpTabFree(rev);
}
// old takes fresh's state in place (same class, same live children, same level counts): every field but its reference
// count, child list head, level array pointer and parent back pointer; the level entries' polygon vectors are swapped, so
// freeing fresh frees the old arrays. FMEs keep pointing at old. false = shapes differ (nothing changed).
// 0.32.4 reused the old copy as it was: BP lot audits found fewer tree entries than live (the old bounds / counters), since
// 0.32.1 no longer refreshed copies through its own records (0.32.8)
static bool RpFpRefreshOk(const u8* o, const u8* f) {
    if (RpD(o) != RpD(f)) return false;
    const RpCls* c = RpClass(RpD(o));
    if (!c) return false;
    if (c->cache && (RpLevels(o) != RpLevels(f) || !RpP(o + 0x38) || !RpP(f + 0x38))) return false;
    if (!RpHasList(o)) return true;
    const u8* lo = RpP(o + 0x24);
    const u8* lf = RpP(f + 0x24);
    for (; lo != o + 0x24 && lf != f + 0x24; lo = RpP(lo), lf = RpP(lf))
        if (RpD(lo + 12) != RpD(lf + 12) || !RpFpRefreshOk(RpP(lo + 8), RpP(lf + 8))) return false;
    return lo == o + 0x24 && lf == f + 0x24;
}
static void RpFpRefreshIn(u8* o, u8* f) {
    const RpCls* c = RpClass(*(DWORD*)o);
    const LONG refs = *(LONG*)(o + 8);
    const DWORD par = *(DWORD*)(o + 0xC);
    u8 head[8];
    const bool list = RpHasList(o);
    if (list) memcpy(head, o + 0x24, 8);
    u8* lc = c->cache ? *(u8**)(o + 0x38) : NULL;
    memcpy(o, f, c->size);
    *(LONG*)(o + 8) = refs;
    if (c->child) *(DWORD*)(o + 0xC) = par;
    if (list) memcpy(o + 0x24, head, 8);
    if (c->cache) {
        *(u8**)(o + 0x38) = lc;
        u8* fc = *(u8**)(f + 0x38);
        const DWORD lv = RpLevels(o);
        for (DWORD l = 0; l < lv; l++) {
            u8 t[0x24];
            memcpy(t, lc + l * 0x24, 0x24), memcpy(lc + l * 0x24, fc + l * 0x24, 0x24), memcpy(fc + l * 0x24, t, 0x24);
        }
    }
    if (!list) return;
    for (u8 *lo = *(u8**)(o + 0x24), *lf = *(u8**)(f + 0x24); lo != o + 0x24; lo = *(u8**)lo, lf = *(u8**)lf) RpFpRefreshIn(*(u8**)(lo + 8), *(u8**)(lf + 8));
}
static LONG g_rpInsFreeInPlace;              // [RouteSnap] InsFreeInPlace (0.35.25, default 0)
static volatile LONG g_rpInsFreed, g_rpInsRetired;  // consumer / tests: old copies freed in place vs retired (log)
static u8* RpFpUse(RpClone* c, u8* live, u8* fresh) {
    DWORD* v = RpTabFind(c->fps, (DWORD)(uintptr_t)live);
    u8* old = v ? (u8*)(uintptr_t)*v : NULL;
    // other FMEs still hold the old copy: it takes the record's state in place (0.32.8), unless the live children changed
    // (0.32.4: BP lot audits found the replayed insert putting the old children in): then fresh replaces it below
    if (old && RpFpRefs(old) > 0 && (!fresh || RpFpRefreshOk(old, fresh))) {
        if (fresh) RpFpRefreshIn(old, fresh);
        RpFpFree(fresh);
        return old;
    }
    if (!fresh) return NULL;
    bool unify = old && RpFpRefs(old) > 0;
    // 0.35.25 (docs/94 §22.29, I-94-501): [RouteSnap] InsFreeInPlace=1 frees an old copy that nothing holds (no FME, no child
    // pin: a moved footprint's REM then INS) here, as RpOldGc below would. Retiring it ran RpRepin (every clone footprint's
    // header) and, through its own oldLive entry, RpUnify's table walks, for nothing: ~1.1-1.6 ms per INS under +0x918
    if (old && !unify && g_rpInsFreeInPlace) {
        RpKids(c->kids, old, false);
        RpFpFree(old);
        ts3native_counters.rpGcFreed++;
        g_rpInsFreed++;
        old = NULL;
    }
    if (old) RpRetire(c, old, fresh), g_rpInsRetired++;
    if (!RpTabPut(c->fps, (DWORD)(uintptr_t)live, (DWORD)(uintptr_t)fresh)) { RpFpFree(fresh); return NULL; }
    RpKids(c->kids, fresh, true);
    RpTab want = {};
    RpWant(want, fresh, (DWORD)(uintptr_t)live);
    for (DWORD i = 0; !unify && c->oldLive.used && i < want.cap; i++) unify = want.k[i] > 1 && RpTabFind(c->oldLive, want.k[i]);
    if (unify) RpUnify(c, want, fresh);
    RpTabFree(want);
    RpOldGc(c);
    return fresh;
}
// own-block children (RpFpCopyChild) whose parent is old: re-pointed to fresh, the pin moved with them
static void RpRepin(RpClone* c, u8* old, u8* fresh) {
    g_rpRepinScans++;
    for (DWORD i = 0; i < c->fps.cap; i++) {
        u8* ch = c->fps.k[i] > 1 ? (u8*)(uintptr_t)c->fps.v[i] : NULL;
        if (!ch || ((RpFpHdr*)(ch - 16))->pad != 1 || *(u8**)(ch + 0xC) != old) continue;
        (*(LONG*)(old + 8))--, (*(LONG*)(fresh + 8))++;
        *(u8**)(ch + 0xC) = fresh;
    }
}
static void RpGc(RpClone* c) {
    for (DWORD i = 0; i < c->fps.cap; i++)
        if (c->fps.k[i] > 1 && RpFpRefs((u8*)(uintptr_t)c->fps.v[i]) <= 0) {
            RpKids(c->kids, (u8*)(uintptr_t)c->fps.v[i], false);
            RpFpFree((u8*)(uintptr_t)c->fps.v[i]);
            c->fps.k[i] = 1;
            ts3native_counters.rpGcFreed++;
        }
    RpOldGc(c);
}
// re-chain the clone's map in the live order of the batch (bucket count, chains, policy); false = a node had no partner
struct RpOrd { DWORD t[5]; u8* node; int next; bool used; };
// a replaced block and its children in the clone -> live table (their FMEs still name the live objects)
static void RpRevOld(RpTab& rev, const u8* blk, const u8* live) {
    RpTabPut(rev, (DWORD)(uintptr_t)blk, (DWORD)(uintptr_t)live);
    if (!RpHasList(blk)) return;
    for (const u8* l = RpP(blk + 0x24); l != blk + 0x24; l = RpP(l)) RpRevOld(rev, RpP(l + 8), RpP(l + 12));
}
static LONG g_rpResyncLog;  // first 8 resync failures logged (0.32.1)
// soft / nSoft (0.33.1): the clone lots of the unmatched live nodes (up to 16); *nSoft -1 = a miss no forced lot can fix
static bool RpMapResync(RpClone* c, const RpBatch* b, u64* soft = NULL, int* nSoft = NULL) {
    u8* m = c->m;
    const DWORD nb = RpD(m + 8), cnt = RpD(m + 0xC);
    const bool same = cnt == b->nOrder;  // else matched dry, to name the first node without a partner (0.32.1)
    if (b->mapN <= 1 || (!same && g_rpResyncLog >= 8)) {
        if (!(same && cnt == 0) && InterlockedIncrement(&g_rpResyncLog) <= 8)
            Log("snap replay: map resync failed: clone %lu entries, live %lu at the cut (buckets %lu)", cnt, b->nOrder, b->mapN);
        return same && cnt == 0;
    }
    RpTab rev = {}, lq = {}, heads = {};
    RpTabGrow(rev, c->fps.used + c->kids.used + c->old.used * 2), RpTabGrow(heads, cnt);  // sized once (0.35.12): no rehash chain under +0x918
    for (DWORD i = 0; i < c->fps.cap; i++) if (c->fps.k[i] > 1) RpTabPut(rev, c->fps.v[i], c->fps.k[i]);
    for (DWORD i = 0; i < c->kids.cap; i++) if (c->kids.k[i] > 1) RpTabPut(rev, c->kids.v[i], c->kids.k[i]);
    for (DWORD i = 0; i < c->old.cap; i++) if (c->old.k[i] > 1) RpRevOld(rev, (const u8*)(uintptr_t)c->old.k[i], ((const RpFpHdr*)(uintptr_t)(c->old.k[i] - 16))->live);
    for (int i = 0; i < c->nLots; i++) for (int j = 0; j < c->lots[i].nQ; j++) if (c->lots[i].q[j]) RpTabPut(lq, (DWORD)(uintptr_t)c->lots[i].q[j], c->lots[i].liveQ[j]);
    RpOrd* o = (RpOrd*)RpAlloc((cnt + 1) * sizeof(RpOrd));
    bool ok = o != NULL;
    DWORD n = 0;
    DWORD* bk = (DWORD*)RpP(m + 4);
    for (DWORD i = 0; ok && i < nb; i++)
        for (u8* nd = (u8*)(uintptr_t)bk[i]; nd; nd = RpP(nd + 0x10)) {
            if (n >= cnt) { ok = false; break; }
            const u8* f = RpP(nd + 8);
            const DWORD* q = f ? RpTabFind(lq, RpD(f + 0xC)) : NULL;
            const DWORD* lf = f ? RpTabFind(rev, RpD(f + 8)) : NULL;
            RpOrd& r = o[n];
            r.t[0] = RpD(nd), r.t[1] = RpD(nd + 4), r.t[2] = q ? *q : 0, r.t[3] = f ? RpD(f + 0x10) : 0, r.t[4] = lf ? *lf : 0;
            r.node = nd, r.used = false;
            const DWORD h = (r.t[0] * 31 + r.t[2]) * 31 + r.t[3] * 7 + r.t[4] | 2;
            DWORD* hd = RpTabFind(heads, h);
            r.next = hd ? (int)*hd : -1;
            if (!RpTabPut(heads, h, n)) ok = false;
            n++;
        }
    ok = ok && n == cnt;
    DWORD* nbk = ok && same ? (DWORD*)g_rpMapAlloc(m + 0x1C, NULL, b->mapN * 4 + 4, 0x400000) : NULL;
    if (ok && same && !nbk) ok = false;
    if (nSoft) *nSoft = same ? 0 : -1;
    bool miss = false;
    if (ok) {
        if (nbk) memset(nbk, 0, b->mapN * 4), nbk[b->mapN] = 0xFFFFFFFF;
        DWORD tailBucket = 0xFFFFFFFF;
        u8* tail = NULL;
        for (DWORD i = 0; ok && i < b->nOrder; i++) {
            const DWORD* t = b->order + i * 5;
            const DWORD h = (t[0] * 31 + t[2]) * 31 + t[3] * 7 + t[4] | 2;
            const DWORD* hd = RpTabFind(heads, h);
            int k = hd ? (int)*hd : -1;
            while (k >= 0 && (o[k].used || memcmp(o[k].t, t, 20))) k = o[k].next;
            if (k < 0) {
                ok = false;
                if (InterlockedIncrement(&g_rpResyncLog) <= 8) {
                    DWORD vt = 0;  // the live footprint (read under the consumer's +0x918 hold)
                    __try { vt = t[4] ? RpD((const u8*)(uintptr_t)t[4]) : 0; } __except (EXCEPTION_EXECUTE_HANDLER) {}
                    const RpCls* lc = RpClass(vt);
                    const DWORD* cf = RpTabFind(c->fps, t[4]);
                    const DWORD* ck = RpTabFind(c->kids, t[4]);
                    Log("snap replay: map resync failed (clone %lu entries): live node %lu of %lu has no clone partner (key 0x%08lX%08lX, Q 0x%08lX, +0x10 0x%08lX, footprint 0x%08lX class 0x%08lX%s; clone copy: %s%s%s)",
                        cnt, i, b->nOrder, t[1], t[0], t[2], t[3], t[4], vt, lc && lc->child ? " child" : "",
                        cf ? (((const RpFpHdr*)(uintptr_t)(*cf - 16))->pad == 1 ? "own block" : "top level") : "", ck ? " in a parent's block" : "",
                        RpTabFind(c->oldLive, t[4]) ? " retired" : (!cf && !ck ? "none" : ""));
                }
                miss = true;
                u64 lot = 0;  // the clone lot that holds the node's live Q (0: none)
                for (int li = 0; t[2] && !lot && li < c->nLots; li++)
                    for (int j = 0; j < c->lots[li].nQ; j++) if (c->lots[li].liveQ[j] == t[2]) { lot = c->lots[li].key; break; }
                if (nSoft && *nSoft >= 0) {
                    int s = 0;
                    while (s < *nSoft && soft[s] != lot) s++;
                    if (!lot || (s == *nSoft && *nSoft == 16)) *nSoft = lot ? -1 : -2;  // -2: no clone lot holds the Q (rpHard[0])
                    else if (s == *nSoft) soft[(*nSoft)++] = lot;
                }
                if (!nSoft || *nSoft < 0) break;
                ok = true;  // collect the other misses' lots; the old chains are put back below
                continue;
            }
            o[k].used = true;
            if (!same) continue;
            u8* nd = o[k].node;
            const DWORD bi = t[0] % b->mapN;
            *(DWORD*)(nd + 0x10) = 0;
            if (bi == tailBucket) *(DWORD*)(tail + 0x10) = (DWORD)(uintptr_t)nd; else nbk[bi] = (DWORD)(uintptr_t)nd;
            tailBucket = bi, tail = nd;
        }
        if (miss) ok = false;
        if (ok && !same) {  // every live node matched: the clone has extra ones
            ok = false;
            for (DWORD j = 0; j < n; j++)
                if (!o[j].used) {
                    if (InterlockedIncrement(&g_rpResyncLog) <= 8)
                        Log("snap replay: map resync failed: clone %lu entries, live %lu; clone node without a live partner (key 0x%08lX%08lX, Q 0x%08lX, +0x10 0x%08lX, footprint 0x%08lX)",
                            cnt, b->nOrder, o[j].t[1], o[j].t[0], o[j].t[2], o[j].t[3], o[j].t[4]);
                    break;
                }
        } else if (ok) {
            if (nb > 1) g_rpMapFree(m + 0x1C, NULL, bk, nb * 4 + 4);
            *(DWORD**)(m + 4) = nbk, *(DWORD*)(m + 8) = b->mapN;
            memcpy(m + 0x10, b->policy, 12);
        } else if (same) {  // put the old chains back: they were not touched except +0x10 of nodes already moved, so rebuild them from o
            memset(bk, 0, nb * 4);
            for (DWORD i = n; i-- > 0;) { u8* nd = o[i].node; const DWORD bi = RpD(nd) % nb; *(DWORD*)(nd + 0x10) = bk[bi]; bk[bi] = (DWORD)(uintptr_t)nd; }
            g_rpMapFree(m + 0x1C, NULL, nbk, b->mapN * 4 + 4);
        }
    }
    RpFree(o);
    RpTabFree(rev), RpTabFree(lq), RpTabFree(heads);
    return ok;
}
static LONG g_rpShellMiss;  // shells the clone could not take (its lot's Qs differ): forced to a full rebase
static void RpApplyShells(RpClone* c, RpBatch* b) {
    for (int i = 0; i < b->n; i++) {
        RpBatchLot& bl = b->lots[i];
        if (!bl.shell) continue;
        RpLot* old = RpFindLot(c, bl.key);
        bool same = old && old->nQ == bl.lot.nQ;
        for (int j = 0; same && j < old->nQ; j++) same = old->liveQ[j] == bl.lot.liveQ[j] && (!old->liveQ[j] || old->q[j]);
        if (!same) {  // a full rebase at the next cut instead; the shell is freed with the batch
            while (InterlockedExchange(&g_rpRingLock, 1)) YieldProcessor();
            int j = 0;
            while (j < g_rpNForce && g_rpForce[j] != bl.key) j++;
            if (j == g_rpNForce) { if (g_rpNForce < 64) g_rpForce[g_rpNForce++] = bl.key; else g_rpForceAll = 1; }
            InterlockedExchange(&g_rpRingLock, 0);
            if (InterlockedIncrement(&g_rpShellMiss) <= 8)
                Log("snap replay: shell of lot 0x%08lX%08lX not taken (clone lot %s, %d Qs vs %d): full rebase at the next cut",
                    (DWORD)(bl.key >> 32), (DWORD)bl.key, old ? "differs" : "missing", old ? old->nQ : -1, bl.lot.nQ);
            continue;
        }
        u8** qv = *(u8***)(RpGraph(bl.lot.node) + 0x74);  // the shell's own copy of the Q vector (live Qs until now)
        for (int j = 0; j < old->nQ; j++) bl.lot.q[j] = old->q[j], qv[j] = old->q[j];
        old->nQ = 0;  // its Qs and trees stay in the clone: the grave frees only the old shell
        RpTableUnlink(c, old);
        RpGraveLot(old);
        *old = bl.lot;
        RpTableLink(c, old);
        bl.removed = true;  // taken
    }
}
static void RpApplyBatch(RpClone* c, RpBatch* b) {
    RpApplyShells(c, b);
    // every clone node on the rebased lots' old Qs goes; the batch brings the lots' nodes at the cut. 0.32.1 kept nodes on a
    // replaced Q (dummy Q): on BP the game had dropped them, so the clone held 6 more entries than live at each SYNC (0.32.4)
    RpTab qs = {};
    for (int i = 0; i < b->n; i++)
        if (RpLot* old = b->lots[i].shell ? NULL : RpFindLot(c, b->lots[i].key))
            for (int j = 0; j < old->nQ; j++) if (old->q[j]) RpTabPut(qs, (DWORD)(uintptr_t)old->q[j], 1);
    if (qs.used) RpMapDrop(c, qs);
    RpTabFree(qs);
    for (int i = 0; i < b->n; i++) {
        if (RpLot* old = b->lots[i].shell ? NULL : RpFindLot(c, b->lots[i].key)) {
            RpTableUnlink(c, old);
            RpGraveLot(old);  // freed in slices by the next holds (0.35.13)
            *old = c->lots[--c->nLots];
        }
    }
    for (int i = 0; i < b->n; i++) {
        if (b->lots[i].removed || b->lots[i].shell) continue;  // a shell not taken is freed with the batch
        RpLot* l = RpLotAdd(c);
        if (!l) { RP_NEED_EPOCH(); continue; }
        *l = b->lots[i].lot;
        b->lots[i].removed = true;  // taken
        RpTableLink(c, l);
    }
    // lot table chains in the live order (a rebased lot was linked at its bucket head; the live table may hold it later):
    // head inserts in reverse live order. shortcut: a live bucket count change is not followed (lotN stays the build's)
    if (b->lotKeys && c->lotN) {
        memset(c->lotBk, 0, c->lotN * 4);
        for (int i = 0; i < c->nLots; i++) {
            int j = 0;
            while (j < b->nLotKeys && b->lotKeys[j] != c->lots[i].key) j++;
            if (j == b->nLotKeys) RpTableLink(c, &c->lots[i]);
        }
        for (int j = b->nLotKeys - 1; j >= 0; j--) if (RpLot* l = RpFindLot(c, b->lotKeys[j])) RpTableLink(c, l);
    }
    for (int i = 0; i < b->nMap; i++) {
        u8* f = (u8*)(uintptr_t)b->map[i * 3 + 2];
        (*(LONG*)(f + 4))--;  // the batch's hold, the map node takes its own
        if (!RpMapAdd(c, b->map[i * 3], b->map[i * 3 + 1], f)) RP_NEED_EPOCH();
        b->map[i * 3 + 2] = 0;
    }
    b->nMap = 0;
    // children in a block of their own (RpFpCopyChild) whose parent the batch replaces: re-pointed to the fresh parent and the
    // pin moved with them, so the old parent can go below
    for (DWORD i = 0; b->fps.used && i < c->fps.cap; i++) {  // (no copies in the batch: nothing to re-point)
        u8* ch = c->fps.k[i] > 1 ? (u8*)(uintptr_t)c->fps.v[i] : NULL;
        u8* par = ch && ((RpFpHdr*)(ch - 16))->pad == 1 ? *(u8**)(ch + 0xC) : NULL;
        const DWORD* nv = par ? RpTabFind(b->fps, (DWORD)(uintptr_t)((RpFpHdr*)(par - 16))->live) : NULL;
        if (!nv || (u8*)(uintptr_t)*nv == par) continue;
        (*(LONG*)(par + 8))--, (*(LONG*)((u8*)(uintptr_t)*nv + 8))++;
        *(u8**)(ch + 0xC) = (u8*)(uintptr_t)*nv;
    }
    // footprints: the batch's copies (live at the cut) replace the clone's; FMEs of other lots move over to them
    RpTab want = {};
    for (DWORD i = 0; i < b->fps.cap; i++) {
        if (b->fps.k[i] <= 1) continue;
        const DWORD live = b->fps.k[i];
        u8* fresh = (u8*)(uintptr_t)b->fps.v[i];
        b->fps.k[i] = 1;
        DWORD* v = RpTabFind(c->fps, live);
        if (u8* old = v ? (u8*)(uintptr_t)*v : NULL) RpRetire(c, old, fresh, false);  // children re-pointed above
        if (!RpTabPut(c->fps, live, (DWORD)(uintptr_t)fresh)) { RP_NEED_EPOCH(); continue; }
        RpKids(c->kids, fresh, true);
        RpWant(want, fresh, live);
    }
    if (want.used) RpUnify(c, want);  // FMEs of other lots move to the batch's copies (children matched by their live object)
    RpTabFree(want);
    RpOldGc(c);
    u64 soft[16];
    int nSoft = 0;
    Ts3nCounters& k = ts3native_counters;
    // this batch's lots leave the pending soft forces (it carries them as they were at its cut)
    while (InterlockedExchange(&g_rpRingLock, 1)) YieldProcessor();
    for (int i = 0; i < b->n; i++)
        for (int j = 0; j < g_rpNSoftPend; j++)
            if (g_rpSoftPend[j] == b->lots[i].key) { g_rpSoftPend[j--] = g_rpSoftPend[--g_rpNSoftPend]; }
    InterlockedExchange(&g_rpRingLock, 0);
    if (!b->order) {  // a shells-only batch (0.35.17): the map was not touched, nothing to re-chain or compare
    } else if (!RpMapResync(c, b, soft, &nSoft)) {  // the SYNC after it then reports a count difference too
        k.rpDiverge++;
        int real = 0, fresh = 0, flight = 0;
        for (int i = 0; i < nSoft; i++) {
            int j = 0;
            while (j < b->n && b->lots[j].key != soft[i]) j++;
            if (j < b->n) { real++; continue; }
            j = 0;
            while (j < g_rpNSoftPend && g_rpSoftPend[j] != soft[i]) j++;
            if (j < g_rpNSoftPend) flight++; else fresh++;
        }
        const bool tries = real ? g_rpSoftRun < kRpSoftMax : g_rpSoftFresh < kRpSoftFreshMax;  // in-flight-only batches count as fresh: a lost force ends too
        if (nSoft > 0 && tries) {
            k.rpSoftReal += real, k.rpSoftFresh += fresh, k.rpSoftInFlight += flight;
            while (InterlockedExchange(&g_rpRingLock, 1)) YieldProcessor();
            for (int i = 0; i < nSoft; i++) {
                int j = 0;
                while (j < g_rpNSoftPend && g_rpSoftPend[j] != soft[i]) j++;
                if (j < g_rpNSoftPend) continue;  // in flight: its force is already waiting for the next cut
                if (g_rpNSoftPend < 64) g_rpSoftPend[g_rpNSoftPend++] = soft[i];
                j = 0;
                while (j < g_rpNForce && g_rpForce[j] != soft[i]) j++;
                if (j == g_rpNForce) { if (g_rpNForce < 64) g_rpForce[g_rpNForce++] = soft[i]; else g_rpForceAll = 1; }
            }
            InterlockedExchange(&g_rpRingLock, 0);
            g_rpStale = 1;
            if (real) g_rpSoftRun++;
            else g_rpSoftFresh++;
            if (InterlockedIncrement(&g_rpResyncLog) <= 16)
                Log("snap replay: map resync: %d lot(s) missed (%d rebased in this batch, %d fresh, %d forced already), forced to "
                    "rebase at the next cut (first 0x%08lX%08lX; tries %d, fresh %d), no rebuild",
                    nSoft, real, fresh, flight, (DWORD)(soft[0] >> 32), (DWORD)soft[0], g_rpSoftRun, g_rpSoftFresh);
        } else {
            // 0.35.2 (docs/94 §21.17): why the soft path was not taken; each of these is a whole rebuild in the next cut (sim)
            ts3native_counters.rpHard[nSoft == -2 ? 0 : nSoft > 0 ? 1 : nSoft == -1 ? 2 : 3]++;
            RP_NEED_EPOCH();
        }
    } else if (g_rpStale) g_rpStale = 0, g_rpSoftRun = 0, g_rpSoftFresh = 0;  // the whole map matched live again
}
// 0.35.13 (docs/94 §22.16, I-94-463): an audited mismatch was only counted and logged, so the wrong clone lot stayed wrong
// (and fed P4 shadows) until the next whole rebuild: IP 0.35.12 audited lot 0x08F300536B484DF0 wrong three times in a row
// (live 13590, clone 13599 entries on Q 1), 0.35.9 the same lot and counts. Now the lot is forced to rebase at the next cut
// (the gate still counts the mismatch), and the first 4 mismatches per session name their entries: the next hold (+0x918
// held: the live trees are stable) lists that Q's leaf entries on both sides and logs those only one side has
static LONG g_rpAuditForced;
static void RpAuditForce(u64 key) {
    while (InterlockedExchange(&g_rpRingLock, 1)) YieldProcessor();
    int j = 0;
    while (j < g_rpNForce && g_rpForce[j] != key) j++;
    if (j == g_rpNForce) { if (g_rpNForce < 64) g_rpForce[g_rpNForce++] = key; else g_rpForceAll = 1; }
    const int f = RpAudFFind(key);
    if (f >= 0) g_rpAudFSt[f] = 1;
    else if (g_rpNAudF < 32) g_rpAudF[g_rpNAudF] = key, g_rpAudFSt[g_rpNAudF++] = 1;
    InterlockedExchange(&g_rpRingLock, 0);
    if (InterlockedIncrement(&g_rpAuditForced) <= 16)
        Log("snap replay audit: lot 0x%08lX%08lX forced to rebase at the next cut (%ld so far)", (DWORD)(key >> 32), (DWORD)key, g_rpAuditForced);
}
// 0.35.14 (docs/94 §22.17, I-94-470): LotQuadTree_ApplyRemove 0x666750 erases every map node of the id (and of ~id, the
// children) on its Q, but removes an FME's leaf entries (0x666010) only when the footprint's bounds for the FME's level
// (vtable +0x2C, level = FME +0x10 byte) are not flat: min x == max x or min z == max z skips the leaves and keeps the erase
// [verified disassembly 0x66683C..0x66687F, 0x666956..0x666999]. So when the clone's copy answers flat bounds where the live
// footprint did not, the clone keeps leaf entries the live Q lost while the maps agree: the IP lot 0x08F300536B484DF0 Q 1
// shape (9 entries, 3 footprints, map totals equal). The game's own +0x2C is asked once more here (the replayed remove asks
// it next anyway); a flat answer forces the lot to rebase at the next cut and is logged with the copy's bounds.
static LONG g_rpRemFlat;  // replayed removes that kept leaves (selftest)
// 0.35.18 (I-94-478): a non-flat copy answer that differs from the live one the REM record carries may miss the Q's rect
// where live's reached it (the IP lot 0x08F300536B484DF0 Q 1 shape with no "keeps leaves" line): the lot is forced as well
static LONG g_rpRemDiff;  // replayed removes whose copy bounds differ from live's (selftest)
static LONG g_rpRemPatched;  // of those, copies patched to live's answer (no rebase; 0.35.21)
static LONG g_rpRemPatchLog;
// 0 = no live answer or equal, 1 = differed and patched to live (no force), 2 = differs (force the lot)
static int RpRemDiff(const RpRemLive* lv, u8* fp, const u8* f, const float* r, const u8* q) {
    for (int i = 0; lv && i < lv->n; i++) {
        const RpRemLiveE& e = lv->e[i];
        if (e.cls != RpD(fp) || e.lo != RpD(fp + 0x14) || e.hi != RpD(fp + 0x18) || e.lvl != f[0x10]) continue;
        if (!memcmp(e.b, r, sizeof(e.b))) return 0;
        float c0[4];
        memcpy(c0, r, sizeof(c0));
        if (RpRemPatchIn(e.p, fp, e.lvl)) {  // 0.35.21: the live inputs into the copy, asked again
            float b[4] = {};
            const float* r2 = ((RpBoundsFn)(uintptr_t)RpD(RpP(fp) + 0x2C))(fp, NULL, b, f[0x10], 0.0f);
            if (r2 && !memcmp(e.b, r2, sizeof(e.b))) {
                const LONG n = InterlockedIncrement(&g_rpRemPatched);
                if (n <= 8 || !(n & (n - 1)))
                    Log("snap replay: replayed remove on Q 0x%08lX: footprint 0x%08lX%08lX class 0x%08lX level %lu: copy bounds %.3f %.3f "
                        "%.3f %.3f patched to live %.3f %.3f %.3f %.3f (%s inputs; %ld so far)", (DWORD)(uintptr_t)q, e.hi, e.lo, e.cls,
                        e.lvl, c0[0], c0[1], c0[2], c0[3], e.b[0], e.b[1], e.b[2], e.b[3], e.p.child ? "child z + parent box" : "box", n);
                return 1;
            }
        } else if (e.p.has && InterlockedIncrement(&g_rpRemPatchLog) <= 8)
            Log("snap replay: copy bounds not patchable (child list %s in live, other shape in the copy): footprint 0x%08lX%08lX class 0x%08lX",
                e.p.empty ? "empty" : "not empty", e.hi, e.lo, e.cls);
        const LONG n = InterlockedIncrement(&g_rpRemDiff);
        if (n <= 8 || !(n & (n - 1)))  // first 8, then at powers of two (a high rate stays visible)
            Log("snap replay: replayed remove on Q 0x%08lX: footprint 0x%08lX%08lX class 0x%08lX level %lu, FME +0x10 0x%08lX: "
                "copy bounds %.3f %.3f %.3f %.3f, live %.3f %.3f %.3f %.3f (%ld so far)", (DWORD)(uintptr_t)q, e.hi, e.lo, e.cls,
                e.lvl, RpD(f + 0x10), c0[0], c0[1], c0[2], c0[3], e.b[0], e.b[1], e.b[2], e.b[3], n);
        return 2;
    }
    return 0;
}
// 0.36.2 (docs/94 §22.31, I-94-508): the leaf entries hold no FME reference (leaf insert 0x6653A0 takes none) and the map
// erase 0x628F00 in ApplyRemove drops the FME's last one (0x6668A4..0x6668BE: deleting destructor at 0) [verified
// disassembly]. A replayed remove whose copy answers other bounds than the clone's insert used (flat, a drift, or patched to
// live's since 0.35.21) erases only part of the clone's leaves: the rest name a freed FME, and its +8 copy is freed by the
// GC. The next walk of that Q (0x667DB0 / 0x6685B0 in a P4 clone run, a later apply) called +0x60 / +0x84 through the
// freed copy: the IP 0.35.24 / 0.35.25 faults (eip in heap). Such a remove runs with the copy's +0x2C answering a rect that
// covers every node, so 0x666010 erases every leaf entry of the id's FMEs on that Q, as the map erase does with the nodes.
static float* __fastcall RpCoverBounds(const u8*, void*, float* out, DWORD, float) {
    out[0] = out[1] = -1e9f, out[2] = out[3] = 1e9f;
    return out;
}
static const int kRpCoverSlots = 0x80;  // 0x200 bytes of the class's table (footprint slots used reach +0x94)
struct RpCoverVt { DWORD vt; DWORD slot[kRpCoverSlots]; };
static RpCoverVt g_rpCoverVt[16];  // one per class seen; consumer only (one apply at a time)
static int g_rpNCoverVt;
static bool g_rpRemCoverOff;  // selftest negative control
static LONG g_rpRemCover;     // removes erased with the cover (selftest, log)
static DWORD RpCoverVtFor(DWORD vt) {  // 0 = table full (that copy keeps its own answer)
    for (int i = 0; i < g_rpNCoverVt; i++) if (g_rpCoverVt[i].vt == vt) return (DWORD)(uintptr_t)g_rpCoverVt[i].slot;
    if (g_rpNCoverVt == 16) return 0;
    RpCoverVt& t = g_rpCoverVt[g_rpNCoverVt];
    memcpy(t.slot, (const void*)(uintptr_t)vt, sizeof(t.slot));
    t.slot[0x2C / 4] = (DWORD)(uintptr_t)RpCoverBounds;
    t.vt = vt;
    g_rpNCoverVt++;
    return (DWORD)(uintptr_t)t.slot;
}
static void RpRemCovered(u8* q, DWORD lo, DWORD hi, u8* const* cov, int n) {
    DWORD vt[kRpRemLiveMax];
    for (int i = 0; i < n; i++) {
        vt[i] = RpD(cov[i]);
        if (const DWORD v = RpCoverVtFor(vt[i])) *(DWORD*)cov[i] = v;
    }
    __try { g_rpRem(q, NULL, lo, hi); } __finally { for (int i = 0; i < n; i++) *(DWORD*)cov[i] = vt[i]; }
    const LONG k = InterlockedIncrement(&g_rpRemCover);
    if (k == 1 || k == 10 || k == 100 || k == 1000 || k == 10000)
        Log("snap replay: removes erased with a cover rect (copy bounds flat or not the live answer): %ld so far", k);
}
// true = force the lot; cov gets the copies (deduplicated) whose answer is flat or differed from live's
static bool RpRemFlat(RpClone* c, const u8* q, DWORD lo, DWORD hi, const RpRemLive* lv, u8** cov, int* nCov) {
    const u8* m = c->m;
    const DWORD nb = m ? RpD(m + 8) : 0;
    const DWORD* bk = nb ? (const DWORD*)RpP(m + 4) : NULL;
    bool force = false;
    if (!bk) return false;
    for (int pass = 0; pass < 2; pass++) {
        const DWORD klo = pass ? ~lo : lo, khi = pass ? ~hi : hi;
        for (const u8* nd = (const u8*)(uintptr_t)bk[klo % nb]; nd; nd = RpP(nd + 0x10)) {
            if (RpD(nd) != klo || RpD(nd + 4) != khi) continue;
            const u8* f = RpP(nd + 8);
            // 0.35.16 (docs/94 §22.18, I-94-474): only an FME that holds leaves can keep them. W1 sets FME +0x10 bit 0x200
            // only when its leaf insert 0x6653A0 succeeded (0x665E7B), which needs the same non-flat bounds [verified
            // disassembly]; 0.35.14 also tested FMEs inserted flat (no leaves on either side): BP forced 15k lot rebases
            u8* fp = f && RpP(f + 0xC) == q && (RpD(f + 0x10) & 0x200) ? RpP(f + 8) : NULL;
            if (!fp) continue;
            int j = 0;
            while (j < *nCov && cov[j] != fp) j++;
            if (j < *nCov) continue;  // that copy is covered already (its other levels)
            float b[4] = {};
            const float* r = ((RpBoundsFn)(uintptr_t)RpD(RpP(fp) + 0x2C))(fp, NULL, b, f[0x10], 0.0f);
            int d = 3;  // flat
            if (r && r[0] != r[2] && r[1] != r[3]) {
                d = RpRemDiff(lv, fp, f, r, q);
                if (!d) continue;
            } else if (InterlockedIncrement(&g_rpRemFlat) <= 8)
                Log("snap replay: replayed remove of id 0x%08lX%08lX keeps leaves on Q 0x%08lX: footprint class 0x%08lX level %u, "
                    "FME +0x10 0x%08lX, copy bounds %.3f %.3f %.3f %.3f (flat)", khi, klo, (DWORD)(uintptr_t)q, RpD(fp), f[0x10],
                    RpD(f + 0x10), r ? r[0] : 0.f, r ? r[1] : 0.f, r ? r[2] : 0.f, r ? r[3] : 0.f);
            if (d != 1) force = true;  // the flat / unpatched copy answers other bounds than live for later inserts: rebase
            if (*nCov < kRpRemLiveMax) cov[(*nCov)++] = fp;
            else force = true;  // shortcut: past 16 copies the rest keep their own answer (leaves may stay until the rebase)
        }
    }
    return force;
}
static void RpRemForce(u64 key, const u8* q, DWORD lo, DWORD hi) {
    while (InterlockedExchange(&g_rpRingLock, 1)) YieldProcessor();
    int j = 0;
    while (j < g_rpNForce && g_rpForce[j] != key) j++;
    if (j == g_rpNForce) { if (g_rpNForce < 64) g_rpForce[g_rpNForce++] = key; else g_rpForceAll = 1; }
    InterlockedExchange(&g_rpRingLock, 0);
    if (g_rpRemFlat + g_rpRemDiff <= 16)
        Log("snap replay: lot 0x%08lX%08lX forced to rebase at the next cut (remove of 0x%08lX%08lX on Q 0x%08lX kept leaves or copy bounds not live's)",
            (DWORD)(key >> 32), (DWORD)key, hi, lo, (DWORD)(uintptr_t)q);
}
// replayed-insert answer check (0.36.35, RpInsLiveTake above): the FMEs of the footprint's key / ~key on q, before the insert
static int RpInsFmes(const RpClone* c, const u8* q, const u8* fp, DWORD* out, int cap) {
    int n = 0;
    __try {
        u64 key;
        const u8* m = c->m;
        const DWORD nb = m ? RpD(m + 8) : 0;
        const DWORD* bk = nb > 1 ? (const DWORD*)RpP(m + 4) : NULL;
        if (!bk || !RpFpKey(fp, &key)) return -1;
        for (int pass = 0; pass < 2; pass++) {
            const u64 kk = pass ? ~key : key;
            for (const u8* nd = (const u8*)(uintptr_t)bk[(DWORD)kk % nb]; nd; nd = RpP(nd + 0x10))
                if (RpD(nd) == (DWORD)kk && RpD(nd + 4) == (DWORD)(kk >> 32) && RpP(nd + 8) && RpP(RpP(nd + 8) + 0xC) == q) {
                    if (n == cap) return -1;  // shortcut: past 64 FMEs of one key on one Q the insert goes unchecked
                    out[n++] = RpD(nd + 8);
                }
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) { return -1; }
    return n;
}
// the live footprint a clone copy stands for: an in-block child through its parent's list (node +12), else the block header
static DWORD RpCopyLive(const u8* cp) {
    if (RpIsChild(cp)) {
        const u8* par = RpP(cp + 0xC);
        if (par && RpHasList(par))
            for (const u8* l = RpP(par + 0x24); l && l != par + 0x24; l = RpP(l)) if (RpP(l + 8) == cp) return RpD(l + 12);
    }
    return (DWORD)(uintptr_t)((const RpFpHdr*)(cp - 16))->live;
}
static void RpInsCheck(const RpClone* c, u64 lot, const u8* q, const u8* fp, const DWORD* pre, int nPre, const RpInsLive* lv) {
    Ts3nCounters& k = ts3native_counters;
    DWORD now[64];
    const int n = RpInsFmes(c, q, fp, now, 64);
    bool forced = false;
    for (int i = 0; i < n && lv; i++) {
        int j = 0;
        while (j < nPre && pre[j] != now[i]) j++;
        if (j < nPre) continue;  // an FME from before this insert
        __try {
            const u8* f = (const u8*)(uintptr_t)now[i];
            u8* cp = RpP(f + 8);
            if (!cp) continue;
            const DWORD live = RpCopyLive(cp), lvl = f[0x10];
            const RpInsLiveE* e = NULL;
            for (int x = 0; x < lv->n && !e; x++) if (lv->e[x].live == live && lv->e[x].lvl == lvl) e = &lv->e[x];
            if (!e) continue;  // a level or child the record did not ask (over kRpRemLiveMax): unchecked
            float b[4] = {};
            const float* r = ((RpBoundsFn)(uintptr_t)RpD(RpP(cp) + 0x2C))(cp, NULL, b, lvl, 0.0f);
            if (!r) continue;
            const bool fc = r[0] == r[2] || r[1] == r[3], fl = e->b[0] == e->b[2] || e->b[1] == e->b[3];
            float d = 0.f;
            for (int x = 0; x < 4; x++) { const float a = r[x] - e->b[x]; d = a > d ? a : -a > d ? -a : d; }
            if (fc == fl && d == 0.f) continue;
            const LONG cnt = InterlockedIncrement(&k.rpInsDiff);
            const bool force = g_rpInsCheck >= 2 && (fc != fl || d > 0.01f);
            if (cnt <= 8 || !(cnt & (cnt - 1)))
                Log("snap replay: replayed insert on Q 0x%08lX: copy of footprint 0x%08lX (class 0x%08lX, %s) level %lu answers %.3f %.3f %.3f %.3f, "
                    "live answered %.3f %.3f %.3f %.3f at the record (%s; %ld so far)", (DWORD)(uintptr_t)q, live, RpD(cp),
                    !RpIsChild(cp) ? "parent" : RpCopyLive(cp) == (DWORD)(uintptr_t)((const RpFpHdr*)(cp - 16))->live ? "own-block child" : "child in its parent's list", lvl,
                    r[0], r[1], r[2], r[3], e->b[0], e->b[1], e->b[2], e->b[3], force ? "lot forced" : g_rpInsCheck >= 2 ? "within 0.01" : "log only", cnt);
            if (force && !forced) { forced = true; RpForceLot(lot); InterlockedIncrement(&k.rpInsForced); }
        } __except (EXCEPTION_EXECUTE_HANDLER) {}
    }
}
struct RpDiffEnt { DWORD lo, hi, fl, w4, w8, cls, fme; };
static void RpTreeList(const u8* n, int depth, RpDiffEnt* v, DWORD cap, DWORD* nv) {  // RpTreeHash's walk
    if (!n || depth > 40) return;
    if (n[4] & 1) { for (int i = 0; i < 4; i++) RpTreeList(RpP(n + 0xC + 4 * i), depth + 1, v, cap, nv); return; }
    const u8* a = n + 8;
    DWORD cnt = 1;
    if (n[8] & 1) cnt = RpD(n + 0xC), a = (const u8*)(uintptr_t)(RpD(n + 8) & ~1u);
    for (DWORD j = 0; j < cnt && j < 65536 && *nv < cap; j++) {
        const u8* e = a + j * 12;
        RpDiffEnt& d = v[(*nv)++];
        memset(&d, 0, sizeof(d));
        d.w4 = RpD(e + 4), d.w8 = RpD(e + 8);
        const u8* f = RpP(e);
        const u8* fp = f ? RpP(f + 8) : NULL;
        d.fme = (DWORD)(uintptr_t)f;
        if (f) d.fl = RpD(f + 0x10);
        if (fp) d.lo = RpD(fp + 0x14), d.hi = RpD(fp + 0x18), d.cls = RpD(fp);
    }
}
static int __cdecl RpDiffCmp(const void* x, const void* y) { return memcmp(x, y, 5 * 4); }  // cls is not compared (the hash's class word is)
static u64 g_rpDiffLot;
static DWORD g_rpDiffSeq;  // ring position of the audit that asked for the diff (docs/157 §7)
static volatile int g_rpDiffQ = -1;
static LONG g_rpDiffN, g_rpDiffOnly[2];  // done; entries only live / only in the clone (selftest)
// 0.36.23 (docs/94 §22.32, I-94-510): a clone-only leaf entry whose FME no clone map node holds (id or ~id) names an FME the
// map erase freed: a replayed remove that missed leaves with no difference / cover line before it (log only; the audit still
// forces the lot)
static LONG g_rpDiffOrphan;
static bool RpMapHoldsFme(const RpClone* c, DWORD lo, DWORD hi, DWORD fme) {
    const u8* m = c->m;
    const DWORD nb = m ? RpD(m + 8) : 0;
    const DWORD* bk = nb ? (const DWORD*)RpP(m + 4) : NULL;
    if (!bk) return true;  // no map to ask: not counted
    for (int pass = 0; pass < 2; pass++) {
        const DWORD klo = pass ? ~lo : lo, khi = pass ? ~hi : hi;
        for (const u8* nd = (const u8*)(uintptr_t)bk[klo % nb]; nd; nd = RpP(nd + 0x10))
            if (RpD(nd) == klo && RpD(nd + 4) == khi && RpD(nd + 8) == fme) return true;
    }
    return false;
}
static void RpAuditDiffBody(RpClone* c);
static void RpAuditDiff(RpClone* c) {  // consumer, inside its hold
    const u64 lot = g_rpDiffLot;
    RpAuditDiffBody(c);
    while (InterlockedExchange(&g_rpRingLock, 1)) YieldProcessor();
    const bool held = g_rpHoldKey && g_rpHoldKey == lot;
    if (held) g_rpHoldLast = lot, g_rpHoldKey = 0, ts3native_counters.rpHold[1]++;
    InterlockedExchange(&g_rpRingLock, 0);
    if (held && ts3native_counters.rpHold[1] <= 16)
        Log("snap replay audit diff lot 0x%08lX%08lX: ran on the drifted clone, the lot's rebase held until now (AuditHoldRebase; %ld so far)",
            (DWORD)(lot >> 32), (DWORD)lot, ts3native_counters.rpHold[1]);
}
static void RpAuditDiffBody(RpClone* c) {
    const int qd = g_rpDiffQ;
    g_rpDiffQ = -1;
    g_rpDiffN++;
    const RpLot* l = RpFindLot(c, g_rpDiffLot);
    if (!l || qd >= l->nQ || !l->q[qd] || !l->liveQ[qd]) return;
    const DWORD cap = 1 << 16;
    RpDiffEnt* v[2] = {(RpDiffEnt*)RpAlloc(cap * sizeof(RpDiffEnt)), (RpDiffEnt*)RpAlloc(cap * sizeof(RpDiffEnt))};
    DWORD n[2] = {0, 0};
    if (v[0] && v[1]) {
        __try {
            RpTreeList(RpP((const u8*)(uintptr_t)l->liveQ[qd] + 0x24), 0, v[0], cap, &n[0]);
            RpTreeList(RpP(l->q[qd] + 0x24), 0, v[1], cap, &n[1]);
        } __except (EXCEPTION_EXECUTE_HANDLER) { n[0] = n[1] = 0; }
        qsort(v[0], n[0], sizeof(RpDiffEnt), RpDiffCmp), qsort(v[1], n[1], sizeof(RpDiffEnt), RpDiffCmp);
        // 0.36.35 (docs/157 §5.3, I-157-6): the live map's FMEs (the consumer holds +0x918: the live map is stable). A live
        // leaf entry on an FME no live map node holds is the game's own leftover: ApplyRemove erases leaves only inside the
        // footprint's rect at the remove; cells its insert-time rect covered keep entries on the freed (pooled) FME. The 0.36.2
        // cover erases every leaf of such a remove in the clone, so those entries are "only live" by design
        RpTab liveF = {};
        bool liveOk = true;
        __try {
            const u8* m = RpP(RpP((const u8*)(uintptr_t)l->liveQ[qd] + 0x20) + 0x24);
            const DWORD nb = m ? RpD(m + 8) : 0;
            const DWORD* bk = nb > 1 ? (const DWORD*)RpP(m + 4) : NULL;
            for (DWORD b = 0; bk && b < nb && liveOk; b++)
                for (const u8* nd = (const u8*)(uintptr_t)bk[b]; nd && liveOk; nd = RpP(nd + 0x10))
                    if (RpD(nd + 8) > 1 && !RpTabPut(liveF, RpD(nd + 8), 1)) liveOk = false;
            if (!bk) liveOk = false;
        } __except (EXCEPTION_EXECUTE_HANDLER) { liveOk = false; }
        LONG leftover = 0;
        DWORD i = 0, j = 0;
        int logged = 0;
        LONG only[2] = {0, 0}, orphans = 0;
        while (i < n[0] || j < n[1]) {
            const int s = i == n[0] ? 1 : j == n[1] ? -1 : RpDiffCmp(&v[0][i], &v[1][j]);
            if (!s) { i++, j++; continue; }
            const RpDiffEnt& d = s < 0 ? v[0][i++] : v[1][j++];
            only[s < 0 ? 0 : 1]++;
            if (s < 0 && liveOk && !RpTabFind(liveF, d.fme)) leftover++;
            if (s > 0) {
                bool held = true;
                __try { held = RpMapHoldsFme(c, d.lo, d.hi, d.fme); } __except (EXCEPTION_EXECUTE_HANDLER) {}
                orphans += !held;
            }
            if (logged++ < 12)
                Log("snap replay audit diff lot 0x%08lX%08lX Q %d: only %s: footprint 0x%08lX%08lX class 0x%08lX, FME +0x10 0x%08lX, entry +4 0x%08lX +8 0x%08lX",
                    (DWORD)(g_rpDiffLot >> 32), (DWORD)g_rpDiffLot, qd, s < 0 ? "live" : "in the clone", d.hi, d.lo, d.cls, d.fl, d.w4, d.w8);
        }
        g_rpDiffOnly[0] += only[0], g_rpDiffOnly[1] += only[1];
        g_rpDiffOrphan += orphans;
        Log("snap replay audit diff lot 0x%08lX%08lX Q %d: live %lu entries, clone %lu; only live %ld, only in the clone %ld "
            "(%ld of them on an FME no clone map node holds: I-94-510 shape; %ld removes covered so far; audit at ring %lu, now %lu)",
            (DWORD)(g_rpDiffLot >> 32), (DWORD)g_rpDiffLot, qd, n[0], n[1], only[0], only[1], orphans, g_rpRemCover, g_rpDiffSeq, g_rpTail);
        if (liveOk)
            Log("snap replay audit diff lot 0x%08lX%08lX Q %d: %ld of the %ld only-live entries are on an FME no live map node holds "
                "(the game's own leftover leaves of a remove whose rect moved; the 0.36.2 cover erases them in the clone)%s",
                (DWORD)(g_rpDiffLot >> 32), (DWORD)g_rpDiffLot, qd, leftover, only[0],
                only[0] && leftover == only[0] && !only[1] ? ": explained, game leftovers only" : "");
        RpTabFree(liveF);
    }
    RpFree(v[0]), RpFree(v[1]);
}
// docs/157 §7: each lot's last equal audit (ring position), so a mismatch line names the window of records in which the
// clone drifted: (last equal, this audit]. Lossy: a slot keeps the latest lot hashed into it (0 = none known).
struct RpAuditOk { u64 key; DWORD seq; };
static RpAuditOk g_rpAuditOk[256];
static DWORD g_rpMisSeq, g_rpMisOkSeq;  // the last mismatch's ring position and its lot's last equal one (selftest)
static RpAuditOk& RpAuditOkSlot(u64 key) { return g_rpAuditOk[(DWORD)(key ^ (key >> 32)) * 2654435761u >> 24]; }
static void RpAudit(RpClone* c, const RpAuditRec* a) {
    Ts3nCounters& k = ts3native_counters;
    LARGE_INTEGER t0;
    QueryPerformanceCounter(&t0);
    int seen = 0;
    for (int i = 0; i < a->n; i++) {
        const RpLot* l = RpFindLot(c, a->keys[i]);
        DWORD ents = 0;
        DWORD perQ[16];
        const u64 h = l ? RpLotHash(l->node, &ents, perQ) : 0;
        if (l) seen++;
        k.rpAudits++;
        RpAuditOk& ok = RpAuditOkSlot(a->keys[i]);
        if (l && h == a->hash[i]) {
            ok.key = a->keys[i], ok.seq = a->seq;
            if (g_rpNAudF) {  // 0.36.55: the first equal audit after a forced rebase shows the repair
                while (InterlockedExchange(&g_rpRingLock, 1)) YieldProcessor();
                const int f = RpAudFFind(a->keys[i]);
                const bool rep = f >= 0 && g_rpAudFSt[f] == 2;
                if (rep) g_rpAudF[f] = g_rpAudF[--g_rpNAudF], g_rpAudFSt[f] = g_rpAudFSt[g_rpNAudF], g_rpAudFRep++;
                InterlockedExchange(&g_rpRingLock, 0);
                if (rep && InterlockedIncrement(&g_rpAudFLog) <= 128)
                    Log("snap replay audit: lot 0x%08lX%08lX equal again at ring %lu, cut %ld (first audit after its forced rebase), entries %lu",
                        (DWORD)(a->keys[i] >> 32), (DWORD)a->keys[i], a->seq, a->cut, ents);
            }
            continue;
        }
        k.rpMis++;
        int qd = -1;  // first Q whose tree entry count differs (0.32.8)
        for (int j = 0; l && a->perQ && j < 16 && qd < 0; j++) if (perQ[j] != a->perQ[i * 16 + j]) qd = j;
        const bool diff = qd >= 0 && g_rpDiffQ < 0 && g_rpDiffN < 4;
        if (diff && g_rpAuditHold) {  // before the force: no cut may take the force without the hold
            while (InterlockedExchange(&g_rpRingLock, 1)) YieldProcessor();
            if (!g_rpHoldKey && g_rpHoldLast != a->keys[i]) g_rpHoldKey = a->keys[i], g_rpHoldCuts = 0, k.rpHold[0]++;
            InterlockedExchange(&g_rpRingLock, 0);
        }
        RpAuditForce(a->keys[i]);
        g_rpMisSeq = a->seq, g_rpMisOkSeq = ok.key == a->keys[i] ? ok.seq : 0;
        char tag[96];  // ring window of the drift (docs/157 §7; tools/p5-mismatch-report.ts reads it)
        if (g_rpMisOkSeq) sprintf_s(tag, " (ring %lu, cut %ld, last equal ring %lu, %lu records)", a->seq, a->cut, g_rpMisOkSeq, a->seq - g_rpMisOkSeq);
        else sprintf_s(tag, " (ring %lu, cut %ld, last equal ring none)", a->seq, a->cut);
        {
            if (diff) g_rpDiffLot = a->keys[i], g_rpDiffSeq = a->seq, g_rpDiffQ = qd;  // named at the next hold
            if (InterlockedIncrement(&g_rpLogged) <= (LONG)g_rpLogMis && l && qd >= 0)
                Log("snap replay audit MISMATCH lot 0x%08lX%08lX: Q %d (level index) entries live %lu clone %lu%s", (DWORD)(a->keys[i] >> 32), (DWORD)a->keys[i],
                    qd, a->perQ[i * 16 + qd], perQ[qd], tag);
        }
        if (InterlockedIncrement(&g_rpLogged) <= (LONG)g_rpLogMis)
            Log("snap replay audit MISMATCH lot 0x%08lX%08lX: %s, entries live %lu clone %lu (clone %d lots, map %lu / live %lu)%s",
                (DWORD)(a->keys[i] >> 32), (DWORD)a->keys[i], l ? "hash differs" : "not in the clone", a->ents[i], ents, c->nLots,
                RpD(c->m + 0xC), a->mapCount, tag);
    }
    if (a->nAll != c->nLots || seen != a->n) {
        k.rpAudits++, k.rpMis++;
        if (InterlockedIncrement(&g_rpLogged) <= (LONG)g_rpLogMis) Log("snap replay audit MISMATCH: clone has %d lots, live %d (ring %lu, cut %ld)", c->nLots, a->n, a->seq, a->cut);
    }
    k.rpAuditWorkerUs += UsSince(t0);
}
static void RpSync(RpClone* c, const RpSyncRec* s) {
    Ts3nCounters& k = ts3native_counters;
    k.rpSyncs++;
    if (RpD(c->m + 8) == s->mapN && RpD(c->m + 0xC) == s->mapCount && (DWORD)c->nLots == s->lots) return;
    k.rpDiverge++;
    RP_NEED_EPOCH();
    if (InterlockedIncrement(&g_rpLogged) <= (LONG)g_rpLogMis)
        Log("snap replay: clone diverged: map buckets %lu / live %lu, entries %lu / %lu, lots %d / %lu (rebuilt at the next cut)",
            RpD(c->m + 8), s->mapN, RpD(c->m + 0xC), s->mapCount, c->nLots, s->lots);
}
// consumer: the clone's Q-less nodes of the record's key (or all) replaced by the record's, in its order
static void RpNoQApply(RpClone* c, RpNoQRec* r) {
    u8* m = c->m;
    const DWORD nb = RpD(m + 8);
    DWORD* bk = (DWORD*)RpP(m + 4);
    if (nb <= 1) { if (r->n) RP_NEED_EPOCH(); return; }
    const DWORD b0 = r->all ? 0 : (DWORD)r->key % nb, b1 = r->all ? nb : b0 + 1;
    for (DWORD i = b0; i < b1; i++)
        for (DWORD* at = &bk[i]; *at;) {
            u8* nd = (u8*)(uintptr_t)*at;
            const u8* f = RpP(nd + 8);
            if (f && !RpD(f + 0xC) && (r->all || (RpD(nd) == (DWORD)r->key && RpD(nd + 4) == (DWORD)(r->key >> 32)))) {
                *at = RpD(nd + 0x10);
                g_rpMapNodeFree(m, NULL, nd);
                (*(DWORD*)(m + 0xC))--;
            } else at = (DWORD*)(nd + 0x10);
        }
    for (int i = 0; i < r->n; i++) {
        RpNoQEnt& e = r->e[i];
        u8* fp = e.liveFp ? RpFpUse(c, e.liveFp, e.copy) : NULL;
        e.copy = NULL;  // RpFpUse took it (kept or freed)
        if (e.liveFp && !fp) { RP_NEED_EPOCH(); continue; }  // no copy of its footprint: rebuild
        u8* f = (u8*)g_rpFmePool(0x14, 0xFEAC80, 0, 0, 0, 0);
        if (!f) { RP_NEED_EPOCH(); continue; }
        memcpy(f, e.fme, 0x14);
        *(LONG*)(f + 4) = 0;
        *(u8**)(f + 8) = fp;
        if (fp) (*(LONG*)(fp + 8))++;
        *(u8**)(f + 0xC) = NULL;
        // shortcut: RpMapAdd puts it after the run of its key; the live insert 0x628220 may order equal keys otherwise (the
        // count, which the SYNC checks, is exact; order only matters to a search walking equal keys)
        if (!RpMapAdd(c, e.lo, e.hi, f)) { ((RpDtorFn)(uintptr_t)RpD(RpP(f)))(f, NULL, 1); RP_NEED_EPOCH(); }
    }
}
// one record; true = keep going in this hold, false = release the lock first (audit / epoch)
static bool RpApply(const RpRec& r) {
    Ts3nCounters& k = ts3native_counters;
    RpClone* c = g_rpClone;
    switch (r.type) {
        case RP_NOQ:
            if (c) RpNoQApply(c, (RpNoQRec*)r.p);
            RpNoQFree((RpNoQRec*)r.p);
            break;
        case RP_EPOCH:
            RpGraveClone(g_rpClone);  // freed in slices by the next holds (0.35.13)
            g_rpClone = (RpClone*)r.p;
            g_rpStale = 0, g_rpSoftRun = 0, g_rpBad = 0, g_rpSoftFresh = 0;
            while (InterlockedExchange(&g_rpRingLock, 1)) YieldProcessor();
            g_rpNSoftPend = 0;  // the new clone was built after those forces' misses
            InterlockedExchange(&g_rpRingLock, 0);
            k.rpLots = g_rpClone->nLots;
            k.rpReady |= 2;
            break;
        case RP_INS: case RP_REM: {
            u8* q = NULL;
            RpLot* l = c ? RpFindQ(c, r.a, &q) : NULL;
            const int skip = !c ? RPS_NOCLONE : !l ? RPS_NOQ : -1;
            if (skip >= 0) {
                k.rpSkip[skip]++;
                if (skip == RPS_NOQ && k.rpSkip[RPS_NOQ] <= 8) {  // which Q (0.30.2 diagnostics; read under the consumer's +0x918 hold)
                    const u64 key = RpQKey(r.a);
                    Log("snap replay: %s on a Q the clone does not have: Q 0x%08lX, lot 0x%08lX%08lX (clone has that lot: %s)",
                        r.type == RP_INS ? "insert" : "remove", r.a, (DWORD)(key >> 32), (DWORD)key, RpFindLot(c, key) ? "yes" : "no");
                }
                if (r.type == RP_INS) RpFpFree((u8*)r.p), RpFree((void*)(uintptr_t)r.pad); else RpFree(r.p);
                return true;
            }
            if (l->stale) k.rpSkip[RPS_STALE]++;  // applied anyway: its footprints are shared with other lots (the rebase replaces the lot)
            if (r.type == RP_INS) {
                u8* fp = RpFpUse(c, (u8*)(uintptr_t)r.b, (u8*)r.p);
                if (!fp) {  // the writer's copy failed (counted in rpCopyFail): this lot is rebased at the next cut instead
                    k.rpSkip[RPS_NOCOPY]++;
                    l->stale = true;
                    while (InterlockedExchange(&g_rpRingLock, 1)) YieldProcessor();
                    int i = 0;
                    while (i < g_rpNForce && g_rpForce[i] != l->key) i++;
                    if (i == g_rpNForce) { if (g_rpNForce < 64) g_rpForce[g_rpNForce++] = l->key; else g_rpForceAll = 1; }
                    InterlockedExchange(&g_rpRingLock, 0);
                    RpFree((void*)(uintptr_t)r.pad);
                    return true;
                }
                DWORD pre[64];
                const int nPre = r.pad && g_rpInsCheck ? RpInsFmes(c, q, fp, pre, 64) : -1;
                g_rpIns(q, NULL, fp, r.c);
                if (nPre >= 0) RpInsCheck(c, l->key, q, fp, pre, nPre, (const RpInsLive*)(uintptr_t)r.pad);
                RpFree((void*)(uintptr_t)r.pad);
            } else {
                u8* cov[kRpRemLiveMax];
                int nCov = 0;
                if (RpRemFlat(c, q, r.b, r.c, (const RpRemLive*)r.p, cov, &nCov)) RpRemForce(l->key, q, r.b, r.c);
                if (nCov && !g_rpRemCoverOff) RpRemCovered(q, r.b, r.c, cov, nCov);
                else g_rpRem(q, NULL, r.b, r.c);
                RpFree(r.p);
            }
            break;
        }
        case RP_STALE:
            if (c) if (RpLot* l = RpFindLot(c, ((u64)r.b << 32) | r.a)) l->stale = true;
            break;
        case RP_LOTS:
            if (c) RpApplyBatch(c, (RpBatch*)r.p);
            RpBatchFree((RpBatch*)r.p, true);
            if (c) k.rpLots = c->nLots;
            break;
        case RP_SYNC:
            if (c) RpSync(c, (const RpSyncRec*)r.p);
            RpFree(r.p);
            break;
        case RP_AUDIT: return false;  // compared outside the lock (the clone is ours)
        default: break;
    }
    k.rpApplied[r.type]++;
    return true;
}
// apply up to the published position; lock = take +0x918 (the worker); the tests call it directly
// P4 (p4clone.cpp): the consumer stops at g_rpHoldAt (the armed job's cut) until the clone run is done or the hold expires,
// and never drains while a clone run uses the clone (g_rpUse, exclusive)
static volatile DWORD g_rpHoldAt;
static volatile ULONGLONG g_rpHoldUntil;
static SRWLOCK g_rpUse = SRWLOCK_INIT;
static DWORD RpLimit() {
    const DWORD pub = g_rpPub, h = g_rpHoldAt;
    if (h && GetTickCount64() < g_rpHoldUntil && (DWORD)(h - g_rpTail) <= (DWORD)(pub - g_rpTail)) return h;
    return pub;
}
static bool RpDrainBody(bool lock);
// 0.35.13 (docs/94 §22.16): the consumer's time under +0x918 by record type (and the grave), logged every 5 min: which work
// fills the 206 ms/s of 0.35.12 (only single records over 100 ms were logged)
static LONGLONG g_rpTypeUs[RP_NTYPES + 3];  // + the grave, + RP_LOTS applied outside the lock (0.35.23), + RecsUnlocked rounds (0.36.34)
static LONG g_rpTypeN[RP_NTYPES + 3], g_rpTypeMax[RP_NTYPES + 3];
static ULONGLONG g_rpTypeAt;
static void RpTypeTime(DWORD type, LONG us) {
    if (type > RP_NTYPES + 2) return;
    g_rpTypeUs[type] += us, g_rpTypeN[type]++;
    if (us > g_rpTypeMax[type]) g_rpTypeMax[type] = us;
    const ULONGLONG now = GetTickCount64();
    static LONG s_apUs0;
    static LONG64 s_apCy0;
    if (!g_rpTypeAt) g_rpTypeAt = now, s_apUs0 = ts3native_counters.rpApplyUs, s_apCy0 = g_cyApply;
    if (now - g_rpTypeAt < 300000) return;
    static const char* const kName[RP_NTYPES + 3] = {"noq", "ins", "rem", "stale", "lots", "epoch", "audit", "sync", "grave", "lots outside the lock",
                                                     "records outside the lock (RecsUnlocked rounds)"};
    char buf[640];
    int o = 0;
    for (int i = 0; i <= RP_NTYPES + 2 && o < 560; i++)
        if (g_rpTypeN[i]) o += sprintf_s(buf + o, sizeof(buf) - o, " %s %lld ms (%ld, max %ld us);", kName[i], g_rpTypeUs[i] / 1000, g_rpTypeN[i], g_rpTypeMax[i]);
    buf[o] = 0;
    const double s = (now - g_rpTypeAt) / 1000.0;  // 0.35.21: the holds (closed before this record's) in wall ms/s and thread CPU
    Log("snap replay: consumer time under the routing lock by record, last %lu s:%s holds: wall %.2f ms/s, thread CPU (2.3 GHz nominal) "
        "%.2f cyc-ms/s", (DWORD)s, buf, (ts3native_counters.rpApplyUs - s_apUs0) / 1000.0 / s, CycMsPerS(g_cyApply - s_apCy0, s));
    s_apUs0 = ts3native_counters.rpApplyUs, s_apCy0 = g_cyApply;
    static LONG s_fr0, s_rt0, s_rs0;  // 0.35.25: which INS / NOQ footprint path the time went to
    Log("snap replay: old footprint copies at an insert, last %lu s: freed in place %ld, retired %ld (whole-clone repin scans %ld); "
        "InsFreeInPlace=%ld", (DWORD)s, g_rpInsFreed - s_fr0, g_rpInsRetired - s_rt0, g_rpRepinScans - s_rs0, g_rpInsFreeInPlace);
    s_fr0 = g_rpInsFreed, s_rt0 = g_rpInsRetired, s_rs0 = g_rpRepinScans;
    memset(g_rpTypeUs, 0, sizeof(g_rpTypeUs)), memset(g_rpTypeN, 0, sizeof(g_rpTypeN)), memset(g_rpTypeMax, 0, sizeof(g_rpTypeMax));
    g_rpTypeAt = now;
}
static volatile LONG g_rpStop;  // tests only
static volatile LONG g_rpLongLog;
static void RpDrain(bool lock) {  // exclusive with a P4 clone run (g_rpUse)
    for (;;) {
        AcquireSRWLockExclusive(&g_rpUse);
        CfUse(true);
        g_rpPhase = 1;
        // the replay's own ApplyInsert / ApplyRemove on clone Qs enter P1's W1/W2 stubs too: not records (0.32.1; before, each
        // applied record came back as one on an unknown Q: BP 34,086 skips, a refresh batch at nearly every cut, resync failures)
        t_rpReplay++;
        const bool busy = RpDrainBody(lock);
        t_rpReplay--;
        g_rpPhase = 0;
        CfUse(false);
        ReleaseSRWLockExclusive(&g_rpUse);
        if (!busy || g_rpStop) return;
        Sleep(1);  // the routing lock is busy: wait for it without g_rpUse (0.33.2)
    }
}
// true: stopped because the routing lock was busy (the caller retries without g_rpUse). Never a wait for +0x918 while
// holding g_rpUse: 0.33.1 waited there (a thread holding +0x918 and waiting for g_rpUse, as P5's acquire inside its job,
// would never get either); 0.33.2 only tried it, so with the sim and the jobs holding +0x918 most of the time the consumer
// fell behind (0.33.3: "mat"/"timeout", and parked shadows never reached their cut). 0.34.1 (docs/94 §21.16): lets go of
// g_rpUse, waits for +0x918 holding nothing of ours, then tries g_rpUse; a clone run that took it meanwhile is waited out
// with +0x918 released again
// 0.35.23 (docs/94 §22.26.1): [RouteSnap] LotsUnlocked (0 = off, the default). A rebase batch (RP_LOTS) touches only the
// clone (lots, map M with its private node pool at M+0x1C, footprint tables), the private pool set and EA heap blocks
// [verified source: RpApplyBatch and its callees read live addresses only as table keys]; the clone is guarded by g_rpUse,
// not by +0x918 (RpAudit and RpGc already run outside the lock). So with LotsUnlocked=1 it is applied holding g_rpUse and the
// pool set but not +0x918: a whole-map resync (BP 0444: 45 ms, a force-all 181 ms) no longer stalls the sim. The batch's
// leftovers (untaken shells: portal releases, FME destructors) are freed at the start of the next locked hold, as before
static LONG g_rpLotsUnlocked;
static RpBatch* g_rpLotsFreePend;  // consumer only: an unlocked batch waiting for its free under +0x918
static LONG g_rpLotsUnlockedN, g_rpLotsUnlockedMaxUs;  // selftest / log
static bool RpLotsUnlocked() {  // false = the pool set is busy (the caller retries); the ring tail is an RP_LOTS record
    Ts3nCounters& k = ts3native_counters;
    LONG op;
    if (!RpOpTry(&op)) return false;
    const RpRec& r = g_rpRing[g_rpTail & (kRpRing - 1)];
    RpBatch* b = (RpBatch*)r.p;
    g_rpPhase = 2 | RP_LOTS << 8 | 0x10000;  // bit 16: outside +0x918
    LARGE_INTEGER t0;
    QueryPerformanceCounter(&t0);
    __try {
        if (g_rpClone) RpApplyBatch(g_rpClone, b), k.rpLots = g_rpClone->nLots;
        k.rpApplied[RP_LOTS]++;
        g_rpLotsFreePend = b;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        k.rpFaults++;
        RP_NEED_EPOCH();
        Log("snap replay: fault applying record %lu (unlocked batch): clone dropped, rebuilt at the next cut", g_rpTail);
        g_rpClone = NULL;  // shortcut: as the locked path, the batch and the clone are dropped, not freed
        k.rpReady &= ~2;
    }
    const LONG us = UsSince(t0);
    g_rpLotsUnlockedN++;
    if (us > g_rpLotsUnlockedMaxUs) g_rpLotsUnlockedMaxUs = us;
    RpTypeTime(RP_NTYPES + 1, us);
    g_rpTail++;
    RpOpLeave(op);
    g_rpPhase = 1;
    return true;
}
// 0.36.34 (docs/182): [RouteSnap] RecsUnlocked (0 = off, the default). INS, REM, NOQ, STALE and SYNC records touch only the
// clone (its Qs, trees, map M, footprint copies and tables), the private pool set, the locked FME pool and the locked EA heap:
// the game's ApplyInsert 0x665C50 / ApplyRemove 0x666750 closure reaches no other pool or lock-guarded world state (docs/94
// §16.6 forward closure [verified static]; footprint slots on clone copies [inferred], counted by ulLockTrip), and the
// records' live addresses are table keys or read fault-guarded (RpQKey). So with RecsUnlocked=1 they are applied holding
// g_rpUse and the pool set but not +0x918, up to 1 ms a round, as LotsUnlocked does for RP_LOTS. EPOCH (portal refs of the
// replaced clone), AUDIT, the grave and leftover frees keep their locked holds. The cut never touches g_rpClone; the flags the
// consumer sets (g_rpBad, g_rpNeedEpoch, forces under the ring lock) are the ones LotsUnlocked already sets outside the lock
static LONG g_rpRecsUnlocked;
static bool RpUlType(DWORD t) { return t == RP_INS || t == RP_REM || t == RP_NOQ || t == RP_STALE || t == RP_SYNC; }
static bool RpRecsUnlocked() {  // false = the pool set is busy (the caller retries); the ring tail is an RpUlType record
    Ts3nCounters& k = ts3native_counters;
    LONG op;
    if (!RpOpTry(&op)) { k.ulOpBusy++; return false; }
    LARGE_INTEGER t0;
    QueryPerformanceCounter(&t0);
    LONG n = 0;
    t_rpUl = 1;
    __try {
        while (g_rpTail != RpLimit()) {
            const RpRec& r = g_rpRing[g_rpTail & (kRpRing - 1)];
            if (!RpUlType(r.type)) break;
            g_rpPhase = 2 | (r.type & 0xFF) << 8 | 0x10000;  // bit 16: outside +0x918
            RpApply(r);  // false only for AUDIT, not in the set
            g_rpTail++, n++;
            if (UsSince(t0) > 1000) break;
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        k.rpFaults++, k.ulFaults++;
        RP_NEED_EPOCH();
        Log("snap replay: fault applying record %lu (RecsUnlocked round): clone dropped, rebuilt at the next cut", g_rpTail);
        g_rpTail++;  // shortcut: as the locked path, the record's payload and the clone are dropped, not freed
        g_rpClone = NULL;
        k.rpReady &= ~2;
    }
    t_rpUl = 0;
    const LONG us = UsSince(t0);
    k.ulRounds++, k.ulRecs += n, k.ulUs += us;
    if (us > k.ulMaxUs) k.ulMaxUs = us;
    RpTypeTime(RP_NTYPES + 2, us);
    RpOpLeave(op);
    g_rpPhase = 1;
    return true;
}
static void RpLotsFreePend() {  // inside a hold (+0x918 and the pool set held)
    if (RpBatch* b = g_rpLotsFreePend) g_rpLotsFreePend = NULL, RpBatchFree(b, true);
}
static bool RpDrainBody(bool lock) {
    Ts3nCounters& k = ts3native_counters;
    static const DWORD kTry = 0, kInf = 0xFFFFFFFF;
    // the grave is freed in holds of its own too, except while a P4 hold is armed (its clone run waits for g_rpUse)
    while (g_rpTail != RpLimit() || (RpGraveBusy() && !g_rpHoldAt) || g_rpLotsFreePend) {
        if (g_rpLotsUnlocked && !g_rpLotsFreePend && g_rpTail != RpLimit() && g_rpRing[g_rpTail & (kRpRing - 1)].type == RP_LOTS) {
            if (!RpLotsUnlocked()) return true;  // the pool set is busy: retry without our locks
            continue;
        }
        if (g_rpRecsUnlocked && !g_rpLotsFreePend && g_rpTail != RpLimit() && RpUlType(g_rpRing[g_rpTail & (kRpRing - 1)].type)) {
            if (!RpRecsUnlocked()) return true;  // the pool set is busy: retry without our locks
            static DWORD s_ulGc;  // as the locked holds: the GC once per 16 rounds or when caught up (our heap only: no lock)
            if (g_rpClone && (++s_ulGc % 16 == 0 || g_rpTail == RpLimit())) RpGc(g_rpClone);
            Max(&k.rpLag, (LONG)(g_rpHead - g_rpTail));
            if (!g_rpOn) break;
            continue;
        }
        const u8* world = *g_spWorldSlot;
        LARGE_INTEGER tw, t0;
        QueryPerformanceCounter(&tw);
        void* lk = world ? (void*)(world + 0x918) : NULL;
        if (lock && !lk) return false;
        if (lock && g_spAcq(lk, NULL, &kTry) <= 0) {
            g_rpPhase = 4;  // waiting for +0x918, g_rpUse released
            CfUse(false);
            ReleaseSRWLockExclusive(&g_rpUse);
            const LONG r = g_spAcq(lk, NULL, &kInf);
            if (r <= 0 || !TryAcquireSRWLockExclusive(&g_rpUse)) {
                if (r > 0) g_spRel(lk, NULL);
                AcquireSRWLockExclusive(&g_rpUse);  // holding nothing else: a clone run's end
                CfUse(true);
                g_rpPhase = 1;
                k.rpLockWaitUs += UsSince(tw);
                continue;  // the limit again: that run may have moved the hold
            }
            CfUse(true);
            g_rpPhase = 1;
        }
        LONG op;  // inside the hold: this thread's tree pool pops are private (pools.cpp)
        if (!RpOpTry(&op)) {  // the pool set is a P4 run's (0.35.12; not expected: runs hold g_rpUse): retry without our locks
            if (lock) g_spRel(lk, NULL);
            return true;
        }
        k.rpLockWaitUs += UsSince(tw);
        QueryPerformanceCounter(&t0);
        const LONG64 cy0 = CycNow();
        const RpRec* audit = NULL;
        __try {
            if (g_rpClone && world && PlMoved(g_rpClone->seen, RpP(world + 0x95C))) {  // the game's pools were recreated (§16.6)
                RpGraveStep(-1);
                RpFreeClone(g_rpClone);  // private memory only: safe after PathPlanner_Reset; records skip until the next EPOCH
                g_rpClone = NULL;        // (the sim's next cut sees the change itself and builds that EPOCH)
                k.rpReady &= ~2;
            }
            RpLotsFreePend();
            if (g_rpDiffQ >= 0 && g_rpClone) RpAuditDiff(g_rpClone);
            if (RpGraveBusy()) {
                LARGE_INTEGER tg;
                QueryPerformanceCounter(&tg);
                RpGraveStep(1000);
                RpTypeTime(RP_NTYPES, UsSince(tg));
            }
            while (g_rpTail != RpLimit()) {
                const RpRec& r = g_rpRing[g_rpTail & (kRpRing - 1)];
                if (g_rpLotsUnlocked && r.type == RP_LOTS) break;  // applied outside the lock by the next round (0.35.23)
                if (g_rpRecsUnlocked && RpUlType(r.type)) break;     // the same (0.36.34, RecsUnlocked)
                g_rpPhase = 2 | (r.type & 0xFF) << 8;
                LARGE_INTEGER ta;
                QueryPerformanceCounter(&ta);
                const DWORD type = r.type;
                const RpBatch* rb = type == RP_LOTS ? (const RpBatch*)r.p : NULL;
                const int bn = rb ? rb->n : 0, bm = rb ? rb->nMap : 0, bf = rb ? (int)rb->fps.used : 0;
                if (!RpApply(r)) { audit = &r; break; }
                const LONG ua = UsSince(ta);  // 0.34.1: one record's apply under +0x918, logged when long (the hold's cause)
                RpTypeTime(type, ua);
                if (ua > 100000 && InterlockedIncrement(&g_rpLongLog) <= 16)
                    Log("snap replay: record type %lu applied in %ld ms under the routing lock (batch: %d lots, %d map nodes, %d footprints)",
                        type, ua / 1000, bn, bm, bf);
                g_rpTail++;
                if (UsSince(t0) > 1000) break;
            }
        } __except (EXCEPTION_EXECUTE_HANDLER) {
            k.rpFaults++;
            RP_NEED_EPOCH();
            Log("snap replay: fault applying record %lu: clone dropped, rebuilt at the next cut", g_rpTail);
            g_rpTail++;  // shortcut: that record's payload and the clone may hold half-applied game state: dropped, not freed
            g_rpClone = NULL;
            k.rpReady &= ~2;
        }
        const LONG us = UsSince(t0);
        g_cyApply += CycNow() - cy0;  // consumer thread only
        k.rpApplyUs += us;
        Max(&k.rpApplyMaxUs, us);
        RpOpLeave(op);
        if (lock) g_spRel(lk, NULL);
        g_rpPhase = 1;
        if (audit) {
            if (g_rpClone) RpAudit(g_rpClone, (const RpAuditRec*)audit->p);
            RpAuditFree((RpAuditRec*)audit->p);
            k.rpApplied[RP_AUDIT]++;
            g_rpTail++;
        }
        static DWORD s_gcHolds;  // 0.35.12: the GC scans every footprint slot; once per 16 holds or when caught up, not per hold
        if (g_rpClone && (++s_gcHolds % 16 == 0 || g_rpTail == RpLimit())) RpGc(g_rpClone);  // our heap only: no lock
        Max(&k.rpLag, (LONG)(g_rpHead - g_rpTail));
        if (!g_rpOn) break;
    }
    if (!g_rpOn && g_rpClone) {  // switched off: drop the clone under the lock
        const u8* world = *g_spWorldSlot;
        LONG op;
        if (!lock || (world && g_spAcq((void*)(world + 0x918), NULL, &kTry) > 0)) {
            if (!RpOpTry(&op)) { if (lock) g_spRel((void*)(world + 0x918), NULL); return true; }
            RpLotsFreePend();
            RpGraveStep(-1);
            RpFreeClone(g_rpClone);
            RpOpLeave(op);
            g_rpClone = NULL;
            k.rpReady &= ~2;
            if (lock) g_spRel((void*)(world + 0x918), NULL);
        } else if (world) return true;
    }
    return false;
}
static void LuPoll();  // lotuse.cpp: [RouteSnap] LotUseReadout walk, between rounds
static DWORD WINAPI RpThread(void*) {
    g_rpTid = GetCurrentThreadId();
    while (!g_rpStop) {
        WaitForSingleObject(g_rpEvent, 1000);
        LuPoll();
        RpDrain(true);
    }
    return 0;
}

// SpLotStrategy "replay": begin = the cut; no per-version lot images (the replay audit compares hashes itself)
static_assert(offsetof(Ts3nCounters, rpReady) == 0x2768 && offsetof(Ts3nCounters, rpMis) == 0x281C && offsetof(Ts3nCounters, snP4Ready) == 0x2968,
              "counters v49 layout (docs/registry.md)");
static void RpBegin(SpVer*, const SpVer*, const u8* world, const SpDirty&, const u64*, int) { RpCut(world); }
static const SpLotStrategy kSpLotReplay = {"replay", NULL, RpBegin, NULL, RpPre};

// [RouteSnap] Lots=replay (from ApplySnapImages)
static bool ApplyReplay(const wchar_t* ini) {
    Ts3nCounters& k = ts3native_counters;
    wchar_t lots[32];
    GetPrivateProfileStringW(L"RouteSnap", L"Lots", L"none", lots, 32, ini);
    if (_wcsicmp(lots, L"replay")) { Log("route snapshot lots: none (Lots=%ls)", lots); return false; }
    g_rpAuditEvery = (DWORD)Clamp(GetPrivateProfileIntW(L"RouteSnap", L"ReplayAuditEvery", 4, ini), 0, 100000);
    g_rpAuditLots = (DWORD)Clamp(GetPrivateProfileIntW(L"RouteSnap", L"ReplayAuditLots", 2, ini), 0, 1024);
    g_rpCapBytes = Clamp(GetPrivateProfileIntW(L"RouteSnap", L"ReplayCapMB", 128, ini), 16, 512) << 20;
    g_rpLogMis = g_spLogMis;
    g_rpPrebuild = GetPrivateProfileIntW(L"RouteSnap", L"Prebuild", 0, ini) != 0;
    g_rpQuietMs = (DWORD)Clamp(GetPrivateProfileIntW(L"RouteSnap", L"ReplayQuietMs", 0, ini), 0, 2000);
    g_rpSigMs = (DWORD)Clamp(GetPrivateProfileIntW(L"RouteSnap", L"ReplaySigMs", 0, ini), 0, 2000);
    g_rpLotsUnlocked = GetPrivateProfileIntW(L"RouteSnap", L"LotsUnlocked", 0, ini) != 0;
    g_rpLazyKeep = GetPrivateProfileIntW(L"RouteSnap", L"LazyKeep", 0, ini) != 0;
    g_rpInsFreeInPlace = GetPrivateProfileIntW(L"RouteSnap", L"InsFreeInPlace", 0, ini) != 0;
    g_rpRecsUnlocked = GetPrivateProfileIntW(L"RouteSnap", L"RecsUnlocked", 0, ini) != 0;
    g_rpAuditHold = GetPrivateProfileIntW(L"RouteSnap", L"AuditHoldRebase", 0, ini) != 0;
    // docs/182 §8: the apply's lot-table ask answered from the clone's table inside a round (both call sites, else off)
    if (g_rpRecsUnlocked && !PatchCalls("replay lot table on the clone (RecsUnlocked)", kRpGetIfPresentSites, 2)) {
        Log("SKIP route snapshot RecsUnlocked: the lot-table call sites are not as expected (another hook?)");
        g_rpRecsUnlocked = 0;
    }
    k.ulMode = g_rpRecsUnlocked;
    g_rpInsCheck = Clamp(GetPrivateProfileIntW(L"RouteSnap", L"InsCheck", 1, ini), 0, 2);
    // docs/66 §7.1 slimming (0.36.73, both off by default): A1 pool regions grown on demand, A3 one build per load + one heap per build
    const LONG chunk = GetPrivateProfileIntW(L"RouteSnap", L"PoolChunkMB", 0, ini);
    g_plChunkMB = chunk ? (DWORD)Clamp(chunk, 16, 48) : 0;
    g_rpEpochHeap = GetPrivateProfileIntW(L"RouteSnap", L"EpochHeap", 0, ini) != 0;
    k.rpGenOn = g_rpEpochHeap;
    if (g_plChunkMB || g_rpEpochHeap) Log("route snapshot slimming (docs/66 §7.1): PoolChunkMB=%lu, EpochHeap=%d", g_plChunkMB, g_rpEpochHeap ? 1 : 0);
    g_rpRing = (RpRec*)VirtualAlloc(NULL, kRpRing * sizeof(RpRec), MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    g_rpEvent = CreateEventW(NULL, FALSE, FALSE, NULL);
    if (!g_rpRing || !g_rpEvent) { Log("SKIP route snapshot replay: no ring"); return false; }
    // private tree and map pools (docs/94 §16.6-§16.7): without them the clone would share unlocked free lists with the game
    if (!PlInstall()) { Log("SKIP route snapshot replay: no private pools"); return false; }
    g_rpHeap = g_rpHeap ? g_rpHeap : HeapCreate(0, 0, 0);
    g_rpThread = CreateThread(NULL, 1 << 20, RpThread, NULL, STACK_SIZE_PARAM_IS_A_RESERVATION, NULL);
    if (!g_rpThread) { Log("SKIP route snapshot replay: no thread"); return false; }
    g_spLot = &kSpLotReplay;
    RpLockKeyInstall();
    g_rpOn = 1;
    k.rpReady = 1 | 4;
    Log("OK   route snapshot lots: replay (docs/94 §11 B, audit only): ring %lu records, ReplayAuditEvery=%lu, ReplayAuditLots=%lu, ReplayCapMB=%ld, Prebuild=%d, ReplayQuietMs=%lu, LazyKeep=%ld, ReplaySigMs=%lu, LotsUnlocked=%ld, InsFreeInPlace=%ld, RecsUnlocked=%ld, InsCheck=%ld, AuditHoldRebase=%ld",
        kRpRing, g_rpAuditEvery, g_rpAuditLots, g_rpCapBytes >> 20, g_rpPrebuild ? 1 : 0, g_rpQuietMs, g_rpLazyKeep, g_rpSigMs, g_rpLotsUnlocked,
        g_rpInsFreeInPlace, g_rpRecsUnlocked, g_rpInsCheck, g_rpAuditHold);
    return true;
}
