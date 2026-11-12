// [AutShadow] part 2 (docs/122 §1.2, §4, §14): request, cut, reference run, worker dispatch, window and stop, the
// AutShadowNative internal calls of ts3fixes, the rooted column and the array allocator. Included by autshadow.cpp
// (after the snapshot code, before the install). Rides [MonoThread]: the cut and the window use its Simulate slot stub
// (g_mtOnEnter / g_mtOnLeave, one slot owner), the worker is its M5 thread (MtSubmit, g_mtJobHook).
//
// One shadow search (docs/122 §1.2):
//   ts3fixes request hook (sim thread, B13 entry): Begin() -> AddRow(...) -> Post(id)
//   end of the frame (AsSimLeave): cut (AsCut), then Mode 1/2: the reference method on the sim thread with the script
//     RNG (host +0xC30) saved and restored, then the work method on the M5 worker; Mode 0 or no idle worker: finish at once
//   top of the next frame (AsSimEnter): window closed; a worker still inside gets the stop flag (overrun, A5)
//   a later end of frame, once the job left the worker: the finish method on the sim thread (compare, counters)
// Where (docs/51 I-188-3, docs/122 §14.11): Simulate loops (simloop.cpp), so "end" and "top" both sit at the loop-back and
// the window there is ~0. With the post-pump hook (simloop.cpp SimPumpAdd) the end runs after the sim's remote-call pump
// (0xD81FDA returns to 0xD81FDC), just before the frame limiter 0xD81FDE, and the top stays at the loop-back: the window
// is the limiter's sleep. Without a pump call (main == sim: no sim job record) the loop-back keeps doing both.
// Managed methods are static, no arguments: request and results live in rooted ts3fixes statics.

enum { AS_RUN_IDLE, AS_RUN_WINDOW, AS_RUN_WAIT };
enum { AS_LAST_OK, AS_LAST_STOPPED, AS_LAST_EPOCH, AS_LAST_NOWORKER, AS_LAST_REFUSED, AS_LAST_EXC };
static int g_asMode = -1, g_asSample = 4, g_asWarmIn = 20, g_asDump, g_asWaitUs;  // g_asWaitUs: WindowWaitUs (0 = no wait)
static void* g_asMFinish;  // MonoMethod* of AutShadow's finish / reference / work (SetMethods)
static void* g_asMRef;
static void* g_asMWork;
static volatile LONG g_asReqId;         // posted request (the batch's first), 0 = none
// docs/122 §14.18 shared cut: every request posted before the cut joins it (one snapshot, one reference call and one worker
// job for all of them; ts3fixes compares each request against its own rows). AS_BATCH_MAX = ts3fixes kMaxReq.
static const int AS_BATCH_MAX = 4;
static int g_asBatchN;                  // requests in the posted batch
static LONG g_asFrame, g_asNoteFrame;   // ends of frame (AsSimLeave), and the frame of the last B13 note
static volatile LONG g_asRun;           // AS_RUN_*
static LONG g_asLast = AS_LAST_OK;      // what LastRun() tells finish
static LONG g_asRunEpoch, g_asTrack0, g_asWaitTicks;
static volatile LONG g_asJobExc;
static volatile LONG g_asJobIn;         // the worker entered the current job (phase 0 seen)
static bool g_asPumpCut;                // the post-pump hook fired: the loop-back no longer cuts
static LARGE_INTEGER g_asWin0, g_asJob0;
static double g_asUsPerTick;
static int g_asSatbTicks;  // Satb act: ends of frame the job has been in flight
// Mode 3 go-live (docs/124 §17): ts3fixes reads these through Config 13-18; VerifyEvery runs the reference at every Nth cut
static int g_asLiveTopN = 10, g_asLiveBands = 3, g_asLiveMarginPm, g_asLiveMaxWait = 3, g_asLiveVerify = 50, g_asLiveDry;
static int g_asLiveUnseen;  // ini Unseen=1 with Mode=3 and the slot stub: ts3fixes turns autshadow, autlive, autunseen on (UserDiag op 8)
static LONG g_asCutFrame;   // the frame count at the last cut (frames are counted at their end, AsSimLeave)
static int g_asLiveRefRan;  // the last cut ran the reference (Mode 3: VerifyEvery)
static HANDLE g_asJsonl = INVALID_HANDLE_VALUE;
static wchar_t g_asJsonlPath[MAX_PATH];
// game addresses; selftest and monohost point them at fakes or the host's copies
typedef void(__cdecl* AsAddIcFn)(const char* name, const void* fn);
typedef void(__cdecl* AsAddRootsFn)(void* lo, void* hi);
typedef void*(__cdecl* AsGcMallocFn)(size_t n);
static AsAddIcFn g_asAddIc = (AsAddIcFn)(uintptr_t)0x00E82550;          // mono_add_internal_call
static AsAddRootsFn g_asAddRoots = (AsAddRootsFn)(uintptr_t)0x00E4D9F0;  // GC_add_roots (P3 site 17)
static AsGcMallocFn g_asGcMalloc = (AsGcMallocFn)(uintptr_t)0x00E4BE30;  // GC_malloc (P3-locked)
static u8** g_asHostPtr = (u8**)(uintptr_t)0x011EE514;                  // MonoScriptHost; script RNG at +0xC30 (docs/27 §1.1)
// MT object: 624 words, read pointer +0x9C0 (absolute, into the words), words left +0x9C4; the LCG follows at +0x15F8
// [verified: 0x4FA9F0 / 0x4FA8B0 in the disassembly, docs/125 §2.1]
static const DWORD AS_RNG_OFF = 0xC30, AS_RNG_LEN = 0x9C8;

// ---- script RNG for the worker (docs/125 §2): per-unit streams ----
// The driver marks units (RngUnit: each meta object's meta score, each pair). A unit's draws come from a Mersenne Twister
// seeded with AsRngSeed(cut seed, unit), on the worker in its own MT object, in the sim-thread reference in the host's
// (saved and restored around the reference), both through the game's own init and draw code. So the k-th draw of a unit
// is the same value on both threads whatever happened in other units (aborts, tooltip draws), and Mode R stays bit-equal.
typedef void(__fastcall* AsMtInitFn)(void* mt, void* edx, DWORD seed);  // 0x4FA990 thiscall, ret 4 (seed -1 = RDTSC)
typedef double(__fastcall* AsMtNextFn)(void* mt, void* edx);             // 0x4FAA70 thiscall: [0,1) on x87
static AsMtInitFn g_asMtInit = (AsMtInitFn)(uintptr_t)0x004FA990;
static AsMtNextFn g_asMtNext = (AsMtNextFn)(uintptr_t)0x004FAA70;
static const volatile u8* g_asDetFlag = (const volatile u8*)(uintptr_t)0x011CDBC5;  // `Deterministic`: draws are 0.5 (docs/27 §1.3)
static volatile DWORD g_asRngSeedV[2];  // per published version: the cut seed, 0 = none
static LONG g_asRngUnitW = -1;          // worker: current unit (-1 = none yet in this run)
static bool g_asRngLiveW;               // worker: g_asRngMtW is seeded for that unit
static u8 g_asRngMtW[AS_RNG_LEN];
static bool g_asInRef;                  // sim: inside the reference run
static volatile LONG g_asRngPendS = -1; // sim: unit whose stream the next reference draw starts
static DWORD g_asRngSeedS;
static void* g_asRngOrig;
// docs/125 addendum §4.1: a hooked call (LocalizeString, IsAllowedInRoom) inside the reference draws from a scratch stream,
// so its one-sided draws never shift the unit's stream; the host MT state is put back when the outermost bracket closes
static int g_asRngPause;
static u8 g_asRngPaused[AS_RNG_LEN];

// even, so the init's `seed |= 1` keeps distinct values apart and never makes -1 (RDTSC)
static DWORD AsRngSeed(DWORD cut, LONG unit) {
    DWORD h = cut ^ (DWORD)unit * 0x9E3779B1u;
    h ^= h >> 16, h *= 0x85EBCA6Bu, h ^= h >> 13, h *= 0xC2B2AE35u, h ^= h >> 16;
    return h << 1;
}
// the cut seed: the live MT words and count (not the read pointer: an address), so seeded runs (docs/27) repeat it
static DWORD AsRngCutSeed() {
    const u8* host = g_asHostPtr ? *g_asHostPtr : NULL;
    if (!host) return 0;
    const DWORD* w = (const DWORD*)(host + AS_RNG_OFF);
    DWORD h = 0x811C9DC5u ^ w[0x271];
    for (int i = 0; i < 0x270; i++) { h ^= w[i]; h *= 0x1000193u; }
    return h | 1;
}
static int AsRngWorker(DWORD idx, AsRet* r) {
    const LONG v = g_asHeld;
    const DWORD seed = v >= 0 ? g_asRngSeedV[v] : 0;
    if (g_asRngUnitW < 0 || !seed) { AsAbort(AS_AB_RNG, idx); return 1; }  // a draw outside any unit: no defined stream
    if (*g_asDetFlag) { r->f = 0.5; return 1; }  // as 0xD70EE0
    if (!g_asRngLiveW) { g_asMtInit(g_asRngMtW, NULL, AsRngSeed(seed, g_asRngUnitW)); g_asRngLiveW = true; }
    r->f = g_asMtNext(g_asRngMtW, NULL);
    return 1;
}
// the guard's sim-thread target for the RNG IC while the reference runs: start the pending unit's stream, then the game's draw
static double __cdecl AsRngSimDraw() {
    const LONG u = g_asRngPendS;
    if (g_asRngPause) ts3native_autshadow.rngPauseDraws++;  // scratch stream: the pending unit waits for its first real draw
    else if (u >= 0) {
        g_asRngPendS = -1;
        u8* host = *g_asHostPtr;
        if (host) g_asMtInit(host + AS_RNG_OFF, NULL, AsRngSeed(g_asRngSeedS, u));
    }
    return ((double(__cdecl*)())g_asRngOrig)();
}

