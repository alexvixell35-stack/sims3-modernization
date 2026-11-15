// [RouteJobPhases] SyncPlans=1 (docs/164, counters v97), observe only: routephases.cpp's phase timers on the sim thread's own
// sync plans, the ones a player sees as a frozen frame (docs/134 sync fallback plans, sim-thread Replan / PlanFromPoint with A10
// off, ThreadedObjects plans). Included by ts3native.cpp right after routephases.cpp.
//
// A session = one sync plan on the sim thread. It opens at the outermost of:
//   - SyncIc (the route IC's sync path: [RouteAsync] fallback / no job): the whole IC, goal step and result mapping included;
//   - a Route_PlanLocked entry on the sim thread with no session open (Replan and every other caller that never reaches SyncIc).
//     The Route_PlanLocked stub lets every thread into RjpEnter (g_rjpAny), so that entry is one pushad + call per plan.
// While it is open, g_rjpSyncTid = the sim's id and every stub times that thread too, exactly as the worker inside a job; frames
// land in this file's per-plan sums (SypFrame), not in the v87/v92 worker rows. The lock phase also gets the whole
// LockAcquireHook wait (LockAcquireAsync's pump runs the worker's managed calls between try-locks; the plain stub would see only
// the tries). One session at a time: another thread's plan during an open session is not timed (sypBusy).
// World A* calls are split by caller (SypLoopOf): the return address of the call, found by scanning up from the stub's return
// slot (an owner hook such as [RouteEarlyOut] or the steerro K8 counter calls it from C, so the game's return address sits in
// that hook's frame a few words up). All four direct callers are known [verified, E8 scan of the Steam exe]:
//   0x65F809 goal-lot entry loop, 0x65FB99 / 0x65FDA5 open-ground goals, 0x6762B4 the A*'s own circle pre-pass.
// Loop times are inclusive (allocator and nested pre-pass included); phase times are self times that sum to the plan.
// At the end: counters, and one log line per plan >= [RouteAsync] LogFallbackMs (20 per minute), right after SyncIc's
// "route async: sync plan N ms" line (and its script stack line) when the plan came through the IC.

enum { SYP_IC, SYP_PLANLOCKED };
enum { SYP_LOOP_ENTRY, SYP_LOOP_GROUND, SYP_LOOP_PREPASS, SYP_LOOP_OTHER, SYP_LOOPS };
static DWORD g_sypLoopRet[4] = {TS3A(0x0065F80E), TS3A(0x0065FB9E), TS3A(0x0065FDAA), TS3A(0x006762B9)};  // selftest sets its own
static const int kSypLoopOf[4] = {SYP_LOOP_ENTRY, SYP_LOOP_GROUND, SYP_LOOP_GROUND, SYP_LOOP_PREPASS};
static const int kSypScanWords = 256;  // shortcut: a stale copy of a known return address in a hook's frame would misfile one call
static const char* const kSypPhase[RJP_N] = {"plan", "cell", "post", "insert", "world", "lotleg", "portal", "cand", "lotgraph",
                                             "pending", "water", "alloc", "lock", "host", "other"};

struct SypPlan {
    bool open, planPopped;
    int entry, kind, why, lastOk;  // lastOk: result of the last Route_PlanLocked that returned, -1 none
    DWORD* openSlot;
    DWORD handle;
    const char* ent;
    LONGLONG t0;
    LONG planN, us[RJP_N], loopN[SYP_LOOPS], loopUs[SYP_LOOPS];
};
static __declspec(thread) SypPlan t_syp;
static __declspec(thread) bool t_sypIcIn;
static bool g_sypOn;
static DWORD g_sypMinute, g_sypLinesMin;

static DWORD g_sypLinesCap = 20;
static bool SypConfig(const wchar_t* ini) {
    g_sypLinesCap = Clamp(GetPrivateProfileIntW(L"RouteJobPhases", L"SyncLinesPerMin", 20, ini), 1, 1000);  // docs/247 §9: IP boats alone reach ~28 a minute
    return GetPrivateProfileIntW(L"RouteJobPhases", L"SyncPlans", 0, ini) != 0;
}

