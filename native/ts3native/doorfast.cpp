// [DoorFast] native portal-answer fast path (docs/548, counters v916 df*). Included by ts3native.cpp after cellmemo.cpp and
// before hostcalls.cpp: the door stub at 0x64F4D3 (HcDoorEnter) calls it, and ApplyHostCalls starts it with the stub.
//
// The route worker's door asks (World_FirePortalLockedQuery 0x791C00, proxied to the sim thread: Lot.OnPortalPathPlan) are
// predicted from the door mirror ts3fixes pushes at each route START (snap.cpp door channel, DoorMirror.cs): per lot the door
// records, the rule-b portal owner ids, typed owners, hidden rooms and lot flags, per route actor its flags (snap.cpp SDA_*). No
// game memory is read per ask.
//  rule a, plain door -> unlocked (2): the owner has a door record whose type does not override IsAllowedThrough and is not a barn
//    or single-gender door, lock Anybody (3), not one-way (36863); initialLocked 0; a human actor; no hidden room on the lot.
//  rule b, a portal type with no rule of its own in Sim.OnPortalPathPlan -> initialLocked ? locked (1) : unlocked (2): the owner
//    is one of the lot's rule-b ids (Stairs, StaircaseSpiral, Pool, PoolLadder, BuildableFountain, ElevatorInterior, Platform,
//    VelvetRopes, Subway and their subclasses).
//  both: only EA's Sim.OnPortalPathPlan listens on the lot (no PrivacySituation or mod handler), the actor is not a tomb mummy and
//    has no Proprietor role.
// Anything else (no valid lot table of the current epoch, no actor record younger than ActorMs, an unknown owner): no prediction,
// the ask goes to the sim as today. Mode 1 (shadow): predict, ask anyway, compare the exact class. Mode 2: the same, and while
// dfAnswer (+0x62B0, --ab doorfast) is 1 a prediction for an actor outside household scope is returned without the round trip;
// every 16th of those is asked anyway (self-check). Any mismatch in Mode 2 ends answering for the session (dfFail). Household-scope
// actors (and actors ts3fixes could not classify) are always asked: household Sims untouched.
// A door (any CommonDoor of the lot's records) is answered natively only when predicted unlocked: Sim.OnPortalPathPlan adds every
// locked door to the actor's LockedDoorsDuringPlan (cleared per plan, read by the route-fail "door locked" reaction), so a door
// predicted locked is asked (dfDoorLocked). Rule-b portals are no doors: a locked answer has no side effect there. A skipped ask
// otherwise loses only the handler's event-args allocation (docs/159 §4.1, docs/105 §2); the caller's code after the call runs.
// Coverage (v919, docs/548 "Coverage"; no new answer rule): lots asked about with no valid table go into a small ring that
// ts3fixes reads at its next route START and pushes (snap.cpp SDM_MISS_*); the hidden-rooms test is per room pair, against the
// lot's pushed hidden-room ids (EA: CommonDoor.IsAllowedThrough tests IsRoomHidden(from) || IsRoomHidden(to)); portal owners of
// the types with no record before (ElevatorDoors, FirefighterPole, IBoxStall, IHorseJump, DivingBoard, Ladder, TubeElevator,
// WaterSlide, Houseboat, HouseboatJig) have typed records, counted per type (dfType); a router that is no Sim gets a record
// too. Where EA's answer is the initial class (a null Actor: Sim.OnPortalPathPlan returns at once; a typed owner with no rule
// of its own), a shadow guess "initialLocked ? locked : unlocked" is compared with the live answer (dfGuessOk / dfGuessBad) and
// never answered.
// Rules c-g (v920, docs/548 §7; the managed shadow dp12 / dp13 found them exact: 0 mismatches), counted and answered like a / b:
//  rule c, ElevatorDoors (typed kind 0, exact type) -> initialLocked ? locked : unlocked: a human actor, not ToddlerOrBelow, the
//    lot without virtual residential slots (ElevatorDoors.IsAllowedThrough is None then; apartment lots: no prediction).
//  rule d, single-gender door (IsAllowedThrough declared on DoorSingleGender; it does not call CommonDoor's, so lock, one-way and
//    hidden rooms do not count) -> unlocked: a human actor of the door's gender, initialLocked 0, not a resort lot. Never locked.
//  rule e, Ladder (typed kind 5, exact type) -> initialLocked ? locked : avoids ladders (Elder or a Ladder.kTraitsThatAvoidLadders
//    trait) ? discouraged (3) : unlocked: EA's class is locked before discouraged.
//  rule f, DivingBoard (typed kind 4, exact type) -> initialLocked, ToddlerOrBelow or IsVisuallyPregnant ? locked : unlocked.
//  c-f also need the one handler, no tomb mummy and no Proprietor role; rule e and f take any species, as EA's steps do.
//  rule g, a router that is no Sim (a record with SDA_NOT_SIM, or router id 0) -> initialLocked ? locked : unlocked for any
//    portal: EA passes a null Actor and Sim.OnPortalPathPlan does nothing (its `args.Actor != null` test). Only the one handler is needed. A door
//    predicted locked by rule g may be answered: with no Sim nothing goes into LockedDoorsDuringPlan.

