// docs/676 §14 worldalt.cpp / worldalt-core.h: [WorldPrecompute] WorldAlt. Included by selftest.cpp (short run) and
// worldalt-host-test.cpp (long run on Linux). Nothing here reads a clock.
//
// A random routing world in the game's layouts (R: chunks, records, packed references, edge bytes, blocked bits; G: the
// two shoreline trees and the body cell counts; the A* container's closed bits and tuning) and a stand-in for the world
// A* 0x676260 that calls our four hook bodies where the game's code does: WaBegin for the first heuristic call of a
// loop, WaHeur for every generated node, WaPush for its push, WaPathOut at the goal. Its cost function restates the
// game's rules for modes 1 and 3 from the decompile (docs/99 §10.1), separately from the lower-bound graph's factors:
// hub rules (flat 5,000,000, 30, 10, trespass x 10 / x 1000, walls by lot-edge group), the parent's offsets for a hub's
// from position, keep-type, blocked lot edges, random walls for the polygon and point tests, water links at
// max(10 x cells, 1) or the water-lot value, link admission by slot, the retry flag, both cost sets.
// Checked:
//  - the bound of every node against a brute-force shortest distance to the goal on the true costs of that search (the
//    cheapest parent for a hub's from position), and the triangle rule along every true edge: random blocking, flags,
//    cost sets and tuning, cheap and dear water bodies, one-way edges, records without edge bytes;
//  - where the stand-in's own heuristic is consistent (dear water, no parent offsets): the path cost with the bound ==
//    the plain search's == the brute force's;
//  - the build in slices (graph, then one landmark per loop), the graph block freed at the end, invalidation by a cell
//    count, a new link, a type flip to 0x15, a tuning value, a record edit at the periodic land check;
//  - every reason a loop takes no bound; [RouteAsync]'s budget stub in front of the cost function;
//  - shadow: both runs compared, the planner context and the closed bits as before, a budget cut, the rerun after a
//    circle loop not compared twice, a deliberately inadmissible bound reported as dearer;
//  - Windows only: the install checks on a copy of the sites' bytes and the two thunks called as the A* calls them.
#ifndef WAT_WORLDS
#define WAT_WORLDS 10
#define WAT_QUERIES 10
#endif

enum { WAT_MAXN = 640, WAT_MAXDEG = 10, WAT_RAW = 1 << 15, WAT_MAXLINK = 512, WAT_NODES = WAT_MAXN + WA_BODIES, WAT_POOL = 1 << 16 };
static const float kWatWall = 1e8f;
struct WatWp { int c, i, n, group; u8 type, bx, bz; bool noEdge, blocked; int nb[WAT_MAXDEG]; u8 eb[WAT_MAXDEG]; };
struct WatLink { int wp; u8 body; bool in30, in4C; };
static WatWp g_watWp[WAT_MAXN];
static WatLink g_watLink[WAT_MAXLINK];
static int g_watN, g_watW, g_watH, g_watLinks, g_watIdx[32][64], g_watCnt[32];
static u8 g_watRaw[WAT_RAW], g_watR[0x20], g_watG[0x800], g_watTree[2][WAT_MAXLINK * 0x20], g_watCtr[0x80], g_watClosed[WAT_NODES / 8 + 8];
static DWORD g_watOff[32], g_watRawN, g_watCells[256];
static DWORD g_watSeed = 97531;
static DWORD WatRnd(DWORD n) { g_watSeed = g_watSeed * 1103515245 + 12345; return ((g_watSeed >> 8) & 0xFFFFFF) % n; }
static DWORD WatHash(DWORD a, DWORD b, DWORD c) {
    DWORD h = a * 0x9E3779B1u ^ (b + 0x7F4A7C15u) * 0x85EBCA6Bu ^ (c + 0x165667B1u) * 0xC2B2AE35u;
    h ^= h >> 15, h *= 0x2C1B3C6Du, h ^= h >> 12;
    return h;
}
static void WatF(u8* at, float v) { memcpy(at, &v, 4); }
static void WatD(u8* at, DWORD v) { memcpy(at, &v, 4); }
static void WatP(u8* at, const void* v) { memcpy(at, &v, sizeof(v)); }
static bool WatLotEdge(u8 t) { return t == 2 || t == 0x15; }
static float WatX(const WatWp& w) { return (float)(w.c % g_watW) * 32.0f + (float)w.bx * 0.125f; }
static float WatZ(const WatWp& w) { return (float)(w.c / g_watW) * 32.0f + (float)w.bz * 0.125f; }

