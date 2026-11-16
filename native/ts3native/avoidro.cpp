// [AvoidRO] (docs/137): read-only avoidance steering while an async route job runs. Included by ts3native.cpp after
// obstructro.cpp (uses steerro.cpp's QL, writer set, point query, lot table hit and transform; savewrite.cpp's PatchCalls).
//
// With S2 on, the sim's next +0x918 wait is RouteFollow_A 0x846B70 (cdecl(path, agent, goal*, ignore*, n), EDX:EAX = the
// id of an obstruction near the path's goal; key 0x846D21) and its sibling RouteFollow_B 0x85B470 (cdecl(wm, world, P,
// point*, option word), AL; key 0x85B50C). Both call RoutingWorld_ObstacleQuery 0x82EDB0 with mode 1 and radius 0, then
// test the footprints without the lock. While a job runs the clone answers from the same data:
//   phase 1, no lock, the game's own calls (they run before the lock today): the path's options (vtable +0xC), the world,
//   lot planner, goal level 0x8468C0, option word 0x953690 or 0x100, World_LotLookup on both world managers. The goal
//   height 0x829470 is not computed: the query reads only x, z and the level.
//   phase 2, QL shared (try only), the S2 protocol (tripwires armed, sequence, generation, T-AV): the point query with
//   mode 1 (SrQuery, footprints in 0x82EDB0's order, no refcounts), then A's consumer: sub type 0x14 skip, the caller's
//   ignore list 0x85AAA0, the options' id lists 0x81C970 / 0x81C930, no goal lot = the first survivor hits, else the
//   GetOrInsert hit (SrEntry; a miss would insert: the game's call), the transform (SrXform, bit-exact), outer slot 0
//   levels and slot +0xB8 (0x80E8A0 on a valid, unchanged polygon cache; 0xBFB320 = false). B: the same query, the
//   GetOrInsert hit when wm has a lot at the raw point, then 1 when a footprint's sub type is 0xC, 0xD or 0x17.
// A hit returns the id at once (a hit with id 0 is a miss, as the original). A miss sets t_avMiss and runs the game's body:
// the A-site (the call at 0x846D1C, retargeted to AvSite) returns 0 without the query, so the body goes on to its Sim scan
// (no lock, no store outside the stack) exactly as after a query that found nothing. Any doubt calls the game's code
// (today's wait). T-AV: RoutingOptions_Assign 0x81D090 and RouteFollow_EditPathForRoute 0x85FF30 (the followed path's id
// lists, read by the consumer) wrapped; an entry off the sim thread while armed or beside a clone drops its answer.
// Counters v77 (v74 on its branch) (+0x31EC): avMode (+0x31F0, --ab avoidro), avShadow (+0x31F4, --ab avoidshadow).

static const DWORD AV_A = TS3A(0x00846B70), AV_B = TS3A(0x0085B470), AV_SITE = TS3A(0x00846D1C), AV_QUERY = TS3A(0x0082EDB0);
static const u8 kAvPro[] = {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF0};  // push ebp; mov ebp,esp; and esp,-16 (A, B and 0x85FF30)
typedef u64(__cdecl* AvAFn)(u8* path, u8* agent, const float* goal, const void* ign, DWORD n);
typedef u8(__cdecl* AvBFn)(u8* wm, u8* world, u8* p, const float* pt, DWORD opt);
static AvAFn g_avAOrig;
static AvBFn g_avBOrig;
typedef u8*(__fastcall* AvOptsFn)(u8* path, void* edx);                                     // path vtable +0xC
typedef LONG(__cdecl* AvLevelFn)(const float* goal);                                        // 0x8468C0
typedef DWORD(__fastcall* AvPostureFn)(const u8* a, void* edx);                             // 0x953690
typedef u8(__fastcall* AvOptIdFn)(const u8* opts, void* edx, DWORD lo, DWORD hi);           // 0x81C970 / 0x81C930, ret 8
typedef DWORD(__fastcall* AvCountFn)(const u8* fp, void* edx);                              // outer slot 0
typedef u8(__fastcall* AvPtFn)(const u8* fp, void* edx, DWORD level, const float* pt);     // outer slot +0xB8, ret 8
typedef int (*AvQueryFn)(SrCtx* c, const u8* p, DWORD lo, DWORD hi, const SrPt& pt, const u8* rec);
static int AvQueryGame(SrCtx* c, const u8* p, DWORD lo, DWORD hi, const SrPt& pt, const u8* rec) {
    return SrQuery(c, p, lo, hi, pt, true, 0.0f, rec, true);  // radius 0, mode 1
}
static AvLevelFn g_avLevel = (AvLevelFn)(uintptr_t)TS3A(0x008468C0);
static AvPostureFn g_avPosture = (AvPostureFn)(uintptr_t)TS3A(0x00953690);
static AvOptIdFn g_avOpt38 = (AvOptIdFn)(uintptr_t)TS3A(0x0081C970);
static AvOptIdFn g_avOpt58 = (AvOptIdFn)(uintptr_t)TS3A(0x0081C930);
static AvQueryFn g_avQuery = AvQueryGame;  // selftest hands in fake footprint lists
static const DWORD AV_PT_POLY = TS3A(0x0080E8A0), AV_PT_NONE = TS3A(0x00BFB320);  // outer +0xB8 targets (docs/137 §2)

