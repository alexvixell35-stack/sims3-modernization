// [RouteSnap] FreeLock=1: P5c (docs/94 §22.11; counters v65). Included by ts3native.cpp after p5b.cpp. Needs ReleaseLock=1
// (P5b's window: +0x918 lent out at the clone run's start, taken back before the answer, before each door ask and at any
// tripwire) and so Mode=2. P5b lends the lock only to the sim's steering and, with ReleaseGates, its read-only gate leaves.
// P5c widens the window of a taken job to every plain +0x918 acquire of the sim, and lets destroys run in it:
//   - admission (P5bAllowed): while g_p5cJob, a sim acquire outside a job callback that is not a gate wait (RgEnter's
//     t_waitKey unset, or the C-A rebuild's key 0x80E5B0: writers such as UpdateSimFootprint, steering, any nested caller)
//     takes the lent lock. A gate-0 miss given back keeps its key, so it never retries as a plain acquire.
//     Gate waits keep P5b's rules: the read families with ReleaseGates; every other gate waits for the job as in A10 (site 0
//     misses, sites 1-11 = lot planner, Q, goal step and the sync plans' own gate, whose shared state the suspended IC frame
//     consumed before Route_PlanLocked (docs/87); the coarse rebuilds 13-14; the gate-12 writer families);
//   - stale answers: the clone answers the START cut. Any recorded routing write since that cut (snap.cpp g_spWrites) refuses
//     the answer at the retake, and the game's own plan answers under the retaken lock (P5's fallback, reason epoch);
//   - lifetime (docs/88 design G): a destroy inside the window parks one reference to its object (FindByIdAddRef) and runs at
//     once, without D2's clone-run wait and without the job wait. Parked references are released on the owner (sim) thread at a
//     point with no job (destroy, save, closing and teardown entries; the START / READY polls), job starts held off through
//     g_rgOffSimOpen. Kept waits: a D-X record for the id, a full graveyard, off the sim thread or inside a job callback;
//   - no deadlock against the retake: the sim never waits for the job or its clone run while it owns the lent lock. A sync
//     plan under the lent lock plans on the sim without the job wait (RunPlan). Any other such wait hands the lock back,
//     waits for the whole job, and takes the lock again (P5cHandBegin / P5cHandEnd; a tripwire: P5c off for the session).
// A_RUNNING keeps "a job is live"; "the worker holds +0x918" gets the P5c window as a second exception next to P5b's
// (docs/94 §22.11.3: every site that reads it, and what it does now).

static volatile LONG g_p5cOn;   // FreeLock=1 installed
// g_p5cJob (p5b.cpp): 1 from before this job's window opens until after its last retake (worker writes it)
static __declspec(thread) bool t_p5cRan;  // worker: this job's window opened with P5c admission
static __declspec(thread) LONG t_p5cW1;   // worker: g_spWrites when it opened
static volatile LONG g_p5cLog;

// the START cut's recorded-write count, by job sequence number (sim at START, worker at the retake; 64 > kJobs)
struct P5cCut { volatile LONG seq, w; };
static P5cCut g_p5cCut[64];
static void P5cOnStart(const AsyncJob* j) {  // P5OnStart, right after the cut: its write count (0.36.41: a borrowed cut's own, p4W)
    P5cCut& e = g_p5cCut[j->seq & 63];
    e.w = j->p4W;
    InterlockedExchange(&e.seq, (LONG)j->seq);
}

