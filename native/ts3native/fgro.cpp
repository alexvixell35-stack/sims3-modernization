// [PlacementRO] (docs/174): gate-12 family F-G answered read-only while an async route job holds +0x918. Included by
// ts3native.cpp after rtoro.cpp (uses steerro.cpp's QL, writer set and SrQuery; avoidro.cpp's AvMayRun; savewrite.cpp's
// PatchCalls).
//
// LotRouting_FootprintsNearPoint 0x631F10 (thiscall P = lot planner; ctx, pt, radius, set, mode; ret 0x14) asks
// LotTable_Contains (gate 12, key 0x631F40), the cell graph 0x62FB10, the lot transform 0x62ED40 (gate 0), then QueryPoint
// 0x667480 (pin Q+0x48 plain +1/-1, flush when deferred, walk with visitor 0x662DD0 / 0x662DB0 into `set`); it returns 1 when
// the set is not empty [verified disassembly 2026-10-23, docs/174 §2]. Its sim caller is 0x634F10 (placement test from
// IC_World_FindGoodLocation* via 0xB470C0, and 4 more callers): a stack ctx with ctx+0x40 = the caller's record (0 from
// FindGoodLocation), ctx+0x50 = 0, ctx+0x54 = the lot; pt = {x, z, level}; mode = 1 (constant push); the set is only freed
// afterwards (0x6282F0), 0x634F10 returns !result. The other caller, RoutingWorld_ObstacleQuery 0x82EF6D, holds +0x918.
//
// One call-site retarget, 0x635013 (the E8 in 0x634F10): on the sim (owner) thread while a job runs, not the +0x918 owner,
// no job callback, no gate frame, fgMode: the answer from S2's point query (SrQuery: Contains rules, cell graph, transform,
// the game's walk 0x663E20 with SrVisit in place of the collector 0x669CD0: no pin, no flush, no refcounts) under QL shared,
// try only, with S2's protocol (tripwires, sequence, generation): result = any footprint collected. The set stays empty
// (the caller only frees it). Any doubt (QL busy, lot miss, deferred flush, pin moved, unknown slot, buffer limit, tripwire,
// mode 0) calls the game's 0x631F10 (today's wait). Shadow (no job): the game's call, then the clone, compared.
// Counters v100 (+0x42C8): fgMode (+0x42CC, --ab placementro), fgShadow (+0x42D0, --ab placementroshadow).

static const DWORD FG_SITE = TS3A(0x00635013), FG_TARGET = TS3A(0x00631F10);
typedef DWORD(__fastcall* FgFn)(u8* p, void* edx, const u8* ctx, const float* pt, float r, void* set, DWORD mode);  // ret 0x14
static FgFn g_fgOrig = (FgFn)(uintptr_t)FG_TARGET;
typedef int (*FgQueryFn)(SrCtx* c, const u8* p, DWORD lo, DWORD hi, const SrPt& pt, float r, const u8* rec);
static int FgQueryGame(SrCtx* c, const u8* p, DWORD lo, DWORD hi, const SrPt& pt, float r, const u8* rec) {
    return SrQuery(c, p, lo, hi, pt, true, r, rec, true, rec ? 2 : 0);  // mode 1 = visitor 0x662DD0; rec+0x28 options apply
}
static FgQueryFn g_fgQuery = FgQueryGame;  // selftest hands in fake footprint lists
// docs/202 §3.4 C4 (unseenfbc.cpp UfcPlace, set when [UnseenFallback] Sites C4 is installed): the game's 0x631F10 for a
// FindGoodLocation of an unseen Sim, taken with a deadline; NULL = the game's call as is
static FgFn g_ufcFg;
static DWORD FgGame(u8* p, void* edx, const u8* ctx, const float* pt, float r, void* set, DWORD mode) {
    return g_ufcFg ? g_ufcFg(p, edx, ctx, pt, r, set, mode) : g_fgOrig(p, edx, ctx, pt, r, set, mode);
}