// --- T-AV: the followed path's option writers (docs/137 §4.4). C wrappers (count, call, leave), installed with the hooks.
// 0x81D090 is thiscall(src) ret 4; 0x85FF30 is cdecl(3 args); both read no register but ecx [verified disassembly].
static const u8 kAvAssignPro[] = {0x56, 0x8B, 0x74, 0x24, 0x08};  // push esi; mov esi,[esp+8]
static void* g_avTwTramp[2];
static volatile LONG g_avTwActive;  // T-AV calls running now off the sim thread
static volatile LONG g_avTwEpoch;   // moves when one of them leaves
static bool AvTwEnter(int i) {
    if (t_snapRun || RgIsSim(GetCurrentThreadId())) return false;  // P4 clone run / the sim's own calls (the clone calls none)
    InterlockedIncrement(&g_avTwActive);  // before the window test (the clone sets g_srInClone, then reads g_avTwActive)
    if (!g_twArmed && g_async.state != A_RUNNING && !g_srInClone) { InterlockedDecrement(&g_avTwActive); return false; }
    InterlockedIncrement(&ts3native_counters.avTripAv);
    InterlockedIncrement(&ts3native_counters.avTwSite[i]);
    return true;
}
static void AvTwLeave() {  // the epoch first, so a clone that sees g_avTwActive drop also sees it move
    InterlockedIncrement(&g_avTwEpoch);
    InterlockedDecrement(&g_avTwActive);
}
static u64 __fastcall AvTwAssign(u8* self, void* edx, const void* src) {
    const bool on = AvTwEnter(0);
    const u64 r = ((u64(__fastcall*)(u8*, void*, const void*))g_avTwTramp[0])(self, edx, src);
    if (on) AvTwLeave();
    return r;
}
static u64 __cdecl AvTwEdit(DWORD a, DWORD b, DWORD c) {
    const bool on = AvTwEnter(1);
    const u64 r = ((u64(__cdecl*)(DWORD, DWORD, DWORD))g_avTwTramp[1])(a, b, c);
    if (on) AvTwLeave();
    return r;
}

// --- the clone ---
enum { AV_FB_STATE, AV_FB_TRY, AV_FB_DIRTY, AV_FB_LOTMISS, AV_FB_DEFERRED, AV_FB_CACHE, AV_FB_SHAPE, AV_FB_TRIP, AV_FB_PIN,
       AV_FB_JOBEND };
static __declspec(thread) bool t_avPin, t_avJobEnd;  // why the last AvLocked dropped (pin moved / disarmed)
static __declspec(thread) LONG t_avMiss;              // 1: the A-site skips the query once (2: the same, shadow, not counted)
static SrCtx g_avCtx;  // owner thread only: the last clone's footprints (shadow compares them with 0x82EDB0's)

// Phase 1 output (the inputs of the original's 0x82EDB0 call and of its consumer)
struct AvIn { u8* world; const u8* p; const u8* opts; const u8* lotE; const u8* qlot; DWORD opt; float rx, rz; SrPt pt; };

