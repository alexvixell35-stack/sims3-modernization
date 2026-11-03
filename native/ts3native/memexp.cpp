// Memory experiments on the Boehm heap (docs/199). Included by ts3native.cpp after gcthreads.cpp. Both off by default.
//
// [MemDiag] TypeGrowth (Cork, Jump & McKinley, POPL 2007 / SPE 2009): after a completed collection, while the mark bits
// are complete, one walk over the marked blocks sums live managed objects per MonoClass (object +0 vtable, vtable +0
// klass) and ranks the classes by Cork's ratio ranking (decay factor f, growth phases); a second walk scans the live
// pointer-carrying objects for references to the top-K growing classes and sums the bytes per holder class (one hop of
// Cork's points-from graph). Lines "typegrowth:".
// [GC] SqrtHeapLimit (MemBalancer, Kirisame, Shenoy, Panchekha, OOPSLA 2022): heap limit M = L + sqrt(L g / (c s)) from
// live bytes L after each completed collection, the smoothed allocation rate g and GC speed s; GC_collect_or_expand
// grows the heap up to M instead of running a forced full collection. Lines "gcsize:".
//
// Hooks [verified static, docs/199 §2]: GC_notify_event pointer [0x1234360] (Mono's libgc event hook; MonoScriptHost
// init stores the empty function 0xC0FD60 at 0xD7E62A; event 3 = RECLAIM_START at the top of GC_finish_collection
// 0xE49A50, marks complete, before GC_finalize and the sweep); GC_collect_or_expand 0xE4A120 (cdecl needed blocks,
// ignore_off_page; detour) and the per-frame GC_try_to_collect call 0xD819AA (call-site retarget, timing only).
// Block layout (EA's libgc 6.x): bottom index list [0x11F3054] (+0x1000 next, +0x1008 key), top index 0x1233318,
// header +0 size in words, +0xC descriptor, +0x10 map (free block = [0x11F30D8]), +0x14 kind byte, +0x1C mark bits
// (one per word, set at object starts only; +0x9C = EA's free bits).

#include <math.h>

struct MxAddrs {  // game globals; the host selftest points them at fakes
    const DWORD* top;          // top index, 1024 bottom-index pointers
    const DWORD* bottomHead;   // GC_all_bottom_indices
    const DWORD* invalidMap;   // GC_invalid_map (hb_map of a free block)
    const DWORD* gcNo;         // GC_gc_no
    const DWORD* heapSize;     // GC_heapsize (bytes)
    const DWORD* wordsAllocd;  // GC_words_allocd since the last collection
    const DWORD* wordsBefore;  // GC_words_allocd_before_gc (finish adds wordsAllocd to it after event 3)
    const DWORD* incremental;  // GC_incremental
    const DWORD* dontGc;       // GC_dont_gc
};
static MxAddrs g_mx = {(const DWORD*)(uintptr_t)0x01233318, (const DWORD*)(uintptr_t)0x011F3054,
                       (const DWORD*)(uintptr_t)0x011F30D8, (const DWORD*)(uintptr_t)0x011F2F90,
                       (const DWORD*)(uintptr_t)0x012225A0, (const DWORD*)(uintptr_t)0x012225C4,
                       (const DWORD*)(uintptr_t)0x012225C0, (const DWORD*)(uintptr_t)0x011F2F9C,
                       (const DWORD*)(uintptr_t)0x011F30A8};
typedef int(__cdecl* MxExpandFn)(DWORD blocks);               // GC_expand_hp_inner 0xE49C80 (min 0x40 blocks)
typedef int(__cdecl* MxCoeFn)(DWORD needed, int ignoreOffPage);  // GC_collect_or_expand 0xE4A120
typedef int(__cdecl* MxStepFn)(void* stop);                    // GC_try_to_collect 0xE4A050
static MxExpandFn g_mxExpand = (MxExpandFn)(uintptr_t)0x00E49C80;
static MxCoeFn g_mxCoeOrig;
static MxStepFn g_mxStepOrig;

static const DWORD MX_SMALL_MAX_WORDS = 0x200;  // larger = one object per block run (GC_reclaim_block 0xE50160)
static const int MX_CLS_MAX = 16384, MX_KMAP = 1 << 15, MX_VMAP = 1 << 16, MX_TOPK = 16, MX_HOLD = 24;

struct MxCls {
    DWORD klass;
    DWORD n;
    u64 bytes, last, maxVol;  // this pass, previous pass, Cork maxVolumeTraced
    double rank;
    int phases;
    int target;  // index into the graph targets this pass, -1 none
};
struct MxHold { int cls; u64 bytes; DWORD n; };
struct MxTarget { int cls; MxHold h[MX_HOLD]; u64 other; DWORD nh; };
struct MxState {
    MxCls cls[MX_CLS_MAX];
    int nCls;
    DWORD kKey[MX_KMAP];
    short kIdx[MX_KMAP];   // klass -> class index (persistent)
    DWORD vKey[MX_VMAP];
    short vIdx[MX_VMAP];   // vtable -> class index this pass, -1 = not a vtable
    int vFill;
    MxTarget tg[MX_TOPK * 2];
    int nTg;
    DWORD heapLo, heapHi;
    u64 objs, objBytes, markedBytes, rejects;
};
static MxState* g_mxs;  // VirtualAlloc'd (not a GC root region: no heap pointers kept)

