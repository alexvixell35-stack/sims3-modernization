// docs/669 [RouteSpeed] CellIndex: the pure part (no Windows, no game memory), shared by cellindex.cpp, its selftest and
// the freestanding host test (cellindex-host-test.cpp).
//
// A side index of positive answers of AStarCellTable_Find 0x664370: (table, k0, k1) -> (cell, bucket). The game's table has
// 10007 chained buckets, bucket = (k0 xor k1) mod 10007; k1's shape byte and level nibble are xor-ed into the same bits as
// k0's x index, so the sub-cells of neighbouring leaves share buckets and a lookup walks a chain of cell objects (one cache
// miss each). The index is a 2-way set cache, one per thread, with its own mixed hash and the key words inline, so a repeat
// lookup touches one set (48 bytes) and then the right cell.
//
// It is a cache, never the truth: only what the game's own Find returned is kept, "not found" is never kept, a full set
// evicts, and the caller checks the cell's own key words (+0x30 / +0x34) before it serves. Cleared as a whole, O(1) by a
// generation bump, when the clear sequence moves (Q's cell table clear 0x61C020, the only place cells leave a table).
#ifndef CELLINDEX_CORE_H
#define CELLINDEX_CORE_H

#include <stdint.h>

enum { CI_SETS = 8192, CI_WAYS = 2, CI_BUCKETS = 10007 };
enum { CI_NEW, CI_SAME, CI_EVICT };

struct CiSlot {
    uintptr_t table, cell, bucket;
    uint32_t k0, k1, gen;
};
struct CiIndex {
    uint32_t gen, seqClear, rr;
    CiSlot slot[CI_SETS * CI_WAYS];
};

// the game's bucket number (0x664370: xor, div 10007)
static inline uint32_t CiBucket(uint32_t k0, uint32_t k1) { return (k0 ^ k1) % CI_BUCKETS; }

// first slot of the key's set
static inline uint32_t CiHash(uintptr_t table, uint32_t k0, uint32_t k1) {
    uint32_t h = k0 * 0x9E3779B1u ^ k1 * 0x85EBCA6Bu ^ (uint32_t)table * 0xC2B2AE35u;
    h ^= h >> 15;
    h *= 0x2C1B3C6Du;
    h ^= h >> 13;
    return (h & (CI_SETS - 1)) * CI_WAYS;
}

// every entry gone; gen 0 never marks a live slot, so a wrap zeroes the slots first
static inline void CiReset(CiIndex* x) {
    if (++x->gen == 0) {
        for (uint32_t i = 0; i < CI_SETS * CI_WAYS; i++) x->slot[i].gen = 0;
        x->gen = 1;
    }
}

// true = the index was cleared because the clear sequence moved since it was filled
static inline bool CiSync(CiIndex* x, uint32_t seqClear) {
    if (x->gen && x->seqClear == seqClear) return false;
    CiReset(x);
    x->seqClear = seqClear;
    return true;
}

static inline CiSlot* CiFind(CiIndex* x, uintptr_t table, uint32_t k0, uint32_t k1) {
    CiSlot* s = x->slot + CiHash(table, k0, k1);
    for (int w = 0; w < CI_WAYS; w++, s++)
        if (s->gen == x->gen && s->k0 == k0 && s->k1 == k1 && s->table == table) return s;
    return 0;
}

static inline void CiDrop(CiSlot* s) { s->gen = 0; }

// keep one answer of the game's Find. The same key again rewrites its slot; a set with no free way loses one entry
// (ways in turn), which only costs that key a later walk.
static inline int CiStore(CiIndex* x, uintptr_t table, uint32_t k0, uint32_t k1, uintptr_t cell, uintptr_t bucket) {
    CiSlot* const set = x->slot + CiHash(table, k0, k1);
    CiSlot* s = 0;
    int r = CI_NEW;
    for (int w = 0; w < CI_WAYS; w++)
        if (set[w].gen == x->gen && set[w].k0 == k0 && set[w].k1 == k1 && set[w].table == table) { s = set + w, r = CI_SAME; break; }
    for (int w = 0; !s && w < CI_WAYS; w++)
        if (set[w].gen != x->gen) s = set + w;
    if (!s) s = set + (x->rr++ & (CI_WAYS - 1)), r = CI_EVICT;
    s->gen = 0;  // not live while it is rewritten
    s->table = table, s->k0 = k0, s->k1 = k1, s->cell = cell, s->bucket = bucket;
    s->gen = x->gen;
    return r;
}

// serve rule: 0 = run the game's function, 1 = serve the index. mode 1 serves; mode 2 (shadow) never serves. keyOk: the
// kept cell still carries the asked key words; scopeOk: the router may take a served answer.
static inline int CiServeRule(int mode, bool found, bool keyOk, bool scopeOk) {
    return mode == 1 && found && keyOk && scopeOk ? 1 : 0;
}

#endif
