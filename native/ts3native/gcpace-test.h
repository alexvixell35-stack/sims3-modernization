// TestGcPace (gcpace.cpp, docs/624): [GC] CyclePace and PartialFreq on a fake collector. Included by selftest.cpp. Checks
// the pass-through for other stop functions, that the replica runs EA's 0xE49FB0 calls in EA's order when both keys are off
// in GpRun, the pacing gate (Boehm's scan / N rule and the collect-at-heapsize escape), the full / partial schedule, the
// forced-full rules (heap growth, mark state, run-time switch-off), the failure path and the counters.
static char g_gptTrace[256];
static int g_gptLen, g_gptStepsLeft, g_gptImmediate;
static DWORD g_gptMs, g_gptPending, g_gptDone, g_gptFull, g_gptGcNo, g_gptIncr = 1, g_gptHeap, g_gptLf, g_gptRoots,
    g_gptCollectAt, g_gptWords, g_gptBefore, g_gptAdj;
static GpVoidFn g_gptStartCb;
static void GptT(char c) { if (g_gptLen < 255) g_gptTrace[g_gptLen++] = c, g_gptTrace[g_gptLen] = 0; }
static int __cdecl GptOrig(void*) { GptT('O'); return 7; }
// EA's 0xE49F10 reduced: one step per frame; the frame the marking completes runs out of time, the next one finishes + sweeps
static int __cdecl GptFinishInProgress(void*) {
    GptT('F');
    if (g_gptStepsLeft > 0) {
        if (--g_gptStepsLeft) return 0;
        g_gptGcNo++, g_gptPending = 1, g_gptMs = 0;
        return 0;
    }
    if (g_gptPending) { GptT('n'); g_gptPending = 0, g_gptBefore += g_gptWords, g_gptWords = 0, g_gptAdj = 0; }
    return 1;
}
static int __cdecl GptStopped(void*) {
    GptT('M');
    if (g_gptImmediate) { g_gptMs = 0; return 1; }
    g_gptStepsLeft = 3, g_gptMs = 1;  // incremental: in progress
    return 0;
}
static void __cdecl GptPromote() { GptT('P'); }
static void __cdecl GptInvalidate() { GptT('I'); g_gptMs = 5; }
static void __cdecl GptClear() { GptT('C'); g_gptMs = 0; }
static void __cdecl GptFinish() { GptT('N'); g_gptBefore += g_gptWords, g_gptWords = 0, g_gptAdj = 0; }
static void __cdecl GptUnpromote() { GptT('U'); }
static DWORD __cdecl GptAdj() { return g_gptAdj; }

static void GptReset() {
    g_gptLen = 0, g_gptTrace[0] = 0;
}
static void GptAlloc(DWORD bytes) { g_gptWords += bytes / 4, g_gptAdj += bytes / 4; }

