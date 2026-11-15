// [RouteJobPhases] (docs/150 §7, counters v87), observe only: where the route worker's job time goes. Per job kind (own START,
// own A10 kind, foreign/fallback START) and phase: entries and SELF time (a phase nested in another is subtracted from the outer
// one, so the phases plus RJP_OTHER sum to the job time exactly; nothing is counted twice). Included by ts3native.cpp after
// loadprof.cpp, installed after every other section except [Verify].
//
// Timing runs only on the worker inside a job: ServeAsync sets g_rjpTid to the worker's id around CallIc, and every stub first
// compares the TEB thread id (fs:[0x24]) with it, so other threads (the sim thread's allocator calls and plans included) pay a
// compare and a jump. On the worker the stub is loadprof.cpp's pattern: pushad/pushfd, RjpEnter(phase, return slot), the slot
// swapped for RjpLeaveThunk, the real return kept on a per-thread stack of 48 (frames an exception unwound are dropped at the
// next entry). The phase code runs with the same registers, flags and stack.
// Sites: an entry whose prologue still matches gets our detour. An entry another section owns is timed one step later instead:
// our stub is put into that section's trampoline pointer (Route_PlanLocked g_planOrig, Lock_Acquire g_lockOrig, world A*
// g_eoAsOrig, LotGraph_Build g_rgTramp[3] or g_lpJmp[LP_LOTGRAPH], ApplyPendingUpdates g_rtTramp[RT_PENDING]), so the owner's
// stub sees the real caller first and its own leave thunk is what we save and restore (LIFO). Host-call waits are a frame
// pushed by Proxy itself (RJP_HOST): plans the sim sends back during the wait are its children.
// Phases (docs/150 §2): lock = Lock_Acquire on the worker (contended wait plus the uncontended call; +0x918 is taken before the
// job clock starts); alloc = EA_AllocTagged + EA_Free (their callees are the allocator); candidate = PathPlanner_PlanHierarchical
// own code + LotSearch_Entry; lot leg = LotPlan own code incl. LotPlan_Inner and InRoomPlan own code; plan = Route_PlanLocked
// own code (RoutingJob_Execute, PathPlan_WithRetries bookkeeping, sub-path split).
// shortcut: each timed call costs two QPC reads on the worker (allocator calls dominate); the job time grows by that, so read
// shares, not absolute ms, and compare rjpJobUs with asyncUs. Upgrade to rdtsc if the overhead shows above ~5%.
// v92 (docs/150 §14): allocator frames also go to a per-call-site table (row = allocator return address + the return address of
// the innermost open phase frame), a size histogram, PathNodes_Insert's grow capacities and allocator calls per job.
// Self time is carried per phase in ticks (t.rem) so frames shorter than 1 us are not truncated to 0 (v87 dropped up to ~1 us
// per allocator call).
// Prologues checked by bytes (whole instructions, no relative operand, Steam 1.67, docs/150 §7). The branch-into scan of the
// stolen bytes (I-150-2) was done for the fix: no intra-function branch lands inside any of the 15 prologues; steerro-test
// STEERRO_PHASES=1 runs K8's planner with the stubs on.

enum { RJP_PLAN, RJP_CELL, RJP_POST, RJP_INSERT, RJP_WORLD, RJP_LOTLEG, RJP_PORTAL, RJP_CAND, RJP_LOTGRAPH, RJP_PENDING, RJP_WATER,
       RJP_ALLOC, RJP_LOCK, RJP_HOST, RJP_OTHER, RJP_N };
static_assert(RJP_N == 15, "rjpN/rjpUs/rjpMaxUs are [15] (counters v87)");
static_assert(RJP_LOTGRAPH == kP6LazyPhase, "p5.cpp P6 phase split reads the LotGraph_Build row");
// docs/177 §5 probe P-G ([RouteJobPhases] GoalSplit=1, counters v102 pg*): four more phases of the worker's job, outside v87's
// rows (pgN/pgUs/pgMaxUs by phase - RJP_GGEN). Their self time used to be RJP_OTHER ("rest of job"); with GoalSplit on,
// rest = RJP_OTHER + these four. Not timed in sim sync plan sessions (docs/164 rows stay as they were).
// docs/178 T3 probe P-GS ([RouteJobPhases] GoalStepSplit=1, needs GoalSplit=1, counters v103 gs*): eight sites timed only
// when called directly from the goal step 0x75C2D0 (or from one of them inside it), so their self time leaves the goal step's
// self time and nothing else (v87 rows stay as they were); a goal step >= 50 ms is logged with the split and its QPC window
enum { RJP_GGEN = RJP_N, RJP_GSTEP, RJP_GEDIT, RJP_GVAL, RJP_GS0, RJP_NALL = RJP_GS0 + 8 };
static void GgVal(DWORD ret, u8 al);  // goalgen.cpp: D1 shadow, the validation answers of both rings
static void CmCell(LONGLONG incl);  // cellmemo.cpp ([CellMemoProbe], docs/150 §12): a closed cell A* frame
static void CmJob(bool begin);      // cellmemo.cpp: job start / end
// syncphases.cpp ([RouteJobPhases] SyncPlans=1, docs/164, counters v97): the same stubs on sim-thread sync plans
enum { RJP_KSYNC = 3 };  // t_rjp.kind of a sync plan session (not a v87 row: its frames go to SypFrame)
struct RjpFrame;
static bool SypEnter(int site, DWORD* slot);
static void SypFrame(const RjpFrame& f, LONG self, LONGLONG incl);
static void SypAfterLeave(DWORD eax);
static LONG SypLoopOf(DWORD ret, const DWORD* slot);

