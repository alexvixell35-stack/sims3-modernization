// docs/679 §5 wpgather-core.h: [WaypointSnap] Gather. Included by selftest.cpp and wpgather-host-test.cpp (the long run on
// Linux). No clock: the tests pass their own "now".
// - A model of the game's gather (0x654790) as the brute-force reference: a grid of chunks with waypoints (type, blocked
//   bit, position bytes, links into other chunks, lot waypoints whose position comes from a lot table), an area walked
//   column by column with the game's single-precision sums, bound tests and filter. The tested path plans the same area in
//   doubles (WpgPlanArea), takes each chunk's rows from the table or from the reference on a one-chunk area (as the hook
//   does with the game's gather) and must give the reference's list, row for row, whenever it serves at all.
// - Writes (blocked bits, types, lot positions, a chunk rebuilt) move the sequence, the objects change, time moves and
//   wraps; a served list must equal the reference computed at that moment.
// - An area within WPG_EDGE of a chunk border or a world bound is refused; a header change under an unchanged sequence is
//   caught; a full table starts over; an aged chunk is fetched again.
#ifndef WPGT_ROUNDS
#define WPGT_ROUNDS 3000
#endif
enum { WPGT_W = 12, WPGT_H = 9, WPGT_CH = WPGT_W * WPGT_H, WPGT_WP = 24, WPGT_LOTS = 16 };
struct WpgtWp {
    unsigned char type, dead, bx, bz, nLinks, lot;
    unsigned short linkChunk[2], linkWp[2];
};
static WpgtWp g_wpgtWp[WPGT_CH][WPGT_WP];
static unsigned int g_wpgtCount[WPGT_CH], g_wpgtOff[WPGT_CH];
static float g_wpgtLotX[WPGT_LOTS], g_wpgtLotZ[WPGT_LOTS];
static unsigned char g_wpgtLotOk[WPGT_LOTS];
static const float kWpgtOrgX = 64.0f, kWpgtOrgZ = 32.0f, kWpgtMaxX = 64.0f + WPGT_W * 32.0f - 40.0f, kWpgtMaxZ = 32.0f + WPGT_H * 32.0f - 7.5f;
static unsigned int g_wpgtSeed = 24680, g_wpgtSeq = 1;
static long g_wpgtRefCalls;
static unsigned int WpgtRnd(unsigned int n) { g_wpgtSeed = g_wpgtSeed * 1103515245u + 12345u; return ((g_wpgtSeed >> 8) & 0xFFFFFF) % n; }

