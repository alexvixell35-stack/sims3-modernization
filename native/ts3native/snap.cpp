// [RouteSnap] Enabled=1 Mode=0 (docs/94 §5 phase P3, 0.26.0, counters v47): snapshot H, images and audit only, no search.
// Included by ts3native.cpp after routesnap.cpp (P1); the change signals come from P1's stubs (SnEnter / SnIcEnter call
// SpNote / SpIcNote / SpStart below), so this file adds no detour of its own. Enabled=1 also installs P1's sites.
//
// What is copied (docs/94 §8.2, §9): immutable, refcounted images shared between versions:
//   R      world+0x54: w, h, size, chunk offset table, record area (type and blocked bits included)
//   Gcore  world+0x8C: header +0..+0x1C, cells (u16), body table +0x39C..+0x79C, passability maps (+0x7B0), sizes +0x7A0..+0x7AF
//   Gtiles the 10 high-cost maps (+0x7C0) in 64 x 64-cell tiles, one image per tile holding all maps
//   bnm    blockedNodeMap +0x35C (hash: key +0, byte +4, next +8), sorted (cell, count) pairs
//   grid   P's footprint grid P+0xDC (32-byte cells, records chained at +0x48), rows sorted by lot id, cell, key
//   blend  world-builder layer 0 of the terrain blend map ([WM+0x54]+0x0C, vector +0x2C), decoded bytes (value copy, §9.4)
//   tune   W+0x8C/+0x90 and the tuning block W+0x18+0x24..+0x63 (pvTune's fields)
//   lots   per-lot footprints / quadtrees: a pluggable strategy (SpLotStrategy below; default none; Lots=replay is design B of
//          docs/94 §11 in replay.cpp, audit only, docs/94 §16). The canonical lot image (SpBuildLot, what the audit compares with): key, lot
//          graph pointer, level base, built byte +0x8C, both transforms +0xA0..+0x11F, portal pointers (+0x44), add/remove
//          list sizes, per level quadtree: pointer, +0x44, every leaf entry (leaf node, FootprintMapEntry, mask, footprint,
//          footprint id). Not in it (lazy state or search outputs, §2.2 E16-E18): room records, polygon caches, Q pin
//          counter +0x48 and deferred flag +0x4C, the planner-cleared lock byte +0x8F.
// Change signals (dirty marks, any thread, spin lock): the lot leaves (W1-W6, footprint and lot remove leaves) go to the
// strategy's note; 0x82B590 / 0x82A190 mark R; 0x636BF0 / 0x62D9F0 mark the
// grid rows of their lot and the blockedNodeMap; 0x6733C0 marks the tiles of its polygon's vertex box plus one 4 m cell and
// records that box per (owner, footprint) key, which 0x670510 replays (docs/94 §9.2); a G build marks R, Gcore, all tiles
// and bnm; the blend reload job marks blend; portal / lock-status ICs (E9-E11, E24) flag every lot; world events 5, 7, 9,
// 0x0A move the epoch (full build), 0x0D-0x10 mark every lot and the whole grid.
// Cut (docs/94 §8.5): on the sim thread at a route START (P1's IC entry), at most every CutMs, holding +0x918 through the
// game's Lock_Acquire (so [RouteGate] / async bookkeeping sees it); skipped while an async job runs or is queued (P3 has no
// consumer, so it never adds a wait). Epoch moved or no version: full build. Else only the dirty images are rebuilt and the
// rest shared (dirty Gtiles only when the cut has a reader, else left out of the version: lazy tiles, docs/94 §21.9); a new
// version is published and the old one released. Not forced at the cut in P3 (no search reads the
// images yet): first use of the start/goal lots (§9.3), ApplyPendingUpdates (counted: spPendingAtCut) and the deferred
// quadtree flush (counted: spDeferredAtCut); P4 adds them with the START handshake.
// Audit (Mode=0, every AuditEvery cuts, same lock): every image is rebuilt from live memory and compared with the version
// (invariant S, docs/94 §4.2); a difference counts in spMis[input] and the first LogMismatch ones are logged with the live
// address. A mismatch on an image that was not dirty at the cut is a missed change signal (class S1).
// Door mirror (docs/105 §10.4.5), native side only: lot -> door records filled by ts3fixes over the GetOption2 channel
// (SpDoorMsg, contract in docs/94 "P3 build status"); invalidated at the world epoch, lot events and build/buy exit.

static_assert(offsetof(Ts3nCounters, spReady) == 0x20C0 && offsetof(Ts3nCounters, spMis) == 0x2138 &&
              offsetof(Ts3nCounters, spDoor) == 0x2180 && offsetof(Ts3nCounters, snSrc2) == 0x2198, "counters v47 layout (docs/registry.md)");
enum { SP_R, SP_GCORE, SP_GHC, SP_BNM, SP_GRID, SP_BLEND, SP_TUNE, SP_LOTS, SP_IN };
static const char* const kSpName[SP_IN] = {"R", "Gcore", "Gtiles", "bnm", "grid", "blend", "tune", "lots"};
// cells per tile side; tiles per side in the bitmap. 64 again (0.29.0): the 32-cell tiles of 0.28.0 rebuilt 7.5x as many
// tiles on BP (57,892 vs 7,756 in 10 min, bp-snap-p3b vs bp-snap-p3) and the cut went from 0.50 to 0.86 ms without the
// failed clone builds; the IP tile storm 32 cells were meant for came from the key map that never erased (fixed in 0.28.0)
static const int kSpTile = 64, kSpTileMax = 64;

// 0.35.15 (docs/94 §22.18): no content hash any more. The byte-serial FNV-1a over every new image was most of a tile build
// (40 KB, 51 us/tile live) and the whole cost of hashing R (~1 MB) at each R rebuild; the audit compares with memcmp
struct SpImg { volatile LONG refs; DWORD size; DWORD pad[2]; u64 key; u8 data[8]; };
struct SpVer {
    volatile LONG refs;
    LONG id, epoch;
    SpImg* w[SP_IN];  // SP_GHC and SP_LOTS unused (tiles, lots)
    int tw, th;       // tiles across / down (0 = no G maps)
    SpImg** tiles;
    int nLots;
    SpImg** lots;     // sorted by key
};

static HANDLE g_spHeap;
static volatile LONG g_spOn;          // 1 = images on (Enabled=1); dirty marks are taken only then
static volatile LONG g_spBytes;       // live image bytes
static LONG g_spCapBytes = 64 << 20;
static DWORD g_spCutMs = 250, g_spAuditEvery = 20, g_spLogMis = 32;
static SpVer* g_spCur;                // sim thread (the cut) only
static LONG g_spVerId, g_spEpoch;
static ULONGLONG g_spLastCut;
static LONG g_spLogged;
static const u8* g_spWorld;           // world of the current version

// game entry points the cut calls (thiscall; the tests point them elsewhere)
static LockAcquireFn g_spAcq = (LockAcquireFn)(uintptr_t)TS3A(0x004E16F0);
typedef LONG(__fastcall* SpRelFn)(void* lock, void* edx);
static SpRelFn g_spRel = (SpRelFn)(uintptr_t)TS3A(0x004E17B0);
static const u8* const* g_spWorldSlot = (const u8* const*)(uintptr_t)TS3A(0x011D9128);  // routing world
static const u8* const* g_spWmSlot = (const u8* const*)(uintptr_t)TS3A(0x011ECBC4);     // world manager
static const u8* SpWorld() { return *g_spWorldSlot; }

// --- images ---

static SpImg* SpImgNew(const void* p, DWORD n, u64 key) {
    if (!g_spHeap) g_spHeap = HeapCreate(0, 0, 0);
    SpImg* m = (SpImg*)HeapAlloc(g_spHeap, 0, sizeof(SpImg) + n);
    if (!m) return NULL;
    m->refs = 1;
    m->size = n;
    m->key = key;
    memcpy(m->data, p, n);
    Ts3nCounters& k = ts3native_counters;
    const LONG b = InterlockedExchangeAdd(&g_spBytes, (LONG)(sizeof(SpImg) + n)) + (LONG)(sizeof(SpImg) + n);
    k.spKB = b >> 10;
    Max(&k.spKBMax, k.spKB);
    InterlockedIncrement(&k.spImgLive);
    return m;
}
static SpImg* SpImgRef(SpImg* m) { if (m) InterlockedIncrement(&m->refs); return m; }
static bool SpImgEq(const SpImg* a, const SpImg* b) { return a && b && a->size == b->size && !memcmp(a->data, b->data, a->size); }
static void SpImgRel(SpImg* m) {
    if (!m || InterlockedDecrement(&m->refs)) return;
    ts3native_counters.spKB = (InterlockedExchangeAdd(&g_spBytes, -(LONG)(sizeof(SpImg) + m->size)) - (LONG)(sizeof(SpImg) + m->size)) >> 10;
    InterlockedDecrement(&ts3native_counters.spImgLive);
    HeapFree(g_spHeap, 0, m);
}

static inline SpVer* SpVerRef(SpVer* v) { if (v) InterlockedIncrement(&v->refs); return v; }
static void SpVerRel(SpVer* v) {  // a job (P4) holds a version with SpVerRef and lets it go here; the last one frees it
    if (!v || InterlockedDecrement(&v->refs)) return;
    for (int i = 0; i < SP_IN; i++) SpImgRel(v->w[i]);
    for (int i = 0; i < v->tw * v->th; i++) SpImgRel(v->tiles[i]);
    for (int i = 0; i < v->nLots; i++) SpImgRel(v->lots[i]);
    HeapFree(g_spHeap, 0, v->tiles);
    HeapFree(g_spHeap, 0, v->lots);
    HeapFree(g_spHeap, 0, v);
    InterlockedDecrement(&ts3native_counters.spVersionsLive);
}
static SpVer* SpVerNew() {
    if (!g_spHeap) g_spHeap = HeapCreate(0, 0, 0);
    SpVer* v = (SpVer*)HeapAlloc(g_spHeap, HEAP_ZERO_MEMORY, sizeof(SpVer));
    if (!v) return NULL;
    v->refs = 1;
    v->id = ++g_spVerId;
    v->epoch = g_spEpoch;
    InterlockedIncrement(&ts3native_counters.spVersionsLive);
    return v;
}

// growable scratch (sim thread only)
struct SpBuf { u8* p; DWORD n, cap; };
static SpBuf g_spB, g_spB2;
static bool SpPut(SpBuf& b, const void* src, DWORD n) {
    if (b.n + n > b.cap) {
        DWORD c = b.cap ? b.cap : 1 << 16;
        while (c < b.n + n) c *= 2;
        if (!g_spHeap) g_spHeap = HeapCreate(0, 0, 0);
        u8* q = (u8*)(b.p ? HeapReAlloc(g_spHeap, 0, b.p, c) : HeapAlloc(g_spHeap, 0, c));
        if (!q) return false;
        b.p = q, b.cap = c;
    }
    memcpy(b.p + b.n, src, n);
    b.n += n;
    return true;
}
static bool SpPutD(SpBuf& b, DWORD v) { return SpPut(b, &v, 4); }
static inline DWORD SpD(const u8* p) { return *(volatile const DWORD*)p; }

// --- builders: live memory -> image (NULL on a fault or a shape outside the caps; the caller keeps the old image) ---
static SpImg* SpFault() { InterlockedIncrement(&ts3native_counters.spFaults); return NULL; }

