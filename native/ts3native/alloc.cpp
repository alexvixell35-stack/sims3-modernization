// Phase 8: per-thread cache in front of EA's general allocator (docs/15 phase 8). Included by ts3native.cpp (one translation unit).
//
// EA's GeneralAllocator (a dlmalloc 2.7 derivative, 0x518-byte heap objects: "Services/MemoryMan/MemoryManHeap", "Audio/Allocator", ...)
// has one CRITICAL_SECTION per heap (+0x4E4, spin count 10). Its locked entry points:
//   0x4E69D0 Malloc(size, flags)   __thiscall, ret 8: lock, 0x4E5E40, unlock   (179 call sites + ICoreAllocator::Alloc slot +8)
//   0x4E6A20 Memalign(size, align, offset, flags)  ret 10h: lock, 0x4E63F0 (= 0x4E5E40 when align <= 16, offset 0), unlock
//            (60 call sites + ICoreAllocator::Alloc slot +4 0x4E3E90 of the default allocator, vtable 0xFB8B38)
//   0x4E5920 Free(ptr)             __thiscall, ret 4: lock, 0x4E51B0, unlock   (307 call sites + ICoreAllocator::Free 0x4E3EF0)
//   0x4E5CB0 Shutdown()            __thiscall: frees the heap's own VirtualAlloc cores (also via the jmp thunk 0x4E69C0)
// Chunks: 16-byte header {prev_size, size|flags, fd, bk}, user pointer = chunk + 0x10, sizes are multiples of 16, size word at p-0xC
// (bit 0 = previous chunk in use, 2 = direct VirtualAlloc, 4 / 0x80000000 = in a fastbin or debug-tagged). A chunk is in use when
// the NEXT chunk's bit 0 is set. Fastbins only cover chunks <= 64 bytes; everything larger goes through the unsorted-bin sort,
// and any request >= 512 bytes first consolidates all fastbins (0x4E5960).
//
// The cache keeps freed small chunks (16..1024 bytes) per thread and per heap and hands them back to Malloc of the same chunk size
// without the lock and without 0x4E5E40. A cached chunk stays "in use" from the heap's point of view: its header, its neighbour's
// in-use bit and every byte of user data are left as Free received them (the link lives at p-4, the header word the fastbin
// uses too), so realloc, memalign, heap walks and the debug layer see exactly what they would see for a live allocation.
// No memory is reserved: cached chunks are already-committed heap memory, bounded by ThreadKB per thread and MaxMB in total.

static const DWORD EA_MALLOC = 0x004E69D0, EA_MEMALIGN = 0x004E6A20, EA_FREE = 0x004E5920, EA_SHUTDOWN = 0x004E5CB0;
// push esi ; push edi ; mov edi,ecx ; mov esi,[edi+4E4h]   (same 10 bytes at Malloc, Memalign and Free)
static const u8 kEaLockedPrologue[] = {0x56, 0x57, 0x8B, 0xF9, 0x8B, 0xB7, 0xE4, 0x04, 0x00, 0x00};
// push ebp ; mov ebp,esp ; sub esp,14h
static const u8 kEaShutdownPrologue[] = {0x55, 0x8B, 0xEC, 0x83, 0xEC, 0x14};

typedef void* (__fastcall* EaMallocFn)(void* heap, void* edx, DWORD size, DWORD flags);
typedef void* (__fastcall* EaMemalignFn)(void* heap, void* edx, DWORD size, DWORD align, DWORD offset, DWORD flags);
typedef void(__fastcall* EaFreeFn)(void* heap, void* edx, void* p);
typedef DWORD(__fastcall* EaShutdownFn)(void* heap, void* edx);
static EaMallocFn g_eaMallocOrig;
static EaMemalignFn g_eaMemalignOrig;
static EaFreeFn g_eaFreeOrig;
static EaShutdownFn g_eaShutdownOrig;

