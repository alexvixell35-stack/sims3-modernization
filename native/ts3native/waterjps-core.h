// The pure part of the water-grid jump (docs/15 phase 9, moved here unchanged from waterjps.cpp) and its straight-scan
// table (docs/676). No Windows, no game: waterjps.cpp includes it; waterscan-host-test.cpp runs it on Linux as is.
// The includer provides DWORD (32-bit unsigned) and u8.
#ifndef WATERJPS_CORE_H
#define WATERJPS_CORE_H

typedef u8(__fastcall* WjCbFn)(void* self, void* edx, const DWORD* xy);  // thiscall, ret 4

static const int kWjDx[8] = {0, 1, 1, 1, 0, -1, -1, -1};
static const int kWjDy[8] = {-1, -1, 0, 1, 1, 1, 0, -1};

struct WjCtx {
    const u8* g1;
    const u8* g2;
    DWORD w, h;
    void* cb;
    bool nearCb;  // cb is the game's 0x66DC50: inlined, start x,y and goal x,y copied to o[4..7]
    DWORD cells;
    int step[8];  // index delta per direction (0x61F750)
    int o[8];
    // docs/676 scan table (NULL = every straight scan walks its cells): 4 * w * h entries, see WsEntry below
    const unsigned short* tab;
    DWORD goalX, goalY;        // the goal's cell (set with tab; goal >= w * h: no goal on any ray)
    DWORD tabScans, tabSlow;   // straight scans answered from the table / walked because the entry was "unknown"
};

static inline bool WjCallback(const WjCtx& c, DWORD x, DWORD y) {
    if (!c.cb) return false;
    if (c.nearCb) {
        // 0x66DC50: |x - sx| <= 3 && |y - sy| <= 3 for the start (+0x10) or the goal (+0x18); cdq/xor/sub abs, signed compare
        const int* o = c.o;
        int a = (int)(x - (DWORD)o[4]), b = (int)(y - (DWORD)o[5]);
        if (!(3 < (int)((DWORD)(a ^ (a >> 31)) - (DWORD)(a >> 31))) && !(3 < (int)((DWORD)(b ^ (b >> 31)) - (DWORD)(b >> 31)))) return true;
        a = (int)(x - (DWORD)o[6]), b = (int)(y - (DWORD)o[7]);
        return !(3 < (int)((DWORD)(a ^ (a >> 31)) - (DWORD)(a >> 31))) && !(3 < (int)((DWORD)(b ^ (b >> 31)) - (DWORD)(b >> 31)));
    }
    const DWORD xy[2] = {x, y};
    return ((WjCbFn)(*(void***)c.cb)[1])(c.cb, NULL, xy) != 0;
}

// 0x61F9A0 at neighbour k of (x,y): water in g1 and not marked in g2
static inline bool WjOpen(const WjCtx& c, DWORD x, DWORD y, int k) {
    const DWORD nx = x + kWjDx[k & 7], ny = y + kWjDy[k & 7];
    if (nx >= c.w || ny >= c.h) return false;
    const DWORD i = ny * c.w + nx;
    return c.g1[i] && !c.g2[i];
}

// g2 at neighbour k of (x,y), 0 outside
static inline bool WjMark(const WjCtx& c, DWORD x, DWORD y, int k) {
    const DWORD nx = x + kWjDx[k & 7], ny = y + kWjDy[k & 7];
    return nx < c.w && ny < c.h && c.g2[ny * c.w + nx];
}