// the record area, R, both trees and G from g_watWp / g_watLink / g_watCells
static u8* WatTreeBuild(u8* nodes, int lo, int hi, u8* parent) {
    if (lo > hi) return NULL;
    const int m = (lo + hi) / 2;
    u8* n = nodes + m * 0x20;
    WatP(n + 8, parent);
    WatP(n + 4, WatTreeBuild(nodes, lo, m - 1, n));
    WatP(n, WatTreeBuild(nodes, m + 1, hi, n));
    return n;
}
static void WatTreeSet(u8* anchor, u8* nodes, int n) {
    memset(anchor, 0, 0x14);
    if (!n) { WatP(anchor, anchor), WatP(anchor + 4, anchor); return; }
    WatP(anchor + 8, WatTreeBuild(nodes, 0, n - 1, anchor));
    WatP(anchor, nodes + (n - 1) * 0x20), WatP(anchor + 4, nodes);
    WatD(anchor + 0x10, (DWORD)n);
}
static void WatSerialize() {
    DWORD at = 0;
    int k = 0;
    for (int c = 0; c < g_watW * g_watH; c++) {
        g_watOff[c] = at;
        const int cnt = g_watCnt[c];
        u8* ch = g_watRaw + at;
        ch[0] = (u8)cnt, ch[1] = (u8)(cnt >> 8);
        DWORD rel = 2 + 2 * (DWORD)cnt;
        for (int i = 0; i < cnt; i++, k++) {
            const WatWp& w = g_watWp[k];
            assert(w.c == c && w.i == i);
            ch[2 + 2 * i] = (u8)rel, ch[3 + 2 * i] = (u8)(rel >> 8);
            u8* rec = ch + rel;
            rec[0] = (u8)w.n, rec[1] = (u8)(w.type | (w.noEdge ? 0 : 0x40) | (w.blocked ? 0x80 : 0)), rec[2] = 0, rec[3] = w.bx, rec[4] = w.bz;
            for (int e = 0; e < w.n; e++) {
                const WatWp& t = g_watWp[w.nb[e]];
                const int dx = t.c % g_watW - c % g_watW, dz = t.c / g_watW - c / g_watW;
                const DWORD ref = (DWORD)t.i | ((DWORD)(dx + 3) << 10) | ((DWORD)(dz + 3) << 13);
                rec[5 + 2 * e] = (u8)ref, rec[6 + 2 * e] = (u8)(ref >> 8);
                if (!w.noEdge) rec[5 + 2 * w.n + e] = w.eb[e];
            }
            rel += 5 + 2 * (DWORD)w.n + (w.noEdge ? 0 : (DWORD)w.n);
        }
        at += rel;
        assert(at + 64 < WAT_RAW);
    }
    g_watRawN = at;
    WatD(g_watR, (DWORD)g_watW), WatD(g_watR + 4, (DWORD)g_watH), WatP(g_watR + 8, g_watOff), WatP(g_watR + 0xC, g_watRaw), WatD(g_watR + 0x10, at);
    memset(g_watG, 0, sizeof(g_watG));
    int n30 = 0, n4C = 0;
    for (int i = 0; i < g_watLinks; i++) {  // tree 0x30: keyed by waypoint, in waypoint order as generated
        const WatLink& l = g_watLink[i];
        if (!l.in30) continue;
        u8* nd = g_watTree[0] + 0x20 * n30++;
        memset(nd, 0, 0x20);
        WatD(nd + 0x10, ((DWORD)g_watWp[l.wp].c << 16) | (DWORD)g_watWp[l.wp].i), nd[0x14] = l.body;
    }
    for (int body = 0; body < 256; body++)  // tree 0x4C: keyed by body
        for (int i = 0; i < g_watLinks; i++) {
            const WatLink& l = g_watLink[i];
            if (!l.in4C || l.body != body) continue;
            u8* nd = g_watTree[1] + 0x20 * n4C++;
            memset(nd, 0, 0x20);
            nd[0x10] = l.body, WatD(nd + 0x14, ((DWORD)g_watWp[l.wp].c << 16) | (DWORD)g_watWp[l.wp].i);
        }
    WatTreeSet(g_watG + 0x30, g_watTree[0], n30), WatTreeSet(g_watG + 0x4C, g_watTree[1], n4C);
    for (int b = 0; b < 0x80; b++) WatD(g_watG + 0x3A0 + 8 * b, g_watCells[b]);
}
static void WatLinkNodes(int a, int b, u8 e, u8 back) {
    WatWp &x = g_watWp[a], &y = g_watWp[b];
    if (a == b || x.n == WAT_MAXDEG || y.n == WAT_MAXDEG) return;
    for (int k = 0; k < x.n; k++)
        if (x.nb[k] == b) return;
    x.nb[x.n] = b, x.eb[x.n++] = e;
    if (back == 0xFF) return;  // one way
    for (int k = 0; k < y.n; k++)
        if (y.nb[k] == a) return;
    y.nb[y.n] = a, y.eb[y.n++] = back;
}
static u8 WatEdgeByte(u8 a, u8 b) {
    if (a == 0x0D && b == 0x0D) return 3;
    if ((a == 1 && WatLotEdge(b)) || (b == 1 && WatLotEdge(a))) return 1;
    if (WatLotEdge(a) && WatLotEdge(b)) return 2;
    static const u8 k[12] = {7, 7, 7, 7, 4, 5, 6, 8, 3, 2, 1, 0};
    return k[WatRnd(12)];
}
// dear = every water link costs more than any straight line in the world (the stand-in's heuristic stays consistent)
static void WatWorld(bool dear) {
    static const u8 kTypes[18] = {7, 7, 7, 7, 0x0D, 0x0D, 0x0D, 0x10, 0x10, 0x13, 0x13, 0x13, 2, 2, 0x15, 0x15, 1, 0x0E};
    g_watW = 2 + (int)WatRnd(4), g_watH = 2 + (int)WatRnd(4), g_watN = 0, g_watLinks = 0;
    int island[32] = {};
    const int islands = 1 + (int)WatRnd(3);
    for (int c = 0; c < g_watW * g_watH; c++) {
        island[c] = (int)WatRnd((DWORD)islands);
        g_watCnt[c] = WatRnd(8) ? (int)WatRnd(28) : 0;
        if (g_watN + g_watCnt[c] > WAT_MAXN) g_watCnt[c] = 0;
        for (int i = 0; i < g_watCnt[c]; i++) {
            WatWp& w = g_watWp[g_watN];
            memset(&w, 0, sizeof(w));
            w.c = c, w.i = i, w.type = kTypes[WatRnd(18)], w.bx = (u8)WatRnd(256), w.bz = (u8)WatRnd(256);
            w.noEdge = WatRnd(30) == 0, w.blocked = WatLotEdge(w.type) && WatRnd(4) == 0, w.group = WatLotEdge(w.type) ? (int)WatRnd(4) : 8;
            g_watIdx[c][i] = g_watN++;
        }
    }
    for (int a = 0; a < g_watN; a++)
        for (int e = 2 + (int)WatRnd(4); e > 0; e--) {
            const int cx = g_watWp[a].c % g_watW + (int)WatRnd(3) - 1, cz = g_watWp[a].c / g_watW + (int)WatRnd(3) - 1;
            if (cx < 0 || cz < 0 || cx >= g_watW || cz >= g_watH) continue;
            const int c = cz * g_watW + cx;
            if (island[c] != island[g_watWp[a].c] || !g_watCnt[c]) continue;
            const int b = g_watIdx[c][WatRnd((DWORD)g_watCnt[c])];
            const u8 eb = WatEdgeByte(g_watWp[a].type, g_watWp[b].type);
            WatLinkNodes(a, b, eb, WatRnd(12) == 0 ? (u8)0xFF : WatRnd(15) == 0 ? WatEdgeByte(g_watWp[b].type, g_watWp[a].type) : eb);
        }
    memset(g_watCells, 0, sizeof(g_watCells));
    for (int b = 1; b < 6; b++) g_watCells[b] = dear ? 300 + WatRnd(3000) : WatRnd(25);
    for (int a = 0; a < g_watN && g_watLinks < WAT_MAXLINK; a++) {
        const u8 t = g_watWp[a].type;
        const bool shore = t == 0x13 || t == 0x15;
        if (!(shore ? WatRnd(10) < 7 : WatRnd(60) == 0)) continue;
        WatLink& l = g_watLink[g_watLinks++];
        l.wp = a, l.body = dear || WatRnd(12) ? (u8)(1 + WatRnd(5)) : (u8)0x85;  // 0x85: no cell count, the link costs 1
        l.in30 = shore && WatRnd(25) != 0, l.in4C = !l.in30 || WatRnd(25) != 0;
    }
    memset(g_watCtr, 0, sizeof(g_watCtr));
    WatD(g_watCtr + 8, sizeof(g_watClosed)), WatP(g_watCtr + 0x10, g_watClosed);
    for (int s = 0; s < 2; s++) {
        u8* t = g_watCtr + (s ? 0x4C : 0x28);
        // dear worlds keep every factor at 1 per metre or more (the straight line stays a consistent heuristic); the others
        // also take keep-type and trespass below 1
        WatF(t, (dear ? 1.0f : 0.6f) + (float)WatRnd(50) * 0.01f), WatF(t + 4, (dear ? 1.0f : 0.5f) + (float)WatRnd(100) * 0.01f), WatF(t + 8, 1.2f + (float)WatRnd(280) * 0.01f);
        WatF(t + 12, 1.5f + (float)WatRnd(850) * 0.01f), WatF(t + 16, 1.5f + (float)WatRnd(650) * 0.01f);
        WatF(t + 20, dear ? 2000.0f + (float)WatRnd(1000) : 0.5f + (float)WatRnd(95) * 0.1f);
    }
    WatSerialize();
}

