// [RouteEarlyOut] (docs/142, counters v78 + v86): no-path pre-check at the world A* WorldAStar_Search 0x676260.
// Included by ts3native.cpp. Mode 1 never changes a result: every A* call still runs; the probe only asks, before the call,
// whether the search can be proven to fail, and checks the answer against the real result afterwards (eoWrong, expected 0).
// Mode 2 (docs/142 §9, v86) acts on the car and memo proofs: it writes what the game's failed search would leave (the
// context fields the A* sets, *best, the closed bits) and returns 0 without the flood; colour proofs stay observe only.
//
// The A* (thiscall on the PathContainer W+0x18, 7 stack args, `ret 1Ch`, result in EAX: non-zero = path) expands every
// node it can reach before it reports no path (docs/11 Q1, docs/99 §2.7). Three proofs that the goal is not reachable:
//   car    mode 2 (car only) admits only edge byte 3 and has no other walls that could open an edge: the goal must lie in
//          the start's byte-3 component of R (our own union-find over R's records, built once per R) [docs/99 §10.1]
//   colour modes 1-3 without water/boat context flags (0x80/0x100): the game's own flood colours (R+0x14/+0x18/+0x28,
//          docs/99 §1.8) differ; the planner checks this only for goals in a lot (0x65F781), not for open-ground goals
//   memo   an earlier unrestricted search of the same request with the same start and the same wall context failed, and
//          the goal is not in its closed set (the closed bits after a failed search = every node reachable from the start)
// Per request (one Route_PlanLocked, CallPlan below) it also measures how much of the plan is world A* at all.

typedef DWORD(__fastcall* EoAsFn)(u8* c, void* e, const u8* R, const u8* G, const DWORD* s, const DWORD* g, DWORD m, u8* o, DWORD* b);
static EoAsFn g_eoAsOrig;
static const DWORD WORLD_ASTAR = TS3A(0x00676260);
static const u8 kWorldAStarPrologue[] = {0x83, 0xEC, 0x3C, 0x53, 0x8B, 0x5C, 0x24, 0x58};  // sub esp,3Ch; push ebx; mov ebx,[esp+58h]
static LONG g_eoLogUs = 200000;
static DWORD g_eoMinute, g_eoLines, g_eoDropped;

// --- Mode 2: what the A* writes before its loop (0x676260..0x67642A, docs/142 §9.1) and the functions it calls there ---
// cost functions: cdecl (R, G, &from, &to, edge, edge byte, opts, parent node), float in ST0; the heuristic passes 0, 0, null
typedef double(__cdecl* EoCostFn)(const u8* R, const u8* G, const DWORD* from, const DWORD* to, DWORD edge, DWORD eb, u8* o, void* parent);
// circle prepass: stdcall (R, opts, &start, &goal), sets the centre and opts+0xA0 = r^2, returns AL != 0 to run the circle pass
typedef char(__stdcall* EoCircleFn)(const u8* R, u8* o, const DWORD* s, const DWORD* g);
static DWORD g_eoCostFn[5] = {kCostFns[0], kCostFns[1], kCostFns[2], kCostFns[3], kCostFns[4]};  // modes 1-4, default (selftest: fakes)
static EoCircleFn g_eoCircleFn = (EoCircleFn)(uintptr_t)TS3A(0x00673600);
static const float* g_eoRecipK = (const float*)(uintptr_t)TS3A(0x0107A538);  // numerator of the 1/x slots (0x6763B8)
static const DWORD* g_eoRetryTrespass = (const DWORD*)(uintptr_t)TS3A(0x011587D0);  // FLT_MAX into trespass with flag 0x40 (0x676411)
static LONG g_eoShadowFirst = 32, g_eoShadowEvery = 16, g_eoMemoMinUs = 1000;
static volatile LONG g_eoActN;  // proofs that reached the act path (shadow schedule)

// wall context of one search: everything the edge costs read except the goal waypoint and the circle (docs/142 §3.3)
enum { EO_KEY = 5 + 25 + 2 + 3 };
struct EoReq {
    LONG active, depth;
    LARGE_INTEGER t0;
    LONG as, asFail, asUs, prov, provUs, why[3], wrong, acted, actWhy[2], savedUs;
    bool memo;
    DWORD key[EO_KEY];
    DWORD memoPrint;  // Mode 2: wall print at the memo's search (docs/142 §9.3)
    u8* bits;     // closed bits of the failed search (per thread, kept across requests)
    DWORD bitsCap, bitsLen;
    DWORD* bfs;   // car BFS queue (per thread, kept)
    DWORD bfsCap;
    u8* shBits;   // shadow: our closed bits (per thread, kept)
    DWORD shCap;
};
static __declspec(thread) EoReq t_eo;

static void EoKey(DWORD* k, const u8* c, const u8* R, const u8* G, const DWORD* s, DWORD m, const u8* o) {
    k[0] = (DWORD)(uintptr_t)c, k[1] = (DWORD)(uintptr_t)R, k[2] = (DWORD)(uintptr_t)G, k[3] = m, k[4] = *s;
    memcpy(k + 5, o, 25 * 4);           // +0x00..+0x63: flags, hubs, start/goal lot points, edge-group sets
    memcpy(k + 30, o + 0x88, 2 * 4);    // +0x88 W, +0x8C job block (polygon set)
    memcpy(k + 32, o + 0xA8, 3 * 4);    // +0xA8 start point (+0xB4 goal ref and +0xB8 goal point stay out)
}

