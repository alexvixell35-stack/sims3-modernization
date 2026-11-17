// [MovedWaits] (0.36.49, docs/178 §9, counters v120): where the wait a read-only answer skipped goes, measured in place.
// Observe only, no hook of its own. Each RO switch calls MwSkip at the point where it answered without the game's +0x918 /
// gate wait while a route job ran (the "taken" counter of that switch). The first skip per (switch, job) opens a record
// with the job's sequence number and the skip time. The sim thread's next job wait behind the same job (WaitNote: +0x918
// pump in LockAcquireAsync, gate RtWaitJob, RunPlan) closes it as "moved": key, wait us, delay from the skip to the wait's
// start. The worker's job end closes every open record of that job and adds skip -> job end to mwAvoidUs, the upper bound
// of the wait the skip avoided (the game's call would have waited until the job released +0x918; TailUnlock / P5c lending
// can end such a wait earlier). Net in-place gain per switch ~= mwAvoidUs - mwMovedUs.
// Next-taker log points (docs/178 §9.3): every sim job wait at the ranked keys kNtKeys is counted with job kind and the
// T6 / IdleCheckRO switch state, skip or not, so the first T6 / IdleCheckRO run already shows the next taker.
// [HitchAttr] on: the frame block gets the moved ms, count, switch mask and the longest moved key (HaFrame v2).
// shortcut: the worker reads the sim thread's records at job end without a lock; a record the sim reopens for the next job in
// that instant can lose one mwAvoidUs sample (counters only). Upgrade if mwAvoidUs ever looks off against mwJobs.
// shortcut: a wait records its job as seq | 0x80000000 for a foreign job (g_jobSeq stays far below 2^31 in a session).
// Included by ts3native.cpp after hitchattr.cpp.

static const DWORD kNtKeys[10] = {
    TS3A(0x0075AA16),  // 0 GoalPoint_Validate's goal footprint gather 0x82F0B0 (0x75AA11): next +0x918 take after T6's empty answer
    TS3A(0x0075A64C),  // 1 GoalPoint_Validate's own query (T6 fallbacks: footprints found, doubt) on the next ring point
    TS3A(0x00755990),  // 2 IsOkToIdleAtCurrentLocation's obstacle query 0x82EDB0 (0x75598B): IdleCheckRO's ikOnward answers
    TS3A(0x0062FB74),  // 3 gate-12 GetIfPresent (other sim callers, IdleCheckRO fallbacks)
    TS3A(0x00760BFF),  // 4 gate 10, the sync plan's goal step
    0x05000000,  // 5 RunPlan: a sim sync plan that waits for the job first
    TS3A(0x0085FE10),  // 6 steering obstacle query (locomotion pass)
    TS3A(0x0062EF7A),  // 7 gate 0
    TS3A(0x00755FDD),  // 8 0x755EA0's obstacle query (route flow 0x75DE90)
    TS3A(0x00833593),  // 9 UpdateSimFootprint (docs/89: the usual absorber)
};
static const char* const kMwName[MW_N] = {"goalvalid", "idlecheck", "slot", "avoid", "gate0", "routetoobj", "placement",
                                          "obstructed", "steer"};
static struct { volatile DWORD seq; LARGE_INTEGER t; bool moved; } g_mwPend[MW_N];  // sim thread writes, worker reads at job end

static void MwSkip(int sw) {
    Ts3nCounters& k = ts3native_counters;
    if (!k.mwReady || g_async.state != A_RUNNING || GetCurrentThreadId() != g_async.owner) return;
    InterlockedIncrement(&k.mwSkips[sw]);
    const DWORD seq = g_asyncSeq;
    if (g_mwPend[sw].seq == seq) return;  // one record per (switch, job): the first skip
    QueryPerformanceCounter(&g_mwPend[sw].t);
    g_mwPend[sw].moved = false;
    g_mwPend[sw].seq = seq;
    InterlockedIncrement(&k.mwJobs[sw]);
}

// the job a wait is behind, read at the wait's start (0 = none running)
static DWORD MwSeqNow() { return g_async.state == A_RUNNING ? g_asyncSeq | (g_jobFb ? 0x80000000 : 0) : 0; }

static void MwWait(DWORD key, LONG us, DWORD tok) {
    Ts3nCounters& k = ts3native_counters;
    if (!k.mwReady || !tok) return;
    const DWORD seq = tok & 0x7FFFFFFF;
    for (int i = 0; i < 10; i++) {
        if (kNtKeys[i] != key) continue;
        InterlockedIncrement(&k.ntN[i]);
        InterlockedExchangeAdd(&k.ntUs[i], us);
        Max(&k.ntMaxUs[i], us);
        if (tok & 0x80000000) InterlockedIncrement(&k.ntFb[i]);
        if (k.gvMode) InterlockedIncrement(&k.ntGv[i]);
        if (k.ikMode) InterlockedIncrement(&k.ntIk[i]);
        break;
    }
    LARGE_INTEGER now;
    QueryPerformanceCounter(&now);
    const LONGLONG start = now.QuadPart - (LONGLONG)us * g_qpf.QuadPart / 1000000;
    LONG mask = 0;
    for (int sw = 0; sw < MW_N; sw++) {
        if (g_mwPend[sw].seq != seq || g_mwPend[sw].moved) continue;
        g_mwPend[sw].moved = true;
        mask |= 1 << sw;
        InterlockedIncrement(&k.mwMoved[sw]);
        InterlockedExchangeAdd(&k.mwMovedUs[sw], us);
        if (us > k.mwMovedMaxUs[sw]) k.mwMovedMaxUs[sw] = us, k.mwMaxKey[sw] = (LONG)key;  // sim thread only
        const LONGLONG d = start - g_mwPend[sw].t.QuadPart;
        if (d > 0) InterlockedExchangeAdd(&k.mwDelayUs[sw], (LONG)(d * 1000000 / g_qpf.QuadPart));
    }
    if (!mask) return;
    int i = 0;
    while (i < 12 && k.mwKey[i] && k.mwKey[i] != (LONG)key) i++;
    if (i == 12) InterlockedIncrement(&k.mwKeyOther);
    else k.mwKey[i] = (LONG)key, InterlockedIncrement(&k.mwKeyN[i]), InterlockedExchangeAdd(&k.mwKeyUs[i], us);
    HaFrame& f = ts3native_hitchattr;  // the owner thread only, as HaNote
    if (f.enabled) {
        f.mvUs += us, f.mvN++, f.mvSw |= mask;
        if (us >= f.mvMaxUs) f.mvMaxUs = us, f.mvKey = (LONG)key;
    }
}

