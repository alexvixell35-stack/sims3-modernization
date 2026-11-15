// Phase 10: package commit compression on worker threads (docs/15 phase 10). Included by ts3native.cpp.
//
// A package commit (FUN_004A8500 -> FUN_004A7910) writes the pending records one by one, leftmost of the pending map first
// (EASTL rbtree, anchor at pkg+0x2EC, leftmost +0x2F0, count +0x2FC, record at node+0x20). Per record FUN_004A8910 reads the
// record's stream (record+0x28: vt+0x1C size, +0x28 seek, +0x30 read) into a buffer and FUN_004A82C0 -> FUN_004A7030 calls
// the RefPack codec's Compress slot (vtable 0xFB9018 slot 1 = 0x4EC200) with (src, len, dst, cap, flags); that runs the
// compressor 0x4EC0A0 (cdecl: dst, cap, src, len, ICoreAllocator*, mode). The compressor is pure apart from two scratch
// tables it takes from the allocator it is given, so it can run on any thread with our own allocator.
//
// Mode 1: at the commit call (0x4A850F) the sim thread copies the next pending records' bytes (up to MaxMB in flight), and
// worker threads run the game's own compressor on private buffers. The Compress slot hook then answers a call whose input
// bytes, length and mode equal a finished job with that job's output; anything else (other input, other mode, a job that
// failed) runs the game's slot as before. So the bytes written are the game's own compressor's output by construction.
// No game state is touched on the workers: no Mono, no game allocator, no locks of the game.

static const DWORD SC_COMMIT_SITE = TS3A(0x004A850F), SC_COMMIT = TS3A(0x004A7910);  // call in FUN_004A8500 (package commit)
static const DWORD SC_SLOT = TS3A(0x00FB901C), SC_SLOT_FN = TS3A(0x004EC200);        // RefPack codec vtable, slot 1 (Compress)
static const DWORD SC_COMPRESS = TS3A(0x004EC0A0);
static const DWORD SC_LOCK_ACQUIRE = TS3A(0x004E16F0), SC_LOCK_RELEASE = TS3A(0x004E17B0), SC_LOCK_DEADLINE = TS3A(0x00FB24F8);
// Code the workers run: size writer 0x4EB340, cores 0x4EB750 / 0x4EBB90, wrapper 0x4EC0A0, slot 0x4EC200 (not the decoder
// 0x4EB3B0..0x4EB74C, which S3SS patches, nor 0x4EC010..0x4EC07F, where phase 6 retargets a call). FNV-1a of Steam 1.67.
static const DWORD kScCode[3][2] = {{TS3A(0x004EB340), TS3A(0x004EB3B0)}, {TS3A(0x004EB750), TS3A(0x004EBFD0)}, {TS3A(0x004EC0A0), TS3A(0x004EC282)}};
#ifdef TS3A_TABLE
static const DWORD SC_CODE_FNV = 0x0B2F02B4;  // EA app 1.69.47: the same code 0x140 higher, operands moved (vtable 0x1003154, thunks)
#else
static const DWORD SC_CODE_FNV = 0x86B12C94;
#endif
static const DWORD kScRecClass = 0x12E4A892;  // record class id at record+8 that FUN_004A8E00 sends to FUN_004A8910
static const DWORD kScMinLen = 4096;          // smaller records stay with the game (job overhead)
static const DWORD kScMaxLen = 0xF42400;      // FUN_004A8910 / FUN_004A82C0 compress 0x32..0xF42400 bytes

typedef DWORD(__fastcall* ScCommitFn)(void* pkg, void* edx);
typedef int(__fastcall* ScSlotFn)(void* codec, void* edx, const u8* src, DWORD len, u8* dst, DWORD cap, DWORD flags);  // ret 14h
typedef int(__cdecl* ScCompressFn)(u8* dst, DWORD cap, const u8* src, DWORD len, void* alloc, DWORD mode);
typedef DWORD(__fastcall* ScSizeFn)(void* s, void* edx);
typedef DWORD(__fastcall* ScSeekFn)(void* s, void* edx, DWORD pos, DWORD origin);
typedef DWORD(__fastcall* ScReadFn)(void* s, void* edx, void* buf, DWORD n);
typedef LONG(__fastcall* ScLockFn)(void* lock, void* edx, const DWORD* deadline);
typedef LONG(__fastcall* ScUnlockFn)(void* lock, void* edx);
static ScCommitFn g_scCommitOrig = (ScCommitFn)(uintptr_t)SC_COMMIT;
static ScSlotFn g_scSlotOrig = (ScSlotFn)(uintptr_t)SC_SLOT_FN;
static ScCompressFn g_scCompress = (ScCompressFn)(uintptr_t)SC_COMPRESS;
static ScLockFn g_scLock = (ScLockFn)(uintptr_t)SC_LOCK_ACQUIRE;
static ScUnlockFn g_scUnlock = (ScUnlockFn)(uintptr_t)SC_LOCK_RELEASE;

