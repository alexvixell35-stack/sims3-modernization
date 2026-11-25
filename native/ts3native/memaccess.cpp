// [MemDiag] Access=1 (docs/190 phase 2, TEST-RUN TOOL, off by default; diagnostics only, the game sees the same answers):
// access counters for the movable resource caches and the transient read buffers, so docs/190 §6 can pick an offload by
// hot set instead of size alone. Included by ts3native.cpp right after memdiag.cpp (uses its manager slot g_mdRcMgrSlot,
// sample numbers and tags). Two hooks, both chained (another module's earlier patch stays in front of ours):
//   - ResourceCache Find 0x7324E0 through the cache vtable 0xFFDD94 slot 6 (the function has no direct callers): per cache
//     object (the priority-0 ResourceCache = "rc", the AnimationCache = "anim") the gets (Find with an out pointer: the
//     manager's get 0x7366B0 -> 0x736716 and the async loader 0x729E7E, each asking the one cache the manager picks for the
//     key) with hits and a 1 KB HyperLogLog of their 16-byte keys (distinct keys in the window, ~3% error), and apart from
//     them the owner scans (Find with out = NULL: 0x4B0070 asks every cache in the array in turn, call 0x4B009C, until one
//     has the key) [verified disassembly 2026-11-15]. Run memdiag2-access-1115 counted both together: every scanned key
//     reached anim and then rc, so the two caches showed the same key count and anim a 0.3% hit rate at load.
//   - DBPF ReadDecompress 0x4A8380 (thiscall: compression, offset, fileSize, memSize, out buffer; ret 0x14, returns al
//     [verified disassembly of 0x4A8380 and its two call sites 0x4A96BA / 0x4A97B1, 2026-11-15]): reads, decoded MB
//     (memSize), raw MB (fileSize), the largest single read (fileSize + memSize: both buffers exist during the call), the
//     peak bytes in flight over all threads, reads >= 4 MB, time inside, share on the exe's main thread, failed reads.
// One "diag: memaccess" line after each diag: mem line of a world load, of the play timer (every 5 min with [MemDiag]),
// of a mem_mark and at exit (not at saves or self-test samples); deltas over the window since the previous line:
//   diag: memaccess #7 periodic win=300 rc get=48211 hit=97.1 miss=1398 keys=2911 scan=9120 scanHit=40.2 anim get=812
//     hit=88.0 miss=97 keys=140 scan=15302 scanHit=0.3 rd n=1402 MB=212.4 rawMB=80.1 maxMB=5.3 flyMaxMB=11.0 big=3 ms=1840 main=41.2 fail=0 ourMs=0.6 t=1234
// Not built (docs/190 phase 2 rows left out, see docs/190 §5.2): CAS part cache GetCachedPart 0x5A44A0 and the script-side
// string lookup (docs/209 #95 measured both caches at <= ~10 MB with S3PP: no offload case), AsyncLoader (instance RE §7 Q5).
// Privacy: counts and sizes only; key hashes stay inside the sketch.

static bool g_maOn;
static SRWLOCK g_maLock = SRWLOCK_INIT;  // one line at a time (the memory worker and a mem_mark on the sim thread)

// HyperLogLog, 1024 one-byte registers (p = 10, standard error 1.04 / sqrt(1024) = 3.3%). Racy register updates are fine
// for a sketch: a lost max is re-set by the next key that hashes there.
struct MaHll { volatile u8 r[1024]; };
static DWORD MaMix(DWORD h) {  // murmur3 fmix32
    h ^= h >> 16, h *= 0x85EBCA6Bu, h ^= h >> 13, h *= 0xC2B2AE35u, h ^= h >> 16;
    return h;
}
static void MaHllAdd(MaHll& s, DWORD h) {
    h = MaMix(h);
    const DWORD w = h << 10;
    unsigned long b = 0;
    const u8 rank = w && _BitScanReverse(&b, w) ? (u8)(32 - b) : 23;  // leading zeros of the low 22 bits + 1
    volatile u8& r = s.r[h >> 22];
    if (rank > r) r = rank;
}
static double MaHllCount(const MaHll& s) {
    double sum = 0;
    int zeros = 0;
    for (int i = 0; i < 1024; i++) sum += ldexp(1.0, -(int)s.r[i]), zeros += !s.r[i];
    const double m = 1024, e = 0.7213 / (1 + 1.079 / m) * m * m / sum;
    return e <= 2.5 * m && zeros ? m * log(m / zeros) : e;  // linear counting for small sets
}

