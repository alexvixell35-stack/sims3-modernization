// [RouteToObjRO] (docs/140, built docs/140 §12): RoutingWorld_RouteToObjectCheck 0x831530 answered read-only while an async
// route job holds +0x918. Included by ts3native.cpp after tailunlock.cpp (uses steerro.cpp's QL, writer set, walks, lot table
// hit and transform; avoidro.cpp's AvMayRun and 0x81C970 pointer; slotro.cpp's SlFn; p5b.cpp's P5bOwner; PatchCalls).
//
// 0x831530 (thiscall world, 9 args, ret 0x24) takes +0x918 at 0x831568 (key 0x83156D), reads sim-owned option words and the
// object record, calls the route part 0x830340 at 0x831633, two physics checks, and releases at 0x8316FF. Only the sim ICs
// IsSlotUnobstructed and IsRouteToObjectUnobstructed wait there (docs/140 §2). Three call-site retargets, no prologue detour:
//   R-acq 0x831568 (naked): owner thread, a job running, not the +0x918 owner, no job callback, no gate frame, rtoMode: push
//     SKIP and return without the lock (0x831530 ignores eax). Otherwise jump to the game's Lock_Acquire (return address and
//     key 0x83156D kept). Shadow with no job: hold job starts off (g_rgOffSimOpen), then the same jump.
//   R-call 0x831633: SKIP: the clone (below); an answer writes out[0] (code) and out[2..3] (id) as 0x830340 would and marks
//     ANSWERED; a doubt takes +0x918 now through the game's Lock_Acquire (keyed 0x83156D, t_rtoKey) and runs 0x830340.
//     Shadow: the clone, then the game's 0x830340 under the lock R-acq took, compared.
//   R-rel 0x8316FF: SKIP (no route part: 0x40000 set and 0x8000 clear, or the object bit) and ANSWERED release nothing.
// Clone of 0x830340 [verified disassembly 2026-10-22, docs/140 §12.1]:
//   phase 1, no lock (sim-owned, the game's calls): planner P = world+0x95C, lot index dirty test, the point's lot (wm
//     world+0x964), the target's transform component (0x935770 key [0x11E9158]), position 0x959000, slot (0xB02360, component
//     [0x11E9114], 0x951420(0x31229A4C); missing = code 8 later) with the slot matrix product and 0x76F0A0 through SH's
//     detour, the object record 0xC8EC50 (0xB01770), option tests 0x81C880, the option copies 0x81D3C0 (+ 0x81C810 bit 4)
//     BlockerCheck2d hands its collectors, the ignore ids (caller's, target, 0x80000: transform +0x30's).
//   phase R, QL shared, try only, one hold (avoidro.cpp's protocol): the obstacle query at the point (S2 point walk, mode 1,
//     radius [0xFEC6DC]; type 2/4/0xC/0xD/0x11/0x15/0x17 not the target = code 4), the target footprint (0x631C20 / 0x6649C0
//     map walks, no refcount), the point's lot coordinates (0x62F000 copy), +0xB8 inside = code 0, the near-point goal
//     (+0xC4, +0xBC, 0x62EB40 copy) and kind 9's shifted goal (the x87 distance as the game computes it), then BlockerCheck2d
//     0x82E340 once or twice: S2 segment walk with its record (options object = the copy), the per-footprint rules in set
//     (pointer) order, merge "raise to max" (0x829250).
//   Any doubt gives up: QL busy, dirty lot index, lot miss, deferred quadtree flush, pin moved, invalid or changed polygon
//   cache, unknown slot, tripwire, BlockerCheck2d's physics tail (option 4, no hit), option 0x100000 (slot children), SH off.
// Counters v93 (+0x3FF0, after v95): rtoMode (+0x3FF4, --ab routetoobjro), rtoShadow (+0x3FF8, --ab routetoobjroshadow).

static const DWORD RTO_ACQ_SITE = TS3A(0x00831568), RTO_CALL_SITE = TS3A(0x00831633), RTO_REL_SITE = TS3A(0x008316FF);
static const DWORD RTO_LOCK_ACQ = TS3A(0x004E16F0), RTO_ROUTE = TS3A(0x00830340), RTO_LOCK_REL = TS3A(0x004E17B0);
typedef DWORD*(__fastcall* RtoRouteFn)(u8* world, void* edx, DWORD* out, u8* sim, u8* target, const float* point, u8* opts,
                                      DWORD a6, DWORD a7, DWORD a8, DWORD a9, DWORD a10);  // 0x830340, ret 0x28
static LockAcquireFn g_rtoAcq = (LockAcquireFn)(uintptr_t)RTO_LOCK_ACQ;  // through the address: LockAcquireHook runs
static LockReleaseFn g_rtoRel = (LockReleaseFn)(uintptr_t)RTO_LOCK_REL;
static RtoRouteFn g_rtoRoute = (RtoRouteFn)(uintptr_t)RTO_ROUTE;
static const DWORD* g_rtoDeadline = (const DWORD*)(uintptr_t)TS3A(0x01021CF0);  // 0x831530's Lock_Acquire argument

// game helpers (sim-owned reads; selftest points them at fakes)
typedef u8*(__fastcall* RtoCompFn)(const u8* obj, void* edx, DWORD key);      // 0x935770 / 0x951420, ret 4
typedef void(__fastcall* RtoMatFn)(const u8* o, void* edx, float* out);       // 0x959000, 0x959060, slot vtable +0x14, ret 4
typedef u8(__cdecl* RtoHasSlotFn)(const u8* obj);                              // 0xB02360
typedef u8*(__fastcall* RtoObjRecFn)(const u8* tab, void* edx, const u8* key); // 0xC8EC50, ret 4
typedef void(__fastcall* RtoOptCopyFn)(u8* dst, void* edx, const u8* src);     // 0x81D3C0, ret 4
typedef void(__fastcall* RtoOptSetFn)(u8* dst, void* edx, DWORD a, DWORD b, DWORD c, DWORD d);  // 0x81C810, ret 0x10
typedef void(__fastcall* RtoOptFreeFn)(u8* dst, void* edx);                    // 0x81CBE0
typedef u8(__fastcall* RtoListFn)(const u8* list, void* edx, DWORD lo, DWORD hi);  // 0xB69840, ret 8
typedef u8(__fastcall* RtoPortalFn)(const u8* portal, void* edx, const u8* rec);   // 0x946E90, ret 4
typedef int (*RtoQueryFn)(SrCtx* c, const u8* p, DWORD lo, DWORD hi, const SrPt& pt, float r, const u8* rec);
typedef int (*RtoGatherFn)(SrCtx* c, const u8* p, DWORD lo, DWORD hi, LONG level, const u8* rec, float u0, float v0, float u1,
                           float v1, float r);