// P5Plan around P4RunClone, in place of P5bBegin / P5bEnd
static void P5cLend(void* lock, bool verify) {
    Ts3nCounters& k = ts3native_counters;
    const bool want = g_p5cOn && k.p5cFree && !k.p5cOff && !verify;
    t_p5cW1 = g_spWrites;
    if (want) InterlockedExchange(&g_p5cJob, 1);  // published before P5bOpen publishes the window
    P5bBegin(lock, verify);
    t_p5cRan = want && t_p5b == P5B_OPEN;
    if (want && !t_p5cRan) InterlockedExchange(&g_p5cJob, 0);
    if (t_p5cRan) InterlockedIncrement(&k.p5cJobs);
}
static void P5cTake() {
    P5bEnd();  // the worker owns +0x918 again: no sim holder is left
    InterlockedExchange(&g_p5cJob, 0);
}
static bool P5cRan() { return t_p5cRan; }  // P5Plan, before P5cStale (which clears it)
// P5Plan after the run: true = a recorded routing write landed after the START cut, so the clone answer is never taken
static bool P5cStale(DWORD seq) {
    if (!t_p5cRan) return false;
    t_p5cRan = false;
    const P5cCut& e = g_p5cCut[seq & 63];
    const LONG now = g_spWrites;
    const bool known = e.seq == (LONG)seq;
    if (known && now == e.w) return false;
    InterlockedIncrement(&ts3native_counters.p5cStale[known && now == t_p5cW1 ? 0 : 1]);  // 0: all before the window
    if (t_auJob) InterlockedIncrement(&ts3native_counters.auStaleAfter);  // AskLent: a lent ask in this job (p5b.cpp)
    return true;
}

// 0.35.26 (docs/94 §22.30), P5Plan before the acquire: a recorded routing write since the START cut already means P5cStale
// refuses the answer at the retake whatever the run finds, so the job skips the acquire and the clone run and the game's plan
// answers at once (counted as stale before the window). A window P5bOpen would refuse could have taken it: conservative
static bool P5cStaleNow(DWORD seq, bool verify) {
    const Ts3nCounters& k = ts3native_counters;
    if (!g_p5cOn || !k.p5cFree || k.p5cOff || verify) return false;
    const P5cCut& e = g_p5cCut[seq & 63];
    return e.seq == (LONG)seq && g_spWrites != e.w;
}
static bool P5cStaleEarly(DWORD seq, bool verify) {
    if (!P5cStaleNow(seq, verify)) return false;
    InterlockedIncrement(&ts3native_counters.p5cStale[0]);
    return true;
}

// --- lifetime: the destroy graveyard (docs/88 §5.1) ---
struct P5cGrave { void* obj; LARGE_INTEGER t; };
static const int kP5cGrave = 512;
static P5cGrave g_p5cGy[kP5cGrave];
static volatile LONG g_p5cGyN;
static SRWLOCK g_p5cGyLock = SRWLOCK_INIT;

// docs/67 §15 ([CrashForensics] Enabled=1 only, first 4096 per kind): one line per park and per final release with the object's
// id (+0x08), refcount (+0x04), destroyed bit (+0x28 & 0x400000) and resource key (+0x18..+0x24: its type), so a later fault on
// a freed object, an ErrorTrap "Destroyed Object Found" or a guard hit can be matched to the object by id and time
static volatile LONG g_p5cLogN[2];
static void P5cObjLog(const char* what, void* obj, LONG ageUs) {
    if (!g_cfOn || InterlockedIncrement(&g_p5cLogN[what[0] == 'p' ? 0 : 1]) > 4096) return;
    const DWORD* o = (const DWORD*)obj;
    if (!Readable(o, 0x2C)) { Log("P5c %s: object %p unreadable", what, obj); return; }
    Log("P5c %s: object %08lX id %08lX%08lX vt %08lX count %ld flags %08lX%s key %08lX %08lX %08lX %08lX, age %ld us, job %s, "
        "graveyard %ld",
        what, (DWORD)(uintptr_t)obj, o[3], o[2], o[0], (LONG)o[1], o[10], o[10] & 0x400000 ? " (destroyed)" : "", o[6], o[7],
        o[8], o[9], ageUs, g_async.state == A_RUNNING ? "running" : "idle", g_p5cGyN);
}

static int P5cDiscN(DWORD idLo, DWORD idHi) {  // D-X: queued discourage records of this id (owner thread only)
    int n = 0;
    for (int i = 0; i < g_discCount; i++) {
        const DiscRec& r = g_disc[(g_discHead + i) % kDiscCap];
        if (r.idLo == idLo && r.idHi == idHi && r.lock == g_rgLock) n++;
    }
    return n;
}
// DestroyParkDx (dxMode 1, 2026-11-04): the last P5cPark parked an object that had queued records; RT_DESTROY then skips
// D-X 1 (it takes +0x918, the job wait) and the records stay queued. At the next drain (before any flush: DiscPoll runs
// before P5cFlush at the START / READY polls, save and closing drain first) the id is gone, and DiscApplyOne's gone rules
// apply: Remove, usf disable and pf removes swept by id, Add, usf enable and fp2 dropped, a reused id dropped
// (discIdMismatch). Owner thread only. g_p5cParkDx is defined in rtsites.cpp, beside P5cPark's declaration.

