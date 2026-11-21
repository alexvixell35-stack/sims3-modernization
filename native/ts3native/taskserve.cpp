// [RouteAsync] TaskServe (docs/637, default 0). Included by ts3native.cpp after locowin.cpp (savewrite.cpp's PatchCalls).
//
// A route job on the worker spends most of its wall time in Proxy, waiting for the job owner (the sim thread) to run the
// planner's managed calls (the portal-lock door asks, docs/159): 288 of 394 s of job time in phase-20261117-1604, 2 s of it
// running the calls. The owner picks a call up only at its serve sites: the waiting task's READY poll (one call, then the
// task sleeps a sim tick), a lock pump or a blocking job wait. A job with k calls therefore takes about k ticks, and the
// task's Simulator.Sleep(0) poll loop sees 4-6+ polls of ~37 ms for a job with a few ms of search.
//
// Fix: the four call sites of ScriptHost_RunTask 0xD7F000 (ThreadedObjects 0xD7FA74 / 0xD7FB53, TaskObjects 0xD7FE01,
// 0xD800FC) go to TsRunTaskHook, which runs the task as before and then, on the owner thread with a call posted, serves it
// at once. Same call, same thread, same order, same ServeInvoke (EA's task-id guard around it) as the READY serve; only
// sooner, between two tasks instead of inside the next poll. No spin and no wait: a check is one flag read (g_invPosted, set by
// Proxy) while nothing is posted, so the sim tick is not lengthened beyond the calls themselves (~13 us each, docs/159).
// Not served here: another thread, inside a served call (g_inInvoke: its nested plan's calls go through RunPlan's own
// wait), or no running async job (a sync worker plan's caller is blocked in RunPlan and serves its calls there).
// The waiting task still collects at its next poll (Sleep(0) resumes next tick at the earliest), so a plan's floor is one
// tick, as with READY; what goes is the tick per managed call.
// Call shape (objdump of the Steam 1.67 exe): thiscall(host, entry*, bool, bool), ret 0Ch -> __fastcall with edx unused.

typedef void(__fastcall* TsRunTaskFn)(void* host, void* edx, void* entry, DWORD a, DWORD b);
static const DWORD TS_RUNTASK = TS3A(0x00D7F000);
static TsRunTaskFn g_tsRunTask = (TsRunTaskFn)(uintptr_t)TS_RUNTASK;

// owner thread, after a task run: serve the call the running job has posted (one; the job posts its next only after this
// one returns, and that next one is taken after the next task run or at any other serve site)
static void TsServe() {
    TaskServeCounters& t = ts3native_taskserve;
    InterlockedIncrement(&t.checks);
    if (GetCurrentThreadId() != g_async.owner || g_inInvoke || g_async.state != A_RUNNING || !g_invJob) {
        InterlockedIncrement(&t.skipped);
        return;
    }
    if (WaitForSingleObject(g_invReady, 0) != WAIT_OBJECT_0) return;  // another site took it between the flag and here
    const LONGLONG post = g_invPostAt;
    ServeInvoke(HC_TASK);
    InterlockedIncrement(&t.served);
    if (post && g_qpf.QuadPart) {
        LARGE_INTEGER now;
        QueryPerformanceCounter(&now);
        const LONG us = (LONG)((now.QuadPart - post) * 1000000 / g_qpf.QuadPart);
        InterlockedExchangeAdd(&t.queueUs, us);
        Max(&t.maxQueueUs, us);
    }
}

static void __fastcall TsRunTaskHook(void* host, void* edx, void* entry, DWORD a, DWORD b) {
    g_tsRunTask(host, edx, entry, a, b);
    if (g_invPosted && ts3native_taskserve.mode) TsServe();
}

static const CallSite kTsSites[4] = {{TS3A(0x00D7FA74), TS_RUNTASK, (void*)TsRunTaskHook},
                                     {TS3A(0x00D7FB53), TS_RUNTASK, (void*)TsRunTaskHook},
                                     {TS3A(0x00D7FE01), TS_RUNTASK, (void*)TsRunTaskHook},
                                     {TS3A(0x00D800FC), TS_RUNTASK, (void*)TsRunTaskHook}};

// [RouteAsync] TaskServe=1: the four sites are byte-checked (E8 to 0xD7F000) and retargeted; any other value writes nothing.
// mode is the runtime switch (export +0x00); the sites stay patched when it is turned off (the hook then only calls through).
static void InstallTaskServe(const wchar_t* ini) {
    TaskServeCounters& t = ts3native_taskserve;
    if (!GetPrivateProfileIntW(L"RouteAsync", L"TaskServe", 0, ini)) { Log("route task serve off ([RouteAsync] TaskServe=0)"); return; }
    if (!ts3native_counters.asyncReady || !g_invReady) { Log("SKIP route task serve: needs the async route path"); return; }
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
    if (!PatchCalls("route task serve (ScriptHost_RunTask call sites)", kTsSites, 4)) return;
    t.ready = 1;
    t.mode = 1;
    Log("OK   route task serve: the job's managed calls are served after each script task run (TaskServe=1, docs/637)");
}
