// TestMonoThreadM5 (monothread-m5.cpp: P3 gated GC lock, P4 frame TLS blocks, worker entry; docs/30 §14). Included by
// selftest.cpp after TestMonoThread, whose fake GC thread table (g_mtFakeTab, MtFakeNewThread) it reuses.

#include <setjmp.h>

// fake free list, popped and pushed with a gap so two unlocked threads would race
struct MtNode { MtNode* next; volatile LONG owner; };
static MtNode g_mtNodes[64];
static MtNode* volatile g_mtFreeList;
static void MtSpin(int n) { for (volatile int s = 0; s < n; s++) {} }
static void* __cdecl MtFakeAlloc(int) {
    MtNode* n = g_mtFreeList;
    if (!n) return NULL;
    MtSpin(30);
    g_mtFreeList = n->next;
    return n;
}
static void __cdecl MtFakeFree(MtNode* n) {
    MtNode* h = g_mtFreeList;
    MtSpin(30);
    n->next = h;
    g_mtFreeList = n;
}
typedef void*(__cdecl* MtAllocFn)(int);
typedef void(__cdecl* MtFreeFn)(MtNode*);
typedef void*(__stdcall* MtGrowFn)(int);
static MtAllocFn g_mtHAlloc;
static MtFreeFn g_mtHFree;
static MtGrowFn g_mtHGrow;
static LONG g_mtGrowDepth;
// stdcall (ret 4) and nested: two hooked calls inside a hooked call, like 0xD71900 -> GC_malloc
static void* __stdcall MtFakeGrow(int n) {
    void* last = NULL;
    g_mtGrowDepth = g_mtDepth;
    for (int i = 0; i < n; i++) {
        last = g_mtHAlloc(8);
        if (last) g_mtHFree((MtNode*)last);
    }
    return (void*)((uintptr_t)last | 1);
}
// I-120-8 fakes: a hooked entry left by longjmp while locked (as GC_oom_fn -> 0xE53740), and a hooked outer entry that
// catches it inside its own frame and then returns normally
typedef void(__cdecl* MtJumpFn)(jmp_buf*);
typedef int(__cdecl* MtOuterFn)(int);
static MtJumpFn g_mtHJump;
static MtOuterFn g_mtHOuter;
static void __cdecl MtFakeJump(jmp_buf* jb) { longjmp(*jb, 1); }
#pragma warning(push)
#pragma warning(disable : 4611)  // setjmp with C++: no objects with destructors here
static int __cdecl MtFakeOuter(int v) {
    jmp_buf jb;
    if (!setjmp(jb)) g_mtHJump(&jb);
    return v + 1;
}
static int MtJumpOut() {
    jmp_buf jb;
    if (!setjmp(jb)) g_mtHJump(&jb);
    return 1;
}
#pragma warning(pop)
static DWORD WINAPI MtOtherLocked(void*) {
    void* n = g_mtHAlloc(8);
    if (n) g_mtHFree((MtNode*)n);
    return n ? 0 : 1;
}
// GC_oom_fn wrapper fakes (docs/120 §10.5): hooked outer -> hooked inner -> call [slot] (as GC_malloc ->
// GC_generic_malloc -> GC_oom_fn); the fake Mono handler checks the lock is free for another thread, then longjmps
// (interpreter context) or returns NULL (none)
static DWORD g_mtOomFakeSlot;
static jmp_buf* g_mtOomJb;
static LONG g_mtOomOther = -1;  // other thread's result inside the handler: 0 = got the lock
typedef void*(__cdecl* MtOomCallFn)(size_t);
static MtOomCallFn g_mtHOomInner, g_mtHOomOuter;
static void* __cdecl MtFakeOomInner(size_t n) { return ((MtOomFn)(uintptr_t)g_mtOomFakeSlot)(n); }
static volatile LONG g_mtOomOuterBack;
static void* __cdecl MtFakeOomOuter(size_t n) {  // not a tail call: a real nested frame, as GC_malloc -> GC_generic_malloc
    void* r = g_mtHOomInner(n);
    g_mtOomOuterBack++;
    return r;
}
static void* __cdecl MtFakeMonoOom(size_t) {
    HANDLE ot = CreateThread(NULL, 0, MtOtherLocked, NULL, 0, NULL);  // no further call on this thread before it finishes
    DWORD rc = 9;
    if (WaitForSingleObject(ot, 5000) == WAIT_OBJECT_0) GetExitCodeThread(ot, &rc);
    CloseHandle(ot);
    g_mtOomOther = (LONG)rc;
    if (g_mtOomJb) longjmp(*g_mtOomJb, 1);
    return NULL;
}
#pragma warning(push)
#pragma warning(disable : 4611)
static int MtOomJumpOut() {
    jmp_buf jb;
    g_mtOomJb = &jb;
    if (!setjmp(jb)) g_mtHOomOuter(16);
    g_mtOomJb = NULL;
    return 1;
}
#pragma warning(pop)
static volatile LONG g_mtDup, g_mtNull;
static void MtHammer(int iters) {
    const LONG me = (LONG)GetCurrentThreadId();
    for (int i = 0; i < iters; i++) {
        MtNode* n = (MtNode*)g_mtHAlloc(8);
        if (!n) { InterlockedIncrement(&g_mtNull); continue; }
        if (InterlockedCompareExchange(&n->owner, me, 0) != 0) InterlockedIncrement(&g_mtDup);  // someone else holds it
        MtSpin(10);
        n->owner = 0;
        g_mtHFree(n);
    }
}
static DWORD WINAPI MtHammerThread(void* p) { MtHammer((int)(uintptr_t)p); return 0; }
static void MtResetList() {
    g_mtFreeList = NULL;
    for (int i = 63; i >= 0; i--) g_mtNodes[i].owner = 0, g_mtNodes[i].next = g_mtFreeList, g_mtFreeList = &g_mtNodes[i];
}
static int MtListLen() {
    int n = 0;
    for (MtNode* p = g_mtFreeList; p && n < 1000; p = p->next) n++;
    return n;
}
// a target the detour can patch: 5 NOPs (the "prologue"), then jmp fn
static u8* MtMakeFake(u8* mem, const void* fn) {
    memset(mem, 0x90, 5);
    mem[5] = 0xE9;
    *(LONG*)(mem + 6) = (LONG)((const u8*)fn - (mem + 10));
    return mem;
}

