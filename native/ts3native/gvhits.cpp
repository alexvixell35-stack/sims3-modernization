// [GoalValidRO] Hits (docs/670 §11): GoalPoint_Validate's obstacle query answered read-only when footprints ARE found, by
// running the validator's own hit loop on the clone's footprints inside the QL hold. Included by ts3native.cpp before
// gvro.cpp (GvStub calls in here; uses steerro.cpp's SrCtx, slot types, lot lookup and option test, obstructro.cpp's point
// test type, savewrite.cpp's PatchCalls).
//
// The wait (key 0x75A64C, the largest +0x918 wait key left in s151-on: 23 lines, one 314 ms hitch): [GoalValidRO] answers
// only empty gathers, and on a lot every validated point has a footprint within the radius (s148-on: 223 of 577 job-time
// calls "footprints found", all given back). The game's loop over the gathered footprints, 0x75A672..0x75A92E inline in
// GoalPoint_Validate 0x759B20, runs after 0x82EDB0 has released +0x918: it holds one reference per footprint and reads
// them unlocked. It decides whether another Sim or object covers the goal point (two Sims must not pick the same spot),
// so the clone is exact: the same footprints in the same order through the same slot functions, the same writes.
//
// The loop [verified disassembly + decompile 2026-11-20], per footprint (vector of interface pointers, obj = pointer - 4):
//   id = obj slot +0x84; skipped when it is the caller's id ([ebp+0x18]+0x20), the validator's own ([esp+0x80]+0x50) or in
//   the options' id list (0x81C970 on [ebp+0x14]); type = interface slot +8, switch (bytes 0x75AF5C, targets 0x75AF4C):
//   1 (a Sim): id 0 or not in the object service (0x939560 on [esp+0x9C]): next. Slot +0xC0 true: class = slot +8 (record
//     esp+0xC0, 0); class 0, 1 or 0xD (0x620CA0): next; lot = World_LotLookup([esp+0x58], x, z), none: next; the point
//     through Lot 0xAB88A0, then slot +0xD4 (level 0, u, v, [0xFEC6DC]): covered = write the id to out+0x20 when that is 0
//     and LEAVE; else *cost = [0x1010960], next. Slot +0xC0 false: slot +0x5C false, or class 0 / 1 / 0xD: *cost, next;
//     else LEAVE.
//   9, 0x14: id != 0, out+0x20 == 0 and the object exists: out+0x20 = id. Then as the next row.
//   2, 0xC, 0xD, 0x11, 0x17: LEAVE unless [esp+0x13] != 0, [esp+0x17] == 0, slot +0x5C true and the mask obj+0x18 & +0x1C is
//     not -1; then out+0x20 = 0, next.
//   anything else: next.
// LEAVE = 0x75AABB: drop the footprint reference, release the vector, destroy the record, return 1 (the point is rejected).
// The loop's end = 0x75A939, where the empty answer also lands (same registers: esi and ebx are reloaded only because the
// loop used them, edi is dead).
// Slot targets over the nine footprint classes and the child class [vtable scan 2026-11-20]: +0x5C is 0x620D60 (false) or
// 0x7EAEA0 (true, the child class 0xFEBF90 only); +0xD4 is 0x80F030 wherever +0xC0 is true (the level-0 polygon cache, a
// pure point / radius test 0x67B130 -> 0x67AE90 -> 0x676F70 once the cache is valid; the rebuild 0x80E5B0 is the case
// given back, as [ObstructedRO] does). The class functions with a record that carries an options object read only the
// footprint, the record and the options (0x8294E0, 0x81C880, 0x81C970, 0x946E90), as under [PlacementRO].
// The object's smart pointer (0x565970 / 0x7460F0: lock +1, lock -1, destructor at 0) is skipped: with a count >= 1 it nets
// to nothing; a count below 1 is a doubt (the game's -1 would destroy the object).
//
// So GvStub hands GvhVisit to GvRun: it runs inside the QL hold on the sorted clone list (the game's vector is sorted by
// the same pointer) and only records what the loop would write. After every check of the protocol passed, GvhFinish writes
// the blocker id and the cost and returns al 1 with the vector still empty: the game continues at 0x75A939 exactly as
// after an empty answer. For LEAVE the stub's return address becomes 0x75AAC4, the exit's second step (the vector's
// release, empty here, the record's destructor, mov al, 1, the epilogue); the first step would release a footprint
// reference this path never took. The validator's frame is found from the stub's return slot and ebp (a naked thunk at the
// call site passes ebp in edx) and checked: return address, (ebp & ~15) == esp + 0x100, the vector and record addresses,
// the point argument. The code ranges this depends on are hashed at install.
// Any doubt calls the game's 0x82EDB0 (today's wait).
// HitsShadow (no job running): the game's call, then the loop on the clone; the prediction is checked against the game's
// own loop by two observer stubs on the calls every path leaves through (0x75A950 the loop's end, 0x75AACB LEAVE).
// Exported block ts3native_goalhitsro: +0x00 mode is the runtime switch (native_set goalhitsro).

