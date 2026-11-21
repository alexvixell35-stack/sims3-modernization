// Gate-0 fix for IC_Route_IsPointObstructedBySim (docs/86 §10). Included by ts3native.cpp after lotfd.cpp (uses steerro.cpp's
// QL, writer set, point query and lot transform).
//
// [ObstructedRO] Enabled=1 (default 0; needs [RouteGate] Enforce=1 and the async path). The IC 0x751480 (cdecl(handle,
// Vector3 point), result EDX:EAX = the id of a Sim whose footprint covers the point, else 0; script: SimRoutingComponent
// after a route with CheckForFootprintsNearGoals) touches routing state twice: the lot transform 0x62EF30 (gate 0, key
// 0x62EF7A: 99% of its waits in bp-p1b) and RoutingWorld_ObstacleQuery 0x82EDB0 (+0x918). While a job runs the clone answers
// from the same data without either wait:
//   phase 1, no lock, the game's own calls in the IC's order (they run before its first routing touch today, i.e. already
//   beside jobs): RouteHandle_Lookup 0x74B2C0 and the route's vtable pair +0x10 / +0x14, World_LotLookup, 0xADD860 (the
//   point's level). The height (0x74A8F0, 0xB015B0) is not computed: the obstacle query reads only x, z and the level.
//   phase 2, QL shared (try only), the S2 protocol (tripwires armed, sequence, generation): GetOrInsert's hit (SrEntry; a
//   miss would insert: the game's call), the transform (SrXform, bit-exact), the obstacle query as S2's point query (SrQuery:
//   footprints in 0x82EDB0's order, no refcounts), then the IC's loop: type 0x14, not the route's own Sim, not in the
//   route's ignore list (0x81C970 inline), slot +0xC0, slot +0xD4 = 0x80F030 on a valid level-0 polygon cache (else the
//   original would rebuild it: the game's call). The footprint refcount +1/-1 of the original loop is skipped (net 0).
// Any doubt (no job, QL busy, the running job is this route's, dirty lot index, lot not in the table, deferred quadtree
// flush, invalid or changed polygon cache, unknown slot, buffer limit, tripwire) undoes phase 1's route pair and calls the
// original (today's behaviour: it waits at gate 0). Counters v41 (+0x1524): orMode (+0x1528, --ab obstructedro) and
// orShadow (+0x152C, --ab obstructedshadow): with no job running, the game's call, then the clone, compared.

static const DWORD OR_IC = TS3A(0x00751480);
static const u8 kOrPro[] = {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF0};  // push ebp; mov ebp,esp; and esp,-16 (no relative operand)
struct OrV3 { float x, y, z; };  // the IC's Vector3, by value at [ebp+0xC]
typedef u64(__cdecl* OrFn)(DWORD handle, OrV3 p);
static OrFn g_orOrig;
typedef u8*(__fastcall* OrRouteFn)(const void* rm, void* edx, DWORD handle);    // RouteHandle_Lookup 0x74B2C0, ret 4
typedef void(__fastcall* OrRefFn)(u8* route, void* edx);                         // route vtable +0x10 / +0x14
typedef void(__cdecl* OrLevelFn)(const u8* lot, const float* pt, float* io);     // 0xADD860: io[0..1] floats, io[2] the level
typedef u8(__fastcall* OrPtFn)(const u8* fp, void* edx, DWORD level, float u, float v, float r);  // slot +0xD4 0x80F030, ret 0x10
typedef int (*OrQueryFn)(SrCtx* c, const u8* p, DWORD lo, DWORD hi, const SrPt& pt, float r, const u8* rec);
static int OrQueryGame(SrCtx* c, const u8* p, DWORD lo, DWORD hi, const SrPt& pt, float r, const u8* rec) {
    return SrQuery(c, p, lo, hi, pt, true, r, rec);
}
static OrRouteFn g_orRoute = (OrRouteFn)(uintptr_t)TS3A(0x0074B2C0);
static OrLevelFn g_orLevel = (OrLevelFn)(uintptr_t)TS3A(0x00ADD860);
static OrQueryFn g_orQuery = OrQueryGame;  // selftest hands in fake footprint lists
static u8* const* g_orRmSlot = (u8* const*)(uintptr_t)TS3A(0x011D40C4);  // the route handle table the IC passes to 0x74B2C0
static const float* g_orRadius = (const float*)(uintptr_t)TS3A(0x0106E8A8);  // 0.25: query radius and the +0xD4 radius
static const DWORD OR_SLOT_PT = TS3A(0x0080F030);  // slot +0xD4 of the 8 footprint classes whose type comes from +0x35

