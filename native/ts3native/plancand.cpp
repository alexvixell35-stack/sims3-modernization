// docs/643: the candidate phase of PathPlanner_PlanHierarchical 0x65E0D0. Included by ts3native.cpp right after plandeadline.cpp
// (PdSess, g_pdOrig, KdCamHold), installed right after InstallPlanDeadline and before [RouteJobPhases] (RjpOwner chains through
// PccOwnerAt / g_wgcNext where our detours sit).
//
// Three default-off parts:
//  1. [RouteJobPhases] CandidateSplit=1, observe only: the planner's helpers timed per PlanHierarchical call (self time and
//     calls), any thread: closest waypoints 0x65BB90 (three tiers of 0x6595B0), same land component 0x65DF90 (two closest-
//     waypoint searches), same water body 0x650D80, LotEdgePortals_Choose 0x65BC20, LotEdgePortals_Gather 0x654CC0, the shore
//     helper 0x6443A0 and LotSearch_Entry 0x640E00. A helper called outside an open PlanHierarchical frame passes untimed.
//     "rest" = PlanHierarchical's own self time: its code plus everything not hooked here (world A*, cell A*, water, lot legs,
//     allocator); run with SyncPlans=1 for those. One line per call >= CandidateSplitLogMs (default 100), 20 a minute.
//  2. [RouteAsync] CandidateCut=1: past the plan's deadline (the PdPlanBegin / IC session of plandeadline.cpp, or a worker job's
//     t_fbDeadline on the g_pdWkTid slot), a helper called from one of the listed planner sites answers "not found" at once.
//     Only a router whose scope is known unseen (2) or seen NPC (3); household, selected and unknown (0) never: counted as
//     exempt. Sites (return addresses into 0x65E0D0), every not-found path checked in the disassembly (docs/643 §3):
//       0x65BC20 from 0x65EF72 (start-lot exits; AL 0 -> 0x65F3C2 -> 0x65EB97, return 5) and 0x65F41B (goal-lot loop; AL 0 ->
//         0x65F697 -> closest-waypoint fallback -> 0x65F910 -> 0x65EBD6, return 5): the game's own early exit 0x65CAC0 writes
//         nothing and returns AL 0, ret 0x20;
//       0x65DF90 from 0x65F3DA (the loop's same-component test): AL 0 = "not known same", only flag 0x200 stays clear; ret 0x10;
//       0x65BB90 from 0x65F731, 0x65FA0E, 0x65FB55, 0x65FC97: AL 0 and the out vector (arg 8) end = begin, as 0x6595B0's own
//         first write leaves it; every caller there hands it a fresh empty vector and frees it after; ret 0x2C.
//     Calls of the same helpers from elsewhere (inside 0x65BC20 / 0x65DF90, scripts) are never cut.
//  3. [RouteAsync] WaterGoalCheck: WaterPath_Plan 0x671930 answers "no path" without the search 0x620560 when that search
//     cannot succeed: the start cell is water in the boat tier's grid and the goal cell is not (plancand-rule.h). Exact (the
//     game's answer is the same failure, only sooner), so every scope. 2 = shadow: the search runs; a would-reject whose search
//     succeeds counts in wgcMismatch (must stay 0).
#include "plancand-rule.h"