enum { DF_NO_LOT, DF_NO_ACTOR, DF_NO_HANDLERS, DF_NO_ROLE, DF_NO_OWNER, DF_NO_DOOR, DF_NO_INITIAL, DF_NO_ANIMAL, DF_NO_HIDDEN, DF_NO_N,
       DF_NO_NOTSIM = DF_NO_N, DF_NO_RULE };  // the v919 reasons count in dfNo2 (DF_NO_NOTSIM unused since rule g, v920)
enum { DF_RULE_A = 1, DF_RULE_B, DF_RULE_C, DF_RULE_D, DF_RULE_E, DF_RULE_F, DF_RULE_G };
// DoorRecord kind bits (DoorMirrorCore.cs); v920: IsAllowedThrough declared on DoorSingleGender, the door's mGender male / female
static const DWORD kDfBarnDoor = 2, kDfSingleGender = 32, kDfOverridden = 64, kDfGenderOwn = 128, kDfDoorMale = 256, kDfDoorFemale = 512;
static_assert(kDfDoorMale == SDA_MALE && kDfDoorFemale == SDA_FEMALE, "door and actor gender bits are compared directly");
static const DWORD kDfTypeElevator = 0, kDfTypeDiving = 4, kDfTypeLadder = 5;  // typed kinds (DoorMirror.cs ReadLotInfo order)
static const DWORD kDfAnybody = 3, kDfNoRoom = 36863;
static const SpActor kDfNullActor = {0, SDA_NOT_SIM | SDA_HOUSEHOLD, 0};  // router id 0: no object, EA's Actor is null; scope unknown
static LONG g_dfLogMax = 20;        // [DoorFast] LogMismatch
static volatile LONG g_dfSelfN, g_dfMisLog;
// ans 0 = no prediction (why, sub = its split: dfLotNo / dfActNo / dfHidN / dfType index), else the class; rule DF_RULE_A .. G;
// door = the owner is a CommonDoor of the lot's door records; guess = the shadow guess class (0 none), gk its dfGuess* index
struct DfPred { DWORD ans, actor, kind; int why, rule; bool door; DWORD guess; int sub, gk; };
static void DfGuessInitial(const DWORD* key, DfPred& p, int gk) { p.guess = key[8] ? 1 : 2, p.gk = gk; }