// settings
static int g_tgEvery = 1, g_tgTopK = 8, g_tgGraphEvery = 10;
static double g_tgRankMin = 100.0, g_tgDecay = 0.15;  // Cork RRT defaults (rank threshold 100, f = 15%)
static double g_shCPerMB = 0.0005;                    // MemBalancer c, as GC-time fraction per MB of extra heap
static DWORD g_shFloor = 352u << 20, g_shCeil = 1024u << 20, g_shMinExtra = 16u << 20, g_shStepBlocks = 32 * 256;
static DWORD g_shLogMs = 10000;

// MemBalancer state (doubles: sim thread or the collecting thread only)
static double g_shGb, g_shGt, g_shSb, g_shSt;  // smoothed alloc bytes / seconds, live bytes / GC seconds
static LARGE_INTEGER g_shLastT;
static u64 g_shLastAlloc;
static bool g_shPrimed;
static volatile LONGLONG g_shGcUs;  // GC time since the last completed collection (per-frame steps + forced), minus ours
static volatile DWORD g_shLimit;    // bytes, 0 = no limit computed yet
static DWORD g_shLastLog, g_shGrowKBSince, g_shForcedSince;
static LONG g_mxPass, g_tgPassN;

// a fault aborts the pass (no line); the first few are logged so a silent stop shows (docs/199 §7.1)
static void MxFault(const char* where) {
    const LONG n = ++ts3native_counters.tyFaults;
    if (n <= 8) Log("typegrowth: pass fault in %s (gc=%lu, faults=%ld)%s", where, *g_mx.gcNo, n, n == 8 ? ", not logged again" : "");
}

static int MxPopcnt(DWORD x) { return (int)__popcnt(x); }

static const DWORD* MxHdr(DWORD a) {
    const DWORD bi = g_mx.top[a >> 22];
    if (!bi) return NULL;
    const DWORD h = ((const DWORD*)(uintptr_t)bi)[(a >> 12) & 0x3FF];
    return h < 0x1000 ? NULL : (const DWORD*)(uintptr_t)h;
}

// every in-use block start (same walk as GC_apply_to_all_blocks 0xE4B850)
template <class F> static void MxBlocks(F f) {
    for (DWORD bi = *g_mx.bottomHead; bi; bi = *(const DWORD*)(uintptr_t)(bi + 0x1000)) {
        const DWORD* idx = (const DWORD*)(uintptr_t)bi;
        const DWORD key = *(const DWORD*)(uintptr_t)(bi + 0x1008);
        for (int j = 0x3FF; j >= 0;) {
            const DWORD h = idx[j];
            if (h < 0x1000) { j -= h ? (int)h : 1; continue; }
            const DWORD* hd = (const DWORD*)(uintptr_t)h;
            if (hd[4] != *g_mx.invalidMap) f((key * 0x400 + (DWORD)j) * 0x1000, hd);
            j--;
        }
    }
}
static int MxKind(const DWORD* hd) { return ((const u8*)hd)[0x14]; }
static bool MxUncollectable(const DWORD* hd) { return (MxKind(hd) & 0xFE) == 2; }

// marked bytes only (SqrtHeapLimit without TypeGrowth): popcount of the mark bits times the object size
static u64 MxMarkedBytes() {
    u64 b = 0;
    MxBlocks([&](DWORD, const DWORD* hd) {
        const DWORD sz = hd[0];
        if (sz > MX_SMALL_MAX_WORDS) { if (hd[7] & 1) b += (u64)sz * 4; return; }
        int n = 0;
        for (int w = 0; w < 32; w++) n += MxPopcnt(hd[7 + w]);
        b += (u64)n * sz * 4;
    });
    return b;
}