// ---- the stand-in's cost function (the game's rules for one search) ----
struct WatS {
    int mode, set, startHub, goalHub;  // cost mode 1 or 3; 0 SHORT, 1 LONG; hubs as waypoint indices (-1 none)
    DWORD flags, seed, wallPct;
    DWORD startMask, goalMask;         // lot-edge groups of the start / goal lot
    bool parentPos;                    // a hub's from position and group test come from the parent (as the game)
    bool gameBodyH;                    // heuristic at a body = the game's (a link cost), else 0
    bool capNested;                    // the fake budget cuts the next nested run
};
static WatS g_watS;
static float WatTune(int i) {
    float v = 0;
    memcpy(&v, g_watCtr + (g_watS.set ? 0x4C : 0x28) + 4 * i, 4);
    return i == WAT_TRES && (g_watS.flags & 0x40) ? 3.4e38f : v;
}
static float WatFee(DWORD body) {
    const float f = (float)g_watCells[body & 0xFF] * 10.0f;
    return f > 1.0f ? f : 1.0f;
}
static bool WatLinkOpen(int wp, DWORD body, bool toBody) { return WatHash(g_watS.seed, (DWORD)wp * 2 + (toBody ? 1 : 0), body + 77) % 100 >= 8; }
// cost of from -> to; parent = the waypoint from was reached from (-1 none, -2 = the cheapest any parent gives)
static float WatEdgeCost(int from, int to, u8 eb, int parent);
static float WatEdgeCostP(int from, int to, u8 eb, int parent) {
    const WatS& S = g_watS;
    const WatWp &f = g_watWp[from], &t = g_watWp[to];
    if ((S.flags & 0x200) && t.type == 2) return kWatWall;
    if (t.type == 0x15 && !(S.flags & 0x180)) return kWatWall;
    if (t.blocked && !(WatLotEdge(f.type) && WatLotEdge(t.type) && f.n && t.n && f.nb[0] == t.nb[0])) return kWatWall;
    if (!eb) return kWatWall;
    float k = 10.0f;
    const WatWp* fr = &f;  // the record whose type and offsets stand for from
    if (eb == 1) {
        if (to == S.startHub && (S.flags & 0x10)) return 5e6f;
        if (to == S.goalHub) {
            if (S.flags & 0x20) return 5e6f;
            if (!((S.goalMask >> f.group) & 1)) return kWatWall;
            k = (S.flags & 8) ? 10.0f : 30.0f;
        } else if (t.type != 1) {
            if (parent >= 0) {
                k = WatTune(WAT_TRES) * (S.parentPos && g_watWp[parent].group != t.group ? 1000.0f : 10.0f);
                if (S.parentPos) fr = &g_watWp[parent];
            } else {
                if (!((S.startMask >> t.group) & 1)) return kWatWall;
                k = (S.flags & 4) ? 10.0f : 30.0f;
            }
        }
    } else if (eb == 2) k = WatTune(WAT_TRES) * (f.group != t.group ? 1000.0f : 10.0f);
    else if (eb == 3) k = S.mode == 1 ? 10.0f / WatTune(WAT_SIDE) : (f.type == 0x0D || t.type == 0x0D) ? 10.0f / WatTune(WAT_STREET) : 10.0f / WatTune(WAT_RIDE);
    else if (eb == 4 || eb == 5) k = 10.0f / WatTune(WAT_SIDE);
    if (fr->type != t.type) k = WatTune(WAT_KEEP) * k;
    if (k <= 0) return 0;
    if (WatLotEdge(t.type) && WatHash(S.seed, (DWORD)to, 5) % 100 < 4) return kWatWall;                           // the point test
    if ((eb == 2 || eb == 7) && WatHash(S.seed, (DWORD)from, (DWORD)to) % 100 < S.wallPct) return kWatWall;      // the polygon set
    const float fx = (float)(f.c % g_watW) * 32.0f + (float)fr->bx * 0.125f, fz = (float)(f.c / g_watW) * 32.0f + (float)fr->bz * 0.125f;
    const float dx = fx - WatX(t), dz = fz - WatZ(t);
    return (float)WA_SQRT((double)(dz * dz + dx * dx)) * k;
}
static float WatEdgeCost(int from, int to, u8 eb, int parent) {
    if (parent != -2) return WatEdgeCostP(from, to, eb, parent);
    float best = WatEdgeCostP(from, to, eb, -1);
    if (eb != 1) return best;
    const float self = WatEdgeCostP(from, to, eb, from);
    if (self < best) best = self;
    for (int p = 0; p < g_watN; p++)
        for (int k = 0; k < g_watWp[p].n; k++)
            if (g_watWp[p].nb[k] == from) {
                const float c = WatEdgeCostP(from, to, eb, p);
                if (c < best) best = c;
                break;
            }
    return best;
}
static float WatLinkCost(int wp, DWORD body) {
    if (!(g_watS.flags & 0x80)) return kWatWall;
    if (g_watS.mode == 1 && g_watWp[wp].type == 0x15) return WatTune(WAT_WLOT);
    return WatFee(body);
}
static int WatRefIdx(const WORD* r) { return r[0] == 0xFFFF ? -1 : g_watIdx[r[0]][r[1]]; }
static float __cdecl WatCost(const u8*, const u8*, const WORD* from, const WORD* to, DWORD isEdge, DWORD eb, DWORD*, void* parent) {
    if (from[0] == 0xFFFF || to[0] == 0xFFFF) {
        if (isEdge) return from[0] == 0xFFFF ? WatLinkCost(WatRefIdx(to), from[1]) : WatLinkCost(WatRefIdx(from), to[1]);
        return g_watS.gameBodyH ? WatLinkCost(WatRefIdx(to), from[1]) : 0.0f;
    }
    const int f = WatRefIdx(from), t = WatRefIdx(to);
    if (isEdge) {  // a body as parent has no record: the stand-in takes from's own (the game never has a hub behind a body)
        const int p = parent ? WatRefIdx((const WORD*)parent + 4) : -1;
        return WatEdgeCost(f, t, (u8)eb, parent && p < 0 ? f : p);
    }
    const float dx = WatX(g_watWp[f]) - WatX(g_watWp[t]), dz = WatZ(g_watWp[f]) - WatZ(g_watWp[t]);
    return (float)WA_SQRT((double)(dz * dz + dx * dx));
}
// one function per cost mode, as the A*'s table has (WaModeOf tells them apart by address)
// three bodies that differ (the store), so MSVC's identical-code folding (/OPT:ICF) cannot merge them into one address:
// WaModeOf tells the cost modes apart by address, as it tells the game's two cost functions apart
static volatile DWORD g_watCostLast;
static float __cdecl WatCost1(const u8* R, const u8* G, const WORD* a, const WORD* b, DWORD e, DWORD eb, DWORD* o, void* p) { g_watCostLast = 1; return WatCost(R, G, a, b, e, eb, o, p); }
static float __cdecl WatCost3(const u8* R, const u8* G, const WORD* a, const WORD* b, DWORD e, DWORD eb, DWORD* o, void* p) { g_watCostLast = 3; return WatCost(R, G, a, b, e, eb, o, p); }
static float __cdecl WatCost4(const u8* R, const u8* G, const WORD* a, const WORD* b, DWORD e, DWORD eb, DWORD* o, void* p) { g_watCostLast = 4; return WatCost(R, G, a, b, e, eb, o, p); }

// every true edge of the running search: f(from node, to node, cost); bodies are nodes g_watN + id. g_watTrueStart < 0:
// a hub's from position from its cheapest parent (a floor for any path); else the search from that start exactly (only
// the start is expanded without a parent; needs S.parentPos off, so the parent itself does not matter)
static int g_watTrueStart = -1;
template <class F> static void WatEachTrueEdge(F f) {
    for (int a = 0; a < g_watN; a++) {
        const WatWp& w = g_watWp[a];
        for (int k = 0; k < w.n; k++) {
            const float c = WatEdgeCost(a, w.nb[k], w.noEdge ? (u8)0 : w.eb[k], g_watTrueStart < 0 ? -2 : a == g_watTrueStart ? -1 : a);
            if (c < kWatWall) f(a, w.nb[k], c);
        }
    }
    for (int i = 0; i < g_watLinks; i++) {
        const WatLink& l = g_watLink[i];
        const u8 t = g_watWp[l.wp].type;
        const float c = WatLinkCost(l.wp, l.body);
        if (c >= kWatWall) continue;
        if (l.in30 && (t == 0x13 || t == 0x15) && WatLinkOpen(l.wp, l.body, true)) f(l.wp, g_watN + l.body, c);
        if (l.in4C && (!(g_watS.flags & 0x200) || t == 0x15) && WatLinkOpen(l.wp, l.body, false)) f(g_watN + l.body, l.wp, c);
    }
}
// brute force: cheapest cost of every node to `goal` on the true edges (relaxed until nothing moves)
static double g_watDist[WAT_NODES];
static void WatBrute(int goal) {
    for (int i = 0; i < WAT_NODES; i++) g_watDist[i] = 1e30;
    g_watDist[goal] = 0;
    for (bool moved = true; moved;) {
        moved = false;
        WatEachTrueEdge([&](int a, int b, float c) {
            if (g_watDist[b] + (double)c < g_watDist[a]) g_watDist[a] = g_watDist[b] + (double)c, moved = true;
        });
    }
}

