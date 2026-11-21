// [EditPathRO] (docs/670 §8): RouteFollow_EditPath's goal footprint gather answered read-only for the route form, while an
// async route job holds +0x918. Included by ts3native.cpp after placero.cpp (uses steerro.cpp's QL, writer set, SrEntry /
// SrCell / SrXform / SrWalk2 / SrConsume; avoidro.cpp's AvMayRun; savewrite.cpp's PatchCalls).
//
// The wait (s146-s148 storms: the largest +0x918 wait key left once [PlaceTestRO] is on, 0x85F6CB, 0.9-2.4 s per 4 min):
// RouteFollow_EditPath 0x85F5A0 (cdecl: wm, world, planner, ids, start, goal, opt, excl, ign1, n1, ign2, n2) calls the goal
// footprint gather 0x82F0B0 at 0x85F6C6, which takes +0x918. S2 ([SteerRO]) answers it only under Locomotion_ObstacleCheck
// 0x85FD10 (start == goal). The storm's waits come from the other caller, RouteFollow_EditPathForRoute 0x85FF30 (cdecl:
// path, ids, opt; called by IC_Route_SetAGS, Route_PlanCommon's tail and the two path insert ICs): for every node of the path
// it calls EditPath at 0x85FFC2 with start = the node, goal = node + dir * len (0x81D7A0), excl = 0 and both ignore lists
// empty [verified disassembly 0x85FF84..0x85FFD1]; the stall watch names IC_Route_SetAGS in 20 of 27 such waits.
//
// EditPath for that form [verified decompile + disassembly, docs/670 §8.2]: 0x85AE60 on both points, the goal's level set
// to the start's; 0x82F0B0 (lot of the goal point, else of the start point with the two points swapped, else nothing;
// 0x631E20: LotTable_Contains, the cell graph of the first point's level, both points through the lot transform, segment
// walk 0x667400 width 0 with visitor 0x662DD0 and no record; the set copied in pointer order); then per footprint the id,
// type, ignore, ids and level-0 segment tests S2's SrConsume already clones (the consumer's lot is the goal's raw point,
// else the start's; a = start, b = goal through that lot's transform), and IdVec_PushBack for the ones that pass. Its
// other writes are refcounts (+1 / -1) and the pin; the polygon-cache rebuild 0x80E5B0 and the deferred flush are the
// cases the clone gives back.
//
// So the stub at 0x85FFC2 answers one node's call from S2's protocol (QL shared, try only; tripwires, sequence,
// generation; the slot whitelist) on the sim (owner) thread while a job runs, when the thread is not the +0x918 owner, in
// no job callback and in no gate frame, and appends the same ids in the same order. Any doubt calls the game's EditPath
// (today's wait). The worker's own calls (a job's plan tail) and every other form (excl != 0) pass through.
// Shadow (no job): the game's call, then the clone, the appended ids compared.
// Exported block ts3native_editpathro: +0x00 mode is the runtime switch (native_set editpathro); Shadow is ini only.

struct EditPathRoCounters {
    volatile LONG epMode;            // +0x00 runtime switch: 1 = answer while a job runs
    volatile LONG epShadow;          // +0x04 1 = with no job running, compare the clone with the game's call
    volatile LONG epReady;           // +0x08 the call site is patched
    volatile LONG epCalls;           // +0x0C route-form calls of the sim thread seen with mode or shadow on
    volatile LONG epJob;             // +0x10 of those, while a job ran
    volatile LONG epTaken;           // +0x14 answered without the game's call
    volatile LONG epIds;             // +0x18 ids appended by answered calls
    volatile LONG epNoLot;           // +0x1C of the answered calls, neither point on a lot (the game returns before any read)
    volatile LONG epUs;              // +0x20 time in answered clones, us
    LONG epMaxUs;                    // +0x24
    volatile LONG epFb[8];           // +0x28 calls given back to the game: EP_FB_*
    volatile LONG epShadowRuns;      // +0x48
    volatile LONG epShadowMismatch;  // +0x4C must stay 0
    volatile LONG epGameUs;          // +0x50 time in the game's call while a job ran (the wait), us
    LONG epGameMaxUs;                // +0x54
    volatile LONG epOtherForm;       // +0x58 sim-thread calls with an exclude vector (not the route form): the game's call
    // docs/670 §14 (the 0x85F6CB residue): StartGuard and Retry
    volatile LONG epGuard;           // +0x5C ini StartGuard: no job starts inside the game's call of a call that found no job
    volatile LONG epRetry;           // +0x60 ini Retry: a dropped clone answer (QL busy, tripwire, job end) is run again
    volatile LONG epGuardCalls;      // +0x64 the game's calls made inside a start-guard frame
    volatile LONG epGuardRace;       // +0x68 a job had started between the stub's first look and the frame: the clone path instead
    volatile LONG epRetryN;          // +0x6C reruns (clone or guard) after a dropped answer
    volatile LONG epRetryOk;         // +0x70 calls answered by a rerun, by the clone or by the game's call with no job live
};
extern "C" __declspec(dllexport) EditPathRoCounters ts3native_editpathro = {};

