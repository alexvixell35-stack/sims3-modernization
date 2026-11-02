// [UnseenFallback] Sites=1 (docs/202 §3.4, phases C1-C4, counters v139 ufc*): an unseen Sim that would wait for the route
// job's +0x918 at one of four sim-thread sites takes the lock with a short deadline (SiteWaitMs, default 0 = one try) through the
// game's own Lock_Acquire 0x4E16F0 (fastcall(lock; const DWORD* deadline), absolute GetTickCount deadline, -2 = timed out
// [verified, docs/18 §246, selftest TestLockAcquire's finite-deadline case]). Taken: the whole locked block runs as the game's
// under that hold (its own acquires inside are recursive) and the hold is released after it. Timed out: the whole block is
// skipped with its harmless answer, so nothing of it runs and nothing is written. Included by ts3native.cpp after
// unseenfb.cpp (uses unseen.cpp's agent context and classifier, rtoro.cpp's three stubs, fgro.cpp's stub).
//
// Owner (who counts as unseen) = docs/202 §2, decided by ts3fixes (UnseenFallback.cs UfWhy, switch unseenfbsites): it pushes
// the ids of the unseen Sims every few sim ticks over the 0x83 channel (codes 7 BEGIN, 8 id low, 9 id high = append, 10 END
// with the count, 14 C4 log query) about every 0.5 s. Out of live mode it pushes an empty list; a list older than 3 s counts
// as none (nothing acts); in Build/Buy (scripts paused, no push) the routing world's build/buy byte drops the list (UfcFresh). Native
// checks on top, at the moment of the wait: the game's frustum and the reach rule (unseen.cpp class 1) on the Sim itself,
// plus, for C1 / C2, EA's on-screen byte agent+0xC8 off, a walking Sim (state 1), its own agent part, not a rider, no
// group link (the [UnseenThrottle] holds).
// Sites:
//   C1 steering obstacle list: the call at 0x9573B7 (Locomotion_UpdateAgent -> Locomotion_ObstacleList 0x85FFE0, cdecl, 5
//      args; takes +0x918 inside at 0x82EDB0 / 0x82F0B0, keys 0x85FE10 / 0x85F6CB). Skip = return at once: the caller's id
//      vector stays empty, the game's "no obstacles" path (docs/81 §4, the same skip [UnseenThrottle] makes 3 ticks in 4).
//      Chained behind UtListThunk when the throttle owns the site.
//   C2 avoidance: the call at 0x847697 (ObstacleAvoidance 0x847390 -> RouteFollow_A 0x846B70, cdecl, 5 args, u64 id; takes
//      +0x918 inside at 0x846D1C, key 0x846D21). Skip = return 0: "nothing obstructs the goal", the notice +0x64 is not sent
//      this tick (docs/137 §2: a 0 answer is also the no-hit result of its Sim scan).
//   C3 route-to-object check 0x831530 (IsSlotUnobstructed / IsRouteToObjectUnobstructed, key 0x83156D): rtoro.cpp's three
//      call sites; R-acq takes with the deadline (g_ufcC3). Timed out = the route part answers code 4 "obstructed", id 0 (the
//      early code-4 answer, docs/140 §12.1), nothing is released; with no route part the physics checks run unlocked (as
//      [RouteToObjRO]'s no-route path).
//   C4 placement test: the call at 0x635013 (0x634F10 -> LotRouting_FootprintsNearPoint 0x631F10, gate 12 F-G, key
//      0x631F40; FindGoodLocation and its 4 other callers). Timed out = 1 "footprints here": 0x634F10 answers "not free",
//      the search tries its next spot or fails.
//   C3 / C4 owner = the running script task when it is a Sim's own task: MonoScriptHost +0xBD0 holds the running task's id
//   (docs/119 §11.2, autledger.cpp) and a Sim's own task id is its ObjectId (ts3fixes RouteBackoff.cs tests
//   Simulator.CurrentTask == sim.ObjectId for "own task") [verified]. Neither IC carries the Sim: 0x831530's second argument
//   is a stand-in the IC builds from the age/gender flags (0x74E075; script RouteManager.IsRouteToObjectUnobstructed has no
//   Sim parameter) [verified disassembly + decompiled ScriptCore]. Alarms, one-shot tasks and other objects' tasks are not on
//   the list: they never act. Rule 6 keeps carrying Sims off the list, so a held object being put down is never answered
//   "not free" by us (the game's own inventory / keep-in-hand branch runs only on its own failed search).
// FinishWalk=1 (docs/202 §3.4.2, below): C1 / C2 hold flagged walkers for ts3fixes unseenfbfinish instead of faking, and give
// every other unseen owner the game's own wait.
// One wait per busy job: after a timeout every later site ask of an unseen owner during that job is a try (deadline 0), and a
// C3 / C4 Sim that got a fake answer < 1.5 s ago gets a try too (ufcRecover), so the sim never waits SiteWaitMs per agent.

enum { UFC_C1, UFC_C2, UFC_C3, UFC_C4 };
static const DWORD kUfcKey[4] = {0x0085FE10, 0x00846D21, 0x0083156D, 0x00631F40};  // probe P0 keys of the waits they replace