static SpImg* SpBuildR(const u8* world) {
    SpBuf& b = g_spB;
    b.n = 0;
    __try {
        const u8* r = world + 0x54;
        const DWORD w = SpD(r), h = SpD(r + 4), size = SpD(r + 0x10);
        const u8* tbl = (const u8*)(uintptr_t)SpD(r + 8);
        const u8* raw = (const u8*)(uintptr_t)SpD(r + 0x0C);
        const bool ok = raw && tbl && w && h && w <= 256 && h <= 256 && size <= (64u << 20);
        if (!SpPutD(b, ok ? w : 0) || !SpPutD(b, ok ? h : 0) || !SpPutD(b, ok ? size : 0)) return SpFault();
        if (ok && (!SpPut(b, tbl, w * h * 4) || !SpPut(b, raw, size))) return SpFault();
    } __except (EXCEPTION_EXECUTE_HANDLER) { return SpFault(); }
    return SpImgNew(b.p, b.n, 0);
}

// G sizes: w, h (cells), number of passability / high-cost maps; false = no maps
static bool SpGDims(const u8* g, DWORD* w, DWORD* h, DWORD* np, DWORD* nh) {
    __try {
        *w = SpD(g + 0x7A0), *h = SpD(g + 0x7A4), *np = SpD(g + 0x7A8), *nh = SpD(g + 0x7AC);
    } __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
    return *w && *h && *w <= 65536 && *h <= 65536 && *np <= 16 && *nh <= 16;
}
// map `m` of the vector at g+off (16-byte entries): begin, or NULL when shorter than w * h
static const u8* SpGMap(const u8* g, DWORD off, DWORD m, DWORD cells) {
    const u8* v = (const u8*)(uintptr_t)SpD(g + off);
    if (!v) return NULL;
    const u8* mb = (const u8*)(uintptr_t)SpD(v + m * 16);
    const u8* me = (const u8*)(uintptr_t)SpD(v + m * 16 + 4);
    return mb && me >= mb && (DWORD)(me - mb) >= cells ? mb : NULL;
}

static SpImg* SpBuildGCore(const u8* g) {
    SpBuf& b = g_spB;
    b.n = 0;
    __try {
        if (!SpPut(b, g, 0x1C)) return SpFault();
        const u8* cb = (const u8*)(uintptr_t)SpD(g + 0x1C);
        const u8* ce = (const u8*)(uintptr_t)SpD(g + 0x20);
        const DWORD cn = cb && ce > cb && (DWORD)(ce - cb) <= (64u << 20) ? (DWORD)(ce - cb) : 0;
        if (!SpPutD(b, cn) || (cn && !SpPut(b, cb, cn)) || !SpPut(b, g + 0x39C, 0x400) || !SpPut(b, g + 0x7A0, 0x10)) return SpFault();
        DWORD w, h, np, nh;
        if (SpGDims(g, &w, &h, &np, &nh))
            for (DWORD m = 0; m < np; m++) {
                const u8* mp = SpGMap(g, 0x7B0, m, w * h);
                if (!SpPutD(b, mp ? 1 : 0) || (mp && !SpPut(b, mp, w * h))) return SpFault();
            }
    } __except (EXCEPTION_EXECUTE_HANDLER) { return SpFault(); }
    return SpImgNew(b.p, b.n, 0);
}