static const DWORD EP_SITE = TS3A(0x0085FFC2), EP_TARGET = TS3A(0x0085F5A0);
typedef void(__cdecl* EpFn)(u8* wm, u8* world, u8* planner, u8* ids, const float* start, const float* goal, DWORD opt, const void* excl,
                            const void* ign1, DWORD n1, const void* ign2, DWORD n2);
static EpFn g_epOrig = (EpFn)(uintptr_t)EP_TARGET;

enum { EP_FB_STATE, EP_FB_TRY, EP_FB_LOT, EP_FB_SHAPE, EP_FB_CACHE, EP_FB_TRIP, EP_FB_JOBEND, EP_FB_MAYNOT };
static const int EP_WHY_JOBEND = 110;  // above the SR_* codes

// The clone of one EditPath call in the route form: the ids it would append, in order, into out (ids0 / n0 = the caller's
// ids before the call). *noLot = neither point is on a lot (0x82F0B0 returns 0 before any routing read)
static int EpRun(u8* wm, u8* world, const u8* planner, const DWORD* ids0, int n0, const float* start, const float* goal, const void* ign1,
                 DWORD n1, const void* ign2, DWORD n2, SrOut* out, SrCtx* c, bool job, bool* noLot) {
    out->nIds = out->nSubs = 0;
    *noLot = false;
    c->n = 0, c->fail = SR_OK, c->over = 0, c->rec = NULL, c->flag = 1;  // 0x82F0B0: no record reaches the walk, visitor flag 1
    if (!world || !wm) return SR_FB_STATE;
    const u8* p = world[0x974] ? *(const u8* const*)(world + 0x95C) : NULL;  // RoutingWorld_GetLotPlanner 0x829BC0
    u8* wmw = *(u8* const*)(world + 0x964);
    if (!p || !wmw || p != planner) return SrShape(SRS_NOPLANNER);
    if (wmw[0x1CC] || wm[0x1CC]) return SR_FB_DIRTY;  // World_LotLookup would rebuild the lot index (wm+0x228)
    // phase 1, no lock, as the original runs them before its lock: 0x85AE60 and the lot lookups
    static SrNode nd;   // owner thread only; conv = the start point
    static SrPt cg;     // the goal point
    memset(&nd.conv, 0, sizeof(nd.conv));
    memset(&cg, 0, sizeof(cg));
    nd.conv.one = cg.one = *g_srOne;
    nd.raw = (const u8*)start;
    g_srConv(start, &nd.conv);
    g_srConv(goal, &cg);
    cg.level = nd.conv.level;  // 0x85F66F..0x85F678: the goal takes the start's level
    const SrPt* a = &nd.conv;  // 0x631E20's first point (its level picks the cell graph) and second point
    const SrPt* b = &cg;
    const u8* lot = g_srLotLookup(wmw, NULL, cg.v[0], cg.v[2]);  // 0x82F0B0: the goal's lot first
    if (!lot) {
        lot = g_srLotLookup(wmw, NULL, nd.conv.v[0], nd.conv.v[2]);
        if (!lot) { *noLot = true; return SR_OK; }  // returns 0: EditPath appends nothing
        a = &cg, b = &nd.conv;  // the two points swap (0x82F1A1)
    }
    const DWORD gLo = *(const DWORD*)(lot + 0x48), gHi = *(const DWORD*)(lot + 0x4C);
    lot = g_srLotLookup(wm, NULL, goal[0], goal[2]);  // EditPath's own lookups on the raw points: goal, else start
    if (!lot) lot = g_srLotLookup(wm, NULL, start[0], start[2]);
    nd.eLot = lot != NULL;
    nd.eLo = lot ? *(const DWORD*)(lot + 0x48) : 0;
    nd.eHi = lot ? *(const DWORD*)(lot + 0x4C) : 0;
    // phase 2, QL shared (try only)
    if (t_qlDepth || t_qlShared) return SR_FB_STATE;  // SRW locks are not recursive
    if (!TryAcquireSRWLockShared(&g_ql)) return SR_FB_TRY;
    if (g_steerBroken) { ReleaseSRWLockShared(&g_ql); return SR_FB_STATE; }
    const LONG seq = g_qlSeq;
    InterlockedExchange(&g_srInClone, 1);
    const LONG twEpoch = g_twEpoch, twGen = g_twGen;
    t_inRo = true;
    t_qlShared = 1;
    t_srFail = SR_OK;
    int why = job && !g_twArmed ? EP_WHY_JOBEND : g_twActive ? SR_FB_TRIP : SR_OK;
    if (!why) {
        const u8* entry = NULL;
        why = SrEntry(p, gLo, gHi, true, &entry);
        const u8* q = !why && entry ? SrCell(entry, a->level) : NULL;
        if (q) {
            float u0 = 0.0f, v0 = 0.0f, u1 = 0.0f, v1 = 0.0f;
            SrXform(entry, a->v[0], a->v[2], &u0, &v0);
            SrXform(entry, b->v[0], b->v[2], &u1, &v1);
            if ((why = SrWalk2(c, q, false, u0, v0, u1, v1, 0.0f)) == SR_OK) qsort(c->objs, c->n, sizeof(c->objs[0]), SrCmpPtr);
        }
    }
    if (!why) why = SrConsume(c, p, nd, ids0, n0, out, NULL, 0, ign1, n1, ign2, n2, goal);
    if (!why && t_srFail) why = t_srFail;
    if (!why && (g_qlSeq != seq || g_twActive || g_twEpoch != twEpoch)) why = SR_FB_TRIP;
    if (!why && job && g_twGen != twGen) why = EP_WHY_JOBEND;  // disarmed during the clone: writers unseen
    t_inRo = false;
    t_qlShared = 0;
    ReleaseSRWLockShared(&g_ql);
    InterlockedExchange(&g_srInClone, 0);
    return why;
}