// rtsites.cpp RT_DESTROY (thiscall svc; idLo, idHi), before D2 and the job wait: true = one reference parked, the destroy runs
// now; its last Release cannot run before a flush, so nothing the suspended frame or the clone run reads is freed.
// Design G standalone ([Discourage] DestroyPark=1, dpMode; docs/88 §10): the same park for any running async job, with or
// without P5c / RouteSnap. The job's FindById pointers (docs/88 §3.2) stay allocated until a no-job flush, as in the window.
static bool P5cPark(DWORD idLo, DWORD idHi) {
    Ts3nCounters& k = ts3native_counters;
    const bool alone = !(g_p5cJob && g_p5bWin);
    if (alone && !(k.dpReady && k.dpMode == 1 && g_async.state == A_RUNNING && !k.rtClosing)) return false;
    int kept = -1, dx = 0;
    g_p5cParkDx = false;
    if (GetCurrentThreadId() != g_async.owner || g_inInvoke) kept = 2;
    else if ((dx = P5cDiscN(idLo, idHi)) && (k.dxMode != 1 || !k.discSweepMode)) kept = 0;  // D-X applies its records under the job wait; DestroyParkDx leans on the sweep
    else if (g_p5cGyN >= kP5cGrave) kept = 1;   // only this thread parks; a flush only shrinks the list
    void* obj = NULL;
    if (kept < 0) {
        void* svc = g_objSvc();
        if (!svc || !g_findRef(svc, NULL, idLo, idHi, &obj) || !obj) kept = 3;
    }
    if (kept >= 0) {
        InterlockedIncrement(&k.p5cKeptWait[kept]);
        if (alone) InterlockedIncrement(&k.dpKeptWait);
        return false;
    }
    AcquireSRWLockExclusive(&g_p5cGyLock);
    P5cGrave& g = g_p5cGy[g_p5cGyN];
    g.obj = obj;
    QueryPerformanceCounter(&g.t);
    InterlockedIncrement(&g_p5cGyN);
    ReleaseSRWLockExclusive(&g_p5cGyLock);
    Max(&k.p5cPendMax, g_p5cGyN);
    InterlockedIncrement(&k.p5cParked);  // every park (graveyard total); dpParked = those outside a P5c window
    if (alone) InterlockedIncrement(&k.dpParked);
    if (dx) { g_p5cParkDx = true; InterlockedIncrement(&k.dxParked); InterlockedExchangeAdd(&k.dxLeft, dx); }
    CeRec(CE_P5C_PARK, (DWORD)(uintptr_t)obj, idLo, idHi);
    P5cObjLog("park", obj, 0);
    return true;
}

// the owner (sim) thread outside a job callback, no job running: every parked reference released, oldest first, with job
// starts held off (ServeAsync's Dekker on g_rgOffSimOpen). A release may destroy more; a nested flush finds the list already
// taken. Owner only (0.36.9 design G): the free runs component destructors and the managed release, which belong to the sim.
// Crash I (docs/67 §11): the last 64 released references (object, its vtable and count before the release, the flush's
// caller in this module, time), matched by XcptLog against an access violation's registers: a hit names our flush as the
// free of the faulting object. Owner thread writes only; the crash-time read is racy by design
struct P5cRel { DWORD obj, vt, site; LONG count; LARGE_INTEGER t; DWORD idLo, idHi, key0; LONG ageUs; };  // id, key type, park age: docs/67 §15
static P5cRel g_p5cRel[64];
static volatile LONG g_p5cRelN;
// per register the nearest released object at or below it (within 0x400); returns the first register's object, 0 = none
static DWORD P5cRelMatch(const CONTEXT* c) {
    const DWORD regs[6] = {c->Eax, c->Ebx, c->Ecx, c->Edx, c->Esi, c->Edi};
    static const char* const names[6] = {"eax", "ebx", "ecx", "edx", "esi", "edi"};
    const LONG n = g_p5cRelN;
    DWORD first = 0;
    for (int i = 0; i < 6; i++) {
        LONG best = -1;
        for (LONG j = n - 1; j >= 0 && j >= n - 64; j--) {
            const P5cRel& r = g_p5cRel[j & 63];
            if (regs[i] >= r.obj && regs[i] - r.obj < 0x400 && (best < 0 || r.obj > g_p5cRel[best & 63].obj)) best = j;
        }
        if (best < 0) continue;
        const P5cRel& r = g_p5cRel[best & 63];
        if (!first) first = r.obj;
        Log("P5c: fault register %s %08lX is +0x%lX into object %08lX (vt %08lX, count %ld before) released %ld releases ago "
            "by the flush from %08lX, %ld us before the fault (id %08lX%08lX, key %08lX, parked %ld us)", names[i], regs[i],
            regs[i] - r.obj, r.obj, r.vt, r.count, n - 1 - best, r.site, UsSince(r.t), r.idHi, r.idLo, r.key0, r.ageUs);
    }
    return first;
}

