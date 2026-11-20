// docs/676 §14: [WorldPrecompute] WorldAlt, the pure part (no Windows, no libc beyond memset / memcpy): a lower-bound copy
// of the world waypoint graph R with one node per water body, landmark distance tables on it, and the bound a search adds
// to the game's heuristic. Included by worldalt.cpp and (through it) by worldalt-host-test.cpp.
//
// The lower-bound graph. Nodes: every waypoint of R (chunk order, then waypoint order) and WA_BODIES body nodes. Edges are
// undirected; each carries, per cost mode (0 = the game's mode 1 pedestrian 0x674E40, 1 = mode 3 pedestrian + car
// 0x674250), the smallest cost either direction can have for any search, in units of 1 / WA_UNIT:
//   real edge = (smallest factor) x (smallest length). Factor by edge byte: 0 none (a wall), 1 lot hub: 10 x min(1,
//   trespass) (the hub rules give 10, 30, trespass x 10 or x 1000, or a flat 5,000,000), 2: trespass x 10, 3: mode 1
//   10 / sidewalk, mode 3 10 / street when either end is type 0x0D, else 10 / riding, 4 and 5: 10 / sidewalk, any other
//   10; always x min(1, keep-type) (the game multiplies by keep-type when the two types differ; we do not read the types,
//   they change with every water grid build). Each tuning value is the smaller (or, where it divides, the larger) of the
//   SHORT and LONG cost sets. Length: the distance between the two waypoints, except edge byte 1, where the game takes
//   the from position's in-chunk offsets from the parent's record (docs/99 §10.1 4d): the distance from the target to the
//   from chunk's square, which no offset pair can undercut.
//   water link (waypoint <-> body, both of G's shoreline maps): mode 1 and a waypoint of type 0x15: the smaller water-lot
//   value (the game returns it raw); else max(10 x body cells, 1). Mode 3's lot-bound water entry and the links of a
//   virtual start or goal cost less than that: such a search takes no bound (worldalt.cpp, WaEligible).
// Everything else a search adds (blocked lot edges, the job's polygon set, the circle, the point test, the blocked-node
// map, lot sets, boat kind, the retry flag) can only raise a cost or remove an edge, so a distance on this graph never
// exceeds the cost of any path the game's search can take. Each cost is scaled by (1 - 1e-5) before it is cut to whole
// units, which covers the game's float32 rounding of length x factor.
//
// Distances are whole units in 32 bits, so a table entry is exact on the unit graph and |d(L, u) - d(L, goal)| obeys the
// triangle rule on it: the bound is admissible and consistent, and the game's search with max(its heuristic, bound) keeps
// returning a cheapest path (docs/676 §14.3).
#ifndef WA_SQRT
#include <math.h>
#define WA_SQRT(x) sqrt(x)
#endif

enum { WA_MAXK = 64, WA_ACT = 8, WA_BODIES = 256, WA_UNIT = 16, WA_MODES = 2, WA_DEPTH = 96, WA_MAXLINKS = 1 << 18 };
static const DWORD WA_INF = 0xFFFFFFFF, WA_FAR = 0xF0000000;  // unreached; an entry at or above WA_FAR gives no bound
static const DWORD WA_MAXCOST = 0x3FFFFFFF;

// the six tuning values the cost modes read, [0] SHORT (container +0x28), [1] LONG (+0x4C): keep-type, trespass, sidewalk,
// riding, street, water lot
struct WaTune { float v[2][6]; };
enum { WAT_KEEP, WAT_TRES, WAT_SIDE, WAT_RIDE, WAT_STREET, WAT_WLOT };

