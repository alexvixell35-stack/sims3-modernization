// docs/671: [FglProf] pure parts (no Windows, no game memory): the per-search tallies of the FindGoodLocation profile.
// Included by fglprof.cpp (ts3native) and fglprof-host-test.cpp (Linux). Measurement only: nothing here changes a result.
//
// One search = one call of the game's 0xB470C0. It walks tiles around the start; a tile that passes the cheap tile checks
// gets up to 8 placement tests (one per allowed facing, 0xB47040) and then one more with the object's own facing (0xB47070).
// The tallies answer: how many tiles and tests a slow search runs, which check rejects, how many tests are spent on a tile
// after its first facing failed (and how often a later facing still passes there), and whether the same search repeats.
#pragma once

// the first check that answered 0 in one placement test (fail-fast: the test stops there)
enum {
    FGC_B3ECE0,   // 0xB46160's 1st check
    FGC_B45B90,   // 2nd: "TestIntersectionWithObjectsForFootprintPoly" (objects on the tiles the footprint covers)
    FGC_B3DE50,   // 3rd
    FGC_B3F0A0,   // 4th: oriented box against the physics world
    FGC_B41120,   // 5th: routing footprints of the lot level (docs/670 §2), not hooked here: 0xB46160 failed, no hooked check did
    FGC_B3DCF0,   // 6th
    FGC_B3DDC0,   // 7th
    FGC_PRE,      // 0xB463F0 answered 0 before or without 0xB46160 (lot bounds, tile kind, 0xB40DA0)
    FGC_OTHER,    // the test failed outside 0xB463F0 (0xB3CB90, 0xB46B20's bounds, 0xB41D10, 0xB3E260, 0xB424B0, 0xB45010)
    FGC_COUNT
};
enum { FGC_TIMED = 7 };  // the first 7 have a time of their own (B41120's is 0xB46160 minus the six hooked ones)

struct FglTally {
    long tilesVisited, tilesInLot;  // spiral steps; those inside the lot (the game's 0x807B30 / 0x869360 said yes)
    long tilesTested;               // tiles that reached a placement test
    long tests, passes;             // placement tests (facing + own-facing), and how many passed
    long octTests, fpTests;         // 0xB463F0 calls: the Sim octagon (0xB469C0) and footprint parts (0xB46800)
    long fail[FGC_COUNT];           // failed tests by their first failing check
    // facing census (the 0xB47040 loop only): a tile whose first tested facing failed
    long rotTiles;                  // such tiles
    long rotExtra;                  // facing tests run on them after that first one
    long rotExtraUs;                // their time
    long rotFlip;                   // such tiles where a later facing passed (the cases a "first facing only" search would lose)
    // state of the tile under test
    unsigned key[3];
    bool have, firstFailed, flipped;
};

inline void FglTallyReset(FglTally* t) { *t = FglTally(); }

// before a placement test: is this a new tile? (key = the record's position words)
inline void FglTileBegin(FglTally* t, const unsigned key[3]) {
    if (t->have && t->key[0] == key[0] && t->key[1] == key[1] && t->key[2] == key[2]) return;
    t->key[0] = key[0]; t->key[1] = key[1]; t->key[2] = key[2];
    t->have = true;
    t->firstFailed = false;
    t->flipped = false;
    t->tilesTested++;
}

// after a placement test. facing: true for the 0xB47040 loop, false for the own-facing test. firstOnTile: no facing test ran
// on this tile before this one (the caller counts them per tile).
inline void FglTestEnd(FglTally* t, bool facing, bool firstOnTile, bool pass, int failCheck, long us) {
    t->tests++;
    if (pass) t->passes++;
    else if (failCheck >= 0 && failCheck < FGC_COUNT) t->fail[failCheck]++;
    if (!facing) return;
    if (firstOnTile) {
        if (!pass) { t->firstFailed = true; t->rotTiles++; }
        return;
    }
    if (!t->firstFailed) return;
    t->rotExtra++;
    t->rotExtraUs += us;
    if (pass && !t->flipped) { t->flipped = true; t->rotFlip++; }
}

// the last searches, to see a caller's retry loop: the same object, start, constraints within kFglRepeatMs
struct FglKey {
    unsigned idLo, idHi;   // object id
    unsigned lot;          // lot pointer
    unsigned pos[3];       // start position words
    unsigned bools;        // FindGoodLocationBooleans rebuilt from the record's bytes
    unsigned dirFacing;    // DirectionToSelect << 8 | facing mask
    unsigned room;
    unsigned radius;       // MaximumSearchRadius bits
};
struct FglPast { FglKey k; unsigned tick; bool ok, used; };
enum { FGL_RING = 32 };
const unsigned kFglRepeatMs = 10000;