struct RjpSite { DWORD at; u8 len; u8 pro[10]; u8 phase; const char* name; };
enum { RJS_PLAN, RJS_CELL, RJS_POST, RJS_INSERT, RJS_WORLD, RJS_LOTLEG, RJS_PORTAL, RJS_CAND, RJS_LOTSEARCH, RJS_LOTGRAPH, RJS_PENDING,
       RJS_WATER, RJS_ALLOC, RJS_FREE, RJS_LOCK, RJS_V87, RJS_GGEN = RJS_V87, RJS_GSTEP, RJS_GEDIT, RJS_GVAL, RJS_GS0,
       RJS_N = RJS_GS0 + 8 };
static const RjpSite kRjpSites[RJS_N] = {
    {TS3A(0x0082C6E0), 6, {0x81, 0xEC, 0xD8, 0x00, 0x00, 0x00}, RJP_PLAN, "Route_PlanLocked"},
    {TS3A(0x0061E500), 6, {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF0}, RJP_CELL, "QuadTreeCellAStar"},
    {TS3A(0x0064E380), 7, {0x80, 0x3D, 0x4C, 0x82, 0x15, 0x01, 0x00}, RJP_POST, "path-node post pass"},  // absolute operand
    {TS3A(0x00629140), 5, {0x83, 0xEC, 0x08, 0x53, 0x55}, RJP_INSERT, "PathNodes_Insert"},
    {TS3A(0x00676260), 8, {0x83, 0xEC, 0x3C, 0x53, 0x8B, 0x5C, 0x24, 0x58}, RJP_WORLD, "WorldAStar_Search"},
    {TS3A(0x006410B0), 8, {0x8B, 0x44, 0x24, 0x34, 0xD9, 0x44, 0x24, 0x10}, RJP_LOTLEG, "LotPlan"},
    {TS3A(0x0063E340), 6, {0x81, 0xEC, 0x14, 0x01, 0x00, 0x00}, RJP_PORTAL, "LotPortalAStar"},
    {TS3A(0x0065E0D0), 6, {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF0}, RJP_CAND, "PathPlanner_PlanHierarchical"},
    {TS3A(0x00640E00), 6, {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF8}, RJP_CAND, "LotSearch_Entry"},
    {TS3A(0x0063CB90), 8, {0x83, 0xEC, 0x5C, 0x53, 0x8B, 0x5C, 0x24, 0x64}, RJP_LOTGRAPH, "LotGraph_Build"},
    {TS3A(0x0082C420), 9, {0x57, 0x8B, 0xF9, 0x8B, 0x87, 0x5C, 0x09, 0x00, 0x00}, RJP_PENDING, "RoutingWorld_ApplyPendingUpdates"},
    {TS3A(0x00671930), 7, {0x8B, 0x44, 0x24, 0x24, 0x83, 0xEC, 0x50}, RJP_WATER, "WaterPath_Plan"},
    {TS3A(0x004E3F90), 8, {0x8B, 0x44, 0x24, 0x0C, 0x8B, 0x4C, 0x24, 0x04}, RJP_ALLOC, "EA_AllocTagged"},
    {TS3A(0x004E3F70), 6, {0x8B, 0x44, 0x24, 0x04, 0x85, 0xC0}, RJP_ALLOC, "EA_Free"},
    {TS3A(0x004E16F0), 5, {0x53, 0x55, 0x56, 0x8B, 0xF1}, RJP_LOCK, "Lock_Acquire"},
    // P-G (v102): push ebp; mov ebp,esp; and esp,-16 (no branch into the 6 bytes, linear sweep of .text)
    {TS3A(0x00751FF0), 6, {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF0}, RJP_GGEN, "Route_GenerateDestinationCandidates"},
    {TS3A(0x0075C2D0), 6, {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF0}, RJP_GSTEP, "goal step"},
    {TS3A(0x0085FF30), 6, {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF0}, RJP_GEDIT, "RouteFollow_EditPathForRoute"},
    {TS3A(0x00759B20), 6, {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF0}, RJP_GVAL, "GoalPoint_Validate"},
    // P-GS (v103, docs/178 §1.3): prologues as docs/178 read them; every one but 0x63EC40 is already a live detour of
    // [RouteGate] (0x63F490, 0x63D2F0, 0x61E320) or its Sites (the other four) with the same stolen length
    {TS3A(0x0063F490), 7, {0x83, 0xEC, 0x54, 0x8B, 0x44, 0x24, 0x5C}, RJP_GS0 + 0, "LotPlanner_GoalRooms"},
    {TS3A(0x0063D2F0), 6, {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF8}, RJP_GS0 + 1, "RoomGraph_FirstUse"},
    {TS3A(0x0063EC40), 6, {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF8}, RJP_GS0 + 2, "LotPortalSearch"},
    {TS3A(0x0061E320), 5, {0x53, 0x8B, 0x5C, 0x24, 0x08}, RJP_GS0 + 3, "AStarSearch_Reset"},
    {TS3A(0x0082EDB0), 6, {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF0}, RJP_GS0 + 4, "RoutingWorld_ObstacleQuery"},
    {TS3A(0x0082F0B0), 6, {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF0}, RJP_GS0 + 5, "GoalFootprintGather"},
    {TS3A(0x0083B3A0), 5, {0x83, 0xEC, 0x10, 0x53, 0x55}, RJP_GS0 + 6, "Routing_PhysicsRangeQuery"},
    {TS3A(0x00C68680), 6, {0x83, 0xEC, 0x1C, 0x53, 0x55, 0x56}, RJP_GS0 + 7, "WorldManager_RebuildLotIndex"},
};
static const char* const kGsShort[8] = {"lot planner", "room graph", "portal search", "Q reset", "obstacle query", "gather",
                                        "range query", "lot index rebuild"};

