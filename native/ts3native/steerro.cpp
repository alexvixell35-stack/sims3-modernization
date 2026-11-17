// H6 design S2 (docs/84 §3.4, implementation §3.6): a read-only Locomotion_ObstacleCheck while an async route job runs, with
// the docs/83 worker writer detours W1-W6 under the reader/writer lock QL. Included by ts3native.cpp after discourage.cpp.
// Installed with [SteerRO] Enabled=1 (default 0; needs [RouteGate] Enforce=1 and the async path). Runtime switches (counters
// v31): steerMode (+0x10A4) 1 = while a job runs, answer from the read-only clone; steerShadow (+0x10A8) 1 = with no job
// running, run the game's call, then the clone, and count differences.
//
// Locomotion_ObstacleCheck 0x85FD10 (thiscall agent: ids, ign1, n1, ign2, n2; ret 0x14; its caller 0x860129 ignores eax)
// queries 0x82EDB0 twice for the first flagged path node and calls RouteFollow_EditPath 0x85F5A0 (-> 0x82F0B0) per flagged
// node; both queries take +0x918, so while a job runs the sim thread waits for it (P0: 22% of its waits). The clone answers
// the same ids from the same structures without +0x918 and without any write outside our stack and the caller's id vector:
//   phase 1 (no lock): 0x85AE60 per flagged node and the World_LotLookup calls (both already run concurrently with jobs);
//   phase 2 (QL shared, try only): lot table find (0x627E00, no insert), cell graph, lot transform (SSE copy of 0x62ED40 /
//   0x62EF30), the game's point / segment walks 0x663E20 / 0x663B20 with our visitor SrVisit (0x669CD0 rules, no refcounts,
//   no set), the EditPath consumer with the game's footprint slots, 0x85AAA0 and the level-0 test 0x80E690 (cache checked);
//   phase 3: the ids are appended with IdVec_PushBack 0x572400.
// Any doubt (QL busy, dirty lot index, lot not in the table, deferred quadtree flag, invalid polygon cache, unknown slot target,
// overflow, the pin counter Q+0x48 or the flag Q+0x4C changing during a walk, a sim write while the clone runs) gives up and
// calls the original (today's behaviour: it waits for the job).
// Worker side: W1-W6 (12 detours) and the gate stub of LotGraph_Build 0x63CB90 (routegate.cpp site 3) hold QL exclusive on
// the worker thread for the whole call; the frame stack and leave thunk of routegate.cpp release it (RG_REL_QL).
// Fix C-A ([FpRebuildLock], docs/84 §4.4) is the sim-thread branch of the W6a stub (CaEnter below); it installs that stub
// alone when [SteerRO] is off.
// Tripwires T4 and T-S7 (0.15.0, counters v33, docs/84 §3.6): 11 counting stubs, installed with the W stubs; a writer outside
// the "job + W1-W6 only" assumption running beside a clone makes it give up (steerFbTrip).
// 0.18.0 (counters v36, docs/84 §3.6): the tripwire sites jump through g_twGo, which points at the counting stub only while
// the worker has a job armed (TwArm, ServeAsync) and at the trampoline otherwise; per-call timers (rdtsc) for the S2 entry,
// the stubs, shadow compares and the QL try-lock.

static const DWORD STEER_LOCO = TS3A(0x0085FD10);
static const u8 kLocoPro[] = {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF0};  // push ebp; mov ebp,esp; and esp,-16 (no relative operand)
typedef void(__fastcall* LocoFn)(u8* agent, void* edx, u8* ids, const void* ign1, DWORD n1, const void* ign2, DWORD n2);
static LocoFn g_locoOrig;

// Game functions the clone calls (conventions read in the disassembly 2026-10-17; tests point them elsewhere)
typedef u8*(__cdecl* SrWorldFn)();                                                         // RoutingWorld_Get 0x8292D0
typedef void(__cdecl* SrConvFn)(const void* nodePt, void* out);                             // 0x85AE60: movaps both
typedef u8*(__fastcall* SrLotLookupFn)(void* wm, void* edx, float x, float z);              // World_LotLookup 0xC68990, ret 8
typedef void(__fastcall* SrLotFindFn)(const void* tbl, void* edx, DWORD* out, const DWORD* key);  // LotTable_Find 0x627E00, ret 8
typedef const u8*(__cdecl* SrActiveLotFn)();                                                // 0xC88FD0
typedef void(__fastcall* SrWalkPtFn)(const void* q, void* edx, void* ctx, float x, float y, float r, void* visit, void* list);  // 0x663E20, ret 0x18
typedef void(__fastcall* SrWalkSegFn)(const void* q, void* edx, void* ctx, float sx, float sy, float gx, float gy, float w,
                                      void* visit, DWORD cb, void* list);                   // 0x663B20, ret 0x24
typedef u8(__cdecl* SrIdListFn)(DWORD lo, DWORD hi, const void* list, DWORD n);              // IdList_Contains 0x85AAA0
typedef void(__fastcall* SrIdPushFn)(void* vec, void* edx, const DWORD* id);                // IdVec_PushBack 0x572400, ret 4
typedef u8(__fastcall* SrQueryFn)(u8* world, void* edx, const void* pt, float r, void* vec, DWORD mode, DWORD opt);  // 0x82EDB0, ret 0x14
typedef void(__fastcall* SrVecRelFn)(void* vec, void* edx, void* b, void* e);               // FootprintRefVec_ReleaseRange 0xAAC090, ret 8
typedef void(__cdecl* SrFreeFn)(void* p);                                                   // EA_Free 0x4E3F70
static SrWorldFn g_srWorldGet = (SrWorldFn)(uintptr_t)TS3A(0x008292D0);
static SrConvFn g_srConv = (SrConvFn)(uintptr_t)TS3A(0x0085AE60);
static SrLotLookupFn g_srLotLookup = (SrLotLookupFn)(uintptr_t)TS3A(0x00C68990);
static SrLotFindFn g_srLotFind = (SrLotFindFn)(uintptr_t)TS3A(0x00627E00);
static SrActiveLotFn g_srActiveLot = (SrActiveLotFn)(uintptr_t)TS3A(0x00C88FD0);
static SrWalkPtFn g_srWalkPt = (SrWalkPtFn)(uintptr_t)TS3A(0x00663E20);
static SrWalkSegFn g_srWalkSeg = (SrWalkSegFn)(uintptr_t)TS3A(0x00663B20);
static SrIdListFn g_srIdList = (SrIdListFn)(uintptr_t)TS3A(0x0085AAA0);
static SrIdPushFn g_srIdPush = (SrIdPushFn)(uintptr_t)TS3A(0x00572400);
static SrQueryFn g_srQuery = (SrQueryFn)(uintptr_t)TS3A(0x0082EDB0);
static SrVecRelFn g_srVecRel = (SrVecRelFn)(uintptr_t)TS3A(0x00AAC090);
static SrFreeFn g_srFree = (SrFreeFn)(uintptr_t)TS3A(0x004E3F70);
static u8* const* g_srWmSlot = (u8* const*)(uintptr_t)TS3A(0x011ECBC4);     // the world manager Locomotion passes to EditPath
static const float* g_srOne = (const float*)(uintptr_t)TS3A(0x0107A538);    // 1.0f, the w of every transformed point
static const DWORD* g_srMaskTab = (const DWORD*)(uintptr_t)TS3A(0x00FEDE48);  // CollectMasked: iterator byte -> mask bit
static const DWORD kSrVecTag = TS3A(0x01026E1C);                            // 4th word of Locomotion's footprint vector
static bool g_srCheckSlots = true;  // selftest fakes turn the slot whitelist off

// Footprint vtable slots the clone calls (thiscall); every target was read (docs/84 §3.6) and is checked per object
typedef u8(__fastcall* SlotBoolArgFn)(const u8* o, void* edx, DWORD a);                     // +0x60
typedef u8(__fastcall* SlotBoolFn)(const u8* o, void* edx);                                 // +0x28, +0xC0
typedef int(__fastcall* SlotClassFn)(const u8* o, void* edx, const void* rec, DWORD b);     // +0x8, ret 8
typedef int(__fastcall* SlotIntFn)(const u8* o, void* edx);                                 // +0xC, sub +8
typedef unsigned __int64(__fastcall* SlotIdFn)(const u8* o, void* edx);                     // +0x84 (-> +0x7C), sub +4
typedef u8(__fastcall* SlotSegFn)(const u8* o, void* edx, DWORD level, const float* a, const float* b);  // +0xE8, ret 0xC

static SRWLOCK g_ql = SRWLOCK_INIT;   // worker exclusive in W1-W6, sim shared (try only) in the clone
static volatile LONG g_qlSeq;         // exclusive holds; T2 checks it does not move under a shared hold
static volatile LONG g_steerBroken;   // sticky: a worker W frame could not be tracked, so QL did not cover that call
static __declspec(thread) LONG t_qlDepth;
static __declspec(thread) int t_srFail;   // set by QlEnter when the sim thread writes while the clone runs (t_inRo)
static __declspec(thread) bool t_inRo;

enum { SR_OK, SR_FB_STATE, SR_FB_TRY, SR_FB_DIRTY, SR_FB_LOTMISS, SR_FB_DEFERRED, SR_FB_CACHE, SR_FB_SHAPE, SR_FB_TRIP };
static const char* const kSrFbName[] = {"", "state", "QL busy", "dirty lot index", "lot not in table", "deferred quadtree flag",
                                        "polygon cache", "unknown slot/overflow/null", "tripwire"};
