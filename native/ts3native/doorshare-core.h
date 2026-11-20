// docs/674: the pure part of [RouteSnap] DoorShare (doorshare.cpp): the class-shared door answer table, the note hash shared
// with ts3fixes (DoorShareCore.cs), who may take a shared answer, and the ask census. No Windows, no game, no clock:
// doorshare-host-test.cpp and the selftest run it as is. Times are plain tick counts handed in by the caller.
#ifndef DOORSHARE_CORE_H
#define DOORSHARE_CORE_H

#include <stdint.h>

enum { DS_SETS = 256, DS_WAYS = 4, DS_ROWS = DS_SETS * DS_WAYS };
enum { DS_NONE, DS_VALID, DS_OTHERGEN, DS_OLD };  // DsFind
enum { DS_KIND_NONE, DS_KIND_DOOR, DS_KIND_INERT, DS_KINDS };  // ts3fixes' note: a plain door open to anybody, a portal no rule reads
enum { DS_C_ORDINARY = 1, DS_C_COMMAND = 2, DS_C_SIMSHIFT = 3 };  // op 40's 16 bits: class, player command, 13 bits of the Sim id
// why an ask was not answered from the table (DsMayTake; 0 = it may be)
enum { DS_TAKE_OK, DS_NO_CLASS, DS_NO_SIM, DS_NO_SCOPE, DS_NO_COMMAND, DS_TAKE_WHYS };

// a row answers "unlocked" (2) for every Sim of the ordinary class: the key has no Sim in it
struct DsRow {
    uint32_t key[7];  // lot id, portal owner id (two words each), room from, room to, initialLocked
    uint32_t deleg;   // the event 59 delegate slot when the row was written
    int32_t gen;      // the door generation when the row was written
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
// what ts3fixes sends with its note (op 41) for the ask it just answered: portal owner id, Sim id, rooms. Never 0.
static inline uint32_t DsNoteHash(uint32_t portalLo, uint32_t portalHi, uint32_t simLo, uint32_t simHi, uint32_t from, uint32_t to) {
    uint32_t h = 0x811C9DC5u;
    h = DsMix(h, portalLo), h = DsMix(h, portalHi), h = DsMix(h, simLo), h = DsMix(h, simHi);
    h = DsMix(h, from & 0xFFFF), h = DsMix(h, to & 0xFFFF);
    return h ? h : 1;
}
static inline uint32_t DsNoteHashKey(const uint32_t* k9) { return DsNoteHash(k9[4], k9[5], k9[2], k9[3], k9[6], k9[7]); }
// 13 bits of a Sim's object id, carried in op 40 so a class is used only for asks about that Sim
static inline uint32_t DsSimFold(uint32_t lo, uint32_t hi) {
    const uint32_t x = lo ^ hi;
    return (x ^ (x >> 13) ^ (x >> 26)) & 0x1FFF;
}
static inline int DsSameKey(const DsRow* r, const uint32_t* k7) {
    for (int i = 0; i < 7; i++)
        if (r->key[i] != k7[i]) return 0;
    return 1;
}
static inline DsRow* DsSet(DsTable* t, const uint32_t* k7) { return &t->r[(DsHash(k7, 7) & (DS_SETS - 1)) * DS_WAYS]; }

// DS_VALID: *out answers the key now. A row of another generation or delegate, or one no live ask confirmed for maxAge
// ticks, is not an answer (and stays until a live answer replaces it)
static inline int DsFind(DsTable* t, const uint32_t* k7, int32_t gen, uint32_t deleg, int64_t now, int64_t maxAge, const DsRow** out) {
    DsRow* s = DsSet(t, k7);
    *out = 0;
    for (int i = 0; i < DS_WAYS; i++) {
        if (!s[i].kind || !DsSameKey(&s[i], k7)) continue;
        if (s[i].gen != gen || s[i].deleg != deleg) return DS_OTHERGEN;
        if (now - s[i].t > maxAge) return DS_OLD;
        *out = &s[i];
        return DS_VALID;
    }
    return DS_NONE;
}
// a live "unlocked" answer ts3fixes marked as shareable: the key's row is written or refreshed (one row a key; a full set
// loses its oldest row)
static inline void DsPut(DsTable* t, const uint32_t* k7, int32_t gen, uint32_t deleg, int64_t now, int kind) {
    DsRow* s = DsSet(t, k7);
    DsRow* w = 0;
    for (int i = 0; i < DS_WAYS && !w; i++)
        if (s[i].kind && DsSameKey(&s[i], k7)) w = &s[i];
    for (int i = 0; i < DS_WAYS && !w; i++)
        if (!s[i].kind) w = &s[i];
    if (!w) {
        w = &s[0];
        for (int i = 1; i < DS_WAYS; i++)
            if (s[i].t < w->t) w = &s[i];
    }
    for (int i = 0; i < 7; i++) w->key[i] = k7[i];
    w->deleg = deleg, w->gen = gen, w->kind = kind, w->t = now;
}
// a live answer that is not a shareable "unlocked": the key's row goes
static inline int DsDrop(DsTable* t, const uint32_t* k7) {
    DsRow* s = DsSet(t, k7);
    for (int i = 0; i < DS_WAYS; i++)
        if (s[i].kind && DsSameKey(&s[i], k7)) { s[i].kind = DS_KIND_NONE; return 1; }
    return 0;
}

// May the running job take a shared answer for an ask about Sim (simLo, simHi)? bits = op 40's value for the job's route
// (0 = ts3fixes sent none: not a Sim of the ordinary class, or an old package). scope: 0 unknown, 1 household or selected,
// 2 unseen, 3 seen NPC. household = [RouteSnap] DoorShareHousehold or [Experimental] HouseholdToo. A player command of the
// selected Sim always asks.
static inline int DsMayTake(uint32_t bits, uint32_t simLo, uint32_t simHi, int scope, int household) {
    if (!(bits & DS_C_ORDINARY)) return DS_NO_CLASS;
    if ((bits >> DS_C_SIMSHIFT) != DsSimFold(simLo, simHi)) return DS_NO_SIM;
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
