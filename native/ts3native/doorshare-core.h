// docs/674: the pure part of [RouteSnap] DoorShare (doorshare.cpp): the class-shared door answer table, its stamps (what
// each change event clears), the hashes shared with ts3fixes (DoorShareCore.cs), who may take a shared answer, and the ask
// census. No Windows, no game, no clock: doorshare-host-test.cpp and the selftest run it as is. Times are plain tick counts
// handed in by the caller.
#ifndef DOORSHARE_CORE_H
#define DOORSHARE_CORE_H

#include <stdint.h>

enum { DS_SETS = 256, DS_WAYS = 4, DS_ROWS = DS_SETS * DS_WAYS };
enum { DS_NONE, DS_VALID, DS_OTHERGEN, DS_OLD, DS_FINDS };  // DsFind
// ts3fixes' note: a plain door open to anybody, a portal no rule reads (both: every ordinary Sim and every router that is
// no Sim), any portal asked by a router that is no Sim (only such routers)
enum { DS_KIND_NONE, DS_KIND_DOOR, DS_KIND_INERT, DS_KIND_NOACTOR, DS_KINDS };
// op 40's 16 bits: ordinary Sim, player command, the router is no Sim, 13 bits of the router's id. Neither class bit set:
// bits 3-15 hold the reason (DS_WHY_*)
enum { DS_C_ORDINARY = 1, DS_C_COMMAND = 2, DS_C_NOACTOR = 4, DS_C_SIMSHIFT = 3 };
enum { DS_CLS_NONE, DS_CLS_ORDINARY, DS_CLS_NOACTOR };
enum { DS_WHY_NOOP, DS_WHY_NODESC, DS_WHY_HORSE, DS_WHY_STRAY, DS_WHY_MUMMY, DS_WHY_PROPRIETOR, DS_WHY_SERVICE, DS_WHY_IGNORELOCKS, DS_WHYS };
// why an ask was not answered from a valid row (DsMayTake; 0 = it may be)
enum { DS_TAKE_OK, DS_NO_CLASS, DS_NO_SIM, DS_NO_SCOPE, DS_NO_COMMAND, DS_NO_KIND, DS_TAKE_WHYS };

// What a change event clears. A row's answer is a function of its key and of: the portal object's own script state (lock
// type, one-way room, its type), the lot's hidden rooms and handler list, and the event 59 callback (docs/674 §9).
enum { DS_SC_NONE, DS_SC_OWNER, DS_SC_LOT, DS_SC_GLOBAL, DS_SCOPES };
enum { DS_LOTS = 256, DS_OWNERS = 2048 };
struct DsStamps { int32_t global; int32_t lot[DS_LOTS]; int32_t owner[DS_OWNERS]; };
struct DsGen { int32_t global, lot, owner; };
// ts3fixes doorgen's sources (op 36 hi; DoorShareCore.kSrc*)
enum { DS_SRC_UNKNOWN, DS_SRC_LOCK, DS_SRC_OWNERLIST, DS_SRC_LOTHANDLER, DS_SRC_HIDDEN, DS_SRC_GREET, DS_SRC_HOUSEHOLD, DS_SRC_FIRE,
       DS_SRC_FIREBUFF, DS_SRC_IGNORELOCKS, DS_SRC_OCCULT, DS_SRC_ROLE, DS_SRC_RESORT, DS_SRC_DESC, DS_SRCS, DS_SRC_ARM = 15, DS_SRC_SLOTS = 16 };
// op 36 hi = DS_SRC_OP_BASE + source (hi 1 / 2 are DoorGen.cs' lot hold / free, hi 0 an older package's arm or plain bump)
enum { DS_SRC_OP_BASE = 16 };
enum { DS_IC_SLOTS = 40, DS_WEV_SLOTS = 17 };

