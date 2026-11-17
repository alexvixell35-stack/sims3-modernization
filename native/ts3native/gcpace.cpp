// [GC] CyclePace and PartialFreq (docs/624): when the per-frame GC step starts a new cycle. Included by ts3native.cpp after
// memexp.cpp (PatchCalls, Matches). Both keys default 0: nothing is patched and the game's driver runs as before.
//
// EA's per-frame driver [verified: Ghidra decompile + bytes, Steam 1.67, docs/624 §1]: MonoScriptHost_Simulate calls
// GC_try_to_collect 0xE4A050 at 0xD819AA with EA's frame-budget stop function 0xD717B0 (its only caller, and the only push
// of 0xD717B0). 0xE4A050 runs the finalizer notify 0xE4B480 and calls GC_try_to_collect_inner 0xE49FB0 at 0xE4A089. That
// function first finishes the cycle in progress (0xE49F10: incremental steps 0xE49DA0 until the stop function fires, then
// the deferred GC_finish_collection 0xE49A50 and GC_reclaim_all 0xE50380), and when that returns 1 it ALWAYS starts a new
// full cycle: GC_promote_black_lists 0xE4A7C0, GC_invalidate_mark_state 0xE4C370, GC_clear_marks 0xE4C270, then
// GC_stopped_mark 0xE49980. GC_should_collect 0xE49950 is never asked (its only callers are 0xE493B7 and
// GC_collect_or_expand 0xE4A16D).
//
// We retarget only the call at 0xE4A089 (0xE49FB0 -> GpInner). GpInner passes every call straight to 0xE49FB0 unless the
// stop function is 0xD717B0, so GC.Collect (GC_gcollect 0xE4A0B0 -> its own call at 0xE4A0E8) and every forced path
// (GC_collect_or_expand 0xE4A120 -> 0xE4A17B / 0xE4A24E / 0xE4A320) never come here. For the per-frame call it runs EA's
// 0xE49FB0 body itself (byte-checked below) with two changes, each behind its own key:
//   CyclePace=N  after 0xE49F10 returned 1 (nothing in progress, the last cycle finished and swept), start a new cycle only
//                when Boehm's GC_should_collect would say so with free-space divisor N: GC_adj_words_allocd() 0xE49900 >=
//                scan words / N, or GC_heapsize >= GC_collect_at_heapsize. Scan words as min_words_allocd 0xE498B0:
//                (heapsize - large_free + large_free/4 + 20000 + root_size) / 4. N=10 equals what stock incremental Boehm
//                uses here (divisor 5, halved in incremental mode), N=5 the non-incremental rule.
//   PartialFreq=N between full cycles run N-1 partial ones: skip promote / invalidate / clear_marks and set
//                GC_is_full_gc = 0, so GC_initiate_gc 0xE4C2A0 takes MS_NONE -> MS_PUSH_RESCUERS and the mark restarts
//                from the roots plus the marked objects on pages GetWriteWatch saw dirty (Boehm's GC_maybe_gc partial
//                branch). Full when N-1 partials ran, the heap grew since the last full, or the mark state is not MS_NONE.

typedef int(__cdecl* GpStopFn)();
typedef int(__cdecl* GpStepFn)(void* stop);
typedef void(__cdecl* GpVoidFn)();
typedef DWORD(__cdecl* GpWordsFn)();
struct GpAddrs {  // game globals and functions; the host selftest points them at fakes
    DWORD *markState, *finishPending, *finishDone, *isFull, *gcNo, *incremental, *heapSize, *largeFree, *rootSize,
        *collectAt, *wordsAllocd, *wordsBefore;
    GpVoidFn* startCb;  // [0x11F30A0], called for never_stop only
    GpStepFn origInner, finishInProgress, stoppedMark;
    GpVoidFn promote, invalidate, clearMarks, finish, unpromote;
    GpWordsFn adjWords;
    DWORD frameStop, neverStop;
};
#ifdef TS3A_TABLE
static GpAddrs g_gp = {};  // EA 1.69: none of these is in ts3addr-ea169.h; ApplyGcPace SKIPs there
#else
static GpAddrs g_gp = {
    (DWORD*)(uintptr_t)TS3A(0x011F3078), (DWORD*)(uintptr_t)TS3A(0x011F2F94), (DWORD*)(uintptr_t)TS3A(0x011947E4),
    (DWORD*)(uintptr_t)TS3A(0x011F2FB8), (DWORD*)(uintptr_t)TS3A(0x011F2F90), (DWORD*)(uintptr_t)TS3A(0x011F2F9C),
    (DWORD*)(uintptr_t)TS3A(0x012225A0), (DWORD*)(uintptr_t)TS3A(0x012225B4), (DWORD*)(uintptr_t)TS3A(0x011F3088),
    (DWORD*)(uintptr_t)TS3A(0x01194818), (DWORD*)(uintptr_t)TS3A(0x012225C4), (DWORD*)(uintptr_t)TS3A(0x012225C0),
    (GpVoidFn*)(uintptr_t)TS3A(0x011F30A0),
    (GpStepFn)(uintptr_t)TS3A(0x00E49FB0), (GpStepFn)(uintptr_t)TS3A(0x00E49F10), (GpStepFn)(uintptr_t)TS3A(0x00E49980),
    (GpVoidFn)(uintptr_t)TS3A(0x00E4A7C0), (GpVoidFn)(uintptr_t)TS3A(0x00E4C370), (GpVoidFn)(uintptr_t)TS3A(0x00E4C270),
    (GpVoidFn)(uintptr_t)TS3A(0x00E49A50), (GpVoidFn)(uintptr_t)TS3A(0x00E4A450), (GpWordsFn)(uintptr_t)TS3A(0x00E49900),
    TS3A(0x00D717B0), TS3A(0x005BFB90)};