// R as the game holds it (docs/99 §1.2, §1.4)
struct WaR { DWORD w, h, chunks; const DWORD* off; const u8* raw; DWORD size; };
static bool WaRView(const u8* R, WaR* v) {
    v->w = v->h = v->chunks = v->size = 0, v->off = NULL, v->raw = NULL;
    if (!R) return false;
    v->w = *(const DWORD*)R, v->h = *(const DWORD*)(R + 4);
    v->off = *(const DWORD* const*)(R + 8), v->raw = *(const u8* const*)(R + 0xC), v->size = *(const DWORD*)(R + 0x10);
    if (!v->w || !v->h || v->w > 0xFFFE || v->h > 0xFFFE || v->w * v->h > 0xFFFE || !v->off || !v->raw || v->size < 2) return false;
    v->chunks = v->w * v->h;  // a chunk id is 16 bits and 0xFFFF is the virtual chunk
    return true;
}
static inline DWORD WaU16(const u8* p) { return (DWORD)p[0] | ((DWORD)p[1] << 8); }
// waypoints of chunk c, 0 for a chunk that does not fit the record area
static DWORD WaChunkCount(const WaR& r, DWORD c) {
    const DWORD o = r.off[c];
    if (o > r.size - 2) return 0;
    const DWORD n = WaU16(r.raw + o);
    return n > 1024 || o + 2 + 2 * n > r.size ? 0 : n;
}
// record i of chunk c (i below WaChunkCount), NULL when it does not fit; *edges = its edge bytes or NULL
static const u8* WaRecord(const WaR& r, DWORD c, DWORD i, const u8** edges) {
    const DWORD o = r.off[c], at = o + WaU16(r.raw + o + 2 + 2 * i);
    *edges = NULL;
    if (at < o || at > r.size - 5) return NULL;
    const u8* rec = r.raw + at;
    const DWORD n = rec[0], len = 5 + 2 * n + ((rec[1] & 0x40) ? n : 0);
    if (len > r.size - at) return NULL;
    if (rec[1] & 0x40) *edges = rec + 5 + 2 * n;
    return rec;
}
// the A*'s neighbour decode (0x6765F3..0x676622, 16-bit arithmetic): target chunk and waypoint of reference `ref` of chunk c
static inline void WaRefTarget(const WaR& r, DWORD c, DWORD ref, DWORD* tc, DWORD* ti) {
    *tc = (((ref >> 13) - 3) * r.w + ((ref >> 10) & 7) + c - 3) & 0xFFFF;
    *ti = ref & 0x3FF;
}

