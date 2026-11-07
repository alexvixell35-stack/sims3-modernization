// [MemDiag] (docs/190 phase 1, docs/191 module 1): one "diag: mem" line after every [UserDiag] "memory at" line, numbers
// only, plus an opt-in self-test that proves each field moves by a known injected size. Included by ts3native.cpp right
// after userdiag.cpp (it reuses UdMemStat / UdMemWalk / UdMemSample / the memory worker; no second walk code).
//   diag: mem #4 load ev=travel,world-switch priv=1851 wPriv=1830 wCom=2167 free=1646 big=1525? img=310? map=120 stkRes=140
//     stkCom=9.1? miRes=320 miCom=210 eaDir=22.0? eaDirN=3? eaCom=612? eaRes=640? eaCoreN=46? rp=20.1 pl=96.0? sp=7.0? gcHeap=360? gcFree=151? gcN=812?
//     rc=388? rcCold=14.2? rcBudget=200? rcN=41000? anim=4.1? rest=402? t=734   (t = seconds since the game started)
// MB unless the key ends in N (count) or is gcN. "-" = not readable (no game, feature off). Suffix "?" = no validation has
// passed for this field (read from a documented address, unproven), "!" = it failed this session's self-test; no suffix =
// a self-test check passed (host selftest for walk / OS / routesnap fields, or this session's game self-test).
// eaCom/eaRes/eaCoreN: EA GeneralAllocator cores (MemoryMan heap + the script heap when it is a separate object), committed /
// reserved MB and count (docs/209 #96). rc is cache bytes, not 32-bit address space: under DXVK most texture data does not
// sit in TS3W's address space (docs/40 §4-5), so rc falling does not mean free space rising by as much.
// Validation in docs/191 "Goal: a correct memory measuring tool first".
#include <initializer_list>

static bool g_mdOn;
static volatile LONG g_mdSelfTest;  // 0 off, 1 pending (game: 30 s after the first world load), 2 done
static volatile LONG g_mdN, g_mdLegend, g_mdMarks;
static DWORD g_mdWorldTick;         // first world load seen by the worker (GetTickCount)
static const DWORD kMdSelfTestDelayMs = 30000;

enum {
    MD_PRIV, MD_WPRIV, MD_WCOM, MD_FREE, MD_BIG, MD_IMG, MD_MAP, MD_STKRES, MD_STKCOM, MD_MIRES, MD_MICOM, MD_EADIR, MD_EADIRN,
    MD_EACOM, MD_EARES, MD_EACOREN, MD_RP, MD_PL, MD_SP, MD_GCHEAP, MD_GCFREE, MD_GCN, MD_RC, MD_RCCOLD, MD_RCBUD, MD_RCN, MD_ANIM, MD_REST, MD_N
};
// check: 'H' = a check in the host selftest (selftest-all, every build) and in the game self-test, 'G' = game self-test
// only (needs the EA heap), 0 = no check: unvalidated until docs/191's later modules add one.
struct MdDef { const char* key; bool count; char check; };
static const MdDef kMd[MD_N] = {
    {"priv", 0, 'H'},   {"wPriv", 0, 'H'},  {"wCom", 0, 'H'},  {"free", 0, 'H'},    {"big", 0, 0},     {"img", 0, 0},
    {"map", 0, 'H'},    {"stkRes", 0, 'H'}, {"stkCom", 0, 0},  {"miRes", 0, 'H'},   {"miCom", 0, 'H'}, {"eaDir", 0, 'G'},
    {"eaDirN", 1, 'G'}, {"eaCom", 0, 0},    {"eaRes", 0, 0},     {"eaCoreN", 1, 0}, {"rp", 0, 'H'},     {"pl", 0, 0},      {"sp", 0, 0},        {"gcHeap", 0, 0},  {"gcFree", 0, 0},
    {"gcN", 1, 0},      {"rc", 0, 0},       {"rcCold", 0, 0},  {"rcBudget", 0, 0},  {"rcN", 1, 0},     {"anim", 0, 0},
    {"rest", 0, 0},
};
// 'G' fields count as validated in every session once mem-diag-verify.json passed on this build (set true then).
static bool g_mdGameVerified = false;  // not const: no constant-condition warning
static volatile LONG g_mdPass, g_mdFail;  // bit per field: this session's self-test result

// game addresses (the host selftest points them at fakes)
static const u8* const* g_mdEaHeapSlot = (const u8* const*)(uintptr_t)0x011CB864;  // MemoryMan heap (docs/re/structs.md §9)
static const u8* const* g_mdRcMgrSlot = (const u8* const*)(uintptr_t)0x011C78D8;   // resource manager (structs.md §10)
static const DWORD* g_mdGc[3] = {(const DWORD*)(uintptr_t)0x012225A0, (const DWORD*)(uintptr_t)0x012225B4,
                                 (const DWORD*)(uintptr_t)0x011F2F90};  // GC_heapsize, GC_large_free_bytes, GC_gc_no (docs/66 §6.1)

// GeneralAllocator instances whose cores are summed: MemoryMan [0x11CB864] and the script host's [0x11D4BC8] (docs/130
// I-130-7: a dedicated heap or the same object; the same object is counted once)
static const u8* const* g_mdEaCoreSlot[2] = {(const u8* const*)(uintptr_t)0x011CB864, (const u8* const*)(uintptr_t)0x011D4BC8};

struct MdVals { double v[MD_N]; bool have[MD_N]; };

