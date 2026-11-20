// docs/669 §8-§10 [RouteSpeed] ClassMemo / OpenHint: the parts with no Windows call, shared by cellspeed.cpp, its selftest
// and the freestanding host test (cellspeed-host-test.cpp). They read 32-bit game objects through plain offsets, so the
// host test builds with -m32.
//
// ClassMemo: a per-thread 2-way set cache of the results of the cell class recompute 0x66B090, keyed by everything the
// function reads besides the routing data itself (tree, quadtree node, shape, X+0x50, X+0x4C, the agent's option words
// and id lists). Cleared as a whole, O(1) by a generation bump, when a sequence it was filled under moves.
//
// OpenHint: start nodes for the sorted insert 0x61B880 of the open list. A hint is a node that was inserted into the same
// bucket of the same search; it is used only after a local proof that it is still linked in that bucket and that its
// predecessor's f is below the new node's f. In a sorted list every node before it is then below the new node as well, so
// the game's walk from the head would pass them all and reach the hint: the walk from the hint ends on the same node.
#ifndef CELLSPEED_CORE_H
#define CELLSPEED_CORE_H

#include <stdint.h>

static inline uint32_t CsRd(uintptr_t a, int off) { return *(const uint32_t*)(a + off); }
static inline float CsRdF(uintptr_t a, int off) { return *(const float*)(a + off); }

// ---------------------------------------------------------------------------------------------------------------------
// ClassMemo
// ---------------------------------------------------------------------------------------------------------------------
enum { KM_SETS = 2048, KM_WAYS = 2, KM_AGENTS = 8, KM_IDS = 16, KM_OPT_WORDS = 14, KM_D_WORDS = 16 };
enum { KM_NEW, KM_SAME, KM_EVICT };

// What the footprint class / multiplier slots and the leaf scans read of the agent [verified disassembly, docs/669 §8.2]:
// the route options object O = X+0x40 (+0x00 kind word, +0x08 / +0x0C own object id) and, when the global 0x11D9128 is
// set, the data block D = [O+0x28] (+0x08 / +0x0C the sorted held-id vector with its flag byte +0x18, +0x20 / +0x24 one
// id, +0x28..+0x34 the option words of 0x81C880, +0x38 / +0x3C the ignore list). Kept raw: O's first 0x38 bytes, D's
// first 0x40 bytes and the elements (8 bytes each) of both lists.
struct KmAgent {
    uintptr_t opts, d;
    uint32_t haveD, nIgn, nHeld;
    uint32_t o[KM_OPT_WORDS], dw[KM_D_WORDS];
    uint32_t ign[KM_IDS * 2], held[KM_IDS * 2];
};
struct KmSlot {
    uintptr_t tree, node;
    uint32_t shape, flags, cls, agent, gen;
    uint32_t mult, postCls;  // the multiplier's bits, X+0x4C after the call
    uint8_t klass, keyClear; // the class byte, 1 = the function cleared the cell's cache key (cell+0x1C high 24 bits)
};
struct KmMemo {
    uint32_t gen, seqClear, seqWrite, seqHost, rr, nAgents;
    KmAgent agent[KM_AGENTS];
    KmSlot slot[KM_SETS * KM_WAYS];
};

// an id list [begin, end) of 8-byte elements: its length, or -1 when it does not look like one or is longer than KM_IDS
static inline int KmListLen(uint32_t begin, uint32_t end) {
    if (begin == end) return 0;
    if (!begin || end < begin || ((end - begin) & 7) || (end - begin) / 8 > KM_IDS) return -1;
    return (int)((end - begin) / 8);
}

// Reads the agent behind `opts` (0 = no agent). globalSet: [0x11D9128] != 0, as 0x8292D0 tests it. false = not usable
// (a list longer than KM_IDS or of an odd shape): the caller runs the game's function.
static inline bool KmAgentRead(KmAgent* a, uintptr_t opts, bool globalSet) {
    a->opts = opts, a->d = 0, a->haveD = 0, a->nIgn = a->nHeld = 0;
    for (int i = 0; i < KM_OPT_WORDS; i++) a->o[i] = opts ? CsRd(opts, i * 4) : 0;
    for (int i = 0; i < KM_D_WORDS; i++) a->dw[i] = 0;
    if (!opts || !globalSet) return true;
    const uintptr_t d = CsRd(opts, 0x28);
    if (!d) return true;
    a->d = d, a->haveD = 1;
    for (int i = 0; i < KM_D_WORDS; i++) a->dw[i] = CsRd(d, i * 4);
    const int ni = KmListLen(a->dw[0x38 / 4], a->dw[0x3C / 4]), nh = KmListLen(a->dw[0x08 / 4], a->dw[0x0C / 4]);
    if (ni < 0 || nh < 0) return false;
    a->nIgn = (uint32_t)ni, a->nHeld = (uint32_t)nh;
    for (int i = 0; i < ni * 2; i++) a->ign[i] = CsRd(a->dw[0x38 / 4], i * 4);
    for (int i = 0; i < nh * 2; i++) a->held[i] = CsRd(a->dw[0x08 / 4], i * 4);
    return true;
}

