// [BoatProbe] (2026-11-14, read only): answers ts3fixes' boatplace / boattrip question about one point of the routing water grid
// G (world+0x8C, docs/100 §2) and logs what the planner sees there. Included by ts3native.cpp (one translation unit).
//
// Why: 8 Isla Paradiso games, 98% of the failed townie boat plan time started on the water inside one residential lot, while
// boats one cell outside that lot planned fine. The probe line says what G holds at such a start; the answer gives ts3fixes
// the nearest boat-passable water cell that no lot covers, to plan from in shadow.
//
// No hook, no ini key, nothing written to the game: the code runs only when ts3fixes sends the message (its switches
// boatplace / boattrip, default off), on the sim thread inside Route.GetOption2, where every G writer also runs (docs/100 §3.1).
// Every game read sits under SEH; a fault answers "no".
//
// Channel: Route.GetOption2 with RouteOption2 bits 63 + 59 + 56 (optHi top byte 0x89; the game and older ts3native answer
// false), code in optHi bits 16-23, 32-bit payload in optLo:
//   1  payload = x (float bits)                    2  payload = z (float bits)
//   3  run for that point; payload bit 0 = log the probe line, bits 8-31 = a tag for the line (low bits of the boat id).
//      Answer: the point is water under a lot (cell body 1-0x7E, flag 0x01)
//   4  payload = bit index into the result word of the last run:
//      bit 0 = a target cell was found within kBpR cells, bit 1 = it is in the start's water body,
//      bits 2-9 / 10-17 = target cell centre minus the point, x / z, in half metres (signed 8 bit),
//      bits 18 / 19 = the start cell in passability map 0 / 1
//   5  forget the excluded points (answer 1)
//   6  add the point of messages 1 / 2 to the excluded points (other boats; answer 1, 0 = list full). A cell whose centre is
//      within 4 m of an excluded point is never the target of a later run (ts3fixes boattrip: no two boats on one cell)
// Target: the nearest cell (Euclidean, cell centres) with a body id, without the lot flag and open in passability map 1
// (boats, docs/100 §4); a cell of the start's body wins over a nearer cell of another body.
//
// Cell index as the game's 0x66D850: (trunc(z - WM+0xB8) >> 2) * G.width + (trunc(x - WM+0xB4) >> 2) [verified, capstone].
// Shoreline tree G+0x30 (docs/99 §2.4, p4clone.cpp PcTreeWalk): node value +0x10 waypoint key, +0x14 body byte, +0x18 foot
// cell, +0x1C boat cell (-1 = none). blockedNodeMap G+0x35C: buckets +0x360, count +0x364, node {key, count, next}.

static const DWORD kBpHi = 0x89000000;
static const DWORD kBpWorldSlot = TS3A(0x011D9128), kBpWmSlot = TS3A(0x011ECBC4);  // routing world, world manager (snap.cpp's slots)
static const int kBpR = 6;         // search radius in 4 m cells
static const int kBpShoreR = 8;    // shoreline entries within this many cells of the start are "near"
static const LONG kBpMaxLines = 200;

struct BpPick { bool found; int dx, dz; u8 body; };
struct BpNear { WORD word; u8 p0, p1; BpPick same, any; };

// Pure: cells = G's WORD cells, m0 / m1 = passability maps 0 / 1 (m1 may be NULL: then no target), idx < w * h.
// ex = nEx excluded points as x, z pairs in cell units (grid origin 0,0; a cell's centre is +0.5)
static void BpNearest(const WORD* cells, const u8* m0, const u8* m1, DWORD w, DWORD h, DWORD idx, int R, BpNear* o, const float* ex = NULL, int nEx = 0) {
    memset(o, 0, sizeof(*o));
    o->word = cells[idx];
    o->p0 = m0 ? m0[idx] : 0, o->p1 = m1 ? m1[idx] : 0;
    if (!m1) return;
    const int cx = (int)(idx % w), cz = (int)(idx / w);
    const u8 body = (u8)(o->word & 0xFF);
    int bestSame = INT_MAX, bestAny = INT_MAX;
    for (int dz = -R; dz <= R; dz++)
        for (int dx = -R; dx <= R; dx++) {
            const int x = cx + dx, z = cz + dz;
            if (x < 0 || z < 0 || x >= (int)w || z >= (int)h) continue;  // no row wrap
            const DWORD n = (DWORD)z * w + (DWORD)x;
            const WORD c = cells[n];
            const u8 b = (u8)(c & 0xFF);
            if (!b || b >= 0x7F || (c & 0x0100) || !m1[n]) continue;  // land / excluded, under a lot, closed for boats
            bool taken = false;
            for (int e = 0; e < nEx && !taken; e++) {
                const float ux = x + 0.5f - ex[e * 2], uz = z + 0.5f - ex[e * 2 + 1];
                taken = ux * ux + uz * uz < 1.0f;
            }
            if (taken) continue;
            const int d2 = dx * dx + dz * dz;
            if (d2 < bestAny) bestAny = d2, o->any = {true, dx, dz, b};
            if (b == body && d2 < bestSame) bestSame = d2, o->same = {true, dx, dz, b};
        }
}

