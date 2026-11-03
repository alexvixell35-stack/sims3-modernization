// Routing gate measurement (docs/18 H6 steps S2a and S2b, docs/60 §2.3). Included by ts3native.cpp (one translation unit).
// Measurement only unless [RouteGate] Enforce=1 (H6 S3b, rgEnforce): then, while an async job runs (the worker holds +0x918
// for the whole job), an outermost gate entry by another thread takes +0x918 for the call (the owner's acquire pumps the
// job's managed calls, LockAcquireAsync), and the owner inside a callback it serves for the job borrows the job's hold
// (the worker is blocked in that callback): its gate entries and +0x918 acquires/releases skip the real lock (rgBorrows).
//
// Owner of the routing lock world+0x918 (learned in PlanLockedHook): Lock_Acquire 0x4E16F0 (shared LockAcquireHook) sets
// rgOwnerTid on every successful acquire of that lock (it returns the recursion count [lock+0x20], > 0); Lock_Release
// 0x4E17B0 clears it before the outermost release (count 1), so a thread that takes the lock right after is not clobbered.
// Gate sites: the 12 S0 sites (docs/18 "Gate-site list") plus sites 13-15 of docs/60 §2.2 (#3 LotTable_Contains, #7 the
// two coarse gates) and site 16, the coarse IC_World_FindPlacesOnShoreline gate (docs/67 §13, crash K), get pass-through stubs. An entry counts in rgUnlocked[site] when it is outermost on its thread (no
// other gate frame open) and the thread does not own +0x918; while a route-worker job (g_callerTid set) or an async job
// runs it also counts in rgJob[site]. Outermost needs the leave: the stub swaps the return address for RgLeaveThunk and
// keeps the real one on a per-thread stack (Frida's onLeave does the same). A frame left without returning (exception
// unwind, longjmp) is dropped at the thread's next entry: its return slot is at or below the new entry's, so it cannot
// still be live. The game imports no fiber API, so one stack per thread holds. rtsites.cpp (S2b) pushes reader frames
// (the worker's quadtree readers) on the same stack; they do not count as gate frames.
// G11 (docs/18 (3)): a sim-thread plan's free window runs from its return to the sim thread's next routing touch (an
// outermost +0x918 acquire, an unlocked gate entry, an object destroy, a quadtree write, the next plan); g11MovableUs sums
// min(plan length, window). Plan = the Route_PlanLocked detour on the sim thread (shortcut: the rest of IC_Route_Plan
// around it is not timed, so the plan length is a lower bound of what an async job would move).
// Verify probes on an address a stub here owns are counted from inside the stub (docs/68 §6 rule 3): VfEnter runs first,
// so this module reads the real caller through VfRealRet.
// shortcut: entries are counted, not P/Q reads that skip the leaves.