enum { OR_OK, OR_FB_STATE, OR_FB_TRY, OR_FB_LOT, OR_FB_CACHE, OR_FB_SHAPE, OR_FB_TRIP };
static int OrMap(int sr) {  // steerro.cpp reasons -> orFb
    switch (sr) {
    case SR_OK: return OR_OK;
    case SR_FB_TRY: return OR_FB_TRY;
    case SR_FB_DIRTY: case SR_FB_LOTMISS: case SR_FB_DEFERRED: return OR_FB_LOT;
    case SR_FB_CACHE: return OR_FB_CACHE;
    case SR_FB_SHAPE: return OR_FB_SHAPE;
    case SR_FB_TRIP: return OR_FB_TRIP;
    default: return OR_FB_STATE;
    }
}

// The IC after its 0x82EDB0 call (0x7516B8..0x7517B7) on the clone's footprint list
static int OrScan(const SrCtx* c, const u8* route, float u, float v, u64* res) {
    const DWORD* ign = *(const DWORD* const*)(route + 0x158 + 0x38);  // 0x81C970: 8-byte ids in [+0x38, +0x3C)
    const DWORD* ignEnd = *(const DWORD* const*)(route + 0x158 + 0x3C);
    for (int i = 0; i < c->n; i++) {
        const u8* obj = c->objs[i];
        const void* const* vt = *(const void* const* const*)obj;
        if (((SlotIntFn)vt[3])(obj, NULL) != 0x14) continue;
        const unsigned __int64 id = ((SlotIdFn)vt[0x84 / 4])(obj, NULL);
        const DWORD lo = (DWORD)id, hi = (DWORD)(id >> 32);
        if (lo == *(const DWORD*)(route + 0x40) && hi == *(const DWORD*)(route + 0x44)) continue;
        bool skip = false;
        for (const DWORD* p = ign; p != ignEnd && !skip; p += 2) skip = p[0] == lo && p[1] == hi;
        if (skip || !((SlotBoolFn)vt[0xC0 / 4])(obj, NULL)) continue;
        if (g_srCheckSlots && (DWORD)(uintptr_t)vt[0xD4 / 4] != OR_SLOT_PT) return SrShape(SRS_SLOT);
        const u8* cache = *(const u8* const*)(obj + 0x38);
        if (!cache || !cache[0x20]) return SR_FB_CACHE;  // 0x80F030 would rebuild level 0 (0x80E5B0, W6a)
        u8 before[0x24];
        memcpy(before, cache, sizeof(before));
        const u8 hit = ((OrPtFn)vt[0xD4 / 4])(obj, NULL, 0, u, v, *g_orRadius);
        if (memcmp(before, cache, sizeof(before)) != 0) return SR_FB_CACHE;  // a writer outside W1-W6
        if (hit) { *res = id; return SR_OK; }
    }
    return SR_OK;
}