// Phase 2 under QL shared, SrRun's protocol (steerro.cpp) plus T-AV: no answer unless every writer entry beside it was seen
template <class F> static int AvLocked(bool job, F body) {
    Ts3nCounters& k = ts3native_counters;
    t_avPin = t_avJobEnd = false;
    if (t_qlDepth || t_qlShared) return SR_FB_STATE;  // SRW locks are not recursive
    if (!TryAcquireSRWLockShared(&g_ql)) return SR_FB_TRY;
    if (g_steerBroken) { ReleaseSRWLockShared(&g_ql); return SR_FB_STATE; }
    const LONG seq = g_qlSeq;
    InterlockedExchange(&g_srInClone, 1);  // T4 / T-S7 / T-AV writers on other threads count from here
    const LONG twEpoch = g_twEpoch, avEpoch = g_avTwEpoch;
    const LONG twGen = g_twGen;  // then g_twArmed (TwArm's disarm order is the reverse)
    bool jobEnd = job && !g_twArmed;
    const LONG w0 = k.steerSimWriteInRo, pin0 = k.steerPinMoved;
    t_inRo = true;
    t_qlShared = 1;
    t_srFail = SR_OK;
    int why = jobEnd ? SR_FB_STATE : g_twActive || g_avTwActive ? SR_FB_TRIP : SR_OK;
    if (!why) why = body();
    if (!why && t_srFail) why = t_srFail;
    if (!why && (g_qlSeq != seq || g_twActive || g_twEpoch != twEpoch || g_avTwActive || g_avTwEpoch != avEpoch)) why = SR_FB_TRIP;
    if (!why && job && g_twGen != twGen) { jobEnd = true; why = SR_FB_STATE; }  // disarmed during the clone: writers unseen
    t_inRo = false;
    t_qlShared = 0;
    ReleaseSRWLockShared(&g_ql);
    InterlockedExchange(&g_srInClone, 0);
    if (k.steerSimWriteInRo != w0) InterlockedIncrement(&k.avSimWriteInRo);
    t_avPin = why == SR_FB_TRIP && k.steerPinMoved != pin0;
    t_avJobEnd = jobEnd;
    return why;
}

static bool AvSlot0Ok(DWORD s) { return s == TS3A(0x00619EF0) || s == TS3A(0x0083F5F0); }  // 1 or 3 levels (docs/137 §2)
// A's consumer slots on top of SrSlotsOk (checked for every collected footprint): slot 0 and +0xB8 (the child 0xFEBF90
// forwards slot 0 to its parent and has +0xB8 = 0xBFB320)
static bool AvSlotsOk(const u8* obj) {
    if (!g_srCheckSlots) return true;
    const DWORD* vt = *(const DWORD* const*)obj;
    const DWORD b8 = vt[0xB8 / 4];
    if (b8 != AV_PT_POLY && b8 != AV_PT_NONE) return false;
    if (vt[0] != TS3A(0x00626210)) return AvSlot0Ok(vt[0]);
    const u8* parent = *(const u8* const*)(obj + 0xC);
    return parent && AvSlot0Ok(**(const DWORD* const*)parent);
}