// ---- class resolution: vtable -> klass, accepted only when klass's runtime info (+0xB8: u16 max domain id, vtables
// from +4) holds this vtable [verified I-123-28]. Every read is checked against a page-readability cache.
static LONG g_mxQueries;  // VirtualQuery calls (host bench)
static u8* g_mxPage;  // 2 bits per 4 KB page: 0 unknown, 1 readable, 2 not
// One VirtualQuery fills the whole region's pages: per-page queries cost ~7 us each in the game (4,600 regions) and
// non-vtable first words (pointers, ints, floats) spread over most of the 4 GB, so the census paid 2-8 s per pass
// (docs/199 §7.1); now at most one query per region touched.
static int MxQueryRegion(DWORD p) {
    MEMORY_BASIC_INFORMATION mbi;
    DWORD lo = p, hi = p + 1;
    g_mxQueries++;
    int st = 2;
    if (VirtualQuery((const void*)(uintptr_t)(p << 12), &mbi, sizeof(mbi))) {
        st = mbi.State == MEM_COMMIT && !(mbi.Protect & (PAGE_NOACCESS | PAGE_GUARD)) ? 1 : 2;
        lo = (DWORD)((uintptr_t)mbi.BaseAddress >> 12), hi = lo + (DWORD)(mbi.RegionSize >> 12);
        if (hi > (1u << 20) || hi <= lo) hi = 1u << 20;
        if (lo > p || hi <= p) lo = p, hi = p + 1;
    }
    for (DWORD q = lo; q < hi; q++) g_mxPage[q >> 2] = (u8)((g_mxPage[q >> 2] & ~(3 << ((q & 3) * 2))) | (st << ((q & 3) * 2)));
    return st;
}
static bool MxReadable(DWORD a, DWORD n) {
    if (!n || a + n - 1 < a) return false;  // wraps past 4 GB: runtime info 0xFFFFFFFF faulted every pass after load (§7.1)
    for (DWORD p = a >> 12; p <= (a + n - 1) >> 12; p++) {
        int st = (g_mxPage[p >> 2] >> ((p & 3) * 2)) & 3;
        if (!st) st = MxQueryRegion(p);
        if (st != 1) return false;
    }
    return true;
}
static int MxClassIdx(DWORD klass) {
    MxState& s = *g_mxs;
    for (DWORD i = (klass >> 3) * 2654435761u >> 17;; i = (i + 1) & (MX_KMAP - 1)) {
        if (s.kKey[i] == klass) return s.kIdx[i];
        if (!s.kKey[i]) {
            if (s.nCls >= MX_CLS_MAX - 1) return MX_CLS_MAX - 1;  // shortcut: the last slot collects the overflow
            MxCls& c = s.cls[s.nCls];
            memset(&c, 0, sizeof(c));
            c.klass = klass, c.target = -1;
            s.kKey[i] = klass, s.kIdx[i] = (short)s.nCls;
            return s.nCls++;
        }
    }
}
static int MxVt(DWORD vt) {
    MxState& s = *g_mxs;
    if (vt < 0x10000 || (vt & 3)) { s.rejects++; return -1; }
    DWORD i = (vt >> 2) * 2654435761u >> 16;
    for (;; i = (i + 1) & (MX_VMAP - 1)) {
        if (s.vKey[i] == vt) return s.vIdx[i];
        if (!s.vKey[i]) break;
    }
    int r = -1;
    if (MxReadable(vt, 4)) {
        const DWORD klass = *(const DWORD*)(uintptr_t)vt;
        if (klass >= 0x10000 && !(klass & 3) && MxReadable(klass + 0xB8, 4)) {
            const DWORD ri = *(const DWORD*)(uintptr_t)(klass + 0xB8);
            if (ri >= 0x10000 && MxReadable(ri, 4)) {
                const DWORD maxId = *(const WORD*)(uintptr_t)ri;
                if (maxId < 64 && MxReadable(ri + 4, (maxId + 1) * 4))
                    for (DWORD d = 0; d <= maxId; d++)
                        if (((const DWORD*)(uintptr_t)(ri + 4))[d] == vt) { r = MxClassIdx(klass); break; }
            }
        }
    }
    if (s.vFill < MX_VMAP * 3 / 4) s.vKey[i] = vt, s.vIdx[i] = (short)r, s.vFill++;  // full: uncached (still correct)
    if (r < 0) s.rejects++;
    return r;
}
// "Ns.Name" (MonoClass +0x38 namespace, +0x34 name), separators of our line format replaced
static void MxName(DWORD klass, char* out, int cap) {
    out[0] = 0;
    if (klass == 0) { strcpy_s(out, cap, "(no class)"); return; }
    __try {
        const char* nm = *(const char* const*)(uintptr_t)(klass + 0x34);
        const char* ns = *(const char* const*)(uintptr_t)(klass + 0x38);
        _snprintf_s(out, cap, _TRUNCATE, "%s%s%s", ns && *ns ? ns : "", ns && *ns ? "." : "", nm ? nm : "?");
    } __except (EXCEPTION_EXECUTE_HANDLER) { strcpy_s(out, cap, "?"); }
    for (char* p = out; *p; p++)
        if (*p == ',' || *p == ':' || *p == '=' || *p == ' ' || *p == '<' ) *p = '_';
}