static RtoCompFn g_rtoComp = (RtoCompFn)(uintptr_t)TS3A(0x00935770);
static RtoCompFn g_rtoSlotGet = (RtoCompFn)(uintptr_t)TS3A(0x00951420);
static RtoMatFn g_rtoPos = (RtoMatFn)(uintptr_t)TS3A(0x00959000);
static RtoMatFn g_rtoMat = (RtoMatFn)(uintptr_t)TS3A(0x00959060);
static RtoHasSlotFn g_rtoHasSlot = (RtoHasSlotFn)(uintptr_t)TS3A(0x00B02360);
static SlFn g_rtoSlPos = (SlFn)(uintptr_t)TS3A(0x0076F0A0);  // through the address: SH's detour decides the hold
static RtoObjRecFn g_rtoObjRec = (RtoObjRecFn)(uintptr_t)TS3A(0x00C8EC50);
static RtoOptCopyFn g_rtoOptCopy = (RtoOptCopyFn)(uintptr_t)TS3A(0x0081D3C0);
static RtoOptSetFn g_rtoOptSet = (RtoOptSetFn)(uintptr_t)TS3A(0x0081C810);
static RtoOptFreeFn g_rtoOptFree = (RtoOptFreeFn)(uintptr_t)TS3A(0x0081CBE0);
static RtoListFn g_rtoSimList = (RtoListFn)(uintptr_t)TS3A(0x00B69840);
static RtoPortalFn g_rtoPortal = (RtoPortalFn)(uintptr_t)TS3A(0x00946E90);
static const DWORD* g_rtoKeyTc = (const DWORD*)(uintptr_t)TS3A(0x011E9158);    // transform component key
static const DWORD* g_rtoKeySlot = (const DWORD*)(uintptr_t)TS3A(0x011E9114);  // slot component key
static u8* const* g_rtoObjTab = (u8* const*)(uintptr_t)TS3A(0x011ED034);       // object record table (0xB01770, 0xB02360)
static const float* g_rtoRadius = (const float*)(uintptr_t)TS3A(0x00FEC6DC);   // obstacle query radius (0.123)
static const float* g_rtoSegR = (const float*)(uintptr_t)TS3A(0x0106E8A8);     // BlockerCheck2d segment gather radius
static const float* g_rtoFar = (const float*)(uintptr_t)TS3A(0x00FECE14);      // kind 9: shift the goal above this distance (0.9)
static const float* g_rtoHalf = (const float*)(uintptr_t)TS3A(0x00F9A5AC);     // kind 9: shift length (0.5)

static int RtoQueryGame(SrCtx* c, const u8* p, DWORD lo, DWORD hi, const SrPt& pt, float r, const u8* rec) {
    return SrQuery(c, p, lo, hi, pt, true, r, rec, true);  // 0x82EDB0 mode 1 (visitor flag 1)
}
// 0x631E20 for BlockerCheck2d: Contains on the Sim's lot (key 0, absent, the active lot: empty), the start level's cell graph,
// segment walk 0x667400 with flag 1 and the record (options object: SrCollect flag bit 2); set order = pointer order
// (0x628340 compares the footprint pointer unsigned [verified 0x628358])
static int RtoGatherGame(SrCtx* c, const u8* p, DWORD lo, DWORD hi, LONG level, const u8* rec, float u0, float v0, float u1,
                         float v1, float r) {
    c->n = 0, c->fail = SR_OK, c->over = 0, c->rec = rec, c->flag = 3;
    if (!(lo | hi)) return SR_OK;
    const u8* entry;
    int why = SrEntry(p, lo, hi, true, &entry);
    if (why || !entry) return why;
    const u8* q = SrCell(entry, level);
    if (!q) return SR_OK;
    if ((why = SrWalk2(c, q, false, u0, v0, u1, v1, r)) != SR_OK) return why;
    qsort(c->objs, c->n, sizeof(c->objs[0]), SrCmpPtr);
    return SR_OK;
}
static RtoQueryFn g_rtoQuery = RtoQueryGame;
static RtoGatherFn g_rtoGather = RtoGatherGame;

enum { RTO_FB_STATE, RTO_FB_TRY, RTO_FB_DIRTY, RTO_FB_LOTMISS, RTO_FB_DEFERRED, RTO_FB_CACHE, RTO_FB_SHAPE, RTO_FB_TRIP,
       RTO_FB_TAIL, RTO_FB_OPT, RTO_FB_SH, RTO_FB_JOBEND };
static const int RTO_WHY_TAIL = 100, RTO_WHY_OPT = 101, RTO_WHY_SH = 102;  // above the SR_* codes
static __declspec(thread) bool t_rtoPin, t_rtoJobEnd;

// --- R-acq / R-call / R-rel state per nesting level (0x831530 never nests in practice: deeper levels take the game path)
// RTO_HELD / RTO_UFC (docs/202 §3.4 C3, unseenfbc.cpp): R-acq took +0x918 itself with a deadline and got it (R-call plain,
// R-rel releases) / the deadline passed (R-call answers "obstructed", R-rel releases nothing)
enum { RTO_PLAIN, RTO_SKIP, RTO_LATE, RTO_SHADOW, RTO_ANSWERED, RTO_HELD, RTO_UFC };
// docs/202 §3.4 C3 (unseenfbc.cpp UfcC3, set when [UnseenFallback] Sites C3 is installed): the take of +0x918 with a deadline
// when the running script task is an unseen Sim's own; 0 = not for this call (take it as the game does), 1 = taken (held),
// -2 = the deadline passed (nothing held)
static int (*g_ufcC3)(void* lock);
static __declspec(thread) u8 t_rtoSt[8];
static __declspec(thread) bool t_rtoWait[8];  // R-acq saw another thread own +0x918 (the original would have waited)
static __declspec(thread) int t_rtoN;

// Phase 1 output (pt and pos 16-byte aligned: SSE reads, and 0x76F0A0 gets pos as the game passes it)
#pragma warning(push)
#pragma warning(disable : 4324)  // padded for the alignment, as intended
struct RtoIn {
    u8* world;
    const u8* p;
    u8 *sim, *target, *opts;
    const float* point;
    DWORD a6, a7, a8, a9, a10;
    SrPt pt;
    const u8* qlot;
    const u8* tc;
    __declspec(align(16)) float pos[4];
    bool slotMissing;
    const u8* objrec;
    const u8* simList;
    bool t1, t2, t4, t800, tSim;
    DWORD ign[3][2];
    int nIgn;
    u8 *copyA, *copyB;  // option copies with bit 4 from (a7 || T(4)) and from T(4) (BlockerCheck2d calls 1 and 2)
};
#pragma warning(pop)
static __declspec(align(16)) u8 g_rtoCopy[2][0x100];  // owner thread only (0x82E340's frame holds the copy in < 0x80 bytes)
static SrCtx g_rtoQ, g_rtoG;                           // owner thread only: the last clone's query and gather sets
static bool g_rtoTailPend;                             // owner thread only: the first check left the physics tail to phase P
static float g_rtoTailGoal[2];                         // ... and its goal (x, z)

static void RtoMerge(DWORD* r, LONG code, DWORD lo, DWORD hi) {  // 0x829250: raise to the higher code, its id
    if (code > (LONG)r[0]) r[0] = code, r[2] = lo, r[3] = hi;
}

