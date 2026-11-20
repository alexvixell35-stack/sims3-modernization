// docs/674: [RouteSnap] DoorShare. Included by ts3native.cpp right before hostcalls.cpp; the door stub (HcDoorEnter) calls
// DsBefore / DsAfter around the route worker's door ask at 0x64F4D3 (World event 59, "may this Sim pass this portal").
//
// Why: DoorMemoX keys a row by the asking Sim and keeps it 250 ms, so the first ask of every Sim about every door is a host
// call (s159: 15,942 of 18,081 misses had no row). Most of those answers do not depend on the Sim at all. For a Sim of the
// ordinary class (a human that is no tomb mummy, has no proprietor role, no service, no IgnoreDoorLocks flag and is no stray:
// ts3fixes DoorShare.cs reads that at each plan and sends it with the route, UserDiag op 40) the game's handler
// (Sim.OnPortalPathPlan, CommonDoor.IsAllowedThrough) answers "unlocked" for
//   - a plain door (a Door whose IsAllowedThrough is CommonDoor's own) with lock type Anybody: from the lot's hidden rooms and
//     the door's one-way room only, and
//   - a portal object no rule of the handler names (stairs, arches, gates that are no Door, ...): from initialLocked only,
// as long as the lot's handler list is the game's single handler and event 59 has the game's single callback. ts3fixes checks
// all of that for the ask it has just answered and says so with a note (op 41: a hash of the ask, the kind). A live "unlocked"
// answer with a matching note becomes a row without the Sim in its key; any later ask for that key by an ordinary-class Sim is
// answered 2 from the row while
//   - the door generation is unchanged (DoorMemoX's: portal ICs, lot world events, and ts3fixes doorgen's bump after every
//     lock, owner list, one-way room, lot handler, fire, ... setter; a world load bumps it too),
//   - the event 59 delegate slot is unchanged, and
//   - a live ask confirmed the row within DoorShareMs (a safety net for an input nobody hooked, not part of the argument).
// Only "unlocked" is kept: a "locked" answer has a managed side effect per plan (LockedDoorsDuringPlan.Add, docs/159 §4.1).
// Every DoorShareCheck-th answer is asked anyway and compared; one difference turns DoorShare off for the session
// (`FAIL door share`). Mode 2 (shadow) answers nothing and compares every valid row.
// Who: jobs of scope 2 / 3 (unseen, seen NPC). Household, selected and unknown scope ask live unless DoorShareHousehold=1
// (or [Experimental] HouseholdToo); a route ts3fixes marked as the selected Sim's own command always asks live.
// Needs DoorMemoX's door stub, the route snapshot's generation hooks, ts3fixes doorgen (armed) and ts3fixes doorshare.
// Also counts, in every mode but 0: door asks by part of the job, different keys with and without the Sim, live-ask wait.
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
    LONG keysSim, keysDoor;   // different (portal, rooms, Sim) keys / (portal, rooms) keys, lower bounds
    LONG hits;                // answered from a row
    LONG checks, mismatch;    // valid rows asked anyway / of them a different live answer (must stay 0)
    LONG miss[4];             // by DsFind: no row, (valid: unused), other generation, older than DoorShareMs
    LONG noTake[DS_TAKE_WHYS];// valid row not used: (unused), no class, another Sim, scope, player command
    LONG classAsks;           // asks of an ordinary-class job
    LONG puts[DS_KINDS];      // rows written: (unused), plain door, inert portal
    LONG liveNoNote;          // live "unlocked" answers to an ordinary-class Sim without a matching note (not shareable)
    LONG liveOther;           // live "unlocked" answers to a Sim outside the class
    LONG liveLocked;          // live answers that were not "unlocked"
    LONG drops;               // rows removed by such an answer
    LONG live;                // live asks (host calls) made through us
    LONG unarmed;             // asks while ts3fixes doorgen was not armed (nothing answered or kept)
    volatile LONGLONG liveUs; // their wait, us
};
extern "C" __declspec(dllexport) DoorShareCounters ts3native_doorshare = {};

