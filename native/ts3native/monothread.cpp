// Second managed thread, step 1 (docs/30 §1 P1, §2 P2) plus the M5 build (§14). Included by ts3native.cpp (one translation unit).
//
// Boehm's thread table 0x1221000 (256 entries of 0x14: +0 in use, +4 thread id, +8 handle, +0xC stack base,
// +0x10 suspended; highest index [0x11F4124]) gets entries only from GC_new_thread 0xE51070, which registers the
// calling thread. GC_stop_world 0xE511A0 sets [0x11F4120] = 1 and suspends every entry with a stack base and another
// thread id; but the root scan only pushes the collector's own registers and stack: GC_push_roots 0xE4D910 ends with
// `jmp [0x11949CC]`, which holds 0xE4EF90 (`jmp 0xE51350`, `jmp 0xD71820`: EA's push of the current stack) [verified].
//
// P2: [0x11949CC] -> MtPushOtherRoots: the old hook first, then, only while the world is stopped, every other
// suspended entry's registers (GC_push_one 0xE4D520) and stack [ESP, base) (GC_push_all_stack 0xE4CEE0, hi exclusive).
// With only the collector registered, the loop finds nothing and the scan is the game's.
// P1: at the first MonoScriptHost_Simulate tick (vtable slot 0x108B6B0) the sim thread logs the table and registers
// itself with 0xE51070 if it is missing. P3, P4 and the worker (M5 build) are in monothread-m5.cpp.

static const DWORD MT_PUSH_HOOK = 0x011949CC, MT_PUSH_THUNK = 0x00E4EF90, MT_PUSH_THUNK2 = 0x00E51350, MT_PUSH_EA = 0x00D71820;
static const DWORD MT_NEW_THREAD = 0x00E51070, MT_PUSH_ONE = 0x00E4D520, MT_PUSH_STACK = 0x00E4CEE0;
static const DWORD MT_SIM_SLOT = 0x0108B6B0, MT_SIMULATE = 0x00D81840;
static const u8 kMtNewThread[16] = {0x56, 0x57, 0x8B, 0x3D, 0x34, 0x52, 0xF9, 0x00, 0x6A, 0x01, 0x68, 0x00, 0x10, 0x22, 0x01, 0x33};
static const u8 kMtPushOne[10] = {0x8B, 0x44, 0x24, 0x04, 0x3B, 0x05, 0x24, 0x48, 0x19, 0x01};  // mov eax,[esp+4]; cmp eax,[0x1194824]
static const u8 kMtPushStack[7] = {0x83, 0x3D, 0xB4, 0x49, 0x19, 0x01, 0x00};                // cmp [0x11949B4],0
static const u8 kMtPushEa[6] = {0x83, 0xEC, 0x20, 0x56, 0x6A, 0x1C};                         // sub esp,0x20; push esi; push 0x1C
static const u8 kMtThunk[5] = {0xE9, 0xBB, 0x23, 0x00, 0x00};   // 0xE4EF90: jmp 0xE51350
static const u8 kMtThunk2[5] = {0xE9, 0xCB, 0x04, 0xF2, 0xFF};  // 0xE51350: jmp 0xD71820
static const u8 kMtPushTail[11] = {0xA1, 0xCC, 0x49, 0x19, 0x01, 0x85, 0xC0, 0x74, 0x02, 0xFF, 0xE0};  // 0xE4D9DC

typedef void(__cdecl* MtVoidFn)();
typedef void(__cdecl* MtPushOneFn)(DWORD p);
typedef void(__cdecl* MtPushStackFn)(DWORD lo, DWORD hi);
typedef void*(__cdecl* MtNewThreadFn)();
// game addresses; the selftest points them at fakes
static MtVoidFn g_mtOrigPush;
static MtPushOneFn g_mtPushOne = (MtPushOneFn)(uintptr_t)MT_PUSH_ONE;
static MtPushStackFn g_mtPushStack = (MtPushStackFn)(uintptr_t)MT_PUSH_STACK;
static MtNewThreadFn g_mtNewThread = (MtNewThreadFn)(uintptr_t)MT_NEW_THREAD;
static DWORD* g_mtTable = (DWORD*)(uintptr_t)0x01221000;               // 5 DWORDs per entry
static volatile LONG* g_mtMaxIdx = (volatile LONG*)(uintptr_t)0x011F4124;
static volatile LONG* g_mtStopped = (volatile LONG*)(uintptr_t)0x011F4120;
static __declspec(align(16)) CONTEXT g_mtCtx;  // only used with the world stopped, by the one collecting thread
static void* g_mtSimOrig;
static volatile LONG g_mtTicked;
static DWORD g_mtSimTid;
// [AutShadow] part 2 (docs/122 §14) rides this slot stub (one owner, docs/122 §12.4): top of Simulate and its return
static void(__cdecl* g_mtOnEnter)();
static void(__cdecl* g_mtOnLeave)();
static DWORD g_mtSimRet;     // the caller's return address while a hooked Simulate runs (sim thread only)
static DWORD g_mtSimRetThunk;
static void(__cdecl* g_mtPushExtra)();  // [AutShadow] Satb: the log's old values and words (satb.cpp), world stopped

