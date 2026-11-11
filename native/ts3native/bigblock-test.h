// TestBigBlock ([BigBlock] bigblock.cpp, docs/543). Included by selftest.cpp. The stubs run against this process's own
// VirtualAlloc / VirtualFree (through fake import slots), on real reservations, with EA's header written as 0x4E50F0
// writes it; the patch windows are copies of the game's bytes in local buffers; the heap is a fake. No game code runs.
static DWORD g_bbtLastType;
static LONG g_bbtVa, g_bbtVf;
static LPVOID WINAPI BbtVa(LPVOID a, SIZE_T n, DWORD t, DWORD p) {
    InterlockedIncrement(&g_bbtVa);
    if (!a) g_bbtLastType = t;
    return VirtualAlloc(a, n, t, p);
}
static BOOL WINAPI BbtVf(LPVOID a, SIZE_T n, DWORD t) { InterlockedIncrement(&g_bbtVf); return VirtualFree(a, n, t); }
static void* g_bbtVaSlot = (void*)&BbtVa;
static void* g_bbtVfSlot = (void*)&BbtVf;

// As 0x4E50F0 after its VirtualAlloc: n = the bytes it asked for (page multiple); header at the base.
static u8* BbtEaAlloc(DWORD n, DWORD type = MEM_RESERVE | MEM_COMMIT) {
    u8* p = (u8*)BbAlloc(NULL, n, type, PAGE_READWRITE);
    if (p) *(DWORD*)p = 0, *(DWORD*)(p + 4) = (n - 0x10) | 2;
    return p;
}
static BOOL BbtEaFree(void* p) { return BbFree(p, 0, MEM_RELEASE); }  // as 0x4E5306
static DWORD BbtState(const void* p) {
    MEMORY_BASIC_INFORMATION m;
    return VirtualQuery(p, &m, sizeof(m)) ? m.State : 0;
}
static void BbtReset(DWORD budgetMB, DWORD blockMB) {  // empty cache (reservations released), empty map, counters 0
    AcquireSRWLockExclusive(&g_bbLock);
    while (g_bbCacheN) VirtualFree((void*)(uintptr_t)BbPopOldest().base, 0, MEM_RELEASE);
    memset(g_bbMap, 0, 0x10000 * sizeof(DWORD));
    ReleaseSRWLockExclusive(&g_bbLock);
    memset((void*)&g_bb, 0, sizeof(g_bb));
    g_bbBudget = budgetMB << 20, g_bbMaxBlock = blockMB << 20;
}
static DWORD BbtMapCount(DWORD flag) {  // map entries with exactly this flag state (0 = live)
    DWORD n = 0;
    for (DWORD i = 0; i < 0x10000; i++) n += g_bbMap[i] && (g_bbMap[i] & BB_M_FLAGS) == flag;
    return n;
}

// fake EA Malloc for the Mono wrapper: thiscall (size, flags), ret 8, as __fastcall (ecx, edx, size, flags)
static LONG g_bbtSawTid;
static void* g_bbtSawHeap;
static DWORD g_bbtSawN, g_bbtSawF, g_bbtEspDelta;
static void* __fastcall BbtMalloc(void* heap, void*, DWORD n, DWORD f) {
    g_bbtSawTid = g_bbMonoTid, g_bbtSawHeap = heap, g_bbtSawN = n, g_bbtSawF = f;
    return (void*)(uintptr_t)0x12340000;
}
// calls BbMonoMalloc as 0xD71553 does (ecx = heap, size and flags pushed) and records how far esp moved
static __declspec(naked) void* __cdecl BbtCallMono(void* /*heap*/, DWORD /*n*/, DWORD /*f*/) {
    __asm {
        push ebx
        mov ebx, esp
        mov ecx, [esp + 8]
        push dword ptr [esp + 10h]    // flags
        push dword ptr [esp + 10h]    // size
        call BbMonoMalloc
        mov edx, ebx
        sub edx, esp
        mov g_bbtEspDelta, edx        // 0 when it popped its two arguments
        mov esp, ebx
        pop ebx
        ret
    }
}