// Rule dispatch, one function per portal family (lock held, lot valid, actor known, one handler, no mummy / proprietor). To add a
// rule: a branch with the next rule number in the family's function. A door rule may predict locked (1): DfEnter never answers
// that natively (Sim.OnPortalPathPlan adds every locked door to LockedDoorsDuringPlan, the route-fail "door locked" reaction).
static void DfGenderRules(const SpDoorLot* l, const SpDoor* d, const SpActor* a, const DWORD* key, DfPred& p) {
    const DWORD g = d->kind & (kDfDoorMale | kDfDoorFemale);
    if (key[8]) p.why = DF_NO_INITIAL;
    else if (!(a->flags & SDA_HUMAN)) p.why = DF_NO_ANIMAL;
    else if ((g != kDfDoorMale && g != kDfDoorFemale) || g != (a->flags & (SDA_MALE | SDA_FEMALE)) || (l->flags & SDLF_RESORT)) p.why = DF_NO_DOOR;
    else p.ans = 2, p.rule = DF_RULE_D;  // rule d: DoorSingleGender.IsAllowedThrough is None for a human of its gender
}
static void DfDoorRules(const SpDoorLot* l, const SpDoor* d, const SpActor* a, const DWORD* key, DfPred& p) {
    p.door = true, p.kind = d->kind;
    if (d->kind & kDfGenderOwn) DfGenderRules(l, d, a, key, p);
    else if ((d->kind & (kDfOverridden | kDfBarnDoor | kDfSingleGender)) || d->lock != kDfAnybody || d->oneWay != kDfNoRoom) p.why = DF_NO_DOOR;
    else if (key[8]) p.why = DF_NO_INITIAL;
    else if (!(a->flags & SDA_HUMAN)) p.why = DF_NO_ANIMAL;
    else if (l->flags & SDLF_HIDDEN_ROOMS) p.why = DF_NO_HIDDEN, p.sub = 1;  // the lot's hidden-room list unknown
    else if (SpRoomHidden(l, key[6]) || SpRoomHidden(l, key[7])) p.why = DF_NO_HIDDEN, p.sub = 0;
    else p.ans = 2, p.rule = DF_RULE_A;  // rule a: plain door, unlocked
}
static void DfPortalRules(const DWORD* key, DfPred& p) {  // the owner is a rule-b id (never a CommonDoor: DoorMirror.cs)
    p.ans = key[8] ? 1 : 2, p.rule = DF_RULE_B;  // rule b: no rule of its own, the native L0 stands
}
// typed owners: rules c, e, f (header); the rest no rule yet, counted per type, with a shadow guess for the kinds with no step of their own
static void DfTypedRules(const SpDoorLot* l, const SpTyped* t, const SpActor* a, const DWORD* key, DfPred& p) {
    const DWORD f = a->flags;
    p.kind = t->kind;
    if (t->kind == kDfTypeElevator && (f & SDA_HUMAN) && !(f & SDA_TODDLER) && !(l->flags & SDLF_APARTMENT)) p.ans = key[8] ? 1 : 2, p.rule = DF_RULE_C;
    else if (t->kind == kDfTypeLadder) p.ans = key[8] ? 1 : (f & SDA_AVOID_LADDER) ? 3 : 2, p.rule = DF_RULE_E;
    else if (t->kind == kDfTypeDiving) p.ans = (key[8] || (f & (SDA_TODDLER | SDA_PREGNANT))) ? 1 : 2, p.rule = DF_RULE_F;
    else {
        p.why = DF_NO_RULE, p.sub = (int)t->kind;
        if (t->kind >= kSpTypePlain) DfGuessInitial(key, p, 1);
    }
}