#endif

// ts3native_gcpace (docs/registry.md): not versioned, append only. Totals since start; *Win = the current log window.
struct GcPaceCounters {
    volatile LONG pace, partialFreq;  // ini; the probe may set either to 0 at run time (back to EA's driver)
    LONG ready;                       // 1 = the call at 0xE4A089 goes to GpInner
    LONG calls;                       // per-frame inner calls
    LONG started, full, partial;      // cycles started by the per-frame step
    LONG skipped;                     // calls where EA's driver would have started a cycle and pacing held it
    LONG otherGcs;                    // GC_gc_no advances outside the per-frame step (GC.Collect, forced collections)
    LONG lastCycleUs, maxCycleUs;     // per-frame GC time of the last closed cycle (start to next start)
    LONG lastAllocKB;                 // allocation during that cycle
    LONG heapKB, thresholdKB;         // at the last start check
};
extern "C" __declspec(dllexport) GcPaceCounters ts3native_gcpace = {};

static DWORD g_gpLogMs = 10000, g_gpLastLog, g_gpGcSeen, g_gpHeapAtFull;
static LONG g_gpPartialsSinceFull, g_gpForceFull = 1;  // the first cycle is full
static LONGLONG g_gpCycleUs;  // per-frame GC time since the current cycle started
static u64 g_gpAllocAtStart;
static bool g_gpOpen;         // a cycle was started by us and not closed yet
static struct { LONG started, full, partial, skipped, other, cycles; LONGLONG us; u64 allocB; } g_gpWin;

// Boehm's GC_should_collect with divisor n (0xE49950 / 0xE498B0, the incremental halving folded into n)
static DWORD GpScanWords() {
    const DWORD lf = *g_gp.largeFree;
    return ((lf >> 2) - lf + *g_gp.heapSize + 20000 + *g_gp.rootSize) >> 2;
}
static bool GpDue(LONG n, DWORD* thrWords) {
    const DWORD minW = GpScanWords() / (DWORD)n;
    *thrWords = minW;
    return g_gp.adjWords() >= minW || *g_gp.heapSize >= *g_gp.collectAt;
}

static u64 GpAllocBytes() { return ((u64)*g_gp.wordsBefore + *g_gp.wordsAllocd) * 4; }

static void GpLog(GcPaceCounters& k, DWORD now) {
    const double cyc = g_gpWin.cycles ? (double)g_gpWin.cycles : 1.0;
    Log("gcpace: started=%ld (full %ld, partial %ld) skipped=%ld other=%ld | window %.0fs: started=%ld full=%ld partial=%ld "
        "skipped=%ld other=%ld msPerCycle=%.2f maxMs=%.2f MBPerCycle=%.2f heap=%.1f threshold=%.1f",
        k.started, k.full, k.partial, k.skipped, k.otherGcs, (now - g_gpLastLog) / 1000.0, g_gpWin.started, g_gpWin.full,
        g_gpWin.partial, g_gpWin.skipped, g_gpWin.other, g_gpWin.us / 1000.0 / cyc, k.maxCycleUs / 1000.0,
        g_gpWin.allocB / 1048576.0 / cyc, k.heapKB / 1024.0, k.thresholdKB / 1024.0);
    memset(&g_gpWin, 0, sizeof(g_gpWin));
    g_gpLastLog = now ? now : 1;
}

