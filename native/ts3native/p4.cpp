// [RouteSnap] Mode=1: routing snapshot P4 part A (docs/94 §12, §13, §17, §18; counters v50). Included by ts3native.cpp after
// snap.cpp. Everything of the option G redirect that does not depend on the clone's internals:
//   - T1-T4 (§12.2): the A* stamp add 0x61E360 (T1, inside [RouteGate] site 8's trampoline when that section owns it), the
//     stamp loads 0x61E030 (T2) and 0x61CC73 (T2', dead code), the clip scratch load 0x66AC37 (T3) and the routing world
//     root RoutingWorld_GetImpl 0x836200 (T4). Each thunk compares the TEB thread id (fs:[0x24]) with g_snapRunTid: the
//     clone-running worker gets the copy's value from g_snapCtx, every other thread the game's global.
//   - door answers (§12.5): a stub at the call 0x64F4D3 -> World_FirePortalLockedQuery 0x791C00; chained behind
//     [ProbeValues]' stub there when that section owns the site (one owner, docs/68 rule 3). The live job records
//     (9 argument dwords -> eax); the clone run replays them and never calls the managed side.
//   - per-job capture (§12.3.1) at the live Route_PlanLocked on the worker (PlanLockedHook -> P4LivePlan): deep copies of
//     the start and goal vectors and the options object, the answer (paths, nodes), the door answers, the D5 window counters.
//   - the shadow (§12.4.2) after J_DONE: checks, clone acquire (provider below), arm with the D2 handshake, the game's
//     plan on the copy under __try, disarm, compare, one JSON line (§13 v2), release. The game's answer is always kept.
//   - fallbacks (§12.6, §17.4) with counters; an exception in a clone run turns P4 off for the session.
// The clone itself (P3-B, design B ring + full clone, §11.4) plugs in through P4CloneProvider. None is registered in this
// build: every shadow is refused "clone" (M4) and no clone run happens.
// shortcut: the thunks stay installed for the whole session (they serve the live globals while no clone run is armed); the
// §12.2.3 uninstall at world close is not built, add it when an unload or a Mode switch at run time is wanted.

// P5 ([RouteSnap] Mode=2, p5.cpp; docs/94 §19, §22): the job's Route_PlanLocked takes the clone run's answer
static void P5OnStart(struct AsyncJob* j);
static void P6JobBegin(const struct AsyncJob* j);  // p5.cpp: P6 phase split (docs/94 §24.7), worker
static void P6JobEnd();
static u8 P5Plan(void* world, void* edx, int* outList, int* starts, int* goals, DWORD agent, DWORD doPlan, u8* opts, int* out1,
                 int* out2, DWORD guidLo, DWORD guidHi);
static void ApplyP5(const wchar_t* ini);

// --- the clone provider interface (P3-B) ---
struct P4Clone { void* world; DWORD ver; DWORD epoch; LONG fresh; };  // fresh: every node stamp was reset to 0 (materialised)
enum { P4REL_WRAP = 1, P4REL_DISCARD = 2, P4REL_LAZY = 4 };  // LAZY (0.35.21): a lazy stop whose writes are all in the dirty list
struct P4CloneProvider {
    DWORD (*cut)();                            // sim thread, async START: version of the cut (0 = none); §12.4.1 step 1
    int (*acquire)(DWORD ver, P4Clone* out);   // worker, before a clone run: -1 = ok, else a P4_AB_* reason (epoch, mat, ...)
    void (*release)(const P4Clone* c, LONG flags, const unsigned __int64* dirty, int nDirty);  // nDirty -1 = every lot
    void (*drop)(DWORD ver);                   // worker: the job of cut `ver` ends its shadow without an acquire (0.33.1)
};
// refusal detail for the §13 record's "sub" (0.33.1, docs/94 §21.14): which check of the cut, the acquire or the shadow refused
enum { PS_NONE, PS_BUSY, PS_OFF, PS_PORTAL, PS_NOCLONE, PS_P3, PS_COPY, PS_TIMEOUT, PS_STALE, PS_PASSED, PS_REPLACED, PS_MOVED,
       PS_SHADOW, PS_QUEUED, PS_LOCK, PS_HELD, PS_N };
static const char* const kPcSub[PS_N] = {"", "busy", "off", "portal", "noclone", "p3", "copy", "timeout", "stale", "passed",
                                          "replaced", "moved", "shadow", "queued", "lock", "held"};
static int g_pcCutWhy;  // sim: the last cut's PS_* (0 = cut made)
static LONG g_pcCutW;    // sim: g_spWrites at the last cut, read under its +0x918 hold (0.35.6; a shared cut: that cut's)
static int g_pcAcqWhy;  // worker: the last acquire's PS_*
static const P4CloneProvider* g_p4Clone;  // NULL: unavailable (this build)
// external linkage: unreferenced until P3-B calls it (no C4505)
void P4SetCloneProvider(const P4CloneProvider* p) {
    g_p4Clone = p;
    if (p && ts3native_counters.snP4Ready) ts3native_counters.snP4Ready |= 16;
}
static DWORD P4CloneCut() { return g_p4Clone && g_p4Clone->cut ? g_p4Clone->cut() : 0; }
#ifdef TS3N_FAULTINJ
// docs/157: fault injection for P5 / P5b / P5c, test builds only (selftest.cpp defines it; a live probe build sets CL=/DTS3N_FAULTINJ).
// [RouteSnap] FaultInject = mask, FaultInjectEvery = N: every Nth armed Mode=2 plan injects the next mask bit in turn
// (FI_EAHEAP only modifies FI_AV). Live builds read only FI_LIVE; the rest change shared state and are selftest only.
enum { FI_OOM = 1, FI_MANAGED = 2, FI_AV = 4, FI_EAHEAP = 8, FI_STALE = 16, FI_CLOSE = 32, FI_BUDGET = 64 };
static const LONG FI_LIVE = FI_OOM | FI_MANAGED | FI_AV | FI_EAHEAP, FI_TURN = FI_OOM | FI_MANAGED | FI_AV | FI_STALE | FI_CLOSE | FI_BUDGET;
static LONG g_fiMask, g_fiEvery = 1, g_fiN, g_fiTurn;  // worker only (one Mode=2 plan at a time)
static LONG g_fiPlan;                                   // the bits this plan injects (P5Plan sets and clears)
static LONG g_fiBudgetOn = -1;                          // FI_BUDGET: t_budget.on seen inside the clone run
#endif
static int P4CloneAcquire(DWORD ver, P4Clone* out) {
    if (!g_p4Clone) return P4_AB_CLONE;
    if (!ver) return P4_AB_NOVER;
    const int r = g_p4Clone->acquire(ver, out);
#ifdef TS3N_FAULTINJ
    if (r < 0 && (g_fiPlan & FI_OOM)) {  // the copy failed after the hold (PcAcquire's RpAlloc NULL -> ok false -> PS_COPY)
        g_fiPlan &= ~FI_OOM;
        g_p4Clone->release(out, 0, NULL, 0);
        Log("FaultInject: OOM in the clone build (acquire refused as mat)");
        return P4_AB_MAT;
    }
#endif
    return r;
}
static void P4CloneRelease(const P4Clone* c, LONG flags, const unsigned __int64* dirty, int nDirty) {
    if (g_p4Clone) g_p4Clone->release(c, flags, dirty, nDirty);
}
static void P4CloneDrop(DWORD ver) {
    if (g_p4Clone && g_p4Clone->drop && ver) g_p4Clone->drop(ver);
}

// Hook for the private pools (docs/94 §16.6 P4 note; pools.cpp is a separate build): set these to switch the A*, goal and
// cell pools to per-job private ones around each clone run (on the worker, g_snapRunTid already set). NULL = no pools build:
// the clone run uses the game's pools as part A did.
void (*g_p4PoolsEnterJob)() = NULL;
void (*g_p4PoolsLeaveJob)() = NULL;

// --- state the thunks read (fixed offsets: asm below) ---
struct SnapWork { DWORD stamp; LONG wrap; };       // per working copy: +0 stamp (u16 in a dword), +4 wrapped
struct SnapWorker { u8 clip[0x20]; LONG init; };   // per worker thread: +0 the clip scratch object
struct SnapCtx { void* world; SnapWork* work; SnapWorker* worker; };  // +0 T4, +4 T1/T2, +8 T3
static SnapWork g_p4Work;
static SnapWorker g_p4Worker;

// the game's globals (selftest points them at its own memory)
static DWORD* g_p4StampLive = (DWORD*)(uintptr_t)TS3A(0x011580C4);
static DWORD g_p4ClipLive = TS3A(0x01158580);
static const DWORD* g_p4WorldSlot = (const DWORD*)(uintptr_t)TS3A(0x011D9128);

// T1: `add word [stamp], 1` (8 bytes) -> `call SnStampAdd; nop x3`. Flags are not kept (next writer `or ecx,-1`, §12.2.1).
static __declspec(naked) void SnStampAdd() {
    __asm {
        push eax
        mov eax, dword ptr fs:[24h]
        cmp eax, g_snapRunTid
        jne live
        mov eax, g_snapCtx
        mov eax, [eax + 4]
        add word ptr [eax], 1
        jnz done
        mov word ptr [eax], 1  // wrapped: the copy restarts at 1; every lot is rematerialised after this job (§12.3.1)
        mov dword ptr [eax + 4], 1
        lock inc ts3native_counters.snStampWrap
        jmp done
    live:
        mov eax, g_p4StampLive
        add word ptr [eax], 1
    done:
        pop eax
        ret
    }
}
// T2: `mov eax, [stamp]` (5 bytes) -> `call SnStampLoad`; eax only, flags free (`sub esp,0x10` follows)
static __declspec(naked) void SnStampLoad() {
    __asm {
        mov eax, dword ptr fs:[24h]
        cmp eax, g_snapRunTid
        jne live
        mov eax, g_snapCtx
        mov eax, [eax + 4]
        mov eax, [eax]
        ret
    live:
        mov eax, g_p4StampLive
        mov eax, [eax]
        ret
    }
}
// T2' (dead in 1.67, docs/109 §2): as T2 plus a counter that must stay 0; flags free (`cmp ax,[esi+0x30]` follows)
static __declspec(naked) void SnStampLoadDead() {
    __asm {
        lock inc ts3native_counters.snDeadStamp
        jmp SnStampLoad
    }
}
// T3: `mov ecx, 0x1158580` -> `call SnClipEcx`; ecx only: eax, edx and the flags are kept
static __declspec(naked) void SnClipEcx() {
    __asm {
        pushfd
        push eax
        mov eax, dword ptr fs:[24h]
        cmp eax, g_snapRunTid
        jne live
        mov ecx, g_snapCtx
        mov ecx, [ecx + 8]
        jmp done
    live:
        mov ecx, g_p4ClipLive
    done:
        pop eax
        popfd
        ret
    }
}
// T4: the body `mov eax, [0x11D9128]; ret` -> `jmp SnWorldGet; int3`
static __declspec(naked) void SnWorldGet() {
    __asm {
        mov eax, dword ptr fs:[24h]
        cmp eax, g_snapRunTid
        jne live
        mov eax, g_snapCtx
        mov eax, [eax]
        ret
    live:
        mov eax, g_p4WorldSlot
        mov eax, [eax]
        ret
    }
}

enum { P4_T1, P4_T2, P4_T2D, P4_T3, P4_T4, P4_SITES };
static DWORD g_p4At[P4_SITES] = {TS3A(0x0061E360), TS3A(0x0061E030), TS3A(0x0061CC73), TS3A(0x0066AC37), TS3A(0x00836200)};
static const u8 kP4Len[P4_SITES] = {8, 5, 5, 5, 6};
static const u8 kP4Pro[P4_SITES][8] = {
    {0x66, 0x83, 0x05, 0xC4, 0x80, 0x15, 0x01, 0x01},  // add word [0x11580C4], 1
    {0xA1, 0xC4, 0x80, 0x15, 0x01},                    // mov eax, [0x11580C4]
    {0xA1, 0xC4, 0x80, 0x15, 0x01},                    // mov eax, [0x11580C4]
    {0xB9, 0x80, 0x85, 0x15, 0x01},                    // mov ecx, 0x1158580
    {0xA1, 0x28, 0x91, 0x1D, 0x01, 0xC3},              // mov eax, [0x11D9128]; ret
};
static void* const kP4Thunk[P4_SITES] = {(void*)SnStampAdd, (void*)SnStampLoad, (void*)SnStampLoadDead, (void*)SnClipEcx, (void*)SnWorldGet};

// All five or none: every site's bytes are checked first (T1 in `t1Tramp`, [RouteGate] site 8's relocated copy, when that
// section owns 0x61E360). Returns the snP4Ready bits (1, | 2 with the trampoline) or 0.
static LONG P4PatchSites(const DWORD* at, u8* t1Tramp) {
    char hex[64];
    for (int i = 0; i < P4_SITES; i++) {
        const u8* p = i == P4_T1 && t1Tramp ? t1Tramp : (const u8*)(uintptr_t)at[i];
        if (Matches(p, kP4Pro[i], kP4Len[i])) continue;
        if (Readable(p, kP4Len[i])) Hex(p, kP4Len[i], hex, sizeof(hex)); else strcpy_s(hex, "<unreadable>");
        Log("SKIP P4: site T%d 0x%08lX bytes %s differ%s, nothing patched", i, at[i], hex, i == P4_T1 && t1Tramp ? " (route gate trampoline)" : "");
        return 0;
    }
    for (int i = 0; i < P4_SITES; i++) {
        u8* p = i == P4_T1 && t1Tramp ? t1Tramp : (u8*)(uintptr_t)at[i];
        u8 b[8];
        memset(b, 0x90, sizeof(b));
        b[0] = i == P4_T4 ? 0xE9 : 0xE8;
        *(LONG*)(b + 1) = (LONG)((u8*)kP4Thunk[i] - (p + 5));
        if (i == P4_T4) b[5] = 0xCC;
        if (!Write(p, b, kP4Len[i])) { Log("FAIL P4: write at 0x%p (err %lu); earlier sites stay patched (they serve live values)", p, GetLastError()); return 0; }
    }
    return 1 | (t1Tramp ? 2 : 0);
}

