// --- [RouteJobPhases] (routephases.cpp, docs/150 §7): layout, worker-only stubs, owner interposition, self time without double
// counting, unwound frames. Included by selftest.cpp.
static void TestRouteJobPhases() {
    static_assert(offsetof(Ts3nCounters, rjpReady) == 0x36AC && offsetof(Ts3nCounters, rjpJobs) == 0x36BC &&
                  offsetof(Ts3nCounters, rjpN) == 0x36D4 && offsetof(Ts3nCounters, rjpUs) == 0x3788 &&
                  offsetof(Ts3nCounters, rjpMaxUs) == 0x383C && sizeof(Ts3nCounters) == 5909 * 4,
                  "counters v87 layout after v86 (docs/registry.md)");
    static_assert(offsetof(Ts3nCounters, raAllocN) == 0x39C8 && offsetof(Ts3nCounters, raJobHist) == 0x39D4 &&
                  offsetof(Ts3nCounters, raSizeLog2) == 0x39F4 && offsetof(Ts3nCounters, raInsCap) == 0x3A34 &&
                  offsetof(Ts3nCounters, raInsUs) == 0x3A74 && offsetof(Ts3nCounters, raSite) == 0x3AB4 &&
                  offsetof(Ts3nCounters, raParent) == 0x3BB4 && offsetof(Ts3nCounters, raN) == 0x3CB4 &&
                  offsetof(Ts3nCounters, raUs) == 0x3DB4 && offsetof(Ts3nCounters, raBytes) == 0x3EB4,
                  "counters v92 layout after v91 (docs/registry.md)");
    memset(&ts3native_counters.raAllocN, 0, sizeof(Ts3nCounters) - offsetof(Ts3nCounters, raAllocN));
    static_assert(sizeof(kRjpStubs) / sizeof(kRjpStubs[0]) == RJS_N && sizeof(kRjpSites) / sizeof(kRjpSites[0]) == RJS_N, "a stub per site");
    Ts3nCounters& k = ts3native_counters;
    memset(&k.rjpReady, 0, offsetof(Ts3nCounters, rjpMaxUs) + sizeof(k.rjpMaxUs) - offsetof(Ts3nCounters, rjpReady));
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
    u8* code = (u8*)VirtualAlloc(NULL, 4096, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
    assert(code);
    // Both callee-cleanup like the game's sites (12 of 15 end in ret N): a nested ret-N leave popped the parent's frame before the
    // docs/150 §7 fix (game exit ~10 s into every phases job), so this pair crashes the selftest on the old RjpPop.
    // inner: Lock_Acquire's prologue (push ebx; push ebp; push esi; mov esi,ecx), pops, mov eax,7; ret 4 (as Lock_Acquire)
    static const u8 kIn[] = {0x53, 0x55, 0x56, 0x8B, 0xF1, 0x5E, 0x5D, 0x5B, 0xB8, 0x07, 0x00, 0x00, 0x00, 0xC2, 0x04, 0x00};
    memcpy(code, kIn, sizeof(kIn));
    // outer: PathNodes_Insert's prologue (sub esp,8; push ebx; push ebp), push 0, call inner, pop ebp; pop ebx; add esp,8;
    // ret 8 (as PathNodes_Insert)
    u8* o = code + 64;
    static const u8 kOut[] = {0x83, 0xEC, 0x08, 0x53, 0x55, 0x6A, 0x00, 0xE8, 0, 0, 0, 0, 0x5D, 0x5B, 0x83, 0xC4, 0x08, 0xC2, 0x08, 0x00};
    memcpy(o, kOut, sizeof(kOut));
    *(LONG*)(o + 8) = (LONG)(code - (o + 12));
    // owned: mov eax,9; ret (an owner's trampoline); the site's bytes are not World A*'s
    u8* w = code + 128;
    static const u8 kOwned[] = {0xB8, 0x09, 0x00, 0x00, 0x00, 0xC3};
    memcpy(w, kOwned, sizeof(kOwned));
    void* owner = w;
    assert(RjpInstallAt(RJS_LOCK, (DWORD)(uintptr_t)code, NULL));
    assert(RjpInstallAt(RJS_INSERT, (DWORD)(uintptr_t)o, NULL));
    assert(RjpInstallAt(RJS_WORLD, (DWORD)(uintptr_t)(code + 200), &owner) && owner == (void*)kRjpStubs[RJS_WORLD]);
    assert(!RjpInstallAt(RJS_CELL, (DWORD)(uintptr_t)(code + 200), NULL));  // other bytes, no owner: skipped
    typedef int(__cdecl * Fn)();
    typedef int(__stdcall * Fn2)(int, int);
    const Fn owned = (Fn)owner;
    const Fn2 outer2 = (Fn2)o;
    const auto outer = [outer2]() { return outer2(1, 2); };
    // no job: straight through, nothing counted
    assert(outer() == 7 && owned() == 9 && k.rjpN[0][RJP_INSERT] == 0 && k.rjpN[0][RJP_LOCK] == 0);
    // switch off (rjpReady 0): a job does not arm the stubs
    RjpJobBegin(0);
    assert(!g_rjpTid && outer() == 7 && k.rjpN[0][RJP_INSERT] == 0);
    RjpJobEnd(5);
    assert(k.rjpJobs[0] == 0);
    k.rjpReady = 1;
    // a job of kind 1: stubs, a nested pair of manual frames (outer 0 ms self, inner 20 ms), a host frame
    const LONGLONG t0 = RjpNow();
    RjpJobBegin(1);
    assert(g_rjpTid == GetCurrentThreadId());
    assert(outer() == 7 && owned() == 9 && t_rjp.n == 0 && k.rjpDropped == 0);  // the ret-4 leave closes its own frame
    DWORD stk[4] = {};
    assert(RjpPush(RJP_PLAN, &stk[3], false) && RjpPush(RJP_CELL, &stk[1], false));
    Sleep(20);
    RjpPop(&stk[1]);
    RjpPop(&stk[3]);
    assert(RjpHostBegin(&stk[2]));
    Sleep(2);
    RjpHostEnd(&stk[2]);
    // unwound frame: a frame left at a deeper slot is dropped when a shallower one is pushed
    assert(RjpPush(RJP_POST, &stk[0], false) && RjpPush(RJP_WATER, &stk[2], false) && k.rjpDropped == 1 && t_rjp.n == 1);
    RjpPop(&stk[2]);
    const LONG us = RjpUs(RjpNow() - t0);
    RjpJobEnd(us);
    assert(!g_rjpTid && k.rjpJobs[1] == 1 && k.rjpJobUs[1] == us);
    assert(k.rjpN[1][RJP_INSERT] == 1 && k.rjpN[1][RJP_LOCK] == 1 && k.rjpN[1][RJP_WORLD] == 1 && k.rjpN[1][RJP_HOST] == 1 &&
           k.rjpN[1][RJP_PLAN] == 1 && k.rjpN[1][RJP_CELL] == 1 && k.rjpN[1][RJP_WATER] == 1 && k.rjpN[1][RJP_POST] == 0 &&
           k.rjpN[1][RJP_OTHER] == 1);
    assert(k.rjpUs[1][RJP_CELL] >= 15000 && k.rjpUs[1][RJP_PLAN] < k.rjpUs[1][RJP_CELL] && k.rjpUs[1][RJP_HOST] >= 1000);
    LONG sum = 0;
    for (int p = 0; p < RJP_N; p++) sum += k.rjpUs[1][p];
    assert(sum >= us - RJP_N && sum <= us);  // phases + other = the job; the nested 20 ms counted once (rounding per frame)
    for (int p = 0; p < RJP_N; p++) assert(k.rjpN[0][p] == 0 && k.rjpN[2][p] == 0);
    // after the job: straight through again
    assert(outer() == 7 && k.rjpN[1][RJP_INSERT] == 1);
    // v92 allocator sites: EA_AllocTagged's prologue (mov eax,[esp+0xC]; mov ecx,[esp+4]), ret; two call sites, sizes 64 and 200
    u8* al = code + 256;
    static const u8 kAl[] = {0x8B, 0x44, 0x24, 0x0C, 0x8B, 0x4C, 0x24, 0x04, 0x33, 0xC0, 0xC3};
    memcpy(al, kAl, sizeof(kAl));
    assert(RjpInstallAt(RJS_ALLOC, (DWORD)(uintptr_t)al, NULL));
    typedef void*(__cdecl * AllocFn)(DWORD size, DWORD flags, DWORD x);
    const AllocFn alloc = (AllocFn)al;
    alloc(64, 0, 0);  // no job: not recorded
    assert(k.raAllocN == 0 && k.raN[0] == 0);
    RjpJobBegin(2);
    assert(RjpPush(RJP_LOTLEG, &stk[3], false));
    for (int i = 0; i < 3; i++) alloc(64, 0, 0);  // one site, 3 calls
    alloc(200, 0, 0);                             // a second site
    RjpPop(&stk[3]);
    RjpJobEnd(100);
    RjpJobBegin(2);
    RjpJobEnd(10);  // a job without allocator calls
    int rows = 0, n3 = -1, n1 = -1;
    for (int r = 0; r < 64; r++)
        if (k.raN[r]) {
            rows++;
            assert(k.raParent[r] == 0);  // the open phase frame is a manual one (no return address)
            if (k.raN[r] == 3) n3 = r; else if (k.raN[r] == 1) n1 = r;
        }
    assert(rows == 2 && n3 >= 0 && n1 >= 0 && k.raBytes[n3] == 192 && k.raBytes[n1] == 200 && k.raSite[n3] != k.raSite[n1]);
    assert(k.raAllocN == 4 && k.raFreeN == 0 && k.raFull == 0 && k.raJobHist[0] == 2 && k.raJobHist[1] == 1);
    assert(k.raSizeLog2[4] == 3 && k.raSizeLog2[5] == 1);  // 64 = [64,128) bucket 4; 200 = [128,256) bucket 5
    assert(k.rjpN[2][RJP_ALLOC] == 4 && k.rjpN[2][RJP_LOTLEG] == 1);
    VirtualFree(code, 0, MEM_RELEASE);
    memset(&k.raAllocN, 0, sizeof(Ts3nCounters) - offsetof(Ts3nCounters, raAllocN));
    memset(&k.rjpReady, 0, offsetof(Ts3nCounters, rjpMaxUs) + sizeof(k.rjpMaxUs) - offsetof(Ts3nCounters, rjpReady));
    printf("route job phases: layout, worker-only stubs, owner interposition, self time and drops ok; v92 allocator sites (rows, bytes, size and per-job histograms) ok\n");
}
