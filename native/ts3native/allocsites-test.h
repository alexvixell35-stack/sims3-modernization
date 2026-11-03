// TestAllocSites ([MemDiag] AllocSites, allocsites.cpp, docs/201). Included by selftest.cpp after memexp-test.h.
// Probe-safety rule: every hook runs here on a dummy first. EA heap: four generated entries with the game's exact locked
// prologue (56 57 8B F9 8B B7 E4 04 00 00, then pop edi / pop esi / jmp to a C body with the game's argument count and
// ret N) over a fake heap object; the chained case on a jmp entry. Virtual: the real ntdll stubs of this (dummy) process,
// round trips through VirtualAlloc / VirtualFree / MapViewOfFile / UnmapViewOfFile(Ex), then the original bytes back.
// Also: sampling estimate and exact zero after frees, big blocks, realloc / memalign, reentrancy, 4 threads, output
// lines, Apex stand-down, and the cost per hooked call (printed; docs/201 §5).

static u8 g_astHeap[0x600];  // the prologue reads [heap+0x4E4]
static volatile LONG g_astImplCalls;
static void* AstNew(DWORD n) {  // EA-like block: chunk size word at p-0xC, user pointer 16 bytes into the raw block
    const DWORD cs = n + 0x1B <= 0x10 ? 0x10 : (n + 0x1B) & 0xFFFFFFF0;
    u8* raw = (u8*)HeapAlloc(GetProcessHeap(), 0, cs + 16);
    if (!raw) return NULL;
    *(DWORD*)(raw + 4) = cs | 1;
    return raw + 16;
}
static void AstDel(void* p) { if (p) HeapFree(GetProcessHeap(), 0, (u8*)p - 16); }
// cost runs: a body that costs ~nothing (1024 fixed 128-byte blocks in turn), so the hook's own time is what moves
static u8* g_astPool;
static DWORD g_astPoolI;
static void* __fastcall AstMallocImpl(void*, void*, DWORD n, DWORD) {
    if (g_astPool) return g_astPool + 16 + (g_astPoolI++ & 1023) * 128;
    InterlockedIncrement(&g_astImplCalls);
    return AstNew(n);
}
static void* __fastcall AstMemalignImpl(void*, void*, DWORD n, DWORD, DWORD, DWORD) { return AstNew(n); }
static void* __fastcall AstReallocImpl(void*, void*, void* p, DWORD n, DWORD) {
    void* r = AstNew(n);
    if (p && r) memcpy(r, p, 16), AstDel(p);
    return r;
}
static void __fastcall AstFreeImpl(void*, void*, void* p) { if (!g_astPool) AstDel(p); }
static int __cdecl AstRet7() { return 7; }
typedef int(__cdecl* AstIntFn)();
static AstIntFn g_astChainOrig;
static int __cdecl AstChainHook() { return g_astChainOrig() + 1; }

static u8* g_astCode;
static void AstEntry(int slot, void* impl) {  // game prologue + pop edi + pop esi + jmp impl, 32 bytes per slot
    u8* e = g_astCode + slot * 32;
    memcpy(e, kEaLockedPrologue, sizeof(kEaLockedPrologue));
    e[10] = 0x5F, e[11] = 0x5E, e[12] = 0xE9;
    *(LONG*)(e + 13) = (LONG)((u8*)impl - (e + 17));
}
static EaMallocFn AstMallocAt() { return (EaMallocFn)(void*)(g_astCode + 0); }
static EaMemalignFn AstMemalignAt() { return (EaMemalignFn)(void*)(g_astCode + 32); }
static AsReallocFn AstReallocAt() { return (AsReallocFn)(void*)(g_astCode + 64); }
static EaFreeFn AstFreeAt() { return (EaFreeFn)(void*)(g_astCode + 96); }

// call sites with known return addresses (f0 must fall inside these functions); the volatile store after each call keeps
// the compiler from turning it into a tail jump
static void* volatile g_astSink;
static __declspec(noinline) void* AstCallMalloc(DWORD n) { void* r = AstMallocAt()(g_astHeap, NULL, n, 0); g_astSink = r; return r; }
static __declspec(noinline) void AstCallFree(void* p) { AstFreeAt()(g_astHeap, NULL, p); g_astSink = p; }
static __declspec(noinline) void* AstCallReserve(SIZE_T n) { void* r = VirtualAlloc(NULL, n, MEM_RESERVE, PAGE_NOACCESS); g_astSink = r; return r; }
static __declspec(noinline) void* AstCallMap(HANDLE h, SIZE_T n) { void* r = MapViewOfFile(h, FILE_MAP_WRITE, 0, 0, n); g_astSink = r; return r; }
// a reservation whose stack holds a module base below the caller's frame (an HMODULE in a local, as in Frida's injected
// agent thread 2026-10-26): the frame scan must not read base-5
static __declspec(noinline) void* AstCallReserveWith(DWORD word) {
    volatile DWORD pad[8];
    for (int i = 0; i < 8; i++) pad[i] = word;
    void* r = VirtualAlloc(NULL, 1 << 20, MEM_RESERVE, PAGE_NOACCESS);
    g_astSink = (void*)(uintptr_t)(pad[3] ^ (DWORD)(uintptr_t)r);
    return r;
}
static volatile LONG g_astAvs;
static DWORD g_astAvThread;
static LONG CALLBACK AstAvCount(EXCEPTION_POINTERS* e) {
    if (e->ExceptionRecord->ExceptionCode == EXCEPTION_ACCESS_VIOLATION && GetCurrentThreadId() == g_astAvThread) InterlockedIncrement(&g_astAvs);
    return EXCEPTION_CONTINUE_SEARCH;
}

