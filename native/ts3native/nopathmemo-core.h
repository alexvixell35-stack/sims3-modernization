// [RouteAsync] NoPathMemo (docs/643): the memo table, pure code (no Windows calls, no game reads), so the unit test
// (nopathmemo-test.h) also builds and runs as a plain C++ program. nopathmemo.cpp holds the lock and the game side.
//
// One entry = one failed no-path plan of an NPC router. The request (NpmReq) is keyed by what stays the same between EA's
// retries: routing world, follower, agent class, the four RouteOption words and the route's destination set (its
// destination records, not the goal points the candidate step generates for each try). The start is not in the key: the
// entry keeps the recorded start point and a request matches only within a tolerance (the follower drifts between tries).
// 32 sets of 8 ways (256 entries); a set is picked by the key, a full set evicts its least recently used entry. Every call
// is O(ways) except NpmTabNearest (the capped miss diagnostic, whole table).
#pragma once
#include <stdint.h>
#include <string.h>
#include <math.h>

enum { NPM_WAYS = 8, NPM_SETS = 32, NPM_ENTRIES = NPM_WAYS * NPM_SETS };
struct NpmReq {
    uint64_t key;       // NpmReqKey: world, follower, agent, options, destination set
    uint64_t foll;      // follower ObjectGuid
    uint64_t goal;      // destination-set hash
    uint32_t agent, opts[4];
    float start[3];     // the first start point
    // [RouteAsync] NoPathMemoFuzzy (docs/643 §7): the request without its exact destination records
    uint64_t fkey;      // NpmFuzzyKey: world, follower, agent, options, destination count and types, valid rooms and their lot
    float gc[3];        // centroid of the plan's goal points (the `goals` records Route_PlanCommon generated for this try)
    float gr;           // their spread: the largest xz distance of a goal point from gc
    int32_t gn;         // goal points seen (0 = none: no fuzzy look-up)
};
struct NpmEntry {
    NpmReq req;
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
// FAR: an entry of this key whose start is beyond the tolerance (kept; the plan runs)
enum NpmLook { NPM_MISS, NPM_HIT, NPM_EXPIRED, NPM_INVALID, NPM_FAR };

static inline uint64_t NpmMix(uint64_t h, uint64_t v) {
    for (int i = 0; i < 8; i++) { h ^= (v >> (i * 8)) & 0xFF; h *= 0x100000001B3ull; }
    return h;
}
// the key of a request: everything but the start
static inline uint64_t NpmReqKey(const NpmReq& r, uint64_t world) {
    uint64_t h = NpmMix(NpmMix(NpmMix(0xCBF29CE484222325ull, world), r.foll), r.goal);
    h = NpmMix(h, r.agent);
    for (int i = 0; i < 4; i++) h = NpmMix(h, r.opts[i]);
    return h;
}
static inline float NpmDist2(const float* a, const float* b) {
    const float dx = a[0] - b[0], dy = a[1] - b[1], dz = a[2] - b[2];
    return dx * dx + dy * dy + dz * dz;
}

static inline NpmEntry* NpmSet(NpmTable* t, uint64_t key) {
    const uint64_t m = key ^ (key >> 29) ^ (key >> 47);
    return &t->e[(uint32_t)(m & (NPM_SETS - 1)) * NPM_WAYS];
}
static inline NpmEntry* NpmFind(NpmTable* t, uint64_t key) {
    NpmEntry* s = NpmSet(t, key);
    for (int i = 0; i < NPM_WAYS; i++)
        if (s[i].live && s[i].req.key == key) return &s[i];
    return 0;
}

// The request at sim tick `now`, start within tolM metres of the recorded one. HIT: *ms = the recorded plan's time, the
// entry stays as it was except its LRU stamp. EXPIRED (ttl ticks passed, or the clock went back: another save) and INVALID
// (a world version moved) drop the entry; FAR keeps it.
static inline int NpmTabLook(NpmTable* t, const NpmReq& r, uint64_t now, uint64_t ttl, uint32_t sver, uint32_t ver, float tolM,
                             int32_t* ms) {
    NpmEntry* e = NpmFind(t, r.key);
    if (!e) return NPM_MISS;
    if (now < e->tick || now - e->tick >= ttl) { e->live = 0; return NPM_EXPIRED; }
    if (e->sver != sver || (e->dyn && e->ver != ver)) { e->live = 0; return NPM_INVALID; }
    if (NpmDist2(e->req.start, r.start) > tolM * tolM) return NPM_FAR;
    e->use = ++t->clock;
    if (ms) *ms = e->ms;
    return NPM_HIT;
}

// Records (or re-records, start included) a failure. Returns 1 when a live entry of another key was evicted for it.
static inline int NpmTabPut(NpmTable* t, const NpmReq& r, uint64_t now, uint32_t sver, uint32_t ver, bool dyn, int32_t ms) {
    NpmEntry* s = NpmSet(t, r.key);
    NpmEntry* pick = 0;
    int evicted = 0;
    for (int i = 0; i < NPM_WAYS && !pick; i++)
        if (s[i].live && s[i].req.key == r.key) pick = &s[i];
    for (int i = 0; i < NPM_WAYS && !pick; i++)
        if (!s[i].live) pick = &s[i];
    if (!pick) {
        pick = &s[0];
        for (int i = 1; i < NPM_WAYS; i++)
            if (s[i].use < pick->use) pick = &s[i];
        evicted = 1;
    }
    pick->req = r, pick->tick = now, pick->use = ++t->clock;
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

// miss diagnostic: the live entry of this follower whose start is nearest, or 0
static inline const NpmEntry* NpmTabNearest(const NpmTable* t, uint64_t foll, const float* start) {
    const NpmEntry* best = 0;
    float bd = 0;
    for (int i = 0; i < NPM_ENTRIES; i++) {
        const NpmEntry& e = t->e[i];
        if (!e.live || e.req.foll != foll) continue;
        const float d = NpmDist2(e.req.start, start);
        if (!best || d < bd) best = &e, bd = d;
    }
    return best;
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

// ---- [RouteAsync] NoPathMemoFuzzy (docs/643 §7) ------------------------------------------------------------------------
// EA's FollowerMeetUpAtPoint (GoHereWithHoldingInteraction.Run) makes a new Route for every try, each with a RadialRange
// destination around a meeting point that moves 1.5 x 0.3 m in a random direction after a failed try, and the situation
// hands out new meeting points around the leader: no two tries have the same destination records, so the exact key above
// never repeats. The fuzzy table remembers the geometry of a recorded no-path plan instead: same follower, the same request
// without its destination records (fkey), a start within NPM_FZ_START_M, a goal centroid within NPM_FZ_GOAL_M (xz) and
// NPM_FZ_DY_M (height: same level) and a goal spread within NPM_FZ_GOAL_M of the recorded one, inside a short TTL. Any plan
// of that follower that does not fail with no path drops all its fuzzy entries (NpmFzDropFoll), so an answer never stands
// after a success. 64 entries, LRU; every call scans the 64 (O(64)).
enum { NPM_FZ_ENTRIES = 64 };
static const float NPM_FZ_START_M = 0.5f, NPM_FZ_GOAL_M = 1.0f, NPM_FZ_DY_M = 0.5f;
struct NpmFzEntry {
    uint64_t fkey, foll;
    float start[3], gc[3], gr;
    uint64_t tick, use;
    uint32_t sver, ver;
    int32_t ms;
    uint8_t dyn, live;
};
struct NpmFzTable {
    NpmFzEntry e[NPM_FZ_ENTRIES];
    uint64_t clock;
};
static inline float NpmDistXz(const float* a, const float* b) {
    const float dx = a[0] - b[0], dz = a[2] - b[2];
    return sqrtf(dx * dx + dz * dz);
}

// the fuzzy key: the exact key's parts without the destination records; shape = destination count and types, valid rooms
// and their lot (NpmReqOf), the same on every FollowerMeetUpAtPoint try
static inline uint64_t NpmFuzzyKey(const NpmReq& r, uint64_t world, uint64_t shape) {
    uint64_t h = NpmMix(NpmMix(NpmMix(0x84222325CBF29CE4ull, world), r.foll), shape);
    h = NpmMix(h, r.agent);
    for (int i = 0; i < 4; i++) h = NpmMix(h, r.opts[i]);
    return h;
}

// centroid and spread of n goal records (stride bytes, x/y/z floats at +0/+4/+8: 64-byte route points). The 256 first only.
static inline void NpmGoalGeom(const uint8_t* recs, int n, int stride, NpmReq* r) {
    r->gn = 0, r->gc[0] = r->gc[1] = r->gc[2] = 0, r->gr = 0;
    if (!recs || n <= 0) return;
    if (n > 256) n = 256;
    double sx = 0, sy = 0, sz = 0;
    for (int i = 0; i < n; i++) {
        const float* p = (const float*)(recs + (size_t)i * stride);
        sx += p[0], sy += p[1], sz += p[2];
    }
    r->gc[0] = (float)(sx / n), r->gc[1] = (float)(sy / n), r->gc[2] = (float)(sz / n);
    float m = 0;
    for (int i = 0; i < n; i++) {
        const float d = NpmDistXz((const float*)(recs + (size_t)i * stride), r->gc);
        if (d > m) m = d;
    }
    r->gr = m, r->gn = n;
}

// HIT: the nearest-goal entry that matches (*ms its plan's time, *goalM / *startM its distances); FAR: entries of this
// follower and fkey, none near enough; MISS: none. Entries past ttl (or the clock went back) and entries whose world
// version moved are dropped on the way (*dropped counts them).
static inline int NpmFzLook(NpmFzTable* t, const NpmReq& q, uint64_t now, uint64_t ttl, uint32_t sver, uint32_t ver,
                            int32_t* ms, float* goalM, float* startM, int* dropped) {
    NpmFzEntry* best = 0;
    float bd = 0;
    bool any = false;
    if (dropped) *dropped = 0;
    if (q.gn <= 0) return NPM_MISS;
    for (int i = 0; i < NPM_FZ_ENTRIES; i++) {
        NpmFzEntry& e = t->e[i];
        if (!e.live || e.foll != q.foll || e.fkey != q.fkey) continue;
        if (now < e.tick || now - e.tick >= ttl || e.sver != sver || (e.dyn && e.ver != ver)) {
            e.live = 0;
            if (dropped) ++*dropped;
            continue;
        }
        any = true;
        const float g = NpmDistXz(e.gc, q.gc);
        const float dy = e.gc[1] - q.gc[1], dr = e.gr - q.gr;
        if (sqrtf(NpmDist2(e.start, q.start)) > NPM_FZ_START_M || g > NPM_FZ_GOAL_M || dy > NPM_FZ_DY_M || dy < -NPM_FZ_DY_M ||
            dr > NPM_FZ_GOAL_M || dr < -NPM_FZ_GOAL_M)
            continue;
        if (!best || g < bd) best = &e, bd = g;
    }
    if (!best) return any ? NPM_FAR : NPM_MISS;
    best->use = ++t->clock;
    if (ms) *ms = best->ms;
    if (goalM) *goalM = bd;
    if (startM) *startM = sqrtf(NpmDist2(best->start, q.start));
    return NPM_HIT;
}

// records a real no-path plan's geometry. An entry of the same follower and fkey with the same start and goal (5 cm) is
// refreshed in place; else a free slot, else the least recently used. Returns 1 when a live entry was evicted.
static inline int NpmFzPut(NpmFzTable* t, const NpmReq& q, uint64_t now, uint32_t sver, uint32_t ver, bool dyn, int32_t ms) {
    if (q.gn <= 0) return 0;
    NpmFzEntry* pick = 0;
    int evicted = 0;
    for (int i = 0; i < NPM_FZ_ENTRIES && !pick; i++) {
        NpmFzEntry& e = t->e[i];
        if (e.live && e.foll == q.foll && e.fkey == q.fkey && NpmDistXz(e.gc, q.gc) < 0.05f && NpmDist2(e.start, q.start) < 0.0025f)
            pick = &e;
    }
    for (int i = 0; i < NPM_FZ_ENTRIES && !pick; i++)
        if (!t->e[i].live) pick = &t->e[i];
    if (!pick) {
        pick = &t->e[0];
        for (int i = 1; i < NPM_FZ_ENTRIES; i++)
            if (t->e[i].use < pick->use) pick = &t->e[i];
        evicted = 1;
    }
    pick->fkey = q.fkey, pick->foll = q.foll;
    memcpy(pick->start, q.start, sizeof(pick->start));
    memcpy(pick->gc, q.gc, sizeof(pick->gc));
    pick->gr = q.gr, pick->tick = now, pick->use = ++t->clock;
    pick->sver = sver, pick->ver = ver, pick->ms = ms, pick->dyn = dyn ? 1 : 0, pick->live = 1;
    return evicted;
}

// a plan of this follower ran and did not fail with no path: none of its fuzzy entries may answer any more
static inline int NpmFzDropFoll(NpmFzTable* t, uint64_t foll) {
    int n = 0;
    for (int i = 0; i < NPM_FZ_ENTRIES; i++)
        if (t->e[i].live && t->e[i].foll == foll) t->e[i].live = 0, n++;
    return n;
}
static inline int NpmFzLive(const NpmFzTable* t) {
    int n = 0;
    for (int i = 0; i < NPM_FZ_ENTRIES; i++) n += t->e[i].live;
    return n;
}
static inline int NpmFzClear(NpmFzTable* t) {
    const int n = NpmFzLive(t);
    memset(t, 0, sizeof(*t));
    return n;
}