// the class EA would answer for the ask (key = P4DoorKey) from the mirror tables; any thread
static DfPred DfPredict(const DWORD* key, DWORD now) {
    DfPred p = {0, 0, 0, DF_NO_LOT, 0, false};
    const u64 lot = (u64)key[1] << 32 | key[0], sim = (u64)key[3] << 32 | key[2], owner = (u64)key[5] << 32 | key[4];
    AcquireSRWLockShared(&g_spDoorLock);
    const SpDoorLot* l = SpDoorLotFor(lot, false);
    const bool lotOk = l && l->valid && l->epoch == g_spEpoch;
    int old = 0;
    const SpActor* a = !lotOk ? NULL : sim ? SpActorFind(sim, now, g_dfActorMs, &old) : &kDfNullActor;
    if (!lotOk) p.why = DF_NO_LOT, p.sub = !lot ? 0 : !l ? 1 : !l->valid ? 2 : 3;
    else if (!a) p.why = DF_NO_ACTOR, p.sub = old;
    else {
        p.actor = a->flags;
        const SpDoor* d = NULL;
        bool b = false;
        const SpTyped* t = NULL;
        for (int i = 0; i < l->n && !d; i++) if (l->d[i].obj == owner) d = &l->d[i];
        for (int i = 0; i < l->nB && !d && !b; i++) b = l->b[i] == owner;
        for (int i = 0; i < l->nT && !d && !b && !t; i++) if (l->t[i].id == owner) t = &l->t[i];
        if (!(l->flags & SDLF_ONE_HANDLER)) p.why = DF_NO_HANDLERS, p.door = d != NULL;
        else if (a->flags & SDA_NOT_SIM) p.ans = key[8] ? 1 : 2, p.rule = DF_RULE_G, p.door = d != NULL;  // null Actor: the handler does nothing
        else if ((a->flags & (SDA_NOT_MUMMY | SDA_NO_PROPRIETOR)) != (SDA_NOT_MUMMY | SDA_NO_PROPRIETOR)) p.why = DF_NO_ROLE, p.door = d != NULL;
        else if (d) DfDoorRules(l, d, a, key, p);
        else if (b) DfPortalRules(key, p);
        else if (t) DfTypedRules(l, t, a, key, p);
        else p.why = DF_NO_OWNER;
    }
    ReleaseSRWLockShared(&g_spDoorLock);
    return p;
}

// HcDoorEnter, worker inside a job (dfMode != 0): 1 = answered with p->ans, 0 = ask (then DfCompare at the live answer)
static int DfDecide(const DfPred* p);
static int DfEnter(const DWORD* key, DfPred* p) {
    InterlockedIncrement(&ts3native_counters.dfAsks);
    const DWORD now = GetTickCount();
    *p = DfPredict(key, now);
    if (!p->ans && p->why == DF_NO_LOT && p->sub) SpMissNote((u64)key[1] << 32 | key[0], now);  // sub 0: lot id 0, nothing to push
    return DfDecide(p);
}
static int DfDecide(const DfPred* p) {
    Ts3nCounters& k = ts3native_counters;
    if (!p->ans) {
        InterlockedIncrement(p->why < DF_NO_N ? &k.dfNo[p->why] : &k.dfNo2[p->why - DF_NO_N]);
        volatile LONG* sub = p->why == DF_NO_LOT ? k.dfLotNo : p->why == DF_NO_ACTOR ? k.dfActNo : p->why == DF_NO_HIDDEN ? k.dfHidN
                           : p->why == DF_NO_RULE ? k.dfType : NULL;
        if (sub) InterlockedIncrement(&sub[p->sub]);
        return 0;
    }
    volatile LONG* const pred[] = {&k.dfPredA, &k.dfPredB, &k.dfPredC, &k.dfPredD, &k.dfPredE, &k.dfPredF, &k.dfPredG};
    InterlockedIncrement(pred[p->rule - 1]);
    // rule g has no Sim: nothing goes into LockedDoorsDuringPlan, so its locked doors may be answered
    const bool house = (p->actor & SDA_HOUSEHOLD) != 0, doorLocked = p->door && p->ans != 2 && p->rule != DF_RULE_G;
    if (house) InterlockedIncrement(&k.dfHouse);
    if (doorLocked) InterlockedIncrement(&k.dfDoorLocked);  // a door predicted locked / discouraged is always asked (header)
    if (!house && !doorLocked && k.dfMode == 2 && k.dfAnswer && !k.dfFail) {
        if (InterlockedIncrement(&g_dfSelfN) & 15) { InterlockedIncrement(&k.dfAnswered); return 1; }
        InterlockedIncrement(&k.dfSelf);
    }
    InterlockedIncrement(&k.dfSent);
    return 0;
}