// RouteFollow_A's consumer (0x846D41..0x846EF7) on the clone's footprints; *id = 0: no footprint answers (the Sim scan runs)
static int AvConsumeA(const SrCtx* c, const AvIn& in, const void* ign, DWORD n, u64* id) {
    *id = 0;
    const u8* entry = NULL;
    __declspec(align(16)) float vec[4];
    for (int i = 0; i < c->n; i++) {
        const u8* obj = c->objs[i];
        const u8* sub = obj + 4;
        const void* const* svt = *(const void* const* const*)sub;
        if (((SlotIntFn)svt[2])(sub, NULL) == 0x14) continue;
        const u64 sid = ((SlotIdFn)svt[1])(sub, NULL);
        const DWORD lo = (DWORD)sid, hi = (DWORD)(sid >> 32);
        if (g_srIdList(lo, hi, ign, n)) continue;
        if (in.opts && (g_avOpt38(in.opts, NULL, lo, hi) || g_avOpt58(in.opts, NULL, lo, hi))) continue;
        if (!in.lotE) { *id = sid; return SR_OK; }  // no lot at the goal: the first survivor is the answer
        if (!AvSlotsOk(obj)) return SrShape(SRS_SLOT);
        if (!entry) {  // 0x62EF30 (GetOrInsert) on the goal's lot, called per survivor by the original: same answer each time
            const int why = SrEntry(in.p, *(const DWORD*)(in.lotE + 0x48), *(const DWORD*)(in.lotE + 0x4C), false, &entry);
            if (why) return why;
            float u, v;
            SrXform(entry, in.rx, in.rz, &u, &v);
            vec[0] = vec[3] = u, vec[1] = 0.0f, vec[2] = v;  // {u, 0, v, u}
        }
        const void* const* vt = *(const void* const* const*)obj;
        const DWORD levels = ((AvCountFn)vt[0])(obj, NULL);
        const bool poly = !g_srCheckSlots || (DWORD)(uintptr_t)vt[0xB8 / 4] == AV_PT_POLY;  // 0xBFB320 reads nothing
        for (DWORD l = 0; l < levels; l++) {
            u8 hit;
            if (poly) {
                const u8* cache = *(const u8* const*)(obj + 0x38);
                if (!cache || !cache[l * 0x24 + 0x20]) return SR_FB_CACHE;  // 0x80E8A0 would rebuild it (0x80E5B0, W6a)
                u8 before[0x24];
                memcpy(before, cache + l * 0x24, sizeof(before));
                hit = ((AvPtFn)vt[0xB8 / 4])(obj, NULL, l, vec);
                if (memcmp(before, cache + l * 0x24, sizeof(before)) != 0) return SR_FB_CACHE;  // a writer outside W1-W6
            } else {
                hit = ((AvPtFn)vt[0xB8 / 4])(obj, NULL, l, vec);
            }
            if (hit) { *id = sid; return SR_OK; }  // id 0 included: the original then goes on to the Sim scan, as a miss
        }
    }
    return SR_OK;
}

static void AvPoint(AvIn* in, const float* src, LONG level) {
    memset(&in->pt, 0, sizeof(in->pt));
    in->pt.v[0] = src[0], in->pt.v[1] = src[1], in->pt.v[2] = src[2], in->pt.v[3] = src[3];
    in->pt.one = *g_srOne;
    in->pt.level = level;
}

// The whole A clone. *none: the original takes no lock at all (no routing world: it goes straight to its Sim scan)
static int AvRunA(u8* path, u8* agent, const float* goal, const void* ign, DWORD n, bool job, u64* id, AvIn* in, bool* none) {
    *id = 0;
    *none = false;
    g_avCtx.n = 0;
    in->lotE = in->qlot = NULL;
    in->opts = path ? ((AvOptsFn)(*(void* const* const*)path)[0xC / 4])(path, NULL) : NULL;
    in->world = g_srWorldGet();
    if (!in->world) { *none = true; return SR_OK; }
    u8* wm = *g_srWmSlot;
    u8* wmw = *(u8* const*)(in->world + 0x964);
    in->p = in->world[0x974] ? *(const u8* const*)(in->world + 0x95C) : NULL;  // RoutingWorld_GetLotPlanner 0x829BC0
    if (!wm || !wmw || !in->p) return SrShape(SRS_NOPLANNER);  // the original would fault (or reads no lot index we know)
    if (wm[0x1CC] || wmw[0x1CC]) return SR_FB_DIRTY;  // a lookup (also inside 0x8468C0) would rebuild the lot index
    AvPoint(in, goal, g_avLevel(goal));
    in->rx = goal[0], in->rz = goal[2];
    in->lotE = g_srLotLookup(wm, NULL, goal[0], goal[2]);  // the consumer's own lookup
    u8* a = agent ? agent - 8 : NULL;  // 0x846CF1
    in->opt = a ? g_avPosture(a, NULL) : 0x100;
    in->qlot = g_srLotLookup(wmw, NULL, goal[0], goal[2]);  // 0x82EDB0's lookup: none = it returns 0 before any lock
    if (!in->qlot) return SR_OK;
    return AvLocked(job, [&]() -> int {
        __declspec(align(16)) u8 rec[0x30] = {};  // 0x82EDB0's option record
        *(DWORD*)rec = in->opt;
        rec[0x24] = 1;
        const int why = g_avQuery(&g_avCtx, in->p, *(const DWORD*)(in->qlot + 0x48), *(const DWORD*)(in->qlot + 0x4C), in->pt, rec);
        return why ? why : AvConsumeA(&g_avCtx, *in, ign, n, id);
    });
}