// Buffer limits; a call that needs more gives up (SR_FB_SHAPE). 0.21.0 doubled the 0.13.0 sizes (512 / 64 / 16 / 256): the
// live split (v39 s2Shape, s2Max*) was not known yet. All buffers are static (owner thread only) except SrCollect's local list
// on the sim stack (kSrLocal * 4 = 512 bytes); depth 32 stays (a quadtree 32 levels deep is 2^32 cells across)
static const int kSrCap = 1024;    // distinct footprints per gather (SrCtx.objs, SrOut.subs, the exclude list)
static const int kSrLocal = 128;   // class-2 footprints per cell in a segment gather, kept only with a type 0x1F one
static const int kSrNodes = 32;    // flagged path nodes after the first (nodes[] holds kSrNodes + 1)
static const int kSrIds = 512;     // ids appended per call
static const int kSrDepth = 32;    // CollectChildren recursion
// v39: which SR_FB_SHAPE return gave up (s2Shape[] index, agent.js names in the same order)
enum { SRS_OBJCAP, SRS_NULLFP, SRS_SLOT, SRS_LOCALCAP, SRS_DEPTH, SRS_NOACTLOT, SRS_LOTTBL, SRS_IDCAP, SRS_NOPLANNER, SRS_NODECAP,
       SRS_NODECAP2, SRS_N };
static __declspec(thread) int t_srShape;  // the last SrShape site on this thread; SrFallback counts it with SR_FB_SHAPE
static int SrShape(int site) {
    t_srShape = site;
    return SR_FB_SHAPE;
}

// v36 per-call timers: rdtsc ticks summed in 64 bits, published as units of 1024 ticks (`*Kt`); maxima in ticks (`*MaxT`);
// s2TscPerUs converts both (calibrated against QPC on each job arm)
static volatile LONGLONG g_s2Acc[5];  // entry, tripwire stubs, QL stubs, shadow, try-lock
enum { S2T_ENTRY, S2T_TW, S2T_QL, S2T_SHADOW, S2T_TRY };
static void S2Time(int what, unsigned __int64 t0) {
    Ts3nCounters& k = ts3native_counters;
    static LONG* const kKt[5] = {&k.s2EntryKt, &k.s2TwKt, &k.s2QlKt, &k.s2ShadowKt, &k.s2TryKt};
    static LONG* const kMax[5] = {&k.s2EntryMaxT, &k.s2TwMaxT, &k.s2QlMaxT, &k.s2ShadowMaxT, &k.s2TryMaxT};
    const LONGLONG d = (LONGLONG)(__rdtsc() - t0);
    *kKt[what] = (LONG)((InterlockedExchangeAdd64(&g_s2Acc[what], d) + d) >> 10);
    Max(kMax[what], d > 0x7FFFFFFF ? 0x7FFFFFFF : (LONG)d);
}
static unsigned __int64 g_s2Tsc0;
static LARGE_INTEGER g_s2Qpc0;
static void S2Calibrate() {  // worker, once per job arm; the first call only takes the reference
    LARGE_INTEGER q;
    QueryPerformanceCounter(&q);
    const unsigned __int64 t = __rdtsc();
    if (!g_s2Tsc0) { g_s2Tsc0 = t, g_s2Qpc0 = q; return; }
    const LONGLONG us = (q.QuadPart - g_s2Qpc0.QuadPart) * 1000000 / g_qpf.QuadPart;
    if (us > 1000000) ts3native_counters.s2TscPerUs = (LONG)((t - g_s2Tsc0) / (unsigned __int64)us);
}

static void QlAcquireEx() {
    if (t_qlDepth++) return;
    if (!TryAcquireSRWLockExclusive(&g_ql)) {
        LARGE_INTEGER t0;
        QueryPerformanceCounter(&t0);
        AcquireSRWLockExclusive(&g_ql);
        InterlockedExchangeAdd(&ts3native_counters.steerQlWaitUs, UsSince(t0));
    }
    InterlockedIncrement(&g_qlSeq);
    InterlockedIncrement(&ts3native_counters.steerQlHolds);
}
static void QlReleaseEx() {
    if (--t_qlDepth == 0) ReleaseSRWLockExclusive(&g_ql);
}

// W4 (routegate.cpp RgEnter, gate site 3) and the W stubs: the worker takes QL for the call; false = no frame to release it
// at the leave, so it is released now and the clone is switched off for good (the call runs outside QL)
static bool QlPushWorker(RgThread& t, DWORD* slot, LONG reader) {
    QlAcquireEx();
    if (RgPush(t, slot, reader, RG_REL_QL)) return true;
    InterlockedExchange(&g_steerBroken, 1);
    InterlockedIncrement(&ts3native_counters.steerUntracked);
    QlReleaseEx();
    return false;
}

// W1-W6 (docs/83 §6): all bytes read from the exe (2026-10-17), whole instructions, no relative operand
struct QlSite { DWORD addr; u8 len; u8 pro[8]; u8 cache; const char* name; };
static const int QL_N = 12;
static const QlSite kQlSites[QL_N] = {
    {TS3A(0x00665C50), 6, {0x81, 0xEC, 0x80, 0x00, 0x00, 0x00}, 0, "LotQuadTree_ApplyInsert (W1)"},          // ret 8
    {TS3A(0x00666750), 5, {0x83, 0xEC, 0x60, 0x53, 0x55}, 0, "LotQuadTree_ApplyRemove (W2)"},                // ret 8
    {TS3A(0x0062F8A0), 7, {0x8B, 0x44, 0x24, 0x08, 0x83, 0xEC, 0x14}, 0, "LotRouting_QuadTreeBuild (W3)"},   // ret 0x10
    {TS3A(0x0062E680), 8, {0x83, 0xEC, 0x08, 0x53, 0x8B, 0x5C, 0x24, 0x14}, 0, "LotTable_Insert (W5)"},      // ret 0xC
    {TS3A(0x0080E5B0), 5, {0x53, 0x8B, 0x5C, 0x24, 0x08}, 1, "Footprint_EnsureLevelPolygons (W6a)"},         // ret 4
    {TS3A(0x0080E610), 8, {0x51, 0x57, 0x8B, 0xF9, 0x83, 0x7F, 0x38, 0x00}, 1, "Footprint_EnsureAllPolygons (W6b)"},  // ret
    {TS3A(0x0080E980), 8, {0x83, 0xEC, 0x2C, 0x53, 0x8B, 0x5C, 0x24, 0x34}, 1, "footprint +0x78 rebuild (W6c)"},       // ret 0xC
    {TS3A(0x0080EC30), 6, {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF0}, 1, "footprint +0xF0 transform (W6d)"},       // ret 4
    {TS3A(0x0080E0B0), 7, {0x56, 0x8B, 0xF1, 0x83, 0x7E, 0x38, 0x00}, 1, "Footprint_InvalidatePolygons (W6e)"},  // ret
    {TS3A(0x0080F6A0), 5, {0x8B, 0x54, 0x24, 0x08, 0x53}, 1, "Footprint_SetLevelPolygons (W6f)"},            // ret 0xC
    {TS3A(0x0080CB40), 6, {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF0}, 1, "footprint +0x100 class 0xFEB848 (W6g)"},  // ret 8
    {TS3A(0x00806770), 6, {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF0}, 1, "footprint +0x100 class 0xFEB970 (W6g)"},  // ret 8
};
static void* g_qlTramp[QL_N];
static volatile LONG g_snQl[QL_N];  // [RouteSnap] site index + 1 counted from inside the W stub, 0 = none (routesnap.cpp)

// Fix C-A (docs/84 §4.4, §7 #6): Footprint_EnsureLevelPolygons 0x80E5B0 (W6a, thiscall(fp)(level), ret 4) marks an invalid
// level valid and frees its polygons before it rebuilds them through slots +0xF8/+0xFC; for these four classes the rebuild
// reaches the lot table (gate 12), so on the sim thread during a job it waits there with the level valid and empty for the
// rest of the job, which the job may read. C-A takes +0x918 in the stub, before the original body runs: the same wait, moved
// before the level is touched. Only with no QL held on this thread (lock order, §5); counted either way (T-L5).
static volatile LONG g_caOn;
static DWORD g_caClasses[4] = {TS3A(0x00FEB848), TS3A(0x00FEB970), TS3A(0x00FEBD10), TS3A(0x00FEBE38)};  // steerro-test points one at its fake vtable
static const int QL_W6A = 4;  // kQlSites index of 0x80E5B0