static const int RG_SITES = 16;
struct RgSite { DWORD addr; u8 len; u8 pro[10]; const char* name; };
// prologues verified on the Steam 1.67 exe (docs/18 S0 table, docs/60 §2.2); site 10 moves to Route_PlanCommon with G10-C
static RgSite kRgSites[RG_SITES] = {
    {0x0062EAB0, 6, {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF0}, "lot table find-or-insert"},
    {0x0063F490, 7, {0x83, 0xEC, 0x54, 0x8B, 0x44, 0x24, 0x5C}, "goal-step lot planner"},
    {0x0063D2F0, 6, {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF8}, "lazy room connectivity"},
    {0x0063CB90, 8, {0x83, 0xEC, 0x5C, 0x53, 0x8B, 0x5C, 0x24, 0x64}, "lazy lot build"},
    {0x0063D7B0, 5, {0x8B, 0x44, 0x24, 0x08, 0x55}, "lot rebuild"},
    {0x0063D860, 8, {0x8B, 0x44, 0x24, 0x08, 0x8B, 0x54, 0x24, 0x04}, "room graph refresh"},
    {0x0061E320, 5, {0x53, 0x8B, 0x5C, 0x24, 0x08}, "Q reset"},
    {0x0061E420, 7, {0x53, 0x56, 0x8B, 0xF1, 0x8B, 0x46, 0x18}, "Q reset (clear path)"},
    {0x0061E360, 8, {0x66, 0x83, 0x05, 0xC4, 0x80, 0x15, 0x01, 0x01}, "search generation stamp"},  // absolute operand
    {0x0061C020, 5, {0xB8, 0x70, 0x9C, 0x00, 0x00}, "Q node table clear"},  // the stack-probe call at +5 stays in place
    {0x0075C2D0, 6, {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF0}, "goal step (coarse)"},
    {0x007929F0, 6, {0x83, 0xEC, 0x14, 0x53, 0x33, 0xDB}, "IC_World_GetConnectedRoomsImpl (coarse)"},
    // docs/60 §2.2 #3 and #7 (S2b): stolen bytes end before the jz rel32 at +10 of the two coarse gates
    {0x00628A90, 8, {0x83, 0xEC, 0x08, 0x53, 0x8B, 0x5C, 0x24, 0x14}, "LotTable_Contains"},
    {0x00835240, 10, {0x56, 0x8B, 0xF1, 0x80, 0xBE, 0x7B, 0x09, 0x00, 0x00, 0x00}, "RoutingSystem_OnExitBuildBuy (coarse)"},
    {0x00835110, 10, {0x56, 0x8B, 0xF1, 0x80, 0xBE, 0x74, 0x09, 0x00, 0x00, 0x00}, "RoutingSystem_RebuildLotRouting (coarse)"},
    // docs/67 §13 (crash K): the IC reaches LotSearch_Entry 0x640E00 -> in-room A* 0x61E500 on live P+0xBC with no +0x918
    // (0x78C54A -> 0xC75920, call 0xC7630A); coarse like 0x7929F0: a job running = wait for +0x918, else no job starts
    {0x0078C440, 6, {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF0}, "IC_World_FindPlacesOnShoreline (coarse)"},
};
static const DWORD LOCK_RELEASE = 0x004E17B0;
static const u8 kLockReleasePrologue[] = {0x83, 0x41, 0x20, 0xFF, 0x80, 0x79, 0x24, 0x00};  // add [ecx+20h],-1; cmp byte [ecx+24h],0
static const DWORD* g_rtSimTidSlot = (const DWORD*)(uintptr_t)0x011D8A8C;  // game global: the sim thread (selftest: its own)

typedef LONG(__fastcall* LockReleaseFn)(void* lock, void* edx);  // 0x4E17B0 thiscall, no args, returns the new count
static LockReleaseFn g_rgRelOrig;
static void* g_rgTramp[RG_SITES];
static volatile LONG g_rgVf[RG_SITES];  // Verify probe index + 1 counted from inside the stub, 0 = none
static volatile LONG g_pvRg[RG_SITES];  // [ProbeValues] site index + 1 counted from inside the stub, 0 = none (values.cpp)
static void __cdecl PvEnter(int id, DWORD* slot);  // values.cpp
static volatile LONG g_snRg[RG_SITES];  // [RouteSnap] site index + 1 counted from inside the stub, 0 = none (routesnap.cpp)
static void __cdecl SnEnter(int id, DWORD* slot);  // routesnap.cpp

static void __cdecl VfEnter(int id, DWORD* slot);  // verify.cpp
static DWORD VfRealRet(const DWORD* slot);          // verify.cpp: the caller behind a Verify leave thunk in *slot
static volatile LONG g_p1bOn;                                     // lotfd.cpp: probe P1b
static void P1bNote(int site, DWORD key, const DWORD* slot, LONG us);  // lotfd.cpp
static bool SlGate(int site, DWORD* slot);                             // slotro.cpp
// t_p5bSite (p5b.cpp, 0.34.2: site + 1 of the gate waiting for +0x918 here, 0 none) is declared in ts3native.cpp (LockAcquireAsync reads it)
static __declspec(thread) DWORD* t_p5bSlot;    //   and its return slot (P5bGate: read-only gate leaves may take a lent lock)
// docs/189 phase 1 (p5b.cpp HoldKeyed): this thread holds +0x918 through an admitted re-keyed gate wait (set at the outer
// acquire); g_hgOpen = such holds open now. Cleared at the outermost release, before the lock goes, so a retake never sees one
static __declspec(thread) bool t_hgHeld;
static volatile LONG g_hgOpen;