// a row answers "unlocked" (2) for every router of its class: the key has no Sim in it
struct DsRow {
    uint32_t key[7];  // lot id, portal owner id (two words each), room from, room to, initialLocked
    uint32_t deleg;   // the event 59 delegate slot when the row was written
    DsGen gen;        // the stamps of its lot and owner, and the global one, when the row was written
    int32_t kind;     // DS_KIND_* (0 = empty)
    int64_t t;        // when a live ask last confirmed it
};
struct DsTable { DsRow r[DS_ROWS]; };

static inline uint32_t DsMix(uint32_t h, uint32_t v) { return (h ^ v) * 0x01000193u; }  // FNV-1a over words; DoorShareCore.cs has the same

// the door stub's key (P4DoorKey: lot, Sim, portal owner as two words each, rooms, initialLocked) without the Sim
static inline void DsKey(const uint32_t* k9, uint32_t* k7) {
    k7[0] = k9[0], k7[1] = k9[1], k7[2] = k9[4], k7[3] = k9[5], k7[4] = k9[6], k7[5] = k9[7], k7[6] = k9[8];
}
static inline uint32_t DsHash(const uint32_t* k, int n) {
    uint32_t h = 0x811C9DC5u;
    for (int i = 0; i < n; i++) h = DsMix(h, k[i]);
    return h ^ (h >> 15);
}
// what ts3fixes sends with its note (op 41) for the ask it just answered: portal owner id, router id, rooms. Never 0.
static inline uint32_t DsNoteHash(uint32_t portalLo, uint32_t portalHi, uint32_t simLo, uint32_t simHi, uint32_t from, uint32_t to) {
    uint32_t h = 0x811C9DC5u;
    h = DsMix(h, portalLo), h = DsMix(h, portalHi), h = DsMix(h, simLo), h = DsMix(h, simHi);
    h = DsMix(h, from & 0xFFFF), h = DsMix(h, to & 0xFFFF);
    return h ? h : 1;
}
static inline uint32_t DsNoteHashKey(const uint32_t* k9) { return DsNoteHash(k9[4], k9[5], k9[2], k9[3], k9[6], k9[7]); }
// names a door (object id) or a lot (lot id) in a doorgen bump (op 36 lo; DoorShareCore.IdHash). Never 0 or 1.
static inline uint32_t DsIdHash(uint32_t lo, uint32_t hi) {
    uint32_t h = DsMix(DsMix(0x811C9DC5u, lo), hi);
    h ^= h >> 15;
    return h < 2 ? h + 2 : h;
}
// 13 bits of a router's object id, carried in op 40 so a class is used only for asks about that router
static inline uint32_t DsSimFold(uint32_t lo, uint32_t hi) {
    const uint32_t x = lo ^ hi;
    return (x ^ (x >> 13) ^ (x >> 26)) & 0x1FFF;
}