// result word of message 4 (see the header); fx / fz = the point's offset inside its cell, 0 <= f < 4
static DWORD BpPack(const BpNear& n, float fx, float fz) {
    const u8 body = (u8)(n.word & 0xFF);
    DWORD r = (n.p0 ? 1u << 18 : 0) | (n.p1 ? 1u << 19 : 0);
    if (body && body < 0x7F && (n.word & 0x0100)) r |= 1u << 20;
    const BpPick& p = n.same.found ? n.same : n.any;
    if (!p.found) return r;
    const int ox = (int)lroundf((p.dx * 4 + 2 - fx) * 2), oz = (int)lroundf((p.dz * 4 + 2 - fz) * 2);
    return r | 1 | (n.same.found ? 2 : 0) | ((DWORD)ox & 0xFF) << 2 | ((DWORD)oz & 0xFF) << 10;
}

static float g_bpX, g_bpZ;
static const int kBpMaxEx = 16;
static float g_bpEx[kBpMaxEx * 2];  // world x, z
static int g_bpNEx;
static DWORD g_bpRes;
static LONG g_bpLines, g_bpRuns;

static inline DWORD BpD(const u8* p) { return *(const volatile DWORD*)p; }

// reference count of `cell` in blockedNodeMap, 0 = not blocked
static DWORD BpBlocked(const u8* g, DWORD cell) {
    const DWORD nb = BpD(g + 0x364);
    const DWORD* bk = (const DWORD*)(uintptr_t)BpD(g + 0x360);
    if (!nb || nb > (1u << 24) || !bk) return 0;
    const u8* nd = (const u8*)(uintptr_t)bk[cell % nb];
    for (int i = 0; nd && i < 4096; i++, nd = (const u8*)(uintptr_t)BpD(nd + 8))
        if (BpD(nd) == cell) return BpD(nd + 4) & 0xFF;
    return 0;
}

