// docs/674: [RouteSnap] DoorShare. Included by ts3native.cpp right before hostcalls.cpp; the door stub (HcDoorEnter) calls
// DsBefore / DsAfter around the route worker's door ask at 0x64F4D3 (World event 59, "may this Sim pass this portal").
//
// Why: DoorMemoX keys a row by the asking Sim and keeps it 250 ms, so the first ask of every Sim about every door is a host
// call (s159: 15,942 of 18,081 misses had no row). Most of those answers do not depend on the Sim at all. ts3fixes
// DoorShare.cs sends the class of each route's follower at every plan (UserDiag op 40):
//   - ordinary: a Sim that is no horse, no stray, no tomb mummy, has no proprietor role, no service and no IgnoreDoorLocks
//     flag. For it the game's handler (Sim.OnPortalPathPlan, CommonDoor.IsAllowedThrough) answers "unlocked" for a plain
//     door (a Door whose IsAllowedThrough is CommonDoor's own) with lock type Anybody from the lot's hidden rooms and the
//     door's one-way room only, and for a portal object no rule of the handler names from initialLocked only;
//   - no actor: the router is no Sim (a boat, a car). The handler does nothing for it: every portal answers initialLocked;
// as long as the lot's handler list is the game's handler alone and event 59 has the game's callback alone. ts3fixes checks
// that for the ask it has just answered and says so with a note (op 41: a hash of the ask, the kind). A live "unlocked"
// answer with a matching note becomes a row without the router in its key; any later ask for that key by a router of a class
// the row's kind speaks for is answered 2 from the row while
//   - the row's three stamps are unchanged (v2, docs/674 §9): its portal owner's (the object's portals were added or
//     removed; ts3fixes doorgen reported a lock type, lock owner, one-way room or owner list change of that door), its
//     lot's (the lot's handler list or hidden rooms changed; SetPortalLockStatus) and the global one (world load, lot added
//     or removed, build / buy exit, a doorgen source this build does not know). Events that cannot change a shared answer
//     move nothing: portal cost and age / gender flags (they only feed the key's initialLocked), lot bounds and type, and
//     doorgen's Sim-side sources (greeting, household, fire, roles, occults, resort data, age, gender), which matter to
//     DoorMemoX's per-Sim rows only;
//   - the event 59 delegate slot is unchanged, and
//   - a live ask confirmed the row within DoorShareMs (a safety net for an input nobody hooked, not part of the argument).
// Only "unlocked" is kept: a "locked" answer has a managed side effect per plan (LockedDoorsDuringPlan.Add, docs/159 §4.1).
// Every DoorShareCheck-th answer is asked anyway and compared; one difference turns DoorShare off for the session
// (`FAIL door share`). Mode 2 (shadow) answers nothing and compares every valid row.
// Who: jobs of scope 2 / 3 (unseen, seen NPC). Household, selected and unknown scope ask live unless DoorShareHousehold=1
// (or [Experimental] HouseholdToo); a route ts3fixes marked as the selected Sim's own command always asks live.
// Needs DoorMemoX's door stub, the route snapshot's hooks (its IC slots and World_FireEvent detour call DsIcBump /
// DsWevBump), ts3fixes doorgen (armed) and ts3fixes doorshare.
// Also counts, in every mode but 0: door asks by part of the job, different keys with and without the router, live-ask wait,
// the change events by source and scope, and the routers in no class by reason.
#include "doorshare-core.h"

