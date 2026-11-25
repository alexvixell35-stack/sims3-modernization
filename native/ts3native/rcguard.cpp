// [RouteAsync] RefcountGuard (docs/177 §7, counters v104 rc*): no footprint refcount write of the sim thread runs beside a
// route job. Included by ts3native.cpp after goalgen.cpp (uses routegate.cpp's frame keys and LockReleaseFn, rtsites.cpp's
// RT_STEER key t_steerRet, avoidro.cpp's A-site frame pointer).
//
// The race (docs/177 §4, §7.1): the obstacle query 0x82EDB0 and the goal footprint gather 0x82F0B0 copy their hits into the
// caller's vector under +0x918 (each hit +1 on [fp+4], 0x82D061 / 0x82F38F / 0xAAE781) and release +0x918 before they return
// (0x82F098 / 0x82F45C, the only releases after a non-empty copy; the early releases 0x82EE53 / 0x82F191 leave the vector
// empty). Their 12 callers then bump and drop [fp+4] with plain read-modify-writes and release the vector (0xAAC090, inline
// -1 and delete at 0) without any lock. On the sim thread that is outside +0x918, while a job that starts right after the
// release (docs/86) writes the same fields under its hold (goal validation, its own query copies, EditPath, flushes).
// The guard: on the sim thread only, the release at 0x82F098 / 0x82F45C is kept (one +0x918 level more) when the vector
// holds hits and the caller is one of the 12 [verified call sites, table below]; the level is released when that caller
// returns (its return slot is swapped for RcLeaveThunk, as HxEnter does). All 12 callers keep an ebp frame and never write
// ebp after the prologue [verified, capstone], so the caller's frame is the query's saved ebp ([ebp+0] inside 0x82EDB0 /
// 0x82F0B0, both `push ebp; mov ebp,esp`); the caller is told by t_steerRet (rtsites.cpp RT_STEER, the real return address
// even behind other owners' leave thunks; the AvoidRO A-site keys 0x846D21 and gives RouteFollow_A's ebp via g_avRfEbp).
// So every sim-side [fp+4] write of those callers happens while the sim holds +0x918: the worker cannot be in a job body
// then (it holds +0x918 for the whole job), and a job that lent the lock to the sim (TailUnlock passes 16-19, P5b) gets it
// back only after the caller's writes. Worker-side writes need no change (all inside the job's hold).
// Deadlock rule: the sim never waits for the worker while it keeps a level: every sim wait on a job (WaitAsyncDone,
// WaitJobDone, RunPlan's worker plan, RtWaitJob, P4NrEnter) calls RcDrop first (all levels released, rcDropped[0]).
// Frames an exception unwound are released at the next RcRel / RcDrop / locomotion pass (rcDropped[1]).
// shortcut: callers outside the table (ts3native shadows and clones that call the queries themselves) pass through
// (rcPass[1]); they write no footprint refcount except [SteerRO] / [AvoidRO] shadows, which are off in every queue.
static const DWORD RC_REL_SITES[2] = {TS3A(0x0082F098), TS3A(0x0082F45C)}, RC_LOCK_RELEASE = TS3A(0x004E17B0);
struct RcCaller { DWORD ret; int fn; };
// return address after each call of 0x82EDB0 / 0x82F0B0 -> rcByFn index: 0 RouteObstructionAtPoint 0x655880,
// 1 IC_Route_IsPointObstructedBySim 0x751480, 2 IC_RouteManager_IsOkToIdleAtCurrentLocation 0x7556B0, 3 0x755EA0,
// 4 GoalPoint_Validate 0x759B20, 5 Route_GoalStep 0x75C2D0, 6 0x8243E0, 7 route-to-object 0x830340, 8 RouteFollow_A 0x846B70,
// 9 RouteFollow_B 0x85B470, 10 RouteFollow_EditPath 0x85F5A0, 11 Locomotion_ObstacleCheck 0x85FD10
static const RcCaller kRcCallers[15] = {
    {TS3A(0x00655AFB), 0}, {TS3A(0x007516B8), 1}, {TS3A(0x00755990), 2}, {TS3A(0x00755FDD), 3}, {TS3A(0x0075A64C), 4}, {TS3A(0x0075AA16), 4}, {TS3A(0x0075CC3B), 5},
    {TS3A(0x0075CFE5), 5}, {TS3A(0x00824497), 6}, {TS3A(0x008303EA), 7}, {TS3A(0x00846D21), 8}, {TS3A(0x0085B50C), 9}, {TS3A(0x0085F6CB), 10}, {TS3A(0x0085FE10), 11},
    {TS3A(0x0085FE81), 11}};