static DWORD WINAPI BbtWorker(void* seed) {
    DWORD s = (DWORD)(uintptr_t)seed;
    static const DWORD kSizes[5] = {0x10000, 0x20000, 0x41000, 0x80000, 0x100000};
    u8* live[4] = {};
    for (int i = 0; i < 3000; i++) {
        s = s * 1103515245u + 12345u;
        u8*& slot = live[(s >> 8) & 3];
        if (slot) assert(BbtEaFree(slot)), slot = NULL;
        else assert((slot = BbtEaAlloc(kSizes[(s >> 12) % 5])) != NULL), slot[0x10] = 1;
    }
    for (u8* p : live)
        if (p) assert(BbtEaFree(p));
    return 0;
}

static void TestBigBlock() {
    void* const* const va0 = g_bbVaIat;
    void* const* const vf0 = g_bbVfIat;
    u8* const* const heap0 = g_bbHeapSlot;
    const DWORD malloc0 = g_bbMonoMalloc, budget0 = g_bbBudget, block0 = g_bbMaxBlock;
    const int found0 = g_apexFound;
    const bool sd0 = g_apexStandDown;
    g_bbVaIat = &g_bbtVaSlot, g_bbVfIat = &g_bbtVfSlot;
    assert(BbMapInit());

    // 1. not armed: both stubs pass straight through, nothing recorded
    g_bbArm = 0;
    u8* p = BbtEaAlloc(0x40000);
    assert(p && BbtState(p) == MEM_COMMIT && !g_bbMap[(DWORD)(uintptr_t)p >> 16] && !g_bb.allocs);
    assert(BbtEaFree(p) && BbtState(p) == MEM_FREE && !g_bb.frees);

    // 2. miss, then the free keeps the reservation (decommitted) and the same-size request gets it back, zeroed
    g_bbArm = BB_REUSE | BB_MONO, g_bbMonoTid = 0;
    BbtReset(4, 1);
    u8* a = BbtEaAlloc(0x40000);
    assert(a && g_bb.misses == 1 && g_bb.hits == 0 && g_bbMap[(DWORD)(uintptr_t)a >> 16] == 0x40000);
    memset(a + 8, 0xAB, 0x40000 - 8);
    assert(BbtEaFree(a) && BbtState(a) == MEM_RESERVE && g_bbCacheN == 1 && g_bbCacheBytes == 0x40000 && BbtMapCount(BB_M_CACHED) == 1);
    u8* b = (u8*)BbAlloc(NULL, 0x40000, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
    assert(b == a && g_bb.hits == 1 && g_bbCacheN == 0 && BbtState(b) == MEM_COMMIT && b[8] == 0 && b[0x3FFFF] == 0);
    *(DWORD*)b = 0, *(DWORD*)(b + 4) = (0x40000 - 0x10) | 2;

    // 3. same 64 KB class, shorter request: served from the longer reservation; the region size stays
    assert(BbtEaFree(b) && g_bbCacheN == 1);
    u8* c = BbtEaAlloc(0x3F000);
    assert(c == a && g_bb.hits == 2 && g_bbMap[(DWORD)(uintptr_t)c >> 16] == 0x40000);
    assert(BbtEaFree(c) && g_bbCacheN == 1 && g_bbCacheBytes == 0x40000);
    u8* d = BbtEaAlloc(0x50000);  // another class: a fresh block, the cached one stays
    assert(d && d != a && g_bb.misses == 2 && g_bbCacheN == 1);
    assert(BbtEaFree(d) && g_bbCacheN == 2 && g_bbCacheBytes == 0x90000);

    // 4. double free of a cached block: refused, the reservation stays ours
    assert(!BbtEaFree(a) && GetLastError() == ERROR_INVALID_ADDRESS && g_bb.dfree == 1 && BbtState(a) == MEM_RESERVE && g_bbCacheN == 2);

    // 5. unknown addresses pass through: a block made before arming, and sizes outside 64 KB-ReuseMaxBlockMB
    u8* f = (u8*)VirtualAlloc(NULL, 0x40000, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
    assert(BbtEaFree(f) && BbtState(f) == MEM_FREE && g_bb.pass == 1);
    u8* s1 = BbtEaAlloc(0x8000);
    u8* s2 = BbtEaAlloc(0x200000);
    assert(s1 && s2 && !g_bbMap[(DWORD)(uintptr_t)s1 >> 16] && !g_bbMap[(DWORD)(uintptr_t)s2 >> 16]);
    assert(BbtEaFree(s1) && BbtEaFree(s2) && BbtState(s1) == MEM_FREE && BbtState(s2) == MEM_FREE && g_bb.pass == 3 && g_bbCacheN == 2);
    // other shapes than 0x4E5306's go straight to VirtualFree
    u8* g = BbtEaAlloc(0x40000);
    assert(g == a && BbFree(g + 0x1000, 0x1000, MEM_DECOMMIT) && g_bb.frees == 8 && BbtState(g + 0x1000) == MEM_RESERVE && BbtMapCount(0) == 1);
    *(DWORD*)(g + 4) = 0xDEAD;  // header no longer EA's: released, not cached
    assert(BbtEaFree(g) && g_bb.bad == 1 && BbtState(g) == MEM_FREE && !g_bbMap[(DWORD)(uintptr_t)g >> 16] && g_bbCacheN == 1);

    // 6. budget: 4 MB, 1 MB blocks, six freed: the four newest stay, the two oldest are released
    BbtReset(4, 1);
    u8* m[6];
    for (u8*& x : m) assert((x = BbtEaAlloc(0x100000)) != NULL);
    for (u8* x : m) assert(BbtEaFree(x));
    assert(g_bbCacheN == 4 && g_bbCacheBytes == 4u << 20 && g_bb.evicts == 2 && BbtState(m[0]) == MEM_FREE && BbtState(m[1]) == MEM_FREE);
    for (int i = 2; i < 6; i++) assert(BbtState(m[i]) == MEM_RESERVE && g_bbMap[(DWORD)(uintptr_t)m[i] >> 16] == (0x100000 | BB_M_CACHED));
    assert(BbtEaAlloc(0x100000) == m[5] && BbtEaFree(m[5]) && g_bbCacheN == 4);  // newest first

    // 7. table full: 260 blocks of 64 KB under a 64 MB budget: BB_CACHE_N stay, the oldest four are released
    BbtReset(64, 1);
    static u8* t[260];
    for (u8*& x : t) assert((x = BbtEaAlloc(0x10000)) != NULL);
    for (u8* x : t) assert(BbtEaFree(x));
    assert(g_bbCacheN == BB_CACHE_N && g_bb.evicts == 4 && BbtState(t[3]) == MEM_FREE && BbtState(t[4]) == MEM_RESERVE);
    assert(BbtMapCount(BB_M_CACHED) == BB_CACHE_N && BbtMapCount(0) == 0);

    // 8. Mono-marked thread: MEM_TOP_DOWN stripped, the cache not used, nothing recorded; other threads keep the flag
    BbtReset(4, 1);
    u8* k1 = BbtEaAlloc(0x40000);
    assert(BbtEaFree(k1) && g_bbCacheN == 1);
    g_bbMonoTid = (LONG)GetCurrentThreadId();
    u8* k2 = BbtEaAlloc(0x40000, MEM_RESERVE | MEM_COMMIT | MEM_TOP_DOWN);
    assert(k2 && k2 != k1 && g_bbtLastType == (MEM_RESERVE | MEM_COMMIT) && g_bb.strips == 1 && !g_bbMap[(DWORD)(uintptr_t)k2 >> 16]);
    assert(BbtEaFree(k2) && BbtState(k2) == MEM_FREE && g_bbCacheN == 1);
    g_bbMonoTid = (LONG)GetCurrentThreadId() + 4;  // another thread holds the mark
    u8* k3 = BbtEaAlloc(0x80000, MEM_RESERVE | MEM_COMMIT | MEM_TOP_DOWN);
    assert(k3 && g_bbtLastType == (MEM_RESERVE | MEM_COMMIT | MEM_TOP_DOWN) && g_bb.strips == 1 && g_bbMap[(DWORD)(uintptr_t)k3 >> 16] == 0x80000);
    assert(BbtEaFree(k3) && g_bbCacheN == 2);
    g_bbMonoTid = 0;

    // 9. the Mono wrapper: marks this thread around EA's Malloc, stack balanced; a held mark is left alone (monoBusy)
    g_bbMonoMalloc = (DWORD)(uintptr_t)&BbtMalloc;
    assert(BbtCallMono((void*)(uintptr_t)0x5000, 0x100000, 0) == (void*)(uintptr_t)0x12340000 && g_bbtEspDelta == 0);
    assert(g_bbtSawTid == (LONG)GetCurrentThreadId() && g_bbtSawHeap == (void*)(uintptr_t)0x5000 && g_bbtSawN == 0x100000 && !g_bbtSawF && !g_bbMonoTid);
    g_bbMonoTid = 77;
    assert(BbtCallMono((void*)(uintptr_t)0x5000, 0x200000, 3) == (void*)(uintptr_t)0x12340000 && g_bbtEspDelta == 0);
    assert(g_bbtSawTid == 77 && g_bbtSawN == 0x200000 && g_bbtSawF == 3 && g_bbMonoTid == 77 && g_bb.monoBusy == 1);
    g_bbMonoTid = 0;

    // 10. four threads x 3000 mixed alloc / free: every block back, the map holds exactly the cached ones, budget kept
    BbtReset(4, 1);
    HANDLE th[4];
    for (int i = 0; i < 4; i++) assert((th[i] = CreateThread(NULL, 0, BbtWorker, (void*)(uintptr_t)(i * 7919 + 1), 0, NULL)) != NULL);
    WaitForMultipleObjects(4, th, TRUE, INFINITE);
    for (HANDLE h : th) CloseHandle(h);
    assert(g_bbCacheBytes <= g_bbBudget && BbtMapCount(BB_M_CACHED) == g_bbCacheN && BbtMapCount(0) == 0 && BbtMapCount(BB_M_BUSY) == 0);
    assert(!g_bb.bad && !g_bb.dfree && !g_bb.pass && g_bb.hits + g_bb.misses == g_bb.allocs && g_bb.hits > 0);
    BbtReset(4, 1);

    // 11. sites: every window checked before any write; disp32 / rel32 point at our slots / wrapper; given back exactly
    alignas(16) static u8 wa[0x50], wb[0x10], wf[0x50], wm[0x50];
    static_assert((BB_ALLOC_SITE & 0xF) == 0 && (BB_FREE_SITE & 0xF) == 0xF && (BB_MONO_SITE & 0xF) == 0, "buffers mirror the sites' alignment");
    u8* const fa = wa, *const ff = wf + 0xF, *const fm = wm;
    memcpy(fa, kBbAllocBytes, sizeof(kBbAllocBytes)), memcpy(wb, kBbFreeBranch, sizeof(kBbFreeBranch));
    memcpy(ff, kBbFreeBytes, sizeof(kBbFreeBytes)), memcpy(fm, kBbMonoBytes, sizeof(kBbMonoBytes));
    assert(g_bbPatchN == 0 && BbInstallSites(fa, wb, ff, fm, BB_TOPDOWN | BB_MONO | BB_REUSE) == (BB_TOPDOWN | BB_MONO | BB_REUSE) && g_bbPatchN == 3);
    assert(*(DWORD*)(fa + BB_ALLOC_DISP) == (DWORD)(uintptr_t)&g_bbAllocSlot && *(DWORD*)(ff + BB_FREE_DISP) == (DWORD)(uintptr_t)&g_bbFreeSlot);
    assert((uintptr_t)fm + BB_MONO_REL + 4 + *(LONG*)(fm + BB_MONO_REL) == (uintptr_t)&BbMonoMalloc);
    assert(fa[BB_ALLOC_DISP - 2] == 0xFF && fa[BB_ALLOC_DISP - 1] == 0x15 && fm[BB_MONO_REL - 1] == 0xE8);
    BbGiveBack();
    assert(!g_bbPatchN && !memcmp(fa, kBbAllocBytes, sizeof(kBbAllocBytes)) && !memcmp(ff, kBbFreeBytes, sizeof(kBbFreeBytes)) &&
           !memcmp(fm, kBbMonoBytes, sizeof(kBbMonoBytes)));
    assert(BbInstallSites(fa, wb, ff, fm, BB_TOPDOWN | BB_REUSE) == (BB_TOPDOWN | BB_REUSE) && g_bbPatchN == 2);
    *(DWORD*)(ff + BB_FREE_DISP) = 0xAABBCCDD;  // another module rewrote the free call after us: give-back leaves it
    BbGiveBack();
    assert(!g_bbPatchN && *(DWORD*)(ff + BB_FREE_DISP) == 0xAABBCCDD && !memcmp(fa, kBbAllocBytes, sizeof(kBbAllocBytes)));
    assert(BbInstallSites(fa, wb, ff, fm, BB_REUSE) == 0 && g_bbPatchN == 0);  // and its window no longer matches: nothing
    memcpy(ff, kBbFreeBytes, sizeof(kBbFreeBytes));
    fm[0x20] ^= 1;  // MonoBottom's window differs: the rest installs, that window is untouched
    assert(BbInstallSites(fa, wb, ff, fm, BB_TOPDOWN | BB_MONO | BB_REUSE) == (BB_TOPDOWN | BB_REUSE) && g_bbPatchN == 2 &&
           *(LONG*)(fm + BB_MONO_REL) == *(const LONG*)(kBbMonoBytes + BB_MONO_REL));
    BbGiveBack();
    fm[0x20] ^= 1, wb[6] ^= 1;  // the IS_MMAPPED branch differs: no Reuse
    assert(BbInstallSites(fa, wb, ff, fm, BB_TOPDOWN | BB_MONO | BB_REUSE) == (BB_TOPDOWN | BB_MONO) && g_bbPatchN == 2 &&
           !memcmp(ff, kBbFreeBytes, sizeof(kBbFreeBytes)));
    BbGiveBack();
    wb[6] ^= 1, fa[0x21] ^= 1;  // the +0x498 reader differs: nothing at all
    assert(BbInstallSites(fa, wb, ff, fm, BB_TOPDOWN | BB_MONO | BB_REUSE) == 0 && g_bbPatchN == 0 && !memcmp(ff, kBbFreeBytes, sizeof(kBbFreeBytes)));
    fa[0x21] ^= 1;
    // the game's own addresses are not mapped in this process (image at 0x20000000): every switch SKIPs
    assert(BbInstallSites((u8*)(uintptr_t)BB_ALLOC_SITE, (u8*)(uintptr_t)BB_FREE_BRANCH, (u8*)(uintptr_t)BB_FREE_SITE,
                          (u8*)(uintptr_t)BB_MONO_SITE, BB_TOPDOWN | BB_MONO | BB_REUSE) == 0);

    // 12. TopDown: written once, only on a heap whose page size and core reserve / commit are EA's or S3PP's
    static u8 heap[0x520];
    *(DWORD*)(heap + 0x4D4) = 0x1000, *(DWORD*)(heap + 0x4D8) = 16u << 20, *(DWORD*)(heap + 0x4DC) = 4u << 20, heap[0] = 1;
    u8 old = 9;
    assert(!BbSetTopDown(heap, &old) && old == 0 && heap[0x498] == 1);
    assert(BbSetTopDown(heap, &old) && old == 1 && heap[0x498] == 1);  // already set: left
    heap[0x498] = 0, *(DWORD*)(heap + 0x4D8) = 4u << 20, *(DWORD*)(heap + 0x4DC) = 1u << 20;
    assert(!BbSetTopDown(heap, &old) && heap[0x498] == 1);
    heap[0x498] = 0, *(DWORD*)(heap + 0x4D8) = 8u << 20, *(DWORD*)(heap + 0x4DC) = 2u << 20;
    assert(BbSetTopDown(heap, &old) && heap[0x498] == 0);
    *(DWORD*)(heap + 0x4D8) = 16u << 20, *(DWORD*)(heap + 0x4DC) = 4u << 20, heap[0] = 0;
    assert(BbSetTopDown(heap, &old) && heap[0x498] == 0);  // Init not run
    heap[0] = 1;

    // 13. going live: armed + byte set; a late Apex gives the sites back and arms nothing; start stand-down installs nothing
    g_bbArm = 0;
    assert(BbInstallSites(fa, wb, ff, fm, BB_TOPDOWN | BB_MONO | BB_REUSE) == 7 && g_bbPatchN == 3);
    g_bbWant = 7, g_apexStandDown = true;
    const LONG stood0 = g_apexStood;
    BbGoLive(heap, true);
    assert(!g_bbArm && !g_bbPatchN && heap[0x498] == 0 && g_apexStood == stood0 + 1 && !memcmp(fa, kBbAllocBytes, sizeof(kBbAllocBytes)));
    assert(BbInstallSites(fa, wb, ff, fm, 7) == 7);
    BbGoLive(heap, false);
    assert(g_bbArm == (BB_MONO | BB_REUSE) && heap[0x498] == 1);
    BbGiveBack();
    g_bbArm = 0, g_bbWant = 0, g_apexFound = 1;
    {
        wchar_t ini[MAX_PATH];
        GetTempPathW(MAX_PATH, ini);
        wcscat_s(ini, L"ts3n-bigblock-test.ini");
        WritePrivateProfileStringW(L"BigBlock", L"Enabled", L"1", ini);
        WritePrivateProfileStringW(L"BigBlock", L"Reuse", L"1", ini);
        WritePrivateProfileStringW(L"BigBlock", L"ReuseMaxMB", L"8", ini);
        WritePrivateProfileStringW(L"BigBlock", L"ReuseMaxBlockMB", L"64", ini);
        assert(!ApplyBigBlock(ini) && g_apexStood == stood0 + 2 && !g_bbWant && !g_bbPatchN);
        assert(g_bbBudget == 8u << 20 && g_bbMaxBlock == 8u << 20);  // ReuseMaxBlockMB clamped to ReuseMaxMB
        g_apexFound = 0;
        assert(!ApplyBigBlock(ini) && !g_bbWant && !g_bbPatchN);  // no Apex: the game's bytes are not here, SKIP
        DeleteFileW(ini);
    }

    g_bbVaIat = va0, g_bbVfIat = vf0, g_bbHeapSlot = heap0, g_bbMonoMalloc = malloc0, g_bbBudget = budget0, g_bbMaxBlock = block0;
    g_apexFound = found0, g_apexStandDown = sd0;
    memset((void*)&g_bb, 0, sizeof(g_bb));
    printf("selftest bigblock: reuse, budget, table full, double free, pass-through, Mono strip + wrapper, 4 threads, sites, TopDown ok\n");
}