struct DoorShareCounters {
    volatile LONG mode;       // runtime switch: 0 off, 1 answer, 2 shadow
    LONG ready;               // 1 = installed
    LONG household;           // DoorShareHousehold
    LONG checkN;              // DoorShareCheck
    LONG maxMs;               // DoorShareMs
    LONG classOps, noteOps;   // ts3fixes ops 40 / 41 seen
    LONG asks;                // door asks of the worker in a job that reached us
    LONG part[3];             // of them: before the first Route_PlanLocked, inside one, after one
    LONG keysSim, keysDoor;   // different (portal, rooms, router) keys / (portal, rooms) keys, lower bounds
    LONG hits;                // answered from a row
    LONG checks, mismatch;    // valid rows asked anyway / of them a different live answer (must stay 0)
    LONG miss[DS_FINDS];      // by DsFind: no row, (valid: unused), a stamp or the delegate moved, older than DoorShareMs
    LONG noTake[DS_TAKE_WHYS];// valid row not used: (unused), no class, another router, scope, player command, row of the other class
    LONG classAsks[3];        // asks by the job's class: none, ordinary, no actor
    LONG puts[DS_KINDS];      // rows written: (unused), plain door, inert portal, no-actor only
    LONG liveNoNote;          // live "unlocked" answers to a class router without a matching note (not shareable)
    LONG liveOther;           // live "unlocked" answers to a router in no class
    LONG liveLocked;          // live answers that were not "unlocked"
    LONG drops;               // rows removed by such an answer
    LONG live;                // live asks (host calls) made through us
    LONG unarmed;             // asks while ts3fixes doorgen was not armed (nothing answered or kept)
    LONG staleBy[DS_SCOPES];  // misses by the stamp that moved: (the delegate), owner, lot, global (first that differs, in that order)
    LONG whyAsks[DS_WHYS];    // asks of a job in no class, by ts3fixes' reason (0 = no op 40 reached the job)
    LONG bumpScope[DS_SCOPES];// change events by what they cleared: nothing, one owner, one lot, everything
    LONG bumpIc[DS_IC_SLOTS]; // ... by routesnap IC slot
    LONG bumpWev[DS_WEV_SLOTS];// ... by World event id (0-0x10)
    LONG bumpSrc[DS_SRC_SLOTS];// ... by ts3fixes doorgen source (DS_SRC_*; 15 = the arm at world load)
    volatile LONGLONG liveUs; // the live asks' wait, us
};
extern "C" __declspec(dllexport) DoorShareCounters ts3native_doorshare = {};

static DsTable g_dsTab;              // worker only
static DsStamps g_dsStamps;          // moved by the sim thread (ICs, world events, ops), read by the worker
static DsSeen g_dsSeenSim, g_dsSeenDoor;
static LONGLONG g_dsTicks;           // DoorShareMs in QPC ticks
static long g_dsCheckSeq;            // worker only
static volatile LONG g_dsNoteHash, g_dsNoteKind;  // sim thread writes inside the served call; the worker reads after it
static volatile LONG g_dsFailLog;

struct DsAsk {
    uint32_t k7[7];
    DsGen gen;
    DWORD deleg;
    LONGLONG now;
    const DsRow* row;  // a valid row that was not used as the answer (check, shadow or not allowed)
    bool on;
};

// --- change events (any thread; the stamps only ever grow) ---
static void DsBumpScope(int scope, uint32_t idHash) {
    InterlockedIncrement(&ts3native_doorshare.bumpScope[scope]);
    int32_t* const p = DsStamp(&g_dsStamps, scope, idHash);
    if (p) InterlockedIncrement((volatile LONG*)p);
}
// routesnap.cpp SnIcEnter: IC slot ic was called; slot[1], slot[2] = its first argument (an object id or a lot id)
static void DsIcBump(int ic, const DWORD* slot) {
    DoorShareCounters& c = ts3native_doorshare;
    if (!c.ready || ic < 0 || ic >= DS_IC_SLOTS) return;
    const int scope = DsIcScope(ic);
    if (ic < 4 || (ic > 14 && scope == DS_SC_NONE)) return;  // not a portal IC: not counted
    InterlockedIncrement(&c.bumpIc[ic]);
    DsBumpScope(scope, scope == DS_SC_OWNER || scope == DS_SC_LOT ? DsIdHash(slot[1], slot[2]) : 0);
}
// routesnap.cpp, World_FireEvent
static void DsWevBump(DWORD id) {
    DoorShareCounters& c = ts3native_doorshare;
    if (!c.ready || id >= DS_WEV_SLOTS) return;
    const int scope = DsWevScope((int)id);
    if (scope == DS_SC_NONE && id != 0x0F && id != 0x10) return;  // not a load or lot event: not counted
    InterlockedIncrement(&c.bumpWev[id]);
    DsBumpScope(scope, 0);
}
// UserDiag op 36 (hostcalls.cpp DmxScriptOp): a ts3fixes doorgen bump. src 0 = an older package's plain bump
static void DsSrcBump(int src, uint32_t idHash) {
    DoorShareCounters& c = ts3native_doorshare;
    if (!c.ready) return;
    InterlockedIncrement(&c.bumpSrc[src >= 0 && src < DS_SRC_SLOTS ? src : DS_SRC_UNKNOWN]);
    DsBumpScope(DsSrcScope(src, idHash), idHash);
}

// UserDiag op 41 (sim thread, inside the managed handler of the ask being served): the answer just given is shareable
static u8 DsNoteOp(DWORD lo, DWORD hi) {
    InterlockedIncrement(&ts3native_doorshare.noteOps);
    InterlockedExchange(&g_dsNoteKind, (LONG)(hi & 0xFFFF));
    InterlockedExchange(&g_dsNoteHash, (LONG)lo);
    return (u8)(ts3native_doorshare.mode != 0);
}
// UserDiag op 40 (sim thread, before a route's START): the route's follower class; 1 = DoorShare is on (ts3fixes may skip notes otherwise)
static u8 DsClassOp(DWORD lo, DWORD hi) {
    InterlockedIncrement(&ts3native_doorshare.classOps);
    DsClassSet(lo, (LONG)(hi & 0xFFFF));
    return (u8)(ts3native_doorshare.mode != 0);
}

