// [GC] IdleGate (docs/281 §8, G1; default 0). Included by ts3native.cpp after memexp.cpp (g_mx, MxStep) and savewrite.cpp
// (CallSite, PatchCalls).
//
// EA's MonoScriptHost_Simulate 0xD81840 calls GC_try_to_collect 0xE4A050 once per sim iteration (call 0xD819AA). With
// nothing in progress that call starts the next cycle at once, so the incremental GC never idles (~120 ms/s on BP). The
// gate retargets the call and skips it while (a) no mark runs, (b) no finish or sweep is pending and (c) less than the
// threshold has been allocated since the last collection. A skipped call still runs what 0xE4A050 does before any GC
// work (the optional start hook and GC_notify_or_invoke_finalizers 0xE4B480), so finalizer timing per tick is vanilla.
//
// Fields [verified, game-copy TS3W.exe Steam 1.67 disassembly, docs/281 §8]:
//   mark state       [0x11F3078]  GC_collection_in_progress 0xE4C060 = `xor eax,eax; cmp [0x11F3078],eax; setne al`
//   finish pending   [0x11F2F94]  set 1 at 0xE4A022 when a mark completes (with GC_gc_no++), cleared at 0xE49F91 when
//                                 the lazy sweep 0xE50380 is done; 0xE49F10 returns 1 ("start the next cycle") only then
//   GC_words_allocd  [0x12225C4]  zeroed in GC_finish_collection at 0xE49AD9 (after adding it to [0x12225C0])
//   GC_heapsize [0x12225A0], GC_gc_no [0x11F2F90], GC_incremental [0x11F2F9C], GC_dont_gc [0x11F30A8], GC_is_initialized
//   [0x11F30D0]; start hook flag [0x11F3094] / pointer [0x11F309C].
//   GC_collect_a_little_inner 0xE49DA0 has one caller (0xE49F49): allocation never drives the incremental GC itself.
// Forced collection: in incremental mode GC_collect_or_expand 0xE4A120 (allocation out of free blocks, words allocated
// > 0) calls 0xD70800 = `jmp 0xE4A320`, a full collection with GC_never_stop_func 0x5BFB90. The gate retargets that jmp
// to GcgForced to count it, and backs the threshold off (halves it) when a forced one follows a skipped tick.
// Hold (bugfix 11-17, smoke gcgSkipped=0 in 18368 calls): 0xE49FB0 calls 0xE49F10 (0xE49FB6), which clears the pending
// flag when the lazy sweep is done and returns 1, and then starts the next mark in the SAME call. So between ticks mark
// or pending is always set and the per-tick check alone never skips. GcgHold wraps that call and returns 0 ("cycle not
// done") when the sweep just finished inside our per-tick call and less than T was allocated; the collector is then
// idle (mark 0, pending 0) and GcgStep skips the next ticks until T is reached.

// run (0) or skip (1) the per-tick call; *thresh = threshold in bytes. Pure: the selftest drives it with plain values.
static int GcgDecide(DWORD mark, DWORD pending, DWORD inited, DWORD incr, DWORD dontGc, DWORD wordsAllocd, DWORD heap, DWORD capBytes,
                     int pct, int backoff, DWORD* thresh) {
    const u64 byHeap = (u64)heap * (DWORD)pct / 100;
    DWORD t = byHeap < capBytes ? (DWORD)byHeap : capBytes;
    t = backoff >= 32 ? 0 : t >> backoff;
    *thresh = t;
    if (mark || pending || !inited || !incr || dontGc) return 0;
    return (u64)wordsAllocd * 4 < t ? 1 : 0;
}

typedef void(__cdecl* GcgVoidFn)();
static MxStepFn g_gcgNext;      // what 0xD819AA called before us (0xE4A050, or MxStep with [GC] SqrtHeapLimit)
static GcgVoidFn g_gcgForcedOrig;  // 0xE4A320
static MxStepFn g_gcgHoldOrig;     // 0xE49F10
static DWORD g_gcgTid;             // thread inside a per-tick call that GcgStep passed on (0 = none)
static DWORD g_gcgCap = 64u << 20, g_gcgLastNo;
static int g_gcgPct = 15, g_gcgBackoff, g_gcgLastSkipped, g_gcgSeen;
static u64 g_gcgUs, g_gcgForcedUs;
static const DWORD* const g_gcgMark = (const DWORD*)(uintptr_t)TS3A(0x011F3078);
static const DWORD* const g_gcgPending = (const DWORD*)(uintptr_t)TS3A(0x011F2F94);
// G2 (docs/281 §8.7): GC_n_attempts [0x11F2FB4], aborted stop-the-world finish attempts of the current mark: +1 at
// 0xE49E6D when GC_stopped_mark 0xE49980 aborts, reset at 0xE49ACD (GC_finish_collection) and 0xE49E16; > 40
// ([0x1194820]) forces the finish with GC_never_stop_func.
static const LONG* const g_gcgAttempts = (const LONG*)(uintptr_t)TS3A(0x011F2FB4);
static const DWORD* const g_gcgHookOn = (const DWORD*)(uintptr_t)TS3A(0x011F3094);
static const GcgVoidFn* const g_gcgHook = (const GcgVoidFn*)(uintptr_t)TS3A(0x011F309C);
static const GcgVoidFn g_gcgNotify = (GcgVoidFn)(uintptr_t)TS3A(0x00E4B480);

