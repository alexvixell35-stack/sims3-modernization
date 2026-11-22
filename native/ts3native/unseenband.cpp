// [UnseenProbe] Bands (docs/322, docs/320 §5.4 step 1): measurement only. Splits the sim-thread time the existing probes already
// time by the distance of the Sim from its nearest viewer, so a later cap / skip / park change can target the band that costs.
// Included by ts3native.cpp right after unseen.cpp (camera g_unCam, viewer table g_utView, household table AnxHousehold).
//
// Distance = the FarFromViewers points (ts3fixes Fixes.cs FarCompute): the camera target, the camera position and every
// active-household Sim in the fresh viewer table (ts3fixes UnseenViewers.cs pushes them every 5 ticks); the nearest one counts.
// Bands = [UnseenProbe] Bands, ascending edges in metres (up to 6, e.g. 50,150,400,1000 = 5 bands); empty = off. Nothing here
// is tied to the world's size: the edges come from the ini, every distance is computed at use from this tick's points.
// Rows: 0..edges = NPC bands, UB_HH = a household Sim (the selected Sim is one of them) or a Sim of unknown scope (no fresh
// household table), UB_UNK = an NPC with no position (no transform, not found by id, no camera and no viewers).
// Sites (each is a sub-part of a site the existing probes time; sites 1-2 run inside site 0's agent updates):
//   0 locomotion agent update of a Sim (unseen.cpp UnAgentBody, Locomotion_UpdateAgent 0x957210 whole call)
//   1 keyed +0x918 waits inside that update (UnWait; keys need [RouteGate] Sites=1)
//   2 UpdateSimFootprint 0x833560 of a Sim (UnUsfHook; inside or outside an agent update)
//   3 IC_Route_Plan with a Sim follower (UnPlan; needs [RouteAsync])
//   4 rig pose 0x940430 of a Sim (unseenanim.cpp AnxPose; needs [AnimPhase] Enabled=1 Split=1)
//   5 the Sim's own script task run (taskseam.cpp TskRunHook, ScriptProxy target Sim: Sim.Simulate = interactions, routing
//     calls from scripts, idle; needs [Diag] TaskSeam=1)
//   6 AutonomyManager task runs (the one global autonomy search per tick, go-live included), attributed to the actor of the
//     search ts3fixes names at Autonomy.ScoreInteractionsOnObjects entry (viewer channel codes 27 / 28); needs TaskSeam
// Never changes what runs: every hook calls the game with the same arguments; this file only reads positions and adds.
// Sim thread only (every caller runs there), so the sums need no interlock; the agent reads them from the export.

// UB_* rows / sites and g_ubOn: unseen.cpp (its hooks use them)
struct UnseenBandCounters {
    LONG version;            // +0x00, 1
    LONG ready;              // +0x04, bit 0 bands in force, 1 locomotion ([UnseenProbe]), 2 footprint, 3 plans, 4 anim pose
                             //        ([AnimPhase] Split), 5 task runs ([Diag] TaskSeam), 6 an autonomy actor was named this session
    LONG nEdges;             // +0x08, edges in force (bands = nEdges + 1)
    LONG edgeM[UB_EDGES];    // +0x0C, the edges, metres
    LONG n[UB_SITES][UB_ROWS];   // +0x24, calls per site and row
    LONG us[UB_SITES][UB_ROWS];  // +0x120, their time, us (from 64-bit QPC sums: no floor loss)
    LONG simUpd[UB_ROWS];    // +0x21C, Sim agent updates per row (/ unPasses = Sims per band per locomotion pass)
    LONG autNamed;           // +0x240, autonomy actors named by ts3fixes (codes 27 + 28)
    LONG autNone;            // +0x244, AutonomyManager runs with no actor named yet (row UB_UNK)
    LONG camOnly;            // +0x248, band asks with no fresh viewer table (camera points only)
    LONG spare[5];           // +0x24C
};
static_assert(sizeof(UnseenBandCounters) == 0x260, "docs/registry.md ts3native_unseenband layout");
extern "C" __declspec(dllexport) UnseenBandCounters ts3native_unseenband = {1};
static float g_ubEdge2[UB_EDGES];
static LONGLONG g_ubTk[UB_SITES][UB_ROWS];
static DWORD g_ubAutLo, g_ubAutHi, g_ubAutLoIn;
static bool g_ubAutSet;

