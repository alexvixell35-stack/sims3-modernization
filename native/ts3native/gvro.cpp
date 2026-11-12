// [GoalValidRO] (docs/178 §7, T6): the steering obstacle query of GoalPoint_Validate answered read-only while an async route
// job holds +0x918, for empty answers only. Included by ts3native.cpp after fgro.cpp (uses steerro.cpp's QL, writer set,
// SrQuery and lot lookup; avoidro.cpp's AvMayRun; savewrite.cpp's PatchCalls).
//
// GoalPoint_Validate 0x759B20 (radial goal points, docs/177 §2; the sim runs it in sync plans) calls
// RoutingWorld_ObstacleQuery 0x82EDB0 at 0x75A647 (E8, return = wait key 0x75A64C): thiscall world, (pt, r, vec, mode, opt),
// ret 0x14, answer in al. The call site pushes mode = ebx = 0 (0x75A59F; every path into 0x75A59C..0x75A647 passes it),
// opt = the validator's option word [ebp+8], r = [0x106E33C], vec = a fresh empty FootprintRefVec on the stack (begin = end =
// cap = 0, tag 0xFED2FC). 0x82EDB0 [verified disassembly 2026-10-24]: takes +0x918, releases the vector's old range, lot =
// World_LotLookup(world+0x964, x, z) (no lot: release, al 0), else 0x631F10 (planner world+0x95C, ctx with the record
// {opt, +0x24 = 1}, point {x, z, level pt+0x24}, r, set, mode) and, when the set is not empty, copies its footprints (+1 each,
// sorted, unique) into vec; release, al 1. GoalPoint_Validate: al 0 or begin == end jump to 0x75A939 (release the empty
// range, free nothing); only a non-empty vector enters the hit loop 0x75A672..0x75A92E (plain refcount +1/-1, virtual calls,
// output writes).
// So an empty answer is exact without the copy: al = lot found, vec untouched, and the caller's next instruction sees what it
// sees after the game's call with an empty set. A non-empty answer would need the footprint refs (plain refcount writes that
// race the job) and the hit loop: those calls go to the game (today's wait). The query: S2's point query (SrQuery, mode 0,
// visitor 0x662DB0, record option word = opt) under QL shared, try only, S2's protocol (tripwires, sequence, generation),
// as [ObstructedRO] (same 0x82EDB0 mode 0 record). Any doubt (QL busy, dirty lot index, lot not in the table, deferred
// flush, pin moved, unknown slot, buffer limit, tripwire, job end, mode != 0, vec not empty, footprints found) calls the
// game's 0x82EDB0. Shadow (no job): the game's call, then the clone, compared (al and footprint count).
// Counters v118 (+0x47DC): gvMode (+0x47E0, --ab goalvalidro), gvShadow (+0x47E4, --ab goalvalidroshadow).

static const DWORD GV_SITE = 0x0075A647, GV_TARGET = 0x0082EDB0;
typedef DWORD(__fastcall* GvFn)(u8* world, void* edx, const float* pt, float r, DWORD* vec, DWORD mode, DWORD opt);  // ret 0x14
static GvFn g_gvOrig = (GvFn)(uintptr_t)GV_TARGET;
typedef int (*GvQueryFn)(SrCtx* c, const u8* p, DWORD lo, DWORD hi, const SrPt& pt, float r, const u8* rec);
static int GvQueryGame(SrCtx* c, const u8* p, DWORD lo, DWORD hi, const SrPt& pt, float r, const u8* rec) {
    return SrQuery(c, p, lo, hi, pt, true, r, rec);  // 0x82EDB0 mode 0: visitor 0x662DB0 (flag 0), options object 0
}
static GvQueryFn g_gvQuery = GvQueryGame;  // selftest hands in fake footprint lists

enum { GV_FB_STATE, GV_FB_TRY, GV_FB_LOT, GV_FB_SHAPE, GV_FB_TRIP, GV_FB_JOBEND, GV_FB_HITS };
static const int GV_WHY_JOBEND = 100;  // above the SR_* codes