struct GoalHitsRoCounters {
    volatile LONG gvhMode;            // +0x00 runtime switch: 1 = answer calls with footprints while a job runs
    volatile LONG gvhShadow;          // +0x04 1 = with no job running, compare the prediction with the game's loop
    volatile LONG gvhReady;           // +0x08 the thunk is on the call site (2: the observer sites too)
    volatile LONG gvhRuns;            // +0x0C job-time calls with footprints found on which the loop clone ran
    volatile LONG gvhTaken;           // +0x10 answered without the game's call
    volatile LONG gvhLeave;           // +0x14 of those: the point is rejected (LEAVE)
    volatile LONG gvhBlocker;         // +0x18 of those: a blocker id was written or cleared
    volatile LONG gvhCost;            // +0x1C of those: the cost was written
    volatile LONG gvhUs;              // +0x20 time in answered calls (query + loop), us
    LONG gvhMaxUs;                    // +0x24
    volatile LONG gvhFb[8];           // +0x28 given back to the game: GVH_FB_*
    volatile LONG gvhShadowRuns;      // +0x48 predictions checked by an observer
    volatile LONG gvhShadowMismatch;  // +0x4C must stay 0
    volatile LONG gvhFootprints;      // +0x50 footprints the answered loops walked
    LONG gvhMaxN;                     // +0x54 most footprints in one loop
    volatile LONG gvhShadowLeave;     // +0x58 of the checked predictions: LEAVE
    volatile LONG gvhShadowLost;      // +0x5C predictions no observer saw (the next call found one pending)
};
extern "C" __declspec(dllexport) GoalHitsRoCounters ts3native_goalhitsro = {};

enum { GVH_FB_FRAME, GVH_FB_ALIAS, GVH_FB_SLOT, GVH_FB_REF, GVH_FB_CACHE, GVH_FB_OVER, GVH_FB_LOTIDX, GVH_FB_OTHER };
enum { GVH_END = 0, GVH_LEAVE = 1 };  // the loop's two ways out; a doubt is -(1 + GVH_FB_*)

static const DWORD GVH_RET = TS3A(0x0075A64C);       // the query's return address: the frame's esp is fixed from here on
static const DWORD GVH_LEAVE_AT = TS3A(0x0075AAC4);  // LEAVE after its footprint release: lea ecx, [esp+0x84]; call 0x652470; ...
static const DWORD GVH_LEAVE_LBL = TS3A(0x0075AAA1), GVH_TABLE = TS3A(0x0075AF4C), GVH_FRAME_SUB = TS3A(0x00759B26);
static const DWORD GVH_SITE_END = TS3A(0x0075A950), GVH_SITE_LEAVE = GVH_LEAVE_AT + 7;  // the observers' call sites
static const DWORD GVH_SLOT_FALSE = TS3A(0x00620D60), GVH_SLOT_TRUE = TS3A(0x007EAEA0);
static const DWORD kGvhCodeFnv = 0xB6EAEA97;  // FNV-1a over the four ranges of GvhCodeOk (Steam 1.67)