static LONG AsUs(const LARGE_INTEGER& t0) {
    LARGE_INTEGER t1;
    QueryPerformanceCounter(&t1);
    return (LONG)((t1.QuadPart - t0.QuadPart) * g_asUsPerTick);
}
static void AsMax(LONG& m, LONG v) { if (v > m) m = v; }

// Arrays answered to the worker (docs/122 §3.2 a-lists, a-lot): a MonoArray {vtable, sync, max_length, bounds, data at +16}
// [verified in monohost: length at +8, as AsRefresh reads it] from GC_malloc (scanned, zeroed; P3 lock while the worker runs).
// Elements are 4 bytes (object references, int[], uint[], float[]) or 8 (ulong[], docs/126 #7; data at +16 is 8-aligned).
static void* __cdecl AsNewArrayGc(DWORD vtable, DWORD n, DWORD elemBytes) {
    if (n > (1u << 20) || (elemBytes != 4 && elemBytes != 8)) return NULL;
    DWORD* a = (DWORD*)g_asGcMalloc(16 + n * elemBytes);
    if (a) a[0] = vtable, a[2] = n;
    return a;
}

// first cut (sim thread, outside any GC): the rooted column, pools and the two kept objects become GC roots
static void AsRootOnce() {
    Ts3nAutShadow& k = ts3native_autshadow;
    if (k.rooted & 1) return;
    if (g_asAddRoots) {
        for (int v = 0; v < 3; v++) {
            AsTab& t = v ? g_as->v[v - 1] : g_as->m;
            g_asAddRoots(t.refs, t.pool + AS_POOL);
        }
        g_asAddRoots(g_as->poolTmp, g_as->keep + 4);
        g_asRefsRooted = true;
        k.rooted |= 1;
    }
    if (!g_asNewArray && g_asGcMalloc) g_asNewArray = AsNewArrayGc;
    if (g_asNewArray) k.rooted |= 2;
}

// ---- managed calls (0xE5A600 on this thread, monothread-m5.cpp g_mtInvoke) ----
static bool AsInvoke(void* m, LONG* us) {
    LARGE_INTEGER t0;
    QueryPerformanceCounter(&t0);
    void* exc = NULL;
    g_mtInvoke(m, NULL, NULL, &exc);
    if (us) *us = AsUs(t0);
    return exc == NULL;
}

static void AsFinish(LONG status) {
    Ts3nAutShadow& k = ts3native_autshadow;
    g_asLast = status;
    g_asLastJobBits = g_asJobBits | (k.satbWLoads != g_asJobLoads0 ? AS_FEAT_SATB : 0) | (g_satbColdGiveUps != g_asJobCold0 ? AS_FEAT_COLD : 0);
    g_asJobBits = 0, g_asJobLoads0 = k.satbWLoads, g_asJobCold0 = g_satbColdGiveUps;
    k.finishRuns++;
    if (g_asMFinish && !AsInvoke(g_asMFinish, NULL)) k.finishExc++;
    InterlockedExchange(&g_asRun, AS_RUN_IDLE);
    InterlockedExchange(&g_asReqId, 0);
    g_asBatchN = 0;
}

// the reference run on the sim thread: same driver, same cut, script RNG put back afterwards (docs/122 §3.4)
static void AsReference() {
    Ts3nAutShadow& k = ts3native_autshadow;
    static u8 saved[AS_RNG_LEN];
    u8* host = g_asHostPtr ? *g_asHostPtr : NULL;
    u8* rng = host ? host + AS_RNG_OFF : NULL;
    if (rng) memcpy(saved, rng, AS_RNG_LEN);
    const int ri = g_asIcRng;
    const DWORD seed = g_asPub >= 0 ? g_asRngSeedV[g_asPub] : 0;
    if (rng && ri >= 0 && seed) {  // RngUnit marks start per-unit streams in the host MT (docs/125 §2)
        g_asRngOrig = g_asOrig[ri], g_asRngSeedS = seed, g_asRngPendS = -1, g_asInRef = true;
        g_asOrig[ri] = (void*)AsRngSimDraw;
    }
    LONG us = 0;
    if (!AsInvoke(g_asMRef, &us)) k.refExc++;
    if (g_asInRef) g_asOrig[ri] = g_asRngOrig, g_asInRef = false, g_asRngPendS = -1;
    g_asRngPause = 0;  // an exception inside a bracket: the whole MT state is restored just below
    if (rng && memcmp(saved, rng, AS_RNG_LEN)) { k.rngDraws++; memcpy(rng, saved, AS_RNG_LEN); }
    k.refRuns++, k.refUs += us;
    AsMax(k.refMaxUs, us);
}

// docs/122 §14.21 / docs/124 §15: a fresh re-score at the finish (sim thread, >= 1 tick after the cut), bracketed as the
// reference is: the script RNG is saved and put back, RngUnit draws per-unit streams with the cut's seed, so only the world
// change between the cut and now can make a score differ. on = 1 opens, 0 closes (time into freshUs).
static int __cdecl AsIcIsWorker();
static int g_asFreshEvery, g_asKeepPartial;
static LARGE_INTEGER g_asFreshT0;
static u8 g_asFreshRng[AS_RNG_LEN];
static bool g_asFreshOpen;
static void __cdecl AsIcFresh(int on) {
    if (AsIcIsWorker() || !g_asFreshEvery) return;
    Ts3nAutShadow& k = ts3native_autshadow;
    u8* host = g_asHostPtr ? *g_asHostPtr : NULL;
    u8* rng = host ? host + AS_RNG_OFF : NULL;
    const int ri = g_asIcRng;
    if (on && !g_asFreshOpen) {
        g_asFreshOpen = true;
        if (rng) memcpy(g_asFreshRng, rng, AS_RNG_LEN);
        const DWORD seed = g_asPub >= 0 ? g_asRngSeedV[g_asPub] : 0;
        if (rng && ri >= 0 && seed && !g_asInRef) {
            g_asRngOrig = g_asOrig[ri], g_asRngSeedS = seed, g_asRngPendS = -1, g_asInRef = true;
            g_asOrig[ri] = (void*)AsRngSimDraw;
        }
        QueryPerformanceCounter(&g_asFreshT0);
    } else if (!on && g_asFreshOpen) {
        g_asFreshOpen = false;
        if (g_asInRef && ri >= 0) g_asOrig[ri] = g_asRngOrig, g_asInRef = false, g_asRngPendS = -1;
        g_asRngPause = 0;
        if (rng) memcpy(rng, g_asFreshRng, AS_RNG_LEN);
        k.freshReqs++, k.freshUs += AsUs(g_asFreshT0);
    }
}
// ts3fixes' Mode R consume-side stats (docs/124 §15): 0 pass, 1 classified, 2 fresh pairs, 3 differ, 4 top-1, 5 top-10,
// 6 partial overrun runs kept; contract 9 (docs/122 §14.23): 7 / 8 top-1 / top-10 over the pairs both sides answered,
// 9 / 10 top-1 / top-10 of the cut-time reference vs fresh, 11 reference passes among worker-A4 pairs
static void __cdecl AsIcStat(int which, int add) {
    if (AsIcIsWorker()) return;
    Ts3nAutShadow& k = ts3native_autshadow;
    LONG* const f[] = {&k.passPairs, &k.classPairs, &k.freshPairs, &k.freshDiff, &k.freshTop1, &k.freshTop10, &k.partialKept,
                       &k.freshTop1Ans, &k.freshTop10Ans, &k.refFreshTop1, &k.refFreshTop10, &k.refPassA4};
    if (which >= 0 && which < (int)(sizeof(f) / sizeof(f[0]))) *f[which] += add;
}
// contract 9: the game's C2 time per pair (ts3fixes AutScoreHook, sampled searches). PairT0 = the low 32 bits of the
// performance counter; PairT1(kind, t0) adds now - t0 to pairT[kind] in 100 ns units (0 allow-listed pass, 1 allow-listed
// fail, 2 off-list pass, 3 off-list fail). Sim thread only (C2 runs there); nested calls are fine (t0 lives in the caller).
static int __cdecl AsIcPairT0() { LARGE_INTEGER t; QueryPerformanceCounter(&t); return (int)t.LowPart; }
static void __cdecl AsIcPairT1(int kind, int t0) {
    if (AsIcIsWorker() || kind < 0 || kind > 3) return;
    LARGE_INTEGER t;
    QueryPerformanceCounter(&t);
    Ts3nAutShadow& k = ts3native_autshadow;
    k.pairT[kind] += (LONG)((DWORD)(t.LowPart - (DWORD)t0) * g_asUsPerTick * 10.0), k.pairN[kind]++;
}

