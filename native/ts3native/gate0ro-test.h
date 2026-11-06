// [Gate0RO] (gate0ro.cpp, docs/152 §G1): selftest. Included by selftest.cpp after tailunlock-test.h (RgTryAcquire,
// RgFakeRelease, g_rgFakeCs, g_rgLockObj, g_enfHeld from the route gate tests). The test thread is the sim (the jobs'
// owner); the gate stub's entry RgEnter(0, slot) is called on a fake pushad frame whose wrapper ebp names the second frame.
// +0x918 is a real CRITICAL_SECTION (count at +0x20) held by a worker thread; QL is the real SRW lock, taken exclusive by a
// writer thread that rehashes the fake lot table (swaps the bucket array, poisons the old one) as W5 does.
// Checks: a G-a hit while the worker holds +0x918 enters without waiting, holds QL shared to its leave (a writer cannot get
// in), compares the predicted entry with the returned eax; G-b frames, misses, odd tables, pieces off, busy QL, a callback
// and another thread all wait as today; shadow with no job compares only; an unwind drops QL; threaded stress against the
// rehashing writer: no torn read, no mismatch.

static HANDLE g_g0Held, g_g0Go, g_g0Go2;
static DWORD WINAPI G0Holder(void*) {  // the worker's job: holds +0x918 until told, then ends the job
    static const DWORD inf = 0xFFFFFFFF;
    LockAcquireHook(g_rgLockObj, NULL, &inf);
    SetEvent(g_g0Held);
    WaitForSingleObject(g_g0Go, INFINITE);
    LockReleaseHook(g_rgLockObj, NULL);
    InterlockedExchange(&g_async.state, A_IDLE);
    SetEvent(g_asyncDone);
    return 0;
}
static DWORD WINAPI G0TryEx(void*) {  // another thread: can it take QL exclusive now?
    if (!TryAcquireSRWLockExclusive(&g_ql)) return 0;
    ReleaseSRWLockExclusive(&g_ql);
    return 1;
}
static DWORD G0OnOtherThread(LPTHREAD_START_ROUTINE fn) {
    HANDLE h = CreateThread(NULL, 0, fn, NULL, 0, NULL);
    WaitForSingleObject(h, INFINITE);
    DWORD r = 0;
    GetExitCodeThread(h, &r);
    CloseHandle(h);
    return r;
}
static DWORD WINAPI G0HoldEx(void*) {  // W5 in flight: QL exclusive until told
    AcquireSRWLockExclusive(&g_ql);
    SetEvent(g_g0Held);
    WaitForSingleObject(g_g0Go2, INFINITE);
    ReleaseSRWLockExclusive(&g_ql);
    return 0;
}
static u8* volatile g_g0P;
static DWORD g_g0Bk[2][2];
static volatile LONG g_g0Stop, g_g0Rehash;
static DWORD WINAPI G0Rehasher(void*) {  // W5: rehash under QL exclusive; the old bucket array is poisoned (freed memory)
    for (int i = 0; !g_g0Stop; i++) {
        AcquireSRWLockExclusive(&g_ql);
        DWORD* cur = *(DWORD**)(g_g0P + 0x4C);
        DWORD* nxt = cur == g_g0Bk[0] ? g_g0Bk[1] : g_g0Bk[0];
        nxt[0] = cur[0] == 0x10 ? nxt[0] : cur[0];
        nxt[1] = 0x5E117E1;
        *(DWORD**)(g_g0P + 0x4C) = nxt;
        cur[0] = 0x10;  // a reader without QL would follow it into unreadable memory
        ReleaseSRWLockExclusive(&g_ql);
        InterlockedIncrement(&g_g0Rehash);
        if (!(i & 15)) Sleep(0);
    }
    return 0;
}