// --- car-only components of R (union-find over byte-3 edges) ---
struct EoCar { const u8* rec; DWORD size, w, h, n; bool ok; DWORD* base; int* lab; };
static EoCar g_eoCar;
static CRITICAL_SECTION g_eoCs;

static int EoFind(int* p, int x) {
    while (p[x] != x) { p[x] = p[p[x]]; x = p[x]; }
    return x;
}
// R: +0 w (the A* reads it as u16), +4 h, +8 chunk offset table, +0xC records, +0x10 size. Chunk: u16 count, u16 recOff[count]
// from the chunk start; record: n, flags (0x40 = edge bytes), surface, bx, bz, u16 ref[n], u8 edge[n]; ref = wp | (dx+3)<<10 |
// (dz+3)<<13, target chunk c + dz*w + dx (docs/99 §1.2, §1.3). A reference the A* would follow outside the table makes the
// labels unusable (ok = false): the proof must cover every edge the search can take.
static bool EoCarBuild(EoCar& e, const u8* R) {
    const DWORD w = *(const unsigned short*)R, h = *(const DWORD*)(R + 4), chunks = w * h;
    const DWORD* table = *(const DWORD* const*)(R + 8);
    const u8* rec = *(const u8* const*)(R + 0xC);
    const DWORD size = *(const DWORD*)(R + 0x10);
    HeapFree(GetProcessHeap(), 0, e.base);
    HeapFree(GetProcessHeap(), 0, e.lab);
    e = {rec, size, w, h, 0, false, NULL, NULL};
    if (!rec || !table || !chunks || chunks > 0x10000) return false;
    e.base = (DWORD*)HeapAlloc(GetProcessHeap(), 0, (chunks + 1) * 4);
    if (!e.base) return false;
    DWORD n = 0;
    for (DWORD c = 0; c < chunks; c++) {
        e.base[c] = n;
        if (table[c] + 2 > size) return false;
        n += *(const unsigned short*)(rec + table[c]);
    }
    e.base[chunks] = n;
    e.n = n;
    e.lab = (int*)HeapAlloc(GetProcessHeap(), 0, (n ? n : 1) * 4);
    if (!e.lab) return false;
    for (DWORD i = 0; i < n; i++) e.lab[i] = (int)i;
    for (DWORD c = 0; c < chunks; c++) {
        const u8* ch = rec + table[c];
        const DWORD cnt = e.base[c + 1] - e.base[c];
        for (DWORD i = 0; i < cnt; i++) {
            const u8* r = ch + ((const unsigned short*)(ch + 2))[i];
            if (r + 5 > rec + size) return false;
            const DWORD nb = r[0];
            if (!(r[1] & 0x40)) continue;  // no edge bytes: every edge is byte 0, a wall in mode 2
            if (r + 5 + 3 * nb > rec + size) return false;
            for (DWORD j = 0; j < nb; j++) {
                if (r[5 + 2 * nb + j] != 3) continue;
                const DWORD ref = ((const unsigned short*)(r + 5))[j];
                const int dx = (int)((ref >> 10) & 7) - 3, dz = (int)((ref >> 13) & 7) - 3;
                const int tc = (int)c + dz * (int)w + dx;
                if (tc < 0 || (DWORD)tc >= chunks || (ref & 0x3FF) >= e.base[tc + 1] - e.base[tc]) return false;
                const int a = EoFind(e.lab, (int)(e.base[c] + i)), b = EoFind(e.lab, (int)(e.base[tc] + (ref & 0x3FF)));
                if (a != b) e.lab[a] = b;
            }
        }
    }
    for (DWORD i = 0; i < n; i++) e.lab[i] = EoFind(e.lab, (int)i);
    e.ok = true;
    return true;
}

// 1 = proven unreachable, 0 = not proven (or no usable labels)
static int EoCarApart(const u8* R, DWORD s, DWORD g) {
    int r = 0;
    EnterCriticalSection(&g_eoCs);
    EoCar& e = g_eoCar;
    const u8* rec = *(const u8* const*)(R + 0xC);
    if (e.rec != rec || e.size != *(const DWORD*)(R + 0x10) || e.w != *(const unsigned short*)R || e.h != *(const DWORD*)(R + 4)) {
        LARGE_INTEGER t0;
        QueryPerformanceCounter(&t0);
        EoCarBuild(e, R);
        InterlockedIncrement(&ts3native_counters.eoLabelBuilds);
        InterlockedExchangeAdd(&ts3native_counters.eoLabelUs, UsSince(t0));
    }
    const DWORD cs = s & 0xFFFF, cg = g & 0xFFFF, ws = s >> 16, wg = g >> 16;
    if (e.ok && cs < e.w * e.h && cg < e.w * e.h && ws < e.base[cs + 1] - e.base[cs] && wg < e.base[cg + 1] - e.base[cg])
        r = e.lab[e.base[cs] + ws] != e.lab[e.base[cg] + wg];
    LeaveCriticalSection(&g_eoCs);
    return r;
}

