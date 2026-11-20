// docs/679 §5 [WaypointSnap] Gather: the parts of wpgather.cpp without the game or Windows (wpgather-test.h,
// wpgather-host-test.cpp).
// The game's gather 0x654790 walks the chunks of a candidate area column by column and appends, per chunk, that chunk's
// waypoints in record order; what one chunk contributes depends on the chunk, the filter bits of the area's flags and the
// routing data, never on the point or on the other chunks. So the memo is per (chunk, filter bits):
//  - WpgPlanArea: the chunks an area covers, in the game's order, from the same inputs the game uses (point, side class,
//    extent, try, world origin and size). The game computes them in x87 floats; here in doubles, and an area with any value
//    within WPG_EDGE of a chunk border or a world bound is refused (0: the caller runs the game's gather), so the two can
//    never disagree about a chunk.
//  - WpgTab: (chunk, filter bits) -> that chunk's candidate rows by value (position, waypoint, chunk, record offset, type).
//    The whole table belongs to one context (the routing objects and the world geometry) and one write sequence; another
//    context or sequence empties it. A row holds no pointer: the record is an offset into the record area of that context.
//  - WpgBuild: the area's list = the chunks' rows in plan order; a chunk that is not in the table is fetched through a
//    callback (the game's gather on a one-chunk area) and stored.
#pragma once

enum {
    WPG_SLOTS = 8192,    // (chunk, filter) entries; a power of two; the table empties itself at 3/4
    WPG_POOL = 131072,   // rows of all entries (20 bytes each)
    WPG_OUT = 16384,     // rows of one area the caller's buffer holds
    WPG_MAXCH = 36,      // chunks of one area: 6 x 6 on the third try
    WPG_PROBE = 16,      // linear probe length
};
#define WPG_EDGE 0.01    // world units: the float error of the game's own sums is under 0.004 below 32768
#define WPG_FILTER 0x4007u  // the flag bits the gather's type filter reads (0x800, the water point test, is never stored)

struct WpgRow {
    float x, z;               // the game's row +0x0C, +0x10
    unsigned short wp, chunk; // +0x14, +0x16
    unsigned int rec;         // +0x18 as an offset from the record area's base
    unsigned int type;        // +0x1C
};
struct WpgGameRow {  // the 32-byte row of the game's candidate vector
    float s0, s1, s2, x, z;
    unsigned short wp, chunk;
    unsigned int rec;  // a pointer in the game (32-bit)
    unsigned int type;
};
struct WpgCtx {
    unsigned int w[14];  // the routing objects and geometry words a table belongs to
};
struct WpgSlot {
    unsigned int gen;       // == tab gen: in use
    unsigned int key;       // chunk | filter index << 16
    unsigned int first, n;  // rows in the pool
    unsigned int chunkOff;  // the chunk's offset word and waypoint count when stored (a tripwire, checked at use)
    unsigned int wpCount;
    unsigned int ms;        // when stored
};
struct WpgTab {
    WpgCtx ctx;
    unsigned int seq, gen, used, poolUsed, valid;
    WpgSlot slot[WPG_SLOTS];
    WpgRow pool[WPG_POOL];
};
struct WpgGeom {
    double orgX, orgZ, maxX, maxZ;  // world manager +0xB4, +0xB8, +0xBC, +0xC0
    unsigned int w, h;              // chunks per row, rows
};
struct WpgArea {
    float x, z;                       // area +0x00, +0x04
    unsigned int cx, ex, cz, ez, tryN; // +0x14, +0x18, +0x1C, +0x20, +0x24
};
struct WpgPlan {
    int n;
    unsigned int chunk[WPG_MAXCH];
    float px[WPG_MAXCH], pz[WPG_MAXCH];  // a point of that chunk the game's bound tests accept (the area's own sums)
};
struct WpgStats {
    unsigned int hits, fills, stale, full, rows;
};

// the gather's offset table 0xFED2A8, indexed by side class + column
static const double kWpgOff[7] = {-32.0, 0.0, 32.0, 64.0, 96.0, 128.0, 160.0};

// one axis: the columns the game visits. -1 = refuse, else the count; idx[i] = chunk column, v[i] = the game's sum
static int WpgAxis(double p, unsigned int cls, unsigned int ext, unsigned int tryN, double org, double maxV, unsigned int dim, unsigned int* idx, float* v) {
    if (cls > 1 || ext < 1 || ext > 2 || tryN > 2) return -1;
    if (!(p > -32768.0 && p < 32768.0)) return -1;  // also NaN
    const unsigned int cols = ext + tryN * 2;
    int n = 0;
    for (unsigned int i = 0; i < cols; i++) {
        const double s = kWpgOff[cls + i] + p - (double)tryN * 32.0;
        if (s > -WPG_EDGE && s < WPG_EDGE) return -1;                // the game's 0 <= s
        if (s > maxV - WPG_EDGE && s < maxV + WPG_EDGE) return -1;   // the game's s < size
        if (!(s >= 0.0 && s < maxV)) continue;                       // the game skips this column
        const double rel = s - org;
        if (!(rel >= WPG_EDGE)) return -1;  // left of the origin: the game would index below its table
        const double q = rel / 32.0;
        const unsigned int c = (unsigned int)(int)q;  // under 1024: no unsigned conversion helper
        const double frac = rel - (double)c * 32.0;
        if (frac < WPG_EDGE || frac > 32.0 - WPG_EDGE) return -1;
        if (c >= dim) return -1;
        idx[n] = c;
        v[n] = (float)s;
        n++;
    }
    return n;
}

