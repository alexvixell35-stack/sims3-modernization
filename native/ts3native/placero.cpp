// [PlaceTestRO] (docs/670): the footprint check of the object placement test answered read-only while an async route job
// holds +0x918. Included by ts3native.cpp after gvro.cpp (uses steerro.cpp's QL, writer set, SrEntry / SrCell / SrVisit;
// avoidro.cpp's AvMayRun; savewrite.cpp's PatchCalls).
//
// The wait (s142 / s143 storms: the largest +0x918 wait key of the sim thread, 0x82F4AB, 3.2-6.1 s per 4 min):
// RoutingWorld_PortalCheckLocked 0x82F470 (thiscall world; lot, level, polygon, vec, mode, opt; ret 0x18) takes +0x918 at
// 0x82F4A6 and gathers the footprints of one lot level that overlap a polygon: record {opt, +0x24 = 1, id and options
// object 0}, ctx+0x50 = 0, ctx+0x54 = lot, then 0x632060 (LotTable_Contains, cell graph 0x62FB10 of the level, QueryPolygon
// 0x667560: pin, flush when deferred, polygon walk 0x6641B0 with visitor 0x662DB0 for mode 0), copies the set into vec
// (+1 each) and returns 1 [verified decompile + disassembly, docs/670 §2]. Its only caller is the placement footprint check
// 0xB41120 (cdecl (rec, x), al; called at 0xB462B7 only, under World_FindGoodLocation*, FindPlaceOnRoad*,
// FindPlacesToDisembarkOnShoreline, TestObjectPlacement* and Slots_CanAttachToSlotWithOffset [verified CallerTree]):
// rec+0x30 == 0, rec+4 (lot) == 0 or rec+0 (object) == 0 answer 1 before any routing read; else it reads the routing world's
// byte +0x30, gathers with (lot = rec+4, level = rec+0x24, polygon = rec+0xA0, a fresh empty vec, mode 0, opt 0x100) and
// answers 0 when a gathered footprint whose id (interface slot +4) is not the object's (rec+0 -> +8 / +0xC) has a
// blocking type (interface slot +8; PtBlocks in placero-core.h), else 1. It writes nothing else that outlives the call.
//
// So the whole check is a pure function of the lot's footprint quadtree: the stub at 0xB462B7 answers it from S2's
// protocol (QL shared, try only; tripwires, sequence, generation; SrCollect's slot whitelist, which covers both interface
// slots) on the sim (owner) thread while a job runs, when the thread is not the +0x918 owner, in no job callback and in no
// gate frame. No refcount is written and nothing is copied.
//
// The polygon walk is ours (PtWalk, placero-core.h) on the game's iterator helpers, because the game's walk tests each
// cell with 0x66AB70, which clips the polygon in the global scratch object 0x1158580 (two point vectors; 0x67C070 is
// thiscall on it): one scratch for every thread, safe in the game only because every user holds +0x918. PtClip builds
// the same cell rectangle (same operations: the sums are exact, the table values are dyadic) and calls 0x67C070 on a
// scratch of our own.
// Any doubt (QL busy, lot not in the table, deferred flush, pin moved, unknown slot, buffer limit, tripwire, job end, a
// walk 13 cells deep) calls the game's 0xB41120 (today's wait). Shadow (no job): the game's call, then the clone, compared.
// Exported block ts3native_placetestro: +0x00 mode is the runtime switch (native_set placetestro); Shadow is ini only.
#include "placero-core.h"

struct PlaceTestRoCounters {
    volatile LONG ptMode;            // +0x00 runtime switch: 1 = answer while a job runs
    volatile LONG ptShadow;          // +0x04 runtime switch: 1 = with no job running, compare the clone with the game's call
    volatile LONG ptReady;           // +0x08 the call site is patched
    volatile LONG ptCalls;           // +0x0C checks of the sim thread seen with mode or shadow on
    volatile LONG ptJob;             // +0x10 of those, while a job ran
    volatile LONG ptTaken;           // +0x14 answered without the game's call
    volatile LONG ptBlocked;         // +0x18 of those, answer 0 (a blocking footprint)
    volatile LONG ptEmpty;           // +0x1C of those, no footprint gathered
    volatile LONG ptUs;              // +0x20 time in answered clones, us
    LONG ptMaxUs;                    // +0x24
    volatile LONG ptFb[8];           // +0x28 calls given back to the game: PT_FB_*
    volatile LONG ptShadowRuns;      // +0x48
    volatile LONG ptShadowMismatch;  // +0x4C must stay 0
    volatile LONG ptGameUs;          // +0x50 time in the game's call while a job ran (the wait), us
    LONG ptGameMaxUs;                // +0x54
};
extern "C" __declspec(dllexport) PlaceTestRoCounters ts3native_placetestro = {};

