// [RouteSnap] Mode=2: routing snapshot P5 (docs/94 §19, §22; counters v61). Included by ts3native.cpp after p4clone.cpp.
// For an async ReplanFromPosition job (A10 kind 3, [RouteSnap] Kinds bit 8) the game takes the answer of the clone-run search
// instead of running its own plan. Everything else is P4's (p4.cpp, p4clone.cpp): the cut at START, the capture of the
// arguments at the job's Route_PlanLocked, the acquire of the working copy, the armed run with T1-T4, the private path list,
// the release. P5 adds:
//   - P5Plan (worker, the job's Route_PlanLocked, still under the job's +0x918 as every A10 job): the clone run into a private
//     list; a clean run's list nodes are moved into the route's out list, the very nodes P4CaptureAnswerRaw read for g_p4Copy,
//     so the taken answer is the one the §13 shadow compare compares;
//   - door asks of a Mode=2 clone run go to the game's own chain, proxied to the sim thread as in any A10 job (P4DoorEnter,
//     §19.1 (b)); the ask runs disarmed, so the P4 managed / event tripwires stay armed for everything else (M2-2);
//   - fallbacks: any refusal, lazy stop, fault, tripwire, stale cut (rtClosing / rtBusy at entry, the P3 epoch moved during the
//     run, the provider's own epoch checks) or a non-routing write the plan read (nr != 0) runs the game's own plan for that
//     request (PlanLockedBody), so a route is never left without an answer;
//   - VerifyEvery: one job in N runs the clone and then the game's plan, keeps the game's answer and writes the §13 record
//     (v3, "m2":1), so shadow-compare-report.ts applies G4 to what Mode=2 would have taken;
//   - mod guard (docs/104 as §19.2 narrows it for (b)): M2-1 the code bytes of every site the clone run passes, snapshotted
//     after the last install, compared at each armed START (a change = a foreign hook: Mode=2 off for the session); M2-2 three
//     tripwires turn it off; M2-3 MonoPatcher's L2 bytes logged once per finding (no fallback).
// [RouteSnap] ReleaseLock=1 (P5b, p5b.cpp, docs/94 §22.9) lends the job's +0x918 to the sim's steering while the clone run
// runs; without it the live +0x918 stays held through the clone run (A10's job hold). [RouteSnap] FreeLock=1 (P5c, p5c.cpp,
// docs/94 §22.11) lends it to every plain sim acquire, parks destroys, and refuses an answer with a routing write after the cut. shortcut: L1 (EA image GUIDs) is not built: it is log only under (b) and needs a managed class
// lookup; add it with docs/104's guard for option (a).

static void P5bBegin(void* lock, bool verify);  // p5b.cpp: lend +0x918 out for the clone run
static void P5bEnd();                           // p5b.cpp: +0x918 back before anything live
static void ApplyP5b(const wchar_t* ini);
static void P5cOnStart(const AsyncJob* j);      // p5c.cpp: the cut's write count for the stale check
static void P5cLend(void* lock, bool verify);   // p5c.cpp: P5bBegin, with every plain sim acquire admitted (FreeLock=1)
static void P5cTake();                          // p5c.cpp: P5bEnd
static bool P5cStale(DWORD seq);                // p5c.cpp: a recorded routing write since the cut: never take the answer
static bool P5cStaleEarly(DWORD seq, bool verify);  // p5c.cpp: the same, known before the acquire (0.35.26)
static bool P5cStaleNow(DWORD seq, bool verify);    // p5c.cpp: P5cStaleEarly's test without its count (P6EarlyCopy, 0.36.56)
// stalelot.cpp (0.36.59, docs/189 phase 2): [RouteSnap] StaleLot arms, read set, verdicts
static bool P5cRan();                           // p5c.cpp: this job's window opened with P5c admission
static int StlModeNow();
static void StlRunBegin(int slm);
static void StlRunEnd();
static void LuNoteRun(DWORD agent);  // lotuse.cpp
static void ApplyLu(const wchar_t* ini);
static bool StlActDefer(int slm, LONG w0);
static bool StlJob(int slm, LONG w0, bool* globOut, bool* fpOnly = NULL);
static void StlVerify(int slm, LONG w0, int eq);
static void ApplySl(const wchar_t* ini);

static volatile LONG g_p5On;   // Mode=2 installed and not turned off
static LONG g_p5TakeRfp = 8;   // [RouteSnap] TakeKinds bit 8: ReplanFromPosition answers taken (P5's default)
// P6: bit 1 = runtime switch p6Plan; docs/189 phase 2c: bits 2 and 4 (A10 Replan, PlanFromPoint) = runtime switch p6All
static LONG P5TakeMask() {
    const Ts3nCounters& k = ts3native_counters;
    return g_p5TakeRfp | (k.p6Plan ? 1 : 0) | (k.p6All ? 6 : 0);
}
static LONG g_p5Verify = 10;   // VerifyEvery (0 = never)
static LONG g_p5VerLend;       // VerifyLend=1 (0.36.23): verify jobs lend +0x918 (P5b / P5c window) like taken ones
static DWORD g_p5N;            // sim thread: armed STARTs, for the verify sample
static void* volatile g_p5GuardLock;  // the routing lock of the world the L2 check last ran in
enum { P5_OFF_SITE = 1, P5_OFF_TRIPS, P5_OFF_FAULT, P5_OFF_STORM };
static const int kP5Trips = 3;
// 0.36.35 (docs/157 §4.4, risk 2): refusal-storm breaker. A refused acquire can cost up to kPcWaitMs under the job's +0x918
// (the sim waits too) and answers nothing; StormRefusals ([RouteSnap], default 128, 0 = off) tried refusals in a row pause
// Mode=2 for StormPauseMs (default 30000): its plans run the game's plan at once, no acquire (m2Fb other, p5StormSkips). The
// third storm turns Mode=2 off for the session (m2Off 4). A taken or verified answer, or a clean run with nr skew, ends a run.
static LONG g_p5StormN = 128, g_p5StormPauseMs = 30000;
static const LONG kP5StormOff = 3;
static LONG g_p5StormRun;            // worker: tried refusals in a row
static ULONGLONG g_p5StormUntil;     // worker: pause end (GetTickCount64), 0 = none

static void P5Off(int why, DWORD at) {
    if (!InterlockedExchange(&g_p5On, 0)) return;
    Ts3nCounters& k = ts3native_counters;
    k.m2Off = why;
    InterlockedAnd(&k.m2Ready, ~1);
    if (why == P5_OFF_SITE) Log("RouteSnap: Mode=2 off: site 0x%08lX owned by unknown hook; plans run as A10", at);
    else Log("RouteSnap: Mode=2 off: %s; plans run as A10", why == P5_OFF_TRIPS ? "3 clone-run tripwires (M2-2)" :
             why == P5_OFF_STORM ? "3 refusal storms" : "P4 fault");
}