static LONG DsUs(LONGLONG ticks) { return g_qpf.QuadPart ? (LONG)(ticks * 1000000 / g_qpf.QuadPart) : 0; }

// worker, in a job, before the ask goes out. true = answered "unlocked" from a row (the caller returns 2, no host call).
static bool DsBefore(const DWORD* key, DWORD deleg, LONGLONG now, DsAsk* a) {
    DoorShareCounters& c = ts3native_doorshare;
    a->on = false, a->row = NULL;
    const LONG mode = c.mode;
    if (!mode || !c.ready) return false;  // not installed: a runtime switch flip does nothing
    InterlockedIncrement(&c.asks);
    InterlockedIncrement(&c.part[t_dsPlDepth > 0 ? 1 : t_dsPlans ? 2 : 0]);
    const uint32_t* k9 = (const uint32_t*)key;
    if (DsSeenAdd(&g_dsSeenSim, DsHash(k9, 9))) c.keysSim = g_dsSeenSim.n;
    DsKey(k9, a->k7);
    if (DsSeenAdd(&g_dsSeenDoor, DsHash(a->k7, 7))) c.keysDoor = g_dsSeenDoor.n;
    const int cls = DsClassOf((uint32_t)t_dsBits, k9[2], k9[3]);
    InterlockedIncrement(&c.classAsks[cls]);
    const int whyNot = DsWhyOf((uint32_t)t_dsBits);
    if (whyNot >= 0) InterlockedIncrement(&c.whyAsks[whyNot]);
    if (!ts3native_doorgen.armed) { InterlockedIncrement(&c.unarmed); return false; }  // ts3fixes reports no door changes
    a->on = true, a->deleg = deleg, a->now = now;
    DsGenOf(&g_dsStamps, a->k7, &a->gen);  // before the ask: a change during it leaves a row that no longer answers
    const DsRow* row = NULL;
    const int st = DsFind(&g_dsTab, a->k7, &a->gen, deleg, now, g_dsTicks, &row);
    if (st != DS_VALID) {
        InterlockedIncrement(&c.miss[st]);
        if (st == DS_OTHERGEN) {
            const DsRow* const old = DsPeek(&g_dsTab, a->k7);
            InterlockedIncrement(&c.staleBy[!old || old->gen.owner != a->gen.owner ? DS_SC_OWNER : old->gen.lot != a->gen.lot ? DS_SC_LOT :
                                            old->gen.global != a->gen.global ? DS_SC_GLOBAL : DS_SC_NONE]);
        }
        return false;
    }
    a->row = row;
    // household, selected and unknown scope: only with DoorShareHousehold, or where [Experimental] HouseholdToo lifts the rule
    const bool hhOk = t_hcScope != 2 && t_hcScope != 3 && (c.household != 0 || !HhExempt(true));
    const int why = DsMayTake((uint32_t)t_dsBits, k9[2], k9[3], t_hcScope, hhOk ? 1 : 0, row->kind);
    if (why != DS_TAKE_OK) { InterlockedIncrement(&c.noTake[why]); return false; }
    if (mode != 1 || DsCheckDue(&g_dsCheckSeq, c.checkN)) return false;  // shadow, or this one is asked anyway (DsAfter compares)
    a->row = NULL;
    InterlockedIncrement(&c.hits);
    return true;
}
// worker, right before the live ask: no note yet for it
static void DsLive(const DsAsk* a) {
    if (a->on) InterlockedExchange(&g_dsNoteHash, 0);
}
// worker, after the live ask returned r (waited us for it)
static void DsAfter(const DsAsk* a, const DWORD* key, DWORD r, LONG us) {
    DoorShareCounters& c = ts3native_doorshare;
    if (!a->on) return;
    InterlockedIncrement(&c.live);
    InterlockedExchangeAdd64(&c.liveUs, us);
    const uint32_t* k9 = (const uint32_t*)key;
    const int cls = DsClassOf((uint32_t)t_dsBits, k9[2], k9[3]);
    // a change event while the ask was out (the sim thread changed the door, then served the ask): the row stopped answering
    // the key before the live answer was read, so a different answer is no mismatch (the put below is stale already)
    DsGen genNow = {};
    DsGenOf(&g_dsStamps, a->k7, &genNow);
    const bool moved = !DsGenSame(&genNow, &a->gen);
    if (a->row && !moved && DsKindFor(cls, a->row->kind)) {  // a valid row and a router it speaks for: the live answer must be "unlocked" too
        InterlockedIncrement(&c.checks);
        if (r != 2) {
            InterlockedIncrement(&c.mismatch);
            if (c.mode == 1) {
                c.mode = 0;
                Log("FAIL door share: a shared answer differed from the live ask: DoorShare off for this session");
            }
            if (InterlockedIncrement(&g_dsFailLog) <= 20)
                Log("door share check: live answer %lu, row unlocked (kind %ld, %ld ms old), class %ld, key %08lX %08lX %08lX %08lX %08lX %08lX "
                    "%04lX %04lX %02lX, scope %ld", r, (LONG)a->row->kind, DsUs(a->now - a->row->t) / 1000, (LONG)cls, key[0], key[1], key[2],
                    key[3], key[4], key[5], key[6], key[7], key[8], t_hcScope);
        }
    }
    const LONG kind = g_dsNoteKind;
    const bool noted = (uint32_t)g_dsNoteHash == DsNoteHashKey(k9) && DsNoteFor(cls, (int)kind);
    if (r == 2 && noted) {
        DsPut(&g_dsTab, a->k7, &a->gen, a->deleg, a->now, (int)kind);
        InterlockedIncrement(&c.puts[kind]);
        return;
    }
    InterlockedIncrement(r != 2 ? &c.liveLocked : cls != DS_CLS_NONE ? &c.liveNoNote : &c.liveOther);
    // a class router got an answer this table may not keep: a row that speaks for its class goes (a router in no class, or a
    // row for the other class only, says nothing about it)
    DsRow* const old = cls != DS_CLS_NONE ? DsPeek(&g_dsTab, a->k7) : NULL;
    if (old && DsKindFor(cls, old->kind)) {
        old->kind = DS_KIND_NONE;
        InterlockedIncrement(&c.drops);
    }
}