// The whole B clone (0x85B470): *res = its AL
static int AvRunB(u8* wm, u8* world, u8* pB, const float* raw, DWORD opt, bool job, u8* res, AvIn* in) {
    *res = 0;
    g_avCtx.n = 0;
    in->world = world;
    in->opt = opt;
    in->opts = NULL;
    in->lotE = in->qlot = NULL;
    if (!world) return SrShape(SRS_NOPLANNER);  // the original would fault in 0x82EDB0
    u8* wmw = *(u8* const*)(world + 0x964);
    in->p = world[0x974] ? *(const u8* const*)(world + 0x95C) : NULL;  // the query's planner
    if (!wmw || !in->p) return SrShape(SRS_NOPLANNER);
    if (wmw[0x1CC] || (wm && wm[0x1CC])) return SR_FB_DIRTY;
    memset(&in->pt, 0, sizeof(in->pt));
    in->pt.one = *g_srOne;
    g_srConv(raw, &in->pt);  // 0x85AE60, as the original before its query
    in->rx = raw[0], in->rz = raw[2];
    in->qlot = g_srLotLookup(wmw, NULL, in->pt.v[0], in->pt.v[2]);
    if (!in->qlot) return SR_OK;  // 0x82EDB0 returns 0: B returns 0 before any other read
    if (!wm) return SrShape(SRS_NOPLANNER);  // the original calls World_LotLookup on it next
    in->lotE = g_srLotLookup(wm, NULL, raw[0], raw[2]);
    if (in->lotE && !pB) return SrShape(SRS_NOPLANNER);  // 0x62EF30 on a NULL planner
    return AvLocked(job, [&]() -> int {
        __declspec(align(16)) u8 rec[0x30] = {};
        *(DWORD*)rec = opt;
        rec[0x24] = 1;
        int why = g_avQuery(&g_avCtx, in->p, *(const DWORD*)(in->qlot + 0x48), *(const DWORD*)(in->qlot + 0x4C), in->pt, rec);
        if (why) return why;
        if (in->lotE) {  // the original's 0x62EF30 (result unused): a miss would insert
            const u8* entry;
            if ((why = SrEntry(pB, *(const DWORD*)(in->lotE + 0x48), *(const DWORD*)(in->lotE + 0x4C), false, &entry)) != SR_OK) return why;
        }
        for (int i = 0; i < g_avCtx.n; i++) {
            const u8* sub = g_avCtx.objs[i] + 4;
            const int t = ((SlotIntFn)(*(const void* const* const*)sub)[2])(sub, NULL);
            if (t == 0xC || t == 0xD || t == 0x17) { *res = 1; break; }
        }
        return SR_OK;
    });
}

static void AvFallback(int sr) {
    int i;
    switch (sr) {
    case SR_FB_TRY: i = AV_FB_TRY; break;
    case SR_FB_DIRTY: i = AV_FB_DIRTY; break;
    case SR_FB_LOTMISS: i = AV_FB_LOTMISS; break;
    case SR_FB_DEFERRED: i = AV_FB_DEFERRED; break;
    case SR_FB_CACHE: i = AV_FB_CACHE; break;
    case SR_FB_SHAPE: i = AV_FB_SHAPE; break;
    case SR_FB_TRIP: i = t_avPin ? AV_FB_PIN : AV_FB_TRIP; break;
    default: i = t_avJobEnd ? AV_FB_JOBEND : AV_FB_STATE; break;
    }
    InterlockedIncrement(&ts3native_counters.avFb[i]);
}