static void* g_rjpJmp[RJS_N];       // where each stub goes on: our trampoline, or the owner's trampoline we took the place of
static volatile DWORD g_rjpTid;     // the worker's id while it runs a job, else 0 (stubs pass straight through)
// g_rjpSyncTid (docs/164): defined in ts3native.cpp before LockAcquireHook (its fast path reads it)
static volatile LONG g_rjpAny[RJS_N];  // docs/164: 1 = every thread enters RjpEnter at this site (SyncPlans: Route_PlanLocked)

struct RjpFrame { DWORD* slot; DWORD ret, parent, arg; int phase, site; LONGLONG t0, child; };
// child = time of the job's outermost frames; rem = sub-us ticks per phase not yet counted; rem100 = the same for v92's 100 ns rows
// pgUs / pgVal: this job's P-G self time (us) by phase and its validations (v102)
// jobUs: this job's v87 self time by phase (docs/178 T1 long-job line); gsPh / gsPhN: self time (us) and frames by phase of
// every frame closed inside the open goal step, any depth (0.36.32, docs/178 §6.5: step = its own code + these, exactly)
struct RjpThread {
    int n, kind;
    LONG allocs;
    LONGLONG child, rem[RJP_NALL], rem100;
    LONG pgUs[4], pgVal, jobUs[RJP_N], gsPh[RJP_NALL], gsPhN[RJP_NALL];
    RjpFrame f[48];
};
static __declspec(thread) RjpThread t_rjp;

static void __cdecl RjpLeaveThunk();
static LONGLONG RjpNow() { LARGE_INTEGER q; QueryPerformanceCounter(&q); return q.QuadPart; }
static LONG RjpUs(LONGLONG ticks) { return (LONG)(ticks * 1000000 / g_qpf.QuadPart); }

static int RaLog2(DWORD v) { int i = 0; while (v > 1 && i < 31) v >>= 1, i++; return i; }

// v92: one allocator frame into the site table (only the worker writes; the probe reads), the size and grow histograms
static void RaRecord(RjpThread& t, const RjpFrame& f, LONGLONG selfTicks) {
    Ts3nCounters& k = ts3native_counters;
    const bool alloc = f.site == RJS_ALLOC;
    t.allocs++;
    InterlockedIncrement(alloc ? &k.raAllocN : &k.raFreeN);
    const LONGLONG h = selfTicks * 10000000 + t.rem100;  // 100 ns units, remainder carried
    const LONG hns = (LONG)(h / g_qpf.QuadPart);
    t.rem100 = h % g_qpf.QuadPart;
    if (alloc) {
        const int b = RaLog2(f.arg) - 2;
        InterlockedIncrement(&k.raSizeLog2[b < 0 ? 0 : b > 15 ? 15 : b]);
        if (f.ret == TS3A(0x006291E3)) {  // PathNodes_Insert's grow: size = new capacity x 64
            const int c = RaLog2(f.arg >> 6);
            InterlockedIncrement(&k.raInsCap[c > 15 ? 15 : c]);
            InterlockedExchangeAdd(&k.raInsUs[c > 15 ? 15 : c], hns);
        }
    }
    const DWORD hash = (f.ret * 0x9E3779B1u) ^ (f.parent * 0x85EBCA6Bu);
    for (int i = 0; i < 64; i++) {
        const int r = (hash + i) & 63;
        if (!k.raN[r]) { k.raSite[r] = (LONG)f.ret; k.raParent[r] = (LONG)f.parent; }  // a fresh row: keys first, then the count
        else if (k.raSite[r] != (LONG)f.ret || k.raParent[r] != (LONG)f.parent) continue;
        InterlockedIncrement(&k.raN[r]);
        InterlockedExchangeAdd(&k.raUs[r], hns);
        if (alloc) InterlockedExchangeAdd(&k.raBytes[r], (LONG)f.arg);
        return;
    }
    InterlockedIncrement(&k.raFull);
}

