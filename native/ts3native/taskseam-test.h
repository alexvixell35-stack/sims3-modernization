// TestTaskSeam ([Diag] TaskSeam, taskseam.cpp / taskseam-core.h, docs/308). Included by selftest.cpp.
// The histogram on plain numbers, then the hooks on a fake RunTask / runtime invoke: every run goes through exactly once with
// its arguments, is counted under its owner row, the side table is reused per node and resolved again for a new task on the
// same node, other threads / nested runs / switch off pass straight through uncounted, the invoke result and exc pass through.
static int g_tskFakeRuns;
static void* g_tskFakeEntry;
static void __fastcall TskFakeRun(void*, void*, void* entry, DWORD a, DWORD b) { assert(entry == g_tskFakeEntry && a == 1 && b == 0); g_tskFakeRuns++; }
static DWORD __cdecl TskFakeInvoke(DWORD m, DWORD o, DWORD, DWORD* exc) { g_tskFakeRuns++; if (exc) *exc = 0; return m + o; }

static void __fastcall TskFakeRunThrow(void*, void*, void*, DWORD, DWORD) { RaiseException(0xE0001234, 0, 0, NULL); }
static DWORD __cdecl TskFakeInvokeThrow(DWORD, DWORD, DWORD, DWORD*) { RaiseException(0xE0001234, 0, 0, NULL); return 0; }
// an SEH exception out of the next hook: the seam's depth is back to 0 after the unwind
static bool TskThrowLeavesDepth0(void* entry) {
    __try { TskRunHook(NULL, NULL, entry, 1, 0); } __except (EXCEPTION_EXECUTE_HANDLER) {}
    if (g_tskDepth) return false;
    __try { TskInvokeHook(0, 0, 0, NULL); } __except (EXCEPTION_EXECUTE_HANDLER) {}
    return g_tskDepth == 0;
}

static void TestTaskSeam() {
    assert(TsBucket(0) == 0 && TsBucket(0.9) == 0 && TsBucket(1) == 1 && TsBucket(3) == 2 && TsBucket(4) == 3 && TsBucket(1e12) == TS_HB - 1);
    LONG h[TS_HB] = {};
    assert(TsQuantUs(h, 0.5) == 0);
    h[3] = 90, h[10] = 10;  // 90 runs in [4, 8) us, 10 in [512, 1024) us
    assert(TsQuantUs(h, 0.5) == 8 && TsQuantUs(h, 0.9) == 8 && TsQuantUs(h, 0.99) == 1024 && TsQuantUs(h, 1) == 1024);

    const TsRunTaskFn savedNext = g_tskNext;
    const TskInvokeFn savedInv = g_tskInvNext;
    const int savedOn = g_tskOn;
    const DWORD savedTid = g_tskSimTid;
    Ts3nCounters& k = ts3native_counters;
    const LONG r0 = k.tskRuns, i0 = k.tskInvRuns;
    QueryPerformanceFrequency(&g_tskQpf);
    g_tskNext = TskFakeRun, g_tskInvNext = TskFakeInvoke;
    g_tskSimTid = GetCurrentThreadId(), g_tskStart = TskNow(), g_tskOn = 1;
    memset(g_tskEnt, 0, sizeof(g_tskEnt));
    memset(g_tskOwn, 0, sizeof(g_tskOwn));
    // node: guid, +8 state; state: [0] method 0 (owner unreadable: row 1, game), [1] object 0 (no wrapper to unwrap)
    static DWORD state[8], state2[8];
    static u8 node[16];
    *(DWORD*)node = 0x11, *(DWORD*)(node + 4) = 0x22, *(DWORD*)(node + 8) = (DWORD)(uintptr_t)state;
    DWORD entry = (DWORD)(uintptr_t)node;
    g_tskFakeEntry = &entry;
    g_tskFakeRuns = 0;
    TskRunHook(NULL, NULL, &entry, 1, 0);
    TskRunHook(NULL, NULL, &entry, 1, 0);
    TskEnt* e = TskLookup((DWORD)(uintptr_t)node, (DWORD)(uintptr_t)state, 0, 0, 0x11, 0x22, true);
    assert(g_tskFakeRuns == 2 && k.tskRuns == r0 + 2 && e && e->owner == 1 && e->kind == MDT_GAME && e->scope == TS_SC_HH);
    assert(g_tskOwn[1].runs == 2 && g_tskOwn[1].runsIter == 2);
    // a new task on the same node: the entry is resolved again (same slot)
    e->owner = 7;
    *(DWORD*)(node + 8) = (DWORD)(uintptr_t)state2;
    TskRunHook(NULL, NULL, &entry, 1, 0);
    assert(e->state == (DWORD)(uintptr_t)state2 && e->owner == 1 && g_tskOwn[1].runs == 3);
    TskIter();
    assert(g_tskOwn[1].iters == 1 && g_tskOwn[1].maxRunsIter == 3 && g_tskOwn[1].runsIter == 0);
    // an unreadable node: still runs, counted under row 1
    DWORD bad = 0x10;
    g_tskFakeEntry = &bad;
    TskRunHook(NULL, NULL, &bad, 1, 0);
    assert(g_tskFakeRuns == 4 && g_tskOwn[1].runs == 4);
    g_tskFakeEntry = &entry;
    // another thread, a nested run, the switch off: straight through, uncounted
    const LONG r1 = k.tskRuns;
    g_tskSimTid = GetCurrentThreadId() + 1;
    TskRunHook(NULL, NULL, &entry, 1, 0);
    g_tskSimTid = GetCurrentThreadId();
    g_tskDepth = 1;
    TskRunHook(NULL, NULL, &entry, 1, 0);
    g_tskDepth = 0;
    g_tskOn = 0;
    TskRunHook(NULL, NULL, &entry, 1, 0);
    g_tskOn = 1;
    assert(g_tskFakeRuns == 7 && k.tskRuns == r1);
    // invoke path: counted as an invoke run, the result and exc pass through
    DWORD exc = 7;
    assert(TskInvokeHook(0, 0, 0, &exc) == 0 && exc == 0 && k.tskInvRuns == i0 + 1 && g_tskOwn[1].invRuns == 1 && g_tskFakeRuns == 8);
    g_tskOn = 0;
    assert(TskInvokeHook(0x100, 2, 0, &exc) == 0x102 && k.tskInvRuns == i0 + 1);
    g_tskOn = 1;
    g_tskNext = TskFakeRunThrow, g_tskInvNext = TskFakeInvokeThrow;
    assert(TskThrowLeavesDepth0(&entry));
    g_tskNext = TskFakeRun, g_tskInvNext = TskFakeInvoke;
    // a call site's target (E8 rel32); anything else is no call
    static u8 site[8] = {0xE8};
    *(LONG*)(site + 1) = 0x1000;
    assert(TskCallTarget((DWORD)(uintptr_t)site) == (DWORD)(uintptr_t)site + 5 + 0x1000);
    site[0] = 0xE9;
    assert(TskCallTarget((DWORD)(uintptr_t)site) == 0);

    g_tskNext = savedNext, g_tskInvNext = savedInv, g_tskOn = savedOn, g_tskSimTid = savedTid;
    memset(g_tskEnt, 0, sizeof(g_tskEnt));
    memset(g_tskOwn, 0, sizeof(g_tskOwn));
    printf("taskseam: histogram, run / invoke hooks (owner rows, node reuse, threads, nesting, off, SEH unwind) ok\n");
}