static void CaEnter(DWORD tid, DWORD* slot) {
    Ts3nCounters& k = ts3native_counters;
    const u8* fp = (const u8*)(uintptr_t)slot[-2];  // ecx in the stub's pushad frame
    const u8* cache = fp ? *(const u8* const*)(fp + 0x38) : NULL;
    if (!cache || cache[slot[1] * 0x24 + 0x20]) return;  // level valid: the original rebuilds nothing (its own test 0x80E5B9)
    int cls = 0;
    while (cls < 4 && g_caClasses[cls] != *(const DWORD*)fp) cls++;
    if (cls == 4) return;
    InterlockedIncrement(&k.caSimInJob[cls]);
    if (!k.caMode) return;
    RgThread& t = t_rg;
    RgDrop(t, slot);
    if (t_qlDepth) { InterlockedIncrement(&k.caQlHeld); return; }     // never wait holding QL
    if (t.n != t.readers || k.rgOwnerTid == (LONG)tid) return;          // inside a gate frame or the +0x918 owner: no wait
    void* lock = g_async.lock;
    if (!lock) return;
    if (g_inInvoke && tid == g_async.owner) { InterlockedIncrement(&k.rgBorrows); return; }  // nested in the job: borrowed
    InterlockedIncrement(&g_rgOffSimOpen);  // as a gate entry: published before the job state is read again
    LONG rel = RG_REL_OFFSIM;
    if (g_async.state == A_RUNNING) {
        static const DWORD kInfinite = 0xFFFFFFFF;
        LARGE_INTEGER t0;
        QueryPerformanceCounter(&t0);
        t_waitKey = TS3A(0x0080E5B0);  // probe P0: this wait keyed by the rebuild
        LockAcquireHook(lock, NULL, &kInfinite);
        t_waitKey = 0;
        rel |= RG_REL_LOCK;
        InterlockedIncrement(&k.caWaits);
        InterlockedExchangeAdd(&k.caWaitUs, UsSince(t0));
    }
    if (!RgPush(t, slot, 0, rel, lock)) RgUndo(RgFrame{NULL, 0, 0, rel, lock});
}

static volatile LONG g_twArmed;  // tripwire stubs armed (TwArm below); also gates the QL stub timer
static void QlEnterBody(int i, DWORD* slot);
static void __cdecl QlEnter(int i, DWORD* slot) {
    if (!g_twArmed) { QlEnterBody(i, slot); return; }
    const unsigned __int64 t0 = __rdtsc();
    QlEnterBody(i, slot);
    InterlockedIncrement(&ts3native_counters.s2QlCalls);
    S2Time(S2T_QL, t0);
}
static void QlEnterBody(int i, DWORD* slot) {
    if (g_snQl[i]) SnEnter(g_snQl[i] - 1, slot);  // P1 counters (docs/94 §5): count only, every thread
    if (t_snapRun) return;  // P4 clone run: no QL hold, no tripwire (the copy is not game data, docs/94 §12.4.3)
    const DWORD tid = GetCurrentThreadId();
    if (RgIsWorker(tid)) {
        RgThread& t = t_rg;
        RgDrop(t, slot);
        QlPushWorker(t, slot, 1);
        return;
    }
    if (i == QL_W6A && g_caOn && g_async.state == A_RUNNING && RgIsSim(tid)) CaEnter(tid, slot);
    // tripwires: the sim thread rewriting the tree, the lot table or a polygon cache while a job runs (an existing race with
    // the job's readers; inside the clone it would be ours: that answer is dropped)
    if (g_async.state == A_RUNNING && RgIsSim(tid)) {
        Ts3nCounters& k = ts3native_counters;
        InterlockedIncrement(kQlSites[i].cache ? &k.steerSimCacheWrite : &k.steerSimTreeWrite);
        if (t_inRo) { InterlockedIncrement(&k.steerSimWriteInRo); t_srFail = SR_FB_TRIP; }
    }
}

#define QL_STUB(i) static __declspec(naked) void QlStub##i() { __asm pushad __asm pushfd __asm lea eax, [esp + 36] \
    __asm push eax __asm push i __asm call QlEnter __asm add esp, 8 __asm popfd __asm popad __asm jmp dword ptr [g_qlTramp + i * 4] }
QL_STUB(0) QL_STUB(1) QL_STUB(2) QL_STUB(3) QL_STUB(4) QL_STUB(5) QL_STUB(6) QL_STUB(7) QL_STUB(8) QL_STUB(9) QL_STUB(10) QL_STUB(11)
static void (*const kQlStubs[QL_N])() = {QlStub0, QlStub1, QlStub2, QlStub3, QlStub4, QlStub5, QlStub6, QlStub7, QlStub8, QlStub9,
                                         QlStub10, QlStub11};

// Tripwires T4 and T-S7 (docs/83 §7, docs/84 §3.6 and §7.3; 0.15.0, counters v33): counting stubs on the writers that the
// clone's assumption says never run beside it. T4: the tree node and map entry helpers outside QL on any thread but the sim
// (static: only inside W1/W2). T-S7: the writers of fp+0x20 (slot +0x8C, the footprint load slot +0xA0's base), of the lock-key
// component vectors behind it, and LotGraph_Reset (static: sim thread only, docs/84 §7.3). While a job runs or a clone is open,
// a hit on another thread is counted, keeps g_twActive up for the call (released by the routegate leave thunk, RG_REL_TRIP) and
// moves g_twEpoch on the leave; a clone that sees either drops its answer and the game's call runs (steerFbTrip, steerTripDiscard).
struct TwSite { DWORD addr; u8 len; u8 pro[9]; const char* name; };
static const int TW_T4 = 4, TW_N = 11;
static const TwSite kTwSites[TW_N] = {  // bytes read from the exe (2026-10-17): whole instructions, no relative operand
    {TS3A(0x006653A0), 5, {0x83, 0xEC, 0x68, 0x53, 0x55}, "LotQuadTree node insert helper (T4)"},                       // ret 0xC
    {TS3A(0x00666010), 6, {0x81, 0xEC, 0x8C, 0x00, 0x00, 0x00}, "LotQuadTree node remove helper (T4)"},              // ret 8
    {TS3A(0x0061ED80), 6, {0x56, 0x68, 0x6C, 0xAC, 0xFE, 0x00}, "FootprintMapEntry_Alloc (T4)"},                     // ret
    {TS3A(0x0061ED10), 9, {0x56, 0x8B, 0xF1, 0xC7, 0x06, 0x7C, 0xAC, 0xFE, 0x00}, "FootprintMapEntry_DeletingDtor (T4)"},  // ret 4
    {TS3A(0x00807110), 7, {0x8B, 0x41, 0x20, 0x8B, 0x54, 0x24, 0x04}, "Footprint_SetLockKeyObject (T-S7)"},          // ret 4
    {TS3A(0x008077A0), 5, {0x51, 0x53, 0x55, 0x8B, 0xE9}, "Footprint_Load (T-S7)"},                                  // ret 0x10
    {TS3A(0x0062E420), 5, {0x83, 0xEC, 0x0C, 0x53, 0x55}, "LotGraph_Reset (T-S7)"},                                  // ret 8
    {TS3A(0x00947FA0), 6, {0x8B, 0x44, 0x24, 0x0C, 0x85, 0xC0}, "LockKeys_Set (T-S7)"},                              // ret 0xC
    {TS3A(0x00948050), 7, {0x8B, 0x44, 0x24, 0x0C, 0x83, 0xEC, 0x08}, "LockKeys_Add (T-S7)"},                        // ret 0xC
    {TS3A(0x009479D0), 6, {0x8B, 0x44, 0x24, 0x0C, 0x85, 0xC0}, "LockKeys_Remove (T-S7)"},                           // ret 0xC
    {TS3A(0x00947620), 6, {0x8B, 0x41, 0x08, 0x89, 0x41, 0x0C}, "LockKeys_Clear (T-S7)"},                            // ret
};
static void* g_twTramp[TW_N];
static volatile LONG g_twActive;   // tripwire calls running now on other threads
static volatile LONG g_twEpoch;    // moves when one of them leaves
static volatile LONG g_srInClone;  // the sim is between its QL acquire and release in SrRun
static __declspec(thread) LONG t_twDepth;
static volatile LONG g_twGen;      // moves at each disarm: a clone that sees it move dropped out of the armed window

// Arming (0.18.0). Each site's detour jumps to TwGo<i>, a single `jmp [g_twGo + i*4]` (no flag, no register touched): to
// the counting stub TwStub<i> while armed, to the trampoline otherwise. Only the route worker arms, around each async job
// (ServeAsync: armed before A_RUNNING is published, disarmed after A_IDLE), one job at a time, so no count is needed.
// Order: arm = stubs in place, then g_twArmed = 1; disarm = g_twArmed = 0, then g_twGen + 1, then the trampolines. A Mode 1
// clone (SrRun) reads g_twGen, then g_twArmed, at its start and g_twGen again at its end: armed at the start and the same
// generation at the end means the stubs counted every writer entry during the whole clone. A clone that straddles a disarm
// gives up (s2JobEndDrop). Shadow clones (no job, starts held off) do not need the stubs: their answer is never used.
static void* volatile g_twGo[TW_N];

static void TwLeave() {  // RgUndo (RG_REL_TRIP); the epoch first, so a clone that sees g_twActive drop also sees it move
    t_twDepth--;
    InterlockedIncrement(&g_twEpoch);
    InterlockedDecrement(&g_twActive);
}