// A-site: the retargeted call at 0x846D1C (thiscall world, 5 args, ret 0x14, as 0x82EDB0)
static u8 __fastcall AvSite(u8* world, void* edx, const void* pt, float r, void* vec, DWORD mode, DWORD opt) {
    if (t_avMiss) {
        if (t_avMiss == 1) InterlockedIncrement(&ts3native_counters.avSiteSkip);
        t_avMiss = 0;
        return 0;  // the caller's vector is still empty (made on its stack just before)
    }
    t_avSite = true;  // probe P0 keys this wait 0x846D21, as without the retarget (docs/137 §12)
    const u8 hit = g_srQuery(world, edx, pt, r, vec, mode, opt);
    t_avSite = false;
    return hit;
}
// [RouteAsync] RefcountGuard (rcguard.cpp): RouteFollow_A's frame pointer at the A-site (ebp frame, never rewritten in
// 0x846B70 [verified]), the consumer frame whose return ends a deferred +0x918 release; AvSite's own ebp is not one.
// shortcut: one global, not TLS: RouteFollow_A runs on the sim thread only (rcguard.cpp reads it there only)
static DWORD* volatile g_avRfEbp;
static __declspec(naked) void AvSiteEntry() {
    __asm {
        mov g_avRfEbp, ebp
        jmp AvSite
    }
}
static u64 AvBodyNoQuery(u8* path, u8* agent, const float* goal, const void* ign, DWORD n, LONG how) {
    t_avMiss = how;
    const u64 r = g_avAOrig(path, agent, goal, ign, n);
    t_avMiss = 0;  // a body that did not reach the site (no routing world) leaves it set
    return r;
}

static volatile LONG g_avNoted;
static void AvNote(const char* what, DWORD a, DWORD b, DWORD c, DWORD d) {
    if (InterlockedIncrement(&g_avNoted) <= 16) Log("avoid RO: %s (%08lX:%08lX vs %08lX:%08lX)", what, a, b, c, d);
}

// Shadow, query level: 0x82EDB0 itself (mode 1, radius 0) into our own vector vs the clone's footprints
static void AvShadowQuery(const AvIn& in) {
    if (!in.qlot) return;
    DWORD vec[4] = {0, 0, 0, kSrVecTag};
    g_srQuery(in.world, NULL, &in.pt, 0.0f, vec, 1, in.opt);
    const u8* const* b = (const u8* const*)(uintptr_t)vec[0];
    const int n = (int)((vec[1] - vec[0]) / 4);
    bool same = n == g_avCtx.n, set = same;
    for (int i = 0; same && i < n; i++) same = b[i] == g_avCtx.objs[i] + 4;
    for (int i = 0; !same && set && i < n; i++) {
        bool in2 = false;
        for (int j = 0; !in2 && j < n; j++) in2 = b[i] == g_avCtx.objs[j] + 4;
        set = in2;
    }
    if (!same) {
        InterlockedIncrement(set ? &ts3native_counters.avShadowOrder : &ts3native_counters.avShadowQueryMismatch);
        AvNote(set ? "shadow: query order" : "shadow: query differs", n, 0, g_avCtx.n, 0);
    }
    g_srVecRel(vec, NULL, (void*)(uintptr_t)vec[0], (void*)(uintptr_t)vec[1]);
    if (vec[0]) g_srFree((void*)(uintptr_t)vec[0]);
}

// Shadow A (no job, job starts held off): the game's call, the clone, and when the clone found no footprint the game's own
// body once more with the site skipped (its Sim scan); the answers must be equal
static u64 AvShadowA(u8* path, u8* agent, const float* goal, const void* ign, DWORD n) {
    Ts3nCounters& k = ts3native_counters;
    const u64 r = g_avAOrig(path, agent, goal, ign, n);
    static AvIn in;  // owner thread only
    u64 c;
    bool none;
    const int why = AvRunA(path, agent, goal, ign, n, false, &c, &in, &none);
    if (none) return r;
    InterlockedIncrement(&k.avShadowRuns);
    if (why) { AvFallback(why); return r; }
    const u64 want = c ? c : AvBodyNoQuery(path, agent, goal, ign, n, 2);
    if (r != want) {
        InterlockedIncrement(&k.avShadowMismatch);
        AvNote(c ? "shadow A: footprint id differs" : "shadow A: game found a footprint, clone none", (DWORD)(r >> 32), (DWORD)r,
               (DWORD)(want >> 32), (DWORD)want);
    } else if (!c && r) {
        InterlockedIncrement(&k.avShadowSimScan);
    }
    AvShadowQuery(in);
    return r;
}

