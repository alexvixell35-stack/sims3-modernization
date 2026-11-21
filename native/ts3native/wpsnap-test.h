// docs/679 wpsnap-core.h: [WaypointSnap]. Included by selftest.cpp and wpsnap-host-test.cpp (the long run on Linux).
// No clock: the tests pass their own "now".
// - the memo against a brute-force reference: random waypoints with a land colour each; the question "same colour at the
//   nearest waypoint of A and of B" is asked for random pairs from a pool, waypoints move or change colour at random (a
//   write: the epoch moves), time moves; every memo answer must equal the reference computed at that moment;
// - a row does not answer another context, another epoch, after the cap, or a key that differs in one bit;
// - a full bucket replaces its older row; the burst bookkeeping.
#ifndef WPST_ROUNDS
#define WPST_ROUNDS 4000
#endif
enum { WPST_WP = 300, WPST_PTS = 48 };
static float g_wpstX[WPST_WP], g_wpstZ[WPST_WP];
static unsigned char g_wpstCol[WPST_WP];
static unsigned int g_wpstSeed = 97531;
static unsigned int WpstRnd(unsigned int n) { g_wpstSeed = g_wpstSeed * 1103515245u + 12345u; return ((g_wpstSeed >> 8) & 0xFFFFFF) % n; }
static int WpstNearest(float x, float z) {
    int best = 0;
    float bd = 0;
    for (int i = 0; i < WPST_WP; i++) {
        const float dx = g_wpstX[i] - x, dz = g_wpstZ[i] - z, d = dx * dx + dz * dz;
        if (i == 0 || d < bd) best = i, bd = d;
    }
    return best;
}
static bool WpstRef(float ax, float az, float bx, float bz) { return g_wpstCol[WpstNearest(ax, az)] == g_wpstCol[WpstNearest(bx, bz)]; }
static unsigned int WpstBits(float f) {
    unsigned int u = 0;
    memcpy(&u, &f, 4);
    return u;
}