// 0x61FA10 != 0 for an in-bounds cell n = (x,y) entered in direction d (0..7)
static inline bool WjForced(const WjCtx& c, DWORD n, DWORD x, DWORD y, int d) {
    if (WjCallback(c, x, y)) return true;
    if (x - 1 < c.w - 2 && y - 1 < c.h - 2) {
        // not on the border: all eight neighbours are inside, so plain index offsets and no bounds checks (same tests as below)
        const u8* a = c.g1 + n;
        const u8* b = c.g2 + n;
        const int* s = c.step;
        const int o = (d & 1) + 1;
#define WJ_OPEN(k) ((a[s[(k) & 7]] != 0) & (b[s[(k) & 7]] == 0))
        const int here = b[0] != 0;
        return (WJ_OPEN(d - o) & !WJ_OPEN(d - o - 1)) | (WJ_OPEN(d + o) & !WJ_OPEN(d + o + 1)) | ((b[s[d]] != 0) != here) |
               ((b[s[(d - 1) & 7]] != 0) != here) | ((b[s[(d + 1) & 7]] != 0) != here);
#undef WJ_OPEN
    }
    if (d & 1) {
        if (WjOpen(c, x, y, d - 2) && !WjOpen(c, x, y, d - 3)) return true;
        if (WjOpen(c, x, y, d + 2) && !WjOpen(c, x, y, d + 3)) return true;
    } else {
        if (WjOpen(c, x, y, d - 1) && !WjOpen(c, x, y, d - 2)) return true;
        if (WjOpen(c, x, y, d + 1) && !WjOpen(c, x, y, d + 2)) return true;
    }
    const bool here = c.g2[y * c.w + x] != 0;
    return WjMark(c, x, y, d) != here || WjMark(c, x, y, d - 1) != here || WjMark(c, x, y, d + 1) != here;
}

// One step from index n at (x,y) in direction d; false when the new cell is outside the grid or not water.
// Equals the original's index step + n / w, n % w: w * (h + 2) < 2^32 is checked by the caller.
static inline bool WjStep(WjCtx& c, DWORD& n, DWORD& x, DWORD& y, int d) {
    n += (DWORD)c.step[d];
    x += kWjDx[d];
    y += kWjDy[d];
    if (x == c.w) x = 0, y++;
    else if (x == 0xFFFFFFFF) x = c.w - 1, y--;
    c.cells++;
    return y < c.h && c.g1[n];
}

// Straight scan (even d) from n: the jump point or 0xFFFFFFFF. A straight jump never branches.
static DWORD WjScan(WjCtx& c, DWORD goal, DWORD n, DWORD x, DWORD y, int d) {
    for (;;) {
        if (!WjStep(c, n, x, y, d)) return 0xFFFFFFFF;
        if (n == goal || WjForced(c, n, x, y, d) || c.g2[n]) return n;
    }
}

// ---- docs/676: the straight-scan table ----------------------------------------------------------------------------------
// A straight scan (WjScan) from cell n in an even direction d stops at the first cell that is not water or outside the grid
// (no jump point), or that is the goal, lies in the callback's start / goal box, is forced by its neighbours, or is marked
// in g2 (a jump point). Everything but the goal and the boxes depends on the two grids only. The table keeps, per cell and
// per straight direction, how far that static scan runs and how it ends:
//   entry 0            unknown (the run is longer than WS_MAXRUN cells): walk it
//   entry k            cells 1..k-1 are water and no static stop, cell k is not water or outside: no jump point
//   entry 0x8000 | k   the same, and cell k is a static jump point
// A query then only has to ask whether the goal or a box cell comes before cell k on the ray, which is arithmetic. The ray
// is the game's: an index walk (n + i * step), so an east or west ray runs on into the next row exactly as WjStep does.
// The answer equals WjScan's for every input the table path accepts (WjTabOk); the host test compares them on random grids.
enum { WS_HIT = 0x8000, WS_MAXRUN = 0x7FFE };
static inline DWORD WsDirOf(int d) { return (DWORD)d >> 1; }  // 0 north (-w), 1 east (+1), 2 south (+w), 3 west (-1)