static DWORD ScFnv(DWORD h, DWORD lo, DWORD hi) {
    for (const u8* p = (const u8*)(uintptr_t)lo; p < (const u8*)(uintptr_t)hi; p++) h = (h ^ *p) * 0x01000193;
    return h;
}
static DWORD ScCodeHash() {
    DWORD h = 0x811C9DC5;
    for (int i = 0; i < 3; i++) h = ScFnv(h, kScCode[i][0], kScCode[i][1]);
    return h;
}

// The codec slot's flags -> the compressor's mode (as 0x4EC200): bit 1 -> 1 (window 0x1FFFF); else bit 16 -> 2; else 0.
static DWORD ScModeOf(DWORD flags) { return (flags & 2) ? 1 : (flags & 0x10000) ? 2 : 0; }
// The flags FUN_004A7030 will pass for a record of `len` bytes on its default codec path (a guess: a wrong one is a miss).
static DWORD ScPredictMode(const u8* pkg, DWORD len) {
    const int c = *(const int*)(pkg + 0x20);
    DWORD flags;
    if (c == 1) flags = *(void* const*)(pkg + 0x390) ? 1 : 0x10001;
    else if (c == 3) flags = 2;
    else flags = len > 250000 ? 1 : 2;
    return ScModeOf(flags);
}
static DWORD ScWindowBytes(DWORD mode) { return ((mode & 1) ? 0x1FFFF : 0x3FFF) * 4 + 4; }
static DWORD ScOutCap(DWORD len) { return len + len / 4 + 0x20 + 1024; }  // the game's bound ((len*20)>>4)+0x20, plus slack

// --- a worker's allocator: an ICoreAllocator look-alike (slot +8 alloc, +0xC free) with two cached blocks ---
// The compressor takes at most two blocks per call (hash 0x40000 for inputs > 16 KB, chain window*4+4); ScEnsure makes both
// exist before the call, so its allocations never fail (the game's code does not check them).
struct ScAlloc { void** vt; u8* blk[2]; DWORD size[2]; bool used[2]; };
static void* __fastcall ScAllocMalloc(ScAlloc* a, void*, DWORD n, const char*, DWORD) {
    int best = -1;  // the smallest free block that fits, so the larger one stays for the larger request
    for (int i = 0; i < 2; i++)
        if (!a->used[i] && a->blk[i] && a->size[i] >= n && (best < 0 || a->size[i] < a->size[best])) best = i;
    if (best < 0) return NULL;  // not reached after ScEnsure
    a->used[best] = true;
    return a->blk[best];
}
static void __fastcall ScAllocFree(ScAlloc* a, void*, void* p, DWORD) {
    for (int i = 0; i < 2; i++)
        if (a->blk[i] == p) a->used[i] = false;
}
static void* g_scAllocVt[4] = {NULL, NULL, (void*)ScAllocMalloc, (void*)ScAllocFree};
static bool ScEnsure(ScAlloc* a, DWORD need0, DWORD need1) {  // slot 0 >= the larger request, slot 1 >= the smaller
    const DWORD need[2] = {need0 > need1 ? need0 : need1, need0 > need1 ? need1 : need0};
    for (int i = 0; i < 2; i++) {
        a->used[i] = false;
        if (a->blk[i] && a->size[i] >= need[i]) continue;
        if (a->blk[i]) HeapFree(GetProcessHeap(), 0, a->blk[i]);
        a->blk[i] = (u8*)HeapAlloc(GetProcessHeap(), 0, need[i]);
        a->size[i] = a->blk[i] ? need[i] : 0;
        if (!a->blk[i]) return false;
    }
    return true;
}