static void SypArm() {
    if (!g_rjpJmp[RJS_PLAN]) { Log("FAIL route phases SyncPlans: Route_PlanLocked site not timed, sync plans stay untimed"); return; }
    g_sypOn = true;
    ts3native_counters.sypMode = 1;
    InterlockedExchange(&g_rjpAny[RJS_PLAN], 1);
    Log("OK   route phases SyncPlans: sim-thread sync plans timed (lines >= %ld ms, LogFallbackMs)", ts3native_counters.rfbLogMs);
}

// opens a session on this thread; false when another thread's session is open
static bool SypBegin(int entry, DWORD* slot) {
    const DWORD tid = GetCurrentThreadId();
    if ((DWORD)InterlockedCompareExchange((volatile LONG*)&g_rjpSyncTid, (LONG)tid, 0) != 0) {
        InterlockedIncrement(&ts3native_counters.sypBusy);
        return false;
    }
    RjpThread& t = t_rjp;
    t.n = 0;
    t.kind = RJP_KSYNC;
    t.child = 0;
    t.allocs = 0;
    memset(t.rem, 0, sizeof(t.rem));
    memset(&t_syp, 0, sizeof(t_syp));
    t_syp.open = true;
    t_syp.entry = entry;
    t_syp.openSlot = slot;
    t_syp.lastOk = -1;
    t_syp.ent = "Route_PlanLocked (no IC)";
    InterlockedIncrement(&ts3native_counters.sypEntry[entry]);
    t_syp.t0 = RjpNow();
    return true;
}

static void Add(LONG* a, const LONG* v, int n) { for (int i = 0; i < n; i++) if (v[i]) InterlockedExchangeAdd(&a[i], v[i]); }

// "route phases: sync plan N ms failed, ..."; returns the length (selftest reads it)
static int SypLine(char* d, int room, const SypPlan& p, LONG ms, int res) {
    static const char* const kRes[3] = {"succeeded", "failed", "no-plan"};
    int n = sprintf_s(d, room, "route phases: sync plan %ld ms %s, entry %s, route %08lX, planlocked %ld; loops entry=%ld/%ld "
                               "ground=%ld/%ld prepass=%ld/%ld othercaller=%ld/%ld; ms",
                      ms, kRes[res], p.ent, p.handle, p.planN, p.loopN[0], p.loopUs[0] / 1000, p.loopN[1], p.loopUs[1] / 1000,
                      p.loopN[2], p.loopUs[2] / 1000, p.loopN[3], p.loopUs[3] / 1000);
    for (int i = 0; i < RJP_N && n > 0 && n < room - 24; i++)
        if (i != RJP_HOST) n += sprintf_s(d + n, room - n, " %s=%ld", kSypPhase[i], p.us[i] / 1000);
    if (p.us[RJP_WATER] && g_wjLastH && n > 0 && n < room - 24) n += sprintf_s(d + n, room - n, " grid=%lux%lu", g_wjLastW, g_wjLastH);  // needs [WaterJps]
    return n;
}

static void SypEnd() {
    Ts3nCounters& k = ts3native_counters;
    RjpThread& t = t_rjp;
    const LONGLONG now = RjpNow();
    while (t.n) { const RjpFrame f = t.f[--t.n]; RjpClose(t, f, now); }  // only frames that were unwound (none expected)
    const LONG us = RjpUs(now - t_syp.t0);
    LONG other = us - RjpUs(t.child);
    t_syp.us[RJP_OTHER] += other < 0 ? 0 : other;
    t.kind = 0;
    const SypPlan p = t_syp;
    t_syp.open = false;
    InterlockedExchange((volatile LONG*)&g_rjpSyncTid, 0);
    const int res = p.planN == 0 ? 2 : p.lastOk ? 0 : 1;
    InterlockedIncrement(&k.sypPlans[res]);
    InterlockedExchangeAdd(&k.sypUs[res], us);
    Max(&k.sypMaxUs, us);
    Add(k.sypPhaseUs, p.us, RJP_N);
    Add(k.sypLoopN, p.loopN, SYP_LOOPS);
    Add(k.sypLoopUs, p.loopUs, SYP_LOOPS);
    const LONG ms = us / 1000;
    if (!k.rfbLogMs || ms < k.rfbLogMs) return;
    InterlockedIncrement(&k.sypLongN);
    Add(k.sypLongPhaseUs, p.us, RJP_N);
    Add(k.sypLongLoopN, p.loopN, SYP_LOOPS);
    Add(k.sypLongLoopUs, p.loopUs, SYP_LOOPS);
    const DWORD tick = GetTickCount();
    if (tick - g_sypMinute >= 60000) { g_sypMinute = tick; g_sypLinesMin = 0; }
    if (g_sypLinesMin >= g_sypLinesCap) { InterlockedIncrement(&k.sypLinesDropped); return; }
    g_sypLinesMin++;
    char line[480];
    SypLine(line, sizeof(line), p, ms, res);
    Log("%s", line);
    InterlockedIncrement(&k.sypLines);
}