static void* g_rcRelOrig = (void*)(uintptr_t)RC_LOCK_RELEASE;                   // the call sites' target (route gate detour)
static LockReleaseFn g_rcRel = (LockReleaseFn)(uintptr_t)RC_LOCK_RELEASE;      // releases a kept level; selftest: a fake
static DWORD g_rcTestRet;  // selftest: one more caller key (fn 0)

struct RcFrame { DWORD* slot; DWORD ret; void* lock; LONG owed; LARGE_INTEGER t0; };
struct RcThread { int n; RcFrame f[8]; };  // outermost first (highest slot)
static __declspec(thread) RcThread t_rc;
static void __cdecl RcLeaveThunk();

static void RcRelease(RcFrame& f, int why) {  // why: -1 the caller returned, 0 before a wait, 1 stale
    if (!f.owed) return;
    CeRec(CE_RC_REL, (DWORD)(uintptr_t)f.lock, (DWORD)f.owed, (DWORD)why);  // docs/186 §8
    Ts3nCounters& k = ts3native_counters;
    if (why >= 0) InterlockedExchangeAdd(&k.rcDropped[why], f.owed);
    for (; f.owed; f.owed--) g_rcRel(f.lock, NULL);
    const LONG us = UsSince(f.t0);
    InterlockedExchangeAdd(&k.rcHoldUs, us);
    Max(&k.rcHoldMaxUs, us);
}
// frames an exception unwound: below this stack pointer, or their slot no longer holds our thunk (a newer call wrote its own
// return address there); a live caller frame sits above and keeps RcLeaveThunk in its slot until it returns
static void RcStale() {
    RcThread& t = t_rc;
    const DWORD* sp = (const DWORD*)_AddressOfReturnAddress();
    while (t.n && (t.f[t.n - 1].slot < sp || *t.f[t.n - 1].slot != (DWORD)(uintptr_t)RcLeaveThunk)) RcRelease(t.f[--t.n], 1);
}
// every sim wait for the worker: no kept level across it (the job may need +0x918 to end)
static void RcDrop() {
    RcThread& t = t_rc;
    if (!t.n) return;
    RcStale();
    for (int i = 0; i < t.n; i++) RcRelease(t.f[i], 0);
}

