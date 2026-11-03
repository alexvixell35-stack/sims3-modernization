// [RouteAsync] PlanDeadlineMs / PlanDeadlineOwnMs (docs/164 §4 D1-D4) and FailMemo (D5), docs/164 §7, counters v99. Included by
// ts3native.cpp after earlyout.cpp and replandedupe.cpp (RdRun), before routephases.cpp (RjpOwner takes our trampolines).
//
// D1-D4: one wall deadline per sim-thread sync plan (a "session"), opened at the outermost of
//   - SyncIc, the route IC's sync path (the whole IC: goal step, every nested Route_PlanLocked, result mapping). Deadline by
//     reason: NoStart / Foreign (no own task: docs/141 classes b and c, ThreadedObjects, alarms, resets, other Sims' tasks) =
//     pdUs; any other reason (the route Sim's own task could not go async: NoYield, QueueFull, M1*, Unstarted, ...) = pdOwnUs,
//     0 by default = never cut (a route the Sim must take is not failed for time);
//   - CallPlanBudget on the sim thread with no session open (Replan / PlanFromPoint / ReplanFromPosition with A10 off, any other
//     non-IC sim plan) = pdUs. The goal step of such a plan runs before Route_PlanLocked, outside the session.
//   docs/164 §7.8 (0.36.78): an exempt sync IC (own task with pdOwnUs 0, or a stuck-check round whose own START carried a tag
//   but no probe mark = the last round, the only one that resets, as KindDeadlines' kdExemptSc) runs with t_pdNoCut set, so
//   its nested Route_PlanLocked opens no pdUs session either (before, those plans were cut at pdUs through CallPlanBudget).
// Past the deadline every search of the plan answers the way the game's own no-path exit does, so the planner's own failure
// handling runs and the plan ends as an ordinary failed plan (the script callers already handle that):
//   world A* 0x676260 (D1; D2 = every later candidate of the goal-lot entry loop 0x65F809 and open-ground loop 0x65FB99 /
//     0x65FDA5): the built FbJobCapMs path. The session sets t_fbDeadline; CallPlanBudget gives each Route_PlanLocked a Budget
//     with it; BudgetStep reads the clock on every 4th cost call (the first included) and from then on every edge costs the
//     wall, the open list drains through the A*'s "open list empty" exit 0x676BF7 (b.capped stays set for that Route_PlanLocked)
//   cell A* 0x61E500 (D3; lot legs D4 through it: LotPlan_Inner takes its own code-5 path, LotPlan 0x6410B0 its own retry per
//     goal room, each retry cut again): the A*'s empty-start-set exit (0x61E53C je 0x61E62F): [this+0x24] = 0, AL = 0,
//     ret 0x18, nothing else written [verified disassembly, Steam 1.67]
//   water 0x671930 (D4): its "not the same water body" exit (0x671948 ja 0x671B98): out vector (arg 9) end = begin, AL = 0,
//     ret 0x24 [verified disassembly]
// Not cut (no exact no-write exit, or not a search; docs/164 §7.2): portal A* 0x63E340 (writes its context fields before any
// exit), LotPlan 0x6410B0 itself (PathPlanner_PlanHierarchical hands its out indices to 0x650950 whatever the code), one cell
// A* already running (bounded by the game's 64,000-node cap), LotGraph_Build, pending lot updates, post pass / insert (success
// path only), the IC's goal step and script callbacks.
// A plan that finishes before its deadline runs exactly as before: the stubs pass (one thread compare), the cost stubs read
// the clock every 4th call. With a deadline, SyncCapMs's node clock is not used inside that plan (CallPlanBudget: dl wins).
//
// docs/178 T1 [RouteAsync] FbJobDeadline=1 (counters v103 fbjd*, runtime switch fbjdMode): the same two entry stubs also answer
// the route worker while it runs a foreign (FallbackAsync) job whose FbJobCapMs deadline is set (t_fbDeadline from the job's
// start, ServeAsync; g_pdWkTid = the worker's id for that job only). Before, only the world A* cost stubs read that deadline,
// so a foreign job that spent its time in cell A* (SV crowds: 2.08 s) was never cut. Past the deadline each further cell A* /
// water search takes the exit above; the job's IC continues through the game's own failure handling and its failed result
// is collected like any failed job (IcCollect copies j->result), as with the world A* cap. Own and A10 jobs: no deadline.
//
// docs/163 §10 (ts3fixes stuckcheckcap): a stuck-check probe job (own START right after a PROBE mark, ts3native.cpp
// ScProbeMark) takes the same worker slot with PlanDeadlineMs as its deadline (t_scProbe), whatever fbjdMode says.
//
// D5 FailMemo: replandedupe.cpp's RdRun with kind 0 on SyncIc's Plan call (CallIc, t_fmSync): a repeat of a FAILED plan within
// 500 ms with the same follower, goal, start within 10 cm, route state as that plan left it and no routing write since is
// answered with that plan's result bytes.