static void TestWpSnap() {
    static WpsTab tab;
    memset(&tab, 0, sizeof(tab));
    static float px[WPST_PTS], pz[WPST_PTS];
    for (int i = 0; i < WPST_WP; i++) g_wpstX[i] = (float)WpstRnd(2048), g_wpstZ[i] = (float)WpstRnd(2048), g_wpstCol[i] = (unsigned char)WpstRnd(4);
    for (int i = 0; i < WPST_PTS; i++) px[i] = (float)WpstRnd(20480) * 0.1f, pz[i] = (float)WpstRnd(20480) * 0.1f;
    unsigned int epoch = 7, now = 0xFFFFF000u, ctx = 0x1234;  // the clock wraps during the run
    const unsigned int cap = 500;
    long hits = 0, asked = 0;
    for (int n = 0; n < WPST_ROUNDS; n++) {
        const unsigned int what = WpstRnd(100);
        if (what < 3) {  // a write: one waypoint moves or changes colour
            const int w = (int)WpstRnd(WPST_WP);
            if (WpstRnd(2)) g_wpstX[w] = (float)WpstRnd(2048), g_wpstZ[w] = (float)WpstRnd(2048);
            else g_wpstCol[w] = (unsigned char)WpstRnd(4);
            epoch++;
            continue;
        }
        if (what < 5) {  // another world / graph
            ctx += 0x10001;
            continue;
        }
        now += WpstRnd(40);
        const int a = (int)WpstRnd(WPST_PTS), b = (int)WpstRnd(WPST_PTS);
        const WpsKey k = {{WpstBits(px[a]), WpstBits(pz[a]), WpstBits(px[b]), WpstBits(pz[b])}};
        const bool ref = WpstRef(px[a], pz[a], px[b], pz[b]);
        bool got = !ref;
        asked++;
        if (WpsGet(&tab, k, ctx, epoch, now, cap, &got)) {
            hits++;
            assert(got == ref);
        } else {
            WpsPut(&tab, k, ctx, epoch, now, ref);
            const bool again = WpsGet(&tab, k, ctx, epoch, now, cap, &got);
            assert(again && got == ref);
        }
    }
    assert(asked > 0 && hits > 0 && hits < asked);

    // one row, every way it must not answer
    memset(&tab, 0, sizeof(tab));
    const WpsKey k = {{0x41200000u, 0x42480000u, 0x43000000u, 0x80000000u}};
    bool ans = false, r = false;
    r = WpsGet(&tab, k, 1, 2, 1000, cap, &ans);
    assert(!r);
    WpsPut(&tab, k, 1, 2, 1000, true);
    r = WpsGet(&tab, k, 1, 2, 1000, cap, &ans);
    assert(r && ans);
    r = WpsGet(&tab, k, 1, 2, 1500, cap, &ans);  // exactly the cap
    assert(r && ans);
    r = WpsGet(&tab, k, 1, 2, 1501, cap, &ans);  // past it
    assert(!r);
    r = WpsGet(&tab, k, 1, 2, 999, cap, &ans);   // a clock that went back: the unsigned age is huge
    assert(!r);
    r = WpsGet(&tab, k, 9, 2, 1000, cap, &ans);  // other context
    assert(!r);
    r = WpsGet(&tab, k, 1, 3, 1000, cap, &ans);  // a routing write
    assert(!r);
    for (int w = 0; w < 4; w++) {
        WpsKey o = k;
        o.w[w] ^= 1;  // the last bit of one coordinate
        r = WpsGet(&tab, o, 1, 2, 1000, cap, &ans);
        assert(!r);
    }
    WpsKey z = k;
    z.w[3] = 0;  // +0.0 is not -0.0 here: only identical words answer
    r = WpsGet(&tab, z, 1, 2, 1000, cap, &ans);
    assert(!r);
    WpsPut(&tab, k, 1, 2, 1200, false);  // the same key again: its row, new answer
    r = WpsGet(&tab, k, 1, 2, 1200, cap, &ans);
    assert(r && !ans);
    int used = 0;
    for (int i = 0; i < WPS_SLOTS; i++) used += tab.r[i].used;
    assert(used == 1);

    // a bucket holds two keys; a third replaces the older one
    memset(&tab, 0, sizeof(tab));
    WpsKey c[3] = {};
    int nc = 0;
    WpsKey q = {{1, 2, 3, 0}};
    const WpsRow* home = WpsBucket(&tab, q);
    for (unsigned int i = 0; nc < 3 && i < 200000; i++) {
        q.w[3] = i;
        if (WpsBucket(&tab, q) == home) c[nc++] = q;
    }
    assert(nc == 3);
    WpsPut(&tab, c[0], 1, 1, 100, true);
    WpsPut(&tab, c[1], 1, 1, 200, false);
    WpsPut(&tab, c[2], 1, 1, 300, true);
    r = WpsGet(&tab, c[0], 1, 1, 300, cap, &ans);
    assert(!r);
    r = WpsGet(&tab, c[1], 1, 1, 300, cap, &ans);
    assert(r && !ans);
    r = WpsGet(&tab, c[2], 1, 1, 300, cap, &ans);
    assert(r && ans);

    // bursts: calls within the gap are one burst; the first call after a longer gap hands the finished burst over
    WpsBurst b = {}, done = {};
    r = WpsBurstStart(&b, 1000, 3000, &done);
    assert(!r);
    WpsBurstEnd(&b, 1500, 500, false);
    b.gathers += 2, b.cands += 40, b.most = 30;
    r = WpsBurstStart(&b, 4500, 3000, &done);  // gap exactly 3000
    assert(!r);
    WpsBurstEnd(&b, 4600, 100, true);
    r = WpsBurstStart(&b, 7601, 3000, &done);
    assert(r);
    assert(done.calls == 2 && done.hits == 1 && done.us == 600 && done.gathers == 2 && done.cands == 40 && done.most == 30);
    assert(b.calls == 0 && b.us == 0 && b.gathers == 0 && b.most == 0);
    r = WpsBurstStart(&b, 99999, 3000, &done);  // nothing to report before the next burst's first call ended
    assert(!r);
}
