// [HitchAttr] HitchMs / RouteLockMs / JobWaitLogMs (docs/666 §5), log only, all off by default. Included by ts3native.cpp
// after stallwatch.cpp (needs hitchattr.cpp HoRole, syncphases.cpp t_syp / g_sypOn, memexp.cpp g_mx, simloop.cpp).
//
// HitchMs=N: one "hitch:" line at the end of every Simulate iteration (sim frame) of >= N ms with the frame's ms split:
//   sync      sim-thread sync plans ([RouteJobPhases] SyncPlans=1 gives the phases world / water / post / insert / cell /
//             lot (lot leg + portal A* + lot graph) / lock / other; without it only the IC plans [RouteAsync] times, as other);
//   jobwait   sim waits for a route job (SwNote: runplan, rtwait, disc, collect, drop), with the longest and its site;
//   lock918   sim waits on the routing lock world+0x918, with the longest, its key, the holder and `job250` = the part spent
//             behind a worker job that ended inside the wait after running >= 250 ms (the docs/666 §2 hypothesis);
//   otherlock sim waits on other game locks (complete with [HitchAttr] Holders=1, else only while a route job runs);
//   gc        the per-frame GC_try_to_collect step (call-site timer 0xD819AA, memexp.cpp's when [GC] SqrtHeapLimit owns it,
//             then forced collections too) and +N = collections finished in the frame (GC_gc_no);
//   limiter   sim pump return -> loop-back (the frame limiter's sleep, simloop.cpp SimPumpAdd), -1 = not measured;
//   cpu       the sim thread's kernel + user time in the frame (GetThreadTimes; ~16 ms steps), -1 = not measured;
//   unknown   the frame minus the above (waits inside a sync plan are taken out once: `insync`).
// Cause = the largest of sync work (sync minus its lock phase and job waits), jobwait, lock918, otherlock, gc, limiter when
// it covers >= 50% of the frame; else by the CPU time nothing above explains: sim-cpu (script or native work nothing
// logs), off-cpu (the sim thread waited on something nothing logs), mixed, or unknown without a CPU time.
// Managed script time has no cheap hook ([MonoJit] Time is per method): sim-cpu is its upper bound, [StallWatch] Managed
// names the script frames.
// RouteLockMs=N: a "route lock wait:" line per sim or main thread wait >= N ms on +0x918: who waits, how long, the holder's
// thread and role, the job the worker ended inside the wait (route, kind, ms) or the running one. Counters by holder in the
// export `ts3native_hitchsum`, printed every 60 s ("hitch totals:").
// JobWaitLogMs=N (default 300, the old fixed value): threshold of "route jobs: sim waited N ms for a job" lines.
// Cost when off: one global test at each of the six call sites (after a wait or a plan that already took its own timers).
// When on: the +0x918 acquire of the sim / main thread outside the job pump gets two QPC reads (the acquire itself is the
// same single call), each sim frame one QPC, one GetThreadTimes and one GetTickCount.
// shortcut: waits nested in another wait's pump are counted twice (as hitchattr.cpp); unknown is clamped at 0.

enum { HSP_WORLD, HSP_WATER, HSP_POST, HSP_INSERT, HSP_CELL, HSP_LOT, HSP_LOCK, HSP_OTHER, HSP_N };
enum { HSC_SYNC, HSC_JOBWAIT, HSC_LOCK918, HSC_OTHERLOCK, HSC_GC, HSC_LIMITER, HSC_CPU, HSC_OFFCPU, HSC_MIXED, HSC_UNKNOWN, HSC_N };
static const char* const kHsPhase[HSP_N] = {"world", "water", "post", "insert", "cell", "lot", "lock", "other"};
static const char* const kHsCause[HSC_N] = {"sync-plan", "job-wait", "lock918", "other-lock", "gc", "limiter", "sim-cpu", "off-cpu", "mixed", "unknown"};
static const char* const kHsSite[SW_N] = {"runplan", "rtwait", "disc", "collect", "drop", "other"};

