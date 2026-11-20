// docs/673: [RouteJobPhases] HoldProbe=1, observe only. Included by ts3native.cpp right after plancand.cpp (its frames call
// WhEnter / WhPlanEnd); ServeAsync calls WhJobBegin / WhJobEnd around the job's IC, LockAcquireAsync counts the sim thread's
// +0x918 waits in g_whSimWait. Nothing here changes what a plan does: it patches nothing and reads only our own state.
//
// What it answers (the worker holds +0x918 for a whole job, docs/670 §7):
//   - where the hold time is: jobs by length, split by "a plan of the job failed"; the job time outside PlanHierarchical;
//   - what a plan is made of: plans by candidate count (LotEdgePortals_Choose calls) and result, their cell A* time;
//   - what a hand-over of the lock at a boundary could give: at the start of every plan, candidate, lot leg, world A*, water
//     plan and post pass of a worker job, is the sim thread waiting on +0x918 right now? The first such boundary of a job is
//     where a yield would have let the sim in: the time from there to the job's end is the wait it would have cut, at most
//     (the sim's taker also needs its own time, and the worker would wait for it);
//   - what the job time outside the planner is (docs/673 §9): before / inside / after Route_PlanLocked, the host-call waits
//     in each, our own wrappers and job end, and how much of a job is left when its last plan ends with the sim waiting;
//   - how many jobs repeat: the same route handle and IC kind starting within HoldProbeDupMs of that pair's last end.
// Needs [RouteAsync]; the plan and boundary parts need [RouteJobPhases] CandidateSplit=1 (plancand.cpp's frames).
// Runtime switch: export ts3native_workerhold +0x00 (native_set workerhold), 1 = count.
#include "workerhold-core.h"

// exported block, not in the ts3fixes slot range (docs/registry.md "native exports")
struct WorkerHoldCounters {
    volatile LONG whMode;                   // +0x00 runtime switch: 1 = count
    volatile LONG whReady;                  // +0x04 bit 0 job hooks ([RouteAsync]), bit 1 plan frames (CandidateSplit)
    volatile LONG whDupMs;                  // +0x08 HoldProbeDupMs
    volatile LONG whDupN;                   // +0x0C jobs that repeat a route handle + IC kind within whDupMs of its last end
    volatile LONG whEndWait;                // +0x10 jobs that ended with the sim waiting on +0x918 and no boundary reached in the wait
    volatile LONG whPad;                    // +0x14
    volatile LONGLONG whDupUs;              // +0x18 their time, us
    volatile LONGLONG whOutUs;              // +0x20 job time outside PlanHierarchical, us (all jobs)
    volatile LONG whJobN[2][WH_JOBB];       // +0x28 jobs by length (WhJobBucket); [1] = a plan of the job failed
    volatile LONGLONG whJobUs[2][WH_JOBB];  // +0x58
    volatile LONG whPlanN[2][2][WH_CANDB];  // +0xB8 plans: [worker, sync][ok, failed][candidates 0-1, 2, 3-9, >= 10]
    volatile LONGLONG whPlanUs[2][2][WH_CANDB];
    volatile LONGLONG whPlanCellUs[2][2];   // their cell A* self time
    volatile LONG whSeen[WH_KINDS];         // boundaries reached inside worker jobs
    volatile LONG whWaiting[WH_KINDS];      // of those: the sim thread was waiting on +0x918
    volatile LONG whFirstN[WH_KINDS];       // jobs whose first boundary with the sim waiting was of this kind
    volatile LONGLONG whFirstRemUs[WH_KINDS];  // from that boundary to the job's end, us
    // docs/673 §9, appended: the job's time by part (WH_PRE, WH_IN, WH_TAIL)
    volatile LONGLONG whPartUs[WH_PARTS];  // before / between Route_PlanLocked calls, inside (our detour's outermost call), after the last
    volatile LONGLONG whHostUs[WH_PARTS];  // of it: the worker's waits for a managed call on the sim thread (Proxy)
    volatile LONG whHostN[WH_PARTS];
    volatile LONG whNoPlanN;               // jobs whose IC never reached Route_PlanLocked
    volatile LONGLONG whNoPlanUs;
    volatile LONGLONG whGameUs;            // the game's Route_PlanLocked inside our detour (t_jobPlanUs), us
    volatile LONGLONG whEndUs;             // our own job end between the IC's return and the +0x918 release
    volatile LONG whEndMaxUs;
    volatile LONG whTailN;                 // jobs with a plan
    volatile LONG whTailWaitN;             // of them: the sim waited on +0x918 when the last Route_PlanLocked returned
    volatile LONG whPad2;
    volatile LONGLONG whTailWaitUs;        // their time from there to the release (tail + our job end)
};
extern "C" __declspec(dllexport) WorkerHoldCounters ts3native_workerhold = {};