#ifndef DOORSHARE_HOST_TEST
// "name n, name n, ..." of the nonzero counts; names by index (NULL entry = "0x<index>")
static void DsBumpList(char* out, size_t cap, const LONG* n, int count, const char* (*name)(int)) {
    size_t at = 0;
    out[0] = 0;
    for (int i = 0; i < count && at + 48 < cap; i++) {
        if (!n[i]) continue;
        const char* const nm = name(i);
        const int w = nm ? _snprintf_s(out + at, cap - at, _TRUNCATE, "%s%s %ld", at ? ", " : "", nm, n[i])
                         : _snprintf_s(out + at, cap - at, _TRUNCATE, "%s0x%X %ld", at ? ", " : "", (unsigned)i, n[i]);
        if (w < 0) break;
        at += (size_t)w;
    }
    if (!at) _snprintf_s(out, cap, _TRUNCATE, "none");
}
static const char* DsIcName(int i) {
    const char* const n = i < SN_IC_N ? g_snIc[i].name : NULL;
    return n && !strncmp(n, "RouteManager_", 13) ? n + 13 : n;
}
static const char* DsWevName(int) { return NULL; }
static const char* DsSrcName(int i) {
    static const char* const k[DS_SRC_SLOTS] = {"plain bump (older ts3fixes)", "lock", "owner list", "lot handler", "hidden rooms", "greeting",
                                                "household", "fire", "fire buff", "IgnoreDoorLocks", "occult", "role", "resort", "age / gender",
                                                NULL, "arm"};
    return k[i];
}
static void DsLogCounters() {
    const DoorShareCounters& c = ts3native_doorshare;
    if (!c.ready) return;
    Log("door share: mode %ld (household %ld, check 1 in %ld, %ld ms), ts3fixes class ops %ld notes %ld; door asks %ld (before the plan %ld, "
        "in Route_PlanLocked %ld, after %ld; class ordinary %ld, no actor %ld, none %ld), different keys >= %ld with the Sim, >= %ld without; "
        "answered %ld (~%ld ms at the mean live wait), checks %ld mismatch %ld", c.mode, c.household, c.checkN, c.maxMs, c.classOps, c.noteOps,
        c.asks, c.part[0], c.part[1], c.part[2], c.classAsks[DS_CLS_ORDINARY], c.classAsks[DS_CLS_NOACTOR], c.classAsks[DS_CLS_NONE], c.keysSim,
        c.keysDoor, c.hits, c.live ? (LONG)(c.liveUs / c.live * c.hits / 1000) : 0, c.checks, c.mismatch);
    Log("door share misses: no row %ld, a stamp moved %ld (owner %ld, lot %ld, global %ld, delegate %ld), older than DoorShareMs %ld, doorgen "
        "not armed %ld; a valid row not used: no class %ld, another Sim %ld, household / unknown scope %ld, player command %ld, row of the "
        "other class %ld; live asks %ld (%ld ms): rows written %ld door + %ld inert + %ld no-actor, unlocked without a note %ld, unlocked for "
        "a router in no class %ld, not unlocked %ld, rows dropped %ld", c.miss[DS_NONE], c.miss[DS_OTHERGEN], c.staleBy[DS_SC_OWNER],
        c.staleBy[DS_SC_LOT], c.staleBy[DS_SC_GLOBAL], c.staleBy[DS_SC_NONE], c.miss[DS_OLD], c.unarmed, c.noTake[DS_NO_CLASS], c.noTake[DS_NO_SIM],
        c.noTake[DS_NO_SCOPE], c.noTake[DS_NO_COMMAND], c.noTake[DS_NO_KIND], c.live, (LONG)(c.liveUs / 1000), c.puts[DS_KIND_DOOR],
        c.puts[DS_KIND_INERT], c.puts[DS_KIND_NOACTOR], c.liveNoNote, c.liveOther, c.liveLocked, c.drops);
    char ic[700] = {}, wev[200] = {}, src[500] = {};
    DsBumpList(ic, sizeof(ic), c.bumpIc, DS_IC_SLOTS, DsIcName);
    DsBumpList(wev, sizeof(wev), c.bumpWev, DS_WEV_SLOTS, DsWevName);
    DsBumpList(src, sizeof(src), c.bumpSrc, DS_SRC_SLOTS, DsSrcName);
    Log("door share changes: cleared nothing %ld, one owner %ld, one lot %ld, everything %ld; world events: %s; ts3fixes doorgen: %s",
        c.bumpScope[DS_SC_NONE], c.bumpScope[DS_SC_OWNER], c.bumpScope[DS_SC_LOT], c.bumpScope[DS_SC_GLOBAL], wev, src);
    Log("door share changes by portal IC: %s", ic);
    Log("door share no class: asks of a job with no op 40 %ld, no description %ld, horse %ld, stray %ld, tomb mummy %ld, proprietor %ld, "
        "service %ld, IgnoreDoorLocks %ld", c.whyAsks[DS_WHY_NOOP], c.whyAsks[DS_WHY_NODESC], c.whyAsks[DS_WHY_HORSE], c.whyAsks[DS_WHY_STRAY],
        c.whyAsks[DS_WHY_MUMMY], c.whyAsks[DS_WHY_PROPRIETOR], c.whyAsks[DS_WHY_SERVICE], c.whyAsks[DS_WHY_IGNORELOCKS]);
}
#endif