static void TwEnterBody(int i, DWORD* slot);
static void __cdecl TwEnter(int i, DWORD* slot) {
    const unsigned __int64 t0 = __rdtsc();
    TwEnterBody(i, slot);
    InterlockedIncrement(&ts3native_counters.s2TwCalls);
    S2Time(S2T_TW, t0);
}
static void TwEnterBody(int i, DWORD* slot) {
    if (t_snapRun) return;  // P4 clone run: pass through
    if (t_rpUl) return;     // replay consumer outside +0x918 (docs/182): clone trees, private pools; S2 reads none of it
    Ts3nCounters& k = ts3native_counters;
    const DWORD tid = GetCurrentThreadId();
    if (RgIsSim(tid)) {  // the writers' own thread (T-S7 by design); the clone calls none of them
        if (t_inRo) { InterlockedIncrement(&k.steerSimWriteInRo); t_srFail = SR_FB_TRIP; }
        if (i >= TW_T4 && g_async.state == A_RUNNING) InterlockedIncrement(&k.steerS7Sim);
        return;
    }
    if (i < TW_T4 && t_qlDepth) return;  // inside a W1-W6 frame: QL covers it
    InterlockedIncrement(&g_twActive);   // before the window test (the clone sets g_srInClone, then reads g_twActive)
    // window: armed (from before A_RUNNING), a job runs, or a clone is open (0.15.0 had only the last two)
    if (!g_twArmed && g_async.state != A_RUNNING && !g_srInClone) { InterlockedDecrement(&g_twActive); return; }
    InterlockedIncrement(i < TW_T4 ? &k.steerT4Bare : &k.steerS7Foreign);
    InterlockedIncrement(&k.steerTwSite[i]);
    RgThread& t = t_rg;
    RgDrop(t, slot);
    if (t_twDepth) { InterlockedIncrement(&g_twEpoch); InterlockedDecrement(&g_twActive); return; }  // the outer frame covers it
    if (!RgPush(t, slot, 0, RG_REL_TRIP)) {  // no frame for the leave: the clone could miss this writer, so it stays off
        InterlockedExchange(&g_steerBroken, 1);
        InterlockedIncrement(&k.steerUntracked);
        InterlockedIncrement(&g_twEpoch);
        InterlockedDecrement(&g_twActive);
        return;
    }
    t_twDepth++;
}

#define TW_STUB(i) static __declspec(naked) void TwStub##i() { __asm pushad __asm pushfd __asm lea eax, [esp + 36] \
    __asm push eax __asm push i __asm call TwEnter __asm add esp, 8 __asm popfd __asm popad __asm jmp dword ptr [g_twTramp + i * 4] }
TW_STUB(0) TW_STUB(1) TW_STUB(2) TW_STUB(3) TW_STUB(4) TW_STUB(5) TW_STUB(6) TW_STUB(7) TW_STUB(8) TW_STUB(9) TW_STUB(10)
static void (*const kTwStubs[TW_N])() = {TwStub0, TwStub1, TwStub2, TwStub3, TwStub4, TwStub5, TwStub6, TwStub7, TwStub8, TwStub9,
                                         TwStub10};
#define TW_GO(i) static __declspec(naked) void TwGo##i() { __asm jmp dword ptr [g_twGo + i * 4] }
TW_GO(0) TW_GO(1) TW_GO(2) TW_GO(3) TW_GO(4) TW_GO(5) TW_GO(6) TW_GO(7) TW_GO(8) TW_GO(9) TW_GO(10)
static void (*const kTwGo[TW_N])() = {TwGo0, TwGo1, TwGo2, TwGo3, TwGo4, TwGo5, TwGo6, TwGo7, TwGo8, TwGo9, TwGo10};

// Worker only (one job at a time; steerro-test and selftest call it from their "job"). Aligned pointer stores are atomic;
// the interlocked operations after them order them before the flag (arm) and the flag before them (disarm).
static void TwArm(bool on) {
    if (on) {
        S2Calibrate();
        for (int i = 0; i < TW_N; i++) if (g_twTramp[i]) g_twGo[i] = (void*)kTwStubs[i];
        InterlockedExchange(&g_twArmed, 1);
        InterlockedIncrement(&ts3native_counters.s2ArmJobs);
        return;
    }
    InterlockedExchange(&g_twArmed, 0);
    InterlockedIncrement(&g_twGen);
    for (int i = 0; i < TW_N; i++) if (g_twTramp[i]) g_twGo[i] = g_twTramp[i];
}

// all eleven or none (a missing one would leave the clone blind to that writer). The detour targets TwGo<i>; until the
// trampoline exists g_twGo[i] is the counting stub (as 0.15.0, which jumps through g_twTramp), then the trampoline unless armed.
static int InstallTripwires() {
    for (int i = 0; i < TW_N; i++)
        if (!Matches((const void*)(uintptr_t)kTwSites[i].addr, kTwSites[i].pro, kTwSites[i].len)) return 0;
    int n = 0;
    for (int i = 0; i < TW_N; i++) {
        g_twGo[i] = (void*)kTwStubs[i];
        n += (g_twTramp[i] = InstallDetour(kTwSites[i].name, kTwSites[i].addr, kTwSites[i].pro, kTwSites[i].len, (void*)kTwGo[i], &g_twTramp[i])) != NULL;
        if (g_twTramp[i] && !g_twArmed) g_twGo[i] = g_twTramp[i];
    }
    ts3native_counters.steerTwStubs = n;
    return n;
}

// --- the clone ---
// 0x85AE60's output (0x34 bytes, 16-aligned, as Locomotion's and EditPath's stack copies): xyz w, a zero vector, 1.0, level
struct alignas(16) SrPt { float v[4]; float zero[4]; float one; LONG level; DWORD tail[6]; };
struct SrCtx { const u8* rec; DWORD flag; int n; int fail; int over; const u8* objs[kSrCap]; };  // over: adds past kSrCap

static bool SrIn(DWORD v, const DWORD* s, int n) {
    for (int i = 0; i < n; i++) if (s[i] == v) return true;
    return false;
}

// Slot targets of the 9 footprint classes and the middle table (docs/83 §8, re-read 2026-10-17); anything else is a doubt
static bool SrParentOk(const u8* obj) {
    static const DWORD k8[] = {TS3A(0x0080F840), TS3A(0x0080C300), TS3A(0x00807090), TS3A(0x00807B70), TS3A(0x008066C0), TS3A(0x006119F0)};
    const DWORD* vt = *(const DWORD* const*)obj;
    const DWORD* sv = *(const DWORD* const*)(obj + 4);
    if (!vt || !sv) return false;
    return vt[0x60 / 4] == TS3A(0x009C1B00) && (vt[0x28 / 4] == TS3A(0x00620D60) || vt[0x28 / 4] == TS3A(0x00620DD0)) && SrIn(vt[2], k8, 6) &&
           (vt[3] == TS3A(0x00624450) || vt[3] == TS3A(0x0070AE20)) && vt[0x84 / 4] == TS3A(0x00620CC0) && vt[0x7C / 4] == TS3A(0x00806FE0) &&
           (vt[0xC0 / 4] == TS3A(0x00620D60) ||
            (vt[0xC0 / 4] == TS3A(0x007EAEA0) && vt[0xE8 / 4] == TS3A(0x0080E690) && (vt[0] == TS3A(0x00619EF0) || vt[0] == TS3A(0x0083F5F0)))) &&
           sv[1] == TS3A(0x00620E60) && (sv[2] == TS3A(0x00624ED0) || sv[2] == TS3A(0x00620E50));
}
// 0.37 (docs/84 §3.6, bp-s2cov-shadow: every one of the 31% shape fallbacks was SRS_SLOT): the child footprint 0xFEBF90
// ("Routing/ExtensionFootprint", 0x28 bytes, docs/94 §21; the only live class of replay.cpp's 11 that the list above
// rejects). Its slots forward to the parent at +0xC or read its own +0x18/+0x1C mask and +0x20 byte, all pure [verified
// disassembly 2026-10-21]: +0 0x626210 parent slot 0; +8 0x806BF0 class (+0x20 set: the parent's +8 with the same
// arguments, else 2 or 0 from the mask); +0xC 0x626220 parent type; +0x28 0x620D60 false; +0x60 0x806F00 mask vs
// 0x624180(arg) (bit arithmetic); +0x84 0x6262D0 parent +0x7C id; +0xB8 0xBFB320 false; +0xC0 0x620D60 false; interface
// 0xFEBF6C: +4 0x6262E0 = ~(parent id), +8 0x626300 parent type. So the child is fine when its parent is one of the above.
static bool SrSlotsOk(const u8* obj) {
    if (!g_srCheckSlots) return true;
    const DWORD* vt = *(const DWORD* const*)obj;
    const DWORD* sv = *(const DWORD* const*)(obj + 4);
    if (!vt || !sv) return false;
    if (vt[0x60 / 4] != TS3A(0x00806F00)) return SrParentOk(obj);
    const u8* parent = *(const u8* const*)(obj + 0xC);  // the game forwards without a NULL check (it would fault)
    return vt[0] == TS3A(0x00626210) && vt[2] == TS3A(0x00806BF0) && vt[3] == TS3A(0x00626220) && vt[0x28 / 4] == TS3A(0x00620D60) &&
           vt[0x84 / 4] == TS3A(0x006262D0) && vt[0xB8 / 4] == TS3A(0x00BFB320) && vt[0xC0 / 4] == TS3A(0x00620D60) && sv[1] == TS3A(0x006262E0) &&
           sv[2] == TS3A(0x00626300) && parent && SrParentOk(parent);
}