typedef const u8*(__fastcall* GvhFindFn)(const void* svc, void* edx, DWORD lo, DWORD hi);           // ObjectService_FindById 0x939560, ret 8
typedef void(__fastcall* GvhToLotFn)(const u8* lot, void* edx, const float* pt, float* out);         // 0xAB88A0, ret 8 (movaps both)
typedef void(__fastcall* GvhVecRelFn)(DWORD* vec, void* edx);                                        // 0x652470
static GvhFindFn g_gvhFind = (GvhFindFn)(uintptr_t)TS3A(0x00939560);
static GvhToLotFn g_gvhToLot = (GvhToLotFn)(uintptr_t)TS3A(0x00AB88A0);
static SrVecRelFn g_gvhRelRange = (SrVecRelFn)(uintptr_t)TS3A(0x00AAC090);
static GvhVecRelFn g_gvhVecRel = (GvhVecRelFn)(uintptr_t)TS3A(0x00652470);
static const float* g_gvhRadius = (const float*)(uintptr_t)TS3A(0x00FEC6DC);  // the +0xD4 test's radius
static const DWORD* g_gvhCostBits = (const DWORD*)(uintptr_t)TS3A(0x01010960);  // the cost the loop writes (copied as bits)

// What the loop reads from the validator's frame and arguments, and what it would write
struct GvhCall {
    const u8* f;        // the validator's esp at 0x75A64C
    const float* pt;    // [ebp+0x10]
    const u8* opts;     // [ebp+0x14]
    const u8* self;     // [ebp+0x18]: +0x20 / +0x24 the caller's id
    u8* out;            // [ebp+0x20]: +0x20 / +0x24 the blocker id
    DWORD* cost;        // [ebp+0x24]
    const u8* owner;    // [esp+0x80]: +0x50 / +0x54 the validator's own id
    u8* wm;             // [esp+0x58]
    const void* svc;    // [esp+0x9C]
    const u8* rec;      // esp+0xC0
    u8 f13, f17;        // [esp+0x13], [esp+0x17]
    // the answer (GvhVisit)
    DWORD blk[2];       // out+0x20 / +0x24 as the loop leaves them
    bool blkWrote, costWrote;
    int n, doubt;
};

static bool GvhOverlap(const void* a, size_t na, const void* b, size_t nb) {
    const uintptr_t x = (uintptr_t)a, y = (uintptr_t)b;
    return x < y + nb && y < x + na;
}