static int g_ufIni;  // docs/202 [UnseenFallback] Enabled as read (unseenfb.cpp, included after this file)
static void ReadUnseenFallback(const wchar_t* ini);
static void UfInstalled();
static void* g_pdOrig[2];          // trampolines: [0] cell A*, [1] water ([RouteJobPhases] puts its stubs here, RjpOwner)
static volatile DWORD g_pdTid;     // the sim's id while its session has a deadline (the entry stubs' thread compare), else 0
static int g_fmIni;                // [RouteAsync] FailMemo as read
static int g_fbjdIni;              // [RouteAsync] FbJobDeadline as read (docs/178 T1)
struct PdSess {
    bool open;
    int entry;                     // 0 IC, 1 Route_PlanLocked
    LONGLONG t0, dl, savedDl;
    LONG skip[2], capped;
    bool scLast;                   // a stuck-check last round's IC ran nested in this session (could not be exempted)
    char ent[48];
    DWORD handle;
};
static __declspec(thread) PdSess t_pd;
static DWORD g_pdMinute, g_pdLinesMin;

// docs/197 [RouteAsync] KindDeadlines=1 (counters v134 kd*, runtime switch kdMode): every worker job gets the deadline of its
// kind (ini per kind, ms from the job's start; 0 = that kind is never cut), through the same exits as FbJobDeadline: world A*
// cost stubs (CallPlanBudget reads t_fbDeadline), cell A* / water entry stubs on the worker slot (g_pdWkTid). A cut job's IC
// runs on through the game's own failure handling and its failed result is collected like any failed job: the script caller
// sees an ordinary failed plan (route-fail reaction, interaction retries or cancels, caller's own retry).
// Behaviour guards: (1) per-follower backoff: a follower (route +0x40/+0x44, read at START) whose job was cut within 10 s gets
// the next job's deadline doubled, after a second cut its next job runs uncut; a success resets it. So a route a Sim must take
// fails for time at most twice in a row, then plans as without the switch. (2) An own job with a stuck-check tag but no probe
// mark (the last round, the only one that resets) is never cut (kdExemptSc). (3) The probe kind (stuck-check rounds before
// the last) has no backoff: ts3fixes stuckcheckcap already plans the last round uncut.
// Not distinguished here (no field in the job): car / boat drivers, NRaas GoHere, crowds: they are own or foreign jobs by START.
// P5/P6 clone runs (p5.cpp) read only a foreign job's deadline: with [RouteSnap] Mode=2 an own clone job is not cut (off in release).
// shortcut: 64 followers, oldest cut evicted; a town with more Sims cut inside 10 s loses their backoff (they then stay at
// the base deadline); grow kKdRows if kdBackoff stays 0 while kdCut is high.
enum { KD_PLAN, KD_REPLAN, KD_PFP, KD_RFP, KD_A10F, KD_FGN, KD_PROBE, KD_N };
static const wchar_t* const kKdIniKey[KD_N] = {L"KdOwnPlanMs", L"KdOwnReplanMs", L"KdOwnPlanFromPointMs", L"KdOwnReplanFromPositionMs",
                                               L"KdA10ForeignMs", L"KdForeignPlanMs", L"KdProbeMs"};