static DsTable g_dsTab;              // worker only
static DsSeen g_dsSeenSim, g_dsSeenDoor;
static LONGLONG g_dsTicks;           // DoorShareMs in QPC ticks
static long g_dsCheckSeq;            // worker only
static volatile LONG g_dsNoteHash, g_dsNoteKind;  // sim thread writes inside the served call; the worker reads after it
static volatile LONG g_dsFailLog;

struct DsAsk {
    uint32_t k7[7];
    LONG gen;
    DWORD deleg;
    LONGLONG now;
    const DsRow* row;  // a valid row that was not used as the answer (check, shadow or not allowed)
    bool on;
};

// UserDiag op 41 (sim thread, inside the managed handler of the ask being served): the answer just given is shareable
static u8 DsNoteOp(DWORD lo, DWORD hi) {
    InterlockedIncrement(&ts3native_doorshare.noteOps);
    InterlockedExchange(&g_dsNoteKind, (LONG)(hi & 0xFFFF));
    InterlockedExchange(&g_dsNoteHash, (LONG)lo);
    return (u8)(ts3native_doorshare.mode != 0);
}
// UserDiag op 40 (sim thread, before a route's START): the route's Sim class; 1 = DoorShare is on (ts3fixes may skip notes otherwise)
static u8 DsClassOp(DWORD lo, DWORD hi) {
    InterlockedIncrement(&ts3native_doorshare.classOps);
    DsClassSet(lo, (LONG)(hi & 0xFFFF));
    return (u8)(ts3native_doorshare.mode != 0);
}

static LONG DsUs(LONGLONG ticks) { return g_qpf.QuadPart ? (LONG)(ticks * 1000000 / g_qpf.QuadPart) : 0; }

// worker, in a job, before the ask goes out. true = answered "unlocked" from a row (the caller returns 2, no host call).
static bool DsBefore(const DWORD* key, LONG gen, DWORD deleg, LONGLONG now, DsAsk* a) {
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
    if (!ts3native_doorgen.armed) { InterlockedIncrement(&c.unarmed); return false; }  // the generation lacks the managed inputs
    a->on = true, a->gen = gen, a->deleg = deleg, a->now = now;
    const DsRow* row = NULL;
    const int st = DsFind(&g_dsTab, a->k7, gen, deleg, now, g_dsTicks, &row);
    // household, selected and unknown scope: only with DoorShareHousehold, or where [Experimental] HouseholdToo lifts the rule
    const bool hhOk = t_hcScope != 2 && t_hcScope != 3 && (c.household != 0 || !HhExempt(true));
    const int why = DsMayTake((uint32_t)t_dsBits, k9[2], k9[3], t_hcScope, hhOk ? 1 : 0);
    if (why != DS_NO_CLASS && why != DS_NO_SIM) InterlockedIncrement(&c.classAsks);
    if (st != DS_VALID) { InterlockedIncrement(&c.miss[st]); return false; }
    a->row = row;
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
    const bool classAsk = (t_dsBits & DS_C_ORDINARY) && ((uint32_t)t_dsBits >> DS_C_SIMSHIFT) == DsSimFold(k9[2], k9[3]);
    if (a->row && classAsk) {  // a valid row and a Sim it speaks for: the live answer must be "unlocked" too
        InterlockedIncrement(&c.checks);
        if (r != 2) {
            InterlockedIncrement(&c.mismatch);
            if (c.mode == 1) {
                c.mode = 0;
                Log("FAIL door share: a shared answer differed from the live ask: DoorShare off for this session");
            }
            if (InterlockedIncrement(&g_dsFailLog) <= 20)
                Log("door share check: live answer %lu, row unlocked (kind %ld, %ld ms old), key %08lX %08lX %08lX %08lX %08lX %08lX %04lX %04lX "
                    "%02lX, scope %ld", r, (LONG)a->row->kind, DsUs(a->now - a->row->t) / 1000, key[0], key[1], key[2], key[3], key[4],
                    key[5], key[6], key[7], key[8], t_hcScope);
        }
    }
    const LONG kind = g_dsNoteKind;
    const bool noted = (uint32_t)g_dsNoteHash == DsNoteHashKey(k9) && kind > DS_KIND_NONE && kind < DS_KINDS;
    if (r == 2 && noted && classAsk) {
        DsPut(&g_dsTab, a->k7, a->gen, a->deleg, a->now, (int)kind);
        InterlockedIncrement(&c.puts[kind]);
        return;
    }
    InterlockedIncrement(r != 2 ? &c.liveLocked : classAsk ? &c.liveNoNote : &c.liveOther);
    // an ordinary-class Sim got an answer this table may not keep: the key's row goes (another Sim's answer says nothing about it)
    if (classAsk && DsDrop(&g_dsTab, a->k7)) InterlockedIncrement(&c.drops);
}

