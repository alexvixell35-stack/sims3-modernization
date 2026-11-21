// docs/676 §14: [WorldPrecompute] SmoothMemo, the table. Included by smoothmemo.cpp (and through it by
// smoothmemo-host-test.cpp). No Windows calls here: plain memory reads and one array per planning thread.
//
// One record = one run of the game's quadtree cell evaluator QuadTreeCellPos_C 0x66B090, reached through QuadCell_Multiplier
// 0x66B670 or the class reader 0x66B600 with a context whose flags differ from the cell cursor's cache key:
//   key    what the evaluator reads of the cursor and the context: the cursor's tree (+0x04), parent (+0x08), node (+0x0C),
//          size (+0x14), position word (+0x18), shape (low byte of +0x1C) and quadrant (bits 8-9 of +0x20, the only bits
//          of that word the evaluator and its callees test: 0x66A380); the context's route options object (+0x40) with a
//          hash of its option words and ids (+0x00..+0x37, as FloodMemo), its class word (+0x4C), its flags (+0x50) and
//          its lot (+0x54);
//   value  what the evaluator writes: the cursor's multiplier (+0x10), its key word (+0x1C) and class byte (+0x20), and
//          the context's class word (+0x4C, written in one branch only).
// The cursor's own +0x10, the upper 24 bits of +0x1C and the class byte are not in the key: 0x66B670 / 0x66B600 overwrite
// the key bits before the evaluator runs, and the evaluator sets +0x10 and the class byte before it reads anything.
// A table lives for one pass (the generation moves at every pass start and at every drop), so nothing needs erasing.
#pragma once
#include <stdint.h>

enum { SM_SLOTS = 4096, SM_PROBE = 8, SM_THREADS = 4 };

struct SmKey { unsigned int w04, w08, w0c, w14, w18, shape, quad, opts, optHash, cls, flags, lot; };
struct SmVal { unsigned int w10, w1c, cls8, ctxCls; };
struct SmEntry { unsigned int gen; SmKey k; SmVal v; };
struct SmTab { unsigned int gen, used; SmEntry e[SM_SLOTS]; };

static inline unsigned int SmRd(const unsigned char* p, int off) { return *(const unsigned int*)(p + off); }

// the option words and ids (+0x00..+0x37), 14 words
static inline unsigned int SmOptHash(const unsigned char* opts) {
    unsigned int h = 0x811C9DC5u;
    if (!opts) return h;
    for (int i = 0; i < 0x38; i += 4) h = (h ^ SmRd(opts, i)) * 0x01000193u, h ^= h >> 15;
    return h;
}

static inline void SmKeyOf(SmKey* k, const unsigned char* cur, const unsigned char* ctx) {
    k->w04 = SmRd(cur, 0x04), k->w08 = SmRd(cur, 0x08), k->w0c = SmRd(cur, 0x0C), k->w14 = SmRd(cur, 0x14), k->w18 = SmRd(cur, 0x18);
    k->shape = SmRd(cur, 0x1C) & 0xFF, k->quad = SmRd(cur, 0x20) & 0x300;
    k->opts = SmRd(ctx, 0x40), k->optHash = SmOptHash((const unsigned char*)(uintptr_t)k->opts);
    k->cls = SmRd(ctx, 0x4C), k->flags = SmRd(ctx, 0x50), k->lot = SmRd(ctx, 0x54);
}
static inline void SmValOf(SmVal* v, const unsigned char* cur, const unsigned char* ctx) {
    v->w10 = SmRd(cur, 0x10), v->w1c = SmRd(cur, 0x1C), v->cls8 = cur[0x20], v->ctxCls = SmRd(ctx, 0x4C);
}
// the evaluator's writes, from a record. The context word is written only where it differs (the game writes it in one branch)
static inline void SmApply(const SmVal& v, unsigned char* cur, unsigned char* ctx) {
    *(unsigned int*)(cur + 0x10) = v.w10, *(unsigned int*)(cur + 0x1C) = v.w1c, cur[0x20] = (unsigned char)v.cls8;
    if (SmRd(ctx, 0x4C) != v.ctxCls) *(unsigned int*)(ctx + 0x4C) = v.ctxCls;
}
static inline bool SmKeyEq(const SmKey& a, const SmKey& b) {
    return a.w04 == b.w04 && a.w08 == b.w08 && a.w0c == b.w0c && a.w14 == b.w14 && a.w18 == b.w18 && a.shape == b.shape && a.quad == b.quad &&
           a.opts == b.opts && a.optHash == b.optHash && a.cls == b.cls && a.flags == b.flags && a.lot == b.lot;
}
static inline bool SmValEq(const SmVal& a, const SmVal& b) { return a.w10 == b.w10 && a.w1c == b.w1c && a.cls8 == b.cls8 && a.ctxCls == b.ctxCls; }

// node pointers are aligned and position words are regular: mix every word, then finalise (the slot is the low bits)
static inline unsigned int SmHash(const SmKey& k) {
    unsigned int h = k.w0c ^ (k.w18 << 13 | k.w18 >> 19) ^ (k.w08 << 7 | k.w08 >> 25) ^ k.w04 * 0x9E3779B1u;
    h ^= (k.shape | k.quad) * 0x85EBCA6Bu ^ k.flags * 0xC2B2AE35u ^ (k.cls << 24) ^ k.optHash ^ k.w14 ^ (k.lot << 3 | k.lot >> 29) ^ k.opts;
    h ^= h >> 16, h *= 0x85EBCA6Bu, h ^= h >> 13, h *= 0xC2B2AE35u, h ^= h >> 16;
    return h;
}

// every record gone. Generation 0 marks a slot that was never written (the block comes zeroed)
static inline void SmClear(SmTab* t) {
    if (++t->gen == 0) {
        for (int i = 0; i < SM_SLOTS; i++) t->e[i].gen = 0;
        t->gen = 1;
    }
    t->used = 0;
}
static inline const SmEntry* SmFind(const SmTab* t, const SmKey& k) {
    const unsigned int h = SmHash(k);
    for (unsigned int i = 0; i < SM_PROBE; i++) {
        const SmEntry* e = &t->e[(h + i) & (SM_SLOTS - 1)];
        if (e->gen != t->gen) return 0;  // records are never erased one by one: the first free slot ends the run
        if (SmKeyEq(e->k, k)) return e;
    }
    return 0;
}
// false = the key's SM_PROBE slots are taken (not kept). A key is stored once: the caller stores only after a failed find
static inline bool SmStore(SmTab* t, const SmKey& k, const SmVal& v) {
    const unsigned int h = SmHash(k);
    for (unsigned int i = 0; i < SM_PROBE; i++) {
        SmEntry* e = &t->e[(h + i) & (SM_SLOTS - 1)];
        if (e->gen == t->gen) continue;
        e->k = k, e->v = v, e->gen = t->gen;
        t->used++;
        return true;
    }
    return false;
}