static const int kKdDefMs[KD_N] = {250, 250, 250, 250, 200, 200, 180};  // docs/197 §3
static const char* const kKdName[KD_N] = {"own Plan", "own Replan", "own PlanFromPoint", "own ReplanFromPosition", "A10 foreign",
                                          "foreign Plan", "probe"};
static int g_kdIni;
static const DWORD kKdWinMs = 10000, kKdStuckMs = 30000;
static const int kKdRows = 64;
struct KdRow { u64 id; DWORD cutTick; LONG level, pending; };  // level = cuts in a row (1 doubled, 2 uncut); pending = a cut not yet followed
static KdRow g_kdTab[kKdRows];
static SRWLOCK g_kdLock = SRWLOCK_INIT;
static __declspec(thread) bool t_kdUncut;  // this job runs uncut because of its follower's backoff

static void ReadKindDeadlines(const wchar_t* ini) {
    Ts3nCounters& k = ts3native_counters;
    g_kdIni = Clamp(GetPrivateProfileIntW(L"RouteAsync", L"KindDeadlines", 0, ini), 0, 1);
    for (int i = 0; i < KD_N; i++) {
        const int ms = GetPrivateProfileIntW(L"RouteAsync", kKdIniKey[i], kKdDefMs[i], ini);
        k.kdUs[i] = ms > 0 ? Clamp(ms, 50, 60000) * 1000 : 0;
    }
    if (g_kdIni)
        Log("route async: KindDeadlines=1 own Plan %ld Replan %ld PlanFromPoint %ld ReplanFromPosition %ld, A10 foreign %ld, foreign "
            "Plan %ld, probe %ld ms", k.kdUs[0] / 1000, k.kdUs[1] / 1000, k.kdUs[2] / 1000, k.kdUs[3] / 1000, k.kdUs[4] / 1000,
            k.kdUs[5] / 1000, k.kdUs[6] / 1000);
}
static void KdInstalled() {  // after PdInstallAt: world A* through the cost stubs, cell A* and water through the entry stubs
    Ts3nCounters& k = ts3native_counters;
    k.kdReady = k.budgetReady && g_pdOrig[0] && g_pdOrig[1] ? 1 : 0;
    k.kdMode = k.kdReady;
    Log("%s kind deadlines: ready %ld (needs the cost stubs and both entry stubs), mode %ld", k.kdReady ? "OK  " : "FAIL", k.kdReady,
        k.kdMode);
}
static LONG ScProbeUs() {
    const Ts3nCounters& k = ts3native_counters;
    return k.kdMode == 1 && k.kdReady ? k.kdUs[KD_PROBE] : k.pdUs;
}
static int KdKind(const AsyncJob* j) { return j->probe ? KD_PROBE : j->fb ? KD_FGN : j->af ? KD_A10F : j->kind & 3; }
// sim thread (START, stuck-check mark): the route's follower id, 0 = no route or no follower
static u64 KdFollower(DWORD handle) {
    __try {
        const u8* rm = *g_orRmSlot;
        const u8* route = rm && handle ? g_orRoute(rm, NULL, handle) : NULL;
        return route ? *(const u64*)(route + 0x40) : 0;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return 0;
    }
}
// caller holds g_kdLock (shared for add = false); add = take a free row, else the one cut longest ago
static KdRow* KdFind(u64 id, bool add) {
    KdRow* victim = &g_kdTab[0];
    for (KdRow& r : g_kdTab) {
        if (r.id == id) return &r;
        if (victim->id && (!r.id || (LONG)(r.cutTick - victim->cutTick) < 0)) victim = &r;
    }
    if (!add) return NULL;
    memset(victim, 0, sizeof(*victim));
    victim->id = id;
    return victim;
}
// worker, before the job's IC: its deadline in us (0 = uncut), -1 = KindDeadlines off (the old rules apply)
static LONG KdJobBegin(const AsyncJob* j) {
    Ts3nCounters& k = ts3native_counters;
    t_kdKind = 0, t_kdUncut = false;
    if (k.kdMode != 1 || !k.kdReady) return -1;
    const int kind = KdKind(j);
    t_kdKind = kind + 1;
    if (kind != KD_PROBE && j->sc) {  // a stuck-check round without the probe mark: the last one, the only one that resets
        InterlockedIncrement(&k.kdExemptSc);
        return 0;
    }
    LONG us = k.kdUs[kind];
    if (!us) return 0;
    LONG level = 0;
    if (kind != KD_PROBE && j->kdId) {
        AcquireSRWLockShared(&g_kdLock);
        const KdRow* r = KdFind(j->kdId, false);
        if (r && GetTickCount() - r->cutTick < kKdWinMs) level = r->level;
        ReleaseSRWLockShared(&g_kdLock);
    }
    if (level >= 2) {
        InterlockedIncrement(&k.kdBackoff[1]);
        t_kdUncut = true;
        return 0;
    }
    if (level == 1) {
        InterlockedIncrement(&k.kdBackoff[0]);
        us *= 2;
    }
    InterlockedIncrement(&k.kdJobs[kind]);
    return us;
}
// worker, after the job's IC (j->result filled): counters, the follower's backoff row and the outcome of an earlier cut
static void KdJobEnd(const AsyncJob* j, bool capped, LONG us, LONGLONG end, LONGLONG dl) {
    Ts3nCounters& k = ts3native_counters;
    if (!t_kdKind) return;
    const int kind = t_kdKind - 1;
    const bool ok = *(const DWORD*)j->result == 1;  // RoutePlanResult type 1 = succeeded
    if (us >= 300000) InterlockedIncrement(&k.kdLong[kind]);
    if (capped) {
        InterlockedIncrement(&k.kdCut[kind]);
        if (ok) InterlockedIncrement(&k.kdCutOk);
        const LONGLONG late = dl && end > dl ? (end - dl) * 1000000 / g_qpf.QuadPart : 0;
        Max(&k.kdLateMaxUs, late > 0x7FFFFFFF ? 0x7FFFFFFF : (LONG)late);
    }
    if (kind == KD_PROBE || !j->kdId) return;
    const DWORD now = GetTickCount();
    AcquireSRWLockExclusive(&g_kdLock);
    KdRow* r = KdFind(j->kdId, capped);
    if (r) {
        const bool fresh = now - r->cutTick < kKdWinMs;
        if (r->pending && fresh) {
            InterlockedIncrement(ok ? &k.kdAfterOk : &k.kdAfterFail);
            if (ok && t_kdUncut) InterlockedIncrement(&k.kdUncutOk);
        }
        if (capped) {
            r->level = fresh && r->level ? (r->level >= 2 ? 2 : r->level + 1) : 1;
            r->cutTick = now;
            r->pending = 1;
        } else {
            r->pending = 0;
            if (ok) r->level = 0;  // a success ends the backoff
        }
    }
    ReleaseSRWLockExclusive(&g_kdLock);
}
// sim thread, every stuck-check mark (ScProbeMark): its follower had a job cut within 30 s
static void KdStuckMark(DWORD handle) {
    if (ts3native_counters.kdMode != 1) return;
    const u64 id = KdFollower(handle);
    if (!id) return;
    AcquireSRWLockShared(&g_kdLock);
    const KdRow* r = KdFind(id, false);
    const bool hit = r && r->level && GetTickCount() - r->cutTick < kKdStuckMs;
    ReleaseSRWLockShared(&g_kdLock);
    if (hit) InterlockedIncrement(&ts3native_counters.kdAfterStuckCheck);
}
static void KdLogCounters() {
    const Ts3nCounters& c = ts3native_counters;
    char b[512];
    int n = 0;
    for (int i = 0; i < KD_N; i++) {
        const int w = _snprintf_s(b + n, sizeof(b) - n, _TRUNCATE, "%s%s %ld/%ld/%ld", i ? ", " : "", kKdName[i], c.kdJobs[i],
                                  c.kdCut[i], c.kdLong[i]);
        if (w < 0) break;
        n += w;
    }
    Log("route kind deadlines: mode %ld; jobs/cut/>=300 ms by kind: %s; late max %ld ms, cut ok %ld; backoff doubled %ld uncut %ld; "
        "after a cut ok %ld fail %ld (uncut ok %ld), stuck checks %ld; stuck-check last round exempt %ld",
        c.kdMode, b, c.kdLateMaxUs / 1000, c.kdCutOk, c.kdBackoff[0], c.kdBackoff[1], c.kdAfterOk, c.kdAfterFail, c.kdUncutOk,
        c.kdAfterStuckCheck, c.kdExemptSc);
}