// --- one commit = one batch of jobs ---
enum { SC_QUEUED, SC_RUNNING, SC_DONE, SC_CANCELLED, SC_FREED };
struct ScJob { u8* in; DWORD len, mode; u8* out; int outLen; LONG state; };
struct ScBatch {
    bool active;
    DWORD tid;
    u8* pkg;
    ScJob* jobs;
    int cap, n, next, cursor;  // n queued, next = next for the workers, cursor = first job not yet freed
    int count0, walked;        // pending records at the start; nodes looked at (from the start's leftmost)
    LONG64 inflight, peak, maxBytes;
    LONG hits, misses, mismatches, stolen;
    LONG64 waitTicks, inBytes;
};
static ScBatch g_sb;
static SRWLOCK g_scMu = SRWLOCK_INIT;
static CONDITION_VARIABLE g_scWork = CONDITION_VARIABLE_INIT, g_scDone = CONDITION_VARIABLE_INIT;
static volatile LONG g_scBusy;  // one batch at a time (another thread's commit runs plain)
static int g_scThreads, g_scStarted;
static LONG64 g_scInTotal;

static void ScRun(ScJob* j, ScAlloc* a) {
    j->out = (u8*)HeapAlloc(GetProcessHeap(), 0, ScOutCap(j->len));
    if (!j->out || !ScEnsure(a, j->len > 0x4000 ? 0x40000 : 0, ScWindowBytes(j->mode))) { j->outLen = -1; return; }
    j->outLen = g_scCompress(j->out, ScOutCap(j->len), j->in, j->len, a, j->mode);
}

static DWORD WINAPI ScWorker(LPVOID) {
    ScAlloc a = {g_scAllocVt, {NULL, NULL}, {0, 0}, {false, false}};
    AcquireSRWLockExclusive(&g_scMu);
    for (;;) {
        while (!(g_sb.active && g_sb.next < g_sb.n)) SleepConditionVariableSRW(&g_scWork, &g_scMu, INFINITE, 0);
        ScJob* j = &g_sb.jobs[g_sb.next++];
        if (j->state != SC_QUEUED) continue;  // the sim thread took it
        j->state = SC_RUNNING;
        ReleaseSRWLockExclusive(&g_scMu);
        ScRun(j, &a);
        AcquireSRWLockExclusive(&g_scMu);
        j->state = SC_DONE;
        WakeAllConditionVariable(&g_scDone);
    }
}

static bool ScStartThreads() {
    while (g_scStarted < g_scThreads) {
        HANDLE t = CreateThread(NULL, 256 * 1024, ScWorker, NULL, STACK_SIZE_PARAM_IS_A_RESERVATION, NULL);
        if (!t) break;
        CloseHandle(t);
        g_scStarted++;
    }
    return g_scStarted > 0;
}

static void ScFreeJob(ScJob* j) {  // sim thread; the job is not running
    ScBatch& b = g_sb;
    if (j->state == SC_FREED) return;
    if (j->state == SC_DONE && j->outLen > 0) b.inBytes += j->len;
    b.inflight -= (LONG64)j->len + ScOutCap(j->len);
    if (j->in) HeapFree(GetProcessHeap(), 0, j->in);
    if (j->out) HeapFree(GetProcessHeap(), 0, j->out);
    j->in = j->out = NULL;
    j->state = SC_FREED;
    while (b.cursor < b.n && b.jobs[b.cursor].state == SC_FREED) b.cursor++;
}

// EASTL rbtree increment (FUN_004E0B20): node +0 right, +4 left, +8 parent.
static u8* ScNext(u8* node) {
    u8* r = *(u8**)node;
    if (r) {
        while (*(u8**)(r + 4)) r = *(u8**)(r + 4);
        return r;
    }
    u8* p = *(u8**)(node + 8);
    while (node == *(u8**)p) { node = p; p = *(u8**)(p + 8); }
    return *(u8**)node != p ? p : node;
}

