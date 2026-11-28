// docs/663 [RouteSpeed] NeighbourMemo: the pure part (no Windows, no game memory), shared by routespeed.cpp, its selftest
// and the freestanding host test (routespeed-host-test.cpp).
//
// A memo of QuadCell_Neighbours 0x66B9C0 (cell, Q) -> the ordered list of neighbour cell objects. One per thread, open
// addressing (NM_SLOTS slots, NM_PROBE probes), list words in one pool (NM_POOL words). Cleared as a whole, O(1) by a
// generation bump, when either sequence it was filled under moves:
//   seqClear: Q's cell table clear 0x61C020 (every hierarchical plan's Q reset 0x61E320 and Q's init 0x61E420): the cells
//             of the old table die there, so no entry survives the plan it was made in;
//   seqTree:  quadtree / lot-structure writes (routesnap.cpp SnEnter rows W1-W5, RemoveLot, ApplyLotUpdate) on any thread.
// An entry also carries the cell's own key words (+0x30/+0x34): a cell object freed and reused at the same address with
// another key misses. Lists longer than NM_MAXN are not kept; a full pool or a full probe run stores nothing (counted).
#ifndef ROUTESPEED_CORE_H
#define ROUTESPEED_CORE_H

#include <stdint.h>

enum { NM_SLOTS = 4096, NM_POOL = 32768, NM_MAXN = 32, NM_PROBE = 8 };
enum { NM_STORED, NM_LONG, NM_POOLFULL, NM_TABLEFULL, NM_NULLCELL };

struct NmSlot {
    uintptr_t q, cell;
    uint32_t k0, k1, gen, off;
    uint32_t n;
};
struct NmMemo {
    uint32_t gen, seqClear, seqTree, used, entries;
    NmSlot slot[NM_SLOTS];
    uintptr_t pool[NM_POOL];
};

static inline uint32_t NmHash(uintptr_t q, uintptr_t cell) {
    uint32_t h = (uint32_t)cell * 0x9E3779B1u ^ (uint32_t)q * 0x85EBCA6Bu;
    h ^= h >> 15;
    h *= 0x2C1B3C6Du;
    h ^= h >> 13;
    return h & (NM_SLOTS - 1);
}

// every entry gone; gen 0 never marks a live slot, so a wrap zeroes the slots first
static inline void NmReset(NmMemo* m) {
    if (++m->gen == 0) {
        for (uint32_t i = 0; i < NM_SLOTS; i++) m->slot[i].gen = 0;
        m->gen = 1;
    }
    m->used = m->entries = 0;
}

// true = the memo was cleared because a sequence moved since it was filled
static inline bool NmSync(NmMemo* m, uint32_t seqClear, uint32_t seqTree) {
    if (m->gen && m->seqClear == seqClear && m->seqTree == seqTree) return false;
    NmReset(m);
    m->seqClear = seqClear, m->seqTree = seqTree;
    return true;
}

static inline const NmSlot* NmFind(const NmMemo* m, uintptr_t q, uintptr_t cell, uint32_t k0, uint32_t k1) {
    uint32_t i = NmHash(q, cell);
    for (int p = 0; p < NM_PROBE; p++, i = (i + 1) & (NM_SLOTS - 1)) {
        const NmSlot& s = m->slot[i];
        if (s.gen != m->gen) return 0;  // probe runs end at the first free slot (no removals inside a generation)
        if (s.cell == cell && s.q == q && s.k0 == k0 && s.k1 == k1) return &s;
    }
    return 0;
}

static inline const uintptr_t* NmList(const NmMemo* m, const NmSlot* s) { return m->pool + s->off; }

// list = the neighbour cells as the game's function left them in its out vector
static inline int NmStore(NmMemo* m, uintptr_t q, uintptr_t cell, uint32_t k0, uint32_t k1, const uintptr_t* list, uint32_t n) {
    if (n > NM_MAXN) return NM_LONG;
    for (uint32_t j = 0; j < n; j++)
        if (!list[j]) return NM_NULLCELL;  // the game's push of a failed cell create: never kept
    if (m->used + n > NM_POOL) return NM_POOLFULL;
    uint32_t i = NmHash(q, cell);
    for (int p = 0; p < NM_PROBE; p++, i = (i + 1) & (NM_SLOTS - 1)) {
        NmSlot& s = m->slot[i];
        if (s.gen == m->gen) {
            if (s.cell == cell && s.q == q && s.k0 == k0 && s.k1 == k1) return NM_STORED;  // already there (a capacity fallback)
            continue;
        }
        s.q = q, s.cell = cell, s.k0 = k0, s.k1 = k1, s.off = m->used, s.n = n;
        for (uint32_t j = 0; j < n; j++) m->pool[m->used + j] = list[j];
        m->used += n;
        m->entries++;
        s.gen = m->gen;  // last: the slot is live only once its list is written
        return NM_STORED;
    }
    return NM_TABLEFULL;
}

static inline bool NmSame(const NmMemo* m, const NmSlot* s, const uintptr_t* list, uint32_t n) {
    if (s->n != n) return false;
    const uintptr_t* a = NmList(m, s);
    for (uint32_t j = 0; j < n; j++)
        if (a[j] != list[j]) return false;
    return true;
}

// serve rule: 0 = run the game's function, 1 = serve the memo. mode 1 serves; mode 2 (shadow) never serves. scopeOk: the
// router may take a served answer (NPC scope, or NeighbourMemoAll / HouseholdToo). cap = words of room in the out vector.
static inline int NmServeRule(int mode, bool found, bool scopeOk, uint32_t cap, uint32_t n) {
    return mode == 1 && found && scopeOk && cap >= n ? 1 : 0;
}

#endif
