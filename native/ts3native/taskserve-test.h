// docs/637 [RouteAsync] TaskServe, standalone (own events, no worker thread of the plugin): a poster thread stands in for the
// route worker in Proxy; TsRunTaskHook after a fake task run must serve its call on this (owner) thread only when the mode is
// on, this thread owns the running job and is not inside a served call. Included by selftest.cpp.
static DWORD g_tsRuns;
static void __fastcall TsFakeRunTask(void* host, void*, void* entry, DWORD a, DWORD b) {
    assert(host == (void*)g_fakeHost && entry == (void*)0x1234 && a == 1 && b == 0);
    g_tsRuns++;
}
static volatile LONG g_tsPosterDone;
static DWORD g_tsPosterResult;
static DWORD WINAPI TsPoster(LPVOID) {
    DWORD out = 0;
    InvokeJob v = {{&out, (void*)2, (void*)3, (void*)4}, (void*)1, NULL};
    Proxy(v);  // posts, then waits for g_invDone like the worker in a job
    g_tsPosterResult = out;
    InterlockedExchange(&g_tsPosterDone, 1);
    return 0;
}
static HANDLE TsPost() {
    g_tsPosterDone = 0;
    g_tsPosterResult = 0;
    HANDLE h = CreateThread(NULL, 0, TsPoster, NULL, 0, NULL);
    while (!g_invPosted) Sleep(0);
    return h;
}
static void TsRun() { TsRunTaskHook(g_fakeHost, NULL, (void*)0x1234, 1, 0); }

static void TestTaskServe() {
    TaskServeCounters& t = ts3native_taskserve;
    const HANDLE savedReady = g_invReady, savedDone = g_invDone, savedJob = g_jobReady;
    const AsyncSlot savedAsync = g_async;
    const HostCallFn savedHost = g_hostCallOrig;
    DWORD* const savedHostPtr = g_hostPtr;
    const TsRunTaskFn savedRun = g_tsRunTask;
    g_invReady = CreateEventW(NULL, FALSE, FALSE, NULL);
    g_invDone = CreateEventW(NULL, FALSE, FALSE, NULL);
    g_jobReady = CreateEventW(NULL, FALSE, FALSE, NULL);
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
    g_hostCallOrig = FakeHostCall;
    g_hostPtr = &g_fakeHostPtr;
    FakeTask(kRoot, 7);
    g_tsRunTask = TsFakeRunTask;
    memset(&t, 0, sizeof(t));
    g_async.owner = GetCurrentThreadId();
    g_async.state = A_RUNNING;
    g_tsRuns = 0;

    // 1. mode off: the task runs, the posted call stays posted (the READY poll would take it a tick later)
    HANDLE h = TsPost();
    TsRun();
    assert(g_tsRuns == 1 && g_invPosted && !g_tsPosterDone && t.checks == 0 && t.served == 0);
    // 2. on, but inside a served call / not the owner / no running job: not served here, counted as skipped
    t.mode = 1;
    g_inInvoke = 1;
    TsRun();
    g_inInvoke = 0;
    g_async.owner = GetCurrentThreadId() + 1;
    TsRun();
    g_async.owner = GetCurrentThreadId();
    g_async.state = A_IDLE;
    TsRun();
    g_async.state = A_RUNNING;
    assert(g_tsRuns == 4 && t.checks == 3 && t.skipped == 3 && t.served == 0 && g_invPosted && !g_tsPosterDone);
    // 3. on, owner, job running: served right after the task run, on this thread, with the task id cleared around it
    // (EA's guard), the id back after; the poster gets the call's own result
    // Proxy sets g_invPosted just before SetEvent(g_invReady): a task run in that window finds no event and leaves the call to
    // the next run (as in game), so run tasks until the poster is answered
    g_hostTid = 0;
    while (!g_tsPosterDone) TsRun();
    WaitForSingleObject(h, INFINITE);
    CloseHandle(h);
    assert(g_tsRuns >= 5 && t.served == 1 && !g_invPosted && g_tsPosterDone && g_tsPosterResult == 1 + 2 + 3 + 4);
    assert(g_hostTid == GetCurrentThreadId() && g_hostIdSeen == 0 && *(DWORD*)(g_fakeHost + 0xBD0) == 7);
    assert(t.maxQueueUs >= 0 && t.queueUs >= 0);
    // 4. nothing posted: a task run costs one flag read, nothing counted
    const LONG checks = t.checks;
    TsRun();
    assert(t.checks == checks && t.served == 1);
    // 5. the flag is cleared by any serve site: a READY-style serve takes the call, the next task run finds nothing
    h = TsPost();
    assert(WaitForSingleObject(g_invReady, INFINITE) == WAIT_OBJECT_0);  // the post's SetEvent may follow the flag
    ServeInvoke(HC_READY);
    WaitForSingleObject(h, INFINITE);
    CloseHandle(h);
    assert(!g_invPosted && g_tsPosterDone);
    TsRun();
    assert(t.checks == checks && t.served == 1);

    CloseHandle(g_invReady);
    CloseHandle(g_invDone);
    CloseHandle(g_jobReady);
    g_invReady = savedReady, g_invDone = savedDone, g_jobReady = savedJob;
    g_async = savedAsync;
    g_hostCallOrig = savedHost;
    g_hostPtr = savedHostPtr;
    g_tsRunTask = savedRun;
    memset(&t, 0, sizeof(t));
    printf("TaskServe (docs/637): off / nested / not owner / no job leave the call posted; on serves it after the task run on "
           "the owner with the task id guard; nothing posted = nothing counted; another site's serve clears the flag ok\n");
}
