// [HitchAttr] (0.34.10, docs/96 §9, counters v66): how much of each sim frame the sim thread spent waiting for the route job.
// Observe only, no hook: the four places where the job owner (the sim thread) already times a wait for the job call HaNote
// after the wait. Classes (HA_*): 0 the routing lock world+0x918 (LockAcquireAsync pump; gate waits from RgEnter land here
// keyed by the leaf's caller, docs/84 keys), 1 another game lock taken while a job runs or is queued (same pump), 2 a wait
// for the running job itself (RtWaitJob: destroy / drain keys 0x02xxxxxx; the discourage drain 0x06000000), 3 a plan that
// waited for the job first (RunPlan, key 0x05000000).
// Frames: tools/frida/stutter-profile.js reads `ts3native_hitchattr` at each iteration boundary (GC_try_to_collect entry, on
// the sim thread) and zeroes it, so the frame split needs no clock fit: hitches.jsonl gets rtLock/rtOtherLock/rtJob/rtPlanWait
// ms, rtWaits, rtMaxMs, rtMaxKey; seconds.csv the same sums per second. tools/harness/hitch-attr-report.ts joins them.
// shortcut: a wait nested inside another wait's pump (a managed callback for the worker that waits again) is counted twice;
// H6 sends plans made in such callbacks to the worker and borrows the lock, so it is rare; split it if rtWaits ever shows it.
// Included by ts3native.cpp.
// HA_* classes: enum next to WaitNote in ts3native.cpp (the hook lines come before this file)
struct HaFrame {
    LONG version;      // +0x00, 1
    LONG enabled;      // +0x04, [HitchAttr] Enabled
    LONG us[HA_N];     // +0x08, wait us by class since the reader last zeroed this frame block
    LONG n;            // +0x18, waits
    LONG maxUs;        // +0x1C, the longest of them
    LONG maxKey;       // +0x20, its key
    // v2 (0.36.49, docs/178 §9, movedwait.cpp, [MovedWaits] on): waits a read-only skip moved (same job, a later wait)
    LONG mvUs;         // +0x24, their wait us
    LONG mvN;          // +0x28, how many
    LONG mvSw;         // +0x2C, MW_* bit mask of the switches whose skip moved them
    LONG mvMaxUs;      // +0x30, the longest
    LONG mvKey;        // +0x34, its key
};
extern "C" __declspec(dllexport) HaFrame ts3native_hitchattr = {2, 0, {0, 0, 0, 0}, 0, 0, 0, 0, 0, 0, 0, 0};

static void HaNote(DWORD key, LONG us, int cls) {
    if (!ts3native_hitchattr.enabled || us <= 0 || GetCurrentThreadId() != g_async.owner) return;
    HaFrame& f = ts3native_hitchattr;  // the owner thread only (the reader zeroes it on the same thread)
    f.us[cls] += us;
    f.n++;
    if (us > f.maxUs) f.maxUs = us, f.maxKey = (LONG)key;
    Ts3nCounters& k = ts3native_counters;
    InterlockedIncrement(&k.haWaits[cls]);
    InterlockedExchangeAdd(&k.haUs[cls], us);
    Max(&k.haMaxUs, us);
}

