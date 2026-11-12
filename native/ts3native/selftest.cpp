// Self-check for the patch helpers on local memory (no game needed). Built and run by build.cmd.
#define TS3NATIVE_NO_DLLMAIN
#define TS3N_FAULTINJ  // docs/157: the P5 / P5b / P5c fault-injection hooks (p4.cpp, p5.cpp), test builds only
#define TS3N_TEST_EXPECTED  // TestDetourCallRel: live bytes that differ from the literal, as on EA
#include "ts3native.cpp"
static void TestUfStrict();  // unseenfbc-test.h (docs/202 §3.1.1), run from the uf block
#include <assert.h>
// CI runners are shared and slow: wall-clock bounds and thread-interleaving checks flake there (docs/RELEASE.md, "CI-skipped
// selftests"). TIMING_ASSERT skips its condition on CI (GITHUB_ACTIONS / CI set) with one SKIPPED line per site; runs locally.
static bool StOnCi() { return GetEnvironmentVariableA("GITHUB_ACTIONS", NULL, 0) || GetEnvironmentVariableA("CI", NULL, 0); }
#define TIMING_ASSERT(name, cond)                                                                                     \
    do {                                                                                                              \
        if (!StOnCi()) assert(cond);                                                                                  \
        else {                                                                                                        \
            static bool said_;                                                                                        \
            if (!said_) said_ = true, printf("selftest %s SKIPPED on CI (timing-dependent, runs locally)\n", name); \
        }                                                                                                             \
    } while (0)
#include "savecompress-fake.h"
#include "snap-fake.h"
#include "replay-fake.h"

static int __cdecl Hook() { return 7; }

// --- route cache: key, table, and the hook against a fake Route_PlanLocked ---
static float g_pts[3][16];  // 64-byte route points: x,y,z at +0
static int g_status;        // what the fake path reports
static int g_origCalls;
static int __fastcall FakeStatus(void*, void*) { return g_status; }
static void* g_fakeVtbl[16];
static void* g_fakePath[2] = {g_fakeVtbl, 0};
static int g_node[3];  // list node: next, prev, path

static u8 __fastcall FakePlan(void*, void*, int* outList, int*, int*, DWORD, DWORD, u8*, int*, int*, DWORD, DWORD) {
    g_origCalls++;
    g_node[0] = g_node[1] = (int)(uintptr_t)outList;
    g_node[2] = (int)(uintptr_t)g_fakePath;
    outList[0] = outList[1] = (int)(uintptr_t)g_node;
    return g_status <= 2;
}

static void TestRouteCache() {
    u8 opts[0x38] = {0};
    int s[2] = {(int)(uintptr_t)g_pts[0], (int)(uintptr_t)g_pts[1]};
    int g[2] = {(int)(uintptr_t)g_pts[1], (int)(uintptr_t)g_pts[2]};
    g_pts[0][0] = 10.1f; g_pts[0][2] = 20.2f;
    g_pts[1][0] = 50.0f; g_pts[1][2] = 60.0f;
    u64 k = RouteKey(s, g, 0x100, opts, 5, 0);
    g_pts[0][0] = 10.4f;  // same 0.5 m cell
    assert(RouteKey(s, g, 0x100, opts, 5, 0) == k);
    g_pts[0][0] = 10.6f;  // next cell
    assert(RouteKey(s, g, 0x100, opts, 5, 0) != k);
    g_pts[0][0] = 10.1f;
    assert(RouteKey(s, g, 0x100, opts, 6, 0) != k);    // other follower
    assert(RouteKey(s, g, 0x800, opts, 5, 0) != k);    // other agent class
    opts[0x2C] = 0x10;                                 // RouteOption hi word bit
    assert(RouteKey(s, g, 0x100, opts, 5, 0) != k);
    opts[0x2C] = 0;
    g[1] += 64;                                        // one more goal
    assert(RouteKey(s, g, 0x100, opts, 5, 0) != k);
    g[1] -= 64;

    assert(CacheInit(300, 2000) && g_mask == 511);
    assert(!CacheHit(k, 1000));
    CacheStore(k, 1000);
    assert(CacheHit(k, 2999) && !CacheHit(k, 3000));   // window is exclusive
    CacheStore(k, 5000);                               // re-store refreshes, no duplicate slot
    int n = 0;
    for (DWORD i = 0; i <= g_mask; i++) n += g_tab[i].key == k;
    assert(n == 1 && CacheHit(k, 6000));
    // 9 live keys on one probe chain: the oldest is evicted, the table never grows
    for (u64 i = 0; i < 9; i++) CacheStore(512 * i + 7, 100 + i);
    assert(!CacheHit(7, 101) && CacheHit(512 * 8 + 7, 101) && CacheHit(512 + 7, 101));
    free(g_tab);

    // hook: 7/8 failure is stored, the repeat is answered without the original; success and 3 are never stored
    assert(CacheInit(4096, 2000));
    g_fakeVtbl[0x20 / 4] = (void*)FakeStatus;
    g_planOrig = FakePlan;
    int list[2];
    list[0] = list[1] = (int)(uintptr_t)list;
    g_status = 8;
    assert(PlanLockedHook(NULL, NULL, list, s, g, 0x100, 1, opts, NULL, NULL, 5, 0) == 0 && g_origCalls == 1);
    list[0] = list[1] = (int)(uintptr_t)list;
    assert(PlanLockedHook(NULL, NULL, list, s, g, 0x100, 1, opts, NULL, NULL, 5, 0) == 0 && g_origCalls == 1);
    assert(list[0] == (int)(uintptr_t)list);           // hit leaves the out list empty
    assert(ts3native_counters.hits == 1 && ts3native_counters.stored == 1);
    g_status = 3;
    assert(PlanLockedHook(NULL, NULL, list, s, g, 0x100, 1, opts, NULL, NULL, 6, 0) == 0 && g_origCalls == 2);
    assert(PlanLockedHook(NULL, NULL, list, s, g, 0x100, 1, opts, NULL, NULL, 6, 0) == 0 && g_origCalls == 3);
    g_status = 2;
    assert(PlanLockedHook(NULL, NULL, list, s, g, 0x100, 1, opts, NULL, NULL, 7, 0) == 1);
    assert(PlanLockedHook(NULL, NULL, list, s, g, 0x100, 1, opts, NULL, NULL, 7, 0) == 1 && g_origCalls == 5);
    assert(PlanLockedHook(NULL, NULL, list, s, g, 0x100, 0, opts, NULL, NULL, 5, 0) == 1 && g_origCalls == 6);  // doPlan=0 passes through
    assert(ts3native_counters.otherFails == 2 && ts3native_counters.hits == 1 && ts3native_counters.calls == 7);
}

// --- route worker: the original runs on the worker with the caller's FPU control word, composed with the cache ---
// MonoScriptHost stand-in: +0xBC8 -> slot -> the task's root interpreter state, +0xBD0 task id; g_fakeCur = current state
static u8 g_fakeHost[0xC00];
static DWORD g_fakeHostPtr, g_fakeCur, g_fakeRootSlot;
static const DWORD kRoot = 0x1000, kNested = 0x2000;
static void FakeTask(DWORD cur, DWORD id) {
    g_fakeHostPtr = (DWORD)(uintptr_t)g_fakeHost;
    g_fakeRootSlot = kRoot;
    *(DWORD**)(g_fakeHost + 0xBC8) = &g_fakeRootSlot;
    *(DWORD*)(g_fakeHost + 0xBD0) = id;
    g_fakeCur = cur;
}

// Host delegate call 0xD75FA0 stand-in: records its thread and that the task id pair was cleared around it
static DWORD g_hostTid, g_hostIdSeen;
static void* __fastcall FakeHostCall(void* self, void*, void* out, void* a2, void* a3, void* a4) {
    g_hostTid = GetCurrentThreadId();
    g_hostIdSeen = *(DWORD*)(g_fakeHost + 0xBD0) | *(DWORD*)(g_fakeHost + 0xBD4);
    *(DWORD*)out = (DWORD)(uintptr_t)self + (DWORD)(uintptr_t)a2 + (DWORD)(uintptr_t)a3 + (DWORD)(uintptr_t)a4;
    return out;
}
static int g_callHost;  // WorkerPlan makes a host delegate call (through the vtable in the game)
static DWORD g_planTid;
static unsigned short g_planCw;
static int g_reenter;
static int g_callInvoke;      // WorkerPlan makes a managed call through the runtime_invoke slot
static DWORD g_fakeSlot;      // stands in for 0x1195B60
static DWORD g_invokeTid;
static int g_invokeNested;    // the managed call starts a plan of its own
static void* __cdecl FakeInvoke(void* method, void* obj, void**, void** exc) {
    g_invokeTid = GetCurrentThreadId();
    if (exc) *exc = NULL;
    if (g_invokeNested) {  // like script code planning a route from inside the callback: back to the worker
        g_invokeNested = 0;
        u8 opts[0x38] = {0};
        int s[2] = {1, 2}, g[2] = {3, 4}, list[2];
        assert(PlanLockedHook(NULL, NULL, list, s, g, 0x100, 1, opts, NULL, NULL, 1, 0) == 1 && g_planTid == g_workerTid);
    }
    return (char*)obj + (uintptr_t)method;
}
static u8 __fastcall WorkerPlan(void*, void*, int* outList, int* starts, int* goals, DWORD agent, DWORD, u8* opts, int*, int*,
                                DWORD guidLo, DWORD) {
    g_planTid = GetCurrentThreadId();
    g_planCw = GetX87();
    if (g_reenter) {  // a plan started on the worker runs there directly instead of waiting for itself
        g_reenter = 0;
        assert(PlanLockedHook(NULL, NULL, outList, starts, goals, agent, 1, opts, NULL, NULL, 1, 0) == 1);
        assert(g_planTid == GetCurrentThreadId());
    }
    if (g_callInvoke) {  // the managed call runs on the waiting caller, the plan continues here with its result
        g_callInvoke = 0;
        void* r = ((InvokeFn)(uintptr_t)g_fakeSlot)((void*)1, (void*)0x100, NULL, NULL);
        assert(r == (void*)0x101 && g_invokeTid != GetCurrentThreadId());
        g_planTid = GetCurrentThreadId();
        g_planCw = GetX87();
    }
    if (g_callHost) {  // the whole host call runs on the waiting caller, with the task id pair cleared there, then restored
        g_callHost = 0;
        DWORD out = 0;
        assert(HostCallHook((void*)0x10, NULL, &out, (void*)1, (void*)2, (void*)3) == &out && out == 0x16);
        assert(g_hostTid != GetCurrentThreadId() && g_hostIdSeen == 0);
    }
    return (u8)guidLo;  // echo, so each call's own result comes back
}

static void TestRouteWorker() {
    free(g_tab);
    g_tab = NULL;  // worker only
    assert(StartWorker());
    ts3native_counters.workerReady = 1;
    g_planOrig = WorkerPlan;
    u8 opts[0x38] = {0};
    int s[2] = {(int)(uintptr_t)g_pts[0], (int)(uintptr_t)g_pts[1]};
    int g[2] = {(int)(uintptr_t)g_pts[1], (int)(uintptr_t)g_pts[2]};
    int list[2];
    const DWORD me = GetCurrentThreadId();
    const Ts3nCounters c0 = ts3native_counters;
    g_hostPtr = &g_fakeHostPtr;
    FakeTask(kRoot, 7);
    g_hostCallOrig = FakeHostCall;

    ts3native_counters.workerMode = 0;
    assert(PlanLockedHook(NULL, NULL, list, s, g, 0x100, 1, opts, NULL, NULL, 1, 0) == 1 && g_planTid == me);
    assert(PlanLockedHook(NULL, NULL, list, s, g, 0x100, 1, opts, NULL, NULL, 0, 0) == 0);
    assert(ts3native_counters.planOffCalls - c0.planOffCalls == 2 && ts3native_counters.planOffFails - c0.planOffFails == 1);
    ts3native_counters.workerMode = 1;
    // runtime_invoke slot not as expected (Mono not up, or another build): refused, plans stay on the caller
    g_invokeSlot = &g_fakeSlot;
    g_invokeCheckBytes = false;
    g_invokeExpect = (DWORD)(uintptr_t)FakeInvoke;
    g_fakeSlot = 0xEA1080;
    assert(PlanLockedHook(NULL, NULL, list, s, g, 0x100, 1, opts, NULL, NULL, 1, 0) == 1 && g_planTid == me);
    assert(ts3native_counters.invokeHook == -1 && g_fakeSlot == 0xEA1080);
    ts3native_counters.invokeHook = 0;
    g_fakeSlot = (DWORD)(uintptr_t)FakeInvoke;
    // slot as expected, prologue bytes differ (FakeInvoke's): refused unless they matched at load (Frida hooked it later)
    g_invokeCheckBytes = true;
    g_invokePrologueAtLoad = false;
    assert(PlanLockedHook(NULL, NULL, list, s, g, 0x100, 1, opts, NULL, NULL, 1, 0) == 1 && g_planTid == me);
    assert(ts3native_counters.invokeHook == -1 && g_fakeSlot == (DWORD)(uintptr_t)FakeInvoke);
    ts3native_counters.invokeHook = 0;
    g_invokePrologueAtLoad = true;
    const unsigned short cw = GetX87();
    SetX87((unsigned short)(cw & ~0x300));  // 24-bit precision, as D3D9 can leave the game thread
    assert(PlanLockedHook(NULL, NULL, list, s, g, 0x100, 1, opts, NULL, NULL, 0, 0) == 0);
    assert(g_planTid == g_workerTid && g_planTid != me && g_planCw == GetX87());
    SetX87(cw);
    assert(PlanLockedHook(NULL, NULL, list, s, g, 0x100, 1, opts, NULL, NULL, 1, 0) == 1 && g_planCw == cw);
    assert(ts3native_counters.invokeHook == 1 && g_fakeSlot == (DWORD)(uintptr_t)InvokeHook);
    assert(((InvokeFn)(uintptr_t)g_fakeSlot)((void*)2, (void*)0x100, NULL, NULL) == (void*)0x102 && g_invokeTid == me);  // other threads: pass-through
    g_callInvoke = 1;
    assert(PlanLockedHook(NULL, NULL, list, s, g, 0x100, 1, opts, NULL, NULL, 1, 0) == 1 && g_invokeTid == me && g_planTid == g_workerTid);
    g_callInvoke = g_invokeNested = 1;  // worker plan -> managed call on the caller -> nested plan on the worker
    assert(PlanLockedHook(NULL, NULL, list, s, g, 0x100, 1, opts, NULL, NULL, 1, 0) == 1 && g_invokeTid == me && !g_invokeNested);
    g_reenter = 1;
    assert(PlanLockedHook(NULL, NULL, list, s, g, 0x100, 1, opts, NULL, NULL, 1, 0) == 1);
    // host delegate call on the worker: runs on the caller; the caller's task id pair is back afterwards
    g_callHost = 1;
    assert(PlanLockedHook(NULL, NULL, list, s, g, 0x100, 1, opts, NULL, NULL, 1, 0) == 1 && g_hostTid == me && !g_callHost);
    assert(*(DWORD**)(g_fakeHost + 0xBC8) == &g_fakeRootSlot && *(DWORD*)(g_fakeHost + 0xBD0) == 7 && ts3native_counters.hostCalls == 1);
    DWORD hostOut = 0;  // other threads: pass-through
    assert(HostCallHook((void*)0x10, NULL, &hostOut, NULL, NULL, NULL) == &hostOut && hostOut == 0x10 && g_hostTid == me);
    assert(PlanLockedHook(NULL, NULL, list, s, g, 0x100, 0, opts, NULL, NULL, 1, 0) == 1 && g_planTid == me);  // doPlan=0 stays
    const Ts3nCounters& c = ts3native_counters;
    assert(c.workerCalls - c0.workerCalls == 7 && c.fallbacks - c0.fallbacks == 1 && c.watchdogs == 0 && c.invokes == 2);
    assert(c.planOnCalls - c0.planOnCalls == 10 && c.planOnFails - c0.planOnFails == 1);  // nested plans count too
    assert(c.waitUs >= c.workerUs);

    // with the cache: a 7/8 failure found on the worker is stored, the repeat is a hit that never reaches the worker
    assert(CacheInit(4096, 2000));
    g_planOrig = FakePlan;
    g_status = 8;
    const LONG calls0 = c.workerCalls, hits0 = c.hits;
    list[0] = list[1] = (int)(uintptr_t)list;
    assert(PlanLockedHook(NULL, NULL, list, s, g, 0x100, 1, opts, NULL, NULL, 9, 0) == 0);
    list[0] = list[1] = (int)(uintptr_t)list;
    assert(PlanLockedHook(NULL, NULL, list, s, g, 0x100, 1, opts, NULL, NULL, 9, 0) == 0);
    assert(c.workerCalls - calls0 == 1 && c.hits - hits0 == 1);
    ts3native_counters.workerMode = 0;
}

// --- route budget: stubs in place of the A*'s cost-function pointers, against a fake A* ---
static int g_costCalls;
static float __cdecl FakeCost(DWORD, DWORD, DWORD, DWORD, DWORD isEdge, DWORD, DWORD, DWORD) { g_costCalls++; return isEdge ? 1.0f : 2.0f; }
typedef float(__cdecl* CostFn)(DWORD, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD);
static CostFn g_cost;  // what the A* would load from [esp+0x60]
static int g_searchNodes;

// Like 0x676260: initial heuristic, then per expansion one edge cost; an edge below the wall generates a node (heuristic).
// Succeeds after 1000 nodes; once every edge is a wall the open list empties and it fails.
static u8 __fastcall FakeSearch(void*, void*, int*, int*, int*, DWORD, DWORD, u8*, int*, int*, DWORD, DWORD) {
    g_cost(0, 0, 0, 0, 0, 0, 0, 0);
    for (g_searchNodes = 0; g_searchNodes < 1000; g_searchNodes++) {
        if (g_cost(0, 0, 0, 0, 1, 0, 0, 0) >= 1e8f) return 0;
        assert(g_cost(0, 0, 0, 0, 0, 0, 0, 0) == 2.0f);
    }
    return 1;
}

static void TestRouteBudget() {
    u8 code[5][8];
    DWORD sites[5], fns[5];
    for (int i = 0; i < 5; i++) {
        fns[i] = (DWORD)(uintptr_t)FakeCost;
        sites[i] = (DWORD)(uintptr_t)code[i];
        const u8 mov[4] = {0xC7, 0x44, 0x24, 0x60};
        memcpy(code[i], mov, 4);
        memcpy(code[i] + 4, &fns[i], 4);
    }
    code[3][7] ^= 1;
    assert(!PatchCostSites(sites, fns) && *(DWORD*)(code[0] + 4) == fns[0]);  // one differs: nothing written
    code[3][7] ^= 1;
    assert(PatchCostSites(sites, fns));
    g_cost = *(CostFn*)(code[2] + 4);
    assert((DWORD)(uintptr_t)g_cost != fns[2]);

    QueryPerformanceFrequency(&g_qpf);
    ts3native_counters.workerReady = 0;
    ts3native_counters.budgetReady = 1;
    g_planOrig = FakeSearch;
    u8 opts[0x38] = {0};
    int list[2];
    const Ts3nCounters& c = ts3native_counters;

    ts3native_counters.budgetMode = 0;  // switch off: stubs pass through
    g_costCalls = 0;
    assert(CallPlan(NULL, NULL, list, NULL, NULL, 0, 1, opts, NULL, NULL, 0, 0) == 1 && g_costCalls == 2001 && c.budgetCalls == 0);
    ts3native_counters.budgetMode = 1;
    g_maxNodes = 100000;  // above the search: same result, counted
    assert(CallPlan(NULL, NULL, list, NULL, NULL, 0, 1, opts, NULL, NULL, 0, 0) == 1);
    assert(c.budgetCalls == 1 && c.budgetCapped == 0 && c.budgetMaxNodesOk == 1001 && c.budgetMaxNodes == 1001);
    g_maxNodes = 300;  // capped: the next edge is a wall, the search fails through its own exit
    assert(CallPlan(NULL, NULL, list, NULL, NULL, 0, 1, opts, NULL, NULL, 0, 0) == 0);
    assert(g_searchNodes == 300 && c.budgetCapped == 1 && c.budgetCapNodes == 301 && c.budgetMaxNodesOk == 1001);
    assert(!t_budget.on);
    g_costCalls = 0;  // no budget outside a request: a search not started by Route_PlanLocked is untouched
    assert(FakeSearch(NULL, NULL, NULL, NULL, NULL, 0, 0, NULL, NULL, NULL, 0, 0) == 1 && g_costCalls == 2001);
    g_onlyFlagged = 1;  // only routes with DebugPathPlan (hi word bit 0x1)
    assert(CallPlan(NULL, NULL, list, NULL, NULL, 0, 1, opts, NULL, NULL, 0, 0) == 1 && c.budgetCalls == 2);
    opts[0x2C] = 1;
    assert(CallPlan(NULL, NULL, list, NULL, NULL, 0, 1, opts, NULL, NULL, 0, 0) == 0 && c.budgetCapped == 2);
    g_onlyFlagged = 0;
    g_maxNodes = 0;
    g_maxUs = 5000;  // time cap: checked every 16 nodes
    g_planOrig = [](void*, void*, int*, int*, int*, DWORD, DWORD, u8*, int*, int*, DWORD, DWORD) -> u8 {
        g_cost(0, 0, 0, 0, 0, 0, 0, 0);
        for (;;) {
            if (g_cost(0, 0, 0, 0, 1, 0, 0, 0) >= 1e8f) return 0;
            g_cost(0, 0, 0, 0, 0, 0, 0, 0);
            Sleep(1);
        }
    };
    assert(CallPlan(NULL, NULL, list, NULL, NULL, 0, 1, opts, NULL, NULL, 0, 0) == 0 && c.budgetCapped == 3 && c.budgetCapUs >= 5000);
    // clock starts at the first node: 30 ms waiting for the routing lock, then a quick search, is not capped,
    // but the request time in the counters still includes the wait
    g_planOrig = [](void* w, void* e, int* l, int* s, int* g, DWORD a, DWORD d, u8* o, int* o1, int* o2, DWORD gl, DWORD gh) -> u8 {
        Sleep(30);
        return FakeSearch(w, e, l, s, g, a, d, o, o1, o2, gl, gh);
    };
    assert(CallPlan(NULL, NULL, list, NULL, NULL, 0, 1, opts, NULL, NULL, 0, 0) == 1 && c.budgetCapped == 3 && c.budgetMaxUsOk >= 30000);
    g_maxUs = 0;
    // docs/134 SyncCapMs: this thread's sync fallback cap ends an endless search with the global budget off, notes the hit
    ts3native_counters.budgetMode = 0;
    g_planOrig = [](void*, void*, int*, int*, int*, DWORD, DWORD, u8*, int*, int*, DWORD, DWORD) -> u8 {
        g_cost(0, 0, 0, 0, 0, 0, 0, 0);
        for (;;) {
            if (g_cost(0, 0, 0, 0, 1, 0, 0, 0) >= 1e8f) return 0;
            g_cost(0, 0, 0, 0, 0, 0, 0, 0);
            Sleep(1);
        }
    };
    t_fbCapUs = 5000;
    t_fbCapHit = 0;
    assert(CallPlan(NULL, NULL, list, NULL, NULL, 0, 1, opts, NULL, NULL, 0, 0) == 0 && c.budgetCapped == 4 && t_fbCapHit == 1 && !t_budget.on);
    t_fbCapUs = t_fbCapHit = 0;
    ts3native_counters.budgetMode = 1;
    g_costCalls = 0;
    g_planOrig = FakeSearch;  // no cap on this thread and the global one has no limits: untouched, not counted as capped
    assert(CallPlan(NULL, NULL, list, NULL, NULL, 0, 1, opts, NULL, NULL, 0, 0) == 1 && c.budgetCapped == 4);

    // per-thread state: a capped plan on another thread in the middle of ours neither ends nor feeds our budget
    g_maxNodes = 300;
    g_planOrig = [](void*, void*, int*, int*, int*, DWORD, DWORD, u8*, int*, int*, DWORD, DWORD) -> u8 {
        g_cost(0, 0, 0, 0, 0, 0, 0, 0);
        for (int i = 0; i < 1000; i++) {
            if (g_cost(0, 0, 0, 0, 1, 0, 0, 0) >= 1e8f) return 0;
            g_cost(0, 0, 0, 0, 0, 0, 0, 0);
            if (i == 200) {
                HANDLE t = CreateThread(NULL, 0, [](LPVOID) -> DWORD {
                    u8 o[0x38] = {0};
                    int l[2];
                    g_planOrig = FakeSearch;  // the other thread's own capped search
                    return CallPlan(NULL, NULL, l, NULL, NULL, 0, 1, o, NULL, NULL, 0, 0);
                }, NULL, 0, NULL);
                DWORD rc = 1;
                WaitForSingleObject(t, INFINITE);
                GetExitCodeThread(t, &rc);
                CloseHandle(t);
                assert(rc == 0 && t_budget.on && t_budget.nodes == 202 && !t_budget.capped);
            }
        }
        return 1;
    };
    const LONG capped0 = c.budgetCapped;
    assert(CallPlan(NULL, NULL, list, NULL, NULL, 0, 1, opts, NULL, NULL, 0, 0) == 0 && c.budgetCapped - capped0 == 2);

    // on the route worker: the worker's own budget caps the plan, the caller's stays off
    ts3native_counters.workerReady = ts3native_counters.workerMode = 1;
    g_planOrig = FakeSearch;
    assert(RunPlan(NULL, NULL, list, NULL, NULL, 0, 1, opts, NULL, NULL, 0, 0) == 0 && c.budgetCapped - capped0 == 3);
    assert(g_searchNodes == 300 && !t_budget.on);
    ts3native_counters.workerReady = ts3native_counters.workerMode = 0;
    g_maxNodes = 0;
    ts3native_counters.budgetMode = 0;
    ts3native_counters.budgetReady = 0;
}

// --- async route plan: START/READY/CANCEL handshake, collect, drop, refuse, lock pump, plans queued behind a job ---
static CRITICAL_SECTION g_fakeLock;  // stands in for the routing lock world+0x918
static volatile LONG g_icHoldLock, g_icLockTaken, g_icInvoke, g_icSleepMs;
static volatile LONG g_icInClose;  // CE-1 (docs/154): IC bodies run while rtClosing is set (a world close already drained)
static LONG __fastcall FakeLockAcquire(void* lock, void*, const DWORD* deadline) {
    if (*deadline == 0xFFFFFFFF) { EnterCriticalSection((CRITICAL_SECTION*)lock); return 1; }
    return TryEnterCriticalSection((CRITICAL_SECTION*)lock) ? 1 : -2;
}
static LONG __fastcall FakeLockRelease(void* lock, void*) { LeaveCriticalSection((CRITICAL_SECTION*)lock); return 0; }
static u8 __cdecl FakeGetOption(DWORD, DWORD, DWORD, DWORD) { return 0; }  // the game: bit 58 is never set
static u8* __cdecl FakeIc(u8* out, DWORD handle) {
    memset(out, 0, 0x48);
    *(DWORD*)out = 1;  // Succeeded
    *(DWORD*)(out + 4) = handle;
    *(DWORD*)(out + 0x40) = GetCurrentThreadId();
    if (ts3native_counters.rtClosing) InterlockedIncrement(&g_icInClose);
    if (g_icHoldLock) { EnterCriticalSection(&g_fakeLock); g_icLockTaken = 1; }
    if (g_icInvoke) {  // the portal-lock query: runs on the owner even though it is not waiting for us
        g_icInvoke = 0;
        void* r = ((InvokeFn)(uintptr_t)g_fakeSlot)((void*)1, (void*)0x100, NULL, NULL);
        assert(r == (void*)0x101 && g_invokeTid != GetCurrentThreadId());
    }
    if (g_icSleepMs) Sleep(g_icSleepMs);
    if (g_icHoldLock) { g_icHoldLock = g_icLockTaken = 0; LeaveCriticalSection(&g_fakeLock); }
    return out;
}
static const DWORD kH1 = 0x25000001, kH2 = 0x25000002;
static u8 Start(DWORD h) { return GetOptionHook(h, 0, kAsyncOptHi, 0); }
static u8 Ready(DWORD h) { return GetOptionHook(h, 0, kAsyncOptHi, 1); }
static void Cancel(DWORD h) { assert(GetOptionHook(h, 0, kAsyncOptHi | kAsyncCancelHi, 1) == 1); }
static void WaitReady(DWORD h) { while (!Ready(h)) Sleep(1); }
static u8 NoYield(DWORD h) { return GetOptionHook(h, 0, kAsyncOptHi | kNoYieldHi, 1); }

// --- H2 locomotion window (locowin.cpp): thunk convention, hold, running at entry, drop before a wait, watchdog, nesting ---
static void* g_lpSelf;
static float g_lpDt;
static int g_lpCase, g_lpRuns;
static DWORD g_lpMs;  // case 4: how long the raw wait took
static void __fastcall FakePass(void* self, void*, float dt) {
    g_lpSelf = self;
    g_lpDt = dt;
    g_lpRuns++;
    const Ts3nCounters& c = ts3native_counters;
    u8 out[0x48];
    const int which = g_lpCase;
    g_lpCase = 0;  // a nested pass runs case 0
    switch (which) {
        case 1:  // Mode 1: a job queued inside the pass is not started before it returns (claimed at most, held)
            assert(g_g0Win == 1 && g_g0wIn == 1 && t_g0wDepth == 1);
            assert(Start(kH1));
            Sleep(60);
            assert(g_async.state != A_RUNNING && JobFor(kH1)->state != J_DONE && c.g0wJobsHeld >= 1);
            break;
        case 2:  // a job running at entry finishes inside; the one queued meanwhile is held
            assert(JobFor(kH1)->state == J_RUNNING || JobFor(kH1)->state == J_DONE);
            assert(Start(kH2));
            while (JobFor(kH1)->state != J_DONE) Sleep(1);  // waits only for the running job: allowed
            Sleep(40);
            assert(JobFor(kH2)->state != J_DONE && g_async.state != A_RUNNING);
            break;
        case 3: {  // a sim wait for a held job drops the window first and completes (no 250 ms watchdog)
            assert(Start(kH1));
            while (JobFor(kH1)->state != J_RUNNING) Sleep(0);  // claimed by the worker, start held
            Sleep(10);
            assert(g_async.state != A_RUNNING);
            const DWORD t0 = GetTickCount();
            assert(IcPlanHook(out, kH1) == out && *(DWORD*)(out + 0x40) == GetCurrentThreadId());  // not READY: planned here
            assert(g_g0Win == 0 && g_g0wIn == 1 && c.g0wDrop[G0W_JOBWAIT] == 1);
            TIMING_ASSERT("H2 window job-wait plan < 200 ms", GetTickCount() - t0 < 200);
            break;
        }
        case 4: {  // [RouteWorker] Mode 1 sync plan while a start is held: dropped, the worker runs the job then the plan
            assert(Start(kH1));
            while (JobFor(kH1)->state != J_RUNNING) Sleep(0);
            u8 opts[0x38] = {0};
            int s[2] = {1, 2}, g[2] = {3, 4}, list[2];
            assert(PlanLockedHook(NULL, NULL, list, s, g, 0x100, 1, opts, NULL, NULL, 1, 0) == 1 && g_planTid == g_workerTid);
            assert(g_g0Win == 0 && c.g0wDrop[G0W_SYNCPLAN] == 1);
            break;
        }
        case 5: {  // a wait no G0wDrop covers: the worker lets go after 250 ms (watchdog), no deadlock
            assert(Start(kH1));
            const DWORD t0 = GetTickCount();
            while (JobFor(kH1)->state != J_DONE) Sleep(1);
            g_lpMs = GetTickCount() - t0;
            break;
        }
        case 6:  // nested pass: the inner call leaves the outer window alone
            LocoWinThunk((void*)0x77, NULL, 2.5f);
            assert(g_lpSelf == (void*)0x77 && g_lpDt == 2.5f && g_g0Win == 1 && g_g0wIn == 1 && t_g0wDepth == 1);
            g_lpSelf = self;
            g_lpDt = dt;
            break;
        case 7:  // Mode 0: count only, the job starts inside
            assert(g_g0Win == 0 && g_g0wIn == 1);
            assert(Start(kH1));
            while (JobFor(kH1)->state != J_DONE) Sleep(1);
            break;
        case 8:  // an exception leaves the pass: the window must not stay open
            RaiseException(0xE0000001, 0, 0, NULL);
        case 9: {  // CE-1 (docs/154): travel / quit drains while a claimed job's start is held (after ServeAsync's world check,
                   // before A_RUNNING); RT_CLOSING's body (rtsites.cpp): DiscDrain, rtClosing = 1, RtWaitJob (A_IDLE: no wait)
            const LONG held0 = c.g0wJobsHeld;
            assert(Start(kH1));
            while (c.g0wJobsHeld == held0) Sleep(0);  // the worker passed the world check (rtClosing 0) and spins in G0wHolds
            assert(g_async.state != A_RUNNING && JobFor(kH1)->state == J_RUNNING);
            LONG us = 0;
            InterlockedExchange(&ts3native_counters.rtClosing, 1);
            assert(RtWaitJob(&us, 0x02000000 | RT_CLOSING) && us == 0);  // drained: the teardown may run from here on
            break;
        }
    }
}
// the thunk as the game calls it: ecx = self, one float pushed, callee pops it (ret 4)
static void* volatile g_ctSelf;
static volatile float g_ctDt;
static void* g_ctThunk = (void*)LocoWinThunk;
static volatile DWORD g_ctEsp0, g_ctEsp1;
static __declspec(noinline) void CallThunk(void* self, float dt) {
    g_ctSelf = self;
    g_ctDt = dt;
    __asm {
        mov g_ctEsp0, esp
        push g_ctDt
        mov ecx, g_ctSelf
        call g_ctThunk
        mov g_ctEsp1, esp
    }
    assert(g_ctEsp0 == g_ctEsp1 && g_lpSelf == g_ctSelf && g_lpDt == g_ctDt);
}
static void Collect(DWORD h) {
    u8 out[0x48];
    WaitReady(h);
    assert(IcPlanHook(out, h) == out && *(DWORD*)(out + 0x40) == g_workerTid && *(DWORD*)(out + 4) == h);
}
static void TestLocoWindow() {
    static_assert(offsetof(Ts3nCounters, g0wReady) == 0x13E4 && offsetof(Ts3nCounters, g0wMode) == 0x13E8 &&
                  offsetof(Ts3nCounters, g0wDrop) == 0x140C && offsetof(Ts3nCounters, g0wGate0Us) == 0x1420 &&
                  sizeof(Ts3nCounters) >= 1289 * 4, "v37 layout");
    Ts3nCounters& k = ts3native_counters;
    const LONG wd0 = k.watchdogs;
    g_g0wOrig = FakePass;
    k.g0wReady = 1;
    k.g0wMode = 1;
    g_icSleepMs = 0;
    // 1. hold, through the game's calling convention
    g_lpCase = 1;
    CallThunk((void*)0x55, 1.5f);
    assert(g_g0Win == 0 && g_g0wIn == 0 && t_g0wDepth == 0 && g_rgOffSimOpen == 0);
    Collect(kH1);
    assert(k.g0wPasses == 1 && k.g0wJobsHeld == 1 && k.g0wJobStartInPass == 0 && k.g0wHoldUs >= 40000 && k.g0wRunningAtEntry == 0);
    // 2. running at entry
    g_icSleepMs = 50;
    assert(Start(kH1));
    while (g_async.state != A_RUNNING) Sleep(0);
    g_lpCase = 2;
    CallThunk((void*)0x55, 1.0f);
    assert(k.g0wRunningAtEntry == 1 && k.g0wJobStartInPass == 0 && k.g0wJobsHeld == 2);
    Collect(kH1);
    Collect(kH2);
    g_icSleepMs = 0;
    // 3. drop before WaitJobDone
    g_lpCase = 3;
    CallThunk((void*)0x55, 1.0f);
    assert(k.g0wDrop[G0W_JOBWAIT] == 1 && k.g0wWatchdog == 0 && k.g0wJobStartInPass == 1);  // it started after the drop
    // 4. drop before a sync worker plan
    k.workerReady = k.workerMode = 1;
    g_planOrig = WorkerPlan;
    g_lpCase = 4;
    CallThunk((void*)0x55, 1.0f);
    k.workerReady = k.workerMode = 0;
    Collect(kH1);
    assert(k.g0wDrop[G0W_SYNCPLAN] == 1 && k.g0wWatchdog == 0);
    // 5. watchdog: a raw wait with the window up ends after ~250 ms, logged once
    g_lpCase = 5;
    CallThunk((void*)0x55, 1.0f);
    assert(g_lpMs >= 240 && k.g0wWatchdog == 1 && k.g0wHoldMaxUs >= 240000);
    TIMING_ASSERT("H2 watchdog wait < 2 s", g_lpMs < 2000);
    Collect(kH1);
    // 6. nested
    const LONG passes = k.g0wPasses;
    g_lpCase = 6;
    CallThunk((void*)0x55, 3.0f);
    assert(g_lpRuns > 0 && k.g0wPasses == passes + 1 && g_g0Win == 0 && g_g0wIn == 0);
    // 7. Mode 0 counts the start it would have held
    k.g0wMode = 0;
    const LONG held = k.g0wJobsHeld, inPass = k.g0wJobStartInPass;
    g_lpCase = 7;
    CallThunk((void*)0x55, 1.0f);
    assert(k.g0wJobsHeld == held && k.g0wJobStartInPass == inPass + 1);
    Collect(kH1);
    // 8. an exception through the pass closes the window
    k.g0wMode = 1;
    g_lpCase = 8;
    __try { LocoWinThunk((void*)0x55, NULL, 1.0f); } __except (EXCEPTION_EXECUTE_HANDLER) {}
    assert(g_g0Win == 0 && g_g0wIn == 0 && t_g0wDepth == 0 && k.g0wPasses == passes + 3);
    // nothing left: no job, no window, no open off-sim frame, only the deliberate watchdog
    assert(!AsyncBusy() && g_rgOffSimOpen == 0 && k.watchdogs == wd0 && k.g0wPassMaxUs >= 240000);
    // 9. CE-1: the held job starts after the pass, past the closing drain: it must back out (J_SKIPPED, IC not run), and the
    //    route plans synchronously at its collect as with an early refusal
    const LONG skip0 = k.asyncSkipped;
    g_icInClose = 0;
    g_lpCase = 9;
    CallThunk((void*)0x55, 1.0f);
    while (JobFor(kH1)->state == J_RUNNING) Sleep(0);
    if (g_icInClose || JobFor(kH1)->state != J_SKIPPED)
        printf("CE-1 FAIL: job state %ld, IC ran %ld time(s) after the closing drain\n", JobFor(kH1)->state, g_icInClose);
    assert(g_icInClose == 0 && JobFor(kH1)->state == J_SKIPPED && k.asyncSkipped == skip0 + 1 && g_async.state == A_IDLE);
    u8 out9[0x48];
    assert(IcPlanHook(out9, kH1) == out9 && *(DWORD*)(out9 + 0x40) == GetCurrentThreadId());  // the game's sync answer
    assert(TryEnterCriticalSection((CRITICAL_SECTION*)g_rgLock));  // the worker let +0x918 go on the way out
    LeaveCriticalSection((CRITICAL_SECTION*)g_rgLock);
    k.rtClosing = 0;
    assert(!AsyncBusy() && g_rgOffSimOpen == 0 && g_g0Win == 0 && g_g0wIn == 0);
    g_g0wOrig = (LocoUpdateFn)(uintptr_t)LOCO_UPDATE;
    memset(&k.g0wReady, 0, sizeof(LONG) * 16);
}

// --- H2 threaded (docs/149): fake passes that take the real routing lock through LockAcquireHook, jobs queued inside and
// between passes, real worker. Mode 1: no job starts inside a pass (except one running at entry), none lost or starved, no
// deadlock; Mode 0 shows the check can fail (starts land inside) ---
static volatile LONG g_tpIn, g_tpEntryHandle, g_tpViolations, g_tpStarts;
static volatile LONG g_tpQueueInPass;
static DWORD g_tpOut[4];  // handles started and not yet collected (0 = free)
static DWORD g_tpNext;
static u8* __cdecl TpIc(u8* out, DWORD handle) {  // worker: the job's start, then hold the lock a while (ServeAsync holds it)
    InterlockedIncrement(&g_tpStarts);
    if (g_tpIn && (LONG)handle != g_tpEntryHandle) InterlockedIncrement(&g_tpViolations);
    return FakeIc(out, handle);
}
static int TpQueue() {  // owner thread only (START is the sim's)
    for (int i = 0; i < 4; i++)
        if (!g_tpOut[i]) {
            const DWORD h = 0x26000000 + ++g_tpNext;
            if (!Start(h)) return 0;
            g_tpOut[i] = h;
            return 1;
        }
    return 0;
}
static void TpCollect(bool wait) {
    u8 out[0x48];
    for (int i = 0; i < 4; i++)
        if (g_tpOut[i] && (wait || Ready(g_tpOut[i]))) {
            if (wait) WaitReady(g_tpOut[i]);
            assert(IcPlanHook(out, g_tpOut[i]) == out && *(DWORD*)(out + 0x40) == g_workerTid && *(DWORD*)(out + 4) == g_tpOut[i]);
            g_tpOut[i] = 0;
        }
}
static void __fastcall TpPass(void*, void*, float) {
    g_tpEntryHandle = g_async.state == A_RUNNING ? (LONG)g_async.handle : -1;  // after the thunk raised the window (Dekker)
    InterlockedExchange(&g_tpIn, 1);
    if (g_tpQueueInPass) TpQueue();
    static const DWORD inf = 0xFFFFFFFF;
    for (int i = 0; i < 3; i++) {  // three +0x918 takers per pass (steering, avoidance, footprint), a little work under it
        assert(LockAcquireHook(g_rgLock, NULL, &inf) >= 1);
        LARGE_INTEGER t0, t;
        QueryPerformanceCounter(&t0);
        do QueryPerformanceCounter(&t); while (t.QuadPart - t0.QuadPart < g_qpf.QuadPart / 5000);  // ~200 us
        LONG* depth = (LONG*)((u8*)g_rgLock + 0x20);  // the game lock's recursion count (the fake lock keeps none): last release
        *depth = 1;                                   // clears rgOwnerTid, else M1 refuses the sim's next START (why 5)
        LockReleaseHook(g_rgLock, NULL);
        *depth = 0;
    }
    InterlockedExchange(&g_tpIn, 0);
}
static LONG TpRun(LONG mode, int passes) {
    Ts3nCounters& k = ts3native_counters;
    k.g0wMode = mode;
    g_tpViolations = 0;
    const DWORD t0 = GetTickCount();
    for (int p = 0; p < passes; p++) {
        g_tpQueueInPass = p % 2 == 0;
        CallThunk((void*)0x55, 1.0f);  // the game's convention (TpPassChecked feeds CallThunk's esp/argument check)
        if (p % 3 == 0) TpQueue();     // between passes too
        TpCollect(false);
        Sleep(p % 4 == 0 ? 1 : 0);
        assert(GetTickCount() - t0 < 20000);  // a deadlock ends the test here, not in a hang
    }
    TpCollect(true);
    assert(GetTickCount() - t0 < 20000 && !AsyncBusy());
    return g_tpViolations;
}
static void __fastcall TpPassChecked(void* self, void* edx, float dt) {
    g_lpSelf = self;
    g_lpDt = dt;
    TpPass(self, edx, dt);
}
static void TestH2Threaded() {
    static_assert(offsetof(Ts3nCounters, h2Released) == 0x364C && offsetof(Ts3nCounters, h2PassLockN) == 0x3650 &&
                  offsetof(Ts3nCounters, h2PassLockUs) == 0x3658 && offsetof(Ts3nCounters, h2ReleaseMaxUs) == 0x3660 && sizeof(Ts3nCounters) == 6112 * 4, "v85 layout after v84 (docs/registry.md)");
    Ts3nCounters& k = ts3native_counters;
    const LONG wd0 = k.watchdogs;
    QueryPerformanceFrequency(&g_qpf);
    IcPlanFn icSaved = g_icPlanOrig;
    g_icPlanOrig = TpIc;
    g_g0wOrig = TpPassChecked;
    k.g0wReady = 1;
    memset(&k.h2Released, 0, sizeof(LONG) * 6);  // TestLocoWindow's held starts released at pass end
    g_icSleepMs = 2;  // the job holds +0x918 ~2 ms (ServeAsync's whole-job hold)
    // Mode 1: the window holds every start that is not already running at entry
    LONG started = g_tpStarts;
    assert(TpRun(1, 300) == 0);
    // g0wJobStartInPass is exact (v84 inPass order): 0 here, as the independent TpIc check
    TIMING_ASSERT("H2 TpRun > 50 starts in 300 ms", g_tpStarts - started > 50);
    assert(k.g0wJobStartInPass == 0 && k.g0wWatchdog == 0 && k.g0wDrop[0] == 0 && k.g0wDrop[1] == 0);
    assert(k.h2Released == k.g0wJobsHeld && k.h2ReleaseMaxUs > 0);  // no held job lost, each soon after
    TIMING_ASSERT("H2 held-job release < 100 ms", k.h2ReleaseMaxUs < 100000);
    assert(k.g0wJobsHeld > 0 && k.h2Released > 0 && k.h2Released <= k.g0wJobsHeld && k.g0wHoldUs > 0);
    TIMING_ASSERT("H2 window hold < 250 ms", k.g0wHoldMaxUs < 250000);
    assert(k.h2PassLockN[0] > 0 && k.h2PassLockUs[0] > 0);  // jobs running at entry: the pass waits out their hold
    const LONG held = k.g0wJobsHeld, rel = k.h2Released, lockN = k.h2PassLockN[0] + k.h2PassLockN[1];
    // Mode 0 (the off side of --ab h2): nothing held, starts land inside the pass, the same checker sees them
    started = g_tpStarts;
    assert(TpRun(0, 300) > 0);
    assert(g_tpStarts - started > 50 && k.g0wJobStartInPass > 0 && k.g0wJobsHeld == held && k.h2Released == rel);
    assert(k.h2PassLockN[0] + k.h2PassLockN[1] > lockN);  // counted in both modes
    // a wait with no job at all inside the pass: another thread holds +0x918, nothing queued -> counted as "none running"
    k.g0wMode = 1;
    static volatile LONG s_held;
    struct Holder {
        static DWORD WINAPI Run(void*) {
            EnterCriticalSection((CRITICAL_SECTION*)g_rgLock);
            InterlockedExchange(&s_held, 1);
            Sleep(40);
            LeaveCriticalSection((CRITICAL_SECTION*)g_rgLock);
            return 0;
        }
    };
    HANDLE th = CreateThread(NULL, 0, Holder::Run, NULL, 0, NULL);
    while (!s_held) Sleep(0);
    const LONG none = k.h2PassLockN[1], noneUs = k.h2PassLockUs[1];
    g_tpQueueInPass = 0;
    CallThunk((void*)0x55, 1.0f);
    WaitForSingleObject(th, INFINITE);
    CloseHandle(th);
    assert(k.h2PassLockN[1] == none + 1 && k.h2PassLockUs[1] - noneUs >= 20000);
    // nothing left
    assert(!AsyncBusy() && g_rgOffSimOpen == 0 && g_g0Win == 0 && g_g0wIn == 0 && t_g0wDepth == 0 && k.watchdogs == wd0);
    g_icSleepMs = 0;
    g_icPlanOrig = icSaved;
    g_g0wOrig = (LocoUpdateFn)(uintptr_t)LOCO_UPDATE;
    memset(&k.g0wReady, 0, sizeof(LONG) * 16);
    memset(&k.h2Released, 0, sizeof(LONG) * 6);
}

// --- A10 (a10.cpp, docs/87 §5-§6): Replan-family jobs through the real worker, M1 world and gate (TestRouteAsync's setup) ---
// fake originals: FakeIc's result plus the kind at +0x3C and the point at +0x10 (kinds 2-3)
static u8* __cdecl ArFakeReplan(u8* out, DWORD h) { FakeIc(out, h); out[0x3C] = 1; return out; }
static float g_arSeen[3];
static u8* ArFakePt(int kind, u8* out, DWORD h, float x, float y, float z) {
    FakeIc(out, h);
    out[0x3C] = (u8)kind;
    const float p[3] = {x, y, z};
    memcpy(out + 0x10, p, 12);
    memcpy(g_arSeen, p, 12);
    return out;
}
static u8* __cdecl ArFakePt2(u8* out, DWORD h, float x, float y, float z) { return ArFakePt(2, out, h, x, y, z); }
static u8* __cdecl ArFakePt3(u8* out, DWORD h, float x, float y, float z) { return ArFakePt(3, out, h, x, y, z); }
static u8 g_arActive;
static u8 __cdecl ArFakeIsActive(DWORD) { return g_arActive; }
static u8 ArStart(DWORD h, int kind, const float* pt) {
    for (int i = 0; pt && i < 3; i++) {
        DWORD b;
        memcpy(&b, &pt[i], 4);
        assert(GetOptionHook(h, b, kAsyncOptHi | kArHi | kArPtHi | (DWORD)i << 24, 1) == 1);
    }
    return GetOptionHook(h, 0, kAsyncOptHi | kArHi | (DWORD)kind << 24, 1);
}
static u8* ArCollect(int kind, u8* out, DWORD h, const float* pt) {
    switch (kind) {
        case 0: return IcPlanHook(out, h);
        case 1: return ArReplanHook(out, h);
        case 2: return ArPlanFromPointHook(out, h, pt[0], pt[1], pt[2]);
        default: return ArReplanFromPositionHook(out, h, pt[0], pt[1], pt[2]);
    }
}
// the result came from the worker's job of this kind (and point)
static bool ArFromJob(const u8* out, DWORD h, int kind, const float* pt) {
    return *(const DWORD*)(out + 4) == h && *(const DWORD*)(out + 0x40) == g_workerTid && out[0x3C] == kind &&
           (kind < 2 || memcmp(out + 0x10, pt, 12) == 0);
}
static bool ArSync(const u8* out, int kind) { return *(const DWORD*)(out + 0x40) == GetCurrentThreadId() && out[0x3C] == kind; }
static char g_arDLog[8];
static int g_arDLogN, g_arDAtJob;
static u8 __fastcall ArDRemove(void*, void*, void*) { g_arDLog[g_arDLogN++] = 'r'; return 1; }
static u8 g_arDObj[16];
static u8 __fastcall ArDFind(void*, void*, DWORD lo, DWORD, void** out) { *out = g_arDObj; return lo == 0xD; }
static void __fastcall ArDRelease(void**, void*) {}
static void* __cdecl ArDSvc() { return (void*)1; }
static u8* __cdecl ArFakePt3D(u8* out, DWORD h, float x, float y, float z) { g_arDAtJob = g_arDLogN; return ArFakePt3(out, h, x, y, z); }

#include "h2ic-test.h"  // TestH2Ic (docs/177 §4, H2 IC window)
#include "rcguard-test.h"  // TestRcGuard (docs/177 §7, refcount guard)
#include "jobyield-test.h"  // TestJobYield (docs/183, JOBBUSY step-aside + sim job waits by site)
#include "rwait-test.h"  // TestRouteWorkerWait (docs/203 §6, [RouteAsync] WorkerWait)
static void TestA10() {
    static_assert(offsetof(Ts3nCounters, arReady) == 0x14A4 && offsetof(Ts3nCounters, arMode) == 0x14A8 &&
                  offsetof(Ts3nCounters, arStarted) == 0x14AC && offsetof(Ts3nCounters, arCollected) == 0x14B8 &&
                  offsetof(Ts3nCounters, arRefused) == 0x14C4 && offsetof(Ts3nCounters, arDropped) == 0x14D0 &&
                  offsetof(Ts3nCounters, arShadow) == 0x14DC && offsetof(Ts3nCounters, arActive) == 0x14E8 &&
                  offsetof(Ts3nCounters, arJobUs) == 0x14F4 && offsetof(Ts3nCounters, arSyncUs) == 0x1500 &&
                  offsetof(Ts3nCounters, arSyncN) == 0x150C && offsetof(Ts3nCounters, arMismatch) == 0x1518 &&
                  offsetof(Ts3nCounters, arOrphan) == 0x151C && offsetof(Ts3nCounters, arWdPass) == 0x1520 && sizeof(Ts3nCounters) >= 1353 * 4, "v40 layout");
    Ts3nCounters& k = ts3native_counters;
    memset(&k.arReady, 0, sizeof(LONG) * 32);
    const LONG wd0 = k.watchdogs;
    const RouteIsActiveFn savedActive = g_routeIsActive;
    g_routeIsActive = ArFakeIsActive;
    g_arOrig[1] = (void*)ArFakeReplan;
    g_arOrig[2] = (void*)ArFakePt2;
    g_arOrig[3] = (void*)ArFakePt3;
    u8 out[0x48];
    const float p1[3] = {1.5f, 2.25f, -3.0f}, p2[3] = {7.0f, 0.5f, 9.75f};
    const DWORD kH3 = 0x25000003;
    g_icSleepMs = 0;

    // 1. a kind whose detour is not in place: refused, plain sync plan; the old encodings stay what they were
    k.arMode = 1;
    assert(!ArStart(kH1, 3, p1) && k.arRefused[2] == 1 && k.arStarted[2] == 0 && !JobFor(kH1));
    assert(!ArStart(kH1, 0, NULL));  // kind 0 is not an A10 START
    k.arRefused[2] = 0;
    k.arReady = kArBuilt;
    for (int kind = 1; kind <= 3; kind++) {
        const int i = kind - 1;
        if (!(kArBuilt >> i & 1)) continue;
        const float* pt = kind >= 2 ? p1 : NULL;
        k.arSyncN[i] = 0;  // the previous kind's mismatch case planned this IC once
        // 2. start -> not ready while running -> ready -> the IC takes the worker's result of its own kind
        g_icSleepMs = 30;
        assert(ArStart(kH1, kind, pt) && k.arStarted[i] == 1 && JobFor(kH1)->kind == kind);
        assert(!Ready(kH1));
        WaitReady(kH1);
        assert(ArCollect(kind, out, kH1, pt) == out && ArFromJob(out, kH1, kind, pt) && k.arCollected[i] == 1 && k.arJobUs[i] >= 25000);
        assert(!JobFor(kH1) && k.arSyncN[i] == 0);
        Cancel(kH1);
        g_icSleepMs = 0;
        // 3. refusals: nested interpreter state (NoYield), route being followed (Active), M1 (save busy); each planned sync
        FakeTask(kNested, 7);
        assert(!ArStart(kH1, kind, pt) && k.arRefused[i] == 1);
        FakeTask(kRoot, 7);
        g_arActive = 1;
        assert(!ArStart(kH1, kind, pt) && k.arActive[i] == 1 && k.arRefused[i] == 2);
        g_arActive = 0;
        k.rtBusy = 1;
        assert(!ArStart(kH1, kind, pt) && k.arRefused[i] == 3);
        k.rtBusy = 0;
        assert(ArCollect(kind, out, kH1, pt) == out && ArSync(out, kind) && k.arSyncN[i] == 1 && !JobFor(kH1));
        // kinds 2-3 need the point first (all three components since the last START)
        if (kind >= 2) {
            assert(!ArStart(kH1, kind, NULL) && k.arRefused[i] == 4 && !JobFor(kH1));
            k.arRefused[i] = 3;
        }
        // 4. Mode 0 (shadow): counted, refused, no job, nothing else touched (a done job of the route stays)
        assert(ArStart(kH2, kind, pt));
        WaitReady(kH2);
        k.arMode = 0;
        assert(!ArStart(kH2, kind, pt) && k.arShadow[i] == 1 && k.arStarted[i] == 2 && JobFor(kH2) && JobFor(kH2)->state == J_DONE);
        k.arMode = 1;
        // 5. the same route started again drops the older request (its caller is gone)
        assert(ArStart(kH2, kind, pt) && k.arDropped[i] == 1);
        WaitReady(kH2);
        assert(ArCollect(kind, out, kH2, pt) == out && ArFromJob(out, kH2, kind, pt) && k.arCollected[i] == 2);
        Cancel(kH2);
        // 6. kind mismatch: another IC of the same route meets the job: dropped, that IC plans here, logged once
        const LONG mm = k.arMismatch;
        assert(ArStart(kH2, kind, pt));
        WaitReady(kH2);
        const int other = kind < 3 && (kArBuilt >> kind & 1) ? kind + 1 : 0;
        assert(ArCollect(other, out, kH2, p2) == out && ArSync(out, other) && k.arMismatch == mm + 1 && k.arDropped[i] == 2 && !JobFor(kH2));
        Cancel(kH2);
        // ... and Route.Plan's job met by this IC
        assert(Start(kH2));
        WaitReady(kH2);
        assert(ArCollect(kind, out, kH2, pt) == out && ArSync(out, kind) && k.arMismatch == mm + 2 && !JobFor(kH2));
        Cancel(kH2);
        if (kind >= 2) {  // another start point is another plan
            assert(ArStart(kH2, kind, p1));
            WaitReady(kH2);
            assert(ArCollect(kind, out, kH2, p2) == out && ArSync(out, kind) && memcmp(g_arSeen, p2, 12) == 0 && k.arMismatch == mm + 3);
            Cancel(kH2);
        }
        // 7. a job queued behind another route's running one, its IC called before it started: taken back, planned here
        g_icSleepMs = 40;
        assert(Start(kH1) && ArStart(kH2, kind, pt));
        const LONG dr = k.arDropped[i];
        assert(ArCollect(kind, out, kH2, pt) == out && ArSync(out, kind) && k.arDropped[i] == dr + 1);
        WaitReady(kH1);
        assert(IcPlanHook(out, kH1) == out && *(DWORD*)(out + 0x40) == g_workerTid && out[0x3C] == 0);
        g_icSleepMs = 0;
        // 8. armed by READY, then async switched off before the IC: the job is drained, the IC plans here (tripwire)
        const LONG orphan = k.arOrphan;
        assert(ArStart(kH2, kind, pt));
        WaitReady(kH2);
        k.asyncMode = 0;
        assert(ArCollect(kind, out, kH2, pt) == out && ArSync(out, kind) && k.arOrphan == orphan + 1);
        k.asyncMode = 1;
        // 9. CANCEL while running (Sim reset): waited for, dropped; the next START is not refused
        g_icSleepMs = 30;
        assert(ArStart(kH1, kind, pt));
        Cancel(kH1);
        assert(g_async.state == A_IDLE && !JobFor(kH1) && ArStart(kH1, kind, pt));
        WaitReady(kH1);
        assert(ArCollect(kind, out, kH1, pt) == out && ArFromJob(out, kH1, kind, pt));
        g_icSleepMs = 0;
        // 10. a destroy during the job (RT_DESTROY, closure #4) waits for it; the result is still collected after
        k.rtReady = RT_ALL;
        g_icSleepMs = 40;
        assert(ArStart(kH3, kind, pt));
        while (g_async.state != A_RUNNING) Sleep(0);
        DWORD stack[24] = {};
        stack[20] = 0x999;
        stack[21] = 0x77;
        static DWORD simTid;
        simTid = GetCurrentThreadId();  // the destroy comes from the sim thread
        const DWORD* const savedSim = g_rtSimTidSlot;
        g_rtSimTidSlot = &simTid;
        RtEnter(7, &stack[20]);
        g_rtSimTidSlot = savedSim;
        assert(JobFor(kH3)->state == J_DONE && g_rgOffSimOpen == 1);
        assert(RgLeave() == 0x999 && g_rgOffSimOpen == 0);
        assert(Ready(kH3) && ArCollect(kind, out, kH3, pt) == out && ArFromJob(out, kH3, kind, pt));
        k.rtReady = 0;
        g_icSleepMs = 0;
        assert(!AsyncBusy() && g_rgOffSimOpen == 0);
    }

    // 11. design D: a discourage Remove deferred during a Replan job is applied at the next A10 START, before that job runs
    {
        u8* world = (u8*)g_rgLock - 0x918;
        const u8 w974 = world[0x974];
        world[0x974] = 1;  // DiscWorldOk
        LONG* const depth = (LONG*)((u8*)g_rgLock + 0x20);  // the game lock's count: the drain's release ends the ownership
        *depth = 1;
        *(DWORD*)(g_arDObj + 8) = 0xD;
        g_discRemoveOrig = ArDRemove;
        g_findRef = ArDFind;
        g_objSvc = ArDSvc;
        g_objRelease = ArDRelease;
        k.discMode = 1;
        g_icSleepMs = 30;
        assert(ArStart(kH1, 3, p1));
        while (g_async.state != A_RUNNING) Sleep(0);
        assert(DiscRemoveHook(world, NULL, g_arDObj) == 1 && g_discCount == 1 && g_arDLogN == 0);  // deferred
        while (JobFor(kH1)->state != J_DONE) Sleep(1);  // no READY in between (READY is a drain point too)
        g_arOrig[3] = (void*)ArFakePt3D;
        g_arDAtJob = -1;
        assert(ArStart(kH2, 3, p1) && g_discCount == 0 && g_arDLogN == 1);  // drained by the START, then queued
        WaitReady(kH1);
        assert(ArCollect(3, out, kH1, p1) == out && ArFromJob(out, kH1, 3, p1));
        WaitReady(kH2);
        assert(ArCollect(3, out, kH2, p1) == out && ArFromJob(out, kH2, 3, p1) && g_arDAtJob == 1);  // the job ran after the apply
        g_arOrig[3] = (void*)ArFakePt3;
        g_icSleepMs = 0;
        k.discMode = 0;
        world[0x974] = w974;
        *depth = 0;
        memset(&k.discReady, 0, offsetof(Ts3nCounters, discOverflow) + 4 - offsetof(Ts3nCounters, discReady));
    }

    // 12. random order over 4 routes and all kinds (incl. Route.Plan): START / READY / IC / CANCEL; every collected result
    // is its own route's and kind's, nothing is left at the end
    {
        static const DWORD kHs[4] = {0x25000011, 0x25000012, 0x25000013, 0x25000014};
        int want[4] = {-1, -1, -1, -1};  // kind of the START each route last got answered yes, -1 = none
        float pts[4][3];
        DWORD seed = 0xA10;
        auto rnd = [&seed](DWORD n) { seed = seed * 1103515245 + 12345; return (seed >> 16) % n; };
        long collected = 0, sync = 0;
        for (int step = 0; step < 600; step++) {
            const int r = (int)rnd(4), op = (int)rnd(4);
            const DWORD h = kHs[r];
            g_icSleepMs = rnd(3) ? 0 : rnd(4) + 1;
            if (op == 0) {
                int kind = (int)rnd(4);
                if (kind && !(kArBuilt >> (kind - 1) & 1)) kind = 0;  // an IC this build does not hook: Route.Plan
                for (int c = 0; c < 3; c++) pts[r][c] = (float)rnd(100) * 0.25f;
                const u8 ok = kind ? ArStart(h, kind, pts[r]) : Start(h);
                want[r] = ok ? kind : -1;
            } else if (op == 1 && want[r] >= 0) {  // the C# side: READY loop, then the original method's IC
                WaitReady(h);
                assert(ArCollect(want[r], out, h, pts[r]) == out && ArFromJob(out, h, want[r], pts[r]));
                Cancel(h);
                want[r] = -1;
                collected++;
            } else if (op == 2) {  // an IC call nobody armed (another caller of the route): plans here
                int kind = (int)rnd(4);
                if (kind && !(kArBuilt >> (kind - 1) & 1)) kind = 0;  // an IC this build does not hook: Route.Plan
                assert(ArCollect(kind, out, h, pts[r]) == out && ArSync(out, kind) && !JobFor(h));
                want[r] = -1;
                sync++;
            } else {
                Cancel(h);
                want[r] = -1;
            }
        }
        for (int r = 0; r < 4; r++) Cancel(kHs[r]);
        g_icSleepMs = 0;
        assert(collected > 20 && sync > 20 && !AsyncBusy() && g_rgOffSimOpen == 0 && g_async.state == A_IDLE);
        for (int i = 0; i < kJobs; i++) assert(g_jobs[i].state == J_FREE);
    }

    // 13. docs/176 ForeignKinds: a kind START with bit 61 (the caller's task is not the route's Sim)
    {
        static_assert(offsetof(Ts3nCounters, afMode) == 0x4308 && offsetof(Ts3nCounters, afSeen) == 0x4310 &&
                      offsetof(Ts3nCounters, afCollected) == 0x4340 && offsetof(Ts3nCounters, afActiveTrip) == 0x435C, "v101 layout");
        memset(&k.afMode, 0, offsetof(Ts3nCounters, afActiveTrip) + 4 - offsetof(Ts3nCounters, afMode));
        auto fstart = [](DWORD h, int kind, const float* pt) -> u8 {
            for (int i = 0; pt && i < 3; i++) {
                DWORD b;
                memcpy(&b, &pt[i], 4);
                assert(GetOptionHook(h, b, kAsyncOptHi | kArHi | kArPtHi | (DWORD)i << 24, 1) == 1);
            }
            return GetOptionHook(h, 0, kAsyncOptHi | kArHi | kArFgnHi | (DWORD)kind << 24, 1);
        };
        const int ownKinds = g_arOwnKinds;
        for (int kind = 1; kind <= 3; kind++) {
            const int i = kind - 1;
            const float* pt = kind >= 2 ? p1 : NULL;
            // ForeignKinds off (afKinds 0) or afMode 0: refused as Foreign, counted, no job; the IC plans here
            assert(!fstart(kH1, kind, pt) && k.afSeen[i] == 1 && !JobFor(kH1) && g_rfbWhy == RFB_FOREIGN);
            assert(ArCollect(kind, out, kH1, pt) == out && ArSync(out, kind));
            k.afKinds = 7;
            assert(!fstart(kH1, kind, pt) && k.afSeen[i] == 2 && !JobFor(kH1));
            k.afMode = 1;
            // own STARTs of a kind hooked only for ForeignKinds stay synchronous; the foreign one starts, flagged af (no fb cap)
            g_arOwnKinds = 0;
            assert(!ArStart(kH1, kind, pt) && !JobFor(kH1));
            g_icSleepMs = 20;
            assert(fstart(kH1, kind, pt) && k.afStarted[i] == 1 && JobFor(kH1)->af == 1 && JobFor(kH1)->fb == 0);
            WaitReady(kH1);
            assert(ArCollect(kind, out, kH1, pt) == out && ArFromJob(out, kH1, kind, pt) && k.afCollected[i] == 1 && k.afJobUs[i] >= 15000);
            assert(k.afJobMaxUs >= 15000 && k.afActiveTrip == 0);
            Cancel(kH1);
            g_icSleepMs = 0;
            // route being followed, nested call: refused and split out
            g_arActive = 1;
            assert(ArWould(kH1, 6));  // docs/247 §9: the question answers without touching the counters
            assert(k.afActive[i] == 0);
            assert(!fstart(kH1, kind, pt) && k.afActive[i] == 1);
            g_arActive = 0;
            assert(!ArWould(kH1, 6) && ArWould(kH1, 5) && ArWould(kH1, 3) && !ArWould(kH1, 0));
            FakeTask(kNested, 7);
            assert(!ArWould(kH1, 5));
            assert(!fstart(kH1, kind, pt) && k.afNoYield[i] == 1);
            FakeTask(kRoot, 7);
            assert(ArCollect(kind, out, kH1, pt) == out && ArSync(out, kind) && !JobFor(kH1));
            g_arOwnKinds = ownKinds;
            k.afMode = 0;
            k.afKinds = 0;
        }
        assert(!AsyncBusy() && g_async.state == A_IDLE);
        memset(&k.afMode, 0, offsetof(Ts3nCounters, afActiveTrip) + 4 - offsetof(Ts3nCounters, afMode));
    }

    // 14. docs/250 §7.1 [RouteAsync] WatchdogKind2 (release shape Kinds=0 ForeignKinds=4): an own PlanFromPoint START starts
    // only with the switch on and the watchdog tag (ScProbeMark bit 10) on its route; every other kind-2 call runs the game's IC
    // directly (arWdPass), with no SyncIc (arSyncN unchanged)
    {
        const int ownKinds = g_arOwnKinds;
        g_arOwnKinds = 0;
        k.afKinds = 4;
        const LONG sync0 = k.arSyncN[1], st0 = k.arStarted[1], wdN0 = k.wdJobN;
        auto wdTag = [](DWORD h) { return ScProbeMark(h, 0x10400); };
        // switch off: tagged or not, refused (as 0.36.238); q 9 says no; the IC call is the old A10 sync path
        g_arWdKind2 = 0;
        assert(!ArWould(kH1, 9));
        assert(wdTag(kH1) == 0 && !ArStart(kH1, 2, p1) && !JobFor(kH1) && k.arStarted[1] == st0);
        assert(ArCollect(2, out, kH1, p1) == out && ArSync(out, 2) && k.arSyncN[1] == sync0 + 1 && k.arWdPass == 0);
        // switch on: an untagged own START is refused (the refused tagged one above used its tag up) and its IC goes straight
        // to the game (no SyncIc)
        g_arWdKind2 = 1;
        assert(ArWould(kH1, 9));
        assert(!ArStart(kH1, 2, p1) && !JobFor(kH1) && k.arStarted[1] == st0);
        assert(ArCollect(2, out, kH1, p1) == out && ArSync(out, 2) && k.arSyncN[1] == sync0 + 1 && k.arWdPass == 1);
        // a stuck-check tag (no bit 10) or the watchdog tag on another route does not open the gate
        ScProbeMark(kH1, 0x10001);
        assert(!ArStart(kH1, 2, p1) && !JobFor(kH1));
        wdTag(kH2);
        assert(!ArStart(kH1, 2, p1) && !JobFor(kH1) && k.arStarted[1] == st0);
        // a foreign kind-2 START with the tag stays refused (ForeignKinds has no kind 2)
        wdTag(kH1);
        for (int c = 0; c < 3; c++) {
            DWORD b;
            memcpy(&b, &p1[c], 4);
            GetOptionHook(kH1, b, kAsyncOptHi | kArHi | kArPtHi | (DWORD)c << 24, 1);
        }
        assert(!GetOptionHook(kH1, 0, kAsyncOptHi | kArHi | kArFgnHi | 2u << 24, 1) && !JobFor(kH1));
        assert(!ArStart(kH1, 2, p1) && !JobFor(kH1) && k.arStarted[1] == st0);  // the refused foreign START used the tag up
        // the watchdog-tagged own START: starts, labelled (sc bit 10, counted in wdJob*); another PlanFromPoint of this route
        // (another point, nothing armed) is not passed through while the job is there: it meets the job (dropped, planned sync)
        g_icSleepMs = 10;
        assert(wdTag(kH1) == 0 && ArStart(kH1, 2, p1) && k.arStarted[1] == st0 + 1 && JobFor(kH1) && (JobFor(kH1)->sc & 0x400));
        assert(ArCollect(2, out, kH1, p2) == out && ArSync(out, 2) && !JobFor(kH1) && k.arWdPass == 1 && k.arSyncN[1] == sync0 + 2);
        Cancel(kH1);
        // ... and READY then the IC take the worker's result
        assert(wdTag(kH1) == 0 && ArStart(kH1, 2, p1) && k.arStarted[1] == st0 + 2);
        WaitReady(kH1);
        assert(ArCollect(2, out, kH1, p1) == out && ArFromJob(out, kH1, 2, p1) && k.arWdPass == 1 && k.wdJobN > wdN0);
        Cancel(kH1);
        g_icSleepMs = 0;
        // the tag was used up by that START: the next own START of the route is refused again
        assert(!ArStart(kH1, 2, p1) && !JobFor(kH1));
        assert(ArCollect(2, out, kH1, p1) == out && ArSync(out, 2) && k.arWdPass == 2 && k.arSyncN[1] == sync0 + 2);
        // kind 2 in Kinds: no pass-through (the old A10 path), q 9 yes without the switch
        g_arWdKind2 = 0;
        g_arOwnKinds = 2;
        assert(ArWould(kH1, 9));
        assert(ArCollect(2, out, kH1, p1) == out && ArSync(out, 2) && k.arWdPass == 2 && k.arSyncN[1] == sync0 + 3);
        g_arOwnKinds = ownKinds;
        k.afKinds = 0;
        k.arWdPass = 0;
        assert(!AsyncBusy() && g_async.state == A_IDLE);
    }
    assert(k.watchdogs == wd0);
    g_routeIsActive = savedActive;
    memset(g_arOrig, 0, sizeof(g_arOrig));
    memset(&k.arReady, 0, sizeof(LONG) * 32);
}

// --- docs/134: sync fallback freezes ([RouteAsync] FallbackAsync, SyncCapMs, LogFallbackStack); runs inside TestRouteAsync ---
static volatile DWORD g_fbActiveH;  // the route the fake Route_IsActive calls "being followed"
static u8 __cdecl FbFakeActive(DWORD h) { return h == g_fbActiveH; }
static volatile LONG g_fbApplyOk, g_fbSeenCap;
static void __fastcall FbFakeApply(void* world, void*) {
    const CRITICAL_SECTION* lock = (const CRITICAL_SECTION*)((u8*)world + 0x918);
    g_fbApplyOk = lock->OwningThread == (HANDLE)(uintptr_t)GetCurrentThreadId();  // applied under +0x918, on this thread
    (*(u8**)((u8*)world + 0x95C))[0x10E] = 0;
}
static LONG __fastcall FbBusyAcquire(void*, void*, const DWORD* deadline) { assert(*deadline == 0); return -2; }
static u8* __cdecl FbCapIc(u8* out, DWORD handle) {  // stands in for a sim plan whose world A* hit the cap
    g_fbSeenCap = t_fbCapUs;
    if (t_fbCapUs) t_fbCapHit = 1;
    return FakeIc(out, handle);
}
static u8 Foreign(DWORD h) { return GetOptionHook(h, 0, kAsyncOptHi | kArHi, 1); }  // ts3fixes 0.36.7 foreign START
// docs/134 §11: a foreign job shaped like the BP 2026-10-22 one (32 world A* nodes in 1,942 ms): work before the first node,
// slow expansions that generate few nodes, work after the cap; the world A* cost calls go through the patched stubs (g_cost)
static volatile LONG g_fbPreMs, g_fbStepMs, g_fbPostMs, g_fbNodes;
// every cost call stamped before and after (QPC), and the call that returned the wall: lets 4c bound how late the cap fired
// by the measured gap between two deadline reads instead of by Sleep's wall time (timer resolution and machine load vary)
static LONGLONG g_fbTs[2001][2], g_fbPreT, g_fbPostUs;
static int g_fbCalls, g_fbWallAt;
static float FbCost(DWORD isEdge) {
    LARGE_INTEGER a, b;
    QueryPerformanceCounter(&a);
    const float r = g_cost(0, 0, 0, 0, isEdge, 0, 0, 0);
    QueryPerformanceCounter(&b);
    if (g_fbCalls < 2001) g_fbTs[g_fbCalls][0] = a.QuadPart, g_fbTs[g_fbCalls][1] = b.QuadPart;
    if (r >= 1e8f && g_fbWallAt < 0) g_fbWallAt = g_fbCalls;
    g_fbCalls++;
    return r;
}
static u8 __fastcall FbSlowSearch(void*, void*, int*, int*, int*, DWORD, DWORD, u8*, int*, int*, DWORD, DWORD) {
    g_fbCalls = 0, g_fbWallAt = -1;
    LARGE_INTEGER t;
    QueryPerformanceCounter(&t);
    g_fbPreT = t.QuadPart;
    Sleep(g_fbPreMs);
    FbCost(0);
    for (g_fbNodes = 0; g_fbNodes < 1000; g_fbNodes++) {
        if (FbCost(1) >= 1e8f) break;
        FbCost(0);
        Sleep(g_fbStepMs);
    }
    QueryPerformanceCounter(&t);
    Sleep(g_fbPostMs);
    LARGE_INTEGER e;
    QueryPerformanceCounter(&e);
    g_fbPostUs = (e.QuadPart - t.QuadPart) * 1000000 / g_qpf.QuadPart;
    return 0;
}
static u8* __cdecl FbSlowIc(u8* out, DWORD handle) {
    u8 opts[0x38] = {0};
    int list[2];
    CallPlan(NULL, NULL, list, NULL, NULL, 0, 1, opts, NULL, NULL, 0, 0);
    return FakeIc(out, handle);
}
// docs/178 T1: 1 = this job ran with the worker slot of the deadline stubs open for this thread and a deadline set
static volatile LONG g_fbjdSeen;
static u8* __cdecl FbjdSeeIc(u8* out, DWORD handle) {
    g_fbjdSeen = g_pdWkTid == GetCurrentThreadId() && t_fbDeadline ? 1 : 0;
    return FakeIc(out, handle);
}
// docs/163 §10: the probe job's cap and deadline slot as the worker's IC call sees them (a cut is simulated: capped)
static u8* __cdecl ScSeeIc(u8* out, DWORD handle) {
    g_fbSeenCap = t_fbCapUs;
    if (t_fbCapUs) t_fbCapHit = 1;
    return FbjdSeeIc(out, handle);
}
static u8 Probe(DWORD h) { return GetOptionHook(h, 0, kProbeHi, 1); }  // ts3fixes stuckcheckcap PROBE mark
static u8 g_fbCtx[12], g_fbInfo[3][12], g_fbMeth[2][0x20], g_fbCls[2][0x40];
static DWORD g_fbFrame[3][2];
static const u8* __cdecl FbFakeCtx() { return g_fbCtx; }

static void TestFallback() {
    static_assert(offsetof(Ts3nCounters, fbMode) == 0x314C && offsetof(Ts3nCounters, fbCapUs) == 0x3164 &&
                  offsetof(Ts3nCounters, fbActiveTrip) == 0x3170, "counters v73 layout (docs/registry.md)");
    static_assert(offsetof(Ts3nCounters, fbRetryMode) == 0x31C8 && offsetof(Ts3nCounters, fbRetryTicksMax) == 0x31D8,
                  "counters v75 layout after v74 (docs/registry.md)");
    static_assert(offsetof(Ts3nCounters, fbjcMode) == 0x354C && offsetof(Ts3nCounters, ownJobLong) == 0x3564,
                  "counters v81 layout after v80 (docs/registry.md)");
    Ts3nCounters& k = ts3native_counters;
    const DWORD me = GetCurrentThreadId();
    u8 out[0x48];
    const RouteIsActiveFn savedActive = g_routeIsActive;
    g_routeIsActive = FbFakeActive;
    g_fbActiveH = kH2;
    g_rfbLines = g_rfbDropped = 0;
    k.rfbLogMs = 1;

    // 1. foreign START, fbMode 0: refused as "Foreign", the plan stays sync and is counted under that reason
    g_icSleepMs = 60;
    assert(!Foreign(kH1) && k.fbForeign == 1 && g_rfbHandle == kH1 && g_rfbWhy == RFB_FOREIGN && !JobFor(kH1));
    assert(IcPlanHook(out, kH1) == out && *(DWORD*)(out + 0x40) == me && (k.rfbMax & 0xFF) == RFB_FOREIGN && k.rfbN[2] == 1);
    // fbMode 1: on the worker while the caller's task sleeps; READY, then the plan takes the worker's result
    k.fbMode = 1;
    g_icSleepMs = 20;
    assert(Foreign(kH1) && k.fbForeignStarted == 1 && JobFor(kH1) && JobFor(kH1)->fb == 1);
    WaitReady(kH1);
    assert(IcPlanHook(out, kH1) == out && *(DWORD*)(out + 0x40) == g_workerTid && k.fbActiveTrip == 0);
    Cancel(kH1);
    // a route being followed stays on the sim thread (Active), as for A10
    assert(!Foreign(kH2) && g_rfbWhy == RFB_ACTIVE && !JobFor(kH2) && k.fbForeignStarted == 1);
    assert(IcPlanHook(out, kH2) == out && *(DWORD*)(out + 0x40) == me);
    // inside a native->managed call: NoYield, as for the Sim's own task
    FakeTask(kNested, 7);
    assert(!Foreign(kH1) && g_rfbWhy == RFB_NOYIELD);
    assert(IcPlanHook(out, kH1) == out && *(DWORD*)(out + 0x40) == me);
    FakeTask(kRoot, 7);
    // tripwire: the route started being followed during the job (logged once, counted)
    assert(Foreign(kH1));
    WaitReady(kH1);
    g_fbActiveH = kH1;
    assert(IcPlanHook(out, kH1) == out && k.fbActiveTrip == 1);
    Cancel(kH1);
    g_fbActiveH = kH2;

    // 2. M1Pending: fbMode 0 refuses; fbMode 1 applies the pending lot updates under one +0x918 try, then starts
    u8* world = (u8*)g_rgLock - 0x918;
    u8* p = *(u8**)(world + 0x95C);
    p[0x10E] = 1;
    k.fbMode = 0;
    assert(!Start(kH1) && g_rfbWhy == RFB_M1 + 3 && k.fbPendApplied == 0 && p[0x10E] == 1);
    assert(IcPlanHook(out, kH1) == out && *(DWORD*)(out + 0x40) == me);
    k.fbMode = 1;
    g_fbAcq = FakeLockAcquire;
    g_fbRel = FakeLockRelease;
    g_fbApply = FbFakeApply;
    assert(Start(kH1) && k.fbPendApplied == 1 && g_fbApplyOk && p[0x10E] == 0);
    WaitReady(kH1);  // the worker took +0x918 for the job, so the START released it
    assert(IcPlanHook(out, kH1) == out && *(DWORD*)(out + 0x40) == g_workerTid);
    Cancel(kH1);
    p[0x10E] = 1;  // +0x918 busy: one try, nothing applied, refused as before
    g_fbAcq = FbBusyAcquire;
    assert(!Start(kH1) && k.fbPendBusy == 1 && k.fbPendApplied == 1 && p[0x10E] == 1 && g_rfbWhy == RFB_M1 + 3);
    assert(IcPlanHook(out, kH1) == out && *(DWORD*)(out + 0x40) == me);
    p[0x10E] = 0;

    // 2b. M1Retry (docs/134 §2.6): the busy try parks the START (answered yes, no job); each READY tries once more
    k.fbRetryMode = 1;
    p[0x10E] = 1;
    const LONG started0 = k.asyncStarted;
    assert(Start(kH1) && k.fbRetryParked == 1 && k.fbPendBusy == 2 && JobFor(kH1) && JobFor(kH1)->state == J_PEND);
    assert(!AsyncBusy() && k.asyncStarted == started0);  // a parked START is no job: no lock pump, worker idle
    assert(!Ready(kH1) && JobFor(kH1)->state == J_PEND && JobFor(kH1)->pend == 1 && k.fbPendBusy == 3 && p[0x10E] == 1);
    // D's deferred footprint records still queued (here: a drain in progress): the tick waits even with the lock free
    g_fbAcq = FakeLockAcquire;
    g_discCount = 1;
    g_discDraining = true;
    assert(!Ready(kH1) && JobFor(kH1)->state == J_PEND && k.fbPendApplied == 1 && p[0x10E] == 1);
    g_discCount = 0;
    g_discDraining = false;
    // lock free, D empty: applied once under +0x918 on this thread, then queued; READY later, the worker's result collected
    g_fbApplyOk = 0;
    assert(!Ready(kH1) && k.fbPendApplied == 2 && g_fbApplyOk && p[0x10E] == 0 && k.fbRetryQueued == 1);
    assert(k.asyncStarted == started0 + 1 && k.fbRetryTicksMax == 3);
    WaitReady(kH1);
    assert(IcPlanHook(out, kH1) == out && *(DWORD*)(out + 0x40) == g_workerTid);
    Cancel(kH1);
    assert(k.fbPendApplied == 2);  // never applied twice
    // RemovalFirst (docs/89 §18), M1Retry off as shipped: a queued removal parks a route not known unseen, the tick waits while it
    // is queued, then the job queues; other records and the switch keep today's START; the tick cap goes on
    {
        p[0x10E] = 0;
        k.fbRetryMode = 0;
        g_rfMode = 1;
        g_fbAcq = FakeLockAcquire;
        const LONG q0 = k.asyncStarted, parked0 = k.fbRetryParked, gaveUp0 = k.fbRetryGaveUp;
        g_discDraining = true;  // keeps DiscPoll from draining the fake record, as a running job does
        g_discHead = 0;
        DiscRec& rec = g_disc[0];
        rec = DiscRec{};
        rec.fp2 = 2;
        g_discCount = 1;
        assert(Start(kH1) && JobFor(kH1)->state == J_PEND && JobFor(kH1)->rf == 1 && k.asyncStarted == q0 && g_rfParked == 1);
        assert(!Ready(kH1) && JobFor(kH1)->state == J_PEND && JobFor(kH1)->pend == 1);
        g_discCount = 0;  // applied by DiscPoll
        g_discDraining = false;
        assert(!Ready(kH1) && k.asyncStarted == q0 + 1 && k.fbRetryGaveUp == gaveUp0);
        WaitReady(kH1);
        assert(IcPlanHook(out, kH1) == out && *(DWORD*)(out + 0x40) == g_workerTid);
        Cancel(kH1);
        // J_PEND audit: household and unknown scope park, known unseen queues; CANCEL frees; a world closing and a Plan without READY plan here
        g_discDraining = true;
        rec = DiscRec{};
        rec.fp2 = 2;
        g_discCount = 1;
        KdScopeSet(kH1, 1);
        assert(Start(kH1) && JobFor(kH1)->state == J_PEND && JobFor(kH1)->rf == 1);
        Cancel(kH1);
        assert(!JobFor(kH1));
        KdScopeSet(kH1, 2);
        assert(Start(kH1) && JobFor(kH1)->rf == 0 && JobFor(kH1)->state != J_PEND);
        WaitReady(kH1);
        assert(IcPlanHook(out, kH1) == out && *(DWORD*)(out + 0x40) == g_workerTid);
        Cancel(kH1);
        KdScopeClear(kH1);  // ts3fixes op 12: the IC kept the scope for a sync plan
        assert(Start(kH1) && JobFor(kH1)->rf == 1);
        g_discCount = 0;
        k.rtClosing = 1;
        assert(Ready(kH1) && !JobFor(kH1) && g_rfbHandle == kH1 && g_rfbWhy == RFB_M1 + 2);
        k.rtClosing = 0;
        assert(IcPlanHook(out, kH1) == out && *(DWORD*)(out + 0x40) == me);
        g_discCount = 1;
        assert(Start(kH1) && JobFor(kH1)->rf == 1);
        assert(IcPlanHook(out, kH1) == out && *(DWORD*)(out + 0x40) == me && !JobFor(kH1));
        g_discDraining = false;
        g_discCount = 0;
        g_discDraining = true;
        g_discCount = 1;
        rec = DiscRec{};
        rec.usf = true;  // a moved footprint does not park
        assert(!DiscRfStart());
        rec = DiscRec{};
        rec.fp2 = 1;
        assert(!DiscRfStart());
        rec = DiscRec{};
        rec.add = true;
        assert(!DiscRfStart());
        rec = DiscRec{};  // a plain Remove
        assert(DiscRemovalQueued());
        rec.pf = true;
        assert(DiscRemovalQueued());
        g_rfMode = 0;
        assert(!DiscRfStart());
        g_rfMode = 1;
        assert(DiscRfWait(kFbRetryTicks - 1, kFbRetryTicks) && !DiscRfWait(kFbRetryTicks, kFbRetryTicks) && g_rfCapped == 1);
        g_discCount = 0;
        g_discDraining = false;
        g_rfMode = 0;
        g_rfParked = g_rfCapped = g_rfTicksMax = 0;
        k.fbRetryParked = parked0;
        k.fbRetryGaveUp = gaveUp0;
        k.fbRetryMode = 1;
        p[0x10E] = 1;
        g_fbAcq = FbBusyAcquire;
    }
    // the tick cap: READY gives up, the plan runs here with reason M1Pending, nothing applied
    p[0x10E] = 1;
    g_fbAcq = FbBusyAcquire;
    assert(Start(kH1) && JobFor(kH1)->state == J_PEND);
    JobFor(kH1)->pend = kFbRetryTicks - 1;
    assert(Ready(kH1) && !JobFor(kH1) && k.fbRetryGaveUp == 1 && g_rfbHandle == kH1 && g_rfbWhy == RFB_M1 + 3);
    assert(IcPlanHook(out, kH1) == out && *(DWORD*)(out + 0x40) == me && k.fbPendApplied == 2);
    // the C# poll cap (Plan without READY saying done): the parked slot is freed, planned here
    assert(Start(kH1) && JobFor(kH1)->state == J_PEND);
    assert(IcPlanHook(out, kH1) == out && *(DWORD*)(out + 0x40) == me && !JobFor(kH1) && k.fbRetryGaveUp == 2);
    // CANCEL while parked frees the slot; M1Retry off: refused at START as before
    assert(Start(kH1) && JobFor(kH1)->state == J_PEND);
    Cancel(kH1);
    assert(!JobFor(kH1) && k.fbRetryParked == 4);
    k.fbRetryMode = 0;
    assert(!Start(kH1) && g_rfbWhy == RFB_M1 + 3 && !JobFor(kH1) && k.fbRetryParked == 4);
    assert(IcPlanHook(out, kH1) == out && *(DWORD*)(out + 0x40) == me);
    p[0x10E] = 0;

    // 3. QueueFull: 8 fresh uncollected results fill fbMode 0's slots; fbMode 1 queues past them
    k.fbMode = 0;
    g_icSleepMs = 0;
    for (DWORD i = 0; i < 8; i++) assert(Start(0x25000200 + i));
    for (DWORD i = 0; i < 8; i++) WaitReady(0x25000200 + i);
    assert(!Start(kH1) && g_rfbWhy == RFB_QFULL);
    assert(IcPlanHook(out, kH1) == out && *(DWORD*)(out + 0x40) == me);
    k.fbMode = 1;
    assert(Start(kH1) && k.fbDeepQueued == 1 && JobFor(kH1) - g_jobs >= kJobsBase);
    WaitReady(kH1);
    assert(IcPlanHook(out, kH1) == out && *(DWORD*)(out + 0x40) == g_workerTid);
    Cancel(kH1);
    for (DWORD i = 0; i < 8; i++) Cancel(0x25000200 + i);
    assert(!AsyncBusy());

    // 4. SyncCapMs: a sim-thread sync plan runs with this thread's cap set (cleared after), a capped one is counted
    k.fbCapUs = 150000;
    g_icPlanOrig = FbCapIc;
    g_fbSeenCap = -1;
    assert(IcPlanHook(out, kH2) == out && g_fbSeenCap == 150000 && k.fbCapped == 1 && t_fbCapUs == 0 && t_fbCapHit == 0);
    g_fbSeenCap = -1;
    assert(Start(kH1));  // an async plan: the worker's call has no cap
    WaitReady(kH1);
    assert(IcPlanHook(out, kH1) == out && g_fbSeenCap == 0 && k.fbCapped == 1);
    Cancel(kH1);
    // 4b. FbJobCapMs (docs/134 §10): a foreign job runs under the job cap on the worker, an own job does not; switch off = none
    k.fbjcCapUs = 200000;
    k.fbjcMode = 1;
    k.fbjcMaxUs = k.ownJobMaxUs = k.fbjcLong = k.ownJobLong = 0;  // earlier cases ran long own jobs
    g_fbSeenCap = -1;
    assert(Start(kH1));
    WaitReady(kH1);
    assert(IcPlanHook(out, kH1) == out && g_fbSeenCap == 0 && k.fbjcCapped == 0);
    Cancel(kH1);
    g_icSleepMs = 210;  // also a long job: counted and logged
    assert(Foreign(kH1) && JobFor(kH1)->fb == 1);
    WaitReady(kH1);
    assert(IcPlanHook(out, kH1) == out && g_fbSeenCap == 200000 && k.fbjcCapped == 1 && k.fbCapped == 1);
    assert(k.fbjcLong == 1 && k.ownJobLong == 0 && k.fbjcMaxUs >= 200000);
    TIMING_ASSERT("foreign-cap own job < 200 ms", k.ownJobMaxUs < 200000);
    Cancel(kH1);
    g_icSleepMs = 0;
    k.fbjcMode = 0;
    g_fbSeenCap = -1;
    assert(Foreign(kH1));
    WaitReady(kH1);
    assert(IcPlanHook(out, kH1) == out && g_fbSeenCap == 0 && k.fbjcCapped == 1);
    Cancel(kH1);
    // docs/178 T1: the worker slot of the cell / water deadline stubs is open exactly while a foreign job with a deadline runs
    // (stubs in place); an own job and a job without the stubs leave it shut; closed again after the job
    {
        const LONG rdy0 = k.fbjdReady;
        k.fbjdReady = 3, k.fbjcMode = 1;
        g_icPlanOrig = FbjdSeeIc;
        g_fbjdSeen = -1;
        assert(Foreign(kH1));
        WaitReady(kH1);
        assert(IcPlanHook(out, kH1) == out && g_fbjdSeen == 1 && g_pdWkTid == 0 && k.fbjcCapped == 1);
        Cancel(kH1);
        g_fbjdSeen = -1;
        assert(Start(kH1));
        WaitReady(kH1);
        assert(IcPlanHook(out, kH1) == out && g_fbjdSeen == 0 && g_pdWkTid == 0);
        Cancel(kH1);
        k.fbjdReady = 0;
        g_fbjdSeen = -1;
        assert(Foreign(kH1));
        WaitReady(kH1);
        assert(IcPlanHook(out, kH1) == out && g_fbjdSeen == 0);
        Cancel(kH1);
        k.fbjdReady = rdy0, k.fbjcMode = 0;
        memset(k.fbjdLong, 0, sizeof(k.fbjdLong));
        g_icPlanOrig = FbCapIc;
    }
    // docs/163 §10 stuckcheckcap: a PROBE mark makes this route's next own START a job under PlanDeadlineMs (cap + the deadline
    // stubs' worker slot); unmarked STARTs, a mark on another route, a used mark and PlanDeadlineMs 0 leave own jobs uncut
    {
        const LONG pd0 = k.pdUs, rdy0 = k.pdReady, capped0 = k.fbjcCapped;
        k.pdUs = 300000, k.pdReady = 7;
        g_icPlanOrig = ScSeeIc;
        g_fbSeenCap = g_fbjdSeen = -1;
        assert(Probe(kH1) == 1 && Start(kH1) && JobFor(kH1)->probe == 1 && JobFor(kH1)->fb == 0);
        WaitReady(kH1);
        assert(IcPlanHook(out, kH1) == out && g_fbSeenCap == 300000 && g_fbjdSeen == 1 && g_pdWkTid == 0 && t_scProbe == 0);
        assert(k.fbjcCapped == capped0);  // a cut probe is not a capped foreign job
        Cancel(kH1);
        g_fbSeenCap = g_fbjdSeen = -1;
        assert(Start(kH1) && JobFor(kH1)->probe == 0);  // the mark was used up
        WaitReady(kH1);
        assert(IcPlanHook(out, kH1) == out && g_fbSeenCap == 0 && g_fbjdSeen == 0);
        Cancel(kH1);
        assert(Probe(kH2) == 1 && Start(kH1) && JobFor(kH1)->probe == 0);  // marked another route: not this one
        WaitReady(kH1);
        assert(IcPlanHook(out, kH1) == out);
        Cancel(kH1);
        assert(Start(kH2) && JobFor(kH2)->probe == 0);  // kH1's START took the mark away
        WaitReady(kH2);
        assert(IcPlanHook(out, kH2) == out);
        Cancel(kH2);
        k.pdUs = 0;
        g_fbSeenCap = -1;
        assert(Probe(kH1) == 0 && Start(kH1) && JobFor(kH1)->probe == 0);  // no PlanDeadlineMs: no mark
        WaitReady(kH1);
        assert(IcPlanHook(out, kH1) == out && g_fbSeenCap == 0);
        Cancel(kH1);
        // §10.1 tag: every mark labels the next own START with its round; only the cut bit (or the old optLo 0 form) cuts
        k.pdUs = 300000;
        assert(GetOptionHook(kH1, 0x10000 | 0x200 | 4, kProbeHi, 1) == 0 && Start(kH1) && JobFor(kH1)->probe == 0 && JobFor(kH1)->sc == 0x10204);
        WaitReady(kH1);
        assert(IcPlanHook(out, kH1) == out && g_lastOwnH == kH1 && g_lastOwnSc == 0x10204);
        Cancel(kH1);
        assert(GetOptionHook(kH1, 0x10000 | 0x100 | 2, kProbeHi, 1) == 1 && Start(kH1) && JobFor(kH1)->probe == 1 && JobFor(kH1)->sc == 0x10002);
        WaitReady(kH1);
        assert(IcPlanHook(out, kH1) == out);
        Cancel(kH1);
        assert(Probe(kH1) == 1 && Start(kH1) && JobFor(kH1)->sc == 0x10000);  // old ts3fixes: round unknown
        WaitReady(kH1);
        assert(IcPlanHook(out, kH1) == out);
        Cancel(kH1);
        assert(Start(kH1) && JobFor(kH1)->sc == 0);
        WaitReady(kH1);
        assert(IcPlanHook(out, kH1) == out);
        Cancel(kH1);
        assert(GetOptionHook(kH1, 0x20000 | 0x100, kProbeHi, 1) == 0 && g_scTagH == 0);  // other optLo forms: not this channel
        // docs/250 §7.1 bit 10: the watchdog's idle check; a label only (no probe even with the cut bit, no sync mark), job time in v179
        static_assert(offsetof(Ts3nCounters, wdJobN) == 0x5F18 && offsetof(Ts3nCounters, wdJobLong) == 0x5F24 && sizeof(Ts3nCounters) == 6112 * 4,
                      "counters v179 after the v913 gap (docs/registry.md)");
        const LONG wdN0 = k.wdJobN;
        assert(GetOptionHook(kH1, 0x10000 | 0x400 | 0x100, kProbeHi, 1) == 0);
        assert(Start(kH1));
        assert(JobFor(kH1)->probe == 0 && JobFor(kH1)->sc == 0x10400 && g_scSyncH == 0);  // the cut bit is never part of the tag
        WaitReady(kH1);
        assert(IcPlanHook(out, kH1) == out && k.wdJobN == wdN0 + 1 && k.wdJobMaxUs >= 0 && k.wdJobUs >= 0);
        Cancel(kH1);
        assert(Start(kH1) && JobFor(kH1)->sc == 0);  // one START per mark
        WaitReady(kH1);
        assert(IcPlanHook(out, kH1) == out && k.wdJobN == wdN0 + 1);
        Cancel(kH1);
        char tb[48];
        ScTagText(tb, sizeof(tb), 0x10400), assert(!strcmp(tb, ", watchdog check"));
        ScTagText(tb, sizeof(tb), 0), assert(!strcmp(tb, ", no stuck-check mark"));
        ScTagText(tb, sizeof(tb), 0x10000), assert(!strcmp(tb, ", stuck-check round ?"));
        ScTagText(tb, sizeof(tb), 0x10204), assert(!strcmp(tb, ", stuck-check round 4 (last)"));
        ScTagText(tb, sizeof(tb), 0x10102), assert(!strcmp(tb, ", stuck-check round 2"));
        k.pdUs = pd0, k.pdReady = rdy0;
        g_icPlanOrig = FbCapIc;
        printf("stuckcheckcap PROBE (docs/163 §10): marked own START cut at PlanDeadlineMs on the worker, one START per mark ok\n");
    }

    // docs/202 [UnseenFallback]: a MARK makes the route's next START a job under ufUs (worker deadline slot open); one START
    // per mark, a mark on another route or with ufMode 0 marks nothing; CUT? answers once; sync deadline choice; job end
    {
        static_assert(offsetof(Ts3nCounters, ufMode) == 0x5360 && offsetof(Ts3nCounters, ufCutAsks) == 0x538C && sizeof(Ts3nCounters) == 6112 * 4,
                      "counters v138 layout after v137 (docs/registry.md)");
        const LONG um0 = k.ufMode, uu0 = k.ufUs, ur0 = k.ufReady, pr0 = k.pdReady;
        // the 0.36.91 miss (sv-ufb-farcrowd-loop: a marked 402 ms sync plan, "world A* capped 0"): [UnseenFallback] alone,
        // [RouteBudget] off and no other deadline, must still install the world A* cost stubs
        {
            const LONG c0 = k.fbCapUs, c1 = k.fbjcCapUs, c2 = k.pdUs, c3 = k.pdOwnUs;
            const int kd0 = g_kdIni, ui0 = g_ufIni;
            k.fbCapUs = k.fbjcCapUs = k.pdUs = k.pdOwnUs = 0, g_kdIni = 0;
            g_ufIni = 0;
            assert(!CostStubsWanted(false, 1) && CostStubsWanted(true, -1));
            g_ufIni = 1;
            assert(CostStubsWanted(false, 1) && !CostStubsWanted(false, -1));
            k.fbCapUs = c0, k.fbjcCapUs = c1, k.pdUs = c2, k.pdOwnUs = c3, g_kdIni = kd0, g_ufIni = ui0;
        }
        k.ufMode = 1, k.ufUs = 300000, k.ufReady = 15, k.pdReady = 7;
        g_icPlanOrig = ScSeeIc;
        auto Uf = [](DWORD h, DWORD lo) { return GetOptionHook(h, lo, 0x83000000, 1); };
        g_fbSeenCap = g_fbjdSeen = -1;
        assert(Uf(kH1, 1) == 1 && g_ufH == kH1 && Start(kH1) && JobFor(kH1)->uf == 1 && g_ufH == 0);
        WaitReady(kH1);
        assert(IcPlanHook(out, kH1) == out && g_fbSeenCap == 220000 && g_fbjdSeen == 1 && g_pdWkTid == 0 && t_ufJob == 0 && k.ufJobs >= 1);
        Cancel(kH1);
        Uf(kH1, 2);  // clears whatever the fake IC left
        g_ufCutH = 0;
        g_fbSeenCap = -1;
        assert(Start(kH1) && JobFor(kH1)->uf == 0);  // used up
        WaitReady(kH1);
        assert(IcPlanHook(out, kH1) == out && g_fbSeenCap == 0);
        Cancel(kH1);
        assert(Uf(kH2, 1) == 1 && Start(kH1) && JobFor(kH1)->uf == 0 && g_ufH == kH2);  // another route's mark stays
        WaitReady(kH1);
        assert(IcPlanHook(out, kH1) == out);
        Cancel(kH1);
        assert(Uf(kH2, 2) == 0 && g_ufH == 0);  // CUT? clears the mark
        k.ufMode = 0;
        const LONG refused0 = k.ufMarkRefused;
        assert(Uf(kH1, 1) == 0 && g_ufH == 0 && k.ufMarkRefused == refused0 + 1);
        k.ufMode = 1, k.ufReady = 9;  // no cell / water exit: nothing to cut with
        assert(Uf(kH1, 1) == 0);
        k.ufReady = 15;
        assert(GetOptionHook(kH1, 3, 0x83000000, 1) == 0 && g_ufH == 0);  // other optLo: passed to the game (fake answers 0)
        g_ufCutH = kH1;
        assert(Uf(kH1, 2) == 1 && Uf(kH1, 2) == 0);  // once
        // sync deadline: the effective cut (setting 300 - start overrun 80 = 220 ms), or a smaller PlanDeadlineMs; exempt (< 0)
        // and none (0) take the cut
        assert(UfSyncUs(0) == 220000 && UfSyncUs(-1) == 220000 && UfSyncUs(250000) == 220000 && UfSyncUs(200000) == 200000 &&
               UfSyncUs(400000) == 220000 && k.ufEffCutUs[0] == 220000 && k.ufOverP99Us[0] == 80000);
        // player setting clamp: ini and runtime (unseenfbus) values outside [50, 175] ms
        assert(UfClampMs(300) == 300 && UfClampMs(200) == 200 && UfClampMs(175) == 175 && UfClampMs(120) == 175 && UfClampMs(-5) == 175 && UfClampMs(301) == 300 && UfClampMs(5000) == 300);
        k.ufUs = 900000;
        assert(UfUs() == 300000 && UfSyncUs(0) == 220000 && UfSyncUs(200000) == 200000);
        k.ufUs = 0;
        assert(UfUs() == 175000 && UfEffUs(0) == 95000);  // 175 - 80
        k.ufUs = 300000;
        t_pdHitLast = true;
        u8 res[0x40] = {};
        *(DWORD*)res = 2;  // failed
        const LONG sc0 = k.ufSyncCut;
        UfSyncEnd(kH2, res, 130000);
        assert(k.ufSyncCut == sc0 + 1 && g_ufCutH == kH2 && !t_pdHitLast);
        g_ufCutH = 0;
        t_pdHitLast = true;
        *(DWORD*)res = 1;  // succeeded at the deadline: not a cut
        UfSyncEnd(kH2, res, 130000);
        assert(k.ufSyncCut == sc0 + 1 && g_ufCutH == 0);
        AsyncJob fj = {};
        *(DWORD*)fj.result = 2;
        const LONG jc0 = k.ufJobCut, lg0 = k.ufLong;
        UfJobEnd(&fj, true, 350000, -1, 0);
        assert(fj.ufCut == 1 && k.ufJobCut == jc0 + 1 && k.ufLong == lg0 + 1 && k.ufMaxUs >= 350000);
        UfJobEnd(&fj, false, 1000, -1, 0);
        assert(fj.ufCut == 0);
        TestUfStrict();
        k.ufMode = um0, k.ufUs = uu0, k.ufReady = ur0, k.pdReady = pr0;
        g_icPlanOrig = FbCapIc;
        printf("unseen fallback (docs/202): MARK -> job under ufUs, one START per mark, CUT? once, sync deadline, job end ok\n");
    }
    // 4c. docs/134 §11: the job cap is a deadline from the job's start, read every 4th cost call of either kind
    {
        static_assert(offsetof(Ts3nCounters, fbjcCapAtMaxUs) == 0x3604 && offsetof(Ts3nCounters, fbjcWaitHist) == 0x3634 &&
                          sizeof(Ts3nCounters) == 6112 * 4, "counters v84 layout after v83 (docs/registry.md)");
        const PlanLockedFn planSave = g_planOrig;
        const LONG readySave = k.budgetReady;
        g_planOrig = FbSlowSearch;
        k.budgetReady = 1;
        g_icPlanOrig = FbSlowIc;
        memset((void*)&k.fbjcCapAtMaxUs, 0, sizeof(LONG) * 18);
        k.fbjcMode = 1;
        k.fbjcCapped = 0;
        // slow expansions (25 ms a node): the old clock read the time first at node 16 (>= 400 ms); now at most 2 nodes late
        g_fbPreMs = 0, g_fbStepMs = 25, g_fbPostMs = 0;
        assert(Foreign(kH1));
        WaitReady(kH1);
        assert(IcPlanHook(out, kH1) == out && k.fbjcCapped == 1 && g_fbNodes < 16);
        // the cap fired at a deadline read (every 4th call: the wall comes at that call or the next) and no later than the gap
        // since the read before it, which saw the clock short of the deadline; the late counter agrees with the measured time
        {
            const int at = g_fbWallAt & ~3;
            assert(g_fbWallAt >= 4 && g_fbWallAt - at <= 1);
            const LONGLONG gapUs = (g_fbTs[at][1] - g_fbTs[at - 4][0]) * 1000000 / g_qpf.QuadPart;
            if (k.fbjcCapAtMaxUs - 200000 > gapUs + 1)
                printf("4c: cap at %ld us, deadline reads %lld us apart (calls %d, %d)\n", k.fbjcCapAtMaxUs, gapUs, at - 4, at);
            assert(k.fbjcCapAtMaxUs >= 200000 && k.fbjcCapAtMaxUs - 200000 <= gapUs + 1);
            assert(k.fbjcCapLate == (k.fbjcCapAtMaxUs > 220000) &&
                   k.fbjcPlanMaxUs + 1 >= (g_fbTs[at][0] - g_fbPreT) * 1000000 / g_qpf.QuadPart);  // the plan ran past the firing read
        }
        Cancel(kH1);
        // 300 ms before the first node (pending lot updates, lazy lot builds): the first cost call is already past the deadline,
        // the search ends at its first edge; the cap fired late and says so
        g_fbPreMs = 300, g_fbStepMs = 0;
        const LONG late1 = k.fbjcCapLate;  // the case above may have been late too (finer timer, load)
        assert(Foreign(kH1));
        WaitReady(kH1);
        assert(IcPlanHook(out, kH1) == out && k.fbjcCapped == 2 && g_fbNodes == 0 && g_fbWallAt == 1);
        // the cap fired at the first deadline read, after the work before it (measured: Sleep may wake a little early)
        assert(k.fbjcCapAtMaxUs + 1 >= (g_fbTs[0][0] - g_fbPreT) * 1000000 / g_qpf.QuadPart && k.fbjcCapAtMaxUs > 220000 &&
               k.fbjcCapLate == late1 + 1);
        Cancel(kH1);
        // work after the cap (lot legs per candidate) is the tail the cap cannot stop: measured
        g_fbPreMs = 0, g_fbStepMs = 25, g_fbPostMs = 150;
        assert(Foreign(kH1));
        WaitReady(kH1);
        assert(IcPlanHook(out, kH1) == out && k.fbjcCapped == 3 && k.fbjcTailMaxUs + 2 >= g_fbPostUs &&g_fbPostUs >= 100000);
        Cancel(kH1);
        // an own job has no deadline: the whole search runs
        g_fbPreMs = g_fbStepMs = g_fbPostMs = 0;
        assert(Start(kH1));
        WaitReady(kH1);
        assert(IcPlanHook(out, kH1) == out && k.fbjcCapped == 3 && g_fbNodes == 1000);
        Cancel(kH1);
        // switch off: a foreign job is not capped but still split and counted
        k.fbjcMode = 0;
        assert(Foreign(kH1));
        WaitReady(kH1);
        assert(IcPlanHook(out, kH1) == out && k.fbjcCapped == 3 && g_fbNodes == 1000);
        Cancel(kH1);
        LONG jobs = 0;
        for (int i = 0; i < 6; i++) jobs += k.fbjcJobHist[i];
        assert(jobs == 4 && k.fbjcJobHist[0] >= 1 && k.fbjcJobHist[0] + k.fbjcJobHist[1] + k.fbjcJobHist[2] + k.fbjcJobHist[3] == 4);
        // sim waits behind a foreign job: buckets and max; other waits are not counted
        FbjcSimWait(50000, true), FbjcSimWait(2500000, true), FbjcSimWait(9000000, false);
        assert(k.fbjcWaitHist[0] == 1 && k.fbjcWaitHist[5] == 1 && k.fbjcSimWaitMaxUs == 2500000);
        g_planOrig = planSave;
        k.budgetReady = readySave;
        g_icPlanOrig = FbCapIc;
        memset((void*)&k.fbjcCapAtMaxUs, 0, sizeof(LONG) * 18);
        k.fbjcCapped = 0;
    }
    k.fbCapUs = 0;
    assert(IcPlanHook(out, kH2) == out && g_fbSeenCap == 0 && k.fbCapped == 1);  // off: no cap
    g_icPlanOrig = FakeIc;

    // 5. LogFallbackStack: interpreter frames named innermost first; a bad MonoMethod reads as "?"
    const char* names[] = {"Route", "ScriptCore", "Plan", "Sim+GroupChatB", "Sims3.Gameplay.Actors", "RouteToGroupTalk"};
    for (int i = 0; i < 2; i++) {
        *(const char**)(g_fbCls[i] + 0x34) = names[i * 3];
        *(const char**)(g_fbCls[i] + 0x38) = names[i * 3 + 1];
        *(u8**)(g_fbMeth[i] + 8) = g_fbCls[i];
        *(const char**)(g_fbMeth[i] + 0x18) = names[i * 3 + 2];
        *(u8**)(g_fbInfo[i] + 8) = g_fbMeth[i];
    }
    *(DWORD*)(g_fbInfo[2] + 8) = 0x10;  // unreadable
    for (int i = 0; i < 3; i++) {
        g_fbFrame[i][0] = i < 2 ? (DWORD)(uintptr_t)g_fbFrame[i + 1] : 0;
        g_fbFrame[i][1] = (DWORD)(uintptr_t)g_fbInfo[i];
    }
    *(DWORD**)(g_fbCtx + 8) = g_fbFrame[0];
    const u8* (__cdecl* savedCtx)() = g_lgCtx;
    g_lgCtx = FbFakeCtx;
    char st[200];
    assert(FbStackText(st, sizeof(st), 8) == 3 &&
           strcmp(st, "ScriptCore.Route.Plan < Sims3.Gameplay.Actors.Sim+GroupChatB.RouteToGroupTalk < ?") == 0);
    assert(FbStackText(st, sizeof(st), 1) == 1 && strcmp(st, "ScriptCore.Route.Plan") == 0);
    {   // docs/67 §22 (Crash S): a fault in the callvirt miss helper names caller, callee, receiver links and the script stack
        static DWORD rm[4], f3[1], f2[1], obj[1], frame[16];
        rm[2] = (DWORD)(uintptr_t)g_fbMeth[1];  // RuntimeMethod +8 = MonoMethod
        f2[0] = (DWORD)(uintptr_t)f3, obj[0] = (DWORD)(uintptr_t)f2;  // free-list shape: object -> heap -> heap
        CONTEXT c = {};
        c.Eip = 0x00E5201F, c.Esi = (DWORD)(uintptr_t)g_fbMeth[0], c.Ebp = (DWORD)(uintptr_t)(frame + 12);
        frame[12 - 4] = (DWORD)(uintptr_t)rm, frame[12 - 10] = (DWORD)(uintptr_t)obj;  // [ebp-0x10], [ebp-0x28]
        char mf[512], want[200];
        assert(MonoFaultText(&c, mf, sizeof(mf)));
        sprintf_s(want, "receiver %08lX vtable %08lX klass %08lX;", (DWORD)(uintptr_t)obj, (DWORD)(uintptr_t)f2, (DWORD)(uintptr_t)f3);
        assert(strstr(mf, "in Sims3.Gameplay.Actors.Sim+GroupChatB.RouteToGroupTalk") && strstr(mf, "callvirt of ScriptCore.Route.Plan") &&
               strstr(mf, want) && strstr(mf, "script stack: ScriptCore.Route.Plan < "));
        c.Eip = 0x00E54AEC, c.Ebp = 0x10;  // interpreter body, unreadable frame: still one line, no callee part
        assert(MonoFaultText(&c, mf, sizeof(mf)) && strstr(mf, "in ? (rm 00000000), receiver 00000000") && !strstr(mf, "callvirt"));
        c.Eip = 0x00401000;
        assert(!MonoFaultText(&c, mf, sizeof(mf)));
    }
    g_fbStackFrames = 8;
    g_rfbLines = 0;
    RfbNote(RFB_NOSTART, 300, "nojob", kH1, NULL);
    assert(k.fbStacks == 1);
    g_fbStackFrames = 0;
    RfbNote(RFB_NOSTART, 300, "nojob", kH1, NULL);
    assert(k.fbStacks == 1);
    g_lgCtx = savedCtx;

    k.fbMode = 0;
    g_fbAcq = (LockAcquireFn)(uintptr_t)0x004E16F0;
    g_fbRel = (FbRelFn)(uintptr_t)0x004E17B0;
    g_fbApply = (FbApplyFn)(uintptr_t)0x0082C420;
    g_routeIsActive = savedActive;
    g_rfbHandle = 0;
    memset(&k.rfbLogMs, 0, sizeof(LONG) * 8);
    memset((void*)&k.fbMode, 0, sizeof(LONG) * 10);  // v73
    memset((void*)&k.fbRetryMode, 0, sizeof(LONG) * 5);  // v75
    memset((void*)&k.fbjcMode, 0, sizeof(LONG) * 7);  // v80 (docs/134 §10)
}

static void TestRouteAsync() {
    const DWORD me = GetCurrentThreadId();
    const Ts3nCounters& c = ts3native_counters;
    free(g_tab);
    g_tab = NULL;  // no route cache: the fake plans below have no real points
    InitializeCriticalSection(&g_fakeLock);
    g_icPlanOrig = FakeIc;
    g_getOptOrig = FakeGetOption;
    g_lockOrig = FakeLockAcquire;
    u8 out[0x48];
    const DWORD inf = 0xFFFFFFFF;
    g_hostPtr = &g_fakeHostPtr;
    g_curState = &g_fakeCur;
    FakeTask(kRoot, 7);  // the Sim's own task at its top level: may yield

    ts3native_counters.asyncMode = 0;  // switch off: START says no, the C# hook plans synchronously
    assert(!Start(kH1) && c.asyncStarted == 0);
    assert(GetOptionHook(kH1, 1, 0, 0) == 0);  // other options pass through
    ts3native_counters.asyncMode = 1;
    assert(!Start(kH1) && c.asyncStarted == 0);  // detours not all in place (only Route_GetOption, for NOYIELD): START says no
    ts3native_counters.asyncReady = 1;
    assert(IcPlanHook(out, kH1) == out && *(DWORD*)(out + 0x40) == me);  // no job: plain call
    // H6 M1: START needs the gate's closures and a quiet world; the worker then holds the world's +0x918 for the job
    static u8 m1World[0xA00], m1P[0x200], m1Wm[0x200];
    *(u8**)(m1World + 0x95C) = m1P;
    *(u8**)(m1World + 0x964) = m1Wm;
    CRITICAL_SECTION* m1Lock = (CRITICAL_SECTION*)(m1World + 0x918);
    InitializeCriticalSection(m1Lock);
    g_rgLock = m1Lock;
    g_rgRelOrig = FakeLockRelease;
    assert(!Start(kH1) && c.asyncRefusedM1[0] == 1 && c.asyncStarted == 0);  // closures off
    ts3native_counters.rgEnforce = 1;

    // started -> not ready while running -> ready -> the next Route.Plan takes the worker's result
    g_icSleepMs = 30;
    assert(Start(kH1) && c.asyncStarted == 1);
    assert(!Ready(kH1));
    assert(Ready(kH2));  // no job for that route: "ready", nothing armed
    WaitReady(kH1);
    assert(IcPlanHook(out, kH1) == out && *(DWORD*)(out + 0x40) == g_workerTid && *(DWORD*)(out + 4) == kH1);
    assert(c.asyncCollected == 1 && g_async.state == A_IDLE && g_async.lock == m1Lock);
    assert(TryEnterCriticalSection(m1Lock));  // the worker released the job's lock
    LeaveCriticalSection(m1Lock);
    Cancel(kH1);

    // CANCEL after READY (destination went invalid, Plan returned before the internal call): the result is not handed to a later plan
    assert(Start(kH1));
    WaitReady(kH1);
    Cancel(kH1);
    assert(IcPlanHook(out, kH1) == out && *(DWORD*)(out + 0x40) == me && c.asyncDropped == 1 && c.asyncCollected == 1);

    // 0.11.3 queue: another route's START while a job runs is queued behind it; a plain plan of the running route waits
    // for it, drops the result and plans itself; the queued one runs next on the worker and is collected
    g_icSleepMs = 50;
    assert(Start(kH1));
    assert(Start(kH2) && c.asyncRefused == 1 && c.asyncQueueMax >= 2);  // 1 = the M1 refusal above
    assert(IcPlanHook(out, kH1) == out && *(DWORD*)(out + 0x40) == me && c.asyncDropped == 2);
    WaitReady(kH2);
    assert(IcPlanHook(out, kH2) == out && *(DWORD*)(out + 0x40) == g_workerTid && c.asyncCollected == 2 && !AsyncBusy());
    // a queued job that has not started yet is taken back by its route's own plan and planned on this thread
    g_icSleepMs = 50;
    assert(Start(kH1) && Start(kH2));
    assert(IcPlanHook(out, kH2) == out && *(DWORD*)(out + 0x40) == me && c.asyncDropped == 3);
    WaitReady(kH1);
    assert(IcPlanHook(out, kH1) == out && *(DWORD*)(out + 0x40) == g_workerTid && c.asyncCollected == 3 && !AsyncBusy());
    // the same route started again drops its older request; a result whose Sim vanished is reclaimed when the queue is full
    g_icSleepMs = 0;
    assert(Start(kH1));
    WaitReady(kH1);
    assert(Start(kH1) && c.asyncDropped == 4);
    WaitReady(kH1);
    Cancel(kH1);
    assert(c.asyncDropped == 5 && !JobFor(kH1));
    for (DWORD i = 0; i < 8; i++) assert(Start(0x25000100 + i));
    for (DWORD i = 0; i < 8; i++) WaitReady(0x25000100 + i);
    const LONG full0 = c.asyncQueueFull;
    assert(!Start(kH2) && c.asyncQueueFull == full0 + 1);  // 8 uncollected results, all fresh
    g_jobs[3].doneTick -= kDoneKeepMs;
    assert(Start(kH2) && c.asyncDropped == 6 && !JobFor(0x25000103));
    WaitReady(kH2);
    assert(IcPlanHook(out, kH2) == out && *(DWORD*)(out + 0x40) == g_workerTid && c.asyncCollected == 4);
    for (DWORD i = 0; i < 8; i++) Cancel(0x25000100 + i);
    assert(c.asyncDropped == 13 && !AsyncBusy());
    // the counts below continue from the single-slot test: dropped 3, collected 2 (relative checks keep them readable)
    ts3native_counters.asyncDropped = 3;
    ts3native_counters.asyncCollected = 2;

    // lock pump: the job holds the routing lock and makes a managed call; the owner, blocked on that lock, runs it meanwhile
    g_icHoldLock = g_icInvoke = 1;
    g_icSleepMs = 20;
    const LONG inv0 = c.invokes;
    assert(Start(kH1));
    while (!g_icLockTaken) Sleep(1);
    g_invokeTid = 0;
    assert(LockAcquireHook(&g_fakeLock, NULL, &inf) == 1 && g_invokeTid == me && c.invokes == inv0 + 1 && c.asyncPumpWaits == 1);
    LeaveCriticalSection(&g_fakeLock);
    assert(LockAcquireHook(&g_fakeLock, NULL, &inf) == 1);  // no job, or not contended: straight through
    LeaveCriticalSection(&g_fakeLock);
    WaitReady(kH1);
    assert(IcPlanHook(out, kH1) == out && c.asyncCollected == 3);

    // a plan of another route (Route_PlanLocked) while a job runs: waits for the job, running its managed call, then runs here
    ts3native_counters.workerReady = 1;
    ts3native_counters.workerMode = 0;
    g_planOrig = WorkerPlan;
    g_icInvoke = 1;
    g_icSleepMs = 30;
    u8 opts[0x38] = {0};
    int s[2] = {1, 2}, g[2] = {3, 4}, list[2];
    assert(Start(kH1));
    while (g_async.state != A_RUNNING) Sleep(0);  // the worker has claimed it (queued jobs start a moment later)
    assert(PlanLockedHook(NULL, NULL, list, s, g, 0x100, 1, opts, NULL, NULL, 1, 0) == 1 && g_planTid == me);
    assert(JobFor(kH1) && JobFor(kH1)->state == J_DONE && g_invokeTid == me && c.invokes == inv0 + 2);
    Cancel(kH1);  // drops the done kH1 result nobody collected
    assert(c.asyncDropped == 4 && g_async.state == A_IDLE);
    assert(Start(kH2) && c.asyncDropped == 4);
    WaitReady(kH2);
    assert(IcPlanHook(out, kH2) == out);
    assert(c.watchdogs == 0 && c.asyncUs >= 30000 && c.asyncMaxUs >= 30000);
    ts3native_counters.workerReady = 0;

    // CANCEL while the job runs (Sim reset): waits for it, drops it; no orphan job, the next START is not refused
    g_icSleepMs = 40;
    assert(Start(kH1));
    Cancel(kH2);  // another route's CANCEL leaves it alone
    assert(JobFor(kH1) != NULL);
    Cancel(kH1);
    assert(g_async.state == A_IDLE && c.asyncDropped == 5);
    assert(IcPlanHook(out, kH1) == out && *(DWORD*)(out + 0x40) == me && c.asyncDropped == 5);

    // mode switched off mid-job: the owner's next entry (READY here) drains and drops it, Route.Plan then plans inline
    assert(Start(kH1));
    ts3native_counters.asyncMode = 0;
    assert(Ready(kH1) && g_async.state == A_IDLE && c.asyncDropped == 6);
    assert(IcPlanHook(out, kH1) == out && *(DWORD*)(out + 0x40) == me);
    ts3native_counters.asyncMode = 1;
    assert(Start(kH1));
    ts3native_counters.asyncMode = 0;  // ... or the internal call itself
    assert(IcPlanHook(out, kH1) == out && *(DWORD*)(out + 0x40) == me && g_async.state == A_IDLE && c.asyncDropped == 7);
    ts3native_counters.asyncMode = 1;

    // lock pump on a finite deadline: managed call served, lock taken; a deadline that passes ends with the original's -2
    g_icHoldLock = g_icInvoke = 1;
    g_icSleepMs = 20;
    const LONG inv1 = c.invokes, waits1 = c.asyncPumpWaits;
    assert(Start(kH1));
    while (!g_icLockTaken) Sleep(1);
    g_invokeTid = 0;
    const DWORD later = GetTickCount() + 5000;
    assert(LockAcquireHook(&g_fakeLock, NULL, &later) == 1 && g_invokeTid == me && c.invokes == inv1 + 1 && c.asyncPumpWaits == waits1 + 1);
    LeaveCriticalSection(&g_fakeLock);
    WaitReady(kH1);
    assert(IcPlanHook(out, kH1) == out);
    g_icHoldLock = 1;
    g_icSleepMs = 300;
    assert(Start(kH1));
    while (!g_icLockTaken) Sleep(1);
    const DWORD soon = GetTickCount() + 30, t0 = GetTickCount();
    assert(LockAcquireHook(&g_fakeLock, NULL, &soon) == -2 && g_async.state == A_RUNNING);
    TIMING_ASSERT("lock deadline 30 ms returns < 250 ms", GetTickCount() - t0 < 250);
    WaitReady(kH1);
    assert(IcPlanHook(out, kH1) == out);
    g_icSleepMs = 0;

    // yield guard (interp.c:3595): only the task's root interpreter state may yield
    assert(NoYield(0) == 0 && NoYield(kH1) == 0);
    FakeTask(kNested, 7);  // inside a native->managed call during the task
    const LONG refused = c.asyncRefused, started = c.asyncStarted;
    assert(NoYield(0) == 1 && !Start(kH1) && c.asyncRefused == refused + 1 && c.asyncStarted == started && g_async.state == A_IDLE);
    FakeTask(kRoot, 0);  // no task running (stale slot)
    assert(NoYield(0) == 1 && !Start(kH1));
    FakeTask(0, 7);
    assert(NoYield(0) == 1);
    g_fakeHostPtr = 0;  // host not created yet
    assert(NoYield(0) == 1);
    FakeTask(kRoot, 7);
    assert(GetOptionHook(kH1, 0, kNoYieldHi, 1) == 0 && GetOptionHook(kH1, 1, kAsyncOptHi | kNoYieldHi, 1) == 0);  // not ours: passes through

    // v35: a long sync plan after a refused START is counted under its reason; short ones and LogFallbackMs=0 are not
    static_assert(offsetof(Ts3nCounters, rfbLogMs) == 0x1384 && offsetof(Ts3nCounters, rfbMs) == 0x1388 &&
                  offsetof(Ts3nCounters, rfbN) == 0x1394 && offsetof(Ts3nCounters, rfbMax) == 0x13A0 &&
                  sizeof(Ts3nCounters) >= 1257 * 4, "v35 layout");
    Ts3nCounters& w = ts3native_counters;
    w.rtBusy = 1;  // save busy: M1 refuses (asyncRefusedM1[1])
    g_icSleepMs = 60;
    assert(!Start(kH1) && IcPlanHook(out, kH1) == out && w.rfbN[0] == 0);  // LogFallbackMs=0: not timed
    w.rfbLogMs = 55;
    assert(!Start(kH1) && IcPlanHook(out, kH1) == out && w.rfbN[0] == 1 && w.rfbMs[0] >= 55);
    assert((w.rfbMax & 0xFF) == RFB_M1 + 1 && (w.rfbMax >> 8) >= 55 && g_rfbLines == 1 && g_rfbHandle == 0);
    assert(IcPlanHook(out, kH2) == out && w.rfbN[2] == 1);  // no START for this route: NoStart
    w.rtBusy = 0;
    g_icSleepMs = 0;
    assert(Start(kH1));
    WaitReady(kH1);
    assert(IcPlanHook(out, kH1) == out);  // async: collected, not counted
    assert(w.rfbN[0] == 1 && w.rfbN[1] == 0 && w.rfbN[2] == 1);
    for (int i = 0; i < 30; i++) RfbNote(RFB_QFULL, 300, "nojob", kH1, NULL);
    assert(w.rfbN[1] == 30 && w.rfbMs[1] == 9000 && (w.rfbMax >> 8) == 300 && (w.rfbMax & 0xFF) == RFB_QFULL);
    assert(g_rfbLines == (DWORD)kRfbLinesPerMin && g_rfbDropped == 30 - (kRfbLinesPerMin - 2));  // rate limit: 20 lines/min
    memset(&w.rfbLogMs, 0, sizeof(LONG) * 8);
    TestFallback();    // docs/134 on the same setup
    TestA10();         // A10 on the same setup
    TestLocoWindow();  // H2 on the same async setup (real worker, M1 world, gate enforced)
    TestH2Threaded();  // docs/149: threaded passes on the real lock
    TestH2Ic();        // docs/177 §4: the same hold around the listed script ICs
    TestRcGuard();     // docs/177 §7: the sim keeps the query's +0x918 level until its caller returns
    TestJobYield();    // docs/183: JOBBUSY + sim job waits by site
    TestRouteWorkerWait();  // docs/203 §6: job-start wait, no lost wake-ups
    ts3native_counters.rgEnforce = 0;
    ts3native_counters.rgOwnerTid = 0;
    ts3native_counters.rgAcquires = 0;  // the worker took m1Lock as the learned lock
    g_rgLock = NULL;
    g_async.lock = NULL;
    DeleteCriticalSection(m1Lock);
}

// --- save write: the backup job against a fake native save on real temp folders ---
static wchar_t g_sv[MAX_PATH];         // <tmp>\User
static const wchar_t* g_svName;        // name the fake game passes to the backup step
static bool g_svTouch, g_svReachBackup;
static int g_svWritePath;  // 0 = through SaveWriteHook (0xAB12AF ours), 1 = Apex owns the call + gate entry, 2 = no gate either
static int g_svOrigBackups;
static DWORD __fastcall FakeInfo(void*, void*, u8*, int, int) { return 0; }
static DWORD __fastcall FakeWrite(void*, void*, void*, int) {
    Sleep(30);
    if (g_svTouch) {  // the old save changes while the worker copies it
        wchar_t p[MAX_PATH];
        swprintf_s(p, L"%s\\Saves\\Foo.sims3\\Meta.data", g_sv);
        HANDLE h = CreateFileW(p, FILE_APPEND_DATA, 0, NULL, OPEN_EXISTING, 0, NULL);
        DWORD n;
        WriteFile(h, "x", 1, &n, NULL);
        CloseHandle(h);
    }
    return 0;
}
static DWORD __fastcall FakeBackup(void*, void*, const wchar_t*) { g_svOrigBackups++; return 1; }
static int g_svFlushes, g_svLoadCopies;  // the game's flush FUN_00AAF4B0 ran / the load copy ran
static void __cdecl FakeFlush(const wchar_t*) { g_svFlushes++; }
static void SvFlush() {  // steps 9 and 11 flush the new save folder
    wchar_t p[MAX_PATH];
    swprintf_s(p, L"%s\\Saves\\Foo.sims3", g_sv);
    SaveFlushHook(p);
}
static DWORD __fastcall FakeCopy(void*, void*, const wchar_t*, int) { SvFlush(); return 1; }
static DWORD __fastcall FakeFinal(void*, void*) { SvFlush(); return 1; }
static DWORD __fastcall FakeLoadCopy(void*, void*, const wchar_t*, int) { g_svLoadCopies++; return 1; }
static DWORD __fastcall FakeGame(void*, void*, void* info, int travel, int) {  // FUN_00AB1030's order of steps
    if (SaveInfoHook(NULL, NULL, (u8*)info, 0, travel)) return 2;
    if (!g_svWritePath) SaveWriteHook(NULL, NULL, NULL, 1);
    else {  // docs/148 §7a: the game's call; the [RouteGate] stub at 0xAAC110 reports the entry
        Sleep(10);  // Apex's preparation before its call: in the info span, not in the gate span
        if (g_svWritePath == 1) SaveGateEnter(GetCurrentThreadId());
        g_saveWriteOrig(NULL, NULL, NULL, 1);
    }
    if (!travel && g_svReachBackup && SaveBackupHook(NULL, NULL, g_svName)) SaveCopyHook(NULL, NULL, g_svName, 0);
    SaveFinalHook(info, NULL);
    return 0;
}

static void SvFile(const wchar_t* rel, const char* text) {
    wchar_t p[MAX_PATH];
    swprintf_s(p, L"%s\\%s", g_sv, rel);
    HANDLE h = CreateFileW(p, GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, 0, NULL);
    assert(h != INVALID_HANDLE_VALUE);
    DWORD n;
    WriteFile(h, text, (DWORD)strlen(text), &n, NULL);
    CloseHandle(h);
}
static bool SvExists(const wchar_t* rel) {
    wchar_t p[MAX_PATH];
    swprintf_s(p, L"%s\\%s", g_sv, rel);
    return GetFileAttributesW(p) != INVALID_FILE_ATTRIBUTES;
}
static u64 SvHash(const wchar_t* rel) {
    wchar_t p[MAX_PATH];
    swprintf_s(p, L"%s\\%s", g_sv, rel);
    u64 h, b;
    assert(TreeHashOf(p, &h, &b));
    return h;
}
static void SvReset() {  // old save with a world package, metadata and a sub folder; an older backup with other files
    RemoveTree(g_sv);
    wchar_t p[MAX_PATH];
    CreateDirectoryW(g_sv, NULL);
    const wchar_t* dirs[] = {L"Saves", L"Saves\\Foo.sims3", L"Saves\\Foo.sims3\\Sub", L"Saves\\Foo.sims3.backup"};
    for (const wchar_t* d : dirs) { swprintf_s(p, L"%s\\%s", g_sv, d); assert(CreateDirectoryW(p, NULL)); }
    SvFile(L"Saves\\Foo.sims3\\World_0x1.nhd", "world package bytes");
    SvFile(L"Saves\\Foo.sims3\\Meta.data", "meta");
    SvFile(L"Saves\\Foo.sims3\\Sub\\x.package", "sub");
    SvFile(L"Saves\\Foo.sims3.backup\\Old.nhd", "older save");
}

static void TestSaveWrite() {
    // PatchCalls: a site that is not a call to the expected target -> nothing written; a matching one -> rel32 rewritten.
    u8* code = (u8*)VirtualAlloc(NULL, 64, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
    const DWORD site = (DWORD)(uintptr_t)code, target = site + 0x20;
    code[0] = 0xE8;
    *(LONG*)(code + 1) = 0x20 - 5;
    CallSite wrong[] = {{site, target + 1, (void*)Hook}}, right[] = {{site, target, (void*)Hook}};
    assert(!PatchCalls("wrong", wrong, 1) && *(LONG*)(code + 1) == 0x20 - 5);
    assert(PatchCalls("right", right, 1) && site + 5 + *(LONG*)(code + 1) == (DWORD)(uintptr_t)Hook);

    g_saveGameOrig = FakeGame; g_saveInfoOrig = FakeInfo; g_saveWriteOrig = FakeWrite;
    g_saveBackupOrig = FakeBackup; g_saveCopyOrig = FakeCopy; g_saveFinalOrig = FakeFinal;
    g_saveFlushOrig = FakeFlush; g_loadCopyOrig = FakeLoadCopy;
    QueryPerformanceFrequency(&g_qpf);
    wchar_t tmp[MAX_PATH];
    GetTempPathW(MAX_PATH, tmp);
    swprintf_s(g_sv, L"%sts3n-selftest-%lu", tmp, GetCurrentProcessId());
    // own page: a static buffer can end at a section edge, where Readable() over the 520-char path fails
    static u8* const info = (u8*)VirtualAlloc(NULL, 0x1000, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    swprintf_s((wchar_t*)(info + kInfoNhdPath), 520, L"%s\\Saves\\Foo.sims3\\World_0x1.nhd", g_sv);
    Ts3nCounters& c = ts3native_counters;

    // 1. mode 1: the worker copy becomes the backup, the game's backup copy is skipped, no temp copy left.
    SvReset();
    const u64 old = SvHash(L"Saves\\Foo.sims3");
    c.saveMode = 1; g_svName = L"Foo"; g_svTouch = false; g_svReachBackup = true; g_svOrigBackups = 0;
    assert(SaveGameHook(NULL, NULL, info, 0, 0) == 0);
    assert(g_svOrigBackups == 0 && c.saveJobs == 1 && c.savePromoted == 1 && c.saveFallbacks == 0 && c.saveCount == 1);
    assert(SvHash(L"Saves\\Foo.sims3.backup") == old && !SvExists(L"Saves\\Foo.sims3.backup\\Old.nhd"));
    assert(!SvExists(L"ts3native-backup.tmp") && c.saveLastWriteUs >= 20000);

    // 2. the name with ".sims3" is the same folder.
    SvReset(); g_svName = L"Foo.sims3";
    SaveGameHook(NULL, NULL, info, 0, 0);
    assert(g_svOrigBackups == 0 && c.savePromoted == 2);

    // 3. old save changed during the write -> copy dropped, the game backs up.
    SvReset(); g_svName = L"Foo"; g_svTouch = true;
    SaveGameHook(NULL, NULL, info, 0, 0);
    assert(g_svOrigBackups == 1 && c.saveFallbacks == 1 && c.savePromoted == 2);
    assert(SvExists(L"Saves\\Foo.sims3.backup\\Old.nhd") && !SvExists(L"ts3native-backup.tmp"));  // ours never touched it
    g_svTouch = false;

    // 4. another folder name at the backup step -> fallback.
    SvReset(); g_svName = L"Bar";
    SaveGameHook(NULL, NULL, info, 0, 0);
    assert(g_svOrigBackups == 2 && c.saveFallbacks == 2 && SvExists(L"Saves\\Foo.sims3.backup\\Old.nhd"));

    // 5. save fails before the backup step: the job is waited for and its copy removed; backup untouched.
    SvReset(); g_svName = L"Foo"; g_svReachBackup = false;
    SaveGameHook(NULL, NULL, info, 0, 0);
    assert(c.saveJobs == 5 && !SvExists(L"ts3native-backup.tmp") && SvExists(L"Saves\\Foo.sims3.backup\\Old.nhd"));
    g_svReachBackup = true;

    // 6. travel save, mode 0 and a new save (no world package yet) start no job.
    SvReset();
    SaveGameHook(NULL, NULL, info, 1, 1);
    c.saveMode = 0;
    SaveGameHook(NULL, NULL, info, 0, 0);
    assert(c.saveJobs == 5 && g_svOrigBackups == 3);
    c.saveMode = 1;
    SvReset();
    wchar_t p[MAX_PATH];
    swprintf_s(p, L"%s\\Saves\\Foo.sims3\\World_0x1.nhd", g_sv);
    DeleteFileW(p);
    SaveGameHook(NULL, NULL, info, 0, 0);
    assert(c.saveJobs == 5 && g_svOrigBackups == 4 && c.saveCount == 8);

    // 7. write timing without the 0xAB12AF hook (Apex owns it): gate span, else info span, else not measured.
    char b[48];
    assert(g_st.writeHow == SW_CALL && strstr(SaveWriteText(b, sizeof(b), g_st), " (call)"));
    SvReset(); g_svWritePath = 1;
    SaveGameHook(NULL, NULL, info, 0, 0);
    assert(g_st.writeHow == SW_GATE && g_st.gateIn >= 0 && g_st.write >= 20000 && g_st.write < g_st.spanEnd - g_st.infoEnd);
    assert(c.saveLastWriteUs == g_st.write && SaveWriteText(b, sizeof(b), g_st)[0] == '~' && strstr(b, " (gate span)"));
    SvReset(); g_svWritePath = 2;
    SaveGameHook(NULL, NULL, info, 0, 0);
    assert(g_st.writeHow == SW_INFO && g_st.gateIn < 0 && g_st.write >= 30000 && strstr(SaveWriteText(b, sizeof(b), g_st), " (info span)"));
    SaveGateEnter(GetCurrentThreadId());  // outside a save: ignored
    assert(g_st.gateIn < 0 && !g_saveTid);
    g_svReachBackup = false;  // fails before the next step: the final hook still ends the span
    SvReset();
    SaveGameHook(NULL, NULL, info, 0, 0);
    assert(g_st.writeHow == SW_INFO && g_st.backup < 0 && g_st.copy < 0 && g_st.final >= 0);
    assert(!strcmp(SavePhaseText(b, sizeof(b), g_st.backup), "not measured") && !strcmp(SavePhaseText(b, sizeof(b), 0), "0"));
    SaveTimes t = {};
    t.write = t.gateIn = t.spanEnd = t.infoEnd = -1;  // nothing reached after the info step
    SaveWriteResolve(t);
    assert(t.writeHow == SW_NONE && !strcmp(SaveWriteText(b, sizeof(b), t), "not measured"));
    g_svReachBackup = true; g_svWritePath = 0;
    assert(RemoveTree(g_sv));
}

// --- save flush (phase 10): steps 9 and 11 flushes recorded during the save, replayed by a worker after it ---
static void TestSaveFlush() {
    Ts3nCounters& c = ts3native_counters;
    // own page: a static buffer can end at a section edge, where Readable() over the 520-char path fails
    static u8* const info = (u8*)VirtualAlloc(NULL, 0x1000, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    swprintf_s((wchar_t*)(info + kInfoNhdPath), 520, L"%s\\Saves\\Foo.sims3\\World_0x1.nhd", g_sv);
    c.saveMode = 0;
    g_svName = L"Foo"; g_svTouch = false; g_svReachBackup = true;

    // 1. not ready (no [SaveFlush]): the game flushes during the save, as before.
    SvReset();
    g_svFlushes = 0;
    SaveGameHook(NULL, NULL, info, 0, 0);
    assert(g_svFlushes == 2 && c.flushDeferred == 0 && c.flushJobs == 0 && !g_fj.thread);

    // 2. mode 1: both flushes recorded, none by the game during the save; the worker flushes the 3 files twice.
    c.flushReady = 1; c.flushMode = 1;
    g_svFlushes = 0;
    SaveGameHook(NULL, NULL, info, 0, 0);
    assert(g_svFlushes == 0 && c.flushDeferred == 2 && c.flushJobs == 1 && g_fj.thread);
    WaitForSingleObject(g_fj.thread, INFINITE);
    assert(g_fj.files == 6 && g_fj.fails == 0 && c.flushFails == 0);

    // 3. the next save waits for the worker first (here already done: no wait counted), then defers again.
    SaveGameHook(NULL, NULL, info, 0, 0);
    assert(c.flushDeferred == 4 && c.flushJobs == 2 && c.flushWaits == 0);

    // 4. the load copy waits for the worker before it runs.
    g_svLoadCopies = 0;
    assert(LoadCopyHook(NULL, NULL, L"Foo", 1) == 1 && g_svLoadCopies == 1 && !g_fj.thread);

    // 5. a flushed folder that is gone by then counts as a fail; mode 0 lets the game flush again.
    SaveGameHook(NULL, NULL, info, 0, 0);
    WaitForSingleObject(g_fj.thread, INFINITE);
    assert(g_fj.fails == 0);
    g_fj.n = 1;
    swprintf_s(g_fj.path[0], L"%s\\gone", g_sv);
    FlushMain(NULL);
    assert(g_fj.fails == 1 && c.flushFails == 1);
    c.flushMode = 0;
    g_svFlushes = 0;
    SaveGameHook(NULL, NULL, info, 0, 0);
    assert(g_svFlushes == 2 && c.flushDeferred == 6 && !g_fj.thread);
    c.flushReady = 0; c.saveMode = 1;
    assert(RemoveTree(g_sv));
}

// --- save compress (phase 10): a fake package commit with a fake compressor; output identical with and without workers ---
static LONG g_fkSlotCalls;  // the game's slot ran (bound, miss or verify)
static u8 g_fkSalt;         // != 0: the workers' compressor gives other bytes (verify test)
typedef void*(__fastcall* FkAllocFn)(void*, void*, DWORD, const char*, DWORD);
typedef void(__fastcall* FkFreeFn)(void*, void*, void*, DWORD);
static int FkCompressImpl(u8* dst, const u8* src, DWORD len, void* alloc, DWORD mode, u8 salt) {
    void** vt = *(void***)alloc;  // takes its scratch tables like 0x4EC0A0 does, and writes them
    void* h = len > 0x4000 ? ((FkAllocFn)vt[2])(alloc, NULL, 0x40000, "h", 0) : NULL;
    void* t = ((FkAllocFn)vt[2])(alloc, NULL, ScWindowBytes(mode), "t", 0);
    assert(t && (len <= 0x4000 || h));
    memset(t, 0x5A, ScWindowBytes(mode));
    if (h) memset(h, 0xA5, 0x40000);
    const int n = 2 + (int)(len / 3) + (int)mode;
    if (dst) {
        dst[0] = (u8)(0x10 | mode); dst[1] = 0xFB;
        for (int i = 0; i < n - 2; i++) dst[2 + i] = (u8)(src[((DWORD)i * 3) % len] ^ (u8)i ^ salt);
    }
    if (h) ((FkFreeFn)vt[3])(alloc, NULL, h, 0);
    ((FkFreeFn)vt[3])(alloc, NULL, t, 0);
    return n;
}
static int __cdecl FkCompressWorker(u8* dst, DWORD, const u8* src, DWORD len, void* alloc, DWORD mode) {
    return FkCompressImpl(dst, src, len, alloc, mode, g_fkSalt);
}
static int __fastcall FkSlot(void* codec, void*, const u8* src, DWORD len, u8* dst, DWORD, DWORD flags) {  // as 0x4EC200
    InterlockedIncrement(&g_fkSlotCalls);
    if (!dst && (flags & 1)) return (int)((len * 20) >> 4) + 0x20;
    return FkCompressImpl(dst, src, len, ((FkCodec*)codec)->alloc, ScModeOf(flags), 0);
}
static void* __fastcall FkHeapAlloc(void*, void*, DWORD n, const char*, DWORD) { return HeapAlloc(GetProcessHeap(), 0, n); }
static void __fastcall FkHeapFree(void*, void*, void* p, DWORD) { HeapFree(GetProcessHeap(), 0, p); }
static void* g_fkHeapVt[4] = {NULL, NULL, (void*)FkHeapAlloc, (void*)FkHeapFree};
static void* g_fkHeap = g_fkHeapVt;

static DWORD g_fkRnd = 4711;
static DWORD FkRnd(DWORD n) { g_fkRnd = g_fkRnd * 1103515245 + 12345; return (g_fkRnd >> 8) % n; }
static const int kFkN = 240;
static u8* g_fkData[kFkN];
static DWORD g_fkLen[kFkN];
static u8* g_fkRef[kFkN];
static int g_fkRefLen[kFkN];
static u8* g_fkGot[kFkN];
static int g_fkGotLen[kFkN];

static void FkRun(int compression, u8** out, int* outLen) {
    FkPkg p;
    FkBuild(&p, g_fkData, g_fkLen, kFkN, compression);
    g_fkOut = out;
    g_fkOutLen = outLen;
    ScCommitHook(&p, NULL);
    assert(*(int*)(p.b + 0x2FC) == 0);
    FkFree(&p);
}
static void FkCheckSame() {
    for (int i = 0; i < kFkN; i++) {
        assert(g_fkGotLen[i] == g_fkRefLen[i] && !memcmp(g_fkGot[i], g_fkRef[i], (size_t)g_fkRefLen[i]));
        free(g_fkGot[i]);
    }
}

static void TestSaveCompress() {
    static_assert(offsetof(Ts3nCounters, compressMode) == 0x150 && offsetof(Ts3nCounters, flushMode) == 0x180 && offsetof(Ts3nCounters, heightMode) == 0x1B4 && sizeof(Ts3nCounters) >= 210 * 4 && offsetof(Ts3nCounters, mtReady) == 0x2A4 && offsetof(Ts3nCounters, mtMode) == 0x2A8 && offsetof(Ts3nCounters, cacheMode) == 0x280 && offsetof(Ts3nCounters, icLockReady) == 0x1DC && offsetof(Ts3nCounters, rgReady) == 0x204 && offsetof(Ts3nCounters, rgUnlocked) == 0x21C && offsetof(Ts3nCounters, rgJob) == 0x24C, "v18 layout");
    static_assert(offsetof(Ts3nCounters, lsReady) == 0x2C4 && offsetof(Ts3nCounters, lsMode) == 0x2C8 && offsetof(Ts3nCounters, pumpMaxUs) == 0x2F4 &&
                  offsetof(Ts3nCounters, lotScoreUs) == 0x304 && offsetof(Ts3nCounters, lotSetOn) == 0x310 && offsetof(Ts3nCounters, objSceneHist) == 0x330 &&
                  offsetof(Ts3nCounters, lsLogDropped) == 0x344, "v19 layout");
    Ts3nCounters& k = ts3native_counters;
    QueryPerformanceFrequency(&g_qpf);
    g_scCompress = FkCompressWorker; g_scSlotOrig = FkSlot; g_scCommitOrig = FkCommit;
    g_scLock = FkLock; g_scUnlock = FkUnlock;
    g_scThreads = 4;
    g_sb.maxBytes = 1 << 20;  // small window: the queue refills many times
    g_fkCodecVt[1] = (void*)ScSlotHook;
    g_fkCodec.vt = g_fkCodecVt;
    g_fkCodec.alloc = &g_fkHeap;
    int large = 0;
    for (int i = 0; i < kFkN; i++) {
        const DWORD kind = FkRnd(10);
        g_fkLen[i] = kind < 3 ? 0x32 + FkRnd(kScMinLen - 0x32) : kind < 9 ? kScMinLen + FkRnd(300000) : 1000000 + FkRnd(1500000);
        g_fkData[i] = (u8*)malloc(g_fkLen[i]);
        for (DWORD j = 0; j < g_fkLen[i]; j++) g_fkData[i][j] = (u8)FkRnd(256);
        large += g_fkLen[i] >= kScMinLen;
    }

    // 1. mode 0: the game's slot for every call (bound + compress per record); this is the reference.
    k.compressMode = 0;
    FkRun(1, g_fkRef, g_fkRefLen);
    assert(g_fkSlotCalls == 2 * kFkN && k.compressBatches == 0);

    // 2. mode 1: every large record served by a job, byte-identical; only bounds and small records reach the game's slot.
    k.compressMode = 1;
    g_fkSlotCalls = 0;
    FkRun(1, g_fkGot, g_fkGotLen);
    FkCheckSame();
    assert(k.compressBatches == 1 && k.compressJobs == large && k.compressHits == large && k.compressMisses == 0);
    assert(g_fkSlotCalls == 2 * kFkN - large && k.compressPeakMB <= 1 + 2 * 3);

    // 3. the commit uses another mode than predicted (+0x20 = 1 predicts mode 2; flags 2 = mode 1): all misses, same bytes.
    for (int i = 0; i < kFkN; i++) free(g_fkRef[i]);
    k.compressMode = 0;
    g_fkFlags = 2;
    FkRun(1, g_fkRef, g_fkRefLen);  // mode 1 reference
    k.compressMode = 1;
    FkRun(1, g_fkGot, g_fkGotLen);
    FkCheckSame();
    g_fkFlags = 0;
    assert(k.compressMisses == large && k.compressHits == large);

    // 4. +0x20 = 3 (flags 2, predicted right), with a size-only call before the real one (codec plugin path): both
    //    calls served by the same job.
    g_fkSizeFirst = true;
    FkRun(3, g_fkGot, g_fkGotLen);
    FkCheckSame();
    g_fkSizeFirst = false;
    assert(k.compressHits == 3 * large && k.compressMisses == large);

    // 5. verify: the workers' bytes differ; mismatches counted, the game's bytes used.
    g_fkSalt = 1;
    k.compressVerify = 1;
    FkRun(3, g_fkGot, g_fkGotLen);
    FkCheckSame();
    assert(k.compressMismatches == large);
    g_fkSalt = 0;
    k.compressVerify = 0;

    // 6. no compression on the package (+0x20 = 0): no batch; outside a batch the hook is the game's slot.
    const LONG batches = k.compressBatches;
    FkRun(0, g_fkGot, g_fkGotLen);
    for (int i = 0; i < kFkN; i++) free(g_fkGot[i]);
    g_fkSlotCalls = 0;
    u8 dst[64];
    assert(ScSlotHook(&g_fkCodec, NULL, g_fkData[0], 20, dst, 64, 2) == 2 + 20 / 3 + 1 && g_fkSlotCalls == 1 && k.compressBatches == batches);
    assert(k.compressDirectCalls > 0 && k.compressCommits > 0 && k.compressCommitPending == kFkN);

    // 7. compressed-bytes cache (docs/26 §8), outside a batch as every save record is today: the second pass over the same
    //    inputs is answered from the cache with the game's bytes and never reaches the game's slot.
    const DWORD kBuf = 3000000;
    u8* got = (u8*)malloc(kBuf);
    u8* ref = (u8*)malloc(kBuf);
    const int n7 = 40;
    g_ccMax = 64 << 20;
    k.cacheMode = 1;
    for (int pass = 0; pass < 2; pass++) {
        g_fkSlotCalls = 0;
        const LONG h0 = k.cacheHits, m0 = k.cacheMisses;
        for (int i = 0; i < n7; i++) {
            const int r = ScSlotHook(&g_fkCodec, NULL, g_fkData[i], g_fkLen[i], got, kBuf, 2);
            const int rr = FkCompressImpl(ref, g_fkData[i], g_fkLen[i], &g_fkHeap, 1, 0);
            assert(r == rr && !memcmp(got, ref, (size_t)r));
        }
        assert(pass ? g_fkSlotCalls == 0 && k.cacheHits - h0 == n7 && k.cacheMisses == m0 : g_fkSlotCalls == n7 && k.cacheMisses - m0 == n7);
    }
    assert(k.cacheEntries == n7 && k.cacheMismatches == 0 && k.cacheEvicted == 0);
    // same bytes, other mode (flags 0x10000 = mode 2): a miss, its own entry
    g_fkSlotCalls = 0;
    ScSlotHook(&g_fkCodec, NULL, g_fkData[0], g_fkLen[0], got, kBuf, 0x10000);
    assert(g_fkSlotCalls == 1 && k.cacheEntries == n7 + 1);
    // dst too small for the stored output: the game's slot (a miss), nothing stored twice
    ScSlotHook(&g_fkCodec, NULL, g_fkData[1], g_fkLen[1], got, 4, 2);
    assert(g_fkSlotCalls == 2 && k.cacheEntries == n7 + 1);
    // the same input with one word changed: a miss
    g_fkData[2][g_fkLen[2] / 2] ^= 1;
    ScSlotHook(&g_fkCodec, NULL, g_fkData[2], g_fkLen[2], got, kBuf, 2);
    g_fkData[2][g_fkLen[2] / 2] ^= 1;
    assert(g_fkSlotCalls == 3 && k.cacheEntries == n7 + 2);
    // verify: a corrupted entry is counted, dropped, and the game's bytes are returned
    CcEntry* e = *CcFind(CcHash(g_fkData[3], g_fkLen[3]), g_fkLen[3], 1);
    assert(e);
    ((u8*)(e + 1))[2] ^= 0xFF;
    k.compressVerify = 1;
    const int r7 = ScSlotHook(&g_fkCodec, NULL, g_fkData[3], g_fkLen[3], got, kBuf, 2);
    assert(r7 == FkCompressImpl(ref, g_fkData[3], g_fkLen[3], &g_fkHeap, 1, 0) && !memcmp(got, ref, (size_t)r7));
    assert(k.cacheMismatches == 1 && k.cacheEntries == n7 + 1 && g_fkSlotCalls == 4);
    ScSlotHook(&g_fkCodec, NULL, g_fkData[4], g_fkLen[4], got, kBuf, 2);  // a good entry under verify: a hit, no mismatch
    assert(k.cacheMismatches == 1 && g_fkSlotCalls == 5);
    k.compressVerify = 0;
    // switch off: the game's slot, no lookup
    k.cacheMode = 0;
    const LONG h7 = k.cacheHits;
    ScSlotHook(&g_fkCodec, NULL, g_fkData[4], g_fkLen[4], got, kBuf, 2);
    assert(g_fkSlotCalls == 6 && k.cacheHits == h7);
    k.cacheMode = 1;
    // a 4 MB budget over all records: least recently used entries go, the budget holds, the newest entry stays
    while (g_ccLru.next != &g_ccLru) CcDrop(g_ccLru.next);  // the budget is fixed at load in game; start empty here
    g_ccMax = 4 << 20;
    int last = -1;
    for (int i = 0; i < kFkN; i++) {
        ScSlotHook(&g_fkCodec, NULL, g_fkData[i], g_fkLen[i], got, kBuf, 2);
        assert(g_ccBytes <= g_ccMax);
        if (FkCompressImpl(NULL, g_fkData[i], g_fkLen[i], &g_fkHeap, 1, 0) <= (int)(g_ccMax / 4)) last = i;
    }
    assert(k.cacheEvicted > 0 && k.cacheKB <= 4096 && last >= 0);
    g_fkSlotCalls = 0;
    ScSlotHook(&g_fkCodec, NULL, g_fkData[last], g_fkLen[last], got, kBuf, 2);
    assert(g_fkSlotCalls == 0);
    while (g_ccLru.next != &g_ccLru) CcDrop(g_ccLru.next);
    assert(g_ccBytes == 0 && k.cacheEntries == 0);
    g_ccMax = 0;
    LARGE_INTEGER q0, q1;  // hash speed, for the cost per save (docs/26 §8.3)
    memset(got, 0x37, kBuf);
    unsigned __int64 hs = 0;
    QueryPerformanceCounter(&q0);
    for (int i = 0; i < 40; i++) hs += CcHash(got + (i & 3), kBuf - 4);
    QueryPerformanceCounter(&q1);
    printf("save compress cache: hash %.0f MB/s (%llx)\n", 40.0 * kBuf / 1048576 / ((double)(q1.QuadPart - q0.QuadPart) / g_qpf.QuadPart), hs & 0xF);
    free(got), free(ref);
    for (int i = 0; i < kFkN; i++) free(g_fkRef[i]), free(g_fkData[i]);
}

// --- decompress (phase 6): random valid RefPack streams with every command type, then corrupted ones ---
static DWORD g_rnd = 12345;
static DWORD Rnd(DWORD n) { g_rnd = g_rnd * 1103515245 + 12345; return (g_rnd >> 8) % n; }

// Writes a stream that decodes to `size` bytes (also written to `plain`); returns the stream length.
static DWORD GenRefpack(u8* out, u8* plain, DWORD size) {
    u8* o = out;
    *o++ = 0x10; *o++ = 0xFB;
    *o++ = (u8)(size >> 16); *o++ = (u8)(size >> 8); *o++ = (u8)size;
    DWORD done = 0;
    const DWORD alpha = 2 + Rnd(200);  // small alphabets give long matches
    for (;;) {
        const DWORD left = size - done;
        if (left < 4 && Rnd(2)) {  // stop code with the 0-3 tail bytes
            *o++ = (u8)(0xFC | left);
            for (DWORD i = 0; i < left; i++) *o++ = plain[done++] = (u8)Rnd(alpha);
            return (DWORD)(o - out);
        }
        const DWORD lit = Rnd(4) < left ? Rnd(4) : 0, kind = Rnd(4);
        const DWORD avail = done + lit;  // history the match may reach back into
        DWORD n = 0, off = 0, maxN = 0, maxOff = 0;
        if (kind == 0) { maxN = 10; maxOff = 1024; n = 3 + Rnd(8); }
        if (kind == 1) { maxN = 67; maxOff = 16384; n = 4 + Rnd(64); }
        if (kind == 2) { maxN = 1028; maxOff = 131072; n = 5 + Rnd(Rnd(2) ? 1024 : 40); }
        if (maxN && avail && n <= maxN && lit + n <= left) {
            off = 1 + Rnd(avail < maxOff ? avail : maxOff);
            if (Rnd(3) == 0) off = 1 + Rnd(avail < 20 ? avail : 20);  // overlapping copies
            const DWORD o1 = off - 1;
            if (kind == 0) { *o++ = (u8)(((o1 >> 3) & 0x60) | ((n - 3) << 2) | lit); *o++ = (u8)o1; }
            if (kind == 1) { *o++ = (u8)(0x80 | (n - 4)); *o++ = (u8)((lit << 6) | (o1 >> 8)); *o++ = (u8)o1; }
            if (kind == 2) { *o++ = (u8)(0xC0 | (((o1 >> 16) & 1) << 4) | (((n - 5) >> 8) << 2) | lit); *o++ = (u8)(o1 >> 8); *o++ = (u8)o1; *o++ = (u8)(n - 5); }
            for (DWORD i = 0; i < lit; i++) *o++ = plain[done++] = (u8)Rnd(alpha);
            for (DWORD i = 0; i < n; i++, done++) plain[done] = plain[done - off];
        } else if (left >= 4) {
            DWORD k = 1 + Rnd(28);
            if (k * 4 > left) k = left / 4;
            *o++ = (u8)(0xE0 | (k - 1));
            for (DWORD i = 0; i < k * 4; i++) *o++ = plain[done++] = (u8)Rnd(alpha);
        }
    }
}

static int __cdecl FakeCore(u8*, DWORD, const u8*, DWORD) { return -2; }

static void TestDecompress() {
    const DWORD kMax = 300000, kGuard = 64;
    u8* plain = (u8*)malloc(kMax);
    u8* stream = (u8*)malloc(kMax * 2 + 64);
    u8* a = (u8*)malloc(kMax + kGuard);
    u8* b = (u8*)malloc(kMax + kGuard);
    u8* c = (u8*)malloc(kMax + kGuard);
    for (int iter = 0; iter < 3000; iter++) {
        const DWORD size = iter < 50 ? (DWORD)iter : (Rnd(4) ? Rnd(5000) : Rnd(kMax));
        DWORD len = GenRefpack(stream, plain, size);
        memset(a, 0xAA, kMax + kGuard);
        memset(b, 0xAA, kMax + kGuard);
        assert(RefpackRef(a, size, stream, len) == (int)size && memcmp(a, plain, size) == 0);
        assert(RefpackFast(b, size, stream, len) == (int)size && memcmp(b, plain, size) == 0);
        assert(b[size] == 0xAA);
        // Corrupt: flip bytes, cut the stream, shrink or grow the cap. Same result; same bytes on success; nothing past cap.
        for (int k = 0; k < 4; k++) {
            DWORD clen = len;
            const DWORD flips = Rnd(4);
            for (DWORD f = 0; f < flips && len > 2; f++) stream[2 + Rnd(len - 2)] ^= (u8)(1 + Rnd(255));
            if (Rnd(3) == 0) clen = Rnd(len + 1);
            DWORD cap = Rnd(3) == 0 ? Rnd(size + 1) : Rnd(2) ? size : (size + Rnd(64) < kMax ? size + Rnd(64) : size);
            memset(a, 0xAA, kMax + kGuard);
            memset(b, 0xAA, kMax + kGuard);
            memset(c, 0x55, kMax + kGuard);
            const int ra = RefpackRef(a, cap, stream, clen), rb = RefpackFast(b, cap, stream, clen);
            assert(ra == rb && RefpackRef(c, cap, stream, clen) == ra);
            // Every byte the game's decoder wrote (same in a and c despite different fills) is the same in b. Bytes it
            // left alone may hold scratch from a wild copy, only when the stream ends short of its header size.
            if (ra > 0)
                for (DWORD i = 0; i < cap; i++) assert(a[i] != c[i] || b[i] == a[i]);
            for (DWORD g = cap; g < cap + kGuard; g++) assert(b[g] == 0xAA);
            len = GenRefpack(stream, plain, size);  // a fresh valid stream for the next round
        }
    }
    free(plain); free(stream); free(a); free(b); free(c);

    // The hook site: `call core` rel32 is retargeted only when the 5 bytes match.
    u8* site = (u8*)VirtualAlloc(NULL, 64, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
    const DWORD at = (DWORD)(uintptr_t)site, core = (DWORD)(uintptr_t)FakeCore;
    site[0] = 0xE8;
    *(LONG*)(site + 1) = (LONG)(core - (at + 5));
    assert(!PatchRefpackCall(at, core + 1, (void*)RefpackHook));
    assert(*(LONG*)(site + 1) == (LONG)(core - (at + 5)));
    assert(PatchRefpackCall(at, core, (void*)RefpackHook));
    assert(at + 5 + *(LONG*)(site + 1) == (DWORD)(uintptr_t)RefpackHook);
    // Hook: mode 0 runs the original, mode 1 the fast decoder, verify keeps the original's result on a mismatch.
    g_refpackOrig = FakeCore;
    QueryPerformanceFrequency(&g_dqpf);
    g_dstat.mainTid = (LONG)GetCurrentThreadId();
    u8 src[] = {0x10, 0xFB, 0x00, 0x00, 0x04, 0xFC | 0, 0};
    u8 hello[] = {0x10, 0xFB, 0x00, 0x00, 0x04, 0xE0, 'a', 'b', 'c', 'd', 0xFC};
    u8 out[8];
    ts3native_counters.decompressMode = 0;
    assert(RefpackHook(out, 4, src, 6) == -2);
    ts3native_counters.decompressMode = 1;
    assert(RefpackHook(out, 4, hello, sizeof(hello)) == 4 && memcmp(out, "abcd", 4) == 0);
    ts3native_counters.decompressVerify = 1;
    assert(RefpackHook(out, 4, hello, sizeof(hello)) == -2 && ts3native_counters.decompressMismatches == 1);
    g_refpackOrig = RefpackRef;
    assert(RefpackHook(out, 4, hello, sizeof(hello)) == 4 && ts3native_counters.decompressMismatches == 1);
    assert(ts3native_counters.decompressCalls == 4 && ts3native_counters.decompressMainCalls == 4 && g_dstat.tid[0] == (LONG)GetCurrentThreadId());
    ts3native_counters.decompressVerify = 0;
}

// --- EA allocator cache (phase 8): a fake heap that lays chunks out like the game's (16-byte header, in-use bit in the next chunk) ---
static __declspec(align(16)) u8 g_fakeArena[1 << 18];
static DWORD g_fakeTop = 0x10;
static int g_fakeMallocs, g_fakeFrees, g_fakeOom;
static void* __fastcall FakeEaMalloc(void*, void*, DWORD n, DWORD) {
    g_fakeMallocs++;
    if (g_fakeOom) { g_fakeOom--; return NULL; }
    const DWORD sz = n >= 0x1000 ? 0x1000 : AcChunk(n);
    if (g_fakeTop + sz + 0x20 > sizeof(g_fakeArena)) g_fakeTop = 0x10;
    u8* c = g_fakeArena + g_fakeTop;
    g_fakeTop += sz;
    *(DWORD*)(c + 4) = sz | 1;
    *(DWORD*)(c + sz + 4) = 1;  // next chunk: previous (this one) in use
    return c + 0x10;
}
static int g_fakeAligns;
static void* __fastcall FakeEaMemalign(void* h, void* e, DWORD n, DWORD, DWORD, DWORD f) { g_fakeAligns++; return FakeEaMalloc(h, e, n, f); }
static void __fastcall FakeEaFree(void*, void*, void*) { g_fakeFrees++; }
static DWORD __fastcall FakeEaShutdown(void*, void*) { return 1; }
static CRITICAL_SECTION g_fakeCs;
static void* __fastcall LockedFakeMalloc(void* h, void* e, DWORD n, DWORD f) { EnterCriticalSection(&g_fakeCs); void* r = FakeEaMalloc(h, e, n, f); LeaveCriticalSection(&g_fakeCs); return r; }
static void __fastcall LockedFakeFree(void* h, void* e, void* p) { EnterCriticalSection(&g_fakeCs); FakeEaFree(h, e, p); LeaveCriticalSection(&g_fakeCs); }
static void* const kHeapA = (void*)0x1000;
static void* const kHeapB = (void*)0x2000;
static DWORD WINAPI AcFreeThree(void*) {
    void* p[3];
    for (int i = 0; i < 3; i++) p[i] = EaMallocHook(kHeapA, NULL, 100, 0);
    for (int i = 0; i < 3; i++) EaFreeHook(kHeapA, NULL, p[i]);
    return 0;
}

static void TestAlloc() {
    Ts3nCounters& c = ts3native_counters;
    g_eaMallocOrig = FakeEaMalloc;
    g_eaMemalignOrig = FakeEaMemalign;
    g_eaFreeOrig = FakeEaFree;
    g_eaShutdownOrig = FakeEaShutdown;
    assert(AcInit(16, 1, 4));
    c.allocMode = 1;
    assert(AcChunk(0) == 16 && AcChunk(4) == 16 && AcChunk(5) == 32 && AcChunk(40) == 64 && AcChunk(36) == 48 && AcChunk(1000) == 1024);

    // free then malloc of the same chunk size: same pointer, no heap call; header and user bytes untouched
    void* p = EaMallocHook(kHeapA, NULL, 40, 0);
    assert(g_fakeMallocs == 1 && ((uintptr_t)p & 15) == 0);
    memset(p, 0xAB, 40);
    const DWORD hdr = ((DWORD*)p)[-3];
    EaFreeHook(kHeapA, NULL, p);
    assert(g_fakeFrees == 0 && ((u8*)p)[0] == 0xAB && ((u8*)p)[39] == 0xAB && ((DWORD*)p)[-3] == hdr);
    assert(EaMallocHook(kHeapA, NULL, 36, 0) != p && g_fakeMallocs == 2);  // 48-byte chunk: another bin
    assert(EaMallocHook(kHeapB, NULL, 40, 0) != p && g_fakeMallocs == 3);  // other heap
    assert(EaMallocHook(kHeapA, NULL, 37, 8) != p && g_fakeMallocs == 4);  // flag 8: direct VirtualAlloc path
    assert(EaMallocHook(kHeapA, NULL, 37, 0) == p && g_fakeMallocs == 4);  // 37 -> 64 too

    // memalign: only align <= 16 with offset 0 (0x4E63F0's plain 0x4E5E40 path) may come from the cache
    EaFreeHook(kHeapA, NULL, p);
    assert(EaMemalignHook(kHeapA, NULL, 40, 32, 0, 0) != p && g_fakeAligns == 1);
    assert(EaMemalignHook(kHeapA, NULL, 40, 4, 16, 0) != p && g_fakeAligns == 2);  // ZoneObject: align 4, offset 16
    assert(EaMemalignHook(kHeapA, NULL, 40, 16, 0, 0) == p && g_fakeAligns == 2);

    // not cached: direct-mapped, fastbin-tagged, too big, NULL, next chunk says free
    u8* q = (u8*)EaMallocHook(kHeapA, NULL, 100, 0);
    const DWORD qh = ((DWORD*)q)[-3];
    ((DWORD*)q)[-3] = qh | 2;          EaFreeHook(kHeapA, NULL, q); assert(g_fakeFrees == 1);
    ((DWORD*)q)[-3] = qh | 0x80000004; EaFreeHook(kHeapA, NULL, q); assert(g_fakeFrees == 2);
    ((DWORD*)q)[-3] = qh;
    DWORD* next = (DWORD*)(q - 0x10 + (qh & ~15u) + 4);
    *next &= ~1u;                      EaFreeHook(kHeapA, NULL, q); assert(g_fakeFrees == 3);
    *next |= 1;
    EaFreeHook(kHeapA, NULL, EaMallocHook(kHeapA, NULL, 1013, 0)); assert(g_fakeFrees == 4);  // 1040-byte chunk
    EaFreeHook(kHeapA, NULL, NULL);    assert(g_fakeFrees == 5);

    // per-bin cap 4, then the 16 KB thread cap
    void* v[24];
    for (int i = 0; i < 5; i++) v[i] = EaMallocHook(kHeapA, NULL, 200, 0);
    for (int i = 0; i < 5; i++) EaFreeHook(kHeapA, NULL, v[i]);
    assert(g_fakeFrees == 6);
    AcThread* t = (AcThread*)FlsGetValue(g_acFls);
    assert(t && t->bytes == 4 * 224);
    for (int i = 0; i < 24; i++) v[i] = EaMallocHook(kHeapA, NULL, 1000 - 16 * (i % 6), 0);
    for (int i = 0; i < 24; i++) EaFreeHook(kHeapA, NULL, v[i]);
    assert(t->bytes <= 16 * 1024 && g_fakeFrees > 6);

    // mode 0: straight through
    c.allocMode = 0;
    int m = g_fakeMallocs, f = g_fakeFrees;
    void* z = EaMallocHook(kHeapA, NULL, 200, 0);
    EaFreeHook(kHeapA, NULL, z);
    assert(g_fakeMallocs == m + 1 && g_fakeFrees == f + 1);
    c.allocMode = 1;

    // docs/67 double free on one thread: with Dedupe the second Free is dropped (counted), the bin keeps one link, and two
    // Mallocs of that size get two different chunks (without it they got the same chunk twice)
    {
        c.allocDedupe = 1;
        const LONG d0 = c.allocDupFrees;
        void* a = EaMallocHook(kHeapA, NULL, 300, 0);
        const int f0 = g_fakeFrees;
        EaFreeHook(kHeapA, NULL, a);
        EaFreeHook(kHeapA, NULL, a);
        assert(c.allocDupFrees == d0 + 1 && g_fakeFrees == f0);
        void* x = EaMallocHook(kHeapA, NULL, 300, 0);
        void* y = EaMallocHook(kHeapA, NULL, 300, 0);
        assert(x == a && y != a);
        EaFreeHook(kHeapA, NULL, x);
        EaFreeHook(kHeapA, NULL, y);
        EaFreeHook(kHeapA, NULL, x);  // dup behind another link: found by the walk
        assert(c.allocDupFrees == d0 + 2);
        // switch off: still counted, old behaviour (pushed again)
        c.allocDedupe = 0;
        void* u = EaMallocHook(kHeapA, NULL, 300, 0);
        EaFreeHook(kHeapA, NULL, u);
        EaFreeHook(kHeapA, NULL, u);
        assert(c.allocDupFrees == d0 + 3);
        // count 0 with a head (what the old double free left behind): AcPop treats the bin as empty
        AcThread* tt = (AcThread*)FlsGetValue(g_acFls);
        AcHeap* hh = AcFind(tt, kHeapA, false);
        AcBin* bb = &hh->bins[AcChunk(300) / 16 - 1];
        const DWORD keep = bb->count;
        bb->count = 0;
        const int mm = g_fakeMallocs;
        assert(EaMallocHook(kHeapA, NULL, 300, 0) != u && g_fakeMallocs == mm + 1);
        tt->bytes -= keep * AcChunk(300);  // drop the looped bin before later tests
        bb->head = NULL;
    }

    // heap A shuts down: its chunks are forgotten (not freed), heap B's go back to B
    void* b1 = EaMallocHook(kHeapB, NULL, 60, 0);
    EaFreeHook(kHeapB, NULL, b1);
    f = g_fakeFrees;
    const LONG dropped = c.allocDropped, flushed = c.allocFlushed;
    EaShutdownHook(kHeapA, NULL);
    assert(g_fakeFrees == f + 1 && c.allocFlushed == flushed + 1 && c.allocDropped > dropped && t->bytes == 0 && c.allocShutdowns == 1);

    // out of memory: this thread's chunks of the heap go back, then one retry
    b1 = EaMallocHook(kHeapB, NULL, 60, 0);
    EaFreeHook(kHeapB, NULL, b1);
    f = g_fakeFrees;
    m = g_fakeMallocs;
    g_fakeOom = 1;
    assert(EaMallocHook(kHeapB, NULL, 500, 0) != NULL);
    assert(g_fakeMallocs == m + 2 && g_fakeFrees == f + 1 && c.allocOomRetries == 1);

    // thread exit returns its chunks and its quota
    f = g_fakeFrees;
    const LONG threads = g_acThreads;
    HANDLE th = CreateThread(NULL, 0, AcFreeThree, NULL, 0, NULL);
    WaitForSingleObject(th, INFINITE);
    CloseHandle(th);
    assert(g_fakeFrees == f + 3 && g_acThreads == threads);

    // quota: 1 MB / 16 KB = 64 threads; the 65th runs uncached
    g_acThreadsMax = g_acThreads;
    f = g_fakeFrees;
    th = CreateThread(NULL, 0, AcFreeThree, NULL, 0, NULL);
    WaitForSingleObject(th, INFINITE);
    CloseHandle(th);
    assert(g_fakeFrees == f + 3 && g_acThreads == threads);
    g_acThreadsMax = 64;

    // benchmark: one free + malloc pair through the cache vs the fake heap behind a CRITICAL_SECTION (uncontended)
    InitializeCriticalSectionAndSpinCount(&g_fakeCs, 10);
    LARGE_INTEGER t0, t1, t2, fq;
    QueryPerformanceFrequency(&fq);
    const int N = 2000000;
    void* r = EaMallocHook(kHeapB, NULL, 100, 0);
    QueryPerformanceCounter(&t0);
    for (int i = 0; i < N; i++) { EaFreeHook(kHeapB, NULL, r); r = EaMallocHook(kHeapB, NULL, 100, 0); }
    QueryPerformanceCounter(&t1);
    g_fakeTop = 0x10;
    for (int i = 0; i < N; i++) { LockedFakeFree(kHeapB, NULL, r); r = LockedFakeMalloc(kHeapB, NULL, 100, 0); if (g_fakeTop > 0x30000) g_fakeTop = 0x10; }
    QueryPerformanceCounter(&t2);
    printf("alloc cache: %.1f ns per free+malloc hit, %.1f ns for lock + bump allocator\n", (t1.QuadPart - t0.QuadPart) * 1e9 / fq.QuadPart / N,
           (t2.QuadPart - t1.QuadPart) * 1e9 / fq.QuadPart / N);
}

// --- water jps (phase 9): hand-checked jumps on tiny grids; waterjps-test.exe compares against the game's code ---
static DWORD __cdecl FakeJump(const u8* const*, const u8* const*, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD, void*) { return 1234; }

static void TestWaterJps() {
    Ts3nCounters& k = ts3native_counters;
    static u8 g1[9], g2[9];
    const u8* p1 = g1;
    const u8* p2 = g2;
    k.waterJpsMode = 1;
    auto J = [&](DWORD w, DWORD h, DWORD goal, DWORD cell, DWORD dir, DWORD flag) { return WaterJumpHook(&p1, &p2, w, h, goal, cell, dir, flag, NULL); };
    memset(g1, 1, 9);
    assert(J(6, 1, 0xFFFFFFFF, 0, 2, 0) == 0xFFFFFFFF);  // open row east: no jump point before the edge
    assert(J(6, 1, 4, 0, 2, 0) == 4);                    // the goal
    g2[3] = 1;
    assert(J(6, 1, 0xFFFFFFFF, 0, 2, 0) == 2);  // next to a cost-layer cell
    g2[3] = 0;
    g1[0] = 0;
    assert(J(3, 1, 0xFFFFFFFF, 0, 2, 1) == 1);  // start on land: one step out
    g1[0] = 1;
    assert(J(3, 1, 0xFFFFFFFF, 0, 2, 1) == 0xFFFFFFFF);
    // 3x3 sea, diagonal from (0,0): the east scan off (1,1) wraps past the row end (the game's index step) to (0,2), (1,2),
    // (2,2), so it finds either goal and (1,1) is the jump point
    assert(J(3, 3, 7, 0, 3, 0) == 4 && J(3, 3, 8, 0, 3, 0) == 4);
    assert(J(3, 3, 0xFFFFFFFF, 0, 3, 0) == 0xFFFFFFFF);
    // mode 0 and the odd cases go to the game's jump
    g_wjOrig = FakeJump;
    assert(J(3, 3, 7, 0, 0xFF, 0) == 1234 && k.waterJpsFallbacks == 1);
    k.waterJpsMode = 0;
    assert(J(3, 3, 7, 0, 3, 0) == 1234 && k.waterJpsFallbacks == 1);
    g_wjOrig = (WjJumpFn)(uintptr_t)WJ_JUMP;
    k.waterJpsFallbacks = 0;
}

static int g_fakeHeightCalls;
static double g_fakeHeight = 12.5;
static double __fastcall FakeHeight(void*, void*, float x, float z) { g_fakeHeightCalls++; return g_fakeHeight + x - z; }

static int g_fakeRebuilds;
static DWORD __fastcall FakeRebuild(void*, void*, int, int, void*, void*, void*) { g_fakeRebuilds++; return 0; }

static void TestHeightCache() {
    static u8 grid[0xD4];
    *(DWORD*)(grid + 0xCC) = 4;  // 4 x 4 cells of 256
    *(DWORD*)(grid + 0xD0) = 4;
    g_hc = (HcEntry*)calloc(1024, sizeof(HcEntry));
    g_hcMask = 511;
    g_hcOrig = FakeHeight;
    g_hcRebuildOrig = FakeRebuild;
    Ts3nCounters& s = ts3native_counters;
    s.heightMode = 0;
    assert(HcHook(grid, NULL, 3.f, 1.f) == 14.5 && g_fakeHeightCalls == 1 && s.heightCalls == 0);  // mode 0: the game's raycast
    s.heightMode = 1;
    assert(HcHook(grid, NULL, 3.f, 1.f) == 14.5 && g_fakeHeightCalls == 2 && s.heightMisses == 1);
    assert(HcHook(grid, NULL, 3.f, 1.f) == 14.5 && g_fakeHeightCalls == 2 && s.heightHits == 1);   // hit: no raycast
    assert(HcHook(grid, NULL, 3.f, 2.f) == 13.5 && g_fakeHeightCalls == 3);                  // other point
    *(DWORD*)(grid + 0x34) = 7;  // ray start changed
    assert(HcHook(grid, NULL, 3.f, 1.f) == 14.5 && g_fakeHeightCalls == 4);
    g_hcGen++;                   // cell rebuild
    g_fakeHeight = 20;
    assert(HcHook(grid, NULL, 3.f, 1.f) == 22 && g_fakeHeightCalls == 5);
    g_fakeHeight = 30;           // terrain changed without a rebuild: Verify returns the real height and counts it
    g_hcVerify = true;
    assert(HcHook(grid, NULL, 3.f, 1.f) == 32 && g_fakeHeightCalls == 6 && s.heightMismatches == 1);
    g_hcVerify = false;
    g_fakeHeight = 12.5;
    g_hcGen++;

    // per-cell invalidation: rebuilding cell (1, 0) keeps cell (0, 0); boundary x = 256 belongs to cell 1 (floor >> 8)
    int c = g_fakeHeightCalls;
    HcHook(grid, NULL, 3.f, 1.f); HcHook(grid, NULL, 255.9f, 1.f); HcHook(grid, NULL, 256.f, 1.f); HcHook(grid, NULL, 300.f, 1.f);
    c = g_fakeHeightCalls;
    HcHook(grid, NULL, 3.f, 1.f); HcHook(grid, NULL, 255.9f, 1.f); HcHook(grid, NULL, 256.f, 1.f); HcHook(grid, NULL, 300.f, 1.f);
    assert(g_fakeHeightCalls == c);  // all four cached
    const LONG rb = s.heightRebuilds;
    HcRebuildHook(grid, NULL, 256, 0, NULL, NULL, NULL);
    assert(g_fakeRebuilds == 1 && s.heightRebuilds == rb + 1);
    assert(HcHook(grid, NULL, 3.f, 1.f) == 14.5 && HcHook(grid, NULL, 255.9f, 1.f) == 12.5 + 255.9f - 1.f && g_fakeHeightCalls == c);
    assert(HcHook(grid, NULL, 256.f, 1.f) == 267.5 && g_fakeHeightCalls == c + 1);  // cell 1 rebuilt: raycast again
    assert(HcHook(grid, NULL, 300.f, 1.f) == 311.5 && g_fakeHeightCalls == c + 2);
    // a rebuild outside the grid bumps the global generation: everything is dropped
    HcRebuildHook(grid, NULL, 4 * 256, 0, NULL, NULL, NULL);
    assert(HcHook(grid, NULL, 3.f, 1.f) == 14.5 && g_fakeHeightCalls == c + 3);
    // points outside the grid (incl. negative, NaN-free) are never cached
    HcHook(grid, NULL, -0.5f, 1.f);
    assert(HcHook(grid, NULL, -0.5f, 1.f) == 11 && g_fakeHeightCalls == c + 5);
    assert(HcHook(grid, NULL, 3.f, 1100.f) == 12.5 + 3 - 1100 && g_fakeHeightCalls == c + 6);
    assert(HcFloor(-0.5f) == -1 && HcFloor(-1.f) == -1 && HcFloor(255.99f) == 255 && HcFloor(256.f) == 256 && HcFloor(-0.f) == 0);

    // 2 ways: two keys of one set both stay; a third evicts the one used least recently
    const DWORD gk = (DWORD)(uintptr_t)grid, x3 = 0x40400000;  // 3.f
    HcEntry* set0 = HcSet(gk, x3, 0x3F800000);                // (3, 1)
    float zs[2]; int found = 0;
    for (float z = 2.f; found < 2 && z < 1000.f; z += 0.125f) if (HcSet(gk, x3, *(DWORD*)&z) == set0) zs[found++] = z;
    assert(found == 2);
    HcHook(grid, NULL, 3.f, zs[0]);  // set: (3, 1) and zs[0]
    c = g_fakeHeightCalls;
    assert(HcHook(grid, NULL, 3.f, 1.f) == 14.5 && HcHook(grid, NULL, 3.f, zs[0]) == 15.5 - zs[0] && g_fakeHeightCalls == c);
    HcHook(grid, NULL, 3.f, 1.f);    // (3, 1) used last
    HcHook(grid, NULL, 3.f, zs[1]);  // evicts zs[0]
    assert(g_fakeHeightCalls == c + 1);
    HcHook(grid, NULL, 3.f, 1.f);
    assert(g_fakeHeightCalls == c + 1);
    HcHook(grid, NULL, 3.f, zs[0]);
    assert(g_fakeHeightCalls == c + 2);
    s.heightMode = 0;
    free(g_hc);
    g_hc = NULL;
}

// --- [TerrainGrave] (H6 S3a): the game's PhysShape_Release bytes under our detour, called from a fake swap site ---
static int g_tgDtors;
static void* g_tgLastDtor;
static void __fastcall TgFakeDtor(void* self, void*, int flag) { g_tgDtors++; g_tgLastDtor = self; assert(flag == 1); }
static void* g_tgFakeVtbl[1] = {(void*)TgFakeDtor};
struct TgShape { void** vtbl; u8 pad[0x48]; LONG count; };  // count at +0x4C
static_assert(offsetof(TgShape, count) == 0x4C, "shape layout");
static volatile LONG g_tgSeenInFlight;
static double __fastcall TgFakeRay(void*, void*, float, float) { g_tgSeenInFlight = g_tgInFlight; return 1.0; }

static void TestTerrainGrave() {
    Ts3nCounters& k = ts3native_counters;
    // PhysShape_Release 0x682760 as in the exe [verified disassembly]: add [ecx+4Ch],-1; push esi; mov esi,[ecx+4Ch];
    // jne +8; mov eax,[ecx]; mov edx,[eax]; push 1; call edx; mov eax,esi; pop esi; ret
    static const u8 kRelease[] = {0x83, 0x41, 0x4C, 0xFF, 0x56, 0x8B, 0x71, 0x4C, 0x75, 0x08, 0x8B, 0x01, 0x8B, 0x10, 0x6A, 0x01,
                                  0xFF, 0xD2, 0x8B, 0xC6, 0x5E, 0xC3};
    u8* code = (u8*)VirtualAlloc(NULL, 128, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
    u8* rel = code;
    memcpy(rel, kRelease, sizeof(kRelease));
    // the swap site: ecx = [esp+4]; call rel; ret  (the return address after the call stands for 0xC2A896)
    u8* swap = code + 64;
    swap[0] = 0x8B; swap[1] = 0x4C; swap[2] = 0x24; swap[3] = 0x04;
    swap[4] = 0xE8;
    *(LONG*)(swap + 5) = (LONG)(rel - (swap + 9));
    swap[9] = 0xC3;
    typedef void(__cdecl* SwapFn)(void* shape);
    typedef LONG(__fastcall* RelFn)(void* shape, void*);
    const SwapFn Swap = (SwapFn)(void*)swap;
    const RelFn Release = (RelFn)(void*)rel;
    g_tgSwapRet = (DWORD)(uintptr_t)(swap + 9);
    g_tgTramp = InstallDetour("graveyard release", (DWORD)(uintptr_t)rel, kTgPrologue, sizeof(kTgPrologue), (void*)TgStub, &g_tgTramp);
    assert(g_tgTramp);
    static TgShape shapes[70];
    for (TgShape& s : shapes) { s.vtbl = g_tgFakeVtbl; s.count = 1; }

    // other callers and the shared count: the game's release (count 2 -> 1, returns the new count, no destructor)
    shapes[0].count = 2;
    assert(Release(&shapes[0], NULL) == 1 && g_tgDtors == 0);
    // switch off: the swap frees at once; with a ray in flight that free is a race
    k.tgMode = 0;
    Swap(&shapes[0]);
    assert(shapes[0].count == 0 && g_tgDtors == 1 && g_tgLastDtor == &shapes[0] && k.vfRtGraveRaces == 0);
    g_tgInFlight = 1;
    Swap(&shapes[1]);
    assert(g_tgDtors == 2 && k.vfRtGraveRaces == 1);
    shapes[2].count = 2;  // shared: not freed, not a race
    Swap(&shapes[2]);
    assert(g_tgDtors == 2 && shapes[2].count == 1 && k.vfRtGraveRaces == 1);
    // switch on: parked (count untouched, no destructor) until a rebuild entry sees no ray in flight
    k.tgMode = 1;
    Swap(&shapes[3]);
    assert(shapes[3].count == 1 && g_tgDtors == 2 && k.vfRtGraveParked == 1 && k.vfRtGraveHeld == 1 && k.vfRtGraveRaces == 1);
    assert(Release(&shapes[4], NULL) == 0 && g_tgDtors == 3);  // not the swap: the game's release even when on
    TgOnSwap();  // a ray is in flight: nothing released
    assert(g_tgDtors == 3 && k.vfRtGraveHeld == 1 && k.vfSwaps == 1 && k.vfSwapsInRay == 1);
    g_tgInFlight = 0;
    TgOnSwap();
    assert(g_tgDtors == 4 && g_tgLastDtor == &shapes[3] && shapes[3].count == 0 && k.vfRtGraveFreed == 1 && k.vfRtGraveHeld == 0 &&
           k.vfSwaps == 2 && k.vfSwapsInRay == 1);
    // ring full: the 65th park waits for no ray, drains the 64, then parks itself
    for (int i = 5; i < 69; i++) Swap(&shapes[i]);
    assert(k.vfRtGraveHeld == 64 && g_tgDtors == 4);
    Swap(&shapes[69]);
    assert(k.vfRtGraveSpins == 1 && g_tgDtors == 68 && k.vfRtGraveFreed == 65 && k.vfRtGraveHeld == 1 && k.vfRtGraveParked == 66);
    TgOnSwap();
    assert(g_tgDtors == 69 && g_tgLastDtor == &shapes[69] && k.vfRtGraveHeld == 0);
    // switching off with nothing parked: back to the game's release
    k.tgMode = 0;
    shapes[0].count = 1;
    Swap(&shapes[0]);
    assert(g_tgDtors == 70 && k.vfRtGraveParked == 66);

    // the ray counts itself in flight only while the graveyard is installed
    const HcFn savedRay = g_hcOrig;
    g_hcOrig = TgFakeRay;
    g_tgOn = false;
    HcOrig(NULL, NULL, 0, 0);
    assert(g_tgSeenInFlight == 0);
    g_tgOn = true;
    assert(HcOrig(NULL, NULL, 0, 0) == 1.0 && g_tgSeenInFlight == 1 && g_tgInFlight == 0);
    k.heightMode = 1;  // no table (graveyard alone): the switch cannot reach the cache
    assert(HcHook(NULL, NULL, 0, 0) == 1.0 && g_tgSeenInFlight == 1);
    k.heightMode = 0;
    g_tgOn = false;
    g_hcOrig = savedRay;

    // Verify's release probe is counted inside this stub
    assert(VfOwnerSlot(TG_RELEASE) == &g_tgVf);
    g_tgTramp = NULL;  // the detour stays on the test code, which nothing calls again
    assert(!VfOwnerSlot(TG_RELEASE));
    memset(&k.tgReady, 0, offsetof(Ts3nCounters, vfRtGraveHeld) + 4 - offsetof(Ts3nCounters, tgReady));
    k.vfSwaps = k.vfSwapsInRay = 0;
}

// --- loader lock check: counting on a local section, contention from a second thread ---
static HANDLE g_ilHeld;
static DWORD WINAPI IlHolder(void*) {
    IlLock();
    SetEvent(g_ilHeld);
    Sleep(40);  // main thread is waiting by now
    IlUnlock();
    return 0;
}

static void TestIcLock() {
    static CRITICAL_SECTION cs;
    static DWORD slot;
    InitializeCriticalSection(&cs);
    g_ilCs = &cs;
    g_ilCallbackSlot = &slot;
    LARGE_INTEGER f;
    QueryPerformanceFrequency(&f);
    g_ilUsPerTick = 1e6 / (double)f.QuadPart;
    Ts3nCounters& k = ts3native_counters;
    const LONG me = (LONG)GetCurrentThreadId();
    IlLock(); IlLock(); IlUnlock(); IlUnlock();  // recursive, uncontended
    assert(g_ilEnterLow == 2 && k.icLockContended == 0 && k.icLockLastTid == me && k.icLockOwnerChanges == 0 && k.icLockLeaveWaiters == 0);
    assert(k.icLockCallback == 0 && g_ilCallerCount >= 1);  // slot still empty; callers noted (inlining decides how many)
    g_ilHeld = CreateEventW(NULL, TRUE, FALSE, NULL);
    HANDLE t = CreateThread(NULL, 0, IlHolder, NULL, 0, NULL);
    WaitForSingleObject(g_ilHeld, INFINITE);
    slot = 0x1234;
    IlLock();  // waits ~40 ms for the holder
    assert(k.icLockContended == 1 && k.icLockWaitUs >= 20000 && k.icLockMaxWaitUs == k.icLockWaitUs);
    assert(k.icLockOwnerChanges == 2 && k.icLockLastTid == me && k.icLockPrevTid != me && k.icLockPrevTid != 0);
    assert(k.icLockLeaveWaiters == 1 && k.icLockCallback == 0x1234);  // the holder's unlock had us waiting
    IlUnlock();
    WaitForSingleObject(t, INFINITE);
    CloseHandle(t);
    CloseHandle(g_ilHeld);
    for (int i = 0; i < 1020; i++) { IlLock(); IlUnlock(); }  // 4 + 1020 = 1024 enters
    assert(k.icLockEntersK == 1 && g_ilEnterLow == 0 && k.icLockLeaveWaiters == 1);
    DeleteCriticalSection(&cs);
}

// --- route gate measurement: owner tracking of a recursive lock, outermost unlocked gate entries, dead frames ---
static CRITICAL_SECTION g_rgFakeCs;
static LONG __fastcall RgFakeAcquire(void* lock, void*, const DWORD*) {  // like 0x4E16F0: returns the recursion count
    EnterCriticalSection(&g_rgFakeCs);
    return ++*(LONG*)((u8*)lock + 0x20);
}
static LONG __fastcall RgFakeRelease(void* lock, void*) {
    const LONG r = --*(LONG*)((u8*)lock + 0x20);
    LeaveCriticalSection(&g_rgFakeCs);
    return r;
}
static u8 g_rgLockObj[0x28], g_rgOtherObj[0x28];
static HANDLE g_rgHeld, g_rgGo;
static DWORD WINAPI RgHolder(void*) {
    const DWORD inf = 0xFFFFFFFF;
    LockAcquireHook(g_rgLockObj, NULL, &inf);
    SetEvent(g_rgHeld);
    WaitForSingleObject(g_rgGo, INFINITE);
    LockReleaseHook(g_rgLockObj, NULL);
    return 0;
}

static void TestRouteGate() {
    Ts3nCounters& k = ts3native_counters;
    const LONG me = (LONG)GetCurrentThreadId();
    const DWORD inf = 0xFFFFFFFF;
    const LockAcquireFn savedLock = g_lockOrig;
    g_lockOrig = RgFakeAcquire;
    g_rgRelOrig = RgFakeRelease;
    InitializeCriticalSection(&g_rgFakeCs);
    InitializeCriticalSection(&g_rgCs);
    // inner: mov eax,42; ret.  outer (stdcall, 1 arg): mov eax,0; call inner; ret 4
    u8* code = (u8*)VirtualAlloc(NULL, 64, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
    static const u8 kInner[5] = {0xB8, 0x2A, 0x00, 0x00, 0x00}, kOuter[5] = {0xB8, 0x00, 0x00, 0x00, 0x00};
    u8* inner = code;
    u8* outer = code + 16;
    memcpy(inner, kInner, 5);
    inner[5] = 0xC3;
    memcpy(outer, kOuter, 5);
    outer[5] = 0xE8;
    *(LONG*)(outer + 6) = (LONG)(inner - (outer + 10));
    outer[10] = 0xC2; outer[11] = 0x04; outer[12] = 0x00;
    g_rgTramp[0] = InstallDetour("gate outer", (DWORD)(uintptr_t)outer, kOuter, 5, (void*)RgStub0, &g_rgTramp[0]);
    g_rgTramp[1] = InstallDetour("gate inner", (DWORD)(uintptr_t)inner, kInner, 5, (void*)RgStub1, &g_rgTramp[1]);
    assert(g_rgTramp[0] && g_rgTramp[1]);
    typedef int(__stdcall* OuterFn)(int);
    typedef int(__cdecl* InnerFn)();
    const OuterFn Outer = (OuterFn)(void*)outer;
    const InnerFn Inner = (InnerFn)(void*)inner;

    k.rgReady = 7;
    assert(Outer(5) == 42 && k.rgEntries == 0);  // lock not learned yet: pass-through, nothing counted
    g_rgLock = g_rgLockObj;
    assert(Outer(5) == 42 && k.rgEntries == 2 && t_rg.n == 0);
    assert(k.rgUnlocked[0] == 1 && k.rgUnlocked[1] == 0);  // the nested inner entry is not outermost
    assert(Inner() == 42 && k.rgUnlocked[1] == 1 && k.rgJob[0] == 0 && k.rgJob[1] == 0);

    // recursive hold: still owned after the inner release, free after the outer one; only the outermost acquire counts
    assert(LockAcquireHook(g_rgLockObj, NULL, &inf) == 1 && LockAcquireHook(g_rgLockObj, NULL, &inf) == 2);
    assert(k.rgAcquires == 1 && k.rgOwnerTid == me);
    assert(LockReleaseHook(g_rgLockObj, NULL) == 1 && k.rgOwnerTid == me);
    assert(Outer(5) == 42 && Inner() == 42 && k.rgUnlocked[0] == 1 && k.rgUnlocked[1] == 1 && k.rgEntries == 6);
    assert(LockReleaseHook(g_rgLockObj, NULL) == 0 && k.rgOwnerTid == 0);
    assert(Outer(5) == 42 && k.rgUnlocked[0] == 2);
    // any other lock is not tracked
    assert(LockAcquireHook(g_rgOtherObj, NULL, &inf) == 1 && k.rgAcquires == 1 && k.rgOwnerTid == 0);
    assert(LockReleaseHook(g_rgOtherObj, NULL) == 0);

    // another thread holds it: our entries still count, its release frees the owner
    g_rgHeld = CreateEventW(NULL, TRUE, FALSE, NULL);
    g_rgGo = CreateEventW(NULL, TRUE, FALSE, NULL);
    HANDLE t = CreateThread(NULL, 0, RgHolder, NULL, 0, NULL);
    WaitForSingleObject(g_rgHeld, INFINITE);
    assert(k.rgOwnerTid != 0 && k.rgOwnerTid != me && k.rgAcquires == 2);
    assert(Outer(5) == 42 && k.rgUnlocked[0] == 3);
    SetEvent(g_rgGo);
    WaitForSingleObject(t, INFINITE);
    CloseHandle(t);
    CloseHandle(g_rgHeld);
    CloseHandle(g_rgGo);
    assert(k.rgOwnerTid == 0);

    // a route-worker job runs: the same entries also count as job entries
    g_callerTid = 1234;
    assert(Inner() == 42 && k.rgUnlocked[1] == 2 && k.rgJob[1] == 1);
    g_callerTid = 0;

    // frames left without returning: an entry at or above a frame's return slot drops it (it is outermost again)
    DWORD stack[20] = {};
    stack[10] = 0x111;
    RgEnter(0, &stack[10]);
    assert(t_rg.n == 1 && stack[10] == (DWORD)(uintptr_t)RgLeaveThunk && k.rgUnlocked[0] == 4);
    RgEnter(1, &stack[5]);  // deeper: nested, not counted
    assert(t_rg.n == 2 && k.rgUnlocked[1] == 2);
    RgEnter(1, &stack[12]);  // above both: they are dead
    assert(t_rg.n == 1 && k.rgUnlocked[1] == 3 && t_rg.f[0].ret == 0);
    t_rg.n = 0;
    // 16 open frames: a 17th entry is counted as untracked and its return address left alone
    for (int i = 0; i < 17; i++) RgEnter(1, &stack[19 - i]);
    assert(t_rg.n == 16 && k.rgUntracked == 1 && stack[3] == 0 && k.rgUnlocked[1] == 4);
    t_rg.n = 0;

    // every stub pushes its own site index and jumps through its own trampoline slot
    for (int i = 0; i < RG_SITES; i++) {
        const u8* s = (const u8*)(void*)kRgStubs[i];
        const DWORD slot = (DWORD)(uintptr_t)&g_rgTramp[i];
        bool push = false, jmp = false;
        for (int j = 0; j < 40; j++) {
            push |= s[j] == 0x6A && s[j + 1] == i;
            jmp |= s[j] == 0xFF && s[j + 1] == 0x25 && *(const DWORD*)(s + j + 2) == slot;
        }
        assert(push && jmp);
    }

    k.rgReady = 0;
    g_rgLock = NULL;
    g_lockOrig = savedLock;
    VirtualFree(code, 0, MEM_RELEASE);
    DeleteCriticalSection(&g_rgFakeCs);
}

// --- mono thread step 1: the P2 root push over a fake GC thread table, P1 registration guard ---
static DWORD g_mtFakeTab[256 * 5];
static volatile LONG g_mtFakeMax, g_mtFakeStopped;
static int g_mtOrigCalls, g_mtOnes, g_mtStacks;
static DWORD g_mtOneVals[16], g_mtLo[4], g_mtHi[4];
static void __cdecl MtFakeOrig() { g_mtOrigCalls++; }
static void __cdecl MtFakeOne(DWORD p) { if (g_mtOnes < 16) g_mtOneVals[g_mtOnes] = p; g_mtOnes++; }
static void __cdecl MtFakeStack(DWORD lo, DWORD hi) { if (g_mtStacks < 4) { g_mtLo[g_mtStacks] = lo; g_mtHi[g_mtStacks] = hi; } g_mtStacks++; }
static void* __cdecl MtFakeNewThread() {  // like 0xE51070: claims the next entry for the caller
    const LONG i = ++g_mtFakeMax;
    DWORD* e = g_mtFakeTab + i * 5;
    e[0] = 1; e[1] = GetCurrentThreadId(); e[3] = (DWORD)(uintptr_t)((NT_TIB*)NtCurrentTeb())->StackBase;
    return e;
}
static volatile DWORD g_mtMarkerAt, g_mtBase, g_mtTid;
static HANDLE g_mtReady, g_mtQuit;
static DWORD WINAPI MtVictim(void*) {
    volatile DWORD marker = 0xC0FFEE11;  // a "root" on this stack
    g_mtMarkerAt = (DWORD)(uintptr_t)&marker;
    g_mtBase = (DWORD)(uintptr_t)((NT_TIB*)NtCurrentTeb())->StackBase;
    g_mtTid = GetCurrentThreadId();
    SetEvent(g_mtReady);
    WaitForSingleObject(g_mtQuit, INFINITE);
    return marker == 0xC0FFEE11 ? 0 : 1;
}
static DWORD WINAPI MtOtherRegister(void*) { return MtRegisterCurrentThread() ? 1 : 0; }

static void TestMonoThread() {
    g_mtOrigPush = MtFakeOrig; g_mtPushOne = MtFakeOne; g_mtPushStack = MtFakeStack; g_mtNewThread = MtFakeNewThread;
    g_mtTable = g_mtFakeTab; g_mtMaxIdx = &g_mtFakeMax; g_mtStopped = &g_mtFakeStopped;
    Ts3nCounters& k = ts3native_counters;
    const DWORD me = GetCurrentThreadId();
    g_mtReady = CreateEventW(NULL, TRUE, FALSE, NULL);
    g_mtQuit = CreateEventW(NULL, TRUE, FALSE, NULL);
    HANDLE t = CreateThread(NULL, 0, MtVictim, NULL, 0, NULL);
    WaitForSingleObject(g_mtReady, INFINITE);
    assert(SuspendThread(t) != (DWORD)-1);
    // [0] the collector itself (suspended flag set to prove the tid test), [1] the victim, [2] not suspended, [3] freed
    DWORD* e = g_mtFakeTab;
    e[0] = 1; e[1] = me; e[2] = 0; e[3] = 0x1000; e[4] = 1;
    e[5] = 1; e[6] = g_mtTid; e[7] = (DWORD)(uintptr_t)t; e[8] = g_mtBase; e[9] = 1;
    e[10] = 1; e[11] = g_mtTid + 1; e[12] = (DWORD)(uintptr_t)t; e[13] = g_mtBase; e[14] = 0;
    e[15] = 0; e[16] = g_mtTid + 2; e[17] = (DWORD)(uintptr_t)t; e[18] = 0; e[19] = 1;
    g_mtFakeMax = 3;
    // world running (incremental step): only the game's push
    g_mtFakeStopped = 0;
    MtPushOtherRoots();
    assert(g_mtOrigCalls == 1 && g_mtOnes == 0 && g_mtStacks == 0 && k.mtPushCalls == 0);
    // world stopped: the victim's 7 registers and its stack [ESP, base), which holds the marker
    g_mtFakeStopped = 1;
    MtPushOtherRoots();
    CONTEXT c;
    c.ContextFlags = CONTEXT_INTEGER | CONTEXT_CONTROL;
    assert(GetThreadContext(t, &c));
    assert(g_mtOrigCalls == 2 && g_mtOnes == 7 && g_mtStacks == 1 && k.mtPushCalls == 1 && k.mtStacksPushed == 1 && k.mtCtxFails == 0);
    assert(g_mtOneVals[0] == c.Eax && g_mtOneVals[1] == c.Ebx && g_mtOneVals[2] == c.Ecx && g_mtOneVals[3] == c.Edx);
    assert(g_mtOneVals[4] == c.Esi && g_mtOneVals[5] == c.Edi && g_mtOneVals[6] == c.Ebp);
    assert(g_mtLo[0] == c.Esp && g_mtHi[0] == g_mtBase && g_mtLo[0] <= g_mtMarkerAt && g_mtMarkerAt < g_mtHi[0]);
    // GetThreadContext fails (bad handle): whole committed stack, still covering the marker
    e[7] = 0x1234;
    MtPushOtherRoots();
    assert(k.mtCtxFails == 1 && g_mtOnes == 7 && g_mtStacks == 2 && g_mtHi[1] == g_mtBase);
    assert(g_mtLo[1] <= g_mtLo[0] && g_mtLo[1] <= g_mtMarkerAt && (g_mtLo[1] & 0xFFF) == 0);
    // max index above 255 is clamped (entries past the fake's 4 are zero)
    e[7] = (DWORD)(uintptr_t)t;
    g_mtFakeMax = 1000;
    MtPushOtherRoots();
    assert(g_mtStacks == 3 && k.mtCtxFails == 1);
    g_mtFakeMax = 3;
    ResumeThread(t);
    SetEvent(g_mtQuit);
    WaitForSingleObject(t, INFINITE);
    DWORD code = 1;
    GetExitCodeThread(t, &code);
    assert(code == 0);
    CloseHandle(t);
    // P1: only the sim thread registers, once
    g_mtSimTid = me;
    e[1] = me + 7;  // the collector entry is someone else now: "me" is missing
    HANDLE o = CreateThread(NULL, 0, MtOtherRegister, NULL, 0, NULL);
    WaitForSingleObject(o, INFINITE);
    GetExitCodeThread(o, &code);
    CloseHandle(o);
    assert(code == 0 && g_mtFakeMax == 3);
    assert(MtRegisterCurrentThread() && g_mtFakeMax == 4 && MtFind(me) == 4);
    assert(MtRegisterCurrentThread() && g_mtFakeMax == 4);
    CloseHandle(g_mtReady);
    CloseHandle(g_mtQuit);
    memset(&k.mtReady, 0, 8 * sizeof(LONG));
}

// --- Simulate loop-back hook (simloop.cpp, docs/51 §188): a copy of the game's loop tail driving a 3-iteration loop ---
static int g_slpCalls;
static DWORD g_slpEsiSeen;
static u8 g_slpHost[0xC20];
static void __cdecl SlpFake() {
    g_slpCalls++;
    g_slpEsiSeen = *(DWORD*)(g_slpHost + 0xC10);  // remaining count at the callback
}
static void TestSimLoop() {
    u8* p = (u8*)VirtualAlloc(NULL, 4096, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
    assert(p);
    u8* q = p;
    *q++ = 0x56;                                                          // push esi
    *q++ = 0xBE, *(DWORD*)q = (DWORD)(uintptr_t)g_slpHost, q += 4;         // mov esi, host
    u8* site = q;
    memcpy(q, kSlpSite, sizeof(kSlpSite)), q += sizeof(kSlpSite);            // mov ecx,[esi+0xC08]; cmp ecx,1; je
    u8* rel = q;
    q += 4;
    *q++ = 0x5E, *q++ = 0x33, *q++ = 0xC0, *q++ = 0xC3;                   // pop esi; xor eax,eax; ret
    u8* head = p + 0x40;                                                  // loop head: dec [esi+0xC10]; jnz +10; mov [esi+0xC08],0; jmp site
    *(LONG*)rel = (LONG)(head - (rel + 4));
    q = head;
    *q++ = 0xFF, *q++ = 0x8E, *(DWORD*)q = 0xC10, q += 4;
    *q++ = 0x75, *q++ = 0x0A;
    *q++ = 0xC7, *q++ = 0x86, *(DWORD*)q = 0xC08, q += 4, *(DWORD*)q = 0, q += 4;
    *q++ = 0xE9, *(LONG*)q = (LONG)(site - (q + 4)), q += 4;
    typedef int(__cdecl* RunFn)();
    const RunFn run = (RunFn)(void*)p;
    const DWORD target = (DWORD)(uintptr_t)head;
    *(DWORD*)(g_slpHost + 0xC08) = 1, *(DWORD*)(g_slpHost + 0xC10) = 3;
    assert(run() == 0 && g_slpCalls == 0);  // unhooked: the loop runs 3 times, no callback
    // refused: a changed byte, or a je that goes elsewhere; nothing written
    const LONG rel0 = *(LONG*)rel;
    site[7] = 0xFA;
    assert(!SlpInstall(site, target) && *(LONG*)rel == rel0);
    site[7] = 0xF9;
    assert(!SlpInstall(site, target + 1) && *(LONG*)rel == rel0);
    // hooked: one callback per loop-back (3), none on the exit path; esi, the flags and the loop still work
    assert(SimLoopAdd(SlpFake, "selftest", site, target) && g_slpState == 1 && g_slpN == 1 && g_slpBack == target);
    assert((DWORD)(uintptr_t)(rel + 4) + (DWORD)*(LONG*)rel == (DWORD)(uintptr_t)SlpThunk);
    *(DWORD*)(g_slpHost + 0xC08) = 1, *(DWORD*)(g_slpHost + 0xC10) = 3;
    assert(run() == 0 && g_slpCalls == 1 + 1 + 1 && g_slpEsiSeen == 1);
    assert(SimLoopAdd(SlpFake, "selftest 2") && g_slpN == 2);  // second owner: no second patch
    *(DWORD*)(g_slpHost + 0xC08) = 1, *(DWORD*)(g_slpHost + 0xC10) = 2;
    assert(run() == 0 && g_slpCalls == 3 + 2 * 2);
    VirtualFree(p, 0, MEM_RELEASE);
    g_slpN = 0, g_slpState = 0, g_slpBack = 0;
    printf("sim loop: loop-back hook ok (3 iterations, %d callbacks)\n", g_slpCalls);
}

// post-pump hook (simloop.cpp SimPumpAdd): the slot chains to what it held; callbacks only after a return to the pump site
static int g_sppFakeRuns, g_sppCalls;
static DWORD __fastcall SppFakeRun(void* jm, void*, void* rec, DWORD deadline, DWORD nonblock) {
    assert(jm == (void*)0x1234 && rec == (void*)0x77 && deadline == 0 && nonblock == 1);
    g_sppFakeRuns++;
    return 0x55;
}
static void __cdecl SppFake() { g_sppCalls++; }
static void TestSimPump() {
    u8* p = (u8*)VirtualAlloc(NULL, 4096, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
    assert(p);
    static DWORD slot;
    u8* q = p;
    *q++ = 0xB8, *(DWORD*)q = (DWORD)(uintptr_t)&slot, q += 4;  // mov eax, &slot
    *q++ = 0x6A, *q++ = 0x01, *q++ = 0x6A, *q++ = 0x00, *q++ = 0x6A, *q++ = 0x77;  // push 1; push 0; push rec
    *q++ = 0xB9, *(DWORD*)q = 0x1234, q += 4;  // mov ecx, jm
    *q++ = 0xFF, *q++ = 0x10;  // call [eax] (thiscall, ret 0xC), as 0xD81FDA
    u8* back = q;
    *q++ = 0xC3;
    typedef DWORD(__cdecl* SiteFn)();
    const SiteFn site = (SiteFn)(void*)p;
    // refused: the slot holds something that is neither RunJobs nor code of ours; nothing written
    slot = 0x00001000;  // no module there
    assert(!SimPumpAdd(SppFake, "selftest", &slot, 0x0059A430) && g_sppState == -1 && slot == 0x00001000);
    g_sppState = 0;
    // a stub of ours in the slot (stands for [JobQueue]'s): chained; callbacks after the pump site's return only
    slot = (DWORD)(uintptr_t)SppFakeRun;
    g_sppRet = (DWORD)(uintptr_t)back;
    assert(SimPumpAdd(SppFake, "selftest", &slot, 0x0059A430) && g_sppState == 1 && slot == (DWORD)(uintptr_t)SppHook);
    assert(site() == 0x55 && g_sppFakeRuns == 1 && g_sppCalls == 1);
    assert(((SppRunFn)(uintptr_t)slot)((void*)0x1234, NULL, (void*)0x77, 0, 1) == 0x55 && g_sppFakeRuns == 2 && g_sppCalls == 1);  // other caller
    assert(SimPumpAdd(SppFake, "selftest 2") && g_sppN == 2 && slot == (DWORD)(uintptr_t)SppHook);  // second owner: no second patch
    assert(site() == 0x55 && g_sppFakeRuns == 3 && g_sppCalls == 3);
    VirtualFree(p, 0, MEM_RELEASE);
    g_sppN = 0, g_sppState = 0, g_sppRet = SPP_RET, g_sppNext = NULL;
    printf("sim pump: post-pump hook ok (%d pump calls, %d callbacks)\n", g_sppFakeRuns, g_sppCalls);
}

// --- lot streaming measurement: each hook on a fake of the game function it wraps (docs/28 §5.5) ---
static void LsSpin(double ms) { const LONGLONG t0 = LsNow(); while ((LsNow() - t0) * g_lsUsPerTick < ms * 1000) {} }
static int g_lsFakeExecs, g_lsPumpNest;
static u8 g_lsCallAdd[0x1C], g_lsCallRemove[0x1C], g_lsCallOther[0x1C];
static u8 g_lsLot[0x400], g_lsLot2[0x400];
static DWORD g_lsLotObjs[3];
static DWORD __fastcall LsFakeExec(u8*, void*) { g_lsFakeExecs++; LsSpin(2); return 0x55; }
static DWORD __fastcall LsFakePump(void*, void*, DWORD a, DWORD b, DWORD c) {
    assert(a == 1 && b == 2 && c == 3);
    if (g_lsPumpNest++ == 0) {
        LsAddHook(g_lsCallAdd, NULL);
        LsRemoveHook(g_lsCallRemove, NULL);
        LsPumpHook(NULL, NULL, 1, 2, 3);  // nested pass (a wait helping run jobs): part of the outer one
    }
    return 1;
}
static DWORD __fastcall LsFakeSetActive(u8* lot, void*, DWORD on) { if (!lot[0xC9]) lot[0xC1] = (u8)on; return 0; }
static DWORD __fastcall LsFakeScore(void*, void*, DWORD a, DWORD, DWORD, DWORD d) {
    assert(a == 0x3F800000 && d == 4);
    LsSetActiveHook(g_lsLot, NULL, 1);   // switches
    LsSetActiveHook(g_lsLot2, NULL, 1);  // [lot+0xC9] set: the game ignores it
    return 9;
}
static DWORD __fastcall LsFakeSync(void*, void*, DWORD, DWORD) { LsSpin(1); return 1; }
static DWORD __fastcall LsFakeObj(u8*, void*, DWORD a, DWORD, DWORD) { LsSpin(2); return a + 1; }
static DWORD WINAPI LsOtherThread(void*) { g_lsPumpNest = 0; return LsPumpHook(NULL, NULL, 1, 2, 3); }

static void TestLotStream() {
    Ts3nCounters& k = ts3native_counters;
    LARGE_INTEGER f;
    QueryPerformanceFrequency(&f);
    g_lsUsPerTick = 1e6 / (double)f.QuadPart;
    static DWORD simTid, mainTid;
    simTid = GetCurrentThreadId();
    mainTid = simTid + 1;  // main is another thread: synchronous posts to it wait
    g_lsSimTid = &simTid;
    g_lsMainTidSlot = &mainTid;
    g_lsAddOrig = g_lsRemoveOrig = LsFakeExec;
    g_lsPumpOrig = LsFakePump;
    g_lsScoreOrig = LsFakeScore;
    g_lsSetActiveOrig = LsFakeSetActive;
    g_lsSyncOrig = LsFakeSync;
    g_lsObjOrig = LsFakeObj;
    *(u8**)(g_lsCallAdd + 0x0C) = g_lsLot - 0x10;  // the execute adds [call+0x14] to [call+0x0C]
    *(int*)(g_lsCallAdd + 0x14) = 0x10;
    *(DWORD*)(g_lsCallAdd + 0x10) = LS_ADD_FN;
    *(u8**)(g_lsCallRemove + 0x0C) = g_lsLot;
    *(DWORD*)(g_lsCallRemove + 0x10) = LS_REMOVE_FN;
    memcpy(g_lsCallOther, g_lsCallAdd, sizeof(g_lsCallOther));
    *(DWORD*)(g_lsCallOther + 0x10) = 0x12345678;
    *(DWORD**)(g_lsLot + 0x14) = g_lsLotObjs;
    *(DWORD**)(g_lsLot + 0x18) = g_lsLotObjs + 3;
    *(DWORD*)(g_lsLot + 0x48) = 1;
    *(DWORD*)(g_lsLot + 0x4C) = 2;
    g_lsLot2[0xC9] = 1;
    wchar_t tmp[MAX_PATH], path[MAX_PATH], ini[MAX_PATH];
    GetTempPathW(MAX_PATH, tmp);
    swprintf_s(path, L"%sts3n-selftest-lotstream-%lu.jsonl", tmp, GetCurrentProcessId());  // per process: parallel runs
    swprintf_s(ini, L"%sts3n-selftest-lotstream-%lu.ini", tmp, GetCurrentProcessId());
    assert(LsOpenLog(path));

    // mode 0: every hook passes straight through, nothing counted or logged
    k.lsMode = 0;
    assert(LsAddHook(g_lsCallAdd, NULL) == 0x55 && g_lsFakeExecs == 1 && k.lotAdds == 0);
    assert(LsPumpHook(NULL, NULL, 1, 2, 3) == 1 && k.pumpCalls == 0 && k.lotAdds == 0 && k.lotRemoves == 0);
    assert(LsScoreHook(NULL, NULL, 0x3F800000, 2, 3, 4) == 9 && k.lotScoreCalls == 0 && k.lotSetOn == 0 && g_lsLot[0xC1] == 1);
    g_lsLot[0xC1] = 0;
    k.lsMode = 1;

    // add outside a pass; another fn on the same execute is not counted
    const int e0 = g_lsFakeExecs;
    assert(LsAddHook(g_lsCallAdd, NULL) == 0x55 && g_lsFakeExecs == e0 + 1);
    assert(k.lotAdds == 1 && k.lotAddObjs == 3 && k.lotAddMaxObjs == 3 && k.lotAddUs >= 1500 && k.lotAddMaxUs == k.lotAddUs);
    assert(LsAddHook(g_lsCallOther, NULL) == 0x55 && k.lotAdds == 1 && k.lsOtherFns == 1 && g_lsOtherFnCount == 1);
    assert(LsRemoveHook(g_lsCallAdd, NULL) == 0x55 && k.lotRemoves == 0 && k.lsOtherFns == 2 && g_lsOtherFnCount == 2);  // add fn on the remove execute

    // pass on the sim thread: add + remove inside it, the nested pass is part of it
    g_lsPumpNest = 0;
    assert(LsPumpHook(NULL, NULL, 1, 2, 3) == 1);
    assert(k.pumpCalls == 1 && k.pumpLotPasses == 1 && k.pumpMaxLots == 2 && k.lotAdds == 2 && k.lotRemoves == 1);
    assert(k.pumpMaxUs >= 3500 && k.pumpUs == k.pumpMaxUs && k.lotRemoveUs >= 1500 && g_lsPumpTid == 0);
    // a pass on another thread is not timed (its lot calls still are)
    HANDLE t = CreateThread(NULL, 0, LsOtherThread, NULL, 0, NULL);
    WaitForSingleObject(t, INFINITE);
    CloseHandle(t);
    assert(k.pumpCalls == 1 && k.pumpLotPasses == 1 && k.lotAdds == 3 && k.lotRemoves == 2);
    // the reader's per-second reset
    k.pumpMaxUs = 0;
    g_lsPumpNest = 1;  // a pass with no lot calls
    LsPumpHook(NULL, NULL, 1, 2, 3);
    assert(k.pumpCalls == 2 && k.pumpLotPasses == 1);
    TIMING_ASSERT("lot pump empty pass < 1.5 ms", k.pumpMaxUs < 1500);

    // scoring: one switch made inside it; SetActive's own change test
    assert(LsScoreHook(NULL, NULL, 0x3F800000, 2, 3, 4) == 9);
    assert(k.lotScoreCalls == 1 && k.lotScoreChanged == 1 && k.lotSetOn == 1 && k.lotSetOff == 0 && g_lsScoreTid == 0);
    LsSetActiveHook(g_lsLot, NULL, 1);  // already active
    assert(k.lotSetOn == 1);
    LsSetActiveHook(g_lsLot, NULL, 0x100);  // byte arg: low byte 0 = off
    assert(k.lotSetOff == 1 && g_lsLot[0xC1] == 0 && k.lotScoreChanged == 1);

    // sync dispatch: only a synchronous post to another thread is a wait (low bytes of both args)
    assert(LsSyncHook(NULL, NULL, 0, 0) == 1 && k.syncWaits == 1 && k.syncWaitUs >= 800);
    LsSyncHook(NULL, NULL, 1, 0);      // target = sim = caller: runs inline
    LsSyncHook(NULL, NULL, 0, 1);      // async
    assert(k.syncWaits == 1);
    LsSyncHook(NULL, NULL, 0x100, 0x100);
    assert(k.syncWaits == 2);

    // per object: histogram, slow ones logged
    g_lsObjLogUs = 1000;
    assert(LsObjHook(g_lsLot, NULL, 0xABCD, 0, 0) == 0xABCE && k.objScenes == 1 && k.objSceneHist[2] == 1 && k.objSceneMaxUs >= 1500);
    g_lsObjLogUs = 1000000;
    LsObjHook(g_lsLot, NULL, 0xABCD, 0, 0);
    assert(k.objScenes == 2 && k.lsLogDropped == 0);

    // event file: add (no pass), add + remove (in the pass), add + remove (other thread's pass, untimed), obj
    CloseHandle(g_lsLog);
    g_lsLog = INVALID_HANDLE_VALUE;
    FILE* fp = NULL;
    assert(_wfopen_s(&fp, path, L"rb") == 0 && fp);
    char line[512], lines[8][512];
    int n = 0;
    while (n < 8 && fgets(line, sizeof(line), fp)) strcpy_s(lines[n++], line);
    fclose(fp);
    DeleteFileW(path);
    assert(n == 6);
    assert(strstr(lines[0], "\"kind\":\"add\",\"lot\":\"0x") && strstr(lines[0], "\"lotId\":\"0x0000000200000001\",\"objs\":3,\"ms\":") &&
           strstr(lines[0], "\"passMs\":null}\n") && strncmp(lines[0], "{\"t\":17", 7) == 0);
    assert(strstr(lines[1], "\"kind\":\"add\"") && !strstr(lines[1], "null") && strstr(lines[2], "\"kind\":\"remove\"") && !strstr(lines[2], "null"));
    assert(strstr(lines[3], "\"kind\":\"add\"") && strstr(lines[3], "\"passMs\":null") && strstr(lines[4], "\"kind\":\"remove\""));
    assert(strstr(lines[5], "\"kind\":\"obj\"") && strstr(lines[5], "\"objs\":null") && strstr(lines[5], ",\"obj\":\"0x0000ABCD\"}\n"));

    // install: refused as a whole when the prologues are not there (no game in this process)
    WritePrivateProfileStringW(L"LotStream", L"Enabled", L"0", ini);
    assert(!ApplyLotStream(ini));
    WritePrivateProfileStringW(L"LotStream", L"Enabled", L"1", ini);
    assert(!ApplyLotStream(ini) && k.lsReady == 0 && g_lsLog == INVALID_HANDLE_VALUE);
    DeleteFileW(ini);
    memset(&k.lsReady, 0, (offsetof(Ts3nCounters, lsLogDropped) - offsetof(Ts3nCounters, lsReady) + 4));
}

// --- [Verify]: every probe through its real stub, trampoline and leave thunk on a dummy (nop prologue, mov eax; ret 8) ---
typedef DWORD(__fastcall* VfDummyFn)(DWORD ecx, DWORD edx, DWORD a1, DWORD a2);
static VfDummyFn g_vfDummy[VF_N];
static DWORD VfCall(int id, DWORD ecx = 0, DWORD a1 = 0, DWORD a2 = 0) { return g_vfDummy[id](ecx, 0, a1, a2); }
struct VfCallArgs { int id; DWORD ecx, a1, a2, ret; };
static DWORD WINAPI VfCallThread(void* p) {
    VfCallArgs* a = (VfCallArgs*)p;
    a->ret = g_vfDummy[a->id](a->ecx, 0, a->a1, a->a2);
    return (DWORD)t_vf.n;  // every frame left again
}
static DWORD VfOnOtherThread(int id, DWORD ecx = 0, DWORD a1 = 0, DWORD a2 = 0) {
    VfCallArgs a = {id, ecx, a1, a2, 0};
    HANDLE t = CreateThread(NULL, 0, VfCallThread, &a, 0, NULL);
    WaitForSingleObject(t, INFINITE);
    DWORD open = 1;
    GetExitCodeThread(t, &open);
    CloseHandle(t);
    assert(open == 0);
    return a.ret;
}

// v22 probes (docs/51 I-52..I-62); this thread is the sim thread, main is me + 1
static void TestVerifyV22(DWORD me, DWORD* stack) {
    Ts3nCounters& k = ts3native_counters;
    assert(*g_vfSimTidSlot == me && *g_vfMainTidSlot == me + 1);
    // timed thread probes: one sim call each, off the device thread, us total = max
    static const int kTimed[] = {58, 59, 61, 62, 63, 64, 65, 66, 67, 71, 73, 74, 75, 76, 77, 78, 81, 82};
    for (int i : kTimed) {
        assert(kVf[i].thr >= 46 && kVf[i].thr < VF_NSLOT && VfCall(i) == 0x1000u + i);
        const LONG* s = VfSlot(kVf[i].thr);
        assert(s == k.vfThread3[kVf[i].thr - 46] && s[0] == 0 && s[1] == 1 && s[2] == 0 && s[3] == 1 && s[5] == s[4]);
    }
    // count-only probes (the mid-function cctor slow path, arena grow, GC_free): no time, no frame
    static const int kCount[] = {60, 83, 84};
    for (int i : kCount) {
        assert(kVf[i].kind == VK_COUNT && VfCall(i) == 0x1000u + i && t_vf.n == 0);
        const LONG* s = VfSlot(kVf[i].thr);
        assert(s[1] == 1 && s[0] == 0 && s[4] == 0 && s[5] == 0);
    }
    // shape release: only the terrain swap's call (return address 0xC2A896) counts
    assert(VfCall(68) == 0x1044 && VfSlot(VF_SLOT_SHAPEREL)[1] == 0);
    stack[10] = VF_SWAP_RELEASE_RET;
    VfEnter(68, &stack[10]);
    assert(VfSlot(VF_SLOT_SHAPEREL)[1] == 1 && t_vf.n == 0 && stack[10] == VF_SWAP_RELEASE_RET);
    // shaders: creates before the first world load 0xC6D970 are startup ones
    g_vfWorldLoaded = 0;
    VfCall(79);
    VfCall(80);
    assert(k.vfShaderStartup[0] == 1 && k.vfShaderStartup[1] == 1);
    VfCall(38);  // worldLoad
    VfCall(79);
    assert(k.vfShaderStartup[0] == 1 && VfSlot(73)[1] == 2 && VfSlot(74)[1] == 1);
    // pump same pass: a job readied inside a RunJobs pass and executed in it counts once
    const DWORD jobA = 0xA000, jobB = 0xB000;
    VfCall(70, 0, jobA);  // outside a pass: not recorded
    assert(VfSlot(VF_SLOT_READY)[1] == 0);
    stack[10] = 0xAAA;
    VfEnter(69, &stack[10]);
    VfCall(70, 0, jobA);
    VfCall(4, 0, jobB);
    VfCall(4, 0, jobA);
    VfCall(4, 0, jobA);
    assert(VfSlot(VF_SLOT_READY)[1] == 1 && VfSlot(VF_SLOT_SAMEPASS)[1] == 1 && t_vf.pumpDepth == 1);
    assert(VfLeave() == 0xAAA && t_vf.pumpDepth == 0 && VfSlot(57)[1] == 1);
    VfCall(4, 0, jobA);  // after the pass
    stack[10] = 0xAAA;
    VfEnter(69, &stack[10]);  // a new pass forgets the old one's jobs
    VfCall(4, 0, jobA);
    assert(VfLeave() == 0xAAA && VfSlot(VF_SLOT_SAMEPASS)[1] == 1 && VfSlot(57)[1] == 2);
    // remote calls: selector 1 = sim (this thread): inline; selector 0 = main: posted
    VfCall(40, 0, 1, 1);
    VfCall(40, 0, 0, 1);
    assert(VfSlot(VF_SLOT_RCINLINE)[1] == 1 && t_vf.syncDepth == 0);
    // a synchronous post opens a wait: jobs and lot adds run in it count
    static DWORD callObj[8], otherCall[8];
    callObj[4] = VF_LOTADD_FN;
    otherCall[4] = 0x12345678;
    stack[10] = 0xBBB;
    stack[11] = 0;
    stack[12] = 0;
    VfEnter(40, &stack[10]);
    assert(t_vf.syncDepth == 1);
    VfCall(4, 0, jobB);
    VfCall(72, (DWORD)(uintptr_t)callObj);
    assert(VfSlot(VF_SLOT_JOBINWAIT)[1] == 1 && VfSlot(VF_SLOT_LOTADDINWAIT)[1] == 1 && VfSlot(65)[1] == 1);
    assert(VfLeave() == 0xBBB && t_vf.syncDepth == 0);
    // lot add: only fn 0xAC1130; managed entries and inline re-posts inside it count
    VfCall(72, (DWORD)(uintptr_t)otherCall);
    VfCall(85);
    assert(VfSlot(65)[1] == 1 && VfSlot(VF_SLOT_INVOKE)[1] == 1 && VfSlot(VF_SLOT_LOTADDMANAGED)[1] == 0);
    stack[8] = (DWORD)(uintptr_t)callObj;  // ecx in the pushad block
    stack[10] = 0xCCC;
    VfEnter(72, &stack[10]);
    assert(t_vf.lotAddDepth == 1);
    VfCall(86);
    VfCall(87);
    VfCall(88);
    VfCall(40, 0, 1, 1);
    assert(VfSlot(VF_SLOT_INVOKE)[1] == 4 && VfSlot(VF_SLOT_LOTADDMANAGED)[1] == 3 && VfSlot(VF_SLOT_RCINLOTADD)[1] == 1);
    assert(VfLeave() == 0xCCC && t_vf.lotAddDepth == 0 && VfSlot(65)[1] == 2 && VfSlot(VF_SLOT_RCINLINE)[1] == 2);
    // a dropped lot-add frame gives its depth back
    stack[5] = 0xEEE;
    stack[3] = (DWORD)(uintptr_t)callObj;
    stack[12] = 0xFFF;
    VfEnter(72, &stack[5]);
    VfEnter(0, &stack[12]);
    assert(t_vf.lotAddDepth == 0 && t_vf.n == 1 && VfLeave() == 0xFFF);
    // cursor manager: two threads in Apply at once (self group 4); own nesting does not count
    stack[10] = 0xDDD;
    VfEnter(82, &stack[10]);
    VfCall(82);
    assert(k.vfOverlapCur == 0);
    VfOnOtherThread(82);
    assert(k.vfOverlapCur == 1 && VfLeave() == 0xDDD && g_vfOvIn[4][0] == 0 && g_vfOvIn[4][1] == 0 && t_vf.n == 0);
}

// --- [JobQueue] (v23): sim record mask switch and ExecuteJob counters on fake records and jobs ---
static void TestJobQueue() {
    static_assert(offsetof(Ts3nCounters, jqReady) == 0xBF0 && offsetof(Ts3nCounters, jqSimMode) == 0xBF4 &&
                  offsetof(Ts3nCounters, jqWorkerJobs) == 0xC14 && offsetof(Ts3nCounters, d3dMtReady) == 0xC18 &&
                  offsetof(Ts3nCounters, allocDedupe) == 0xC1C && offsetof(Ts3nCounters, allocDupFrees) == 0xC20, "v23 layout");
    // only the dedicated sim record (0x80000004) is switched; the merged main+sim record (0x80000005) and others never
    assert(JqSimMask(0x80000004, 1) == 4 && JqSimMask(4, 0) == 0x80000004 && JqSimMask(4, 1) == 4 && JqSimMask(0x80000004, 0) == 0x80000004);
    assert(JqSimMask(0x80000005, 1) == 0x80000005 && JqSimMask(0x80000001, 1) == 0x80000001 && JqSimMask(0x80000000, 1) == 0x80000000 &&
           JqSimMask(2, 1) == 2);
    Ts3nCounters& k = ts3native_counters;
    __declspec(align(4)) u8 job[0x50] = {};
    *(DWORD*)(job + 0x10) = kJqRenderFns[2];
    *(LONG*)(job + 0x28) = -1;
    DWORD simTid = 0;
    g_jqSimTid = &simTid;
    const LONG r0 = k.jqRenderJobs, w0 = k.jqWorkerJobs, t0 = k.jqWorkerTids;
    JqNote(job);  // this thread is neither main (g_mainTid) nor sim
    assert(k.jqRenderJobs == r0 + 1 && k.jqWorkerJobs == w0 + 1 && k.jqWorkerTids == t0 + 1);
    JqNote(job);  // same thread: not a new worker
    assert(k.jqWorkerTids == t0 + 1 && k.jqWorkerJobs == w0 + 2);
    *(LONG*)(job + 0x28) = 4;  // sim-only job on a worker: not shared, not counted as worker work
    JqNote(job);
    assert(k.jqWorkerJobs == w0 + 2 && k.jqRenderJobs == r0 + 3);
    *(DWORD*)(job + 0x10) = 0;  // no fn: completes without running, ignored
    JqNote(job);
    assert(k.jqRenderJobs == r0 + 3);
    // as the sim thread: shared and frame jobs counted as sim work
    simTid = GetCurrentThreadId();
    *(DWORD*)(job + 0x10) = kJqRenderFns[0];
    *(LONG*)(job + 0x28) = -1;
    const LONG s0 = k.jqSimSharedJobs, sr0 = k.jqSimRenderJobs;
    JqNote(job);
    assert(k.jqSimSharedJobs == s0 + 1 && k.jqSimRenderJobs == sr0 + 1 && k.jqWorkerJobs == w0 + 2);
    // the slot stub flips bit 31 of the sim record per the switch and counts passes; other threads' records untouched
    static int calls;
    struct Fake { static DWORD __fastcall Run(void*, void*, u8*, DWORD, DWORD) { calls++; return 1; } };
    g_jqRunJobsOrig = Fake::Run;
    __declspec(align(4)) u8 rec[0x44] = {};
    *(DWORD*)(rec + 0x2C) = 0x80000004;
    k.jqSimMode = 1;
    const LONG p0 = k.jqSimPumps, sp0 = k.jqSimSkipPasses;
    assert(JqRunJobsHook(NULL, NULL, rec, 0, 1) == 1 && *(DWORD*)(rec + 0x2C) == 4 && k.jqSimSkipPasses == sp0 + 1);
    k.jqSimMode = 0;
    assert(JqRunJobsHook(NULL, NULL, rec, 0, 1) == 1 && *(DWORD*)(rec + 0x2C) == 0x80000004 && k.jqSimSkipPasses == sp0 + 1);
    assert(k.jqSimPumps == p0 + 2 && calls == 2);
    *(DWORD*)(rec + 0x2C) = 0x80000005;  // merged main+sim record: never switched
    k.jqSimMode = 1;
    JqRunJobsHook(NULL, NULL, rec, 0, 1);
    assert(*(DWORD*)(rec + 0x2C) == 0x80000005 && k.jqSimPumps == p0 + 2);
    simTid = 0;
    k.jqSimMode = 0;
    // #14: one Init bit per worker, clamped to 1-4
    assert(JqWorkerBits(1) == 1 && JqWorkerBits(2) == 3 && JqWorkerBits(3) == 7 && JqWorkerBits(4) == 0xF && JqWorkerBits(9) == 0xF &&
           JqWorkerBits(0) == 1);
}

static void TestVerify() {
    static_assert(offsetof(Ts3nCounters, vfReady) == 0x348 && offsetof(Ts3nCounters, vfMode) == 0x34C && offsetof(Ts3nCounters, vfThread) == 0x350 &&
                  offsetof(Ts3nCounters, vfSwaps) == 0x470 && offsetof(Ts3nCounters, vfIdOffSimRet) == 0x494 && offsetof(Ts3nCounters, vfPkgCalls) == 0x4B4 &&
                  offsetof(Ts3nCounters, vfIdChain) == 0x4C8 && offsetof(Ts3nCounters, vfW2lCallsK) == 0x4E8 && offsetof(Ts3nCounters, vfFdbHolds) == 0x4F8 &&
                  offsetof(Ts3nCounters, vfUiLock) == 0x510 && offsetof(Ts3nCounters, vfFpsGate) == 0x534 && offsetof(Ts3nCounters, vfSimSlice) == 0x538 &&
                  offsetof(Ts3nCounters, vfDeviceTid) == 0x550 && offsetof(Ts3nCounters, vfLogDropped) == 0x558, "v20 layout");
    static_assert(offsetof(Ts3nCounters, vfReady2) == 0x55C && offsetof(Ts3nCounters, vfThread2) == 0x560 && offsetof(Ts3nCounters, vfOverlap) == 0x890 &&
                  offsetof(Ts3nCounters, vfFdbPublishes) == 0x8A0 && offsetof(Ts3nCounters, vfFpsLoops) == 0x8A4 &&
                  VF_NSLOT - VF_NT == 68, "v21 layout");
    static_assert(offsetof(Ts3nCounters, vfReady3) == 0x8A8 && offsetof(Ts3nCounters, vfThread3) == 0x8AC && offsetof(Ts3nCounters, vfOverlapCur) == 0xBDC &&
                  offsetof(Ts3nCounters, vfShaderStartup) == 0xBE0 && offsetof(Ts3nCounters, vfMonoIcNestNeg) == 0xBEC && sizeof(Ts3nCounters) >= 764 * 4 &&
                  VF_N - VF_LOOP - 1 == 31, "v22 layout");
    Ts3nCounters& k = ts3native_counters;
    LARGE_INTEGER f;
    QueryPerformanceFrequency(&f);
    g_vfFreq = f.QuadPart;
    const DWORD me = GetCurrentThreadId();
    static DWORD mainTid, simTid;
    static u8 dev[0x20];
    static u8* devPtr = dev;
    mainTid = me + 1;  // this thread is the sim thread; the device belongs to "main"
    simTid = me;
    *(DWORD*)(dev + 0x10) = mainTid;
    g_vfMainTidSlot = &mainTid;
    g_vfSimTidSlot = &simTid;
    g_vfDevSlot = &devPtr;
    g_vfGroups = VF_THREADS | VF_RACES | VF_COSTS;
    u8* code = (u8*)VirtualAlloc(NULL, VF_N * 32, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
    u8 nops[16];
    memset(nops, 0x90, sizeof(nops));
    for (int i = 0; i < VF_N; i++) {
        u8* d = code + i * 32;
        const int n = kVf[i].len;
        assert(n >= 5 && n <= 10);
        memset(d, 0x90, n);
        d[n] = 0xB8;  // mov eax, 0x1000 + i ; ret 8
        *(DWORD*)(d + n + 1) = 0x1000 + i;
        d[n + 5] = 0xC2; d[n + 6] = 0x08; d[n + 7] = 0x00;
        assert(VfInstallAt(i, (DWORD)(uintptr_t)d, nops));
        g_vfDummy[i] = (VfDummyFn)(void*)d;
    }
    wchar_t tmp[MAX_PATH], path[MAX_PATH], ini[MAX_PATH];
    GetTempPathW(MAX_PATH, tmp);
    swprintf_s(path, L"%sts3n-selftest-verify-%lu.jsonl", tmp, GetCurrentProcessId());
    swprintf_s(ini, L"%sts3n-selftest-verify-%lu.ini", tmp, GetCurrentProcessId());
    g_vfLog = CreateFileW(path, GENERIC_WRITE, FILE_SHARE_READ, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    assert(g_vfLog != INVALID_HANDLE_VALUE);
    static DWORD buckets[0x40C], obj[3][5];
    for (DWORD j = 0; j < 3; j++) { obj[j][2] = 5 + j * VF_ID_BUCKETS; obj[j][3] = j + 1; obj[j][4] = j < 2 ? (DWORD)(uintptr_t)obj[j + 1] : 0; }
    buckets[5] = (DWORD)(uintptr_t)obj[0];
    buckets[0x1028 / 4] = 3;
    DWORD idLast[2] = {5 + 2 * VF_ID_BUCKETS, 3}, idFirst[2] = {5, 1}, idMiss[2] = {6, 0};
    const DWORD B = (DWORD)(uintptr_t)buckets;

    // mode 0: every probe passes straight through, nothing counted
    k.vfMode = 0;
    for (int i = 0; i < VF_N; i++) assert(VfCall(i, i == 16 ? B : 0, i == 25 ? VF_UI_LOCK : 0, i == 16 ? (DWORD)(uintptr_t)idLast : 0) == 0x1000u + i);
    for (const LONG* p = &k.vfThread[0][0]; p <= &k.vfMonoIcNestNeg; p++) assert(*p == 0 || p == &k.vfReady2 || p == &k.vfReady3);
    assert(t_vf.n == 0 && g_vfRayK == 0);
    k.vfMode = 1;

    // threads: role, off-device, time; the leave thunk returns the dummy's eax
    for (int i = 0; i < VF_NT; i++) assert(VfCall(i) == 0x1000u + i);
    for (int i = 0; i < VF_NT; i++) {
        const LONG* s = k.vfThread[i];
        assert(s[0] == 0 && s[1] == 1 && s[2] == 0 && s[3] == 1 && s[4] >= 0 && s[5] == s[4]);
    }
    // v21 thread probes: slots 12-41 in probe order, the same counting
    for (int i = 27; i < VF_LOOP; i++) {
        assert((kVf[i].kind == VK_THREAD || kVf[i].kind == VK_RCALL) && kVf[i].thr == VF_NT + i - 27 && VfCall(i) == 0x1000u + i);
        const LONG* s = VfSlot(kVf[i].thr);
        assert(s == k.vfThread2[i - 27] && s[0] == 0 && s[1] == 1 && s[2] == 0 && s[3] == 1 && s[4] >= 0 && s[5] == s[4]);
    }
    for (int g = 0; g < VF_NOV; g++) assert(*VfOvCount(g) == 0 && g_vfOvIn[g][0] == 0 && g_vfOvIn[g][1] == 0);
    assert(t_vf.n == 0 && t_vf.syncDepth == 0);
    mainTid = me;  // now the main thread, which owns the device
    *(DWORD*)(dev + 0x10) = me;
    assert(VfCall(0) == 0x1000 && k.vfThread[0][0] == 1 && k.vfThread[0][3] == 1);
    mainTid = me + 1;
    *(DWORD*)(dev + 0x10) = mainTid;
    assert(VfOnOtherThread(1) == 0x1001 && k.vfThread[1][2] == 1 && k.vfThread[1][3] == 2);
    DWORD stack[20] = {};
    stack[10] = 0x111;
    VfEnter(0, &stack[10]);
    assert(t_vf.n == 1 && stack[10] == (DWORD)(uintptr_t)VfLeaveThunk);
    Sleep(5);
    const LONG us0 = k.vfThread[0][4];
    assert(VfLeave() == 0x111 && t_vf.n == 0 && k.vfThread[0][4] - us0 >= 3000 && k.vfThread[0][5] >= 3000);

    // races: a shape swap while another thread casts a ray; own rays do not count
    assert(VfCall(12) == 0x100C && k.vfSwaps == 1 && k.vfSwapsInRay == 0);
    assert(VfCall(13) == 0x100D && g_vfRayK == 1 && g_vfRayIn == 0 && k.vfRayMaxInFlight == 1);
    stack[10] = 0x222;
    VfEnter(13, &stack[10]);  // this thread is inside a ray
    assert(g_vfRayIn == 1 && t_vf.rayDepth == 1);
    VfCall(12);
    assert(k.vfSwaps == 2 && k.vfSwapsInRay == 0);
    VfOnOtherThread(12);
    assert(k.vfSwaps == 3 && k.vfSwapsInRay == 1);
    assert(VfLeave() == 0x222 && g_vfRayIn == 0 && t_vf.rayDepth == 0);
    assert(VfSlot(VF_SLOT_SWAP)[1] == 2 && VfSlot(VF_SLOT_SWAP)[2] == 1 && k.vfSwaps == 3);  // swap thread roles (I-38-3)
    // overlap groups: own nesting never counts, another thread inside the other side does (both directions)
    stack[10] = 0x777;
    VfEnter(48, &stack[10]);  // scene message handler open here
    VfCall(5);
    assert(k.vfOverlap[0] == 0 && t_vf.ov[0][0] == 1);
    VfOnOtherThread(5);
    VfOnOtherThread(48);  // side A again: no other thread inside side B
    assert(k.vfOverlap[0] == 1 && VfLeave() == 0x777 && g_vfOvIn[0][0] == 0 && t_vf.ov[0][0] == 0);
    stack[10] = 0x777;
    VfEnter(7, &stack[10]);  // a cull job open here, the scene handler on another thread
    VfOnOtherThread(48);
    assert(k.vfOverlap[0] == 2 && VfLeave() == 0x777 && g_vfOvIn[0][1] == 0);
    stack[10] = 0x777;
    VfEnter(28, &stack[10]);  // route ray: a self group
    VfCall(28);
    assert(k.vfOverlap[3] == 0);
    VfOnOtherThread(28);
    assert(k.vfOverlap[3] == 1 && VfLeave() == 0x777 && g_vfOvIn[3][0] == 0 && g_vfOvIn[3][1] == 0 && t_vf.ov[3][1] == 0);
    stack[10] = 0x777;
    VfEnter(55, &stack[10]);  // UI draw open: a layout IC on another thread is a UI overlap, an effect update an R1 one
    VfOnOtherThread(34);
    VfOnOtherThread(56);
    assert(k.vfOverlap[1] == 1 && k.vfOverlap[2] == 1 && VfLeave() == 0x777);
    stack[5] = 0x888;  // a frame left without returning gives its groups back when it is dropped
    stack[12] = 0x999;
    VfEnter(56, &stack[5]);
    assert(g_vfOvIn[2][0] == 1);
    VfEnter(0, &stack[12]);
    assert(g_vfOvIn[2][0] == 0 && t_vf.ov[2][0] == 0 && t_vf.n == 1 && VfLeave() == 0x999);
    // key list open on this thread, registration on another (and on the same thread: not an overlap)
    stack[10] = 0x333;
    VfEnter(14, &stack[10]);
    VfOnOtherThread(15);
    assert(k.vfRegDbs == 1 && k.vfKeyListOverlaps == 1);
    VfCall(15);
    assert(k.vfRegDbs == 2 && k.vfKeyListOverlaps == 1);
    assert(VfLeave() == 0x333 && g_vfKeyIn == 0 && g_vfRegIn == 0 && k.vfKeyLists == 1);
    VfCall(14);
    assert(k.vfKeyLists == 2 && k.vfKeyListOverlaps == 1);
    // a frame left without returning: dropped (and its in-flight count undone) at the next entry above it
    stack[5] = 0x555;
    stack[12] = 0x666;
    VfEnter(13, &stack[5]);
    assert(g_vfRayIn == 1);
    VfEnter(14, &stack[12]);
    assert(g_vfRayIn == 0 && t_vf.rayDepth == 0 && t_vf.n == 1 && g_vfKeyIn == 1);
    assert(VfLeave() == 0x666 && g_vfKeyIn == 0 && t_vf.n == 0);
    // 16 open frames: the 17th timed entry is untracked and its return address left alone
    memset(stack, 0, sizeof(stack));
    for (int i = 0; i < 17; i++) VfEnter(0, &stack[19 - i]);
    assert(t_vf.n == 16 && k.vfUntracked == 1 && stack[3] == 0);
    t_vf.n = 0;
    // object id lookups: chain walk on the sim thread, off-sim callers recorded once each
    assert(VfCall(16, B, 0, (DWORD)(uintptr_t)idLast) == 0x1010);
    assert(k.vfIdChain[2] == 1 && k.vfIdMaxChain == 3 && k.vfIdObjects == 3);
    VfCall(16, B, 0, (DWORD)(uintptr_t)idMiss);
    VfCall(16, B, 0, (DWORD)(uintptr_t)idFirst);
    assert(k.vfIdChain[0] == 1 && k.vfIdChain[1] == 1 && k.vfIdOffSim == 0);
    VfOnOtherThread(16, B, 0, (DWORD)(uintptr_t)idFirst);
    VfOnOtherThread(16, B, 0, (DWORD)(uintptr_t)idFirst);
    assert(k.vfIdOffSim == 2 && k.vfIdOffSimRet[0] != 0 && k.vfIdOffSimRet[1] == 0 && k.vfIdChain[1] == 1 && g_vfIdK == 5);

    // costs: package probe with the index searches inside it (outermost call only)
    stack[10] = 0x444;
    VfEnter(17, &stack[10]);
    VfCall(18);
    VfCall(18);
    VfCall(17);  // nested call: part of the outer one
    assert(VfLeave() == 0x444 && k.vfPkgCalls == 1 && k.vfPkgWalked == 2 && k.vfPkgMaxWalked == 2 && t_vf.pkgDepth == 0);
    VfCall(18);  // outside a probe: not counted
    assert(VfCall(17) == 0x1011 && k.vfPkgCalls == 2 && k.vfPkgWalked == 2);
    for (int j = 0; j < 1024; j++) VfCall(19);
    assert(k.vfW2lCallsK == 1 && g_vfW2lK == 1024);
    assert(VfCall(20) == 0x1014 && k.vfFindLotCalls == 1);
    mainTid = me;  // the consume and the UI draw run on main
    VfCall(21); VfCall(21); VfCall(22);
    assert(k.vfTreeRebuilds == 2 && k.vfTreeMaxPerFrame == 2 && k.vfFdbHolds == 1);
    VfCall(21); VfCall(22);
    assert(k.vfTreeMaxPerFrame == 2 && k.vfFdbHolds == 2 && k.vfFdbHoldMaxUs >= 0);
    assert(VfSlot(VF_SLOT_TREE)[0] == 3 && VfSlot(VF_SLOT_TREE)[1] == 0);  // tree rebuild thread roles (I-34-8)
    VfCall(23); VfCall(24);  // FrameDb lock on main: not a sim wait
    assert(k.vfFdbWaits == 0 && k.vfFdbPublishes == 0);
    // Lock_Acquire is classified by its timeout pointer [esp+4], not by ecx (the lock object)
    assert(VfCall(25, 0, VF_UI_LOCK) == 0x1019 && k.vfUiLock[0][0] == 1);
    mainTid = me + 1;
    VfCall(23); VfCall(24);
    assert(k.vfFdbWaits == 2 && k.vfFdbHolds == 2 && k.vfFdbPublishes == 1);
    assert(VfCall(25, VF_UI_LOCK, 0x12345678) == 0x1019 && k.vfUiLock[1][0] == 0);  // any other lock, even with ecx = the old filter
    VfCall(25, 0, VF_UI_LOCK);
    VfOnOtherThread(25, 0, VF_UI_LOCK);
    assert(k.vfUiLock[1][0] == 1 && k.vfUiLock[2][0] == 1 && k.vfUiLock[1][2] >= 0);
    VfCall(25, 0, VF_JOB_LOCK);
    VfOnOtherThread(25, 0, VF_JOB_LOCK);
    VfCall(25, 0, VF_SIMSVC_LOCK);
    assert(VfSlot(VF_SLOT_JOBLOCK)[1] == 1 && VfSlot(VF_SLOT_JOBLOCK)[2] == 1 && VfSlot(VF_SLOT_JOBLOCK)[4] >= 0 &&
           VfSlot(VF_SLOT_SIMSVCLOCK)[1] == 1 && VfSlot(VF_SLOT_SIMSVCLOCK)[0] == 0 && k.vfUiLock[1][0] == 1);
    assert(VfCall(26) == 0x101A && k.vfFpsGate == 1 && k.vfFpsLoops == 0);
    assert(VfCall(57) == 0x1039 && VfCall(57) == 0x1039 && k.vfFpsLoops == 2 && k.vfFpsGate == 1);
    assert(t_vf.n == 0);
    TestVerifyV22(me, stack);

    // every stub pushes its own probe index (the gate stub has none) and jumps through its own trampoline slot
    for (int i = 0; i < VF_N; i++) {
        const u8* s = (const u8*)(void*)kVfStubs[i];
        const DWORD slot = (DWORD)(uintptr_t)&g_vfTramp[i];
        bool push = i == VF_GATE || i == VF_LOOP, jmp = false;
        for (int j = 0; j < 40; j++) {
            push |= s[j] == 0x6A && s[j + 1] == i;
            jmp |= s[j] == 0xFF && s[j + 1] == 0x25 && *(const DWORD*)(s + j + 2) == slot;
        }
        assert(push && jmp);
    }

    // readouts over fake game objects; bad pointers end as -1 (SEH)
    static u8 dc[0x300], svc[0x140];
    static DWORD cache[0x30], anim[0x30], otherCache[4] = {0x12345678}, arr[6], mgr[0x30];
    *(LONG*)(dc + 0x238) = 2048;
    svc[0x139] = 1;
    cache[0] = 0xFFDD94; cache[0x94 / 4] = 3 << 20; cache[0x98 / 4] = 1 << 20; cache[0x8C / 4] = 200 << 20;
    anim[0] = 0xFFDD94; anim[0x94 / 4] = 7 << 20; anim[0x98 / 4] = 5 << 20; anim[0x8C / 4] = 15 << 20;
    // as in game: the AnimationCache (priority 1000) comes first, then another class, then the ResourceCache (priority 0)
    arr[0] = (DWORD)(uintptr_t)anim; arr[1] = 1000; arr[2] = (DWORD)(uintptr_t)otherCache; arr[4] = (DWORD)(uintptr_t)cache;
    mgr[0] = 0xFFE250; mgr[0xA0 / 4] = (DWORD)(uintptr_t)arr; mgr[0xA4 / 4] = (DWORD)(uintptr_t)(arr + 6);
    static u8 *dcp = dc, *svcp = svc, *mgrp = (u8*)mgr, *nul = NULL, *bad = (u8*)(uintptr_t)16;
    g_vfDevCfgSlot = &dcp; g_vfSimSvcSlot = &svcp; g_vfResMgrSlot = &mgrp;
    static LONG nest = 3;
    g_vfIcNestSlot = &nest;
    VfPollOnce();
    assert(k.vfMonoIcNestMax == 3 && k.vfMonoIcNestNeg == 0);
    nest = -1;
    VfPollOnce();
    assert(k.vfMonoIcNestMax == 3 && k.vfMonoIcNestNeg == 1);
    nest = 0;
    VfPollOnce();
    assert(k.vfSpeedTreeKB == -1 && k.vfVramMB == 2048 && k.vfSimSlice == 1 && k.vfRcBytesKB == 3072 && k.vfRcFlushKB == 1024 &&
           k.vfRcBudgetMB == 200 && k.vfDeviceTid == (LONG)mainTid);
    arr[5] = 1;  // no priority-0 instance: no values
    VfPollOnce();
    assert(k.vfRcBudgetMB == -1 && k.vfRcBytesKB == 0);
    g_vfSimSvcSlot = &nul; g_vfDevCfgSlot = &bad; g_vfResMgrSlot = &nul;
    VfPollOnce();
    assert(k.vfSimSlice == -1 && k.vfVramMB == -1 && k.vfRcBytesKB == 0 && k.vfRcBudgetMB == -1);

    // event file: first roles, off-sim lookup, the two overlaps
    CloseHandle(g_vfLog);
    g_vfLog = INVALID_HANDLE_VALUE;
    FILE* fp = NULL;
    assert(_wfopen_s(&fp, path, L"rb") == 0 && fp);
    static char text[65536];
    const size_t got = fread(text, 1, sizeof(text) - 1, fp);
    text[got] = 0;
    fclose(fp);
    DeleteFileW(path);
    assert(strncmp(text, "{\"t\":17", 7) == 0 && k.vfLogDropped == 0);
    assert(strstr(text, "\"kind\":\"first\",\"probe\":\"thumbTex\",\"role\":\"sim\""));
    assert(strstr(text, "\"kind\":\"first\",\"probe\":\"thumbTex\",\"role\":\"offDevice\""));
    assert(strstr(text, "\"kind\":\"first\",\"probe\":\"thumbTex\",\"role\":\"main\""));
    assert(strstr(text, "\"kind\":\"first\",\"probe\":\"texCreate\",\"role\":\"other\""));
    assert(strstr(text, "\"kind\":\"idOffSim\"") && strstr(text, "\"kind\":\"swapInRay\"") && strstr(text, "\"kind\":\"registerInKeyList\""));
    assert(strstr(text, "\"kind\":\"overlap\",\"group\":0,\"probe\":\"jobMainCull\"") && strstr(text, "\"probe\":\"routeRay\",\"role\":\"other\""));
    assert(strstr(text, "\"kind\":\"first\",\"probe\":\"jobLock\",\"role\":\"sim\"") && strstr(text, "\"probe\":\"effectUpdate\",\"role\":\"sim\""));
    assert(!strstr(text, "\"probe\":\"texUpload\",\"role\":\"main\""));
    assert(strstr(text, "\"kind\":\"first\",\"probe\":\"pumpSamePass\",\"role\":\"sim\"") && strstr(text, "\"probe\":\"lotAddManaged\",\"role\":\"sim\""));
    assert(strstr(text, "\"kind\":\"overlap\",\"group\":4,\"probe\":\"cursorApply\""));

    // install in this process: nothing matches the game's bytes, so no probe is hooked
    WritePrivateProfileStringW(L"Verify", L"Enabled", L"0", ini);
    assert(!ApplyVerify(ini));
    WritePrivateProfileStringW(L"Verify", L"Enabled", L"1", ini);
    WritePrivateProfileStringW(L"Verify", L"Threads", L"1", ini);
    WritePrivateProfileStringW(L"Verify", L"Races", L"1", ini);
    WritePrivateProfileStringW(L"Verify", L"Log", L"0", ini);
    k.vfReady = 0;
    assert(!ApplyVerify(ini) && k.vfReady == 0 && g_vfLog == INVALID_HANDLE_VALUE);
    DeleteFileW(ini);
    k.vfMode = 0;
    g_vfGroups = 0;
    VirtualFree(code, 0, MEM_RELEASE);
}

// [LotSinCos]: the fake game function is plain C (x87 sin/cos, then float math as docs/63's model); the hook must return
// the same bits with the cache cold, warm, off, and through Verify.
static int g_fakeW2LCalls;
static bool g_fakeW2LLie;
static void __fastcall FakeW2L(void* lotp, void*, float x, float z, float* outX, float* outZ) {
    g_fakeW2LCalls++;
    const u8* lot = (const u8*)lotp;
    float s, c;
    LscSinCos((const float*)(lot + 0x1A0), &s, &c);
    const float dx = x - *(const float*)(lot + 0x190), dz = z - *(const float*)(lot + 0x198);
    const float ns = g_lscNegZero - s;
    const float p1 = ns * dz, p2 = c * dx, p3 = s * dx, p4 = c * dz;
    *outX = p1 + p2;
    *outZ = p3 + p4;
    if (g_fakeW2LLie) *outX += 1.0f;
}

static void TestLotSinCos() {
    static_assert(offsetof(Ts3nCounters, lscReady) == 0xC24 && offsetof(Ts3nCounters, lscMode) == 0xC28 &&
                  offsetof(Ts3nCounters, lscMismatches) == 0xC34 && offsetof(Ts3nCounters, idcMode) == 0xC3C &&
                  offsetof(Ts3nCounters, idcObjects) == 0xC5C && sizeof(Ts3nCounters) >= 792 * 4, "v24 layout");
    Ts3nCounters& k = ts3native_counters;
    g_lscOrig = FakeW2L;
    __declspec(align(16)) static u8 lots[4][0x200];
    const float angles[6] = {0.0f, -0.0f, 1.5707964f, 3.1415927f, -2.1f, 5.0e3f};
    unsigned seed = 12345;
    auto rnd = [&]() { seed = seed * 1103515245u + 12345u; return (float)((seed >> 8) & 0xFFFF) / 65536.0f; };
    k.lscMode = 1;
    g_lscVerify = false;
    memset(g_lsc, 0, sizeof(g_lsc));
    for (int i = 0; i < 4000; i++) {
        u8* lot = lots[i & 3];
        *(float*)(lot + 0x190) = rnd() * 2048.0f;
        *(float*)(lot + 0x198) = rnd() * 2048.0f;
        *(float*)(lot + 0x1A0) = (i % 7 == 6) ? (rnd() - 0.5f) * 12.0f : angles[i % 6];  // repeats (hits) and fresh angles
        const float x = *(float*)(lot + 0x190) + (rnd() - 0.5f) * 128.0f, z = *(float*)(lot + 0x198) + (rnd() - 0.5f) * 128.0f;
        float ax, az, bx, bz;
        const int calls = g_fakeW2LCalls;
        LscHook(lot, NULL, x, z, &ax, &az);
        assert(g_fakeW2LCalls == calls);  // mode 1 never runs the game's function without Verify
        FakeW2L(lot, NULL, x, z, &bx, &bz);
        assert(LscBits(ax) == LscBits(bx) && LscBits(az) == LscBits(bz));
    }
    // fixed angles only: each misses at most once, then hits (random angles above may have evicted them)
    const unsigned __int64 h0 = g_lscHits;
    for (int i = 0; i < 600; i++) {
        *(float*)(lots[1] + 0x1A0) = angles[i % 6];
        float ax, az;
        LscHook(lots[1], NULL, 1.f, 2.f, &ax, &az);
    }
    assert(g_lscHits - h0 >= 594);
    // -0.0 and +0.0 are different keys (fsin keeps the sign): both answers stay exact
    float o1[2], o2[2];
    *(float*)(lots[0] + 0x1A0) = -0.0f; LscHook(lots[0], NULL, 5.f, 7.f, &o1[0], &o1[1]);
    *(float*)(lots[0] + 0x1A0) = 0.0f; LscHook(lots[0], NULL, 5.f, 7.f, &o2[0], &o2[1]);
    float r[2];
    FakeW2L(lots[0], NULL, 5.f, 7.f, &r[0], &r[1]);
    assert(LscBits(o2[0]) == LscBits(r[0]) && LscBits(o2[1]) == LscBits(r[1]));
    // Verify: a hit that disagrees with the game is counted and the game's bits are returned
    g_lscVerify = true;
    g_fakeW2LLie = true;
    const LONG m0 = k.lscMismatches;
    LscHook(lots[0], NULL, 5.f, 7.f, &o1[0], &o1[1]);
    assert(k.lscMismatches == m0 + 1 && LscBits(o1[0]) == LscBits(r[0] + 1.0f) && LscBits(o1[1]) == LscBits(r[1]));
    g_fakeW2LLie = false;
    LscHook(lots[0], NULL, 5.f, 7.f, &o1[0], &o1[1]);
    assert(k.lscMismatches == m0 + 1 && LscBits(o1[0]) == LscBits(r[0]));
    // a torn entry (check word not matching) is a miss, never a wrong answer
    g_lscVerify = false;
    LscEntry& e = LscSlot(LscBits(0.0f));
    e.s = 0.75f;
    const int calls = g_fakeW2LCalls;
    LscHook(lots[0], NULL, 5.f, 7.f, &o1[0], &o1[1]);
    assert(LscBits(o1[0]) == LscBits(r[0]) && LscBits(o1[1]) == LscBits(r[1]) && g_fakeW2LCalls == calls);
    // mode 0: the game's function only
    k.lscMode = 0;
    LscHook(lots[0], NULL, 5.f, 7.f, &o1[0], &o1[1]);
    assert(g_fakeW2LCalls == calls + 1);
    // install in this process: Enabled=0 does nothing; Enabled=1 finds no matching prologue in our own image
    wchar_t ini[MAX_PATH];
    GetTempPathW(MAX_PATH, ini);
    swprintf_s(ini + wcslen(ini), MAX_PATH - wcslen(ini), L"ts3n-lsc-test-%lu.ini", GetCurrentProcessId());
    WritePrivateProfileStringW(L"LotSinCos", L"Enabled", L"0", ini);
    assert(!ApplyLotSinCos(ini));
    DeleteFileW(ini);
}

// [IdCache]: a fake bucket array with the game's layout (buckets at +0, sentinel at +0x1024, count at +0x1028; node id at
// +8/+0xC, next at +0x10). FakeIdLookup is the game's lookup as a C model (also checked against the exe by tools/emu
// object_id_lookup.py); FakeIdErase unlinks like 0x938D00.
static int g_fakeIdLookups;
static DWORD* __fastcall FakeIdLookup(u8* tbl, void*, DWORD* out, const DWORD* id) {
    g_fakeIdLookups++;
    u8** b = (u8**)(tbl + (id[0] % 0x409) * 4);
    for (u8* n = *b; n; n = *(u8**)(n + 0x10))
        if (*(DWORD*)(n + 8) == id[0] && *(DWORD*)(n + 0xC) == id[1]) { out[0] = (DWORD)(uintptr_t)n; out[1] = (DWORD)(uintptr_t)b; return out; }
    out[0] = *(DWORD*)(tbl + 0x1024);
    out[1] = (DWORD)(uintptr_t)(tbl + 0x1024);
    return out;
}
static void* __fastcall FakeIdErase(u8* tbl, void*, void* out, u8* node, void* bucket) {
    u8** p = (u8**)bucket;
    while (*p != node) p = (u8**)(*p + 0x10);
    *p = *(u8**)(node + 0x10);
    --*(LONG*)(tbl + 0x1028);
    return out;
}
static void IdTestInsert(u8* tbl, u8* node, DWORD lo, DWORD hi) {
    *(DWORD*)(node + 8) = lo;
    *(DWORD*)(node + 0xC) = hi;
    u8** b = (u8**)(tbl + (lo % 0x409) * 4);
    *(u8**)(node + 0x10) = *b;
    *b = node;
    ++*(LONG*)(tbl + 0x1028);
}

static void TestIdCache() {
    Ts3nCounters& k = ts3native_counters;
    static u8 tbl[0x1040];
    static u8 nodes[3000][0x20];
    static DWORD simTid;
    simTid = GetCurrentThreadId();
    g_idcSimTid = &simTid;
    g_idcLookupOrig = FakeIdLookup;
    g_idcEraseOrig = FakeIdErase;
    g_idcTbl = NULL;
    *(DWORD*)(tbl + 0x1024) = 0xE11D;  // sentinel value
    unsigned seed = 777;
    auto rnd = [&]() { seed = seed * 1103515245u + 12345u; return seed; };
    for (int i = 0; i < 3000; i++) IdTestInsert(tbl, nodes[i], rnd(), rnd());  // ~3 per bucket, as in play
    k.idcMode = 1;
    g_idcVerify = false;
    // every answer (hit, fill, miss) equals the game's, iterator included
    auto same = [&](DWORD lo, DWORD hi) {
        const DWORD id[2] = {lo, hi};
        DWORD a[2], b[2];
        const int calls = g_fakeIdLookups;
        assert(IdcLookupHook(tbl, NULL, a, id) == a);
        assert(g_fakeIdLookups == calls);  // mode 1 on the sim thread never runs the game's walk
        FakeIdLookup(tbl, NULL, b, id);
        assert(a[0] == b[0] && a[1] == b[1]);
        return a[0];
    };
    for (int r = 0; r < 4; r++)
        for (int i = 0; i < 3000; i += 7) same(*(DWORD*)(nodes[i] + 8), *(DWORD*)(nodes[i] + 0xC));
    assert(g_idcHits > 1000 && g_idcFills >= 400 && g_idcSteps > 0);
    assert(same(1, 2) == 0xE11D && g_idcNotFound == 1);  // not in the hash: sentinel, nothing stored
    assert(same(1, 2) == 0xE11D && g_idcNotFound == 2);
    // erase drops the entry: the next lookup misses (sentinel), never the freed node
    u8* victim = nodes[21];
    const DWORD vlo = *(DWORD*)(victim + 8), vhi = *(DWORD*)(victim + 0xC);
    assert(same(vlo, vhi) == (DWORD)(uintptr_t)victim);
    IdcEraseHook(tbl, NULL, NULL, victim, tbl + (vlo % 0x409) * 4);
    assert(k.idcErases == 1 && same(vlo, vhi) == 0xE11D);
    // inserted again: found and cached again
    IdTestInsert(tbl, victim, vlo, vhi);
    assert(same(vlo, vhi) == (DWORD)(uintptr_t)victim);
    // erase while the switch is off still clears: switching back on cannot return a stale node
    k.idcMode = 0;
    IdcEraseHook(tbl, NULL, NULL, victim, tbl + (vlo % 0x409) * 4);
    k.idcMode = 1;
    assert(same(vlo, vhi) == 0xE11D);
    // the bucket array emptied behind our back (constructor re-run): no hit on a cached node
    {
        u8* n = nodes[84];
        const DWORD lo = *(DWORD*)(n + 8), hi = *(DWORD*)(n + 0xC);
        assert(same(lo, hi) == (DWORD)(uintptr_t)n);
        u8** b = (u8**)(tbl + (lo % 0x409) * 4);
        u8* keep = *b;
        *b = NULL;
        assert(same(lo, hi) == 0xE11D);
        *b = keep;
    }
    // Verify: a wrong entry (id still matches, node no longer in the chain) is caught, dropped and the walk's answer returned
    {
        u8* n = nodes[99];
        const DWORD lo = *(DWORD*)(n + 8), hi = *(DWORD*)(n + 0xC);
        assert(same(lo, hi) == (DWORD)(uintptr_t)n);
        FakeIdErase(tbl, NULL, NULL, n, tbl + (lo % 0x409) * 4);  // unlinked without our hook
        g_idcVerify = true;
        const LONG m = k.idcMismatches;
        assert(same(lo, hi) == 0xE11D && k.idcMismatches == m + 1 && IdcSlot(lo, hi).node == NULL);
        g_idcVerify = false;
    }
    // other threads and other tables go to the game
    {
        const DWORD id[2] = {*(DWORD*)(nodes[5] + 8), *(DWORD*)(nodes[5] + 0xC)};
        DWORD a[2];
        int calls = g_fakeIdLookups;
        simTid = GetCurrentThreadId() + 1;
        IdcLookupHook(tbl, NULL, a, id);
        assert(g_fakeIdLookups == calls + 1 && a[0] == (DWORD)(uintptr_t)nodes[5]);
        simTid = GetCurrentThreadId();
        static u8 other[0x1040];
        IdcLookupHook(other, NULL, a, id);
        assert(g_fakeIdLookups == calls + 2);
        k.idcMode = 0;
        IdcLookupHook(tbl, NULL, a, id);
        assert(g_fakeIdLookups == calls + 3);
    }
    IdcPublish(tbl);
    assert(k.idcObjects == 2998 && k.idcLookupsK == (LONG)(g_idcLookups / 1000));
    wchar_t ini[MAX_PATH];
    GetTempPathW(MAX_PATH, ini);
    swprintf_s(ini + wcslen(ini), MAX_PATH - wcslen(ini), L"ts3n-idc-test-%lu.ini", GetCurrentProcessId());
    WritePrivateProfileStringW(L"IdCache", L"Enabled", L"0", ini);
    assert(!ApplyIdCache(ini));
    DeleteFileW(ini);
}

// --- H6 S2b: gate sites 13-15, exact G11 window, docs/60 §2.2 count-only sites, Verify counted inside an owner stub ---
// Runs after TestVerify (its role slots point at this thread as the sim thread).
static void TestRouteSites() {
    static_assert(offsetof(Ts3nCounters, rgUnlocked2) == 0xC60 && offsetof(Ts3nCounters, rgJob2) == 0xC6C &&
                  offsetof(Ts3nCounters, rtReady) == 0xC78 && offsetof(Ts3nCounters, vfRtDestroyInJob) == 0xC94 &&
                  offsetof(Ts3nCounters, vfRtLotIdxRebuildInJob) == 0xCA4 && offsetof(Ts3nCounters, vfRtPendingInJob) == 0xCB4 &&
                  offsetof(Ts3nCounters, vfRtTeardownInJob) == 0xCD0 && offsetof(Ts3nCounters, g11Plans) == 0xCD4 &&
                  offsetof(Ts3nCounters, g11End) == 0xCE4 && offsetof(Ts3nCounters, tgReady) == 0xCFC &&
                  offsetof(Ts3nCounters, tgMode) == 0xD00 && offsetof(Ts3nCounters, vfRtGraveHeld) == 0xD14 &&
                  sizeof(Ts3nCounters) >= 838 * 4, "v25 layout");
    Ts3nCounters& k = ts3native_counters;
    memset(&k.rgUnlocked2, 0, offsetof(Ts3nCounters, vfRtGraveHeld) + 4 - offsetof(Ts3nCounters, rgUnlocked2));
    const DWORD me = GetCurrentThreadId(), inf = 0xFFFFFFFF;
    static DWORD simTid;
    simTid = me;
    g_rtSimTidSlot = &simTid;
    const DWORD savedWorker = g_workerTid;
    const LockAcquireFn savedLock = g_lockOrig;
    g_lockOrig = RgFakeAcquire;
    g_rgRelOrig = RgFakeRelease;
    InitializeCriticalSection(&g_rgFakeCs);
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
    k.rgReady = 7;
    g_rgLock = g_rgLockObj;
    k.rgOwnerTid = 0;
    DWORD stack[24] = {};
    auto reset = [&] { t_rg.n = 0; t_rg.readers = 0; };

    // gate sites 13-15 count in the v25 arrays
    RgEnter(12, &stack[20]);
    assert(k.rgUnlocked2[0] == 1 && k.rgJob2[0] == 0 && t_rg.n == 1 && stack[20] == (DWORD)(uintptr_t)RgLeaveThunk);
    reset();
    g_callerTid = 1234;
    RgEnter(14, &stack[20]);
    assert(k.rgUnlocked2[2] == 1 && k.rgJob2[2] == 1);
    g_callerTid = 0;
    reset();
    // gate site 16 (index 15, IC_World_FindPlacesOnShoreline, crash K) counts in v108, not in the v25 arrays
    static_assert(offsetof(Ts3nCounters, rgUnlocked3) == 0x45C4 && offsetof(Ts3nCounters, rgJob3) == 0x45C8 &&
                  sizeof(Ts3nCounters) == 6112 * 4, "counters v108 layout after v107 (docs/registry.md)");
    assert(kRgSites[15].addr == 0x0078C440 && kRgSites[15].len == 6);
    k.rgUnlocked3 = k.rgJob3 = 0;
    RgEnter(15, &stack[20]);
    assert(k.rgUnlocked3 == 1 && k.rgJob3 == 0 && t_rg.n == 1 && k.rgUnlocked2[2] == 1);
    reset();
    g_callerTid = 1234;
    RgEnter(15, &stack[20]);
    assert(k.rgUnlocked3 == 2 && k.rgJob3 == 1 && k.rgJob2[2] == 1);
    g_callerTid = 0;
    reset();

    // G11: plan, then a gate entry closes the window (min(plan, gap) <= both); a second touch adds nothing
    assert(G11PlanBegin());
    Sleep(2);
    G11PlanEnd(true);
    assert(k.g11Plans == 0 && t_g11.end.QuadPart != 0);
    Sleep(1);
    RgEnter(13, &stack[20]);
    reset();
    assert(k.g11Plans == 1 && k.g11End[G11_GATE13] == 1 && k.g11PlanUs >= 1000 && k.g11MovableUs > 0 &&
           k.g11MovableUs <= k.g11PlanUs && k.g11MovableUs <= k.g11GapUs);
    G11Touch(G11_LOCK, 1);
    assert(k.g11Plans == 1);
    // nested plans: the inner one opens nothing and touches inside a plan are ignored
    assert(G11PlanBegin() && G11PlanBegin());
    G11Touch(G11_LOCK, 1);
    G11PlanEnd(true);
    assert(t_g11.end.QuadPart == 0 && k.g11Plans == 1);
    G11PlanEnd(true);
    assert(t_g11.end.QuadPart != 0);
    // an outermost +0x918 acquire closes it; a recursive one does not count again
    assert(LockAcquireHook(g_rgLockObj, NULL, &inf) == 1 && k.g11End[G11_LOCK] == 1 && k.g11Plans == 2);
    assert(LockReleaseHook(g_rgLockObj, NULL) == 0);
    // the next plan closes an open window
    G11PlanBegin(); G11PlanEnd(true);
    G11PlanBegin();
    assert(k.g11End[G11_PLAN] == 1 && k.g11Plans == 3);
    G11PlanEnd(true);
    // destroys and quadtree writes on the sim thread close it too (rtsites.cpp)
    k.rtReady = 1;
    RtEnter(7, &stack[20]);
    assert(k.g11End[G11_DESTROY] == 1 && k.g11Plans == 4 && k.vfRtDestroyInJob == 0 && k.vfRtDestroyOffSim == 0);
    G11PlanBegin(); G11PlanEnd(true);
    RtEnter(3, &stack[20]);
    assert(k.g11End[G11_QTWRITE] == 1 && k.g11Plans == 5 && k.vfRtQtWriteInJob == 0);
    // not the sim thread: no plan window; an unlocked gate entry counts as off-sim
    simTid = me + 1;
    assert(!G11PlanBegin());
    RgEnter(0, &stack[20]);
    assert(k.vfRtGateOffSim == 1);
    reset();

    // the worker: quadtree leaves outside a reader are bare; readers push frames (not gate frames)
    g_workerTid = me;
    RtEnter(5, &stack[20]);
    assert(k.vfRtQtBare == 1 && t_rg.n == 0);
    stack[20] = 0x111; stack[15] = 0x222; stack[8] = 0x333;
    RtEnter(0, &stack[20]);
    RtEnter(1, &stack[15]);  // nested reader: one outermost read
    assert(k.vfRtQtReads == 1 && t_rg.n == 2 && t_rg.readers == 2 && stack[15] == (DWORD)(uintptr_t)RgLeaveThunk);
    RtEnter(6, &stack[10]);
    assert(k.vfRtQtBare == 1);
    const LONG unl0 = k.rgUnlocked[0];
    RgEnter(0, &stack[8]);  // a gate entry inside a reader is still outermost; the worker is not off-sim
    assert(k.rgUnlocked[0] == unl0 + 1 && k.vfRtGateOffSim == 1 && t_rg.n == 3 && t_rg.readers == 2);
    assert(RgLeave() == 0x333 && RgLeave() == 0x222 && t_rg.readers == 1 && RgLeave() == 0x111 && t_rg.n == 0 && t_rg.readers == 0);
    RtEnter(0, &stack[10]);
    RtEnter(5, &stack[12]);  // above the reader's slot: the reader frame is dead, so this read is bare
    assert(k.vfRtQtBare == 2 && t_rg.n == 0 && t_rg.readers == 0);
    // pending lot updates applied by the worker: ecx = world (pushad slot -2), P = [world+0x95C], flag P+0x10E
    static u8 world[0xA00], planner[0x200];
    *(u8**)(world + 0x95C) = planner;
    stack[18] = (DWORD)(uintptr_t)world;
    RtEnter(9, &stack[20]);
    assert(k.vfRtPendingInJob == 0);
    planner[0x10E] = 1;
    RtEnter(9, &stack[20]);
    assert(k.vfRtPendingInJob == 1);
    RtEnter(8, &stack[20]);
    assert(k.vfRtLotGridWorker == 1);
    g_workerTid = savedWorker;
    RtEnter(9, &stack[20]);  // not the worker: not counted
    RtEnter(8, &stack[20]);
    assert(k.vfRtPendingInJob == 1 && k.vfRtLotGridWorker == 1);

    // during a job (Mode 1 caller set): every in-job counter; outside a job none
    simTid = me;
    g_callerTid = 1234;
    for (int i = 2; i < RT_N; i++) if (i != 5 && i != 6) RtEnter(i, &stack[20]);
    assert(k.vfRtQtWriteInJob == 3 && k.vfRtDestroyInJob == 1 && k.vfRtDestroyOffSim == 0 && k.vfRtLotIdxRebuildInJob[1] == 1 &&
           k.vfRtDrainSave == 1 && k.vfRtDrainBrd == 1 && k.vfRtDrainLotSave == 1 && k.vfRtDebugSkip == 1 && k.vfRtTeardownInJob == 3);
    g_callerTid = 0;
    for (int i = 2; i < RT_N; i++) if (i != 5 && i != 6) RtEnter(i, &stack[20]);
    assert(k.vfRtQtWriteInJob == 3 && k.vfRtDestroyInJob == 1 && k.vfRtDrainSave == 1 && k.vfRtTeardownInJob == 3);
    simTid = me + 1;
    RtEnter(7, &stack[20]);
    assert(k.vfRtDestroyOffSim == 1);
    simTid = me;
    k.rtReady = 0;
    RtEnter(7, &stack[20]);  // not installed: nothing
    assert(k.vfRtDestroyOffSim == 1 && k.vfRtDestroyInJob == 1);

    // every stub pushes its own site index and jumps through its own trampoline slot
    for (int i = 0; i < RT_N; i++) {
        const u8* s = (const u8*)(void*)kRtStubs[i];
        const DWORD slot = (DWORD)(uintptr_t)&g_rtTramp[i];
        bool push = false, jmp = false;
        for (int j = 0; j < 40; j++) {
            push |= s[j] == 0x6A && s[j + 1] == i;
            jmp |= s[j] == 0xFF && s[j + 1] == 0x25 && *(const DWORD*)(s + j + 2) == slot;
        }
        assert(push && jmp);
    }

    // Verify counted inside the owner stub (docs/68 §6 rule 3): the owner table, then a timed thread probe through RtEnter
    assert(!VfOwnerSlot(0x00939420) && !VfOwnerSlot(0x00628A90));
    g_rtTramp[7] = (void*)1;
    g_rgTramp[12] = (void*)1;
    assert(VfOwnerSlot(0x00939420) == &g_rtVf[7] && VfOwnerSlot(0x00628A90) == &g_rgVf[12] && !VfOwnerSlot(0x00939421));
    g_rtTramp[7] = g_rgTramp[12] = NULL;
    int probe = -1;
    for (int i = 0; i < VF_N; i++) if (kVf[i].at == 0x00939420) probe = i;
    assert(probe >= 0 && kVf[probe].thr != VF_NONE);
    k.vfMode = 1;
    k.rtReady = 1;
    g_rtVf[7] = probe + 1;
    const LONG sims = VfSlot(kVf[probe].thr)[1];
    stack[20] = 0x777;
    RtEnter(7, &stack[20]);
    assert(VfSlot(kVf[probe].thr)[1] == sims + 1 && stack[20] == (DWORD)(uintptr_t)VfLeaveThunk && VfRealRet(&stack[20]) == 0x777);
    assert(VfLeave() == 0x777 && t_vf.n == 0 && VfRealRet(&stack[20]) == (DWORD)(uintptr_t)VfLeaveThunk);
    g_rtVf[7] = 0;
    k.vfMode = 0;
    k.rtReady = 0;

    reset();
    memset(&t_g11, 0, sizeof(t_g11));
    k.rgReady = 0;
    g_rgLock = NULL;
    g_lockOrig = savedLock;
    DeleteCriticalSection(&g_rgFakeCs);
}

// --- H6 S3b + M1: gate enforced during an async job, borrow rule, closures, START limits (routegate.cpp, rtsites.cpp) ---
static LONG __fastcall RgTryAcquire(void* lock, void*, const DWORD* deadline) {  // 0x4E16F0 with deadline 0 = try once
    if (*deadline == 0) {
        if (!TryEnterCriticalSection(&g_rgFakeCs)) return -2;
    } else {
        EnterCriticalSection(&g_rgFakeCs);
    }
    return ++*(LONG*)((u8*)lock + 0x20);
}
static HANDLE g_enfHeld;
static DWORD WINAPI EnfWorker(LPVOID) {  // plays the worker: holds the job's lock for 30 ms, then ends the job
    static const DWORD inf = 0xFFFFFFFF;
    LockAcquireHook(g_rgLockObj, NULL, &inf);
    SetEvent(g_enfHeld);
    Sleep(30);
    LockReleaseHook(g_rgLockObj, NULL);
    InterlockedExchange(&g_async.state, A_IDLE);
    SetEvent(g_asyncDone);
    return 0;
}
static HANDLE EnfStartWorker() {
    ResetEvent(g_enfHeld);
    ResetEvent(g_asyncDone);
    InterlockedExchange(&g_async.state, A_RUNNING);
    HANDLE w = CreateThread(NULL, 0, EnfWorker, NULL, 0, NULL);
    WaitForSingleObject(g_enfHeld, INFINITE);
    return w;
}
static void EnfJoin(HANDLE w) {
    WaitForSingleObject(w, INFINITE);
    CloseHandle(w);
}

static void TestRouteEnforce() {
    static_assert(offsetof(Ts3nCounters, rgEnforce) == 0xD18 && offsetof(Ts3nCounters, asyncRefusedM1) == 0xD30 &&
                  offsetof(Ts3nCounters, vfRtDestroyInInvoke) == 0xD48 && offsetof(Ts3nCounters, rtClosing) == 0xD54 &&
                  sizeof(Ts3nCounters) >= 854 * 4, "v26 layout");
    Ts3nCounters& k = ts3native_counters;
    memset(&k.rgEnforce, 0, offsetof(Ts3nCounters, rtClosing) + 4 - offsetof(Ts3nCounters, rgEnforce));
    const DWORD me = GetCurrentThreadId(), inf = 0xFFFFFFFF;
    static DWORD simTid;
    simTid = me;
    g_rtSimTidSlot = &simTid;
    const DWORD savedWorker = g_workerTid;
    const LockAcquireFn savedLock = g_lockOrig;
    const HANDLE savedDone = g_asyncDone, savedInv = g_invReady;
    g_lockOrig = RgTryAcquire;
    g_rgRelOrig = RgFakeRelease;
    InitializeCriticalSection(&g_rgFakeCs);
    InitializeCriticalSection(&g_qtCs);
    g_asyncDone = CreateEventW(NULL, FALSE, FALSE, NULL);
    g_invReady = CreateEventW(NULL, FALSE, FALSE, NULL);
    g_enfHeld = CreateEventW(NULL, TRUE, FALSE, NULL);
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
    k.rgReady = 7;
    k.rtReady = RT_ALL;
    k.rgEnforce = 1;
    k.rgOwnerTid = 0;
    g_workerTid = me + 7;  // nobody: the test thread is sim and owner
    g_async.owner = me;
    g_async.lock = g_rgLockObj;
    LONG& count = *(LONG*)(g_rgLockObj + 0x20);
    DWORD stack[24] = {};
    auto reset = [&] { t_rg.n = 0; t_rg.readers = 0; g_rgOffSimOpen = 0; };

    // M1: each refusal reason, then allowed with the world's lock
    static u8 world[0xA00], planner[0x200], wm[0x200];
    *(u8**)(world + 0x95C) = planner;
    *(u8**)(world + 0x964) = wm;
    g_rgLock = world + 0x918;
    void* lock = NULL;
    k.rgEnforce = 0;
    assert(!RtM1Allows(&lock) && k.asyncRefusedM1[0] == 1);
    k.rgEnforce = 1;
    k.rtBusy = 1;
    assert(!RtM1Allows(&lock) && k.asyncRefusedM1[1] == 1);
    k.rtBusy = 0;
    k.rtClosing = 1;
    assert(!RtM1Allows(&lock) && k.asyncRefusedM1[2] == 1);
    k.rtClosing = 0;
    planner[0x10E] = 1;
    assert(!RtM1Allows(&lock) && k.asyncRefusedM1[3] == 1);
    planner[0x10E] = 0;
    wm[0x1CC] = 1;
    assert(!RtM1Allows(&lock) && k.asyncRefusedM1[4] == 1);
    wm[0x1CC] = 0;
    k.rgOwnerTid = (LONG)me;
    assert(!RtM1Allows(&lock) && k.asyncRefusedM1[5] == 1);
    k.rgOwnerTid = 0;
    assert(RtM1Allows(&lock) && lock == world + 0x918 && !lock == false);
    g_rgLock = NULL;
    assert(!RtM1Allows(&lock) && k.asyncRefusedM1[0] == 2);  // no lock learned in this world yet
    g_rgLock = g_rgLockObj;

    // borrow: the owner inside a callback for the job takes no real lock, and releases without one
    g_async.state = A_RUNNING;
    g_inInvoke = 1;
    assert(LockAcquireHook(g_rgLockObj, NULL, &inf) == 2 && count == 0 && k.rgBorrows == 1 && t_borrow == 1);
    assert(LockReleaseHook(g_rgLockObj, NULL) == 1 && t_borrow == 0 && count == 0);
    RgEnter(0, &stack[20]);
    assert(k.rgBorrows == 2 && t_rg.n == 1 && t_rg.f[0].rel == RG_REL_OFFSIM && k.rgGateWaits == 0);
    reset();
    RtEnter(7, &stack[20]);  // a destroy there cannot wait for the job
    assert(k.vfRtDestroyInInvoke == 1 && k.vfRtDestroyWaitUs == 0);
    g_inInvoke = 0;
    g_async.state = A_IDLE;

    // a gate entry during a job waits for the worker's hold (pumping), holds +0x918 for the call, the leave releases it
    HANDLE w = EnfStartWorker();
    stack[20] = 0x444;
    RgEnter(0, &stack[20]);
    assert(k.rgGateWaits == 1 && k.rgGateWaitUs >= 10000 && count == 1 && t_rg.n == 1 && t_rg.f[0].rel == (RG_REL_LOCK | RG_REL_OFFSIM));
    assert(k.rgOwnerTid == (LONG)me);
    RgEnter(1, &stack[16]);  // nested: no second acquire
    assert(count == 1 && k.rgGateWaits == 1 && t_rg.n == 2);
    RgLeave();
    assert(RgLeave() == 0x444 && count == 0 && t_rg.n == 0 && g_rgOffSimOpen == 0 && k.rgOwnerTid == 0);
    EnfJoin(w);
    assert(g_async.state == A_IDLE);
    // no job running: the gate takes nothing
    g_async.state = A_IDLE;
    RgEnter(0, &stack[20]);
    assert(count == 0 && t_rg.f[0].rel == RG_REL_OFFSIM && k.rgGateWaits == 1);
    reset();
    // gate site 16 (crash K, IC_World_FindPlacesOnShoreline): during a job the IC waits and holds +0x918 for the whole call;
    // with no job its frame keeps job starts out (g_rgOffSimOpen) until it returns
    w = EnfStartWorker();
    stack[20] = 0x666;
    RgEnter(15, &stack[20]);
    assert(k.rgGateWaits == 2 && count == 1 && t_rg.n == 1 && t_rg.f[0].rel == (RG_REL_LOCK | RG_REL_OFFSIM));
    assert(RgLeave() == 0x666 && count == 0 && t_rg.n == 0 && g_rgOffSimOpen == 0);
    EnfJoin(w);
    RgEnter(15, &stack[20]);
    assert(count == 0 && t_rg.f[0].rel == RG_REL_OFFSIM && g_rgOffSimOpen == 1 && k.rgGateWaits == 2);
    reset();

    // destroy and save during a job wait for it; the save refuses START until it returns
    w = EnfStartWorker();
    RtEnter(7, &stack[20]);
    assert(g_async.state == A_IDLE && k.vfRtDestroyWaitUs >= 10000 && t_rg.n == 1 && g_rgOffSimOpen == 1);
    EnfJoin(w);
    w = EnfStartWorker();
    stack[20] = 0x555;
    RtEnter(11, &stack[20]);
    assert(g_async.state == A_IDLE && k.vfRtDrainWaitUs >= 10000 && k.rtBusy == 1 && t_rg.n == 1);
    EnfJoin(w);
    g_rgLock = world + 0x918;
    assert(!RtM1Allows(&lock) && k.asyncRefusedM1[1] == 2);
    g_rgLock = g_rgLockObj;
    assert(RgLeave() == 0x555 && k.rtBusy == 0);
    // a thread that really holds +0x918 does not wait (the worker cannot be in its job body)
    g_async.state = A_RUNNING;
    k.rgOwnerTid = (LONG)me;
    RtEnter(7, &stack[20]);
    assert(g_async.state == A_RUNNING);
    k.rgOwnerTid = 0;

    // the routing debug frame is skipped during a job only
    assert(RtEnter(14, &stack[20]) == 1);
    g_async.state = A_IDLE;
    assert(RtEnter(14, &stack[20]) == 0);

    // quadtree: a worker reader holds g_qtCs until its leave; a mutator during a job takes it, outside a job not
    g_workerTid = me;
    stack[20] = 0x666;
    RtEnter(0, &stack[20]);
    assert(t_rg.n == 1 && t_rg.f[0].rel == RG_REL_QT && g_qtCs.RecursionCount == 1);
    assert(RgLeave() == 0x666 && g_qtCs.RecursionCount == 0);
    // 0.11.1: the extra readers lock too; a bare leaf read on the worker takes the lock as a fallback (and still counts)
    stack[20] = 0x676;
    RtEnter(20, &stack[20]);
    assert(t_rg.n == 1 && t_rg.f[0].rel == RG_REL_QT && g_qtCs.RecursionCount == 1);
    assert(RgLeave() == 0x676 && g_qtCs.RecursionCount == 0);
    const LONG bare0 = k.vfRtQtBare;
    stack[20] = 0x686;
    RtEnter(5, &stack[20]);
    assert(k.vfRtQtBare == bare0 + 1 && t_rg.n == 1 && g_qtCs.RecursionCount == 1);
    assert(RgLeave() == 0x686 && g_qtCs.RecursionCount == 0);
    g_workerTid = me + 7;
    g_async.state = A_RUNNING;
    stack[20] = 0x777;
    RtEnter(2, &stack[20]);
    assert(t_rg.n == 1 && g_qtCs.RecursionCount == 1);
    assert(RgLeave() == 0x777 && g_qtCs.RecursionCount == 0);
    g_async.state = A_IDLE;
    RtEnter(2, &stack[20]);
    assert(t_rg.n == 1 && g_rgOffSimOpen == 1 && g_qtCs.RecursionCount == 0);

    // travel / quit ICs set the closing flag; world init clears it and forgets the lock
    RtEnter(18, &stack[20]);
    RtEnter(19, &stack[20]);
    assert(k.rtClosing == 1 && k.vfRtClosingDrains == 2);
    RtEnter(15, &stack[20]);
    assert(k.rtClosing == 0 && g_rgLock == NULL);
    g_rgLock = g_rgLockObj;

    // an off-sim thread inside a gate leaf is published until its leave
    simTid = me + 1;
    stack[20] = 0x888;
    RgEnter(0, &stack[20]);
    assert(g_rgOffSimOpen == 1 && t_rg.f[0].rel == RG_REL_OFFSIM);
    assert(RgLeave() == 0x888 && g_rgOffSimOpen == 0);
    simTid = me;

    // a gate frame that held +0x918 and was left without returning is released at the next entry
    LockAcquireHook(g_rgLockObj, NULL, &inf);
    RgPush(t_rg, &stack[20], 0, RG_REL_LOCK, g_rgLockObj);
    RgEnter(0, &stack[22]);
    assert(k.rgDropLocked == 1 && count == 0 && t_rg.n == 1);
    reset();

    g_async.state = A_IDLE;
    g_async.lock = NULL;
    g_async.owner = 0;
    k.rgEnforce = 0;
    k.rgReady = 0;
    k.rtReady = 0;
    k.rgOwnerTid = 0;
    g_rgLock = NULL;
    g_workerTid = savedWorker;
    g_lockOrig = savedLock;
    CloseHandle(g_asyncDone);
    CloseHandle(g_invReady);
    CloseHandle(g_enfHeld);
    g_asyncDone = savedDone;
    g_invReady = savedInv;
    DeleteCriticalSection(&g_qtCs);
    DeleteCriticalSection(&g_rgFakeCs);
}

// --- SeedWaitKeys (2026-10-22, 16 keys since 2026-11-04): the A/B keys keep their slots when the other 16 slots are full ---
static void TestSeedWaitKeys() {
    Ts3nCounters& k = ts3native_counters;
    LONG key[32], n[32], us[32];
    memcpy(key, k.blockKey, sizeof(key)), memcpy(n, k.blockN, sizeof(n)), memcpy(us, k.blockUs, sizeof(us));
    const LONG other = k.blockOther;
    memset(k.blockKey, 0, sizeof(k.blockKey)), memset(k.blockN, 0, sizeof(k.blockN)), memset(k.blockUs, 0, sizeof(k.blockUs));
    SeedWaitKeys();
    for (int i = 16; i < 32; i++) WaitNote(0x00700000 + i, 1);  // 16 other keys fill the table
    assert(k.blockKey[14] == 0x00755990 && k.blockKey[15] == 0x0075A64C);
    WaitNote(0x00833593, 7);  // first hit after the table is full: still timed
    assert(k.blockKey[3] == 0x00833593 && k.blockN[3] == 1 && k.blockUs[3] == 7 && k.blockOther == other);
    ts3native_blockmax[3] = 0, WaitNote(0x00833593, 7), WaitNote(0x00833593, 3);  // per-key maximum: the longer wait stays
    assert(ts3native_blockmax[3] == 7 && k.blockUs[3] == 17);
    WaitNote(0x00846D21, 5);
    assert(k.blockKey[5] == 0x00846D21 && k.blockUs[5] == 5);
    WaitNote(0x00999999, 5);  // an unseeded key: count only
    assert(k.blockOther == other + 1);
    memcpy(k.blockKey, key, sizeof(key)), memcpy(k.blockN, n, sizeof(n)), memcpy(k.blockUs, us, sizeof(us));
    k.blockOther = other;
}

// --- vfG11CloseBy (0.11.2): each G11 close is keyed by its closer; a +0x918 acquire inside a steering query by its caller ---
static void TestG11CloseBy() {
    static_assert(offsetof(Ts3nCounters, g11CloseKey) == 0xD58 && offsetof(Ts3nCounters, g11CloseN) == 0xDD8 &&
                  offsetof(Ts3nCounters, g11CloseLostUs) == 0xE58 && offsetof(Ts3nCounters, g11CloseOther) == 0xED8 &&
                  sizeof(Ts3nCounters) >= 951 * 4, "v27 layout");
    static_assert(offsetof(Ts3nCounters, asyncSkipped) == 0xEDC && offsetof(Ts3nCounters, asyncQueueMax) == 0xEE8 &&
                  sizeof(Ts3nCounters) >= 955 * 4, "v28 layout");
    static_assert(offsetof(Ts3nCounters, blockKey) == 0xEEC && offsetof(Ts3nCounters, blockOther) == 0x106C &&
                  sizeof(Ts3nCounters) >= 1052 * 4, "v29 layout");
    static_assert(offsetof(Ts3nCounters, discReady) == 0x1070 && offsetof(Ts3nCounters, discOverflow) == 0x109C &&
                  sizeof(Ts3nCounters) >= 1064 * 4, "v30 layout");
    Ts3nCounters& k = ts3native_counters;
    memset(&k.g11CloseKey, 0, offsetof(Ts3nCounters, g11CloseOther) + 4 - offsetof(Ts3nCounters, g11CloseKey));
    const DWORD me = GetCurrentThreadId(), inf = 0xFFFFFFFF;
    static DWORD simTid;
    simTid = me;
    g_rtSimTidSlot = &simTid;
    const LockAcquireFn savedLock = g_lockOrig;
    g_lockOrig = RgTryAcquire;
    g_rgRelOrig = RgFakeRelease;
    InitializeCriticalSection(&g_rgFakeCs);
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
    k.rgReady = 7;
    k.rtReady = RT_ALL;
    g_rgLock = g_rgLockObj;
    memset(&t_g11, 0, sizeof(t_g11));
    DWORD stack[24] = {};

    // a plan, then the steering query called from 0x85FE10 takes +0x918: the close is keyed by that call site
    G11PlanBegin();
    Sleep(1);
    G11PlanEnd(true);
    stack[20] = 0x0085FE10;
    RtEnter(23, &stack[20]);
    assert(t_steerRet == 0x0085FE10 && t_rg.n == 1);
    RtEnter(24, &stack[16]);  // nested gather: keeps the outer caller
    assert(t_steerRet == 0x0085FE10 && t_rg.n == 1);
    assert(LockAcquireHook(g_rgLockObj, NULL, &inf) == 1);
    assert(k.g11CloseKey[0] == 0x0085FE10 && k.g11CloseN[0] == 1 && k.g11CloseLostUs[0] >= 0);
    LockReleaseHook(g_rgLockObj, NULL);
    assert(RgLeave() == 0x0085FE10 && t_steerRet == 0 && t_rg.n == 0);
    // AvSite (avoidro.cpp) runs the query for the retargeted call at 0x846D1C: keyed 0x846D21, not by AvSite's own return
    t_avSite = true;
    RtEnter(23, &stack[20]);
    assert(t_steerRet == 0x00846D21 && t_rg.n == 1);
    RgLeave();
    assert(t_steerRet == 0 && t_rg.n == 0);
    t_avSite = false;
    {  // a call-site stub (IcStub, GvStub) calls the query for the game: keyed by its game site, then the stub's key goes
        const StubKey sk(0x00755990);
        RtEnter(23, &stack[20]);
        assert(t_steerRet == 0x00755990 && t_rg.n == 1);
        RgLeave();
    }
    assert(t_stubKey == 0 && t_steerRet == 0 && t_rg.n == 0);
    // outside a steering query an acquire is keyed by Lock_Acquire's return address (here: inside this test)
    G11PlanBegin();
    G11PlanEnd(true);
    LockAcquireHook(g_rgLockObj, NULL, &inf);
    LockReleaseHook(g_rgLockObj, NULL);
    assert(k.g11CloseKey[1] != 0 && k.g11CloseKey[1] != 0x0085FE10 && k.g11CloseN[1] == 1);
    // a gate entry and a repeat of the first key
    G11PlanBegin();
    G11PlanEnd(true);
    RgEnter(0, &stack[20]);
    t_rg.n = 0;
    t_rg.readers = 0;
    assert(k.g11CloseKey[2] == 0x01000000 && k.g11CloseN[2] == 1);
    G11PlanBegin();
    G11PlanEnd(true);
    G11Touch(G11_LOCK, 0x0085FE10);
    assert(k.g11CloseN[0] == 2 && k.g11CloseKey[3] == 0);
    // a full table: the 33rd key counts as other
    for (DWORD i = 0; i < 40; i++) {
        G11PlanBegin();
        G11PlanEnd(true);
        G11Touch(G11_LOCK, 0x00900000 + i);
    }
    assert(k.g11CloseKey[31] != 0 && k.g11CloseOther == 40 - 29);

    k.rgReady = 0;
    k.rtReady = 0;
    k.rgOwnerTid = 0;
    k.rgAcquires = 0;
    g_rgLock = NULL;
    g_lockOrig = savedLock;
    memset(&t_g11, 0, sizeof(t_g11));
    DeleteCriticalSection(&g_rgFakeCs);
}

// --- design D (0.12.0, docs/84 §2.4): discourage footprints deferred while a job runs, applied in order later ---
static char g_dLog[64];
static int g_dLogN;
static u8 g_dObjA[16], g_dObjB[16];
static bool g_dNestOnce;
static u8 __fastcall FakeDiscAdd(void* world, void*, void* obj, void* points, int, int, int) {
    const u8* const* v = (const u8* const*)points;
    g_dLog[g_dLogN++] = obj == g_dObjA ? 'A' : 'B';
    assert(v && ((v[1] - v[0]) / 16 < 3 || *(const float*)v[0] == 1.5f));  // 3+ points: the copy made at defer time
    assert(((uintptr_t)v[0] & 15) == 0);  // the original reads them with movaps (0xAB88BD)
    if (g_dNestOnce) { g_dNestOnce = false; DiscRemoveHook(world, NULL, g_dObjB); }  // an event handler during the drain
    return 1;
}
static u8 __fastcall FakeDiscRemove(void* world, void*, void* obj) {
    g_dLog[g_dLogN++] = obj == g_dObjA ? 'a' : 'b';
    return world ? 1 : 0;
}
static void* __cdecl FakeSvc() { return (void*)1; }
static bool g_dObjBGone;
static u8 __fastcall FakeFindRef(void*, void*, DWORD lo, DWORD, void** out) {  // same convention as 0x9395A0 (ret 0xC)
    void* o = lo == 0xA ? (void*)g_dObjA : g_dObjBGone ? NULL : (void*)g_dObjB;
    if (o) *out = o;
    return o != NULL;
}
static int g_dReleases;
static void __fastcall FakeObjRelease(void** pp, void*) { assert(*pp); g_dReleases++; }

static void TestDiscourage() {
    Ts3nCounters& k = ts3native_counters;
    memset(&k.discReady, 0, offsetof(Ts3nCounters, discOverflow) + 4 - offsetof(Ts3nCounters, discReady));
    const DWORD me = GetCurrentThreadId();
    const LockAcquireFn savedLock = g_lockOrig;
    g_lockOrig = RgTryAcquire;
    g_rgRelOrig = RgFakeRelease;
    InitializeCriticalSection(&g_rgFakeCs);
    const HANDLE savedDone = g_asyncDone, savedInv = g_invReady;
    g_asyncDone = CreateEventW(NULL, FALSE, FALSE, NULL);
    g_invReady = CreateEventW(NULL, FALSE, FALSE, NULL);
    g_discAddOrig = FakeDiscAdd;
    g_discRemoveOrig = FakeDiscRemove;
    g_objSvc = FakeSvc;
    g_findRef = FakeFindRef;
    g_objRelease = FakeObjRelease;
    static u8 world[0xA00];
    world[0x974] = 1;
    g_rgLock = world + 0x918;
    *(DWORD*)(g_dObjA + 8) = 0xA;
    *(DWORD*)(g_dObjB + 8) = 0xB;
    k.discMode = 1;
    g_async.owner = me;
    __declspec(align(16)) float pts[3][4] = {{1.5f}, {2}, {3}};
    u8* vec[3] = {(u8*)pts, (u8*)pts + 48, (u8*)pts + 48};
    g_dLogN = 0;

    // no job: straight to the original
    assert(DiscAddHook(world, NULL, g_dObjA, vec, 0, 1, 0) == 1 && g_dLogN == 1 && k.discDeferredAdd == 0);
    // a job runs: recorded (points copied), answered 1, nothing applied yet; early outs still go to the original
    g_async.state = A_RUNNING;
    assert(DiscAddHook(world, NULL, g_dObjA, vec, 0, 1, 0) == 1 && DiscRemoveHook(world, NULL, g_dObjB) == 1);
    assert(DiscRemoveHook(world, NULL, g_dObjA) == 1 && g_dLogN == 1 && k.discDeferredAdd == 1 && k.discDeferredRemove == 2);
    pts[0][0] = 9;  // the record holds its own copy
    u8* shortVec[3] = {(u8*)pts, (u8*)pts + 32, (u8*)pts + 32};
    assert(DiscAddHook(world, NULL, g_dObjA, shortVec, 0, 0, 0) == 1 && g_dLogN == 2);  // fewer than 3 points: original
    DiscPoll();  // a job still runs and the records are fresh: nothing applied
    assert(g_dLogN == 2 && g_discCount == 3);
    // job over: the next poll applies all three in order with the object's reference held
    g_async.state = A_IDLE;
    g_dLogN = 0;
    DiscPoll();
    assert(g_dLogN == 3 && memcmp(g_dLog, "Aba", 3) == 0 && g_discCount == 0 && k.discApplied == 3 && g_dReleases == 3);
    assert(*(LONG*)(g_rgLockObj + 0x20) == 0);
    // an object gone by then is dropped; a record from another world is dropped
    pts[0][0] = 1.5f;
    g_async.state = A_RUNNING;
    DiscRemoveHook(world, NULL, g_dObjB);
    DiscAddHook(world, NULL, g_dObjA, vec, 0, 0, 0);
    g_async.state = A_IDLE;
    g_dObjBGone = true;
    g_dLogN = 0;
    DiscPoll();
    assert(g_dLogN == 1 && g_dLog[0] == 'A' && k.discDropped == 1);
    g_dObjBGone = false;
    g_async.state = A_RUNNING;
    DiscRemoveHook(world, NULL, g_dObjA);
    static u8 world2[0xA00];
    world2[0x974] = 1;
    g_rgLock = world2 + 0x918;  // travel: a new world before the drain
    g_async.state = A_IDLE;
    g_dLogN = 0;
    DiscPoll();
    assert(g_dLogN == 0 && k.discDropped == 2);
    g_rgLock = world + 0x918;
    // a record made during a drain (event handler) is applied in the same drain, after the one that made it
    g_async.state = A_RUNNING;
    DiscAddHook(world, NULL, g_dObjA, vec, 0, 0, 0);
    g_async.state = A_IDLE;
    g_dNestOnce = true;
    g_dLogN = 0;
    DiscPoll();
    assert(g_dLogN == 2 && memcmp(g_dLog, "Ab", 2) == 0 && k.discNested == 1 && g_discCount == 0);
    // a record older than the age limit is applied even while a job runs (the drain waits for it: here none runs)
    g_async.state = A_RUNNING;
    DiscRemoveHook(world, NULL, g_dObjA);
    g_disc[g_discHead].t -= kDiscMaxAgeMs;
    g_async.state = A_IDLE;  // WaitAsyncDone returns at once in the test
    DiscPoll();
    assert(g_discCount == 0 && k.discForced == 0);  // no job ran: a plain drain
    // mode 0: never deferred
    k.discMode = 0;
    g_async.state = A_RUNNING;
    g_dLogN = 0;
    assert(DiscRemoveHook(world, NULL, g_dObjA) == 1 && g_dLogN == 1 && g_discCount == 0);
    g_async.state = A_IDLE;

    g_async.owner = 0;
    g_rgLock = NULL;
    k.discMode = 0;
    k.rgOwnerTid = 0;
    k.rgAcquires = 0;
    g_lockOrig = savedLock;
    CloseHandle(g_asyncDone);
    CloseHandle(g_invReady);
    g_asyncDone = savedDone;
    g_invReady = savedInv;
    DeleteCriticalSection(&g_rgFakeCs);
}

// --- fix D-X (0.16.0, docs/84 §7.2): destroy-gate apply, dead-id sweep, pointer check ---
static u8 g_xObj[3][16];          // objects with ids 0xA, 0xB, 0xC
static u8* g_xLive[3];            // what the id hash answers per id (NULL = gone, another object = reused id)
static char g_xLog[64];
static int g_xLogN, g_xSweeps, g_xKeyFinds;
static bool g_xSweepHit, g_xKeyHit;
static DWORD g_xSweepArgs[4];
static int XIdx(const void* o) { return o == g_xObj[0] ? 0 : o == g_xObj[1] ? 1 : 2; }
static u8 __fastcall XAdd(void*, void*, void* obj, void*, int, int, int) { g_xLog[g_xLogN++] = (char)('A' + XIdx(obj)); return 1; }
static u8 __fastcall XRemove(void*, void*, void* obj) { g_xLog[g_xLogN++] = (char)('a' + XIdx(obj)); return 1; }
static u8 __fastcall XFindRef(void*, void*, DWORD lo, DWORD hi, void** out) {
    assert(hi == 0 && lo >= 0xA && lo <= 0xC);
    u8* o = g_xLive[lo - 0xA];
    if (o) *out = o;
    return o != NULL;
}
static u8 __fastcall XLotRemove(void* p, void*, DWORD lotLo, DWORD lotHi, DWORD idLo, DWORD idHi) {
    assert(p != NULL);
    g_xSweeps++;
    g_xSweepArgs[0] = lotLo, g_xSweepArgs[1] = lotHi, g_xSweepArgs[2] = idLo, g_xSweepArgs[3] = idHi;
    return g_xSweepHit;
}
static void __fastcall XKeyFind(void* map, void*, DWORD* out, const DWORD* id) {
    g_xKeyFinds++;
    const DWORD* buckets = *(const DWORD* const*)((u8*)map + 4);
    out[0] = g_xKeyHit && id[0] == 0xA ? 0x1234 : buckets[*(const DWORD*)((u8*)map + 8)];
    out[1] = 0;
}

static void TestDiscourageDX() {
    static_assert(offsetof(Ts3nCounters, discDestroyMode) == 0x135C && offsetof(Ts3nCounters, discSweepMode) == 0x1360 &&
                  offsetof(Ts3nCounters, discDropAdd) == 0x1364 && offsetof(Ts3nCounters, discDropRemove) == 0x1368 &&
                  offsetof(Ts3nCounters, discDestroyApplied) == 0x136C && offsetof(Ts3nCounters, discDestroyInInvoke) == 0x1370 &&
                  offsetof(Ts3nCounters, discDeadSweep) == 0x1374 && offsetof(Ts3nCounters, discDeadHit) == 0x1378 &&
                  offsetof(Ts3nCounters, discDeadKeyMap) == 0x137C && offsetof(Ts3nCounters, discIdMismatch) == 0x1380 &&
                  sizeof(Ts3nCounters) >= 1249 * 4, "v34 layout");
    Ts3nCounters& k = ts3native_counters;
    memset(&k.discReady, 0, offsetof(Ts3nCounters, discOverflow) + 4 - offsetof(Ts3nCounters, discReady));
    memset((void*)&k.discDestroyMode, 0, offsetof(Ts3nCounters, discIdMismatch) + 4 - offsetof(Ts3nCounters, discDestroyMode));
    const DWORD me = GetCurrentThreadId();
    const LockAcquireFn savedLock = g_lockOrig;
    g_lockOrig = RgTryAcquire;
    g_rgRelOrig = RgFakeRelease;
    InitializeCriticalSection(&g_rgFakeCs);
    const HANDLE savedDone = g_asyncDone, savedInv = g_invReady;
    g_asyncDone = CreateEventW(NULL, FALSE, FALSE, NULL);
    g_invReady = CreateEventW(NULL, FALSE, FALSE, NULL);
    g_discAddOrig = XAdd;
    g_discRemoveOrig = XRemove;
    g_objSvc = FakeSvc;
    g_findRef = XFindRef;
    g_objRelease = FakeObjRelease;
    g_lotRemove = XLotRemove;
    g_keyFind = XKeyFind;
    static u8 world[0xA00];
    static u8 planner[0x200];
    static DWORD buckets[2] = {0, 0x5E5E5E5E};  // one bucket, then the end sentinel
    world[0x974] = 1;
    *(u8**)(world + 0x95C) = planner;
    *(DWORD**)(world + 0x8BC) = buckets;
    *(DWORD*)(world + 0x8C0) = 1;
    *(DWORD*)(world + 0x8C4) = 1;  // the lock-key map holds entries
    g_rgLock = world + 0x918;
    for (int i = 0; i < 3; i++) { *(DWORD*)(g_xObj[i] + 8) = 0xA + i; *(DWORD*)(g_xObj[i] + 0xC) = 0; g_xLive[i] = g_xObj[i]; }
    u8* const A = g_xObj[0], * const B = g_xObj[1], * const C = g_xObj[2];
    k.discMode = 1;
    k.discDestroyMode = 1;
    k.discSweepMode = 1;
    g_async.owner = me;
    __declspec(align(16)) float pts[3][4] = {{1.5f}, {2}, {3}};
    u8* vec[3] = {(u8*)pts, (u8*)pts + 48, (u8*)pts + 48};
    const int rel0 = g_dReleases;

    // 1. destroy of A with A's and others' records queued: A's Removes applied in order, A's Add dropped, B and C keep order
    g_async.state = A_RUNNING;
    DiscRemoveHook(world, NULL, A);
    DiscAddHook(world, NULL, B, vec, 0, 0, 0);
    DiscAddHook(world, NULL, A, vec, 0, 0, 0);
    DiscRemoveHook(world, NULL, C);
    DiscRemoveHook(world, NULL, A);
    assert(g_discCount == 5 && g_xLogN == 0);
    g_async.state = A_IDLE;  // RT_DESTROY waited for the job
    DiscOnDestroy(0xA, 0, true);
    assert(g_xLogN == 2 && memcmp(g_xLog, "aa", 2) == 0 && k.discDestroyApplied == 2 && k.discDropAdd == 1 && k.discDropped == 1);
    assert(g_discCount == 2 && k.rgOwnerTid == 0 && !g_discDraining);
    DiscPoll();
    assert(g_xLogN == 4 && memcmp(g_xLog, "aaBc", 4) == 0 && g_discCount == 0 && k.discApplied == 4 && g_dReleases - rel0 == 4);
    // a destroy of an object with no record, and one with an empty queue: nothing happens
    g_async.state = A_RUNNING;
    DiscRemoveHook(world, NULL, B);
    DiscOnDestroy(0xC, 0, true);
    assert(g_discCount == 1 && g_xLogN == 4);
    g_async.state = A_IDLE;
    DiscPoll();
    DiscOnDestroy(0xA, 0, true);
    assert(g_xLogN == 5 && g_xLog[4] == 'b' && g_discCount == 0);

    // 2. destroy inside a job callback: skipped and counted, the record stays; at the drain the object is gone: dead-id sweep
    g_async.state = A_RUNNING;
    DiscRemoveHook(world, NULL, A);
    DiscAddHook(world, NULL, A, vec, 0, 0, 0);
    DiscOnDestroy(0xA, 0, false);
    assert(k.discDestroyInInvoke == 1 && g_discCount == 2 && k.discDestroyApplied == 2);
    g_xLive[0] = NULL;  // DestroyById ran
    g_xSweepHit = true;
    g_xKeyHit = true;
    g_async.state = A_IDLE;
    DiscPoll();
    assert(g_xLogN == 5 && g_discCount == 0 && k.discDropRemove == 1 && k.discDropAdd == 2 && k.discDropped == 3);
    assert(k.discDeadSweep == 1 && k.discDeadHit == 1 && k.discDeadKeyMap == 1 && g_xSweeps == 1 && g_xKeyFinds == 1);
    assert(g_xSweepArgs[0] == 0 && g_xSweepArgs[1] == 0 && g_xSweepArgs[2] == 0xA && g_xSweepArgs[3] == 0);  // lot 0 = all lots
    assert(world[0x994] == 0 && k.discDropRemove == k.discDeadSweep);  // T-D5
    // an empty lock-key map is not looked up; a sweep that finds nothing is not a hit
    world[0x994] = 1;
    *(DWORD*)(world + 0x8C4) = 0;
    g_xSweepHit = false;
    g_async.state = A_RUNNING;
    g_xLive[0] = A;
    DiscRemoveHook(world, NULL, A);
    g_xLive[0] = NULL;
    g_async.state = A_IDLE;
    DiscPoll();
    assert(k.discDeadSweep == 2 && k.discDeadHit == 1 && g_xKeyFinds == 1 && world[0x994] == 0 && k.discDropRemove == k.discDeadSweep);

    // 3. switches off: no gate, no sweep (the A/B baseline of the bug: discDropRemove > discDeadSweep)
    k.discDestroyMode = 0;
    k.discSweepMode = 0;
    g_xLive[0] = A;
    g_async.state = A_RUNNING;
    DiscRemoveHook(world, NULL, A);
    DiscOnDestroy(0xA, 0, true);
    assert(g_discCount == 1 && k.discDestroyApplied == 2);
    g_xLive[0] = NULL;
    g_async.state = A_IDLE;
    DiscPoll();
    assert(k.discDropRemove == 3 && k.discDeadSweep == 2 && g_xSweeps == 2);
    k.discDestroyMode = 1;
    k.discSweepMode = 1;

    // 4. pointer check: the id now names another object (reused): dropped, counted, released, never swept or applied
    g_xLive[0] = A;
    g_async.state = A_RUNNING;
    DiscRemoveHook(world, NULL, B);
    DiscAddHook(world, NULL, B, vec, 0, 0, 0);
    g_xLive[1] = C;
    const int rel1 = g_dReleases;
    g_async.state = A_IDLE;
    DiscPoll();
    assert(k.discIdMismatch == 2 && g_xLogN == 5 && g_xSweeps == 2 && g_dReleases - rel1 == 2 && k.discDropRemove == 3);
    // the destroy gate does the same check: a reused id is not applied there either
    g_async.state = A_RUNNING;
    g_xLive[1] = B;
    DiscRemoveHook(world, NULL, B);
    g_xLive[1] = C;
    DiscOnDestroy(0xB, 0, true);
    assert(k.discIdMismatch == 3 && k.discDestroyApplied == 2 && g_discCount == 0 && g_xLogN == 5);
    g_xLive[1] = B;
    g_async.state = A_IDLE;

    // 5. through the RT_DESTROY stub: DestroyById's arguments (idLo, idHi) are slot[1], slot[2]
    static DWORD simTid;
    simTid = me;
    g_rtSimTidSlot = &simTid;
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
    k.rtReady = RT_ALL;
    k.rgEnforce = 1;
    g_async.state = A_RUNNING;
    DiscRemoveHook(world, NULL, C);
    DiscRemoveHook(world, NULL, B);
    g_async.state = A_IDLE;
    DWORD stack[24] = {};
    stack[20] = 0x999;
    stack[21] = 0xC;
    stack[22] = 0;
    RtEnter(7, &stack[20]);
    assert(k.discDestroyApplied == 3 && g_discCount == 1 && g_xLogN == 6 && g_xLog[5] == 'c' && g_rgOffSimOpen == 1);
    assert(RgLeave() == 0x999 && g_rgOffSimOpen == 0);
    DiscPoll();
    assert(g_discCount == 0 && g_xLogN == 7 && g_xLog[6] == 'b');
    k.rtReady = 0;
    k.rgEnforce = 0;

    g_async.owner = 0;
    g_rgLock = NULL;
    k.discMode = 0;
    k.rgOwnerTid = 0;
    k.rgAcquires = 0;
    g_lockOrig = savedLock;
    CloseHandle(g_asyncDone);
    CloseHandle(g_invReady);
    g_asyncDone = savedDone;
    g_invReady = savedInv;
    DeleteCriticalSection(&g_rgFakeCs);
}

// --- design U (docs/89 §10, counters v74): UpdateSimFootprint 0x833560 deferred into design D's queue ---
// log: U records 'P'/'Q'/'R' (enable 1) or 'p'/'q'/'r' (enable 0) for A/B/C, D's 'A'.. / 'a'.. as in the D-X fakes
static bool g_uShould = true, g_uNestOnce;
static DWORD g_uForce[512];
static void __fastcall FakeUsf(void* world, void*, void* obj, DWORD enable, DWORD force, DWORD arg) {
    assert(arg == 7);
    g_uForce[g_xLogN] = force;
    g_xLog[g_xLogN++ & 63] = (char)((enable & 0xFF ? 'P' : 'p') + XIdx(obj));
    if (g_uNestOnce) {  // a nested call under +0x918 (as Locomotion_Notify from a discourage apply): runs at once
        g_uNestOnce = false;
        assert(ts3native_counters.rgOwnerTid == (LONG)GetCurrentThreadId());
        UsfHook((u8*)world, NULL, g_xObj[2], 1, 1, 7);
    }
}
static u8 __fastcall FakeUsfShould(void*, void*, void* obj) { assert(obj); return g_uShould; }

static void TestFootprintDefer() {
    static_assert(offsetof(Ts3nCounters, usfReady) == 0x3174 && offsetof(Ts3nCounters, usfMode) == 0x3178 &&
                  offsetof(Ts3nCounters, usfApplied) == 0x31A4 && offsetof(Ts3nCounters, usfApplyInJob) == 0x31C4 &&
                  sizeof(Ts3nCounters) == 6112 * 4, "v74 layout (without v52, v58)");
    Ts3nCounters& k = ts3native_counters;
    memset(&k.discReady, 0, offsetof(Ts3nCounters, discOverflow) + 4 - offsetof(Ts3nCounters, discReady));
    memset((void*)&k.discDestroyMode, 0, offsetof(Ts3nCounters, discIdMismatch) + 4 - offsetof(Ts3nCounters, discDestroyMode));
    memset(&k.usfReady, 0, sizeof(Ts3nCounters) - offsetof(Ts3nCounters, usfReady));
    const DWORD me = GetCurrentThreadId();
    const LockAcquireFn savedLock = g_lockOrig;
    g_lockOrig = RgTryAcquire;
    g_rgRelOrig = RgFakeRelease;
    InitializeCriticalSection(&g_rgFakeCs);
    const HANDLE savedDone = g_asyncDone, savedInv = g_invReady;
    g_asyncDone = CreateEventW(NULL, FALSE, FALSE, NULL);
    g_invReady = CreateEventW(NULL, FALSE, FALSE, NULL);
    g_discAddOrig = XAdd;
    g_discRemoveOrig = XRemove;
    g_usfOrig = FakeUsf;
    g_usfShould = FakeUsfShould;
    g_objSvc = FakeSvc;
    g_findRef = XFindRef;
    g_objRelease = FakeObjRelease;
    g_lotRemove = XLotRemove;
    g_keyFind = XKeyFind;
    static u8 world[0xA00];
    static u8 planner[0x200];
    static DWORD buckets[2] = {0, 0x5E5E5E5E};
    world[0x974] = 1;
    *(u8**)(world + 0x95C) = planner;
    *(DWORD**)(world + 0x8BC) = buckets;
    *(DWORD*)(world + 0x8C0) = 1;
    *(DWORD*)(world + 0x8C4) = 0;  // nothing parked
    g_xKeyHit = false;
    g_rgLock = world + 0x918;
    for (int i = 0; i < 3; i++) { *(DWORD*)(g_xObj[i] + 8) = 0xA + i; *(DWORD*)(g_xObj[i] + 0xC) = 0; g_xLive[i] = g_xObj[i]; }
    u8* const A = g_xObj[0], * const B = g_xObj[1], * const C = g_xObj[2];
    k.discMode = 1;
    k.discDestroyMode = 1;
    k.discSweepMode = 1;
    k.usfMode = 1;
    g_async.owner = me;
    g_xLogN = 0;
    auto log = [](const char* s) { return g_xLogN == (int)strlen(s) && memcmp(g_xLog, s, g_xLogN) == 0; };
    auto forced = [](int from) {  // every U apply in the log ran with force 1
        for (int i = from; i < g_xLogN; i++)
            if (((g_xLog[i] >= 'P' && g_xLog[i] <= 'R') || (g_xLog[i] >= 'p' && g_xLog[i] <= 'r')) && g_uForce[i] != 1) return false;
        return true;
    };

    // 1. no job: the original at once, with the caller's force
    UsfHook(world, NULL, A, 1, 0, 7);
    assert(log("P") && g_uForce[0] == 0 && k.usfCalls == 1 && k.usfDeferred == 0 && g_discCount == 0);
    // 2. a job runs: recorded in one FIFO with D's records, applied in order at the next drain with force 1
    g_xLogN = 0;
    g_async.state = A_RUNNING;
    UsfHook(world, NULL, A, 0, 0, 7);
    DiscRemoveHook(world, NULL, B);
    UsfHook(world, NULL, B, 1, 1, 7);
    g_uShould = false;
    UsfHook(world, NULL, C, 1, 0, 7);  // the early out says no: nothing to do, no record
    g_uShould = true;
    assert(g_xLogN == 0 && g_discCount == 3 && k.usfDeferred == 2 && k.usfEarlyOut == 1 && k.usfInJob == 3);
    DiscPoll();  // job still runs, records fresh: kept
    assert(g_xLogN == 0 && g_discCount == 3);
    g_async.state = A_IDLE;  // job end: the next drain point applies them
    DiscPoll();
    assert(log("pbQ") && forced(0) && g_discCount == 0 && k.usfApplied == 2 && k.discApplied == 1 && k.rgOwnerTid == 0);
    // 3. collapse: an exact repeat is dropped at once; at the drain only the object's last U record runs
    g_xLogN = 0;
    g_async.state = A_RUNNING;
    UsfHook(world, NULL, A, 1, 0, 7);
    UsfHook(world, NULL, A, 1, 0, 7);  // same call again
    UsfHook(world, NULL, A, 0, 0, 7);
    UsfHook(world, NULL, B, 0, 0, 7);
    UsfHook(world, NULL, A, 1, 0, 7);
    assert(k.usfDup == 1 && g_discCount == 4);
    g_async.state = A_IDLE;
    DiscPoll();
    assert(log("qP") && k.usfCollapsed == 2 && k.usfApplied == 4);
    // a D record between two U records of the object stops the collapse (the Remove must see the first apply)
    g_xLogN = 0;
    g_async.state = A_RUNNING;
    UsfHook(world, NULL, A, 1, 0, 7);
    DiscRemoveHook(world, NULL, A);
    UsfHook(world, NULL, A, 0, 0, 7);
    g_async.state = A_IDLE;
    DiscPoll();
    assert(log("Pap") && k.usfCollapsed == 2);
    // 4. order: with A's record queued, A's next call queues even with no job (and in mode 0); B runs at once
    g_xLogN = 0;
    g_async.state = A_RUNNING;
    DiscRemoveHook(world, NULL, A);
    g_async.state = A_IDLE;
    UsfHook(world, NULL, A, 1, 0, 7);
    k.usfMode = 0;
    UsfHook(world, NULL, A, 0, 0, 7);
    k.usfMode = 1;
    UsfHook(world, NULL, B, 1, 0, 7);
    assert(log("Q") && k.usfOrderDefer == 2 && g_discCount == 3);
    DiscPoll();
    assert(log("Qap") && g_discCount == 0);  // P collapsed into p
    // a D call with no job drains the queue (U records first), then runs
    g_xLogN = 0;
    g_async.state = A_RUNNING;
    UsfHook(world, NULL, A, 0, 0, 7);
    g_async.state = A_IDLE;
    DiscRemoveHook(world, NULL, B);
    assert(log("pb") && g_discCount == 0);
    // 5. pass-through: mode 0 with nothing queued, nested under +0x918 (from an apply), another world, a job callback
    g_xLogN = 0;
    g_async.state = A_RUNNING;
    k.usfMode = 0;
    UsfHook(world, NULL, A, 1, 0, 7);
    k.usfMode = 1;
    k.rgOwnerTid = (LONG)me;
    UsfHook(world, NULL, A, 0, 0, 7);
    k.rgOwnerTid = 0;
    g_inInvoke = 1;
    UsfHook(world, NULL, A, 1, 0, 7);
    g_inInvoke = 0;
    static u8 world2[0xA00];
    world2[0x974] = 1;
    UsfHook(world2, NULL, A, 0, 0, 7);
    assert(log("PpPp") && k.usfPass == 3 && g_discCount == 0);
    UsfHook(world, NULL, B, 0, 0, 7);  // recorded; its apply makes a nested call for C, which runs inside it
    g_uNestOnce = true;
    g_async.state = A_IDLE;
    DiscPoll();
    assert(log("PpPpqR") && k.usfPass == 4 && g_discCount == 0);
    // 6. parked object with nothing queued: the original (it waits); with records queued it queues, no collapse
    g_xLogN = 0;
    *(DWORD*)(world + 0x8C4) = 1;
    g_xKeyHit = true;  // id 0xA is in the lock-key map
    g_async.state = A_RUNNING;
    UsfHook(world, NULL, A, 1, 0, 7);
    assert(log("P") && g_uForce[0] == 1 && k.usfParkedPass == 1 && g_discCount == 0);
    DiscRemoveHook(world, NULL, A);
    UsfHook(world, NULL, A, 1, 0, 7);
    UsfHook(world, NULL, A, 0, 0, 7);
    g_async.state = A_IDLE;
    DiscPoll();
    assert(log("PaPp") && k.usfCollapsed == 3);
    *(DWORD*)(world + 0x8C4) = 0;
    g_xKeyHit = false;
    // 7. destroy while queued (D-X): the object's U records applied in order, its Add dropped, others kept
    g_xLogN = 0;
    g_async.state = A_RUNNING;
    __declspec(align(16)) float pts[3][4] = {{1.5f}, {2}, {3}};
    u8* vec[3] = {(u8*)pts, (u8*)pts + 48, (u8*)pts + 48};
    UsfHook(world, NULL, A, 0, 0, 7);
    UsfHook(world, NULL, B, 1, 0, 7);
    DiscAddHook(world, NULL, A, vec, 0, 0, 0);
    UsfHook(world, NULL, A, 1, 0, 7);
    g_async.state = A_IDLE;  // RT_DESTROY waited for the job
    DiscOnDestroy(0xA, 0, true);
    assert(log("pP") && k.usfDestroyApplied == 2 && g_discCount == 1 && k.rgOwnerTid == 0);
    DiscPoll();
    assert(log("pPQ") && g_discCount == 0);
    // 8. gone at apply: a disable sweeps the dead id (T-U3), an enable is dropped, a reused id is dropped
    g_xLogN = 0;
    g_async.state = A_RUNNING;
    UsfHook(world, NULL, A, 0, 0, 7);
    UsfHook(world, NULL, B, 1, 0, 7);
    UsfHook(world, NULL, C, 1, 0, 7);
    g_xLive[0] = NULL, g_xLive[1] = NULL, g_xLive[2] = A;
    const int sweeps0 = g_xSweeps;
    g_async.state = A_IDLE;
    DiscPoll();
    assert(g_xLogN == 0 && k.usfGoneEnable0 == 1 && k.usfSwept == 1 && g_xSweeps == sweeps0 + 1 && g_xSweepArgs[2] == 0xA);
    assert(k.usfGoneEnable1 == 1 && k.usfIdMismatch == 1 && g_discCount == 0);
    for (int i = 0; i < 3; i++) g_xLive[i] = g_xObj[i];
    // 9. queue full: with no Add queued it drains (Remove and U are managed-free), then runs the call
    g_async.state = A_RUNNING;
    UsfHook(world, NULL, C, 0, 0, 7);
    for (int i = 1; i < kDiscCap; i++) UsfHook(world, NULL, B, i & 1, 0, 7);
    assert(g_discCount == kDiscCap);
    g_async.state = A_IDLE;
    g_xLogN = 0;
    UsfHook(world, NULL, C, 1, 0, 7);  // C has a record: queues for order, finds the queue full
    assert(log("rQR") && k.usfFullDrain == 1 && g_discCount == 0);  // B's 255 collapse into the last (enable 1)
    // with an Add queued it cannot drain here: the call runs now (counted as out of order)
    g_async.state = A_RUNNING;
    DiscAddHook(world, NULL, A, vec, 0, 0, 0);
    for (int i = 1; i < kDiscCap; i++) UsfHook(world, NULL, B, i & 1, 0, 7);
    g_xLogN = 0;
    UsfHook(world, NULL, C, 1, 0, 7);
    assert(log("R") && k.usfFullPass == 1 && g_discCount == kDiscCap);
    g_async.state = A_IDLE;
    DiscPoll();
    assert(log("RAQ") && g_discCount == 0);
    // tripwires
    assert(k.usfApplyInJob == 0);
    TIMING_ASSERT("DeferFootprint record age < 300 ms", k.usfAgeMaxMs < 300);

    g_async.owner = 0;
    g_async.state = A_IDLE;
    g_rgLock = NULL;
    k.discMode = 0;
    k.usfMode = 0;
    k.rgOwnerTid = 0;
    k.rgAcquires = 0;
    g_lockOrig = savedLock;
    CloseHandle(g_asyncDone);
    CloseHandle(g_invReady);
    g_asyncDone = savedDone;
    g_invReady = savedInv;
    DeleteCriticalSection(&g_rgFakeCs);
}

// --- design U phase 2 (docs/89 §12, counters v94): ObjectFootprintFromTool 0x8334A0 / UpdateObjectLot 0x82D280 into D's queue,
// flushed before the private-footprint ICs. log: FromTool 'F'/'G'/'H' (lower case = world +0x978 was 0 at the apply), lot
// 'L'/'M'/'N', private remove 'X'/'Y'/'Z', private add 'U'/'V'/'W', U and D as above ---
static u8* g_f2World;
static void __fastcall FakeFt(void* world, void*, void* obj) {
    assert(world == g_f2World);
    g_xLog[g_xLogN++ & 63] = (char)((((u8*)world)[0x978] ? 'F' : 'f') + XIdx(obj));
}
static void __fastcall FakeUol(void*, void*, void* obj) { g_xLog[g_xLogN++ & 63] = (char)('L' + XIdx(obj)); }
static u8 __fastcall FakePfRemove(void*, void*, void* obj, DWORD flag) {
    assert(flag == 1);
    g_xLog[g_xLogN++ & 63] = (char)('X' + XIdx(obj));
    return 1;
}
static u8 __fastcall FakePfAdd(void*, void*, void* obj, void* pts, DWORD flag) {
    assert(pts && flag == 0);
    g_xLog[g_xLogN++ & 63] = (char)('U' + XIdx(obj));
    return 1;
}
static DWORD WINAPI F2JobEnd(void*) {  // the running job ends a little later (a forced flush waits for it)
    Sleep(30);
    g_async.state = A_IDLE;
    SetEvent(g_asyncDone);
    return 0;
}

static void TestFootprintDefer2() {
    static_assert(offsetof(Ts3nCounters, fp2Mode) == 0x3FB4 && offsetof(Ts3nCounters, fp2Deferred) == 0x3FBC &&
                  offsetof(Ts3nCounters, fp2Forced) == 0x3FDC && offsetof(Ts3nCounters, fp2ApplyInJob) == 0x3FE8 &&
                  sizeof(Ts3nCounters) == 6112 * 4, "counters v94 layout after v92 (docs/registry.md)");
    Ts3nCounters& k = ts3native_counters;
    memset(&k.discReady, 0, offsetof(Ts3nCounters, discOverflow) + 4 - offsetof(Ts3nCounters, discReady));
    memset((void*)&k.discDestroyMode, 0, offsetof(Ts3nCounters, discIdMismatch) + 4 - offsetof(Ts3nCounters, discDestroyMode));
    memset(&k.usfReady, 0, offsetof(Ts3nCounters, usfApplyInJob) + 4 - offsetof(Ts3nCounters, usfReady));
    memset(&k.fp2Mode, 0, sizeof(Ts3nCounters) - offsetof(Ts3nCounters, fp2Mode));
    const DWORD me = GetCurrentThreadId();
    const LockAcquireFn savedLock = g_lockOrig;
    g_lockOrig = RgTryAcquire;
    g_rgRelOrig = RgFakeRelease;
    InitializeCriticalSection(&g_rgFakeCs);
    const HANDLE savedDone = g_asyncDone, savedInv = g_invReady;
    g_asyncDone = CreateEventW(NULL, FALSE, FALSE, NULL);
    g_invReady = CreateEventW(NULL, FALSE, FALSE, NULL);
    g_discAddOrig = XAdd;
    g_discRemoveOrig = XRemove;
    g_usfOrig = FakeUsf;
    g_usfShould = FakeUsfShould;
    g_ftOrig = FakeFt;
    g_uolOrig = FakeUol;
    g_pfRemove = FakePfRemove;
    g_pfAdd = FakePfAdd;
    g_objSvc = FakeSvc;
    g_findRef = XFindRef;
    g_objRelease = FakeObjRelease;
    g_lotRemove = XLotRemove;
    g_keyFind = XKeyFind;
    static u8 world[0xA00];
    static u8 planner[0x200];
    static DWORD buckets[2] = {0, 0x5E5E5E5E};
    g_f2World = world;
    world[0x974] = 1;
    *(u8**)(world + 0x95C) = planner;
    *(DWORD**)(world + 0x8BC) = buckets;
    *(DWORD*)(world + 0x8C0) = 1;
    *(DWORD*)(world + 0x8C4) = 0;
    g_xKeyHit = false;
    g_uShould = true;
    g_rgLock = world + 0x918;
    for (int i = 0; i < 3; i++) { *(DWORD*)(g_xObj[i] + 8) = 0xA + i; *(DWORD*)(g_xObj[i] + 0xC) = 0; g_xLive[i] = g_xObj[i]; }
    u8* const A = g_xObj[0], * const B = g_xObj[1], * const C = g_xObj[2];
    k.discMode = 1;
    k.discDestroyMode = 1;
    k.discSweepMode = 1;
    k.usfMode = 1;
    k.fp2Mode = 1;
    g_async.owner = me;
    g_xLogN = 0;
    auto log = [](const char* s) { return g_xLogN == (int)strlen(s) && memcmp(g_xLog, s, g_xLogN) == 0; };
    auto settled = [&]() {  // no lost update: every record queued was applied or dropped
        return g_discCount == 0 && k.fp2Deferred[0] == k.fp2Applied[0] + k.fp2Dropped[0] &&
               k.fp2Deferred[1] == k.fp2Applied[1] + k.fp2Dropped[1];
    };

    // 1. no job: both run at once
    FtHook(world, NULL, A);
    UolHook(world, NULL, B);
    assert(log("fM") && g_discCount == 0 && k.fp2Deferred[0] == 0 && k.fp2Deferred[1] == 0);
    // 2. a job runs: queued in one FIFO with U and D records, applied in order at the next drain; FromTool's early out runs
    // at call time; the call-time +0x978 (the caller's bracket) is set for the apply and restored after
    g_xLogN = 0;
    g_async.state = A_RUNNING;
    UsfHook(world, NULL, A, 0, 0, 7);
    world[0x978] = 1;  // 0x9470B0's bracket around the call
    FtHook(world, NULL, A);
    world[0x978] = 0;
    UolHook(world, NULL, B);
    DiscRemoveHook(world, NULL, C);
    g_uShould = false;
    FtHook(world, NULL, C);  // the early out says no: no record (the original would not take the lock)
    g_uShould = true;
    assert(g_xLogN == 0 && g_discCount == 4 && k.fp2Deferred[0] == 1 && k.fp2Deferred[1] == 1);
    g_async.state = A_IDLE;
    DiscPoll();
    assert(log("pFMc") && world[0x978] == 0 && settled() && k.fp2Applied[0] == 1 && k.fp2Applied[1] == 1 && k.rgOwnerTid == 0);
    // 3. order per object: with A's record queued, A's later FromTool / lot / U calls queue even with no job and in mode 0;
    // an object with nothing queued runs at once
    g_xLogN = 0;
    g_async.state = A_RUNNING;
    UolHook(world, NULL, A);
    g_async.state = A_IDLE;
    k.fp2Mode = 0;
    FtHook(world, NULL, A);
    k.fp2Mode = 1;
    UsfHook(world, NULL, A, 1, 0, 7);
    UolHook(world, NULL, A);
    FtHook(world, NULL, B);
    assert(log("g") && g_discCount == 4);
    DiscPoll();
    assert(log("gLfPL") && settled());
    // a U record right before a phase 2 record of the object is not collapsed into it (it is not a U record)
    g_xLogN = 0;
    g_async.state = A_RUNNING;
    UsfHook(world, NULL, A, 1, 0, 7);
    FtHook(world, NULL, A);
    UsfHook(world, NULL, A, 0, 0, 7);
    g_async.state = A_IDLE;
    DiscPoll();
    assert(log("Pfp") && settled());
    // 4. duplicates: the same call right behind is dropped; another bracket byte, another kind or a record between is not
    g_xLogN = 0;
    g_async.state = A_RUNNING;
    FtHook(world, NULL, A);
    FtHook(world, NULL, A);  // dup
    world[0x978] = 1;
    FtHook(world, NULL, A);  // other bracket: kept
    world[0x978] = 0;
    UolHook(world, NULL, A);
    UolHook(world, NULL, A);  // dup
    UolHook(world, NULL, B);
    UolHook(world, NULL, A);  // B between does not count: A's newest record is the same call
    assert(k.fp2Dup == 3 && g_discCount == 4);
    g_async.state = A_IDLE;
    DiscPoll();
    assert(log("fFLM") && settled());
    // 5. pass-through: +0x918 owned, job callback, another world, the world's batch mode (FromTool only)
    g_xLogN = 0;
    g_async.state = A_RUNNING;
    k.rgOwnerTid = (LONG)me;
    FtHook(world, NULL, A);
    k.rgOwnerTid = 0;
    g_inInvoke = 1;
    UolHook(world, NULL, A);
    g_inInvoke = 0;
    static u8 world2[0xA00];
    world2[0x974] = 1;
    g_f2World = world2;
    FtHook(world2, NULL, A);
    g_f2World = world;
    world[0x97E] = 1, world[0x976] = 1;
    FtHook(world, NULL, B);  // batch mode: the bracket byte decides the early outs inside, so today's call
    UolHook(world, NULL, B);  // the lot update does not read it: queued
    world[0x97E] = 0, world[0x976] = 0;
    assert(log("fLfg") && k.fp2Pass == 4 && g_discCount == 1);
    g_async.state = A_IDLE;
    DiscPoll();
    assert(log("fLfgM") && settled());
    // 6. flush before a reader: a private-footprint IC call for an object with records drains the queue first, waiting for
    // the running job; for an object with nothing queued, or with +0x918 owned, it only runs the call
    g_xLogN = 0;
    g_async.state = A_RUNNING;
    FtHook(world, NULL, A);
    UsfHook(world, NULL, B, 1, 0, 7);
    assert(PfRemoveSite(world, NULL, C, 1) == 1 && log("Z") && g_discCount == 2 && k.fp2Forced == 0);
    k.rgOwnerTid = (LONG)me;
    assert(PfRemoveSite(world, NULL, A, 1) == 1 && log("ZX") && g_discCount == 2);
    k.rgOwnerTid = 0;
    const LONG forced0 = k.discForced;
    CloseHandle(CreateThread(NULL, 0, F2JobEnd, NULL, 0, NULL));
    assert(PfRemoveSite(world, NULL, A, 1) == 1);
    assert(log("ZXfQX") && settled() && k.fp2Forced == 1 && k.discForced == forced0 + 1 && g_async.state == A_IDLE);
    g_xLogN = 0;
    g_async.state = A_RUNNING;
    UolHook(world, NULL, B);
    g_async.state = A_IDLE;
    __declspec(align(16)) float pts[3][4] = {{1.5f}, {2}, {3}};
    assert(PfAddSite(world, NULL, B, pts, 0) == 1 && log("MV") && settled() && k.fp2Forced == 2);
    // 7. destroy while queued (D-X): the object's phase 2 records applied in order before it, others kept
    g_xLogN = 0;
    g_async.state = A_RUNNING;
    FtHook(world, NULL, A);
    UolHook(world, NULL, B);
    UolHook(world, NULL, A);
    g_async.state = A_IDLE;
    DiscOnDestroy(0xA, 0, true);
    assert(log("fL") && g_discCount == 1 && k.rgOwnerTid == 0);
    DiscPoll();
    assert(log("fLM") && settled());
    // 8. gone or reused at apply: dropped and counted
    g_xLogN = 0;
    g_async.state = A_RUNNING;
    FtHook(world, NULL, A);
    UolHook(world, NULL, B);
    g_xLive[0] = NULL, g_xLive[1] = A;
    g_async.state = A_IDLE;
    DiscPoll();
    assert(g_xLogN == 0 && k.fp2Dropped[0] == 1 && k.fp2Dropped[1] == 1 && settled());
    for (int i = 0; i < 3; i++) g_xLive[i] = g_xObj[i];
    // 9. queue full: drained (no Add queued) then the call, counted as forced; with an Add queued the call runs now
    g_async.state = A_RUNNING;
    for (int i = 0; i < kDiscCap; i++) (i & 1 ? UolHook : FtHook)(world, NULL, B);
    assert(g_discCount == kDiscCap);
    g_async.state = A_IDLE;
    g_xLogN = 0;
    const LONG forced1 = k.fp2Forced;
    FtHook(world, NULL, B);
    assert(g_xLogN == kDiscCap + 1 && g_discCount == 0 && k.fp2Forced == forced1 + 1 && settled());
    g_async.state = A_RUNNING;
    u8* vec[3] = {(u8*)pts, (u8*)pts + 48, (u8*)pts + 48};
    DiscAddHook(world, NULL, A, vec, 0, 0, 0);
    for (int i = 1; i < kDiscCap; i++) (i & 1 ? UolHook : FtHook)(world, NULL, B);
    g_xLogN = 0;
    FtHook(world, NULL, B);  // B's newest record is a lot update: not a duplicate
    assert(log("g") && k.fp2FullPass == 1 && g_discCount == kDiscCap);
    g_async.state = A_IDLE;
    DiscPoll();
    assert(g_xLogN == kDiscCap + 1 && settled());
    // tripwires
    assert(k.fp2ApplyInJob == 0 && k.usfApplyInJob == 0);
    TIMING_ASSERT("DeferFootprint2 record age < 300 ms", k.fp2AgeMaxMs < 300);

    g_async.owner = 0;
    g_async.state = A_IDLE;
    g_rgLock = NULL;
    k.discMode = 0;
    k.usfMode = 0;
    k.fp2Mode = 0;
    k.rgOwnerTid = 0;
    k.rgAcquires = 0;
    g_lockOrig = savedLock;
    g_pfRemove = (PfRemoveFn)(uintptr_t)PFR_FN;
    g_pfAdd = (PfAddFn)(uintptr_t)PFA_FN;
    CloseHandle(g_asyncDone);
    CloseHandle(g_invReady);
    g_asyncDone = savedDone;
    g_invReady = savedInv;
    DeleteCriticalSection(&g_rgFakeCs);
}

// --- LostRemovalFix and GhostSweep (docs/89 §16, §17, counters v172). g_lrMap[object] = one bit per lot holding its footprint, so
// each path can be compared with the order vanilla has ---
static DWORD g_lrMap[3], g_lrLot[3];
static int g_lrCore;
static u8 LrOff(DWORD i, DWORD lot) {
    const u8 hit = (u8)((g_lrMap[i] >> lot) & 1);
    g_lrMap[i] &= ~(1u << lot);
    return hit;
}
static u8 __fastcall LrCore(void* world, void*, DWORD idLo, DWORD idHi, DWORD lotLo, DWORD lotHi) {
    assert(world == g_f2World && idHi == 0 && lotHi == 0x55);
    g_lrCore++;
    return LrOff(idLo - 0xA, lotLo);
}
static void __fastcall LrUolOrig(void*, void*, void* obj) { LrOff(XIdx(obj), g_lrLot[XIdx(obj)]); }  // reads the lot at the apply
static void __fastcall LrUof(void*, void*, void* obj) { g_lrMap[XIdx(obj)] |= 1u << g_lrLot[XIdx(obj)]; }
static unsigned __int64 __cdecl LrObjLot(const void* o) { return (unsigned __int64)0x55 << 32 | g_lrLot[XIdx(o)]; }
static unsigned __int64 __cdecl LrPfKey(int, int, DWORD lo, DWORD hi) { return (unsigned __int64)hi << 32 | lo | 1; }

struct LrNode { DWORD lo, hi; u8* fme; DWORD pad; LrNode* next; };
static_assert(offsetof(LrNode, fme) == 8 && offsetof(LrNode, next) == 0x10, "the game's node, docs/89 §16");
static LrNode g_lrNodes[24];
static u8 g_lrFme[24][0x14], g_lrFp[24][0x40];
static LrNode* g_lrBk[9];
static int g_lrNodeN, g_lrRemoves;
static DWORD g_lrLive[4][2], g_lrParked[2], g_lrRemoved[2];
static void LrEntry(DWORD lo, DWORD hi, DWORD cls, signed char type, bool q = true, bool idOk = true, u8* badFp = NULL) {
    const int i = g_lrNodeN++;
    LrNode& nd = g_lrNodes[i];
    nd.lo = lo, nd.hi = hi, nd.fme = g_lrFme[i];
    *(u8**)(g_lrFme[i] + 8) = badFp ? badFp : g_lrFp[i];
    *(void**)(g_lrFme[i] + 0xC) = q ? (void*)g_lrFme[i] : NULL;
    *(DWORD*)g_lrFp[i] = cls;
    *(DWORD*)(g_lrFp[i] + 0x14) = idOk ? lo : lo + 0x10;
    *(DWORD*)(g_lrFp[i] + 0x18) = hi;
    g_lrFp[i][0x35] = (u8)type;
    nd.next = g_lrBk[lo % 8];
    g_lrBk[lo % 8] = &nd;
}
static u8 __fastcall LrFind(void*, void*, DWORD lo, DWORD hi, void** out) {
    for (int i = 0; i < 4; i++)
        if (g_lrLive[i][0] == lo && g_lrLive[i][1] == hi) { *out = g_xObj[0]; return 1; }
    return 0;
}
static void __fastcall LrKeyFind(void* map, void*, DWORD* out, const DWORD* id) {
    const DWORD* buckets = *(const DWORD* const*)((u8*)map + 4);
    out[0] = id[0] == g_lrParked[0] && id[1] == g_lrParked[1] ? 0x1234 : buckets[*(const DWORD*)((u8*)map + 8)];
    out[1] = 0;
}
static u8 __fastcall LrLotRemove(void*, void*, DWORD lotLo, DWORD lotHi, DWORD idLo, DWORD idHi) {
    assert(lotLo == 0 && lotHi == 0);
    if (!(idLo & 1)) g_lrRemoves++, g_lrRemoved[0] = idLo, g_lrRemoved[1] = idHi;
    return 1;
}
static DWORD WINAPI LrHoldThread(void*) { FpgHoldPoint(); return 0; }

static void TestFp2LostRemoval() {
    static_assert(offsetof(Ts3nCounters, fp2lrMode) == 0x5DE8 && offsetof(Ts3nCounters, fp2lrOrderMiss) == 0x5E08 &&
                  offsetof(Ts3nCounters, fpgMode) == 0x5E0C && offsetof(Ts3nCounters, fpgHeld) == 0x5E34 &&
                  sizeof(Ts3nCounters) == 6112 * 4, "counters v172 layout (docs/registry.md)");
    Ts3nCounters& k = ts3native_counters;
    memset(&k.discReady, 0, offsetof(Ts3nCounters, discOverflow) + 4 - offsetof(Ts3nCounters, discReady));
    memset((void*)&k.discDestroyMode, 0, offsetof(Ts3nCounters, discIdMismatch) + 4 - offsetof(Ts3nCounters, discDestroyMode));
    memset(&k.usfReady, 0, offsetof(Ts3nCounters, usfApplyInJob) + 4 - offsetof(Ts3nCounters, usfReady));
    memset(&k.fp2Mode, 0, sizeof(Ts3nCounters) - offsetof(Ts3nCounters, fp2Mode));
    const DWORD me = GetCurrentThreadId();
    const LockAcquireFn savedLock = g_lockOrig;
    const PfKeyFn savedKey = g_pfKey;
    g_lockOrig = RgTryAcquire;
    g_rgRelOrig = RgFakeRelease;
    InitializeCriticalSection(&g_rgFakeCs);
    const HANDLE savedDone = g_asyncDone, savedInv = g_invReady;
    g_asyncDone = CreateEventW(NULL, FALSE, FALSE, NULL);
    g_invReady = CreateEventW(NULL, FALSE, FALSE, NULL);
    g_discRemoveOrig = XRemove;
    g_discAddOrig = XAdd;
    g_usfOrig = FakeUsf;
    g_usfShould = FakeUsfShould;
    g_uolOrig = LrUolOrig;
    g_uolCore = LrCore;
    g_objLot = LrObjLot;
    g_uof = LrUof;
    g_pfKey = LrPfKey;
    g_objSvc = FakeSvc;
    g_findRef = XFindRef;
    g_objRelease = FakeObjRelease;
    g_lotRemove = XLotRemove;
    g_keyFind = XKeyFind;
    static u8 world[0xA00];
    static u8 planner[0x200];
    static DWORD buckets[2] = {0, 0x5E5E5E5E};
    g_f2World = world;
    world[0x974] = 1;
    *(u8**)(world + 0x95C) = planner;
    *(DWORD**)(world + 0x8BC) = buckets;
    *(DWORD*)(world + 0x8C0) = 1;
    *(DWORD*)(world + 0x8C4) = 0;
    g_xKeyHit = false;
    g_rgLock = world + 0x918;
    g_async.lock = world + 0x918;
    for (int i = 0; i < 3; i++) { *(DWORD*)(g_xObj[i] + 8) = 0xA + i; *(DWORD*)(g_xObj[i] + 0xC) = 0; g_xLive[i] = g_xObj[i]; }
    u8* const A = g_xObj[0], * const B = g_xObj[1];
    k.discMode = k.discDestroyMode = k.discSweepMode = 1;
    k.usfMode = k.fp2Mode = 1;
    k.fp2lrReady = k.fp2lrMode = 1;
    g_async.owner = me;
    g_lrCore = 0, g_xSweeps = 0, g_xSweepHit = false;
    u8* mapHdr[3] = {NULL, (u8*)g_lrBk, (u8*)(uintptr_t)8};
    *(u8***)(planner + 0x24) = mapHdr;
    LrEntry(0xA, 0, kFpgFpClass, 1);  // object A's entry, for fp2lrLost
    auto place = [](DWORD lot) { g_lrMap[0] = 1u << lot, g_lrLot[0] = lot, g_lrMap[1] = 0; };
    auto settled = [&]() { return g_discCount == 0 && k.fp2Deferred[1] == k.fp2Applied[1] + k.fp2Dropped[1]; };
    auto lotMove = [&](DWORD to) {  // 0xB6E19C event 0xA, the lot change, 0xB6E1B6 event 9 (0x9462C9, 0x9462D4)
        UolHook(world, NULL, A);
        g_lrLot[0] = to;
        UolHook(world, NULL, A);
        UofSite(world, NULL, A);
    };

    // path a: removal queued, then the object goes. Vanilla's result is an empty map: the fix reaches it with the call's lot and sweeps
    // the private key; the old rule drops the record and leaves the ghost.
    place(1);
    UolHook(world, NULL, A);
    const DWORD vanillaGone = g_lrMap[0];
    assert(vanillaGone == 0 && g_lrCore == 0);
    place(1);
    g_async.state = A_RUNNING;
    UolHook(world, NULL, A);
    g_lrLot[0] = 0;  // the owner is cleared after event 0xA
    g_xLive[0] = NULL;
    g_async.state = A_IDLE;
    g_xSweepHit = true;
    DiscPoll();
    assert(g_lrMap[0] == vanillaGone && g_lrCore == 1 && k.fp2lrGone == 1 && k.fp2lrGoneHit == 1 && settled());
    assert(g_xSweeps == 1 && g_xSweepArgs[0] == 0 && g_xSweepArgs[2] == 0xB && world[0x994] == 0);
    k.fp2lrMode = 0;
    place(1);
    g_xLive[0] = A;
    g_async.state = A_RUNNING;
    UolHook(world, NULL, A);
    g_xLive[0] = NULL;
    g_async.state = A_IDLE;
    DiscPoll();
    assert(g_lrMap[0] == 1u << 1 && g_lrCore == 1 && k.fp2Dropped[1] == 1 && settled());  // the bug, off
    assert(k.fp2lrLost == 1);
    k.fp2lrMode = 1;

    // path c: DestroyById parks, then fires event 0xA itself: the same record, no gate ran for it
    place(2);
    g_xLive[0] = A;
    g_async.state = A_RUNNING;
    UolHook(world, NULL, A);
    g_lrLot[0] = 0;
    g_xLive[0] = NULL;
    g_async.state = A_IDLE;
    DiscPoll();
    assert(g_lrMap[0] == 0 && k.fp2lrGone == 2 && settled());
    // the destroy gate takes the same branch while the object is alive; a reused id is left alone
    place(1);
    g_xLive[0] = A;
    g_async.state = A_RUNNING;
    UolHook(world, NULL, A);
    g_async.state = A_IDLE;
    DiscOnDestroy(0xA, 0, true);
    assert(g_lrMap[0] == 0 && k.fp2lrApplied == 1 && settled());
    const int core0 = g_lrCore;
    place(1);
    g_async.state = A_RUNNING;
    UolHook(world, NULL, A);
    g_xLive[0] = B;
    g_async.state = A_IDLE;
    DiscPoll();
    assert(g_lrCore == core0 && g_lrMap[0] == 1u << 1 && settled());
    g_xLive[0] = A;

    // path b: a lot move. Vanilla first, no job
    place(1);
    lotMove(2);
    const DWORD vanillaMove = g_lrMap[0];
    assert(vanillaMove == 1u << 2 && k.fp2lrFlush == 0);
    // during a job: 0x833680's site flushes after the job, in vanilla's order, with each call's lot; the same map
    place(1);
    g_async.state = A_RUNNING;
    CloseHandle(CreateThread(NULL, 0, F2JobEnd, NULL, 0, NULL));
    lotMove(2);
    assert(g_lrMap[0] == vanillaMove && k.fp2lrFlush == 1 && k.fp2lrMoved == 1 && g_async.state == A_IDLE && settled());
    // off: both removes land after the insert, on the new lot: the old lot keeps its copy and the fresh insert is lost
    k.fp2lrMode = 0;
    place(1);
    g_async.state = A_RUNNING;
    lotMove(2);
    g_async.state = A_IDLE;
    DiscPoll();
    assert(g_lrMap[0] == 1u << 1 && settled() && k.fp2lrLost == 2);
    k.fp2lrMode = 1;
    // no flush for another object's record; none possible with +0x918 held or inside a callback (counted, the insert runs)
    place(1);
    g_async.state = A_RUNNING;
    UolHook(world, NULL, B);
    UofSite(world, NULL, A);
    assert(g_lrMap[0] == (1u << 1) && g_discCount == 1 && k.fp2lrOrderMiss == 0);
    UolHook(world, NULL, A);
    k.rgOwnerTid = (LONG)me;
    UofSite(world, NULL, A);
    k.rgOwnerTid = 0;
    assert(k.fp2lrOrderMiss == 1 && g_discCount == 2);
    g_inInvoke = 1;
    UofSite(world, NULL, A);
    g_inInvoke = 0;
    assert(k.fp2lrOrderMiss == 2 && g_discCount == 2);
    // the flush takes only that object's records; a queued Add of it stays behind, counted
    __declspec(align(16)) float pts[3][4] = {{1.5f}, {2}, {3}};
    u8* vec[3] = {(u8*)pts, (u8*)pts + 48, (u8*)pts + 48};
    DiscAddHook(world, NULL, A, vec, 0, 0, 0);
    CloseHandle(CreateThread(NULL, 0, F2JobEnd, NULL, 0, NULL));
    UofSite(world, NULL, A);
    assert(k.fp2lrOrderMiss == 3 && g_discCount == 2 && g_disc[g_discHead].idLo == 0xB && g_disc[(g_discHead + 1) % kDiscCap].add);
    DiscPoll();
    assert(g_discCount == 0 && k.fp2ApplyInJob == 0 && k.rgOwnerTid == 0);

    // GhostSweep, a fake footprint map holding every kind of entry
    const DWORD G = 0x1D3272D0, GH = 0x38B400F5, L = 0x1D327130, P = 0x1D328EB0;
    assert(k.fp2lrLost == 2);  // with the fix on nothing is lost
    u8* worldPtr = world;
    g_fpgWorldSlot = (u8* const*)&worldPtr;
    g_findRef = LrFind;
    g_lotRemove = LrLotRemove;
    g_keyFind = LrKeyFind;
    *(DWORD*)(world + 0x8C4) = 1;
    g_lrParked[0] = P, g_lrParked[1] = GH;
    g_lrLive[0][0] = L, g_lrLive[0][1] = GH;
    LrEntry(G, GH, kFpgFpClass, 1);           // the ghost, on two levels
    LrEntry(G, GH, kFpgFpClass, 1);
    LrEntry(L, GH, kFpgFpClass, 1);           // a live object: never
    LrEntry(0xA620D05, 0x9D1, kFpgFpClass, 1);        // 0x7CFA30 key
    LrEntry(0x2E31605, 0, 0x00FEBA98, 1);            // a clean Baseline's non-object ids (diag2 run)
    LrEntry(0x38B0C05, 0x1179, 0x00FEBF90, 1);
    LrEntry(0x7780C05, 0x11451, 0x00FEBF90, 1);
    LrEntry(0x1D328000, GH, 0x00FEBA98, 1);          // object-shaped, no object, other class: kept, in the class line
    LrEntry(0x1D328010, GH, 0x00FEBF90, 1);
    LrEntry(0x1D328020, GH, kFpgFpClass, 0xC);        // other type
    LrEntry(0x1D328030, GH, kFpgFpClass, 1, true, false);  // the footprint names another id
    LrEntry(0x1D328040, GH, kFpgFpClass, 1, false);   // on its way out
    LrEntry(P, GH, kFpgFpClass, 1);                   // parked
    LrEntry(0x1D328050, 0, kFpgFpClass, 1);           // no id high part: not an object id
    LrEntry(0x1D328060, GH, kFpgFpClass, 1, true, true, (u8*)(uintptr_t)0x10);  // unreadable: kept
    const int entries = g_lrNodeN;
    // the census runs before g_rgLock is known
    g_rgLock = NULL;
    k.fpgMode = 1;
    FpgCensus(1);
    assert(k.fpgCensus == 1 && k.fpgEntries == entries && k.fpgGhosts == 1 && g_lrRemoves == 0 && k.fpgRemoved == 0);
    assert(k.fpgKeptRead == 1 && k.fpgOther == 6 && k.fpgObjFp == 10);
    assert(g_fpgClass[0].n + g_fpgClass[1].n + g_fpgClass[2].n == 6 && g_fpgClass[3].n == 0);
    assert(k.rgOwnerTid == 0 && g_rgOffSimOpen == 0);
    k.fpgMode = 0;
    FpgCensus(2);
    assert(k.fpgCensus == 1);
    k.fpgMode = 2;
    FpgCensus(3);
    assert(k.fpgRemoved == 1 && g_lrRemoves == 1 && g_lrRemoved[0] == G && g_lrRemoved[1] == GH);
    // the recheck at the removal: an object under the id, or a parked id, keeps the entry
    assert(!FpgRemove(world, L, GH) && !FpgRemove(world, P, GH) && g_lrRemoves == 1);
    k.fpgMode = 0;

    // the test ops: closed without GhostTest
    g_rgLock = world + 0x918;
    g_fpgTest = false;
    assert(FpgOp(20, 50) == 0 && FpgOp(25, 0) == 0);
    FpgHoldPoint();
    g_fpgTest = true;
    assert(FpgOp(23, G) == 1 && FpgOp(24, GH) == 1 && FpgOp(25, 0) == 1);
    assert(FpgOp(23, G + 0x100) == 1 && FpgOp(25, 0) == 0);
    assert(FpgOp(23, G) == 1 && FpgOp(26, 0) == 1 && g_lrRemoves == 2);
    assert(FpgOp(27, 0) == 1 && k.fp2lrMode == 0 && FpgOp(27, 1) == 1 && k.fp2lrMode == 1);
#ifdef TS3N_RFTEST
    {   // docs/89 §18 ops: the switch, the parked / capped answers, the hold's end on a later job only
        assert(FpgOp(30, 1) == 1 && g_rfMode == 1 && FpgOp(30, 0) == 1 && g_rfMode == 0);
        assert(FpgOp(32, 0) == (g_rfParked > 0) && FpgOp(33, 0) == (g_rfCapped > 0) && FpgOp(34, 0) == 1);
        AsyncJob& q = g_jobs[kJobs - 1];
        const LONG st = q.state;
        const DWORD sq = q.seq;
        q.state = J_QUEUED;
        q.seq = g_jobSeq;  // queued before op 31: does not end the hold
        assert(!FpgNextJobWaits() && FpgOp(31, 0) == 1 && !FpgNextJobWaits());
        q.seq = g_jobSeq + 1;
        assert(FpgNextJobWaits());
        q.state = J_PEND;
        q.seq = g_jobSeq;
        assert(FpgNextJobWaits() && FpgOp(22, 0) == 1 && !FpgNextJobWaits());
        q.state = st;
        q.seq = sq;
    }
#endif
    assert(FpgOp(20, 2000) == 1 && FpgOp(21, 0) == 0);
    const HANDLE hold = CreateThread(NULL, 0, LrHoldThread, NULL, 0, NULL);
    while (!FpgOp(21, 0)) Sleep(1);
    assert(WaitForSingleObject(hold, 20) == WAIT_TIMEOUT && k.fpgHeld == 1);
    assert(FpgOp(22, 0) == 1 && WaitForSingleObject(hold, 1000) == WAIT_OBJECT_0 && FpgOp(21, 0) == 0);
    CloseHandle(hold);
    assert(FpgOp(20, 20) == 1);
    FpgHoldPoint();  // nobody releases: the bound ends it
    assert(k.fpgHeld == 2);
    g_fpgTest = false;

    g_fpgWorldSlot = (u8* const*)(uintptr_t)0x011D9128;
    g_uolCore = (UolCoreFn)(uintptr_t)0x0082C120;
    g_objLot = (ObjLotFn)(uintptr_t)0x008297C0;
    g_uof = (UofFn)(uintptr_t)UOF_FN;
    g_pfKey = savedKey;
    g_keyFind = XKeyFind;
    g_async.owner = 0;
    g_async.state = A_IDLE;
    g_async.lock = NULL;
    g_rgLock = NULL;
    k.discMode = k.usfMode = k.fp2Mode = 0;
    k.fp2lrMode = k.fp2lrReady = 0;
    k.rgOwnerTid = 0;
    k.rgAcquires = 0;
    g_lockOrig = savedLock;
    CloseHandle(g_asyncDone);
    CloseHandle(g_invReady);
    g_asyncDone = savedDone;
    g_invReady = savedInv;
    DeleteCriticalSection(&g_rgFakeCs);
}

// --- [Discourage] DeferPrivateFp (docs/89 §14, counters v113 pf*): the IC RemovePrivateFootprint call site 0x75465E deferred
// into D's queue while a job runs. Phase 2 installed with its switch off (DeferFootprint2=2), so its hooks only keep order.
// Log letters as in TestFootprintDefer2 ---
static unsigned __int64 __cdecl FakePfKey(int kind, int type, DWORD idLo, DWORD idHi) {
    assert(kind == 2 && type == 0x19);
    return ((unsigned __int64)idHi << 32 | idLo) + 0x7700;
}

static void TestPrivateFpDefer() {
    static_assert(offsetof(Ts3nCounters, pfReady) == 0x473C && offsetof(Ts3nCounters, pfMode) == 0x4740 &&
                  offsetof(Ts3nCounters, pfApplyInJob) == 0x476C && sizeof(Ts3nCounters) == 6112 * 4,
                  "counters v113 layout after v112 (docs/registry.md)");
    Ts3nCounters& k = ts3native_counters;
    memset(&k.discReady, 0, offsetof(Ts3nCounters, discOverflow) + 4 - offsetof(Ts3nCounters, discReady));
    memset((void*)&k.discDestroyMode, 0, offsetof(Ts3nCounters, discIdMismatch) + 4 - offsetof(Ts3nCounters, discDestroyMode));
    memset(&k.usfReady, 0, offsetof(Ts3nCounters, usfApplyInJob) + 4 - offsetof(Ts3nCounters, usfReady));
    memset(&k.fp2Mode, 0, offsetof(Ts3nCounters, fp2ApplyInJob) + 4 - offsetof(Ts3nCounters, fp2Mode));
    memset(&k.pfReady, 0, sizeof(Ts3nCounters) - offsetof(Ts3nCounters, pfReady));
    const DWORD me = GetCurrentThreadId();
    const LockAcquireFn savedLock = g_lockOrig;
    g_lockOrig = RgTryAcquire;
    g_rgRelOrig = RgFakeRelease;
    InitializeCriticalSection(&g_rgFakeCs);
    const HANDLE savedDone = g_asyncDone, savedInv = g_invReady;
    g_asyncDone = CreateEventW(NULL, FALSE, FALSE, NULL);
    g_invReady = CreateEventW(NULL, FALSE, FALSE, NULL);
    g_discAddOrig = XAdd;
    g_discRemoveOrig = XRemove;
    g_usfOrig = FakeUsf;
    g_usfShould = FakeUsfShould;
    g_ftOrig = FakeFt;
    g_uolOrig = FakeUol;
    g_pfRemove = FakePfRemove;
    g_pfAdd = FakePfAdd;
    g_pfKey = FakePfKey;
    g_objSvc = FakeSvc;
    g_findRef = XFindRef;
    g_objRelease = FakeObjRelease;
    g_lotRemove = XLotRemove;
    g_keyFind = XKeyFind;
    static u8 world[0xA00];
    static u8 planner[0x200];
    static DWORD buckets[2] = {0, 0x5E5E5E5E};
    g_f2World = world;
    world[0x974] = 1;
    world[0x978] = 0;
    *(u8**)(world + 0x95C) = planner;
    *(DWORD**)(world + 0x8BC) = buckets;
    *(DWORD*)(world + 0x8C0) = 1;
    *(DWORD*)(world + 0x8C4) = 0;
    g_xKeyHit = false;
    g_uShould = true;
    g_rgLock = world + 0x918;
    for (int i = 0; i < 3; i++) { *(DWORD*)(g_xObj[i] + 8) = 0xA + i; *(DWORD*)(g_xObj[i] + 0xC) = 0; g_xLive[i] = g_xObj[i]; }
    u8* const A = g_xObj[0], * const B = g_xObj[1], * const C = g_xObj[2];
    k.discMode = 1;
    k.discDestroyMode = 1;
    k.discSweepMode = 1;
    k.usfMode = 1;
    k.fp2Mode = 0;  // DeferFootprint2=2
    k.pfReady = 1;
    k.pfMode = 1;
    g_async.owner = me;
    g_xLogN = 0;
    auto log = [](const char* s) { return g_xLogN == (int)strlen(s) && memcmp(g_xLog, s, g_xLogN) == 0; };
    auto settled = [&]() { return g_discCount == 0 && k.pfDeferred == k.pfApplied + k.pfSwept + k.pfDropped; };

    // 1. no job: today's call, its result
    assert(PfRemoveSite(world, NULL, A, 1) == 1 && log("X") && k.pfCalls == 1 && k.pfInJob == 0 && k.pfDeferred == 0);
    // 2. a job runs: recorded and answered 1; the object's later FromTool and U calls queue behind it (order rule, fp2Mode 0);
    // another object's FromTool runs now; nothing applies while the job runs, all in call order at the next poll with no job
    g_xLogN = 0;
    g_async.state = A_RUNNING;
    assert(PfRemoveSite(world, NULL, A, 1) == 1 && g_xLogN == 0 && k.pfDeferred == 1 && k.pfInJob == 1);
    FtHook(world, NULL, A);
    FtHook(world, NULL, B);
    UsfHook(world, NULL, A, 1, 0, 7);
    assert(log("g") && g_discCount == 3);
    DiscPoll();  // a young queue while the job runs: kept
    assert(g_discCount == 3);
    g_async.state = A_IDLE;
    DiscPoll();
    assert(log("gXfP") && settled() && k.pfApplied == 1 && k.rgOwnerTid == 0);
    // 3. the same remove right behind is dropped; behind another record of the object it is kept
    g_xLogN = 0;
    g_async.state = A_RUNNING;
    PfRemoveSite(world, NULL, A, 1);
    PfRemoveSite(world, NULL, A, 1);
    UsfHook(world, NULL, A, 0, 0, 7);
    PfRemoveSite(world, NULL, A, 1);
    assert(k.pfDup == 1 && g_discCount == 3);
    g_async.state = A_IDLE;
    DiscPoll();
    assert(log("XpX") && settled());
    // 4. mode 0: today's call with nothing queued; with a record of the object queued it still queues (order)
    g_xLogN = 0;
    g_async.state = A_RUNNING;
    k.pfMode = 0;
    PfRemoveSite(world, NULL, B, 1);
    UsfHook(world, NULL, A, 1, 0, 7);
    PfRemoveSite(world, NULL, A, 1);
    assert(log("Y") && g_discCount == 2);
    k.pfMode = 1;
    g_async.state = A_IDLE;
    DiscPoll();
    assert(log("YPX") && settled());
    // 5. pass-through: +0x918 owned, a job callback, another world
    g_xLogN = 0;
    g_async.state = A_RUNNING;
    k.rgOwnerTid = (LONG)me;
    PfRemoveSite(world, NULL, A, 1);
    k.rgOwnerTid = 0;
    g_inInvoke = 1;
    PfRemoveSite(world, NULL, B, 1);
    g_inInvoke = 0;
    static u8 world2[0xA00];
    world2[0x974] = 1;
    PfRemoveSite(world2, NULL, C, 1);
    assert(log("XYZ") && k.pfPass == 3 && g_discCount == 0);
    // 6. AddPrivateFootprint of an object with a queued remove: flushed first (waits for the running job), then the add
    g_xLogN = 0;
    PfRemoveSite(world, NULL, A, 1);
    CloseHandle(CreateThread(NULL, 0, F2JobEnd, NULL, 0, NULL));
    __declspec(align(16)) float pts[3][4] = {{1.5f}, {2}, {3}};
    assert(PfAddSite(world, NULL, A, pts, 0) == 1 && log("XU") && settled() && k.pfAddFlush == 1 && g_async.state == A_IDLE);
    // an add with nothing queued: today's call, no flush
    assert(PfAddSite(world, NULL, B, pts, 0) == 1 && log("XUV") && k.pfAddFlush == 1);
    // 7. destroy while queued (D-X): the object's remove applied before the destroy, the other object's record kept
    g_xLogN = 0;
    g_async.state = A_RUNNING;
    PfRemoveSite(world, NULL, A, 1);
    PfRemoveSite(world, NULL, B, 1);
    g_async.state = A_IDLE;  // RT_DESTROY waited for the job
    DiscOnDestroy(0xA, 0, true);
    assert(log("X") && g_discCount == 1 && k.rgOwnerTid == 0);
    DiscPoll();
    assert(log("XY") && settled());
    // 8. gone at apply (destroyed inside a job callback, left queued): the private key swept from every lot; reused id: dropped
    g_xLogN = 0;
    g_async.state = A_RUNNING;
    PfRemoveSite(world, NULL, A, 1);
    PfRemoveSite(world, NULL, B, 1);
    g_xLive[0] = NULL, g_xLive[1] = A;
    const int sweeps0 = g_xSweeps;
    world[0x994] = 1;
    g_async.state = A_IDLE;
    DiscPoll();
    assert(g_xLogN == 0 && k.pfSwept == 1 && k.pfDropped == 1 && g_xSweeps == sweeps0 + 1 && settled());
    assert(g_xSweepArgs[0] == 0 && g_xSweepArgs[1] == 0 && g_xSweepArgs[2] == 0xA + 0x7700 && g_xSweepArgs[3] == 0);
    assert(world[0x994] == 0);
    // with DeadSweep off the gone record is only dropped
    g_xLive[1] = B;
    k.discSweepMode = 0;
    g_async.state = A_RUNNING;
    PfRemoveSite(world, NULL, A, 1);
    g_async.state = A_IDLE;
    DiscPoll();
    assert(g_xSweeps == sweeps0 + 1 && k.pfDropped == 2 && settled());
    k.discSweepMode = 1;
    for (int i = 0; i < 3; i++) g_xLive[i] = g_xObj[i];
    // 9. queue full: drained (no Add queued) then today's call; with a D Add queued the call runs now (pfFullPass)
    g_async.state = A_RUNNING;
    for (int i = 0; i < kDiscCap; i++) {
        if (!(i & 1)) PfRemoveSite(world, NULL, B, 1);  // the last one (i odd) is a U record: no duplicate
        else UsfHook(world, NULL, B, i & 2 ? 1 : 0, 0, 7);
    }
    assert(g_discCount == kDiscCap);
    g_async.state = A_IDLE;
    g_xLogN = 0;
    assert(PfRemoveSite(world, NULL, B, 1) == 1);
    assert(g_discCount == 0 && settled() && g_xLog[(g_xLogN - 1) & 63] == 'Y');
    g_async.state = A_RUNNING;
    u8* vec[3] = {(u8*)pts, (u8*)pts + 48, (u8*)pts + 48};
    DiscAddHook(world, NULL, A, vec, 0, 0, 0);
    for (int i = 1; i < kDiscCap; i++) {
        if (!(i & 1)) PfRemoveSite(world, NULL, B, 1);  // the last one (i odd) is a U record: no duplicate
        else UsfHook(world, NULL, B, i & 2 ? 1 : 0, 0, 7);
    }
    assert(g_discCount == kDiscCap);
    g_xLogN = 0;
    PfRemoveSite(world, NULL, C, 1);  // C has nothing queued, but the job runs: wants a record, finds the queue full
    assert(log("Z") && k.pfFullPass == 1 && g_discCount == kDiscCap);
    g_async.state = A_IDLE;
    DiscPoll();
    assert(settled());
    // tripwires
    assert(k.pfApplyInJob == 0 && k.usfApplyInJob == 0 && k.fp2ApplyInJob == 0);

    g_async.owner = 0;
    g_async.state = A_IDLE;
    g_rgLock = NULL;
    k.discMode = 0;
    k.usfMode = 0;
    k.fp2Mode = 0;
    k.pfReady = 0;
    k.pfMode = 0;
    k.rgOwnerTid = 0;
    k.rgAcquires = 0;
    g_lockOrig = savedLock;
    g_pfRemove = (PfRemoveFn)(uintptr_t)PFR_FN;
    g_pfAdd = (PfAddFn)(uintptr_t)PFA_FN;
    g_pfKey = (PfKeyFn)(uintptr_t)0x007CFA30;
    CloseHandle(g_asyncDone);
    CloseHandle(g_invReady);
    g_asyncDone = savedDone;
    g_invReady = savedInv;
    DeleteCriticalSection(&g_rgFakeCs);
}

// --- [Discourage] PlanDrain (docs/89 §13, counters v107 sd*): a sim-thread sync plan through PlanLockedHook applies D's queue
// first. The fake plan records the queue length and the apply log length it saw; log letters as in TestFootprintDefer2 ---
static int g_pdSeenCount, g_pdSeenLog, g_pdPlans;
static u8 g_pdRet = 1;
static bool g_pdNest;
static u8* g_pdNestObj;
static u8 __fastcall PdFakePlan(void* world, void*, int*, int*, int*, DWORD, DWORD, u8*, int*, int*, DWORD, DWORD) {
    g_pdPlans++;
    g_pdSeenCount = g_discCount;
    g_pdSeenLog = g_xLogN;
    if (g_pdNest) {  // the game's plan holds +0x918; a footprint call made inside it passes through (usfPass)
        g_pdNest = false;
        ts3native_counters.rgOwnerTid = (LONG)GetCurrentThreadId();
        UsfHook((u8*)world, NULL, g_pdNestObj, 1, 1, 7);
        ts3native_counters.rgOwnerTid = 0;
    }
    return g_pdRet;
}

static void TestPlanDrain() {
    static_assert(offsetof(Ts3nCounters, sdReady) == 0x4588 && offsetof(Ts3nCounters, sdMode) == 0x458C &&
                  offsetof(Ts3nCounters, sdSkip) == 0x45A8 && offsetof(Ts3nCounters, sdFail) == 0x45BC &&
                  sizeof(Ts3nCounters) == 6112 * 4, "counters v107 layout after v106 (docs/registry.md)");
    Ts3nCounters& k = ts3native_counters;
    memset(&k.discReady, 0, offsetof(Ts3nCounters, discOverflow) + 4 - offsetof(Ts3nCounters, discReady));
    memset((void*)&k.discDestroyMode, 0, offsetof(Ts3nCounters, discIdMismatch) + 4 - offsetof(Ts3nCounters, discDestroyMode));
    memset(&k.usfReady, 0, offsetof(Ts3nCounters, usfApplyInJob) + 4 - offsetof(Ts3nCounters, usfReady));
    memset(&k.fp2Mode, 0, offsetof(Ts3nCounters, fp2ApplyInJob) + 4 - offsetof(Ts3nCounters, fp2Mode));
    memset(&k.sdReady, 0, sizeof(Ts3nCounters) - offsetof(Ts3nCounters, sdReady));
    const DWORD me = GetCurrentThreadId();
    const LockAcquireFn savedLock = g_lockOrig;
    g_lockOrig = RgTryAcquire;
    g_rgRelOrig = RgFakeRelease;
    InitializeCriticalSection(&g_rgFakeCs);
    const HANDLE savedDone = g_asyncDone, savedInv = g_invReady;
    g_asyncDone = CreateEventW(NULL, FALSE, FALSE, NULL);
    g_invReady = CreateEventW(NULL, FALSE, FALSE, NULL);
    const auto planSave = g_planOrig;
    CacheEntry* const tabSave = g_tab;
    const LONG workerSave = k.workerMode, workerReadySave = k.workerReady, rgReadySave = k.rgReady;
    g_tab = NULL;  // no negative cache: every plan reaches the fake
    k.workerMode = 0;
    k.workerReady = 0;
    k.rgReady = 0;
    g_planOrig = PdFakePlan;
    g_discAddOrig = XAdd;
    g_discRemoveOrig = XRemove;
    g_usfOrig = FakeUsf;
    g_usfShould = FakeUsfShould;
    g_ftOrig = FakeFt;
    g_uolOrig = FakeUol;
    g_objSvc = FakeSvc;
    g_findRef = XFindRef;
    g_objRelease = FakeObjRelease;
    g_lotRemove = XLotRemove;
    g_keyFind = XKeyFind;
    static u8 world[0xA00];
    static u8 planner[0x200];
    static DWORD buckets[2] = {0, 0x5E5E5E5E};
    g_f2World = world;
    world[0x974] = 1;
    *(u8**)(world + 0x95C) = planner;
    *(DWORD**)(world + 0x8BC) = buckets;
    *(DWORD*)(world + 0x8C0) = 1;
    *(DWORD*)(world + 0x8C4) = 0;
    g_xKeyHit = false;
    g_uShould = true;
    g_rgLock = world + 0x918;
    for (int i = 0; i < 3; i++) { *(DWORD*)(g_xObj[i] + 8) = 0xA + i; *(DWORD*)(g_xObj[i] + 0xC) = 0; g_xLive[i] = g_xObj[i]; }
    u8* const A = g_xObj[0], * const B = g_xObj[1], * const C = g_xObj[2];
    k.discMode = 1;
    k.discDestroyMode = 1;
    k.discSweepMode = 1;
    k.usfMode = 1;
    k.fp2Mode = 1;
    k.sdReady = 1;
    k.sdMode = 1;
    g_async.owner = me;
    g_async.state = A_IDLE;
    static float pts[3][16] = {{1, 0, 1}, {5, 0, 5}, {9, 0, 9}};  // 64-byte route points
    int starts[2] = {(int)(uintptr_t)pts[0], (int)(uintptr_t)pts[1]};
    int goals[2] = {(int)(uintptr_t)pts[1], (int)(uintptr_t)pts[2]};
    u8 opts[0x38] = {0};
    int list[2];
    DWORD guid = 0x500;
    auto plan = [&]() {
        list[0] = list[1] = (int)(uintptr_t)list;
        g_pdSeenCount = g_pdSeenLog = -1;
        return PlanLockedHook(world, NULL, list, starts, goals, 0x100, 1, opts, NULL, NULL, guid++, 0);
    };
    auto log = [](const char* s) { return g_xLogN == (int)strlen(s) && memcmp(g_xLog, s, g_xLogN) == 0; };
    auto queue4 = [&]() {  // 3 U records and 1 phase 2 record while a job runs
        g_async.state = A_RUNNING;
        UsfHook(world, NULL, A, 0, 0, 7);
        UsfHook(world, NULL, B, 0, 0, 7);
        UsfHook(world, NULL, C, 0, 0, 7);
        UolHook(world, NULL, A);
        assert(g_discCount == 4);
    };

    // 1. job running, 4 records queued: the drain waits for the job, applies all in order, then the plan runs with none left
    g_xLogN = 0;
    queue4();
    CloseHandle(CreateThread(NULL, 0, F2JobEnd, NULL, 0, NULL));
    g_pdRet = 1;
    assert(plan() == 1 && g_pdPlans == 1);
    assert(g_pdSeenCount == 0 && g_pdSeenLog == 4 && log("pqrL") && g_async.state == A_IDLE);
    assert(k.sdChecks == 1 && k.sdDrains == 1 && k.sdRecs == 4 && k.sdJobRan == 1 && k.usfApplyInJob == 0 && k.fp2ApplyInJob == 0);
    // 2. no job running: drained with no wait; 9a. the plan fails after a drain
    g_xLogN = 0;
    queue4();
    g_async.state = A_IDLE;
    g_pdRet = 0;
    const LONG forced0 = k.discForced;
    assert(plan() == 0 && g_pdSeenCount == 0 && g_pdSeenLog == 4 && log("pqrL"));
    assert(k.sdDrains == 2 && k.sdRecs == 8 && k.sdJobRan == 1 && k.discForced == forced0 && k.sdFail[0] == 1 && k.sdFail[1] == 0);
    // 3. an Add queued: nothing applied (World_FireEvent 0x11 only at IC points); 9b. the plan fails after a skip
    g_xLogN = 0;
    g_async.state = A_RUNNING;
    __declspec(align(16)) float fp[3][4] = {{1.5f}, {2}, {3}};
    u8* vec[3] = {(u8*)fp, (u8*)fp + 48, (u8*)fp + 48};
    DiscAddHook(world, NULL, A, vec, 0, 0, 0);
    UsfHook(world, NULL, B, 0, 0, 7);
    g_async.state = A_IDLE;
    assert(plan() == 0 && g_pdSeenCount == 2 && g_pdSeenLog == 0 && k.sdSkip[0] == 1 && k.sdDrains == 2 && k.sdFail[1] == 1);
    DiscPoll();
    assert(log("Aq") && g_discCount == 0);
    g_pdRet = 1;
    // 4. inside a job callback: skipped, no job wait (the job still runs; RunPlan sends the plan on as before)
    g_xLogN = 0;
    queue4();
    g_inInvoke = 1;
    assert(plan() == 1 && g_pdSeenCount == 4 && k.sdSkip[1] == 1 && g_async.state == A_RUNNING && k.discForced == forced0);
    g_inInvoke = 0;
    // 5. +0x918 already owned by this thread: skipped
    g_async.state = A_IDLE;
    k.rgOwnerTid = (LONG)me;
    assert(plan() == 1 && g_pdSeenCount == 4 && k.sdSkip[2] == 1);
    k.rgOwnerTid = 0;
    // 6. the P5c lent lock: skipped, no hand-over (the plan runs here under the lent hold, as RunPlan does)
    CRITICAL_SECTION lent = {};
    lent.OwningThread = (HANDLE)(uintptr_t)me;
    void* const p5bLockSave = g_p5bLock;
    g_p5bLock = &lent;
    g_p5bWin = 1;
    g_async.state = A_RUNNING;
    const LONG hand0 = k.p5cHandOver;
    assert(plan() == 1 && g_pdSeenCount == 4 && k.sdSkip[3] == 1 && k.p5cHandOver == hand0 && g_async.state == A_RUNNING);
    g_p5bWin = 0;
    g_p5bLock = p5bLockSave;
    g_async.state = A_IDLE;
    //    draining (a plan inside an apply): skipped
    g_discDraining = true;
    assert(plan() == 1 && g_pdSeenCount == 4 && k.sdSkip[4] == 1);
    g_discDraining = false;
    // 7. another thread (the worker's own plans) and a P4 clone run: not counted at all
    const LONG checks0 = k.sdChecks;
    g_async.owner = me + 1;
    assert(plan() == 1 && g_pdSeenCount == 4);
    g_async.owner = me;
    t_snapRun = 1;
    assert(plan() == 1 && g_pdSeenCount == 4);
    t_snapRun = 0;
    // 8. switch off with records queued, or on with nothing queued: nothing
    k.sdMode = 0;
    assert(plan() == 1 && g_pdSeenCount == 4 && k.sdChecks == checks0);
    k.sdMode = 1;
    g_xLogN = 0;
    DiscPoll();
    assert(log("pqrL") && g_discCount == 0);
    assert(plan() == 1 && g_pdSeenCount == 0 && k.sdChecks == checks0 && k.sdDrains == 2);
    // 10. order: a footprint call made inside the plan (+0x918 held) runs at once, after the drained records
    g_xLogN = 0;
    queue4();
    g_async.state = A_IDLE;
    g_pdNest = true;
    g_pdNestObj = B;
    const LONG pass0 = k.usfPass;
    assert(plan() == 1 && g_pdSeenCount == 0 && log("pqrLQ") && k.usfPass == pass0 + 1 && g_discCount == 0);
    assert(k.sdChecks == checks0 + 1 && k.sdDrains == 3 && k.sdRecs == 12 && k.sdMaxUs >= 0 && k.sdUs >= k.sdMaxUs);
    // tripwires
    assert(k.usfApplyInJob == 0 && k.fp2ApplyInJob == 0 && k.rgOwnerTid == 0);

    g_async.owner = 0;
    g_async.state = A_IDLE;
    g_rgLock = NULL;
    k.discMode = 0;
    k.usfMode = 0;
    k.fp2Mode = 0;
    k.sdMode = 0;
    k.sdReady = 0;
    k.rgOwnerTid = 0;
    k.rgAcquires = 0;
    k.workerMode = workerSave;
    k.workerReady = workerReadySave;
    k.rgReady = rgReadySave;
    g_tab = tabSave;
    g_planOrig = planSave;
    g_lockOrig = savedLock;
    CloseHandle(g_asyncDone);
    CloseHandle(g_invReady);
    g_asyncDone = savedDone;
    g_invReady = savedInv;
    DeleteCriticalSection(&g_rgFakeCs);
}

// --- design G standalone ([Discourage] DestroyPark, counters v76; p5c.cpp P5cPark / P5cFlush outside a P5c window; docs/88
// §10): fake objects with the game's layout where the park reads it (count +4). The fake release frees (poisons the canary,
// logs the order) when the count reaches 0, as 0x7460F0's deleting destructor would; GDestroy is DestroyById's net effect
// (hash erase, count -1). The run against the game's own DestroyById / FindById / release with a job thread is steerro-test
// section DG. ---
static const int kGObjs = 8;
static u8 g_gObj[kGObjs][0x20];
static bool g_gInHash[kGObjs];
static char g_gFreeLog[640];
static int g_gFreeN, g_gRelCalls;
static DWORD g_gFreeTid;
static int GIdx(const void* o) { for (int i = 0; i < kGObjs; i++) if (o == g_gObj[i]) return i; return -1; }
static LONG& GCount(int i) { return *(LONG*)(g_gObj[i] + 4); }
static DWORD& GCanary(int i) { return *(DWORD*)(g_gObj[i] + 0x18); }
static void* __cdecl GSvc() { return g_gObj; }
static u8 __fastcall GFindRef(void* svc, void*, DWORD lo, DWORD hi, void** out) {  // 0x9395A0's convention
    assert(svc == (void*)g_gObj && hi == 0);
    const int i = (int)lo - 0x100;
    if (i < 0 || i >= kGObjs || !g_gInHash[i]) return 0;
    InterlockedIncrement(&GCount(i));
    *out = g_gObj[i];
    return 1;
}
static void GDrop(int i) {
    assert(GCount(i) > 0);
    if (InterlockedDecrement(&GCount(i)) == 0) {
        GCanary(i) = 0xDDDDDDDD;
        g_gFreeLog[g_gFreeN++] = (char)('A' + i);
        g_gFreeTid = GetCurrentThreadId();
    }
}
static void __fastcall GRelease(void** pp, void*) { g_gRelCalls++; GDrop(GIdx(*pp)); }
static void GDestroy(int i) { assert(g_gInHash[i]); g_gInHash[i] = false; GDrop(i); }
static void GNew(int i) { GCount(i) = 1; *(DWORD*)(g_gObj[i] + 8) = 0x100 + i; GCanary(i) = 0xC0FFEE00 + i; g_gInHash[i] = true; }
static DWORD WINAPI GFlushOther(void*) { P5cFlush(); return 0; }

// docs/67 §15: the 0x958560 parent guard on dummies (check only: the original always runs, a bad parent is counted by kind)
static void* __fastcall XgDummy(u8*, void*, void* out) { return out; }
static void TestXformGuard() {
    g_xgOrig = XgDummy;
    alignas(16) DWORD parent[16] = {0x01042CA8, 2, 0x11, 0x22, 0, 0, 0xABCD, 0, 0, 0, 0};
    alignas(16) u8 self[0x40] = {};
    *(DWORD*)(self + 0x30) = (DWORD)(uintptr_t)parent;
    int out = 0;
    const LONG b0 = g_xgBad[0], b1 = g_xgBad[1], b2 = g_xgBad[2];
    assert(XgHook(self, NULL, &out) == &out && g_xgBad[0] == b0 && g_xgBad[1] == b1 && g_xgBad[2] == b2);  // live parent
    parent[10] = 0x400000;
    XgHook(self, NULL, &out);
    assert(g_xgBad[2] == b2 + 1);  // destroyed, still allocated
    parent[1] = 0;
    XgHook(self, NULL, &out);
    assert(g_xgBad[1] == b1 + 1);  // refcount 0
    parent[0] = 0;
    XgHook(self, NULL, &out);
    assert(g_xgBad[0] == b0 + 1);  // vtable gone: freed
    *(DWORD*)(self + 0x30) = 0x1000;  // unreadable
    XgHook(self, NULL, &out);
    assert(g_xgBad[0] == b0 + 2);
    *(DWORD*)(self + 0x30) = 0;  // no parent: fine
    XgHook(self, NULL, &out);
    assert(g_xgBad[0] == b0 + 2 && g_xgBad[1] == b1 + 1 && g_xgBad[2] == b2 + 1);
    g_xgOrig = NULL;
    Log("selftest: transform parent guard ok");
}

static void TestDestroyPark() {
    static_assert(offsetof(Ts3nCounters, dpReady) == 0x31DC && offsetof(Ts3nCounters, dpMode) == 0x31E0 &&
                  offsetof(Ts3nCounters, dpParked) == 0x31E4 && offsetof(Ts3nCounters, dpKeptWait) == 0x31E8 &&
                  sizeof(Ts3nCounters) == 6112 * 4, "v76 layout after v75 (docs/registry.md)");
    Ts3nCounters& k = ts3native_counters;
    k.dpReady = k.dpMode = k.dpParked = k.dpKeptWait = 0;
    k.vfRtDestroyWaitUs = 0;
    const DWORD me = GetCurrentThreadId();
    static DWORD simTid;
    simTid = me;
    const DWORD* const savedSim = g_rtSimTidSlot;
    g_rtSimTidSlot = &simTid;
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
    const SvcFn savedSvc = g_objSvc;
    const FindRefFn savedFind = g_findRef;
    const ReleaseFn savedRel = g_objRelease;
    const DWORD savedWorker = g_workerTid;
    g_objSvc = GSvc, g_findRef = GFindRef, g_objRelease = GRelease;
    k.rtReady = RT_ALL;
    k.rgEnforce = 1;
    k.rgOwnerTid = 0;
    k.rtClosing = 0;
    g_workerTid = me + 7;
    g_async.owner = me;
    g_rgLock = NULL;
    g_discCount = 0;
    g_p5cJob = 0, g_p5bWin = 0;  // no P5c window: the standalone path only
    assert(g_p5cGyN == 0);
    for (int i = 0; i < kGObjs; i++) GNew(i);
    DWORD stack[24] = {};
    auto reset = [&] { t_rg.n = 0; t_rg.readers = 0; g_rgOffSimOpen = 0; };
    // RT_DESTROY through the stub's view: slot[1] / slot[2] = the id; the destroy itself (GDestroy) runs after the stub
    auto destroy = [&](DWORD id) {
        stack[20] = 0x777;
        stack[21] = id;
        stack[22] = 0;
        RtEnter(7, &stack[20]);
        assert(RgLeave() == 0x777 && g_rgOffSimOpen == 0);
    };
    const LONG parked0 = k.p5cParked, flushed0 = k.p5cFlushed, refused0 = k.p5cFlushRefused, low0 = k.p5cCountLow;

    // 0. not installed / switch off: no park, today's path (a destroy with no job, so the stub does not wait)
    g_async.state = A_RUNNING;
    assert(!P5cPark(0x100, 0) && k.dpKeptWait == 0);  // dpReady 0: the probe's switch alone does not arm it
    k.dpMode = 1;
    assert(!P5cPark(0x100, 0));
    k.dpReady = 1, k.dpMode = 0;
    assert(!P5cPark(0x100, 0) && g_p5cGyN == 0);
    k.dpMode = 1;

    // 1. destroy during a job: a job uses A (raw pointer, FindById) and B (its own reference) across their destroys
    u8* const jobA = g_gObj[0];
    u8* const jobB = g_gObj[1];
    InterlockedIncrement(&GCount(1));  // the job's lock inc on B (the 0x643010 pattern)
    destroy(0x100);
    GDestroy(0);
    destroy(0x101);
    GDestroy(1);
    assert(g_async.state == A_RUNNING && k.vfRtDestroyWaitUs == 0 && k.dpParked == 2 && g_p5cGyN == 2);
    assert(k.p5cParked == parked0 + 2 && k.p5cFlushRefused == refused0);  // 0.36.19: no flush at a destroy entry (crash I)
    assert(GCount(0) == 1 && GCount(1) == 2 && g_gFreeN == 0);
    assert(*(DWORD*)(jobA + 0x18) == 0xC0FFEE00 && *(DWORD*)(jobB + 0x18) == 0xC0FFEE01);  // H-b: no read of a freed block
    GDrop(1);  // the job's release of B is not the last one (H-c: no destructor on the worker)
    assert(GCount(1) == 1 && g_gFreeN == 0);
    // release points refuse while the job runs: the START / READY poll
    P5cFlush();
    assert(g_p5cGyN == 2 && g_gFreeN == 0 && g_rgOffSimOpen == 0);
    // the job ends; a flush from another (non-worker, non-owner) thread is refused: the free belongs to the sim
    g_async.state = A_IDLE;
    HANDLE th = CreateThread(NULL, 0, GFlushOther, NULL, 0, NULL);
    WaitForSingleObject(th, INFINITE);
    CloseHandle(th);
    assert(g_p5cGyN == 2 && g_gFreeN == 0);
    g_inInvoke = 1;  // nor inside a job callback
    P5cFlush();
    assert(g_p5cGyN == 2 && g_gFreeN == 0);
    g_inInvoke = 0;
    // the next poll on the owner frees both, in park order, on this thread
    P5cFlush();
    assert(g_p5cGyN == 0 && k.p5cFlushed == flushed0 + 2 && g_gFreeN == 2 && memcmp(g_gFreeLog, "AB", 2) == 0);
    assert(g_gFreeTid == me && GCount(0) == 0 && GCount(1) == 0 && GCanary(0) == 0xDDDDDDDD && g_rgOffSimOpen == 0);

    // 2. release order over three objects. Crash I (docs/67 §11, 0.36.19): a no-job destroy entry no longer releases them
    //    (DestroyById's native callers sit mid-update, e.g. locomotion 0x84CE30 with the follower's event list swapped out
    //    at 0x84E900; a parked object's destructor run there can free what that caller holds); the next START / READY poll
    //    does, oldest first, and the released ring names the flush when a fault register points into a released object
    for (int i = 0; i < kGObjs; i++) GNew(i);
    g_gFreeN = 0;
    g_async.state = A_RUNNING;
    destroy(0x104); GDestroy(4);
    destroy(0x102); GDestroy(2);
    destroy(0x103); GDestroy(3);
    assert(k.dpParked == 5 && k.p5cPendMax >= 3 && g_gFreeN == 0 && k.vfRtDestroyWaitUs == 0);
    g_async.state = A_IDLE;
    const int rel0 = g_gRelCalls;
    destroy(0x105);  // the job ended; a native caller's destroy (the 0x84CE30 loop): nothing parked is released here
    assert(g_p5cGyN == 3 && g_gRelCalls == rel0 && g_gFreeN == 0 && GCanary(2) == 0xC0FFEE02 && k.dpParked == 5);
    GDestroy(5);
    assert(g_gFreeN == 1 && g_gFreeLog[0] == 'F');
    const LONG relN0 = g_p5cRelN;
    P5cFlush();  // the START / READY poll (GetOptionHook, a yielding script task)
    assert(g_p5cGyN == 0 && g_gRelCalls - rel0 == 3 && memcmp(g_gFreeLog + 1, "ECD", 3) == 0 && g_p5cRelN == relN0 + 3);
    {
        CONTEXT c = {};
        c.Ecx = (DWORD)(uintptr_t)(g_gObj[2] + 0x18);  // a fault on C's freed block (the canary word)
        c.Eax = 0x10;
        assert(P5cRelMatch(&c) == (DWORD)(uintptr_t)g_gObj[2]);  // C, the nearest released object below ecx
        c.Ecx = (DWORD)(uintptr_t)&c;  // not one of ours
        assert(P5cRelMatch(&c) == 0);
        char b[256];  // docs/67 §15: the guard-line lookup names the same object, the dump runs once
        assert(P5cRelFind((DWORD)(uintptr_t)(g_gObj[2] + 0x18), b, sizeof(b)) && strstr(b, "+0x18 into"));
        assert(!P5cRelFind((DWORD)(uintptr_t)&c, b, sizeof(b)));
        P5cRelDump();
        assert(g_p5cDumpN == 1);
    }
    // ... while the world closes (DestroyAll, shutdown destroys after RT_CLOSING) a destroy entry still releases
    g_async.state = A_RUNNING;
    GNew(4);
    destroy(0x104); GDestroy(4);
    g_async.state = A_IDLE;
    k.rtClosing = 1;
    destroy(0x105);  // 0x105 is out of the hash: the stub's own work, then the flush
    k.rtClosing = 0;
    assert(g_p5cGyN == 0 && g_gFreeN == 5 && g_gFreeLog[4] == 'E');

    // 3. kept-wait rules (P5cPark false: RT_DESTROY then waits for the job as today)
    g_async.state = A_RUNNING;
    const LONG kw0[4] = {k.p5cKeptWait[0], k.p5cKeptWait[1], k.p5cKeptWait[2], k.p5cKeptWait[3]};
    k.rtClosing = 1;
    assert(!P5cPark(0x106, 0) && k.dpKeptWait == 0);  // closing: not admitted at all
    k.rtClosing = 0;
    g_async.state = A_IDLE;
    assert(!P5cPark(0x106, 0));  // no job
    g_async.state = A_RUNNING;
    g_async.owner = me + 1;
    assert(!P5cPark(0x106, 0) && k.p5cKeptWait[2] == kw0[2] + 1);  // not the owner (sim) thread
    g_async.owner = me;
    g_inInvoke = 1;
    assert(!P5cPark(0x106, 0) && k.p5cKeptWait[2] == kw0[2] + 2);  // inside a job callback
    g_inInvoke = 0;
    g_disc[g_discHead].idLo = 0x106;  // D-X (and design U): a queued record for the id keeps the wait
    g_disc[g_discHead].idHi = 0;
    g_disc[g_discHead].lock = g_rgLock;
    g_disc[g_discHead].pts = NULL;
    g_discCount = 1;
    assert(!P5cPark(0x106, 0) && k.p5cKeptWait[0] == kw0[0] + 1);
    g_disc[g_discHead].lock = (void*)(uintptr_t)0x918;  // a record of another world does not
    assert(P5cPark(0x106, 0) && g_p5cGyN == 1 && GCount(6) == 2);
    g_discCount = 0;
    assert(!P5cPark(0x1FF, 0) && k.p5cKeptWait[3] == kw0[3] + 1);  // not in the hash: waits as today
    assert(k.dpKeptWait == 4 && k.dpParked == 7);

    // 4. many parks: the graveyard fills to its cap (one object parked 511 more times), then a destroy keeps the wait
    for (int n = 1; n < kP5cGrave; n++) assert(P5cPark(0x107, 0));
    assert(g_p5cGyN == kP5cGrave && GCount(7) == kP5cGrave &&k.p5cPendMax == kP5cGrave);
    assert(!P5cPark(0x107, 0) && k.p5cKeptWait[1] == kw0[1] + 1);
    g_async.state = A_IDLE;
    const int rel1 = g_gRelCalls;
    P5cFlush();
    assert(g_p5cGyN == 0 && GCount(6) == 1 && GCount(7) == 1 && g_gRelCalls - rel1 == kP5cGrave && g_gFreeN == 5);

    // 5. T-G1: a parked object whose count is already 0 is counted and not released again
    g_async.state = A_RUNNING;
    assert(P5cPark(0x106, 0));
    GDestroy(6);
    GCount(6) = 0;  // someone dropped our reference: a refcount bug
    g_async.state = A_IDLE;
    const int rel2 = g_gRelCalls;
    P5cFlush();
    assert(k.p5cCountLow == low0 + 1 && g_gRelCalls == rel2 && g_p5cGyN == 0);
    GCount(6) = 1, GCanary(6) = 0xC0FFEE06, g_gInHash[6] = true;

    // 6. save: RT_SAVE waits for the job (none now) and releases what was parked
    g_async.state = A_RUNNING;
    assert(P5cPark(0x106, 0));
    GDestroy(6);
    g_async.state = A_IDLE;
    assert(kRtSites[11].kind == RT_SAVE);
    stack[20] = 0x778;
    RtEnter(11, &stack[20]);
    assert(g_p5cGyN == 0 && GCount(6) == 0 && g_gFreeLog[g_gFreeN - 1] == 'G');
    assert(RgLeave() == 0x778 && g_rgOffSimOpen == 0 && k.rtBusy == 0);

    // 7. world close: a teardown entry during a job keeps the graveyard; travel / quit (RT_CLOSING) waits for the job, then
    //    releases; closing refuses new parks
    GNew(6);
    g_async.state = A_RUNNING;
    assert(P5cPark(0x107, 0));
    GDestroy(7);
    assert(kRtSites[15].kind == RT_TEARDOWN && kRtSites[18].kind == RT_CLOSING);
    RtEnter(15, &stack[20]);
    assert(g_p5cGyN == 1 && GCount(7) == 1);
    reset();
    g_async.state = A_IDLE;
    RtEnter(18, &stack[20]);
    assert(g_p5cGyN == 0 && GCount(7) == 0 && g_gFreeLog[g_gFreeN - 1] == 'H' && k.rtClosing == 1);
    g_async.state = A_RUNNING;  // a job that outlived the drain (not possible: START is refused while closing)
    assert(!P5cPark(0x106, 0));
    k.rtClosing = 0;
    reset();
    assert(k.p5cCountLow == low0 + 1);

    // 8. DestroyParkDx (dxMode 1): a destroy with a queued record for its id parks, runs without the wait, skips D-X 1 and
    //    leaves the record queued; with dxMode 0 the same destroy keeps the wait (case 3)
    GNew(6);
    g_disc[g_discHead].idLo = 0x106, g_disc[g_discHead].idHi = 0, g_disc[g_discHead].lock = g_rgLock, g_disc[g_discHead].pts = NULL;
    g_discCount = 1;
    const LONG dxp0 = k.dxParked, dxl0 = k.dxLeft, kw00 = k.p5cKeptWait[0];
    g_async.state = A_RUNNING;
    assert(!P5cPark(0x106, 0) && !g_p5cParkDx && k.p5cKeptWait[0] == kw00 + 1 && k.dxParked == dxp0);
    k.dxMode = 1;
    const LONG sweep8 = k.discSweepMode;
    k.discSweepMode = 0;  // no sweep: the records would leave footprints behind, so the wait stays
    assert(!P5cPark(0x106, 0) && !g_p5cParkDx && k.p5cKeptWait[0] == kw00 + 2 && k.dxParked == dxp0);
    k.discSweepMode = 1;
    const LONG w8 = k.vfRtDestroyWaitUs;
    destroy(0x106);
    GDestroy(6);
    assert(g_p5cParkDx && k.dxParked == dxp0 + 1 && k.dxLeft == dxl0 + 1 && g_p5cGyN == 1 && GCount(6) == 1);
    assert(g_discCount == 1 && g_disc[g_discHead].idLo == 0x106 && k.vfRtDestroyWaitUs == w8 && k.p5cKeptWait[0] == kw00 + 2);
    GNew(7);
    assert(P5cPark(0x107, 0) && !g_p5cParkDx && k.dxParked == dxp0 + 1);  // no record: a plain park
    GDestroy(7);
    g_async.state = A_IDLE;
    P5cFlush();
    assert(g_p5cGyN == 0 && GCount(6) == 0 && GCount(7) == 0);
    g_discCount = 0, k.dxMode = 0, k.discSweepMode = sweep8;
    reset();

    g_async.state = A_IDLE;
    k.dpMode = 0, k.dpReady = 0;
    k.rtReady = 0;
    k.rgEnforce = 0;
    g_async.owner = 0;
    g_workerTid = savedWorker;
    g_objSvc = savedSvc, g_findRef = savedFind, g_objRelease = savedRel;
    g_rtSimTidSlot = savedSim;
    printf("destroy park (design G standalone): job across deletion, owner-only flush, release order, kept-wait rules, 512 "
           "parks, T-G1, save and world close ok\n");
}

// --- design S2 (0.13.0, docs/84 §3.6): QL frames on the worker, W4, tripwires and the clone's give-up paths on fakes.
// The clone against the game's own code: steerro-test.exe (needs the exe). ---
static int g_srLocoCalls;
static void __fastcall FkLoco(u8*, void*, u8*, const void*, DWORD, const void*, DWORD) { g_srLocoCalls++; }
static u8 __fastcall FkS60(const u8*, void*, DWORD) { return 1; }
static u8 __fastcall FkSFalse(const u8*, void*) { return 0; }
static u8 __fastcall FkSTrue(const u8*, void*) { return 1; }
static int __fastcall FkS8(const u8* o, void*, const void*, DWORD) { return o[0x34]; }
static int __fastcall FkSC(const u8* o, void*) { return o[0x35]; }
static unsigned __int64 __fastcall FkSId(const u8* o, void*) { return *(const unsigned __int64*)(o + 0x14); }
static unsigned __int64 __fastcall FkSubId(const u8* s, void*) { return *(const unsigned __int64*)(s - 4 + 0x14); }
static int __fastcall FkSubType(const u8* s, void*) { return s[-4 + 0x35]; }
static bool g_srE8Touch;
static u8 __fastcall FkSE8(const u8* o, void*, DWORD, const float* a, const float* b) {
    assert(((uintptr_t)a & 15) == 0 && ((uintptr_t)b & 15) == 0);  // 0x80E690 reads them with movaps
    if (g_srE8Touch) (*(u8**)(o + 0x38))[0x10]++;                 // a bounds write by someone else meanwhile
    return 1;
}
static void* g_srFkVt[0x40];
static void* g_srFkSubVt[4];
static u8* SrFakeFp(u8 cls, u8 type, DWORD id) {
    u8* f = (u8*)_aligned_malloc(0x80, 16);
    memset(f, 0, 0x80);
    *(void***)f = g_srFkVt;
    *(void***)(f + 4) = g_srFkSubVt;
    f[0x34] = cls;
    f[0x35] = type;
    *(DWORD*)(f + 0x14) = id;
    u8* cache = (u8*)_aligned_malloc(0x24, 16);
    memset(cache, 0, 0x24);
    cache[0x20] = 1;
    *(u8**)(f + 0x38) = cache;
    return f;
}
static u8 g_srLeaf[0x20];
static u8 g_srIter[0x24];
static bool g_srBumpQ;
static void __fastcall FkWalkPt(const void* q, void*, void* ctx, float, float, float, void* visit, void* list) {
    if (g_srBumpQ) (*(LONG*)((u8*)q + 0x48))++;  // a job's query or pin on the same tree during the walk
    ((u8(__stdcall*)(void*, const u8*, void*))visit)(ctx, g_srIter, list);
}

static void TestSteerRO() {
    static_assert(offsetof(Ts3nCounters, steerReady) == 0x10A0 && offsetof(Ts3nCounters, steerMode) == 0x10A4 &&
                  offsetof(Ts3nCounters, steerShadow) == 0x10A8 && offsetof(Ts3nCounters, steerFb) == 0x10B4 &&
                  offsetof(Ts3nCounters, steerUntracked) == 0x1108 && offsetof(Ts3nCounters, caMode) == 0x1110 &&
                  offsetof(Ts3nCounters, caSimInJob) == 0x1114 && offsetof(Ts3nCounters, caQlHeld) == 0x112C &&
                  sizeof(Ts3nCounters) >= 1100 * 4, "v31 layout");
    Ts3nCounters& k = ts3native_counters;
    memset(&k.steerReady, 0, offsetof(Ts3nCounters, caQlHeld) + 4 - offsetof(Ts3nCounters, steerReady));
    const DWORD me = GetCurrentThreadId();
    static DWORD simTid;
    simTid = me + 3;
    g_rtSimTidSlot = &simTid;
    const DWORD savedWorker = g_workerTid;
    DWORD stack[40] = {};
    auto reset = [&] { t_rg.n = 0; t_rg.readers = 0; };

    // worker: QlEnter holds QL exclusive until the leave; nested entries count up; the leave releases
    g_workerTid = me;
    stack[30] = 0x111;
    QlEnter(4, &stack[30]);
    assert(t_qlDepth == 1 && t_rg.n == 1 && t_rg.f[0].rel == RG_REL_QL && t_rg.readers == 1 && !TryAcquireSRWLockShared(&g_ql));
    stack[20] = 0x222;
    QlEnter(0, &stack[20]);
    assert(t_qlDepth == 2 && k.steerQlHolds == 1);
    assert(RgLeave() == 0x222 && t_qlDepth == 1);
    assert(RgLeave() == 0x111 && t_qlDepth == 0 && TryAcquireSRWLockShared(&g_ql));
    ReleaseSRWLockShared(&g_ql);
    // a frame left without returning is released at the next entry (RgDrop)
    QlEnter(4, &stack[20]);
    QlEnter(4, &stack[30]);  // the slot above the open frame: that frame is gone
    assert(t_qlDepth == 1 && t_rg.n == 1);
    RgLeave();
    assert(t_qlDepth == 0);
    // no frame left (16 open): QL is released at once and the clone is switched off for good
    for (int i = 0; i < 16; i++) RgPush(t_rg, &stack[39 - i], 1);
    QlEnter(4, &stack[2]);
    assert(k.steerUntracked == 1 && g_steerBroken == 1 && t_qlDepth == 0 && TryAcquireSRWLockShared(&g_ql));
    ReleaseSRWLockShared(&g_ql);
    reset();
    // W4: the LotGraph_Build gate stub (site 3) on the worker holds QL; other sites and other threads do not
    static u8 world[0xA00];
    g_rgLock = world + 0x918;
    k.rgReady = 7;
    g_qlOn = 1;
    stack[30] = 0x333;
    RgEnter(3, &stack[30]);
    assert(t_rg.n == 1 && t_rg.f[0].rel == RG_REL_QL && t_qlDepth == 1);
    assert(RgLeave() == 0x333 && t_qlDepth == 0);
    RgEnter(2, &stack[30]);
    assert(t_rg.f[0].rel == 0 && t_qlDepth == 0);
    reset();
    g_workerTid = me + 7;
    RgEnter(3, &stack[30]);
    assert(t_rg.f[0].rel == 0 && t_qlDepth == 0);
    reset();
    k.rgReady = 0;
    g_rgLock = NULL;
    // sim-thread writes during a job are counted; inside the clone the answer is dropped
    simTid = me;
    g_async.state = A_RUNNING;
    QlEnter(5, &stack[30]);
    QlEnter(1, &stack[30]);
    assert(k.steerSimCacheWrite == 1 && k.steerSimTreeWrite == 1 && t_rg.n == 0);
    t_inRo = true;
    QlEnter(5, &stack[30]);
    assert(k.steerSimWriteInRo == 1 && t_srFail == SR_FB_TRIP);
    t_inRo = false;
    t_srFail = 0;
    g_async.state = A_IDLE;
    QlEnter(5, &stack[30]);
    assert(k.steerSimCacheWrite == 2);

    // fix C-A: on the sim thread during a job, an invalid level of one of the four classes takes +0x918 in the W6a stub,
    // i.e. before the original body marks the level valid and frees its polygons; never with QL held
    {
        const LockAcquireFn savedLock = g_lockOrig;
        g_lockOrig = RgTryAcquire;
        g_rgRelOrig = RgFakeRelease;
        InitializeCriticalSection(&g_rgFakeCs);
        if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
        LONG& count = *(LONG*)(g_rgLockObj + 0x20);
        g_rgLock = g_rgLockObj;
        g_async.lock = g_rgLockObj;
        g_async.owner = me;
        g_async.state = A_RUNNING;
        g_caOn = 1;
        static u8 caFp[0x40], caCache[0x24 * 2];
        *(DWORD*)caFp = g_caClasses[1];
        *(u8**)(caFp + 0x38) = caCache;
        DWORD fr[8] = {};
        fr[2] = (DWORD)(uintptr_t)caFp;  // ecx in the pushad frame (slot[-2])
        fr[4] = 0x444;                   // return address (slot)
        fr[5] = 1;                       // level argument (slot[1])
        k.caMode = 0;  // count only
        QlEnter(QL_W6A, &fr[4]);
        assert(k.caSimInJob[1] == 1 && k.caWaits == 0 && count == 0 && t_rg.n == 0);
        k.caMode = 1;
        QlEnter(QL_W6A, &fr[4]);
        assert(count == 1 && k.caWaits == 1 && caCache[0x24 + 0x20] == 0);  // held before the body touches the level
        assert(t_rg.n == 1 && t_rg.f[0].rel == (RG_REL_LOCK | RG_REL_OFFSIM) && fr[4] == (DWORD)(uintptr_t)RgLeaveThunk);
        caCache[0x24 + 0x20] = 1;  // the body ran: level valid
        assert(RgLeave() == 0x444 && count == 0 && g_rgOffSimOpen == 0);
        // valid level, other class, other level index: nothing
        fr[4] = 0x444;
        QlEnter(QL_W6A, &fr[4]);
        *(DWORD*)caFp = 0x00FEBBC0;
        caCache[0x24 + 0x20] = 0;
        QlEnter(QL_W6A, &fr[4]);
        assert(count == 0 && t_rg.n == 0 && k.caSimInJob[1] == 2 && k.caSimInJob[0] + k.caSimInJob[2] + k.caSimInJob[3] == 0);
        // QL held on this thread: counted, no wait (lock order: +0x918 is never awaited under QL)
        *(DWORD*)caFp = g_caClasses[3];
        t_qlDepth = 1;
        QlEnter(QL_W6A, &fr[4]);
        assert(k.caQlHeld == 1 && count == 0 && t_rg.n == 0 && k.caSimInJob[3] == 1);
        t_qlDepth = 0;
        // the +0x918 owner, or a callback served for the job: no wait
        k.rgOwnerTid = (LONG)me;
        QlEnter(QL_W6A, &fr[4]);
        assert(count == 0 && t_rg.n == 0);
        k.rgOwnerTid = 0;
        g_inInvoke = 1;
        QlEnter(QL_W6A, &fr[4]);
        assert(count == 0 && t_rg.n == 0);
        g_inInvoke = 0;
        // no job: the stub does nothing
        g_async.state = A_IDLE;
        QlEnter(QL_W6A, &fr[4]);
        assert(k.caSimInJob[3] == 3 && k.caWaits == 1);  // counted twice above (owner, invoke), not here
        g_caOn = 0;
        k.caMode = 0;
        g_async.lock = NULL;
        g_rgLock = NULL;
        k.rgOwnerTid = 0;
        k.rgAcquires = 0;
        g_lockOrig = savedLock;
        DeleteCriticalSection(&g_rgFakeCs);
    }

    // the clone's pieces on fakes (slot whitelist off: these vtables are ours)
    g_srCheckSlots = false;
    const float* savedOne = g_srOne;
    static const float one = 1.0f;
    g_srOne = &one;  // 0x107A538 does not exist here
    g_srFkVt[0x60 / 4] = (void*)FkS60;
    g_srFkVt[0x28 / 4] = (void*)FkSFalse;
    g_srFkVt[2] = (void*)FkS8;
    g_srFkVt[3] = (void*)FkSC;
    g_srFkVt[0x84 / 4] = (void*)FkSId;
    g_srFkVt[0xC0 / 4] = (void*)FkSTrue;
    g_srFkVt[0xE8 / 4] = (void*)FkSE8;
    g_srFkSubVt[1] = (void*)FkSubId;
    g_srFkSubVt[2] = (void*)FkSubType;
    // a leaf with a heap list: class 3 type 5; class 2 (local list) type 5; type 0x1F ("seen"); class 1; class 0xD; id 0
    u8* fps[6] = {SrFakeFp(3, 5, 0x10), SrFakeFp(2, 5, 0x11), SrFakeFp(3, 0x1F, 0x12), SrFakeFp(1, 5, 0x13), SrFakeFp(0xD, 5, 0x14),
                  SrFakeFp(3, 5, 0)};
    u8 ents[6][0x20] = {};
    DWORD list[6][3];
    for (int i = 0; i < 6; i++) {
        *(u8**)(ents[i] + 8) = fps[i];
        list[i][0] = (DWORD)(uintptr_t)ents[i];
        list[i][1] = list[i][2] = 0xFFFFFFFF;
    }
    *(DWORD*)(g_srLeaf + 8) = (DWORD)(uintptr_t)list | 1;
    *(DWORD*)(g_srLeaf + 0xC) = 6;
    static SrCtx c;
    auto collect = [&](const u8* rec, DWORD flag) {
        c.n = 0;
        c.fail = 0;
        c.rec = rec;
        c.flag = flag;
        SrCollect(&c, g_srLeaf, false, 0);
    };
    collect(NULL, 1);  // EditPath's filter: class >= 2 and not 0xD; class 2 only with a "seen" entry in the same leaf
    assert(!c.fail && c.n == 3 && c.objs[0] == fps[0] && c.objs[1] == fps[5] && c.objs[2] == fps[1]);
    fps[2][0x35] = 5;
    collect(NULL, 1);
    assert(c.n == 3 && c.objs[0] == fps[0] && c.objs[1] == fps[2] && c.objs[2] == fps[5]);  // no "seen": class 2 dropped
    __declspec(align(16)) u8 rec[0x30] = {};
    collect(rec, 0);  // the obstacle query: all classes, but not the record's own id (0)
    assert(c.n == 5 && c.objs[4] == fps[4]);
    // masked lists: the entry's bit for the sub-cell
    list[0][1] = 0;
    list[0][2] = 1u << 3;
    c.n = 0;
    c.fail = 0;
    c.rec = rec;
    c.flag = 0;
    SrCollect(&c, g_srLeaf, true, 35);
    assert(c.n == 5);
    c.n = 0;
    SrCollect(&c, g_srLeaf, true, 3);
    assert(c.n == 4);  // fps[0] not in sub-cell 3
    list[0][1] = list[0][2] = 0xFFFFFFFF;
    // a NULL footprint (the original would dereference it), the whitelist, overflow: give up
    *(u8**)(ents[3] + 8) = NULL;
    collect(rec, 0);
    assert(c.fail == SR_FB_SHAPE && t_srShape == SRS_NULLFP);
    *(u8**)(ents[3] + 8) = fps[3];
    g_srCheckSlots = true;
    collect(rec, 0);
    assert(c.fail == SR_FB_SHAPE && t_srShape == SRS_SLOT);
    g_srCheckSlots = false;
    c.n = kSrCap;  // full: the walk goes on counting (v39 s2MaxObjs), SrWalk gives up after it
    memset(c.objs, 0, sizeof(c.objs));  // none of the leaf's footprints stored yet
    c.fail = 0;
    c.over = 0;
    c.flag = 0;
    SrCollect(&c, g_srLeaf, false, 0);
    assert(!c.fail && c.n == kSrCap && c.over == 5);
    c.over = 0;

    // walks: the deferred flag with the pin counter below 1 (the original flushes first), the pin counter moving meanwhile
    static u8 q[0x60];
    *(u8**)(g_srIter + 0xC) = g_srLeaf;
    g_srIter[0x1C] = 0;
    const SrWalkPtFn savedWalk = g_srWalkPt;
    g_srWalkPt = FkWalkPt;
    q[0x4C] = 1;
    *(LONG*)(q + 0x48) = 0;
    c.fail = 0;
    c.n = 0;
    assert(SrWalk(&c, q, true, 0, 0, 1) == SR_FB_DEFERRED && c.n == 0);
    *(LONG*)(q + 0x48) = 1;
    assert(SrWalk(&c, q, true, 0, 0, 1) == SR_OK && c.n == 5);
    g_srBumpQ = true;
    c.n = 0;
    assert(SrWalk(&c, q, true, 0, 0, 1) == SR_FB_TRIP && k.steerPinMoved == 1);
    g_srBumpQ = false;
    g_srWalkPt = savedWalk;

    // EditPath's consumer: the level-0 test needs a valid cache, and the cache must not change under it (T3)
    static u8 entry[0x100];  // lot entry: rows identity
    float* rows = (float*)(((uintptr_t)entry + 15) & ~(uintptr_t)15);
    (void)rows;
    SrNode nd = {};
    static float rawPt[4] = {1, 0, 2, 0};
    nd.raw = (const u8*)rawPt;
    nd.eLot = false;
    static SrOut out;
    out.nIds = 0;
    c.n = 1;
    c.objs[0] = fps[0];
    const SrIdListFn savedIdList = g_srIdList;
    g_srIdList = [](DWORD lo, DWORD hi, const void* l, DWORD n) -> u8 {  // IdList_Contains 0x85AAA0
        for (DWORD i = 0; l && i < n; i++) if (((const DWORD*)l)[i * 2] == lo && ((const DWORD*)l)[i * 2 + 1] == hi) return 1;
        return 0;
    };
    assert(SrConsume(&c, NULL, nd, NULL, 0, &out, NULL, 0, NULL, 0, NULL, 0) == SR_OK && out.nIds == 1 && out.ids[0][0] == 0x10);
    // ignore list, ids already present, exclude list: no second append
    const DWORD have[2] = {0x10, 0};
    out.nIds = 0;
    assert(SrConsume(&c, NULL, nd, have, 1, &out, NULL, 0, NULL, 0, NULL, 0) == SR_OK && out.nIds == 0);
    assert(SrConsume(&c, NULL, nd, NULL, 0, &out, have, 1, NULL, 0, NULL, 0) == SR_OK && out.nIds == 0);
    assert(SrConsume(&c, NULL, nd, NULL, 0, &out, NULL, 0, have, 1, NULL, 0) == SR_OK && out.nIds == 0);
    // with a lot for the level-0 test: an invalid cache gives up (the original would rebuild it), a changed one too (T3)
    static u8 p[0x200], lotNode[0x200];
    static DWORD buckets[2];
    u8* ln = (u8*)(((uintptr_t)lotNode + 15) & ~(uintptr_t)15);
    *(DWORD*)ln = 0x20;
    float* r = (float*)(ln + 0x10 + 0xA0);
    r[0] = r[5] = r[10] = r[15] = 1;
    buckets[0] = (DWORD)(uintptr_t)ln;
    buckets[1] = 0x5E117E1;
    *(DWORD**)(p + 0x4C) = buckets;
    *(DWORD*)(p + 0x50) = 1;
    const SrLotFindFn savedFind = g_srLotFind;
    g_srLotFind = [](const void* tbl, void*, DWORD* o, const DWORD* key) {  // LotTable_Find 0x627E00, one bucket
        const u8* n = (const u8*)(uintptr_t)(*(const DWORD* const*)((const u8*)tbl + 4))[0];
        o[0] = *(const DWORD*)n == key[0] ? (DWORD)(uintptr_t)n : 0x5E117E1;
        o[1] = 0;
    };
    nd.eLot = true;
    nd.eLo = 0x20;
    (*(u8**)(fps[0] + 0x38))[0x20] = 0;
    assert(SrConsume(&c, p, nd, NULL, 0, &out, NULL, 0, NULL, 0, NULL, 0) == SR_FB_CACHE);
    (*(u8**)(fps[0] + 0x38))[0x20] = 1;
    g_srE8Touch = true;
    assert(SrConsume(&c, p, nd, NULL, 0, &out, NULL, 0, NULL, 0, NULL, 0) == SR_FB_CACHE);
    g_srE8Touch = false;
    out.nIds = 0;
    assert(SrConsume(&c, p, nd, NULL, 0, &out, NULL, 0, NULL, 0, NULL, 0) == SR_OK && out.nIds == 1);
    nd.eLo = 0x21;  // EditPath's lot not in the table: GetOrInsert would insert it
    assert(SrConsume(&c, p, nd, NULL, 0, &out, NULL, 0, NULL, 0, NULL, 0) == SR_FB_LOTMISS);
    // Contains rules: key 0 and -1 and the active lot have no entry; a key mismatch at the entry neither
    static u8 act[0x60];
    static const u8* actp;
    actp = act;
    const SrActiveLotFn savedAct = g_srActiveLot;
    g_srActiveLot = []() -> const u8* { return actp; };
    const u8* e;
    assert(SrEntry(p, 0, 0, true, &e) == SR_OK && !e);
    assert(SrEntry(p, 0xFFFFFFFF, 0xFFFFFFFF, true, &e) == SR_OK && !e);
    *(DWORD*)(act + 0x48) = 0x20;
    assert(SrEntry(p, 0x20, 0, true, &e) == SR_OK && !e);
    *(DWORD*)(act + 0x48) = 0x99;
    assert(SrEntry(p, 0x20, 0, true, &e) == SR_OK && !e);  // node+0x10 holds no key yet
    *(DWORD*)(ln + 0x10) = 0x20;
    assert(SrEntry(p, 0x20, 0, true, &e) == SR_OK && e == ln + 0x10);
    assert(SrEntry(p, 0x21, 0, true, &e) == SR_OK && !e);
    actp = NULL;
    assert(SrEntry(p, 0x20, 0, true, &e) == SR_FB_SHAPE && t_srShape == SRS_NOACTLOT);
    static_assert(offsetof(Ts3nCounters, p10Ready) == 0x1424 && offsetof(Ts3nCounters, s2Shape) == 0x1464 &&
                      offsetof(Ts3nCounters, s2MaxObjs) == 0x1490 && offsetof(Ts3nCounters, s2MaxDepth) == 0x14A0 &&
                      sizeof(Ts3nCounters) >= 1321 * 4 && SRS_N == 11,
                  "v39 layout");
    const LONG sh0 = k.s2Shape[SRS_NOACTLOT], fbs0 = k.steerFb[SR_FB_SHAPE - 1];
    SrFallback(SR_FB_SHAPE);  // counted under the last site
    assert(k.s2Shape[SRS_NOACTLOT] == sh0 + 1 && k.steerFb[SR_FB_SHAPE - 1] == fbs0 + 1);
    // cell graph bounds (level base +0x40, vector +0x74/+0x78)
    static DWORD qv[2] = {0x1234, 0x5678};
    *(LONG*)(ln + 0x10 + 0x40) = 1;
    *(DWORD**)(ln + 0x10 + 0x74) = qv;
    *(DWORD**)(ln + 0x10 + 0x78) = qv + 2;
    assert(!SrCell(ln + 0x10, 0) && SrCell(ln + 0x10, 1) == (const u8*)0x1234 && SrCell(ln + 0x10, 2) == (const u8*)0x5678 && !SrCell(ln + 0x10, 3));
    g_srActiveLot = savedAct;
    g_srLotFind = savedFind;
    g_srIdList = savedIdList;

    // the hook: no switch, not the owner, a job with QL held elsewhere, the job's callback, the broken flag
    g_locoOrig = FkLoco;
    static u8 agent[0x100], node[0x40];
    *(u8**)(agent + 8) = node;
    *(u8**)node = agent + 8;
    node[0x30] = 1;
    static DWORD idsv[3];
    g_srLocoCalls = 0;
    LocoHook(agent, NULL, (u8*)idsv, NULL, 0, NULL, 0);
    assert(g_srLocoCalls == 1 && k.steerCalls == 0);
    k.steerMode = 1;
    g_async.owner = me + 1;
    LocoHook(agent, NULL, (u8*)idsv, NULL, 0, NULL, 0);
    assert(g_srLocoCalls == 2 && k.steerCalls == 0);
    g_async.owner = me;
    g_async.state = A_RUNNING;
    g_twArmed = 1;  // the worker arms the tripwire stubs for its job (0.18.0)
    g_steerBroken = 0;
    g_inInvoke = 1;
    LocoHook(agent, NULL, (u8*)idsv, NULL, 0, NULL, 0);
    assert(g_srLocoCalls == 3 && k.steerFb[SR_FB_STATE - 1] == 1);
    g_inInvoke = 0;
    g_steerBroken = 1;
    LocoHook(agent, NULL, (u8*)idsv, NULL, 0, NULL, 0);
    assert(g_srLocoCalls == 4 && k.steerFb[SR_FB_STATE - 1] == 2);
    g_steerBroken = 0;
    // QL held exclusive by another thread (a worker writer): try fails, today's call (fake world: World_Get answers it)
    static u8 fworld[0xA00], fwm[0x200], fp[0x200];
    fworld[0x974] = 1;
    *(u8**)(fworld + 0x95C) = fp;
    *(u8**)(fworld + 0x964) = fwm;
    static u8* fwmSlot;
    fwmSlot = fwm;
    const SrWorldFn savedWorld = g_srWorldGet;
    u8* const* savedWm = g_srWmSlot;
    const SrConvFn savedConv = g_srConv;
    const SrLotLookupFn savedLook = g_srLotLookup;
    g_srWorldGet = []() -> u8* { return fworld; };
    g_srWmSlot = &fwmSlot;
    g_srConv = [](const void*, void*) {};
    g_srLotLookup = [](void*, void*, float, float) -> u8* { return NULL; };
    HANDLE held = CreateEventW(NULL, TRUE, FALSE, NULL), go = CreateEventW(NULL, TRUE, FALSE, NULL);
    struct Hold { HANDLE held, go; } h = {held, go};
    HANDLE th = CreateThread(NULL, 0, [](void* p) -> DWORD {
        Hold* hh = (Hold*)p;
        AcquireSRWLockExclusive(&g_ql);
        SetEvent(hh->held);
        WaitForSingleObject(hh->go, 5000);
        ReleaseSRWLockExclusive(&g_ql);
        return 0;
    }, &h, 0, NULL);
    WaitForSingleObject(held, 5000);
    LocoHook(agent, NULL, (u8*)idsv, NULL, 0, NULL, 0);
    assert(g_srLocoCalls == 5 && k.steerFb[SR_FB_TRY - 1] == 1);
    SetEvent(go);
    WaitForSingleObject(th, 5000);
    CloseHandle(th);
    CloseHandle(held);
    CloseHandle(go);
    // QL free, no lot anywhere: the read-only answer is "nothing to add" without the game's call
    const LONG taken0 = k.steerTaken;
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
    LocoHook(agent, NULL, (u8*)idsv, NULL, 0, NULL, 0);
    assert(g_srLocoCalls == 5 && k.steerTaken == taken0 + 1);
    // a dirty lot index: give up
    fwm[0x1CC] = 1;
    LocoHook(agent, NULL, (u8*)idsv, NULL, 0, NULL, 0);
    assert(g_srLocoCalls == 6 && k.steerFb[SR_FB_DIRTY - 1] == 1);
    fwm[0x1CC] = 0;
    g_srWorldGet = savedWorld;
    g_srWmSlot = savedWm;
    g_srConv = savedConv;
    g_srLotLookup = savedLook;
    g_srOne = savedOne;
    g_srCheckSlots = true;
    g_async.state = A_IDLE;
    g_twArmed = 0;
    g_async.owner = 0;
    g_qlOn = 0;
    g_workerTid = savedWorker;
    k.steerMode = 0;
    simTid = 0;
    for (u8* f : fps) { _aligned_free(*(u8**)(f + 0x38)); _aligned_free(f); }
}

// --- S2 tripwires T4 and T-S7 (0.15.0): who is counted, the frame and its leave, a clone dropping its answer ---
// One tripwire call on a thread that is neither the sim nor (unless `worker`) the worker: enter, optionally block, leave.
struct TwCall { int site; bool worker; LONG qlDepth; HANDLE in, go; int nested; int fill; };
static DWORD WINAPI TwThread(void* p) {
    TwCall* c = (TwCall*)p;
    if (c->worker) g_workerTid = GetCurrentThreadId();
    t_qlDepth = c->qlDepth;
    DWORD stack[40] = {};
    for (int i = 0; i < c->fill; i++) RgPush(t_rg, &stack[39 - i], 0);  // frames already open on this thread
    stack[20] = 0x555;
    TwEnter(c->site, &stack[20]);
    if (c->nested) {  // a second tripwire call inside the first: the outer frame covers it
        stack[10] = 0x666;
        TwEnter(c->nested, &stack[10]);
        assert(stack[10] == 0x666 && t_twDepth == 1);
    }
    if (c->in) { SetEvent(c->in); WaitForSingleObject(c->go, 5000); }
    if (stack[20] != 0x555) assert(RgLeave() == 0x555);  // the site returned through the leave thunk
    assert(t_twDepth == 0);
    t_qlDepth = 0;
    return 0;
}
static void TwRun(TwCall c) {
    HANDLE t = CreateThread(NULL, 0, TwThread, &c, 0, NULL);
    WaitForSingleObject(t, 5000);
    CloseHandle(t);
}
static int g_twFkMode;  // inside the clone (from the fake active-lot lookup): 1 = a foreign tripwire call starts and ends;
                        // 2 = the job ends (the worker disarms the stubs); 3 = the job ends and the next one arms them again
static u8 g_twAct[0x60];
static const u8* TwFkActiveLot() {
    if (g_twFkMode == 1) TwRun({6});
    if (g_twFkMode >= 2) TwArm(false);
    if (g_twFkMode == 3) TwArm(true);
    return g_twAct;
}

static void TestSteerTrip() {
    static_assert(offsetof(Ts3nCounters, steerTwStubs) == 0x131C && offsetof(Ts3nCounters, steerT4Bare) == 0x1320 &&
                  offsetof(Ts3nCounters, steerS7Foreign) == 0x1324 && offsetof(Ts3nCounters, steerS7Sim) == 0x1328 &&
                  offsetof(Ts3nCounters, steerTripDiscard) == 0x132C && offsetof(Ts3nCounters, steerTwSite) == 0x1330 &&
                  sizeof(Ts3nCounters) >= 1239 * 4, "v33 layout");
    static_assert(offsetof(Ts3nCounters, rfbLogMs) == 0x1384 && offsetof(Ts3nCounters, s2TscPerUs) == 0x13A4 &&
                  offsetof(Ts3nCounters, s2EntryKt) == 0x13B0 && offsetof(Ts3nCounters, s2TwCalls) == 0x13B8 &&
                  offsetof(Ts3nCounters, s2QlCalls) == 0x13C4 && offsetof(Ts3nCounters, s2ShadowKt) == 0x13D0 &&
                  offsetof(Ts3nCounters, s2TryMaxT) == 0x13E0 && sizeof(Ts3nCounters) >= 1273 * 4, "v36 layout");
    static_assert(TW_N == 11 && TW_T4 == 4, "sites");
    Ts3nCounters& k = ts3native_counters;
    memset(&k.steerTwStubs, 0, sizeof(Ts3nCounters) - offsetof(Ts3nCounters, steerTwStubs));
    const LONG fb0 = k.steerFb[SR_FB_TRIP - 1], inRo0 = k.steerSimWriteInRo, unt0 = k.steerUntracked;
    const DWORD me = GetCurrentThreadId();
    static DWORD simTid;
    simTid = me;
    const DWORD* const savedSimSlot = g_rtSimTidSlot;
    g_rtSimTidSlot = &simTid;
    const DWORD savedWorker = g_workerTid;
    g_workerTid = me + 7;
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);

    // no job and no clone: nothing counted, nothing held
    LONG e0 = g_twEpoch;
    TwRun({4});
    assert(k.steerS7Foreign == 0 && g_twActive == 0 && g_twEpoch == e0);
    // a job: T-S7 on another thread is counted, held for the call, the epoch moves on the leave
    g_async.state = A_RUNNING;
    TwRun({4});
    assert(k.steerS7Foreign == 1 && k.steerTwSite[4] == 1 && g_twActive == 0 && g_twEpoch == e0 + 1);
    TwRun({0});  // T4 on a thread outside QL
    assert(k.steerT4Bare == 1 && k.steerTwSite[0] == 1 && g_twEpoch == e0 + 2);
    // the worker: T4 inside a W frame (QL depth > 0) is covered; outside it counts; T-S7 counts either way
    TwRun({1, true, 1});
    assert(k.steerT4Bare == 1 && g_twEpoch == e0 + 2);
    TwRun({1, true, 0});
    assert(k.steerT4Bare == 2 && k.steerTwSite[1] == 1);
    TwRun({5, true, 1});
    assert(k.steerS7Foreign == 2 && k.steerTwSite[5] == 1);
    g_workerTid = me + 7;
    // the sim thread: T-S7 by design (counted apart), T4 not at all; inside the clone either one drops the answer
    DWORD fr[4] = {0, 0x777, 0, 0};
    const int rgN = t_rg.n;
    TwEnter(4, &fr[1]);
    TwEnter(2, &fr[1]);
    assert(k.steerS7Sim == 1 && k.steerS7Foreign == 2 && k.steerT4Bare == 2 && fr[1] == 0x777 && t_rg.n == rgN);
    t_inRo = true;
    TwEnter(3, &fr[1]);
    assert(t_srFail == SR_FB_TRIP && k.steerSimWriteInRo == inRo0 + 1 && fr[1] == 0x777);
    t_inRo = false;
    t_srFail = 0;
    // nested on one thread: one frame, the inner call only moves the epoch
    e0 = g_twEpoch;
    TwRun({6, false, 0, NULL, NULL, 7});
    assert(k.steerTwSite[6] == 1 && k.steerTwSite[7] == 1 && g_twActive == 0 && g_twEpoch == e0 + 2);
    // no frame left (16 open): the call is counted, the clone is switched off for good (it could not see this writer leave)
    TwRun({8, false, 0, NULL, NULL, 0, 16});
    assert(k.steerTwSite[8] == 1 && g_steerBroken == 1 && k.steerUntracked == unt0 + 1 && g_twActive == 0);
    g_steerBroken = 0;

    // the clone: a fake world whose only flagged node lies on a lot the active-lot check rejects (no walk, nothing appended)
    static u8 fworld[0xA00], fwm[0x200], fp[0x200], flot[0x60];
    fworld[0x974] = 1;
    *(u8**)(fworld + 0x95C) = fp;
    *(u8**)(fworld + 0x964) = fwm;
    *(DWORD*)(flot + 0x48) = 0x55;
    *(DWORD*)(g_twAct + 0x48) = 0x55;
    static u8* fwmSlot;
    fwmSlot = fwm;
    const SrWorldFn savedWorld = g_srWorldGet;
    u8* const* savedWm = g_srWmSlot;
    const SrConvFn savedConv = g_srConv;
    const SrLotLookupFn savedLook = g_srLotLookup;
    const SrActiveLotFn savedAct = g_srActiveLot;
    const float* savedOne = g_srOne;
    static const float one = 1.0f;
    g_srOne = &one;
    g_srWorldGet = []() -> u8* { return fworld; };
    g_srWmSlot = &fwmSlot;
    g_srConv = [](const void*, void*) {};
    g_srLotLookup = [](void*, void*, float, float) -> u8* { return flot; };
    g_srActiveLot = TwFkActiveLot;
    g_locoOrig = FkLoco;
    g_srLocoCalls = 0;
    static u8 agent[0x100], node[0x40];
    *(u8**)(agent + 8) = node;
    *(u8**)node = agent + 8;
    node[0x30] = 1;
    static DWORD idsv[3];
    g_async.owner = me;
    k.steerMode = 1;
    // the stubs not armed (a job seen running, but the worker has already disarmed): no answer, the game's call (0.18.0)
    const LONG tkA = k.steerTaken;
    LocoHook(agent, NULL, (u8*)idsv, NULL, 0, NULL, 0);
    assert(g_srLocoCalls == 1 && k.s2JobEndDrop == 1 && k.steerTaken == tkA && g_srInClone == 0);
    g_srLocoCalls = 0;
    TwArm(true);
    assert(g_twArmed == 1 && k.s2ArmJobs == 1);
    // no tripwire call: a read-only answer
    const LONG tk0 = k.steerTaken;
    LocoHook(agent, NULL, (u8*)idsv, NULL, 0, NULL, 0);
    assert(g_srLocoCalls == 0 && k.steerTaken == tk0 + 1 && k.steerTripDiscard == 0 && g_srInClone == 0);
    // the job ends during the clone (disarm), with or without the next job's arm before the clone ends: writers after the
    // disarm were not seen, so the answer is dropped (generation moved)
    for (int m = 2; m <= 3; m++) {
        const LONG d0 = k.s2JobEndDrop, c0 = g_srLocoCalls;
        g_twFkMode = m;
        LocoHook(agent, NULL, (u8*)idsv, NULL, 0, NULL, 0);
        g_twFkMode = 0;
        assert(g_srLocoCalls == c0 + 1 && k.s2JobEndDrop == d0 + 1 && k.steerTaken == tk0 + 1 && g_srInClone == 0);
        TwArm(true);
    }
    g_srLocoCalls = 0;
    assert(k.s2EntryMaxT > 0 && k.s2TryCalls == 4 && k.steerTaken == tk0 + 1);
    // a writer already running on another thread: the clone gives up at once, the game's call runs
    HANDLE in = CreateEventW(NULL, FALSE, FALSE, NULL), go = CreateEventW(NULL, FALSE, FALSE, NULL);
    TwCall blk = {9, false, 0, in, go};
    HANDLE th = CreateThread(NULL, 0, TwThread, &blk, 0, NULL);
    WaitForSingleObject(in, 5000);
    assert(g_twActive == 1);
    LocoHook(agent, NULL, (u8*)idsv, NULL, 0, NULL, 0);
    assert(g_srLocoCalls == 1 && k.steerTripDiscard == 1 && k.steerFb[SR_FB_TRIP - 1] == fb0 + 1 && k.steerTaken == tk0 + 1);
    SetEvent(go);
    WaitForSingleObject(th, 5000);
    CloseHandle(th);
    assert(g_twActive == 0);
    // a writer that starts and ends inside the clone: the epoch moved, the answer is dropped
    g_twFkMode = 1;
    LocoHook(agent, NULL, (u8*)idsv, NULL, 0, NULL, 0);
    assert(g_srLocoCalls == 2 && k.steerTripDiscard == 2 && k.steerFb[SR_FB_TRIP - 1] == fb0 + 2 && k.steerTwSite[6] == 2);
    // shadow (no job, stubs disarmed): the clone's window alone makes the call count, and the clone's answer is dropped
    g_async.state = A_IDLE;
    TwArm(false);
    k.steerShadow = 1;
    LocoHook(agent, NULL, (u8*)idsv, NULL, 0, NULL, 0);
    assert(g_srLocoCalls == 3 && k.steerTwSite[6] == 3 && k.steerTripDiscard == 3 && k.steerShadowMismatch == 0 &&
           g_rgOffSimOpen == 0 && g_srInClone == 0);
    g_twFkMode = 0;
    k.steerShadow = 0;
    assert(k.steerS7Foreign == 8 && k.steerT4Bare == 2 && g_twActive == 0);
    CloseHandle(in);
    CloseHandle(go);
    g_srWorldGet = savedWorld;
    g_srWmSlot = savedWm;
    g_srConv = savedConv;
    g_srLotLookup = savedLook;
    g_srActiveLot = savedAct;
    g_srOne = savedOne;
    g_async.owner = 0;
    k.steerMode = 0;
    g_workerTid = savedWorker;
    g_rtSimTidSlot = savedSimSlot;
}

// --- design L1 and probe P1b (0.14.0, lotfd.cpp): clone rules on fakes, no writes, QL rules, hook dispatch, P1b keys, T-L3 ---
static u64 g_fdSkew;
static int g_fdCalls;
static u64 __fastcall FkFd(const u8* p, void*, DWORD lo, DWORD hi, DWORD type, const u8** out) {  // stands for 0x631800
    g_fdCalls++;
    u64 r;
    const u8* pt;
    LtFrontDoor(p, lo, hi, type, &r, &pt);
    if (out && pt) *out = pt;
    return r + g_fdSkew;
}
static LONG __fastcall FkPortalAddRef(u8* o, void*) { return ++*(LONG*)(o + 0x70); }
static LONG __fastcall FkPortalRelease(u8* o, void*) { return --*(LONG*)(o + 0x70); }
static DWORD Fnv32(const void* p, size_t n) {
    DWORD h = 2166136261u;
    for (size_t i = 0; i < n; i++) h = (h ^ ((const u8*)p)[i]) * 16777619u;
    return h;
}

static void TestLotFd() {
    static_assert(offsetof(Ts3nCounters, ltReady) == 0x1130 && offsetof(Ts3nCounters, ltMode) == 0x1134 &&
                  offsetof(Ts3nCounters, ltShadow) == 0x1138 && offsetof(Ts3nCounters, ltFb) == 0x1144 &&
                  offsetof(Ts3nCounters, ltUs) == 0x115C && offsetof(Ts3nCounters, ltShadowMismatch) == 0x1168 &&
                  offsetof(Ts3nCounters, ltQlHeldBlock) == 0x116C && offsetof(Ts3nCounters, portalRef) == 0x1170 &&
                  offsetof(Ts3nCounters, p1bReady) == 0x1190 && offsetof(Ts3nCounters, p1bKey) == 0x1194 &&
                  offsetof(Ts3nCounters, p1bN) == 0x1214 && offsetof(Ts3nCounters, p1bUs) == 0x1294 &&
                  offsetof(Ts3nCounters, p1bOther) == 0x1314 && offsetof(Ts3nCounters, p1bNoFrame) == 0x1318 &&
                  sizeof(Ts3nCounters) >= 1223 * 4, "v32 layout");
    Ts3nCounters& k = ts3native_counters;
    memset(&k.ltReady, 0, offsetof(Ts3nCounters, p1bNoFrame) + 4 - offsetof(Ts3nCounters, ltReady));
    const DWORD me = GetCurrentThreadId();
    static DWORD simTid;
    simTid = me;
    g_rtSimTidSlot = &simTid;
    const DWORD savedWorker = g_workerTid;
    g_workerTid = me + 7;
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);

    // a lot table: P+0x48, buckets +0x4C (2 + the end slot), count +0x50; lots 0x10 (head of bucket 0), 0x12 (behind it),
    // 0x13 (bucket 1; its node+0x10 holds another key); the active lot 0x99
    static u8 p[0x200], wm[0x200], act[0x60], nodes[3][0x1A0], portals[3][0x80];
    static DWORD bk[3], vt[2], vec[4];
    static u8* wmSlot;
    wmSlot = wm;
    u8* const* savedWm = g_srWmSlot;
    g_srWmSlot = &wmSlot;
    *(u8**)(wm + 0xB0) = act;
    *(DWORD*)(act + 0x48) = 0x99;
    const DWORD keys[3] = {0x10, 0x12, 0x13};
    for (int i = 0; i < 3; i++) {
        *(DWORD*)nodes[i] = keys[i];
        *(DWORD*)(nodes[i] + 0x10) = keys[i];
    }
    *(DWORD*)(nodes[2] + 0x10) = 0x77;
    bk[0] = (DWORD)(uintptr_t)nodes[0];
    *(u8**)(nodes[0] + 0x190) = nodes[1];
    bk[1] = (DWORD)(uintptr_t)nodes[2];
    bk[2] = 0x5E117E1;
    *(DWORD**)(p + 0x4C) = bk;
    *(DWORD*)(p + 0x50) = 2;
    // lot 0x12's entry: 3 portals (the middle one NULL), front door indices +0x18 = 0, +0x1C = 2
    u8* e = nodes[1] + 0x10;
    vt[0] = (DWORD)(uintptr_t)FkPortalAddRef;
    vt[1] = (DWORD)(uintptr_t)FkPortalRelease;
    for (int i = 0; i < 3; i++) {
        *(DWORD**)portals[i] = vt;
        *(DWORD*)(portals[i] + 8) = 0x1000 + i;
        *(DWORD*)(portals[i] + 0xC) = 0xA0 + i;
        *(LONG*)(portals[i] + 0x70) = 1;
        vec[i] = i == 1 ? 0 : (DWORD)(uintptr_t)portals[i];
    }
    *(DWORD**)(e + 0x44) = vec;
    *(DWORD**)(e + 0x48) = vec + 3;
    *(DWORD*)(e + 0x18) = 0;
    *(DWORD*)(e + 0x1C) = 2;
    const DWORD savedAdd = g_ltAddRef, savedRel = g_ltRelease;
    g_ltAddRef = vt[0];
    g_ltRelease = vt[1];
    auto sum = [&] { return Fnv32(p, sizeof(p)) ^ Fnv32(nodes, sizeof(nodes)) ^ Fnv32(portals, sizeof(portals)) ^ Fnv32(vec, sizeof(vec)) ^ Fnv32(act, sizeof(act)); };
    auto fd = [&](DWORD lo, DWORD hi, DWORD type, u64 want, const u8* wantPt) {
        u64 r;
        const u8* pt;
        const DWORD before = sum();
        const int why = LtFrontDoor(p, lo, hi, type, &r, &pt);
        assert(sum() == before);  // the clone writes nothing (no refcount either)
        if (!why) assert(r == want && pt == wantPt);
        return why;
    };
    const u64 id0 = 0xA0ull << 32 | 0x1000, id2 = 0xA2ull << 32 | 0x1002;
    assert(fd(0, 0, 0, 0, NULL) == 0 && fd(0xFFFFFFFF, 0xFFFFFFFF, 0, 0, NULL) == 0);  // Contains rejects
    *(DWORD*)(act + 0x48) = 0x12;
    assert(fd(0x12, 0, 0, 0, NULL) == 0);  // the active lot
    *(DWORD*)(act + 0x48) = 0x99;
    assert(fd(0x12, 0, 0, id2, portals[2]) == 0);  // type 0: +0x1C valid
    assert(fd(0x12, 0, 1, id2, portals[2]) == 0 && fd(0x12, 0, 2, id0, portals[0]) == 0 && fd(0x12, 0, 3, 0, NULL) == 0);
    assert(fd(0x12, 1, 0, 0, NULL) == 0 && fd(0x14, 0, 0, 0, NULL) == 0);  // chain miss (hi differs), chain end
    assert(fd(0x13, 0, 0, 0, NULL) == 0);  // node+0x10 holds another key: Contains says no
    *(DWORD*)(e + 0x1C) = 0xFFFFFFFF;
    assert(fd(0x12, 0, 0, id0, portals[0]) == 0 && fd(0x12, 0, 1, 0, NULL) == 0);  // -1: type 0 takes +0x18, type 1 none
    *(DWORD*)(e + 0x1C) = 3;
    assert(fd(0x12, 0, 0, id0, portals[0]) == 0 && fd(0x12, 0, 1, 0, NULL) == 0);  // >= n: the same
    *(DWORD*)(e + 0x18) = 1;
    assert(fd(0x12, 0, 0, 0, NULL) == 0 && fd(0x12, 0, 2, 0, NULL) == 0);  // NULL portal
    *(DWORD*)(e + 0x18) = 0xFFFFFFFF;
    assert(fd(0x12, 0, 2, 0, NULL) == 0);
    *(DWORD*)(e + 0x18) = 0;
    *(DWORD*)(e + 0x1C) = 2;
    assert(fd(0x10, 0, 0, 0, NULL) == 0);  // head of the chain, no portals
    // doubts: slots that are not the count pair, a count below 1 (the original's Release would delete), no table, no active lot
    vt[1] = (DWORD)(uintptr_t)FkPortalAddRef;
    assert(fd(0x12, 0, 0, 0, NULL) == LT_FB_SHAPE);
    vt[1] = (DWORD)(uintptr_t)FkPortalRelease;
    *(LONG*)(portals[2] + 0x70) = 0;
    assert(fd(0x12, 0, 0, 0, NULL) == LT_FB_SHAPE);
    *(LONG*)(portals[2] + 0x70) = 1;
    *(DWORD*)(p + 0x50) = 0;
    assert(fd(0x12, 0, 0, 0, NULL) == LT_FB_SHAPE);
    *(DWORD*)(p + 0x50) = 2;
    *(u8**)(wm + 0xB0) = NULL;
    assert(fd(0x12, 0, 0, 0, NULL) == LT_FB_NOWM);
    *(u8**)(wm + 0xB0) = act;
    wmSlot = NULL;
    assert(fd(0x12, 0, 0, 0, NULL) == LT_FB_NOWM);
    wmSlot = wm;

    // QL rules: never nested (QL exclusive or shared already held on this thread), never with a lost writer frame; T-L2 flag
    // set only inside the hold
    u64 r;
    const u8* pt;
    assert(LtClone(p, 0x12, 0, 0, &r, &pt) == LT_OK && r == id2 && t_qlShared == 0 && TryAcquireSRWLockExclusive(&g_ql));
    ReleaseSRWLockExclusive(&g_ql);
    t_qlDepth = 1;
    assert(LtClone(p, 0x12, 0, 0, &r, &pt) == LT_FB_NESTED && r == 0);
    t_qlDepth = 0;
    t_qlShared = 1;
    assert(LtClone(p, 0x12, 0, 0, &r, &pt) == LT_FB_NESTED);
    t_qlShared = 0;
    g_steerBroken = 1;
    assert(LtClone(p, 0x12, 0, 0, &r, &pt) == LT_FB_BROKEN && TryAcquireSRWLockExclusive(&g_ql));
    ReleaseSRWLockExclusive(&g_ql);
    g_steerBroken = 0;
    // T-L2: a Lock_Acquire while this thread holds QL shared is counted
    const LockAcquireFn savedLock = g_lockOrig;
    g_lockOrig = [](void*, void*, const DWORD*) -> LONG { return 1; };
    static const DWORD inf = 0xFFFFFFFF;
    static u8 otherLock[0x28];
    t_qlShared = 1;
    LockAcquireHook(otherLock, NULL, &inf);
    t_qlShared = 0;
    LockAcquireHook(otherLock, NULL, &inf);
    assert(k.ltQlHeldBlock == 1);
    g_lockOrig = savedLock;

    // the hook: switches off, not the owner, no job, a job (read-only answer), each fall-back
    const FdFn savedFd = g_fdOrig;
    g_fdOrig = FkFd;
    g_fdCalls = 0;
    const u8* out = NULL;
    g_async.owner = me;
    assert(FdHook(p, NULL, 0x12, 0, 0, &out) == id2 && g_fdCalls == 1 && k.ltCalls == 0);  // switches off
    k.ltMode = 1;
    g_async.owner = me + 1;
    assert(FdHook(p, NULL, 0x12, 0, 0, &out) == id2 && g_fdCalls == 2 && k.ltCalls == 0);
    g_async.owner = me;
    g_async.state = A_IDLE;
    assert(FdHook(p, NULL, 0x12, 0, 0, &out) == id2 && g_fdCalls == 3 && k.ltCalls == 1 && k.ltShadowRuns == 0);
    g_async.state = A_RUNNING;
    out = NULL;
    assert(FdHook(p, NULL, 0x12, 0, 0, &out) == id2 && g_fdCalls == 3 && k.ltTaken == 1 && out == portals[2]);
    assert(FdHook(p, NULL, 0x12, 0, 3, NULL) == 0 && g_fdCalls == 3 && k.ltTaken == 2);
    g_inInvoke = 1;
    assert(FdHook(p, NULL, 0x12, 0, 0, NULL) == id2 && g_fdCalls == 4 && k.ltFb[LT_FB_STATE - 1] == 1);
    g_inInvoke = 0;
    k.rgOwnerTid = (LONG)me;
    assert(FdHook(p, NULL, 0x12, 0, 0, NULL) == id2 && g_fdCalls == 5 && k.ltFb[LT_FB_STATE - 1] == 2);
    k.rgOwnerTid = 0;
    t_rg.n = 1;  // a gate frame open on this thread
    assert(FdHook(p, NULL, 0x12, 0, 0, NULL) == id2 && g_fdCalls == 6 && k.ltFb[LT_FB_STATE - 1] == 3);
    t_rg.n = 0;
    t_qlDepth = 1;
    assert(FdHook(p, NULL, 0x12, 0, 0, NULL) == id2 && g_fdCalls == 7 && k.ltFb[LT_FB_NESTED - 1] == 1);
    t_qlDepth = 0;
    *(LONG*)(portals[2] + 0x70) = 0;
    FdHook(p, NULL, 0x12, 0, 0, NULL);  // (the fake game call refuses it too)
    assert(g_fdCalls == 8 && k.ltFb[LT_FB_SHAPE - 1] == 1);
    *(LONG*)(portals[2] + 0x70) = 1;
    // QL held exclusive by a worker writer: try fails, the game's call
    HANDLE held = CreateEventW(NULL, TRUE, FALSE, NULL), go = CreateEventW(NULL, TRUE, FALSE, NULL);
    struct Hold { HANDLE held, go; } h = {held, go};
    HANDLE th = CreateThread(NULL, 0, [](void* a) -> DWORD {
        Hold* hh = (Hold*)a;
        AcquireSRWLockExclusive(&g_ql);
        SetEvent(hh->held);
        WaitForSingleObject(hh->go, 5000);
        ReleaseSRWLockExclusive(&g_ql);
        return 0;
    }, &h, 0, NULL);
    WaitForSingleObject(held, 5000);
    assert(LtClone(p, 0x12, 0, 0, &r, &pt) == LT_FB_TRY);
    assert(FdHook(p, NULL, 0x12, 0, 0, NULL) == id2 && g_fdCalls == 9 && k.ltFb[LT_FB_TRY - 1] == 1);
    SetEvent(go);
    WaitForSingleObject(th, 5000);
    CloseHandle(th);
    CloseHandle(held);
    CloseHandle(go);
    // shadow (no job): the game's call, then the clone; a different answer is counted, *out compared with what the game left
    k.ltMode = 0;
    k.ltShadow = 1;
    assert(FdHook(p, NULL, 0x12, 0, 0, NULL) == id2 && g_fdCalls == 10 && k.ltFb[LT_FB_TRY - 1] == 1);  // job: game only
    g_async.state = A_IDLE;
    out = (const u8*)(uintptr_t)0x1234;
    assert(FdHook(p, NULL, 0x14, 0, 0, &out) == 0 && out == (const u8*)(uintptr_t)0x1234 && g_fdCalls == 11);
    assert(FdHook(p, NULL, 0x12, 0, 0, &out) == id2 && out == portals[2] && g_fdCalls == 12);
    assert(k.ltShadowRuns == 2 && k.ltShadowMismatch == 0 && g_rgOffSimOpen == 0);
    g_fdSkew = 1;
    FdHook(p, NULL, 0x12, 0, 0, NULL);
    assert(k.ltShadowRuns == 3 && k.ltShadowMismatch == 1);
    g_fdSkew = 0;
    k.ltShadow = 0;
    g_fdOrig = savedFd;

    // P1b: second frames at the fixed offsets of the four gate-12 keys, the ebp frame of the gate-0 key
    DWORD st[48] = {};
    st[37] = 0x0080CF36;  // slot[7]
    P1bNote(12, 0x0062F782, &st[30], 100);
    P1bNote(12, 0x0062F782, &st[30], 50);
    assert(k.p1bKey[0] == (LONG)(1u << 24 | 0x80CF36) && k.p1bN[0] == 2 && k.p1bUs[0] == 150);
    st[34] = 0x0076F15D;  // slot[4]
    P1bNote(12, 0x0062FB74, &st[30], 1);
    st[34] = 0x00788373;
    P1bNote(12, 0x00631814, &st[30], 1);
    st[39] = 0x00634F15;  // slot[9]
    P1bNote(12, 0x00631F40, &st[30], 1);
    assert(k.p1bKey[1] == (LONG)(2u << 24 | 0x76F15D) && k.p1bKey[2] == (LONG)(3u << 24 | 0x788373) &&
           k.p1bKey[3] == (LONG)(4u << 24 | 0x634F15));
    st[24] = (DWORD)(uintptr_t)&st[40];  // slot[-6] = ebp of 0x62EF30; [ebp + 4] = its return address
    st[41] = 0x0062F3D0;
    P1bNote(0, 0x0062EF7A, &st[30], 1);
    assert(k.p1bKey[4] == (LONG)(5u << 24 | 0x62F3D0) && k.p1bN[4] == 1);
    st[24] = (DWORD)(uintptr_t)&st[10];  // ebp below the slot: no frame
    P1bNote(0, 0x0062EF7A, &st[30], 1);
    assert(k.p1bNoFrame == 1);
    P1bNote(0, 0x0062F782, &st[30], 1);  // a gate-12 key seen at another gate, an unknown key: nothing
    P1bNote(12, 0x00123456, &st[30], 1);
    st[37] = 0x10001000;  // a return address outside the exe
    P1bNote(12, 0x0062F782, &st[30], 1);
    assert(k.p1bKey[5] == (LONG)(1u << 24 | 0xFFFFFF) && !k.p1bKey[6] && k.p1bNoFrame == 1);
    for (DWORD i = 0; i < 32; i++) {  // the table fills, then p1bOther
        st[37] = 0x00500000 + i;
        P1bNote(12, 0x0062F782, &st[30], 1);
    }
    assert(k.p1bOther == 6 && k.p1bKey[31] == (LONG)(1u << 24 | 0x500019));
    // through the gate stub's entry: an enforced gate-12 wait during a job is keyed by its second frame too
    memset(k.p1bKey, 0, sizeof(k.p1bKey) * 3 + 8);
    {
        const HANDLE savedDone = g_asyncDone, savedInv = g_invReady;
        g_lockOrig = RgTryAcquire;
        g_rgRelOrig = RgFakeRelease;
        InitializeCriticalSection(&g_rgFakeCs);
        g_asyncDone = CreateEventW(NULL, FALSE, FALSE, NULL);
        g_invReady = CreateEventW(NULL, FALSE, FALSE, NULL);
        g_enfHeld = CreateEventW(NULL, TRUE, FALSE, NULL);
        k.rgReady = 7;
        k.rgEnforce = 1;
        g_async.lock = g_rgLockObj;
        g_rgLock = g_rgLockObj;
        g_p1bOn = 1;
        st[30] = 0x0062FB74;
        st[34] = 0x0080D798;
        HANDLE w = EnfStartWorker();
        RgEnter(12, &st[30]);
        assert(k.p1bKey[0] == (LONG)(2u << 24 | 0x80D798) && k.p1bN[0] == 1 && k.p1bUs[0] >= 10000 && t_rg.n == 1);
        assert(RgLeave() == 0x0062FB74 && *(LONG*)(g_rgLockObj + 0x20) == 0 && g_rgOffSimOpen == 0);
        EnfJoin(w);
        g_p1bOn = 0;
        g_async.state = A_IDLE;
        g_async.lock = NULL;
        g_rgLock = NULL;
        k.rgEnforce = 0;
        k.rgReady = 0;
        k.rgOwnerTid = 0;
        g_lockOrig = savedLock;
        CloseHandle(g_asyncDone);
        CloseHandle(g_invReady);
        CloseHandle(g_enfHeld);
        g_asyncDone = savedDone;
        g_invReady = savedInv;
        DeleteCriticalSection(&g_rgFakeCs);
    }

    // T-L3: the counting thunks in the vtable slots, by thread, during a job only
    static u8 portal[0x80];
    static DWORD pvt[2];
    pvt[0] = vt[0];
    pvt[1] = vt[1];
    assert(InstallPortalRefThunks(pvt) && pvt[0] == (DWORD)(uintptr_t)PortalAddRefThunk);
    assert(!InstallPortalRefThunks(pvt));  // swapped already: refused
    *(DWORD**)portal = pvt;
    *(LONG*)(portal + 0x70) = 1;
    typedef LONG(__fastcall * RefFn)(u8*, void*);
    g_async.state = A_RUNNING;
    assert(((RefFn)(uintptr_t)pvt[0])(portal, NULL) == 2 && ((RefFn)(uintptr_t)pvt[1])(portal, NULL) == 1);
    assert(k.portalRef[0] == 1 && k.portalRef[4] == 1);
    simTid = me + 3;
    g_workerTid = me;
    t_qlDepth = 1;
    ((RefFn)(uintptr_t)pvt[0])(portal, NULL);
    t_qlDepth = 0;
    ((RefFn)(uintptr_t)pvt[1])(portal, NULL);
    g_workerTid = me + 7;
    ((RefFn)(uintptr_t)pvt[0])(portal, NULL);
    g_async.state = A_IDLE;
    ((RefFn)(uintptr_t)pvt[0])(portal, NULL);
    assert(k.portalRef[1] == 1 && k.portalRef[6] == 1 && k.portalRef[3] == 1 && *(LONG*)(portal + 0x70) == 3);
    assert(k.portalRef[0] + k.portalRef[1] + k.portalRef[2] + k.portalRef[3] == 3);

    g_ltAddRef = savedAdd;
    g_ltRelease = savedRel;
    g_srWmSlot = savedWm;
    g_workerTid = savedWorker;
    g_async.owner = 0;
    k.ltMode = 0;
    simTid = 0;
}

// --- P10 (docs/87 §6): route IC tags, plan time, early returns, gate-10 waits by IC; G10-C frame (docs/87 §5) ---
static DWORD g_p10PlanTid;
static u8 __fastcall P10FakePlan(void*, void*, int*, int*, int*, DWORD, DWORD, u8*, int*, int*, DWORD, DWORD) {
    g_p10PlanTid = GetCurrentThreadId();
    Sleep(2);
    return 1;
}
// --- design SH (slotro.cpp, docs/90): interlocked portal counts, gate pass / drop, hook dispatch, shadow, fallbacks ---
static int g_slDel;
static void __fastcall SlFkDelete(u8* p, void*, DWORD flag) { assert(flag == 1 && *(LONG*)(p + 0x70) <= 1); g_slDel++; }
static int g_slOrigCalls, g_slOrigHold, g_slTripLock, g_slSkew, g_slThrow;
static DWORD __cdecl SlFkOrig(float* pt, const u8*, DWORD ags, DWORD) {
    g_slOrigCalls++;
    g_slOrigHold = t_slHold * 10 + t_qlShared;
    if (g_slTripLock) QlSharedTrip(SLD_LOCK);  // a Lock_Acquire inside the call
    if (g_slThrow) RaiseException(0xE0005151, 0, 0, NULL);
    pt[0] += 1.0f;
    pt[1] = (float)ags;
    return t_slDry && g_slSkew ? 0 : 1;
}
static void TestSlotRo() {
    static_assert(offsetof(Ts3nCounters, arReady) == 0x14A4 && offsetof(Ts3nCounters, orReady) == 0x1524 &&
                  offsetof(Ts3nCounters, slReady) == 0x1564 && offsetof(Ts3nCounters, slMode) == 0x1568 &&
                  offsetof(Ts3nCounters, slShadow) == 0x156C && offsetof(Ts3nCounters, slPass12) == 0x1580 &&
                  offsetof(Ts3nCounters, slDrop) == 0x1588 && offsetof(Ts3nCounters, slFb) == 0x15A0 &&
                  offsetof(Ts3nCounters, slShadowRuns) == 0x15B0 && offsetof(Ts3nCounters, slRelRetry) == 0x15C0 &&
                  sizeof(Ts3nCounters) >= 1393 * 4, "v42 layout");
    Ts3nCounters& k = ts3native_counters;
    memset(&k.slReady, 0, sizeof(Ts3nCounters) - offsetof(Ts3nCounters, slReady));
    // interlocked count pair: same results as 0x64E8E0 / 0x64E8F0, delete through slot +8 at a count <= 1, no lost update
    static DWORD vt[3];
    static u8 pt[0x80];
    vt[2] = (DWORD)(uintptr_t)SlFkDelete;
    *(DWORD**)pt = vt;
    *(LONG*)(pt + 0x70) = 1;
    assert(PortalAddRefX(pt, NULL) == 2 && PortalReleaseX(pt, NULL) == 1 && g_slDel == 0);
    assert(PortalReleaseX(pt, NULL) == 0 && g_slDel == 1 && *(LONG*)(pt + 0x70) == 1);  // deleted, count left as the original does
    HANDLE th[2];
    for (int i = 0; i < 2; i++)
        th[i] = CreateThread(NULL, 0, [](void*) -> DWORD {
            for (int n = 0; n < 200000; n++) { PortalAddRefX(pt, NULL); PortalReleaseX(pt, NULL); }
            return 0;
        }, NULL, 0, NULL);
    for (int n = 0; n < 200000; n++) { PortalAddRefX(pt, NULL); PortalReleaseX(pt, NULL); }
    WaitForMultipleObjects(2, th, TRUE, INFINITE);
    CloseHandle(th[0]);
    CloseHandle(th[1]);
    assert(*(LONG*)(pt + 0x70) == 1 && g_slDel == 1);  // 600,000 pairs on three threads: no update lost, never deleted

    // a lot table with lot 0x12 holding two portals of object 0x5000:0x7, one of another object
    const DWORD me = GetCurrentThreadId();
    static u8 p[0x200], wm[0x200], act[0x60], node[0x1A0], prt[3][0x80];
    static DWORD bk[2], vec[3];
    static u8* wmSlot;
    wmSlot = wm;
    u8* const* savedWm = g_srWmSlot;
    g_srWmSlot = &wmSlot;
    *(u8**)(wm + 0xB0) = act;
    *(DWORD*)(act + 0x48) = 0x99;
    *(DWORD*)node = 0x12;
    *(DWORD*)(node + 0x10) = 0x12;
    bk[0] = (DWORD)(uintptr_t)node;
    bk[1] = 0x5E117E1;
    *(DWORD**)(p + 0x4C) = bk;
    *(DWORD*)(p + 0x50) = 1;
    for (int i = 0; i < 3; i++) {
        *(DWORD*)prt[i] = LT_PORTAL_VT;
        *(DWORD*)(prt[i] + 8) = i < 2 ? 0x5000 : 0x6000;
        *(DWORD*)(prt[i] + 0xC) = 7;
        *(LONG*)(prt[i] + 0x70) = 1;
        vec[i] = (DWORD)(uintptr_t)prt[i];
    }
    *(DWORD**)(node + 0x10 + 0x44) = vec;
    *(DWORD**)(node + 0x10 + 0x48) = vec + 3;
    // gate stacks: f[2] = ecx of the stub's pushad frame (slot[-2]), slot = f + 4
    DWORD f[16] = {};
    DWORD* slot = f + 4;
    auto hold = [&] { assert(SlBegin(false) == SL_OK && t_slHold == 1 && t_qlShared == 1); };
    auto end = [&] { return SlEnd(); };
    auto g12 = [&](DWORD lo, DWORD ret, DWORD idLo) {
        slot[-2] = (DWORD)(uintptr_t)p, slot[0] = ret, slot[1] = lo, slot[2] = 0, slot[9] = idLo, slot[10] = 7;
        return SlGate(12, slot);
    };
    DWORD key[2];
    auto g0 = [&](DWORD lo) {
        key[0] = lo, key[1] = 0;
        slot[-2] = (DWORD)(uintptr_t)(p + 0x48), slot[0] = 0x0062F7A2, slot[1] = (DWORD)(uintptr_t)key;
        return SlGate(0, slot);
    };
    hold();
    assert(g12(0x12, SL_PFO_RET, 0x5000) && g12(0x12, 0x0062FB74, 0x5000) && g12(0x44, SL_PFO_RET, 0x5000));  // hit, other caller, miss
    assert(g0(0x12) && t_slHold == 1 && k.slPass12 == 3 && k.slPass0 == 1);
    *(LONG*)(prt[1] + 0x70) = 0;  // its Release would delete
    assert(!g12(0x12, SL_PFO_RET, 0x5000) && t_slHold == 2 && t_qlShared == 0 && k.slDrop[SLD_PORTAL] == 1);
    assert(!end() && t_slHold == 0);
    hold();
    assert(g12(0x12, SL_PFO_RET, 0x6000));  // another object's portals only: the bad one is not its
    *(LONG*)(prt[1] + 0x70) = 1;
    *(DWORD*)prt[0] = 0x00FED999;  // another class
    assert(!g12(0x12, SL_PFO_RET, 0x5000) && k.slDrop[SLD_PORTAL] == 2);
    end();
    *(DWORD*)prt[0] = LT_PORTAL_VT;
    hold();
    assert(!g0(0x44) && k.slDrop[SLD_MISS] == 1 && t_slHold == 2);  // GetOrInsert miss would insert under shared QL
    end();
    hold();
    assert(!g0(0x99) && k.slDrop[SLD_MISS] == 2);  // the active lot: Contains rules say no entry, so no pass
    end();
    hold();
    assert(!SlGate(3, slot) && k.slDrop[SLD_GATE] == 1);  // any other gate site
    end();
    hold();
    *(DWORD*)(p + 0x50) = 0;
    assert(!g0(0x12) && k.slDrop[SLD_TABLE] == 1);  // the original would divide by 0
    *(DWORD*)(p + 0x50) = 1;
    end();
    hold();
    const LONG tl2 = k.ltQlHeldBlock;
    QlSharedTrip(SLD_MANAGED);
    assert(t_slHold == 2 && k.slDrop[SLD_MANAGED] == 1 && k.ltQlHeldBlock == tl2);  // a managed call drops, never counts in T-L2
    end();
    // SlBegin fallbacks
    t_qlDepth = 1;
    assert(SlBegin(false) == SL_FB_NESTED);
    t_qlDepth = 0;
    g_steerBroken = 1;
    assert(SlBegin(false) == SL_FB_BROKEN);
    g_steerBroken = 0;

    // the hook: off, no flags, not the owner, no job, job (hold), drop inside, state fallbacks, QL busy, shadow, unwind
    g_slOrig = SlFkOrig;
    g_async.owner = me;
    g_async.state = A_RUNNING;
    __declspec(align(16)) float pt4[4] = {1, 2, 3, 0};
    const u8* obj = p;
    assert(SlHook(pt4, obj, 4, 0) == 1 && g_slOrigHold == 0 && k.slCalls == 0);  // both switches off
    k.slMode = 1;
    assert(SlHook(pt4, obj, 0, 0) == 1 && SlHook(pt4, NULL, 4, 0) == 1 && k.slCalls == 0);  // returns before any routing read
    g_async.owner = me + 1;
    assert(SlHook(pt4, obj, 4, 0) == 1 && g_slOrigHold == 0 && k.slCalls == 0);
    g_async.owner = me;
    assert(SlHook(pt4, obj, 4, 0) == 1 && g_slOrigHold == 11 && k.slCalls == 1 && k.slTaken == 1 && t_slHold == 0 && t_qlShared == 0);
    assert(TryAcquireSRWLockExclusive(&g_ql));  // released after the call
    ReleaseSRWLockExclusive(&g_ql);
    g_slTripLock = 1;
    assert(SlHook(pt4, obj, 4, 0) == 1 && k.slTaken == 1 && k.slDrop[SLD_LOCK] == 1 && t_slHold == 0);
    g_slTripLock = 0;
    g_inInvoke = 1;
    assert(SlHook(pt4, obj, 4, 0) == 1 && g_slOrigHold == 0 && k.slFb[SL_FB_STATE - 1] == 1);
    g_inInvoke = 0;
    k.rgOwnerTid = (LONG)me;
    assert(SlHook(pt4, obj, 4, 0) == 1 && g_slOrigHold == 0 && k.slFb[SL_FB_STATE - 1] == 2);
    k.rgOwnerTid = 0;
    t_rg.n = 1;
    assert(SlHook(pt4, obj, 4, 0) == 1 && g_slOrigHold == 0 && k.slFb[SL_FB_STATE - 1] == 3);
    t_rg.n = 0;
    {
        HANDLE ev = CreateEventW(NULL, FALSE, FALSE, NULL);
        HANDLE qh = CreateThread(NULL, 0, [](void* e) -> DWORD {
            AcquireSRWLockExclusive(&g_ql);
            SetEvent((HANDLE)e);
            Sleep(200);
            ReleaseSRWLockExclusive(&g_ql);
            return 0;
        }, ev, 0, NULL);
        WaitForSingleObject(ev, INFINITE);
        assert(SlHook(pt4, obj, 4, 0) == 1 && g_slOrigHold == 0 && k.slFb[SL_FB_TRY - 1] == 1);  // a worker writer holds QL
        WaitForSingleObject(qh, INFINITE);
        CloseHandle(qh);
        CloseHandle(ev);
    }
    // an exception through the held call still releases QL
    g_slThrow = 1;
    __try {
        SlHook(pt4, obj, 4, 0);
        assert(!"no exception");
    } __except (GetExceptionCode() == 0xE0005151 ? EXCEPTION_EXECUTE_HANDLER : EXCEPTION_CONTINUE_SEARCH) {
    }
    g_slThrow = 0;
    assert(t_slHold == 0 && t_qlShared == 0 && TryAcquireSRWLockExclusive(&g_ql));
    ReleaseSRWLockExclusive(&g_ql);
    // shadow (no job): the game's call on the point, then the dry hold on a copy; equal, then a skewed answer counted
    g_async.state = A_IDLE;
    assert(SlHook(pt4, obj, 4, 0) == 1 && g_slOrigHold == 0 && k.slShadowRuns == 0);  // shadow off: the game's call only
    k.slShadow = 1;
    const int calls0 = g_slOrigCalls;
    pt4[0] = 1;
    assert(SlHook(pt4, obj, 4, 0) == 1 && g_slOrigCalls == calls0 + 2 && g_slOrigHold == 11 && pt4[0] == 2.0f);
    assert(k.slShadowRuns == 1 && k.slShadowMismatch == 0 && g_rgOffSimOpen == 0 && t_slHold == 0);
    g_slSkew = 1;
    assert(SlHook(pt4, obj, 4, 0) == 1 && k.slShadowMismatch == 1);  // the game's answer is returned
    g_slSkew = 0;
    g_slTripLock = 1;
    assert(SlHook(pt4, obj, 4, 0) == 1 && k.slShadowDrop == 1 && k.slShadowMismatch == 1);
    g_slTripLock = 0;
    k.slMode = k.slShadow = 0;
    g_async.owner = 0;
    g_srWmSlot = savedWm;
    memset(&k.slReady, 0, sizeof(Ts3nCounters) - offsetof(Ts3nCounters, slReady));
    puts("slot position RO (SH): interlocked counts, gate pass/drop, hook, fallbacks, shadow, unwind ok");
}

static void TestP10() {
    static_assert(offsetof(Ts3nCounters, p10Ready) == 0x1424 && offsetof(Ts3nCounters, p10Entries) == 0x1428 &&
                  offsetof(Ts3nCounters, p10PlanUs) == 0x1438 && offsetof(Ts3nCounters, p10EarlyRet) == 0x1448 &&
                  offsetof(Ts3nCounters, p10CanYield) == 0x1458 && sizeof(Ts3nCounters) >= 1305 * 4, "v38 layout");
    Ts3nCounters& k = ts3native_counters;
    memset(&k.p10Ready, 0, sizeof(LONG) * 16);
    memset(k.p1bKey, 0, sizeof(k.p1bKey) * 3 + 8);
    const DWORD me = GetCurrentThreadId();
    static DWORD simTid;
    simTid = me;
    g_rtSimTidSlot = &simTid;
    const DWORD savedWorker = g_workerTid;
    const LockAcquireFn savedLock = g_lockOrig;
    const PlanLockedFn savedPlan = g_planOrig;
    const HANDLE savedDone = g_asyncDone, savedInv = g_invReady;
    g_lockOrig = RgTryAcquire;
    g_rgRelOrig = RgFakeRelease;
    InitializeCriticalSection(&g_rgFakeCs);
    g_asyncDone = CreateEventW(NULL, FALSE, FALSE, NULL);
    g_invReady = CreateEventW(NULL, FALSE, FALSE, NULL);
    g_enfHeld = CreateEventW(NULL, TRUE, FALSE, NULL);
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
    g_workerTid = me + 7;
    g_async.owner = me;
    g_rgLock = g_rgLockObj;
    g_planOrig = P10FakePlan;
    g_hostPtr = &g_fakeHostPtr;
    g_curState = &g_fakeCur;
    FakeTask(kRoot, 7);
    DWORD st[48] = {};
    const DWORD thunk = (DWORD)(uintptr_t)RgLeaveThunk;
    int list[2] = {}, pts[2] = {1, 2};  // starts != ends: the hook plans (no early-out)
    u8 opts[0x38] = {};

    // off: nothing tagged
    st[40] = 0x111;
    P10Enter(1, &st[40]);
    assert(t_p10 == 0 && t_rg.n == 0 && st[40] == 0x111 && k.p10Entries[1] == 0);
    g_p10On = 1;
    // another thread than the sim: not tagged
    simTid = me + 1;
    P10Enter(1, &st[40]);
    assert(t_p10 == 0 && t_rg.n == 0);
    simTid = me;

    // 1. tags nest and clear; a reader frame, not a gate frame; CanYield counted (not for Plan)
    P10Enter(1, &st[40]);
    assert(t_p10 == 2 && st[40] == thunk && t_rg.n == 1 && t_rg.readers == 1 && k.p10Entries[1] == 1 && k.p10CanYield[0] == 1);
    st[30] = 0x222;
    FakeTask(kNested, 7);  // a nested interpreter state: no yield here
    P10Enter(2, &st[30]);
    FakeTask(kRoot, 7);
    assert(t_p10 == 3 && t_rg.n == 2 && k.p10Entries[2] == 1 && k.p10CanYield[1] == 0);
    // gate site 10 reached inside PlanFromPoint, no plan: an early return of that IC at its leave
    k.rgReady = 7;
    const LONG unl10 = k.rgUnlocked[10];
    st[20] = 0x00760BFF;
    RgEnter(10, &st[20]);
    assert(t_p10 == (3 | P10_REACHED) && k.rgUnlocked[10] == unl10 + 1 && t_rg.n == 3);
    assert(RgLeave() == 0x00760BFF && RgLeave() == 0x222 && t_p10 == 2 && k.p10EarlyRet[2] == 1);
    // a plan inside Replan: timed and charged to Replan; reached + planned is no early return
    st[20] = 0x00760BFF;
    RgEnter(10, &st[20]);
    RgLeave();
    k.rgReady = 0;  // PlanLockedHook would learn a lock from the NULL world otherwise
    assert(PlanLockedHook(NULL, NULL, list, pts, pts, 0x100, 1, opts, NULL, NULL, 1, 0) == 1 && g_p10PlanTid == me);
    assert(k.p10PlanUs[1] >= 1000 && k.p10PlanUs[0] == 0 && t_p10 == (2 | P10_REACHED | P10_PLANNED));
    assert(RgLeave() == 0x111 && t_p10 == 0 && t_rg.n == 0 && t_rg.readers == 0 && k.p10EarlyRet[1] == 0);
    // no tag: a plan is not charged
    const LONG planUs1 = k.p10PlanUs[1];
    PlanLockedHook(NULL, NULL, list, pts, pts, 0x100, 1, opts, NULL, NULL, 1, 0);
    assert(k.p10PlanUs[1] == planUs1 && k.p10PlanUs[0] + k.p10PlanUs[2] + k.p10PlanUs[3] == 0);

    // 2. a frame left without returning (managed exception): dropped at the next entry, its tag cleared
    k.rgReady = 7;
    st[40] = 0x333;
    P10Enter(3, &st[40]);
    st[30] = 0x00760BFF;
    RgEnter(10, &st[30]);
    assert(t_p10 == (4 | P10_REACHED) && t_rg.n == 2);
    st[44] = 0x444;
    P10Enter(1, &st[44]);  // above both: they are dead
    assert(t_p10 == 2 && t_rg.n == 1 && t_rg.f[0].ret == 0x444 && k.p10EarlyRet[3] == 1 && k.p10Entries[3] == 1);
    // a stale tag is not charged to a plan outside that IC: the plan's own slot drops it first
    k.rgReady = 0;
    const LONG planUs1b = k.p10PlanUs[1];
    t_rg.f[0].slot = (DWORD*)((u8*)st - 0x10000);  // pretend the Replan frame lay below this call (left by an exception)
    PlanLockedHook(NULL, NULL, list, pts, pts, 0x100, 1, opts, NULL, NULL, 1, 0);
    assert(t_p10 == 0 && t_rg.n == 0 && k.p10PlanUs[1] == planUs1b);
    k.rgReady = 7;

    // 3. IcPlanHook tags Plan through its own return slot (no job: plain sync plan)
    {
        struct Fake { static u8* __cdecl Ic(u8* out, DWORD) {
            assert(t_p10 == 1 && t_rg.n == 1);
            return out;
        } };
        const IcPlanFn savedIc = g_icPlanOrig;
        g_icPlanOrig = Fake::Ic;
        u8 out[0x48];
        assert(IcPlanHook(out, 0x77) == out && t_p10 == 0 && t_rg.n == 0 && k.p10Entries[0] == 1);
        g_icPlanOrig = savedIc;
    }

    // 4. a gate-10 wait during a job, keyed in the P1b table by the IC (row 6): Replan
    k.rgEnforce = 1;
    g_async.lock = g_rgLockObj;
    st[40] = 0x555;
    P10Enter(1, &st[40]);
    st[30] = 0x00760BFF;
    HANDLE w = EnfStartWorker();
    RgEnter(10, &st[30]);
    assert(k.p1bKey[0] == (LONG)(6u << 24 | 0x765490) && k.p1bN[0] == 1 && k.p1bUs[0] >= 10000);
    assert(RgLeave() == 0x00760BFF && RgLeave() == 0x555 && *(LONG*)(g_rgLockObj + 0x20) == 0 && g_rgOffSimOpen == 0);
    EnfJoin(w);
    // without P10 (P1b alone) the row keys by Route_PlanCommon's caller through the ebp frame
    g_p10On = 0;
    st[24] = (DWORD)(uintptr_t)&st[40];
    st[41] = 0x0076429D;
    P1bNote(10, 0x00760BFF, &st[30], 1);
    assert(k.p1bKey[1] == (LONG)(6u << 24 | 0x76429D));
    g_p10On = 1;

    // 5. G10-C: the prologue is checked; the frame on Route_PlanCommon keeps job starts off for the whole plan, the goal
    // step and other gate sites inside are nested (not counted), the plan stays on this thread, a dropped frame closes
    static const u8 badPro[6] = {0x55, 0x8B, 0xEC, 0x83, 0xEC, 0x10};
    static const u8 goodPro[6] = {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF0};
    const RgSite saved10 = kRgSites[10];
    assert(!RgUseG10Common(badPro) && !g_g10c && kRgSites[10].addr == 0x0075C2D0);
    assert(RgUseG10Common(goodPro) && g_g10c && kRgSites[10].addr == (DWORD)(uintptr_t)goodPro);
    g_async.state = A_IDLE;
    st[30] = 0x0076429D;
    RgEnter(10, &st[30]);
    assert(t_g10cOpen == 1 && g_rgOffSimOpen == 1 && t_rg.f[0].rel == (RG_REL_OFFSIM | RG_REL_G10C));
    const LONG unl1 = k.rgUnlocked[1];
    RgEnter(1, &st[26]);  // the goal step's lot planner inside the frame
    assert(k.rgUnlocked[1] == unl1 && t_rg.n == 2 && g_rgOffSimOpen == 1);
    RgLeave();
    k.workerMode = k.workerReady = 1;  // a sync worker plan would wait for a worker held by this frame: planned here
    g_p10PlanTid = 0;
    RunPlan(NULL, NULL, list, pts, pts, 0x100, 1, opts, NULL, NULL, 1, 0);
    assert(g_p10PlanTid == me);
    k.workerMode = k.workerReady = 0;
    assert(RgLeave() == 0x0076429D && t_g10cOpen == 0 && g_rgOffSimOpen == 0);
    // a job running at entry: one wait, +0x918 held to the frame's end
    w = EnfStartWorker();
    st[30] = 0x0076429D;  // a leave does not write the slot back (the thunk returns there itself)
    RgEnter(10, &st[30]);
    assert(*(LONG*)(g_rgLockObj + 0x20) == 1 && t_rg.f[0].rel == (RG_REL_LOCK | RG_REL_OFFSIM | RG_REL_G10C) && t_g10cOpen == 1);
    EnfJoin(w);
    RgEnter(2, &st[26]);
    assert(*(LONG*)(g_rgLockObj + 0x20) == 1 && t_rg.n == 2);  // no second acquire inside
    RgLeave();
    assert(RgLeave() == 0x0076429D && *(LONG*)(g_rgLockObj + 0x20) == 0 && t_g10cOpen == 0 && t_rg.n == 0);
    // dropped: the next outer entry closes it
    RgEnter(10, &st[30]);
    RgEnter(0, &st[34]);
    assert(t_g10cOpen == 0 && t_rg.n == 1 && g_rgOffSimOpen == 1);
    RgLeave();
    assert(g_rgOffSimOpen == 0);
    kRgSites[10] = saved10;
    g_g10c = 0;

    g_p10On = 0;
    k.rgEnforce = 0;
    k.rgReady = 0;
    k.rgOwnerTid = 0;
    g_async.state = A_IDLE;
    g_async.lock = NULL;
    g_async.owner = 0;
    g_rgLock = NULL;
    g_workerTid = savedWorker;
    g_lockOrig = savedLock;
    g_planOrig = savedPlan;
    CloseHandle(g_asyncDone);
    CloseHandle(g_invReady);
    CloseHandle(g_enfHeld);
    g_asyncDone = savedDone;
    g_invReady = savedInv;
    DeleteCriticalSection(&g_rgFakeCs);
    simTid = 0;
    memset(&k.p10Ready, 0, sizeof(LONG) * 16);
}

// --- [ObstructedRO] (obstructro.cpp, docs/86 §10): read-only IC_Route_IsPointObstructedBySim ---
static int g_orRefIn, g_orRefOut, g_orGameCalls, g_orQueryWhy;
static u64 g_orGameAnswer;
static u8* g_orFakeRoute;
static const u8* g_orFps[8];
static int g_orNFps;
static float g_orPtU, g_orPtV;
static u8* g_orCacheSkew;  // the +0xD4 fake writes this cache (a writer the clone must notice)
static void __fastcall FkOrRef(u8*, void*) { g_orRefIn++; }
static void __fastcall FkOrUnref(u8*, void*) { g_orRefOut++; }
static u8* __fastcall FkOrRoute(const void*, void*, DWORD handle) { return handle == 0x25000001 || handle == 0x25000002 ? g_orFakeRoute : NULL; }
static void __cdecl FkOrLevel(const u8*, const float* pt, float* io) {
    assert(((uintptr_t)pt & 15) == 0 && pt[3] == pt[0]);  // 0x76BF90's {x, y, z, x}, 16-aligned as the IC's frame
    io[0] = io[1] = 7.0f;
    *(LONG*)&io[2] = 0;
}
static int FkOrQuery(SrCtx* c, const u8*, DWORD lo, DWORD, const SrPt& pt, float r, const u8* rec) {
    assert(lo == 0x30 && r == 0.25f && pt.v[0] == 3.0f && pt.v[2] == 5.0f && *(const DWORD*)rec == 0xABCD && rec[0x24] == 1);
    c->n = g_orNFps;
    for (int i = 0; i < g_orNFps; i++) c->objs[i] = g_orFps[i];
    return g_orQueryWhy;
}
static int __fastcall FkOrType(const u8* o, void*) { return (signed char)o[0x35]; }
static unsigned __int64 __fastcall FkOrId(const u8* o, void*) { return (u64)*(const DWORD*)(o + 0x18) << 32 | *(const DWORD*)(o + 0x14); }
static u8 __fastcall FkOrC0(const u8* o, void*) { return o[0x48]; }
static u8 __fastcall FkOrPt(const u8* o, void*, DWORD level, float u, float v, float r) {
    assert(level == 0 && r == 0.25f);
    g_orPtU = u, g_orPtV = v;
    if (g_orCacheSkew) g_orCacheSkew[0] ^= 1;
    return o[0x49];
}
static u64 __cdecl FkOrGame(DWORD, OrV3) { g_orGameCalls++; return g_orGameAnswer; }
static void TestObstructedRO() {
    static_assert(offsetof(Ts3nCounters, arReady) == 0x14A4 && offsetof(Ts3nCounters, orReady) == 0x1524 &&
                      offsetof(Ts3nCounters, orMode) == 0x1528 && offsetof(Ts3nCounters, orShadow) == 0x152C &&
                      offsetof(Ts3nCounters, orFb) == 0x1538 && offsetof(Ts3nCounters, orUs) == 0x1550 &&
                      offsetof(Ts3nCounters, orShadowMismatch) == 0x155C && offsetof(Ts3nCounters, orHits) == 0x1560 &&
                      sizeof(Ts3nCounters) >= 1369 * 4,
                  "v41 layout");
    Ts3nCounters& k = ts3native_counters;
    memset(&k.orReady, 0, 16 * 4);
    const DWORD me = GetCurrentThreadId();
    static DWORD simTid;
    simTid = me;
    g_rtSimTidSlot = &simTid;
    const DWORD savedWorker = g_workerTid;
    g_workerTid = me + 7;
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
    // world, world manager, planner P with one lot (key 0x30) in a 1-bucket table; identity rows, so (u, v) = (x, z)
    static u8 world[0xA00], wm[0x200], wmw[0x200], p[0x200], lot[0x60], act[0x60], rm[0x10];
    static __declspec(align(16)) u8 node[0x200];
    static DWORD bk[2];
    static u8 *wmSlot, *rmSlot;
    wmSlot = wm;
    rmSlot = rm;
    world[0x974] = 1;
    *(u8**)(world + 0x95C) = p;
    *(u8**)(world + 0x964) = wmw;
    *(u8**)(wm + 0xB0) = act;
    *(DWORD*)(act + 0x48) = 0x99;
    *(DWORD*)(lot + 0x48) = 0x30;
    *(DWORD*)node = 0x30;
    *(DWORD*)(node + 0x10) = 0x30;
    float* rows = (float*)(node + 0x10 + 0xA0);
    memset(rows, 0, 64);
    rows[0] = rows[5] = rows[10] = rows[15] = 1.0f;
    rows[12] = 10.0f;  // translation in x: u = x + 10
    bk[0] = (DWORD)(uintptr_t)node;
    bk[1] = 0x5E117E1;
    *(DWORD**)(p + 0x4C) = bk;
    *(DWORD*)(p + 0x50) = 1;
    static float one = 1.0f, radius = 0.25f;
    static bool lotMissing;
    const SrWorldFn savedWorld = g_srWorldGet;
    const SrLotLookupFn savedLook = g_srLotLookup;
    const SrLotFindFn savedFind = g_srLotFind;
    u8* const* savedWm = g_srWmSlot;
    const float* savedOne = g_srOne;
    g_srWorldGet = []() -> u8* { return world; };
    g_srLotLookup = [](void*, void*, float, float) -> u8* { return lotMissing ? NULL : lot; };
    g_srLotFind = [](const void* tbl, void*, DWORD* o, const DWORD* key) {
        const DWORD* b = *(const DWORD* const*)((const u8*)tbl + 4);
        o[0] = key[0] == *(const DWORD*)(uintptr_t)b[0] ? b[0] : b[1];
    };
    g_srWmSlot = &wmSlot;
    g_srOne = &one;
    g_orRmSlot = &rmSlot;
    g_orRadius = &radius;
    g_orRoute = FkOrRoute;
    g_orLevel = FkOrLevel;
    g_orQuery = FkOrQuery;
    g_srCheckSlots = false;
    // the route: vtable pair +0x10 / +0x14, option word +0x3C, own Sim id +0x40, ignore list at +0x158 + 0x38
    static DWORD rvt[8], ign[4];
    rvt[4] = (DWORD)(uintptr_t)FkOrRef;
    rvt[5] = (DWORD)(uintptr_t)FkOrUnref;
    static u8 route[0x200];
    g_orFakeRoute = route;
    *(DWORD**)route = rvt;
    *(DWORD*)(route + 0x3C) = 0xABCD;
    *(DWORD*)(route + 0x40) = 0x500;
    ign[0] = 0x600;
    *(DWORD**)(route + 0x190) = ign;
    *(DWORD**)(route + 0x194) = ign + 2;
    // footprints (fake class): type +0x35, id +0x14/+0x18, slot +0xC0 = byte +0x48, slot +0xD4 = byte +0x49, cache +0x38
    static DWORD fvt[0x40];
    fvt[3] = (DWORD)(uintptr_t)FkOrType;
    fvt[0x84 / 4] = (DWORD)(uintptr_t)FkOrId;
    fvt[0xC0 / 4] = (DWORD)(uintptr_t)FkOrC0;
    fvt[0xD4 / 4] = (DWORD)(uintptr_t)FkOrPt;
    static u8 fps[6][0x60], caches[6][0x24];
    const DWORD ids[6] = {0x700, 0x500, 0x600, 0x701, 0x702, 0x703};  // own Sim and ignored at 1 and 2
    for (int i = 0; i < 6; i++) {
        *(DWORD**)fps[i] = fvt;
        *(DWORD*)(fps[i] + 0x14) = ids[i];
        fps[i][0x35] = 0x14;
        fps[i][0x48] = 1;
        *(u8**)(fps[i] + 0x38) = caches[i];
        caches[i][0x20] = 1;
        g_orFps[i] = fps[i];
    }
    fps[0][0x35] = 3;     // not a Sim
    fps[3][0x48] = 0;     // slot +0xC0 false
    fps[4][0x49] = 0;     // the point test misses
    fps[5][0x49] = 1;     // the answer
    g_orNFps = 6;
    const OrV3 at = {3.0f, 1.0f, 5.0f};
    u64 r;
    auto run = [&](bool job) {
        const int in0 = g_orRefIn, out0 = g_orRefOut;
        const int why = OrRun(0x25000001, at, job, &r);
        assert(g_orRefIn - in0 == g_orRefOut - out0 && t_qlShared == 0 && t_inRo == false && g_srInClone == 0);  // pair balanced
        return why;
    };
    assert(run(false) == SR_OK && r == 0x703 && g_orPtU == 13.0f && g_orPtV == 5.0f && g_orRefIn == 1);
    assert(run(true) == SR_FB_STATE && r == 0);  // a job answer needs the tripwire stubs armed
    TwArm(true);
    assert(run(true) == SR_OK && r == 0x703);
    fps[5][0x49] = 0;
    assert(run(true) == SR_OK && r == 0);  // no footprint covers the point
    fps[5][0x49] = 1;
    // shapes and doubts: no route, no world manager, no lot: 0 as the IC; dirty lot index, lot not in the table, deferred
    // flush (query), invalid or changed cache, unknown +0xD4 target, planner missing, tripwire, lost writer frame
    assert(OrRun(0x25000009, at, true, &r) == SR_OK && r == 0);
    wmSlot = NULL;
    assert(run(true) == SR_OK && r == 0);
    wmSlot = wm;
    lotMissing = true;
    assert(run(true) == SR_OK && r == 0);
    lotMissing = false;
    wmw[0x1CC] = 1;
    assert(run(true) == SR_FB_DIRTY);
    wmw[0x1CC] = 0;
    *(DWORD*)(lot + 0x48) = 0x31;
    assert(run(true) == SR_FB_LOTMISS && r == 0);  // GetOrInsert would insert
    *(DWORD*)(lot + 0x48) = 0x30;
    g_orQueryWhy = SR_FB_DEFERRED;
    assert(run(true) == SR_FB_DEFERRED && OrMap(SR_FB_DEFERRED) == OR_FB_LOT);
    g_orQueryWhy = SR_OK;
    caches[4][0x20] = 0;
    assert(run(true) == SR_FB_CACHE && r == 0);  // the original would rebuild level 0
    caches[4][0x20] = 1;
    g_orCacheSkew = caches[4];
    assert(run(true) == SR_FB_CACHE);
    g_orCacheSkew = NULL;
    caches[4][0] = 0;
    g_srCheckSlots = true;
    assert(run(true) == SR_FB_SHAPE);  // +0xD4 is not 0x80F030
    g_srCheckSlots = false;
    world[0x974] = 0;
    assert(run(true) == SR_FB_SHAPE);
    world[0x974] = 1;
    g_twActive = 1;
    assert(run(true) == SR_FB_TRIP);
    g_twActive = 0;
    g_steerBroken = 1;
    assert(run(true) == SR_FB_STATE);
    g_steerBroken = 0;
    t_qlDepth = 1;
    assert(run(true) == SR_FB_STATE);
    t_qlDepth = 0;
    assert(run(true) == SR_OK && r == 0x703);

    // the hook: switches off, not the owner, no job, a job (read-only answer), each state fall-back, QL busy, shadow
    const OrFn savedOrig = g_orOrig;
    g_orOrig = FkOrGame;
    g_orGameAnswer = 0x703;
    g_async.owner = me;
    g_async.handle = 0x25000002;
    g_async.state = A_RUNNING;
    assert(OrHook(0x25000001, at) == 0x703 && g_orGameCalls == 1 && k.orCalls == 0);  // switches off
    k.orMode = 1;
    g_async.owner = me + 1;
    assert(OrHook(0x25000001, at) == 0x703 && g_orGameCalls == 2 && k.orCalls == 0);
    g_async.owner = me;
    assert(OrHook(0x25000001, at) == 0x703 && g_orGameCalls == 2 && k.orTaken == 1 && k.orHits == 1);
    fps[5][0x49] = 0;
    assert(OrHook(0x25000001, at) == 0 && g_orGameCalls == 2 && k.orTaken == 2 && k.orHits == 1);
    fps[5][0x49] = 1;
    assert(OrHook(0x25000002, at) == 0x703 && g_orGameCalls == 3 && k.orFb[OR_FB_STATE - 1] == 1);  // the job plans this route
    g_inInvoke = 1;
    assert(OrHook(0x25000001, at) == 0x703 && g_orGameCalls == 4 && k.orFb[OR_FB_STATE - 1] == 2);
    g_inInvoke = 0;
    k.rgOwnerTid = (LONG)me;
    assert(OrHook(0x25000001, at) == 0x703 && g_orGameCalls == 5 && k.orFb[OR_FB_STATE - 1] == 3);
    k.rgOwnerTid = 0;
    t_rg.n = 1;
    assert(OrHook(0x25000001, at) == 0x703 && g_orGameCalls == 6 && k.orFb[OR_FB_STATE - 1] == 4);
    t_rg.n = 0;
    caches[4][0x20] = 0;
    assert(OrHook(0x25000001, at) == 0x703 && g_orGameCalls == 7 && k.orFb[OR_FB_CACHE - 1] == 1);
    caches[4][0x20] = 1;
    g_twActive = 1;
    assert(OrHook(0x25000001, at) == 0x703 && g_orGameCalls == 8 && k.orFb[OR_FB_TRIP - 1] == 1);
    g_twActive = 0;
    // QL held exclusive by a worker writer: the try fails, the game's call
    HANDLE held = CreateEventW(NULL, TRUE, FALSE, NULL), go = CreateEventW(NULL, TRUE, FALSE, NULL);
    struct Hold { HANDLE held, go; } h = {held, go};
    HANDLE th = CreateThread(NULL, 0, [](void* a) -> DWORD {
        Hold* hh = (Hold*)a;
        AcquireSRWLockExclusive(&g_ql);
        SetEvent(hh->held);
        WaitForSingleObject(hh->go, 5000);
        ReleaseSRWLockExclusive(&g_ql);
        return 0;
    }, &h, 0, NULL);
    WaitForSingleObject(held, 5000);
    assert(OrHook(0x25000001, at) == 0x703 && g_orGameCalls == 9 && k.orFb[OR_FB_TRY - 1] == 1);
    SetEvent(go);
    WaitForSingleObject(th, 5000);
    CloseHandle(th);
    CloseHandle(held);
    CloseHandle(go);
    assert(OrHook(0x25000001, at) == 0x703 && g_orGameCalls == 9 && k.orTaken == 3);
    // no job: the game's call (Mode 1 alone), then shadow: the game's call and the clone compared, job starts held meanwhile
    TwArm(false);
    g_async.state = A_IDLE;
    assert(OrHook(0x25000001, at) == 0x703 && g_orGameCalls == 10 && k.orShadowRuns == 0);
    k.orShadow = 1;
    assert(OrHook(0x25000001, at) == 0x703 && g_orGameCalls == 11 && k.orShadowRuns == 1 && k.orShadowMismatch == 0);
    g_orGameAnswer = 0x702;
    assert(OrHook(0x25000001, at) == 0x702 && g_orGameCalls == 12 && k.orShadowRuns == 2 && k.orShadowMismatch == 1);
    caches[4][0x20] = 0;  // a shadow clone that gives up is counted, not compared
    assert(OrHook(0x25000001, at) == 0x702 && k.orShadowRuns == 3 && k.orShadowMismatch == 1 && k.orFb[OR_FB_CACHE - 1] == 2);
    caches[4][0x20] = 1;
    assert(g_rgOffSimOpen == 0 && g_orRefIn == g_orRefOut);
    printf("obstructed RO: clone, %ld read-only answers, fallbacks %ld/%ld/%ld/%ld/%ld/%ld, shadow %ld runs\n", k.orTaken, k.orFb[0],
           k.orFb[1], k.orFb[2], k.orFb[3], k.orFb[4], k.orFb[5], k.orShadowRuns);

    g_orOrig = savedOrig;
    g_srWorldGet = savedWorld;
    g_srLotLookup = savedLook;
    g_srLotFind = savedFind;
    g_srWmSlot = savedWm;
    g_srOne = savedOne;
    g_srCheckSlots = true;
    g_orQuery = OrQueryGame;
    g_workerTid = savedWorker;
    g_async.owner = 0;
    g_async.handle = 0;
    memset(&k.orReady, 0, 16 * 4);
    simTid = 0;
}

// --- [AvoidRO] (avoidro.cpp, docs/137): read-only RouteFollow_A / _B, the A-site, T-AV, shadow; S2's child footprint class ---
namespace av {
static u8 world[0xA00], wm[0x200], wmw[0x200], p[0x200], lot[0x60], lotB[0x60];
static __declspec(align(16)) u8 node[0x200];
static DWORD bk[2];
static u8* wmSlot;
static bool noLotE, noQlot;
static int queryCalls, queryWhy, levelCalls, postureCalls, gameCalls, siteQueries, cacheSkewLevel = -1;
static const u8* fps[8];
static int nfps;
static u64 gameAnswer, simScanAnswer;
static bool bodySkipsSite;
static float ptU, ptV;
static DWORD optSeen;
static u8* cacheSkew;
static u8 bAnswer;
static u8* __fastcall Look(void* m, void*, float, float) {
    if (m == wm) return noLotE ? NULL : lot;
    return noQlot ? NULL : lot;
}
static LONG __cdecl Level(const float* g) { assert(((uintptr_t)g & 15) == 0); levelCalls++; return 0; }
static DWORD __fastcall Posture(const u8* a, void*) { postureCalls++; return *(const DWORD*)(a + 0x54) | *(const DWORD*)(a + 0x4C); }
static u8 __fastcall Opt38(const u8* o, void*, DWORD lo, DWORD) { return lo == *(const DWORD*)(o + 0x38); }
static u8 __fastcall Opt58(const u8* o, void*, DWORD lo, DWORD) { return lo == *(const DWORD*)(o + 0x58); }
static u8* __fastcall PathOpts(u8* path, void*) { return *(u8**)(path + 4); }
static int Query(SrCtx* c, const u8*, DWORD lo, DWORD, const SrPt& pt, const u8* rec) {
    queryCalls++;
    optSeen = *(const DWORD*)rec;
    assert(lo == 0x30 && rec[0x24] == 1 && pt.v[0] == 3.0f && pt.v[2] == 5.0f && pt.one == 1.0f && t_qlShared == 1);
    c->n = nfps;
    for (int i = 0; i < nfps; i++) c->objs[i] = fps[i];
    return queryWhy;
}
// footprints: outer slot 0 = levels (+0x50), slot +0xB8 = hit byte +0x60 + level; sub (obj + 4) +4 id (+0x14), +8 type (+0x35)
static DWORD __fastcall Levels(const u8* o, void*) { return o[0x50]; }
static u8 __fastcall Pt(const u8* o, void*, DWORD level, const float* v) {
    assert(((uintptr_t)v & 15) == 0 && v[1] == 0.0f && v[3] == v[0]);
    ptU = v[0], ptV = v[2];
    if (cacheSkew && (int)level == cacheSkewLevel) cacheSkew[level * 0x24] ^= 1;
    return o[0x60 + level];
}
static u64 __fastcall SubId(const u8* s, void*) { return *(const DWORD*)(s + 0x10); }
static int __fastcall SubType(const u8* s, void*) { return (signed char)s[0x31]; }
static u8 __fastcall FkQuery(u8*, void*, const void*, float, void* vec, DWORD mode, DWORD) {  // 0x82EDB0 for the site / shadow
    siteQueries++;
    assert(mode == 1);
    DWORD* v = (DWORD*)vec;
    DWORD* b = (DWORD*)malloc(4 * (nfps ? nfps : 1));
    for (int i = 0; i < nfps; i++) b[i] = (DWORD)(uintptr_t)(fps[i] + 4);
    v[0] = (DWORD)(uintptr_t)b, v[1] = (DWORD)(uintptr_t)(b + nfps);
    return 1;
}
static void __fastcall FkVecRel(void*, void*, void*, void*) {}
static void __cdecl FkFree(void* q) { free(q); }
// the game's RouteFollow_A: the footprint part through the site (AvSite), then "the Sim scan"
static u64 __cdecl GameA(u8*, u8*, const float*, const void*, DWORD) {
    gameCalls++;
    if (bodySkipsSite) return simScanAnswer;  // no routing world: the body never reaches 0x846D1C
    DWORD vec[4] = {};
    const u8 r = AvSite(world, NULL, NULL, 0.0f, vec, 1, 0);
    if (vec[0]) free((void*)(uintptr_t)vec[0]);
    return r && gameAnswer ? gameAnswer : simScanAnswer;
}
static u8 __cdecl GameB(u8*, u8*, u8*, const float*, DWORD) { gameCalls++; return bAnswer; }
static u64 __fastcall TwTramp0(u8*, void*, const void*) { assert(g_avTwActive == 1); return 0x1234; }
static u64 __cdecl TwTramp1(DWORD, DWORD, DWORD) { return 0; }
}  // namespace av

static void TestAvoidRO() {
    static_assert(offsetof(Ts3nCounters, avReady) == 0x31EC && offsetof(Ts3nCounters, avMode) == 0x31F0 &&
                      offsetof(Ts3nCounters, avShadow) == 0x31F4 && offsetof(Ts3nCounters, avSiteSkip) == 0x3204 &&
                      offsetof(Ts3nCounters, avFb) == 0x3208 && offsetof(Ts3nCounters, avUs) == 0x3230 &&
                      offsetof(Ts3nCounters, avTwSite) == 0x323C && offsetof(Ts3nCounters, avShadowRuns) == 0x3244 &&
                      offsetof(Ts3nCounters, avSimWriteInRo) == 0x325C && sizeof(Ts3nCounters) == 6112 * 4,
                  "counters v74 layout (docs/registry.md)");
    using namespace av;
    Ts3nCounters& k = ts3native_counters;
    memset(&k.avReady, 0, 29 * 4);
    const DWORD me = GetCurrentThreadId();
    static DWORD simTid;
    simTid = me;
    const DWORD* savedSim = g_rtSimTidSlot;
    g_rtSimTidSlot = &simTid;
    const DWORD savedWorker = g_workerTid;
    g_workerTid = me + 7;
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);

    // S2's child footprint class 0xFEBF90 (0.37): vtables as DWORD tables (compared, never called)
    {
        static DWORD pvt[0x40], psv[4], cvt[0x40], csv[4];
        pvt[0] = 0x00619EF0, pvt[2] = 0x00807090, pvt[3] = 0x00624450, pvt[0x28 / 4] = 0x00620D60, pvt[0x60 / 4] = 0x009C1B00;
        pvt[0x7C / 4] = 0x00806FE0, pvt[0x84 / 4] = 0x00620CC0, pvt[0xB8 / 4] = 0x0080E8A0, pvt[0xC0 / 4] = 0x007EAEA0;
        pvt[0xE8 / 4] = 0x0080E690;
        psv[1] = 0x00620E60, psv[2] = 0x00624ED0;
        cvt[0] = 0x00626210, cvt[2] = 0x00806BF0, cvt[3] = 0x00626220, cvt[0x28 / 4] = 0x00620D60, cvt[0x60 / 4] = 0x00806F00;
        cvt[0x84 / 4] = 0x006262D0, cvt[0xB8 / 4] = 0x00BFB320, cvt[0xC0 / 4] = 0x00620D60;
        csv[1] = 0x006262E0, csv[2] = 0x00626300;
        static u8 parent[0x40], child[0x28];
        *(DWORD**)parent = pvt, *(DWORD**)(parent + 4) = psv;
        *(DWORD**)child = cvt, *(DWORD**)(child + 4) = csv, *(u8**)(child + 0xC) = parent;
        assert(SrSlotsOk(parent) && SrSlotsOk(child) && AvSlotsOk(parent) && AvSlotsOk(child));
        *(u8**)(child + 0xC) = NULL;
        assert(!SrSlotsOk(child) && !AvSlotsOk(child));  // the game would fault forwarding to it
        *(u8**)(child + 0xC) = child;
        assert(!SrSlotsOk(child));  // a child's parent must be one of the parent classes
        *(u8**)(child + 0xC) = parent;
        csv[1] = 0x00620E60;
        assert(!SrSlotsOk(child));
        csv[1] = 0x006262E0;
        cvt[0xB8 / 4] = 0x0080E8A0;
        assert(!SrSlotsOk(child));  // the child's +0xB8 is 0xBFB320 (every footprint the clone sees passed SrSlotsOk)
        cvt[0xB8 / 4] = 0x00BFB320;
        pvt[0] = 0x00401000;
        assert(!AvSlotsOk(parent) && !AvSlotsOk(child));  // slot 0 (levels) unknown, also through the child
        pvt[0] = 0x0083F5F0;
        pvt[0xB8 / 4] = 0x00401000;
        assert(!AvSlotsOk(parent) && AvSlotsOk(child));
        pvt[0xB8 / 4] = 0x00BFB320;
        assert(AvSlotsOk(parent));
    }

    // world, managers, planner with one lot (key 0x30); rows: u = x + 10, v = z
    wmSlot = wm;
    world[0x974] = 1;
    *(u8**)(world + 0x95C) = p;
    *(u8**)(world + 0x964) = wmw;
    *(DWORD*)(lot + 0x48) = 0x30;
    *(DWORD*)(lotB + 0x48) = 0x31;  // not in the table
    *(DWORD*)node = 0x30;
    *(DWORD*)(node + 0x10) = 0x30;
    float* rows = (float*)(node + 0x10 + 0xA0);
    rows[0] = rows[5] = rows[10] = rows[15] = 1.0f;
    rows[12] = 10.0f;
    bk[0] = (DWORD)(uintptr_t)node;
    bk[1] = 0x5E117E1;
    *(DWORD**)(p + 0x4C) = bk;
    *(DWORD*)(p + 0x50) = 1;
    static float one = 1.0f;
    const SrWorldFn savedWorld = g_srWorldGet;
    const SrLotLookupFn savedLook = g_srLotLookup;
    const SrLotFindFn savedFind = g_srLotFind;
    const SrIdListFn savedIdList = g_srIdList;
    const SrQueryFn savedQuery = g_srQuery;
    const SrVecRelFn savedRel = g_srVecRel;
    const SrFreeFn savedFree = g_srFree;
    const SrConvFn savedConv = g_srConv;
    u8* const* savedWm = g_srWmSlot;
    const float* savedOne = g_srOne;
    g_srWorldGet = []() -> u8* { return world; };
    g_srLotLookup = Look;
    g_srLotFind = [](const void* tbl, void*, DWORD* o, const DWORD* key) {
        const DWORD* b = *(const DWORD* const*)((const u8*)tbl + 4);
        o[0] = key[0] == *(const DWORD*)(uintptr_t)b[0] ? b[0] : b[1];
    };
    g_srIdList = [](DWORD lo, DWORD, const void* l, DWORD n) -> u8 {
        for (DWORD i = 0; i < n; i++) if (((const DWORD*)l)[i * 2] == lo) return 1;
        return 0;
    };
    g_srQuery = FkQuery;
    g_srVecRel = FkVecRel;
    g_srFree = FkFree;
    g_srConv = [](const void* src, void* out) { memcpy(out, src, 16); };
    g_srWmSlot = &wmSlot;
    g_srOne = &one;
    g_avLevel = Level;
    g_avPosture = Posture;
    g_avOpt38 = Opt38;
    g_avOpt58 = Opt58;
    g_avQuery = Query;
    g_srCheckSlots = false;
    // footprints
    static void* fvt[0x40];
    static void* svt[4];
    fvt[0] = (void*)Levels;
    fvt[0xB8 / 4] = (void*)Pt;
    svt[1] = (void*)SubId;
    svt[2] = (void*)SubType;
    static u8 fp[6][0x80], cache[6][0x24 * 3];
    const DWORD ids[6] = {0x700, 0x701, 0x702, 0x703, 0x704, 0x705};
    for (int i = 0; i < 6; i++) {
        *(void***)fp[i] = fvt;
        *(void***)(fp[i] + 4) = svt;
        *(DWORD*)(fp[i] + 0x14) = ids[i];
        fp[i][0x35] = 3;
        fp[i][0x50] = 1;
        *(u8**)(fp[i] + 0x38) = cache[i];
        cache[i][0x20] = cache[i][0x24 + 0x20] = cache[i][0x48 + 0x20] = 1;
        fps[i] = fp[i];
    }
    nfps = 6;
    fp[0][0x35] = 0x14;  // a Sim footprint: skipped
    // 0x701 in the caller's ignore list, 0x702 in options +0x38, 0x703 in options +0x58; 0x704 one level, no hit; 0x705
    // three levels, the hit at level 2
    fp[5][0x50] = 3;
    fp[5][0x62] = 1;
    static u8 opts[0x80], path[8];
    static void* pvtbl[4];
    pvtbl[3] = (void*)PathOpts;
    *(void***)path = pvtbl;
    *(u8**)(path + 4) = opts;
    *(DWORD*)(opts + 0x38) = 0x702;
    *(DWORD*)(opts + 0x58) = 0x703;
    static DWORD ign[2] = {0x701, 0};
    static u8 agentBase[0x80];
    u8* agent = agentBase + 8;
    *(DWORD*)(agentBase + 0x54) = 0x40, *(DWORD*)(agentBase + 0x4C) = 0x2;
    static __declspec(align(16)) float goal[4] = {3.0f, 1.0f, 5.0f, 3.0f};
    static AvIn in;
    u64 id;
    bool none;
    // pth has no default here: a default argument may not name a local (static) variable in standard C++ (clang-cl)
    auto runAp = [&](bool job, u8* pth, u8* ag = NULL) {
        const int why = AvRunA(pth, ag ? ag : agentBase + 8, goal, ign, 1, job, &id, &in, &none);
        assert(t_qlShared == 0 && !t_inRo && g_srInClone == 0);
        return why;
    };
    auto runA = [&](bool job) { return runAp(job, path); };
    assert(runA(false) == SR_OK && id == 0x705 && !none && ptU == 13.0f && ptV == 5.0f && optSeen == 0x42 && postureCalls == 1);
    assert(runA(true) == SR_FB_STATE && t_avJobEnd);  // a job answer needs the tripwire stubs armed
    TwArm(true);
    assert(runA(true) == SR_OK && id == 0x705);
    fp[5][0x62] = 0;
    assert(runA(true) == SR_OK && id == 0);  // no footprint covers the goal: the Sim scan answers
    fp[5][0x62] = 1;
    // no options object: 0x702 / 0x703 are not filtered (0x702 misses, 0x703 hits); agent NULL or 8: option word 0x100
    fp[3][0x60] = 1;
    assert(runAp(true, NULL) == SR_OK && id == 0x703);
    {
        const int pc = postureCalls;
        assert(AvRunA(NULL, NULL, goal, ign, 1, true, &id, &in, &none) == SR_OK && id == 0x703 && optSeen == 0x100 && postureCalls == pc);
        assert(AvRunA(path, (u8*)(uintptr_t)8, goal, ign, 1, true, &id, &in, &none) == SR_OK && optSeen == 0x100 && postureCalls == pc);
    }
    fp[3][0x60] = 0;
    // no lot at the goal (consumer's lookup): the first survivor, no polygon test; no query lot: 0 and no lock, no query
    noLotE = true;
    assert(runA(true) == SR_OK && id == 0x704);
    noLotE = false;
    noQlot = true;
    {
        const int q = queryCalls;
        assert(runA(true) == SR_OK && id == 0 && queryCalls == q);
    }
    noQlot = false;
    // no routing world: none (the original takes no lock); dirty indexes; no planner; lot not in the table
    g_srWorldGet = []() -> u8* { return NULL; };
    assert(runA(true) == SR_OK && none);
    g_srWorldGet = []() -> u8* { return world; };
    wm[0x1CC] = 1;
    assert(runA(true) == SR_FB_DIRTY);
    wm[0x1CC] = 0;
    wmw[0x1CC] = 1;
    assert(runA(true) == SR_FB_DIRTY);
    wmw[0x1CC] = 0;
    world[0x974] = 0;
    assert(runA(true) == SR_FB_SHAPE);
    world[0x974] = 1;
    // the consumer's lot (key 0x31) is not in the table: GetOrInsert would insert
    g_srLotLookup = [](void* m, void*, float, float) -> u8* { return m == wm ? lotB : lot; };
    assert(runA(true) == SR_FB_LOTMISS);
    g_srLotLookup = Look;
    // polygon cache: invalid at level 0 of 0x704, at level 2 of 0x705; changed during the test
    cache[4][0x20] = 0;
    assert(runA(true) == SR_FB_CACHE);
    cache[4][0x20] = 1;
    cache[5][0x48 + 0x20] = 0;
    assert(runA(true) == SR_FB_CACHE);
    cache[5][0x48 + 0x20] = 1;
    cacheSkew = cache[5];
    cacheSkewLevel = 1;
    assert(runA(true) == SR_FB_CACHE);
    cacheSkew = NULL;
    cache[5][0x24] = 0;
    // query doubts pass through; unknown +0xB8 with the whitelist on; tripwires; lost writer frame; QL held on this thread
    queryWhy = SR_FB_DEFERRED;
    assert(runA(true) == SR_FB_DEFERRED);
    queryWhy = SR_OK;
    g_srCheckSlots = true;
    assert(runA(true) == SR_FB_SHAPE && t_srShape == SRS_SLOT);
    g_srCheckSlots = false;
    g_twActive = 1;
    assert(runA(true) == SR_FB_TRIP && !t_avPin);
    g_twActive = 0;
    g_avTwActive = 1;
    assert(runA(true) == SR_FB_TRIP);
    g_avTwActive = 0;
    g_steerBroken = 1;
    assert(runA(true) == SR_FB_STATE);
    g_steerBroken = 0;
    t_qlDepth = 1;
    assert(runA(true) == SR_FB_STATE);
    t_qlDepth = 0;
    assert(runA(true) == SR_OK && id == 0x705);

    // B: types 0xC / 0xD / 0x17 answer 1; the wm lot must be a table hit (GetOrInsert); no query lot: 0, no query
    {
        static __declspec(align(16)) float raw[4] = {3.0f, 0.0f, 5.0f, 0.0f};
        u8 res;
        assert(AvRunB(wm, world, p, raw, 0x77, true, &res, &in) == SR_OK && res == 0 && optSeen == 0x77);
        fp[2][0x35] = 0xD;
        assert(AvRunB(wm, world, p, raw, 0x77, true, &res, &in) == SR_OK && res == 1);
        fp[2][0x35] = 0x17;
        assert(AvRunB(wm, world, p, raw, 0x77, true, &res, &in) == SR_OK && res == 1);
        fp[2][0x35] = 0xC;
        assert(AvRunB(wm, world, p, raw, 0x77, true, &res, &in) == SR_OK && res == 1);
        g_srLotLookup = [](void* m, void*, float, float) -> u8* { return m == wm ? lotB : lot; };
        assert(AvRunB(wm, world, p, raw, 0x77, true, &res, &in) == SR_FB_LOTMISS);
        g_srLotLookup = Look;
        noLotE = true;
        assert(AvRunB(wm, world, NULL, raw, 0x77, true, &res, &in) == SR_OK && res == 1);  // no wm lot: no transform, no planner needed
        noLotE = false;
        assert(AvRunB(wm, world, NULL, raw, 0x77, true, &res, &in) == SR_FB_SHAPE);
        noQlot = true;
        const int q = queryCalls;
        assert(AvRunB(wm, world, p, raw, 0x77, true, &res, &in) == SR_OK && res == 0 && queryCalls == q);
        noQlot = false;
        assert(AvRunB(wm, NULL, p, raw, 0x77, true, &res, &in) == SR_FB_SHAPE);
        fp[2][0x35] = 3;
    }

    // the A-site: flag clear = the game's query; 1 = 0 once (counted), 2 = 0 once (shadow, not counted)
    {
        DWORD vec[4] = {};
        const int sq = siteQueries;
        assert(AvSite(world, NULL, NULL, 0.0f, vec, 1, 0) == 1 && siteQueries == sq + 1);
        free((void*)(uintptr_t)vec[0]);
        t_avMiss = 1;
        assert(AvSite(world, NULL, NULL, 0.0f, vec, 1, 0) == 0 && t_avMiss == 0 && k.avSiteSkip == 1 && siteQueries == sq + 1);
        t_avMiss = 2;
        assert(AvSite(world, NULL, NULL, 0.0f, vec, 1, 0) == 0 && t_avMiss == 0 && k.avSiteSkip == 1);
        k.avSiteSkip = 0;
    }

    // the hook: switches off, not the owner, a job (hit: the body does not run; miss: the body runs with the site skipped),
    // each state fall-back, QL busy, a body that never reaches the site, shadow
    const AvAFn savedA = g_avAOrig;
    const AvBFn savedB = g_avBOrig;
    g_avAOrig = GameA;
    g_avBOrig = GameB;
    gameAnswer = 0x705;
    simScanAnswer = 0x900;
    g_async.owner = me;
    g_async.state = A_RUNNING;
    assert(AvAHook(path, agent, goal, ign, 1) == 0x705 && gameCalls == 1);  // switches off: the game's call
    k.avMode = 1;
    g_async.owner = me + 1;
    assert(AvAHook(path, agent, goal, ign, 1) == 0x705 && gameCalls == 2 && k.avTakenHit == 0);
    g_async.owner = me;
    assert(AvAHook(path, agent, goal, ign, 1) == 0x705 && gameCalls == 2 && k.avTakenHit == 1);
    fp[5][0x62] = 0;
    {
        const int sq = siteQueries;
        assert(AvAHook(path, agent, goal, ign, 1) == 0x900 && gameCalls == 3 && k.avTakenMiss == 1 && k.avSiteSkip == 1 &&
               siteQueries == sq && t_avMiss == 0);
    }
    fp[5][0x62] = 1;
    g_inInvoke = 1;
    assert(AvAHook(path, agent, goal, ign, 1) == 0x705 && gameCalls == 4 && k.avFb[AV_FB_STATE] == 1);
    g_inInvoke = 0;
    k.rgOwnerTid = (LONG)me;
    assert(AvAHook(path, agent, goal, ign, 1) == 0x705 && gameCalls == 5 && k.avFb[AV_FB_STATE] == 2);
    k.rgOwnerTid = 0;
    t_rg.n = 1;
    assert(AvAHook(path, agent, goal, ign, 1) == 0x705 && gameCalls == 6 && k.avFb[AV_FB_STATE] == 3);
    t_rg.n = 0;
    cache[4][0x20] = 0;
    assert(AvAHook(path, agent, goal, ign, 1) == 0x705 && gameCalls == 7 && k.avFb[AV_FB_CACHE] == 1);
    cache[4][0x20] = 1;
    TwArm(false);  // disarmed (job end) while the job state still says running
    assert(AvAHook(path, agent, goal, ign, 1) == 0x705 && gameCalls == 8 && k.avFb[AV_FB_JOBEND] == 1);
    TwArm(true);
    // no routing world: the game's call, nothing counted
    g_srWorldGet = []() -> u8* { return NULL; };
    bodySkipsSite = true;
    assert(AvAHook(path, agent, goal, ign, 1) == 0x900 && gameCalls == 9 && t_avMiss == 0);
    // a miss whose body never reaches the site: the flag is cleared by A-entry
    g_srWorldGet = []() -> u8* { return world; };
    fp[5][0x62] = 0;
    assert(AvAHook(path, agent, goal, ign, 1) == 0x900 && gameCalls == 10 && t_avMiss == 0 && k.avTakenMiss == 2 && k.avSiteSkip == 1);
    bodySkipsSite = false;
    fp[5][0x62] = 1;
    // QL held exclusive by a worker writer: the try fails, the game's call
    {
        HANDLE held = CreateEventW(NULL, TRUE, FALSE, NULL), go = CreateEventW(NULL, TRUE, FALSE, NULL);
        struct Hold { HANDLE held, go; } h = {held, go};
        HANDLE th = CreateThread(NULL, 0, [](void* a) -> DWORD {
            Hold* hh = (Hold*)a;
            AcquireSRWLockExclusive(&g_ql);
            SetEvent(hh->held);
            WaitForSingleObject(hh->go, 5000);
            ReleaseSRWLockExclusive(&g_ql);
            return 0;
        }, &h, 0, NULL);
        WaitForSingleObject(held, 5000);
        assert(AvAHook(path, agent, goal, ign, 1) == 0x705 && gameCalls == 11 && k.avFb[AV_FB_TRY] == 1);
        static __declspec(align(16)) float raw[4] = {3.0f, 0.0f, 5.0f, 0.0f};
        bAnswer = 1;
        assert(AvBHook(wm, world, p, raw, 0x77) == 1 && gameCalls == 12 && k.avFb[AV_FB_TRY] == 2);
        SetEvent(go);
        WaitForSingleObject(th, 5000);
        CloseHandle(th);
        CloseHandle(held);
        CloseHandle(go);
        // B through the hook: the clone's answer (0: no 0xC / 0xD / 0x17 footprint), the game's body not run
        assert(AvBHook(wm, world, p, raw, 0x77) == 0 && gameCalls == 12 && k.avTakenB == 1);
        // T-AV: a writer off the sim thread while armed is counted and drops a clone that overlaps it
        simTid = me + 1;  // this thread plays the worker
        g_avTwTramp[0] = (void*)TwTramp0;
        g_avTwTramp[1] = (void*)TwTramp1;
        const LONG ep = g_avTwEpoch;
        assert(AvTwAssign(NULL, NULL, NULL) == 0x1234 && k.avTripAv == 1 && k.avTwSite[0] == 1 && g_avTwEpoch == ep + 1 && g_avTwActive == 0);
        AvTwEdit(1, 2, 3);
        assert(k.avTripAv == 2 && k.avTwSite[1] == 1 && g_avTwEpoch == ep + 2);
        TwArm(false);
        g_async.state = A_IDLE;
        AvTwEdit(1, 2, 3);  // no job, not armed, no clone: not counted
        assert(k.avTripAv == 2 && g_avTwEpoch == ep + 2);
        simTid = me;
        AvTwEdit(1, 2, 3);  // the sim's own call: never counted
        assert(k.avTripAv == 2);
        g_avTwTramp[0] = g_avTwTramp[1] = NULL;
        // shadow (no job, starts held off): equal; the game's footprint differs; the clone finds none and the Sim scan answers
        assert(AvAHook(path, agent, goal, ign, 1) == 0x705 && k.avShadowRuns == 0);  // Mode 1 alone, no job: the game's call
        k.avShadow = 1;
        const int g0 = gameCalls;
        assert(AvAHook(path, agent, goal, ign, 1) == 0x705 && gameCalls == g0 + 1 && k.avShadowRuns == 1 && k.avShadowMismatch == 0 &&
               k.avShadowQueryMismatch == 0 && k.avShadowOrder == 0 && g_rgOffSimOpen == 0);
        gameAnswer = 0x704;
        assert(AvAHook(path, agent, goal, ign, 1) == 0x704 && k.avShadowRuns == 2 && k.avShadowMismatch == 1);
        gameAnswer = 0x705;
        fp[5][0x62] = 0;
        gameAnswer = 0;  // the game found no footprint either: its Sim scan answers 0x900 both times
        assert(AvAHook(path, agent, goal, ign, 1) == 0x900 && k.avShadowRuns == 3 && k.avShadowMismatch == 1 && k.avShadowSimScan == 1 &&
               k.avSiteSkip == 1);  // the shadow's second body does not count as a skip
        gameAnswer = 0x705;  // the game hit 0x705, the clone none: a mismatch
        assert(AvAHook(path, agent, goal, ign, 1) == 0x705 && k.avShadowMismatch == 2);
        fp[5][0x62] = 1;
        // query level: the game's set in another order, then a different set
        const u8* swap = fps[1];
        fps[1] = fps[2], fps[2] = swap;
        int savedN = nfps;
        g_avQuery = [](SrCtx* c, const u8*, DWORD, DWORD, const SrPt&, const u8*) -> int {  // the clone keeps the old order
            static const u8* order[6];
            order[0] = fps[0], order[1] = fps[2], order[2] = fps[1], order[3] = fps[3], order[4] = fps[4], order[5] = fps[5];
            c->n = nfps;
            for (int i = 0; i < nfps; i++) c->objs[i] = order[i];
            return SR_OK;
        };
        AvAHook(path, agent, goal, ign, 1);
        assert(k.avShadowOrder == 1 && k.avShadowQueryMismatch == 0);
        fps[2] = fps[1], fps[1] = swap;
        g_avQuery = Query;
        nfps = 5;  // the game's 0x82EDB0 sees 5, the clone (Query) too: equal; then make the clone see 6
        AvAHook(path, agent, goal, ign, 1);
        assert(k.avShadowQueryMismatch == 0);
        g_avQuery = [](SrCtx* c, const u8*, DWORD, DWORD, const SrPt&, const u8*) -> int {
            c->n = 6;
            for (int i = 0; i < 6; i++) c->objs[i] = fps[i];
            return SR_OK;
        };
        AvAHook(path, agent, goal, ign, 1);
        assert(k.avShadowQueryMismatch == 1);
        g_avQuery = Query;
        nfps = savedN;
        // B shadow: equal, then the game's answer differs
        bAnswer = 0;
        const LONG r0 = k.avShadowRuns;
        assert(AvBHook(wm, world, p, raw, 0x77) == 0 && k.avShadowRuns == r0 + 1 && k.avShadowMismatchB == 0);
        bAnswer = 1;
        assert(AvBHook(wm, world, p, raw, 0x77) == 1 && k.avShadowMismatchB == 1);
        // a shadow clone that gives up is counted, not compared
        cache[4][0x20] = 0;
        const LONG m0 = k.avShadowMismatch;
        AvAHook(path, agent, goal, ign, 1);
        assert(k.avShadowMismatch == m0 && k.avFb[AV_FB_CACHE] == 2);
        cache[4][0x20] = 1;
    }
    assert(g_rgOffSimOpen == 0 && k.avSimWriteInRo == 0);
    printf("avoid RO: clone A/B, A-site skips %ld, hits %ld misses %ld B %ld, fallbacks state %ld try %ld cache %ld jobend %ld, T-AV %ld, "
           "shadow %ld runs (mismatch %ld / B %ld / query %ld / order %ld, sim scan %ld); child footprint whitelist\n",
           k.avSiteSkip, k.avTakenHit, k.avTakenMiss, k.avTakenB, k.avFb[AV_FB_STATE], k.avFb[AV_FB_TRY], k.avFb[AV_FB_CACHE],
           k.avFb[AV_FB_JOBEND], k.avTripAv, k.avShadowRuns, k.avShadowMismatch, k.avShadowMismatchB, k.avShadowQueryMismatch,
           k.avShadowOrder, k.avShadowSimScan);

    g_avAOrig = savedA;
    g_avBOrig = savedB;
    g_srWorldGet = savedWorld;
    g_srLotLookup = savedLook;
    g_srLotFind = savedFind;
    g_srIdList = savedIdList;
    g_srQuery = savedQuery;
    g_srVecRel = savedRel;
    g_srFree = savedFree;
    g_srConv = savedConv;
    g_srWmSlot = savedWm;
    g_srOne = savedOne;
    g_srCheckSlots = true;
    g_avQuery = AvQueryGame;
    g_avLevel = (AvLevelFn)(uintptr_t)0x008468C0;
    g_avPosture = (AvPostureFn)(uintptr_t)0x00953690;
    g_avOpt38 = (AvOptIdFn)(uintptr_t)0x0081C970;
    g_avOpt58 = (AvOptIdFn)(uintptr_t)0x0081C930;
    g_workerTid = savedWorker;
    g_async.owner = 0;
    g_async.state = A_IDLE;
    g_rtSimTidSlot = savedSim;
    memset(&k.avReady, 0, 29 * 4);
}

// --- [ProbeSnap] indirect call target sampler (vcall.cpp): decode, target, live int3 round trip on our own code ---
static volatile LONG g_vcT;
static void __cdecl VcT1() { g_vcT += 1; }
static void __cdecl VcT2() { g_vcT += 10; }
static void VcExec(DWORD code, DWORD eaxv, DWORD ecxv, DWORD arg) {
    __asm {
        mov eax, eaxv
        mov ecx, ecxv
        push arg
        call code
        add esp, 4
    }
}
static void TestProbeSnap() {
    static_assert(offsetof(Ts3nCounters, vcReady) == 0x15C4 && offsetof(Ts3nCounters, vcHits) == 0x15D0 &&
                  offsetof(Ts3nCounters, vcFaults) == 0x15E0 && offsetof(Ts3nCounters, vcArmed) == 0x15E8 &&
                  sizeof(Ts3nCounters) >= 1403 * 4, "v43 layout");
    Ts3nCounters& k = ts3native_counters;
    memset(&k.vcReady, 0, 10 * sizeof(LONG));
    static DWORD tbl[3], vt[2];
    tbl[0] = tbl[2] = vt[1] = (DWORD)(uintptr_t)VcT1;
    tbl[1] = (DWORD)(uintptr_t)VcT2;
    VcSite d;
    const u8 a1[] = {0xFF, 0x50, 0x08}, a2[] = {0xFF, 0x24, 0x85, 0x10, 0, 0, 0}, a3[] = {0xFF, 0x54, 0x24, 0x04},
             a4[] = {0xFF, 0x15, 0, 0, 0, 0}, a5[] = {0xFF, 0x30}, a6[] = {0xFF, 0x94, 0x8E, 0x00, 0x01, 0x00, 0x00};
    assert(VcDecode(a1, 3, d) && d.call && d.mod == 1 && d.base == 0 && d.index == 8 && d.disp == 8);
    assert(!VcDecode(a1, 2, d));  // length differs from the table
    assert(VcDecode(a2, 7, d) && !d.call && d.base == 8 && d.index == 0 && d.scale == 2 && d.disp == 0x10);
    assert(VcDecode(a3, 4, d) && d.call && d.base == 4 && d.index == 8 && d.disp == 4);
    assert(!VcDecode(a4, 6, d) && !VcDecode(a5, 2, d));  // call [abs], push [eax]
    assert(VcDecode(a6, 7, d) && d.base == 6 && d.index == 1 && d.scale == 2 && d.disp == 0x100);  // call [esi+ecx*4+0x100]
    CONTEXT c = {};
    c.Esi = (DWORD)(uintptr_t)tbl - 0x100 - 8;
    c.Ecx = 3;
    DWORD t = 0;
    assert(VcTarget(d, c, &t) && t == tbl[1]);
    VcDecode(a1, 3, d);
    c.Eax = 0x10;
    assert(!VcTarget(d, c, &t));  // unreadable operand

    // live: call [eax+4], call ecx, jmp [eax*4+tbl], call [esp+4], and a row whose bytes differ from memory
    u8* code = (u8*)VirtualAlloc(NULL, 4096, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
    assert(code);
    memset(code, 0xC3, 64);
    const DWORD b = (DWORD)(uintptr_t)code, ta = (DWORD)(uintptr_t)tbl;
    VcSiteDef defs[5] = {{b, 3, {0xFF, 0x50, 0x04}}, {b + 8, 2, {0xFF, 0xD1}}, {b + 16, 7, {0xFF, 0x24, 0x85}},
                         {b + 24, 4, {0xFF, 0x54, 0x24, 0x04}}, {b + 32, 2, {0xFF, 0xD0}}};
    memcpy(defs[2].bytes + 3, &ta, 4);
    for (int i = 0; i < 4; i++) memcpy(code + defs[i].addr - b, defs[i].bytes, defs[i].len);
    code[32] = code[33] = 0x90;
    assert(VcSetup(defs, 5) == 5 && k.vcSites == 5);
    g_vcPer = 3;
    PVOID h = AddVectoredExceptionHandler(1, VcVeh);
    assert(h);
    VcArmAll();
    assert(code[0] == 0xCC && code[8] == 0xCC && code[16] == 0xCC && code[24] == 0xCC && code[32] == 0x90);
    assert(k.vcSkipped == 1 && k.vcArmed == 4 && k.vcWindows == 1);
    g_vcT = 0;
    VcExec(b, (DWORD)(uintptr_t)vt, 0, 0);
    assert(g_vcT == 1);
    VcExec(b + 8, 0, (DWORD)(uintptr_t)VcT2, 0);
    assert(g_vcT == 11);
    VcExec(b + 16, 1, 0, 0);
    VcExec(b + 16, 0, 0, 0);
    assert(g_vcT == 22);
    VcExec(b + 24, 0, 0, (DWORD)(uintptr_t)VcT2);
    assert(g_vcT == 32 && k.vcHits == 5 && k.vcPairs == 5 && k.vcOutside == 0 && g_vc[2].p[0].tgt == (LONG)tbl[1]);
    VcExec(b, (DWORD)(uintptr_t)vt, 0, 0);
    VcExec(b, (DWORD)(uintptr_t)vt, 0, 0);  // third hit: the site disarms itself
    assert(code[0] == 0xFF && k.vcArmed == 3 && g_vc[0].p[0].n == 3);
    VcExec(b, (DWORD)(uintptr_t)vt, 0, 0);  // runs natively now
    assert(g_vcT == 35 && k.vcHits == 7);
    // targets in another module, and more than VC_PAIRS (16) targets on one site
    g_vcPer = 1000;
    const HMODULE k32 = GetModuleHandleA("kernel32.dll");
    const char* const fn[16] = {"GetTickCount", "GetCurrentThreadId", "GetCurrentProcessId", "GetLastError",
                                "GetCurrentThread", "GetCurrentProcess", "GetProcessHeap", "GetOEMCP",
                                "GetACP", "GetVersion", "GetTickCount64", "GetCommandLineA",
                                "GetCommandLineW", "GetEnvironmentStringsW", "GetUserDefaultLCID", "GetSystemDefaultLCID"};
    for (int i = 0; i < 16; i++) {
        const FARPROC f = GetProcAddress(k32, fn[i]);
        assert(f);
        VcExec(b + 8, 0, (DWORD)(uintptr_t)f, 0);
    }
    assert(k.vcOutside == 15 && k.vcOverflow == 1 && g_vc[1].over == 1 && k.vcPairs == 20);
    assert(VcLogPairs() == 21 && VcLogPairs() == 0);  // 1 + (16 + overflow) + 2 + 1 changed pairs, then none
    // next window: hit budget reset, a site whose bytes another hook changed is dropped for good
    code[1] = 0x51;
    VcArmAll();
    assert(g_vc[0].dead && code[0] == 0xFF && k.vcSkipped == 2 && k.vcWindows == 2 && k.vcArmed == 3);
    // Threads=1 (docs/109 §8.8): hits by thread class; only job hits spend PerWindow, the rest VC_OTHER_MUL x it
    {
        const DWORD me = GetCurrentThreadId(), savedWorker = g_workerTid, savedCaller = g_callerTid;
        DWORD simTid = me;
        const DWORD* const savedSim = g_rtSimTidSlot;
        g_rtSimTidSlot = &simTid;
        g_vcThreads = 1;
        g_vcPer = 2;
        g_vc[3].hits = 0;  // still armed from window 1 (one hit then), so window 2 did not reset it
        g_workerTid = me + 7;
        VcExec(b + 24, 0, 0, (DWORD)(uintptr_t)VcT2);  // sim
        g_workerTid = me;
        VcExec(b + 24, 0, 0, (DWORD)(uintptr_t)VcT2);  // worker between plans
        assert(code[24] == 0xCC && g_vc[3].hits == 0 && g_vc[3].other == 2);
        g_callerTid = me;
        VcExec(b + 24, 0, 0, (DWORD)(uintptr_t)VcT2);  // job
        VcExec(b + 24, 0, 0, (DWORD)(uintptr_t)VcT2);  // second job hit: budget spent, disarmed
        const VcPair& q = g_vc[3].p[0];
        assert(code[24] == 0xFF && q.cls[VC_JOB] == 2 && q.cls[VC_WORKER] == 1 && q.cls[VC_SIM] == 1 && q.cls[VC_OTHER] == 0);
        assert(VcLogPairs() == 1);
        g_callerTid = savedCaller;
        g_workerTid = savedWorker;
        g_rtSimTidSlot = savedSim;
        g_vcThreads = 0;
    }
    assert(RemoveVectoredExceptionHandler(h));
    AcquireSRWLockExclusive(&g_vcPoke);
    for (int i = 0; i < g_vcN; i++) VcPokeLocked(g_vc[i], false);
    ReleaseSRWLockExclusive(&g_vcPoke);
    assert(code[8] == 0xFF && code[16] == 0xFF && code[24] == 0xFF && k.vcArmed == 0);
    VirtualFree(code, 0, MEM_RELEASE);
    free(g_vc);
    g_vc = NULL;
    g_vcN = 0;
    g_vcPer = 16;
    memset(&k.vcReady, 0, 10 * sizeof(LONG));
}

// --- [ProbeSnap] CallSites=1 (vcall.cpp, docs/133 §7): the self-disarming rule, a hit racing the disarm, cost counters ---
static void TestCallSites() {
    static_assert(offsetof(Ts3nCounters, cpOn) == 0x517C && offsetof(Ts3nCounters, cpTrapUs) == 0x518C &&
                  offsetof(Ts3nCounters, cpLateMin) == 0x51A4 && offsetof(Ts3nCounters, plChunkMB) == 0x51CC &&
                  offsetof(Ts3nCounters, rpSlimSpare) == 0x51F8 && sizeof(Ts3nCounters) == 0x5F80 && offsetof(Ts3nCounters, luOn) == 0x51FC, "v130 + v131 + v132 + v133 layout");
    static_assert(sizeof(kVcCovSites) / sizeof(kVcCovSites[0]) == 2115, "coverage table = vcallsites5.h");
    Ts3nCounters& k = ts3native_counters;
    memset(&k.vcReady, 0, 10 * sizeof(LONG));
    memset(&k.cpOn, 0, 16 * sizeof(LONG));
    static DWORD vt[8];
    for (int i = 0; i < 8; i++) vt[i] = (DWORD)(uintptr_t)(i & 1 ? VcT2 : VcT1);
    u8* code = (u8*)VirtualAlloc(NULL, 4096, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
    assert(code);
    memset(code, 0xC3, 64);
    const DWORD b = (DWORD)(uintptr_t)code;
    VcSiteDef defs[2] = {{b, 2, {0xFF, 0xD1}}, {b + 8, 2, {0xFF, 0xD1}}};  // call ecx
    memcpy(code, defs[0].bytes, 2);
    memcpy(code + 8, defs[1].bytes, 2);
    assert(VcSetup(defs, 2) == 2);
    g_cpOn = 1;
    g_cpMin = 4; g_cpPoly = 3; g_cpMax = 10;
    PVOID h = AddVectoredExceptionHandler(1, VcVeh);
    assert(h);
    k.cpTrapNs = CpCalibrate();
    assert(k.cpTrapNs > 0 && g_cpCal && k.vcHits == 0);  // calibration traps are not table hits
    VcArmAll();
    assert(code[0] == 0xCC && code[8] == 0xCC && k.vcArmed == 2);
    g_vcT = 0;
    // site 0: one target, disarmed at its MinHits-th hit
    for (int i = 0; i < 4; i++) VcExec(b, 0, vt[0], 0);
    assert(code[0] == 0xFF && k.cpDone == 1 && k.vcArmed == 1 && g_vc[0].hits == 4 && g_vcT == 4);
    // a hit that raced the disarm (int3 raised before the byte came back) still finds its site and is emulated
    CONTEXT c = {};
    c.Ecx = vt[0];
    c.Esp = (DWORD)(uintptr_t)&vt[7];
    c.Eip = b;
    EXCEPTION_RECORD er = {};
    er.ExceptionCode = EXCEPTION_BREAKPOINT;
    er.ExceptionAddress = (PVOID)(uintptr_t)b;
    EXCEPTION_POINTERS ep = {&er, &c};
    assert(VcVeh(&ep) == EXCEPTION_CONTINUE_EXECUTION && c.Eip == vt[0] && vt[7 - 1] == b + 2 && c.Esp == (DWORD)(uintptr_t)&vt[6]);
    assert(code[0] == 0xFF && k.cpDone == 1 && g_vc[0].hits == 5);  // no second disarm, no byte written
    vt[6] = (DWORD)(uintptr_t)VcT1;
    // site 1: three targets by MinHits (poly), sampled on to MaxHits
    VcExec(b + 8, 0, vt[0], 0);
    VcExec(b + 8, 0, vt[1], 0);
    VcExec(b + 8, 0, (DWORD)(uintptr_t)GetTickCount, 0);
    VcExec(b + 8, 0, vt[0], 0);  // MinHits-th hit with 3 targets: kept armed
    assert(code[8] == 0xCC && k.cpPoly == 1 && g_vc[1].hits == 4);
    for (int i = 0; i < 5; i++) VcExec(b + 8, 0, vt[0], 0);
    assert(code[8] == 0xCC && g_vc[1].hits == 9);
    VcExec(b + 8, 0, vt[0], 0);  // MaxHits
    assert(code[8] == 0xFF && g_vc[1].hits == 10 && k.cpDone == 2 && k.vcArmed == 0);
    assert(VcLogPairs(false) > 0 && VcLogPairs(false) == 0);  // coverage: no summary line when nothing changed
    // minute accounting: 1000 hits x cpTrapNs + measured handler ticks; late minutes only past WarmMin
    k.cpTrapNs = 2000;
    g_cpWarmMin = 1;
    assert(CpMinute(1000, 3000, 1000000) == 5000 && k.cpHandlerUs == 3000 && k.cpLateMin == 0);
    assert(CpMinute(10, 0, 1000000) == 20 && k.cpTrapUs == 5020 && k.cpLastMinUs == 20 && k.cpMaxMinUs == 5000);
    assert(k.cpMinutes == 2 && k.cpLateUs == 20 && k.cpLateMin == 1);
    assert(RemoveVectoredExceptionHandler(h));
    VirtualFree(code, 0, MEM_RELEASE);
    free(g_vc);
    g_vc = NULL;
    g_vcN = 0;
    g_cpOn = 0;
    g_cpMin = 32; g_cpPoly = 4; g_cpMax = 256; g_cpWarmMin = 10;
    g_cpTicks = 0;
    memset(&k.vcReady, 0, 10 * sizeof(LONG));
    memset(&k.cpOn, 0, 16 * sizeof(LONG));
}

// --- [ProbeValues] (values.cpp, docs/108): layout, R checksum (same constants as tools/rtdata-decode.ts), frames, live stubs ---
static int __stdcall PvFakeQuery(DWORD, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD) { return 2; }
typedef int(__stdcall* PvLotFn)(DWORD, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD);

static void TestProbeValues() {
    static_assert(offsetof(Ts3nCounters, pvReady) == 0x15EC && offsetof(Ts3nCounters, pvTune) == 0x15FC &&
                  offsetof(Ts3nCounters, pvG) == 0x1650 && offsetof(Ts3nCounters, pvR) == 0x1664 &&
                  offsetof(Ts3nCounters, pvGBuild) == 0x1684 && offsetof(Ts3nCounters, pvAStarBit) == 0x16D0 &&
                  offsetof(Ts3nCounters, pvLotPlanBit) == 0x176C && offsetof(Ts3nCounters, pvStatus) == 0x17FC &&
                  offsetof(Ts3nCounters, pvPortal) == 0x18A8 && offsetof(Ts3nCounters, pvQuery) == 0x190C &&
                  offsetof(Ts3nCounters, pvLazy) == 0x195C && offsetof(Ts3nCounters, pvFrameFull) == 0x19D8 &&
                  offsetof(Ts3nCounters, pvRaiseIn) == 0x19DC && offsetof(Ts3nCounters, pvRestore) == 0x1A00 &&
                  offsetof(Ts3nCounters, pvRBlk) == 0x1A0C && offsetof(Ts3nCounters, pvRRebase) == 0x1A18 &&
                  offsetof(Ts3nCounters, snReady) == 1671 * 4, "v45 layout");
    static_assert(PV_N == 43 && sizeof(kPvStubs) / sizeof(kPvStubs[0]) == PV_N, "one stub per site");
    Ts3nCounters& k = ts3native_counters;
    memset(&k.pvReady, 0, sizeof(Ts3nCounters) - offsetof(Ts3nCounters, pvReady));

    // R checksum: the 2x1-chunk graph of rtdata-decode's self-check in memory form (version 2 records)
    static const u8 raw[31] = {0x02, 0x00, 0x06, 0x00, 0x0E, 0x00, 0x01, 0x41, 0x28, 0x08, 0x10, 0x00, 0x70, 0x04, 0x00, 0x8E,
                               0x28, 0x01, 0x02, 0x01, 0x00, 0x04, 0x00, 0x01, 0x42, 0x28, 0xC8, 0xFF, 0x00, 0x68, 0x03};
    static DWORD tbl[2] = {0, 19};
    DWORD R[5] = {2, 1, (DWORD)(uintptr_t)tbl, (DWORD)(uintptr_t)raw, sizeof(raw)}, out[5] = {};
    // blocked hash: FNV-1a over the dwords (chunk 0, record 1), the one record with bit 7
    assert(PvRSum((const u8*)R, out) && out[0] == 0xE6F9D31A && out[1] == 0x1750A4D1 && out[2] == 3 && out[3] == 1 && out[4] == 0x4BB53254);
    R[4] = 30;  // one byte short: the last record runs past the area
    assert(!PvRSum((const u8*)R, out));
    R[3] = 0;
    assert(!PvRSum((const u8*)R, out));

    // frames on a fake stack: plan scope, H9 per plan, family, dead frames, portal path and state
    DWORD stk[64] = {};
    DWORD mType = 16, opts[20] = {};
    opts[0] = 0x31;  // bits 1, 0x10, 0x20
    opts[5] = 7;     // +0x14
    stk[40] = 0x1234;
    PvEnter(PV_JOB, &stk[40]);
    assert(t_pv.n == 1 && stk[40] == (DWORD)(uintptr_t)PvLeaveThunk && k.pvJobs == 1);
    PvEnter(PV_H9REM, &stk[30]);
    PvEnter(PV_H9INS, &stk[30]);
    stk[21] = (DWORD)(uintptr_t)&mType;  // argument 1 of the call at slot 20
    stk[20] = 0x5678;
    PvEnter(PV_PC_POINT, &stk[20]);
    stk[16] = (DWORD)(uintptr_t)opts;
    PvEnter(PV_ASTAR, &stk[10]);
    assert(k.pvH9[0] == 1 && k.pvH9[1] == 1 && k.pvH9InPlan == 2 && k.pvAStar[2] == 1 && k.pvAStarBit[2][0] == 1 &&
           k.pvAStarBit[2][4] == 1 && k.pvAStarBit[2][5] == 1 && k.pvAStarBit[2][1] == 0 && k.pvAStarRef14[2] == 1 && k.pvAStarRef44[2] == 0);
    assert(PvLeave(&stk[20]) == 0x5678 && k.pvStatus[1][16] == 1 && t_pv.n == 1);
    stk[19] = 0;  // eax
    assert(PvLeave(&stk[40]) == 0x1234 && k.pvH9Plans == 1 && k.pvH9Max == 2 && t_pv.n == 0);
    PvEnter(PV_JOB, &stk[40]);
    PvEnter(PV_PC_PLAN, &stk[30]);
    PvEnter(PV_TIE, &stk[50]);  // above both: they were unwound
    assert(t_pv.n == 0 && k.pvTies == 1 && k.pvH9Plans == 1);
    PvEnter(PV_PATH_LOT, &stk[40]);
    stk[23] = 0x00641234;  // some other caller of the wrapper
    PvEnter(PV_LS_LOCKED, &stk[20]);
    stk[19] = 1;
    PvLeave(&stk[20]);
    stk[23] = PV_INROOM_RET;
    PvEnter(PV_LS_DISC, &stk[20]);
    stk[19] = 3;
    PvLeave(&stk[20]);
    stk[29] = 1;  // initialLocked (arg 9 of the query at slot 20)
    PvEnter(PV_QUERY, &stk[20]);
    stk[19] = 7;
    PvLeave(&stk[20]);
    assert(k.pvPortal[2][1] == 1 && k.pvPortal[4][3] == 1 && k.pvQuery[1][4] == 1 && t_pv.n == 1);
    PvLeave(&stk[40]);
    assert(t_pv.n == 0);

    // v45: display-level raises only inside the CanSee frame, by class (this thread is neither sim nor route worker: 2)
    PvEnter(PV_RAISE0, &stk[20]);
    stk[40] = 0x2222;
    PvEnter(PV_CANSEE, &stk[40]);
    PvEnter(PV_RAISE1, &stk[30]);
    assert(k.pvCanSeeRaise == 2 && k.pvRaiseIn[2] == 1 && k.pvRaiseIn[0] == 0 && k.pvRaiseIn[1] == 0);
    assert(PvLeave(&stk[40]) == 0x2222 && t_pv.n == 0);
    // RoomGraph_FirstUse builds: lot graph unbuilt, or room record byte 0; built rooms and rooms past the end are reads
    static u8 graph[0x98], recs[3 * 0x44];
    graph[0x8C] = 1;
    *(DWORD*)(graph + 0x90) = (DWORD)(uintptr_t)recs;
    *(DWORD*)(graph + 0x94) = (DWORD)(uintptr_t)(recs + sizeof(recs));
    recs[0] = 1;
    stk[21] = (DWORD)(uintptr_t)graph;
    const DWORD rooms[4] = {0, 1, 0x10005, 0};
    for (int i = 0; i < 4; i++) {
        if (i == 3) graph[0x8C] = 0;
        stk[22] = rooms[i];
        PvEnter(PV_LAZY2, &stk[20]);
    }
    assert(k.pvLazy[2][0] == 4 && k.pvRoomBuild[2] == 2 && k.pvRoomBuild[0] == 0);
    // EnsureLevelPolygons rebuilds: level valid byte 0 (ecx = fp at slot[-2])
    static u8 levels[2 * 0x24];
    static DWORD fp[16];
    levels[0x20] = 1;
    fp[0x38 / 4] = (DWORD)(uintptr_t)levels;
    stk[18] = (DWORD)(uintptr_t)fp;
    for (DWORD lv = 0; lv < 2; lv++) { stk[21] = lv; PvEnter(PV_LAZY5, &stk[20]); }
    assert(k.pvLazy[5][0] == 2 && k.pvLevelBuild[2] == 1);
    // RestoreLotBlockedBits: a pvLazy-style row (calls / in a plan / off sim) and a writer log line
    static u8 lot[0x50];
    *(DWORD*)(lot + 0x48) = 0x1234;
    stk[21] = (DWORD)(uintptr_t)lot;
    PvEnter(PV_RESTORE, &stk[20]);
    PvEnter(PV_JOB, &stk[40]);
    PvEnter(PV_RESTORE, &stk[20]);
    assert(k.pvRestore[0] == 2 && k.pvRestore[1] == 1 && k.pvRestore[2] == 2);
    stk[39] = 0;
    PvLeave(&stk[40]);
    assert(t_pv.n == 0);

    // live: a call site (frame, args, return value) and an entry detour, on dummies of the same shape
    u8* code = (u8*)VirtualAlloc(NULL, 4096, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
    assert(code);
    u8* c = code;
    *c++ = 0x6A; *c++ = 0x01;                              // push 1 (arg 9, initialLocked)
    for (int i = 0; i < 8; i++) { *c++ = 0x6A; *c++ = 0x00; }  // push 0 (args 8..1)
    const DWORD site = (DWORD)(uintptr_t)c, fake = (DWORD)(uintptr_t)PvFakeQuery;
    *c++ = 0xE8;
    const LONG rel = (LONG)(fake - (site + 5));
    memcpy(c, &rel, 4);
    c += 4;
    *c++ = 0xC3;
    assert(PvInstallAt(PV_QUERY, site, fake + 1) == 0);  // E8 to another target: skipped
    assert(PvInstallAt(PV_QUERY, site, fake) == 1 && g_pvJmp[PV_QUERY] == (void*)PvFakeQuery);
    assert(((int(__cdecl*)())code)() == 2 && k.pvQuery[1][2] == 1 && t_pv.n == 0);
    u8* lp = code + 256;
    static const u8 kLp[] = {0x81, 0xEC, 0xC0, 0x01, 0x00, 0x00, 0x81, 0xC4, 0xC0, 0x01, 0x00, 0x00, 0xC2, 0x34, 0x00};
    memcpy(lp, kLp, sizeof(kLp));
    assert(PvInstallAt(PV_LOTPLAN, (DWORD)(uintptr_t)lp, 0) == 1);
    stk[41] = (DWORD)(uintptr_t)&mType;
    stk[40] = 0x1111;
    PvEnter(PV_PC_PLAN, &stk[40]);  // the plan family frame (its slot is above this function's callees)
    ((PvLotFn)lp)(1, 2, 3, 4, 5, 6, 7, 8, 0x200011, 10, 11, 1, 13);
    assert(k.pvLotPlan[1] == 1 && k.pvLotPlanBit[1][0] == 1 && k.pvLotPlanBit[1][4] == 1 && k.pvLotPlanBit[1][9] == 1 &&
           k.pvLotPlanBit[1][1] == 0 && k.pvLotPlanObst[1] == 1 && t_pv.n == 1);
    assert(PvLeave(&stk[40]) == 0x1111 && k.pvStatus[0][16] == 1);
    // U5 (docs/112 §2): Route_PlanCommon's shape, thiscall with two stack args and `ret 8`; the callee writes mType 16 into
    // argument 1 and argument 2 is a vector whose first dword is a heap pointer (reading arg 2 bins it as 19+)
    static DWORD res[18], vec[3];
    vec[0] = (DWORD)(uintptr_t)vec;
    u8* pc = code + 512;
    static const u8 kPc[] = {0x8B, 0x44, 0x24, 0x04, 0xC7, 0x00, 0x10, 0x00, 0x00, 0x00, 0xC2, 0x08, 0x00};  // eax = arg 1; [eax] = 16; ret 8
    memcpy(pc, kPc, sizeof(kPc));
    c = code + 640;
    u8* const pcCall = c;
    *c++ = 0x68; *(DWORD*)c = (DWORD)(uintptr_t)vec; c += 4;  // push arg 2
    *c++ = 0x68; *(DWORD*)c = (DWORD)(uintptr_t)res; c += 4;  // push arg 1
    const DWORD pcSite = (DWORD)(uintptr_t)c;
    *c++ = 0xE8;
    *(LONG*)c = (LONG)((DWORD)(uintptr_t)pc - (pcSite + 5));
    c += 4;
    *c++ = 0xC3;
    assert(PvInstallAt(PV_PC_PLAN, pcSite, (DWORD)(uintptr_t)pc) == 1);
    assert(((DWORD(__cdecl*)())pcCall)() == (DWORD)(uintptr_t)res && res[0] == 16);
    assert(k.pvStatus[0][16] == 2 && k.pvStatus[0][19] == 0 && t_pv.n == 0);
    VirtualFree(code, 0, MEM_RELEASE);

    // an entry owned by a route gate stub is counted from inside it (0x63D2F0 is not mapped here: the bytes differ)
    void* saved = g_rgTramp[2];
    g_rgTramp[2] = (void*)1;
    assert(PvInstallAt(PV_LAZY2, kRgSites[2].addr, 0) == 2 && g_pvRg[2] == PV_LAZY2 + 1);
    g_rgTramp[2] = saved;
    g_pvRg[2] = 0;
    assert(PvInstallAt(PV_LAZY1, 0x10, 0) == 0);  // unreadable, no owner: skipped
    memset(&k.pvReady, 0, sizeof(Ts3nCounters) - offsetof(Ts3nCounters, pvReady));
}

// --- [PlanRepeat] (planrepeat.cpp, docs/23 §18): key, result summary, IC class, ring windows, world version split ---
static __declspec(noinline) bool PrNestedBegin(const int* s, const int* g, const u8* opts) {  // a frame deeper than the caller's token
    PrTok inner;
    return PrBegin(inner, (void*)0x1000, s, g, 0x100, 1, opts, 5, 0);
}
static void TestPlanRepeat() {
    Ts3nCounters& k = ts3native_counters;
    memset(&k.prReady, 0, sizeof(Ts3nCounters) - offsetof(Ts3nCounters, prReady));
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
    // key: two 64-byte goal records, options with an ignore list
    static DWORD rec[3][16] = {};
    rec[0][0] = 0x41200000; rec[1][0] = 0x42480000; rec[1][8] = 0x3F800000;  // x = 10 / 50, a radius-like word at +0x20
    int s[2] = {(int)(uintptr_t)rec[0], (int)(uintptr_t)rec[1]}, g[2] = {(int)(uintptr_t)rec[1], (int)(uintptr_t)rec[2]};
    static u64 ign[2] = {0x1234, 0x5678};
    static u8 opts[0x80] = {};
    *(u64**)(opts + 0x38) = ign;
    *(u64**)(opts + 0x3C) = ign + 2;
    u64 key, loose, k2, l2;
    assert(PrKey((void*)0x1000, s, g, 0x100, 1, opts, 5, 0, &key, &loose) && key && loose);
    assert(PrKey((void*)0x1000, s, g, 0x100, 0x7701, opts, 5, 0, &k2, &l2) && k2 == key && l2 == loose);  // doPlan: low byte only
    rec[1][8] = 0x40000000;  // radius word: strict key changes, loose key (positions in 0.5 m cells) does not
    assert(PrKey((void*)0x1000, s, g, 0x100, 1, opts, 5, 0, &k2, &l2) && k2 != key && l2 == loose);
    rec[1][8] = 0x3F800000;
    ign[1] = 0x9999;  // ignore list content
    assert(PrKey((void*)0x1000, s, g, 0x100, 1, opts, 5, 0, &k2, &l2) && k2 != key);
    ign[1] = 0x5678;
    assert(PrKey((void*)0x2000, s, g, 0x100, 1, opts, 5, 0, &k2, &l2) && k2 != key);  // another routing world
    assert(!PrKey((void*)0x1000, s, g, 0x100, 1, (const u8*)0x10, 5, 0, &k2, &l2));  // unreadable options: fault, no key
    // result: out list anchor {next, prev} -> node {next, prev, path}; path status +0x94, 48-byte nodes +0x98/+0x9C
    static u8 path[0x190] = {};
    static float nodes[2][12] = {{1, 2, 3}, {4, 5, 6}};
    *(float**)(path + 0x98) = nodes[0];
    *(float**)(path + 0x9C) = nodes[0] + 24;  // end = past node 1
    int anchor[2], node[3];
    anchor[0] = (int)(uintptr_t)node; anchor[1] = (int)(uintptr_t)node;
    node[0] = (int)(uintptr_t)anchor; node[1] = (int)(uintptr_t)anchor; node[2] = (int)(uintptr_t)path;
    u64 res, res2;
    assert(PrResult(anchor, 1, &res) && PrResult(anchor, 1, &res2) && res == res2);
    assert(PrResult(anchor, 0, &res2) && res2 != res);  // failed plan
    nodes[1][2] = 6.5f;  // end point moved
    assert(PrResult(anchor, 1, &res2) && res2 != res);
    nodes[1][2] = 6;
    node[2] = 0x10;  // unreadable path
    assert(!PrResult(anchor, 1, &res2));
    node[2] = (int)(uintptr_t)path;
    // class: sim thread inside a Replan IC frame (dead frames below `here` ignored), outside any IC, another thread
    DWORD simTid = GetCurrentThreadId();
    const DWORD* const savedSim = g_rtSimTidSlot;
    g_rtSimTidSlot = &simTid;
    DWORD slots[4];
    t_sn.n = 2;
    t_sn.f[0] = {&slots[3], 0, SRC_IC, (u8)(SN_IC_START + 1)};
    t_sn.f[1] = {&slots[0], 0, SRC_IC, (u8)(SN_IC_START + 3)};  // at or below `here`: unwound
    assert(PrClass(&slots[1]) == 1);
    t_sn.n = 0;
    assert(PrClass(&slots[1]) == 4);
    simTid = simTid + 1;
    assert(PrClass(&slots[1]) == 5);  // stays another thread for the begin/end part below
    // ring: exact repeats by window, result stability, footprint-only and other changes, loose key, window edge
    if (!g_prRing) g_prRing = (PrEntry*)calloc(PR_RING, sizeof(PrEntry));
    assert(g_prRing);
    g_prHead = g_prN = 0;
    const ULONGLONG t0 = 1000000;
    PrNote(0, 0xA, 0xAA, 10, 5, 10, 5, t0, 0x111, 100);
    assert(k.prExact[0][2] == 0 && k.prRing == 1);
    PrNote(1, 0xA, 0xAA, 10, 5, 10, 5, t0 + 500, 0x111, 200);  // 0.5 s, same answer
    assert(k.prExact[1][0] == 1 && k.prExact[1][1] == 1 && k.prExact[1][2] == 1 && k.prExactUs[1][0] == 200 && k.prMatch[1] == 1);
    PrNote(1, 0xA, 0xAA, 10, 5, 10, 5, t0 + 4000, 0x222, 300);  // 3.5 s after the newest, another answer
    assert(k.prExact[1][0] == 1 && k.prExact[1][1] == 2 && k.prExactUs[1][1] == 500 && k.prMismatch[1] == 1);
    PrNote(2, 0xA, 0xAA, 12, 5, 12, 5, t0 + 20000, 0x222, 50);  // only footprints changed, same answer
    assert(k.prFpOnly[2] == 1 && k.prFpOnlyMatch[2] == 1 && k.prFpOnlyUs[2] == 50 && k.prExact[2][2] == 0);
    PrNote(2, 0xA, 0xAA, 15, 7, 15, 7, t0 + 49000, 0x333, 50);  // 29 s later, a static writer changed
    assert(k.prChanged[2] == 1 && k.prLoose[2] == 0);
    PrNote(3, 0xB, 0xAA, 15, 7, 16, 7, t0 + 49100, 0x333, 70);  // other strict key, same loose key and version
    assert(k.prLoose[3] == 1 && k.prLooseUs[3] == 70 && k.prExact[3][2] == 0);
    PrNote(3, 0xB, 0xBB, 16, 7, 16, 7, t0 + 79200, 0x333, 70);  // 30.1 s after the last 0xB: outside every window
    assert(k.prExact[3][2] == 0 && k.prChanged[3] == 0 && k.prFpOnly[3] == 0 && k.prVersion == 16);
    // a full ring of young entries counts evictions
    for (LONG i = 0; i < PR_RING; i++) PrNote(4, 0x100 + i, 0x100 + i, 16, 7, 16, 7, t0 + 80000, 1, 1);
    assert(k.prRing == PR_RING && k.prEvictYoung == 1);  // only the 0.8 s old entry was young
    // versions: a footprint leaf moves only the full version, a static leaf or writer IC both
    DWORD v0, s0, v1, s1;
    PrVersions(&v0, &s0);
    k.snLeaf[SL_W1][0]++;
    PrVersions(&v1, &s1);
    assert(v1 == v0 + 1 && s1 == s0);
    k.snLeaf[SL_W6][0]++;  // cache fill: no change
    k.snIc[SN_IC_START]++;  // a route IC: no change
    PrVersions(&v1, &s1);
    assert(v1 == v0 + 1 && s1 == s0);
    k.snLeaf[SL_W4][2]++;
    k.snIc[0]++;
    PrVersions(&v1, &s1);
    assert(v1 == v0 + 3 && s1 == s0 + 2);
    // begin/end: off = not counted; on: nested plan not counted, an unwound outer one is
    g_prOn = 0;
    PrTok tok;
    assert(!PrBegin(tok, (void*)0x1000, s, g, 0x100, 1, opts, 5, 0));
    g_prOn = 1;
    g_prHead = g_prN = 0;
    assert(PrBegin(tok, (void*)0x1000, s, g, 0x100, 1, opts, 5, 0) && tok.keyed);
    assert(!PrNestedBegin(s, g, opts));
    PrEnd(true, tok, anchor, 1);
    assert(k.prPlans[5] == 1 && t_prOuter == 0 && k.prRing == 1);
    assert(PrBegin(tok, (void*)0x1000, s, g, 0x100, 1, opts, 5, 0));
    PrEnd(true, tok, anchor, 1);
    assert(k.prExact[5][0] == 1 && k.prMatch[5] == 1);
    t_prOuter = (uintptr_t)&tok - 64;  // an outer plan deeper than us that never ended
    assert(PrBegin(tok, (void*)0x1000, s, g, 0x100, 1, opts, 5, 0) && k.prUnwound == 1);
    PrEnd(true, tok, anchor, 1);
    g_prOn = 0;
    memset(&k.prReady, 0, sizeof(Ts3nCounters) - offsetof(Ts3nCounters, prReady));
    memset(k.snLeaf, 0, sizeof(k.snLeaf));
    memset(k.snIc, 0, sizeof(k.snIc));
    g_rtSimTidSlot = savedSim;
}

// --- [LoadProf] (loadprof.cpp, docs/39 §10): window, rows, outermost routing time, sites skipped on other bytes ---
typedef int(__cdecl* LpInnerFn)();
static LpInnerFn g_lpInner;
static int __cdecl LpFakeOuter() { return g_lpInner() + 1; }  // the routing world setup calling a hooked routing entry
static void TestLoadProf() {
    static_assert(LP_N == 28 && sizeof(kLpStubs) / sizeof(kLpStubs[0]) == LP_N && sizeof(kLpSites) / sizeof(kLpSites[0]) == LP_N, "one stub per site");
    static_assert(sizeof(((Ts3nCounters*)0)->lpSite) / sizeof(((Ts3nCounters*)0)->lpSite[0]) == LP_N, "a counter row per site");
    Ts3nCounters& k = ts3native_counters;
    memset(&k.lpReady, 0, sizeof(Ts3nCounters) - offsetof(Ts3nCounters, lpReady));
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
    u8* code = (u8*)VirtualAlloc(NULL, 4096, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
    assert(code);
    // entry dummy with LotQuadTree_ApplyInsert's prologue: sub esp,80h; add esp,80h; mov eax,5; ret
    static const u8 kIns[] = {0x81, 0xEC, 0x80, 0x00, 0x00, 0x00, 0x81, 0xC4, 0x80, 0x00, 0x00, 0x00, 0xB8, 0x05, 0x00, 0x00, 0x00, 0xC3};
    memcpy(code, kIns, sizeof(kIns));
    assert(LpInstallAt(LP_QTINS, (DWORD)(uintptr_t)code, 0));
    g_lpInner = (LpInnerFn)code;
    u8* c = code + 256;  // call site: call LpFakeOuter; ret
    const DWORD site = (DWORD)(uintptr_t)c, outer = (DWORD)(uintptr_t)LpFakeOuter;
    *c++ = 0xE8;
    *(LONG*)c = (LONG)(outer - (site + 5));
    c += 4;
    *c = 0xC3;
    assert(!LpInstallAt(LP_WORLDINIT, site, outer + 1));  // E8 to another target: skipped
    assert(LpInstallAt(LP_WORLDINIT, site, outer));
    assert(!LpInstallAt(LP_LOTINS, 0x10, 0));  // unreadable: skipped
    const LpInnerFn run = (LpInnerFn)(uintptr_t)site;
    // window: LoadWorld entry (frame on a slot above this function's callees), the nested routing pair, event 7, LoadWorld leave
    DWORD stk[4] = {0, 0x1111, 0, 0};
    LpEnter(LP_LOADWORLD0, &stk[1]);
    assert(g_lpOpen == 1 && k.lpLoads == 1 && stk[1] == (DWORD)(uintptr_t)LpLeaveThunk && t_lp.n == 1);
    assert(run() == 6 && t_lp.n == 1);
    assert(k.lpSite[LP_WORLDINIT][0][0] == 1 && k.lpSite[LP_QTINS][0][0] == 1 && k.lpSite[LP_QTINS][1][0] == 0);
    assert(k.lpRtUs[0] == k.lpSite[LP_WORLDINIT][0][1] && k.lpSite[LP_QTINS][0][1] <= k.lpRtUs[0]);  // outermost only
    assert(k.lpRtThrUs[0] + k.lpRtThrUs[1] + k.lpRtThrUs[2] == k.lpRtUs[0]);
    LpEnter(LP_EV7, &stk[0]);
    assert(g_lpOpen == 0 && k.lpEv7 == 1 && t_lp.n == 1);
    LpEnter(LP_EV7, &stk[0]);  // a second event 7 with no window open: nothing
    assert(k.lpEv7 == 1);
    assert(LpLeave(&stk[1]) == 0x1111 && t_lp.n == 0 && k.lpSite[LP_LOADWORLD0][0][0] == 1);
    // after the window: row 1; the inner entry alone is outermost then
    assert(g_lpInner() == 5 && run() == 6);
    assert(k.lpSite[LP_QTINS][1][0] == 2 && k.lpSite[LP_WORLDINIT][1][0] == 1 && k.lpSite[LP_QTINS][0][0] == 1);
    assert(k.lpRtUs[1] >= k.lpSite[LP_WORLDINIT][1][1]);
    // a new LoadWorld zeroes the rows and opens a new window
    stk[1] = 0x1111;  // the thunk puts the real return address back; here the test does
    LpEnter(LP_LOADWORLD1, &stk[1]);
    assert(k.lpLoads == 2 && k.lpEv7 == 1 && k.lpSite[LP_QTINS][1][0] == 0 && k.lpRtUs[1] == 0 && g_lpOpen == 1);
    assert(LpLeave(&stk[1]) == 0x1111 && k.lpSite[LP_LOADWORLD1][0][0] == 1);
    g_lpOpen = 0;
    VirtualFree(code, 0, MEM_RELEASE);
    memset(&k.lpReady, 0, sizeof(Ts3nCounters) - offsetof(Ts3nCounters, lpReady));
    printf("load profile: window, rows, outermost routing time and skips ok\n");
}

// --- [RouteSnap] (routesnap.cpp, docs/94 P1): layout, attribution, START windows, object nodes, live stubs, sharing ---
static int __cdecl SnFakeIc(int a, int b) { return a + b; }
static int __cdecl SnFakeCallee() { return 77; }
struct SnFakeNode { LONG pri; void* fn; SnFakeNode* next; };
static SnFakeNode g_snFakeNodes[64];
static int g_snFakeN;
static DWORD g_snFakeHeads[42];
static char __cdecl SnFakeReg(DWORD id, void* fn, LONG pri) {  // ObjectEvents_Register: before the first node with a lower priority
    if (g_snFakeN == 64) return 0;
    SnFakeNode* n = &g_snFakeNodes[g_snFakeN++];
    n->pri = pri;
    n->fn = fn;
    SnFakeNode *prev = NULL, *cur = (SnFakeNode*)(uintptr_t)g_snFakeHeads[id];
    while (cur && cur->pri >= pri) { prev = cur; cur = cur->next; }
    n->next = cur;
    if (prev) prev->next = n; else g_snFakeHeads[id] = (DWORD)(uintptr_t)n;
    return 1;
}
static char SnFakeChain(DWORD id) {  // GlobalHandlerChain 0x936C90: stops at the first handler that returns 0
    for (SnFakeNode* n = (SnFakeNode*)(uintptr_t)g_snFakeHeads[id]; n; n = n->next)
        if (!((char(__cdecl*)(void*, void*))n->fn)(NULL, NULL)) return 0;
    return 1;
}
static DWORD g_snFakeStk[8];
static char __cdecl SnFakeFootprintHandler(void*, void*) { g_snFakeStk[0] = 0x00947100; SnEnter(13, &g_snFakeStk[0]); return 1; }
static char __cdecl SnFakeStopper(void*, void*) { return 0; }

static void TestRouteSnap() {
    static_assert(offsetof(Ts3nCounters, snReady) == 0x1A1C && offsetof(Ts3nCounters, snLeaf) == 0x1A30 &&
                  offsetof(Ts3nCounters, snSrc) == 0x1B14 && offsetof(Ts3nCounters, snUnattributed) == 0x1C90 &&
                  offsetof(Ts3nCounters, snUaKey) == 0x1C94 && offsetof(Ts3nCounters, snWev) == 0x1D18 &&
                  offsetof(Ts3nCounters, snObj) == 0x1E9C && offsetof(Ts3nCounters, snIc) == 0x1F08 &&
                  offsetof(Ts3nCounters, snIcWrites) == 0x1FA4 && offsetof(Ts3nCounters, snGSite) == 0x2044 &&
                  offsetof(Ts3nCounters, snStartLots) == 0x205C && offsetof(Ts3nCounters, snFpEn) == 0x20A4 &&
                  offsetof(Ts3nCounters, snFrameFull) == 0x20BC && offsetof(Ts3nCounters, spReady) == 2096 * 4, "v46 layout");
    static_assert(offsetof(Ts3nCounters, snSrc2) == 0x2198 && offsetof(Ts3nCounters, snRec) == 0x23C8 &&
                  offsetof(Ts3nCounters, snLotCopyUs) == 0x2438 && offsetof(Ts3nCounters, snUa2Key) == 0x2464 &&
                  offsetof(Ts3nCounters, snUa2Other) == 0x2764 && sizeof(Ts3nCounters) >= 0x2768, "v48 layout");
    static_assert(SN_N == 38 && SN_F0 + 6 == SN_N && SRC_N2 == SRC_N + 7 && sizeof(kSnStubs) / sizeof(kSnStubs[0]) == SN_N && SN_IC_N == 39 &&
                  sizeof(kSnIcShims) / sizeof(kSnIcShims[0]) == SN_IC_N && SL_N == 19, "one stub per site and slot");
    Ts3nCounters& k = ts3native_counters;
    memset(&k.snReady, 0, sizeof(Ts3nCounters) - offsetof(Ts3nCounters, snReady));
    memcpy(g_snIc, kSnIcDef, sizeof(g_snIc));
    DWORD* const sim = (DWORD*)g_rtSimTidSlot;
    const DWORD savedSim = *sim;

    // off the sim thread: counted by class, no attribution
    DWORD stk[128] = {};  // the next-frame scan reads 32 words above a leaf's slot
    static u8 lotA[0x50], lotB[0x50];
    *(DWORD*)(lotA + 0x48) = 0x1234;
    *(DWORD*)(lotB + 0x48) = 0x5678;
    stk[20] = 0x00ABCDEF;
    stk[21] = (DWORD)(uintptr_t)lotA;
    SnEnter(18, &stk[20]);  // ApplyLotUpdate
    assert(k.snLeaf[SL_APPLYLOT][2] == 1 && k.snUnattributed == 0 && k.snSrc[SL_APPLYLOT][SRC_NONE] == 0);

    *sim = GetCurrentThreadId();
    // no source open: unattributed, keyed by the caller
    SnEnter(18, &stk[20]);
    assert(k.snLeaf[SL_APPLYLOT][0] == 1 && k.snSrc[SL_APPLYLOT][SRC_NONE] == 1 && k.snUnattributed == 1 &&
           k.snUa2Key[0] == (LONG)(SL_APPLYLOT << 24 | 0x00ABCDEF) && k.snUa2N[0] == 1 && k.snUaKey[0] == 0);
    // IC frame: writes inside it count for that IC; the leave restores the return address
    stk[40] = 0x1111;
    SnIcEnter(4, &stk[40]);  // AddPortal
    assert(k.snIc[4] == 1 && t_sn.n == 1 && stk[40] == (DWORD)(uintptr_t)SnLeaveThunk);
    SnEnter(13, &stk[20]);   // UpdateObjectFootprintCore
    assert(k.snSrc[SL_FPCORE][SRC_IC] == 1 && k.snIcWrites[4] == 1 && k.snUnattributed == 1);
    assert(SnLeave(&stk[40]) == 0x1111 && t_sn.n == 0);
    // World_FireEvent frame, counted by id (argument 2)
    stk[40] = 0x2222;
    stk[42] = 0x2F;
    SnEnter(SN_WEV, &stk[40]);
    SnEnter(15, &stk[20]);  // remove leaf
    assert(k.snWev[0x2F] == 1 && k.snSrc[SL_FPREMOVE][SRC_WEV] == 1 && SnLeave(&stk[40]) == 0x2222);
    // Footprint_SetEnabled frame; a frame left by an unwind is dropped at the next entry above it
    stk[40] = 0x3333;
    SnEnter(SN_FPEN, &stk[40]);
    SnEnter(14, &stk[20]);  // AddPrivateFootprint
    assert(k.snFpEn[0] == 1 && k.snSrc[SL_PRIVATE][SRC_FPEN] == 1 && t_sn.n == 1);
    SnEnter(14, &stk[50]);  // above the frame's slot: it was unwound
    assert(t_sn.n == 0 && k.snSrc[SL_PRIVATE][SRC_NONE] == 1);

    // object nodes: registered once at world event 7 (head first, tail last), a game handler between them is attributed
    g_snObjReg = SnFakeReg;
    g_snObjHeads = g_snFakeHeads;
    SnFakeReg(0x21, (void*)SnFakeFootprintHandler, 0);
    SnFakeReg(0x0E, (void*)SnFakeStopper, 0);
    stk[40] = 0x4444;
    stk[42] = 7;
    SnEnter(SN_WEV, &stk[40]);
    assert(SnLeave(&stk[40]) == 0x4444 && k.snObjNodes == 16 && k.snObjRegFails == 0 && (k.snReady & 8) && k.snSeqEv7 == 1);
    SnObjRegister();  // already listed: nothing new
    assert(k.snObjNodes == 16 && g_snFakeN == 18);
    assert(((SnFakeNode*)(uintptr_t)g_snFakeHeads[0x21])->fn == (void*)SnObjHead<7>);
    assert(SnFakeChain(0x21) == 1 && k.snObj[7][0] == 1 && t_sn.objDepth == 0 && k.snSrc[SL_FPCORE][SRC_OBJ] == 1);
    assert(SnFakeChain(0x0E) == 0 && k.snObj[5][0] == 1 && t_sn.objDepth == 1);  // stopped before the tail
    stk[40] = 0x5555;
    SnIcEnter(25, &stk[40]);
    assert(k.snObjOpenAtIc == 1 && t_sn.objDepth == 0 && SnLeave(&stk[40]) == 0x5555);

    // START windows: distinct known lots, writes, unknown-lot writes; overflow past 16 lots
    SnStart();  // closes the window of the writes above; its bins are cleared
    memset(k.snStartLots, 0, offsetof(Ts3nCounters, snFpEn) - offsetof(Ts3nCounters, snStartLots));
    const LONG starts = k.snStarts;
    stk[21] = (DWORD)(uintptr_t)lotA;
    SnEnter(18, &stk[20]);
    SnEnter(18, &stk[20]);
    stk[21] = (DWORD)(uintptr_t)lotB;
    SnEnter(19, &stk[20]);  // RestoreLotBlockedBits, the same lot key rule
    stk[21] = 7;
    stk[22] = 0;
    SnEnter(17, &stk[20]);  // RemoveLot(lo, hi)
    SnEnter(13, &stk[20]);  // no lot argument
    stk[40] = 0x6666;
    SnIcEnter(SN_IC_START, &stk[40]);
    assert(k.snStarts == starts + 1 && k.snStartLots[3] == 1 && k.snStartWrites[2] == 1 && k.snStartUnknown == 1);
    assert(SnLeave(&stk[40]) == 0x6666 && k.snIc[SN_IC_START] == 1);
    for (DWORD i = 0; i < 18; i++) { stk[21] = 100 + i; SnEnter(17, &stk[20]); }
    SnIcEnter(SN_IC_START + 1, &stk[40]);
    assert(k.snStartLots[7] == 1 && k.snSetFull == 1 && k.snStartWrites[4] == 1 && k.snStartUnknown == 1);
    SnLeave(&stk[40]);

    // blend reload: only the reload pass (argument 3 == 4); G builds: per site, sequenced after event 7
    stk[23] = 3;
    SnEnter(29, &stk[20]);
    stk[23] = 4;
    SnEnter(29, &stk[20]);
    SnEnter(SN_G0 + 3, &stk[20]);
    assert(k.snLeaf[SL_BLEND][0] == 1 && k.snGSite[3] == 1 && k.snLeaf[SL_GBUILD][0] == 1 && k.snSeqGBuild > k.snSeqEv7);

    // live: an entry detour, a call site and an IC slot on dummies of the same shape
    u8* code = (u8*)VirtualAlloc(NULL, 4096, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
    assert(code);
    static const u8 kFn[] = {0x83, 0xEC, 0x08, 0x56, 0x57, 0xB8, 0x2A, 0x00, 0x00, 0x00, 0x5F, 0x5E, 0x83, 0xC4, 0x08, 0xC3};
    memcpy(code, kFn, sizeof(kFn));  // sub esp,8; push esi; push edi; mov eax,42; pop edi; pop esi; add esp,8; ret
    assert(SnInstallAt(15, (DWORD)(uintptr_t)code, 0) == 1);
    const LONG rm = k.snLeaf[SL_FPREMOVE][0];
    assert(((int(__cdecl*)())code)() == 42 && k.snLeaf[SL_FPREMOVE][0] == rm + 1 && t_sn.n == 0);
    u8* c = code + 256;
    const DWORD site = (DWORD)(uintptr_t)c, fake = (DWORD)(uintptr_t)SnFakeCallee;
    *c++ = 0xE8;
    *(LONG*)c = (LONG)(fake - (site + 5));
    c += 4;
    *c++ = 0xC3;
    assert(SnInstallAt(SN_G0, site, fake + 1) == 0);  // E8 to another target: skipped
    assert(SnInstallAt(SN_G0, site, fake) == 1 && g_snJmp[SN_G0] == (void*)SnFakeCallee);
    assert(((int(__cdecl*)())(code + 256))() == 77 && k.snGSite[0] == 1);
    assert(SnInstallAt(13, 0x10, 0) == 0);  // unreadable, no owner: skipped
    static DWORD tbl[2] = {(DWORD)(uintptr_t)SnFakeIc, 0x1234};
    g_snIc[0].slot = (DWORD)(uintptr_t)&tbl[0];
    g_snIc[0].fn = (DWORD)(uintptr_t)SnFakeIc;
    g_snIc[1].slot = (DWORD)(uintptr_t)&tbl[1];
    assert(SnSwapIc(0) && tbl[0] == (DWORD)(uintptr_t)SnIc0 && !SnSwapIc(1) && tbl[1] == 0x1234);
    assert(((int(__cdecl*)(int, int))(uintptr_t)tbl[0])(2, 3) == 5 && k.snIc[0] == 1 && t_sn.n == 0);
    *sim = 0;
    assert(((int(__cdecl*)(int, int))(uintptr_t)tbl[0])(4, 5) == 9 && k.snIc[0] == 2 && k.snIcOffSim == 1);
    *sim = GetCurrentThreadId();
    VirtualFree(code, 0, MEM_RELEASE);

    // one owner per address: a site another stub of ours owns is counted from inside it
    void* saved = g_qlTramp[0];
    g_qlTramp[0] = (void*)1;
    assert(SnInstallAt(0, kQlSites[0].addr, 0) == 2 && g_snQl[0] == 1);
    const LONG w1 = k.snLeaf[SL_W1][0];
    QlEnter(0, &stk[20]);
    assert(k.snLeaf[SL_W1][0] == w1 + 1);
    g_qlTramp[0] = saved;
    g_snQl[0] = 0;
    saved = g_rgTramp[3];
    g_rgTramp[3] = (void*)1;
    assert(SnInstallAt(12, kRgSites[3].addr, 0) == 2 && g_snRg[3] == 13);
    g_rgTramp[3] = saved;
    g_snRg[3] = 0;
    saved = g_pvJmp[PV_LAZY3];
    g_pvJmp[PV_LAZY3] = (void*)1;
    assert(SnInstallAt(18, kPv[PV_LAZY3].at, 0) == 2 && g_snPv[PV_LAZY3] == 19);
    const LONG al = k.snLeaf[SL_APPLYLOT][0];
    stk[21] = 0;
    PvEnter(PV_LAZY3, &stk[20]);
    assert(k.snLeaf[SL_APPLYLOT][0] == al + 1);
    g_pvJmp[PV_LAZY3] = saved;
    g_snPv[PV_LAZY3] = 0;
    saved = g_pvJmp[PV_GB_MSG];
    g_pvJmp[PV_GB_MSG] = (void*)1;
    assert(SnInstallAt(SN_G0, kPv[PV_GB_MSG].at, kPv[PV_GB_MSG].target) == 2 && g_snPv[PV_GB_MSG] == SN_G0 + 1);
    g_pvJmp[PV_GB_MSG] = saved;
    g_snPv[PV_GB_MSG] = 0;
    // every shared row has the owner's bytes (same address, same prologue or call target)
    for (int i = 0; i < SN_N; i++) {
        for (int q = 0; q < QL_N; q++) if (kQlSites[q].addr == kSn[i].at) assert(!memcmp(kQlSites[q].pro, kSn[i].pro, kSn[i].len) && kQlSites[q].len == kSn[i].len);
        for (int p = 0; p < PV_N; p++) if (kPv[p].at == kSn[i].at) assert(kPv[p].target == kSn[i].target && (kSn[i].target || !memcmp(kPv[p].pro, kSn[i].pro, kSn[i].len)));
    }

    *sim = savedSim;
    g_snObjReg = (SnObjRegFn)(uintptr_t)0x00936BC0;
    g_snObjHeads = (DWORD*)(uintptr_t)0x011DC2A8;
    memcpy(g_snIc, kSnIcDef, sizeof(g_snIc));
    t_sn.n = 0;
    t_sn.objDepth = 0;
    memset(&k.pvReady, 0, sizeof(Ts3nCounters) - offsetof(Ts3nCounters, pvReady));
}

// [RouteSnap] Enabled=1 (snap.cpp, docs/94 P3): K1/K2/K5 on the fake world (C rasteriser), the tile rule, the door channel
// 0.35.15 (docs/94 §22.18): the cut's image build cost. SpSortU64 == qsort order; grid merge == full sort is K1's
// "incremental == full rebuild" (SnapK1). Micro-benchmark of the parts the cut dropped or replaced, at live sizes: a 40 KB
// tile and a 1 MB R hashed (FNV-1a, gone) vs copied, bnm sort qsort vs radix, grid 800 rows full qsort vs merge
static double BenchUs(LARGE_INTEGER a, LARGE_INTEGER b) { LARGE_INTEGER f; QueryPerformanceFrequency(&f); return (b.QuadPart - a.QuadPart) * 1e6 / f.QuadPart; }
static void TestSnapCost() {
    DWORD seed = 7;
    auto rnd = [&]() { seed = seed * 1103515245 + 12345; return seed >> 8; };
    static u64 a[40000], b[40000];
    const DWORD ns[] = {0, 1, 2, 3, 100, 4096, 40000};
    for (DWORD n : ns) {
        for (DWORD i = 0; i < n; i++) a[i] = b[i] = ((u64)(rnd() % 3000 * 7919) << 32) | (rnd() & 0xFF);
        qsort(b, n, 8, SpCmpU64);
        SpSortU64(a, n);
        assert(!n || !memcmp(a, b, n * 8));
    }
    LARGE_INTEGER t0, t1;
    static u8 src[1 << 20], dst[1 << 20];
    for (DWORD i = 0; i < sizeof(src); i++) src[i] = (u8)rnd();
    volatile DWORD sink = 0;
    double hTile, cTile, hR, cR, qs, rs, gq, gm;
    QueryPerformanceCounter(&t0); for (int r = 0; r < 100; r++) sink += SpHash(src + r * 64, 40960); QueryPerformanceCounter(&t1); hTile = BenchUs(t0, t1) / 100;
    QueryPerformanceCounter(&t0); for (int r = 0; r < 100; r++) { memcpy(dst, src + r * 64, 40960); sink += dst[r]; } QueryPerformanceCounter(&t1); cTile = BenchUs(t0, t1) / 100;
    QueryPerformanceCounter(&t0); for (int r = 0; r < 10; r++) sink += SpHash(src, sizeof(src)); QueryPerformanceCounter(&t1); hR = BenchUs(t0, t1) / 10;
    QueryPerformanceCounter(&t0); for (int r = 0; r < 10; r++) { memcpy(dst, src, sizeof(src)); sink += dst[r]; } QueryPerformanceCounter(&t1); cR = BenchUs(t0, t1) / 10;
    const DWORD nb = 4096;
    for (DWORD i = 0; i < nb; i++) a[i] = ((u64)(rnd() % 100000) << 32) | (rnd() & 0xFF);
    QueryPerformanceCounter(&t0); for (int r = 0; r < 20; r++) { memcpy(b, a, nb * 8); qsort(b, nb, 8, SpCmpU64); } QueryPerformanceCounter(&t1); qs = BenchUs(t0, t1) / 20;
    QueryPerformanceCounter(&t0); for (int r = 0; r < 20; r++) { memcpy(b, a, nb * 8); SpSortU64(b, nb); } QueryPerformanceCounter(&t1); rs = BenchUs(t0, t1) / 20;
    // grid: 800 rows over 40 lots (IP image 45 KB = 820 rows), 4 dirty lots re-read per cut; the merge path through SpBuildGrid
    // needs a live grid, so the two sorts are timed on the rows directly
    const DWORD nr = 800;
    static SpRow rows[nr], work[nr];
    for (DWORD i = 0; i < nr; i++) { memset(&rows[i], 0, sizeof(SpRow)); rows[i].lotLo = rnd() % 40; rows[i].cell = rnd() % 64; rows[i].keyLo = rnd(); }
    qsort(rows, nr, sizeof(SpRow), SpCmpRow);
    QueryPerformanceCounter(&t0); for (int r = 0; r < 20; r++) { memcpy(work, rows, sizeof(rows)); qsort(work, nr, sizeof(SpRow), SpCmpRow); } QueryPerformanceCounter(&t1); gq = BenchUs(t0, t1) / 20;
    QueryPerformanceCounter(&t0);
    for (int r = 0; r < 20; r++) {  // as SpBuildGrid: the ~10% fresh rows sorted, then merged with the kept rows
        DWORD nf = 0, nk = 0;
        static SpRow fresh[nr], kept[nr];
        for (DWORD i = 0; i < nr; i++) (rows[i].lotLo < 4 ? fresh[nf++] : kept[nk++]) = rows[i];
        qsort(fresh, nf, sizeof(SpRow), SpCmpRow);
        DWORD j = 0, o = 0;
        for (DWORD i = 0; i < nk; i++) { for (; j < nf && SpCmpRow(&fresh[j], &kept[i]) < 0; j++) work[o++] = fresh[j]; work[o++] = kept[i]; }
        while (j < nf) work[o++] = fresh[j++];
        assert(o == nr && !memcmp(work, rows, sizeof(rows)));
    }
    QueryPerformanceCounter(&t1);
    gm = BenchUs(t0, t1) / 20;
    printf("snap cut cost: tile 40 KB hash %.1f us (copy %.1f), R 1 MB hash %.0f us (copy %.0f), bnm 4096 sort qsort %.0f us radix %.0f, "
           "grid 800 rows sort %.0f us merge %.0f (%lu)\n", hTile, cTile, hR, cR, qs, rs, gq, gm, (DWORD)sink & 1);
}

// 0.35.23 (docs/94 §22.26.5): a bnm mark with an unchanged blockedNodeMap shares the old image (no sort); any count change,
// insert or erase builds a new one equal to a fresh SpBuildBnm; a re-chained but equal map rebuilds and still shares by
// content. Bench: walk + compare vs walk + sort + image at 4,096 and 16,384 entries
static void TestBnmShare() {
    SfSetup(21);
    g_spLot = &kSpLotNone;
    u8* g = g_sf.world + 0x8C;
    DWORD* bk = *(DWORD**)(g + 0x360);
    u8* nodes[40];
    for (int i = 0; i < 40; i++) {  // 40 cells, counts 1-3, chained in 5 buckets
        u8* nd = nodes[i] = (u8*)SfA(0x10);
        *(DWORD*)nd = 1000 + i * 37, nd[4] = (u8)(1 + i % 3);
        const DWORD b = *(DWORD*)nd % 5;
        *(DWORD*)(nd + 8) = bk[b], bk[b] = (DWORD)(uintptr_t)nd;
    }
    auto mark = [] { SpLockD(); g_spD.flags |= SPD_BNM; SpUnlockD(); };
    auto fresh = [&] { SpImg* f = SpBuildBnm(g); const bool eq = SpImgEq(f, g_spCur->w[SP_BNM]); SpImgRel(f); return eq; };
    assert(SpCut(g_sf.world) && fresh());
    const SpImg* i0 = g_spCur->w[SP_BNM];
    const LONG same0 = g_spSplit[SP_BNM].same;
    mark();  // a grid mark that reached no bnm writer
    assert(SpCut(g_sf.world) && g_spCur->w[SP_BNM] == i0 && g_spSplit[SP_BNM].same == same0 + 1 && fresh());
    nodes[7][4]++;  // +1 on an existing cell (0x66FB40)
    mark();
    assert(SpCut(g_sf.world) && g_spCur->w[SP_BNM] != i0 && fresh());
    const SpImg* i1 = g_spCur->w[SP_BNM];
    mark();
    assert(SpCut(g_sf.world) && g_spCur->w[SP_BNM] == i1);
    {  // erase (0x66F6D0 at 0): unlink nodes[11] from its chain
        DWORD* at = &bk[*(DWORD*)nodes[11] % 5];
        while (*at != (DWORD)(uintptr_t)nodes[11]) at = (DWORD*)(*at + 8);
        *at = *(DWORD*)(nodes[11] + 8);
    }
    mark();
    assert(SpCut(g_sf.world) && g_spCur->w[SP_BNM] != i1 && fresh());
    const SpImg* i2 = g_spCur->w[SP_BNM];
    {  // same pairs, another chain order (a rehash): rebuilt, equal content, the old image shared by put()
        const DWORD b = *(DWORD*)nodes[0] % 5;
        u8* h = (u8*)(uintptr_t)bk[b];
        u8* n2 = (u8*)(uintptr_t)*(DWORD*)(h + 8);
        assert(n2);
        *(DWORD*)(h + 8) = *(DWORD*)(n2 + 8), *(DWORD*)(n2 + 8) = (DWORD)(uintptr_t)h, bk[b] = (DWORD)(uintptr_t)n2;
    }
    const LONG same1 = g_spSplit[SP_BNM].same;
    mark();
    assert(SpCut(g_sf.world) && g_spCur->w[SP_BNM] == i2 && g_spSplit[SP_BNM].same == same1 + 1 && fresh());
    mark();  // and the new order is the kept walk now
    assert(SpCut(g_sf.world) && g_spCur->w[SP_BNM] == i2 && fresh());
    // negative control: the share needs the kept walk to be the old image's; with it cleared every mark rebuilds
    g_spBnmFor = NULL;
    nodes[3][4]++;
    mark();
    assert(SpCut(g_sf.world) && g_spCur->w[SP_BNM] != i2 && fresh());
    SpAudit(g_spCur, g_sf.world);
    SfTeardown();
    // bench (this machine): the same-case cost before (walk + sort + image + compare) and now (walk + compare)
    static u8 pool[16384][16];
    static DWORD bb[4096];
    static const DWORD kBn[] = {4096, 16384};
    for (DWORD n : kBn) {
        u8 gg[0x400] = {};
        memset(bb, 0, sizeof(bb));
        for (DWORD i = 0; i < n; i++) {
            u8* nd = pool[i];
            *(DWORD*)nd = i * 2654435761u >> 8, nd[4] = (u8)(1 + i % 5);
            const DWORD b = *(DWORD*)nd % 4096;
            *(DWORD*)(nd + 8) = bb[b], bb[b] = (DWORD)(uintptr_t)nd;
        }
        *(DWORD**)(gg + 0x360) = bb, *(DWORD*)(gg + 0x364) = 4096;
        LARGE_INTEGER t0, t1;
        SpImg* ref = SpBuildBnm(gg);
        QueryPerformanceCounter(&t0);
        for (int r = 0; r < 50; r++) { SpImg* f = SpBuildBnm(gg); assert(SpImgEq(f, ref)); SpImgRel(f); }
        QueryPerformanceCounter(&t1);
        const double before = BenchUs(t0, t1) / 50;
        g_spBnmFor = NULL;
        bool same;
        SpImg* f0 = SpBnmCut(gg, NULL, &same);
        g_spBnmFor = f0;
        QueryPerformanceCounter(&t0);
        for (int r = 0; r < 50; r++) { assert(!SpBnmCut(gg, f0, &same) && same); }
        QueryPerformanceCounter(&t1);
        const double now = BenchUs(t0, t1) / 50;
        printf("bnm share: %lu entries, unchanged build %.1f us before, %.1f us now\n", n, before, now);
        SpImgRel(f0), SpImgRel(ref);
        g_spBnmFor = NULL;
    }
}

static void TestSnapImages() {
    Ts3nCounters& k = ts3native_counters;
    static DWORD simTid;
    const DWORD* savedSim = g_rtSimTidSlot;
    simTid = GetCurrentThreadId();
    g_rtSimTidSlot = &simTid;
    long cuts = 0, writes = 0;
    for (DWORD seed = 1; seed <= 3; seed++) SnapK1(seed, 300, &cuts, &writes);
    SnapK2(7);
    SnapK5(9);
    {  // lazy Gtiles: reader every 8 cuts builds fewer tiles than eager cuts; reader every cut builds exactly the dirty ones
        long r = 0, b = 0, e = 0, r1 = 0, b1 = 0, e1 = 0;
        SnapLazy(5, 400, 8, &r, &b, &e);
        SnapLazy(5, 400, 1, &r1, &b1, &e1);
        assert(r == 50 && b < e && r1 == 400 && b1 == e1 && e1 == e);
    }
    // live default strategy "none": world images and their audit only, no lot images and no lot audits
    SfSetup(13);
    g_spLot = &kSpLotNone;
    const LONG la0 = k.spAudits[SP_LOTS], m0 = SfMisSum();
    for (int i = 0; i < 200; i++) {
        SfStep();
        if (i % 7 == 0) { assert(SpCut(g_sf.world)); SpAudit(g_spCur, g_sf.world); }
    }
    assert(g_spCur->nLots == 0 && k.spAudits[SP_LOTS] == la0 && SfMisSum() == m0 && k.spAudits[SP_GHC] > 0);
    SfTeardown();
    // tile rule: a polygon from (10, 10) to (300, 20) marks tiles x 0-1, z 0 only; its remove marks them again
    memset(&g_spD, 0, sizeof(g_spD));
    SpKeysClear();
    g_spOn = 1;
    float pts[6] = {10, 10, 300, 20, 150, 12};
    u8 vec[12];
    *(float**)vec = pts, *(float**)(vec + 4) = pts + 6;
    DWORD* s = SfSlot();
    s[1] = 0x77, s[3] = 0x88, s[5] = (DWORD)(uintptr_t)vec;
    SnEnter(22, s);
    const DWORD tl = kSpTile == 64 ? 3u : 7u;  // cells 1-76 x 1-6: 64-cell tiles (0,0), (1,0); 32-cell (0,0), (1,0), (2,0)
    assert(g_spD.tiles[0] == tl && !(g_spD.flags & SPD_TILES));
    memset(g_spD.tiles, 0, sizeof(g_spD.tiles));
    SnEnter(23, s);
    assert(g_spD.tiles[0] == tl);  // the key is erased with it (a second remove of it marks every tile)
    s[1] = 0x99;  // a key never stamped: every tile
    SnEnter(23, s);
    assert(g_spD.flags & SPD_TILES);
    memset(&g_spD, 0, sizeof(g_spD));
    // world events: 7 moves the epoch, 0x0E re-images lots and grid
    s = SfSlot();
    s[2] = 7;
    SpNote(SN_WEV, s);  // P1's own handling of event 7 registers object nodes through the game (not here)
    assert(g_spD.flags & SPD_EPOCH);
    s[2] = 0x0E;
    SpNote(SN_WEV, s);
    assert(g_spD.flags & SPD_LOTS && g_spD.flags & SPD_GRID);
    memset(&g_spD, 0, sizeof(g_spD));
    // SpStart: rate limit, async job busy -> skipped (no lock taken)
    SfSetup(11);
    g_spCutMs = 100000;
    g_spLastCut = 0;
    const LONG c0 = k.spCuts;
    SpStart();
    SpStart();  // inside CutMs
    assert(k.spCuts == c0 + 1);
    g_spLastCut = 0;
    const LONG st = g_async.state;
    g_async.state = A_RUNNING;
    SpStart();
    g_async.state = st;
    assert(k.spCutSkip[0] == 1 && k.spCuts == c0 + 1 && *(LONG*)(g_sf.world + 0x918 + 0x20) == 0);
    SfTeardown();
    // door channel (GetOption2, optHi = 0x84000000 | code << 16): lot 0x300000123, two doors, the second with an owner list
    auto msg = [](DWORD code, DWORD v) { return SpDoorMsg(v, 0x84000000 | code << 16); };
    g_spEpoch = 5;
    assert(msg(SDM_Q_LO, 0x123) && !msg(SDM_Q_HI, 3));
    assert(msg(SDM_LOT_LO, 0x123) && msg(SDM_LOT_HI, 3));
    assert(msg(SDM_OBJ_LO, 0xA1) && msg(SDM_OBJ_HI, 0) && msg(SDM_KIND, 1) && msg(SDM_LOCK, 2) && msg(SDM_ROOM_A, 4) && msg(SDM_ROOM_B, 5));
    assert(msg(SDM_OBJ_LO, 0xA2) && msg(SDM_OBJ_HI, 0) && msg(SDM_KIND, 9) && msg(SDM_LOCK, 6) && msg(SDM_OWNER_LO, 0x55) && msg(SDM_OWNER_HI, 0));
    assert(msg(SDM_HH_LO, 0x66) && msg(SDM_HH_HI, 0) && msg(SDM_ONEWAY, 36863) && msg(SDM_LIST_LO, 0x71) && msg(SDM_LIST_HI, 0));
    assert(msg(SDM_LIST_LO, 0x72) && msg(SDM_LIST_HI, 1) && msg(SDM_EPOCH, 42) && msg(SDM_END, 2));
    const u64 lot = 0x300000123ull;
    const SpDoor* d = SpDoorFind(lot, 0xA2);
    assert(d && d->kind == 9 && d->lock == 6 && d->owner == 0x55 && d->household == 0x66 && d->oneWay == 36863 && d->listN == 2);
    const SpDoorLot* dl = SpDoorLotFor(lot, false);
    assert(dl->ids[d->listOff] == 0x71 && dl->ids[d->listOff + 1] == 0x100000072ull && dl->mEpoch == 42);
    assert(SpDoorFind(lot, 0xA1)->roomB == 5 && !SpDoorFind(lot, 0xA3));  // no record: unknown, guard path (I-105-32)
    assert(msg(SDM_Q_LO, 0x123) && msg(SDM_Q_HI, 3));
    g_spEpoch++;  // a world epoch: the lot is stale until refilled
    assert(!SpDoorFind(lot, 0xA2) && !msg(SDM_Q_HI, 3));
    g_spEpoch--;
    // count mismatch at END: the lot stays invalid; a field outside BEGIN..END is a protocol error
    const LONG e0 = k.spDoor[4];
    assert(msg(SDM_LOT_LO, 0x123) && msg(SDM_LOT_HI, 3) && msg(SDM_OBJ_LO, 1) && msg(SDM_OBJ_HI, 0) && !msg(SDM_END, 2));
    assert(!SpDoorFind(lot, 1) && !msg(SDM_KIND, 1) && k.spDoor[4] == e0 + 2);
    assert(msg(SDM_LOT_LO, 0x123) && msg(SDM_LOT_HI, 3) && msg(SDM_END, 0) && msg(SDM_INV_LO, 0x123) && msg(SDM_INV_HI, 3));
    assert(!msg(SDM_Q_HI, 3) && k.spDoor[3] >= 1);
    // through the GetOption hook (GetOption2 only; GetOption with the same bits is not ours)
    assert(GetOptionHook(0, 0, 0x84000000 | SDM_INV_ALL << 16, 1) == 1);
    printf("snap: K1 %ld cuts over %ld writes (3 seeds), audit 0 mismatches and incremental == full at each; negative control "
           "found; K2 lazy state not imaged; K5 held version kept its images, references back to 0; lazy Gtiles (built only for a reader, carried == live); lot strategy none: world audit only; door channel ok\n", cuts, writes);
    g_spEpoch = 0;
    g_spDoorN = 0;
    g_spWorldSlot = (const u8* const*)(uintptr_t)0x011D9128;
    g_spWmSlot = (const u8* const*)(uintptr_t)0x011ECBC4;
    g_spAcq = (LockAcquireFn)(uintptr_t)0x004E16F0;
    g_spRel = (SpRelFn)(uintptr_t)0x004E17B0;
    g_rtSimTidSlot = savedSim;
    memset(&k.spReady, 0, sizeof(Ts3nCounters) - offsetof(Ts3nCounters, spReady));
}

// --- [RouteSnap] P1b (routesnap.cpp, docs/94 §15): frames, drain, cache fills, caller keys, records, clone and lot copy ---
static ULONGLONG g_snFakeMs;
static ULONGLONG WINAPI SnFakeNow() { return g_snFakeMs; }
static __declspec(noinline) DWORD SnRetHere() { return (DWORD)(uintptr_t)_ReturnAddress(); }

static void TestRouteSnapP1b() {
    Ts3nCounters& k = ts3native_counters;
    static DWORD simTid;
    const DWORD* savedSim = g_rtSimTidSlot;
    simTid = GetCurrentThreadId();
    g_rtSimTidSlot = &simTid;
    memset(&k.snReady, 0, sizeof(Ts3nCounters) - offsetof(Ts3nCounters, snReady));
    SnTextInit();  // this test's own code section
    t_sn.n = 0;
    t_sn.objDepth = 0;
    DWORD stk[128] = {};

    // each P1b frame at our own stub: a leaf inside it counts for that source; the leave restores the return address
    for (int i = SN_F0; i < SN_N; i++) {
        g_snJmp[i] = (void*)1;
        stk[40] = 0x9000 + i;
        SnEnter(i, &stk[40]);
        assert(t_sn.n == 1 && stk[40] == (DWORD)(uintptr_t)SnLeaveThunk && k.snFrameCalls[i - SN_F0] == 1);
        SnEnter(13, &stk[20]);  // UpdateObjectFootprintCore
        assert(k.snSrc2[SL_FPCORE][kSn[i].arg - SRC_N] == 1 && SnLeave(&stk[40]) == 0x9000u + i && t_sn.n == 0);
        g_snJmp[i] = NULL;
    }
    assert(k.snUnattributed == 0);
    // a site another stub owns (no trampoline of ours): counted, no frame
    stk[40] = 0x9999;
    SnEnter(SN_F0 + 5, &stk[40]);
    assert(t_sn.n == 0 && stk[40] == 0x9999 && k.snFrameCalls[5] == 2);
    // design D drain: wins over an open IC frame
    g_discDraining = true;
    SnIcEnter(4, &stk[40]);
    SnEnter(13, &stk[20]);
    assert(k.snSrc2[SL_FPCORE][SRC_DRAIN - SRC_N] == 1 && k.snIcWrites[4] == 0 && SnLeave(&stk[40]) == 0x9999);
    g_discDraining = false;
    // W6 with no source: a cache fill, not unattributed
    SnEnter(4, &stk[20]);
    assert(k.snCacheFill == 1 && k.snSrc[SL_W6][SRC_NONE] == 1 && k.snUnattributed == 0);

    // caller table: leaf, return address and next frame (the first call-preceded code address above the slot)
    const DWORD here = SnRetHere();
    assert(SnIsRet(here) && !SnIsRet(0x00ABCDEF) && !SnIsRet(0));
    stk[20] = 0x00ABCDEF;
    stk[25] = here;
    SnEnter(15, &stk[20]);  // footprint remove 0x82A0C0
    SnEnter(15, &stk[20]);
    stk[25] = 0;
    SnEnter(15, &stk[20]);
    assert(k.snUnattributed == 3 && k.snUa2Key[0] == (LONG)(SL_FPREMOVE << 24 | 0x00ABCDEF) && k.snUa2Next[0] == (LONG)here &&
           k.snUa2N[0] == 2 && k.snUa2Key[1] == k.snUa2Key[0] && k.snUa2Next[1] == 0 && k.snUa2N[1] == 1);
    for (DWORD r = 0; r < 70; r++) { stk[20] = 0x500000 + r; SnEnter(15, &stk[20]); }
    assert(k.snUa2Other == 8 && k.snUa2N[63] == 1);
    stk[20] = 0x01234567;  // outside the exe range of the game: 0xFFFFFF
    memset(k.snUa2Key, 0, sizeof(k.snUa2Key) * 3);
    SnEnter(15, &stk[20]);
    assert(k.snUa2Key[0] == (LONG)(SL_FPREMOVE << 24 | 0x00FFFFFF));

    // design B records: kinds, 1-s buckets with empty seconds, other threads, rebase suppression, IC records
    g_snNowMs = SnFakeNow;
    g_snRecSec = 0;
    g_snFakeMs = 5000;
    static u8 fp[0x44], lv[0x24], poly[2][16], verts[2][24];
    *(DWORD*)fp = 0xFEBA98;  // a class with a polygon cache (replay.cpp kRpClsGame)
    lv[0x20] = 1;            // level valid
    *(u8**)(fp + 0x38) = lv;
    *(u8**)lv = poly[0];
    *(u8**)(lv + 4) = poly[0] + 32;
    for (int i = 0; i < 2; i++) { *(u8**)poly[i] = verts[i]; *(u8**)(poly[i] + 4) = verts[i] + 24; }
    stk[21] = (DWORD)(uintptr_t)fp;
    SnEnter(0, &stk[20]);  // W1 apply insert: a record and the clone-equivalent copy
    assert(k.snRec[RK_W1] == 1 && k.snCloneN == 1 && k.snCloneMaxBytes == 0x44 + 0x24 + 2 * 16 + 2 * 24 && k.snCloneFault == 0);
    for (int i = 0; i < 3; i++) SnEnter(1, &stk[20]);  // W2
    g_snFakeMs = 8200;
    SnEnter(2, &stk[20]);  // W3: closes second 5 (4 records), seconds 6 and 7 had none
    assert(k.snRec[RK_W2] == 3 && k.snRec[RK_W3] == 1 && k.snRecSec[1] == 1 && k.snRecSec[0] == 2 && k.snRecMaxSec == 4);
    SnEnter(13, &stk[20]);  // UpdateObjectFootprintCore: reaches W1/W2, not a record itself
    SnEnter(4, &stk[20]);   // W6: not a record
    LONG sum = 0;
    for (int i = 0; i < RK_N; i++) sum += k.snRec[i];
    assert(sum == 5);
    simTid = 0;  // another thread (not the route worker): a record, no clone
    SnEnter(0, &stk[20]);
    assert(k.snRec[RK_W1] == 2 && k.snCloneN == 1);
    simTid = GetCurrentThreadId();
    stk[40] = 0x7777;
    SnIcEnter(22, &stk[40]);  // OnExitBuildBuy: a rebase, its nested records are suppressed
    SnEnter(1, &stk[20]);
    assert(k.snRec[RK_SUPPRESSED] == 1 && k.snRec[RK_W2] == 3 && SnLeave(&stk[40]) == 0x7777);
    SnIcEnter(12, &stk[40]);  // SetPortalCost: an inline record from the slot shim
    SnLeave(&stk[40]);
    SnIcEnter(2, &stk[40]);   // FootprintRemoveLockKey
    SnLeave(&stk[40]);
    assert(k.snRec[RK_PORTAL] == 1 && k.snRec[RK_LOCKKEY] == 1);
    g_snFakeMs = 9000;
    SnEnter(5, &stk[20]);  // W6 only: no record, no bucket change
    assert(k.snRecSec[1] == 1);
    SnEnter(21, &stk[20]);  // grid remove: closes second 8
    assert(k.snRecSec[1] == 2 && k.snRec[RK_GRIDREMOVE] == 1);
    stk[21] = 0x10;  // unreadable footprint
    SnEnter(0, &stk[20]);
    assert(k.snCloneFault == 1 && k.snCloneN == 1 && k.snRecUs >= 0);
    {  // 0.31.4: the box class 0xFEAE08 keeps floats at +0x38 (-0.5f): not a cache pointer, no fault (0.31.2 faulted 11x a load)
        static u8 box[0x4C];
        *(DWORD*)box = 0xFEAE08;
        *(DWORD*)(box + 0x38) = 0xBF000000;
        static u8 kid[0x44];      // the 0x28-byte child class: +0x38 lies past the object
        *(DWORD*)kid = 0xFEBF90;
        *(DWORD*)(kid + 0x38) = 0xBF000000;
        static u8 stale[0x44];    // a cache class whose level 0 is invalid: its arrays are not read
        static u8 slv[0x24];
        *(DWORD*)stale = 0xFEBA98;
        *(u8**)(stale + 0x38) = slv;
        *(DWORD*)slv = 0xBF000000, *(DWORD*)(slv + 4) = 0xBF000100;
        const LONG n0 = k.snCloneN;
        u8* const probes[3] = {box, kid, stale};
        for (u8* f : probes) { stk[21] = (DWORD)(uintptr_t)f; SnEnter(0, &stk[20]); }
        assert(k.snCloneFault == 1 && k.snCloneN == n0 + 3);
    }
    g_snNowMs = GetTickCount64;

    // rebase copy: the lot of a sim LotGraph_Build, timed at the next START; a lot gone from the table is a fault
    SfSetup(13);
    g_snCopyPending = false;  // TestSnapImages' fake steps leave one
    DWORD* s = SfSlot();
    s[1] = (DWORD)(uintptr_t)(g_sf.nodes[2] + 0x10);
    SnEnter(12, s);  // W4 (counted here; with [RouteGate] on its stub calls SnEnter the same way)
    assert(g_snCopyPending && g_snCopyKey == 0x102);
    s = SfSlot();
    s[0] = 0x8888;
    SnIcEnter(SN_IC_START, s);
    assert(!g_snCopyPending && k.snLotCopyN == 1 && SnLeave(s) == 0x8888);
    LONG n = 0;
    for (int i = 0; i < 8; i++) n += k.snLotCopyUs[i];
    const LONG kbMax = k.spKBMax, faults = k.spFaults;
    g_snCopyPending = true;
    SnLotCopyProbe();
    assert(n == 1 && k.snLotCopyN == 2 && k.spKBMax == kbMax && k.spFaults == faults && k.snLotCopyMaxKB >= 0);
    g_snCopyKey = 0x999;
    SnLotCopyProbe();
    assert(k.snLotCopyFault == 1 && k.snLotCopyN == 2);
    SfTeardown();

    // live: a frame row on a dummy with the same prologue; the frame is popped by the leave thunk on return
    u8* code = (u8*)VirtualAlloc(NULL, 4096, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
    assert(code);
    static const u8 kFn[] = {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF0, 0xB8, 0x2A, 0x00, 0x00, 0x00, 0x8B, 0xE5, 0x5D, 0xC3};
    memcpy(code, kFn, sizeof(kFn));  // push ebp; mov ebp,esp; and esp,-16; mov eax,42; mov esp,ebp; pop ebp; ret
    assert(SnInstallAt(SN_F0, (DWORD)(uintptr_t)code, 0) == 1 && g_snJmp[SN_F0]);
    const LONG f0 = k.snFrameCalls[0];
    assert(((int(__cdecl*)())code)() == 42 && k.snFrameCalls[0] == f0 + 1 && t_sn.n == 0);
    g_snJmp[SN_F0] = NULL;
    VirtualFree(code, 0, MEM_RELEASE);

    printf("routesnap P1b: %d frames, drain, cache fill, 64-key caller table, %d record kinds with 1-s buckets and suppression, "
           "clone-equivalent copy, rebase lot copy ok\n", SN_N - SN_F0, (int)RK_N);
    g_spWorldSlot = (const u8* const*)(uintptr_t)0x011D9128;
    g_spWmSlot = (const u8* const*)(uintptr_t)0x011ECBC4;
    g_spAcq = (LockAcquireFn)(uintptr_t)0x004E16F0;
    g_spRel = (SpRelFn)(uintptr_t)0x004E17B0;
    g_rtSimTidSlot = savedSim;
    g_snRecSec = 0;
    t_sn.n = 0;
    memset(&k.snReady, 0, sizeof(Ts3nCounters) - offsetof(Ts3nCounters, snReady));
}

// [RouteSnap] Lots=replay (replay.cpp, docs/94 §11 B, §16): random writer sequences through P1's stubs on the fake world of
// replay-fake.h (random allocation addresses), replayed into the clone; at every cut the audit compares every lot's hash
// H20 (docs/109 §8.9): fake portals with the interlocked count pair (H20a) and a deleting slot that only counts
static volatile LONG g_stPortalDel;
static void __fastcall StPortalDel(u8*, void*, DWORD) { InterlockedIncrement(&g_stPortalDel); }
static DWORD g_stPortalVt[3];
static u8* StPortal() {
    u8* p = (u8*)RfAlloc(0x80);
    memset(p, 0, 0x80);
    *(DWORD*)p = (DWORD)(uintptr_t)g_stPortalVt;
    return p;
}
static void StPortalSetup(DWORD seed) { RfSetup(seed); RfCut(); }

static void TestSnapReplay() {
    Ts3nCounters& k = ts3native_counters;
    static DWORD simTid;
    const DWORD* savedSim = g_rtSimTidSlot;
    simTid = GetCurrentThreadId();
    g_rtSimTidSlot = &simTid;
    long cuts = 0, steps = 0;
    for (DWORD seed = 1; seed <= 4; seed++) {
        g_rpEpochHeap = seed >= 3;  // [RouteSnap] EpochHeap (docs/66 §7 A3) on seeds 3-4: same sequences, one heap per clone build
        const LONG gf0 = k.rpGenFreed;
        RfSetup(seed);
        if (g_rpEpochHeap) {  // the load wait (world event 9 / 0x0A seen, 7 not yet): the cut builds nothing
            const LONG w0 = k.rpLoadWaitCuts, e0 = k.rpEpochs;
            g_rpLoadWaitUntil = GetTickCount64() + kRpLoadWaitMs;
            RfCut();
            assert(!g_rpHave && k.rpEpochs == e0 && k.rpLoadWaitCuts == w0 + 1);
            g_rpLoadWaitUntil = 0;  // event 7
        }
        RfCut();  // first cut: the clone
        assert(k.rpEpochs >= 1 && g_rpClone && g_rpClone->nLots == g_rf.nLots && k.rpEpochFails == 0);
        if (seed == 3) {  // [RouteSnap] LotUseReadout (docs/66 §7.1): the walk sizes every lot, nothing noted = all untouched
            const LONG w0 = k.luWalks, f0 = k.rpFaults;
            g_luOn = true;
            LuWalk();
            DWORD fpB = 0, treeB = 0;
            for (int i = 0; i < g_luNB; i++) fpB += g_luB[i].b[0], treeB += g_luB[i].b[1];
            assert(k.luWalks == w0 + 1 && k.rpFaults == f0 && g_luNB == g_rpClone->nLots && treeB > 0 && fpB > 0);
            assert(k.luLots == g_luNB && k.luTouched == 0 && k.luUntouchedPermille == 1000);
            t_stlN = 1, t_stlOver = false, t_stlKeys[0] = g_rpClone->lots[0].key;
            LuNoteRun(7), LuNoteRun(7), LuNoteRun(9);
            assert(k.luRuns >= 3 && LuTouchOf(t_stlKeys[0])->runs == 3 && LuTouchOf(t_stlKeys[0])->nAg == 2);
            LuReport("selftest", 0);
            assert(k.luTouched == 1 && k.luUntouchedPermille < 1000);
            g_luOn = false, g_luNT = 0, t_stlN = 0;
        }
        const LONG mis0 = k.rpMis, dv0 = k.rpDiverge, ep0 = k.rpEpochs, rd0 = g_rpRemDiff;
        for (int i = 0; i < 600; i++) {
            RfStep();
            steps++;
            if (RfRnd(5) == 0) {
                RfCut();
                cuts++;
                if (k.rpMis != mis0 || k.rpFaults) {
                    printf("replay seed %lu step %d: audit mismatch %ld faults %ld diverge %ld epochs %ld\n", seed, i, k.rpMis - mis0, k.rpFaults, k.rpDiverge - dv0, k.rpEpochs - ep0);
                    fflush(stdout);
                }
                assert(k.rpMis == mis0 && k.rpFaults == 0);
            }
        }
        RfCut();
        assert(k.rpMis == mis0 && g_rpTail == g_rpPub && k.rpDiverge == dv0 && k.rpEpochs == ep0);  // no rebuild needed either
        {  // 0.35.17: a lot graph build (W4) is a shell refresh: no batch walks the map, every lot audit stays equal
            const LONG sh0 = g_rpShellLots, mb0 = g_rpMapBatches, reb0 = k.rpRebaseLots, miss0 = g_rpShellMiss;
            for (int j = 0; j < 40; j++) {
                RfW4(j % g_rf.nLots);
                if (j & 1) RfW4((j + 1) % g_rf.nLots);  // two lots in one cut
                RfCut();
            }
            if (g_rpMapBatches != mb0 || k.rpMis != mis0 || g_rpShellMiss != miss0)
                printf("replay seed %lu: W4 shells %ld, map batches +%ld, mismatches +%ld, shells not taken +%ld\n", seed,
                       g_rpShellLots - sh0, g_rpMapBatches - mb0, k.rpMis - mis0, g_rpShellMiss - miss0);
            assert(g_rpShellLots >= sh0 + 60 && g_rpMapBatches == mb0 && k.rpRebaseLots >= reb0 + 60 && k.rpMis == mis0 &&
                   g_rpShellMiss == miss0 && k.rpDiverge == dv0 && k.rpEpochs == ep0);
            // a W4 whose lot also got a new Q (W3 build) is a full rebase: the map is walked
            RfW4(0);
            u8* g = g_rf.lots[0] + 0x10;
            u8** qv = *(u8***)(g + 0x74);
            RfRemAll(qv[0]);
            DWORD* s = RfSlot();
            s[1] = *(DWORD*)g, s[2] = 0, s[3] = 0, s[4] = 1;
            qv[0] = NULL;
            SnEnter(2, s);
            qv[0] = RfQ(*(DWORD*)g);
            RfIns(qv[0], g_rf.fps[0], 0, true);
            RfCut();
            assert(g_rpMapBatches == mb0 + 1 && k.rpMis == mis0 && k.rpDiverge == dv0);
        }
        {  // 0.35.14: a replayed remove whose clone copy answers flat bounds (the game keeps the leaves) forces its lot
            u8* q = RfQOf(0, 0);
            u8* fp = g_rf.fps[1];
            RfIns(q, fp, 0, true);
            RfCut();
            const LONG flat0 = g_rpRemFlat;
            g_rfFlatId = *(DWORD*)(fp + 0x14);
            RfRem(q, fp, true);
            RfCut();
            g_rfFlatId = 0;
            assert(*(DWORD*)(fp + 0x14) == 0 || g_rpRemFlat > flat0);
            RfCut();
            assert(k.rpMis == mis0 && k.rpFaults == 0);
            // 0.35.16: an FME inserted flat (no 0x200: no leaves on either side) is not a trigger. 0.35.14 forced its lot at
            // every such remove (BP 15k rebased lots): 50 insert / remove pairs now rebase nothing
            u8* q2 = RfQOf(0, 1);
            u8* fp2 = g_rf.fps[2];
            const LONG flat1 = g_rpRemFlat, reb0 = k.rpRebaseLots;
            for (int j = 0; j < 50; j++) {
                RfRem(q2, fp2, true);
                RfCut();  // replayed before the bounds turn flat (the replay asks +0x2C when it applies)
                g_rfFlatId = *(DWORD*)(fp2 + 0x14);
                RfIns(q2, fp2, 0, true);
                RfCut();
                RfRem(q2, fp2, true);
                g_rfFlatId = 0;
                RfCut();
            }
            if (g_rpRemFlat != flat1 || k.rpRebaseLots != reb0)
                printf("replay seed %lu: flat-inserted removes forced %ld, rebased lots +%ld\n", seed, g_rpRemFlat - flat1, k.rpRebaseLots - reb0);
            assert(g_rpRemFlat == flat1 && k.rpRebaseLots == reb0 && k.rpMis == mis0);
            assert(g_rpRemDiff == rd0);  // equal live and copy bounds over every remove so far: no force from the 0.35.18 check
        }
        // 0.35.18 (I-94-478): the IP lot 0x08F300536B484DF0 Q 1 shape. The clone copy of a 3-level footprint answers bounds
        // outside the Q's rect while live's reach it: the stand-in remove (as 0x666010) keeps the copy's 3 leaf entries,
        // the map erase agrees. The REM record's live bounds differ from the copy's: logged, the lot forced, equal again.
        // 0.36.2 (I-94-508, the IP 0.35.24 / 0.35.25 clone-run faults): those kept entries name FMEs no map node holds (the
        // game frees them; the stand-in pins them). Pass 0 = negative control (cover off): 3 orphans; pass 1: the remove runs
        // with the cover answer and leaves none
        for (int pass = 0; pass < 2; pass++) {
            g_rpRemCoverOff = pass == 0;
            const LONG misP = k.rpMis;
            u8* q = RfQOf(0, 0);
            u8* fp = NULL;
            for (int j = 3; j < g_rf.nFps && !fp; j++)
                if (*(DWORD*)g_rf.fps[j] == g_rfCls[2].vt) fp = g_rf.fps[j];
            assert(fp);
            RfRem(q, fp, true);
            RfCut();
            RfIns(q, fp, 0, true);
            RfCut();
            const DWORD* cp = RpTabFind(g_rpClone->fps, (DWORD)(uintptr_t)fp);
            assert(cp);
            u8* cq = NULL;
            assert(RpFindQ(g_rpClone, (DWORD)(uintptr_t)q, &cq) && cq);
            DWORD e0 = 0, c0 = 0;
            RpTreeHash(0, RpP(q + 0x24), 0, &e0), RpTreeHash(0, RpP(cq + 0x24), 0, &c0);
            assert(e0 == c0 && RfOrphans(g_rpClone->m, cq) == 0);
            const LONG d0 = g_rpRemDiff, reb0 = k.rpRebaseLots, cov0 = g_rpRemCover;
            g_rfFarFp = (const u8*)(uintptr_t)*cp;
            RfRem(q, fp, true);
            RfCut();  // the remove replayed (cover off: the clone keeps the copy's entries until the forced rebase)
            g_rfFarFp = NULL;
            DWORD e1 = 0, c1 = 0;
            RpFindQ(g_rpClone, (DWORD)(uintptr_t)q, &cq);
            RpTreeHash(0, RpP(q + 0x24), 0, &e1), RpTreeHash(0, RpP(cq + 0x24), 0, &c1);
            const int orph = RfOrphans(g_rpClone->m, cq), want = pass ? 0 : 3;
            if (c1 != e1 + want || orph != want || g_rpRemDiff != d0 + 1 || g_rpRemCover != cov0 + pass)
                printf("replay seed %lu pass %d: far copy: live %lu clone %lu entries, orphans %d, diffs +%ld, covered +%ld\n", seed, pass, e1, c1,
                       orph, g_rpRemDiff - d0, g_rpRemCover - cov0);
            assert(c1 == e1 + want && orph == want && g_rpRemDiff == d0 + 1 && g_rpRemCover == cov0 + pass);  // one difference forces the lot
            RfCut();  // a lot audit in this window may still see the 3 entries (counted once); the force lands here
            RfCut();
            const LONG misW = k.rpMis;
            RfCut();
            RfCut();
            DWORD e2 = 0, c2 = 0;
            RpFindQ(g_rpClone, (DWORD)(uintptr_t)q, &cq);
            RpTreeHash(0, RpP(q + 0x24), 0, &e2), RpTreeHash(0, RpP(cq + 0x24), 0, &c2);
            const int orph2 = RfOrphans(g_rpClone->m, cq);
            if (c2 != e2 || k.rpRebaseLots <= reb0 || k.rpMis != misW || misW > misP + 1 || k.rpFaults || orph2)
                printf("replay seed %lu pass %d: after the forced rebase live %lu clone %lu entries, orphans %d, rebased +%ld, mis +%ld (+%ld after), faults %ld\n",
                       seed, pass, e2, c2, orph2, k.rpRebaseLots - reb0, misW - misP, k.rpMis - misW, k.rpFaults);
            assert(c2 == e2 && k.rpRebaseLots > reb0 && k.rpMis == misW && misW <= misP + (pass ? 0 : 1) && k.rpFaults == 0 && !orph2);
        }
        g_rpRemCoverOff = false;
        g_rpDiffN = 0;
        const LONG only0 = g_rpDiffOnly[0], forced0 = g_rpAuditForced, reb0a = g_rpAudFReb, rep0a = g_rpAudFRep;
        const LONG misN = k.rpMis;  // the 0.35.18 block may have counted one window mismatch (its negative-control pass)
        RfIns(RfQOf(0, 0), g_rf.fps[0], 1, false);  // negative control: an insert with no record
        RfCut();
        assert(k.rpMis > misN && g_rpAuditForced > forced0);
        // docs/157 §7: the mismatch carries its ring position and the lot's last equal audit before it (the drift window)
        if (!(g_rpMisSeq && g_rpMisOkSeq && g_rpMisOkSeq < g_rpMisSeq)) printf("replay seed %lu: mismatch ring %lu, last equal %lu\n", seed, g_rpMisSeq, g_rpMisOkSeq);
        assert(g_rpMisSeq && g_rpMisOkSeq && g_rpMisOkSeq < g_rpMisSeq);
        // 0.35.13: the mismatched lot is named (the next hold lists the live-only entry) and rebased at the next cut: equal again
        const LONG mis1 = k.rpMis;
        RfCut();
        RfCut();
        if (k.rpMis != mis1 || g_rpDiffOnly[0] <= only0) printf("replay seed %lu: after the forced rebase mis +%ld, live-only %ld\n", seed, k.rpMis - mis1, g_rpDiffOnly[0] - only0);
        assert(k.rpMis == mis1 && g_rpDiffOnly[0] > only0);
        if (!(g_rpAudFReb > reb0a && g_rpAudFRep > rep0a)) printf("audf reb %ld->%ld rep %ld->%ld n %d st %d\n", reb0a, g_rpAudFReb, rep0a, g_rpAudFRep, g_rpNAudF, g_rpNAudF ? g_rpAudFSt[0] : -1);
        assert(g_rpAudFReb > reb0a && g_rpAudFRep > rep0a);  // 0.36.55: "rebased at cut" then "equal again" for the forced lot
        if (seed == 1) {  // 0.35.13: an EPOCH's old clone goes to the grave, freed by later holds (none while a P4 hold is armed)
            RP_NEED_EPOCH();
            RpCut(g_rf.world);
            g_rpHoldAt = g_rpHead, g_rpHoldUntil = GetTickCount64() + 60000;
            RpDrain(false);
            const bool busy = RpGraveBusy();
            g_rpHoldAt = 0;
            RpDrain(false);
            assert(busy && !RpGraveBusy() && !g_rpGrave.lots);
            RfCut();
            assert(k.rpMis == mis1);
        }
        if (seed == 1) {  // docs/157 §10, AuditHoldRebase: the lot's rebase waits for its diff, at most one cut
            // the drift: the 0.35.18 far-copy remove with the cover off (the clone keeps 3 leaf entries, maps equal: no epoch)
            g_rpAuditHold = 1, g_rpRemCoverOff = true;
            u8* q = RfQOf(0, 0);
            u8* fp = NULL;
            for (int j = 3; j < g_rf.nFps && !fp; j++)
                if (*(DWORD*)g_rf.fps[j] == g_rfCls[2].vt) fp = g_rf.fps[j];
            assert(fp);
            const LONG h0[3] = {k.rpHold[0], k.rpHold[1], k.rpHold[2]}, onlyC = g_rpDiffOnly[1], epH = k.rpEpochs;
            for (int pass = 0; pass < 2; pass++) {  // 0: the diff comes (released by it), 1: it does not (timeout)
                g_rpDiffN = 0, g_rpHoldKey = g_rpHoldLast = 0;
                RfRem(q, fp, true);
                RfCut();
                RfIns(q, fp, 0, true);
                RfCut();
                const DWORD* cp = RpTabFind(g_rpClone->fps, (DWORD)(uintptr_t)fp);
                assert(cp);
                g_rfFarFp = (const u8*)(uintptr_t)*cp;
                RfRem(q, fp, true);
                RfCut();  // replayed: the clone keeps 3 entries; the audit mismatches, the diff is pending, the lot held
                g_rfFarFp = NULL;
                assert(k.rpHold[0] == h0[0] + 1 + pass && g_rpHoldKey);
                const u64 lot = g_rpHoldKey;
                const LONG reb0 = k.rpRebaseLots, onlyP = g_rpDiffOnly[1];
                RpCut(g_rf.world);  // the consumer is behind (I-157-11's order): this cut skips the lot, its force stays
                int j = 0;
                while (j < g_rpNForce && g_rpForce[j] != lot) j++;
                if (g_rpHoldKey != lot || j == g_rpNForce || k.rpRebaseLots != reb0)
                    printf("replay hold pass %d: key %d, force %d of %d, rebased +%ld, epochs +%ld\n", pass, g_rpHoldKey == lot, j, g_rpNForce,
                           k.rpRebaseLots - reb0, k.rpEpochs - epH);
                assert(g_rpHoldKey == lot && j < g_rpNForce && k.rpRebaseLots == reb0);
                if (pass == 0) {
                    RpDrain(false);  // the diff runs on the drifted clone (names the 3 entries), then the hold is released
                    if (g_rpDiffOnly[1] < onlyP + 3 || k.rpHold[1] != h0[1] + 1 || g_rpHoldKey)
                        printf("replay hold: clone-only +%ld, released %ld, key %d\n", g_rpDiffOnly[1] - onlyP, k.rpHold[1] - h0[1], g_rpHoldKey != 0);
                    assert(g_rpDiffOnly[1] >= onlyP + 3 && k.rpHold[1] == h0[1] + 1 && !g_rpHoldKey);
                } else {
                    RpCut(g_rf.world);  // no diff yet: the second cut releases the hold and rebases the lot
                    assert(!g_rpHoldKey && k.rpRebaseLots > reb0 && k.rpHold[2] == h0[2] + 1);
                    RpDrain(false);
                    assert(k.rpHold[1] == h0[1] + 1);  // the late diff found no hold to release
                }
                RfCut();  // rebased now (the held cut's repeat mismatch is not held again)
                RfCut();
                const LONG misH = k.rpMis;
                RfCut();
                RfCut();
                assert(k.rpMis == misH && k.rpFaults == 0 && !g_rpHoldKey && k.rpRebaseLots > reb0 && k.rpEpochs == epH);
            }
            g_rpRemCoverOff = false, g_rpAuditHold = 0, g_rpHoldLast = 0;
            printf("replay AuditHoldRebase: held 2, released by the diff 1 (clone-only +%ld named before the rebase), by timeout 1, equal after\n",
                   g_rpDiffOnly[1] - onlyC);
        }
        RfTeardown();
        assert(g_rpBytes == 0 && k.rpFps == 0);
        assert(k.rpGenLive == 0 && (!g_rpEpochHeap || k.rpGenFreed > gf0));  // EpochHeap: every build heap destroyed with its last block
        g_rpEpochHeap = false;
    }
    printf("replay totals: ins %ld rem applied %ld rebased %ld gc %ld stale %ld skips %ld/%ld audits %ld ensure %ld syncs %ld\n", k.rpRec[RP_INS], k.rpApplied[RP_REM], k.rpRebaseLots, k.rpGcFreed, k.rpRec[RP_STALE], k.rpSkip[RPS_NOQ], k.rpSkip[RPS_STALE], k.rpAudits, g_rfEnsure, k.rpSyncs);
    assert(k.rpRec[RP_INS] > 1000 && k.rpApplied[RP_REM] > 500 && k.rpRebaseLots > 0 && k.rpGcFreed > 0 && k.rpRec[RP_STALE] > 0 &&
           k.rpSkip[RPS_NOQ] + k.rpSkip[RPS_STALE] > 0 && k.rpAudits > 1000 && g_rfEnsure > 0 && k.rpSyncs > 0);
    // the consumer thread: the same sequences drained by RpThread (lock taken through g_spAcq), audits equal
    {
        RfSetup(7);
        g_rpThreaded = true;
        g_rpEvent = CreateEventW(NULL, FALSE, FALSE, NULL);
        g_rpStop = 0;
        HANDLE th = CreateThread(NULL, 0, RpThread, NULL, 0, NULL);
        assert(th);
        const LONG mis0 = k.rpMis, a0 = k.rpAudits;
        auto wait = [] { for (int w = 0; w < 5000 && g_rpTail != g_rpPub; w++) Sleep(1); assert(g_rpTail == g_rpPub); };
        RpCut(g_rf.world);
        wait();
        for (int i = 0; i < 300; i++) {
            RfStep();
            if (RfRnd(5) == 0) { RpCut(g_rf.world); wait(); }  // the sim waits only in the test (it checks each audit)
        }
        RpCut(g_rf.world);
        wait();
        assert(k.rpMis == mis0 && k.rpAudits > a0 + 50 && k.rpFaults == 0);
        g_rpStop = 1;
        SetEvent(g_rpEvent);
        WaitForSingleObject(th, 5000);
        CloseHandle(th), CloseHandle(g_rpEvent);
        g_rpEvent = NULL;  // 0.33.3: a stale value here was recycled as TestPcThreaded's own event (flaky test)
        g_rpStop = 0;
        g_rpThreaded = false;
        RfTeardown();
        assert(g_rpBytes == 0);
    }
    // world event 7 builds the clone on the sim at once (+0x918 taken) and publishes it; with Prebuild=1 every unbuilt lot the
    // table accepts gets its first use before (the fake first use sets the built byte; lot 0x101 plays the active lot)
    RfSetup(5);
    struct Fu {
        static char __fastcall FirstUse(const u8*, void*, u8* g, DWORD) { g[0x8C] = 1; return 1; }
        static char __fastcall Contains(const u8*, void*, DWORD lo, DWORD) { return lo != 0x101; }
    };
    g_rpFirstUse = Fu::FirstUse, g_rpContains = Fu::Contains, g_rpPrebuild = true;
    for (int i = 0; i < g_rf.nLots; i++) g_rf.lots[i][0x10 + 0x8C] = (u8)(i % 2);  // lots 0x100, 0x102, 0x104 unbuilt
    DWORD* s = RfSlot();
    s[2] = 7;
    const LONG e0 = k.rpEpochs, pb0 = k.rpPrebuilt;
    SpNote(SN_WEV, s);
    assert(k.rpEpochs == e0 + 1 && g_rpPub == g_rpHead && g_rpHead == 1);
    assert(k.rpPrebuilt == pb0 + 3 && g_rf.lots[0][0x10 + 0x8C] == 1 && g_rf.lots[1][0x10 + 0x8C] == 1);  // 0x101 was built already
    g_rpPrebuild = false, g_rpFirstUse = (RpFirstUseFn)(uintptr_t)0x0063D2F0, g_rpContains = (RpContainsFn)(uintptr_t)0x00628A90;
    RpDrain(false);
    assert(g_rpClone && g_rpTail == 1);
    // W1's own child recursion (return 0x665F87) is not recorded: the replayed parent inserts its children
    const LONG ins0 = k.rpRec[RP_INS];
    s = RfSlot();
    s[-2] = (DWORD)(uintptr_t)RfQOf(0, 0), s[1] = (DWORD)(uintptr_t)g_rf.fps[1], s[0] = 0x00665F87;
    SnEnter(0, s);
    assert(k.rpRec[RP_INS] == ins0);
    RfTeardown();
    assert(g_rpBytes == 0);
    // the child class (0xFEBF90 in the game: 0x28 bytes, +0xC the parent, no list; P3-B live run 2026-10-18: every clone build
    // failed on it): the children RfBuild made become child objects; copied inside the parent's block, +0xC re-pointed
    {
        RfSetup(11);
        u8* kids[6];
        for (int i = 0; i < 6; i++) {
            u8* par = g_rf.fps[i];
            u8* ch = kids[i] = *(u8**)(*(u8**)(par + 0x24) + 8);
            *(DWORD*)ch = g_rfCls[3].vt, *(u8**)(ch + 0xC) = par, *(DWORD*)(ch + 0x24) = 0x12345678;  // +0x24: data, not a list
        }
        const LONG mis0 = k.rpMis, f0 = k.rpEpochFails, dv0 = k.rpDiverge;
        RfCut();
        assert(g_rpClone && k.rpEpochFails == f0 && g_rpClone->nLots == g_rf.nLots && k.rpExclLots == 0);
        int seen = 0;
        for (int i = 0; i < 6; i++) {
            const DWORD* cp = RpTabFind(g_rpClone->fps, (DWORD)(uintptr_t)g_rf.fps[i]);
            const DWORD* cc = RpTabFind(g_rpClone->kids, (DWORD)(uintptr_t)kids[i]);
            if (!cp) continue;  // a parent no lot holds yet
            assert(cc && *(DWORD*)(uintptr_t)(*cc + 0xC) == *cp && *(DWORD*)(uintptr_t)(*cc + 0x24) == 0x12345678);
            seen++;
        }
        assert(seen > 0);
        for (int i = 0; i < 300; i++) {
            RfStep();
            if (RfRnd(5) == 0) RfCut();
            assert(k.rpMis == mis0 && k.rpFaults == 0);
        }
        RfCut();
        assert(k.rpMis == mis0 && k.rpDiverge == dv0 && k.rpEpochFails == f0);
        RfTeardown();
        assert(g_rpBytes == 0 && k.rpFps == 0);
    }
    // a footprint class no table knows: its lot is left out of the clone (counted per vtable), the others are cloned and audited
    {
        RfSetup(12);
        const HANDLE savedLog = g_log;
        g_log = GetStdHandle(STD_OUTPUT_HANDLE);  // shows divergence lines if any
        u8* odd = RfFootprint(0x3999);
        *(DWORD*)odd = (DWORD)(uintptr_t)g_rfFpVt[4];
        RfIns(RfQOf(0, 0), odd, 0, false);  // before the build: lot 0 holds it, in three of its Qs (three FMEs)
        RfIns(RfQOf(0, 1), odd, 0, false);
        RfIns(RfQOf(0, 2), odd, 0, false);
        g_rf.fps[g_rf.nFps++] = odd;  // the steps insert it into other lots too (their rebase fails: rebuilt without them) and remove it
        const LONG mis0 = k.rpMis, f0 = k.rpEpochFails, dv0 = k.rpDiverge, u0 = k.rpUnkClsN[0] + k.rpUnkClsN[1] + k.rpUnkClsN[2] + k.rpUnkClsN[3];
        const LONG cf0 = k.rpCopyFail;
        RfCut();
        assert(g_rpClone && k.rpEpochFails == f0 && k.rpExclLots == 1 && g_rpClone->nLots == g_rf.nLots - 1 && !RpFindLot(g_rpClone, 0x100));
        assert(k.rpUnkClsN[0] + k.rpUnkClsN[1] + k.rpUnkClsN[2] + k.rpUnkClsN[3] > u0);
        assert(k.rpCopyFail == cf0 + 1);  // refused once per build, not once per FME or leaf entry (0.29.0 BP: 63k per build)
        bool vtSeen = false;
        for (int i = 0; i < 4; i++) vtSeen |= (DWORD)k.rpUnkClsVt[i] == (DWORD)(uintptr_t)g_rfFpVt[4];
        assert(vtSeen);
        for (int i = 0; i < 300; i++) {
            RfStep();
            const LONG d0 = k.rpDiverge;
            if (RfRnd(5) == 0) RfCut();
            if (k.rpDiverge != d0) {  // diagnostics: live map nodes by the lot of their Q
                int noLot = 0, ex = 0;
                const u8* M = g_rf.m;
                for (DWORD b = 0; RpD(M + 8) > 1 && b < RpD(M + 8); b++)
                    for (const u8* nd = RpP(RpP(M + 4) + 4 * b); nd; nd = RpP(nd + 0x10)) {
                        const DWORD q = RpD(RpP(nd + 8) + 0xC);
                        int lot = -1;
                        for (int l = 0; l < g_rf.nLots && lot < 0; l++)
                            for (u8** qp = *(u8***)(g_rf.lots[l] + 0x10 + 0x74); qp < *(u8***)(g_rf.lots[l] + 0x10 + 0x78); qp++)
                                if ((DWORD)(uintptr_t)*qp == q) lot = l;
                        if (lot < 0) noLot++; else if (RpIsEx(*(DWORD*)g_rf.lots[lot])) ex++;
                    }
                printf("step %d op %lu lot %d: nodes with no lot %d, in left-out lots %d, nEx %d\n", i, g_rfOp, g_rfOpLot, noLot, ex, g_rpNEx);
                fflush(stdout);
            }
            if (!(k.rpMis == mis0 && k.rpFaults == 0 && g_rpClone)) {
                printf("step %d op %lu lot %d: mis %ld faults %ld clone %p epochs %ld fails %ld\n", i, g_rfOp, g_rfOpLot, k.rpMis - mis0, k.rpFaults, (void*)g_rpClone, k.rpEpochs, k.rpEpochFails);
                fflush(stdout);
            }
            assert(k.rpMis == mis0 && k.rpFaults == 0 && g_rpClone);
        }
        RfCut();
        if (k.rpDiverge != dv0) printf("left-out lot test: %ld divergences, %ld epochs, excl %ld\n", k.rpDiverge - dv0, k.rpEpochs, k.rpExclLots);
        assert(k.rpMis == mis0 && k.rpDiverge == dv0);
        RfTeardown();
        assert(g_rpBytes == 0 && k.rpFps == 0);
        g_rpNEx = 0;
        g_log = savedLog;
    }
    // 0.29.2 (BP on 0.29.1: 6 builds failed with no fault, no refused copy, no unknown class): FMEs that only a tree leaf holds
    // (no map node). (a) one whose footprint the copy refuses: its lot is left out (before: "met only in a tree leaf", the whole
    // build failed), the cause is named; (b) one without a footprint (+8 = 0): copied as it is (before: the table lookup of
    // key 0 hit an empty slot, so its lot was left out, or the build failed)
    auto leafOnlyFme = [](u8* q, u8* fp) {
        u8* f = (u8*)g_rpFmePool(0x14, 0, 0, 0, 0, 0);
        memset(f, 0, 0x14);
        *(DWORD*)f = (DWORD)(uintptr_t)g_rfFmeVt;
        *(LONG*)(f + 4) = 1;  // held by the test (no map node holds it)
        *(u8**)(f + 8) = fp, (*(LONG*)(fp + 8))++;
        *(u8**)(f + 0xC) = q;
        u8 e[12];
        *(u8**)e = f, *(DWORD*)(e + 4) = *(DWORD*)(fp + 0x14), *(DWORD*)(e + 8) = 0;
        RfTreeAdd((u8**)(q + 0x24), e, 0);
        return f;
    };
    {
        RfSetup(13);
        u8* odd = RfFootprint(0x3998);
        *(DWORD*)odd = (DWORD)(uintptr_t)g_rfFpVt[4];
        leafOnlyFme(RfQOf(1, 0), odd);
        const u64 key1 = ((u64)*(DWORD*)(g_rf.lots[1] + 4) << 32) | *(DWORD*)g_rf.lots[1];
        const LONG mis0 = k.rpMis, f0 = k.rpEpochFails;
        RfCut();
        assert(g_rpClone && k.rpEpochFails == f0 && k.rpExclLots == 1 && !RpFindLot(g_rpClone, key1));
        assert(g_rpBuildExWhy.code == RW_UNKNOWN && g_rpBuildExWhy.lot == key1 && g_rpBuildExWhy.vt == (DWORD)(uintptr_t)g_rfFpVt[4]);
        for (int i = 0; i < 100; i++) {
            RfStep();
            if (RfRnd(5) == 0) RfCut();
            assert(k.rpMis == mis0 && k.rpFaults == 0 && g_rpClone);
        }
        RfCut();
        assert(k.rpMis == mis0);
        RfTeardown();
        assert(g_rpBytes == 0 && k.rpFps == 0);
        g_rpNEx = 0;
    }
    {
        RfSetup(14);
        u8* f = leafOnlyFme(RfQOf(2, 0), g_rf.fps[0]);
        (*(LONG*)(g_rf.fps[0] + 8))--, *(u8**)(f + 8) = NULL;  // the footprint went, the leaf entry stayed
        const LONG mis0 = k.rpMis, f0 = k.rpEpochFails, au0 = k.rpAudits;
        RfCut();
        assert(g_rpClone && k.rpEpochFails == f0 && k.rpExclLots == 0 && g_rpClone->nLots == g_rf.nLots && !g_rpBuildExWhy.code);
        RfCut();  // audits every lot, the one with the footprint-less FME too
        assert(k.rpMis == mis0 && k.rpAudits > au0);
        RfTeardown();
        assert(g_rpBytes == 0 && k.rpFps == 0);
    }
    printf("snap replay: leaf-only FME with a refused footprint leaves its lot out (cause named); one without a footprint is copied\n");
    {  // H20b: the clone's portal references (3 in +0x44 and 2 rooms x 3 in +4 / +0x14 per lot), raced by the sim
        g_stPortalVt[0] = (DWORD)(uintptr_t)PortalAddRefX, g_stPortalVt[1] = (DWORD)(uintptr_t)PortalReleaseX;
        g_stPortalVt[2] = (DWORD)(uintptr_t)StPortalDel;
        g_rfMakePortal = StPortal;
        const LONG rb = k.rpRebaseLots;
        const LONG n1 = RfPortalRefsRun(StPortalSetup, 21, 600), n2 = RfPortalRefsRun(StPortalSetup, 22, 600);
        g_rfMakePortal = NULL;
        assert(g_stPortalDel == 0 && k.rpRebaseLots > rb);
        printf("snap replay H20b: clone portal references exact (9 per lot copy), raced by %ld sim AddRef/Release pairs over rebases and "
               "frees; none deleted, all released at teardown\n", n1 + n2);
    }
    // 0.29.3 (BP on 0.29.2: "child not in its parent's list" left 209 lots out; "FME ... on another lot's Q (lot 0)" failed
    // the builds): (c) a child-class footprint whose +0xC parent does not list it, inserted on its own: own block, +0xC = the
    // parent's copy (pinned); (d) a map FME without a Q (world-level hanging footprint, 0x6377F0): copied with Q 0
    {
        RfSetup(15);
        u8* par = g_rf.fps[2];
        u8* ch = (u8*)RfAlloc(0x50);
        memset(ch, 0, 0x50);
        *(DWORD*)ch = g_rfCls[3].vt, *(LONG*)(ch + 8) = 1000, *(u8**)(ch + 0xC) = par;
        *(DWORD*)(ch + 0x14) = 0x3997, *(DWORD*)(ch + 0x24) = 0x12345678;
        RfIns(RfQOf(1, 1), ch, 0, false);
        u8* noq = (u8*)g_rpFmePool(0x14, 0, 0, 0, 0, 0);
        memset(noq, 0, 0x14);
        *(DWORD*)noq = (DWORD)(uintptr_t)g_rfFmeVt;
        *(u8**)(noq + 8) = g_rf.fps[3], (*(LONG*)(g_rf.fps[3] + 8))++;
        RfMapIns(g_rf.m, *(DWORD*)(g_rf.fps[3] + 0x14), *(DWORD*)(g_rf.fps[3] + 0x18), noq);  // the map's reference only
        const LONG mis0 = k.rpMis, f0 = k.rpEpochFails;
        RfCut();
        assert(g_rpClone && k.rpEpochFails == f0 && k.rpExclLots == 0 && g_rpClone->nLots == g_rf.nLots);
        assert(g_rpBuildAlone == 1 && g_rpBuildNoQ == 1 && RpD(g_rpClone->m + 0xC) == RpD(g_rf.m + 0xC));
        const DWORD* cc = RpTabFind(g_rpClone->fps, (DWORD)(uintptr_t)ch);
        const DWORD* cp = RpTabFind(g_rpClone->fps, (DWORD)(uintptr_t)par);
        assert(cc && cp && *(DWORD*)(uintptr_t)(*cc + 0xC) == *cp && *(DWORD*)(uintptr_t)(*cc + 0x24) == 0x12345678);
        assert(RpFpRefs((u8*)(uintptr_t)*cp) >= 1);  // the child's pin keeps the parent's copy past the GC
        const LONG dvc = k.rpDiverge, epc = k.rpEpochs, rbc = k.rpRebaseLots;
        const HANDLE savedLogC = g_log;
        g_log = GetStdHandle(STD_OUTPUT_HANDLE);  // resync failure lines, if any
        for (int i = 0; i < 200; i++) {
            RfStep();
            if (RfRnd(5) == 0) RfCut();
            assert(k.rpMis == mis0 && k.rpFaults == 0 && g_rpClone);
        }
        RfCut();
        // 0.32.1: lot rebases around the own-block child and the Q-less FME resync the map without a rebuild
        if (k.rpDiverge != dvc || k.rpEpochs != epc)
            printf("own-block child / Q-less world: %ld divergences, %ld rebuilds, %ld rebased lots\n", k.rpDiverge - dvc, k.rpEpochs - epc, k.rpRebaseLots - rbc);
        g_log = savedLogC;
        assert(k.rpMis == mis0 && k.rpDiverge == dvc && k.rpEpochs == epc && k.rpRebaseLots > rbc);
        RfTeardown();
        assert(g_rpBytes == 0 && k.rpFps == 0);
    }
    printf("snap replay: a child outside its parent's list gets its own block (parent pinned); a map FME without a Q is copied\n");
    // 0.30.2 (BP on 0.30.1: 486 builds, 872 divergences, 919 rebases in 10 min): (e) W3 = QuadTreeBuild 0x62F8A0 is get-or-
    // create: a get of an existing Q, a missing Q without create, a lot not in the table: no STALE; a missing Q with create:
    // STALE. (f) Q-less map FMEs added (0x636BF0 with lot 0, then the map insert) and removed (0x635700 with lot 0) between
    // cuts, mixed with the random steps: the clone follows through RP_NOQ, no divergence, no rebuild
    {
        int iGrid = -1, iLotRem = -1;
        for (int i = 0; i < SN_N; i++) {
            if (kSn[i].leaf == SL_GRIDADD) iGrid = i;
            if (kSn[i].leaf == SL_LOTREMOVEFP) iLotRem = i;
        }
        assert(iGrid >= 0 && iLotRem >= 0 && kSn[2].leaf == SL_W3);
        RfSetup(16);
        RfCut();
        const LONG st0 = k.rpRec[RP_STALE];
        const DWORD lot0 = *(DWORD*)g_rf.lots[0];
        auto w3 = [](DWORD lo, DWORD level, DWORD create) {
            DWORD* s = RfSlot();
            s[1] = lo, s[2] = 0, s[3] = level, s[4] = create;
            SnEnter(2, s);
        };
        w3(lot0, 0, 1), w3(lot0, 0, 0), w3(lot0, 9, 0), w3(0xDEAD, 0, 1), w3(lot0, 13, 1);
        assert(k.rpRec[RP_STALE] == st0);
        w3(lot0, 9, 1);  // level 9 has no Q: built
        assert(k.rpRec[RP_STALE] == st0 + 1);
        RfTeardown();
        assert(g_rpBytes == 0 && k.rpFps == 0);

        RfSetup(17);
        RfCut();
        const LONG mis0 = k.rpMis, dv0 = k.rpDiverge, ep0 = k.rpEpochs, nq0 = k.rpApplied[0];
        int adds = 0, rems = 0;
        for (int i = 0; i < 400; i++) {
            if (RfRnd(3) == 0) RfStep();
            u8* fp = g_rf.fps[RfRnd(g_rf.nFps)];
            const DWORD lo = *(DWORD*)(fp + 0x14), hi = *(DWORD*)(fp + 0x18);
            DWORD* sl = RfSlot();
            if (RfRnd(2)) {  // add: 0x636BF0 (fp, lot 0), then the Q-less FME into the map
                sl[1] = (DWORD)(uintptr_t)fp, sl[2] = 0;
                SnEnter(iGrid, sl);
                u8* f = (u8*)g_rpFmePool(0x14, 0, 0, 0, 0, 0);
                memset(f, 0, 0x14);
                *(DWORD*)f = (DWORD)(uintptr_t)g_rfFmeVt;
                *(u8**)(f + 8) = fp, (*(LONG*)(fp + 8))++;
                RfMapIns(g_rf.m, lo, hi, f);
                adds++;
            } else {  // remove: 0x635700 (lot 0, key), the key's Q-less nodes go
                sl[1] = 0, sl[2] = 0, sl[3] = lo, sl[4] = hi;
                SnEnter(iLotRem, sl);
                u8* m = g_rf.m;
                const DWORD nb = *(DWORD*)(m + 8);
                for (DWORD* at = nb > 1 ? &(*(DWORD**)(m + 4))[lo % nb] : NULL; at && *at;) {
                    u8* nd = (u8*)(uintptr_t)*at;
                    const u8* f = RpP(nd + 8);
                    if (RpD(nd) == lo && RpD(nd + 4) == hi && f && !RpD(f + 0xC)) {
                        *at = *(DWORD*)(nd + 0x10);
                        RfMapNodeFree(m, NULL, nd);
                        (*(DWORD*)(m + 0xC))--;
                        rems++;
                    } else at = (DWORD*)(nd + 0x10);
                }
            }
            if (RfRnd(4) == 0) RfCut();
            if (k.rpDiverge != dv0) printf("Q-less step %d (op %lu): %ld divergences\n", i, g_rfOp, k.rpDiverge - dv0);
            assert(k.rpMis == mis0 && k.rpFaults == 0 && k.rpDiverge == dv0);
        }
        RfCut();
        assert(k.rpMis == mis0 && k.rpDiverge == dv0 && k.rpApplied[0] > nq0 && adds > 50 && rems > 0);
        assert(RpD(g_rpClone->m + 0xC) == RpD(g_rf.m + 0xC));
        const LONG rebuilt = k.rpEpochs - ep0;
        RfTeardown();
        assert(g_rpBytes == 0 && k.rpFps == 0);
        RfSetup(18);  // (g) ReplayAuditLots: 2 lots per audit cut, rotating, still equal
        g_rpAuditLots = 2;
        RfCut();
        const LONG au0 = k.rpAudits, mis1 = k.rpMis;
        for (int i = 0; i < 40; i++) { RfStep(); RfCut(); }
        assert(k.rpAudits - au0 == 80 && k.rpMis == mis1);
        g_rpAuditLots = 0;
        RfTeardown();
        assert(g_rpBytes == 0 && k.rpFps == 0);
        // (i) 0.32.4 (BP on 0.32.1: 5 lot mismatches, resync "no clone partner" on child FMEs): a parent in two lots' Qs,
        // removed from one, its child replaced by a new object with another key (as 0x807400 does), inserted again; the
        // other lot's FMEs keep the old block alive, and the replayed insert must use the new child list
        {
            RfSetup(20);
            const HANDLE savedLogI = g_log;
            g_log = GetStdHandle(STD_OUTPUT_HANDLE);
            RfCut();
            const LONG mis2 = k.rpMis, dv2 = k.rpDiverge, ep2 = k.rpEpochs;
            u8* par = g_rf.fps[0];
            u8* qa = RfQOf(0, 0);
            u8* qb = RfQOf(1, 0);
            RfIns(qa, par, 0, true), RfIns(qb, par, 0, true);
            RfCut();
            RfRem(qa, par, true);
            u8* ln = *(u8**)(par + 0x24);
            u8* nch = RfFootprint(~*(DWORD*)(par + 0x14) ^ 0x10);
            *(DWORD*)(nch + 0x18) = ~*(DWORD*)(par + 0x18);
            *(u8**)(ln + 8) = nch;
            RfIns(qa, par, 0, true);
            RfCut();
            RfCut();  // audits every lot after the insert
            if (k.rpMis != mis2 || k.rpDiverge != dv2 || k.rpEpochs != ep2)
                printf("changed child list: %ld mismatches, %ld divergences, %ld rebuilds\n", k.rpMis - mis2, k.rpDiverge - dv2, k.rpEpochs - ep2);
            assert(k.rpMis == mis2 && k.rpDiverge == dv2 && k.rpEpochs == ep2);
            for (int i = 0; i < 300; i++) {
                RfStep();
                if (RfRnd(5) == 0) RfCut();
                if (k.rpMis != mis2) printf("changed child list, step %d op %lu lot %d: mismatch\n", i, g_rfOp, g_rfOpLot);
                assert(k.rpMis == mis2);
            }
            g_log = savedLogI;
            RfCut();
            assert(k.rpMis == mis2 && k.rpDiverge == dv2 && k.rpEpochs == ep2);
            RfTeardown();
            assert(g_rpBytes == 0 && k.rpFps == 0);
        }
        // (i2) 0.35.8 (I-94-440; BP 0.35.6: 692 fresh soft misses, every one a child FME with "clone copy: none"): a live
        // child address reused by a new child of another parent. Parent 1 (in two lots, so its old block stays) drops child
        // X; X becomes parent 2's child; parent 2 is inserted again (the child table maps X to its new copy), then parent 1
        // (its old block is retired). 0.35.7 deleted X's entry with the old block: X's FMEs had no clone partner
        {
            RfSetup(23);
            RfCut();
            const LONG dv4 = k.rpDiverge, ep4 = k.rpEpochs, kept0 = k.rpKidsKept;
            u8* p1 = g_rf.fps[0];
            u8* p2 = g_rf.fps[1];
            u8* qa = RfQOf(0, 0);
            u8* qb = RfQOf(1, 0);
            u8* qc = RfQOf(2, 0);
            RfIns(qa, p1, 0, true), RfIns(qb, p1, 0, true), RfIns(qc, p2, 0, true);
            RfCut();
            RfRem(qa, p1, true), RfRem(qc, p2, true);
            u8* l1 = *(u8**)(p1 + 0x24);
            u8* l2 = *(u8**)(p2 + 0x24);
            u8* x = *(u8**)(l1 + 8);
            *(DWORD*)(x + 0x14) = ~*(DWORD*)(p2 + 0x14), *(DWORD*)(x + 0x18) = ~*(DWORD*)(p2 + 0x18);  // a new object at X
            *(u8**)(l2 + 8) = x;
            u8* nch = RfFootprint(~*(DWORD*)(p1 + 0x14) ^ 0x10);
            *(DWORD*)(nch + 0x18) = ~*(DWORD*)(p1 + 0x18);
            *(u8**)(l1 + 8) = nch;
            RfIns(qc, p2, 0, true);
            RfIns(qa, p1, 0, true);
            RfCut();
            const DWORD* xv = RpTabFind(g_rpClone->kids, (DWORD)(uintptr_t)x);
            if (!xv || k.rpKidsKept == kept0) printf("reused child address: entry %s, kept %ld\n", xv ? "kept" : "deleted", k.rpKidsKept - kept0);
            assert(xv && k.rpKidsKept > kept0);
            for (int i = 0; i < 300; i++) {  // batches re-chain the map: every live node needs its partner
                RfStep();
                if (RfRnd(5) == 0) RfCut();
            }
            RfCut();
            if (k.rpDiverge != dv4 || k.rpEpochs != ep4) printf("reused child address: %ld divergences, %ld rebuilds\n", k.rpDiverge - dv4, k.rpEpochs - ep4);
            assert(k.rpDiverge == dv4 && k.rpEpochs == ep4);
            RfTeardown();
            assert(g_rpBytes == 0 && k.rpFps == 0);
            printf("replay 0.35.8: a reused live child address keeps its child table entry (kept %ld)\n", k.rpKidsKept - kept0);
        }
        // (j) 0.32.8 (BP on 0.32.4: 6 lot mismatches, clone trees with fewer entries): a footprint held by two lots changes
        // a field the search reads (+0x34) while it is out of one lot, then goes back in; the clone's copy, held by the other
        // lot's FMEs, must take the record's state (0.32.4 reused it unchanged)
        {
            RfSetup(21);
            RfCut();
            const LONG mis3 = k.rpMis, dv3 = k.rpDiverge, ep3 = k.rpEpochs;
            u8* par = g_rf.fps[1];
            u8* qa = RfQOf(0, 0);
            u8* qb = RfQOf(2, 0);
            RfIns(qa, par, 0, true), RfIns(qb, par, 0, true);
            RfCut();
            RfRem(qa, par, true);
            *(DWORD*)(par + 0x34) ^= 0x5;  // e.g. the class / type bytes, set by an IC between the remove and the insert
            RfIns(qa, par, 0, true);
            RfCut();
            RfCut();
            if (k.rpMis != mis3) printf("changed footprint field: %ld mismatches\n", k.rpMis - mis3);
            assert(k.rpMis == mis3 && k.rpDiverge == dv3 && k.rpEpochs == ep3);
            RfTeardown();
            assert(g_rpBytes == 0 && k.rpFps == 0);
        }
        // (k) 0.32.8 (BP on 0.32.4: clone memory 126 MB): 20,000 steps with a child list change every 400; the clone's memory
        // after the warm-up stays flat (retired blocks freed once no FME holds them)
        {
            RfSetup(22);
            RfCut();
            const LONG mis4 = k.rpMis;
            LONG warm = 0, peak = 0;
            for (int i = 0; i < 20000; i++) {
                RfStep();
                if (i % 400 == 199) {  // a parent in two lots gets a new child object while out of one of them
                    u8* par = g_rf.fps[i / 400 % 6];
                    u8* qa = RfQOf(0, 0);
                    RfIns(qa, par, 0, true), RfIns(RfQOf(1, 0), par, 0, true);
                    RfRem(qa, par, true);
                    u8* ln = *(u8**)(par + 0x24);
                    if (ln != par + 0x24) {
                        u8* nch = RfFootprint(~*(DWORD*)(par + 0x14));
                        *(DWORD*)(nch + 0x18) = ~*(DWORD*)(par + 0x18);
                        *(u8**)(ln + 8) = nch;
                    }
                    RfIns(qa, par, 0, true);
                }
                if (RfRnd(5) == 0) RfCut();
                if (i == 2000) warm = g_rpBytes;
                if (i > 2000 && g_rpBytes > peak) peak = g_rpBytes;
                assert(k.rpMis == mis4 && k.rpFaults == 0);
            }
            RfCut();
            printf("long replay: clone memory %ld KB after the warm-up, peak %ld KB over 18,000 more steps\n", warm >> 10, peak >> 10);
            assert(k.rpMis == mis4 && peak < warm * 2 + (1 << 20));
            RfTeardown();
            assert(g_rpBytes == 0 && k.rpFps == 0);
        }
        // (h) 0.32.1: the stand-ins behind a P1 stub, as the patched entries are in the game, so the consumer's own applies on
        // clone Qs reach the recorder too; the replay counters must equal those of the same steps without the stub
        LONG cnt[2][14];
        for (int hooked = 0; hooked < 2; hooked++) {
            g_rfHooked = hooked != 0;
            const LONG r0[14] = {k.rpRec[RP_INS], k.rpRec[RP_REM], k.rpRec[RP_STALE], k.rpRec[RP_LOTS], k.rpRec[RP_EPOCH], k.rpSkip[RPS_NOQ],
                                 k.rpSkip[RPS_STALE], k.rpRebaseLots, k.rpDiverge, k.rpMis, k.rpApplied[RP_INS], k.rpApplied[RP_REM], k.rpFaults, k.rpRec[0]};
            RfSetup(19);
            RfCut();
            for (int i = 0; i < 600; i++) { RfStep(); if (RfRnd(5) == 0) RfCut(); }
            RfCut();
            const LONG r1[14] = {k.rpRec[RP_INS], k.rpRec[RP_REM], k.rpRec[RP_STALE], k.rpRec[RP_LOTS], k.rpRec[RP_EPOCH], k.rpSkip[RPS_NOQ],
                                 k.rpSkip[RPS_STALE], k.rpRebaseLots, k.rpDiverge, k.rpMis, k.rpApplied[RP_INS], k.rpApplied[RP_REM], k.rpFaults, k.rpRec[0]};
            for (int j = 0; j < 14; j++) cnt[hooked][j] = r1[j] - r0[j];
            RfTeardown();
            g_rfHooked = false;
            assert(g_rpBytes == 0 && k.rpFps == 0);
        }
        for (int j = 0; j < 14; j++)
            if (cnt[0][j] != cnt[1][j]) printf("hooked replay counter %d: %ld without the stub, %ld with it\n", j, cnt[0][j], cnt[1][j]);
        printf("hooked replay: ins %ld rem %ld stale %ld lots %ld epochs %ld noQ %ld staleSkip %ld rebased %ld diverge %ld mis %ld applied %ld/%ld faults %ld noq %ld\n", cnt[0][0], cnt[0][1], cnt[0][2], cnt[0][3], cnt[0][4], cnt[0][5], cnt[0][6], cnt[0][7], cnt[0][8], cnt[0][9], cnt[0][10], cnt[0][11], cnt[0][12], cnt[0][13]);
        assert(!memcmp(cnt[0], cnt[1], sizeof(cnt[0])) && cnt[0][8] == 0 && cnt[0][9] == 0 && cnt[0][10] > 100);
        printf("snap replay: W3 get-or-create (no STALE for a get); %d Q-less adds, %d removes followed by RP_NOQ (%ld records), "
               "0 divergences, %ld rebuilds\n", adds, rems, k.rpApplied[0] - nq0, rebuilt);
    }
    {  // the middle class 0x1020AE8 (LotEdgeFootprint, PolygonFootprint; 0.29.0 left it out): 0x40 bytes with a polygon cache
        const RpCls* m = RpClass(0x1020AE8);
        assert(g_rpCls == kRpClsGame && m && m->size == 0x40 && m->cache && !m->child);
    }
    printf("snap replay: child footprint class copied in its parent's block; an unknown class leaves its lot out (rpExclLots), "
           "refused once per build, audits equal; middle class 0x1020AE8 known\n");
    printf("snap replay:%ld cuts over %ld writes on 4 seeds (random allocation addresses): clone == live at every cut (%ld lot audits, "
           "%ld rebased lots, %ld syncs, %ld rebuilds after divergence), unrecorded insert found, consumer thread ok, event 7 prebuild + clone ok, memory back to 0\n", cuts, steps,
           k.rpAudits, k.rpRebaseLots, k.rpSyncs, k.rpDiverge);
    g_spWorldSlot = (const u8* const*)(uintptr_t)0x011D9128;
    g_spAcq = (LockAcquireFn)(uintptr_t)0x004E16F0;
    g_spRel = (SpRelFn)(uintptr_t)0x004E17B0;
    g_rtSimTidSlot = savedSim;
}

// 0.35.12 (docs/94 §22.15, I-94-458/459): replay memory over a long run. (a) The pointer tables stay sized to their live
// entries under key churn (before, erased slots counted as used and every grow doubled: the fake's fps table went 1024 ->
// 4096 in 50k steps with ~45 live blocks). (b) With the consumer behind (threaded mode, nobody draining) at most one LOTS
// batch is in the ring: later cuts' rebases wait and go in one batch; after the drain the clone audits equal.
static int RpRingCount(DWORD type, bool mapOnly = false) {  // mapOnly: RP_LOTS batches with map work (an order)
    int n = 0;
    for (DWORD i = g_rpTail; i != g_rpHead; i++) {
        const RpRec& r = g_rpRing[i & (kRpRing - 1)];
        n += r.type == type && (!mapOnly || ((const RpBatch*)r.p)->order);
    }
    return n;
}
// 0.36.23 (docs/94 §22.32, I-94-452): soft forces on a left-out or vanished lot leave g_rpSoftPend at the cut; live ones stay
static void TestSoftPendPrune() {
    const int nEx = g_rpNEx, nPend = g_rpNSoftPend;
    u64 ex0[64], pend0[64];
    memcpy(ex0, g_rpEx, sizeof(ex0)), memcpy(pend0, g_rpSoftPend, sizeof(pend0));
    RpLiveLot live[3] = {{0x10, NULL}, {0x20, NULL}, {0x30, NULL}};
    g_rpEx[0] = 0x20, g_rpNEx = 1;
    g_rpSoftPend[0] = 0x10, g_rpSoftPend[1] = 0x20, g_rpSoftPend[2] = 0x99, g_rpSoftPend[3] = 0x30, g_rpNSoftPend = 4;
    const LONG p0 = g_rpSoftPruned;
    assert(RpSoftPendPrune(live, 3) == 2 && g_rpNSoftPend == 2 && g_rpSoftPruned == p0 + 2);
    assert((g_rpSoftPend[0] == 0x10 && g_rpSoftPend[1] == 0x30) || (g_rpSoftPend[0] == 0x30 && g_rpSoftPend[1] == 0x10));
    assert(RpSoftPendPrune(live, 3) == 0 && g_rpNSoftPend == 2);
    memcpy(g_rpEx, ex0, sizeof(ex0)), memcpy(g_rpSoftPend, pend0, sizeof(pend0));
    g_rpNEx = nEx, g_rpNSoftPend = nPend;
    printf("soft pend prune ok\n");
}
static void TestReplayLongRun() {
    Ts3nCounters& k = ts3native_counters;
    static DWORD simTid;
    const DWORD* savedSim = g_rtSimTidSlot;
    simTid = GetCurrentThreadId();
    g_rtSimTidSlot = &simTid;
    {  // (a0) the table alone: 1M distinct keys, at most 64 live
        RpTab t = {};
        for (DWORD i = 0; i < 1000000; i++) {
            assert(RpTabPut(t, 0x1000 + i * 16, i));
            if (i >= 64) { DWORD* v = RpTabFind(t, 0x1000 + (i - 64) * 16); assert(v); t.k[v - t.v] = 1; }
        }
        assert(t.cap <= 1024 && RpTabFind(t, 0x1000 + 999999 * 16) && !RpTabFind(t, 0x1000 + 999935 * 16));
        RpTabFree(t);
    }
    RfSetup(21);
    RfCut();
    const LONG mis0 = k.rpMis, dv0 = k.rpDiverge;
    LONG kb20 = 0, kbMax = 0;
    DWORD capMax = 0;
    for (int i = 1; i <= 60000; i++) {
        RfStep();
        if (i % 5 == 0) RfCut();
        if (i == 20000) kb20 = g_rpBytes >> 10;
        if (i > 20000) kbMax = max(kbMax, g_rpBytes >> 10);
        if (g_rpClone) capMax = max(capMax, max(max(g_rpClone->fps.cap, g_rpClone->kids.cap), max(g_rpClone->old.cap, g_rpClone->oldLive.cap)));
    }
    printf("replay long run: 60k steps, 12k cuts: %ld KB at 20k steps, max %ld KB after; largest clone table %lu slots; fps %lu live / %lu\n",
           kb20, kbMax, capMax, g_rpClone->fps.used, g_rpClone->fps.cap);
    assert(capMax <= 2048 && kbMax <= kb20 * 2 + 64 && k.rpMis == mis0 && k.rpDiverge == dv0 && k.rpFaults == 0);
    // (b) the consumer behind: threaded mode with nobody draining between cuts
    g_rpThreaded = true;
    const HANDLE ev = g_rpEvent;
    g_rpEvent = NULL;
    int lotsMax = 0, waits = 0;
    const LONG lw0 = g_rpLotsWait, rb0 = k.rpRec[RP_LOTS];
    for (int i = 1; i <= 6000; i++) {
        RfStep();
        if (i % 5) continue;
        RpCut(g_rf.world);
        waits += g_rpCutDeferred;
        lotsMax = max(lotsMax, RpRingCount(RP_LOTS, true));  // 0.35.23: one batch with map work in flight (shells may queue)
        if (i % 1000 == 0) RpDrain(false);  // the consumer catches up now and then
    }
    g_rpThreaded = false;
    g_rpEvent = ev;
    RfCut();  // the waiting rebases in one batch, then SYNC / AUDIT
    RfCut();
    printf("replay consumer behind: %d cuts deferred (%ld waits logged), at most %d LOTS batch in the ring, %ld batches; audits equal\n", waits,
           g_rpLotsWait - lw0, lotsMax, k.rpRec[RP_LOTS] - rb0);
    assert(lotsMax <= 1 && waits > 0 && g_rpLotsWait > lw0 && k.rpMis == mis0 && k.rpFaults == 0);
    RfTeardown();
    assert(g_rpBytes == 0 && k.rpFps == 0);
    g_rtSimTidSlot = savedSim;
}

// --- private pools (pools.cpp, docs/94 §16.6-§16.7): the 12 site stubs on dummy code shaped like the game's pop / push sites,
// selection by thread, a pool-object change as an epoch on the replay, and two threads on the game's and the private pools ---
static volatile LONG g_tpGo, g_tpBad;
static u8* g_tpPool;  // the "game" pool of the race test
static DWORD WINAPI TpRace(void* arg) {
    const bool owner = arg != NULL;
    const DWORD sig = owner ? 0xC10E0000u : 0x6A3E0000u;
    if (owner) RpOpEnter();
    u8* held[64];
    int n = 0;
    DWORD seed = owner ? 7 : 11;
    while (!g_tpGo) YieldProcessor();
    for (int i = 0; i < 200000; i++) {
        seed = seed * 1103515245 + 12345;
        if (n < 64 && (n == 0 || (seed >> 16) & 1)) {
            u8* pool = PlPickPop(PL_LEAF);
            if (pool != (owner ? g_plPool[PL_LEAF] : g_tpPool)) InterlockedIncrement(&g_tpBad);
            u8* x = (u8*)PlPopFrom(pool);
            if (!x || PlOwns(x) != owner || (*(DWORD*)(x + 4) & 0xFFFF0000) == (owner ? 0x6A3E0000u : 0xC10E0000u)) InterlockedIncrement(&g_tpBad);
            *(DWORD*)(x + 4) = sig | (DWORD)i;  // a node held by the other thread would carry its mark
            *(DWORD*)(x + 8) = sig;
            held[n++] = x;
        } else {
            u8* x = held[--n];
            if (*(DWORD*)(x + 8) != sig) InterlockedIncrement(&g_tpBad);
            u8* pool = PlPickPush(PL_LEAF, x);
            if (pool != (owner ? g_plPool[PL_LEAF] : g_tpPool)) InterlockedIncrement(&g_tpBad);
            PlPushTo(pool, x);
        }
    }
    while (n) { u8* x = held[--n]; PlPushTo(PlPickPush(PL_LEAF, x), x); }
    if (owner) RpOpLeave(0);
    return 0;
}
static int TpFree(const u8* pool) {  // free-list length, -1 on a loop
    int n = 0;
    for (const u8* x = *(u8* const*)(pool + 0x10); x; x = *(u8* const*)x) if (++n > 1 << 20) return -1;
    return n;
}
// crash G (0.32.2): a fake private cAStarSearch whose deleting dtor (vtable slot 0, as 0x61EB20) pushes one private node
// through the A* push site, as the real dtor's node frees do
static u8* g_saNode;
static u8* g_saWent;
static void __fastcall SaDtor(u8*, void*, DWORD) {
    typedef u8*(__cdecl * PushFn)(void* node);
    g_saWent = ((PushFn)(uintptr_t)(g_plPushAt[PL_ASTAR] - 5))(g_saNode);
}
static void* g_saVt[1] = {(void*)SaDtor};
static void TestPools() {
    Ts3nCounters& k = ts3native_counters;
    static __declspec(align(16)) u8 livePool[PL_N][0x20];
    static u8* liveSlot[PL_N];
    u8* code = (u8*)VirtualAlloc(NULL, 4096, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
    assert(code && PlRegion());
    DWORD savedPop[PL_N], savedPush[PL_N], savedGlobal[PL_N];
    memcpy(savedPop, g_plPopAt, sizeof(savedPop)), memcpy(savedPush, g_plPushAt, sizeof(savedPush)), memcpy(savedGlobal, g_plGlobal, sizeof(savedGlobal));
    const LONG savedReady = k.plReady, savedOn = g_plOn;
    u8* at = code;
    for (int i = 0; i < PL_N; i++) {
        PlInitPool(livePool[i], i == PL_INNER ? 0x1C : 0x14, (void*)RfBlock);
        liveSlot[i] = livePool[i];
        g_plGlobal[i] = (DWORD)(uintptr_t)&liveSlot[i];
        g_plPopAt[i] = (DWORD)(uintptr_t)at;  // mov eax, [global]; ret
        *at++ = 0xA1, *(DWORD*)at = g_plGlobal[i], at += 4, *at++ = 0xC3;
        if (kPlPushReg[i] == 0) {  // push esi; mov esi, [esp+8]; mov eax, [global]; pop esi; ret
            *at++ = 0x56, *at++ = 0x8B, *at++ = 0x74, *at++ = 0x24, *at++ = 0x08;
            g_plPushAt[i] = (DWORD)(uintptr_t)at;
            *at++ = 0xA1, *(DWORD*)at = g_plGlobal[i], at += 4, *at++ = 0x5E, *at++ = 0xC3;
        } else {  // mov ecx, [esp+4]; mov eax, [global]; ret
            *at++ = 0x8B, *at++ = 0x4C, *at++ = 0x24, *at++ = 0x04;
            g_plPushAt[i] = (DWORD)(uintptr_t)at;
            *at++ = 0xA1, *(DWORD*)at = g_plGlobal[i], at += 4, *at++ = 0xC3;
        }
    }
    typedef u8*(__cdecl * PopFn)();
    typedef u8*(__cdecl * PushFn)(void* node);
    auto pop = [&](int i) { return ((PopFn)(uintptr_t)g_plPopAt[i])(); };
    auto push = [&](int i, void* n) { return ((PushFn)(uintptr_t)(g_plPushAt[i] - (kPlPushReg[i] ? 4 : 5)))(n); };
    *(u8*)(uintptr_t)g_plPushAt[1] = 0xB8;  // one differing byte: nothing patched
    assert(!PlPatch(0, PL_N - 1, "test") && *(u8*)(uintptr_t)g_plPopAt[0] == 0xA1);
    *(u8*)(uintptr_t)g_plPushAt[1] = 0xA1;
    assert(PlPatch(0, PL_N - 1, "test") && *(u8*)(uintptr_t)g_plPopAt[0] == 0xE8 && *(u8*)(uintptr_t)g_plPushAt[5] == 0xE8);
    const LONG live0 = k.rpLivePool, for0 = k.plForeign, ov0 = k.plOverlap;
    for (int i = 0; i < PL_N; i++) assert(pop(i) == livePool[i]);  // no clone thread: the game's pools
    LONG op = RpOpEnter();  // the replay op thread: tree pools private, the A* family the game's
    assert(pop(PL_LEAF) == g_plPool[PL_LEAF] && pop(PL_INNER) == g_plPool[PL_INNER] && pop(PL_ASTAR) == livePool[PL_ASTAR]);
    assert(*(DWORD*)g_plPool[PL_INNER] == 0x1C && *(void**)(g_plPool[PL_INNER] + 0x14) == (void*)PlBlockAlloc);
    u8* mine = (u8*)PlBlockAlloc(0x1000, 0);
    assert(mine && PlOwns(mine) && k.plKB > 0);
    u8 other[0x20];
    assert(push(PL_LEAF, mine) == g_plPool[PL_LEAF] && push(PL_INNER, mine + 0x40) == g_plPool[PL_INNER] && k.plForeign == for0);
    assert(push(PL_LEAF, other) == livePool[PL_LEAF] && k.rpLivePool == live0 + 1);  // the op thread pushing a game node
    {  // [RouteSnap] PoolChunkMB (docs/66 §7 A1): first region full -> the next block from a new 16 MB region the stubs own
        g_plChunkMB = 16, g_plResMB = 16;  // kept on: later tests grow in 16 MB regions too
        g_plEnd = (u8*)(((uintptr_t)g_plTop + 15) & ~(uintptr_t)15);
        u8* q = (u8*)PlBlockAlloc(0x1000, 0);
        assert(q && g_plNReg == 1 && q == g_plRegLo[0] && PlOwns(q) && PlOwns(q + 0xFFF) && !PlOwns(q + 0x1000) && !PlOwns(other));
        assert(k.plChunks == 2 && k.plResKB == 32 << 10 && k.plSavedKB == 64 << 10);
        assert(push(PL_LEAF, q) == g_plPool[PL_LEAF] && push(PL_INNER, q + 0x40) == g_plPool[PL_INNER] && k.plForeign == for0);
    }
    RpOpLeave(op);
    assert(push(PL_LEAF, other) == livePool[PL_LEAF] && k.rpLivePool == live0 + 1);
    assert(push(PL_ASTAR, mine) == g_plSink && k.plForeign == for0 + 1);  // a private A* node from a thread owning nothing: the sink
    assert(!PoolsEnterJob());  // sites not in place: no private job
    k.plReady = 3;
    assert(PoolsEnterJob() && k.plOverlap == ov0);  // a P4 clone run: every pool
    for (int i = 0; i < PL_N; i++) assert(pop(i) == g_plPool[i]);
    assert(push(PL_CELL, other) == g_plSink && k.rpLivePool == live0 + 2 && push(PL_QCELL, mine) == g_plPool[PL_QCELL] && k.plForeign == for0 + 1);
    PoolsLeaveJob();
    for (int i = 0; i < PL_N; i++) assert(pop(i) == livePool[i]);
    // crash G: the DISCARD / WRAP teardown of the private A* object (PcAstarDrop, worker, after the run left the job) pushes
    // its private nodes as job work: back to the private pool, not plForeign to the sink (0.32.0 logged 8 such sink lines)
    {
        void (*savedEnter)() = g_p4PoolsEnterJob, (*savedLeave)() = g_p4PoolsLeaveJob;
        g_p4PoolsEnterJob = PoolsEnterJobHook, g_p4PoolsLeaveJob = PoolsLeaveJob;
        static __declspec(align(16)) u8 fakeA[0x10];
        *(void***)fakeA = g_saVt;
        g_saNode = mine + 0x80, g_saWent = NULL;
        PcWork& pw = g_pcWork;
        u8* const savedA = pw.astar;
        pw.astar = fakeA;
        const LONG f0 = k.plForeign, ov = k.plOverlap;
        PcAstarDrop(pw);
        if (g_saWent != g_plPool[PL_ASTAR] || k.plForeign != f0) {
            printf("TestPools FAIL (crash G): the A* teardown pushed its private node to %s (plForeign +%ld)\n",
                   g_saWent == g_plSink ? "the sink" : "another pool", k.plForeign - f0);
            assert(false);
        }
        assert(!pw.astar && !g_plJobTid && k.plOverlap == ov);
        pw.astar = savedA;
        g_p4PoolsEnterJob = savedEnter, g_p4PoolsLeaveJob = savedLeave;
    }
    // another thread while this one holds the switch: the game's pools; a job waits for the set (0.35.12, I-94-460: before,
    // it ran on the same private lists and only this order was counted as plOverlap); a cut on another thread is deferred
    op = RpOpEnter();
    struct T { static DWORD WINAPI Run(void* p) { *(u8**)p = ((PopFn)(uintptr_t)g_plPopAt[PL_LEAF])(); PoolsEnterJob(); PoolsLeaveJob(); return 0; } };
    u8* got = NULL;
    HANDLE th = CreateThread(NULL, 0, T::Run, &got, 0, NULL);
    assert(WaitForSingleObject(th, 200) == WAIT_TIMEOUT);  // the job waits while the op owns the pool set
    struct T2 { static DWORD WINAPI Run(void* p) { LONG pv; *(int*)p = RpOpTry(&pv) ? 1 : 2; return 0; } };
    int tried = 0;
    HANDLE th2 = CreateThread(NULL, 0, T2::Run, &tried, 0, NULL);
    WaitForSingleObject(th2, 5000), CloseHandle(th2);
    assert(tried == 2);  // the sim only tries: refused, no wait
    RpOpLeave(op);
    WaitForSingleObject(th, 5000), CloseHandle(th);
    assert(got == livePool[PL_LEAF] && k.plOverlap == ov0 && !g_plJobTid && !g_rpOpTid);
    {  // the other order: a job owns the set, the replay op tries and is refused (the sim's cut is deferred)
        assert(PoolsEnterJob());
        HANDLE th3 = CreateThread(NULL, 0, T2::Run, &tried, 0, NULL);
        WaitForSingleObject(th3, 5000), CloseHandle(th3);
        assert(tried == 2 && k.plOverlap == ov0);
        PoolsLeaveJob();
        LONG pv;
        assert(RpOpTry(&pv));
        RpOpLeave(pv);
    }
    // the map: the clone's M+0x1C gets a private pool made from the live one; a live pool object change is an epoch
    g_plOn = 0;
    assert(!PlMapPool(livePool[PL_ASTAR]));  // off
    g_plOn = 1;
    assert(PlMapPool(NULL) == g_plPool[PL_MAP] && *(DWORD*)g_plPool[PL_MAP] == 0x18);
    u8 fakeP[0x40] = {};
    *(u8**)(fakeP + 0x28) = livePool[PL_ASTAR];
    const PlSeen seen = PlSeenNow(fakeP);
    assert(!PlMoved(seen, fakeP));
    liveSlot[PL_LEAF] = livePool[PL_GOAL];
    assert(PlMoved(seen, fakeP));
    liveSlot[PL_LEAF] = livePool[PL_LEAF];
    *(u8**)(fakeP + 0x28) = livePool[PL_GOAL];
    assert(PlMoved(seen, fakeP));
    // race: one thread on the game's leaf pool, one (the op thread) on the private one, 200k pops / pushes each; no node
    // reaches the other thread, every node comes back, the free lists hold every node once
    g_tpPool = livePool[PL_LEAF];
    g_tpGo = 0, g_tpBad = 0;
    const int liveFree0 = TpFree(g_tpPool), privFree0 = TpFree(g_plPool[PL_LEAF]);
    HANDLE ta = CreateThread(NULL, 0, TpRace, NULL, 0, NULL), tb = CreateThread(NULL, 0, TpRace, (void*)1, 0, NULL);
    assert(ta && tb);
    g_tpGo = 1;
    WaitForSingleObject(ta, 30000), WaitForSingleObject(tb, 30000);
    CloseHandle(ta), CloseHandle(tb);
    const int liveFree = TpFree(g_tpPool), privFree = TpFree(g_plPool[PL_LEAF]);
    assert(g_tpBad == 0 && liveFree >= liveFree0 && privFree >= privFree0 && liveFree > 0 && privFree > 0 && g_rpOpTid == 0);
    for (const u8* x = *(u8* const*)(g_tpPool + 0x10); x; x = *(u8* const*)x) assert(!PlOwns(x));
    for (const u8* x = *(u8* const*)(g_plPool[PL_LEAF] + 0x10); x; x = *(u8* const*)x) assert(PlOwns(x));
    memcpy(g_plPopAt, savedPop, sizeof(savedPop)), memcpy(g_plPushAt, savedPush, sizeof(savedPush)), memcpy(g_plGlobal, savedGlobal, sizeof(savedGlobal));
    k.plReady = savedReady, g_plOn = savedOn;
    VirtualFree(code, 0, MEM_RELEASE);
    // epoch: the replay on its fake world; the game's leaf pool object replaced between cuts (PathPlanner_Reset): the cut
    // counts it and rebuilds, the drain drops the old clone first; audits stay equal and every clone node stays private
    static DWORD simTid;
    const DWORD* savedSim = g_rtSimTidSlot;
    simTid = GetCurrentThreadId();
    g_rtSimTidSlot = &simTid;
    RfSetup(21);
    RfCut();
    for (int i = 0; i < 150; i++) { RfStep(); if (RfRnd(4) == 0) RfCut(); }
    RfCut();
    const LONG mis0 = k.rpMis, ep0 = k.rpEpochs, pe0 = k.plEpochs, fa0 = k.rpFaults;
    assert(RfPoolCheck() > 50);
    static __declspec(align(16)) u8 reset[0x20];
    PlInitPool(reset, 0x14, (void*)RfBlock);
    g_rfPoolSlot[0] = reset;
    RpCut(g_rf.world);
    assert(k.plEpochs == pe0 + 1 && k.rpEpochs == ep0 + 1);
    RpClone* old = g_rpClone;
    RpDrain(false);
    assert(g_rpClone && g_rpClone != old && g_rpClone->seen.leaf == (DWORD)(uintptr_t)reset);
    for (int i = 0; i < 150; i++) { RfStep(); if (RfRnd(4) == 0) RfCut(); }
    RfCut();
    if (k.rpMis != mis0 || k.rpEpochs != ep0 + 1 || k.rpLivePool != live0 + 2 || k.plForeign != for0 + 1)
        printf("pools epoch: mis %ld faults %ld epochs %ld diverge %ld livePool %ld foreign %ld\n", k.rpMis - mis0, k.rpFaults - fa0, k.rpEpochs - ep0,
               k.rpDiverge, k.rpLivePool - live0, k.plForeign - for0);
    assert(k.rpMis == mis0 && k.rpFaults == fa0 && k.rpEpochs == ep0 + 1 && k.rpLivePool == live0 + 2 && k.plForeign == for0 + 1);
    assert(RfPoolCheck() > 50);
    RfTeardown();
    g_rtSimTidSlot = savedSim;
    g_spWorldSlot = (const u8* const*)(uintptr_t)0x011D9128;
    g_spAcq = (LockAcquireFn)(uintptr_t)0x004E16F0;
    g_spRel = (SpRelFn)(uintptr_t)0x004E17B0;
    printf("private pools: 12 site stubs (byte check all-or-none; pops private on the replay op thread (tree) and in a P4 job (all), pushes "
           "by address; rpLivePool / plForeign / plOverlap count misuse), map pool, pool-object change = epoch (clone dropped and rebuilt, "
           "audits equal), race 2 x 200k ops game vs private pool: 0 corruption (free lists %d / %d)\n", liveFree, privFree);
}

// --- [RouteSnap] Mode=1, P4 part A (p4.cpp, docs/94 §12, §17, §18): thunks on dummies, door record / replay, shadow flow with
// a fake clone provider, fallbacks, D2 handshake, the §13 v2 record ---
static_assert(offsetof(Ts3nCounters, rpReady) == 0x2768 && offsetof(Ts3nCounters, snP4Ready) == 0x2968 &&
                  offsetof(Ts3nCounters, snM) == 0x297C && offsetof(Ts3nCounters, snAbort) == 0x2998 &&
                  offsetof(Ts3nCounters, snShadowUs) == 0x29D8 && offsetof(Ts3nCounters, snDoorRec) == 0x2A04 &&
                  offsetof(Ts3nCounters, snNrBits) == 0x2A2C && offsetof(Ts3nCounters, snPathsFreed) == 0x2A50 &&
                  offsetof(Ts3nCounters, snPcReady) == 0x2A54, "v50 layout");
static_assert(offsetof(Ts3nCounters, snPcRefused) == 0x2A64 && offsetof(Ts3nCounters, snPcAcquired) == 0x2A84 &&
                  offsetof(Ts3nCounters, snPcDiscard) == 0x2AA4 && offsetof(Ts3nCounters, arLosRefused) == 0x2AB0 &&
                  offsetof(Ts3nCounters, plReady) == 0x2AB4, "v51 layout");
static_assert(offsetof(Ts3nCounters, rpLivePool) == 0x2AB8 && offsetof(Ts3nCounters, plJobs) == 0x2AD0,
              "v53 layout (pools.cpp; after a merged v52 it moves, docs/registry.md)");
static_assert(offsetof(Ts3nCounters, snNrBitPortal) == 0x2AD4 && offsetof(Ts3nCounters, rpPortalRefs) == 0x2AD8 &&
                  offsetof(Ts3nCounters, snPcPortalPend) == 0x2ADC,
              "v54 layout (P4 portal references H20)");
static_assert(offsetof(Ts3nCounters, spTileFill) == 0x2AE4 && offsetof(Ts3nCounters, spTileFillUs) == 0x2AEC &&
                  offsetof(Ts3nCounters, spTileOut) == 0x2AF4,
              "v55 layout (lazy Gtiles)");
static_assert(offsetof(Ts3nCounters, p4VarTrip) == 0x2AF8, "v56 layout (H20 tripwire)");
static_assert(offsetof(Ts3nCounters, lrPendFlush) == 0x2AFC && offsetof(Ts3nCounters, lrPendBusy) == 0x2B00 &&
                  offsetof(Ts3nCounters, pcDeferStop) == 0x2B04,
              "v57 layout (lot-0 remove guard, P4 deferral stop)");
static_assert(offsetof(Ts3nCounters, lpReady) == 0x2B08 && offsetof(Ts3nCounters, lpWindowMs) == 0x2B20 &&
                  offsetof(Ts3nCounters, lpRtUs) == 0x2B24 && offsetof(Ts3nCounters, lpRtThrUs) == 0x2B2C &&
                  offsetof(Ts3nCounters, lpSite) == 0x2B38 && offsetof(Ts3nCounters, prReady) == 0x2DD8,
              "v59 layout ([LoadProf]; v58 not in this build)");
static_assert(offsetof(Ts3nCounters, prPlans) == 0x2DF0 && offsetof(Ts3nCounters, prExact) == 0x2E20 &&
                  offsetof(Ts3nCounters, prExactUs) == 0x2E68 && offsetof(Ts3nCounters, prFpOnly) == 0x2EB0 &&
                  offsetof(Ts3nCounters, prMismatch) == 0x2F28 && offsetof(Ts3nCounters, prLooseUs) == 0x2F58 &&
                  sizeof(Ts3nCounters) >= 0x2F70 && offsetof(Ts3nCounters, m2Ready) == 0x2F70,
              "v60 layout ([PlanRepeat]; v58 not in this build)");

// 0.31.4 (docs/94 §21.11): a quadtree insert / remove on a clone Q that is being iterated would defer into the live pending
// maps (the clone P carries the live P+0x30 / P+0x3C); in a clone run the hook stops the run (lazy stop) before the original.
static int g_dsCalls;
static DWORD __fastcall DsIns(void*, void*, void*) { return ++g_dsCalls; }
static DWORD __fastcall DsRem(void*, void*, DWORD, DWORD) { return ++g_dsCalls; }
static DWORD DsTry(bool ins, u8* q) {
    __try { if (ins) PcQInsHook(q, NULL, NULL); else PcQRemHook(q, NULL, 1, 2); }
    __except (EXCEPTION_EXECUTE_HANDLER) { return GetExceptionCode(); }
    return 0;
}
static void TestP4DeferStop() {
    Ts3nCounters& k = ts3native_counters;
    g_pcQIns = DsIns, g_pcQRem = DsRem;
    static u8 q[0x58];
    const LONG stop0 = k.pcDeferStop, lazy0 = k.snCloneLazy;
    *(LONG*)(q + 0x48) = 1;
    assert(DsTry(true, q) == 0 && DsTry(false, q) == 0 && g_dsCalls == 2);  // live (no clone run): passes, even iterated
    t_snapRun = 1;
    g_p4Job.abort = -1;
    *(DWORD*)(q + 0x18) = 0x1234, *(DWORD*)(q + 0x1C) = 5;
    g_p4Job.nDirty = 0, g_p4LazyKeep = 0, g_rpLazyKeep = 1;
    assert(DsTry(true, q) == kP4LazyCode && DsTry(false, q) == kP4LazyCode && g_dsCalls == 2);  // stopped before the original
    assert(k.pcDeferStop == stop0 + 2 && k.snCloneLazy == lazy0 + 2 && g_p4Job.abort == P4_AB_LAZY);
    // 0.35.21: a keep-stop (P4REL_LAZY at the release), the Q's lot in the run's dirty list once
    assert(g_p4LazyKeep == 1 && g_p4Job.nDirty == 1 && g_p4Job.dirty[0] == 0x500001234ull);
    g_p4Job.nDirty = 0, g_p4LazyKeep = 0, g_rpLazyKeep = 0;
    *(LONG*)(q + 0x48) = 0;  // not iterated: the game writes only the clone Q (flush + apply), passes
    assert(DsTry(true, q) == 0 && DsTry(false, q) == 0 && g_dsCalls == 4 && k.pcDeferStop == stop0 + 2);
    t_snapRun = 0;
    g_p4Job.abort = -1;
    g_pcQIns = NULL, g_pcQRem = NULL;
    puts("P4 deferral stop: an iterated clone Q's insert / remove stops the clone run (lazy) before the live pending maps; live and "
         "non-iterated calls pass");
}

static DWORD g_t4Live = 0x1111, g_t2Live = 0x0041FFFF, g_clipLive[8] = {1, 2, 3, 0xA11C, 5, 6, 7, 0xA11D};
static u8* g_p4Code;  // dummy sites: T1 +0, T2 +16, T2' +32, T3 +48, T4 +64 (each followed by ret)
static void P4Dummies(u8* c) {
    memset(c, 0xCC, 96);
    for (int i = 0; i < P4_SITES; i++) {
        memcpy(c + 16 * i, kP4Pro[i], kP4Len[i]);
        if (i != P4_T4) c[16 * i + kP4Len[i]] = 0xC3;
    }
}
static DWORD P4CallEax(const u8* fn) { return ((DWORD(__cdecl*)())fn)(); }
// T3 through a dummy `call; ret`: ecx out; eax, edx and CF must survive
static void P4T3Probe(const u8* fn, DWORD* ecxOut, DWORD* eaxOut, DWORD* edxOut, DWORD* cf) {
    DWORD a, c, d, f;
    __asm {
        mov eax, 0x11111111
        mov edx, 0x22222222
        mov ecx, 0x33333333
        stc
        call dword ptr [fn]
        pushfd
        pop f
        mov a, eax
        mov c, ecx
        mov d, edx
    }
    *ecxOut = c; *eaxOut = a; *edxOut = d; *cf = f & 1;
}
static DWORD WINAPI P4OtherThread(LPVOID p) {
    DWORD* out = (DWORD*)p;
    out[0] = P4CallEax(g_p4Code + 64);
    out[1] = P4CallEax(g_p4Code + 16);
    return 0;
}

// fake door target (cdecl, 9 args)
static int g_p4FdCalls;
static DWORD __cdecl P4FakeDoor(DWORD a1, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD a9) { g_p4FdCalls++; return a1 * 10 + (a9 & 0xFF); }  // the game reads arg 9 as a byte

// fake RoutingPathImp (docs/93 §4.2 offsets) with a vtable: [0] destroy, +0x88 / +0x90 unlink
static int g_fpDestroy, g_fpUnlink, g_fpListFree;
static void __fastcall P4FpDestroy(void* self, void*, DWORD) { g_fpDestroy++; free(*(void**)((u8*)self + 0x98)); free(self); }
static void __fastcall P4FpUnlink(void*, void*, DWORD a) { assert(a == 0); g_fpUnlink++; }
static void* g_fpVt[0x100 / 4];
static void __fastcall P4FakeListFree(void* head, void*) {  // 0x866E90: release each node's path, free the node
    int* h = (int*)head;
    for (int* n = (int*)(uintptr_t)h[0]; n != h;) {
        int* next = (int*)(uintptr_t)n[0];
        u8* p = (u8*)(uintptr_t)n[2];
        if (p && InterlockedDecrement((volatile LONG*)(p + 4)) == 0) ((P4PathSlotFn)g_fpVt[0])(p, NULL, 1);
        free(n);
        n = next;
        g_fpListFree++;
    }
}
static void P4AddPath(int* head, DWORD status, int nodes, float yMid) {
    u8* p = (u8*)calloc(1, 0x190);
    *(void**)p = g_fpVt;
    *(LONG*)(p + 4) = 1;
    *(DWORD*)(p + 0x94) = status;
    *(DWORD*)(p + 0xB0) = 0xC6600B67;
    *(float*)(p + 0x8C) = 7.5f;
    u8* nb = (u8*)calloc(nodes ? nodes : 1, 48);
    for (int i = 0; i < nodes; i++) {
        float* f = (float*)(nb + 48 * i);
        f[0] = 10.0f + i; f[1] = i && i < nodes - 1 ? yMid : -100.0f; f[2] = 20.0f - i;
        *(float*)(nb + 48 * i + 0x20) = 1.25f * i;
        *(DWORD*)(nb + 48 * i + 0x2C) = i == nodes - 1 ? 8 : 0;
    }
    *(u8**)(p + 0x98) = nb;
    *(u8**)(p + 0x9C) = nb + 48 * nodes;
    int* n = (int*)malloc(12);
    n[2] = (int)(uintptr_t)p;
    n[0] = (int)(uintptr_t)head;           // append at the end
    n[1] = head[1];
    ((int*)(uintptr_t)head[1])[0] = (int)(uintptr_t)n;
    head[1] = (int)(uintptr_t)n;
}

// one fake planner for both runs: the clone run is told apart by t_snapRun and must see the copy through T4
static void* g_p4CloneWorld = (void*)(uintptr_t)0xC10E;
static int g_p4Variant;  // 0 same, 1 node y, 2 goal index, 3 status, 4 door miss, 5 fault, 6 destroy in the window
static int g_p4LiveCalls, g_p4CloneCalls;
static volatile LONG g_p4PastLazy;
static const unsigned __int64* g_p4IdSet;  // the live options' +0x08 id set
static struct { CRITICAL_SECTION cs; LONG depth; } g_fkEaLock;  // EA heap lock object (heap +0x4E4: CS, depth +0x18)
static u8 __fastcall P4FakePlan(void* world, void*, int* outList, int* starts, int* goals, DWORD agent, DWORD, u8* opts, int* out1,
                                int* out2, DWORD, DWORD) {
    const bool clone = t_snapRun != 0;
    assert(clone == (world == g_p4CloneWorld));
    assert(P4CallEax(g_p4Code + 64) == (clone ? (DWORD)(uintptr_t)g_p4CloneWorld : g_t4Live));  // T4 picks by thread state
    assert(agent == 0xA6E && *(const float*)(uintptr_t)starts[0] == 1.5f && ((const float*)(uintptr_t)goals[0])[16] == 3.5f);
    assert(*(const DWORD*)(opts + 0x78) == 0x7777);
    if (clone) {
        g_p4CloneCalls++;
        assert(*(DWORD*)(opts + 0x38) != 0 || *(DWORD*)(opts + 0x3C) == *(DWORD*)(opts + 0x38));  // a private vector header
        const unsigned __int64* ids = *(const unsigned __int64* const*)(opts + 0x08);  // +0x08 deep-copied too
        assert(ids != g_p4IdSet && *(DWORD*)(opts + 0x0C) == (DWORD)(uintptr_t)(ids + 3) && ids[2] == 0x33);
        // RoutingJob_ctor copies the private start/goal records with movaps (0x651350, docs/94 §21.10): 16-aligned or C0000005
        const int* const vecs[2] = {starts, goals};
        for (const int* v : vecs)
            for (const float* r = (const float*)(uintptr_t)v[0]; r < (const float*)(uintptr_t)v[1]; r += 16) {
                assert(((uintptr_t)r & 15) == 0);
                volatile __m128 x = _mm_load_ps(r), y = _mm_load_ps(r + 4);
                (void)x, (void)y;
            }
        if (g_p4Variant == 5) {  // crash H: the fault inside EA's free, its heap lock taken as 0x4E5920 takes it
            EnterCriticalSection(&g_fkEaLock.cs);
            g_fkEaLock.depth++;
            *(volatile int*)0 = 1;
        }
        if (g_p4Variant == 6) InterlockedIncrement(&g_nrN[2]);  // a destroy counted in the window (D5)
        if (g_p4Variant == 7 || g_p4Variant == 8) InterlockedIncrement(&g_nrN[7]);  // a display level write in the window
        if (g_p4Variant == 11) InterlockedIncrement(&ts3native_counters.snIc[12]);  // SetPortalCost in the window (H20d)
        if (g_p4Variant == 9 || g_p4Variant == 10) {  // D4 (H20c): a lazy build / lot table insert stops the run before the call
            DWORD stk[16] = {};
            if (g_p4Variant == 9) RgEnter(3, stk + 4); else PvEnter(PV_LAZY0, stk + 4);
            g_p4PastLazy = 1;  // never reached
        }
    } else g_p4LiveCalls++;
    if (g_p4Variant == 7 && t_p4Read) t_p4Read |= P4R_VRAY;  // the plan ran the vertical ray 0x6583E0 (variant 8 did not)
    // arg 9 carries different register junk above its byte in the two runs (docs/94 §18.7)
    const DWORD door = ((P4DoorFn)P4DoorStub)(clone && g_p4Variant == 4 ? 4 : 3, 0, 0, 0, 0, 0, 0, 0, clone ? 0xAB000002 : 0x12000002);
    assert(door == (clone && g_p4Variant == 4 ? 0u : 32u));
    if (clone) {
        assert(t_snapRun && SnLive() && P4CallEax(g_p4Code + 64) == (DWORD)(uintptr_t)g_p4CloneWorld);  // armed again after a P5 ask
        if (g_p4Variant == 12) g_spEpoch++;                // P5: the world moved during the run (stale cut)
        if (g_p4Variant == 13) P4Abort(P4_AB_MANAGED);     // P5 M2-2: a managed call outside a door ask
    }
    const bool alt = clone && g_p4Variant;
    P4AddPath(outList, alt && g_p4Variant == 3 ? 7 : 2, 3, alt && (g_p4Variant == 1 || g_p4Variant >= 6) ? 0.0f : -100.0f);
    *out1 = 0;
    *out2 = alt && g_p4Variant == 2 ? 0 : 1;
    return !(alt && g_p4Variant == 3);
}

static int g_pvRel;
static P4Clone g_pvCl;
static DWORD P4FakeCut() { return 9; }
static int P4FakeAcquire(DWORD ver, P4Clone* out) {
    assert(ver == 9);
    out->world = g_p4CloneWorld; out->ver = ver; out->epoch = (DWORD)g_spEpoch; out->fresh = 1;
    return -1;
}
static void P4FakeRelease(const P4Clone* c, LONG flags, const unsigned __int64*, int) { g_pvCl = *c; g_pvRel = flags | 0x100; }
static const P4CloneProvider kP4Fake = {P4FakeCut, P4FakeAcquire, P4FakeRelease};

static volatile LONG g_nrDone;
static volatile LONG g_nrStarted;  // the writer thread runs (under parallel load a thread can start > 50 ms late)
static DWORD WINAPI P4NrWriter(LPVOID) { InterlockedExchange(&g_nrStarted, 1); P4NrEnter(); InterlockedExchange(&g_nrDone, 1); InterlockedDecrement(&g_snNrIn); return 0; }

// one async job through the P4 path (START, live capture, J_DONE, shadow); returns the record
static const char* P4RunJob(int variant, int* outList, int* s, int* g, u8* opts) {
    g_p4Variant = variant;
    AsyncJob j = {};
    j.kind = 1;
    j.seq = 100 + variant;
    j.x87 = GetX87();
    j.mxcsr = _mm_getcsr();
    P4OnStart(&j);
    P4LiveBegin(&j);
    outList[0] = outList[1] = (int)(uintptr_t)outList;
    int o1 = -1, o2 = -1;
    const u8 r = PlanLockedHook((void*)(uintptr_t)0x2000, NULL, outList, s, g, 0xA6E, 1, opts, &o1, &o2, 5, 0);
    assert(r == 1 && o2 == 1);
    P4LiveEnd(&j);
    P4FakeListFree(outList, NULL);  // the live route's list: the game's business
    P4Shadow();
    g_p4Buf[g_p4Len] = 0;
    return g_p4Buf;
}

// --- P4 part B end to end (p4clone.cpp): replay-fake.h's world, its replay clone as the provider, and a fake planner that
// reads only the routing world it is handed: P's lot table and every lot's trees (RpLotHash), the lock-key sets of the
// footprints in P's map, and one world byte (+0x9F0) as the status. Equal worlds give equal answers. ---
static int g_pcVariant;  // 0 unchanged, 1 an unrecorded tree insert in the live world after the cut, 2 a world byte after the cut
static const u8* g_pcLiveSet;
static int g_pcCloneRuns, g_pcSetsSeen;
static u8 __fastcall PcFakePlan(void* world, void*, int* outList, int*, int*, DWORD, DWORD, u8*, int* out1, int* out2, DWORD, DWORD) {
    const bool clone = t_snapRun != 0;
    u8* w = (u8*)world;
    assert(clone == (w != g_rf.world));
    if (!clone && g_pcVariant == 1) RfIns(RfQOf(0, 0), g_rf.fps[3], 1, false);  // no record: the clone cannot know (flag 1: never a no-op)
    if (!clone && g_pcVariant == 2) w[0x9F0] = 1;
    const u8* P = *(const u8* const*)(w + 0x95C);
    u64 h = 0xCBF29CE484222325ull;
    RpLiveLot lots[64];
    const int n = RpLiveLots(P, lots, 64);
    assert(n > 0);
    for (int i = 0; i < n; i++) { DWORD e; h = RpMix(h, (DWORD)RpLotHash(lots[i].node, &e)); h = RpMix(h, e); }
    const u8* M = RpP(P + 0x24);  // lock-key sets through the footprints of P's map
    for (DWORD b = 0; RpD(M + 8) > 1 && b < RpD(M + 8); b++)
        for (const u8* nd = RpP(RpP(M + 4) + 4 * b); nd; nd = RpP(nd + 0x10)) {
            const u8* fp = RpP(RpP(nd + 8) + 8);
            const u8* set = RpP(fp + 0x20);
            if (!set) continue;
            if (clone) { assert(set != g_pcLiveSet); g_pcSetsSeen++; }  // a private copy, never the live set
            for (const u8* id = RpP(set + 8); id < RpP(set + 0xC); id += 8) h = RpMix(h, RpD(id));
        }
    if (clone) {
        g_pcCloneRuns++;
        assert(w[0x918 + 0x24] == 1 && *(LONG*)(w + 0x918 + 0x20) == 0);  // the copy's own unlocked +0x918
        assert(P == g_rpClone->p);
    }
    const float y = (float)(h % 997);  // the world's content decides the inner node's y
    P4AddPath(outList, w[0x9F0] ? 7 : 2, 3, y);
    *out1 = 0, *out2 = 1;
    return !w[0x9F0];
}
static const char* PcRunJob(int variant, int* outList, int* s, int* g, u8* opts) {
    g_pcVariant = variant;
    AsyncJob j = {};
    j.kind = 1;
    j.seq = 200 + variant;
    j.x87 = GetX87();
    j.mxcsr = _mm_getcsr();
    P4OnStart(&j);
    P4LiveBegin(&j);
    outList[0] = outList[1] = (int)(uintptr_t)outList;
    int o1 = -1, o2 = -1;
    PlanLockedHook(g_rf.world, NULL, outList, s, g, 0xA6E, 1, opts, &o1, &o2, 5, 0);
    P4LiveEnd(&j);
    P4FakeListFree(outList, NULL);
    P4Shadow();
    g_p4Buf[g_p4Len] = 0;
    return g_p4Buf;
}
static u8 g_pcWm[0x200], g_pcAct[0x60];
static const u8* g_pcWmSlot = g_pcWm;
// a world planner W (I-94-220) with a lot-edge group hash, and G's two water-link trees (I-94-221), on replay-fake's world
static __declspec(align(16)) u8 g_pcW[2][0x198];
static DWORD g_pcGrpBk[6], g_pcGrpNd[4][3];
static u8 g_pcTreeNd[2][3][0x20];
static void PcFakeW(int which) {
    u8* W = g_pcW[which];
    memset(W, 0, sizeof(g_pcW[0]));
    memset(g_pcGrpBk, 0, sizeof(g_pcGrpBk));
    g_pcGrpBk[5] = 0x2468ACE0;
    for (int i = 0; i < 4; i++) {
        DWORD* n = g_pcGrpNd[i];
        n[0] = (DWORD)(i + 1) << 16 | (DWORD)(2 * i + 1), n[1] = 10 + i, n[2] = g_pcGrpBk[n[0] % 5];
        g_pcGrpBk[n[0] % 5] = (DWORD)(uintptr_t)n;
    }
    *(DWORD**)(W + 0xA4) = g_pcGrpBk, *(DWORD*)(W + 0xA8) = 5, *(DWORD*)(W + 0xAC) = 4;
    *(DWORD*)(W + 0x9C) = 0x5EED0000 + which;  // the RNG word
    *(DWORD*)(W + 0xD8) = 0x61, *(DWORD*)(W + 0x114) = 0x61;  // next-resize words of caches that have grown (crash G)
    for (int t = 0; t < 2; t++) {
        u8* anc = g_rf.world + 0x8C + (t ? 0x4C : 0x30);
        u8(*n)[0x20] = g_pcTreeNd[t];
        memset(n, 0, sizeof(g_pcTreeNd[0]));
        for (int i = 0; i < 3; i++) *(DWORD*)(n[i] + 0x10) = 100 * t + i;
        *(u8**)(n[1] + 4) = n[0], *(u8**)n[1] = n[2], *(u8**)(n[1] + 8) = anc, *(u8**)(n[0] + 8) = *(u8**)(n[2] + 8) = n[1];
        *(u8**)anc = n[2], *(u8**)(anc + 4) = n[0], *(u8**)(anc + 8) = n[1], *(DWORD*)(anc + 0x10) = 3;
    }
    *(u8**)(g_rf.world + 0x960) = W;
}
// after an acquire: [copy+0x960] = W' (private), its P / R / G the copy's, the group hash and the trees private and equal
static void PcCheckWp(const u8* liveW) {
    const PcWork& w = g_pcWork;
    u8* W = w.wp;
    assert(W && W != liveW && *(u8**)(w.world + 0x960) == W && w.wpLive == liveW && *(LONG*)(W + 4) == RP_BIAS);
    assert(*(u8**)(W + 8) == g_rpClone->p && *(u8**)(W + 0x10) == w.world + 0x54 && *(u8**)(W + 0x14) == w.world + 0x8C);
    assert(*(u8**)(W + 0xEC) == g_rpClone->p && *(u8**)(W + 0xF0) == W && *(u8**)(W + 0xF8) == w.world + 0x54);
    assert(*(DWORD*)(W + 0x9C) == *(const DWORD*)(liveW + 0x9C) && *(DWORD*)(W + 0xC4) == kPcEmptyBk && *(u8**)(W + 0xE0) == W + 0xE0);
    // crash G: empty caches on the shared empty bucket array must rehash on their first insert (next resize 0, as the ctor)
    if (*(DWORD*)(W + 0xC4) == kPcEmptyBk && *(DWORD*)(W + 0xD8)) { printf("PcCheckWp FAIL (crash G): W' +0xD8 = live's\n"); assert(false); }
    if (*(DWORD*)(W + 0x100) == kPcEmptyBk && *(DWORD*)(W + 0x114)) { printf("PcCheckWp FAIL (crash G): W' +0x114 = live's\n"); assert(false); }
    const DWORD* bk = *(DWORD**)(W + 0xA4);
    assert(bk != *(DWORD**)(liveW + 0xA4) && *(DWORD*)(W + 0xA8) == 5 && *(DWORD*)(W + 0xAC) == 4 && bk[5] == 0x2468ACE0);
    for (int i = 0; i < 4; i++) {
        const DWORD key = g_pcGrpNd[i][0];
        const DWORD* n = (const DWORD*)(uintptr_t)bk[key % 5];
        while (n && n[0] != key) n = (const DWORD*)(uintptr_t)n[2];
        assert(n && n != g_pcGrpNd[i] && n[1] == g_pcGrpNd[i][1]);
    }
    for (int t = 0; t < 2; t++) {
        const u8* la = g_rf.world + 0x8C + (t ? 0x4C : 0x30);
        const u8* ca = w.world + 0x8C + (t ? 0x4C : 0x30);
        u8 lv[3 * 16], cv[3 * 16];
        assert(PcTreeWalk(la, lv) == 3 && PcTreeWalk(ca, cv) == 3 && !memcmp(lv, cv, sizeof(lv)));  // the copy's walk ends at its own anchor
        assert(*(u8**)(ca + 4) != *(u8**)(la + 4) && *(u8**)(*(u8**)(ca + 8) + 8) == ca);
    }
}
static void PcSetup(DWORD seed) {
    RfSetup(seed);
    PcFakeW(0);
    for (int i = 0; i < g_rf.nLots; i++) g_rf.lots[i][0x10 + 0x8C] = 1;  // built: no first use at the START
    static u8 set[0x30];
    static unsigned __int64 ids[2] = {0x5151, 0x5252};
    memset(set, 0, sizeof(set));
    *(u64**)(set + 8) = ids, *(u64**)(set + 0xC) = ids + 2, *(u64**)(set + 0x10) = ids + 2;
    *(u64**)(set + 0x1C) = ids, *(u64**)(set + 0x20) = ids, *(u64**)(set + 0x24) = ids;
    *(u8**)(g_rf.fps[2] + 0x20) = set;  // one footprint with a lock-key set (as Footprint_SetLockKeyObject 0x807110 leaves it)
    g_pcLiveSet = set;
    memset(g_pcWm, 0, sizeof(g_pcWm));
    *(u8**)(g_pcWm + 0xB0) = g_pcAct;
    *(DWORD*)(g_pcAct + 0x48) = 0x77;  // the active lot (none of ours)
    g_spWmSlot = &g_pcWmSlot;
    g_spKeysDirty = 1;
    RfCut();  // the epoch clone, drained
    assert(g_rpClone && g_rpClone->nLots == g_rf.nLots);
}

// 0.33.1 (docs/94 §21.14), on PcSetup's world with the replay clone provider registered:
// (a) two cut slots: with Mode=1 the next START's cut goes to the other slot and the hold stays on the older cut until its
//     shadow ends (release or drop); an older hold past kPcPendMs is replaced as before ("epoch", sub "replaced" / "passed")
// (b) soft resync: an unrecorded footprint swap on a lot-0 FME, then a lot-1 change: the batch's map resync misses the lot-0
//     node, lot 0 is forced, the clone is stale (acquire refused "mat", sub "stale"), no rebuild; the next cut rebases lot 0
static void TestPcMode2(int* list, int* s, int* g, u8* opts);  // after p5c-test.h (P5b's lock fakes)
static void TestPcTwoSlots(int* list, int* s, int* g, u8* opts) {
    Ts3nCounters& k = ts3native_counters;
    const LONG mode = k.snP4Mode;
    k.snP4Mode = 1;
    AsyncJob a = {}, b = {};
    a.kind = b.kind = 1;
    for (PcCut& h : g_pcCuts) h.pend = 0;  // the cut the test above left unacquired (its job is gone)
    P4OnStart(&a);
    P4OnStart(&b);  // every cut pushes SYNC / AUDIT records: b's version is newer
    assert(a.p4Ver && b.p4Ver && a.p4Ver != b.p4Ver && g_rpHoldAt == a.p4Ver && g_pcHoldNext == b.p4Ver);
    P4Clone ca = {}, cb = {};
    assert(PcAcquire(a.p4Ver, &ca) < 0);  // the older cut: the consumer stopped at it
    PcRelease(&ca, 0, NULL, 0);
    assert(g_rpHoldAt == b.p4Ver && !g_pcHoldNext);
    assert(PcAcquire(b.p4Ver, &cb) < 0);
    PcRelease(&cb, 0, NULL, 0);
    assert(!g_rpHoldAt && !g_pcHoldNext);
    P4OnStart(&a);
    P4OnStart(&b);
    PcDrop(a.p4Ver);  // a shadow that ends without an acquire (busy, refused before it) moves the hold on
    assert(g_rpHoldAt == b.p4Ver && !g_pcHoldNext);
    PcDrop(b.p4Ver);
    assert(!g_rpHoldAt);
    P4OnStart(&a);
    for (int i = 0; i < 2; i++) if (g_pcCuts[i].ver == a.p4Ver) g_pcCuts[i].tick -= kPcPendMs + 1;  // its job never got to the shadow
    P4OnStart(&b);
    assert(g_rpHoldAt == b.p4Ver && !g_pcHoldNext);
    assert(PcAcquire(a.p4Ver, &ca) == P4_AB_EPOCH && g_pcAcqWhy == PS_PASSED);
    assert(PcAcquire(b.p4Ver, &cb) < 0);
    PcRelease(&cb, 0, NULL, 0);
    // (a2) 0.35.6 (docs/94 §21.21): a START while a job runs shares the newest pending cut: same version, that cut's write
    // count, the hold until both shadows end; a shadow that wrote clone lots refuses the other ("mat"/"stale")
    {
        const LONG js = g_jobs[0].state, bw0 = k.p4Borrow, bm0 = k.p4BorrowMiss, bd0 = k.p4BorrowDirty;
        P4OnStart(&a);
        assert(a.p4Ver && g_rpHoldAt == a.p4Ver);
        InterlockedIncrement(&g_spWrites);  // a write after the cut (the running job's): the shared cut's count stays the cut's
        g_jobs[0].state = J_RUNNING;
        P4OnStart(&b);
        assert(b.p4Ver == a.p4Ver && b.p4W == a.p4W && b.p4W != g_spWrites && k.p4Borrow == bw0 + 1);
        assert(PcAcquire(a.p4Ver, &ca) < 0);
        PcRelease(&ca, 0, NULL, 0);
        assert(g_rpHoldAt == a.p4Ver);  // still held for b's shadow
        assert(PcAcquire(b.p4Ver, &cb) < 0);
        PcRelease(&cb, 0, NULL, 0);
        assert(!g_rpHoldAt);
        P4OnStart(&b);  // nothing pending to share: refused as before
        assert(!b.p4Ver && b.p4Why == PS_BUSY && k.p4BorrowMiss == bm0 + 1);
        g_jobs[0].state = J_FREE;
        P4OnStart(&a);
        g_jobs[0].state = J_RUNNING;
        P4OnStart(&b);
        g_jobs[0].state = js;
        assert(b.p4Ver == a.p4Ver);
        const unsigned __int64 lot = 0x5A5A5A5A5A5A5A5Aull;  // no such lot: the force list is cleared below
        assert(PcAcquire(a.p4Ver, &ca) < 0);
        PcRelease(&ca, 0, &lot, 1);
        assert(PcAcquire(b.p4Ver, &cb) == P4_AB_MAT && g_pcAcqWhy == PS_STALE && k.p4BorrowDirty == bd0 + 1 && !g_rpHoldAt);
        g_rpNForce = 0;
        P4OnStart(&a);  // a new cut is clean again
        assert(a.p4Ver && PcAcquire(a.p4Ver, &ca) < 0);
        PcRelease(&ca, 0, NULL, 0);
        printf("P4 0.35.6 shared cut: a START during a running job shares the pending cut (w0 the cut's), hold kept for both "
               "shadows, a dirty release refuses the second\n");
    }
    // (b)
    const LONG ep = k.rpEpochs, dv = k.rpDiverge, st = k.snPcRefused[7], m0 = k.snM[0];
    u8* m = g_rf.m;
    u8* f = NULL;
    for (DWORD i = 0; !f && i < *(DWORD*)(m + 8); i++)
        for (u8* nd = (u8*)(uintptr_t)(*(DWORD**)(m + 4))[i]; nd && !f; nd = RpP(nd + 0x10)) {
            u8* fme = RpP(nd + 8);
            const u8* fp = fme ? RpP(fme + 8) : NULL;
            if (!fp || RpIsChild(fp)) continue;
            for (u8** qp = *(u8***)(g_rf.lots[0] + 0x10 + 0x74); qp < *(u8***)(g_rf.lots[0] + 0x10 + 0x78); qp++)
                if (*qp == RpP(fme + 0xC)) f = fme;
        }
    assert(f);
    u8* old = RpP(f + 8);
    u8* nf = RfFootprint(*(DWORD*)(old + 0x14));
    *(DWORD*)(nf + 0x18) = *(DWORD*)(old + 0x18), *(DWORD*)(nf + 0x34) = *(DWORD*)(old + 0x34);
    (*(LONG*)(old + 8))--, (*(LONG*)(nf + 8))++;
    *(u8**)(f + 8) = nf;  // no record, no lot signature change: the clone keeps the old footprint's copy on this node
    // lot 1 forced (as a W3 build): a full batch rebases it and resyncs the map. 0.35.17: a portal list change alone (a
    // signature move, Q list unchanged) is a shell refresh, which neither walks nor resyncs the map
    g_rpForce[g_rpNForce++] = *(DWORD*)g_rf.lots[1];
    const char* r = PcRunJob(0, list, s, g, opts);
    assert(strstr(r, "\"refused\":\"mat\"") && strstr(r, "\"sub\":\"stale\"") && k.snPcRefused[7] == st + 1);
    assert(k.rpDiverge == dv + 1 && k.rpEpochs == ep && g_rpStale && !g_rpNeedEpoch);
    const LONG repin0 = g_rpRepinScans;
    r = PcRunJob(0, list, s, g, opts);  // lot 0 forced into this cut's batch: the map resyncs, the clone answers again
    assert(g_rpRepinScans == repin0);  // 0.34.1: a batch never walks every clone footprint per replaced one (the long holds)
    assert(strstr(r, "\"refused\":null") && k.snM[0] == m0 + 1 && !g_rpStale && k.rpEpochs == ep && k.rpDiverge == dv + 1);
    {  // 0.35.3 (docs/94 §21.18): a consumer miss before a queued EPOCH marks the clone bad and asks no rebuild; P4 refuses it
        const DWORD at0 = g_rpEpochAt;  // g_rpTail is left alone (a consumer thread may read it)
        const LONG old0 = k.rpOldAsk, bad0 = k.p4AcqBad, ref0 = k.snPcRefused[2];
        t_rpReplay++;
        g_rpEpochAt = g_rpTail + 5;  // an EPOCH at tail + 4: the record being applied is older
        RP_NEED_EPOCH();
        assert(!g_rpNeedEpoch && g_rpBad && k.rpOldAsk == old0 + 1);
        g_rpEpochAt = g_rpTail;  // the record is after the last EPOCH: the newest clone missed it, rebuild as before
        RP_NEED_EPOCH();
        assert(g_rpNeedEpoch && k.rpOldAsk == old0 + 1);
        t_rpReplay--;
        g_rpEpochAt = at0, g_rpNeedEpoch = 0;
        r = PcRunJob(0, list, s, g, opts);  // the rebuild ask is gone (a later cut built it), the clone is still bad
        assert(strstr(r, "\"sub\":\"noclone\"") && k.p4AcqBad == bad0 + 1 && k.snPcRefused[2] == ref0 + 1);
        g_rpBad = 0;
        r = PcRunJob(0, list, s, g, opts);
        assert(strstr(r, "\"refused\":null"));
    }
    // (c) 0.35.5 (docs/94 §21.20): two batches cut before the consumer's force lands, then 5 fresh misses in a row (0.35.2
    // rebuilt on the 4th failed batch): the second batch's miss is in flight (not forced again), no rebuild, the clone matches
    // once the forced lots are in a batch
    auto swap = [&](int lot) {  // an unrecorded footprint swap on one of the lot's FMEs (as (b))
        u8* fm = NULL;
        for (DWORD i = 0; !fm && i < *(DWORD*)(m + 8); i++)
            for (u8* nd = (u8*)(uintptr_t)(*(DWORD**)(m + 4))[i]; nd && !fm; nd = RpP(nd + 0x10)) {
                u8* fme = RpP(nd + 8);
                const u8* fp = fme ? RpP(fme + 8) : NULL;
                if (!fp || RpIsChild(fp)) continue;
                for (u8** qp = *(u8***)(g_rf.lots[lot] + 0x10 + 0x74); qp < *(u8***)(g_rf.lots[lot] + 0x10 + 0x78); qp++)
                    if (*qp == RpP(fme + 0xC)) fm = fme;
            }
        assert(fm);
        u8* o = RpP(fm + 8);
        u8* n = RfFootprint(*(DWORD*)(o + 0x14));
        *(DWORD*)(n + 0x18) = *(DWORD*)(o + 0x18), *(DWORD*)(n + 0x34) = *(DWORD*)(o + 0x34);
        (*(LONG*)(o + 8))--, (*(LONG*)(n + 8))++;
        *(u8**)(fm + 8) = n;
    };
    const LONG fl0 = k.rpSoftInFlight, fr0 = k.rpSoftFresh, h0 = k.rpHard[1], why1 = k.rpRebaseWhy[1];
    swap(0);
    g_rpForce[g_rpNForce++] = *(DWORD*)g_rf.lots[1];  // a full batch (0.35.17: a portal list change alone is a shell refresh, no resync)
    RpCut(g_rf.world);
    g_rpForce[g_rpNForce++] = *(DWORD*)g_rf.lots[1];
    RpCut(g_rf.world);  // cut before the consumer saw the first batch's miss
    RpDrain(false);
    assert(k.rpSoftFresh == fr0 + 1 && k.rpSoftInFlight == fl0 + 1 && g_rpStale && !g_rpNeedEpoch && g_rpNForce == 1);
    RfCut();  // lot 0 forced into this batch
    assert(!g_rpStale && k.rpEpochs == ep && k.rpRebaseWhy[1] == why1 + 1 && !g_rpNSoftPend);
    swap(0);
    g_rpForce[g_rpNForce++] = *(DWORD*)g_rf.lots[1];
    RfCut();
    for (int i = 1; i <= 4; i++) {  // each batch rebases the lot forced before and misses the other one
        swap(i & 1);
        RfCut();
        assert(g_rpStale && !g_rpNeedEpoch && g_rpSoftFresh == i + 1);
    }
    RfCut();
    assert(!g_rpStale && !g_rpSoftFresh && k.rpEpochs == ep && k.rpHard[1] == h0 && k.rpSoftFresh == fr0 + 6);
    k.snP4Mode = mode;
    printf("P4 0.33.1: two cut slots (older hold kept until its shadow ends, dropped shadows move it on, stale holds replaced); "
           "soft map resync (lot forced, clone stale, no rebuild, next cut compares M0); 0.35.5: a miss in flight not forced "
           "again, 5 fresh misses in a row without a rebuild\n");
}

// 0.33.2 (docs/94 §21.15): the threaded shape of the 0.33.1 hang. Real locks: +0x918 is a CRITICAL_SECTION (g_spAcq /
// g_spRel), the replay consumer runs on its own thread (RpThread, RpDrain(true)), the "worker" thread runs each job's live
// plan under +0x918 and then P4Shadow, the test thread is the sim (recorded writes under +0x918, async STARTs with their
// cut). A step that makes no progress for 5 s is a hang: the phases of the three threads are printed and the test fails.
// Every 7th job the worker keeps +0x918 through its shadow (a leaked hold): 0.33.1's consumer then waited for the lock while
// holding g_rpUse and the shadow waited for g_rpUse (the hang shape); 0.33.2 refuses that shadow ("other", sub "held").
// +0x918 is a real CRITICAL_SECTION inside the fake world, as in the game (P4HoldsRoutingLock reads its owner).
static CRITICAL_SECTION* g_stLk;
#define g_stCs (*g_stLk)
static LONG __fastcall StAcq(void*, void*, const DWORD* d) {
    if (d && *d == 0) return TryEnterCriticalSection(g_stLk) ? 1 : -2;  // deadline 0: a try, as Lock_Acquire 0x4E16F0
    EnterCriticalSection(g_stLk);
    return 1;
}
static LONG __fastcall StRel(void*, void*) { LeaveCriticalSection(g_stLk); return 0; }
static volatile LONG g_stWorkPhase, g_stJobs, g_stStop, g_stLeaks, g_stB2b;
static HANDLE g_stGo;
static int* g_stArgs[4];
// g_stB2b (0.34.1): back to back, as BP / IP play: the sim STARTs the next job as soon as the last one's live plan is done,
// so it queues while the worker runs the shadow; with nothing queued the idle worker runs a parked shadow (ServeAsync's)
static DWORD WINAPI StWorker(LPVOID) {
    for (;;) {
        WaitForSingleObject(g_stGo, g_stB2b ? 20 : INFINITE);
        if (g_stStop) return 0;
        AsyncJob* jp = NULL;
        for (int i = 0; i < 2; i++)
            if (g_jobs[i].state == J_QUEUED && (!jp || (LONG)(g_jobs[i].seq - jp->seq) < 0)) jp = &g_jobs[i];
        if (!jp) {  // a wake-up without a job: the idle worker's parked shadow
            if (g_stB2b) { g_stWorkPhase = 3; P4ShadowIdle(); g_stWorkPhase = 0; }
            continue;
        }
        AsyncJob& j = *jp;
        InterlockedExchange(&j.state, J_RUNNING);
        g_stWorkPhase = 1;  // live plan under +0x918
        EnterCriticalSection(&g_stCs);
        P4LiveBegin(&j);
        int* list = g_stArgs[0];
        list[0] = list[1] = (int)(uintptr_t)list;
        int o1 = -1, o2 = -1;
        g_pcVariant = 0;
        PlanLockedHook(g_rf.world, NULL, list, g_stArgs[1], g_stArgs[2], 0xA6E, 1, (u8*)g_stArgs[3], &o1, &o2, 5, 0);
        P4LiveEnd(&j);
        P4FakeListFree(list, NULL);
        const bool leak = !g_stB2b && j.seq % 7 == 0;
        if (!leak) LeaveCriticalSection(&g_stCs);
        InterlockedExchange(&j.state, J_DONE);
        g_stWorkPhase = 2;  // shadow
        if (leak) InterlockedIncrement(&g_stLeaks);
        P4Shadow();
        if (leak) LeaveCriticalSection(&g_stCs);
        g_stWorkPhase = 0;
        InterlockedExchange(&j.state, J_FREE);
        InterlockedIncrement(&g_stJobs);
    }
}
static void TestPcThreadedB2b();
static void TestPcThreaded(int* list, int* s, int* g, u8* opts) {
    Ts3nCounters& k = ts3native_counters;
    const LONG mode = k.snP4Mode;
    k.snP4Mode = 1;
    g_stLk = (CRITICAL_SECTION*)(g_rf.world + 0x918);
    InitializeCriticalSection(g_stLk);
    const DWORD* savedWs = g_p4WorldSlot;
    g_p4WorldSlot = (const DWORD*)&g_rfWorldSlot;
    const LONG oth0 = k.snAbort[P4_AB_OTHER], m1 = k.snM[1], m2 = k.snM[2], m3 = k.snM[3], m5 = k.snM[5];
    const LockAcquireFn savedAcq = g_spAcq;
    const SpRelFn savedRel = g_spRel;
    g_spAcq = StAcq, g_spRel = (SpRelFn)StRel;
    g_stArgs[0] = list, g_stArgs[1] = s, g_stArgs[2] = g, g_stArgs[3] = (int*)opts;
    g_rpThreaded = true, g_rpStop = 0;
    // own events: the consumer's and the worker's must be two (0.33.3: a closed g_rpEvent value left by an earlier test was
    // recycled as g_stGo, so the consumer's wake-ups ran extra "jobs" and the main thread's go reached the consumer)
    const HANDLE savedEv = g_rpEvent;
    g_rpEvent = CreateEventW(NULL, FALSE, FALSE, NULL);
    g_stGo = CreateEventW(NULL, FALSE, FALSE, NULL);
    assert(g_rpEvent && g_stGo && g_rpEvent != g_stGo);
    g_stStop = 0, g_stJobs = 0, g_stLeaks = 0, g_stB2b = 0;
    memset(&g_jobs[1], 0, sizeof(g_jobs[1]));
    HANDLE cons = CreateThread(NULL, 0, RpThread, NULL, 0, NULL), work = CreateThread(NULL, 0, StWorker, NULL, 0, NULL);
    const LONG m0 = k.snM[0], recs0 = k.snShadow;
    g_rfSeed = 4242;
    for (int i = 0; i < 400; i++) {
        EnterCriticalSection(&g_stCs);  // the sim's own routing writes
        for (int n = (int)RfRnd(4); n >= 0; n--) RfStep();
        LeaveCriticalSection(&g_stCs);
        if (g_jobs[0].state != J_FREE) continue;
        AsyncJob& j = g_jobs[0];
        memset(&j, 0, sizeof(j));
        j.kind = 1, j.seq = 1000 + i, j.x87 = GetX87(), j.mxcsr = _mm_getcsr();
        P4OnStart(&j);  // the cut takes +0x918 on this thread
        const LONG done = g_stJobs;
        InterlockedExchange(&j.state, J_QUEUED);
        SetEvent(g_stGo);
        // the sim goes on while the job is queued or runs: recorded writes (W1 / W2 leaves) wait for +0x918. Not RfStep's
        // unrecorded kinds here (portal list IC, lot table, polygon cache): the cut's signature compare finds those only at
        // the next cut, and in the game their families are D5 nr bits, which this fake world does not count
        for (int n = (int)RfRnd(3); n > 0; n--) {
            EnterCriticalSection(&g_stCs);
            u8* q = RfQOf((int)RfRnd(g_rf.nLots), RfRnd(4));
            if (RfRnd(2)) RfIns(q, g_rf.fps[RfRnd(g_rf.nFps)], 1, true); else RfRem(q, g_rf.fps[RfRnd(g_rf.nFps)], true);
            LeaveCriticalSection(&g_stCs);
        }
        const ULONGLONG t0 = GetTickCount64();
        while (g_stJobs == done) {
            if (GetTickCount64() - t0 > 5000) {
                printf("threaded P4 HANG at job %d: worker phase %ld, job state %ld, hold %lu next %lu tail %lu pub %lu head %lu, "
                       "stale %ld needEpoch %ld\n", i, g_stWorkPhase, g_jobs[0].state, g_rpHoldAt, g_pcHoldNext, g_rpTail, g_rpPub,
                       g_rpHead, g_rpStale, g_rpNeedEpoch);
                assert(false);
            }
            Sleep(1);
        }
    }
    // one shadow per job, a "held" refusal for exactly the jobs that kept the lock, M1-M3 0 (stale mismatches are M5)
    const LONG recs1 = k.snShadow - recs0, held1 = k.snAbort[P4_AB_OTHER] - oth0;
    assert(recs1 == g_stJobs && held1 == g_stLeaks && g_stLeaks > 0);
    TestPcThreadedB2b();
    g_stStop = 1;
    SetEvent(g_stGo);
    WaitForSingleObject(work, 5000);
    g_rpStop = 1;
    SetEvent(g_rpEvent);
    WaitForSingleObject(cons, 5000);
    CloseHandle(work), CloseHandle(cons), CloseHandle(g_stGo), CloseHandle(g_rpEvent);
    g_rpEvent = savedEv;
    g_rpThreaded = false, g_rpStop = 0;
    g_rpHoldAt = 0, g_pcHoldNext = 0;
    g_spAcq = savedAcq, g_spRel = savedRel;
    g_p4WorldSlot = savedWs;
    DeleteCriticalSection(g_stLk);
    memset(g_stLk, 0, sizeof(CRITICAL_SECTION));
    k.snP4Mode = mode;
    // the sim's writes between START and the live plan make the clone at the cut older than what the live plan reads: each
    // such mismatch is "stale" (M5), never M1-M3 (the 0.33.1 M2 shape); leaked holds refuse their shadows
    printf("P4 threaded (sim + worker + replay consumer, real +0x918): serial 400 jobs, %ld shadows (%ld held); with back-to-back "
           "jobs: M0 %ld, M5 (stale) %ld, M1-M3 %ld / %ld / %ld, no hang\n", recs1, held1, k.snM[0] - m0, k.snM[5] - m5,
           k.snM[1] - m1, k.snM[2] - m2, k.snM[3] - m3);
    assert(k.snM[1] == m1 && k.snM[2] == m2 && k.snM[3] == m3 && k.snM[0] > m0);
}

// back-to-back phase of TestPcThreaded (threads, lock and events already set up): 400 jobs, each START as soon as no job is
// queued or running; shadows park behind the next job and run before its own; every job ends in a record or a busy drop
static void TestPcThreadedB2b() {
    Ts3nCounters& k = ts3native_counters;
    const LONG recs0 = k.snShadow, busy0 = k.snShadowBusy, park0 = g_p4ParkN, run0 = g_p4ParkRun, cap0 = k.snAbort[P4_AB_CAPTURE];
    LONG ab0[P4_AB_N], m0s[6], pr0[8];
    memcpy(ab0, (const void*)k.snAbort, sizeof(ab0)), memcpy(m0s, (const void*)k.snM, sizeof(m0s));
    memcpy(pr0, (const void*)k.snPcRefused, sizeof(pr0));
    g_stB2b = 1;
    const LONG jobs0 = g_stJobs;
    int started = 0;
    for (int i = 0; i < 400; i++) {
        if (i % 2) Sleep(2);  // every other job: the sim does other work first, so the worker has idle gaps (as in play)
        const ULONGLONG t0 = GetTickCount64();
        int slot = -1;
        for (;;) {  // no job queued or running, and a free slot (the other may be in its shadow)
            bool busy = false;
            for (int s2 = 0; s2 < 2; s2++) busy |= g_jobs[s2].state == J_QUEUED || g_jobs[s2].state == J_RUNNING;
            for (int s2 = 0; s2 < 2 && !busy && slot < 0; s2++) if (g_jobs[s2].state == J_FREE) slot = s2;
            if (!busy && slot >= 0) break;
            slot = -1;
            if (GetTickCount64() - t0 > 5000) {
                printf("threaded P4 (back to back) HANG at job %d: worker phase %ld, states %ld / %ld, hold %lu next %lu, parked %d\n", i,
                       g_stWorkPhase, g_jobs[0].state, g_jobs[1].state, g_rpHoldAt, g_pcHoldNext, (int)g_p4Parked);
                assert(false);
            }
            Sleep(0);
        }
        // the sim's writes after the last job's live plan (it took the job: no unrecorded write lands in a job's window;
        // in the game those families are D5 nr bits, not counted in this fake world)
        EnterCriticalSection(&g_stCs);
        for (int n = (int)RfRnd(3); n >= 0; n--) RfStep();
        LeaveCriticalSection(&g_stCs);
        AsyncJob& j = g_jobs[slot];
        memset(&j, 0, sizeof(j));
        j.kind = 1, j.seq = 5000 + i, j.x87 = GetX87(), j.mxcsr = _mm_getcsr();
        P4OnStart(&j);
        InterlockedExchange(&j.state, J_QUEUED);
        started++;
        SetEvent(g_stGo);
    }
    const ULONGLONG t0 = GetTickCount64();
    while (g_stJobs - jobs0 < started || g_p4Parked) {
        if (GetTickCount64() - t0 > 5000) { printf("threaded P4 (back to back): jobs or a parked shadow never finished\n"); assert(false); }
        Sleep(1);
    }
    g_stB2b = 0;
    const LONG recs = k.snShadow - recs0, busy = k.snShadowBusy - busy0, parks = g_p4ParkN - park0, runs = g_p4ParkRun - run0;
    printf("P4 0.34.1 back-to-back jobs: %d jobs, %ld records, %ld busy drops, %ld parked (%ld run later)\n", started, recs, busy,
           parks, runs);
    printf("  classes M0-M5:");
    for (int c2 = 0; c2 < 6; c2++) printf(" %ld", k.snM[c2] - m0s[c2]);
    printf("; acquire refusals 0-7:");
    for (int r2 = 0; r2 < 8; r2++) printf(" %ld", k.snPcRefused[r2] - pr0[r2]);
    printf("; refusals:");
    for (int r2 = 0; r2 < P4_AB_N; r2++) if (k.snAbort[r2] != ab0[r2]) printf(" %s %ld", kP4Ab[r2], k.snAbort[r2] - ab0[r2]);
    printf("\n");
    assert(recs + busy + (k.snAbort[P4_AB_CAPTURE] - cap0) == started && runs <= parks);  // parking follows the schedule: 0 parks is a valid run
    // 0.35.2 (docs/94 §21.17): STARTs while the other job may still be queued (only a free slot is waited for): the cut tries
    // +0x918 once (p4QCut, or p4QCutMiss when the worker took it first); every job still ends in a record, a busy drop or a
    // capture refusal, and the caller's M1-M3 assert covers these jobs (the queued job's writes make a record stale, M5)
    const LONG q0 = k.p4QCut, qm0 = k.p4QCutMiss, recsQ0 = k.snShadow, busyQ0 = k.snShadowBusy, capQ0 = k.snAbort[P4_AB_CAPTURE];
    const LONG bq0 = k.p4Borrow, bmq0 = k.p4BorrowMiss, bdq0 = k.p4BorrowDirty;
    const LONG jobsQ0 = g_stJobs;
    LONG mq0[6];
    memcpy(mq0, (const void*)k.snM, sizeof(mq0));
    g_stB2b = 1;
    int startedQ = 0;
    for (int i = 0; i < 200; i++) {
        const ULONGLONG tq = GetTickCount64();
        int slot = -1;
        while (slot < 0) {
            for (int s2 = 0; s2 < 2 && slot < 0; s2++) if (g_jobs[s2].state == J_FREE) slot = s2;
            if (slot >= 0) break;
            if (GetTickCount64() - tq > 5000) {
                printf("threaded P4 (queued STARTs) HANG at job %d: worker phase %ld, states %ld / %ld\n", i, g_stWorkPhase,
                       g_jobs[0].state, g_jobs[1].state);
                assert(false);
            }
            Sleep(0);
        }
        // 0.35.11: RfStep's unrecorded kinds only with no job between its cut and its live plan (as the phases above); with
        // the other job queued or running, recorded writes only (they make its record stale, M5). Before, an unrecorded write
        // in a queued job's window (or before a START that shares its cut) made the live plan differ from the clone: M1 ~8%
        EnterCriticalSection(&g_stCs);
        bool inWin = false;
        for (int s2 = 0; s2 < 2; s2++) inWin |= g_jobs[s2].state == J_QUEUED || g_jobs[s2].state == J_RUNNING;
        for (int n = (int)RfRnd(3); n >= 0; n--) {
            if (!inWin) { RfStep(); continue; }
            u8* q = RfQOf((int)RfRnd(g_rf.nLots), RfRnd(4));
            if (RfRnd(2)) RfIns(q, g_rf.fps[RfRnd(g_rf.nFps)], 1, true); else RfRem(q, g_rf.fps[RfRnd(g_rf.nFps)], true);
        }
        LeaveCriticalSection(&g_stCs);
        AsyncJob& j = g_jobs[slot];
        memset(&j, 0, sizeof(j));
        j.kind = 1, j.seq = 9000 + i, j.x87 = GetX87(), j.mxcsr = _mm_getcsr();
        P4OnStart(&j);
        InterlockedExchange(&j.state, J_QUEUED);
        startedQ++;
        // 0.35.11: every 4th job no wake-up (the worker's 20 ms poll takes it), so the next START finds it still queued and
        // takes the queued-job cut; with a wake-up each time, under CPU load every START found the job running (0 cuts, 190 shared)
        if (i % 4) SetEvent(g_stGo);
    }
    const ULONGLONG tq = GetTickCount64();
    while (g_stJobs - jobsQ0 < startedQ || g_p4Parked) {
        if (GetTickCount64() - tq > 5000) { printf("threaded P4 (queued STARTs): jobs or a parked shadow never finished\n"); assert(false); }
        Sleep(1);
    }
    g_stB2b = 0;
    const LONG recsQ = k.snShadow - recsQ0, busyQ = k.snShadowBusy - busyQ0;
    // 0.35.6: STARTs while the other job runs share its pending cut (p4Borrow); the caller's M1-M3 assert covers their records
    printf("P4 0.35.2 queued STARTs: %d jobs, %ld cuts behind a queued job, %ld tries missed, %ld records, %ld busy drops; "
           "0.35.6 shared cuts %ld (none to share %ld, refused dirty %ld)\n", startedQ, k.p4QCut - q0, k.p4QCutMiss - qm0, recsQ,
           busyQ, k.p4Borrow - bq0, k.p4BorrowMiss - bmq0, k.p4BorrowDirty - bdq0);
    printf("  queued STARTs classes M0-M5:");
    for (int c2 = 0; c2 < 6; c2++) printf(" %ld", k.snM[c2] - mq0[c2]);
    printf("\n");
    assert(recsQ + busyQ + (k.snAbort[P4_AB_CAPTURE] - capQ0) == startedQ && k.p4QCut + k.p4QCutMiss > q0 + qm0);
}

// 0.33.1: the grid copy's records own their polygon sets (both levels private; 0.32.8 fault at 0x7CDAA8 read a freed one)
static void TestPcGridPoly() {
    static u8 P[0x110], cell[32];
    static DWORD bk[2];
    static __declspec(align(16)) u8 rec[2][0x50];
    static float pts[2][2][6];
    static u8 polys[2][2][16];
    memset(P, 0, sizeof(P)), memset(cell, 0, sizeof(cell)), memset(rec, 0, sizeof(rec));
    for (int r = 0; r < 2; r++) {
        for (int p = 0; p < 2; p++) {
            for (int i = 0; i < 6; i++) pts[r][p][i] = (float)(r * 100 + p * 10 + i);
            *(float**)polys[r][p] = pts[r][p], *(float**)(polys[r][p] + 4) = *(float**)(polys[r][p] + 8) = pts[r][p] + 6;
            *(DWORD*)(polys[r][p] + 12) = 0xA110C;
        }
        *(u8**)(rec[r] + 8) = polys[r][0], *(u8**)(rec[r] + 0xC) = *(u8**)(rec[r] + 0x10) = polys[r][0] + 32;
        *(DWORD*)(rec[r] + 0x38) = 0x1000 + r;
    }
    *(u8**)(rec[0] + 0x48) = rec[1];
    bk[0] = (DWORD)(uintptr_t)rec[0], bk[1] = 0xFFFFFFFF;
    *(DWORD**)(cell + 4) = bk, *(DWORD*)(cell + 8) = 1;
    *(u8**)(P + 0xDC) = cell, *(u8**)(P + 0xE0) = cell + 32;
    PcGrid* gr = PcGridCopy(P);
    assert(gr && gr->nCells == 1);
    float want[2][2][6];
    memcpy(want, pts, sizeof(pts));
    memset(pts, 0xCC, sizeof(pts)), memset(polys, 0, sizeof(polys));  // the sim freed and reused the live sets after the cut
    const u8* c = (const u8*)(uintptr_t)(*(DWORD**)(gr->cells + 4))[0];
    for (int r = 0; r < 2; r++, c = RpP(c + 0x48)) {
        assert(c && c != rec[r] && RpD(c + 0x38) == 0x1000 + (DWORD)r);
        const u8* pb = RpP(c + 8);
        assert(pb && pb != polys[r][0] && RpP(c + 0xC) == pb + 32 && RpP(c + 0x10) == pb + 32);
        for (int p = 0; p < 2; p++) {
            const float* v = (const float*)RpP(pb + p * 16);
            assert(v && v != pts[r][p] && RpP(pb + p * 16 + 4) == (const u8*)(v + 6) && RpD(pb + p * 16 + 12) == 0xA110C);
            assert(!memcmp(v, want[r][p], sizeof(want[r][p])));
        }
    }
    assert(!c);
    PcGridRel(gr);
    // a broken set (end before begin) fails the copy: the cut is refused, never a half-live copy
    memcpy(pts, want, sizeof(pts));
    *(u8**)(rec[1] + 0xC) = polys[1][0] - 16;
    assert(!PcGridCopy(P));
    printf("P4 0.33.1: grid records' polygon sets deep-copied (live freed after the cut: copy intact); a broken set refuses the copy\n");
}

// 0.35.4 (docs/94 §21.19): the obstacle sets of an M2/M3 record. replay-fake.h's world and its clone after a cut hold the
// same footprints (same id, class, point hash); a polygon point moved in place on a live footprint (a write no replay record
// carries) shows as that footprint's hash differing; the record line carries "obst" once
static const P4Ob* TObFind(const P4ObSet& s, const P4Ob& o) {
    for (int i = 0; i < s.n; i++) if (s.o[i].id == o.id && s.o[i].vt == o.vt) return &s.o[i];
    return NULL;
}
static void TestP4Obst() {
    Ts3nCounters& k = ts3native_counters;
    const LONG bytes0 = g_rpBytes;  // TestP4's provider may still hold blocks of its own
    RfSetup(21);
    RfCut();
    assert(g_rpClone && g_rpClone->p);
    static u8 cloneWorld[0xA00];  // a routing world stand-in: the clone planner at +0x95C
    *(u8**)(cloneWorld + 0x95C) = g_rpClone->p;
    g_p4ObBox[0] = g_p4ObBox[1] = -1.0f, g_p4ObBox[2] = g_p4ObBox[3] = 65.0f;
    P4ObWalk(g_rf.world, g_p4ObL);
    P4ObWalk(cloneWorld, g_p4ObC);
    assert(g_p4ObL.n > 0 && g_p4ObL.more >= 0 && g_p4ObC.more >= 0);
    int same = 0;
    for (int i = 0; i < g_p4ObC.n; i++) {
        const P4Ob* l = TObFind(g_p4ObL, g_p4ObC.o[i]);
        if (!l) continue;
        for (int j = 0; j < g_p4ObL.n && l->h != g_p4ObC.o[i].h; j++)  // the fake world reuses ids: the same id and hash
            if (g_p4ObL.o[j].id == g_p4ObC.o[i].id && g_p4ObL.o[j].h == g_p4ObC.o[i].h) l = &g_p4ObL.o[j];
        assert(l->h == g_p4ObC.o[i].h && l->inv == 0 && !memcmp(l->b, g_p4ObC.o[i].b, sizeof(l->b)));
        same++;
    }
    assert(same > 0 && (g_p4ObL.more || g_p4ObC.more || (same == g_p4ObL.n && same == g_p4ObC.n)));
    // in-place move of one live point (no W1/W2): only the hash / bounds of that footprint change
    const P4Ob target = g_p4ObL.o[0];
    u8* fp = NULL;
    for (int i = 0; i < g_rf.nFps && !fp; i++)
        if (g_rf.fps[i] && *(DWORD*)(g_rf.fps[i] + 0x14) == (DWORD)target.id && *(DWORD*)g_rf.fps[i] == target.vt) fp = g_rf.fps[i];
    assert(fp);
    float* v = *(float**)(*(u8**)(*(u8**)(fp + 0x38)));  // level 0, polygon 0, point 0
    v[0] += 0.25f;
    P4ObWalk(g_rf.world, g_p4ObL);
    const P4Ob* moved = TObFind(g_p4ObL, target);
    const P4Ob* old = TObFind(g_p4ObC, target);
    assert(moved && old && moved->h != old->h);
    v[0] -= 0.25f;
    // the capture and the record: one obstacle block after "copy", live read under the (fake) lock
    static P4Job job;
    memset(&job, 0, sizeof(job));
    g_p4Live.valid = g_p4Copy.valid = true;
    g_p4Live.nPaths = g_p4Copy.nPaths = 0;
    g_p4Live.nNodes = g_p4Copy.nNodes = 2;
    g_p4Live.n[0].x = g_p4Live.n[0].z = 10.0f, g_p4Live.n[1].x = g_p4Live.n[1].z = 12.0f;
    g_p4Copy.n[0] = g_p4Live.n[0], g_p4Copy.n[1] = g_p4Live.n[1];
    const LONG ob0 = k.p4Obst;
    P4ObCapture(job, cloneWorld);
    assert(k.p4Obst == ob0 + 1 && g_p4ObLive == 1 && g_p4ObOn && g_p4ObBox[0] == 9.0f && g_p4ObBox[3] == 13.0f);
    if (!g_p4Buf) g_p4Buf = (char*)VirtualAlloc(NULL, kP4BufCap, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    P4Record(job, NULL, 0);
    g_p4Buf[g_p4Len] = 0;
    assert(!g_p4ObOn && strstr(g_p4Buf, ",\"obst\":{\"box\":[9,9,13,13],\"liveState\":\"ok\"") && strstr(g_p4Buf, "\"live\":[[\"0x"));
    P4Record(job, NULL, 0);  // the next record has none
    g_p4Buf[g_p4Len] = 0;
    assert(!strstr(g_p4Buf, "\"obst\""));
    g_p4Live.valid = g_p4Copy.valid = false;
    g_p4Live.nNodes = g_p4Copy.nNodes = 0;
    printf("P4 obstacle sets: live %d (+%d), clone %d (+%d), %d equal, in-place move found\n", g_p4ObL.n, g_p4ObL.more, g_p4ObC.n,
           g_p4ObC.more, same);
    RfTeardown();
    assert(g_rpBytes == bytes0);
}

// docs/94 §22.27 (I-94-499): [RouteSnap] DoorDump. Two lots, one portal each, live and clone lot tables; the clone's lot B
// has +0x8F = 0 while live has 1 (the stale-byte case): the dump names lot B in f8fDiff, both doors in ids, the live ask the
// clone did not use, the portal records on both sides and the door mirror; capped; box mode dumps a matched plan
static LONG __fastcall TDdAcq(void*, void*, const DWORD*) { return 1; }
static LONG __fastcall TDdRel(void*, void*) { return 0; }
static void TestP4DoorDump() {
    static u8 worlds[2][0xA00], Ps[2][0x100], nodes[2][2][0x194], pts[2][0x80];
    static DWORD bks[2][3], vecs[2][2][1];
    const unsigned __int64 lotA = 0x285900360000AAAAull, lotB = 0x6C11003300BBBB00ull;
    const unsigned __int64 doorA = 0x285900362D9533C0ull, doorB = 0x6C1100337D22F4D0ull;
    memset(worlds, 0, sizeof(worlds)), memset(Ps, 0, sizeof(Ps)), memset(nodes, 0, sizeof(nodes)), memset(pts, 0, sizeof(pts));
    for (int p = 0; p < 2; p++) {
        const unsigned __int64 own = p ? doorB : doorA;
        *(DWORD*)(pts[p] + 8) = (DWORD)own, *(DWORD*)(pts[p] + 0xC) = (DWORD)(own >> 32);
        *(float*)(pts[p] + 0x2C) = 1.5f + p;
        *(WORD*)(pts[p] + 0x48) = (WORD)(3 + p), *(WORD*)(pts[p] + 0x4A) = (WORD)(7 + p);
        *(DWORD*)(pts[p] + 0x78) = 0x0F00u + p, *(DWORD*)(pts[p] + 0x7C) = 0;
    }
    for (int w = 0; w < 2; w++) {  // 0 live, 1 clone
        *(u8**)(worlds[w] + 0x95C) = Ps[w];
        for (int l = 0; l < 2; l++) {
            u8* nd = nodes[w][l];
            const unsigned __int64 key = l ? lotB : lotA;
            *(DWORD*)nd = (DWORD)key, *(DWORD*)(nd + 4) = (DWORD)(key >> 32);
            u8* g = nd + 0x10;
            g[0x8C] = 1, g[0x8F] = (u8)(w == 1 && l == 1 ? 0 : 1);
            vecs[w][l][0] = (DWORD)(uintptr_t)pts[l];
            *(DWORD**)(g + 0x44) = vecs[w][l], *(DWORD**)(g + 0x48) = vecs[w][l] + 1;
            bks[w][l] = (DWORD)(uintptr_t)nd;
        }
        bks[w][2] = 0;
        *(DWORD**)(Ps[w] + 0x4C) = bks[w], *(DWORD*)(Ps[w] + 0x50) = 2;
    }
    const u8* const* slot0 = g_spWorldSlot;
    const LockAcquireFn acq0 = g_spAcq;
    const SpRelFn rel0 = g_spRel;
    static const u8* liveSlot;
    liveSlot = worlds[0];
    g_spWorldSlot = &liveSlot, g_spAcq = TDdAcq, g_spRel = (SpRelFn)TDdRel;
    // door mirror: one record for door B
    const int dn0 = g_spDoorN;
    static SpDoor mir;
    memset(&mir, 0, sizeof(mir));
    mir.obj = doorB, mir.kind = 2, mir.lock = 4, mir.owner = 0x1234;
    SpDoorLot* ml = SpDoorLotFor(lotB, true);
    assert(ml);
    ml->d = &mir, ml->n = 1, ml->valid = 1, ml->epoch = g_spEpoch;
    // the job: goal at (10, 20), one live ask for door B (answer 1 = locked) the clone did not replay
    static P4Job job;
    memset(&job, 0, sizeof(job));
    static float goal[16];
    goal[0] = 10.0f, goal[1] = 0.0f, goal[2] = 20.0f;
    job.goals[0] = (DWORD)(uintptr_t)goal, job.goals[1] = (DWORD)(uintptr_t)(goal + 16);
    job.seq = 66;
    P4Door& d = job.doors[job.nDoors++];
    d.a[0] = (DWORD)(lotB & 0xFFFFFFFF), d.a[1] = (DWORD)(lotB >> 32), d.a[4] = (DWORD)(doorB & 0xFFFFFFFF), d.a[5] = (DWORD)(doorB >> 32);
    d.a[6] = 3, d.a[7] = 7;
    d.eax = 1, d.used = 0;
    g_p4DdOn = true, g_p4DdBoxOn = false, g_p4DdLeft = 1;
    P4DdPlan(job, worlds[0]);
    assert(job.ddN == 2 && job.dd[1].key == lotB && job.dd[1].f8f == 1);
    g_p4Live.valid = g_p4Copy.valid = true;
    g_p4Live.nNodes = g_p4Copy.nNodes = 0, g_p4Live.out2 = 0;
    g_p4Live.nPaths = g_p4Copy.nPaths = 2;
    memset(g_p4Live.p, 0, 2 * sizeof(P4Path)), memset(g_p4Copy.p, 0, 2 * sizeof(P4Path));
    g_p4Live.p[0].obj = g_p4Copy.p[0].obj = 0x6C1100337D2360D0ull;  // shared door: not in ids
    g_p4Live.p[1].obj = doorA, g_p4Copy.p[1].obj = doorB;
    P4DdCapture(job, worlds[1], 2);
    assert(g_p4DdRec && g_p4DdNIds == 2 && g_p4DdIds[0] == doorA && g_p4DdIds[1] == doorB);
    assert(g_p4DdC.state == 1 && g_p4DdL.state == 1 && g_p4DdC.nAll == 2 && g_p4DdL.nAll == 2 && g_p4DdC.nRec == 2 && g_p4DdL.nRec == 2);
    P4DdDiff df[kDdDiff];
    assert(P4DdDiffs(job, df) == 1 && df[0].key == lotB && df[0].clone == 0 && df[0].plan == 1 && df[0].now == 1);
    if (!g_p4Buf) g_p4Buf = (char*)VirtualAlloc(NULL, kP4BufCap, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    P4Record(job, NULL, 0);
    g_p4Buf[g_p4Len] = 0;
    assert(!g_p4DdRec && strstr(g_p4Buf, ",\"doors\":{\"why\":\"m2\",\"cloneState\":\"ok\",\"liveState\":\"ok\",\"ids\":[\"0x285900362D9533C0\",\"0x6C1100337D22F4D0\"]"));
    assert(strstr(g_p4Buf, "\"asksN\":1,\"unused\":1,\"asks\":[[\"0x6C11003300BBBB00\",\"0x6C1100337D22F4D0\",3,7,0,1,0]]"));
    assert(strstr(g_p4Buf, "\"f8fDiffN\":1,\"f8fDiff\":[[\"0x6C11003300BBBB00\",0,1,1]]"));
    assert(strstr(g_p4Buf, "{\"lot\":\"0x6C11003300BBBB00\",\"f8f\":0,\"f8c\":1,\"portals\":[[\"0x6C1100337D22F4D0\",\"0x"));
    assert(strstr(g_p4Buf, "\"live\":[{\"lot\":\"0x285900360000AAAA\",\"f8f\":1"));
    assert(strstr(g_p4Buf, ",2.5,4,8,\"0x0000000000000F01\",0]"));
    assert(strstr(g_p4Buf, "\"mirror\":[[\"0x6C1100337D22F4D0\",\"0x6C11003300BBBB00\",1,1,2,4,\"0x0000000000001234\""));
    const char* line = strstr(g_p4Buf, "\"doors\"");
    printf("P4 door dump record: %.200s...\n", line);
    // capped: DoorDumpMax=1 is spent, the next M2 has no block
    P4DdCapture(job, worlds[1], 2);
    assert(!g_p4DdRec);
    P4Record(job, NULL, 0);
    g_p4Buf[g_p4Len] = 0;
    assert(!strstr(g_p4Buf, "\"doors\""));
    // box mode: a matched plan (class 0) whose goal is in the box is dumped, one outside is not; off = never
    g_p4DdLeft = 5, g_p4DdBoxOn = true;
    g_p4DdBox[0] = 9.0f, g_p4DdBox[1] = 19.0f, g_p4DdBox[2] = 11.0f, g_p4DdBox[3] = 21.0f;
    g_p4Copy.p[1].obj = doorA;
    P4DdCapture(job, worlds[1], 0);
    assert(g_p4DdRec && !strcmp(g_p4DdWhy, "box") && g_p4DdNIds == 2);  // nothing differs: every path owner
    g_p4DdRec = false;
    goal[0] = 30.0f;
    P4DdCapture(job, worlds[1], 0);
    assert(!g_p4DdRec);
    g_p4DdOn = false;
    P4DdCapture(job, worlds[1], 2);
    assert(!g_p4DdRec);
    // a fault in the walk (a broken bucket pointer) is a "fault" state, never a crash
    g_p4DdOn = true;
    *(DWORD*)(Ps[1] + 0x4C) = 0x10;
    P4DdCapture(job, worlds[1], 2);
    assert(g_p4DdRec && g_p4DdC.state == -1);
    g_p4DdRec = false, g_p4DdOn = false, g_p4DdBoxOn = false;
    g_p4Live.valid = g_p4Copy.valid = false;
    g_p4Live.nPaths = g_p4Copy.nPaths = 0;
    ml->d = NULL, ml->n = 0, ml->valid = 0;
    g_spDoorN = dn0;
    g_spWorldSlot = slot0, g_spAcq = acq0, g_spRel = rel0;
    printf("P4 door dump: stale clone +0x8F named, ids, unused ask, portals, mirror, cap, box mode, fault state\n");
}

// 0.36.50 (docs/94 §24.8.4, I-94-521): one lot at world (1200, 1700) whose footprint polygons are lot-local, as the plan 5849
// route near world (1250.2, 1731.8). (a) "obst": the world box misses the points (the old check listed nothing), the lot box
// lists the footprint as 'l', walked 2, nearest first, live lock-key set vs clone NULL. (b) M2LotDump: the start lot's Q leaf
// entry in cell [30,62]^2 on both sides, one diff with bit 1 (NULL vs set); a clone copy standing for an older live set with
// the same ids is bit 2 only; a clone leaf without the entry is "only live"; box mode dumps a matched plan, outside it not
static void TestP4LotDump() {
    static_assert(offsetof(Ts3nCounters, ldDumps) == 0x4B3C && offsetof(Ts3nCounters, ldObL) == 0x4B60 &&
                      offsetof(Ts3nCounters, ldSpare) == 0x4B64 && sizeof(Ts3nCounters) == 6112 * 4,
                  "counters v121 layout after v120 (docs/registry.md)");
    assert(ts3native_counters.version == 912);
    struct Side {
        u8 world[0xA00], P[0x100], M[0x20], Q[0x60], root[0x20], mid[0x20], leaf[0x20], fme[2][0x20], fp[2][0x50], cache[2][0x24];
        u8 poly[2][16], mnode[2][0x14];
        __declspec(align(16)) u8 node[0x200];
        DWORD bk[2], mbk[1], qv[2], arr[3];
        float pts[2][8];
    };
    static Side sd[2];  // 0 live, 1 clone
    static DWORD vt[0x80 / 4];
    static u8 set[2][0x30], ids[16], hdr[16 + 0x30];
    vt[0] = (DWORD)(uintptr_t)RfLevels1;
    RpCls cls[1] = {{(DWORD)(uintptr_t)vt, 0x44, 1, 0}};
    const RpCls* savedCls = g_rpCls;
    const int savedN = g_rpNCls;
    g_rpCls = cls, g_rpNCls = 1;
    const unsigned __int64 lot = 0x6C1100342B1C14A0ull;
    memset(sd, 0, sizeof(sd));
    *(DWORD*)ids = 0x111, *(DWORD*)(ids + 8) = 0x222;
    for (int s = 0; s < 2; s++) {  // live set, and an older live set with the same ids (the U1 case: replaced after the cut)
        *(u8**)(set[s] + 8) = ids, *(u8**)(set[s] + 0xC) = ids + 16;
    }
    for (int w = 0; w < 2; w++) {
        Side& x = sd[w];
        *(u8**)(x.world + 0x95C) = x.P;
        *(u8**)(x.P + 0x24) = x.M, *(DWORD**)(x.P + 0x4C) = x.bk, *(DWORD*)(x.P + 0x50) = 1;
        x.bk[0] = (DWORD)(uintptr_t)x.node, x.bk[1] = 0;
        *(DWORD*)x.node = (DWORD)(lot & 0xFFFFFFFF), *(DWORD*)(x.node + 4) = (DWORD)(lot >> 32);
        u8* g = x.node + 0x10;
        float* m = (float*)(g + 0xA0);
        m[0] = m[5] = m[10] = m[15] = 1.0f, m[12] = -1200.0f, m[14] = -1700.0f;  // world -> lot
        x.qv[0] = (DWORD)(uintptr_t)x.Q;
        *(DWORD**)(g + 0x74) = x.qv, *(DWORD**)(g + 0x78) = x.qv + 1;
        float* qb = (float*)(x.Q + 4);
        qb[0] = qb[1] = -2.0f, qb[2] = qb[3] = 66.0f;
        *(float*)(x.Q + 0x38) = 128.0f;
        *(u8**)(x.Q + 0x24) = x.root;
        x.root[4] = 1, *(u8**)(x.root + 0xC) = x.mid;      // child 0: [-2, 62]^2
        x.mid[4] = 1, *(u8**)(x.mid + 0xC + 12) = x.leaf;  // its child 3 (dx 1, dz 1): [30, 62]^2
        x.arr[0] = (DWORD)(uintptr_t)x.fme[0], x.arr[1] = 0x00FF00, x.arr[2] = 0x3;
        *(DWORD*)(x.leaf + 8) = (DWORD)(uintptr_t)x.arr | 1, *(DWORD*)(x.leaf + 0xC) = 1;
        *(u8**)(x.M + 4) = (u8*)x.mbk, *(DWORD*)(x.M + 8) = 1;
        x.mbk[0] = (DWORD)(uintptr_t)x.mnode[0];
        *(u8**)(x.mnode[0] + 0x10) = x.mnode[1];
        static const float kPts[2][8] = {{50.5f, 32.0f, 51.0f, 32.0f, 51.0f, 32.5f, 50.5f, 32.5f},   // near the route, lot frame
                                         {10.0f, 10.0f, 11.0f, 10.0f, 11.0f, 11.0f, 10.0f, 11.0f}};  // far: walked, not listed
        for (int f = 0; f < 2; f++) {
            u8* fp = x.fp[f];
            *(DWORD*)fp = (DWORD)(uintptr_t)vt;
            *(DWORD*)(fp + 0x14) = f ? 0x6911605 : 0x1401605, *(DWORD*)(fp + 0x18) = 0x6C110034;
            *(DWORD*)(fp + 0x34) = 3;
            *(u8**)(fp + 0x38) = x.cache[f];
            memcpy(x.pts[f], kPts[f], sizeof(kPts[f]));
            *(u8**)x.poly[f] = (u8*)x.pts[f], *(u8**)(x.poly[f] + 4) = (u8*)(x.pts[f] + 8);
            *(u8**)x.cache[f] = x.poly[f], *(u8**)(x.cache[f] + 4) = x.poly[f] + 16, x.cache[f][0x20] = 1;
            *(u8**)(x.fme[f] + 8) = fp, *(DWORD*)(x.fme[f] + 0x10) = 0x10001;
            *(DWORD*)x.mnode[f] = *(DWORD*)(fp + 0x14), *(DWORD*)(x.mnode[f] + 4) = 0x6C110034;
            *(u8**)(x.mnode[f] + 8) = x.fme[f];
        }
    }
    *(u8**)(sd[0].fp[0] + 0x20) = set[0];  // live: a lock-key set; clone: NULL (U1: the copy kept NULL)
    const u8* const* slot0 = g_spWorldSlot;
    const LockAcquireFn acq0 = g_spAcq;
    const SpRelFn rel0 = g_spRel;
    static const u8* liveSlot;
    liveSlot = sd[0].world;
    g_spWorldSlot = &liveSlot, g_spAcq = TDdAcq, g_spRel = (SpRelFn)TDdRel;
    static P4Job job;
    memset(&job, 0, sizeof(job));
    static float st[16], gl[16];
    st[0] = 1250.48047f, st[1] = 69.6178f, st[2] = 1731.79663f, gl[0] = 1250.58398f, gl[1] = 69.6178f, gl[2] = 1733.54614f;
    job.starts[0] = (DWORD)(uintptr_t)st, job.starts[1] = (DWORD)(uintptr_t)(st + 16);
    job.goals[0] = (DWORD)(uintptr_t)gl, job.goals[1] = (DWORD)(uintptr_t)(gl + 16);
    job.seq = 5849;
    g_p4Live.valid = g_p4Copy.valid = true;
    g_p4Live.nPaths = g_p4Copy.nPaths = 0, g_p4Live.out2 = 0;
    g_p4Live.nNodes = g_p4Copy.nNodes = 2;
    g_p4Live.n[0].x = 1250.21f, g_p4Live.n[0].z = 1731.83f, g_p4Live.n[1].x = 1250.58f, g_p4Live.n[1].z = 1733.55f;
    g_p4Copy.n[0] = g_p4Live.n[0], g_p4Copy.n[1] = g_p4Live.n[1];
    // (a) the obstacle sets
    Ts3nCounters& k = ts3native_counters;
    const LONG obL0 = k.ldObL;
    P4ObCapture(job, sd[1].world);
    assert(g_p4ObLive == 1 && g_p4ObC.walked == 2 && g_p4ObL.walked == 2 && g_p4ObC.haveL && g_p4ObC.lot == lot);
    assert(g_p4ObC.n == 1 && g_p4ObL.n == 1 && g_p4ObC.o[0].fr == 'l' && g_p4ObC.o[0].id == 0x6C11003401401605ull);
    assert(!P4BoxHit(g_p4ObC.o[0].b, g_p4ObBox));  // what the 0.35.4 check compared: no hit, the block stayed empty
    assert(g_p4ObC.nNear == 2 && g_p4ObC.nearO[0].id == 0x6C11003401401605ull && g_p4ObC.nearD[0] < g_p4ObC.nearD[1]);
    assert(!g_p4ObC.o[0].k.set && g_p4ObL.o[0].k.set == (DWORD)(uintptr_t)set[0] && g_p4ObL.o[0].k.nA == 2 && g_p4ObL.o[0].k.nB == 0);
    assert(k.ldObL == obL0 + 2);
    if (!g_p4Buf) g_p4Buf = (char*)VirtualAlloc(NULL, kP4BufCap, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    // (b) the lot dump on the same M2
    g_p4LdOn = true, g_p4LdBoxOn = false, g_p4LdLeft = 8;
    const LONG d0 = k.ldDumps, kd0 = k.ldKeyDiff, ko0 = k.ldKeyOrigin;
    P4LdCapture(job, sd[1].world, 2);
    assert(g_p4LdRec && !strcmp(g_p4LdWhy, "m2") && g_p4LdC.state == 1 && g_p4LdL.state == 1 && g_p4LdC.lot == lot);
    assert(g_p4LdC.n == 1 && g_p4LdL.n == 1 && g_p4LdC.nQ == 1 && g_p4LdC.e[0].cell[0] == 30.0f && g_p4LdC.e[0].cell[3] == 62.0f);
    assert(g_p4LdND == 1 && g_p4LdD[0].bits == 1 && k.ldDumps == d0 + 1 && k.ldKeyDiff == kd0 + 1 && k.ldKeyOrigin == ko0);
    P4Record(job, NULL, 0);
    g_p4Buf[g_p4Len] = 0;
    assert(!g_p4LdRec && !g_p4ObOn);
    assert(strstr(g_p4Buf, ",\"obst\":{\"box\":[") && strstr(g_p4Buf, ",\"l\",null]") == NULL);  // the child class alone prints null
    assert(strstr(g_p4Buf, "\"clone\":[[\"0x6C11003401401605\",") && strstr(g_p4Buf, ",\"l\",[\"0x00000000\",\"0x00000000\",0,0,"));
    assert(strstr(g_p4Buf, "\"walkC\":{\"walked\":2,\"lot\":\"0x6C1100342B1C14A0\",\"lbox\":["));
    assert(strstr(g_p4Buf, ",\"lotdump\":{\"why\":\"m2\",\"pt\":[") && strstr(g_p4Buf, "\"diffs\":[[1,0,0]]}"));
    const char* ld = strstr(g_p4Buf, "\"lotdump\"");
    assert(ld && strstr(ld, "\"ents\":[[0,\"0x") && strstr(ld, "[30,30,62,62]"));
    printf("P4 lot dump record: %.220s...\n", ld);
    // the clone holds our copy of an older live set with the same ids: key origin only (bit 2)
    *(u8**)hdr = set[1], *(DWORD*)(hdr + 4) = kP4KeyMagic;
    memcpy(hdr + 16, set[1], 0x30);
    *(u8**)(sd[1].fp[0] + 0x20) = hdr + 16;
    P4LdCapture(job, sd[1].world, 2);
    assert(g_p4LdND == 1 && g_p4LdD[0].bits == 2 && g_p4LdC.e[0].k.origin == (DWORD)(uintptr_t)set[1] && k.ldKeyOrigin == ko0 + 1);
    *(u8**)(sd[1].fp[0] + 0x20) = set[0] + 0;  // not a copy: origin = the set itself, equal to live
    P4LdCapture(job, sd[1].world, 2);
    assert(g_p4LdND == 0);
    // a clone leaf without the entry: "only live"
    *(DWORD*)(sd[1].leaf + 8) = 1, *(DWORD*)(sd[1].leaf + 0xC) = 0;
    P4LdCapture(job, sd[1].world, 2);
    assert(g_p4LdC.n == 0 && g_p4LdL.n == 1 && g_p4LdND == 1 && g_p4LdD[0].c == -1 && g_p4LdD[0].l == 0);
    // box mode: a matched compare whose start is in the box is dumped, one outside is not; off = never; capped
    g_p4LdRec = false, g_p4LdBoxOn = true;
    g_p4LdBox[0] = 1250.0f, g_p4LdBox[1] = 1731.3f, g_p4LdBox[2] = 1251.0f, g_p4LdBox[3] = 1734.0f;
    P4LdCapture(job, sd[1].world, 0);
    assert(g_p4LdRec && !strcmp(g_p4LdWhy, "box"));
    g_p4LdRec = false;
    g_p4LdBox[0] = 1260.0f, g_p4LdBox[2] = 1270.0f;
    P4LdCapture(job, sd[1].world, 0);
    assert(!g_p4LdRec);
    g_p4LdLeft = 0;
    P4LdCapture(job, sd[1].world, 2);
    assert(!g_p4LdRec);
    g_p4LdLeft = 8, g_p4LdOn = false;
    P4LdCapture(job, sd[1].world, 2);
    assert(!g_p4LdRec);
    // 0.36.54 (docs/94 §24.12): Q vector indexed by level - g+0x40 (0x6320D0); base -1 with the slot-0 (basement) Q NULL:
    // the level-0 Q in slot 1 is found (0.36.51 read slot 0 only: "no lot"), the lot table records base, slots and mask
    g_p4LdOn = true, g_p4LdLeft = 8;
    for (int w = 0; w < 2; w++) {
        u8* g = sd[w].node + 0x10;
        sd[w].qv[0] = 0, sd[w].qv[1] = (DWORD)(uintptr_t)sd[w].Q;
        *(int*)(g + 0x40) = -1, *(DWORD**)(g + 0x78) = sd[w].qv + 2;
    }
    P4LdCapture(job, sd[1].world, 2);
    assert(g_p4LdRec && g_p4LdC.state == 1 && g_p4LdL.state == 1 && g_p4LdC.lot == lot && g_p4LdC.nQ == 2 && g_p4LdL.n == 1);
    assert(g_p4LdC.why.qSlot == 1 && g_p4LdC.why.slot0Null == 1 && g_p4LdC.why.withQ == 1 && g_p4LdC.why.lots == 1);
    assert(g_p4LdC.why.lot[0].key == lot && g_p4LdC.why.lot[0].base == -1 && g_p4LdC.why.lot[0].nSlots == 2 && g_p4LdC.why.lot[0].mask == 2);
    for (int w = 0; w < 2; w++) {  // back to one slot, base 0
        u8* g = sd[w].node + 0x10;
        sd[w].qv[0] = (DWORD)(uintptr_t)sd[w].Q;
        *(int*)(g + 0x40) = 0, *(DWORD**)(g + 0x78) = sd[w].qv + 1;
    }
    g_p4LdRec = false;
    // a fault in the walk (a broken lot table) is a "fault" state, never a crash
    g_p4LdOn = true;
    *(DWORD*)(sd[1].P + 0x4C) = 0x10;
    P4LdCapture(job, sd[1].world, 2);
    assert(g_p4LdRec && g_p4LdC.state == -1);
    g_p4LdRec = false, g_p4LdOn = false, g_p4LdBoxOn = false;
    g_p4Live.valid = g_p4Copy.valid = false;
    g_p4Live.nNodes = g_p4Copy.nNodes = 0;
    g_spWorldSlot = slot0, g_spAcq = acq0, g_spRel = rel0;
    g_rpCls = savedCls, g_rpNCls = savedN;
    printf("P4 lot dump + obst frame: lot-local footprint listed ('l', the world box misses it), lock key NULL vs set, key origin, "
           "only live, box mode, cap, fault state\n");
}

// 0.35.21 (docs/94 §22.24, I-94-484): on PcSetup's world (provider registered, every cut audits every lot)
// (a) the clone-run stop sites: ApplyLotUpdate names its lot and keeps the clone (P4LazyKeep), another world-level writer
//     stops as before (rebuild); W6b names the clone footprint's lot (no all-lot rebase); W3 names its lot argument
// (b) a keep release after unhooked clone writes on lot A (built byte, a transform: what 0x630F10 / 0x82B810 write) and a
//     quadtree pin the unwind left on a lot-B Q: no rebuild, the pin cleared, A and B rebased at the next cut, every lot
//     audit equal, the same clone acquired again
// (c) contrast: the same clone write with A missing from the dirty list: the audit finds the lot (the dirty list heals it)
static const u8* PcCloneFme(const RpLot* l) {  // a clone FME on one of the lot's clone Qs
    const u8* m = g_rpClone->m;
    for (DWORD i = 0; i < RpD(m + 8); i++)
        for (const u8* nd = (const u8*)(uintptr_t)((const DWORD*)RpP(m + 4))[i]; nd; nd = RpP(nd + 0x10)) {
            const u8* f = RpP(nd + 8);
            for (int j = 0; f && j < l->nQ; j++) if (l->q[j] && RpP(f + 0xC) == l->q[j] && !RpIsChild(RpP(f + 8))) return f;
        }
    return NULL;
}
static void TestPcLazyKeep() {
    Ts3nCounters& k = ts3native_counters;
    const LONG mode = k.snP4Mode;
    k.snP4Mode = 1;
    g_rpLazyKeep = 1;  // [RouteSnap] LazyKeep=1 (default off since 0.35.22)
    for (PcCut& h : g_pcCuts) h.pend = 0;
    g_rpHoldAt = 0;
    RfCut();
    RpClone* const cl0 = g_rpClone;
    assert(cl0 && cl0->nLots >= 3 && !g_rpNeedEpoch && !g_rpBad);
    RpLot* A = &cl0->lots[0];
    RpLot* B = &cl0->lots[1];
    // (a)
    DWORD stk[64] = {};
    DWORD* slot = &stk[20];
    static u8 lotObj[0x50];
    *(u64*)(lotObj + 0x48) = A->key;
    slot[1] = (DWORD)(uintptr_t)lotObj;
    t_snapRun = 1;
    g_p4Job.abort = -1, g_p4Job.nDirty = 0, g_p4LazyKeep = 0;
    bool stopped = false;
    __try { SnEnter(18, slot); } __except (GetExceptionCode() == kP4LazyCode ? EXCEPTION_EXECUTE_HANDLER : EXCEPTION_CONTINUE_SEARCH) { stopped = true; }
    assert(stopped && g_p4LazyKeep == 1 && g_p4Job.nDirty == 1 && g_p4Job.dirty[0] == A->key);
    g_p4Job.nDirty = 0, g_p4LazyKeep = 0, stopped = false;
    g_rpLazyKeep = 0;  // LazyKeep=0 (the default since 0.35.22): the same stop discards as before 0.35.21
    __try { SnEnter(18, slot); } __except (GetExceptionCode() == kP4LazyCode ? EXCEPTION_EXECUTE_HANDLER : EXCEPTION_CONTINUE_SEARCH) { stopped = true; }
    assert(stopped && !g_p4LazyKeep);
    g_rpLazyKeep = 1, g_p4Job.nDirty = 0, stopped = false;
    slot[1] = (DWORD)A->key, slot[2] = (DWORD)(A->key >> 32);
    __try { SnEnter(21, slot); } __except (GetExceptionCode() == kP4LazyCode ? EXCEPTION_EXECUTE_HANDLER : EXCEPTION_CONTINUE_SEARCH) { stopped = true; }
    assert(stopped && !g_p4LazyKeep);  // grid remove: not audited, the release still rebuilds
    g_p4Job.nDirty = 0;
    const u8* fme = PcCloneFme(B);
    assert(fme);
    slot[-2] = RpD(fme + 8);
    SnEnter(SN_W6B, slot);  // W6b on a clone footprint: its lot, not "every lot"
    {
        bool hasB = false;  // the footprint may sit in other lots' Qs too: each named
        for (int i = 0; i < g_p4Job.nDirty; i++) hasB |= g_p4Job.dirty[i] == B->key;
        // the random writes of TestPcThreaded (thread timing picks the steps) can put this footprint into more than the 8 lots
        // routesnap's W6b names: then the designed fallback, every lot rebased (nDirty -1)
        u64 all[64];
        const int nl = RpCloneFpLots((const u8*)(uintptr_t)slot[-2], all, 64);
        if (!hasB) printf("W6b lots: nDirty %d, footprint in %d lots\n", g_p4Job.nDirty, nl);
        assert(nl > 8 ? g_p4Job.nDirty == -1 : g_p4Job.nDirty >= 1 && hasB);
    }
    // 0.35.23 (docs/94 §22.26.2): a live footprint (refcount far below RP_BIAS) wrote no clone memory: no lot, no all-lot rebase
    g_p4Job.nDirty = 0;
    slot[-2] = (DWORD)(uintptr_t)lotObj;
    SnEnter(SN_W6B, slot);
    assert(g_p4Job.nDirty == 0);
    *(LONG*)(lotObj + 8) = RP_BIAS;  // negative control: a clone-biased object the map cannot name still rebases every lot
    SnEnter(SN_W6B, slot);
    assert(g_p4Job.nDirty == -1);
    *(LONG*)(lotObj + 8) = 0;
    g_p4Job.nDirty = 0;
    {  // an older clone copy of B's footprint (not the one the map's FMEs hold): named by its key
        const u8* cur = (const u8*)(uintptr_t)RpD(fme + 8);
        static __declspec(align(16)) u8 older[0x200];
        assert(RpClass(RpD(cur)) && RpClass(RpD(cur))->size <= sizeof(older));
        memcpy(older, cur, RpClass(RpD(cur))->size);  // +8 keeps RP_BIAS: a clone copy
        slot[-2] = (DWORD)(uintptr_t)older;
        SnEnter(SN_W6B, slot);
        bool hasB = false;
        for (int i = 0; i < g_p4Job.nDirty; i++) hasB |= g_p4Job.dirty[i] == B->key;
        u64 all[64];
        const int nl = RpCloneFpLots(older, all, 64);  // over 8 lots: every lot rebased, as above
        assert(nl > 8 ? g_p4Job.nDirty == -1 : g_p4Job.nDirty >= 1 && hasB);
        g_p4Job.nDirty = 0;
    }
    slot[1] = (DWORD)B->key, slot[2] = (DWORD)(B->key >> 32), slot[3] = 0, slot[4] = 1;
    SnEnter(2, slot);  // W3: its lot argument
    assert(g_p4Job.nDirty == 1 && g_p4Job.dirty[0] == B->key);
    t_snapRun = 0;
    g_p4Job.abort = -1, g_p4Job.nDirty = 0;
    // (b)
    const LONG ep0 = k.rpEpochs, mis0 = k.rpMis, au0 = k.rpAudits, disc0 = k.snPcDiscard;
    AsyncJob a = {};
    a.kind = 1;
    P4OnStart(&a);
    P4Clone ca = {};
    assert(a.p4Ver && PcAcquire(a.p4Ver, &ca) < 0 && g_rpClone == cl0);
    u8* ga = RpGraph(A->node);
    ga[0x8C] ^= 1, ((float*)(ga + 0xA0))[3] += 1.0f;  // unhooked clone writes on lot A before the stop
    int bq = 0;
    while (bq < B->nQ && !B->q[bq]) bq++;
    assert(bq < B->nQ);
    *(LONG*)(B->q[bq] + 0x48) = 2;  // a pin the unwind left on a lot-B Q
    const u64 dirtyA = A->key;
    g_rpNForce = 0, g_rpForceAll = 0;
    PcRelease(&ca, P4REL_LAZY, &dirtyA, 1);
    assert(!g_rpNeedEpoch && !g_rpBad && k.snPcDiscard == disc0 && *(LONG*)(B->q[bq] + 0x48) == 0 && !g_rpForceAll);
    assert(g_rpNForce == 2 && ((g_rpForce[0] == A->key && g_rpForce[1] == B->key) || (g_rpForce[0] == B->key && g_rpForce[1] == A->key)));
    const LONG rb0 = k.rpRebaseWhy[0];
    RfCut();
    assert(k.rpRebaseWhy[0] == rb0 + 2 && k.rpEpochs == ep0 && k.rpMis == mis0 && k.rpAudits > au0 && g_rpClone == cl0);
    RfCut();
    P4OnStart(&a);
    assert(a.p4Ver && PcAcquire(a.p4Ver, &ca) < 0);  // the clone answers again (no rebuild)
    PcRelease(&ca, 0, NULL, 0);
    // (d) 0.35.22 (docs/94 §22.25): the BP 0.35.21 0419 crash shapes. With an A* object (a stand-in with the game's deleting
    // destructor slot), a keep release drops it; the next acquire must re-point every clone Q's +0x28 from the freed object to
    // the new one (0.35.21 left them on the freed one: the fault at 0x664396, then the EA heap). A cut made before a keep release
    // (a parked or second-slot shadow's) is refused "stale" (0.35.21 let it read the half-written lot)
    {
        struct AsFake {
            static void* __fastcall Ctor(void* self, void*) { static DWORD vt[4] = {(DWORD)(uintptr_t)&Dtor}; *(DWORD**)self = vt; return self; }
            static void __fastcall Dtor(u8* self, void*, DWORD) { memset(self, 0xDD, 64); RfFreeP(self); }
        };
        const PcCtorFn savedCtor = g_pcAstarCtor;
        g_pcAstarCtor = AsFake::Ctor;
        const LONG odd0 = k.snPcOddQ, bd0 = k.p4BorrowDirty;
        auto qsOn = [](const u8* as) {
            int n = 0;
            for (int i = 0; i < g_rpClone->nLots; i++)
                for (int j = 0; j < g_rpClone->lots[i].nQ; j++)
                    if (const u8* q = g_rpClone->lots[i].q[j]) { assert(RpP(q + 0x28) == as); n++; }
            return n;
        };
        P4OnStart(&a);
        assert(a.p4Ver && PcAcquire(a.p4Ver, &ca) < 0 && g_pcWork.astar);
        const u8* a1 = g_pcWork.astar;
        assert(qsOn(a1) > 0);
        AsyncJob b2 = {};
        b2.kind = 1;
        P4OnStart(&b2);  // a second pending cut, made before the keep release below (as a parked shadow's)
        const unsigned __int64 lotA = g_rpClone->lots[0].key;
        PcRelease(&ca, P4REL_LAZY, &lotA, 1);
        assert(!g_pcWork.astar && g_pcAstarDead == a1);
        P4Clone cb2 = {};
        assert(b2.p4Ver && PcAcquire(b2.p4Ver, &cb2) == P4_AB_MAT && g_pcAcqWhy == PS_STALE && k.p4BorrowDirty == bd0 + 1);
        void* blocker = RfAlloc(0x9C90);  // the next A* object gets another address
        RfCut();
        P4OnStart(&a);
        assert(a.p4Ver && PcAcquire(a.p4Ver, &ca) < 0 && g_pcWork.astar && g_pcWork.astar != a1);
        assert(qsOn(g_pcWork.astar) > 0 && k.snPcOddQ == odd0 && !g_pcAstarDead);
        PcRelease(&ca, 0, NULL, 0);
        RfFreeP(blocker);
        PcAstarDrop(g_pcWork);  // later tests run without an A* object
        g_pcAstarDead = NULL;
        g_pcAstarCtor = savedCtor;
    }
    // (c)
    P4OnStart(&a);
    assert(a.p4Ver && PcAcquire(a.p4Ver, &ca) < 0);
    A = &g_rpClone->lots[0];
    RpGraph(A->node)[0x8C] ^= 1;
    const u64 other = g_rpClone->lots[2].key;
    PcRelease(&ca, P4REL_LAZY, &other, 1);
    RfCut();
    assert(k.rpMis > mis0);  // the audit sees a clone write the dirty list missed
    RfCut();  // the mismatch rebuilds the clone
    RfCut();
    k.snP4Mode = mode;
    g_rpLazyKeep = 0;
    printf("P4 0.35.21 lazy keep: ApplyLotUpdate / deferral stops name their lot and keep the clone, W6b and W3 name theirs; a keep "
           "release rebased the dirty lot and a pinned one (pin cleared), no rebuild, audits equal (%ld); a write outside the list is "
           "found by the audit\n", k.rpAudits - au0);
}

static void TestP4() {
    Ts3nCounters& k = ts3native_counters;
    // T1-T4 on dummies: bytes checked first (all or nothing), then each thunk by thread state
    g_p4Code = (u8*)VirtualAlloc(NULL, 4096, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
    P4Dummies(g_p4Code);
    DWORD at[P4_SITES];
    for (int i = 0; i < P4_SITES; i++) at[i] = (DWORD)(uintptr_t)(g_p4Code + 16 * i);
    g_p4Code[48 + 1] ^= 1;
    assert(P4PatchSites(at, NULL) == 0 && g_p4Code[0] == 0x66 && g_p4Code[16] == 0xA1);
    g_p4Code[48 + 1] ^= 1;
    assert(P4PatchSites(at, NULL) == 1);
    assert(g_p4Code[0] == 0xE8 && g_p4Code[5] == 0x90 && g_p4Code[7] == 0x90 && g_p4Code[16] == 0xE8 && g_p4Code[64] == 0xE9 && g_p4Code[69] == 0xCC);
    g_p4StampLive = &g_t2Live;
    g_p4ClipLive = (DWORD)(uintptr_t)g_clipLive;
    g_p4WorldSlot = &g_t4Live;
    P4CallEax(g_p4Code);  // T1 live: the word only (0xFFFF -> 0, the high word stays)
    assert(g_t2Live == 0x00410000 && P4CallEax(g_p4Code + 16) == 0x00410000 && P4CallEax(g_p4Code + 64) == 0x1111);
    DWORD ecx, eax, edx, cf;
    P4T3Probe(g_p4Code + 48, &ecx, &eax, &edx, &cf);
    assert(ecx == (DWORD)(uintptr_t)g_clipLive && eax == 0x11111111 && edx == 0x22222222 && cf == 1);
    SnapWork work = {0xFFFE, 0};
    SnapWorker worker = {};
    SnapCtx ctx = {(void*)(uintptr_t)0xC0C0, &work, &worker};
    g_snapCtx = &ctx;
    g_snapRunTid = (LONG)GetCurrentThreadId();
    P4CallEax(g_p4Code);
    assert(work.stamp == 0xFFFF && P4CallEax(g_p4Code + 16) == 0xFFFF && g_t2Live == 0x00410000);
    const LONG wraps = k.snStampWrap, dead = k.snDeadStamp;
    P4CallEax(g_p4Code);  // wraps: restarts at 1, wrap flag set
    assert(work.stamp == 1 && work.wrap == 1 && k.snStampWrap == wraps + 1);
    assert(P4CallEax(g_p4Code + 32) == 1 && k.snDeadStamp == dead + 1);
    P4T3Probe(g_p4Code + 48, &ecx, &eax, &edx, &cf);
    assert(ecx == (DWORD)(uintptr_t)worker.clip && eax == 0x11111111 && edx == 0x22222222 && cf == 1);
    assert(P4CallEax(g_p4Code + 64) == 0xC0C0);
    DWORD other[2] = {};
    HANDLE th = CreateThread(NULL, 0, P4OtherThread, other, 0, NULL);
    WaitForSingleObject(th, INFINITE);
    CloseHandle(th);
    assert(other[0] == 0x1111 && other[1] == 0x00410000);  // another thread during the clone run: the live globals
    g_snapRunTid = 0;
    g_snapCtx = NULL;
    assert(P4CallEax(g_p4Code + 64) == 0x1111);
    // T1 inside [RouteGate] site 8's trampoline (relocated `add` + jmp back): the call goes there, the game site is left
    u8* c2 = g_p4Code + 1024;
    P4Dummies(c2);
    u8* tramp = g_p4Code + 2048;
    memcpy(tramp, kP4Pro[P4_T1], 8);
    tramp[8] = 0xC3;
    DWORD at2[P4_SITES];
    for (int i = 0; i < P4_SITES; i++) at2[i] = (DWORD)(uintptr_t)(c2 + 16 * i);
    c2[0] = 0xE9;  // the game site holds the gate's jmp
    assert(P4PatchSites(at2, tramp) == 3 && tramp[0] == 0xE8 && tramp[7] == 0x90 && c2[0] == 0xE9);
    P4CallEax(tramp);
    assert(g_t2Live == 0x00410001);

    // door stub: chained behind [ProbeValues] (its jump slot) or on its own call site
    const LONG pvOn = g_pvOn;
    void* pvJmp = g_pvJmp[PV_QUERY];
    g_pvOn = 1;
    g_pvJmp[PV_QUERY] = (void*)P4FakeDoor;
    assert(P4InstallDoor(kPv[PV_QUERY].at, (DWORD)(uintptr_t)P4FakeDoor) == 4 && g_pvJmp[PV_QUERY] == (void*)P4DoorStub &&
           g_p4DoorJmp == (void*)P4FakeDoor);
    g_pvOn = pvOn;
    g_pvJmp[PV_QUERY] = pvJmp;
    u8* site = g_p4Code + 3072;
    site[0] = 0xE8;
    *(LONG*)(site + 1) = (LONG)((u8*)P4FakeDoor - (site + 5));
    assert(P4InstallDoor((DWORD)(uintptr_t)site + 1, (DWORD)(uintptr_t)P4FakeDoor) == 0);  // not a call to the target
    assert(P4InstallDoor((DWORD)(uintptr_t)site, (DWORD)(uintptr_t)P4FakeDoor) == 8 && (u8*)site + 5 + *(LONG*)(site + 1) == (u8*)P4DoorStub);
    g_p4FdCalls = 0;
    assert(((P4DoorFn)P4DoorStub)(3, 0, 0, 0, 0, 0, 0, 0, 2) == 32 && g_p4FdCalls == 1);  // no capture, no clone run: pass through
    g_p4Job.abort = -1;
    g_p4Job.nDoors = 0;
    t_p4DoorRec = 1;
    // rooms (args 7-8) and initialLocked (arg 9) carry register junk above their low word / byte (docs/94 §18.7)
    assert(((P4DoorFn)P4DoorStub)(3, 0, 0, 0, 0, 0, 0x11110005, 0x22220006, 0xABCDEF02) == 32 && g_p4FdCalls == 2 && g_p4Job.nDoors == 1);  // recorded
    assert(g_p4Job.doors[0].a[6] == 5 && g_p4Job.doors[0].a[7] == 6 && g_p4Job.doors[0].a[8] == 2);  // stored masked
    t_p4DoorRec = 0;
    t_snapRun = 1;
    assert(((P4DoorFn)P4DoorStub)(3, 0, 0, 0, 0, 0, 0x7F7F0007, 0x00000006, 0x13579B02) == 0 && g_p4Job.abort == P4_AB_DOORMISS);  // room 7: a miss
    g_p4Job.abort = -1;
    assert(((P4DoorFn)P4DoorStub)(3, 0, 0, 0, 0, 0, 0x99990005, 0x00000006, 0x13579B02) == 32 && g_p4FdCalls == 2 && g_p4Job.doors[0].used);  // replayed, other junk
    assert(((P4DoorFn)P4DoorStub)(3, 0, 0, 0, 0, 0, 5, 6, 2) == 0 && g_p4Job.abort == P4_AB_DOORMISS && g_p4FdCalls == 2);  // used up
    t_snapRun = 0;

    // D2 handshake (K4-NR): a writer waits for a running clone run; the worker's arm waits for writers in flight
    SnapCtx nctx = {g_p4CloneWorld, &work, &worker};
    assert(P4Arm(&nctx) && SnLive() && t_snapRun);
    g_nrDone = 0;
    const LONG nrWaits = k.snNrWaits;
    g_nrStarted = 0;
    th = CreateThread(NULL, 0, P4NrWriter, NULL, 0, NULL);
    while (!g_nrStarted) Sleep(1);
    Sleep(50);
    assert(!g_nrDone && g_snNrIn == 0);  // stepped back and waits
    P4Disarm();
    WaitForSingleObject(th, INFINITE);
    CloseHandle(th);
    assert(g_nrDone && g_snNrIn == 0 && k.snNrWaits == nrWaits + 1 && !SnLive() && !t_snapRun);
    InterlockedIncrement(&g_snNrIn);  // a writer that stays: the arm gives up after 250 ms (shadow refused "nrbusy")
    assert(!P4Arm(&nctx));
    P4Disarm();
    InterlockedDecrement(&g_snNrIn);
    InterlockedIncrement(&g_snNrIn);  // the gate frame's undo
    RgUndo(RgFrame{NULL, 0, 0, RG_REL_NR, NULL});
    assert(g_snNrIn == 0);
    // D5 bits: physics needs a physics read, destroys count always
    LONG n0[kP4Fam] = {}, n1[kP4Fam] = {1, 0, 0, 0, 0, 0, 0};
    assert(P4NrBits(n0, n1, 0) == 0 && P4NrBits(n0, n1, P4R_PHYS) == 1);
    n1[0] = 0; n1[2] = 1;
    assert(P4NrBits(n0, n1, 0) == 4);

    // shadow flow with fakes
    PlanLockedFn planSave = g_planOrig;
    CacheEntry* tab = g_tab;
    const LONG workerMode = k.workerMode, budgetMode = k.budgetMode;
    g_tab = NULL;
    k.workerMode = 0;
    k.budgetMode = 0;
    g_planOrig = P4FakePlan;
    g_p4ListFree = P4FakeListFree;
    g_fpVt[0] = (void*)P4FpDestroy;
    g_fpVt[0x88 / 4] = g_fpVt[0x90 / 4] = (void*)P4FpUnlink;
    g_p4DoorJmp = (void*)P4FakeDoor;
    g_p4Heap = HeapCreate(0, 0, 0);
    for (size_t n = 0; n < 200; n += 8) {  // HeapAlloc gives 8 on x86; the capture buffers must be 16-aligned (0.30.1 fault)
        void* p = P4Alloc(g_p4Job, n);
        assert(p && ((uintptr_t)p & 15) == 0);
        memset(p, 0xCD, n);
        P4JobFree(g_p4Job);
    }
    g_p4Buf =(char*)VirtualAlloc(NULL, kP4BufCap, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    char path[MAX_PATH];
    GetTempPathA(MAX_PATH, path);
    sprintf_s(path + strlen(path), MAX_PATH - strlen(path), "ts3native-p4-selftest-%lu.jsonl", GetCurrentProcessId());
    g_p4Jsonl = CreateFileA(path, GENERIC_WRITE, FILE_SHARE_READ, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    static u8 guard12 = 1;
    static DWORD guard14[2] = {0, 3};
    static u8 guard27 = 1;
    g_p4Guards[0] = (DWORD)(uintptr_t)&guard12;
    g_p4Guards[1] = (DWORD)(uintptr_t)&guard14[0];
    g_p4Guards[2] = (DWORD)(uintptr_t)&guard14[1];
    g_p4Guards[3] = (DWORD)(uintptr_t)&guard27;
    strcpy_s(g_p4WorldTag, "ST");
    k.snP4Ready = 1;
    g_p4On = 1;
    static float sp[16] = {1.5f, 2.0f, 3.0f}, gp[32] = {0};
    gp[16] = 3.5f; gp[17] = 4.0f; gp[18] = 5.0f;  // goal record 1 (the fake picks index 1)
    int s[4] = {(int)(uintptr_t)sp, (int)(uintptr_t)(sp + 16), (int)(uintptr_t)(sp + 16), 0};
    int g[4] = {(int)(uintptr_t)gp, (int)(uintptr_t)(gp + 32), (int)(uintptr_t)(gp + 32), 0};
    static unsigned __int64 ign[2] = {0x123, 0x456};
    u8 opts[0x100] = {};
    *(DWORD*)(opts + 0x38) = (DWORD)(uintptr_t)ign;
    *(DWORD*)(opts + 0x3C) = (DWORD)(uintptr_t)(ign + 2);
    static unsigned __int64 idset[3] = {0x11, 0x22, 0x33};  // +0x08: the sorted id set (docs/94 §18.7)
    *(DWORD*)(opts + 0x08) = (DWORD)(uintptr_t)idset;
    *(DWORD*)(opts + 0x0C) = (DWORD)(uintptr_t)(idset + 3);
    g_p4IdSet = idset;
    *(DWORD*)(opts + 0x78) = 0x7777;
    int list[4] = {0, 0, 0x5A5A, 0};
    // no provider: refused "clone" (M4), the game's answer recorded, no clone run
    const LONG m4 = k.snM[4], abClone = k.snAbort[P4_AB_CLONE];
    const char* rec = P4RunJob(0, list, s, g, opts);
    assert(strstr(rec, "\"refused\":\"clone\"") && strstr(rec, "\"copy\":null") && strstr(rec, "\"ver\":0") && k.snM[4] == m4 + 1 &&
           k.snAbort[P4_AB_CLONE] == abClone + 1 && g_p4CloneCalls == 0 && g_p4LiveCalls == 1);
    assert(strstr(rec, "{\"v\":3,\"plan\":100,\"ic\":\"Replan\",\"world\":\"ST\",\"search\":\"G\"") && strstr(rec, "\"start\":[1.5,2,3]") &&
           strstr(rec, "\"goal\":[3.5,4,5]") && strstr(rec, "\"nodes\":[[10,-100,20,0,0,-1],[11,-100,19,1.25,0,-1],[12,-100,18,2.5,8,-1]]"));
    // provider registered: the clone run on the copy, door replayed, private paths released, M0
    P4SetCloneProvider(&kP4Fake);
    assert(k.snP4Ready & 16);
    const LONG m0 = k.snM[0], replay = k.snDoorReplay, freed = k.snPathsFreed;
    g_fpUnlink = g_fpDestroy = 0;
    rec = P4RunJob(0, list, s, g, opts);
    assert(g_p4CloneCalls == 1 && k.snM[0] == m0 + 1 && strstr(rec, "\"refused\":null") && strstr(rec, "\"ver\":9") &&
           strstr(rec, "\"copy\":{\"mType\":1,\"status\":2,\"startIdx\":0,\"goalIdx\":1"));
    assert(k.snDoorReplay == replay + 1 && k.snPathsFreed == freed + 1 && g_fpUnlink == 2 && g_fpDestroy == 2);  // live + clone
    assert(g_pvRel == 0x100 && g_pvCl.world == g_p4CloneWorld && !t_snapRun && !SnLive());
    // mismatch classes: node y -> M1, goal index -> M2, status -> M3, destroy in the window -> M5 (nr bit 2)
    const LONG m1 = k.snM[1], m2 = k.snM[2], m3 = k.snM[3], m5 = k.snM[5];
    P4RunJob(1, list, s, g, opts);
    P4RunJob(2, list, s, g, opts);
    P4RunJob(3, list, s, g, opts);
    rec = P4RunJob(6, list, s, g, opts);
    assert(k.snM[1] == m1 + 1 && k.snM[2] == m2 + 1 && k.snM[3] == m3 + 1 && k.snM[5] == m5 + 1 && strstr(rec, "\"nr\":4"));
    // N15 (docs/94 §17.9): a display level write in the window is M5 only when the plan ran the vertical ray 0x6583E0
    {
        const LONG m5n = k.snM[5], m1n = k.snM[1], dl = k.snNrBitDl;
        rec = P4RunJob(7, list, s, g, opts);
        assert(k.snM[5] == m5n + 1 && k.snNrBitDl == dl + 1 && strstr(rec, "\"nr\":128"));
        P4RunJob(8, list, s, g, opts);
        assert(k.snM[1] == m1n + 1 && k.snNrBitDl == dl + 1);  // no vertical ray: the write does not excuse the difference
        // the three writer stubs on dummies with the game's prologues: counted; a call from a clone run is the tripwire
        u8* dc = (u8*)VirtualAlloc(NULL, 4096, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
        memcpy(dc, kP4Dl[0].pro, 10), memcpy(dc + 10, "\x5E\x5B\xC3", 3);                    // pop esi; pop ebx; ret
        memcpy(dc + 32, kP4Dl[1].pro, 5), memcpy(dc + 37, "\x5E\x5B\x83\xC4\x08\xC3", 6);   // pop esi; pop ebx; add esp,8; ret
        memcpy(dc + 64, kP4Dl[2].pro, 5), memcpy(dc + 69, "\x5D\xC3", 2);                    // pop ebp; ret
        const DWORD dlAt[3] = {(DWORD)(uintptr_t)dc, (DWORD)(uintptr_t)(dc + 32), (DWORD)(uintptr_t)(dc + 64)};
        void* savedDl[3] = {g_p4DlJmp[0], g_p4DlJmp[1], g_p4DlJmp[2]};
        g_p4DlJmp[0] = g_p4DlJmp[1] = g_p4DlJmp[2] = NULL;
        assert(P4InstallDl(dlAt) == 3 && dc[0] == 0xE9 && dc[32] == 0xE9 && dc[64] == 0xE9);
        const LONG n7 = g_nrN[7], trip = k.snDlOnClone;
        static u8 lotObj[0x200];
        ((void(__fastcall*)(void*, void*))(void*)dc)(lotObj, NULL);
        ((void(__cdecl*)())(void*)(dc + 32))();
        ((void(__cdecl*)(DWORD))(void*)(dc + 64))(5);
        assert(g_nrN[7] == n7 + 3 && k.snDlOnClone == trip);
        t_snapRun = 1;
        ((void(__cdecl*)())(void*)(dc + 32))();
        t_snapRun = 0;
        assert(k.snDlOnClone == trip + 1 && g_nrN[7] == n7 + 3);
        g_p4DlJmp[0] = savedDl[0], g_p4DlJmp[1] = savedDl[1], g_p4DlJmp[2] = savedDl[2];
    }
    // Los (docs/94 §17.9): a route with ForcePhysicsBasedRaycast (option bit 0x2000000) never becomes a job
    {
        static u8 losRoute[0x200];
        static u8 losRm[16];
        static const u8* losRmSlot = losRm;
        struct R { static const u8* __fastcall Look(const void*, void*, DWORD h) { return h == 0x4C05 ? losRoute : NULL; } };
        const AsRouteFn savedRoute = g_asRoute;
        const u8* const* savedSlot = g_asRmSlot;
        const LONG savedMode = k.asyncMode, savedReady = k.asyncReady, los0 = k.arLosRefused;
        g_asRoute = R::Look, g_asRmSlot = &losRmSlot;
        k.asyncMode = 1, k.asyncReady = 1;
        *(DWORD*)(losRoute + 0x158 + 0x28) = 0x2000000;
        assert(AsyncStart(0x4C05) == 0 && k.arLosRefused == los0 + 1 && g_rfbWhy == RFB_LOS);
        assert(!AsyncLosRoute(0x4C06));  // another route: not refused for Los
        g_asRoute = savedRoute, g_asRmSlot = savedSlot;
        k.asyncMode = savedMode, k.asyncReady = savedReady;
    }
    // H20d: a portal IC (SetPortalCost) in the window is D5 family 9, always counted (no reader stub): M5 with nr 256
    {
        const LONG m5p = k.snM[5], bp = k.snNrBitPortal;
        rec = P4RunJob(11, list, s, g, opts);
        assert(k.snM[5] == m5p + 1 && k.snNrBitPortal == bp + 1 && strstr(rec, "\"nr\":256"));
    }
    // goal tie set (docs/94 §13.1, RoutingJob_Execute 0x81B8CC..0x81BC65): equal priority and every d² within 0.05f of the
    // running best -> the job's rng(n) draw, any index valid; a goal index that differs inside the set is M1 goal-tie
    {
        assert(strstr(rec, "\"tie\":[]"));  // the records above: d² 11.25 and 8, no draw
        float tg[3 * 16] = {}, ts[16] = {1.0f, 0, 2.0f};
        const DWORD tsv[2] = {(DWORD)(uintptr_t)ts, (DWORD)(uintptr_t)(ts + 16)};
        auto tieN = [&](int n, float d0, float d1, float d2, float p2) {
            const float d[3] = {d0, d1, d2};
            for (int i = 0; i < 3; i++) tg[i * 16] = 1.0f + sqrtf(d[i]), tg[i * 16 + 2] = 2.0f, tg[i * 16 + 8] = i == 2 ? p2 : 7.0f;
            const DWORD gv[2] = {(DWORD)(uintptr_t)tg, (DWORD)(uintptr_t)(tg + n * 16)};
            return P4TieN(tsv, gv);
        };
        assert(tieN(3, 10, 10, 10, 7) == 3 && tieN(2, 10, 10.04f, 99, 7) == 2 && tieN(1, 10, 10, 10, 7) == 0);  // one record: no draw
        assert(tieN(3, 10, 10.04f, 10.08f, 7) == 0);   // 10.08 is 0.08 from the running best 10
        assert(tieN(3, 10.04f, 10, 10.04f, 7) == 3);   // the best moved to 10: 10.04 is within 0.05 of it
        assert(tieN(3, 10, 10, 10, 8) == 0);           // a priority differs
        assert(tieN(3, 10, 10.06f, 10, 7) == 0);
        const DWORD noStart[2] = {0, 0};
        assert(P4TieN(noStart, tsv) == 0);
        // end to end: goal record 0 moved to d² 8 like record 1 -> "tie":[0,1]; the clone's other index (variant 2) is M1
        const float g0x = gp[0], g0z = gp[2];
        gp[0] = -0.5f, gp[2] = 5.0f;  // start (1.5, 3): record 0 d² 4 + 4, record 1 (3.5, 5) d² 4 + 4
        const LONG m1t = k.snM[1], tie = k.snM1Tie, m2t = k.snM[2];
        rec = P4RunJob(2, list, s, g, opts);
        assert(k.snM[1] == m1t + 1 && k.snM1Tie == tie + 1 && k.snM[2] == m2t && strstr(rec, "\"tie\":[0,1]"));
        rec = P4RunJob(6, list, s, g, opts);  // tie set present, same goal, node y + a destroy in the window: M5, no tie count
        assert(k.snM1Tie == tie + 1 && strstr(rec, "\"nr\":4"));
        gp[0] = g0x, gp[2] = g0z;
    }
    // D4 (H20c): a lazy LotGraph_Build (gate site 3) or LotTable_Insert (PV_LAZY0) in a clone run stops it before the call:
    // "lazy", the clone discarded, P4 stays on (not a fault). The crash logger skips the stop (no slot, no line; 0.31.3)
    void* const xcpt = AddVectoredExceptionHandler(1, XcptLog);
    assert(xcpt);
    const LONG xcptN = g_xcptN;
    for (int v = 9; v <= 10; v++) {
        const LONG lazy = k.snCloneLazy, faults = k.snShadowFault, m4l = k.snM[4];
        g_p4PastLazy = 0;
        rec = P4RunJob(v, list, s, g, opts);
        assert(strstr(rec, "\"refused\":\"lazy\"") && k.snCloneLazy == lazy + 1 && k.snM[4] == m4l + 1 && !g_p4PastLazy);
        assert(k.snShadowFault == faults && g_p4On && (k.snP4Ready & 1) && g_pvRel == (P4REL_DISCARD | 0x100) && !t_snapRun && !SnLive());
    }
    assert(g_xcptN == xcptN);
    RemoveVectoredExceptionHandler(xcpt);
    // door miss: refused, NoListeners answered, no managed call
    rec = P4RunJob(4, list, s, g, opts);
    assert(strstr(rec, "\"refused\":\"door-miss\"") && strstr(rec, "\"copy\":null"));
    // fallbacks before the clone run: another job queued (no record), epoch moved, cold statics
    const LONG busy = k.snShadowBusy, recs = k.snJsonl;
    g_jobs[0].state = J_QUEUED;
    P4RunJob(0, list, s, g, opts);
    g_jobs[0].state = J_FREE;
    assert(k.snShadowBusy == busy + 1 && k.snJsonl == recs);
    g_p4Variant = 0;
    {
        AsyncJob j = {};
        j.kind = 2;
        P4OnStart(&j);
        P4LiveBegin(&j);
        int o1, o2;
        list[0] = list[1] = (int)(uintptr_t)list;
        PlanLockedHook((void*)(uintptr_t)0x2000, NULL, list, s, g, 0xA6E, 1, opts, &o1, &o2, 5, 0);
        P4LiveEnd(&j);
        P4FakeListFree(list, NULL);
        g_spEpoch++;
        P4Shadow();
        g_p4Buf[g_p4Len] = 0;
        assert(strstr(g_p4Buf, "\"refused\":\"epoch\"") && strstr(g_p4Buf, "\"ic\":\"PlanFromPoint\""));
    }
    guard12 = 0;
    assert(strstr(P4RunJob(0, list, s, g, opts), "\"refused\":\"cold\""));
    guard12 = 1;
    guard27 = 2;  // #27: only bit 0 is the guard
    assert(strstr(P4RunJob(0, list, s, g, opts), "\"refused\":\"cold\""));
    guard27 = 1;
    g_p4Kinds = 1;  // Replan not in Kinds
    assert(strstr(P4RunJob(0, list, s, g, opts), "\"refused\":\"unsupported\""));
    g_p4Kinds = 15;
    // tripwires inside a clone run: World_FireEvent / managed / live lock / lazy build set the first abort reason
    g_p4Job.abort = -1;
    P4Abort(P4_AB_EVENT);
    P4Abort(P4_AB_MANAGED);
    assert(g_p4Job.abort == P4_AB_EVENT && k.snCloneEvent && k.snCloneManaged);
    g_p4Job.nDirty = 0;
    P4WorkerDirty(true, 7);
    P4WorkerDirty(true, 7);
    P4WorkerDirty(true, 8);
    assert(g_p4Job.nDirty == 2);
    P4WorkerDirty(false, 0);
    assert(g_p4Job.nDirty == -1);
    // P4 part B end to end: the replay clone provider (p4clone.cpp) on replay-fake.h's world
    {
        static DWORD simTid;
        const DWORD* savedSim = g_rtSimTidSlot;
        simTid = GetCurrentThreadId();
        g_rtSimTidSlot = &simTid;
        PcThisFn savedPending = g_pcApplyPending, savedFlush = g_pcFlushDeferred;
        PcCtorFn savedCtor = g_pcAstarCtor;
        g_pcApplyPending = NULL, g_pcFlushDeferred = NULL, g_pcAstarCtor = NULL;  // the game's; the fake world has nothing pending
        const PcFcInitFn savedFc = g_pcFcInit;
        const PcClosedFn savedClosed = g_pcClosedInit;
        const PcThisFn savedFcReset = g_pcFcReset, savedCtrClear = g_pcCtrClear;
        const PcHashClrFn savedC0 = g_pcClrC0, savedFC = g_pcClrFC;
        const PcGameFreeFn savedFree = g_pcGameFree;
        g_pcFcInit = NULL, g_pcClosedInit = NULL, g_pcFcReset = NULL, g_pcCtrClear = NULL, g_pcClrC0 = NULL, g_pcClrFC = NULL, g_pcGameFree = NULL;  // W': game code, steerro-test K6
        P4SetCloneProvider(&kPcProvider);
        g_planOrig = (PlanLockedFn)PcFakePlan;
        const LONG c0 = k.snM[0], c1 = k.snM[1], c3 = k.snM[3], acq = k.snPcAcquired, cuts = k.snPcCuts;
        TestPcGridPoly();
        PcSetup(31);
        const char* r0 = PcRunJob(0, list, s, g, opts);  // unchanged world: M0
        assert(strstr(r0, "\"refused\":null") && !strstr(r0, "\"ver\":0,") && k.snM[0] == c0 + 1 && k.snPcAcquired == acq + 1 && k.snPcCuts == cuts + 1);
        assert(g_pcCloneRuns == 1 && g_pcSetsSeen > 0 && g_rpHoldAt == 0);
        PcCheckWp(g_pcW[0]);  // I-94-220 / I-94-221
        const u8* wp0 = g_pcWork.wp;
        for (int i = 0; i < 40; i++) RfStep();  // recorded writes between jobs: the clone follows them
        r0 = PcRunJob(0, list, s, g, opts);
        assert(strstr(r0, "\"refused\":null") && k.snM[0] == c0 + 2);
        assert(g_pcWork.wp == wp0);  // W' kept across acquires
        // 0.35.7 (docs/94 §21.22, I-94-430): 70 lock-key tables in one clone's life (no epoch, as soft resync since 0.35.5):
        // still M0, and the clone keeps only the table its footprints point into (0.35.5 refused "mat"/"copy" from the 64th on)
        {
            const RpClone* cl0 = g_rpClone;
            for (int i = 0; i < 70; i++) {
                if (i % 7 == 0) RfStep();
                g_spKeysDirty = 1;  // a lock-key IC ran: the next cut copies a new table
                const LONG mk = k.snM[0];
                r0 = PcRunJob(0, list, s, g, opts);
                if (!strstr(r0, "\"refused\":null") || k.snM[0] != mk + 1) { printf("lock-key tables: run %d: %s\n", i, r0); assert(false); }
            }
            assert(g_rpClone == cl0 && g_pcWork.nKeys == 1);
        }
        TestPcMode2(list, s, g, opts);  // 0.35.9: the same clone life under P5 / P5b / P5c
        PcFakeW(1);  // another live W (a world load): W' made again from it
        r0 = PcRunJob(0, list, s, g, opts);
        assert(strstr(r0, "\"refused\":null"));
        PcCheckWp(g_pcW[1]);
        *(u8**)(g_rf.world + 0x960) = g_pcW[0];
        RfTeardown();
        PcSetup(32);
        PcRunJob(1, list, s, g, opts);  // an unrecorded insert after the cut: the inner node's y differs, M1
        assert(k.snM[1] == c1 + 1);
        RfTeardown();
        PcSetup(33);
        PcRunJob(2, list, s, g, opts);  // a world byte written after the cut: the copy has the cut's, status differs, M3
        assert(k.snM[3] == c3 + 1);
        g_rf.world[0x9F0] = 0;
        // H20c: a live lot with a pending portal add (+0x54) refuses the armed cut ("nover"); a clone lot with a pending remove
        // (+0x64, 8-byte ids) refuses the acquire ("mat")
        {
            u8* g0 = g_rf.lots[0] + 0x10;
            static DWORD pendAdd[1] = {0};
            const DWORD sb = RpD(g0 + 0x54), se = RpD(g0 + 0x58);
            *(DWORD**)(g0 + 0x54) = pendAdd, *(DWORD**)(g0 + 0x58) = pendAdd + 1;
            const LONG pc = k.snPcPortalPend[0], pa = k.snPcPortalPend[1];
            const char* rp = PcRunJob(0, list, s, g, opts);
            assert(strstr(rp, "\"refused\":\"nover\"") && strstr(rp, "\"sub\":\"portal\"") && k.snPcPortalPend[0] == pc + 1);
            *(DWORD*)(g0 + 0x54) = sb, *(DWORD*)(g0 + 0x58) = se;
            rp = PcRunJob(0, list, s, g, opts);  // settled: the lot is rebased to the empty vector again
            assert(strstr(rp, "\"refused\":null") && k.snPcPortalPend[0] == pc + 1);
            u8* cg = RpGraph(g_rpClone->lots[0].node);
            const DWORD ce = RpD(cg + 0x68);
            *(DWORD*)(cg + 0x68) = RpD(cg + 0x64) + 8;
            rp = PcRunJob(0, list, s, g, opts);
            assert(strstr(rp, "\"refused\":\"mat\"") && strstr(rp, "\"sub\":\"portal\"") && k.snPcPortalPend[1] == pa + 1);
            *(DWORD*)(cg + 0x68) = ce;
        }
        // a held cut the worker never acquires expires; an older cut is refused "epoch"
        AsyncJob j = {};
        j.kind = 1;
        P4OnStart(&j);
        const DWORD v1 = j.p4Ver;
        P4OnStart(&j);
        P4Clone cl = {};
        assert(v1 && j.p4Ver && v1 != j.p4Ver && PcAcquire(v1, &cl) == P4_AB_EPOCH);  // the second cut replaced the first
        g_rpHoldAt = 0;
        TestPcTwoSlots(list, s, g, opts);
        TestPcThreaded(list, s, g, opts);
        TestPcLazyKeep();
        RfTeardown();
        g_p4Variant = 0;
        g_spWmSlot = (const u8* const*)(uintptr_t)0x011ECBC4;
        g_pcApplyPending = savedPending, g_pcFlushDeferred = savedFlush, g_pcAstarCtor = savedCtor;
        g_pcFcInit = savedFc, g_pcClosedInit = savedClosed, g_pcFcReset = savedFcReset, g_pcCtrClear = savedCtrClear;
        g_pcClrC0 = savedC0, g_pcClrFC = savedFC, g_pcGameFree = savedFree;
        g_planOrig = (PlanLockedFn)P4FakePlan;
        P4SetCloneProvider(&kP4Fake);
        g_rtSimTidSlot = savedSim;
        printf("P4 part B: replay clone provider end to end (fake planner on the working copy): M0 unchanged and after recorded "
               "writes, M1 for an unrecorded tree insert, M3 for a world byte after the cut; lock-key sets private; W' (I-94-220: P / R / G "
               "the copy's, group hash private, kept across acquires, made again for a new live W) and G trees re-made under the copy's "
               "anchors (I-94-221); stale cut refused; H20c: pending portal add on a live lot "
               "refuses the cut, pending remove on a clone lot refuses the acquire\n");
    }
    // an exception in the clone run: caught, P4 off for the session, the copy discarded, the record says "fault"; crash H: the
    // EA heap lock the fault left held is released (else every other thread's next allocation waits for ever)
    static u8 fkHeap[0x4E8];
    static DWORD fkHeapSlot;
    InitializeCriticalSection(&g_fkEaLock.cs);
    *(void**)(fkHeap + 0x4E4) = &g_fkEaLock, fkHeapSlot = (DWORD)(uintptr_t)fkHeap;
    DWORD* const savedEaHeap = g_p4EaHeap;
    g_p4EaHeap = &fkHeapSlot;
    rec = P4RunJob(5, list, s, g, opts);
    assert(strstr(rec, "\"refused\":\"fault\"") && k.snShadowFault == 1 && k.snP4Ready == 0 && !g_p4On && g_pvRel == (P4REL_DISCARD | 0x100));
    assert(!g_fkEaLock.cs.OwningThread && !g_fkEaLock.cs.RecursionCount && g_fkEaLock.depth == 0);
    g_p4EaHeap = savedEaHeap;
    DeleteCriticalSection(&g_fkEaLock.cs);
    assert(!t_snapRun && !SnLive() && g_snapCtx == NULL);
    {
        AsyncJob j = {};
        P4OnStart(&j);
        assert(!j.p4);  // nothing armed any more
    }
    CloseHandle(g_p4Jsonl);
    DeleteFileA(path);  // per-process name (parallel runs): no leftovers in %TEMP%
    g_p4Jsonl = INVALID_HANDLE_VALUE;
    P4SetCloneProvider(NULL);
    g_planOrig = planSave;
    g_tab = tab;
    k.workerMode = workerMode;
    k.budgetMode = budgetMode;
    g_p4On = 0;
    printf("P4 part A: T1-T4 by thread (wrap, dead stamp, T3 registers and flags, T1 in the gate trampoline), door record/replay/miss, "
           "D2 handshake and arm limit, D5 bits, shadow M0-M5 with a fake clone, fallbacks clone/epoch/cold/unsupported/busy/door-miss/"
           "fault ok; D4 lazy build / lot table insert stop the clone run (lazy, discarded, P4 stays on); D5 portal IC family (nr 256) "
           "ok; goal tie set (equal priority, d2 within 0.05f of the running best) -> \"tie\" and M1 goal-tie ok; records in %s\n", path);
}

// --- P5 ([RouteSnap] Mode=2, p5.cpp; docs/94 §22): the clone answer taken, door asks to the game's chain, every fallback ---
static int g_p5DoorCalls, g_p5PoolIn, g_p5PoolOut;
static DWORD __cdecl P5FakeDoor(DWORD a1, DWORD a2, DWORD a3, DWORD a4, DWORD a5, DWORD a6, DWORD a7, DWORD a8, DWORD a9) {
    assert(!t_snapRun && !SnLive());  // a P5 ask runs disarmed: its proxy and any nested plan see the live world
    g_p5DoorCalls++;
    return P4FakeDoor(a1, a2, a3, a4, a5, a6, a7, a8, a9);
}
// one ReplanFromPosition job through START, the job's Route_PlanLocked and J_DONE; keepList: the route's list is not reset
static bool g_p5WriteAfterCut;
static u8 P5RunJob(int variant, int* outList, int* s, int* g, u8* opts, int* o2, bool keepList = false) {
    g_p4Variant = variant;
    AsyncJob j = {};
    j.kind = 3;
    j.seq = 300 + variant;
    j.x87 = GetX87();
    j.mxcsr = _mm_getcsr();
    j.p4W = 0x7FFF0000;  // a stale slot value: P5OnStart must set the cut's count (0.36.23)
    P4OnStart(&j);
    if (g_p5WriteAfterCut) InterlockedIncrement(&g_spWrites);  // a recorded routing write while the job is queued
    P4LiveBegin(&j);
    if (!keepList) outList[0] = outList[1] = (int)(uintptr_t)outList;
    int o1 = -1;
    *o2 = -1;
    g_p4Len = 0;
    g_p4Buf[0] = 0;
    const u8 r = PlanLockedHook((void*)(uintptr_t)0x2000, NULL, outList, s, g, 0xA6E, 1, opts, &o1, o2, 5, 0);
    P4LiveEnd(&j);
    P4Shadow();  // P5 left nothing armed: no shadow, no second record
    g_p4Buf[g_p4Len] = 0;
    return r;
}
// the §13 answer JSON of an answer (what shadow-compare-report.ts compares)
static void P5AnsStr(const P4Ans& a, char* out, size_t n) {
    g_p4Len = 0;
    P4PutAns(a);
    g_p4Buf[g_p4Len] = 0;
    strcpy_s(out, n, g_p4Buf);
}
// a fallback: the game's own plan answered (the live fake ran, its answer in the list), nothing taken, `cnt` up by one
static void P5Fallback(int variant, const volatile LONG* cnt, int* list, int* s, int* g, u8* opts, bool keepList = false) {
    Ts3nCounters& k = ts3native_counters;
    const LONG c0 = *cnt, taken = k.m2Taken, live = g_p4LiveCalls;
    int o2;
    const u8 r = P5RunJob(variant, list, s, g, opts, &o2, keepList);
    assert(r == 1 && o2 == 1 && *cnt == c0 + 1 && k.m2Taken == taken && g_p4LiveCalls == live + 1);
    assert(!t_snapRun && !SnLive() && !g_p4Job.armed);
    P4FakeListFree(list, NULL);
}
static void TestP5() {
    Ts3nCounters& k = ts3native_counters;
    static_assert(offsetof(Ts3nCounters, m2Ready) == 0x2F70 && offsetof(Ts3nCounters, m2Take) == 0x2F74 &&
                  offsetof(Ts3nCounters, m2Fb) == 0x2F90 && offsetof(Ts3nCounters, m2VerM) == 0x2FDC &&
                  offsetof(Ts3nCounters, m2DoorHist) == 0x3000 && offsetof(Ts3nCounters, m2Guard) == 0x3024 &&
                  sizeof(Ts3nCounters) == 6112 * 4, "counters v61 layout + v62 + v63 + v65 + v66 (docs/registry.md)");
    static_assert(offsetof(Ts3nCounters, p4QCut) == 0x30EC && offsetof(Ts3nCounters, p4QCutMiss) == 0x30F0 &&
                  offsetof(Ts3nCounters, p4WaitExt) == 0x30F4 && offsetof(Ts3nCounters, oiClone) == 0x30F8 &&
                  offsetof(Ts3nCounters, rpHard) == 0x30FC && sizeof(Ts3nCounters) == 6112 * 4,
                  "counters v67 layout (docs/registry.md)");
    static_assert(offsetof(Ts3nCounters, rpOldAsk) == 0x310C && offsetof(Ts3nCounters, p4AcqBad) == 0x3110, "counters v68 layout (docs/registry.md)");
    static_assert(offsetof(Ts3nCounters, p4Obst) == 0x3114 && offsetof(Ts3nCounters, p4ObstBusy) == 0x3118,
                  "counters v69 layout after v68 (docs/registry.md)");
    static_assert(offsetof(Ts3nCounters, rpSoftReal) == 0x311C && offsetof(Ts3nCounters, rpRebaseWhy) == 0x3128 &&
                  offsetof(Ts3nCounters, rpRebaseQSame) == 0x3138, "counters v70 layout (docs/registry.md)");
    static_assert(offsetof(Ts3nCounters, p4Borrow) == 0x313C && offsetof(Ts3nCounters, p4BorrowMiss) == 0x3140 &&
                  offsetof(Ts3nCounters, p4BorrowDirty) == 0x3144, "counters v71 layout (docs/registry.md)");
    static_assert(offsetof(Ts3nCounters, rpKidsKept) == 0x3148, "counters v72 layout (docs/registry.md)");
    assert(k.version == 912);
    PlanLockedFn planSave = g_planOrig;
    CacheEntry* tab = g_tab;
    const LONG workerMode = k.workerMode, budgetMode = k.budgetMode;
    void (*poolIn)() = g_p4PoolsEnterJob;
    void (*poolOut)() = g_p4PoolsLeaveJob;
    g_tab = NULL;
    k.workerMode = 0;
    k.budgetMode = 0;
    g_planOrig = P4FakePlan;
    g_p4ListFree = P4FakeListFree;
    g_p4DoorJmp = (void*)P5FakeDoor;
    g_p4PoolsEnterJob = [] { g_p5PoolIn++; };
    g_p4PoolsLeaveJob = [] { g_p5PoolOut++; };
    static u8 guard12 = 1, guard27 = 1;
    static DWORD guard14[2] = {0, 3};
    const DWORD guardsSave[4] = {g_p4Guards[0], g_p4Guards[1], g_p4Guards[2], g_p4Guards[3]};
    g_p4Guards[0] = (DWORD)(uintptr_t)&guard12;
    g_p4Guards[1] = (DWORD)(uintptr_t)&guard14[0];
    g_p4Guards[2] = (DWORD)(uintptr_t)&guard14[1];
    g_p4Guards[3] = (DWORD)(uintptr_t)&guard27;
    // M2-3 rows on our own bytes (vanilla first)
    static u8 l2[5][8] = {{0x51}, {0x55, 0x8D, 0x6C, 0x24, 0x98, 0x81, 0xEC, 0x08}, {0x0F, 0x86, 0xA9, 0xB1, 0xFF, 0xFF}, {0x75, 0x26}, {0x74, 0x4C}};
    DWORD l2Save[5];
    for (int i = 0; i < 5; i++) l2Save[i] = g_p5L2[i].at, g_p5L2[i].at = (DWORD)(uintptr_t)l2[i];
    k.snP4Ready = 1;
    k.snP4Mode = 2;
    g_p4On = 1;
    g_p4Kinds = 15;
    P4SetCloneProvider(&kP4Fake);
    g_p5On = 1;
    k.m2Take = 1;
    k.m2Ready = 1;
    k.m2Off = 0;
    g_p5Verify = 0;
    g_p5NSites = 0;
    g_p5GuardLock = (void*)(uintptr_t)1;  // another world: the first START reads L2
    static float sp[16] = {1.5f, 2.0f, 3.0f}, gp[32] = {0};
    gp[16] = 3.5f; gp[17] = 4.0f; gp[18] = 5.0f;
    int s[4] = {(int)(uintptr_t)sp, (int)(uintptr_t)(sp + 16), (int)(uintptr_t)(sp + 16), 0};
    int g[4] = {(int)(uintptr_t)gp, (int)(uintptr_t)(gp + 32), (int)(uintptr_t)(gp + 32), 0};
    static unsigned __int64 ign[2] = {0x123, 0x456}, idset[3] = {0x11, 0x22, 0x33};
    u8 opts[0x100] = {};
    *(DWORD*)(opts + 0x38) = (DWORD)(uintptr_t)ign;
    *(DWORD*)(opts + 0x3C) = (DWORD)(uintptr_t)(ign + 2);
    *(DWORD*)(opts + 0x08) = (DWORD)(uintptr_t)idset;
    *(DWORD*)(opts + 0x0C) = (DWORD)(uintptr_t)(idset + 3);
    g_p4IdSet = idset;
    *(DWORD*)(opts + 0x78) = 0x7777;
    int list[4] = {0, 0, 0x5A5A, 0x6B6B};

    // taken: one clone run, its door ask answered by the game's chain (disarmed, pools handed back), no game plan
    const LONG taken = k.m2Taken, asks = k.m2DoorAsk, h1 = k.m2DoorHist[1], live0 = g_p4LiveCalls, clone0 = g_p4CloneCalls;
    g_p5PoolIn = g_p5PoolOut = g_p5DoorCalls = 0;
    int o2;
    u8 r = P5RunJob(0, list, s, g, opts, &o2);
    assert(r == 1 && o2 == 1 && k.m2Taken == taken + 1 && g_p4CloneCalls == clone0 + 1 && g_p4LiveCalls == live0);
    assert(k.m2DoorAsk == asks + 1 && k.m2DoorHist[1] == h1 + 1 && g_p5DoorCalls == 1 && g_p5PoolIn == 2 && g_p5PoolOut == 2);
    assert(k.m2Guard == 0 && (k.m2Ready & 4) && !g_p4Job.armed && !t_snapRun && !SnLive() && g_p4Buf[0] == 0);  // no record
    // the route's list holds exactly the answer the shadow compare would compare: the §13 answer JSON of the list equals the
    // one of the clone run's capture (g_p4Copy), node for node; the head's allocator words are the route's own
    static P4Ans got;
    assert(P4CaptureAnswerRaw(list, r, 0, o2, &got) && got.nPaths == 1 && got.nNodes == 3);
    static char ja[8192], jb[8192];
    P5AnsStr(g_p4Copy, ja, sizeof(ja));
    P5AnsStr(got, jb, sizeof(jb));
    assert(!strcmp(ja, jb) && strstr(ja, "\"nodes\":[[10,-100,20,0,0,-1],[11,-100,19,1.25,0,-1],[12,-100,18,2.5,8,-1]]"));
    const int* first = (const int*)(uintptr_t)list[0];
    assert(first != list && first[1] == (int)(uintptr_t)list && ((const int*)(uintptr_t)list[1])[0] == (int)(uintptr_t)list);
    assert(list[2] == 0x5A5A && list[3] == 0x6B6B);
    P4FakeListFree(list, NULL);

    // verify (VerifyEvery=1): the clone run, then the game's plan; the game's answer kept; one §13 record v3 with "m2":1
    g_p5Verify = 1;
    const LONG ver = k.m2Verify, vm0 = k.m2VerM[0], vm2 = k.m2VerM[2], bad = k.m2VerBad, jsonl = k.snJsonl;
    r = P5RunJob(0, list, s, g, opts, &o2);
    assert(r == 1 && o2 == 1 && g_p4LiveCalls == live0 + 1 && g_p4CloneCalls == clone0 + 2 && k.m2Verify == ver + 1 && k.m2VerM[0] == vm0 + 1);
    assert(strstr(g_p4Buf, "{\"v\":3,") && strstr(g_p4Buf, "\"ic\":\"ReplanFromPosition\"") && strstr(g_p4Buf, "\"nr\":0,\"m2\":1,") &&
           strstr(g_p4Buf, "\"refused\":null") && k.snJsonl == jsonl + 1 && k.m2Taken == taken + 1);
    P4FakeListFree(list, NULL);
    r = P5RunJob(2, list, s, g, opts, &o2);  // the clone's goal index differs: M2, logged, the game's index kept, Mode=2 stays on
    assert(o2 == 1 && k.m2VerM[2] == vm2 + 1 && k.m2VerBad == bad + 1 && g_p5On && strstr(g_p4Buf, "\"stale\":false"));
    P4FakeListFree(list, NULL);
    // 0.36.23: the same difference with a recorded write between the START cut and the job is time skew (M5), not M2
    const LONG vm5 = k.m2VerM[5];
    g_p5WriteAfterCut = true;
    r = P5RunJob(2, list, s, g, opts, &o2);
    g_p5WriteAfterCut = false;
    assert(o2 == 1 && k.m2VerM[2] == vm2 + 1 && k.m2VerM[5] == vm5 + 1 && k.m2VerBad == bad + 1 && strstr(g_p4Buf, "\"stale\":true"));
    P4FakeListFree(list, NULL);
    g_p5Verify = 0;

    // fallbacks: each request still gets the game's own plan
    P4SetCloneProvider(NULL);
    P5Fallback(0, &k.m2Fb[P4_AB_CLONE], list, s, g, opts);  // no clone provider
    P4SetCloneProvider(&kP4Fake);
    list[0] = list[1] = (int)(uintptr_t)list;
    P4AddPath(list, 2, 2, -100.0f);  // the route's list not empty at entry
    const LONG fbList = k.m2FbList;
    P5Fallback(0, &k.m2Fb[P4_AB_CAPTURE], list, s, g, opts, true);
    assert(k.m2FbList == fbList + 1);
    k.rtBusy = 1;  // a save in progress at entry
    P5Fallback(0, &k.m2Fb[P4_AB_EPOCH], list, s, g, opts);
    k.rtBusy = 0;
    const LONG freed = g_fpListFree;
    P5Fallback(12, &k.m2Fb[P4_AB_EPOCH], list, s, g, opts);  // stale cut: the world moved during the clone run
    assert(g_fpListFree > freed);                            // the clone's private list released, not leaked into the route
    P5Fallback(6, &k.m2FbNr, list, s, g, opts);               // a destroy in the window (nr 4): M5 skew
    P5Fallback(9, &k.m2Fb[P4_AB_LAZY], list, s, g, opts);    // D4 lazy stop
    guard12 = 0;
    P5Fallback(0, &k.m2Fb[P4_AB_COLD], list, s, g, opts);    // cold statics
    guard12 = 1;
    // M2-2: three tripwires (a managed call outside an ask) turn Mode=2 off; then jobs run as plain A10 jobs
    const LONG trips = k.m2Trips;
    for (int i = 0; i < 3; i++) P5Fallback(13, &k.m2Fb[P4_AB_MANAGED], list, s, g, opts);
    assert(k.m2Trips == trips + 3 && !g_p5On && k.m2Off == 2 && !(k.m2Ready & 1));
    const LONG plans = k.m2Plans, armed = k.m2Armed;
    r = P5RunJob(0, list, s, g, opts, &o2);
    assert(r == 1 && o2 == 1 && k.m2Plans == plans && k.m2Armed == armed && g_p4LiveCalls == live0 + 14);
    P4FakeListFree(list, NULL);
    g_p5On = 1, k.m2Off = 0, k.m2Ready |= 1;
    // the runtime switch (`--ab routesnap`): 0 = plain A10 jobs
    k.m2Take = 0;
    P5RunJob(0, list, s, g, opts, &o2);
    assert(k.m2Armed == armed && g_p4LiveCalls == live0 + 15);
    P4FakeListFree(list, NULL);
    k.m2Take = 1;
    // M2-1: a snapshotted site whose bytes change (a foreign hook installed after us) turns Mode=2 off at the next START
    static u8 site[8] = {0xE8, 1, 2, 3, 4, 0x90};
    P5SnapAt((DWORD)(uintptr_t)site);
    P5SnapAt((DWORD)(uintptr_t)site);  // once
    assert(g_p5NSites == 1);
    P5RunJob(0, list, s, g, opts, &o2);  // unchanged: taken
    assert(k.m2Taken == taken + 2);
    P4FakeListFree(list, NULL);
    site[0] = 0xE9;
    P5RunJob(0, list, s, g, opts, &o2);
    assert(!g_p5On && k.m2Off == 1 && k.m2SiteAt == (LONG)(uintptr_t)site && o2 == 1 && k.m2Taken == taken + 2);
    P4FakeListFree(list, NULL);
    g_p5NSites = 0, g_p5On = 1, k.m2Off = 0, k.m2SiteAt = 0, k.m2Ready |= 1;
    // M2-3: MonoPatcher's bytes logged once per finding, Mode=2 stays on
    l2[0][0] = 0xE9;
    l2[2][1] = 0xE9;
    P5ModGuard();
    P5ModGuard();
    assert(k.m2Guard == (1 | 4) && g_p5On);
    // docs/157 (TS3N_FAULTINJ): a refusal storm, 64 clone builds out of memory in a row: each released to the provider and
    // answered by the game's plan, Mode=2 stays on (no breaker by design: a refusal costs the request its clone run only)
    {
        g_fiMask = FI_OOM, g_fiEvery = 1, g_fiN = 0, g_fiTurn = 0;
        const LONG mat = k.m2Fb[P4_AB_MAT], taken0 = k.m2Taken;
        for (int i = 0; i < 64; i++) {
            g_pvRel = 0;
            P5Fallback(0, &k.m2Fb[P4_AB_MAT], list, s, g, opts);
            assert(g_pvRel == 0x100 && g_fiPlan == 0);  // released with no flags, the hold not left to time out
        }
        assert(k.m2Fb[P4_AB_MAT] == mat + 64 && k.m2Taken == taken0 && g_p5On && g_p4On);
        // a foreign ReplanFromPosition job with FbJobCapMs (the deadline already past on this thread): the clone run is not
        // under CallPlanBudget, so the job cap never cuts a clone search short; the clone answer is taken whole
        g_fiMask = FI_BUDGET;
        const LONG ready = k.budgetReady;
        k.budgetReady = 1, t_fbCapUs = 1, t_fbDeadline = 1, t_jobT0 = 1;
        int o2b;
        P5RunJob(0, list, s, g, opts, &o2b);
        assert(g_fiBudgetOn == 0 && k.m2Taken == taken0 + 1 && o2b == 1);
        k.budgetReady = ready, t_fbCapUs = 0, t_fbDeadline = 0, t_jobT0 = 0, g_fiMask = 0;
        P4FakeListFree(list, NULL);
    }
    // a fault in the clone run: P4 and Mode=2 off for the session, the request answered by the game's plan
    static u8 fkHeap[0x4E8];
    static DWORD fkHeapSlot;
    InitializeCriticalSection(&g_fkEaLock.cs);
    *(void**)(fkHeap + 0x4E4) = &g_fkEaLock, fkHeapSlot = (DWORD)(uintptr_t)fkHeap;
    DWORD* const savedEaHeap = g_p4EaHeap;
    g_p4EaHeap = &fkHeapSlot;
    const LONG faults = k.snShadowFault;
    P5Fallback(5, &k.m2Fb[P4_AB_FAULT], list, s, g, opts);
    assert(k.snShadowFault == faults + 1 && !g_p4On && !(k.snP4Ready & 1) && !g_p5On && k.m2Off == 3 && !g_fkEaLock.cs.OwningThread);
    g_p4EaHeap = savedEaHeap;
    DeleteCriticalSection(&g_fkEaLock.cs);

    for (int i = 0; i < 5; i++) g_p5L2[i].at = l2Save[i];
    for (int i = 0; i < 4; i++) g_p4Guards[i] = guardsSave[i];
    P4SetCloneProvider(NULL);
    g_p4PoolsEnterJob = poolIn;
    g_p4PoolsLeaveJob = poolOut;
    g_planOrig = planSave;
    g_tab = tab;
    k.workerMode = workerMode;
    k.budgetMode = budgetMode;
    k.snP4Mode = 0;
    k.snP4Ready = 0;
    g_p4On = 0;
    g_p5On = 0;
    printf("P5 (Mode=2): counters v61; ReplanFromPosition answer taken from the clone run (no game plan), its list node for node "
           "the answer the shadow compare reads; door ask to the game's chain disarmed (pools handed back); verify records v3 "
           "\"m2\":1, M2 kept the game's answer; fallbacks clone / list / rtBusy / stale cut / nr / lazy / cold / tripwires (3 = off) "
           "/ fault (off) all planned by the game; runtime switch; M2-1 site change off; M2-3 L2 log only ok\n");
}

#include "monothread-test.h"  // TestMonoThreadM5 (P3, P4, worker; docs/30 §14)
#include "arenaguard-test.h"  // TestArenaGuard ([ArenaGuard], docs/67 §22.2)
#include "gcroots-test.h"  // TestGcRoots (mono fault task line, [GcRootCheck], docs/67 §22.4)
#include "arenatail-test.h"  // TestArenaTail ([ArenaGuard] TailPad, [GcRootCheck] TailWatch, docs/67 §22.7)
#include "p5b-test.h"  // TestP5b (P5b, docs/94 §22.9)
#include "hitchattr-test.h"  // TestHitchAttr ([HitchAttr], docs/96 §9)
#include "unseen-test.h"  // TestUnseen ([UnseenProbe], docs/143)
#include "ghost-test.h"   // TestUnseenGhost ([UnseenGhost], docs/202 §3.9.5)
#include "thumbstall-test.h"  // TestThumbLod ([ThumbStall] Actions 3 and 4, thumbstall.cpp)
#include "unseenanim-test.h"  // TestUnseenAnim ([UnseenAnim], docs/144 §3.4)
#include "unseenrm-test.h"  // TestUnseenRootMotion, TestUnseenRootMotion2 ([UnseenRootMotion] Mode 1 / Mode 2, docs/228)
#include "unseentable-test.h"  // TestUnseenTable ([UnseenTable], docs/227 §6)
#include "unseencost-test.h"  // TestUnseenCost ([UnseenCost] probe, docs/227)
#include "unseenfb-test.h"  // TestUfCam ([UnseenFallback] camera queries, docs/202 §2.1)
#include "unseenfbc-test.h"  // TestUnseenFallbackSites ([UnseenFallback] Sites C1-C4, docs/202 §3.4)
#include "p5c-test.h"  // TestP5cIn (P5c, docs/94 §22.11), run from TestP5b
#include "p5fault-test.h"  // TestP5Fault (docs/157 fault injection), run from TestP5b
#include "satb-test.h"  // TestSatbLog ([AutShadow] Satb log, docs/206 §7)
#include "ilpatch-test.h"  // TestIlPatch ([IlPatch], docs/506 §2, docs/526)
#include "mintcall-test.h"  // TestMintCall ([MonoFast] CallPath, docs/139 §8)
#include "monojit-test.h"  // TestMonoJit ([MonoJit], docs/527 step 1)
#include "mintfuse-test.h"  // TestMintFuse ([MintFuse] Sub, docs/139 §4)
#include "autshadow-test.h"  // TestAutShadow ([AutShadow] part 1, docs/122 §13: T1, T2, T4)
#include "threadstack-test.h"  // TestThreadStack ([ThreadStack], docs/66 §6)
#include "userdiag-test.h"  // TestUserDiag ([UserDiag], docs/188)
#include "played-test.h"    // TestPlayed (played.cpp, docs/14 section 8.6)
#include "memdiag-test.h"   // TestMemDiag ([MemDiag], docs/190 phase 1, docs/191)
#include "memexp-test.h"    // TestMemExp ([MemDiag] TypeGrowth, [GC] SqrtHeapLimit, docs/199)
#include "gcgrow-test.h"    // TestGcGrowLog ([GcGrowLog], [GC] BlackListCapKB, docs/232 Run 8l/8n)
#include "allocsites-test.h"  // TestAllocSites ([MemDiag] AllocSites, docs/201)
#include "crashreport-test.h"  // TestCrashReport ([CrashReport], docs/135)
#include "watchpt-test.h"  // TestWatchpoint ([Watchpoint], docs/157 §8)
#include "crashfx-test.h"  // TestCrashForensics ([CrashForensics], docs/157 §11.7)
#include "apex-test.h"  // TestApex ([Compat] ApexStandDown, docs/148 §7)
#include "compaction-test.h"  // TestCompaction ([Compaction], docs/204)
#include "saveslim-test.h"  // TestSaveSlim ([SaveSlim], docs/15 phase 11)
#include "rctrim-test.h"  // TestRcTrim ([ResourceCache] TrimAfterCleanup, docs/198 §6)
#include "lptrim-test.h"  // TestLotPoolTrim ([LotPool] Trim, docs/541)
#include "castrim-test.h"  // TestCasTrim ([CasTrim], docs/191 step 2)
#include "uafcheck-test.h"  // TestUafCheck ([UafCheck], docs/539 §17)
#include "slotguard-test.h"  // TestSlotGuard ([SlotGuard], [UafCheck] Object frees, docs/539 §18)
#include "float5free-test.h"  // TestFloat5Free ([Float5Free], docs/539 §19)
#include "f5watch-test.h"  // TestFloat5Watch ([Float5Sticky], [Float5Watch], [UafCheck] TravelHold, docs/539 §21)
#include "monosig-test.h"     // TestMonoSigGuard ([MonoSigGuard], docs/539 §20)
#include "s3sscompat-test.h"  // TestS3ssCompat ([S3SSCompat] StaleWriteGuard, docs/539 §22)
#include "pkglock-test.h"  // TestPkgLock ([PkgLock], docs/539 §13.9)
#include "eafix-test.h"   // TestEaCrashFixes ([EaCrashFixes] ConformActor + ThumbPose, docs/67 §26.4 / §27.5)
#include "wastar-test.h"  // TestWorldAStarRefs ([EaCrashFixes] WorldAStarRefs, docs/67 §30)
#include "earlyout-test.h"  // TestEarlyOut ([RouteEarlyOut] probe, docs/142)
#include "routephases-test.h"  // TestRouteJobPhases ([RouteJobPhases], docs/150 §7)
#include "goalgen-test.h"      // TestGoalGen, TestGoalSplit (docs/177: D1 and probe P-G)
#include "fgrender-test.h"     // TestForegroundRender ([Harness] ForegroundRender, docs/162 §7)
#include "syncphases-test.h"   // TestSyncPhases ([RouteJobPhases] SyncPlans, docs/164)
#include "hostcalls-test.h"    // TestHostCalls (HostSplit + DoorMemo, docs/159)
#include "asklent-test.h"      // TestAskLent ([RouteSnap] AskLent + DoorMemoX, docs/189 phase 3)
#include "tailunlock-test.h"  // TestTailUnlock ([RouteAsync] TailUnlock, docs/150 §11)
#include "cellmemo-test.h"  // TestCellMemoProbe ([CellMemoProbe], docs/150 §12)
#include "replandedupe-test.h"  // TestReplanDedupe ([RouteAsync] ReplanDedupe, docs/150 §13)
#include "plandeadline-test.h"  // TestPlanDeadline ([RouteAsync] PlanDeadlineMs / FailMemo, docs/164 §7)
#include "gate0ro-test.h"     // TestGate0Ro ([Gate0RO], docs/152 §G1)
#include "rtotail-test.h"  // TestRouteToObjTail ([RouteToObjRO] Tail); its stubs serve rtoro-test.h
#include "rtoro-test.h"  // TestRouteToObjRO ([RouteToObjRO], docs/140 §12)
#include "fgro-test.h"   // TestPlacementRO ([PlacementRO], docs/174)
#include "gvro-test.h"   // TestGoalValidRO ([GoalValidRO], docs/178 §7 T6)
#include "fsro-test.h"
#include "idlero-test.h"  // TestIdleCheckRO ([IdleCheckRO], docs/178 §8)
#include "icc-test.h"     // TestIdleCheckC ([IdleCheckRO] SiteC, docs/195 §4 C1; uses gvro-test.h fakes)
#include "movedwait-test.h"  // TestMovedWaits ([MovedWaits], docs/178 §9)
#include "p6wait-test.h"  // TestP6Waits ([P6Waits], docs/189 phase 0)
#include "lockfast-test.h"  // TestLockFastPath (docs/165 F1)

// 0.35.9 (docs/94 §22.13, I-94-450..452): P5 (Mode=2) with ReleaseLock=1 and FreeLock=1 on the replay clone provider, in one
// clone's life (no epoch, as with soft resync since 0.35.5); the test thread is the worker, world+0x918 the game's lock:
// (a) 70 taken jobs, a new lock-key table each: every answer the clone's, the window lent each time, one table kept (lane F)
// (b) a bad clone (lane B): refused "mat", the game's plan answers, no window
// (c) a START while a job runs, with another cut pending: no shared cut in Mode=2 (lane D is Mode=1 only)
// (d) a clone footprint whose lock-key origin no table has: kPcKeyFailMax refusals, then one rebuild, then taken again
// (0.35.8 refused it for the rest of the session)
static AsyncJob PcArmM2(DWORD seq, int kind = 3) {  // the sim's START (P4OnStart -> P5OnStart)
    AsyncJob j = {};
    j.kind = kind;
    j.seq = seq;
    j.x87 = GetX87();
    j.mxcsr = _mm_getcsr();
    P4OnStart(&j);
    return j;
}
// the worker's run of an armed job; between: called after P5PreLock (the P6EarlyCopy point) and before the job's +0x918
static u8 PcPlanM2(AsyncJob& j, int* list, int* s, int* g, u8* opts, void (*between)() = NULL) {
    void* const lock = g_rf.world + 0x918;
    static const DWORD kInf = 0xFFFFFFFF;
    P5PreLock(&j);  // ServeAsync: the consumer wait and, with P6EarlyCopy, the own-only copy, before the hold
    if (between) between();
    P5bAcq(lock, NULL, &kInf);  // ServeAsync's hold for the job
    g_async.state = A_RUNNING;
    P4LiveBegin(&j);
    list[0] = list[1] = (int)(uintptr_t)list;
    int o1 = -1, o2 = -1;
    const u8 r = PlanLockedHook(g_rf.world, NULL, list, s, g, 0xA6E, 1, opts, &o1, &o2, 5, 0);
    P5EarlyEnd();  // ServeAsync after the IC
    P4LiveEnd(&j);
    g_async.state = A_IDLE;
    assert(P5bOwner(lock) == (LONG)GetCurrentThreadId() && *(LONG*)((u8*)lock + 0x20) == 1);  // retaken, every level
    P5bRel(lock, NULL);
    assert(!g_p5bWin && !g_p5cJob);
    P4FakeListFree(list, NULL);
    return r;
}
static u8 PcRunM2(DWORD seq, int* list, int* s, int* g, u8* opts, bool writeAfterCut = false, int kind = 3) {
    AsyncJob j = PcArmM2(seq, kind);
    assert(j.p4 == 2 && j.p4Ver);
    if (writeAfterCut) InterlockedIncrement(&g_spWrites);  // a recorded routing write between the START cut and the job
    return PcPlanM2(j, list, s, g, opts);
}
// 0.36.56 (docs/94 §24.14): [RouteSnap] P6EarlyCopy on the same clone life (own-only acquires: the A* object and W' exist)
static void P6eWrite() { InterlockedIncrement(&g_spWrites); }  // a recorded routing write between the early copy and the hold
static void P6eBad() { g_rpBad = 1; }                           // a clone flag set between the early copy and the hold
static void P6eUseFree() {  // nothing keeps g_rpUse: the consumer can drain
    assert(TryAcquireSRWLockExclusive(&g_rpUse));
    ReleaseSRWLockExclusive(&g_rpUse);
}
#include "stalelot-test.h"  // TestStaleLot ([RouteSnap] StaleLot, docs/189 phase 2), run after TestP6Early
static void TestP6Early(int* list, int* s, int* g, u8* opts, DWORD& seq) {
    static_assert(offsetof(Ts3nCounters, p6Early) == 0x4B7C && offsetof(Ts3nCounters, p6eRel) == 0x4B8C &&
                  offsetof(Ts3nCounters, p6eSkip) == 0x4B98 && offsetof(Ts3nCounters, p6eCopyUs) == 0x4BA8 &&
                  offsetof(Ts3nCounters, p6eSpare) == 0x4BAC && sizeof(Ts3nCounters) == 6112 * 4,
                  "counters v122 layout after v121 (docs/registry.md)");
    Ts3nCounters& k = ts3native_counters;
    k.p6Plan = 1, k.p6Share = 1, k.p6Early = 1;
    // (g1) a fresh-cut job: copied before the hold, its plan runs that clone (no acquire in the plan), taken; the phase split
    // books the copy as copy and still adds up; the cut's hold and g_rpUse are free after it
    {
        const LONG cp = k.p6eCopies, used = k.p6eUsed, tk = k.p6Taken[0], lend = g_p5EarlyLend, acq = k.snPcAcquired;
        const LONG t0 = k.p6jTotUs[0], o0 = k.p6jOtherUs[0], c0 = k.p6jCopyUs[0];
        const LONG parts0 = k.p6jQueueUs[0] + k.p6jStartUs[0] + k.p6jRpWaitUs[0] + k.p6jCopyUs[0] + k.p6jCloneUs[0] + k.p6jGameUs[0];
        PcRunM2(seq++, list, s, g, opts, false, 0);
        const LONG tot = k.p6jTotUs[0] - t0, other = k.p6jOtherUs[0] - o0;
        const LONG parts = k.p6jQueueUs[0] + k.p6jStartUs[0] + k.p6jRpWaitUs[0] + k.p6jCopyUs[0] + k.p6jCloneUs[0] + k.p6jGameUs[0] - parts0;
        if (!(k.p6eCopies == cp + 1 && k.p6eUsed == used + 1 && k.p6Taken[0] == tk + 1 && g_p5EarlyLend == lend &&
              k.snPcAcquired == acq + 1 && !g_p5e.live && !g_rpHoldAt && parts + other == tot && other >= -50 &&
              k.p6jCopyUs[0] - c0 >= 0)) {
            printf("P6EarlyCopy (g1): copies %ld used %ld taken %ld lend %ld acquired %ld skip %ld/%ld/%ld refused %ld hold %lu "
                   "total %ld parts %ld other %ld\n", k.p6eCopies - cp, k.p6eUsed - used, k.p6Taken[0] - tk, g_p5EarlyLend - lend,
                   k.snPcAcquired - acq, k.p6eSkip[0], k.p6eSkip[1], k.p6eSkip[2], k.p6eRefused, g_rpHoldAt, tot, parts, other);
            assert(false);
        }
        P6eUseFree();
    }
    // (g2) a Route.Plan and a ReplanFromPosition job on one shared cut: both copied early and taken, the second a same-cut copy
    {
        AsyncJob a = PcArmM2(seq++, 0);
        g_async.state = A_RUNNING;
        AsyncJob b = PcArmM2(seq++, 3);
        g_async.state = A_IDLE;
        assert(b.p6Borrow && b.p4Ver == a.p4Ver);
        const LONG used = k.p6eUsed, tk = k.p6Taken[0], tk3 = k.p6Taken[1], re = k.p6ReAcq;
        PcPlanM2(a, list, s, g, opts);
        PcPlanM2(b, list, s, g, opts);
        assert(k.p6eUsed == used + 2 && k.p6Taken[0] == tk + 1 && k.p6Taken[1] == tk3 + 1 && k.p6ReAcq == re + 1 && !g_rpHoldAt);
        P6eUseFree();
    }
    // (g3) copy, then a recorded routing write before the job's hold: the plan refuses it as stale (P5c, before the acquire)
    // and the early clone goes back with the cut's hold; the next job is taken
    {
        const LONG st = k.p5cStale[0], rel = k.p6eRel[0], cp = k.p6eCopies, tk = k.m2Taken, ep = k.m2Fb[P4_AB_EPOCH];
        AsyncJob a = PcArmM2(seq++, 0);
        PcPlanM2(a, list, s, g, opts, P6eWrite);
        assert(k.p6eCopies == cp + 1 && k.p6eRel[0] == rel + 1 && k.p5cStale[0] == st + 1 && k.m2Taken == tk &&
               k.m2Fb[P4_AB_EPOCH] == ep + 1 && !g_rpHoldAt && !g_p5e.live);
        P6eUseFree();
        PcRunM2(seq++, list, s, g, opts, false, 0);
        assert(k.m2Taken == tk + 1);
    }
    // (g4) a write before the early copy: no copy (skip 1), the plan's own stale refusal as before
    {
        const LONG sk = k.p6eSkip[1], cp = k.p6eCopies, st = k.p5cStale[0], tk = k.m2Taken;
        PcRunM2(seq++, list, s, g, opts, true, 0);
        assert(k.p6eSkip[1] == sk + 1 && k.p6eCopies == cp && k.p5cStale[0] == st + 1 && k.m2Taken == tk && !g_rpHoldAt);
        P6eUseFree();
    }
    // (g5) copy, then a clone flag set before the hold: the plan-entry recheck refuses (mat), clone and hold released
    {
        const LONG rel = k.p6eRel[1], mat = k.m2Fb[P4_AB_MAT], tk = k.m2Taken;
        AsyncJob a = PcArmM2(seq++, 0);
        PcPlanM2(a, list, s, g, opts, P6eBad);
        g_rpBad = 0;
        assert(k.p6eRel[1] == rel + 1 && k.m2Fb[P4_AB_MAT] == mat + 1 && k.m2Taken == tk && !g_rpHoldAt);
        P6eUseFree();
    }
    // (g6) the IC never reaches Route_PlanLocked: ServeAsync's P5EarlyEnd gives the clone and the hold back
    {
        const LONG rel = k.p6eRel[2];
        AsyncJob a = PcArmM2(seq++, 0);
        P5PreLock(&a);
        assert(g_p5e.live && g_p5e.r < 0 && g_p5e.seq == a.seq);
        P5EarlyEnd();
        assert(k.p6eRel[2] == rel + 1 && !g_p5e.live && !g_rpHoldAt);
        P6eUseFree();
        P5EarlyEnd();  // nothing left: no second release
        assert(k.p6eRel[2] == rel + 1);
    }
#ifdef TS3N_FAULTINJ
    // (g7) docs/157 FI_OOM on an early clone: refused as mat after the copy, released, the game's plan answers
    {
        const LONG mat = k.m2Fb[P4_AB_MAT], tk = k.m2Taken, mask = g_fiMask, every = g_fiEvery;
        g_fiMask = FI_OOM, g_fiEvery = 1, g_fiN = 0, g_fiTurn = 0;
        PcRunM2(seq++, list, s, g, opts, false, 0);
        g_fiMask = mask, g_fiEvery = every;
        assert(k.m2Fb[P4_AB_MAT] == mat + 1 && k.m2Taken == tk && !g_rpHoldAt && !g_p5e.live);
        P6eUseFree();
    }
#endif
    // (g8) switch off: no early copy, the plan acquires as before (inside the lent window)
    {
        k.p6Early = 0;
        const LONG cp = k.p6eCopies, lend = g_p5EarlyLend, tk = k.m2Taken;
        PcRunM2(seq++, list, s, g, opts, false, 0);
        assert(k.p6eCopies == cp && g_p5EarlyLend == lend + 1 && k.m2Taken == tk + 1);
    }
    k.p6Early = 0;
    printf("P6EarlyCopy (docs/94 §24.14): own-only copy before the job's hold, the plan runs it (fresh and shared cut, taken); "
           "a write after the copy refuses stale and releases; a write before skips the copy; recheck refuses a clone flag; an "
           "unplanned IC releases at the job end; FI_OOM on the early clone; off = the plan's acquire\n");
}

// docs/189 phase 2c (0.36.57): [RouteSnap] TakeKindsAll (p6All) on the same clone life: own A10 Replan (1) and PlanFromPoint (2)
// jobs armed and taken only with the switch, decided at the START; foreign (af) ones and kinds outside Kinds never; stale and
// verify per kind; the P6 [2] split (Route.Plan / ReplanFromPosition) and its phase split untouched by kinds 1-2
static void TestP6Kinds(int* list, int* s, int* g, u8* opts, DWORD& seq) {
    static_assert(offsetof(Ts3nCounters, p6All) == 0x503C && offsetof(Ts3nCounters, pkArmed) == 0x5040 &&
                  offsetof(Ts3nCounters, pkVerify) == 0x5080 && offsetof(Ts3nCounters, pkUntaken) == 0x50C0 &&
                  offsetof(Ts3nCounters, pkAfSkip) == 0x50D0 && offsetof(Ts3nCounters, pkSpare) == 0x50D8 &&
                  sizeof(Ts3nCounters) == 6112 * 4, "counters v127 layout after the v123-v126 reservations (docs/registry.md)");
    Ts3nCounters& k = ts3native_counters;
    const LONG early = k.p6Early, share = k.p6Share;
    k.p6Early = 0, k.p6Share = 1;
    const LONG p6a0 = k.p6Armed[0], p6a1 = k.p6Armed[1], p6p0 = k.p6Plans[0], p6t0 = k.p6Taken[0], p6t1 = k.p6Taken[1], p6j = k.p6jN[0] + k.p6jN[1];
    for (int kind = 1; kind <= 2; kind++) {
        // off: not armed, counted as what the switch would add
        k.p6All = 0;
        const LONG un = k.pkUntaken[kind];
        AsyncJob a = PcArmM2(seq++, kind);
        assert(a.p4 == 0 && !a.p4Ver && k.pkUntaken[kind] == un + 1);
        // on: armed, taken from the clone
        k.p6All = 1;
        const LONG ar = k.pkArmed[kind], pl = k.pkPlans[kind], tk = k.pkTaken[kind], m2 = k.m2Taken;
        PcRunM2(seq++, list, s, g, opts, false, kind);
        assert(k.pkArmed[kind] == ar + 1 && k.pkPlans[kind] == pl + 1 && k.pkTaken[kind] == tk + 1 && k.m2Taken == m2 + 1 &&
               k.pkPlanUs[kind] >= 0 && !g_rpHoldAt);
        // decided at the START: armed with the switch on, still taken after it went off; armed off, never taken after it went on
        AsyncJob b = PcArmM2(seq++, kind);
        k.p6All = 0;
        PcPlanM2(b, list, s, g, opts);
        assert(k.pkTaken[kind] == tk + 2);
        AsyncJob c = PcArmM2(seq++, kind);
        k.p6All = 1;
        assert(c.p4 == 0);
        // a foreign-task (af) START: never armed (docs/189 phase 4)
        const LONG af = k.pkAfSkip[kind - 1];
        AsyncJob f = {};
        f.kind = kind, f.seq = seq++, f.af = 1;
        P4OnStart(&f);
        assert(f.p4 == 0 && !f.p4Ver && k.pkAfSkip[kind - 1] == af + 1);
        // Kinds (the shadow mask) still gates
        g_p4Kinds = 15 & ~(1 << kind);
        AsyncJob x = PcArmM2(seq++, kind);
        assert(x.p4 == 0);
        g_p4Kinds = 15;
        // a recorded write after the cut: refused stale, the game's plan answers, counted per kind
        const LONG st = k.pkStale[kind], gu = k.pkGameUs[kind];
        PcRunM2(seq++, list, s, g, opts, true, kind);
        assert(k.pkStale[kind] == st + 1 && k.pkTaken[kind] == tk + 2 && k.pkGameUs[kind] >= gu && !g_rpHoldAt);
        // VerifyEvery covers the kind: the clone run is compared, the game's answer kept, the record names the IC
        const LONG ver = g_p5Verify, vv = k.pkVerify[kind], mv = k.m2Verify;
        g_p5Verify = 1;
        AsyncJob v = PcArmM2(seq++, kind);
        assert(v.p4 == 3 && v.p4Ver);
        PcPlanM2(v, list, s, g, opts);
        g_p5Verify = ver;
        assert(k.pkVerify[kind] == vv + 1 && k.m2Verify == mv + 1 && k.pkTaken[kind] == tk + 2 &&
               strstr(g_p4Buf, kind == 1 ? "\"ic\":\"Replan\"" : "\"ic\":\"PlanFromPoint\""));
    }
    // kinds 1-2 leave the P6 split and its phase split alone
    assert(k.p6Armed[0] == p6a0 && k.p6Armed[1] == p6a1 && k.p6Plans[0] == p6p0 && k.p6Taken[0] == p6t0 && k.p6Taken[1] == p6t1 &&
           k.p6jN[0] + k.p6jN[1] == p6j);
    k.p6All = 0, k.p6Early = early, k.p6Share = share;
    printf("P6 all own kinds (docs/189 phase 2c): Replan and PlanFromPoint jobs armed and taken only with TakeKindsAll (decided "
           "at START); foreign (af) and Kinds-gated not; stale refusal and VerifyEvery per kind; P6 split untouched\n");
}

// 0.36.41 (docs/94 §24, P6): TakeKinds and ShareCuts on the same clone life as TestPcMode2 (a)-(e)
// docs/189 §4 phase 4: [RouteSnap] TakeForeign (counters v128). A FallbackAsync foreign Route.Plan START is armed only with the
// switch on, always as a take job (VerifyEvery 1 would verify every own job), and its plan takes the clone answer; the clone
// run carries the job's FbJobCapMs deadline on the world A* cost stubs (t_budget, restored after) only for a foreign job with
// a deadline; past the deadline BudgetStep walls the edges and the job counts as capped
static void TestP6Foreign(int* list, int* s, int* g, u8* opts, DWORD& seq) {
    static_assert(offsetof(Ts3nCounters, p6fbOn) == 0x50DC && offsetof(Ts3nCounters, p6fbCloneCapped) == 0x50F0 &&
                  offsetof(Ts3nCounters, p6fbSpare) == 0x50F4 && sizeof(Ts3nCounters) == 6112 * 4,
                  "counters v128 layout after the v123-v127 pad (docs/registry.md)");
    Ts3nCounters& k = ts3native_counters;
    const LONG verify0 = g_p5Verify, ready0 = k.budgetReady, n0 = (LONG)g_p5N;
    g_p5Verify = 1;
    const LONG capUs0 = k.fbjcCapUs;
    k.fbjcCapUs = 1000;  // a deadline: foreign jobs are take-only (with FbJobCapMs=0 they verify on the own cadence, below)
    // switch off: a foreign START stays unarmed (as (f1))
    AsyncJob f0 = {};
    f0.kind = 0, f0.seq = seq++, f0.fb = 1;
    P4OnStart(&f0);
    assert(f0.p4 == 0 && !f0.p4Ver);
    k.p6fbOn = 1;
    // on: armed as a take job with a cut; the own verify cadence is not advanced
    AsyncJob f = {};
    f.kind = 0, f.seq = seq++, f.fb = 1, f.x87 = GetX87(), f.mxcsr = _mm_getcsr();
    const LONG armed = k.p6fbArmed, taken = k.p6fbTaken, game = k.p6fbGame, dlN = k.p6fbCloneDl, cap = k.p6fbCloneCapped;
    P4OnStart(&f);
    assert(f.p4 == 2 && f.p4Ver && k.p6fbArmed == armed + 1 && (LONG)g_p5N == n0);
    // its plan on the worker (ServeAsync sets g_jobFb and t_fbDeadline for the job): taken; a future deadline reaches the clone
    // run (counted) and t_budget is back to the outer one after it
    LARGE_INTEGER q;
    QueryPerformanceCounter(&q);
    k.budgetReady = 1;
    g_jobFb = 1;
    t_fbDeadline = q.QuadPart + g_qpf.QuadPart * 60;
    t_fbCapHit = 0;
    PcPlanM2(f, list, s, g, opts);
    assert(k.p6fbTaken == taken + 1 && k.p6fbGame == game && k.p6fbCloneDl == dlN + 1 && k.p6fbCloneCapped == cap);
    assert(!t_budget.on && !t_fbCapHit);
    // the budget helpers alone: an own job or no deadline changes nothing; a passed deadline caps at the first cost call
    Budget outer = {};
    g_jobFb = 0;
    assert(!P6fbCloneBudget(outer) && !t_budget.on);
    g_jobFb = 1;
    t_fbDeadline = 0;
    assert(!P6fbCloneBudget(outer) && !t_budget.on);
    t_fbDeadline = q.QuadPart;  // already passed
    assert(P6fbCloneBudget(outer) && t_budget.on && t_budget.dl == q.QuadPart);
    assert(BudgetStep(1) == 1 && t_budget.capped);  // a wall cost from the first call past the deadline
    P6fbCloneBudgetEnd(outer);
    assert(!t_budget.on && t_fbCapHit == 1 && k.p6fbCloneCapped == cap + 1);
    // a stale foreign job (a recorded write after its cut) plans with the game, counted as p6fbGame
    AsyncJob f2 = {};
    f2.kind = 0, f2.seq = seq++, f2.fb = 1, f2.x87 = GetX87(), f2.mxcsr = _mm_getcsr();
    P4OnStart(&f2);
    assert(f2.p4 == 2);
    InterlockedIncrement(&g_spWrites);
    t_fbDeadline = 0;
    PcPlanM2(f2, list, s, g, opts);
    assert(k.p6fbGame == game + 1 && k.p6fbTaken == taken + 1 && !g_rpHoldAt);
    // FbJobCapMs=0 (no deadline): a foreign START verifies on the own cadence (integration review); the verify job runs clone
    // + game plan with the game's answer kept (p6fbGame, not p6fbTaken)
    k.fbjcCapUs = 0;
    t_fbCapHit = 0, t_fbDeadline = 0;
    {
        AsyncJob fv = {};
        fv.kind = 0, fv.seq = seq++, fv.fb = 1, fv.x87 = GetX87(), fv.mxcsr = _mm_getcsr();
        const LONG nv = (LONG)g_p5N;
        P4OnStart(&fv);
        assert(fv.p4 == 3 && fv.p4Ver && (LONG)g_p5N == nv + 1);
        PcPlanM2(fv, list, s, g, opts);
        assert(k.p6fbGame == game + 2 && k.p6fbTaken == taken + 1);
    }
    k.fbjcCapUs = capUs0;
    g_jobFb = 0, t_fbCapHit = 0, t_fbDeadline = 0;
    k.p6fbOn = 0, k.budgetReady = ready0, g_p5Verify = verify0;
    printf("P6 TakeForeign (docs/189 phase 4): foreign STARTs armed take-only with the switch, clone answer taken, deadline in "
           "the clone run, stale -> game plan: ok\n");
}

static void TestP6(int* list, int* s, int* g, u8* opts, DWORD& seq) {
    static_assert(offsetof(Ts3nCounters, p6Plan) == 0x4660 && offsetof(Ts3nCounters, p6Share) == 0x4664 &&
                  offsetof(Ts3nCounters, p6BorrowMiss) == 0x4688 && offsetof(Ts3nCounters, p6ReAcqUs) == 0x46AC &&
                  offsetof(Ts3nCounters, p6jN) == 0x46B0 && offsetof(Ts3nCounters, p6jOut) == 0x4728 &&
                  offsetof(Ts3nCounters, p6jLines) == 0x4738 &&
                  sizeof(Ts3nCounters) == 6112 * 4, "counters v112 layout after v111 (docs/registry.md)");
    Ts3nCounters& k = ts3native_counters;
    assert(k.version == 912);
    // (f1) TakeKinds 8 (default): a Route.Plan START is not armed; 9: it is, and its answer is the clone's
    {
        k.p6Plan = 0;
        AsyncJob a = PcArmM2(seq++, 0);
        assert(a.p4 == 0 && !a.p4Ver);
        k.p6Plan = 1;
        const LONG armed = k.p6Armed[0], plans = k.p6Plans[0], taken = k.p6Taken[0], t3 = k.p6Taken[1], m2 = k.m2Taken;
        PcRunM2(seq++, list, s, g, opts, false, 0);
        assert(k.p6Armed[0] == armed + 1 && k.p6Plans[0] == plans + 1 && k.p6Taken[0] == taken + 1 && k.p6Taken[1] == t3 &&
               k.m2Taken == m2 + 1 && k.p6PlanUs[0] >= 0);
        // §24.7 phase split: the taken job is one slot-0 record whose phases add up to its total (other >= 0 within 50 us of
        // clock skew), planner entries >= 1, outcome taken; one log line with P6LogMs forced to 1 us
        {
            const LONG n0 = k.p6jN[0], out0 = k.p6jOut[0], lines0 = k.p6jLines, logUs = g_p6LogUs;
            const LONG t0 = k.p6jTotUs[0], q0 = k.p6jQueueUs[0], s0 = k.p6jStartUs[0], w0 = k.p6jRpWaitUs[0], c0 = k.p6jCopyUs[0],
                       r0 = k.p6jCloneUs[0], gm0 = k.p6jGameUs[0], o0 = k.p6jOtherUs[0], pl0 = k.p6jPlans[0];
            g_p6LogUs = 1;
            PcRunM2(seq++, list, s, g, opts, false, 0);
            g_p6LogUs = logUs;
            const LONG tot = k.p6jTotUs[0] - t0, other = k.p6jOtherUs[0] - o0;
            const LONG parts = (k.p6jQueueUs[0] - q0) + (k.p6jStartUs[0] - s0) + (k.p6jRpWaitUs[0] - w0) + (k.p6jCopyUs[0] - c0) +
                               (k.p6jCloneUs[0] - r0) + (k.p6jGameUs[0] - gm0);
            if (!(k.p6jN[0] == n0 + 1 && k.p6jOut[0] == out0 + 1 && parts + other == tot && other >= -50 && parts <= tot + 50 &&
                  k.p6jPlans[0] - pl0 >= 1 && k.p6jCloneUs[0] - r0 > 0 && k.p6jLines == lines0 + 1)) {
                printf("P6 phase split: n %ld out %ld total %ld parts %ld other %ld plans %ld clone %ld lines %ld\n", k.p6jN[0] - n0,
                       k.p6jOut[0] - out0, tot, parts, other, k.p6jPlans[0] - pl0, k.p6jCloneUs[0] - r0, k.p6jLines - lines0);
                assert(false);
            }
        }
        // a FallbackAsync foreign Route.Plan START keeps its own path: never armed
        AsyncJob f = {};
        f.kind = 0, f.seq = seq++, f.fb = 1;
        P4OnStart(&f);
        assert(f.p4 == 0 && !f.p4Ver && k.p6Armed[0] == armed + 2);  // + the phase-split job
        TestP6Foreign(list, s, g, opts, seq);
        // Kinds (the shadow mask) still gates: TakeKinds 9 with Kinds 8 takes no Route.Plan
        g_p4Kinds = 8;
        AsyncJob x = PcArmM2(seq++, 0);
        assert(x.p4 == 0);
        g_p4Kinds = 15;
    }
    // (f2) ShareCuts 0: a START behind a running job gets no cut (as (c)); 1: it borrows the pending one, with no write since it
    {
        k.p6Share = 0;
        AsyncJob a = PcArmM2(seq++, 0);
        assert(a.p4 == 2 && a.p4Ver && !a.p6Borrow);
        g_async.state = A_RUNNING;
        AsyncJob b0 = PcArmM2(seq++, 0);
        assert(b0.p4 == 2 && !b0.p4Ver);
        k.p6Share = 1;
        const LONG bor = k.p6Borrowed[0], bor3 = k.p6Borrowed[1], p4b = k.p4Borrow, m0 = k.p6BorrowMiss[0], m1 = k.p6BorrowMiss[1],
                   m2 = k.p6BorrowMiss[2];
        AsyncJob b = PcArmM2(seq++, 0), c = PcArmM2(seq++, 3);
        assert(b.p4Ver == a.p4Ver && b.p6Borrow && c.p4Ver == a.p4Ver && c.p6Borrow && b.p4W == a.p4W);
        assert(k.p6Borrowed[0] == bor + 1 && k.p6Borrowed[1] == bor3 + 1 && k.p4Borrow == p4b + 2);
        // a recorded routing write since the cut: no borrow (miss 1)
        InterlockedIncrement(&g_spWrites);
        AsyncJob d = PcArmM2(seq++, 0);
        assert(d.p4 == 2 && !d.p4Ver && !d.p6Borrow && k.p6BorrowMiss[1] == m1 + 1);
        InterlockedDecrement(&g_spWrites);  // test only: the cut's count again
        // lot updates pending on the live world: no borrow (miss 2)
        u8* P = (u8*)RpP((const u8*)*g_spWorldSlot + 0x95C);
        assert(P);
        const u8 pend = P[0x10E];
        P[0x10E] = 1;
        AsyncJob e = PcArmM2(seq++, 0);
        P[0x10E] = pend;
        assert(!e.p4Ver && k.p6BorrowMiss[2] == m2 + 1);
        g_async.state = A_IDLE;
        // the jobs run in queue order on the one cut: a (fresh), then b and c (borrowed): each taken, b and c re-acquire it
        const LONG taken = k.p6Taken[0], taken3 = k.p6Taken[1], re = k.p6ReAcq;
        PcPlanM2(a, list, s, g, opts);
        PcPlanM2(b, list, s, g, opts);
        PcPlanM2(c, list, s, g, opts);
        assert(k.p6Taken[0] == taken + 2 && k.p6Taken[1] == taken3 + 1 && k.p6ReAcq == re + 2 && k.p6ReAcqUs >= 0);
        assert(!g_rpHoldAt);  // all three released: the consumer moves on
        // no cut pending at all: miss 0
        g_async.state = A_RUNNING;
        AsyncJob h = PcArmM2(seq++, 0);
        g_async.state = A_IDLE;
        assert(!h.p4Ver && k.p6BorrowMiss[0] == m0 + 1);
        // b0, d, e, h hold no cut (p4Ver 0): their plans refuse before the acquire and the game answers
        const LONG fb = k.m2Taken;
        PcPlanM2(h, list, s, g, opts);
        assert(k.m2Taken == fb);
        // (f3) a borrowed job after a write landed between its borrow and its plan: P5c refuses it early (stale before the
        // acquire), as for a fresh cut
        AsyncJob a2 = PcArmM2(seq++, 0);
        g_async.state = A_RUNNING;
        AsyncJob b2 = PcArmM2(seq++, 0);
        g_async.state = A_IDLE;
        assert(b2.p6Borrow && b2.p4Ver == a2.p4Ver);
        const LONG st0 = k.p5cStale[0], t0 = k.p6Taken[0];
        PcPlanM2(a2, list, s, g, opts);
        InterlockedIncrement(&g_spWrites);  // the sim wrote after a2 ran, before b2's plan
        PcPlanM2(b2, list, s, g, opts);
        assert(k.p6Taken[0] == t0 + 1 && k.p5cStale[0] == st0 + 1 && !g_rpHoldAt);
        PcRunM2(seq++, list, s, g, opts, false, 0);  // a fresh cut again: taken
        assert(k.p6Taken[0] == t0 + 2);
    }
    TestP6Early(list, s, g, opts, seq);
    TestStaleLot(list, s, g, opts, seq);
    TestP6Kinds(list, s, g, opts, seq);
    k.p6Plan = 0, k.p6Share = 0;
    printf("P6 (docs/94 §24): Route.Plan jobs armed and taken only with TakeKinds bit 1 (own STARTs; FallbackAsync foreign and "
           "Kinds-gated not); ShareCuts: STARTs behind a running job borrow the pending cut (Route.Plan and ReplanFromPosition), "
           "none after a recorded write or with lot updates pending; 3 jobs on one cut all taken, 2 re-acquires; phase split adds up; a write before a "
           "borrowed job's plan refuses it (stale)\n");
}
static void TestPcMode2(int* list, int* s, int* g, u8* opts) {
    Ts3nCounters& k = ts3native_counters;
    void* const lock = g_rf.world + 0x918;
    u8 w918[0x28];
    memcpy(w918, lock, sizeof(w918));
    memset(lock, 0, sizeof(w918));
    InitializeCriticalSection((CRITICAL_SECTION*)lock);
    const LockAcquireFn savedAcq = g_lockOrig;
    const LockReleaseFn savedRel = g_rgRelOrig;
    void* const savedRgLock = g_rgLock;
    const AsyncSlot savedAsync = g_async;
    const LONG mode = k.snP4Mode, kinds = g_p4Kinds, verify = g_p5Verify, nSites = g_p5NSites;
    void* const guardLock = g_p5GuardLock;
    g_lockOrig = P5bAcq, g_rgRelOrig = P5bRel, g_rgLock = lock;
    g_async.lock = lock, g_async.state = A_IDLE;
    k.snP4Mode = 2, g_p4Kinds = 15, k.p6Plan = 0, k.p6Share = 0;
    g_p5On = 1, k.m2Take = 1, k.m2Ready = 1, k.m2Off = 0, g_p5Verify = 0, g_p5NSites = 0, g_p5GuardLock = lock;
    g_p5bOn = 1, k.p5bRelease = 1, k.p5bGates = 0;
    g_p5cOn = 1, k.p5cFree = 1, k.p5cOff = 0;
    DWORD seq = 900;
    // (a)
    const RpClone* const cl0 = g_rpClone;
    const LONG taken0 = k.m2Taken, wins0 = k.p5bWins, jobs0 = k.p5cJobs;
    for (int i = 0; i < 70; i++) {
        if (i % 7 == 0) RfStep();
        g_spKeysDirty = 1;
        const LONG t = k.m2Taken;
        PcRunM2(seq++, list, s, g, opts);
        if (k.m2Taken != t + 1) { printf("Mode=2 lock-key tables: job %d not taken (fallbacks mat %ld)\n", i, k.m2Fb[P4_AB_MAT]); assert(false); }
    }
    assert(g_rpClone == cl0 && g_pcWork.nKeys == 1 && k.m2Taken == taken0 + 70 && k.p5bWins == wins0 + 70 && k.p5cJobs == jobs0 + 70);
    // (b)
    LONG mat = k.m2Fb[P4_AB_MAT], wins = k.p5bWins, taken = k.m2Taken, early = g_p5EarlyLend;
    g_rpBad = 1;
    PcRunM2(seq++, list, s, g, opts);  // 0.35.26: a window opened for the copy is closed again before the game's plan
    assert(k.m2Fb[P4_AB_MAT] == mat + 1 && k.m2Taken == taken && k.p5bWins == wins + (g_p5EarlyLend - early));
    g_rpBad = 0;
    PcRunM2(seq++, list, s, g, opts);
    assert(k.m2Taken == taken + 1);
    // (c)
    {
        AsyncJob a = {}, b = {};
        a.kind = b.kind = 3;
        a.seq = seq++, b.seq = seq++;
        const LONG borrow = k.p4Borrow, miss = k.p4BorrowMiss;
        P4OnStart(&a);
        assert(a.p4 == 2 && a.p4Ver);
        g_async.state = A_RUNNING;
        P4OnStart(&b);
        g_async.state = A_IDLE;
        assert(b.p4 == 2 && !b.p4Ver && k.p4Borrow == borrow && k.p4BorrowMiss == miss);
        P4CloneDrop(a.p4Ver);
        assert(!g_rpHoldAt);
    }
    // (d)
    u8* fp = NULL;
    for (DWORD e = 0; e < g_rpClone->fps.cap && !fp; e++)
        if (g_rpClone->fps.k[e] > 1 && *(u8**)((u8*)(uintptr_t)g_rpClone->fps.v[e] + 0x20)) fp = (u8*)(uintptr_t)g_rpClone->fps.v[e];
    assert(fp);
    static u8 foreign[0x40];
    u8* const set0 = *(u8**)(fp + 0x20);
    *(u8**)(fp + 0x20) = foreign + 16;  // an origin no lock-key table has
    mat = k.m2Fb[P4_AB_MAT], taken = k.m2Taken;
    const RpClone* const cl1 = g_rpClone;
    for (int n = 0; n < kPcKeyFailMax; n++) {
        g_spKeysDirty = n & 1;  // with and without a new table
        assert(!g_rpNeedEpoch);
        PcRunM2(seq++, list, s, g, opts);
    }
    assert(k.m2Fb[P4_AB_MAT] == mat + kPcKeyFailMax && k.m2Taken == taken && g_rpNeedEpoch && g_rpClone == cl1);
    *(u8**)(fp + 0x20) = set0;  // the old clone is freed with its own pointer
    PcRunM2(seq++, list, s, g, opts);  // the START cut rebuilds; the new clone's footprints hold live sets
    assert(g_rpClone != cl1 && !g_rpNeedEpoch && k.m2Taken == taken + 1 && g_pcWork.nKeys == 1 && !g_pcWork.keyFails);
    // (e) 0.35.26 (docs/94 §22.30): with the A* object and W' made by the jobs above, the copy runs inside the lent window (one
    // window per job, taken); a routing write after the START cut skips the acquire and the run, drops the cut's hold, and the
    // game's plan answers with no window
    {
        PcRunM2(seq++, list, s, g, opts);  // the same R image as the last acquire from here on
        early = g_p5EarlyLend, taken = k.m2Taken, wins = k.p5bWins;
        PcRunM2(seq++, list, s, g, opts);
        assert(g_p5EarlyLend == early + 1 && k.m2Taken == taken + 1 && k.p5bWins == wins + 1);
        const LONG st0 = k.p5cStale[0], acq0 = k.snPcAcquired, ep0 = k.m2Fb[P4_AB_EPOCH], es0 = g_p5EarlyStale, dr0 = g_p5Drops;
        wins = k.p5bWins, taken = k.m2Taken;
        PcRunM2(seq++, list, s, g, opts, true);
        assert(k.p5cStale[0] == st0 + 1 && k.snPcAcquired == acq0 && k.m2Fb[P4_AB_EPOCH] == ep0 + 1 && k.m2Taken == taken &&
               k.p5bWins == wins && g_p5EarlyStale == es0 + 1 && g_p5Drops == dr0 + 1 && !g_rpHoldAt);
        PcRunM2(seq++, list, s, g, opts);  // the next job takes its answer again
        assert(k.m2Taken == taken + 1);
    }
    TestP6(list, s, g, opts, seq);
    g_p5On = 0, k.m2Take = 0, g_p5bOn = 0, k.p5bRelease = 0, g_p5cOn = 0, k.p5cFree = 0;
    g_p5Verify = verify, g_p5NSites = nSites, g_p5GuardLock = guardLock;
    k.snP4Mode = mode, g_p4Kinds = kinds;
    g_lockOrig = savedAcq, g_rgRelOrig = savedRel, g_rgLock = savedRgLock, g_async = savedAsync;
    DeleteCriticalSection((CRITICAL_SECTION*)lock);
    memcpy(lock, w918, sizeof(w918));
    printf("P5 / P5b / P5c on the replay clone, one clone life (0.35.9): 70 taken jobs with a new lock-key table each (window lent "
           "each time, one table kept); bad clone refused (game plan); no shared cut in Mode=2; a key origin no table has: %d "
           "refusals, then one rebuild, then taken; 0.35.26: copy inside the lent window, a stale job skips acquire and run, its "
           "cut hold dropped\n", kPcKeyFailMax);
}

// 0.35.19 (docs/94 §22.22): a Mode=2 job waits for the replay consumer before it takes +0x918 (the consumer applies only under
// it), until the consumer is at the job's cut and idle, bounded by kPcWaitMs; Mode=1, no cut or a passed cut: no wait
static DWORD WINAPI TestP5PreLockConsumer(void*) {
    Sleep(2);
    g_rpPhase = 1;  // applying, then caught up and running RpGc (still holding g_rpUse)
    g_rpTail = 105;
    Sleep(3);
    g_rpPhase = 0;
    return 0;
}
static void TestP5PreLock() {
    const DWORD tail = g_rpTail, head = g_rpHead;
    const bool thr = g_rpThreaded;
    const P4CloneProvider* const pv = g_p4Clone;
    const LONG ph = g_rpPhase;
    const HANDLE ev = g_rpEvent;
    g_rpEvent = NULL;  // no consumer thread here: SetEvent must not reach a recycled handle (0.33.3)
    g_rpThreaded = true, g_p4Clone = &kPcProvider;
    g_rpTail = 100, g_rpHead = 110, g_rpPhase = 0;
    AsyncJob j = {};
    j.p4 = 2, j.p4Ver = 105;
    LARGE_INTEGER t0;
    for (int n = 0; n < 5; n++) {  // (a) the consumer reaches the cut and goes idle: the job starts then, not at the bound
        g_rpTail = 100;
        HANDLE th = CreateThread(NULL, 0, TestP5PreLockConsumer, NULL, 0, NULL);
        assert(th);
        QueryPerformanceCounter(&t0);
        P5PreLock(&j);
        const LONG us = UsSince(t0);
        // under heavy parallel load the fake consumer may miss the bound itself: then the bound ended the wait
        assert((g_rpTail == 105 && !g_rpPhase) || us >= (LONG)kPcWaitMs * 1000 - 16000);
        WaitForSingleObject(th, INFINITE);
        CloseHandle(th);
    }
    g_rpTail = 100;  // (b) never reached: the bound
    QueryPerformanceCounter(&t0);
    P5PreLock(&j);
    LONG us = UsSince(t0);
    assert(us >= (LONG)kPcWaitMs * 1000 - 16000 && g_rpTail == 100);
    QueryPerformanceCounter(&t0);  // (c) no wait: Mode=1 (shadow after J_DONE), no cut, a passed cut, no consumer thread
    j.p4 = 1, P5PreLock(&j);
    j.p4 = 3, j.p4Ver = 0, P5PreLock(&j);
    j.p4Ver = 99, P5PreLock(&j);
    j.p4Ver = 105, g_rpThreaded = false, P5PreLock(&j);
    us = UsSince(t0);
    assert(us < (LONG)kPcWaitMs * 1000 / 2);
    g_rpTail = tail, g_rpHead = head, g_rpThreaded = thr, g_p4Clone = pv, g_rpPhase = ph, g_rpEvent = ev;
    printf("P5 pre-lock (0.35.19): a Mode=2 job waits for the replay consumer at its cut and idle before it takes +0x918; bounded; no wait otherwise\n");
}

// 0.35.20 (docs/94 §22.23): the cut's lot signatures use RpSigMix instead of RpMix. The 0.35.19 signature (copied here) and
// the new one must agree on "changed" for every mutation; then a hook-less node change still rebases its lot in the cut
static u64 RsOldSig(const u8* node) {
    u64 h = RpMix(0xCBF29CE484222325ull, (DWORD)(uintptr_t)node);
    const u8* g = node + 0x10;
    h = RpMix(RpMix(h, g[0x8C]), RpD(g + 0x40));
    for (DWORD o = 0xA0; o < 0x120; o += 4) h = RpMix(h, RpD(g + o));
    for (DWORD v = 0x44; v <= 0x74; v += 0x10) {
        const u8* b = RpP(g + v);
        const u8* e = RpP(g + v + 4);
        h = RpMix(h, (DWORD)(e - b));
        if (v == 0x44 || v == 0x74) for (const u8* p = b; p && p < e && p < b + 4 * 4096; p += 4) h = RpMix(h, RpD(p));
    }
    return h;
}
static u64 RsOldQSig(const u8* node) {
    u64 h = 0xCBF29CE484222325ull;
    const u8* b = RpP(node + 0x10 + 0x74);
    const u8* e = RpP(node + 0x10 + 0x78);
    for (const u8* p = b; p && p < e && p < b + 64; p += 4) h = RpMix(h, RpD(p));
    return h;
}
static void TestReplaySigMix() {
    Ts3nCounters& k = ts3native_counters;
    static __declspec(align(16)) u8 node[0x200];
    static DWORD vec[4][64];
    DWORD seed = 12345;
    auto rnd = [&](DWORD n) { seed = seed * 1103515245 + 12345; return ((seed >> 8) & 0xFFFFFF) % n; };
    int same = 0, diff = 0;
    for (int step = 0; step < 20000; step++) {
        if (step % 50 == 0) {  // a fresh node
            for (int i = 0; i < 0x200 / 4; i++) ((DWORD*)node)[i] = rnd(0x1000000) * 256 + rnd(256);
            for (int v = 0; v < 4; v++) {
                for (int i = 0; i < 64; i++) vec[v][i] = rnd(0x1000000);
                const DWORD len = v == 3 ? 1 + rnd(16) : rnd(40);
                *(DWORD*)(node + 0x10 + 0x44 + v * 0x10) = (DWORD)(uintptr_t)vec[v];
                *(DWORD*)(node + 0x10 + 0x48 + v * 0x10) = (DWORD)(uintptr_t)(vec[v] + len);
            }
        }
        const u64 o0 = RsOldSig(node), n0 = RpSig(node), oq0 = RsOldQSig(node), nq0 = RpQSig(node);
        const int kind = (int)rnd(7);
        const int nm = 1 + (int)rnd(3);
        for (int m = 0; m < nm; m++) {
            const DWORD bit = 1u << rnd(32);
            switch (kind) {
                case 0: *(DWORD*)(node + 0x10 + 0xA0 + rnd(32) * 4) ^= bit; break;  // transforms
                case 1: node[0x10 + 0x8C] ^= (u8)(1 + rnd(255)); *(DWORD*)(node + 0x10 + 0x40) += rnd(2); break;  // built, level
                case 2: vec[0][rnd(64)] ^= bit; break;  // portals (maybe past the end: no change)
                case 3: vec[3][rnd(16)] ^= bit; break;  // Q pointers (maybe past the end)
                case 4: vec[1][rnd(64)] ^= bit; break;  // +0x54 contents: not in the signature
                case 5: *(DWORD*)(node + 0x10 + 0x30 + rnd(4) * 4) ^= bit; break;  // outside the signature
                default: *(DWORD*)(node + 0x10 + 0x48) -= *(DWORD*)(node + 0x10 + 0x48) > *(DWORD*)(node + 0x10 + 0x44) ? 4 : 0; break;  // shorter
            }
        }
        const bool oc = RsOldSig(node) != o0, nc = RpSig(node) != n0, oqc = RsOldQSig(node) != oq0, nqc = RpQSig(node) != nq0;
        if (oc != nc || oqc != nqc) printf("sig mix step %d kind %d: old %d new %d, q old %d new %d\n", step, kind, oc, nc, oqc, nqc);
        assert(oc == nc && oqc == nqc);
        (oc ? diff : same)++;
    }
    assert(same > 2000 && diff > 2000);
    // end to end: a node change no hook saw (transform, portal) rebases that lot at the next cut; the audit (every cut, all lots) stays equal
    static DWORD simTid;
    const DWORD* savedSim = g_rtSimTidSlot;
    simTid = GetCurrentThreadId();
    g_rtSimTidSlot = &simTid;
    RfSetup(7);
    RfCut();
    const LONG mis0 = k.rpMis, ep0 = k.rpEpochs;
    for (int i = 0; i < 20; i++) {
        const LONG w2 = k.rpRebaseWhy[2];
        u8* g = g_rf.lots[i % g_rf.nLots] + 0x10;
        if (i & 1) ((float*)(g + 0xA0))[i % 32] += 1.0f;
        else (*(DWORD**)(g + 0x44))[0] ^= 0x10;
        RfCut();
        assert(k.rpRebaseWhy[2] == w2 + 1 && k.rpMis == mis0 && k.rpEpochs == ep0);
        RfCut();  // nothing new: no rebase
        assert(k.rpRebaseWhy[2] == w2 + 1 && k.rpMis == mis0);
    }
    // 0.35.24 (docs/94 §22.28, I-94-499): a door-ask byte +0x8F flip with no other node change. The 0.35.23 signature (RsOldSig)
    // misses it, so the clone kept the old byte (recurring BP M2); now the next cut refreshes the lot and the clone holds the new byte
    for (int i = 0; i < 6; i++) {
        u8* live = g_rf.lots[i % g_rf.nLots];
        const u64 key = ((u64)*(DWORD*)(live + 4) << 32) | *(DWORD*)live;
        const u8 was = live[0x10 + 0x8F];
        assert(RpFindLot(g_rpClone, key) && RpFindLot(g_rpClone, key)->node[0x10 + 0x8F] == was);
        const u64 o = RsOldSig(live);
        live[0x10 + 0x8F] ^= 1;
        assert(RsOldSig(live) == o);  // the 0.35.23 shape: no signature change, no rebase
        const LONG w2 = k.rpRebaseWhy[2];
        RfCut();
        assert(k.rpRebaseWhy[2] == w2 + 1 && k.rpMis == mis0 && RpFindLot(g_rpClone, key)->node[0x10 + 0x8F] == (u8)(was ^ 1));
        RfCut();
        assert(k.rpRebaseWhy[2] == w2 + 1);
    }
    RfTeardown();
    g_rtSimTidSlot = savedSim;
    printf("replay signature mix (0.35.20): 20000 random node mutations, old and new signature agree on every change (%d changed, %d not); "
           "20 hook-less node changes rebased at the next cut, audits equal; 6 lone +0x8F flips refresh the clone byte (0.35.24)\n", diff, same);
}

// 0.35.21 (docs/94 §22.24.3, I-94-485): ReplayQuietMs. Cuts with nothing recorded reuse the clone position (a SYNC each, no
// signature scan); a recorded write or a routing IC makes the next cut full; a hook-less node change waits for the next full
// scan (ReplayQuietMs after the last one), which rebases it and counts it as a late catch; audits stay equal
static void TestReplayQuiet() {
    Ts3nCounters& k = ts3native_counters;
    static DWORD simTid;
    const DWORD* savedSim = g_rtSimTidSlot;
    simTid = GetCurrentThreadId();
    g_rtSimTidSlot = &simTid;
    RfSetup(9);
    RfCut();
    g_rpAuditEvery = 0;
    g_rpQuietMs = 250;
    RfCut();  // the first full cut with reuse on
    const LONG q0 = g_rcN[RC_QUIET], ep0 = k.rpEpochs, mis0 = k.rpMis;
    for (int i = 0; i < 5; i++) {
        const DWORD pub = g_rpPub;
        RfCut();
        assert(g_rpPub != pub);  // a SYNC per cut: each P4 cut keeps its own position
    }
    assert(g_rcN[RC_QUIET] == q0 + 5);
    RfIns(RfQOf(0, 0), g_rf.fps[3], 0, true);  // a recorded write: full
    RfCut();
    assert(g_rcN[RC_QUIET] == q0 + 5);
    RfCut();
    assert(g_rcN[RC_QUIET] == q0 + 6);
    SpIcNote(5);  // a routing IC (a portal change no leaf records): full
    RfCut();
    assert(g_rcN[RC_QUIET] == q0 + 6);
    const LONG w2 = k.rpRebaseWhy[2], ls = g_rcLateScans, ll = g_rcLateLots;
    ((float*)(g_rf.lots[1] + 0x10 + 0xA0))[2] += 1.0f;  // hook-less: waits for the full scan
    RfCut();
    assert(g_rcN[RC_QUIET] == q0 + 7 && k.rpRebaseWhy[2] == w2);
    g_rpQFullAt -= 1000;  // ReplayQuietMs passed
    RfCut();
    assert(g_rcN[RC_QUIET] == q0 + 7 && k.rpRebaseWhy[2] == w2 + 1 && g_rcLateScans == ls + 1 && g_rcLateLots == ll + 1);
    g_rpAuditEvery = 1;  // an audit cut is always full
    RfCut();
    assert(g_rcN[RC_QUIET] == q0 + 7 && k.rpMis == mis0 && k.rpEpochs == ep0 && k.rpAudits > 0);
    g_rpQuietMs = 0;
    RfTeardown();
    g_rtSimTidSlot = savedSim;
    printf("replay quiet cuts (ReplayQuietMs): 7 reused with a SYNC each, a recorded write and a routing IC force a full cut, a hook-less "
           "change rebased at the next full scan (counted late), audits equal\n");
}

// 0.35.23 (docs/94 §22.26): (a) a shells-only rebase is published while a map batch is in flight or a P4 run owns the pool
// set (a full one still waits); (b) ReplaySigMs: a hook-less change waits for the next scan, a rebase on a skipped-scan cut
// keeps the other lots' old signatures (negative control: a plain mirror set loses the change, only the audit finds it);
// (c) LotsUnlocked: batches applied outside the lock, their leftovers freed in the next hold; audits equal throughout
static void RfW3(int lot) {  // a new Q on the lot (W3 build): a full rebase with map work
    u8* g = g_rf.lots[lot] + 0x10;
    u8** qv = *(u8***)(g + 0x74);
    RfRemAll(qv[0]);
    DWORD* s = RfSlot();
    s[1] = *(DWORD*)g, s[2] = 0, s[3] = 0, s[4] = 1;
    qv[0] = NULL;
    SnEnter(2, s);
    qv[0] = RfQ(*(DWORD*)g);
    RfIns(qv[0], g_rf.fps[0], 0, true);
}
static void RfAuditAll() {  // every lot hashed at the next cut
    const DWORD e = g_rpAuditEvery, l = g_rpAuditLots;
    g_rpAuditEvery = 1, g_rpAuditLots = 0;
    RfCut();
    g_rpAuditEvery = e, g_rpAuditLots = l;
}
// 0.35.25 (docs/94 §22.29): [RouteSnap] InsFreeInPlace. The same random replay (inserts, removes, replaced footprints, W3 / W4,
// lot adds / removes) with the switch off and on: every-lot audits equal both ways, the clone ends with the same footprint
// count, and with it on the old copies nothing holds are freed in place (no whole-clone repin scan). Plus a BP-size bench of
// the scan an INS no longer pays (~74k clone footprints, as BP's clone map)
static void TestInsFreeInPlace() {
    Ts3nCounters& k = ts3native_counters;
    static DWORD simTid;
    const DWORD* savedSim = g_rtSimTidSlot;
    simTid = GetCurrentThreadId();
    g_rtSimTidSlot = &simTid;
    LONG freed[2], retired[2], scans[2], fps[2];
    for (int on = 0; on < 2; on++) {
        g_rpInsFreeInPlace = on;
        RfSetup(31);
        RfCut();
        const LONG mis0 = k.rpMis, dv0 = k.rpDiverge, fr0 = g_rpInsFreed, rt0 = g_rpInsRetired, rs0 = g_rpRepinScans;
        for (int i = 1; i <= 8000; i++) {
            RfStep();
            if (i % 5 == 0) RfCut();
            if (i % 400 == 0) RfAuditAll(), RfCut();
        }
        RfAuditAll();
        RfCut();
        freed[on] = g_rpInsFreed - fr0, retired[on] = g_rpInsRetired - rt0, scans[on] = g_rpRepinScans - rs0, fps[on] = k.rpFps;
        assert(k.rpMis == mis0 && k.rpDiverge == dv0 && k.rpFaults == 0);
        RfTeardown();
        assert(g_rpBytes == 0 && k.rpFps == 0);
    }
    g_rpInsFreeInPlace = 0;
    if (!(freed[0] == 0 && freed[1] > 0 && scans[1] < scans[0] && freed[1] + retired[1] == retired[0] && fps[0] == fps[1]))
        printf("InsFreeInPlace: freed %ld/%ld retired %ld/%ld scans %ld/%ld fps %ld/%ld\n", freed[0], freed[1], retired[0], retired[1],
               scans[0], scans[1], fps[0], fps[1]);
    assert(freed[0] == 0 && freed[1] > 0 && scans[1] < scans[0] && freed[1] + retired[1] == retired[0] && fps[0] == fps[1]);
    // the scan: RpRepin over a clone footprint table of BP size (top-level copies, none a child of the retired block)
    const DWORD n = 74000;
    u8* buf = (u8*)VirtualAlloc(NULL, n * 64, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    assert(buf);
    RpClone c = {};
    for (DWORD i = 0; i < n; i++) {
        RpFpHdr* h = (RpFpHdr*)(buf + (size_t)((i * 40503u) % n) * 64);  // scattered, as heap blocks are
        h->pad = 0;
        assert(RpTabPut(c.fps, 0x10000000 + i * 16, (DWORD)(uintptr_t)((u8*)h + 16)));
    }
    u8 old[64] = {}, fresh[64] = {};
    LARGE_INTEGER f, t0, t1;
    QueryPerformanceFrequency(&f);
    QueryPerformanceCounter(&t0);
    for (int r = 0; r < 50; r++) RpRepin(&c, old + 16, fresh + 16);
    QueryPerformanceCounter(&t1);
    const double us = (t1.QuadPart - t0.QuadPart) * 1e6 / f.QuadPart / 50;
    RpTabFree(c.fps);
    VirtualFree(buf, 0, MEM_RELEASE);
    printf("InsFreeInPlace: off retired %ld (repin scans %ld), on freed in place %ld + retired %ld (repin scans %ld); audits equal; "
           "one repin scan over %lu clone footprints %.0f us\n", retired[0], scans[0], freed[1], retired[1], scans[1], n, us);
    g_rtSimTidSlot = savedSim;
}
// 0.36.34 (docs/182): [RouteSnap] RecsUnlocked. The same random replay as TestInsFreeInPlace with the switch off and on: every-lot
// audits equal both ways, the same clone footprint count, and with it on every applied INS / REM went through an unlocked
// round (ulRecs), none through a locked hold; no lock trip, no fault
static void TestRecsUnlocked() {
    Ts3nCounters& k = ts3native_counters;
    static_assert(offsetof(Ts3nCounters, ulMode) == 0x4548 && offsetof(Ts3nCounters, ulFaults) == 0x4564 &&
                  sizeof(Ts3nCounters) == 6112 * 4, "counters v105 layout after v104 (docs/registry.md)");
    static DWORD simTid;
    const DWORD* savedSim = g_rtSimTidSlot;
    simTid = GetCurrentThreadId();
    g_rtSimTidSlot = &simTid;
    LONG fps[2], recs[2], insRem[2], rounds[2];
    for (int on = 0; on < 2; on++) {
        g_rpRecsUnlocked = on;
        g_rpInsFreeInPlace = 1;  // the release setting
        RfSetup(31);
        RfCut();
        const LONG mis0 = k.rpMis, dv0 = k.rpDiverge, f0 = k.rpFaults, ur0 = k.ulRecs, un0 = k.ulRounds, lt0 = k.ulLockTrip;
        const LONG ir0 = k.rpApplied[RP_INS] + k.rpApplied[RP_REM];
        for (int i = 1; i <= 8000; i++) {
            RfStep();
            if (i % 5 == 0) RfCut();
            if (i % 400 == 0) RfAuditAll(), RfCut();
        }
        RfAuditAll();
        RfCut();
        fps[on] = k.rpFps, recs[on] = k.ulRecs - ur0, rounds[on] = k.ulRounds - un0;
        insRem[on] = k.rpApplied[RP_INS] + k.rpApplied[RP_REM] - ir0;
        assert(k.rpMis == mis0 && k.rpDiverge == dv0 && k.rpFaults == f0 && k.ulLockTrip == lt0 && !t_rpUl);
        RfTeardown();
        assert(g_rpBytes == 0 && k.rpFps == 0);
    }
    g_rpRecsUnlocked = 0, g_rpInsFreeInPlace = 0;
    // docs/182 §8: a gate entry inside a round: sites 0 / 12 (the lot table, on the clone's copy) pass through, every other
    // site's row only logs (no frame, no lock)
    {
        DWORD fake[2] = {0, 0x00665D36};
        const LONG lt = k.ulLockTrip;
        t_rpUl = 1, g_rpPhase = 2 | RP_INS << 8 | 0x10000;
        for (int s = 0; s < RG_SITES; s++) assert(RpUlGate(s, fake + 1) == (s == 0 || s == 12));
        t_rpUl = 0, g_rpPhase = 0;
        assert(k.ulLockTrip == lt && t_rg.n == 0);
    }
    // docs/182 §8: the apply's LotTable_GetIfPresent ask (RpGetIfPresent at 0x665D1E / 0x666796) inside a round is answered
    // from the clone's own P copy and lot table (the clone's node), the live table is not read; outside a round the caller's
    // table as before; a miss on the clone table answers 0 (the game's body skips the record) and forces the lot
    {
        static const u8* seenP;
        static int calls;
        struct Fk {
            static u8* __fastcall Get(const u8* p, void*, DWORD lo, DWORD hi) {  // the game's table walk: buckets p+0x4C, chain +0x190
                seenP = p, calls++;
                const DWORD* bk = *(const DWORD* const*)(p + 0x4C);
                const DWORD nb = *(const DWORD*)(p + 0x50);
                for (const u8* nd = (const u8*)(uintptr_t)bk[lo % nb]; nd && (DWORD)(uintptr_t)nd != bk[nb]; nd = *(const u8* const*)(nd + 0x190))
                    if (*(const DWORD*)nd == lo && *(const DWORD*)(nd + 4) == hi) return (u8*)nd;
                return NULL;
            }
        };
        RfSetup(31);
        RfCut();
        RpClone* c = g_rpClone;
        assert(c && c->nLots > 0 && c->p != g_rf.p);
        const RpGetIfPresentFn saved = g_rpGetIfPresent;
        g_rpGetIfPresent = Fk::Get;
        const RpLot& l = c->lots[0];
        const DWORD lo = (DWORD)l.key, hi = (DWORD)(l.key >> 32);
        u8* live = RpGetIfPresent(g_rf.p, NULL, lo, hi);
        assert(live && live != l.node && seenP == g_rf.p);
        const LONG lt = k.ulLockTrip, f0 = g_rpNForce;
        t_rpUl = 1;
        u8* clone = RpGetIfPresent(g_rf.p, NULL, lo, hi);
        assert(clone == l.node && seenP == c->p);
        u8* miss = RpGetIfPresent(g_rf.p, NULL, 0x7777, 0x8888);
        assert(!miss && seenP == c->p && (g_rpNForce == f0 + 1 || g_rpForceAll));
        t_rpUl = 0;
        assert(k.ulLockTrip == lt && calls == 3);
        g_rpGetIfPresent = saved;
        RfTeardown();
    }
    if (!(recs[0] == 0 && rounds[0] == 0 && insRem[1] > 0 && recs[1] >= insRem[1] && rounds[1] > 0 && fps[0] == fps[1] && insRem[0] == insRem[1]))
        printf("RecsUnlocked: recs %ld/%ld rounds %ld/%ld ins+rem %ld/%ld fps %ld/%ld\n", recs[0], recs[1], rounds[0], rounds[1], insRem[0],
               insRem[1], fps[0], fps[1]);
    assert(recs[0] == 0 && rounds[0] == 0 && insRem[1] > 0 && recs[1] >= insRem[1] && rounds[1] > 0 && fps[0] == fps[1] && insRem[0] == insRem[1]);
    printf("RecsUnlocked: off 0 unlocked rounds; on %ld rounds applied %ld records outside the lock (all %ld INS / REM); audits equal, same "
           "clone footprint count (%ld)\n", rounds[1], recs[1], insRem[1], fps[1]);
    g_rtSimTidSlot = savedSim;
}
static void TestReplay03523() {
    Ts3nCounters& k = ts3native_counters;
    static DWORD simTid;
    const DWORD* savedSim = g_rtSimTidSlot;
    simTid = GetCurrentThreadId();
    g_rtSimTidSlot = &simTid;
    RfSetup(23);
    RfCut();
    const LONG mis0 = k.rpMis, ep0 = k.rpEpochs, dv0 = k.rpDiverge;
    // (a) in flight: the consumer does not drain between these cuts
    g_rpThreaded = true;
    const HANDLE ev = g_rpEvent;
    g_rpEvent = NULL;
    RfW3(0);
    RpCut(g_rf.world);
    assert(!g_rpCutDeferred && RpRingCount(RP_LOTS) == 1);
    RfW4(1);
    RpCut(g_rf.world);
    assert(!g_rpCutDeferred && RpRingCount(RP_LOTS) == 2);  // the shells went out behind the batch in flight
    RfW3(2);
    RpCut(g_rf.world);
    assert(g_rpCutDeferred && RpRingCount(RP_LOTS) == 2);  // a full one still waits (one map batch in flight)
    RpDrain(false);
    RpCut(g_rf.world);
    assert(!g_rpCutDeferred && RpRingCount(RP_LOTS) == 1);
    RpDrain(false);
    // pools owned by a "P4 run" (this thread holds g_plSet as another owner would)
    AcquireSRWLockExclusive(&g_plSet);
    RfW4(3);
    RpCut(g_rf.world);
    assert(!g_rpCutDeferred && RpRingCount(RP_LOTS) == 1);
    RfW3(4);
    RpCut(g_rf.world);
    assert(g_rpCutDeferred);
    ReleaseSRWLockExclusive(&g_plSet);
    g_rpThreaded = false;
    g_rpEvent = ev;
    RfCut();
    RfAuditAll();
    assert(k.rpMis == mis0 && k.rpEpochs == ep0 && k.rpDiverge == dv0);
    // (b) ReplaySigMs (no audit cuts in between: an audit would find the change and force the lot itself)
    const DWORD ae = g_rpAuditEvery;
    g_rpAuditEvery = 0;
    g_rpSigMs = 250;
    RfCut();
    const LONG w2 = k.rpRebaseWhy[2], late0 = g_rcSigLate;
    g_rpSigAt = GetTickCount64();  // a scan just ran
    ((float*)(g_rf.lots[1] + 0x10 + 0xA0))[3] += 1.0f;  // hook-less
    RfW4(2);  // a shell rebase in the same, unscanned cut: the mirror keeps lot 1's old signature
    RfCut();
    assert(k.rpRebaseWhy[2] == w2);
    RfCut();
    assert(k.rpRebaseWhy[2] == w2);
    g_rpSigAt -= 1000;  // ReplaySigMs passed
    RfCut();
    assert(k.rpRebaseWhy[2] == w2 + 1 && g_rcSigLate == late0 + 1);
    RfAuditAll();
    assert(k.rpMis == mis0);
    // negative control: the same change, then a mirror set without keep (0.35.22's call) on an unscanned cut: lost
    g_rpSigAt = GetTickCount64();
    ((float*)(g_rf.lots[1] + 0x10 + 0xA0))[3] += 1.0f;
    RpMirrorSet(g_rpLive, g_rpNMir);
    g_rpSigAt -= 1000;
    RfCut();
    assert(k.rpRebaseWhy[2] == w2 + 1);  // the scan found nothing
    RfAuditAll();
    assert(k.rpMis > mis0);  // only the audit sees it (and forces the lot)
    RfCut();
    RfAuditAll();
    const LONG mis1 = k.rpMis;
    g_rpSigMs = 0;
    g_rpAuditEvery = ae;
    // (c) LotsUnlocked: random steps with W3 / W4 rebases, applied outside the lock
    g_rpLotsUnlocked = 1;
    const LONG un0 = g_rpLotsUnlockedN;
    for (int i = 1; i <= 400; i++) {
        RfStep();
        if (i % 40 == 0) RfW3(i / 40 % g_rf.nLots);
        if (i % 7 == 0) RfW4(i % g_rf.nLots);
        if (i % 5 == 0) { RfCut(); assert(!g_rpLotsFreePend); }
    }
    RfAuditAll();
    assert(g_rpLotsUnlockedN > un0 + 10 && k.rpMis == mis1 && k.rpFaults == 0 && k.rpEpochs == ep0);
    g_rpLotsUnlocked = 0;
    RfTeardown();
    g_rtSimTidSlot = savedSim;
    printf("replay 0.35.23: shells published past a batch in flight and busy pools (full rebases wait); ReplaySigMs keeps unscanned "
           "signatures (a hook-less change rebased late, %ld late; a plain mirror set loses it: control); %ld batches applied "
           "outside the lock, audits equal\n", g_rcSigLate - late0, g_rpLotsUnlockedN - un0);
}

// 0.35.21 (docs/94 §22.24.5): REM bounds patch. Stand-ins with the logic of the level-box answer 0x80EF80 (box of the level
// entry, widened by +0x30 when +0x34 bit 0x2000000 and the child list is empty, else by z) and the child's 0x806C50 (its parent's
// answer with z = the child's +0x10). The IP shapes: a child copy whose +0x10 is stale (copy widened by 2.0 more than live), a
// box drift of 0.001 (an unrecorded transform), and a copy parent whose child list is empty while live's is not (not patchable)
static float* __fastcall StLevelBox(u8* fp, void*, float* out, DWORD lvl, float z) {
    const float* e = (const float*)(*(u8**)(fp + 0x38) + lvl * 0x24 + 0x10);
    const float w = (*(DWORD*)(fp + 0x34) & 0x2000000) ? (*(u8**)(fp + 0x24) == fp + 0x24 ? *(float*)(fp + 0x30) : z) : 0.f;
    out[0] = e[0] - w, out[1] = e[1] - w, out[2] = e[2] + w, out[3] = e[3] + w;
    return out;
}
static float* __fastcall StChildBox(u8* fp, void*, float* out, DWORD lvl, float) {
    u8* par = *(u8**)(fp + 0xC);
    return ((RpBoundsFn)(uintptr_t)RpD(RpP(par) + 0x2C))(par, NULL, out, lvl, *(float*)(fp + 0x10));
}
// docs/232 Run 8e: the trampoline slot is published before the jmp goes live. A thread calls the target nonstop while
// the detour is installed and removed; its hook calls through the slot, so a late store calls address 0 (crash = FAIL).
static void* volatile g_dtSlot;
static volatile LONG g_dtCalls, g_dtStop;
static int __cdecl DtHook() { return ((int(__cdecl*)())g_dtSlot)() + 1; }
static DWORD WINAPI DtSpin(void* fn) {
    while (!g_dtStop) {
        const int r = ((int(__cdecl*)())fn)();
        assert(r == 42 || r == 43);
        InterlockedIncrement(&g_dtCalls);
    }
    return 0;
}
// docs/232 Run 8e: a detour over `call rel32` re-targets the trampoline's copy at the callee of the LIVE bytes. With
// TS3N_TEST_EXPECTED (both editions) the caller's literal points at another callee, as Steam bytes do on EA 1.69; taking
// the rel32 from the literal returns 9 instead of 7 (the 0x769111 PRIV_INSTRUCTION crash).
static int __cdecl DcHook() { return 1; }
static void TestDetourCallRel() {
    u8* b = (u8*)VirtualAlloc(NULL, 128, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
    const u8 ret7[] = {0xB8, 0x07, 0x00, 0x00, 0x00, 0xC3}, ret9[] = {0xB8, 0x09, 0x00, 0x00, 0x00, 0xC3};
    memcpy(b + 64, ret7, 6), memcpy(b + 96, ret9, 6);
    u8 live[6] = {0xE8, 0, 0, 0, 0, 0xC3}, lit[6] = {0xE8, 0, 0, 0, 0, 0xC3};
    *(LONG*)(live + 1) = 64 - 5;  // call b+64 (ret 7)
    *(LONG*)(lit + 1) = 64 - 5;
    memcpy(b, live, 6);
#ifdef TS3N_TEST_EXPECTED
    *(LONG*)(lit + 1) = 96 - 5;  // the literal's call goes to b+96 (ret 9)
    g_testExpAt = (DWORD)(uintptr_t)b, g_testExpBytes = live;
#endif
    void* tr = NULL;
    assert(InstallDetour("call rel32 site", (DWORD)(uintptr_t)b, lit, 5, (void*)DcHook, &tr) == tr && tr);
#ifdef TS3N_TEST_EXPECTED
    g_testExpBytes = NULL;
#endif
    const int r = ((int(__cdecl*)())tr)();
    printf("detour call rel32: trampoline reaches the live callee (%d, want 7)\n", r);
    assert(r == 7);
    assert(((int(__cdecl*)())(void*)b)() == 1);
}

static void TestDetourPublish() {
    u8* fn = (u8*)VirtualAlloc(NULL, 64, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
    const u8 code[] = {0xB8, 0x2A, 0x00, 0x00, 0x00, 0xC3};  // mov eax,42 ; ret
    memcpy(fn, code, sizeof(code));
    const HANDLE saved = g_log;
    g_log = INVALID_HANDLE_VALUE;  // 200 OK lines
    HANDLE th = CreateThread(NULL, 0, DtSpin, fn, 0, NULL);
    int n = 0;
    for (; n < 200; n++) {
        while (g_dtCalls < 2) SwitchToThread();
        assert(InstallDetour("publish order", (DWORD)(uintptr_t)fn, code, 5, (void*)DtHook, &g_dtSlot) == g_dtSlot && g_dtSlot);
        assert(Write5(fn, code));  // original bytes back, atomically
        g_dtCalls = 0;
        while (g_dtCalls < 2) SwitchToThread();  // the spinner finished a call that began after the restore
        g_dtSlot = NULL;  // a pool slot (TrampAlloc): never freed
        g_dtCalls = 0;
    }
    g_log = saved;
    g_dtStop = 1;
    WaitForSingleObject(th, INFINITE);
    CloseHandle(th);
    VirtualFree(fn, 0, MEM_RELEASE);
    printf("detour publish order: %d installs under a spinning caller, slot set before the jmp\n", n);
}

// TrampAlloc (issue #40): trampolines are slots of a shared 64 KB RWX block, not one 64 KB reservation each
static bool TaRwx(const u8* p, MEMORY_BASIC_INFORMATION* m) {
    return VirtualQuery(p, m, sizeof(*m)) == sizeof(*m) && m->State == MEM_COMMIT && m->Protect == PAGE_EXECUTE_READWRITE;
}
static void TestTrampAlloc() {
    u8* const a = TrampAlloc(32, "test a");
    u8* const b = TrampAlloc(20, "test b");  // rounded up to 32
    MEMORY_BASIC_INFORMATION m;
    assert(a && b && a != b && (a + 32 <= b || b + 32 <= a) && !((uintptr_t)a & 15) && !((uintptr_t)b & 15));
    assert(TaRwx(a, &m) && TaRwx(b, &m));
    for (int i = 0; i < 32; i++) assert(a[i] == 0 && b[i] == 0);
    a[0] = 0xB8, *(LONG*)(a + 1) = 5, a[5] = 0xC3;  // mov eax,5 ; ret
    FlushInstructionCache(GetCurrentProcess(), a, 6);
    assert(((int(__cdecl*)())(void*)a)() == 5);
    // 2048 x 32 bytes fill exactly one block: wherever the current block stands, exactly one new block opens
    const LONG blocks = g_trampBlocks;
    const void* base = NULL;
    u8* prev = NULL;
    int bases = 0;
    for (int i = 0; i < 2048; i++) {
        u8* const p = TrampAlloc(32, "test n");
        assert(p && !((uintptr_t)p & 15) && TaRwx(p, &m) && p[0] == 0);
        if (m.AllocationBase != base) bases++, base = m.AllocationBase;
        else assert(p == prev + 32);  // same block: back to back, no overlap
        prev = p;
    }
    assert(g_trampBlocks == blocks + 1 && bases <= 2);
    printf("trampoline pool: 2048 x 32 bytes in %d block(s) (%ld new), slots 16-aligned, RWX, zero-filled\n", bases, g_trampBlocks - blocks);
}

static void TestRemPatch() {
    static DWORD parVt[0x48 / 4], kidVt[0x48 / 4];
    parVt[0] = kidVt[0] = (DWORD)(uintptr_t)RfLevels1;
    parVt[0x2C / 4] = (DWORD)(uintptr_t)StLevelBox, kidVt[0x2C / 4] = (DWORD)(uintptr_t)StChildBox;
    RpCls cls[2] = {{(DWORD)(uintptr_t)parVt, 0x44, 1, 0}, {(DWORD)(uintptr_t)kidVt, 0x28, 0, 1}};
    const RpCls* savedCls = g_rpCls;
    const int savedN = g_rpNCls;
    const DWORD savedBox = g_rpLevelBox;
    g_rpCls = cls, g_rpNCls = 2, g_rpLevelBox = (DWORD)(uintptr_t)StLevelBox;
    struct Fp { u8 b[0x50]; u8 lvl[0x24]; u8 node[16]; };
    static Fp lp, cp, lk, ck;  // live parent / copy parent / live child / copy child
    auto mk = [](Fp& p, Fp& k, bool listed, float z, float x0) {
        memset(&p, 0, sizeof(p)), memset(&k, 0, sizeof(k));
        *(DWORD*)p.b = (DWORD)(uintptr_t)parVt, *(DWORD*)k.b = (DWORD)(uintptr_t)kidVt;
        *(u8**)(p.b + 0x38) = p.lvl, p.lvl[0x20] = 1;
        float* bx = (float*)(p.lvl + 0x10);
        bx[0] = x0, bx[1] = 4.f, bx[2] = 25.f, bx[3] = 35.f;
        *(float*)(p.b + 0x30) = 2.f, *(DWORD*)(p.b + 0x34) = 0x2000000;
        *(u8**)(k.b + 0xC) = p.b, *(float*)(k.b + 0x10) = z, *(DWORD*)(k.b + 0x14) = 0x78;
        *(DWORD*)(p.b + 0x14) = 0x99;
        if (listed) {  // list head {next, prev}; node {next, prev, footprint}
            *(u8**)p.node = p.b + 0x24, *(u8**)(p.node + 4) = p.b + 0x24, *(u8**)(p.node + 8) = k.b;
            *(u8**)(p.b + 0x24) = *(u8**)(p.b + 0x28) = p.node;
        } else *(u8**)(p.b + 0x24) = *(u8**)(p.b + 0x28) = p.b + 0x24;
    };
    u8 fme[0x14] = {};
    *(DWORD*)(fme + 0x10) = 0x200;
    const LONG pt0 = g_rpRemPatched, d0 = g_rpRemDiff;
    auto run = [&](u8* liveFp, u8* copyFp) {  // the REM record from live, then the replay's compare on the copy
        RpRemLive lv = {};
        RpRemLiveE& e = lv.e[lv.n++];
        float* r = ((RpBoundsFn)(uintptr_t)RpD(RpP(liveFp) + 0x2C))(liveFp, NULL, e.b, 0, 0.f);
        memcpy(e.b, r, 16);
        e.cls = RpD(liveFp), e.lo = RpD(liveFp + 0x14), e.hi = RpD(liveFp + 0x18), e.lvl = 0;
        RpRemPatchTake(e.p, liveFp, 0);
        float b[4];
        const float* rc = ((RpBoundsFn)(uintptr_t)RpD(RpP(copyFp) + 0x2C))(copyFp, NULL, b, 0, 0.f);
        const bool differ = memcmp(e.b, rc, 16) != 0;
        const bool force = differ && RpRemDiff(&lv, copyFp, fme, rc, NULL) == 2;
        return differ ? (force ? 2 : 1) : 0;
    };
    mk(lp, lk, true, 0.f, 2.1f), mk(cp, ck, true, 2.f, 2.1f);  // (1) the child's +0x10: copy 2.0, live 0.0
    assert(run(lk.b, ck.b) == 1 && *(float*)(ck.b + 0x10) == 0.f && g_rpRemPatched == pt0 + 1 && g_rpRemDiff == d0);
    mk(lp, lk, true, 0.f, 5.634f), mk(cp, ck, true, 0.f, 5.633f);  // (2) a box drift on the parent itself (as 0xFEB848)
    assert(run(lp.b, cp.b) == 1 && ((float*)(cp.lvl + 0x10))[0] == 5.634f && g_rpRemPatched == pt0 + 2);
    mk(lp, lk, true, 0.f, 2.1f), mk(cp, ck, false, 0.f, 2.1f);  // (3) copy parent's list empty, live's not: forced
    assert(run(lk.b, ck.b) == 2 && g_rpRemPatched == pt0 + 2 && g_rpRemDiff == d0 + 1);
    mk(lp, lk, true, 0.f, 2.1f), mk(cp, ck, true, 0.f, 2.1f);  // (4) equal: nothing to do
    assert(run(lk.b, ck.b) == 0);
    g_rpCls = savedCls, g_rpNCls = savedN, g_rpLevelBox = savedBox;
    puts("replay REM bounds patch (0.35.21): a stale child +0x10 and a drifted level box are patched to live's answer (no rebase); "
         "a copy parent with another child-list state is still forced");
}

// 0.36.35 (docs/157 §4.1-§4.2): the lock-key cover (RpLockKeyEnter, the 0x82B3A1 stub's body) on a fake live world, and the
// replayed-insert answer check (RpInsLiveTake at the record, RpInsCheck after the insert) on a fake clone map, with
// TestRemPatch's level-box / child stand-ins
static void TestP5Covers() {
    static_assert(offsetof(Ts3nCounters, rpLockKeyForce) == 0x4568 && offsetof(Ts3nCounters, p5bWdEscalate) == 0x4578 &&
                  offsetof(Ts3nCounters, p5StormRunMax) == 0x4584 && sizeof(Ts3nCounters) == 6112 * 4,
                  "counters v106 layout after v105 (docs/registry.md)");
    Ts3nCounters& k = ts3native_counters;
    // the fake replay worlds above (TestSnapReplay .. TestInsFreeInPlace) ran with InsCheck 1: their inserts' differences
    printf("replayed-insert check over the earlier replay tests: %ld differences (InsCheck 1, log only)\n", k.rpInsDiff);
    static DWORD parVt[0x80 / 4], kidVt[0x80 / 4];
    parVt[0] = kidVt[0] = (DWORD)(uintptr_t)RfLevels1;
    parVt[0x2C / 4] = (DWORD)(uintptr_t)StLevelBox, kidVt[0x2C / 4] = (DWORD)(uintptr_t)StChildBox;
    parVt[0x7C / 4] = 0x00806FE0, kidVt[0x7C / 4] = 0x006262B0;  // RpFpKey: id slot / the child's ~parent id
    RpCls cls[2] = {{(DWORD)(uintptr_t)parVt, 0x44, 1, 0}, {(DWORD)(uintptr_t)kidVt, 0x28, 0, 1}};
    const RpCls* savedCls = g_rpCls;
    const int savedN = g_rpNCls;
    const DWORD savedBox = g_rpLevelBox;
    g_rpCls = cls, g_rpNCls = 2, g_rpLevelBox = (DWORD)(uintptr_t)StLevelBox;
    DWORD simTid = GetCurrentThreadId();
    const DWORD* const savedSim = g_rtSimTidSlot;
    g_rtSimTidSlot = &simTid;
    const LONG savedOn = g_rpOn;
    const bool savedHave = g_rpHave;
    g_rpOn = 1, g_rpHave = true;
    g_rpNForce = 0, g_rpForceAll = 0, g_rpNNoQ = 0, g_rpNoQAll = 0;
    struct Fp { u8 hdr[16]; u8 b[0x50]; u8 lvl[0x24]; u8 node[16]; };
    static Fp lp, lk, cp, ck;
    auto mk = [](Fp& p, Fp& c, float z, const u8* live, const u8* liveKid) {  // a parent with one listed child (TestRemPatch's shape)
        memset(&p, 0, sizeof(p)), memset(&c, 0, sizeof(c));
        *(DWORD*)p.b = (DWORD)(uintptr_t)parVt, *(DWORD*)c.b = (DWORD)(uintptr_t)kidVt;
        *(u8**)(p.b + 0x38) = p.lvl, p.lvl[0x20] = 1;
        float* bx = (float*)(p.lvl + 0x10);
        bx[0] = 2.f, bx[1] = 4.f, bx[2] = 25.f, bx[3] = 35.f;
        *(float*)(p.b + 0x30) = 2.f, *(DWORD*)(p.b + 0x34) = 0x2000000 | 0x04000000;  // widened by z; inserted once
        *(DWORD*)(p.b + 0x14) = 0x99;
        *(u8**)(c.b + 0xC) = p.b, *(float*)(c.b + 0x10) = z;
        *(u8**)p.node = p.b + 0x24, *(u8**)(p.node + 4) = p.b + 0x24, *(u8**)(p.node + 8) = c.b, *(const u8**)(p.node + 12) = liveKid;
        *(u8**)(p.b + 0x24) = *(u8**)(p.b + 0x28) = p.node;
        ((RpFpHdr*)p.hdr)->live = (u8*)live;
    };
    // --- A. lock-key cover. Live map (2 buckets): the parent's FME on a Q of lot 0x77; the set pointer changes NULL -> set
    {
        mk(lp, lk, 0.f, NULL, NULL);
        u8 world[0x960] = {}, P[0x40] = {}, M[0x10] = {}, q[0x20] = {}, fme[0x14] = {}, nd[0x14] = {}, set[16] = {};
        DWORD bk[2] = {};
        *(u8**)(world + 0x95C) = P, *(u8**)(P + 0x24) = M, *(DWORD**)(M + 4) = bk, *(DWORD*)(M + 8) = 2;
        *(DWORD*)(q + 0x18) = 0x77, *(DWORD*)(q + 0x1C) = 0;
        *(u8**)(fme + 8) = lp.b, *(u8**)(fme + 0xC) = q;
        *(DWORD*)nd = 0x99, *(DWORD*)(nd + 4) = 0, *(u8**)(nd + 8) = fme;
        bk[0x99 % 2] = (DWORD)(uintptr_t)nd;
        DWORD regs[16] = {};
        DWORD* slot = &regs[8];
        slot[-8] = (DWORD)(uintptr_t)lp.b, slot[-6] = (DWORD)(uintptr_t)set, slot[-4] = (DWORD)(uintptr_t)world;
        const LONG f0 = k.rpLockKeyForce, nl0 = k.rpLockKeyNoLot, w0 = g_spWrites;
        RpLockKeyEnter(slot);
        assert(k.rpLockKeyForce == f0 + 1 && g_rpNForce == 1 && g_rpForce[0] == 0x77 && g_spWrites == w0 + 1);
        *(u8**)(lp.b + 0x20) = set;  // the game's 0x807110 then stores it: the same set again is no change
        RpLockKeyEnter(slot);
        *(u8**)(lp.b + 0x20) = NULL, *(DWORD*)(lp.b + 0x34) = 0x2000000;  // not inserted: the next INS copies the new set
        RpLockKeyEnter(slot);
        assert(k.rpLockKeyForce == f0 + 1 && g_spWrites == w0 + 1);
        *(DWORD*)(lp.b + 0x34) |= 0x04000000, *(u8**)(fme + 0xC) = NULL;  // a Q-less FME: refreshed through the cut's Q-less pass
        RpLockKeyEnter(slot);
        assert(k.rpLockKeyNoLot == nl0 + 1 && g_rpNNoQ == 2 && g_rpNForce == 1);
        g_rpOn = 0;  // replay off: nothing
        RpLockKeyEnter(slot);
        assert(g_spWrites == w0 + 2);
        g_rpOn = 1;
        g_rpNForce = 0, g_rpNNoQ = 0;
    }
    // --- B. insert answer check. The record asks live (parent + its child, level 0); the clone map then holds the insert's
    // two FMEs (parent key 0x99 and ~0x99) on Q q; the copies answer (1) equal, (2) a child +0x10 off by 2.0 (log only at
    // InsCheck 1), (3) the same at InsCheck 2 (the lot forced once)
    {
        u8 q[0x20] = {}, fP[0x14] = {}, fK[0x14] = {}, nP[0x14] = {}, nK[0x14] = {}, m[0x10] = {};
        DWORD bk[2] = {};
        RpClone c = {};
        c.m = m;
        *(DWORD**)(m + 4) = bk, *(DWORD*)(m + 8) = 2;
        const LONG d0 = k.rpInsDiff, fo0 = k.rpInsForced, chk = g_rpInsCheck;
        for (int pass = 0; pass < 3; pass++) {
            mk(lp, lk, 0.f, NULL, NULL);
            mk(cp, ck, pass ? 2.f : 0.f, lp.b, lk.b);
            RpInsLive* lv = RpInsLiveTake(lp.b);
            assert(lv && lv->n == 2);
            bk[0] = bk[1] = 0;
            DWORD pre[4];
            const int nPre = RpInsFmes(&c, q, cp.b, pre, 4);
            assert(nPre == 0);
            *(u8**)(fP + 8) = cp.b, *(u8**)(fP + 0xC) = q, *(DWORD*)(fP + 0x10) = 0x200;  // the replayed insert's FMEs
            *(u8**)(fK + 8) = ck.b, *(u8**)(fK + 0xC) = q, *(DWORD*)(fK + 0x10) = 0x200;
            *(DWORD*)nP = 0x99, *(DWORD*)(nP + 4) = 0, *(u8**)(nP + 8) = fP;
            *(DWORD*)nK = ~0x99u, *(DWORD*)(nK + 4) = ~0u, *(u8**)(nK + 8) = fK;
            bk[0x99 % 2] = (DWORD)(uintptr_t)nP, bk[~0x99u % 2] = (DWORD)(uintptr_t)nK;
            g_rpInsCheck = pass == 2 ? 2 : 1;
            RpInsCheck(&c, 0x55, q, cp.b, pre, nPre, lv);
            RpFree(lv);
            if (pass == 0) assert(k.rpInsDiff == d0 && k.rpInsForced == fo0);
            if (pass == 1) assert(k.rpInsDiff == d0 + 1 && k.rpInsForced == fo0 && g_rpNForce == 0);
            if (pass == 2) assert(k.rpInsDiff == d0 + 2 && k.rpInsForced == fo0 + 1 && g_rpNForce == 1 && g_rpForce[0] == 0x55);
        }
        g_rpInsCheck = chk;
        g_rpNForce = 0;
    }
    g_rpOn = savedOn, g_rpHave = savedHave;
    g_rtSimTidSlot = savedSim;
    g_rpCls = savedCls, g_rpNCls = savedN, g_rpLevelBox = savedBox;
    puts("P5 covers (0.36.35, docs/157 §4): a lock-key set swapped on an inserted footprint forces its lot and counts as a routing "
         "write (same set, not inserted, replay off: nothing; Q-less FME: Q-less refresh); a replayed insert whose child copy "
         "answers another rect than live at the record is logged (InsCheck 1) or forces its lot once (InsCheck 2)");
}

// 0.35.21 (docs/94 §22.24.4): thread CPU cycles -> cycle-ms/s at the nominal 2.3 GHz; the read itself is cheap enough per
// section, and a busy section counts cycles at a plausible rate (a preempted thread counts fewer, never more)
static void TestCycles() {
    assert(fabs(CycMsPerS(2300000, 1.0) - 1.0) < 1e-9);                 // 2.3 M cycles in 1 s = 1 cyc-ms/s
    assert(fabs(CycMsPerS((LONG64)(2.3e9 * 0.005 * 300), 300.0) - 5.0) < 1e-6);  // the stage-1 budget over a 5-min window
    assert(CycMsPerS(12345, 0.0) == 0.0 && CycMsPerS(0, 10.0) == 0.0);
    LARGE_INTEGER f, t0, t1;
    QueryPerformanceFrequency(&f);
    QueryPerformanceCounter(&t0);
    const LONG64 c0 = CycNow();
    for (int i = 0; i < 100000; i++) (void)CycNow();
    const LONG64 c1 = CycNow();
    QueryPerformanceCounter(&t1);
    const double readNs = (t1.QuadPart - t0.QuadPart) * 1e9 / f.QuadPart / 100000;
    volatile DWORD x = 1;
    QueryPerformanceCounter(&t0);
    const LONG64 b0 = CycNow();
    do { for (int i = 0; i < 100000; i++) x = x * 1664525u + 1013904223u; QueryPerformanceCounter(&t1); } while ((t1.QuadPart - t0.QuadPart) * 1000 < f.QuadPart * 20);
    const LONG64 b1 = CycNow();
    const double secs = (t1.QuadPart - t0.QuadPart) / (double)f.QuadPart, hz = (b1 - b0) / secs;
    assert(c1 > c0 && b1 > b0 && hz > 1e8 && hz < 1e10);
    printf("thread cycles: read %.0f ns each, a 20 ms busy loop counted %.2f GHz (cycle-ms/s use 2.3 GHz nominal: %.1f vs %.1f wall ms)\n",
           readNs, hz / 1e9, CycMsPerS(b1 - b0, 1.0), secs * 1000);
}

// [LauncherFix] (launcherfix.cpp): the TSLHost site as a tiny function; the patched bytes return 0x20 with a balanced stack
static void TestLauncherFix() {
    u8* m = (u8*)VirtualAlloc(NULL, 0x4000, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
    u8* site = m + 0x2000;
    memcpy(site, kLfOrig, sizeof(kLfOrig));
    site[8] = 0xC3;  // ret
    const u8 callee[] = {0xB8, 0x28, 0x00, 0x00, 0x00, 0xC2, 0x04, 0x00};  // mov eax,28h ; ret 4 (the upload-failed result)
    memcpy(site + 8 + (int)0xFFFFE7B3, callee, sizeof(callee));  // where the original rel32 points
    assert(((int(__cdecl*)())site)() == 0x28);
    site[1] = 0x8C;
    assert(LfPatchSite(site) == -1 && site[0] == 0x50);  // other bytes: nothing written
    site[1] = 0x8B;
    assert(LfPatchSite(site) == 1 && !memcmp(site, kLfNew, sizeof(kLfNew)));
    assert(((int(__cdecl*)())site)() == 0x20);
    assert(LfPatchSite(site) == 0);
    const LfUnicode yes = {22, 24, L"tslhost.DLL"}, no = {24, 26, L"TSLHost.dllx"}, other = {22, 24, L"TSLHost.exe"};
    assert(LfIsTsl(&yes) && !LfIsTsl(&no) && !LfIsTsl(&other) && !LfIsTsl(NULL));
    VirtualFree(m, 0, MEM_RELEASE);
}

int main() {
    setvbuf(stdout, NULL, _IONBF, 0);  // a crash keeps what was printed
    SetUnhandledExceptionFilter([](EXCEPTION_POINTERS* e) -> LONG {
        printf("selftest: unhandled exception %08lX at %p\n", e->ExceptionRecord->ExceptionCode, e->ExceptionRecord->ExceptionAddress);
        return EXCEPTION_EXECUTE_HANDLER;
    });
    static DWORD noSimTid;  // the game global 0x11D8A8C does not exist here
    g_rtSimTidSlot = &noSimTid;
    // PatchImm32: all sites match -> written; one differs -> nothing written.
    static u8 buf[16] = {0x3D, 0x00, 0x08, 0x00, 0x00, 0x76, 0x2C, 0xB8, 0x00, 0x08, 0x00, 0x00, 0x50};
    Site ok[] = {{(DWORD)(uintptr_t)buf, {0x3D, 0x00, 0x08, 0x00, 0x00, 0x76, 0x2C}, 7, 1},
                 {(DWORD)(uintptr_t)(buf + 7), {0xB8, 0x00, 0x08, 0x00, 0x00, 0x50}, 6, 1}};
    Site bad[] = {ok[0], {(DWORD)(uintptr_t)(buf + 7), {0xB8, 0x00, 0x09, 0x00, 0x00, 0x50}, 6, 1}};
    assert(!PatchImm32("bad", bad, 2, 0x2000));
    assert(buf[2] == 0x08 && buf[9] == 0x08);
    assert(PatchImm32("ok", ok, 2, 0x2000));
    assert(buf[2] == 0x20 && buf[9] == 0x20 && buf[5] == 0x76);

    // InstallDetour on a tiny function: mov eax,42 ; ret
    u8* fn = (u8*)VirtualAlloc(NULL, 64, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
    const u8 code[] = {0xB8, 0x2A, 0x00, 0x00, 0x00, 0xC3};
    memcpy(fn, code, sizeof(code));
    assert(!InstallDetour("bad", (DWORD)(uintptr_t)fn, (const u8*)"\xB8\x2B\x00\x00\x00", 5, (void*)Hook));
    void* tramp = InstallDetour("ok", (DWORD)(uintptr_t)fn, code, 5, (void*)Hook, &tramp);
    assert(tramp);
    assert(((int(__cdecl*)())fn)() == 7);
    assert(((int(__cdecl*)())tramp)() == 42);
    TestDetourPublish();
    TestDetourCallRel();
    TestTrampAlloc();
    TestRouteCache();
    TestRouteWorker();
    TestRouteBudget();
    TestRouteAsync();
    TestSaveWrite();
    TestSaveFlush();
    TestDecompress();
    TestAlloc();
    TestWaterJps();
    TestSaveCompress();
    TestSaveSlim();
    TestHeightCache();
    TestTerrainGrave();
    TestIcLock();
    TestRouteGate();
    TestMonoThread();
    TestSimLoop();
    TestSimPump();
    TestMonoThreadM5();
    TestArenaGuard();  // docs/67 §22.2
    TestGcRoots();  // docs/67 §22.4
    TestArenaTail();  // docs/67 §22.7
    TestLotStream();
    TestVerify();
    TestRouteSites();
    TestRouteEnforce();
    TestG11CloseBy();
    TestSeedWaitKeys();
    TestDiscourage();
    TestDiscourageDX();
    TestFootprintDefer();
    TestFootprintDefer2();
    TestFp2LostRemoval();
    TestPrivateFpDefer();
    TestPlanDrain();
    TestDestroyPark();
    TestXformGuard();
    TestSteerRO();
    TestSteerTrip();
    TestLotFd();
    TestObstructedRO();
    TestAvoidRO();
    TestSlotRo();
    TestP10();
    TestProbeSnap();
    TestCallSites();
    TestProbeValues();
    TestLoadProf();
    TestPlanRepeat();
    TestRouteSnap();
    TestSnapImages();
    TestSnapCost();
    TestBnmShare();
    TestRouteSnapP1b();
    TestSnapReplay();
    TestReplaySigMix();
    TestReplayQuiet();
    TestReplay03523();
    TestInsFreeInPlace();
    TestRecsUnlocked();
    TestRemPatch();
    TestP5Covers();
    TestCycles();
    TestReplayLongRun();
    TestSoftPendPrune();
    TestPools();
    TestJobQueue();
    TestLotSinCos();
    TestIdCache();
    g_p4ObsOn = false;  // TestP4's fake worlds have no planner at +0x95C and its +0x918 is not the fake lock; TestP4Obst covers it
    TestP4();
    TestP4DeferStop();
    TestP4Obst();
    TestP4DoorDump();
    TestP4LotDump();
    TestP5();
    TestP5b();
    TestP5PreLock();
    TestHitchAttr();
    TestLockFastPath();
    TestUnseen();
    TestUnseenGhost();
    TestUnseenAnim();
    TestUnseenRootMotion();
    TestUnseenRootMotion2();
    TestUnseenTable();
    TestUnseenCost();
    TestUfCam();
    TestUnseenFallbackSites();
    TestUfColour();
    TestUnseenThrottle();
    TestUnseenThrottle2();
    TestUnseenDiag();
    TestSatbLog();
    TestMintCall();
    TestMintFuse();
    TestAutShadow();
    TestThreadStack();
    TestCrashReport();
    TestUserDiag();
    TestPlayed();
    TestMemDiag();
    TestMemExp();
    TestGcGrowLog();
    TestAllocSites();
    TestWatchpoint();
    TestCrashForensics();
    TestCrashForensicsDraw();  // docs/186 §8
    TestCrashForensicsFx();    // docs/186 §9
    TestCrashForensicsObjRes();  // docs/186 §10
    TestApex();
    TestCompaction();
    TestRcTrim();
    TestLotPoolTrim();
    TestCasTrim();
    TestCasTrimSweep();
    TestCasTrimInv();
    TestCasTrimCells();
    TestCasTrimMb();  // docs/539 §14
    TestCasTrimOs();  // docs/539 §15
    TestCasTrimTrap();  // docs/539 §16
    TestCasTrimRealm();  // docs/539 §16.6
    TestUafCheck();  // docs/539 §17
    TestSlotGuard();  // docs/539 §18
    TestFloat5Free();  // docs/539 §19
    TestFloat5Watch();  // docs/539 §21
    TestMonoSigGuard();  // docs/539 §20
    TestS3ssCompat();  // docs/539 §22
    TestEaCrashFixes();
    TestPkgLock();
    TestWorldAStarRefs();
    TestIlPatch();
    TestMonoJit();
    TestEarlyOut();
    TestRouteJobPhases();
    TestGoalGen();
    TestGoalSplit();
    TestGoalStepSplit();  // docs/178 T3 P-GS + T1 phase line
    TestSyncPhases();
    TestForegroundRender();
    TestHostCalls();
    TestAskLent();
    TestTailUnlock();
    TestCellMemoProbe();
    TestReplanDedupe();
    TestPlanDeadline();
    TestGate0Ro();
    TestRouteToObjRO();
    TestPlacementRO();
    TestGoalValidRO();
    TestFootstepRO();
    TestRouteToObjTail();
    TestIdleCheckRO();
    TestIdleCheckC();
    TestMovedWaits();
    TestP6Waits();
    TestLauncherFix();
    TestThumbLod();
    puts("selftest ok");
    return 0;
}