// the game's flood colour of a waypoint ref (chunk | wp << 16), as the planner reads it at 0x65F781; -2 = none
static int EoColour(const u8* R, DWORD ref) {
    if (!R[0x14]) return -2;
    const DWORD* first = *(const DWORD* const*)(R + 0x18);
    const DWORD* firstEnd = *(const DWORD* const*)(R + 0x1C);
    const signed char* col = *(const signed char* const*)(R + 0x28);
    const signed char* colEnd = *(const signed char* const*)(R + 0x2C);
    const DWORD c = ref & 0xFFFF, i = (first && c < (DWORD)(firstEnd - first)) ? first[c] + (ref >> 16) : 0xFFFFFFFF;
    return col && i < (DWORD)(colEnd - col) ? col[i] : -2;
}

// SHORT (+0x28) and LONG (+0x4C) cost sets of the container: the A* picks one per search from the goal's distance (and, Mode 2,
// the act path copies the same one). Walls are equal under both when no factor product can reach the 1e8 wall: factors 0-5 of
// each set (keep, trespass, sidewalk, riding, street, water-lot) finite and positive, and the largest edge factor of docs/99
// §10.1 (keep x max(1000 trespass, 10/sidewalk, 10/riding, 10/street, 30)) times the longest edge (3 chunks of 32 m + one more
// chunk in x and z: 182 m) below 1e8. Slots 6-8 are the 1/x the A* computes into the context (0 in the container, docs/108 §7
// F-1); v78 required them > 0 too, so the memo never stored in game (docs/142 §9.2).
static bool EoCostSetsPlain(const u8* c) {
    for (int s = 0; s < 2; s++) {
        const float* f = (const float*)(c + 0x28 + s * 0x24);
        for (int i = 0; i < 6; i++)
            if (!(f[i] > 0.0f && f[i] < 1e6f)) return false;
        float k = 1000.0f * f[1] > 30.0f ? 1000.0f * f[1] : 30.0f;
        for (int i = 2; i <= 4; i++) k = 10.0f / f[i] > k ? 10.0f / f[i] : k;
        if ((f[0] > 1.0f ? f[0] : 1.0f) * k * 182.0f >= 1e8f) return false;
    }
    return true;
}

static inline void EoFnv(DWORD& h, DWORD v) { for (int i = 0; i < 4; i++, v >>= 8) { h ^= v & 0xFF; h *= 16777619u; } }

// EASTL int set (anchor: right +0, left +4, parent = root +8; size +0x10; node value +0x10): order-free hash of its values;
// false when it is larger than 1024 or does not walk (shortcut: no cycle check beyond the size bound)
static bool EoSetHash(const u8* set, DWORD* out) {
    const DWORD size = *(const DWORD*)(set + 0x10);
    const u8* root = *(const u8* const*)(set + 8);
    DWORD sum = size * 0x9E3779B9u, n = 0;
    if (size > 1024) return false;
    const u8* stack[64];
    int sp = 0;
    if (size && root && root != set) stack[sp++] = root;
    while (sp) {
        const u8* x = stack[--sp];
        if (++n > size) return false;
        DWORD v = *(const DWORD*)(x + 0x10) * 0x85EBCA6Bu;
        sum += v ^ (v >> 13);
        for (int k = 0; k < 2; k++) {
            const u8* ch = *(const u8* const*)(x + k * 4);
            if (ch && ch != set) { if (sp == 64) return false; stack[sp++] = ch; }
        }
    }
    *out = sum;
    return n == size;
}

// Mode 2 wall print (docs/142 §9.3): the dynamic inputs of the edge walls a memo depends on. R's records (count, flags byte with
// the blocked bit and the type bits, docs/112 §3.1), the lot-edge group hash W+0xA0 (head words; its writers are
// ApplyLotUpdate 0x82B590 / LoadLotData 0x82CA10, p4clone.cpp) and the start/goal group sets opts+0x18 / +0x48. Not covered:
// the point test 0xC6BAB0 and the segment test 0x627780 (world manager and footprints; inferred stable inside one request,
// sampled by the shadow runs). False = no print (no memo)
static bool EoWallPrint(const u8* R, const u8* o, DWORD* out) {
    DWORD h = 2166136261u;
    const DWORD w = *(const unsigned short*)R, chunks = w * *(const DWORD*)(R + 4);
    const DWORD* table = *(const DWORD* const*)(R + 8);
    const u8* rec = *(const u8* const*)(R + 0xC);
    const DWORD size = *(const DWORD*)(R + 0x10);
    if (!rec || !table || !chunks || chunks > 0x10000) return false;
    EoFnv(h, (DWORD)(uintptr_t)rec), EoFnv(h, size);
    for (DWORD c = 0; c < chunks; c++) {
        if (table[c] + 2 > size) return false;
        const u8* ch = rec + table[c];
        const DWORD cnt = *(const unsigned short*)ch;
        if (table[c] + 2 + 2 * cnt > size) return false;
        EoFnv(h, cnt);
        for (DWORD i = 0; i < cnt; i++) {
            const DWORD off = table[c] + ((const unsigned short*)(ch + 2))[i];
            if (off + 2 > size) return false;
            h ^= rec[off + 1], h *= 16777619u;
        }
    }
    const u8* W = *(const u8* const*)(o + 0x88);
    if (W) for (int i = 0; i < 4; i++) EoFnv(h, *(const DWORD*)(W + 0xA0 + i * 4));
    DWORD a, b;
    if (!EoSetHash(o + 0x18, &a) || !EoSetHash(o + 0x48, &b)) return false;
    EoFnv(h, a), EoFnv(h, b);
    *out = h;
    return true;
}