// ---- the unseen-Sim list (sim thread only: GetOptionHook) ----
enum { UFC_MAX = 256 };
static const DWORD kUfcListAgeMs = 3000;  // docs/202 §5.6: ts3fixes pushes every ~0.5 s
struct UfcIds { DWORD lo[UFC_MAX], hi[UFC_MAX]; u8 fin[UFC_MAX]; int n; DWORD at; };  // fin: FinishWalk flag (code 15)
static UfcIds g_ufcList, g_ufcIn;
static bool g_ufcInOpen;
static DWORD g_ufcInLo;
static void UfwSweep();
static bool UfcBuildBuy();
static volatile LONG g_ufcBbPend;  // §3.4.4: poll thread sets, sim thread clears (drop) or a push after the exit clears

static u8 UfcListMsg(int code, DWORD lo) {
    Ts3nCounters& k = ts3native_counters;
    if (!k.ufcReady) return 0;  // no site installed: ts3fixes stops pushing for this load
    if (code == 7) { g_ufcInOpen = true, g_ufcIn.n = 0; return 1; }
    if (!g_ufcInOpen) { k.ufcPushBad++; return 0; }
    if (code == 8) { g_ufcInLo = lo; return 1; }
    if (code == 9) {
        if (g_ufcIn.n == UFC_MAX) { g_ufcInOpen = false, k.ufcPushBad++; return 0; }
        g_ufcIn.fin[g_ufcIn.n] = 0;
        g_ufcIn.lo[g_ufcIn.n] = g_ufcInLo, g_ufcIn.hi[g_ufcIn.n++] = lo;
        return 1;
    }
    g_ufcInOpen = false;  // 10 END
    if ((DWORD)g_ufcIn.n != lo) { k.ufcPushBad++; return 0; }
    memcpy(g_ufcList.lo, g_ufcIn.lo, g_ufcIn.n * 4), memcpy(g_ufcList.hi, g_ufcIn.hi, g_ufcIn.n * 4);
    memcpy(g_ufcList.fin, g_ufcIn.fin, g_ufcIn.n);
    g_ufcList.n = g_ufcIn.n;
    const DWORD now = GetTickCount();
    g_ufcList.at = now ? now : 1;  // never ahead of GetTickCount (an `| 1` could be: the age test then wraps)
    k.ufcList = g_ufcList.n;
    k.ufcPushes++;
    if (!UfcBuildBuy()) g_ufcBbPend = 0;  // a live-mode list after the exit: nothing left to drop (§3.4.4)
    UfwSweep();  // holds of Sims no longer listed as finishable end now (they walk on)
    return 1;
}

// docs/202 §3.4.3 (0.36.111): Build/Buy needs no script. In Build/Buy scripts pause, so ts3fixes cannot push the empty
// out-of-live list and the last live list would act for up to kUfcListAgeMs (sv-ufc-buildbuy 10-28: C sites tried and faked
// in Buy mode). The routing world's own build/buy byte world+0x97B (RoutingWorld_OnMessage 0x835300: set at 0x835818 on
// message 0x4E11976, cleared at 0x8357FB on 0x4E119BB; RoutingSystem_OnExitBuildBuy 0x835240 tests it first) [verified raw
// bytes, docs/100 §65] is read through the job's lock (world+0x918, g_rgLock). Seen set = the list is dropped, so after the
// exit nothing acts until ts3fixes pushes a fresh live-mode list. Live mode: the byte is 0, behaviour unchanged.
static LONG g_ufcBbDrops;  // log line only (v139 block is full); sim thread only
static bool UfcBuildBuy() {
    const u8* lock = (const u8*)g_async.lock;
    if (!lock) return false;
    __try { return lock[0x97B - 0x918] != 0; } __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}