// rel: what the leave undoes (S3b): RG_REL_LOCK releases `lock`, RG_REL_QT leaves g_qtCs, RG_REL_BUSY drops rtBusy,
// RG_REL_OFFSIM drops g_rgOffSimOpen
// RG_REL_STEER puts back the previous t_steerRet (kept in `lock`); RG_REL_QL releases the worker's QL hold (steerro.cpp);
// RG_REL_TRIP ends a T4 / T-S7 tripwire call (steerro.cpp TwLeave); RG_REL_P10 ends a P10 IC frame (p10.cpp, previous t_p10
// in `lock`); RG_REL_G10C ends a G10-C frame (t_g10cOpen, hold-time log); RG_REL_NR ends a P4 D2 writer window (g_snNrIn);
// RG_REL_G0QL releases [Gate0RO]'s QL shared hold, RG_REL_G0CMP compares its predicted entry (in `lock`) with eax (gate0ro.cpp)
enum { RG_REL_LOCK = 1, RG_REL_QT = 2, RG_REL_BUSY = 4, RG_REL_OFFSIM = 8, RG_REL_STEER = 16, RG_REL_QL = 32, RG_REL_TRIP = 64,
       RG_REL_P10 = 128, RG_REL_G10C = 256, RG_REL_NR = 512, RG_REL_G0QL = 1024, RG_REL_G0CMP = 2048,
       RG_REL_CE = 4096 };  // RG_REL_CE: our-events ring destroy-out (crashev.h, docs/186 §8; the object pointer in `lock`)
struct RgFrame { DWORD* slot; DWORD ret; LONG reader; LONG rel; void* lock; };
struct RgThread { int n; LONG readers; RgFrame f[16]; };
static __declspec(thread) RgThread t_rg;
static CRITICAL_SECTION g_qtCs;            // H6 S3b #1: physics quadtree, worker readers vs mutators during a job (rtsites.cpp)
static volatile LONG g_qlOn;               // [SteerRO] (steerro.cpp): the LotGraph_Build gate stub (site 3) is W4
static void QlReleaseEx();                                        // steerro.cpp
static bool QlPushWorker(RgThread& t, DWORD* slot, LONG reader);  // steerro.cpp
static void TwLeave();                                            // steerro.cpp
static void P10Leave(DWORD prev);                                 // p10.cpp
static void G10cLeave();
static volatile LONG g_g0On;                                                    // gate0ro.cpp
static LONG G0Gate(const DWORD* slot, DWORD tid, bool job, const u8** pred);  // gate0ro.cpp
static void G0Undo();                                                           // gate0ro.cpp
static void G0Check(const void* pred, DWORD eax);                               // gate0ro.cpp

static void RgUndo(const RgFrame& f) {
    if (f.rel & RG_REL_NR) InterlockedDecrement(&g_snNrIn);
    if (f.rel & RG_REL_TRIP) TwLeave();
    if (f.rel & RG_REL_P10) P10Leave((DWORD)(uintptr_t)f.lock);
    if (f.rel & RG_REL_G10C) G10cLeave();
    if (f.rel & RG_REL_QL) QlReleaseEx();
    if (f.rel & RG_REL_G0QL) G0Undo();
    if (f.rel & RG_REL_LOCK) LockReleaseHook(f.lock, NULL);
    if (f.rel & RG_REL_QT) LeaveCriticalSection(&g_qtCs);
    if (f.rel & RG_REL_BUSY) InterlockedDecrement(&ts3native_counters.rtBusy);
    if (f.rel & RG_REL_OFFSIM) RgOffSimDrop();
    if (f.rel & RG_REL_CE) CeRec(CE_DESTROY_OUT, (DWORD)(uintptr_t)f.lock);  // DestroyById returned
    if (f.rel & RG_REL_STEER) t_steerRet = (DWORD)(uintptr_t)f.lock;
}

// sites 0-11 v16, 12-14 v25, 15 v108
static inline volatile LONG* RgUnl(int s) {
    return s < 12 ? &ts3native_counters.rgUnlocked[s] : s < 15 ? &ts3native_counters.rgUnlocked2[s - 12] : &ts3native_counters.rgUnlocked3;
}
static inline volatile LONG* RgJob(int s) {
    return s < 12 ? &ts3native_counters.rgJob[s] : s < 15 ? &ts3native_counters.rgJob2[s - 12] : &ts3native_counters.rgJob3;
}
static inline bool RgJobLive() { return g_callerTid || g_async.state == A_RUNNING; }
static inline bool RgIsSim(DWORD tid) { return *g_rtSimTidSlot && tid == *g_rtSimTidSlot; }
static inline bool RgIsWorker(DWORD tid) { return g_workerTid && tid == g_workerTid; }