// ---- ResourceCache Find (vtable slot) ----
typedef bool(__fastcall* MaFindFn)(void* self, void* edx, const void* key, void** out);
static MaFindFn g_maFind;  // the slot's value before ours (0x7324E0, or another module's hook)
static DWORD g_maRcVt = TS3A(0x00FFDD94);  // selftest: its own
static const int MA_CACHES = 4;      // cache objects told apart (ResourceCache, AnimationCache, spare); more go to "other"
static volatile LONG g_maSelf[MA_CACHES], g_maGet[MA_CACHES + 1], g_maHit[MA_CACHES + 1], g_maScan[MA_CACHES + 1], g_maScanHit[MA_CACHES + 1];
static MaHll g_maKeys[MA_CACHES + 1];
static int MaSlot(const void* self) {
    for (int i = 0; i < MA_CACHES; i++) {
        const LONG s = g_maSelf[i];
        if (s == (LONG)(uintptr_t)self) return i;
        if (!s && (InterlockedCompareExchange(&g_maSelf[i], (LONG)(uintptr_t)self, 0) == 0 || g_maSelf[i] == (LONG)(uintptr_t)self)) return i;
    }
    return MA_CACHES;
}
static void MaFindCount(const void* self, const void* key, bool hit, bool scan) {
    const int i = MaSlot(self);
    if (scan) {  // an owner scan (out = NULL) asks every cache: counted apart, not in the key sketch
        InterlockedIncrement(&g_maScan[i]);
        if (hit) InterlockedIncrement(&g_maScanHit[i]);
        return;
    }
    InterlockedIncrement(&g_maGet[i]);
    if (hit) InterlockedIncrement(&g_maHit[i]);
    if (key) {  // ResourceKey: instance 8, type 4, group 4 (the original has already read it)
        const DWORD* k = (const DWORD*)key;
        MaHllAdd(g_maKeys[i], k[0] * 0x9E3779B1u ^ k[1] * 0x85EBCA77u ^ k[2] * 0xC2B2AE3Du ^ k[3] * 0x27D4EB2Fu);
    }
}
static bool __fastcall MaFind(void* self, void* edx, const void* key, void** out) {
    const bool r = g_maFind(self, edx, key, out);
    MaFindCount(self, key, r, !out);
    return r;
}

// ---- DBPF ReadDecompress (detour) ----
static const DWORD MA_READ = TS3A(0x004A8380);
static const u8 kMaReadPro[] = {0x53, 0x32, 0xDB, 0x83, 0x7C, 0x24, 0x0C, 0x00};  // push ebx; xor bl,bl; cmp dword [esp+0Ch],0
typedef bool(__fastcall* MaReadFn)(void* self, void* edx, DWORD comp, DWORD off, DWORD fileSize, DWORD memSize, void* buf);
static MaReadFn g_maRead;
static volatile LONG g_maRdN, g_maRdBig, g_maRdMain, g_maRdFail, g_maFlyKB, g_maRdMaxKB, g_maFlyMaxKB;  // max: per window
static volatile LONGLONG g_maRdMem, g_maRdRaw, g_maRdTicks;
static void MaReadCount(DWORD fs, DWORD ms, LONGLONG ticks, bool ok) {  // after the call; the in-flight add is the caller's
    InterlockedIncrement(&g_maRdN);
    InterlockedExchangeAdd64(&g_maRdMem, ms), InterlockedExchangeAdd64(&g_maRdRaw, fs), InterlockedExchangeAdd64(&g_maRdTicks, ticks);
    const LONG kb = (LONG)(((u64)fs + ms + 1023) >> 10);
    Max((LONG*)&g_maRdMaxKB, kb);
    if (kb >= 4096) InterlockedIncrement(&g_maRdBig);
    if (GetCurrentThreadId() == g_mainTid) InterlockedIncrement(&g_maRdMain);
    if (!ok) InterlockedIncrement(&g_maRdFail);
}
static bool __fastcall MaRead(void* self, void* edx, DWORD comp, DWORD off, DWORD fs, DWORD ms, void* buf) {
    const DWORD raw = fs & 0x7FFFFFFF;  // counted without bit 31 (DBPF index sizes can carry a flag there); the game gets fs as is
    const LONG kb = (LONG)(((u64)raw + ms + 1023) >> 10);
    Max((LONG*)&g_maFlyMaxKB, InterlockedExchangeAdd(&g_maFlyKB, kb) + kb);
    LARGE_INTEGER a, b;
    QueryPerformanceCounter(&a);
    const bool r = g_maRead(self, edx, comp, off, fs, ms, buf);
    QueryPerformanceCounter(&b);
    InterlockedExchangeAdd(&g_maFlyKB, -kb);
    MaReadCount(raw, ms, b.QuadPart - a.QuadPart, r);
    return r;
}

