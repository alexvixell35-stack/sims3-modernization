// docs/670: the two game-independent parts of [PlaceTestRO] (placero.cpp), shared with placero-host-test.cpp.
//
// 1. PtBlocks: the footprint type test of the placement footprint check 0xB41120 [verified disassembly 0xB411E2..0xB41221
//    and its tables: 4 targets at 0xB41280, 0x17 index bytes at 0xB41290 for type - 2]. A gathered footprint that is not
//    the placed object's own blocks the placement when its type is 2, 4, 9, 0xC, 0xD, 0x11, 0x15 or 0x17; type 0x12 blocks
//    when the routing world's byte +0x30 is set; types 0x13 and 0x18 block when the placement record's byte +0x84 is 0;
//    every other type passes.
//
// 2. PtWalk: the quadtree polygon walk 0x6641B0 [verified disassembly 0x6641B0..0x66432B] as a loop over the game's own
//    iterator helpers (constructor 0x669BE0, root set-up 0x669C30, "is terminal" 0x669CB0, child 0x66A040), with the cell
//    test and the visitor handed in. The game keeps 13 iterators (0x24 bytes each) and two 13-entry arrays (next child,
//    last child = 3) on its stack: a depth-first walk, each child cell tested against the polygon first (0x66AB70), a
//    terminal cell that overlaps handed to the visitor, a non-terminal one descended into. The visitor returning 0 ends the
//    walk. The game's loop would write a 14th iterator past its array when 13 nested non-terminal cells overlap (its
//    depth check runs after the descent); PtWalk has room for it and reports that case as PT_WALK_DEEP, so the caller
//    gives the call back to the game instead of answering.
#pragma once

static const u8 kPtTypeIdx[0x17] = {0, 3, 0, 3, 3, 3, 3, 0, 3, 3, 0, 0, 3, 3, 3, 0, 1, 2, 3, 0, 3, 0, 2};  // 0xB41290, type - 2
// worldFlag = RoutingWorld+0x30 (read before the gather, 0xB41173); recFlag = the placement record's byte +0x84
static bool PtBlocks(int type, bool worldFlag, bool recFlag) {
    const unsigned i = (unsigned)(type - 2);
    if (i > 0x16) return false;
    switch (kPtTypeIdx[i]) {
    case 0: return true;         // 0xB41201
    case 1: return worldFlag;    // 0xB4121C
    case 2: return !recFlag;     // 0xB411F8
    default: return false;       // 0xB41223
    }
}

// the game's cell iterator, 0x24 bytes: +4 Q, +8 parent node, +0xC node, +0x14 cell size, +0x18 packed cell position
// (x 12 bits, level 4 bits, y 12 bits, depth 4 bits), +0x1C sub-cell code (byte), +0x20 child index bits
struct PtIter { DWORD vt; const u8* q; const u8* parent; const u8* node; DWORD w10; float size; DWORD pos; DWORD code; DWORD w20; };
static_assert(sizeof(PtIter) == 0x24, "the game's iterator is 0x24 bytes");

struct PtWalkFns {
    void(__fastcall* ctor)(PtIter* it);                                                                    // 0x669BE0
    void(__fastcall* root)(PtIter* it, void* edx, const u8* q, DWORD parent, DWORD child, float size, DWORD x, DWORD y, DWORD level,
                           DWORD z, const u8* node, DWORD code);                                           // 0x669C30, ret 0x28
    DWORD(__fastcall* term)(const PtIter* it);                                                             // 0x669CB0
    void(__fastcall* child)(PtIter* it, void* edx, DWORD idx, PtIter* out);                                // 0x66A040, ret 8
};
typedef bool (*PtClipFn)(const PtIter* it, const void* shape);  // 0x66AB70: the polygon overlaps the iterator's cell
typedef bool (*PtVisitFn)(void* ctx, const PtIter* it);         // false ends the walk

enum { PT_WALK_OK, PT_WALK_DEEP };
enum { PT_ITERS = 13 };  // the game's count; one more here for the write it makes past its array

static int PtWalk(const PtWalkFns& f, const u8* q, const void* shape, PtClipFn clip, PtVisitFn visit, void* ctx) {
    PtIter it[PT_ITERS + 1];
    int next[PT_ITERS + 1] = {}, last[PT_ITERS + 1] = {};
    for (int i = 0; i <= PT_ITERS; i++) f.ctor(&it[i]);
    f.root(&it[0], NULL, q, 0, 0, *(const float*)(q + 0x38), 0, 0, *(const DWORD*)(q + 0x44), 0, *(const u8* const*)(q + 0x24), 0);
    next[0] = 0, last[0] = 3;
    if ((u8)f.term(&it[0])) {  // 0x66421D: the root is the only cell; the visitor's answer is not read
        if (clip(&it[0], shape)) visit(ctx, &it[0]);
        return PT_WALK_OK;
    }
    for (int d = 1; d > 0;) {  // d = the game's esi: it[d - 1] is the cell whose children are walked
        const int b = next[d - 1];
        if (b > last[d - 1]) { d--; continue; }
        if ((u8)f.term(&it[d - 1])) break;  // 0x664287: never true for a cell the walk descended into
        if (d > PT_ITERS - 1) return PT_WALK_DEEP;  // the child would be the game's 14th iterator
        f.child(&it[d - 1], NULL, (DWORD)b, &it[d]);
        next[d - 1] = b + 1;
        if (!clip(&it[d], shape)) continue;
        if ((u8)f.term(&it[d])) {
            if (!visit(ctx, &it[d])) break;
            continue;
        }
        next[d] = 0, last[d] = 3;
        d++;
    }
    return PT_WALK_OK;
}