// which proof holds before the search: 0 none, 1 car, 2 colour, 3 memo. act: Mode 2, the memo also needs an equal wall print
static int EoProve(EoReq& q, const u8* c, const u8* R, const u8* G, const DWORD* s, const DWORD* g, DWORD m, const u8* o, bool act) {
    if ((*s & 0xFFFF) == 0xFFFF || (*g & 0xFFFF) == 0xFFFF || *s == *g || !R) return 0;  // virtual (water) ends: no proof
    if (m == 2 && EoCarApart(R, *s, *g)) return 1;
    const DWORD flags = *(const DWORD*)o;
    if (m >= 1 && m <= 3 && !(flags & 0x180)) {
        const int a = EoColour(R, *s), b = EoColour(R, *g);
        if (a >= 0 && b >= 0 && a != b) return 2;
    }
    if (q.memo) {
        DWORD k[EO_KEY];
        EoKey(k, c, R, G, s, m, o);
        if (!memcmp(k, q.key, sizeof(k))) {
            const DWORD* table = *(const DWORD* const*)(c + 0xC);
            const DWORD i = table[((*g & 0xFFFF) + 1) & 0xFFFF] + ((*g >> 16) >> 3);
            if (i < q.bitsLen && !(q.bits[i] & (1 << ((*g >> 16) & 7)))) {
                DWORD p;
                if (act && (!EoWallPrint(R, o, &p) || p != q.memoPrint)) {
                    InterlockedIncrement(&ts3native_counters.eoStale[1]);
                    return 0;
                }
                return 3;
            }
        }
    }
    return 0;
}

// after a failed unrestricted search: its closed bits are the start's reachable set under this wall context
static void EoMemoStore(EoReq& q, const u8* c, const u8* R, const u8* G, const DWORD* s, DWORD m, const u8* o, LONG us) {
    q.memo = false;
    const DWORD len = *(const DWORD*)(c + 8);
    const u8* bits = *(const u8* const*)(c + 0x10);
    if (!bits || !len || len > (1u << 20) || !EoCostSetsPlain(c)) return;
    // Mode 2: a memo is worth its wall print only behind a search that cost at least MemoMinUs
    if (ts3native_counters.eoAct && (us < g_eoMemoMinUs || !EoWallPrint(R, o, &q.memoPrint))) return;
    if (q.bitsCap < len) {
        HeapFree(GetProcessHeap(), 0, q.bits);
        q.bits = (u8*)HeapAlloc(GetProcessHeap(), 0, len);
        q.bitsCap = q.bits ? len : 0;
        if (!q.bits) return;
    }
    memcpy(q.bits, bits, len);
    q.bitsLen = len;
    EoKey(q.key, c, R, G, s, m, o);
    q.memo = true;
    LONG nodes = 0;
    for (DWORD i = 0; i < len; i++)
        for (u8 x = bits[i]; x; x &= x - 1) nodes++;
    Max(&ts3native_counters.eoReachMax, nodes);
}

