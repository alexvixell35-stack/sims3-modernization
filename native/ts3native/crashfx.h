// [CrashForensics] ring core (docs/157 §11.7, default off). Included by ts3native.cpp before routesnap.cpp, so the P5 code
// (replay.cpp, p4clone.cpp) can record its EA-heap calls and g_rpUse holds; the hooks and the crash dump are in crashfx.cpp.
//
// Every EA_New 0x4E3F90 / EA_Free 0x4E3F70 call the P5 clone makes, and every g_rpUse acquire / release, goes into a lock-free
// ring of the last 4096 events {seq, address, size, thread, P5 phase, job running, tag, QPC}. Nothing is logged per event: one
// InterlockedIncrement, one QPC read and eight stores (~30-60 ns). The ring is read only when something is already wrong (a
// guarded site sees a bad pointer, a heap check fails, or an access violation reaches XcptLog).
//
// Heap check (EA layout verified in alloc.cpp: dlmalloc 2.7 chunks, size word at p-0xC, a chunk is in use when the NEXT chunk's
// bit 0 is set; EA_Free 0x4E3F70 passes the pointer unchanged to Free 0x4E5920 on [0x11CB864], docs/150 §14): before our free,
// a plain chunk whose next chunk says "previous not in use" is already free (a double free). Read only: no poison, the free goes
// on as before. Off when 0x4E5920 is neither the game's prologue nor our [Alloc] detour (another allocator owns the layout).

enum CfTag : u8 {
    CF_NONE, CF_COPY_NEW, CF_COPY_FREE, CF_VEC_NEW, CF_ROOM_FREE, CF_HASH_NEW, CF_HNODE_NEW, CF_HASH_FREE, CF_LEAF_NEW,
    CF_ASTAR_NEW, CF_WP_FREE, CF_USE_ACQ, CF_USE_REL, CF_NTAGS
};
static const char* const kCfTag[CF_NTAGS] = {
    "?", "copy-new", "copy-free", "vec-new", "room-free", "hash-new", "hnode-new", "hash-free", "leaf-new",
    "astar-new", "wp-free", "use-acq", "use-rel"
};
static bool CfIsFree(u8 tag) { return tag == CF_COPY_FREE || tag == CF_ROOM_FREE || tag == CF_HASH_FREE || tag == CF_WP_FREE; }

struct CfEnt {
    volatile LONG seq;  // written last: 0 or a stale value = the slot is being rewritten
    DWORD addr, size;   // size: the request (new) or the chunk size from the header (free, 0 = unknown)
    DWORD tid;
    LONG phase;         // g_rpPhase at the call (2 | type << 8 | 0x10000 = a RecsUnlocked / LotsUnlocked record)
    u8 tag, job, pad[2];
    LONGLONG qpc;
};
static_assert(sizeof(CfEnt) == 32, "crash forensics ring entry");
static const DWORD kCfRing = 4096;  // power of two
static CfEnt g_cfRing[kCfRing];
static volatile LONG g_cfSeq;
static LONG g_cfOn;                 // [CrashForensics] Enabled=1
static LONG g_cfHeap;               // heap check on (layout verified at install)
static volatile DWORD g_cfUseTid;   // g_rpUse holder (0 = free), set after acquire, cleared before release
static volatile LONG g_cfHeapLogs;
static LONGLONG g_cfQpf;

static void CfRec(u8 tag, const void* p, DWORD size) {
    if (!g_cfOn) return;
    const LONG s = InterlockedIncrement(&g_cfSeq);
    CfEnt& e = g_cfRing[(DWORD)(s - 1) & (kCfRing - 1)];
    e.seq = 0;
    e.addr = (DWORD)(uintptr_t)p, e.size = size, e.tid = GetCurrentThreadId(), e.phase = g_rpPhase;
    e.tag = tag, e.job = g_async.state == A_RUNNING;
    LARGE_INTEGER q;
    QueryPerformanceCounter(&q);
    e.qpc = q.QuadPart;
    _WriteBarrier();
    e.seq = s;
    ts3native_counters.cfRecs = s;  // plain store: the newest sequence number
}
static void CfUse(bool acq) {
    if (!g_cfOn) return;
    if (acq) g_cfUseTid = GetCurrentThreadId();
    CfRec(acq ? CF_USE_ACQ : CF_USE_REL, NULL, 0);
    if (!acq) g_cfUseTid = 0;
}