static const DWORD kAcMaxChunk = 1024;            // largest cached chunk; one bin per 16 bytes
static const DWORD kAcBins = kAcMaxChunk / 16;
static const int kAcHeaps = 4;                    // heaps one thread caches for (shortcut: a 5th heap is not cached, raise if allocPassed is high)
static const int kAcDead = 16;                    // remembered heap shutdowns

struct AcBin { u8* head; DWORD count; };
struct AcHeap { void* heap; AcBin bins[kAcBins]; };
struct AcThread {
    LONG gen;          // g_acGen when this cache was last checked
    DWORD bytes;       // chunk bytes held
    LONG hits, misses, cached, passed;  // not yet published
    DWORD ops;
    AcHeap heaps[kAcHeaps];
};
struct AcStats { volatile LONG64 hits, misses, cached, passed; };
static AcStats g_astat;
static DWORD g_acFls = FLS_OUT_OF_INDEXES;
static DWORD g_acThreadBytes = 64 * 1024, g_acPerBin = 32;
static LONG g_acThreadsMax = 64;
static volatile LONG g_acThreads;                // threads holding a ThreadKB quota
static volatile LONG g_acGen;                    // heap shutdowns so far
static void* g_acDead[kAcDead];                  // heap of shutdown n at [n % kAcDead]
static SRWLOCK g_acDeadLock = SRWLOCK_INIT;
static volatile LONG64 g_aNextLog;
static AcThread* const kAcDenied = (AcThread*)1;  // FLS marker: no quota left, this thread runs uncached

static DWORD AcChunk(DWORD n) { return n + 0x1B <= 0x10 ? 0x10 : (n + 0x1B) & 0xFFFFFFF0; }  // the game's request2size 0x4E4770

static void AcPublish(AcThread* t) {
    InterlockedAdd64(&g_astat.hits, t->hits);
    InterlockedAdd64(&g_astat.misses, t->misses);
    InterlockedAdd64(&g_astat.cached, t->cached);
    InterlockedAdd64(&g_astat.passed, t->passed);
    t->hits = t->misses = t->cached = t->passed = 0;
    t->ops = 0;
    Ts3nCounters& k = ts3native_counters;
    k.allocHitsK = (LONG)(g_astat.hits >> 10);
    k.allocMissesK = (LONG)(g_astat.misses >> 10);
    k.allocCachedK = (LONG)(g_astat.cached >> 10);
    k.allocPassedK = (LONG)(g_astat.passed >> 10);
    const ULONGLONG now = GetTickCount64();
    const LONG64 next = g_aNextLog;
    if ((LONG64)now >= next && InterlockedCompareExchange64(&g_aNextLog, (LONG64)now + 60000, next) == next)
        Log("alloc cache: hits %lldK misses %lldK cached %lldK passed %lldK threads %ld flushed %ld dropped %ld shutdowns %ld oom %ld",
            g_astat.hits >> 10, g_astat.misses >> 10, g_astat.cached >> 10, g_astat.passed >> 10, k.allocThreads, k.allocFlushed,
            k.allocDropped, k.allocShutdowns, k.allocOomRetries);
}

// Hands every chunk of one heap slot back to the heap (`give`) or forgets them (heap already shut down).
static void AcEmpty(AcThread* t, AcHeap* h, bool give) {
    LONG n = 0;
    for (DWORD i = 0; i < kAcBins; i++) {
        DWORD left = h->bins[i].count;  // bounded: a double free (Dedupe=0) can make a bin link to itself
        for (u8* p = h->bins[i].head; p && left; left--) {
            u8* next = *(u8**)(p - 4);
            if (give) g_eaFreeOrig(h->heap, NULL, p);
            t->bytes -= (i + 1) * 16;
            n++;
            p = next;
        }
        h->bins[i].head = NULL;
        h->bins[i].count = 0;
    }
    h->heap = NULL;
    InterlockedAdd(give ? &ts3native_counters.allocFlushed : &ts3native_counters.allocDropped, n);
}