// Sim thread: copies the bytes of one pending record and queues a job (false = record skipped).
static bool ScQueue(u8* rec) {
    ScBatch& b = g_sb;
    if (!rec || *(const DWORD*)(rec + 8) != kScRecClass || b.n >= b.cap) return false;
    void* s = rec + 0x28;
    void** vt = *(void***)s;
    const DWORD len = ((ScSizeFn)vt[0x1C / 4])(s, NULL);
    if (len < kScMinLen || len > kScMaxLen) return false;
    u8* in = (u8*)HeapAlloc(GetProcessHeap(), 0, len);
    if (!in) return false;
    // the same seek and read FUN_004A8910 does next (it seeks to 0 again before reading)
    if (!(((ScSeekFn)vt[0x28 / 4])(s, NULL, 0, 0) & 0xFF) || ((ScReadFn)vt[0x30 / 4])(s, NULL, in, len) != len) {
        HeapFree(GetProcessHeap(), 0, in);
        return false;
    }
    ScJob j = {in, len, ScPredictMode(b.pkg, len), NULL, 0, SC_QUEUED};
    b.inflight += (LONG64)len + ScOutCap(len);
    if (b.inflight > b.peak) b.peak = b.inflight;
    AcquireSRWLockExclusive(&g_scMu);
    b.jobs[b.n++] = j;
    WakeConditionVariable(&g_scWork);
    ReleaseSRWLockExclusive(&g_scMu);
    return true;
}

// Sim thread: queue the next pending records while fewer than MaxMB are in flight. Pending records are committed leftmost
// first, so the node `walked` places after the start's leftmost is `walked - committed` places after the current one.
static void ScRefill() {
    ScBatch& b = g_sb;
    u8* const anchor = b.pkg + 0x2EC;
    const int count = *(const int*)(b.pkg + 0x2FC);
    const int committed = b.count0 - count;
    if (b.walked < committed) b.walked = committed;
    if (b.walked - committed >= count || (b.inflight >= b.maxBytes && b.cursor < b.n)) return;
    u8* node = *(u8**)(b.pkg + 0x2F0);
    for (int i = committed; i < b.walked && node != anchor; i++) node = ScNext(node);
    while (node != anchor && b.walked - committed < count && b.n < b.cap && (b.inflight < b.maxBytes || b.cursor == b.n)) {
        b.walked++;
        ScQueue(*(u8**)(node + 0x20));
        node = ScNext(node);
    }
}

// Sim thread: the job is done (waits for a worker, or runs it here if no worker took it yet).
static void ScWait(ScJob* j) {
    LARGE_INTEGER t0, t1;
    QueryPerformanceCounter(&t0);
    AcquireSRWLockExclusive(&g_scMu);
    if (j->state == SC_QUEUED) {
        j->state = SC_RUNNING;
        ReleaseSRWLockExclusive(&g_scMu);
        static ScAlloc s_own = {g_scAllocVt, {NULL, NULL}, {0, 0}, {false, false}};  // only the batch thread steals
        ScRun(j, &s_own);
        AcquireSRWLockExclusive(&g_scMu);
        j->state = SC_DONE;
        g_sb.stolen++;
    }
    while (j->state == SC_RUNNING) SleepConditionVariableSRW(&g_scDone, &g_scMu, INFINITE, 0);
    ReleaseSRWLockExclusive(&g_scMu);
    QueryPerformanceCounter(&t1);
    g_sb.waitTicks += t1.QuadPart - t0.QuadPart;
}

static ScJob* ScFind(const u8* src, DWORD len, DWORD mode) {
    ScBatch& b = g_sb;
    for (int i = b.cursor; i < b.n; i++) {
        ScJob* j = &b.jobs[i];
        if (j->state != SC_FREED && j->len == len && j->mode == mode && !memcmp(j->in, src, len)) return j;
    }
    return NULL;
}

// --- compressed-bytes cache (docs/26 §8) ---
// The compressor's output depends only on (input bytes, length, mode) (docs/26 §8.2), so a compress call whose input
// equals an earlier call's gets that call's output: what the game would write, byte for byte. Keyed by (length, mode,
// 64-bit hash); least recently used entries go first to stay under [SaveCompress] CacheMB. Any thread (g_ccMu).
struct CcEntry { CcEntry *hnext, *prev, *next; unsigned __int64 h; DWORD len, mode; int outLen; };  // output bytes follow
static const DWORD kCcBuckets = 1 << 14;
static CcEntry* g_ccTab[kCcBuckets];
static CcEntry g_ccLru = {NULL, &g_ccLru, &g_ccLru, 0, 0, 0, 0};  // anchor: next = most recently used
static SRWLOCK g_ccMu = SRWLOCK_INIT;
static LONG64 g_ccBytes, g_ccMax;  // compressed bytes held; budget (0 = cache off)
static volatile LONG64 g_ccHashTicks, g_ccHitBytes;

