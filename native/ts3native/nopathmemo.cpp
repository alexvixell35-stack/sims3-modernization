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
// route the scope belongs to must be this plan's route object (Route_PlanCommon's this = outList - 0xE0) with this plan's
// follower (route +0x40/+0x44 == guid args); when the slot names another route (a nested plan overwrote it, or its op 12
// cleared it), the per-handle table of open plans (g_npmScopes, NpmScopeNote) is asked the same way; else unknown. Household, selected and unknown scope never record and are never answered (HhExempt; [Experimental]
// HouseholdToo=1 lifts it, the answer being a cached real failure).
// What: Mode 1 = requests with water or boat planning (RouteOption hi 0x10000000 EnableWaterPlanning / 0x20000000
// EnablePondPlanning, RouteOption2 lo 0x2 EnablePlanningAsBoat / 0x8 BeginAsBoat [verified docs/11 Q2, docs/99 §11.1]);
// Mode 2 = any request of such a router (also the houseboat gangway leg, which has water and boat planning off, docs/642 §6).
// Record: Route_PlanLocked returned 0 and the first path's status is 7/8 (FailedBecauseNoPathToDestination, docs/93 §2.1),
// no path in the list names a Sim blocker (record kind 0x14 with an id other than the follower; RouteBlockerType Vehicle is
// never produced natively [verified 0x752620, docs/93 §2.2]), no exit cut the plan (PlanDeadlineMs / UnseenFallback session
// skips and caps, SyncCapMs, the worker's job deadlines, a budget cap), the world was quiet (no pending lot updates) and the
// static world version did not move during the plan, and the plan took at least NoPathMemoMinMs (default 50: cheap failures
// would evict the slow ones). Key (NpmReqOf): routing world, follower ObjectGuid, agent class, the four RouteOption words and
// the route's destination set; the start is matched within 1 m of the recorded one. The first in-game run (Isla Paradiso,
// 2026-11-19: recorded 514, served 0) keyed start and goal points in 0.5 m cells (RouteKey): the follower drifts between
// tries and the candidate step's goal points differ per try, so no retry matched. Capped `miss` lines name the key part
// that differs from the nearest entry of the same follower; `exempt` lines name slow no-path plans the scope rule kept out.
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
// Counters: the ts3native_nopathmemo export (docs/registry.md: recorded, served, ms stood for, invalidated, ...).
// docs/643 §7: NoPathMemoFuzzy (ts3fixes npmfuzzy, op 38) also answers a plan whose goal lies within 1 m of a goal the same
// follower just failed to reach with no path (same request shape, start 0.5 m, short TTL, nothing but no-path plans of that
// follower since: NpmFzNote), for EA's FollowerMeetUpAtPoint retries; NoPathMemoHousehold (npmhousehold) lets household,
// selected and unknown scope in, except routes ts3fixes marks as a player command (op 39, NpmIsCmd).
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
    LONG minMs;           // +0x40 NoPathMemoMinMs: failures that took less are not recorded
    LONG cheap;           // +0x44 recordable failures under minMs (not recorded)
    LONG misses;          // +0x48 recorded failures that found no entry of their key, or one too far (the miss lines)
    LONG tooFar;          // +0x4C look-ups whose entry's start was more than 1 m away (the plan ran)
    LONG notPlanCommon;   // +0x50 eligible plans not from Route_PlanCommon's call site (road plans): run as vanilla
    LONG exemptSlow;      // +0x54 exempt plans that took >= minMs and failed with no path (the exempt lines)
    LONG scopeViaMap;     // +0x58 eligible plans whose scope came from the per-handle table (the slot named another route or none)
    LONG slowNpc;         // +0x5C plans of scope 2 / 3 that ran >= minMs (any result): slow NPC plans the memo sees
    LONG slowNpcViaMap;   // +0x60 of those, scoped by the per-handle table
    // docs/643 §7, appended (ts3fixes npmfuzzy / npmhousehold, UserDiag ops 38 / 39)
    volatile LONG fuzzy;      // +0x64 runtime switch NoPathMemoFuzzy: 1 = a goal near a recorded no-path goal is answered too
    volatile LONG household;  // +0x68 runtime switch NoPathMemoHousehold: 1 = household, selected and unknown scope record / are answered
    LONG fzTtlMin;            // +0x6C NoPathMemoFuzzyMinutes
    LONG fzRecorded;          // +0x70 fuzzy entries recorded (with the exact one)
    LONG fzServed;            // +0x74 plans answered by a fuzzy entry
    LONG fzServedMs;          // +0x78 ms of the recorded plans those answers stand for
    LONG fzFar;               // +0x7C fuzzy look-ups with entries of this follower and request, none near enough (the plan ran)
    LONG fzDropped;           // +0x80 fuzzy entries dropped because a plan of their follower did not fail with no path
    LONG fzStale;             // +0x84 fuzzy entries dropped on look-up: TTL, clock back, world version moved
    LONG fzEvicted;           // +0x88 live fuzzy entries evicted (LRU)
    LONG hhLifted;            // +0x8C plans of household / selected / unknown scope let in by NoPathMemoHousehold
    LONG hhServed;            // +0x90 of all answers (exact and fuzzy), those of such scopes
    LONG cmdExempt;           // +0x94 plans of a route ts3fixes marked as the selected Sim's own command (op 39): always searched
    LONG optMsgs;             // +0x98 op 38 messages (ts3fixes switch state) received
};
extern "C" __declspec(dllexport) NoPathMemoCounters ts3native_nopathmemo = {};