// The validator's frame behind the stub's return slot, or false. ebp is the validator's frame pointer (push ebp; mov ebp,
// esp; and esp, -16; sub esp, 0xF4; three pushes: esp = (ebp & ~15) - 0x100 everywhere the loop reads [esp+...]).
static bool GvhFrame(const void* retSlot, const u8* ebp, const float* pt, const DWORD* vec, DWORD opt, GvhCall* g, bool checkRet = true) {
    const u8* f = (const u8*)retSlot + 4 + 0x14;  // the query pops its five arguments
    if (checkRet && *(const DWORD*)retSlot != GVH_RET) return false;
    if (!ebp || ((uintptr_t)ebp & ~(uintptr_t)15) != (uintptr_t)f + 0x100) return false;
    if ((const u8*)vec != f + 0x84 || *(const float* const*)(ebp + 0x10) != pt) return false;
    const u8* rec = f + 0xC0;  // 0x75A5B7..0x75A618: {opt, id +8, pointer +0x20 = 0, +0x24 = 1, +0x25 = 0, options +0x28}
    if (*(const DWORD*)rec != opt || *(const DWORD*)(rec + 0x20) || rec[0x24] != 1 || *(const u8* const*)(rec + 0x28) != *(const u8* const*)(ebp + 0x14))
        return false;
    g->f = f, g->pt = pt, g->rec = rec;
    g->opts = *(const u8* const*)(ebp + 0x14);
    g->self = *(const u8* const*)(ebp + 0x18);
    g->out = *(u8* const*)(ebp + 0x20);
    g->cost = *(DWORD* const*)(ebp + 0x24);
    g->owner = *(const u8* const*)(f + 0x80);
    g->wm = *(u8* const*)(f + 0x58);
    g->svc = *(const void* const*)(f + 0x9C);
    g->f13 = f[0x13], g->f17 = f[0x17];
    g->blkWrote = g->costWrote = false;
    g->n = 0, g->doubt = 0;
    g->blk[0] = g->blk[1] = 0;
    if (!g->opts || !g->self || !g->out || !g->cost || !g->owner || !g->wm || !g->svc) return false;
    return true;
}
// The loop's writes (out+0x20 .. +0x28, *cost) must not be something it reads back through another pointer: the clone
// replays the writes after the loop. The game reads the two ids, the point, the record and the options afresh per footprint.
static bool GvhAliased(const GvhCall& g) {
    const u8* o = g.out + 0x20;
    const void* const rd[] = {g.self + 0x20, g.owner + 0x50, g.pt, g.rec, g.opts + 0x28, g.f};
    const size_t rn[] = {8, 8, 16, 0x30, 0x18, 0x100};
    for (int i = 0; i < 6; i++)
        if (GvhOverlap(o, 8, rd[i], rn[i]) || GvhOverlap(g.cost, 4, rd[i], rn[i])) return true;
    return GvhOverlap(o, 8, g.cost, 4);
}

static int GvhDoubt(GvhCall* g, int fb) { g->doubt = fb; return -(1 + fb); }
static bool GvhClassSkips(int cls) { return (DWORD)cls < 2 || cls == 0xD; }  // 0x620CA0

