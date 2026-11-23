// TestRouteDestroyDefer ([RouteAsync] DestroyDefer, routedefer.cpp, docs/67 §41). Included by selftest.cpp.
// The hook runs against a landing function in place of the trampoline; job slots and the async slot are set by hand.
static DWORD g_rdtCalls, g_rdtLast;
static void __cdecl RdtLand(DWORD handle) { g_rdtCalls++, g_rdtLast = handle; }

static void TestRouteDestroyDefer() {
    const RdDestroyFn t0 = g_rdDestroyT;
    const LONG on0 = g_rdOn;
    const DWORD owner0 = g_async.owner, h0 = g_async.handle;
    const LONG st0 = g_async.state, js0 = g_jobs[3].state;
    const DWORD jh0 = g_jobs[3].handle;
    g_rdDestroyT = RdtLand, g_rdOn = 1, g_async.owner = GetCurrentThreadId(), g_async.state = A_IDLE;
    // no job names the route: destroyed at once
    RdDestroyHook(0x25000011);
    assert(g_rdtCalls == 1 && g_rdtLast == 0x25000011 && g_rdN == 0);
    // a queued job names it: deferred; the worker could still resolve the handle
    g_jobs[3].handle = 0x25000022, g_jobs[3].state = J_QUEUED;
    RdDestroyHook(0x25000022);
    assert(g_rdtCalls == 1 && g_rdN == 1 && g_rdDeferred >= 1);
    // the running job's own route (the worker between its lookup and its AddRef): deferred too
    g_jobs[3].state = J_RUNNING, g_async.state = A_RUNNING, g_async.handle = 0x25000022;
    RdDestroyHook(0x25000033);  // another route: destroyed now, the deferred one stays (a job runs: no flush)
    assert(g_rdtCalls == 2 && g_rdtLast == 0x25000033 && g_rdN == 1);
    g_async.handle = 0x25000044, g_jobs[3].handle = 0x25000044;
    RdDestroyHook(0x25000044);
    assert(g_rdtCalls == 2 && g_rdN == 2);
    // a flush while the job still holds one keeps it, runs the other
    g_async.handle = 0x25000022, g_jobs[3].handle = 0x25000022;
    RdFlush(false);
    assert(g_rdtCalls == 3 && g_rdtLast == 0x25000044 && g_rdN == 1);
    // inside a job callback, or off the owner thread: nothing runs
    g_jobs[3].state = J_DONE, g_async.state = A_IDLE;
    g_inInvoke++;
    RdFlush(false);
    g_inInvoke--;
    assert(g_rdtCalls == 3 && g_rdN == 1);
    // job done: the next poll destroys it
    RdFlush(false);
    assert(g_rdtCalls == 4 && g_rdtLast == 0x25000022 && g_rdN == 0);
    // closing: a route a job still holds is dropped, never run in the next world
    g_jobs[3].state = J_QUEUED, g_jobs[3].handle = 0x25000055;
    RdDestroyHook(0x25000055);
    const LONG drop0 = g_rdDropped;
    RdFlush(true);
    assert(g_rdtCalls == 4 && g_rdN == 0 && g_rdDropped == drop0 + 1);
    // switch off or another thread: straight through
    g_rdOn = 0;
    RdDestroyHook(0x25000055);
    assert(g_rdtCalls == 5 && g_rdN == 0);
    g_rdDestroyT = t0, g_rdOn = on0, g_async.owner = owner0, g_async.handle = h0, g_async.state = st0;
    g_jobs[3].state = js0, g_jobs[3].handle = jh0;
    g_rdDeferred = g_rdRun = g_rdFull = g_rdDropped = g_rdLog = 0;
    printf("route destroy defer: queued / running job's route deferred, others at once, flush keeps held ones, none in a job "
           "callback, dropped at closing, off = straight through: ok\n");
}
