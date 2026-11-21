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

// [RouteAsync] PlanEndLend / LendWake (docs/673 §9; PelPlanEnd below). Exported block, not in the ts3fixes slot range
// (docs/registry.md "native exports")
struct PlanEndLendCounters {
    volatile LONG pelMode;        // +0x00 runtime switch: 0 off, 1 lend, 2 count only (no window)
    volatile LONG pelReady;       // +0x04 1 = installed
    volatile LONG pelWaitUs;      // +0x08 PlanEndLendWaitUs: how long the parked worker waits for the sim's taker
    volatile LONG pelWake;        // +0x0C LendWake (runtime switch): a TailUnlock window opened with the sim waiting wakes its pump
    volatile LONG pelEnds;        // +0x10 plan ends looked at (worker job, Route_PlanCommon's Route_PlanLocked returned)
    volatile LONG pelSimWait;     // +0x14 of them: the sim thread waited on +0x918
    volatile LONG pelWins;        // +0x18 windows opened
    volatile LONG pelRefused[4];  // +0x1C no window: [1] lock / job state, [2] an acquire in flight, [3] a window already open
    volatile LONG pelTaken;       // +0x2C windows the sim's taker used (it held +0x918, or its wait ended)
    volatile LONG pelHeldOut;     // +0x30 windows closed early: the waiting taker is not admitted (P5bLock held it out)
    volatile LONG pelTimeout;     // +0x34 windows nobody took within pelWaitUs
    volatile LONG pelTrip;        // +0x38 assertion: the window was closed by something else while the worker was parked (must stay 0)
    volatile LONG pelParkMaxUs;   // +0x3C
    volatile LONGLONG pelParkUs;  // +0x40 worker time from the window's open to +0x918 taken back
    volatile LONGLONG pelTailUs;  // +0x48 from the retake to the job's release, used windows: the most the sim's wait got shorter
    volatile LONG pelTuWakes;     // +0x50 LendWake: TailUnlock windows that woke the sim's pump
    volatile LONG pelPad;         // +0x54
};
extern "C" __declspec(dllexport) PlanEndLendCounters ts3native_planendlend = {};

// -1 = the window is open (every level of the job's +0x918 released), else why not (the tuRefused index)
static int TuOpenWhy() {
    Ts3nCounters& k = ts3native_counters;
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
    if (why >= 0) return why;
    g_p5bLock = lock;
    g_p5bN = cs->RecursionCount;
    InterlockedCompareExchange(&k.rgOwnerTid, 0, (LONG)me);
    for (LONG i = 0; i < g_p5bN; i++) g_rgRelOrig(lock, NULL);
    QueryPerformanceCounter(&t_tuT0);
    t_tuOpen = true;
    return -1;
}