static void SrAdd(SrCtx* c, const u8* obj) {
    for (int i = 0; i < c->n; i++) if (c->objs[i] == obj) return;
    if (c->n == kSrCap) { c->over++; return; }  // counted on (not deduplicated: an upper bound); SrWalk gives up after the walk
    c->objs[c->n++] = obj;
}

// CollectLeaf 0x669850 (masked = 0, its args 4/5 are 1/0 from the visitor) and CollectMasked 0x669230 for this context:
// options object 0 (0x8294E0 returns record+0x28 = 0, or 0 for no record), so the id list and RoutingOptions_Test are not
// reached; ctx+0x50 = 0, so the type 0x13/0x18 and 0x1E branches are not either.
// flag bit 2 ([RouteToObjRO], BlockerCheck2d's record): record+0x28 is an options object, so both collectors first drop a
// footprint whose id is in its lists (0x81C970, 0x669915 / 0x669332) and keep a type-0x14 footprint's class when
// RoutingOptions_Test(0x1004, 0, 0) holds (0x669A2F / 0x669437) [verified disassembly 2026-10-22]
typedef u8(__fastcall* SrOptIdFn)(const u8* opts, void* edx, DWORD lo, DWORD hi);           // 0x81C970, ret 8
typedef u8(__fastcall* SrOptTestFn)(const u8* opts, void* edx, DWORD a, DWORD b, DWORD c);  // 0x81C880, ret 0xC
static SrOptIdFn g_srOptId = (SrOptIdFn)(uintptr_t)TS3A(0x0081C970);
static SrOptTestFn g_srOptTest = (SrOptTestFn)(uintptr_t)TS3A(0x0081C880);
static void SrCollect(SrCtx* c, const u8* node, bool masked, DWORD bit) {
    const u8* l = node + 8;
    const bool heap = (l[0] & 1) != 0;
    const DWORD cnt = heap ? *(const DWORD*)(l + 4) : 1;
    if (heap && !cnt) return;
    const u8* e = heap ? (const u8*)(uintptr_t)(*(const DWORD*)l & ~1u) : l;
    const u8* end = e + cnt * 12;
    const u8* local[kSrLocal];
    int nl = 0;
    bool seen = false;
    const DWORD ow = c->rec ? *(const DWORD*)c->rec : 0;
    const u8* opts = (c->flag & 2) && c->rec ? *(const u8* const*)(c->rec + 0x28) : NULL;
    for (; e != end; e += 12) {
        const u8* ent = *(const u8* const*)e;
        if (!ent) continue;
        if (masked && !(bit < 32 ? *(const DWORD*)(e + 4) & (1u << (bit & 31)) : *(const DWORD*)(e + 8) & (1u << ((bit - 32) & 31))))
            continue;
        const u8* obj = *(const u8* const*)(ent + 8);
        if (!obj) { c->fail = SrShape(SRS_NULLFP); return; }  // the original dereferences a NULL footprint
        if (!SrSlotsOk(obj)) { c->fail = SrShape(SRS_SLOT); return; }
        const void* const* vt = *(const void* const* const*)obj;
        if (opts) {
            const unsigned __int64 id = ((SlotIdFn)vt[0x84 / 4])(obj, NULL);
            if (g_srOptId(opts, NULL, (DWORD)id, (DWORD)(id >> 32))) continue;
        }
        if (!((SlotBoolArgFn)vt[0x60 / 4])(obj, NULL, ow)) continue;
        if (((SlotBoolFn)vt[0x28 / 4])(obj, NULL)) continue;
        int cls = ((SlotClassFn)vt[2])(obj, NULL, c->rec, ent[0x10]);
        if (c->rec && ((SlotIdFn)vt[0x84 / 4])(obj, NULL) == *(const unsigned __int64*)(c->rec + 8)) continue;
        const int t = ((SlotIntFn)vt[3])(obj, NULL);
        if (t == 0x1F) { seen = true; continue; }
        if (t == 0x14 && !(opts && g_srOptTest(opts, NULL, 0x1004, 0, 0))) cls = 1;
        if (c->flag) {
            if (cls == 2) {
                if (nl < kSrLocal) local[nl] = obj;
                nl++;  // past kSrLocal: counted on, matters only with a type 0x1F entry (below)
                continue;
            }
            if ((DWORD)cls < 2 || cls == 0xD) continue;
        }
        SrAdd(c, obj);
    }
    if (c->flag && seen) {
        Max(&ts3native_counters.s2MaxLocal, nl);
        if (nl > kSrLocal) { c->fail = SrShape(SRS_LOCALCAP); return; }
        for (int i = 0; i < nl; i++) SrAdd(c, local[i]);
    }
}

static void SrChildren(SrCtx* c, const u8* node, int depth) {  // CollectChildren 0x669B90 / CollectSubtree 0x669B30
    Max(&ts3native_counters.s2MaxDepth, depth);
    if (depth > kSrDepth) { c->fail = SrShape(SRS_DEPTH); return; }
    for (int i = 0; i < 4 && !c->fail; i++) {
        const u8* ch = *(const u8* const*)(node + 0xC + i * 4);
        if (!ch) continue;
        if (!(ch[4] & 3)) SrCollect(c, ch, false, 0);
        else SrChildren(c, ch, depth + 1);
    }
}

// The walks' visitor (in place of 0x662DB0 / 0x662DD0 -> LotQuadTree_FootprintVisitor 0x669CD0): stdcall (ctx, cell iterator,
// list), ecx = Q unused, returns 1 to go on. A failed clone stops the walk (its answer is dropped).
static long g_srVisitKind[3];  // masked, leaf, children (owner thread; steerro-test reports its coverage)
static u8 __stdcall SrVisit(SrCtx* c, const u8* iter, void*) {
    if (c->fail) return 0;
    const u8* node = *(const u8* const*)(iter + 0xC);
    if (!node) return 1;
    const DWORD b = iter[0x1C];
    const int kind = b ? 0 : !(node[4] & 3) ? 1 : 2;
    g_srVisitKind[kind]++;
    if (kind == 0) SrCollect(c, node, true, g_srMaskTab[b]);
    else if (kind == 1) SrCollect(c, node, false, 0);
    else SrChildren(c, node, 0);
    return c->fail ? 0 : 1;
}

// LotTable_Find 0x627E00 on P+0x48. contains = LotTable_Contains 0x628A90 rules (key 0 / -1 / active lot: no entry, key
// re-checked at node+0x10); else LotTable_GetOrInsert 0x62EAB0 (a miss would insert: a doubt)
static int SrEntry(const u8* p, DWORD lo, DWORD hi, bool contains, const u8** entry) {
    *entry = NULL;
    if (contains) {
        if (!(lo | hi) || (lo & hi) == 0xFFFFFFFF) return SR_OK;
        const u8* act = g_srActiveLot();
        if (!act) return SrShape(SRS_NOACTLOT);  // the original reads [0 + 0x48]
        if (lo == *(const DWORD*)(act + 0x48) && hi == *(const DWORD*)(act + 0x4C)) return SR_OK;
    }
    const u8* tbl = p + 0x48;
    const DWORD cnt = *(const DWORD*)(tbl + 8);
    const DWORD* bk = *(const DWORD* const*)(tbl + 4);
    if (!cnt || !bk) return SrShape(SRS_LOTTBL);
    DWORD out[2];
    const DWORD key[2] = {lo, hi};
    g_srLotFind(tbl, NULL, out, key);
    if (out[0] == bk[cnt]) return contains ? SR_OK : SR_FB_LOTMISS;
    const u8* node = (const u8*)(uintptr_t)out[0];
    if (contains && (*(const DWORD*)(node + 0x10) != lo || *(const DWORD*)(node + 0x14) != hi)) return SR_OK;
    *entry = node + 0x10;
    return SR_OK;
}

static const u8* SrCell(const u8* entry, LONG level) {  // LotTable_GetCellGraph 0x62FB10 after the hit
    const LONG i = level - *(const LONG*)(entry + 0x40);
    if (i < 0) return NULL;
    const DWORD* b = *(const DWORD* const*)(entry + 0x74);
    const DWORD* e = *(const DWORD* const*)(entry + 0x78);
    if (i >= (LONG)(e - b)) return NULL;
    return (const u8*)(uintptr_t)b[i];
}

// 0x62ED40 / 0x62EF30 after the hit: {x, 0, z, 1} times the rows at entry+0xA0/+0xB0/+0xC0/+0xD0, same SSE operations in the
// same order (bit-exact, steerro-test); u = x', v = z'
static void SrXform(const u8* entry, float x, float z, float* u, float* v) {
    const __m128 p = _mm_set_ps(*g_srOne, z, 0.0f, x);
    __m128 r = _mm_add_ps(_mm_mul_ps(_mm_shuffle_ps(p, p, 0x55), _mm_load_ps((const float*)(entry + 0xB0))),
                          _mm_mul_ps(_mm_shuffle_ps(p, p, 0x00), _mm_load_ps((const float*)(entry + 0xA0))));
    r = _mm_add_ps(r, _mm_mul_ps(_mm_shuffle_ps(p, p, 0xAA), _mm_load_ps((const float*)(entry + 0xC0))));
    r = _mm_add_ps(r, _mm_mul_ps(_mm_shuffle_ps(p, p, 0xFF), _mm_load_ps((const float*)(entry + 0xD0))));
    __declspec(align(16)) float o[4];
    _mm_store_ps(o, r);
    *u = o[0];
    *v = o[2];
}