static const char* const kRjpPhName[RJP_GS0] = {"plan", "cell A*", "post", "insert", "world A*", "lot leg", "portal A*",
                                                "candidate", "lot graph", "pending", "water", "alloc", "lock", "host", "other",
                                                "goal gen", "goal step", "EditPathForRoute", "validate"};

// P-GS (v103): a goal step frame closed; >= 50 ms logged (worker only, 20 lines a minute) with the per-site self split, every
// other timed phase inside the step (self), the step's own code as "unattributed", the sites' share of the whole step and its
// QPC window in ms (joins the -hitches.jsonl `t` of a sim wait). 0.36.31 printed only the 8 sites' self time and divided it by
// (sites + step self): nested v87/P-G frames (cell A*, portal A*, validate, ...) were in neither, so a 172 ms step read
// "self 0 ms ... P-GS 97%" with ~1 ms of sites (docs/178 §6.5).
// shortcut: a goal step nested in another (not seen) resets the outer one's split; upgrade if gsSteps > goal step frames.
static void GsStepEnd(RjpThread& t, const RjpFrame& f, LONG self, LONGLONG incl) {
    Ts3nCounters& k = ts3native_counters;
    InterlockedIncrement(&k.gsSteps);
    const LONG inclUs = RjpUs(incl);
    LONG sum = 0;
    for (int i = 0; i < 8; i++) sum += t.gsPh[RJP_GS0 + i];
    if (inclUs >= 50000) {
        InterlockedIncrement(&k.gsLong);
        static DWORD minute, lines;
        const DWORD now = GetTickCount();
        if (now - minute >= 60000) minute = now, lines = 0;
        if (lines++ < 20) {
            char split[400], other[600];
            int at = 0;
            for (int i = 0; i < 8; i++) {
                const LONG us = t.gsPh[RJP_GS0 + i];
                at += sprintf_s(split + at, sizeof(split) - at, "%s%s %ld.%ld/%ld", i ? ", " : "", kGsShort[i], us / 1000,
                                us / 100 % 10, t.gsPhN[RJP_GS0 + i]);
            }
            at = 0;
            other[0] = 0;
            for (int i = 0; i < RJP_GS0; i++)
                if (t.gsPhN[i])
                    at += sprintf_s(other + at, sizeof(other) - at, "%s%s %ld.%ld/%ld", at ? ", " : "", kRjpPhName[i],
                                    t.gsPh[i] / 1000, t.gsPh[i] / 100 % 10, t.gsPhN[i]);
            const LONGLONG q0 = f.t0 * 1000 / g_qpf.QuadPart, q1 = (f.t0 + incl) * 1000 / g_qpf.QuadPart;
            Log("route phases: goal step %ld ms qpc %lld-%lld ms, job kind %d route %08lX: P-GS sites %s; other phases inside %s "
                "(self ms/frames); unattributed %ld ms (the step's own code); P-GS %ld%% of the step", inclUs / 1000, q0, q1, t.kind,
                g_async.handle, split, at ? other : "none", self / 1000, inclUs > 0 ? (LONG)((LONGLONG)sum * 100 / inclUs) : 0);
        }
    }
    memset(t.gsPh, 0, sizeof(t.gsPh));
    memset(t.gsPhN, 0, sizeof(t.gsPhN));
}

// adds one finished frame: its self time to its phase, its whole time to the frame below (or the job)
static void RjpClose(RjpThread& t, const RjpFrame& f, LONGLONG now) {
    Ts3nCounters& k = ts3native_counters;
    const LONGLONG incl = now - f.t0;
    const LONGLONG selfTicks = incl - f.child + t.rem[f.phase];
    const LONG self = RjpUs(selfTicks);
    t.rem[f.phase] = selfTicks - (LONGLONG)self * g_qpf.QuadPart / 1000000;
    if (t.kind == RJP_KSYNC) SypFrame(f, self, incl);  // docs/164: per-plan sums; the v87/v92 rows and the cell memo stay the worker's
    else if (f.phase >= RJP_GS0) {  // P-GS (v103)
        const int p = f.phase - RJP_GS0;
        InterlockedIncrement(&k.gsN[p]);
        InterlockedExchangeAdd(&k.gsUs[p], self);
        Max(&k.gsMaxUs[p], self);
    } else if (f.phase >= RJP_N) {  // P-G (v102)
        const int p = f.phase - RJP_N;
        InterlockedIncrement(&k.pgN[p]);
        InterlockedExchangeAdd(&k.pgUs[p], self);
        Max(&k.pgMaxUs[p], self);
        t.pgUs[p] += self;
        if (f.phase == RJP_GVAL) t.pgVal++;
        if (f.phase == RJP_GSTEP && k.gsReady) GsStepEnd(t, f, self, incl);
    } else {
        if (f.phase == RJP_ALLOC) RaRecord(t, f, incl - f.child);
        t.jobUs[f.phase] += self;
        InterlockedIncrement(&k.rjpN[t.kind][f.phase]);
        InterlockedExchangeAdd(&k.rjpUs[t.kind][f.phase], self);
        Max(&k.rjpMaxUs[f.phase], self);
        if (f.phase == RJP_CELL) CmCell(incl);
    }
    if (k.gsReady && t.kind != RJP_KSYNC)  // P-GS: inside an open goal step (any depth): its per-step split (t.n = ancestors here)
        for (int i = 0; i < t.n; i++)
            if (t.f[i].phase == RJP_GSTEP) { t.gsPh[f.phase] += self; t.gsPhN[f.phase]++; break; }
    if (t.n) t.f[t.n - 1].child += incl; else t.child += incl;
}