static void DsLogCounters() {
    const DoorShareCounters& c = ts3native_doorshare;
    if (!c.ready) return;
    Log("door share: mode %ld (household %ld, check 1 in %ld, %ld ms), ts3fixes class ops %ld notes %ld; door asks %ld (before the plan %ld, "
        "in Route_PlanLocked %ld, after %ld; of an ordinary-class job %ld), different keys >= %ld with the Sim, >= %ld without; answered %ld "
        "(~%ld ms at the mean live wait), checks %ld mismatch %ld", c.mode, c.household, c.checkN, c.maxMs, c.classOps, c.noteOps, c.asks, c.part[0], c.part[1],
        c.part[2], c.classAsks, c.keysSim, c.keysDoor, c.hits, c.live ? (LONG)(c.liveUs / c.live * c.hits / 1000) : 0, c.checks, c.mismatch);
    Log("door share misses: no row %ld, other generation %ld, older than DoorShareMs %ld, doorgen not armed %ld; a valid row not used: no "
        "class %ld, another Sim %ld, household / unknown scope %ld, player command %ld; live asks %ld (%ld ms): rows written %ld door + %ld "
        "inert, unlocked without a note %ld, unlocked for a Sim outside the class %ld, not unlocked %ld, rows dropped %ld", c.miss[DS_NONE], c.miss[DS_OTHERGEN], c.miss[DS_OLD],
        c.unarmed, c.noTake[DS_NO_CLASS], c.noTake[DS_NO_SIM], c.noTake[DS_NO_SCOPE], c.noTake[DS_NO_COMMAND], c.live,
        (LONG)(c.liveUs / 1000), c.puts[DS_KIND_DOOR], c.puts[DS_KIND_INERT], c.liveNoNote, c.liveOther, c.liveLocked, c.drops);
}

// ApplyHostCalls, once the door stub and DoorMemoX's generation hooks are in (stubIn, genOk)
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
    c.maxMs = Clamp((LONG)GetPrivateProfileIntW(L"RouteSnap", L"DoorShareMs", 5000, ini), 1, 60000);
    c.checkN = Clamp((LONG)GetPrivateProfileIntW(L"RouteSnap", L"DoorShareCheck", 8, ini), 1, 1000);
    c.household = GetPrivateProfileIntW(L"RouteSnap", L"DoorShareHousehold", 0, ini) == 1 ? 1 : 0;
    g_dsTicks = g_qpf.QuadPart * c.maxMs / 1000;
    c.ready = 1;
    c.mode = mode;
    Log("OK   door share: DoorShare=%ld (%s), rows confirmed within %ld ms, 1 in %ld asked anyway, household %ld; acts once ts3fixes "
        "doorgen is armed and ts3fixes doorshare sends classes (ops 40 / 41)", mode, mode == 1 ? "answers shared by ordinary-class Sims" :
        "shadow: ask and compare", c.maxMs, c.checkN, c.household);
}