// GC root hook. Runs inside the mark with other threads suspended (one may hold the CRT heap or a game lock):
// no allocation, no Log, no lock, no CRT call.
static void __cdecl MtPushOtherRoots() {
    g_mtOrigPush();
    if (*g_mtStopped != 1) return;  // incremental step with the world running: the stopped mark rescans (docs/30 §12.6)
    Ts3nCounters& k = ts3native_counters;
    k.mtPushCalls++;
    const DWORD me = GetCurrentThreadId();
    LONG n = *g_mtMaxIdx;
    if (n > 255) n = 255;
    for (LONG i = 0; i <= n; i++) {
        const DWORD* e = g_mtTable + i * 5;
        const DWORD base = e[3];
        if (!base || e[4] != 1 || e[1] == me) continue;  // same test as GC_stop_world/GC_start_world, plus "suspended"
        DWORD lo = 0;
        g_mtCtx.ContextFlags = CONTEXT_INTEGER | CONTEXT_CONTROL;  // also completes the asynchronous SuspendThread
        // an ESP outside (base - 64 MB, base) is not on this stack (no game thread reserves more): treat as a failure
        if (GetThreadContext((HANDLE)(uintptr_t)e[2], &g_mtCtx) && g_mtCtx.Esp < base && base - g_mtCtx.Esp < (64u << 20)) {
            g_mtPushOne(g_mtCtx.Eax); g_mtPushOne(g_mtCtx.Ebx); g_mtPushOne(g_mtCtx.Ecx); g_mtPushOne(g_mtCtx.Edx);
            g_mtPushOne(g_mtCtx.Esi); g_mtPushOne(g_mtCtx.Edi); g_mtPushOne(g_mtCtx.Ebp);
            lo = g_mtCtx.Esp;
        } else {
            // an unscanned stack frees live objects: push the whole committed stack instead (conservative, slower)
            k.mtCtxFails++;
            MEMORY_BASIC_INFORMATION mbi;
            if (VirtualQuery((const void*)(uintptr_t)(base - 4), &mbi, sizeof(mbi)) && mbi.State == MEM_COMMIT) lo = (DWORD)(uintptr_t)mbi.BaseAddress;
        }
        if (lo && lo < base) { g_mtPushStack(lo, base); k.mtStacksPushed++; }
    }
    if (g_mtPushExtra) g_mtPushExtra();
}

static int MtFind(DWORD tid) {
    LONG n = *g_mtMaxIdx;
    if (n > 255) n = 255;
    for (LONG i = 0; i <= n; i++)
        if (g_mtTable[i * 5 + 3] && g_mtTable[i * 5 + 1] == tid) return (int)i;
    return -1;
}

static void MtLogTable(const char* when) {
    LONG n = *g_mtMaxIdx, used = 0;
    if (n > 255) n = 255;
    for (LONG i = 0; i <= n; i++) {
        const DWORD* e = g_mtTable + i * 5;
        if (!e[0] && !e[3]) continue;
        used++;
        Log("mono thread: GC table %s [%ld] in use %lu tid %lu%s%s base %08X suspended %lu", when, i, e[0], e[1],
            e[1] == g_mtSimTid ? " (sim)" : "", e[1] == g_mainTid ? " (main)" : "", e[3], e[4]);
    }
    ts3native_counters.mtThreads = used;
}

#include "monothread-m5.cpp"  // P3, P4, worker entry (M5 build, docs/30 §14)

// P1: register the calling thread in Boehm's table (GC_new_thread 0xE51070 registers its caller).
// The sim thread may register at a Simulate tick boundary (outside every GC function, no other thread collects); any
// other thread only while the P3 gate is on, and then under the GC lock (the worker, docs/30 §5 step 1).
static bool MtRegisterCurrentThread() {
    const DWORD tid = GetCurrentThreadId();
    if (tid != g_mtSimTid && !ts3native_mtprobe.locking) return false;
    const bool held = MtLockC();
    bool ok = MtFind(tid) >= 0;
    if (!ok) g_mtNewThread(), ok = MtFind(tid) >= 0;
    MtUnlockC(held);
    return ok;
}