// frames left without returning (see the file comment)
static inline void RgDrop(RgThread& t, const DWORD* slot) {
    while (t.n && t.f[t.n - 1].slot <= slot) {
        const RgFrame& f = t.f[--t.n];
        t.readers -= f.reader;
        if (f.rel & RG_REL_LOCK) InterlockedIncrement(&ts3native_counters.rgDropLocked);
        if (f.rel) RgUndo(f);  // late, but a hold left behind would deadlock the next job
    }
}

static void __cdecl RgLeaveThunk();

// false: no frame left; the caller must undo `rel` itself (the call then runs without its closure's leave)
static bool RgPush(RgThread& t, DWORD* slot, LONG reader, LONG rel = 0, void* lock = NULL) {
    if (t.n == 16) { InterlockedIncrement(&ts3native_counters.rgUntracked); return false; }
    t.f[t.n].slot = slot;
    t.f[t.n].ret = *slot;
    t.f[t.n].reader = reader;
    t.f[t.n].rel = rel;
    t.f[t.n].lock = lock;
    t.n++;
    t.readers += reader;
    *slot = (DWORD)(uintptr_t)RgLeaveThunk;
    return true;
}

static CRITICAL_SECTION g_rgCs;  // guards the caller list only
static volatile LONG g_rgNoted;
static DWORD g_rgCallers[48];    // distinct (site << 24 | return address & 0xFFFFFF) of counted entries, logged once each

static LONG __fastcall LockReleaseHook(void* lock, void* edx) {
    // docs/165 F1: every check below needs +0x918 or the job's lock; any other lock ends in the original unchanged
    if (lock != g_rgLock && lock != g_async.lock) return g_rgRelOrig(lock, edx);
    if (t_snapRun) return g_rgRelOrig(lock, edx);  // P4 clone run: pass through (§12.4.3)
    // CE-4 (docs/154): the worker releasing +0x918 with its TailUnlock window open (an unwind through passes 16-19 caught in
    // Route_PlanLocked): every level back first, so the release is its own
    if (t_tuOpen && lock == g_async.lock) LendBackstop();
    if (t_borrow && lock == g_async.lock) { t_borrow--; return 1; }  // a borrowed acquire (LockAcquireHook)
    if (lock == g_rgLock && *(const LONG*)((const u8*)lock + 0x20) == 1) {
        if (t_hgHeld) t_hgHeld = false, InterlockedDecrement(&g_hgOpen);
        InterlockedCompareExchange(&ts3native_counters.rgOwnerTid, 0, (LONG)GetCurrentThreadId());
    }
    return g_rgRelOrig(lock, edx);
}

// first 48 distinct site/caller pairs (gate sites 0-15, rtsites.cpp sites 32+) with a line in ts3native.log
static void RgNoteAs(int site, const char* name, DWORD at, const char* what, DWORD ret, LONG tid) {
    if (g_rgNoted >= 48) return;
    const DWORD key = (DWORD)site << 24 | (ret & 0xFFFFFF);
    EnterCriticalSection(&g_rgCs);
    bool seen = false;
    for (LONG i = 0; i < g_rgNoted && !seen; i++) seen = g_rgCallers[i] == key;
    if (!seen && g_rgNoted < 48) {
        g_rgCallers[g_rgNoted++] = key;
        Log("route gate: %s %s 0x%08X from 0x%08X thread %ld (main %lu, sim %lu, worker %lu)%s", what, name, at, ret, tid, g_mainTid,
            *g_rtSimTidSlot, g_workerTid, RgJobLive() ? " during a job" : "");
    }
    LeaveCriticalSection(&g_rgCs);
}

// --- G10-C (docs/87 §5): [RouteGate] G10Common=1 moves gate site 10 from the goal step 0x75C2D0 to its only caller
// Route_PlanCommon 0x7606E0 (same prologue 55 8B EC 83 E4 F0). An outermost non-worker entry then waits once for a running
// job and holds +0x918 (or, with no job, keeps job starts off through g_rgOffSimOpen) for the whole sync plan: goal step,
// Route_PlanLocked and the EditPath tails. The goal step is not hooked then (nested in the frame anyway).
static const DWORD ROUTE_PLAN_COMMON = 0x007606E0;
static volatile LONG g_g10c;
static __declspec(thread) LARGE_INTEGER t_g10cT0;
static volatile LONG g_g10cLongHolds;
static const LONG kG10cLogMs = 100, kG10cLogLines = 20;