// crash dump (docs/67 §15): the whole released ring, newest first, once per session, for a fault in TS3W.exe code
static volatile LONG g_p5cDumpN;
static void P5cRelDump() {
    const LONG n = g_p5cRelN;
    if (!n || InterlockedIncrement(&g_p5cDumpN) > 1) return;
    Log("P5c: released ring (last %ld of %ld, newest first: obj vt count-before id key parked-us site ms-ago)", n < 64 ? n : 64L, n);
    for (LONG j = n - 1; j >= 0 && j >= n - 64; j--) {
        const P5cRel& r = g_p5cRel[j & 63];
        Log("  %08lX %08lX %ld %08lX%08lX %08lX %ld %08lX %.1f", r.obj, r.vt, r.count, r.idHi, r.idLo, r.key0, r.ageUs, r.site,
            UsSince(r.t) / 1000.0);
    }
}
// nearest released object at or below `a` (within 0x400), for guard lines; false = none in the ring
static bool P5cRelFind(DWORD a, char* out, size_t cap) {
    const LONG n = g_p5cRelN;
    LONG best = -1;
    for (LONG j = n - 1; j >= 0 && j >= n - 64; j--) {
        const P5cRel& r = g_p5cRel[j & 63];
        if (a >= r.obj && a - r.obj < 0x400 && (best < 0 || r.obj > g_p5cRel[best & 63].obj)) best = j;
    }
    if (best < 0) { sprintf_s(out, cap, "not in the P5c released ring (last 64)"); return false; }
    const P5cRel& r = g_p5cRel[best & 63];
    sprintf_s(out, cap, "+0x%lX into %08lX released by our flush %ld releases ago, %.1f ms ago, count %ld before, id %08lX%08lX, key %08lX",
              a - r.obj, r.obj, n - 1 - best, UsSince(r.t) / 1000.0, r.count, r.idHi, r.idLo, r.key0);
    return true;
}

static void P5cFlush() {
    if (!g_p5cGyN) return;
    if (GetCurrentThreadId() != g_async.owner || g_inInvoke) return;
    Ts3nCounters& k = ts3native_counters;
    InterlockedIncrement(&g_rgOffSimOpen);
    if (g_async.state == A_RUNNING) {
        RgOffSimDrop();
        InterlockedIncrement(&k.p5cFlushRefused);
        return;
    }
    P5cGrave out[kP5cGrave];
    AcquireSRWLockExclusive(&g_p5cGyLock);
    const LONG n = g_p5cGyN;
    memcpy(out, g_p5cGy, n * sizeof(P5cGrave));
    InterlockedExchange(&g_p5cGyN, 0);
    ReleaseSRWLockExclusive(&g_p5cGyLock);
    for (LONG i = 0; i < n; i++) {
        void* obj = out[i].obj;
        const LONG age = UsSince(out[i].t);
        Max(&k.p5cAgeMaxUs, age);
        // T-G1: our reference must still be there (count at obj+4, docs/88 §3.1); else leak it rather than free twice
        if (!Readable((const u8*)obj + 4, 4) || *(volatile LONG*)((u8*)obj + 4) < 1) {
            InterlockedIncrement(&k.p5cCountLow);
            continue;
        }
        P5cRel& r = g_p5cRel[g_p5cRelN & 63];
        r.obj = (DWORD)(uintptr_t)obj;
        r.vt = *(const DWORD*)obj;
        r.count = *(volatile LONG*)((u8*)obj + 4);
        r.site = (DWORD)(uintptr_t)_ReturnAddress();
        r.idLo = ((const DWORD*)obj)[2], r.idHi = ((const DWORD*)obj)[3], r.key0 = ((const DWORD*)obj)[6], r.ageUs = age;
        QueryPerformanceCounter(&r.t);
        InterlockedIncrement(&g_p5cRelN);
        CeRec(CE_P5C_REL, r.obj, (DWORD)age, (DWORD)r.count);
        if (r.count == 1) P5cObjLog("final release", obj, age);  // count 1 = ours was the last: the free runs now
        g_objRelease(&obj, NULL);
    }
    InterlockedExchangeAdd(&k.p5cFlushed, n);
    RgOffSimDrop();
}