// the site whose caller frame lies in fn's first 0x60 bytes (with live bytes when live is set)
static int AstFind(LONG kind, void* fn, bool live) {
    const DWORD lo = (DWORD)(uintptr_t)fn;
    for (int i = 1; i < kAsSites; i++)
        if (g_asSite[i].used == 2 && g_asSite[i].kind == kind && g_asSite[i].f0 > lo && g_asSite[i].f0 < lo + 0x60 && (!live || g_asSite[i].live > 0)) return i;
    return -1;
}
static DWORD AstFn(void* f) { return (DWORD)(uintptr_t)f; }
static DWORD WINAPI AstThread(LPVOID) {
    void* keep[64] = {};
    for (int i = 0; i < 20000; i++) {
        const int k = i & 63;
        if (keep[k]) AstCallFree(keep[k]);
        keep[k] = AstCallMalloc(16 + (i * 37) % 3000 + (i % 997 == 0 ? 100000 : 0));
    }
    for (void* p : keep) AstCallFree(p);
    return 0;
}
static double AstNsPerPair(int n) {  // median of 5 runs of n malloc + free pairs on the pool body
    double r[5];
    LARGE_INTEGER f, a, b;
    QueryPerformanceFrequency(&f);
    for (double& x : r) {
        QueryPerformanceCounter(&a);
        for (int i = 0; i < n; i++) AstCallFree(AstCallMalloc(64));
        QueryPerformanceCounter(&b);
        x = (b.QuadPart - a.QuadPart) * 1e9 / f.QuadPart / n;
    }
    for (int i = 0; i < 5; i++) for (int j = i + 1; j < 5; j++) if (r[j] < r[i]) { const double t = r[i]; r[i] = r[j], r[j] = t; }
    return r[2];
}
static double AstUsPerReserve(int n) {
    LARGE_INTEGER f, a, b;
    QueryPerformanceFrequency(&f);
    QueryPerformanceCounter(&a);
    for (int i = 0; i < n; i++) VirtualFree(AstCallReserve(64 * 1024), 0, MEM_RELEASE);
    QueryPerformanceCounter(&b);
    return (b.QuadPart - a.QuadPart) * 1e6 / f.QuadPart / n;
}

// docs/201 §14: VirtualAlloc2 / MapViewOfFile3 (NtAllocateVirtualMemoryEx / NtMapViewOfSectionEx), MEM_COMMIT at NULL
typedef PVOID(WINAPI* AstVa2Fn)(HANDLE, PVOID, SIZE_T, ULONG, ULONG, void*, ULONG);
typedef PVOID(WINAPI* AstMv3Fn)(HANDLE, HANDLE, PVOID, ULONG64, SIZE_T, ULONG, ULONG, void*, ULONG);
static AstVa2Fn g_astVa2;
static AstMv3Fn g_astMv3;
static __declspec(noinline) void* AstCallReserve2(SIZE_T n) { void* r = g_astVa2(GetCurrentProcess(), NULL, n, MEM_RESERVE, PAGE_NOACCESS, NULL, 0); g_astSink = r; return r; }
static __declspec(noinline) void* AstCallMap3(HANDLE h, SIZE_T n) { void* r = g_astMv3(h, GetCurrentProcess(), NULL, 0, n, 0, PAGE_READWRITE, NULL, 0); g_astSink = r; return r; }
static __declspec(noinline) void* AstCallCommit(SIZE_T n) { void* r = VirtualAlloc(NULL, n, MEM_COMMIT, PAGE_READWRITE); g_astSink = r; return r; }