// The clone of 0x82EDB0 from its lot lookup on: *lot = World_LotLookup found one (the game's al), *n = footprints collected.
// visit (SiteCLoop): runs on the collected footprints inside the QL hold (they stay valid only there), its answer in *ans.
typedef int (*GvVisitFn)(const SrCtx& c, const void* arg);
static int GvRun(u8* world, const float* pt, float r, DWORD opt, bool job, bool* lot, int* n, GvVisitFn visit = NULL,
                 const void* arg = NULL, int* ans = NULL) {
    *lot = false, *n = 0;
    if (!world) return SR_FB_STATE;
    const u8* p = *(const u8* const*)(world + 0x95C);  // 0x82EEC8: the planner as 0x82EDB0 passes it
    u8* wmw = *(u8* const*)(world + 0x964);
    if (!p || !wmw) return SrShape(SRS_NOPLANNER);
    if (wmw[0x1CC]) return SR_FB_DIRTY;  // World_LotLookup would rebuild the lot index
    // phase 1, no lock: the lot lookup (S2 runs the same call beside jobs; GoalPoint_Validate itself calls it in its loop)
    const u8* lp = g_srLotLookup(wmw, NULL, pt[0], pt[2]);
    if (!lp) return SR_OK;  // 0x82EE4D: no lot, al 0, the vector stays empty
    *lot = true;
    const DWORD lo = *(const DWORD*)(lp + 0x48), hi = *(const DWORD*)(lp + 0x4C);
    SrPt sp;
    memset(&sp, 0, sizeof(sp));
    sp.v[0] = pt[0], sp.v[2] = pt[2];
    sp.one = *g_srOne;
    sp.level = *(const LONG*)(pt + 9);  // pt+0x24 (0x82EDF1)
    __declspec(align(16)) u8 rec[0x30] = {};  // 0x82EDB0's record: option word, +0x24 = 1, id and options object 0
    *(DWORD*)rec = opt;
    rec[0x24] = 1;
    if (t_qlDepth || t_qlShared) return SR_FB_STATE;  // SRW locks are not recursive (e.g. inside SH's hold)
    if (!TryAcquireSRWLockShared(&g_ql)) return SR_FB_TRY;
    if (g_steerBroken) { ReleaseSRWLockShared(&g_ql); return SR_FB_STATE; }
    const LONG seq = g_qlSeq;
    InterlockedExchange(&g_srInClone, 1);
    const LONG twEpoch = g_twEpoch, twGen = g_twGen;
    t_inRo = true;
    t_qlShared = 1;
    t_srFail = SR_OK;
    int why = job && !g_twArmed ? GV_WHY_JOBEND : g_twActive ? SR_FB_TRIP : SR_OK;
    static SrCtx c;  // owner thread only
    if (!why) why = g_gvQuery(&c, p, lo, hi, sp, r, rec);
    if (!why && t_srFail) why = t_srFail;
    if (!why && visit && c.n) *ans = visit(c, arg);  // before the checks below, so they cover it too
    if (!why && (g_qlSeq != seq || g_twActive || g_twEpoch != twEpoch)) why = SR_FB_TRIP;
    if (!why && job && g_twGen != twGen) why = GV_WHY_JOBEND;  // disarmed during the clone: writers unseen
    t_inRo = false;
    t_qlShared = 0;
    ReleaseSRWLockShared(&g_ql);
    InterlockedExchange(&g_srInClone, 0);
    if (!why) *n = c.n;
    return why;
}

static int GvFbIdx(int why) {  // also [IdleCheckRO] SiteC (idlero.cpp)
    switch (why) {
    case SR_FB_TRY: return GV_FB_TRY;
    case SR_FB_DIRTY: case SR_FB_LOTMISS: case SR_FB_DEFERRED: return GV_FB_LOT;
    case SR_FB_SHAPE: case SR_FB_CACHE: return GV_FB_SHAPE;
    case SR_FB_TRIP: return GV_FB_TRIP;
    case GV_WHY_JOBEND: return GV_FB_JOBEND;
    default: return GV_FB_STATE;
    }
}
static void GvFallback(int why) { InterlockedIncrement(&ts3native_counters.gvFb[GvFbIdx(why)]); }