// ---- the stand-in for 0x676260 ----
static DWORD g_watPool[WAT_POOL][5];  // node: g, f, ref, parent, count (docs/99 §2.2)
static DWORD* g_watHeap[WAT_POOL];
static int g_watPoolN, g_watHeapN;
static long g_watPops, g_watPushes, g_watSearches;
static float g_watFound;       // the goal's g of the last search, -1 none
static DWORD g_watPath[4];
static DWORD g_watOpts[64];
static float WatNodeF(const DWORD* n) { float f = 0; memcpy(&f, n + 1, 4); return f; }
static void __fastcall WatPushOrig(void*, void*, DWORD** node) {
    int i = g_watHeapN++;
    assert(g_watHeapN < WAT_POOL);
    for (; i && WatNodeF(g_watHeap[(i - 1) / 2]) > WatNodeF(*node); i = (i - 1) / 2) g_watHeap[i] = g_watHeap[(i - 1) / 2];
    g_watHeap[i] = *node;
    g_watPushes++;
}
static DWORD* WatPop() {
    DWORD* top = g_watHeap[0];
    DWORD* last = g_watHeap[--g_watHeapN];
    int i = 0;
    for (;;) {
        int c = 2 * i + 1;
        if (c >= g_watHeapN) break;
        if (c + 1 < g_watHeapN && WatNodeF(g_watHeap[c + 1]) < WatNodeF(g_watHeap[c])) c++;
        if (WatNodeF(g_watHeap[c]) >= WatNodeF(last)) break;
        g_watHeap[i] = g_watHeap[c], i = c;
    }
    if (g_watHeapN) g_watHeap[i] = last;
    return top;
}
static DWORD* __fastcall WatPathOrig(u8*, void*, DWORD* node) {
    memcpy(&g_watFound, node, 4);
    return g_watPath;
}
static void __fastcall WatPathFree(void*, void*, DWORD* path) {
    assert(path == g_watPath);
    (void)path;
}
static bool WatClosedBit(int node, bool set) {
    const bool was = ((g_watClosed[node >> 3] >> (node & 7)) & 1) != 0;
    if (set) g_watClosed[node >> 3] |= (u8)(1 << (node & 7));
    return was;
}
static DWORD* WatNewNode(float g, float f, DWORD ref, DWORD* parent) {
    assert(g_watPoolN < WAT_POOL);
    DWORD* n = g_watPool[g_watPoolN++];
    memcpy(n, &g, 4), memcpy(n + 1, &f, 4);
    n[2] = ref, n[3] = (DWORD)(uintptr_t)parent, n[4] = 1;
    return n;
}
static DWORD* __fastcall WatSearch(u8* ctr, void*, const u8* R, const u8* G, const WORD* s, const WORD* g, DWORD mode, DWORD* opts, DWORD* best) {
    const WaCostFn fn = mode == 1 ? WatCost1 : mode == 3 ? WatCost3 : WatCost4;
    const bool nested = t_wa.nested != 0;
    if (nested && g_watS.capNested) t_budget.capped = 1;
    // a nested run starts inside the outer search's first heuristic call: the outer has no node and no heap entry yet
    const int pool0 = g_watPoolN;
    assert(g_watHeapN == 0);
    g_watSearches++;
    g_watFound = -1;
    opts[0x29] = (DWORD)s[0] | ((DWORD)s[1] << 16), opts[0x2D] = (DWORD)g[0] | ((DWORD)g[1] << 16);
    opts[0x19] = 0x12345678;  // the search writes its cost set into the context
    memset(g_watClosed, 0, sizeof(g_watClosed));
    const float h0 = WaBegin(fn, ctr, R, G, s, g, 0, 0, opts, NULL);
    for (size_t i = 0; i < sizeof(g_watClosed); i++) assert(!g_watClosed[i]);  // a shadow pair leaves them cleared
    *best = opts[0x29];
    DWORD* found = NULL;
    DWORD* start = WatNewNode(0, h0, opts[0x29], NULL);
    WatPushOrig(NULL, NULL, &start);
    g_watPushes--;
    while (g_watHeapN) {
        DWORD* n = WatPop();
        const WORD ref[2] = {(WORD)n[2], (WORD)(n[2] >> 16)};
        const int node = ref[0] == 0xFFFF ? g_watN + (ref[1] & 0xFF) : g_watIdx[ref[0]][ref[1]];
        if (WatClosedBit(node, false)) continue;
        if (ref[0] == g[0] && ref[1] == g[1]) { found = n; break; }
        WatClosedBit(node, true);
        g_watPops++;
        float ng = 0;
        memcpy(&ng, n, 4);
        const auto gen = [&](const WORD* to, DWORD eb) {
            const int tn = to[0] == 0xFFFF ? g_watN + (to[1] & 0xFF) : g_watIdx[to[0]][to[1]];
            if (WatClosedBit(tn, false)) return;
            const float c = fn(R, G, ref, to, 1, eb, opts, (void*)(uintptr_t)n[3]);
            if (!(c < kWatWall)) return;
            const float h = WaHeur(fn, R, G, to, g, 0, 0, opts, NULL);
            const float gg = c + ng;
            DWORD* m = WatNewNode(gg, gg + h, (DWORD)to[0] | ((DWORD)to[1] << 16), n);
            WaPush(NULL, NULL, &m);
        };
        if (ref[0] != 0xFFFF) {
            const WatWp& w = g_watWp[node];
            for (int k = 0; k < w.n; k++) {
                const WORD to[2] = {(WORD)g_watWp[w.nb[k]].c, (WORD)g_watWp[w.nb[k]].i};
                gen(to, w.noEdge ? 0 : w.eb[k]);
            }
            if (w.type == 0x13 || w.type == 0x15)
                for (int i = 0; i < g_watLinks; i++)
                    if (g_watLink[i].in30 && g_watLink[i].wp == node && WatLinkOpen(node, g_watLink[i].body, true)) {
                        const WORD to[2] = {0xFFFF, g_watLink[i].body};
                        gen(to, 8);
                    }
        } else {
            for (int i = 0; i < g_watLinks; i++) {
                const WatLink& l = g_watLink[i];
                if (!l.in4C || l.body != (ref[1] & 0xFF) || ((opts[0] & 0x200) && g_watWp[l.wp].type != 0x15) || !WatLinkOpen(l.wp, l.body, false)) continue;
                const WORD to[2] = {(WORD)g_watWp[l.wp].c, (WORD)g_watWp[l.wp].i};
                gen(to, 8);
            }
        }
    }
    DWORD* path = found ? WaPathOut(ctr, NULL, found) : NULL;
    g_watHeapN = 0;
    if (nested) g_watPoolN = pool0;
    return path;
}

