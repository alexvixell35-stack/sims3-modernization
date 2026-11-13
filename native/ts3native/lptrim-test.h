// TestLotPoolTrim ([LotPool] Trim, lptrim.cpp, docs/541). Included by selftest.cpp. A fake pool built the way EA's
// add-core 0x4E42F0 and carve 0x4E4290 build one (cores 16-aligned, as the EA heap returns them), with a fake lock and
// free callback: no game code runs.
static int g_lpFakeLocks, g_lpFakeUnlocks, g_lpFakeFrees;
static int __fastcall LpFakeLock(void*, void*, const LONG* deadline) { assert(*deadline == -1); return ++g_lpFakeLocks; }
static void __fastcall LpFakeUnlock(void*, void*) { g_lpFakeUnlocks++; }
static void __cdecl LpFakeFree(void* core, void*) { g_lpFakeFrees++, _aligned_free(core); }
static void* __cdecl LpFakeAlloc(DWORD n, void*) { return _aligned_malloc(n, 16); }

static void LpFakeAddCore(u8* p) {  // as 0x4E42F0 (size (items + 2) * item + 0xC, link at the head) + 0x4E4290 (carve)
    const DWORD item = *(DWORD*)p, align = *(DWORD*)(p + 4), bytes = (*(DWORD*)(p + 8) + 2) * item + 0xC;
    u8* c = (u8*)LpFakeAlloc(bytes, NULL);
    *(DWORD*)c = *(DWORD*)(p + 0xC), *(DWORD*)(c + 4) = bytes, *(DWORD*)(p + 0xC) = (DWORD)(uintptr_t)c;
    DWORD s = LpFirst((DWORD)(uintptr_t)c, align), x;
    const DWORD e = (DWORD)(uintptr_t)c + bytes - 2 * item;
    *(DWORD*)(p + 0x10) = s;
    do x = s + item, *(DWORD*)(uintptr_t)s = x, s = x;
    while (x <= e);
    *(DWORD*)(uintptr_t)x = 0;
}
static DWORD LpFakePop(u8* p) {  // as 0xAE00A0
    if (!*(DWORD*)(p + 0x10)) LpFakeAddCore(p);
    const DWORD s = *(DWORD*)(p + 0x10);
    *(DWORD*)(p + 0x10) = *(DWORD*)(uintptr_t)s;
    return s;
}
static void LpFakePush(u8* p, DWORD s) { *(DWORD*)(uintptr_t)s = *(DWORD*)(p + 0x10), *(DWORD*)(p + 0x10) = s; }  // as 0xAE00F0
static DWORD LpListLen(DWORD head) {
    DWORD n = 0;
    for (DWORD s = head; s && n < 100000; s = *(DWORD*)(uintptr_t)s) n++;
    return n;
}