struct HsFrame {  // one sim frame, us; the sim thread only
    LONG syncN, syncUs, syncPh[HSP_N];
    LONG jobN, jobUs, jobInSyncUs, jobMaxUs, jobMaxSite;
    DWORD jobMaxH;
    LONG lockN, lockUs, lockInSyncUs, lockMaxUs, lockMaxRole, lockJob250Us;
    DWORD lockMaxKey, lockMaxTid;
    LONG olockN, olockUs, olockInSyncUs;
    LONG gcUs;
};
struct HsView { LONG frameUs, cpuUs, limiterUs, gcN; HsFrame f; };  // cpuUs / limiterUs -1 = not measured

// export for test tools (not ts3native_counters: no layout change for a diagnostic, as ts3native_blockmax)
struct HsTotals {
    LONG version;               // +0x00, 1
    LONG hitchUs;               // +0x04, HitchMs in us (0 = off)
    LONG lockLogUs;             // +0x08, RouteLockMs in us (0 = off)
    LONG frames, hitches;       // +0x0C, sim frames seen / of them >= HitchMs
    LONG hitchMs;               // +0x14, their summed ms
    LONG causeN[HSC_N];         // +0x18, hitches by cause (HSC_*)
    LONG causeMs[HSC_N];        // +0x40
    // +0x918 waits >= 1 ms of the sim / main thread
    LONG lkWaits, lkUs, lkMaxUs;  // +0x68
    LONG lkLines, lkDropped;      // +0x74, "route lock wait:" lines written / rate-limited
    LONG lkWorkerN, lkWorkerUs;   // +0x7C, the route worker held it (or lent it out) with a job
    LONG lkJobEndN, lkJobEndUs;   // +0x84, of those: a worker job ended inside the wait
    LONG lkJob250N, lkJob250Us;   // +0x8C, of those: that job ran >= 250 ms
    LONG lkOtherN, lkOtherUs;     // +0x94, any other holder
    LONG lkMainN, lkMainUs;       // +0x9C, of all: the waiter was the main thread
    LONG hitchLock250;            // +0xA4, hitches with >= 50% of the frame on +0x918 behind a worker job >= 250 ms
    LONG lines, dropped;          // +0xA8, "hitch:" lines written / rate-limited
};
static_assert(HSC_N == 10 && offsetof(HsTotals, causeMs) == 0x40 && offsetof(HsTotals, lkWaits) == 0x68 &&
              offsetof(HsTotals, hitchLock250) == 0xA4 && sizeof(HsTotals) == 0xB0, "ts3native_hitchsum layout");
extern "C" __declspec(dllexport) HsTotals ts3native_hitchsum = {1, 0, 0, 0, 0, 0, {0, 0, 0, 0, 0, 0, 0, 0, 0, 0}, {0, 0, 0, 0, 0, 0, 0, 0, 0, 0},
                                                                  0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0};

// the last job the route worker ended (written before its +0x918 release, endQ last; read by the waiter: log only)
struct HsJobEndRec { LONGLONG endQ; DWORD handle, seq; LONG us, kind, who; };
static HsJobEndRec g_hsJe;

static HsFrame g_hsF;
static DWORD g_hsSimTid;
static LONGLONG g_hsT0, g_hsPumpQ;  // QPC at the frame's start / at the sim pump's return (0 = none this frame)
static ULONGLONG g_hsCpu0;          // the sim thread's kernel + user time at the frame's start (100 ns)
static bool g_hsCpuOk;
static DWORD g_hsGc0, g_hsTotTick;
static const DWORD* g_hsGcNo;       // GC_gc_no when readable
static LONG g_hsLinesPerMin = 60;

// [RouteAsync] LogFallbackCaller: the compiled method that holds code address a ("Ns.Cls.Meth +0x1A")
static bool FbJitName(DWORD a, char* out, int n) {
    if (!SsJitAt(a)) return false;
    const int nMeth = g_jitMethN;
    for (int i = 0; i < nMeth; i++) {
        const JitMeth& m = g_jitMeth[i];
        const DWORD m0 = (DWORD)(uintptr_t)m.native0;
        if (a < m0 || a - m0 >= (DWORD)m.bytes) continue;
        _snprintf_s(out, n, _TRUNCATE, "%.80s +0x%lX", m.name, a - m0);
        return true;
    }
    return false;
}