// One footprint of the loop: 0 = next, GVH_LEAVE, or a doubt
static int GvhOne(const u8* obj, GvhCall* g) {
    const void* const* vt = *(const void* const* const*)obj;
    const u8* sub = obj + 4;
    const void* const* svt = *(const void* const* const*)sub;
    if (!vt || !svt) return GvhDoubt(g, GVH_FB_SLOT);
    // SrSlotsOk vetted slots +8, +0x84, +0xC0 and the interface's +8 at the gather; the loop's two other slots here
    const DWORD s5c = (DWORD)(uintptr_t)vt[0x5C / 4];
    if (g_srCheckSlots && s5c != GVH_SLOT_FALSE && s5c != GVH_SLOT_TRUE) return GvhDoubt(g, GVH_FB_SLOT);
    const unsigned __int64 id = ((SlotIdFn)vt[0x84 / 4])(obj, NULL);
    const DWORD lo = (DWORD)id, hi = (DWORD)(id >> 32);
    if (lo == *(const DWORD*)(g->self + 0x20) && hi == *(const DWORD*)(g->self + 0x24)) return 0;
    if (lo == *(const DWORD*)(g->owner + 0x50) && hi == *(const DWORD*)(g->owner + 0x54)) return 0;
    if (g_srOptId(g->opts, NULL, lo, hi)) return 0;
    const int t = ((SlotIntFn)svt[2])(sub, NULL);
    if (t == 1) {
        if (!(lo | hi)) return 0;
        const u8* o = g_gvhFind(g->svc, NULL, lo, hi);
        if (!o) return 0;
        if (*(const LONG*)(o + 4) < 1) return GvhDoubt(g, GVH_FB_REF);  // the game's +1 / -1 would run its destructor
        if (((SlotBoolFn)vt[0xC0 / 4])(obj, NULL)) {
            if (GvhClassSkips(((SlotClassFn)vt[2])(obj, NULL, g->rec, 0))) return 0;
            const u8* lot = g_srLotLookup(g->wm, NULL, g->pt[0], g->pt[2]);
            if (!lot) return 0;
            __declspec(align(16)) float uv[4] = {};
            g_gvhToLot(lot, NULL, g->pt, uv);
            if (g_srCheckSlots && (DWORD)(uintptr_t)vt[0xD4 / 4] != OR_SLOT_PT) return GvhDoubt(g, GVH_FB_SLOT);
            const u8* cache = *(const u8* const*)(obj + 0x38);
            if (!cache || !cache[0x20]) return GvhDoubt(g, GVH_FB_CACHE);  // 0x80F030 would rebuild level 0 (0x80E5B0)
            u8 before[0x24];
            memcpy(before, cache, sizeof(before));
            const u8 hit = ((OrPtFn)vt[0xD4 / 4])(obj, NULL, 0, uv[0], uv[2], *g_gvhRadius);
            if (memcmp(before, cache, sizeof(before)) != 0) return GvhDoubt(g, GVH_FB_CACHE);  // a writer outside W1-W6
            if (hit) {
                if (!(g->blk[0] | g->blk[1])) g->blk[0] = lo, g->blk[1] = hi, g->blkWrote = true;
                return GVH_LEAVE;
            }
            g->costWrote = true;
            return 0;
        }
        if (((SlotBoolFn)vt[0x5C / 4])(obj, NULL) && !GvhClassSkips(((SlotClassFn)vt[2])(obj, NULL, g->rec, 0))) return GVH_LEAVE;
        g->costWrote = true;
        return 0;
    }
    if (t == 9 || t == 0x14) {
        if ((lo | hi) && !(g->blk[0] | g->blk[1])) {
            const u8* o = g_gvhFind(g->svc, NULL, lo, hi);
            if (o) {
                if (*(const LONG*)(o + 4) < 1) return GvhDoubt(g, GVH_FB_REF);
                g->blk[0] = lo, g->blk[1] = hi, g->blkWrote = true;
            }
        }
    } else if (t != 2 && t != 0xC && t != 0xD && t != 0x11 && t != 0x17) return 0;
    if (!g->f13 || g->f17 || !((SlotBoolFn)vt[0x5C / 4])(obj, NULL)) return GVH_LEAVE;
    if ((*(const DWORD*)(obj + 0x18) & *(const DWORD*)(obj + 0x1C)) == 0xFFFFFFFF) return GVH_LEAVE;
    g->blk[0] = g->blk[1] = 0, g->blkWrote = true;
    return 0;
}
// The loop on a list (sub 0: the clone's objects; 4: the game's vector of interface pointers, NULL entries skipped)
static int GvhLoop(const u8* const* list, int n, int sub, GvhCall* g) {
    g->blk[0] = *(const DWORD*)(g->out + 0x20), g->blk[1] = *(const DWORD*)(g->out + 0x24);
    g->blkWrote = g->costWrote = false;
    g->n = n, g->doubt = 0;
    for (int i = 0; i < n; i++) {
        if (!list[i]) continue;
        if (const int a = GvhOne(list[i] - sub, g)) return a;
    }
    return GVH_END;
}
// GvRun's visitor: inside the QL hold, on the sorted clone list
static int GvhVisit(const SrCtx& c, const void* arg) {
    GvhCall* g = (GvhCall*)arg;
    if (c.over) return GvhDoubt(g, GVH_FB_OVER);
    return GvhLoop(c.objs, c.n, 0, g);
}
// the loop's writes, once the protocol's checks passed
static void GvhCommit(const GvhCall& g) {
    if (g.blkWrote) *(DWORD*)(g.out + 0x20) = g.blk[0], *(DWORD*)(g.out + 0x24) = g.blk[1];
    if (g.costWrote) *g.cost = *g_gvhCostBits;
}
// before the query: the frame, the alias rule and the lot index the loop's own lot lookup would rebuild
static bool GvhArm(const void* retSlot, const u8* ebp, const float* pt, const DWORD* vec, DWORD opt, GvhCall* g) {
    GoalHitsRoCounters& k = ts3native_goalhitsro;
    if (!GvhFrame(retSlot, ebp, pt, vec, opt, g)) { InterlockedIncrement(&k.gvhFb[GVH_FB_FRAME]); return false; }
    if (GvhAliased(*g)) { InterlockedIncrement(&k.gvhFb[GVH_FB_ALIAS]); return false; }
    if (g->wm[0x1CC]) { InterlockedIncrement(&k.gvhFb[GVH_FB_LOTIDX]); return false; }
    return true;
}
// after GvRun answered with footprints and no doubt of its own: true = the call is answered (ans GVH_END or GVH_LEAVE)
static bool GvhFinish(const GvhCall& g, int ans, LARGE_INTEGER t0) {
    GoalHitsRoCounters& k = ts3native_goalhitsro;
    InterlockedIncrement(&k.gvhRuns);
    if (ans < 0) { InterlockedIncrement(&k.gvhFb[g.doubt >= 0 && g.doubt < 8 ? g.doubt : GVH_FB_OTHER]); return false; }
    GvhCommit(g);
    const LONG us = UsSince(t0);
    InterlockedIncrement(&k.gvhTaken);
    if (ans == GVH_LEAVE) InterlockedIncrement(&k.gvhLeave);
    if (g.blkWrote) InterlockedIncrement(&k.gvhBlocker);
    if (g.costWrote) InterlockedIncrement(&k.gvhCost);
    InterlockedExchangeAdd(&k.gvhFootprints, g.n);
    Max(&k.gvhMaxN, g.n);
    InterlockedExchangeAdd(&k.gvhUs, us);
    Max(&k.gvhMaxUs, us);
    return true;
}