// One walk under QL. The original flushes first when Q+0x48 < 1 and the deferred flag is set (a write): a doubt. Pin counter
// and flag must not move while we walk (tripwire: a job query or pin on this tree in between).
// x1, y1: the segment's far end ([RouteToObjRO]'s BlockerCheck2d gather 0x631E20 -> 0x667400); S2 walks start == goal
static int SrWalk2(SrCtx* c, const u8* q, bool point, float x, float y, float x1, float y1, float r) {
    const LONG q48 = *(volatile const LONG*)(q + 0x48);
    const u8 q4c = *(volatile const u8*)(q + 0x4C);
    if (q4c && q48 < 1) return SR_FB_DEFERRED;
    if (point) g_srWalkPt(q, NULL, c, x, y, r, (void*)SrVisit, NULL);
    else g_srWalkSeg(q, NULL, c, x, y, x1, y1, r, (void*)SrVisit, 0, NULL);  // locomotion: start == goal, w = 0
    if (*(volatile const LONG*)(q + 0x48) != q48 || *(volatile const u8*)(q + 0x4C) != q4c) {
        InterlockedIncrement(&ts3native_counters.steerPinMoved);
        return SR_FB_TRIP;
    }
    Max(&ts3native_counters.s2MaxObjs, c->n + c->over);
    if (!c->fail && c->over) return SrShape(SRS_OBJCAP);
    return c->fail;
}
static int SrWalk(SrCtx* c, const u8* q, bool point, float x, float y, float r) { return SrWalk2(c, q, point, x, y, x, y, r); }

static int SrCmpPtr(const void* a, const void* b) {
    const uintptr_t x = *(const uintptr_t*)a, y = *(const uintptr_t*)b;
    return x < y ? -1 : x > y;
}

// Footprints of lot (lo, hi) near pt as 0x631F10 / 0x631E20 collect them (point: radius r, record rec, mode 0; segment:
// NULL context, mode 1), sorted by sub-object pointer like 0x82EDB0 / 0x82F0B0 copy them out. mode1: a point query with
// 0x82EDB0's mode 1 ([AvoidRO], RouteFollow_A / _B): QueryPoint 0x667480 then picks visitor 0x662DD0 (0x669CD0 flag 1)
// instead of 0x662DB0 (flag 0), nothing else changes [verified 0x667493..0x6674A4]. flagOr 2 ([PlacementRO], docs/174): the
// record's +0x28 options object applies (SrCollect's bit 2)
static int SrQuery(SrCtx* c, const u8* p, DWORD lo, DWORD hi, const SrPt& pt, bool point, float r, const u8* rec, bool mode1 = false,
                   DWORD flagOr = 0) {
    c->n = 0;
    c->fail = SR_OK;
    c->over = 0;
    c->rec = point ? rec : NULL;
    c->flag = (point && !mode1 ? 0 : 1) | flagOr;
    const u8* entry;
    int why = SrEntry(p, lo, hi, true, &entry);
    if (why || !entry) return why;
    const u8* q = SrCell(entry, pt.level);
    if (!q) return SR_OK;
    float u, v;
    SrXform(entry, pt.v[0], pt.v[2], &u, &v);
    if ((why = SrWalk(c, q, point, u, v, r)) != SR_OK) return why;
    qsort(c->objs, c->n, sizeof(c->objs[0]), SrCmpPtr);  // obj+4 order = obj order
    return SR_OK;
}

struct SrNode { SrPt conv; const u8* raw; bool gLot, eLot; DWORD gLo, gHi, eLo, eHi, pad[2]; };  // 16-byte multiple
struct SrOut { int nIds; DWORD ids[kSrIds][2]; int nSubs; const u8* subs[kSrCap]; };

static bool SrHasId(const DWORD* b, int n, DWORD lo, DWORD hi) {
    for (int i = 0; i < n; i++) if (b[i * 2] == lo && b[i * 2 + 1] == hi) return true;
    return false;
}

// RouteFollow_EditPath 0x85F5A0 after its gather, for start == goal (docs/83 §5): ids of the gathered footprints that pass
// its type, ignore-list, ids and exclude tests, and its level-0 segment test 0x80E690 (cache valid, unchanged)
static int SrConsume(SrCtx* c, const u8* p, const SrNode& nd, const DWORD* ids0, int n0, SrOut* out, const DWORD* excl, int nExcl,
                     const void* ign1, DWORD n1, const void* ign2, DWORD n2) {
    __declspec(align(16)) float a[4], b[4];
    const u8* entryE = NULL;
    if (nd.eLot) {
        const int why = SrEntry(p, nd.eLo, nd.eHi, false, &entryE);
        if (why) return why;
        float u, v;
        SrXform(entryE, ((const float*)nd.raw)[0], ((const float*)nd.raw)[2], &u, &v);
        a[0] = a[2] = a[3] = u;  // {u, v, u, u} for start and goal (the same point here)
        a[1] = v;
        memcpy(b, a, sizeof(b));
    }
    int over = 0;  // ids past kSrIds (counted on for s2MaxIds; not deduplicated among themselves)
    for (int i = 0; i < c->n; i++) {
        const u8* obj = c->objs[i];
        const u8* sub = obj + 4;
        const void* const* svt = *(const void* const* const*)sub;
        const unsigned __int64 id = ((SlotIdFn)svt[1])(sub, NULL);
        const DWORD lo = (DWORD)id, hi = (DWORD)(id >> 32);
        const int t = ((SlotIntFn)svt[2])(sub, NULL);
        if (t == 0x14 || t == 9 || t == 0x11 || t == 0xC || t == 0xD || t == 0x17) continue;
        if (g_srIdList(lo, hi, ign1, n1) || g_srIdList(lo, hi, ign2, n2)) continue;
        if (SrHasId(ids0, n0, lo, hi) || SrHasId(&out->ids[0][0], out->nIds, lo, hi)) continue;  // already in the caller's ids
        if (SrHasId(excl, nExcl, lo, hi)) continue;                                             // an obstacle-query footprint
        const void* const* vt = *(const void* const* const*)obj;
        if (((SlotBoolFn)vt[0xC0 / 4])(obj, NULL) && entryE) {
            const u8* cache = *(const u8* const*)(obj + 0x38);
            if (!cache || !cache[0x20]) return SR_FB_CACHE;  // the original would rebuild it (0x80E5B0)
            u8 before[0x24];
            memcpy(before, cache, sizeof(before));
            const u8 hit = ((SlotSegFn)vt[0xE8 / 4])(obj, NULL, 0, a, b);
            if (memcmp(before, cache, sizeof(before)) != 0) return SR_FB_CACHE;  // T3: a writer outside W1-W6
            if (!hit) continue;
        }
        if (out->nIds == kSrIds) { over++; continue; }
        out->ids[out->nIds][0] = lo;
        out->ids[out->nIds][1] = hi;
        out->nIds++;
    }
    Max(&ts3native_counters.s2MaxIds, out->nIds + over);
    return over ? SrShape(SRS_IDCAP) : SR_OK;
}

static u8* SrFirstFlagged(u8* agent) {
    u8* head = agent + 8;
    for (u8* n = *(u8**)head; n != head; n = *(u8**)n)
        if (n[0x30]) return n;
    return NULL;
}