// Two different chains, each step a bijection of the state and of the word, 4 lanes each, then a bijective fold: inputs
// of the same length that differ in one 32-bit word never collide; other differences about 2^-64 [inferred].
static unsigned __int64 CcHash(const u8* p, DWORD n) {
    DWORD a[4] = {0x9E3779B1, 0x85EBCA77, 0xC2B2AE3D, 0x27D4EB2F}, b[4] = {0x165667B1, 0xD3A2646C, 0xFD7046C5, 0xB55A4F09};
    DWORD i = 0;
    for (; i + 16 <= n; i += 16)
        for (int l = 0; l < 4; l++) {
            const DWORD x = *(const DWORD*)(p + i + 4 * l);  // x86: unaligned reads are fine
            a[l] = _rotl(a[l] ^ x, 13) * 0x9E3779B1u;
            b[l] = _rotl(b[l] + x, 17) * 0x85EBCA77u;
        }
    for (int l = 0; i < n; i++, l = (l + 1) & 3) {
        a[l] = _rotl(a[l] ^ p[i], 13) * 0x9E3779B1u;
        b[l] = _rotl(b[l] + p[i], 17) * 0x85EBCA77u;
    }
    DWORD ha = n, hb = ~n;
    for (int l = 0; l < 4; l++) {
        ha = _rotl((ha * 0x27D4EB2Fu) ^ a[l], 11);
        hb = _rotl((hb * 0x165667B1u) + b[l], 7);
    }
    return ((unsigned __int64)ha << 32) | hb;
}
static CcEntry** CcFind(unsigned __int64 h, DWORD len, DWORD mode) {  // under g_ccMu: the link that holds the entry, or NULL
    CcEntry** pp = &g_ccTab[(DWORD)h & (kCcBuckets - 1)];
    while (*pp && !((*pp)->h == h && (*pp)->len == len && (*pp)->mode == mode)) pp = &(*pp)->hnext;
    return pp;
}
static void CcFront(CcEntry* e) { e->next = g_ccLru.next; e->prev = &g_ccLru; g_ccLru.next->prev = e; g_ccLru.next = e; }
static void CcUnlink(CcEntry* e) { e->prev->next = e->next; e->next->prev = e->prev; }
static void CcDrop(CcEntry* e) {  // under g_ccMu
    Ts3nCounters& k = ts3native_counters;
    CcEntry** pp = CcFind(e->h, e->len, e->mode);
    *pp = e->hnext;
    CcUnlink(e);
    g_ccBytes -= e->outLen;
    k.cacheEntries--;
    k.cacheKB = (LONG)(g_ccBytes >> 10);
    HeapFree(GetProcessHeap(), 0, e);
}
static int CcGet(unsigned __int64 h, DWORD len, DWORD mode, u8* dst, DWORD cap) {  // output length, or -1
    int r = -1;
    AcquireSRWLockExclusive(&g_ccMu);
    CcEntry* e = *CcFind(h, len, mode);
    if (e && (DWORD)e->outLen <= cap) {
        memcpy(dst, e + 1, (size_t)e->outLen);
        r = e->outLen;
        CcUnlink(e);
        CcFront(e);
    }
    ReleaseSRWLockExclusive(&g_ccMu);
    return r;
}
static void CcPut(unsigned __int64 h, DWORD len, DWORD mode, const u8* out, int outLen) {
    Ts3nCounters& k = ts3native_counters;
    if (outLen <= 0 || (LONG64)outLen > g_ccMax / 4) return;  // one record never takes over the cache
    CcEntry* e = (CcEntry*)HeapAlloc(GetProcessHeap(), 0, sizeof(CcEntry) + (size_t)outLen);
    if (!e) return;
    e->hnext = NULL; e->h = h; e->len = len; e->mode = mode; e->outLen = outLen;
    memcpy(e + 1, out, (size_t)outLen);
    AcquireSRWLockExclusive(&g_ccMu);
    CcEntry** pp = CcFind(h, len, mode);
    if (*pp) {  // another thread stored it first
        ReleaseSRWLockExclusive(&g_ccMu);
        HeapFree(GetProcessHeap(), 0, e);
        return;
    }
    *pp = e;
    CcFront(e);
    g_ccBytes += outLen;
    k.cacheEntries++;
    while (g_ccBytes > g_ccMax) {
        CcDrop(g_ccLru.prev);
        k.cacheEvicted++;
    }
    k.cacheKB = (LONG)(g_ccBytes >> 10);
    ReleaseSRWLockExclusive(&g_ccMu);
}
// A compress call (src and dst set) through the cache. Verify: the game always compresses; a stored entry must equal it.
static int CcSlot(void* codec, const u8* src, DWORD len, u8* dst, DWORD cap, DWORD flags) {
    Ts3nCounters& k = ts3native_counters;
    const DWORD mode = ScModeOf(flags);
    LARGE_INTEGER t0, t1;
    QueryPerformanceCounter(&t0);
    const unsigned __int64 h = CcHash(src, len);
    QueryPerformanceCounter(&t1);
    k.cacheHashMs = (LONG)(InterlockedAdd64(&g_ccHashTicks, t1.QuadPart - t0.QuadPart) * 1000 / (g_qpf.QuadPart ? g_qpf.QuadPart : 1));
    if (!k.compressVerify) {
        const int r = CcGet(h, len, mode, dst, cap);
        if (r > 0) {
            InterlockedIncrement(&k.cacheHits);
            k.cacheHitMB = (LONG)(InterlockedAdd64(&g_ccHitBytes, len) >> 20);
            return r;
        }
    }
    const int r = g_scSlotOrig(codec, NULL, src, len, dst, cap, flags);
    if (k.compressVerify) {
        bool had = false, bad = false;
        AcquireSRWLockExclusive(&g_ccMu);
        CcEntry* e = *CcFind(h, len, mode);
        if (e) {
            had = true;
            bad = e->outLen != r || memcmp(e + 1, dst, (size_t)r) != 0;
            if (bad) CcDrop(e);
        }
        ReleaseSRWLockExclusive(&g_ccMu);
        if (had) {
            InterlockedIncrement(&k.cacheHits);
            k.cacheHitMB = (LONG)(InterlockedAdd64(&g_ccHitBytes, len) >> 20);
            if (bad && InterlockedIncrement(&k.cacheMismatches) <= 5) Log("save compress cache MISMATCH: len %lu flags 0x%lX game %d", len, flags, r);
            return r;
        }
    }
    InterlockedIncrement(&k.cacheMisses);
    if (r > 0 && (DWORD)r <= cap) CcPut(h, len, mode, dst, r);
    return r;
}

