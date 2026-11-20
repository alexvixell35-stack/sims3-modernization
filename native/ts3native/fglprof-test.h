// docs/671: [FglProf] tallies (fglprof-core.h). Included by selftest.cpp and fglprof-host-test.cpp.
#pragma once

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

    // [FglFacing]: the regular octagon about its centre, drawn in by 1 mm; anything else is refused
    const float kC = 0.70710678f * 0.25f;
    const float centre[2] = {7.5f, 3.5f};
    float oct[16] = {0.25f, 0, kC, kC, 0, 0.25f, -kC, kC, -0.25f, 0, -kC, -kC, 0, -0.25f, kC, -kC};
    for (int i = 0; i < 8; i++) { oct[2 * i] += centre[0]; oct[2 * i + 1] += centre[1]; }
    float in[16];
    assert(FglOctagonShrink(oct, centre, FGL_SHRINK, in));
    for (int i = 0; i < 8; i++) {
        const float ox = oct[2 * i] - centre[0], oz = oct[2 * i + 1] - centre[1], ix = in[2 * i] - centre[0], iz = in[2 * i + 1] - centre[1];
        const float ro2 = ox * ox + oz * oz, ri2 = ix * ix + iz * iz;   // 0.25^2 = 0.0625, 0.249^2 = 0.062001
        assert(ri2 < ro2 && ri2 > 0.0619f && ri2 < 0.0621f && ix * oz - iz * ox < 1e-6f && iz * ox - ix * oz < 1e-6f);
    }
    // the same octagon turned by 45 degrees (points shifted by one) is the same point set; the drawn-in one is not
    float turned[16];
    for (int i = 0; i < 8; i++) { turned[2 * i] = oct[2 * ((i + 1) & 7)]; turned[2 * i + 1] = oct[2 * ((i + 1) & 7) + 1]; }
    assert(FglSamePoints(turned, oct, 0.0005f) && !FglSamePoints(in, oct, 0.0005f));
    float bad[16];
    const float off[2] = {7.52f, 3.5f};
    assert(!FglOctagonShrink(oct, off, FGL_SHRINK, bad));          // not about the candidate position (a pivot offset)
    for (int i = 0; i < 16; i++) bad[i] = oct[i];
    bad[2] += 0.01f;
    assert(!FglOctagonShrink(bad, centre, FGL_SHRINK, in));         // one point off
    for (int i = 0; i < 8; i++) { bad[2 * i] = centre[0] + (i & 1 ? 0.25f : -0.25f); bad[2 * i + 1] = centre[1] + (i & 2 ? 0.25f : -0.25f); }
    assert(!FglOctagonShrink(bad, centre, FGL_SHRINK, in));         // equal radii, not 45 degrees apart
    for (int i = 0; i < 16; i++) bad[i] = centre[i & 1];
    assert(!FglOctagonShrink(bad, centre, FGL_SHRINK, in));         // no size
    FglProof pr = FglProof();
    const unsigned pk[3] = {1, 2, 3}, pk2[3] = {1, 2, 4};
    assert(!FglProofSame(pr, oct, 5, pk));
    FglProofSet(&pr, oct, 5, pk);
    assert(FglProofSame(pr, oct, 5, pk) && !FglProofSame(pr, oct, 5, pk2) && !FglProofSame(pr, oct, 6, pk) && !FglProofSame(pr, in, 5, pk));
    printf("ok FglProf core\n");
}