// the start of a cycle closes the previous one (its steps, finish and sweep all ran in between)
static void GpCloseCycle(GcPaceCounters& k) {
    const u64 a = GpAllocBytes();
    if (g_gpOpen) {
        const LONG us = (LONG)(g_gpCycleUs > 0x7FFFFFFF ? 0x7FFFFFFF : g_gpCycleUs);
        k.lastCycleUs = us, k.lastAllocKB = (LONG)((a - g_gpAllocAtStart) >> 10);
        Max(&k.maxCycleUs, us);
        g_gpWin.cycles++, g_gpWin.us += g_gpCycleUs, g_gpWin.allocB += a - g_gpAllocAtStart;
    }
    g_gpOpen = true, g_gpCycleUs = 0, g_gpAllocAtStart = a;
}

// EA's 0xE49FB0 with the pacing gate and the partial branch (kGpInnerBody is checked at install)
static int GpRun(void* stop) {
    GcPaceCounters& k = ts3native_gcpace;
    k.calls++;
    if (*g_gp.gcNo != g_gpGcSeen) k.otherGcs += (LONG)(*g_gp.gcNo - g_gpGcSeen), g_gpWin.other += (LONG)(*g_gp.gcNo - g_gpGcSeen);
    if (!g_gp.finishInProgress(stop)) return 0;
    const LONG pace = k.pace, freq = k.partialFreq;
    if (pace > 0) {
        DWORD thr;
        const bool due = GpDue(pace, &thr);
        k.heapKB = (LONG)(*g_gp.heapSize >> 10), k.thresholdKB = (LONG)(thr >> 8);  // words -> KB
        if (!due) { k.skipped++, g_gpWin.skipped++; return 0; }
    }
    GpCloseCycle(k);
    const bool full = freq <= 1 || g_gpForceFull || g_gpPartialsSinceFull >= freq - 1 || *g_gp.markState != 0 ||
                      *g_gp.heapSize > g_gpHeapAtFull;
    if (full) {
        if ((DWORD)(uintptr_t)stop == g_gp.neverStop && *g_gp.startCb) (*g_gp.startCb)();
        g_gp.promote();
        g_gp.invalidate();
        g_gp.clearMarks();
        g_gpPartialsSinceFull = 0, g_gpForceFull = 0, g_gpHeapAtFull = *g_gp.heapSize;
        k.full++, g_gpWin.full++;
    } else {
        g_gpPartialsSinceFull++;
        k.partial++, g_gpWin.partial++;
    }
    k.started++, g_gpWin.started++;
    *g_gp.finishDone = 0;
    *g_gp.isFull = full ? 1 : 0;
    if (g_gp.stoppedMark(stop)) {
        ++*g_gp.gcNo;
        *g_gp.finishPending = 1;
        *g_gp.finishDone = 0xFFFFFFFF;
        g_gp.finish();
        return 1;
    }
    if (!*g_gp.incremental) {
        g_gp.invalidate();
        g_gp.unpromote();
    }
    return 0;
}

static int __cdecl GpInner(void* stop) {
    GcPaceCounters& k = ts3native_gcpace;
    if ((DWORD)(uintptr_t)stop != g_gp.frameStop) return g_gp.origInner(stop);  // not the per-frame step
    if (k.pace <= 0 && k.partialFreq <= 1) {
        g_gpForceFull = 1, g_gpOpen = false;  // switched off at run time: a later switch-on starts with a full cycle
        return g_gp.origInner(stop);
    }
    LARGE_INTEGER t0;
    QueryPerformanceCounter(&t0);
    const int r = GpRun(stop);
    g_gpCycleUs += UsSince(t0);
    g_gpGcSeen = *g_gp.gcNo;
    const DWORD now = GetTickCount();
    if (!g_gpLastLog) g_gpLastLog = now ? now : 1;
    else if (now - g_gpLastLog >= g_gpLogMs) GpLog(k, now);
    return r;
}
#ifdef TS3A_TABLE
// the EA-table build never installs GpInner (Steam layout only); keep it referenced so /W4 /WX (C4505) builds
extern "C" void* g_gpInnerKeep;
void* g_gpInnerKeep = (void*)&GpInner;
#endif