// --- per-job state (one route worker: one job in capture or shadow at a time) ---
struct P4Path { DWORD type, status; unsigned __int64 obj; float len; int n0, nn; };
struct P4Node { float x, y, z, seg; DWORD flags; };
// 0.33.1: 16 paths / 128 doors refused 166 / 1 of 4,001 BP plans ("capture" / "door-over"): every capture refusal had 16 paths
static const int kP4Paths = 64, kP4Nodes = 4096, kP4Doors = 512, kP4Dirty = 64;
static const DWORD kP4OptSz = 0x80;  // the route options object (docs/94 §18.7)
static const int kP4Fam = 9;  // D5 writer families: physics, terrain swap, destroy, lot index rebuild, events 0x0D-0x10, blend, lock keys, display level, portal ICs
struct P4Ans { bool valid; u8 ok; int out1, out2, nPaths, nNodes; P4Path p[kP4Paths]; P4Node n[kP4Nodes]; };
struct P4Door { DWORD a[9]; DWORD eax; LONG used; };
struct P4DdF { unsigned __int64 key; u8 f8f, f8c; };  // [RouteSnap] DoorDump (p4door.h): one lot's +0x8F / +0x8C
static const int kP4DdLots = 256;
struct P4Job {
    LONG armed, seen;  // armed at START and claimed by the worker; seen = the live Route_PlanLocked was captured
    int kind, abort;   // abort: first P4_AB_* that voids the compare, -1 = none
    LONG borrow;       // P6 (0.36.41): the job's Mode=2 cut was borrowed (AsyncJob::p6Borrow)
    int sub;           // PS_* detail of the refusal (0.33.1)
    LONG w0; bool stale;  // g_spWrites at the cut; routing writes landed between the cut and the live plan (0.33.2)
    LONG wPlan;           // g_spWrites when the live plan began (0.35.4: the obstacle record's liveW)
    int mode, nAsk;    // AsyncJob::p4 (1 shadow, 2 P5 take, 3 P5 take + verify); P5 door asks of this job's clone run
    DWORD seq, ver, epoch;
    unsigned short x87;
    unsigned int mxcsr;
    LONG gameUs, jobUs, liveRead, cloneRead, nr0[kP4Fam];
    // args (deep copies, private heap): starts/goals/option vectors, the options object, the out list tail words
    DWORD starts[4], goals[4], agent, doPlan, guidLo, guidHi, outTail[2];
    u8 opts[0x80];
    void* bufs[8];
    int nBufs;
    P4Door doors[kP4Doors];
    int nDoors;
    unsigned __int64 dirty[kP4Dirty];
    int nDirty;  // -1 = every lot
    P4DdF dd[kP4DdLots];  // DoorDump: every lot's +0x8F right after the live plan (p4door.h)
    int ddN;
};
static void P4DdPlan(P4Job& c, const void* world);
static P4Job g_p4Job;
static P4Ans g_p4Live, g_p4Copy;
static __declspec(thread) LONG t_p4DoorRec;  // the worker is inside the live Route_PlanLocked: record door answers
static HANDLE g_p4Heap;
static LONG g_p4Every = 1, g_p4Kinds = 15;
static char g_p4WorldTag[16] = "?";
static HANDLE g_p4Jsonl = INVALID_HANDLE_VALUE;
static char* g_p4Buf;
static const size_t kP4BufCap = 1 << 20;
static DWORD g_p4StartN;  // sim thread only
static const char* const kP4Ab[P4_AB_N] = {"clone", "nover", "epoch", "cold", "unsupported", "capture", "nrbusy", "mat",
                                            "door-miss", "door-over", "managed", "event", "livelock", "lazy", "fault", "other"};
static const char* const kP4Ic[4] = {"Plan", "Replan", "PlanFromPoint", "ReplanFromPosition"};
// docs/101 §2.2 #12, #14, and #27's guard dword (bit 0, identity matrix of 0x80D8C0) (selftest: own memory)
static DWORD g_p4Guards[4] = {kPvGuards[0], kPvGuards[1], kPvGuards[2], TS3A(0x011D9060)};

// tripwires and abort reasons from the stubs (worker only: a clone run, or the live capture for door-over)
static void P4Abort(int why) {
    P5bAbort();  // P5b (p5b.cpp): a lent-out +0x918 back before the tripwire's call goes on
    Ts3nCounters& k = ts3native_counters;
    if (why == P4_AB_MANAGED) InterlockedIncrement(&k.snCloneManaged);
    else if (why == P4_AB_EVENT) InterlockedIncrement(&k.snCloneEvent);
    else if (why == P4_AB_LIVELOCK) {
        InterlockedIncrement(&k.snCloneLiveLock);
        if (k.snCloneLiveLock <= 20) Log("P4: live +0x918 acquire from a clone run (design breach: the copy reached live routing data)");
    } else if (why == P4_AB_LAZY) InterlockedIncrement(&k.snCloneLazy);
    if (g_p4Job.abort < 0) g_p4Job.abort = why;
}
// D4 (docs/94 §17.4, H20c 0.30.0): a lazy LotGraph_Build (gate site 3) or LotTable_Insert (PV_LAZY0) in a clone run would grow
// the clone's private vectors with the game's allocator (EA-free of an RpAlloc block) or write portals into them, so the run
// stops before the call: this exception unwinds to P4Guarded, which ends the run as "lazy" and discards the clone. Only from
// a clone run (callers test t_snapRun).
static const DWORD kP4LazyCode = 0xE0503401;
static LONG g_p4LazyKeep;  // worker: the stop was P4LazyKeep (one clone run at a time)
static void P4Lazy() {
    P4Abort(P4_AB_LAZY);
    RaiseException(kP4LazyCode, 0, 0, NULL);
}
// 0.35.21 (docs/94 §22.24, I-94-484): a stop whose call path is audited: every clone write before it is in the run's dirty list
// (the caller marked the stopped writer's lot first), so the release rebases those lots instead of rebuilding the clone. The
// state the unwind leaves in other clone lots (quadtree pins +0x48) is found and forced by PcRelease
static void P4LazyKeep() {
    g_p4LazyKeep = g_rpLazyKeep;  // [RouteSnap] LazyKeep (0.35.22: off by default after the 0.35.21 crash, until a run proves it)
    P4Lazy();
}

