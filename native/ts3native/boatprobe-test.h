// --- [BoatProbe] (boatprobe.cpp, 2026-11-14): the nearest-cell pick, the result word and the message codes that need no
// game memory. Included by selftest.cpp.
static void TestBoatProbe() {
    // 8 x 6 grid. Body 1 = sea, body 2 = a pond. Flag 0x0100 = a lot covers the cell.
    //   x:  0 1 2 3 4 5 6 7
    // z0:   .  .  .  .  .  .  .  .      . land
    // z1:   .  L1 L1 L1 s1 S1 .  .      L1 body 1 under the lot, boat-open
    // z2:   .  L1 L1 L1 .  S1 .  P2     s1 body 1, no lot, shallow (map 1 closed)
    // z3:   .  L1 L1 L1 .  .  .  P2     S1 body 1, no lot, boat-open
    // z4:   .  .  .  .  .  .  .  .      P2 body 2, no lot, boat-open
    // z5:   S1 .  .  .  .  .  .  .
    const DWORD w = 8, h = 6;
    WORD c[48] = {};
    u8 m0[48] = {}, m1[48] = {};
    auto set = [&](int x, int z, WORD word, bool boat) { c[z * w + x] = word, m0[z * w + x] = 1, m1[z * w + x] = boat ? 1 : 0; };
    for (int z = 1; z <= 3; z++)
        for (int x = 1; x <= 3; x++) set(x, z, 0x0101, true);
    set(4, 1, 0x0201, false);
    set(5, 1, 0x0001, true);
    set(5, 2, 0x0001, true);
    set(7, 2, 0x0002, true);
    set(7, 3, 0x0002, true);
    set(0, 5, 0x0001, true);
    BpNear n;
    // from the lot's middle cell (2,2): same body nearest is (5,1)/(5,2) at dx 3; (5,2) is nearer (d2 9 vs 10)
    BpNearest(c, m0, m1, w, h, 2 * w + 2, 6, &n);
    assert(n.word == 0x0101 && n.p0 == 1 && n.p1 == 1);
    assert(n.same.found && n.same.dx == 3 && n.same.dz == 0 && n.same.body == 1);
    assert(n.any.found && n.any.dx == 3 && n.any.dz == 0 && n.any.body == 1);
    // the shallow cell (4,1) is never picked (map 1 closed), lot cells never (flag), land never
    BpNearest(c, m0, m1, w, h, 1 * w + 3, 6, &n);
    assert(n.same.found && n.same.dx == 2 && n.same.dz == 0);
    // radius: from (1,1) with R 2 nothing outside the lot is in reach; (0,5) is dz 4 away
    BpNearest(c, m0, m1, w, h, 1 * w + 1, 2, &n);
    assert(!n.same.found && !n.any.found);
    BpNearest(c, m0, m1, w, h, 1 * w + 1, 4, &n);
    assert(n.same.found && n.same.dx == 4 && n.same.dz == 0);  // (5,1) d2 16 before (0,5) d2 17 and (5,2) d2 17
    // another body: from the pond cell (7,3) with the sea out of reach of "same", any = itself (not under a lot, d2 0)
    BpNearest(c, m0, m1, w, h, 3 * w + 7, 1, &n);
    assert(n.same.found && n.same.dx == 0 && n.same.dz == 0 && n.any.body == 2);
    // same body wins over a nearer cell of another body: make (3,2) a pond cell under the lot, start there, sea at dx 2, pond at...
    c[2 * w + 3] = 0x0102;
    c[2 * w + 4] = 0x0002, m0[2 * w + 4] = 1, m1[2 * w + 4] = 1;  // pond cell right next to it, no lot
    c[3 * w + 3] = 0x0102;
    BpNearest(c, m0, m1, w, h, 3 * w + 3, 6, &n);
    assert(n.any.found && n.any.dx == 1 && n.any.dz == -1 && n.any.body == 2);   // (4,2), d2 2
    assert(n.same.found && n.same.dx == 1 && n.same.dz == -1);                    // the same cell: body 2 is the start's body here
    BpNearest(c, m0, m1, w, h, 1 * w + 3, 6, &n);  // body 1 start at (3,1): any = pond (4,2) d2 2, same = sea (5,1) d2 4
    assert(n.any.body == 2 && n.any.dx == 1 && n.any.dz == 1 && n.same.dx == 2 && n.same.dz == 0);
    DWORD r = BpPack(n, 1.0f, 3.5f);  // target = the same-body cell: ox = 2 * 4 + 2 - 1 = 9 m, oz = 0 + 2 - 3.5 = -1.5 m
    assert((r & 3) == 3 && (r >> 2 & 0xFF) == 18 && (r >> 10 & 0xFF) == (0xFF & (DWORD)-3) && (r >> 18 & 7) == 7);
    // no map 1: no target, and the start bits only
    BpNearest(c, m0, NULL, w, h, 1 * w + 1, 6, &n);
    assert(!n.same.found && !n.any.found && BpPack(n, 0, 0) == (1u << 18 | 1u << 20));
    // a start on land or outside a lot is not "water under a lot"
    BpNearest(c, m0, m1, w, h, 0, 6, &n);
    assert(!(BpPack(n, 0, 0) >> 20 & 1));
    BpNearest(c, m0, m1, w, h, 1 * w + 5, 6, &n);
    assert(!(BpPack(n, 0, 0) >> 20 & 1) && (BpPack(n, 2.0f, 2.0f) & 0x3FFFF) == 3);  // itself: offset 0,0
    // excluded points (boattrip): another boat on (5,1) leaves that cell and nothing else out; the pick moves to the next-nearest
    {
        BpNear q;
        const float ex1[] = {5.5f, 1.5f};
        BpNearest(c, m0, m1, w, h, 1 * w + 3, 6, &q, ex1, 1);
        assert(q.same.found && !(q.same.dx == 2 && q.same.dz == 0) && q.same.dx == 2 && q.same.dz == 1);  // (5,2), d2 5
        const float ex2[] = {5.5f, 1.5f, 5.4f, 2.6f};
        BpNearest(c, m0, m1, w, h, 1 * w + 3, 6, &q, ex2, 2);
        assert(!(q.same.found && q.same.dx == 2 && (q.same.dz == 0 || q.same.dz == 1)));
        const float ex3[] = {6.6f, 1.5f};  // 4.4 m from the centre of (5,1): not excluded
        BpNearest(c, m0, m1, w, h, 1 * w + 3, 6, &q, ex3, 1);
        assert(q.same.found && q.same.dx == 2 && q.same.dz == 0);
    }
    // messages: 1 / 2 store the point, 4 reads bits of the result word, unknown codes answer 0
    const float fx = 12.5f, fz = -3.0f;
    DWORD bx, bz;
    memcpy(&bx, &fx, 4), memcpy(&bz, &fz, 4);
    assert(BpMsg(bx, kBpHi | 1u << 16) == 1 && BpMsg(bz, kBpHi | 2u << 16) == 1 && g_bpX == 12.5f && g_bpZ == -3.0f);
    g_bpRes = 0x00100005;
    assert(BpMsg(0, kBpHi | 4u << 16) == 1 && BpMsg(1, kBpHi | 4u << 16) == 0 && BpMsg(2, kBpHi | 4u << 16) == 1 && BpMsg(20, kBpHi | 4u << 16) == 1);
    assert(BpMsg(0, kBpHi | 9u << 16) == 0);
    assert(BpMsg(0, kBpHi | 5u << 16) == 1 && BpMsg(0, kBpHi | 6u << 16) == 1 && g_bpNEx == 1 && g_bpEx[0] == 12.5f && BpMsg(0, kBpHi | 5u << 16) == 1 && g_bpNEx == 0);
    g_bpRes = 0;
}