static inline bool HsOnSim() { return g_hsOn && __readfsdword(0x24) == g_hsSimTid; }
static inline LONG HsMs(LONG us) { return us < 0 ? -1 : us / 1000; }

static void HsJob(int site, LONG us, DWORD handle) {
    if (!HsOnSim() || us <= 0) return;
    HsFrame& f = g_hsF;
    f.jobN++, f.jobUs += us;
    if (t_syp.open) f.jobInSyncUs += us;
    if (us > f.jobMaxUs) f.jobMaxUs = us, f.jobMaxSite = site, f.jobMaxH = handle;
}
static void HsOtherLock(LONG us) {
    if (!HsOnSim() || us <= 0) return;
    HsFrame& f = g_hsF;
    f.olockN++, f.olockUs += us;
    if (t_syp.open) f.olockInSyncUs += us;
}
static void HsGc(LONG us) {
    if (HsOnSim() && us > 0) g_hsF.gcUs += us;
}
// a closed [RouteJobPhases] SyncPlans session: its self times by phase (RJP_N) and its length
static void HsSync(const LONG* ph, LONG us) {
    if (!HsOnSim()) return;
    HsFrame& f = g_hsF;
    f.syncN++, f.syncUs += us;
    LONG named = 0;
    static const int kMap[HSP_OTHER][3] = {{RJP_WORLD, -1, -1}, {RJP_WATER, -1, -1}, {RJP_POST, -1, -1}, {RJP_INSERT, -1, -1},
                                           {RJP_CELL, -1, -1}, {RJP_LOTLEG, RJP_PORTAL, RJP_LOTGRAPH}, {RJP_LOCK, -1, -1}};
    for (int p = 0; p < HSP_OTHER; p++)
        for (int j = 0; j < 3; j++)
            if (kMap[p][j] >= 0) f.syncPh[p] += ph[kMap[p][j]], named += ph[kMap[p][j]];
    f.syncPh[HSP_OTHER] += us > named ? us - named : 0;
}
// a sync IC plan [RouteAsync] timed, when no SyncPlans session splits it
static void HsSyncMs(LONG ms) {
    if (g_sypOn || !HsOnSim() || ms <= 0) return;
    HsFrame& f = g_hsF;
    f.syncN++, f.syncUs += ms * 1000, f.syncPh[HSP_OTHER] += ms * 1000;
}

// worker: a job ended (before it gives +0x918 back, so the waiter it wakes finds the record)
static void HsJobEnd(DWORD handle, DWORD seq, LONG us, LONG kind, LONG who) {
    HsJobEndRec& r = g_hsJe;
    r.handle = handle, r.seq = seq, r.us = us, r.kind = kind, r.who = who;
    _WriteBarrier();
    r.endQ = HcNow();
}

// "route lock wait: ..." (selftest reads it). ended = a worker job ended inside the wait (je); run = the running job's text
static void HsLockText(char* b, size_t n, bool mainWaiter, LONG us, DWORD key, bool pump, DWORD holder, int role, bool ended,
                       const HsJobEndRec& je, const char* run, DWORD now) {
    static const char* const who[] = {"own", "foreign", "A10 foreign"};
    char job[260];
    if (ended)
        _snprintf_s(job, sizeof(job), _TRUNCATE, "job route %08lX seq %lu %s IC kind %ld ended in the wait after %ld ms (>= 250 ms: %s)", je.handle,
                    je.seq, who[je.who >= 0 && je.who <= 2 ? je.who : 0], je.kind, je.us / 1000, je.us >= 250000 ? "yes" : "no");
    else
        _snprintf_s(job, sizeof(job), _TRUNCATE, "no job ended in the wait, now: %s", run);
    char hold[64];
    if (holder) _snprintf_s(hold, sizeof(hold), _TRUNCATE, "%s %lu", kHoRoleName[role], holder);
    else strcpy_s(hold, "nobody at the start (lent window or a race)");
    _snprintf_s(b, n, _TRUNCATE, "route lock wait: %s waited %ld ms on +0x918 (key %08lX, %s), held by %s; %s, at %lu ms", mainWaiter ? "main" : "sim",
                us / 1000, key, pump ? "job pump" : "plain", hold, job, now);
}