// ---- cost of our part, measured before the hooks go in ----
static double g_maRcNs, g_maRdNs;
static void MaCalibrate() {
    static const DWORD key[4] = {1, 2, 3, 4};
    LARGE_INTEGER f, a, b, c, d;
    QueryPerformanceFrequency(&f), QueryPerformanceCounter(&a);
    for (volatile int i = 0; i < 20000; i++) MaFindCount(key, key, true, false);
    QueryPerformanceCounter(&b);
    g_maRcNs = (b.QuadPart - a.QuadPart) * 1e9 / f.QuadPart / 20000;
    QueryPerformanceCounter(&a);
    for (volatile int i = 0; i < 20000; i++) {  // the in-flight add / max / sub and two QPC reads of MaRead, then the counts
        Max((LONG*)&g_maFlyMaxKB, InterlockedExchangeAdd(&g_maFlyKB, 1) + 1);
        QueryPerformanceCounter(&c), QueryPerformanceCounter(&d);
        InterlockedExchangeAdd(&g_maFlyKB, -1);
        MaReadCount(1, 1, d.QuadPart - c.QuadPart, true);
    }
    QueryPerformanceCounter(&b);
    g_maRdNs = (b.QuadPart - a.QuadPart) * 1e9 / f.QuadPart / 20000;
    memset((void*)g_maSelf, 0, sizeof(g_maSelf)), memset((void*)g_maGet, 0, sizeof(g_maGet)), memset((void*)g_maHit, 0, sizeof(g_maHit));
    memset((void*)g_maKeys, 0, sizeof(g_maKeys)), memset((void*)g_maScan, 0, sizeof(g_maScan)), memset((void*)g_maScanHit, 0, sizeof(g_maScanHit));
    g_maRdN = g_maRdBig = g_maRdMain = g_maRdFail = g_maFlyKB = g_maRdMaxKB = g_maFlyMaxKB = 0;
    g_maRdMem = g_maRdRaw = g_maRdTicks = 0;
}