// true: site 10 now names `at` (Route_PlanCommon, or a test buffer); false: bytes differ, site 10 stays on the goal step
static bool RgUseG10Common(const u8* at) {
    if (!Matches(at, kRgSites[10].pro, kRgSites[10].len)) {
        char hex[64];
        if (Readable(at, 10)) Hex(at, 10, hex, sizeof(hex)); else strcpy_s(hex, "<unreadable>");
        Log("SKIP G10-C: Route_PlanCommon 0x%08X bytes %s differ, gate site 10 stays on the goal step", (DWORD)(uintptr_t)at, hex);
        return false;
    }
    kRgSites[10].addr = (DWORD)(uintptr_t)at;
    kRgSites[10].name = "Route_PlanCommon (coarse, G10-C)";
    g_g10c = 1;
    return true;
}

static LONG G10cEnter() {
    if (t_g10cOpen++ == 0) QueryPerformanceCounter(&t_g10cT0);
    return RG_REL_G10C;
}

// hold-time watchdog: log only (the frame guards real shared state, it cannot be cut short)
// shortcut: no counter for the hold time (v38 has no room left); add one if a G10-C A/B is planned
static void G10cLeave() {
    if (--t_g10cOpen) return;
    const LONG ms = UsSince(t_g10cT0) / 1000;
    if (ms >= kG10cLogMs && InterlockedIncrement(&g_g10cLongHolds) <= kG10cLogLines)
        Log("G10-C: a sync plan held job starts for %ld ms (thread %lu)", ms, GetCurrentThreadId());
}

// --- G11: per-thread plan state; only the sim thread opens windows ---
struct G11State { int depth; LARGE_INTEGER t0, end; LONG planUs; };
static __declspec(thread) G11State t_g11;

static bool G11PlanBegin() {
    if (ts3native_counters.rgReady != 7 || !RgIsSim(GetCurrentThreadId())) return false;
    G11State& g = t_g11;
    if (g.depth == 0) {
        G11Touch(G11_PLAN, 0x04000000);  // a plan right after a plan closes the window too
        QueryPerformanceCounter(&g.t0);
    }
    g.depth++;
    return true;
}

static void G11PlanEnd(bool begun) {
    if (!begun) return;
    G11State& g = t_g11;
    if (--g.depth) return;
    QueryPerformanceCounter(&g.end);
    g.planUs = (LONG)((g.end.QuadPart - g.t0.QuadPart) * 1000000 / g_qpf.QuadPart);
}

static void G11Touch(int kind, DWORD key) {
    G11State& g = t_g11;
    if (g.depth || !g.end.QuadPart || !RgIsSim(GetCurrentThreadId())) return;
    LARGE_INTEGER now;
    QueryPerformanceCounter(&now);
    LONGLONG gap = (now.QuadPart - g.end.QuadPart) * 1000000 / g_qpf.QuadPart;
    if (gap > 1000000) gap = 1000000;
    g.end.QuadPart = 0;
    Ts3nCounters& k = ts3native_counters;
    const LONG movable = gap < g.planUs ? (LONG)gap : g.planUs;
    InterlockedIncrement(&k.g11Plans);
    InterlockedExchangeAdd(&k.g11PlanUs, g.planUs);
    InterlockedExchangeAdd(&k.g11MovableUs, movable);
    InterlockedExchangeAdd(&k.g11GapUs, (LONG)gap);
    InterlockedIncrement(&k.g11End[kind]);
    // vfG11CloseBy: only the sim thread gets here, so the table needs no interlocked insert
    int i = 0;
    while (i < 32 && k.g11CloseKey[i] && k.g11CloseKey[i] != (LONG)key) i++;
    if (i == 32) { InterlockedIncrement(&k.g11CloseOther); return; }
    k.g11CloseKey[i] = (LONG)key;
    InterlockedIncrement(&k.g11CloseN[i]);
    InterlockedExchangeAdd(&k.g11CloseLostUs[i], g.planUs - movable);
}