enum { FG_FB_STATE, FG_FB_TRY, FG_FB_LOT, FG_FB_SHAPE, FG_FB_TRIP, FG_FB_JOBEND };
static const int FG_WHY_JOBEND = 100;  // above the SR_* codes

// The clone: 0x631F10 from its first read on; *hit = the game's al. Under QL shared, S2's protocol (obstructro.cpp OrLocked)
static int FgRun(const u8* p, const u8* ctx, const float* pt, float r, DWORD mode, bool job, bool* hit) {
    *hit = false;
    const u8* lot = *(const u8* const*)(ctx + 0x54);
    if (!lot) return SR_OK;  // 0x631F21: 0 before any routing touch
    const DWORD lo = *(const DWORD*)(lot + 0x48), hi = *(const DWORD*)(lot + 0x4C);
    if (!(lo | hi)) return SR_OK;  // 0x631F31
    if (!(u8)mode) return SR_FB_STATE;  // visitor 0x662DB0 with an options record: not modelled (0x634F10 always pushes 1)
    const u8* rec = *(const u8* const*)(ctx + 0x40);
    SrPt sp;
    memset(&sp, 0, sizeof(sp));
    sp.v[0] = pt[0], sp.v[2] = pt[1];  // 0x62ED40 reads {x, z}; pt+8 = the level (0x631F48)
    sp.one = *g_srOne;
    sp.level = *(const LONG*)(pt + 2);
    if (t_qlDepth || t_qlShared) return SR_FB_STATE;  // SRW locks are not recursive (e.g. inside SH's hold)
    if (!TryAcquireSRWLockShared(&g_ql)) return SR_FB_TRY;
    if (g_steerBroken) { ReleaseSRWLockShared(&g_ql); return SR_FB_STATE; }
    const LONG seq = g_qlSeq;
    InterlockedExchange(&g_srInClone, 1);
    const LONG twEpoch = g_twEpoch, twGen = g_twGen;
    t_inRo = true;
    t_qlShared = 1;
    t_srFail = SR_OK;
    int why = job && !g_twArmed ? FG_WHY_JOBEND : g_twActive ? SR_FB_TRIP : SR_OK;
    static SrCtx c;  // owner thread only
    if (!why) why = g_fgQuery(&c, p, lo, hi, sp, r, rec);
    if (!why && t_srFail) why = t_srFail;
    if (!why && (g_qlSeq != seq || g_twActive || g_twEpoch != twEpoch)) why = SR_FB_TRIP;
    if (!why && job && g_twGen != twGen) why = FG_WHY_JOBEND;  // disarmed during the clone: writers unseen
    t_inRo = false;
    t_qlShared = 0;
    ReleaseSRWLockShared(&g_ql);
    InterlockedExchange(&g_srInClone, 0);
    if (!why) *hit = c.n > 0;
    return why;
}

static void FgFallback(int why) {
    int i;
    switch (why) {
    case SR_FB_TRY: i = FG_FB_TRY; break;
    case SR_FB_DIRTY: case SR_FB_LOTMISS: case SR_FB_DEFERRED: i = FG_FB_LOT; break;
    case SR_FB_SHAPE: case SR_FB_CACHE: i = FG_FB_SHAPE; break;
    case SR_FB_TRIP: i = FG_FB_TRIP; break;
    case FG_WHY_JOBEND: i = FG_FB_JOBEND; break;
    default: i = FG_FB_STATE; break;
    }
    InterlockedIncrement(&ts3native_counters.fgFb[i]);
}

