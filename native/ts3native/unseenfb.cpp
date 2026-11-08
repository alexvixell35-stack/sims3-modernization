// [UnseenFallback] (docs/202 §3.1, counters v138 uf*): a deadline for the route plans and route jobs of Sims the player cannot
// see. ts3fixes (UnseenFallback.cs, switch unseenfbplan) decides "unseen" (live mode, off screen, far from the camera and the
// household, not on the active lot, ...) and MARKs the route right before its plan; this file only applies the deadline.
// Included by ts3native.cpp after plandeadline.cpp (it uses PdSess / t_pd and the deadline exits installed there).
//
// Channel: GetOption2 with optHi 0x83000000 (bits 63 + 57 + 56, no RouteOption2 name; the game and older builds answer 0),
//   optLo 1 = MARK: the route's next async START (any kind, own or foreign) or sync IC takes the mark; answers 1 when
//            Enabled=1, ufMode 1 and the exits are installed (ufReady bits 1-3), else 0 and nothing is marked;
//   optLo 2 = CUT?: 1 once when that route's marked plan or job ran into its deadline; clears the mark either way.
// The deadline (ufUs, or the smaller one the plan already had) runs through PlanDeadlineMs' exits: on the worker the job slot
// (t_fbDeadline from the job's start, g_pdWkTid: world A* cost stubs, cell A*, water), on the sim a deadline session around
// the sync IC (PdIcBegin, also for own-task ICs PlanDeadlineOwnMs 0 exempts; never a stuck-check last round). A cut plan
// ends as an ordinary failed plan [verified exits, docs/164 §7.2, docs/178 T1]; nothing else is written.
// shortcut: one mark slot; a nested plan of another route marked inside the marked plan's callbacks takes it (the outer
// plan then runs uncut). Upgrade to a per-handle table if ufMarks >> ufSync + ufJobs in game.

// docs/202: DeadlineMs is a player setting, default 300, kept in [175, 300] ms (Rodney 2026-10-26: 175 = the lowest a player may set)
static const int kUfMinMs = 175, kUfMaxMs = 300;
static int UfClampMs(int ms) { return ms < kUfMinMs ? kUfMinMs : ms > kUfMaxMs ? kUfMaxMs : ms; }

static void ReadUnseenFallback(const wchar_t* ini) {
    Ts3nCounters& k = ts3native_counters;
    g_ufIni = Clamp(GetPrivateProfileIntW(L"UnseenFallback", L"Enabled", 0, ini), 0, 1);
    const int raw = GetPrivateProfileIntW(L"UnseenFallback", L"DeadlineMs", 300, ini);
    const int ms = UfClampMs(raw);
    k.ufUs = ms * 1000;
    if (ms != raw) Log("unseenfb: DeadlineMs=%d (clamped from %d)", ms, raw);
    k.ufHlMode = Clamp(GetPrivateProfileIntW(L"UnseenFallback", L"HostLate", 0, ini), 0, 1);  // docs/202 §3.1.1 item 5
    if (g_ufIni) Log("unseen fallback: Enabled=1 DeadlineMs=%ld HostLate=%ld", k.ufUs / 1000, k.ufHlMode);
}
// the deadline in use (us): ufUs as the harness may have written it (unseenfbus), clamped the same way
static LONG UfUs() { return UfClampMs(ts3native_counters.ufUs / 1000) * 1000; }

// after PdInstallAt (InstallPlanDeadline): needs at least one search exit and the route IC hooks ([RouteAsync])
static void UfInstalled() {
    Ts3nCounters& k = ts3native_counters;
    k.ufReady = (k.budgetReady ? 1 : 0) | (g_pdOrig[0] ? 2 : 0) | (g_pdOrig[1] ? 4 : 0) | (k.asyncReady ? 8 : 0);
    k.ufMode = g_ufIni && (k.ufReady & 6) && (k.ufReady & 8) ? 1 : 0;
    Log("%s unseen fallback: deadline %ld ms, ready 0x%lX (bit 0 world A* cost stubs, 1 cell A*, 2 water, 3 route IC hooks), mode %ld",
        k.ufReady == 15 ? "OK  " : k.ufMode ? "PART" : "FAIL", k.ufUs / 1000, k.ufReady, k.ufMode);
}

static bool UfOn() {
    const Ts3nCounters& k = ts3native_counters;
    return k.ufMode == 1 && (k.ufReady & 6) && (k.ufReady & 8);
}

