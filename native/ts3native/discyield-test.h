// docs/640 [RouteAsync] DiscYieldForeign, run from TestFallback (its worker, fake IC and lock) right after the RemovalFirst block.
// A "running foreign job" is faked by the slot alone (g_async.state A_RUNNING, g_jobOwn 0; the worker stays idle); a forced drain
// then waits until F2JobEnd ends it. The queued record has no world (lock NULL), so a drain drops it without touching game code.
// Included by selftest.cpp.
static void DyRecord(DWORD ageMs) {
    g_discHead = 0;
    g_disc[0] = DiscRec{};
    g_disc[0].usf = true;
    g_disc[0].t = GetTickCount() - ageMs;
    g_discCount = 1;
}
static void DyFakeJob(DWORD handle) {
    g_async.state = A_RUNNING;
    g_async.handle = handle;
    g_jobOwn = 0;
}
static void DyEndJobSoon() { CloseHandle(CreateThread(NULL, 0, F2JobEnd, NULL, 0, NULL)); }

static void TestDiscYield(u8* out) {
    Ts3nCounters& k = ts3native_counters;
    u8* p = *(u8**)((u8*)g_rgLock - 0x918 + 0x95C);
    const u8 savedPend = p[0x10E];
    const LockAcquireFn savedAcq = g_fbAcq;
    const LONG savedRetry = k.fbRetryMode;
    // TestFallback's later asserts count the M1Retry parks absolutely: this test's parks are given back at the end
    const LONG savedParked = k.fbRetryParked, savedQueued = k.fbRetryQueued, savedGaveUp = k.fbRetryGaveUp, savedTicks = k.fbRetryTicksMax;
    p[0x10E] = 0;
    g_fbAcq = FakeLockAcquire;
    k.fbRetryMode = 0;  // as shipped (M1Retry=0): a park by this switch must not depend on it
    // the fake +0x918 is a CRITICAL_SECTION with no game recursion count at +0x20, so LockReleaseHook would never see the last level
    // and rgOwnerTid would stay on this thread after a drain (M1 refuses with 5); the game's lock keeps that count
    LONG* const lockLevel = (LONG*)((u8*)g_rgLock + 0x20);
    const LONG savedLevel = *lockLevel;
    *lockLevel = 1;
    // no route manager: AsyncLosRoute reads none (the game slot's address may hold heap bytes in this process)
    static const u8* const kNoRm = NULL;
    const u8* const* savedRm = g_asRmSlot;
    g_asRmSlot = &kNoRm;
    const DWORD kJob = 0x25ABCDEF;  // the foreign job "running" on the worker

    // 1. the decision: off; on with a young record, an own job, no job; household / unknown scope, nested call, age ceiling
    DyRecord(300);
    DyFakeJob(kJob);
    g_dyMode = 0;
    assert(!DiscYieldOk(true, DS_START));
    g_dyMode = 1;
    DyRecord(10);
    assert(!DiscYieldOk(true, DS_START));  // DiscPoll would not wait either
    DyRecord(300);
    g_jobOwn = 1;
    assert(!DiscYieldOk(true, DS_START));  // an own job: out of reach
    g_jobOwn = 0;
    g_async.state = A_IDLE;
    assert(!DiscYieldOk(true, DS_START));  // nothing to wait for
    g_async.state = A_RUNNING;
    assert(!DiscYieldOk(false, DS_START) && g_dyKept[0] == 1);  // household, selected or unknown scope: the wait stays
    FakeTask(kNested, 7);
    assert(!DiscYieldOk(true, DS_START) && g_dyKept[1] == 1);  // a nested call cannot sleep
    FakeTask(kRoot, 7);
    DyRecord(kDyMaxAgeMs + 50);
    assert(!DiscYieldOk(true, DS_START) && g_dyKept[2] == 1);  // the hard age ceiling
    DyRecord(300);
    assert(DiscYieldOk(true, DS_START) && g_dyYield[DS_START] == 1);
    g_dyYield[DS_START] = 0;
    // docs/670 §27 DiscYieldOwn: an own job is yielded to only with its own switch, a foreign one only with DiscYieldForeign
    g_jobOwn = 1;
    assert(!DiscYieldOk(true, DS_START) && g_dyYield[DS_START] == 0 && g_dyOwnYield == 0);
    g_dyOwn = 1;
    assert(DiscYieldOk(true, DS_START) && g_dyYield[DS_START] == 1 && g_dyOwnYield == 1);
    assert(!DiscYieldOk(false, DS_START));  // the household rule is the same
    g_dyMode = 0;
    assert(DiscYieldOk(true, DS_START) && g_dyOwnYield == 2);  // independent of DiscYieldForeign
    g_jobOwn = 0;
    assert(!DiscYieldOk(true, DS_START) && g_dyOwnYield == 2);  // a foreign job: DiscYieldForeign's, off here
    DiscYieldQueuedNote();
    assert(g_dyReadyQueued == 0);  // a foreign job with only DiscYieldOwn on: not counted
    g_jobOwn = 1;
    DiscYieldQueuedNote();
    assert(g_dyReadyQueued == 1);
    g_jobOwn = 0;
    g_dyOwn = 0;
    g_dyMode = 1;
    g_dyYield[DS_START] = 0;
    g_dyOwnYield = g_dyReadyQueued = 0;
    g_dyKept[0] = 1;  // as before this block (one household refusal)

    // 2. READY's reach (DyReadyMay): the running job of the caller, a done one, a park of its own with ticks left; never a queued
    // job (the drain keeps the worker from claiming it), another park, or no job
    {
        AsyncJob f = {};
        f.handle = kJob;
        f.state = J_RUNNING;
        assert(DyReadyMay(&f));
        f.state = J_DONE;
        assert(DyReadyMay(&f));
        f.state = J_QUEUED;
        assert(!DyReadyMay(&f));
        f.state = J_PEND;
        assert(!DyReadyMay(&f));  // an M1 / RemovalFirst park
        f.dy = 1;
        assert(DyReadyMay(&f));
        f.pend = kDyTicks - 1;
        assert(!DyReadyMay(&f));  // the cap: this READY drains with the wait
        f.handle = kJob + 1, f.state = J_RUNNING, f.dy = 0;
        assert(!DyReadyMay(&f) && !DyReadyMay(NULL));  // running is another route's job
    }

    // 3. an unseen START parks instead of waiting (no job queued, no M1 test), READY answers "not yet" without a wait; once the job
    // ended, READY's DiscPoll applies the record without a wait and the START's job queues: the worker's result is collected
    const LONG q0 = k.asyncStarted, dsStart0 = k.dsN[DS_START], dsReady0 = k.dsN[DS_READY];
    KdScopeSet(kH1, 2);
    assert(Start(kH1) && JobFor(kH1) && JobFor(kH1)->state == J_PEND && JobFor(kH1)->dy == 1 && JobFor(kH1)->kdScope == 2);
    assert(k.asyncStarted == q0 && g_dyYield[DS_START] == 1 && !g_dyArm && !g_dyParked && g_discCount == 1);
    assert(!Ready(kH1) && JobFor(kH1)->state == J_PEND && JobFor(kH1)->pend == 1 && g_dyYield[DS_READY] == 1 && g_discCount == 1);
    assert(!Ready(kH1) && JobFor(kH1)->pend == 2 && g_dyYield[DS_READY] == 2);
    g_async.state = A_IDLE;
    assert(!Ready(kH1) && g_discCount == 0 && k.asyncStarted == q0 + 1 && JobFor(kH1)->dy == 0);
    assert(k.dsN[DS_START] == dsStart0 && k.dsN[DS_READY] == dsReady0 && g_dyTicksMax == 3 && g_dyCapped == 0);
    WaitReady(kH1);
    assert(IcPlanHook(out, kH1) == out && *(DWORD*)(out + 0x40) == g_workerTid);
    Cancel(kH1);

    // 4. the tick cap: the last READY takes the forced drain (waits for the job as before), then the job queues
    DyRecord(300);
    DyFakeJob(kJob);
    KdScopeSet(kH1, 2);
    assert(Start(kH1) && JobFor(kH1)->state == J_PEND && JobFor(kH1)->dy == 1);
    JobFor(kH1)->pend = kDyTicks - 1;
    DyEndJobSoon();
    assert(!Ready(kH1) && g_discCount == 0 && k.dsN[DS_READY] == dsReady0 + 1);
    assert(k.asyncStarted == q0 + 2 && g_dyTicksMax == kDyTicks);
    WaitReady(kH1);
    assert(IcPlanHook(out, kH1) == out && *(DWORD*)(out + 0x40) == g_workerTid);
    Cancel(kH1);

    // 5. household / unknown scope: START keeps the wait (forced drain at START), then queues at once
    DyRecord(300);
    DyFakeJob(kJob);
    KdScopeSet(kH1, 1);
    DyEndJobSoon();
    assert(Start(kH1) && g_discCount == 0 && k.dsN[DS_START] == dsStart0 + 1 && JobFor(kH1)->dy == 0 && k.asyncStarted == q0 + 3);
    assert(g_dyKept[0] == 2);
    WaitReady(kH1);
    Cancel(kH1);

    // 6. a START the switch let skip its drain but AsyncStartBody refused (here: a foreign START of a route being followed) takes
    // the drain before its sync plan, at its own drain point
    DyRecord(300);
    DyFakeJob(kJob);
    KdScopeSet(kH2, 2);  // g_fbActiveH == kH2: Active
    const LONG a10s0 = k.dsN[DS_A10START];
    DyEndJobSoon();
    assert(!Foreign(kH2) && g_rfbWhy == RFB_ACTIVE && !JobFor(kH2) && g_discCount == 0 && k.dsN[DS_A10START] == a10s0 + 1);
    assert(g_dyYield[DS_A10START] == 1 && !g_dyArm && !g_dyParked);
    KdScopeClear(kH2);
    assert(IcPlanHook(out, kH2) == out);

    // 7. CANCEL while parked frees the slot; the switch off keeps today's forced drain
    DyRecord(300);
    DyFakeJob(kJob);
    KdScopeSet(kH1, 2);
    assert(Start(kH1) && JobFor(kH1)->state == J_PEND && JobFor(kH1)->dy == 1);
    DyEndJobSoon();
    Cancel(kH1);  // CANCEL keeps the wait
    assert(!JobFor(kH1) && g_discCount == 0 && k.dsN[DS_CANCEL] >= 1);
    g_dyMode = 0;
    DyRecord(300);
    DyFakeJob(kJob);
    KdScopeSet(kH1, 2);
    DyEndJobSoon();
    assert(Start(kH1) && g_discCount == 0 && k.dsN[DS_START] == dsStart0 + 2 && JobFor(kH1)->dy == 0);
    WaitReady(kH1);
    Cancel(kH1);

    g_async.state = A_IDLE;
    g_async.handle = 0;
    g_discCount = 0;
    memset(g_dyYield, 0, sizeof(g_dyYield));
    memset(g_dyKept, 0, sizeof(g_dyKept));
    g_dyTicksMax = g_dyCapped = g_dyAgeMaxMs = 0;
    g_dyOwn = 0;
    g_dyOwnYield = g_dyReadyQueued = 0;
    k.fbRetryMode = savedRetry;
    k.fbRetryParked = savedParked, k.fbRetryQueued = savedQueued, k.fbRetryGaveUp = savedGaveUp, k.fbRetryTicksMax = savedTicks;
    *lockLevel = savedLevel;
    g_asRmSlot = savedRm;
    g_fbAcq = savedAcq;
    p[0x10E] = savedPend;
}