// ---- census walk: per class count + bytes, total marked bytes, heap bounds
template <class F> static void MxMarked(const DWORD* hd, DWORD blk, F f) {
    const DWORD sz = hd[0];
    if (sz > MX_SMALL_MAX_WORDS) { if (hd[7] & 1) f(blk, sz * 4); return; }
    for (int w = 0; w < 32; w++) {
        DWORD m = hd[7 + w];
        while (m) {
            unsigned long b;
            _BitScanForward(&b, m);
            m &= m - 1;
            const DWORD off = ((DWORD)w * 32 + b) * 4;
            if (off + sz * 4 <= 0x1000) f(blk + off, sz * 4);
        }
    }
}
static void MxCensus() {
    MxState& s = *g_mxs;
    memset(s.vKey, 0, sizeof(s.vKey)), s.vFill = 0;
    memset(g_mxPage, 0, 1 << 18);
    for (int i = 0; i < s.nCls; i++) s.cls[i].last = s.cls[i].bytes, s.cls[i].n = 0, s.cls[i].bytes = 0;
    s.objs = s.objBytes = s.markedBytes = s.rejects = 0, s.heapLo = ~0u, s.heapHi = 0;
    MxBlocks([&](DWORD blk, const DWORD* hd) {
        const DWORD end = blk + (hd[0] > MX_SMALL_MAX_WORDS ? (hd[0] * 4 + 0xFFF) & ~0xFFFu : 0x1000);
        if (blk < s.heapLo) s.heapLo = blk;
        if (end > s.heapHi) s.heapHi = end;
        const bool unc = MxUncollectable(hd);
        MxMarked(hd, blk, [&](DWORD obj, DWORD bytes) {
            s.markedBytes += bytes;
            if (unc) return;
            const int c = MxVt(*(const DWORD*)(uintptr_t)obj);
            if (c < 0) return;
            s.cls[c].n++, s.cls[c].bytes += bytes, s.objs++, s.objBytes += bytes;
        });
    });
}

// Cork ratio ranking (SPE 2009 Fig. 5): growth phase when this volume > maxVolume * (1 - f); rank += phases * (ratio - 1)
// on growth, -= on shrink; a non-growth phase resets. Candidates: phases >= 2 and rank > threshold.
static void MxRank(MxCls& c, double f) {
    if (!c.bytes) return;
    if ((double)c.bytes > (double)c.maxVol * (1 - f)) {
        c.phases++;
        if (c.bytes > c.maxVol) c.maxVol = c.bytes;
        if (c.last) {
            if (c.bytes > c.last) c.rank += c.phases * ((double)c.bytes / c.last - 1);
            else c.rank -= c.phases * ((double)c.last / c.bytes - 1);
        }
    } else c.phases = 0, c.rank = 0, c.maxVol = c.bytes;
}

// top-K targets: ranked candidates first (phases >= 2, rank > 0), then the largest growth since the last pass
static void MxPickTargets(int k) {
    MxState& s = *g_mxs;
    for (int i = 0; i < s.nCls; i++) s.cls[i].target = -1;
    s.nTg = 0;
    for (int round = 0; round < 2 && s.nTg < k; round++)
        while (s.nTg < k) {
            int best = -1;
            double bv = 0;
            for (int i = 0; i < s.nCls; i++) {
                const MxCls& c = s.cls[i];
                if (c.target >= 0 || !c.bytes) continue;
                const double v = round == 0 ? (c.phases >= 2 ? c.rank : 0) : (double)c.bytes - (double)c.last;
                if (v > bv) bv = v, best = i;
            }
            if (best < 0) break;
            s.cls[best].target = s.nTg;
            MxTarget& t = s.tg[s.nTg++];
            memset(&t, 0, sizeof(t));
            t.cls = best;
        }
}
static void MxAddHolder(MxTarget& t, int src, DWORD bytes) {
    for (DWORD i = 0; i < t.nh; i++)
        if (t.h[i].cls == src) { t.h[i].bytes += bytes, t.h[i].n++; return; }
    if (t.nh < MX_HOLD) { t.h[t.nh].cls = src, t.h[t.nh].bytes = bytes, t.h[t.nh].n = 1, t.nh++; return; }
    t.other += bytes;
}
// graph walk: every word of every live pointer-carrying object (hb_descr != 0) that points at the start of a live object
// of a target class adds the target's size to (source class -> target). Conservative: a word that only looks like a
// pointer counts too (Boehm scans NORMAL-kind objects conservatively; typed objects are scanned whole here as well).
static void MxGraph() {
    MxState& s = *g_mxs;
    if (!s.nTg) return;
    MxBlocks([&](DWORD blk, const DWORD* hd) {
        if (!hd[3] || MxUncollectable(hd)) return;
        MxMarked(hd, blk, [&](DWORD obj, DWORD bytes) {
            const int src = MxVt(*(const DWORD*)(uintptr_t)obj);
            const DWORD* p = (const DWORD*)(uintptr_t)obj;
            for (DWORD w = 1; w < bytes / 4; w++) {
                const DWORD a = p[w];
                if (a < s.heapLo || a >= s.heapHi || (a & 3)) continue;
                const DWORD* th = MxHdr(a);
                if (!th || th[4] == *g_mx.invalidMap || MxUncollectable(th)) continue;
                const DWORD off = a & 0xFFF, tsz = th[0];
                if (tsz > MX_SMALL_MAX_WORDS ? (off != 0 || !(th[7] & 1))
                                             : (((const u8*)(uintptr_t)th[4])[off] != 0 || !((th[7 + (off >> 7)] >> ((off >> 2) & 31)) & 1)))
                    continue;
                const int tc = MxVt(*(const DWORD*)(uintptr_t)a);
                if (tc < 0 || s.cls[tc].target < 0) continue;
                MxAddHolder(s.tg[s.cls[tc].target], src, tsz * 4);
            }
        });
    });
}