static void ReadPlanDeadline(const wchar_t* ini) {
    Ts3nCounters& k = ts3native_counters;
    const int ms = GetPrivateProfileIntW(L"RouteAsync", L"PlanDeadlineMs", 0, ini);
    const int own = GetPrivateProfileIntW(L"RouteAsync", L"PlanDeadlineOwnMs", 0, ini);
    k.pdUs = ms > 0 ? Clamp(ms, 50, 60000) * 1000 : 0;
    k.pdOwnUs = own > 0 ? Clamp(own, 50, 60000) * 1000 : 0;
    k.pdMode = k.pdUs || k.pdOwnUs ? 1 : 0;
    g_fmIni = Clamp(GetPrivateProfileIntW(L"RouteAsync", L"FailMemo", 0, ini), 0, 2);
    g_fbjdIni = Clamp(GetPrivateProfileIntW(L"RouteAsync", L"FbJobDeadline", 0, ini), 0, 1);  // docs/178 T1
    k.owReady = GetPrivateProfileIntW(L"RouteAsync", L"OwnJobWait", 0, ini) ? 1 : 0;          // docs/178 T4 (observe only)
    k.jyMode = GetPrivateProfileIntW(L"RouteAsync", L"JobYield", 0, ini) ? 1 : 0;             // docs/183 (runtime switch)
    k.rwMode = GetPrivateProfileIntW(L"RouteAsync", L"WorkerWait", 0, ini) ? 1 : 0;           // docs/203 §6 (runtime switch)
    ReadKindDeadlines(ini);                                                                    // docs/197
    ReadUnseenFallback(ini);                                                                   // docs/202 (unseenfb.cpp)
    Log("route async: PlanDeadlineMs=%ld PlanDeadlineOwnMs=%ld FailMemo=%d FbJobDeadline=%d OwnJobWait=%ld JobYield=%ld WorkerWait=%ld",
        k.pdUs / 1000, k.pdOwnUs / 1000, g_fmIni, g_fbjdIni, k.owReady, k.jyMode, k.rwMode);
}