static void EpFallback(int why) {
    int i = EP_FB_STATE;
    switch (why) {
    case SR_FB_TRY: i = EP_FB_TRY; break;
    case SR_FB_DIRTY: case SR_FB_LOTMISS: case SR_FB_DEFERRED: i = EP_FB_LOT; break;
    case SR_FB_SHAPE: i = EP_FB_SHAPE; break;
    case SR_FB_CACHE: i = EP_FB_CACHE; break;
    case SR_FB_TRIP: i = EP_FB_TRIP; break;
    case EP_WHY_JOBEND: i = EP_FB_JOBEND; break;
    default: break;
    }
    InterlockedIncrement(&ts3native_editpathro.epFb[i]);
}

static int EpCount(const u8* ids) { return (int)((*(DWORD* const*)(ids + 4) - *(DWORD* const*)ids) / 2); }

static volatile LONG g_epNoted;
static void EpShadow(u8* wm, u8* world, u8* planner, u8* ids, const float* start, const float* goal, DWORD opt, const void* ign1, DWORD n1,
                     const void* ign2, DWORD n2, SrOut* out, SrCtx* c) {
    EditPathRoCounters& k = ts3native_editpathro;
    const int n0 = EpCount(ids);
    g_epOrig(wm, world, planner, ids, start, goal, opt, NULL, ign1, n1, ign2, n2);
    bool noLot = false;
    const int why = EpRun(wm, world, planner, *(const DWORD* const*)ids, n0, start, goal, ign1, n1, ign2, n2, out, c, false, &noLot);
    InterlockedIncrement(&k.epShadowRuns);
    if (why) { EpFallback(why); return; }
    const int added = EpCount(ids) - n0;
    const DWORD* got = *(const DWORD* const*)ids + n0 * 2;
    if (added != out->nIds || (added && memcmp(got, out->ids, (size_t)added * 8) != 0)) {
        InterlockedIncrement(&k.epShadowMismatch);
        if (InterlockedIncrement(&g_epNoted) <= 16)
            Log("edit path RO: SHADOW MISMATCH start (%.2f, %.2f) goal (%.2f, %.2f): game appended %d ids, clone %d (%d footprints "
                "gathered; first ids %08lX / %08lX)", start[0], start[2], goal[0], goal[2], added, out->nIds, c->n, added ? got[0] : 0,
                out->nIds ? out->ids[0][0] : 0);
    }
}

