// TestGcIdleGate (gcgate.cpp, docs/281 §8): the [GC] IdleGate skip decision. Included by selftest.cpp.
// [GC] SubTimers (docs/287 §2): each wrapper passes arguments and the return value through and books its time under
// [0] only for the thread inside the per-tick call (g_gcgTid), else under [1].
static int g_tgctArgs;
static int __cdecl TGct1(void* a) { g_tgctArgs += (int)(uintptr_t)a; return 7; }
static int __cdecl TGct0() { g_tgctArgs += 100; return 9; }
static int __cdecl TGct2(void* a, int b) { g_tgctArgs += (int)(uintptr_t)a * 1000 + b * 10000; return 11; }
static void TestGcSubTimers() {
    Ts3nCounters& k = ts3native_counters;
    const LARGE_INTEGER qpf = g_qpf;
    const DWORD tid = g_gcgTid;
    QueryPerformanceFrequency(&g_qpf);
    g_gctMarkSome = g_gctStopped = g_gctStartReclaim = TGct1, g_gctFinalize = g_gctClear = g_gctNotify = TGct0, g_gctReclaimAll = TGct2;
    k.gctMarkN = k.gctStopN = k.gctFinN = k.gctSweepN = k.gctRunN = 0, g_tgctArgs = 0;
    g_gcgTid = GetCurrentThreadId();
    assert(GctMark((void*)1) == 7 && GctStop((void*)2) == 7 && GctFin() == 9);
    g_gcgTid = 0;
    assert(GctStart((void*)3) == 7 && GctReclaim((void*)4, 5) == 11 && GctClear() == 9 && GctRun() == 9);
    assert(g_tgctArgs == 1 + 2 + 100 + 3 + 4000 + 50000 + 100 + 100);
    assert(k.gctMarkN == 1 && k.gctStopN == 1 && k.gctFinN == 1 && k.gctSweepN == 2 && k.gctRunN == 1);
    // the index: a 1 s span lands in [0] with the thread tag, in [1] without it or with another thread's tag
    LARGE_INTEGER t0;
    QueryPerformanceCounter(&t0);
    t0.QuadPart -= g_qpf.QuadPart;
    k.gctFinUs[0] = k.gctFinUs[1] = 0;
    g_gcgTid = GetCurrentThreadId(), GctAdd(k.gctFinUs, t0);
    assert(k.gctFinUs[0] >= 1000000 && k.gctFinUs[1] == 0);
    g_gcgTid = GetCurrentThreadId() + 4, GctAdd(k.gctFinUs, t0);
    g_gcgTid = 0, GctAdd(k.gctFinUs, t0);
    assert(k.gctFinUs[1] >= 2000000 && k.gctFinUs[0] < 2000000);
    memset(&k.gctReady, 0, 16 * sizeof(LONG));
    g_gcgTid = tid, g_qpf = qpf;
    printf("gc sub-timers: pass-through + index OK\n");
}

static void TestGcIdleGate() {
    TestGcSubTimers();
    DWORD t = 0;
    const DWORD MB = 1u << 20, heap = 352 * MB, cap = 64 * MB;
    // threshold: min(cap, heap * pct / 100), halved per backoff
    assert(GcgDecide(0, 0, 1, 1, 0, 0, heap, cap, 15, 0, &t) == 1 && t == (DWORD)((u64)heap * 15 / 100));
    assert(GcgDecide(0, 0, 1, 1, 0, 0, 1024 * MB, cap, 15, 0, &t) == 1 && t == cap);
    assert(GcgDecide(0, 0, 1, 1, 0, 0, 1024 * MB, cap, 15, 2, &t) == 1 && t == cap / 4);
    assert(GcgDecide(0, 0, 1, 1, 0, 0, 1024 * MB, cap, 15, 40, &t) == 0 && t == 0);  // backoff past 31: never skip
    // idle and below the threshold: skip; at or above (words * 4 bytes): run
    const DWORD w = (cap / 4) - 1;
    assert(GcgDecide(0, 0, 1, 1, 0, w, 1024 * MB, cap, 15, 0, &t) == 1);
    assert(GcgDecide(0, 0, 1, 1, 0, w + 1, 1024 * MB, cap, 15, 0, &t) == 0);
    assert(GcgDecide(0, 0, 1, 1, 0, 0xFFFFFFFF, 1024 * MB, cap, 15, 0, &t) == 0);  // no 32-bit wrap of words * 4
    // any work in progress or an unusual GC state: always run
    assert(GcgDecide(1, 0, 1, 1, 0, 0, heap, cap, 15, 0, &t) == 0);  // mark in progress
    assert(GcgDecide(0, 1, 1, 1, 0, 0, heap, cap, 15, 0, &t) == 0);  // finish / sweep pending
    assert(GcgDecide(0, 0, 0, 1, 0, 0, heap, cap, 15, 0, &t) == 0);  // GC_init not run yet
    assert(GcgDecide(0, 0, 1, 0, 0, 0, heap, cap, 15, 0, &t) == 0);  // not incremental
    assert(GcgDecide(0, 0, 1, 1, 1, 0, heap, cap, 15, 0, &t) == 0);  // GC_dont_gc
    printf("gc idle gate: decision OK\n");
}