static void WpgtMakeChunk(unsigned int c) {
    static const unsigned char kTypes[] = {2, 5, 7, 8, 0xB, 0xD, 0xE, 0x10, 0x10, 0x10, 0x11, 0x13, 0x15, 2};
    g_wpgtCount[c] = WpgtRnd(8) ? WpgtRnd(WPGT_WP + 1) : 0;
    g_wpgtOff[c] = c * 4096 + WpgtRnd(64) * 4;
    for (unsigned int i = 0; i < g_wpgtCount[c]; i++) {
        WpgtWp& w = g_wpgtWp[c][i];
        w.type = kTypes[WpgtRnd(sizeof(kTypes))];
        w.dead = WpgtRnd(6) == 0;
        w.bx = (unsigned char)WpgtRnd(256), w.bz = (unsigned char)WpgtRnd(256);
        w.nLinks = (unsigned char)WpgtRnd(3);
        w.lot = (unsigned char)WpgtRnd(WPGT_LOTS);
        for (int k = 0; k < 2; k++) w.linkChunk[k] = (unsigned short)WpgtRnd(WPGT_CH), w.linkWp[k] = (unsigned short)WpgtRnd(WPGT_WP);
    }
}
// the gather's type filter for flags f (0x654790, the tests on 0x4000, 5, 7 and the type)
static bool WpgtPass(unsigned int f, unsigned int t) {
    if ((f & 0x4000) && t != 0x13) return false;
    if (t == 0x11) return false;
    if ((f & 5) == 0) return (f & 7) != 2 || t == 8 || t == 5 || t == 0xB || t == 0xD;
    return t != 8 && t != 5 && t != 0xB && t != 0xE;
}
// the reference: the candidate rows of an area in the game's order, with the game's float sums. recBase = the record area.
static unsigned int WpgtRef(const WpgArea& a, unsigned int f, unsigned int recBase, WpgGameRow* out, unsigned int cap) {
    static const float kOff[7] = {-32.0f, 0.0f, 32.0f, 64.0f, 96.0f, 128.0f, 160.0f};
    unsigned int n = 0;
    g_wpgtRefCalls++;
    for (unsigned int i = 0; i < a.ex + a.tryN * 2; i++) {
        volatile float sx = kOff[a.cx + i] + a.x;
        sx = sx - (float)a.tryN * 32.0f;
        if (!(0.0f <= sx && sx < kWpgtMaxX)) continue;
        for (unsigned int j = 0; j < a.ez + a.tryN * 2; j++) {
            volatile float sz = kOff[a.cz + j] + a.z;
            sz = sz - (float)a.tryN * 32.0f;
            if (!(0.0f <= sz && sz < kWpgtMaxZ)) continue;
            volatile float qx = (sx - kWpgtOrgX) * 0.03125f, qz = (sz - kWpgtOrgZ) * 0.03125f;
            const unsigned int c = (unsigned int)(int)qx + (unsigned int)(int)qz * WPGT_W;
            assert(c < WPGT_CH);  // the tests ask no area left of the origin
            const float cornerX = (float)((c % WPGT_W) << 5) + kWpgtOrgX, cornerZ = (float)((c / WPGT_W) << 5) + kWpgtOrgZ;
            for (unsigned int w = 0; w < g_wpgtCount[c]; w++) {
                const WpgtWp& p = g_wpgtWp[c][w];
                if (p.dead || !WpgtPass(f, p.type)) continue;
                for (unsigned int k = 0; k < p.nLinks; k++) {
                    const unsigned int lc = p.linkChunk[k], lw = p.linkWp[k];
                    if (lw >= g_wpgtCount[lc] || g_wpgtWp[lc][lw].dead) continue;
                    float x = (float)p.bx * 0.125f + cornerX, z = (float)p.bz * 0.125f + cornerZ;
                    if (p.type == 2 || p.type == 0x15) {
                        if (!g_wpgtLotOk[p.lot]) break;  // no lot: the waypoint is left out
                        x = g_wpgtLotX[p.lot], z = g_wpgtLotZ[p.lot];
                    }
                    assert(n < cap);
                    WpgGameRow& r = out[n++];
                    r.s0 = r.s1 = r.s2 = 0;
                    r.x = x, r.z = z, r.wp = (unsigned short)w, r.chunk = (unsigned short)c;
                    r.rec = recBase + g_wpgtOff[c] + 2 + w * 8;
                    r.type = p.type;
                    break;
                }
            }
        }
    }
    return n;
}
struct WpgtUser {
    unsigned int flags, recBase;
    int writeInFill;  // a write goes by during the next fill
    long fills;
};
static WpgGameRow g_wpgtTmp[WPGT_WP];
static WpgRow g_wpgtFill[WPGT_WP];
static void WpgtHead(void*, unsigned int chunk, unsigned int* chunkOff, unsigned int* wpCount) { *chunkOff = g_wpgtOff[chunk], *wpCount = g_wpgtCount[chunk]; }
static int WpgtFill(void* user, unsigned int chunk, float px, float pz, const WpgRow** rows, unsigned int* n, unsigned int* chunkOff, unsigned int* wpCount) {
    WpgtUser* u = (WpgtUser*)user;
    WpgArea one = {};
    one.x = px, one.z = pz, one.cx = 1, one.ex = 1, one.cz = 1, one.ez = 1, one.tryN = 0;
    const unsigned int seq = g_wpgtSeq;
    WpgtHead(user, chunk, chunkOff, wpCount);
    const unsigned int cnt = WpgtRef(one, u->flags, u->recBase, g_wpgtTmp, WPGT_WP);
    u->fills++;
    for (unsigned int i = 0; i < cnt; i++) {
        if (g_wpgtTmp[i].chunk != chunk) return 0;
        WpgRow& w = g_wpgtFill[i];
        w.x = g_wpgtTmp[i].x, w.z = g_wpgtTmp[i].z, w.wp = g_wpgtTmp[i].wp, w.chunk = g_wpgtTmp[i].chunk, w.rec = g_wpgtTmp[i].rec - u->recBase, w.type = g_wpgtTmp[i].type;
    }
    if (u->writeInFill) u->writeInFill = 0, g_wpgtSeq++;
    *rows = g_wpgtFill;
    *n = cnt;
    return seq == g_wpgtSeq ? 1 : 2;
}
static WpgGeom WpgtGeom() {
    WpgGeom g = {};
    g.orgX = kWpgtOrgX, g.orgZ = kWpgtOrgZ, g.maxX = kWpgtMaxX, g.maxZ = kWpgtMaxZ;
    g.w = WPGT_W, g.h = WPGT_H;
    return g;
}
// the area the game's lookup 0x652D80 makes for a point and a try
static WpgArea WpgtAreaOf(float x, float z, unsigned int tryN) {
    WpgArea a = {};
    const unsigned int ux = (unsigned int)(int)x & 31, uz = (unsigned int)(int)z & 31;
    a.x = x, a.z = z, a.tryN = tryN;
    a.cx = ux < 10 ? 0 : 1, a.ex = ux < 10 || ux > 20 ? 2 : 1;
    a.cz = uz < 10 ? 0 : 1, a.ez = uz < 10 || uz > 20 ? 2 : 1;
    return a;
}