// G's two shoreline trees (docs/100 §2; EASTL red-black nodes: right +0, left +4, value from +0x10): in-order walk without
// parent pointers. f(node) per entry. False = not a tree we can walk (too deep, too many nodes).
template <class F> static bool WaTreeWalk(const u8* anchor, F f) {
    const u8* stack[WA_DEPTH];
    int sp = 0;
    DWORD seen = 0;
    const u8* n = *(const u8* const*)(anchor + 8);  // root
    while (n || sp) {
        while (n) {
            if (sp == WA_DEPTH) return false;
            stack[sp++] = n;
            n = *(const u8* const*)(n + 4);
        }
        n = stack[--sp];
        if (++seen > WA_MAXLINKS) return false;
        f(n);
        n = *(const u8* const*)n;
    }
    return true;
}
static inline DWORD WaBodyCells(const u8* G, DWORD body) { return body < 0x80 ? *(const DWORD*)(G + 0x3A0 + 8 * body) : 0; }  // 0x66D8D0
static inline DWORD WaMix(DWORD h, DWORD v) {
    h ^= v;
    h *= 0x01000193;
    return h ^ (h >> 15);
}
// type of the waypoint a shoreline entry names (key = chunk << 16 | waypoint), 0xFF when it is not a waypoint of R
static DWORD WaKeyType(const WaR& r, DWORD key) {
    const DWORD c = key >> 16, i = key & 0xFFFF;
    const u8* e = NULL;
    if (c >= r.chunks || i >= WaChunkCount(r, c)) return 0xFF;
    const u8* rec = WaRecord(r, c, i, &e);
    return rec ? (DWORD)(rec[1] & 0x1F) : 0xFF;
}
// What the link part of the graph is built from: every entry of both trees (waypoint, body, "is type 0x15") and the body
// cell counts. Compared at every search. False = G cannot be read.
static bool WaLinkHash(const WaR& r, const u8* G, DWORD* hash, DWORD* links) {
    DWORD h = 0x811C9DC5, n = 0;
    const bool a = WaTreeWalk(G + 0x30, [&](const u8* nd) {
        const DWORD key = *(const DWORD*)(nd + 0x10);
        h = WaMix(WaMix(WaMix(h, key), nd[0x14]), WaKeyType(r, key) == 0x15);
        n++;
    });
    h = WaMix(h, 0x4C4C4C4C);
    const bool b = a && WaTreeWalk(G + 0x4C, [&](const u8* nd) {
        const DWORD key = *(const DWORD*)(nd + 0x14);
        h = WaMix(WaMix(WaMix(h, key), nd[0x10]), WaKeyType(r, key) == 0x15);
        n++;
    });
    for (DWORD i = 0; i < 0x80; i++) h = WaMix(h, WaBodyCells(G, i));
    *hash = h, *links = n;
    return a && b;
}
// What the land part is built from: chunk counts, and per record its neighbour count, "has edge bytes", "is type 0x0D",
// position, references and edge bytes. Not the blocked bit and not the other type bits (they move in play and the graph
// does not read them). Checked now and then (R's records are written by the world builder only).
static DWORD WaLandHash(const WaR& r) {
    DWORD h = WaMix(WaMix(0x811C9DC5, r.w), r.h);
    for (DWORD c = 0; c < r.chunks; c++) {
        const DWORD n = WaChunkCount(r, c);
        h = WaMix(h, n);
        for (DWORD i = 0; i < n; i++) {
            const u8* e = NULL;
            const u8* rec = WaRecord(r, c, i, &e);
            if (!rec) { h = WaMix(h, 0xDEAD); continue; }
            h = WaMix(h, rec[0] | ((DWORD)(rec[1] & 0x40) << 2) | ((rec[1] & 0x1F) == 0x0D ? 0x200 : 0) | ((DWORD)rec[3] << 16) | ((DWORD)rec[4] << 24));
            for (DWORD k = 0; k < rec[0]; k++) h = WaMix(h, WaU16(rec + 5 + 2 * k) | (e ? (DWORD)e[k] << 16 : 0));
        }
    }
    return h;
}

// ---- the lower-bound graph ----
struct WaGraph {
    DWORD n, nodes, chunks, slots, comps;
    DWORD* base;              // chunks + 1: node index of each chunk's first waypoint
    DWORD* off;               // nodes + 1
    DWORD* to;                // slots
    DWORD* cost[WA_MODES];    // slots, units
    DWORD* comp;              // n: land component of each waypoint
    DWORD* compSize;          // comps (room for n): waypoints
    DWORD* compCnt;           // scratch, comps
    DWORD* heap;              // Dijkstra: nodes
    DWORD* pos;               // nodes
    DWORD* dist;              // nodes (scratch tree)
    DWORD* minD[WA_MODES];    // n: distance to the nearest landmark of the waypoint's own component (WA_INF none)
    DWORD minK[WA_MODES];     // landmarks of the mode's table that minD covers
};
struct WaSizes { DWORD n, chunks, edges, links; };
// counts for the allocation; false = R or G cannot be read
static bool WaMeasure(const WaR& r, const u8* G, WaSizes* s) {
    s->n = s->edges = s->links = 0, s->chunks = r.chunks;
    for (DWORD c = 0; c < r.chunks; c++) {
        const DWORD n = WaChunkCount(r, c);
        s->n += n;
        for (DWORD i = 0; i < n; i++) {
            const u8* e = NULL;
            const u8* rec = WaRecord(r, c, i, &e);
            if (rec && e) s->edges += rec[0];
        }
    }
    DWORD h = 0;
    return s->n != 0 && s->n < 0x00FFFFFF && WaLinkHash(r, G, &h, &s->links);
}
// DWORDs one graph needs (the caller allocates them as one block): slots = 2 x (edges + links) covers a reverse for every
// edge that has none
static DWORD WaGraphWords(const WaSizes& s) {
    const DWORD nodes = s.n + WA_BODIES, slots = 2 * (s.edges + s.links);
    return (s.chunks + 1) + (nodes + 1) + slots * (1 + WA_MODES) + (3 + WA_MODES) * s.n + 3 * nodes;
}