// The entry of cell n = (x, y) for direction d from the entry of the next cell on the ray (t = this direction's part of
// the table). The next cell's coordinates follow WjStep (a step off a row's end lands on the neighbouring row).
static inline unsigned short WsEntryAt(const WjCtx& c0, const unsigned short* t, DWORD n, DWORD x, DWORD y, int d) {
    const DWORD cells = c0.w * c0.h, m = n + (DWORD)c0.step[d];
    if (m >= cells || !c0.g1[m]) return 1;  // WjStep fails on the first step
    DWORD mx = x + (DWORD)kWjDx[d], my = y + (DWORD)kWjDy[d];
    if (mx == c0.w) mx = 0, my++;
    else if (mx == 0xFFFFFFFF) mx = c0.w - 1, my--;
    if (WjForced(c0, m, mx, my, d) || c0.g2[m]) return (unsigned short)(WS_HIT | 1);
    const unsigned short e = t[m];
    if (!e || (e & 0x7FFFu) >= (DWORD)WS_MAXRUN) return 0;
    return (unsigned short)(e + 1);
}
static inline unsigned short WsEntry(const WjCtx& c0, const unsigned short* t, DWORD n, int d) { return WsEntryAt(c0, t, n, n % c0.w, n / c0.w, d); }

// c0: a context over the two grids with no callback and no table. Fills all four directions (4 * w * h entries).
static void WsBuild(const WjCtx& c0, unsigned short* tab) {
    const DWORD cells = c0.w * c0.h;
    unsigned short* const tn = tab;
    unsigned short* const te = tab + cells;
    unsigned short* const ts = tab + 2 * cells;
    unsigned short* const tw = tab + 3 * cells;
    DWORD n = 0;
    for (DWORD y = 0; y < c0.h; y++)  // north and west: the next cell's index is below n
        for (DWORD x = 0; x < c0.w; x++, n++) tn[n] = WsEntryAt(c0, tn, n, x, y, 0), tw[n] = WsEntryAt(c0, tw, n, x, y, 6);
    for (DWORD y = c0.h; y-- > 0;)    // east and south: above n
        for (DWORD x = c0.w; x-- > 0;) n--, te[n] = WsEntryAt(c0, te, n, x, y, 2), ts[n] = WsEntryAt(c0, ts, n, x, y, 4);
}