// --- M2-1: the code bytes of our sites in the clone run's closure, as they were after every install ---
struct P5Site { DWORD at; u8 b[5]; };
static const int kP5Sites = 160;
static P5Site g_p5Sites[kP5Sites];
static int g_p5NSites;
static void P5SnapAt(DWORD at) {
    if (!at || g_p5NSites == kP5Sites || !Readable((const void*)(uintptr_t)at, 5)) return;
    for (int i = 0; i < g_p5NSites; i++) if (g_p5Sites[i].at == at) return;
    P5Site& s = g_p5Sites[g_p5NSites++];
    s.at = at;
    memcpy(s.b, (const void*)(uintptr_t)at, 5);
}
// DllMain, after the last section installed (S3SS and S3PP install later, from their own threads: docs/68 §5)
static void P5Snapshot() {
    if (!g_p5On) return;
    Ts3nCounters& k = ts3native_counters;
    for (int i = 0; i < P4_SITES; i++) P5SnapAt(g_p4At[i]);
    P5SnapAt(P4_DOOR_AT);
    P5SnapAt(ROUTE_PLAN_LOCKED);
    P5SnapAt(TS3A(0x00765550));  // IC_Route_ReplanFromPosition: the A10 collect detour
    if (g_pcQIns) P5SnapAt(TS3A(0x00666C50));
    if (g_pcQRem) P5SnapAt(TS3A(0x00666E30));
    for (int i = 0; i < RG_SITES; i++) if (g_rgTramp[i]) P5SnapAt(kRgSites[i].addr);
    for (int i = 0; i < RT_N; i++) if (g_rtTramp[i]) P5SnapAt(kRtSites[i].addr);
    for (int i = 0; i < SN_N; i++) if (g_snJmp[i]) P5SnapAt(kSn[i].at);
    for (int i = 0; i < 3; i++) if (g_p4DlJmp[i]) P5SnapAt(kP4Dl[i].at);
    if (k.p6Plan) P5SnapAt(TS3A(0x00765160));  // P6: IC_Route_Plan, the async START detour of the taken Route.Plan jobs
    if (k.p6All) {  // docs/189 phase 2c: the A10 collect detours of the taken Replan / PlanFromPoint jobs
        if (k.arReady & 1) P5SnapAt(TS3A(0x00765490));
        if (k.arReady & 2) P5SnapAt(TS3A(0x00765240));
    }
    k.m2Sites = g_p5NSites;
    InterlockedOr(&k.m2Ready, 2);
    Log("route snapshot P5: M2-1 snapshot of %d code sites; clone provider %s", g_p5NSites,
        g_p4Clone ? "registered" : "none (every Mode=2 job plans as A10, fallback \"clone\")");
}
static DWORD P5SiteChanged() {
    for (int i = 0; i < g_p5NSites; i++) {
        const P5Site& s = g_p5Sites[i];
        if (!Readable((const void*)(uintptr_t)s.at, 5) || memcmp((const void*)(uintptr_t)s.at, s.b, 5)) return s.at;
    }
    return 0;
}

// --- M2-3 (log only): MonoPatcher.asi's native sites (docs/107 §2.2, docs/104 L2) ---
struct P5L2 { DWORD at; u8 len; u8 b[8]; const char* what; };
static P5L2 g_p5L2[5] = {  // b = the vanilla bytes [verified, docs/104 §5.4, docs/107 §2.2; steerro-test checks them on the image]
    {TS3A(0x00D7E560), 1, {0x51}, "MonoScriptHost_Init: detoured (MinHook)"},
    {TS3A(0x00E5CA90), 8, {0x55, 0x8D, 0x6C, 0x24, 0x98, 0x81, 0xEC, 0x08}, "GenerateCode: ReplaceIL detour"},
    {TS3A(0x00E59218), 6, {0x0F, 0x86, 0xA9, 0xB1, 0xFF, 0xFF}, "ret-values jump rewritten (MonoPatcher.asi at load)"},
    {TS3A(0x00E5637D), 2, {0x75, 0x26}, "JIT check 1: ForceJIT on"},
    {TS3A(0x00E648DC), 2, {0x74, 0x4C}, "JIT check 2: ForceJIT on"},
};
static void P5ModGuard() {
    Ts3nCounters& k = ts3native_counters;
    for (int i = 0; i < 5; i++) {
        const P5L2& s = g_p5L2[i];
        const u8* p = (const u8*)(uintptr_t)s.at;
        if (!Readable(p, s.len)) continue;
        const bool found = memcmp(p, s.b, s.len) != 0;
        if (!found || (k.m2Guard & (1 << i))) continue;
        InterlockedOr(&k.m2Guard, 1 << i);
        Log("ModGuard: L2 0x%08lX %s (MonoPatcher; log only, Mode=2 stays on: docs/94 §19.2 M2-3)", s.at, s.what);
    }
    InterlockedOr(&k.m2Ready, 4);
}