static u8 UfMsg(DWORD handle, DWORD lo) {
    Ts3nCounters& k = ts3native_counters;
    if (lo == 2) {  // CUT?
        const bool cut = handle && g_ufCutH == handle;
        if (g_ufH == handle) g_ufH = 0;
        g_ufCutH = 0;
        if (cut) InterlockedIncrement(&k.ufCutAsks);
        return cut ? 1 : 0;
    }
    g_ufCutH = 0;
    if (!handle || !UfOn()) {
        g_ufH = 0;
        InterlockedIncrement(&k.ufMarkRefused);
        return 0;
    }
    g_ufH = handle;
    InterlockedIncrement(&k.ufMarks);
    return 1;
}

// sim thread: the mark is good for this route's next START or sync IC only
static bool UfTake(DWORD handle) {
    if (!handle || g_ufH != handle) return false;
    g_ufH = 0;
    return UfOn();
}

// docs/202 §3.1.1 strict DeadlineMs (Rodney 2026-10-26): the whole marked plan or job (its pre-search +0x918 wait included)
// stays under the setting. The cut point = setting - overrun budget. Per path (ctx 0 sync plan, 1 route job; exit 0 world A*
// cost stubs, 1 cell A*, 2 water) the budget is the p99 of the measured time from the deadline to the return of plans that
// exit cut, a job's waits for the sim thread left out (no exit runs in them), floor 30 ms; below 20 samples max(80 ms, the largest sample). A context takes the largest of its exits (80 ms
// with none yet); the cut point never goes below setting / 2.
// shortcut: one histogram per path for the whole session (no aging); upgrade to a sliding window if loads differ a lot.
enum { kUfOverBuckets = 76 };                       // 4 ms buckets over 0-300 ms, the last one = 300 ms and more
static const LONG kUfOverBucketUs = 4000, kUfOverFloorUs = 30000, kUfOverStartUs = 80000;
static const LONG kUfOverMinN = 20;
struct UfOver { LONG n, maxUs, hist[kUfOverBuckets]; };
static UfOver g_ufOver[2][3];  // [ctx][exit]: ctx 0 written on the sim thread only, ctx 1 on the route worker only

static void UfOverNote(int ctx, unsigned mask, LONG lateUs) {
    if (!(mask & 7) || lateUs < 0) return;
    const LONG b = lateUs / kUfOverBucketUs < kUfOverBuckets - 1 ? lateUs / kUfOverBucketUs : kUfOverBuckets - 1;
    for (int e = 0; e < 3; e++) {
        if (!(mask >> e & 1)) continue;
        UfOver& o = g_ufOver[ctx][e];
        o.n++, o.hist[b]++;
        if (lateUs > o.maxUs) o.maxUs = lateUs;
    }
    InterlockedIncrement(&ts3native_counters.ufOverSamples);
}
static LONG UfOverP99(const UfOver& o) {
    if (o.n < kUfOverMinN) return o.maxUs > kUfOverStartUs ? o.maxUs : kUfOverStartUs;
    const LONG need = o.n - o.n / 100;  // at least 99% of the samples at or below the bucket's upper edge
    LONG cum = 0, us = o.maxUs;
    for (LONG b = 0; b < kUfOverBuckets - 1; b++)
        if ((cum += o.hist[b]) >= need) { us = (b + 1) * kUfOverBucketUs; break; }
    return us > kUfOverFloorUs ? us : kUfOverFloorUs;
}
static LONG UfOverBudget(int ctx) {
    LONG b = -1;
    for (int e = 0; e < 3; e++)
        if (g_ufOver[ctx][e].n) {
            const LONG p = UfOverP99(g_ufOver[ctx][e]);
            if (p > b) b = p;
        }
    return b < 0 ? kUfOverStartUs : b;
}
// the effective cut point of a context (us), published in ufEffCutUs / ufOverP99Us
static LONG UfEffUs(int ctx) {
    Ts3nCounters& k = ts3native_counters;
    const LONG s = UfUs(), b = UfOverBudget(ctx);
    const LONG eff = s - b < s / 2 ? s / 2 : s - b;
    k.ufEffCutUs[ctx] = eff, k.ufOverP99Us[ctx] = b;
    return eff;
}

