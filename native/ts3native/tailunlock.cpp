// [RouteAsync] TailUnlock=1 (docs/150 §11; counters v89). Included by ts3native.cpp after p5c.cpp (uses P5b's window).
// The post pass 0x64E380 ends with passes 16-19 (0x64C7C0 smoothing + heights, 0x64D5B0 portals, 0x64A690 portal entry,
// 0x641DF0 type reset): job-private path nodes, the planner's tuning, world manager heights / lots (WM+0x228) and water;
// no +0x918-guarded routing state in 166 functions to 8 hops (docs/150 §11.1 [verified, direct calls]). So the route worker
// lends +0x918 to the sim while they run, the same window P5b opens around a clone run:
//   - open (worker, before pass 16): only inside an async job that holds the lock (ServeAsync's level and Route_PlanLocked's,
//     any count 1-16, the game's own count at +0x20 equal); not in a clone run or with P4 installed (snapshot proofs stay as
//     they are); publish g_p5bWin, wait up to 200 us for no disallowed acquire inside the game's Lock_Acquire, release every
//     level with the game's Lock_Release;
//   - while open, P5bLock admits only the sim's steering (RT_STEER frames) and, with [RouteSnap] ReleaseGates, its read-only
//     gate leaves; everything else (writers, gates, destroys, sync plans) sees the worker's hold as before and g_async.state
//     stays A_RUNNING (docs/94 §22.9 rules, unchanged);
//   - retake (worker) with the game's own Lock_Acquire: after pass 19, or when pass 16, 17 or 18 fails (0x64E380 then returns
//     false at once), or at a tripwire (TuTrip: a worker +0x918 acquire, a gate site or a routing site other than the lot index
//     / physics reads while open); nothing after 0x64E380 runs without the hold (docs/150 §11.2).
// Four call-site retargets in 0x64E380 (no detour on the passes: their entries stay free for other owners).
// shortcut: the tripwire covers acquires and our framed sites only, not every guarded leaf (lot-table / quadtree writers have
// no frame of their own); the static closure is the proof, the tripwire the alarm.

static const DWORD TU_P16 = TS3A(0x0064C7C0), TU_P17 = TS3A(0x0064D5B0), TU_P18 = TS3A(0x0064A690), TU_P19 = TS3A(0x00641DF0);
typedef bool(__fastcall* TuFn1)(void* pp, void* edx, DWORD a);  // thiscall via fastcall + dummy edx, callee pops
typedef bool(__fastcall* TuFn2)(void* pp, void* edx, DWORD a, DWORD b);
typedef bool(__fastcall* TuFn3)(void* pp, void* edx, DWORD a, DWORD b, DWORD c);
// the passes called (the game's; the selftest swaps in fakes)
static TuFn2 g_tuP16 = (TuFn2)(uintptr_t)TU_P16;
static TuFn3 g_tuP17 = (TuFn3)(uintptr_t)TU_P17;
static TuFn1 g_tuP18 = (TuFn1)(uintptr_t)TU_P18;
static TuFn1 g_tuP19 = (TuFn1)(uintptr_t)TU_P19;
static __declspec(thread) LARGE_INTEGER t_tuT0;  // window start
static volatile LONG g_tuLog;

static void TuOpen() {
    Ts3nCounters& k = ts3native_counters;
    if (!k.tuMode) { InterlockedIncrement(&k.tuRefused[0]); return; }
    void* lock = g_rgLock;
    const DWORD me = GetCurrentThreadId();
    const CRITICAL_SECTION* cs = (const CRITICAL_SECTION*)lock;
    int why = -1;
    if (g_p5bWin || t_p5b == P5B_OPEN) why = 3;
    else if (!RgIsWorker(me) || t_snapRun || g_p4On || !lock || lock != g_async.lock || g_async.state != A_RUNNING ||
             P5bOwner(lock) != (LONG)me || cs->RecursionCount < 1 || cs->RecursionCount > 16 ||
             *(const LONG*)((const u8*)lock + 0x20) != cs->RecursionCount)
        why = 1;
    if (why < 0) {  // Dekker with P5bLock: published, then no disallowed acquirer may be inside the game's Lock_Acquire
        InterlockedExchange(&g_p5bWin, 1);
        LARGE_INTEGER t0;
        QueryPerformanceCounter(&t0);
        while (InterlockedCompareExchange(&g_p5bIn, 0, 0) && UsSince(t0) < kP5bInWaitUs) YieldProcessor();
        if (InterlockedCompareExchange(&g_p5bIn, 0, 0)) {
            InterlockedExchange(&g_p5bWin, 0);
            why = 2;
        }
    }
    if (why >= 0) { InterlockedIncrement(&k.tuRefused[why]); return; }
    g_p5bLock = lock;
    g_p5bN = cs->RecursionCount;
    InterlockedCompareExchange(&k.rgOwnerTid, 0, (LONG)me);
    for (LONG i = 0; i < g_p5bN; i++) g_rgRelOrig(lock, NULL);
    QueryPerformanceCounter(&t_tuT0);
    t_tuOpen = true;
    InterlockedIncrement(&k.tuWins);
}

