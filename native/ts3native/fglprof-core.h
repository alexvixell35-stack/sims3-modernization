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

// ---- [FglFacing] (docs/671 §10) ----
// poly = 8 points (x, z); centre = the candidate position. True when poly is a regular octagon about centre (every radius
// within 1e-3 of the first, neighbours 45 degrees apart within 1e-2 rad by their chord, radius 0.05-5); out = the same points
// drawn towards the centre by `shrink` (absolute length). No libm: the checks use squared lengths.
const float FGL_SHRINK = 0.001f;
inline bool FglOctagonShrink(const float poly[16], const float centre[2], float shrink, float out[16]) {
    const float dx0 = poly[0] - centre[0], dz0 = poly[1] - centre[1];
    const float r2 = dx0 * dx0 + dz0 * dz0;
    if (!(r2 > 0.05f * 0.05f && r2 < 5.0f * 5.0f)) return false;
    // |r - r0| < 1e-3 <=> |r^2 - r0^2| < ~2 r0 1e-3; chord of 45 degrees: c^2 = (2 - sqrt 2) r^2
    float r = r2;   // Newton square root, enough steps for float
    for (int i = 0; i < 24; i++) r = 0.5f * (r + r2 / r);
    const float tolR2 = 2.0f * r * 1e-3f, chord2 = 0.585786438f * r2, tolC2 = 2.0f * 0.765366865f * r * (1e-2f * r);
    for (int i = 0; i < 8; i++) {
        const float dx = poly[2 * i] - centre[0], dz = poly[2 * i + 1] - centre[1];
        const float d2 = dx * dx + dz * dz;
        if (!(d2 - r2 < tolR2 && r2 - d2 < tolR2)) return false;
        const int j = (i + 1) & 7;
        const float ex = poly[2 * j] - poly[2 * i], ez = poly[2 * j + 1] - poly[2 * i + 1];
        const float c2 = ex * ex + ez * ez;
        if (!(c2 - chord2 < tolC2 && chord2 - c2 < tolC2)) return false;
    }
    if (!(shrink > 0 && shrink < 0.5f * r)) return false;
    const float k = 1.0f - shrink / r;
    for (int i = 0; i < 8; i++) {
        out[2 * i] = centre[0] + (poly[2 * i] - centre[0]) * k;
        out[2 * i + 1] = centre[1] + (poly[2 * i + 1] - centre[1]) * k;
    }
    return true;
}

// the tile whose facing tests are answered: one record (one search), one object, one position
struct FglProof { bool on; const void* rec; unsigned obj; unsigned key[3]; };
inline void FglProofSet(FglProof* p, const void* rec, unsigned obj, const unsigned key[3]) {
    p->on = true; p->rec = rec; p->obj = obj;
    p->key[0] = key[0]; p->key[1] = key[1]; p->key[2] = key[2];
}
inline bool FglProofSame(const FglProof& p, const void* rec, unsigned obj, const unsigned key[3]) {
    return p.on && p.rec == rec && p.obj == obj && p.key[0] == key[0] && p.key[1] == key[1] && p.key[2] == key[2];
}

// every point of a (8 points) lies within tol of some point of b
inline bool FglSamePoints(const float a[16], const float b[16], float tol) {
    for (int i = 0; i < 8; i++) {
        bool hit = false;
        for (int j = 0; j < 8 && !hit; j++) {
            const float dx = a[2 * i] - b[2 * j], dz = a[2 * i + 1] - b[2 * j + 1];
            hit = dx * dx + dz * dz < tol * tol;
        }
        if (!hit) return false;
    }
    return true;
}