// true = the agent behind `opts` reads now exactly as the kept record (every word, both lists)
static inline bool KmAgentSame(const KmAgent* a, uintptr_t opts, bool globalSet) {
    if (a->opts != opts) return false;
    if (!opts) return true;
    for (int i = 0; i < KM_OPT_WORDS; i++)
        if (a->o[i] != CsRd(opts, i * 4)) return false;
    const uintptr_t d = globalSet ? CsRd(opts, 0x28) : 0;
    if (a->d != d || (a->haveD != 0) != (d != 0)) return false;
    if (!d) return true;
    for (int i = 0; i < KM_D_WORDS; i++)
        if (a->dw[i] != CsRd(d, i * 4)) return false;
    for (uint32_t i = 0; i < a->nIgn * 2; i++)
        if (a->ign[i] != CsRd(a->dw[0x38 / 4], (int)i * 4)) return false;
    for (uint32_t i = 0; i < a->nHeld * 2; i++)
        if (a->held[i] != CsRd(a->dw[0x08 / 4], (int)i * 4)) return false;
    return true;
}

// the record number of the agent behind `opts`, read now; a new record when none matches; -1 = not usable or no room
static inline int KmAgentIndex(KmMemo* m, uintptr_t opts, bool globalSet) {
    for (uint32_t i = 0; i < m->nAgents; i++)
        if (KmAgentSame(&m->agent[i], opts, globalSet)) return (int)i;
    if (m->nAgents == KM_AGENTS) return -1;
    if (!KmAgentRead(&m->agent[m->nAgents], opts, globalSet)) return -1;
    return (int)m->nAgents++;
}

static inline uint32_t KmHash(uintptr_t tree, uintptr_t node, uint32_t shape, uint32_t flags, uint32_t cls, uint32_t agent) {
    uint32_t h = (uint32_t)node * 0x9E3779B1u ^ (shape + (agent << 8) + (cls << 12)) * 0x85EBCA6Bu ^ flags * 0xC2B2AE35u ^ (uint32_t)tree;
    h ^= h >> 15;
    h *= 0x2C1B3C6Du;
    h ^= h >> 13;
    return (h & (KM_SETS - 1)) * KM_WAYS;
}

// every entry and agent record gone; gen 0 never marks a live slot, so a wrap zeroes the slots first
static inline void KmReset(KmMemo* m) {
    if (++m->gen == 0) {
        for (uint32_t i = 0; i < KM_SETS * KM_WAYS; i++) m->slot[i].gen = 0;
        m->gen = 1;
    }
    m->nAgents = 0;
}

// true = the memo was cleared because a sequence moved since it was filled
static inline bool KmSync(KmMemo* m, uint32_t seqClear, uint32_t seqWrite, uint32_t seqHost) {
    if (m->gen && m->seqClear == seqClear && m->seqWrite == seqWrite && m->seqHost == seqHost) return false;
    KmReset(m);
    m->seqClear = seqClear, m->seqWrite = seqWrite, m->seqHost = seqHost;
    return true;
}

static inline bool KmSlotIs(const KmMemo* m, const KmSlot* s, uintptr_t tree, uintptr_t node, uint32_t shape, uint32_t flags,
                            uint32_t cls, uint32_t agent) {
    return s->gen == m->gen && s->node == node && s->tree == tree && s->shape == shape && s->flags == flags && s->cls == cls &&
           s->agent == agent;
}

static inline KmSlot* KmFind(KmMemo* m, uintptr_t tree, uintptr_t node, uint32_t shape, uint32_t flags, uint32_t cls, uint32_t agent) {
    KmSlot* s = m->slot + KmHash(tree, node, shape, flags, cls, agent);
    for (int w = 0; w < KM_WAYS; w++, s++)
        if (KmSlotIs(m, s, tree, node, shape, flags, cls, agent)) return s;
    return 0;
}

