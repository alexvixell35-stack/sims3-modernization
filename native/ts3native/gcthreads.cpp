// GC allocation thread check (docs/29 §4.5). Included by ts3native.cpp (one translation unit). Measurement only.
//
// Boehm's GC here registers one thread (GC_new_thread 0xE51070, called once from GC_thr_init) and its GC_malloc fast
// path 0xE4BE30 takes no lock [verified, docs/29]. If any other thread allocates from the GC heap, that is a latent
// heap-corruption bug; static analysis could not rule it out (every candidate path is a vtable or job runnable).
// We detour the two slow paths, GC_allocobj 0xE4A2B0 and GC_alloc_large 0xE4BA90 (every thread that allocates reaches
// them once its free list runs dry), note the first thread as the owner and count calls from any other thread.
// shortcut: fast-path-only allocations are not seen; a thread that allocates often still shows up on its first refill.

static const DWORD GT_ALLOCOBJ = 0x00E4A2B0, GT_ALLOCLARGE = 0x00E4BA90, GT_TABLE0 = 0x01221004;
static const u8 kGtAllocObj[5] = {0x53, 0x8B, 0x5C, 0x24, 0x08};          // push ebx; mov ebx,[esp+8]
static const u8 kGtAllocLarge[6] = {0x51, 0x53, 0x8B, 0x5C, 0x24, 0x0C};  // push ecx; push ebx; mov ebx,[esp+0xC]

static void* g_gtObjOrig;
static void* g_gtLargeOrig;

static void __cdecl GtNote(DWORD ret, DWORD which) {
    Ts3nCounters& k = ts3native_counters;
    const DWORD tid = GetCurrentThreadId();
    if (!k.gcOwnerTid && InterlockedCompareExchange(&k.gcOwnerTid, (LONG)tid, 0) == 0) {
        const DWORD* t = (const DWORD*)(uintptr_t)GT_TABLE0;
        if (Readable(t, 16))
            Log("gc threads: first allocating thread %lu (main %lu); GC thread table entry 0: %08X %08X %08X %08X", tid,
                g_mainTid, t[0], t[1], t[2], t[3]);
        return;
    }
    if ((LONG)tid == k.gcOwnerTid) return;
    k.gcForeignTid = (LONG)tid;
    if (InterlockedIncrement(&k.gcForeignAllocs) <= 8)
        Log("gc threads: FOREIGN %s from thread %lu (owner %ld), return 0x%08X", which ? "GC_alloc_large" : "GC_allocobj", tid,
            k.gcOwnerTid, ret);
}

// Entry of either slow path: [esp] = caller's return address. Registers and flags kept, then the game's code.
static __declspec(naked) void GtObjStub() {
    __asm {
        pushad
        pushfd
        push 0
        push dword ptr [esp + 40]
        call GtNote
        add esp, 8
        popfd
        popad
        jmp dword ptr [g_gtObjOrig]
    }
}
static __declspec(naked) void GtLargeStub() {
    __asm {
        pushad
        pushfd
        push 1
        push dword ptr [esp + 40]
        call GtNote
        add esp, 8
        popfd
        popad
        jmp dword ptr [g_gtLargeOrig]
    }
}

// [GcThreadCheck] Enabled=1 installs both detours (measurement only, no behaviour change).
static bool ApplyGcThreadCheck(const wchar_t* ini) {
    if (!GetPrivateProfileIntW(L"GcThreadCheck", L"Enabled", 0, ini)) { Log("gc thread check disabled (Enabled=0)"); return false; }
    g_gtObjOrig = InstallDetour("GC_allocobj (gc thread check)", GT_ALLOCOBJ, kGtAllocObj, sizeof(kGtAllocObj), (void*)GtObjStub, &g_gtObjOrig);
    g_gtLargeOrig = InstallDetour("GC_alloc_large (gc thread check)", GT_ALLOCLARGE, kGtAllocLarge, sizeof(kGtAllocLarge), (void*)GtLargeStub, &g_gtLargeOrig);
    ts3native_counters.gcCheckReady = (g_gtObjOrig ? 1 : 0) | (g_gtLargeOrig ? 2 : 0);
    return g_gtObjOrig || g_gtLargeOrig;
}