// Codec vtable slot 1 (thiscall, ret 14h). Outside a batch, or on another thread, it is the game's slot (or the cache).
static int __fastcall ScSlotHook(void* codec, void*, const u8* src, DWORD len, u8* dst, DWORD cap, DWORD flags) {
    ScBatch& b = g_sb;
    Ts3nCounters& k = ts3native_counters;
    if (!b.active || b.tid != GetCurrentThreadId() || !k.compressMode || (!dst && (flags & 1)) || !src || len < kScMinLen) {
        if (!dst || (b.active && b.tid == GetCurrentThreadId())) return g_scSlotOrig(codec, NULL, src, len, dst, cap, flags);
        // outside a batch: count and time it (a save commits each record when the writer closes it, 0x4AD9F0 -> vt+0x3C)
        LARGE_INTEGER t0, t1;
        QueryPerformanceCounter(&t0);
        const int r = g_ccMax && k.cacheMode && src ? CcSlot(codec, src, len, dst, cap, flags) : g_scSlotOrig(codec, NULL, src, len, dst, cap, flags);
        QueryPerformanceCounter(&t1);
        static volatile LONG64 s_ticks, s_bytes;
        const LONG64 f = g_qpf.QuadPart ? g_qpf.QuadPart : 1;
        InterlockedIncrement(&k.compressDirectCalls);
        k.compressDirectMs = (LONG)(InterlockedAdd64(&s_ticks, t1.QuadPart - t0.QuadPart) * 1000 / f);
        k.compressDirectMB = (LONG)(InterlockedAdd64(&s_bytes, len) >> 20);
        return r;
    }
    ScJob* j = ScFind(src, len, ScModeOf(flags));
    int r = -1;
    if (j) {
        ScWait(j);
        r = j->outLen;
    }
    if (r <= 0 || (dst && (DWORD)r > cap)) {  // not prepared, failed, or would not fit: the game's own call
        b.misses++;
        if (j && dst) ScFreeJob(j);
        ScRefill();
        return g_scSlotOrig(codec, NULL, src, len, dst, cap, flags);
    }
    if (k.compressVerify) {  // the game compresses too; its result is the one used
        const int r2 = g_scSlotOrig(codec, NULL, src, len, dst, cap, flags);
        if (r2 != r || (dst && memcmp(dst, j->out, (size_t)r) != 0)) {
            if (InterlockedIncrement(&k.compressMismatches) <= 5) Log("save compress MISMATCH: len %lu flags 0x%lX game %d ours %d", len, flags, r2, r);
            b.mismatches++;
        }
        r = r2;
    } else if (dst) {
        memcpy(dst, j->out, (size_t)r);
    }
    b.hits++;
    if (dst) {  // a size-only call (dst 0) is followed by the real one with the same input: keep the job until then
        ScFreeJob(j);
        ScRefill();
    }
    return r;
}

