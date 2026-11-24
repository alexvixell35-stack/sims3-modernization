// TestRouteDestroyDefer ([RouteAsync] DestroyDefer, routedefer.cpp, docs/67 §47). Included by selftest.cpp.
// The hook runs against a landing function in place of the trampoline; job slots and the async slot are set by hand.
static DWORD g_rddtCalls, g_rddtLast;
static void __cdecl RddtLand(DWORD handle) { g_rddtCalls++, g_rddtLast = handle; }

static void TestRouteDestroyDefer() {
    const RddDestroyFn t0 = g_rddDestroyT;
    const LONG on0 = g_rddOn;
    const DWORD owner0 = g_async.owner, h0 = g_async.handle;
    const LONG st0 = g_async.state, js0 = g_jobs[3].state;
    const DWORD jh0 = g_jobs[3].handle;
    g_rddDestroyT = RddtLand, g_rddOn = 1, g_async.owner = GetCurrentThreadId(), g_async.state = A_IDLE;
    // no job names the route: destroyed at once
    RddDestroyHook(0x25000011);
    assert(g_rddtCalls == 1 && g_rddtLast == 0x25000011 && g_rddN == 0);
    // a queued job names it: deferred; the worker could still resolve the handle
    g_jobs[3].handle = 0x25000022, g_jobs[3].state = J_QUEUED;
    RddDestroyHook(0x25000022);
    assert(g_rddtCalls == 1 && g_rddN == 1 && g_rddDeferred >= 1);
    // the running job's own route (the worker between its lookup and its AddRef): deferred too
    g_jobs[3].state = J_RUNNING, g_async.state = A_RUNNING, g_async.handle = 0x25000022;
    RddDestroyHook(0x25000033);  // another route: destroyed now, the deferred one stays (a job runs: no flush)
    assert(g_rddtCalls == 2 && g_rddtLast == 0x25000033 && g_rddN == 1);
    g_async.handle = 0x25000044, g_jobs[3].handle = 0x25000044;
    RddDestroyHook(0x25000044);
    assert(g_rddtCalls == 2 && g_rddN == 2);
    // a flush while the job still holds one keeps it, runs the other
    g_async.handle = 0x25000022, g_jobs[3].handle = 0x25000022;
    RddFlush(false);
    assert(g_rddtCalls == 3 && g_rddtLast == 0x25000044 && g_rddN == 1);
    // inside a job callback, or off the owner thread: nothing runs
    g_jobs[3].state = J_DONE, g_async.state = A_IDLE;
    g_inInvoke++;
    RddFlush(false);
    g_inInvoke--;
    assert(g_rddtCalls == 3 && g_rddN == 1);
    // job done: the next poll destroys it
    RddFlush(false);
    assert(g_rddtCalls == 4 && g_rddtLast == 0x25000022 && g_rddN == 0);
    // closing: a route a job still holds is dropped, never run in the next world
    g_jobs[3].state = J_QUEUED, g_jobs[3].handle = 0x25000055;
    RddDestroyHook(0x25000055);
    const LONG drop0 = g_rddDropped;
    RddFlush(true);
    assert(g_rddtCalls == 4 && g_rddN == 0 && g_rddDropped == drop0 + 1);
    // switch off or another thread: straight through
    g_rddOn = 0;
    RddDestroyHook(0x25000055);
    assert(g_rddtCalls == 5 && g_rddN == 0);
    g_rddDestroyT = t0, g_rddOn = on0, g_async.owner = owner0, g_async.handle = h0, g_async.state = st0;
    g_jobs[3].state = js0, g_jobs[3].handle = jh0;
    g_rddDeferred = g_rddRun = g_rddFull = g_rddDropped = g_rddLog = 0;
    printf("route destroy defer: queued / running job's route deferred, others at once, flush keeps held ones, none in a job "
           "callback, dropped at closing, off = straight through: ok\n");
}
