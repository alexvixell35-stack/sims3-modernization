// TestCullRace ([CrashForensics] CullRaceWatch + [EaCrashFixes] CullDrainWait, cullrace.cpp, docs/338). Included by selftest.cpp.
// Fake renderer / job / scene memory in the layout the drain reads; CrBefore runs on them, the fix's wait is a fake that
// finishes the job. The game hooks themselves are byte-checked by PatchCalls at run time (not here).
static u8* g_crtJob;  // fake job record (state at handle + 0x1C)
static LONG g_crtWaitCalls;
static void __fastcall CrtFakeWait(DWORD handle) {
    assert(handle == (DWORD)(uintptr_t)(g_crtJob + 8));
    *(LONG*)(g_crtJob + 0x24) = 6;  // done
    g_crtWaitCalls++;
}

static void TestCullRace() {
    Ts3nCounters& k = ts3native_counters;
    static u8 r[0x400], job[0x50], scene[0x50];
    static DWORD objs[4], list[8];
    g_crtJob = job;
    *(u8**)(r + 0x318) = job + 8, *(LONG*)(job + 0x24) = 4;
    *(DWORD**)(scene + 4) = objs, *(DWORD**)(scene + 8) = objs + 4;
    for (int i = 0; i < 3; i++) list[i] = (DWORD)i;
    *(DWORD**)(scene + 0x40) = list, *(DWORD**)(scene + 0x44) = list + 3;
    LONG state, badAt, count, len;
    DWORD bad;


    // multithreaded cull off: no race even with the job running; clean list
    r[0x28F] = 0, *(LONG*)(job + 0x24) = 4;
    assert(CrCheck(scene, r, &state, &bad, &badAt, &count, &len) == 0 && count == 4 && len == 3);
    // on + running -> race; on + done -> none; null handle -> none
    r[0x28F] = 1;
    assert(CrCheck(scene, r, &state, &bad, &badAt, &count, &len) == 1 && state == 4);
    *(LONG*)(job + 0x24) = 6;
    assert(CrCheck(scene, r, &state, &bad, &badAt, &count, &len) == 0);
    *(LONG*)(job + 0x24) = 3;  // queued, not started: no overlap
    assert(CrCheck(scene, r, &state, &bad, &badAt, &count, &len) == 0 && state == 3);
    *(u8**)(r + 0x318) = NULL, *(LONG*)(job + 0x24) = 3;
    assert(CrCheck(scene, r, &state, &bad, &badAt, &count, &len) == 0);
    *(u8**)(r + 0x318) = job + 8, *(LONG*)(job + 0x24) = 4;
    // the crash shape: an entry holding a heap pointer (idx >= count); idx == count is already bad
    list[1] = 0x274E3380;
    assert(CrCheck(scene, r, &state, &bad, &badAt, &count, &len) == 3 && bad == 0x274E3380 && badAt == 1);
    list[1] = 4;
    assert(CrCheck(scene, r, &state, &bad, &badAt, &count, &len) == 3 && bad == 4);
    list[1] = 1;
    // unreadable list buffer -> bad, entry -1
    *(DWORD**)(scene + 0x40) = (DWORD*)(uintptr_t)0x10, *(DWORD**)(scene + 0x44) = (DWORD*)(uintptr_t)0x18;
    assert((CrCheck(scene, r, &state, &bad, &badAt, &count, &len) & 2) && badAt == -1);
    *(DWORD**)(scene + 0x40) = list, *(DWORD**)(scene + 0x44) = list + 3;

    // CrBefore: watch on, fix off -> counts the race, no wait
    const LONG d0 = k.crDrains, r0 = k.crRace, b0 = k.crBadIdx, w0 = k.cdwWaits;
    g_crWatch = 1, g_cdwMode = 0, g_cdwWait = CrtFakeWait, g_crLogEvery = 64;
    QueryPerformanceFrequency(&g_crQpf);
    *(LONG*)(job + 0x24) = 4;
    CrBefore(scene, r);
    assert(k.crDrains == d0 + 1 && k.crRace == r0 + 1 && k.crBadIdx == b0 && k.cdwWaits == w0 && g_crtWaitCalls == 0);
    // fix on -> waits once (the fake finishes the job), race still counted (seen before the wait)
    g_cdwMode = 1;
    CrBefore(scene, r);
    assert(k.crRace == r0 + 2 && k.cdwWaits == w0 + 1 && g_crtWaitCalls == 1 && *(LONG*)(job + 0x24) == 6);
    // fix on, cull off (+0x28F = 0): no wait
    r[0x28F] = 0;
    CrBefore(scene, r);
    assert(k.cdwWaits == w0 + 1 && g_crtWaitCalls == 1);
    // watch off: no detection, bad list not counted
    r[0x28F] = 1, *(LONG*)(job + 0x24) = 4, list[0] = 99, g_crWatch = 0, g_cdwMode = 0;
    CrBefore(scene, r);
    assert(k.crRace == r0 + 2 && k.crBadIdx == b0 && k.crDrains == d0 + 4);
    // watch on: bad index counted
    g_crWatch = 1;
    CrBefore(scene, r);
    assert(k.crBadIdx == b0 + 1 && k.crRace == r0 + 3);
    // cull thread record: the hook keeps the tid only while the cull runs
    struct F { static DWORD __fastcall Cull(void*, void*) { assert((DWORD)g_crCullTid == GetCurrentThreadId()); return 7; } };
    g_crCullOrig = F::Cull;
    assert(CrCullHook(r, NULL) == 7 && g_crCullTid == 0);
    g_crWatch = 0, g_cdwMode = 0, g_cdwWait = NULL, g_crCullOrig = NULL;
    k.crDrains = d0, k.crRace = r0, k.crBadIdx = b0, k.cdwWaits = w0;
    printf("CullRace: race (job 318 running, state 4 only) / bad index / unreadable list detected, wait only with +0x28F and the fix, cull tid record: ok\n");
}