// sim thread, async START of a Mode=2 build (P4OnStart): arm the jobs of the taken kinds ([RouteSnap] TakeKinds & Kinds: bit 8
// ReplanFromPosition, P6 bit 1 own Route.Plan STARTs; a FallbackAsync foreign Route.Plan job keeps its FbJobCapMs path and is
// armed only with [RouteSnap] TakeForeign, docs/189 phase 4: always a take job, never verify, its FbJobCapMs deadline kept in
// the clone run through P6fbCloneBudget), every VerifyEvery-th one to verify. docs/189 phase 2c ([RouteSnap] TakeKindsAll, runtime p6All): own-task
// Replan (1) and PlanFromPoint (2) jobs too. Their IC runs the same Route_PlanLocked below Route_PlanCommon as kinds 0 and 3
// (docs/87 §5 as built, I-87-10), only its first call is captured, and P5Plan is kind-independent; a foreign-task one (af,
// docs/176) is left to docs/189 phase 4. The switch is read here, once per job: the take decision is the arm
static void P5OnStart(AsyncJob* j) {
    Ts3nCounters& k = ts3native_counters;
    if (!g_p5On || !k.m2Take || (unsigned)j->kind > 3 || (j->fb && !k.p6fbOn)) return;
    const bool newKind = j->kind == 1 || j->kind == 2;
    if (newKind && j->af) {
        if ((g_p4Kinds >> j->kind) & 1) InterlockedIncrement(&k.pkAfSkip[j->kind - 1]);
        return;
    }
    if (!((g_p4Kinds >> j->kind) & 1)) return;
    if (!((P5TakeMask() >> j->kind) & 1)) { InterlockedIncrement(&k.pkUntaken[j->kind]); return; }
    if (const DWORD at = P5SiteChanged()) {
        k.m2SiteAt = (LONG)at;
        P5Off(P5_OFF_SITE, at);
        return;
    }
    if (g_rgLock != g_p5GuardLock) {  // once per world: ForceJIT can change between worlds
        g_p5GuardLock = g_rgLock;
        P5ModGuard();
    }
    // foreign: take only (its FbJobCapMs deadline makes the two answers time-dependent), own verify cadence unchanged; with
    // FbJobCapMs=0 (a probe night) there is no deadline and foreign jobs verify on the same cadence (integration review)
    j->p4 = (!j->fb || !k.fbjcCapUs) && g_p5Verify && ++g_p5N % (DWORD)g_p5Verify == 0 ? 3 : 2;
    if (j->fb) InterlockedIncrement(&k.p6fbArmed);
    // 0.36.23 (docs/94 §22.32): the cut's write count, as P4OnStart keeps it. Before, Mode=2 left j->p4W as the slot's last
    // value, so c.stale (writes between the cut and the live plan) was noise and a verify job compared a newer live world as M2
    g_pcCutW = g_spWrites;
    j->p4Ver = P4CloneCut();
    j->p4Why = j->p4Ver ? PS_NONE : g_pcCutWhy;
    j->p4W = j->p4Ver ? g_pcCutW : g_spWrites;
    j->p6Borrow = j->p4Ver && g_pcBorrowed;
    P5cOnStart(j);
    InterlockedIncrement(&k.pkArmed[j->kind]);
    const int ki = j->kind == 3;
    if (!newKind) InterlockedIncrement(&k.p6Armed[ki]);  // the P6 [2] split stays Route.Plan / ReplanFromPosition
    if (j->p6Borrow && !newKind) InterlockedIncrement(&k.p6Borrowed[ki]);
    if (j->kind == 0) {  // P6 phase split (§24.7): the START and its cut, for the worker's per-job record
        LARGE_INTEGER q;
        QueryPerformanceCounter(&q);
        j->p6T0 = q.QuadPart;
        j->p6CutQpc = j->p4Ver ? g_pcCutQpc : 0;
    }
    InterlockedIncrement(&k.m2Armed);
    InterlockedIncrement(&k.snShadowArmed);
}

// worker, ServeAsync before it takes the job's +0x918 (0.35.19, docs/94 §22.22): the replay consumer applies the records up
// to the job's cut only under +0x918 (RpDrainBody), so once the job holds it PcAcquire can only wait out kPcWaitMs with the
// lock the sim waits for held, then refuse (mat/timeout) and plan with the game. Wait for the consumer here instead, holding
// nothing, bounded the same way; idle too (caught up, it runs RpGc holding g_rpUse, which the acquire needs)
static LONGLONG g_p6Pick;   // worker: QPC at this job's pickup (P6 phase split)
static DWORD g_p6PickSeq;   // ... and the job it belongs to
static LONG g_p6PreUs;      // ... and P5PreLock's wait
static void P5PreLockBody(const AsyncJob* j);
static void P5EarlyCopy(const AsyncJob* j);
static void P5PreLock(const AsyncJob* j) {
    if (!j->p6T0) { P5PreLockBody(j); P5EarlyCopy(j); return; }
    LARGE_INTEGER q;
    QueryPerformanceCounter(&q);
    g_p6Pick = q.QuadPart, g_p6PickSeq = j->seq;
    P5PreLockBody(j);
    P5EarlyCopy(j);
    g_p6PreUs = UsSince(q);  // the early acquire included: P6JobBegin splits its copy off
}
static void P5PreLockBody(const AsyncJob* j) {
    if (j->p4 < 2 || !j->p4Ver || !g_rpThreaded || g_p4Clone != &kPcProvider) return;
    const DWORD ver = j->p4Ver;
    const ULONGLONG until = GetTickCount64() + kPcWaitMs;
    while ((g_rpTail != ver || g_rpPhase) && (DWORD)(ver - g_rpTail) <= (DWORD)(g_rpHead - g_rpTail) && GetTickCount64() <= until) {
        SetEvent(g_rpEvent);
        Sleep(0);
    }
}