// After g1 or g2 changed at cell `at` (the grids already hold the new bytes): bring the table back to what WsBuild gives.
// A cell's stop tests read its eight neighbours, so the entries whose next cell is in the 3 x 3 block around `at` are
// recomputed, then the entries behind them on the same ray for as long as they change. Several changed cells: call once
// per cell, in any order, after all bytes are in place.
// Rows of one changed cell: the east and west entries of its three rows, then along the ray while they change.
static inline void WsUpdateCellRows(const WjCtx& c0, unsigned short* tab, DWORD at) {
    const DWORD w = c0.w, h = c0.h, cells = w * h, cy = at / w;
    const DWORD y0 = cy ? cy - 1 : 0, y1 = cy + 1 < h ? cy + 1 : h - 1;
    const DWORD lo = y0 * w, hi = y1 * w + w - 1;  // the three rows, as an index range
    unsigned short* const te = tab + cells;
    unsigned short* const tw = tab + 3 * cells;
    // east: next cell n + 1. Entries hi .. lo - 1 always, then down while an entry changes
    for (DWORD n = hi + 1; n-- > 0;) {
        const unsigned short e = WsEntry(c0, te, n, 2);
        if (n + 1 < lo && e == te[n]) break;
        te[n] = e;
    }
    // west: next cell n - 1. Entries lo .. hi + 1 always, then up while an entry changes
    for (DWORD n = lo; n < cells; n++) {
        const unsigned short e = WsEntry(c0, tw, n, 6);
        if (n > hi + 1 && e == tw[n]) break;
        tw[n] = e;
    }
}
// North and south never leave their column: one column, whole.
static inline void WsUpdateColumn(const WjCtx& c0, unsigned short* tab, DWORD x) {
    const DWORD w = c0.w, h = c0.h, cells = w * h;
    unsigned short* const tn = tab;
    unsigned short* const ts = tab + 2 * cells;
    for (DWORD y = 0; y < h; y++) tn[y * w + x] = WsEntry(c0, tn, y * w + x, 0);
    for (DWORD y = h; y-- > 0;) ts[y * w + x] = WsEntry(c0, ts, y * w + x, 4);
}
static inline void WsUpdateCell(const WjCtx& c0, unsigned short* tab, DWORD at) {  // tests; WsRefresh uses the batch
    const DWORD w = c0.w, cx = at % w;
    const DWORD x0 = cx ? cx - 1 : 0, x1 = cx + 1 < w ? cx + 1 : w - 1;
    WsUpdateCellRows(c0, tab, at);
    for (DWORD x = x0; x <= x1; x++) WsUpdateColumn(c0, tab, x);
}
// n changed cells at once (all bytes in place): the rows per cell, every touched column once. A whole-column pass reads
// only the grid and the column's own entries in ray order, so one pass gives what WsUpdateCell's last pass over that
// column gives. A boat's outline is a cluster of cells that share columns; this is what keeps a refresh cheap.
// colBits: caller's scratch of (w + 31) / 32 DWORDs.
static inline void WsUpdateCells(const WjCtx& c0, unsigned short* tab, const DWORD* list, DWORD n, DWORD* colBits) {
    const DWORD w = c0.w;
    for (DWORD i = 0; i < (w + 31) / 32; i++) colBits[i] = 0;
    for (DWORD i = 0; i < n; i++) {
        const DWORD cx = list[i] % w;
        const DWORD x0 = cx ? cx - 1 : 0, x1 = cx + 1 < w ? cx + 1 : w - 1;
        WsUpdateCellRows(c0, tab, list[i]);
        for (DWORD x = x0; x <= x1; x++) colBits[x >> 5] |= 1u << (x & 31);
    }
    for (DWORD x = 0; x < w; x++)
        if (colBits[x >> 5] & (1u << (x & 31))) WsUpdateColumn(c0, tab, x);
}

// The table path reproduces the inlined callback 0x66DC50 only for box centres where its 32-bit compare cannot wrap.
static inline bool WsBoxesSane(const int* o) {
    for (int i = 4; i < 8; i++)
        if (o[i] < 0 || o[i] > 0x3FFFFFFF) return false;
    return true;
}
// May this call use a table over these grids? No callback, or the game's own one (inlined) with sane boxes; a grid the
// index arithmetic below cannot overflow on.
static inline bool WjTabOk(const WjCtx& c) {
    return (!c.cb || (c.nearCb && WsBoxesSane(c.o))) && c.w >= 2 && c.h >= 1 && c.w <= 0x8000 && c.h <= 0x8000;
}

// First i in [1, last] whose ray cell lies in the box around (bx, by) (half width 3), or 0. North / south ray at column x.
static inline DWORD WsBoxOnColumn(int bx, int by, int x, int y, bool south, DWORD last) {
    const int dx = x - bx;
    if (dx > 3 || dx < -3) return 0;
    if (south) {
        const int r = y + 1 > by - 3 ? y + 1 : by - 3;  // first row at or below y + 1 in the box
        return r <= by + 3 && (DWORD)(r - y) <= last ? (DWORD)(r - y) : 0;
    }
    const int r = y - 1 < by + 3 ? y - 1 : by + 3;      // first row at or above y - 1 in the box
    return r >= by - 3 && r >= 0 && (DWORD)(y - r) <= last ? (DWORD)(y - r) : 0;
}
// East / west ray from index n: the box is up to seven index runs [row * w + c0, row * w + c1].
static inline DWORD WsBoxOnRow(const WjCtx& c, int bx, int by, DWORD n, int y, bool east, DWORD last) {
    const int w = (int)c.w, h = (int)c.h;
    const int c0 = bx - 3 > 0 ? bx - 3 : 0, c1 = bx + 3 < w - 1 ? bx + 3 : w - 1;
    if (c0 > c1) return 0;
    const int r0 = by - 3 > 0 ? by - 3 : 0, r1 = by + 3 < h - 1 ? by + 3 : h - 1;
    if (east) {
        for (int r = r0 > y ? r0 : y; r <= r1; r++) {
            const DWORD a = (DWORD)r * c.w + (DWORD)c0, b = (DWORD)r * c.w + (DWORD)c1;
            if (b <= n) continue;
            const DWORD i = (a > n ? a : n + 1) - n;
            return i <= last ? i : 0;  // rows ascend, so the first run that reaches past n holds the nearest cell
        }
        return 0;
    }
    for (int r = r1 < y ? r1 : y; r >= r0; r--) {
        const DWORD a = (DWORD)r * c.w + (DWORD)c0, b = (DWORD)r * c.w + (DWORD)c1;
        if (a >= n) continue;
        const DWORD i = n - (b < n ? b : n - 1);
        return i <= last ? i : 0;
    }
    return 0;
}