// one finished +0x918 acquire of the sim or main thread that started at QPC t0 and took `us`; holder = the lock's owner
// thread read before it (0 = free then)
static void HsLock(DWORD key, LONG us, LONGLONG t0, DWORD holder, bool pump) {
    if (us < 1000) return;
    HsTotals& z = ts3native_hitchsum;
    const DWORD tid = __readfsdword(0x24);
    const bool mainWaiter = tid == g_mainTid && !RgIsSim(tid);
    const HsJobEndRec je = g_hsJe;
    const bool ended = je.endQ >= t0 && je.endQ != 0;
    const int role = holder ? HoRole(holder) : HO_OTHER;
    const bool worker = role == HO_WORKER || (!holder && (ended || g_async.state == A_RUNNING));
    const bool job250 = worker && ended && je.us >= 250000;
    InterlockedIncrement(&z.lkWaits);
    InterlockedExchangeAdd(&z.lkUs, us);
    Max(&z.lkMaxUs, us);
    if (mainWaiter) InterlockedIncrement(&z.lkMainN), InterlockedExchangeAdd(&z.lkMainUs, us);
    if (worker) {
        InterlockedIncrement(&z.lkWorkerN), InterlockedExchangeAdd(&z.lkWorkerUs, us);
        if (ended) InterlockedIncrement(&z.lkJobEndN), InterlockedExchangeAdd(&z.lkJobEndUs, us);
        if (job250) InterlockedIncrement(&z.lkJob250N), InterlockedExchangeAdd(&z.lkJob250Us, us);
    } else {
        InterlockedIncrement(&z.lkOtherN), InterlockedExchangeAdd(&z.lkOtherUs, us);
    }
    if (HsOnSim()) {
        HsFrame& f = g_hsF;
        f.lockN++, f.lockUs += us;
        if (t_syp.open) f.lockInSyncUs += us;
        if (job250) f.lockJob250Us += us;
        if (us > f.lockMaxUs) f.lockMaxUs = us, f.lockMaxKey = key, f.lockMaxTid = holder, f.lockMaxRole = worker ? HO_WORKER : role;
    }
    if (!g_hsLockLogUs || us < g_hsLockLogUs) return;
    static volatile LONG minute, lines;  // sim and main may both write: a racy count only moves the cap by a line
    const DWORD now = GetTickCount();
    if (now - (DWORD)minute >= 60000) minute = (LONG)now, lines = 0;
    if (InterlockedIncrement(&lines) > 30) { InterlockedIncrement(&z.lkDropped); return; }
    char run[160], b[640];
    RunJobText(run, sizeof(run));
    HsLockText(b, sizeof(b), mainWaiter, us, key, pump, holder, role, ended, je, run, now);
    Log("%s", b);
    InterlockedIncrement(&z.lkLines);
}

// LockAcquireAsync, a +0x918 acquire outside the job pump with HitchMs or RouteLockMs on: the same single P5bLock call, timed
// on the sim and main threads (the worker's and other threads' acquires pass through)
static LONG HsLockTimed(void* lock, void* edx, const DWORD* deadline, DWORD key) {
    const DWORD tid = __readfsdword(0x24);
    if (tid != g_mainTid && !RgIsSim(tid)) return P5bLock(lock, edx, deadline);
    const DWORD holder = LockOwnerTid(lock);
    if (holder == tid) return P5bLock(lock, edx, deadline);  // a recursive acquire never waits
    LARGE_INTEGER t0;
    QueryPerformanceCounter(&t0);
    const LONG r = P5bLock(lock, edx, deadline);
    HsLock(key, UsSince(t0), t0.QuadPart, holder, false);
    return r;
}