// 0x62F000 after the GetOrInsert hit: (x, y, z) of the point through rows +0xA0/+0xB0/+0xC0 plus +0xD0, the game's order
static void RtoLotPt(const u8* entry, const float* p, float* out) {
    const __m128 v = _mm_loadu_ps(p);
    const float* e = (const float*)(entry + 0xA0);
    __m128 r = _mm_add_ps(_mm_mul_ps(_mm_shuffle_ps(v, v, 0x00), _mm_load_ps(e)), _mm_load_ps(e + 12));
    r = _mm_add_ps(r, _mm_mul_ps(_mm_shuffle_ps(v, v, 0x55), _mm_load_ps(e + 4)));
    r = _mm_add_ps(r, _mm_mul_ps(_mm_shuffle_ps(v, v, 0xAA), _mm_load_ps(e + 8)));
    _mm_storeu_ps(out, r);
}
// 0x62EB40 after the hit: {u, 0, v, 1} through rows +0xE0..+0x110 (lot to world); io[0] = x', io[1] = z', io[2] kept
static void RtoWorldPt(const u8* entry, float* io) {
    const __m128 v = _mm_set_ps(*g_srOne, io[1], 0.0f, io[0]);
    const float* e = (const float*)(entry + 0xE0);
    __m128 r = _mm_add_ps(_mm_mul_ps(_mm_shuffle_ps(v, v, 0x55), _mm_load_ps(e + 4)), _mm_mul_ps(_mm_shuffle_ps(v, v, 0x00), _mm_load_ps(e)));
    r = _mm_add_ps(r, _mm_mul_ps(_mm_shuffle_ps(v, v, 0xAA), _mm_load_ps(e + 8)));
    r = _mm_add_ps(r, _mm_mul_ps(_mm_shuffle_ps(v, v, 0xFF), _mm_load_ps(e + 12)));
    __declspec(align(16)) float o[4];
    _mm_store_ps(o, r);
    io[0] = o[0], io[1] = o[2];
}
// kind 9 (0x83074F..0x830789): the distance on the x87 stack as the game computes it (the sim thread's control word), the
// rounded float kept for the shift (0x830779), the comparison on the unrounded value (fcomip, jbe = not far)
static bool RtoFar(float px, float pz, float gx, float gz, float* d) {
    const float thr = *g_rtoFar;
    float df;
    u8 far8;
    __asm {
        fld px
        fsub gx
        fld pz
        fsub gz
        fmul st(0), st(0)
        fld st(1)
        fmulp st(2), st(0)
        faddp st(1), st(0)
        fsqrt
        fst df
        fld thr
        fxch st(1)
        fcomip st(0), st(1)
        fstp st(0)
        seta far8
    }
    *d = df;
    return far8 != 0;
}

// Polygon-cache rule (S2's T3): level valid before, the entry unchanged after the game's slot; else a doubt
template <class F> static int RtoCached(const u8* fp, DWORD level, F call) {
    const u8* cache = *(const u8* const*)(fp + 0x38);
    if (!cache || !cache[level * 0x24 + 0x20]) return SR_FB_CACHE;  // the slot would rebuild it (0x80E5B0, W6a)
    u8 before[0x24];
    memcpy(before, cache + level * 0x24, sizeof(before));
    call();
    return memcmp(before, cache + level * 0x24, sizeof(before)) ? SR_FB_CACHE : SR_OK;
}

// 0x6643E0's equal range on (lo, hi) in a lot-routing hash map (+4 buckets, +8 count, node key +0/+4, next +0x10): the run
// of equal keys from the first match in the bucket chain (the range ends at the first other key or the next bucket head)
static const u8* RtoMapFirst(const u8* map, DWORD lo, DWORD hi, int* why) {
    const DWORD n = map ? *(const DWORD*)(map + 8) : 0;
    const DWORD* bk = map ? *(const DWORD* const*)(map + 4) : NULL;
    if (!n || !bk) { *why = SrShape(SRS_LOTTBL); return NULL; }  // the original divides by the count
    for (const u8* nd = (const u8*)(uintptr_t)bk[lo % n]; nd; nd = *(const u8* const*)(nd + 0x10))
        if (*(const DWORD*)nd == lo && *(const DWORD*)(nd + 4) == hi) return nd;
    return NULL;
}
static const u8* RtoMapNext(const u8* nd, DWORD lo, DWORD hi) {
    nd = *(const u8* const*)(nd + 0x10);
    return nd && *(const DWORD*)nd == lo && *(const DWORD*)(nd + 4) == hi ? nd : NULL;
}

// 0x631C20 (target footprint) without its refcounts: lot key 0 = the planner's world map P+0x24 (holder +8 -> object, id slot
// +0x7C, level slot +0x14 = 0x806FD0: EnsureAllPolygons 0x80E610 (a no-op when every level is valid) then byte +0x36);
// else Contains, level -4..12, the cell graph, 0x6649C0's three maps of [Q+0x20] (+0x3C: an entry for Q = none; +0x30:
// value +8 when +0xC == Q; +0x24: holder +8, its +8 when holder +0xC == Q)
static int RtoTargetFp(const RtoIn& in, const u8** out) {
    *out = NULL;
    const DWORD lo = *(const DWORD*)(in.sim + 0x48), hi = *(const DWORD*)(in.sim + 0x4C);
    const DWORD idLo = *(const DWORD*)(in.target + 8), idHi = *(const DWORD*)(in.target + 0xC);
    const LONG level = *(const LONG*)((const u8*)in.point + 0x24);
    int why = SR_OK;
    if (!(lo | hi)) {
        for (const u8* nd = RtoMapFirst(*(const u8* const*)(in.p + 0x24), idLo, idHi, &why); nd; nd = RtoMapNext(nd, idLo, idHi)) {
            const u8* h = *(const u8* const*)(nd + 8);
            const u8* obj = h ? *(const u8* const*)(h + 8) : NULL;
            if (!obj) continue;
            const DWORD* vt = *(const DWORD* const*)obj;
            if (g_srCheckSlots && (!SrSlotsOk(obj) || vt[0x14 / 4] != TS3A(0x00806FD0) || vt[0x70 / 4] != TS3A(0x0080E610) ||
                                   (vt[0] != TS3A(0x00619EF0) && vt[0] != TS3A(0x0083F5F0))))
                return SrShape(SRS_SLOT);
            const unsigned __int64 oid = ((SlotIdFn)(uintptr_t)vt[0x7C / 4])(obj, NULL);
            if (oid != ((unsigned __int64)idHi << 32 | idLo)) continue;
            if (const u8* cache = *(const u8* const*)(obj + 0x38)) {  // 0x80E610 would rebuild an invalid level
                const DWORD levels = ((AvCountFn)(uintptr_t)vt[0])(obj, NULL);
                for (DWORD l = 0; l < levels; l++) if (!cache[l * 0x24 + 0x20]) return SR_FB_CACHE;
            }
            if ((LONG)(signed char)obj[0x36] == level) { *out = obj; return SR_OK; }
        }
        return why;
    }
    const u8* entry;
    if ((why = SrEntry(in.p, lo, hi, true, &entry)) != SR_OK || !entry) return why;
    if (level < -4 || level > 12) return SR_OK;
    const u8* q = SrCell(entry, level);
    if (!q) return SR_OK;
    const u8* root = *(const u8* const*)(q + 0x20);
    for (const u8* nd = RtoMapFirst(*(const u8* const*)(root + 0x3C), idLo, idHi, &why); nd; nd = RtoMapNext(nd, idLo, idHi))
        if (*(const u8* const*)(nd + 8) == q) return SR_OK;
    if (why) return why;
    for (const u8* nd = RtoMapFirst(*(const u8* const*)(root + 0x30), idLo, idHi, &why); nd; nd = RtoMapNext(nd, idLo, idHi))
        if (*(const u8* const*)(nd + 0xC) == q) { *out = *(const u8* const*)(nd + 8); return SR_OK; }
    if (why) return why;
    for (const u8* nd = RtoMapFirst(*(const u8* const*)(root + 0x24), idLo, idHi, &why); nd; nd = RtoMapNext(nd, idLo, idHi)) {
        const u8* h = *(const u8* const*)(nd + 8);
        if (h && *(const u8* const*)(h + 0xC) == q) { *out = *(const u8* const*)(h + 8); return SR_OK; }
    }
    return why;
}