// Phase 2 under QL shared, with SrRun's protocol (steerro.cpp): no answer unless every writer entry beside it was seen
static int OrLocked(const u8* p, DWORD lotLo, DWORD lotHi, const u8* qlot, const SrPt& pt, const u8* route, bool job, u64* res) {
    if (t_qlDepth || t_qlShared) return SR_FB_STATE;  // SRW locks are not recursive
    if (!TryAcquireSRWLockShared(&g_ql)) return SR_FB_TRY;
    if (g_steerBroken) { ReleaseSRWLockShared(&g_ql); return SR_FB_STATE; }
    const LONG seq = g_qlSeq;
    InterlockedExchange(&g_srInClone, 1);
    const LONG twEpoch = g_twEpoch;
    const LONG twGen = g_twGen;
    const bool jobEnd = job && !g_twArmed;
    t_inRo = true;
    t_qlShared = 1;
    t_srFail = SR_OK;
    int why = jobEnd ? SR_FB_STATE : g_twActive ? SR_FB_TRIP : SR_OK;
    const u8* entry = NULL;
    if (!why) why = SrEntry(p, lotLo, lotHi, false, &entry);  // GetOrInsert's hit (0x62EF30 -> 0x62EAB0)
    if (!why) {
        float u, v;
        SrXform(entry, pt.v[0], pt.v[2], &u, &v);
        if (qlot) {  // else 0x82EDB0 returns 0 and the IC 0
            __declspec(align(16)) u8 rec[0x30] = {};  // 0x82EDB0's option record: the route's option word
            *(DWORD*)rec = *(const DWORD*)(route + 0x3C);
            rec[0x24] = 1;
            static SrCtx c;  // owner thread only
            why = g_orQuery(&c, p, *(const DWORD*)(qlot + 0x48), *(const DWORD*)(qlot + 0x4C), pt, *g_orRadius, rec);
            if (!why) why = OrScan(&c, route, u, v, res);
        }
    }
    if (!why && t_srFail) why = t_srFail;
    // (S2's own counters steerSeqSkew / steerTripDiscard / s2JobEndDrop stay S2's: these drops count in orFb only)
    if (!why && (g_qlSeq != seq || g_twActive || g_twEpoch != twEpoch)) why = SR_FB_TRIP;
    if (!why && job && g_twGen != twGen) why = SR_FB_STATE;  // disarmed during the clone: writers unseen
    t_inRo = false;
    t_qlShared = 0;
    ReleaseSRWLockShared(&g_ql);
    InterlockedExchange(&g_srInClone, 0);
    if (why) *res = 0;
    return why;
}

// The IC between its route pair (0x7514BF..0x7516B3 for phase 1); SR_* reason
static int OrBody(const u8* route, const OrV3& in, bool job, u64* res) {
    u8* world = g_srWorldGet();
    const u8* wm = *g_srWmSlot;
    if (!world) return SR_FB_STATE;  // the original calls 0x829BC0 on it
    const u8* p = world[0x974] ? *(const u8* const*)(world + 0x95C) : NULL;  // RoutingWorld_GetLotPlanner 0x829BC0
    if (!wm) return SR_OK;  // the IC returns 0 before any routing touch
    SrPt pt;
    memset(&pt, 0, sizeof(pt));
    pt.v[0] = in.x, pt.v[1] = in.y, pt.v[2] = in.z, pt.v[3] = in.x;  // 0x76BF90: {x, y, z, x}
    pt.one = *g_srOne;
    const u8* lot = g_srLotLookup((void*)wm, NULL, in.x, in.z);
    if (!lot) return SR_OK;  // the IC returns 0 before any routing touch
    __declspec(align(16)) float io[4] = {};  // 0xADD860 writes io[0..2] and reads nothing of it
    g_orLevel(lot, pt.v, io);
    pt.level = *(const LONG*)&io[2];
    // (the IC's height y = 0x74A8F0(...) goes into the query point's y, which the query never reads: not computed)
    u8* wmw = *(u8* const*)(world + 0x964);
    if (!p || !wmw) return SrShape(SRS_NOPLANNER);  // the original would fault
    if (wmw[0x1CC] || wm[0x1CC]) return SR_FB_DIRTY;  // the query's World_LotLookup would rebuild the lot index (wm+0x228)
    const u8* qlot = g_srLotLookup(wmw, NULL, in.x, in.z);  // 0x82EDB0's lookup on the same x, z
    return OrLocked(p, *(const DWORD*)(lot + 0x48), *(const DWORD*)(lot + 0x4C), qlot, pt, route, job, res);
}

// The whole IC: route lookup and its vtable pair around the body, as the original
static int OrRun(DWORD handle, const OrV3& in, bool job, u64* res) {
    *res = 0;
    const u8* rm = *g_orRmSlot;
    if (!rm) return SR_OK;
    u8* route = g_orRoute(rm, NULL, handle);
    if (!route) return SR_OK;
    ((OrRefFn)(*(void* const* const*)route)[0x10 / 4])(route, NULL);
    const int why = OrBody(route, in, job, res);
    ((OrRefFn)(*(void* const* const*)route)[0x14 / 4])(route, NULL);
    return why;
}