// ---- the checks ----
static u8 g_watWm[4];
static const u8* g_watWmSlot = g_watWm;
static const u8* __fastcall WatLotAt(const u8* wm, void*, float x, float) {
    static u8 lot[0x4B4];
    lot[0x4B0] = wm[0];
    return x < 0 ? NULL : lot;
}
static void WatReset(LONG landmarks) {
    WaDrop(g_waW);
    for (int m = 0; m < WA_MODES; m++) WaMemFree(g_waW.tab[m].d, g_waW.tabCap[m]), g_waW.tab[m].d = NULL, g_waW.tabCap[m] = 0;
    WaMemFree(g_waW.base, g_waW.baseCap), g_waW.base = NULL, g_waW.baseCap = 0;
    assert(!g_waWords);
    for (int m = 0; m < WA_MODES; m++) g_waW.tab[m].kmax = (DWORD)landmarks;
    memset(&ts3native_worldalt, 0, sizeof(ts3native_worldalt));
    memset(&t_wa, 0, sizeof(t_wa));
    ts3native_worldalt.waLandmarks = landmarks, ts3native_worldalt.waReady = 3;
    assert((const void*)WatCost1 != (const void*)WatCost3 && (const void*)WatCost3 != (const void*)WatCost4 && (const void*)WatCost1 != (const void*)WatCost4);
    g_waCost[0] = (const void*)WatCost1, g_waCost[1] = (const void*)WatCost3;
    g_waPushOrig = WatPushOrig, g_waPathOrig = WatPathOrig, g_waPathFree = WatPathFree, g_waSearch = WatSearch;
    g_waAll = true, g_waLotAt = WatLotAt, g_waWm = &g_watWmSlot, g_watWm[0] = 0;
    g_watTrueStart = -1;
}
static void WatPickSearch(bool consistent) {
    WatS& S = g_watS;
    memset(&S, 0, sizeof(S));
    S.mode = WatRnd(2) ? 1 : 3, S.set = (int)WatRnd(2), S.seed = WatRnd(1 << 24), S.wallPct = WatRnd(3) ? WatRnd(30) : 0;
    S.flags = (WatRnd(4) ? 0x80u : 0) | (WatRnd(3) ? 0 : 0x100u) | (WatRnd(8) ? 0 : 0x200u) | (WatRnd(6) ? 0 : 0x40u) | (WatRnd(2) ? 0x10u : 0) | (WatRnd(2) ? 0x20u : 0) |
              (WatRnd(4) ? 0 : 4u) | (WatRnd(4) ? 0 : 8u);
    S.startHub = S.goalHub = -1;
    for (int tries = 0; tries < 8; tries++) {
        const int a = (int)WatRnd((DWORD)g_watN);
        if (g_watWp[a].type == 1) (tries & 1 ? S.startHub : S.goalHub) = a;
    }
    S.startMask = WatRnd(16) | WatRnd(16), S.goalMask = WatRnd(16) | WatRnd(16);
    S.parentPos = !consistent, S.gameBodyH = false;
    memset(g_watOpts, 0, sizeof(g_watOpts));
    g_watOpts[0] = S.flags;
}
// runs the stand-in from waypoint a to waypoint b; returns the goal's g or -1
static float WatRun(int a, int b) {
    const WORD s[2] = {(WORD)g_watWp[a].c, (WORD)g_watWp[a].i}, g[2] = {(WORD)g_watWp[b].c, (WORD)g_watWp[b].i};
    DWORD best = 0;
    g_watPoolN = 0, g_watHeapN = 0;
    WatSearch(g_watCtr, NULL, g_watR, g_watG, s, g, (DWORD)g_watS.mode, g_watOpts, &best);
    return g_watFound;
}
// loops until cost mode `mode`'s table is complete (the graph, then one landmark a loop)
static void WatBuildAll(int mode) {
    const int mi = mode == 1 ? 0 : 1;
    const WatS keep = g_watS;
    memset(&g_watS, 0, sizeof(g_watS));
    g_watS.mode = mode, g_watS.startHub = g_watS.goalHub = -1;
    memset(g_watOpts, 0, sizeof(g_watOpts));
    ts3native_worldalt.waMode = 1;
    for (int i = 0; i < 2 * WA_MAXK + 4 && !(g_waW.tab[mi].k && (g_waW.done[mi] || g_waW.tab[mi].k == g_waW.tab[mi].kmax)); i++) WatRun(0, g_watN - 1);
#ifndef WORLDALT_HOST_TEST
    if (!g_waW.tab[mi].k || g_waW.bad) {
        const WorldAltCounters& c = ts3native_worldalt;
        printf("WorldAlt build mode %d: k %lu kmax %lu bad %d done %d graph %d n %d loops %ld trees %ld skips", mode, (unsigned long)g_waW.tab[mi].k,
               (unsigned long)g_waW.tab[mi].kmax, (int)g_waW.bad, (int)g_waW.done[mi], (int)g_waW.haveGraph, g_watN, (long)c.waLoops, (long)c.waTrees);
        for (int r = 0; r < 8; r++) printf(" %ld", (long)c.waSkip[r]);
        printf("\n");
    }
#endif
    assert(g_waW.tab[mi].k && !g_waW.bad);
    g_watS = keep;
    memset(g_watOpts, 0, sizeof(g_watOpts));
    g_watOpts[0] = g_watS.flags;
}
// the bound of every node towards goal b against the brute force, and the triangle rule on every true edge
static long g_watBoundChecks, g_watTight;
static void WatCheckBound(int a, int b) {
    const int mi = g_watS.mode == 1 ? 0 : 1;
    const WORD s[2] = {(WORD)g_watWp[a].c, (WORD)g_watWp[a].i}, g[2] = {(WORD)g_watWp[b].c, (WORD)g_watWp[b].i};
    WaTh& t = t_wa;
    if (WaPrepare(t, mi, g_watCtr, g_watR, g_watG, s, g, g_watOpts) != WAS_OK) return;
    WatBrute(b);
    static float bound[WAT_NODES];
    for (int u = 0; u < g_watN + WA_BODIES; u++) {
        const DWORD node = u < g_watN ? WaNodeOf(t.base, t.chunks, t.n, (DWORD)g_watWp[u].c, (DWORD)g_watWp[u].i) : WaNodeOf(t.base, t.chunks, t.n, 0xFFFF, (DWORD)(u - g_watN));
        assert(node == (DWORD)u);
        bound[u] = WaUnitsToCost(WaBoundUnits(t.q, node));
        if (g_watDist[u] > 1e29) continue;
        assert((double)bound[u] <= g_watDist[u] * (1.0 + 1e-6) + 1e-4);
        g_watBoundChecks++;
        if (g_watDist[u] > 0 && (double)bound[u] > 0.5 * g_watDist[u]) g_watTight++;
    }
    assert(bound[b] == 0);
    WatEachTrueEdge([&](int x, int y, float c) { assert((double)bound[x] <= ((double)c + (double)bound[y]) * (1.0 + 1e-6) + 1e-3); });
}

static long g_watPopsPlain, g_watPopsBound, g_watEqual;
static void TestWaWorlds(int worlds, int queries) {
    for (int wi = 0; wi < worlds; wi++) {
        const bool consistent = (wi & 1) == 0;
        do WatWorld(consistent); while (g_watN < 12);
        WatReset(4 + (LONG)WatRnd(13));
        WatBuildAll(1), WatBuildAll(3);
        assert(!g_waW.haveGraph && !g_waW.gmem);  // both modes finished: the graph block is gone
        for (int q = 0; q < queries; q++) {
            WatPickSearch(consistent);
            const int a = (int)WatRnd((DWORD)g_watN), b = (int)WatRnd((DWORD)g_watN);
            WatCheckBound(a, b);
            if (!consistent) {  // the stand-in's own heuristic is not exact here (cheap water, parent offsets): both must end
                ts3native_worldalt.waMode = 1;
                WatRun(a, b);
                continue;
            }
            g_watTrueStart = a;
            WatBrute(b);
            g_watTrueStart = -1;
            const bool reach = g_watDist[a] < 1e29;
            ts3native_worldalt.waMode = 0;
            g_watPops = 0;
            const float plain = WatRun(a, b);
            const long popsPlain = g_watPops;
            ts3native_worldalt.waMode = 1;
            g_watPops = 0;
            const float alt = WatRun(a, b);
            assert((plain >= 0) == reach && (alt >= 0) == reach);
            if (!reach) continue;
            const double tol = g_watDist[a] * 2e-5 + 0.01;
            assert((double)plain <= g_watDist[a] + tol && (double)plain >= g_watDist[a] - tol);
            assert((double)alt <= g_watDist[a] + tol && (double)alt >= g_watDist[a] - tol);
            g_watPopsPlain += popsPlain, g_watPopsBound += g_watPops, g_watEqual++;
        }
    }
    assert(g_watBoundChecks > 0 && g_watEqual > 0 && g_watTight > 0);
    assert(g_watPopsBound <= g_watPopsPlain + g_watPopsPlain / 50 + 16);  // over all pairs the bound expands no more (ties aside)
}