static void __cdecl EpStub(u8* wm, u8* world, u8* planner, u8* ids, const float* start, const float* goal, DWORD opt, const void* excl,
                           const void* ign1, DWORD n1, const void* ign2, DWORD n2) {
    EditPathRoCounters& k = ts3native_editpathro;
    const DWORD tid = GetCurrentThreadId();
    if ((!k.epMode && !k.epShadow) || tid != g_async.owner) { g_epOrig(wm, world, planner, ids, start, goal, opt, excl, ign1, n1, ign2, n2); return; }
    if (excl || !ids || !start || !goal) {  // 0x85FF30 passes excl 0; anything else is not the form cloned here
        InterlockedIncrement(&k.epOtherForm);
        g_epOrig(wm, world, planner, ids, start, goal, opt, excl, ign1, n1, ign2, n2);
        return;
    }
    static SrOut out;  // owner thread only
    static SrCtx ctx;
    InterlockedIncrement(&k.epCalls);
    // docs/670 §14: at most kEpTries passes. A pass is the game's call with no job live, the clone's answer, or (Retry) a
    // rerun after a dropped answer; the last pass ends in the game's call whatever the state (today's wait)
    enum { kEpTries = 4 };
    bool counted = false;
    LARGE_INTEGER t0 = {};
    for (int pass = 0;; pass++) {
        if (g_async.state != A_RUNNING) {
            if (k.epShadow && !pass) {
                InterlockedIncrement(&g_rgOffSimOpen);  // no job starts while the two answers are compared (worker Dekker order)
                if (g_async.state == A_RUNNING) g_epOrig(wm, world, planner, ids, start, goal, opt, excl, ign1, n1, ign2, n2);
                else EpShadow(wm, world, planner, ids, start, goal, opt, ign1, n1, ign2, n2, &out, &ctx);
                RgOffSimDrop();
                return;
            }
            if (!k.epGuard || !k.epMode) {  // as before this switch: the game's call, no frame
                g_epOrig(wm, world, planner, ids, start, goal, opt, excl, ign1, n1, ign2, n2);
                if (pass) InterlockedIncrement(&k.epRetryOk);
                return;
            }
            // StartGuard: the stub saw no job, but the worker may take +0x918 before the game's call below does, and that
            // call would then wait for the whole job. The frame holds the start off for this one call (ServeAsync backs
            // off while g_rgOffSimOpen > 0; Dekker order, as the shadow path above)
            InterlockedIncrement(&g_rgOffSimOpen);
            if (g_async.state != A_RUNNING) {
                g_epOrig(wm, world, planner, ids, start, goal, opt, excl, ign1, n1, ign2, n2);
                RgOffSimDrop();
                InterlockedIncrement(&k.epGuardCalls);
                if (pass) InterlockedIncrement(&k.epRetryOk);
                return;
            }
            RgOffSimDrop();  // a job started in the gap: the clone path
            InterlockedIncrement(&k.epGuardRace);
        }
        if (!counted) {
            counted = true;
            InterlockedIncrement(&k.epJob);
            QueryPerformanceCounter(&t0);
        }
        if (!k.epMode) break;
        if (!AvMayRun(tid)) {  // the +0x918 owner (lent or recursive hold), a job callback or an open gate frame: no wait to save
            InterlockedIncrement(&k.epFb[EP_FB_MAYNOT]);
            g_epOrig(wm, world, planner, ids, start, goal, opt, excl, ign1, n1, ign2, n2);
            return;
        }
        bool noLot = false;
        const int why = EpRun(wm, world, planner, *(const DWORD* const*)ids, EpCount(ids), start, goal, ign1, n1, ign2, n2, &out, &ctx, true, &noLot);
        if (!why) {
            for (int i = 0; i < out.nIds; i++) g_srIdPush(ids, NULL, out.ids[i]);
            const LONG us = UsSince(t0);
            InterlockedIncrement(&k.epTaken);
            InterlockedExchangeAdd(&k.epIds, out.nIds);
            if (noLot) InterlockedIncrement(&k.epNoLot);
            InterlockedExchangeAdd(&k.epUs, us);
            Max(&k.epMaxUs, us);
            if (pass) InterlockedIncrement(&k.epRetryOk);
            return;
        }
        // Retry: a rerun starts S2's protocol again (sequence, epoch, generation read by EpRun), so its answer is as exact
        // as a first one. QL busy and a tripwire are a writer's short hold: yield first. Job end: the loop's top decides
        const bool again = k.epRetry && pass + 1 < kEpTries && (why == SR_FB_TRY || why == SR_FB_TRIP || why == EP_WHY_JOBEND) && UsSince(t0) < 1000;
        if (!again) { EpFallback(why); break; }
        InterlockedIncrement(&k.epRetryN);
        if (why != EP_WHY_JOBEND) SwitchToThread();
    }
    LARGE_INTEGER t1 = {};
    QueryPerformanceCounter(&t1);
    g_epOrig(wm, world, planner, ids, start, goal, opt, excl, ign1, n1, ign2, n2);  // the +0x918 wait this switch is about
    const LONG us = UsSince(t1);
    InterlockedExchangeAdd(&k.epGameUs, us);
    Max(&k.epGameMaxUs, us);
}

