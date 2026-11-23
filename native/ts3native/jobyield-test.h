// docs/183 JobYield + sim job waits by site, on TestRouteAsync's setup (real worker, M1 world, the Sim's own task at its root
// state). Included by selftest.cpp before TestRouteAsync.
static u8 JyAsk() { return GetOptionHook(0, 0, kJobBusyHi, 1); }  // ts3fixes asks on a route with handle 0

static DWORD g_jyTestActive;  // the route JyFakeActive calls followed
static u8 __cdecl JyFakeActive(DWORD h) { return h && h == g_jyTestActive; }
static u8 JyAskH(DWORD h, DWORD op) { return GetOptionHook(h, op, kJobBusyHi, 1); }  // ts3fixes asks on the plan's own route
static void JyArm(DWORD h) {  // what route h's marks and its refused START leave for the IC right after
    g_rfbHandle = h, g_rfbWhy = RFB_QFULL, g_scSyncH = h, g_ufH = h, g_kdScopeH = h, g_kdScope = 2, g_kdScopeIa = 7;
}
static bool JyArmed(DWORD h) {
    return g_rfbHandle == h && g_rfbWhy == RFB_QFULL && g_scSyncH == h && g_ufH == h && g_kdScopeH == h && g_kdScope == 2 && g_kdScopeIa == 7;
}
static bool JyClear() { return !g_rfbHandle && !g_scSyncH && !g_ufH && !g_kdScopeH; }
static void JyJob() {  // a 60 ms job of route kH1 runs
    assert(Start(kH1));
    while (g_async.state != A_RUNNING) Sleep(0);
}
static void JyJobEnd(u8* out) {
    WaitReady(kH1);
    assert(IcPlanHook(out, kH1) == out);
    g_rfbHandle = 0, g_scSyncH = 0, g_ufH = 0, g_kdScopeH = 0;
}