static const DWORD PT_SITE = TS3A(0x00B462B7), PT_TARGET = TS3A(0x00B41120);
typedef DWORD(__cdecl* PtFn)(const u8* rec, void* x);
static PtFn g_ptOrig = (PtFn)(uintptr_t)PT_TARGET;
typedef DWORD(__fastcall* PtClipGameFn)(DWORD* scratch, void* edx, const void* poly, const float* lo, const float* hi, void* out);  // 0x67C070, ret 0x10
static PtClipGameFn g_ptClipGame = (PtClipGameFn)(uintptr_t)TS3A(0x0067C070);
static PtWalkFns g_ptWalkFns = {(void(__fastcall*)(PtIter*))(uintptr_t)TS3A(0x00669BE0),
                                (void(__fastcall*)(PtIter*, void*, const u8*, DWORD, DWORD, float, DWORD, DWORD, DWORD, DWORD, const u8*,
                                                   DWORD))(uintptr_t)TS3A(0x00669C30),
                                (DWORD(__fastcall*)(const PtIter*))(uintptr_t)TS3A(0x00669CB0),
                                (void(__fastcall*)(PtIter*, void*, DWORD, PtIter*))(uintptr_t)TS3A(0x0066A040)};
static const float* g_ptCellXY = (const float*)(uintptr_t)TS3A(0x00FEE118);    // per sub-cell code: {x, y} offset in cells
static const float* g_ptCellSize = (const float*)(uintptr_t)TS3A(0x00FEE3C0);  // per sub-cell code: edge in cells
static const DWORD* g_ptGameScratch = (const DWORD*)(uintptr_t)TS3A(0x01158580);  // read for its two allocator tags only
static DWORD g_ptScratch[8];  // our clip scratch: two EA vectors {begin, end, cap, tag}; owner thread only, never freed
static bool g_ptScratchSet;

// QuadTreeCell_ClipScratch 0x66AB70 on our scratch: the cell's rectangle [verified disassembly 0x66AB73..0x66AC48: x87
// int + table float stored as single, then single mul / add in this order], then the game's clip
static bool PtClip(const PtIter* it, const void* shape) {
    const DWORD b = it->code & 0xFF;
    const float fy = (float)((double)(LONG)(it->pos >> 16 & 0xFFF) + (double)g_ptCellXY[b * 2 + 1]);
    const float fx = (float)((double)(LONG)(it->pos & 0xFFF) + (double)g_ptCellXY[b * 2]);
    const __m128 s = _mm_load_ss(&it->size), ox = _mm_load_ss((const float*)(it->q + 4)), oy = _mm_load_ss((const float*)(it->q + 8));
    const __m128 d = _mm_load_ss(&g_ptCellSize[b]), vx = _mm_load_ss(&fx), vy = _mm_load_ss(&fy);
    float lo[2], hi[2];
    _mm_store_ss(&lo[0], _mm_add_ss(_mm_mul_ss(vx, s), ox));
    _mm_store_ss(&lo[1], _mm_add_ss(_mm_mul_ss(s, vy), oy));
    _mm_store_ss(&hi[0], _mm_add_ss(_mm_mul_ss(_mm_add_ss(d, vx), s), ox));
    _mm_store_ss(&hi[1], _mm_add_ss(_mm_mul_ss(_mm_add_ss(d, vy), s), oy));
    return (u8)g_ptClipGame(g_ptScratch, NULL, shape, lo, hi, NULL) != 0;
}
static bool PtVisit(void* ctx, const PtIter* it) { return SrVisit((SrCtx*)ctx, (const u8*)it, NULL) != 0; }