#ifndef TS3A_TABLE
// 0xE49FB0..0xE4A041, the body GpRun reproduces (rel32 calls included: the Steam layout only)
static const u8 kGpInnerBody[0x92] = {
    0x56, 0x8B, 0x74, 0x24, 0x08, 0x56, 0xE8, 0x55, 0xFF, 0xFF, 0xFF, 0x83, 0xC4, 0x04, 0x85, 0xC0, 0x74, 0x55, 0x81, 0xFE,
    0x90, 0xFB, 0x5B, 0x00, 0x75, 0x0B, 0xA1, 0xA0, 0x30, 0x1F, 0x01, 0x85, 0xC0, 0x74, 0x02, 0xFF, 0xD0, 0xE8, 0xE6, 0x07,
    0x00, 0x00, 0xE8, 0x91, 0x23, 0x00, 0x00, 0xE8, 0x8C, 0x22, 0x00, 0x00, 0x56, 0xC7, 0x05, 0xE4, 0x47, 0x19, 0x01, 0x00,
    0x00, 0x00, 0x00, 0xC7, 0x05, 0xB8, 0x2F, 0x1F, 0x01, 0x01, 0x00, 0x00, 0x00, 0xE8, 0x82, 0xF9, 0xFF, 0xFF, 0x83, 0xC4,
    0x04, 0x85, 0xC0, 0x75, 0x16, 0x39, 0x05, 0x9C, 0x2F, 0x1F, 0x01, 0x75, 0x0A, 0xE8, 0x5E, 0x23, 0x00, 0x00, 0xE8, 0x39,
    0x04, 0x00, 0x00, 0x33, 0xC0, 0x5E, 0xC3, 0x83, 0x05, 0x90, 0x2F, 0x1F, 0x01, 0x01, 0xC7, 0x05, 0x94, 0x2F, 0x1F, 0x01,
    0x01, 0x00, 0x00, 0x00, 0xC7, 0x05, 0xE4, 0x47, 0x19, 0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0xE8, 0x15, 0xFA, 0xFF, 0xFF, 0xB8,
    0x01, 0x00, 0x00, 0x00, 0x5E, 0xC3};
// GC_initiate_gc 0xE4C2A0 up to the MS_NONE -> MS_PUSH_RESCUERS store (the partial branch relies on it)
static const u8 kGpInitiate[0x26] = {0x56, 0x33, 0xF6, 0x39, 0x35, 0xF0, 0x40, 0x1F, 0x01, 0x74, 0x05, 0xE8, 0x00, 0x2D,
                                     0x00, 0x00, 0xA1, 0x78, 0x30, 0x1F, 0x01, 0x3B, 0xC6, 0x89, 0x35, 0x3C, 0x43, 0x23,
                                     0x01, 0x75, 0x12, 0x89, 0x35, 0x70, 0x30, 0x1F, 0x01, 0xC7};
#endif

static bool ApplyGcPace(const wchar_t* ini) {
    const int pace = Clamp(GetPrivateProfileIntW(L"GC", L"CyclePace", 0, ini), 0, 100);
    int freq = Clamp(GetPrivateProfileIntW(L"GC", L"PartialFreq", 0, ini), 0, 100);
    if (freq == 1) freq = 0;  // every cycle full = EA's driver
    if (!pace && !freq) return false;
#ifdef TS3A_TABLE
    Log("SKIP gc pace ([GC] CyclePace / PartialFreq): Steam 1.67 only (docs/624)");
    return false;
#else
    GcPaceCounters& k = ts3native_gcpace;
    g_gpLogMs = (DWORD)Clamp(GetPrivateProfileIntW(L"GC", L"SqrtLogSec", 10, ini), 1, 600) * 1000;
    if (!Matches((const void*)(uintptr_t)TS3A(0x00E49FB0), kGpInnerBody, sizeof(kGpInnerBody))) {
        Log("SKIP gc pace: GC_try_to_collect_inner 0xE49FB0 differs from Steam 1.67, nothing written");
        return false;
    }
    if (freq && !Matches((const void*)(uintptr_t)TS3A(0x00E4C2A0), kGpInitiate, sizeof(kGpInitiate))) {
        Log("SKIP gc pace PartialFreq: GC_initiate_gc 0xE4C2A0 differs; PartialFreq off");
        freq = 0;
        if (!pace) return false;
    }
    const CallSite cs = {TS3A(0x00E4A089), TS3A(0x00E49FB0), (void*)GpInner};
    if (!PatchCalls("GC_try_to_collect -> inner (gc pace)", &cs, 1)) return false;
    g_gpGcSeen = *g_gp.gcNo;
    k.pace = pace, k.partialFreq = freq, k.ready = 1;
    Log("gc pace: CyclePace=%d (start a cycle at allocation >= scan/%d) PartialFreq=%d (%d partial per full), log every %lu s",
        pace, pace, freq, freq ? freq - 1 : 0, g_gpLogMs / 1000);
    return true;
#endif
}