// --- stamps ---
// the stamp a change of this scope moves (NULL: none). idHash = DsIdHash of the door or lot; two ids may share a stamp
// (then one's change also clears the other's rows: more asks, never a wrong answer)
static inline int32_t* DsStamp(DsStamps* s, int scope, uint32_t idHash) {
    if (scope == DS_SC_OWNER) return &s->owner[idHash & (DS_OWNERS - 1)];
    if (scope == DS_SC_LOT) return &s->lot[idHash & (DS_LOTS - 1)];
    return scope == DS_SC_GLOBAL ? &s->global : 0;
}
static inline void DsGenOf(const DsStamps* s, const uint32_t* k7, DsGen* g) {
    g->global = s->global;
    g->lot = s->lot[DsIdHash(k7[0], k7[1]) & (DS_LOTS - 1)];
    g->owner = s->owner[DsIdHash(k7[2], k7[3]) & (DS_OWNERS - 1)];
}
static inline int DsGenSame(const DsGen* a, const DsGen* b) { return a->global == b->global && a->lot == b->lot && a->owner == b->owner; }
// ts3fixes doorgen sources. Lock type, lock owner, one-way room and owner list are the door's; the handler list and the
// hidden rooms are the lot's. The others are Sim facts read at every plan (role, occult, IgnoreDoorLocks, household, age,
// gender), or inputs of rules no shared kind uses (greeting: strays; fire: a pet or a barn door skips the lock type, and
// Anybody allows either way; resort VIP data: the VIPRoom lock type). A source this build does not know clears everything.
static inline int DsSrcScope(int src, uint32_t idHash) {
    switch (src) {
        case DS_SRC_LOCK: case DS_SRC_OWNERLIST: return idHash ? DS_SC_OWNER : DS_SC_GLOBAL;
        case DS_SRC_LOTHANDLER: case DS_SRC_HIDDEN: return idHash ? DS_SC_LOT : DS_SC_GLOBAL;
        case DS_SRC_GREET: case DS_SRC_HOUSEHOLD: case DS_SRC_FIRE: case DS_SRC_FIREBUFF: case DS_SRC_IGNORELOCKS: case DS_SRC_OCCULT:
        case DS_SRC_ROLE: case DS_SRC_RESORT: case DS_SRC_DESC: return DS_SC_NONE;
        default: return DS_SC_GLOBAL;  // DS_SRC_UNKNOWN (an older package's plain bump), the arm at world load, a newer package
    }
}
// routesnap's IC slots (kSnIcDef order). 4-11 add or remove the portals of one object (arg 1 = its id): its rows go, in
// case the object behind the id is another one now. 12 SetPortalCost and 13 SetPortalAgeGenderFlags only feed the planner's
// own lock test, whose result is the key's initialLocked: nothing to clear. 14 SetPortalLockStatus (arg 1 = lot id) turns a
// lot's asks on or off with its first / last handler. 22 OnExitBuildBuy: rooms may have new numbers.
static inline int DsIcScope(int ic) {
    if (ic >= 4 && ic <= 11) return DS_SC_OWNER;
    if (ic == 14) return DS_SC_LOT;
    return ic == 22 ? DS_SC_GLOBAL : DS_SC_NONE;
}
// World events: 5 / 7 load started / finished, 9 / 0x0A world file / save loaded, 0x0D / 0x0E lot added / removed: everything.
// 0x0F lot bounds and 0x10 lot type are read by no shared rule (a new or reloaded lot adds its handler: DS_SRC_LOTHANDLER).
static inline int DsWevScope(int id) {
    return id == 5 || id == 7 || id == 9 || id == 0x0A || id == 0x0D || id == 0x0E ? DS_SC_GLOBAL : DS_SC_NONE;
}

// --- table ---
static inline int DsSameKey(const DsRow* r, const uint32_t* k7) {
    for (int i = 0; i < 7; i++)
        if (r->key[i] != k7[i]) return 0;
    return 1;
}
static inline DsRow* DsSet(DsTable* t, const uint32_t* k7) { return &t->r[(DsHash(k7, 7) & (DS_SETS - 1)) * DS_WAYS]; }
// the key's row in whatever state, NULL = none
static inline DsRow* DsPeek(DsTable* t, const uint32_t* k7) {
    DsRow* s = DsSet(t, k7);
    for (int i = 0; i < DS_WAYS; i++)
        if (s[i].kind && DsSameKey(&s[i], k7)) return &s[i];
    return 0;
}
// DS_VALID: *out answers the key now. A row whose stamps or delegate moved, or one no live ask confirmed for maxAge ticks,
// is not an answer (and stays until a live answer replaces it)
static inline int DsFind(DsTable* t, const uint32_t* k7, const DsGen* gen, uint32_t deleg, int64_t now, int64_t maxAge, const DsRow** out) {
    const DsRow* r = DsPeek(t, k7);
    *out = 0;
    if (!r) return DS_NONE;
    if (!DsGenSame(&r->gen, gen) || r->deleg != deleg) return DS_OTHERGEN;
    if (now - r->t > maxAge) return DS_OLD;
    *out = r;
    return DS_VALID;
}
// a live "unlocked" answer ts3fixes marked as shareable: the key's row is written or refreshed (one row a key; a full set
// loses its oldest row). A no-actor note on a row that still holds as a door or inert row only confirms it: that row
// serves both classes.
static inline void DsPut(DsTable* t, const uint32_t* k7, const DsGen* gen, uint32_t deleg, int64_t now, int kind) {
    DsRow* w = DsPeek(t, k7);
    if (w && kind == DS_KIND_NOACTOR && w->kind != DS_KIND_NOACTOR && DsGenSame(&w->gen, gen) && w->deleg == deleg) { w->t = now; return; }
    DsRow* s = DsSet(t, k7);
    for (int i = 0; i < DS_WAYS && !w; i++)
        if (!s[i].kind) w = &s[i];
    if (!w) {
        w = &s[0];
        for (int i = 1; i < DS_WAYS; i++)
            if (s[i].t < w->t) w = &s[i];
    }
    for (int i = 0; i < 7; i++) w->key[i] = k7[i];
    w->deleg = deleg, w->gen = *gen, w->kind = kind, w->t = now;
}