// Mode 2, car: the closed bits a failed mode-2 search leaves = every node reachable from the start over forward byte-3 edges
// (mode 2 walls every other edge and every virtual end, with no blocked, point or polygon test: docs/99 §10.1). A BFS on the
// live R writes them into the container; 1 = it reached the goal (the labels were stale: no act), -1 = a reference outside
// the tables (no act), 0 = done
static int EoCarFill(EoReq& q, const u8* c, const u8* R, const DWORD* s, const DWORD* g) {
    const DWORD w = *(const unsigned short*)R, chunks = w * *(const DWORD*)(R + 4);
    const DWORD* rt = *(const DWORD* const*)(R + 8);
    const u8* rec = *(const u8* const*)(R + 0xC);
    const DWORD size = *(const DWORD*)(R + 0x10);
    const DWORD* ct = *(const DWORD* const*)(c + 0xC);
    u8* bits = *(u8* const*)(c + 0x10);
    const DWORD len = *(const DWORD*)(c + 8);
    memset(bits, 0, len);
    DWORD head = 0, tail = 0;
    auto push = [&](DWORD ref) -> bool {
        const DWORD i = ct[((ref & 0xFFFF) + 1) & 0xFFFF] + ((ref >> 16) >> 3);
        if (i >= len) return false;
        const u8 bit = (u8)(1 << ((ref >> 16) & 7));
        if (bits[i] & bit) return true;
        bits[i] |= bit;
        if (tail == q.bfsCap) {
            const DWORD cap = q.bfsCap ? q.bfsCap * 2 : 4096;
            DWORD* nb = (DWORD*)HeapAlloc(GetProcessHeap(), 0, cap * 4);
            if (!nb) return false;
            if (q.bfs) memcpy(nb, q.bfs, tail * 4), HeapFree(GetProcessHeap(), 0, q.bfs);
            q.bfs = nb, q.bfsCap = cap;
        }
        q.bfs[tail++] = ref;
        return true;
    };
    if (!push(*s)) return -1;
    while (head < tail) {
        const DWORD at = q.bfs[head++], ch = at & 0xFFFF, wp = at >> 16;
        if (ch >= chunks || rt[ch] + 2 > size) return -1;
        const u8* cp = rec + rt[ch];
        if (wp >= *(const unsigned short*)cp || rt[ch] + 4 + 2 * wp > size) return -1;
        const u8* r = cp + ((const unsigned short*)(cp + 2))[wp];
        if (r + 5 > rec + size) return -1;
        const DWORD n = r[0];
        if (!(r[1] & 0x40)) continue;
        if (r + 5 + 3 * n > rec + size) return -1;
        for (DWORD j = 0; j < n; j++) {
            if (r[5 + 2 * n + j] != 3) continue;
            const DWORD ref = ((const unsigned short*)(r + 5))[j];
            const int tc = (int)ch + ((int)(ref >> 13) - 3) * (int)w + (int)((ref >> 10) & 7) - 3;
            if (tc < 0 || (DWORD)tc >= chunks) return -1;
            const DWORD to = (DWORD)tc | (ref & 0x3FF) << 16;
            if (to == *g) return 1;
            if (!push(to)) return -1;
        }
    }
    return 0;
}

// Mode 2: the A*'s own writes before its loop, in its order (0x676268..0x67642A [verified, decompile + asm]): the circle
// prepass (on a failure the game clears r^2 again, 0x6762C4), +0xA4 start, +0xB4 goal, LONG set into +0x64..+0x87, h0 with the
// heuristic, SHORT set when threshold >= h0, the three 1/x slots, trespass = FLT_MAX with flag 0x40, *best = start. Returns h0
// as the A* keeps it (float)
static float EoActPrologue(const u8* c, const u8* R, const u8* G, const DWORD* s, const DWORD* g, EoCostFn fn, u8* o, DWORD* b) {
    if (*(const float*)(o + 0xA0) == 0.0f && g_eoCircleFn(R, o, s, g)) *(float*)(o + 0xA0) = 0.0f;
    *(DWORD*)(o + 0xA4) = *s;
    *(DWORD*)(o + 0xB4) = *g;
    memcpy(o + 0x64, c + 0x4C, 36);
    const double h0 = fn(R, G, s, g, 0, 0, o, NULL);
    if (h0 <= (double)*(const float*)(c + 0x24)) memcpy(o + 0x64, c + 0x28, 36);
    float* k = (float*)(o + 0x64);
    for (int i = 0; i < 3; i++)
        if (k[2 + i] != 0.0f) k[6 + i] = *g_eoRecipK / k[2 + i];
    if (*o & 0x40) *(DWORD*)(o + 0x68) = *g_eoRetryTrespass;
    *b = *s;
    return (float)h0;
}

// Mode 2: *best as the failed search leaves it: the start, then every generated node (= the closed set minus the start) whose
// heuristic is strictly smaller than the smallest so far (float compares, JBE skips: 0x67672A, 0x67694D, 0x676B8A). The game takes the first of equal
// minima in push order, which only the search knows: when the smallest heuristic below h0 belongs to two or more nodes the
// answer is refused (-2, eoActTie; steerro-test K6E found such ties on byte-grid positions). Returns the node count, -1 for a
// table shape it does not know (no act)
static LONG EoActBest(const u8* c, const u8* R, const u8* G, const DWORD* s, const DWORD* g, EoCostFn fn, u8* o, DWORD* b, float h0) {
    const DWORD chunks = (DWORD)*(const unsigned short*)R * *(const DWORD*)(R + 4);
    const DWORD* ct = *(const DWORD* const*)(c + 0xC);
    const u8* bits = *(const u8* const*)(c + 0x10);
    const DWORD len = *(const DWORD*)(c + 8);
    float best = h0;
    LONG n = 0, nBest = 0;
    for (DWORD t = 0; t <= chunks; t++) {
        const DWORD lo = ct[t], hi = t < chunks ? ct[t + 1] : len;
        if (lo > hi || hi > len) return -1;
        for (DWORD i = lo; i < hi; i++)
            for (u8 x = bits[i]; x; x &= x - 1) {
                DWORD bit = 0;
                while (!((x >> bit) & 1)) bit++;
                const DWORD ref = (t ? t - 1 : 0xFFFF) | ((i - lo) * 8 + bit) << 16;
                n++;
                if (ref == *s) continue;
                const float h = (float)fn(R, G, &ref, g, 0, 0, o, NULL);
                if (h < best) best = h, *b = ref, nBest = 1;
                else if (h == best) nBest++;
            }
    }
    return *b != *s && nBest > 1 ? -2 : n;
}