// --- HitsShadow: the prediction, checked where the game's own loop leaves ---
static struct GvhPending { bool valid; int ans; GvhCall g; DWORD cost0; } g_gvhPend;  // owner thread only
static volatile LONG g_gvhNoted;
static void GvhPredict(const GvhCall& g, int ans) {
    if (g_gvhPend.valid) InterlockedIncrement(&ts3native_goalhitsro.gvhShadowLost);
    g_gvhPend.valid = ans >= 0;
    g_gvhPend.ans = ans, g_gvhPend.g = g, g_gvhPend.cost0 = *g.cost;
    if (ans < 0) InterlockedIncrement(&ts3native_goalhitsro.gvhFb[g.doubt >= 0 && g.doubt < 8 ? g.doubt : GVH_FB_OTHER]);
}
static void GvhObserve(int how, const u8* f) {
    GvhPending& p = g_gvhPend;
    if (!p.valid || GetCurrentThreadId() != g_async.owner || p.g.f != f) return;
    GoalHitsRoCounters& k = ts3native_goalhitsro;
    p.valid = false;
    const GvhCall& g = p.g;
    const DWORD b0 = *(const DWORD*)(g.out + 0x20), b1 = *(const DWORD*)(g.out + 0x24);
    const DWORD costWant = g.costWrote ? *g_gvhCostBits : p.cost0;
    InterlockedIncrement(&k.gvhShadowRuns);
    if (how == GVH_LEAVE) InterlockedIncrement(&k.gvhShadowLeave);
    if (how != p.ans || b0 != g.blk[0] || b1 != g.blk[1] || *g.cost != costWant) {
        InterlockedIncrement(&k.gvhShadowMismatch);
        if (InterlockedIncrement(&g_gvhNoted) <= 16)
            Log("goal hits RO: SHADOW MISMATCH at (%.2f, %.2f), %d footprints: the game %s, blocker %08lX:%08lX, cost %08lX; clone %s, "
                "blocker %08lX:%08lX, cost %08lX", g.pt[0], g.pt[2], g.n, how == GVH_LEAVE ? "left" : "ended", b1, b0, *g.cost,
                p.ans == GVH_LEAVE ? "left" : "ended", g.blk[1], g.blk[0], costWant);
    }
}
// 0x75A950: FootprintRefVec_ReleaseRange after the loop's end (and after an empty answer): esp = the frame after two pushes
static void __fastcall GvhEndStub(void* vec, void* edx, void* b, void* e) {
    GvhObserve(GVH_END, (const u8*)_AddressOfReturnAddress() + 4 + 8);
    g_gvhRelRange(vec, edx, b, e);
}
// 0x75AACB: the vector's release on LEAVE (no stack argument)
static void __fastcall GvhLeaveStub(DWORD* vec, void* edx) {
    GvhObserve(GVH_LEAVE, (const u8*)_AddressOfReturnAddress() + 4);
    g_gvhVecRel(vec, edx);
}
static const CallSite kGvhObservers[] = {{GVH_SITE_END, TS3A(0x00AAC090), (void*)GvhEndStub}, {GVH_SITE_LEAVE, TS3A(0x00652470), (void*)GvhLeaveStub}};