inline bool FglKeyEq(const FglKey& a, const FglKey& b) {
    return a.idLo == b.idLo && a.idHi == b.idHi && a.lot == b.lot && a.pos[0] == b.pos[0] && a.pos[1] == b.pos[1] && a.pos[2] == b.pos[2] &&
           a.bools == b.bools && a.dirFacing == b.dirFacing && a.room == b.room && a.radius == b.radius;
}

// the newest equal search not older than kFglRepeatMs: its age in ms and result; -1 = none. Then records this one.
inline long FglRepeat(FglPast ring[FGL_RING], unsigned* next, const FglKey& k, unsigned now, bool ok, bool* sameResult) {
    long age = -1;
    for (int i = 0; i < FGL_RING; i++) {
        const FglPast& p = ring[i];
        if (!p.used || !FglKeyEq(p.k, k)) continue;
        const unsigned d = now - p.tick;
        if (d > kFglRepeatMs) continue;
        if (age < 0 || (long)d < age) { age = (long)d; *sameResult = p.ok == ok; }
    }
    FglPast& w = ring[*next % FGL_RING];
    w.k = k; w.tick = now; w.ok = ok; w.used = true;
    (*next)++;
    return age;
}

// FindGoodLocationBooleans from the search record's flag bytes (0xB0E130 unpacks them at 0xB0E1A0..: +0x2C bit 0 ... ; bits
// 7-10, 13, 14, 20, 21 have no byte of their own and stay 0 here)
inline unsigned FglBools(const unsigned char* rec) {
    static const struct { unsigned char off, bit; } kMap[] = {
        {0x2C, 0}, {0x2D, 1}, {0x2E, 2}, {0x2F, 3}, {0x30, 4}, {0x31, 5}, {0x32, 6}, {0x36, 11}, {0x37, 12}, {0x33, 15},
        {0x34, 16}, {0x38, 17}, {0x39, 18}, {0x3A, 19}, {0x3B, 22}, {0x3C, 23}, {0x3D, 24}, {0x3E, 25},
    };
    unsigned v = 0;
    for (unsigned i = 0; i < sizeof(kMap) / sizeof(kMap[0]); i++)
        if (rec[kMap[i].off]) v |= 1u << kMap[i].bit;
    return v;
}

// ---- [FglFacing] (docs/671 §10-§11) ----
const float FGL_MARGIN = 0.001f;     // the proof octagon stays this far inside the smallest learned disc
const float FGL_MIN_DISC = 0.02f;    // no proof on a smaller disc
enum { FGL_PARTS = 4 };

inline float FglSqrt(float v) {
    if (!(v > 0)) return 0;
    float r = v > 1 ? v : 1;
    for (int i = 0; i < 40; i++) r = 0.5f * (r + v / r);
    return r;
}

// pts = n points (x, z) of a convex polygon, either winding. The distance from c to its nearest edge when c is strictly
// inside; 0 otherwise (outside, on an edge, not convex about c, a degenerate edge).
inline float FglInradius(const float* pts, int n, const float c[2]) {
    if (n < 3) return 0;
    float best = 0;
    int sign = 0;
    for (int i = 0; i < n; i++) {
        const int j = i + 1 < n ? i + 1 : 0;
        const float ex = pts[2 * j] - pts[2 * i], ez = pts[2 * j + 1] - pts[2 * i + 1];
        const float cr = ex * (c[1] - pts[2 * i + 1]) - ez * (c[0] - pts[2 * i]);
        const float len = FglSqrt(ex * ex + ez * ez);
        if (!(len > 1e-6f)) return 0;
        const int sg = cr > 0 ? 1 : cr < 0 ? -1 : 0;
        if (!sg || (sign && sg != sign)) return 0;
        sign = sg;
        const float d = (cr > 0 ? cr : -cr) / len;
        if (i == 0 || d < best) best = d;
    }
    return best;
}

// a regular octagon of circumradius r about c: inside the disc of radius r
inline void FglDiscOctagon(const float c[2], float r, float out[16]) {
    const float k = 0.70710678f * r;
    const float d[16] = {r, 0, k, k, 0, r, -k, k, -r, 0, -k, -k, 0, -r, k, -k};
    for (int i = 0; i < 8; i++) { out[2 * i] = c[0] + d[2 * i]; out[2 * i + 1] = c[1] + d[2 * i + 1]; }
}

// facing index of an angle: a whole multiple of step (within 1e-3 rad), -1 otherwise
inline int FglFacingIdx(float angle, float step) {
    if (!(step > 0.1f && step < 7.0f)) return -1;
    const float q = angle / step;
    const int k = (int)(q < 0 ? q - 0.5f : q + 0.5f);
    const float d = angle - (float)k * step;
    if (!(d < 1e-3f && d > -1e-3f)) return -1;
    return k & 7;
}