// finalizer queue fakes
// F1 fakes: exec checks the record fill and nests once (a native -> managed -> native -> managed chain on the worker)
static u8* g_mtFakeRecs[2];
static int g_mtFakeExecs;
static void* __cdecl MtFakeSig(void* m) { return (u8*)m + 1; }
static void* __cdecl MtFakeUncoll(size_t n) { return calloc(1, n); }
static void __cdecl MtFakeExec(void* rec, void* ctx) {
    u8* r = (u8*)rec;
    const int depth = g_mtFakeExecs++;
    g_mtFakeRecs[depth] = r;
    u8* b = *(u8**)(r + 0x30);
    assert(*(void**)(r + 0x14) == ctx && *(LONG*)(r + 0x140) == -1 && *(DWORD*)(r + 0) == 0x5000 && *(DWORD*)(r + 0x24) == 0x5001);
    assert(*(DWORD*)(r + 0x2C) == 0x7000 && *(DWORD*)(r + 0x38) == 0x7100 && *(DWORD*)(b + 0x14) == 0x7000 && *(DWORD*)(b + 8) == 0x7100 &&
           *(DWORD*)(b + 0) == 0xC0C0 && *(DWORD*)(r + 0x34) == 0xC0C0);
    if (depth == 0) {
        void* exc = NULL;
        DWORD rm[3] = {0, 0, 0x5000};
        assert(MtN2mWorker(rm, NULL, (void*)0x7000, (void*)0x7100, &exc, (u8*)ctx));
        *(DWORD*)(r + 0x0C) = 0xE0E0;  // exception out of the outer call
    }
}
static LONG g_mtFakeRelAllCalls;
static void __cdecl MtFakeRelAll(void*) { g_mtFakeRelAllCalls++; }  // stands in for 0xD71B70: leaves +0x13C as it was
static DWORD g_mtFinFakeQ, g_mtFinFakeFreed, g_mtFinFakeAcc;
static volatile LONG g_mtFinFakeLim;
static DWORD g_mtFinSeen[8];
static int g_mtFinN;
static void __cdecl MtFakeFinalizer(DWORD base, DWORD data) {
    g_mtFinSeen[g_mtFinN++ & 7] = base ^ data;
    g_mtFinFakeFreed += 16;  // the finalizer frees memory meanwhile
}
static __declspec(naked) DWORD MtCallFinKeepEcx() {  // returns ECX after the call (must still be 0x1234)
    __asm {
        push ecx
        mov ecx, 0x1234
        call MtInvokeFinalizers
        mov eax, ecx
        pop ecx
        ret
    }
}

// run a (patched) block copy with chosen EBP/EAX/ECX/EDX; returns ECX afterwards
static __declspec(naked) DWORD __cdecl MtRunBlock(void*, DWORD, DWORD, DWORD, DWORD) {
    __asm {
        push ebp
        push ebx
        push esi
        push edi
        mov esi, [esp + 20]
        mov eax, [esp + 28]
        mov ecx, [esp + 32]
        mov edx, [esp + 36]
        mov ebp, [esp + 24]
        call esi
        mov eax, ecx
        pop edi
        pop esi
        pop ebx
        pop ebp
        ret
    }
}
static u8* g_mtBlk[3];
static DWORD WINAPI MtOtherFrames(void*) {
    // a second thread: its own slots start NULL and take its own values
    if (TlsGetValue(g_mtSlotF) || TlsGetValue(g_mtSlotA)) return 1;
    MtRunBlock(g_mtBlk[1], 0, 0x5150, 0x5151, 0);
    return TlsGetValue(g_mtSlotF) == (void*)0x5150 && TlsGetValue(g_mtSlotA) == (void*)0x5151 ? 0 : 2;
}

