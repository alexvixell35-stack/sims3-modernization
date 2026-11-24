// [RouteAsync] DestroyDefer=1 (docs/67 §47, rs-C4; default 0). Included by ts3native.cpp after p5c.cpp.
// IC_Route_DestroyRoute 0x750400 (cdecl, [esp+4] = route handle 0x25xxxxxx) runs only from ~Route (ScriptCore Route.cs: the
// finalizer; Dispose has no callers), on the sim thread. The route job's IC on the worker resolves the same handle by itself:
// IC_Route_Plan 0x765160 -> lookup 0x74B2C0 (script host slot +0xCC under the host lock, no reference taken, lock dropped)
// -> route slot +0x10 (AddRef) -> plan -> slot +0x14 (Release 0x8ABE60) [verified: disassembly TS3W 1.67]. The destroy
// (0x74E490: lookup, AddRef, unregister 0x74E410, Release) has no wait for the job, so a Route finalized while its job is
// queued or running (its task dropped without the CANCEL finally: a Sim destroyed mid-plan) either
//   - lands between the worker's lookup and its AddRef: the route is freed, the worker AddRefs, plans on and Releases the
//     freed block (a double free: the block later serves two objects; a still-registered route then shows a vtable that is
//     not in .rdata, the rs-C4 wild call at 0x74E4E9), or
//   - lands after the AddRef: the worker's Release becomes the last one and the route destructor runs on the worker thread.
// The fix: on the owner (sim) thread, a destroy of a handle that a QUEUED or RUNNING job names (or the running job's handle)
// is queued here and run on the owner when no job runs (the START / READY polls, the next destroy, save / closing /
// teardown entries). Only the sim moves a job to QUEUED and only the worker moves QUEUED to RUNNING, so a handle with no
// such job when the owner looks cannot be picked up by the worker later. Off the owner thread, or switch off: as before.
static const DWORD RDD_DESTROY = TS3A(0x00750400);
static const u8 kRddDestroy[6] = {0x8B, 0x0D, 0xC4, 0x40, 0x1D, 0x01};  // mov ecx,[0x11D40C4] (RouteManager), as c2diag
typedef void(__cdecl* RddDestroyFn)(DWORD handle);
static RddDestroyFn g_rddDestroyT;
static volatile LONG g_rddOn;
static const int kRddCap = 256;
struct RddRec { DWORD handle; LARGE_INTEGER t; };
static RddRec g_rddQ[kRddCap];
static LONG g_rddN;  // owner thread only
static volatile LONG g_rddDeferred, g_rddRun, g_rddFull, g_rddDropped, g_rddLog;
static int g_rddFlushing;

// true = a job of this route may still resolve its handle: queued, running, or the running job's own
static bool RddJobHolds(DWORD handle) {
    if (g_async.state == A_RUNNING && g_async.handle == handle) return true;
    for (int i = 0; i < kJobs; i++) {
        const AsyncJob& j = g_jobs[i];
        const LONG st = j.state;
        if ((st == J_QUEUED || st == J_RUNNING) && j.handle == handle) return true;
    }
    return false;
}
// the owner thread outside a job callback with no job running; closing = the world goes: entries a job still holds are
// dropped (leaked) rather than run against a later world's handles
static void RddFlush(bool closing) {
    if (!g_rddN || g_rddFlushing || GetCurrentThreadId() != g_async.owner || g_inInvoke) return;
    g_rddFlushing = 1;
    LONG keep = 0;
    for (LONG i = 0; i < g_rddN; i++) {
        const RddRec r = g_rddQ[i];
        if (RddJobHolds(r.handle)) {
            if (closing) { InterlockedIncrement(&g_rddDropped); continue; }
            g_rddQ[keep++] = r;
            continue;
        }
        InterlockedIncrement(&g_rddRun);
        if (InterlockedIncrement(&g_rddLog) <= 200)
            Log("route destroy defer: route %08lX destroyed now, %.1f ms after its finalizer (%ld deferred, %ld run)", r.handle,
                UsSince(r.t) / 1000.0, g_rddDeferred, g_rddRun);
        g_rddDestroyT(r.handle);
    }
    g_rddN = keep;
    g_rddFlushing = 0;
}
static void __cdecl RddDestroyHook(DWORD handle) {
    if (g_rddOn && GetCurrentThreadId() == g_async.owner && handle) {
        if (RddJobHolds(handle)) {
            if (g_rddN < kRddCap) {
                RddRec& r = g_rddQ[g_rddN++];
                r.handle = handle;
                QueryPerformanceCounter(&r.t);
                InterlockedIncrement(&g_rddDeferred);
                if (InterlockedIncrement(&g_rddLog) <= 200)
                    Log("route destroy defer: route %08lX finalized while its job %s (job route %08lX): destroy deferred, %ld queued",
                        handle, g_async.state == A_RUNNING && g_async.handle == handle ? "runs" : "is queued", g_async.handle, g_rddN);
                return;
            }
            InterlockedIncrement(&g_rddFull);  // full: as before (the old race stays possible for this one)
        }
        if (g_async.state != A_RUNNING) RddFlush(false);
    }
    g_rddDestroyT(handle);
}
static bool ApplyRouteDestroyDefer(const wchar_t* ini) {
    if (GetPrivateProfileIntW(L"RouteAsync", L"DestroyDefer", 0, ini) <= 0) return false;
#ifdef TS3A_TABLE
    if (ts3a_noaddr) { Log("SKIP route destroy defer ([RouteAsync] DestroyDefer): no EA 1.69 address"); return false; }
#endif
    if (g_c2DestroyT) {  // [Crash2Diag] owns the prologue (diagnostics only)
        Log("SKIP route destroy defer ([RouteAsync] DestroyDefer=1): [Crash2Diag] holds IC_Route_DestroyRoute");
        return false;
    }
    if (!InstallDetour("IC_Route_DestroyRoute (destroy defer)", RDD_DESTROY, kRddDestroy, sizeof(kRddDestroy), (void*)&RddDestroyHook,
                       &g_rddDestroyT))
        return false;
    InterlockedExchange(&g_rddOn, 1);
    Log("OK   route destroy defer ([RouteAsync] DestroyDefer=1, docs/67 §47): a route finalized while a job holds its handle is "
        "destroyed on the sim thread once no job runs");
    return true;
}