// docs/202 §3.4.4 (0.36.117): the byte read above only runs when a C site asks, and Build/Buy pauses the sim, so no site asks
// there (sv-ufc-buildbuy 10-29: lists dropped 0). A poll thread (UfcBbThread) watches the byte every 10 ms: an entry sets
// g_ufcBbPend so the pre-entry list is dropped at the first ask after the exit too, and ufcBb[3] (v140 spare slots) count
// spans, tries while the byte stayed set (must stay 0), and tries in the 3 s after an exit (live mode: the window tails).
static bool UfcFresh() {
    if (UfcBuildBuy() || (g_ufcBbPend && InterlockedExchange(&g_ufcBbPend, 0))) {
        if (g_ufcList.at) g_ufcList.n = 0, g_ufcList.at = 0, ts3native_counters.ufcList = 0, g_ufcBbDrops++;
        return false;
    }
    return g_ufcList.at && GetTickCount() - g_ufcList.at <= kUfcListAgeMs;
}
// The Build/Buy span watch (§3.4.4), poll thread only (the selftest calls UfcBbPoll with its own clock)
static const DWORD kUfcBbTailMs = 3000;
static struct { bool in; LONG last; DWORD exitAt; DWORD enterAt; LONG triesAt; int lines; } g_ufcBbW;
static void UfcBbPoll(DWORD now) {
    Ts3nCounters& k = ts3native_counters;
    const bool b = UfcBuildBuy(), wasIn = g_ufcBbW.in;
    const LONG t = k.ufcTry[0] + k.ufcTry[1] + k.ufcTry[2] + k.ufcTry[3], d = t - g_ufcBbW.last;
    g_ufcBbW.last = t;
    if (b != wasIn) {
        g_ufcBbW.in = b;
        const bool log = g_ufcBbW.lines < 64;
        if (log) g_ufcBbW.lines++;
        if (b) {
            k.ufcBb[0]++, g_ufcBbPend = 1, g_ufcBbW.enterAt = now, g_ufcBbW.triesAt = t;
            if (log) Log("unseen fallback sites: build/buy entered (span %ld), list dropped at the next ask", k.ufcBb[0]);
        } else {
            g_ufcBbW.exitAt = now ? now : 1;
            if (log) Log("unseen fallback sites: build/buy left after %lu ms, C-site tries meanwhile %ld (the gate allows none)",
                         now - g_ufcBbW.enterAt, t - g_ufcBbW.triesAt);
        }
    }
    if (b && wasIn) k.ufcBb[1] += d;  // both polls saw the byte set (edge intervals are ambiguous: not counted here)
    else if (!b && g_ufcBbW.exitAt && now - g_ufcBbW.exitAt < kUfcBbTailMs) k.ufcBb[2] += d;
}
static DWORD WINAPI UfcBbThread(void*) {
    for (;;) UfcBbPoll(GetTickCount()), Sleep(10);
}
// index of the id in the current list, -1 = not listed
static int UfcFind(DWORD lo, DWORD hi) {
    for (int i = 0; i < g_ufcList.n; i++)
        if (g_ufcList.lo[i] == lo && g_ufcList.hi[i] == hi) return i;
    return -1;
}
static bool UfcListedBody(DWORD lo, DWORD hi) {
    if (!UfcFresh()) {
        ts3native_counters.ufcStale++;
        return false;
    }
    return UfcFind(lo, hi) >= 0;
}
static bool UfcListed(DWORD lo, DWORD hi) { return g_ucOn ? UcTime(UC_LISTED, [&] { return UfcListedBody(lo, hi); }) : UfcListedBody(lo, hi); }
static bool UfcObjId(const u8* obj, DWORD* lo, DWORD* hi) {
    __try {
        *lo = *(const DWORD*)(obj + 8), *hi = *(const DWORD*)(obj + 0xC);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}

// C3, C4: the running script task's id (MonoScriptHost +0xBD0), listed by ts3fixes, and its Sim now unseen by the camera
// (a Sim, not parented: riders, carried)
static bool UfcTaskSimUnseenBody(DWORD* lo, DWORD* hi) {
    const u8* sim = NULL;
    __try {
        const u8* host = *g_hostPtr ? (const u8*)(uintptr_t)*g_hostPtr : NULL;
        if (!host) return false;
        *lo = *(const DWORD*)(host + 0xBD0), *hi = *(const DWORD*)(host + 0xBD4);
        if (!(*lo | *hi) || !UfcListed(*lo, *hi)) return false;
        void* mgr = *g_unObjMgr;
        sim = mgr ? (const u8*)g_unFind(mgr, NULL, *lo, *hi) : NULL;
    } __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
    if (!sim) return false;
    UfCamFresh();
    int kind = 1;
    bool parented = true;
    const int c = UnObjClass((void*)sim, &kind, NULL, &parented);
    return c == 1 && kind == 0 && !parented;
}
static bool UfcTaskSimUnseen(DWORD* lo, DWORD* hi) {
    return g_ucOn ? UcTime(UC_TASKSIM, [&] { return UfcTaskSimUnseenBody(lo, hi); }) : UfcTaskSimUnseenBody(lo, hi);
}

// The updating agent's own part (C1, C2): the [UnseenThrottle] holds, then the list
static bool UfcAgentUnseenBody(const u8* part, DWORD* lo, DWORD* hi) {
    u8* agent = t_unAgent;
    if (!agent || t_unCls != 2 || part != agent + 8 || t_unKind != 0 || t_unRider) return false;
    const u8* owner = NULL;
    __try {
        if (agent[0xC8] || *(const DWORD*)(agent + 0x94) != 1 || UtGroupLinked(agent)) return false;
        owner = *(u8* const*)(agent + 4);
    } __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
    return owner && UfcObjId(owner, lo, hi) && UfcListed(*lo, *hi);
}
static bool UfcAgentUnseen(const u8* part, DWORD* lo, DWORD* hi) {
    return g_ucOn ? UcTime(UC_AGENT, [&] { return UfcAgentUnseenBody(part, lo, hi); }) : UfcAgentUnseenBody(part, lo, hi);
}

// A route job is busy and this sim-thread wait would be behind it: the job owner thread, not the +0x918 owner (recursive or
// lent hold), not serving a job's managed call, no gate frame open (as AvMayRun, without S2's broken flag)
static bool UfcBusy(DWORD tid) {
    const Ts3nCounters& k = ts3native_counters;
    if (k.ufcMode != 1 || tid != g_async.owner || !g_async.lock || !AsyncBusy()) return false;
    const RgThread& t = t_rg;
    return !g_inInvoke && k.rgOwnerTid != (LONG)tid && t.n == t.readers;
}

// ---- the timed take ----
static LockAcquireFn g_ufcAcq = (LockAcquireFn)(uintptr_t)0x004E16F0;  // through the address: LockAcquireHook (pump, keys)
static LockReleaseFn g_ufcRel = (LockReleaseFn)(uintptr_t)0x004E17B0;
static DWORD g_ufcSpentH, g_ufcSpentAt;  // the busy job (route handle) that already cost one wait; sim thread only
static struct { DWORD lo, hi, at, c4At; LONG c4n; } g_ufcRecent[16];  // C3 / C4: Sims answered with a fake (recovery window)
static const DWORD kUfcRecentMs = 1500;
// docs/202 §5.6 (3): v020-natural IP on-1 answered 19,660 C4 "not free" in ~2 min (19,657 recover = one Sim in a row). At most
// kUfcC4Cap fakes per Sim per second; past it that Sim's C4 calls get the game's own answer (its wait) until the second ends.
static const LONG kUfcC4Cap = 8;
static const DWORD kUfcC4WindowMs = 1000;
static DWORD g_ufcC4LogLo, g_ufcC4LogHi;  // last Sim C4 answered with a fake, for ts3fixes' log (code 14); 0 = none
static bool g_ufcC4LogSet;
static LONG g_ufcC4Capped;  // sim thread only

static bool UfcRecent(DWORD lo, DWORD hi, DWORD now) {
    for (auto& e : g_ufcRecent)
        if (e.at && e.lo == lo && e.hi == hi && now - e.at < kUfcRecentMs) return true;
    return false;
}
static int UfcSlot(DWORD lo, DWORD hi, DWORD now) {
    int pick = 0;
    for (int i = 0; i < 16; i++) {
        if (g_ufcRecent[i].at && g_ufcRecent[i].lo == lo && g_ufcRecent[i].hi == hi) return i;
        if (g_ufcRecent[i].at - now < g_ufcRecent[pick].at - now) pick = i;  // oldest stamp (wrap-safe)
    }
    g_ufcRecent[pick].lo = lo, g_ufcRecent[pick].hi = hi, g_ufcRecent[pick].at = now ? now : 1, g_ufcRecent[pick].c4n = 0, g_ufcRecent[pick].c4At = 0;
    return pick;
}
static void UfcNoteRecent(DWORD lo, DWORD hi, DWORD now) { g_ufcRecent[UfcSlot(lo, hi, now)].at = now ? now : 1; }
// C4: true = this Sim already got kUfcC4Cap fakes in the current second (give it the game's answer)
static bool UfcC4Capped(DWORD lo, DWORD hi, DWORD now) {
    for (auto& e : g_ufcRecent)
        if (e.at && e.lo == lo && e.hi == hi) return e.c4n >= kUfcC4Cap && now - e.c4At < kUfcC4WindowMs;
    return false;
}
static void UfcC4Note(DWORD lo, DWORD hi, DWORD now) {
    auto& e = g_ufcRecent[UfcSlot(lo, hi, now)];
    if (!e.c4At || now - e.c4At >= kUfcC4WindowMs) e.c4At = now ? now : 1, e.c4n = 0;
    e.c4n++;
    g_ufcC4LogLo = lo, g_ufcC4LogHi = hi, g_ufcC4LogSet = true;
}
// code 14: lo 0-63 = bit lo of the logged Sim id, 64 = one pending, 65 = clear
static u8 UfcC4LogMsg(DWORD lo) {
    if (lo == 64) return g_ufcC4LogSet ? 1 : 0;
    if (lo == 65) { g_ufcC4LogSet = false; return 1; }
    if (lo >= 64) return 0;
    const u64 id = (u64)g_ufcC4LogHi << 32 | g_ufcC4LogLo;
    return (u8)(id >> lo & 1);
}

// 1 = held (release with g_ufcRel), -2 = not held (the deadline passed or a try failed)
static int UfcTake(int site, void* lock, DWORD lo, DWORD hi) {
    Ts3nCounters& k = ts3native_counters;
    const DWORD now = GetTickCount();
    const bool fast = (g_ufcSpentH && g_ufcSpentH == g_async.handle && now - g_ufcSpentAt < 5000) ||
                      (site >= UFC_C3 && UfcRecent(lo, hi, now));
    const int ms = fast ? 0 : Clamp(k.ufcWaitMs, 0, 150);
    DWORD dl = ms ? now + (DWORD)ms : 0;
    if (ms && (dl == 0 || dl == INFINITE)) dl = 1;  // GetTickCount wrap: never "try" or "infinite" by accident
    InterlockedIncrement(&k.ufcTry[site]);
    const DWORD key0 = t_waitKey;
    t_waitKey = kUfcKey[site];  // the wait keeps its site's P0 key
    LARGE_INTEGER t0;
    QueryPerformanceCounter(&t0);
    const LONG r = g_ufcAcq(lock, NULL, &dl);
    const LONG us = UsSince(t0);
    t_waitKey = key0;
    InterlockedExchangeAdd(&k.ufcWaitUs[site], us);
    Max(&k.ufcMaxWaitUs, us);
    if (r > 0) { InterlockedIncrement(&k.ufcGot[site]); return 1; }
    InterlockedIncrement(&k.ufcTimeout[site]);
    if (fast) InterlockedIncrement(&k.ufcRecover[site]);
    else g_ufcSpentH = g_async.handle, g_ufcSpentAt = now;
    if (site >= UFC_C3) UfcNoteRecent(lo, hi, now);
    return -2;
}

// ---- docs/202 §3.1.1: the pre-search +0x918 wait of a marked unseen sync plan (phase A, not a Sites switch) ----
// Site: Route_PlanLocked 0x82C6E0 takes Lock_Acquire(world+0x918, &INFINITE [0x1021CF0]) before its search, and our detour
// (RunPlan) waits for a running job before that [verified, docs/11 addendum 2026-10-12 + RunPlan]. A marked session (t_ufSess)
// instead takes the lock first through 0x4E16F0's deadline form with the session's own deadline (= the effective cut point:
// the search floor is the learned overrun budget): taken = the plan runs as the game's under that hold (its acquire inside is
// recursive, no job can start meanwhile); timed out = RunPlan returns 0 before the lock, the planner's own "Failed, no
// blocker" path (docs/11 Q5 [verified], the negative cache's answer): nothing of the search ran, nothing is written.
// GetTickCount deadline: up to ~16 ms late, inside the 30 ms overrun floor.
static int UfLockGate(void* world) {
    Ts3nCounters& k = ts3native_counters;
    const DWORD me = GetCurrentThreadId();
    if (!t_ufSess || !t_pd.open || k.ufMode != 1 || k.workerMode == 1 || g_inInvoke || me != g_async.owner || !world) return 0;
    void* const lock = (u8*)world + 0x918;
    if (lock != g_async.lock || !AsyncBusy()) return 0;
    const LONGLONG rem = t_pd.dl - PdNow();
    const LONG ms = rem > 0 ? (LONG)(rem * 1000 / g_qpf.QuadPart) : 0;
    DWORD dl = ms > 0 ? GetTickCount() + (DWORD)ms : 0;
    if (ms > 0 && (dl == 0 || dl == INFINITE)) dl = 1;  // GetTickCount wrap: never "try" or "infinite" by accident
    InterlockedIncrement(&k.ufLockWaits);
    const DWORD key0 = t_waitKey;
    t_waitKey = 0x0082C6E0;  // the wait keeps the planner's acquire as its key
    const LONG r = g_ufcAcq(lock, NULL, &dl);
    t_waitKey = key0;
    if (r > 0) return 1;
    InterlockedIncrement(&k.ufLockFail);
    t_ufLockFail = true;
    return -2;
}
static void UfLockRelease(void* world) { g_ufcRel((u8*)world + 0x918, NULL); }

// ---- docs/202 §3.4.2 [UnseenFallback] FinishWalk=1 (Rodney 2026-10-27): "finish the walk" instead of C1 / C2's fake answers ----
// C1 / C2 faked "no obstacles" / "nothing to avoid" every tick the lock was busy; unseen Sims then walked into things and
// replanned (200-300 ms hitches). With FinishWalk, a Sim ts3fixes flagged finishable in the pushed list (code 15: unseen, on
// foot, a single open-ground subpath left, its end unseen and on no lot with a household Sim, ...) is HELD at the first busy
// site: this tick the skipped block's answer (the empty list / 0, as one [UnseenThrottle] skipped tick), from the next pass on
// its whole agent update is skipped (unseen.cpp g_unHoldSkip at 0x83CF52: no step, no route event, no +0x918 taker). ts3fixes
// (unseenfbfinish) claims the hold (codes 16 + 17), waits the remaining walk time, then ends the route as the game's own arrival
// does (FollowRouteAction.Kill(Success, kStop_Hard)), releases the hold (16 + 18) and places the Sim at its path end in the
// Sim's own task before DoRoute resumes. Any Sim not flagged gets the game's own wait (no fake). A hold ends without ts3fixes
// (the Sim walks on from where it stood, its path and route action untouched) when: the native class turns visible, the Sim
// drops out of a pushed list or its flag, the list goes stale (3 s), unclaimed 3 s, held 10 min, or the mode is off.
// Sim thread only (C1 / C2 run in Locomotion_UpdateAgent, the messages in GetOptionHook from script calls).
enum { UFW_MAX = 32 };
enum { UFW_R_FINISH, UFW_R_GIVEUP, UFW_R_SEEN, UFW_R_EXPIRED };
static const DWORD kUfwClaimMs = 3000, kUfwMaxMs = 600000;
struct UfwHold { u8* agent; DWORD lo, hi, at; bool claimed; };  // agent is only compared, never read after the hold starts
static UfwHold g_ufw[UFW_MAX];
static int g_ufwN;      // slots in use
static bool g_ufwOn;    // installed
static DWORD g_ufwQLo;  // id low staged by code 16

static void UfwRelease(int i, int why) {
    g_ufw[i].agent = NULL;
    g_ufwN--;
    InterlockedIncrement(&ts3native_counters.ufwRelease[why]);
}
static int UfwById(DWORD lo, DWORD hi) {
    for (int i = 0; i < UFW_MAX; i++)
        if (g_ufw[i].agent && g_ufw[i].lo == lo && g_ufw[i].hi == hi) return i;
    return -1;
}
// -1 = keep, else the release reason (time and mode rules shared by the per-pass skip and the list sweep)
static int UfwExpired(const UfwHold& h, DWORD now) {
    if (ts3native_counters.ufwMode != 1 || ts3native_counters.ufcMode != 1 || !UfcFresh()) return UFW_R_EXPIRED;
    if ((!h.claimed && now - h.at > kUfwClaimMs) || now - h.at > kUfwMaxMs) return UFW_R_EXPIRED;
    return -1;
}
static void UfwSweep() {
    if (!g_ufwN) return;
    const DWORD now = GetTickCount();
    for (int i = 0; i < UFW_MAX; i++) {
        if (!g_ufw[i].agent) continue;
        const int why = UfwExpired(g_ufw[i], now);
        if (why >= 0) { UfwRelease(i, why); continue; }
        const int x = UfcFind(g_ufw[i].lo, g_ufw[i].hi);
        if (x < 0 || !g_ufcList.fin[x]) UfwRelease(i, UFW_R_SEEN);
    }
}
// unseen.cpp's agent-update thunk: true = held, skip this update
static bool UfwHoldSkip(u8* agent, int c) {
    if (!g_ufwN) return false;
    for (int i = 0; i < UFW_MAX; i++) {
        if (g_ufw[i].agent != agent) continue;
        const int why = c != 1 ? UFW_R_SEEN : UfwExpired(g_ufw[i], GetTickCount());
        if (why >= 0) { UfwRelease(i, why); return false; }  // walks on this pass, from where it stood
        InterlockedIncrement(&ts3native_counters.ufwHeldTicks);
        return true;
    }
    return false;
}
// C1 / C2 found +0x918 busy for an unseen owner (lo, hi) of the updating agent: 1 = held (give the skipped block's answer),
// 0 = the game's own wait, -1 = FinishWalk off (the C fake)
static int UfwOnBusy(DWORD lo, DWORD hi) {
    Ts3nCounters& k = ts3native_counters;
    if (!g_ufwOn || k.ufwMode != 1) return -1;
    u8* const agent = t_unAgent;
    const int x = UfcFresh() ? UfcFind(lo, hi) : -1;
    if (agent && x >= 0 && g_ufcList.fin[x]) {
        for (int i = 0; i < UFW_MAX; i++)
            if (g_ufw[i].agent == agent) return 1;  // already held (the other site this pass)
        for (int i = 0; i < UFW_MAX; i++) {
            if (g_ufw[i].agent) continue;
            const DWORD now = GetTickCount();
            g_ufw[i].agent = agent, g_ufw[i].lo = lo, g_ufw[i].hi = hi, g_ufw[i].at = now ? now : 1, g_ufw[i].claimed = false;
            g_ufwN++;
            InterlockedIncrement(&k.ufwHolds);
            Max(&k.ufwMaxHeld, (LONG)g_ufwN);
            return 1;
        }
        InterlockedIncrement(&k.ufwFull);
    }
    InterlockedIncrement(&k.ufwPlainWait);
    return 0;
}
// codes 15-20 on the 0x83 channel (ts3fixes UnseenFallbackFinish.cs). 0 = refused / no (FinishWalk not installed: ts3fixes
// stops flagging for this load).
//   15 flag the id just appended (code 9) finishable; 16 stage id low; 17 id high: held? (1 = yes, and the hold is claimed);
//   18 id high: release as finished; 19 id high: release as given up; 20 any unclaimed hold (1 = yes)
static u8 UfwMsg(int code, DWORD lo) {
    Ts3nCounters& k = ts3native_counters;
    if (!g_ufwOn) return 0;
    switch (code) {
    case 15:
        if (!g_ufcInOpen || !g_ufcIn.n) return 0;
        g_ufcIn.fin[g_ufcIn.n - 1] = 1;
        InterlockedIncrement(&k.ufwFlagged);
        return 1;
    case 16: g_ufwQLo = lo; return 1;
    case 17: case 18: case 19: {
        const int i = UfwById(g_ufwQLo, lo);
        if (i < 0) return 0;
        if (code == 17) {
            if (!g_ufw[i].claimed) g_ufw[i].claimed = true, InterlockedIncrement(&k.ufwClaims);
            return 1;
        }
        UfwRelease(i, code == 18 ? UFW_R_FINISH : UFW_R_GIVEUP);
        const int x = UfcFind(g_ufwQLo, lo);
        if (x >= 0) g_ufcList.fin[x] = 0;  // not held again before the next list (its stop pass may meet a busy lock)
        return 1;
    }
    case 20:
        for (int i = 0; i < UFW_MAX; i++)
            if (g_ufw[i].agent && !g_ufw[i].claimed) return 1;
        return 0;
    }
    return 0;
}

// ---- C1: steering obstacle list at 0x9573B7 ----
static UtListFn g_ufcListNext = (UtListFn)(uintptr_t)UT_LIST;
static void __cdecl UfcListHook(void* part, void* path, void* vec, DWORD a3, DWORD a4) {
    Ts3nCounters& k = ts3native_counters;
    if (!UfcBusy(GetCurrentThreadId())) { g_ufcListNext(part, path, vec, a3, a4); return; }
    DWORD lo, hi;
    if (!UfcAgentUnseen((const u8*)part, &lo, &hi)) {
        InterlockedIncrement(&k.ufcSeen[UFC_C1]);
        g_ufcListNext(part, path, vec, a3, a4);
        return;
    }
    void* const lock = g_async.lock;
    if (UfcTake(UFC_C1, lock, lo, hi) < 0) {
        const int w = UfwOnBusy(lo, hi);
        if (w == 0) { g_ufcListNext(part, path, vec, a3, a4); return; }  // FinishWalk, not flagged: the game's own wait
        if (w < 0) InterlockedIncrement(&k.ufcFake[UFC_C1]);
        return;  // "no obstacles" (the C fake, or this tick of a hold: the Sim stands from the next pass)
    }
    __try { g_ufcListNext(part, path, vec, a3, a4); } __finally { g_ufcRel(lock, NULL); }
}

// ---- C2: RouteFollow_A at 0x847697 ----
typedef u64(__cdecl* UfcAvAFn)(u8* path, u8* agent, const float* goal, const void* ign, DWORD n);
static const DWORD UFC_AV_SITE = 0x00847697, UFC_AV_A = 0x00846B70;
static UfcAvAFn g_ufcAvA = (UfcAvAFn)(uintptr_t)UFC_AV_A;  // through the address: [AvoidRO]'s A-entry detour if installed
static u64 UfcAvHeld(u8* path, u8* agent, const float* goal, const void* ign, DWORD n, void* lock) {
    __try { return g_ufcAvA(path, agent, goal, ign, n); } __finally { g_ufcRel(lock, NULL); }
}
static u64 __cdecl UfcAvHook(u8* path, u8* agent, const float* goal, const void* ign, DWORD n) {
    Ts3nCounters& k = ts3native_counters;
    if (!UfcBusy(GetCurrentThreadId())) return g_ufcAvA(path, agent, goal, ign, n);
    DWORD lo, hi;
    if (!UfcAgentUnseen(agent, &lo, &hi)) {
        InterlockedIncrement(&k.ufcSeen[UFC_C2]);
        return g_ufcAvA(path, agent, goal, ign, n);
    }
    void* const lock = g_async.lock;
    if (UfcTake(UFC_C2, lock, lo, hi) < 0) {
        const int w = UfwOnBusy(lo, hi);
        if (w == 0) return g_ufcAvA(path, agent, goal, ign, n);  // FinishWalk, not flagged: the game's own wait
        if (w < 0) InterlockedIncrement(&k.ufcFake[UFC_C2]);
        return 0;  // nothing to avoid (the C fake, or this tick of a hold)
    }
    return UfcAvHeld(path, agent, goal, ign, n, lock);
}

// ---- C3: rtoro.cpp's R-acq (and a given-up clone's late take) ----
static int UfcC3(void* lock) {
    Ts3nCounters& k = ts3native_counters;
    if (!(k.ufcReady & 4) || lock != g_async.lock || !UfcBusy(GetCurrentThreadId())) return 0;
    DWORD lo, hi;
    if (!UfcTaskSimUnseen(&lo, &hi)) { InterlockedIncrement(&k.ufcSeen[UFC_C3]); return 0; }
    return UfcTake(UFC_C3, lock, lo, hi);
}

// ---- C4: fgro.cpp's game call ----
static DWORD __fastcall UfcPlace(u8* p, void* edx, const u8* ctx, const float* pt, float r, void* set, DWORD mode) {
    Ts3nCounters& k = ts3native_counters;
    if (!UfcBusy(GetCurrentThreadId())) return g_fgOrig(p, edx, ctx, pt, r, set, mode);
    DWORD lo, hi;
    if (!UfcTaskSimUnseen(&lo, &hi)) {
        InterlockedIncrement(&k.ufcSeen[UFC_C4]);
        return g_fgOrig(p, edx, ctx, pt, r, set, mode);
    }
    void* const lock = g_async.lock;
    const DWORD now = GetTickCount();
    if (UfcC4Capped(lo, hi, now)) {  // the game's own answer: waits for the job as without the guard
        g_ufcC4Capped++;  // log line only: no counter slot (v139 block is full)
        return g_fgOrig(p, edx, ctx, pt, r, set, mode);
    }
    if (UfcTake(UFC_C4, lock, lo, hi) < 0) {  // "footprints here": not free
        InterlockedIncrement(&k.ufcFake[UFC_C4]);
        UfcC4Note(lo, hi, now);
        return 1;
    }
    __try { return g_fgOrig(p, edx, ctx, pt, r, set, mode); } __finally { g_ufcRel(lock, NULL); }
}

static void UfcLogCounters() {
    const Ts3nCounters& c = ts3native_counters;
    Log("unseen fallback sites: mode %ld wait %ld ms list %ld (pushes %ld bad %ld stale %ld); try/got/timeout/fake/recover/seen "
        "C1 %ld/%ld/%ld/%ld/%ld/%ld C2 %ld/%ld/%ld/%ld/%ld/%ld C3 %ld/%ld/%ld/%ld/%ld/%ld (no route %ld) C4 %ld/%ld/%ld/%ld/%ld/%ld; "
        "longest wait %ld ms; C4 capped %ld; lists dropped in build/buy %ld; build/buy spans %ld, tries inside %ld, "
        "tries in the 3 s after an exit %ld",
        c.ufcMode, c.ufcWaitMs, c.ufcList, c.ufcPushes, c.ufcPushBad, c.ufcStale, c.ufcTry[0], c.ufcGot[0], c.ufcTimeout[0],
        c.ufcFake[0], c.ufcRecover[0], c.ufcSeen[0], c.ufcTry[1], c.ufcGot[1], c.ufcTimeout[1], c.ufcFake[1], c.ufcRecover[1],
        c.ufcSeen[1], c.ufcTry[2], c.ufcGot[2], c.ufcTimeout[2], c.ufcFake[2], c.ufcRecover[2], c.ufcSeen[2], c.ufcNoRoute,
        c.ufcTry[3], c.ufcGot[3], c.ufcTimeout[3], c.ufcFake[3], c.ufcRecover[3], c.ufcSeen[3], c.ufcMaxWaitUs / 1000, g_ufcC4Capped, g_ufcBbDrops,
        c.ufcBb[0], c.ufcBb[1], c.ufcBb[2]);
    if (c.ufwReady)
        Log("unseen fallback finish walk: mode %ld flagged %ld holds %ld (held now %d, most %ld, full %ld) plain waits %ld held passes %ld "
            "claims %ld; ended finished %ld given up %ld seen %ld expired %ld",
            c.ufwMode, c.ufwFlagged, c.ufwHolds, g_ufwN, c.ufwMaxHeld, c.ufwFull, c.ufwPlainWait, c.ufwHeldTicks, c.ufwClaims,
            c.ufwRelease[UFW_R_FINISH], c.ufwRelease[UFW_R_GIVEUP], c.ufwRelease[UFW_R_SEEN], c.ufwRelease[UFW_R_EXPIRED]);
}

// [UnseenFallback] Sites=1 with C1..C4 (each default 1 once Sites=1), SiteWaitMs (0-150, default 0 since 0.36.104). Needs [RouteAsync] (the
// route jobs and their owner thread). ufcMode (+0x5390, --ab unseenfbc) starts at 1 when a site is installed.
static bool ApplyUnseenFallbackSites(const wchar_t* ini) {
    if (!GetPrivateProfileIntW(L"UnseenFallback", L"Sites", 0, ini)) return false;
    Ts3nCounters& k = ts3native_counters;
    // asyncReady only: g_async.owner is the sim thread id, set by the first queued job (JobQueue), so it is 0 at load
    // (0.36.90-0.36.102 also required it here and every C site skipped); UfcBusy / the C3 / C4 rules check it per call
    if (!k.asyncReady) {
        Log("SKIP unseen fallback sites: needs [RouteAsync] (route jobs and the GetOption2 channel)");
        return true;
    }
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
    k.ufcWaitMs = Clamp(GetPrivateProfileIntW(L"UnseenFallback", L"SiteWaitMs", 0, ini), 0, 150);
    const bool c1 = GetPrivateProfileIntW(L"UnseenFallback", L"C1", 1, ini) != 0, c2 = GetPrivateProfileIntW(L"UnseenFallback", L"C2", 1, ini) != 0;
    const bool c3 = GetPrivateProfileIntW(L"UnseenFallback", L"C3", 1, ini) != 0, c4 = GetPrivateProfileIntW(L"UnseenFallback", L"C4", 1, ini) != 0;
    // C1 / C2 need the agent update context (t_unAgent): the probe's / throttle's call site, else ours
    bool agent = false;
    if (c1 || c2) {
        const u8* s = (const u8*)(uintptr_t)UN_AGENT_CALL;
        const DWORD to = Readable(s, 5) && s[0] == 0xE8 ? UN_AGENT_CALL + 5 + *(const LONG*)(s + 1) : 0;
        const CallSite site = {UN_AGENT_CALL, UN_AGENT, (void*)UnAgentThunk};
        agent = to == (DWORD)(uintptr_t)UnAgentThunk || (to == UN_AGENT && PatchCalls("unseen fallback agent update", &site, 1));
        if (agent) k.ufcReady |= 32;
        else Log("SKIP unseen fallback C1 / C2: the agent update call 0x%08lX goes to 0x%08lX", UN_AGENT_CALL, to);
    }
    if (c1 && agent) {
        const u8* s = (const u8*)(uintptr_t)UT_LIST_CALL;
        const DWORD to = Readable(s, 5) && s[0] == 0xE8 ? UT_LIST_CALL + 5 + *(const LONG*)(s + 1) : 0;
        if (to == (DWORD)(uintptr_t)UtListThunk) {  // [UnseenThrottle] List=1 owns the site: run after its decision
            g_ufcListNext = g_utOrig;
            g_utOrig = UfcListHook;
            k.ufcReady |= 1;
        } else {
            const CallSite site = {UT_LIST_CALL, UT_LIST, (void*)UfcListHook};
            if (PatchCalls("unseen fallback C1 obstacle list", &site, 1)) k.ufcReady |= 1;
        }
    }
    if (c2 && agent) {
        const CallSite site = {UFC_AV_SITE, UFC_AV_A, (void*)UfcAvHook};
        if (PatchCalls("unseen fallback C2 RouteFollow_A", &site, 1)) k.ufcReady |= 2;
    }
    if (c3 && ((k.rtoReady & 7) == 7 || (RtoSitesOk("unseen fallback C3") && RtoPatchSites()))) g_ufcC3 = UfcC3, k.ufcReady |= 4;
    if (c4 && (k.fgReady || PatchCalls("unseen fallback C4 placement (0x634F10 -> FootprintsNearPoint)", &kFgSite, 1))) g_ufcFg = UfcPlace, k.ufcReady |= 8;
    if (k.ufcReady & 12) k.ufcReady |= 16;  // C3 / C4 read the running script task's id (MonoScriptHost +0xBD0)
    k.ufcMode = k.ufcReady & 15 ? 1 : 0;
    Log("%s unseen fallback sites: ready 0x%lX (bit 0 C1 obstacle list, 1 C2 avoidance, 2 C3 route-to-object check, 3 C4 placement, "
        "4 script task owner, 5 agent context), wait %ld ms, mode %ld; needs ts3fixes unseenfbsites (the unseen list)",
        (k.ufcReady & 31) == 31 ? "OK  " : k.ufcMode ? "PART" : "FAIL", k.ufcReady, k.ufcWaitMs, k.ufcMode);
    if (k.ufcMode) {  // §3.4.4 Build/Buy span watch
        if (HANDLE t = CreateThread(NULL, 64 * 1024, UfcBbThread, NULL, STACK_SIZE_PARAM_IS_A_RESERVATION, NULL)) CloseHandle(t);
        else Log("SKIP unseen fallback sites build/buy watch: CreateThread failed (%lu)", GetLastError());
    }
    // §3.4.2 FinishWalk: holds instead of the C1 / C2 fakes; the hold is the agent-update thunk's skip (needs bit 5)
    if (GetPrivateProfileIntW(L"UnseenFallback", L"FinishWalk", 0, ini)) {
        if ((k.ufcReady & 32) && (k.ufcReady & 3)) {
            g_unHoldSkip = UfwHoldSkip, g_ufwOn = true, k.ufwReady = 1, k.ufwMode = 1;
            Log("OK   unseen fallback finish walk: unseen flagged walkers are held and finished by ts3fixes unseenfbfinish; "
                "unflagged ones get the game's own wait (no C1 / C2 fake)");
        } else {
            Log("SKIP unseen fallback finish walk: needs C1 or C2 and the agent update context (ready 0x%lX)", k.ufcReady);
        }
    }
    return true;
}
