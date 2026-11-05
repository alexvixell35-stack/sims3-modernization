// TestFootstepRO ([FootstepRO], fsro.cpp): the cache answers only on the owner thread while a job runs; misses, stale entries
// MissNear and MissLast; other threads and no-job calls go to the game, refresh the entry and check the cached answers.
namespace fst {
static LONG calls;
static DWORD ret;
static DWORD __fastcall Orig(u8*, void*, const u8*, DWORD) { calls++; return ret; }
}  // namespace fst

static void TestFootstepRO() {
    using namespace fst;
    Ts3nCounters& k = ts3native_counters;
    memset(&k.fsReady, 0, sizeof(Ts3nCounters) - offsetof(Ts3nCounters, fsReady));
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
    const FsFn savedOrig = g_fsOrig;
    const AsyncSlot savedAsync = g_async;
    g_fsOrig = Orig;
    memset(g_fsc, 0, sizeof(g_fsc));
    g_fsHaveLast = false, g_fsMissLast = false, g_fsMissNear = false, g_fsMaxAgeMs = 60000;
    const DWORD me = GetCurrentThreadId();
    static u8 world[16];
    u8 pt[12] = {};
    auto at = [&](float x, float z, signed char lv) { *(float*)pt = x, *(float*)(pt + 4) = z, pt[8] = (u8)lv; };
    auto call = [&]() { return FsStub(world, NULL, pt, 0x27); };
    at(10.2f, 20.3f, 0);
    // switch off: the game, nothing counted
    calls = 0, ret = 7, g_async.owner = me, g_async.state = A_RUNNING;
    assert(call() == 7 && calls == 1 && k.fsCalls == 0);
    k.fsMode = 1;
    // another thread: the game
    g_async.owner = me + 1;
    assert(call() == 7 && calls == 2 && k.fsCalls == 0);
    g_async.owner = me;
    // job, empty cache: the game (a miss), stored
    assert(call() == 7 && calls == 3 && k.fsMiss == 1 && k.fsStored == 1 && k.fsJob == 1);
    // job, same 0.5 m cell: the cache, no game call
    ret = 9, at(10.4f, 20.4f, 0);
    assert(call() == 7 && calls == 3 && k.fsHit == 1);
    // other level or other cell: a miss
    at(10.4f, 20.4f, 1);
    assert(call() == 9 && calls == 4 && k.fsMiss == 2);
    at(10.6f, 20.4f, 0);
    assert(call() == 9 && calls == 5 && k.fsMiss == 3);
    // no job: always the game, and the entry is refreshed
    g_async.state = A_IDLE, ret = 11, at(10.2f, 20.3f, 0);
    assert(call() == 11 && calls == 6 && k.fsJob == 4);
    // ... and checked: the fresh entry said 7, the last answer was 9, the game says 11
    assert(k.fsChk == 1 && k.fsChkDiff == 1 && k.fsLastChk == 1 && k.fsLastDiff == 1);
    // ... and the neighbour cell (21, 40) said 9 (counted with MissNear off too)
    assert(k.fsNearChk == 1 && k.fsNearDiff == 1);
    g_async.state = A_RUNNING;
    assert(call() == 11 && calls == 6 && k.fsHit == 2);
    // stale entry: counted, then the game
    FsSlot((LONG)floorf(10.2f * 2.0f), (LONG)floorf(20.3f * 2.0f), 0, 0x27)->tick -= 120000;
    ret = 12;
    assert(call() == 12 && calls == 7 && k.fsStale == 1 && k.fsMiss == 4);
    // MissLast: a miss answers the game's last answer, no call
    g_fsMissLast = true, at(-55.0f, 3.0f, 0);
    assert(call() == 12 && calls == 7 && k.fsLast == 1);
    // NaN point: the game
    *(float*)pt = NAN;
    assert(call() == 12 && calls == 8);
    // MissNear: a miss next to a fresh cell answers that cell's surface, before MissLast
    g_fsMissNear = true, g_async.state = A_IDLE, ret = 20, at(30.2f, 40.2f, 0);  // cell (60, 80), stored
    assert(call() == 20 && calls == 9);
    g_async.state = A_RUNNING, ret = 21, at(30.7f, 40.2f, 0);  // cell (61, 80), next to (60, 80)
    assert(call() == 20 && calls == 9 && k.fsNear == 1);
    at(31.7f, 40.2f, 0);  // cell (63, 80): no fresh neighbour, MissLast answers the last surface
    assert(call() == 20 && calls == 9 && k.fsLast == 2);
    g_async.state = A_IDLE, ret = 22, at(30.7f, 40.2f, 0);  // no job: the neighbour (20) checked against the game (22)
    assert(call() == 22 && calls == 10 && k.fsNearChk == 2 && k.fsNearDiff == 2);
    printf("TestFootstepRO: owner + job only, cell / level keys, miss, stale, MissNear, MissLast, NaN -> game, answer checks\n");
    g_fsOrig = savedOrig, g_async = savedAsync, g_fsMissLast = false, g_fsMissNear = false, g_fsHaveLast = false;
    memset(g_fsc, 0, sizeof(g_fsc));
    memset(&k.fsReady, 0, sizeof(Ts3nCounters) - offsetof(Ts3nCounters, fsReady));
}