// docs/201 §15: lock-order stress. Every hook entry while other threads hold the PEB lock, the process heap lock, the
// loader lock (LoadLibrary / FreeLibrary map and unmap images), NT heap locks (HeapCreate / large HeapAlloc reserve under
// the heap lock), first TlsSetValue on a high index (heap + PEB lock) on fresh threads, and samples. A hang = deadlock.
typedef void(NTAPI* AstPebFn)();
static AstPebFn g_astPebAcq, g_astPebRel;
static volatile LONG g_astStop, g_astOps;
static DWORD g_astHighTls = TLS_OUT_OF_INDEXES;
static HANDLE g_astMapH;
static DWORD WINAPI AstFresh(void*) {  // a new thread: its first hook entry and its first high-index TlsSetValue
    VirtualFree(AstCallReserve(1 << 20), 0, MEM_RELEASE);
    if (g_astHighTls != TLS_OUT_OF_INDEXES) TlsSetValue(g_astHighTls, (void*)1);
    HeapFree(GetProcessHeap(), 0, HeapAlloc(GetProcessHeap(), 0, 1 << 20));
    InterlockedIncrement(&g_astOps);
    return 0;
}
static DWORD WINAPI AstStress(void* arg) {
    const int role = (int)(uintptr_t)arg;
    for (DWORD i = 0; !g_astStop; i++) {
        if (role == 0 || role == 1) {  // EA heap, reserve, view, private NT heap growth
            AstCallFree(AstCallMalloc(i & 1 ? 96 : 256 * 1024));
            VirtualFree(AstCallReserve(2u << 20), 0, MEM_RELEASE);
            void* m = AstCallMap(g_astMapH, 1u << 20);
            if (m) UnmapViewOfFile(m);
            HANDLE hp = HeapCreate(0, 0, 0);
            if (hp) { HeapAlloc(hp, 0, 2u << 20), HeapAlloc(hp, 0, 64 * 1024); HeapDestroy(hp); }
        } else if (role == 2) {  // fresh threads
            HANDLE t = CreateThread(NULL, 0, AstFresh, NULL, 0, NULL);
            if (t) WaitForSingleObject(t, INFINITE), CloseHandle(t);
        } else if (role == 3) {  // loader lock: image map / unmap
            HMODULE d = LoadLibraryW(L"msimg32.dll");
            if (d) FreeLibrary(d);
        } else if (role == 4) {  // PEB lock -> process heap lock (TlsSetValue's order)
            g_astPebAcq();
            HeapFree(GetProcessHeap(), 0, HeapAlloc(GetProcessHeap(), 0, (i & 1) ? 1u << 20 : 64));
            g_astPebRel();
        } else {  // process heap lock held while hooks run elsewhere
            HeapLock(GetProcessHeap());
            SwitchToThread();
            HeapUnlock(GetProcessHeap());
        }
        InterlockedIncrement(&g_astOps);
    }
    return 0;
}
static HANDLE g_astReady, g_astDone;
static DWORD WINAPI AstFirstEntry(void* ev) {  // waits, then enters a hook for the first time
    SetEvent(g_astReady);  // past the loader's thread init (static TLS block from the process heap), in our own code
    WaitForSingleObject((HANDLE)ev, INFINITE);
    VirtualFree(AstCallReserve(1 << 20), 0, MEM_RELEASE);
    SetEvent(g_astDone);  // the hook entries are through; thread exit (LdrShutdownThread frees TLS on the process heap) is not tested
    return 0;
}