static __declspec(thread) WhJob t_whJob;
static __declspec(thread) DWORD t_whStartTick;
static __declspec(thread) LONGLONG t_whIcQ;    // the IC's return (0 = no open probe job)
static __declspec(thread) LONGLONG t_whWaitQ;  // -1, or the tail ticks of a job whose last plan ended with the sim waiting
static WhDupRing g_whDups;  // the route worker only (one job at a time)

static LONGLONG WhNowQ() {
    LARGE_INTEGER q;
    QueryPerformanceCounter(&q);
    return q.QuadPart;
}

// ServeAsync, before the job's IC (the worker holds +0x918 from here to WhJobEnd)
static void WhJobBegin() {
    if (ts3native_workerhold.whMode != 1) { t_whJob.open = false; return; }
    WhJobReset(&t_whJob, true);
    WhSplitReset(&t_whJob.sp, WhNowQ());
    t_whStartTick = GetTickCount();
    t_whIcQ = 0, t_whWaitQ = -1;
}

// PlanLockedHook (ts3native.cpp), around the whole detour body: our wrappers and the game's Route_PlanLocked
static void WhPlEnter() {
    if (t_whJob.open) WhSplitEnter(&t_whJob.sp, WhNowQ());
}
static void WhPlLeave() {
    if (t_whJob.open) WhSplitLeave(&t_whJob.sp, WhNowQ(), g_whSimWait > 0);
}
// Proxy (ts3native.cpp): the worker waited from fromQ to now for a managed call on the sim thread
static void WhHost(LONGLONG fromQ) {
    if (t_whJob.open) WhSplitHost(&t_whJob.sp, WhNowQ() - fromQ);
}
// ServeAsync, right after the job's IC returned
static void WhIcDone() {
    if (!t_whJob.open) return;
    t_whIcQ = WhNowQ();
    WhSplitIcDone(&t_whJob.sp, t_whIcQ);
}
// ServeAsync, right before the job's +0x918 release (after WhJobEnd): what our own job end cost under the lock
static void WhRelease() {
    if (!t_whIcQ) return;
    WorkerHoldCounters& c = ts3native_workerhold;
    const LONGLONG endQ = WhNowQ() - t_whIcQ;
    const LONG endUs = (LONG)(endQ * 1000000 / g_qpf.QuadPart);
    InterlockedExchangeAdd64(&c.whEndUs, endUs);
    if (endUs > c.whEndMaxUs) c.whEndMaxUs = endUs;
    if (t_whWaitQ >= 0) {
        InterlockedIncrement(&c.whTailWaitN);
        InterlockedExchangeAdd64(&c.whTailWaitUs, (t_whWaitQ + endQ) * 1000000 / g_qpf.QuadPart);
    }
    t_whIcQ = 0, t_whWaitQ = -1;
}

// plancand.cpp PccEnter, a frame just opened on this thread; site = PCC_*
static void WhEnter(int site) {
    WhJob& j = t_whJob;
    if (!j.open) return;
    const int kind = site == PCC_PH ? WH_PLAN : site == PCC_CHOOSE ? WH_CAND : site == PCC_LOTPLAN ? WH_LOTLEG
                   : site == PCC_WORLD ? WH_WORLD : site == PCC_WATER ? WH_WATER : site == PCC_POST ? WH_POST : -1;
    if (kind < 0) return;
    const bool waits = g_whSimWait > 0;
    WhBoundary(&j, kind, waits, waits && j.firstKind < 0 ? WhNowQ() : 0);
}

// plancand.cpp PccPhEnd: an outermost PlanHierarchical call ended (any thread). cands = its LotEdgePortals_Choose calls
static void WhPlanEnd(LONG cands, LONG cellUs, LONG us, LONGLONG inclQ, DWORD code) {
    WorkerHoldCounters& c = ts3native_workerhold;
    if (c.whMode != 1) return;
    WhJob& j = t_whJob;
    const int sync = j.open ? 0 : 1, fail = code ? 1 : 0, b = WhCandBucket(cands);
    if (j.open) j.plans++, j.fails += fail, j.phQ += inclQ;
    InterlockedIncrement(&c.whPlanN[sync][fail][b]);
    InterlockedExchangeAdd64(&c.whPlanUs[sync][fail][b], us);
    InterlockedExchangeAdd64(&c.whPlanCellUs[sync][fail], cellUs);
}

