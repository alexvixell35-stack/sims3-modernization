// docs/644: the candidate phase of PathPlanner_PlanHierarchical 0x65E0D0. Included by ts3native.cpp right after plandeadline.cpp
// (PdSess, g_pdOrig, KdCamHold), installed right after InstallPlanDeadline and before [RouteJobPhases] (RjpOwner chains through
// PccOwnerAt / g_wgcNext where our detours sit).
//
// Three default-off parts:
//  1. [RouteJobPhases] CandidateSplit=1, observe only: the planner's helpers timed per PlanHierarchical call (self time and
//     calls), any thread: closest waypoints 0x65BB90 (three tiers of 0x6595B0), same land component 0x65DF90 (two closest-
//     waypoint searches), same water body 0x650D80, LotEdgePortals_Choose 0x65BC20, LotEdgePortals_Gather 0x654CC0, the shore
//     helper 0x6443A0, LotSearch_Entry 0x640E00, and (docs/644 §8) the two searches under them: QuadTreeCellAStar 0x61E500
//     (reached through LotPlan 0x6410B0 -> 0x63F6E0 -> 0x632630 and LotSearch -> 0x633FA0 -> 0x632E40) and LotPlan itself,
//     whose inclusive time is also split by its call site in 0x65E0D0 (the lot legs of the planner's loops). A helper called
//     outside an open PlanHierarchical frame passes untimed. "rest" = PlanHierarchical's own self time: its code plus what is
//     not hooked here (allocator, the planner's smaller helpers). docs/663 §3 splits the old "rest" further: world A* 0x676260
//     (after [RouteEarlyOut]'s stub when it is on), WaterPath_Plan 0x671930 (after the plan deadline / water goal check stubs),
//     the path-node post pass 0x64E380 and PathNodes_Insert 0x629140, each self time. One line per call >= CandidateSplitLogMs
//     (default 100), 20 a minute; every 60 s the 8 slowest calls since the last counter line ("route candidate top").
//  2. [RouteAsync] CandidateCut=1: past the plan's deadline (the PdPlanBegin / IC session of plandeadline.cpp, or a worker job's
//     t_fbDeadline on the g_pdWkTid slot), a helper called from one of the listed planner sites answers "not found" at once.
//     Only a router whose scope is known unseen (2) or seen NPC (3); household, selected and unknown (0) never: counted as
//     exempt. Sites (return addresses into 0x65E0D0), every not-found path checked in the disassembly (docs/644 §3):
//       0x65BC20 from 0x65EF72 (start-lot exits; AL 0 -> 0x65F3C2 -> 0x65EB97, return 5) and 0x65F41B (goal-lot loop; AL 0 ->
//         0x65F697 -> closest-waypoint fallback -> 0x65F910 -> 0x65EBD6, return 5): the game's own early exit 0x65CAC0 writes
//         nothing and returns AL 0, ret 0x20;
//       0x65DF90 from 0x65F3DA (the loop's same-component test): AL 0 = "not known same", only flag 0x200 stays clear; ret 0x10;
//       0x65BB90 from 0x65F731, 0x65FA0E, 0x65FB55, 0x65FC97: AL 0 and the out vector (arg 8) end = begin, as 0x6595B0's own
//         first write leaves it; every caller there hands it a fresh empty vector and frees it after; ret 0x2C.
//     Calls of the same helpers from elsewhere (inside 0x65BC20 / 0x65DF90, scripts) are never cut.
//     docs/644 §8, the cell A* loop: the deadline stub at 0x61E500's entry cuts only searches that start past the deadline; one
//     that is running goes on to its node cap (the measured overrun). CandidateCut also replaces the loop's node-cap test at
//     0x61E76E (9 bytes: mov ecx,[esp+18h] / cmp [edi+24h],ecx / jae 0x61E7D9) by a stub that runs that test unchanged, then
//     every 32nd expansion reads the same deadline and scope rule; a cut jumps to 0x61E59C, the function's own "no start node /
//     open list empty" exit: it frees the two local vectors, releases the current and best node references (ebx / esi, null
//     tested there), [this+0x1C] = -1, AL 0, ret 0x18. Every caller already takes AL 0 (the entry stub's exit gives the same).
//  3. [RouteAsync] WaterGoalCheck: WaterPath_Plan 0x671930 answers "no path" without the search 0x620560 when that search
//     cannot succeed: the start cell is water in the boat tier's grid and the goal cell is not (plancand-rule.h). Exact (the
//     game's answer is the same failure, only sooner), so every scope. 2 = shadow: the search runs; a would-reject whose search
//     succeeds counts in wgcMismatch (must stay 0).
#include "plancand-rule.h"

// exported block ts3native_plancand (counters layout unchanged; docs/registry.md "(no slot)" row), offsets in this order
struct PlanCandCounters {
    volatile LONG ready;        // +0x00: bit 0 split sites, 1 cut sites, 2 water check (bit 3 = chained into the deadline stub)
    volatile LONG cutMode;      // +0x04: CandidateCut, runtime switch (1 on)
    volatile LONG cutPlans;     // +0x08: plans / jobs with at least one candidate cut
    volatile LONG cutCalls;     // +0x0C: helper calls answered "not found"
    volatile LONG cutExempt;    // +0x10: calls past the deadline at a cut site of a household / selected / unknown router (not cut)
    volatile LONG wgcMode;      // +0x14: WaterGoalCheck, runtime switch (1 reject, 2 shadow)
    volatile LONG wgcReject;    // +0x18: water plans answered (mode 1) or that would be (mode 2)
    volatile LONG wgcMismatch;  // +0x1C: mode 2, a would-reject whose search found a path (must stay 0)
    volatile LONG splitLong;    // +0x20: PlanHierarchical calls >= CandidateSplitLogMs
    volatile LONG lines;        // +0x24: log lines written (split, cut, mismatch)
};
extern "C" __declspec(dllexport) PlanCandCounters ts3native_plancand = {0, 0, 0, 0, 0, 0, 0, 0, 0, 0};