static double MxMs(LARGE_INTEGER t0) { return UsSince(t0) / 1000.0; }

static void MxTypeGrowth(LONG gcNo) {
    MxState& s = *g_mxs;
    Ts3nCounters& k = ts3native_counters;
    LARGE_INTEGER t0, t1;
    QueryPerformanceCounter(&t0);
    MxCensus();
    for (int i = 0; i < s.nCls; i++) MxRank(s.cls[i], g_tgDecay);
    const double cenMs = MxMs(t0);
    QueryPerformanceCounter(&t1);
    // the points-from graph only every TypeGrowthGraphEvery-th pass (0 = never): it walks every word of every live object
    const bool graph = g_tgGraphEvery > 0 && (g_tgPassN + 1) % g_tgGraphEvery == 0;
    MxPickTargets(graph ? g_tgTopK : 0);
    __try { MxGraph(); } __except (EXCEPTION_EXECUTE_HANDLER) { MxFault("graph"); }
    // one hop more for arrays: an array class that holds most of a target becomes a target of its own (List<T> -> T[])
    const int base = s.nTg;
    for (int t = 0; t < base && s.nTg < MX_TOPK * 2; t++) {
        const MxTarget& x = s.tg[t];
        int bi = -1;
        for (DWORD i = 0; i < x.nh; i++) if (x.h[i].cls >= 0 && (bi < 0 || x.h[i].bytes > x.h[bi].bytes)) bi = (int)i;
        if (bi < 0) continue;
        char nm[96];
        MxName(s.cls[x.h[bi].cls].klass, nm, sizeof(nm));
        const size_t L = strlen(nm);
        if (L < 2 || strcmp(nm + L - 2, "[]") || s.cls[x.h[bi].cls].target >= 0) continue;
        s.cls[x.h[bi].cls].target = s.nTg;
        MxTarget& n = s.tg[s.nTg++];
        memset(&n, 0, sizeof(n));
        n.cls = x.h[bi].cls;
    }
    if (s.nTg > base) {
        for (int t = 0; t < base; t++) s.cls[s.tg[t].cls].target = -1;  // second pass: only the new array targets
        __try { MxGraph(); } __except (EXCEPTION_EXECUTE_HANDLER) { MxFault("graph"); }
        for (int t = 0; t < base; t++) s.cls[s.tg[t].cls].target = t;
    }
    const LONG grUs = UsSince(t1), allUs = UsSince(t0);
    k.tyPasses++, k.tyLastUs = allUs, k.tyGraphLastUs = grUs, k.tyObjs = (LONG)s.objs, k.tyClasses = s.nCls, k.tyRejects = (LONG)s.rejects;
    Max(&k.tyMaxUs, allUs), Max(&k.tyGraphMaxUs, grUs);

    // log: top 12 by rank (candidates marked *), then holders per target
    char line[480], nm[96];
    int n = _snprintf_s(line, sizeof(line), _TRUNCATE, "typegrowth: #%ld gc=%ld objs=%llu MB=%.1f marked=%.1f classes=%d census=%.1f graph=%.1f top=",
                        ++g_tgPassN, gcNo, s.objs, s.objBytes / 1048576.0, s.markedBytes / 1048576.0, s.nCls, cenMs, grUs / 1000.0);
    // top 12: entries name:KB:+dKB:rank:phases, "*" = Cork candidate (phases >= 2, rank > TypeGrowthRankMin)
    int shown[12], ns = 0;
    for (int r = 0; r < 12; r++) {
        int best = -1;
        for (int i = 0; i < s.nCls; i++) {
            const MxCls& c = s.cls[i];
            bool dup = false;
            for (int j = 0; j < ns; j++) dup = dup || shown[j] == i;
            if (!c.bytes || dup) continue;
            if (best < 0 || c.rank > s.cls[best].rank || (c.rank == s.cls[best].rank && c.bytes > s.cls[best].bytes)) best = i;
        }
        if (best < 0) break;
        shown[ns++] = best;
        const MxCls& c = s.cls[best];
        MxName(c.klass, nm, sizeof(nm));
        const bool cand = c.phases >= 2 && c.rank > g_tgRankMin;
        n += _snprintf_s(line + n, sizeof(line) - n, _TRUNCATE, "%s%s%s:%.0f:%+.0f:%.1f:%d", r ? "," : "", cand ? "*" : "", nm,
                         c.bytes / 1024.0, ((double)c.bytes - (double)c.last) / 1024.0, c.rank, c.phases);
        if (n >= (int)sizeof(line) - 1) break;
    }
    Log("%s", line);
    // holders per target: name:KB, largest first
    for (int t = 0; t < s.nTg; t++) {
        MxTarget& x = s.tg[t];
        MxName(s.cls[x.cls].klass, nm, sizeof(nm));
        n = _snprintf_s(line, sizeof(line), _TRUNCATE, "typegrowth: holders #%ld %s KB=%.0f <-", g_tgPassN, nm, s.cls[x.cls].bytes / 1024.0);
        for (int r = 0; r < 8; r++) {
            int bi = -1;
            for (DWORD i = 0; i < x.nh; i++) if (x.h[i].n && (bi < 0 || x.h[i].bytes > x.h[bi].bytes)) bi = (int)i;
            if (bi < 0) break;
            if (x.h[bi].cls >= 0) MxName(s.cls[x.h[bi].cls].klass, nm, sizeof(nm)); else strcpy_s(nm, "(non-object)");
            n += _snprintf_s(line + n, sizeof(line) - n, _TRUNCATE, " %s:%.0f", nm, x.h[bi].bytes / 1024.0);
            x.h[bi].n = 0;  // printed (the table is rebuilt next pass)
            if (n >= (int)sizeof(line) - 1) break;
        }
        if (x.other) _snprintf_s(line + n, sizeof(line) - n, _TRUNCATE, " other:%.0f", x.other / 1024.0);
        Log("%s", line);
    }
}