// --- no wait for the job while holding its lent lock ---
static bool P5cLent(DWORD tid) {  // this thread owns +0x918 inside a window (the lock the worker lent out)
    void* lock = g_p5bLock;
    return g_p5bWin && lock && P5bOwner(lock) == (LONG)tid;
}
// RunPlan on the owner while a job runs: with the lent lock the job is in its lock-free search, and waiting would deadlock
// against its retake; the sync plan runs here, under this thread's hold, as A10 runs a sync plan with no job
static bool P5cPlanHere() {
    if (!P5cLent(GetCurrentThreadId())) return false;
    InterlockedIncrement(&ts3native_counters.p5cPlanHere);
    return true;
}
// WaitAsyncDone, WaitJobDone, P5cNrEnter: the sim about to wait for the job (or its clone run) while it owns the lent lock.
// Every level goes back for the wait (the worker's retake gets it); P5cHandEnd waits for the whole job, then takes the levels
// again through the pump (the job's managed calls keep running). Tripwire: P5c is off for the rest of the session.
static __declspec(thread) void* t_p5cHand;
static LONG P5cHandBegin() {
    const DWORD me = GetCurrentThreadId();
    if (!P5cLent(me) || RgIsWorker(me)) return 0;
    Ts3nCounters& k = ts3native_counters;
    void* lock = g_p5bLock;
    const LONG n = ((const CRITICAL_SECTION*)lock)->RecursionCount;
    InterlockedIncrement(&k.p5cHandOver);
    k.p5cOff = 1;
    if (InterlockedIncrement(&g_p5cLog) <= 8)
        Log("P5c: thread %lu waited for the job of route %08lX holding its lent +0x918 (%ld levels): handed back for the wait; "
            "FreeLock off for this session", me, g_async.handle, n);
    InterlockedCompareExchange(&k.rgOwnerTid, 0, (LONG)me);
    for (LONG i = 0; i < n; i++) g_rgRelOrig(lock, NULL);
    t_p5cHand = lock;
    return n;
}
static void P5cHandEnd(LONG n) {
    if (!n) return;
    static const DWORD kInf = 0xFFFFFFFF;
    WaitAsyncDone();  // the whole job: a D2 writer must not write into a re-armed clone run either
    for (LONG i = 0; i < n; i++) LockAcquireHook(t_p5cHand, NULL, &kInf);
}
// D2 entry (rtsites.cpp, routegate.cpp coarse gates): P4NrEnter waits for the clone run, which may need the lock back first
static void P5cNrEnter() {
    const LONG n = SnLive() ? P5cHandBegin() : 0;
    P4NrEnter();
    P5cHandEnd(n);
}

// [RouteSnap] FreeLock=1 (from ApplyP5b, after P5b installed)
static void ApplyP5c(const wchar_t* ini) {
    Ts3nCounters& k = ts3native_counters;
    if (!GetPrivateProfileIntW(L"RouteSnap", L"FreeLock", 0, ini)) {
        Log("route snapshot P5c off (FreeLock=0): P5b admission only");
        return;
    }
    k.p5cFree = 1;
    k.p5cReady = 1;
    g_p5cOn = 1;
    Log("OK   route snapshot P5c (FreeLock=1): a taken clone run lends +0x918 to every plain sim acquire (writers too); destroys "
        "park one reference and run; answers with a routing write after the cut are refused (the game's plan answers)");
}
