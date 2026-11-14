// --- docs/177 §7 [RouteAsync] RefcountGuard (rcguard.cpp, counters v104 rc*): on the async setup of TestH2Ic (real worker,
// M1 world lock as +0x918). A fake query with 0x82EDB0's frame (push ebp; mov ebp,esp; vector at [ebp+0x10]) takes the lock,
// sets the steering key as rtsites.cpp does and calls RcRelThunk the way the retargeted 0x82F098 does; a fake caller with an
// ebp frame calls it. Cases: level kept until the caller returns, empty vector, Mode 0, two queries in one caller, other
// thread, caller not listed, exception through the caller (stale frame released at the next query), RcDrop inside the caller,
// and a job claimed while the level is kept: the caller's collect (WaitJobDone) drops it and the job runs (no deadlock).
// Included by selftest.cpp after h2ic-test.h.
static CRITICAL_SECTION* g_rcTLock;  // the M1 lock (g_rgLock)
static DWORD g_rcTVec[2];
static DWORD* g_rcTVecAddr = g_rcTVec;  // pushed as a value: clang-cl 23 crashes on `push offset <global>`            // the caller's hit vector (begin, end)
static int g_rcTCase, g_rcTQueries;
static LONG g_rcTHeld;                // recursion count seen inside the caller after its queries
static void __cdecl RcTEnter(DWORD ret) {  // the query's acquire and rtsites.cpp's RT_STEER key
    EnterCriticalSection(g_rcTLock);
    t_steerRet = ret;
    if (g_rcTCase != 6) g_rcTestRet = ret;
    if (g_rcTCase == 10) t_stubKey = 0x00755990;  // the query runs under a call-site stub (IcStub)
}
static void __cdecl RcTLeave() { t_steerRet = 0, t_stubKey = 0; }
static __declspec(naked) void RcTQuery() {  // thiscall-like, 3 stack arguments (a, b, vector), ret 0xC
    __asm {
        push ebp
        mov ebp, esp
        and esp, 0xFFFFFFF0
        sub esp, 0x20
        push dword ptr [ebp + 4]
        call RcTEnter
        add esp, 4
        mov ecx, g_rcTLock
        call RcRelThunk  // as the retargeted call at 0x82F098
        call RcTLeave
        mov esp, ebp
        pop ebp
        ret 0xC
    }
}
static LONG RcTCount() { return g_rcTLock->OwningThread == (HANDLE)(uintptr_t)GetCurrentThreadId() ? g_rcTLock->RecursionCount : 0; }
static void __cdecl RcTBody() {
    Ts3nCounters& k = ts3native_counters;
    g_rcTHeld = RcTCount();
    switch (g_rcTCase) {
        case 7: RaiseException(0xE0000003, 0, 0, NULL); break;
        case 8:  // RcDrop inside the caller: the level goes now, not again at the return
            RcDrop();
            assert(RcTCount() == 0 && k.rcDropped[0] == 1);
            break;
        case 9: {  // a job claimed while the level is kept waits for the lock; the caller's collect drops it first
            u8 out[0x48];
            assert(Start(kH1));
            while (JobFor(kH1)->state != J_RUNNING) Sleep(0);
            Sleep(20);
            assert(g_async.state != A_RUNNING && JobFor(kH1)->state == J_RUNNING);  // blocked on our level
            const DWORD t0 = GetTickCount();
            // not READY: IcPlanHook waits for the job (WaitJobDone), then plans here (as TestH2Ic case 3)
            assert(IcPlanHook(out, kH1) == out && *(DWORD*)(out + 0x40) == GetCurrentThreadId());
            assert(RcTCount() == 0 && k.rcDropped[0] >= 1);
            TIMING_ASSERT("RcGuard job-wait plan < 1 s", GetTickCount() - t0 < 1000);
            break;
        }
    }
}
static __declspec(naked) void RcTCaller() {  // cdecl, no arguments: ebp frame, g_rcTQueries queries, then the body
    __asm {
        push ebp
        mov ebp, esp
        and esp, 0xFFFFFFF0
        push esi
        push edi
        mov esi, g_rcTQueries
    again:
        push g_rcTVecAddr
        push 0
        push 0
        call RcTQuery
        dec esi
        jnz again
        call RcTBody
        pop edi
        pop esi
        mov esp, ebp
        pop ebp
        ret
    }
}
static __declspec(noinline) void RcTRun(int which, int queries) {  // one frame layout for every case (case 7's stale slot)
    g_rcTCase = which;
    g_rcTQueries = queries;
    RcTCaller();
}