// ---- MemBalancer square-root rule
static DWORD MxSqrtLimit(double L, double g, double s, double cPerMB, DWORD floorB, DWORD ceilB, DWORD minExtraB) {
    double e = 0;
    if (L > 0 && g > 0 && s > 0 && cPerMB > 0) e = sqrt(L * g / ((cPerMB / 1048576.0) * s));
    if (e < minExtraB) e = minExtraB;
    double m = L + e;
    if (m < floorB) m = floorB;
    if (m > ceilB) m = ceilB;
    return (DWORD)m;
}
// blocks to grow by when the heap may still grow under the limit: up to the limit, capped at one growth step, at least
// what the allocation needs; 0 = no room (run the game's own collection)
static DWORD MxGrowBlocks(DWORD heap, DWORD limit, DWORD needed, DWORD stepBlocks) {
    if (!limit || heap >= limit || limit - heap < needed * 4096u) return 0;
    DWORD b = (limit - heap) / 4096u;
    if (b > stepBlocks) b = stepBlocks;
    return b < needed ? needed : b;
}

static void MxSqrtOnGc(double L, LONG gcNo, LONG walkUs) {
    Ts3nCounters& k = ts3native_counters;
    LARGE_INTEGER now;
    QueryPerformanceCounter(&now);
    const u64 alloc = ((u64)*g_mx.wordsBefore + *g_mx.wordsAllocd) * 4;
    const LONGLONG gcUs = InterlockedExchange64(&g_shGcUs, 0);
    if (g_shPrimed) {
        const double dt = (double)(now.QuadPart - g_shLastT.QuadPart) / g_qpf.QuadPart;
        const double a = (double)(u64)(alloc - g_shLastAlloc), al = pow(0.95, dt);  // half-life ~13 s per the paper
        g_shGb = al * g_shGb + (1 - al) * a, g_shGt = al * g_shGt + (1 - al) * dt;
        if (gcUs > 0) g_shSb = 0.5 * g_shSb + 0.5 * L, g_shSt = 0.5 * g_shSt + 0.5 * (gcUs / 1e6);
    }
    g_shPrimed = true, g_shLastT = now, g_shLastAlloc = alloc;
    const double g = g_shGt > 0 ? g_shGb / g_shGt : 0, s = g_shSt > 0 ? g_shSb / g_shSt : 0;
    const DWORD lim = (g > 0 && s > 0) ? MxSqrtLimit(L, g, s, g_shCPerMB, g_shFloor, g_shCeil, g_shMinExtra) : 0;
    g_shLimit = lim;
    const DWORD heap = *g_mx.heapSize;
    k.sqGcs++, k.sqLiveKB = (LONG)(L / 1024), k.sqLimitKB = (LONG)(lim >> 10), k.sqHeapKB = (LONG)(heap >> 10);
    k.sqAllocKBs = (LONG)(g / 1024), k.sqSpeedMBs = (LONG)(s / 1048576), k.sqGcUsLast = (LONG)gcUs, k.sqWalkUsLast = walkUs;
    Max(&k.sqWalkUsMax, walkUs);
    k.sqWouldFreeKB = lim && heap > lim ? (LONG)((heap - lim) >> 10) : 0;
    const DWORD t = GetTickCount();
    if (t - g_shLastLog >= g_shLogMs || !g_shLastLog) {
        Log("gcsize: gc=%ld mode=%ld live=%.1f limit=%.1f heap=%.1f alloc=%.2f speed=%.0f gcMs=%.1f grewMB=%.1f forced=%lu wouldFree=%.1f walkMs=%.2f",
            gcNo, k.sqMode, L / 1048576, lim / 1048576.0, heap / 1048576.0, g / 1048576, s / 1048576, gcUs / 1000.0,
            g_shGrowKBSince / 1024.0, g_shForcedSince, k.sqWouldFreeKB / 1024.0, walkUs / 1000.0);
        g_shLastLog = t ? t : 1, g_shGrowKBSince = 0, g_shForcedSince = 0;
    }
}