// Mode 2: answer a proven call without the search. kind 0 car, 1 memo. 0 = answered (result 0), 1 = car BFS reached the goal,
// -1 = not answered, -2 = heuristic tie (the caller runs the game, which rewrites everything written here). *nodes = closed-set size
static int EoAct(EoReq& q, int kind, u8* c, const u8* R, const u8* G, const DWORD* s, const DWORD* g, DWORD m, u8* o, DWORD* b, LONG* nodes) {
    const EoCostFn fn = (EoCostFn)(uintptr_t)g_eoCostFn[m >= 1 && m <= 4 ? m - 1 : 4];
    if (kind == 0) {
        const int r = EoCarFill(q, c, R, s, g);
        if (r) return r;
    } else {
        if (q.bitsLen != *(const DWORD*)(c + 8)) return -1;
        memcpy(*(u8* const*)(c + 0x10), q.bits, q.bitsLen);
    }
    const float h0 = EoActPrologue(c, R, G, s, g, fn, o, b);
    *nodes = EoActBest(c, R, G, s, g, fn, o, b, h0);
    return *nodes < 0 ? *nodes : 0;
}

static bool EoShadowDue() {
    if (ts3native_counters.eoAct == 1) return true;
    const LONG n = InterlockedIncrement(&g_eoActN);
    return n <= g_eoShadowFirst || (g_eoShadowEvery > 0 && n % g_eoShadowEvery == 0);
}

static DWORD __fastcall EoAsHook(u8* c, void* e, const u8* R, const u8* G, const DWORD* s, const DWORD* g, DWORD m, u8* o, DWORD* b) {
    EoReq& q = t_eo;
    Ts3nCounters& k = ts3native_counters;
    // nested: the A*'s own circle rerun (0x6762B4) or a plan inside a managed call; the outer call measures the whole
    if (!q.active || q.depth || !k.eoMode) return g_eoAsOrig(c, e, R, G, s, g, m, o, b);
    const bool restricted = *(const float*)(o + 0xA0) != 0.0f;  // circle set at entry: one restricted search, no rerun
    const bool act = k.eoAct && !k.eoActOff;
    int why = EoProve(q, c, R, G, s, g, m, o, act);
    bool shadow = false;
    LONG actUs = 0, nodes = 0;
    u8 mine[0x58];
    DWORD myBest = 0;
    if (why && act) {
        // colour proofs do not know the reachable set (no *best); a restricted entry reaches an unknown subset; a capped
        // budget makes the game's own search end at once with *best = start
        if (why == 2 || restricted || m < 1 || m > 4 || !EoCostSetsPlain(c) || (t_budget.on && t_budget.capped)) {
            InterlockedIncrement(&k.eoActRefused);
        } else {
            const int kind = why == 1 ? 0 : 1;
            LARGE_INTEGER t0;
            QueryPerformanceCounter(&t0);
            const int a = EoAct(q, kind, c, R, G, s, g, m, o, b, &nodes);
            actUs = UsSince(t0);
            InterlockedExchangeAdd(&k.eoActUs, actUs);
            Max(&k.eoActMaxUs, actUs);
            if (a == 1) InterlockedIncrement(&k.eoStale[0]), why = 0;  // the live BFS overrules stale labels: no proof
            else if (a == -2) InterlockedIncrement(&k.eoActTie);
            else if (a < 0) InterlockedIncrement(&k.eoActRefused);
            else if (!(shadow = EoShadowDue())) {
                q.prov++, q.why[why - 1]++, q.acted++, q.actWhy[kind]++;
                InterlockedIncrement(&k.eoActed);
                InterlockedIncrement(&k.eoActedWhy[kind]);
                const LONG sn = k.eoShadowNodes[kind];
                if (sn > 0) q.savedUs += (LONG)((LONGLONG)nodes * k.eoShadowUs[kind] / sn) - actUs;
                return 0;
            } else {  // keep ours, then let the game answer the same call and compare
                memcpy(mine, o + 0x64, sizeof(mine));
                myBest = *b;
                const DWORD len = *(const DWORD*)(c + 8);
                if (q.shCap < len) {
                    HeapFree(GetProcessHeap(), 0, q.shBits);
                    q.shBits = (u8*)HeapAlloc(GetProcessHeap(), 0, len);
                    q.shCap = q.shBits ? len : 0;
                }
                if (q.shBits) memcpy(q.shBits, *(u8* const*)(c + 0x10), len); else shadow = false;
            }
        }
    }
    q.depth++;
    LARGE_INTEGER t0;
    QueryPerformanceCounter(&t0);
    const DWORD r = g_eoAsOrig(c, e, R, G, s, g, m, o, b);
    const LONG us = UsSince(t0);
    q.depth--;
    q.as++, q.asUs += us;
    Max(&k.eoAsMaxUs, us);
    if (!r) q.asFail++;
    if (why) {
        q.prov++, q.provUs += us, q.why[why - 1]++;
        if (r) {  // the proof was wrong: the design's correctness argument failed live
            q.wrong++;
            if (InterlockedIncrement(&k.eoWrong) <= 4)
                Log("route early-out: WRONG proof %d: mode %lu start %08lX goal %08lX flags %08lX found a path", why, m, *s, *g, *(const DWORD*)o);
        }
    }
    if (shadow && !t_budget.capped) {
        const int kind = why == 1 ? 0 : 1;
        InterlockedIncrement(&k.eoShadow);
        InterlockedExchangeAdd(&k.eoShadowUs[kind], us);
        InterlockedExchangeAdd(&k.eoShadowNodes[kind], nodes);
        const DWORD len = *(const DWORD*)(c + 8);
        if (r || *b != myBest || memcmp(mine, o + 0x64, sizeof(mine)) || memcmp(q.shBits, *(u8* const*)(c + 0x10), len)) {
            InterlockedIncrement(&k.eoActDiff);
            if (!InterlockedExchange(&k.eoActOff, 1))
                Log("route early-out: act OFF: proof %d mode %lu start %08lX goal %08lX: game %s best %08lX/%08lX context %s bits %s", why, m,
                    *s, *g, r ? "PATH" : "no path", *b, myBest, memcmp(mine, o + 0x64, sizeof(mine)) ? "differs" : "same",
                    memcmp(q.shBits, *(u8* const*)(c + 0x10), len) ? "differ" : "same");
        }
    }
    // a capped search (budget or SyncCapMs) stopped early: its closed set is not the reachable set
    if (!r && !restricted && *(const float*)(o + 0xA0) == 0.0f && !t_budget.capped) EoMemoStore(q, c, R, G, s, m, o, us);
    return r;
}