// worker, at the job's +0x918 release
static void MwJobEnd(DWORD seq) {
    Ts3nCounters& k = ts3native_counters;
    if (!k.mwReady) return;
    LARGE_INTEGER now;
    QueryPerformanceCounter(&now);
    for (int sw = 0; sw < MW_N; sw++) {
        if (g_mwPend[sw].seq != seq) continue;
        const LONGLONG d = now.QuadPart - g_mwPend[sw].t.QuadPart;
        if (d > 0) InterlockedExchangeAdd(&k.mwAvoidUs[sw], (LONG)(d * 1000000 / g_qpf.QuadPart));
    }
}

// one stats-block line per switch with skips, the top moved keys and the next-taker keys; plus the T6 / IdleCheckRO summary
static void MwLogCounters() {
    const Ts3nCounters& c = ts3native_counters;
    if (c.gvReady)
        Log("goal validation RO: mode %ld shadow %ld calls %ld taken %ld (no lot %ld) max %ld us, fallbacks %ld/%ld/%ld/%ld/%ld/%ld/%ld, "
            "shadow %ld empty %ld (%ld%%) mismatch %ld", c.gvMode, c.gvShadow, c.gvCalls, c.gvTaken, c.gvNoLot, c.gvMaxUs, c.gvFb[0],
            c.gvFb[1], c.gvFb[2], c.gvFb[3], c.gvFb[4], c.gvFb[5], c.gvFb[6], c.gvShadowRuns, c.gvShadowEmpty,
            c.gvShadowRuns ? c.gvShadowEmpty * 100 / c.gvShadowRuns : 0, c.gvShadowMismatch);
    if (c.ikReady)
        Log("idle check RO: mode %ld shadow %ld calls %ld taken %ld hits %ld pass %ld onward %ld max %ld us, fallbacks state %ld "
            "qlbusy %ld shape %ld tripwire %ld jobend %ld redo %ld, shadow %ld mismatch %ld", c.ikMode, c.ikShadow, c.ikCalls, c.ikTaken,
            c.ikHits, c.ikPass, c.ikOnward, c.ikMaxUs, c.ikFb[0], c.ikFb[1], c.ikFb[2], c.ikFb[3], c.ikFb[4], c.ikFb[5], c.ikShadowRuns,
            c.ikShadowMismatch);
    if (!c.mwReady) return;
    for (int sw = 0; sw < MW_N; sw++)
        if (c.mwSkips[sw])
            Log("moved waits %s: skips %ld jobs %ld moved %ld (%ld ms, max %ld ms at 0x%08lX) delay %ld ms avoided <= %ld ms net <= %ld ms",
                kMwName[sw], c.mwSkips[sw], c.mwJobs[sw], c.mwMoved[sw], c.mwMovedUs[sw] / 1000, c.mwMovedMaxUs[sw] / 1000,
                c.mwMaxKey[sw], c.mwDelayUs[sw] / 1000, c.mwAvoidUs[sw] / 1000, (c.mwAvoidUs[sw] - c.mwMovedUs[sw]) / 1000);
    char b[640];
    int n = 0;
    for (int i = 0; i < 12 && c.mwKey[i] && n < 560; i++)
        n += sprintf_s(b + n, sizeof(b) - n, " 0x%08lX %ld/%ld", c.mwKey[i], c.mwKeyN[i], c.mwKeyUs[i] / 1000);
    b[n] = 0;
    Log("moved waits keys (n/ms):%s other %ld", n ? b : " none", c.mwKeyOther);
    n = 0;
    for (int i = 0; i < 10 && n < 560; i++)
        if (c.ntN[i])
            n += sprintf_s(b + n, sizeof(b) - n, " 0x%08lX %ld/%ld/%ld fb %ld gv %ld ik %ld", c.ntKey[i], c.ntN[i], c.ntUs[i] / 1000,
                           c.ntMaxUs[i] / 1000, c.ntFb[i], c.ntGv[i], c.ntIk[i]);
    b[n] = 0;
    Log("next takers (n/ms/max ms):%s", n ? b : " none");
}

static void InstallMovedWaits(const wchar_t* ini) {
    Ts3nCounters& k = ts3native_counters;
    for (int i = 0; i < 10; i++) k.ntKey[i] = (LONG)kNtKeys[i];
    if (!GetPrivateProfileIntW(L"MovedWaits", L"Enabled", 0, ini)) { Log("moved waits off (Enabled=0)"); return; }
    k.mwReady = 1;
    Log("OK   moved waits: per-switch skip records, next-taker keys (docs/178 §9)");
}