// 0.36.56 (docs/94 §24.14): [RouteSnap] P6EarlyCopy (runtime switch p6Early, --ab routesnapearly; default 0). The own-only
// acquire P5Plan makes in the lent window (0.35.26) runs here instead, on the worker after P5PreLock and before ServeAsync
// takes the job's +0x918, so the copy is outside the job's A_RUNNING span (§24.11.4 (c)1(i)). Why it answers the same world:
//   - an own-only acquire reads only the cut's private copies, the P3 images and the clone, and writes only g_pcWork and the
//     clone (PcAcquireOwnOnly; no game call, PcActiveLot fault-guarded), so it needs no game lock; the clone is held still
//     by g_rpUse from here to the job's release (only the consumer frees or applies to it, and only holding g_rpUse);
//   - no lock-order cycle: the worker then waits for +0x918 holding g_rpUse, and no +0x918 holder waits for g_rpUse (the
//     consumer only tries it under +0x918 and lets go of +0x918 before it blocks on g_rpUse, RpDrainBody; the sim never takes
//     g_rpUse; the cut lock is not held past the acquire);
//   - the checks PcAcquire made against live state (epoch, active lot, clone flags) are made again by P5Plan under the job's
//     hold (P5EarlyRecheck); every other P5Plan refusal (capture, rtClosing / rtBusy, cold, the P5c write test before and
//     after the run, the epoch after the run, nr) runs unchanged on the early clone.
// Kept as before: a non-own-only acquire (game calls) runs in P5Plan under the hold; an early refusal ended the cut's hold
// already (PcHoldEnd), so P5Plan takes the refusal without a second acquire or a P4CloneDrop.
struct P5Early { DWORD seq, ver; P4Clone cl; int r; bool live; LONG copyUs; u64 active; };
static P5Early g_p5e;  // worker only: the early acquire of the job ServeAsync is starting (live until its plan or its end)
static void P5EarlyCopy(const AsyncJob* j) {
    Ts3nCounters& k = ts3native_counters;
    g_p5e.live = false;
    if (!k.p6Early || j->p4 < 2) return;
    if (!j->p4Ver || !g_p5On || !k.m2Take || !(k.snP4Ready & 1) || g_p4Clone != &kPcProvider) { InterlockedIncrement(&k.p6eSkip[2]); return; }
    // P5Plan would skip the acquire: a recorded write since the cut (non-verify, P5c) or a refusal-storm pause
    if ((P5cStaleNow(j->seq, j->p4 == 3) && !StlActDefer(StlModeNow(), j->p4W)) || (g_p5StormUntil && GetTickCount64() < g_p5StormUntil)) { InterlockedIncrement(&k.p6eSkip[1]); return; }
    if (!PcAcquireOwnOnly(j->p4Ver)) { InterlockedIncrement(&k.p6eSkip[0]); return; }
    P5Early& x = g_p5e;
    LARGE_INTEGER t0;
    QueryPerformanceCounter(&t0);
    x.cl = P4Clone();
    x.r = P4CloneAcquire(j->p4Ver, &x.cl);
    const LONG us = UsSince(t0);
    x.copyUs = x.r < 0 ? g_pcLastCopyUs : 0;
    x.active = g_pcLastActive;
    x.seq = j->seq, x.ver = j->p4Ver, x.live = true;
    InterlockedExchangeAdd(&k.snMatUs, us);
    InterlockedExchangeAdd(&k.p6eUs, us);
    InterlockedExchangeAdd(&k.p6eCopyUs, x.copyUs);
    InterlockedIncrement(x.r < 0 ? &k.p6eCopies : &k.p6eRefused);
}
// P5Plan: claims this job's early acquire, if any (*r = its result: -1 = the clone in *cl, held)
static bool P5EarlyClaim(const P4Job& c, P4Clone* cl, int* r) {
    P5Early& x = g_p5e;
    if (!x.live || x.seq != c.seq || x.ver != c.ver) return false;
    x.live = false;
    *cl = x.cl, *r = x.r;
    return true;
}
// P5Plan under the job's +0x918: PcAcquire's live-state tests again (they ran at the copy, before the hold)
static int P5EarlyRecheck(const P4Clone& cl) {
    Ts3nCounters& k = ts3native_counters;
    if (cl.epoch != (DWORD)g_spEpoch || PcActiveLot() != g_p5e.active) { k.snPcRefused[5]++, g_pcAcqWhy = PS_MOVED; return P4_AB_EPOCH; }
    if (!g_rpClone || g_rpNeedEpoch || g_rpBad || g_rpStale) { k.snPcRefused[2]++, g_pcAcqWhy = PS_NOCLONE; return P4_AB_MAT; }
    return -1;
}
// ServeAsync after the IC, and on its back-out: an early clone no plan claimed goes back (g_rpUse, the cut's hold)
static void P5EarlyEnd() {
    if (!g_p5e.live) return;
    g_p5e.live = false;
    if (g_p5e.r >= 0) return;  // a refusal: the hold ended at the acquire
    P4CloneRelease(&g_p5e.cl, 0, NULL, 0);
    InterlockedIncrement(&ts3native_counters.p6eRel[2]);
}

// the clone run's nodes into the route's out list (empty: P5Plan checks it at entry). Same EASTL list, same allocator words
// (P4Head copied the live head's), so the route owns and frees them as its own [layout verified, docs/94 §18.7 I-94-102]
static void P5Move(int* head, int* outList) {
    if (head[0] == (int)(uintptr_t)head) return;
    int* first = (int*)(uintptr_t)head[0];
    int* last = (int*)(uintptr_t)head[1];
    first[1] = last[0] = (int)(uintptr_t)outList;
    outList[0] = (int)(uintptr_t)first;
    outList[1] = (int)(uintptr_t)last;
    head[0] = head[1] = (int)(uintptr_t)head;
}

// 0.35.26 (docs/94 §22.30), log only (no counter change; worker only): acquires made inside the lent window, stale jobs that
// skipped the acquire, cut holds dropped by a refusal before the acquire; logged every 5 min
static LONG g_p5EarlyLend, g_p5EarlyStale, g_p5Drops;
static void P5Log5() {
    static ULONGLONG s_at;
    static LONG s_l0, s_s0, s_d0;
    const ULONGLONG now = GetTickCount64();
    if (!s_at) s_at = now;
    if (now - s_at < 300000) return;
    Log("P5: last %lu s: acquires inside the lent window %ld (of %ld acquired since start), stale before the acquire %ld "
        "(skipped acquire and run), cut holds dropped before the acquire %ld", (DWORD)((now - s_at) / 1000), g_p5EarlyLend - s_l0,
        ts3native_counters.snPcAcquired, g_p5EarlyStale - s_s0, g_p5Drops - s_d0);
    s_at = now, s_l0 = g_p5EarlyLend, s_s0 = g_p5EarlyStale, s_d0 = g_p5Drops;
}

static volatile LONG g_p5VerLog;
static void P5Storm(bool refused) {  // worker, once per Mode=2 plan that reached the acquire
    Ts3nCounters& k = ts3native_counters;
    if (!refused) { g_p5StormRun = 0; return; }
    Max(&k.p5StormRunMax, ++g_p5StormRun);
    if (!g_p5StormN || g_p5StormRun < g_p5StormN) return;
    g_p5StormRun = 0;
    const LONG n = InterlockedIncrement(&k.p5StormTrips);
    g_p5StormUntil = GetTickCount64() + (ULONGLONG)g_p5StormPauseMs;
    Log("RouteSnap: Mode=2 refusal storm %ld: %ld refused acquires in a row (mat %ld, epoch %ld, noclone/other %ld so far): paused %ld ms",
        n, g_p5StormN, k.m2Fb[P4_AB_MAT], k.m2Fb[P4_AB_EPOCH], k.m2Fb[P4_AB_CLONE] + k.m2Fb[P4_AB_OTHER], g_p5StormPauseMs);
    if (n >= kP5StormOff) P5Off(P5_OFF_STORM, 0);
}
// worker, the armed job's Route_PlanLocked (P4LivePlan after the argument capture; the job holds +0x918)
// docs/189 §4 phase 4, worker: an armed foreign job's clone run gets the job's FbJobCapMs deadline on the world A* cost stubs, as
// CallPlanBudget0 gives the game's plan (the clone run calls g_planOrig directly, P4Guarded). Past it BudgetStep walls every edge
// and the clone's A* leaves through its own "open list empty" exit: the answer is the planner's no-path, as the game's capped
// plan. BudgetStep touches only this thread's t_budget, t_jobCapAt and counters; the cost stubs are code the clone run executes
// anyway (with t_budget.on 0 they pass). false = no deadline (own job, cap off, cost stubs not installed): nothing changed
static bool P6fbCloneBudget(Budget& outer) {
    if (!g_jobFb || !t_fbDeadline || !ts3native_counters.budgetReady) return false;
    outer = t_budget;
    const Budget b = {1, 0, {}, 0, 0, 0, t_fbDeadline, 0};
    t_budget = b;
    InterlockedIncrement(&ts3native_counters.p6fbCloneDl);
    return true;
}
static void P6fbCloneBudgetEnd(const Budget& outer) {
    if (t_budget.capped) {  // counted as the job's cap (ServeAsync's fbjcCapped, the long-job line)
        t_fbCapHit = 1;
        InterlockedIncrement(&ts3native_counters.p6fbCloneCapped);
    }
    t_budget = outer;
}