// the cause class of a frame; *unknownUs = the frame minus every measured part, *sub = the largest sync phase (HSP_*) for HSC_SYNC
static int HsClassify(const HsView& v, LONG* unknownUs, int* sub) {
    const HsFrame& f = v.f;
    const LONG lim = v.limiterUs > 0 ? v.limiterUs : 0;
    LONG known = f.syncUs + (f.jobUs - f.jobInSyncUs) + (f.lockUs - f.lockInSyncUs) + (f.olockUs - f.olockInSyncUs) + f.gcUs + lim;
    LONG unk = v.frameUs - known;
    if (unk < 0) unk = 0;
    *unknownUs = unk;
    LONG syncWork = f.syncUs - f.syncPh[HSP_LOCK] - f.jobInSyncUs;
    if (syncWork < 0) syncWork = 0;
    *sub = HSP_OTHER;
    for (int p = 0; p < HSP_N; p++)
        if (p != HSP_LOCK && f.syncPh[p] > f.syncPh[*sub]) *sub = p;
    const LONG part[6] = {syncWork, f.jobUs, f.lockUs, f.olockUs, f.gcUs, lim};
    int best = 0;
    for (int i = 1; i < 6; i++) if (part[i] > part[best]) best = i;
    if (part[best] > 0 && (LONGLONG)part[best] * 2 >= v.frameUs) return best;  // HSC_SYNC .. HSC_LIMITER in this order
    if (v.cpuUs < 0) return HSC_UNKNOWN;
    LONG cpuUnk = v.cpuUs - syncWork - f.gcUs;  // CPU time no measured part explains
    if (cpuUnk < 0) cpuUnk = 0;
    if (cpuUnk > unk) cpuUnk = unk;
    if ((LONGLONG)cpuUnk * 2 >= v.frameUs) return HSC_CPU;
    if ((LONGLONG)(unk - cpuUnk) * 2 >= v.frameUs) return HSC_OFFCPU;
    return HSC_MIXED;
}

// "hitch: frame N ms (#n), cause C: ..." (selftest and tools/harness/route-stutter-report.ts read it); returns the cause
static int HsLine(char* d, size_t room, const HsView& v, LONG seq, DWORD now) {
    const HsFrame& f = v.f;
    LONG unk = 0;
    int sub = 0;
    const int cause = HsClassify(v, &unk, &sub);
    char cs[40];
    if (cause == HSC_SYNC) sprintf_s(cs, "%s/%s", kHsCause[cause], kHsPhase[sub]);
    else strcpy_s(cs, kHsCause[cause]);
    int n = _snprintf_s(d, room, _TRUNCATE, "hitch: frame %ld ms (#%ld), cause %s: sync %ld (n=%ld", v.frameUs / 1000, seq, cs, f.syncUs / 1000, f.syncN);
    for (int p = 0; p < HSP_N && n > 0 && n < (int)room - 24; p++) n += sprintf_s(d + n, room - n, " %s=%ld", kHsPhase[p], f.syncPh[p] / 1000);
    if (n > 0 && n < (int)room - 330)
        _snprintf_s(d + n, room - n, _TRUNCATE, "), jobwait %ld (n=%ld max=%ld at %s route %08lX insync=%ld), lock918 %ld (n=%ld max=%ld key %08lX holder %s %lu "
                    "job250=%ld insync=%ld), otherlock %ld (n=%ld insync=%ld), gc %ld (+%ld), limiter %ld, cpu %ld, unknown %ld, at %lu ms",
                    f.jobUs / 1000, f.jobN, f.jobMaxUs / 1000, kHsSite[f.jobMaxSite >= 0 && f.jobMaxSite < SW_N ? f.jobMaxSite : SW_N - 1], f.jobMaxH,
                    f.jobInSyncUs / 1000, f.lockUs / 1000, f.lockN, f.lockMaxUs / 1000, f.lockMaxKey,
                    f.lockN ? kHoRoleName[f.lockMaxRole >= 0 && f.lockMaxRole < HO_ROLES ? f.lockMaxRole : HO_OTHER] : "-", f.lockMaxTid,
                    f.lockJob250Us / 1000, f.lockInSyncUs / 1000, f.olockUs / 1000, f.olockN, f.olockInSyncUs / 1000, f.gcUs / 1000, v.gcN,
                    HsMs(v.limiterUs), HsMs(v.cpuUs), unk / 1000, now);
    return cause;
}

