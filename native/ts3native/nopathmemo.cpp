// [RouteAsync] NoPathMemo (docs/643, default 0 = off): an NPC's water / boat plan that failed with no path is answered from
// a memo while nothing it depends on changed, instead of searching again. Included by ts3native.cpp after plandeadline.cpp
// (g_kdScope*) and earlyout.cpp (CallPlan calls NpmPlan, CallPlanEo is the plan with the early-out request scope).
//
// Why: Isla Paradiso, NPC water / boat route plans fail with FailedBecauseNoPathToDestination after ~300-650 ms. The time is
// inside PathPlanner_PlanHierarchical 0x65E0D0 before any world A*, cell A* or water entry exit, so PlanDeadlineMs' 300 ms
// session never cuts them ("world A* capped 0, cell skipped 0, water skipped 0") [verified, log]. EA then plans the identical
// route again every ~10 ticks: the houseboat leg (own sync kind 2, ArWdPass -> game IC -> Route_PlanLocked -> CallPlan ->
// CallPlanBudget -> PdPlanBegin), boat-rider jobs (worker, kinds 0 / 3) and watchdog plans (docs/642 §1, §4, §6).
//
// Site: CallPlan (earlyout.cpp), the one Route_PlanLocked path of both the sim thread (RunPlan) and the route worker (sync
// PlanJob and async jobs), so every caller above is covered once.
// Who: the follower is an NPC of known scope only: 2 unseen or 3 seen NPC. Sim thread: the UfWhy answer ts3fixes sent for the
// route it is planning (g_kdScopeH / g_kdScope, as KdSyncCeilUs(0)); worker in a job: that job's scope (uf / kdScope). The
// route the scope belongs to must have this plan's follower (route +0x40/+0x44 == guid args), else the scope is someone
// else's: unknown. Household, selected and unknown scope never record and are never answered (HhExempt; [Experimental]
// HouseholdToo=1 lifts it, the answer being a cached real failure).
// What: Mode 1 = requests with water or boat planning (RouteOption hi 0x10000000 EnableWaterPlanning / 0x20000000
// EnablePondPlanning, RouteOption2 lo 0x2 EnablePlanningAsBoat / 0x8 BeginAsBoat [verified docs/11 Q2, docs/99 §11.1]);
// Mode 2 = any request of such a router (also the houseboat gangway leg, which has water and boat planning off, docs/642 §6).
// Record: Route_PlanLocked returned 0 and the first path's status is 7/8 (FailedBecauseNoPathToDestination, docs/93 §2.1),
// no path in the list names a Sim blocker (record kind 0x14 with an id other than the follower; RouteBlockerType Vehicle is
// never produced natively [verified 0x752620, docs/93 §2.2]), no exit cut the plan (PlanDeadlineMs / UnseenFallback session
// skips and caps, SyncCapMs, the worker's job deadlines, a budget cap), the world was quiet (no pending lot updates) and the
// static world version did not move during the plan. Key: routing world, agent class, the four RouteOption words, follower
// ObjectGuid, start and goal points in 0.5 m cells (RouteKey, the phase-2 cache's key).
// Answer: an identical request within TTL sim-minutes (NoPathMemoMinutes, default 120; SimClock ticks, 37.5 per sim-minute)
// returns 0 before the lock without searching, with the result type the recorded plan had: FailedBecauseNoPathToDestination
// (16), no blocker. Route_PlanCommon maps a 0 with an empty out list to Failed (2) (its local result's default); the one je
// that takes that branch (0x760F2F) is retargeted to NpmPcStub, which writes 16 into that local result when this thread's
// plan was answered here, then goes on to the copy-out at 0x76156D (NpmPcInstall). So only plans from Route_PlanCommon's
// call site (return 0x760DE6: Plan, Replan, PlanFromPoint, ReplanFromPosition) are answered, and only with the stub in.
// No blocker: GetRouteFailPath plans nothing (docs/11 Q5). Only when the out list is empty, the world is quiet and [RouteSnap]'s static version
// (lot routing / quad tree / LotGraph builds, water grid builds and marks, lot updates, IC epochs: PrVersions) equals the
// recorded one; a request that reads dynamic footprints (BlockedByPeople / BlockedByDynamicFootprints) also needs the
// footprint version unchanged (FailMemo's rule, replandedupe.cpp). An answer never extends its entry; a success of the same
// request drops it; another routing world clears the table.
// Counters: the ts3native_nopathmemo export (docs/registry.md, slots 4000-4003 = recorded, served, ms stood for, invalidated).
#include "nopathmemo-core.h"