enum { PT_FB_STATE, PT_FB_TRY, PT_FB_LOT, PT_FB_SHAPE, PT_FB_TRIP, PT_FB_JOBEND, PT_FB_DEEP, PT_FB_MAYNOT };
static const int PT_WHY_JOBEND = 100, PT_WHY_DEEP = 101;  // above the SR_* codes

// 0x632060 from its first read on, with 0x82F470's record: the footprints land in c (not sorted: the answer is "any")
typedef int (*PtQueryFn)(SrCtx* c, const u8* p, const u8* lot, LONG level, const void* shape, const u8* rec);
static int PtQueryGame(SrCtx* c, const u8* p, const u8* lot, LONG level, const void* shape, const u8* rec) {
    c->n = 0;
    c->fail = SR_OK;
    c->over = 0;
    c->rec = rec;
    c->flag = 0;  // mode 0: visitor 0x662DB0
    const DWORD lo = *(const DWORD*)(lot + 0x48), hi = *(const DWORD*)(lot + 0x4C);
    if (!(lo | hi)) return SR_OK;  // 0x63207B
    const u8* entry;
    const int why = SrEntry(p, lo, hi, true, &entry);
    if (why || !entry) return why;
    const u8* q = SrCell(entry, level);
    if (!q) return SR_OK;
    // QueryPolygon 0x667560 flushes first when Q+0x48 < 1 and the deferred flag is set (a write): a doubt (as SrWalk2)
    const LONG q48 = *(volatile const LONG*)(q + 0x48);
    const u8 q4c = *(volatile const u8*)(q + 0x4C);
    if (q4c && q48 < 1) return SR_FB_DEFERRED;
    if (PtWalk(g_ptWalkFns, q, shape, PtClip, PtVisit, c) != PT_WALK_OK) return PT_WHY_DEEP;
    if (*(volatile const LONG*)(q + 0x48) != q48 || *(volatile const u8*)(q + 0x4C) != q4c) return SR_FB_TRIP;
    if (!c->fail && c->over) return SrShape(SRS_OBJCAP);
    return c->fail;
}
static PtQueryFn g_ptQuery = PtQueryGame;  // selftest hands in fake footprint lists

// 0xB41120's loop over the gathered footprints (inside the QL hold: they stay valid only there). 1 = nothing blocks
static DWORD PtAnswer(const SrCtx& c, const u8* rec, bool worldFlag) {
    const u8* self = *(const u8* const*)rec;
    const bool recFlag = rec[0x84] != 0;
    for (int i = 0; i < c.n; i++) {
        const u8* sub = c.objs[i] + 4;
        const void* const* svt = *(const void* const* const*)sub;
        const unsigned __int64 id = ((SlotIdFn)svt[1])(sub, NULL);
        if ((DWORD)id == *(const DWORD*)(self + 8) && (DWORD)(id >> 32) == *(const DWORD*)(self + 0xC)) continue;
        if (PtBlocks(((SlotIntFn)svt[2])(sub, NULL), worldFlag, recFlag)) return 0;
    }
    return 1;
}

// The clone of 0xB41120 past its three early outs. *ans = its al, *n = footprints gathered
static int PtRun(const u8* rec, bool job, DWORD* ans, int* n) {
    *ans = 1, *n = 0;
    u8* world = g_srWorldGet();  // 0xB41147
    if (!world) return SR_FB_STATE;
    const u8* p = *(const u8* const*)(world + 0x95C);  // 0x82F548
    if (!p) return SrShape(SRS_NOPLANNER);
    const bool worldFlag = world[0x30] != 0;  // 0xB41173
    const u8* lot = *(const u8* const*)(rec + 4);
    __declspec(align(16)) u8 qrec[0x30] = {};  // 0x82F470's record: option word 0x100, +0x24 = 1, id and options object 0
    *(DWORD*)qrec = 0x100;
    qrec[0x24] = 1;
    if (!g_ptScratchSet) {  // the allocator tags of the game's scratch vectors (constants since its static initialiser)
        g_ptScratch[3] = g_ptGameScratch[3], g_ptScratch[7] = g_ptGameScratch[7];
        g_ptScratchSet = true;
    }
    if (t_qlDepth || t_qlShared) return SR_FB_STATE;  // SRW locks are not recursive
    if (!TryAcquireSRWLockShared(&g_ql)) return SR_FB_TRY;
    if (g_steerBroken) { ReleaseSRWLockShared(&g_ql); return SR_FB_STATE; }
    const LONG seq = g_qlSeq;
    InterlockedExchange(&g_srInClone, 1);
    const LONG twEpoch = g_twEpoch, twGen = g_twGen;
    t_inRo = true;
    t_qlShared = 1;
    t_srFail = SR_OK;
    int why = job && !g_twArmed ? PT_WHY_JOBEND : g_twActive ? SR_FB_TRIP : SR_OK;
    static SrCtx c;  // owner thread only
    if (!why) why = g_ptQuery(&c, p, lot, *(const LONG*)(rec + 0x24), rec + 0xA0, qrec);
    if (!why && t_srFail) why = t_srFail;
    if (!why) *ans = PtAnswer(c, rec, worldFlag);  // before the checks below, so they cover it too
    if (!why && (g_qlSeq != seq || g_twActive || g_twEpoch != twEpoch)) why = SR_FB_TRIP;
    if (!why && job && g_twGen != twGen) why = PT_WHY_JOBEND;  // disarmed during the clone: writers unseen
    t_inRo = false;
    t_qlShared = 0;
    ReleaseSRWLockShared(&g_ql);
    InterlockedExchange(&g_srInClone, 0);
    if (!why) *n = c.n;
    return why;
}