// ---- the line ----
static LONG g_maPrevGet[MA_CACHES + 1], g_maPrevHit[MA_CACHES + 1], g_maPrevScan[MA_CACHES + 1], g_maPrevScanHit[MA_CACHES + 1], g_maPrevRdN, g_maPrevBig, g_maPrevMain, g_maPrevFail, g_maLineN;
static LONGLONG g_maPrevMem, g_maPrevRaw, g_maPrevTicks;
static DWORD g_maPrevMs;
static LONG g_maSessRdMaxKB, g_maSessFlyMaxKB;
static char g_maNames[MA_CACHES][16];  // per cache slot, the name at the last non-exit line
static const char* MaName(LONG self, char* buf, size_t cap) {  // by the cache's priority in the manager's array
    __try {
        const u8* m = *g_mdRcMgrSlot;
        const DWORD* b = m ? *(const DWORD* const*)(m + 0xA0) : NULL;
        const DWORD* e = m ? *(const DWORD* const*)(m + 0xA4) : NULL;
        for (int i = 0; b && b + 2 * i < e && i < 16; i++)
            if ((LONG)b[2 * i] == self) {
                if (b[2 * i + 1] == 0) return "rc";
                if (b[2 * i + 1] == 1000) return "anim";
                _snprintf_s(buf, cap, _TRUNCATE, "c%lu", b[2 * i + 1]);
                return buf;
            }
    } __except (EXCEPTION_EXECUTE_HANDLER) {}
    _snprintf_s(buf, cap, _TRUNCATE, "c%08lX", (DWORD)self);
    return buf;
}
static bool MaWants(const char* tag) { return !strcmp(tag, "load") || !strcmp(tag, "periodic") || !strcmp(tag, "mark") || !strcmp(tag, "exit"); }
static void MaLine(LONG n, const char* tag) {
    if (!g_maOn || !MaWants(tag)) return;
    AcquireSRWLockExclusive(&g_maLock);
    const DWORD now = GetTickCount();
    const DWORD winS = (now - (g_maPrevMs ? g_maPrevMs : g_udT0) + 500) / 1000;
    g_maPrevMs = now;
    char line[900], nm[16];
    int at = _snprintf_s(line, sizeof(line), _TRUNCATE, "diag: memaccess #%ld %s win=%lu", n, tag, winS);
    double ourNs = 0;
    // names first (exit: the manager may be torn down, memdiag.cpp MdValues skips its walks there too, so the last name is
    // reused), then the slots in a fixed order: rc, anim, the rest as first seen, "other" last (slots fill in the order the
    // game first asks a cache, which put anim before rc in the 2026-11-14 run)
    const bool exiting = !strcmp(tag, "exit");
    int ord[MA_CACHES + 1], no = 0;
    for (int i = 0; i < MA_CACHES; i++)
        if (g_maSelf[i] && (!exiting || !g_maNames[i][0])) SCpy(g_maNames[i], exiting ? "c?" : MaName(g_maSelf[i], nm, sizeof(nm)));
    for (const char* want : {"rc", "anim", (const char*)NULL})
        for (int i = 0; i < MA_CACHES; i++) {
            const bool named = !strcmp(g_maNames[i], "rc") || !strcmp(g_maNames[i], "anim");
            if (g_maSelf[i] && (want ? !strcmp(g_maNames[i], want) : !named)) ord[no++] = i;
        }
    ord[no++] = MA_CACHES;
    for (int j = 0; j < no && at > 0; j++) {
        const int i = ord[j];
        const LONG get = g_maGet[i], hit = g_maHit[i], sc = g_maScan[i], sh = g_maScanHit[i];
        const LONG dg = get - g_maPrevGet[i], dh = hit - g_maPrevHit[i], ds = sc - g_maPrevScan[i], dsh = sh - g_maPrevScanHit[i];
        g_maPrevGet[i] = get, g_maPrevHit[i] = hit, g_maPrevScan[i] = sc, g_maPrevScanHit[i] = sh;
        ourNs += (dg + ds) * g_maRcNs;
        if (i == MA_CACHES && !get && !sc) continue;  // "other": only when more caches than slots asked
        const double keys = MaHllCount(g_maKeys[i]);
        memset((void*)&g_maKeys[i], 0, sizeof(MaHll));  // a window sketch; racing adds land in either window
        const int w = _snprintf_s(line + at, sizeof(line) - at, _TRUNCATE, " %s get=%ld hit=%.1f miss=%ld keys=%.0f scan=%ld scanHit=%.1f",
                                  i < MA_CACHES ? g_maNames[i] : "other", dg, dg ? 100.0 * dh / dg : 0.0, dg - dh, keys, ds,
                                  ds ? 100.0 * dsh / ds : 0.0);
        at = w < 0 ? -1 : at + w;
    }
    LARGE_INTEGER f;
    QueryPerformanceFrequency(&f);
    const LONG rn = g_maRdN, big = g_maRdBig, mainN = g_maRdMain, fail = g_maRdFail;
    const LONGLONG mem = g_maRdMem, raw = g_maRdRaw, ticks = g_maRdTicks;
    const LONG dn = rn - g_maPrevRdN, maxKB = InterlockedExchange(&g_maRdMaxKB, 0), flyKB = InterlockedExchange(&g_maFlyMaxKB, 0);
    if (maxKB > g_maSessRdMaxKB) g_maSessRdMaxKB = maxKB;
    if (flyKB > g_maSessFlyMaxKB) g_maSessFlyMaxKB = flyKB;
    ourNs += dn * g_maRdNs;
    if (at > 0 && g_maRead) {
        const double MB = 1.0 / (1 << 20);
        const int w = _snprintf_s(line + at, sizeof(line) - at, _TRUNCATE,
                                  " rd n=%ld MB=%.1f rawMB=%.1f maxMB=%.1f flyMaxMB=%.1f big=%ld ms=%.0f main=%.1f fail=%ld",
                                  dn, (mem - g_maPrevMem) * MB, (raw - g_maPrevRaw) * MB, maxKB / 1024.0, flyKB / 1024.0, big - g_maPrevBig,
                                  (ticks - g_maPrevTicks) * 1e3 / f.QuadPart, dn ? 100.0 * (mainN - g_maPrevMain) / dn : 0.0, fail - g_maPrevFail);
        at = w < 0 ? -1 : at + w;
    }
    g_maPrevRdN = rn, g_maPrevBig = big, g_maPrevMain = mainN, g_maPrevFail = fail, g_maPrevMem = mem, g_maPrevRaw = raw, g_maPrevTicks = ticks;
    if (at > 0) _snprintf_s(line + at, sizeof(line) - at, _TRUNCATE, " ourMs=%.1f t=%lu", ourNs / 1e6, (now - g_udT0) / 1000);
    Log("%s", line);
    Ts3nCounters& k = ts3native_counters;
    LONG gets = 0, hits = 0;
    for (int i = 0; i <= MA_CACHES; i++) gets += g_maGet[i], hits += g_maHit[i];
    k.maRcGets = gets, k.maRcHits = hits, k.maRdN = rn, k.maRdMB = (LONG)(mem >> 20);
    k.maRdMaxKB = g_maSessRdMaxKB, k.maRdFlyMaxKB = g_maSessFlyMaxKB, k.maLines = ++g_maLineN;
    ReleaseSRWLockExclusive(&g_maLock);
}

