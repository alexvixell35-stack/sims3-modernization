// P5c ([RouteSnap] FreeLock=1, p5c.cpp; docs/94 §22.11): selftest. Included by selftest.cpp after p5b-test.h and run from
// TestP5b inside its fake world: +0x918 a real CRITICAL_SECTION with the game's count at +0x20, the route worker P5bWorker
// running each ReplanFromPosition job as ServeAsync does (hold, A_RUNNING, PlanLockedHook -> P4LivePlan -> P5Plan, the IC's
// tail under the hold, which records g_spWrites in g_p5bTailW). The fake clone run answers the START cut's world (its node
// y = g_spWrites at the cut), the game's own plan answers the live world (y = g_spWrites when it runs), so an answer that is
// stale at the job's tail is visible. While each job runs, the sim (this thread) makes, mixed at random: steering calls,
// gate-12 reads, gate-0 hits and misses, gate waits P5c keeps out (site 10 = a sync plan's goal step, site 3 = lazy lot
// build), writers (a plain acquire; 1 in 16 is a recorded routing write: g_spWrites + 1 under the lock), saves (D2, then
// RtWaitJob), sync plans under a lent lock (RunPlan), and one destroy of the object the IC frame holds (a page with a
// refcount; the last Release frees it, so a use after free faults the selftest): P5cPark, else D2 and RtWaitJob, then the
// game's destroy (out of the fake id hash, its reference dropped) and the flush at the destroy entry.
// Jobs 0-29 keep the hold, 30-149 lend as P5b with the gates, 150-389 with FreeLock; from job 380 on, the first job whose
// window the sim enters hands the lent lock back (a save with the lent lock held: the tripwire, P5c off after it), and the
// jobs after it run as P5b.
static DWORD* volatile g_p5cObj;  // the frame's object while it is in the fake id hash ([0] canary, [1] refcount)
static const DWORD kP5cObjId = 0x00C0FFEE;
static volatile LONG g_p5cCutW;   // g_spWrites at the START cut: the world the clone run answers
static DWORD g_p5cSimTid;
static volatile LONG g_p5cSimPlans, g_p5cFreed, g_p5cBad;