// Top of every MonoScriptHost_Simulate call, on the sim thread. The first one logs and registers; every one runs the
// M5 worker switch (one compare when nothing is installed).
static void MtFirstTick();
static void __cdecl MtTick() {
    if (!g_mtTicked) MtFirstTick();
    MtM5Tick();
    if (g_mtOnEnter) g_mtOnEnter();
}
// return of MonoScriptHost_Simulate 0xD81840 (sim thread; monohost calls it directly as its tick end)
static void __cdecl MtSimLeave() {
    if (g_mtOnLeave) g_mtOnLeave();
}
// end of every Simulate loop iteration (simloop.cpp): the frame's "return", then the next frame's "top"
static void __cdecl MtIterate() {
    MtSimLeave();
    MtTick();
}

static void MtFirstTick() {
    g_mtTicked = 1;
    Ts3nCounters& k = ts3native_counters;
    g_mtSimTid = GetCurrentThreadId();
    k.mtSimTid = (LONG)g_mtSimTid;
    MtLogTable("at first tick");
    if (MtFind(g_mtSimTid) < 0) {
        const bool ok = MtRegisterCurrentThread();
        k.mtSimRegistered = ok ? 1 : -1;
        Log("mono thread: sim thread %lu was not registered (main %lu); GC_new_thread %s", g_mtSimTid, g_mainTid, ok ? "added it" : "FAILED");
        MtLogTable("after register");
    } else {
        Log("mono thread: sim thread %lu already registered (main %lu)", g_mtSimTid, g_mainTid);
    }
    k.mtReady |= 4;
    MtM5FirstTick();
}

// The original returns here when g_mtOnLeave is set: run MtSimLeave, then back to the caller (EAX/EDX/flags kept;
// `ret N` of the original already dropped its arguments).
static __declspec(naked) void MtSimReturn() {
    __asm {
        push dword ptr [g_mtSimRet]
        mov dword ptr [g_mtSimRet], 0
        pushad
        pushfd
        call MtSimLeave
        popfd
        popad
        ret
    }
}

static DWORD g_mtHostThis;  // MonoScriptHost `this` (thiscall: ecx at the slot call), read by satb.cpp for the tick statics (+0xC20/+0xC24)
static __declspec(naked) void MtSimulateStub() {
    __asm {
        mov dword ptr [g_mtHostThis], ecx
        pushad
        pushfd
        call MtTick
        popfd
        popad
        cmp dword ptr [g_mtOnLeave], 0
        je plain
        cmp dword ptr [g_mtSimRet], 0
        jne plain  // nested Simulate: only the outer call gets the return hook
        pop dword ptr [g_mtSimRet]
        push dword ptr [g_mtSimRetThunk]
    plain:
        jmp dword ptr [g_mtSimOrig]
    }
}