// exported block (counters layout unchanged); slots 4050-4059 in docs/registry.md, one per field in this order
struct PlanCandCounters {
    volatile LONG ready;        // 4050: bit 0 split sites, 1 cut sites, 2 water check (bit 3 = chained into the deadline stub)
    volatile LONG cutMode;      // 4051: CandidateCut, runtime switch (1 on)
    volatile LONG cutPlans;     // 4052: plans / jobs with at least one candidate cut
    volatile LONG cutCalls;     // 4053: helper calls answered "not found"
    volatile LONG cutExempt;    // 4054: calls past the deadline at a cut site of a household / selected / unknown router (not cut)
    volatile LONG wgcMode;      // 4055: WaterGoalCheck, runtime switch (1 reject, 2 shadow)
    volatile LONG wgcReject;    // 4056: water plans answered (mode 1) or that would be (mode 2)
    volatile LONG wgcMismatch;  // 4057: mode 2, a would-reject whose search found a path (must stay 0)
    volatile LONG splitLong;    // 4058: PlanHierarchical calls >= CandidateSplitLogMs
    volatile LONG lines;        // 4059: log lines written (split, cut, mismatch)
};
extern "C" __declspec(dllexport) PlanCandCounters ts3native_plancand = {0, 0, 0, 0, 0, 0, 0, 0, 0, 0};

enum { PCC_PH, PCC_CLOSEST, PCC_SAMELAND, PCC_SAMEWATER, PCC_CHOOSE, PCC_GATHER, PCC_SHORE, PCC_LOTSEARCH, PCC_N };
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
};
// cut sites: the return addresses of the calls inside 0x65E0D0 (0 = unused)
static const uint32_t kPccRets[PCC_N][4] = {
    {0},
    {TS3A(0x0065F736), TS3A(0x0065FA13), TS3A(0x0065FB5A), TS3A(0x0065FC9C)},
    {TS3A(0x0065F3DF)},
    {0},
    {TS3A(0x0065EF77), TS3A(0x0065F420)},
    {0}, {0}, {0},
};
static void* g_pccNext[PCC_N];  // our trampoline, or what an owner's pointer held (RJP takes our place in it later)
static bool g_pccSplit;         // CandidateSplit (ini, install time)
static LONG g_pccLogUs = 100000;

// ---- split timing (per thread) ----
struct PccFr { const void* mark; int site; LONGLONG t0, child; };
struct PccTh {
    int n, ph;  // frames, open PlanHierarchical frames
    PccFr f[32];
    LONGLONG self[PCC_N];
    LONG calls[PCC_N], cuts;
};
static __declspec(thread) PccTh t_pcc;
static volatile LONG64 g_pccTotUs[PCC_N];
static volatile LONG g_pccTotN[PCC_N], g_pccPh;
static volatile LONG g_pccMinute, g_pccLinesMin;

static bool PccLineOk() {  // 20 lines a minute over all threads (log only; a race lets one or two more through)
    const DWORD now = GetTickCount();
    if (now - (DWORD)g_pccMinute >= 60000) InterlockedExchange(&g_pccMinute, (LONG)now), InterlockedExchange(&g_pccLinesMin, 0);
    if (InterlockedIncrement(&g_pccLinesMin) > 20) return false;
    InterlockedIncrement(&ts3native_plancand.lines);
    return true;
}
static LONG PccUs(LONGLONG ticks) { return (LONG)(ticks * 1000000 / g_qpf.QuadPart); }

// mark = an address in the hook's frame; frames at or below it were left by an unwind (dropped, their time stays in the parent)
static bool PccEnter(int site, const void* mark) {
    if (!g_pccSplit) return false;
    PccTh& t = t_pcc;
    while (t.n && t.f[t.n - 1].mark <= mark) {
        if (t.f[--t.n].site == PCC_PH) t.ph--;
    }
    if (site != PCC_PH && !t.ph) return false;  // outside a planner call (script callers): untimed
    if (t.n == 32) return false;
    if (site == PCC_PH && !t.ph) {
        memset(t.self, 0, sizeof(t.self));
        memset(t.calls, 0, sizeof(t.calls));
        t.cuts = 0;
    }
    PccFr& f = t.f[t.n++];
    f.mark = mark, f.site = site, f.child = 0;
    if (site == PCC_PH) t.ph++;
    LARGE_INTEGER q;
    QueryPerformanceCounter(&q);
    f.t0 = q.QuadPart;
    return true;
}