// The install's choice of originals (WpgChainPlan) on a model of the two call sites: a site is a word holding what it calls,
// a hook "calls" its original. A gather must reach the game's code in a bounded number of calls however often the install
// runs; with the hook as its own original (the 2026-11-20 hang) the walk never ends.
static int WpgtCallsToGame(unsigned int site, unsigned int hook, unsigned int orig, unsigned int game) {
    unsigned int at = site;
    for (int calls = 1; calls <= 8; calls++) {
        if (at == game) return calls;
        if (at != hook) return -1;  // neither ours nor the game's
        at = orig;
    }
    return 0;  // still in our hook after 8 calls: it calls itself
}
static void TestWpGatherChain() {
    enum : unsigned int { GAME_S = 0x0065DF90, GAME_G = 0x00654790, HOOK_S = 0x10001000, HOOK_G = 0x10002000, SNAP_S = 0x10003000, SNAP_G = 0x10004000 };
    // the model's install, as InstallWpGather does it: read the sites, plan, then write
    struct Sites { unsigned int same, gather; };
    struct Run {
        static int Install(WpgChain* ch, Sites* s) {
            if (!WpgChainPlan(ch, s->same, s->gather, HOOK_S, HOOK_G)) return 0;
            s->same = HOOK_S, s->gather = HOOK_G;
            return 1;
        }
    };
    // the plain case, then the install call a second and a third time (the duplicated line)
    WpgChain ch = {};
    Sites s = {GAME_S, GAME_G};
    assert(WpgtCallsToGame(s.gather, HOOK_G, ch.gatherOrig, GAME_G) == 1);
    assert(Run::Install(&ch, &s) == 1 && ch.done == 1 && ch.sameOrig == GAME_S && ch.gatherOrig == GAME_G);
    for (int again = 0; again < 2; again++) {
        assert(Run::Install(&ch, &s) == 0);
        assert(ch.sameOrig == GAME_S && ch.gatherOrig == GAME_G && s.same == HOOK_S && s.gather == HOOK_G);
        assert(ch.sameOrig != HOOK_S && ch.gatherOrig != HOOK_G);
        assert(WpgtCallsToGame(s.gather, HOOK_G, ch.gatherOrig, GAME_G) == 2);
        assert(WpgtCallsToGame(s.same, HOOK_S, ch.sameOrig, GAME_S) == 2);
    }
    // what the unguarded install did on its second pass: the walk shows the self-call this test exists for
    assert(WpgtCallsToGame(s.gather, HOOK_G, s.gather, GAME_G) == 0);
    // the "done" word lost (another copy of the state): the sites themselves still refuse, each alone and crossed
    const Sites bad[] = {{HOOK_S, HOOK_G}, {HOOK_S, GAME_G}, {GAME_S, HOOK_G}, {HOOK_G, GAME_G}, {GAME_S, HOOK_S}, {0, GAME_G}, {GAME_S, 0}};
    for (unsigned int i = 0; i < sizeof(bad) / sizeof(bad[0]); i++) {
        WpgChain c2 = {};
        Sites b = bad[i];
        assert(Run::Install(&c2, &b) == 0 && !c2.done && !c2.sameOrig && !c2.gatherOrig);
        assert(b.same == bad[i].same && b.gather == bad[i].gather);  // nothing written
    }
    WpgChain c3 = {};
    assert(!WpgChainPlan(&c3, GAME_S, GAME_G, 0, HOOK_G) && !WpgChainPlan(&c3, GAME_S, GAME_G, HOOK_S, 0) && !c3.done);
    // wpsnap.cpp's stubs already on the sites: chained over, and the walk goes hook -> their stub (not the game, not us)
    WpgChain c4 = {};
    Sites t = {SNAP_S, SNAP_G};
    assert(Run::Install(&c4, &t) == 1 && c4.sameOrig == SNAP_S && c4.gatherOrig == SNAP_G);
    assert(WpgtCallsToGame(t.gather, HOOK_G, c4.gatherOrig, SNAP_G) == 2 && Run::Install(&c4, &t) == 0);
}