struct WaFactors { double hub, tres, side, street[2], keep, wlot; bool ok; };
// false = a tuning value the bound cannot be built on (not positive, not finite): no tables
static WaFactors WaFactorsOf(const WaTune& t) {
    WaFactors f = {};
    f.ok = true;
    for (int s = 0; s < 2; s++)
        for (int i = 0; i < 5; i++) {
            const float v = t.v[s][i];
            if (!(v > 1e-6f) || !(v < 1e12f)) f.ok = false;  // NaN fails both
        }
    if (!f.ok) return f;
    const auto lo = [&](int i) { return (double)(t.v[0][i] < t.v[1][i] ? t.v[0][i] : t.v[1][i]); };
    const auto hi = [&](int i) { return (double)(t.v[0][i] > t.v[1][i] ? t.v[0][i] : t.v[1][i]); };
    f.keep = lo(WAT_KEEP) < 1.0 ? lo(WAT_KEEP) : 1.0;
    f.tres = 10.0 * lo(WAT_TRES);
    f.hub = f.tres < 10.0 ? f.tres : 10.0;
    f.side = 10.0 / hi(WAT_SIDE);
    f.street[0] = 10.0 / hi(WAT_RIDE), f.street[1] = 10.0 / hi(WAT_STREET);
    const double wl = lo(WAT_WLOT);
    f.wlot = wl > 0 && wl < 1e12 ? wl : 0.0;  // the raw value is the cost; anything odd counts as free
    return f;
}
static inline DWORD WaUnits(double cost) {
    const double u = cost * (double)WA_UNIT * (1.0 - 1e-5);
    return !(u > 0) ? 0 : u >= (double)WA_MAXCOST ? WA_MAXCOST : (DWORD)u;
}
static DWORD WaEdgeUnits(const WaFactors& f, int mode, DWORD eb, bool street, double len) {
    double k = 10.0;
    if (eb == 1) k = f.hub;
    else if (eb == 2) k = f.tres;
    else if (eb == 3) k = mode == 0 ? f.side : f.street[street ? 1 : 0];
    else if (eb == 4 || eb == 5) k = f.side;
    return WaUnits(len * k * f.keep);
}
static DWORD WaLinkUnits(const WaFactors& f, int mode, bool is15, DWORD cells) {
    if (mode == 0 && is15) return WaUnits(f.wlot);
    const double fee = 10.0 * (double)cells;
    return WaUnits(fee > 1.0 ? fee : 1.0);
}
static inline double WaPosX(const WaR& r, DWORD c, const u8* rec) { return (double)(c % r.w) * 32.0 + rec[3] * 0.125; }
static inline double WaPosZ(const WaR& r, DWORD c, const u8* rec) { return (double)(c / r.w) * 32.0 + rec[4] * 0.125; }
// distance from (x, z) to the square the from position can lie in when its offsets come from another record
static double WaChunkDist(const WaR& r, DWORD c, double x, double z) {
    const double x0 = (double)(c % r.w) * 32.0, z0 = (double)(c / r.w) * 32.0, x1 = x0 + 31.875, z1 = z0 + 31.875;
    const double dx = x < x0 ? x0 - x : x > x1 ? x - x1 : 0, dz = z < z0 ? z0 - z : z > z1 ? z - z1 : 0;
    return WA_SQRT(dx * dx + dz * dz);
}