// --- [HitchAttr] Holders=1 (0.36.25, docs/165 §8 gap #2, counters v98): who holds the lock a long sim/main wait is on ---
// Every thread's outermost acquire through LockAcquireHook stores (lock, thread, return address) in a 4-way set-associative
// side table right after the acquire returns, before anything else. Entries are never cleared: a release leaves the last
// holder's entry, and the next outermost acquire of that lock overwrites it (0.36.26, docs/165 §8: the old clear-on-release
// and the write after HoNote left the holder's site 0 across lock hand-offs). A sim or main thread acquire of a lock another
// thread holds (the EA lock's CRITICAL_SECTION OwningThread, plain read: the lock is live, we are about to take it) reads
// the holder's entry at wait start and again when its own acquire returns (before writing its own; the holder has released
// by then, so its entry is complete) and prefers the second, times the acquire, and at >= MinMs writes a record to
// `ts3native_lockholders` (ring, read by stutter-profile.js into hitches.jsonl `holders`) and a ts3native.log line. +0x918
// waits stay [HitchAttr]'s (keys); the acquire itself goes through LockAcquirePlain unchanged, so with Holders=0 (default)
// the only cost is one global test in the acquire hook (the release hook has none).
// shortcut: the holder tid is read once at wait start; a lock handed through several holders during one wait names the
// first, with its site only while its entry is still in the set.
// EA mutex-type locks (byte +0x24 == 0, a mutex handle at +0, Lock_Acquire 0x4E175F) have no OwningThread: never recorded.
struct HoEntry { void* volatile lock; volatile DWORD tid, site, pad; };
static __declspec(align(64)) HoEntry g_hoTab[256][4];  // one 64-byte line per set
static u8 g_hoNext[256];                               // per-set FIFO victim; a racy increment only picks another way
static inline int HoSetIx(void* lock) { return ((uintptr_t)lock >> 4 ^ (uintptr_t)lock >> 12) & 255; }
// the site recorded for (lock, tid); 0 when no way holds a complete entry for both
static DWORD HoFind(void* lock, DWORD tid) {
    HoEntry* const set = g_hoTab[HoSetIx(lock)];
    for (int w = 0; w < 4; w++) {
        HoEntry& e = set[w];
        if (e.lock != lock) continue;
        const DWORD etid = e.tid, esite = e.site;
        _ReadBarrier();
        if (e.lock == lock && etid == tid) return esite;
    }
    return 0;
}
static void HoPut(void* lock, DWORD tid, DWORD site) {
    const int ix = HoSetIx(lock);
    HoEntry* const set = g_hoTab[ix];
    int w = 0;
    while (w < 4 && set[w].lock != lock) w++;
    if (w == 4) w = g_hoNext[ix]++ & 3;
    HoEntry& e = set[w];
    e.lock = NULL;
    _WriteBarrier();
    e.tid = tid, e.site = site;
    _WriteBarrier();
    e.lock = lock;
}
enum { HO_SIM, HO_MAIN, HO_WORKER, HO_REPLAY, HO_OTHER, HO_ROLES };
struct HoRec {
    volatile LONG seq;  // +0x00, ring index + 1, written last (0 = never written)
    LONG waiter;        // +0x04, 0 sim thread, 1 main thread
    DWORD lock;         // +0x08
    LONG us;            // +0x0C, wait time
    DWORD holderTid;    // +0x10
    LONG holderRole;    // +0x14, HO_* (4 = other: EA job threads, loaders, audio)
    DWORD holderSite;   // +0x18, return address of the holder's acquire (0 = not in the side table)
    DWORD waiterSite;   // +0x1C, return address of the waiting acquire
};
struct HoRing {
    LONG version;         // +0x00, 1
    LONG enabled;         // +0x04
    LONG minUs;           // +0x08
    volatile LONG head;   // +0x0C, records written so far; record i (1-based) sits at r[(i - 1) & 63]
    HoRec r[64];          // +0x10
};
static_assert(sizeof(HoRec) == 32 && offsetof(HoRing, r) == 0x10, "ts3native_lockholders layout (stutter-profile.js)");
extern "C" __declspec(dllexport) HoRing ts3native_lockholders = {1, 0, 20000, 0, {}};

static int HoRole(DWORD tid) {
    return tid == g_mainTid ? HO_MAIN : RgIsSim(tid) ? HO_SIM : RgIsWorker(tid) ? HO_WORKER : g_rpTid && tid == g_rpTid ? HO_REPLAY : HO_OTHER;
}
static const char* const kHoRoleName[HO_ROLES] = {"sim", "main", "route worker", "replay consumer", "thread"};

static void HoNote(int waiter, void* lock, LONG us, DWORD htid, DWORD hsite, DWORD wsite) {
    Ts3nCounters& k = ts3native_counters;
    const int role = htid ? HoRole(htid) : HO_OTHER;
    InterlockedIncrement(&k.hoWaits[waiter]);
    InterlockedExchangeAdd(&k.hoUs[waiter], us);
    Max(&k.hoMaxUs, us);
    InterlockedIncrement(&k.hoByRole[role]);
    if (!hsite) InterlockedIncrement(&k.hoNoSite);
    HoRing& g = ts3native_lockholders;
    const LONG i = InterlockedIncrement(&g.head);
    HoRec& r = g.r[(i - 1) & 63];
    r.seq = 0;
    r.waiter = waiter, r.lock = (DWORD)(uintptr_t)lock, r.us = us, r.holderTid = htid, r.holderRole = role;
    r.holderSite = hsite, r.waiterSite = wsite;
    _WriteBarrier();
    r.seq = i;
    if (i <= 200 || i % 100 == 0)  // the first 200, then every 100th
        Log("lock holders: %s waited %ld ms on lock %p (at 0x%08lX), held by %s %lu from 0x%08lX (wait #%ld)", waiter ? "main" : "sim",
            us / 1000, lock, wsite, kHoRoleName[role], htid, hsite, i);
}