// A heap shut down since this thread last looked: forget that heap's chunks, return the others (their heap may be the next to go).
static void AcCatchUp(AcThread* t) {
    void* dead[kAcDead];
    AcquireSRWLockShared(&g_acDeadLock);  // snapshot only: no heap lock is taken while holding it
    const LONG gen = g_acGen;
    memcpy(dead, g_acDead, sizeof(dead));
    ReleaseSRWLockShared(&g_acDeadLock);
    const bool all = gen - t->gen > kAcDead;
    for (int i = 0; i < kAcHeaps; i++) {
        AcHeap* h = &t->heaps[i];
        if (!h->heap) continue;
        bool gone = all;
        for (LONG g = t->gen; !gone && g != gen; g++) gone = dead[g % kAcDead] == h->heap;
        AcEmpty(t, h, !gone);
    }
    t->gen = gen;
}

static AcThread* AcSelf(bool create) {
    AcThread* t = (AcThread*)FlsGetValue(g_acFls);
    if (t == kAcDenied) return NULL;
    if (t) {
        if (t->gen != g_acGen) AcCatchUp(t);
        return t;
    }
    if (!create) return NULL;
    if (InterlockedIncrement(&g_acThreads) > g_acThreadsMax) {
        InterlockedDecrement(&g_acThreads);
        FlsSetValue(g_acFls, kAcDenied);
        return NULL;
    }
    t = (AcThread*)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, sizeof(AcThread));
    if (!t) { InterlockedDecrement(&g_acThreads); return NULL; }
    t->gen = g_acGen;
    FlsSetValue(g_acFls, t);
    ts3native_counters.allocThreads = g_acThreads;
    return t;
}

// Thread exit (FLS callback, the same place the CRT frees its per-thread data): return the chunks, release the quota.
static void WINAPI AcThreadExit(void* v) {
    AcThread* t = (AcThread*)v;
    if (!t || t == kAcDenied) return;
    if (t->gen != g_acGen) AcCatchUp(t);
    for (int i = 0; i < kAcHeaps; i++)
        if (t->heaps[i].heap) AcEmpty(t, &t->heaps[i], true);
    AcPublish(t);
    ts3native_counters.allocThreads = InterlockedDecrement(&g_acThreads);
    HeapFree(GetProcessHeap(), 0, t);
}

static AcHeap* AcFind(AcThread* t, void* heap, bool claim) {
    AcHeap* empty = NULL;
    for (int i = 0; i < kAcHeaps; i++) {
        if (t->heaps[i].heap == heap) return &t->heaps[i];
        if (!empty && !t->heaps[i].heap) empty = &t->heaps[i];
    }
    if (claim && empty) empty->heap = heap;
    return claim ? empty : NULL;
}

// A cached chunk of exactly the size 0x4E5E40 would carve for (n, flags), or NULL.
static u8* AcPop(void* heap, DWORD n, DWORD flags) {
    // flags bit 3 = direct VirtualAlloc block (0x4E5E40 takes another path); n first so AcChunk cannot wrap
    if (n >= kAcMaxChunk || (flags & 8) || !ts3native_counters.allocMode) return NULL;
    AcThread* t = AcSelf(false);
    if (!t) return NULL;
    const DWORD sz = AcChunk(n);
    AcHeap* h = AcFind(t, heap, false);
    AcBin* b = h && sz <= kAcMaxChunk ? &h->bins[sz / 16 - 1] : NULL;
    if (++t->ops >= 1024) AcPublish(t);
    if (!b || !b->head || !b->count) { t->misses++; return NULL; }  // count 0 with a head: corrupted bin, treat as empty (docs/67)
    u8* p = b->head;
    b->head = *(u8**)(p - 4);
    b->count--;
    t->bytes -= sz;
    t->hits++;
    return p;
}

// Out of memory: give this thread's cached chunks of the heap back; true when the caller should try once more.
static bool AcOomFlush(void* heap) {
    AcThread* t = AcSelf(false);
    AcHeap* h = t ? AcFind(t, heap, false) : NULL;
    if (!h) return false;
    AcEmpty(t, h, true);
    InterlockedIncrement(&ts3native_counters.allocOomRetries);
    return true;
}