static u8 P5Plan(void* world, void* edx, int* outList, int* starts, int* goals, DWORD agent, DWORD doPlan, u8* opts, int* out1,
                 int* out2, DWORD guidLo, DWORD guidHi) {
    Ts3nCounters& k = ts3native_counters;
    P4Job& c = g_p4Job;
    const bool verify = c.mode == 3;
    const bool noLend = verify && !g_p5VerLend;  // VerifyLend: the verify job's clone run also runs in the lent window
    const int ki = c.kind == 3;  // P6 split: 0 Route.Plan, 1 ReplanFromPosition
    const int slm = StlModeNow();  // StaleLot arm, fixed for this plan (a flip between jobs takes effect at the next plan)
    const int kk = c.kind & 3;   // docs/189 phase 2c: pk* by job kind
    const bool p6k = kk == 0 || kk == 3;  // the P6 [2] split keeps Route.Plan / ReplanFromPosition only
    LARGE_INTEGER tPlan;
    QueryPerformanceCounter(&tPlan);
    InterlockedIncrement(&k.m2Plans);
    if (p6k) InterlockedIncrement(&k.p6Plans[ki]);
    InterlockedIncrement(&k.pkPlans[kk]);
#ifdef TS3N_FAULTINJ
    g_fiPlan = 0;  // docs/157: every Nth plan injects the next FaultInject bit in turn
    if (const LONG turn = g_fiMask & FI_TURN)
        if (++g_fiN % g_fiEvery == 0) {
            do g_fiTurn = (g_fiTurn + 1) & 31; while (!(turn & (1 << g_fiTurn)));
            g_fiPlan = 1 << g_fiTurn | (g_fiMask & FI_EAHEAP);
            Log("FaultInject: Mode=2 plan %lu (armed plan %ld) injects 0x%lX", c.seq, g_fiN, g_fiPlan);
        }
#endif
    if (t_p6.act && t_p6.cutQpc) t_p6.cutAgeUs = (LONG)((tPlan.QuadPart - t_p6.cutQpc) * 1000000 / g_qpf.QuadPart);
    bool p6Stale = false;  // P6 split: refused for a recorded write since the cut (P5c)
    int why = c.abort;  // the capture's
    if (why < 0 && (!g_p5On || !(k.snP4Ready & 1))) why = P4_AB_OTHER;
    if (why < 0 && outList[0] != (int)(uintptr_t)outList) {  // the clone's nodes go into an empty list only
        why = P4_AB_CAPTURE;
        InterlockedIncrement(&k.m2FbList);
    }
    if (why < 0 && (k.rtClosing || k.rtBusy)) why = P4_AB_EPOCH;
    if (why < 0 && P4Cold()) why = P4_AB_COLD;
    // 0.35.26 (docs/94 §22.30): an answer P5c refuses anyway (a routing write since the START cut) skips the acquire and the run
    // StaleLot acting: only lot-keyed writes since the cut = decided after the run against its read set (slDef)
    const bool slDef = why < 0 && P5cStaleNow(c.seq, verify) && StlActDefer(slm, c.w0);
    if (why < 0 && !slDef && P5cStaleEarly(c.seq, verify)) why = P4_AB_EPOCH, g_p5EarlyStale++, p6Stale = true;
    if (why < 0 && g_p5StormUntil) {
        if (GetTickCount64() < g_p5StormUntil) why = P4_AB_OTHER, InterlockedIncrement(&k.p5StormSkips);
        else g_p5StormUntil = 0;
    }
    P4Clone cl = {};
    bool have = false, ran = false, lent = false, tried = false;
    int er = -1;  // P6EarlyCopy (0.36.56): the early acquire's result, -1 = its clone is in cl, held
    const bool early = P5EarlyClaim(c, &cl, &er);
    bool eHeld = early && er < 0;
#ifdef TS3N_FAULTINJ
    if (eHeld && (g_fiPlan & FI_OOM)) {  // as P4CloneAcquire's: the copy failed after the hold (refused as mat)
        g_fiPlan &= ~FI_OOM;
        P4CloneRelease(&cl, 0, NULL, 0);
        eHeld = false, er = P4_AB_MAT;
        Log("FaultInject: OOM in the early clone build (acquire refused as mat)");
    }
#endif
    if (why < 0 && early) {
        tried = true;
        if (eHeld && (why = P5EarlyRecheck(cl)) >= 0) {
            P4CloneRelease(&cl, 0, NULL, 0);
            eHeld = false;
            InterlockedIncrement(&k.p6eRel[1]);
        } else if (eHeld) {
            have = true;
            InterlockedIncrement(&k.p6eUsed);
        } else why = er;
    } else if (early) {
        if (eHeld) P4CloneRelease(&cl, 0, NULL, 0), InterlockedIncrement(&k.p6eRel[0]);  // the cut's hold ends with it
    } else if (why < 0) {
        tried = true;
        // 0.35.26: the copy (5-6 ms on BP) inside the lent window when it makes no game call; else under the hold as before
        if (!noLend && g_p4Clone == &kPcProvider && PcAcquireOwnOnly(c.ver)) {
            P5cLend((u8*)world + 0x918, noLend);
            lent = true;
            g_p5EarlyLend++;
        }
        LARGE_INTEGER t0;
        QueryPerformanceCounter(&t0);
        const int r = P4CloneAcquire(c.ver, &cl);
        const LONG acqUs = UsSince(t0);
        InterlockedExchangeAdd(&k.snMatUs, acqUs);
        if (t_p6.act) {  // the acquire = waits for the consumer / g_rpUse + the copy
            const LONG cp = g_p4Clone == &kPcProvider ? g_pcLastCopyUs : 0;
            t_p6.copyUs += cp, t_p6.rpWaitUs += acqUs - cp;
        }
        if (r >= 0) why = r; else have = true;
    } else if (c.ver) {
        P4CloneDrop(c.ver);  // refused before the acquire: the consumer's hold moves on now, not at kPcPendMs (as P4's shadow)
        g_p5Drops++;
    }
    int head[4];
    P4Head(head, c);
    LONG rel = 0;
    const LONG read0 = t_p4Read;  // the IC's own reader bits so far (P4RunClone resets the thread's)
    if (have && !lent) P5cLend((u8*)world + 0x918, noLend);
    if (have) StlRunBegin(slm);  // StaleLot: the clone run's LotTable_Find keys = its read set
    if (have) {
        Budget outer = {};
        const bool dl = P6fbCloneBudget(outer);  // TakeForeign: the foreign job's FbJobCapMs deadline on the clone's world A*
        rel = P4RunClone(c, cl, head, true);
        if (dl) P6fbCloneBudgetEnd(outer);
    }
    StlRunEnd();
    if (have) LuNoteRun(agent);  // [RouteSnap] LotUseReadout
    if (slm && !have) InterlockedIncrement(&k.stlNoRun);
    if (have || lent) P5cTake();  // also after a refused acquire in the window: +0x918 back before the game's plan
    if (have) {
        ran = true;
        t_p4Read = read0;
        if (c.abort >= 0) why = c.abort;
        InterlockedExchangeAdd(&k.m2JobUs, c.jobUs);
        if (t_p6.act) t_p6.cloneUs += c.jobUs;
        const LONG n = c.nAsk;
        InterlockedIncrement(&k.m2DoorHist[n == 0 ? 0 : n <= 2 ? 1 : n <= 5 ? 2 : n <= 10 ? 3 : 4]);
        const LONG recs = g_p4Clone == &kPcProvider ? (LONG)(g_rpHead - c.ver) : 0;  // replay records written since the cut
        if (recs > 0) { InterlockedIncrement(&k.m2Stale); InterlockedExchangeAdd(&k.m2StaleRecs, recs); }
    }
    LONG nr1[kP4Fam];
    P4NrSnap(nr1);
    LONG nr = P4NrBits(c.nr0, nr1, read0 | c.cloneRead);
    if (why < 0 && (k.rtClosing || k.rtBusy || c.epoch != (DWORD)g_spEpoch)) why = P4_AB_EPOCH;  // the world moved during the run
    // P5c: the lock was free; a routing write after the cut refuses the answer. A verify job (VerifyLend) is compared instead:
    // the game's plan below sees the write, the record classifies it as time skew (M5) through c.stale
    const bool t_p5cWasRan = P5cRan();  // StaleLot: the P5c rule applies to this job (its window opened)
    const bool stale5c = P5cStale(c.seq);
    // StaleLot (docs/189 phase 2): both verdicts on a clean-run non-verify job; the acting arm takes a job that only lot-keyed
    // writes to lots the run did not read made stale (nr, the epoch test and every other refusal stay)
    bool slTake = false;
    if (slm && have && !verify && why < 0 && (t_p5cWasRan || slDef)) {
        bool glob;
        const bool lot = StlJob(slm, c.w0, &glob);
        slTake = slm == 1 && stale5c && !lot;
        if (slDef && glob && lot && !stale5c) why = P4_AB_EPOCH, p6Stale = true;  // deferred, no window: the per-lot verdict
    }
    if (slDef && !have && why < 0) why = P4_AB_EPOCH, p6Stale = true;  // deferred but never ran: stale as the global rule
    if (why < 0 && stale5c && !verify && !slTake) why = P4_AB_EPOCH, p6Stale = true;
    if (slTake && why < 0 && !nr) InterlockedIncrement(&k.stlActTaken);
    if (why < 0 && !g_p4Copy.valid) why = P4_AB_CAPTURE;
    if ((why == P4_AB_MANAGED || why == P4_AB_EVENT) && InterlockedIncrement(&k.m2Trips) >= kP5Trips) P5Off(P5_OFF_TRIPS, 0);
    if (why == P4_AB_FAULT) P5Off(P5_OFF_FAULT, 0);
    P5Storm(tried && why >= 0);
    u8 r;
    if (why < 0 && !verify && !nr) {  // taken: the answer is the clone run's, no game plan
        P5Move(head, outList);
        if (out1) *out1 = g_p4Copy.out1;
        if (out2) *out2 = g_p4Copy.out2;
        r = g_p4Copy.ok;
        InterlockedIncrement(&k.m2Taken);
        if (p6k) InterlockedIncrement(&k.p6Taken[ki]);
        InterlockedIncrement(&k.pkTaken[kk]);
        if (g_jobFb) InterlockedIncrement(&k.p6fbTaken);  // the worker's own ServeAsync write: this job is a foreign one
        if (t_p6.act) t_p6.out = 0;
        t_p6wOut = c.borrow ? P6O_BORROWED : P6O_TAKEN;  // [P6Waits] (p6wait.cpp): the job's outcome, last plan wins
    } else {
        t_p6wOut = p6Stale && !verify ? P6O_STALE : P6O_REFUSED;
        // a lazy stop already released the list; a faulted run's list is left alone (half-built, the copy is discarded)
        if (ran && why != P4_AB_FAULT && !P4FreeList(head)) Log("P5: private path list of plan %lu not released", c.seq);
        if (why >= 0) InterlockedIncrement(&k.m2Fb[why]);
        else if (!verify) InterlockedIncrement(&k.m2FbNr);
        InterlockedIncrement(verify ? &k.pkVerify[kk] : p6Stale ? &k.pkStale[kk] : &k.pkFb[kk]);
        if (g_jobFb) InterlockedIncrement(&k.p6fbGame);  // under t_fbDeadline: CallPlanBudget cuts it as an unarmed foreign job
        LARGE_INTEGER t0;
        QueryPerformanceCounter(&t0);
        r = PlanLockedBody(world, edx, outList, starts, goals, agent, doPlan, opts, out1, out2, guidLo, guidHi);
        c.gameUs = UsSince(t0);
        if (p6k) InterlockedExchangeAdd(&k.p6GameUs[ki], c.gameUs);
        InterlockedExchangeAdd(&k.pkGameUs[kk], c.gameUs);
        if (t_p6.act) t_p6.gameUs += c.gameUs, t_p6.out = p6Stale ? 1 : 3;
        if (verify) {
            // 0.36.23 (docs/94 §22.32): the live plan ran after the clone run, so its skew window ends here, not at nr1: the
            // non-routing writes up to its end (with its own reader bits), and every recorded write since the START cut (the
            // job's queue time and any lent window) make a difference time skew (M5), as in the P4 shadow
            P4NrSnap(nr1);
            nr = P4NrBits(c.nr0, nr1, t_p4Read | read0 | c.cloneRead);
            c.stale = g_spWrites != c.w0;
            int cls = 4;
            const bool capOk = P4CaptureAnswerRaw(outList, r, out1 ? *out1 : -1, out2 ? *out2 : -1, &g_p4Live) && why < 0 && g_p4Copy.valid;
            if (capOk) cls = P4Classify(g_p4Live, g_p4Copy, nr, P4TieN(c.starts, c.goals), c.stale);
            else if (why < 0) why = P4_AB_CAPTURE;
            if (slm && have) {  // StaleLot evidence: the raw class (no skew folding, no tie count); M0 / M1 = equal
                const int raw = capOk && !nr ? P4Classify(g_p4Live, g_p4Copy, 0, P4TieN(c.starts, c.goals), false, false) : -1;
                StlVerify(slm, c.w0, raw < 0 ? 2 : raw <= 1 ? 0 : 1);
            }
            if (have && (cls == 2 || cls == 3) && g_p4ObsOn) P4ObCapture(c, cl.world);  // I-94-381: name an in-room M2 live
            if (have && cls < 4) P4LdCapture(c, cl.world, cls);  // docs/94 §24.8.4, [RouteSnap] M2LotDump (off by default)
            InterlockedIncrement(&k.m2Verify);
            InterlockedIncrement(&k.m2VerM[cls]);
            if (cls == 2 || cls == 3) {
                InterlockedIncrement(&k.m2VerBad);
                if (c.borrow) InterlockedIncrement(&k.p6BorrowVerBad);
                if (t_p6.act) t_p6.out = 2;
                if (InterlockedIncrement(&g_p5VerLog) <= 20)
                    Log("P5 verify: plan %lu M%d (the clone answer would have been taken; the game's was kept)", c.seq, cls);
            }
            if (!g_p4Live.valid) g_p4Live.nPaths = 0;  // the record prints no paths for an unreadable answer
            P4Record(c, why >= 0 ? kP4Ab[why] : NULL, nr);
        }
    }
    if (have) P4CloneRelease(&cl, rel, c.dirty, c.nDirty);
    if (rel & P4REL_WRAP) g_p4Work.wrap = 0;
    c.armed = 0;  // nothing left for P4Shadow after J_DONE
    P4JobFree(c);
#ifdef TS3N_FAULTINJ
    g_fiPlan = 0;  // a bit the plan never reached (refused before the acquire or the run) is not carried over
#endif
    const LONG planUs = UsSince(tPlan);
    if (p6k) InterlockedExchangeAdd(&k.p6PlanUs[ki], planUs);
    InterlockedExchangeAdd(&k.pkPlanUs[kk], planUs);
    P5Log5();
    return r;
}

