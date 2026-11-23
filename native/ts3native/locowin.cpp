// [LocoWindow] design H2 (docs/86 §5): no queued async route job starts while the sim thread runs the locomotion pass.
// Included by ts3native.cpp after lotfd.cpp.
//
// Site [verified disassembly, Steam 1.67]: MonoScriptHost_Simulate 0xD81840 calls Locomotion_Update 0x83CED0 once, at
// 0xD81C5C: `mov ecx,[0x11D9140]; test ecx,ecx; jz 0xD81C61; fld [esp+58h]; push ecx; fstp [esp]; call 0x83CED0`. The
// callee is thiscall (ecx = locomotion system, one float on the stack), reads only ecx and [ebp+8] at entry (eax is zeroed,
// edx is written before any read), has one `ret 4` (0x83D383), and the caller uses no register, flag or x87 value it
// returns (next: `mov ecx,[0x11CD800]`). So a __fastcall(self, edx, float) thunk is a drop-in for the rel32.
//
// The thunk publishes g_g0wIn (both modes) and, in Mode 1, g_g0Win before it reads the job state; ServeAsync publishes
// A_RUNNING before it reads g_g0Win (Dekker order, both interlocked). So either the pass sees the job (g0wRunningAtEntry:
// it may finish inside the pass) or the worker sees the window and holds the start until the pass returns.
//
// Sim waits on the worker, reviewed for a held start (docs/86 §9 "H2 as built"):
//   WaitJobDone (IcPlanHook, DropJob <- AsyncStart / CANCEL / DropAllJobs): waits for a job that may be queued or claimed
//     and held -> G0wDrop(G0W_JOBWAIT) first.
//   RunPlan with [RouteWorker] Mode 1: the worker serves g_jobReady only after ServeAsync returns -> G0wDrop(G0W_SYNCPLAN).
//   WaitAsyncDone (RunPlan, RtWaitJob, DiscDrain), gate waits (RgEnter), LockAcquireAsync pump, QL / C-A waits (steerro.cpp):
//     they wait only for a job in A_RUNNING (or a lock it holds), which a held start never is -> no drop needed.
// A missed site cannot deadlock: the worker lets go after kG0wMaxHoldUs (G0wHolds, g0wWatchdog).
static const DWORD LOCO_CALL = TS3A(0x00D81C5C), LOCO_UPDATE = TS3A(0x0083CED0);
static const u8 kLocoCall[] = {0xD9, 0x44, 0x24, 0x58, 0x51, 0xD9, 0x1C, 0x24, 0xE8, 0x6F, 0xB2, 0xAB, 0xFF};  // at 0xD81C54
typedef void(__fastcall* LocoUpdateFn)(void* self, void* edx, float dt);
static LocoUpdateFn g_g0wOrig = (LocoUpdateFn)(uintptr_t)LOCO_UPDATE;  // selftest / steerro-test point it at a fake pass
static DWORD g_g0wTid;            // the thread inside the pass (the sim)
static __declspec(thread) int t_g0wDepth;
static LONG g_g0wDropLogged[2];

static void G0wDrop(int site, const void* ret) {
    if (!g_g0Win || GetCurrentThreadId() != g_g0wTid) return;
    InterlockedExchange(&g_g0Win, 0);  // for the rest of this pass; g_g0wIn stays (later starts count as in-pass)
    RwWake();  // [RouteAsync] WorkerWait: a parked worker starts the job this wait needs now
    InterlockedIncrement(&ts3native_counters.g0wDrop[site]);
    if (!g_g0wDropLogged[site]++) Log("loco window: dropped before a sim wait (%s), caller %p", site ? "sync worker plan" : "WaitJobDone", ret);
}

static void HxReset();  // below: an IC window left open by an unwind is closed before the pass opens its own