static void TestLotPoolTrim() {
    static_assert(offsetof(Ts3nCounters, lpOn) == 0x5EF8 && offsetof(Ts3nCounters, lpBad) == 0x5F14 && sizeof(Ts3nCounters) == 6379 * 4,
                  "counters v913 layout after v173 (docs/registry.md)");
    alignas(16) static u8 pool[0x20];
    static DWORD lock[10];
    memset(pool, 0, sizeof(pool));
    *(DWORD*)pool = LP_ITEM, *(DWORD*)(pool + 4) = LP_ALIGN, *(DWORD*)(pool + 8) = LP_PER;
    *(DWORD*)(pool + 0x14) = (DWORD)(uintptr_t)&LpFakeAlloc, *(DWORD*)(pool + 0x18) = (DWORD)(uintptr_t)&LpFakeFree;
    u8* const pool0 = g_lpPool;
    void* const lock0 = g_lpLock;
    const LpLockFn lockFn0 = g_lpLockFn;
    const LpUnlockFn unlockFn0 = g_lpUnlockFn;
    const DWORD alloc0 = g_lpAllocCb, free0 = g_lpFreeCb, keep0 = g_lpKeep;
    g_lpPool = pool, g_lpLock = lock, g_lpLockFn = (LpLockFn)(void*)&LpFakeLock, g_lpUnlockFn = (LpUnlockFn)(void*)&LpFakeUnlock;
    g_lpAllocCb = *(DWORD*)(pool + 0x14), g_lpFreeCb = *(DWORD*)(pool + 0x18), g_lpKeep = 1;

    // 1. five cores, every slot popped (in carve order, core by core) and marked; the carve gives items + 1 slots per core
    const DWORD per = LP_PER + 1, n = 5 * per;
    static DWORD live[5 * (LP_PER + 1)];
    for (DWORD i = 0; i < n; i++) live[i] = LpFakePop(pool), *(DWORD*)(uintptr_t)(live[i] + 4) = 0xA5A50000 | i;
    assert(*(DWORD*)(pool + 0x10) == 0 && LpListLen(*(DWORD*)(pool + 0xC)) == 5);
    // back: cores 0, 1, 3 whole; core 2 all but slot 7; core 4 the odd slots (the even ones, 65, stay live)
    auto stays = [&](DWORD i) { return (i / per == 2 && i % per == 7) || (i / per == 4 && i % per % 2 == 0); };
    for (DWORD i = 0; i < n; i++)
        if (!stays(i)) LpFakePush(pool, live[i]);
    LpResult r = LpTrim();
    assert(!r.bad && r.cores == 5 && r.slots == n && r.freeSlots == 3 * per + (per - 1) + (per - 1) / 2);
    assert(r.fullFree == 3 && r.kept == 1 && r.freed == 2 && r.freedBytes == 2 * ((LP_PER + 2) * LP_ITEM + 0xC));
    assert(g_lpFakeFrees == 2 && g_lpFakeLocks == 1 && g_lpFakeUnlocks == 1);
    assert(LpListLen(*(DWORD*)(pool + 0xC)) == 3 && LpListLen(*(DWORD*)(pool + 0x10)) == r.freeSlots - 2 * per);
    for (DWORD i = 0; i < n; i++)  // live slots untouched
        if (stays(i)) assert(*(DWORD*)(uintptr_t)(live[i] + 4) == (0xA5A50000 | i));
    for (DWORD s = *(DWORD*)(pool + 0x10); s; s = *(DWORD*)(uintptr_t)s) {  // every free slot in a core still listed
        bool in = false;
        for (DWORD c = *(DWORD*)(pool + 0xC); c; c = *(DWORD*)(uintptr_t)c) in = in || (s >= c && s < c + *(DWORD*)(uintptr_t)(c + 4));
        assert(in);
    }
    // the pool still works: pops drain the kept cores' free slots, then a new core is added
    const DWORD left = LpListLen(*(DWORD*)(pool + 0x10));
    for (DWORD i = 0; i < left; i++) LpFakePop(pool);
    assert(*(DWORD*)(pool + 0x10) == 0);
    const DWORD extra = LpFakePop(pool);
    assert(LpListLen(*(DWORD*)(pool + 0xC)) == 4);
    LpFakePush(pool, extra);

    // 2. nothing completely free beyond KeepCores: no core goes
    g_lpKeep = 16;
    r = LpTrim();
    assert(!r.bad && r.cores == 4 && r.freed == 0 && g_lpFakeFrees == 2 && g_lpFakeUnlocks == 2);

    // 3. a free slot off the carve positions: skipped, unlocked, nothing changed
    const DWORD head = *(DWORD*)(pool + 0x10), core = *(DWORD*)(pool + 0xC);
    *(DWORD*)(pool + 0x10) = head + 4;
    r = LpTrim();
    assert(r.bad && !strcmp(r.bad, "free slot off the carve") && g_lpFakeUnlocks == 3 && *(DWORD*)(pool + 0x10) == head + 4);
    *(DWORD*)(pool + 0x10) = head;
    // 4. a loop in the free list: skipped
    const DWORD save = *(DWORD*)(uintptr_t)head;
    *(DWORD*)(uintptr_t)head = head;
    r = LpTrim();
    assert(r.bad && !strcmp(r.bad, "free list longer than the slots") && g_lpFakeUnlocks == 4);
    *(DWORD*)(uintptr_t)head = save;
    // 5. a free slot outside every core: skipped
    alignas(16) static u8 stray[LP_ITEM];
    *(DWORD*)stray = head, *(DWORD*)(pool + 0x10) = (DWORD)(uintptr_t)stray;
    r = LpTrim();
    assert(r.bad && !strcmp(r.bad, "free slot outside every core") && g_lpFakeUnlocks == 5);
    *(DWORD*)(pool + 0x10) = head;
    // 6. another free callback in the header: skipped before the lock
    g_lpFreeCb = 0x12345678;
    r = LpTrim();
    assert(r.bad && !strcmp(r.bad, "pool header not as expected") && g_lpFakeLocks == 5 && g_lpFakeUnlocks == 5);
    assert(*(DWORD*)(pool + 0xC) == core && g_lpFakeFrees == 2);
    // 7. Apex Radiance loaded ([Compat] ApexStandDown=1): stood down before the header and the lock
    const int apex0 = g_apexFound;
    g_apexFound = 1;
    r = LpTrim();
    assert(g_apexStandDown && r.bad && !strcmp(r.bad, "stood down: Apex Radiance loaded") && g_lpFakeLocks == 5);
    g_apexFound = apex0;

    for (DWORD c = *(DWORD*)(pool + 0xC); c;) {
        const DWORD next = *(DWORD*)(uintptr_t)c;
        _aligned_free((void*)(uintptr_t)c);
        c = next;
    }
    g_lpPool = pool0, g_lpLock = lock0, g_lpLockFn = lockFn0, g_lpUnlockFn = unlockFn0;
    g_lpAllocCb = alloc0, g_lpFreeCb = free0, g_lpKeep = keep0;
    printf("lot pool trim: selftest ok (5 cores: 3 completely free, 1 kept, 2 freed; pool works after; off-carve, loop, stray, header, Apex skips)\n");
}