typedef float(__fastcall* RtoF44Fn)(const u8* fp, void* edx);                                          // +0x44 0x806FC0
typedef u8(__fastcall* RtoSegFn)(const u8* fp, void* edx, DWORD lvl, float u0, float v0, float u1, float v1, float r);  // +0xD8
typedef const u8*(__fastcall* RtoGetFn)(const u8* fp, void* edx);                                      // +0x88 0xF20360
typedef u8(__fastcall* RtoLvlFn)(const u8* fp, void* edx, DWORD lvl);                                  // +0xC4 0x80E540
typedef float(__fastcall* RtoNearFn)(const u8* fp, void* edx, DWORD lvl, const float* pt, float* out);  // +0xBC 0x80E910

// BlockerCheck2d 0x82E340 from the point to goal (x, z) on the Sim's lot; b24..b34 its byte arguments 8-12 (docs/140 §12.1)
static int RtoBlock(const RtoIn& in, const u8* entry, DWORD* res, const float* goal, bool b28, bool b2c, bool b30, bool b34,
                    const u8* copy) {
    res[0] = res[2] = res[3] = 0;
    const bool o24 = in.t2, o28 = b28 || in.t1, o4 = b2c || in.t4;
    float u0, v0, u1, v1;
    SrXform(entry, in.point[0], in.point[2], &u0, &v0);  // 0x62EF30 x2 (0x82E691, 0x82E6C2)
    SrXform(entry, goal[0], goal[1], &u1, &v1);
    __declspec(align(16)) u8 rec[0x30] = {};  // 0x82E340's record at frame +0x150
    *(DWORD*)rec = in.a6;
    *(DWORD*)(rec + 8) = in.a8, *(DWORD*)(rec + 0xC) = in.a9;
    *(const u8**)(rec + 0x28) = copy;
    int why = g_rtoGather(&g_rtoG, in.p, *(const DWORD*)(in.sim + 0x48), *(const DWORD*)(in.sim + 0x4C),
                          *(const LONG*)((const u8*)in.point + 0x24), rec, u0, v0, u1, v1, *g_rtoSegR);
    if (why) return why;
    for (int i = 0; i < g_rtoG.n; i++) {
        const u8* fp = g_rtoG.objs[i];
        const DWORD* vt = *(const DWORD* const*)fp;
        const unsigned __int64 id = ((SlotIdFn)(uintptr_t)vt[0x84 / 4])(fp, NULL);
        const DWORD lo = (DWORD)id, hi = (DWORD)(id >> 32);
        bool ign = false;
        for (int j = 0; j < in.nIgn && !ign; j++) ign = in.ign[j][0] == lo && in.ign[j][1] == hi;
        if (ign || g_srOptId(in.opts, NULL, lo, hi)) continue;
        if (in.tSim && in.simList && g_rtoSimList(in.simList, NULL, lo, hi)) continue;
        if (!((SlotBoolFn)(uintptr_t)vt[0xC0 / 4])(fp, NULL)) continue;
        if (g_srCheckSlots && (vt[0x44 / 4] != TS3A(0x00806FC0) || vt[0xD8 / 4] != TS3A(0x0080F080) || vt[0x88 / 4] != TS3A(0x00F20360)))
            return SrShape(SRS_SLOT);
        const float r = ((RtoF44Fn)(uintptr_t)vt[0x44 / 4])(fp, NULL);
        u8 hit = 0;
        if ((why = RtoCached(fp, 0, [&]() { hit = ((RtoSegFn)(uintptr_t)vt[0xD8 / 4])(fp, NULL, 0, u0, v0, u1, v1, r); })) != SR_OK)
            return why;
        if (!hit) continue;
        const int t = ((SlotIntFn)(uintptr_t)vt[3])(fp, NULL);
        if (b34) {
            if (t == 0xC || t == 0x17) RtoMerge(res, 4, 0, 0);  // 0x8292B0
            continue;
        }
        if (t == 1) {  // portal: lock-key test with a record {+0x20 null handle, +0x24 = 1, +0x28 options} (ctor/dtor no-ops)
            const u8* portal = ((RtoGetFn)(uintptr_t)vt[0x88 / 4])(fp, NULL);
            u8 pass = 1;
            if (portal) {
                __declspec(align(16)) u8 s[0x30] = {};
                s[0x24] = 1;
                *(const u8**)(s + 0x28) = in.opts;
                pass = g_rtoPortal(portal, NULL, s);
            }
            if (!o24 && pass) RtoMerge(res, 3, lo, hi);
            continue;
        }
        if (t == 0x14) {
            if (o4) RtoMerge(res, 1, 0, 0);
            continue;
        }
        if (t == 0xC || t == 0x17) {
            if (!o28) RtoMerge(res, 4, 0, 0);
            continue;
        }
        if (t == 9) {
            const unsigned __int64 id9 = ((SlotIdFn)(uintptr_t)vt[0x7C / 4])(fp, NULL);
            RtoMerge(res, 6, (DWORD)id9, (DWORD)(id9 >> 32));
            continue;
        }
        if (!in.t800 && (t == 0x10 || t == 0x1D)) continue;
        if (b30 && (t == 0x13 || t == 0x18)) continue;
        RtoMerge(res, 7, 0, 0);
    }
    // the physics samples after a clean footprint pass (0x82EB49..0x82ED0D): with rtoTailMode, RtoTail runs them after QL is
    // released (phase P); else the game's call runs (rtoFb[8])
    if (!res[0] && !b34 && o4) {
        if (!ts3native_counters.rtoTailMode) return RTO_WHY_TAIL;
        g_rtoTailPend = true, g_rtoTailGoal[0] = goal[0], g_rtoTailGoal[1] = goal[1];
    }
    return SR_OK;
}

static void RtoFree(RtoIn& in) {
    if (in.copyA) g_rtoOptFree(in.copyA, NULL);
    if (in.copyB && in.copyB != in.copyA) g_rtoOptFree(in.copyB, NULL);
    in.copyA = in.copyB = NULL;
}