static void TuOpen() {
    Ts3nCounters& k = ts3native_counters;
    if (!k.tuMode) { InterlockedIncrement(&k.tuRefused[0]); return; }
    const int why = TuOpenWhy();
    if (why >= 0) { InterlockedIncrement(&k.tuRefused[why]); return; }
    InterlockedIncrement(&k.tuWins);
    // LendWake (docs/673 §9): the sim's +0x918 wait pump (LockAcquireAsync) tries the lock once a millisecond, or when
    // g_invReady / g_asyncDone is set; passes 16-19 are often over before its next try. The event only makes it try now
    // (every waiter on it re-reads its own condition in a loop)
    if (ts3native_planendlend.pelWake && g_whSimWait > 0) {
        SetEvent(g_asyncDone);
        InterlockedIncrement(&ts3native_planendlend.pelTuWakes);
    }
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

// --- [RouteAsync] PlanEndLend (docs/673 §9) ---
// The worker's job holds +0x918 from before its IC to after it. When Route_PlanCommon's Route_PlanLocked call returns
// (return address 0x760DE6: the only planner call of the four route ICs), the search is over: what is left of the job is the
// result mapping, RouteFollow_EditPathForRoute per path (it takes +0x918 itself for its gather and then reads lot rows,
// polygon caches and footprint refcounts outside its own acquire, so it needs the job's hold) and our job end. If the sim
// thread is waiting on +0x918 right then, the worker lends the lock here: a TailUnlock window (same admission, P5bLock;
// same job state, A_RUNNING), in which the worker is PARKED: it runs no game code until it has every level back. So nothing
// of the tail runs without the hold, and the tail reads the world as it is after the sim's taker, which is what
// EditPathForRoute does whenever the game calls it later on a planned path (IC_Route_SetAGS).
// Why this point: after the call, Route_PlanCommon's frame uses the route object, its own goal vectors (by index) and the
// route's path list only [docs/673 §9.3, decompile]; the planner's frames (lot rows, quadtree nodes, A* tables) are gone.
// The window ends when the sim's taker has the lock (the retake then waits for its release), when P5bLock held the waiter
// out (not an admitted class: nothing to wait for), or after PlanEndLendWaitUs.
// Mode 2 counts the plan ends and the sim's waits there and opens nothing.
static const DWORD kPelPlanRet = TS3A(0x00760DE6);
static __declspec(thread) LONGLONG t_pelQ;  // this job's used window: the clock at the retake (0 = none)

static void PelPlanEnd(DWORD ret) {
    PlanEndLendCounters& c = ts3native_planendlend;
    const LONG mode = c.pelMode;
    if (!mode || !ret || ret != kPelPlanRet || !t_jobT0 || g_invJob || !RgIsWorker(GetCurrentThreadId())) return;
    InterlockedIncrement(&c.pelEnds);
    if (g_whSimWait <= 0) return;
    InterlockedIncrement(&c.pelSimWait);
    if (mode != 1) return;
    const Ts3nCounters& k = ts3native_counters;
    const LONG heldOut0 = k.p5bHeldOut;
    const int why = TuOpenWhy();
    if (why >= 0) { InterlockedIncrement(&c.pelRefused[why & 3]); return; }
    InterlockedIncrement(&c.pelWins);
    const void* lock = g_p5bLock;
    SetEvent(g_asyncDone);  // the sim's wait pump tries the lock now, not at its next 1 ms poll
    LARGE_INTEGER t0;
    QueryPerformanceCounter(&t0);
    int end = 0;  // 1 taken, 2 held out, 3 nobody came
    while (!end) {
        if (P5bOwner(lock) != 0 || g_whSimWait <= 0) end = 1;
        else if (k.p5bHeldOut != heldOut0) end = 2;
        else if (UsSince(t0) >= c.pelWaitUs) end = 3;
        else SwitchToThread();
    }
    if (!t_tuOpen) InterlockedIncrement(&c.pelTrip);  // the parked worker ran nothing that could close it
    TuClose();  // every level back; waits while the sim's taker holds it (an admitted taker waits for no job)
    const LONG us = UsSince(t0);
    InterlockedIncrement(end == 1 ? &c.pelTaken : end == 2 ? &c.pelHeldOut : &c.pelTimeout);
    InterlockedExchangeAdd64(&c.pelParkUs, us);
    if (us > c.pelParkMaxUs) c.pelParkMaxUs = us;
    if (end == 1) {
        LARGE_INTEGER q;
        QueryPerformanceCounter(&q);
        t_pelQ = q.QuadPart;
    }
}

// ServeAsync, right before the job's +0x918 release: the part of the job that ran after a used window
static void PelJobEnd() {
    if (!t_pelQ) return;
    LARGE_INTEGER q;
    QueryPerformanceCounter(&q);
    InterlockedExchangeAdd64(&ts3native_planendlend.pelTailUs, (q.QuadPart - t_pelQ) * 1000000 / g_qpf.QuadPart);
    t_pelQ = 0;
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

// [RouteAsync] PlanEndLend / PlanEndLendWaitUs / LendWake (after InstallTailUnlock: its window, P5bLock's admission)
static void InstallPlanEndLend(const wchar_t* ini) {
    PlanEndLendCounters& c = ts3native_planendlend;
    const LONG mode = Clamp(GetPrivateProfileIntW(L"RouteAsync", L"PlanEndLend", 0, ini), 0, 2);
    const LONG wake = GetPrivateProfileIntW(L"RouteAsync", L"LendWake", 0, ini) ? 1 : 0;
    if (!mode && !wake) { Log("plan end lend off (PlanEndLend=0, LendWake=0)"); return; }
    if (!ts3native_counters.tuReady || !g_asyncDone) {
        Log("SKIP plan end lend / lend wake: needs [RouteAsync] TailUnlock=1 installed (its window and admission)");
        return;
    }
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
    c.pelWaitUs = Clamp(GetPrivateProfileIntW(L"RouteAsync", L"PlanEndLendWaitUs", 300, ini), 20, 5000);
    c.pelWake = wake;
    if (mode && !kPelPlanRet) Log("SKIP plan end lend: Route_PlanCommon's return address is not mapped in this build");
    else c.pelMode = mode;
    c.pelReady = 1;
    Log("OK   plan end lend: PlanEndLend=%ld (%s; worker parked up to %ld us), LendWake=%ld (switches ts3native_planendlend +0x00, +0x0C)",
        c.pelMode, c.pelMode == 1 ? "+0x918 lent to a waiting sim taker when the job's plan has returned"
                                  : c.pelMode == 2 ? "count only" : "off", c.pelWaitUs, c.pelWake);
}

// the 60 s counter line (ts3native.cpp MaybeLogCounters)
static void PelLogCounters() {
    const PlanEndLendCounters& c = ts3native_planendlend;
    if (!c.pelReady) return;
    Log("plan end lend: mode %ld, plan ends %ld, with the sim waiting on +0x918 %ld; windows %ld (taken by the sim %ld, waiter "
        "held out %ld, nobody came %ld; refused: state %ld, acquire in flight %ld, window open %ld), worker parked %ld ms (max %ld us), "
        "job time after a taken window %ld ms (= the most the sim's waits ended earlier), closed while parked %ld (must be 0); "
        "lend wake %ld: TailUnlock windows that woke the sim %ld",
        c.pelMode, c.pelEnds, c.pelSimWait, c.pelWins, c.pelTaken, c.pelHeldOut, c.pelTimeout, c.pelRefused[1], c.pelRefused[2],
        c.pelRefused[3], (LONG)(c.pelParkUs / 1000), c.pelParkMaxUs, (LONG)(c.pelTailUs / 1000), c.pelTrip, c.pelWake, c.pelTuWakes);
}