static LONGLONG PdNow() { LARGE_INTEGER q; QueryPerformanceCounter(&q); return q.QuadPart; }

static void PdOpen(int entry, LONG us) {
    PdSess& s = t_pd;
    memset(&s, 0, sizeof(s));
    s.open = true;
    s.entry = entry;
    s.t0 = PdNow();
    s.dl = s.t0 + (LONGLONG)us * g_qpf.QuadPart / 1000000;
    s.savedDl = t_fbDeadline;
    t_fbDeadline = s.dl;  // read by CallPlanBudget for every Route_PlanLocked of the session (D1, D2)
    strcpy_s(s.ent, "Route_PlanLocked (no IC)");
    InterlockedExchange((volatile LONG*)&g_pdTid, (LONG)GetCurrentThreadId());
    InterlockedIncrement(&ts3native_counters.pdSessions[entry]);
}

// ok = the plan's own result: succeeded
static void PdClose(bool ok) {
    Ts3nCounters& k = ts3native_counters;
    PdSess& s = t_pd;
    const LONGLONG now = PdNow();
    t_fbDeadline = s.savedDl;
    InterlockedExchange((volatile LONG*)&g_pdTid, 0);
    s.open = false;
    if (s.scLast && (s.capped || s.skip[0] || s.skip[1])) InterlockedIncrement(&k.pdCutSc);
    t_pdHitLast = now >= s.dl;  // docs/202 (UfSyncEnd)
    if (now < s.dl) return;
    const LONGLONG lateUs = (now - s.dl) * 1000000 / g_qpf.QuadPart;  // a test sets dl = 1 ("already past")
    const LONG us = (LONG)((now - s.t0) * 1000000 / g_qpf.QuadPart), late = lateUs > 0x7FFFFFFF ? 0x7FFFFFFF : (LONG)lateUs;
    t_pdLastLateUs = late;  // docs/202 §3.1.1 (UfSyncEnd): the overrun and the exits that cut this session
    t_pdLastMask = (s.capped ? 1u : 0u) | (s.skip[0] ? 2u : 0u) | (s.skip[1] ? 4u : 0u);
    InterlockedIncrement(&k.pdHit);
    if (ok) InterlockedIncrement(&k.pdHitOk);
    InterlockedExchangeAdd(&k.pdHitUs, us);
    Max(&k.pdLateMaxUs, late);
    const DWORD tick = GetTickCount();
    if (tick - g_pdMinute >= 60000) g_pdMinute = tick, g_pdLinesMin = 0;
    if (g_pdLinesMin >= 20) return;
    g_pdLinesMin++;
    InterlockedIncrement(&k.pdLines);
    Log("route deadline: sync plan %ld ms %s, %ld ms past its deadline, entry %s, route %08lX; world A* capped %ld, cell skipped %ld, "
        "water skipped %ld", us / 1000, ok ? "succeeded" : "failed", late / 1000, s.ent, s.handle, s.capped, s.skip[0], s.skip[1]);
}