// --- P6 phase split (docs/94 §24.7; count / log only): one armed Route.Plan job on the worker, from P4LiveBegin to P4LiveEnd.
// queue = START to pickup (P5PreLock entry), start = pickup to the IC call less P5PreLock's wait (H2 hold, +0x918, back-offs),
// rpWait = P5PreLock + the acquire's waits, copy, clone run, the game's plan in P5Plan, other = the rest of the job
static LONG g_p6LogUs = 20000;  // [RouteSnap] P6LogMs (default 20; 0 = off), in us
static const int kP6LazyPhase = 8;  // RJP_LOTGRAPH (routephases.cpp asserts it): LotGraph_Build, the lazy lot graph build
static LONGLONG P6Now() { LARGE_INTEGER q; QueryPerformanceCounter(&q); return q.QuadPart; }
static LONG P6Us(LONGLONG d) { return (LONG)(d * 1000000 / g_qpf.QuadPart); }
static void P6JobBegin(const AsyncJob* j) {
    t_p6.act = false;
    if (!j->p6T0 || j->p4 < 2) return;
    const Ts3nCounters& k = ts3native_counters;
    const LONGLONG now = P6Now();
    const bool picked = g_p6PickSeq == j->seq && g_p6Pick >= j->p6T0 && g_p6Pick <= now;  // else (tests): all queue
    const LONGLONG pick = picked ? g_p6Pick : now;
    const LONG pre = picked ? g_p6PreUs : 0;
    const LONG eCopy = picked && g_p5e.live && g_p5e.seq == j->seq ? g_p5e.copyUs : 0;  // P6EarlyCopy: its copy, inside pre
    t_p6 = P6Job();
    t_p6.t0 = j->p6T0, t_p6.cutQpc = j->p6CutQpc;
    t_p6.queueUs = P6Us(pick - j->p6T0);
    t_p6.rpWaitUs = pre - eCopy;
    t_p6.copyUs = eCopy;
    t_p6.startUs = P6Us(now - pick) - pre;
    t_p6.out = 3;  // until P5Plan says otherwise (an IC that never reached Route_PlanLocked stays "other fallback")
    t_p6.lazyN0 = k.rjpN[0][kP6LazyPhase], t_p6.lazyUs0 = k.rjpUs[0][kP6LazyPhase];
    t_p6.act = true;
}
static void P6JobEnd() {
    if (!t_p6.act) return;
    t_p6.act = false;
    Ts3nCounters& k = ts3native_counters;
    const P6Job& p = t_p6;
    const LONG tot = P6Us(P6Now() - p.t0);
    const LONG other = tot - p.queueUs - p.startUs - p.rpWaitUs - p.copyUs - p.cloneUs - p.gameUs;
    const LONG lazyN = k.rjpN[0][kP6LazyPhase] - p.lazyN0, lazyUs = k.rjpUs[0][kP6LazyPhase] - p.lazyUs0;
    const int s = p.out ? 1 : 0;
    InterlockedIncrement(&k.p6jN[s]);
    InterlockedExchangeAdd(&k.p6jTotUs[s], tot);
    InterlockedExchangeAdd(&k.p6jQueueUs[s], p.queueUs);
    InterlockedExchangeAdd(&k.p6jStartUs[s], p.startUs);
    InterlockedExchangeAdd(&k.p6jRpWaitUs[s], p.rpWaitUs);
    InterlockedExchangeAdd(&k.p6jCutAgeUs[s], p.cutAgeUs);
    InterlockedExchangeAdd(&k.p6jCopyUs[s], p.copyUs);
    InterlockedExchangeAdd(&k.p6jCloneUs[s], p.cloneUs);
    InterlockedExchangeAdd(&k.p6jGameUs[s], p.gameUs);
    InterlockedExchangeAdd(&k.p6jOtherUs[s], other);
    InterlockedExchangeAdd(&k.p6jDoorN[s], p.doorN);
    InterlockedExchangeAdd(&k.p6jDoorUs[s], p.doorUs);
    InterlockedExchangeAdd(&k.p6jLazyN[s], lazyN);
    InterlockedExchangeAdd(&k.p6jLazyUs[s], lazyUs);
    InterlockedExchangeAdd(&k.p6jPlans[s], p.plans);
    InterlockedIncrement(&k.p6jOut[p.out & 3]);
    if (!g_p6LogUs || tot < g_p6LogUs) return;
    static DWORD minute, lines;  // as JobLongNote: 20 lines a minute
    const DWORD now = GetTickCount();
    if (now - minute >= 60000) minute = now, lines = 0;
    if (lines++ >= 20) return;
    InterlockedIncrement(&k.p6jLines);
    static const char* const kOut[4] = {"taken", "stale-refused", "verify M2/M3", "game's plan"};
    Log("P6 job %ld us (%s): queue %ld, start %ld, rpwait %ld, copy %ld, clone %ld, game %ld, other %ld; cut age %ld; door %ld "
        "asks %ld us; lazy builds %ld %ld us; plans %ld", tot, kOut[p.out & 3], p.queueUs, p.startUs, p.rpWaitUs, p.copyUs,
        p.cloneUs, p.gameUs, other, p.cutAgeUs, p.doorN, p.doorUs, lazyN, lazyUs, p.plans);
}