static bool MdEaDirect(DWORD* bytes, DWORD* count) {  // heap +0x488 count / +0x48C bytes of direct-mapped blocks
    __try {
        const u8* h = *g_mdEaHeapSlot;
        if (!h) return false;
        *count = *(const volatile DWORD*)(h + 0x488);
        *bytes = *(const volatile DWORD*)(h + 0x48C);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}
// Cores (AddCore 0x4E4E30, exe bytes): a ring through core +0x18 (next) whose head node is heap +0x448 (first core =
// [heap+0x460]); core +4 committed bytes, +8 reserved bytes. Read without the heap lock like eaDir: a core added or
// trimmed mid-walk costs one sample's accuracy; a broken ring ends at the cap or the SEH guard (no field then).
static bool MdEaCores(double* com, double* res, DWORD* n) {
    __try {
        *com = *res = 0, *n = 0;
        const u8* seen = NULL;
        for (const u8* const* slot : g_mdEaCoreSlot) {
            const u8* h = *slot;
            if (!h || h == seen) continue;
            seen = h;
            const u8* c = *(const u8* const volatile*)(h + 0x460);
            for (; c && c != h + 0x448; c = *(const u8* const volatile*)(c + 0x18)) {
                if (++*n > 4096) return false;
                *com += *(const volatile DWORD*)(c + 4), *res += *(const volatile DWORD*)(c + 8);
            }
        }
        return seen != NULL;
    } __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}
static bool MdGc(DWORD* out) {
    __try {
        for (int i = 0; i < 3; i++) out[i] = *(const volatile DWORD*)g_mdGc[i];
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}
// ResourceCache (priority 0) and AnimationCache (priority 1000), both vtable 0xFFDD94, from the manager's cache array
// +0xA0/+0xA4 of {cache*, priority} (as verify.cpp VfPollOnce). Plain aligned dword reads, no lock (docs/190 §5.4).
// out: held, cold, budget, entries, anim held; anim = ~0 when not found.
static bool MdRc(DWORD* out, const u8** cache = NULL) {  // cache: the priority-0 ResourceCache (RcCensus)
    __try {
        const u8* m = *g_mdRcMgrSlot;
        if (!m || (*(const DWORD*)m != 0xFFE250 && *(const DWORD*)m != 0xFB2DA0)) return false;
        const DWORD* b = *(const DWORD* const*)(m + 0xA0);
        const DWORD* e = *(const DWORD* const*)(m + 0xA4);
        bool rc = false;
        out[4] = ~0u;
        for (int i = 0; b + 2 * i < e && i < 16; i++) {
            const u8* c = (const u8*)(uintptr_t)b[2 * i];
            if (!c || *(const DWORD*)c != 0xFFDD94) continue;
            if (b[2 * i + 1] == 0 && !rc) {
                out[0] = *(const volatile DWORD*)(c + 0x94), out[1] = *(const volatile DWORD*)(c + 0x98);
                out[2] = *(const volatile DWORD*)(c + 0x8C), out[3] = *(const volatile DWORD*)(c + 0x28);
                rc = true;
                if (cache) *cache = c;
            } else if (b[2 * i + 1] == 1000 && out[4] == ~0u) out[4] = *(const volatile DWORD*)(c + 0x94);
        }
        return rc;
    } __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}

// [MemDiag] RcCensus=1 (2026-11-05, docs/191 "ResourceCache census"): which entries make rcN grow. Off by default.
// rcN grew 100k -> 348k in a 60-min old-save run while rc stayed ~500-600 MB, and every erase path ends in the node erase
// 0x732130 [verified disassembly], so the entries that stay are referenced or not evictable. At each diag: mem line (not
// exit) this takes the cache's own lock, walks the map and logs the types with the most entries and the most growth since
// the last census. The game waits for the lock during the walk: one hitch per sample (the line gives walk ms).
// Layout [verified disassembly: Add 0x732A00, find 0xAD3F40, AddRef 0x731DB0, cold test 0x731E30, lock 0x4E16F0]: lock =
// CRITICAL_SECTION at cache +0x58 (its wrapper's enabled byte +0x7C); map at +0x1C: buckets +0x20 (one more slot, a
// sentinel), bucket count +0x24, entries +0x28; node: key 16 B (instance 8, type 4, group 4), resource +0x10, next +0x18;
// resource: key +8, refs = [+4] >> 8 (the cache holds one), cold-list links +0x18 / +0x1C (either set = cold), size +0x20.
static bool g_rcCensusOn;
static DWORD g_rcLockWaitMs = 500;  // selftest: shorter
struct RcType { DWORD type, n, cold, ref1, ref2, ref3, ref1Held, g0, g0n; u64 bytes, inst0; LONG dn; double dmb; };  // g0 / inst0: the first entry's group and instance
struct RcTotals { DWORD walked, mapN, types, other, keyBad; };
static const int RC_SLOTS = 256, RC_MAXTYPES = 200, RC_TOP = 10, RC_GROW = 5;
static RcType g_rcPrev[RC_SLOTS];
static int g_rcPrevN;
static volatile LONG g_rcBusy;
// RcCensusGraph (below): the walk lists the part compositors here
static bool g_rgOn;
static DWORD g_rgVt[2] = {0x00FE68D8, 0x00FE68F4};  // TXTC 0x033A1435, 0x0341ACC9; selftest: its own values
static const DWORD RG_CAP = 1u << 19;
static DWORD *g_rgObj, g_rgN, g_rgOver;
// under the lock, no C++ objects (SEH). false = map not readable, or longer than its count (a changing or wrong map)
static bool RcWalk(const u8* c, RcType* t, RcTotals& z) {
    __try {
        const DWORD* bk = *(const DWORD* const*)(c + 0x20);
        const DWORD nb = *(const DWORD*)(c + 0x24);
        z.mapN = *(const DWORD*)(c + 0x28);
        if (!bk || !nb || nb > (1u << 24)) return false;
        const DWORD limit = z.mapN + z.mapN / 8 + 64;
        for (DWORD i = 0; i < nb; i++)
            for (const u8* nd = (const u8*)(uintptr_t)bk[i]; nd; nd = (const u8*)(uintptr_t) * (const DWORD*)(nd + 0x18)) {
                if (++z.walked > limit) return false;
                const DWORD type = *(const DWORD*)(nd + 8);
                DWORD h = (type * 2654435761u) >> 24;
                while (t[h].n && t[h].type != type) h = (h + 1) & (RC_SLOTS - 1);
                if (!t[h].n) {
                    if (z.types == RC_MAXTYPES) { z.other++; continue; }
                    t[h].type = type, z.types++;
                }
                RcType& e = t[h];
                if (!e.n++) e.g0 = *(const DWORD*)(nd + 12), e.inst0 = *(const u64*)nd;
                e.g0n += *(const DWORD*)(nd + 12) == e.g0;
                const u8* r = (const u8*)(uintptr_t) * (const DWORD*)(nd + 0x10);
                if (!r) continue;
                if (g_rgObj && (*(const DWORD*)r == g_rgVt[0] || *(const DWORD*)r == g_rgVt[1])) {
                    if (g_rgN < RG_CAP) g_rgObj[g_rgN++] = (DWORD)(uintptr_t)r;
                    else g_rgOver++;
                }
                if (memcmp(r + 8, nd, 16)) z.keyBad++;
                const LONG refs = *(const LONG*)(r + 4) >> 8;
                const bool cold = *(const DWORD*)(r + 0x18) || *(const DWORD*)(r + 0x1C);
                e.bytes += *(const DWORD*)(r + 0x20), e.cold += cold;
                if (refs <= 1) e.ref1++, e.ref1Held += !cold;
                else if (refs == 2) e.ref2++;
                else e.ref3++;
            }
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}
static int RcByN(const void* a, const void* b) { const DWORD x = ((const RcType*)a)->n, y = ((const RcType*)b)->n; return x < y ? 1 : x > y ? -1 : 0; }
static int RcByDn(const void* a, const void* b) { const LONG x = ((const RcType*)a)->dn, y = ((const RcType*)b)->dn; return x < y ? 1 : x > y ? -1 : 0; }
static bool RcLockOn(const u8*& c) {  // the wrapper's enabled byte; c = NULL when not readable
    __try { return *(const volatile u8*)(c + 0x7C) != 0; } __except (EXCEPTION_EXECUTE_HANDLER) { c = NULL; return false; }
}
static bool RcEnter(CRITICAL_SECTION* cs, DWORD* wait) {  // false = busy for g_rcLockWaitMs
    const DWORD t0 = GetTickCount();
    bool in;
    while (!(in = TryEnterCriticalSection(cs) != 0) && GetTickCount() - t0 < g_rcLockWaitMs) Sleep(1);
    *wait = GetTickCount() - t0;
    return in;
}

// [MemDiag] RcCensusHolders=K (2026-11-05, with RcCensus): once, at the K-th census, who else points at the entries of the
// type that grew most since the census before. Under the lock it samples up to 64 of that type's resources spread over the
// map; after unlocking it scans every committed readable region for dwords within 0x40 bytes of a sample (a holder may
// keep another interface of the object) and names each one's holder by the nearest TS3W.exe vtable at or below it (an
// .rdata dword whose first slot is in .text, up to 0x200 bytes back, same region). The cache's own nodes, the samples'
// own bytes and our tables do not count. Counts by (vtable, offset, delta) and how many samples each covers. Reads only;
// once, ~1-3 s on the memory worker. Not a proof: a stale or unrelated dword can match.
static DWORD g_rcHoldersAt, g_rcCensusN;
static const int RH_N = 64, RH_KEYS = 64;
static const u8* g_rhRes[RH_N];
static const u8* g_rhNode[RH_N];
static DWORD g_rhSorted[RH_N], g_rhBits[1 << 15];  // samples by address; one bit per 4 KB page that a sample range touches
struct RhKey { DWORD vt, off; LONG delta; DWORD n; u64 mask; };
struct RhStats { DWORD hits, own, novt, keysFull, img, priv, map; u64 bytes; DWORD novtAt[3], novtBase[3]; };
static RhKey g_rhKeys[RH_KEYS];
static int g_rhKeyN;
static DWORD g_rhTxLo, g_rhTxHi, g_rhRdLo, g_rhRdHi;
static bool RhRanges() {  // this process's exe .text / .rdata (the selftest's own in the host test)
    if (g_rhTxHi) return g_rhRdHi != 0;
    const u8* b = (const u8*)GetModuleHandleW(NULL);
    const IMAGE_NT_HEADERS* nt = (const IMAGE_NT_HEADERS*)(b + ((const IMAGE_DOS_HEADER*)b)->e_lfanew);
    const IMAGE_SECTION_HEADER* s = IMAGE_FIRST_SECTION(nt);
    for (int i = 0; i < nt->FileHeader.NumberOfSections; i++, s++) {
        const DWORD lo = (DWORD)(uintptr_t)(b + s->VirtualAddress), hi = lo + s->Misc.VirtualSize;
        if (!memcmp(s->Name, ".text", 6)) g_rhTxLo = lo, g_rhTxHi = hi;
        if (!memcmp(s->Name, ".rdata", 7)) g_rhRdLo = lo, g_rhRdHi = hi;
    }
    return g_rhTxHi && g_rhRdHi;
}
static int RhSample(const u8* c, DWORD type, DWORD every) {  // under the lock
    int k = 0;
    DWORD w = 0, seen = 0;
    __try {
        const DWORD* bk = *(const DWORD* const*)(c + 0x20);
        const DWORD nb = *(const DWORD*)(c + 0x24), cnt = *(const DWORD*)(c + 0x28), limit = cnt + cnt / 8 + 64;
        for (DWORD i = 0; bk && i < nb && i < (1u << 24) && k < RH_N; i++)
            for (const u8* nd = (const u8*)(uintptr_t)bk[i]; nd && k < RH_N; nd = (const u8*)(uintptr_t) * (const DWORD*)(nd + 0x18)) {
                if (++w > limit) return k;
                if (*(const DWORD*)(nd + 8) != type || seen++ % every) continue;
                const u8* r = (const u8*)(uintptr_t) * (const DWORD*)(nd + 0x10);
                if (r) g_rhNode[k] = nd, g_rhRes[k++] = r;
            }
    } __except (EXCEPTION_EXECUTE_HANDLER) {}
    return k;
}
static int RhFind(DWORD v, int k) {  // the sample nearest to v within 0x40 bytes (heap objects can sit closer), or -1
    int lo = 0, hi = k, best = -1;
    while (lo < hi) { const int m = (lo + hi) / 2; if (g_rhSorted[m] + 0x40 < v) lo = m + 1; else hi = m; }
    for (DWORD bd = 0x41; lo < k && g_rhSorted[lo] - 0x40 <= v; lo++) {
        const DWORD d = v > g_rhSorted[lo] ? v - g_rhSorted[lo] : g_rhSorted[lo] - v;
        if (d < bd) bd = d, best = lo;
    }
    return best;
}
static void RhHit(DWORD p, DWORD v, int j, int k, const MEMORY_BASIC_INFORMATION& m, RhStats& st) {
    const DWORD base = g_rhSorted[j];
    int si = 0;
    while (si < k && (DWORD)(uintptr_t)g_rhRes[si] != base) si++;
    if (si == k || p - base + 0x40 < 0xC0) return;  // inside the sample object itself
    if (p == (DWORD)(uintptr_t)g_rhNode[si] + 0x10) { st.own++; return; }
    if ((p >= (DWORD)(uintptr_t)g_rhRes && p < (DWORD)(uintptr_t)(g_rhRes + RH_N)) || (p >= (DWORD)(uintptr_t)g_rhSorted && p < (DWORD)(uintptr_t)(g_rhSorted + RH_N))) return;
    st.hits++;
    if (m.Type == MEM_IMAGE) st.img++; else if (m.Type == MEM_MAPPED) st.map++; else st.priv++;
    const DWORD rb = (DWORD)(uintptr_t)m.BaseAddress;
    DWORD vt = 0, q = p & ~3u;
    for (; q >= rb && p - q <= 0x200; q -= 4) {
        const DWORD x = *(const DWORD*)(uintptr_t)q;
        if (x >= g_rhRdLo && x + 4 <= g_rhRdHi && !(x & 3)) {
            const DWORD f = *(const DWORD*)(uintptr_t)x;
            if (f >= g_rhTxLo && f < g_rhTxHi) { vt = x; break; }
        }
        if (q < 4) break;
    }
    if (!vt) {
        if (st.novt < 3) st.novtAt[st.novt] = p, st.novtBase[st.novt] = (DWORD)(uintptr_t)m.AllocationBase;
        st.novt++;
        return;
    }
    const DWORD off = p - q;
    const LONG delta = (LONG)(v - base);
    int i = 0;
    while (i < g_rhKeyN && !(g_rhKeys[i].vt == vt && g_rhKeys[i].off == off && g_rhKeys[i].delta == delta)) i++;
    if (i == g_rhKeyN) {
        if (g_rhKeyN == RH_KEYS) { st.keysFull++; return; }
        g_rhKeys[g_rhKeyN++] = RhKey{vt, off, delta, 0, 0};
    }
    g_rhKeys[i].n++, g_rhKeys[i].mask |= 1ull << si;
}
static bool RhRegion(const MEMORY_BASIC_INFORMATION& m, int k, RhStats& st) {  // false = faulted (region changed)
    const DWORD* p = (const DWORD*)m.BaseAddress;
    const DWORD* e = (const DWORD*)((const u8*)m.BaseAddress + m.RegionSize);
    __try {
        for (; p < e; p++) {
            const DWORD v = *p;
            if (!(g_rhBits[v >> 17] & (1u << ((v >> 12) & 31)))) continue;
            const int j = RhFind(v, k);
            if (j >= 0) RhHit((DWORD)(uintptr_t)p, v, j, k, m, st);
        }
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}
static int RhByN(const void* a, const void* b) { const DWORD x = ((const RhKey*)a)->n, y = ((const RhKey*)b)->n; return x < y ? 1 : x > y ? -1 : 0; }
// committed, readable, not uncached / write-combined (very slow reads), not our own stack
static bool RhScannable(const MEMORY_BASIC_INFORMATION& m, const MEMORY_BASIC_INFORMATION& own) {
    return m.State == MEM_COMMIT && !(m.Protect & (PAGE_GUARD | PAGE_NOACCESS | PAGE_NOCACHE | PAGE_WRITECOMBINE)) && (m.Protect & 0xEE) &&
           m.AllocationBase != own.AllocationBase;
}

// [MemDiag] RcCensusBuilders=1 (2026-11-05, with RcCensus): at each census, the live CAS SimBuilders and what their parts
// hold. The census showed the growing TXTC 0x033A1435 / 0x0341ACC9 entries are made in memory with random instances
// (0x5DFDB0) by SetPartCompositor / SetPartPreset (0x5E2C10, allocation tag "CAS/SimBuilder/SetPartCompositor") and
// kept as RefPtrs in a hash map per part [verified disassembly]. SimBuilder: vtable 0xFE46A0 (ctor 0x5E4150); part list
// at +0x1E0 (doubly linked, node next +0, prev +4, list name pointer +0x1E8 = 0xFE4668 "CAS/SimBuilder/PartList");
// part node 0x98 B (0x5E4100) with its compositor map at +0x78 (EASTL hash map: buckets +4, bucket count +8, count +0xC;
// node key dword +0, RefPtr +4, next +8: 0x5E2B30). Counts objects whose vtable and list name match and whose list links
// are consistent. Answers: more SimBuilders over time (not freed) or more compositors in the same ones.
static bool g_rbOn;
static DWORD g_rbVt = 0x00FE46A0, g_rbName = 0x00FE4668;  // selftest: its own values
struct RbTop { DWORD at, parts, comps; };
struct RbStats { DWORD n, bad, parts, comps; RbTop top[5]; u64 bytes; };
static void RbObject(const u8* o, RbStats& st) {  // its own __try: a bad candidate's links must not end the region scan
    DWORD parts = 0, comps = 0;
    __try {
        if (*(const DWORD*)(o + 0x1E8) != g_rbName) return;
        const u8* head = o + 0x1E0;
        const u8* prev = head;
        for (const u8* nd = *(const u8* const*)head; nd != head; prev = nd, nd = *(const u8* const*)nd) {
            if (!nd || *(const u8* const*)(nd + 4) != prev || ++parts > 4096 || *(const DWORD*)(nd + 0x84) > 1000000) { st.bad++; return; }
            comps += *(const DWORD*)(nd + 0x84);
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) { st.bad++; return; }
    st.n++, st.parts += parts, st.comps += comps;
    int i = 4;
    if (comps <= st.top[4].comps) return;
    while (i > 0 && comps > st.top[i - 1].comps) st.top[i] = st.top[i - 1], i--;
    st.top[i] = RbTop{(DWORD)(uintptr_t)o, parts, comps};
}
static bool RbRegion(const MEMORY_BASIC_INFORMATION& m, RbStats& st) {  // false = faulted (region changed)
    const DWORD* p = (const DWORD*)m.BaseAddress;
    const DWORD* e = (const DWORD*)((const u8*)m.BaseAddress + m.RegionSize - 0x200);  // the object's fields stay in the region
    __try {
        for (; p < e; p++)
            if (*p == g_rbVt) RbObject((const u8*)p, st);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}
// [MemDiag] RcCensusGraph=1 (2026-11-05, with RcCensus): do the part compositors hold each other? A TXTC resource keeps
// RefPtrs to the compositors its stream names in an EASTL vector at +0x28 (begin, end +0x2C), filled by its reader 0x607070
// through the cache find (slot 6) and kept until it dies; the TXTF class (vtable 0xFE68F4) shares the reader (slot 5
// 0x607970) and the 0x68-byte layout [verified disassembly; ctor 0x6004C0]. al218 (2026-11-05): 25-30k such finds per
// 5 min, and the census sees every compositor at 2+ references with ref1held 0. The census walk lists the entries with
// either vtable; after unlocking, this counts the edges between them, the references each has beyond the cache's and its
// compositor parents', the roots (no compositor parent) by reference count, and what a Kahn peel from the roots leaves:
// nodes on or only below a cycle, which nothing but the world's teardown frees. Reads only; a vector that changes during
// the pass can miscount. 6.5 MB once; tens of ms on the memory worker, without the cache lock.
static const DWORD RG_SIZE = 0x68, RG_KIDS = 256;
static DWORD *g_rgDeg, *g_rgQ;
static u8* g_rgMore;
struct RgStats { DWORD n, kids, edges, edgesC, self, other, nulls, badVec, more, moreSum, fewer, roots, root1, root2, root3, left, leftMore; };
static int RgCmp(const void* a, const void* b) { const DWORD x = *(const DWORD*)a, y = *(const DWORD*)b; return x < y ? -1 : x > y; }
static int RgFind(const DWORD* o, DWORD n, DWORD p) {  // the listed object that contains p (another interface of it too), or -1
    DWORD lo = 0, hi = n;
    while (lo < hi) { const DWORD m = (lo + hi) / 2; if (o[m] <= p) lo = m + 1; else hi = m; }
    return lo && p - o[lo - 1] < RG_SIZE ? (int)lo - 1 : -1;
}
static int RgVec(DWORD obj, const DWORD*& b) {  // child count; -1 = not readable or not a plausible vector
    __try {
        b = *(const DWORD* const*)(uintptr_t)(obj + 0x28);
        const DWORD* e = *(const DWORD* const*)(uintptr_t)(obj + 0x2C);
        if (!b && !e) return 0;
        if (!b || e < b || (DWORD)(e - b) > RG_KIDS) return -1;
        if (e > b) (void)*(const volatile DWORD*)(e - 1);
        return (int)(e - b);
    } __except (EXCEPTION_EXECUTE_HANDLER) { return -1; }
}
static LONG RgRefs(DWORD obj) {
    __try { return *(const LONG*)(uintptr_t)(obj + 4) >> 8; } __except (EXCEPTION_EXECUTE_HANDLER) { return 0; }
}
static DWORD RgKid(const DWORD* b, int j) {  // 0 = null or gone
    __try { return b[j]; } __except (EXCEPTION_EXECUTE_HANDLER) { return 0; }
}
// o: n listed objects (sorted here); deg / q: n DWORDs each, more: n bytes
static void RgAnalyze(DWORD* o, DWORD n, DWORD* deg, DWORD* q, u8* more, RgStats& st) {
    qsort(o, n, sizeof(DWORD), RgCmp);
    memset(deg, 0, n * sizeof(DWORD)), memset(more, 0, n);
    st.n = n;
    for (DWORD i = 0; i < n; i++) {
        const DWORD* b;
        const int k = RgVec(o[i], b);
        if (k < 0) { st.badVec++; continue; }
        st.kids += k > 0;
        for (int j = 0; j < k; j++) {
            const DWORD p = RgKid(b, j);
            const int t = p ? RgFind(o, n, p) : -1;
            st.edges++;
            if (!p) st.nulls++;
            else if (t < 0) st.other++;
            else st.edgesC++, deg[t]++, st.self += (DWORD)t == i;
        }
    }
    DWORD qn = 0;
    for (DWORD i = 0; i < n; i++) {
        const LONG r = RgRefs(o[i]), x = r - 1 - (LONG)deg[i];  // beyond the cache's own and the compositor parents'
        if (x > 0) st.more++, st.moreSum += x, more[i] = 1;
        else if (x < 0) st.fewer++;
        if (deg[i]) continue;
        st.roots++, q[qn++] = i;
        if (r <= 1) st.root1++; else if (r == 2) st.root2++; else st.root3++;
    }
    for (DWORD h = 0; h < qn; h++) {  // Kahn: what the roots alone account for
        const DWORD* b;
        const int k = RgVec(o[q[h]], b);
        for (int j = 0; j < k; j++) {
            const DWORD p = RgKid(b, j);
            const int t = p ? RgFind(o, n, p) : -1;
            if (t >= 0 && deg[t] && !--deg[t]) q[qn++] = (DWORD)t;
        }
    }
    st.left = n - qn;
    for (DWORD i = 0; i < n; i++) st.leftMore += deg[i] && more[i];
}
static void RcGraph(LONG n) {
    LARGE_INTEGER f, a, b;
    QueryPerformanceFrequency(&f), QueryPerformanceCounter(&a);
    RgStats st = {};
    RgAnalyze(g_rgObj, g_rgN, g_rgDeg, g_rgQ, g_rgMore, st);
    QueryPerformanceCounter(&b);
    Log("diag: rc graph #%ld: %lu part compositors%s, %lu with children; %lu child refs: %lu to listed compositors (%lu self), %lu to others, "
        "%lu null; %lu vectors not readable; beyond the cache and compositor parents: %lu held more (+%lu), %lu fewer; %lu roots (refs 1: %lu, "
        "2: %lu, 3+: %lu); the peel from the roots leaves %lu on or below a cycle (%lu of them held more); %.1f ms",
        n, st.n, g_rgOver ? " (more not listed: table full)" : "", st.kids, st.edges, st.edgesC, st.self, st.other, st.nulls, st.badVec,
        st.more, st.moreSum, st.fewer, st.roots, st.root1, st.root2, st.root3, st.left, st.leftMore,
        (b.QuadPart - a.QuadPart) * 1000.0 / f.QuadPart);
}
static bool RgAlloc() {
    if (g_rgObj) return true;
    u8* m = (u8*)VirtualAlloc(NULL, RG_CAP * 13, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
    if (!m) return false;
    g_rgDeg = (DWORD*)(m + RG_CAP * 4), g_rgQ = (DWORD*)(m + RG_CAP * 8), g_rgMore = m + RG_CAP * 12;
    g_rgObj = (DWORD*)m;
    return true;
}

// [MemDiag] RcRefCallers=1 (2026-11-05, docs/191 "Who holds the part compositors"; diagnostic, test runs only; independent
// of RcCensus): who takes and drops references on the generated part compositors. The census showed them at 2+ references
// and never cold, while the CAS SimBuilder part maps that make them (0x5E2C10) hold only 0-40 and release them on part
// removal (0x5E2ED0 -> 0x515510) [verified]. Replaces AddRef / Release (slots 0 / 1: 0x723130 -> 0x731D00, 0x5B07A0 ->
// 0x731D40) in the TXTC 0x033A1435 vtable 0xFE68D8 and the 0x0341ACC9 vtable 0xFE68F4 with counters keyed by return
// address (only these two types reach the hooks), and the cache's Find (below). Each diag: mem line logs every caller's
// AddRef / Release / net since the start, the top 20 by net; the stack chains of the births (an AddRef at 0 references)
// and of the finds; the sampled objects alive at 2+ references by their last AddRef (RcRefHistory); the compositor
// service registry's size. Direct (non-virtual) calls are not seen. Counters v908 (rrc*).
static bool g_rsOn;
static const int RS_SLOTS = 1024;
static volatile LONG g_rsAt[RS_SLOTS], g_rsN[3][RS_SLOTS], g_rsFull;  // key = return address; [0] AddRef, [1] Release, [2] Find
static LONG g_rsPrev[3][RS_SLOTS], g_rsPrevNet[RS_SLOTS];
static DWORD g_rsPrevMs;
static double g_rsNs, g_rsChainNs;  // our work per counted call and per find chain, measured at install (RsCalibrate)
typedef LONG(__fastcall* RsFn)(void* self, void* edx);
static RsFn g_rsAdd = (RsFn)(uintptr_t)0x00731D00, g_rsRel = (RsFn)(uintptr_t)0x00731D40;
static void RsCount(int kind, DWORD key) {
    volatile LONG* at = g_rsAt;
    for (DWORD i = (key * 2654435761u) >> 22, k = 0; k < 64; i = (i + 1) & (RS_SLOTS - 1), k++) {
        const LONG a = at[i];
        if (a == (LONG)key || (!a && (InterlockedCompareExchange(&at[i], (LONG)key, 0) == 0 || at[i] == (LONG)key))) {
            InterlockedIncrement(&g_rsN[kind][i]);
            return;
        }
    }
    InterlockedIncrement(&g_rsFull);
}
// [MemDiag] RcRefCallersSample=N (with RcRefCallers; 0 = off): who keeps the one reference, per object. al221: no compositor cycles; nearly every compositor is a root at 2+ references (the cache's + one outside
// holder), the 2-reference group growing 34k -> 123k in 30 min; and only the cache's Find (0x732542) and Add (0x732AC5)
// AddRef directly [verified: every E8/E9 to 0x731DB0 / 0x731D00 / 0x731D40], so the outside holder's AddRef is a counted
// virtual one. This samples 1 in g_ryEvery new compositors (an AddRef at 0 references) and records the first RY_EV
// AddRef / Release / Find events on each with the caller (the return address); at each census the ones that lived through
// a census and still have 2+ references are grouped by their event lists, and by their last AddRef (the reference taken
// last and not dropped since: in the pattern seen so far that is the holder's). shortcut: a freed sample's slot is not
// reused (a new object at its address is not sampled): 16384 slots hold one 35-min run at 1 in 32.
static bool g_ryOn;
static DWORD g_ryEvery = 32;  // selftest: 1
static const int RY_SLOTS = 16384, RY_EV = 16;
struct RyObj { volatile LONG obj, born, n, dead, reg; DWORD ev[RY_EV]; };  // ev: caller | kind << 30 (0 AddRef, 1 Release, 2 Find); reg: last line its pointer was in the registry
static RyObj g_ry[RY_SLOTS];
static volatile LONG g_ryNew, g_ryTaken, g_ryFull, g_ryCensus;
static LONG RyRefs(DWORD o) {
    __try { return *(const LONG*)(uintptr_t)(o + 4) >> 8; } __except (EXCEPTION_EXECUTE_HANDLER) { return -1; }
}
static DWORD RyVt(DWORD o) {
    __try { return *(const DWORD*)(uintptr_t)o; } __except (EXCEPTION_EXECUTE_HANDLER) { return 0; }
}
static RyObj* RySlot(DWORD o, bool claim) {
    for (DWORD i = (o * 2654435761u) >> 20, k = 0; k < 32; i = (i + 1) & (RY_SLOTS - 1), k++) {
        const LONG a = g_ry[i].obj;
        if (a == (LONG)o) return &g_ry[i];
        if (a) continue;
        if (!claim) return NULL;
        if (InterlockedCompareExchange(&g_ry[i].obj, (LONG)o, 0) == 0) {
            g_ry[i].born = g_ryCensus;
            InterlockedIncrement(&g_ryTaken);
            return &g_ry[i];
        }
        if (g_ry[i].obj == (LONG)o) return &g_ry[i];
    }
    if (claim) InterlockedIncrement(&g_ryFull);
    return NULL;
}
static void RyNote(DWORD o, DWORD at, int kind) {  // before the original runs: refs is the count before this event
    RyObj* r = RySlot(o, false);
    if (!r) {
        if (kind || RyRefs(o) != 0 || (DWORD)InterlockedIncrement(&g_ryNew) % g_ryEvery) return;
        if (!(r = RySlot(o, true))) return;
    }
    if (r->dead) return;
    const LONG k = InterlockedIncrement(&r->n) - 1;
    if (k < RY_EV) r->ev[k] = (at & 0x3FFFFFFF) | (DWORD)kind << 30;
    if (kind == 1 && RyRefs(o) <= 1) r->dead = 1;  // this Release frees it
}
static LONG __fastcall RsAddRef(void* self, void* edx) {
    const DWORD at = (DWORD)(uintptr_t)_ReturnAddress();
    RsCount(0, at);
    if (g_ryOn && self) RyNote((DWORD)(uintptr_t)self, at, 0);
    return g_rsAdd(self, edx);
}
static LONG __fastcall RsRelease(void* self, void* edx) {
    const DWORD at = (DWORD)(uintptr_t)_ReturnAddress();
    RsCount(1, at);
    if (g_ryOn && self) RyNote((DWORD)(uintptr_t)self, at, 1);
    return g_rsRel(self, edx);
}
static DWORD g_rsVt[2] = {0x00FE68D8, 0x00FE68F4}, g_rsOrig[2] = {0x00723130, 0x005B07A0};  // selftest: its own
// The run of 2026-11-05 showed the counted AddRef / Release balance (+868 net while 28k compositors appeared), so both
// lasting references are taken directly: the cache's own (Add 0x732A00 -> 0x731DB0) and a lookup by key (cache vtable
// 0xFFDD94 slot 6 Find 0x7324E0, which AddRefs through 0x731DB0 [verified]). So Find is wrapped too: a find that returns
// a part compositor records its caller chain (the return address + the next 3 exe .text values up the stack).
typedef bool(__fastcall* RfFn)(void* self, void* edx, const void* key, void** out);
static RfFn g_rfFind = (RfFn)(uintptr_t)0x007324E0;
static DWORD g_rfVt = 0x00FFDD94, g_rfOrig = 0x007324E0;  // selftest: its own
static const int RF_SLOTS = 512;
struct RfRow { volatile LONG h, n; DWORD c[4]; };
static RfRow g_rsFs[RF_SLOTS];
static LONG g_rfPrev[RF_SLOTS], g_rfPrevHits;
static volatile LONG g_rfHits, g_rfFull;
static void RsFindChain(const DWORD* sp) {  // shortcut: a stack scan, so stale values can show; good enough to rank callers
    InterlockedIncrement(&g_rfHits);
    DWORD c[4] = {sp[0], 0, 0, 0};
    __try {
        for (int i = 1, m = 1; i < 80 && m < 4; i++)
            if (sp[i] >= g_rhTxLo && sp[i] < g_rhTxHi) c[m++] = sp[i];
    } __except (EXCEPTION_EXECUTE_HANDLER) {}
    DWORD h = 2166136261u;
    for (DWORD x : c) h = (h ^ x) * 16777619u;
    if (!h) h = 1;
    for (DWORD i = h & (RF_SLOTS - 1), k = 0; k < 64; i = (i + 1) & (RF_SLOTS - 1), k++) {
        if (g_rsFs[i].h == (LONG)h) { InterlockedIncrement(&g_rsFs[i].n); return; }
        if (!g_rsFs[i].h && InterlockedCompareExchange(&g_rsFs[i].h, (LONG)h, 0) == 0) {
            memcpy(g_rsFs[i].c, c, sizeof(c));
            InterlockedIncrement(&g_rsFs[i].n);
            return;
        }
        if (g_rsFs[i].h == (LONG)h) { InterlockedIncrement(&g_rsFs[i].n); return; }
    }
    InterlockedIncrement(&g_rfFull);
}
static bool __fastcall RsFind(void* self, void* edx, const void* key, void** out) {
    const bool r = g_rfFind(self, edx, key, out);
    if (r && out) {
        DWORD vt = 0;
        __try { vt = *out ? **(const DWORD* const*)out : 0; } __except (EXCEPTION_EXECUTE_HANDLER) {}
        if (vt && (vt == g_rsVt[0] || vt == g_rsVt[1])) {
            RsCount(2, (DWORD)(uintptr_t)_ReturnAddress());  // Find AddRefs what it returns (direct 0x731DB0)
            RsFindChain((const DWORD*)_AddressOfReturnAddress());
            if (g_ryOn && RySlot((DWORD)(uintptr_t)*out, false)) RyNote((DWORD)(uintptr_t)*out, (DWORD)(uintptr_t)_ReturnAddress(), 2);
        }
    }
    return r;
}
static void RsCalibrate() {  // before the hooks go in: time our part of a hooked call (fake key, an unsampled object), then clear
    static DWORD fake[2] = {0, 1 << 8};  // refs 1: RyNote's common path
    LARGE_INTEGER f, a, b;
    QueryPerformanceFrequency(&f), QueryPerformanceCounter(&a);
    for (volatile int i = 0; i < 20000; i++) {
        RsCount(0, 1);
        if (g_ryOn) RyNote((DWORD)(uintptr_t)fake, 1, 0);
    }
    QueryPerformanceCounter(&b);
    g_rsNs = (b.QuadPart - a.QuadPart) * 1e9 / f.QuadPart / 20000;
    QueryPerformanceCounter(&a);
    for (volatile int i = 0; i < 2000; i++) RsFindChain((const DWORD*)_AddressOfReturnAddress());
    QueryPerformanceCounter(&b);
    g_rsChainNs = (b.QuadPart - a.QuadPart) * 1e9 / f.QuadPart / 2000;
    memset((void*)g_rsAt, 0, sizeof(g_rsAt)), memset((void*)g_rsN, 0, sizeof(g_rsN)), memset(g_rsFs, 0, sizeof(g_rsFs));
    g_rsFull = g_rfFull = g_rfHits = 0;
}
static bool RsInstall() {
    for (DWORD vt : g_rsVt) {
        const DWORD* s = (const DWORD*)(uintptr_t)vt;
        if (!Readable(s, 8) || s[0] != g_rsOrig[0] || s[1] != g_rsOrig[1]) {
            Log("FAIL mem diag: rc ref callers: vtable 0x%08lX slots are not 0x%08lX / 0x%08lX, not installed", vt, g_rsOrig[0], g_rsOrig[1]);
            return false;
        }
    }
    DWORD* fs = (DWORD*)(uintptr_t)g_rfVt + 6;
    if (!Readable(fs, 4) || *fs != g_rfOrig || !RhRanges()) {
        Log("FAIL mem diag: rc ref callers: cache vtable 0x%08lX slot 6 is not 0x%08lX (or no exe .text), not installed", g_rfVt, g_rfOrig);
        return false;
    }
    RsCalibrate();
    const DWORD fn[2] = {(DWORD)(uintptr_t)&RsAddRef, (DWORD)(uintptr_t)&RsRelease}, ff = (DWORD)(uintptr_t)&RsFind;
    for (DWORD vt : g_rsVt)
        if (!Write((void*)(uintptr_t)vt, fn, sizeof(fn))) { Log("FAIL mem diag: rc ref callers: VirtualProtect at 0x%08lX", vt); return false; }
    if (!Write(fs, &ff, 4)) { Log("FAIL mem diag: rc ref callers: VirtualProtect at 0x%08lX", g_rfVt); return false; }
    return true;
}
static int RsByDn(const void* a, const void* b) { const LONG x = ((const LONG*)a)[1], y = ((const LONG*)b)[1]; return x < y ? 1 : x > y ? -1 : 0; }
struct RyPat { char s[RY_EV * 10 + 1]; DWORD c; LONG refs; };
static int RyByC(const void* a, const void* b) { const DWORD x = ((const RyPat*)a)->c, y = ((const RyPat*)b)->c; return x < y ? 1 : x > y ? -1 : 0; }
// The CAS texture compositor service ([0x11CE6A0], made in 0x5F16F0) registers each new compositor in a hash map at
// +0x104 (insert 0x5EECD0, from the compositor builder 0x5C1B30's callers; the node copy 0x5ED870 AddRefs at 0x5ED8B8).
// Erases: the clear-all 0x5EEA90 (the service's ctor and its vtable slot 5) and the per-key 0x5EE640 [verified code, TS3W
// 1.67]. eastl layout read here: buckets +0x108, bucket count +0x10C, entries +0x110; node = {ResourceKey 16 bytes,
// compositor +0x10, next +0x18} (next: the bucket free loop 0x5ED83F [verified]; +0x14 before 2026-11-05 stopped the walk).
// Read only, without the service's lock: a node freed during the walk can stop it (logged) or miscount one line.
static DWORD g_rrSvc = 0x011CE6A0, g_rrPrev;  // selftest: its own
static void RcRegistry(LONG n) {
    DWORD cnt = 0, nb = 0, seen = 0, comp = 0, r1 = 0, r2 = 0, r3 = 0, bad = 0, sampled = 0;
    __try {
        const u8* m = *(const u8* const*)(uintptr_t)g_rrSvc;
        if (!m) { Log("diag: rc registry #%ld: no compositor service", n); return; }
        const DWORD* b = *(const DWORD* const*)(m + 0x108);
        nb = *(const DWORD*)(m + 0x10C), cnt = *(const DWORD*)(m + 0x110);
        for (DWORD i = 0; b && i < nb && i < (1u << 22) && seen <= cnt + 64; i++)
            for (const u8* e = (const u8*)(uintptr_t)b[i]; e && seen <= cnt + 64; e = *(const u8* const*)(e + 0x18)) {
                seen++;
                const DWORD o = *(const DWORD*)(e + 0x10), vt = RyVt(o);
                if (vt != g_rsVt[0] && vt != g_rsVt[1]) continue;
                comp++;
                const LONG r = RyRefs(o);
                if (r <= 1) r1++; else if (r == 2) r2++; else r3++;
                if (g_ryOn)
                    if (RyObj* y = RySlot(o, false)) y->reg = n, sampled++;
            }
    } __except (EXCEPTION_EXECUTE_HANDLER) { bad = 1; }
    Log("diag: rc registry #%ld: %lu entries (%+ld since the last line, %lu buckets), walked %lu%s: %lu part compositors: refs 1: %lu, "
        "2: %lu, 3+: %lu; %lu sampled ones",
        n, cnt, (LONG)(cnt - g_rrPrev), nb, seen, bad ? " (stopped: unreadable)" : "", comp, r1, r2, r3, sampled);
    g_rrPrev = cnt, ts3native_counters.rrcRegistry = (LONG)cnt;
}

struct RyLast { DWORD ev, c, reg; };
static int RyLastByC(const void* a, const void* b) { const DWORD x = ((const RyLast*)a)->c, y = ((const RyLast*)b)->c; return x < y ? 1 : x > y ? -1 : 0; }
static void RcRefHistory(LONG n) {
    static RyPat pat[48];
    static RyLast last[32];
    int np = 0, nl = 0;
    DWORD dead = 0, young = 0, other = 0, alive = 0, r1 = 0, r2 = 0, r3 = 0, longer = 0, patFull = 0, net1 = 0, net1Reg = 0, lastFull = 0;
    for (int i = 0; i < RY_SLOTS; i++) {
        RyObj& y = g_ry[i];
        if (!y.obj) continue;
        if (y.dead) { dead++; continue; }
        if (y.born >= n) { young++; continue; }
        const DWORD vt = RyVt((DWORD)y.obj);
        if (vt != g_rsVt[0] && vt != g_rsVt[1]) { other++; continue; }
        alive++;
        const LONG refs = RyRefs((DWORD)y.obj), m = y.n < RY_EV ? y.n : RY_EV;
        longer += y.n > RY_EV;
        // net +1 in the counted events (the cache's own reference is a direct AddRef, not counted): grouped by the last
        // AddRef or find, the reference not dropped since
        if (y.n <= RY_EV) {
            LONG net = 0;
            DWORD lastEv = 0;
            for (LONG k = 0; k < m; k++)
                if (y.ev[k] >> 30 == 1) net--; else net++, lastEv = y.ev[k];
            if (net == 1) {
                const bool reg = y.reg == n;
                net1++, net1Reg += reg;
                int j = 0;
                while (j < nl && last[j].ev != lastEv) j++;
                if (j < nl) last[j].c++, last[j].reg += reg;
                else if (nl < 32) last[nl].ev = lastEv, last[nl].c = 1, last[nl].reg = reg, nl++;
                else lastFull++;
            }
        }
        if (refs <= 1) { r1++; continue; }
        refs == 2 ? r2++ : r3++;
        char s[sizeof(pat[0].s)];
        int at = 0;
        for (LONG k = 0; k < m; k++)
            at += _snprintf_s(s + at, sizeof(s) - at, _TRUNCATE, "%s%c%lX", k ? " " : "", "ARF?"[y.ev[k] >> 30], y.ev[k] & 0x3FFFFFFF);
        int j = 0;
        while (j < np && (pat[j].refs != refs || strcmp(pat[j].s, s))) j++;
        if (j < np) pat[j].c++;
        else if (np < 48) strcpy_s(pat[np].s, s), pat[np].c = 1, pat[np].refs = refs, np++;
        else patFull++;
    }
    g_ryCensus = n;
    ts3native_counters.rrcSampled = g_ryTaken;
    Log("diag: rc ref history #%ld: %ld sampled (1 in %lu new, %ld not: table full), %lu freed, %lu too young, %lu not a compositor now; "
        "%lu alive: refs 1: %lu, 2: %lu, 3+: %lu; %lu with more than %d events, %lu in no listed group",
        n, g_ryTaken, g_ryEvery, g_ryFull, dead, young, other, alive, r1, r2, r3, longer, RY_EV, patFull);
    qsort(pat, np, sizeof(RyPat), RyByC);
    for (int j = 0; j < np && j < 12; j++) Log("diag: rc ref history #%ld x%lu refs %ld: %s", n, pat[j].c, pat[j].refs, pat[j].s);
    Log("diag: rc ref history #%ld: %lu alive at net +1 (counted AddRef + finds - Release; the cache's own reference is not counted), "
        "%lu of them in the compositor registry; %lu in no listed group; by the last AddRef or find:", n, net1, net1Reg, lastFull);
    qsort(last, nl, sizeof(RyLast), RyLastByC);
    for (int j = 0; j < nl && j < 12; j++)
        Log("diag: rc ref history #%ld net+1 x%lu last %c%lX, in the registry %lu", n, last[j].c, "ARF?"[last[j].ev >> 30],
            last[j].ev & 0x3FFFFFFF, last[j].reg);
}
static void RcRefCallers(LONG n) {
    static LONG rows[RS_SLOTS][6];  // caller, net, net since the last line, AddRef, Release, finds
    int k = 0;
    LONG tot[3] = {}, d[3] = {};
    for (int i = 0; i < RS_SLOTS; i++) {
        const LONG key = g_rsAt[i];
        if (!key) continue;
        LONG c[3];
        for (int j = 0; j < 3; j++) c[j] = g_rsN[j][i], tot[j] += c[j], d[j] += c[j] - g_rsPrev[j][i], g_rsPrev[j][i] = c[j];
        const LONG net = c[0] + c[2] - c[1];
        rows[k][0] = key, rows[k][1] = net, rows[k][2] = net - g_rsPrevNet[i], rows[k][3] = c[0], rows[k][4] = c[1], rows[k][5] = c[2], k++;
        g_rsPrevNet[i] = net;
    }
    const DWORD now = GetTickCount(), ms = g_rsPrevMs ? now - g_rsPrevMs : 0;
    g_rsPrevMs = now;
    const double costMs = ((double)d[0] + d[1]) * g_rsNs / 1e6 + (double)d[2] * g_rsChainNs / 1e6;
    Ts3nCounters& z = ts3native_counters;
    z.rrcAddRefs = tot[0], z.rrcReleases = tot[1], z.rrcFinds = tot[2], z.rrcCallers = k;
    Log("diag: rc ref callers #%ld: since the start AddRef %ld, finds %ld, Release %ld, net %+ld; since the last line AddRef +%ld, finds +%ld, "
        "Release +%ld, net %+ld; %d callers%s; our hook work ~%.1f ms since the last line (%.0f ns a call, %.0f ns a find chain; %.3f%% of %.0f s)",
        n, tot[0], tot[2], tot[1], tot[0] + tot[2] - tot[1], d[0], d[2], d[1], d[0] + d[2] - d[1], k, g_rsFull ? " (table full)" : "", costMs,
        g_rsNs, g_rsChainNs, ms ? costMs / ms * 100 : 0.0, ms / 1000.0);
    qsort(rows, k, sizeof(rows[0]), RsByDn);
    for (int i = 0; i < k && i < 20 && rows[i][1] > 0; i++)
        Log("diag: rc ref callers #%ld 0x%08lX net %+ld (%+ld): AddRef %ld, finds %ld, Release %ld", n, rows[i][0], rows[i][1], rows[i][2],
            rows[i][3], rows[i][5], rows[i][4]);
    for (int i = k - 1, m = 0; i >= 0 && m < 5 && rows[i][1] < 0; i--, m++)  // the most negative: who drops the references
        Log("diag: rc ref callers #%ld 0x%08lX net %+ld (%+ld): AddRef %ld, finds %ld, Release %ld", n, rows[i][0], rows[i][1], rows[i][2],
            rows[i][3], rows[i][5], rows[i][4]);
    k = 0;
    for (int i = 0; i < RF_SLOTS; i++) {
        const LONG t = g_rsFs[i].n;
        if (!g_rsFs[i].h || !g_rsFs[i].c[0]) continue;
        rows[k][0] = i, rows[k][1] = t - g_rfPrev[i], rows[k][2] = t, k++;
        g_rfPrev[i] = t;
    }
    qsort(rows, k, sizeof(rows[0]), RsByDn);
    const LONG hits = g_rfHits;
    Log("diag: rc find sites #%ld: +%ld finds by key returned a part compositor since the last line; %d chains%s", n, hits - g_rfPrevHits, k,
        g_rfFull ? " (table full)" : "");
    g_rfPrevHits = hits;
    for (int i = 0; i < k && i < 12 && rows[i][1]; i++) {
        const RfRow& f = g_rsFs[rows[i][0]];
        Log("diag: rc find sites #%ld +%ld (total %ld) from 0x%08lX < 0x%08lX < 0x%08lX < 0x%08lX", n, rows[i][1], rows[i][2], f.c[0], f.c[1],
            f.c[2], f.c[3]);
    }
}
// At each diag: mem line but exit (the service may be gone): no-op unless [MemDiag] RcRefCallers=1.
static void RcRefCallersLine(LONG n, const char* tag) {
    if (!g_rsOn || !strcmp(tag, "exit")) return;
    RcRefCallers(n);
    RcRegistry(n);  // marks the sampled compositors it holds, before the history reads them
    if (g_ryOn) RcRefHistory(n);
}

static void RcBuilders(LONG n) {
    RbStats st = {};
    const DWORD t0 = GetTickCount();
    MEMORY_BASIC_INFORMATION m, own = {};
    VirtualQuery(&st, &own, sizeof(own));
    for (const u8* a = (const u8*)0x10000; VirtualQuery(a, &m, sizeof(m)) == sizeof(m); a = (const u8*)m.BaseAddress + m.RegionSize) {
        if ((const u8*)m.BaseAddress + m.RegionSize <= a) break;  // wrapped
        if (m.RegionSize > 0x200 && RhScannable(m, own) && RbRegion(m, st)) st.bytes += m.RegionSize;
    }
    char top[200] = "";
    for (int i = 0, at = 0; i < 5 && st.top[i].comps && at < (int)sizeof(top) - 40; i++)
        at += _snprintf_s(top + at, sizeof(top) - at, _TRUNCATE, "%s%lu in %lu parts", i ? ", " : "; most: ", st.top[i].comps, st.top[i].parts);
    Log("diag: rc builders #%ld: %lu SimBuilders, %lu parts, %lu compositors%s; %lu not valid; scanned %.0f MB in %lu ms", n, st.n,
        st.parts, st.comps, top, st.bad, st.bytes / 1048576.0, GetTickCount() - t0);
}
static void RcHolders(LONG n, const u8* c, CRITICAL_SECTION* cs, DWORD type, DWORD typeN) {
    if (!RhRanges()) return (void)Log("diag: rc holders #%ld: exe sections not found", n);
    DWORD wait = 0;
    if (cs && !RcEnter(cs, &wait)) return (void)Log("diag: rc holders #%ld: skipped, cache lock busy for %lu ms", n, wait);
    const DWORD every = typeN > RH_N ? typeN / RH_N : 1;
    const int k = RhSample(c, type, every);
    if (cs) LeaveCriticalSection(cs);
    memset(g_rhBits, 0, sizeof(g_rhBits)), g_rhKeyN = 0;
    for (int i = 0; i < k; i++) {
        const DWORD b = (DWORD)(uintptr_t)g_rhRes[i];
        g_rhSorted[i] = b;
        for (DWORD pg = (b - 0x40) >> 12; pg <= (b + 0x40) >> 12; pg++) g_rhBits[pg >> 5] |= 1u << (pg & 31);
    }
    qsort(g_rhSorted, k, sizeof(DWORD), [](const void* a, const void* b) { const DWORD x = *(const DWORD*)a, y = *(const DWORD*)b; return x < y ? -1 : x > y ? 1 : 0; });
    RhStats st = {};
    const DWORD t0 = GetTickCount();
    MEMORY_BASIC_INFORMATION m, own = {};
    VirtualQuery(&st, &own, sizeof(own));  // our own stack: copies of the samples in locals
    for (const u8* a = (const u8*)0x10000; k && VirtualQuery(a, &m, sizeof(m)) == sizeof(m); a = (const u8*)m.BaseAddress + m.RegionSize) {
        if ((const u8*)m.BaseAddress + m.RegionSize <= a) break;  // wrapped
        if (RhScannable(m, own) && RhRegion(m, k, st)) st.bytes += m.RegionSize;
    }
    Log("diag: rc holders #%ld type 0x%08lX: %d sampled (1 in %lu of %lu), scanned %.0f MB in %lu ms; pointers %lu (image %lu, "
        "private %lu, mapped %lu) + %lu cache nodes; no vtable %lu%s",
        n, type, k, every, typeN, st.bytes / 1048576.0, GetTickCount() - t0, st.hits, st.img, st.priv, st.map, st.own, st.novt,
        st.keysFull ? " (key table full)" : "");
    qsort(g_rhKeys, g_rhKeyN, sizeof(RhKey), RhByN);
    for (int i = 0; i < g_rhKeyN && i < 12; i++) {
        int cov = 0;
        for (u64 x = g_rhKeys[i].mask; x; x &= x - 1) cov++;
        Log("diag: rc holders #%ld vt 0x%08lX +0x%lX delta %+ld n=%lu covers %d of %d", n, g_rhKeys[i].vt, g_rhKeys[i].off,
            g_rhKeys[i].delta, g_rhKeys[i].n, cov, k);
    }
    for (DWORD i = 0; i < st.novt && i < 3; i++) Log("diag: rc holders #%ld no vtable at 0x%08lX (allocation 0x%08lX)", n, st.novtAt[i], st.novtBase[i]);
}
static void RcCensus(LONG n, const char* tag) {
    if (!g_rcCensusOn || !strcmp(tag, "exit")) return;  // exit: the other threads are gone and one may own the lock
    if (InterlockedExchange(&g_rcBusy, 1)) return;      // a mark sample on another thread
    DWORD r[5];
    const u8* c = NULL;
    static RcType t[RC_SLOTS];
    memset(t, 0, sizeof(t));
    RcTotals z = {};
    bool locked = false, ok = false;
    DWORD wait = 0;
    double ms = 0;
    const bool lockOn = MdRc(r, &c) && RcLockOn(c);
    CRITICAL_SECTION* cs = c ? (CRITICAL_SECTION*)(c + 0x58) : NULL;
    g_rgN = g_rgOver = 0;
    if (cs && lockOn) locked = RcEnter(cs, &wait);
    if (c && (locked || !lockOn)) {
        LARGE_INTEGER f, a, b;
        QueryPerformanceFrequency(&f), QueryPerformanceCounter(&a);
        ok = RcWalk(c, t, z);
        QueryPerformanceCounter(&b);
        if (locked) LeaveCriticalSection(cs);
        ms = (b.QuadPart - a.QuadPart) * 1000.0 / f.QuadPart;
    }
    if (!c) Log("diag: rc census #%ld %s: no ResourceCache", n, tag);
    else if (lockOn && !locked) Log("diag: rc census #%ld %s: skipped, cache lock busy for %lu ms", n, tag, wait);
    else if (!ok) Log("diag: rc census #%ld %s: map not readable (stopped after %lu of %lu entries, %.1f ms)", n, tag, z.walked, z.mapN, ms);
    else {
        static RcType top[RC_SLOTS];
        int k = 0;
        DWORD cold = 0, held1 = 0;
        u64 bytes = 0;
        for (int i = 0; i < RC_SLOTS; i++) {
            if (!t[i].n) continue;
            RcType& e = top[k++] = t[i];
            cold += e.cold, held1 += e.ref1Held, bytes += e.bytes;
            e.dn = (LONG)e.n, e.dmb = e.bytes / 1048576.0;
            for (int j = 0; j < g_rcPrevN; j++)
                if (g_rcPrev[j].type == e.type) { e.dn -= (LONG)g_rcPrev[j].n, e.dmb -= g_rcPrev[j].bytes / 1048576.0; break; }
        }
        Log("diag: rc census #%ld %s entries=%lu (map %lu) types=%lu%s MB=%.1f cold=%lu ref1held=%lu keyBad=%lu walk=%.1f ms wait=%lu ms%s%s",
            n, tag, z.walked, z.mapN, z.types, z.other ? " +other" : "", bytes / 1048576.0, cold, held1, z.keyBad, ms, wait,
            lockOn ? "" : " (lock off)", g_rcPrevN ? "" : "; first census, changes are since 0");
        qsort(top, k, sizeof(RcType), RcByN);
        if (k > RC_TOP) qsort(top + RC_TOP, k - RC_TOP, sizeof(RcType), RcByDn);
        for (int i = 0; i < k && i < RC_TOP + RC_GROW; i++) {
            const RcType& e = top[i];
            if (i >= RC_TOP && e.dn <= 0) break;
            Log("diag: rc census #%ld %s 0x%08lX n=%lu (%+ld) MB=%.1f (%+.1f) avgKB=%.1f cold=%lu ref1=%lu ref2=%lu ref3+=%lu ref1held=%lu "
                "g=0x%08lX x%lu e.g. 0x%016llX",
                n, i < RC_TOP ? "type" : "grow", e.type, e.n, e.dn, e.bytes / 1048576.0, e.dmb, e.bytes / 1024.0 / e.n, e.cold,
                e.ref1, e.ref2, e.ref3, e.ref1Held, e.g0, e.g0n, e.inst0);
        }
        if (g_rcHoldersAt && ++g_rcCensusN == g_rcHoldersAt && k) {
            int g = 0;
            for (int i = 1; i < k; i++) if (top[i].dn > top[g].dn) g = i;
            RcHolders(n, c, lockOn ? cs : NULL, top[g].type, top[g].n);
        }
        if (g_rbOn) RcBuilders(n);
        if (g_rgOn) RcGraph(n);
        memcpy(g_rcPrev, top, k * sizeof(RcType)), g_rcPrevN = k;
    }
    InterlockedExchange(&g_rcBusy, 0);
}

static void MdValues(const UdMemStat& s, u64 privUsage, MdVals& x) {
    const double MB = 1.0 / (1 << 20);
    for (int i = 0; i < MD_N; i++) x.have[i] = false;
    auto set = [&](int i, double v) { x.v[i] = v, x.have[i] = true; };
    set(MD_PRIV, privUsage * MB), set(MD_WPRIV, s.privCommit * MB), set(MD_WCOM, s.commit * MB), set(MD_FREE, s.freeB * MB);
    set(MD_BIG, s.big1 * MB), set(MD_IMG, s.image * MB), set(MD_MAP, s.mapped * MB);
    set(MD_STKRES, s.stkRes * MB), set(MD_STKCOM, s.stkCom * MB), set(MD_MIRES, s.miRes * MB), set(MD_MICOM, s.miCom * MB);
    DWORD a = 0, b = 0, r[5] = {}, g[3] = {};
    if (MdEaDirect(&a, &b)) set(MD_EADIR, a * MB), set(MD_EADIRN, b);
    double ec = 0, er = 0;
    DWORD en = 0;
    if (MdEaCores(&ec, &er, &en)) set(MD_EACOM, ec * MB), set(MD_EARES, er * MB), set(MD_EACOREN, en);
    const Ts3nCounters& k = ts3native_counters;
    set(MD_RP, k.rpKB / 1024.0), set(MD_PL, k.plKB / 1024.0), set(MD_SP, k.spKB / 1024.0);
    if (MdGc(g)) set(MD_GCHEAP, g[0] * MB), set(MD_GCFREE, g[1] * MB), set(MD_GCN, g[2]);
    if (MdRc(r)) {
        set(MD_RC, r[0] * MB), set(MD_RCCOLD, r[1] * MB), set(MD_RCBUD, r[2] * MB), set(MD_RCN, r[3]);
        if (r[4] != ~0u) set(MD_ANIM, r[4] * MB);
    }
    // remainder of the OS private bytes after the attributed owners that were readable (an upper bound when one is "-")
    double rest = x.v[MD_PRIV];
    for (int i : {MD_GCHEAP, MD_EADIR, MD_EACOM, MD_MICOM, MD_RP, MD_PL, MD_SP, MD_STKCOM})
        if (x.have[i]) rest -= x.v[i];
    set(MD_REST, rest);
}

static char MdMarkOf(int i) {  // '' validated, '?' unvalidated, '!' failed this session
    if (g_mdFail & (1 << i)) return '!';
    if (g_mdPass & (1 << i)) return 0;
    if (kMd[i].check == 'H' || (kMd[i].check == 'G' && g_mdGameVerified)) return 0;
    return '?';
}

static void MdNum(char* out, size_t cap, double v, bool count) {
    _snprintf_s(out, cap, _TRUNCATE, count || v >= 100 || v <= -100 ? "%.0f" : "%.1f", v);
}

// "diag: mem #N <tag>[ ev=<ev>] key=value[?|!] ... t=<s since start>" (~360-500 characters; the Log buffer is 1536)
static void MdFormat(char* out, size_t cap, LONG n, const char* tag, const char* ev, const MdVals& x, DWORD secs) {
    int at = _snprintf_s(out, cap, _TRUNCATE, "diag: mem #%ld %s%s%s", n, tag, ev && *ev ? " ev=" : "", ev && *ev ? ev : "");
    for (int i = 0; i < MD_N && at > 0; i++) {
        char num[32] = "-";
        const char m = x.have[i] ? MdMarkOf(i) : 0;
        if (x.have[i]) MdNum(num, sizeof(num), x.v[i], kMd[i].count);
        const int w = _snprintf_s(out + at, cap - at, _TRUNCATE, " %s=%s%s", kMd[i].key, num, m == '?' ? "?" : m == '!' ? "!" : "");
        at = w < 0 ? -1 : at + w;  // shortcut: a truncated line (not reachable at today's field count) stops at the cut
    }
    // docs/89 §16: no walk, so it can ride on every line: removals lost so far (fix off), ghosts the load's census left in the map, removals
    // the fix saved. LostRemovalFix on/off in one load shows on the same lines.
    const Ts3nCounters& fp = ts3native_counters;
    if (at > 0 && (fp.fp2lrReady || fp.fpgCensus)) {
        const int w = _snprintf_s(out + at, cap - at, _TRUNCATE, " fpLost=%ld fpLoad=%ld fpSaved=%ld", fp.fp2lrLost, fp.fpgMode == 1 ? fp.fpgGhosts : 0L, fp.fp2lrGoneHit);
        at = w < 0 ? -1 : at + w;
    }
    if (at > 0) _snprintf_s(out + at, cap - at, _TRUNCATE, " t=%lu", secs);
}

static const char* MdTag(const char* what) {  // the [UserDiag] sample name -> short tag
    if (strstr(what, "world load")) return "load";
    if (strstr(what, "at start")) return "save-start";
    if (strstr(what, "at end")) return "save-end";
    if (strstr(what, "at exit")) return "exit";
    if (strstr(what, "at mark")) return "mark";
    if (strstr(what, "selftest")) return "selftest";
    if (strstr(what, " min")) return "periodic";
    return "other";
}

static void MdLegend(const char* why) {
    char ok[200] = "", un[200] = "", bad[200] = "";
    for (int i = 0; i < MD_N; i++) {
        const char m = MdMarkOf(i);
        char* d = m == '!' ? bad : m == '?' ? un : ok;
        if (*d) strcat_s(d, sizeof(ok), " ");
        strcat_s(d, sizeof(ok), kMd[i].key);
    }
    Log("diag: mem fields (%s; MB, counts for keys ending in N and gcN; '?' = unvalidated, '!' = failed this session's "
        "self-test, '-' = not readable): validated %s; unvalidated %s%s%s", why, ok[0] ? ok : "none", un[0] ? un : "none",
        bad[0] ? "; FAILED " : "", bad);
}

// ---------------------------------------------------------------------------------------------------------------------
// suspect counters (docs/191 module 2): ts3fixes MemSuspects.cs reads managed counts (UIManager custom-control dictionary,
// delegate invocation lists of docs/192 #1-#5) on the sim thread. Every diag: mem line leaves a "wanted" slot (its number,
// tag, event); ts3fixes polls it over the text channel and answers with one "memsus: k=v ..." line, logged as
//   diag: memsus #N <tag>[ ev=<ev>] k=v[?|!] ... lag=<ms> t=<s>     (N = the diag: mem sample it answers)
// Ops on the 0x87 channel (UdTextMsg): 3 = [MemDiag] on (ts3fixes starts its poll task), 4 = a sample is wanted, 5 = the
// managed self-test is wanted ([MemDiag] SelfTest=1, until a "memsus selftest PASS|FAIL" line). Validation marks are
// ts3fixes' own (its self-test adds and removes a known entry per counter); native only relays.
// Wanted samples queue up (8 slots, oldest answered first): a single slot lost the world load sample whenever the native
// self-test's samples came before ts3fixes' first poll (0.36.74 bp-memsus-catalogue: load answer missing, lag ~15 s).
static SRWLOCK g_msLock = SRWLOCK_INIT;
static volatile LONG g_msLines;
static LONG g_msHead, g_msTail;  // under g_msLock
static struct { LONG n; char tag[16], ev[40]; DWORD tick; } g_msQ[8];
static volatile LONG g_msSelfTest;  // 0 off, 1 wanted, 2 done
static void MsWant(LONG n, const char* tag, const char* ev) {
    AcquireSRWLockExclusive(&g_msLock);
    if (g_msHead - g_msTail >= 8) g_msTail++;  // full: the oldest unanswered sample is dropped (8 within one poll do not happen)
    auto& q = g_msQ[g_msHead & 7];
    strncpy_s(q.tag, tag, _TRUNCATE);
    strncpy_s(q.ev, ev ? ev : "", _TRUNCATE);
    q.tick = GetTickCount();
    q.n = n;
    g_msHead++;
    ReleaseSRWLockExclusive(&g_msLock);
}
static u8 MsOp(int op) {
    if (!g_mdOn) return 0;
    if (op == 3) return 1;
    if (op == 4) { AcquireSRWLockShared(&g_msLock); const bool w = g_msHead != g_msTail; ReleaseSRWLockShared(&g_msLock); return w; }
    if (op == 5) return g_msSelfTest == 1;
    return 0;
}
static void MsLine(const char* line) {  // sim thread, a complete "memsus..." text line from ts3fixes
    if (!g_mdOn || InterlockedIncrement(&g_msLines) > 4000) return;  // cap: a looping sender cannot fill the disk
    if (strncmp(line, "memsus: ", 8)) {  // self-test steps, legend
        if (!strncmp(line, "memsus selftest PASS", 20) || !strncmp(line, "memsus selftest FAIL", 20)) g_msSelfTest = 2;
        Log("diag: %s", line);
        return;
    }
    char tag[16] = "", ev[40] = "";
    LONG n = 0;
    DWORD tick = GetTickCount();
    AcquireSRWLockExclusive(&g_msLock);
    if (g_msHead != g_msTail) {  // the oldest wanted sample; none = an unasked line (#0)
        const auto& q = g_msQ[g_msTail++ & 7];
        n = q.n;
        tick = q.tick;
        strcpy_s(tag, q.tag);
        strcpy_s(ev, q.ev);
    }
    ReleaseSRWLockExclusive(&g_msLock);
    Log("diag: memsus #%ld %s%s%s %s lag=%lu t=%lu", n, n ? tag : "unasked", ev[0] ? " ev=" : "", ev, line + 8, GetTickCount() - tick,
        (GetTickCount() - g_udT0) / 1000);
}

static void AsAfterSample(LONG n, const char* tag, const char* ev);  // allocsites.cpp: [MemDiag] AllocSites lines (docs/201)
static void CtMemLine(LONG n, const char* tag);                     // castrim.cpp: [CasTrim] Probe=1, docs/539 P3
static void MdAfterSample(const char* what, const char* ev, const UdMemStat& s, u64 privUsage) {
    if (!g_mdOn) return;
    if (!InterlockedExchange(&g_mdLegend, 1)) MdLegend("docs/190 phase 1");
    MdVals x;
    MdValues(s, privUsage, x);
    char line[640];
    const LONG n = InterlockedIncrement(&g_mdN);
    MdFormat(line, sizeof(line), n, MdTag(what), ev, x, (GetTickCount() - g_udT0) / 1000);
    Log("%s", line);
    MsWant(n, MdTag(what), ev);  // the suspect counters for this sample (ts3fixes answers on the sim thread)
    AsAfterSample(n, MdTag(what), ev);  // no-op unless [MemDiag] AllocSites=1
    RcCensus(n, MdTag(what));           // no-op unless [MemDiag] RcCensus=1
    RcRefCallersLine(n, MdTag(what));   // no-op unless [MemDiag] RcRefCallers=1
    CtMemLine(n, MdTag(what));          // no-op unless [CasTrim] Probe=1
}

// ---------------------------------------------------------------------------------------------------------------------
// events (sim thread)

static char g_mdWorld[64];
static bool g_mdTravel;
static void MdSaveStart(bool travelSave) { if (travelSave) g_mdTravel = true; }
// "world: <name>, lots ..." (ts3fixes UserDiag.cs) -> "travel,world-switch" / "world-switch" / "travel" / NULL
static const char* MdWorldEvent(const char* worldLine) {
    static char ev[40];
    char name[64] = "";
    if (!strncmp(worldLine, "world: ", 7)) {
        const char* s = worldLine + 7;
        size_t n = strcspn(s, ",");
        if (n >= sizeof(name)) n = sizeof(name) - 1;
        memcpy(name, s, n);
        name[n] = 0;
    }
    const bool sw = g_mdWorld[0] && name[0] && strcmp(name, g_mdWorld);
    if (name[0]) strcpy_s(g_mdWorld, name);
    sprintf_s(ev, "%s%s%s", g_mdTravel ? "travel" : "", g_mdTravel && sw ? "," : "", sw ? "world-switch" : "");
    g_mdTravel = false;
    return ev[0] ? ev : NULL;
}
// "mem: <tag>" over the ts3fixes text channel (ts3agent mem_mark): one sample tagged ev=<tag> ([A-Za-z0-9_-], 31 chars).
// Sampled inline on the sender's (sim) thread since module 2: the line is written before the agent's next step runs, so a
// mark brackets an action exactly (mem_gc's gc-before must not be walked after the collection; one walk is ~ms, test-only).
static void MdMark(const char* tag) {
    if (!g_mdOn) return;
    char t[32];
    size_t n = 0;
    for (; tag[n] && n < sizeof(t) - 1; n++) t[n] = ((tag[n] | 0x20) >= 'a' && (tag[n] | 0x20) <= 'z') || (tag[n] >= '0' && tag[n] <= '9') || tag[n] == '-' || tag[n] == '_' ? tag[n] : '_';
    t[n] = 0;
    char what[48];
    sprintf_s(what, "diag: memory at mark #%ld", InterlockedIncrement(&g_mdMarks));
    UdMemSample(what, n ? t : "unnamed");
}

// ---------------------------------------------------------------------------------------------------------------------
// self-test: inject known sizes, check each field moves by them (both on the way up and back down where the step undoes)

static u64 MdTake(MdVals& x, const void* view = NULL, size_t len = 0) {  // returns the walk's MEM_MAPPED bytes in the view
    UdMemStat s;
    UdMemWalk(s, NULL, view, len);
    MdValues(s, UdPrivateUsage(), x);
    return s.ownMapped;
}

typedef void* (*MdActFn)(void* st, bool on);  // on: allocate, returns state; off: free it
struct MdWant { int field; double mb, tol; };

static DWORD WINAPI MdIdleThread(LPVOID) { return 0; }
static void* MdActCommit(void* p, bool on) {
    if (on) return VirtualAlloc(NULL, 48u << 20, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
    VirtualFree(p, 0, MEM_RELEASE);
    return NULL;
}
static void* MdActArena(void* p, bool on) {  // like a mimalloc arena: 64 MiB reserve, 16 MB committed
    if (!on) { VirtualFree(p, 0, MEM_RELEASE); return NULL; }
    void* r = VirtualAlloc(NULL, 64u << 20, MEM_RESERVE, PAGE_READWRITE);
    if (r && !VirtualAlloc(r, 16u << 20, MEM_COMMIT, PAGE_READWRITE)) { VirtualFree(r, 0, MEM_RELEASE); r = NULL; }
    return r;
}
static void* MdActStack(void* p, bool on) {  // a suspended thread with a 4 MB stack reserve; off: let it run out and exit
    if (on) return CreateThread(NULL, 4u << 20, MdIdleThread, NULL, CREATE_SUSPENDED | STACK_SIZE_PARAM_IS_A_RESERVATION, NULL);
    if (p) { ResumeThread(p); WaitForSingleObject(p, 5000); CloseHandle(p); }
    return NULL;
}
static HANDLE g_mdMapH;
static void* MdActMap(void* p, bool on) {  // 32 MB pagefile-backed view
    if (!on) { UnmapViewOfFile(p); CloseHandle(g_mdMapH); g_mdMapH = NULL; return NULL; }
    g_mdMapH = CreateFileMappingW(INVALID_HANDLE_VALUE, NULL, PAGE_READWRITE, 0, 32u << 20, NULL);
    return g_mdMapH ? MapViewOfFile(g_mdMapH, FILE_MAP_WRITE, 0, 0, 32u << 20) : NULL;
}
static void* MdActRp(void* p, bool on) {  // routesnap's own heap (replay.cpp): rpKB counts the request size
    if (on) return RpAlloc(4u << 20);
    RpFree(p);
    return NULL;
}
// EA Malloc / Free on the MemoryMan heap, flag bit 3 = direct VirtualAlloc block (docs/re/structs.md §9; thiscall, alloc.cpp)
static bool MdEaUsable() {
    const u8* h = NULL;
    __try { h = *g_mdEaHeapSlot; } __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
    const u8* f = (const u8*)(uintptr_t)EA_MALLOC;
    // the game's prologue, or a jmp (ours [Alloc] or another module's detour: same ABI)
    return h && Readable(f, sizeof(kEaLockedPrologue)) && (!memcmp(f, kEaLockedPrologue, sizeof(kEaLockedPrologue)) || f[0] == 0xE9);
}
static void* MdEaAlloc(DWORD n) { return ((EaMallocFn)(uintptr_t)EA_MALLOC)((void*)*g_mdEaHeapSlot, NULL, n, 8); }
static void MdEaFree(void* p) { if (p) ((EaFreeFn)(uintptr_t)EA_FREE)((void*)*g_mdEaHeapSlot, NULL, p); }
static void* MdActEa(void* p, bool on) {
    if (on) return MdEaAlloc(8u << 20);
    MdEaFree(p);
    return NULL;
}

// One step: up to 3 tries (other threads allocate too); a try passes when every field moved by mb +- tol from before to
// during and (down = true) from after back to during. Logs the last try per field; returns the failed field bits.
static DWORD MdStep(const char* name, MdActFn act, const MdWant* w, int n, bool down) {
    MdVals a, b, c;
    double up[MD_N] = {}, dn[MD_N] = {};
    bool ok = false;
    int tries = 0;
    DWORD bad = 0;
    while (!ok && tries < 3) {
        tries++;
        MdTake(a);
        void* st = act(NULL, true);
        if (!st) { Log("diag: mem selftest %s: allocation failed", name); bad = ~0u; break; }
        MdTake(b);
        act(st, false);
        MdTake(c);
        ok = true, bad = 0;
        for (int i = 0; i < n; i++) {
            const int f = w[i].field;
            up[f] = b.v[f] - a.v[f], dn[f] = b.v[f] - c.v[f];
            const bool fOk = a.have[f] && b.have[f] && fabs(up[f] - w[i].mb) <= w[i].tol && (!down || fabs(dn[f] - w[i].mb) <= w[i].tol);
            if (!fOk) ok = false, bad |= 1u << f;
        }
    }
    if (bad == ~0u) {  // the allocation itself failed: this step's fields fail
        bad = 0;
        for (int i = 0; i < n; i++) bad |= 1u << w[i].field;
        return bad;
    }
    for (int i = 0; i < n; i++) {
        const int f = w[i].field;
        char d[48] = "";
        if (down) sprintf_s(d, ", back %+.2f", -dn[f]);
        Log("diag: mem selftest %s %s %+.2f%s (want %+.2f +-%.2f) %s, try %d", name, kMd[f].key, up[f], d, w[i].mb, w[i].tol,
            bad & (1u << f) ? "FAIL" : "ok", tries);
    }
    return bad;
}

// File mapping: the game maps and unmaps files all the time (streaming), so the whole-process map total moves by other
// mappings during the step (run 2026-10-25: +36.12 / back -30.62). The check is the walk's own count of our view: the walk
// that sums the map field must count all 32 MB of it as MEM_MAPPED while mapped and none after the unmap. The total is
// compared too; a try whose total missed while the view was exact is retried (other mappings moved), and after 3 such tries
// the view count decides. Returns 0 ok, 1 retry, 2 fail.
static int MdMapVerdict(u64 viewDuring, u64 viewAfter, u64 want, double up, double dn, double tol, bool last) {
    if (viewDuring != want) return 2;  // the walk did not count our view exactly
    if (viewAfter) return last ? 2 : 1;  // a mapping in our range after the unmap (address reused, or the unmap failed)
    const double w = want / 1048576.0;
    if (fabs(up - w) > tol || fabs(dn - w) > tol) return last ? 0 : 1;  // the total moved by other mappings only
    return 0;
}
static DWORD MdStepMap(double tol) {
    const u64 want = 32ull << 20;
    const double MB = 1.0 / (1 << 20);
    MdVals a, b, c;
    u64 vb = 0, vc = 0;
    int tries = 0, v = 1;
    double up = 0, dn = 0;
    while (v == 1) {
        tries++;
        MdTake(a);
        void* st = MdActMap(NULL, true);
        if (!st) { Log("diag: mem selftest file mapping 32 MB: allocation failed"); return 1u << MD_MAP; }
        vb = MdTake(b, st, want);
        MdActMap(st, false);
        vc = MdTake(c, st, want);
        up = b.v[MD_MAP] - a.v[MD_MAP], dn = b.v[MD_MAP] - c.v[MD_MAP];
        v = MdMapVerdict(vb, vc, want, up, dn, tol, tries >= 3);
    }
    char note[64] = "total moved by other mappings, not compared";
    if (fabs(up - want * MB) <= tol && fabs(dn - want * MB) <= tol) sprintf_s(note, "total within +-%.2f", tol);
    Log("diag: mem selftest file mapping 32 MB map view %+.2f, after %.2f (want %+.2f exact); total %+.2f, back %+.2f, other "
        "mappings %+.2f / %+.2f (%s) %s, try %d", vb * MB, vc * MB, want * MB, up, -dn, up - vb * MB, (vb - vc) * MB - dn,
        note, v ? "FAIL" : "ok", tries);
    return v ? 1u << MD_MAP : 0;
}

// game = in the game (EA heap steps, wider tolerances for the other threads' allocations); false = host selftest.
static bool MdSelfTest(bool game) {
    const double t = game ? 3.0 : 1.0;  // OS private bytes and walk sums: MB of noise allowed per step
    DWORD bad = 0, checked = 0;
    auto run = [&](const char* name, MdActFn act, std::initializer_list<MdWant> w, bool down) {
        for (const MdWant& x : w) checked |= 1u << x.field;
        bad |= MdStep(name, act, w.begin(), (int)w.size(), down);
    };
    run("commit 48 MB", MdActCommit, {{MD_PRIV, 48, t}, {MD_WPRIV, 48, t}, {MD_WCOM, 48, t}, {MD_FREE, -48, t}, {MD_MIRES, 0, 0.5}, {MD_STKRES, 0, 0.5}}, true);
    run("arena 64 MiB reserve, 16 MB commit", MdActArena, {{MD_MIRES, 64, 0.5}, {MD_MICOM, 16, 0.5}, {MD_PRIV, 16, t}, {MD_WPRIV, 16, t}}, true);
    // the stack is freed by the thread's exit, not by us: checked on the way up only
    run("thread stack 4 MB reserve", MdActStack, {{MD_STKRES, 4, 0.5}, {MD_MIRES, 0, 0.5}}, false);
    checked |= 1u << MD_MAP;
    bad |= MdStepMap(0.5);
    run("routesnap heap 4 MB", MdActRp, {{MD_RP, 4, game ? 1.0 : 0.01}}, true);
    const bool ea = game && MdEaUsable();
    if (ea) run("EA heap direct block 8 MB", MdActEa, {{MD_EADIR, 8, 0.1}, {MD_EADIRN, 1, 0}, {MD_PRIV, 8, t}}, true);
    else if (game) Log("diag: mem selftest EA heap: skipped (heap or Malloc entry not as expected)");

    // synthetic leak: 8 x 1 MB that stay allocated (EA direct blocks in the game, VirtualAlloc in the host); the report
    // tool must see +8 MB between the two tagged samples
    UdMemSample("diag: memory at selftest leak before", "selftest-leak-before");
    MdVals a, b;
    MdTake(a);
    static void* leak[8];
    for (void*& p : leak) p = ea ? MdEaAlloc(1u << 20) : VirtualAlloc(NULL, 1u << 20, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
    MdTake(b);
    UdMemSample("diag: memory at selftest leak after", "selftest-leak-after");
    const int lf = ea ? MD_EADIR : MD_WPRIV;
    const double lt = ea ? 0.2 : t + 1;
    for (int f : {(int)MD_PRIV, lf}) {
        const double d = b.v[f] - a.v[f], tol = f == MD_PRIV ? t + 1 : lt;
        const bool ok = a.have[f] && b.have[f] && fabs(d - 8) <= tol;
        checked |= 1u << f;
        if (!ok) bad |= 1u << f;
        Log("diag: mem selftest leak 8 x 1 MB %s %+.2f (want +8.00 +-%.2f) %s", kMd[f].key, d, tol, ok ? "ok" : "FAIL");
    }
    if (!game) for (void* p : leak) VirtualFree(p, 0, MEM_RELEASE);  // the host selftest cleans up; the game keeps them

    // cross-check against the OS: walk private committed vs PrivateUsage, and the attributed owners never above it
    MdTake(a);
    const double gap = fabs(a.v[MD_PRIV] - a.v[MD_WPRIV]), gapTol = a.v[MD_PRIV] / 10 > 64 ? a.v[MD_PRIV] / 10 : 64;
    const bool xOk = gap <= gapTol && a.v[MD_REST] >= -16;
    Log("diag: mem selftest cross-check: OS private %.0f MB vs walk private committed %.0f MB (gap %.1f, allowed %.0f), rest %.1f MB "
        "(allowed >= -16) %s", a.v[MD_PRIV], a.v[MD_WPRIV], gap, gapTol, a.v[MD_REST], xOk ? "ok" : "FAIL");
    if (!xOk) bad |= 1u << MD_PRIV | 1u << MD_WPRIV;

    InterlockedOr(&g_mdFail, (LONG)bad);
    InterlockedOr(&g_mdPass, (LONG)(checked & ~bad));
    char list[200] = "";
    for (int i = 0; i < MD_N; i++)
        if (bad & (1u << i)) { if (*list) strcat_s(list, " "); strcat_s(list, kMd[i].key); }
    Log(bad ? "diag: mem selftest FAIL: %s" : "diag: mem selftest PASS%s", bad ? list : " (every checked field moved by the injected size)");
    MdLegend("after the self-test");
    return !bad;
}

// memory worker (userdiag.cpp UdMemMain): runs the pending game self-test 30 s after the first world load; returns how long
// the worker may sleep before it wants to be called again (INFINITE = nothing pending)
static DWORD MdWorkerPoll() {
    if (!g_mdOn || g_mdSelfTest != 1) return INFINITE;
    if (!g_udWorlds) return 5000;
    if (!g_mdWorldTick) g_mdWorldTick = GetTickCount() | 1;
    const DWORD since = GetTickCount() - g_mdWorldTick;
    if (since < kMdSelfTestDelayMs) return kMdSelfTestDelayMs - since;
    g_mdSelfTest = 2;
    MdSelfTest(true);
    return INFINITE;
}

// Light (0.36.79, docs/191 "Player memory lines", release default on): the same "diag: mem" / "diag: memsus" lines (numbers
// only, reads only) at world load / travel arrival, save start / end, exit and every 5 min of play, without the self-test.
// Enabled=1 = the full tool for correctness runs (SelfTest allowed); Enabled=0 Light=0 = off.
static bool g_mdLight;
static void ApplyMemDiag(const wchar_t* ini) {
    const bool full = GetPrivateProfileIntW(L"MemDiag", L"Enabled", 0, ini) > 0;
    g_mdLight = !full && GetPrivateProfileIntW(L"MemDiag", L"Light", 1, ini) > 0;
    g_mdOn = full || g_mdLight;
    g_mdSelfTest = full && GetPrivateProfileIntW(L"MemDiag", L"SelfTest", 0, ini) > 0 ? 1 : 0;
    g_msSelfTest = g_mdSelfTest;  // the managed suspect-counter self-test (ts3fixes MemSuspects.cs) runs in the same runs
    if (g_mdOn) g_udMemPeriodMs = 5 * 60 * 1000;  // set before the memory worker starts (userdiag.cpp)
    if (g_mdOn) Log("mem diag %s (docs/191): diag: mem + memsus lines at each memory at line, every 5 min of play%s", g_mdLight ? "light" : "on",
                    g_mdSelfTest ? "; self-test 30 s after the first world load (correctness runs only: leaks 8 MB on purpose)" : "");
    g_rcCensusOn = g_mdOn && GetPrivateProfileIntW(L"MemDiag", L"RcCensus", 0, ini) > 0;
    g_rcHoldersAt = g_rcCensusOn ? GetPrivateProfileIntW(L"MemDiag", L"RcCensusHolders", 0, ini) : 0;
    g_rbOn = g_rcCensusOn && GetPrivateProfileIntW(L"MemDiag", L"RcCensusBuilders", 0, ini) > 0;
    // RcRefCallers: its own switch (not under RcCensus), at the same diag: mem lines; the sampling is set first so the
    // install's cost measure includes it
    const bool rrc = g_mdOn && GetPrivateProfileIntW(L"MemDiag", L"RcRefCallers", 0, ini) > 0;
    const int every = GetPrivateProfileIntW(L"MemDiag", L"RcRefCallersSample", 32, ini);
    g_ryEvery = every > 0 ? (DWORD)every : 32;
    g_ryOn = rrc && every > 0;
    g_rsOn = rrc && RsInstall();
    g_ryOn = g_ryOn && g_rsOn;
    ts3native_counters.rrcOn = g_rsOn ? 1 + g_ryOn : 0, ts3native_counters.rrcHookNs = (LONG)(g_rsNs + 0.5);
    if (g_ryOn) Log("mem diag: rc ref history on: 1 in %lu new part compositors, first %d events each", g_ryEvery, RY_EV);
    g_rgOn = g_rcCensusOn && GetPrivateProfileIntW(L"MemDiag", L"RcCensusGraph", 0, ini) > 0 && RgAlloc();
    if (g_rgOn) Log("mem diag: rc graph on: part compositor child references (vtables 0x%08lX, 0x%08lX, vector +0x28) at each census", g_rgVt[0], g_rgVt[1]);
    if (g_rsOn) Log("mem diag: rc ref callers on (docs/191): part compositor AddRef / Release counted by caller (vtables 0x%08lX, 0x%08lX), "
                    "cache finds that return one by caller and caller chain (0x%08lX slot 6); our work %.0f ns a call, %.0f ns a find chain",
                    g_rsVt[0], g_rsVt[1], g_rfVt, g_rsNs, g_rsChainNs);
    if (g_rcCensusOn) Log("mem diag: rc census on (docs/191): ResourceCache entries by type at each diag: mem line, under the cache lock%s",
                          g_rcHoldersAt ? "; holder scan once at the census set by RcCensusHolders" : "");
}