// Stub side: slot = the caller's return-address slot ([esp] at the site's entry).
// docs/182 §8 live finding (p5c-recs-0339, BP and IP: ulLockTrip 4 per run, copy mismatches 7 / 30 with RecsUnlocked=1; the
// site named by p5c-site-0340): the game's ApplyInsert / ApplyRemove on a clone Q ask LotTable_GetIfPresent 0x62FB60 (sites 12
// LotTable_Contains and 0 lot table find-or-insert) for the Q's lot node, on the LIVE planner's table, and this entry then
// took the live +0x918 when a job ran (the acquire counts ulLockTrip; with no job the site ran on the consumer unlocked).
// Fix (replay.cpp RpGetIfPresent): inside a round the ask goes to the clone's own P copy and lot table, so sites 0 and 12 are
// clone-private there and pass through here (no frame, no lock). Any other site inside a round is still unexpected: the
// first 8 entries name the site, the caller and the record type, and the entry goes on as any unlocked entry (lock model
// rpu-trip-p5c: no deadlock).
static bool RpUlGate(int site, const DWORD* slot) {  // true = pass through
    if (site == 0 || site == 12) return true;
    static LONG n;
    if (InterlockedIncrement(&n) <= 8)
        Log("snap replay: RecsUnlocked round reached gate site %d (%s 0x%08lX) from 0x%08lX, record type %ld, job %s", site,
            kRgSites[site].name, kRgSites[site].addr, VfRealRet(slot), (g_rpPhase >> 8) & 0xFF, g_async.state == A_RUNNING ? "running" : "none");
    return false;
}