static void __fastcall LocoWinThunk(void* self, void* edx, float dt) {
    if (t_g0wDepth) { g_g0wOrig(self, edx, dt); return; }  // nested: the outer pass owns the window
    Ts3nCounters& k = ts3native_counters;
    HxReset();
    t_g0wDepth = 1;
    g_g0wTid = GetCurrentThreadId();
    LARGE_INTEGER t0;
    QueryPerformanceCounter(&t0);
    // window before g_g0wIn, and g_g0wIn dropped first at the end: a worker that reads g_g0wIn = 1 and then no window started
    // inside the pass for real (ServeAsync's inPass, g0wJobStartInPass exact since v85)
    if (k.g0wMode == 1) InterlockedExchange(&g_g0Win, 1);
    InterlockedExchange(&g_g0wIn, 1);
    if (g_async.state == A_RUNNING) InterlockedIncrement(&k.g0wRunningAtEntry);
    __try {
        g_g0wOrig(self, edx, dt);
    } __finally {  // also when an exception leaves the pass: never a window left open
        LARGE_INTEGER end;  // before the window opens: a released start measures from here
        QueryPerformanceCounter(&end);
        InterlockedExchange64(&g_h2PassEnd, end.QuadPart);
        InterlockedExchange(&g_g0wIn, 0);
        InterlockedExchange(&g_g0Win, 0);
        RwWake();
        t_g0wDepth = 0;
        const LONG us = UsSince(t0);
        InterlockedIncrement(&k.g0wPasses);
        InterlockedExchangeAdd(&k.g0wPassUs, us);
        Max(&k.g0wPassMaxUs, us);
    }
}

// gate wait inside the pass (routegate.cpp RgEnter, after the wait)
static void G0wGateWait(DWORD key, LONG us) {
    if (!g_g0wIn || GetCurrentThreadId() != g_g0wTid) return;
    Ts3nCounters& k = ts3native_counters;
    InterlockedIncrement(&k.g0wGateWaits);
    if (key == TS3A(0x0062EF7A)) { InterlockedIncrement(&k.g0wGate0Waits); InterlockedExchangeAdd(&k.g0wGate0Us, us); }
}

// v85 (docs/149): +0x918 waits of the sim thread inside the pass, by whether a job was running when the wait began
static bool H2InPass() { return g_g0wIn && GetCurrentThreadId() == g_g0wTid; }
static void H2LockWait(bool running, LONG us) {
    Ts3nCounters& k = ts3native_counters;
    InterlockedIncrement(&k.h2PassLockN[running ? 0 : 1]);
    InterlockedExchangeAdd(&k.h2PassLockUs[running ? 0 : 1], us);
}

// H2 IC window (docs/177 §4, counters v102 hx*, [RouteAsync] H2Ic=1, switch hxMode): the script ICs below run RouteFollow_A
// (or EditPath via 0x85FF30) on the sim thread, which bump and drop footprint refcounts [fp+4] with plain read-modify-writes
// after their obstacle query has released +0x918; a job that starts in that gap does the same writes on footprints of the same
// quadtree (goal validation, its query copy, EditPath). The window is H2's: g_g0Win = 1 before the IC body, so the worker's
// ServeAsync holds a queued start until the IC returns (Dekker with A_RUNNING as in the pass). A job already running at entry
// holds +0x918 for its whole body, so the IC's first acquire waits for its end (hxRunningAtEntry), except when it lends the
// lock: with TailUnlock / P5b the IC's query runs in the lend and the job's tail (EditPath) still overlaps the IC's writes
// (p5-lock-model h2x-tu: not closed here, docs/177 §6). Sim waits for a held start drop the window (G0wDrop, same tid), and the
// 250 ms watchdog lets a start go if one is missed. Entered through the IC binding slots (table {fn, name}, Steam 1.67) [verified
// bytes]; the return slot is swapped for HxLeaveThunk (per-thread stack of 8; frames an exception unwound are dropped at the
// next entry or the next locomotion pass, hxStale).
// shortcut: a frame left by an unwind keeps the window up until the next IC entry, pass or the 250 ms watchdog; add a sim
// tick reset if hxStale shows up in runs.
struct HxIc { DWORD slot, fn; const char* name; };
static const HxIc kHxIc[7] = {
    {TS3A(0x01160900), TS3A(0x0074C820), "Route_GetDestinationObstruction"},
    {TS3A(0x01160908), TS3A(0x0074C8E0), "Route_GetObstructionAtPoint"},
    {TS3A(0x011608C8), TS3A(0x00751480), "Route_IsPointObstructedBySim"},
    {TS3A(0x011608D0), TS3A(0x00750430), "Route_FindObstaclesForSimAlongRoute"},
    {TS3A(0x01160B88), TS3A(0x00755600), "Route_SetAGS"},
    {TS3A(0x01160988), TS3A(0x00758BA0), "Route_InsertRouteSubPathsAtIndex"},
    {TS3A(0x01160990), TS3A(0x007623A0), "Route_InsertCustomPathAtIndex"},
};
static void* g_hxOrig[7];
struct HxThread { int n; DWORD* slot[8]; DWORD ret[8]; };
static __declspec(thread) HxThread t_hx;
static void __cdecl HxLeaveThunk();