static u8 BpRun(DWORD lo) {
    g_bpRes = 0;
    InterlockedIncrement(&g_bpRuns);
    __try {
        const u8* world = *(const u8* const*)(uintptr_t)kBpWorldSlot;
        const u8* wm = *(const u8* const*)(uintptr_t)kBpWmSlot;
        if (!world || !wm) return 0;
        const u8* g = world + 0x8C;
        const DWORD w = BpD(g + 4), h = BpD(g + 8);
        const WORD* cells = (const WORD*)(uintptr_t)BpD(g + 0x1C);
        const WORD* cend = (const WORD*)(uintptr_t)BpD(g + 0x20);
        if (!*(const volatile u8*)g || !w || !h || w > 65536 || h > 65536 || !cells || cend < cells || (unsigned long long)(cend - cells) < (unsigned long long)w * h) return 0;
        if (BpD(g + 0x7A0) != w || BpD(g + 0x7A4) != h || BpD(g + 0x7A8) < 2) return 0;
        const float rx = g_bpX - *(const volatile float*)(wm + 0xB4), rz = g_bpZ - *(const volatile float*)(wm + 0xB8);
        if (!(rx >= 0) || !(rz >= 0) || rx >= w * 4.0f || rz >= h * 4.0f) return 0;
        const DWORD cx = (DWORD)rx >> 2, cz = (DWORD)rz >> 2, idx = cz * w + cx;
        const u8* mv = (const u8*)(uintptr_t)BpD(g + 0x7B0);
        if (!mv) return 0;
        const u8* m[2];
        for (int i = 0; i < 2; i++) {
            const u8* b = (const u8*)(uintptr_t)BpD(mv + i * 16);
            const u8* e = (const u8*)(uintptr_t)BpD(mv + i * 16 + 4);
            m[i] = b && e >= b && (unsigned long long)(e - b) >= (unsigned long long)w * h ? b : NULL;
        }
        BpNear n;
        float ex[kBpMaxEx * 2];
        const int nEx = g_bpNEx < kBpMaxEx ? g_bpNEx : kBpMaxEx;
        for (int e = 0; e < nEx; e++) {
            ex[e * 2] = (g_bpEx[e * 2] - *(const volatile float*)(wm + 0xB4)) / 4.0f;
            ex[e * 2 + 1] = (g_bpEx[e * 2 + 1] - *(const volatile float*)(wm + 0xB8)) / 4.0f;
        }
        BpNearest(cells, m[0], m[1], w, h, idx, kBpR, &n, ex, nEx);
        g_bpRes = BpPack(n, rx - cx * 4.0f, rz - cz * 4.0f);
        if ((lo & 1) && InterlockedIncrement(&g_bpLines) <= kBpMaxLines) {
            const u8 body = (u8)(n.word & 0xFF);
            // shoreline entries with a slot near the start
            int nNear = 0, nBoat = 0, nBlocked = 0, bodyBoat = 0, bestD2 = INT_MAX;
            DWORD bestBlocked = 0, total = BpD(g + 0x30 + 0x10), walked = 0;
            u8 bestBody = 0;
            const u8* anc = g + 0x30;
            if (total && total <= (1u << 16))
                for (const u8* x = (const u8*)(uintptr_t)BpD(anc + 4); x && x != anc && walked < total; walked++) {
                    const u8 eb = *(const volatile u8*)(x + 0x14);
                    const DWORD foot = BpD(x + 0x18), boat = BpD(x + 0x1C);
                    if (boat != 0xFFFFFFFF && eb == body) bodyBoat++;
                    bool isNear = false;
                    for (int s = 0; s < 2; s++) {
                        const DWORD c = s ? boat : foot;
                        if (c == 0xFFFFFFFF || c >= w * h) continue;
                        const int dx = (int)(c % w) - (int)cx, dz = (int)(c / w) - (int)cz;
                        if (abs(dx) > kBpShoreR || abs(dz) > kBpShoreR) continue;
                        isNear = true;
                        if (!s) continue;
                        nBoat++;
                        const DWORD bl = BpBlocked(g, c);
                        if (bl) nBlocked++;
                        if (dx * dx + dz * dz < bestD2) bestD2 = dx * dx + dz * dz, bestBody = eb, bestBlocked = bl;
                    }
                    if (isNear) nNear++;
                    // in-order successor (right +0, left +4, parent +8)
                    const u8* r = (const u8*)(uintptr_t)BpD(x);
                    if (r) { x = r; for (const u8* l; (l = (const u8*)(uintptr_t)BpD(x + 4)) != NULL;) x = l; }
                    else { const u8* p = (const u8*)(uintptr_t)BpD(x + 8); while (p && p != anc && x == (const u8*)(uintptr_t)BpD(p)) x = p, p = (const u8*)(uintptr_t)BpD(p + 8); x = p; }
                }
            char same[48] = "none", any[48] = "none";
            if (n.same.found) _snprintf_s(same, sizeof(same), _TRUNCATE, "%.1f m (%+d,%+d)", 4.0 * sqrt((double)(n.same.dx * n.same.dx + n.same.dz * n.same.dz)), n.same.dx, n.same.dz);
            if (n.any.found) _snprintf_s(any, sizeof(any), _TRUNCATE, "%.1f m (%+d,%+d) body %u (%lu cells)", 4.0 * sqrt((double)(n.any.dx * n.any.dx + n.any.dz * n.any.dz)), n.any.dx, n.any.dz,
                                         n.any.body, BpD(g + 0x39C + 8 * n.any.body + 4));
            Log("boat probe: boat %06lX start %.1f,%.1f cell %lu (%lu,%lu) word 0x%04X body %u (%lu cells) map0 %u map1 %u blocked %lu; "
                "nearest boat water outside a lot within %d m: same body %s, any body %s; shoreline entries within %d m: %d (boat slot %d, "
                "blocked %d), nearest boat slot %.1f m body %u blocked %lu; tree %lu entries (%lu walked), start body boat slots %d; grid %lux%lu, %lu bodies",
                lo >> 8, g_bpX, g_bpZ, idx, cx, cz, n.word, body, body && body < 0x80 ? BpD(g + 0x39C + 8 * body + 4) : 0, n.p0, n.p1, BpBlocked(g, idx),
                kBpR * 4, same, any, kBpShoreR * 4, nNear, nBoat, nBlocked, bestD2 == INT_MAX ? -1.0 : 4.0 * sqrt((double)bestD2), bestBody, bestBlocked,
                total, walked, bodyBoat, w, h, BpD(g + 0x14));
        }
        return (u8)(g_bpRes >> 20 & 1);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        g_bpRes = 0;
        return 0;
    }
}

static u8 BpMsg(DWORD lo, DWORD hi) {
    switch (hi >> 16 & 0xFF) {
        case 1: memcpy(&g_bpX, &lo, 4); return 1;
        case 2: memcpy(&g_bpZ, &lo, 4); return 1;
        case 3: return BpRun(lo);
        case 4: return (u8)(g_bpRes >> (lo & 31) & 1);
        case 5: g_bpNEx = 0; return 1;
        case 6:
            if (g_bpNEx >= kBpMaxEx) return 0;
            g_bpEx[g_bpNEx * 2] = g_bpX, g_bpEx[g_bpNEx * 2 + 1] = g_bpZ, g_bpNEx++;
            return 1;
    }
    return 0;
}
