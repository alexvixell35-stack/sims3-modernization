// docs/667 [RouteSpeed] FloodMemo: the pure part (no Windows, no game memory), shared by floodmemo.cpp, its selftest and
// the freestanding host test (floodmemo-host-test.cpp).
//
// A failed in-room cell A* (QuadTreeCellAStar 0x61E500) that ran until its open list was empty has closed every cell node
// that can be reached from its start: the popped nodes are that closed set C. Under the same search context (key) and the
// same routing data, a later search whose start cell is in C can only reach cells of C, so it fails as well unless one of
// its goals hangs on a cell of C. The memo keeps C per failed flood (a record) and answers "this start is inside record R".
// The caller (floodmemo.cpp) then checks the goal links of R's cells and takes the game's own failure exit.
//
// One memo per planning thread: FM_RECS records, their (cell, node) pairs in one pool, and one open-addressing hash from a
// cell to the bit mask of the records that hold it. Cleared as a whole, O(1) by a generation bump, when any sequence it was
// filled under moves (Q's cell table clear, a routing data write, a managed call on the planning thread).
#ifndef FLOODMEMO_CORE_H
#define FLOODMEMO_CORE_H

#include <stdint.h>

enum { FM_RECS = 8, FM_POOL = 16384, FM_HASH = 32768, FM_MAXFLOOD = 8192, FM_PROBE = 64 };
enum { FM_STORED, FM_EMPTY, FM_LONG, FM_POOLFULL, FM_RECSFULL, FM_HASHFULL, FM_DUP };

// everything the walkability of a cell and of an edge depends on besides the routing data itself (docs/667 §4.2)
struct FmKey {
    uintptr_t q, lot, opts;          // the A* object, X+0x54 lot object, X+0x40 route options (agent)
    uint32_t flags, cls, optHash;    // X+0x50, X+0x4C, hash of the option words
};
struct FmPair { uintptr_t cell, node; };
struct FmRec { FmKey key; uint32_t off, n; };
struct FmSlot { uintptr_t cell; uint32_t gen, mask; };
struct FmMemo {
    uint32_t gen, seqClear, seqWrite, seqHost, nRecs, used;
    FmRec rec[FM_RECS];
    FmPair pool[FM_POOL];
    FmSlot slot[FM_HASH];
};

static inline bool FmKeyEq(const FmKey& a, const FmKey& b) {
    return a.q == b.q && a.lot == b.lot && a.opts == b.opts && a.flags == b.flags && a.cls == b.cls && a.optHash == b.optHash;
}

static inline uint32_t FmHash(uintptr_t cell) {
    uint32_t h = (uint32_t)cell * 0x9E3779B1u;
    h ^= h >> 15;
    h *= 0x2C1B3C6Du;
    h ^= h >> 13;
    return h & (FM_HASH - 1);
}

// every record gone; gen 0 never marks a live slot, so a wrap zeroes the slots first
static inline void FmReset(FmMemo* m) {
    if (++m->gen == 0) {
        for (uint32_t i = 0; i < FM_HASH; i++) m->slot[i].gen = 0;
        m->gen = 1;
    }
    m->nRecs = m->used = 0;
}

// true = the memo was cleared because a sequence moved since it was filled
static inline bool FmSync(FmMemo* m, uint32_t seqClear, uint32_t seqWrite, uint32_t seqHost) {
    if (m->gen && m->seqClear == seqClear && m->seqWrite == seqWrite && m->seqHost == seqHost) return false;
    FmReset(m);
    m->seqClear = seqClear, m->seqWrite = seqWrite, m->seqHost = seqHost;
    return true;
}

// bit r set = record r holds the cell
static inline uint32_t FmMaskOf(const FmMemo* m, uintptr_t cell) {
    uint32_t i = FmHash(cell);
    for (int p = 0; p < FM_PROBE; p++, i = (i + 1) & (FM_HASH - 1)) {
        const FmSlot& s = m->slot[i];
        if (s.gen != m->gen) return 0;  // probe runs end at the first free slot (no removals inside a generation)
        if (s.cell == cell) return s.mask;
    }
    return 0;
}

// the record with this key whose flood closed the cell, or -1
static inline int FmFind(const FmMemo* m, const FmKey& key, uintptr_t cell) {
    const uint32_t mask = FmMaskOf(m, cell);
    for (int r = 0; mask >> r; r++)
        if ((mask >> r & 1) && FmKeyEq(m->rec[r].key, key)) return r;
    return -1;
}

static inline const FmPair* FmPairs(const FmMemo* m, int rec) { return m->pool + m->rec[rec].off; }

// pairs = the closed cells of one failed flood (each with its node), n > 0. A memo that cannot take the whole flood is left
// as it was (FM_LONG, FM_POOLFULL, FM_RECSFULL) or cleared (FM_HASHFULL: some cells were already entered).
static inline int FmStore(FmMemo* m, const FmKey& key, const FmPair* pairs, uint32_t n) {
    if (!n) return FM_EMPTY;
    if (n > FM_MAXFLOOD) return FM_LONG;
    if (FmFind(m, key, pairs[0].cell) >= 0) return FM_DUP;  // the same closed set (a start inside a record floods that record)
    if (m->nRecs == FM_RECS) return FM_RECSFULL;
    if (m->used + n > FM_POOL) return FM_POOLFULL;
    const uint32_t bit = 1u << m->nRecs;
    for (uint32_t j = 0; j < n; j++) {
        uint32_t i = FmHash(pairs[j].cell);
        int p = 0;
        for (; p < FM_PROBE; p++, i = (i + 1) & (FM_HASH - 1)) {
            FmSlot& s = m->slot[i];
            if (s.gen != m->gen) { s.cell = pairs[j].cell, s.mask = bit, s.gen = m->gen; break; }
            if (s.cell == pairs[j].cell) { s.mask |= bit; break; }
        }
        if (p == FM_PROBE) { FmReset(m); return FM_HASHFULL; }
        m->pool[m->used + j] = pairs[j];
    }
    FmRec& r = m->rec[m->nRecs++];
    r.key = key, r.off = m->used, r.n = n;
    m->used += n;
    return FM_STORED;
}

// 1 = take the game's failure exit without the flood. mode 1 serves; mode 2 (shadow) never does. found: the start cell is in
// a record of this key; linksOk: no cell of that record carries a goal (or any other foreign link); scopeOk: the router may
// take a served answer.
static inline int FmServeRule(int mode, bool found, bool linksOk, bool scopeOk) {
    return mode == 1 && found && linksOk && scopeOk ? 1 : 0;
}

#endif