// > 0 the IC's deadline (us); 0 no session of its own (switch off, or nested: the open session's deadline applies);
// < 0 exempt (§7.8): no session, nor for its nested Route_PlanLocked (SyncIc sets t_pdNoCut)
static LONG PdIcUs(int why, bool scLast) {
    Ts3nCounters& k = ts3native_counters;
    if (k.pdMode != 1) return 0;
    if (t_pd.open) {
        if (scLast) t_pd.scLast = true;
        return 0;
    }
    if (scLast) {
        InterlockedIncrement(&k.pdExemptSc);
        return -1;
    }
    if (why == RFB_NOSTART || why == RFB_FOREIGN) return k.pdUs;
    if (k.pdOwnUs) return k.pdOwnUs;
    InterlockedIncrement(&k.pdOwnExempt);  // pdMode 1 with pdOwnUs 0: pdUs is set
    return -1;
}
static void PdIcBegin(LONG us, int kind, int why, const char* entry, DWORD handle) {
    PdOpen(0, us);
    sprintf_s(t_pd.ent, "IC %s/%s/%s", kArIcName[kind & 3], kRfbName[why], entry);
    t_pd.handle = handle;
}
static void PdIcEnd(const u8* out) { if (t_pd.open && t_pd.entry == 0) PdClose(*(const DWORD*)out == 1); }  // type 1 = succeeded

static bool PdPlanBegin() {
    const Ts3nCounters& k = ts3native_counters;
    if (k.pdMode != 1 || !k.pdUs || t_pd.open || t_pdNoCut || !RgIsSim(GetCurrentThreadId())) return false;
    PdOpen(1, k.pdUs);
    return true;
}
static void PdPlanEnd(u8 r) { if (t_pd.open && t_pd.entry == 1) PdClose(r != 0); }

static void PdCapped() {
    if (!t_pd.open) return;
    t_pd.capped++;
    InterlockedIncrement(&ts3native_counters.pdWorldCapped);
}