// Phase 1 (no lock). *done: the answer needs no routing read (no target: code 0 before the query)
static int RtoPhase1(bool job, RtoIn& in, bool* done) {
    Ts3nCounters& k = ts3native_counters;
    *done = false;
    in.copyA = in.copyB = NULL;
    in.qlot = in.tc = in.objrec = in.simList = NULL;
    in.slotMissing = false;
    in.nIgn = 0;
    in.p = in.world[0x974] ? *(const u8* const*)(in.world + 0x95C) : NULL;
    if (!in.target) { *done = true; return SR_OK; }  // 0x8303B4
    u8* wmw = *(u8* const*)(in.world + 0x964);
    if (!in.p || !wmw || !g_srWorldGet()) return SrShape(SRS_NOPLANNER);
    if (wmw[0x1CC]) return SR_FB_DIRTY;  // the lookup would rebuild the lot index
    memset(&in.pt, 0, sizeof(in.pt));
    memcpy(in.pt.v, in.point, 16);
    in.pt.one = *g_srOne;
    in.pt.level = *(const LONG*)((const u8*)in.point + 0x24);
    in.qlot = g_srLotLookup(wmw, NULL, in.pt.v[0], in.pt.v[2]);  // 0x82EDB0's lookup: none = no footprints, step 2 passes
    in.tc = g_rtoComp(in.target, NULL, *g_rtoKeyTc);
    if (!in.tc) return SR_OK;  // code 0 after the query
    g_rtoPos(in.tc, NULL, in.pos);
    if (g_rtoHasSlot(in.target)) {
        const u8* sc = g_rtoComp(in.target, NULL, *g_rtoKeySlot);
        const u8* slot = sc ? g_rtoSlotGet(sc, NULL, 0x31229A4C) : NULL;
        if (!slot) {
            in.slotMissing = true;  // code 8 after the footprint lookup
        } else {
            if (job && !(k.slReady && k.slMode)) return RTO_WHY_SH;  // 0x76F0A0 would wait at gate 12 / 0
            __declspec(align(16)) float m[16], s[16];
            g_rtoMat(in.tc, NULL, m);
            ((RtoMatFn)(*(void* const* const*)slot)[0x14 / 4])(slot, NULL, s);
            const __m128 l = _mm_load_ps(s + 12);  // 0x830587: slot translation through the target's matrix
            __m128 r = _mm_add_ps(_mm_mul_ps(_mm_shuffle_ps(l, l, 0x00), _mm_load_ps(m)), _mm_load_ps(m + 12));
            r = _mm_add_ps(r, _mm_mul_ps(_mm_shuffle_ps(l, l, 0x55), _mm_load_ps(m + 4)));
            r = _mm_add_ps(r, _mm_mul_ps(_mm_shuffle_ps(l, l, 0xAA), _mm_load_ps(m + 8)));
            _mm_store_ps(in.pos, r);
            g_rtoSlPos(in.pos, in.target, in.a6, 0);
            InterlockedIncrement(&k.rtoSlot);
        }
    }
    const u8* tab = *g_rtoObjTab;
    if (!tab || !in.opts) return SrShape(SRS_NOPLANNER);  // the original would fault
    in.objrec = g_rtoObjRec(tab, NULL, in.target + 0x18);
    in.t1 = g_srOptTest(in.opts, NULL, 1, 0, 0) != 0;
    in.t2 = g_srOptTest(in.opts, NULL, 2, 0, 0) != 0;
    in.t4 = g_srOptTest(in.opts, NULL, 4, 0, 0) != 0;
    in.tSim = g_srOptTest(in.opts, NULL, 0, 4, 0) != 0;
    in.t800 = g_srOptTest(in.opts, NULL, 0, 0x800, 0) != 0;
    if (g_srOptTest(in.opts, NULL, 0x100000, 0, 0)) return RTO_WHY_OPT;  // slot children ids (0x952E40 iterator): not cloned
    in.simList = *(const u8* const*)(in.sim + 0x2C0);
    if (in.a8 | in.a9) in.ign[in.nIgn][0] = in.a8, in.ign[in.nIgn][1] = in.a9, in.nIgn++;
    in.ign[in.nIgn][0] = *(const DWORD*)(in.target + 8), in.ign[in.nIgn][1] = *(const DWORD*)(in.target + 0xC), in.nIgn++;
    if (g_srOptTest(in.opts, NULL, 0x80000, 0, 0)) {
        const u8* o = *(const u8* const*)(in.tc + 0x30);
        if (o) in.ign[in.nIgn][0] = *(const DWORD*)(o + 8), in.ign[in.nIgn][1] = *(const DWORD*)(o + 0xC), in.nIgn++;
    }
    // BlockerCheck2d's option copies (0x81D3C0, bit 4 forced by 0x81C810(4, 0, 1, 0) when its option 4 holds)
    const bool o4a = (u8)in.a7 || in.t4;
    for (int i = 0; i < 2; i++) {
        const bool o4 = i ? in.t4 : o4a;
        if (i && o4 == o4a) { in.copyB = in.copyA; break; }
        u8* c = g_rtoCopy[i];
        g_rtoOptCopy(c, NULL, in.opts);
        if (o4) g_rtoOptSet(c, NULL, 4, 0, 1, 0);
        (i ? in.copyB : in.copyA) = c;
    }
    return SR_OK;
}

// Phase R, under QL shared: 0x830340 from its obstacle query on (docs/140 §3.1 steps 2-7)
static int RtoPhaseR(RtoIn& in, DWORD* res) {
    res[0] = res[2] = res[3] = 0;
    int why;
    if (in.qlot) {  // step 2
        __declspec(align(16)) u8 rec[0x30] = {};  // 0x82EDB0's record: option word, mode 1
        *(DWORD*)rec = in.a6;
        rec[0x24] = 1;
        if ((why = g_rtoQuery(&g_rtoQ, in.p, *(const DWORD*)(in.qlot + 0x48), *(const DWORD*)(in.qlot + 0x4C), in.pt, *g_rtoRadius,
                              rec)) != SR_OK)
            return why;
        const DWORD tLo = *(const DWORD*)(in.target + 8), tHi = *(const DWORD*)(in.target + 0xC);
        for (int i = 0; i < g_rtoQ.n; i++) {
            const u8* sub = g_rtoQ.objs[i] + 4;
            const void* const* svt = *(const void* const* const*)sub;
            const unsigned __int64 sid = ((SlotIdFn)svt[1])(sub, NULL);
            if ((DWORD)sid == tLo && (DWORD)(sid >> 32) == tHi) continue;
            const int t = ((SlotIntFn)svt[2])(sub, NULL);
            if (t == 2 || t == 4 || t == 0xC || t == 0xD || t == 0x11 || t == 0x15 || t == 0x17) { res[0] = 4; return SR_OK; }
        }
    }
    if (!in.tc) return SR_OK;  // step 3: no transform component, code 0
    const u8* fp;
    if ((why = RtoTargetFp(in, &fp)) != SR_OK) return why;
    if (in.slotMissing) { res[0] = 8; return SR_OK; }  // step 4
    const u8* entry;  // step 5: 0x62F000 GetOrInsert on the Sim's lot (a miss would insert)
    if ((why = SrEntry(in.p, *(const DWORD*)(in.sim + 0x48), *(const DWORD*)(in.sim + 0x4C), false, &entry)) != SR_OK) return why;
    __declspec(align(16)) float lotPt[4];
    RtoLotPt(entry, in.point, lotPt);
    const DWORD* vt = fp ? *(const DWORD* const*)fp : NULL;
    if (fp) {
        if (g_srCheckSlots && (!SrSlotsOk(fp) || (vt[0xB8 / 4] != AV_PT_POLY && vt[0xB8 / 4] != AV_PT_NONE) ||
                               (vt[3] != TS3A(0x00624450) && vt[3] != TS3A(0x0070AE20))))
            return SrShape(SRS_SLOT);
        u8 inside = 0;
        if (!g_srCheckSlots || vt[0xB8 / 4] == AV_PT_POLY) {
            if ((why = RtoCached(fp, 0, [&]() { inside = ((AvPtFn)(uintptr_t)vt[0xB8 / 4])(fp, NULL, 0, lotPt); })) != SR_OK) return why;
        } else {
            inside = ((AvPtFn)(uintptr_t)vt[0xB8 / 4])(fp, NULL, 0, lotPt);  // 0xBFB320 reads nothing
        }
        if (inside) return SR_OK;  // code 0: the point is inside the target
    }
    // step 6: goals at the target's position, or the nearest footprint point (kind 1 / 9), kind 9's shifted goal
    float goal[3], shifted[3];
    goal[0] = shifted[0] = in.pos[0], goal[1] = shifted[1] = in.pos[2];
    *(LONG*)&goal[2] = *(LONG*)&shifted[2] = in.pt.level;
    bool flag = false, flag2 = false;
    if (fp) {
        const int kind = ((SlotIntFn)(uintptr_t)vt[3])(fp, NULL);
        if (kind == 1 || kind == 9) {
            if (g_srCheckSlots && (vt[0xC4 / 4] != TS3A(0x0080E540) || vt[0xBC / 4] != TS3A(0x0080E910))) return SrShape(SRS_SLOT);
            if (!((RtoLvlFn)(uintptr_t)vt[0xC4 / 4])(fp, NULL, 0)) {
                __declspec(align(16)) float nearPt[4] = {};
                if ((why = RtoCached(fp, 0, [&]() { ((RtoNearFn)(uintptr_t)vt[0xBC / 4])(fp, NULL, 0, lotPt, nearPt); })) != SR_OK)
                    return why;
                goal[0] = nearPt[0], goal[1] = nearPt[1];
                RtoWorldPt(entry, goal);
                if (((SlotIntFn)(uintptr_t)vt[3])(fp, NULL) == 9) {
                    float d;
                    if (RtoFar(in.point[0], in.point[2], goal[0], goal[1], &d)) {  // 0x83078B
                        const __m128 t = _mm_div_ss(_mm_load_ss(g_rtoHalf), _mm_load_ss(&d));
                        const __m128 s = _mm_sub_ss(_mm_load_ss(g_srOne), t);
                        _mm_store_ss(&shifted[0], _mm_add_ss(_mm_mul_ss(s, _mm_load_ss(&goal[0])), _mm_mul_ss(_mm_load_ss(&in.point[0]), t)));
                        _mm_store_ss(&shifted[1], _mm_add_ss(_mm_mul_ss(s, _mm_load_ss(&goal[1])), _mm_mul_ss(_mm_load_ss(&in.point[2]), t)));
                        *(LONG*)&shifted[2] = *(const LONG*)&goal[2];
                        flag2 = true;
                    }
                    flag = true;
                } else {
                    flag = in.objrec && (*(const DWORD*)(in.objrec + 0xD0) & 0x10000);  // 0x8307F3: the object record's bit
                }
            }
        }
    }
    // step 7
    if ((why = RtoBlock(in, entry, res, goal, flag, (u8)in.a7 != 0, (u8)in.a10 != 0, false, in.copyA)) != SR_OK) return why;
    if (flag && flag2 && (LONG)res[0] < 4) {
        DWORD r2[4];
        if ((why = RtoBlock(in, entry, r2, shifted, false, false, true, true, in.copyB)) != SR_OK) return why;
        RtoMerge(res, r2[0], 0, 0);  // 0x830891: the second check's code with id 0
    }
    return SR_OK;
}

