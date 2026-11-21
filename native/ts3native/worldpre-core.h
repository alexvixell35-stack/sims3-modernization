// docs/676: the pure parts of [WorldPrecompute] (worldpre.cpp): a scan-table slot and its refresh against the live grids,
// and the repeat table of the probe. No Windows, no game: waterscan-host-test.cpp and the selftest run it as is.
// Included after waterjps-core.h (WjCtx, WsBuild, WsUpdateCell); the includer provides memcpy and memcmp.
#ifndef WORLDPRE_CORE_H
#define WORLDPRE_CORE_H

// One scan table: the two grids it was built from (their data pointers and size), our copy of both (c1, c2: what the table
// matches) and the table. The memory is the caller's: cells bytes for each copy, 4 * cells entries for the table.
struct WsSlot {
    const u8* g1;
    const u8* g2;
    DWORD w, h;
    u8* c1;
    u8* c2;
    unsigned short* tab;
    bool built;
};
// WsRefresh: nothing changed / changed cells updated / built: the slot had no table for these grids / built: more than
// maxInc cells changed
enum { WS_CLEAN, WS_INC, WS_FULL, WS_FULLOVER };
// Up to this many changed cells are updated in place. A cell costs its three rows' ends and a share of three columns
// (2 * h entries each, shared between neighbours); a build is 4 * w * h entries. 128 scattered cells cost under half a build.
enum { WS_INCMAX = 128, WS_COLWORDS = 0x8000 / 32 };

static inline WjCtx WsCtx0(const u8* g1, const u8* g2, DWORD w, DWORD h) {
    WjCtx c = {g1, g2, w, h, 0, false, 0, {-(int)w, 1 - (int)w, 1, (int)w + 1, (int)w, (int)w - 1, -1, -(int)w - 1}, {0}, 0, 0, 0, 0, 0};
    return c;
}

// Makes the slot's table match the live grids s->g1 / s->g2 (read here, byte by byte against the copies: this compare is
// the table's whole invalidation, no writer of the grids is hooked). Up to maxInc changed cells are updated in place, more
// rebuild the table. *changed = cells updated in place.
static int WsRefresh(WsSlot* s, DWORD maxInc, DWORD* changed) {
    const DWORD cells = s->w * s->h;
    // The table is computed from our copies, never from the live grids: another thread may write a live cell between the
    // copy and the table's read of it. A table built from a byte the copy does not hold is repaired only while that byte
    // still differs at a later compare; a cell written and written back in between stays wrong for good.
    const WjCtx c0 = WsCtx0(s->c1, s->c2, s->w, s->h);
    *changed = 0;
    if (maxInc > WS_INCMAX) maxInc = WS_INCMAX;
    DWORD list[WS_INCMAX] = {};
    DWORD n = 0;
    bool full = !s->built;
    for (DWORD at = 0; at < cells && !full; at += 256) {
        const DWORD len = cells - at < 256 ? cells - at : 256;
        if (!memcmp(s->c1 + at, s->g1 + at, len) && !memcmp(s->c2 + at, s->g2 + at, len)) continue;
        for (DWORD i = at; i < at + len; i++) {
            if (s->c1[i] == s->g1[i] && s->c2[i] == s->g2[i]) continue;
            if (n == maxInc) { full = true; break; }
            list[n++] = i;
        }
    }
    if (full || s->w > WS_COLWORDS * 32) {
        const int why = s->built ? WS_FULLOVER : WS_FULL;
        memcpy(s->c1, s->g1, cells);
        memcpy(s->c2, s->g2, cells);
        WsBuild(c0, s->tab);
        s->built = true;
        return why;
    }
    if (!n) return WS_CLEAN;
    for (DWORD i = 0; i < n; i++) s->c1[list[i]] = s->g1[list[i]], s->c2[list[i]] = s->g2[list[i]];
    DWORD cols[WS_COLWORDS] = {};
    WsUpdateCells(c0, s->tab, list, n, cols);  // after every byte is in place
    *changed = n;
    return WS_INC;
}

// ---- the probe's repeat table: has this query been asked before, and has the world changed since? ----
struct WxRepE {
    unsigned long long key;  // 0 = empty
    DWORD gen;               // the change counter at the key's last sighting
    DWORD n;                 // sightings
};
enum { WX_REPN = 4096, WX_REPPROBE = 16 };
struct WxRep {
    WxRepE e[WX_REPN];
    DWORD used, full;  // keys kept / sightings not kept (every probe slot taken by another key)
};
static inline unsigned long long WxMix(unsigned long long h, unsigned long long v) {
    h = h * 0x9E3779B97F4A7C15ull + v;
    h = (h ^ (h >> 29)) * 0xBF58476D1CE4E5B9ull;
    return h ^ (h >> 32);
}
// 0 = first sighting, 1 = seen before, 2 = seen before and gen has not moved since its last sighting; -1 = no room.
static inline int WxRepNote(WxRep* t, unsigned long long key, DWORD gen) {
    if (!key) key = 1;
    DWORD at = (DWORD)(key >> 20) & (WX_REPN - 1);
    for (int p = 0; p < WX_REPPROBE; p++, at = (at + 1) & (WX_REPN - 1)) {
        WxRepE& e = t->e[at];
        if (!e.key) {
            e.key = key, e.gen = gen, e.n = 1;
            t->used++;
            return 0;
        }
        if (e.key != key) continue;
        const int r = e.gen == gen ? 2 : 1;
        e.gen = gen, e.n++;
        return r;
    }
    t->full++;
    return -1;
}

// The land component ("colour") of a waypoint, as the game's own flood stored it (docs/99 §1.4, §1.8; read the way
// 0x65DF90 reads it). r = the waypoint graph object R: +0x00 / +0x04 chunks wide / high, +0x14 components built,
// +0x18 / +0x1C a vector with each chunk's first index into the colours, +0x28 / +0x2C the colours (one signed byte per
// waypoint). ref = chunk | waypoint << 16. -3 = not known (no R, not built, a virtual water node, out of range).
static inline int WxLandColour(const u8* r, DWORD ref) {
    const DWORD chunk = ref & 0xFFFF, wp = ref >> 16;
    if (!r || !r[0x14] || chunk == 0xFFFF) return -3;
    const DWORD* const first = *(const DWORD* const*)(r + 0x18);
    const DWORD* const firstEnd = *(const DWORD* const*)(r + 0x1C);
    const signed char* const col = *(const signed char* const*)(r + 0x28);
    const signed char* const colEnd = *(const signed char* const*)(r + 0x2C);
    if (!first || !col || chunk >= (DWORD)(firstEnd - first)) return -3;
    const DWORD at = first[chunk] + wp;
    if (at >= (DWORD)(colEnd - col)) return -3;
    return col[at];
}

#endif