static void* g_watAllocFail(size_t) { return NULL; }
// two waypoints the first landmark of every built table reaches (so a query for them has something to say)
static void WatPair(int* a, int* b) {
    for (int tries = 0;; tries++) {
        assert(tries < 100000);
        *a = (int)WatRnd((DWORD)g_watN), *b = (int)WatRnd((DWORD)g_watN);
        bool ok = *a != *b;
        for (int m = 0; m < WA_MODES && ok; m++)
            if (g_waW.tab[m].k) ok = g_waW.tab[m].d[(DWORD)*a * g_waW.tab[m].kmax] < WA_FAR && g_waW.tab[m].d[(DWORD)*b * g_waW.tab[m].kmax] < WA_FAR;
        if (ok) return;
    }
}
// a pair as above that the current search settings also connect: probed plain (WorldAlt off), so no table step and no
// bound loop is counted; after 200 failed picks the walls go, which every picked pair then connects
static void WatPairFound(int* a, int* b) {
    WorldAltCounters& c = ts3native_worldalt;
    const LONG keepMode = c.waMode;
    c.waMode = 0;
    for (int tries = 0;; tries++) {
        assert(tries < 400);
        if (tries == 200) g_watS.wallPct = 0, g_watS.startMask = g_watS.goalMask = 0;
        WatPair(a, b);
        if (WatRun(*a, *b) >= 0) break;
    }
    WaFlush(t_wa);
    c.waMode = keepMode;
}
static void TestWaManager() {
    WorldAltCounters& c = ts3native_worldalt;
    do WatWorld(true); while (g_watN < 40);
    WatReset(6);
    WatPickSearch(true);
    g_watS.mode = 1, g_watS.flags = 0x80, g_watOpts[0] = 0x80;
    c.waMode = 1;
    // slices: the first loop builds the graph, each later one adds one landmark; the bound serves from the first landmark
    WatRun(0, g_watN - 1);
    assert(c.waLoops == 1 && c.waGraphs == 1 && c.waTrees == 0 && c.waSkip[WAS_NOTAB - 1] == 1 && c.waBound == 0 && g_waW.haveGraph);
    WatRun(0, g_watN - 1);
    assert(c.waTrees == 1 && g_waW.tab[0].k == 1 && c.waBound + c.waSkip[WAS_NOTAB - 1] == 2);
    int pa = 0, pb = 0;
    WatPairFound(&pa, &pb);
    const LONG bound0 = c.waBound;
    for (int i = 2; i <= 6; i++) {
        WatRun(pa, pb);
        assert((int)g_waW.tab[0].k == (g_waW.done[0] ? (int)g_waW.tab[0].k : i) && c.waBound == bound0 + i - 1);
    }
    assert(!g_waW.haveGraph && !g_waW.gmem && c.waGraphs == 1);
    const LONG trees = c.waTrees;
    WatRun(pa, pb);
    assert(c.waTrees == trees && c.waDrops == 0 && c.waBound == bound0 + 6 && c.waGen[1] > 0 && c.waMemKb > 0);
    // mode 3 starts later: the graph is built again for it, mode 1's table stays
    g_watS.mode = 3;
    WatRun(pa, pb);
    assert(c.waGraphs == 2 && g_waW.tab[0].k && g_waW.tab[1].k == 0);
    WatBuildAll(3);
    assert(!g_waW.haveGraph && g_waW.tab[1].k);
    g_watS.mode = 1, g_watOpts[0] = g_watS.flags = 0x80;
    // invalidation: a body's cell count, a new link, a type flip to 0x15, a tuning value; each drops and rebuilds
    for (int kind = 0; kind < 5; kind++) {
        const LONG drops = c.waDrops, gen = g_waGen;
        int flip = -1;
        if (kind == 0) g_watCells[1] += 3;
        else if (kind == 1) {
            for (int a = 0; a < g_watN && flip < 0; a++)
                if (g_watWp[a].type == 7 && g_watLinks < WAT_MAXLINK) flip = a;
            assert(flip >= 0);
            WatLink& l = g_watLink[g_watLinks++];
            l.wp = flip, l.body = 2, l.in30 = false, l.in4C = true;
        } else if (kind == 2) {
            for (int i = 0; i < g_watLinks && flip < 0; i++)
                if (g_watWp[g_watLink[i].wp].type == 0x13) flip = g_watLink[i].wp;
            assert(flip >= 0);
            g_watWp[flip].type = 0x15;
        } else if (kind == 3) WatF(g_watCtr + 0x4C + 8, 9.5f);
        else {  // a record edit with the same link data: seen at the periodic land check only
            for (int a = 0; a < g_watN && flip < 0; a++)
                if (g_watWp[a].n && !g_watWp[a].noEdge && g_watWp[a].eb[0] == 7) flip = a;
            assert(flip >= 0);
            g_watWp[flip].eb[0] = 4;
        }
        WatSerialize();
        if (kind == 4) {
            WatRun(0, g_watN - 1);
            assert(c.waDrops == drops);
            g_waW.checks = 255;
        }
        WatRun(0, g_watN - 1);
        assert(c.waDrops == drops + 1 && g_waGen != gen && g_waW.tab[0].k == 0 && g_waW.tab[1].k == 0);
        WatBuildAll(1), WatBuildAll(3);
        for (int q = 0; q < 6; q++) {
            WatPickSearch((q & 1) != 0);
            WatCheckBound((int)WatRnd((DWORD)g_watN), (int)WatRnd((DWORD)g_watN));
        }
        memset(&g_watS, 0, sizeof(g_watS));
        g_watS.mode = 1, g_watOpts[0] = g_watS.flags = 0x80, g_watS.startHub = g_watS.goalHub = -1;
    }
    WatPair(&pa, &pb);
    const WORD s[2] = {(WORD)g_watWp[pa].c, (WORD)g_watWp[pa].i}, g[2] = {(WORD)g_watWp[pb].c, (WORD)g_watWp[pb].i}, v[2] = {0xFFFF, 1}, bad[2] = {0x7000, 3};
    WaTh& t = t_wa;
    // a query set up before a drop stops using its rows
    memset(g_watOpts, 0, sizeof(g_watOpts));
    const float h0 = WaBegin(WatCost1, g_watCtr, g_watR, g_watG, s, g, 0, 0, g_watOpts, NULL);
    assert(t.active && h0 >= 0);
    InterlockedIncrement(&g_waGen);
    WaHeur(WatCost1, g_watR, g_watG, s, g, 0, 0, g_watOpts, NULL);
    assert(!t.active && t.delta == 0);
    InterlockedExchangeAdd(&g_waGen, -1);
    // the push: the excess goes onto f once
    {
        DWORD node[5] = {};
        float f = 12.5f;
        memcpy(node + 1, &f, 4);
        DWORD* p = node;
        g_watHeapN = 0;
        t.delta = 3.0f;
        WaPush(NULL, NULL, &p);
        assert(WatNodeF(node) == 15.5f && t.delta == 0 && g_watHeapN == 1);
        WaPush(NULL, NULL, &p);
        assert(WatNodeF(node) == 15.5f);
        g_watHeapN = 0;
    }
    // reasons a loop takes no bound
    assert(WaPrepare(t, 0, g_watCtr, g_watR, g_watG, s, g, g_watOpts) == WAS_OK);
    assert(WaPrepare(t, -1, g_watCtr, g_watR, g_watG, s, g, g_watOpts) == WAS_MODE && WaModeOf((const void*)WatCost4) == -1);
    assert(WaPrepare(t, 0, g_watCtr, g_watR, g_watG, v, g, g_watOpts) == WAS_VIRTUAL && WaPrepare(t, 0, g_watCtr, g_watR, g_watG, s, v, g_watOpts) == WAS_VIRTUAL);
    assert(WaPrepare(t, 0, g_watCtr, g_watR, g_watG, bad, g, g_watOpts) == WAS_VIRTUAL);
    assert(WaPrepare(t, 0, g_watCtr, NULL, g_watG, s, g, g_watOpts) == WAS_WORLD);
    for (DWORD f = 1; f <= 3; f++) {
        g_watOpts[0] = f;
        assert(WaPrepare(t, 0, g_watCtr, g_watR, g_watG, s, g, g_watOpts) == WAS_FLAGS);
    }
    g_watOpts[0] = 0x80;
    g_waAll = false;  // no plan session on this thread: scope unknown (whatever an earlier test left: the rule is what is checked)
    {
        bool worker = false;
        const LONG scope = PccScopeNow(&worker);
        assert(WaPrepare(t, 0, g_watCtr, g_watR, g_watG, s, g, g_watOpts) == (scope == 2 || scope == 3 ? WAS_OK : WAS_SCOPE));
    }
    g_waAll = true;
    // mode 3 with water links: a water lot at the start or goal point; no world manager = cannot tell
    assert(WaPrepare(t, 1, g_watCtr, g_watR, g_watG, s, g, g_watOpts) == WAS_OK);
    g_waWm = NULL;
    assert(WaPrepare(t, 1, g_watCtr, g_watR, g_watG, s, g, g_watOpts) == WAS_WATERLOT);
    g_waWm = &g_watWmSlot;
    WatF((u8*)(g_watOpts + 0x2A), 5.0f), WatF((u8*)(g_watOpts + 0x2E), 6.0f);
    g_watWm[0] = 1;
    assert(WaPrepare(t, 1, g_watCtr, g_watR, g_watG, s, g, g_watOpts) == WAS_WATERLOT);
    assert(WaPrepare(t, 0, g_watCtr, g_watR, g_watG, s, g, g_watOpts) == WAS_OK);  // mode 1 never asks
    WatF((u8*)(g_watOpts + 0x2A), -5.0f);  // no lot at the start point, a water lot at the goal point
    assert(WaPrepare(t, 1, g_watCtr, g_watR, g_watG, s, g, g_watOpts) == WAS_WATERLOT);
    WatF((u8*)(g_watOpts + 0x2E), -6.0f);  // no lot at either point
    assert(WaPrepare(t, 1, g_watCtr, g_watR, g_watG, s, g, g_watOpts) == WAS_OK);
    g_watOpts[0] = 0;  // no water links: the lot does not matter
    WatF((u8*)(g_watOpts + 0x2A), 5.0f);
    assert(WaPrepare(t, 1, g_watCtr, g_watR, g_watG, s, g, g_watOpts) == WAS_OK);
    g_watWm[0] = 0;
    AcquireSRWLockExclusive(&g_waLock);
    assert(WaPrepare(t, 0, g_watCtr, g_watR, g_watG, s, g, g_watOpts) == WAS_BUSY);
    ReleaseSRWLockExclusive(&g_waLock);
    // [RouteAsync]'s budget stub in front of a cost function: push orig; jmp
    u8 stub[10] = {0x68, 0, 0, 0, 0, 0xE9, 0, 0, 0, 0};
    const void* f3 = (const void*)WatCost3;
    memcpy(stub + 1, &f3, sizeof(f3));
    assert(WaModeOf(stub) == 1 && WaModeOf((const void*)WatCost1) == 0);
    // tuning the bound cannot be built on, an unreadable R, no memory: no tables, the loops run plain
    void* (*const keepAlloc)(size_t) = g_waAlloc;
    for (int kind = 0; kind < 3; kind++) {
        u8 keepCtr[sizeof(g_watCtr)], keepR[sizeof(g_watR)];
        memcpy(keepCtr, g_watCtr, sizeof(keepCtr)), memcpy(keepR, g_watR, sizeof(keepR));
        if (kind == 0) WatF(g_watCtr + 0x28 + 8, 0.0f);
        else if (kind == 1) WatD(g_watR, 0);
        else WatReset(6), g_waAlloc = g_watAllocFail;
        const LONG skips = c.waSkip[WAS_WORLD - 1];
        g_watS.mode = 1, g_watOpts[0] = g_watS.flags = 0x80;
        c.waMode = 1;
        for (int i = 0; i < 3; i++) WatRun(0, g_watN - 1);
        assert(c.waSkip[WAS_WORLD - 1] >= skips + 2 && g_waW.tab[0].k == 0);
        memcpy(g_watCtr, keepCtr, sizeof(keepCtr)), memcpy(g_watR, keepR, sizeof(keepR));
        g_waAlloc = keepAlloc;
        WaDrop(g_waW);
    }
}