// cumulative totals (selftest reads the text)
static void HsTotalsText(char* b, size_t n, const HsTotals& z, LONG noSim, LONG mainLines, DWORD now) {
    int w = _snprintf_s(b, n, _TRUNCATE, "hitch totals: frames %ld, hitches %ld (%ld ms) by cause", z.frames, z.hitches, z.hitchMs);
    for (int c = 0; c < HSC_N && w > 0 && w < (int)n - 40; c++)
        if (z.causeN[c]) w += sprintf_s(b + w, n - w, " %s=%ld/%ld", kHsCause[c], z.causeN[c], z.causeMs[c]);
    if (w > 0 && w < (int)n - 400)
        _snprintf_s(b + w, n - w, _TRUNCATE, "; +0x918 waits >= 1 ms n=%ld ms=%ld max=%ld (main thread n=%ld ms=%ld): behind the route worker n=%ld ms=%ld, "
                    "its job ended in the wait n=%ld ms=%ld, that job >= 250 ms n=%ld ms=%ld, other holder n=%ld ms=%ld; hitches >= 50%% on +0x918 behind "
                    "a job >= 250 ms: %ld of %ld; stall watch lines without a Simulate frame %ld, main samples %ld, at %lu ms",
                    z.lkWaits, z.lkUs / 1000, z.lkMaxUs / 1000, z.lkMainN, z.lkMainUs / 1000, z.lkWorkerN, z.lkWorkerUs / 1000, z.lkJobEndN,
                    z.lkJobEndUs / 1000, z.lkJob250N, z.lkJob250Us / 1000, z.lkOtherN, z.lkOtherUs / 1000, z.hitchLock250, z.hitches, noSim, mainLines, now);
}

// a frame of frameUs ended: count it and write its line (20-600 a minute, HitchLinesPerMin)
static void HsFrameEnd(const HsView& v, DWORD now) {
    HsTotals& z = ts3native_hitchsum;
    z.frames++;
    if (v.frameUs < z.hitchUs) return;
    static char line[900];
    const int cause = HsLine(line, sizeof(line), v, z.hitches + 1, now);
    z.hitches++, z.hitchMs += v.frameUs / 1000;
    z.causeN[cause]++, z.causeMs[cause] += v.frameUs / 1000;
    if ((LONGLONG)v.f.lockJob250Us * 2 >= v.frameUs) z.hitchLock250++;
    static DWORD minute;
    static LONG lines;
    if (now - minute >= 60000) minute = now, lines = 0;
    if (lines++ >= g_hsLinesPerMin) { z.dropped++; return; }
    Log("%s", line);
    z.lines++;
}

static void __cdecl HsPump() {  // SimPumpAdd: the sim pump returned, the limiter comes next
    g_hsPumpQ = HcNow();
}
static ULONGLONG HsCpuNow(bool* ok) {
    FILETIME c, e, kt, ut;
    *ok = GetThreadTimes(GetCurrentThread(), &c, &e, &kt, &ut) != 0;
    return *ok ? ((ULONGLONG)kt.dwHighDateTime << 32 | kt.dwLowDateTime) + ((ULONGLONG)ut.dwHighDateTime << 32 | ut.dwLowDateTime) : 0;
}
static void __cdecl HsIterate() {  // SimLoopAdd: the end of every Simulate iteration, sim thread
    const LONGLONG q = HcNow();
    const DWORD now = GetTickCount(), gc = g_hsGcNo ? *g_hsGcNo : 0;
    bool cpuOk = false;
    const ULONGLONG cpu = HsCpuNow(&cpuOk);
    if (g_hsT0 && g_hsSimTid == GetCurrentThreadId()) {
        HsView v;
        v.frameUs = HcUs(q - g_hsT0);
        v.cpuUs = cpuOk && g_hsCpuOk ? (LONG)((cpu - g_hsCpu0) / 10) : -1;
        v.limiterUs = g_hsPumpQ >= g_hsT0 ? HcUs(q - g_hsPumpQ) : -1;
        v.gcN = (LONG)(gc - g_hsGc0);
        v.f = g_hsF;
        HsFrameEnd(v, now);
    }
    memset(&g_hsF, 0, sizeof(g_hsF));
    g_hsSimTid = GetCurrentThreadId();
    g_hsT0 = q, g_hsPumpQ = 0, g_hsCpu0 = cpu, g_hsCpuOk = cpuOk, g_hsGc0 = gc;
    if (now - g_hsTotTick >= 60000) {
        const HsTotals& z = ts3native_hitchsum;
        if (g_hsTotTick && (z.hitches || z.lkWaits)) {
            static char b[1100];
            HsTotalsText(b, sizeof(b), z, g_ssNoSimLines, g_ssMainLines, now);
            Log("%s", b);
        }
        g_hsTotTick = now;
    }
}