static void __cdecl RgEnter(int site, DWORD* slot) {
    if (t_tuOpen) TuTrip(1);  // TailUnlock: no gate leaf in passes 16-19 (docs/150 §11)
    if (t_rpUl && RpUlGate(site, slot)) return;  // docs/182 §8: the clone's lot table inside a RecsUnlocked round: no gate
    if (g_snRg[site]) SnEnter(g_snRg[site] - 1, slot);  // first: reads the caller's return address before a probe swaps it
    if (g_rgVf[site]) VfEnter(g_rgVf[site] - 1, slot);
    if (g_pvRg[site]) PvEnter(g_pvRg[site] - 1, slot);  // count only: no frame, *slot unchanged
    if (t_snapRun) {  // P4 clone run (docs/94 §12.4.3): pass through; site 8's trampoline still runs the T1 thunk
        if (site == 3) P4Lazy();  // D4: a lazy LotGraph_Build in a clone run: P4Lazy stops the run before it (0.30.0, H20c)
        return;
    }
    Ts3nCounters& k = ts3native_counters;
    if (k.rgReady != 7 || !g_rgLock) return;  // before the lock is known nobody's hold can be told apart
    InterlockedIncrement(&k.rgEntries);
    RgThread& t = t_rg;
    RgDrop(t, slot);
    // P4 D2 (§17.4): the two coarse rebuild gates wait for a running clone run and keep the next one out until they return;
    // a reader-marked frame, so the outermost-gate test below is unchanged
    if (g_p4On && (site == 13 || site == 14) && !RgIsWorker(GetCurrentThreadId())) {
        P5cNrEnter();  // P4NrEnter; P5c: never with the lent lock held
        if (!RgPush(t, slot, 1, RG_REL_NR)) InterlockedDecrement(&g_snNrIn);
    }
    if (site == 10 && t_p10) t_p10 |= P10_REACHED;  // P10: this IC reached the goal step (Route_PlanCommon with G10-C)
    const DWORD tid = GetCurrentThreadId();
    const LONG me = (LONG)tid;
    if (t.n == t.readers && k.rgOwnerTid != me) {  // no gate frame open on this thread
        InterlockedIncrement(RgUnl(site));
        if (RgJobLive()) InterlockedIncrement(RgJob(site));
        const bool sim = RgIsSim(tid), worker = RgIsWorker(tid);
        if (sim) G11Touch(site < 12 ? G11_GATE : G11_GATE13, 0x01000000 | site);
        else if (!worker) InterlockedIncrement(&k.vfRtGateOffSim);
        RgNoteAs(site, kRgSites[site].name, kRgSites[site].addr, "unlocked entry", VfRealRet(slot), me);
        if (k.rgEnforce && !worker) {
            // design SH (slotro.cpp): inside the hold a Contains or a GetOrInsert hit is a read QL shared covers, so it passes
            // without +0x918; anything else drops the hold (QL released) and goes on below as today
            if (t_slHold == 1 && SlGate(site, slot)) {
                InterlockedIncrement(&g_rgOffSimOpen);
                if (!RgPush(t, slot, 0, RG_REL_OFFSIM)) RgOffSimDrop();
                return;
            }
            // every non-worker frame is published before the job state is read; the worker sets RUNNING before it reads
            // them (ServeAsync), so a leaf entry that saw no job and a job start never overlap
            InterlockedIncrement(&g_rgOffSimOpen);
            LONG rel = RG_REL_OFFSIM;
            void* lock = g_async.state == A_RUNNING ? g_async.lock : NULL;
            // [Gate0RO] (gate0ro.cpp): a G-a hit runs under QL shared with no wait; shadow: the same prediction, no job
            if (site == 0 && g_g0On) {
                const u8* pred;
                if (const LONG g0 = G0Gate(slot, tid, lock != NULL, &pred)) {
                    rel |= g0;
                    if (!RgPush(t, slot, 0, rel, (void*)pred)) RgUndo(RgFrame{NULL, 0, 0, rel, (void*)pred});
                    return;
                }
            }
            if (lock && tid == g_async.owner && g_inInvoke) {
                InterlockedIncrement(&k.rgBorrows);  // the worker waits in this callback: nested in the job, as without us
            } else if (lock) {
                static const DWORD kInfinite = 0xFFFFFFFF;
                LARGE_INTEGER t0;
                QueryPerformanceCounter(&t0);
                const DWORD key = VfRealRet(slot);
                t_waitKey = key, t_p5bSite = site + 1, t_p5bSlot = slot;  // P1 (docs/84 §4): keyed by the leaf's caller; P5b
                LockAcquireHook(lock, NULL, &kInfinite);  // the owner pumps the job's managed calls while it waits
                t_waitKey = 0, t_p5bSite = 0;
                const LONG us = UsSince(t0);
                if (g_p1bOn || (site == 10 && g_p10On)) P1bNote(site, key, slot, us);  // P1b: and by the caller's caller (P10: by IC)
                InterlockedIncrement(&k.rgGateWaits);
                InterlockedExchangeAdd(&k.rgGateWaitUs, us);
                Max(&k.rgGateWaitMaxUs, us);
                G0wGateWait(key, us);
                rel |= RG_REL_LOCK;
            }
            if (site == 10 && g_g10c) rel |= G10cEnter();  // G10-C: this frame spans the whole sync plan (after the wait)
            if (!RgPush(t, slot, 0, rel, lock)) RgUndo(RgFrame{NULL, 0, 0, rel, lock});
            return;
        }
    }
    // W4 (docs/83 §6): LotGraph_Build on the worker holds QL exclusive for the call (level vector, transform, nested W1/W3/W6)
    if (site == 3 && g_qlOn && RgIsWorker(GetCurrentThreadId())) { QlPushWorker(t, slot, 0); return; }
    RgPush(t, slot, 0);
}

static DWORD __cdecl RgLeave(DWORD eax = 0) {  // eax: what the site returned (RgLeaveThunk)
    RgThread& t = t_rg;
    const RgFrame f = t.f[--t.n];
    t.readers -= f.reader;
    if (f.rel & RG_REL_G0CMP) G0Check(f.lock, eax);
    if (f.rel) RgUndo(f);
    return f.ret;
}

// The site returned here (ret or ret N): back to its real caller with eax, edx, flags and the x87 stack as the site left them.
static __declspec(naked) void __cdecl RgLeaveThunk() {
    __asm {
        push eax  // becomes the return address
        pushad
        pushfd
        push eax
        call RgLeave
        add esp, 4
        mov [esp + 36], eax
        popfd
        popad
        ret
    }
}

// Site entry: count, then the site's stolen prologue (trampoline) with the same registers, flags and stack.
#define RG_STUB(i) static __declspec(naked) void RgStub##i() { __asm pushad __asm pushfd __asm lea eax, [esp + 36] \
    __asm push eax __asm push i __asm call RgEnter __asm add esp, 8 __asm popfd __asm popad __asm jmp dword ptr [g_rgTramp + i * 4] }