// One directed edge list, then made symmetric: pass 0 counts, pass 1 fills. `extra` reverses (edges whose target does not
// list the source) were collected by the caller's first symmetry pass.
struct WaEdge { DWORD from, to, cost[WA_MODES]; };
template <class F> static void WaEachEdge(const WaR& r, const u8* G, const WaGraph& g, const WaFactors& f, F emit) {
    for (DWORD c = 0; c < r.chunks; c++) {
        const DWORD n = g.base[c + 1] - g.base[c];
        for (DWORD i = 0; i < n; i++) {
            const u8* e = NULL;
            const u8* rec = WaRecord(r, c, i, &e);
            if (!rec || !e) continue;  // no edge bytes: every edge reads byte 0, a wall
            for (DWORD k = 0; k < rec[0]; k++) {
                DWORD tc = 0, ti = 0;
                WaRefTarget(r, c, WaU16(rec + 5 + 2 * k), &tc, &ti);
                if (!e[k] || tc >= r.chunks || ti >= g.base[tc + 1] - g.base[tc]) continue;
                const u8* te = NULL;
                const u8* trec = WaRecord(r, tc, ti, &te);
                if (!trec) continue;
                const double tx = WaPosX(r, tc, trec), tz = WaPosZ(r, tc, trec);
                double len = 0;
                if (e[k] == 1) len = WaChunkDist(r, c, tx, tz);
                else {
                    const double dx = WaPosX(r, c, rec) - tx, dz = WaPosZ(r, c, rec) - tz;
                    len = WA_SQRT(dx * dx + dz * dz);
                }
                const bool street = (rec[1] & 0x1F) == 0x0D || (trec[1] & 0x1F) == 0x0D;
                WaEdge ed = {g.base[c] + i, g.base[tc] + ti, {WaEdgeUnits(f, 0, e[k], street, len), WaEdgeUnits(f, 1, e[k], street, len)}};
                emit(ed);
            }
        }
    }
    const auto link = [&](DWORD key, DWORD body, bool wpFirst) {
        const DWORD c = key >> 16, i = key & 0xFFFF;
        if (c >= r.chunks || i >= g.base[c + 1] - g.base[c]) return;
        const bool is15 = WaKeyType(r, key) == 0x15;
        const DWORD cells = WaBodyCells(G, body & 0xFF), wp = g.base[c] + i, bn = g.n + (body & 0xFF);
        WaEdge ed = {wpFirst ? wp : bn, wpFirst ? bn : wp, {WaLinkUnits(f, 0, is15, cells), WaLinkUnits(f, 1, is15, cells)}};
        emit(ed);
    };
    WaTreeWalk(G + 0x30, [&](const u8* nd) { link(*(const DWORD*)(nd + 0x10), nd[0x14], true); });
    WaTreeWalk(G + 0x4C, [&](const u8* nd) { link(*(const DWORD*)(nd + 0x14), nd[0x10], false); });
}
// Builds the graph into `mem` (WaGraphWords DWORDs). False = unreadable input or tuning.
static bool WaBuildGraph(const WaR& r, const u8* G, const WaTune& tune, const WaSizes& sz, DWORD* mem, WaGraph* g) {
    const WaFactors f = WaFactorsOf(tune);
    if (!f.ok) return false;
    const DWORD nodes = sz.n + WA_BODIES, maxSlots = 2 * (sz.edges + sz.links);
    g->n = sz.n, g->nodes = nodes, g->chunks = sz.chunks, g->slots = 0, g->comps = 0;
    g->base = mem, mem += sz.chunks + 1;
    g->off = mem, mem += nodes + 1;
    g->to = mem, mem += maxSlots;
    for (int m = 0; m < WA_MODES; m++) g->cost[m] = mem, mem += maxSlots;
    g->comp = mem, mem += sz.n;
    g->compSize = mem, mem += sz.n;
    g->compCnt = mem, mem += sz.n;
    g->heap = mem, mem += nodes;
    g->pos = mem, mem += nodes;
    g->dist = mem, mem += nodes;
    for (int m = 0; m < WA_MODES; m++) g->minD[m] = mem, mem += sz.n, g->minK[m] = 0;
    DWORD at = 0;
    for (DWORD c = 0; c < r.chunks; c++) g->base[c] = at, at += WaChunkCount(r, c);
    g->base[r.chunks] = at;
    if (at != sz.n) return false;
    // forward lists: count, fill; the same (from, to) pair twice keeps one slot with the smaller costs
    memset(g->off, 0, (nodes + 1) * sizeof(DWORD));
    DWORD total = 0;
    WaEachEdge(r, G, *g, f, [&](const WaEdge& e) { g->off[e.from + 1]++, total++; });
    if (total > sz.edges + sz.links) return false;
    // room for one reverse per edge: counted now, used only where the reverse is missing
    WaEachEdge(r, G, *g, f, [&](const WaEdge& e) { g->off[e.to + 1]++; });
    for (DWORD i = 0; i < nodes; i++) g->off[i + 1] += g->off[i];
    DWORD* fill = g->pos;  // scratch: used slots per node
    memset(fill, 0, nodes * sizeof(DWORD));
    const auto find = [&](DWORD from, DWORD to) {
        for (DWORD k = g->off[from]; k < g->off[from] + fill[from]; k++)
            if (g->to[k] == to) return k;
        return WA_INF;
    };
    const auto put = [&](DWORD from, DWORD to, const DWORD* cost, bool merge) {
        DWORD k = merge ? find(from, to) : WA_INF;
        if (k == WA_INF) {
            k = g->off[from] + fill[from]++;
            g->to[k] = to;
            for (int m = 0; m < WA_MODES; m++) g->cost[m][k] = cost[m];
            return;
        }
        for (int m = 0; m < WA_MODES; m++)
            if (cost[m] < g->cost[m][k]) g->cost[m][k] = cost[m];
    };
    WaEachEdge(r, G, *g, f, [&](const WaEdge& e) {
        if (e.from < g->n && e.to < g->n) { put(e.from, e.to, e.cost, true), put(e.to, e.from, e.cost, true); return; }
        // a link: both trees list it and its cost depends on the pair alone, so a second sighting adds nothing. Looked up on
        // the waypoint's side only (a sea's own list has thousands of entries).
        const DWORD wp = e.from < g->n ? e.from : e.to, body = e.from < g->n ? e.to : e.from;
        if (find(wp, body) != WA_INF) return;
        put(wp, body, e.cost, false), put(body, wp, e.cost, false);
    });
    // compact (the reverse room is mostly unused: R lists both directions)
    DWORD w = 0;
    for (DWORD i = 0; i < nodes; i++) {
        const DWORD from = g->off[i], cnt = fill[i];
        g->off[i] = w;
        for (DWORD k = 0; k < cnt; k++, w++) {
            g->to[w] = g->to[from + k];
            for (int m = 0; m < WA_MODES; m++) g->cost[m][w] = g->cost[m][from + k];
        }
    }
    g->off[nodes] = w, g->slots = w;
    // land components: waypoints joined by real edges (the links are left out; bodies have none)
    for (DWORD i = 0; i < sz.n; i++) g->comp[i] = g->minD[0][i] = g->minD[1][i] = WA_INF;
    DWORD* stack = g->heap;
    for (DWORD i = 0; i < sz.n; i++) {
        if (g->comp[i] != WA_INF) continue;
        const DWORD id = g->comps++;
        DWORD sp = 0, size = 0;
        stack[sp++] = i, g->comp[i] = id;
        while (sp) {
            const DWORD u = stack[--sp];
            size++;
            for (DWORD s = g->off[u]; s < g->off[u + 1]; s++) {
                const DWORD v = g->to[s];
                if (v < sz.n && g->comp[v] == WA_INF) g->comp[v] = id, stack[sp++] = v;
            }
        }
        g->compSize[id] = size;
    }
    return true;
}