// the release call inside 0x82EDB0 / 0x82F0B0: 1 = keep the level (the thunk returns without releasing)
static int __cdecl RcRel(const DWORD* ebpQ, void* lock) {
    Ts3nCounters& k = ts3native_counters;
    if (!RgIsSim(GetCurrentThreadId())) { InterlockedIncrement(&k.rcOffSim); return 0; }
    RcStale();
    const DWORD* vec = (const DWORD*)(uintptr_t)ebpQ[4];  // [ebp+0x10]: the caller's hit vector (begin, end)
    if (!vec || vec[0] == vec[1]) { InterlockedIncrement(&k.rcPass[0]); return 0; }
    int fn = g_rcTestRet && t_steerRet == g_rcTestRet ? 0 : -1;
    for (int i = 0; i < 15 && fn < 0; i++) if (kRcCallers[i].ret == t_steerRet) fn = kRcCallers[i].fn;
    // t_stubKey: a call-site stub (GvStub, IcStub) runs this query for the listed caller and keys it by that caller's site,
    // but its ebp is no frame (the stubs omit frame pointers): passed through like the other ts3native callers. Without this
    // the leave thunk went into IcStub's ebp + 4, the IC's vector end (2026-11-04, AV in the IC loop at 0x7559AC)
    if (fn < 0 || lock != g_rgLock || t_stubKey) { InterlockedIncrement(&k.rcPass[1]); return 0; }
    if (g_inInvoke || t_borrow) { InterlockedIncrement(&k.rcPass[2]); return 0; }  // the worker waits in a callback for us
    DWORD* const cebp = t_avSite ? g_avRfEbp : (DWORD*)(uintptr_t)ebpQ[0];
    if ((uintptr_t)cebp <= (uintptr_t)ebpQ || (uintptr_t)cebp - (uintptr_t)ebpQ > 0x40000) { InterlockedIncrement(&k.rcPass[4]); return 0; }
    if (k.rcMode != 1) { InterlockedIncrement(&k.rcWould); return 0; }
    DWORD* const slot = cebp + 1;
    RcThread& t = t_rc;
    int i = t.n - 1;
    while (i >= 0 && t.f[i].slot != slot) i--;
    if (i < 0) {
        if (t.n == 8) { InterlockedIncrement(&k.rcPass[3]); return 0; }
        RcFrame& f = t.f[i = t.n++];
        f.slot = slot;
        f.ret = *slot;
        f.lock = lock;
        f.owed = 0;
        QueryPerformanceCounter(&f.t0);
        *slot = (DWORD)(uintptr_t)RcLeaveThunk;
        InterlockedIncrement(&k.rcFrames);
        if (AsyncBusy()) InterlockedIncrement(&k.rcBusyAtOpen);
    }
    t.f[i].owed++;
    CeRec(CE_RC_HOLD, (DWORD)(uintptr_t)lock, (DWORD)(uintptr_t)slot, (DWORD)t.f[i].owed);  // docs/186 §8
    InterlockedIncrement(&k.rcDeferred);
    InterlockedIncrement(&k.rcByFn[fn]);
    return 1;
}
// at = the leave thunk's entry esp - 4 (slot + N for a callee-cleanup ret N): the frame found from the outermost, as HxLeave
static DWORD __cdecl RcLeave(DWORD* at) {
    RcThread& t = t_rc;
    int i = 0;
    while (i < t.n - 1 && t.f[i].slot > at) i++;
    while (t.n - 1 > i) RcRelease(t.f[--t.n], 1);  // deeper frames left by an unwind
    InterlockedIncrement(&ts3native_counters.rcLeaves);
    RcRelease(t.f[i], -1);
    t.n = i;
    return t.f[i].ret;
}
static __declspec(naked) void __cdecl RcLeaveThunk() {
    __asm {
        push eax  // becomes the return address
        pushad
        pushfd
        lea eax, [esp + 36]
        push eax
        call RcLeave
        add esp, 4
        mov [esp + 36], eax
        popfd
        popad
        ret
    }
}
// the retargeted `call 0x4E17B0` (thiscall, ecx = the lock, no arguments, returns the new count; the queries then only set al)
static __declspec(naked) void RcRelThunk() {
    __asm {
        pushad
        pushfd
        push ecx  // the lock
        push ebp  // the query's frame
        call RcRel
        add esp, 8
        test eax, eax
        jnz keep
        popfd
        popad
        jmp dword ptr [g_rcRelOrig]
    keep:
        popfd
        popad
        mov eax, [ecx + 0x20]  // the game's count; Lock_Release would return one less
        dec eax
        ret
    }
}

// [RouteAsync] RefcountGuard=1 installs (needs the async path with [RouteGate] Enforce=1 and its steering frames);
// RefcountGuardMode = rcMode's start (default 1; 0 counts the releases it would keep, rcWould)
static void InstallRefcountGuard(const wchar_t* ini) {
    Ts3nCounters& k = ts3native_counters;
    if (!GetPrivateProfileIntW(L"RouteAsync", L"RefcountGuard", 0, ini)) { Log("refcount guard off ([RouteAsync] RefcountGuard=0)"); return; }
    const LONG steer = 1 << 23 | 1 << 24;  // rtsites.cpp: the two RT_STEER sites (t_steerRet)
    if (!k.rgEnforce || !k.asyncReady || !g_rgRelOrig || (k.rtReady & steer) != steer) {
        Log("SKIP refcount guard: needs [RouteGate] Enabled=1 Sites=1 Enforce=1 (steering frames) with the async path");
        return;
    }
    const CallSite sites[2] = {{RC_REL_SITES[0], RC_LOCK_RELEASE, (void*)RcRelThunk}, {RC_REL_SITES[1], RC_LOCK_RELEASE, (void*)RcRelThunk}};
    if (!PatchCalls("refcount guard (query releases)", sites, 2)) return;
    k.rcReady = 1;
    k.rcMode = Clamp(GetPrivateProfileIntW(L"RouteAsync", L"RefcountGuardMode", 1, ini), 0, 1);
    Log("OK   refcount guard: 0x82F098 / 0x82F45C -> RcRelThunk (Mode=%ld: %s)", k.rcMode,
        k.rcMode ? "the sim keeps +0x918 until the query's caller returns" : "count only");
}