static volatile LONG g_fgNoted;
static DWORD FgShadow(u8* p, void* edx, const u8* ctx, const float* pt, float r, void* set, DWORD mode) {
    Ts3nCounters& k = ts3native_counters;
    const DWORD a = g_fgOrig(p, edx, ctx, pt, r, set, mode);
    bool b;
    const int why = FgRun(p, ctx, pt, r, mode, false, &b);
    InterlockedIncrement(&k.fgShadowRuns);
    if (why) { FgFallback(why); return a; }
    if (((u8)a != 0) != b) {
        InterlockedIncrement(&k.fgShadowMismatch);
        if (InterlockedIncrement(&g_fgNoted) <= 16)
            Log("placement RO: shadow mismatch at (%.2f, %.2f) level %ld r %.3f: game %u, clone %u", pt[0], pt[1], *(const LONG*)(pt + 2), r,
                (unsigned)(u8)a, (unsigned)b);
    }
    return a;
}

static DWORD __fastcall FgStub(u8* p, void* edx, const u8* ctx, const float* pt, float r, void* set, DWORD mode) {
    Ts3nCounters& k = ts3native_counters;
    const DWORD tid = GetCurrentThreadId();
    if ((!k.fgMode && !k.fgShadow) || tid != g_async.owner) return FgGame(p, edx, ctx, pt, r, set, mode);
    InterlockedIncrement(&k.fgCalls);
    if (g_async.state != A_RUNNING) {
        if (!k.fgShadow) return FgGame(p, edx, ctx, pt, r, set, mode);
        InterlockedIncrement(&g_rgOffSimOpen);  // no job starts while the two answers are compared (worker Dekker order)
        const DWORD a = g_async.state == A_RUNNING ? g_fgOrig(p, edx, ctx, pt, r, set, mode) : FgShadow(p, edx, ctx, pt, r, set, mode);
        RgOffSimDrop();
        return a;
    }
    if (!k.fgMode) return FgGame(p, edx, ctx, pt, r, set, mode);
    int why = SR_FB_STATE;
    if (AvMayRun(tid)) {  // not the +0x918 owner (lent or recursive hold), no job callback, no gate frame open
        LARGE_INTEGER t0;
        QueryPerformanceCounter(&t0);
        bool hit;
        why = FgRun(p, ctx, pt, r, mode, true, &hit);
        if (!why) {
            const LONG us = UsSince(t0);
            InterlockedIncrement(&k.fgTaken);
            MwSkip(MW_PL);
            if (hit) InterlockedIncrement(&k.fgHits);
            InterlockedExchangeAdd(&k.fgUs, us);
            Max(&k.fgMaxUs, us);
            return hit ? 1 : 0;
        }
    }
    FgFallback(why);
    return FgGame(p, edx, ctx, pt, r, set, mode);
}

static const CallSite kFgSite = {FG_SITE, FG_TARGET, (void*)FgStub};

// [PlacementRO] Enabled=1: needs [RouteGate] Enforce=1 with the async path and the S2 writer set (QL, W1-W6, tripwires)
static void InstallPlacementRO(const wchar_t* ini) {
    Ts3nCounters& k = ts3native_counters;
    if (!GetPrivateProfileIntW(L"PlacementRO", L"Enabled", 0, ini)) { Log("placement RO off (Enabled=0)"); return; }
    if (!k.rgEnforce || !k.asyncReady) { Log("SKIP placement RO: needs [RouteGate] Enforce=1 with the async path"); return; }
    if (!InstallSrWriters()) { Log("SKIP placement RO: the S2 writer set (W1-W6, W4, tripwires) is not all in place"); return; }
    if (!PatchCalls("placement RO (0x634F10 -> FootprintsNearPoint)", &kFgSite, 1)) return;
    k.fgMode = Clamp(GetPrivateProfileIntW(L"PlacementRO", L"Mode", 1, ini), 0, 1);
    k.fgShadow = Clamp(GetPrivateProfileIntW(L"PlacementRO", L"Shadow", 0, ini), 0, 1);
    k.fgReady = 1;
    Log("OK   placement RO: 0x635013 + the S2 writer set (Mode=%ld Shadow=%ld)", k.fgMode, k.fgShadow);
}