enum { PCC_PH, PCC_CLOSEST, PCC_SAMELAND, PCC_SAMEWATER, PCC_CHOOSE, PCC_GATHER, PCC_SHORE, PCC_LOTSEARCH, PCC_CELL, PCC_LOTPLAN,
       PCC_WORLD, PCC_WATER, PCC_POST, PCC_INSERT, PCC_HOST, PCC_N };
struct PccSite { DWORD at; u8 len; u8 pro[8]; const char* name; bool cut; };
// prologues: whole instructions, no relative operand (Steam 1.67); 0x65E0D0 / 0x640E00 equal kRjpSites' (docs/150 §7)
static const PccSite kPccSites[PCC_N] = {
    {TS3A(0x0065E0D0), 6, {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF0}, "PathPlanner_PlanHierarchical", false},
    {TS3A(0x0065BB90), 6, {0x51, 0x53, 0x8B, 0x5C, 0x24, 0x2C}, "closestWps", true},
    {TS3A(0x0065DF90), 5, {0x83, 0xEC, 0x10, 0x53, 0x56}, "sameLand", true},
    {TS3A(0x00650D80), 6, {0x56, 0x8B, 0xF1, 0x8B, 0x4E, 0x14}, "sameWater", false},
    {TS3A(0x0065BC20), 6, {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF0}, "choose", true},
    {TS3A(0x00654CC0), 6, {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF0}, "gather", false},
    {TS3A(0x006443A0), 8, {0x8B, 0x44, 0x24, 0x18, 0xD9, 0x44, 0x24, 0x14}, "shore", false},
    {TS3A(0x00640E00), 6, {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF8}, "lotSearch", false},
    // = kRjpSites' / kPdCellPro: with the plan deadline stub on 0x61E500 we take its trampoline pointer (g_pdOrig[0]) instead
    {TS3A(0x0061E500), 6, {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF0}, "cell", false},
    {TS3A(0x006410B0), 8, {0x8B, 0x44, 0x24, 0x34, 0xD9, 0x44, 0x24, 0x10}, "lotPlan", false},
    // docs/663 §3 (= kRjpSites' bytes; ret 0x1C / 0x24 / 0x14 / 8 checked): world A* (owner [RouteEarlyOut] g_eoAsOrig when on),
    // water (owner the plan deadline stub g_pdOrig[1] or the water goal check; installed after it), post pass, path insert
    {TS3A(0x00676260), 8, {0x83, 0xEC, 0x3C, 0x53, 0x8B, 0x5C, 0x24, 0x58}, "world", false},
    {TS3A(0x00671930), 7, {0x8B, 0x44, 0x24, 0x24, 0x83, 0xEC, 0x50}, "water", false},
    {TS3A(0x0064E380), 7, {0x80, 0x3D, 0x4C, 0x82, 0x15, 0x01, 0x00}, "post", false},  // absolute operand only
    {TS3A(0x00629140), 5, {0x83, 0xEC, 0x08, 0x53, 0x55}, "insert", false},
    // docs/667 §2: no hook of its own (at 0). A managed call made under the planner (the door ask of 0x64F4D3 and others) is
    // timed from ts3native.cpp's host-call hook: on the worker that is the wait for the sim thread to run it (Proxy), on the
    // sim thread the script itself. Before this site that time sat in the caller's self time (lotSearch / lotPlan / cell).
    {0, 0, {0}, "host", false},
};
// LotPlan's call sites in 0x65E0D0 (return addresses), for the per-leg split; slot PCC_LPN = any other caller (0x656A70's,
// called from 0x6611A1 / 0x661308, and callers outside the planner)
// PCC_IN(fn, off): an address inside a function the EA table maps, as its Steam 1.67 offset. No new table rows (the EA port
// skips this whole file: ts3a_noaddr), and every byte written there is checked first (Matches), so a moved body only SKIPs.
#define PCC_IN(fn, off) ((uint32_t)(TS3A(fn) + (off)))
enum { PCC_LPN = 7 };
static const uint32_t kPccLpRets[PCC_LPN] = {PCC_IN(0x0065E0D0, 0x2F7),  PCC_IN(0x0065E0D0, 0x1051), PCC_IN(0x0065E0D0, 0x14B7),
                                             PCC_IN(0x0065E0D0, 0x26C9), PCC_IN(0x0065E0D0, 0x29F3), PCC_IN(0x0065E0D0, 0x2CB8),
                                             PCC_IN(0x0065E0D0, 0x39D0)};
static const char* const kPccLpNames[PCC_LPN + 1] = {"65E3C2", "65F11C", "65F582", "660794", "660ABE", "660D83", "661A9B", "other"};
// cut sites: the return addresses of the calls inside 0x65E0D0 (0 = unused)
static const uint32_t kPccRets[PCC_N][4] = {
    {0},
    {TS3A(0x0065F736), TS3A(0x0065FA13), TS3A(0x0065FB5A), TS3A(0x0065FC9C)},
    {TS3A(0x0065F3DF)},
    {0},
    {TS3A(0x0065EF77), TS3A(0x0065F420)},
    {0}, {0}, {0}, {0}, {0}, {0}, {0}, {0}, {0},
};
static void* g_pccNext[PCC_N];  // our trampoline, or what an owner's pointer held (RJP takes our place in it later)
static bool g_pccSplit;         // CandidateSplit (ini, install time)
static LONG g_pccLogUs = 100000;

// ---- split timing (per thread) ----
// sub = LotPlan's call-site slot (kPccLpRets, PCC_LPN = other) or -1: its inclusive time (cell A* under it included)
struct PccFr { const void* mark; int site, sub; LONGLONG t0, child; };
struct PccTh {
    int n, ph;  // frames, open PlanHierarchical frames
    PccFr f[32];
    LONGLONG self[PCC_N], lp[PCC_LPN + 1];
    LONG calls[PCC_N], lpN[PCC_LPN + 1], cuts;
};
static __declspec(thread) PccTh t_pcc;
static volatile LONG64 g_pccTotUs[PCC_N];
static volatile LONG g_pccTotN[PCC_N], g_pccPh;
static volatile LONG g_pccMinute, g_pccLinesMin;