// Phase R under QL shared, avoidro.cpp's AvLocked protocol with this feature's counters
template <class F> static int RtoLocked(bool job, F body) {
    Ts3nCounters& k = ts3native_counters;
    t_rtoPin = t_rtoJobEnd = false;
    if (t_qlDepth || t_qlShared) return SR_FB_STATE;  // SRW locks are not recursive
    if (!TryAcquireSRWLockShared(&g_ql)) return SR_FB_TRY;
    if (g_steerBroken) { ReleaseSRWLockShared(&g_ql); return SR_FB_STATE; }
    const LONG seq = g_qlSeq;
    InterlockedExchange(&g_srInClone, 1);  // T4 / T-S7 writers on other threads count from here
    const LONG twEpoch = g_twEpoch, twGen = g_twGen;
    bool jobEnd = job && !g_twArmed;
    const LONG w0 = k.steerSimWriteInRo, pin0 = k.steerPinMoved;
    t_inRo = true;
    t_qlShared = 1;
    t_srFail = SR_OK;
    int why = jobEnd ? SR_FB_STATE : g_twActive ? SR_FB_TRIP : SR_OK;
    if (!why) why = body();
    if (!why && t_srFail) why = t_srFail;
    if (!why && (g_qlSeq != seq || g_twActive || g_twEpoch != twEpoch)) why = SR_FB_TRIP;
    if (!why && job && g_twGen != twGen) { jobEnd = true; why = SR_FB_STATE; }
    t_inRo = false;
    t_qlShared = 0;
    ReleaseSRWLockShared(&g_ql);
    InterlockedExchange(&g_srInClone, 0);
    if (k.steerSimWriteInRo != w0) InterlockedIncrement(&k.rtoSimWriteInRo);
    t_rtoPin = why == SR_FB_TRIP && k.steerPinMoved != pin0;
    t_rtoJobEnd = jobEnd;
    return why;
}

// Phase P (docs/140 §5.3, no lock): BlockerCheck2d's physics tail 0x82EB49..0x82ED0D of the first check [verified disassembly
// 2026-11-04]. The physics world 0x686A90 ([0x11CF490]) and its scene (+8, 0xC93970); step d = ((goal x, start y, goal z,
// goal x) - start) * 0.25 [0x106E8A8]; samples i = 0..4 at i * d + start (the game's SSE lanes and order); each a range query
// 0x682590 (scene, point, 0.15 [0x106B4B4], 0x10060, &vector "Physics/RangeResult") that returns 1; the first id that 0x82A780
// calls a blocker and that is neither the target's (+8 / +0xC) nor the ignore id (a8, a9) answers code 1 with that id; every
// vector freed (0x4E3F70); no sample after a hit. Run only when the merged answer is still code 0: the second check gives 0
// or 4 and 4 replaces the tail's 1 with id 0 (0x829250), so running the tail after the second check gives the game's answer.
// The physics calls need no +0x918 (docs/140 §3, store scan) and take no QL, so they run after QL shared is released.
typedef void*(__cdecl* RtoPhysFn)();
typedef u8(__fastcall* RtoRangeFn)(void* scene, void* edx, const float* pt, float r, DWORD flags, void* vec);  // ret 0x10
typedef u8(__cdecl* RtoBlockerFn)(DWORD lo, DWORD hi);
typedef void(__cdecl* RtoMemFreeFn)(void* p);
static RtoPhysFn g_rtoPhys = (RtoPhysFn)(uintptr_t)TS3A(0x00686A90);
static RtoRangeFn g_rtoRange = (RtoRangeFn)(uintptr_t)TS3A(0x00682590);
static RtoBlockerFn g_rtoBlocker = (RtoBlockerFn)(uintptr_t)TS3A(0x0082A780);
static RtoMemFreeFn g_rtoMemFree = (RtoMemFreeFn)(uintptr_t)TS3A(0x004E3F70);
static const char* const RTO_RANGE_NAME = (const char*)(uintptr_t)TS3A(0x01003D7C);  // "Physics/RangeResult", the vector's allocator name
struct RtoVec { DWORD *b, *e, *cap; const char* name; };
static void RtoTail(const RtoIn& in, DWORD* res) {
    Ts3nCounters& k = ts3native_counters;
    InterlockedIncrement(&k.rtoTailRuns);
    const u8* phys = (const u8*)g_rtoPhys();
    void* scene = phys ? *(void* const*)(phys + 8) : NULL;
    if (!scene) return;
    const __m128 s = _mm_load_ps(in.point);
    __declspec(align(16)) float g[4] = {g_rtoTailGoal[0], in.point[1], g_rtoTailGoal[1], g_rtoTailGoal[0]};
    const __m128 d = _mm_mul_ps(_mm_sub_ps(_mm_load_ps(g), s), _mm_set1_ps(0.25f));
    for (DWORD i = 0; i <= 4; i++) {
        __declspec(align(16)) float pt[4];
        _mm_store_ps(pt, _mm_add_ps(_mm_mul_ps(_mm_set1_ps((float)i), d), s));
        RtoVec v = {NULL, NULL, NULL, RTO_RANGE_NAME};
        bool hit = false;
        if (g_rtoRange(scene, NULL, pt, 0.15f, 0x10060, &v) == 1) {
            const DWORD n = (DWORD)(((const u8*)v.e - (const u8*)v.b) >> 3);
            for (DWORD j = 0; j < n && !hit; j++) {
                const DWORD lo = v.b[2 * j], hi = v.b[2 * j + 1];
                if (!g_rtoBlocker(lo, hi)) continue;
                if (in.target && lo == *(const DWORD*)(in.target + 8) && hi == *(const DWORD*)(in.target + 0xC)) continue;
                if (lo == in.a8 && hi == in.a9) continue;
                if ((LONG)res[0] < 1) res[0] = 1, res[2] = lo, res[3] = hi;
                hit = true;
            }
        }
        if (v.b) g_rtoMemFree(v.b);
        if (hit) { InterlockedIncrement(&k.rtoTailHits); return; }
    }
}