static void TestGcPace() {
    const GpAddrs saved = g_gp;
    GcPaceCounters& k = ts3native_gcpace;
    const GcPaceCounters savedK = k;
    g_gp = {&g_gptMs, &g_gptPending, &g_gptDone, &g_gptFull, &g_gptGcNo, &g_gptIncr, &g_gptHeap, &g_gptLf, &g_gptRoots,
            &g_gptCollectAt, &g_gptWords, &g_gptBefore, &g_gptStartCb, GptOrig, GptFinishInProgress, GptStopped,
            GptPromote, GptInvalidate, GptClear, GptFinish, GptUnpromote, GptAdj, 0xD717B0, 0x5BFB90};
    void* const frame = (void*)(uintptr_t)0xD717B0;
    g_gptHeap = 352u << 20, g_gptLf = 40u << 20, g_gptRoots = 1u << 20, g_gptCollectAt = 0xFFFFFFFF;
    memset(&k, 0, sizeof(k));
    g_gpLastLog = 0, g_gpLogMs = 0xFFFFFFFF, g_gpForceFull = 1, g_gpOpen = false, g_gpPartialsSinceFull = 0;

    // other stop functions and both keys off go to EA's function untouched
    GptReset();
    assert(GpInner((void*)(uintptr_t)0x5BFB90) == 7 && !strcmp(g_gptTrace, "O") && k.calls == 0);
    GptReset();
    assert(GpInner(frame) == 7 && !strcmp(g_gptTrace, "O"));

    // Boehm's threshold: (352 - 40 + 10 MB + 20000 + 1 MB) / 4 words, / 10
    const DWORD scanW = ((g_gptLf >> 2) - g_gptLf + g_gptHeap + 20000 + g_gptRoots) >> 2;
    assert(GpScanWords() == scanW && scanW == ((352u - 40u + 10u + 1u) * 1048576u + 20000u) / 4);
    DWORD thr;
    g_gptAdj = scanW / 10 - 1;
    assert(!GpDue(10, &thr) && thr == scanW / 10);
    g_gptAdj = scanW / 10;
    assert(GpDue(10, &thr));
    g_gptAdj = 0, g_gptCollectAt = g_gptHeap;
    assert(GpDue(10, &thr));  // GC_collect_at_heapsize escape
    g_gptCollectAt = 0xFFFFFFFF;

    // GpRun with pace 0, freq 0: EA's own sequence (immediate completion, then incremental start, then failure path)
    k.pace = 0, k.partialFreq = 0, g_gptImmediate = 1;
    GptReset();
    assert(GpRun(frame) == 1 && !strcmp(g_gptTrace, "FPICMN") && g_gptGcNo == 1 && g_gptPending == 1 && g_gptDone == 0xFFFFFFFF && g_gptFull == 1);
    g_gptPending = 0, g_gptImmediate = 0;
    GptReset();
    assert(GpRun(frame) == 0 && !strcmp(g_gptTrace, "FPICM") && g_gptDone == 0 && g_gptFull == 1);  // incremental: no unpromote
    g_gptStepsLeft = 0, g_gptMs = 0, g_gptIncr = 0;
    GptReset();
    assert(GpRun(frame) == 0 && !strcmp(g_gptTrace, "FPICMIU"));  // non-incremental failure: invalidate + unpromote
    g_gptIncr = 1, g_gptStepsLeft = 0, g_gptMs = 0;

    // CyclePace=10: after a cycle ends nothing starts until scan/10 bytes are allocated; steps still run every frame
    memset(&k, 0, sizeof(k));
    k.pace = 10, g_gptAdj = g_gptWords = 0, g_gpForceFull = 1, g_gpOpen = false;
    g_gpGcSeen = g_gptGcNo;
    GptReset();
    assert(GpInner(frame) == 0 && !strcmp(g_gptTrace, "F") && k.skipped == 1 && k.started == 0);
    GptAlloc(scanW / 10 * 4);
    GptReset();
    assert(GpInner(frame) == 0 && !strcmp(g_gptTrace, "FPICM") && k.started == 1 && k.full == 1 && g_gptStepsLeft == 3);
    for (int i = 0; i < 3; i++) { GptAlloc(1 << 20); GptReset(); GpInner(frame); assert(!strcmp(g_gptTrace, "F")); }
    assert(g_gptPending == 1 && k.skipped == 1);  // marking finished; finish + sweep are next
    GptReset();
    assert(GpInner(frame) == 0 && !strcmp(g_gptTrace, "Fn") && k.skipped == 2 && k.otherGcs == 0);
    assert(k.thresholdKB == (LONG)((scanW / 10) >> 8) && k.heapKB == 352 * 1024);
    // a GC_gc_no advance outside the per-frame step counts as other
    g_gptGcNo += 2;
    GptReset();
    GpInner(frame);
    assert(k.otherGcs == 2 && k.skipped == 3);
    GptAlloc(scanW / 10 * 4);
    GptReset();
    GpInner(frame);
    assert(k.started == 2 && k.lastAllocKB >= (LONG)((scanW / 10 * 4 + (3u << 20)) >> 10) && k.lastCycleUs >= 0);
    g_gptStepsLeft = 0, g_gptMs = 0, g_gptPending = 0;

    // PartialFreq=4 (no pacing): F p p p F p ...; partial = no promote / invalidate / clear, GC_is_full_gc = 0
    memset(&k, 0, sizeof(k));
    k.partialFreq = 4, g_gpForceFull = 1, g_gpOpen = false, g_gpPartialsSinceFull = 0, g_gptImmediate = 1;
    const char* want[6] = {"FPICMN", "FMN", "FMN", "FMN", "FPICMN", "FMN"};
    for (int i = 0; i < 6; i++) {
        g_gptPending = 0;
        GptReset();
        assert(GpInner(frame) == 1 && !strcmp(g_gptTrace, want[i]) && g_gptFull == (i % 4 == 0 ? 1u : 0u));
    }
    assert(k.full == 2 && k.partial == 4 && k.started == 6);
    // heap growth since the last full forces a full one
    g_gptPending = 0, g_gptHeap += 32u << 20;
    GptReset();
    assert(GpInner(frame) == 1 && !strcmp(g_gptTrace, "FPICMN"));
    // a mark state other than MS_NONE forces a full one
    g_gptPending = 0, g_gptMs = 5;
    GptReset();
    assert(GpInner(frame) == 1 && !strcmp(g_gptTrace, "FPICMN"));
    g_gptPending = 0, g_gptMs = 0;
    GptReset();
    assert(GpInner(frame) == 1 && !strcmp(g_gptTrace, "FMN"));
    // switched off at run time: EA's function; switched on again: the first cycle is full
    k.partialFreq = 0;
    GptReset();
    assert(GpInner(frame) == 7 && !strcmp(g_gptTrace, "O"));
    k.partialFreq = 4, g_gptPending = 0;
    GptReset();
    assert(GpInner(frame) == 1 && !strcmp(g_gptTrace, "FPICMN"));
    // both keys: pacing first, then the schedule
    k.pace = 10, g_gptPending = 0;
    GptReset();
    assert(GpInner(frame) == 0 && !strcmp(g_gptTrace, "F"));
    GptAlloc(GpScanWords() / 10 * 4);  // the heap grew above: a larger threshold
    GptReset();
    assert(GpInner(frame) == 1 && !strcmp(g_gptTrace, "FMN"));
    // the log line formats without faulting
    GpLog(k, GetTickCount());

    g_gptImmediate = 0;
    g_gp = saved, k = savedK;
    g_gpLastLog = 0, g_gpLogMs = 10000, g_gpForceFull = 1, g_gpOpen = false, g_gpPartialsSinceFull = 0, g_gpGcSeen = 0, g_gpHeapAtFull = 0;
    printf("gc pace: ok\n");
}