// contract 10 (docs/124 §17): go-live counters by slot (ts3fixes AutLive.cs kS*), sim thread only
static void __cdecl AsIcLiveStat(int which, int add) {
    if (AsIcIsWorker() || which < 0 || which >= (int)(sizeof(ts3native_autshadow.al) / sizeof(LONG))) return;
    ts3native_autshadow.al[which] += add;
}
// watch a live request's meta object (isLot 0: object id) or a lot (isLot 1: LotId) for ObjStale (autshadow.cpp AsGwWatch)
static void __cdecl AsIcLiveWatch(DWORD lo, DWORD hi, int isLot) {
    if (AsIcIsWorker() || g_asMode != 3) return;
    AsGwWatch((lo | (AsU64)hi << 32) ^ (isLot ? AS_GW_LOT : 0));
}
static int __cdecl AsIcObjStale(DWORD lo, DWORD hi, int isLot, int cutFrame) {
    if (AsIcIsWorker()) return 1;
    return AsGwStale((lo | (AsU64)hi << 32) ^ (isLot ? AS_GW_LOT : 0), cutFrame);
}

// FPU state (docs/122 §14.15): the worker scores with the sim thread's x87 control word and MXCSR, as route jobs do
// (ts3native.cpp ServePlan). mono-cost6: 44 A1/A2 pairs off by 1-200 ULP with bit-equal inputs [inferred: precision
// control; simX87/workX87 show both words].
static unsigned short g_asSimCw, g_asWorkCw;
static volatile LONG g_asLastJobUs;  // the last job's run time (Satb probe span length)
static unsigned int g_asSimCsr, g_asWorkCsr;
static bool g_asFpuSet;  // worker: its own state is saved and the sim's applied (phase 0 seen with a captured state)

// worker, around each M5 job (only ours count): hold the published version, time the run
static void __cdecl AsJobHook(int phase, void* method) {
    if (method != g_asMWork) return;
    Ts3nAutShadow& k = ts3native_autshadow;
    if (phase == 0) {
        g_asWorkCw = GetX87(), g_asWorkCsr = _mm_getcsr();
        k.workX87 = g_asWorkCw, k.workCsr = (LONG)g_asWorkCsr;
        g_asFpuSet = g_asSimCw != 0;  // 0 = no cut captured yet
        if (g_asFpuSet) SetX87(g_asSimCw), _mm_setcsr(g_asSimCsr);
        InterlockedExchange(&g_asHeld, g_asPub);  // AsWorkerBegin without clearing stop: the sim may have set it already
        // the world moved between cut and start; with Satb act the worker reads the cut through the log, so a move is fine
        if ((g_asRunEpoch != g_asEpoch && !(g_satbMode == SATB_ACT && g_satbLive)) || g_arGate) InterlockedExchange(&g_asStop, 1);  // R2c: gate closed
        g_asRngUnitW = -1, g_asRngLiveW = false;
        InterlockedExchange(&g_asJobIn, 1);
        QueryPerformanceCounter(&g_asJob0);
        return;
    }
    if (g_asFpuSet) SetX87(g_asWorkCw), _mm_setcsr(g_asWorkCsr), g_asFpuSet = false;
    const LONG us = AsUs(g_asJob0);
    InterlockedExchangeAdd(&k.workUs, us);
    if (us > k.workMaxUs) InterlockedExchange(&k.workMaxUs, us);
    if (phase == 2) InterlockedIncrement(&k.workExc), InterlockedExchange(&g_asJobExc, 1);
    g_asLastJobUs = us;
    if (g_satbMode == SATB_ACT) SatbOff();  // the job left: the sim stops paying the barrier at once (the log stays until the next cut)
    AsWorkerEnd();
}

static bool __cdecl AsSatbBusy() { return g_mtBusy != 0; }
// races R2c (autraces.cpp ArDrain), sim thread at a going-away world event: our job is inside the worker; tell it to stop and
// drop its result (an overrun's kept part too: the world it scored is going away)
static bool AsDrainBusy() { return g_asRun != AS_RUN_IDLE && g_mtBusy != 0; }
static bool AsDrainStop() {
    InterlockedExchange(&g_asStop, 1);
    g_asLast = AS_LAST_EPOCH;
    return g_satbMode == SATB_ACT && g_satbLive;
}
static bool AsWorkerIdle() { return g_mtWorker && ts3native_mtprobe.workerState == 2 && !g_mtBusy; }

// return of MonoScriptHost_Simulate (sim thread): finish a run whose job ended, else cut for a posted request
static void __cdecl AsSimLeave() {
    Ts3nAutShadow& k = ts3native_autshadow;
    k.simTicks = ++g_asFrame;
    g_asGwNow = g_asFrame;  // go-live watch stamps: events from here on belong to the next frame
    if (g_satbMode == SATB_PROBE && g_satbLive) SatbEnd();  // probe: the span was this tick (from the Simulate entry)
    if (g_asRun == AS_RUN_WAIT) {
        if (g_mtBusy) {  // the job (or a skip) has not left the worker yet
            k.waitTicks++;
            if (g_satbLive && g_satbMode == SATB_ACT) {
                k.satbConcTicks++;
                if (++g_asSatbTicks >= 8 && !g_asStop) { InterlockedExchange(&g_asStop, 1); k.overrun++; if (g_asLast == AS_LAST_OK) g_asLast = AS_LAST_STOPPED; }
            }
            return;
        }
        ArJobLeft();  // races R2c: an aborted drain's job is out
        LONG st = g_asLast;
        if (g_satbMode == SATB_ACT && g_satbLive) {
            SatbEnd();  // worker idle: accounting, roots off
            // an unlogged write (the sim stopped the worker first) or a worker give-up: the objects finished before the stop
            // stay valid (ts3fixes checks the stop flag after each object, KeepPartial), as for an overrun
            if (g_satbStale && st == AS_LAST_OK) st = AS_LAST_STOPPED;
        }
        if (g_asJobExc) st = AS_LAST_EXC;
        else if (ts3native_mtprobe.workerTrackSkips != g_asTrack0) st = AS_LAST_REFUSED, k.workRefused++;
        AsFinish(st);
        return;
    }
    if (g_asRun != AS_RUN_IDLE || !g_asReqId) return;
    if (g_arGate) { ts3native_counters.r2cHeld++; AsFinish(AS_LAST_EPOCH); return; }  // races R2c: no world to cut
    AsRootOnce();
    const LONG pl0 = g_asPreLearnRows, t10 = g_asReqLotT1;
    AsCut();
    // per-search feature mask, native half (ts3fixes AutLiveCore.Feat): this cut's PreLearn / TierReqLots work, and the
    // job's Satb redirects and cold give-ups as deltas from here (read at AsFinish, Config(33))
    g_asJobBits = (g_asPreLearnRows != pl0 ? AS_FEAT_PRELEARN : 0) | (g_asReqLotT1 != t10 ? AS_FEAT_TIERREQ : 0);
    g_asJobLoads0 = k.satbWLoads, g_asJobCold0 = g_satbColdGiveUps;
    g_asRngSeedV[g_asPub] = AsRngCutSeed();
    k.cutReq++;
    if ((k.cutReq & 255) == 0) AsAbortIcLog();
    k.batchReqs += g_asBatchN;
    if (g_asBatchN > 1) k.batchCuts++;
    AsMax(k.batchMax, g_asBatchN);
    k.cutSite = g_asPumpCut ? 2 : 1;
    g_asRunEpoch = g_asEpoch;
    g_asCutFrame = g_asFrame;
    if (g_asMode == 0 || !g_asMRef || !g_asMWork) { k.noWorker++; AsFinish(AS_LAST_NOWORKER); return; }
    if (g_asMode == 3) {  // go-live: the reference is a sampled check only (docs/124 §7.1 VerifyEvery)
        k.alCuts++;
        g_asLiveRefRan = g_asLiveVerify > 0 && k.alCuts % g_asLiveVerify == 0;
        if (g_asLiveRefRan) k.alVerify++, AsReference();
    } else {
        AsReference();
    }
    if (!AsWorkerIdle()) { k.noWorker++; AsFinish(AS_LAST_NOWORKER); return; }
    g_asWorkerTid = g_mtWorkerTid;
    g_asSimCw = GetX87(), g_asSimCsr = _mm_getcsr();
    k.simX87 = g_asSimCw, k.simCsr = (LONG)g_asSimCsr;
    g_asLast = AS_LAST_OK;
    g_asJobExc = 0;
    g_asJobIn = 0;
    g_asTrack0 = ts3native_mtprobe.workerTrackSkips;
    InterlockedExchange(&g_asStop, 0);
    InterlockedExchange(&g_asRun, AS_RUN_WINDOW);
    QueryPerformanceCounter(&g_asWin0);
    if (g_satbMode == SATB_ACT) SatbBegin(g_mtWorkerTid, 0), g_asSatbTicks = 0;  // barrier on before the worker can read
    if (!MtSubmit(g_asMWork, NULL, NULL)) { SatbEnd(); k.workRefused++; AsFinish(AS_LAST_REFUSED); return; }
    k.workRuns++;
}
// loop-back end of frame (g_mtOnLeave): cuts only while the post-pump hook has not fired
static void __cdecl AsSimLeaveLoop() {
    if (!g_asPumpCut) AsSimLeave();
}
// after the sim pump, before the frame limiter (SimPumpAdd): the end of frame from now on
static void __cdecl AsSimPump() {
    g_asPumpCut = true;
    AsSimLeave();
}