// frames whose return slot is at or below `slot` were left without returning (unwound): dropped, their time stays in the parent
static void RjpDrop(RjpThread& t, DWORD* slot) {
    while (t.n && t.f[t.n - 1].slot <= slot) { t.n--; InterlockedIncrement(&ts3native_counters.rjpDropped); }
}

// slot = the return-address slot (stubs) or any address in the pushing function's frame (manual frames, swap = false)
static bool RjpPush(int phase, DWORD* slot, bool swap) {
    RjpThread& t = t_rjp;
    RjpDrop(t, slot);
    if (t.n == 48) { InterlockedIncrement(&ts3native_counters.rjpDropped); return false; }
    RjpFrame& f = t.f[t.n++];
    f.slot = slot;
    f.ret = swap ? *slot : 0;
    f.arg = swap ? slot[1] : 0;  // the first stack argument (EA_AllocTagged: size)
    f.parent = t.n > 1 ? t.f[t.n - 2].ret : 0;
    f.phase = phase;
    f.site = -1;
    f.child = 0;
    f.t0 = RjpNow();
    if (swap) *slot = (DWORD)(uintptr_t)RjpLeaveThunk;
    return true;
}

// pops the frame whose slot is `at` or just below it (and any left above it); returns its saved return address.
// Searched from the outermost frame like loadprof's LpLeave: 12 of the 15 sites return with `ret N` (callee-cleanup), so the
// leave thunk sees at = slot + N. The old top-down `slot < at` walk took our own frame for unwound and popped the parent's,
// returning into the parent's caller with the wrong esp: TS3W.exe vanished ~10 s into every phases job (docs/150 §7 fix,
// docs/67, ledger §NEW-T). An outer frame's slot is above at: the callee's N argument bytes lie below it.
static __declspec(thread) int t_rjpClosedSite;  // site of the frame RjpPop just closed (RjpLeave: D1 shadow answers)
static DWORD RjpPop(DWORD* at) {
    RjpThread& t = t_rjp;
    const LONGLONG now = RjpNow();
    int i = 0;
    while (i < t.n && t.f[i].slot > at) i++;  // frames above the one found were unwound without a leave
    if (i == t.n) return 0;
    while (t.n - 1 > i) { t.n--; InterlockedIncrement(&ts3native_counters.rjpDropped); }
    const RjpFrame f = t.f[i];
    t.n = i;
    RjpClose(t, f, now);
    t_rjpClosedSite = f.site;
    return f.ret;
}

static void __cdecl RjpEnter(int site, DWORD* slot) {
    if (site >= RJS_V87 && (t_rjp.kind == RJP_KSYNC || GetCurrentThreadId() != g_rjpTid)) return;  // P-G: worker jobs only
    if (site >= RJS_GS0 && (!t_rjp.n || (t_rjp.f[t_rjp.n - 1].phase != RJP_GSTEP && t_rjp.f[t_rjp.n - 1].phase < RJP_GS0)))
        return;  // P-GS: directly inside a goal step (or inside another P-GS frame there) only
    if (g_rjpAny[site] && !SypEnter(site, slot)) return;  // docs/164: a thread outside a job or session (not the sim's plan)
    if (!RjpPush(kRjpSites[site].phase, slot, true)) return;
    RjpFrame& f = t_rjp.f[t_rjp.n - 1];
    f.site = site;
    if (site == RJS_WORLD && t_rjp.kind == RJP_KSYNC) f.arg = SypLoopOf(f.ret, slot);  // which planner loop called the world A*
}
// eax = the leaving function's return value (docs/164: Route_PlanLocked's result in AL)
static DWORD __cdecl RjpLeave(DWORD* at, DWORD eax) {
    t_rjpClosedSite = -1;
    const DWORD r = RjpPop(at);
    if (t_rjpClosedSite == RJS_GVAL) GgVal(r, (u8)eax);  // r = the validation's return address (its caller)
    if (t_rjp.kind == RJP_KSYNC) SypAfterLeave(eax);
    return r;
}