// Shortest distances from `src` over cost mode `mode` into out[node] (WA_INF = not reached). Binary heap with positions;
// a sum at or above WA_FAR is not relaxed.
static void WaDijkstra(const WaGraph& g, int mode, DWORD src, DWORD* out) {
    DWORD* heap = g.heap;
    DWORD* pos = g.pos;
    const DWORD* cost = g.cost[mode];
    for (DWORD i = 0; i < g.nodes; i++) out[i] = WA_INF, pos[i] = WA_INF;
    DWORD hn = 0;
    const auto up = [&](DWORD i) {
        const DWORD v = heap[i], d = out[v];
        while (i) {
            const DWORD p = (i - 1) / 2;
            if (out[heap[p]] <= d) break;
            heap[i] = heap[p], pos[heap[i]] = i, i = p;
        }
        heap[i] = v, pos[v] = i;
    };
    out[src] = 0, heap[hn++] = src, pos[src] = 0;
    while (hn) {
        const DWORD u = heap[0], du = out[u];
        pos[u] = WA_INF - 1;  // settled
        const DWORD last = heap[--hn];
        if (hn) {
            DWORD i = 0;
            const DWORD dl = out[last];
            for (;;) {
                DWORD c = 2 * i + 1;
                if (c >= hn) break;
                if (c + 1 < hn && out[heap[c + 1]] < out[heap[c]]) c++;
                if (out[heap[c]] >= dl) break;
                heap[i] = heap[c], pos[heap[i]] = i, i = c;
            }
            heap[i] = last, pos[last] = i;
        }
        for (DWORD s = g.off[u]; s < g.off[u + 1]; s++) {
            const DWORD v = g.to[s], nd = du + cost[s];
            if (nd >= WA_FAR || nd < du || pos[v] == WA_INF - 1 || nd >= out[v]) continue;
            out[v] = nd;
            if (pos[v] == WA_INF) pos[v] = hn, heap[hn++] = v;
            up(pos[v]);
        }
    }
}