static __declspec(noinline) LONG HoAcquire(void* lock, void* edx, const DWORD* deadline, DWORD ret) {
    const DWORD tid = __readfsdword(0x24);
    const int waiter = tid == g_mainTid ? 1 : RgIsSim(tid) ? 0 : -1;
    const DWORD htid = (DWORD)(uintptr_t)((const CRITICAL_SECTION*)lock)->OwningThread;
    if (waiter < 0 || lock == g_rgLock || *deadline == 0 || !htid || htid == tid) {
        const LONG r = LockAcquirePlain(lock, edx, deadline, ret);
        if (r == 1) HoPut(lock, tid, ret);  // outermost acquire (EA Lock_Acquire returns the new count): this thread holds it
        return r;
    }
    const DWORD site0 = HoFind(lock, htid);  // 0 when the holder took it an instant ago and has not written yet
    LARGE_INTEGER t0;
    QueryPerformanceCounter(&t0);
    const LONG r = LockAcquirePlain(lock, edx, deadline, ret);
    const LONG us = UsSince(t0);
    const DWORD site1 = HoFind(lock, htid);  // the holder's entry, complete now; before ours replaces it
    // ours before the record: a waiter on this thread during HoNote's log write finds our site (docs/165 §8 cause A)
    if (r == 1) HoPut(lock, tid, ret);
    if (us >= ts3native_lockholders.minUs) HoNote(waiter, lock, us, htid, site1 ? site1 : site0, ret);
    return r;
}

// --- [HitchAttr] JobNameMs (0.36.191, docs/247 §12.4, counters v173 rjn*): names the route job behind a long rtJob wait ---
// Log only, changes nothing. A sim-thread wait for the running job (RtWaitJob, the discourage drain) of >= rjnMinUs writes
// "rtjob wait:" with the wait key and the job the worker just ended (g_rjnLast): route handle, own / foreign, IC kind, the Sim
// scope at START (ts3fixes UfWhy; household-or-seen and unknown are household, never cut), job ms split into plan / host / other,
// plans and world A* nodes (0 nodes when the job had no budget), its deadline, arm and wall stamp. 20 lines a minute, rest counted.
// shortcut: the record is the last job the worker ended, not looked up by handle; "last job" in the line says when they differ.
static void RjnText(char* b, size_t n, DWORD key, LONG us, DWORD handle, const RjnSum& s, const char* arm, DWORD now) {
    static const char* const who[] = {"own", "foreign", "A10 foreign"};
    char hd[40] = "";
    if (s.handle != handle) _snprintf_s(hd, sizeof(hd), _TRUNCATE, " (waited for %08lX)", handle);
    _snprintf_s(b, n, _TRUNCATE, "rtjob wait: %ld ms key %08lX, %sjob route %08lX%s seq %lu %s, IC kind %ld, scope %s, job %ld ms plan %ld host %ld "
        "other %ld, plans %ld nodes %ld, deadline %ld ms%s, arm %s, at %lu ms", us / 1000, key, s.handle == handle ? "" : "last ", s.handle, hd,
        s.seq, who[s.who >= 0 && s.who <= 2 ? s.who : 0], s.kind, KdScopeTag(s.scope), s.us / 1000, s.planUs / 1000, s.hostUs / 1000,
        (s.us - s.planUs - s.hostUs) / 1000, s.plans, s.nodes, s.dlUs / 1000, s.capped ? ", capped" : "", arm, now);
}
static void RjnWait(DWORD key, LONG us, DWORD handle) {
    Ts3nCounters& k = ts3native_counters;
    const LONG min = k.rjnMinUs;
    if (min <= 0 || us < min) return;
    static DWORD minute, lines;  // the sim thread only (both callers check the job owner)
    const DWORD now = GetTickCount();
    if (now - minute >= 60000) minute = now, lines = 0;
    if (lines >= 20) { InterlockedIncrement(&k.rjnDropped); return; }
    lines++;
    const RjnSum s = g_rjnLast;
    char b[512];
    RjnText(b, sizeof(b), key, us, handle, s, AbArmTag(), now);
    Log("%s", b);
    InterlockedIncrement(&k.rjnLines);
}

static bool ApplyHitchAttr(const wchar_t* ini) {
    const int rjnMs = Clamp(GetPrivateProfileIntW(L"HitchAttr", L"JobNameMs", 0, ini), 0, 60000);
    ts3native_counters.rjnMinUs = rjnMs * 1000;
    if (rjnMs) Log("OK   HitchAttr JobNameMs=%d: \"rtjob wait:\" lines for sim waits on the running route job", rjnMs);
    if (GetPrivateProfileIntW(L"HitchAttr", L"Holders", 0, ini)) {
        ts3native_lockholders.minUs = Clamp(GetPrivateProfileIntW(L"HitchAttr", L"HoldersMinMs", 20, ini), 1, 10000) * 1000;
        ts3native_lockholders.enabled = 1;
        ts3native_counters.hoReady = 1;
        g_hoOn = 1;
        Log("OK   HitchAttr Holders: sim/main waits >= %ld ms on any game lock with the holder's thread and acquire site "
            "(ts3native_lockholders, needs the Lock_Acquire hook: [RouteAsync] or [RouteGate])", ts3native_lockholders.minUs / 1000);
    }
    if (!GetPrivateProfileIntW(L"HitchAttr", L"Enabled", 0, ini)) return g_hoOn != 0;
    ts3native_hitchattr.enabled = 1;
    ts3native_counters.haReady = 1;
    Log("OK   HitchAttr: sim waits for the route job per frame (ts3native_hitchattr, read by stutter-profile.js)");
    return true;
}