static void OrFallback(int sr) {
    const int why = OrMap(sr);
    if (why) InterlockedIncrement(&ts3native_counters.orFb[why - 1]);
}

static volatile LONG g_orNoted;
static u64 OrShadow(DWORD handle, OrV3 p) {
    Ts3nCounters& k = ts3native_counters;
    const u64 r = g_orOrig(handle, p);
    u64 r2;
    const int why = OrRun(handle, p, false, &r2);
    InterlockedIncrement(&k.orShadowRuns);
    if (why) { OrFallback(why); return r; }
    if (r != r2) {
        InterlockedIncrement(&k.orShadowMismatch);
        if (InterlockedIncrement(&g_orNoted) <= 16)
            Log("obstructed RO: shadow mismatch route %08lX at (%.2f, %.2f): game %08lX:%08lX, clone %08lX:%08lX", handle, p.x, p.z,
                (DWORD)(r >> 32), (DWORD)r, (DWORD)(r2 >> 32), (DWORD)r2);
    }
    return r;
}

static u64 __cdecl OrHook(DWORD handle, OrV3 p) {
    Ts3nCounters& k = ts3native_counters;
    const DWORD tid = GetCurrentThreadId();
    if ((!k.orMode && !k.orShadow) || tid != g_async.owner) return g_orOrig(handle, p);
    InterlockedIncrement(&k.orCalls);
    if (g_async.state != A_RUNNING) {
        if (!k.orShadow) return g_orOrig(handle, p);
        InterlockedIncrement(&g_rgOffSimOpen);  // no job starts while the two answers are compared (worker Dekker order)
        const u64 r = g_async.state == A_RUNNING ? g_orOrig(handle, p) : OrShadow(handle, p);
        RgOffSimDrop();
        return r;
    }
    if (!k.orMode) return g_orOrig(handle, p);
    // a job runs: read-only answer, unless this thread holds +0x918, serves the job's callback, has a gate frame open, or the
    // job plans this very route
    const RgThread& t = t_rg;
    int why = SR_FB_STATE;
    if (!g_inInvoke && k.rgOwnerTid != (LONG)tid && t.n == t.readers && g_async.handle != handle) {
        LARGE_INTEGER t0;
        QueryPerformanceCounter(&t0);
        u64 r;
        why = OrRun(handle, p, true, &r);
        if (!why) {
            const LONG us = UsSince(t0);
            InterlockedIncrement(&k.orTaken);
            MwSkip(MW_OR);
            if (r) InterlockedIncrement(&k.orHits);
            InterlockedExchangeAdd(&k.orUs, us);
            Max(&k.orMaxUs, us);
            return r;
        }
    }
    OrFallback(why);
    return g_orOrig(handle, p);
}

static void InstallObstructedRO(const wchar_t* ini) {
    Ts3nCounters& k = ts3native_counters;
    if (!GetPrivateProfileIntW(L"ObstructedRO", L"Enabled", 0, ini)) { Log("obstructed RO off (Enabled=0)"); return; }
    if (!k.rgEnforce || !k.asyncReady) { Log("SKIP obstructed RO: needs [RouteGate] Enforce=1 with the async path"); return; }
    if (!Matches((const void*)(uintptr_t)OR_IC, kOrPro, sizeof(kOrPro))) { Log("SKIP obstructed RO: 0x751480 prologue differs"); return; }
    if (!InstallSrWriters()) { Log("SKIP obstructed RO: the S2 writer set (W1-W6, W4, tripwires) is not all in place"); return; }
    g_orOrig = (OrFn)InstallDetour("IC_Route_IsPointObstructedBySim", OR_IC, kOrPro, sizeof(kOrPro), (void*)OrHook, &g_orOrig);
    if (!g_orOrig) return;
    k.orMode = Clamp(GetPrivateProfileIntW(L"ObstructedRO", L"Mode", 1, ini), 0, 1);
    k.orShadow = Clamp(GetPrivateProfileIntW(L"ObstructedRO", L"Shadow", 0, ini), 0, 1);
    k.orReady = 1;
    Log("OK   obstructed RO: 0x751480 + the S2 writer set (Mode=%ld Shadow=%ld)", k.orMode, k.orShadow);
}
