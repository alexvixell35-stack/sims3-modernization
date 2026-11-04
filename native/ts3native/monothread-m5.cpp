// Second managed thread, M5 build (docs/30 §3 P3, §4 P4, §5 worker entry; site list docs/120 §4). Included by
// monothread.cpp (one translation unit with ts3native.cpp). Everything here is installed only with [MonoThread]
// Enabled=1 plus its own key (GcLock=1, FrameTls=1), at the first MonoScriptHost_Simulate tick (sim thread, outside
// every GC and interpreter function), and does nothing until the gate `ts3native_mtprobe.locking` is set.
//
// P3, gated re-entrant GC lock: 17 detours (docs/57 §7, docs/120 §4) whose stub runs the original directly while the
// gate is 0; with the gate set it takes g_mtLock, swaps the caller's return address for MtLockExitThunk (kept on a
// shadow stack that the lock itself guards, so any calling convention and any nesting works) and runs the original.
// Plus GC_invoke_finalizers 0xE4B3F0 replaced whole (pop under the lock, run the finalizer outside it) and
// GC_add_current_malloc_heap 0xE4EC40 patched to `ret` (CRT malloc inside a stopped-world root push, docs/120 §2.2).
// P4: the frame globals [0x11F4170]/[0x11F416C] of ves_exec_method_with_context become two TLS slots (fs: operands in
// three relocated blocks), and the frame reader 0xE534E0 is replaced by MtFrameAt.
// Worker scaffolding: a native thread registered with Boehm under the lock (P1), domain TLS set, interpreter context
// TLS left NULL (0xE5A600 builds it on the worker's stack), jobs handed over natively; no managed job exists yet
// (ts3fixes MtProbe is a later step), so Mode=1 runs an idle registered worker.

// Counters (separate export so the routing lane's Ts3nCounters versions stay untouched; docs/registry.md "ts3native_mtprobe").
struct Ts3nMtProbe {
    LONG version;            // +0x00, 1
    LONG p3Ready;            // +0x04, bit 0 lock detours, 1 0xE4B3F0 replaced, 2 0xE4EC40 ret, 3 GC_add_roots left out (LockAddRoots=0), 4 arena fix, 5 GC_oom_fn wrapped
    LONG p3Sites;            // +0x08, sites written (19 with everything)
    volatile LONG locking;   // +0x0C, the gate: 1 = hooks take the lock (set by the sim thread at a tick boundary)
    LONG lockedCalls;        // +0x10, hook entries that took the lock (any thread)
    LONG lockedWorker;       // +0x14, of those, on the worker
    LONG lockWaits;          // +0x18, entries that found the lock taken
    LONG lockWaitUs;         // +0x1C, their wait time (us)
    LONG lockMaxWaitUs;      // +0x20, longest wait
    LONG lockMaxDepth;       // +0x24, deepest hook nesting (shadow stack)
    LONG lockOverflow;       // +0x28, tripwire: nesting past the shadow stack (that call ran without the lock)
    LONG finRuns;            // +0x2C, finalizers run by the 0xE4B3F0 replacement
    LONG p4Ready;            // +0x30, bit 0 entry block, 1 exit A, 2 exit B, 3 0xE534E0 replaced
    LONG p4Slots;            // +0x34, TLS indexes: frame | array << 8
    volatile LONG workerState;  // +0x38, 0 none, 1 starting, 2 idle, 3 in a job, 4 stopped, -1 register failed, -2 start failed
    LONG workerTid;          // +0x3C
    LONG workerStarts;       // +0x40
    LONG workerJobs;         // +0x44, jobs run
    LONG workerRunUs;        // +0x48, their time (us)
    LONG workerMaxRunUs;     // +0x4C
    LONG workerExceptions;   // +0x50, managed exceptions returned by the invoke
    LONG workerTrackSkips;   // +0x54, jobs refused: EA memory tracking [0x11DC18C] != 0 (docs/120 §2.2)
    LONG workerStopFails;    // +0x58, stop requests that timed out (the gate stays on)
    LONG workerCollects;     // +0x5C, collection entries (0xE4A050, 0xE4A0B0) on the worker
    LONG workerCtxSet;       // +0x60, tripwire: interpreter context TLS was not NULL on the worker at start
    volatile LONG seq;       // +0x64, finished jobs (the sim thread polls it)
    volatile LONG result;    // +0x68, last job's return value (MonoObject*, read only as a number)
    // v2: EA frame arena pool fix (ArenaFix=1 with GcLock=1, docs/120 §9)
    LONG arenaRelNonEmpty;   // +0x6C, 0xD71B70 release-all calls that found a non-empty chain (+0x13C now cleared after them)
    LONG arenaDropped;       // +0x70, pool nodes dropped: duplicate chunks at install, or a pooled chunk found in use (top != base)
    // v2: F1, worker-private call records for ScriptHost_NativeToManaged 0xD8A8E0 (N2mPrivate=1, docs/120 §9.4)
    LONG n2mReady;           // +0x74, 1 = detour at 0xD8A8E0 installed
    LONG n2mWorker;          // +0x78, entries on the worker (private records)
    LONG n2mMaxDepth;        // +0x7C, deepest private record nesting
    LONG n2mOverflow;        // +0x80, tripwire: nesting past the private records (that call took the game's shared chain)
    // v3: stale shadow entries (docs/120 §10, I-120-8)
    LONG lockStale;          // +0x84, locked hook frames left without their exit (longjmp/unwind), dropped and their lock entry released
    // v4: GC_oom_fn wrapper (docs/120 §10.5)
    LONG lockOomReleased;    // +0x88, shadow entries (and lock recursions) the OOM thread released before calling 0xE78610
    // v5: lazy gate (LazyGate=1, docs/30 §15): the gate is on only from a job's dispatch until the sim sees it finished
    LONG lazyGate;           // +0x8C, 1 = LazyGate=1 in effect
    LONG lazyOn;             // +0x90, gate switched on by MtSubmit (sim thread, tick boundary)
    LONG lazyOff;            // +0x94, gate switched off at a tick boundary with the worker idle
    // v6 (0.36.141, docs/30 §16): who waits on the GC lock, and the gate switched off at the sim's first hooked entry after the job
    LONG simLockWaits;       // +0x98, of lockWaits: the sim thread's (any thread that is not the worker)
    LONG simLockWaitUs;      // +0x9C
    LONG simLockMaxWaitUs;   // +0xA0
    LONG lazyEarlyOff;       // +0xA4, LazyEarly=1: gate off at a depth-0 sim entry once the worker is idle (no wait for the next tick)
};
extern "C" __declspec(dllexport) Ts3nMtProbe ts3native_mtprobe = {6};
static volatile LONG g_mtStopReq, g_mtBusy;  // worker stop request; a job (or a skip) is inside the worker
static bool g_mtLazyGate;   // [MonoThread] LazyGate=1 (default off: the gate stays on while a worker exists)
static bool g_mtLazyEarly;  // [MonoThread] LazyEarly=1 (with LazyGate=1, docs/30 §16)

static const DWORD MT_COLLECT_A = 0x00E4A050, MT_COLLECT_B = 0x00E4A0B0;