// H20 tripwire (docs/94 §21.8.1 risk 1, 0.31.3): IC_RouteManager_AddPortalLocationVariation 0x74D3F0 grows the portal's
// variation vector +0x5C (EA free of the old buffer). Static reads say its portal is always one made after the cut, so never
// one a clone copy holds; a call during a clone run on a portal in the clone's `prefs` is counted and logged, never stopped
// (non-zero = put D2 on that slot). The handle resolves as the IC does: script host 0x11D4BBC slot +0xCC, then the object's
// type test +0xC with the portal id (0x64E8B0 returns the object or NULL). Selftests swap g_p4VarPortal.
typedef u8* (__fastcall* P4ObjArgFn)(void* self, void* edx, DWORD arg);  // thiscall, one stack argument
static u8* P4VarPortalGame(DWORD h) {
    if ((h & 0xFF000000) != 0x28000000) return NULL;  // the IC's own handle type check (0x74D437)
    u8* host = *(u8* volatile*)(uintptr_t)TS3A(0x011D4BBC);
    if (!host) return NULL;
    u8* obj = ((P4ObjArgFn)(uintptr_t)RpD(RpP(host) + 0xCC))(host, NULL, h);
    return obj ? ((P4ObjArgFn)(uintptr_t)RpD(RpP(obj) + 0xC))(obj, NULL, 0xF0464184) : NULL;
}
static u8* (*g_p4VarPortal)(DWORD) = P4VarPortalGame;
static volatile LONG g_p4VarLog;
// the run holds g_rpUse, so the clone does not change under it; at the run's end the consumer may free it, so reads fault-guarded
static bool P4CloneHolds(const u8* pt) {
    __try {
        const RpClone* c = g_rpClone;
        for (int l = 0; c && l < c->nLots; l++)
            for (int j = 0; j < c->lots[l].nPrefs; j++) if (c->lots[l].prefs[j] == pt) return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {}
    return false;
}
static void P4VarTrip(DWORD handle) {
    if (!SnLive() || !g_rpClone) return;
    const u8* pt = g_p4VarPortal(handle);  // no guard: the lookup takes the handle lock, and the IC runs the same lookup next
    if (!pt || !P4CloneHolds(pt) || !SnLive()) return;
    InterlockedIncrement(&ts3native_counters.p4VarTrip);
    if (InterlockedIncrement(&g_p4VarLog) <= 20)
        Log("P4 H20 tripwire: AddPortalLocationVariation on portal 0x%08lX (handle 0x%08lX) that the clone holds, during a clone run",
            (DWORD)(uintptr_t)pt, handle);
}

static void P4WorkerDirty(bool known, unsigned __int64 key) {
    Ts3nCounters& k = ts3native_counters;
    InterlockedIncrement(&k.snWorkerDirty);
    P4Job& c = g_p4Job;
    if (c.nDirty < 0) return;
    if (!known || c.nDirty == kP4Dirty) { c.nDirty = -1; InterlockedIncrement(&k.snDirtyOver); return; }
    for (int i = 0; i < c.nDirty; i++) if (c.dirty[i] == key) return;
    c.dirty[c.nDirty++] = key;
}

// D2 (§17.4): a sim-side non-routing writer publishes itself, then reads SnLive(); a live clone run makes it step back and
// wait for the run to end. The worker sets g_snapRunTid first and then waits for g_snNrIn == 0 (P4Arm), so a writer either
// finished before the clone's first read or waits. Interlocked operations are full fences on x86.
static volatile LONG g_p4NrQtLog;
static void P4NrEnter() {
    for (;;) {
        InterlockedIncrement(&g_snNrIn);
        if (!SnLive()) return;
        // this thread holds g_qtCs (a D3 mutator frame): the clone run's physics readers would wait for us, so no wait here
        if ((DWORD)(uintptr_t)g_qtCs.OwningThread == GetCurrentThreadId()) {
            if (InterlockedIncrement(&g_p4NrQtLog) <= 20) Log("P4 D2: a writer inside a physics mutator did not wait for the clone run");
            return;
        }
        InterlockedDecrement(&g_snNrIn);
        RcDrop();  // docs/177 §7: the clone run may need +0x918 back (door ask, tripwire) before it ends
        LARGE_INTEGER t0;
        QueryPerformanceCounter(&t0);
        while (SnLive()) Sleep(0);
        InterlockedIncrement(&ts3native_counters.snNrWaits);
        InterlockedExchangeAdd(&ts3native_counters.snNrWaitUs, UsSince(t0));
    }
}

static const LONG kP4ArmWaitUs = 250000;
// worker: select the copy for this thread, then wait for in-flight non-routing writers (D2). false: one stayed past the limit.
// waitNr false (P5, a clone run inside the job): every D2 writer waits for the running job after its D2 entry (RtWaitJob, or
// the gate's +0x918 wait the worker holds), so one in flight cannot write before the job ends and waiting would only stall
static bool P4Arm(SnapCtx* ctx, bool waitNr = true) {
    t_snapRun = 1;
    g_snapCtx = ctx;
    InterlockedExchange(&g_snapRunTid, (LONG)GetCurrentThreadId());
    LARGE_INTEGER t0;
    QueryPerformanceCounter(&t0);
    bool ok = true;
    while (waitNr && InterlockedCompareExchange(&g_snNrIn, 0, 0)) {
        if (UsSince(t0) > kP4ArmWaitUs) { ok = false; break; }
        Sleep(0);
    }
    InterlockedExchangeAdd(&ts3native_counters.snNrArmWaitUs, UsSince(t0));
    return ok;
}
static void P4Disarm() {
    InterlockedExchange(&g_snapRunTid, 0);
    g_snapCtx = NULL;
    t_snapRun = 0;
}

// D5: the seven writer families' counts now (physics, terrain swaps, destroys, lot index rebuilds, world events
// 0x0D-0x10, blend reloads, lock-key ICs)
static void P4NrSnap(LONG* out) {
    const Ts3nCounters& k = ts3native_counters;
    out[0] = g_nrN[0];
    out[1] = k.vfSwaps;
    out[2] = g_nrN[2];
    out[3] = g_nrN[3];
    out[4] = k.snWev[0x0D] + k.snWev[0x0E] + k.snWev[0x0F] + k.snWev[0x10];
    out[5] = k.snLeaf[SL_BLEND][0] + k.snLeaf[SL_BLEND][1] + k.snLeaf[SL_BLEND][2];
    out[6] = k.snIc[0] + k.snIc[1] + k.snIc[2] + k.snIc[3];
    out[7] = g_nrN[7];  // display level writers (docs/94 §17.9 N15, 0.29.0)
    // portal ICs (H20d, 0.30.0): AddPortal .. SetPortalLockStatus, routesnap.cpp IC slots 4-14. The clone holds live portal
    // objects (cost +0x2C, age/gender flags +0x78/+0x7C, variation vector +0x5C) that these write on the sim
    LONG portal = 0;
    for (int i = 4; i <= 14; i++) portal += k.snIc[i];
    out[8] = portal;
}
// a family counts only if the plan called one of its readers in either run (destroys, events, blend and lock keys have no
// reader stub: always counted; the display level counts only when the plan ran the planner's vertical ray 0x6583E0)
static LONG P4NrBits(const LONG* nr0, const LONG* nr1, LONG read) {
    static const LONG kNeed[kP4Fam] = {P4R_PHYS, P4R_TERRAIN, 0, P4R_LOTIDX, 0, 0, 0, P4R_VRAY, 0};
    LONG bits = 0;
    for (int f = 0; f < kP4Fam; f++)
        if (nr1[f] != nr0[f] && (!kNeed[f] || (read & kNeed[f]))) bits |= 1 << f;
    return bits;
}

// --- door answers at 0x64F4D3 (cdecl, 9 argument dwords, `add esp,0x24` after the call [verified]) ---
typedef DWORD(__cdecl* P4DoorFn)(DWORD, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD);
static void* g_p4DoorJmp;          // World_FirePortalLockedQuery 0x791C00 (selftest: a fake)
static volatile DWORD g_p4DoorAns; // the answer the stub returns (worker only)
static volatile LONG g_p4DoorMissLog;

// the replay key (docs/94 §18.7, ledger §78): args 1-6 whole (lot, Sim and portal owner ids, 64-bit each), args 7-8 room
// indices (low word), arg 9 initialLocked (low byte). The other bits of 7-9 are register leftovers (a lot table slot index
// on a hit, heap bytes on a miss) that differ between the live and the clone run [verified disassembly, master §18.7].
static void P4DoorKey(const DWORD* args, DWORD* key) {
    for (int i = 0; i < 6; i++) key[i] = args[i];
    key[6] = args[6] & 0xFFFF, key[7] = args[7] & 0xFFFF, key[8] = args[8] & 0xFF;
}

// slot = the call's return slot: slot[1..9] = the arguments. 0 = pass through to the target, 1 = return g_p4DoorAns.
static int __cdecl P4DoorEnter(DWORD* slot) {
    Ts3nCounters& k = ts3native_counters;
    P4Job& c = g_p4Job;
    if (t_snapRun && c.mode >= 2) {
        // P5 (docs/94 §19.1 (b), §22.2): the game's own chain answers. The ask runs outside the clone run (selection off, pools
        // back to the game's), so its managed call is proxied to the sim thread as in any A10 job (ForCaller) and a plan
        // nested in that call plans on the live world; the clone run re-arms after it
        SnapCtx* ctx = (SnapCtx*)g_snapCtx;
        const LONG read = t_p4Read;
        P5bAsk(true);  // P5b (p5b.cpp): the ask runs under the job's +0x918, as in A10
        if (g_p4PoolsLeaveJob) g_p4PoolsLeaveJob();
        P4Disarm();
        t_p4Read = 0;
        LARGE_INTEGER t0;
        QueryPerformanceCounter(&t0);
        t_p6.inDoor = true;  // P6 split: the ask's Proxy wait counts here once
        const DWORD r = ((P4DoorFn)g_p4DoorJmp)(slot[1], slot[2], slot[3], slot[4], slot[5], slot[6], slot[7], slot[8], slot[9]);
        t_p6.inDoor = false;
        const LONG askUs = UsSince(t0);
        InterlockedExchangeAdd(&k.m2DoorWaitUs, askUs);
        if (t_p6.act) t_p6.doorN++, t_p6.doorUs += askUs;
        InterlockedIncrement(&k.m2DoorAsk);
        c.nAsk++;
        P4Arm(ctx, false);
        if (g_p4PoolsEnterJob) g_p4PoolsEnterJob();
        P5bAsk(false);
        t_p4Read = read;
        g_p4DoorAns = r;
        return 1;
    }
    if (t_snapRun) {
        DWORD key[9];
        P4DoorKey(slot + 1, key);
        for (int i = 0; i < c.nDoors; i++) {
            P4Door& d = c.doors[i];
            if (d.used || memcmp(d.a, key, sizeof(d.a))) continue;
            d.used = 1;
            g_p4DoorAns = d.eax;
            InterlockedIncrement(&k.snDoorReplay);
            return 1;
        }
        // the clone asks about a portal the live run did not: the paths already differ (§12.5); NoListeners, no managed call
        InterlockedIncrement(&k.snDoorMiss);
        if (InterlockedIncrement(&g_p4DoorMissLog) <= 20)
            Log("P4 door miss: key %08lX %08lX %08lX %08lX %08lX %08lX %04lX %04lX %02lX (plan %lu)", key[0], key[1], key[2], key[3],
                key[4], key[5], key[6], key[7], key[8], c.seq);
        P4Abort(P4_AB_DOORMISS);
        g_p4DoorAns = 0;
        return 1;
    }
    if (!t_p4DoorRec) return 0;
    const DWORD r = ((P4DoorFn)g_p4DoorJmp)(slot[1], slot[2], slot[3], slot[4], slot[5], slot[6], slot[7], slot[8], slot[9]);
    if (c.nDoors < kP4Doors) {
        P4Door& d = c.doors[c.nDoors++];
        P4DoorKey(slot + 1, d.a);
        d.eax = r;
        d.used = 0;
        InterlockedIncrement(&k.snDoorRec);
    } else {
        InterlockedIncrement(&k.snDoorOver);
        P4Abort(P4_AB_DOOROVER);
    }
    g_p4DoorAns = r;
    return 1;
}

static __declspec(naked) void P4DoorStub() {
    __asm {
        pushad
        pushfd
        lea eax, [esp + 36]
        push eax
        call P4DoorEnter
        add esp, 4
        test eax, eax
        jnz answered
        popfd
        popad
        jmp dword ptr [g_p4DoorJmp]
    answered:
        popfd
        popad
        mov eax, g_p4DoorAns
        ret
    }
}

// D5 display level family (docs/94 §17.9 N15, 0.29.0): the three writers of lot +0x39C and the level groups' bit 0x10000
// counted (any thread but a clone run); a call from a clone-running thread is the tripwire snDlOnClone (expected 0: the
// clone run reads the bits through the vertical rays 0x6583E0 -> 0x686CC0 but never reaches a writer)
struct P4DlSite { DWORD at; u8 len; u8 pro[10]; const char* name; };
static const P4DlSite kP4Dl[3] = {
    {TS3A(0x00AC0570), 10, {0x53, 0x56, 0x8B, 0xF1, 0x8B, 0x86, 0x80, 0x01, 0x00, 0x00}, "Lot_SetDisplayLevel"},  // ... mov eax,[esi+0x180]
    {TS3A(0x00AC2190), 5, {0x83, 0xEC, 0x08, 0x53, 0x56}, "Lot_RoofUp"},
    {TS3A(0x00AC5C90), 5, {0x8B, 0x44, 0x24, 0x04, 0x55}, "Lot_DisplayLevelWriter3"},
};
static void* g_p4DlJmp[3];
static void __cdecl P4DlEnter() {
    if (t_snapRun) InterlockedIncrement(&ts3native_counters.snDlOnClone);
    else InterlockedIncrement(&g_nrN[7]);
}
#define P4_DL_STUB(i) static __declspec(naked) void P4DlStub##i() { __asm pushad __asm pushfd __asm call P4DlEnter \
    __asm popfd __asm popad __asm jmp dword ptr [g_p4DlJmp + i * 4] }
P4_DL_STUB(0) P4_DL_STUB(1) P4_DL_STUB(2)
static void* const kP4DlStub[3] = {(void*)P4DlStub0, (void*)P4DlStub1, (void*)P4DlStub2};
static int P4InstallDl(const DWORD* at) {  // count of the stubs installed (prologue checked by InstallDetour)
    int n = 0;
    for (int i = 0; i < 3; i++)
        if (!g_p4DlJmp[i] && (g_p4DlJmp[i] = InstallDetour(kP4Dl[i].name, at[i], kP4Dl[i].pro, kP4Dl[i].len, kP4DlStub[i], &g_p4DlJmp[i]))) n++;
    return n;
}

static const DWORD P4_DOOR_AT = TS3A(0x0064F4D3), P4_DOOR_TARGET = TS3A(0x00791C00);
// 4 = chained behind [ProbeValues]' stub (its jump slot now names P4DoorStub), 8 = own call-site redirect, 0 = bytes differ
static LONG P4InstallDoor(DWORD at, DWORD target) {
    if (g_pvOn && g_pvJmp[PV_QUERY] == (void*)(uintptr_t)target && kPv[PV_QUERY].at == at) {
        g_p4DoorJmp = g_pvJmp[PV_QUERY];
        g_pvJmp[PV_QUERY] = (void*)P4DoorStub;
        return 4;
    }
    u8 want[5] = {0xE8};
    *(LONG*)(want + 1) = (LONG)(target - (at + 5));
    if (!Matches((const void*)(uintptr_t)at, want, 5)) {
        Log("SKIP P4: door call 0x%08lX is not `call 0x%08lX` (another owner?)", at, target);
        return 0;
    }
    g_p4DoorJmp = (void*)(uintptr_t)target;
    const LONG rel = (LONG)((DWORD)(uintptr_t)P4DoorStub - (at + 5));
    return Write((u8*)(uintptr_t)at + 1, &rel, 4) ? 8 : 0;
}

// --- capture ---
// 16-aligned: RoutingJob_ctor's vector assignment (0x758760 -> 0x7564D0 -> 0x651350) copies the start/goal records with
// movaps, and HeapAlloc gives 8 on x86 (the 0.30.1 C0000005 at 0x651374, docs/94 §21.10). bufs[] keeps the raw block.
static void* P4Alloc(P4Job& c, size_t n) {
    if (c.nBufs == 8) return NULL;
    u8* p = (u8*)HeapAlloc(g_p4Heap, 0, n + 15);
    if (!p) return NULL;
    c.bufs[c.nBufs++] = p;
    return (void*)(((uintptr_t)p + 15) & ~(uintptr_t)15);
}
static void P4JobFree(P4Job& c) {
    for (int i = 0; i < c.nBufs; i++) HeapFree(g_p4Heap, 0, c.bufs[i]);
    c.nBufs = 0;
}

static bool P4Copy(void* dst, const void* src, size_t n) {
    __try { memcpy(dst, src, n); }
    __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
    return true;
}

// an EASTL vector header (begin, end, capacity, allocator word) deep-copied into private memory; `elem` = element size
static bool P4CopyVec(P4Job& c, DWORD* dst, const DWORD* src, DWORD elem) {
    DWORD h[4];
    if (!P4Copy(h, src, sizeof(h))) return false;
    const DWORD bytes = h[1] - h[0];
    if (h[1] < h[0] || bytes % elem || bytes > (1u << 20)) return false;
    u8* b = (u8*)P4Alloc(c, bytes);
    if (!b || !P4Copy(b, (const void*)(uintptr_t)h[0], bytes)) return false;
    dst[0] = (DWORD)(uintptr_t)b;
    dst[1] = dst[2] = (DWORD)(uintptr_t)(b + bytes);
    dst[3] = h[3];
    return true;
}

// Route_PlanLocked's arguments (§12.3.1 `args`): 64-byte start and goal records; the options object (vtable 0x10213E4,
// route+0x158) as 0x80 bytes with its five heap vectors (+0x08 sorted set of 64-bit ids, +0x38, +0x48, +0x58, +0x68, u64)
// copied [docs/94 §18.7: 0x80 bytes and the +0x08 vector; I-94-100 as first written (0x9C, four vectors) was wrong]
static bool P4CaptureArgs(P4Job& c, int* outList, int* starts, int* goals, DWORD agent, DWORD doPlan, u8* opts, DWORD guidLo,
                          DWORD guidHi) {
    c.agent = agent;
    c.doPlan = doPlan;
    c.guidLo = guidLo;
    c.guidHi = guidHi;
    if (!P4Copy(c.outTail, outList + 2, sizeof(c.outTail))) return false;
    if (!P4CopyVec(c, c.starts, (const DWORD*)starts, 64) || !P4CopyVec(c, c.goals, (const DWORD*)goals, 64)) return false;
    if (!opts || !P4Copy(c.opts, opts, kP4OptSz)) return false;
    static const DWORD kVec[5] = {0x08, 0x38, 0x48, 0x58, 0x68};
    for (int i = 0; i < 5; i++)
        if (!P4CopyVec(c, (DWORD*)(c.opts + kVec[i]), (const DWORD*)(opts + kVec[i]), 8)) return false;
    return true;
}