// per facing index and part ordinal: the disc learned in this search; a part seen with two different discs is dead
struct FglLearn { float r[8][FGL_PARTS]; bool seen[8][FGL_PARTS], dead[8][FGL_PARTS]; };
inline void FglLearnReset(FglLearn* l) { *l = FglLearn(); }
inline void FglLearnPut(FglLearn* l, int idx, int part, float r) {
    if (idx < 0 || idx > 7 || part < 0 || part >= FGL_PARTS) return;
    if (l->dead[idx][part]) return;
    if (!l->seen[idx][part]) { l->seen[idx][part] = true; l->r[idx][part] = r; return; }
    const float d = l->r[idx][part] - r;
    if (d > 0.0005f || d < -0.0005f) { l->dead[idx][part] = true; l->r[idx][part] = 0; }
}
// the smallest disc of the part over the facings learned so far (0: none learned, or one of them has no disc)
inline float FglLearnMin(const FglLearn& l, int part) {
    if (part < 0 || part >= FGL_PARTS) return 0;
    float m = 0;
    bool any = false;
    for (int i = 0; i < 8; i++) {
        if (!l.seen[i][part]) continue;
        if (!any || l.r[i][part] < m) m = l.r[i][part];
        any = true;
    }
    return any ? m : 0;
}
// facing idx's part holds the proof octagon of radius r (with half the margin to spare)
inline bool FglLearnCovers(const FglLearn& l, int idx, int part, float r) {
    return idx >= 0 && idx < 8 && part >= 0 && part < FGL_PARTS && l.seen[idx][part] && !l.dead[idx][part] &&
           l.r[idx][part] >= r + 0.5f * FGL_MARGIN;
}

// the tile whose facing tests are answered: one record (one search), one object, one position
struct FglProof { bool on; const void* rec; unsigned obj; unsigned key[3]; int part; float radius; };
inline void FglProofSet(FglProof* p, const void* rec, unsigned obj, const unsigned key[3]) {
    p->on = true; p->rec = rec; p->obj = obj;
    p->key[0] = key[0]; p->key[1] = key[1]; p->key[2] = key[2];
}
inline bool FglProofSame(const FglProof& p, const void* rec, unsigned obj, const unsigned key[3]) {
    return p.on && p.rec == rec && p.obj == obj && p.key[0] == key[0] && p.key[1] == key[1] && p.key[2] == key[2];
}

// ---- [FglMemo] (docs/671 §11): failed facing tests by candidate position words and angle bits ----
inline unsigned FglBits(float f) { unsigned u = 0; const unsigned char* p = (const unsigned char*)&f; for (int i = 3; i >= 0; i--) u = u << 8 | p[i]; return u; }
struct FglMemoCtx { unsigned idLo, idHi, lot, bools, room, ignore; };
inline bool FglMemoCtxEq(const FglMemoCtx& a, const FglMemoCtx& b) {
    return a.idLo == b.idLo && a.idHi == b.idHi && a.lot == b.lot && a.bools == b.bools && a.room == b.room && a.ignore == b.ignore;
}
enum { FGL_MEMO_N = 1 << 16 };   // slots; a full neighbourhood drops the new entry (the test then runs)
struct FglMemoE { unsigned k[4]; unsigned gen; };
struct FglMemo { FglMemoE e[FGL_MEMO_N]; unsigned gen; };
inline void FglMemoReset(FglMemo* m) {
    if (++m->gen == 0) { for (int i = 0; i < FGL_MEMO_N; i++) m->e[i].gen = 0; m->gen = 1; }
}
inline unsigned FglMemoHash(const unsigned key[3], unsigned ang) {
    unsigned h = 2166136261u;
    h = (h ^ key[0]) * 16777619u; h = (h ^ key[1]) * 16777619u; h = (h ^ key[2]) * 16777619u; h = (h ^ ang) * 16777619u;
    return h ^ h >> 15;
}
inline bool FglMemoHas(const FglMemo& m, const unsigned key[3], unsigned ang) {
    const unsigned h = FglMemoHash(key, ang);
    for (unsigned i = 0; i < 8; i++) {
        const FglMemoE& e = m.e[(h + i) & (FGL_MEMO_N - 1)];
        if (e.gen != m.gen) return false;
        if (e.k[0] == key[0] && e.k[1] == key[1] && e.k[2] == key[2] && e.k[3] == ang) return true;
    }
    return false;
}
inline bool FglMemoPut(FglMemo* m, const unsigned key[3], unsigned ang) {
    const unsigned h = FglMemoHash(key, ang);
    for (unsigned i = 0; i < 8; i++) {
        FglMemoE& e = m->e[(h + i) & (FGL_MEMO_N - 1)];
        if (e.gen == m->gen) {
            if (e.k[0] == key[0] && e.k[1] == key[1] && e.k[2] == key[2] && e.k[3] == ang) return false;
            continue;
        }
        e.k[0] = key[0]; e.k[1] = key[1]; e.k[2] = key[2]; e.k[3] = ang; e.gen = m->gen;
        return true;
    }
    return false;
}