// The clone: ids that Locomotion_ObstacleCheck would append, in order, into out (ids0/n0 = the caller's ids before the call).
// Caller: owner thread, preconditions checked. QL is taken here (try only) for phase 2.
// job = the answer will be used (Mode 1): the tripwire stubs must be armed for the whole clone.
static int g_srPhase;  // owner thread: 0 = the clone failed before or in the steering query (key 0x85FE10), 1 = in the EditPath gathers (0x85F6CB)
static int SrRun(u8* agent, const DWORD* ids0, int n0, const void* ign1, DWORD n1, const void* ign2, DWORD n2, SrOut* out, SrCtx* c,
                 bool job = false) {
    out->nIds = out->nSubs = 0;
    g_srPhase = 0;
    u8* world = g_srWorldGet();
    u8* wm = *g_srWmSlot;
    if (!world || !wm) return SR_FB_STATE;
    const u8* p = world[0x974] ? *(const u8* const*)(world + 0x95C) : NULL;  // RoutingWorld_GetLotPlanner 0x829BC0
    u8* wmw = *(u8* const*)(world + 0x964);
    if (!p || !wmw) return SrShape(SRS_NOPLANNER);
    if (wmw[0x1CC] || wm[0x1CC]) return SR_FB_DIRTY;  // World_LotLookup would rebuild the lot index (wm+0x228)
    // phase 1, no lock: 0x85AE60 and the lot lookups, as the originals run them before their lock. nodes[0] = the first
    // flagged node (both 0x82EDB0 calls); nodes[1..] = the flagged nodes from the second node on (the EditPath loop starts
    // at [[agent+8]], 0x85FE81: the path's first node is never edited)
    static SrNode nodes[kSrNodes + 1];  // owner thread only
    int nn = 0;
    u8* head = agent + 8;
    for (u8* n = *(u8**)head; n != head; n = *(u8**)n) {
        if (!n[0x30]) continue;
        if (nn == kSrNodes + 1) {  // the flagged nodes left, for s2MaxNodes
            for (; n != head; n = *(u8**)n) nn += n[0x30] != 0;
            Max(&ts3native_counters.s2MaxNodes, nn);
            return SrShape(SRS_NODECAP);
        }
        SrNode& nd = nodes[nn++];
        nd.raw = n + 0x10;
        memset(&nd.conv, 0, sizeof(nd.conv));
        nd.conv.one = *g_srOne;
        g_srConv(nd.raw, &nd.conv);
        const u8* lot = g_srLotLookup(wmw, NULL, nd.conv.v[0], nd.conv.v[2]);  // 0x82EDB0 / 0x82F0B0 (goal == start)
        nd.gLot = lot != NULL;
        nd.gLo = lot ? *(const DWORD*)(lot + 0x48) : 0;
        nd.gHi = lot ? *(const DWORD*)(lot + 0x4C) : 0;
        const float* raw = (const float*)nd.raw;
        lot = g_srLotLookup(wm, NULL, raw[0], raw[2]);  // EditPath's own lookup on the raw node point
        nd.eLot = lot != NULL;
        nd.eLo = lot ? *(const DWORD*)(lot + 0x48) : 0;
        nd.eHi = lot ? *(const DWORD*)(lot + 0x4C) : 0;
        if (nn == 1 && n != *(u8**)head) {  // the first flagged node is also an EditPath node: second entry, same point
            if (nn == kSrNodes + 1) return SrShape(SRS_NODECAP2);
            nodes[nn++] = nd;
        }
    }
    if (!nn) return SR_FB_STATE;
    Max(&ts3native_counters.s2MaxNodes, nn);
    // phase 2, QL shared
    const unsigned __int64 tTry = __rdtsc();
    const bool got = TryAcquireSRWLockShared(&g_ql) != 0;
    InterlockedIncrement(&ts3native_counters.s2TryCalls);
    S2Time(S2T_TRY, tTry);
    if (!got) return SR_FB_TRY;
    if (g_steerBroken) { ReleaseSRWLockShared(&g_ql); return SR_FB_STATE; }
    const LONG seq = g_qlSeq;
    InterlockedExchange(&g_srInClone, 1);  // T4 / T-S7 writers on other threads count from here (then g_twActive is read)
    const LONG twEpoch = g_twEpoch;
    const LONG twGen = g_twGen;  // then g_twArmed (TwArm's disarm order is the reverse)
    bool jobEnd = job && !g_twArmed;
    t_inRo = true;
    t_qlShared = 1;  // T-L2
    t_srFail = SR_OK;
    // the obstacle query (both of Locomotion's 0x82EDB0 calls: identical inputs): option record as 0x82EDB0 builds it
    __declspec(align(16)) u8 rec[0x30] = {};
    *(DWORD*)rec = *(const DWORD*)(agent + 0xB8);
    rec[0x24] = 1;
    static DWORD excl[kSrCap][2];
    int nExcl = 0;
    bool tw = g_twActive != 0;  // a tripwire writer is running: no answer from this clone
    int why = jobEnd ? SR_FB_STATE : tw ? SR_FB_TRIP : SR_OK;
    if (!why && nodes[0].gLot) why = SrQuery(c, p, nodes[0].gLo, nodes[0].gHi, nodes[0].conv, true, *(const float*)(agent + 0x40), rec);
    else c->n = 0;
    for (int i = 0; !why && i < c->n; i++) {
        const u8* sub = c->objs[i] + 4;
        const unsigned __int64 id = ((SlotIdFn)(*(const void* const* const*)sub)[1])(sub, NULL);
        excl[nExcl][0] = (DWORD)id;
        excl[nExcl][1] = (DWORD)(id >> 32);
        nExcl++;
        out->subs[out->nSubs++] = sub;
    }
    for (int i = 1; !why && i < nn; i++) {
        if (!nodes[i].gLot) continue;  // 0x82F0B0 returned 0: EditPath appends nothing
        g_srPhase = 1;
        if ((why = SrQuery(c, p, nodes[i].gLo, nodes[i].gHi, nodes[i].conv, false, 0.0f, NULL)) != SR_OK) break;
        why = SrConsume(c, p, nodes[i], ids0, n0, out, &excl[0][0], nExcl, ign1, n1, ign2, n2);
    }
    if (!why && t_srFail) why = t_srFail;
    if (!why && g_qlSeq != seq) { InterlockedIncrement(&ts3native_counters.steerSeqSkew); why = SR_FB_TRIP; }
    if (!why && (g_twActive || g_twEpoch != twEpoch)) { tw = true; why = SR_FB_TRIP; }  // one ran (or runs) beside the clone
    if (!why && job && g_twGen != twGen) { jobEnd = true; why = SR_FB_STATE; }  // disarmed during the clone: writers unseen
    if (tw) InterlockedIncrement(&ts3native_counters.steerTripDiscard);
    if (jobEnd) InterlockedIncrement(&ts3native_counters.s2JobEndDrop);
    t_inRo = false;
    t_qlShared = 0;
    ReleaseSRWLockShared(&g_ql);
    InterlockedExchange(&g_srInClone, 0);
    return why;
}

static volatile LONG g_srNoted;
static void SrNote(const char* what, const u8* agent, int a, int b) {
    if (InterlockedIncrement(&g_srNoted) <= 16) Log("steer RO: %s (agent %p, %d / %d)", what, agent, a, b);
}

static void SrFallback(int why) {
    if (why > 0 && why <= 8) InterlockedIncrement(&ts3native_counters.steerFb[why - 1]);
    if (why == SR_FB_SHAPE) InterlockedIncrement(&ts3native_counters.s2Shape[t_srShape]);
}

// Shadow (no job running, job starts held off): the game's call, then the clone on the same world; then the obstacle query
// itself (0x82EDB0 into our own vector) against the clone's point query
static void SrShadow(u8* agent, u8* ids, const void* ign1, DWORD n1, const void* ign2, DWORD n2, SrOut* out, SrCtx* c) {
    Ts3nCounters& k = ts3native_counters;
    const int n0 = (int)((*(DWORD**)(ids + 4) - *(DWORD**)ids) / 2);
    g_locoOrig(agent, NULL, ids, ign1, n1, ign2, n2);
    struct Timer { unsigned __int64 t0 = __rdtsc(); ~Timer() { S2Time(S2T_SHADOW, t0); S2Time(S2T_ENTRY, t0); } } timer;  // ours only
    const int why = SrRun(agent, *(const DWORD* const*)ids, n0, ign1, n1, ign2, n2, out, c);
    InterlockedIncrement(&k.steerShadowRuns);
    if (why) { SrFallback(why); return; }
    const DWORD* got = *(const DWORD* const*)ids + n0 * 2;
    const int added = (int)((*(DWORD**)(ids + 4) - *(DWORD**)ids) / 2) - n0;
    if (added != out->nIds || memcmp(got, out->ids, (size_t)added * 8) != 0) {
        bool same = added == out->nIds;  // same ids in another order?
        for (int i = 0; same && i < added; i++) same = SrHasId(&out->ids[0][0], out->nIds, got[i * 2], got[i * 2 + 1]);
        InterlockedIncrement(same ? &k.steerShadowOrder : &k.steerShadowMismatch);
        SrNote(same ? "shadow: same ids, other order" : "shadow: appended ids differ", agent, added, out->nIds);
    }
    // second level: 0x82EDB0 itself, as Locomotion calls it (same point, radius, mode 0, option word), vs the clone's list
    u8* first = SrFirstFlagged(agent);
    u8* world = g_srWorldGet();
    if (!first || !world) return;
    SrPt pt;
    memset(&pt, 0, sizeof(pt));
    pt.one = *g_srOne;
    g_srConv(first + 0x10, &pt);
    DWORD vec[4] = {0, 0, 0, kSrVecTag};
    g_srQuery(world, NULL, &pt, *(const float*)(agent + 0x40), vec, 0, *(const DWORD*)(agent + 0xB8));
    const u8* const* b = (const u8* const*)(uintptr_t)vec[0];
    const int n = (int)((vec[1] - vec[0]) / 4);
    if (n != out->nSubs || (n && memcmp(b, out->subs, (size_t)n * 4) != 0)) {
        InterlockedIncrement(&k.steerShadowQueryMismatch);
        SrNote("shadow: obstacle query differs", agent, n, out->nSubs);
    }
    g_srVecRel(vec, NULL, (void*)(uintptr_t)vec[0], (void*)(uintptr_t)vec[1]);
    if (vec[0]) g_srFree((void*)(uintptr_t)vec[0]);
}

// [SteerRO] Retry=1 (docs/250 §8, switch steerretry, counters v175): a clone that met QL busy (a W1-W6 writer's short exclusive
// hold) or a tripwire reruns from S2's protocol start (g_qlSeq, g_twEpoch, g_twGen read again by run), after SwitchToThread, at
// most 4 times within ~1 ms of QPC and only while the job runs; then the game's call as before. A rerun answer is as exact as
// a first one. State fallbacks are not retried. Shared shape: run() is the caller's clone (gvro.cpp / idlero.cpp may use it).
template <class F> static int SrRetry(int why, F run) {
    Ts3nCounters& k = ts3native_counters;
    const int site = g_srPhase ? 1 : 0;
    InterlockedIncrement(&k.srrN[site]);
    LARGE_INTEGER t0;
    QueryPerformanceCounter(&t0);
    for (int i = 0; i < 4 && (why == SR_FB_TRY || why == SR_FB_TRIP) && g_async.state == A_RUNNING && UsSince(t0) < 1000; i++) {
        SwitchToThread();
        why = run();
    }
    const LONG us = UsSince(t0);
    InterlockedExchangeAdd(&k.srrUs, us);
    Max(&k.srrMaxUs, us);
    InterlockedIncrement(why ? &k.srrFb[site] : &k.srrOk[site]);
    return why;
}
static int SrWaitSite(DWORD key) { return key == TS3A(0x0085FE10) ? 0 : key == TS3A(0x0085F6CB) ? 1 : -1; }