// 1 = *p holds the chunks of the area in the game's order (possibly none); 0 = not decidable here, run the game's gather
static int WpgPlanArea(const WpgGeom& g, const WpgArea& a, WpgPlan* p) {
    unsigned int ix[6] = {}, iz[6] = {};
    float vx[6] = {}, vz[6] = {};
    p->n = 0;
    if (!g.w || !g.h || g.w > 1024 || g.h > 1024 || (unsigned long long)g.w * g.h > 65536) return 0;  // the row's chunk is 16 bits
    if (!(g.maxX > 0.0 && g.maxX < 32768.0 && g.maxZ > 0.0 && g.maxZ < 32768.0)) return 0;
    if (!(g.orgX > -32768.0 && g.orgX < 32768.0 && g.orgZ > -32768.0 && g.orgZ < 32768.0)) return 0;
    const int nx = WpgAxis((double)a.x, a.cx, a.ex, a.tryN, g.orgX, g.maxX, g.w, ix, vx);
    const int nz = WpgAxis((double)a.z, a.cz, a.ez, a.tryN, g.orgZ, g.maxZ, g.h, iz, vz);
    if (nx < 0 || nz < 0) return 0;
    for (int i = 0; i < nx; i++)
        for (int j = 0; j < nz; j++) {
            p->chunk[p->n] = ix[i] + iz[j] * g.w;
            p->px[p->n] = vx[i];
            p->pz[p->n] = vz[j];
            p->n++;
        }
    return 1;
}

// the filter bits as a small index (0-15); 0x800 areas never get here
static unsigned int WpgFilterIx(unsigned int flags) { return (flags & 7) | ((flags & 0x4000) ? 8u : 0u); }

static void WpgReset(WpgTab* t) {
    t->gen++;
    if (!t->gen) {  // wrapped: the slots' gen words could match again
        for (int i = 0; i < WPG_SLOTS; i++) t->slot[i].gen = 0;
        t->gen = 1;
    }
    t->used = 0;
    t->poolUsed = 0;
}
static bool WpgCtxEq(const WpgCtx& a, const WpgCtx& b) {
    for (int i = 0; i < 14; i++)
        if (a.w[i] != b.w[i]) return false;
    return true;
}
// Called before every use. 0 = the table stands; 1 = emptied for another context; 2 = emptied for another write sequence
static int WpgValidate(WpgTab* t, const WpgCtx& ctx, unsigned int seq) {
    int why = 0;
    if (!t->valid || !t->gen || !WpgCtxEq(t->ctx, ctx)) why = 1;
    else if (t->seq != seq) why = 2;
    if (!why) return 0;
    WpgReset(t);
    t->ctx = ctx;
    t->seq = seq;
    t->valid = 1;
    return why;
}
static unsigned int WpgSlotHash(unsigned int key) {
    unsigned int h = key * 0x9E3779B1u;
    h ^= h >> 15;
    return h & (WPG_SLOTS - 1);
}
static WpgSlot* WpgFind(WpgTab* t, unsigned int chunk, unsigned int fix) {
    const unsigned int key = chunk | fix << 16;
    unsigned int i = WpgSlotHash(key);
    for (int k = 0; k < WPG_PROBE; k++, i = (i + 1) & (WPG_SLOTS - 1)) {
        WpgSlot* s = &t->slot[i];
        if (s->gen != t->gen) return NULL;  // entries are never removed one by one, so a free slot ends the run
        if (s->key == key) return s;
    }
    return NULL;
}
// NULL = no room (the caller empties the table and stores again)
static WpgSlot* WpgStore(WpgTab* t, unsigned int chunk, unsigned int fix, const WpgRow* rows, unsigned int n, unsigned int chunkOff, unsigned int wpCount,
                         unsigned int nowMs) {
    if (n > WPG_POOL - t->poolUsed || t->used >= WPG_SLOTS / 4 * 3) return NULL;
    const unsigned int key = chunk | fix << 16;
    unsigned int i = WpgSlotHash(key);
    for (int k = 0; k < WPG_PROBE; k++, i = (i + 1) & (WPG_SLOTS - 1)) {
        WpgSlot* s = &t->slot[i];
        if (s->gen == t->gen) continue;  // WpgBuild stores only what WpgFind did not find, so no entry has this key
        s->gen = t->gen;
        s->key = key;
        s->first = t->poolUsed;
        s->n = n;
        s->chunkOff = chunkOff;
        s->wpCount = wpCount;
        s->ms = nowMs;
        for (unsigned int r = 0; r < n; r++) t->pool[t->poolUsed + r] = rows[r];
        t->poolUsed += n;
        t->used++;
        return s;
    }
    return NULL;
}

