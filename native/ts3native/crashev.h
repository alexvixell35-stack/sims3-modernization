// [CrashForensics] OurEvents=1 (docs/186 §8, default off): the our-events ring. Included by ts3native.cpp right after g_async,
// so every feature file can record (savewrite.cpp, decompress.cpp, rtsites.cpp, routegate.cpp, discourage.cpp, rcguard.cpp,
// routesnap.cpp, the route worker). The draw guards and the dump that read it are in crashfx.cpp.
//
// Every action of ours that can change an object's or a resource's lifetime or memory goes into a lock-free ring of the last
// 8192 events {seq, address, x, y, thread, tag, job running, QPC}: one InterlockedIncrement, one QPC read and eight stores
// (~30-60 ns), only on paths that run a few hundred times a second at most (destroys, deferrals, decodes, job start / end).
// Nothing is logged per event. The ring is read when a draw guard sees a bad pointer and in the crash dump.
// Not recorded: GC heap growth ([GC] patches three immediates in Mono's heap sizing; no code of ours runs when it grows).

enum CeTag : u8 {
    CE_NONE, CE_DESTROY_IN, CE_DESTROY_RUN, CE_DESTROY_OUT, CE_RC_HOLD, CE_RC_REL, CE_FP_QUEUE, CE_FP_APPLY, CE_DISC_DESTROY,
    CE_LOTRM_FLUSH, CE_DECOMP, CE_SAVE, CE_JOB_START, CE_JOB_END, CE_DRAW_BAD, CE_P5C_PARK, CE_P5C_REL,
    CE_XFORM_BAD, CE_NTAGS
};
// what a, x and y hold per tag (docs/186 §8.2)
static const char* const kCeTag[CE_NTAGS] = {
    "?",
    "destroy-in",      // a object (0 = not found), x idLo, y idHi: DestroyById 0x939420 entered our gate
    "destroy-run",     // a object, x idLo, y job wait us: the gate let the game's destroy run
    "destroy-out",     // a object, x idLo: DestroyById returned (the object's memory may be gone from here on)
    "rc-hold",         // a lock, x caller frame slot, y releases owed: RefcountGuard kept a +0x918 query release
    "rc-rel",          // a lock, x releases, y why (-1 caller returned, 0 before a wait, 1 stale frame)
    "fp-queue",        // a object, x idLo, y kind (0 discourage remove, 0x10 add, 1 design U, 3/4 phase 2, 5 private remove)
    "fp-apply",        // a object at defer time, x idLo, y bit 0 applied (object still there), bit 1 a job ran
    "disc-destroy",    // a world, x idLo, y records applied before the destroy (Discourage DestroyApply)
    "lotrm-flush",     // a Q, x idLo, y idHi: LotRemoveGuard flushed a pending remove before a lot-0 remove
    "decompress",      // a destination buffer, x bytes written (0 = failed), y capacity: RefpackHook output
    "save",            // a 0 backup job start, 1 backup job end, 2 flush start, 3 flush end, 4 backup move; x ok / files
    "job-start",       // a route handle, x kind, y +0x918 lock: the worker holds +0x918 from here
    "job-end",         // a route handle, x job us, y +0x918 lock: released
    "draw-bad",        // a bad pointer, x site, y parent object: a draw guard saw it (crashfx.cpp)
    "p5c-park",        // a object, x idLo, y idHi: P5cPark kept one reference of a destroyed object (docs/67 §15)
    "p5c-rel",         // a object, x park age us, y count before: P5cFlush released it (count 1 = freed now)
    "xform-bad",       // a parent object, x transform component, y caller: the 0x958560 guard saw a bad parent (crashfx.cpp)
};

struct CeEnt {
    volatile LONG seq;  // written last: 0 or a stale value = the slot is being rewritten
    DWORD a, x, y;
    DWORD tid;
    u8 tag, job, pad[2];
    LONGLONG qpc;
};
static_assert(sizeof(CeEnt) == 32, "our-events ring entry");
static const DWORD kCeRing = 8192;  // power of two
static CeEnt g_ceRing[kCeRing];
static volatile LONG g_ceSeq;
static LONG g_ceOn;  // [CrashForensics] Enabled=1 OurEvents=1

static void CeRec(u8 tag, DWORD a, DWORD x = 0, DWORD y = 0) {
    if (!g_ceOn) return;
    const LONG s = InterlockedIncrement(&g_ceSeq);
    CeEnt& e = g_ceRing[(DWORD)(s - 1) & (kCeRing - 1)];
    e.seq = 0;
    e.a = a, e.x = x, e.y = y, e.tid = GetCurrentThreadId();
    e.tag = tag, e.job = g_async.state == A_RUNNING;
    LARGE_INTEGER q;
    QueryPerformanceCounter(&q);
    e.qpc = q.QuadPart;
    _WriteBarrier();
    e.seq = s;
    ts3native_counters.ceRecs = s;  // plain store: the newest sequence number
}

// bytes an entry's address covers: a decode its output, an object 0x400 (shortcut: object sizes unknown; enough for the
// sub-object pointers a draw reads at small offsets), anything else 16
static DWORD CeSpan(const CeEnt& e) {
    if (e.tag == CE_DECOMP) return e.x > 16 ? e.x : 16;
    if (e.tag == CE_DESTROY_IN || e.tag == CE_DESTROY_RUN || e.tag == CE_DESTROY_OUT || e.tag == CE_FP_QUEUE || e.tag == CE_FP_APPLY ||
        e.tag == CE_P5C_PARK || e.tag == CE_P5C_REL) return 0x400;
    return 16;
}
// newest entry whose span holds `a`; false = none in the ring
static bool CeFind(DWORD a, CeEnt* out) {
    const LONG top = g_ceSeq;
    for (LONG s = top; s > 0 && top - s < (LONG)kCeRing; s--) {
        const CeEnt& e = g_ceRing[(DWORD)(s - 1) & (kCeRing - 1)];
        if (e.seq != s || !e.a || e.tag == CE_DRAW_BAD || e.tag == CE_XFORM_BAD) continue;
        if (a - e.a < CeSpan(e)) { *out = e; return true; }
    }
    return false;
}
static double CeAgeMs(LONGLONG qpc) {
    LARGE_INTEGER q, f;
    QueryPerformanceCounter(&q);
    QueryPerformanceFrequency(&f);
    return (double)(q.QuadPart - qpc) * 1000.0 / (double)f.QuadPart;
}
// one line: "<tag> a 0x... (+off) x y, N ms ago, thread T, job J, seq S" or "not in our-events ring"
static void CeDescribe(DWORD a, char* out, size_t cap) {
    CeEnt e;
    if (!CeFind(a, &e)) { SFmt(out, cap, "not in our-events ring (last %lu events)", kCeRing); return; }
    SFmt(out, cap, "%s a 0x%08lX (+0x%lX) x 0x%lX y 0x%lX, %.1f ms ago, thread %lu, job %s, seq %ld",
              e.tag < CE_NTAGS ? kCeTag[e.tag] : "?", e.a, a - e.a, e.x, e.y, CeAgeMs(e.qpc), e.tid, e.job ? "running" : "idle", e.seq);
}