static int RtoRun(bool job, RtoIn& in, DWORD* res) {
    res[0] = res[2] = res[3] = 0;
    g_rtoTailPend = false;
    bool done;
    int why = RtoPhase1(job, in, &done);
    if (!why && !done) why = RtoLocked(job, [&]() { return RtoPhaseR(in, res); });
    if (!why && g_rtoTailPend && !res[0]) RtoTail(in, res);  // phase P, QL released
    RtoFree(in);
    return why;
}

static void RtoFallback(int why) {
    int i;
    switch (why) {
    case SR_FB_TRY: i = RTO_FB_TRY; break;
    case SR_FB_DIRTY: i = RTO_FB_DIRTY; break;
    case SR_FB_LOTMISS: i = RTO_FB_LOTMISS; break;
    case SR_FB_DEFERRED: i = RTO_FB_DEFERRED; break;
    case SR_FB_CACHE: i = RTO_FB_CACHE; break;
    case SR_FB_SHAPE: i = RTO_FB_SHAPE; break;
    case SR_FB_TRIP: i = t_rtoPin ? RTO_FB_DEFERRED : RTO_FB_TRIP; break;
    case RTO_WHY_TAIL: i = RTO_FB_TAIL; break;
    case RTO_WHY_OPT: i = RTO_FB_OPT; break;
    case RTO_WHY_SH: i = RTO_FB_SH; break;
    default: i = t_rtoJobEnd ? RTO_FB_JOBEND : RTO_FB_STATE; break;
    }
    InterlockedIncrement(&ts3native_counters.rtoFb[i]);
}

static volatile LONG g_rtoNoted;
static void RtoNote(const char* what, DWORD a, DWORD b, DWORD c, DWORD d, DWORD e, DWORD f) {
    if (InterlockedIncrement(&g_rtoNoted) <= 16) Log("route-to-object RO: %s (game %ld %08lX:%08lX, clone %ld %08lX:%08lX)", what, a, c, b, d, f, e);
}

// --- the three stubs ---
// R-acq's decision (cdecl, from the naked stub): push this level's state; 1 = skip the lock
static int __cdecl RtoAcqDecide(void* lock) {
    Ts3nCounters& k = ts3native_counters;
    int st = RTO_PLAIN;
    bool wait = false;
    const DWORD tid = GetCurrentThreadId();
    if (lock == g_rgLock && tid == g_async.owner && (k.rtoMode || k.rtoShadow) && t_rtoN < 8) {
        if (t_rtoN && t_rtoSt[t_rtoN - 1] != RTO_PLAIN) {
            InterlockedIncrement(&k.rtoNested);
        } else if (g_async.state == A_RUNNING) {
            if (k.rtoMode && AvMayRun(tid)) {
                st = RTO_SKIP;
                const LONG owner = P5bOwner(lock);
                wait = owner && owner != (LONG)tid;
                InterlockedIncrement(&k.rtoCalls);
            }
        } else if (k.rtoShadow) {
            InterlockedIncrement(&g_rgOffSimOpen);  // no job starts while the two answers are compared (worker Dekker order)
            if (g_async.state == A_RUNNING) RgOffSimDrop();
            else st = RTO_SHADOW;
        }
    } else if (t_rtoN >= 8) {
        InterlockedIncrement(&k.rtoNested);
    }
    // docs/202 C3: a take the RO path leaves to the game (outermost level only)
    if (st == RTO_PLAIN && g_ufcC3 && t_rtoN == 0) {
        const int r = g_ufcC3(lock);
        if (r) st = r > 0 ? RTO_HELD : RTO_UFC;
    }
    if (t_rtoN < 8) t_rtoSt[t_rtoN] = (u8)st, t_rtoWait[t_rtoN] = wait;
    t_rtoN++;
    return st == RTO_SKIP || st == RTO_HELD || st == RTO_UFC;
}
static __declspec(naked) void RtoAcqStub() {  // thiscall(lock; arg), ret 4, as 0x4E16F0
    __asm {
        push ecx
        push ecx
        call RtoAcqDecide
        add esp, 4
        pop ecx
        test eax, eax
        jnz skip
        jmp dword ptr [g_rtoAcq]  // the game's Lock_Acquire with the original return address (P0 key 0x83156D)
    skip:
        ret 4
    }
}