static int __cdecl GcgStep(void* stop) {
    Ts3nCounters& k = ts3native_counters;
    k.gcgCalls++;
    const DWORD no0 = *g_mx.gcNo, heapKB = *g_mx.heapSize >> 10;
    if ((LONG)heapKB > k.gcgHeapMaxKB) k.gcgHeapMaxKB = (LONG)heapKB;
    if (g_gcgSeen && no0 != g_gcgLastNo) k.gcgOutside += (LONG)(no0 - g_gcgLastNo);  // forced, explicit GC.Collect, other threads
    g_gcgSeen = 1;
    DWORD t;
    const int skip = GcgDecide(*g_gcgMark, *g_gcgPending, *(const DWORD*)(uintptr_t)GC_IS_INITIALIZED, *g_mx.incremental, *g_mx.dontGc,
                               *g_mx.wordsAllocd, *g_mx.heapSize, g_gcgCap, g_gcgPct, g_gcgBackoff, &t);
    k.gcgThreshKB = (LONG)(t >> 10);
    if (k.gcgMode == 1 && skip) {
        k.gcgSkipped++, g_gcgLastSkipped = 1;
        if (*g_gcgHookOn) (*g_gcgHook)();  // what 0xE4A050 runs before any GC work
        g_gcgNotify();
        g_gcgLastNo = *g_mx.gcNo;
        return 0;  // caller ignores it (0xD819AF); vanilla returns 0 for an unfinished cycle too
    }
    g_gcgLastSkipped = 0;
    LARGE_INTEGER t0;
    QueryPerformanceCounter(&t0);
    g_gcgTid = GetCurrentThreadId();
    const LONG a0 = *g_gcgAttempts;
    const int r = g_gcgNext(stop);
    g_gcgTid = 0;
    const LONG a1 = *g_gcgAttempts;
    // gcgSpare_1 = aborted finish attempts in passed calls (one call tries at most once; a reset inside the call leaves a1)
    k.gcgSpare[1] += a1 >= a0 ? a1 - a0 : a1;
    const LONG us = UsSince(t0);
    k.gcgRan++;
    k.gcgCycles += (LONG)(*g_mx.gcNo - no0);
    g_gcgUs += (u64)us, k.gcgMs = (LONG)(g_gcgUs / 1000);
    if (us > k.gcgMaxUs) k.gcgMaxUs = us;
    g_gcgLastNo = *g_mx.gcNo;
    return r;
}

// 0xE49FB6: the sweep-done check of 0xE49FB0. Holds the next cycle only inside the per-tick call (thread id), never for
// a collection with GC_never_stop_func 0x5BFB90 (forced / full: 0xE4A0B0, 0xE4A320).
static int __cdecl GcgHold(void* stop) {
    const int r = g_gcgHoldOrig(stop);
    Ts3nCounters& k = ts3native_counters;
    if (!r || k.gcgMode != 1 || g_gcgTid != GetCurrentThreadId() || stop == (void*)(uintptr_t)TS3A(0x005BFB90)) return r;
    DWORD t;
    if (!GcgDecide(*g_gcgMark, *g_gcgPending, *(const DWORD*)(uintptr_t)GC_IS_INITIALIZED, *g_mx.incremental, *g_mx.dontGc,
                   *g_mx.wordsAllocd, *g_mx.heapSize, g_gcgCap, g_gcgPct, g_gcgBackoff, &t))
        return r;
    k.gcgSkipped++, k.gcgSpare[0]++;  // gcgSpare_0 = cycles held at sweep end (each also counts as a skip)
    return 0;                         // 0xE49FB0 returns 0, as for an unfinished cycle
}

static void __cdecl GcgForced() {
    Ts3nCounters& k = ts3native_counters;
    LARGE_INTEGER t0;
    QueryPerformanceCounter(&t0);
    g_gcgForcedOrig();
    g_gcgForcedUs += (u64)UsSince(t0), k.gcgForcedMs = (LONG)(g_gcgForcedUs / 1000);
    k.gcgForced++;
    if (k.gcgMode == 1 && g_gcgLastSkipped && g_gcgBackoff < 8) {  // the gate let the heap run out: halve the threshold
        k.gcgBackoff = ++g_gcgBackoff;
        Log("gc idle gate: forced collection after a skipped tick (heap %lu MB), threshold now %lu KB (backoff %d)", *g_mx.heapSize >> 20,
            (g_gcgCap >> 10) >> g_gcgBackoff, g_gcgBackoff);
    }
}