static NpmTable g_npmTab;
static SRWLOCK g_npmLock = SRWLOCK_INIT;
static const void* g_npmWorld;  // the routing world the table belongs to (g_npmLock)
static uint64_t g_npmTtl;
static volatile LONG g_npmMinute, g_npmLines[4];  // per minute: recorded, served, exempt, miss lines
static const float kNpmStartM = 1.0f;  // a retry's start within this of the recorded one is the same request
// docs/643 §7 fuzzy table (g_npmLock too); g_npmFzLive mirrors its live count so plans skip the lock when it is empty
static NpmFzTable g_npmFz;
static volatile LONG g_npmFzLive;
static uint64_t g_npmFzTtl;
static void NpmFzLiveSet() { InterlockedExchange(&g_npmFzLive, NpmFzLive(&g_npmFz)); }  // g_npmLock held

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

// Route scopes by handle, for nested plans. ts3fixes sends a route's scope (UserDiag ops 10 / 11 / 13, UfPlanBody) first thing
// in every plan and ends it with op 12. plandeadline.cpp keeps one slot (g_kdScopeH), which a plan inside another plan's
// callback overwrites and then clears with its own op 12: EA's PostPlanRouteCallback -> CheckAndUpdateForHouseboats plans the
// houseboat legs on their own routes inside the outer plan, so the outer route's next Route_PlanLocked runs with the slot at 0
// (unknown, exempt). This table keeps every open plan's scope until its own op 12; sim thread writes, the worker reads.
struct NpmScopeRec { DWORD h; LONG scope; DWORD t; };
enum { NPM_SCOPE_RECS = 16 };
static NpmScopeRec g_npmScopes[NPM_SCOPE_RECS];
static SRWLOCK g_npmScLock = SRWLOCK_INIT;
static const DWORD kNpmScopeMaxAgeMs = 120000;  // an op 12 lost (an exception past UfPlan's finally): never older than this

// Player commands (docs/643 §7): ts3fixes marks the route of the selected Sim's own user-directed interaction (a Go Here
// click, not a follower's situation interaction) with op 39 right after the scope op; op 12 of that route ends the mark.
// NoPathMemoHousehold never records or answers those: a player command always gets a real search.
enum { NPM_CMD_RECS = 8 };
static NpmScopeRec g_npmCmd[NPM_CMD_RECS];  // scope unused (g_npmScLock)

// userdiag.cpp UdTextMsg, every op: 10 household or unknown (1), 11 unseen (2), 13 seen NPC (3), 12 the plan is over,
// 39 the route is the selected Sim's own command
static void NpmScopeNote(int op, DWORD handle) {
    if (!g_npmOn || !handle || (op != 10 && op != 11 && op != 12 && op != 13 && op != 39)) return;
    const DWORD now = GetTickCount();
    AcquireSRWLockExclusive(&g_npmScLock);
    if (op == 12) {
        for (int i = 0; i < NPM_SCOPE_RECS; i++)
            if (g_npmScopes[i].h == handle) g_npmScopes[i].h = 0;
        for (int i = 0; i < NPM_CMD_RECS; i++)
            if (g_npmCmd[i].h == handle) g_npmCmd[i].h = 0;
    } else if (op == 39) {
        NpmScopeRec* pick = NULL;
        for (int i = 0; i < NPM_CMD_RECS && !pick; i++)
            if (g_npmCmd[i].h == handle) pick = &g_npmCmd[i];
        for (int i = 0; i < NPM_CMD_RECS && !pick; i++)
            if (!g_npmCmd[i].h || now - g_npmCmd[i].t >= kNpmScopeMaxAgeMs) pick = &g_npmCmd[i];
        if (!pick) {  // 8 commands open at once: replace the oldest
            pick = &g_npmCmd[0];
            for (int i = 1; i < NPM_CMD_RECS; i++)
                if (now - g_npmCmd[i].t > now - pick->t) pick = &g_npmCmd[i];
        }
        pick->h = handle, pick->scope = 0, pick->t = now;
    } else {
        NpmScopeRec* pick = NULL;
        for (int i = 0; i < NPM_SCOPE_RECS && !pick; i++)
            if (g_npmScopes[i].h == handle) pick = &g_npmScopes[i];
        for (int i = 0; i < NPM_SCOPE_RECS && !pick; i++)
            if (!g_npmScopes[i].h || now - g_npmScopes[i].t >= kNpmScopeMaxAgeMs) pick = &g_npmScopes[i];
        if (!pick) {  // 16 plans open at once: replace the oldest
            pick = &g_npmScopes[0];
            for (int i = 1; i < NPM_SCOPE_RECS; i++)
                if (now - g_npmScopes[i].t > now - pick->t) pick = &g_npmScopes[i];
        }
        pick->h = handle, pick->scope = op == 11 ? 2 : op == 13 ? 3 : 1, pick->t = now;
    }
    ReleaseSRWLockExclusive(&g_npmScLock);
}