// worker fakes
static DWORD g_mtDomIdx, g_mtCtxIdx, g_mtFakeInterpKey;
static volatile LONG g_mtFakeTrack;
static void* __cdecl MtFakeDomainGet() { return (void*)0xD0D0; }
static void __cdecl MtFakeDomainSet(void* d) { TlsSetValue(g_mtDomIdx, d); }
static void* __cdecl MtFakeInvoke(void* method, void* obj, void** params, void** exc) {
    if (TlsGetValue(g_mtDomIdx) != (void*)0xD0D0 || GetCurrentThreadId() != g_mtWorkerTid) return NULL;
    if (obj == (void*)1) { *exc = (void*)0xE0; return NULL; }
    MtHammer((int)(uintptr_t)params[0]);  // allocates through the hooked entries while the sim thread does too
    return (void*)((uintptr_t)method + 1);
}
static void MtWaitFor(volatile LONG* v, LONG want) {
    for (int i = 0; i < 2000 && *v != want; i++) Sleep(1);
    assert(*v == want);
}

static void TestMonoThreadM5() {
    Ts3nMtProbe& m = ts3native_mtprobe;
    static_assert(offsetof(Ts3nMtProbe, locking) == 0x0C && offsetof(Ts3nMtProbe, p4Ready) == 0x30 &&
                  offsetof(Ts3nMtProbe, seq) == 0x64 && offsetof(Ts3nMtProbe, arenaRelNonEmpty) == 0x6C && offsetof(Ts3nMtProbe, lockStale) == 0x84 &&
                  offsetof(Ts3nMtProbe, lockOomReleased) == 0x88 && offsetof(Ts3nMtProbe, lazyOff) == 0x94 && offsetof(Ts3nMtProbe, simLockWaits) == 0x98 &&
                  offsetof(Ts3nMtProbe, lazyEarlyOff) == 0xA4 && sizeof(Ts3nMtProbe) == 0xA8,
                  "ts3native_mtprobe v6 layout (docs/registry.md)");
    assert(m.version == 6 && m.locking == 0);
    MtLockInit();
    const DWORD me = GetCurrentThreadId();

    // --- P3: three fake GC entries (cdecl alloc, cdecl free, stdcall nested grow) behind generated stubs ---
    u8* code = (u8*)VirtualAlloc(NULL, 4096, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
    MtSite sites[7] = {{(DWORD)(uintptr_t)MtMakeFake(code, (const void*)MtFakeAlloc), 5, {0x90, 0x90, 0x90, 0x90, 0x90}},
                       {(DWORD)(uintptr_t)MtMakeFake(code + 16, (const void*)MtFakeFree), 5, {0x90, 0x90, 0x90, 0x90, 0x90}},
                       {(DWORD)(uintptr_t)MtMakeFake(code + 32, (const void*)MtFakeGrow), 5, {0x90, 0x90, 0x90, 0x90, 0x90}},
                       {(DWORD)(uintptr_t)MtMakeFake(code + 48, (const void*)MtFakeJump), 5, {0x90, 0x90, 0x90, 0x90, 0x90}},
                       {(DWORD)(uintptr_t)MtMakeFake(code + 64, (const void*)MtFakeOuter), 5, {0x90, 0x90, 0x90, 0x90, 0x90}},
                       {(DWORD)(uintptr_t)MtMakeFake(code + 80, (const void*)MtFakeOomInner), 5, {0x90, 0x90, 0x90, 0x90, 0x90}},
                       {(DWORD)(uintptr_t)MtMakeFake(code + 96, (const void*)MtFakeOomOuter), 5, {0x90, 0x90, 0x90, 0x90, 0x90}}};
    MtSite bad = sites[0];
    bad.bytes[2] = 0xCC;
    MtSite two[2] = {sites[0], bad};
    assert(MtInstallLockSites(two, 2) == 0 && code[0] == 0x90);  // one prologue differs: nothing written
    assert(MtInstallLockSites(sites, 7) == 7 && code[0] == 0xE9 && code[16] == 0xE9 && code[32] == 0xE9);
    g_mtHAlloc = (MtAllocFn)(uintptr_t)sites[0].addr;
    g_mtHFree = (MtFreeFn)(uintptr_t)sites[1].addr;
    g_mtHGrow = (MtGrowFn)(uintptr_t)sites[2].addr;
    g_mtHJump = (MtJumpFn)(uintptr_t)sites[3].addr;
    g_mtHOuter = (MtOuterFn)(uintptr_t)sites[4].addr;
    g_mtHOomInner = (MtOomCallFn)(uintptr_t)sites[5].addr;
    g_mtHOomOuter = (MtOomCallFn)(uintptr_t)sites[6].addr;
    MtResetList();
    // gate 0: the stub runs the original, no lock taken
    void* p = g_mtHAlloc(8);
    assert(p == &g_mtNodes[0] && m.lockedCalls == 0);
    g_mtHFree((MtNode*)p);
    assert(MtListLen() == 64 && m.lockedCalls == 0);
    // gate 1: locked, nested (depth 2 inside the stdcall grow), return value and stack intact
    m.locking = 1;
    void* g = g_mtHGrow(3);
    assert(g == (void*)((uintptr_t)&g_mtNodes[0] | 1) && g_mtGrowDepth == 1 && m.lockMaxDepth == 2 && g_mtDepth == 0);
    assert(m.lockedCalls == 7 && m.lockOverflow == 0 && MtListLen() == 64);
    // two threads hammer the list through the hooks: no node handed out twice, none lost
    g_mtDup = g_mtNull = 0;
    HANDLE th[2];
    for (int i = 0; i < 2; i++) th[i] = CreateThread(NULL, 0, MtHammerThread, (void*)(uintptr_t)20000, 0, NULL);
    WaitForMultipleObjects(2, th, TRUE, INFINITE);
    for (int i = 0; i < 2; i++) CloseHandle(th[i]);
    assert(g_mtDup == 0 && g_mtNull == 0 && MtListLen() == 64 && g_mtDepth == 0);
    assert(m.lockedCalls == 7 + 80000);
    printf("mono thread M5: P3 2 x 20000 locked alloc/free, %ld waits, %ld us waited, max %ld us\n", m.lockWaits, m.lockWaitUs, m.lockMaxWaitUs);
    // control, gate 0: the same hammer unlocked shows the race the lock removes (printed, not asserted: timing-dependent)
    m.locking = 0;
    g_mtDup = g_mtNull = 0;
    for (int i = 0; i < 2; i++) th[i] = CreateThread(NULL, 0, MtHammerThread, (void*)(uintptr_t)20000, 0, NULL);
    WaitForMultipleObjects(2, th, TRUE, INFINITE);
    for (int i = 0; i < 2; i++) CloseHandle(th[i]);
    printf("mono thread M5: control without the lock: %ld nodes handed out twice, %ld empty pops, list %d of 64\n", g_mtDup, g_mtNull, MtListLen());
    MtResetList();
    m.locking = 1;
    // uncontended GC lock entry cost (docs/30 §15): one thread, alloc + free pairs through the stubs, gate on vs off; a
    // worker id is set so MtLockEnter reads the thread id as in game (best of 3: the machine may be busy)
    {
        const int N = 2000000;
        LARGE_INTEGER f, t0, t1;
        QueryPerformanceFrequency(&f);
        double best[2] = {1e30, 1e30};
        const DWORD wt = g_mtWorkerTid;
        g_mtWorkerTid = 1;
        for (int rep = 0; rep < 3; rep++)
            for (int on = 0; on < 2; on++) {
                m.locking = on;
                QueryPerformanceCounter(&t0);
                for (int i = 0; i < N; i++) g_mtHFree((MtNode*)g_mtHAlloc(8));
                QueryPerformanceCounter(&t1);
                const double ns = (double)(t1.QuadPart - t0.QuadPart) * 1e9 / (double)f.QuadPart / (2.0 * N);
                if (ns < best[on]) best[on] = ns;
            }
        g_mtWorkerTid = wt;
        m.locking = 1;
        assert(MtListLen() == 64 && g_mtDepth == 0 && g_mtLock.RecursionCount == 0);
        printf("mono thread M5: GC lock uncontended: %.1f ns per hooked call with the gate on, %.1f ns off, +%.1f ns per locked entry (best of 3 x %d pairs)\n",
               best[1], best[0], best[1] - best[0], N);
    }

    // --- 0xE4B3F0 replacement on a fake queue: per-call limit, order, bytes freed meanwhile, ECX kept ---
    DWORD fin[3][4];
    for (int i = 0; i < 3; i++) {
        fin[i][0] = 0x100 + i, fin[i][1] = i < 2 ? (DWORD)(uintptr_t)fin[i + 1] : 0;
        fin[i][2] = (DWORD)(uintptr_t)MtFakeFinalizer, fin[i][3] = 0x10;
    }
    g_mtFinQueue = (DWORD**)&g_mtFinFakeQ;
    g_mtFinLimit = &g_mtFinFakeLim;
    g_mtMemFreed = &g_mtFinFakeFreed;
    g_mtFinFreed = &g_mtFinFakeAcc;
    g_mtFinFakeQ = (DWORD)(uintptr_t)fin[0];
    g_mtFinFakeLim = 2;
    const LONG lc = m.lockedCalls;
    assert(MtCallFinKeepEcx() == 0x1234);
    assert(g_mtFinN == 2 && g_mtFinSeen[0] == (0x100 ^ 0x10) && g_mtFinSeen[1] == (0x101 ^ 0x10) && g_mtFinFakeAcc == 32);
    assert(g_mtFinFakeQ == (DWORD)(uintptr_t)fin[2] && fin[0][1] == 0 && fin[0][3] == 0 && m.finRuns == 2);
    assert(m.lockedCalls == lc);  // C-side lock does not count as a hook entry
    g_mtFinFakeLim = 0;
    assert(MtInvokeFinalizersC() == 1 && g_mtFinFakeQ == 0 && g_mtFinFakeAcc == 48 && m.finRuns == 3);
    assert(MtInvokeFinalizersC() == 0 && m.finRuns == 3);

    // --- I-120-8 (docs/120 §10): a locked hook frame left by longjmp; the next hooked enter/exit drops it ---
    {
        assert(MtJumpOut() == 1 && g_mtDepth == 1 && g_mtLock.RecursionCount == 1 && m.lockStale == 0);  // leaked as before the fix
        void* q = g_mtHAlloc(8);  // enter path: the stale entry is dropped, this call returns to us
        assert(q == &g_mtNodes[0] && g_mtDepth == 0 && m.lockStale == 1 && g_mtLock.RecursionCount == 0 && !g_mtLock.OwningThread);
        g_mtHFree((MtNode*)q);
        HANDLE ot = CreateThread(NULL, 0, MtOtherLocked, NULL, 0, NULL);  // the lock is free for another thread
        assert(WaitForSingleObject(ot, 5000) == WAIT_OBJECT_0);
        DWORD orc = 9;
        GetExitCodeThread(ot, &orc);
        CloseHandle(ot);
        assert(orc == 0);
        // exit path: the inner frame jumps back into the outer hooked frame, which returns through the thunk
        assert(g_mtHOuter(41) == 42 && g_mtDepth == 0 && m.lockStale == 2 && g_mtLock.RecursionCount == 0 && MtListLen() == 64);
        printf("mono thread M5: I-120-8 longjmp out of a locked hook: stale entries dropped on enter and exit (%ld), lock free\n", m.lockStale);
    }

    // --- GC_oom_fn wrapper (docs/120 §10.5): the lock is free during the OOM call, before any further call on this thread ---
    {
        g_mtOomFakeSlot = 0x1234;
        assert(!MtWrapOom(&g_mtOomFakeSlot, (DWORD)(uintptr_t)MtFakeMonoOom) && g_mtOomFakeSlot == 0x1234);  // someone else's handler: left alone
        g_mtOomFakeSlot = (DWORD)(uintptr_t)MtFakeMonoOom;
        assert(MtWrapOom(&g_mtOomFakeSlot, (DWORD)(uintptr_t)MtFakeMonoOom) && g_mtOomFakeSlot == (DWORD)(uintptr_t)MtOomWrap);
        const LONG st = m.lockStale;
        // not the owner: pass-through, nothing released
        assert(((MtOomFn)(uintptr_t)g_mtOomFakeSlot)(8) == NULL && g_mtOomOther == 0 && m.lockOomReleased == 0);
        // longjmp path (interpreter context): 2 locked frames released before the handler, the other thread got the lock in it
        g_mtOomOther = -1;
        assert(MtOomJumpOut() == 1 && g_mtOomOther == 0 && m.lockOomReleased == 2 && m.lockStale == st && g_mtOomOuterBack == 0);
        assert(g_mtDepth == 0 && g_mtLock.RecursionCount == 0 && !g_mtLock.OwningThread);
        // return path (no context): entries taken back, both frames return through their thunks
        g_mtOomOther = -1;
        assert(g_mtHOomOuter(16) == NULL && g_mtOomOther == 0 && m.lockOomReleased == 4 && m.lockStale == st && g_mtOomOuterBack == 1);
        assert(g_mtDepth == 0 && g_mtLock.RecursionCount == 0 && !g_mtLock.OwningThread && MtListLen() == 64);
        void* q = g_mtHAlloc(8);  // later locked calls on this thread still return right
        assert(q == &g_mtNodes[0] && g_mtDepth == 0 && m.lockStale == st);
        g_mtHFree((MtNode*)q);
        printf("mono thread M5: GC_oom_fn wrapper: lock free for another thread inside the OOM call (longjmp and return paths), %ld entries released\n", m.lockOomReleased);
    }
    m.locking = 0;

    // --- P4: the three blocks, copied with their game bytes and patched, run on fake frames ---
    u8* blk = (u8*)VirtualAlloc(NULL, 4096, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
    u8* stubs = blk + 1024;
    DWORD at[3];
    for (int i = 0; i < 3; i++) {
        g_mtBlk[i] = blk + i * 128;
        memcpy(g_mtBlk[i], kMtBlocks[i].bytes, kMtBlocks[i].len);
        u8* tail = g_mtBlk[i] + kMtBlocks[i].len;
        if (i == 0) *tail++ = 0x5B;  // pop ebx: the entry block pushed it
        *tail = 0xC3;
        at[i] = (DWORD)(uintptr_t)g_mtBlk[i];
    }
    const DWORD globals[2] = {0x1111, 0x2222};
    assert(MtBuildP4(stubs, at, globals) && g_mtSlotF < 64 && g_mtSlotA < 64);
    assert(TlsGetValue(g_mtSlotF) == (void*)0x1111 && TlsGetValue(g_mtSlotA) == (void*)0x2222);
    assert(*MtFrameSlot() == 0x1111);  // MtInstallP4 points g_curState here (CanYield after P4)
    for (int i = 0; i < 3; i++) assert(MtJmpTo(at[i], stubs + i * 64, kMtBlocks[i].len));
    // entry: saves the old pair (frame to [ebp-0x60], array in ECX), sets frame = [ebp+8], array = ebp-0x4C74
    static DWORD frameBuf[64];
    const DWORD ebpv = (DWORD)(uintptr_t)(frameBuf + 32);
    frameBuf[34] = 0xF00D;  // [ebp+8]
    assert(MtRunBlock(g_mtBlk[0], ebpv, 0, 0, 0) == 0x2222);
    assert(frameBuf[32 - 0x60 / 4] == 0x1111);
    assert(TlsGetValue(g_mtSlotF) == (void*)0xF00D && TlsGetValue(g_mtSlotA) == (void*)(uintptr_t)(ebpv - 0x4C74));
    // exit A: frame = EAX, array = ECX; exit B: array = EDX, frame = ECX
    MtRunBlock(g_mtBlk[1], 0, 0xAAAA, 0xBBBB, 0);
    assert(TlsGetValue(g_mtSlotF) == (void*)0xAAAA && TlsGetValue(g_mtSlotA) == (void*)0xBBBB);
    MtRunBlock(g_mtBlk[2], 0, 0, 0xCCCC, 0xDDDD);
    assert(TlsGetValue(g_mtSlotF) == (void*)0xCCCC && TlsGetValue(g_mtSlotA) == (void*)0xDDDD);
    HANDLE o = CreateThread(NULL, 0, MtOtherFrames, NULL, 0, NULL);
    WaitForSingleObject(o, INFINITE);
    DWORD rc = 9;
    GetExitCodeThread(o, &rc);
    CloseHandle(o);
    assert(rc == 0 && TlsGetValue(g_mtSlotF) == (void*)0xCCCC && TlsGetValue(g_mtSlotA) == (void*)0xDDDD);
    // 0xE534E0 replacement: depth 1 of top 2 is entry 1; method = rm[2], pos = (ip - rm[0]) >> 1; past the top: no frame
    static u8 fr[0x150], arr[0x130 * 3];
    static DWORD rm[3] = {0x4000, 0, 0x7777}, cell[2] = {0, (DWORD)(uintptr_t)rm};
    *(DWORD*)(fr + 0x140) = 2;
    *(DWORD*)(arr + 0x130) = (DWORD)(uintptr_t)cell;
    *(DWORD*)(arr + 0x134) = 0x4010;
    TlsSetValue(g_mtSlotF, fr);
    TlsSetValue(g_mtSlotA, arr);
    DWORD meth = 1;
    LONG pos = -1;
    assert(MtFrameAt(1, &meth, &pos) && meth == 0x7777 && pos == 8);
    assert(!MtFrameAt(3, &meth, &pos) && meth == 0);
    assert(!MtFrameAt(0, &meth, &pos) && meth == 0);  // entry 2 has no cell
    TlsSetValue(g_mtSlotF, NULL);
    assert(!MtFrameAt(1, &meth, &pos) && meth == 0);
    TlsSetValue(g_mtSlotA, NULL);

    // --- worker: start on the switch, register under the lock, domain set, jobs, exception, tracking rule, stop ---
    memset(g_mtFakeTab, 0, sizeof(g_mtFakeTab));
    g_mtFakeMax = 0;
    g_mtFakeTab[0] = 1, g_mtFakeTab[1] = me, g_mtFakeTab[3] = 0x1000;  // the sim thread, registered
    g_mtSimTid = me;
    g_mtDomIdx = TlsAlloc(), g_mtCtxIdx = TlsAlloc();
    g_mtFakeInterpKey = g_mtCtxIdx;
    g_mtInvoke = MtFakeInvoke, g_mtDomainGet = MtFakeDomainGet, g_mtDomainSet = MtFakeDomainSet;
    g_mtInterpKey = &g_mtFakeInterpKey, g_mtTrackMode = &g_mtFakeTrack;
    Ts3nCounters& k = ts3native_counters;
    const LONG modeSave = k.mtMode;
    MtM5Tick();  // nothing installed: no worker
    assert(!g_mtWorker && m.workerState == 0);
    m.p3Ready = 7, m.p4Ready = 0xF;
    MtM5Tick();  // mode 0: still none
    assert(!g_mtWorker);
    k.mtMode = 1;
    MtM5Tick();
    assert(g_mtWorker && m.locking == 1 && m.workerStarts == 1);
    MtWaitFor(&m.workerState, 2);
    assert(g_mtFakeMax == 1 && g_mtFakeTab[6] == g_mtWorkerTid && g_mtFakeTab[8] != 0 && m.workerCtxSet == 0);
    MtResetList();
    g_mtDup = g_mtNull = 0;
    void* params[1] = {(void*)(uintptr_t)20000};
    assert(MtSubmit((void*)0x40, NULL, params));
    assert(!MtSubmit((void*)0x40, NULL, params));  // busy
    MtHammer(20000);                                // the sim thread allocates at the same time
    MtWaitFor(&m.seq, 1);
    assert(m.result == 0x41 && m.workerJobs == 1 && g_mtDup == 0 && g_mtNull == 0 && MtListLen() == 64);
    assert(m.lockedWorker >= 40000 && g_mtDepth == 0);
    MtWaitFor(&g_mtBusy, 0);
    assert(MtSubmit((void*)0x40, (void*)1, params));  // the invoke reports a managed exception
    MtWaitFor(&m.seq, 2);
    assert(m.workerExceptions == 1 && m.result == 0);
    MtWaitFor(&g_mtBusy, 0);
    g_mtFakeTrack = 1;  // EA memory tracking on: the worker refuses the job
    assert(MtSubmit((void*)0x40, NULL, params));
    MtWaitFor(&g_mtBusy, 0);
    assert(m.workerTrackSkips == 1 && m.seq == 2);
    g_mtFakeTrack = 0;
    k.mtMode = 0;
    MtM5Tick();  // stop: the worker unregisters, then the gate goes off
    assert(!g_mtWorker && m.locking == 0 && m.workerState == 4 && g_mtFakeTab[5] == 0 && g_mtFakeTab[8] == 0);
    k.mtMode = 1;
    MtM5Tick();  // restart on the switch
    MtWaitFor(&m.workerState, 2);
    assert(m.workerStarts == 2 && m.locking == 1);
    // v5 LazyGate (docs/30 §15): gate off while the worker is idle, on from the dispatch until a tick sees the job finished
    g_mtLazyGate = true;
    MtM5Tick();  // worker idle: gate off
    assert(m.locking == 0 && m.lazyOff == 1);
    {
        const LONG lc0 = m.lockedCalls;
        g_mtHFree((MtNode*)g_mtHAlloc(8));
        assert(m.lockedCalls == lc0);  // idle worker: the sim's GC entries run unlocked
        const LONG lw = m.lockedWorker, sq = m.seq;
        g_mtDup = g_mtNull = 0;
        assert(MtSubmit((void*)0x40, NULL, params));
        assert(m.locking == 1 && m.lazyOn == 1);
        MtHammer(20000);  // the sim allocates while the job runs: both sides lock
        MtWaitFor(&m.seq, sq + 1);
        MtWaitFor(&g_mtBusy, 0);
        assert(m.locking == 1 && g_mtDup == 0 && g_mtNull == 0 && MtListLen() == 64 && m.lockedWorker >= lw + 40000 && g_mtDepth == 0);
        MtM5Tick();  // tick boundary, job finished: off again
        assert(m.locking == 0 && m.lazyOff == 2);
        MtM5Tick();  // nothing to do
        assert(m.lazyOff == 2);
        // v6 LazyEarly (docs/30 §16): the sim's first hooked entry after the job (depth 0, worker idle) turns the gate off and
        // runs unlocked, without waiting for the next tick; a job dispatched after it locks both sides again
        g_mtLazyEarly = true;
        for (int j = 0; j < 2; j++) {  // the hammer may outlast the job: then it is the one that turns the gate off
            const LONG eo = m.lazyEarlyOff;
            g_mtDup = g_mtNull = 0;
            assert(MtSubmit((void*)0x40, NULL, params) && m.locking == 1);
            MtHammer(20000);  // locked while the job runs (worker busy: no early off), both sides allocate
            MtWaitFor(&m.seq, sq + 2 + j);
            MtWaitFor(&g_mtBusy, 0);
            assert(g_mtDup == 0 && g_mtNull == 0 && MtListLen() == 64 && g_mtDepth == 0 && m.simLockWaits <= m.lockWaits);
            const LONG lc1 = m.lockedCalls;
            g_mtHFree((MtNode*)g_mtHAlloc(8));  // first sim entry after the job (if the hammer did not get there first)
            assert(m.locking == 0 && m.lazyEarlyOff == eo + 1 && m.lockedCalls == lc1 && g_mtDepth == 0);
        }
        g_mtLazyEarly = false;
    }
    k.mtMode = 0;
    MtM5Tick();  // stop with the lazy gate: the worker unregisters under the lock, then the gate goes off
    assert(!g_mtWorker && m.locking == 0 && m.workerState == 4 && g_mtFakeTab[5] == 0 && g_mtFakeTab[8] == 0);
    g_mtLazyGate = false;
    k.mtMode = 1;
    MtM5Tick();
    MtWaitFor(&m.workerState, 2);
    assert(m.workerStarts == 3 && m.locking == 1);
    k.mtMode = 0;
    MtM5Tick();
    assert(!g_mtWorker && m.locking == 0 && m.workerStopFails == 0);
    printf("mono thread M5: worker 2 jobs, %ld us max, %ld locked calls on the worker\n", m.workerMaxRunUs, m.lockedWorker);

    // arena pool fix (docs/120 §9) on a fake pool: sentinel + nodes (+0 prev, +4 next, +8 chunk), chunk +0 top
    {
        static DWORD sent[2], nodes[5][3], ch[4][8], st[0x150 / 4];
        DWORD** poolSave = g_mtArenaPool;
        DWORD* pool = sent;
        g_mtArenaPool = &pool;
        const int order[5] = {0, 1, 0, 2, 1};  // chunks A B A C B: two duplicates
        DWORD* prev = sent;
        for (int i = 0; i < 5; i++) {
            nodes[i][0] = (DWORD)(uintptr_t)prev, nodes[i][2] = (DWORD)(uintptr_t)ch[order[i]];
            prev[1] = (DWORD)(uintptr_t)nodes[i], prev = nodes[i];
        }
        prev[1] = (DWORD)(uintptr_t)sent, sent[0] = (DWORD)(uintptr_t)prev;
        for (int i = 0; i < 4; i++) ch[i][0] = (DWORD)(uintptr_t)ch[i] + 0xC;  // pooled: top == data start
        const LONG drop0 = m.arenaDropped;
        MtArenaDedupe();
        assert(m.arenaDropped - drop0 == 2 && sent[1] == (DWORD)(uintptr_t)nodes[0] && nodes[0][1] == (DWORD)(uintptr_t)nodes[1] &&
               nodes[1][1] == (DWORD)(uintptr_t)nodes[3] && nodes[3][1] == (DWORD)(uintptr_t)sent && sent[0] == (DWORD)(uintptr_t)nodes[3]);
        ch[0][0] += 0x40, ch[1][0] += 0x40;  // A and B in use by a live state: a grow must not get them
        MtArenaHeal();
        assert(m.arenaDropped - drop0 == 4 && sent[1] == (DWORD)(uintptr_t)nodes[3] && nodes[3][0] == (DWORD)(uintptr_t)sent);
        MtArenaHeal();  // head free: nothing dropped
        assert(m.arenaDropped - drop0 == 4);
        // release-all wrapper: runs the original, then clears +0x13C; counts non-empty chains
        g_mtRelAllOrig = (void*)MtFakeRelAll;
        st[0x13C / 4] = (DWORD)(uintptr_t)ch[2];
        ((void(__cdecl*)(void*))MtArenaReleaseAll)(st);
        assert(st[0x13C / 4] == 0 && g_mtFakeRelAllCalls == 1 && m.arenaRelNonEmpty == 1);
        ((void(__cdecl*)(void*))MtArenaReleaseAll)(st);  // second free of the same state: empty chain now
        assert(st[0x13C / 4] == 0 && g_mtFakeRelAllCalls == 2 && m.arenaRelNonEmpty == 1);
        g_mtArenaPool = poolSave, g_mtRelAllOrig = NULL;
        printf("mono thread arena fix: 2 duplicates dropped at install, 2 in-use chunks skipped, release-all clears +0x13C\n");
    }
    // F1 private call records (docs/120 §9.4) with fakes for 0xE53EB0 / 0xE85040 / GC_malloc_uncollectable
    {
        MtExecFn e0 = g_mtExec;
        MtSigFn s0 = g_mtSig;
        MtUncollFn u0 = g_mtUncoll;
        g_mtExec = MtFakeExec, g_mtSig = MtFakeSig, g_mtUncoll = MtFakeUncoll;
        DWORD ctx[4] = {0, 0, 0xC0C0, 0}, rm1[3] = {0, 0, 0x5000};
        void* exc = NULL;
        assert(MtN2mWorker(rm1, NULL, (void*)0x7000, (void*)0x7100, &exc, (u8*)ctx));
        assert(g_mtFakeExecs == 2 && g_mtFakeRecs[0] != g_mtFakeRecs[1] && exc == (void*)(uintptr_t)0xE0E0 && g_mtN2mDepth == 0 &&
               m.n2mWorker == 2 && m.n2mMaxDepth == 2 && !m.n2mOverflow);
        for (int i = 0; i < 2; i++) {  // cleared after the call, buffer link kept
            const u8* r = g_mtFakeRecs[i];
            assert(*(DWORD*)(r + 0x14) == 0 && *(LONG*)(r + 0x140) == 0 && *(u8* const*)(r + 0x30) != NULL);
        }
        g_mtN2mDepth = 32;
        assert(!MtN2mWorker(rm1, NULL, NULL, NULL, &exc, (u8*)ctx) && m.n2mOverflow == 1);  // full: the caller takes the game's path
        g_mtN2mDepth = 0;
        for (int i = 0; i < 32; i++)
            if (g_mtN2mRec[i]) free(*(void**)(g_mtN2mRec[i] + 0x30)), free(g_mtN2mRec[i]), g_mtN2mRec[i] = NULL;
        g_mtExec = e0, g_mtSig = s0, g_mtUncoll = u0;
        printf("mono thread F1: worker-private call records, nested 2 deep, cleared after each call, overflow refused\n");
    }

    k.mtMode = modeSave;
    TlsFree(g_mtDomIdx), TlsFree(g_mtCtxIdx);
    VirtualFree(code, 0, MEM_RELEASE);
    VirtualFree(blk, 0, MEM_RELEASE);
    const LONG ver = m.version;
    memset(&m, 0, sizeof(m));
    m.version = ver;
}