static int g_pdSkipMask = 3;  // bit 0 cell, bit 1 water (steerro-test cuts one phase at a time; always 3 in game)
// docs/178 T1: the worker inside a foreign job with a deadline (g_pdWkTid, set by ServeAsync while t_fbDeadline is set): the
// same exits past t_fbDeadline (FbJobCapMs from the job's start), with the runtime switch fbjdMode. Counted per job
// (t_fbjdCut, ServeAsync -> FbjdJobEnd) and per search; the first cut is the job's capAt (as BudgetCap's)
static int PdWorkerSkip(int which) {
    Ts3nCounters& k = ts3native_counters;
    if ((k.fbjdMode != 1 && !t_scProbe && !t_kdKind && !t_ufJob) || !t_fbDeadline || GetCurrentThreadId() != g_pdWkTid || !(g_pdSkipMask >> which & 1)) return 0;
    const LONGLONG now = PdNow();
    if (now < t_fbDeadline) return 0;
    if (t_jobT0 && !t_jobCapAt) t_jobCapAt = now;
    t_fbjdCut[1 + which]++;
    if (!t_scProbe && !t_kdKind && !t_ufJob) InterlockedIncrement(&k.fbjdSkip[which]);  // docs/163 §10: probe cuts show in the job's long-job line only
    return 1;
}
// the entry stubs' question (sim thread inside a session, or the worker in a foreign job): 1 = answer "no path" without searching
static int __cdecl PdSkip(int which) {
    PdSess& s = t_pd;
    if (!s.open) return PdWorkerSkip(which);
    if ((ts3native_counters.pdMode != 1 && !t_ufSess) || !(g_pdSkipMask >> which & 1) || PdNow() < s.dl) return 0;  // docs/202: t_ufSess
    s.skip[which]++;
    InterlockedIncrement(which ? &ts3native_counters.pdSkipWater : &ts3native_counters.pdSkipCell);
    return 1;
}

// docs/178 T1: one foreign job's end (ServeAsync, worker): >= 300 / >= 1000 ms counted in either mode; a cut job's phases and
// its time past the deadline (end - dl: the work after the first cut that no exit stopped)
static void FbjdJobEnd(LONG us, LONGLONG end, LONGLONG dl) {
    Ts3nCounters& k = ts3native_counters;
    if (us >= 300000) InterlockedIncrement(&k.fbjdLong[0]);
    if (us >= 1000000) InterlockedIncrement(&k.fbjdLong[1]);
    if (!t_fbjdCut[0] && !t_fbjdCut[1] && !t_fbjdCut[2]) return;
    for (int i = 0; i < 3; i++)
        if (t_fbjdCut[i]) InterlockedIncrement(&k.fbjdCapped[i]);
    const LONGLONG late = dl && end > dl ? (end - dl) * 1000000 / g_qpf.QuadPart : 0;
    const LONG lateUs = late > 0x7FFFFFFF ? 0x7FFFFFFF : (LONG)late;
    InterlockedExchangeAdd(&k.fbjdLateUs, lateUs);
    Max(&k.fbjdLateMaxUs, lateUs);
}

// cell A* 0x61E500 (thiscall, 6 stack args): past the deadline its empty-start-set exit, else on to the trampoline
static __declspec(naked) void PdCellStub() {
    __asm {
        push eax
        mov eax, dword ptr fs:[0x24]
        cmp eax, g_pdTid
        je on
        cmp eax, g_pdWkTid
        jne off
    on:
        pushad
        pushfd
        push 0
        call PdSkip
        add esp, 4
        test eax, eax
        jnz skip
        popfd
        popad
    off:
        pop eax
        jmp dword ptr [g_pdOrig]
    skip:
        popfd
        popad
        pop eax
        mov dword ptr [ecx + 0x24], 0  // 0x61E539: the inserted-node counter
        mov eax, dword ptr [esp + 8]   // eax as the game's exit leaves it (argument 2), AL = 0
        xor al, al
        ret 0x18
    }
}

// water 0x671930 (thiscall, 9 stack args): past the deadline its "not the same water body" exit
static __declspec(naked) void PdWaterStub() {
    __asm {
        push eax
        mov eax, dword ptr fs:[0x24]
        cmp eax, g_pdTid
        je on
        cmp eax, g_pdWkTid
        jne off
    on:
        pushad
        pushfd
        push 1
        call PdSkip
        add esp, 4
        test eax, eax
        jnz skip
        popfd
        popad
    off:
        pop eax
        jmp dword ptr [g_pdOrig + 4]
    skip:
        popfd
        popad
        pop eax
        mov eax, dword ptr [esp + 0x24]  // 0x671930..0x67193C: the out vector's end = its begin
        mov ecx, dword ptr [eax]
        mov dword ptr [eax + 4], ecx
        xor al, al                       // 0x671B98
        ret 0x24
    }
}