// ---- landmark tables ----
struct WaTab {
    DWORD* d;           // nodes x kmax, one row per node
    DWORD kmax, k;      // landmarks wanted, built
    DWORD lm[WA_MAXK];  // their nodes
};
// Adds one landmark (one or two shortest-path trees). Which waypoint: the land component with the largest
// size / (landmarks + 1) gets the next one; inside it the waypoint farthest from the component's landmarks (minD; its
// first: farthest from the first landmark overall, or from the component's first waypoint). Any choice gives a valid bound; this
// one spreads them. False = nothing left to add.
static bool WaAddLandmark(WaGraph& g, int mode, WaTab* t) {
    if (t->k >= t->kmax || !g.comps) return false;
    DWORD* minD = g.minD[mode];
    if (g.minK[mode] != t->k) {  // a graph built after this table was started: fold its landmarks in again
        for (DWORD v = 0; v < g.n; v++) {
            DWORD m = WA_INF;
            for (DWORD j = 0; j < t->k; j++)
                if (g.comp[t->lm[j]] == g.comp[v] && t->d[v * t->kmax + j] < m) m = t->d[v * t->kmax + j];
            minD[v] = m;
        }
        g.minK[mode] = t->k;
    }
    memset(g.compCnt, 0, g.comps * sizeof(DWORD));
    for (DWORD j = 0; j < t->k; j++) g.compCnt[g.comp[t->lm[j]]]++;
    DWORD best = WA_INF;
    for (DWORD c = 0; c < g.comps; c++) {
        if (g.compSize[c] <= g.compCnt[c]) continue;  // every waypoint of it is a landmark already
        if (best == WA_INF || (unsigned long long)g.compSize[c] * (g.compCnt[best] + 1) > (unsigned long long)g.compSize[best] * (g.compCnt[c] + 1)) best = c;
    }
    if (best == WA_INF) return false;
    DWORD pick = WA_INF, pickD = 0;
    if (g.compCnt[best]) {
        for (DWORD v = 0; v < g.n; v++)
            if (g.comp[v] == best && minD[v] != WA_INF && (pick == WA_INF || minD[v] > pickD)) pick = v, pickD = minD[v];
    } else {
        const DWORD* from = NULL;
        DWORD stride = 1;
        if (t->k) from = t->d, stride = t->kmax;  // column 0: the first landmark's tree
        for (int pass = 0; pass < 2 && pick == WA_INF; pass++) {
            if (!from) {
                DWORD first = 0;
                while (g.comp[first] != best) first++;
                WaDijkstra(g, mode, first, g.dist);
                from = g.dist, stride = 1;
            }
            for (DWORD v = 0; v < g.n; v++)
                if (g.comp[v] == best && from[v * stride] < WA_FAR && (pick == WA_INF || from[v * stride] > pickD)) pick = v, pickD = from[v * stride];
            from = NULL;
        }
    }
    if (pick == WA_INF) return false;
    // the tree goes into the compact scratch row first (the table's rows are a cache line apart per node)
    WaDijkstra(g, mode, pick, g.dist);
    DWORD* col = t->d + t->k;
    for (DWORD v = 0; v < g.nodes; v++) col[v * t->kmax] = g.dist[v];
    for (DWORD v = 0; v < g.n; v++)
        if (g.comp[v] == best && g.dist[v] < minD[v]) minD[v] = g.dist[v];
    t->lm[t->k++] = pick;
    g.minK[mode] = t->k;
    return true;
}

