// TestGcIdleGate (gcgate.cpp, docs/281 §8): the [GC] IdleGate skip decision. Included by selftest.cpp.
static void TestGcIdleGate() {
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
