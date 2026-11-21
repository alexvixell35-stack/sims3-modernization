// docs/671: [FglProf] tallies (fglprof-core.h). Included by selftest.cpp and fglprof-host-test.cpp.
#pragma once

// [FglInside] (docs/682 §9): rooms of a fake lot, three levels of 16 x 16 tiles
struct FiLot { signed char room[3][16 * 16]; int calls; };
static int FiRoom(void* ctx, int tx, int tz, int level) {
    FiLot* l = (FiLot*)ctx;
    l->calls++;
    if (tx < 0 || tz < 0 || tx >= 16 || tz >= 16 || level < 0 || level > 2) return 0xFFFF;   // the game: -1 as a 16-bit word
    return l->room[level][tz * 16 + tx];
}
static unsigned FiRnd(unsigned* s) { *s = *s * 1664525u + 1013904223u; return *s >> 8; }
// One search over the lot's tiles in a fixed order on level 1: per tile up to 3 facing tests (pass = the tile's bit), each
// pass goes to the check (room on checkLevel > 0), an accepted spot ends it. skip = [FglInside] answers. Returns the tile.
static int FiSearch(FiLot* l, const bool* pass, int checkLevel, bool skip, FglIns* ins, long* tests) {
    *ins = FglIns();
    ins->on = true;
    for (int t = 0; t < 256; t++) {
        const float x = (float)(t % 16) + 0.5f, z = (float)(t / 16) + 0.5f;
        unsigned key[3] = {0, 0, 1};
        memcpy(&key[0], &x, 4); memcpy(&key[1], &z, 4);
        for (int f = 0; f < 3; f++) {
            if (FglInsTile(ins, key, FiRoom, l) && skip) { ins->answered++; continue; }
            (*tests)++;
            FglInsRan(ins, pass[t]);
            if (pass[t] && (checkLevel >= 0 && checkLevel <= 2 && l->room[checkLevel][t] > 0)) return t;
        }
    }
    return -1;
}
static void TestFglInsideCore() {
    assert(FglFloor(3.5f) == 3 && FglFloor(3.0f) == 3 && FglFloor(-0.5f) == -1 && FglFloor(0.0f) == 0 && FglFloor(127.5f) == 127);
    static FiLot lot;
    static bool pass[256];
    unsigned seed = 682;
    long skipTests = 0, eaTests = 0;
    int found = 0, wrongLevel = 0;
    for (int k = 0; k < 4000; k++) {
        memset(&lot, 0, sizeof(lot));
        const unsigned dens = FiRnd(&seed) % 40, passPct = FiRnd(&seed) % 60;
        for (int lv = 0; lv < 3; lv++)
            for (int t = 0; t < 256; t++) lot.room[lv][t] = (signed char)(FiRnd(&seed) % 100 < dens ? 1 + FiRnd(&seed) % 5 : 0);
        for (int t = 0; t < 256; t++) pass[t] = FiRnd(&seed) % 100 < passPct;
        // the check may read the tile on the search's level (1) or one either side: the answer must be the same in all three
        for (int cl = 0; cl <= 2; cl++) {
            FglIns a, b;
            long ta = 0, tb = 0;
            const int ea = FiSearch(&lot, pass, cl, false, &a, &ta);
            const int mine = FiSearch(&lot, pass, cl, true, &b, &tb);
            assert(ea == mine && tb <= ta);
            assert(!FglInsMismatch(a, ea >= 0) && !FglInsMismatch(b, mine >= 0));   // shadow never fires when the premise holds
            assert(b.ranOut == 0 && a.answered == 0 && b.tilesOut + b.tilesIn == a.tilesOut + a.tilesIn);
            skipTests += tb; eaTests += ta;
            if (ea >= 0) found++;
        }
        // a check that read two levels off would be caught by the shadow rule in some lot (the cases reach it)
        {
            static FiLot low;
            memset(&low, 0, sizeof(low));
            for (int t = 0; t < 256; t++) low.room[0][t] = lot.room[0][t];   // rooms on level 0 only; the search runs on level 2 below
            FglIns a = FglIns();
            a.on = true;
            for (int t = 0; t < 256 && !FglInsMismatch(a, true); t++) {
                const float x = (float)(t % 16) + 0.5f, z = (float)(t / 16) + 0.5f;
                unsigned key[3] = {0, 0, 2};
                memcpy(&key[0], &x, 4); memcpy(&key[1], &z, 4);
                const bool out = FglInsTile(&a, key, FiRoom, &low);
                assert(out);   // levels 1-3 have no room
                FglInsRan(&a, pass[t] && low.room[0][t] > 0);
            }
            if (FglInsMismatch(a, true)) wrongLevel++;
        }
    }
    assert(found > 1000 && skipTests * 10 < eaTests * 9 && wrongLevel > 1000);
    // the tile's verdict is asked once per tile (three levels), not per facing
    memset(&lot, 0, sizeof(lot));
    lot.room[2][5 * 16 + 3] = 4;
    FglIns s = FglIns();
    s.on = true;
    float x = 3.5f, z = 5.5f;
    unsigned key[3] = {0, 0, 1};
    memcpy(&key[0], &x, 4); memcpy(&key[1], &z, 4);
    assert(!FglInsTile(&s, key, FiRoom, &lot) && lot.calls == 3 && !FglInsTile(&s, key, FiRoom, &lot) && lot.calls == 3 && s.tilesIn == 1);
    key[2] = 0;   // the level below: no room on -1 (off the grid), 0 or 1
    assert(FglInsTile(&s, key, FiRoom, &lot) && s.tilesOut == 1 && lot.calls == 6);
    FglInsRan(&s, true);
    assert(s.ranOut == 1 && s.passOut == 1 && FglInsMismatch(s, true) && !FglInsMismatch(s, false));
    key[2] = 3;   // rooms on level 2: kept
    assert(!FglInsTile(&s, key, FiRoom, &lot));
    FglInsRan(&s, true);
    assert(!FglInsMismatch(s, true) && s.ranIn == 1 && s.passIn == 1);
    printf("ok FglInside core\n");
}