// top of MonoScriptHost_Simulate (sim thread): the window closes; a worker still inside is told to stop (no wait)
static void AsRegisterIcs();
static void AsLgTick();
static void __cdecl AsSimEnter() {
    Ts3nAutShadow& k = ts3native_autshadow;
    if (!k.icRegistered) AsRegisterIcs();
    AsLgTick();
    const bool satbAct = g_satbMode == SATB_ACT && g_satbLive;  // the worker reads the cut through the log: no stop, no wait
    if (g_asRun != AS_RUN_IDLE && g_asRunEpoch != g_asEpoch && g_asLast == AS_LAST_OK && !satbAct) {
        InterlockedExchange(&g_asStop, 1);
        k.epochDrops++;
        g_asLast = AS_LAST_EPOCH;
    }
    if (g_asRun == AS_RUN_WINDOW) {
        const LONG us = AsUs(g_asWin0);
        k.windowUs += us;
        AsMax(k.windowMaxUs, us);
        // WindowWaitUs > 0 (shadow tests only): the sim waits here, before any script work of the frame, for the job to end.
        // mono-cost4 IP: window 6.5 us average (no limiter sleep), the worker never entered a job (overrunIdle 953 of 953).
        if (satbAct) {
            if (g_mtBusy) k.satbConc++;  // the job runs on while this tick's script runs
            InterlockedExchange(&g_asRun, AS_RUN_WAIT);
            return;
        }
        if (g_mtBusy && g_asWaitUs > 0) {
            LARGE_INTEGER w0;
            QueryPerformanceCounter(&w0);
            for (int spin = 0; g_mtBusy && AsUs(w0) < g_asWaitUs; spin++) {
                if (spin < 64) YieldProcessor();
                else SwitchToThread();
            }
            k.windowWaits++, k.windowWaitUs += AsUs(w0);
        }
        if (g_mtBusy) {
            InterlockedExchange(&g_asStop, 1);
            k.overrun++;
            if (!g_asJobIn) k.overrunIdle++;
            if (g_asLast == AS_LAST_OK) g_asLast = AS_LAST_STOPPED;
        }
        InterlockedExchange(&g_asRun, AS_RUN_WAIT);
        // probe (S0): the stores this tick's script makes in one job length from here are what a concurrent job would log
        if (g_satbMode == SATB_PROBE) {
            const LONG runs = k.workRuns, avg = runs ? k.workUs / runs : 0;
            SatbBegin(g_mtWorkerTid, g_mtBusy ? avg : (g_asLastJobUs ? g_asLastJobUs : avg));
        }
    }
}