// --- request scope (CallPlan): counts per Route_PlanLocked, one log line for a slow one ---
static void EoBegin(EoReq& saved) {
    saved = t_eo;
    EoReq& q = t_eo;
    q.active = 1, q.depth = 0;
    q.as = q.asFail = q.asUs = q.prov = q.provUs = q.wrong = q.acted = q.savedUs = 0;
    q.why[0] = q.why[1] = q.why[2] = q.actWhy[0] = q.actWhy[1] = 0;
    q.memo = false;
    QueryPerformanceCounter(&q.t0);
}

static void EoEnd(const EoReq& saved, u8 result) {
    EoReq& q = t_eo;
    Ts3nCounters& k = ts3native_counters;
    const LONG us = UsSince(q.t0);
    InterlockedIncrement(&k.eoReqs);
    InterlockedExchangeAdd(&k.eoAs, q.as);
    InterlockedExchangeAdd(&k.eoAsFail, q.asFail);
    InterlockedExchangeAdd(&k.eoAsMs, (q.asUs + 500) / 1000);
    InterlockedExchangeAdd(&k.eoProv, q.prov);
    InterlockedExchangeAdd(&k.eoProvMs, (q.provUs + 500) / 1000);
    for (int i = 0; i < 3; i++) InterlockedExchangeAdd(&k.eoProvWhy[i], q.why[i]);
    if (q.savedUs > 0) InterlockedExchangeAdd(&k.eoActSavedMs, (q.savedUs + 500) / 1000);
    if (us >= g_eoLogUs) {
        InterlockedIncrement(&k.eoSlow);
        InterlockedExchangeAdd(&k.eoSlowMs, us / 1000);
        InterlockedExchangeAdd(&k.eoSlowAsMs, q.asUs / 1000);
        InterlockedExchangeAdd(&k.eoSlowProvMs, q.provUs / 1000);
        if (us - q.provUs < g_eoLogUs) InterlockedIncrement(&k.eoSlowSaved);  // the proofs alone would bring it under LogMs
        const DWORD now = GetTickCount();
        if (now - g_eoMinute >= 60000) g_eoMinute = now, g_eoLines = 0;
        if (g_eoLines < 20) {
            g_eoLines++;
            Log("route early-out: plan %ld ms (%s, %s): world A* %ld calls %ld fails %ld ms, provable %ld (car %ld colour %ld memo %ld) "
                "%ld ms, wrong %ld, acted %ld (car %ld memo %ld) ~%ld ms saved (%lu lines rate-limited before)", us / 1000,
                GetCurrentThreadId() == g_workerTid ? "worker" : "caller", result ? "path" : "no path", q.as, q.asFail, q.asUs / 1000,
                q.prov, q.why[0], q.why[1], q.why[2], q.provUs / 1000, q.wrong, q.acted, q.actWhy[0], q.actWhy[1], q.savedUs / 1000,
                g_eoDropped);
            g_eoDropped = 0;
        } else {
            g_eoDropped++;
        }
    }
    // the outer request (a plan nested in a managed call of another plan on this thread) resumes; its memo is dropped because
    // the nested search reused the closed bits and the buffer
    u8* bits = q.bits;
    const DWORD cap = q.bitsCap;
    DWORD* bfs = q.bfs;
    const DWORD bfsCap = q.bfsCap;
    u8* sh = q.shBits;
    const DWORD shCap = q.shCap;
    q = saved;
    q.bits = bits, q.bitsCap = cap, q.memo = false;
    q.bfs = bfs, q.bfsCap = bfsCap, q.shBits = sh, q.shCap = shCap;
}