// the slowest PlanHierarchical calls (>= CandidateSplitLogMs) since the last 60 s counter line
enum { PCC_TOPN = 8 };
struct PccTop {
    LONG us, scope, cuts;
    DWORD code, route;
    bool worker;
    LONG selfUs[PCC_N], calls[PCC_N], lpUs[PCC_LPN + 1], lpN[PCC_LPN + 1];
};
static SRWLOCK g_pccTopLock = SRWLOCK_INIT;
static PccTop g_pccTop[PCC_TOPN];
static int g_pccTopN;

static bool PccLineOk() {  // 20 lines a minute over all threads (log only; a race lets one or two more through)
    const DWORD now = GetTickCount();
    if (now - (DWORD)g_pccMinute >= 60000) InterlockedExchange(&g_pccMinute, (LONG)now), InterlockedExchange(&g_pccLinesMin, 0);
    if (InterlockedIncrement(&g_pccLinesMin) > 20) return false;
    InterlockedIncrement(&ts3native_plancand.lines);
    return true;
}
static LONG PccUs(LONGLONG ticks) { return (LONG)(ticks * 1000000 / g_qpf.QuadPart); }

// docs/673 [RouteJobPhases] HoldProbe (workerhold.cpp), counting only: a frame opened / an outermost planner call ended
static void WhEnter(int site);
static void WhPlanEnd(LONG cands, LONG cellUs, LONG us, LONGLONG inclQ, DWORD code);

// mark = an address in the hook's frame; frames at or below it were left by an unwind (dropped, their time stays in the parent)
static bool PccEnter(int site, const void* mark, int sub = -1) {
    if (!g_pccSplit) return false;
    PccTh& t = t_pcc;
    while (t.n && t.f[t.n - 1].mark <= mark) {
        if (t.f[--t.n].site == PCC_PH) t.ph--;
    }
    if (site != PCC_PH && !t.ph) return false;  // outside a planner call (script callers): untimed
    if (t.n == 32) return false;
    if (site == PCC_PH && !t.ph) {
        memset(t.self, 0, sizeof(t.self)), memset(t.lp, 0, sizeof(t.lp));
        memset(t.calls, 0, sizeof(t.calls)), memset(t.lpN, 0, sizeof(t.lpN));
        t.cuts = 0;
    }
    PccFr& f = t.f[t.n++];
    f.mark = mark, f.site = site, f.sub = sub, f.child = 0;
    if (site == PCC_PH) t.ph++;
    LARGE_INTEGER q;
    QueryPerformanceCounter(&q);
    f.t0 = q.QuadPart;
    WhEnter(site);
    return true;
}

// docs/667 §2: the host-call hook's frame (ts3native.cpp HostCallHook / InvokeHook), any thread; false outside a planner call
static void PccLeave(const void* mark, DWORD r);
static bool PccHostEnter(const void* mark) { return PccEnter(PCC_HOST, mark); }
static void PccHostLeave(const void* mark) { PccLeave(mark, 0); }

// "cell 12/3, lotPlan 2/1, ...; lot legs by call 65F11C 140/1, ..." (ms/calls; legs only where called)
static int PccFmt(char* b, int room, const LONG* selfUs, const LONG* calls, const LONG* lpUs, const LONG* lpN) {
    int at = 0;
    for (int i = 1; i < PCC_N && at < room; i++)
        at += sprintf_s(b + at, room - at, "%s%s %ld/%ld", i > 1 ? ", " : "", kPccSites[i].name, selfUs[i] / 1000, calls[i]);
    bool any = false;
    for (int i = 0; i <= PCC_LPN && at < room; i++) {
        if (!lpN[i]) continue;
        at += sprintf_s(b + at, room - at, "%s%s %ld/%ld", any ? ", " : " (self ms/calls); lot legs by call ", kPccLpNames[i], lpUs[i] / 1000, lpN[i]);
        any = true;
    }
    if (at < room) at += sprintf_s(b + at, room - at, any ? " (incl. ms/calls)" : " (self ms/calls); no lot legs");
    return at;
}

static void PccTopLog(const PccTop& e, int rank, int n) {
    char b[700];
    PccFmt(b, sizeof(b), e.selfUs, e.calls, e.lpUs, e.lpN);
    Log("route candidate top %d/%d: PlanHierarchical %ld ms code %lu, %s, route %08lX, scope %s: %s; rest %ld ms "
        "(own code + allocator + unhooked helpers); cut %ld", rank, n, e.us / 1000, e.code, e.worker ? "worker job" : "sync", e.route,
        KdScopeTag(e.scope), b, e.selfUs[PCC_PH] / 1000, e.cuts);
}