static void HxClose() {  // the outermost IC frame is gone: the window down (unless the pass owns it)
    InterlockedExchange(&g_hxOpen, 0);
    if (!t_g0wDepth) InterlockedExchange(&g_g0Win, 0), RwWake();
}
static void HxReset() {  // drops only frames below this stack pointer (dead for sure); a live IC frame always sits above it
    HxThread& t = t_hx;
    const DWORD* sp = (const DWORD*)_AddressOfReturnAddress();
    if (!t.n || t.slot[t.n - 1] >= sp) return;
    while (t.n && t.slot[t.n - 1] < sp) { t.n--; InterlockedIncrement(&ts3native_counters.hxStale); }
    if (!t.n) HxClose();
}

static void __cdecl HxEnter(int i, DWORD* slot) {
    Ts3nCounters& k = ts3native_counters;
    const DWORD tid = GetCurrentThreadId();
    if (!RgIsSim(tid)) { InterlockedIncrement(&k.hxOffSim); return; }
    InterlockedIncrement(&k.hxCalls[i]);
    HxThread& t = t_hx;
    while (t.n && t.slot[t.n - 1] <= slot) {  // left by an unwind (a live caller frame sits above this slot)
        t.n--;
        InterlockedIncrement(&k.hxStale);
        if (!t.n) HxClose();
    }
    if (k.hxMode != 1 || t_g0wDepth || t.n == 8) return;  // off, inside the pass (its window covers it) or too deep
    if (!t.n) {
        g_g0wTid = tid;  // G0wDrop: this thread's waits for a held start drop the window
        InterlockedExchange(&g_hxOpen, 1);
        InterlockedExchange(&g_g0Win, 1);  // before the IC's first read of the job state (its +0x918 acquire)
        if (g_async.state == A_RUNNING) InterlockedIncrement(&k.hxRunningAtEntry);
    }
    t.slot[t.n] = slot;
    t.ret[t.n++] = *slot;
    *slot = (DWORD)(uintptr_t)HxLeaveThunk;
}
// at = the leave thunk's entry esp - 4 (slot + N for a callee-cleanup ret N): the frame found from the outermost, as RjpPop
static DWORD __cdecl HxLeave(DWORD* at) {
    HxThread& t = t_hx;
    int i = 0;
    while (i < t.n - 1 && t.slot[i] > at) i++;
    const DWORD r = t.ret[i];
    if (t.n - 1 > i) InterlockedExchangeAdd(&ts3native_counters.hxStale, t.n - 1 - i);
    t.n = i;
    if (!t.n) HxClose();
    return r;
}
static __declspec(naked) void __cdecl HxLeaveThunk() {
    __asm {
        push eax  // becomes the return address
        pushad
        pushfd
        lea eax, [esp + 36]
        push eax
        call HxLeave
        add esp, 4
        mov [esp + 36], eax
        popfd
        popad
        ret
    }
}
#define HX_IC(i) static __declspec(naked) void HxIc##i() { __asm pushad __asm pushfd __asm lea eax, [esp + 36] \
    __asm push eax __asm push i __asm call HxEnter __asm add esp, 8 __asm popfd __asm popad __asm jmp dword ptr [g_hxOrig + i * 4] }