// "50,150,400,1000" -> edges; false (nothing set) unless 1-6 ascending positive numbers
static bool UbParse(const wchar_t* s, LONG* out, LONG* n) {
    LONG v[UB_EDGES], k = 0;
    while (*s) {
        while (*s == L' ' || *s == L',') s++;
        if (!*s) break;
        if (*s < L'0' || *s > L'9' || k == UB_EDGES) return false;
        LONG x = 0;
        while (*s >= L'0' && *s <= L'9') { x = x * 10 + (*s++ - L'0'); if (x > 1000000) return false; }
        if (x <= 0 || (k && x <= v[k - 1])) return false;
        v[k++] = x;
        if (*s && *s != L',' && *s != L' ') return false;
    }
    if (!k) return false;
    memcpy(out, v, k * sizeof(LONG));
    *n = k;
    return true;
}

static float UbD2(const float* a, const float* b) {
    float d = 0;
    for (int i = 0; i < 3; i++) d += (a[i] - b[i]) * (a[i] - b[i]);
    return d;
}
// NPC band of a world position: nearest of camera target, camera position and the fresh household viewers
static int UbRow(const float* p) {
    UnseenBandCounters& b = ts3native_unseenband;
    float d2 = -1;
    if (g_unCam.ok) {
        d2 = UbD2(p, g_unCam.tgt);
        const float e = UbD2(p, g_unCam.pos);
        if (e < d2) d2 = e;
    }
    if (g_utView.at && GetTickCount() - g_utView.at <= UT_VIEWER_AGE_MS) {
        for (int i = 0; i < g_utView.n; i++) {
            const float e = UbD2(p, g_utView.pos[i]);
            if (d2 < 0 || e < d2) d2 = e;
        }
    } else {
        b.camOnly++;
    }
    if (d2 < 0) return UB_UNK;
    for (int i = 0; i < b.nEdges; i++)
        if (d2 < g_ubEdge2[i]) return i;
    return b.nEdges;
}
static void UbAdd(int site, int row, LONGLONG ticks) {
    UnseenBandCounters& b = ts3native_unseenband;
    b.n[site][row]++;
    g_ubTk[site][row] += ticks;
    b.us[site][row] = (LONG)(g_ubTk[site][row] * 1000000 / g_qpf.QuadPart);
}
static void UbAddUs(int site, int row, LONG us) { UbAdd(site, row, (LONGLONG)us * g_qpf.QuadPart / 1000000); }
// household id test as taskseam.cpp TskHouseholdId (the held table ts3fixes PushHeld sends); no fresh table = household
static bool UbHouseholdId(DWORD lo, DWORD hi) {
    if (!g_utHeld.n || GetTickCount() - g_utHeld.at > 2000) return true;
    for (int i = 0; i < g_utHeld.n; i++) if (g_utHeld.lo[i] == lo && g_utHeld.hi[i] == hi) return true;
    return false;
}
static void UbSimUpd(int row) { ts3native_unseenband.simUpd[row]++; }
// row of a game object (Sims only; -1 = not a Sim, not counted). Caller on the sim thread.
static int UbObjRow(void* obj) {
    if (!obj) return UB_UNK;
    int kind = 1;
    float pos[3];
    const int c = UnObjClass(obj, &kind, pos);
    if (c < 0) return UB_UNK;
    if (kind != 0) return -1;
    return AnxHousehold(obj) ? UB_HH : UbRow(pos);
}

// taskseam.cpp TskRunHook: role 1 = a Sim's ScriptProxy (key = its ObjectId), 2 = AutonomyManager (the named actor)
static void UbTaskRun(int role, DWORD lo, DWORD hi, LONGLONG ticks) {
    int row = UB_UNK;
    if (role == 2) {
        if (!g_ubAutSet) ts3native_unseenband.autNone++;
        else lo = g_ubAutLo, hi = g_ubAutHi;
    }
    if (role == 1 || g_ubAutSet) {
        if (UbHouseholdId(lo, hi)) row = UB_HH;
        else {
            __try {
                void* mgr = *g_unObjMgr;
                void* obj = mgr && (lo | hi) ? g_unFind(mgr, NULL, lo, hi) : NULL;
                const int r = obj ? UbObjRow(obj) : UB_UNK;
                row = r < 0 ? UB_UNK : r;
            } __except (EXCEPTION_EXECUTE_HANDLER) { row = UB_UNK; }
        }
    }
    UbAdd(role == 2 ? UB_AUT : UB_TASK, row, ticks);
}