static DWORD* __fastcall RtoCallStub(u8* world, void* edx, DWORD* out, u8* sim, u8* target, const float* point, u8* opts, DWORD a6,
                                     DWORD a7, DWORD a8, DWORD a9, DWORD a10) {
    Ts3nCounters& k = ts3native_counters;
    const int lvl = t_rtoN - 1;
    const int st = lvl >= 0 && lvl < 8 ? t_rtoSt[lvl] : RTO_PLAIN;
    if (st == RTO_UFC) {  // docs/202 C3: the deadline passed at R-acq: "obstructed" (code 4, id 0: the early code-4 answer)
        out[0] = 4, out[2] = out[3] = 0;
        t_rtoSt[lvl] = RTO_ANSWERED;
        InterlockedIncrement(&k.ufcFake[2]);
        return out;
    }
    if (st != RTO_SKIP && st != RTO_SHADOW) return g_rtoRoute(world, edx, out, sim, target, point, opts, a6, a7, a8, a9, a10);
    // no Sim: 0x830340 answers code 0 at 0x830352 and reads nothing [verified disassembly]; the clone would read sim + 0x2C0
    // (AV in a 2026-11-04 soak). Shadow: the lock is held, the game answers.
    if (!sim) {
        if (st == RTO_SHADOW) return g_rtoRoute(world, edx, out, sim, target, point, opts, a6, a7, a8, a9, a10);
        out[0] = out[2] = out[3] = 0;
        t_rtoSt[lvl] = RTO_ANSWERED;
        return out;
    }
    static RtoIn in;  // owner thread only
    in.world = world, in.sim = sim, in.target = target, in.point = point, in.opts = opts;
    in.a6 = a6, in.a7 = a7, in.a8 = a8, in.a9 = a9, in.a10 = a10;
    DWORD c[4];
    if (st == RTO_SHADOW) {
        const int why = RtoRun(false, in, c);
        DWORD* r = g_rtoRoute(world, edx, out, sim, target, point, opts, a6, a7, a8, a9, a10);
        InterlockedIncrement(&k.rtoShadowRuns);
        if (why) {
            InterlockedIncrement(&k.rtoShadowFb);
            RtoFallback(why);
        } else if (out[0] != c[0] || out[2] != c[2] || out[3] != c[3]) {
            InterlockedIncrement(&k.rtoShadowMismatch);
            RtoNote("shadow: answer differs", out[0], out[2], out[3], c[0], c[2], c[3]);
        }
        if (!why && in.qlot) {  // step 2's set against 0x82EDB0 (mode 1, the same radius and option word)
            DWORD vec[4] = {0, 0, 0, kSrVecTag};
            g_srQuery(world, NULL, &in.pt, *g_rtoRadius, vec, 1, a6);
            const u8* const* b = (const u8* const*)(uintptr_t)vec[0];
            const int n = (int)((vec[1] - vec[0]) / 4);
            bool same = n == g_rtoQ.n, set = same;
            for (int i = 0; same && i < n; i++) same = b[i] == g_rtoQ.objs[i] + 4;
            for (int i = 0; !same && set && i < n; i++) {
                bool in2 = false;
                for (int j = 0; !in2 && j < n; j++) in2 = b[i] == g_rtoQ.objs[j] + 4;
                set = in2;
            }
            if (!same) {
                InterlockedIncrement(set ? &k.rtoShadowOrder : &k.rtoShadowQueryMismatch);
                RtoNote(set ? "shadow: query order" : "shadow: query differs", n, 0, 0, g_rtoQ.n, 0, 0);
            }
            g_srVecRel(vec, NULL, (void*)(uintptr_t)vec[0], (void*)(uintptr_t)vec[1]);
            if (vec[0]) g_srFree((void*)(uintptr_t)vec[0]);
        }
        return r;
    }
    LARGE_INTEGER t0;
    QueryPerformanceCounter(&t0);
    const int why = RtoRun(true, in, c);
    if (!why) {
        const LONG us = UsSince(t0);
        InterlockedExchangeAdd(&k.rtoUs, us);
        Max(&k.rtoMaxUs, us);
        InterlockedIncrement(&k.rtoTaken);
        MwSkip(MW_RTO);
        if (c[0] < 9) InterlockedIncrement(&k.rtoCode[c[0]]);
        if (t_rtoWait[lvl]) InterlockedIncrement(&k.rtoWaitAvoided);
        out[0] = c[0], out[2] = c[2], out[3] = c[3];  // out[1]: 0x831530 never reads it (docs/140 §6)
        t_rtoSt[lvl] = RTO_ANSWERED;
        return out;
    }
    RtoFallback(why);
    t_rtoKey = TS3A(0x0083156D);  // probe P0: the late take keyed as the original site
    const int uf = g_ufcC3 ? g_ufcC3(world + 0x918) : 0;  // docs/202 C3: an unseen Sim's late take with a deadline
    if (!uf) g_rtoAcq(world + 0x918, NULL, g_rtoDeadline);
    t_rtoKey = 0;
    if (uf < 0) {
        out[0] = 4, out[2] = out[3] = 0;
        t_rtoSt[lvl] = RTO_ANSWERED;
        InterlockedIncrement(&k.ufcFake[2]);
        return out;
    }
    t_rtoSt[lvl] = RTO_LATE;
    return g_rtoRoute(world, edx, out, sim, target, point, opts, a6, a7, a8, a9, a10);
}

static LONG __fastcall RtoRelStub(void* lock, void* edx) {
    const int lvl = --t_rtoN;
    const int st = lvl >= 0 && lvl < 8 ? t_rtoSt[lvl] : RTO_PLAIN;
    if (lvl < 0) t_rtoN = 0;  // shortcut: an unbalanced release (never seen) is passed through; the state resets
    if (st == RTO_SKIP) { InterlockedIncrement(&ts3native_counters.rtoNoRoute); return 1; }
    if (st == RTO_ANSWERED) return 1;
    if (st == RTO_UFC) { InterlockedIncrement(&ts3native_counters.ufcNoRoute); return 1; }  // docs/202 C3: nothing held
    const LONG r = g_rtoRel(lock, edx);
    if (st == RTO_SHADOW) RgOffSimDrop();
    return r;
}

static const CallSite kRtoSites[3] = {{RTO_REL_SITE, RTO_LOCK_REL, (void*)RtoRelStub},
                                      {RTO_CALL_SITE, RTO_ROUTE, (void*)RtoCallStub},
                                      {RTO_ACQ_SITE, RTO_LOCK_ACQ, (void*)RtoAcqStub}};

// [RouteToObjRO] Enabled=1: the three call sites (each an E8 to its expected callee, else nothing) in the order R-rel, R-call,
// R-acq, so SKIP is never pushed before its partners exist; needs the S2 writer set (QL, W1-W6, tripwires)
// each site an E8 to its expected callee (checked first, logged under `who`)
static bool RtoSitesOk(const char* who) {
    for (const CallSite& c : kRtoSites) {
        const u8* p = (const u8*)(uintptr_t)c.site;
        if (!Readable(p, 5) || p[0] != 0xE8 || (DWORD)(c.site + 5 + *(const LONG*)(p + 1)) != c.target) {
            Log("SKIP %s: the call at 0x%08lX is not to 0x%08lX (another hook?)", who, c.site, c.target);
            return false;
        }
    }
    return true;
}
// R-rel, R-call, R-acq in that order (rtoReady bits 2, 1, 0); also [UnseenFallback] C3's way in when [RouteToObjRO] is off
static bool RtoPatchSites() {
    for (int i = 0; i < 3; i++) {
        if (!PatchCalls(i == 0 ? "route-to-object RO R-rel" : i == 1 ? "route-to-object RO R-call" : "route-to-object RO R-acq",
                        &kRtoSites[i], 1))
            return false;
        ts3native_counters.rtoReady |= 4 >> i;
    }
    return true;
}

static void InstallRouteToObjRO(const wchar_t* ini) {
    Ts3nCounters& k = ts3native_counters;
    if (!GetPrivateProfileIntW(L"RouteToObjRO", L"Enabled", 0, ini)) { Log("route-to-object RO off (Enabled=0)"); return; }
    if (!k.rgEnforce || !k.asyncReady) { Log("SKIP route-to-object RO: needs [RouteGate] Enforce=1 with the async path"); return; }
    if (!RtoSitesOk("route-to-object RO")) return;
    if (!InstallSrWriters()) { Log("SKIP route-to-object RO: the S2 writer set (W1-W6, W4, tripwires) is not all in place"); return; }
    if (!RtoPatchSites()) return;
    if (k.slReady) k.rtoReady |= 8;
    k.rtoMode = Clamp(GetPrivateProfileIntW(L"RouteToObjRO", L"Mode", 1, ini), 0, 1);
    k.rtoShadow = Clamp(GetPrivateProfileIntW(L"RouteToObjRO", L"Shadow", 0, ini), 0, 1);
    k.rtoTailMode = Clamp(GetPrivateProfileIntW(L"RouteToObjRO", L"Tail", 0, ini), 0, 1);
    Log("OK   route-to-object RO: sites 0x831568 / 0x831633 / 0x8316FF (ready %ld, Mode=%ld Shadow=%ld Tail=%ld; SH %s)", k.rtoReady,
        k.rtoMode, k.rtoShadow, k.rtoTailMode, k.slReady ? "on" : "OFF: slot targets fall back");
}
