// [RouteAsync] NoPathMemo (docs/643): the memo table, pure code (no Windows calls, no game reads), so the unit test
// (nopathmemo-test.h) also builds and runs as a plain C++ program. nopathmemo.cpp holds the lock and the game side.
//
// One entry = one failed no-path plan of an NPC router: the request key (nopathmemo.cpp NpmKeyOf), the sim tick it was
// recorded at, the world versions it was recorded under, and how long the real plan took. 32 sets of 8 ways (256 entries,
// ~10 KB); a set is picked by the key, a full set evicts its least recently used entry. Every call is O(ways).
#pragma once
#include <stdint.h>
#include <string.h>

enum { NPM_WAYS = 8, NPM_SETS = 32, NPM_ENTRIES = NPM_WAYS * NPM_SETS };
struct NpmEntry {
    uint64_t key;
    uint64_t tick;      // sim tick of the recorded plan's end; answers never move it (the TTL counts from the real plan)
    uint64_t use;       // LRU stamp (NpmTable::clock at the last record or answer)
    uint32_t sver, ver; // [RouteSnap] world versions at record: static (lot routing, water grid, IC epochs) / all incl. footprints
    int32_t ms;         // the recorded plan's own time
    uint8_t dyn;        // the request reads dynamic footprints (BlockedByPeople / BlockedByDynamicFootprints): ver must match too
    uint8_t live;
};
struct NpmTable {
    NpmEntry e[NPM_ENTRIES];
    uint64_t clock;
};
enum NpmLook { NPM_MISS, NPM_HIT, NPM_EXPIRED, NPM_INVALID };

static inline NpmEntry* NpmSet(NpmTable* t, uint64_t key) {
    const uint64_t m = key ^ (key >> 29) ^ (key >> 47);
    return &t->e[(uint32_t)(m & (NPM_SETS - 1)) * NPM_WAYS];
}
static inline NpmEntry* NpmFind(NpmTable* t, uint64_t key) {
    NpmEntry* s = NpmSet(t, key);
    for (int i = 0; i < NPM_WAYS; i++)
        if (s[i].live && s[i].key == key) return &s[i];
    return 0;
}

// The request `key` at sim tick `now`. HIT: *ms = the recorded plan's time, the entry stays as it was except its LRU stamp.
// EXPIRED (ttl ticks passed, or the clock went back: another save) and INVALID (a world version moved) drop the entry.
static inline int NpmTabLook(NpmTable* t, uint64_t key, uint64_t now, uint64_t ttl, uint32_t sver, uint32_t ver, int32_t* ms) {
    NpmEntry* e = NpmFind(t, key);
    if (!e) return NPM_MISS;
    if (now < e->tick || now - e->tick >= ttl) { e->live = 0; return NPM_EXPIRED; }
    if (e->sver != sver || (e->dyn && e->ver != ver)) { e->live = 0; return NPM_INVALID; }
    e->use = ++t->clock;
    if (ms) *ms = e->ms;
    return NPM_HIT;
}

// Records (or re-records) a failure. Returns 1 when a live entry of another key was evicted for it.
static inline int NpmTabPut(NpmTable* t, uint64_t key, uint64_t now, uint32_t sver, uint32_t ver, bool dyn, int32_t ms) {
    NpmEntry* s = NpmSet(t, key);
    NpmEntry* pick = 0;
    int evicted = 0;
    for (int i = 0; i < NPM_WAYS && !pick; i++)
        if (s[i].live && s[i].key == key) pick = &s[i];
    for (int i = 0; i < NPM_WAYS && !pick; i++)
        if (!s[i].live) pick = &s[i];
    if (!pick) {
        pick = &s[0];
        for (int i = 1; i < NPM_WAYS; i++)
            if (s[i].use < pick->use) pick = &s[i];
        evicted = 1;
    }
    pick->key = key, pick->tick = now, pick->use = ++t->clock;
    pick->sver = sver, pick->ver = ver, pick->ms = ms, pick->dyn = dyn ? 1 : 0, pick->live = 1;
    return evicted;
}

// A plan of this key ran and did not fail with no path (success, other failure): its memo is wrong now. 1 = one was dropped.
static inline int NpmTabDrop(NpmTable* t, uint64_t key) {
    NpmEntry* e = NpmFind(t, key);
    if (!e) return 0;
    e->live = 0;
    return 1;
}

// another routing world (travel, load) or the switch turned off: nothing recorded holds any more. Returns the live count dropped.
static inline int NpmTabClear(NpmTable* t) {
    int n = 0;
    for (int i = 0; i < NPM_ENTRIES; i++) n += t->e[i].live;
    memset(t, 0, sizeof(*t));
    return n;
}

static inline int NpmTabLive(const NpmTable* t) {
    int n = 0;
    for (int i = 0; i < NPM_ENTRIES; i++) n += t->e[i].live;
    return n;
}

// sim ticks for N sim-minutes: SimClock runs 37.5 ticks per sim-minute (SimClock.kSimulatorTicksPerSimMinute, docs/04a)
static inline uint64_t NpmTtlTicks(int minutes) { return (uint64_t)(minutes > 0 ? minutes : 0) * 75 / 2; }