// ApplyHostCalls, once the door stub and the route snapshot's hooks are in (stubIn, genOk)
static void ApplyDoorShare(const wchar_t* ini, bool stubIn, bool genOk) {
    DoorShareCounters& c = ts3native_doorshare;
    LONG mode = (LONG)GetPrivateProfileIntW(L"RouteSnap", L"DoorShare", 0, ini);
    if (mode < 0 || mode > 2) { Log("SKIP door share: DoorShare=%ld is not 0, 1 or 2", mode); return; }
    if (!mode) return;
    if (!stubIn || !genOk) {
        Log("SKIP door share: DoorShare=%ld needs the door stub and the route snapshot's World_FireEvent hook and IC slots "
            "([RouteSnap] DoorMemoX present, even 0)", mode);
        return;
    }
    c.maxMs = Clamp((LONG)GetPrivateProfileIntW(L"RouteSnap", L"DoorShareMs", 60000, ini), 1, 3600000);
    c.checkN = Clamp((LONG)GetPrivateProfileIntW(L"RouteSnap", L"DoorShareCheck", 8, ini), 1, 1000);
    c.household = GetPrivateProfileIntW(L"RouteSnap", L"DoorShareHousehold", 0, ini) == 1 ? 1 : 0;
    g_dsTicks = g_qpf.QuadPart * c.maxMs / 1000;
    c.ready = 1;
    c.mode = mode;
    Log("OK   door share: DoorShare=%ld (%s), rows confirmed within %ld ms, 1 in %ld asked anyway, household %ld; stamps per portal "
        "owner, per lot and global; acts once ts3fixes doorgen is armed and ts3fixes doorshare sends classes (ops 40 / 41)", mode,
        mode == 1 ? "answers shared by class" : "shadow: ask and compare", c.maxMs, c.checkN, c.household);
}