static void PtFallback(int why) {
    int i;
    switch (why) {
    case SR_FB_TRY: i = PT_FB_TRY; break;
    case SR_FB_DIRTY: case SR_FB_LOTMISS: case SR_FB_DEFERRED: i = PT_FB_LOT; break;
    case SR_FB_SHAPE: case SR_FB_CACHE: i = PT_FB_SHAPE; break;
    case SR_FB_TRIP: i = PT_FB_TRIP; break;
    case PT_WHY_JOBEND: i = PT_FB_JOBEND; break;
    case PT_WHY_DEEP: i = PT_FB_DEEP; break;
    default: i = PT_FB_STATE; break;
    }
    InterlockedIncrement(&ts3native_placetestro.ptFb[i]);
}

static volatile LONG g_ptNoted;
static DWORD PtShadow(const u8* rec, void* x) {
    PlaceTestRoCounters& k = ts3native_placetestro;
    const DWORD a = g_ptOrig(rec, x);
    DWORD b = 1;
    int n = 0;
    const int why = PtRun(rec, false, &b, &n);
    InterlockedIncrement(&k.ptShadowRuns);
    if (why) { PtFallback(why); return a; }
    if (((u8)a != 0) != (b != 0)) {
        InterlockedIncrement(&k.ptShadowMismatch);
        if (InterlockedIncrement(&g_ptNoted) <= 16)
            Log("place test RO: SHADOW MISMATCH level %ld, %d polygon points: game %u, clone %lu (%d footprints)", *(const LONG*)(rec + 0x24),
                (int)((*(const DWORD*)(rec + 0xA4) - *(const DWORD*)(rec + 0xA0)) / 8), (unsigned)(u8)a, b, n);
    }
    return a;
}

// the game's call while a job runs: the +0x918 wait this switch is about
static DWORD PtGame(const u8* rec, void* x) {
    PlaceTestRoCounters& k = ts3native_placetestro;
    LARGE_INTEGER t0;
    QueryPerformanceCounter(&t0);
    const DWORD a = g_ptOrig(rec, x);
    const LONG us = UsSince(t0);
    InterlockedExchangeAdd(&k.ptGameUs, us);
    Max(&k.ptGameMaxUs, us);
    return a;
}