static volatile LONG g_gvNoted;
static DWORD GvShadow(u8* world, void* edx, const float* pt, float r, DWORD* vec, DWORD mode, DWORD opt) {
    Ts3nCounters& k = ts3native_counters;
    const DWORD a = g_gvOrig(world, edx, pt, r, vec, mode, opt);
    const int size = (int)((vec[1] - vec[0]) / 4);  // the game's copy: sorted, unique footprint refs
    bool lot = false;
    int n = 0;
    const int why = mode ? SR_FB_STATE : GvRun(world, pt, r, opt, false, &lot, &n);
    InterlockedIncrement(&k.gvShadowRuns);
    if (why) { GvFallback(why); return a; }
    if (!(u8)a || !size) InterlockedIncrement(&k.gvShadowEmpty);  // what Mode 1 would answer without the game
    if (((u8)a != 0) != lot || (lot && size != n)) {
        InterlockedIncrement(&k.gvShadowMismatch);
        if (InterlockedIncrement(&g_gvNoted) <= 16)
            Log("goal validation RO: shadow mismatch at (%.2f, %.2f) level %ld opt 0x%lX: game al %u / %d footprints, clone lot %u / %d",
                pt[0], pt[2], *(const LONG*)(pt + 9), opt, (unsigned)(u8)a, size, (unsigned)lot, n);
    }
    return a;
}