static LONG PccScopeNow(bool* worker);
static void PccPhEnd(const PccTh& t, LONGLONG incl, DWORD code) {
    for (int i = 0; i < PCC_N; i++)
        if (t.calls[i]) InterlockedExchangeAdd64(&g_pccTotUs[i], PccUs(t.self[i])), InterlockedExchangeAdd(&g_pccTotN[i], t.calls[i]);
    InterlockedIncrement(&g_pccPh);
    const LONG us = PccUs(incl);
    WhPlanEnd(t.calls[PCC_CHOOSE], PccUs(t.self[PCC_CELL]), us, incl, code);
    if (us < g_pccLogUs) return;
    InterlockedIncrement(&ts3native_plancand.splitLong);
    PccTop e;
    bool worker = false;
    e.us = us, e.code = code, e.cuts = t.cuts;
    e.scope = PccScopeNow(&worker);
    e.worker = worker;
    e.route = worker ? g_async.handle : (t_pd.open ? t_pd.handle : 0);
    for (int i = 0; i < PCC_N; i++) e.selfUs[i] = PccUs(t.self[i]), e.calls[i] = t.calls[i];
    for (int i = 0; i <= PCC_LPN; i++) e.lpUs[i] = PccUs(t.lp[i]), e.lpN[i] = t.lpN[i];
    AcquireSRWLockExclusive(&g_pccTopLock);
    int slot = g_pccTopN < PCC_TOPN ? g_pccTopN++ : -1;
    if (slot < 0) {  // full: replace the fastest kept call if this one is slower (one pass, 8 entries)
        int lo = 0;
        for (int i = 1; i < PCC_TOPN; i++)
            if (g_pccTop[i].us < g_pccTop[lo].us) lo = i;
        if (g_pccTop[lo].us < us) slot = lo;
    }
    if (slot >= 0) g_pccTop[slot] = e;
    ReleaseSRWLockExclusive(&g_pccTopLock);
    if (!PccLineOk()) return;
    char b[700];
    PccFmt(b, sizeof(b), e.selfUs, e.calls, e.lpUs, e.lpN);
    Log("route candidate split: PlanHierarchical %ld ms code %lu, %s, route %08lX, scope %s: %s; rest %ld ms "
        "(own code + allocator + unhooked helpers); cut %ld", us / 1000, code, worker ? "worker job" : "sync", e.route,
        KdScopeTag(e.scope), b, e.selfUs[PCC_PH] / 1000, t.cuts);
}

static void PccLeave(const void* mark, DWORD r) {
    PccTh& t = t_pcc;
    int i = t.n - 1;
    while (i >= 0 && t.f[i].mark != mark) i--;
    if (i < 0) return;  // dropped meanwhile
    LARGE_INTEGER q;
    QueryPerformanceCounter(&q);
    while (t.n - 1 > i) {
        if (t.f[--t.n].site == PCC_PH) t.ph--;  // frames above it: left without a leave
    }
    const PccFr f = t.f[i];
    t.n = i;
    const LONGLONG incl = q.QuadPart - f.t0;
    t.self[f.site] += incl - f.child;
    t.calls[f.site]++;
    if (f.sub >= 0 && f.sub <= PCC_LPN) t.lp[f.sub] += incl, t.lpN[f.sub]++;
    if (t.n) t.f[t.n - 1].child += incl;
    if (f.site == PCC_PH && --t.ph == 0) PccPhEnd(t, incl, r);
}

// the 8 slowest calls kept since the last call, slowest first; the list starts empty again
static void PccTopFlush() {
    PccTop top[PCC_TOPN];
    AcquireSRWLockExclusive(&g_pccTopLock);
    const int n = g_pccTopN;
    memcpy(top, g_pccTop, sizeof(PccTop) * n);
    g_pccTopN = 0;
    ReleaseSRWLockExclusive(&g_pccTopLock);
    for (int i = 1; i < n; i++) {  // insertion sort, slowest first
        const PccTop e = top[i];
        int j = i;
        for (; j > 0 && top[j - 1].us < e.us; j--) top[j] = top[j - 1];
        top[j] = e;
    }
    for (int i = 0; i < n; i++) {
        PccTopLog(top[i], i + 1, n);
        InterlockedIncrement(&ts3native_plancand.lines);
    }
}

// ---- candidate cut ----
static __declspec(thread) LONGLONG t_pccCutKey;  // t0 of the plan / job whose first cut was logged

// router scope of the running plan: the sim's session (read at its open) or the worker's job; *worker = the job's
static LONG PccScopeNow(bool* worker) {
    const DWORD tid = GetCurrentThreadId();
    if (t_pd.open && tid == g_pdTid) return *worker = false, t_pd.scope;
    if (tid == g_pdWkTid) return *worker = true, t_hcScope;
    *worker = t_jobT0 != 0;  // ServeAsync sets it for every job on the worker
    return *worker ? t_hcScope : 0;
}

// true = past this plan's deadline (KdCamHold as the other exits: a known-unseen route in view runs on to its ceiling);
// *scope, *t0, *worker for the rule and the log. Plans with no deadline on this thread: false
static bool PccPast(LONG* scope, LONGLONG* t0, bool* worker, LONGLONG* now) {
    const Ts3nCounters& k = ts3native_counters;
    const DWORD tid = GetCurrentThreadId();
    PdSess& s = t_pd;
    if (s.open && tid == g_pdTid) {
        if (k.pdMode != 1 && !t_ufSess) return false;
        *now = PdNow();
        if (*now < s.dl) return false;
        *scope = s.scope, *t0 = s.t0, *worker = false;
        return true;
    }
    if (tid == g_pdWkTid && t_fbDeadline && (k.fbjdMode == 1 || t_scProbe || t_kdKind || t_ufJob)) {
        *now = PdNow();
        if (*now < t_fbDeadline) return false;
        *scope = t_hcScope, *t0 = t_jobT0, *worker = true;
        return true;
    }
    return false;
}

// past the deadline and a cuttable scope: the plan is marked cut and true is returned (ret = the call named in the log line)
static bool PccCutNow(int site, DWORD ret) {
    PlanCandCounters& c = ts3native_plancand;
    LONG scope = 0;
    LONGLONG t0 = 0, now = 0;
    bool worker = false;
    const bool past = PccPast(&scope, &t0, &worker, &now);
    const int r = PccRule(c.cutMode, true, past, scope);
    if (r == PCC_EXEMPT) InterlockedIncrement(&c.cutExempt);
    if (r != PCC_CUT) return false;
    if (KdCamHold(now, worker ? &t_fbDeadline : &t_pd.dl)) return false;  // camera recheck (KdCutCamRecheck): held, runs on
    InterlockedIncrement(&c.cutCalls);
    if (t_pcc.ph) t_pcc.cuts++;
    // the plan must read as cut where the other exits' cuts are read: a worker job as capped (KindDeadlines backoff, routefail
    // and UnseenFallback recovery, long-job lines: t_fbCapHit, ServeAsync), a sync session in its deadline line (counted with
    // "world A* capped"; this file's own line names the helper)
    if (worker) {
        if (t_jobT0 && !t_jobCapAt) t_jobCapAt = now;
        t_fbCapHit = 1;
    } else {
        t_pd.capped++;
    }
    if (t_pccCutKey == t0) return true;
    t_pccCutKey = t0;
    InterlockedIncrement(&c.cutPlans);
    if (PccLineOk())
        Log("route candidate cut: %s after %ld ms, scope %s; %s, route %08lX, call 0x%08lX", kPccSites[site].name,
            PccUs(now - t0) / 1000, KdScopeTag(scope), worker ? "worker job" : "sync plan", worker ? g_async.handle : t_pd.handle, ret);
    return true;
}
static bool PccCut(int site, DWORD ret) {
    if (ts3native_plancand.cutMode != 1 || !PccRetIn(ret, kPccRets[site], 4)) return false;
    return PccCutNow(site, ret);
}