static void TestFglProfCore() {
    FglTally t;
    FglTallyReset(&t);
    const unsigned a[3] = {1, 2, 0}, b[3] = {1, 3, 0}, c[3] = {2, 3, 1};
    // tile a: 8 facings fail (first + 7 more), then the own-facing test fails
    for (int i = 0; i < 8; i++) {
        FglTileBegin(&t, a);
        FglTestEnd(&t, true, i == 0, false, i == 0 ? FGC_B45B90 : FGC_B41120, 10);
    }
    FglTileBegin(&t, a);
    FglTestEnd(&t, false, false, false, FGC_OTHER, 5);
    assert(t.tilesTested == 1 && t.tests == 9 && t.passes == 0 && t.rotTiles == 1 && t.rotExtra == 7 && t.rotExtraUs == 70 && t.rotFlip == 0);
    assert(t.fail[FGC_B45B90] == 1 && t.fail[FGC_B41120] == 7 && t.fail[FGC_OTHER] == 1);
    // tile b: first facing fails, the third passes, a fourth passes too: one flip
    FglTileBegin(&t, b);
    FglTestEnd(&t, true, true, false, FGC_B3F0A0, 10);
    FglTileBegin(&t, b);
    FglTestEnd(&t, true, false, false, FGC_B3F0A0, 10);
    FglTileBegin(&t, b);
    FglTestEnd(&t, true, false, true, -1, 10);
    FglTileBegin(&t, b);
    FglTestEnd(&t, true, false, true, -1, 10);
    assert(t.tilesTested == 2 && t.rotTiles == 2 && t.rotExtra == 10 && t.rotFlip == 1 && t.passes == 2);
    // tile c: first facing passes: later tests on it are not "extra"
    FglTileBegin(&t, c);
    FglTestEnd(&t, true, true, true, -1, 10);
    FglTileBegin(&t, c);
    FglTestEnd(&t, true, false, false, FGC_PRE, 10);
    FglTileBegin(&t, a);   // back on a: a new tile; an own-facing test alone starts no census
    FglTestEnd(&t, false, false, false, FGC_B3ECE0, 10);
    assert(t.tilesTested == 4 && t.rotTiles == 2 && t.rotExtra == 10 && t.rotFlip == 1 && t.tests == 16 && t.fail[FGC_PRE] == 1);
    // an out-of-range check id is not counted
    FglTestEnd(&t, false, false, false, 99, 1);
    long sum = 0;
    for (int i = 0; i < FGC_COUNT; i++) sum += t.fail[i];
    assert(sum == 13 && t.tests == 17);

    // repeats: same key within 10 s, newest match wins, an older one or another key does not count
    static FglPast ring[FGL_RING];
    unsigned next = 0;
    FglKey k = FglKey();
    k.idLo = 7; k.lot = 9; k.bools = 0x14;
    bool same = false;
    assert(FglRepeat(ring, &next, k, 1000, false, &same) == -1);
    assert(FglRepeat(ring, &next, k, 1500, false, &same) == 500 && same);
    assert(FglRepeat(ring, &next, k, 1600, true, &same) == 100 && !same);
    FglKey k2 = k;
    k2.pos[0] = 1;
    assert(FglRepeat(ring, &next, k2, 1700, true, &same) == -1);
    assert(FglRepeat(ring, &next, k, 1600 + kFglRepeatMs + 1, true, &same) == -1);
    for (int i = 0; i < FGL_RING; i++) {   // the ring forgets after FGL_RING other searches
        k2.pos[1] = 100u + (unsigned)i;
        assert(FglRepeat(ring, &next, k2, 20000, true, &same) == -1);
    }
    assert(FglRepeat(ring, &next, k, 20001, true, &same) == -1);
    assert(FglRepeat(ring, &next, k, 20006, true, &same) == 5 && same);

    // the flag bytes -> FindGoodLocationBooleans (PreferEmptyTiles | Routable = 0x14)
    unsigned char rec[0x60];
    for (unsigned i = 0; i < sizeof(rec); i++) rec[i] = 0;
    assert(FglBools(rec) == 0);
    rec[0x2E] = 1; rec[0x30] = 1;
    assert(FglBools(rec) == 0x14);
    rec[0x39] = 1; rec[0x3E] = 1; rec[0x33] = 1; rec[0x35] = 1;   // +0x35 is the game's own byte, no flag
    assert(FglBools(rec) == (0x14u | 0x40000u | 0x2000000u | 0x8000u));

    // [FglFacing]: the disc about a point inside a convex polygon, either winding; 0 when the point is not strictly inside
    const float sq[8] = {-1, -0.5f, 1, -0.5f, 1, 0.5f, -1, 0.5f}, sqr[8] = {-1, 0.5f, 1, 0.5f, 1, -0.5f, -1, -0.5f};
    const float c0[2] = {0, 0}, c1[2] = {0.75f, 0.25f}, c2[2] = {1.5f, 0}, c3[2] = {1, 0};
    float d = FglInradius(sq, 4, c0);
    assert(d > 0.4999f && d < 0.5001f && FglInradius(sqr, 4, c0) == d);
    d = FglInradius(sq, 4, c1);
    assert(d > 0.2499f && d < 0.2501f && FglInradius(sq, 4, c2) == 0 && FglInradius(sq, 4, c3) == 0 && FglInradius(sq, 2, c0) == 0);
    const float dart[8] = {-1, -1, 1, -1, 0.2f, 0, 1, 1};   // not convex about c0's side
    assert(FglInradius(dart, 4, c3) == 0);
    // the proof octagon lies inside its disc, and inside any polygon whose disc is larger
    float oct[16];
    const float centre[2] = {7.5f, 3.5f};
    FglDiscOctagon(centre, 0.3f, oct);
    for (int i = 0; i < 8; i++) {
        const float ox = oct[2 * i] - centre[0], oz = oct[2 * i + 1] - centre[1];
        assert(ox * ox + oz * oz < 0.3f * 0.3f + 1e-6f && ox * ox + oz * oz > 0.3f * 0.3f - 1e-4f);
    }
    d = FglInradius(oct, 8, centre);
    assert(d > 0.2771f && d < 0.2773f);   // 0.3 cos 22.5
    assert(FglSqrt(0.0625f) > 0.24999f && FglSqrt(0.0625f) < 0.25001f && FglSqrt(4.0f) > 1.9999f && FglSqrt(4.0f) < 2.0001f && FglSqrt(0) == 0);
    // facing index: whole multiples of the step only
    const float step = 0.78539813f;
    assert(FglFacingIdx(0, step) == 0 && FglFacingIdx(step * 3, step) == 3 && FglFacingIdx(step * 9, step) == 1 && FglFacingIdx(-step, step) == 7);
    assert(FglFacingIdx(step * 2.5f, step) == -1 && FglFacingIdx(1.0f, step) == -1 && FglFacingIdx(1.0f, 0) == -1);
    // learning: smallest disc over the learned facings; a facing covers a proof only with its own disc; two discs = dead
    static FglLearn ln;
    FglLearnReset(&ln);
    assert(FglLearnMin(ln, 0) == 0 && !FglLearnCovers(ln, 0, 0, 0.1f));
    FglLearnPut(&ln, 0, 0, 0.5f);
    FglLearnPut(&ln, 2, 0, 0.4f);
    FglLearnPut(&ln, 2, 1, 0.3f);
    assert(FglLearnMin(ln, 0) == 0.4f && FglLearnMin(ln, 1) == 0.3f && FglLearnMin(ln, 2) == 0 && FglLearnMin(ln, FGL_PARTS) == 0);
    assert(FglLearnCovers(ln, 0, 0, 0.399f) && FglLearnCovers(ln, 2, 0, 0.399f) && !FglLearnCovers(ln, 2, 0, 0.4f) && !FglLearnCovers(ln, 1, 0, 0.1f));
    FglLearnPut(&ln, 0, 0, 0.5002f);   // the same disc again
    assert(FglLearnCovers(ln, 0, 0, 0.399f));
    FglLearnPut(&ln, 0, 0, 0.45f);     // another disc for the same facing and part: never trusted again
    FglLearnPut(&ln, 0, 0, 0.5f);
    assert(!FglLearnCovers(ln, 0, 0, 0.1f) && FglLearnMin(ln, 0) == 0);
    FglLearnPut(&ln, 3, 0, 0);         // position not inside this facing's part: no proof for the part
    FglLearnPut(&ln, 9, 0, 1); FglLearnPut(&ln, 1, FGL_PARTS, 1); FglLearnPut(&ln, -1, 0, 1);
    assert(FglLearnMin(ln, 0) == 0 && !FglLearnCovers(ln, 3, 0, 0.01f));
    FglProof pr = FglProof();
    const unsigned pk[3] = {1, 2, 3}, pk2[3] = {1, 2, 4};
    assert(!FglProofSame(pr, oct, 5, pk));
    FglProofSet(&pr, oct, 5, pk);
    assert(FglProofSame(pr, oct, 5, pk) && !FglProofSame(pr, oct, 5, pk2) && !FglProofSame(pr, oct, 6, pk) && !FglProofSame(pr, sq, 5, pk));

    // [FglMemo]: failed tests by position and angle; a reset forgets all
    static FglMemo mm;
    FglMemoReset(&mm);
    assert(!FglMemoHas(mm, pk, 7) && FglMemoPut(&mm, pk, 7) && !FglMemoPut(&mm, pk, 7) && FglMemoHas(mm, pk, 7));
    assert(!FglMemoHas(mm, pk, 8) && !FglMemoHas(mm, pk2, 7));
    int put = 0;
    for (unsigned i = 0; i < 30000; i++) { const unsigned kk[3] = {i, i * 7u, 1}; if (FglMemoPut(&mm, kk, i & 7)) put++; }
    int has = 0;
    for (unsigned i = 0; i < 30000; i++) { const unsigned kk[3] = {i, i * 7u, 1}; if (FglMemoHas(mm, kk, i & 7)) has++; }
    assert(put == has && put > 29000 && FglMemoHas(mm, pk, 7));
    for (unsigned i = 0; i < 30000; i++) { const unsigned kk[3] = {i, i * 7u, 2}; assert(!FglMemoHas(mm, kk, i & 7)); }
    FglMemoReset(&mm);
    assert(!FglMemoHas(mm, pk, 7));
    FglMemoCtx ca = FglMemoCtx(), cb = FglMemoCtx();
    ca.idLo = cb.idLo = 5; ca.bools = cb.bools = 0x14;
    assert(FglMemoCtxEq(ca, cb));
    cb.room = 3;
    assert(!FglMemoCtxEq(ca, cb));
    assert(FglBits(1.0f) == 0x3F800000u);
    TestFglInsideCore();
    printf("ok FglProf core\n");
}