static const u8 kPdCellPro[] = {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF0};         // push ebp; mov ebp,esp; and esp,-16
static const u8 kPdWaterPro[] = {0x8B, 0x44, 0x24, 0x24, 0x83, 0xEC, 0x50};  // mov eax,[esp+24h]; sub esp,50h

// parameters let the selftest put the stubs on dummies
static void PdInstallAt(DWORD cellAt, DWORD waterAt) {
    Ts3nCounters& k = ts3native_counters;
    g_pdOrig[0] = InstallDetour("QuadTreeCellAStar (plan deadline)", cellAt, kPdCellPro, sizeof(kPdCellPro), (void*)PdCellStub, &g_pdOrig[0]);
    g_pdOrig[1] = InstallDetour("WaterPath_Plan (plan deadline)", waterAt, kPdWaterPro, sizeof(kPdWaterPro), (void*)PdWaterStub, &g_pdOrig[1]);
    k.pdReady = (k.budgetReady ? 1 : 0) | (g_pdOrig[0] ? 2 : 0) | (g_pdOrig[1] ? 4 : 0);
    g_pdOn = 1;
}

// after the plan detour, the cost stubs, InstallAsync (SyncIc) and ReplanDedupe; before [RouteJobPhases]
static void InstallPlanDeadline() {
    Ts3nCounters& k = ts3native_counters;
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
    if (k.pdUs || k.pdOwnUs || g_fbjdIni || g_kdIni || g_ufIni) {
        PdInstallAt(0x0061E500, 0x00671930);
        if (g_kdIni) KdInstalled();  // docs/197
        if (g_ufIni) UfInstalled();  // docs/202
        if (g_fbjdIni) {  // docs/178 T1: the worker slot of the same stubs; the deadline is FbJobCapMs's (t_fbDeadline)
            k.fbjdReady = (g_pdOrig[0] ? 1 : 0) | (g_pdOrig[1] ? 2 : 0);
            k.fbjdMode = k.fbjdReady && k.fbjcCapUs && k.budgetReady ? 1 : 0;
            Log("%s foreign job deadline: cell A* / water no-path exits for foreign jobs past FbJobCapMs %ld ms, ready 0x%lX, mode %ld%s",
                k.fbjdMode && k.fbjdReady == 3 ? "OK  " : k.fbjdMode ? "PART" : "FAIL", k.fbjcCapUs / 1000, k.fbjdReady, k.fbjdMode,
                k.fbjcCapUs && k.budgetReady ? "" : " (needs FbJobCapMs > 0 and the cost stubs: no foreign job has a deadline)");
        }
        Log("%s route deadline: PlanDeadlineMs %ld, own task %ld (0 = never); ready 0x%lX (bit 0 world A* cost stubs, 1 cell A*, "
            "2 water), Route_PlanLocked detour %s, route IC sync path %s", k.pdReady == 7 && g_planOrig && k.asyncReady ? "OK  " : "PART",
            k.pdUs / 1000, k.pdOwnUs / 1000, k.pdReady, g_planOrig ? "yes" : "NO (non-IC plans not cut)",
            k.asyncReady ? "yes" : "NO (IC plans not cut)");
    } else {
        Log("route deadline off (PlanDeadlineMs=0, PlanDeadlineOwnMs=0, FbJobDeadline=0, KindDeadlines=0, [UnseenFallback] Enabled=0)");
    }
    if (!g_fmIni) { Log("fail memo off (FailMemo=0)"); return; }
    if (!k.asyncReady) { Log("FAIL fail memo: the route IC's sync path is not hooked ([RouteAsync] detours), nothing served"); return; }
    k.fmMode = g_fmIni == 1 ? 1 : 0;
    k.fmReady = 1;
    g_fmOn = 1;
    Log("OK   fail memo: sync Plan ICs through ReplanDedupe's repeat check, fmMode %ld (1 serve, 0 observe); world version from "
        "[RouteSnap] counters (turned on for this section)", k.fmMode);
}