// Reads [RouteEarlyOut]; true = install the A* detour (needs the Route_PlanLocked detour for the request scope)
static bool ReadEarlyOut(const wchar_t* ini) {
    if (!GetPrivateProfileIntW(L"RouteEarlyOut", L"Enabled", 0, ini)) { Log("route early-out disabled (Enabled=0)"); return false; }
    QueryPerformanceFrequency(&g_qpf);
    InitializeCriticalSection(&g_eoCs);
    const int mode = Clamp(GetPrivateProfileIntW(L"RouteEarlyOut", L"Mode", 1, ini), 0, 2);
    const int act = Clamp(GetPrivateProfileIntW(L"RouteEarlyOut", L"Act", 0, ini), 0, 1);
    ts3native_counters.eoMode = mode != 0;
    ts3native_counters.eoAct = mode == 2 ? 1 + act : 0;
    g_eoLogUs = Clamp(GetPrivateProfileIntW(L"RouteEarlyOut", L"LogMs", 200, ini), 1, 60000) * 1000;
    g_eoShadowFirst = Clamp(GetPrivateProfileIntW(L"RouteEarlyOut", L"ShadowFirst", 32, ini), 0, 1000000);
    g_eoShadowEvery = Clamp(GetPrivateProfileIntW(L"RouteEarlyOut", L"ShadowEvery", 16, ini), 0, 1000000);
    g_eoMemoMinUs = Clamp(GetPrivateProfileIntW(L"RouteEarlyOut", L"MemoMinUs", 1000, ini), 0, 10000000);
    Log("route early-out settings: Mode=%d Act=%d (eoMode %ld eoAct %ld) LogMs=%ld ShadowFirst=%ld ShadowEvery=%ld MemoMinUs=%ld", mode,
        act, ts3native_counters.eoMode, ts3native_counters.eoAct, g_eoLogUs / 1000, g_eoShadowFirst, g_eoShadowEvery, g_eoMemoMinUs);
    return true;
}

static void InstallEarlyOut(bool planDetour) {
    if (!planDetour) { Log("FAIL route early-out: no Route_PlanLocked detour, nothing installed"); return; }
    g_eoAsOrig = (EoAsFn)InstallDetour("WorldAStar_Search (early-out)", WORLD_ASTAR, kWorldAStarPrologue,
                                       sizeof(kWorldAStarPrologue), (void*)EoAsHook, &g_eoAsOrig);
    ts3native_counters.eoReady = g_eoAsOrig != NULL;
}

static void EoLogCounters() {
    const Ts3nCounters& c = ts3native_counters;
    Log("route early-out: mode %ld plans %ld A* %ld fails %ld %ld ms max %ld us, provable %ld (car %ld colour %ld memo %ld) %ld ms, "
        "wrong %ld, slow %ld (%ld ms, A* %ld ms, provable %ld ms, saved %ld), reach max %ld, labels %ld (%ld us)", c.eoMode, c.eoReqs,
        c.eoAs, c.eoAsFail, c.eoAsMs, c.eoAsMaxUs, c.eoProv, c.eoProvWhy[0], c.eoProvWhy[1], c.eoProvWhy[2], c.eoProvMs, c.eoWrong,
        c.eoSlow, c.eoSlowMs, c.eoSlowAsMs, c.eoSlowProvMs, c.eoSlowSaved, c.eoReachMax, c.eoLabelBuilds, c.eoLabelUs);
    if (c.eoAct)
        Log("route early-out act: level %ld off %ld acted %ld (car %ld memo %ld) %ld us max %ld us, ~%ld ms saved, shadow %ld (car %ld us "
            "%ld nodes, memo %ld us %ld nodes) diff %ld, stale car %ld memo %ld, refused %ld, ties %ld", c.eoAct, c.eoActOff, c.eoActed,
            c.eoActedWhy[0], c.eoActedWhy[1], c.eoActUs, c.eoActMaxUs, c.eoActSavedMs, c.eoShadow, c.eoShadowUs[0], c.eoShadowNodes[0],
            c.eoShadowUs[1], c.eoShadowNodes[1], c.eoActDiff, c.eoStale[0], c.eoStale[1], c.eoActRefused, c.eoActTie);
}

// Route_PlanLocked on this thread (RunPlan, the worker): the request scope around the budget wrapper
static u8 CallPlan(void* world, void* edx, int* outList, int* starts, int* goals, DWORD agent, DWORD doPlan, u8* opts,
                   int* out1, int* out2, DWORD guidLo, DWORD guidHi) {
    if (!ts3native_counters.eoReady || !ts3native_counters.eoMode)
        return CallPlanBudget(world, edx, outList, starts, goals, agent, doPlan, opts, out1, out2, guidLo, guidHi);
    EoReq saved;
    EoBegin(saved);
    const u8 r = CallPlanBudget(world, edx, outList, starts, goals, agent, doPlan, opts, out1, out2, guidLo, guidHi);
    EoEnd(saved, r);
    return r;
}