static void __fastcall LocoHook(u8* agent, void* edx, u8* ids, const void* ign1, DWORD n1, const void* ign2, DWORD n2) {
    Ts3nCounters& k = ts3native_counters;
    const DWORD tid = GetCurrentThreadId();
    // no switch, not the job owner, or no flagged node (then the original touches no lock): the game's call
    if ((!k.steerMode && !k.steerShadow) || tid != g_async.owner || !SrFirstFlagged(agent)) {
        g_locoOrig(agent, edx, ids, ign1, n1, ign2, n2);
        return;
    }
    static SrOut out;  // owner thread only
    static SrCtx ctx;
    InterlockedIncrement(&k.steerCalls);
    if (g_async.state != A_RUNNING) {
        if (!k.steerShadow) { g_locoOrig(agent, edx, ids, ign1, n1, ign2, n2); return; }
        InterlockedIncrement(&g_rgOffSimOpen);  // no job starts while the two answers are compared (worker Dekker order)
        if (g_async.state == A_RUNNING) g_locoOrig(agent, edx, ids, ign1, n1, ign2, n2);
        else SrShadow(agent, ids, ign1, n1, ign2, n2, &out, &ctx);
        RgOffSimDrop();
        return;
    }
    // a job runs: read-only answer, unless this thread holds +0x918, serves the job's callback, has a gate frame open, or
    // the worker lost a QL frame
    int why = SR_FB_STATE;
    const RgThread& t = t_rg;
    const unsigned __int64 tEntry = __rdtsc();
    if (k.steerMode && !g_inInvoke && k.rgOwnerTid != (LONG)tid && t.n == t.readers && !g_steerBroken) {
        LARGE_INTEGER t0;
        QueryPerformanceCounter(&t0);
        auto run = [&]() {
            return SrRun(agent, *(const DWORD* const*)ids, (int)((*(DWORD**)(ids + 4) - *(DWORD**)ids) / 2), ign1, n1, ign2, n2, &out,
                         &ctx, true);
        };
        why = run();
        if (why && k.srrMode == 1 && (why == SR_FB_TRY || why == SR_FB_TRIP)) why = SrRetry(why, run);
        if (!why) {
            for (int i = 0; i < out.nIds; i++) g_srIdPush(ids, NULL, out.ids[i]);
            const LONG us = UsSince(t0);
            InterlockedIncrement(&k.steerTaken);
            MwSkip(MW_S2);
            InterlockedExchangeAdd(&k.steerUs, us);
            Max(&k.steerMaxUs, us);
            S2Time(S2T_ENTRY, tEntry);
            return;
        }
    }
    SrFallback(why);
    S2Time(S2T_ENTRY, tEntry);  // ours only: the game's call below is today's cost
    const LONG w0 = g_w918Us;
    g_locoOrig(agent, edx, ids, ign1, n1, ign2, n2);
    // docs/250 §8: the fallback's job wait by site, both steerretry arms. shortcut: a call that waits at both keys books the
    // sum on the last one; split per key if the gather share ever matters on its own
    const int site = g_w918Us != w0 ? SrWaitSite(g_w918Key) : -1;
    if (site >= 0) InterlockedExchangeAdd(&k.srrFbWaitUs[site], g_w918Us - w0);
}

// [FpRebuildLock] Enabled=1 alone: only the W6a stub (C-A); with [SteerRO] it rides on the full set
static void InstallCa(const wchar_t* ini, bool needStub) {
    Ts3nCounters& k = ts3native_counters;
    if (needStub) {
        const QlSite& s = kQlSites[QL_W6A];
        if (!(g_qlTramp[QL_W6A] = InstallDetour(s.name, s.addr, s.pro, s.len, (void*)kQlStubs[QL_W6A], &g_qlTramp[QL_W6A]))) return;
    }
    if (!g_qlTramp[QL_W6A]) return;
    k.caMode = Clamp(GetPrivateProfileIntW(L"FpRebuildLock", L"Mode", 0, ini), 0, 1);
    g_caOn = 1;
    k.caReady = 1;
    Log("OK   footprint rebuild lock C-A (Mode=%ld: %s)", k.caMode, k.caMode ? "take +0x918 first" : "count only");
}

// The S2 writer set: W1-W6 (12 stubs) with W4 (the LotGraph_Build gate stub) and the 11 tripwire stubs. Every prologue
// not hooked yet is checked first (one differs: nothing installed). Shared by S2 and [ObstructedRO] (obstructro.cpp); the
// second call returns the first one's result. True = all in place; false with g_qlOn set = some QL stubs only (they lock).
static bool InstallSrWriters() {
    static int done;  // 1 all in place, -1 not
    if (done) return done > 0;
    done = -1;
    bool ok = g_rgTramp[3] != NULL;  // W4 = gate stub 3
    for (int i = 0; ok && i < QL_N; i++) ok = g_qlTramp[i] || Matches((const void*)(uintptr_t)kQlSites[i].addr, kQlSites[i].pro, kQlSites[i].len);
    for (int i = 0; ok && i < TW_N; i++) ok = Matches((const void*)(uintptr_t)kTwSites[i].addr, kTwSites[i].pro, kTwSites[i].len);
    if (!ok) { Log("SKIP S2 writer set: a prologue differs (W1-W6, T4/T-S7) or the LotGraph_Build gate stub is missing"); return false; }
    int n = 0;
    for (int i = 0; i < QL_N; i++) {  // [LotFrontDoorRO] / [ProbeP1b] may have W5 and W6g in place already
        if (!g_qlTramp[i]) g_qlTramp[i] = InstallDetour(kQlSites[i].name, kQlSites[i].addr, kQlSites[i].pro, kQlSites[i].len, (void*)kQlStubs[i], &g_qlTramp[i]);
        n += g_qlTramp[i] != NULL;
    }
    g_qlOn = 1;
    if (n != QL_N) {  // a W site without QL: a clone could read under its writes; the clones stay off (the stubs that exist still lock)
        Log("FAIL S2 writer set: %d of %d worker writer detours, read-only clones stay off", n, QL_N);
        return false;
    }
    const int tws = InstallTripwires();
    if (tws != TW_N) {  // the stubs that exist only count
        Log("FAIL S2 writer set: %d of %d tripwire stubs (T4/T-S7), read-only clones stay off", tws, TW_N);
        return false;
    }
    done = 1;
    return true;
}

// [SteerRO] Enabled=1: every prologue is checked first; one differs and nothing is hooked
static void InstallSteerRO(const wchar_t* ini) {
    const bool ca = GetPrivateProfileIntW(L"FpRebuildLock", L"Enabled", 0, ini) != 0;
    Ts3nCounters& k = ts3native_counters;
    if (!GetPrivateProfileIntW(L"SteerRO", L"Enabled", 0, ini)) {
        Log("steer RO off (Enabled=0)");
        if (!ca) { Log("footprint rebuild lock off (Enabled=0)"); return; }
        if (!k.rgEnforce || !k.asyncReady) { Log("SKIP footprint rebuild lock: needs [RouteGate] Enforce=1 with the async path"); return; }
        InstallCa(ini, true);
        return;
    }
    if (!k.rgEnforce || !k.asyncReady) { Log("SKIP steer RO: needs [RouteGate] Enforce=1 with the async path"); return; }
    if (!Matches((const void*)(uintptr_t)STEER_LOCO, kLocoPro, sizeof(kLocoPro))) { Log("SKIP steer RO: the 0x85FD10 prologue differs"); return; }
    const bool writers = InstallSrWriters();
    if (ca) InstallCa(ini, false);  // rides on the W6a stub when it exists
    if (!writers) return;
    const int n = QL_N, tws = k.steerTwStubs;
    g_locoOrig = (LocoFn)InstallDetour("Locomotion_ObstacleCheck", STEER_LOCO, kLocoPro, sizeof(kLocoPro), (void*)LocoHook, &g_locoOrig);
    if (!g_locoOrig) return;
    k.steerMode = Clamp(GetPrivateProfileIntW(L"SteerRO", L"Mode", 1, ini), 0, 1);
    k.steerShadow = Clamp(GetPrivateProfileIntW(L"SteerRO", L"Shadow", 0, ini), 0, 1);
    k.srrMode = Clamp(GetPrivateProfileIntW(L"SteerRO", L"Retry", 0, ini), 0, 1);
    k.steerReady = 1;
    Log("OK   steer RO: 0x85FD10 + %d worker writer detours + LotGraph_Build gate + %d tripwire stubs (Mode=%ld Shadow=%ld Retry=%ld)", n, tws,
        k.steerMode, k.steerShadow, k.srrMode);
}