struct NoPathMemoCounters {
    volatile LONG mode;   // +0x00 runtime switch: 0 off, 1 water / boat requests, 2 every request of an NPC router
    LONG ready;           // +0x04 1 = installed; bit 1 + bit 2 = world version and sim clock seen; bit 3 = result stub in (answers on)
    LONG recorded;        // +0x08 slot 4000: failed no-path plans recorded
    LONG served;          // +0x0C slot 4001: identical plans answered from the memo
    LONG servedMs;        // +0x10 slot 4002: ms of the recorded plans those answers stand for
    LONG invalidated;     // +0x14 slot 4003: entries dropped because a world version moved (or another world)
    LONG expired;         // +0x18 entries past the TTL (or the sim clock went back)
    LONG evicted;         // +0x1C live entries evicted by a full set (LRU)
    LONG dropped;         // +0x20 entries dropped by a plan of the same request that did not fail with no path
    LONG exempt;          // +0x24 eligible requests of household, selected or unknown scope (run as vanilla)
    LONG notRecorded[4];  // +0x28 failures not recorded: 0 cut, 1 Sim blocker, 2 world not quiet / version moved, 3 other status
    LONG refused;         // +0x38 eligible requests not looked up: no world version, no sim clock, not the live world, key read fault
    LONG ttlMin;          // +0x3C NoPathMemoMinutes
};
extern "C" __declspec(dllexport) NoPathMemoCounters ts3native_nopathmemo = {};

static NpmTable g_npmTab;
static SRWLOCK g_npmLock = SRWLOCK_INIT;
static const void* g_npmWorld;  // the routing world the table belongs to (g_npmLock)
static uint64_t g_npmTtl;
static volatile LONG g_npmMinute, g_npmLines[2];