static __declspec(naked) void __cdecl RjpLeaveThunk() {
    __asm {
        push eax  // becomes the return address
        pushad
        pushfd
        push eax  // the function's return value (pushad/pushfd leave eax as it was)
        lea eax, [esp + 40]
        push eax
        call RjpLeave
        add esp, 8
        mov [esp + 36], eax
        popfd
        popad
        ret
    }
}

// thread check first (TEB +0x24 = thread id): only the worker inside a job, the sim inside a sync plan session (docs/164) and,
// at a g_rjpAny site, any thread reach pushad
#define RJP_STUB(i) static __declspec(naked) void RjpStub##i() { __asm push eax __asm mov eax, dword ptr fs:[0x24] \
    __asm cmp eax, g_rjpTid __asm je on##i __asm cmp eax, g_rjpSyncTid __asm je on##i \
    __asm cmp dword ptr [g_rjpAny + i * 4], 0 __asm je off##i \
    __asm on##i: __asm pop eax __asm pushad __asm pushfd __asm lea eax, [esp + 36] __asm push eax \
    __asm push i __asm call RjpEnter __asm add esp, 8 __asm popfd __asm popad __asm jmp pass##i \
    __asm off##i: __asm pop eax __asm pass##i: __asm jmp dword ptr [g_rjpJmp + i * 4] }
RJP_STUB(0) RJP_STUB(1) RJP_STUB(2) RJP_STUB(3) RJP_STUB(4) RJP_STUB(5) RJP_STUB(6) RJP_STUB(7) RJP_STUB(8) RJP_STUB(9) RJP_STUB(10)
RJP_STUB(11) RJP_STUB(12) RJP_STUB(13) RJP_STUB(14) RJP_STUB(15) RJP_STUB(16) RJP_STUB(17) RJP_STUB(18)
RJP_STUB(19) RJP_STUB(20) RJP_STUB(21) RJP_STUB(22) RJP_STUB(23) RJP_STUB(24) RJP_STUB(25) RJP_STUB(26)
static void (*const kRjpStubs[RJS_N])() = {RjpStub0, RjpStub1, RjpStub2, RjpStub3, RjpStub4, RjpStub5, RjpStub6, RjpStub7,
                                           RjpStub8, RjpStub9, RjpStub10, RjpStub11, RjpStub12, RjpStub13, RjpStub14,
                                           RjpStub15, RjpStub16, RjpStub17, RjpStub18, RjpStub19, RjpStub20, RjpStub21,
                                           RjpStub22, RjpStub23, RjpStub24, RjpStub25, RjpStub26};

// P-G (v102): validations per job, and a log line for a job whose rest of job (OTHER + the four phases) is >= 50 ms
static void PgJobEnd(const RjpThread& t, LONG us, LONG other) {
    Ts3nCounters& k = ts3native_counters;
    static const LONG kValEdge[5] = {1, 19, 36, 71, 201};
    int b = 0;
    while (b < 5 && t.pgVal >= kValEdge[b]) b++;
    InterlockedIncrement(&k.pgValHist[b]);
    Max(&k.pgValMax, t.pgVal);
    const LONG rest = other + t.pgUs[0] + t.pgUs[1] + t.pgUs[2] + t.pgUs[3];
    if (rest < 50000) return;
    InterlockedIncrement(&k.pgLongRest);
    static DWORD minute, lines;  // worker only: 20 lines a minute
    const DWORD now = GetTickCount();
    if (now - minute >= 60000) minute = now, lines = 0;
    if (lines++ >= 20) return;
    Log("route phases: long rest %ld ms of a %ld ms job (kind %d, route %08lX): other %ld, goal gen %ld, goal step %ld, "
        "EditPathForRoute %ld, validate %ld ms (%ld validations)", rest / 1000, us / 1000, t.kind, g_async.handle, other / 1000,
        t.pgUs[0] / 1000, t.pgUs[1] / 1000, t.pgUs[2] / 1000, t.pgUs[3] / 1000, t.pgVal);
}

// ServeAsync around CallIc: kind 0 own START, 1 own A10, 2 foreign
static void RjpJobBegin(int kind) {
    if (!ts3native_counters.rjpReady) return;
    RjpThread& t = t_rjp;
    t.n = 0;
    t.kind = kind;
    t.child = 0;
    CmJob(true);
    t.allocs = 0;
    memset(t.rem, 0, sizeof(t.rem));
    memset(t.pgUs, 0, sizeof(t.pgUs));
    t.pgVal = 0;
    memset(t.jobUs, 0, sizeof(t.jobUs));
    memset(t.gsPh, 0, sizeof(t.gsPh));
    memset(t.gsPhN, 0, sizeof(t.gsPhN));
    InterlockedExchange((volatile LONG*)&g_rjpTid, (LONG)GetCurrentThreadId());
}