// [RouteSnap] Mode=2 (from ApplyP4, after P4 installed: the thunks, the door stub and the capture are P4's)
static void ApplyP5(const wchar_t* ini) {
    Ts3nCounters& k = ts3native_counters;
    g_p5Verify = Clamp(GetPrivateProfileIntW(L"RouteSnap", L"VerifyEvery", 10, ini), 0, 1000);
    g_p5VerLend = GetPrivateProfileIntW(L"RouteSnap", L"VerifyLend", 0, ini) ? 1 : 0;
    g_p5StormN = Clamp(GetPrivateProfileIntW(L"RouteSnap", L"StormRefusals", 128, ini), 0, 100000);
    g_p5StormPauseMs = Clamp(GetPrivateProfileIntW(L"RouteSnap", L"StormPauseMs", 30000, ini), 0, 3600000);
    // I-94-322: verify compares the game's plan, whose open-ground hub refs are stack garbage unless OptsInit zeroes them for
    // every call (Enabled=1); Enabled=0 / 2 leave door-pair M2s that are the game's, not the clone's
    if (g_p5Verify && GetPrivateProfileIntW(L"OptsInit", L"Enabled", 0, ini) != 1)
        Log("route snapshot P5: [OptsInit] Enabled is not 1: verify M2s at open-ground goals near lots can be I-94-322 (the game's stack)");
    // P6 (0.36.41, docs/94 §24): TakeKinds bit 1 = own Route.Plan jobs too (bits 2, 4 not built); ShareCuts defaults on with it
    const int tk = GetPrivateProfileIntW(L"RouteSnap", L"TakeKinds", 8, ini);
    g_p6LogUs = Clamp(GetPrivateProfileIntW(L"RouteSnap", L"P6LogMs", 20, ini), 0, 600000) * 1000;
    k.p6Early = GetPrivateProfileIntW(L"RouteSnap", L"P6EarlyCopy", 0, ini) ? 1 : 0;  // 0.36.56 (§24.14); runtime switch p6Early
    k.p6fbOn = GetPrivateProfileIntW(L"RouteSnap", L"TakeForeign", 0, ini) ? 1 : 0;  // docs/189 §4 phase 4; runtime switch p6fbOn
    g_p5TakeRfp = tk & 8;
    k.p6Plan = tk & 1;
    k.p6Share = GetPrivateProfileIntW(L"RouteSnap", L"ShareCuts", tk & 1, ini) ? 1 : 0;
    // docs/189 phase 2c (0.36.57): TakeKindsAll=1 (runtime switch p6All, --ab routesnapkinds) also takes the own-task A10 Replan
    // and PlanFromPoint jobs; they exist only with [RouteAsync] Kinds bits 1 / 2 (A10 off in the release config)
    k.p6All = GetPrivateProfileIntW(L"RouteSnap", L"TakeKindsAll", 0, ini) ? 1 : 0;
    if (tk & 6) Log("route snapshot P5: TakeKinds bits 2 and 4 are ignored: Replan and PlanFromPoint are taken with TakeKindsAll=1");
    const LONG take = P5TakeMask() & g_p4Kinds;
    if (!take) Log("route snapshot P5: TakeKinds & Kinds is 0: no answer is taken");
    if (k.p6All && (g_p4Kinds & 6) != 6) Log("route snapshot P5: TakeKindsAll=1 but Kinds lacks bit 2 or 4: those kinds are not taken");
    if (k.p6All && (k.arReady & 3) != 3) Log("route snapshot P5: TakeKindsAll=1 but [RouteAsync] Kinds hooks no Replan or PlanFromPoint (bits 1 / 2): no such job starts");
    if ((take & 8) && !(k.arReady & 4)) Log("route snapshot P5: [RouteAsync] Kinds has no ReplanFromPosition (4): no Mode=2 job of it will start");
    if ((take & 1) && !k.p6Share) Log("route snapshot P5: Route.Plan taken without ShareCuts: a START behind a busy job gets no cut (docs/94 §22.30.4 G2)");
    k.m2Take = 1;
    k.m2Ready = 1;
    g_p5On = 1;
    Log("OK   route snapshot P5 (Mode=2): %s jobs take the clone answer; ShareCuts=%ld; VerifyEvery=%ld; door asks proxied to the "
        "sim thread; fallback = the game's own plan%s%s", take == 9 ? "Route.Plan (own) and ReplanFromPosition" : take & 1 ? "Route.Plan (own)" :
        "ReplanFromPosition", k.p6Share, g_p5Verify, g_p5VerLend ? "; VerifyLend=1 (verify jobs lend +0x918 too)" : "",
        k.p6Early ? "; P6EarlyCopy=1 (own-only copies before the job's +0x918)" : "");
    if (k.p6All) Log("route snapshot P5: TakeKindsAll=1: own Replan and PlanFromPoint jobs take the clone answer too (docs/189 phase 2c)");
    if (k.p6fbOn) Log("route snapshot P5: TakeForeign=1: FallbackAsync foreign Route.Plan jobs take the clone answer too (never verify; "
                      "FbJobCapMs kept in the clone run)%s", take & 1 ? "" : " - inactive: TakeKinds has no Route.Plan bit 1");
#ifdef TS3N_FAULTINJ
    g_fiMask = GetPrivateProfileIntW(L"RouteSnap", L"FaultInject", 0, ini) & FI_LIVE;
    g_fiEvery = Clamp(GetPrivateProfileIntW(L"RouteSnap", L"FaultInjectEvery", 10, ini), 1, 100000);
    Log("route snapshot P5: FAULTINJ build (docs/157): FaultInject=0x%lX every %ld armed plans", g_fiMask, g_fiEvery);
#endif
    ApplyP5b(ini);
    ApplySl(ini);
    ApplyLu(ini);
}