static const CallSite kEpSite = {EP_SITE, EP_TARGET, (void*)EpStub};

// [EditPathRO] Enabled=1: needs [RouteGate] Enforce=1 with the async path and the S2 writer set (QL, W1-W6, tripwires)
static void InstallEditPathRO(const wchar_t* ini) {
    EditPathRoCounters& k = ts3native_editpathro;
    if (!GetPrivateProfileIntW(L"EditPathRO", L"Enabled", 0, ini)) { Log("edit path RO off (Enabled=0)"); return; }
#ifdef TS3A_TABLE
    if (ts3a_noaddr) { Log("SKIP edit path RO: no EA 1.69 address, docs/670"); return; }
#endif
    if (!ts3native_counters.rgEnforce || !ts3native_counters.asyncReady) { Log("SKIP edit path RO: needs [RouteGate] Enforce=1 with the async path"); return; }
    if (!InstallSrWriters()) { Log("SKIP edit path RO: the S2 writer set (W1-W6, W4, tripwires) is not all in place"); return; }
    if (!PatchCalls("edit path RO (0x85FF30 -> RouteFollow_EditPath)", &kEpSite, 1)) return;
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
    k.epMode = Clamp(GetPrivateProfileIntW(L"EditPathRO", L"Mode", 1, ini), 0, 1);
    k.epShadow = Clamp(GetPrivateProfileIntW(L"EditPathRO", L"Shadow", 0, ini), 0, 1);
    k.epGuard = Clamp(GetPrivateProfileIntW(L"EditPathRO", L"StartGuard", 0, ini), 0, 1);
    k.epRetry = Clamp(GetPrivateProfileIntW(L"EditPathRO", L"Retry", 0, ini), 0, 1);
    k.epReady = 1;
    Log("OK   edit path RO: 0x%08lX + the S2 writer set (Mode=%ld Shadow=%ld StartGuard=%ld Retry=%ld)", EP_SITE, k.epMode, k.epShadow, k.epGuard,
        k.epRetry);
}

// the 60 s counter line (ts3native.cpp MaybeLogCounters)
static void EpLogCounters() {
    const EditPathRoCounters& k = ts3native_editpathro;
    if (!k.epReady) return;
    Log("edit path RO: mode %ld shadow %ld calls %ld, with a job %ld: answered %ld (ids %ld, no lot %ld; %ld ms, max %ld us), the game's "
        "call %ld ms (max %ld us); given back: state %ld, QL busy %ld, lot %ld, shape %ld, polygon cache %ld, tripwire %ld, job end %ld, "
        "may not %ld; other form %ld; shadow checks %ld mismatch %ld; start guard %ld: the game's call in a frame %ld, a job had started %ld; "
        "retry %ld: reruns %ld, answered after one %ld",
        k.epMode, k.epShadow, k.epCalls, k.epJob, k.epTaken, k.epIds, k.epNoLot, k.epUs / 1000, k.epMaxUs, k.epGameUs / 1000, k.epGameMaxUs,
        k.epFb[EP_FB_STATE], k.epFb[EP_FB_TRY], k.epFb[EP_FB_LOT], k.epFb[EP_FB_SHAPE], k.epFb[EP_FB_CACHE], k.epFb[EP_FB_TRIP],
        k.epFb[EP_FB_JOBEND], k.epFb[EP_FB_MAYNOT], k.epOtherForm, k.epShadowRuns, k.epShadowMismatch, k.epGuard, k.epGuardCalls, k.epGuardRace,
        k.epRetry, k.epRetryN, k.epRetryOk);
}