// docs/178 T1: a foreign job >= 300 ms, its self time by phase and where the deadline cut it (t_fbjdCut, set by ServeAsync
// before RjpJobEnd: searches cut in world A* / cell A* / water). Goal step = P-G's row (GoalSplit=1), else inside "other".
// Worker only, 20 lines a minute
static void FbjdPhaseLine(const RjpThread& t, LONG us) {
    Ts3nCounters& k = ts3native_counters;
    static DWORD minute, lines;
    const DWORD now = GetTickCount();
    if (now - minute >= 60000) minute = now, lines = 0;
    if (lines++ >= 20) return;
    InterlockedIncrement(&k.fbjdLines);
    char gs[24];
    if (k.pgReady & 2) sprintf_s(gs, "%ld", t.pgUs[1] / 1000); else strcpy_s(gs, "n/a");
    const LONG* p = t.jobUs;
    Log("route phases: foreign job %ld ms (route %08lX, deadline %s): world %ld, cell %ld, water %ld, portal %ld, lot legs %ld, "
        "goal step %s, candidate %ld, lot graph %ld, plan %ld, post %ld, insert %ld, host %ld, alloc %ld, other %ld ms; cut world %ld, "
        "cell %ld, water %ld", us / 1000, g_async.handle, k.fbjdMode == 1 ? "on" : "off", p[RJP_WORLD] / 1000, p[RJP_CELL] / 1000,
        p[RJP_WATER] / 1000, p[RJP_PORTAL] / 1000, p[RJP_LOTLEG] / 1000, gs, p[RJP_CAND] / 1000, p[RJP_LOTGRAPH] / 1000,
        p[RJP_PLAN] / 1000, p[RJP_POST] / 1000, p[RJP_INSERT] / 1000, p[RJP_HOST] / 1000, p[RJP_ALLOC] / 1000, p[RJP_OTHER] / 1000,
        t_fbjdCut[0], t_fbjdCut[1], t_fbjdCut[2]);
}
static void RjpJobEnd(LONG us) {
    Ts3nCounters& k = ts3native_counters;
    if (!g_rjpTid) return;
    InterlockedExchange((volatile LONG*)&g_rjpTid, 0);
    RjpThread& t = t_rjp;
    const LONGLONG now = RjpNow();
    while (t.n) { const RjpFrame f = t.f[--t.n]; RjpClose(t, f, now); }  // shortcut: a frame still open at the end (none expected)
    CmJob(false);
    LONG other = us - RjpUs(t.child);
    if (other < 0) other = 0;  // the job clock started a few QPC reads before ours
    InterlockedIncrement(&k.rjpJobs[t.kind]);
    static const LONG kJobEdge[7] = {1, 16, 64, 256, 1024, 4096, 16384};
    int b = 0;
    while (b < 7 && t.allocs >= kJobEdge[b]) b++;
    InterlockedIncrement(&k.raJobHist[b]);
    InterlockedExchangeAdd(&k.rjpJobUs[t.kind], us);
    InterlockedIncrement(&k.rjpN[t.kind][RJP_OTHER]);
    InterlockedExchangeAdd(&k.rjpUs[t.kind][RJP_OTHER], other);
    Max(&k.rjpMaxUs[RJP_OTHER], other);
    t.jobUs[RJP_OTHER] = other;
    if (k.pgReady) PgJobEnd(t, us, other);
    if (t.kind == 2 && us >= 300000 && (k.fbjdReady & 4)) FbjdPhaseLine(t, us);
}
// Proxy (host-call wait): a manual frame, no return slot swapped
static bool RjpHostBegin(DWORD* frame) { return g_rjpTid && GetCurrentThreadId() == g_rjpTid && RjpPush(RJP_HOST, frame, false); }
static void RjpHostEnd(DWORD* frame) { RjpPop(frame); }

// One site: our detour when the prologue matches; else the trampoline pointer of the section that owns it (owner != NULL and
// set); else skipped. Parameters let the selftest use local memory.
static bool RjpInstallAt(int i, DWORD at, void** owner) {
    const RjpSite& s = kRjpSites[i];
    if (Matches((const void*)(uintptr_t)at, s.pro, s.len)) {
        char name[80];
        sprintf_s(name, "%s (route phases)", s.name);
        return (g_rjpJmp[i] = InstallDetour(name, at, s.pro, s.len, (void*)kRjpStubs[i], &g_rjpJmp[i])) != NULL;
    }
    if (owner && *owner) {
        g_rjpJmp[i] = *owner;
        InterlockedExchangePointer(owner, (void*)kRjpStubs[i]);
        Log("OK   route phases %s: timed inside its owner's stub", s.name);
        return true;
    }
    Log("SKIP route phases %s: bytes at 0x%08lX differ and no known owner", s.name, at);
    return false;
}