// worker, marked job: end - deadline (ticks, < 0 = in time) as the overrun sample (us), -1 = none
// HostLate=0 (default, switch ufhostlate): the whole end - deadline, as 0.36.189
static LONG UfLateUs(LONGLONG lateQ) {
    if (lateQ < 0) return -1;
    if (!ts3native_counters.ufHlMode) return (LONG)(lateQ * 1000000 / g_qpf.QuadPart);
    const LONGLONG hostQ = t_ufHostLateQ < lateQ ? t_ufHostLateQ : lateQ, workQ = lateQ - hostQ;
    if (hostQ > 0) InterlockedExchangeAdd(&ts3native_counters.ufHlUs, (LONG)(hostQ * 1000000 / g_qpf.QuadPart));
    return workQ > 0 ? (LONG)(workQ * 1000000 / g_qpf.QuadPart) : 0;
}

// SyncIc: the deadline of a marked sync IC (us), 0 = none of its own (nested in an open session: that one's deadline applies)
static LONG UfSyncUs(LONG pd) {
    const LONG uf = UfEffUs(0);
    if (t_pd.open) return 0;
    return pd > 0 && pd < uf ? pd : uf;
}

static void UfNote(LONG us) {
    Ts3nCounters& k = ts3native_counters;
    Max(&k.ufMaxUs, us);
    if (us >= UfUs()) InterlockedIncrement(&k.ufLong);  // strict: the setting, not 300 ms
}

// SyncIc, after the marked IC: t_pdHitLast from its session's PdClose; a cut plan that still succeeded is not "cut"
static void UfSyncEnd(DWORD handle, const u8* out, LONG us) {
    Ts3nCounters& k = ts3native_counters;
    InterlockedIncrement(&k.ufSync);
    const bool cut = (t_pdHitLast || t_ufLockFail) && *(const DWORD*)out != 1;  // RoutePlanResult type 1 = succeeded
    if (t_pdHitLast) UfOverNote(0, t_pdLastMask, t_pdLastLateUs);
    t_pdHitLast = t_ufLockFail = false;
    t_pdLastMask = 0;
    if (cut) {
        InterlockedIncrement(&k.ufSyncCut);
        g_ufCutH = handle;
    }
    UfNote(us);
}

// worker, after a marked job's IC (before J_DONE): IcCollect hands ufCut to the CUT? answer
// lateUs = end - deadline (< 0: ended in time), mask = the exits that cut it (bit 0 world A*, 1 cell, 2 water)
static void UfJobEnd(AsyncJob* j, bool capped, LONG us, LONG lateUs, unsigned mask) {
    Ts3nCounters& k = ts3native_counters;
    InterlockedIncrement(&k.ufJobs);
    UfOverNote(1, mask, lateUs);
    const bool cut = capped && *(const DWORD*)j->result != 1;
    j->ufCut = cut ? 1 : 0;
    if (cut) InterlockedIncrement(&k.ufJobCut);
    UfNote(us);
}

static void UfLogCounters() {
    const Ts3nCounters& c = ts3native_counters;
    Log("unseen fallback: mode %ld deadline %ld ms, cut at %ld ms sync / %ld ms job (overrun p99 %ld / %ld ms: world %ld/%ld "
        "cell %ld/%ld water %ld/%ld, %ld samples); marks %ld (refused %ld), sync %ld cut %ld (lock waits %ld, lock-wait fails %ld), "
        "jobs %ld cut %ld, cut answers %ld, longest %ld ms, >= setting %ld",
        c.ufMode, UfUs() / 1000, UfEffUs(0) / 1000, UfEffUs(1) / 1000, c.ufOverP99Us[0] / 1000, c.ufOverP99Us[1] / 1000,
        g_ufOver[0][0].n ? UfOverP99(g_ufOver[0][0]) / 1000 : -1, g_ufOver[1][0].n ? UfOverP99(g_ufOver[1][0]) / 1000 : -1,
        g_ufOver[0][1].n ? UfOverP99(g_ufOver[0][1]) / 1000 : -1, g_ufOver[1][1].n ? UfOverP99(g_ufOver[1][1]) / 1000 : -1,
        g_ufOver[0][2].n ? UfOverP99(g_ufOver[0][2]) / 1000 : -1, g_ufOver[1][2].n ? UfOverP99(g_ufOver[1][2]) / 1000 : -1,
        c.ufOverSamples, c.ufMarks, c.ufMarkRefused, c.ufSync, c.ufSyncCut, c.ufLockWaits, c.ufLockFail, c.ufJobs, c.ufJobCut,
        c.ufCutAsks, c.ufMaxUs / 1000, c.ufLong);
}