HX_IC(0) HX_IC(1) HX_IC(2) HX_IC(3) HX_IC(4) HX_IC(5) HX_IC(6)
static void (*const kHxShims[7])() = {HxIc0, HxIc1, HxIc2, HxIc3, HxIc4, HxIc5, HxIc6};

// one slot: the game's fn expected (another owner or another exe: left alone, logged); `slot` lets the selftest use its own table
static bool HxSwap(int i, DWORD* slot) {
    const DWORD cur = Readable(slot, 4) ? *slot : 0;
    if (cur != kHxIc[i].fn) {
        Log("SKIP H2 IC window %s: slot 0x%08lX holds 0x%08lX, expected 0x%08lX", kHxIc[i].name, (DWORD)(uintptr_t)slot, cur, kHxIc[i].fn);
        return false;
    }
    g_hxOrig[i] = (void*)(uintptr_t)cur;
    const DWORD shim = (DWORD)(uintptr_t)kHxShims[i];
    return Write(slot, &shim, 4);
}

// [RouteAsync] H2Ic=1 installs (needs the async path with [RouteGate] Enforce=1, as H2); H2IcMode = hxMode's start (default 1)
static void InstallH2Ic(const wchar_t* ini) {
    Ts3nCounters& k = ts3native_counters;
    if (!GetPrivateProfileIntW(L"RouteAsync", L"H2Ic", 0, ini)) { Log("H2 IC window off ([RouteAsync] H2Ic=0)"); return; }
    if (!k.rgEnforce || !k.asyncReady) { Log("SKIP H2 IC window: needs [RouteGate] Enforce=1 with the async path"); return; }
    for (int i = 0; i < 7; i++) if (HxSwap(i, (DWORD*)(uintptr_t)kHxIc[i].slot)) k.hxReady++;
    k.hxMode = k.hxReady ? Clamp(GetPrivateProfileIntW(L"RouteAsync", L"H2IcMode", 1, ini), 0, 1) : 0;
    Log("%s H2 IC window: %ld of 7 IC slots (Mode=%ld: %s)", k.hxReady == 7 ? "OK  " : "PART", k.hxReady, k.hxMode,
        k.hxMode ? "no job starts while one runs on the sim thread" : "count only");
}

// [LocoWindow] Enabled=1, or [RouteAsync] H2=1 (docs/149: same thunk, Mode 1): the call site bytes are checked first
// (refused and logged when they differ)
static void InstallLocoWindow(const wchar_t* ini) {
    Ts3nCounters& k = ts3native_counters;
    const bool h2 = GetPrivateProfileIntW(L"RouteAsync", L"H2", 0, ini) != 0;
    if (!h2 && !GetPrivateProfileIntW(L"LocoWindow", L"Enabled", 0, ini)) { Log("loco window off ([RouteAsync] H2=0, [LocoWindow] Enabled=0)"); return; }
    if (!k.rgEnforce || !k.asyncReady) { Log("SKIP loco window: needs [RouteGate] Enforce=1 with the async path"); return; }
    if (!Matches((const void*)(uintptr_t)(LOCO_CALL - 8), kLocoCall, sizeof(kLocoCall))) {
        Log("SKIP loco window: bytes at 0x%08X differ (not the Locomotion_Update call of Steam 1.67), nothing written", LOCO_CALL - 8);
        return;
    }
    k.g0wMode = h2 ? 1 : Clamp(GetPrivateProfileIntW(L"LocoWindow", L"Mode", 1, ini), 0, 1);
    const CallSite site = {LOCO_CALL, LOCO_UPDATE, (void*)LocoWinThunk};
    if (!PatchCalls("loco window", &site, 1)) return;
    k.g0wReady = 1;
    Log("OK   loco window: call 0x%08X -> LocoWinThunk (Mode=%ld: %s)", LOCO_CALL, k.g0wMode,
        k.g0wMode ? "no job starts during the pass" : "count only");
}