// fill: the rows of one chunk from the game. 1 = *rows / *n / *chunkOff / *wpCount hold them and may be stored, 2 = they
// hold them but must not be stored (a write went by), 0 = failed. check: the chunk's live offset word and waypoint count.
typedef int (*WpgFillFn)(void* user, unsigned int chunk, float px, float pz, const WpgRow** rows, unsigned int* n, unsigned int* chunkOff, unsigned int* wpCount);
typedef void (*WpgHeadFn)(void* user, unsigned int chunk, unsigned int* chunkOff, unsigned int* wpCount);

// The area's candidate rows into out. Returns the count, or -1: not served (a fill failed, more rows than cap, or a stored
// chunk's header no longer matches: the table is emptied and st->stale counts it). capMs = 0: no age limit.
static int WpgBuild(WpgTab* t, const WpgPlan& p, unsigned int fix, unsigned int nowMs, unsigned int capMs, WpgFillFn fill, WpgHeadFn head, void* user, WpgRow* out,
                    unsigned int cap, WpgStats* st) {
    unsigned int total = 0;
    for (int c = 0; c < p.n; c++) {
        const unsigned int chunk = p.chunk[c];
        const WpgRow* rows = NULL;
        unsigned int n = 0;
        WpgSlot* s = WpgFind(t, chunk, fix);
        WpgSlot* aged = NULL;
        if (s && capMs && nowMs - s->ms > capMs) aged = s, s = NULL;  // too old (unsigned: a wrapped clock ages it): fetched again
        if (s) {
            unsigned int off = 0, cnt = 0;
            head(user, chunk, &off, &cnt);
            if (off != s->chunkOff || cnt != s->wpCount) {  // a write the sequence did not see
                st->stale++;
                WpgReset(t);
                return -1;
            }
            rows = &t->pool[s->first];
            n = s->n;
            st->hits++;
        } else {
            unsigned int off = 0, cnt = 0;
            const int r = fill(user, chunk, p.px[c], p.pz[c], &rows, &n, &off, &cnt);
            if (!r) return -1;
            st->fills++;
            if (r == 1 && aged && aged->n == n) {  // the same room: renewed in place
                for (unsigned int k = 0; k < n; k++) t->pool[aged->first + k] = rows[k];
                aged->chunkOff = off;
                aged->wpCount = cnt;
                aged->ms = nowMs;
            } else if (r == 1 && aged) {  // another size: the table starts over with this chunk
                WpgReset(t);
                WpgStore(t, chunk, fix, rows, n, off, cnt, nowMs);
            } else if (r == 1 && !WpgStore(t, chunk, fix, rows, n, off, cnt, nowMs)) {
                st->full++;
                WpgReset(t);
                WpgStore(t, chunk, fix, rows, n, off, cnt, nowMs);  // may still not fit (a chunk larger than the pool): not stored
            }
        }
        if (n > cap - total) return -1;
        for (unsigned int r = 0; r < n; r++) out[total + r] = rows[r];
        total += n;
    }
    st->rows += total;
    return (int)total;
}

// The two call sites are chained once: what a site calls at install time becomes "the original" our hook calls, and the
// site is pointed at the hook. A second install pass would read our own hook back as the original, and the hook would
// then call itself until the thread's stack ends (2026-11-20, docs/679 §8: the install call stood twice in ts3native.cpp;
// the first gather of a route job never came back and the job kept the routing lock). So the originals are decided here,
// once, and never from a site that already calls one of our hooks.
struct WpgChain {
    unsigned int sameOrig, gatherOrig;  // what the hooks call
    int done;                           // an install pass decided them
};
// 1 = *ch holds the originals, the sites may be pointed at the hooks; 0 = refused, *ch is untouched, nothing may be written
static int WpgChainPlan(WpgChain* ch, unsigned int sameTarget, unsigned int gatherTarget, unsigned int sameHook, unsigned int gatherHook) {
    if (ch->done) return 0;
    if (!sameTarget || !gatherTarget || !sameHook || !gatherHook) return 0;
    if (sameTarget == sameHook || sameTarget == gatherHook || gatherTarget == sameHook || gatherTarget == gatherHook) return 0;
    ch->sameOrig = sameTarget;
    ch->gatherOrig = gatherTarget;
    ch->done = 1;
    return 1;
}

// the game's row against ours (base = the record area)
static bool WpgRowSame(const WpgGameRow& g, const WpgRow& r, unsigned int base) {
    unsigned int gx = 0, gz = 0, rx = 0, rz = 0;
    const unsigned char* a = (const unsigned char*)&g.x;
    const unsigned char* b = (const unsigned char*)&r.x;
    for (int i = 0; i < 4; i++) {
        gx |= (unsigned int)a[i] << (i * 8);
        gz |= (unsigned int)a[4 + i] << (i * 8);
        rx |= (unsigned int)b[i] << (i * 8);
        rz |= (unsigned int)b[4 + i] << (i * 8);
    }
    return gx == rx && gz == rz && g.wp == r.wp && g.chunk == r.chunk && g.rec == base + r.rec && g.type == r.type;
}