// [MonoThread] Enabled=1: P2 at load (data write before any GC runs), P1 at the first Simulate tick; GcLock=1 (P3) and
// FrameTls=1 (P4) install at that tick too; Mode=1 then runs the registered worker (needs both).
static bool ApplyMonoThread(const wchar_t* ini) {
    if (!GetPrivateProfileIntW(L"MonoThread", L"Enabled", 0, ini)) { Log("mono thread disabled (Enabled=0)"); return false; }
    Ts3nCounters& k = ts3native_counters;
    k.mtMode = GetPrivateProfileIntW(L"MonoThread", L"Mode", 0, ini);
    g_mtWantP3 = GetPrivateProfileIntW(L"MonoThread", L"GcLock", 0, ini) != 0;
    g_mtWantP4 = GetPrivateProfileIntW(L"MonoThread", L"FrameTls", 0, ini) != 0;
    if (g_mtWantP4) {
        // Reserve P4's pair now (DLL load, before the exe entry): by the first Simulate tick the game holds slots 0-101,
        // and P4's fs:[0xE10 + 4*index] operands only reach indexes < 64. MtBuildP4 still skips if these are >= 64.
        g_mtSlotF = TlsAlloc();
        g_mtSlotA = TlsAlloc();
        Log("mono thread P4: TLS pair reserved at load: %lu/%lu", g_mtSlotF, g_mtSlotA);
    }
    g_mtAddRoots = GetPrivateProfileIntW(L"MonoThread", L"LockAddRoots", 1, ini) != 0;
    g_mtArenaFix = GetPrivateProfileIntW(L"MonoThread", L"ArenaFix", 1, ini) != 0;  // with GcLock=1: frame arena pool fix (docs/120 §9)
    g_mtN2mPrivate = GetPrivateProfileIntW(L"MonoThread", L"N2mPrivate", 1, ini) != 0;  // with GcLock=1: F1 detour at 0xD8A8E0 (docs/120 §9.4)
    g_mtLazyGate = GetPrivateProfileIntW(L"MonoThread", L"LazyGate", 0, ini) != 0;  // with Mode=1: gate on only while a job runs (docs/30 §15)
    g_mtLazyEarly = g_mtLazyGate && GetPrivateProfileIntW(L"MonoThread", L"LazyEarly", 0, ini) != 0;  // gate off at the first sim entry after the job (docs/30 §16)
    ts3native_mtprobe.lazyGate = g_mtLazyGate ? 1 : 0;
    if (k.mtMode >= 2) { Log("mono thread: Mode=%ld (GC stress) not built yet, running Mode 1", k.mtMode); k.mtMode = 1; }
    if (k.mtMode == 1 && !(g_mtWantP3 && g_mtWantP4)) { Log("mono thread: Mode=1 needs GcLock=1 and FrameTls=1, running Mode 0"); k.mtMode = 0; }
    if (k.mtMode < 0) k.mtMode = 0;
    DWORD* hook = (DWORD*)(uintptr_t)MT_PUSH_HOOK;
    DWORD* slot = (DWORD*)(uintptr_t)MT_SIM_SLOT;
    if (!Readable(hook, 4) || !Readable(slot, 4) || !Readable(g_mtTable, 256 * 0x14)) { Log("SKIP mono thread: GC globals unreadable"); return false; }
    const bool viaThunk = *hook == MT_PUSH_THUNK && Matches((void*)(uintptr_t)MT_PUSH_THUNK, kMtThunk, 5) &&
                          Matches((void*)(uintptr_t)MT_PUSH_THUNK2, kMtThunk2, 5);
    if (!(viaThunk || *hook == MT_PUSH_EA) || !Matches((void*)(uintptr_t)MT_PUSH_EA, kMtPushEa, sizeof(kMtPushEa)) ||
        !Matches((void*)(uintptr_t)0x00E4D9DC, kMtPushTail, sizeof(kMtPushTail)) ||
        !Matches((void*)(uintptr_t)MT_PUSH_ONE, kMtPushOne, sizeof(kMtPushOne)) ||
        !Matches((void*)(uintptr_t)MT_PUSH_STACK, kMtPushStack, sizeof(kMtPushStack)) ||
        !Matches((void*)(uintptr_t)MT_NEW_THREAD, kMtNewThread, sizeof(kMtNewThread)) || *slot != MT_SIMULATE) {
        Log("SKIP mono thread: [0x%08X]=0x%08X or [0x%08X]=0x%08X or GC code bytes differ, nothing written", MT_PUSH_HOOK, *hook,
            MT_SIM_SLOT, *slot);
        return false;
    }
    g_mtOrigPush = (MtVoidFn)(uintptr_t)*hook;
    g_mtSimOrig = (void*)(uintptr_t)*slot;
    g_mtSimRetThunk = (DWORD)(uintptr_t)MtSimReturn;
    const DWORD push = (DWORD)(uintptr_t)MtPushOtherRoots, stub = (DWORD)(uintptr_t)MtSimulateStub;
    if (!Write(hook, &push, 4)) { Log("FAIL mono thread: write [0x%08X] (err %lu)", MT_PUSH_HOOK, GetLastError()); return false; }
    k.mtReady = 1;
    if (Write(slot, &stub, 4)) {
        k.mtReady |= 2;
        // the slot runs once per session (Simulate loops, simloop.cpp): per-frame ticks come from the loop-back hook
        if (SimLoopAdd(MtIterate, "mono thread tick")) k.mtReady |= 8;
    } else Log("FAIL mono thread: Simulate vtable slot (err %lu); P2 stays, no sim registration", GetLastError());
    Log("OK   mono thread: [0x%08X] 0x%08X -> MtPushOtherRoots, Simulate slot 0x%08X %s (Mode=%ld, GcLock=%d, FrameTls=%d)", MT_PUSH_HOOK,
        (DWORD)(uintptr_t)g_mtOrigPush, MT_SIM_SLOT, (k.mtReady & 2) ? "hooked" : "not hooked", k.mtMode, g_mtWantP3, g_mtWantP4);
    return true;
}