static volatile LONG g_p5cBadAt[16];
static void P5cBadAt(int i) { InterlockedIncrement(&g_p5cBad); InterlockedIncrement(&g_p5cBadAt[i]); }
static void* __cdecl P5cSvc() { return (void*)(uintptr_t)1; }
static u8 __fastcall P5cFindRef(void*, void*, DWORD lo, DWORD, void** out) {  // 0x9395A0's convention
    DWORD* o = g_p5cObj;
    if (lo != kP5cObjId || !o) return 0;
    InterlockedIncrement((LONG*)&o[1]);
    *out = o;
    return 1;
}
static void __fastcall P5cRelease(void** pp, void*) {  // 0x7460F0: the last reference frees the object
    DWORD* o = (DWORD*)*pp;
    if (o[0] != 0x0B1EC7) P5cBadAt(1);
    if (InterlockedDecrement((LONG*)&o[1]) == 0) {
        if (g_p5bBody && g_p5bFrame == o) P5cBadAt(2);  // freed under the suspended frame
        VirtualFree(o, 0, MEM_RELEASE);
        InterlockedIncrement(&g_p5cFreed);
    }
}
static u8 __fastcall P5cFakePlan(void* world, void*, int* outList, int*, int*, DWORD, DWORD, u8*, int* out1, int* out2, DWORD,
                                 DWORD) {
    float v;
    if (t_snapRun) {  // the clone run: the cut's world, without the lock while it is lent
        if (g_p5bWin && P5bMine()) P5cBadAt(3);
        P5bSpin(g_p5bSpinUs);
        const DWORD door = ((P4DoorFn)P4DoorStub)(3, 0, 0, 0, 0, 0, 0, 0, 2);
        assert(door == 32 && t_snapRun);
        P5bSpin(g_p5bSpinUs);
        v = (float)g_p5cCutW;
    } else if (GetCurrentThreadId() == g_p5cSimTid) {  // a sync plan the sim makes holding the lent lock (RunPlan, no job wait)
        if (!P5bMine() || world != g_p5bW) P5cBadAt(4);
        InterlockedIncrement(&g_p5cSimPlans);
        v = -1.0f;
    } else {  // the game's own plan (fallback): the worker, under the job's hold, on the live world now
        if (!P5bMine() || g_p5bWin || world != g_p5bW) P5cBadAt(5);
        InterlockedIncrement(&g_p5bLivePlans);
        v = (float)g_spWrites;
    }
    P4AddPath(outList, 2, 3, v);
    *out1 = 0;
    *out2 = 1;
    return 1;
}
static DWORD* P5cNewObj() {
    DWORD* o = (DWORD*)VirtualAlloc(NULL, 4096, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    o[0] = 0x0B1EC7, o[1] = 1;  // the id hash's reference
    g_p5cObj = o;
    return o;
}
static void P5cGameDestroy(DWORD* o) {  // DestroyById's own part: out of the hash, the hash's reference dropped
    g_p5cObj = NULL;
    void* p = o;
    P5cRelease(&p, NULL);
}

static void TestP5cIn(void* lock, const DWORD* hitKey, const DWORD* missKey, int* list) {
    Ts3nCounters& k = ts3native_counters;
    static_assert(offsetof(Ts3nCounters, p5cReady) == 0x3078 && offsetof(Ts3nCounters, p5cFree) == 0x307C &&
                  offsetof(Ts3nCounters, p5cAcq) == 0x3084 && offsetof(Ts3nCounters, p5cStale) == 0x3088 &&
                  offsetof(Ts3nCounters, p5cParked) == 0x3090 && offsetof(Ts3nCounters, p5cKeptWait) == 0x3094 &&
                  offsetof(Ts3nCounters, p5cFlushed) == 0x30A4 && offsetof(Ts3nCounters, p5cCountLow) == 0x30B4 &&
                  offsetof(Ts3nCounters, p5cOff) == 0x30C0 && sizeof(Ts3nCounters) == 5933 * 4,
                  "counters v65 layout (docs/registry.md)");
    const DWORD me = GetCurrentThreadId();
    g_p5cSimTid = me;
    SvcFn const svcSave = g_objSvc;
    FindRefFn const findSave = g_findRef;
    ReleaseFn const relSave = g_objRelease;
    const PlanLockedFn planSave = g_planOrig;
    g_objSvc = P5cSvc, g_findRef = P5cFindRef, g_objRelease = P5cRelease;
    g_planOrig = P5cFakePlan;

    // 1. park and flush rules on this thread (the owner), a fake window open
    {
        DWORD* o = P5cNewObj();
        assert(!P5cPark(kP5cObjId, 0));  // no P5c window: today's path
        g_p5cJob = 1, g_p5bWin = 1;
        g_async.state = A_RUNNING;
        const LONG k0[4] = {k.p5cKeptWait[0], k.p5cKeptWait[1], k.p5cKeptWait[2], k.p5cKeptWait[3]};
        g_inInvoke = 1;
        assert(!P5cPark(kP5cObjId, 0) && k.p5cKeptWait[2] == k0[2] + 1);  // inside a job callback
        g_inInvoke = 0;
        DiscRec& r = g_disc[g_discHead % kDiscCap];
        const int discN = g_discCount;
        assert(discN == 0);
        r.idLo = kP5cObjId, r.idHi = 0, r.lock = g_rgLock, r.pts = NULL;
        g_discCount = 1;
        assert(!P5cPark(kP5cObjId, 0) && k.p5cKeptWait[0] == k0[0] + 1);  // D-X record: it applies under the job wait
        g_discCount = 0;
        g_p5cGyN = kP5cGrave;
        assert(!P5cPark(kP5cObjId, 0) && k.p5cKeptWait[1] == k0[1] + 1);  // full
        g_p5cGyN = 0;
        assert(!P5cPark(0x123, 0) && k.p5cKeptWait[3] == k0[3] + 1);      // not in the hash
        const LONG parked0 = k.p5cParked, fl0 = k.p5cFlushed, ref0 = k.p5cFlushRefused, freed0 = g_p5cFreed;
        assert(P5cPark(kP5cObjId, 0) && o[1] == 2 && g_p5cGyN == 1 && k.p5cParked == parked0 + 1);
        P5cGameDestroy(o);  // the destroy runs now: the parked reference keeps the block
        assert(o[1] == 1 && g_p5cFreed == freed0 && o[0] == 0x0B1EC7);
        P5cFlush();  // a job runs: refused, kept
        assert(g_p5cGyN == 1 && k.p5cFlushRefused == ref0 + 1 && g_rgOffSimOpen == 0);
        g_async.state = A_IDLE;
        P5cFlush();  // no job: released, freed
        assert(g_p5cGyN == 0 && k.p5cFlushed == fl0 + 1 && g_p5cFreed == freed0 + 1 && g_rgOffSimOpen == 0);
        // T-G1: a parked object whose count is already 0 is not released again
        o = P5cNewObj();
        g_async.state = A_RUNNING;
        assert(P5cPark(kP5cObjId, 0));
        const LONG low0 = k.p5cCountLow;
        o[1] = 0;
        g_async.state = A_IDLE;
        P5cFlush();
        assert(k.p5cCountLow == low0 + 1 && g_p5cFreed == freed0 + 1);
        VirtualFree(o, 0, MEM_RELEASE);
        g_p5cObj = NULL;
        g_p5cJob = 0, g_p5bWin = 0;
        assert(!P5cLent(me) && !P5cPlanHere() && P5cHandBegin() == 0);
    }

    // 2. threaded: held, P5b (steering + gate reads), P5c (FreeLock)
    g_p5cOn = 1, k.p5cReady = 1, g_p5bOn = 1;
    g_p5bGo = CreateEventW(NULL, FALSE, FALSE, NULL);
    g_p5bTStop = 0;
    g_p5bVariant = 0;
    HANDLE work = CreateThread(NULL, 0, P5bWorker, NULL, 0, NULL);
    enum { C_STEER, C_G12R, C_G0H, C_G0M, C_GX, C_HK, C_WR, C_PLAN, C_SAVE, C_DES, C_N };
    static const char* const kCls[C_N] = {"steering", "gate-12 reads", "gate-0 hits", "gate-0 misses", "gates kept (F-G, 10)",
                                          "gates re-keyed (3, 4)", "writers", "sync plan acquires", "saves",
                                          "destroys of the frame's object"};
    // docs/189 phase 1 (HoldKeyed): the retake tripwire on its own first (an admitted frame counted open at a retake)
    {
        const LONG trip0 = k.hgTrip;
        g_hgOpen = 1, k.hgReady = 1, k.p5cOff = 1;
        HgRetakeCheck();  // a hand-over turned P5c off first: not a trip
        assert(k.hgTrip == trip0 && k.hgReady == 1);
        k.p5cOff = 0;
        HgRetakeCheck();
        assert(k.hgTrip == trip0 + 1 && k.hgReady == 0);
        g_hgOpen = 0, k.hgTrip = trip0, k.hgReady = 1;
    }
    // per phase: started during a job body; got the lock (or, destroys and saves, finished) inside the job; wait time
    LONG all[4][C_N] = {}, inJ[4][C_N] = {}, jobs[4] = {}, skew[4] = {}, taken[4] = {}, recW[4] = {}, hkW = 0;
    LONGLONG waitUs[4][C_N] = {}, bodyUs[4] = {}, lentUs[4] = {};
    const LONG hgJobs0 = k.hgJobs, hgStale0 = k.hgStaleAfter, hgTrip0 = k.hgTrip, hgA3 = k.hgAdmit[3], hgA4 = k.hgAdmit[4];
    const LONG hgA10 = k.hgAdmit[10], hgLong0 = k.hgLongHold;
    // 0.35.11: the hand-over needs a lent window seen while the body runs; under CPU load none came in jobs 380-389 (hand at
    // -1, hand-overs +0: the old line-316 failure, no P5c fault), so jobs go on until it happens and one job after it ran
    const int kN = 390, kHand = 380, kMax = 1000;
    g_rfSeed = 6161;
    const LONG simPlans0 = g_p5cSimPlans, hand0 = k.p5cHandOver, jobsP5c0 = k.p5cJobs, acq0 = k.p5cAcq, park0 = k.p5cParked;
    const LONG st0 = k.p5cStale[0], st1 = k.p5cStale[1], fbNr0 = k.m2FbNr, here0 = k.p5cPlanHere;
    const LONG low0 = k.p5cCountLow, live0 = g_p5bLivePlans, early0 = g_p5EarlyStale;
    LONG jobsAfterHand = -1;
    int handAt = -1;
    for (int i = 0; i < kMax && (i < kN || handAt < 0 || i <= handAt + 1); i++) {
        const int ph = i < 30 ? 0 : i < 150 ? 1 : i < 270 ? 2 : 3;  // 3: FreeLock + HoldKeyed (docs/189 phase 1; 1 and 2 alternate, §10)
        k.p5bRelease = ph > 0, k.p5bGates = ph > 0, k.p5cFree = ph >= 2, k.hgOn = ph == 3 ? 1 + (i & 1) : 0;
        const bool freeLock = ph >= 2 && !k.p5cOff;  // this job admits writers
        DWORD* fo = P5cNewObj();
        g_p5bFrame = fo;
        AsyncJob& j = g_jobs[0];
        memset(&j, 0, sizeof(j));
        j.kind = 3, j.seq = 7000 + i, j.handle = 0x25000700 + i, j.lock = lock, j.x87 = GetX87(), j.mxcsr = _mm_getcsr();
        P4OnStart(&j);  // P5OnStart: armed to take; P5cOnStart: the cut's write count
        assert(j.p4 == 2);
        g_p5cCutW = g_spWrites;
        const LONGLONG b0 = g_p5bBodyUs;
        const LONG w0 = k.p5bWinUs, tk0 = k.m2Taken, jc0 = k.p5cJobs;
        InterlockedExchange(&j.state, J_QUEUED);
        SetEvent(g_p5bGo);
        bool destroyed = false, handDone = false;
        const ULONGLONG t0 = GetTickCount64();
        while (j.state != J_DONE) {
            if (GetTickCount64() - t0 > 5000) {
                printf("P5c threaded HANG at job %d: job state %ld, async %ld, window %ld, P5c %ld, waiters %ld, lock owner %ld (sim %lu)\n",
                       i, j.state, g_async.state, g_p5bWin, g_p5cJob, g_p5bIn, P5bOwner(lock), me);
                assert(false);
            }
            if (i >= kHand && handAt < 0 && !handDone && g_p5bWin && g_p5bBody) {  // the tripwire: a wait for the job holding the lent lock
                if (LockAcquireHook(lock, NULL, &kP5bInf) > 0 && g_p5bBody && g_p5bWin && P5bMine()) {
                    static LONG us;
                    RtWaitJob(&us, 0x02000000 | RT_SAVE);  // handed back, the whole job waited for, taken again
                    assert(P5bMine() && !g_p5bBody && k.p5cHandOver == hand0 + 1 && k.p5cOff == 1);
                    handDone = true;
                    handAt = i;
                }
                LockReleaseHook(lock, NULL);
                continue;
            }
            const DWORD act = RfRnd(40);
            const bool started = g_p5bBody != 0;
            LARGE_INTEGER s0;
            QueryPerformanceCounter(&s0);
            int c = -1;
            bool got = false;
            if (act >= 32) {  // a destroy of the frame's object (once per job, inside its body)
                if (destroyed || !started) { Sleep(0); continue; }
                c = C_DES;
                const bool parked = P5cPark(kP5cObjId, 0);  // RtEnter: before D2 and the job wait
                if (!parked) {
                    P5cNrEnter();
                    InterlockedDecrement(&g_snNrIn);  // the frame's leave (RG_REL_NR)
                }
                InterlockedIncrement(&g_rgOffSimOpen);  // RT_DESTROY: no job starts during the destroy (RG_REL_OFFSIM)
                if (!parked) {
                    static LONG us;
                    RtWaitJob(&us, 0x02000000 | RT_DESTROY);
                    if (g_p5bBody) P5cBadAt(6);
                }
                P5cGameDestroy(fo);
                destroyed = true;
                got = g_p5bBody != 0;  // finished inside the job
                P5cFlush();            // RtEnter's end: refused while the job runs
                RgOffSimDrop();
            } else if (act == 30) {    // a save: D2, then the job wait (closure #6)
                c = C_SAVE;
                P5cNrEnter();
                InterlockedDecrement(&g_snNrIn);
                InterlockedIncrement(&g_rgOffSimOpen);  // #6: queued jobs held until the call returns (RG_REL_OFFSIM)
                static LONG us;
                RtWaitJob(&us, 0x02000000 | RT_SAVE);
                got = g_p5bBody != 0;
                if (got) P5cBadAt(7);
                P5cFlush();
                RgOffSimDrop();
            } else {
                LONG r;
                if (act < 10) {
                    c = C_STEER;
                    t_steerRet = 0x0085FE10;
                    r = LockAcquireHook(lock, NULL, &kP5bInf);
                } else if (act < 14) {
                    static const DWORD kKey[3] = {0x0062F782, 0x00631814, 0x00631F40};
                    const int f = (int)(act % 3);
                    c = f == 2 ? C_GX : C_G12R;  // F-G is not admitted (docs/155 §8): a gate that waits, as C_GX
                    r = P5bGateWait(12, kKey[f], f ? 0 : 0x0076F15D, hitKey);
                } else if (act < 17) {
                    c = C_G0H;
                    r = P5bGateWait(0, 0x0062EF7A, 0, hitKey);
                } else if (act == 17) {
                    c = C_G0M;
                    r = P5bGateWait(0, 0x0062EF7A, 0, missKey);
                } else if (act < 20) {  // the lot rebuild gate (site 4), the lazy lot build gate (site 3): re-keyed
                    c = C_HK;
                    r = act & 1 ? P5bGateWait(4, 0x0082B8A0, 0, hitKey) : P5bGateWait(3, 0x0063F5A1, 0, hitKey);
                } else if ((act == 20 || act == 21) && (k.hgOn >= 2 || RfRnd(4) == 0)) {  // a sync plan's goal-step gate (site 10): re-keyed with HoldKeyed=2 only (§10)
                    c = k.hgOn >= 2 ? C_HK : C_GX;
                    r = P5bGateWait(10, 0x00760BFF, 0, hitKey);
                } else if (act < 30) {
                    c = C_WR;
                    r = LockAcquireHook(lock, NULL, &kP5bInf);
                } else {  // act 31: a plain acquire, then a sync plan inside it
                    c = C_PLAN;
                    r = LockAcquireHook(lock, NULL, &kP5bInf);
                }
                assert(r > 0 && P5bMine());
                got = g_p5bBody != 0;
                // only a lent lock can be ours during a job, and only for a class its phase admits
                const bool may = c == C_STEER || c == C_G12R || c == C_G0H ? ph > 0
                                 : c == C_WR || c == C_PLAN ? freeLock : (c == C_HK || c == C_G0M) && ph == 3 && freeLock;
                if (got && (!g_p5bWin || !may)) P5cBadAt(8);
                if (c == C_STEER) {
                    assert(LockAcquireHook(lock, NULL, &kP5bInf) == r + 1);
                    LockReleaseHook(lock, NULL);
                }
                if (c == C_WR) {
                    g_p5bLive++;
                    // a recorded routing write; 0.35.26: one before the window now skips the job's window (P5cStaleEarly), so
                    // those are 4x rarer, to keep enough P5c windows for the in-window checks below
                    if (RfRnd(16) == 0 && (got || RfRnd(4) == 0)) InterlockedIncrement(&g_spWrites), recW[ph]++;
                }
                // an admitted re-keyed gate (lot rebuild, lazy build) that writes a world input: a recorded write in the window,
                // which P5cStale must refuse (the skew check below)
                if (c == C_HK && got && g_p5bWin && RfRnd(4) == 0) InterlockedIncrement(&g_spWrites), recW[ph]++, hkW++;
                if (c == C_PLAN && got) {  // RunPlan on the owner, job running, lent lock held: plans here, no job wait
                    int l2[4] = {0, 0, 0, 0}, o1 = -1, o2 = -1;
                    l2[0] = l2[1] = (int)(uintptr_t)l2;
                    const u8 pr = RunPlan(g_p5bW, NULL, l2, g_p5bArgs[1], g_p5bArgs[2], 0xA6E, 1, (u8*)g_p5bArgs[3], &o1, &o2, 5, 0);
                    assert(pr == 1 && P5bMine());
                    P4FakeListFree(l2, NULL);
                }
                P5bSpin(c == C_STEER || c == C_WR ? 100 : 20);
                if (c == C_WR) g_p5bLive--;
                LockReleaseHook(lock, NULL);
                t_steerRet = 0;
            }
            if (started) {
                all[ph][c]++;
                waitUs[ph][c] += UsSince(s0);
                if (got) inJ[ph][c]++;
            }
            Sleep(0);
        }
        if (!destroyed) P5cGameDestroy(fo);
        P5cFlush();  // the next START / READY poll: no job runs
        assert(g_p5cGyN == 0 && g_p5cObj == NULL);
        jobs[ph]++;
        bodyUs[ph] += g_p5bBodyUs - b0;
        lentUs[ph] += k.p5bWinUs - w0;
        if (k.m2Taken != tk0) taken[ph]++;
        if (handAt >= 0 && i == handAt + 1) jobsAfterHand = k.p5cJobs - jc0;
        // the answer's world: never older than the live world at the IC's tail with FreeLock (P5 accepts the START skew)
        static P4Ans a;
        assert(g_p5bR == 1 && g_p5bO2 == 1 && P4CaptureAnswerRaw(list, g_p5bR, 0, g_p5bO2, &a) && a.nPaths == 1);
        const LONG ansW = (LONG)a.n[a.p[0].n0 + 1].y;
        if (ansW != g_p5bTailW) {  // a P5c window opened in this job: the stale check must have refused it
            skew[ph]++;
            if (k.p5cJobs != jc0) P5cBadAt(9);
        }
        P4FakeListFree(list, NULL);
        InterlockedExchange(&j.state, J_FREE);
    }
    g_p5bTStop = 1;
    SetEvent(g_p5bGo);
    WaitForSingleObject(work, 5000);
    CloseHandle(work), CloseHandle(g_p5bGo);
    k.p5bGates = 0, k.p5cFree = 0, k.p5cOff = 0;
    g_p5cOn = 0, k.p5cReady = 0;
    g_objSvc = svcSave, g_findRef = findSave, g_objRelease = relSave;
    g_planOrig = planSave;

    const LONG stale0 = k.p5cStale[0] - st0, stale1 = k.p5cStale[1] - st1, p5cJobs = k.p5cJobs - jobsP5c0;
    if (g_p5cBad || g_p5bBad || g_p5bWin || g_p5cJob || g_p5bIn || g_p5cGyN || g_rgOffSimOpen) {
        printf("P5c FAIL: bad %ld (at", g_p5cBad);
        for (int b = 1; b < 16; b++) if (g_p5cBadAt[b]) printf(" %d:%ld", b, g_p5cBadAt[b]);
        printf("), worker bad %ld, win %ld, job %ld, in %ld, grave %ld, offSim %ld\n", g_p5bBad, g_p5bWin, g_p5cJob, g_p5bIn, g_p5cGyN, g_rgOffSimOpen);
    }
    assert(g_p5cBad == 0 && g_p5bBad == 0 && !g_p5bWin && !g_p5cJob && g_p5bIn == 0 && g_p5cGyN == 0 && g_rgOffSimOpen == 0);
    if (k.p5cCountLow != low0 || k.p5cHandOver != hand0 + 1 || handAt < kHand || jobsAfterHand != 0)
        printf("P5c FAIL: count low +%ld, hand-overs +%ld, hand at %d, P5c jobs after it %ld\n", k.p5cCountLow - low0,
               k.p5cHandOver - hand0, handAt, jobsAfterHand);
    assert(k.p5cCountLow == low0 && k.p5cHandOver == hand0 + 1 && handAt >= kHand && jobsAfterHand == 0);
    // held and P5b: no writer, kept gate, miss, save or destroy inside a job; P5c: writers, sync plans and destroys inside too
    for (int p = 0; p < 4; p++) assert(inJ[p][C_GX] == 0 && inJ[p][C_SAVE] == 0);
    for (int p = 0; p < 3; p++) assert(inJ[p][C_G0M] == 0 && inJ[p][C_HK] == 0);
    // HoldKeyed: re-keyed gates got the lent lock inside jobs, their writes were refused (skew check), no frame open at a retake
    if (inJ[3][C_HK] == 0 || k.hgTrip != hgTrip0 || g_hgOpen || k.hgJobs == hgJobs0 || k.hgAdmit[3] + k.hgAdmit[4] == hgA3 + hgA4)
        printf("P5c HoldKeyed FAIL: re-keyed in job %ld, trips +%ld, open %ld, hg jobs +%ld, admits +%ld\n", inJ[3][C_HK],
               k.hgTrip - hgTrip0, g_hgOpen, k.hgJobs - hgJobs0, k.hgAdmit[3] + k.hgAdmit[4] - hgA3 - hgA4);
    assert(k.hgTrip == hgTrip0 && g_hgOpen == 0 && !g_hgJob && k.hgStaleAfter >= hgStale0);
    TIMING_ASSERT("P5c HoldKeyed gates admitted inside jobs",
                  inJ[3][C_HK] > 0 && k.hgJobs > hgJobs0 && k.hgAdmit[3] + k.hgAdmit[4] > hgA3 + hgA4);
    k.hgOn = 0, k.hgReady = 0;
    for (int c = 0; c < C_N; c++) assert(inJ[0][c] == 0);
    assert(inJ[1][C_WR] == 0 && inJ[1][C_PLAN] == 0 && inJ[1][C_DES] == 0);
    TIMING_ASSERT("P5c sim calls got the lock inside job bodies",
                  inJ[1][C_STEER] > 0 && inJ[2][C_WR] > 0 && inJ[2][C_DES] > 0 && inJ[2][C_STEER] > 0 && inJ[2][C_G12R] > 0);
    assert(g_p5cSimPlans > simPlans0 && k.p5cPlanHere > here0 && k.p5cAcq > acq0 && k.p5cParked > park0);
    // every P5c answer matched the live world at its tail: taken (no write since the cut) or refused (the game's plan)
    assert(taken[2] > 0 && stale0 + stale1 > 0 && p5cJobs > 0);
    auto mean = [&](int p, int c) { return all[p][c] ? waitUs[p][c] / 1000.0 / all[p][c] : 0.0; };
    auto perJob = [&](int p) {
        LONGLONG us = 0;
        for (int c = 0; c < C_N; c++) us += waitUs[p][c];
        return us / 1000.0 / jobs[p];
    };
    auto share = [&](int p) {  // sim acquires started in a job body that got the lock inside it (gates kept out counted too)
        LONG a = 0, in = 0;
        for (int c = C_STEER; c <= C_PLAN; c++) a += all[p][c], in += inJ[p][c];
        return a ? 100.0 * in / a : 0.0;
    };
    TIMING_ASSERT("P5c FreeLock share and mean waits",
                  share(2) > share(1) && mean(2, C_WR) < mean(1, C_WR) && mean(2, C_DES) < mean(1, C_DES));
    printf("P5c HoldKeyed (docs/189 phase 1): %ld jobs, re-keyed gates (3, 4) in a job %ld/%ld (mean wait %.2f ms, FreeLock "
           "alone %.2f), gate-0 misses %ld/%ld, kept (F-G, 10) 0/%ld; %ld gate writes in windows, %ld windows with an admitted "
           "gate (%ld with a write: refused), retake tripwire 0\n", jobs[3], inJ[3][C_HK], all[3][C_HK],
           all[3][C_HK] ? waitUs[3][C_HK] / 1000.0 / all[3][C_HK] : 0.0, all[2][C_HK] ? waitUs[2][C_HK] / 1000.0 / all[2][C_HK] : 0.0,
           inJ[3][C_G0M], all[3][C_G0M], all[3][C_GX], hkW, k.hgJobs - hgJobs0, k.hgStaleAfter - hgStale0);
    // HoldKeyed=2 (§10): site 10 got in on the odd jobs (hgOn 2); with hgOn 1 it was a waiting gate (C_GX above: never inside)
    printf("P5c HoldKeyed=2 (docs/189 §10): site 10 admitted %ld times in hgOn=2 jobs, long holds +%ld\n", k.hgAdmit[10] - hgA10,
           k.hgLongHold - hgLong0);
    assert(k.hgLongHold == hgLong0);
    TIMING_ASSERT("P5c HoldKeyed=2 site 10 admitted", k.hgAdmit[10] > hgA10);
    printf("P5c (FreeLock=1): counters v65; park rules (callback, D-X record, full, not found), flush refused in a job and "
           "released after it, T-G1 count low kept; threaded (sim + worker, real +0x918), %ld held / %ld P5b+gates / %ld FreeLock "
           "jobs (lent %.1f%% / %.1f%% of job time), sim calls started during a job body, got the lock (destroys, saves: done) "
           "inside the job / all (mean wait ms, held -> P5b -> P5c):\n",
           jobs[0], jobs[1], jobs[2], bodyUs[1] ? 100.0 * lentUs[1] / bodyUs[1] : 0.0, bodyUs[2] ? 100.0 * lentUs[2] / bodyUs[2] : 0.0);
    for (int c = 0; c < C_N; c++)
        printf("  %-30s %ld/%ld (%.2f)  %ld/%ld (%.2f)  %ld/%ld (%.2f)\n", kCls[c], inJ[0][c], all[0][c], mean(0, c), inJ[1][c],
               all[1][c], mean(1, c), inJ[2][c], all[2][c], mean(2, c));
    printf("  acquires that no longer wait for the job: held %.0f%%, P5b %.0f%%, P5c %.0f%%; sim wait per job during job bodies "
           "%.2f / %.2f / %.2f ms; P5c: %ld windows, %ld taken, refused stale %ld (before the window %ld, of them %ld skipped the "
           "acquire and run (0.35.26), inside %ld; "
           "%ld recorded writes in the phase), nr fallbacks %ld (destroys), live plans %ld; P5 skew (answer older than the tail) "
           "held %ld, P5b %ld, P5c %ld; %ld destroys parked, %ld sync plans on the sim, 1 hand-over then P5c off; no use after "
           "free, no hang\n",
           share(0), share(1), share(2), perJob(0), perJob(1), perJob(2), p5cJobs, taken[2], stale0 + stale1, stale0,
           g_p5EarlyStale - early0, stale1, recW[2],
           k.m2FbNr - fbNr0, g_p5bLivePlans - live0, skew[0], skew[1], skew[2], k.p5cParked - park0, g_p5cSimPlans - simPlans0);
}