static void* __fastcall EaMallocHook(void* heap, void* edx, DWORD n, DWORD flags) {
    if (void* p = AcPop(heap, n, flags)) return p;
    void* r = g_eaMallocOrig(heap, edx, n, flags);
    if (!r && AcOomFlush(heap)) r = g_eaMallocOrig(heap, edx, n, flags);
    return r;
}

// 0x4E6A20 (ICoreAllocator::Alloc with alignment, slot +4 of vtable 0xFB8B38): 0x4E63F0 calls 0x4E5E40(n, flags) unchanged
// when align <= 16 and offset == 0, so only that case may come from the cache.
static void* __fastcall EaMemalignHook(void* heap, void* edx, DWORD n, DWORD align, DWORD offset, DWORD flags) {
    if (align <= 16 && !offset)
        if (void* p = AcPop(heap, n, flags)) return p;
    void* r = g_eaMemalignOrig(heap, edx, n, align, offset, flags);
    if (!r && AcOomFlush(heap)) r = g_eaMemalignOrig(heap, edx, n, align, offset, flags);
    return r;
}

// True when p is already in this bin. Walks at most count links (<= PerBin), so a looped bin cannot hang it.
// shortcut: same thread only; a double free across threads needs a marker in the chunk (docs/67 §2.4), add it if allocDupFrees
// shows dups here.
static bool AcInBin(const AcBin* b, const u8* p) {
    const u8* q = b->head;
    for (DWORD i = 0; q && i < b->count; i++, q = *(u8* const*)(q - 4))
        if (q == p) return true;
    return false;
}

static void __fastcall EaFreeHook(void* heap, void* edx, void* ptr) {
    if (ptr && ts3native_counters.allocMode) {
        u8* p = (u8*)ptr;
        const DWORD hdr = *(DWORD*)(p - 0xC), sz = hdr & 0x7FFFFFF8;
        // plain heap chunk (not direct-mapped, not fastbin/debug-tagged), cacheable size, and in use (next chunk's bit 0):
        // anything else, e.g. a double free, goes to the game's Free exactly as before
        if (!(hdr & 0x80000006) && !(sz & 0xF) && sz >= 16 && sz <= kAcMaxChunk && (*(DWORD*)(p - 0x10 + sz + 4) & 1)) {
            AcThread* t = AcSelf(true);
            if (t) {
                AcHeap* h = AcFind(t, heap, true);
                AcBin* b = h ? &h->bins[sz / 16 - 1] : NULL;
                if (++t->ops >= 1024) AcPublish(t);
                if (b && AcInBin(b, p)) {
                    // second Free of a chunk this thread already holds (docs/67): pushing it again would hand it to two owners
                    InterlockedIncrement(&ts3native_counters.allocDupFrees);
                    if (ts3native_counters.allocDedupe) return;  // the first Free still owns it
                }
                if (b && b->count < g_acPerBin && t->bytes + sz <= g_acThreadBytes) {
                    *(u8**)(p - 4) = b->head;
                    b->head = p;
                    b->count++;
                    t->bytes += sz;
                    t->cached++;
                    return;
                }
                t->passed++;
            }
        }
    }
    g_eaFreeOrig(heap, edx, ptr);
}

static DWORD __fastcall EaShutdownHook(void* heap, void* edx) {
    AcquireSRWLockExclusive(&g_acDeadLock);
    g_acDead[g_acGen % kAcDead] = heap;
    InterlockedIncrement(&g_acGen);
    ReleaseSRWLockExclusive(&g_acDeadLock);
    ts3native_counters.allocShutdowns = g_acGen;
    AcSelf(false);  // catches this thread up now: its chunks of `heap` are forgotten before the heap's cores go
    return g_eaShutdownOrig(heap, edx);
}