// EA 1.69: the byte checks below embed Steam 1.67 data addresses, so that build logs SKIP until the gate is ported.
static const u8 kGcgInProgress[12] = {0x33, 0xC0, 0x39, 0x05, 0x78, 0x30, 0x1F, 0x01, 0x0F, 0x95, 0xC0, 0xC3};  // 0xE4C060
static const u8 kGcgPendingSet[10] = {0xC7, 0x05, 0x94, 0x2F, 0x1F, 0x01, 0x01, 0x00, 0x00, 0x00};              // 0xE4A022
static const u8 kGcgAllocdZero[6] = {0x89, 0x35, 0xC4, 0x25, 0x22, 0x01};                                       // 0xE49AD9
static const u8 kGcgAttemptInc[6] = {0x89, 0x0D, 0xB4, 0x2F, 0x1F, 0x01};                                       // 0xE49E6D

// [GC] IdleGate=1 installs the gate with gcgMode 1 (on); 2 installs it with gcgMode 0 (counters only, the per-tick call
// always runs: the off arm). gcgMode is the run-time switch (--ab gcidlegate).
static bool ApplyGcIdleGate(const wchar_t* ini) {
    Ts3nCounters& k = ts3native_counters;
    const int mode = GetPrivateProfileIntW(L"GC", L"IdleGate", 0, ini);
    if (!mode) { Log("GC idle gate off (IdleGate=0)"); return false; }
#ifdef TS3A_TABLE
    if (ts3a_noaddr) { Log("SKIP GC idle gate ([GC] IdleGate): no EA 1.69 address, docs/232 §8"); return false; }
#endif
    g_gcgCap = (DWORD)Clamp(GetPrivateProfileIntW(L"GC", L"IdleGateMB", 64, ini), 1, 512) << 20;
    g_gcgPct = Clamp(GetPrivateProfileIntW(L"GC", L"IdleGateHeapPct", 15, ini), 1, 50);
    if (!Matches((void*)(uintptr_t)TS3A(0x00E4C060), kGcgInProgress, sizeof(kGcgInProgress)) ||
        !Matches((void*)(uintptr_t)TS3A(0x00E4A022), kGcgPendingSet, sizeof(kGcgPendingSet)) ||
        !Matches((void*)(uintptr_t)TS3A(0x00E49AD9), kGcgAllocdZero, sizeof(kGcgAllocdZero)) ||
        !Matches((void*)(uintptr_t)TS3A(0x00E49E6D), kGcgAttemptInc, sizeof(kGcgAttemptInc))) {
        Log("SKIP GC idle gate: Boehm state code differs (0xE4C060 / 0xE4A022 / 0xE49AD9 / 0xE49E6D)");
        return false;
    }
    // the forced-collection thunk 0xD70800: jmp 0xE4A320
    u8* th = (u8*)(uintptr_t)TS3A(0x00D70800);
    if (!Readable(th, 5) || th[0] != 0xE9 || (DWORD)(TS3A(0x00D70800) + 5 + *(const LONG*)(th + 1)) != TS3A(0x00E4A320)) {
        Log("SKIP GC idle gate: 0xD70800 is not jmp 0xE4A320");
        return false;
    }
    // the call site: still EA's target, or MxStep when [GC] SqrtHeapLimit retargeted it first (we chain)
    const u8* cs = (const u8*)(uintptr_t)TS3A(0x00D819AA);
    const DWORD cur = Readable(cs, 5) && cs[0] == 0xE8 ? (DWORD)(TS3A(0x00D819AA) + 5 + *(const LONG*)(cs + 1)) : 0;
    if (cur != TS3A(0x00E4A050) && cur != (DWORD)(uintptr_t)MxStep) { Log("SKIP GC idle gate: call 0xD819AA goes to 0x%08lX", cur); return false; }
    g_gcgNext = (MxStepFn)(uintptr_t)cur;
    g_gcgForcedOrig = (GcgVoidFn)(uintptr_t)TS3A(0x00E4A320);
    const CallSite site = {TS3A(0x00D819AA), cur, (void*)GcgStep};
    const CallSite hold = {TS3A(0x00E49FB6), TS3A(0x00E49F10), (void*)GcgHold};
    g_gcgHoldOrig = (MxStepFn)(uintptr_t)TS3A(0x00E49F10);
    if (!PatchCalls("GC idle gate cycle hold (0xE49FB6)", &hold, 1)) return false;
    if (!PatchCalls("per-tick GC_try_to_collect idle gate", &site, 1)) return false;
    const LONG rel = (LONG)((uintptr_t)GcgForced - (TS3A(0x00D70800) + 5));
    if (!Write(th + 1, &rel, 4)) Log("note: GC idle gate without the forced-collection count (0xD70800 not writable)");
    else k.gcgReady |= 2;
    k.gcgReady |= 1;
    k.gcgMode = mode == 1 ? 1 : 0;
    Log("GC idle gate: IdleGate=%d (gcgMode %ld) IdleGateMB=%lu IdleGateHeapPct=%d%s ready=0x%lX", mode, k.gcgMode, g_gcgCap >> 20, g_gcgPct,
        cur == TS3A(0x00E4A050) ? "" : " (chained after SqrtHeapLimit timing)", k.gcgReady);
    return true;
}