// ---- cell A* loop (docs/644 §8): the node-cap test at 0x61E76E, then the deadline every 32nd expansion ----
static DWORD g_pccCellBack, g_pccCellCap, g_pccCellFail;  // 0x61E777 (next test), 0x61E7D9 (node cap), 0x61E59C (no path)
static __declspec(thread) LONG t_pccCellTick;
static bool g_pccCellLoop;
// eax / ecx / edx and the flags are dead at all three targets (each writes them before a read), so the C call may clobber them
static int __cdecl PccCellLoopCut(DWORD /*node*/) {  // the popped node (esi), pushed by the stub
    if (++t_pccCellTick & 31) return 0;
    if (ts3native_plancand.cutMode != 1) return 0;
    return PccCutNow(PCC_CELL, PCC_IN(0x0061E500, 0x26E)) ? 1 : 0;
}
static __declspec(naked) void PccCellLoopStub() {
    __asm {
        mov ecx, dword ptr [esp + 0x18]  // the game's node cap (0xFA00 or 0x40002), esp as at 0x61E76E (jumped here, nothing pushed)
        cmp dword ptr [edi + 0x24], ecx
        jae cap
        push esi                         // the node popped this turn (cdecl keeps esi / edi / ebx)
        call PccCellLoopCut
        add esp, 4
        test eax, eax
        jnz cut
        jmp dword ptr [g_pccCellBack]
    cap:
        jmp dword ptr [g_pccCellCap]
    cut:
        jmp dword ptr [g_pccCellFail]
    }
}
static void PccInstallCellLoop() {
    static const u8 kWant[9] = {0x8B, 0x4C, 0x24, 0x18, 0x39, 0x4F, 0x24, 0x73, 0x62};
    // the exits' own bytes: 0x61E7D9 test ebx,ebx; 0x61E59C lea ecx,[esp+30h]
    static const u8 kCap[2] = {0x85, 0xDB}, kFail[4] = {0x8D, 0x4C, 0x24, 0x30};
    const DWORD at = PCC_IN(0x0061E500, 0x26E);
    u8* p = (u8*)(uintptr_t)at;
    if (!at || !Matches(p, kWant, sizeof(kWant)) || !Matches((const void*)(uintptr_t)PCC_IN(0x0061E500, 0x2D9), kCap, sizeof(kCap)) ||
        !Matches((const void*)(uintptr_t)PCC_IN(0x0061E500, 0x9C), kFail, sizeof(kFail))) {
        Log("SKIP route candidate cut: cell A* loop test at 0x%08lX differs (or its exits do), entry cut only", at);
        return;
    }
    g_pccCellBack = PCC_IN(0x0061E500, 0x277), g_pccCellCap = PCC_IN(0x0061E500, 0x2D9), g_pccCellFail = PCC_IN(0x0061E500, 0x9C);
    u8 j[9] = {0xE9, 0, 0, 0, 0, 0x90, 0x90, 0x90, 0x90};
    const LONG rel = (LONG)((uintptr_t)PccCellLoopStub - (at + 5));
    memcpy(j + 1, &rel, 4);
    g_pccCellLoop = Write(p, j, sizeof(j));
    Log("%s route candidate cut: cell A* loop stub at 0x%08lX (deadline every 32nd expansion; exit 0x%08lX)",
        g_pccCellLoop ? "OK  " : "FAIL", at, g_pccCellFail);
}

// ---- hooks: thiscall entries as __fastcall (edx unused, same stack and callee cleanup); EAX passed back whole ----
typedef DWORD(__fastcall* Pcc4Fn)(void*, void*, DWORD, DWORD, DWORD, DWORD);
typedef DWORD(__fastcall* Pcc6Fn)(void*, void*, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD);
typedef DWORD(__fastcall* Pcc8Fn)(void*, void*, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD);
typedef DWORD(__fastcall* Pcc11Fn)(void*, void*, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD);
typedef DWORD(__fastcall* Pcc12Fn)(void*, void*, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD);
#define PCC_RET ((DWORD)(uintptr_t)_ReturnAddress())
#define PCC_TIMED(site, call)                          \
    const void* const mark = _AddressOfReturnAddress(); \
    const bool on = PccEnter(site, mark);              \
    const DWORD r = call;                              \
    if (on) PccLeave(mark, r);                         \
    return r