static LONG PccScopeNow(bool* worker);
static void PccPhEnd(const PccTh& t, LONGLONG incl, DWORD code) {
    for (int i = 0; i < PCC_N; i++)
        if (t.calls[i]) InterlockedExchangeAdd64(&g_pccTotUs[i], PccUs(t.self[i])), InterlockedExchangeAdd(&g_pccTotN[i], t.calls[i]);
    InterlockedIncrement(&g_pccPh);
    const LONG us = PccUs(incl);
    if (us < g_pccLogUs) return;
    InterlockedIncrement(&ts3native_plancand.splitLong);
    if (!PccLineOk()) return;
    bool worker = false;
    const LONG scope = PccScopeNow(&worker);
    char b[400];
    int at = 0;
    for (int i = 1; i < PCC_N; i++)
        at += sprintf_s(b + at, sizeof(b) - at, "%s%s %ld/%ld", i > 1 ? ", " : "", kPccSites[i].name, PccUs(t.self[i]) / 1000, t.calls[i]);
    Log("route candidate split: PlanHierarchical %ld ms code %lu, %s, route %08lX, scope %s: %s (self ms/calls); rest %ld ms "
        "(own code + world A*, cell A*, water, lot legs); cut %ld", us / 1000, code, worker ? "worker job" : "sync",
        worker ? g_async.handle : (t_pd.open ? t_pd.handle : 0), KdScopeTag(scope), b, PccUs(t.self[PCC_PH]) / 1000, t.cuts);
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
    if (t.n) t.f[t.n - 1].child += incl;
    if (f.site == PCC_PH && --t.ph == 0) PccPhEnd(t, incl, r);
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

static bool PccCut(int site, DWORD ret) {
    PlanCandCounters& c = ts3native_plancand;
    if (c.cutMode != 1 || !PccRetIn(ret, kPccRets[site], 4)) return false;
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
static void* const kPccHooks[PCC_N] = {(void*)PccPhHook, (void*)PccClosestHook, (void*)PccSameLandHook, (void*)PccSameWaterHook,
                                       (void*)PccChooseHook, (void*)PccGatherHook, (void*)PccShoreHook, (void*)PccLotSearchHook};

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

// args as 0x671930 reads them (docs/643 §4): a2 route, a3 start cell, a6 goal cell, a9 out vector
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
    if (ts3a_noaddr) { Log("SKIP route candidate cut / split / water goal check: no EA 1.69 address, docs/643"); return; }
#endif
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
    g_pccLogUs = Clamp(GetPrivateProfileIntW(L"RouteJobPhases", L"CandidateSplitLogMs", 100, ini), 1, 60000) * 1000;
    if (split || cut) {
        int hooked = 0, want = 0;
        for (int i = 0; i < PCC_N; i++) {
            if (!split && !kPccSites[i].cut) continue;
            want++;
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
}

// the 60 s counter lines (ts3native.cpp)
static void PccLogCounters() {
    const PlanCandCounters& c = ts3native_plancand;
    if (!c.ready) return;
    Log("route candidate: cut mode %ld plans %ld calls %ld exempt %ld; water goal check mode %ld rejects %ld mismatch %ld; split calls %ld long %ld",
        c.cutMode, c.cutPlans, c.cutCalls, c.cutExempt, c.wgcMode, c.wgcReject, c.wgcMismatch, g_pccPh, c.splitLong);
    if (!g_pccSplit) return;
    char b[400];
    int at = 0;
    for (int i = 1; i < PCC_N; i++)
        at += sprintf_s(b + at, sizeof(b) - at, "%s%s %lld/%ld", i > 1 ? ", " : "", kPccSites[i].name, (long long)(g_pccTotUs[i] / 1000), g_pccTotN[i]);
    Log("route candidate split totals: %s (self ms/calls); PlanHierarchical rest %lld ms over %ld calls", b, (long long)(g_pccTotUs[PCC_PH] / 1000), g_pccTotN[PCC_PH]);
}