// [GoalValidRO] Attr=1 (docs/250 §7.0, switch gvattr, counters v174): which plans wait here while a job holds +0x918. ts3fixes
// sends each plan's source class and scope (UserDiag op 35, UnseenFallback.cs UfPlanBody) and its end (op 12). Per job-time
// call: n by class; per fallback (the game's call, the wait): us by class and scope; after the plan's last fallback, the job
// waits it still has up to its end (g_w918Us, every owner-thread job wait) = gvaLaterTakeUs, the moved-wait meter for B1.
// Sim thread only, plain statics. shortcut: one slot; a plan inside another plan's callback resets it at its own end, so the
// outer plan's later waits count as unknown scope / no class (rare; a stack if Part 0 shows nested plans matter).
// The design's native caller pairs are not logged: the static caller list (docs/250 §9 step 0) has two sites, both under 0x75BE60.
static LONG g_gvaSrc, g_gvaScope, g_gvaMark;
static bool g_gvaOpen;
// docs/250 §13.3 (v189): the corrected B1 gate. gvaLaterTakeUs reads waits for later jobs (the first wait absorbs the job), so
// per plan: g_gvaPlanUs = its validator waits, and g_gvaTakes (LockAcquireHook: owner thread, routing lock, while g_gvaWatch)
// counts acquires after its last fallback, waited or not. None by the plan's end (op 12) = gvaNoLaterN / gvaNoLaterUs.
// §12.3 gvaWhyWaitUs: the same waits by the SyncIc reason of the plan (g_gvaWhy, ts3native.cpp SyncIc).
static LONG g_gvaPlanUs, g_gvaTakes0;
static u8 GvaSrcMsg(DWORD lo) {  // UserDiag op 35: lo 0xFFFF = is gvattr on; else class | scope << 8, before the plan
    if (lo == 0xFFFF) return ts3native_counters.gvaMode == 1;
    g_gvaSrc = (LONG)(lo & 0xFF) < 6 ? (LONG)(lo & 0xFF) : 0;
    g_gvaScope = (LONG)(lo >> 8 & 0xFF) < 3 ? (LONG)(lo >> 8 & 0xFF) : 0;
    g_gvaOpen = g_gvaWatch = false;
    g_gvaPlanUs = 0;
    return 1;
}
static void GvaPlanEnd() {  // UserDiag op 12: the plan is over
    Ts3nCounters& k = ts3native_counters;
    if (g_gvaOpen) {
        InterlockedExchangeAdd(&k.gvaLaterTakeUs, g_w918Us - g_gvaMark);
        InterlockedIncrement(&k.gvaPlansN);
        if (g_gvaTakes == g_gvaTakes0) {
            InterlockedIncrement(&k.gvaNoLaterN);
            InterlockedExchangeAdd(&k.gvaNoLaterUs, g_gvaPlanUs);
        }
    }
    g_gvaOpen = g_gvaWatch = false;
    g_gvaPlanUs = 0;
    g_gvaSrc = g_gvaScope = 0;
    static DWORD last;
    const DWORD now = GetTickCount();
    if (k.gvaMode == 1 && now - last >= 10000) {
        last = now;
        Log("gvattr: n none %ld wd %ld sc %ld own %ld fgn %ld chk %ld; wait ms %ld %ld %ld %ld %ld %ld; scope ms hh %ld npc %ld unseen %ld; "
            "later ms %ld; at start %ld; no later take %ld of %ld plans, %ld ms", k.gvaSrcN[0], k.gvaSrcN[1], k.gvaSrcN[2],
            k.gvaSrcN[3], k.gvaSrcN[4], k.gvaSrcN[5], k.gvaSrcWaitUs[0] / 1000, k.gvaSrcWaitUs[1] / 1000,
            k.gvaSrcWaitUs[2] / 1000, k.gvaSrcWaitUs[3] / 1000, k.gvaSrcWaitUs[4] / 1000, k.gvaSrcWaitUs[5] / 1000,
            k.gvaScopeWaitUs[0] / 1000, k.gvaScopeWaitUs[1] / 1000, k.gvaScopeWaitUs[2] / 1000, k.gvaLaterTakeUs / 1000,
            k.gvaAtStart, k.gvaNoLaterN, k.gvaPlansN, k.gvaNoLaterUs / 1000);
        char why[400];
        int n = 0;
        for (int i = 0; i <= GVA_WHY_NONE; i++)  // only the reasons that waited a millisecond or more
            if (k.gvaWhyWaitUs[i] >= 1000 && n < (int)sizeof(why) - 32)
                n += sprintf_s(why + n, sizeof(why) - n, " %s %ld", i < GVA_WHY_NONE ? kRfbName[i] : "none", k.gvaWhyWaitUs[i] / 1000);
        Log("gvattr: wait ms by sync reason:%s", n ? why : " -");
    }
}
// the game's 0x82EDB0, timed by class and scope when gvattr is on and a job runs (the call is then the wait)
static DWORD GvaOrig(u8* world, void* edx, const float* pt, float r, DWORD* vec, DWORD mode, DWORD opt) {
    Ts3nCounters& k = ts3native_counters;
    if (k.gvaMode != 1 || g_async.state != A_RUNNING || __readfsdword(0x24) != g_async.owner) return g_gvOrig(world, edx, pt, r, vec, mode, opt);
    LARGE_INTEGER t0;
    QueryPerformanceCounter(&t0);
    const DWORD a = g_gvOrig(world, edx, pt, r, vec, mode, opt);
    const LONG us = UsSince(t0);
    InterlockedExchangeAdd(&k.gvaSrcWaitUs[g_gvaSrc], us);
    InterlockedExchangeAdd(&k.gvaScopeWaitUs[g_gvaScope], us);
    InterlockedExchangeAdd(&k.gvaWhyWaitUs[g_gvaWhy >= 0 && g_gvaWhy <= GVA_WHY_NONE ? g_gvaWhy : GVA_WHY_NONE], us);
    g_gvaOpen = g_gvaWatch = true;
    g_gvaPlanUs += us;
    g_gvaTakes0 = g_gvaTakes;  // its own acquire is counted; later takes of this plan count from here
    g_gvaMark = g_w918Us;  // the validator's own wait is in; later waits of this plan count from here
    return a;
}

