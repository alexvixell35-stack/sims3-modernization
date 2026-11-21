// docs/676 §15: [WorldPrecompute] SmoothCut, the walk. Included by smoothcut.cpp (and through it by
// smoothcut-host-test.cpp). No Windows calls here: the game's own cell functions through a table of pointers.
//
// The lot segment test 0x6675E0 answers "clear" exactly when the segment query 0x667350 saw at least one cell and its
// largest cell value is not above the start cell's or not above the end cell's multiplier. The query's walk 0x663B20
// visits every final cell the segment crosses and its callback 0x662D30 never stops it, so a blocked segment costs as
// much as a clear one. This file is that walk again, call for call (the game's constructor, root set-up, final test,
// child step, box test and callback, in the game's order), with two ways to end early when the answer is already known:
//   cut   after a callback, when the largest value so far is above both end multipliers: every later cell can only raise
//         it, so the answer is "blocked" whatever the rest of the walk finds;
//   hint  before the walk, for each remembered blocking cell of this tree: go down from the root along its child numbers
//         with the game's child step and box test. Every box on the way passing is exactly the condition under which the
//         whole walk reaches that cell; then its value (the game's callback on it) above both end multipliers gives
//         "blocked". A hint is only a list of child numbers: nothing of an old tree is read, a stale one just fails.
// Both rest on the callback being free of lasting writes, which holds while the context's class word (+0x4C) is below 2
// or 13 (docs/98 §5.3 step 1; the caller checks it), and on the walk's depth stop (a cell that is not final at level 13
// ends the game's walk) never firing: it cannot without the game writing past its own 13 cursors (docs/676 §15.3).
#pragma once
#include <stdint.h>

enum { SC_LEVELS = 13, SC_HINTS = 4 };
static const unsigned int SC_FLT_MAX = 0x7F7FFFFFu;  // the game's "blocked" value (float at 0x11584C8)
enum { SC_WHOLE = 0, SC_CUT = 1, SC_HINT = 2 };

// thiscall functions as fastcall with an unused edx; stack arguments are the game's, floats as their bits
typedef void(__fastcall* ScCtorFn)(unsigned char* cur);                                                         // 0x669BE0
typedef void(__fastcall* ScRootFn)(unsigned char* cur, void* edx, const unsigned char* tree, unsigned int a2, unsigned int a3, unsigned int size,
                                   unsigned int a5, unsigned int a6, unsigned int a7, unsigned int a8, unsigned int node, unsigned int a10);  // 0x669C30, ret 0x28
typedef unsigned int(__fastcall* ScFinalFn)(const unsigned char* cur);                                          // 0x669CB0, result in al
typedef void(__fastcall* ScDownFn)(unsigned char* parent, void* edx, unsigned int child, unsigned char* out);   // 0x66A040, ret 8
typedef unsigned int(__fastcall* ScBoxFn)(unsigned char* cur, void* edx, unsigned int x0, unsigned int z0, unsigned int x1, unsigned int z1,
                                          unsigned int radius);                                                 // 0x66A760, ret 0x14, al
typedef unsigned int(__fastcall* ScCbFn)(unsigned char* tree, void* edx, unsigned char* ctx, unsigned char* cur, void* state);  // 0x662D30, ret 0xC, al

struct ScFns { ScCtorFn ctor; ScRootFn root; ScFinalFn fin; ScDownFn down; ScBoxFn box; ScCbFn cb; };
struct ScState { int count; unsigned int minBits, maxBits, flag; };  // the callback's block: count, least, largest, flag byte
struct ScSeg { unsigned int x0, z0, x1, z1, radius; };
struct ScHint { const unsigned char* tree; unsigned int path, len; };  // child numbers, two bits a level from the root; len 0 = empty
struct ScHints { ScHint h[SC_HINTS]; };
struct ScOut { int how; unsigned int cells, tries, hintCells; };

static inline unsigned int ScRd(const unsigned char* p, int off) { return *(const unsigned int*)(p + off); }
static inline float ScF(unsigned int bits) { return *(const float*)&bits; }
// largest value above both end multipliers (ordered compares: false with a NaN on either side, as the game's comiss + jae)
static inline bool ScAbove(unsigned int maxBits, unsigned int m0, unsigned int m1) { return ScF(maxBits) > ScF(m0) && ScF(maxBits) > ScF(m1); }
// 0x6675E0's answer from a whole walk: clear = a cell was seen and (m0 >= largest or m1 >= largest)
static inline bool ScClear(const ScState& s, unsigned int m0, unsigned int m1) {
    return s.count > 0 && (ScF(m0) >= ScF(s.maxBits) || ScF(m1) >= ScF(s.maxBits));
}