static DWORD __fastcall PccPhHook(void* self, void* edx, DWORD a1, DWORD a2, DWORD a3, DWORD a4, DWORD a5, DWORD a6, DWORD a7, DWORD a8,
                                  DWORD a9, DWORD a10, DWORD a11, DWORD a12) {
    PCC_TIMED(PCC_PH, ((Pcc12Fn)g_pccNext[PCC_PH])(self, edx, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11, a12));
}
static DWORD __fastcall PccClosestHook(void* self, void* edx, DWORD a1, DWORD a2, DWORD a3, DWORD a4, DWORD a5, DWORD a6, DWORD a7,
                                       DWORD a8, DWORD a9, DWORD a10, DWORD a11) {
    if (PccCut(PCC_CLOSEST, PCC_RET)) {
        DWORD* v = (DWORD*)(uintptr_t)a8;  // out vector {begin, end, cap, name}: empty, as 0x6595B0's first write leaves it
        v[1] = v[0];
        return 0;
    }
    PCC_TIMED(PCC_CLOSEST, ((Pcc11Fn)g_pccNext[PCC_CLOSEST])(self, edx, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11));
}
static DWORD __fastcall PccSameLandHook(void* self, void* edx, DWORD a1, DWORD a2, DWORD a3, DWORD a4) {
    if (PccCut(PCC_SAMELAND, PCC_RET)) return 0;
    PCC_TIMED(PCC_SAMELAND, ((Pcc4Fn)g_pccNext[PCC_SAMELAND])(self, edx, a1, a2, a3, a4));
}
static DWORD __fastcall PccSameWaterHook(void* self, void* edx, DWORD a1, DWORD a2, DWORD a3, DWORD a4) {
    PCC_TIMED(PCC_SAMEWATER, ((Pcc4Fn)g_pccNext[PCC_SAMEWATER])(self, edx, a1, a2, a3, a4));
}
static DWORD __fastcall PccChooseHook(void* self, void* edx, DWORD a1, DWORD a2, DWORD a3, DWORD a4, DWORD a5, DWORD a6, DWORD a7, DWORD a8) {
    if (PccCut(PCC_CHOOSE, PCC_RET)) return 0;  // 0x65CAC0: AL 0, nothing written
    PCC_TIMED(PCC_CHOOSE, ((Pcc8Fn)g_pccNext[PCC_CHOOSE])(self, edx, a1, a2, a3, a4, a5, a6, a7, a8));
}
static DWORD __fastcall PccGatherHook(void* self, void* edx, DWORD a1, DWORD a2, DWORD a3, DWORD a4, DWORD a5, DWORD a6, DWORD a7, DWORD a8) {
    PCC_TIMED(PCC_GATHER, ((Pcc8Fn)g_pccNext[PCC_GATHER])(self, edx, a1, a2, a3, a4, a5, a6, a7, a8));
}
static DWORD __fastcall PccShoreHook(void* self, void* edx, DWORD a1, DWORD a2, DWORD a3, DWORD a4, DWORD a5, DWORD a6, DWORD a7, DWORD a8) {
    PCC_TIMED(PCC_SHORE, ((Pcc8Fn)g_pccNext[PCC_SHORE])(self, edx, a1, a2, a3, a4, a5, a6, a7, a8));
}
static DWORD __fastcall PccLotSearchHook(void* self, void* edx, DWORD a1, DWORD a2, DWORD a3, DWORD a4, DWORD a5, DWORD a6) {
    PCC_TIMED(PCC_LOTSEARCH, ((Pcc6Fn)g_pccNext[PCC_LOTSEARCH])(self, edx, a1, a2, a3, a4, a5, a6));
}
static DWORD __fastcall PccCellHook(void* self, void* edx, DWORD a1, DWORD a2, DWORD a3, DWORD a4, DWORD a5, DWORD a6) {
    const void* const mark = _AddressOfReturnAddress();
    const bool on = PccEnter(PCC_CELL, mark);
    const DWORD r = ((Pcc6Fn)g_pccNext[PCC_CELL])(self, edx, a1, a2, a3, a4, a5, a6);
    if (on) PccLeave(mark, r);
    return r;
}
// LotPlan 0x6410B0: thiscall, 13 stack args (ret 0x34; float args pass through as their bits)
typedef DWORD(__fastcall* Pcc13Fn)(void*, void*, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD);
static DWORD __fastcall PccLotPlanHook(void* self, void* edx, DWORD a1, DWORD a2, DWORD a3, DWORD a4, DWORD a5, DWORD a6, DWORD a7,
                                       DWORD a8, DWORD a9, DWORD a10, DWORD a11, DWORD a12, DWORD a13) {
    const DWORD ret = PCC_RET;
    int sub = PCC_LPN;
    for (int i = 0; i < PCC_LPN; i++)
        if (kPccLpRets[i] == ret) sub = i;
    const void* const mark = _AddressOfReturnAddress();
    const bool on = PccEnter(PCC_LOTPLAN, mark, sub);
    const DWORD r = ((Pcc13Fn)g_pccNext[PCC_LOTPLAN])(self, edx, a1, a2, a3, a4, a5, a6, a7, a8, a9, a10, a11, a12, a13);
    if (on) PccLeave(mark, r);
    return r;
}
typedef DWORD(__fastcall* Pcc2Fn)(void*, void*, DWORD, DWORD);
typedef DWORD(__fastcall* Pcc5Fn)(void*, void*, DWORD, DWORD, DWORD, DWORD, DWORD);
typedef DWORD(__fastcall* Pcc7Fn)(void*, void*, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD);
typedef DWORD(__fastcall* Pcc9Fn)(void*, void*, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD);
// docs/676 [WorldPrecompute] Probe (worldpre.cpp), counting only: an outermost world A* search and its key (a1 the waypoint
// graph R, a3 start ref, a4 goal ref, a5 cost mode, a6 the search context)
static void WxWorldEnter(const void* mark, const u8* graph, const DWORD* start, const DWORD* goal, DWORD mode, const DWORD* opts);
static void WxWorldLeave(const void* mark, DWORD r);
static DWORD __fastcall PccWorldHook(void* self, void* edx, DWORD a1, DWORD a2, DWORD a3, DWORD a4, DWORD a5, DWORD a6, DWORD a7) {
    const void* const mark = _AddressOfReturnAddress();
    WxWorldEnter(mark, (const u8*)(uintptr_t)a1, (const DWORD*)(uintptr_t)a3, (const DWORD*)(uintptr_t)a4, a5, (const DWORD*)(uintptr_t)a6);
    const bool on = PccEnter(PCC_WORLD, mark);
    const DWORD r = ((Pcc7Fn)g_pccNext[PCC_WORLD])(self, edx, a1, a2, a3, a4, a5, a6, a7);
    if (on) PccLeave(mark, r);
    WxWorldLeave(mark, r);
    return r;
}
static DWORD __fastcall PccWaterHook(void* self, void* edx, DWORD a1, DWORD a2, DWORD a3, DWORD a4, DWORD a5, DWORD a6, DWORD a7, DWORD a8,
                                     DWORD a9) {
    PCC_TIMED(PCC_WATER, ((Pcc9Fn)g_pccNext[PCC_WATER])(self, edx, a1, a2, a3, a4, a5, a6, a7, a8, a9));
}
static DWORD __fastcall PccPostHook(void* self, void* edx, DWORD a1, DWORD a2, DWORD a3, DWORD a4, DWORD a5) {
    PCC_TIMED(PCC_POST, ((Pcc5Fn)g_pccNext[PCC_POST])(self, edx, a1, a2, a3, a4, a5));
}
static DWORD __fastcall PccInsertHook(void* self, void* edx, DWORD a1, DWORD a2) {
    PCC_TIMED(PCC_INSERT, ((Pcc2Fn)g_pccNext[PCC_INSERT])(self, edx, a1, a2));
}
static void* const kPccHooks[PCC_N] = {(void*)PccPhHook,   (void*)PccClosestHook, (void*)PccSameLandHook, (void*)PccSameWaterHook,
                                       (void*)PccChooseHook, (void*)PccGatherHook,  (void*)PccShoreHook,    (void*)PccLotSearchHook,
                                       (void*)PccCellHook,   (void*)PccLotPlanHook, (void*)PccWorldHook,     (void*)PccWaterHook,
                                       (void*)PccPostHook,   (void*)PccInsertHook};