typedef int(__cdecl* HsStepFn)(void* stop);  // GC_try_to_collect 0xE4A050 (memexp.cpp MxStepFn)
static HsStepFn g_hsStepOrig;
static int __cdecl HsGcStep(void* stop) {
    LARGE_INTEGER t0;
    QueryPerformanceCounter(&t0);
    const int r = g_hsStepOrig(stop);
    HsGc(UsSince(t0));
    return r;
}

// [HitchAttr] HitchMs / HitchLinesPerMin / RouteLockMs / JobWaitLogMs / JobWaitLinesPerMin (all default to today's behaviour)
static bool ApplyHitchSum(const wchar_t* ini) {
    HsTotals& z = ts3native_hitchsum;
    const int jobMs = Clamp(GetPrivateProfileIntW(L"HitchAttr", L"JobWaitLogMs", 300, ini), 1, 60000);
    g_hsJobLogUs = jobMs * 1000;
    g_hsJobLinesPerMin = Clamp(GetPrivateProfileIntW(L"HitchAttr", L"JobWaitLinesPerMin", 20, ini), 1, 1000);
    if (jobMs != 300) Log("OK   HitchAttr JobWaitLogMs=%d: \"route jobs: sim waited\" lines from %d ms, %ld a minute", jobMs, jobMs, g_hsJobLinesPerMin);
    const int lockMs = Clamp(GetPrivateProfileIntW(L"HitchAttr", L"RouteLockMs", 0, ini), 0, 60000);
    const int hitchMs = Clamp(GetPrivateProfileIntW(L"HitchAttr", L"HitchMs", 0, ini), 0, 60000);
    g_hsLinesPerMin = Clamp(GetPrivateProfileIntW(L"HitchAttr", L"HitchLinesPerMin", 60, ini), 1, 600);
    z.lockLogUs = lockMs * 1000;
    if (lockMs) {
        InterlockedExchange(&g_hsLockLogUs, lockMs * 1000);
        Log("OK   HitchAttr RouteLockMs=%d: \"route lock wait:\" lines for sim / main waits on +0x918 with the holder and its job (needs the "
            "Lock_Acquire hook: [RouteAsync] or [RouteGate])", lockMs);
    }
    if (!hitchMs) return lockMs != 0 || jobMs != 300;
    if (!SimLoopAdd(HsIterate, "hitch summary: frame end")) { Log("SKIP HitchAttr HitchMs: no Simulate iteration hook"); return lockMs != 0; }
    const bool pump = SimPumpAdd(HsPump, "hitch summary: limiter start");
    g_hsGcNo = Readable(g_mx.gcNo, 4) ? g_mx.gcNo : NULL;
    bool gcStep = g_mxStepOrig != NULL;  // memexp.cpp times the step already and feeds HsGc
    if (!gcStep) {
        const CallSite cs = {TS3A(0x00D819AA), TS3A(0x00E4A050), (void*)HsGcStep};
        g_hsStepOrig = (HsStepFn)(uintptr_t)TS3A(0x00E4A050);
        gcStep = PatchCalls("per-frame GC_try_to_collect timing (hitch summary)", &cs, 1);
        if (!gcStep) g_hsStepOrig = NULL;
    }
    z.hitchUs = hitchMs * 1000;
    InterlockedExchange(&g_hsOn, 1);
    Log("OK   HitchAttr HitchMs=%d: one \"hitch:\" line per sim frame >= %d ms (sync plan phases%s, job waits, +0x918 waits, other locks%s, gc step%s, "
        "limiter%s, sim CPU time), %ld a minute; totals every 60 s",
        hitchMs, hitchMs, g_sypOn ? "" : " need [RouteJobPhases] SyncPlans=1", g_hoOn ? "" : " only during route jobs without [HitchAttr] Holders=1",
        gcStep ? "" : " not timed", pump ? "" : " not timed", g_hsLinesPerMin);
    return true;
}