// the trampoline pointer of a [RouteGate] gate site / Sites site at `at` (its stub's jmp target), NULL = not detoured there.
// v103: by address (RT_* is a kind, not a table index: RJS_PENDING used to take g_rtTramp[5], the ray leaf's slot)
static void** RgOwnerAt(DWORD at) {
    for (int i = 0; i < RG_SITES; i++)
        if (kRgSites[i].addr == at && g_rgTramp[i]) return &g_rgTramp[i];
    return NULL;
}
static void** RtOwnerAt(DWORD at) {
    for (int i = 0; i < RT_N; i++)
        if (kRtSites[i].addr == at && g_rtTramp[i]) return &g_rtTramp[i];
    return NULL;
}

static void** RjpOwner(int i) {
    if (i >= RJS_GS0) {  // P-GS: [RouteGate] gate stub, its Sites stub, [LoadProf]'s room graph entry
        const DWORD at = kRjpSites[i].at;
        if (void** o = RgOwnerAt(at)) return o;
        if (void** o = RtOwnerAt(at)) return o;
        return at == TS3A(0x0063D2F0) && g_lpJmp[LP_ROOMFIRST] ? &g_lpJmp[LP_ROOMFIRST] : NULL;
    }
    switch (i) {
        case RJS_PLAN: return (void**)&g_planOrig;
        case RJS_LOCK: return (void**)&g_lockOrig;
        case RJS_WORLD: return (void**)&g_eoAsOrig;
        case RJS_LOTGRAPH: return g_rgTramp[3] ? &g_rgTramp[3] : &g_lpJmp[LP_LOTGRAPH];
        case RJS_PENDING: return RtOwnerAt(kRjpSites[RJS_PENDING].at);
        case RJS_CELL: return &g_pdOrig[0];  // docs/164 §7 [RouteAsync] PlanDeadlineMs entry stubs (plandeadline.cpp)
        case RJS_WATER: return &g_pdOrig[1];
        case RJS_GSTEP: return g_rgTramp[10] ? &g_rgTramp[10] : NULL;  // [RouteGate] goal step (coarse) entry stub
        case RJS_GEDIT: return g_avTwTramp[1] ? &g_avTwTramp[1] : NULL;  // [AvoidRO] T-AV wrapper
        default: return NULL;
    }
}

static bool SypConfig(const wchar_t* ini);  // syncphases.cpp: [RouteJobPhases] SyncPlans
static void SypArm();

static bool ApplyRouteJobPhases(const wchar_t* ini) {
    const bool worker = GetPrivateProfileIntW(L"RouteJobPhases", L"Enabled", 0, ini) != 0, sync = SypConfig(ini);
    if (!worker && !sync) { Log("route job phases off (Enabled=0, SyncPlans=0)"); return false; }
    Ts3nCounters& k = ts3native_counters;
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
    for (int i = 0; i < RJS_V87; i++) {
        if (RjpInstallAt(i, kRjpSites[i].at, RjpOwner(i))) k.rjpHooks++;
        else k.rjpSkipped++;
    }
    k.rjpReady = worker && k.rjpHooks ? 1 : 0;  // SyncPlans alone: worker jobs stay untimed (cell memo and HostSplit stay off)
    Log("%s route job phases: %ld of %d sites timed (%ld skipped), %s", k.rjpSkipped ? "PART" : "OK  ", k.rjpHooks, (int)RJS_V87,
        k.rjpSkipped, worker ? (sync ? "worker jobs and sim sync plans" : "worker jobs only") : "sim sync plans only");
    if (k.rjpReady && GetPrivateProfileIntW(L"RouteJobPhases", L"GoalSplit", 0, ini)) {  // P-G (docs/177 §5, v102)
        LONG bits = 0;
        for (int i = RJS_V87; i < RJS_GS0; i++)
            if (RjpInstallAt(i, kRjpSites[i].at, RjpOwner(i))) bits |= 1 << (i - RJS_V87);
        k.pgReady = bits;
        Log("%s route phases goal split: sites 0x%lX of 0xF timed", bits == 15 ? "OK  " : "PART", bits);
    }
    if (GetPrivateProfileIntW(L"RouteJobPhases", L"GoalStepSplit", 0, ini)) {  // P-GS (docs/178 T3, v103)
        if (!(k.pgReady & 2)) {
            Log("SKIP route phases goal step split: needs Enabled=1 and GoalSplit=1 with the goal step site timed");
        } else {
            LONG bits = 0;
            for (int i = RJS_GS0; i < RJS_N; i++)
                if (RjpInstallAt(i, kRjpSites[i].at, RjpOwner(i))) bits |= 1 << (i - RJS_GS0);
            k.gsReady = bits;
            Log("%s route phases goal step split (P-GS): sites 0x%lX of 0xFF timed", bits == 0xFF ? "OK  " : "PART", bits);
        }
    }
    if (k.rjpReady && g_fbjdIni) k.fbjdReady |= 4;  // docs/178 T1: the per-phase line of foreign jobs >= 300 ms
    if (sync) SypArm();
    return true;
}