// the answer in an out list (EASTL {next, prev, RoutingPathImp*}; docs/93 §4.2 offsets) [verified layout]
static bool P4CaptureAnswerRaw(const int* head, u8 ok, int out1, int out2, P4Ans* a) {
    a->valid = false;
    a->ok = ok;
    a->out1 = out1;
    a->out2 = out2;
    a->nPaths = a->nNodes = 0;
    __try {
        for (const int* node = (const int*)(uintptr_t)head[0]; node != head; node = (const int*)(uintptr_t)node[0]) {
            const u8* p = (const u8*)(uintptr_t)node[2];
            if (!p) continue;
            if (a->nPaths == kP4Paths) return false;
            P4Path& q = a->p[a->nPaths++];
            q.status = *(const DWORD*)(p + 0x94);
            q.type = *(const DWORD*)(p + 0xB0);
            q.obj = *(const unsigned __int64*)(p + 0xB8);
            q.len = *(const float*)(p + 0x8C);
            const u8* b = *(const u8* const*)(p + 0x98);
            const u8* e = *(const u8* const*)(p + 0x9C);
            q.n0 = a->nNodes;
            q.nn = 0;
            for (; b && b + 48 <= e; b += 48) {
                if (a->nNodes == kP4Nodes) return false;
                P4Node& n = a->n[a->nNodes++];
                n.x = ((const float*)b)[0];
                n.y = ((const float*)b)[1];
                n.z = ((const float*)b)[2];
                n.seg = *(const float*)(b + 0x20);
                n.flags = *(const DWORD*)(b + 0x2C);
                q.nn++;
            }
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
    a->valid = true;
    return true;
}

// docs/93 §2.1 at the Route_PlanLocked boundary: no path = Failed (2), else the first path's status through 0x752620's map
static int P4MType(const P4Ans& a) {
    if (!a.nPaths) return 2;
    const DWORD s = a.p[0].status;
    return s <= 2 ? 1 : s == 3 ? 2 : s == 4 ? 3 : s == 5 ? 4 : s == 6 ? 10 : s == 7 || s == 8 ? 16 : 2;
}

// worker, live job (PlanLockedHook while t_p4Cap): the arguments at entry, the answer and the plan time at leave
static u8 P4LivePlan(void* world, void* edx, int* outList, int* starts, int* goals, DWORD agent, DWORD doPlan, u8* opts,
                     int* out1, int* out2, DWORD guidLo, DWORD guidHi) {
    t_p4Cap = 0;  // one capture per job; a nested plan is not captured
    P4Job& c = g_p4Job;
    c.seen = 1;
    c.wPlan = g_spWrites;
    c.stale = c.wPlan != c.w0;  // the job holds the routing lock from here: no write lands after this
    c.epoch = (DWORD)g_spEpoch;
    P4NrSnap(c.nr0);
    if (!P4CaptureArgs(c, outList, starts, goals, agent, doPlan, opts, guidLo, guidHi) && c.abort < 0) c.abort = P4_AB_CAPTURE;
    if (c.mode >= 2) return P5Plan(world, edx, outList, starts, goals, agent, doPlan, opts, out1, out2, guidLo, guidHi);
    LARGE_INTEGER t0;
    QueryPerformanceCounter(&t0);
    t_p4DoorRec = 1;
    const u8 r = PlanLockedBody(world, edx, outList, starts, goals, agent, doPlan, opts, out1, out2, guidLo, guidHi);
    t_p4DoorRec = 0;
    c.gameUs = UsSince(t0);
    P4DdPlan(c, world);  // DoorDump only: still under the job's +0x918
    if (!P4CaptureAnswerRaw(outList, r, out1 ? *out1 : -1, out2 ? *out2 : -1, &g_p4Live) && c.abort < 0) c.abort = P4_AB_CAPTURE;
    return r;
}

// sim thread, async START (§12.4.1): 1 job in ShadowEvery is armed; the provider cuts its version
static void P4OnStart(AsyncJob* j) {
    j->p4 = 0;
    j->p4Ver = 0;
    j->p4Why = PS_NONE;
    j->p6Borrow = 0;
    j->p6T0 = j->p6CutQpc = 0;
    if (!(ts3native_counters.snP4Ready & 1)) return;
    if (ts3native_counters.snP4Mode == 2) { P5OnStart(j); return; }
    if (++g_p4StartN % (DWORD)g_p4Every) return;
    j->p4 = 1;
    g_pcCutW = g_spWrites;
    j->p4Ver = P4CloneCut();
    j->p4Why = j->p4Ver ? PS_NONE : g_pcCutWhy;
    // the cut's own count (0.35.6): read after the cut released +0x918, a worker that started a queued job meanwhile could
    // have written already, and its write would not mark the record stale
    j->p4W = j->p4Ver ? g_pcCutW : g_spWrites;
    InterlockedIncrement(&ts3native_counters.snShadowArmed);
}

// worker, before the job's IC call
static void P4LiveBegin(AsyncJob* j) {
    P6JobBegin(j);
    if (!j->p4) return;
    P4Job& c = g_p4Job;
    P4JobFree(c);
    c.armed = 1;
    c.seen = 0;
    c.kind = j->kind;
    c.mode = j->p4;
    c.nAsk = 0;
    c.abort = -1;
    c.sub = j->p4Why;
    c.borrow = j->p6Borrow;
    c.w0 = j->p4W, c.stale = false;
    c.seq = j->seq;
    c.ver = j->p4Ver;
    c.x87 = j->x87;
    c.mxcsr = j->mxcsr;
    c.nDoors = c.nDirty = c.ddN = 0;
    c.gameUs = c.jobUs = c.cloneRead = 0;
    g_p4Live.valid = g_p4Copy.valid = false;
    t_p4Cap = 1;
    t_p4Read = P4R_ON;
}
// worker, after the IC call and before J_DONE (the slot is the sim's again after that)
static void P4LiveEnd(AsyncJob*) {
    P6JobEnd();
    t_p4Cap = 0;
    g_p4Job.liveRead = t_p4Read;
    t_p4Read = 0;
}

// the clip scratch of this worker (§12.3.1, I-94-63): the live object's allocator words, empty vectors [layout inferred]
static void P4WorkerInit() {
    SnapWorker& w = g_p4Worker;
    if (w.init) return;
    if (Readable((const void*)(uintptr_t)g_p4ClipLive, sizeof(w.clip))) memcpy(w.clip, (const void*)(uintptr_t)g_p4ClipLive, sizeof(w.clip));
    DWORD* d = (DWORD*)w.clip;
    d[0] = d[1] = d[2] = d[4] = d[5] = d[6] = 0;  // begin, end, capacity of both vectors; +0x0C and +0x1C stay
    w.init = 1;
}

// the game's plan on the copy (POD only: __try)
// fault context (0.31.4): the 0.31.2 fault at 0x7CEF3688 (eip in heap) had no caller; registers and stack code refs name it
struct P4Run { void* world; int* head; P4Job* c; int out1, out2; u8 ok; DWORD faultAt, faultCode, info[2], reg[8]; char refs[160]; };
static int P4FaultFilter(EXCEPTION_POINTERS* e, P4Run* r) {
    const EXCEPTION_RECORD* x = e->ExceptionRecord;
    const CONTEXT* c = e->ContextRecord;
    r->faultCode = x->ExceptionCode;
    r->faultAt = (DWORD)(uintptr_t)x->ExceptionAddress;
    r->info[0] = x->NumberParameters > 0 ? (DWORD)x->ExceptionInformation[0] : 0;
    r->info[1] = x->NumberParameters > 1 ? (DWORD)x->ExceptionInformation[1] : 0;
    const DWORD reg[8] = {c->Eax, c->Ebx, c->Ecx, c->Edx, c->Esi, c->Edi, c->Ebp, c->Esp};
    memcpy(r->reg, reg, sizeof(reg));
    if (r->faultCode != kP4LazyCode) XcptRefs(c, r->refs, sizeof(r->refs), 12);
    return EXCEPTION_EXECUTE_HANDLER;
}
#ifdef TS3N_FAULTINJ
static void FiInRun();  // below P4EaHeapUnlock (it uses g_p4EaHeap)
#endif
static bool P4Guarded(P4Run* r) {
    P4Job& c = *r->c;
    __try {
#ifdef TS3N_FAULTINJ
        if (t_snapRun && g_fiPlan) FiInRun();
#endif
        r->ok = g_planOrig(r->world, NULL, r->head, (int*)c.starts, (int*)c.goals, c.agent, c.doPlan, c.opts, &r->out1, &r->out2,
                           c.guidLo, c.guidHi);
    } __except (P4FaultFilter(GetExceptionInformation(), r)) { return false; }
    return true;
}

// private paths released as the game does: 0x757250's per-path loop (AddRef, unlink slots +0x88/+0x90 with 0, Release),
// then 0x866E90 on the list head (releases each node's path, frees the node) [verified disassembly, Steam 1.67]
typedef void(__fastcall* P4PathSlotFn)(void* self, void* edx, DWORD a);
typedef void(__fastcall* P4ListFreeFn)(void* head, void* edx);
static P4ListFreeFn g_p4ListFree = (P4ListFreeFn)(uintptr_t)TS3A(0x00866E90);
static bool P4FreeList(int* head) {
    __try {
        for (int* node = (int*)(uintptr_t)head[0]; node != head; node = (int*)(uintptr_t)node[0]) {
            u8* p = (u8*)(uintptr_t)node[2];
            if (!p) continue;
            void** vt = *(void***)p;
            InterlockedIncrement((volatile LONG*)(p + 4));
            ((P4PathSlotFn)vt[0x88 / 4])(p, NULL, 0);
            ((P4PathSlotFn)vt[0x90 / 4])(p, NULL, 0);
            if (InterlockedDecrement((volatile LONG*)(p + 4)) == 0) {
                *(volatile LONG*)(p + 4) = 1;
                ((P4PathSlotFn)vt[0])(p, NULL, 1);
            }
            InterlockedIncrement(&ts3native_counters.snPathsFreed);
        }
        g_p4ListFree(head, NULL);
    } __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
    head[0] = head[1] = (int)(uintptr_t)head;
    return true;
}

// statics the clone run must find set (§12.3.2 #12, #14, #27): otherwise "cold" [I-94-64, I-109-21]
static bool P4Cold() {
    const u8* g12 = (const u8*)(uintptr_t)g_p4Guards[0];
    const DWORD* g14a = (const DWORD*)(uintptr_t)g_p4Guards[1];
    const DWORD* g14b = (const DWORD*)(uintptr_t)g_p4Guards[2];
    const u8* g27 = (const u8*)(uintptr_t)g_p4Guards[3];
    if (!Readable(g12, 1) || !Readable(g14a, 4) || !Readable(g14b, 4) || !Readable(g27, 1)) return true;
    return !*g12 || !(*g14a | *g14b) || !(*g27 & 1);
}

// crash H (docs/67 §10, 0.32.6): EA's general allocator [0x11CB864] takes its lock object [+0x4E4] (a CRITICAL_SECTION, depth
// word +0x18) around every alloc / free (0x4E69D0, 0x4E5920) [verified disassembly]. A fault inside them, unwound by
// P4Guarded, never left it: the worker kept the heap lock and every other thread's next allocation waited for ever (the
// 0.32.4 run hung, heartbeat frozen, no xcpt). A clone run starts outside the allocator, so any hold left here is orphaned.
static DWORD* g_p4EaHeap = (DWORD*)(uintptr_t)TS3A(0x011CB864);
static int P4EaHeapUnlock() {
    int n = 0;
    if (!g_p4EaHeap || !Readable(g_p4EaHeap, 4)) return 0;  // no first-chance AV for the crash log (offline: not mapped)
    __try {
        u8* h = (u8*)(uintptr_t)*g_p4EaHeap;
        u8* lk = h && Readable(h + 0x4E4, 4) ? *(u8**)(h + 0x4E4) : NULL;
        if (lk && !Readable(lk, 0x1C)) return -1;
        CRITICAL_SECTION* cs = (CRITICAL_SECTION*)lk;
        for (; cs && (DWORD)(uintptr_t)cs->OwningThread == GetCurrentThreadId() && n < 16; n++) {
            (*(LONG*)(lk + 0x18))--;
            LeaveCriticalSection(cs);
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) { return -1; }
    return n;
}

#ifdef TS3N_FAULTINJ
// inside P4Guarded's __try, on the worker, in a Mode=2 clone run (the P5b / P5c window open if the job lends)
static void FiInRun() {
    const LONG b = g_fiPlan;
    g_fiPlan = 0;
    if (b & FI_BUDGET) g_fiBudgetOn = t_budget.on;
    if (b & FI_STALE) InterlockedIncrement(&g_spWrites);  // a recorded routing write after the cut
    if (b & FI_CLOSE) ts3native_counters.rtClosing = 1;  // world close during the run (the selftest clears it)
    if (b & FI_MANAGED) { Log("FaultInject: managed call in a clone run"); P4Abort(P4_AB_MANAGED); }
    if (b & FI_AV) {
        Log("FaultInject: access violation in a clone run%s", b & FI_EAHEAP ? " holding the EA heap lock" : "");
        if (b & FI_EAHEAP) {  // crash H's shape: the fault inside EA's allocator, its lock (+0x4E4, depth +0x18) held
            u8* lk = *(u8**)((u8*)(uintptr_t)*g_p4EaHeap + 0x4E4);
            EnterCriticalSection((CRITICAL_SECTION*)lk);
            (*(LONG*)(lk + 0x18))++;
        }
        static volatile int* volatile s_null;
        *s_null = 1;
    }
}
#endif

// a private out-list head laid out like route+0xE0 (I-94-67): empty, with the live head's allocator words
static void P4Head(int* head, const P4Job& c) {
    head[0] = head[1] = (int)(uintptr_t)head;
    head[2] = (int)c.outTail[0];
    head[3] = (int)c.outTail[1];
}
// one clone run into `head` (P4Head); sets g_p4Copy or an abort reason. Returns P4REL_* flags for the provider. keep: a
// clean run leaves its paths in `head` for the caller (P5 takes or frees them); else they are released here
static LONG P4RunClone(P4Job& c, const P4Clone& cl, int* head, bool keep) {
    Ts3nCounters& k = ts3native_counters;
    P4WorkerInit();
    if (cl.fresh) { g_p4Work.stamp = 0; g_p4Work.wrap = 0; }
    SnapCtx ctx = {cl.world, &g_p4Work, &g_p4Worker};
    P4Run r = {cl.world, head, &c, -1, -1, 0, 0, 0};
    t_p4Read = P4R_ON;
    if (!P4Arm(&ctx, c.mode < 2)) {
        P4Disarm();
        t_p4Read = 0;
        if (c.abort < 0) c.abort = P4_AB_NRBUSY;
        return 0;
    }
    const unsigned short cw = GetX87();
    const unsigned int csr = _mm_getcsr();
    SetX87(c.x87);
    _mm_setcsr(c.mxcsr);
    LARGE_INTEGER t0;
    QueryPerformanceCounter(&t0);
    if (g_p4PoolsEnterJob) g_p4PoolsEnterJob();  // per-job A*, goal and cell pools (docs/94 §16.6 P4 note): hook for pools.cpp
    g_p4LazyKeep = 0;
    const bool done = P4Guarded(&r);
    const int eaHeld = done ? 0 : P4EaHeapUnlock();
    if (g_p4PoolsLeaveJob) g_p4PoolsLeaveJob();
    c.jobUs = UsSince(t0);
    SetX87(cw);
    _mm_setcsr(csr);
    P4Disarm();
    c.cloneRead = t_p4Read;
    t_p4Read = 0;
    InterlockedExchangeAdd(&k.snShadowUs, c.jobUs);
    Max(&k.snShadowMaxUs, c.jobUs);
    if (!done && r.faultCode == kP4LazyCode) {  // D4 stop (P4Lazy): not a fault; the clone may hold half-done writes
        RgDrop(t_rg, (const DWORD*)_AddressOfReturnAddress());
        if (!P4FreeList(head)) Log("P4: private path list of plan %lu not released after a lazy stop", c.seq);
        c.abort = P4_AB_LAZY;
        return g_p4LazyKeep ? P4REL_LAZY : P4REL_DISCARD;
    }
    if (!done) {
        // crash-cause rule: our code is at fault until proven otherwise. P4 off for the session; the copy is thrown away.
        InterlockedIncrement(&k.snShadowFault);
        Log("P4 FAULT in a clone run: exception %08lX at 0x%08lX (plan %lu, kind %d); P4 off for this session", r.faultCode, r.faultAt,
            c.seq, c.kind);
        Log("P4 FAULT context: info %08lX %08lX thread %lu eax %08lX ebx %08lX ecx %08lX edx %08lX esi %08lX edi %08lX ebp %08lX "
            "esp %08lX; stack code refs:%s", r.info[0], r.info[1], GetCurrentThreadId(), r.reg[0], r.reg[1], r.reg[2], r.reg[3],
            r.reg[4], r.reg[5], r.reg[6], r.reg[7], r.refs);
        if (eaHeld) Log("P4 FAULT: EA heap lock %s (%d holds the unwind left)", eaHeld > 0 ? "released" : "not readable", eaHeld);
        RgDrop(t_rg, (const DWORD*)_AddressOfReturnAddress());  // gate / reader frames the unwind left behind (their holds)
        k.snP4Ready = 0;
        g_p4On = 0;
        c.abort = P4_AB_FAULT;
        return P4REL_DISCARD;
    }
    if (!P4CaptureAnswerRaw(head, r.ok, r.out1, r.out2, &g_p4Copy) && c.abort < 0) c.abort = P4_AB_CAPTURE;
    if (!keep && !P4FreeList(head)) Log("P4: private path list of plan %lu not released (fault while releasing)", c.seq);
    LONG unused = 0;
    for (int i = 0; i < c.nDoors; i++) unused += !c.doors[i].used;
    if (unused && c.abort < 0) InterlockedExchangeAdd(&k.snDoorUnused, unused);
    return g_p4Work.wrap ? P4REL_WRAP : 0;
}

// --- goal tie set (docs/99 §4.1, docs/94 §13.1; RoutingJob_Execute 0x81B8CC..0x81BC65 [verified]) ---
// The job draws its goal with rng(n) over all n records when n > 1, every priority (+0x20) equals the running best's and each
// record's squared xz distance to the first start (x +0, z +8, float) is within 0.05f of the running best d² before it
// (x87 compare at 0x81BA11). The job is a stack local of Route_PlanLocked seeded from RDTSC in its ctor (0x819DBE -> 0x4FA800):
// the live run and the clone run draw independently, so on a tie any index is the game's answer. Returns n, or 0 (no draw).
static int P4TieN(const DWORD* starts, const DWORD* goals) {
    const u8* s = (const u8*)(uintptr_t)starts[0];
    const u8* b = (const u8*)(uintptr_t)goals[0];
    const u8* e = (const u8*)(uintptr_t)goals[1];
    if (!s || (const u8*)(uintptr_t)starts[1] < s + 64 || !b || e < b + 128) return 0;
    const int n = (int)((e - b) / 64);
    const float px = *(const float*)s, pz = *(const float*)(s + 8), prio = *(const float*)(b + 0x20);
    volatile float best = 0;
    for (int i = 0; i < n; i++) {
        const u8* g = b + i * 64;
        volatile float dx = *(const float*)g - px, dz = *(const float*)(g + 8) - pz;
        volatile float dx2 = dx * dx, dz2 = dz * dz;
        volatile float d2 = dx2 + dz2;  // float32 each step, as the SSE code at 0x81B9E7..0x81BA01
        const float pr = *(const float*)(g + 0x20);
        if (!(pr == prio)) return 0;  // ucomiss: unequal or NaN clears the flag
        if (i && fabs((double)d2 - (double)best) > (double)0.05f) return 0;
        if (!i || best > d2) best = d2;
    }
    return n;
}

// --- compare (docs/94 §13 rules, exact under G) ---
static bool P4Eq(float a, float b) { return *(const DWORD*)&a == *(const DWORD*)&b; }
// tieN: P4TieN of the job; a goal index that differs inside the tie set is M1 goal-tie (nothing else compared, §13)
static int P4Classify(const P4Ans& g, const P4Ans& c, LONG nr, int tieN, bool stale = false, bool count = true) {
    const int mg = P4MType(g), mc = P4MType(c);
    const DWORD sg = g.nPaths ? g.p[0].status : 0, sc = c.nPaths ? c.p[0].status : 0;
    int cls;
    if (mg != mc || sg != sc) cls = 3;
    else if (mg != 1) cls = 0;
    else if (g.out2 != c.out2 && tieN && g.out2 >= 0 && c.out2 >= 0 && g.out2 < tieN && c.out2 < tieN) {
        if (!nr && !stale && count) InterlockedIncrement(&ts3native_counters.snM1Tie);  // with nr / stale it is M5 below
        cls = 1;
    } else {
        bool m2 = g.out1 != c.out1 || g.out2 != c.out2 || g.nPaths != c.nPaths, m1 = false;
        float costG = 0, costC = 0;
        for (int i = 0; i < g.nPaths; i++) costG += g.p[i].len;
        for (int i = 0; i < c.nPaths; i++) costC += c.p[i].len;
        if (!P4Eq(costG, costC)) m2 = true;
        for (int i = 0; !m2 && i < g.nPaths; i++) {
            const P4Path &a = g.p[i], &b = c.p[i];
            if (a.type != b.type || a.obj != b.obj) { m2 = true; break; }
            if (!P4Eq(a.len, b.len) || a.nn != b.nn) { m1 = true; continue; }
            for (int n = 0; n < a.nn; n++) {
                const P4Node &x = g.n[a.n0 + n], &y = c.n[b.n0 + n];
                if (!P4Eq(x.x, y.x) || !P4Eq(x.y, y.y) || !P4Eq(x.z, y.z) || !P4Eq(x.seg, y.seg) || x.flags != y.flags) { m1 = true; break; }
            }
        }
        if (!m2 && g.nPaths && g.p[g.nPaths - 1].nn && c.p[c.nPaths - 1].nn) {  // end point
            const P4Node &x = g.n[g.p[g.nPaths - 1].n0 + g.p[g.nPaths - 1].nn - 1], &y = c.n[c.p[c.nPaths - 1].n0 + c.p[c.nPaths - 1].nn - 1];
            if (!P4Eq(x.x, y.x) || !P4Eq(x.z, y.z)) m2 = true;
        }
        cls = m2 ? 2 : m1 ? 1 : 0;
    }
    return cls && (nr || stale) ? 5 : cls;  // D5: a mismatch with a non-routing write in the window is time skew (M5); 0.33.2:
                                             // so is one with routing writes between the cut and the live plan (stale)
}

// --- the §13 record (v 2: adds `nr`; v 3 (0.33.0): adds `m2`, 1 = a P5 verify job, whose copy answer Mode=2 would take) ---
static size_t g_p4Len;
static void P4Put(const char* fmt, ...) {
    if (g_p4Len >= kP4BufCap - 64) return;
    va_list ap;
    va_start(ap, fmt);
    const int n = _vsnprintf_s(g_p4Buf + g_p4Len, kP4BufCap - g_p4Len, _TRUNCATE, fmt, ap);
    va_end(ap);
    g_p4Len += n > 0 ? (size_t)n : 0;
}
static void P4PutF(float f) {
    if (f == f && f - f == 0) P4Put("%.9g", f); else P4Put("null");  // finite: not NaN and not +-inf
}
static void P4PutPoint(const DWORD* vec, int idx) {
    const u8* b = (const u8*)(uintptr_t)vec[0];
    const u8* e = (const u8*)(uintptr_t)vec[1];
    if (idx < 0 || !b || b + (idx + 1) * 64 > e) { P4Put("null"); return; }
    const float* p = (const float*)(b + idx * 64);
    P4Put("[");
    P4PutF(p[0]);
    P4Put(",");
    P4PutF(p[1]);
    P4Put(",");
    P4PutF(p[2]);
    P4Put("]");
}
static void P4PutAns(const P4Ans& a) {
    P4Put("{\"mType\":%d,\"status\":%lu,\"startIdx\":%d,\"goalIdx\":%d,\"paths\":[", P4MType(a), a.nPaths ? a.p[0].status : 0,
          a.out1, a.out2);
    for (int i = 0; i < a.nPaths; i++) {
        const P4Path& p = a.p[i];
        P4Put("%s{\"type\":\"0x%08lX\",\"obj\":\"0x%llX\",\"len\":", i ? "," : "", p.type, p.obj);
        P4PutF(p.len);
        P4Put(",\"o58\":[],\"nodes\":[");  // O+0x58 is filled by the PlanCommon tail, after this boundary: empty on both sides
        for (int n = 0; n < p.nn; n++) {
            const P4Node& q = a.n[p.n0 + n];
            P4Put(n ? ",[" : "[");
            P4PutF(q.x);
            P4Put(",");
            P4PutF(q.y);
            P4Put(",");
            P4PutF(q.z);
            P4Put(",");
            P4PutF(q.seg);
            P4Put(",%lu,-1]", q.flags);  // type: the O2 search node +0x1C is not known at this boundary
        }
        P4Put("]}");
    }
    P4Put("]}");
}
// --- obstacle sets of an M2 / M3 (0.35.4, docs/94 §21.19, I-94-390) ---
// Every footprint in a planner's map M (P+0x24; nodes key +0, FME +8, next +0x10; FME +8 footprint, +0x10 level/flags) whose
// polygon cache ([fp+0x38], 0x24-byte levels, valid +0x20, 16-byte polygon records of 8-byte xz points) has a point inside the
// box round both answers: id (+0x14), class, +0x34 word, FME +0x10, levels, invalid-level mask, a hash of every valid level's
// points and their xz bounds. Taken from the clone (still held) and from the live map under one +0x918 try, so the next live
// M2 names the obstacle the two sides disagree on. Off the sim thread; only on a final M2/M3 (rare).
static const int kP4ObMax = 48;
// 0.36.50 (docs/94 §24.8.4): a footprint's polygon points (hash of every valid level h, per level lh, invalid-level mask, xz
// bounds as stored) and its lock-key set fp+0x20 (docs/156 U1), shared by the obstacle sets and the M2 lot dump
struct P4Fp { unsigned __int64 h, lh[8]; DWORD inv; float b[4]; };
static void P4FpPoints(const u8* fp, DWORD lv, P4Fp& o) {
    o.h = 0xCBF29CE484222325ull, o.inv = 0;
    o.b[0] = o.b[1] = 1e30f, o.b[2] = o.b[3] = -1e30f;
    const u8* lc = RpP(fp + 0x38);
    for (DWORD l = 0; l < lv && l < 8; l++) {
        o.lh[l] = 0;
        const u8* e = lc + l * 0x24;
        if (!e[0x20]) { o.inv |= 1u << l; continue; }
        o.lh[l] = 0xCBF29CE484222325ull;
        const u8* pe = RpP(e + 4);
        for (const u8* r = RpP(e); r && r < pe && r < RpP(e) + 16 * 4096; r += 16) {
            const float* v = (const float*)RpP(r);
            const float* ve = (const float*)RpP(r + 4);
            o.h = RpMix(RpMix(o.h, l), (DWORD)(ve - v));
            for (int j = 0; v && v + 2 <= ve && j < 4096; v += 2, j++) {
                o.h = RpMix(RpMix(o.h, *(const DWORD*)v), *(const DWORD*)(v + 1));
                o.lh[l] = RpMix(RpMix(o.lh[l], *(const DWORD*)v), *(const DWORD*)(v + 1));
                if (v[0] < o.b[0]) o.b[0] = v[0];
                if (v[1] < o.b[1]) o.b[1] = v[1];
                if (v[0] > o.b[2]) o.b[2] = v[0];
                if (v[1] > o.b[3]) o.b[3] = v[1];
            }
        }
    }
}
// lock-key set (0x30 bytes: id lists +0x08/+0x0C and +0x1C/+0x20, 8-byte ids; p4clone.cpp PcKeyCopy). A clone footprint holds
// our copy, whose 16-byte header names the live set it stood for at the cut (origin): clone origin != live set = the live
// footprint got another set after the cut (docs/156 U1). na: the child class has no +0x20
static const DWORD kP4KeyMagic = 0x4B455953;  // = kPcKeyMagic (p4clone.cpp is included after this file)
struct P4Key { DWORD set, origin, nA, nB; unsigned __int64 h; bool na; };
static void P4KeyOf(const u8* fp, const RpCls* c, P4Key& k) {
    memset(&k, 0, sizeof(k));
    if (c->child) { k.na = true; return; }
    const u8* set = RpP(fp + 0x20);
    k.set = k.origin = (DWORD)(uintptr_t)set;
    if (!set) return;
    if (RpD(set - 12) == kP4KeyMagic) k.origin = RpD(set - 16);
    k.h = 0xCBF29CE484222325ull;
    for (DWORD off = 8; off <= 0x1C; off += 0x14) {
        const u8* b = RpP(set + off);
        const u8* e = RpP(set + off + 4);
        const DWORD n = b && e > b && (DWORD)(e - b) <= 4096 * 8 ? (DWORD)(e - b) / 8 : 0;
        (off == 8 ? k.nA : k.nB) = n;
        k.h = RpMix(k.h, n);
        for (DWORD i = 0; i < n; i++) k.h = RpMix(RpMix(k.h, RpD(b + 8 * i)), RpD(b + 8 * i + 4));
    }
}
static bool P4KeyDiff(const P4Key& c, const P4Key& l) { return c.na != l.na || !c.set != !l.set || c.nA != l.nA || c.nB != l.nB || c.h != l.h; }
static void P4PutKey(const P4Key& k) {
    if (k.na) P4Put("null");
    else P4Put("[\"0x%08lX\",\"0x%08lX\",%lu,%lu,\"0x%016llX\"]", k.set, k.origin, k.nA, k.nB, k.h);
}
// I-94-521 (docs/94 §24.8.4): the lot whose level-0 quadtree bounds hold world (x, z) deepest (the lots' 2 m skirts overlap).
// Lot table P+0x4C/+0x50 as P4DdWalk; lot graph g = node + 0x10, world -> lot rows g+0xA0 (translation row g+0xD0, as
// 0x62ED40; 0x62ECD0 is the inverse at g+0xE0), Q vector g+0x74/+0x78, Q bounds +4..+0x10 lot-local (docs/98 §15.7).
// Callers hold a __try.
// g_p4LaWhy: why the last P4LotAt found no lot (0.36.51, docs/94 §24.10: g1dump found no lot for an in-room start): lots
// walked, lots with a Q, the nearest lot by the lot-frame test (its signed inside distance, lot-frame point, Q bounds and
// side +0x38), and how many lots hold the point when the Q bounds are read as world coordinates instead.
// 0.36.54 (docs/94 §24.12): the Q vector is indexed by level - g+0x40 and a slot may be NULL (RoomGraph_NodeFor 0x6320D0
// checks both); 0.36.51 took slot 0 only, so a lot with g+0x40 < 0 and no Q at that lowest level read as "no Q" (g1dump2:
// 56 of 82 lots, the in-room start's lot among them [inferred]). P4LotAt now takes the level-0 slot, else the first
// non-NULL one; slot0Null counts the lots the old rule missed. lot[]: every lot walked (key, g+0x40, slots, non-NULL mask,
// raw +0x74/+0x78, byte +0x8C, world -> lot translation g+0xD0/+0xD8, the point in its frame), logged once per side.
static const int kLaLots = 128;
struct P4LaLot { unsigned __int64 key; int base, nSlots; DWORD mask, v74, v78; BYTE f8c, used; float tx, tz, lx, lz; };
struct P4LaWhy { int st, lots, withQ, worldHits, slot0Null, qSlot; float bestD, lx, lz, qb[4], side; unsigned __int64 key; P4LaLot lot[kLaLots]; };
static const u8* P4LotQ(const u8* g, int* slot, bool* slot0Null) {  // the level-0 Q of lot graph g (as 0x6320D0), else the first set
    const u8* b = RpP(g + 0x74);
    const u8* e = RpP(g + 0x78);
    const int n = b && e > b ? (int)((e - b) / 4) : 0, i0 = -(int)RpD(g + 0x40);
    *slot0Null = n > 0 && !RpP(b);
    if (i0 >= 0 && i0 < n && RpP(b + 4 * i0)) return *slot = i0, RpP(b + 4 * i0);
    for (int i = 0; i < n && i < 64; i++)
        if (RpP(b + 4 * i)) return *slot = i, RpP(b + 4 * i);
    return NULL;
}
static P4LaWhy g_p4LaWhy;
static const u8* P4LotAt(const u8* world, float x, float z, unsigned __int64* key) {
    P4LaWhy& w = g_p4LaWhy;
    memset(&w, 0, sizeof(w));
    w.bestD = -1e30f;
    const u8* P = world ? RpP(world + 0x95C) : NULL;
    const DWORD* bk = P ? (const DWORD*)(uintptr_t)RpD(P + 0x4C) : NULL;
    const DWORD nb = P ? RpD(P + 0x50) : 0;
    if (!bk || nb > (1u << 20)) { w.st = !P ? 1 : 2; return NULL; }  // 1 no lot table, 2 no buckets
    const DWORD sentinel = bk[nb];
    const u8* best = NULL;
    float bestD = -1.0f;
    int n = 0;
    for (DWORD i = 0; i < nb; i++)
        for (const u8* nd = (const u8*)(uintptr_t)bk[i]; nd && (DWORD)(uintptr_t)nd != sentinel && n < 4096; nd = RpP(nd + 0x190), n++) {
            w.lots++;
            const u8* g = nd + 0x10;
            const float* m = (const float*)(g + 0xA0);
            const float lx = x * m[0] + z * m[8] + m[12], lz = x * m[2] + z * m[10] + m[14];
            int slot = -1;
            bool s0 = false;
            const u8* q = P4LotQ(g, &slot, &s0);
            if (w.lots <= kLaLots) {
                P4LaLot& r = w.lot[w.lots - 1];
                const u8* b = RpP(g + 0x74);
                const u8* e = RpP(g + 0x78);
                r.key = (unsigned __int64)RpD(nd + 4) << 32 | RpD(nd), r.base = (int)RpD(g + 0x40);
                r.nSlots = b && e > b ? (int)((e - b) / 4) : 0, r.v74 = (DWORD)(uintptr_t)b, r.v78 = (DWORD)(uintptr_t)e, r.mask = 0;
                for (int j = 0; j < r.nSlots && j < 32; j++) r.mask |= RpP(b + 4 * j) ? 1u << j : 0;
                r.f8c = g[0x8C], r.used = 1, r.tx = m[12], r.tz = m[14], r.lx = lx, r.lz = lz;
            }
            if (!q) continue;
            w.withQ++;
            if (s0) w.slot0Null++;
            const float* qb = (const float*)(q + 4);
            float d = lx - qb[0];
            if (qb[2] - lx < d) d = qb[2] - lx;
            if (lz - qb[1] < d) d = lz - qb[1];
            if (qb[3] - lz < d) d = qb[3] - lz;
            if (x >= qb[0] && x <= qb[2] && z >= qb[1] && z <= qb[3]) w.worldHits++;
            if (d > w.bestD) {
                w.bestD = d, w.lx = lx, w.lz = lz, w.side = *(const float*)(q + 0x38);
                memcpy(w.qb, qb, sizeof(w.qb));
                w.key = (unsigned __int64)RpD(nd + 4) << 32 | RpD(nd);
            }
            if (d >= 0 && d > bestD) bestD = d, best = g, w.qSlot = slot, *key =(unsigned __int64)RpD(nd + 4) << 32 | RpD(nd);
        }
    w.st = best ? 0 : 3;  // 3 no lot holds the point
    return best;
}
static void P4LaLog(unsigned long seq, const char* side) {
    const P4LaWhy& w = g_p4LaWhy;
    Log("P4 lot lookup plan %lu %s: no lot (why %d): lots %d, with Q %d, nearest 0x%016llX inside %.3f, lot-frame point (%.3f, %.3f), "
        "Q bounds [%.3f,%.3f,%.3f,%.3f] side %.3f; lots holding the point in world-frame bounds %d; lots with slot 0 NULL %d",
        seq, side, w.st, w.lots, w.withQ, w.key, w.bestD, w.lx, w.lz, w.qb[0], w.qb[1], w.qb[2], w.qb[3], w.side, w.worldHits, w.slot0Null);
}
static void P4LaTableLog(unsigned long seq, const char* side, const P4LaWhy& w) {  // once per side (the caller gates it)
    Log("P4 lot table plan %lu %s: lots %d, with Q %d (slot 0 NULL %d), picked slot %d", seq, side, w.lots, w.withQ, w.slot0Null, w.qSlot);
    for (int i = 0; i < w.lots && i < kLaLots; i++) {
        const P4LaLot& r = w.lot[i];
        Log("P4 lot table %s: 0x%016llX base %d slots %d mask 0x%lX v74 0x%08lX v78 0x%08lX f8c 0x%02X t (%.3f, %.3f) point (%.3f, %.3f)",
            side, r.key, r.base, r.nSlots, r.mask, r.v74, r.v78, r.f8c, r.tx, r.tz, r.lx, r.lz);
    }
}
static void P4BoxToLot(const u8* g, const float* w, float* l) {  // the world box's 4 corners through g+0xA0, then their bounds
    const float* m = (const float*)(g + 0xA0);
    for (int i = 0; i < 4; i++) {
        const float x = w[i & 1 ? 2 : 0], z = w[i & 2 ? 3 : 1];
        const float lx = x * m[0] + z * m[8] + m[12], lz = x * m[2] + z * m[10] + m[14];
        if (!i || lx < l[0]) l[0] = lx;
        if (!i || lz < l[1]) l[1] = lz;
        if (!i || lx > l[2]) l[2] = lx;
        if (!i || lz > l[3]) l[3] = lz;
    }
}
static bool P4BoxHit(const float* b, const float* bx) { return !(b[0] > bx[2] || b[2] < bx[0] || b[1] > bx[3] || b[3] < bx[1]); }
struct P4Ob { unsigned __int64 id, h; DWORD vt, f34, fme, lv, inv; float b[4]; char fr; P4Key k; };  // fr: 'w' world box, 'l' lot box
struct P4ObSet {
    int n, more;  // more: entries past kP4ObMax; -1 no map, -2 fault in the walk
    P4Ob o[kP4ObMax];
    int walked, nNear;  // 0.36.50 (docs/94 §24.8.4 step 1): footprints walked, and the 4 nearest by centre with no box filter
    bool haveL;         // a lot holds the box centre: the box in its frame is lb
    unsigned __int64 lot;
    float lb[4], nearD[4];
    P4Ob nearO[4];
};
static P4ObSet g_p4ObC, g_p4ObL;
static float g_p4ObBox[4];        // x0, z0, x1, z1
static int g_p4ObLive;            // 1 read under +0x918, 2 lock busy (no live set), 3 no live world
static LONG g_p4ObW, g_p4ObUs;    // recorded writes since the live plan began; capture time
static bool g_p4ObOn;             // the next P4Record carries "obst"
static bool g_p4ObsOn = true;     // [RouteSnap] ShadowObst (default 1)
static void P4ObNear(P4ObSet& s, const P4Ob& o, float d) {  // keeps the 4 smallest d, ascending
    if (s.nNear == 4 && d >= s.nearD[3]) return;
    int i = s.nNear < 4 ? s.nNear++ : 3;
    for (; i > 0 && s.nearD[i - 1] > d; i--) s.nearO[i] = s.nearO[i - 1], s.nearD[i] = s.nearD[i - 1];
    s.nearO[i] = o, s.nearD[i] = d;
}
// 0.36.50 (I-94-521, docs/94 §24.8.4): the 17 live "obst" blocks were all empty: the world box was compared with polygon
// points held in another frame. A footprint is now listed when its points reach the world box ('w') or the box in the frame
// of the lot that holds the box centre ('l'), so whichever frame the cache uses, the obstacles are found and the record says
// which (TestP4LotDump: lot-local points, world box)
static void P4ObWalk(const u8* world, P4ObSet& s) {  // world: a routing world (planner P at +0x95C)
    s.n = s.more = s.walked = s.nNear = 0;
    s.haveL = false, s.lot = 0;
    const float* bx = g_p4ObBox;
    __try {
        const u8* P = world ? RpP(world + 0x95C) : NULL;
        const u8* M = P ? RpP(P + 0x24) : NULL;
        const u8* const* lb = M ? (const u8* const*)(uintptr_t)RpD(M + 4) : NULL;
        if (!lb) { s.more = -1; return; }
        const float cw[2] = {(bx[0] + bx[2]) * 0.5f, (bx[1] + bx[3]) * 0.5f};
        if (const u8* g = P4LotAt(world, cw[0], cw[1], &s.lot)) P4BoxToLot(g, bx, s.lb), s.haveL = true;
        const float cl[2] = {(s.lb[0] + s.lb[2]) * 0.5f, (s.lb[1] + s.lb[3]) * 0.5f};
        const DWORD mb = RpD(M + 8);
        DWORD seen = 0;
        for (DWORD i = 0; i < mb && i < (1u << 20); i++)
            for (const u8* nd = lb[i]; nd && ++seen < 400000; nd = RpP(nd + 0x10)) {
                const u8* f = RpP(nd + 8);
                const u8* fp = f ? RpP(f + 8) : NULL;
                const RpCls* c = fp ? RpClass(RpD(fp)) : NULL;
                if (!c || !c->cache) continue;
                const DWORD lv = RpLevels(fp);
                const u8* lc = RpP(fp + 0x38);
                if (!lc || !lv || lv > 8) continue;
                P4Fp pt;
                P4FpPoints(fp, lv, pt);
                P4Ob o = {RpD(fp + 0x14) | (unsigned __int64)RpD(fp + 0x18) << 32, pt.h, RpD(fp), c->size > 0x34 ? RpD(fp + 0x34) : 0,
                          RpD(f + 0x10), lv, pt.inv, {pt.b[0], pt.b[1], pt.b[2], pt.b[3]}, 0, {}};
                s.walked++;
                if (o.b[0] <= o.b[2]) {
                    const float mx = (o.b[0] + o.b[2]) * 0.5f, mz = (o.b[1] + o.b[3]) * 0.5f;
                    const float dw = (mx - cw[0]) * (mx - cw[0]) + (mz - cw[1]) * (mz - cw[1]);
                    const float dl = s.haveL ? (mx - cl[0]) * (mx - cl[0]) + (mz - cl[1]) * (mz - cl[1]) : 1e30f;
                    o.fr = dl < dw ? 'l' : 'w';
                    P4ObNear(s, o, dl < dw ? dl : dw);
                }
                const bool inW = P4BoxHit(o.b, bx), inL = s.haveL && P4BoxHit(o.b, s.lb);
                if (!inW && !inL) continue;  // no valid point near in either frame: skipped
                o.fr = inW ? 'w' : 'l';
                P4KeyOf(fp, c, o.k);
                bool dup = false;  // the same footprint through several FMEs (lots)
                for (int j = 0; j < s.n && !dup; j++) dup = s.o[j].id == o.id && s.o[j].h == o.h && s.o[j].vt == o.vt;
                if (dup) continue;
                if (s.n == kP4ObMax) { s.more++; continue; }
                s.o[s.n++] = o;
            }
    } __except (EXCEPTION_EXECUTE_HANDLER) { s.more = -2; }
}
static void P4ObBoxOf(const P4Ans& a, bool* any) {
    for (int i = 0; i < a.nNodes; i++) {
        const P4Node& n = a.n[i];
        if (!(n.x - n.x == 0) || !(n.z - n.z == 0)) continue;
        if (!*any || n.x < g_p4ObBox[0]) g_p4ObBox[0] = n.x;
        if (!*any || n.z < g_p4ObBox[1]) g_p4ObBox[1] = n.z;
        if (!*any || n.x > g_p4ObBox[2]) g_p4ObBox[2] = n.x;
        if (!*any || n.z > g_p4ObBox[3]) g_p4ObBox[3] = n.z;
        *any = true;
    }
}
// worker, P4ShadowRun on a final M2/M3 while the clone is still held; cloneWorld = the clone's routing world (P at +0x95C)
static void P4ObCapture(const P4Job& c, const void* cloneWorld) {
    bool any = false;
    P4ObBoxOf(g_p4Live, &any);
    P4ObBoxOf(g_p4Copy, &any);
    if (!any) return;
    g_p4ObBox[0] -= 1.0f, g_p4ObBox[1] -= 1.0f, g_p4ObBox[2] += 1.0f, g_p4ObBox[3] += 1.0f;  // a footprint's reach past the path
    LARGE_INTEGER t0;
    QueryPerformanceCounter(&t0);
    P4ObWalk((const u8*)cloneWorld, g_p4ObC);
    g_p4ObL.n = g_p4ObL.more = 0;
    g_p4ObLive = 3;
    const u8* world = g_spWorldSlot ? *g_spWorldSlot : NULL;
    if (world) {
        static const DWORD kTry = 0;
        void* lk = (void*)(world + 0x918);
        g_p4ObLive = 2;
        if (g_spAcq(lk, NULL, &kTry) > 0) {  // one try, no wait: a sim holder keeps its lock, the record says "busy"
            P4ObWalk(world, g_p4ObL);
            g_spRel(lk, NULL);
            g_p4ObLive = 1;
        } else InterlockedIncrement(&ts3native_counters.p4ObstBusy);
    }
    g_p4ObW = g_spWrites - c.wPlan;
    g_p4ObUs = UsSince(t0);
    g_p4ObOn = true;
    InterlockedIncrement(&ts3native_counters.p4Obst);
    // docs/94 §24.8.4 step 1: once per capture, how many footprints each side walked, which frame listed them, the 4 nearest
    int fr[2][2] = {};
    for (int s = 0; s < 2; s++) {
        const P4ObSet& o = s ? g_p4ObL : g_p4ObC;
        for (int i = 0; i < o.n; i++) fr[s][o.o[i].fr == 'l']++;
    }
    InterlockedExchangeAdd(&ts3native_counters.ldObW, fr[0][0] + fr[1][0]);
    InterlockedExchangeAdd(&ts3native_counters.ldObL, fr[0][1] + fr[1][1]);
    static volatile LONG logs;
    if (InterlockedIncrement(&logs) > 16) return;
    Log("P4 obst plan %lu: walked clone %d, live %d (%s); lot 0x%016llX, box [%.2f,%.2f,%.2f,%.2f] lot frame [%.2f,%.2f,%.2f,%.2f]; "
        "listed clone %d (world %d, lot %d), live %d (world %d, lot %d)",
        c.seq, g_p4ObC.walked, g_p4ObL.walked, g_p4ObLive == 1 ? "ok" : g_p4ObLive == 2 ? "busy" : "noworld", g_p4ObC.lot, g_p4ObBox[0],
        g_p4ObBox[1], g_p4ObBox[2], g_p4ObBox[3], g_p4ObC.lb[0], g_p4ObC.lb[1], g_p4ObC.lb[2], g_p4ObC.lb[3], g_p4ObC.n, fr[0][0],
        fr[0][1], g_p4ObL.n, fr[1][0], fr[1][1]);
    for (int s = 0; s < 2; s++) {
        const P4ObSet& o = s ? g_p4ObL : g_p4ObC;
        for (int i = 0; i < o.nNear; i++) {
            const P4Ob& n = o.nearO[i];
            Log("P4 obst plan %lu: %s nearest %d: footprint 0x%llX vt 0x%08lX, %c-frame distance %.2f, bounds [%.3f,%.3f,%.3f,%.3f]",
                c.seq, s ? "live" : "clone", i, n.id, n.vt, n.fr, sqrtf(o.nearD[i]), n.b[0], n.b[1], n.b[2], n.b[3]);
        }
    }
}
static void P4PutObSet(const P4ObSet& s) {
    P4Put("[");
    for (int i = 0; i < s.n; i++) {
        const P4Ob& o = s.o[i];
        P4Put("%s[\"0x%llX\",\"0x%08lX\",%lu,%lu,%lu,%lu,\"0x%016llX\",", i ? "," : "", o.id, o.vt, o.f34, o.fme, o.lv, o.inv, o.h);
        for (int j = 0; j < 4; j++) { if (j) P4Put(","); P4PutF(o.b[j]); }
        P4Put(",\"%c\",", o.fr);  // 0.36.50: the frame that listed it, then fp+0x20 (docs/94 §24.8.4)
        P4PutKey(o.k);
        P4Put("]");
    }
    P4Put("]");
}
static void P4PutObNear(const P4ObSet& s) {
    P4Put("{\"walked\":%d,\"lot\":\"0x%016llX\",\"lbox\":", s.walked, s.lot);
    if (!s.haveL) P4Put("null");
    else { P4Put("["); for (int j = 0; j < 4; j++) { if (j) P4Put(","); P4PutF(s.lb[j]); } P4Put("]"); }
    P4Put(",\"near\":[");
    for (int i = 0; i < s.nNear; i++) {
        const P4Ob& o = s.nearO[i];
        P4Put("%s[\"0x%llX\",\"0x%08lX\",\"%c\",", i ? "," : "", o.id, o.vt, o.fr);
        P4PutF(sqrtf(s.nearD[i]));
        for (int j = 0; j < 4; j++) { P4Put(","); P4PutF(o.b[j]); }
        P4Put("]");
    }
    P4Put("]}");
}
static void P4PutOb() {
    static const char* const kLive[4] = {"none", "ok", "busy", "noworld"};
    P4Put(",\"obst\":{\"box\":[");
    for (int j = 0; j < 4; j++) { if (j) P4Put(","); P4PutF(g_p4ObBox[j]); }
    P4Put("],\"liveState\":\"%s\",\"liveW\":%ld,\"us\":%ld,\"cloneMore\":%d,\"liveMore\":%d,\"clone\":", kLive[g_p4ObLive & 3], g_p4ObW,
          g_p4ObUs, g_p4ObC.more, g_p4ObL.more);
    P4PutObSet(g_p4ObC);
    P4Put(",\"live\":");
    P4PutObSet(g_p4ObL);
    P4Put(",\"walkC\":");  // 0.36.50 (docs/94 §24.8.4 step 1)
    P4PutObNear(g_p4ObC);
    P4Put(",\"walkL\":");
    P4PutObNear(g_p4ObL);
    P4Put("}");
}

#include "p4door.h"
#include "p4lotdump.h"

static void P4Record(const P4Job& c, const char* refused, LONG nr) {
    g_p4Len = 0;
    P4Put("{\"v\":3,\"plan\":%lu,\"ic\":\"%s\",\"world\":\"%s\",\"search\":\"G\",\"epoch\":%lu,\"ver\":%lu,\"start\":", c.seq,
          kP4Ic[c.kind & 3], g_p4WorldTag, c.epoch, c.ver);
    P4PutPoint(c.starts, 0);
    P4Put(",\"goal\":");
    P4PutPoint(c.goals, g_p4Live.out2);
    P4Put(",\"tie\":[");  // the job's goal tie set (P4TieN): every record index, or empty
    for (int i = 0, n = P4TieN(c.starts, c.goals); i < n; i++) P4Put(i ? ",%d" : "%d", i);
    P4Put("],\"refused\":");
    if (refused) P4Put("\"%s\"", refused); else P4Put("null");
    if (refused && c.sub > PS_NONE && c.sub < PS_N && (!strcmp(refused, "nover") || !strcmp(refused, "mat") || !strcmp(refused, "epoch") || !strcmp(refused, "other")))
        P4Put(",\"sub\":\"%s\"", kPcSub[c.sub]);
    P4Put(",\"stale\":%s,\"nr\":%ld,\"m2\":%d,\"gameUs\":%ld,\"jobUs\":%ld,\"game\":", c.stale && c.ver ? "true" : "false", nr,
          c.mode >= 2, c.gameUs, c.jobUs);
    P4PutAns(g_p4Live);
    P4Put(",\"copy\":");
    if (refused) P4Put("null"); else P4PutAns(g_p4Copy);
    if (g_p4ObOn) P4PutOb();
    g_p4ObOn = false;
    if (g_p4LdRec) P4PutLd();
    g_p4LdRec = false;
    if (g_p4DdRec) P4PutDd(c);
    g_p4DdRec = false;
    P4Put("}\n");
    Ts3nCounters& k = ts3native_counters;
    DWORD w = 0;
    if (g_p4Len >= kP4BufCap - 64) InterlockedIncrement(&k.snJsonlFail);  // shortcut: a record over 1 MB is dropped, not split
    else if (g_p4Jsonl == INVALID_HANDLE_VALUE || (WriteFile(g_p4Jsonl, g_p4Buf, (DWORD)g_p4Len, &w, NULL) && w == g_p4Len))
        InterlockedIncrement(&k.snJsonl);
    else InterlockedIncrement(&k.snJsonlFail);
}

static volatile LONG g_p4HeldLog;
static bool P4HoldsRoutingLock() {
    __try {
        const u8* w = Readable(g_p4WorldSlot, 4) ? *(const u8* const*)g_p4WorldSlot : NULL;
        return w && Readable(w + 0x918, sizeof(CRITICAL_SECTION)) &&
               (DWORD)(uintptr_t)((const CRITICAL_SECTION*)(w + 0x918))->OwningThread == GetCurrentThreadId();
    } __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}
// --- parked shadows (0.34.1, docs/94 §21.16) ---
// A shadow never delays a live job: when one is queued at J_DONE or queues while the acquire waits for the consumer, 0.33.x
// dropped the shadow (689 of 4,091 BP records "mat"/"queued" on 0.33.3). Now one shadow can park: the job's captured state
// (arguments, door answers, live answer, its private buffers) moves aside and its cut stays held (p4clone.cpp keeps the hold
// on the oldest pending cut); it runs when the worker is next idle (after the queued job, before that job's own shadow, or
// from ServeAsync with nothing queued). The compare is the same: the copy at the job's cut against the job's live answer. A
// cut older than kPcPendMs is released by the next cut, so a parked shadow that waits too long is refused "passed".
static P4Job g_p4Park, g_p4Stash;
static P4Ans g_p4ParkLive, g_p4StashLive;
static bool g_p4Parked;
static volatile LONG g_p4ParkN, g_p4ParkRun;  // shadows parked / parked shadows run (log every 60 s with the async line)
static bool P4LiveQueued() {
    for (int i = 0; i < kJobs; i++) if (g_jobs[i].state == J_QUEUED) return true;
    return false;
}
static bool P4ParkCur() {  // g_p4Job (its shadow not run) moves to the park slot; its buffers go with it
    if (ts3native_counters.snP4Mode != 1) return false;
    if (g_p4Parked) {  // one slot: the older parked shadow gives way (its held cut would keep the consumer behind this job's)
        InterlockedIncrement(&ts3native_counters.snShadowBusy);
        P4CloneDrop(g_p4Park.ver);
        P4JobFree(g_p4Park);
        g_p4Parked = false;
    }
    memcpy(&g_p4Park, &g_p4Job, sizeof(P4Job));
    memcpy(&g_p4ParkLive, &g_p4Live, sizeof(P4Ans));
    g_p4Job.nBufs = 0, g_p4Job.armed = 0;
    g_p4Parked = true;
    InterlockedIncrement(&g_p4ParkN);
    return true;
}
static bool P4ShadowRun();
// the parked shadow, if any and nothing is queued; the current job's state (armed, not run yet) is kept aside meanwhile
static void P4ShadowParked() {
    if (!g_p4Parked || P4LiveQueued()) return;
    const bool cur = g_p4Job.armed != 0;
    if (cur) {
        memcpy(&g_p4Stash, &g_p4Job, sizeof(P4Job));
        memcpy(&g_p4StashLive, &g_p4Live, sizeof(P4Ans));
    }
    memcpy(&g_p4Job, &g_p4Park, sizeof(P4Job));
    memcpy(&g_p4Live, &g_p4ParkLive, sizeof(P4Ans));
    g_p4Parked = false;
    InterlockedIncrement(&g_p4ParkRun);
    if (g_p4ParkRun % 500 == 1) Log("P4: %ld shadows parked behind a live job so far, %ld run later", g_p4ParkN, g_p4ParkRun);
    if (P4ShadowRun()) P4JobFree(g_p4Job);  // false: parked again (a job queued during its wait), its buffers went along
    if (cur) {
        memcpy(&g_p4Job, &g_p4Stash, sizeof(P4Job));
        memcpy(&g_p4Live, &g_p4StashLive, sizeof(P4Ans));
    } else g_p4Job.nBufs = 0, g_p4Job.armed = 0;
}
// ServeAsync with nothing queued (worker idle)
static void P4ShadowIdle() {
    if (g_p4Parked) P4ShadowParked();
}

// worker, after J_DONE (§12.4.2 step 3)
static void P4Shadow() {
    P4Job& c = g_p4Job;
    if (!c.armed) { P4ShadowIdle(); return; }
    c.armed = 0;
    Ts3nCounters& k = ts3native_counters;
    if (!c.seen) {  // the IC returned before Route_PlanLocked (no goal, early out): nothing to compare, no record
        InterlockedIncrement(&k.snAbort[P4_AB_CAPTURE]);
        P4CloneDrop(c.ver);
        P4JobFree(c);
        P4ShadowIdle();
        return;
    }
    if (P4LiveQueued()) {  // the shadow never delays a live job's start: park it (or drop it when the slot is taken)
        if (!P4HoldsRoutingLock() && P4ParkCur()) return;
        InterlockedIncrement(&k.snShadowBusy);
        P4CloneDrop(c.ver);
        P4JobFree(c);
        return;
    }
    c.armed = 1;  // P4ShadowParked keeps an armed current job aside
    P4ShadowParked();  // an older parked shadow first: its cut is the held one
    c.armed = 0;
    if (P4ShadowRun()) P4JobFree(c);
}

// the shadow of g_p4Job (armed already cleared); false = parked (a live job queued while the acquire waited)
static bool P4ShadowRun() {
    P4Job& c = g_p4Job;
    Ts3nCounters& k = ts3native_counters;
    int why = c.abort;
    if (why < 0 && !(k.snP4Ready & 1)) why = P4_AB_OTHER;
    // 0.33.2 watchdog (docs/94 §21.15): a shadow never runs while this thread still owns the live routing lock (+0x918 is a
    // CRITICAL_SECTION at +0): the job released its hold before J_DONE, so one left here is a leak, and a shadow that then
    // waited on the clone would hold the game's lock meanwhile
    if (why < 0 && P4HoldsRoutingLock()) {
        why = P4_AB_OTHER, c.sub = PS_HELD;
        if (InterlockedIncrement(&g_p4HeldLog) <= 8) Log("P4: the worker still holds the routing lock after job %lu: shadow refused", c.seq);
    }
    if (why < 0 && (k.rtClosing || k.rtBusy || c.epoch != (DWORD)g_spEpoch)) why = P4_AB_EPOCH, c.sub = PS_SHADOW;
    if (why < 0 && !((g_p4Kinds >> (c.kind & 3)) & 1)) why = P4_AB_UNSUP;
    if (why < 0 && P4Cold()) why = P4_AB_COLD;
    P4Clone cl = {};
    bool have = false;
    if (why < 0) {
        LARGE_INTEGER t0;
        QueryPerformanceCounter(&t0);
        g_pcAcqWhy = PS_NONE;
        const int r = P4CloneAcquire(c.ver, &cl);
        InterlockedExchangeAdd(&k.snMatUs, UsSince(t0));
        if (r == P4_AB_MAT && g_pcAcqWhy == PS_QUEUED) {
            if (P4ParkCur()) return false;  // the cut stays held; the shadow runs after the queued job
            P4CloneDrop(c.ver);
        }
        if (r >= 0) { why = r; if (g_pcAcqWhy) c.sub = g_pcAcqWhy; } else have = true;
    } else P4CloneDrop(c.ver);  // refused before the acquire: the hold moves on to the next job's cut
    LONG rel = 0;
    if (have) {
        int head[4];
        P4Head(head, c);
        g_p4Phase = 5;
        rel = P4RunClone(c, cl, head, false);
        g_p4Phase = 6;
        if (c.abort >= 0) why = c.abort;
    }
    LONG nr1[kP4Fam];
    P4NrSnap(nr1);
    const LONG nr = P4NrBits(c.nr0, nr1, c.liveRead | c.cloneRead);
    for (int f = 0; f < kP4Fam; f++) if (nr & (1 << f)) InterlockedIncrement(f < 7 ? &k.snNrBits[f] : f == 7 ? &k.snNrBitDl : &k.snNrBitPortal);
    int cls = 4;
    InterlockedExchangeAdd(&k.snLiveUs, c.gameUs);
    if (why >= 0) InterlockedIncrement(&k.snAbort[why]);
    else if (g_p4Live.valid && g_p4Copy.valid) cls = P4Classify(g_p4Live, g_p4Copy, nr, P4TieN(c.starts, c.goals), c.stale);
    InterlockedIncrement(&k.snM[cls]);
    InterlockedIncrement(&k.snShadow);
    if (have && (cls == 2 || cls == 3) && g_p4ObsOn) P4ObCapture(c, cl.world);  // before the clone goes back
    if (have && cls < 4) P4DdCapture(c, cl.world, cls);
    if (have && cls < 4) P4LdCapture(c, cl.world, cls);
    P4Record(c, why >= 0 ? kP4Ab[why] : NULL, nr);
    if (have) P4CloneRelease(&cl, rel, c.dirty, c.nDirty);
    if (rel & P4REL_WRAP) g_p4Work.wrap = 0;
    return true;
}

// H20a (docs/109 §8.9, 0.30.0): while P4 is armed the portal count pair of vtable 0xFED048 is design SH's interlocked one
// (slotro.cpp InstallPortalRefX), because the clone run's lot searches AddRef / Release live portals on the worker while the
// sim does the same outside +0x918; then the replay clone takes one reference per portal it holds (H20b, replay.cpp)
static bool P4PortalRefs() {
    if (!InstallPortalRefX()) return false;
    ts3native_counters.slRefSwap = 1;
    g_rpPortalRefs = true;
    return true;
}

// [RouteSnap] Enabled=1 Mode=1, or Mode=2 for P5 (after ApplySnapImages; the async route, the gate with Sites=1 Enforce=1 and
// P1's sites first)
static bool ApplyP4(const wchar_t* ini) {
    Ts3nCounters& k = ts3native_counters;
    const int mode = GetPrivateProfileIntW(L"RouteSnap", L"Mode", 0, ini);
    if (!GetPrivateProfileIntW(L"RouteSnap", L"Enabled", 0, ini) || (mode != 1 && mode != 2)) {
        Log("route snapshot P4 off (needs [RouteSnap] Enabled=1 Mode=1, or Mode=2 for P5)");
        return false;
    }
    if (!g_spOn || !k.asyncReady || !k.rgEnforce || k.rtReady != RT_ALL || !g_planOrig) {
        Log("SKIP route snapshot P4: needs the P3 images (%ld), [RouteAsync] (%ld), [RouteGate] Enforce=1 (%ld) with every site "
            "(0x%lX) and the Route_PlanLocked detour (%d)", g_spOn, k.asyncReady, k.rgEnforce, k.rtReady, g_planOrig != NULL);
        return false;
    }
    g_p4Every = Clamp(GetPrivateProfileIntW(L"RouteSnap", L"ShadowEvery", 1, ini), 1, 1000);
    g_p4Kinds = Clamp(GetPrivateProfileIntW(L"RouteSnap", L"Kinds", 15, ini), 0, 15);
    g_p4ObsOn = GetPrivateProfileIntW(L"RouteSnap", L"ShadowObst", 1, ini) != 0;
    ApplyP4Dd(ini);
    ApplyP4Ld(ini);
    wchar_t tag[16];
    GetPrivateProfileStringW(L"RouteSnap", L"ShadowWorld", L"?", tag, 16, ini);
    for (int i = 0; i < 15; i++) {
        const wchar_t ch = tag[i];
        g_p4WorldTag[i] = (ch >= L'0' && ch <= L'9') || (ch >= L'A' && ch <= L'Z') || (ch >= L'a' && ch <= L'z') || ch == L'-' || ch == L'?'
                              ? (char)ch : 0;
        if (!ch || !g_p4WorldTag[i]) { g_p4WorldTag[i] = 0; break; }
    }
    g_p4Heap = g_p4Heap ? g_p4Heap : HeapCreate(0, 0, 0);
    g_p4Buf = g_p4Buf ? g_p4Buf : (char*)VirtualAlloc(NULL, kP4BufCap, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    if (!g_p4Heap || !g_p4Buf) { Log("FAIL route snapshot P4: no memory"); return false; }
    // door first (one byte check, nothing written yet), then the thunks: all or nothing
    u8 want[5] = {0xE8};
    *(LONG*)(want + 1) = (LONG)(P4_DOOR_TARGET - (P4_DOOR_AT + 5));
    const bool pvOwns = g_pvOn && g_pvJmp[PV_QUERY] == (void*)(uintptr_t)P4_DOOR_TARGET;
    if (!pvOwns && !Matches((const void*)(uintptr_t)P4_DOOR_AT, want, 5)) {
        Log("SKIP route snapshot P4: door call 0x%08lX differs, nothing patched", P4_DOOR_AT);
        return false;
    }
    LONG ready = P4PatchSites(g_p4At, (u8*)g_rgTramp[8]);
    if (!ready) return false;
    ready |= P4InstallDoor(P4_DOOR_AT, P4_DOOR_TARGET);
    if (!(ready & 12)) { Log("FAIL route snapshot P4: door stub not installed; P4 stays off (the thunks serve live values)"); return false; }
    if (!P4PortalRefs()) { Log("SKIP route snapshot P4: portal vtable 0xFED048 holds other count slots (H20a)"); return false; }
    static const DWORD kDlAt[3] = {kP4Dl[0].at, kP4Dl[1].at, kP4Dl[2].at};
    const int dl = P4InstallDl(kDlAt);
    if (dl != 3) Log("P4: display level family (D5): %d of 3 writer stubs installed; the others are not counted", dl);
    wchar_t path[MAX_PATH];
    wcscpy_s(path, ini);
    wchar_t* dot = wcsrchr(path, L'.');
    if (dot) *dot = 0;
    wcscat_s(path, L"-shadow.jsonl");
    g_p4Jsonl = CreateFileW(path, GENERIC_WRITE, FILE_SHARE_READ, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (g_p4Jsonl == INVALID_HANDLE_VALUE) Log("FAIL route snapshot P4: cannot open %ls (err %lu); records are counted only", path, GetLastError());
    k.snP4Mode = mode;
    k.snP4Ready = ready | (g_p4Clone ? 16 : 0);
    g_p4On = 1;
    Log("OK   route snapshot P4 (Mode=%d %s): T1-T4 %s, door stub %s, ShadowEvery=%ld Kinds=0x%lX world %s, clone provider %s", mode,
        mode == 2 ? "P5 take" : "shadow", ready & 2 ? "(T1 in the route gate trampoline)" : "(T1 own)",
        ready & 4 ? "behind [ProbeValues]" : "own call site", g_p4Every, g_p4Kinds, g_p4WorldTag,
        g_p4Clone ? "registered" : "none (every shadow refused \"clone\")");
    if (mode == 2) ApplyP5(ini);
    return true;
}