// newest ring entry whose block holds `a` (a block = [addr, addr + size), at least 16 bytes); false = none in the ring
static bool CfFind(DWORD a, CfEnt* out, LONG before = 0x7FFFFFFF) {
    const LONG top = g_cfSeq;
    for (LONG s = top; s > 0 && top - s < (LONG)kCfRing; s--) {
        const CfEnt& e = g_cfRing[(DWORD)(s - 1) & (kCfRing - 1)];
        if (e.seq != s || s >= before || !e.addr) continue;
        const DWORD n = e.size > 16 ? e.size : 16;
        if (a - e.addr < n) { *out = e; return true; }
    }
    return false;
}
// one line: "<tag> of 0x... (n bytes) Nms ago thread T phase P job J seq S" or "not in the ring"
static void CfDescribe(DWORD a, char* out, size_t cap, LONG before = 0x7FFFFFFF) {
    CfEnt e;
    if (!CfFind(a, &e, before)) { SFmt(out, cap, "not in the ring (last %lu P5 heap events)", kCfRing); return; }
    LARGE_INTEGER q;
    QueryPerformanceCounter(&q);
    const double ms = g_cfQpf ? (double)(q.QuadPart - e.qpc) * 1000.0 / (double)g_cfQpf : -1;
    SFmt(out, cap, "%s %s of 0x%08lX (+0x%lX, %lu bytes) %.1f ms ago, thread %lu, phase 0x%lX, job %s, seq %ld",
              CfIsFree(e.tag) ? "FREED by" : "allocated by", e.tag < CF_NTAGS ? kCfTag[e.tag] : "?", e.addr, a - e.addr, e.size, ms,
              e.tid, e.phase, e.job ? "running" : "idle", e.seq);
}

static void CfHeapBad(u8 tag, const u8* p, DWORD hdr, DWORD next, LONG seq) {
    InterlockedIncrement(&ts3native_counters.cfHeapBad);
    if (InterlockedIncrement(&g_cfHeapLogs) > 16) return;
    char d[200];
    CfDescribe((DWORD)(uintptr_t)p, d, sizeof(d), seq);  // the event before this free
    Log("crash forensics: HEAP %s free of 0x%08lX on thread %lu: chunk not in use (hdr %08lX, next chunk word %08lX) = a double "
        "free or a foreign block; earlier: %s", kCfTag[tag], (DWORD)(uintptr_t)p, GetCurrentThreadId(), hdr, next, d);
}

// before an EA free of one of our blocks: ring entry, chunk size from the header, the in-use check
static void CfFree(u8 tag, const void* p) {
    if (!g_cfOn || !p) return;
    const u8* b = (const u8*)p;
    DWORD sz = 0, hdr = 0, next = 1;
    if (g_cfHeap) {
        __try {
            hdr = *(const DWORD*)(b - 0xC);
            const DWORD c = hdr & 0x7FFFFFF8;
            // plain chunk only (not direct-mapped bit 2, not fastbin/debug-tagged 4 / 0x80000000), sane size
            // user bytes = chunk - 0xC (request2size 0x4E4770: (n + 0x1B) & ~0xF)
            if (!(hdr & 0x80000006) && c >= 16 && c <= (1u << 26)) sz = c - 0xC, next = *(const DWORD*)(b - 0x10 + c + 4);
        } __except (EXCEPTION_EXECUTE_HANDLER) { sz = 0, next = 1; }
    }
    CfRec(tag, p, sz);
    if (sz && !(next & 1)) CfHeapBad(tag, b, hdr, next, g_cfSeq);
}