static void TestWaShadow() {
    WorldAltCounters& c = ts3native_worldalt;
    do WatWorld(true); while (g_watN < 40);
    WatReset(8);
    WatBuildAll(1), WatBuildAll(3);
    c.waMode = 2;
    WaFlush(t_wa);  // the build loops' tally
    const LONG bound0 = c.waBound;
    const LONGLONG gen0 = c.waGen[1];
    LONG pairs = 0;
    for (int q = 0; q < 60; q++) {
        WatPickSearch(true);
        g_watOpts[5] = 0xAAAA5555, g_watOpts[0x30] = 0x77;
        DWORD keep[64];
        memcpy(keep, g_watOpts, sizeof(keep));
        const LONG before = c.waShadow;
        const long searches = g_watSearches;
        const int a = (int)WatRnd((DWORD)g_watN), b = (int)WatRnd((DWORD)g_watN);
        g_watTrueStart = a;
        WatBrute(b);
        g_watTrueStart = -1;
        const float got = WatRun(a, b);
        assert((got >= 0) == (g_watDist[a] < 1e29));
        // the context is the planner's again, apart from what the real search itself writes
        keep[0x19] = g_watOpts[0x19], keep[0x29] = g_watOpts[0x29], keep[0x2D] = g_watOpts[0x2D];
        assert(!memcmp(keep, g_watOpts, sizeof(keep)) && !t_wa.nested && !t_budget.capped);
        assert(c.waShadow == before || c.waShadow == before + 1);
        assert(g_watSearches == searches + (c.waShadow != before ? 3 : 1));
        pairs += c.waShadow - before;
    }
    assert(pairs > 10 && c.waShadow == pairs && c.waShSame + c.waShTie + c.waShRound + c.waShNone == pairs);
    assert(!c.waShDearer && !c.waShCheaper && !c.waShFound && !c.waShCapped && !c.waShNoBound && !c.waLines);
    assert(c.waShNone < pairs && c.waShGen[1] > 0 && c.waShGen[1] <= c.waShGen[0] + c.waShGen[0] / 50 + 16 && c.waShClosed[1] <= c.waShClosed[0] + c.waShClosed[0] / 50 + 16);
    assert(c.waBound == bound0 && c.waGen[1] == gen0);  // the real searches ran plain
    int pa = 0, pb = 0;
    WatPair(&pa, &pb);
    memset(&g_watS, 0, sizeof(g_watS));
    g_watS.mode = 1, g_watS.flags = 0x80, g_watS.startHub = g_watS.goalHub = -1;
    memset(g_watOpts, 0, sizeof(g_watOpts));
    g_watOpts[0] = 0x80;
    // a shadow run left open by an unwind (nested set, but this loop is not below that call): the next loop is the game's own
    {
        const WORD s[2] = {(WORD)g_watWp[pa].c, (WORD)g_watWp[pa].i}, g[2] = {(WORD)g_watWp[pb].c, (WORD)g_watWp[pb].i};
        const LONG loops = c.waLoops, sh = c.waShadow;
        t_wa.nested = 2, t_wa.nestSp = 0, t_wa.nestOpts = g_watOpts;
        g_watPoolN = g_watHeapN = 0;
        WaBegin(WatCost1, g_watCtr, g_watR, g_watG, s, g, 0, 0, g_watOpts, NULL);
        assert(!t_wa.nested && !t_wa.active && c.waLoops == loops + 1 && c.waShadow == sh + 1);
        t_wa.nested = 2, t_wa.nestSp = ~(uintptr_t)0, t_wa.nestOpts = g_watOpts + 1;  // deeper, but another context
        WaBegin(WatCost1, g_watCtr, g_watR, g_watG, s, g, 0, 0, g_watOpts, NULL);
        assert(!t_wa.nested && c.waLoops == loops + 2 && c.waShadow == sh + 2);
        pairs = c.waShadow;
    }
    // a budget cut inside the pair: not compared, the budget as before
    g_watS.capNested = true;
    WatRun(pa, pb);
    assert(c.waShCapped == 1 && c.waShadow == pairs && !t_budget.capped);
    g_watS.capNested = false;
    // a circle loop was compared: its rerun without the circle is not compared again, a new search is
    {
        const WORD s[2] = {(WORD)g_watWp[pa].c, (WORD)g_watWp[pa].i}, g[2] = {(WORD)g_watWp[pb].c, (WORD)g_watWp[pb].i};
        WatF((u8*)(g_watOpts + 0x28), 2500.0f);
        g_watPoolN = g_watHeapN = 0;
        WaBegin(WatCost1, g_watCtr, g_watR, g_watG, s, g, 0, 0, g_watOpts, NULL);
        assert(c.waShadow == pairs + 1 && t_wa.pendOpts == g_watOpts);
        float r2 = 0;
        memcpy(&r2, g_watOpts + 0x28, 4);
        assert(r2 == 2500.0f);
        g_watOpts[0x28] = 0;
        WaBegin(WatCost1, g_watCtr, g_watR, g_watG, s, g, 0, 0, g_watOpts, NULL);
        assert(c.waShadow == pairs + 1 && !t_wa.pendOpts);
        WaBegin(WatCost1, g_watCtr, g_watR, g_watG, s, g, 0, 0, g_watOpts, NULL);
        assert(c.waShadow == pairs + 2);
    }
    // a bound that is too high (every table entry times 40) shows as a dearer path sooner or later; a small world may
    // not mislead the search at all, so up to 20 worlds are tried (the counters below are those of the last one)
    for (int world = 0; world < 20 && !c.waShDearer; world++) {
        if (world) do WatWorld(true); while (g_watN < 40);
        WatReset(8);
        WatBuildAll(1);
        for (DWORD u = 0; u < g_waW.nodes; u++)
            for (DWORD k = 0; k < g_waW.tab[0].k; k++)
                if (g_waW.tab[0].d[u * g_waW.tab[0].kmax + k] < WA_FAR / 64) g_waW.tab[0].d[u * g_waW.tab[0].kmax + k] *= 40;
        c.waMode = 2;
        for (int q = 0; q < 400 && !c.waShDearer; q++) {
            WatPickSearch(true);
            g_watS.mode = 1, g_watS.wallPct = 0;
            WatRun((int)WatRnd((DWORD)g_watN), (int)WatRnd((DWORD)g_watN));
        }
    }
    assert(c.waShDearer > 0 && c.waLines > 0 && !c.waShCheaper && !c.waShGameOver);
}