// worker: every level back with the game's Lock_Acquire, then the window closes (P5bRetake's loop; only an admitted steering
// call can hold it, and it waits for no job)
static void TuClose() {
    if (!t_tuOpen) return;
    t_tuOpen = false;
    Ts3nCounters& k = ts3native_counters;
    static const DWORD kTry = 0, kInf = 0xFFFFFFFF;
    void* lock = g_p5bLock;
    LARGE_INTEGER t0;
    QueryPerformanceCounter(&t0);
    InterlockedExchangeAdd(&k.tuReleasedUs, UsSince(t_tuT0));
    for (LONG next = 2000; g_lockOrig(lock, NULL, &kTry) == -2;) {
        SwitchToThread();
        if (UsSince(t0) / 1000 >= next) {
            InterlockedIncrement(&k.p5bWatchdog);
            if (InterlockedIncrement(&g_tuLog) <= 20)
                Log("TailUnlock: worker waited %ld s to take +0x918 %p back (owner thread %ld, sim %lu); still trying", next / 1000,
                    lock, P5bOwner(lock), g_async.owner);
            next += 2000;
        }
    }
    for (LONG i = 1; i < g_p5bN; i++) g_lockOrig(lock, NULL, &kInf);  // recursive: ours already
    k.rgOwnerTid = (LONG)GetCurrentThreadId();
    InterlockedExchange(&g_p5bWin, 0);
    const LONG us = UsSince(t0);
    InterlockedExchangeAdd(&k.tuRetakeUs, us);
    Max(&k.tuRetakeMaxUs, us);
}

// CE-4 (docs/154 fix D), worker: a TailUnlock window (or a P5b one, after its clone run) still open where the job releases
// +0x918 (LockReleaseHook) or after CallIc: only an unwind that skipped TuClose / P5bEnd leaves one. Every level back, logged
static bool LendBackstop() {
    if (!t_tuOpen && t_p5b != P5B_OPEN) return false;    if (InterlockedIncrement(&g_tuLog) <= 20)
        Log("route async: %s window still open at the job's release (an unwind skipped its close): +0x918 taken back",
            t_tuOpen ? "TailUnlock" : "P5b");
    TuClose();
    if (t_p5b == P5B_OPEN) P5bEnd();
    return true;
}

static void TuTrip(int where) {
    if (!t_tuOpen) return;
    InterlockedIncrement(&ts3native_counters.tuTrip[where]);
    if (InterlockedIncrement(&g_tuLog) <= 20)
        Log("TailUnlock: tripwire %d on the worker inside passes 16-19 (caller %08lX): +0x918 taken back for the rest", where,
            (DWORD)(uintptr_t)_ReturnAddress());
    TuClose();
}

// the four call sites in 0x64E380 (each pass false = the post pass fails: the hold back before that return)
static bool __fastcall TuS16(void* pp, void*, DWORD a, DWORD b) {
    TuOpen();
    const bool r = g_tuP16(pp, NULL, a, b);
    if (!r) TuClose();
    return r;
}
static bool __fastcall TuS17(void* pp, void*, DWORD a, DWORD b, DWORD c) {
    const bool r = g_tuP17(pp, NULL, a, b, c);
    if (!r) TuClose();
    return r;
}
static bool __fastcall TuS18(void* pp, void*, DWORD a) {
    const bool r = g_tuP18(pp, NULL, a);
    if (!r) TuClose();
    return r;
}
static bool __fastcall TuS19(void* pp, void*, DWORD a) {
    const bool r = g_tuP19(pp, NULL, a);
    TuClose();
    return r;
}

static const CallSite kTuSites[4] = {{TS3A(0x0064E4ED), TU_P16, (void*)TuS16}, {TS3A(0x0064E50B), TU_P17, (void*)TuS17},
                                     {TS3A(0x0064E523), TU_P18, (void*)TuS18}, {TS3A(0x0064E533), TU_P19, (void*)TuS19}};

// [RouteAsync] TailUnlock=1 (after InstallRtSites: needs the steering frames; P5bLock's Lock_Release original)
static void InstallTailUnlock(const wchar_t* ini) {
    Ts3nCounters& k = ts3native_counters;
    if (!GetPrivateProfileIntW(L"RouteAsync", L"TailUnlock", 0, ini)) { Log("tail unlock off (TailUnlock=0)"); return; }
    const LONG steer = 1 << 23 | 1 << 24;  // rtsites.cpp: the two RT_STEER sites (they set t_steerRet)
    if (!k.asyncReady || !g_lockOrig || !g_rgRelOrig || !k.rgEnforce || (k.rtReady & steer) != steer) {
        Log("SKIP tail unlock: needs [RouteAsync] with [RouteGate] Enabled=1 Sites=1 Enforce=1 (Lock_Release, steering frames)");
        return;
    }
    if (!PatchCalls("tail unlock (post pass 0x64E380, passes 16-19)", kTuSites, 4)) return;
    k.tuMode = 1;
    k.tuReady = 1;
    g_tuOn = 1;
    Log("OK   tail unlock: +0x918 lent to the sim's steering while passes 16-19 of the post pass run (switch tuMode +0x3878)");
}