// the route object of handle h is this plan's route (Route_PlanCommon's this, ecx -> esi, whose +0xE0 is the out list) with
// this plan's follower
static bool NpmRouteIs(DWORD h, const u8* planRoute, DWORD guidLo, DWORD guidHi) {
    __try {
        const u8* rm = *g_orRmSlot;
        const u8* route = rm ? g_orRoute(rm, NULL, h) : NULL;
        return route && route == planRoute && *(const DWORD*)(route + 0x40) == guidLo && *(const DWORD*)(route + 0x44) == guidHi;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

// the plan's route is one ts3fixes marked as the selected Sim's own command (op 39)
static bool NpmIsCmd(const u8* planRoute, DWORD guidLo, DWORD guidHi) {
    NpmScopeRec recs[NPM_CMD_RECS];
    AcquireSRWLockShared(&g_npmScLock);
    memcpy(recs, g_npmCmd, sizeof(recs));
    ReleaseSRWLockShared(&g_npmScLock);
    const DWORD now = GetTickCount();
    for (int i = 0; i < NPM_CMD_RECS; i++)
        if (recs[i].h && now - recs[i].t < kNpmScopeMaxAgeMs && NpmRouteIs(recs[i].h, planRoute, guidLo, guidHi)) return true;
    return false;
}

// UserDiag op 38 (ts3fixes, on a change of its switches and once per world): bit 0 npmfuzzy, bit 1 npmhousehold. 1 = the memo
// is installed and took them, 0 = not installed (the switches do nothing)
static u8 NpmOptMsg(DWORD lo) {
    NoPathMemoCounters& c = ts3native_nopathmemo;
    if (!g_npmOn) return 0;
    InterlockedIncrement(&c.optMsgs);
    const LONG fz = (lo & 1) ? 1 : 0, hh = (lo & 2) ? 1 : 0;
    const bool fzMoved = InterlockedExchange(&c.fuzzy, fz) != fz;
    const bool hhMoved = InterlockedExchange(&c.household, hh) != hh;
    if (fzMoved || hhMoved)
        Log("route nopath memo: ts3fixes switches npmfuzzy %ld npmhousehold %ld", fz, hh);
    return 1;
}
// op 39 itself is noted by NpmScopeNote (UdTextMsg calls it for every op); the answer: 1 = the mark has an effect
static u8 NpmCmdMsg() {
    return g_npmOn && ts3native_nopathmemo.household ? 1 : 0;
}

// this plan's router scope (0 unknown, 1 seen household side, 2 unseen, 3 seen NPC) and route handle, see the header.
// planRoute = outList - 0xE0. *viaMap = the scope came from g_npmScopes (the slot named another route or none).
static LONG NpmScope(DWORD guidLo, DWORD guidHi, const u8* planRoute, DWORD* handle, int* kind, bool* viaMap) {
    LONG scope = 0;
    DWORD h = 0;
    *kind = -1, *viaMap = false;
    const AsyncJob* j = g_runJob;
    if (GetCurrentThreadId() == g_workerTid && g_async.state == A_RUNNING && j && j->handle == g_async.handle) {
        scope = j->uf ? 2 : j->kdScope, h = j->handle, *kind = j->kind;
    } else {  // the sim thread, or the worker serving the sim's sync plan while the sim waits for it
        h = g_kdScopeH, scope = h ? g_kdScope : 0;
        if (t_p10) *kind = (int)(t_p10 & 0xFF) - 1;  // [ProbeP10] on: the route IC this plan runs in
    }
    *handle = h;
    if (h && scope && NpmRouteIs(h, planRoute, guidLo, guidHi)) return scope;
    // the slot is another route's (a nested plan's) or empty: the open plan of this route, by handle
    NpmScopeRec recs[NPM_SCOPE_RECS];
    AcquireSRWLockShared(&g_npmScLock);
    memcpy(recs, g_npmScopes, sizeof(recs));
    ReleaseSRWLockShared(&g_npmScLock);
    const DWORD now = GetTickCount();
    for (int i = 0; i < NPM_SCOPE_RECS; i++) {
        if (!recs[i].h || now - recs[i].t >= kNpmScopeMaxAgeMs || !NpmRouteIs(recs[i].h, planRoute, guidLo, guidHi)) continue;
        *handle = recs[i].h, *viaMap = true;
        return recs[i].scope;
    }
    return 0;
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
static int NpmFailKind(const int* outList, DWORD guidLo, DWORD guidHi, int* status = NULL) {  // *status: the first path's, -1 none
    __try {
        const int* node = (const int*)(uintptr_t)outList[0];
        if (status) *status = -1;
        if (node == outList) return 2;  // empty: a pre-lock failure (Failed, no blocker), nothing to memo
        for (int i = 0; node != outList && i < 8; i++, node = (const int*)(uintptr_t)node[0]) {
            void* path = (void*)(uintptr_t)node[2];
            if (!path) continue;
            if (i == 0) {
                const int st = ((PathStatusFn)(*(void***)path)[0x20 / 4])(path, NULL);
                if (status) *status = st;
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
    if (!kNpmPcSite || !kNpmPcBack || !kNpmPlanRet) return false;  // a build with no mapped address (EA 1.69 table: 0): record only
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
    if (InterlockedExchange(&g_npmMinute, minute) != minute) g_npmLines[0] = g_npmLines[1] = g_npmLines[2] = g_npmLines[3] = 0;
    return InterlockedIncrement(&g_npmLines[which]) <= 10;  // 10 lines a minute each, like boattaximemo / houseboatlegmemo
}

// the request of a plan from Route_PlanCommon's call site: route = outList - 0xE0 (0x760DCA `lea edi,[esi+0xE0]; push edi`)
// [verified disassembly, Steam 1.67]. Destination set = the route's +0x144 container, hashed as ReplanDedupe does (its list
// +0x18, node = destination + 4, first 0x40 bytes of each destination, then the container's words +0x20..+0x40): the
// destination EA asked for, the same on every retry, while the goal points Route_PlanCommon's candidate step generates for each
// try (the `goals` argument) are not. Start = the first start point (x, y, z). false = no key (no route, no destination set).
// Fuzzy part (docs/643 §7): shape = destination count, each destination's vtable (its type), the set's flag +0x30, its valid
// rooms (vector +0x20/+0x24 of int room ids, contents, not the pointers) and their lot +0x38/+0x3C (docs/92 §route fields
// [verified]); geometry = centroid and spread of the `goals` records (64 bytes, x/y/z at +0, docs/99 §4.1).
static bool NpmReqOf(void* world, const int* outList, const int* starts, const int* goals, DWORD agent, const u8* opts,
                     DWORD guidLo, DWORD guidHi, NpmReq* r) {
    u64 shape = 0x84222325CBF29CE4ull;
    memset(r, 0, sizeof(*r));
    __try {
        const u8* route = (const u8*)outList - 0xE0;
        if (*(const DWORD*)(route + 0x40) != guidLo || *(const DWORD*)(route + 0x44) != guidHi) return false;
        const u8* ds = *(const u8* const*)(route + 0x144);
        if (!ds) return false;
        u64 g = 0xCBF29CE484222325ull;
        const u8* head = ds + 0x18;
        const u8* n = *(const u8* const*)head;
        int nd = 0;
        for (; n && n != head && nd < 16; nd++, n = *(const u8* const*)n) {
            for (int w = 0; w < 16; w++) g = Fnv(g, ((const DWORD*)(n - 4))[w]);
            shape = Fnv(shape, *(const DWORD*)(n - 4));
        }
        for (DWORD off = 0x20; off < 0x40; off += 4) g = Fnv(g, *(const DWORD*)(ds + off));
        r->goal = g;
        shape = Fnv(Fnv(Fnv(Fnv(shape, (DWORD)nd), *(const DWORD*)(ds + 0x30)), *(const DWORD*)(ds + 0x38)), *(const DWORD*)(ds + 0x3C));
        const int* rb = *(const int* const*)(ds + 0x20);
        const int* re = *(const int* const*)(ds + 0x24);
        const int nr = rb && re > rb ? (int)(re - rb) : 0;
        shape = Fnv(shape, (DWORD)nr);
        for (int i = 0; i < nr && i < 16; i++) shape = Fnv(shape, (DWORD)rb[i]);
        const u8* gb = (const u8*)(uintptr_t)goals[0];
        const u8* ge = (const u8*)(uintptr_t)goals[1];
        if (gb && ge >= gb + 64) NpmGoalGeom(gb, (int)((ge - gb) / 64), 64, r);
        const float* s = (const float*)(uintptr_t)starts[0];
        if (!s || starts[1] == starts[0]) return false;
        r->start[0] = s[0], r->start[1] = s[1], r->start[2] = s[2];
        for (int i = 0; i < 4; i++) r->opts[i] = ((const DWORD*)(opts + 0x28))[i];
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
    r->foll = (u64)guidHi << 32 | guidLo, r->agent = agent;
    r->key = NpmReqKey(*r, (uint64_t)(uintptr_t)world);
    r->fkey = NpmFuzzyKey(*r, (uint64_t)(uintptr_t)world, shape);
    return true;
}

// capped diagnostic for a recordable failure that found no entry of its key: the nearest entry of the same follower and what
// differs (destination set, options, agent, start distance), so a key part that changes between EA's retries shows in the log
static void NpmMissLine(DWORD handle, int kind, const NpmReq& q, LONG planMs) {
    NpmEntry e;
    bool have = false;
    AcquireSRWLockShared(&g_npmLock);
    const NpmEntry* n = NpmTabNearest(&g_npmTab, q.foll, q.start);
    if (n) e = *n, have = true;
    ReleaseSRWLockShared(&g_npmLock);
    if (!have) {
        Log("route nopath memo: miss route %08lX kind %d, %ld ms: no entry of this follower", handle, kind, planMs);
        return;
    }
    const uint32_t ox[4] = {e.req.opts[0] ^ q.opts[0], e.req.opts[1] ^ q.opts[1], e.req.opts[2] ^ q.opts[2], e.req.opts[3] ^ q.opts[3]};
    Log("route nopath memo: miss route %08lX kind %d, %ld ms: nearest entry of this follower %.2f m away, destination %s, agent %s, "
        "options xor %08X %08X %08X %08X, recorded %ld ms", handle, kind, planMs, sqrtf(NpmDist2(e.req.start, q.start)),
        e.req.goal == q.goal ? "same" : "differs", e.req.agent == q.agent ? "same" : "differs", ox[0], ox[1], ox[2], ox[3], (LONG)e.ms);
}

// docs/643 §7: a plan of this follower ran and did not fail with no path (success, another failure, an empty out list):
// none of its fuzzy entries may answer any more. fk = NpmFailKind's answer, or -1 = not read yet (read here when needed).
static void NpmFzNote(u8 r, int fk, const int* outList, DWORD guidLo, DWORD guidHi) {
    if (!g_npmFzLive) return;
    if (fk < 0) fk = r ? 2 : NpmFailKind(outList, guidLo, guidHi);
    if (fk == 0) return;  // no path again: the entries stand
    AcquireSRWLockExclusive(&g_npmLock);
    const int d = NpmFzDropFoll(&g_npmFz, (u64)guidHi << 32 | guidLo);
    if (d) NpmFzLiveSet();
    ReleaseSRWLockExclusive(&g_npmLock);
    if (d) InterlockedExchangeAdd(&ts3native_nopathmemo.fzDropped, d);
}
// the plan as vanilla, with the follower's fuzzy entries dropped when it did not fail with no path
static u8 NpmRun(void* world, void* edx, int* outList, int* starts, int* goals, DWORD agent, DWORD doPlan, u8* opts, int* out1,
                 int* out2, DWORD guidLo, DWORD guidHi) {
    const u8 r = CallPlanEo(world, edx, outList, starts, goals, agent, doPlan, opts, out1, out2, guidLo, guidHi);
    if (g_npmFzLive && outList && (guidLo | guidHi)) NpmFzNote(r, -1, outList, guidLo, guidHi);
    return r;
}

static u8 NpmPlan(void* world, void* edx, int* outList, int* starts, int* goals, DWORD agent, DWORD doPlan, u8* opts,
                  int* out1, int* out2, DWORD guidLo, DWORD guidHi) {
    NoPathMemoCounters& c = ts3native_nopathmemo;
    const LONG mode = c.mode;
    bool water = false, dyn = false;
    if ((!mode || !c.fuzzy) && g_npmFzLive) {  // switched off: successes go unseen, so nothing recorded may answer after a switch-on
        AcquireSRWLockExclusive(&g_npmLock);
        const int n = NpmFzClear(&g_npmFz);
        NpmFzLiveSet();
        ReleaseSRWLockExclusive(&g_npmLock);
        if (n) InterlockedExchangeAdd(&c.fzStale, n);
    }
    if (mode && outList && starts && goals && opts && doPlan && (guidLo | guidHi)) {
        __try {
            const DWORD* o = (const DWORD*)(opts + 0x28);  // RouteOption lo/hi, RouteOption2 lo/hi
            water = (o[1] & 0x30000000) || (o[2] & 0xA);
            dyn = (o[0] & (4 | 0x1000)) != 0;
        } __except (EXCEPTION_EXECUTE_HANDLER) {
            return NpmRun(world, edx, outList, starts, goals, agent, doPlan, opts, out1, out2, guidLo, guidHi);
        }
    }
    if (!mode || !(water || mode == 2) || !outList || !starts || !goals || !doPlan || !(guidLo | guidHi))
        return NpmRun(world, edx, outList, starts, goals, agent, doPlan, opts, out1, out2, guidLo, guidHi);
    if (t_npmRet != kNpmPlanRet) {  // not Route_PlanCommon's call (road plans): its answer could not read 16, never memoed
        InterlockedIncrement(&c.notPlanCommon);
        return NpmRun(world, edx, outList, starts, goals, agent, doPlan, opts, out1, out2, guidLo, guidHi);
    }
    DWORD handle;
    int kind;
    bool viaMap;
    const u8* planRoute = (const u8*)outList - 0xE0;
    const LONG scope = NpmScope(guidLo, guidHi, planRoute, &handle, &kind, &viaMap);  // Route_PlanCommon's call only (above)
    if (viaMap) InterlockedIncrement(&c.scopeViaMap);
    const char* via = viaMap ? " (scope by route handle)" : "";
    // who: NPC scope (2 / 3); with NoPathMemoHousehold (ts3fixes npmhousehold) also household, selected and unknown scope, as
    // the answer is EA's own failure of the same request; never the selected Sim's own command (op 39: always a real search)
    const bool npc = scope == 2 || scope == 3;
    const bool cmd = !npc && c.household && NpmIsCmd(planRoute, guidLo, guidHi);
    const bool lift = !npc && !cmd && c.household;
    if (cmd) InterlockedIncrement(&c.cmdExempt);
    if (lift) InterlockedIncrement(&c.hhLifted);
    LARGE_INTEGER t0;
    if (cmd || (!npc && !lift && HhExempt(true))) {
        InterlockedIncrement(&c.exempt);
        QueryPerformanceCounter(&t0);
        const u8 r0 = CallPlanEo(world, edx, outList, starts, goals, agent, doPlan, opts, out1, out2, guidLo, guidHi);
        const LONG ms0 = UsSince(t0) / 1000;
        const int fk0 = r0 ? 2 : NpmFailKind(outList, guidLo, guidHi);
        NpmFzNote(r0, fk0, outList, guidLo, guidHi);
        // which slow no-path plans the scope rule keeps out (log only): household / selected / unknown scope, a player command,
        // or a scope answer that belongs to another follower's route
        if (!r0 && ms0 >= c.minMs && fk0 == 0) {
            InterlockedIncrement(&c.exemptSlow);
            if (NpmLineOk(2)) Log("route nopath memo: exempt route %08lX kind %d scope %ld, %ld ms no path%s", handle, kind, scope, ms0,
                                  cmd ? " (player command)" : "");
        }
        return r0;
    }
    uint64_t now = 0;
    NpmReq q;
    const bool live = g_rgLock && g_rgLock == (u8*)world + 0x918;  // the game's routing world, not a P5 clone
    const bool verOk = (ts3native_counters.snReady & 5) == 5, tickOk = NpmSimTick(&now);
    if (verOk && tickOk && !(c.ready & 6)) InterlockedOr(&c.ready, 6);
    if (!live || !verOk || !tickOk || !NpmReqOf(world, outList, starts, goals, agent, opts, guidLo, guidHi, &q)) {
        InterlockedIncrement(&c.refused);
        return NpmRun(world, edx, outList, starts, goals, agent, doPlan, opts, out1, out2, guidLo, guidHi);
    }
    DWORD ver0, sver0;
    PrVersions(&ver0, &sver0);
    const bool quiet = RtQuietWorld(g_rgLock);
    // an answer needs the world quiet, the out list empty and the result stub in (else it would read 2); otherwise the plan
    // runs and the entries stay
    const bool canServe = quiet && (const int*)(uintptr_t)outList[0] == outList && (c.ready & 8);
    int look = NPM_MISS, fzLook = NPM_MISS, fzStale = 0;
    int32_t ms = 0;
    float fzGoalM = 0, fzStartM = 0;
    AcquireSRWLockExclusive(&g_npmLock);
    if (g_npmWorld != world) {
        const int n = NpmTabClear(&g_npmTab);
        if (n) InterlockedExchangeAdd(&c.invalidated, n);
        fzStale += NpmFzClear(&g_npmFz);
        NpmFzLiveSet();
        g_npmWorld = world;
    }
    look = NpmTabLook(&g_npmTab, q, now, g_npmTtl, sver0, ver0, kNpmStartM, &ms);
    if (look == NPM_HIT && !canServe) look = -1;
    if (c.fuzzy && look != NPM_HIT && look != -1 && g_npmFzLive) {
        int d = 0;
        fzLook = NpmFzLook(&g_npmFz, q, now, g_npmFzTtl, sver0, ver0, &ms, &fzGoalM, &fzStartM, &d);
        if (d) fzStale += d, NpmFzLiveSet();
        if (fzLook == NPM_HIT && !canServe) fzLook = -1;
    }
    ReleaseSRWLockExclusive(&g_npmLock);
    if (fzStale) InterlockedExchangeAdd(&c.fzStale, fzStale);
    if (look == NPM_INVALID) InterlockedIncrement(&c.invalidated);
    else if (look == NPM_EXPIRED) InterlockedIncrement(&c.expired);
    else if (look == NPM_FAR) InterlockedIncrement(&c.tooFar);
    if (fzLook == NPM_FAR) InterlockedIncrement(&c.fzFar);
    if (look == NPM_HIT || fzLook == NPM_HIT) {
        const bool fz = look != NPM_HIT;
        InterlockedIncrement(fz ? &c.fzServed : &c.served);
        InterlockedExchangeAdd(fz ? &c.fzServedMs : &c.servedMs, ms);
        if (lift) InterlockedIncrement(&c.hhServed);
        t_npmServed = true;  // NpmPcStub: Route_PlanCommon's empty-list branch reports 16, not 2
        if (NpmLineOk(1)) {
            if (fz)
                Log("route nopath memo: fuzzy served route %08lX kind %d scope %ld follower %08lX%08lX, goal %.2f m and start %.2f m "
                    "from a recorded no-path plan, stands for %ld ms%s", handle, kind, scope, guidHi, guidLo, fzGoalM, fzStartM, (LONG)ms, via);
            else
                Log("route nopath memo: served route %08lX kind %d scope %ld, stands for %ld ms%s", handle, kind, scope, (LONG)ms, via);
        }
        return 0;  // out list untouched; Route_PlanCommon reports FailedBecauseNoPathToDestination with no blocker
    }
    NpmCutSnap cut;
    NpmCutBefore(cut);
    QueryPerformanceCounter(&t0);
    const u8 r = CallPlanEo(world, edx, outList, starts, goals, agent, doPlan, opts, out1, out2, guidLo, guidHi);
    const LONG planMs = UsSince(t0) / 1000;
    if (planMs >= c.minMs) {
        InterlockedIncrement(&c.slowNpc);
        if (viaMap) InterlockedIncrement(&c.slowNpcViaMap);
    }
    const bool wasCut = NpmCutAfter(cut);
    int st = -1;
    const int fk = r ? 2 : NpmFailKind(outList, guidLo, guidHi, &st);
    NpmFzNote(r, fk, outList, guidLo, guidHi);  // a success (or another failure) of this follower: its fuzzy entries go
    DWORD ver1, sver1;
    PrVersions(&ver1, &sver1);
    const int why = wasCut ? 0 : fk == 1 ? 1 : fk == 2 ? 3 : !quiet || sver1 != sver0 ? 2 : -1;
    if (why >= 0) {
        if (!r) InterlockedIncrement(&c.notRecorded[why]);
        // a slow NPC failure the memo keeps out, and why (proves the scope and names the rule that stops it)
        if (!r && planMs >= c.minMs && NpmLineOk(2)) {
            static const char* const kWhy[3] = {"cut by an exit", "a Sim blocker", "world not quiet or version moved"};
            if (why == 3) Log("route nopath memo: not recorded route %08lX kind %d scope %ld, %ld ms: path status %d%s", handle, kind, scope, planMs, st, via);
            else Log("route nopath memo: not recorded route %08lX kind %d scope %ld, %ld ms: %s%s", handle, kind, scope, planMs, kWhy[why], via);
        }
        if (why != 0) {  // a plan of a memoed request that did not fail the memoed way: the memo is wrong now
            AcquireSRWLockExclusive(&g_npmLock);
            const int d = g_npmWorld == world ? NpmTabDrop(&g_npmTab, q.key) : 0;
            ReleaseSRWLockExclusive(&g_npmLock);
            if (d) InterlockedIncrement(&c.dropped);
        }
        return r;
    }
    if (planMs < c.minMs) {  // a cheap failure: answering it saves little, and recording it would evict the slow ones
        InterlockedIncrement(&c.cheap);
        return r;
    }
    if (look == NPM_MISS || look == NPM_FAR) {
        InterlockedIncrement(&c.misses);
        if (NpmLineOk(3)) NpmMissLine(handle, kind, q, planMs);
    }
    AcquireSRWLockExclusive(&g_npmLock);
    const bool same = g_npmWorld == world;
    const int ev = same ? NpmTabPut(&g_npmTab, q, now, sver1, ver1, dyn, planMs) : 0;
    const bool fzRec = same && c.fuzzy && q.gn > 0;
    const int fev = fzRec ? NpmFzPut(&g_npmFz, q, now, sver1, ver1, dyn, planMs) : 0;
    if (fzRec) NpmFzLiveSet();
    ReleaseSRWLockExclusive(&g_npmLock);
    if (ev) InterlockedIncrement(&c.evicted);
    if (fev) InterlockedIncrement(&c.fzEvicted);
    if (fzRec) InterlockedIncrement(&c.fzRecorded);
    InterlockedIncrement(&c.recorded);
    if (NpmLineOk(0)) Log("route nopath memo: recorded route %08lX kind %d scope %ld, %ld ms%s%s", handle, kind, scope, planMs, via,
                          fzRec ? " (+fuzzy)" : "");
    return r;
}

// [RouteAsync] NoPathMemo (0 off, 1 water / boat requests, 2 every request of an NPC router), NoPathMemoMinutes (default 120)
static void ReadNoPathMemo(const wchar_t* ini) {
    NoPathMemoCounters& c = ts3native_nopathmemo;
    const int mode = Clamp(GetPrivateProfileIntW(L"RouteAsync", L"NoPathMemo", 0, ini), 0, 2);
    c.ttlMin = Clamp(GetPrivateProfileIntW(L"RouteAsync", L"NoPathMemoMinutes", 120, ini), 1, 24 * 60);
    g_npmTtl = NpmTtlTicks(c.ttlMin);
    c.minMs = Clamp(GetPrivateProfileIntW(L"RouteAsync", L"NoPathMemoMinMs", 50, ini), 0, 60000);
    // docs/643 §7: start values of the runtime switches; ts3fixes npmfuzzy / npmhousehold set them through op 38 when present
    c.fuzzy = Clamp(GetPrivateProfileIntW(L"RouteAsync", L"NoPathMemoFuzzy", 0, ini), 0, 1);
    c.household = Clamp(GetPrivateProfileIntW(L"RouteAsync", L"NoPathMemoHousehold", 0, ini), 0, 1);
    c.fzTtlMin = Clamp(GetPrivateProfileIntW(L"RouteAsync", L"NoPathMemoFuzzyMinutes", 10, ini), 1, 60);
    g_npmFzTtl = NpmTtlTicks(c.fzTtlMin);
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
    Log("route nopath memo: NoPathMemo=%d NoPathMemoMinutes=%ld (%lu sim ticks) NoPathMemoMinMs=%ld, %d entries, start within "
        "%.1f m; needs the Route_PlanLocked detour and the [RouteSnap] counters (turned on by this key)", mode, c.ttlMin,
        (DWORD)g_npmTtl, c.minMs, NPM_ENTRIES, kNpmStartM);
    Log("route nopath memo: NoPathMemoFuzzy=%ld NoPathMemoHousehold=%ld (runtime: ts3fixes npmfuzzy / npmhousehold, op 38) "
        "NoPathMemoFuzzyMinutes=%ld (%lu sim ticks), fuzzy: %d entries, same follower and request, start within %.1f m, goal "
        "centroid within %.1f m (height %.1f m), spread within %.1f m, none after a plan of that follower that did not fail with "
        "no path", c.fuzzy, c.household, c.fzTtlMin, (DWORD)g_npmFzTtl, NPM_FZ_ENTRIES, NPM_FZ_START_M, NPM_FZ_GOAL_M, NPM_FZ_DY_M,
        NPM_FZ_GOAL_M);
}

static void NpmLogCounters() {
    const NoPathMemoCounters& c = ts3native_nopathmemo;
    if (!c.ready) return;
    AcquireSRWLockShared(&g_npmLock);
    const int live = NpmTabLive(&g_npmTab);
    ReleaseSRWLockShared(&g_npmLock);
    Log("route nopath memo: mode %ld ready %ld recorded %ld served %ld (%ld ms) invalidated %ld expired %ld evicted %ld dropped %ld "
        "live %d, exempt %ld (slow no path %ld), not recorded: cut %ld sim blocker %ld world moved %ld other %ld cheap %ld, "
        "refused %ld, misses %ld far %ld, not PlanCommon %ld; scope by route handle %ld, slow NPC plans %ld (by route handle %ld)", c.mode, c.ready, c.recorded, c.served, c.servedMs, c.invalidated,
        c.expired, c.evicted, c.dropped, live, c.exempt, c.exemptSlow, c.notRecorded[0], c.notRecorded[1], c.notRecorded[2],
        c.notRecorded[3], c.cheap, c.refused, c.misses, c.tooFar, c.notPlanCommon, c.scopeViaMap, c.slowNpc, c.slowNpcViaMap);
    if (c.fuzzy || c.household || c.fzRecorded || c.hhLifted)
        Log("route nopath memo: fuzzy %ld household %ld: fuzzy recorded %ld served %ld (%ld ms) far %ld dropped %ld stale %ld "
            "evicted %ld live %ld; household scope let in %ld served %ld, player commands searched %ld, switch messages %ld",
            c.fuzzy, c.household, c.fzRecorded, c.fzServed, c.fzServedMs, c.fzFar, c.fzDropped, c.fzStale, c.fzEvicted,
            (LONG)g_npmFzLive, c.hhLifted, c.hhServed, c.cmdExempt, c.optMsgs);
}