// ---- water goal check ----
typedef DWORD(__fastcall* WgcFn)(void*, void*, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD);
static void* g_wgcNext;   // our trampoline, or the deadline stub's trampoline we took the place of
static bool g_wgcOwn;     // g_wgcNext is our own detour's trampoline (RjpOwner chains into it)
static DWORD g_wgcMinute, g_wgcLines;

static bool WgcLoad(const u8* w, const u8* route, WgcView* v) {
    if (!w || !route) return false;
    const DWORD* const* rf = (const DWORD* const*)(route + 0x8C);
    if (!*rf) return false;
    v->cells = *(const DWORD*)(w + 0x0C);
    v->body = *(const u8* const*)(w + 0x1C);
    v->bodyW = *(const DWORD*)(w + 0x04);
    v->gw = *(const DWORD*)(w + 0x7A0), v->gh = *(const DWORD*)(w + 0x7A4);
    v->routeFlags = **rf;
    return true;
}

// args as 0x671930 reads them (docs/644 §4): a2 route, a3 start cell, a6 goal cell, a9 out vector
static DWORD __fastcall WgcHook(void* self, void* edx, DWORD a1, DWORD a2, DWORD a3, DWORD a4, DWORD a5, DWORD a6, DWORD a7, DWORD a8, DWORD a9) {
    PlanCandCounters& c = ts3native_plancand;
    const LONG mode = c.wgcMode;
    bool reject = false;
    if (mode == 1 || mode == 2) {
        WgcView v;
        const u8* grid[5] = {0};
        if (WgcLoad((const u8*)self, (const u8*)(uintptr_t)a2, &v)) {
            const u8* d = *(const u8* const*)((const u8*)self + 0x7B0);  // 16-byte descriptors, data pointer first
            for (int i = 0; d && i < 5; i++) grid[i] = *(const u8* const*)(d + i * 16);
            v.grid = grid;
            reject = WgcWouldReject(v, a3, a6);
        }
    }
    if (reject) {
        InterlockedIncrement(&c.wgcReject);
        if (mode == 1) {
            DWORD* out = (DWORD*)(uintptr_t)a9;  // as 0x671930's first write: end = begin; AL 0 (exit 0x671B98)
            out[1] = out[0];
            return 0;
        }
    }
    const DWORD r = ((WgcFn)g_wgcNext)(self, edx, a1, a2, a3, a4, a5, a6, a7, a8, a9);
    if (reject && (u8)r) {
        InterlockedIncrement(&c.wgcMismatch);
        if (PccLineOk()) Log("WATER GOAL CHECK MISMATCH: start cell %lu goal %lu would be rejected, the search found a path (route flags 0x%lX)",
                             a3, a6, a2 ? **(const DWORD* const*)((const u8*)(uintptr_t)a2 + 0x8C) : 0);
    }
    return r;
}

// RjpOwner (routephases.cpp): our trampoline pointer at a [RouteJobPhases] site we detoured, NULL = not ours
static void** PccOwnerAt(DWORD at) {
    for (int i = 0; i < PCC_N; i++)
        if (kPccSites[i].at == at && g_pccNext[i]) return &g_pccNext[i];
    return NULL;
}
static void** WgcOwner() { return g_wgcOwn && g_wgcNext ? &g_wgcNext : NULL; }