static SpImg* SpBuildTile(const u8* g, int tx, int tz) {
    SpBuf& b = g_spB;
    b.n = 0;
    __try {
        DWORD w, h, np, nh;
        if (!SpGDims(g, &w, &h, &np, &nh)) return SpFault();
        const DWORD x0 = tx * kSpTile, z0 = tz * kSpTile;
        const DWORD x1 = x0 + kSpTile < w ? x0 + kSpTile : w, z1 = z0 + kSpTile < h ? z0 + kSpTile : h;
        for (DWORD m = 0; m < nh; m++) {
            const u8* mp = SpGMap(g, 0x7C0, m, w * h);
            if (!SpPutD(b, mp ? 1 : 0)) return SpFault();
            if (mp)
                for (DWORD z = z0; z < z1; z++)
                    if (!SpPut(b, mp + z * w + x0, x1 - x0)) return SpFault();
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) { return SpFault(); }
    return SpImgNew(b.p, b.n, ((u64)tz << 32) | (DWORD)tx);
}

static int __cdecl SpCmpU64(const void* a, const void* b) {
    const u64 x = *(const u64*)a, y = *(const u64*)b;
    return x < y ? -1 : x > y;
}

// LSD radix sort of n u64 (8-bit digits, a digit all keys share is skipped: bnm keys use 5 of 8); same order as qsort
// SpCmpU64 at O(n) instead of a comparator call per compare (0.35.15)
static SpBuf g_spB3, g_spB4;
static void SpSortU64(u64* a, DWORD n) {
    if (n < 2) return;
    g_spB3.n = 0;
    if (!SpPut(g_spB3, a, n * 8)) { qsort(a, n, 8, SpCmpU64); return; }  // no scratch: the old way
    u64 *src = a, *dst = (u64*)g_spB3.p;
    for (int sh = 0; sh < 64; sh += 8) {
        DWORD cnt[256] = {0};
        for (DWORD i = 0; i < n; i++) cnt[(src[i] >> sh) & 0xFF]++;
        if (cnt[(src[0] >> sh) & 0xFF] == n) continue;
        for (DWORD i = 0, s = 0; i < 256; i++) { const DWORD c = cnt[i]; cnt[i] = s; s += c; }
        for (DWORD i = 0; i < n; i++) dst[cnt[(src[i] >> sh) & 0xFF]++] = src[i];
        u64* t = src; src = dst; dst = t;
    }
    if (src != a) memcpy(a, src, n * 8);
}

static bool SpWalkBnm(const u8* g, SpBuf& b) {  // the (cell, count) pairs in hash order; false = a fault or over the cap
    b.n = 0;
    __try {
        const DWORD* bk = (const DWORD*)(uintptr_t)SpD(g + 0x360);
        const DWORD n = SpD(g + 0x364);
        if (bk && n < (1u << 24))
            for (DWORD i = 0; i < n; i++)
                for (const u8* nd = (const u8*)(uintptr_t)bk[i]; nd; nd = (const u8*)(uintptr_t)SpD(nd + 8)) {
                    const u64 e = ((u64)SpD(nd) << 32) | (nd[4]);
                    if (!SpPut(b, &e, 8) || b.n > (16u << 20)) return false;
                }
    } __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
    return true;
}
static SpImg* SpBuildBnm(const u8* g) {
    SpBuf& b = g_spB;
    if (!SpWalkBnm(g, b)) return SpFault();
    SpSortU64((u64*)b.p, b.n / 8);
    return SpImgNew(b.p, b.n, 0);
}
// 0.35.23 (docs/94 §22.26.5): the cut's bnm. A grid add / remove mark (0x636BF0 / 0x62D9F0) sets SPD_BNM, but only
// non-Sim, all-agent, shape-4 footprints reach the bnm writers 0x66FB40 / 0x66F6D0 (docs/100 §6), so most marked builds were
// equal to the old image (BP 0444: 3,097 of 4,621 "same", 1.0-1.1 ms/s). The walk in hash order is kept with the image it
// gave (g_spBnmFor); an equal walk (same bytes, same order) has the same sorted pairs, so the old image is shared without the
// sort, the image copy and the compare. *same = shared (returns NULL)
static SpBuf g_spBnmRaw;
static const SpImg* g_spBnmFor;
static SpImg* SpBnmCut(const u8* g, const SpImg* old, bool* same) {
    SpBuf& b = g_spB;
    *same = false;
    if (!SpWalkBnm(g, b)) { g_spBnmFor = NULL; return SpFault(); }
    if (old && old == g_spBnmFor && b.n == g_spBnmRaw.n && !memcmp(b.p, g_spBnmRaw.p, b.n)) return *same = true, (SpImg*)NULL;
    g_spBnmRaw.n = 0;
    const bool kept = SpPut(g_spBnmRaw, b.p, b.n) || !b.n;
    g_spBnmFor = NULL;  // set by the caller to the image this walk gave
    SpSortU64((u64*)b.p, b.n / 8);
    SpImg* img = SpImgNew(b.p, b.n, 0);
    if (!kept) g_spBnmRaw.n = 0xFFFFFFFF;  // no walk kept: never equal
    return img;
}

// grid rows: lot id first so a lot's rows are adjacent
struct SpRow { DWORD lotHi, lotLo, cell, keyHi, keyLo; float bnd[4]; DWORD ownLo, ownHi, flag, maskLo, maskHi; };
static int __cdecl SpCmpRow(const void* a, const void* b) { return memcmp(a, b, sizeof(SpRow)); }  // shortcut: byte order, stable for equality only
static bool SpRowIn(const SpRow& r, const u64* keys, int n) {
    const u64 k = ((u64)r.lotHi << 32) | r.lotLo;
    for (int i = 0; i < n; i++) if (keys[i] == k) return true;
    return false;
}
// live rows into `out` (all when keys == NULL, else only the lots in keys)
static bool SpGridLive(const u8* p, SpBuf& out, const u64* keys, int nKeys) {
    __try {
        const u8* cb = (const u8*)(uintptr_t)SpD(p + 0xDC);
        const u8* ce = (const u8*)(uintptr_t)SpD(p + 0xE0);
        if (!cb || ce <= cb) return true;
        const DWORD cells = (DWORD)(ce - cb) / 32;
        if (cells > (1u << 20)) return false;
        for (DWORD c = 0; c < cells; c++) {
            const u8* cell = cb + c * 32;
            const DWORD* bk = (const DWORD*)(uintptr_t)SpD(cell + 4);
            const DWORD nb = SpD(cell + 8);
            if (!bk || nb > (1u << 20)) continue;
            for (DWORD i = 0; i < nb; i++)
                for (const u8* rec = (const u8*)(uintptr_t)bk[i]; rec; rec = (const u8*)(uintptr_t)SpD(rec + 0x48)) {
                    SpRow r;
                    r.lotLo = SpD(rec + 0x40), r.lotHi = SpD(rec + 0x44), r.cell = c, r.keyLo = SpD(rec), r.keyHi = SpD(rec + 4);
                    for (int j = 0; j < 4; j++) r.bnd[j] = *(volatile const float*)(rec + 0x18 + 4 * j);
                    r.ownLo = SpD(rec + 0x28), r.ownHi = SpD(rec + 0x2C), r.flag = rec[0x30];
                    r.maskLo = SpD(rec + 0x38), r.maskHi = SpD(rec + 0x3C);
                    if (keys && !SpRowIn(r, keys, nKeys)) continue;
                    if (!SpPut(out, &r, sizeof(r)) || out.n > (32u << 20)) return false;
                }
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
    return true;
}
// incremental: rows of `old` for lots not in keys + live rows of the lots in keys (keys NULL = all live)
// 0.35.15: the kept rows of `old` are already in order, so only the dirty lots' live rows are sorted and the two merged
// (the whole image was sorted at every grid cut before)
static SpImg* SpBuildGrid(const u8* p, const SpImg* old, const u64* keys, int nKeys) {
    SpBuf& b = g_spB;
    b.n = 0;
    if (!keys || !old) {
        if (!SpGridLive(p, b, NULL, 0)) return SpFault();
        qsort(b.p, b.n / sizeof(SpRow), sizeof(SpRow), SpCmpRow);
        return SpImgNew(b.p, b.n, 0);
    }
    SpBuf& f = g_spB4;
    f.n = 0;
    if (!SpGridLive(p, f, keys, nKeys)) return SpFault();
    const DWORD nf = f.n / sizeof(SpRow);
    qsort(f.p, nf, sizeof(SpRow), SpCmpRow);
    const SpRow* fr = (const SpRow*)f.p;
    DWORD j = 0;
    for (DWORD o = 0; o + sizeof(SpRow) <= old->size; o += sizeof(SpRow)) {
        const SpRow& r = *(const SpRow*)(old->data + o);
        if (SpRowIn(r, keys, nKeys)) continue;
        for (; j < nf && SpCmpRow(&fr[j], &r) < 0; j++) if (!SpPut(b, &fr[j], sizeof(SpRow))) return SpFault();
        if (!SpPut(b, &r, sizeof(SpRow))) return SpFault();
    }
    if (j < nf && !SpPut(b, &fr[j], (nf - j) * sizeof(SpRow))) return SpFault();
    return SpImgNew(b.p, b.n, 0);
}

static SpImg* SpBuildBlend(const u8* wm) {
    SpBuf& b = g_spB;
    b.n = 0;
    __try {
        const u8* t = wm ? (const u8*)(uintptr_t)SpD(wm + 0x54) : NULL;
        const u8* bm = t ? (const u8*)(uintptr_t)SpD(t + 0x0C) : NULL;
        const u8* vb = bm ? (const u8*)(uintptr_t)SpD(bm + 0x2C) : NULL;
        const u8* ve = bm ? (const u8*)(uintptr_t)SpD(bm + 0x30) : NULL;
        const u8* layer = vb && ve > vb ? (const u8*)(uintptr_t)SpD(vb) : NULL;  // 0xC22050 / 0xC21AE0
        const DWORD w = layer ? SpD(bm + 8) : 0, h = layer ? SpD(bm + 0x0C) : 0, per = layer ? SpD(bm + 0x10) : 0;
        const DWORD* blocks = layer ? (const DWORD*)(uintptr_t)SpD(layer) : NULL;
        const DWORD nBlocks = layer ? SpD(layer + 4) : 0;
        const bool ok = blocks && w && h && w <= 8192 && h <= 8192 && per >= (w + 15) / 16;
        if (!SpPutD(b, ok ? w : 0) || !SpPutD(b, ok ? h : 0)) return SpFault();
        if (ok) {
            u8 row[8192];
            for (DWORD z = 0; z < h; z++) {
                for (DWORD x = 0; x < w; x++) {
                    const DWORD bi = (z >> 4) * per + (x >> 4);
                    const DWORD v = bi < nBlocks ? blocks[bi] : 0;
                    row[x] = v <= 0xFF ? (u8)v : ((const u8*)(uintptr_t)v)[(z & 15) * 16 + (x & 15)];
                }
                if (!SpPut(b, row, w)) return SpFault();
            }
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) { return SpFault(); }
    return SpImgNew(b.p, b.n, 0);
}

static SpImg* SpBuildTune(const u8* world) {
    u8 t[0x48];
    memset(t, 0, sizeof(t));
    __try {
        const u8* W = (const u8*)(uintptr_t)SpD(world + 0x960);
        if (W) { memcpy(t, (const void*)(W + 0x8C), 8); memcpy(t + 8, (const void*)(W + 0x18 + 0x24), 0x40); }
    } __except (EXCEPTION_EXECUTE_HANDLER) { return SpFault(); }
    return SpImgNew(t, sizeof(t), 0);
}

// lot image header; then nPortals portal pointers, then per Q: {Q, +0x44, n} and n entries
struct SpLotHdr { DWORD keyLo, keyHi, graph, levelBase, built, nPortals, nAdd, nRem, nQ; float xf[32]; };
struct SpEnt { DWORD node, fme, maskLo, maskHi, fp, idLo, idHi; };
static bool SpWalkQ(SpBuf& b, const u8* node, int depth, DWORD* n) {
    if (!node) return true;
    if (depth > 32) return false;
    if (node[4] & 1) {  // inner node: four children +0xC..+0x18 (CollectChildren 0x669B90)
        for (int i = 0; i < 4; i++)
            if (!SpWalkQ(b, (const u8*)(uintptr_t)SpD(node + 0xC + 4 * i), depth + 1, n)) return false;
        return true;
    }
    const u8* l = node + 8;  // leaf list: inline entry, or heap array (bit 0) with count +0xC (CollectLeaf 0x669850)
    const bool heap = (l[0] & 1) != 0;
    const DWORD cnt = heap ? SpD(l + 4) : 1;
    if (cnt > 65536) return false;
    const u8* e = heap ? (const u8*)(uintptr_t)(SpD(l) & ~1u) : l;
    for (DWORD i = 0; i < cnt; i++, e += 12) {
        const u8* fme = (const u8*)(uintptr_t)SpD(e);
        if (!fme) continue;
        const u8* fp = (const u8*)(uintptr_t)SpD(fme + 8);
        SpEnt en = {(DWORD)(uintptr_t)node, (DWORD)(uintptr_t)fme, SpD(e + 4), SpD(e + 8), (DWORD)(uintptr_t)fp, fp ? SpD(fp + 0x14) : 0,
                    fp ? SpD(fp + 0x18) : 0};
        if (!SpPut(b, &en, sizeof(en))) return false;
        if (++*n > (1u << 18)) return false;
    }
    return true;
}
static SpImg* SpBuildLot(const u8* tnode) {
    SpBuf& b = g_spB;
    b.n = 0;
    SpLotHdr hd;
    __try {
        const u8* g = tnode + 0x10;
        hd.keyLo = SpD(tnode), hd.keyHi = SpD(tnode + 4), hd.graph = (DWORD)(uintptr_t)g, hd.levelBase = SpD(g + 0x40), hd.built = g[0x8C];
        const DWORD* pb = (const DWORD*)(uintptr_t)SpD(g + 0x44);
        const DWORD* pe = (const DWORD*)(uintptr_t)SpD(g + 0x48);
        hd.nPortals = pb && pe > pb && pe - pb < 65536 ? (DWORD)(pe - pb) : 0;
        hd.nAdd = (SpD(g + 0x58) - SpD(g + 0x54)) / 4;
        hd.nRem = (SpD(g + 0x68) - SpD(g + 0x64)) / 4;
        const DWORD* qb = (const DWORD*)(uintptr_t)SpD(g + 0x74);
        const DWORD* qe = (const DWORD*)(uintptr_t)SpD(g + 0x78);
        hd.nQ = qb && qe > qb && qe - qb <= 64 ? (DWORD)(qe - qb) : 0;
        memcpy(hd.xf, (const void*)(g + 0xA0), sizeof(hd.xf));
        if (!SpPut(b, &hd, sizeof(hd)) || (hd.nPortals && !SpPut(b, pb, hd.nPortals * 4))) return SpFault();
        for (DWORD i = 0; i < hd.nQ; i++) {
            const u8* q = (const u8*)(uintptr_t)qb[i];
            const DWORD at = b.n;
            DWORD qh[3] = {(DWORD)(uintptr_t)q, q ? SpD(q + 0x44) : 0, 0};
            if (!SpPut(b, qh, sizeof(qh))) return SpFault();
            if (q && !SpWalkQ(b, (const u8*)(uintptr_t)SpD(q + 0x24), 0, &qh[2])) return SpFault();
            memcpy(b.p + at + 8, &qh[2], 4);
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) { return SpFault(); }
    return SpImgNew(b.p, b.n, ((u64)hd.keyHi << 32) | hd.keyLo);
}

// lot table P+0x48: buckets +4, count +8, node key +0, next +0x190; out = (key, node) pairs sorted by key
struct SpLotRef { u64 key; const u8* node; DWORD pad; };
static int __cdecl SpCmpLotRef(const void* a, const void* b) { return SpCmpU64(a, b); }
static int SpLotTable(const u8* p, SpBuf& out) {
    out.n = 0;
    __try {
        const DWORD* bk = (const DWORD*)(uintptr_t)SpD(p + 0x4C);
        const DWORD n = SpD(p + 0x50);
        if (!bk || n > (1u << 20)) return 0;
        const DWORD sentinel = bk[n];
        for (DWORD i = 0; i < n; i++)
            for (const u8* nd = (const u8*)(uintptr_t)bk[i]; nd && (DWORD)(uintptr_t)nd != sentinel; nd = (const u8*)(uintptr_t)SpD(nd + 0x190)) {
                SpLotRef r = {((u64)SpD(nd + 4) << 32) | SpD(nd), nd, 0};
                if (!SpPut(out, &r, sizeof(r)) || out.n > 4096 * sizeof(r)) return -1;
            }
    } __except (EXCEPTION_EXECUTE_HANDLER) { SpFault(); return -1; }
    qsort(out.p, out.n / sizeof(SpLotRef), sizeof(SpLotRef), SpCmpLotRef);
    return (int)(out.n / sizeof(SpLotRef));
}

// --- dirty marks (SpNote: any thread) ---
enum { SPD_EPOCH = 1, SPD_R = 2, SPD_G = 4, SPD_BNM = 8, SPD_BLEND = 16, SPD_LOTS = 32, SPD_GRID = 64, SPD_TILES = 128 };
static const int kSpSet = 64, kSpKeys = 16384, kSpProbe = 64;  // key map: IP's P3 run filled 4,096 x 16 (stale keys, no erase)
struct SpKeyBox { u64 a, b; short x0, z0, x1, z1; };  // cells; x0 > x1 = free slot: x0 == 1 empty, x0 == 2 erased (tombstone)
struct SpDirty {
    DWORD flags;
    int nPtr, nLot, nGrid;
    DWORD ptr[kSpSet];   // Q (W1/W2) and lot graph (W4) pointers
    u64 lot[kSpSet];     // lot ids
    u64 grid[kSpSet];    // grid lot ids
    LONG tiles[kSpTileMax * kSpTileMax / 32];
};
static SpDirty g_spD;
static SpKeyBox g_spKeys[kSpKeys];
static volatile LONG g_spLock;
static void SpLockD() { while (InterlockedExchange(&g_spLock, 1)) YieldProcessor(); }
static void SpUnlockD() { InterlockedExchange(&g_spLock, 0); }

static void SpAddPtr(DWORD p) {
    for (int i = 0; i < g_spD.nPtr; i++) if (g_spD.ptr[i] == p) return;
    if (g_spD.nPtr < kSpSet) g_spD.ptr[g_spD.nPtr++] = p; else { g_spD.flags |= SPD_LOTS; ts3native_counters.spDirtyOver++; }
}
static void SpAddKey(u64* set, int* n, DWORD all, u64 k) {
    for (int i = 0; i < *n; i++) if (set[i] == k) return;
    if (*n < kSpSet) set[(*n)++] = k; else { g_spD.flags |= all; ts3native_counters.spDirtyOver++; }
}
static void SpMarkBox(int x0, int z0, int x1, int z1) {  // cells, inclusive
    if (x1 < 0 || z1 < 0) return;
    if (x0 < 0) x0 = 0;
    if (z0 < 0) z0 = 0;
    for (int tz = z0 / kSpTile; tz <= z1 / kSpTile; tz++)
        for (int tx = x0 / kSpTile; tx <= x1 / kSpTile; tx++) {
            if (tx >= kSpTileMax || tz >= kSpTileMax) { g_spD.flags |= SPD_TILES; return; }
            const int t = tz * kSpTileMax + tx;
            g_spD.tiles[t >> 5] |= 1 << (t & 31);
        }
}
static int SpCellOf(float v) { return v >= 0 && v < 1e7f ? (int)v >> 2 : -1; }  // 0x670750: trunc(v) >> 2, negative fails
static SpKeyBox* SpKeySlot(u64 a, u64 b, bool add) {
    DWORD h = (DWORD)(a ^ (a >> 32) ^ b * 0x9E3779B1u ^ (b >> 32)) % kSpKeys;
    SpKeyBox* freeSlot = NULL;
    for (int i = 0; i < kSpProbe; i++, h = (h + 1) % kSpKeys) {
        SpKeyBox& e = g_spKeys[h];
        if (e.x0 <= e.x1) { if (e.a == a && e.b == b) return &e; continue; }
        if (!freeSlot) freeSlot = &e;
        if (e.x0 == 1) break;  // empty: the key is not further on
    }
    if (!add || !freeSlot) return NULL;
    freeSlot->a = a, freeSlot->b = b, freeSlot->x0 = 0x7FFF, freeSlot->z0 = 0x7FFF, freeSlot->x1 = -1, freeSlot->z1 = -1;  // filled by the caller
    return freeSlot;
}
static void SpKeyErase(SpKeyBox* e) { e->x0 = 2, e->x1 = 0; }  // 0x670510 cleared the key's cells: its record goes too
static void SpKeysClear() { for (int i = 0; i < kSpKeys; i++) { g_spKeys[i].x0 = 1; g_spKeys[i].x1 = 0; } }

// --- per-lot footprint / quadtree images: a pluggable strategy (docs/94 §11 design, §14 status) ---
// The P1 live run (BP: ~1,600 writer calls/s, W6 26k/min, footprint removes 20k/min, quadtree applies 4-6k/min each, dirty
// lots per START p95 6-10) rules out copying a whole lot on every change; the design (delta log or per-plan copy of the
// touched lots) is decided separately. The interface:
//   note(site, slot)   writer-layer mark for the lot leaves (kSn index; any thread, under the dirty lock): W1-W6, the
//                      footprint leaves and the lot remove leaves
//   begin(v, old, world, d, lots, n)
//                      at the cut (sim, +0x918 held): give version v the lot images it carries for the lot set
//                      (lots == NULL: every lot in the table; P4 passes the request's lots); old = previous version or NULL
//   image(v, key)      the carried image of one lot in the canonical SpBuildLot format, NULL = not carried
// v->lots / v->nLots (sorted by key) hold whatever begin built; SpVerRel releases them. The audit compares image() with a
// fresh SpBuildLot of the live lot, so any strategy is checked by the same scaffolding. Live default: none (no lot images).
struct SpLotStrategy {
    const char* name;
    void (*note)(int site, const DWORD* slot);
    void (*begin)(SpVer* v, const SpVer* old, const u8* world, const SpDirty& d, const u64* lots, int n);
    const SpImg* (*image)(const SpVer* v, u64 key);
    void (*pre)(int site, const DWORD* slot);  // every P1 site, any thread, before the dirty lock (replay.cpp's recorder)
};
static const SpLotStrategy kSpLotNone = {"none", NULL, NULL, NULL};
static const SpLotStrategy* g_spLot = &kSpLotNone;

// 0x6733C0 (thiscall G; args 1-4 key, arg 5 the polygon's point vector of (x, z) floats): vertex box + one cell
static void SpMarkPolygon(const DWORD* slot) {
    const float* pb = (const float*)(uintptr_t)SpD((const u8*)(uintptr_t)slot[5]);
    const float* pe = (const float*)(uintptr_t)SpD((const u8*)(uintptr_t)slot[5] + 4);
    if (!pb || pe <= pb) return;
    int x0 = 0x7FFF, z0 = 0x7FFF, x1 = -1, z1 = -1;
    bool neg = false;
    for (const float* p = pb; p + 1 < pe; p += 2) {
        const int x = SpCellOf(p[0]), z = SpCellOf(p[1]);
        if (x < 0 || z < 0) neg = true;
        if (x >= 0) { x0 = x < x0 ? x : x0; x1 = x > x1 ? x : x1; }
        if (z >= 0) { z0 = z < z0 ? z : z0; z1 = z > z1 ? z : z1; }
    }
    if (neg) x0 = z0 = 0;  // a segment from a negative end point may still write cells from 0 (the rasteriser clips)
    if (x1 < 0 || z1 < 0) return;
    x0--, z0--, x1++, z1++;
    SpMarkBox(x0, z0, x1, z1);
    const u64 a = ((u64)slot[2] << 32) | slot[1], b = ((u64)slot[4] << 32) | slot[3];
    SpKeyBox* e = SpKeySlot(a, b, true);
    if (!e) { ts3native_counters.spKeyMapFull++; return; }  // its remove then marks every tile
    if (x0 < e->x0) e->x0 = (short)(x0 < 0 ? 0 : x0);
    if (z0 < e->z0) e->z0 = (short)(z0 < 0 ? 0 : z0);
    if (x1 > e->x1) e->x1 = (short)(x1 > 0x7FFE ? 0x7FFE : x1);
    if (z1 > e->z1) e->z1 = (short)(z1 > 0x7FFE ? 0x7FFE : z1);
}

// recorded routing writes (writer leaves, lot / epoch world events) on any thread but a clone run or the replay consumer
// (0.33.2, docs/94 §21.15): P4 compares the count at the cut with the count when the job's live plan starts; a difference
// means the live plan saw writes the clone at the cut does not have ("stale": a mismatch then is M5, not M1-M3)
static volatile LONG g_spWrites;
// 0.35.21 (docs/94 §22.24.4): noise-resistant cost. Live runs share the PC, so wall-clock us include preemption; the timed
// sections also sum their thread CPU cycles (QueryThreadCycleTime on the measuring thread), logged next to the wall ms/s as
// cycle-ms/s at a fixed nominal clock so both compare with earlier ms/s. Log lines only, no counters
static const double kCycHz = 2.3e9;  // i7-11800H base clock; the cycle count ticks at the invariant TSC rate [inferred], so this is run time
static inline LONG64 CycNow() { ULONG64 c = 0; QueryThreadCycleTime(GetCurrentThread(), &c); return (LONG64)c; }
static double CycMsPerS(LONG64 cyc, double secs) { return secs > 0 ? cyc / (kCycHz / 1000.0) / secs : 0.0; }
static volatile LONG64 g_cyCut, g_cyCopy, g_cyRebase, g_cyApply, g_cyRpAudit;  // sim cut, copies (any thread), rebases, consumer holds, replay audit
// 0.36.59 (docs/189 phase 2, [RouteSnap] StaleLot, stalelot.cpp): the write ring. Every g_spWrites increment SpNote makes
// (always under SpLockD, so ring writers are serialized) also stores (seq, lot key or world-level kind) in slot seq & mask.
// Seqlock per slot: seq = 0, then the fields, then seq = s; a reader takes a slot only if seq reads s before and after the
// fields. An increment anywhere else (replay.cpp lock-key cover, fault injection, tests) leaves its slot without its seq:
// readers see "unknown" and treat the job as stale (conservative by construction).
enum { SLW_LOT, SLW_EPOCH, SLW_LOTEV, SLW_R, SLW_GRID, SLW_TILES, SLW_GBUILD, SLW_FP, SLW_W6, SLW_TABLE, SLW_LOT0, SLW_NOKEY, SLW_N };
// 0.36.62 (docs/189 §9, [RouteSnap] StaleLotFp): flag on a world-level kind = "covered": the entry changes no routing input
// of its own, its effects are the nested leaves' own ring entries (W1 / W2 by Q lot, LOTREMOVEFP by lot, grid add, high-cost
// marks): W6 polygon caches, the footprint-object leaves FPCORE / PRIVATE / FPREMOVE, and a grid remove whose key no live
// grid record holds. The FpLots verdict skips covered entries; the phase-2 verdict (kind & 0xFF) keeps them world-level
static const LONG SLW_COV = 0x100;
static const LONG kStlRing = 1024;  // ~0.5 s at the measured ~1,600-1,900 writes/s; a wider window is stale (overflow)
struct StlEnt { volatile LONG seq; LONG kind; DWORD lo, hi; };
static StlEnt g_stlRing[kStlRing];
static volatile LONG g_stlOn;  // ring kept ([RouteSnap] StaleLot != 0 at load)

// [RouteSnap] StaleLotFp grid set (SpLockD held): the canonical keys min(k, ~k) of every record in the live footprint grid
// P+0xDC (docs/107 §10: 32-byte cells, buckets +4, count +8, records chained at +0x48, key +0/+4), re-read at a grid remove
// when a grid add, a matched remove, an epoch / lot event or another planner made it old. 0x62D9F0(lotId, key) erases only
// records whose key is key or ~key (and whose lot id is lotId) [verified, docs/107 §10.2]; a remove whose canonical key is
// not in the set finds nothing and writes nothing. Lot ids are ignored here: a superset, so never a missed remove. Every grid
// insert is 0x636BF0 (docs/156 row 5), a hooked leaf, and every walk runs at a remove's entry under +0x918, so no grid writer
// runs during it. Off (fallback world-level) on: set over its cap, a faulting walk, the tripwire below
static const int kFplGrid = 8192;
static u64 g_fplGrid[kFplGrid];
static int g_fplGridN = -1;           // -1 = old: re-read at the next remove; -2 = over the cap until the next add
static DWORD g_fplGridP;              // the planner the set was read from
static volatile LONG g_fplOn;         // ini StaleLotFp: grid check on
static volatile LONG g_fplGridOff;    // tripwire: a no-op-judged remove reached 0x670510
static __declspec(thread) bool t_fplNoop;  // this thread's last leaf was a grid remove judged a no-op
static int __cdecl FplCmp(const void* a, const void* b) { const u64 x = *(const u64*)a, y = *(const u64*)b; return x < y ? -1 : x > y; }
static inline u64 FplCanon(u64 k) { return k < ~k ? k : ~k; }
static bool FplGridRead(const u8* p) {
    int n = 0;
    __try {
        const u8* cb = (const u8*)(uintptr_t)SpD(p + 0xDC);
        const u8* ce = (const u8*)(uintptr_t)SpD(p + 0xE0);
        const DWORD cells = cb && ce > cb ? (DWORD)(ce - cb) / 32 : 0;
        if (cells > (1u << 20)) return false;
        for (DWORD c = 0; c < cells; c++) {
            const u8* cell = cb + c * 32;
            const DWORD* bk = (const DWORD*)(uintptr_t)SpD(cell + 4);
            const DWORD nb = SpD(cell + 8);
            if (nb > (1u << 20) || (!bk && nb)) return false;  // a cell the walk cannot read: no set (fallback, world-level), never a subset
            if (!bk) continue;
            for (DWORD i = 0; i < nb; i++)
                for (const u8* rec = (const u8*)(uintptr_t)bk[i]; rec; rec = (const u8*)(uintptr_t)SpD(rec + 0x48)) {
                    if (n == kFplGrid) { g_fplGridN = -2; return true; }
                    g_fplGrid[n++] = FplCanon(((u64)SpD(rec + 4) << 32) | SpD(rec));
                }
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
    qsort(g_fplGrid, n, sizeof(u64), FplCmp);
    g_fplGridN = n;
    return true;
}
// grid remove 0x62D9F0 (thiscall P; args 1-2 lot id, 3-4 key): 0 = no live record has this key (covered), 1 = a real
// remove, 2 = not judged (off, fallback: world-level)
static int FplGridNoop(const DWORD* slot) {
    Ts3nCounters& k = ts3native_counters;
    if (!g_fplOn) return 2;
    if (g_fplGridOff) { k.fplFb[2]++; return 2; }
    const DWORD p = slot[-2];  // ecx = P
    if (!p) { k.fplFb[1]++; return 2; }
    if (p != g_fplGridP && g_fplGridN != -2) g_fplGridN = -1;
    if (g_fplGridN == -1) {
        g_fplGridP = p;
        k.fplGridReads++;
        if (!FplGridRead((const u8*)(uintptr_t)p)) { g_fplGridN = -1; k.fplFb[1]++; return 2; }
        if (g_fplGridN >= 0) k.fplGridRecs = g_fplGridN;
    }
    if (g_fplGridN < 0 || p != g_fplGridP) { k.fplFb[0]++; return 2; }
    const u64 c = FplCanon(((u64)slot[4] << 32) | slot[3]);
    if (bsearch(&c, g_fplGrid, g_fplGridN, sizeof(u64), FplCmp)) { g_fplGridN = -1; return 1; }  // a real remove: re-read next time
    return 0;
}

static void SpWrite(int kind, unsigned __int64 key) {  // SpLockD held
    const LONG s = InterlockedIncrement(&g_spWrites);
    if (!g_stlOn) return;
    if (kind == SLW_LOT && !key) kind = SLW_LOT0;  // open ground / lot 0: world-level (Q-less map nodes, no lot to name)
    StlEnt& e = g_stlRing[s & (kStlRing - 1)];
    InterlockedExchange(&e.seq, 0);
    e.kind = kind, e.lo = (DWORD)key, e.hi = (DWORD)(key >> 32);
    InterlockedExchange(&e.seq, s);
    InterlockedIncrement(&ts3native_counters.stlWrites[kind == SLW_LOT ? 0 : 1]);
}
// a writer leaf's lot (lot-keyed kinds) or its world-level kind. Lot-keyed only where the lot is an argument or the Q's own
// key: W1 / W2 Q+0x18/+0x1C (as replay.cpp RpQKey), W3 args 1-2, W4 the lot graph's key +0/+4, LOTREMOVEFP args 1-2.
// Everything else changes state outside one lot's Q / graph (R, grid, blocked-node map, tiles, footprint objects and their
// polygon caches, the lot table): world-level, as the global rule
static int StlClassify(const SnSite& s, const DWORD* slot, unsigned __int64* key) {
    *key = 0;
    __try {
        switch (s.leaf) {
            case SL_W1: case SL_W2: {
                const u8* q = (const u8*)(uintptr_t)slot[-2];
                if (q) *key = ((unsigned __int64)SpD(q + 0x1C) << 32) | SpD(q + 0x18);
                return SLW_LOT;
            }
            case SL_W3: *key = ((unsigned __int64)slot[2] << 32) | slot[1]; return SLW_LOT;
            case SL_W4: {
                const u8* g = (const u8*)(uintptr_t)slot[1];
                if (g) *key = ((unsigned __int64)SpD(g + 4) << 32) | SpD(g);
                return SLW_LOT;
            }
            case SL_LOTREMOVEFP: return SnLot(s, slot, key) ? SLW_LOT : SLW_NOKEY;
            case SL_W5: case SL_REMOVELOT: return SLW_TABLE;
            // covered (SLW_COV, docs/189 §9): no routing input of their own; nested leaves carry the lot or world kind
            case SL_W6:
                // integration review (0.36.63): W6g (0x80CB40 / 0x806770, DoorRouting_AddDoorPortals) creates door portal
                // objects lazily [docs/105]: a routing input with no proven nested lot-keyed leaf, so it stays world-level
                if (s.at == TS3A(0x0080CB40) || s.at == TS3A(0x00806770)) return SLW_W6;
                ts3native_counters.fplCov[0]++;
                return SLW_W6 | SLW_COV;
            case SL_FPCORE: case SL_PRIVATE: case SL_FPREMOVE:
                ts3native_counters.fplCov[s.leaf == SL_FPCORE ? 1 : s.leaf == SL_PRIVATE ? 2 : 3]++;
                return SLW_FP | SLW_COV;
            case SL_APPLYLOT: case SL_RESTORE: return SLW_R;
            case SL_GRIDADD: g_fplGridN = -1; return SLW_GRID;  // the set is old after an add (an over-cap set is re-tried)
            case SL_GRIDREMOVE: {
                const int g = FplGridNoop(slot);
                t_fplNoop = g == 0;
                ts3native_counters.fplGrid[g]++;
                return g == 0 ? SLW_GRID | SLW_COV : SLW_GRID;
            }
            case SL_MARKREMOVE:  // inside 0x62D9F0 once per record it found: a no-op-judged remove got here = the key rule is wrong
                if (t_fplNoop && !InterlockedExchange(&g_fplGridOff, 1)) {
                    ts3native_counters.fplGridTrip++;
                    Log("RouteSnap StaleLotFp: a grid remove judged a no-op reached 0x670510: grid check off for the session (docs/189 §9)");
                }
                return SLW_TILES;
            case SL_HIGHCOST: return SLW_TILES;
            case SL_GBUILD: case SL_BLEND: return SLW_GBUILD;
            default: return SLW_NOKEY;
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) { *key = 0; return SLW_NOKEY; }
}
// from SnEnter (routesnap.cpp), every thread, before P1's own counting returns
static void SpNote(int i, const DWORD* slot) {
    if (!g_spOn) return;
    if (g_spLot->pre) g_spLot->pre(i, slot);
    const SnSite& s = kSn[i];
    SpLockD();
    __try {
        if (i == SN_WEV) {
            const DWORD id = slot[2];
            if (id == 5 || id == 7 || id == 9 || id == 0x0A) g_spD.flags |= SPD_EPOCH, SpWrite(SLW_EPOCH, 0);
            else if (id >= 0x0D && id <= 0x10) g_spD.flags |= SPD_LOTS | SPD_GRID, SpWrite(SLW_LOTEV, 0);
            if ((id == 5 || id == 7 || id == 9 || (id >= 0x0A && id <= 0x10))) g_fplGridN = -1;
        } else if (i != SN_FPEN) {
            if (s.leaf != SL_SRC) {
                // the no-op flag lives from the grid remove to the next grid add / remove: a nested leaf inside 0x62D9F0
                // (high cost, a W6 fill) before its 0x670510 must not blind the tripwire (integration review, 0.36.63)
                if (s.leaf == SL_GRIDADD) t_fplNoop = false;
                unsigned __int64 lk;
                const int kind = StlClassify(s, slot, &lk);
                SpWrite(kind, lk);
            }
            unsigned __int64 key;
            switch (s.leaf) {
                case SL_W1: case SL_W2: case SL_W3: case SL_W4: case SL_W5: case SL_W6: case SL_FPCORE: case SL_PRIVATE:
                case SL_FPREMOVE: case SL_LOTREMOVEFP: case SL_REMOVELOT:
                    if (g_spLot->note) g_spLot->note(i, slot);  // per-lot footprints / quadtrees: the strategy's business
                    break;
                case SL_APPLYLOT: case SL_RESTORE: g_spD.flags |= SPD_R; break;
                case SL_GRIDADD: case SL_GRIDREMOVE:
                    g_spD.flags |= SPD_BNM;
                    if (SnLot(s, slot, &key)) SpAddKey(g_spD.grid, &g_spD.nGrid, SPD_GRID, key); else g_spD.flags |= SPD_GRID;
                    break;
                case SL_HIGHCOST: SpMarkPolygon(slot); break;
                case SL_MARKREMOVE: {
                    SpKeyBox* e = SpKeySlot(((u64)slot[2] << 32) | slot[1], ((u64)slot[4] << 32) | slot[3], false);
                    if (e) { SpMarkBox(e->x0, e->z0, e->x1, e->z1); SpKeyErase(e); } else g_spD.flags |= SPD_TILES;  // no record: every tile
                    break;
                }
                case SL_GBUILD: g_spD.flags |= SPD_R | SPD_G | SPD_TILES | SPD_BNM; break;
                case SL_BLEND: if (slot[3] == 4) g_spD.flags |= SPD_BLEND; break;
                default: break;
            }
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) { g_spD.flags |= SPD_LOTS | SPD_GRID | SPD_TILES; ts3native_counters.spFaults++; }
    SpUnlockD();
}

static void SpDoorInvalidateAll();
// from SnIcEnter: portal and lock-status ICs (E9-E11, E24, portal handle destroy) re-image every lot; build/buy exit and
// the door lock event invalidate the door mirror
static volatile LONG g_spKeysDirty = 1;  // a lock-key IC ran (slots 0-3) or a new epoch: P4 re-copies the lock-key sets at its next cut
static volatile LONG g_spIcN;  // routing ICs seen (any): the replay's quiet-cut test (0.35.21)
static void SpIcNote(int i) {
    InterlockedIncrement(&g_spIcN);
    if (i <= 3) g_spKeysDirty = 1;
    if (!g_spOn) return;
    if (i >= 4 && i <= 16) { SpLockD(); g_spD.flags |= SPD_LOTS; SpUnlockD(); }  // what that costs is the strategy's choice
    if (i == 22) SpDoorInvalidateAll();
}

static SpImg* SpKeep(SpImg* fresh, SpImg* old) { return fresh ? fresh : SpImgRef(old); }  // a fault keeps the old image


// Reference strategy "copy the whole lot when marked" (tests only: it exercises the interface, the dirty marks and the
// audit; not a design candidate, see above). Marks: Q pointer (W1/W2 ecx), lot graph (W4 arg 1), lot id (W3, remove leaves).
static void SpRefNote(int site, const DWORD* slot) {
    const SnSite& s = kSn[site];
    unsigned __int64 key;
    switch (s.leaf) {
        case SL_W1: case SL_W2: SpAddPtr(slot[-2]); break;  // ecx = Q
        case SL_W4: SpAddPtr(slot[1]); break;               // arg 1 = the lot graph (cmp [ebx+0x8C] at 0x63CB98)
        case SL_W3: SpAddKey(g_spD.lot, &g_spD.nLot, SPD_LOTS, ((u64)slot[2] << 32) | slot[1]); break;  // lot id, args 1-2
        case SL_LOTREMOVEFP: case SL_REMOVELOT: if (SnLot(s, slot, &key)) SpAddKey(g_spD.lot, &g_spD.nLot, SPD_LOTS, key); break;
        default: break;  // W5 (a new lot: the table walk sees it), W6 (polygon caches: not imaged), footprint leaves (reach W1/W2)
    }
}

static SpImg* SpOldLot(const SpVer* v, u64 key) {
    int lo = 0, hi = v ? v->nLots - 1 : -1;
    while (lo <= hi) {
        const int m = (lo + hi) / 2;
        const u64 k = v->lots[m]->key;
        if (k == key) return v->lots[m];
        if (k < key) lo = m + 1; else hi = m - 1;
    }
    return NULL;
}
static bool SpLotDirty(const SpImg* m, const SpDirty& d) {
    if (d.flags & (SPD_LOTS | SPD_EPOCH)) return true;
    for (int i = 0; i < d.nLot; i++) if (d.lot[i] == m->key) return true;
    if (!d.nPtr) return false;
    const SpLotHdr* h = (const SpLotHdr*)m->data;
    for (int i = 0; i < d.nPtr; i++) if (d.ptr[i] == h->graph) return true;
    const u8* p = m->data + sizeof(SpLotHdr) + h->nPortals * 4;
    for (DWORD q = 0; q < h->nQ; q++) {
        const DWORD* qh = (const DWORD*)p;
        for (int i = 0; i < d.nPtr; i++) if (d.ptr[i] == qh[0]) return true;
        p += 12 + qh[2] * sizeof(SpEnt);
    }
    return false;
}

static void SpRefBegin(SpVer* v, const SpVer* old, const u8* world, const SpDirty& d, const u64*, int) {
    const u8* p = (const u8*)(uintptr_t)SpD(world + 0x95C);
    const int n = p ? SpLotTable(p, g_spB2) : 0;
    if (n <= 0 || !(v->lots = (SpImg**)HeapAlloc(g_spHeap, HEAP_ZERO_MEMORY, sizeof(SpImg*) * n))) return;
    const SpLotRef* refs = (const SpLotRef*)g_spB2.p;
    for (int i = 0; i < n; i++) {
        SpImg* o = old ? SpOldLot(old, refs[i].key) : NULL;
        SpImg* m;
        if (o && ((const SpLotHdr*)o->data)->graph == (DWORD)(uintptr_t)(refs[i].node + 0x10) && !SpLotDirty(o, d)) m = SpImgRef(o);
        else { m = SpKeep(SpBuildLot(refs[i].node), o); ts3native_counters.spCopies[SP_LOTS]++; }
        if (m) v->lots[v->nLots++] = m;  // unbuilt lots are imaged as they are (forced first use: P4)
    }
}
static const SpImg* SpRefImage(const SpVer* v, u64 key) { return SpOldLot(v, key); }
static const SpLotStrategy kSpLotCopyRef = {"copy-ref (tests)", SpRefNote, SpRefBegin, SpRefImage};

// a new version from `old` (NULL = full) and the dirty set; the caller holds +0x918 on the sim thread.
// Lazy tiles (0.31.0, docs/94 §21.9): a dirty tile is rebuilt only when `fill` (a reader: an armed P4 cut) asks; else the
// version does not carry it (NULL) and the next cut with a reader, or the audit, builds it (SpTilesFill). A tile a version
// carries equals live at its cut: shared only when the old version carried it and no mark hit it since.
// cut time split by input (0.35.15, docs/94 §22.18; log only, no counters): sim thread only. n = builds, same = builds equal
// to the old image (shared), us = build time; SP_GHC = tiles built for a reader at the cut, SP_LOTS = the strategy's begin
// (Lots=replay: RpCut, its rebases and copies). Logged every kSpSplitMs by SpCut, then reset
struct SpSplit { LONG n, same; LONGLONG us; };
static SpSplit g_spSplit[SP_IN];
static LONGLONG g_spSplitCutUs;
static LONG g_spSplitCuts;
static ULONGLONG g_spSplitT0;
static const DWORD kSpSplitMs = 300000;
static SpVer* SpBuildVersion(const u8* world, const SpVer* old, const SpDirty& d, bool fill) {
    Ts3nCounters& k = ts3native_counters;
    const bool full = !old || (d.flags & SPD_EPOCH);
    const u8* g = world + 0x8C;
    const u8* p = (const u8*)(uintptr_t)SpD(world + 0x95C);
    const u8* wm = *g_spWmSlot;
    SpVer* v = SpVerNew();
    if (!v) return NULL;
    if (full) old = NULL;
    // world images. 0.35.15: a rebuilt image equal to the old one is dropped and the old one shared, so P4's readers that
    // build only from the image (PcR, PcBnm, keyed on the image pointer) skip their rebuild too. Not the grid: P4's grid copy
    // (PcGridCopy) reads live record bytes and polygons the image leaves out, and keys its recopy on a new grid image
    SpImg* const* ow = old ? old->w : NULL;
    LARGE_INTEGER t0;
    auto put = [&](int in, SpImg* fresh) {
        SpImg* o = ow ? ow[in] : NULL;
        const bool eq = fresh && SpImgEq(fresh, o);
        g_spSplit[in].same += eq;  // grid: counted (how often a grid mark changed nothing), never shared
        if (eq && in != SP_GRID) { SpImgRel(fresh); v->w[in] = SpImgRef(o); }
        else v->w[in] = SpKeep(fresh, o);
        k.spCopies[in]++;
        g_spSplit[in].n++, g_spSplit[in].us += UsSince(t0);
    };
    auto take = [&](int in, bool dirty, SpImg* (*build)(const u8*), const u8* arg) {
        if (!dirty && ow) { v->w[in] = SpImgRef(ow[in]); return; }
        QueryPerformanceCounter(&t0);
        put(in, build(arg));
    };
    take(SP_R, (d.flags & SPD_R) != 0, SpBuildR, world);
    take(SP_GCORE, (d.flags & SPD_G) != 0, SpBuildGCore, g);
    if (!(d.flags & SPD_BNM) && ow) v->w[SP_BNM] = SpImgRef(ow[SP_BNM]);
    else {  // 0.35.23: an equal walk shares the old image without the sort (SpBnmCut)
        QueryPerformanceCounter(&t0);
        SpImg* o = ow ? ow[SP_BNM] : NULL;
        bool same;
        SpImg* fresh = SpBnmCut(g, o, &same);
        if (same) {
            v->w[SP_BNM] = SpImgRef(o);
            g_spSplit[SP_BNM].same++, k.spCopies[SP_BNM]++, g_spSplit[SP_BNM].n++, g_spSplit[SP_BNM].us += UsSince(t0);
        } else {
            put(SP_BNM, fresh);
            if (fresh) g_spBnmFor = v->w[SP_BNM];  // fresh, or o when equal: either way the sorted walk
        }
    }
    take(SP_BLEND, (d.flags & SPD_BLEND) != 0, SpBuildBlend, wm);
    {  // tune: re-read at every cut (72 bytes). W+0x8C has a play-time writer, IC_RouteManager_SetMinimumDistanceForCarTravel
       // 0x74B500 -> 0x82A590 (P3 runs: 580/483 mismatches at +0x2), and the G-build path copies world+0x968/+0x970 at 0x829FE8
        SpImg* t = SpBuildTune(world);
        SpImg* o = ow ? ow[SP_TUNE] : NULL;
        if (SpImgEq(t, o)) { SpImgRel(t); v->w[SP_TUNE] = SpImgRef(o); }
        else { v->w[SP_TUNE] = SpKeep(t, o); k.spCopies[SP_TUNE]++; }
    }
    if (!old || (d.flags & SPD_GRID) || d.nGrid) {
        QueryPerformanceCounter(&t0);
        put(SP_GRID, p ? SpBuildGrid(p, old ? old->w[SP_GRID] : NULL, old && !(d.flags & SPD_GRID) ? d.grid : NULL, d.nGrid) : NULL);
    } else v->w[SP_GRID] = SpImgRef(old->w[SP_GRID]);
    // tiles
    DWORD w = 0, h = 0, np, nh;
    if (SpGDims(g, &w, &h, &np, &nh)) {
        v->tw = (int)((w + kSpTile - 1) / kSpTile), v->th = (int)((h + kSpTile - 1) / kSpTile);
        v->tiles = (SpImg**)HeapAlloc(g_spHeap, HEAP_ZERO_MEMORY, sizeof(SpImg*) * v->tw * v->th);
        if (!v->tiles) v->tw = v->th = 0;
        const bool same = old && old->tw == v->tw && old->th == v->th && !(d.flags & (SPD_G | SPD_TILES));
        for (int tz = 0; tz < v->th; tz++)
            for (int tx = 0; tx < v->tw; tx++) {
                const int t = tz * kSpTileMax + tx;
                const bool bit = tx < kSpTileMax && tz < kSpTileMax && (d.tiles[t >> 5] >> (t & 31) & 1);
                SpImg* o = same ? old->tiles[tz * v->tw + tx] : NULL;
                if (o && !bit) { v->tiles[tz * v->tw + tx] = SpImgRef(o); continue; }
                if (!fill) continue;  // not carried until a reader needs it
                v->tiles[tz * v->tw + tx] = SpBuildTile(g, tx, tz);  // a fault leaves it uncarried (never a stale image)
                k.spCopies[SP_GHC]++;
            }
    }
    if (g_spLot->begin) {  // per-lot images: the strategy
        QueryPerformanceCounter(&t0);
        g_spLot->begin(v, old, world, d, NULL, 0);
        g_spSplit[SP_LOTS].n++, g_spSplit[SP_LOTS].us += UsSince(t0);
    }
    k.spLots = v->nLots;
    k.spTiles = v->tw * v->th;
    k.spTileOut = 0;
    for (int i = 0; i < v->tw * v->th; i++) k.spTileOut += !v->tiles[i];
    return v;
}

// build the tiles `v` does not carry; only right after v's cut under the same +0x918 hold (live == v's cut). why: 0 a reader
// at the cut (armed P4), 1 the audit. true = v carries every tile (a fault leaves one out)
static bool SpTilesFill(SpVer* v, const u8* world, int why) {
    Ts3nCounters& k = ts3native_counters;
    LARGE_INTEGER t0;
    QueryPerformanceCounter(&t0);
    LONG out = 0;
    for (int i = 0; i < v->tw * v->th; i++) {
        if (v->tiles[i]) continue;
        if ((v->tiles[i] = SpBuildTile(world + 0x8C, i % v->tw, i / v->tw)) != NULL) k.spCopies[SP_GHC]++, k.spTileFill[why]++;
        else out++;
    }
    k.spTileOut = out;
    InterlockedExchangeAdd(&k.spTileFillUs[why], UsSince(t0));
    return !out;
}
// a reader's check: every tile carried (P4's clone copies them all)
static bool SpTilesAll(const SpVer* v) {
    if (!v) return false;
    for (int i = 0; i < v->tw * v->th; i++) if (!v->tiles[i]) return false;
    return true;
}

// address in live memory that an image offset came from (for the mismatch log; best effort)
static void SpLogMis(int in, const SpImg* img, const SpImg* live, DWORD off, const void* base) {
    if (InterlockedIncrement(&g_spLogged) > (LONG)g_spLogMis) return;
    const int a = off < img->size ? img->data[off] : -1, b = off < live->size ? live->data[off] : -1;
    Log("snap audit MISMATCH %s key 0x%08lX%08lX: image %lu bytes, live %lu, first difference at +0x%lX (image %02X, live %02X), "
        "object %p, version %ld epoch %ld", kSpName[in], (DWORD)(img->key >> 32), (DWORD)img->key, img->size, live->size, off, a & 0xFF,
        b & 0xFF, base, g_spCur ? g_spCur->id : 0, g_spEpoch);
}
static bool SpSame(int in, const SpImg* img, SpImg* live, const void* base) {
    Ts3nCounters& k = ts3native_counters;
    k.spAudits[in]++;
    bool eq = SpImgEq(img, live);
    if (!eq && img && live) {
        DWORD off = 0;
        const DWORD n = img->size < live->size ? img->size : live->size;
        while (off < n && img->data[off] == live->data[off]) off++;
        SpLogMis(in, img, live, off, base);
        if (in == SP_GRID) {
            // 0.35.14 (docs/94 §22.17, I-94-472): IP 0.35.13 kept one stale row through 8 audits (versions 272-300): name the
            // rows at the first difference, and rebuild the whole grid at the next cut so a missed mark heals there
            const DWORD row = off / sizeof(SpRow) * sizeof(SpRow);
            if (g_spLogged <= (LONG)g_spLogMis) {
                const SpRow* a = row + sizeof(SpRow) <= img->size ? (const SpRow*)(img->data + row) : NULL;
                const SpRow* b = row + sizeof(SpRow) <= live->size ? (const SpRow*)(live->data + row) : NULL;
                Log("snap audit grid rows at +0x%lX: image lot 0x%08lX%08lX cell %lu key 0x%08lX%08lX, live lot 0x%08lX%08lX cell %lu key 0x%08lX%08lX",
                    row, a ? a->lotHi : 0, a ? a->lotLo : 0, a ? a->cell : 0, a ? a->keyHi : 0, a ? a->keyLo : 0, b ? b->lotHi : 0,
                    b ? b->lotLo : 0, b ? b->cell : 0, b ? b->keyHi : 0, b ? b->keyLo : 0);
            }
            SpLockD();
            g_spD.flags |= SPD_GRID;
            SpUnlockD();
        }
    }
    if (!eq) k.spMis[in]++;
    SpImgRel(live);
    return eq;
}

// invariant S: every image of `v` equals a fresh build from live memory (same lock, same moment). Called right after v's cut:
// the tiles v does not carry are built first (lazy tiles; that build is audit cost, not cut cost), then every tile is checked
static void SpAudit(SpVer* v, const u8* world) {
    SpTilesFill(v, world, 1);
    LARGE_INTEGER t0;
    QueryPerformanceCounter(&t0);
    const u8* g = world + 0x8C;
    const u8* p = (const u8*)(uintptr_t)SpD(world + 0x95C);
    SpSame(SP_R, v->w[SP_R], SpBuildR(world), world + 0x54);
    SpSame(SP_GCORE, v->w[SP_GCORE], SpBuildGCore(g), g);
    SpSame(SP_BNM, v->w[SP_BNM], SpBuildBnm(g), g + 0x35C);
    SpSame(SP_BLEND, v->w[SP_BLEND], SpBuildBlend(*g_spWmSlot), *g_spWmSlot);
    SpSame(SP_TUNE, v->w[SP_TUNE], SpBuildTune(world), (const void*)(uintptr_t)SpD(world + 0x960));
    if (p) SpSame(SP_GRID, v->w[SP_GRID], SpBuildGrid(p, NULL, NULL, 0), p + 0xDC);
    for (int i = 0; i < v->tw * v->th; i++) SpSame(SP_GHC, v->tiles[i], SpBuildTile(g, i % v->tw, i / v->tw), g + 0x7C0);
    // lots: every image the strategy carries against the live lot; a carried lot that left the table is a mismatch
    const int n = p && g_spLot->image ? SpLotTable(p, g_spB2) : 0;
    const SpLotRef* refs = (const SpLotRef*)g_spB2.p;
    int found = 0;
    for (int i = 0; i < n; i++) {
        const SpImg* o = g_spLot->image(v, refs[i].key);
        if (!o) continue;
        found++;
        SpSame(SP_LOTS, o, SpBuildLot(refs[i].node), refs[i].node + 0x10);
    }
    if (g_spLot->image && found != v->nLots) { ts3native_counters.spAudits[SP_LOTS]++; ts3native_counters.spMis[SP_LOTS]++; }
    const LONG us = UsSince(t0);
    InterlockedExchangeAdd(&ts3native_counters.spAuditUs, us);
    Max(&ts3native_counters.spAuditMaxUs, us);
}

// one cut on the sim thread holding +0x918; true = a version was published. read: a reader (armed P4 cut) reads this version,
// so the tiles it would not carry are built now, in the cut's time; false leaves dirty tiles uncarried (lazy tiles)
static bool SpCut(const u8* world, bool read = false) {
    Ts3nCounters& k = ts3native_counters;
    LARGE_INTEGER t0;
    QueryPerformanceCounter(&t0);
    const LONG64 cy0 = CycNow(), cyA0 = g_cyRpAudit;
    const LONG rpAudit0 = k.rpAuditUs;  // the replay's audit hashing (ReplayAuditEvery) runs inside the cut: audit cost, not cut cost
    SpDirty d;
    SpLockD();
    memcpy(&d, &g_spD, sizeof(d));
    memset(&g_spD, 0, sizeof(g_spD));
    if (d.flags & (SPD_G | SPD_EPOCH)) SpKeysClear();  // a G build drops the game's records too (docs/100 §12.1)
    SpUnlockD();
    if (world != g_spWorld) { d.flags |= SPD_EPOCH; g_spWorld = world; }
    if (d.flags & SPD_EPOCH) { g_spEpoch++; k.spEpochs++; SpDoorInvalidateAll(); g_spKeysDirty = 1; }
    const u8* p = (const u8*)(uintptr_t)SpD(world + 0x95C);
    if (p && p[0x10E]) k.spPendingAtCut++;
    SpVer* v = SpBuildVersion(world, g_spCur, d, false);
    if (!v) return false;
    if (read) {
        const LONG f0 = k.spTileFill[0], u0 = k.spTileFillUs[0];
        SpTilesFill(v, world, 0);
        g_spSplit[SP_GHC].n += k.spTileFill[0] - f0, g_spSplit[SP_GHC].us += k.spTileFillUs[0] - u0;
    }
    if (!g_spCur || (d.flags & SPD_EPOCH)) k.spFullBuilds++;
    const int nl = p ? SpLotTable(p, g_spB2) : 0;  // some lot's level-0 quadtree has a deferred flush queued (E17)
    __try {
        for (int i = 0; i < nl; i++) {
            const u8* g = ((const SpLotRef*)g_spB2.p)[i].node + 0x10;
            const DWORD* qb = (const DWORD*)(uintptr_t)SpD(g + 0x74);
            const u8* q = qb && SpD(g + 0x78) > (DWORD)(uintptr_t)qb ? (const u8*)(uintptr_t)qb[0] : NULL;
            if (q && q[0x4C]) { k.spDeferredAtCut++; break; }
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {}
    SpVer* old = g_spCur;
    g_spCur = v;
    SpVerRel(old);
    k.spCuts++;
    const LONG us = UsSince(t0) - (k.rpAuditUs - rpAudit0);  // 0.35.12 (docs/94 §22.15): BP 0.35.9 had 10.1 of its 23.4 ms/s there
    InterlockedExchangeAdd(&k.spCutUs, us);
    Max(&k.spCutMaxUs, us);
    g_spSplitCutUs += us, g_spSplitCuts++;
    g_cyCut += CycNow() - cy0 - (g_cyRpAudit - cyA0);
    static LONG s_rpAudit0, s_simUs0;
    static LONG64 s_cy0[3];  // cut, copies, rebases at the window start
    const ULONGLONG now = GetTickCount64();
    if (!g_spSplitT0) {
        g_spSplitT0 = now, s_rpAudit0 = k.rpAuditUs, s_simUs0 = k.rpCopyUs + k.rpRebaseUs;
        s_cy0[0] = g_cyCut, s_cy0[1] = g_cyCopy, s_cy0[2] = g_cyRebase;
    } else if (now - g_spSplitT0 >= kSpSplitMs) {
        const double s = (now - g_spSplitT0) / 1000.0;
        const SpSplit* q = g_spSplit;
        const LONGLONG rpa = k.rpAuditUs - s_rpAudit0;  // inside the strategy's begin, not in the cut cost
        Log("snap cut split (%.0f s, %ld cuts, %.2f ms/s): R %ld (%ld same) %.2f, bnm %ld (%ld same) %.2f, grid %ld (%ld same) %.2f, "
            "Gcore %ld %.2f, blend %ld %.2f, reader tiles %ld %.2f, lots (%s) %.2f, rest %.2f ms/s",
            s, g_spSplitCuts, g_spSplitCutUs / 1000.0 / s, q[SP_R].n, q[SP_R].same, q[SP_R].us / 1000.0 / s, q[SP_BNM].n, q[SP_BNM].same,
            q[SP_BNM].us / 1000.0 / s, q[SP_GRID].n, q[SP_GRID].same, q[SP_GRID].us / 1000.0 / s, q[SP_GCORE].n, q[SP_GCORE].us / 1000.0 / s,
            q[SP_BLEND].n, q[SP_BLEND].us / 1000.0 / s, q[SP_GHC].n, q[SP_GHC].us / 1000.0 / s, g_spLot->name,
            (q[SP_LOTS].us - rpa) / 1000.0 / s,
            (g_spSplitCutUs - q[SP_R].us - q[SP_BNM].us - q[SP_GRID].us - q[SP_GCORE].us - q[SP_BLEND].us - q[SP_GHC].us - (q[SP_LOTS].us - rpa)) / 1000.0 / s);
        // 0.35.21: the stage-1 sum (cut + sim) in wall ms/s and in thread CPU cycles (noise-resistant; same sections)
        const LONG simUs = k.rpCopyUs + k.rpRebaseUs - s_simUs0;
        const double cyCut = CycMsPerS(g_cyCut - s_cy0[0], s), cySim = CycMsPerS(g_cyCopy - s_cy0[1] + g_cyRebase - s_cy0[2], s);
        Log("snap cut cost (%.0f s): wall cut %.2f + sim (copies + rebases) %.2f = %.2f ms/s; thread CPU (QueryThreadCycleTime / 2.3 GHz "
            "nominal) cut %.2f + sim %.2f = %.2f cyc-ms/s", s, g_spSplitCutUs / 1000.0 / s, simUs / 1000.0 / s,
            (g_spSplitCutUs + simUs) / 1000.0 / s, cyCut, cySim, cyCut + cySim);
        memset(g_spSplit, 0, sizeof(g_spSplit));
        g_spSplitCutUs = 0, g_spSplitCuts = 0, g_spSplitT0 = now, s_rpAudit0 = k.rpAuditUs, s_simUs0 = k.rpCopyUs + k.rpRebaseUs;
        s_cy0[0] = g_cyCut, s_cy0[1] = g_cyCopy, s_cy0[2] = g_cyRebase;
    }
    if (g_spAuditEvery && k.spCuts % (LONG)g_spAuditEvery == 0) SpAudit(v, world);
    if (g_spBytes > g_spCapBytes) {  // memory cap (docs/94 §3.3): off for the rest of the session
        g_spOn = 0;
        k.spCapOff = 1;
        k.spReady &= ~1;
        g_spCur = NULL;
        SpVerRel(v);
        Log("snap: images use %ld KB, above CapMB: switched off", g_spBytes >> 10);
    }
    return true;
}

// from SnIcEnter at a route START (sim thread): a cut at most every CutMs, never while an async job runs or waits
static void SpStart() {
    if (!g_spOn) return;
    Ts3nCounters& k = ts3native_counters;
    const ULONGLONG now = GetTickCount64();
    if (now - g_spLastCut < g_spCutMs) return;
    g_spLastCut = now;
    if (AsyncBusy()) { k.spCutSkip[0]++; return; }
    const u8* world = *g_spWorldSlot;
    if (!world || !SpD(world + 0x95C)) { k.spCutSkip[1]++; return; }
    static const DWORD kInf = 0xFFFFFFFF;
    void* lock = (void*)(world + 0x918);
    if (g_spAcq(lock, NULL, &kInf) <= 0) { k.spCutSkip[2]++; return; }
    SpCut(world);
    g_spRel(lock, NULL);
}

// --- door mirror, native side (contract: docs/94 "P3 build status"; managed side docs/105 §10.5) ---
// ts3fixes pushes each lot's DoorMirror.GetLotDoors(lot) answer over GetOption2 (SpDoorMsg); a null answer is not pushed
// (the lot stays invalid). Native invalidations (world epoch, lot events, build/buy exit) are seen by ts3fixes through
// SDM_Q (valid?) before it pushes. Rule I-105-32: a portal owner id with no record in a valid lot is unknown (guard path).
struct SpDoor { u64 obj, owner, household; DWORD kind, lock, oneWay, roomA, roomB, listOff, listN; };
struct SpDoorLot { u64 key; LONG valid, epoch; DWORD mEpoch; int n, cap, nIds, capIds; SpDoor* d; u64* ids; };
static const int kSpDoorLots = 512;
static SpDoorLot g_spDoorLots[kSpDoorLots];
static int g_spDoorN;
static struct { DWORD lotLo, objLo, ownLo, hhLo, idLo, mEpoch; SpDoorLot* open; SpDoor cur; bool haveDoor; } g_spDm;  // sim only

static SpDoorLot* SpDoorLotFor(u64 key, bool add) {
    for (int i = 0; i < g_spDoorN; i++) if (g_spDoorLots[i].key == key) return &g_spDoorLots[i];
    if (!add || g_spDoorN == kSpDoorLots) return NULL;
    SpDoorLot* l = &g_spDoorLots[g_spDoorN++];
    memset(l, 0, sizeof(*l));
    l->key = key;
    return l;
}
static void SpDoorInvalidateAll() {
    for (int i = 0; i < g_spDoorN; i++) if (g_spDoorLots[i].valid) { g_spDoorLots[i].valid = 0; ts3native_counters.spDoor[3]++; }
}
static bool SpGrow(void** p, int* cap, int need, int elem) {
    if (need <= *cap) return true;
    const int c = *cap ? *cap * 2 : 8;
    if (!g_spHeap) g_spHeap = HeapCreate(0, 0, 0);
    void* q = *p ? HeapReAlloc(g_spHeap, 0, *p, (SIZE_T)c * elem) : HeapAlloc(g_spHeap, 0, (SIZE_T)c * elem);
    if (!q) return false;
    *p = q, *cap = c;
    return true;
}
// P4's reader: the door record of `obj` on a valid lot of the current epoch, else NULL (unknown: guard path, docs/105 §4.6)
static inline const SpDoor* SpDoorFind(u64 lot, u64 obj) {
    const SpDoorLot* l = SpDoorLotFor(lot, false);
    if (!l || !l->valid || l->epoch != g_spEpoch) return NULL;
    for (int i = 0; i < l->n; i++) if (l->d[i].obj == obj) return &l->d[i];
    return NULL;
}

enum { SDM_LOT_LO = 1, SDM_LOT_HI, SDM_OBJ_LO, SDM_OBJ_HI, SDM_KIND, SDM_LOCK, SDM_OWNER_LO, SDM_OWNER_HI, SDM_HH_LO, SDM_HH_HI,
       SDM_ONEWAY, SDM_ROOM_A, SDM_ROOM_B, SDM_LIST_LO, SDM_LIST_HI, SDM_EPOCH, SDM_END, SDM_INV_LO, SDM_INV_HI, SDM_INV_ALL,
       SDM_Q_LO, SDM_Q_HI };
static const DWORD kSpDoorHi = 0x84000000, kSpDoorMaskHi = 0xFF000000;  // RouteOption bits 63 + 58, code in bits 48-55
static u8 SpDoorErr() { ts3native_counters.spDoor[4]++; g_spDm.open = NULL; g_spDm.haveDoor = false; return 0; }
static bool SpDoorFlush() {  // the door being described goes into the open lot
    if (!g_spDm.haveDoor) return true;
    g_spDm.haveDoor = false;
    SpDoorLot* l = g_spDm.open;
    if (!SpGrow((void**)&l->d, &l->cap, l->n + 1, sizeof(SpDoor))) return false;
    l->d[l->n++] = g_spDm.cur;
    ts3native_counters.spDoor[2]++;
    return true;
}
// GetOption2 with optHi = kSpDoorHi | code << 16, optLo = the value; 1 = accepted (SDM_Q_HI: 1 = valid). Sim thread.
static u8 SpDoorMsg(DWORD lo, DWORD hi) {
    Ts3nCounters& k = ts3native_counters;
    k.spDoor[0]++;
    const DWORD code = (hi >> 16) & 0xFF;
    switch (code) {
        case SDM_LOT_LO: case SDM_INV_LO: case SDM_Q_LO: g_spDm.lotLo = lo; return 1;
        case SDM_LOT_HI: {
            SpDoorLot* l = SpDoorLotFor(((u64)lo << 32) | g_spDm.lotLo, true);
            if (!l) return SpDoorErr();
            l->n = 0, l->nIds = 0, l->valid = 0, l->mEpoch = 0;
            g_spDm.open = l, g_spDm.haveDoor = false;
            return 1;
        }
        case SDM_INV_HI: {
            SpDoorLot* l = SpDoorLotFor(((u64)lo << 32) | g_spDm.lotLo, false);
            if (l && l->valid) { l->valid = 0; k.spDoor[3]++; }
            return 1;
        }
        case SDM_INV_ALL: SpDoorInvalidateAll(); return 1;
        case SDM_Q_HI: {
            const SpDoorLot* l = SpDoorLotFor(((u64)lo << 32) | g_spDm.lotLo, false);
            return l && l->valid && l->epoch == g_spEpoch ? 1 : 0;
        }
        default: break;
    }
    if (!g_spDm.open) return SpDoorErr();  // a field outside BEGIN .. END
    SpDoorLot* l = g_spDm.open;
    SpDoor& c = g_spDm.cur;
    switch (code) {
        case SDM_OBJ_LO: if (!SpDoorFlush()) return SpDoorErr(); g_spDm.objLo = lo; return 1;
        case SDM_OBJ_HI:
            memset(&c, 0, sizeof(c));
            c.obj = ((u64)lo << 32) | g_spDm.objLo;
            c.listOff = (DWORD)l->nIds;
            g_spDm.haveDoor = true;
            return 1;
        case SDM_EPOCH: g_spDm.mEpoch = lo; return 1;
        case SDM_END: {
            if (!SpDoorFlush()) return SpDoorErr();
            g_spDm.open = NULL;
            if ((DWORD)l->n != lo) return SpDoorErr();  // count check: a lost message leaves the lot invalid
            l->valid = 1, l->epoch = g_spEpoch, l->mEpoch = g_spDm.mEpoch;
            k.spDoor[1]++;
            return 1;
        }
        default: break;
    }
    if (!g_spDm.haveDoor) return SpDoorErr();
    switch (code) {
        case SDM_KIND: c.kind = lo; return 1;
        case SDM_LOCK: c.lock = lo; return 1;
        case SDM_OWNER_LO: g_spDm.ownLo = lo; return 1;
        case SDM_OWNER_HI: c.owner = ((u64)lo << 32) | g_spDm.ownLo; return 1;
        case SDM_HH_LO: g_spDm.hhLo = lo; return 1;
        case SDM_HH_HI: c.household = ((u64)lo << 32) | g_spDm.hhLo; return 1;
        case SDM_ONEWAY: c.oneWay = lo; return 1;
        case SDM_ROOM_A: c.roomA = lo; return 1;
        case SDM_ROOM_B: c.roomB = lo; return 1;
        case SDM_LIST_LO: g_spDm.idLo = lo; return 1;
        case SDM_LIST_HI:  // one owner-list id appended to this door's list
            if (!SpGrow((void**)&l->ids, &l->capIds, l->nIds + 1, sizeof(u64))) return SpDoorErr();
            l->ids[l->nIds++] = ((u64)lo << 32) | g_spDm.idLo;
            c.listN++;
            return 1;
        default: return SpDoorErr();
    }
}

static bool ApplyReplay(const wchar_t* ini);  // replay.cpp: [RouteSnap] Lots=replay
// [RouteSnap] Enabled=1 (after ApplyRouteSnap, which installs P1's sites for Count=1 or Enabled=1)
static bool ApplySnapImages(const wchar_t* ini) {
    Ts3nCounters& k = ts3native_counters;
    if (!GetPrivateProfileIntW(L"RouteSnap", L"Enabled", 0, ini)) { Log("route snapshot images off (Enabled=0)"); return false; }
    if (!g_snOn) { Log("SKIP route snapshot images: the P1 sites are not installed"); return false; }
    k.spMode = Clamp(GetPrivateProfileIntW(L"RouteSnap", L"Mode", 0, ini), 0, 2);  // 0 = audit only (P3); 1 = P4 shadow (p4.cpp); 2 = P5 (p5.cpp)
    g_spCutMs = (DWORD)Clamp(GetPrivateProfileIntW(L"RouteSnap", L"CutMs", 250, ini), 0, 60000);
    g_spAuditEvery = (DWORD)Clamp(GetPrivateProfileIntW(L"RouteSnap", L"AuditEvery", 20, ini), 0, 100000);
    g_spCapBytes = Clamp(GetPrivateProfileIntW(L"RouteSnap", L"CapMB", 64, ini), 8, 512) << 20;
    g_spLogMis = (DWORD)Clamp(GetPrivateProfileIntW(L"RouteSnap", L"LogMismatch", 32, ini), 0, 10000);
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
    g_spHeap = g_spHeap ? g_spHeap : HeapCreate(0, 0, 0);
    SpKeysClear();
    g_spOn = 1;
    k.spReady = 1 | (k.snShared ? 2 : 0) | 4;
    const bool part = !(k.snReady & 1) || k.snSkipped || k.snIcSlots != SN_IC_N;
    Log("%s route snapshot images: Mode=%ld CutMs=%lu AuditEvery=%lu CapMB=%ld (P1 sites: %ld own, %ld shared, %ld skipped)",
        part ? "PART" : "OK  ", k.spMode, g_spCutMs, g_spAuditEvery, g_spCapBytes >> 20, k.snHooks, k.snShared, k.snSkipped);
    ApplyReplay(ini);
    return true;
}

// P1b (routesnap.cpp, docs/94 §15): one timed canonical copy of lot `key` from the live table on the sim thread (at a route
// START after a sim LotGraph_Build); the rebase copy cost of design B. Returns the image bytes, 0 = not in the table,
// -1 = fault. Keeps no image, and P3's own image counters stay as they were.
static LONG SpProbeLotCopy(u64 key, LONG* us) {
    Ts3nCounters& k = ts3native_counters;
    const LONG kbMax = k.spKBMax, faults = k.spFaults;
    DWORD world = 0, p = 0;
    if (!PvRead(g_spWorldSlot, &world) || !world || !PvRead((const u8*)(uintptr_t)world + 0x95C, &p) || !p) return 0;
    const int n = SpLotTable((const u8*)(uintptr_t)p, g_spB2);
    const SpLotRef* refs = (const SpLotRef*)g_spB2.p;
    int i = 0;
    while (i < n && refs[i].key != key) i++;
    LONG r = 0;
    if (i < n) {
        LARGE_INTEGER t0, t1, f;
        QueryPerformanceCounter(&t0);
        SpImg* m = SpBuildLot(refs[i].node);
        QueryPerformanceCounter(&t1);
        QueryPerformanceFrequency(&f);
        *us = (LONG)((t1.QuadPart - t0.QuadPart) * 1000000 / f.QuadPart);
        r = m ? (LONG)m->size : -1;
        SpImgRel(m);
    } else if (n < 0) r = -1;
    k.spKBMax = kbMax;
    k.spFaults = faults;
    return r;
}