// docs/202 §2.1 camera queries for ts3fixes (rules 3-5 and the teleport goal; sim thread only: script calls). The camera is
// read the way [UnseenProbe] reads it (Camera_GetPosition 0x73D410, Camera_GetTarget 0x73D480, frustum 0x96B850, test
// 0x68D130; UnRefreshCamera), cached for 30 ms (a sim tick or two). Channel 0x83 with a code in optHi bits 16-23 and float bits
// in optLo: 3 / 4 / 5 = sphere centre x / y / z, 6 = radius, then the answer: 1 = unseen for sure (camera read, not wide, the
// sphere outside the frustum and its nearest point outside the reach rule max(80 m, 2 |cam y - target y|) of both camera
// position and target), 0 = seen or unknown. Wide camera (reach >= kUfWideReachM, i.e. the camera 120 m+ above its target)
// = everything seen. The game and older builds answer 0 (seen): nothing acts. EA's per-agent byte (agent+0xC8, 0x954240 ->
// 0x980E00, 3x model bounds) uses the same camera frustum; for a point the caller passes the radius (3 m for a Sim-sized goal,
// the bounding circle for a lot).
static float g_ufQ[4];
static DWORD g_ufQSet;
static void UfCamFresh() {
    LARGE_INTEGER now;
    QueryPerformanceCounter(&now);
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
    if (!g_unCam.ok || (now.QuadPart - g_unCam.at.QuadPart) * 1000 / g_qpf.QuadPart > 30) UnRefreshCamera();
}
static u8 UfSphereUnseen(const float* s) {
    UfCamFresh();
    if (!g_unCam.ok || g_unCam.reach >= kUfWideReachM) return 0;
    __declspec(align(16)) float sp[4] = {s[0], s[1], s[2], s[3]};
    bool on = true;
    __try {
        const void* fr = g_unFrustum();
        on = !fr || g_unInFrustum(fr, NULL, sp) != 0;
    } __except (EXCEPTION_EXECUTE_HANDLER) { on = true; }
    if (on) return 0;
    for (int k = 0; k < 2; k++) {
        const float* c = k ? g_unCam.tgt : g_unCam.pos;
        float d2 = 0;
        for (int i = 0; i < 3; i++) d2 += (s[i] - c[i]) * (s[i] - c[i]);
        const float reach = g_unCam.reach + s[3];
        if (d2 < reach * reach) return 0;
    }
    return 1;
}
static u8 UfcListMsg(int code, DWORD lo);
static u8 UfcC4LogMsg(DWORD lo);
static u8 UfwMsg(int code, DWORD lo);  // unseenfbc.cpp: codes 7-10, the unseen-Sim list for the C1-C4 sites (docs/202 §3.4)
// docs/202 §3.5 (1): the game's flood colours of the world graph R near a point, for D's same-component test (ts3fixes UfPre).
// R = world+0x54 (world = the RoutingWorld_Get slot 0x11D9128): w, h, chunk offsets +0x08, records +0x0C, size +0x10, colours
// built +0x14, per-chunk first colour index +0x18/+0x1C, one signed colour byte per waypoint +0x28/+0x2C [verified, docs/99
// §1.2/§1.4/§1.8, the reads of EoColour]; record +1 flags (bits 0-4 type), +3 / +4 offset in the chunk in 0.125 m; chunk
// c = cz * w + cx, 32 m, from the world origin [world+0x964]+0xB4 / +0xB8 [inferred from docs/99 §1.1 and steerro-test's
// world-origin reads: the first query logs R and the origin once, check it in game]. Colours ignore walls, doors, locks and
// portals (lot-side, not in R) and water links [verified, docs/142 §3.2/§3.4]: different colours = no land path; the same
// colour proves nothing about doors (ts3fixes adds the lot-side proof and refuses locked doors).
// A point's colour: every coloured waypoint (colour >= 0; type 0xE = -1 never floods) within kUfColR m in its chunk and the 8
// around it; -2 = none or no R, -3 = several colours (ambiguous: unknown). Codes after 3 (x) and 5 (z): 11 = store as A
// (1 = one colour), 12 = B has A's colour (1 = same), 13 = B's colour differs from A's (1 = proven different). Unknown = 0. (7-10 = unseenfbc.cpp list codes.)
static u8* const* g_ufWorldSlot = (u8* const*)(uintptr_t)0x011D9128;  // selftest: a fake slot
static const float kUfColR = 12.0f;
static int g_ufColA = -2;
static bool g_ufColLogged;
static int UfColourAt(float x, float z) {
    int found = -2;
    __try {
        const u8* world = *g_ufWorldSlot;
        if (!world) return -2;
        const u8* R = world + 0x54;
        const u8* wm = *(const u8* const*)(world + 0x964);
        const DWORD w = *(const DWORD*)R, h = *(const DWORD*)(R + 4);
        const DWORD* off = *(const DWORD* const*)(R + 8);
        const u8* rec = *(const u8* const*)(R + 0xC);
        const DWORD size = *(const DWORD*)(R + 0x10);
        const DWORD* first = *(const DWORD* const*)(R + 0x18);
        const DWORD nFirst = (DWORD)(*(const DWORD* const*)(R + 0x1C) - first);
        const signed char* col = *(const signed char* const*)(R + 0x28);
        const DWORD nCol = (DWORD)(*(const signed char* const*)(R + 0x2C) - col);
        if (!wm || !R[0x14] || !off || !rec || !first || !col || w - 1 >= 1024 || h - 1 >= 1024) return -2;
        const float ox = *(const float*)(wm + 0xB4), oz = *(const float*)(wm + 0xB8);
        if (!g_ufColLogged) {
            g_ufColLogged = true;
            Log("unseenfb colour: R %lux%lu, %lu waypoints coloured, origin (%.1f, %.1f)", w, h, nCol, ox, oz);
        }
        const int cx = (int)floorf((x - ox) / 32.0f), cz = (int)floorf((z - oz) / 32.0f);
        for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++) {
                const int qx = cx + dx, qz = cz + dz;
                if (qx < 0 || qz < 0 || qx >= (int)w || qz >= (int)h) continue;
                const DWORD c = (DWORD)qz * w + (DWORD)qx;
                if (off[c] + 2 > size || c >= nFirst) continue;
                const u8* ch = rec + off[c];
                const DWORD n = *(const unsigned short*)ch;
                for (DWORD i = 0; i < n; i++) {
                    const DWORD ro = ((const unsigned short*)(ch + 2))[i];
                    if (off[c] + ro + 5 > size) continue;
                    const u8* r = ch + ro;
                    const float px = ox + qx * 32.0f + r[3] * 0.125f - x, pz = oz + qz * 32.0f + r[4] * 0.125f - z;
                    if (px * px + pz * pz > kUfColR * kUfColR || first[c] + i >= nCol) continue;
                    const int k = col[first[c] + i];
                    if (k < 0) continue;
                    if (found == -2) found = k;
                    else if (found != k) return -3;
                }
            }
    } __except (EXCEPTION_EXECUTE_HANDLER) { return -2; }
    return found;
}
static u8 UfColourMsg(int code) {
    const bool xz = (g_ufQSet & 5) == 5;
    g_ufQSet = 0;
    const int k = xz ? UfColourAt(g_ufQ[0], g_ufQ[2]) : -2;
    if (code == 11) return (g_ufColA = k) >= 0 ? 1 : 0;
    if (g_ufColA < 0 || k < 0) return 0;
    return (code == 12) == (k == g_ufColA) ? 1 : 0;
}