static void TestGate0Ro() {
    static_assert(offsetof(Ts3nCounters, g0Mode) == 0x3998 && offsetof(Ts3nCounters, g0Shadow) == 0x399C &&
                  offsetof(Ts3nCounters, g0Calls) == 0x39A4 && offsetof(Ts3nCounters, g0Fb) == 0x39AC &&
                  offsetof(Ts3nCounters, g0Checks) == 0x39BC && offsetof(Ts3nCounters, g0Other) == 0x39C4 &&
                  sizeof(Ts3nCounters) == 5946 * 4, "counters v91 layout after v90 (docs/registry.md)");
    Ts3nCounters& k = ts3native_counters;
    memset(&k.g0Mode, 0, sizeof(Ts3nCounters) - offsetof(Ts3nCounters, g0Mode));
    const DWORD me = GetCurrentThreadId();
    static DWORD simTid;
    simTid = me;
    const DWORD* savedSim = g_rtSimTidSlot;
    g_rtSimTidSlot = &simTid;
    const LockAcquireFn savedLock = g_lockOrig;
    const LockReleaseFn savedRel = g_rgRelOrig;
    const HANDLE savedDone = g_asyncDone, savedInv = g_invReady;
    u8* const* savedWm = g_srWmSlot;
    const DWORD savedOwner = g_async.owner;
    g_lockOrig = RgTryAcquire;
    g_rgRelOrig = RgFakeRelease;
    InitializeCriticalSection(&g_rgFakeCs);
    g_asyncDone = CreateEventW(NULL, FALSE, FALSE, NULL);
    g_invReady = CreateEventW(NULL, FALSE, FALSE, NULL);
    g_g0Held = CreateEventW(NULL, FALSE, FALSE, NULL);
    g_g0Go = CreateEventW(NULL, FALSE, FALSE, NULL);
    g_g0Go2 = CreateEventW(NULL, FALSE, FALSE, NULL);
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
    k.rgReady = 7;
    k.rgEnforce = 1;
    g_rgLock = g_rgLockObj;
    g_async.lock = g_rgLockObj;
    g_async.owner = me;
    k.rgOwnerTid = 0;
    *(LONG*)(g_rgLockObj + 0x20) = 0;

    // lot table P (GetOrInsert's ecx = P + 0x48): lot 0x12 at node, the active lot 0x99
    static u8 p[0x200], wm[0x200], act[0x60], node[0x1A0];
    static u8* wmSlot;
    wmSlot = wm;
    g_srWmSlot = &wmSlot;
    *(u8**)(wm + 0xB0) = act;
    *(DWORD*)(act + 0x48) = 0x99;
    *(DWORD*)node = 0x12;
    *(DWORD*)(node + 0x10) = 0x12;
    g_g0Bk[0][0] = (DWORD)(uintptr_t)node;
    g_g0Bk[0][1] = 0x5E117E1;
    *(DWORD**)(p + 0x4C) = g_g0Bk[0];
    *(DWORD*)(p + 0x50) = 1;
    g_g0P = p;
    const DWORD hit = (DWORD)(uintptr_t)(node + 0x10);

    // the stub's frame: slot[-2] = ecx, slot[-6] = ebp (0x62EF30's frame), slot[0] = 0x62EF7A, slot[1] = key*; [ebp+4] = caller
    DWORD st[64] = {};
    DWORD* slot = st + 30;
    DWORD key[2] = {0x12, 0};
    auto frame = [&](DWORD second, DWORD lot) {
        key[0] = lot;
        slot[-2] = (DWORD)(uintptr_t)(p + 0x48), slot[-6] = (DWORD)(uintptr_t)&st[50], slot[0] = G0_WRAP_RET;
        slot[1] = (DWORD)(uintptr_t)key;
        st[51] = second;
    };
    auto qlFree = [&] { return G0OnOtherThread(G0TryEx) == 1; };
    auto startJob = [&] {
        ResetEvent(g_asyncDone);
        InterlockedExchange(&g_async.state, A_RUNNING);
        HANDLE w = CreateThread(NULL, 0, G0Holder, NULL, 0, NULL);
        WaitForSingleObject(g_g0Held, INFINITE);
        return w;
    };
    auto endJob = [&](HANDLE w) {
        SetEvent(g_g0Go);
        WaitForSingleObject(w, INFINITE);
        CloseHandle(w);
    };
    k.steerReady = 1, k.steerMode = 1, k.avReady = 0xF, k.avMode = 1;

    // off (not installed): a G-a hit during a job waits as today
    g_g0On = 0;
    frame(0x00846E0E, 0x12);
    {
        HANDLE w = startJob();
        HANDLE rel = CreateThread(NULL, 0, [](void*) -> DWORD { Sleep(20); SetEvent(g_g0Go); return 0; }, NULL, 0, NULL);
        LARGE_INTEGER t0;
        QueryPerformanceCounter(&t0);
        RgEnter(0, slot);
        TIMING_ASSERT("Gate0RO waited >= 10 ms for the job", UsSince(t0) >= 10000);
        assert(t_rg.n == 1 && k.g0Calls == 0);  // waited for the job
        assert(RgLeave(hit) == G0_WRAP_RET && *(LONG*)(g_rgLockObj + 0x20) == 0 && g_rgOffSimOpen == 0);
        WaitForSingleObject(w, INFINITE);
        CloseHandle(w);
        WaitForSingleObject(rel, INFINITE);
        CloseHandle(rel);
    }

    // served: each G-a second frame, the worker holding +0x918 the whole time
    g_g0On = 1;
    k.g0Mode = 1;
    {
        HANDLE w = startJob();
        for (DWORD ret2 : kG0Ga) {
            frame(ret2, 0x12);
            LARGE_INTEGER t0;
            QueryPerformanceCounter(&t0);
            RgEnter(0, slot);
            TIMING_ASSERT("Gate0RO served hit < 5 ms", UsSince(t0) < 5000);
            assert(t_rg.n == 1 && t_qlShared == 1 && !qlFree());  // no wait; QL shared kept
            assert(*(LONG*)(g_rgLockObj + 0x20) == 1 && g_rgOffSimOpen == 1);         // the worker's count only
            assert(RgLeave(hit) == G0_WRAP_RET && t_rg.n == 0 && t_qlShared == 0 && qlFree() && g_rgOffSimOpen == 0);
        }
        assert(k.g0Calls == 4 && k.g0Served == 4 && k.g0Checks == 4 && k.g0Mismatch == 0);
        // the returned eax differs from the prediction: counted (an insert ran)
        frame(0x0085B57E, 0x12);
        RgEnter(0, slot);
        assert(RgLeave(hit + 0x40) == G0_WRAP_RET && k.g0Mismatch == 1 && k.g0Checks == 5 && qlFree());
        // an unwind leaves the frame: dropped at the next entry, QL back
        frame(0x0085F76D, 0x12);
        RgEnter(0, slot);
        assert(t_qlShared == 1);
        RgDrop(t_rg, slot);
        assert(t_rg.n == 0 && t_qlShared == 0 && qlFree() && g_rgOffSimOpen == 0 && k.g0Served == 6);
        // a full frame stack: no frame, nothing kept
        for (int i = 0; i < 16; i++) t_rg.f[i] = RgFrame{slot + 20, 0, 1, 0, NULL};  // reader frames of outer callers
        t_rg.n = 16, t_rg.readers = 16;
        frame(0x00846E0E, 0x12);
        RgEnter(0, slot);
        assert(t_rg.n == 16 && t_qlShared == 0 && qlFree() && g_rgOffSimOpen == 0);
        t_rg.n = 0, t_rg.readers = 0;
        endJob(w);
    }

    // not ours: each waits for the job (G-b, miss, active lot, odd table, pieces off, busy QL, not the owner, a callback)
    struct Case { DWORD ret2, lot; int fb; };  // fb -1 = G-b (g0Other)
    const Case cases[] = {{0x0075167F, 0x12, -1}, {0x00846E0E, 0x44, G0_FB_TABLE}, {0x00846E0E, 0x99, G0_FB_TABLE},
                          {0x00846E0E, 0x12, G0_FB_PIECES}, {0x00846E0E, 0x12, G0_FB_QL}, {0x00846E0E, 0x12, G0_FB_STATE},
                          {0x00846E0E, 0x12, G0_FB_TABLE}};
    for (int c = 0; c < 7; c++) {
        const LONG fb0 = cases[c].fb < 0 ? k.g0Other : k.g0Fb[cases[c].fb];
        HANDLE ex = NULL;
        if (c == 3) k.avMode = 0;
        if (c == 4) {
            ex = CreateThread(NULL, 0, G0HoldEx, NULL, 0, NULL);
            WaitForSingleObject(g_g0Held, INFINITE);
        }
        if (c == 5) g_async.owner = me + 1;
        if (c == 6) *(DWORD*)(p + 0x50) = 0;  // the original would divide by 0
        HANDLE w = startJob();
        frame(cases[c].ret2, cases[c].lot);
        HANDLE rel = CreateThread(NULL, 0, [](void*) -> DWORD { Sleep(15); SetEvent(g_g0Go); return 0; }, NULL, 0, NULL);
        LARGE_INTEGER t0;
        QueryPerformanceCounter(&t0);
        RgEnter(0, slot);
        TIMING_ASSERT("Gate0RO fallback waited >= 8 ms", UsSince(t0) >= 8000);
        assert(t_rg.n == 1 && t_qlShared == 0);
        assert((cases[c].fb < 0 ? k.g0Other : k.g0Fb[cases[c].fb]) == fb0 + 1);
        assert(RgLeave(0x0BAD) == G0_WRAP_RET && *(LONG*)(g_rgLockObj + 0x20) == 0 && g_rgOffSimOpen == 0);
        WaitForSingleObject(w, INFINITE);
        CloseHandle(w);
        WaitForSingleObject(rel, INFINITE);
        CloseHandle(rel);
        if (ex) {
            SetEvent(g_g0Go2);
            WaitForSingleObject(ex, INFINITE);
            CloseHandle(ex);
        }
        k.avMode = 1;
        g_async.owner = me;
        *(DWORD*)(p + 0x50) = 1;
    }
    assert(k.g0Served == 7 && k.g0Calls == 13 && k.g0Checks == 5 && k.g0Mismatch == 1 && qlFree());
    // a callback the owner serves for the job: borrows as before (no wait, no G1 frame)
    {
        HANDLE w = startJob();
        g_async.owner = me;
        g_inInvoke = 1;
        frame(0x00846E0E, 0x12);
        const LONG b0 = k.rgBorrows, fb3 = k.g0Fb[G0_FB_STATE];
        RgEnter(0, slot);
        assert(k.rgBorrows == b0 + 1 && k.g0Fb[G0_FB_STATE] == fb3 + 1 && t_qlShared == 0);
        RgLeave(hit);
        g_inInvoke = 0;
        endJob(w);
    }

    // shadow, no job: prediction compared, no QL kept, no wait; off: nothing
    k.g0Mode = 0;
    frame(0x00846E0E, 0x12);
    RgEnter(0, slot);
    assert(t_rg.n == 1 && t_qlShared == 0 && k.g0Checks == 5);
    RgLeave(hit);
    k.g0Shadow = 1;
    frame(0x00846E0E, 0x12);  // the push left the leave thunk in the slot
    RgEnter(0, slot);
    assert(t_rg.n == 1 && t_qlShared == 0 && qlFree() && g_rgOffSimOpen == 1);
    assert(RgLeave(hit) == G0_WRAP_RET && k.g0Checks == 6 && k.g0Mismatch == 1 && g_rgOffSimOpen == 0);
    frame(0x0075167F, 0x12);  // G-b: not compared
    RgEnter(0, slot);
    RgLeave(hit);
    assert(k.g0Checks == 6 && k.g0Calls == 14);
    k.g0Shadow = 0;

    // threaded: a job running (lock free so a fallback never blocks), a writer rehashing under QL exclusive
    k.g0Mode = 1;
    k.g0Mismatch = 0;
    InterlockedExchange(&g_async.state, A_RUNNING);
    g_g0Stop = 0;
    g_g0Rehash = 0;
    HANDLE wr = CreateThread(NULL, 0, G0Rehasher, NULL, 0, NULL);
    const LONG served0 = k.g0Served, ql0 = k.g0Fb[G0_FB_QL];
    // bounded by time, not iterations: under load the rehasher can get too few slices in a fixed count
    const ULONGLONG tEnd = GetTickCount64() + 15000;
    for (int i = 0; (k.g0Served - served0 < 2000 || g_g0Rehash < 2000) && GetTickCount64() < tEnd; i++) {
        frame(kG0Ga[i & 3], 0x12);
        RgEnter(0, slot);
        const bool held = t_qlShared == 1;
        const u8* e = NULL;
        if (held) assert(LtFind(p, 0x12, 0, &e) == LT_OK && e == node + 0x10);  // the body's read, under the hold
        RgLeave(held ? (DWORD)(uintptr_t)e : hit);
        assert(t_rg.n == 0 && t_qlShared == 0);
    }
    g_g0Stop = 1;
    WaitForSingleObject(wr, INFINITE);
    CloseHandle(wr);
    assert(k.g0Served - served0 >= 2000 && g_g0Rehash >= 2000 && k.g0Mismatch == 0);
    assert(k.g0Fb[G0_FB_QL] - ql0 + k.g0Served - served0 == k.g0Calls - 14);  // every G-a call: served or QL busy
    assert(*(LONG*)(g_rgLockObj + 0x20) == 0 && g_rgOffSimOpen == 0 && qlFree());
    InterlockedExchange(&g_async.state, A_IDLE);
    printf("Gate0RO ([Gate0RO], counters v91): G-a hits served with no +0x918 wait while the worker holds it, QL shared to "
           "the leave, eax compared; G-b / miss / active lot / odd table / pieces off / QL busy / not owner / callback wait "
           "as today; shadow compares only; unwind and full frame stack release QL; threaded vs a rehashing W5 writer: %ld "
           "served, %ld QL busy, %ld rehashes, 0 mismatches\n",
           k.g0Served - served0, k.g0Fb[G0_FB_QL] - ql0, g_g0Rehash);

    g_g0On = 0;
    g_srWmSlot = savedWm;
    g_async.owner = savedOwner;
    g_async.lock = NULL;
    g_rgLock = NULL;
    k.rgEnforce = 0;
    k.rgReady = 0;
    k.rgOwnerTid = 0;
    k.steerReady = 0, k.steerMode = 0, k.avReady = 0, k.avMode = 0;
    memset(&k.g0Mode, 0, sizeof(Ts3nCounters) - offsetof(Ts3nCounters, g0Mode));
    g_lockOrig = savedLock;
    g_rgRelOrig = savedRel;
    g_rtSimTidSlot = savedSim;
    CloseHandle(g_asyncDone);
    CloseHandle(g_invReady);
    CloseHandle(g_g0Held);
    CloseHandle(g_g0Go);
    CloseHandle(g_g0Go2);
    g_asyncDone = savedDone;
    g_invReady = savedInv;
    DeleteCriticalSection(&g_rgFakeCs);
}