#ifndef WORLDALT_HOST_TEST
// the thunks as the A* calls them: 8 arguments pushed, the cost function pointer 0x80 above them, ebp = the container
static float g_watThunkRet;
static DWORD g_watThunkArgs[10], g_watThunkRegs[4];
static float __cdecl WatThunkCost(const u8* R, const u8* G, const WORD* from, const WORD* to, DWORD isEdge, DWORD eb, DWORD* opts, void* parent) {
    const DWORD a[8] = {(DWORD)(uintptr_t)R, (DWORD)(uintptr_t)G, (DWORD)(uintptr_t)from, (DWORD)(uintptr_t)to, isEdge, eb, (DWORD)(uintptr_t)opts, (DWORD)(uintptr_t)parent};
    memcpy(g_watThunkArgs, a, sizeof(a));
    return 321.25f;
}
static __declspec(naked) void __cdecl WatCallSite(void* /*thunk*/, void* /*costFn*/, void* /*ctr*/) {
    __asm {
        push ebp
        push ebx
        push esi
        push edi
        mov eax, dword ptr [esp + 0x14]   // thunk
        mov ecx, dword ptr [esp + 0x18]   // cost function
        mov ebp, dword ptr [esp + 0x1C]   // container
        mov ebx, 0x11111111
        mov esi, 0x22222222
        mov edi, 0x33333333
        sub esp, 0x90
        mov dword ptr [esp + 0x60], ecx   // the A*'s slot: [esp+0x80] once 8 arguments are pushed
        push 8
        push 7
        push 6
        push 5
        push 4
        push 3
        push 2
        push 1
        call eax
        fstp dword ptr [g_watThunkRet]
        add esp, 0x20
        mov dword ptr [g_watThunkRegs], ebx
        mov dword ptr [g_watThunkRegs + 4], esi
        mov dword ptr [g_watThunkRegs + 8], edi
        mov dword ptr [g_watThunkRegs + 12], ebp
        add esp, 0x90
        pop edi
        pop esi
        pop ebx
        pop ebp
        ret
    }
}
static void TestWaInstall() {
    // the thunks: every argument arrives, the game's value comes back in ST0, callee-saved registers survive. With mode 0
    // and no nested run WaBegin only counts the loop; WaHeur passes through.
    WatReset(4);
    static u8 ctr[0x80];
    for (int which = 0; which < 2; which++) {
        memset(g_watThunkArgs, 0, sizeof(g_watThunkArgs)), g_watThunkRet = 0;
        WatCallSite(which ? (void*)WaHeurThunk : (void*)WaBeginThunk, (void*)WatThunkCost, ctr);
        assert(g_watThunkRet == 321.25f);
        for (DWORD i = 0; i < 8; i++) assert(g_watThunkArgs[i] == i + 1);
        assert(g_watThunkRegs[0] == 0x11111111 && g_watThunkRegs[1] == 0x22222222 && g_watThunkRegs[2] == 0x33333333 && g_watThunkRegs[3] == (DWORD)(uintptr_t)ctr);
    }
    assert(ts3native_worldalt.waLoops == 1 && t_wa.calls == 1);
    // the install on a copy of the sites' bytes: all eight written, or none
    static u8 img[0x1000], plan[0x2100];
    const DWORD astar = (DWORD)(uintptr_t)img + 0x500, planner = (DWORD)(uintptr_t)plan;
    static const u8 kInd[7] = {0xFF, 0x94, 0x24, 0x80, 0x00, 0x00, 0x00}, kFree[7] = {0x56, 0x8B, 0x74, 0x24, 0x08, 0x85, 0xF6};
    const auto call = [](DWORD site, DWORD target) {
        u8* p = (u8*)(uintptr_t)site;
        const LONG rel = (LONG)(target - (site + 5));
        p[0] = 0xE8, memcpy(p + 1, &rel, 4);
    };
    const auto target = [](DWORD site) { return (DWORD)(site + 5 + *(const LONG*)(uintptr_t)(site + 1)); };
    for (int fault = 0; fault < 5; fault++) {
        memset(img, 0xCC, sizeof(img)), memset(plan, 0xCC, sizeof(plan));
        memcpy((u8*)(uintptr_t)(astar + WA_OFF_BEGIN), kInd, 7);
        for (int i = 0; i < 3; i++) memcpy((u8*)(uintptr_t)(astar + kWaHeurOff[i]), kInd, 7), call(astar + kWaPushOff[i], astar - 0x70);
        call(astar + WA_OFF_PUSH0, astar - 0x70), call(astar + WA_OFF_PATH, astar - 0x130);
        call(planner + WA_OFF_FREE, (DWORD)(uintptr_t)img + 0x40);
        memcpy(img + 0x40, kFree, 7);
        if (fault == 1) img[0x500 + 0x674 + 3] = 0x84;                  // another frame offset at one heuristic call
        if (fault == 2) call(astar + kWaPushOff[1], astar - 0x60);       // one push site calls something else
        if (fault == 3) call(astar + WA_OFF_PATH, astar - 0x70);         // the path call is the push
        if (fault == 4) img[0x40] = 0x55;                                // the planner's call is not the path free
        u8 before[sizeof(img)];
        memcpy(before, img, sizeof(img));
        g_waPathFree = NULL, g_waSearch = NULL;
        const LONG ready = WaPatch(astar, planner);
        if (fault >= 1 && fault <= 3) { assert(!ready && !memcmp(before, img, sizeof(img))); continue; }
        assert(ready == (fault == 4 ? 1 : 3));
        assert((g_waPathFree != NULL) == (fault == 0) && (g_waSearch != NULL) == (fault == 0));
        assert(g_waPushOrig == (WaPushFn)(uintptr_t)(astar - 0x70) && g_waPathOrig == (WaPathFn)(uintptr_t)(astar - 0x130));
        for (int i = 0; i < 3; i++) {
            const u8* h = (const u8*)(uintptr_t)(astar + kWaHeurOff[i]);
            assert(h[0] == 0xE8 && h[5] == 0x66 && h[6] == 0x90 && target(astar + kWaHeurOff[i]) == (DWORD)(uintptr_t)WaHeurThunk);
            assert(target(astar + kWaPushOff[i]) == (DWORD)(uintptr_t)WaPush);
        }
        assert(target(astar + WA_OFF_BEGIN) == (DWORD)(uintptr_t)WaBeginThunk && img[0x500 + WA_OFF_BEGIN + 5] == 0x66);
        assert(target(astar + WA_OFF_PATH) == (DWORD)(uintptr_t)WaPathOut && target(astar + WA_OFF_PUSH0) == astar - 0x70);
        // nothing else moved
        int diff = 0;
        for (size_t i = 0; i < sizeof(img); i++) diff += before[i] != img[i];
        assert(diff <= 4 * 7 + 4 * 4);
    }
}
#endif

static void TestWorldAlt() {
    TestWaWorlds(WAT_WORLDS, WAT_QUERIES);
    TestWaManager();
    TestWaShadow();
#ifndef WORLDALT_HOST_TEST
    TestWaInstall();
#endif
    WatReset(32);
    ts3native_worldalt.waReady = 0;
    g_waAll = false, g_waSearch = NULL, g_waPathFree = NULL, g_waPushOrig = NULL, g_waPathOrig = NULL;
}