static void InstallPlanCand(const wchar_t* ini) {
    PlanCandCounters& c = ts3native_plancand;
    const bool split = GetPrivateProfileIntW(L"RouteJobPhases", L"CandidateSplit", 0, ini) != 0;
    const int cut = Clamp(GetPrivateProfileIntW(L"RouteAsync", L"CandidateCut", 0, ini), 0, 1);
    const int wgc = Clamp(GetPrivateProfileIntW(L"RouteAsync", L"WaterGoalCheck", 0, ini), 0, 2);
    if (!split && !cut && !wgc) { Log("route candidate cut off (CandidateCut=0, WaterGoalCheck=0, [RouteJobPhases] CandidateSplit=0)"); return; }
#ifdef TS3A_TABLE
    if (ts3a_noaddr) { Log("SKIP route candidate cut / split / water goal check: no EA 1.69 address, docs/644"); return; }
#endif
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
    g_pccLogUs = Clamp(GetPrivateProfileIntW(L"RouteJobPhases", L"CandidateSplitLogMs", 100, ini), 1, 60000) * 1000;
    if (split || cut) {
        int hooked = 0, want = 0;
        for (int i = 0; i < PCC_N; i++) {
            if (!kPccSites[i].at) continue;  // host: timed from the host-call hook
            if (!split && !kPccSites[i].cut) continue;
            if (i == PCC_WATER) continue;  // after the water goal check below (it may need the entry's own bytes)
            want++;
            if (i == PCC_WORLD && g_eoAsOrig) {  // [RouteEarlyOut] owns the entry: we run after its stub (its request scope first)
                g_pccNext[i] = (void*)g_eoAsOrig;
                InterlockedExchangePointer((void* volatile*)&g_eoAsOrig, kPccHooks[i]);
                hooked++;
                continue;
            }
            if (i == PCC_CELL && g_pdOrig[0]) {  // the plan deadline stub owns the entry: we run after it (its skip exit first)
                g_pccNext[i] = g_pdOrig[0];
                InterlockedExchangePointer(&g_pdOrig[0], kPccHooks[i]);
                hooked++;
                continue;
            }
            char name[80];
            sprintf_s(name, "%s (candidate %s)", kPccSites[i].name, split ? "split" : "cut");
            if (InstallDetour(name, kPccSites[i].at, kPccSites[i].pro, kPccSites[i].len, kPccHooks[i], &g_pccNext[i])) hooked++;
        }
        g_pccSplit = split && g_pccNext[PCC_PH] != NULL;
        const bool cutOk = g_pccNext[PCC_CLOSEST] && g_pccNext[PCC_SAMELAND] && g_pccNext[PCC_CHOOSE];
        c.ready |= (g_pccSplit ? 1 : 0) | (cutOk ? 2 : 0);
        c.cutMode = cut && cutOk ? 1 : 0;
        Log("%s route candidate %s: %d of %d helper sites hooked; CandidateCut=%d (mode %ld: past the plan's deadline, unseen and seen-NPC "
            "routers only), split lines >= %ld ms", hooked == want ? "OK  " : "PART", split ? "split + cut" : "cut", hooked, want, cut,
            c.cutMode, g_pccLogUs / 1000);
        if (c.cutMode == 1) PccInstallCellLoop();
        if (cut && !ts3native_counters.pdReady && !g_kdIni && !g_ufIni)
            Log("route candidate cut: no plan deadline is on (PlanDeadlineMs, KindDeadlines, UnseenFallback, FbJobDeadline): nothing to cut at");
    }
    if (wgc) {
        static const u8 kWgcPro[] = {0x8B, 0x44, 0x24, 0x24, 0x83, 0xEC, 0x50};  // = kPdWaterPro
        const DWORD at = TS3A(0x00671930);
        if (Matches((const void*)(uintptr_t)at, kWgcPro, sizeof(kWgcPro))) {
            g_wgcOwn = InstallDetour("WaterPath_Plan (water goal check)", at, kWgcPro, sizeof(kWgcPro), (void*)WgcHook, &g_wgcNext) != NULL;
        } else if (g_pdOrig[1]) {  // the deadline stub owns the entry: we run after it (its skip exit first)
            g_wgcNext = g_pdOrig[1];
            InterlockedExchangePointer(&g_pdOrig[1], (void*)WgcHook);
            c.ready |= 8;
        }
        if (g_wgcNext) c.ready |= 4, c.wgcMode = wgc;
        Log("%s water goal check: WaterGoalCheck=%d (%s), %s", g_wgcNext ? "OK  " : "FAIL", wgc, wgc == 2 ? "shadow, search still runs" : "reject",
            g_wgcOwn ? "own detour" : g_wgcNext ? "after the plan deadline stub" : "0x671930 bytes differ and no known owner");
    }
    if (g_pccSplit) {  // docs/663 §3: the water site last: after the deadline stub, else after the water goal check, else our own
        const PccSite& w = kPccSites[PCC_WATER];
        void** owner = g_pdOrig[1] ? &g_pdOrig[1] : g_wgcOwn && g_wgcNext ? &g_wgcNext : NULL;
        if (owner) {
            g_pccNext[PCC_WATER] = *owner;
            InterlockedExchangePointer(owner, kPccHooks[PCC_WATER]);
        } else if (Matches((const void*)(uintptr_t)w.at, w.pro, w.len)) {
            InstallDetour("water (candidate split)", w.at, w.pro, w.len, kPccHooks[PCC_WATER], &g_pccNext[PCC_WATER]);
        }
        Log("%s route candidate split water: %s", g_pccNext[PCC_WATER] ? "OK  " : "SKIP",
            g_pccNext[PCC_WATER] ? (owner ? "inside its owner's stub" : "own detour") : "0x671930 bytes differ and no known owner");
    }
}

// the 60 s counter lines (ts3native.cpp)
static void PccLogCounters() {
    const PlanCandCounters& c = ts3native_plancand;
    if (!c.ready) return;
    Log("route candidate: cut mode %ld plans %ld calls %ld exempt %ld; water goal check mode %ld rejects %ld mismatch %ld; split calls %ld long %ld; cell A* loop cut %s",
        c.cutMode, c.cutPlans, c.cutCalls, c.cutExempt, c.wgcMode, c.wgcReject, c.wgcMismatch, g_pccPh, c.splitLong, g_pccCellLoop ? "on" : "off");
    if (!g_pccSplit) return;
    char b[400];
    int at = 0;
    for (int i = 1; i < PCC_N; i++)
        at += sprintf_s(b + at, sizeof(b) - at, "%s%s %lld/%ld", i > 1 ? ", " : "", kPccSites[i].name, (long long)(g_pccTotUs[i] / 1000), g_pccTotN[i]);
    Log("route candidate split totals: %s (self ms/calls); PlanHierarchical rest %lld ms over %ld calls", b, (long long)(g_pccTotUs[PCC_PH] / 1000), g_pccTotN[PCC_PH]);
    PccTopFlush();  // docs/644 §8: the 8 slowest calls of the last minute, with their lot legs by call site
}