// the live answer r of an ask DfEnter predicted (p.ans != 0) and did not answer, or one with a shadow guess (never answered)
static void DfCompare(const DWORD* key, const DfPred& p, DWORD r) {
    Ts3nCounters& k = ts3native_counters;
    if (!p.ans) {
        if (!p.guess) return;
        InterlockedIncrement(r == p.guess ? &k.dfGuessOk[p.gk] : &k.dfGuessBad[p.gk]);
        if (r != p.guess && InterlockedIncrement(&g_dfMisLog) <= g_dfLogMax)
            Log("door fast guess differed: lot %08lX%08lX owner %08lX%08lX %s kind %lu initialLocked %lu guessed %lu real %lu (actor %08lX%08lX)",
                key[1], key[0], key[5], key[4], p.gk ? "typed owner" : "router no Sim", p.kind, key[8], p.guess, r, key[3], key[2]);
        return;
    }
    if (r == p.ans) { InterlockedIncrement(&k.dfMatch); return; }
    volatile LONG* const mis[] = {&k.dfMisA, &k.dfMisB, &k.dfMisC, &k.dfMisD, &k.dfMisE, &k.dfMisF, &k.dfMisG};
    InterlockedIncrement(mis[p.rule - 1]);
    if (k.dfMode == 2 && !InterlockedExchange(&k.dfFail, 1))
        Log("FAIL door fast: a prediction differed from the live answer in Mode=2: no more answers this session");
    if (InterlockedIncrement(&g_dfMisLog) <= g_dfLogMax)
        Log("door fast mismatch: lot %08lX%08lX owner %08lX%08lX rule %c kind 0x%lX initialLocked %lu predicted %lu real %lu "
            "(actor %08lX%08lX flags 0x%lX, rooms %lu -> %lu)", key[1], key[0], key[5], key[4], 'a' + p.rule - 1, p.kind,
            key[8], p.ans, r, key[3], key[2], p.actor, key[6], key[7]);
}

// [DoorFast] Mode: 0 off, 1 shadow, 2 answer-capable (ApplyHostCalls installs the door stub for it)
static LONG DfIniMode(const wchar_t* ini) {
    LONG m = (LONG)GetPrivateProfileIntW(L"DoorFast", L"Mode", 0, ini);
    if (m < 0 || m > 2) { Log("SKIP door fast: Mode=%ld is not 0, 1 or 2", m); m = 0; }
    return m;
}
static void DfStart(const wchar_t* ini, LONG mode, bool stub) {
    Ts3nCounters& k = ts3native_counters;
    if (!mode) return;
    if (!stub) { Log("SKIP door fast: the door stub is not in"); return; }
    // the tables are filled only with [RouteSnap] Enabled=1, and invalidated only through its World_FireEvent hook and IC slots
    if (!g_spOn || (k.snReady & 6) != 6) {
        Log("SKIP door fast: Mode=%ld needs [RouteSnap] Enabled=1 with its World_FireEvent hook and IC slots (snReady 0x%lX)", mode, k.snReady);
        return;
    }
    g_dfActorMs = (DWORD)Clamp((int)GetPrivateProfileIntW(L"DoorFast", L"ActorMs", 10000, ini), 100, 600000);
    g_dfLogMax = Clamp((int)GetPrivateProfileIntW(L"DoorFast", L"LogMismatch", 20, ini), 0, 1000);
    k.dfAnswer = mode == 2;
    k.dfMode = mode;
    Log("OK   door fast: Mode=%ld (%s), actor records %lu ms, needs ts3fixes doormirror (switch dfAnswer +0x62B0, --ab doorfast)", mode,
        mode == 2 ? "answer non-household asks, compare the rest" : "shadow: predict, ask and compare", g_dfActorMs);
}