static inline void ScHintFront(ScHints* hs, int at, const ScHint& h) {
    for (int i = at; i > 0; i--) hs->h[i] = hs->h[i - 1];
    hs->h[0] = h;
}
static inline void ScHintAdd(ScHints* hs, const unsigned char* tree, unsigned int path, unsigned int len) {
    int at = SC_HINTS - 1;
    for (int i = 0; i < SC_HINTS; i++)
        if (hs->h[i].len == len && hs->h[i].path == path && hs->h[i].tree == tree) { at = i; break; }
    const ScHint h = {tree, path, len};
    ScHintFront(hs, at, h);
}

struct ScCursors { unsigned int w[SC_LEVELS + 2][9]; };  // the game keeps 13 of 0x24 bytes; level 13's child is one past them
static inline unsigned char* ScCur(ScCursors* c, unsigned int level) { return (unsigned char*)c->w[level]; }

// down the hint's child numbers. true = a final cell was reached with every box on the way passing; s holds its callback
static inline bool ScGuided(const ScFns& f, unsigned char* tree, unsigned char* ctx, const ScSeg& g, ScCursors* c, const ScHint& h, ScState* s) {
    for (unsigned int k = 1; k <= h.len; k++) {
        if (f.fin(ScCur(c, k - 1)) & 0xFF) return false;
        f.down(ScCur(c, k - 1), 0, (h.path >> (2 * (k - 1))) & 3, ScCur(c, k));
        if (!(f.box(ScCur(c, k), 0, g.x0, g.z0, g.x1, g.z1, g.radius) & 0xFF)) return false;
        if (f.fin(ScCur(c, k)) & 0xFF) {
            f.cb(tree, 0, ctx, ScCur(c, k), s);
            return true;
        }
    }
    return false;
}

// One segment query. cut = the early ends are allowed (else the walk is the game's, whole). st is the callback's block
// as the game's walk would leave it for SC_WHOLE; for SC_CUT / SC_HINT its largest value is above both end multipliers.
// nHints = how many remembered cells to try (0..SC_HINTS).
static inline void ScRun(const ScFns& f, unsigned char* tree, unsigned char* ctx, const ScSeg& g, unsigned int m0, unsigned int m1, unsigned int flag,
                         bool cut, ScHints* hints, int nHints, ScState* st, ScOut* out) {
    ScCursors c;
    unsigned int idx[SC_LEVELS + 2];
    out->how = SC_WHOLE, out->cells = 0, out->tries = 0, out->hintCells = 0;
    st->count = 0, st->minBits = SC_FLT_MAX, st->maxBits = SC_FLT_MAX, st->flag = flag & 0xFF;
    for (unsigned int i = 0; i < SC_LEVELS + 1; i++) f.ctor(ScCur(&c, i));
    f.root(ScCur(&c, 0), 0, tree, 0, 0, ScRd(tree, 0x38), 0, 0, ScRd(tree, 0x44), 0, ScRd(tree, 0x24), 0);
    if (f.fin(ScCur(&c, 0)) & 0xFF) {  // the root is the only cell
        if (f.box(ScCur(&c, 0), 0, g.x0, g.z0, g.x1, g.z1, g.radius) & 0xFF) f.cb(tree, 0, ctx, ScCur(&c, 0), st), out->cells++;
        return;
    }
    if (cut && hints) {
        for (int i = 0; i < nHints && i < SC_HINTS; i++) {
            const ScHint h = hints->h[i];
            if (!h.len || h.tree != tree) continue;
            ScState s = {0, SC_FLT_MAX, SC_FLT_MAX, flag & 0xFF};
            out->tries++;
            if (!ScGuided(f, tree, ctx, g, &c, h, &s)) continue;
            out->hintCells++;
            if (s.count != 1 || !ScAbove(s.maxBits, m0, m1)) continue;
            *st = s;
            ScHintFront(hints, i, h);
            out->how = SC_HINT;
            return;
        }
    }
    unsigned int lvl = 1;
    idx[1] = 0;
    while (lvl > 0) {
        const unsigned int i = idx[lvl];
        if (i > 3) { lvl--; continue; }
        if (f.fin(ScCur(&c, lvl - 1)) & 0xFF) break;
        f.down(ScCur(&c, lvl - 1), 0, i, ScCur(&c, lvl));
        idx[lvl] = i + 1;
        if (!(f.box(ScCur(&c, lvl), 0, g.x0, g.z0, g.x1, g.z1, g.radius) & 0xFF)) continue;
        if (f.fin(ScCur(&c, lvl)) & 0xFF) {
            const unsigned int go = f.cb(tree, 0, ctx, ScCur(&c, lvl), st) & 0xFF;
            out->cells++;
            if (!go) break;
            if (cut && ScAbove(st->maxBits, m0, m1)) {
                if (hints) {
                    unsigned int path = 0;
                    for (unsigned int k = 1; k <= lvl; k++) path |= (idx[k] - 1) << (2 * (k - 1));
                    ScHintAdd(hints, tree, path, lvl);
                }
                out->how = SC_CUT;
                return;
            }
            continue;
        }
        if (lvl >= SC_LEVELS) break;  // the game's depth stop
        idx[lvl + 1] = 0;
        lvl++;
    }
}