// keep one result of the game's function. The same key again rewrites its slot; a full set loses one entry (ways in turn)
static inline int KmStore(KmMemo* m, uintptr_t tree, uintptr_t node, uint32_t shape, uint32_t flags, uint32_t cls, uint32_t agent,
                          uint8_t klass, uint32_t mult, uint32_t postCls, bool keyClear) {
    KmSlot* const set = m->slot + KmHash(tree, node, shape, flags, cls, agent);
    KmSlot* s = 0;
    int r = KM_NEW;
    for (int w = 0; w < KM_WAYS; w++)
        if (KmSlotIs(m, set + w, tree, node, shape, flags, cls, agent)) { s = set + w, r = KM_SAME; break; }
    for (int w = 0; !s && w < KM_WAYS; w++)
        if (set[w].gen != m->gen) s = set + w;
    if (!s) s = set + (m->rr++ & (KM_WAYS - 1)), r = KM_EVICT;
    s->gen = 0;  // not live while it is rewritten
    s->tree = tree, s->node = node, s->shape = shape, s->flags = flags, s->cls = cls, s->agent = agent;
    s->klass = klass, s->mult = mult, s->postCls = postCls, s->keyClear = keyClear ? 1 : 0;
    s->gen = m->gen;
    return r;
}

// Which calls the memo looks at at all (the rest run the game's function untouched). node: cell+0x0C; key24: the cell's
// cache key as the caller set it (cell+0x1C >> 8); clearance: the small-cell clearance test can run for this cell (flag
// 0x200000, a sub-cell with a parent), which reads more than the key holds and fills the lazy polygon cache.
static inline bool KmEligible(uintptr_t x, uintptr_t node, uint32_t key24, bool clearance) {
    return x != 0 && node != 0 && key24 != 0 && !clearance;
}

// 1 = write the kept result instead of running the game's function. mode 1 serves; mode 2 (shadow) never does.
static inline int KmServeRule(int mode, bool found, bool scopeOk) { return mode == 1 && found && scopeOk ? 1 : 0; }

// ---------------------------------------------------------------------------------------------------------------------
// OpenHint
// ---------------------------------------------------------------------------------------------------------------------
// A* node [verified, docs/98 §3.3]: +0x24 f, +0x30 low u16 generation stamp and state bits 20-21 (1 = open), +0x34 open-list
// bucket index, +0x38 / +0x3C bucket links prev / next. Bucket: {head, tail, end}; `end` is the list's end mark (0).
enum { OH_SLOTS = 256, OH_SUB = 32, OH_MIN = 8, OH_TRIES = 3, OH_WALK_CAP = 1 << 20 };
struct OhSlot {
    uintptr_t bucket;
    uint32_t gen, seqClear, mask, count;  // gen: the search generation (u16) + 0x10000, so 0 = never used
    uintptr_t node[OH_SUB];
    float f[OH_SUB];
};
struct OhCtx {
    OhSlot slot[OH_SLOTS];
};

// the sub-range of f inside its bucket [idx, idx + 1): 0..OH_SUB-1 (clamped; a NaN gives 0). Only a hint for where to look
static inline uint32_t OhSub(float f, uint32_t idx) {
    const float t = (f - (float)idx) * (float)OH_SUB;
    if (!(t > 0.0f)) return 0;
    return t < (float)(OH_SUB - 1) ? (uint32_t)t : (uint32_t)(OH_SUB - 1);
}

// the slot of this bucket for this search; emptied when it last served another bucket, search or table-clear window
static inline OhSlot* OhSlotFor(OhCtx* x, uintptr_t bucket, uint32_t idx, uint32_t gen16, uint32_t seqClear) {
    OhSlot* s = &x->slot[idx & (OH_SLOTS - 1)];
    const uint32_t gen = gen16 | 0x10000;
    if (s->bucket != bucket || s->gen != gen || s->seqClear != seqClear) {
        s->bucket = bucket, s->gen = gen, s->seqClear = seqClear;
        s->mask = 0, s->count = 0;
    }
    return s;
}

// The game's walk (0x61B880): from `from`, the first node whose f is not below nf, or `end`. steps = nodes passed.
static inline uintptr_t OhWalk(uintptr_t from, uintptr_t end, float nf, uint32_t* steps) {
    uintptr_t cur = from;
    uint32_t n = 0;
    while (cur != end && nf > CsRdF(cur, 0x24) && n < OH_WALK_CAP) cur = CsRd(cur, 0x3C), n++;
    *steps = n;
    return cur;
}