// The code this depends on, as read: the loop and the loop's end up to the release call's operand, LEAVE up to its release
// call's operand, the rest of the exit, the switch tables; and the frame size
static bool GvhCodeOk() {
    static const DWORD kR[4][2] = {{GVH_RET, GVH_SITE_END + 1}, {GVH_LEAVE_LBL, GVH_SITE_LEAVE + 1}, {GVH_LEAVE_AT + 0xC, GVH_LEAVE_AT + 0x23},
                                   {GVH_TABLE, GVH_TABLE + 0x28}};
    static const u8 kSub[] = {0x81, 0xEC, 0xF4, 0x00, 0x00, 0x00};  // sub esp, 0xF4 (after the 6 prologue bytes other hooks may own)
    if (!Matches((const void*)(uintptr_t)GVH_FRAME_SUB, kSub, sizeof(kSub))) return false;
    DWORD h = 2166136261u;
    for (const auto& r : kR) {
        if (!Readable((const void*)(uintptr_t)r[0], r[1] - r[0])) return false;
        for (const u8 *p = (const u8*)(uintptr_t)r[0], *e = (const u8*)(uintptr_t)r[1]; p != e; p++) h = (h ^ *p) * 16777619u;
    }
    return h == kGvhCodeFnv;
}

// the 60 s counter line (ts3native.cpp MaybeLogCounters)
static void GvhLogCounters() {
    const GoalHitsRoCounters& k = ts3native_goalhitsro;
    if (!k.gvhReady) return;
    Log("goal hits RO: mode %ld shadow %ld, loops run %ld: answered %ld (rejected %ld, blocker id %ld, cost %ld; footprints %ld, max %ld; "
        "%ld ms, max %ld us); given back: frame %ld, alias %ld, slot %ld, object count %ld, polygon cache %ld, list full %ld, lot index "
        "%ld, other %ld; shadow checks %ld (rejected %ld) mismatch %ld, unseen %ld",
        k.gvhMode, k.gvhShadow, k.gvhRuns, k.gvhTaken, k.gvhLeave, k.gvhBlocker, k.gvhCost, k.gvhFootprints, k.gvhMaxN, k.gvhUs / 1000, k.gvhMaxUs,
        k.gvhFb[GVH_FB_FRAME], k.gvhFb[GVH_FB_ALIAS], k.gvhFb[GVH_FB_SLOT], k.gvhFb[GVH_FB_REF], k.gvhFb[GVH_FB_CACHE], k.gvhFb[GVH_FB_OVER],
        k.gvhFb[GVH_FB_LOTIDX], k.gvhFb[GVH_FB_OTHER], k.gvhShadowRuns, k.gvhShadowLeave, k.gvhShadowMismatch, k.gvhShadowLost);
}