// viewer channel codes 27 (actor id low) / 28 (high, commits): 0 = refused (bands off or no task seam): ts3fixes stops this load
static u8 UbAutMsg(DWORD code, DWORD lo) {
    if (!g_ubOn || !ts3native_counters.tskOn) return 0;
    if (code == 27) { g_ubAutLoIn = lo; return 1; }
    g_ubAutLo = g_ubAutLoIn, g_ubAutHi = lo, g_ubAutSet = true;
    ts3native_unseenband.autNamed++;
    return 1;
}

static void UbLogCounters() {
    if (!g_ubOn) return;
    UnseenBandCounters& b = ts3native_unseenband;
    const Ts3nCounters& k = ts3native_counters;
    b.ready = 1 | (g_unOn ? 2 : 0) | (k.unReady & 2 ? 4 : 0) | (k.unReady & 4 ? 8 : 0) | (k.anxReady & 1 ? 16 : 0) | (k.tskOn ? 32 : 0) |
              (b.autNamed ? 64 : 0);
    static const char* const kSite[UB_SITES] = {"loco", "waits", "footprint", "plans", "pose", "simtask", "autonomy"};
    char line[1024];
    for (int s = 0; s < UB_SITES; s++) {
        int o = 0;
        for (int r = 0; r <= b.nEdges; r++) o += sprintf_s(line + o, sizeof(line) - o, " b%d %ld/%ld", r, b.n[s][r], b.us[s][r] / 1000);
        Log("unseen bands %s (n/ms per band, docs/322):%s hh %ld/%ld none %ld/%ld", kSite[s], line, b.n[s][UB_HH], b.us[s][UB_HH] / 1000,
            b.n[s][UB_UNK], b.us[s][UB_UNK] / 1000);
    }
    int o = 0;
    for (int r = 0; r <= b.nEdges; r++) o += sprintf_s(line + o, sizeof(line) - o, " b%d %ld", r, b.simUpd[r]);
    Log("unseen bands: ready 0x%lX, passes %ld, Sim agent updates%s hh %ld none %ld; autonomy actors named %ld, runs with none %ld, camera-only asks %ld",
        b.ready, k.unPasses, line, b.simUpd[UB_HH], b.simUpd[UB_UNK], b.autNamed, b.autNone, b.camOnly);
}

// [UnseenProbe] Bands, read by ApplyUnseenProbe once the probe is in (the band split needs its camera and class reads)
static void ApplyUnseenBands(const wchar_t* ini) {
    wchar_t s[128];
    GetPrivateProfileStringW(L"UnseenProbe", L"Bands", L"", s, _countof(s), ini);
    if (!s[0]) return;
    UnseenBandCounters& b = ts3native_unseenband;
    if (!UbParse(s, b.edgeM, &b.nEdges)) { Log("FAIL unseen bands: [UnseenProbe] Bands=%ls is not 1-6 ascending whole metres, off", s); return; }
    for (int i = 0; i < b.nEdges; i++) g_ubEdge2[i] = (float)b.edgeM[i] * (float)b.edgeM[i];
    g_ubOn = true;
    b.ready = 1;
    char e[96];
    int o = 0;
    for (int i = 0; i < b.nEdges; i++) o += sprintf_s(e + o, sizeof(e) - o, i ? ",%ld" : "%ld", b.edgeM[i]);
    Log("OK   unseen bands (docs/322): edges %s m (%ld bands + household + no position), nearest of camera target / position and household"
        " Sims; sites locomotion, waits, footprint, plans, pose ([AnimPhase] Split), Sim tasks + autonomy ([Diag] TaskSeam); a minute line each",
        e, b.nEdges + 1);
}