// RjpEnter at a g_rjpAny site (Route_PlanLocked): true = time this entry
static bool SypEnter(int site, DWORD* slot) {
    const DWORD tid = GetCurrentThreadId();
    if (tid == g_rjpTid) return true;  // the worker inside a job
    if (tid == g_rjpSyncTid) {
        // a Route_PlanLocked session whose opening frame is gone (unwound): the new entry is at or above its slot
        if (site == RJS_PLAN && t_syp.open && t_syp.entry == SYP_PLANLOCKED && slot >= t_syp.openSlot) {
            InterlockedIncrement(&ts3native_counters.sypStale);
            SypEnd();
            return SypBegin(SYP_PLANLOCKED, slot);
        }
        return true;
    }
    return site == RJS_PLAN && RgIsSim(tid) && SypBegin(SYP_PLANLOCKED, slot);
}

static void SypFrame(const RjpFrame& f, LONG self, LONGLONG incl) {
    SypPlan& p = t_syp;
    p.us[f.phase] += self;
    if (f.site == RJS_PLAN) p.planPopped = true;
    if (f.site == RJS_WORLD) {
        const int l = f.arg < (DWORD)SYP_LOOPS ? (int)f.arg : SYP_LOOP_OTHER;
        p.loopN[l]++;
        p.loopUs[l] += RjpUs(incl);
    }
}

static void SypAfterLeave(DWORD eax) {
    SypPlan& p = t_syp;
    if (!p.open || !p.planPopped) return;
    p.planPopped = false;
    p.planN++;
    p.lastOk = (u8)eax != 0;
    if (p.entry == SYP_PLANLOCKED && t_rjp.n == 0) SypEnd();
}

// ret = the stub's saved return address (the slot holds the leave thunk by now); then the words above it
static LONG SypLoopOf(DWORD ret, const DWORD* slot) {
    for (int i = 0; i < kSypScanWords; i++)
        for (int j = 0; j < 4; j++)
            if ((i ? slot[i] : ret) == g_sypLoopRet[j]) return kSypLoopOf[j];
    return SYP_LOOP_OTHER;
}

static bool SypIcWants() { return g_sypOn && !t_sypIcIn; }

static u8* SypIcRun(int kind, u8* out, DWORD handle, const float* pt, int why, const char* entry, const void* ret) {
    t_sypIcIn = true;
    DWORD here = 0;
    const bool mine = !t_syp.open && SypBegin(SYP_IC, &here);
    char ent[64];
    if (mine) {
        sprintf_s(ent, "IC %s/%s/%s", kArIcName[kind & 3], kRfbName[why], entry);
        t_syp.ent = ent;
        t_syp.handle = handle;
    }
    u8* r = SyncIc(kind, out, handle, pt, why, entry, ret);
    t_sypIcIn = false;
    if (mine) SypEnd();
    return r;
}

static bool SypLockBegin(DWORD* slot) { return g_rjpSyncTid && g_rjpSyncTid == GetCurrentThreadId() && RjpPush(RJP_LOCK, slot, false); }
static void SypLockEnd(DWORD* slot) {
    if (t_rjp.kind == RJP_KSYNC) RjpPop(slot);
}