static void ScEndBatch() {
    ScBatch& b = g_sb;
    AcquireSRWLockExclusive(&g_scMu);
    b.active = false;
    for (int i = 0; i < b.n; i++)
        if (b.jobs[i].state == SC_QUEUED) b.jobs[i].state = SC_CANCELLED;
    for (int i = 0; i < b.n; i++)
        while (b.jobs[i].state == SC_RUNNING) SleepConditionVariableSRW(&g_scDone, &g_scMu, INFINITE, 0);
    ReleaseSRWLockExclusive(&g_scMu);
    for (int i = 0; i < b.n; i++) {
        ScFreeJob(&b.jobs[i]);
    }
    AcquireSRWLockExclusive(&g_scMu);
    b.n = b.next = 0;  // workers see an empty queue before the array goes
    ReleaseSRWLockExclusive(&g_scMu);
    HeapFree(GetProcessHeap(), 0, b.jobs);
    b.jobs = NULL;
}

// Replaces the call to FUN_004A7910 (__fastcall, ecx = package) in the commit FUN_004A8500.
static DWORD __fastcall ScCommitHook(void* pkg, void*) {
    Ts3nCounters& k = ts3native_counters;
    u8* p = (u8*)pkg;
    if (p) {
        InterlockedIncrement(&k.compressCommits);
        k.compressCommitPending = *(const int*)(p + 0x2FC);
    }
    if (!k.compressMode || !p || InterlockedCompareExchange(&g_scBusy, 1, 0) != 0) return g_scCommitOrig(pkg, NULL);
    const int count = *(const int*)(p + 0x2FC);
    if (!(*(const DWORD*)(p + 0x14) & 2) || !*(const int*)(p + 0x20) || count <= 0 || !ScStartThreads()) {
        InterlockedExchange(&g_scBusy, 0);
        return g_scCommitOrig(pkg, NULL);
    }
    ScBatch& b = g_sb;
    AcquireSRWLockExclusive(&g_scMu);  // idle workers read active/next/n under this lock
    const LONG64 maxBytes = b.maxBytes;
    memset(&b, 0, sizeof(b));
    b.maxBytes = maxBytes;
    ReleaseSRWLockExclusive(&g_scMu);
    b.jobs = (ScJob*)HeapAlloc(GetProcessHeap(), 0, (size_t)count * sizeof(ScJob));
    if (!b.jobs) { InterlockedExchange(&g_scBusy, 0); return g_scCommitOrig(pkg, NULL); }
    b.cap = count;
    b.count0 = count;
    b.pkg = p;
    b.tid = GetCurrentThreadId();
    LARGE_INTEGER t0;
    QueryPerformanceCounter(&t0);
    AcquireSRWLockExclusive(&g_scMu);
    b.active = true;
    ReleaseSRWLockExclusive(&g_scMu);
    g_scLock(p + 0x270, NULL, (const DWORD*)(uintptr_t)SC_LOCK_DEADLINE);  // the lock FUN_004A8E00 takes per record
    ScRefill();
    g_scUnlock(p + 0x270, NULL);
    const DWORD r = g_scCommitOrig(pkg, NULL);
    const int jobs = b.n;
    ScEndBatch();
    const LONG ms = UsSince(t0) / 1000;
    const LONG64 f = g_qpf.QuadPart ? g_qpf.QuadPart : 1;
    const LONG waitMs = (LONG)(b.waitTicks * 1000 / f);
    g_scInTotal += b.inBytes;
    InterlockedIncrement(&k.compressBatches);
    k.compressJobs += jobs;
    k.compressHits += b.hits;
    k.compressMisses += b.misses;
    k.compressInMB = (LONG)(g_scInTotal >> 20);
    k.compressWaitMs += waitMs;
    k.compressLastMs = ms;
    Max(&k.compressPeakMB, (LONG)(b.peak >> 20));
    if (jobs)
        Log("save compress: %d records, %d jobs (%lld MB), hits %ld, misses %ld, run here %ld, mismatches %ld, commit %ld ms, wait %ld ms, peak %lld MB",
            count, jobs, b.inBytes >> 20, b.hits, b.misses, b.stolen, b.mismatches, ms, waitMs, b.peak >> 20);
    InterlockedExchange(&g_scBusy, 0);
    return r;
}