// docs/201 §12.1: game caller -> our heap hook -> EA core -> VirtualAlloc; the site must be (core, game caller), not our hook
static __declspec(noinline) void* AstDummyCore(SIZE_T n) { void* r = VirtualAlloc(NULL, n, MEM_RESERVE, PAGE_NOACCESS); g_astSink = r; return r; }
static __declspec(noinline) void* AstDummyHook(SIZE_T n) { void* r = AstDummyCore(n); g_astSink = (void*)((uintptr_t)r ^ 1); return r; }
static __declspec(noinline) void* AstCallViaHook(SIZE_T n) { void* r = AstDummyHook(n); g_astSink = r; return r; }
static void TestAllocSites() {
    // fixed tables, the dummy entries, this image as "the exe" (frames are taken from its code)
    assert(AsInit());
    g_asDrvName[3] = "VERSION.dll";  // the export directory name of version.dll: a stand-in GPU driver (docs/201 §14)
    g_astCode = (u8*)VirtualAlloc(NULL, 4096, MEM_RESERVE | MEM_COMMIT, PAGE_EXECUTE_READWRITE);
    assert(g_astCode);
    AstEntry(0, (void*)AstMallocImpl), AstEntry(1, (void*)AstMemalignImpl), AstEntry(2, (void*)AstReallocImpl), AstEntry(3, (void*)AstFreeImpl);
    const u8* img = (const u8*)GetModuleHandleW(NULL);
    const IMAGE_NT_HEADERS* nt = (const IMAGE_NT_HEADERS*)(img + ((const IMAGE_DOS_HEADER*)img)->e_lfanew);
    g_asExeBase = (DWORD)(uintptr_t)img;
    g_asTextLo = g_asExeBase + nt->OptionalHeader.BaseOfCode, g_asTextHi = g_asTextLo + nt->OptionalHeader.SizeOfCode;
    g_asWrapLo = g_asWrapHi = 0;
    for (int i = 0; i < 4; i++) g_asHeapAt[i] = AstFn(g_astCode + i * 32);
    g_asN = 64, g_asShift = 26, g_asTop = 8;

    // the cost: dummy pairs before and after the hooks (the same near-free pool body both times; the difference is the hook)
    g_astPool = (u8*)VirtualAlloc(NULL, 1024 * 128 + 16, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
    assert(g_astPool);
    for (int i = 0; i < 1024; i++) *(DWORD*)(g_astPool + 16 + i * 128 - 0xC) = 0x80 | 1;
    u8* const pool = g_astPool;
    AstNsPerPair(20000);
    const double nsOff = AstNsPerPair(400000), usVOff = AstUsPerReserve(2000);
    g_astPool = NULL;

    // install: every heap entry and the ntdll stubs this Windows has; call through them
    assert(AsInstallHeap() == 4);
    const int nv = AsInstallVirtual();
    assert(nv >= 4 && nv <= 7);
    g_asOn = true;
    assert(g_asMalloc && g_asFree && g_asRealloc && g_asMemalign && g_asNtAlloc && g_asNtFree && g_asNtMap && g_asNtUnmap);

    // small blocks: 1 in 64 by address, estimate within 25 %; every sampled one leaves on its free (exact 0)
    {
        static void* p[20000];
        const LONG noFree0 = g_asNoFree, calls0 = g_astImplCalls;
        for (void*& q : p) q = AstCallMalloc(200);
        assert(g_astImplCalls - calls0 == 20000);  // the hook ran the body every time
        const int s = AstFind(AS_HEAP, (void*)AstCallMalloc, true);
        assert(s > 0);
        const double want = 20000.0 * 0xE0;  // chunk size of 200 bytes
        assert(fabs(g_asSite[s].live - want) <= want * 0.25 && g_asSite[s].liveN > 20000 * 3 / 4 && g_asSite[s].liveN < 20000 * 5 / 4);
        assert(g_asSite[s].liveN % 64 == 0);
        for (void* q : p) AstCallFree(q);
        assert(g_asSite[s].live == 0 && g_asSite[s].liveN == 0 && g_asNoFree == noFree0);
        assert(g_asSite[s].allocB == g_asSite[s].freeB && g_asSite[s].allocB > 0);
    }
    // big block: always recorded, exact; memalign; realloc moves the record to the new block
    {
        const LONG blocks0 = g_asBlocks;
        void* b = AstCallMalloc(256 * 1024);
        const int s = AstFind(AS_HEAP, (void*)AstCallMalloc, true);
        const LONG64 live0 = g_asSite[s].live;
        assert(live0 == ((256 * 1024 + 0x1B) & ~15) && g_asSite[s].liveN == 1 && g_asBlocks == blocks0 + 1);
        void* a = AstMemalignAt()(g_astHeap, NULL, 1 << 20, 64, 0, 0);
        assert(g_asBlocks == blocks0 + 2);
        void* r = AstReallocAt()(g_astHeap, NULL, b, 512 * 1024, 0);
        assert(r && g_asSite[s].live == 0 && g_asBlocks == blocks0 + 2);  // b left the malloc site; r is the realloc caller's
        AstCallFree(r), AstFreeAt()(g_astHeap, NULL, a);
        assert(g_asBlocks == blocks0);
    }
    // reentrancy: a call while this thread records is not recorded again
    {
        const LONG64 sampled0 = g_asSampled;
        assert(AsEnter());
        void* b = AstCallMalloc(128 * 1024);
        AsLeave();
        assert(g_asSampled == sampled0);
        AstCallFree(b);  // not in the table: one nofree, nothing subtracted
    }
    // 4 threads alloc / free at once: everything recorded leaves again, no drop, no busy give-up
    {
        const LONG blocks0 = g_asBlocks, drops0 = g_asDrops, busy0 = g_asBusy;
        HANDLE t[4];
        for (HANDLE& h : t) h = CreateThread(NULL, 0, AstThread, NULL, 0, NULL);
        WaitForMultipleObjects(4, t, TRUE, INFINITE);
        for (HANDLE h : t) CloseHandle(h);
        assert(g_asBlocks == blocks0 && g_asDrops == drops0 && g_asBusy == busy0);
    }
    // virtual: a reservation and a file view, each attributed to its caller, gone after the release / unmap
    {
        const LONG miss0 = g_asRgnMiss, rgn0 = g_asRgnN;
        void* v = AstCallReserve(8u << 20);
        const int s = AstFind(AS_VIRT, (void*)AstCallReserve, true);
        assert(v && s > 0 && g_asSite[s].live == 8 << 20 && g_asSite[s].liveN == 1 && g_asRgnN == rgn0 + 1);
        VirtualFree(v, 0, MEM_RELEASE);
        assert(g_asSite[s].live == 0 && g_asRgnN == rgn0 && g_asRgnMiss == miss0);
        HANDLE h = CreateFileMappingW(INVALID_HANDLE_VALUE, NULL, PAGE_READWRITE, 0, 4u << 20, NULL);
        void* m = AstCallMap(h, 4u << 20);
        const int sm = AstFind(AS_MAP, (void*)AstCallMap, true);
        assert(m && sm > 0 && g_asSite[sm].live == 4 << 20 && g_asRgnN == rgn0 + 1);
        UnmapViewOfFile(m);
        assert(g_asSite[sm].live == 0 && g_asRgnN == rgn0);
        if (g_asNtUnmapEx) {  // the Ex path too
            m = AstCallMap(h, 4u << 20);
            const int sx = AstFind(AS_MAP, (void*)AstCallMap, true);  // another caller line = another site
            assert(sx > 0 && g_asSite[sx].live == 4 << 20);
            typedef BOOL(WINAPI * UnmapExFn)(PVOID, ULONG);
            UnmapExFn ux = (UnmapExFn)(void*)GetProcAddress(GetModuleHandleW(L"kernelbase.dll"), "UnmapViewOfFileEx");
            assert(ux && ux(m, 0));
            assert(g_asSite[sx].live == 0);
        }
        // docs/201 §14: the Ex entries (Windows 10 1803+) where kernelbase has their callers
        const HMODULE kb = GetModuleHandleW(L"kernelbase.dll");
        g_astVa2 = (AstVa2Fn)(void*)GetProcAddress(kb, "VirtualAlloc2");
        g_astMv3 = (AstMv3Fn)(void*)GetProcAddress(kb, "MapViewOfFile3");
        if (g_astVa2) {
            assert(g_asNtAllocEx);
            void* v2 = AstCallReserve2(8u << 20);
            const int s2 = AstFind(AS_VIRT, (void*)AstCallReserve2, true);
            assert(v2 && s2 > 0 && g_asSite[s2].live == 8 << 20 && g_asRgnN == rgn0 + 1);
            VirtualFree(v2, 0, MEM_RELEASE);
            assert(g_asSite[s2].live == 0 && g_asRgnN == rgn0);
        }
        if (g_astMv3) {
            assert(g_asNtMapEx);
            void* m3 = AstCallMap3(h, 4u << 20);
            const int s3 = AstFind(AS_MAP, (void*)AstCallMap3, true);
            assert(m3 && s3 > 0 && g_asSite[s3].live == 4 << 20 && g_asRgnN == rgn0 + 1);
            UnmapViewOfFile(m3);
            assert(g_asSite[s3].live == 0 && g_asRgnN == rgn0);
        }
        // MEM_COMMIT alone at NULL reserves implicitly: recorded; a commit inside a recorded reserve is not a new region
        void* c = AstCallCommit(16u << 20);
        const int sc = AstFind(AS_VIRT, (void*)AstCallCommit, true);
        assert(c && sc > 0 && g_asSite[sc].live == 16 << 20 && g_asRgnN == rgn0 + 1);
        void* r = AstCallReserve(8u << 20);
        assert(r && VirtualAlloc(r, 64 * 1024, MEM_COMMIT, PAGE_READWRITE) == r && g_asRgnN == rgn0 + 2);
        VirtualFree(r, 0, MEM_RELEASE), VirtualFree(c, 0, MEM_RELEASE);
        assert(g_asSite[sc].live == 0 && g_asRgnN == rgn0);
        CloseHandle(h);
        assert(g_asRgnMiss == miss0);
    }
    // docs/201 §12.1: a frame in our own image (the hook) is skipped: at = the core (allocator range), from = the game caller
    {
        g_asWrapLo = AstFn((void*)AstDummyCore), g_asWrapHi = g_asWrapLo + 0x40;  // shortcut: function sizes assumed < 0x40
        g_asSelfLo = AstFn((void*)AstDummyHook), g_asSelfHi = g_asSelfLo + 0x40;
        void* v = AstCallViaHook(1 << 20);
        int s = 0;
        for (int i = 1; i < kAsSites && !s; i++)
            if (g_asSite[i].used == 2 && g_asSite[i].kind == AS_VIRT && g_asSite[i].live > 0 && AsInWrap(g_asSite[i].f0)) s = i;
        const DWORD c = AstFn((void*)AstCallViaHook);
        assert(v && s > 0 && g_asSite[s].f1 > c && g_asSite[s].f1 < c + 0x60);
        VirtualFree(v, 0, MEM_RELEASE);
        g_asWrapLo = g_asWrapHi = g_asSelfLo = g_asSelfHi = 0;
    }
    // docs/201 §7.2: no first-chance AV in a hook when a stack word is a module base (DLL HMODULE, this exe's base) or
    // a header address; the reservation is still attributed to its caller
    {
        HMODULE dll = LoadLibraryW(L"version.dll");  // not ntdll / kernelbase / kernel32 (those are skipped as system frames)
        assert(dll);
        PVOID veh = AddVectoredExceptionHandler(1, AstAvCount);
        g_astAvThread = GetCurrentThreadId(), g_astAvs = 0;
        const DWORD words[] = {(DWORD)(uintptr_t)dll, (DWORD)(uintptr_t)dll + 0x40, g_asExeBase, g_asExeBase + 0x80};
        for (DWORD w : words) {
            void* v = AstCallReserveWith(w);
            assert(v && AstFind(AS_VIRT, (void*)AstCallReserveWith, true) > 0);
            VirtualFree(v, 0, MEM_RELEASE);
        }
        RemoveVectoredExceptionHandler(veh);
        assert(g_astAvs == 0);
        // docs/67 §21: a cached module row outlives FreeLibrary. A stack word in the unloaded module's old code range must
        // not be read (0.36.85: 16 first-chance AVs per load at AsCallBefore, base-5 of a DLL loaded where one had been);
        // the stale row is dropped
        {
            HMODULE gone = LoadLibraryW(L"msimg32.dll");  // not loaded by the selftest otherwise
            assert(gone);
            const DWORD gb = (DWORD)(uintptr_t)gone;
            DWORD cLo = 0, cHi = 0;
            assert(AsModOf(gb + 0x40, &cLo, &cHi) == gb && cHi > cLo + 0x40);
            const DWORD c = cLo + 0x20;
            assert(FreeLibrary(gone) && !GetModuleHandleW(L"msimg32.dll"));
            veh = AddVectoredExceptionHandler(1, AstAvCount);
            g_astAvs = 0;
            assert(!AsCodeRetSafe(c, gb, cLo, cHi));
            void* v = AstCallReserveWith(c);
            assert(v);
            VirtualFree(v, 0, MEM_RELEASE);
            DWORD l2 = 0, h2 = 0;
            assert(AsModOf(c, &l2, &h2) != gb);
            RemoveVectoredExceptionHandler(veh);
            assert(g_astAvs == 0);
        }
        // the stand-in driver module was named from its export directory; a 64 MB private reserve recorded with the driver
        // flag stays out of miRes, the same reserve without it is a mimalloc-sized arena
        assert(AsInDrv((DWORD)(uintptr_t)GetProcAddress(dll, "GetFileVersionInfoW")) && !AsInDrv(AstFn((void*)AstCallReserve)));
        void* a = AstCallReserve(64u << 20);
        assert(a && !AsIsDriverRegion((DWORD)(uintptr_t)a));
        UdMemStat ms = {};
        ms.gBase = a, ms.gRes = 64ull << 20, ms.gPriv = true;
        UdMemGroupEnd(ms);
        assert(ms.miRes == 64ull << 20);
        const int j = AsRgnFind(g_asRgn, (DWORD)(uintptr_t)a);
        assert(j >= 0);
        g_asRgn[j].kind |= kAsDrv;
        assert(AsIsDriverRegion((DWORD)(uintptr_t)a));
        ms = {}, ms.gBase = a, ms.gRes = 64ull << 20, ms.gPriv = true;
        UdMemGroupEnd(ms);
        assert(ms.miRes == 0);
        VirtualFree(a, 0, MEM_RELEASE);
    }
    // output: totals, live and grow rows, regions in the previous largest free block, free-block neighbours
    {
        wchar_t tmp[MAX_PATH], lp[MAX_PATH];
        GetTempPathW(MAX_PATH, tmp);
        swprintf_s(lp, L"%sts3n-astest-%lu.log", tmp, GetCurrentProcessId());
        HANDLE h = CreateFileW(lp, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ, NULL, CREATE_ALWAYS, FILE_FLAG_DELETE_ON_CLOSE, NULL);
        assert(h != INVALID_HANDLE_VALUE);
        const HANDLE saved = g_log;
        g_log = h;
        static char buf[65536];
        void* keep = AstCallMalloc(2u << 20);
        AsAfterSample(7, "mark", "cyc-full");
        void* v = AstCallReserve(16u << 20);
        void* more = AstCallMalloc(3u << 20);
        AsAfterSample(8, "mark", "cyc-empty");
        void* again = AstCallMalloc(5u << 20);  // the next cycle's full mark: cyc rows against #7
        AsAfterSample(9, "mark", "cyc-full");
        // docs/201 §14: a tracked region whose base lies below the previous window but overlaps it is an inbig row
        void* ov = AstCallReserve(8u << 20);
        g_asWinLo = (DWORD)(uintptr_t)ov + (4u << 20), g_asWinHi = g_asWinLo + (64u << 20);
        AsAfterSample(10, "mark", "ov");
        // an untracked allocation (made with the reentrancy flag set: no record) made after #10, inside the window
        assert(AsEnter());
        void* un = VirtualAlloc(NULL, 4u << 20, MEM_RESERVE, PAGE_NOACCESS);
        AsLeave();
        assert(un);
        g_asWinLo = (DWORD)(uintptr_t)un + (1u << 20), g_asWinHi = g_asWinLo + (1u << 20);
        AsAfterSample(11, "mark", "un");
        UdtLog(buf, sizeof(buf), h);
        g_log = saved;
        CloseHandle(h);
        assert(strstr(buf, "allocsites: #7 mark ev=cyc-full sum heapMB=") && strstr(buf, "allocsites: #7 mark ev=cyc-full live r=1 k="));
        assert(strstr(buf, "allocsites: #8 mark ev=cyc-empty grow r=1 k=") && strstr(buf, " dMB=+"));
        assert(strstr(buf, "allocsites: #8 mark ev=cyc-empty free r=1 base=0x") && strstr(buf, " below=") && strstr(buf, " above="));
        assert(strstr(buf, "k=virtual at=0x") && strstr(buf, "k=heap at=0x"));
        assert(strstr(buf, "allocsites: #9 mark ev=cyc-full cyc r=1 k=") && strstr(buf, " since=#7") && !strstr(buf, "#8 mark ev=cyc-empty cyc "));
        assert(strstr(buf, "allocsites: #10 mark ev=ov inbig r=1 k=virtual at=0x"));
        char want[96];
        sprintf_s(want, " type=private base=0x%08lX MB=4.00 comMB=0.00 ", (DWORD)(uintptr_t)un);
        const char* ur = strstr(buf, want);
        const char* uw = ur ? strstr(ur, " owner=? first=#11 new=1 win=in") : NULL;
        assert(uw && uw < strchr(ur, '\n'));
        assert(strstr(buf, "allocsites: #11 mark ev=un usum private=") && strstr(buf, "owner=ts3native-allocsites first=#7 new=0"));
        assert(strstr(buf, " siteFull=0 "));
        for (char* s = buf; *s;) {  // every line fits the Log buffer
            char* e = strchr(s, '\n');
            assert(e && e - s < 510);
            s = e + 1;
        }
        AstCallFree(keep), AstCallFree(more), AstCallFree(again), VirtualFree(v, 0, MEM_RELEASE), VirtualFree(ov, 0, MEM_RELEASE);
        VirtualFree(un, 0, MEM_RELEASE);
        g_asWinLo = g_asWinHi = 0;
    }
    // docs/201 §15: a thread's first hook entry while another thread holds the PEB lock / the process heap lock returns
    // (0.36.87: TlsSetValue on the game's index >= 64 blocked on both), then the lock-order stress with samples
    {
        const HMODULE ntm = GetModuleHandleW(L"ntdll.dll");
        g_astPebAcq = (AstPebFn)(void*)GetProcAddress(ntm, "RtlAcquirePebLock");
        g_astPebRel = (AstPebFn)(void*)GetProcAddress(ntm, "RtlReleasePebLock");
        assert(g_astPebAcq && g_astPebRel);
        for (int lockKind = 0; lockKind < 2; lockKind++) {
            HANDLE ev = CreateEventW(NULL, TRUE, FALSE, NULL);
            g_astReady = CreateEventW(NULL, TRUE, FALSE, NULL), g_astDone = CreateEventW(NULL, TRUE, FALSE, NULL);
            HANDLE t = CreateThread(NULL, 0, AstFirstEntry, ev, 0, NULL);
            assert(ev && g_astReady && g_astDone && t);
            // the thread is through its start-up before the lock is taken: LdrpAllocateTls takes the process heap lock in
            // the new thread (crashreport.cpp thread_local), so a fixed Sleep(20) failed on a loaded CI runner (not the hook)
            const DWORD rw = WaitForSingleObject(g_astReady, 30000);
            assert(rw == WAIT_OBJECT_0);
            CloseHandle(g_astReady);
            if (lockKind) HeapLock(GetProcessHeap()); else g_astPebAcq();
            SetEvent(ev);
            // wait for the hook, not the thread's exit: exit frees its TLS on the process heap, so under HeapLock it waited
            // for our unlock whenever that free missed the LFH (the flake: 3 s timeout although the hook had returned)
            const DWORD w = WaitForSingleObject(g_astDone, 3000);
            if (lockKind) HeapUnlock(GetProcessHeap()); else g_astPebRel();
            if (w != WAIT_OBJECT_0) printf("allocsites FAIL: first hook entry blocked on the %s lock held elsewhere\n", lockKind ? "process heap" : "PEB");
            assert(w == WAIT_OBJECT_0);
            WaitForSingleObject(t, INFINITE), CloseHandle(t), CloseHandle(ev), CloseHandle(g_astDone);
        }
        DWORD idx[80];
        int ni = 0;
        while (ni < 80 && (idx[ni] = TlsAlloc()) != TLS_OUT_OF_INDEXES && idx[ni] < 64) ni++;
        if (ni < 80 && idx[ni] != TLS_OUT_OF_INDEXES) g_astHighTls = idx[ni];  // >= 64, like the game's
        g_astMapH = CreateFileMappingW(INVALID_HANDLE_VALUE, NULL, PAGE_READWRITE, 0, 1u << 20, NULL);
        wchar_t tmp[MAX_PATH], lp[MAX_PATH];
        GetTempPathW(MAX_PATH, tmp);
        swprintf_s(lp, L"%sts3n-astest-stress-%lu.log", tmp, GetCurrentProcessId());
        HANDLE lh = CreateFileW(lp, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ, NULL, CREATE_ALWAYS, FILE_FLAG_DELETE_ON_CLOSE, NULL);
        const HANDLE saved = g_log;
        g_log = lh;
        g_astStop = 0, g_astOps = 0;
        HANDLE th[7];
        const int roles[7] = {0, 1, 2, 2, 3, 4, 5};
        for (int i = 0; i < 7; i++) th[i] = CreateThread(NULL, 0, AstStress, (void*)(uintptr_t)roles[i], 0, NULL);
        const DWORD t0 = GetTickCount();
        LONG nS = 100;
        while (GetTickCount() - t0 < 1500) AsAfterSample(nS++, "stress", nS & 1 ? "a" : "b");  // samples walk + VirtualQuery + Log
        g_astStop = 1;
        const DWORD w = WaitForMultipleObjects(7, th, TRUE, 20000);
        g_log = saved;
        if (w == WAIT_TIMEOUT) printf("allocsites FAIL: lock-order stress hung (deadlock), %ld ops\n", g_astOps);
        assert(w != WAIT_TIMEOUT);
        for (HANDLE h : th) CloseHandle(h);
        CloseHandle(lh), CloseHandle(g_astMapH);
        for (int i = 0; i <= ni && i < 80; i++) if (idx[i] != TLS_OUT_OF_INDEXES) TlsFree(idx[i]);
        assert(g_astOps > 100 && nS > 101);
        printf("allocsites lock-order stress: %ld ops on 7 threads + %ld samples in 1.5 s, no hang (busy %ld, drops %ld, high TLS index %lu)\n",
               g_astOps, nS - 100, g_asBusy, g_asDrops, g_astHighTls);
        g_asWinLo = g_asWinHi = 0;
    }
    // chained: an entry that is already a jmp (another tool's detour) keeps working through ours
    {
        u8* e = g_astCode + 256;
        e[0] = 0xE9, *(LONG*)(e + 1) = (LONG)((u8*)(void*)AstRet7 - (e + 5));
        g_astChainOrig = (AstIntFn)AsDetour("selftest chain", AstFn(e), kEaLockedPrologue, (int)sizeof(kEaLockedPrologue), (void*)AstChainHook, &g_astChainOrig);
        assert(g_astChainOrig && ((AstIntFn)(void*)e)() == 8 && g_astChainOrig() == 7);
        // bytes that are neither the prologue nor a jmp: refused, nothing written
        u8* f = g_astCode + 288;
        memset(f, 0x90, 16);
        assert(!AsDetour("selftest other bytes", AstFn(f), kEaLockedPrologue, (int)sizeof(kEaLockedPrologue), (void*)AstChainHook, &g_astChainOrig) && f[0] == 0x90 && g_astChainOrig);  // refused: slot kept
    }

    // cost with the hooks in: 1 in 64 sampled (the default) and every block sampled (worst case)
    g_astPool = pool;
    const double nsOn = AstNsPerPair(400000);
    g_asN = 1, g_asShift = 32;
    const double nsAll = AstNsPerPair(100000);
    g_astPool = NULL;
    g_asN = 64, g_asShift = 26;
    const double usVOn = AstUsPerReserve(2000);

    // Apex loaded + ApexStandDown: nothing installed
    {
        wchar_t tmp[MAX_PATH], ini[MAX_PATH];
        GetTempPathW(MAX_PATH, tmp);
        swprintf_s(ini, L"%sts3n-astest-%lu.ini", tmp, GetCurrentProcessId());
        WritePrivateProfileStringW(L"MemDiag", L"AllocSites", L"1", ini);
        const int found0 = g_apexFound, patches0 = g_asPatchN;
        const bool sd0 = g_apexStandDown;
        g_apexFound = 1, g_apexStandDown = true;
        assert(!ApplyAllocSites(ini) && g_asPatchN == patches0);
        g_apexFound = found0, g_apexStandDown = sd0;
        DeleteFileW(ini);
    }

    AsUnhookAll();
    g_asOn = false;
    assert(((const u8*)(void*)AstMallocAt())[0] == 0x56 && ((const u8*)GetProcAddress(GetModuleHandleW(L"ntdll.dll"), "NtFreeVirtualMemory"))[0] == 0xB8);
    printf("allocsites cost: EA heap malloc+free pair %.1f ns unhooked, %.1f ns hooked 1 in 64 (+%.1f), %.1f ns every block sampled (+%.1f); "
           "VirtualAlloc reserve+release 64 KB %.2f us -> %.2f us (+%.2f); ntdll hooks %d\n",
           nsOff, nsOn, nsOn - nsOff, nsAll, nsAll - nsOff, usVOff, usVOn, usVOn - usVOff, nv);
}