struct MtSite {
    DWORD addr;
    u8 len;        // copied prologue length (instruction boundaries, no relative operands: docs/30 §12.1, docs/57 §7)
    u8 bytes[9];
};
// [verified bytes: docs/30 §12.1, docs/57 §7, re-read from the Steam 1.67 exe for this build]
static const MtSite kMtLockSites[17] = {
    {0x00E4BE30, 9, {0x8B, 0x44, 0x24, 0x04, 0xB9, 0x00, 0x08, 0x00, 0x00}},  // GC_malloc
    {0x00E4BDC0, 9, {0x8B, 0x44, 0x24, 0x04, 0xB9, 0x00, 0x08, 0x00, 0x00}},  // GC_malloc_atomic
    {0x00E4BF90, 6, {0x8B, 0x0D, 0xB4, 0x49, 0x19, 0x01}},                    // GC_malloc_uncollectable
    {0x00E50DE0, 5, {0xA1, 0xB4, 0x49, 0x19, 0x01}},                          // GC_malloc_explicitly_typed
    {0x00E4BCE0, 7, {0x83, 0x3D, 0x04, 0x41, 0x1F, 0x01, 0x00}},              // GC_generic_malloc
    {0x00E4A050, 7, {0x83, 0x3D, 0x94, 0x30, 0x1F, 0x01, 0x00}},              // GC_try_to_collect
    {0x00E4A0B0, 7, {0x83, 0x3D, 0x94, 0x30, 0x1F, 0x01, 0x00}},              // GC_gcollect
    {0x00E4E450, 7, {0x83, 0x3D, 0xC0, 0x30, 0x1F, 0x01, 0x00}},              // GC_enable_incremental
    {0x00E4AAC0, 5, {0x53, 0x8B, 0x5C, 0x24, 0x08}},                          // register disappearing link
    {0x00E4ABE0, 5, {0xA1, 0x40, 0x48, 0x19, 0x01}},                          // unregister disappearing link
    {0x00E4AE30, 8, {0x8B, 0x44, 0x24, 0x14, 0x8B, 0x4C, 0x24, 0x10}},        // register finalizer
    {0x00E4A3F0, 5, {0x8B, 0x44, 0x24, 0x04, 0x50}},                          // GC_apply_to_each_object
    {0x00E4BEB0, 5, {0x56, 0x8B, 0x74, 0x24, 0x08}},                          // GC_free
    {0x00D71900, 5, {0x8B, 0x44, 0x24, 0x04, 0x55}},                          // EA frame arena grow
    {0x00D71A20, 5, {0x8B, 0x44, 0x24, 0x04, 0x56}},                          // EA frame arena pop
    {0x00D71B70, 5, {0x8B, 0x44, 0x24, 0x04, 0x56}},                          // EA frame arena release all
    {0x00E4D9F0, 8, {0x8B, 0x44, 0x24, 0x08, 0x8B, 0x4C, 0x24, 0x04}},        // GC_add_roots (last: LockAddRoots=0 leaves it out)
};
static const DWORD MT_FIN_INVOKE = 0x00E4B3F0, MT_MALLOC_HEAP = 0x00E4EC40;
static const u8 kMtFinInvoke[9] = {0x51, 0x53, 0x56, 0x8B, 0x35, 0x3C, 0x30, 0x1F, 0x01};  // push ecx/ebx/esi; mov esi,[0x11F303C]
static const u8 kMtMallocHeap[4] = {0x83, 0xEC, 0x20, 0x53};                                // sub esp,0x20; push ebx

static CRITICAL_SECTION g_mtLock;
static bool g_mtLockInit;
static DWORD g_mtRet[64];   // callers' return addresses, guarded by g_mtLock (only its owner is inside a locked hook)
static DWORD* g_mtSlot[64]; // each entry's return-slot address (caller's ESP at entry): frames nest, so outer slots are higher
static LONG g_mtDepth;
static void* g_mtTramp[17];
void MtLockExitThunk();
static DWORD g_mtWorkerTid;
static double g_mtUsPerTick;
// GC_invoke_finalizers state; the selftest points these at fakes
static DWORD** g_mtFinQueue = (DWORD**)(uintptr_t)0x011F303C;       // ready queue: entry +0 base, +4 next, +8 fn, +0xC data
static volatile LONG* g_mtFinLimit = (volatile LONG*)(uintptr_t)0x01194848;  // 0 = no per-call limit
static DWORD* g_mtMemFreed = (DWORD*)(uintptr_t)0x012225D4;
static DWORD* g_mtFinFreed = (DWORD*)(uintptr_t)0x012225D8;

static void MtLockInit() {
    if (g_mtLockInit) return;
    InitializeCriticalSectionAndSpinCount(&g_mtLock, 4000);
    LARGE_INTEGER f;
    QueryPerformanceFrequency(&f);
    g_mtUsPerTick = 1e6 / (double)f.QuadPart;
    g_mtLockInit = true;
}

static void MtLockTake() {
    Ts3nMtProbe& m = ts3native_mtprobe;
    if (TryEnterCriticalSection(&g_mtLock)) return;
    LARGE_INTEGER t0, t1;
    QueryPerformanceCounter(&t0);
    EnterCriticalSection(&g_mtLock);
    QueryPerformanceCounter(&t1);
    const LONG us = (LONG)((t1.QuadPart - t0.QuadPart) * g_mtUsPerTick);
    m.lockWaits++, m.lockWaitUs += us;  // under the lock now
    if (us > m.lockMaxWaitUs) m.lockMaxWaitUs = us;
    if (!g_mtWorkerTid || GetCurrentThreadId() != g_mtWorkerTid) {  // v6: the sim's share (the stop rule counts only this)
        m.simLockWaits++, m.simLockWaitUs += us;
        if (us > m.simLockMaxWaitUs) m.simLockMaxWaitUs = us;
    }
}

// A locked hook frame left by longjmp (GC_oom_fn -> 0xE53740, docs/120 §10) or any unwind never reaches the thunk: its
// entry stays, and so does one recursion of g_mtLock. The owner drops such entries at its next hooked enter or exit.
static void MtDropTop() {
    g_mtDepth--;
    ts3native_mtprobe.lockStale++;
    LeaveCriticalSection(&g_mtLock);
}