// WjScan from the table. c.tab is set, and WjTabOk(c) held when it was set.
static DWORD WjScanTab(WjCtx& c, DWORD goal, DWORD n, DWORD x, DWORD y, int d) {
    const DWORD cells = c.w * c.h;
    const unsigned short e = c.tab[WsDirOf(d) * cells + n];
    if (!e) {
        c.tabSlow++;
        return WjScan(c, goal, n, x, y, d);
    }
    c.tabScans++;
    const DWORD k = e & 0x7FFFu, last = k - 1;  // cells 1..last are water without a static stop
    DWORD first = 0;                            // nearest of them that is the goal or in a box, 0 = none
    if (last) {
        const bool rowRay = d == 2 || d == 6, fwd = d == 2 || d == 4;
        if (goal < cells) {
            DWORD i = 0;
            if (rowRay) i = fwd ? (goal > n ? goal - n : 0) : (goal < n ? n - goal : 0);
            else if (c.goalX == x) i = fwd ? (c.goalY > y ? c.goalY - y : 0) : (c.goalY < y ? y - c.goalY : 0);
            if (i && i <= last) first = i;
        }
        if (c.cb) {
            for (int b = 4; b < 8; b += 2) {
                const DWORD i = rowRay ? WsBoxOnRow(c, c.o[b], c.o[b + 1], n, (int)y, fwd, last) : WsBoxOnColumn(c.o[b], c.o[b + 1], (int)x, (int)y, fwd, last);
                if (i && (!first || i < first)) first = i;
            }
        }
    }
    if (first) {
        c.cells += first;
        return n + first * (DWORD)c.step[d];
    }
    c.cells += k;
    return (e & WS_HIT) ? n + k * (DWORD)c.step[d] : 0xFFFFFFFF;
}

static inline DWORD WjScanAny(WjCtx& c, DWORD goal, DWORD n, DWORD x, DWORD y, int d) {
    return c.tab ? WjScanTab(c, goal, n, x, y, d) : WjScan(c, goal, n, x, y, d);
}

static DWORD WaterJump(WjCtx& c, DWORD goal, DWORD cell, int d, bool flag) {
    DWORD n = cell, x = cell % c.w, y = cell / c.w;
    if (flag) {
        // start cell on land: one step out, water or not, while the start cell itself is not water
        WjStep(c, n, x, y, d);
        return y < c.h && !c.g1[cell] ? n : 0xFFFFFFFF;
    }
    if (!(d & 1)) return WjScanAny(c, goal, n, x, y, d);
    for (;;) {
        if (!WjStep(c, n, x, y, d)) return 0xFFFFFFFF;
        if (n == goal || WjForced(c, n, x, y, d) || c.g2[n]) return n;
        if ((int)WjScanAny(c, goal, n, x, y, (d - 1) & 7) >= 0 || (int)WjScanAny(c, goal, n, x, y, (d + 1) & 7) >= 0) return n;
    }
}

#endif