static bool AcInit(int threadKB, int maxMB, int perBin) {
    g_acThreadBytes = (DWORD)threadKB * 1024;
    g_acThreadsMax = maxMB * 1024 / threadKB;
    g_acPerBin = (DWORD)perBin;
    if (g_acFls == FLS_OUT_OF_INDEXES) g_acFls = FlsAlloc(AcThreadExit);
    g_aNextLog = (LONG64)GetTickCount64() + 60000;
    return g_acFls != FLS_OUT_OF_INDEXES;
}

// [Alloc]: returns true when the detours are installed (the log must then stay open for the periodic counters).
static bool ApplyAlloc(const wchar_t* ini) {
    if (!GetPrivateProfileIntW(L"Alloc", L"Enabled", 0, ini)) { Log("alloc cache off (Enabled=0)"); return false; }
    Ts3nCounters& c = ts3native_counters;
    const int threadKB = Clamp((int)GetPrivateProfileIntW(L"Alloc", L"ThreadKB", 64, ini), 16, 1024);
    const int maxMB = Clamp((int)GetPrivateProfileIntW(L"Alloc", L"MaxMB", 4, ini), 1, 32);
    const int perBin = Clamp((int)GetPrivateProfileIntW(L"Alloc", L"PerBin", 32, ini), 4, 256);
    c.allocMode = GetPrivateProfileIntW(L"Alloc", L"Mode", 1, ini) ? 1 : 0;
    c.allocDedupe = GetPrivateProfileIntW(L"Alloc", L"Dedupe", 0, ini) ? 1 : 0;
    if (!AcInit(threadKB, maxMB, perBin)) { Log("FAIL alloc cache: FlsAlloc (err %lu)", GetLastError()); return false; }
    // all four or none: the cache must see every shutdown of a heap it holds chunks of
    if (!Matches((void*)(uintptr_t)EA_MALLOC, kEaLockedPrologue, sizeof(kEaLockedPrologue)) ||
        !Matches((void*)(uintptr_t)EA_MEMALIGN, kEaLockedPrologue, sizeof(kEaLockedPrologue)) ||
        !Matches((void*)(uintptr_t)EA_FREE, kEaLockedPrologue, sizeof(kEaLockedPrologue)) ||
        !Matches((void*)(uintptr_t)EA_SHUTDOWN, kEaShutdownPrologue, sizeof(kEaShutdownPrologue))) {
        Log("SKIP alloc cache: EA allocator entry bytes differ (another patch there?), nothing written");
        return false;
    }
    g_eaShutdownOrig = (EaShutdownFn)InstallDetour("EA heap Shutdown", EA_SHUTDOWN, kEaShutdownPrologue, sizeof(kEaShutdownPrologue), (void*)EaShutdownHook, &g_eaShutdownOrig);
    g_eaFreeOrig = g_eaShutdownOrig ? (EaFreeFn)InstallDetour("EA heap Free", EA_FREE, kEaLockedPrologue, sizeof(kEaLockedPrologue), (void*)EaFreeHook, &g_eaFreeOrig) : NULL;
    g_eaMallocOrig = g_eaFreeOrig ? (EaMallocFn)InstallDetour("EA heap Malloc", EA_MALLOC, kEaLockedPrologue, sizeof(kEaLockedPrologue), (void*)EaMallocHook, &g_eaMallocOrig) : NULL;
    g_eaMemalignOrig = g_eaMallocOrig ? (EaMemalignFn)InstallDetour("EA heap Memalign", EA_MEMALIGN, kEaLockedPrologue, sizeof(kEaLockedPrologue), (void*)EaMemalignHook, &g_eaMemalignOrig) : NULL;
    if (!g_eaMemalignOrig) { Log("FAIL alloc cache: detour install"); return false; }  // shortcut: an earlier detour stays; it only counts/forwards
    c.allocReady = 1;
    Log("OK   alloc cache: mode %ld, chunks <= %lu bytes, %d KB per thread, %ld threads max (%d MB), %d per bin", c.allocMode, kAcMaxChunk,
        threadKB, g_acThreadsMax, maxMB, perBin);
    return true;
}