static void TestRcGuard() {
    static_assert(offsetof(Ts3nCounters, rcMode) == 0x44D4 && offsetof(Ts3nCounters, rcByFn) == 0x44E0 &&
                  offsetof(Ts3nCounters, rcPass) == 0x4510 && offsetof(Ts3nCounters, rcDropped) == 0x4530 &&
                  offsetof(Ts3nCounters, rcWould) == 0x4544 && sizeof(Ts3nCounters) == 6337 * 4,
                  "counters v104 layout after v103 (docs/registry.md)");
    Ts3nCounters& k = ts3native_counters;
    memset((void*)&k.rcMode, 0, sizeof(Ts3nCounters) - offsetof(Ts3nCounters, rcMode));
    DWORD me = GetCurrentThreadId();
    const DWORD* savedSim = g_rtSimTidSlot;
    g_rtSimTidSlot = &me;
    g_rcTLock = (CRITICAL_SECTION*)g_rgLock;
    void* const savedOrig = g_rcRelOrig;
    const LockReleaseFn savedRel = g_rcRel;
    g_rcRelOrig = (void*)FakeLockRelease;
    g_rcRel = FakeLockRelease;
    assert(g_rcTLock && !AsyncBusy() && RcTCount() == 0);
    k.rcMode = 1;
    // 1. kept until the caller returns
    g_rcTVec[0] = 0x1000, g_rcTVec[1] = 0x1008;
    RcTRun(0, 1);
    assert(g_rcTHeld == 1 && RcTCount() == 0 && t_rc.n == 0);
    assert(k.rcDeferred == 1 && k.rcFrames == 1 && k.rcLeaves == 1 && k.rcByFn[0] == 1 && k.rcHoldMaxUs >= 0);
    // 2. no hits: released as before
    g_rcTVec[1] = g_rcTVec[0];
    RcTRun(0, 1);
    assert(g_rcTHeld == 0 && k.rcPass[0] == 1 && k.rcDeferred == 1);
    g_rcTVec[1] = 0x1008;
    // 3. Mode 0: counted only
    k.rcMode = 0;
    RcTRun(0, 1);
    assert(g_rcTHeld == 0 && k.rcWould == 1 && k.rcDeferred == 1);
    k.rcMode = 1;
    // 4. two queries in one caller: one frame, two levels, both released at its return
    RcTRun(0, 2);
    assert(g_rcTHeld == 2 && RcTCount() == 0 && k.rcFrames == 2 && k.rcDeferred == 3 && k.rcLeaves == 2);
    // 5. another thread: passed through
    HANDLE th = CreateThread(NULL, 0, [](LPVOID) -> DWORD { RcTRun(0, 1); return (DWORD)g_rcTHeld; }, NULL, 0, NULL);
    DWORD held = 9;
    WaitForSingleObject(th, INFINITE);
    GetExitCodeThread(th, &held);
    CloseHandle(th);
    assert(held == 0 && k.rcOffSim == 1);
    // 6. caller not listed (no steering key match): passed through
    g_rcTestRet = 0;
    RcTRun(6, 1);
    assert(g_rcTHeld == 0 && k.rcPass[1] == 1);
    // 10. a listed caller's query from a call-site stub (t_stubKey; the stub's ebp is no frame): passed through
    RcTRun(10, 1);
    assert(g_rcTHeld == 0 && k.rcPass[1] == 2 && RcTCount() == 0 && t_rc.n == 0);
    // 7. an exception through the caller leaves its frame and level; the next query drops both (stale)
    __try { RcTRun(7, 1); } __except (EXCEPTION_EXECUTE_HANDLER) {}
    assert(t_rc.n == 1 && RcTCount() == 1);
    RcTRun(0, 1);
    assert(k.rcDropped[1] == 1 && t_rc.n == 0 && RcTCount() == 0 && g_rcTHeld == 1);
    // 8. RcDrop inside the caller (a sim wait): released once
    RcTRun(8, 1);
    assert(RcTCount() == 0 && k.rcDropped[0] == 1 && t_rc.n == 0);
    // 9. a claimed job blocked on the kept level: the collect's WaitJobDone drops it, the job runs, no deadlock
    RcTRun(9, 1);
    assert(RcTCount() == 0 && t_rc.n == 0 && !AsyncBusy());
    g_rcRelOrig = savedOrig;
    g_rcRel = savedRel;
    g_rcTestRet = 0;
    g_rtSimTidSlot = savedSim;
    memset((void*)&k.rcMode, 0, sizeof(Ts3nCounters) - offsetof(Ts3nCounters, rcMode));
    printf("refcount guard: level kept to the caller's return, no hits, Mode 0, two queries, off-sim, unlisted caller, "
           "unwound frame dropped, drop before a wait, claimed job collected without deadlock\n");
}