// ---- AutShadowNative internal calls (ts3fixes, docs/122 §3.5; registered at the first Simulate tick) ----
static int AsLgConfig(int key);
static int __cdecl AsIcVersion() { return 11; }  // 3: RngUnit (docs/125); 4: RngPause, abort reasons rain/posture; 5: Redirect; 6: shared cut, ListType*, Note; 7: ObjClass*, ListTypeCount/At; 8: Fresh; 9: Stat 7-11, PairT0/PairT1, Stat; 10: LiveStat, LiveWatch, ObjStale, Config 13-23 (go-live); 11: ImageSha (docs/218 §5 content guard)
// docs/218 §5 (content accept list): SHA-256 of a loaded module's bytes as Mono holds them. MonoImage: raw_data at +0x08,
// raw_data_len at +0x0C [inferred from Mono's MonoImage layout, whose +0x28 GUID string is verified (docs/104 §5.1);
// checked here by the "MZ" header and in game by the hashes matching the S3SA bytes]. sha: a byte[32] (MonoArray:
// max_length +8, data +16, as AsIcObjClassTake). Returns the bytes hashed, 0 when it cannot (no BCrypt SHA-256, a bad
// image pointer, a short array). Sim thread, once per module per load.
typedef LONG(WINAPI* AsBcOpenFn)(void**, LPCWSTR, LPCWSTR, ULONG);
typedef LONG(WINAPI* AsBcHashFn)(void*, PUCHAR, ULONG, PUCHAR, ULONG, PUCHAR, ULONG);
static AsBcHashFn g_asBcHash;
static void* g_asBcSha;
static bool g_asBcTried;
static int __cdecl AsIcImageSha(const u8* image, u8* sha) {
    if (!g_asBcTried) {
        g_asBcTried = true;
        HMODULE b = LoadLibraryW(L"bcrypt.dll");
        AsBcOpenFn open = b ? (AsBcOpenFn)(void*)GetProcAddress(b, "BCryptOpenAlgorithmProvider") : nullptr;
        AsBcHashFn hash = b ? (AsBcHashFn)(void*)GetProcAddress(b, "BCryptHash") : nullptr;
        if (open && hash && open(&g_asBcSha, L"SHA256", nullptr, 0) == 0) g_asBcHash = hash;
    }
    if (!g_asBcHash || !image || !sha) return 0;
    __try {
        if (*(const LONG*)(sha + 8) < 32) return 0;
        const u8* raw = *(const u8* const*)(image + 8);
        const DWORD len = *(const DWORD*)(image + 0xC);
        if (!raw || len < 0x200 || len > (256u << 20) || raw[0] != 'M' || raw[1] != 'Z') return 0;
        if (g_asBcHash(g_asBcSha, nullptr, 0, (PUCHAR)raw, len, sha + 16, 32) != 0) return 0;
        return (int)len;
    } __except (EXCEPTION_EXECUTE_HANDLER) { return 0; }
}
static int __cdecl AsIcConfig(int key) {
    switch (key) {
        case 0: return g_asMode;
        case 1: return g_asSample;
        case 2: return g_asWarmIn;
        case 3: return g_asDump;
        case 4: return AsWorkerIdle() ? 1 : 0;
        case 11: return g_asFreshEvery;  // docs/122 §14.21: re-score 1 in N finishes fresh (0 = off)
        case 12: return g_asKeepPartial;  // docs/122 §14.21: an overrun run keeps the meta objects it finished before the stop
        // docs/124 §17 go-live (Mode 3): settings, then the frame and epoch words the consumer's checks compare
        case 13: return g_asLiveTopN;
        case 14: return g_asLiveBands;
        case 15: return g_asLiveMarginPm;
        case 16: return g_asLiveMaxWait;
        case 17: return g_asLiveVerify;
        case 18: return g_asLiveDry;
        case 19: return g_asFrame;     // frames ended so far
        case 20: return g_asEpoch;     // world epoch now
        case 21: return g_asRunEpoch;  // world epoch at the last cut
        case 22: return g_asCutFrame;  // frame count at the last cut
        case 23: return g_asLiveRefRan;
        // docs/124 §17.10 go-live cost guard (ts3fixes AutLiveCore.CostGuard): 24 = 1 marks these keys (older builds answer 0),
        // then the cumulative sim-side cost and reference words it takes window deltas of (wrap like the LONG counters)
        case 24: return 1;
        case 25: return ts3native_autshadow.cutUs;
        case 26: return ts3native_autshadow.cuts;
        case 27: return ts3native_mtprobe.simLockWaitUs;
        case 28: return ts3native_mtprobe.lockedCalls - ts3native_mtprobe.lockedWorker;  // the sim's locked GC entries
        case 29: return ts3native_autshadow.refUs;
        case 30: return ts3native_autshadow.refRuns;
        case 31: return 1;  // 0.36.147: AddRow kind 3 (a request's scored LotId) is understood (older builds read kind != 0 as a Sim)
        case 32: return g_satbMode == SATB_ACT ? 1 : 0;  // 0.36.148: RainArm is registered and can skip (docs/127 H11 shelter answer)
        case 33: return g_asLastJobBits;  // 0.36.148: native feature bits of the last finished job (AS_FEAT_*, ts3fixes AutLiveCore.Feat)
        case 34: return 1;  // 0.36.148: SearchMark registered, Config(33) answers
        case 35: return 1;  // P6: ActorScope registered (the Query_CountObjectsInRange row's actor gate; P5 slot rows since 0.36.213)
        case 36: return (g_asT2eBits >> 8) & 1;  // 0.36.213 P5: [AutShadow] Tier2SlotRows in force (ts3fixes autofft2ic slots 23-24)
        default: return AsLgConfig(key);  // 5-10: ledger probe (autledger.cpp)
    }
}
static int __cdecl AsIcIsWorker() { return g_asWorkerTid && GetCurrentThreadId() == g_asWorkerTid; }
// worker only, like Aborted/ResetAbort/Stopped: a sim-thread call is a no-op, so ts3fixes can warm the IC up on the sim at
// load (docs/206 §15: Abort was one of the 7 cold give-ups); every ts3fixes caller is already behind OnWorker()
static void __cdecl AsIcAbort(int reason) { if (AsIcIsWorker()) AsManagedAbort(reason); }
// the abort word and the stop flag belong to the worker's run: on any other thread they read as clear
static int __cdecl AsIcAborted() { return AsIcIsWorker() ? AsAborted() : -1; }
static void __cdecl AsIcResetAbort() { if (AsIcIsWorker()) AsResetAbort(); }
static int __cdecl AsIcStopped() { return AsIcIsWorker() && (g_asStop != 0 || g_arGate != 0); }
// P6 actor gate (Config(35) == 1): ts3fixes says per request whether its actor is household, selected or unknown (1) or
// not (0); worker only, a sim-thread call is a no-op (load warm-up)
static void __cdecl AsIcActorScope(int hh) { if (AsIcIsWorker()) InterlockedExchange(&g_asActorHh, hh ? 1 : 0); }
// per-search feature mask (sim thread, at a search's end): the last search's request id, mask and top definition, kept for
// the crash log (AsCrashLine from XcptLog): a fault during or right after a search names the features it used
static void __cdecl AsIcSearchMark(int req, int mask, const DWORD* def) {
    if (AsIcIsWorker()) return;
    g_asMarkReq = req, g_asMarkMask = mask;
    int n = 0;
    if (def && def[1] > 0)  // EA's MonoString {vtable, length, UTF-16 at +8}
        for (const WORD* w = (const WORD*)(def + 2); n < (int)def[1] && n < (int)sizeof(g_asMarkDef) - 1; n++) g_asMarkDef[n] = w[n] < 128 ? (char)w[n] : '?';
    g_asMarkDef[n] = 0;
}
static const char* const kAsFeatNames[] = {"late", "prelearn", "tierreqlots", "rain", "desirefn", "satb", "guard", "cold", "partial"};
static int AsFeatText(int mask, char* out, int cap) {
    int n = 0;
    out[0] = 0;
    for (int b = 0; b < 9; b++)
        if (mask & (1 << b)) n += _snprintf_s(out + n, cap - n, _TRUNCATE, "%s%s", n ? "|" : "", kAsFeatNames[b]);
    return n;
}
// XcptLog (ts3native.cpp, [Crash] Log=1): one line per logged access violation / illegal instruction while [AutShadow] ran
static void AsCrashLine() {
    if (g_asMode < 0 || (!g_asMarkReq && !g_asReqId)) return;
    char names[128];
    AsFeatText(g_asMarkMask, names, sizeof(names));
    const int u = g_asRngUnitW;
    Log("  autshadow search: run %ld, posted req %ld, worker unit obj %d pair %d (thread %s), this cut's native bits 0x%lX; last finished "
        "search req %ld mask 0x%03lX [%s] top definition %s", (LONG)g_asRun, (LONG)g_asReqId, u >> 16, u & 0xFFFF,
        AsIcIsWorker() ? "worker" : "sim", g_asJobBits, g_asMarkReq, g_asMarkMask, names, g_asMarkDef[0] ? g_asMarkDef : "-");
}
// docs/127 H11 (worker only; -1 elsewhere, so the sim-thread warm-up is a no-op): def != null arms the next callvirt of site
// on def (satb.cpp SatbRainSkip), 1 armed, 0 not (no Satb act span: ts3fixes aborts the pair `rain` as before); def null
// disarms and answers the calls skipped since the arm (0 = the branch never made the call: counted rainMiss)
static int __cdecl AsIcRainArm(void* def, void* site) {
    if (!AsIcIsWorker()) return -1;
    Ts3nAutShadow& k = ts3native_autshadow;
    if (!def) {
        const int n = g_asRainSkips;
        if (!n) k.rainMiss++;  // armed, no call: the branch was not reached (meta CalculateScore outside local autonomy)
        g_asRainDef = 0, g_asRainSkips = 0;
        return n;
    }
    if (g_satbMode != SATB_ACT || !g_satbOn || !site) return 0;
    g_asRainMm = (DWORD)(uintptr_t)site, g_asRainDef = (DWORD)(uintptr_t)def, g_asRainSkips = 0;
    k.rainArm++;
    return 1;
}
static int __cdecl AsIcBegin() {
    if (g_asMode < 0 || !g_as || !g_asMFinish || AsIcIsWorker()) return 0;
    if (g_asRun != AS_RUN_IDLE || (g_asReqId && g_asBatchN >= AS_BATCH_MAX)) { ts3native_autshadow.busy++; return 0; }
    if (g_asReqId) { g_asNextActor = true; return 1; }  // joins the posted batch's cut (docs/122 §14.18)
    AsSetClear(g_as->always);  // reachable Sims are per batch
    AsSetClear(g_as->reqLots);  // scored lots too (TierReqLots, PreLearn)
    g_asActor = 0, g_asActorN = 0, g_asNextActor = false;
    return 1;
}
static void __cdecl AsIcAddRow(DWORD lo, DWORD hi, int kind) {
    if (!AsIcIsWorker() && g_asRun == AS_RUN_IDLE) AsAddRowKind(lo | (AsU64)hi << 32, kind);
}
static void AsClsFlush();
static void __cdecl AsIcPost(int id) {
    if (AsIcIsWorker() || g_asRun != AS_RUN_IDLE || g_asMode < 0) return;
    AsClsFlush();  // the request's classification (docs/122 §14.20) ended before its Post
    if (g_asReqId) g_asBatchN++;
    else g_asBatchN = 1, InterlockedExchange(&g_asReqId, id ? id : 1);
    g_asNextActor = false;
    ts3native_autshadow.req++;
}
// docs/122 §14.18: every game B13 call (sampled or not): the go-live request rate, and frames with at least one (the
// go-live cut rate with shared cuts)
static void __cdecl AsIcNote() {
    if (AsIcIsWorker()) return;
    Ts3nAutShadow& k = ts3native_autshadow;
    k.metaCalls++;
    if (g_asNoteFrame != g_asFrame + 1) g_asNoteFrame = g_asFrame + 1, k.metaTicks++;  // +1: frame 0 counts too
}
// list kinds (docs/122 §14.18): the next list type ts3fixes has not classified (its System.Type object), else null; then
// ListTypeSet(type, 1 = some Sim is of this type, 0 = none). Sim thread only; each type is asked once.
static void* __cdecl AsIcListTypeNext() {
    if (AsIcIsWorker()) return NULL;
    for (int i = 0; i < g_asLTypeN; i++) {
        AsLType& t = g_asLTypes[i];
        if (t.cls || t.asked) continue;
        t.asked = 1;
        if (t.type) return (void*)(uintptr_t)t.type;
    }
    return NULL;
}
static void __cdecl AsIcListTypeSet(void* type, int simCapable) {
    if (AsIcIsWorker()) return;
    AsLType* t = AsLTypeOf((DWORD)(uintptr_t)type, false);
    if (!t || t->cls) return;
    t->cls = simCapable ? 2 : 1;
    ts3native_autshadow.listTypes++;
    if (!simCapable) ts3native_autshadow.listTypesSimFree++;
}
// typed marks (docs/122 §14.20), sim thread: the list types in AsLTypeOf order (ts3fixes keeps them by index), the queued
// object ids into a ulong[] (MonoArray: max_length +8, elements +16), and each object's class (bit i = of list type i,
// n = types it was tested against; n 0 = no script object yet, it stays unknown)
static int __cdecl AsIcListTypeCount() { return AsIcIsWorker() ? 0 : g_asLTypeN; }
static void* __cdecl AsIcListTypeAt(int i) { return AsIcIsWorker() || i < 0 || i >= g_asLTypeN ? NULL : (void*)(uintptr_t)g_asLTypes[i].type; }
// newest first (the ids a cut's marks queued come before older event-4 ones); ts3fixes takes one buffer (32) per request,
// so the sim-thread cost per request is bounded; clsUs = Take to the last Set of that batch (the managed classification)
static LARGE_INTEGER g_asClsT0, g_asClsT1;
static void AsClsFlush() {
    if (g_asClsT0.QuadPart && g_asClsT1.QuadPart > g_asClsT0.QuadPart)
        ts3native_autshadow.clsUs += (LONG)((g_asClsT1.QuadPart - g_asClsT0.QuadPart) * g_asUsPerTick);
    g_asClsT0.QuadPart = g_asClsT1.QuadPart = 0;
}
static int __cdecl AsIcObjClassTake(void* arr) {
    if (AsIcIsWorker() || !arr || !g_as) return 0;
    AsClsFlush();
    const LONG cap = *(const LONG*)((const u8*)arr + 8);
    AsU64* out = (AsU64*)((u8*)arr + 16);
    while (InterlockedExchange(&g_asDirtyLock, 1)) YieldProcessor();
    LONG n = 0;
    while (n < cap && g_asClsQN) {
        const AsU64 id = g_asClsQ[--g_asClsQN];
        AsOl* o = AsOlSlot(id, false);
        if (o) o->clsQ = 0;
        // docs/122 §14.26: destroyed (transients, mostly) or classified by the host since it was queued: nothing to ask
        if (o && (o->gone || o->clsN)) { ts3native_autshadow.clsGoneSkip++; continue; }
        out[n++] = id;
    }
    InterlockedExchange(&g_asDirtyLock, 0);
    ts3native_autshadow.clsAsked += n;
    if (n) QueryPerformanceCounter(&g_asClsT0), g_asClsT1 = g_asClsT0;
    return (int)n;
}
static void __cdecl AsIcObjClassSet(DWORD lo, DWORD hi, int mask, int n) {
    if (AsIcIsWorker() || !g_as) return;
    QueryPerformanceCounter(&g_asClsT1);
    if (n <= 0) { ts3native_autshadow.clsNone++; return; }
    while (InterlockedExchange(&g_asDirtyLock, 1)) YieldProcessor();
    AsOl* o = AsOlSlot(lo | (AsU64)hi << 32, false);
    if (o && !o->gone) o->cls = (DWORD)mask, o->clsN = (u8)min(n, 32);
    InterlockedExchange(&g_asDirtyLock, 0);
    ts3native_autshadow.clsSet++;
}
static int __cdecl AsIcLastRun() { return g_asLast; }
// a new RNG unit (unit >= 0): on the worker its own stream, in the reference the host MT's; elsewhere nothing (the live
// RNG is never touched outside the reference, whose MT state is put back afterwards)
static void __cdecl AsIcRngUnit(int unit) {
    if (unit < 0) return;
    if (AsIcIsWorker()) g_asRngUnitW = unit, g_asRngLiveW = false;
    else if (g_asInRef) g_asRngPendS = unit;
}
// on != 0 opens, 0 closes a scratch-stream bracket; only inside the reference run (sim thread), elsewhere nothing
static void __cdecl AsIcRngPause(int on) {
    u8* host = g_asHostPtr ? *g_asHostPtr : NULL;
    if (!g_asInRef || !host || AsIcIsWorker()) return;
    if (on) {
        if (g_asRngPause++) return;
        memcpy(g_asRngPaused, host + AS_RNG_OFF, AS_RNG_LEN);
        g_asMtInit(host + AS_RNG_OFF, NULL, AsRngSeed(g_asRngSeedS, 0x7FFFFFFF));  // no unit id ((o << 16) | k) is this large
        ts3native_autshadow.rngPause++;
    } else if (g_asRngPause > 0 && !--g_asRngPause) {
        memcpy(host + AS_RNG_OFF, g_asRngPaused, AS_RNG_LEN);
    }
}
// Version 5 (docs/122 §15): a worker shim for a method mint transformed before ts3fixes could swap its MonoMethod header
// (Localization.LocalizeString overloads, refused "already compiled"). Callers bind the callee's RuntimeMethod pointer when
// they are transformed (docs/04 F8), so the redirect rotates the three 0x48-byte RuntimeMethod bodies in place: the stub's
// gets the target's (original code), the target's gets the hook's (every bound caller now runs the hook). MonoMethod+0x14
// = RuntimeMethod, RuntimeMethod +8 MonoMethod, +0x40 param count, +0x44 bit 0x100 = transformed (docs/133, I-186-2).
// Only with Mode 1/2 (the shim matters only on the worker), sim thread, no run in flight. Returns 0 done, -1 mode/state,
// -2 no RuntimeMethod, -3 one not transformed, -4 back pointer or param count differs.
static int __cdecl AsIcRedirect(u8* target, u8* hook, u8* stub) {
    if (g_asMode < 1 || AsIcIsWorker() || g_asRun != AS_RUN_IDLE || !target || !hook || !stub) return -1;
    u8* const rm[3] = {*(u8**)(target + 0x14), *(u8**)(hook + 0x14), *(u8**)(stub + 0x14)};
    const u8* const mm[3] = {target, hook, stub};
    for (int i = 0; i < 3; i++) {
        if (!rm[i]) return -2;
        if (!(*(DWORD*)(rm[i] + 0x44) & 0x100)) return -3;
        if (*(const u8**)(rm[i] + 8) != mm[i] || rm[i][0x40] != rm[0][0x40]) return -4;
    }
    u8 orig[0x48];
    memcpy(orig, rm[0], sizeof(orig));
    memcpy(rm[0], rm[1], sizeof(orig));
    memcpy(rm[2], orig, sizeof(orig));
    Log("autshadow: RuntimeMethod redirect 0x%p -> hook 0x%p (original body in stub 0x%p)", rm[0], rm[1], rm[2]);
    return 0;
}
static void __cdecl AsIcSetMethods(void* finish, void* reference, void* work) {
    if (AsIcIsWorker() || g_asRun != AS_RUN_IDLE) return;
    g_asMFinish = finish, g_asMRef = reference, g_asMWork = work;
}
static void __cdecl AsIcSetRefs(void* empty, void* fmtExc) {
    if (!g_as || AsIcIsWorker()) return;
    g_as->keep[0] = (DWORD)(uintptr_t)empty, g_as->keep[1] = (DWORD)(uintptr_t)fmtExc;  // rooted (AsRootOnce)
    g_asEmptyStr = empty, g_asFmtExc = fmtExc;
    if (empty) ts3native_autshadow.rooted |= 4;
    if (fmtExc) ts3native_autshadow.rooted |= 8;
}
// EA's MonoString {vtable, length, UTF-16 at +8} [verified, as AsStrKey]: one line to autshadow.jsonl
static void __cdecl AsIcLog(const DWORD* s) {
    Ts3nAutShadow& k = ts3native_autshadow;
    if (!s || k.logLines >= 100000) return;
    if (g_asJsonl == INVALID_HANDLE_VALUE) {
        if (!g_asJsonlPath[0]) return;
        g_asJsonl = CreateFileW(g_asJsonlPath, FILE_APPEND_DATA, FILE_SHARE_READ, NULL, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
        if (g_asJsonl == INVALID_HANDLE_VALUE) { g_asJsonlPath[0] = 0; return; }
    }
    const LONG len = (LONG)s[1];
    char buf[4096];
    int n = len > 0 && len < 1300 ? WideCharToMultiByte(CP_UTF8, 0, (const wchar_t*)(s + 2), len, buf, sizeof(buf) - 2, NULL, NULL) : 0;
    if (n <= 0) return;
    buf[n++] = '\n';
    DWORD w;
    WriteFile(g_asJsonl, buf, (DWORD)n, &w, NULL);
    k.logLines++;
}

static void AsRegisterIcs() {
    static const struct { const char* name; const void* fn; } kIcs[] = {
        {"Ts3Fixes.AutShadowNative::Version", (const void*)AsIcVersion},
        {"Ts3Fixes.AutShadowNative::Config", (const void*)AsIcConfig},
        {"Ts3Fixes.AutShadowNative::IsWorker", (const void*)AsIcIsWorker},
        {"Ts3Fixes.AutShadowNative::Abort", (const void*)AsIcAbort},
        {"Ts3Fixes.AutShadowNative::Aborted", (const void*)AsIcAborted},
        {"Ts3Fixes.AutShadowNative::ResetAbort", (const void*)AsIcResetAbort},
        {"Ts3Fixes.AutShadowNative::Stopped", (const void*)AsIcStopped},
        {"Ts3Fixes.AutShadowNative::Begin", (const void*)AsIcBegin},
        {"Ts3Fixes.AutShadowNative::AddRow", (const void*)AsIcAddRow},
        {"Ts3Fixes.AutShadowNative::Post", (const void*)AsIcPost},
        {"Ts3Fixes.AutShadowNative::LastRun", (const void*)AsIcLastRun},
        {"Ts3Fixes.AutShadowNative::SetMethods", (const void*)AsIcSetMethods},
        {"Ts3Fixes.AutShadowNative::SetRefs", (const void*)AsIcSetRefs},
        {"Ts3Fixes.AutShadowNative::Log", (const void*)AsIcLog},
        {"Ts3Fixes.AutShadowNative::RngUnit", (const void*)AsIcRngUnit},
        {"Ts3Fixes.AutShadowNative::RngPause", (const void*)AsIcRngPause},
        {"Ts3Fixes.AutShadowNative::Redirect", (const void*)AsIcRedirect},
        {"Ts3Fixes.AutShadowNative::Note", (const void*)AsIcNote},
        {"Ts3Fixes.AutShadowNative::ListTypeNext", (const void*)AsIcListTypeNext},
        {"Ts3Fixes.AutShadowNative::ListTypeSet", (const void*)AsIcListTypeSet},
        {"Ts3Fixes.AutShadowNative::ListTypeCount", (const void*)AsIcListTypeCount},
        {"Ts3Fixes.AutShadowNative::ListTypeAt", (const void*)AsIcListTypeAt},
        {"Ts3Fixes.AutShadowNative::ObjClassTake", (const void*)AsIcObjClassTake},
        {"Ts3Fixes.AutShadowNative::ObjClassSet", (const void*)AsIcObjClassSet},
        {"Ts3Fixes.AutShadowNative::Fresh", (const void*)AsIcFresh},
        {"Ts3Fixes.AutShadowNative::Stat", (const void*)AsIcStat},
        {"Ts3Fixes.AutShadowNative::PairT0", (const void*)AsIcPairT0},
        {"Ts3Fixes.AutShadowNative::PairT1", (const void*)AsIcPairT1},
        {"Ts3Fixes.AutShadowNative::LiveStat", (const void*)AsIcLiveStat},
        {"Ts3Fixes.AutShadowNative::LiveWatch", (const void*)AsIcLiveWatch},
        {"Ts3Fixes.AutShadowNative::ObjStale", (const void*)AsIcObjStale},
        {"Ts3Fixes.AutShadowNative::RainArm", (const void*)AsIcRainArm},
        {"Ts3Fixes.AutShadowNative::SearchMark", (const void*)AsIcSearchMark},
        {"Ts3Fixes.AutShadowNative::ImageSha", (const void*)AsIcImageSha},
        {"Ts3Fixes.AutShadowNative::ActorScope", (const void*)AsIcActorScope},
    };
    if (!g_asAddIc) return;
    for (const auto& ic : kIcs) g_asAddIc(ic.name, ic.fn);
    ts3native_autshadow.icRegistered = (LONG)(sizeof(kIcs) / sizeof(kIcs[0]));
}

// [AutShadow] Mode/Sample/WarmIn/DumpOnMismatch; the cut needs [MonoThread]'s Simulate slot stub (Enabled=1, any Mode)
static void AsInstallRun(const wchar_t* ini, bool slotStub) {
    Ts3nAutShadow& k = ts3native_autshadow;
    LARGE_INTEGER f;
    QueryPerformanceFrequency(&f);
    g_asUsPerTick = 1e6 / (double)f.QuadPart;
    const int mode = ini ? GetPrivateProfileIntW(L"AutShadow", L"Mode", 0, ini) : 0;
    g_asSample = ini ? GetPrivateProfileIntW(L"AutShadow", L"Sample", 4, ini) : 4;
    g_asWarmIn = ini ? GetPrivateProfileIntW(L"AutShadow", L"WarmIn", 20, ini) : 20;
    g_asDump = ini ? GetPrivateProfileIntW(L"AutShadow", L"DumpOnMismatch", 0, ini) : 0;
    g_asWaitUs = ini ? GetPrivateProfileIntW(L"AutShadow", L"WindowWaitUs", 0, ini) : 0;
    g_asColdIc = ini ? GetPrivateProfileIntW(L"AutShadow", L"ColdIc", 0, ini) : 0;
    g_asLean = ini ? GetPrivateProfileIntW(L"AutShadow", L"CutLean", 0, ini) : 0;  // docs/122 §14.16
    g_asTierMs = ini ? GetPrivateProfileIntW(L"AutShadow", L"TierMs", 0, ini) : 0;
    if (g_asTierMs < 0 || g_asTierMs > 60000) g_asTierMs = g_asTierMs < 0 ? 0 : 60000;
    k.tierMs = g_asTierMs;
    // docs/122 §14.17: CutLean=2 dirty lots, ListAudit list rows per cut (0-64), TierClassMs tier cache (100-10,000)
    g_asListAudit = ini ? GetPrivateProfileIntW(L"AutShadow", L"ListAudit", 1, ini) : 1;
    if (g_asListAudit < 0 || g_asListAudit > 64) g_asListAudit = g_asListAudit < 0 ? 0 : 64;
    g_asTierClassMs = ini ? GetPrivateProfileIntW(L"AutShadow", L"TierClassMs", 1000, ini) : 1000;
    if (g_asTierClassMs < 100 || g_asTierClassMs > 10000) g_asTierClassMs = g_asTierClassMs < 100 ? 100 : 10000;
    // docs/122 §14.19: RowHot=N (0 = off, else 16-100,000 cuts) object rows unread by the worker for N cuts wait cold
    g_asRowHot = ini ? GetPrivateProfileIntW(L"AutShadow", L"RowHot", 0, ini) : 0;
    if (g_asRowHot < 0 || g_asRowHot > 100000) g_asRowHot = 0;
    else if (g_asRowHot && g_asRowHot < 16) g_asRowHot = 16;
    k.rowHot = g_asRowHot;
    // docs/122 §14.21: RunSkip=1 skips clean runs of object rows whole in the refresh loop and publishes only changed runs
    g_asRunSkip = ini ? (GetPrivateProfileIntW(L"AutShadow", L"RunSkip", 0, ini) ? 1 : 0) : 0;
    k.runSkip = g_asRunSkip;
    // docs/122 §14.21: FreshEvery=N re-scores 1 in N Mode R finishes on the sim thread (stale-score check, test cost); 0 = off
    g_asFreshEvery = ini ? GetPrivateProfileIntW(L"AutShadow", L"FreshEvery", 0, ini) : 0;
    if (g_asFreshEvery < 0 || g_asFreshEvery > 1000) g_asFreshEvery = 0;
    k.freshEvery = g_asFreshEvery;
    // docs/122 §14.21: KeepPartial=1: an overrun keeps the meta objects the worker finished before the stop (ts3fixes checks
    // the stop flag after each object; an object it was inside of is aborted `stop`), instead of dropping the whole search
    g_asKeepPartial = ini ? (GetPrivateProfileIntW(L"AutShadow", L"KeepPartial", 0, ini) ? 1 : 0) : 0;
    k.keepPartial = g_asKeepPartial;
    // docs/122 §14.26 (mono-cost15): Transient=1 objects created and destroyed between two cuts mark nothing; HostCls=1 the
    // host's own list filter classifies objects at their marks and at destroy; ListWalk=1 one lot walk per cut feeds every
    // list row of the lot (ListWalkCheck=N: every Nth also against the body, 0 = never)
    g_asTransient = ini ? (GetPrivateProfileIntW(L"AutShadow", L"Transient", 0, ini) ? 1 : 0) : 0;
    g_asHostCls = ini ? (GetPrivateProfileIntW(L"AutShadow", L"HostCls", 0, ini) ? 1 : 0) : 0;
    g_asListWalk = ini ? (GetPrivateProfileIntW(L"AutShadow", L"ListWalk", 0, ini) ? 1 : 0) : 0;
    g_asListWalkCheck = ini ? GetPrivateProfileIntW(L"AutShadow", L"ListWalkCheck", 8, ini) : 8;
    if (g_asListWalkCheck < 0 || g_asListWalkCheck > 100000) g_asListWalkCheck = 8;
    k.transOn = g_asTransient, k.hcOn = g_asHostCls, k.lwOn = g_asListWalk;
    if (ini) Log("autshadow cut: RunSkip=%d Transient=%d HostCls=%d ListWalk=%d ListWalkCheck=%d (docs/122 §14.21, §14.26)", g_asRunSkip,
                 g_asTransient, g_asHostCls, g_asListWalk, g_asListWalkCheck);
    // docs/122 §14.27 (mono-cost16): SimEvRows=1 a reachable Sim's rows of the SimEvIcs ICs refresh on its object events
    // (SimEvCheck=N: every Nth kept row against the body); TierMoveLot=1 a Sim's move re-classifies it only across a lot or
    // room; ListAuditEvery=N the audit at every Nth dirty cut; GoneDelta=1 a destroy is removed from the stored list answers
    // (GoneDeltaCheck=N: every Nth delta row against the body)
    g_asSimEv = ini ? (GetPrivateProfileIntW(L"AutShadow", L"SimEvRows", 0, ini) ? 1 : 0) : 0;
    g_asSimEvCheck = ini ? GetPrivateProfileIntW(L"AutShadow", L"SimEvCheck", 8, ini) : 8;
    if (g_asSimEvCheck < 0 || g_asSimEvCheck > 100000) g_asSimEvCheck = 8;
    g_asSimEvIcs = ini ? (DWORD)GetPrivateProfileIntW(L"AutShadow", L"SimEvIcs", 0x27, ini) : 0x27;
    g_asTierMoveLot = ini ? (GetPrivateProfileIntW(L"AutShadow", L"TierMoveLot", 0, ini) ? 1 : 0) : 0;
    g_asListAuditEvery = ini ? GetPrivateProfileIntW(L"AutShadow", L"ListAuditEvery", 1, ini) : 1;
    if (g_asListAuditEvery < 1 || g_asListAuditEvery > 64) g_asListAuditEvery = g_asListAuditEvery < 1 ? 1 : 64;
    g_asGoneDelta = ini ? (GetPrivateProfileIntW(L"AutShadow", L"GoneDelta", 0, ini) ? 1 : 0) : 0;
    g_asGoneDeltaCheck = ini ? GetPrivateProfileIntW(L"AutShadow", L"GoneDeltaCheck", 8, ini) : 8;
    if (g_asGoneDeltaCheck < 0 || g_asGoneDeltaCheck > 100000) g_asGoneDeltaCheck = 8;
    k.seOn = g_asSimEv, k.tmOn = g_asTierMoveLot, k.laEvery = g_asListAuditEvery, k.gdOn = g_asGoneDelta;
    if (ini) Log("autshadow cut16: SimEvRows=%d SimEvIcs=0x%lX SimEvCheck=%d TierMoveLot=%d ListAuditEvery=%d GoneDelta=%d GoneDeltaCheck=%d (docs/122 §14.27)",
                 g_asSimEv, g_asSimEvIcs, g_asSimEvCheck, g_asTierMoveLot, g_asListAuditEvery, g_asGoneDelta, g_asGoneDeltaCheck);
    // docs/122 §14.28 (mono-cost17): IdsDelta=1 list rows answered from their stored answer when the walk ids are the same or
    // the same plus this cut's new objects (IdsDeltaCheck=N: every Nth against the full host call); SimEvLearn=1 / SimEvSeated=1
    // SimEvRows for promoted Sims / Sims on a non-Sim root parent (SimSeatIcs); TierClassMax=N classifications a cut;
    // GoneLoose=1 a destroy record naming another lot or room still drops the element from array rows
    g_asIdsDelta = ini ? (GetPrivateProfileIntW(L"AutShadow", L"IdsDelta", 0, ini) ? 1 : 0) : 0;
    g_asIdsDeltaCheck = ini ? GetPrivateProfileIntW(L"AutShadow", L"IdsDeltaCheck", 8, ini) : 8;
    if (g_asIdsDeltaCheck < 0 || g_asIdsDeltaCheck > 100000) g_asIdsDeltaCheck = 8;
    g_asSimEvLearn = ini ? (GetPrivateProfileIntW(L"AutShadow", L"SimEvLearn", 0, ini) ? 1 : 0) : 0;
    g_asSimEvSeated = ini ? (GetPrivateProfileIntW(L"AutShadow", L"SimEvSeated", 0, ini) ? 1 : 0) : 0;
    g_asSimSeatIcs = ini ? (DWORD)GetPrivateProfileIntW(L"AutShadow", L"SimSeatIcs", 0x25, ini) : 0x25;
    g_asTierClassMax = ini ? GetPrivateProfileIntW(L"AutShadow", L"TierClassMax", 0, ini) : 0;
    if (g_asTierClassMax < 0 || g_asTierClassMax > 4096) g_asTierClassMax = 0;
    g_asGoneLoose = ini ? (GetPrivateProfileIntW(L"AutShadow", L"GoneLoose", 0, ini) ? 1 : 0) : 0;
    k.idOn = g_asIdsDelta, k.seLearnOn = g_asSimEvLearn, k.seSeatOn = g_asSimEvSeated, k.tcMax = g_asTierClassMax;
    if (ini) Log("autshadow cut17: IdsDelta=%d IdsDeltaCheck=%d SimEvLearn=%d SimEvSeated=%d SimSeatIcs=0x%lX TierClassMax=%d GoneLoose=%d (docs/122 §14.28)",
                 g_asIdsDelta, g_asIdsDeltaCheck, g_asSimEvLearn, g_asSimEvSeated, g_asSimSeatIcs, g_asTierClassMax, g_asGoneLoose);
    // docs/216 rows 1 and 5 (0.36.147): PreLearn=1 learned parent/GetTask rows for objects placed on listed or scored lots;
    // TierReqLots=1 tier-2 Sims on the request's scored lots refresh at the cut
    g_asPreLearn = ini ? (GetPrivateProfileIntW(L"AutShadow", L"PreLearn", 0, ini) ? 1 : 0) : 0;
    g_asTierReqLots = ini ? (GetPrivateProfileIntW(L"AutShadow", L"TierReqLots", 0, ini) ? 1 : 0) : 0;
    if (ini) Log("autshadow r7: PreLearn=%d TierReqLots=%d (docs/216 row, simstale)", g_asPreLearn, g_asTierReqLots);
    g_asPrintUsPerTick = g_asUsPerTick;
    if (g_asWaitUs < 0 || g_asWaitUs > 20000) g_asWaitUs = g_asWaitUs < 0 ? 0 : 20000;  // a test knob: never more than 20 ms per frame
    if (ini) {
        wcscpy_s(g_asJsonlPath, ini);
        wchar_t* slash = wcsrchr(g_asJsonlPath, L'\\');
        if (slash) wcscpy_s(slash + 1, MAX_PATH - (slash + 1 - g_asJsonlPath), L"autshadow.jsonl");
        else g_asJsonlPath[0] = 0;
    }
    g_asMode = slotStub ? (mode < 0 || mode > 3 ? 0 : mode) : -1;
    k.mode = g_asMode;
    // docs/124 §17 go-live keys (Mode 3 only; clamped so a typo cannot turn the consumer into a full re-score)
    if (ini && g_asMode == 3) {
        g_asLiveTopN = GetPrivateProfileIntW(L"AutShadow", L"TopN", 10, ini);
        g_asLiveBands = GetPrivateProfileIntW(L"AutShadow", L"Bands", 3, ini);
        g_asLiveMarginPm = GetPrivateProfileIntW(L"AutShadow", L"Margin", 0, ini);
        g_asLiveMaxWait = GetPrivateProfileIntW(L"AutShadow", L"MaxWait", 3, ini);
        g_asLiveVerify = GetPrivateProfileIntW(L"AutShadow", L"VerifyEvery", 50, ini);
        g_asLiveDry = GetPrivateProfileIntW(L"AutShadow", L"Dry", 0, ini) ? 1 : 0;
        g_asLiveTopN = g_asLiveTopN < 1 ? 1 : g_asLiveTopN > 64 ? 64 : g_asLiveTopN;
        g_asLiveBands = g_asLiveBands < 1 ? 1 : g_asLiveBands > 8 ? 8 : g_asLiveBands;
        g_asLiveMarginPm = g_asLiveMarginPm < 0 ? 0 : g_asLiveMarginPm > 1000 ? 1000 : g_asLiveMarginPm;
        g_asLiveMaxWait = g_asLiveMaxWait < 1 ? 1 : g_asLiveMaxWait > 16 ? 16 : g_asLiveMaxWait;
        g_asLiveVerify = g_asLiveVerify < 0 ? 0 : g_asLiveVerify > 100000 ? 100000 : g_asLiveVerify;
        Log("autshadow go-live: Mode=3 TopN=%d Bands=%d Margin=%d/1000 MaxWait=%d VerifyEvery=%d Dry=%d (docs/124 §17)", g_asLiveTopN,
            g_asLiveBands, g_asLiveMarginPm, g_asLiveMaxWait, g_asLiveVerify, g_asLiveDry);
        g_asLiveUnseen = slotStub && GetPrivateProfileIntW(L"AutShadow", L"Unseen", 0, ini) ? 1 : 0;
        if (g_asLiveUnseen) Log("autshadow go-live: Unseen=1, ts3fixes switches autshadow, autlive, autunseen on by ini (docs/222)");
    }
    k.alOn = g_asMode == 3 ? 1 | g_asLiveDry << 1 : 0;
    // docs/206 §7: Satb=1 probe (S0 store counts in one job length of the next tick, no concurrency), 2 act (the worker runs
    // concurrently with the next tick and reads the cut through the log); needs Mode >= 1 and the worker (default 0)
    const int satb = ini ? GetPrivateProfileIntW(L"AutShadow", L"Satb", 0, ini) : 0;
    if (satb > 0 && slotStub && g_asMode >= 1) {
        g_satbRtSwapped = SatbInstallRt();
        const int m = SatbInstall(satb);
        Log("%s autshadow satb: Satb=%d -> mode %d (%s), %d runtime icall entries wrapped, %d EA ICs with byref/array arguments logged per argument, %d cold-path detours, %d native-writer detours (act: binder bodies, static helpers; docs/206 §15)",
            m > 0 && g_satbRtSwapped > 0 ? "OK  " : "FAIL", satb, m, m == SATB_ACT ? "act: concurrent worker, read redirect" :
            m == SATB_PROBE ? "probe: store counts, no concurrency" : "off", g_satbRtSwapped, g_satbIcMapped, g_satbColdN, g_satbNativeN);
    }
    g_satbWorkerBusy = AsSatbBusy;
    if (!slotStub) return;
    g_mtOnEnter = AsSimEnter;
    g_mtOnLeave = AsSimLeaveLoop;
    g_mtJobHook = AsJobHook;
    if (ini) SimPumpAdd(AsSimPump, "autshadow cut + worker dispatch (window = frame limiter)");  // game only: patches 0xFD97C8
}