static u8 AvShadowB(u8* wm, u8* world, u8* pB, const float* raw, DWORD opt) {
    Ts3nCounters& k = ts3native_counters;
    const u8 r = g_avBOrig(wm, world, pB, raw, opt);
    static AvIn in;
    u8 c;
    const int why = AvRunB(wm, world, pB, raw, opt, false, &c, &in);
    InterlockedIncrement(&k.avShadowRuns);
    if (why) { AvFallback(why); return r; }
    if ((r != 0) != (c != 0)) {
        InterlockedIncrement(&k.avShadowMismatchB);
        AvNote("shadow B: answer differs", 0, r, 0, c);
    }
    AvShadowQuery(in);
    return r;
}

// a job runs on another thread and this one may read without it: owner, no +0x918, no job callback, no gate frame
static bool AvMayRun(DWORD tid) {
    const RgThread& t = t_rg;
    return !g_inInvoke && ts3native_counters.rgOwnerTid != (LONG)tid && t.n == t.readers && !g_steerBroken;
}

static u64 __cdecl AvAHook(u8* path, u8* agent, const float* goal, const void* ign, DWORD n) {
    Ts3nCounters& k = ts3native_counters;
    const DWORD tid = GetCurrentThreadId();
    if ((!k.avMode && !k.avShadow) || tid != g_async.owner) return g_avAOrig(path, agent, goal, ign, n);
    if (g_async.state != A_RUNNING) {
        if (!k.avShadow) return g_avAOrig(path, agent, goal, ign, n);
        InterlockedIncrement(&g_rgOffSimOpen);  // no job starts while the two answers are compared (worker Dekker order)
        const u64 r = g_async.state == A_RUNNING ? g_avAOrig(path, agent, goal, ign, n) : AvShadowA(path, agent, goal, ign, n);
        RgOffSimDrop();
        return r;
    }
    if (!k.avMode) return g_avAOrig(path, agent, goal, ign, n);
    int why = SR_FB_STATE;
    t_avPin = t_avJobEnd = false;
    if (AvMayRun(tid)) {
        LARGE_INTEGER t0;
        QueryPerformanceCounter(&t0);
        static AvIn in;  // owner thread only
        u64 id;
        bool none;
        why = AvRunA(path, agent, goal, ign, n, true, &id, &in, &none);
        if (none) return g_avAOrig(path, agent, goal, ign, n);  // no routing world: no lock in the original either
        if (!why) {
            const LONG us = UsSince(t0);
            InterlockedExchangeAdd(&k.avUs, us);
            Max(&k.avMaxUs, us);
            MwSkip(MW_AV);
            if (id) { InterlockedIncrement(&k.avTakenHit); return id; }
            InterlockedIncrement(&k.avTakenMiss);
            return AvBodyNoQuery(path, agent, goal, ign, n, 1);
        }
    }
    AvFallback(why);
    return g_avAOrig(path, agent, goal, ign, n);
}

static u8 __cdecl AvBHook(u8* wm, u8* world, u8* pB, const float* raw, DWORD opt) {
    Ts3nCounters& k = ts3native_counters;
    const DWORD tid = GetCurrentThreadId();
    if ((!k.avMode && !k.avShadow) || tid != g_async.owner) return g_avBOrig(wm, world, pB, raw, opt);
    if (g_async.state != A_RUNNING) {
        if (!k.avShadow) return g_avBOrig(wm, world, pB, raw, opt);
        InterlockedIncrement(&g_rgOffSimOpen);
        const u8 r = g_async.state == A_RUNNING ? g_avBOrig(wm, world, pB, raw, opt) : AvShadowB(wm, world, pB, raw, opt);
        RgOffSimDrop();
        return r;
    }
    if (!k.avMode) return g_avBOrig(wm, world, pB, raw, opt);
    int why = SR_FB_STATE;
    t_avPin = t_avJobEnd = false;
    if (AvMayRun(tid)) {
        LARGE_INTEGER t0;
        QueryPerformanceCounter(&t0);
        static AvIn in;
        u8 res;
        why = AvRunB(wm, world, pB, raw, opt, true, &res, &in);
        if (!why) {
            const LONG us = UsSince(t0);
            InterlockedExchangeAdd(&k.avUs, us);
            Max(&k.avMaxUs, us);
            InterlockedIncrement(&k.avTakenB);
            MwSkip(MW_AV);
            return res;
        }
    }
    AvFallback(why);
    return g_avBOrig(wm, world, pB, raw, opt);
}