static DWORD __fastcall GvStub(u8* world, void* edx, const float* pt, float r, DWORD* vec, DWORD mode, DWORD opt) {
    Ts3nCounters& k = ts3native_counters;
    const StubKey sk(GV_SITE + 5);  // probe P0: the game's calls below keep the site's key
    const DWORD tid = GetCurrentThreadId();
    if (k.gvaMode == 1 && tid == g_async.owner) {
        if (g_asInStart) InterlockedIncrement(&k.gvaAtStart);
        if (g_async.state == A_RUNNING) InterlockedIncrement(&k.gvaSrcN[g_gvaSrc]);
    }
    if ((!k.gvMode && !k.gvShadow) || tid != g_async.owner) return GvaOrig(world, edx, pt, r, vec, mode, opt);
    InterlockedIncrement(&k.gvCalls);
    if (g_async.state != A_RUNNING) {
        if (!k.gvShadow) return GvaOrig(world, edx, pt, r, vec, mode, opt);
        InterlockedIncrement(&g_rgOffSimOpen);  // no job starts while the two answers are compared (worker Dekker order)
        const DWORD a = g_async.state == A_RUNNING ? g_gvOrig(world, edx, pt, r, vec, mode, opt) : GvShadow(world, edx, pt, r, vec, mode, opt);
        RgOffSimDrop();
        return a;
    }
    if (!k.gvMode) return GvaOrig(world, edx, pt, r, vec, mode, opt);
    int why = SR_FB_STATE;
    // not the +0x918 owner (lent or recursive hold), no job callback, no gate frame open; mode 0 and an empty vector as the
    // call site passes them (else the game would release the old range: a write)
    if (!mode && vec[0] == vec[1] && AvMayRun(tid)) {
        LARGE_INTEGER t0;
        QueryPerformanceCounter(&t0);
        bool lot;
        int n;
        why = GvRun(world, pt, r, opt, true, &lot, &n);
        if (!why && !n) {
            const LONG us = UsSince(t0);
            InterlockedIncrement(&k.gvTaken);
            MwSkip(MW_GV);
            if (!lot) InterlockedIncrement(&k.gvNoLot);
            InterlockedExchangeAdd(&k.gvUs, us);
            Max(&k.gvMaxUs, us);
            return lot ? 1 : 0;
        }
        if (!why) {  // footprints near the point: the hit loop needs the game's refs
            InterlockedIncrement(&k.gvFb[GV_FB_HITS]);
            return GvaOrig(world, edx, pt, r, vec, mode, opt);
        }
    }
    GvFallback(why);
    return GvaOrig(world, edx, pt, r, vec, mode, opt);
}

static const CallSite kGvSite = {GV_SITE, GV_TARGET, (void*)GvStub};

// [GoalValidRO] Enabled=1: needs [RouteGate] Enforce=1 with the async path and the S2 writer set (QL, W1-W6, tripwires)
static void InstallGoalValidRO(const wchar_t* ini) {
    Ts3nCounters& k = ts3native_counters;
    // docs/250 §7.3 B2 (unseenfb.cpp GvuMsg): no hook of its own, so independent of Enabled; runtime gvunseen / gvunseenms
    k.gvuMode = Clamp(GetPrivateProfileIntW(L"GoalValidRO", L"UnseenDefer", 0, ini), 0, 1);
    k.gvuMaxMs = Clamp(GetPrivateProfileIntW(L"GoalValidRO", L"UnseenDeferMaxMs", 2000, ini), 100, 10000);
    Log("goal validation: UnseenDefer=%ld UnseenDeferMaxMs=%ld", k.gvuMode, k.gvuMaxMs);
    if (!GetPrivateProfileIntW(L"GoalValidRO", L"Enabled", 0, ini)) { Log("goal validation RO off (Enabled=0)"); return; }
    if (!k.rgEnforce || !k.asyncReady) { Log("SKIP goal validation RO: needs [RouteGate] Enforce=1 with the async path"); return; }
    if (!InstallSrWriters()) { Log("SKIP goal validation RO: the S2 writer set (W1-W6, W4, tripwires) is not all in place"); return; }
    if (!PatchCalls("goal validation RO (0x759B20 -> ObstacleQuery)", &kGvSite, 1)) return;
    k.gvMode = Clamp(GetPrivateProfileIntW(L"GoalValidRO", L"Mode", 1, ini), 0, 1);
    k.gvShadow = Clamp(GetPrivateProfileIntW(L"GoalValidRO", L"Shadow", 0, ini), 0, 1);
    k.gvaMode = Clamp(GetPrivateProfileIntW(L"GoalValidRO", L"Attr", 0, ini), 0, 1);
    k.gvReady = 1;
    Log("OK   goal validation RO: 0x75A647 + the S2 writer set (Mode=%ld Shadow=%ld Attr=%ld)", k.gvMode, k.gvShadow, k.gvaMode);
}