// ---- install (after ApplyMemDiag: needs g_mdOn) ----
static void ApplyMemAccess(const wchar_t* ini) {
    g_maOn = false;
    if (!g_mdOn || GetPrivateProfileIntW(L"MemDiag", L"Access", 0, ini) <= 0) return;
    MaCalibrate();
    Ts3nCounters& k = ts3native_counters;
    k.maOn = 0;
    DWORD* slot = (DWORD*)(uintptr_t)g_maRcVt + 6;
    DWORD prev = 0;
    if (!Readable(slot, 4) || !(prev = *slot)) Log("FAIL mem access: cache vtable 0x%08lX slot 6 not readable, rc counters off", g_maRcVt);
    // EA's Find or another module's hook (outside the exe image); any other exe value = not the vtable this build expects
    else if (prev != TS3A(0x007324E0) && prev >= 0x400000 && prev < 0x400000 + (DWORD)TS3A_HOST_SIZE)
        Log("SKIP mem access: cache vtable 0x%08lX slot 6 holds 0x%08lX, not Find 0x%08lX, rc counters off", g_maRcVt, prev, (DWORD)TS3A(0x007324E0)), prev = 0;
    else {
        g_maFind = (MaFindFn)(uintptr_t)prev;  // set before the slot points at us
        const DWORD ours = (DWORD)(uintptr_t)&MaFind;
        if (Write(slot, &ours, 4)) k.maOn |= 1;
        else Log("FAIL mem access: VirtualProtect at 0x%08lX, rc counters off", g_maRcVt);
    }
    if (InstallDetour("DBPF ReadDecompress (mem access)", MA_READ, kMaReadPro, sizeof(kMaReadPro), (void*)&MaRead, &g_maRead)) k.maOn |= 2;
    else g_maRead = NULL;  // another module patched the entry first: rd counters off, the line says nothing about reads
    k.maRcNs = (LONG)(g_maRcNs + 0.5), k.maRdNs = (LONG)(g_maRdNs + 0.5);
    g_maOn = k.maOn != 0;
    Log("mem access %s (docs/190 phase 2): cache finds%s (slot 6 of 0x%08lX, chained to 0x%08lX), reads%s (0x%08lX); our work %.0f ns a "
        "find, %.0f ns a read; diag: memaccess at load, periodic, mark and exit samples",
        g_maOn ? "on" : "FAILED", k.maOn & 1 ? "" : " off", g_maRcVt, prev, k.maOn & 2 ? "" : " off", MA_READ, g_maRcNs, g_maRdNs);
}