// C side of a locked hook entry: no Log, no allocation (it can run inside GC and interpreter code).
static void __cdecl MtLockEnter(DWORD site, DWORD* retSlot) {
    MtLockTake();
    // any entry left is ours (we hold the lock); one at or below the new slot is a frame already unwound
    while (g_mtDepth > 0 && g_mtSlot[g_mtDepth - 1] <= retSlot) MtDropTop();
    Ts3nMtProbe& m = ts3native_mtprobe;
    // LazyEarly (docs/30 §16): with Satb act the job runs into the next tick and ends mid-tick, but MtLazyOff waits for the
    // top of the tick after it, so the sim paid the gate for the rest of that tick (bp-satb-act-1 on satb5: 17.2 M sim
    // locked entries). Here, on the sim, holding the lock with no locked frame of its own open (depth 0: this entry is not
    // nested in a hooked GC frame) and the worker idle (g_mtBusy 0 = after its last GC exit; only the sim dispatches the next
    // job), no GC entry of either thread is inside the GC: the gate goes off and this entry runs unlocked, as at a tick boundary.
    if (g_mtLazyEarly && g_mtDepth == 0 && m.locking && g_mtWorkerTid && GetCurrentThreadId() != g_mtWorkerTid && m.workerState == 2 &&
        !g_mtBusy && !g_mtStopReq) {
        InterlockedExchange(&m.locking, 0);
        m.lazyEarlyOff++;
        LeaveCriticalSection(&g_mtLock);
        return;
    }
    m.lockedCalls++;
    if (g_mtWorkerTid && GetCurrentThreadId() == g_mtWorkerTid) {
        m.lockedWorker++;
        if (site == MT_COLLECT_A || site == MT_COLLECT_B) m.workerCollects++;
    }
    if (g_mtDepth >= 64) { m.lockOverflow++; LeaveCriticalSection(&g_mtLock); return; }
    g_mtSlot[g_mtDepth] = retSlot;
    g_mtRet[g_mtDepth++] = *retSlot;
    if (g_mtDepth > m.lockMaxDepth) m.lockMaxDepth = g_mtDepth;
    *retSlot = (DWORD)(uintptr_t)MtLockExitThunk;
}

// esp = the thunk's entry ESP (returning slot + 4 + any `ret N`): entries with a slot below it are the returning frame
// (the lowest such) and, above it, frames unwound past it
static DWORD __cdecl MtLockLeave(DWORD* esp) {
    while (g_mtDepth > 1 && g_mtSlot[g_mtDepth - 2] < esp) MtDropTop();
    const DWORD r = g_mtRet[--g_mtDepth];
    LeaveCriticalSection(&g_mtLock);
    return r;
}

// The original returned here (any convention: `ret N` already dropped its arguments). Keeps EAX, EDX, ECX and flags.
__declspec(naked) void MtLockExitThunk() {
    __asm {
        push 0
        pushad
        pushfd
        lea eax, [esp + 40]
        push eax
        call MtLockLeave
        add esp, 4
        mov [esp + 36], eax
        popfd
        popad
        ret
    }
}

// GC_oom_fn [0x11949BC] wrapper (docs/120 §10.5). Boehm calls the slot cdecl(size) with no lock of its own (allocation
// lock compiled out) from 0xE4BDAD, 0xE4AB81, 0xE4AD66, so the only lock held is ours. Mono's 0xE78610 raises the OOM
// exception, which longjmps past every locked hook frame of this thread (none sits above the interpreter's setjmp,
// docs/120 §10.1): release them all first so the other thread gets the lock now, not at our next hooked call. Without
// an interpreter context 0xE78610 returns NULL instead and the hooked frames return through their thunks, so the
// entries are taken back then (a call, not a tail jump). No SEH frame or destructor here: the longjmp crosses this frame.
typedef void*(__cdecl* MtOomFn)(size_t);
static const DWORD MT_OOM_SLOT = 0x011949BC, MT_OOM_MONO = 0x00E78610;
static MtOomFn g_mtOomOrig;
static void* __cdecl MtOomWrap(size_t n) {
    DWORD ret[64];
    DWORD* slot[64];
    LONG k = 0;
    if (g_mtLock.OwningThread == (HANDLE)(uintptr_t)GetCurrentThreadId()) {  // owner: every entry is this thread's
        k = g_mtDepth;
        memcpy(ret, g_mtRet, k * sizeof(DWORD));
        memcpy(slot, g_mtSlot, k * sizeof(DWORD*));
        ts3native_mtprobe.lockOomReleased += k;
        g_mtDepth = 0;
        for (LONG i = 0; i < k; i++) LeaveCriticalSection(&g_mtLock);  // C-side holds (MtLockC), if any, stay
    }
    void* r = g_mtOomOrig(n);
    if (k) {  // returned (no longjmp): the frames are live again, put their entries back on top
        MtLockTake();
        for (LONG i = 1; i < k; i++) EnterCriticalSection(&g_mtLock);
        memcpy(g_mtRet + g_mtDepth, ret, k * sizeof(DWORD));
        memcpy(g_mtSlot + g_mtDepth, slot, k * sizeof(DWORD*));
        g_mtDepth += k;
    }
    return r;
}
// Only over Mono's own handler (any other value = someone else hooked it: leave it). The selftest passes a fake slot.
static bool MtWrapOom(DWORD* slot, DWORD expect) {
    if (*slot != expect) return false;
    g_mtOomOrig = (MtOomFn)(uintptr_t)expect;
    const DWORD w = (DWORD)(uintptr_t)MtOomWrap;
    return Write(slot, &w, 4);
}

// Lock for C code (finalizer pop, worker registration): only while the gate is set, as the hooks.
static bool MtLockC() {
    if (!ts3native_mtprobe.locking) return false;
    MtLockTake();
    return true;
}
static void MtUnlockC(bool held) { if (held) LeaveCriticalSection(&g_mtLock); }

static void MtPut32(u8*& p, DWORD v) { memcpy(p, &v, 4); p += 4; }

// Hook stub for one site (40 bytes): gate 0 -> jmp [tramp]; else MtLockEnter(site, &return address), jmp [tramp].
static u8* MtEmitStub(u8* p, DWORD site, void** trampSlot) {
    *p++ = 0x83, *p++ = 0x3D, MtPut32(p, (DWORD)(uintptr_t)&ts3native_mtprobe.locking), *p++ = 0x00;  // cmp [gate],0
    *p++ = 0x75, *p++ = 0x06;                                                                       // jne locked
    *p++ = 0xFF, *p++ = 0x25, MtPut32(p, (DWORD)(uintptr_t)trampSlot);                              // jmp [tramp]
    *p++ = 0x60, *p++ = 0x9C;                                                                       // pushad; pushfd
    *p++ = 0x8D, *p++ = 0x44, *p++ = 0x24, *p++ = 0x24;                                             // lea eax,[esp+36]
    *p++ = 0x50, *p++ = 0x68, MtPut32(p, site);                                                     // push eax; push site
    *p++ = 0xE8;
    MtPut32(p, (DWORD)((uintptr_t)MtLockEnter - ((uintptr_t)p + 4)));                               // call MtLockEnter
    *p++ = 0x83, *p++ = 0xC4, *p++ = 0x08, *p++ = 0x9D, *p++ = 0x61;                                // add esp,8; popfd; popad
    *p++ = 0xFF, *p++ = 0x25, MtPut32(p, (DWORD)(uintptr_t)trampSlot);                              // jmp [tramp]
    return p;
}