static void TestJobYield() {
    static_assert(offsetof(Ts3nCounters, jyMode) == 0x45CC && offsetof(Ts3nCounters, jyBusy) == 0x45D4 &&
                      offsetof(Ts3nCounters, swN) == 0x45D8 && offsetof(Ts3nCounters, swUs) == 0x45F0 &&
                      offsetof(Ts3nCounters, swMaxUs) == 0x4608 && offsetof(Ts3nCounters, swYieldable) == 0x460C &&
                      offsetof(Ts3nCounters, swLines) == 0x4624 && sizeof(Ts3nCounters) == 6866 * 4,
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
    {   // docs/323 M2: a full park table answers not busy (the task does not sleep), the slots stay, counted in jyLostN
        const JyPark save = g_jyPark;
        for (int i = 0; i < JYP_SLOTS; i++) g_jyPark.r[i].handle = 0x27000000u + i, g_jyPark.r[i].tick = GetTickCount(), g_jyPark.r[i].has = JYP_WHY;
        const LONG full0 = g_jyPark.full, busy0 = k.jyBusy, lost0 = g_jyLostN, asks0 = k.jyAsks;
        const RouteIsActiveFn act0 = g_routeIsActive;
        g_routeIsActive = JyFakeActive, g_jyTestActive = 0;  // no game code here: the route is not followed
        const u8 r = JyAskH(0x27100000u, 0);
        if (g_async.state == A_RUNNING)  // the 60 ms job may have ended in between: then nothing is busy anyway
            assert(r == 0 && g_jyPark.full == full0 + 1 && g_jyLostN == lost0 + 1 && k.jyBusy == busy0 && JypLive(g_jyPark) == JYP_SLOTS);
        g_jyPark.r[JYP_SLOTS - 1].handle = 0;  // control: one free slot, the same ask is busy again (it sleeps)
        const u8 r2 = JyAskH(0x27100000u, 0);
        if (g_async.state == A_RUNNING) assert(r2 == 1 && k.jyBusy == busy0 + 1);
        g_routeIsActive = act0;
        g_jyPark = save;
        JyCounts();
        k.jyBusy = busy0, k.jyAsks = asks0;  // the cases below count from here
    }
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

    // 5. [RouteAsync] CutOnWait on (docs/850), real worker: a wait at disc behind a job of unknown scope (no ts3fixes answer =
    // household) polls until the worker has looked at the job, asks nothing, and ends with the job, uncut
    k.cowMode = 1;
    const LONG cut0 = k.kdCutUnk + k.kdCutSeen + k.kdCutNpc + k.kdCutUnseen + k.budgetCapped;
    assert(Start(kH1));
    while (g_async.state != A_RUNNING) Sleep(0);
    const DWORD cowSeq = g_asyncSeq;
    WaitAsyncDone(SW_DISC);
    assert(g_async.state != A_RUNNING && k.cowAsks == 0 && k.cowCut == 0 && g_cowReq == 0 && g_cowDecided == (LONG)cowSeq && k.swN[SW_DISC] == 1);
    assert(k.kdCutUnk + k.kdCutSeen + k.kdCutNpc + k.kdCutUnseen + k.budgetCapped == cut0);
    WaitReady(kH1);
    assert(IcPlanHook(out, kH1) == out);
    k.cowMode = 0;
    // 6. docs/183 §7: the records of a route whose task steps aside (jobyield.cpp, jobyield-core.h)
    {
        const RouteIsActiveFn act0 = g_routeIsActive;
        g_routeIsActive = JyFakeActive, g_jyTestActive = 0, g_jyFollowed = 0;
        g_icSleepMs = 300;  // every ask below falls inside its job
        memset(&g_jyPark, 0, sizeof(g_jyPark));
        g_jyParkedN = g_jyBackN = g_jyDropN = g_jyLostN = g_jyActiveN = 0;
        // the first busy answer takes route kH2's records out of the slots; other routes' marks come and go while its task
        // sleeps; the answer that ends the sleep puts them back for the plan that follows
        JyJob();
        JyArm(kH2);
        assert(JyAskH(kH2, 0) == 1 && JyClear() && g_jyParkedN == 1);
        g_kdScopeH = 0x25000077, g_kdScope = 3, g_rfbHandle = 0x25000077, g_rfbWhy = RFB_NOSTART;
        assert(JyAskH(kH2, 0) == 1 && g_kdScopeH == 0x25000077 && g_jyParkedN == 1);  // still asleep: another route's slot is not touched
        while (JyAskH(kH2, 0)) Sleep(1);
        assert(JyArmed(kH2) && g_jyBackN == 1 && JypLive(g_jyPark) == 0);
        assert(JyAskH(kH2, 0) == 0 && JyArmed(kH2) && g_jyBackN == 1);  // nothing comes back twice
        JyJobEnd(out);
        // op 1: the caller gives up at its cap while the job still runs: the records go back for its plan
        JyJob();
        JyArm(kH2);
        assert(JyAskH(kH2, 0) == 1 && JyClear());
        assert(JyAskH(kH2, 1) == 0 && JyArmed(kH2) && g_jyBackN == 2);
        JyJobEnd(out);
        // op 2: the sleep ended by an exception: no plan follows, nothing goes back; a new START of the route drops them too
        JyJob();
        JyArm(kH2);
        assert(JyAskH(kH2, 0) == 1 && JyClear());
        assert(JyAskH(kH2, 2) == 0 && JyClear() && g_jyDropN == 1 && JypLive(g_jyPark) == 0);
        JyArm(kH2);
        assert(JyAskH(kH2, 0) == 1 && JyClear());
        JyDrop(kH2);  // AsyncStartBody's call
        assert(g_jyDropN == 2 && JyAskH(kH2, 1) == 0 && JyClear());
        // switched off while the task sleeps: its plan follows now, with its records
        JyArm(kH2);
        assert(JyAskH(kH2, 0) == 1 && JyClear());
        k.jyMode = 0;
        assert(JyAskH(kH2, 0) == 0 && JyArmed(kH2) && g_jyBackN == 3);
        k.jyMode = 1;
        // a followed route does not step aside (its records stay where they are); JobYieldFollowed=1: as before
        g_jyTestActive = kH2;
        assert(JyAskH(kH2, 0) == 0 && JyArmed(kH2) && g_jyActiveN == 1 && g_jyParkedN == 5);
        g_jyFollowed = 1;
        assert(JyAskH(kH2, 0) == 1 && JyClear() && g_jyActiveN == 1 && g_jyParkedN == 6);
        assert(JyAskH(kH2, 1) == 0 && JyArmed(kH2) && g_jyBackN == 4);
        g_jyFollowed = 0, g_jyTestActive = 0;
        // an older ts3fixes asks on a route with handle 0: busy as before, nothing parked
        assert(JyAsk() == 1 && JyArmed(kH2) && g_jyParkedN == 6 && g_jyLostN == 0);
        JyJobEnd(out);
        g_routeIsActive = act0;
        memset(&g_jyPark, 0, sizeof(g_jyPark));
        g_jyParkedN = g_jyBackN = g_jyDropN = g_jyLostN = g_jyActiveN = 0;
    }

    g_icSleepMs = 0;
    memset((void*)&k.jyMode, 0, sizeof(Ts3nCounters) - offsetof(Ts3nCounters, jyMode));
    printf("JobYield (docs/183): counters v109; JOBBUSY off / on / nested / no job, step-aside loop ends with the job; sim job "
           "waits at collect / drop, yieldable flag, none counted without a job, >= 300 ms line; records parked / back / dropped, "
           "cap, switch off, followed route ok\n");
}
