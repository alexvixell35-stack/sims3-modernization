// docs/183 JobYield + sim job waits by site, on TestRouteAsync's setup (real worker, M1 world, the Sim's own task at its root
// state). Included by selftest.cpp before TestRouteAsync.
static u8 JyAsk() { return GetOptionHook(0, 0, kJobBusyHi, 1); }  // ts3fixes asks on a route with handle 0

static void TestJobYield() {
    static_assert(offsetof(Ts3nCounters, jyMode) == 0x45CC && offsetof(Ts3nCounters, jyBusy) == 0x45D4 &&
                      offsetof(Ts3nCounters, swN) == 0x45D8 && offsetof(Ts3nCounters, swUs) == 0x45F0 &&
                      offsetof(Ts3nCounters, swMaxUs) == 0x4608 && offsetof(Ts3nCounters, swYieldable) == 0x460C &&
                      offsetof(Ts3nCounters, swLines) == 0x4624 && sizeof(Ts3nCounters) == 6066 * 4,
                  "counters v109 layout after v108 (docs/registry.md)");
    Ts3nCounters& k = ts3native_counters;
    assert(k.version == 912);
    memset((void*)&k.jyMode, 0, sizeof(Ts3nCounters) - offsetof(Ts3nCounters, jyMode));
    u8 out[0x48];
    FakeTask(kRoot, 7);

    // 1. switch off: never busy, nothing counted (C# plans as before and waits natively)
    g_icSleepMs = 60;
    assert(Start(kH1));
    while (g_async.state != A_RUNNING) Sleep(0);
    assert(g_async.lock && GetOptionHook(0, 8, kWouldHi, 1) == 1);  // watchdoglockskip: the job holds +0x918 (60 ms job)
    assert(JyAsk() == 0 && k.jyAsks == 0);
    // 2. on: busy while the job runs, only at the task's root interpreter state; the step-aside loop ends with the job
    k.jyMode = 1;
    assert(JyAsk() == 1 && k.jyAsks == 1 && k.jyBusy == 1);
    FakeTask(kNested, 7);  // a native->managed call during the task may not yield: not busy, the plan waits as before
    assert(JyAsk() == 0 && k.jyBusy == 1);
    FakeTask(kRoot, 7);
    while (JyAsk()) Sleep(1);
    assert(g_async.state != A_RUNNING && k.jyBusy >= 2);
    WaitReady(kH1);
    assert(IcPlanHook(out, kH1) == out && k.swN[SW_COLLECT] == 0);  // READY first: the collect never waits
    assert(JyAsk() == 0);  // no job: not busy
    assert(GetOptionHook(0, 8, kWouldHi, 1) == 0);  // watchdoglockskip: no job, no skip

    // 3. waits by site: a plan of the running route before READY waits at collect; CANCEL while it runs waits at drop
    assert(Start(kH1));
    while (g_async.state != A_RUNNING) Sleep(0);
    assert(IcPlanHook(out, kH1) == out && k.swN[SW_COLLECT] == 1 && k.swYieldable[SW_COLLECT] == 1 && k.swUs[SW_COLLECT] > 0);
    assert(Start(kH1));
    while (g_async.state != A_RUNNING) Sleep(0);
    FakeTask(kNested, 7);  // CANCEL from a nested call: counted, not yieldable
    Cancel(kH1);
    FakeTask(kRoot, 7);
    assert(k.swN[SW_DROP] == 1 && k.swYieldable[SW_DROP] == 0 && k.swMaxUs > 0 && g_async.state == A_IDLE);
    const LONG n0 = k.swN[SW_DROP];
    Cancel(kH1);  // nothing left to drop: no wait counted
    assert(k.swN[SW_DROP] == n0);
    // 4. one line per wait >= 300 ms (20 a minute)
    SwNote(SW_RTWAIT, 250000, true, kH1, true);
    SwNote(SW_RTWAIT, 400000, true, kH1, true);
    assert(k.swN[SW_RTWAIT] == 2 && k.swYieldable[SW_RTWAIT] == 2 && k.swLines == 1 && k.swMaxUs == 400000);

    g_icSleepMs = 0;
    memset((void*)&k.jyMode, 0, sizeof(Ts3nCounters) - offsetof(Ts3nCounters, jyMode));
    printf("JobYield (docs/183): counters v109; JOBBUSY off / on / nested / no job, step-aside loop ends with the job; sim job "
           "waits at collect / drop, yieldable flag, none counted without a job, >= 300 ms line ok\n");
}