// event 3 (RECLAIM_START): marks complete
static void MxOnGc() {
    Ts3nCounters& k = ts3native_counters;
    const bool tg = k.tyMode != 0 && g_mxs, sh = k.sqMode != 0;
    if (!tg && !sh) return;
    LARGE_INTEGER t0;
    QueryPerformanceCounter(&t0);
    const LONG gcNo = (LONG)*g_mx.gcNo;
    double L = -1;
    __try {
        if (tg && ++g_mxPass % (g_tgEvery < 1 ? 1 : g_tgEvery) == 0) {
            MxTypeGrowth(gcNo);
            L = (double)g_mxs->markedBytes;
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) { MxFault("census"); }
    const LONG walk0 = UsSince(t0);
    if (sh) {
        __try {
            if (L < 0) L = (double)MxMarkedBytes();
        } __except (EXCEPTION_EXECUTE_HANDLER) { MxFault("marked bytes"), L = -1; }
        if (L >= 0) MxSqrtOnGc(L, gcNo, UsSince(t0) - walk0);
    }
    InterlockedExchangeAdd64(&g_shGcUs, -(LONGLONG)UsSince(t0));  // our walk is not GC work
}

static void __cdecl MxNotify(int ev) {
    if (ev == 3) MxOnGc();
}

static int __cdecl MxStep(void* stop) {
    LARGE_INTEGER t0;
    QueryPerformanceCounter(&t0);
    const int r = g_mxStepOrig(stop);
    InterlockedExchangeAdd64(&g_shGcUs, UsSince(t0));
    return r;
}

static int __cdecl MxCoe(DWORD needed, int ignoreOffPage) {
    Ts3nCounters& k = ts3native_counters;
    if (k.sqMode == 1 && *g_mx.incremental && !*g_mx.dontGc && *g_mx.wordsAllocd) {
        const DWORD heap = *g_mx.heapSize, b = MxGrowBlocks(heap, g_shLimit, needed, g_shStepBlocks);
        if (b && (g_mxExpand(b) || (b != needed && g_mxExpand(needed)))) {
            const DWORD kb = (*g_mx.heapSize - heap) >> 10;
            k.sqGrows++, k.sqGrowKB += (LONG)kb, g_shGrowKBSince += kb;
            return 1;
        }
    }
    LARGE_INTEGER t0;
    QueryPerformanceCounter(&t0);
    const int r = g_mxCoeOrig(needed, ignoreOffPage);
    InterlockedExchangeAdd64(&g_shGcUs, UsSince(t0));
    k.sqForced++, g_shForcedSince++;
    return r;
}

static const Site kMxNotifySite[] = {
    {0x00D7E62A, {0xC7, 0x05, 0x60, 0x43, 0x23, 0x01, 0x60, 0xFD}, 8, 6},  // mov [GC_notify_event],0xC0FD60
};
static const u8 kMxCoePro[7] = {0x83, 0x3D, 0x9C, 0x2F, 0x1F, 0x01, 0x00};  // cmp dword [GC_incremental],0

static double MxIniDouble(const wchar_t* sec, const wchar_t* key, double def, const wchar_t* ini) {
    wchar_t b[32];
    GetPrivateProfileStringW(sec, key, L"", b, 32, ini);
    return b[0] ? _wtof(b) : def;
}

// [MemDiag] TypeGrowth=1 and/or [GC] SqrtHeapLimit=1|2 install the hooks; ts3native_counters tyMode / sqMode switch them
// at run time (--ab typegrowth). SqrtHeapLimit 2 = observe: limit computed and logged, the heap policy stays the game's.
static bool ApplyMemExp(const wchar_t* ini) {
    Ts3nCounters& k = ts3native_counters;
    const int tg = GetPrivateProfileIntW(L"MemDiag", L"TypeGrowth", 0, ini), sh = GetPrivateProfileIntW(L"GC", L"SqrtHeapLimit", 0, ini);
    if (!tg && !sh) { Log("mem experiments off (TypeGrowth=0, SqrtHeapLimit=0)"); return false; }
    g_tgEvery = GetPrivateProfileIntW(L"MemDiag", L"TypeGrowthEvery", 1, ini);
    g_tgTopK = Clamp(GetPrivateProfileIntW(L"MemDiag", L"TypeGrowthTopK", 8, ini), 0, MX_TOPK);
    g_tgRankMin = MxIniDouble(L"MemDiag", L"TypeGrowthRankMin", 100.0, ini);
    g_tgGraphEvery = Clamp(GetPrivateProfileIntW(L"MemDiag", L"TypeGrowthGraphEvery", 10, ini), 0, 1000);
    g_shCPerMB = MxIniDouble(L"GC", L"SqrtC", 0.0005, ini);
    g_shFloor = (DWORD)Clamp(GetPrivateProfileIntW(L"GC", L"InitialHeapMB", 256, ini), 40, 1024) << 20;
    g_shCeil = (DWORD)Clamp(GetPrivateProfileIntW(L"GC", L"SqrtCeilingMB", 1024, ini), 64, 2048) << 20;
    if (g_shCeil < g_shFloor) g_shCeil = g_shFloor;
    g_shMinExtra = (DWORD)Clamp(GetPrivateProfileIntW(L"GC", L"SqrtMinExtraMB", 16, ini), 2, 256) << 20;
    g_shStepBlocks = (DWORD)Clamp(GetPrivateProfileIntW(L"GC", L"GrowthStepMB", 32, ini), 8, 256) * 256;
    g_shLogMs = (DWORD)Clamp(GetPrivateProfileIntW(L"GC", L"SqrtLogSec", 10, ini), 1, 600) * 1000;
    if (tg) {
        g_mxs = (MxState*)VirtualAlloc(NULL, sizeof(MxState), MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
        g_mxPage = (u8*)VirtualAlloc(NULL, 1 << 18, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
        if (!g_mxs || !g_mxPage) { Log("SKIP type growth: no memory for its tables"); g_mxs = NULL; }
    }
    if (!g_mxPage) g_mxPage = (u8*)VirtualAlloc(NULL, 1 << 18, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    // the notify pointer: the immediate MonoScriptHost init stores, or the pointer itself when init already ran
    DWORD* slot = (DWORD*)(uintptr_t)0x01234360;
    bool hooked = PatchImm32("GC_notify_event (mem experiments)", kMxNotifySite, 1, (DWORD)(uintptr_t)MxNotify);
    if (Readable(slot, 4) && *slot == 0x00C0FD60) {
        const DWORD v = (DWORD)(uintptr_t)MxNotify;
        hooked = Write(slot, &v, 4) || hooked;
    }
    if (!hooked) { Log("SKIP mem experiments: GC_notify_event site differs"); return false; }
    if (sh) {
        const CallSite cs = {0x00D819AA, 0x00E4A050, (void*)MxStep};
        g_mxStepOrig = (MxStepFn)(uintptr_t)0x00E4A050;
        if (!PatchCalls("per-frame GC_try_to_collect timing (mem experiments)", &cs, 1)) g_mxStepOrig = NULL;
        g_mxCoeOrig = (MxCoeFn)InstallDetour("GC_collect_or_expand (SqrtHeapLimit)", 0x00E4A120, kMxCoePro, sizeof(kMxCoePro), (void*)MxCoe, &g_mxCoeOrig);
        if (!g_mxStepOrig || !g_mxCoeOrig) Log("note: SqrtHeapLimit without %s: GC time undercounted%s", g_mxStepOrig ? "the collect_or_expand detour" : "the per-frame timing",
                                               g_mxCoeOrig ? "" : ", heap policy unchanged");
    }
    k.tyMode = tg && g_mxs ? 1 : 0;
    k.sqMode = !sh ? 0 : (sh == 1 && g_mxCoeOrig) ? 1 : 2;
    k.mxReady = (hooked ? 1 : 0) | (g_mxs ? 2 : 0) | (g_mxStepOrig ? 4 : 0) | (g_mxCoeOrig ? 8 : 0);
    Log("mem experiments: TypeGrowth=%ld (every %d, top %d, graph every %d, rank min %.0f) SqrtHeapLimit=%ld (c=%.6f/MB, floor %lu MB, ceiling %lu MB, min extra %lu MB, step %lu MB) ready=0x%lX",
        k.tyMode, g_tgEvery, g_tgTopK, g_tgGraphEvery, g_tgRankMin, k.sqMode, g_shCPerMB, g_shFloor >> 20, g_shCeil >> 20, g_shMinExtra >> 20,
        g_shStepBlocks / 256, k.mxReady);
    return true;
}