// SimClock.sSimTickCnt: MonoScriptHost_Simulate stores it through [this + 0xC24] (0xD81D43), this = the script host interface
// [0x11D4BBC] (host + 8, vtable 0x108B690 whose slot 8 is Simulate 0xD81840) [verified disassembly, Steam 1.67]. Two dword
// stores: a read on the worker retries until two reads agree.
static const DWORD kNpmHostIface = TS3A(0x011D4BBC);
static bool NpmSimTick(uint64_t* out) {
    __try {
        const DWORD host = *(const DWORD*)(uintptr_t)kNpmHostIface;
        if (host < 0x10000) return false;
        const DWORD p = *(const DWORD*)(uintptr_t)(host + 0xC24);
        if (p < 0x10000) return false;
        for (int i = 0; i < 4; i++) {
            const uint64_t a = *(const volatile uint64_t*)(uintptr_t)p, b = *(const volatile uint64_t*)(uintptr_t)p;
            if (a == b) { *out = a; return true; }
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {}
    return false;
}

// this plan's router scope (0 unknown, 1 seen household side, 2 unseen, 3 seen NPC) and route handle, see the header
static LONG NpmScope(DWORD guidLo, DWORD guidHi, DWORD* handle, int* kind) {
    LONG scope = 0;
    DWORD h = 0;
    *kind = -1;
    const AsyncJob* j = g_runJob;
    if (GetCurrentThreadId() == g_workerTid && g_async.state == A_RUNNING && j && j->handle == g_async.handle) {
        scope = j->uf ? 2 : j->kdScope, h = j->handle, *kind = j->kind;
    } else {  // the sim thread, or the worker serving the sim's sync plan while the sim waits for it
        h = g_kdScopeH, scope = h ? g_kdScope : 0;
        if (t_p10) *kind = (int)(t_p10 & 0xFF) - 1;  // [ProbeP10] on: the route IC this plan runs in
    }
    *handle = h;
    if (!h || !scope) return 0;
    __try {
        const u8* rm = *g_orRmSlot;
        const u8* route = rm ? g_orRoute(rm, NULL, h) : NULL;
        if (!route || *(const DWORD*)(route + 0x40) != guidLo || *(const DWORD*)(route + 0x44) != guidHi) return 0;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return 0;
    }
    return scope;
}

// thread-local evidence that an exit cut a plan; compared before / after it (NpmCutAfter)
struct NpmCutSnap { LONG sess, skips, budget, fbjd; bool open; };
static void NpmCutBefore(NpmCutSnap& s) {
    s.open = t_pd.open;
    s.sess = ts3native_counters.pdSessions[1];
    s.skips = t_pd.open ? t_pd.skip[0] + t_pd.skip[1] + t_pd.capped : 0;
    s.budget = ts3native_counters.budgetCapped;  // global: another thread's cap only costs a recording
    s.fbjd = t_fbjdCut[0] + t_fbjdCut[1] + t_fbjdCut[2];
}
static bool NpmCutAfter(const NpmCutSnap& s) {
    if (ts3native_counters.budgetCapped != s.budget || t_fbCapHit) return true;
    if (t_fbjdCut[0] + t_fbjdCut[1] + t_fbjdCut[2] != s.fbjd || t_jobCapAt) return true;  // worker: this job was cut
    if (s.open) return !t_pd.open || t_pd.skip[0] + t_pd.skip[1] + t_pd.capped != s.skips;
    // a PlanDeadlineMs session this plan opened and closed (PdPlanBegin / PdClose): past its deadline with an exit taken.
    // Past the deadline with no exit taken is the game's own answer (the measured case): recorded.
    return ts3native_counters.pdSessions[1] != s.sess && t_pdHitLast && t_pdLastMask != 0;
}

// the failed plan's paths: 0 = no path to destination without a Sim blocker, 1 = a Sim blocker, 2 = another status
static int NpmFailKind(const int* outList, DWORD guidLo, DWORD guidHi) {
    __try {
        const int* node = (const int*)(uintptr_t)outList[0];
        if (node == outList) return 2;  // empty: a pre-lock failure (Failed, no blocker), nothing to memo
        for (int i = 0; node != outList && i < 8; i++, node = (const int*)(uintptr_t)node[0]) {
            void* path = (void*)(uintptr_t)node[2];
            if (!path) continue;
            if (i == 0) {
                const int st = ((PathStatusFn)(*(void***)path)[0x20 / 4])(path, NULL);
                if (st != 7 && st != 8) return 2;
            }
            // blocker records RoutingPathImp +0xD0..+0xD4, 40 bytes: +0x00 kind, +0x08/+0x0C object id (path slots +0x160 /
            // +0x164 = 0x81FCE0 / 0x81D990; 0x752620 maps kind 0x14 to RouteBlockerType Sim and skips the follower's own id)
            // [verified disassembly, Steam 1.67]
            const u8* b = *(const u8* const*)((const u8*)path + 0xD0);
            const u8* e = *(const u8* const*)((const u8*)path + 0xD4);
            for (int r = 0; b && e > b && b + 40 <= e && r < 64; b += 40, r++)
                if (*(const DWORD*)b == 0x14 && (*(const DWORD*)(b + 8) != guidLo || *(const DWORD*)(b + 0xC) != guidHi)) return 1;
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return 2;
    }
    return 0;
}

// Route_PlanCommon 0x7606E0 after its Route_PlanLocked call (0x760DE1, returns to 0x760DE6) [verified disassembly, Steam 1.67]:
// 0x760F2D `cmp [edi],edi` (out list empty?); 0x760F2F `je 0x76156D` = the empty-list branch, which copies the local result
// [esp+0x78] (0x48 bytes, rep movs at 0x761581) to the caller's out. The non-empty branch instead calls 0x752620 with that same
// [esp+0x78], which writes type 16 for path status 7/8 (docs/93 §2.1). esp is the same at 0x760F2F and 0x761581.
static const DWORD kNpmPcSite = TS3A(0x00760F2D), kNpmPcBack = TS3A(0x0076156D), kNpmPlanRet = TS3A(0x00760DE6);
static const u8 kNpmPcBytes[4] = {0x39, 0x3F, 0x0F, 0x84};  // cmp [edi],edi; je rel32
static DWORD g_npmPcBack;
static void __cdecl NpmPcFix(DWORD* res) {
    if (!t_npmServed) return;
    t_npmServed = false;
    res[0] = 16;  // FailedBecauseNoPathToDestination; blocker id / type / location keep the default's zeros
}
static __declspec(naked) void NpmPcStub() {
    __asm {
        pushad
        pushfd
        lea eax, [esp + 0x24 + 0x78]  // the site's esp + 0x78 (pushad 0x20 + pushfd 4)
        push eax
        call NpmPcFix
        add esp, 4
        popfd
        popad
        jmp dword ptr [g_npmPcBack]
    }
}
static bool NpmPcInstall() {
    u8* site = (u8*)(uintptr_t)kNpmPcSite;
    if (!Matches(site, kNpmPcBytes, sizeof(kNpmPcBytes))) return false;
    u8* rel = site + sizeof(kNpmPcBytes);
    if ((DWORD)(uintptr_t)(rel + 4) + (DWORD)*(const LONG*)rel != kNpmPcBack) return false;
    g_npmPcBack = kNpmPcBack;
    const LONG nrel = (LONG)((DWORD)(uintptr_t)NpmPcStub - (DWORD)(uintptr_t)(rel + 4));
    return Write(rel, &nrel, 4);
}

static bool NpmLineOk(int which) {
    const LONG minute = (LONG)(GetTickCount() / 60000);
    if (InterlockedExchange(&g_npmMinute, minute) != minute) g_npmLines[0] = g_npmLines[1] = 0;
    return InterlockedIncrement(&g_npmLines[which]) <= 10;  // 10 lines a minute each, like boattaximemo / houseboatlegmemo
}

static u8 NpmPlan(void* world, void* edx, int* outList, int* starts, int* goals, DWORD agent, DWORD doPlan, u8* opts,
                  int* out1, int* out2, DWORD guidLo, DWORD guidHi) {
    NoPathMemoCounters& c = ts3native_nopathmemo;
    const LONG mode = c.mode;
    bool water = false, dyn = false;
    if (mode && outList && starts && goals && opts && doPlan && (guidLo | guidHi)) {
        __try {
            const DWORD* o = (const DWORD*)(opts + 0x28);  // RouteOption lo/hi, RouteOption2 lo/hi
            water = (o[1] & 0x30000000) || (o[2] & 0xA);
            dyn = (o[0] & (4 | 0x1000)) != 0;
        } __except (EXCEPTION_EXECUTE_HANDLER) {
            return CallPlanEo(world, edx, outList, starts, goals, agent, doPlan, opts, out1, out2, guidLo, guidHi);
        }
    }
    if (!mode || !(water || mode == 2) || !outList || !starts || !goals || !doPlan || !(guidLo | guidHi))
        return CallPlanEo(world, edx, outList, starts, goals, agent, doPlan, opts, out1, out2, guidLo, guidHi);
    DWORD handle;
    int kind;
    const LONG scope = NpmScope(guidLo, guidHi, &handle, &kind);
    if (HhExempt(scope != 2 && scope != 3)) {
        InterlockedIncrement(&c.exempt);
        return CallPlanEo(world, edx, outList, starts, goals, agent, doPlan, opts, out1, out2, guidLo, guidHi);
    }
    uint64_t now = 0, key = 0;
    const bool live = g_rgLock && g_rgLock == (u8*)world + 0x918;  // the game's routing world, not a P5 clone
    const bool verOk = (ts3native_counters.snReady & 5) == 5, tickOk = NpmSimTick(&now);
    if (verOk && tickOk && !(c.ready & 6)) InterlockedOr(&c.ready, 6);
    bool keyed = live && verOk && tickOk;
    if (keyed) {
        __try { key = Fnv(RouteKey(starts, goals, agent, opts, guidLo, guidHi), (DWORD)(uintptr_t)world); }
        __except (EXCEPTION_EXECUTE_HANDLER) { keyed = false; }
    }
    if (!keyed) {
        InterlockedIncrement(&c.refused);
        return CallPlanEo(world, edx, outList, starts, goals, agent, doPlan, opts, out1, out2, guidLo, guidHi);
    }
    DWORD ver0, sver0;
    PrVersions(&ver0, &sver0);
    const bool quiet = RtQuietWorld(g_rgLock);
    int look = NPM_MISS;
    int32_t ms = 0;
    AcquireSRWLockExclusive(&g_npmLock);
    if (g_npmWorld != world) {
        const int n = NpmTabClear(&g_npmTab);
        if (n) InterlockedExchangeAdd(&c.invalidated, n);
        g_npmWorld = world;
    }
    look = NpmTabLook(&g_npmTab, key, now, g_npmTtl, sver0, ver0, &ms);
    // the plan runs and the entry stays: world not quiet, out list not empty, or a caller whose answer would not read 16
    if (look == NPM_HIT && (!quiet || (const int*)(uintptr_t)outList[0] != outList || !(c.ready & 8) || t_npmRet != kNpmPlanRet))
        look = -1;
    ReleaseSRWLockExclusive(&g_npmLock);
    if (look == NPM_INVALID) InterlockedIncrement(&c.invalidated);
    else if (look == NPM_EXPIRED) InterlockedIncrement(&c.expired);
    if (look == NPM_HIT) {
        InterlockedIncrement(&c.served);
        InterlockedExchangeAdd(&c.servedMs, ms);
        t_npmServed = true;  // NpmPcStub: Route_PlanCommon's empty-list branch reports 16, not 2
        if (NpmLineOk(1)) Log("route nopath memo: served route %08lX kind %d scope %ld, stands for %ld ms", handle, kind, scope, (LONG)ms);
        return 0;  // out list untouched; Route_PlanCommon reports FailedBecauseNoPathToDestination with no blocker
    }
    NpmCutSnap cut;
    NpmCutBefore(cut);
    LARGE_INTEGER t0;
    QueryPerformanceCounter(&t0);
    const u8 r = CallPlanEo(world, edx, outList, starts, goals, agent, doPlan, opts, out1, out2, guidLo, guidHi);
    const LONG planMs = UsSince(t0) / 1000;
    const bool wasCut = NpmCutAfter(cut);
    const int fk = r ? 2 : NpmFailKind(outList, guidLo, guidHi);
    DWORD ver1, sver1;
    PrVersions(&ver1, &sver1);
    const int why = wasCut ? 0 : fk == 1 ? 1 : fk == 2 ? 3 : !quiet || sver1 != sver0 ? 2 : -1;
    if (why >= 0) {
        if (!r) InterlockedIncrement(&c.notRecorded[why]);
        if (look == NPM_MISS || look == -1) {  // a plan of a memoed request that did not fail the memoed way: the memo is wrong now
            AcquireSRWLockExclusive(&g_npmLock);
            const int d = g_npmWorld == world && why != 0 ? NpmTabDrop(&g_npmTab, key) : 0;
            ReleaseSRWLockExclusive(&g_npmLock);
            if (d) InterlockedIncrement(&c.dropped);
        }
        return r;
    }
    AcquireSRWLockExclusive(&g_npmLock);
    const int ev = g_npmWorld == world ? NpmTabPut(&g_npmTab, key, now, sver1, ver1, dyn, planMs) : 0;
    ReleaseSRWLockExclusive(&g_npmLock);
    if (ev) InterlockedIncrement(&c.evicted);
    InterlockedIncrement(&c.recorded);
    if (NpmLineOk(0)) Log("route nopath memo: recorded route %08lX kind %d scope %ld, %ld ms", handle, kind, scope, planMs);
    return r;
}

// [RouteAsync] NoPathMemo (0 off, 1 water / boat requests, 2 every request of an NPC router), NoPathMemoMinutes (default 120)
static void ReadNoPathMemo(const wchar_t* ini) {
    NoPathMemoCounters& c = ts3native_nopathmemo;
    const int mode = Clamp(GetPrivateProfileIntW(L"RouteAsync", L"NoPathMemo", 0, ini), 0, 2);
    c.ttlMin = Clamp(GetPrivateProfileIntW(L"RouteAsync", L"NoPathMemoMinutes", 120, ini), 1, 24 * 60);
    g_npmTtl = NpmTtlTicks(c.ttlMin);
    if (!mode) { Log("route nopath memo off (NoPathMemo=0)"); return; }
    c.mode = mode;
    c.ready = 1;
    g_npmOn = 1;
    if (NpmPcInstall()) {
        c.ready |= 8;
        Log("OK   route nopath memo: je at 0x%08X -> NpmPcStub (answers read FailedBecauseNoPathToDestination)", kNpmPcSite + 2);
    } else {
        Log("SKIP route nopath memo: bytes at 0x%08X differ (not the Steam 1.67 Route_PlanCommon): record only, nothing answered",
            kNpmPcSite);
    }
    Log("route nopath memo: NoPathMemo=%d NoPathMemoMinutes=%ld (%lu sim ticks), %d entries; needs the Route_PlanLocked detour "
        "and the [RouteSnap] counters (turned on by this key)", mode, c.ttlMin, (DWORD)g_npmTtl, NPM_ENTRIES);
}

static void NpmLogCounters() {
    const NoPathMemoCounters& c = ts3native_nopathmemo;
    if (!c.ready) return;
    AcquireSRWLockShared(&g_npmLock);
    const int live = NpmTabLive(&g_npmTab);
    ReleaseSRWLockShared(&g_npmLock);
    Log("route nopath memo: mode %ld ready %ld recorded %ld served %ld (%ld ms) invalidated %ld expired %ld evicted %ld dropped %ld "
        "live %d, exempt %ld, not recorded: cut %ld sim blocker %ld world moved %ld other %ld, refused %ld", c.mode, c.ready,
        c.recorded, c.served, c.servedMs, c.invalidated, c.expired, c.evicted, c.dropped, live, c.exempt, c.notRecorded[0],
        c.notRecorded[1], c.notRecorded[2], c.notRecorded[3], c.refused);
}