static void TestWpGather() {
    TestWpGatherChain();
    static WpgTab tab;
    static WpgGameRow ref[WPGT_CH * WPGT_WP];
    static WpgRow out[WPGT_CH * WPGT_WP];
    memset(&tab, 0, sizeof(tab));
    for (unsigned int c = 0; c < WPGT_CH; c++) WpgtMakeChunk(c);
    for (int i = 0; i < WPGT_LOTS; i++) g_wpgtLotX[i] = (float)WpgtRnd(4000) * 0.1f, g_wpgtLotZ[i] = (float)WpgtRnd(3000) * 0.1f, g_wpgtLotOk[i] = WpgtRnd(5) != 0;
    const WpgGeom geom = WpgtGeom();
    static const unsigned int kFlags[] = {0x107, 0x307, 0x102, 0x4107, 0x100, 0x105, 0x4102};
    WpgCtx ctx = {};
    ctx.w[8] = 0x10000000;  // the record area
    unsigned int now = 0xFFFFE000u;  // the clock wraps during the run
    long served = 0, refused = 0, hits = 0, fills = 0, avoided = 0, drops = 0;
    for (long round = 0; round < WPGT_ROUNDS; round++) {
        // a write, a rebuilt chunk, other objects, time
        const unsigned int ev = WpgtRnd(150);
        if (ev == 0) { WpgtWp& w = g_wpgtWp[WpgtRnd(WPGT_CH)][WpgtRnd(WPGT_WP)]; w.dead ^= 1; g_wpgtSeq++; }
        else if (ev == 1) { WpgtWp& w = g_wpgtWp[WpgtRnd(WPGT_CH)][WpgtRnd(WPGT_WP)]; w.type = w.type == 0x10 ? 0x13 : w.type == 2 ? 0x15 : 0x10; g_wpgtSeq++; }
        else if (ev == 2) { const unsigned int l = WpgtRnd(WPGT_LOTS); g_wpgtLotX[l] += 1.5f, g_wpgtLotOk[l] = WpgtRnd(5) != 0; g_wpgtSeq++; }
        else if (ev == 3 && !WpgtRnd(4)) { WpgtMakeChunk(WpgtRnd(WPGT_CH)); g_wpgtSeq++; }
        else if (ev == 4 && !WpgtRnd(8)) ctx.w[8] += 0x1000;  // the record area moved: no sequence needed
        now += WpgtRnd(50);
        // a point: mostly inside the world, sometimes on a border, near a bound or outside
        float x = kWpgtOrgX + (float)WpgtRnd(WPGT_W * 3200 + 3200) * 0.01f, z = kWpgtOrgZ + (float)WpgtRnd(WPGT_H * 3200 + 3200) * 0.01f;
        const unsigned int kind = WpgtRnd(12);
        if (kind == 0) x = kWpgtOrgX + (float)(WpgtRnd(WPGT_W) * 32) + (float)WpgtRnd(5) * 0.002f;
        if (kind == 1) z = kWpgtOrgZ + (float)(WpgtRnd(WPGT_H) * 32) - (float)WpgtRnd(5) * 0.002f;
        if (kind == 2) x = kWpgtMaxX - 32.0f + (float)WpgtRnd(7) * 0.003f - 0.009f;
        const unsigned int tryN = WpgtRnd(3), f = kFlags[WpgtRnd(sizeof(kFlags) / sizeof(kFlags[0]))];
        WpgArea a = WpgtAreaOf(x, z, tryN);
        if (!WpgtRnd(6)) a.cx = WpgtRnd(2), a.ex = 1 + WpgtRnd(2), a.cz = WpgtRnd(2), a.ez = 1 + WpgtRnd(2);  // any class the lookup can store
        WpgPlan plan = {};
        // left of the origin's first chunk the reference itself has no chunk: the plan must refuse there
        const bool leftOfOrigin = x - 32.0f * (float)(tryN + 1) < kWpgtOrgX + 1.0f || z - 32.0f * (float)(tryN + 1) < kWpgtOrgZ + 1.0f;
        if (!WpgPlanArea(geom, a, &plan)) { refused++; continue; }
        if (leftOfOrigin) {
            // served only when every visited column lies right of the origin; then the reference is defined too
            bool ok = true;
            for (int i = 0; i < plan.n; i++) ok = ok && plan.px[i] >= kWpgtOrgX && plan.pz[i] >= kWpgtOrgZ;
            assert(ok);
        }
        if (WpgValidate(&tab, ctx, g_wpgtSeq)) drops++;
        WpgtUser u = {};
        u.flags = f, u.recBase = ctx.w[8];
        u.writeInFill = WpgtRnd(50) == 0;
        const unsigned int seq0 = g_wpgtSeq;
        WpgStats st = {};
        const int n = WpgBuild(&tab, plan, WpgFilterIx(f), now, 3000, WpgtFill, WpgtHead, &u, out, WPGT_CH * WPGT_WP, &st);
        assert(n >= 0 && !st.stale);
        if (seq0 != g_wpgtSeq) {  // a write during the call: the hook empties the table and runs the game's gather
            WpgReset(&tab);
            continue;
        }
        const unsigned int rn = WpgtRef(a, f, ctx.w[8], ref, WPGT_CH * WPGT_WP);
        assert(rn == (unsigned int)n);
        for (unsigned int i = 0; i < rn; i++) assert(WpgRowSame(ref[i], out[i], ctx.w[8]));
        served++, hits += st.hits, fills += st.fills;
        if (!st.fills && plan.n) avoided++;
    }
    assert(served > WPGT_ROUNDS / 3);
    assert(refused > 0);
    assert(hits > fills / 4);
    assert(avoided > 0);
    assert(drops > 0);

    // the plan: order (columns outside, rows inside), classes, tries, refusals
    WpgPlan p = {};
    WpgArea a = {};
    a.x = kWpgtOrgX + 3 * 32 + 16.0f, a.z = kWpgtOrgZ + 2 * 32 + 16.0f, a.cx = 1, a.ex = 1, a.cz = 1, a.ez = 1;
    int r = WpgPlanArea(geom, a, &p);
    assert(r && p.n == 1 && p.chunk[0] == 3 + 2 * WPGT_W);
    a.cx = 0, a.ex = 2, a.ez = 2;  // x: the lower neighbour too; z: the upper one
    r = WpgPlanArea(geom, a, &p);
    assert(r && p.n == 4 && p.chunk[0] == 2 + 2 * WPGT_W && p.chunk[1] == 2 + 3 * WPGT_W && p.chunk[2] == 3 + 2 * WPGT_W && p.chunk[3] == 3 + 3 * WPGT_W);
    a.tryN = 1;
    r = WpgPlanArea(geom, a, &p);
    assert(r && p.n == 16 && p.chunk[0] == 1 + 1 * WPGT_W && p.chunk[15] == 4 + 4 * WPGT_W);
    a.tryN = 2, a.cx = 1, a.ex = 1, a.ez = 1;
    a.z = kWpgtOrgZ + 1 * 32 + 16.0f;
    r = WpgPlanArea(geom, a, &p);  // the lowest row's sum is 16: inside the world's bound 0 but below the origin 32, so refused
    assert(!r);
    a.z = kWpgtOrgZ + 4 * 32 + 16.0f;
    r = WpgPlanArea(geom, a, &p);
    assert(r && p.n == 25 && p.chunk[0] == 1 + 2 * WPGT_W && p.chunk[24] == 5 + 6 * WPGT_W);
    a.tryN = 0;
    a.x = kWpgtOrgX + 3 * 32 + 0.005f;  // on a chunk border
    r = WpgPlanArea(geom, a, &p);
    assert(!r);
    a.x = kWpgtOrgX + 3 * 32 + 0.02f;
    r = WpgPlanArea(geom, a, &p);
    assert(r && p.n == 1 && p.chunk[0] == 3 + 4 * WPGT_W);
    a.x = kWpgtMaxX - 0.005f;  // at the world bound
    r = WpgPlanArea(geom, a, &p);
    assert(!r);
    a.x = kWpgtMaxX + 5.0f, a.cx = 0, a.ex = 2;  // the point's own column is outside: only the lower neighbour is visited
    r = WpgPlanArea(geom, a, &p);
    assert(r && p.n == 1 && p.chunk[0] == (unsigned int)((kWpgtMaxX + 5.0f - 32.0f - kWpgtOrgX) / 32.0f) + 4 * WPGT_W);
    a.x = kWpgtOrgX + 100.0f, a.cx = 2;  // not a class
    r = WpgPlanArea(geom, a, &p);
    assert(!r);
    a.cx = 1, a.ex = 3;
    r = WpgPlanArea(geom, a, &p);
    assert(!r);
    a.ex = 1, a.tryN = 3;
    r = WpgPlanArea(geom, a, &p);
    assert(!r);
    a.tryN = 0;
    unsigned int nanBits = 0x7FC00000u;
    memcpy(&a.x, &nanBits, 4);
    r = WpgPlanArea(geom, a, &p);
    assert(!r);
    assert(WpgFilterIx(0x107) == 7 && WpgFilterIx(0x4902) == 10 && WpgFilterIx(0x307) == WpgFilterIx(0x107) && WpgFilterIx(0x102) != WpgFilterIx(0x107));

    // the table: context, sequence, a header change the sequence did not see, age, full
    memset(&tab, 0, sizeof(tab));
    WpgCtx c1 = {}, c2 = {};
    c1.w[8] = 0x20000000, c2 = c1, c2.w[13] = 1;
    r = WpgValidate(&tab, c1, 5);
    assert(r == 1);
    r = WpgValidate(&tab, c1, 5);
    assert(r == 0);
    a = WpgArea();
    a.x = kWpgtOrgX + 5 * 32 + 16.0f, a.z = kWpgtOrgZ + 5 * 32 + 16.0f, a.cx = 1, a.ex = 2, a.cz = 1, a.ez = 1;
    r = WpgPlanArea(geom, a, &p);
    assert(r && p.n == 2);
    WpgtUser u = {};
    u.flags = 0x107, u.recBase = c1.w[8];
    WpgStats st = {};
    int n = WpgBuild(&tab, p, 7, 1000, 0, WpgtFill, WpgtHead, &u, out, WPGT_CH * WPGT_WP, &st);
    assert(n >= 0 && st.fills == 2 && st.hits == 0 && tab.used == 2);
    st = WpgStats();
    int n2 = WpgBuild(&tab, p, 7, 0x80000000u, 0, WpgtFill, WpgtHead, &u, out, WPGT_CH * WPGT_WP, &st);  // no age limit
    assert(n2 == n && st.hits == 2 && st.fills == 0);
    st = WpgStats();
    n2 = WpgBuild(&tab, p, 3, 1000, 0, WpgtFill, WpgtHead, &u, out, WPGT_CH * WPGT_WP, &st);  // other filter bits: other entries
    assert(n2 >= 0 && st.fills == 2 && tab.used == 4);
    st = WpgStats();
    n2 = WpgBuild(&tab, p, 7, 1500, 400, WpgtFill, WpgtHead, &u, out, WPGT_CH * WPGT_WP, &st);  // aged: fetched again, renewed in place
    assert(n2 == n && st.fills == 2 && st.hits == 0 && tab.used == 4);
    st = WpgStats();
    n2 = WpgBuild(&tab, p, 7, 1600, 400, WpgtFill, WpgtHead, &u, out, WPGT_CH * WPGT_WP, &st);
    assert(n2 == n && st.hits == 2);
    r = WpgValidate(&tab, c1, 6);  // a write
    assert(r == 2 && tab.used == 0 && !WpgFind(&tab, p.chunk[0], 7));
    st = WpgStats();
    n2 = WpgBuild(&tab, p, 7, 1600, 0, WpgtFill, WpgtHead, &u, out, WPGT_CH * WPGT_WP, &st);
    assert(n2 == n && st.fills == 2);
    r = WpgValidate(&tab, c2, 6);  // other objects
    assert(r == 1 && tab.used == 0);
    st = WpgStats();
    n2 = WpgBuild(&tab, p, 7, 1600, 0, WpgtFill, WpgtHead, &u, out, WPGT_CH * WPGT_WP, &st);
    assert(n2 == n && st.fills == 2);
    g_wpgtOff[p.chunk[1]] += 4;  // the chunk moved in the record area and nothing told the sequence
    st = WpgStats();
    n2 = WpgBuild(&tab, p, 7, 1600, 0, WpgtFill, WpgtHead, &u, out, WPGT_CH * WPGT_WP, &st);
    assert(n2 == -1 && st.stale == 1 && tab.used == 0);
    g_wpgtOff[p.chunk[1]] -= 4;
    u.writeInFill = 1;  // a write during a fill: the rows are used once and not stored
    st = WpgStats();
    n2 = WpgBuild(&tab, p, 7, 1600, 0, WpgtFill, WpgtHead, &u, out, WPGT_CH * WPGT_WP, &st);
    assert(n2 >= 0 && st.fills == 2 && tab.used == 1);
    n2 = WpgBuild(&tab, p, 7, 1600, 0, WpgtFill, WpgtHead, &u, out, 0, &st);  // no room in the caller's buffer
    assert(n2 == -1 || n == 0);
    // full: the slots at 3/4, then the pool
    WpgReset(&tab);
    WpgRow one = {};
    unsigned int stored = 0;
    for (unsigned int k = 0; k < WPG_SLOTS; k++)
        if (WpgStore(&tab, k, 0, &one, 1, 0, 1, 0)) stored++;
    assert(stored <= WPG_SLOTS / 4 * 3 && stored > WPG_SLOTS / 2 && tab.used == stored);
    for (unsigned int k = 0; k < 64; k++) {
        WpgSlot* s = WpgFind(&tab, k, 0);
        assert(!s || (s->n == 1 && s->key == k));
    }
    WpgReset(&tab);
    assert(!WpgFind(&tab, 1, 0) && tab.poolUsed == 0);
    tab.poolUsed = WPG_POOL - 1;
    static WpgRow two[2];
    assert(!WpgStore(&tab, 9, 0, two, 2, 0, 2, 0) && WpgStore(&tab, 9, 0, two, 1, 0, 1, 0) && tab.poolUsed == WPG_POOL);
    tab.gen = 0xFFFFFFFFu;  // the generation wraps: old slots must not come back to life
    tab.slot[5].gen = 1;
    WpgReset(&tab);
    assert(tab.gen == 1 && tab.slot[5].gen == 0);
    // rows: every field counts
    WpgGameRow g = {};
    WpgRow w = {};
    g.x = 1.5f, g.z = -0.0f, g.wp = 3, g.chunk = 9, g.rec = 0x1000 + 40, g.type = 2;
    w.x = 1.5f, w.z = -0.0f, w.wp = 3, w.chunk = 9, w.rec = 40, w.type = 2;
    assert(WpgRowSame(g, w, 0x1000));
    w.z = 0.0f;
    assert(!WpgRowSame(g, w, 0x1000));  // -0.0 and +0.0 are different rows
    w.z = -0.0f, w.wp = 4;
    assert(!WpgRowSame(g, w, 0x1000));
    w.wp = 3, w.rec = 41;
    assert(!WpgRowSame(g, w, 0x1000));
    w.rec = 40, w.type = 0x15;
    assert(!WpgRowSame(g, w, 0x1000));
    w.type = 2;
    assert(!WpgRowSame(g, w, 0x2000) && sizeof(WpgGameRow) == 32 && sizeof(WpgRow) == 20);
}