// Checks every site first (all or nothing), then builds trampolines and stubs before writing any jmp, so a site is
// never live without its trampoline. Returns the number of detours written.
static int MtInstallLockSites(const MtSite* sites, int n) {
    for (int i = 0; i < n; i++)
        if (!Matches((void*)(uintptr_t)sites[i].addr, sites[i].bytes, sites[i].len)) {
            Log("SKIP mono thread P3: prologue at 0x%08X differs, nothing written", sites[i].addr);
            return 0;
        }
    u8* mem = (u8*)VirtualAlloc(NULL, 4096, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
    if (!mem) return 0;
    for (int i = 0; i < n; i++) {
        u8* tramp = mem + i * 112;  // 32 trampoline + 80 stub
        u8* t = (u8*)(uintptr_t)sites[i].addr;
        memcpy(tramp, sites[i].bytes, sites[i].len);
        tramp[sites[i].len] = 0xE9;
        *(LONG*)(tramp + sites[i].len + 1) = (LONG)((t + sites[i].len) - (tramp + sites[i].len + 5));
        g_mtTramp[i] = tramp;
        MtEmitStub(tramp + 32, sites[i].addr, &g_mtTramp[i]);
    }
    FlushInstructionCache(GetCurrentProcess(), mem, 4096);
    int done = 0;
    for (int i = 0; i < n; i++) {
        u8* t = (u8*)(uintptr_t)sites[i].addr;
        u8 jmp[16];
        memset(jmp, 0x90, sizeof(jmp));
        jmp[0] = 0xE9;
        *(LONG*)(jmp + 1) = (LONG)((mem + i * 112 + 32) - (t + 5));
        if (!Write(t, jmp, sites[i].len)) { Log("FAIL mono thread P3: write 0x%08X (err %lu)", sites[i].addr, GetLastError()); break; }
        done++;
    }
    return done;
}

// GC_invoke_finalizers 0xE4B3F0, same logic as the game's [verified bytes]: pop one entry under the lock, run it
// outside, at most [0x1194848] per call when that is non-zero, add the bytes freed meanwhile to [0x12225D8].
static int __cdecl MtInvokeFinalizersC() {
    int count = 0;
    DWORD before = 0;
    for (;;) {
        const LONG lim = *g_mtFinLimit;
        if (lim && count >= lim) break;
        const bool held = MtLockC();
        if (!count) before = *g_mtMemFreed;
        DWORD* cur = *g_mtFinQueue;
        if (cur) *g_mtFinQueue = (DWORD*)(uintptr_t)cur[1];
        MtUnlockC(held);
        if (!cur) break;
        cur[1] = 0;
        ((void(__cdecl*)(DWORD, DWORD))(uintptr_t)cur[2])(cur[0], cur[3]);
        cur[3] = 0;
        count++;
    }
    if (count) {
        const bool held = MtLockC();
        if (*g_mtMemFreed != before) *g_mtFinFreed += *g_mtMemFreed - before;
        MtUnlockC(held);
        ts3native_mtprobe.finRuns += count;  // sim thread only (finalizers run from Simulate)
    }
    return count;
}
// the game's function keeps ECX (push ecx / pop ecx); the C body may not
static __declspec(naked) int MtInvokeFinalizers() {
    __asm {
        push ecx
        call MtInvokeFinalizersC
        pop ecx
        ret
    }
}

static bool MtJmpTo(DWORD at, const void* to, size_t len) {
    u8 jmp[64];
    memset(jmp, 0x90, sizeof(jmp));
    jmp[0] = 0xE9;
    *(LONG*)(jmp + 1) = (LONG)((const u8*)to - ((u8*)(uintptr_t)at + 5));
    return Write((void*)(uintptr_t)at, jmp, len);
}

// --- EA frame arena pool fix (docs/120 §9) ---
// The interpreter's frame memory comes from per-state chunk chains (state+0x13C) fed by one pool of free 0x7FC-byte
// chunks [0x11EE508] (circular list, node +0 prev, +4 next, +8 chunk; chunk +0 top, +8 previous chunk, data at +0xC).
// Release-all 0xD71B70 returns every chunk of a chain to the pool but leaves +0x13C pointing at it [verified bytes], and a
// finished task state is freed twice (0xE59FD0 -> 0xE52DD0, then ScriptHost_RunTask 0xD7F166 -> 0xE53E80 -> 0xE52DD0)
// [verified callers]. A task that ends with frames still on its chain (a managed exception leaving it) so puts its
// chunks into the pool twice, and two live states later get the same chunk: their frames overwrite each other, and a
// frame exit finds the chain already emptied by the other state (fault at 0xE5444D) [verified in monohost].
// Fix: clear +0x13C after release-all, drop duplicate chunks from the pool once at install, and before each grow drop
// pooled chunks that are in use (a pooled chunk has top == data start; every release path resets it [verified bytes]).
static DWORD** g_mtArenaPool = (DWORD**)(uintptr_t)0x011EE508;  // the selftest points it at a fake pool
static void* g_mtGrowOrig;
static void* g_mtRelAllOrig;
static bool g_mtArenaFix = true;

static void MtUnlinkNode(DWORD* n) {
    ((DWORD*)(uintptr_t)n[0])[1] = n[1];
    ((DWORD*)(uintptr_t)n[1])[0] = n[0];
    ts3native_mtprobe.arenaDropped++;
}
static bool MtChunkFree(const DWORD* n) {
    const DWORD c = n[2];
    return !c || *(const DWORD*)(uintptr_t)c == c + 0xC;
}
// before 0xD71900 (under the P3 lock when the gate is on): the head is what a grow would take
static void __cdecl MtArenaHeal() {
    DWORD* s = *g_mtArenaPool;
    if (!s) return;
    for (DWORD* n = (DWORD*)(uintptr_t)s[1]; n != s && !MtChunkFree(n); n = (DWORD*)(uintptr_t)s[1]) {
        if (ts3native_mtprobe.arenaDropped < 8)  // docs/67 §22.2: each one is a frame overwrite that did not happen
            Log("arena guard: pooled chunk %08lX is in use (top %08lX), node dropped before a grow", n[2], *(const DWORD*)(uintptr_t)n[2]);
        MtUnlinkNode(n);
    }
}
static __declspec(naked) void MtArenaGrow() {
    __asm {
        pushad
        call MtArenaHeal
        popad
        jmp dword ptr [g_mtGrowOrig]
    }
}
static __declspec(naked) void MtArenaReleaseAll() {  // cdecl (state); EAX kept
    __asm {
        mov ecx, dword ptr [esp + 4]
        cmp dword ptr [ecx + 0x13C], 0
        je skip
        lock inc dword ptr [ts3native_mtprobe + 0x6C]
    skip:
        push ecx
        call dword ptr [g_mtRelAllOrig]
        add esp, 4
        mov ecx, dword ptr [esp + 4]
        mov dword ptr [ecx + 0x13C], 0
        ret
    }
}
// sim thread at a tick boundary (no state is inside an arena call): each chunk stays in the pool once
static void MtArenaDedupe() {
    DWORD* s = *g_mtArenaPool;
    if (!s) return;
    int n = 0;
    for (DWORD* a = (DWORD*)(uintptr_t)s[1]; a != s && n < 100000; a = (DWORD*)(uintptr_t)a[1], n++)
        for (DWORD* b = (DWORD*)(uintptr_t)a[1]; b != s;) {
            DWORD* next = (DWORD*)(uintptr_t)b[1];
            if (b[2] == a[2]) MtUnlinkNode(b);
            b = next;
        }
}

// [ArenaGuard] (docs/67 §22.2, ts3native 0.36.95): the fix above on the single sim thread, without [MonoThread]. Crash S
// (docs/67 §22-§22.1) read a freed delegate out of a yielded task's frame. A pool duplicate of a chunk that task still uses
// gives the chunk to a second state, whose frames overwrite the task's locals; the GC then frees what only the overwritten
// slot held [verified in monohost, docs/120 §9.1; that it happens in game is inferred]. Same wrappers, own 5-byte detours at
// 0xD71900 / 0xD71B70 (installed at load, before the pool exists: no dedupe needed, the heal drops older duplicates).
// Counters ts3native_mtprobe arenaRelNonEmpty / arenaDropped, minute line "arena guard:".
static const u8 kAgGrow[5] = {0x8B, 0x44, 0x24, 0x04, 0x55}, kAgRelAll[5] = {0x8B, 0x44, 0x24, 0x04, 0x56};
static int g_agReady;  // bit 0 grow heal, bit 1 release-all clear
static bool ApplyArenaGuard(const wchar_t* ini) {
    if (!GetPrivateProfileIntW(L"ArenaGuard", L"Enabled", 0, ini)) { Log("arena guard disabled (Enabled=0)"); return false; }
    if (GetPrivateProfileIntW(L"MonoThread", L"Enabled", 0, ini) && GetPrivateProfileIntW(L"MonoThread", L"GcLock", 0, ini)) {
        Log("arena guard: left to [MonoThread] ArenaFix (P3 owns 0xD71900 / 0xD71B70)");
        return false;
    }
    if ((g_mtGrowOrig = InstallDetour("arena grow (arena guard)", 0x00D71900, kAgGrow, 5, (void*)MtArenaGrow)) != NULL) g_agReady |= 1;
    if ((g_mtRelAllOrig = InstallDetour("arena release all (arena guard)", 0x00D71B70, kAgRelAll, 5, (void*)MtArenaReleaseAll)) != NULL) g_agReady |= 2;
    Log("%s arena guard: ready 0x%X (bit 0 heal before grow, 1 clear after release-all)", g_agReady == 3 ? "OK  " : "PART", g_agReady);
    return g_agReady != 0;
}
static void AgLogCounters() {
    if (g_agReady)
        Log("arena guard: release-all on a non-empty chain %ld, pooled chunks in use dropped %ld", ts3native_mtprobe.arenaRelNonEmpty,
            ts3native_mtprobe.arenaDropped);
}

// P3 install: lock detours, finalizer replacement, 0xE4EC40 ret. addRoots=false leaves GC_add_roots out (S3PP's
// LocalizedStringCache finds it by its first bytes, docs/68 conflict 4).
static void MtInstallP3(bool addRoots) {
    MtLockInit();
    Ts3nMtProbe& m = ts3native_mtprobe;
    const int n = addRoots ? 17 : 16;
    if (!Matches((void*)(uintptr_t)MT_FIN_INVOKE, kMtFinInvoke, sizeof(kMtFinInvoke)) ||
        !Matches((void*)(uintptr_t)MT_MALLOC_HEAP, kMtMallocHeap, sizeof(kMtMallocHeap))) {
        Log("SKIP mono thread P3: 0xE4B3F0 or 0xE4EC40 bytes differ, nothing written");
        return;
    }
    const int done = MtInstallLockSites(kMtLockSites, n);
    m.p3Sites = done;
    if (done != n) { Log("FAIL mono thread P3: %d of %d detours (gate stays 0)", done, n); return; }
    m.p3Ready = 1 | (addRoots ? 0 : 8);
    if (g_mtArenaFix) {  // the stubs jump through these slots on both gate paths: the fix runs with or without the lock
        MtArenaDedupe();
        g_mtGrowOrig = g_mtTramp[13], g_mtTramp[13] = (void*)MtArenaGrow;
        g_mtRelAllOrig = g_mtTramp[15], g_mtTramp[15] = (void*)MtArenaReleaseAll;
        m.p3Ready |= 16;
    }
    if (MtJmpTo(MT_FIN_INVOKE, (const void*)MtInvokeFinalizers, 5)) m.p3Ready |= 2, m.p3Sites++;
    const u8 ret = 0xC3;
    if (Write((void*)(uintptr_t)MT_MALLOC_HEAP, &ret, 1)) m.p3Ready |= 4, m.p3Sites++;
    if (MtWrapOom((DWORD*)(uintptr_t)MT_OOM_SLOT, MT_OOM_MONO)) m.p3Ready |= 32;  // data slot, not counted in p3Sites
    Log("OK   mono thread P3: %d lock detours%s, 0xE4B3F0 %s, 0xE4EC40 %s, arena fix %s (%ld pool duplicates dropped), GC_oom_fn %s (gate 0 until a worker runs)",
        done, addRoots ? "" : " (GC_add_roots left out)", (m.p3Ready & 2) ? "replaced" : "NOT replaced", (m.p3Ready & 4) ? "ret" : "NOT patched",
        (m.p3Ready & 16) ? "on" : "off", m.arenaDropped, (m.p3Ready & 32) ? "wrapped" : "NOT wrapped (slot is not 0xE78610)");
}

// --- P4: interpreter frame globals to TLS ---
static const DWORD MT_FRAME_G = 0x011F4170, MT_ARRAY_G = 0x011F416C, MT_FRAME_AT = 0x00E534E0;
struct MtBlock {
    DWORD addr;
    u8 len;
    u8 nIns;
    u8 insLen[9];
    u8 bytes[37];
};
// [verified: docs/30 §4 table and §12.4; whole blocks re-read from the exe]. Each instruction that names a global has
// it as its last 4 bytes; the stub repeats the block with an fs: prefix on those and the TEB slot offset instead.
static const MtBlock kMtBlocks[3] = {
    {0x00E53EBD, 37, 9, {5, 6, 1, 3, 3, 6, 2, 6, 5},
     {0xA1, 0x70, 0x41, 0x1F, 0x01, 0x8B, 0x0D, 0x6C, 0x41, 0x1F, 0x01, 0x53, 0x8B, 0x5D, 0x08, 0x89, 0x45, 0xA0, 0x8D,
      0x85, 0x8C, 0xB3, 0xFF, 0xFF, 0x33, 0xD2, 0x89, 0x1D, 0x70, 0x41, 0x1F, 0x01, 0xA3, 0x6C, 0x41, 0x1F, 0x01}},  // entry
    {0x00E597BA, 11, 2, {5, 6}, {0xA3, 0x70, 0x41, 0x1F, 0x01, 0x89, 0x0D, 0x6C, 0x41, 0x1F, 0x01}},              // exit A
    {0x00E59866, 12, 2, {6, 6}, {0x89, 0x15, 0x6C, 0x41, 0x1F, 0x01, 0x89, 0x0D, 0x70, 0x41, 0x1F, 0x01}},        // exit B
};
static const u8 kMtFrameAt[16] = {0x8B, 0x15, 0x6C, 0x41, 0x1F, 0x01, 0x85, 0xD2, 0x74, 0x4D, 0xA1, 0x70, 0x41, 0x1F, 0x01, 0x85};
static DWORD g_mtSlotF = TLS_OUT_OF_INDEXES, g_mtSlotA = TLS_OUT_OF_INDEXES;

// Stub for one block at `site` (the block's bytes, here or in a test copy): same instructions, globals -> fs:[0xE10 + 4*slot],
// then jmp site + len. Returns the end of the stub.
static u8* MtEmitBlock(u8* p, const MtBlock& b, DWORD site) {
    const u8* in = b.bytes;
    for (int i = 0; i < b.nIns; i++) {
        const u8 l = b.insLen[i];
        DWORD a;
        memcpy(&a, in + l - 4, 4);
        if (l >= 5 && (a == MT_FRAME_G || a == MT_ARRAY_G)) {
            *p++ = 0x64;  // fs:
            memcpy(p, in, l - 4);
            p += l - 4;
            MtPut32(p, 0xE10 + 4 * (a == MT_FRAME_G ? g_mtSlotF : g_mtSlotA));
        } else {
            memcpy(p, in, l);
            p += l;
        }
        in += l;
    }
    *p++ = 0xE9;
    MtPut32(p, (DWORD)((site + b.len) - ((DWORD)(uintptr_t)p + 4)));
    return p;
}

// 0xE534E0 (frame at depth, only caller the script call-stack reporter 0xD71CC0), same logic on this thread's slots.
// The caller tests *outMethod only; on failure the game leaves EAX as it was, this returns 0.
static void* __cdecl MtFrameAt(DWORD depth, DWORD* outMethod, LONG* outPos) {
    const u8* arr = (const u8*)TlsGetValue(g_mtSlotA);
    const u8* fr = (const u8*)TlsGetValue(g_mtSlotF);
    if (arr && fr) {
        const DWORD top = *(const DWORD*)(fr + 0x140);
        if (depth <= top) {
            const u8* e = arr + (top - depth) * 0x130;
            const DWORD* c = *(const DWORD* const*)e;
            if (c && c[1]) {
                const DWORD* rm = (const DWORD*)(uintptr_t)c[1];
                *outMethod = rm[2];
                *outPos = (LONG)(*(const DWORD*)(e + 4) - rm[0]) >> 1;
                return outPos;
            }
        }
    }
    *outMethod = 0;
    return NULL;
}

// This thread's P4 frame slot as memory (TEB TlsSlots +0xE10, index < 64): what fs:[0xE10 + 4*slot] in the stubs names.
static DWORD* MtFrameSlot() { return (DWORD*)(uintptr_t)(__readfsdword(0x18) + 0xE10 + 4 * g_mtSlotF); }

// Allocates the slot pair (indexes < 64: the value then sits at fs:[0xE10 + 4*index]) and builds the stubs into `stubs`.
// sites[i] is where block i lives (the game addresses, or test copies). The calling thread's slots get the globals' values.
static bool MtBuildP4(u8* stubs, const DWORD* sites, const DWORD* globals) {
    if (g_mtSlotF == TLS_OUT_OF_INDEXES) {
        g_mtSlotF = TlsAlloc();
        g_mtSlotA = TlsAlloc();
    }
    if (g_mtSlotF >= 64 || g_mtSlotA >= 64) {
        Log("SKIP mono thread P4: TLS indexes %lu/%lu not below 64", g_mtSlotF, g_mtSlotA);
        if (g_mtSlotF != TLS_OUT_OF_INDEXES) TlsFree(g_mtSlotF);
        if (g_mtSlotA != TLS_OUT_OF_INDEXES) TlsFree(g_mtSlotA);
        g_mtSlotF = g_mtSlotA = TLS_OUT_OF_INDEXES;
        return false;
    }
    TlsSetValue(g_mtSlotF, (void*)(uintptr_t)globals[0]);
    TlsSetValue(g_mtSlotA, (void*)(uintptr_t)globals[1]);
    for (int i = 0; i < 3; i++) MtEmitBlock(stubs + i * 64, kMtBlocks[i], sites[i]);
    FlushInstructionCache(GetCurrentProcess(), stubs, 192);
    ts3native_mtprobe.p4Slots = (LONG)(g_mtSlotF | (g_mtSlotA << 8));
    return true;
}

// P4 install on the sim thread at a tick boundary (no thread inside the blocks; only the sim thread runs managed code).
static void MtInstallP4() {
    Ts3nMtProbe& m = ts3native_mtprobe;
    for (int i = 0; i < 3; i++)
        if (!Matches((void*)(uintptr_t)kMtBlocks[i].addr, kMtBlocks[i].bytes, kMtBlocks[i].len)) {
            Log("SKIP mono thread P4: block 0x%08X differs (MonoPatcher? docs/68 conflict 12), nothing written", kMtBlocks[i].addr);
            return;
        }
    if (!Matches((void*)(uintptr_t)MT_FRAME_AT, kMtFrameAt, sizeof(kMtFrameAt)) || !Readable((void*)(uintptr_t)MT_FRAME_G, 4)) {
        Log("SKIP mono thread P4: 0xE534E0 differs, nothing written");
        return;
    }
    u8* stubs = (u8*)VirtualAlloc(NULL, 4096, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
    const DWORD sites[3] = {kMtBlocks[0].addr, kMtBlocks[1].addr, kMtBlocks[2].addr};
    const DWORD globals[2] = {*(DWORD*)(uintptr_t)MT_FRAME_G, *(DWORD*)(uintptr_t)MT_ARRAY_G};
    if (!stubs || !MtBuildP4(stubs, sites, globals)) return;
    for (int i = 0; i < 3; i++)
        if (MtJmpTo(kMtBlocks[i].addr, stubs + i * 64, kMtBlocks[i].len)) m.p4Ready |= 1 << i;
    if (MtJmpTo(MT_FRAME_AT, (const void*)MtFrameAt, 5)) m.p4Ready |= 8;
    // docs/51 §377: the global stays stale once the entry block writes the slot; our readers of the current state
    // (CanYield for [RouteAsync] START/JobYield, gcroots.cpp) follow the sim thread's slot (P4 installs on the sim thread)
    if (m.p4Ready & 1) g_curState = MtFrameSlot();
    Log("OK   mono thread P4: frame globals -> TLS %lu/%lu (fs:0x%X/0x%X), blocks 0x%lX of 0xF", g_mtSlotF, g_mtSlotA,
        0xE10 + 4 * g_mtSlotF, 0xE10 + 4 * g_mtSlotA, (DWORD)m.p4Ready);
}

// --- worker entry (docs/30 §5) ---
typedef void*(__cdecl* MtInvokeFn)(void* method, void* obj, void** params, void** exc);
typedef void*(__cdecl* MtDomainGetFn)();
typedef void(__cdecl* MtDomainSetFn)(void* domain);
static MtInvokeFn g_mtInvoke = (MtInvokeFn)(uintptr_t)0x00E5A600;         // runtime invoke, called directly (not the slot)
static MtDomainGetFn g_mtDomainGet = (MtDomainGetFn)(uintptr_t)0x00E75260;  // TlsGetValue([0x11959DC])
static MtDomainSetFn g_mtDomainSet = (MtDomainSetFn)(uintptr_t)0x00E75270;  // TlsSetValue([0x11959DC], domain)
static DWORD* g_mtInterpKey = (DWORD*)(uintptr_t)0x011F4160;                // TLS index of the interpreter ThreadContext
static volatile LONG* g_mtTrackMode = (volatile LONG*)(uintptr_t)0x011DC18C;  // EA memory tracking mode
struct MtJob { void* method; void* obj; void** params; };
static MtJob g_mtJob;
static HANDLE g_mtWorker, g_mtJobEv;
static void* g_mtDomain;
// worker, around each job: phase 0 before the invoke, 1 after, 2 after with a managed exception ([AutShadow] part 2)
static void(__cdecl* g_mtJobHook)(int phase, void* method);

static bool MtRegisterCurrentThread();  // monothread.cpp (P1)
static void MtUnregisterCurrentThread() {
    const bool held = MtLockC();
    const int i = MtFind(GetCurrentThreadId());
    if (i >= 0) {
        DWORD* e = g_mtTable + i * 5;
        CloseHandle((HANDLE)(uintptr_t)e[2]);
        e[3] = 0;  // no stack base: GC_stop_world and P2 skip the entry
        e[0] = 0;
    }
    MtUnlockC(held);
}

static DWORD WINAPI MtWorkerMain(void*) {
    Ts3nMtProbe& m = ts3native_mtprobe;
    if (!MtRegisterCurrentThread()) { m.workerState = -1; return 1; }
    g_mtDomainSet(g_mtDomain);
    if (TlsGetValue(*g_mtInterpKey)) m.workerCtxSet++;  // a new thread's slot is NULL; 0xE5A600 then uses its own stack
    m.workerState = 2;
    for (;;) {
        WaitForSingleObject(g_mtJobEv, INFINITE);
        if (g_mtStopReq) break;
        if (*g_mtTrackMode) { m.workerTrackSkips++; InterlockedExchange(&g_mtBusy, 0); continue; }
        m.workerState = 3;
        LARGE_INTEGER t0, t1;
        if (g_mtJobHook) g_mtJobHook(0, g_mtJob.method);
        QueryPerformanceCounter(&t0);
        void* exc = NULL;
        void* r = g_mtInvoke(g_mtJob.method, g_mtJob.obj, g_mtJob.params, &exc);
        QueryPerformanceCounter(&t1);
        if (g_mtJobHook) g_mtJobHook(exc ? 2 : 1, g_mtJob.method);
        const LONG us = (LONG)((t1.QuadPart - t0.QuadPart) * g_mtUsPerTick);
        m.workerJobs++, m.workerRunUs += us;
        if (us > m.workerMaxRunUs) m.workerMaxRunUs = us;
        if (exc) m.workerExceptions++, r = NULL;  // counted and dropped, never through the game's script error path
        InterlockedExchange(&m.result, (LONG)(uintptr_t)r);
        m.workerState = 2;
        InterlockedIncrement(&m.seq);
        InterlockedExchange(&g_mtBusy, 0);
    }
    MtUnregisterCurrentThread();
    m.workerState = 4;
    return 0;
}

// Sim thread, tick boundary: the gate goes on before the worker exists (docs/30 §3 flag rule).
static bool MtStartWorker() {
    Ts3nMtProbe& m = ts3native_mtprobe;
    g_mtDomain = g_mtDomainGet();
    if (!g_mtJobEv) g_mtJobEv = CreateEventW(NULL, FALSE, FALSE, NULL);
    g_mtStopReq = 0, g_mtBusy = 0;
    m.locking = 1;
    m.workerState = 1;
    DWORD tid = 0;
    g_mtWorker = CreateThread(NULL, 256 << 10, MtWorkerMain, NULL, STACK_SIZE_PARAM_IS_A_RESERVATION | CREATE_SUSPENDED, &tid);
    if (!g_mtWorker) { m.workerState = -2, m.locking = 0; return false; }
    g_mtWorkerTid = tid;  // before it runs: the lock hooks count its calls by this id
    m.workerTid = (LONG)tid;
    m.workerStarts++;
    ResumeThread(g_mtWorker);
    return true;
}

// Sim thread, tick boundary: the worker is between jobs (or finishes the current one), unregisters and exits; then the
// gate goes off. A worker that does not stop in time keeps the gate on (locking stays correct, only slower).
static bool MtStopWorker(DWORD waitMs) {
    Ts3nMtProbe& m = ts3native_mtprobe;
    if (!g_mtWorker) return true;
    if (g_mtLazyGate) InterlockedExchange(&m.locking, 1);  // the worker unregisters under the lock, as it registered
    g_mtStopReq = 1;
    SetEvent(g_mtJobEv);
    if (WaitForSingleObject(g_mtWorker, waitMs) != WAIT_OBJECT_0) { m.workerStopFails++; return false; }
    CloseHandle(g_mtWorker);
    g_mtWorker = NULL, g_mtWorkerTid = 0;
    m.locking = 0;
    return true;
}

// Sim thread: hand one job to the idle worker. The arguments must stay alive and rooted until seq moves (a managed
// static or a GC_malloc_uncollectable cell; docs/120 §1.2). Methods must have run once on the sim thread (docs/30 §5 step 6).
// LazyGate=1: the gate goes on here, before the worker can see the job (SetEvent is a full barrier), so every GC/alloc
// entry the worker makes in the job takes the lock. Only from the sim thread outside every GC and interpreter function
// (its callers: [AutShadow]'s post-pump / loop-back dispatch, monohost's driver): no unlocked GC frame of the sim is open
// when the hooks start locking. MtLazyOff turns it off again.
bool MtSubmit(void* method, void* obj, void** params) {  // external: no game caller until the MtProbe step
    if (!g_mtWorker || ts3native_mtprobe.workerState != 2 || InterlockedCompareExchange(&g_mtBusy, 1, 0) != 0) return false;
    if (g_mtLazyGate && !ts3native_mtprobe.locking) InterlockedExchange(&ts3native_mtprobe.locking, 1), ts3native_mtprobe.lazyOn++;
    g_mtJob.method = method, g_mtJob.obj = obj, g_mtJob.params = params;
    SetEvent(g_mtJobEv);
    return true;
}

// LazyGate=1, sim thread at a tick boundary (outside every GC and interpreter function): gate off once the worker is idle.
// g_mtBusy goes 0 only after the invoke returned, i.e. after every locked hook frame of the job left through its thunk and
// released the lock; the worker then only waits on g_mtJobEv (no GC entry) until the next MtSubmit turns the gate on again.
// A GC on the sim with the gate off still stops and scans the idle worker (P2): its stack holds no live job references.
static void MtLazyOff() {
    Ts3nMtProbe& m = ts3native_mtprobe;
    if (!g_mtLazyGate || !g_mtWorker || m.workerState != 2 || g_mtBusy || !m.locking) return;
    InterlockedExchange(&m.locking, 0);
    m.lazyOff++;
}

// --- F1: ScriptHost_NativeToManaged 0xD8A8E0 on the worker (docs/122 §12.5, docs/120 §8 F1, §9.4) ---
// The game's entry takes the call record at the cursor [0x11EE5A4] of one shared chain (0xD8A830) and steps the cursor
// back on exit (0xD8A9A5) [verified bytes]; two threads interleaved (A in, B in, A out, A in) put two interpreters on one
// record. The detour sends only the worker to the same body on a worker-private record stack; the sim thread keeps the
// game's code. Signature [verified bytes]: (RuntimeMethod* rm, MonoObject* this, stackval* args, stackval* ret,
// MonoObject** exc, ThreadContext* ctx), cdecl, no return value.
typedef void(__cdecl* MtExecFn)(void* rec, void* ctx);
typedef void*(__cdecl* MtSigFn)(void* method);
typedef void*(__cdecl* MtUncollFn)(size_t n);
static MtExecFn g_mtExec = (MtExecFn)(uintptr_t)0x00E53EB0;          // ves_exec_method_with_context
static MtSigFn g_mtSig = (MtSigFn)(uintptr_t)0x00E85040;             // mono_method_signature
static MtUncollFn g_mtUncoll = (MtUncollFn)(uintptr_t)0x00E4BF90;    // GC_malloc_uncollectable (P3-locked): records stay roots
static const DWORD MT_N2M = 0x00D8A8E0;
static const u8 kMtN2m[5] = {0x8B, 0x44, 0x24, 0x18, 0x53};  // mov eax,[esp+0x18]; push ebx
static DWORD g_mtN2mCont = MT_N2M + 5;
static u8* g_mtN2mRec[32];  // 0x150 record + its 0x28 frame buffer at +0x30, as the game's nodes
static LONG g_mtN2mDepth;   // the worker's nesting (only the worker uses these)
static bool g_mtN2mPrivate = true;

// returns false when the private stack is full or a record cannot be made (the caller then takes the game's path)
// [AutShadow] Satb act (docs/206 §8, docs/data/satb-races.csv X03): the sim interprets while the worker runs, so the game's
// shared call-record chain [0x11EE5A4] is the sim's; a worker overflow must not fall back to it. satb.cpp sets the flag for
// a span and the hook (the object gives up, the job stops); the call returns with the preallocated exception instead.
static volatile LONG g_mtNoSharedFallback;
static void* g_mtFallbackExc;
static void(__cdecl* g_mtNoFallbackHook)();
static bool MtN2mRefuse(void** exc) {
    if (!g_mtNoSharedFallback) return false;
    if (exc) *exc = g_mtFallbackExc;
    if (g_mtNoFallbackHook) g_mtNoFallbackHook();
    return true;
}
static bool __cdecl MtN2mWorker(void* rm, void* self, void* args, void* ret, void** exc, u8* ctx) {
    Ts3nMtProbe& m = ts3native_mtprobe;
    const LONG d = g_mtN2mDepth;
    if (d >= 32) { m.n2mOverflow++; return MtN2mRefuse(exc); }
    if (!g_mtN2mRec[d]) {
        u8* r = (u8*)g_mtUncoll(0x150);
        u8* b = (u8*)g_mtUncoll(0x28);
        if (!r || !b) { m.n2mOverflow++; return MtN2mRefuse(exc); }
        *(void**)(r + 0x30) = b;
        g_mtN2mRec[d] = r;
    }
    u8* r = g_mtN2mRec[d];
    u8* b = *(u8**)(r + 0x30);
    g_mtN2mDepth = d + 1;
    if (d + 1 > m.n2mMaxDepth) m.n2mMaxDepth = d + 1;
    m.n2mWorker++;
    // same fill as 0xD8A830 + 0xD8A8F2-0xD8A94C
    void* method = *(void**)((u8*)rm + 8);
    *(void**)(r + 0x10) = exc, *(void**)(r + 0x14) = ctx;
    *(void**)(r + 0x2C) = args, *(void**)(r + 0x38) = ret;
    *(void**)(r + 0x00) = method;
    *(void**)(r + 0x24) = g_mtSig(method);
    void* cur = *(void**)(ctx + 8);
    *(void**)(r + 0x34) = cur;
    *(void**)(b + 0x00) = cur, *(void**)(b + 0x0C) = self, *(void**)(b + 0x14) = args, *(void**)(b + 0x08) = ret, *(void**)(b + 0x04) = rm;
    *(LONG*)(r + 0x140) = -1;
    g_mtExec(r, ctx);
    void* e = *(void**)(r + 0x0C);
    if (e && exc) *exc = e;
    // clear as 0xD8A96B-0xD8A9B2: buffer, record, then the record's buffer link again
    memset(b, 0, 0x28);
    memset(r, 0, 0x150);
    *(void**)(r + 0x30) = b;
    g_mtN2mDepth = d;
    return true;
}
static __declspec(naked) void MtN2mEntry() {
    __asm {
        mov eax, fs:[0x24]  // TEB ClientId.UniqueThread
        cmp eax, dword ptr [g_mtWorkerTid]
        jne game
        push dword ptr [esp + 0x18]
        push dword ptr [esp + 0x18]
        push dword ptr [esp + 0x18]
        push dword ptr [esp + 0x18]
        push dword ptr [esp + 0x18]
        push dword ptr [esp + 0x18]
        call MtN2mWorker
        add esp, 0x18
        test al, al
        jz game
        ret
    game:
        mov eax, dword ptr [esp + 0x18]
        push ebx
        jmp dword ptr [g_mtN2mCont]
    }
}
static void MtInstallN2m() {
    if (!Matches((void*)(uintptr_t)MT_N2M, kMtN2m, sizeof(kMtN2m))) { Log("SKIP mono thread F1: 0xD8A8E0 differs, nothing written"); return; }
    if (MtJmpTo(MT_N2M, (const void*)MtN2mEntry, 5)) ts3native_mtprobe.n2mReady = 1;
    Log("%s mono thread F1: 0xD8A8E0 -> worker-private call records", ts3native_mtprobe.n2mReady ? "OK  " : "FAIL");
}

static bool g_mtWantP3, g_mtWantP4, g_mtAddRoots;

// first Simulate tick (sim thread, after P1): install what the ini asked for
static void MtM5FirstTick() {
    if (g_mtWantP3) MtInstallP3(g_mtAddRoots);
    if (g_mtWantP4) MtInstallP4();
    if (g_mtWantP3 && g_mtN2mPrivate) MtInstallN2m();
}

// every Simulate tick (sim thread): start / stop the worker on the mtMode switch
static void MtM5Tick() {
    Ts3nMtProbe& m = ts3native_mtprobe;
    const bool want = ts3native_counters.mtMode >= 1 && (m.p3Ready & 3) == 3 && (m.p3Ready & 4) && m.p4Ready == 0xF;
    if (want && !g_mtWorker) {
        if (m.workerState >= 0 && m.workerState != 1) MtStartWorker();
    } else if (!want && g_mtWorker) {
        MtStopWorker(1000);
    }
    if (g_mtWorker && m.workerState == -1) MtStopWorker(1000);  // registration failed: thread already gone
    MtLazyOff();
}