RG_STUB(0) RG_STUB(1) RG_STUB(2) RG_STUB(3) RG_STUB(4) RG_STUB(5) RG_STUB(6) RG_STUB(7) RG_STUB(8) RG_STUB(9) RG_STUB(10) RG_STUB(11)
RG_STUB(12) RG_STUB(13) RG_STUB(14) RG_STUB(15)
static void (*const kRgStubs[RG_SITES])() = {RgStub0, RgStub1, RgStub2, RgStub3, RgStub4, RgStub5, RgStub6, RgStub7, RgStub8,
                                             RgStub9, RgStub10, RgStub11, RgStub12, RgStub13, RgStub14, RgStub15};

// [RouteGate] Enabled=1: true when every byte the gate will patch matches (16 sites, Lock_Acquire, Lock_Release); the
// Route_PlanLocked prologue is checked by its own InstallDetour. One mismatch: nothing of the gate is installed.
static bool ReadRouteGate(const wchar_t* ini) {
    if (!GetPrivateProfileIntW(L"RouteGate", L"Enabled", 0, ini)) { Log("route gate disabled (Enabled=0)"); return false; }
    if (GetPrivateProfileIntW(L"RouteGate", L"G10Common", 0, ini) && RgUseG10Common((const u8*)(uintptr_t)ROUTE_PLAN_COMMON))
        Log("route gate: G10-C on, gate site 10 at Route_PlanCommon 0x%08X (one frame per sync plan)", ROUTE_PLAN_COMMON);
    char hex[64];
    for (int i = 0; i < RG_SITES; i++) {
        const u8* p = (const u8*)(uintptr_t)kRgSites[i].addr;
        if (Matches(p, kRgSites[i].pro, kRgSites[i].len)) continue;
        if (Readable(p, 10)) Hex(p, 10, hex, sizeof(hex)); else strcpy_s(hex, "<unreadable>");
        Log("SKIP route gate: %s 0x%08X bytes %s differ, nothing hooked", kRgSites[i].name, kRgSites[i].addr, hex);
        return false;
    }
    // async (installed in between) may own Lock_Acquire by then; InstallRouteGate reuses that detour
    if (!Matches((const void*)(uintptr_t)LOCK_ACQUIRE, kLockAcquirePrologue, sizeof(kLockAcquirePrologue)) ||
        !Matches((const void*)(uintptr_t)LOCK_RELEASE, kLockReleasePrologue, sizeof(kLockReleasePrologue))) {
        Log("SKIP route gate: Lock_Acquire/Lock_Release bytes differ, nothing hooked");
        return false;
    }
    return true;
}

static bool InstallRouteGate(bool planHooked) {
    if (!planHooked) { Log("FAIL route gate: Route_PlanLocked detour missing (the lock is learned there), nothing hooked"); return false; }
    InitializeCriticalSection(&g_rgCs);
    Ts3nCounters& k = ts3native_counters;
    if (!g_lockOrig)
        g_lockOrig = (LockAcquireFn)InstallDetour("Lock_Acquire (route gate)", LOCK_ACQUIRE, kLockAcquirePrologue, sizeof(kLockAcquirePrologue), (void*)LockAcquireHook, &g_lockOrig);
    if (!g_lockOrig) return false;
    g_rgRelOrig = (LockReleaseFn)InstallDetour("Lock_Release (route gate)", LOCK_RELEASE, kLockReleasePrologue, sizeof(kLockReleasePrologue), (void*)LockReleaseHook, &g_rgRelOrig);
    if (!g_rgRelOrig) { k.rgReady = 1; return false; }
    int n = 0;
    for (int i = 0; i < RG_SITES; i++)
        n += (g_rgTramp[i] = InstallDetour(kRgSites[i].name, kRgSites[i].addr, kRgSites[i].pro, kRgSites[i].len, (void*)kRgStubs[i], &g_rgTramp[i])) != NULL;
    k.rgReady = 3 | (n == RG_SITES ? 4 : 0);
    if (g_g10c && g_rgTramp[10]) k.p10Ready |= 2;  // counting needs all three (7); a partial set stays pass-through
    Log("%s route gate: %d of %d gate sites, owner tracking on Lock_Acquire/Lock_Release (measurement only)",
        n == RG_SITES ? "OK  " : "FAIL", n, RG_SITES);
    return n == RG_SITES;
}