static DWORD __cdecl PtStub(const u8* rec, void* x) {
    PlaceTestRoCounters& k = ts3native_placetestro;
    const DWORD tid = GetCurrentThreadId();
    if ((!k.ptMode && !k.ptShadow) || tid != g_async.owner) return g_ptOrig(rec, x);
    if (!rec[0x30] || !*(const DWORD*)(rec + 4) || !*(const DWORD*)rec) return g_ptOrig(rec, x);  // 0xB41128..0xB4113F: al 1, no routing read
    InterlockedIncrement(&k.ptCalls);
    if (g_async.state != A_RUNNING) {
        if (!k.ptShadow) return g_ptOrig(rec, x);
        InterlockedIncrement(&g_rgOffSimOpen);  // no job starts while the two answers are compared (worker Dekker order)
        const DWORD a = g_async.state == A_RUNNING ? g_ptOrig(rec, x) : PtShadow(rec, x);
        RgOffSimDrop();
        return a;
    }
    InterlockedIncrement(&k.ptJob);
    if (!k.ptMode) return PtGame(rec, x);
    if (!AvMayRun(tid)) {  // the +0x918 owner (lent or recursive hold), a job callback or an open gate frame: no wait to save
        InterlockedIncrement(&k.ptFb[PT_FB_MAYNOT]);
        return g_ptOrig(rec, x);
    }
    LARGE_INTEGER t0;
    QueryPerformanceCounter(&t0);
    DWORD ans = 1;
    int n = 0;
    const int why = PtRun(rec, true, &ans, &n);
    if (!why) {
        const LONG us = UsSince(t0);
        InterlockedIncrement(&k.ptTaken);
        if (!ans) InterlockedIncrement(&k.ptBlocked);
        if (!n) InterlockedIncrement(&k.ptEmpty);
        InterlockedExchangeAdd(&k.ptUs, us);
        Max(&k.ptMaxUs, us);
        return ans;
    }
    PtFallback(why);
    return PtGame(rec, x);
}

static const CallSite kPtSite = {PT_SITE, PT_TARGET, (void*)PtStub};

// [PlaceTestRO] Enabled=1: needs [RouteGate] Enforce=1 with the async path and the S2 writer set (QL, W1-W6, tripwires)
static void InstallPlaceTestRO(const wchar_t* ini) {
    PlaceTestRoCounters& k = ts3native_placetestro;
    if (!GetPrivateProfileIntW(L"PlaceTestRO", L"Enabled", 0, ini)) { Log("place test RO off (Enabled=0)"); return; }
#ifdef TS3A_TABLE
    if (ts3a_noaddr) { Log("SKIP place test RO: no EA 1.69 address, docs/670"); return; }
#endif
    if (!ts3native_counters.rgEnforce || !ts3native_counters.asyncReady) { Log("SKIP place test RO: needs [RouteGate] Enforce=1 with the async path"); return; }
    if (!InstallSrWriters()) { Log("SKIP place test RO: the S2 writer set (W1-W6, W4, tripwires) is not all in place"); return; }
    if (!PatchCalls("place test RO (0xB46160 -> placement footprint check)", &kPtSite, 1)) return;
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
    k.ptMode = Clamp(GetPrivateProfileIntW(L"PlaceTestRO", L"Mode", 1, ini), 0, 1);
    k.ptShadow = Clamp(GetPrivateProfileIntW(L"PlaceTestRO", L"Shadow", 0, ini), 0, 1);
    k.ptReady = 1;
    Log("OK   place test RO: 0x%08lX + the S2 writer set (Mode=%ld Shadow=%ld)", PT_SITE, k.ptMode, k.ptShadow);
}

// the 60 s counter line (ts3native.cpp MaybeLogCounters)
static void PtLogCounters() {
    const PlaceTestRoCounters& k = ts3native_placetestro;
    if (!k.ptReady) return;
    Log("place test RO: mode %ld shadow %ld calls %ld, with a job %ld: answered %ld (blocked %ld, no footprint %ld; %ld ms, max %ld us), "
        "the game's call %ld ms (max %ld us); given back: state %ld, QL busy %ld, lot %ld, shape %ld, tripwire %ld, job end %ld, deep %ld, "
        "may not %ld; shadow checks %ld mismatch %ld",
        k.ptMode, k.ptShadow, k.ptCalls, k.ptJob, k.ptTaken, k.ptBlocked, k.ptEmpty, k.ptUs / 1000, k.ptMaxUs, k.ptGameUs / 1000, k.ptGameMaxUs,
        k.ptFb[PT_FB_STATE], k.ptFb[PT_FB_TRY], k.ptFb[PT_FB_LOT], k.ptFb[PT_FB_SHAPE], k.ptFb[PT_FB_TRIP], k.ptFb[PT_FB_JOBEND],
        k.ptFb[PT_FB_DEEP], k.ptFb[PT_FB_MAYNOT], k.ptShadowRuns, k.ptShadowMismatch);
}