// [AvoidRO] Enabled=1: every byte checked first (prologues, the E8 at 0x846D1C to 0x82EDB0); one differs and nothing is
// hooked. Order: T-AV wrappers (both or none), the A-site (harmless while t_avMiss is never set), then the two detours.
static void InstallAvoidRO(const wchar_t* ini) {
    Ts3nCounters& k = ts3native_counters;
    if (!GetPrivateProfileIntW(L"AvoidRO", L"Enabled", 0, ini)) { Log("avoid RO off (Enabled=0)"); return; }
    if (!k.rgEnforce || !k.asyncReady) { Log("SKIP avoid RO: needs [RouteGate] Enforce=1 with the async path"); return; }
    const u8* site = (const u8*)(uintptr_t)AV_SITE;
    if (!Matches((const void*)(uintptr_t)AV_A, kAvPro, sizeof(kAvPro)) || !Matches((const void*)(uintptr_t)AV_B, kAvPro, sizeof(kAvPro)) ||
        !Matches((const void*)(uintptr_t)TS3A(0x0085FF30), kAvPro, sizeof(kAvPro)) ||
        !Matches((const void*)(uintptr_t)TS3A(0x0081D090), kAvAssignPro, sizeof(kAvAssignPro)) || !Readable(site, 5) || site[0] != 0xE8 ||
        AV_SITE + 5 + *(const DWORD*)(site + 1) != AV_QUERY) {
        Log("SKIP avoid RO: a prologue (0x846B70, 0x85B470, 0x85FF30, 0x81D090) or the call at 0x846D1C differs");
        return;
    }
    if (!InstallSrWriters()) { Log("SKIP avoid RO: the S2 writer set (W1-W6, W4, tripwires) is not all in place"); return; }
    g_avTwTramp[0] = InstallDetour("RoutingOptions_Assign (T-AV)", TS3A(0x0081D090), kAvAssignPro, sizeof(kAvAssignPro), (void*)AvTwAssign, &g_avTwTramp[0]);
    if (!g_avTwTramp[0]) return;
    g_avTwTramp[1] = InstallDetour("RouteFollow_EditPathForRoute (T-AV)", TS3A(0x0085FF30), kAvPro, sizeof(kAvPro), (void*)AvTwEdit, &g_avTwTramp[1]);
    if (!g_avTwTramp[1]) return;  // the first wrapper stays: it only counts
    k.avReady = 8;
    const CallSite cs = {AV_SITE, AV_QUERY, (void*)AvSiteEntry};
    if (!PatchCalls("avoid RO A-site", &cs, 1)) return;
    k.avReady |= 2;
    if (!(g_avAOrig = (AvAFn)InstallDetour("RouteFollow_A", AV_A, kAvPro, sizeof(kAvPro), (void*)AvAHook, &g_avAOrig))) return;
    k.avReady |= 1;
    if ((g_avBOrig = (AvBFn)InstallDetour("RouteFollow_B", AV_B, kAvPro, sizeof(kAvPro), (void*)AvBHook, &g_avBOrig)) != NULL) k.avReady |= 4;
    k.avMode = Clamp(GetPrivateProfileIntW(L"AvoidRO", L"Mode", 1, ini), 0, 1);
    k.avShadow = Clamp(GetPrivateProfileIntW(L"AvoidRO", L"Shadow", 0, ini), 0, 1);
    Log("OK   avoid RO: 0x846B70 + site 0x846D1C + 0x85B470 + 2 T-AV wrappers + the S2 writer set (ready %ld, Mode=%ld Shadow=%ld)",
        k.avReady, k.avMode, k.avShadow);
}