// --- classes ---
// the class op 40 gave the running job, if it was sent for the router this ask is about
static inline int DsClassOf(uint32_t bits, uint32_t simLo, uint32_t simHi) {
    if (!(bits & (DS_C_ORDINARY | DS_C_NOACTOR)) || (bits >> DS_C_SIMSHIFT) != DsSimFold(simLo, simHi)) return DS_CLS_NONE;
    return bits & DS_C_NOACTOR ? DS_CLS_NOACTOR : DS_CLS_ORDINARY;
}
// does a row (or a note) of this kind speak for a router of this class?
static inline int DsKindFor(int cls, int kind) {
    if (cls == DS_CLS_NOACTOR) return kind > DS_KIND_NONE && kind < DS_KINDS;
    return cls == DS_CLS_ORDINARY && (kind == DS_KIND_DOOR || kind == DS_KIND_INERT);
}
// the kind a live answer may be stored as: the note's kind must be one the asking class can produce
static inline int DsNoteFor(int cls, int kind) {
    return cls == DS_CLS_NOACTOR ? kind == DS_KIND_NOACTOR : DsKindFor(cls, kind);
}
// the reason op 40 gave for a router in no class (DS_WHY_NOOP: no op 40 reached the job)
static inline int DsWhyOf(uint32_t bits) {
    if (bits & (DS_C_ORDINARY | DS_C_NOACTOR)) return -1;
    const uint32_t w = bits >> DS_C_SIMSHIFT;
    return w < DS_WHYS ? (int)w : DS_WHY_NOOP;
}
// May the running job take a valid row of this kind for an ask about router (simLo, simHi)? bits = op 40's value for the
// job's route. scope: 0 unknown, 1 household or selected, 2 unseen, 3 seen NPC. household = [RouteSnap] DoorShareHousehold or
// [Experimental] HouseholdToo. A player command of the selected Sim always asks.
static inline int DsMayTake(uint32_t bits, uint32_t simLo, uint32_t simHi, int scope, int household, int kind) {
    if (!(bits & (DS_C_ORDINARY | DS_C_NOACTOR))) return DS_NO_CLASS;
    const int cls = DsClassOf(bits, simLo, simHi);
    if (cls == DS_CLS_NONE) return DS_NO_SIM;
    if (!DsKindFor(cls, kind)) return DS_NO_KIND;
    if (bits & DS_C_COMMAND) return DS_NO_COMMAND;
    if (scope != 2 && scope != 3 && !household) return DS_NO_SCOPE;
    return DS_TAKE_OK;
}

// every nth valid answer is asked anyway (n <= 1: every one); *seq is the caller's running count
static inline int DsCheckDue(long* seq, long n) { return n <= 1 || (++*seq % n) == 0; }

// census: how many different keys were asked (a lower bound: one bit a hash, two keys can share one)
enum { DS_SEEN_WORDS = 1024 };
struct DsSeen { uint32_t bits[DS_SEEN_WORDS]; long n; };
static inline int DsSeenAdd(DsSeen* s, uint32_t h) {
    const uint32_t i = (h >> 5) & (DS_SEEN_WORDS - 1), b = 1u << (h & 31);
    if (s->bits[i] & b) return 0;
    s->bits[i] |= b, s->n++;
    return 1;
}

#endif