static u8 GhMsg(int code, DWORD lo);  // ghost.cpp, docs/202 §3.9.5
static u8 UfCamMsg(DWORD lo, DWORD hi) {
    const int code = (int)(hi >> 16 & 0xFF);
    if (code >= 21 && code <= 24) return GhMsg(code, lo);  // docs/202 §3.9.5 [UnseenGhost]
    if (code >= 7 && code <= 10) return UfcListMsg(code, lo);
    if (code == 14) return UfcC4LogMsg(lo);  // docs/202 §5.6: the last Sim C4 answered "not free"
    if (code >= 15 && code <= 20) return UfwMsg(code, lo);  // docs/202 §3.4.2 FinishWalk (unseenfbc.cpp)
    float f;
    memcpy(&f, &lo, 4);
    if (code >= 3 && code <= 5) {
        g_ufQ[code - 3] = f;
        g_ufQSet |= 1u << (code - 3);
        return 1;
    }
    if (code >= 11 && code <= 13) return UfColourMsg(code);
    if (code != 6) return 0;
    const bool all = g_ufQSet == 7;
    g_ufQSet = 0;
    if (!all || !(f >= 0.0f && f < 1000.0f)) return 0;  // also NaN
    g_ufQ[3] = f;
    return UfSphereUnseen(g_ufQ);
}
