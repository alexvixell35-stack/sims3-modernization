// --- [SceneObjFind] (sceneobjfind.cpp, docs/532): layout, chain walk depth on a fake 1,033-bucket table (hit at depth 1 and 3,
// miss = whole chain, empty bucket), counters in units of 1024, max chain, other threads only counted as sfOffMain, and the
// detour's thiscall shape (args popped, eax = out). Included by selftest.cpp.

// stands in for EA's find: out = {table, key}, ret 8, eax = out
static __declspec(naked) void SfFakeOrig() {
    __asm mov eax, dword ptr [esp + 4]
    __asm mov dword ptr [eax], ecx
    __asm mov edx, dword ptr [esp + 8]
    __asm mov dword ptr [eax + 4], edx
    __asm ret 8
}

static DWORD WINAPI SfOffMainThread(LPVOID p) {
    const DWORD* const* a = (const DWORD* const*)p;
    SfCount(a[0], a[1]);
    return 0;
}

static void TestSceneObjFind() {
    static_assert(offsetof(Ts3nCounters, sfKCalls) == 0x5CB4 && offsetof(Ts3nCounters, sfOffMain) == 0x5CC0 && sizeof(Ts3nCounters) == 5937 * 4,
                  "counters v911 layout after the v908-v910 pad (docs/registry.md)");
    Ts3nCounters& k = ts3native_counters;
    static DWORD table[SF_BUCKETS + 1];
    table[SF_BUCKETS] = 0xFFFFFFFF;  // end bucket sentinel (+0x1024)
    // bucket 5: keys {5, 1}, {5 + 1033, 2}, {5 + 2066, 3} in chain order; node = {next, pad, key lo, key hi}
    static DWORD n1[4], n2[4], n3[4];
    n1[0] = (DWORD)(uintptr_t)n2, n1[2] = 5, n1[3] = 1;
    n2[0] = (DWORD)(uintptr_t)n3, n2[2] = 5 + SF_BUCKETS, n2[3] = 2;
    n3[0] = 0, n3[2] = 5 + 2 * SF_BUCKETS, n3[3] = 3;
    table[5] = (DWORD)(uintptr_t)n1;
    const DWORD kFirst[2] = {5, 1}, kThird[2] = {5 + 2 * SF_BUCKETS, 3}, kMissHi[2] = {5 + SF_BUCKETS, 9}, kEmpty[2] = {6, 1};
    assert(SfWalk(table, kFirst) == 1 && SfWalk(table, kThird) == 3);
    assert(SfWalk(table, kMissHi) == 3);  // same lo, other hi: a miss walks the whole chain
    assert(SfWalk(table, kEmpty) == 0);
    n3[0] = (DWORD)(uintptr_t)n1;  // a cyclic chain: the walk stops at the cap instead of spinning
    assert(SfWalk(table, kMissHi) == SF_WALK_CAP);
    n3[0] = 0;

    const DWORD mainTid = g_mainTid;
    g_mainTid = GetCurrentThreadId();
    memset(&k.sfKCalls, 0, 4 * 4);
    g_sfCalls = g_sfNodes = 0;
    for (int i = 0; i < 1023; i++) SfCount(table, kThird);
    assert(k.sfKCalls == 0 && k.sfKNodes == 2 && k.sfMaxNodes == 3);  // 3069 nodes = 2 units, 1023 calls = 0 units
    SfCount(table, kFirst);
    assert(k.sfKCalls == 1 && k.sfKNodes == 2 && k.sfMaxNodes == 3 && g_sfNodes == 3070);

    const DWORD* args[2] = {table, kThird};
    HANDLE th = CreateThread(NULL, 0, SfOffMainThread, args, 0, NULL);
    assert(th);
    WaitForSingleObject(th, INFINITE);
    CloseHandle(th);
    assert(k.sfOffMain == 1 && g_sfCalls == 1024 && g_sfNodes == 3070);  // another thread: counted, not walked

    const SfFindFn orig = g_sfOrig;
    g_sfOrig = (SfFindFn)(void*)SfFakeOrig;
    DWORD out[2] = {};
    volatile DWORD guard = 0x5F5F5F5F;
    DWORD* r = SfFind(table, NULL, out, kFirst);
    assert(r == out && out[0] == (DWORD)(uintptr_t)table && out[1] == (DWORD)(uintptr_t)kFirst && guard == 0x5F5F5F5F);
    assert(g_sfCalls == 1025 && g_sfNodes == 3071);
    g_sfOrig = orig;
    g_mainTid = mainTid;
    memset(&k.sfKCalls, 0, 4 * 4);
    g_sfCalls = g_sfNodes = 0;
    printf("SceneObjFind: chain depth on a fake 1,033-bucket table, counters in 1024 units, max, off-main count, thiscall shape: ok\n");
}