// The game's link step (0x61B880 after its walk): node n goes before `cur`, or to the tail when cur is the end mark.
static inline void OhLink(uint32_t* b, uintptr_t n, uintptr_t cur) {
    const uintptr_t head = b[0], end = b[2];
    if (head != end && cur != end) {
        if (cur == head) {
            *(uint32_t*)(n + 0x38) = (uint32_t)end;
            b[0] = (uint32_t)n;
            *(uint32_t*)(n + 0x3C) = (uint32_t)cur;
            *(uint32_t*)(cur + 0x38) = (uint32_t)n;
            return;
        }
        const uintptr_t prev = CsRd(cur, 0x38);
        *(uint32_t*)(n + 0x38) = (uint32_t)prev;
        if (prev) *(uint32_t*)(prev + 0x3C) = (uint32_t)n;
        *(uint32_t*)(n + 0x3C) = (uint32_t)cur;
        *(uint32_t*)(cur + 0x38) = (uint32_t)n;
        return;
    }
    if (b[1] != end) {
        *(uint32_t*)((uintptr_t)b[1] + 0x3C) = (uint32_t)n;
        *(uint32_t*)(n + 0x38) = b[1];
        *(uint32_t*)(n + 0x3C) = b[2];
        b[1] = (uint32_t)n;
        return;
    }
    b[0] = (uint32_t)n;
    b[1] = (uint32_t)n;
    *(uint32_t*)(n + 0x38) = (uint32_t)end;
    *(uint32_t*)(n + 0x3C) = b[2];
}

// The local proof. st may start the walk for node n (f = nf, bucket index idx) when
//   - st is not n, carries this search's stamp, is open and sits in bucket idx: it is a member of this bucket's list
//     (a node leaves the list only by a pop, which closes it, or by a bucket change, which rewrites +0x34);
//   - its links agree with the list: it is the head, or its predecessor points at it;
//   - it is the head, or its predecessor's f is below nf. The list is sorted, so every node before st is below nf then,
//     and the game's walk from the head would reach st without stopping.
static inline bool OhValid(const uint32_t* b, uintptr_t st, uintptr_t n, float nf, uint32_t gen16, uint32_t idx) {
    if (!st || st == n) return false;
    const uint32_t fl = CsRd(st, 0x30);
    if ((fl & 0xFFFF) != gen16 || (fl >> 20 & 3) != 1 || CsRd(st, 0x34) != idx) return false;
    const uintptr_t p = CsRd(st, 0x38);
    if (p == b[2]) return b[0] == st;
    return CsRd(p, 0x3C) == st && CsRdF(p, 0x24) < nf;
}

// A proven start for node n, or 0: the kept nodes of the sub-ranges at and below n's, highest first, at most OH_TRIES
// proofs. A kept node that is no member any more (popped, moved) leaves the slot.
static inline uintptr_t OhStart(OhSlot* s, const uint32_t* b, uintptr_t n, float nf, uint32_t sub, uint32_t gen16, uint32_t idx) {
    uint32_t m = s->mask & (sub >= 31 ? 0xFFFFFFFFu : (2u << sub) - 1);
    for (int t = 0; m && t < OH_TRIES; t++) {
        uint32_t i = 31;
        while (!(m >> i & 1)) i--;
        m &= ~(1u << i);
        const uintptr_t st = s->node[i];
        if (OhValid(b, st, n, nf, gen16, idx)) return st;
        const uint32_t fl = st && st != n ? CsRd(st, 0x30) : 0;
        if (!st || st == n || (fl & 0xFFFF) != gen16 || (fl >> 20 & 3) != 1 || CsRd(st, 0x34) != idx) s->mask &= ~(1u << i);
    }
    return 0;
}

// after n was linked: keep it as its sub-range's hint when the sub-range has none or n's f is not above the kept one
// (so the slot tends to hold the first node of the sub-range). keep = n may be kept at all (a cell's own graph node,
// which lives until the table clear; start and goal nodes are freed between searches and are never kept).
static inline void OhNote(OhSlot* s, uintptr_t n, float nf, uint32_t sub, bool keep) {
    if (!keep) return;
    const uint32_t bit = 1u << sub;
    if (!(s->mask & bit) || nf <= s->f[sub]) s->node[sub] = n, s->f[sub] = nf, s->mask |= bit;
}

#endif