// ServeAsync, after the job's IC and before the lock goes back; us = the job's time, gameUs = the game's Route_PlanLocked
static void WhJobEnd(DWORD handle, int kind, LONG us, LONG gameUs) {
    WorkerHoldCounters& c = ts3native_workerhold;
    WhJob& j = t_whJob;
    if (!j.open) return;
    j.open = false;
    const int fail = j.fails ? 1 : 0, b = WhJobBucket(us);
    InterlockedIncrement(&c.whJobN[fail][b]);
    InterlockedExchangeAdd64(&c.whJobUs[fail][b], us);
    const LONGLONG phUs = j.phQ * 1000000 / g_qpf.QuadPart;
    if (us > phUs) InterlockedExchangeAdd64(&c.whOutUs, us - phUs);
    for (int i = 0; i < WH_PARTS; i++) {
        InterlockedExchangeAdd64(&c.whPartUs[i], j.sp.partQ[i] * 1000000 / g_qpf.QuadPart);
        InterlockedExchangeAdd64(&c.whHostUs[i], j.sp.hostQ[i] * 1000000 / g_qpf.QuadPart);
        if (j.sp.hostN[i]) InterlockedExchangeAdd(&c.whHostN[i], j.sp.hostN[i]);
    }
    InterlockedExchangeAdd64(&c.whGameUs, gameUs);
    if (j.sp.plans) {
        InterlockedIncrement(&c.whTailN);
        if (j.sp.endWait) t_whWaitQ = j.sp.partQ[WH_TAIL];
    } else {
        InterlockedIncrement(&c.whNoPlanN);
        InterlockedExchangeAdd64(&c.whNoPlanUs, us);
    }
    for (int i = 0; i < WH_KINDS; i++) {
        if (j.seen[i]) InterlockedExchangeAdd(&c.whSeen[i], j.seen[i]);
        if (j.waiting[i]) InterlockedExchangeAdd(&c.whWaiting[i], j.waiting[i]);
    }
    if (j.firstKind >= 0) {
        InterlockedIncrement(&c.whFirstN[j.firstKind]);
        InterlockedExchangeAdd64(&c.whFirstRemUs[j.firstKind], (WhNowQ() - j.firstQ) * 1000000 / g_qpf.QuadPart);
    } else if (g_whSimWait > 0) {
        InterlockedIncrement(&c.whEndWait);
    }
    LONG prevUs = 0;
    if (WhDupNote(&g_whDups, handle, kind, t_whStartTick, GetTickCount(), us, (uint32_t)c.whDupMs, &prevUs)) {
        InterlockedIncrement(&c.whDupN);
        InterlockedExchangeAdd64(&c.whDupUs, us);
    }
}

#ifndef WORKERHOLD_HOST_TEST
// [RouteJobPhases] HoldProbe (after InstallPlanCand and [RouteAsync])
static void InstallWorkerHold(const wchar_t* ini) {
    WorkerHoldCounters& c = ts3native_workerhold;
    if (!GetPrivateProfileIntW(L"RouteJobPhases", L"HoldProbe", 0, ini)) { Log("worker hold probe off ([RouteJobPhases] HoldProbe=0)"); return; }
    if (!g_lockOrig) { Log("SKIP worker hold probe: needs [RouteAsync] Enabled=1 (the worker's jobs and the Lock_Acquire hook)"); return; }
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
    c.whReady = 1 | (g_pccSplit ? 2 : 0);
    c.whDupMs = Clamp(GetPrivateProfileIntW(L"RouteJobPhases", L"HoldProbeDupMs", 2000, ini), 0, 60000);
    c.whMode = 1;
    Log("OK   worker hold probe: jobs by length and result, repeats within %ld ms%s (switch ts3native_workerhold +0x00)", c.whDupMs,
        g_pccSplit ? ", plans by candidates, boundaries with the sim waiting on +0x918"
                   : "; plans and boundaries need [RouteJobPhases] CandidateSplit=1");
}