// ---- the bound ----
struct WaQuery {
    const DWORD* d;      // the table
    DWORD kmax, act;     // row width; landmarks in use
    DWORD col[WA_ACT];   // their columns
    DWORD goal[WA_ACT];  // their distance to the goal
};
// The landmarks that say most about start -> goal (at most WA_ACT). False = none says anything.
static bool WaQueryInit(const WaTab& t, DWORD start, DWORD goal, WaQuery* q) {
    q->d = t.d, q->kmax = t.kmax, q->act = 0;
    DWORD score[WA_ACT] = {};
    for (DWORD j = 0; j < t.k; j++) {
        const DWORD a = t.d[start * t.kmax + j], b = t.d[goal * t.kmax + j];
        if (b >= WA_FAR) continue;  // this landmark does not reach the goal: it bounds nothing
        const DWORD s = a >= WA_FAR ? 0 : a > b ? a - b : b - a;
        DWORD at = q->act;
        if (at == WA_ACT) {
            at = 0;
            for (DWORD i = 1; i < WA_ACT; i++)
                if (score[i] < score[at]) at = i;
            if (score[at] >= s) continue;
        } else q->act++;
        score[at] = s, q->col[at] = j, q->goal[at] = b;
    }
    return q->act != 0;
}
// lower bound of the cost node -> goal, in units
static inline DWORD WaBoundUnits(const WaQuery& q, DWORD node) {
    const DWORD* row = q.d + node * q.kmax;
    DWORD best = 0;
    for (DWORD i = 0; i < q.act; i++) {
        const DWORD a = row[q.col[i]], b = q.goal[i];
        if (a >= WA_FAR) continue;
        const DWORD s = a > b ? a - b : b - a;
        if (s > best) best = s;
    }
    return best;
}
// units as the float the search adds, rounded towards zero
static inline float WaUnitsToCost(DWORD units) {
    const double x = (double)units / (double)WA_UNIT;
    float f = (float)x;
    if ((double)f > x) {
        DWORD bits = 0;
        memcpy(&bits, &f, 4);
        bits--;
        memcpy(&f, &bits, 4);
    }
    return f;
}
// node index of a search reference {chunk, waypoint} (0xFFFF = body), WA_INF when it names no node
static inline DWORD WaNodeOf(const DWORD* base, DWORD chunks, DWORD n, DWORD chunk, DWORD wp) {
    if (chunk == 0xFFFF) return n + (wp & 0xFF);
    if (chunk >= chunks) return WA_INF;
    const DWORD i = base[chunk] + wp;
    return i < base[chunk + 1] ? i : WA_INF;
}