// [SaveCompress]: Enabled=1 hooks the commit call and the codec slot; Mode is the start value of compressMode.
static bool ApplySaveCompress(const wchar_t* ini) {
    if (!GetPrivateProfileIntW(L"SaveCompress", L"Enabled", 0, ini)) { Log("save compress off (Enabled=0)"); return false; }
    if (ApexStandsDown(AXF_FAST_CACHE, "[SaveCompress]", "codec slot 0xFB901C")) return false;
    QueryPerformanceFrequency(&g_qpf);
    Ts3nCounters& k = ts3native_counters;
    k.compressMode = GetPrivateProfileIntW(L"SaveCompress", L"Mode", 1, ini) ? 1 : 0;
    k.compressVerify = GetPrivateProfileIntW(L"SaveCompress", L"Verify", 0, ini) ? 1 : 0;
    g_scThreads = Clamp(GetPrivateProfileIntW(L"SaveCompress", L"Threads", 6, ini), 1, 8);
    g_sb.maxBytes = (LONG64)Clamp(GetPrivateProfileIntW(L"SaveCompress", L"MaxMB", 32, ini), 4, 256) << 20;
    g_ccMax = (LONG64)Clamp(GetPrivateProfileIntW(L"SaveCompress", L"CacheMB", 0, ini), 0, 256) << 20;
    k.cacheMode = 1;
    for (int i = 0; i < 3; i++)
        if (!Readable((void*)(uintptr_t)kScCode[i][0], kScCode[i][1] - kScCode[i][0])) { Log("SKIP save compress: code unreadable"); return false; }
    if (ScCodeHash() != SC_CODE_FNV) { Log("SKIP save compress: compressor code differs from Steam 1.67 (0x%08lX), nothing written", ScCodeHash()); return false; }
    const DWORD* slot = (const DWORD*)(uintptr_t)SC_SLOT;
    if (!Readable(slot, 4) || *slot != SC_SLOT_FN) { Log("SKIP save compress: codec slot 0x%08X is not 0x%08X, nothing written", SC_SLOT, SC_SLOT_FN); return false; }
    const CallSite site = {SC_COMMIT_SITE, SC_COMMIT, (void*)ScCommitHook};
    if (!PatchCalls("save compress commit", &site, 1)) return false;
    const DWORD hook = (DWORD)(uintptr_t)ScSlotHook;
    if (!Write((void*)(uintptr_t)SC_SLOT, &hook, 4)) {
        Log("FAIL save compress: codec slot not writable (err %lu); the commit hook stays and only runs plain commits", GetLastError());
        k.compressMode = 0;
        return false;
    }
    k.compressReady = 1;
    k.cacheReady = g_ccMax > 0;
    Log("OK   save compress: commit 0x%08X, codec slot 0x%08X -> ScSlotHook, mode %ld verify %ld, threads %d, MaxMB %lld, CacheMB %lld", SC_COMMIT_SITE,
        SC_SLOT, k.compressMode, k.compressVerify, g_scThreads, g_sb.maxBytes >> 20, g_ccMax >> 20);
    return true;
}