// the 60 s counter lines (ts3native.cpp MaybeLogCounters)
static void WhLogCounters() {
    const WorkerHoldCounters& c = ts3native_workerhold;
    if (!c.whReady) return;
    static const char* const kJob[WH_JOBB] = {"<5", "<20", "<50", "<100", "<250", ">=250"};
    static const char* const kCand[WH_CANDB] = {"0-1", "2", "3-9", ">=10"};
    static const char* const kKind[WH_KINDS] = {"plan", "candidate", "lot leg", "world", "water", "post"};
    char b[2][400];
    for (int f = 0; f < 2; f++) {
        int at = 0;
        for (int i = 0; i < WH_JOBB && at < (int)sizeof(b[f]); i++)
            at += sprintf_s(b[f] + at, sizeof(b[f]) - at, "%s%s %ld/%ld", i ? ", " : "", kJob[i], c.whJobN[f][i], (LONG)(c.whJobUs[f][i] / 1000));
    }
    Log("worker hold: mode %ld ready 0x%lX; jobs by ms (n/ms), every plan ok: %s; a plan failed: %s; outside PlanHierarchical %ld ms; "
        "repeats (same route and IC kind within %ld ms of its last end) %ld, %ld ms",
        c.whMode, c.whReady, b[0], b[1], (LONG)(c.whOutUs / 1000), c.whDupMs, c.whDupN, (LONG)(c.whDupUs / 1000));
    if (!(c.whReady & 2)) return;
    char p[4][200];
    for (int s = 0; s < 2; s++)
        for (int f = 0; f < 2; f++) {
            int at = 0;
            char* o = p[s * 2 + f];
            for (int i = 0; i < WH_CANDB && at < (int)sizeof(p[0]); i++)
                at += sprintf_s(o + at, sizeof(p[0]) - at, "%s%s %ld/%ld", i ? ", " : "", kCand[i], c.whPlanN[s][f][i], (LONG)(c.whPlanUs[s][f][i] / 1000));
            if (at < (int)sizeof(p[0])) sprintf_s(o + at, sizeof(p[0]) - at, " (cell %ld ms)", (LONG)(c.whPlanCellUs[s][f] / 1000));
        }
    Log("worker hold plans by candidates (n/ms): worker ok: %s; worker failed: %s; sync ok: %s; sync failed: %s", p[0], p[1], p[2], p[3]);
    char k[700];
    int at = 0;
    for (int i = 0; i < WH_KINDS && at < (int)sizeof(k); i++)
        at += sprintf_s(k + at, sizeof(k) - at, "%s%s %ld/%ld/%ld %ld ms", i ? ", " : "", kKind[i], c.whSeen[i], c.whWaiting[i], c.whFirstN[i],
                        (LONG)(c.whFirstRemUs[i] / 1000));
    Log("worker hold boundaries (reached / with the sim waiting on +0x918 / first such of its job, ms from it to the job's end): %s; "
        "jobs that ended with the sim waiting and no boundary in the wait %ld", k, c.whEndWait);
    LONGLONG phUs = 0;  // the worker's PlanHierarchical calls
    for (int f = 0; f < 2; f++)
        for (int i = 0; i < WH_CANDB; i++) phUs += c.whPlanUs[0][f][i];
    const LONGLONG inUs = c.whPartUs[WH_IN], ourUs = inUs - c.whGameUs - c.whHostUs[WH_IN];
    Log("worker hold parts (ms; host = the worker's waits for a managed call on the sim thread, ms / calls): before or between "
        "Route_PlanLocked %ld (host %ld / %ld), jobs that never reached it %ld / %ld ms; inside our Route_PlanLocked detour %ld "
        "(host %ld / %ld, the game's Route_PlanLocked %ld with PlanHierarchical %ld of it, our wrappers at most %ld); after the last "
        "one %ld (host %ld / %ld); our job end before the release %ld (max %ld us); the last plan ended with the sim waiting on "
        "+0x918 in %ld of %ld jobs, %ld ms from there to the release",
        (LONG)(c.whPartUs[WH_PRE] / 1000), (LONG)(c.whHostUs[WH_PRE] / 1000), c.whHostN[WH_PRE], c.whNoPlanN, (LONG)(c.whNoPlanUs / 1000),
        (LONG)(inUs / 1000), (LONG)(c.whHostUs[WH_IN] / 1000), c.whHostN[WH_IN], (LONG)(c.whGameUs / 1000), (LONG)(phUs / 1000),
        (LONG)((ourUs > 0 ? ourUs : 0) / 1000), (LONG)(c.whPartUs[WH_TAIL] / 1000), (LONG)(c.whHostUs[WH_TAIL] / 1000), c.whHostN[WH_TAIL],
        (LONG)(c.whEndUs / 1000), c.whEndMaxUs, c.whTailWaitN, c.whTailN, (LONG)(c.whTailWaitUs / 1000));
}
#endif
