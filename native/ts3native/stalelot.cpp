// [RouteSnap] StaleLot (docs/189 phase 2, docs/94 §24.16; counters v125). Included by ts3native.cpp after p5c.cpp.
// P5c refuses a clone answer when ANY recorded routing write landed since the job's cut (g_spWrites, global). StaleLot judges
// the same window per lot: a job is stale only if a write since its cut is world-level (R, grid, blocked-node map, tiles,
// water, footprint objects / polygon caches, lot table, epoch and lot events, lot 0), unknown (an increment outside the ring,
// or one still being written), or lot-keyed on a lot in the job's read set.
//   - write side (snap.cpp SpWrite / StlClassify): every SpNote increment stores (seq, lot key or kind) in a 1024-slot ring;
//   - read set: the lot keys the clone run passed to LotTable_Find 0x627E00 (the one key lookup under LotTable_Contains
//     0x628A90 and LotTable_GetOrInsert 0x62EAB0: its 4 callers are those two (3 sites) and LotGraph_Reset [verified, raw call
//     scan of the Steam 1.67 .text]), recorded on the route worker inside P5Plan's clone run only, plus the active lot always.
//     NOT PROVEN COMPLETE: reads that reach a lot without a key lookup (lot graph pointers held by portals or R hubs, Q pointers
//     reached through the footprint map world+0x95C, inline table walks) are not seen. Hence the shadow arm and the tripwire;
//   - arms (runtime switch slMode, --ab routesnapstalelot, read once per plan): 2 shadow = both verdicts counted, the global
//     rule acts; 1 acting = a job the global rule refuses but no world-level / unknown / read-lot write touched is taken;
//     before the run (P5cStaleEarly) acting refuses only on world-level / unknown writes, lot-keyed ones wait for the read set;
//   - tripwire: a verify job (VerifyEvery) that is per-lot-only and whose clone answer differs from the game's (M2/M3) sets
//     slActOff: acting runs as shadow for the rest of the session (log line). Verify jobs are never taken, so they are the
//     evidence: run shadow + VerifyEvery=1 before acting.
// Hook-less lot changes (ReplaySigMs window, docs/94 §22.26.4) raise no write under either rule: unchanged exposure.
// 0.36.62 (docs/189 §9, counters v129): [RouteSnap] StaleLotFp. Runtime fplMode (--ab routesnapstalelotfp, read once per plan
// with the arm): 1 = the per-lot verdict skips covered ring entries (snap.cpp SLW_COV: W6 polygon caches, FPCORE / PRIVATE /
// FPREMOVE, grid removes that find no record), whose routing effects are their nested leaves' own entries. Both per-lot
// verdicts are counted whenever the ring is on (fplMat); an fp-only verify mismatch turns fpl off for the session.

static const DWORD STL_FIND = TS3A(0x00627E00);
static const u8 kStlFindPro[] = {0x83, 0xEC, 0x08, 0x53, 0x56};  // sub esp,8; push ebx; push esi (no relative operand; no branch
                                                                 // into 0x627E01..04: raw rel8/rel32/jcc scan of .text, 0 hits)
static void* g_stlFindTramp;
static const int kStlKeys = 32;
static __declspec(thread) bool t_stlRun;   // worker: inside P5Plan's clone run with StaleLot on
static __declspec(thread) bool t_stlOver;  // more than kStlKeys distinct lots
static __declspec(thread) int t_stlN;
static __declspec(thread) u64 t_stlKeys[kStlKeys];
static volatile LONG g_stlLog;

// LotTable_Find entry (thiscall table; [esp+4] iterator out, [esp+8] -> u64 key). slot = the return-address slot
static void __cdecl StlFindEnter(const DWORD* slot) {
    if (!t_stlRun) return;
    u64 key;
    __try { key = *(const u64*)(uintptr_t)slot[2]; } __except (EXCEPTION_EXECUTE_HANDLER) { t_stlOver = true; InterlockedIncrement(&ts3native_counters.stlFaults); return; }
    InterlockedIncrement(&ts3native_counters.stlFinds);
    for (int i = 0; i < t_stlN; i++) if (t_stlKeys[i] == key) return;
    if (t_stlN < kStlKeys) t_stlKeys[t_stlN++] = key; else t_stlOver = true;
}
static __declspec(naked) void StlFindStub() {
    __asm pushad __asm pushfd __asm lea eax, [esp + 36] __asm push eax __asm call StlFindEnter __asm add esp, 4
    __asm popfd __asm popad __asm jmp dword ptr [g_stlFindTramp]
}

static __declspec(thread) bool t_fpl;  // this plan judges with StaleLotFp (set with the arm)
static int StlModeNow() {  // the arm for this plan: 0 off, 1 acting, 2 shadow (acting after the tripwire = shadow)
    const Ts3nCounters& k = ts3native_counters;
    const LONG m = k.stlMode;
    t_fpl = k.fplMode == 1 && (k.fplReady & 1) && !k.fplOff;
    if (!(k.stlReady & 1) || (m != 1 && m != 2)) return 0;
    return m == 1 && k.stlActOff ? 2 : m;
}
static bool g_luOn;  // lotuse.cpp [RouteSnap] LotUseReadout: the read set is recorded for it too
static void StlRunBegin(int slm) { t_stlN = 0, t_stlOver = false, t_stlRun = slm != 0 || g_luOn; }
static void StlRunEnd() { t_stlRun = false; }

// the per-lot verdict of window (w0, now]: true = stale; why = slWhy index, wk = the world-level kind (why 1)
static bool StlStaleOf(LONG w0, LONG now, const u64* keys, int n, bool over, bool have, u64 active, int* why, int* wk, bool fp = false) {
    *why = -1, *wk = 0;
    if (now == w0) return false;
    const DWORD cnt = (DWORD)(now - w0);
    if (cnt > (DWORD)kStlRing) { *why = 3; return true; }  // also a count that went backwards (huge unsigned)
    bool hit = false;
    for (DWORD i = 1; i <= cnt; i++) {
        const LONG s = w0 + (LONG)i;
        const StlEnt& e = g_stlRing[s & (kStlRing - 1)];
        const LONG s1 = e.seq;
        _ReadWriteBarrier();
        const LONG kf = e.kind;
        const u64 key = ((u64)e.hi << 32) | e.lo;
        _ReadWriteBarrier();
        if (s1 != s || e.seq != s) { *why = 2; return true; }
        if (fp && (kf & SLW_COV)) continue;  // StaleLotFp: no routing input of its own (nested leaves have their entries)
        const LONG kind = kf & 0xFF;
        if (kind != SLW_LOT) { *why = 1, *wk = kind > 0 && kind < SLW_N ? kind : SLW_NOKEY; return true; }
        if (key == active) hit = true;
        for (int j = 0; !hit && j < n; j++) hit = keys[j] == key;
    }
    // n == 0 (no recorded find) is a legitimate empty read set (an open-world plan touches no lot), not a detour miss: the
    // pointer paths past LotTable_Find are the same gap for every n and are what the StlVerify tripwire (stlVer_1) and the
    // shadow's stlReadHist_0 share watch (integration review 0.36.63: acting only after both are read)
    if (!have) { *why = 5; return true; }
    if (over) { *why = 4; return true; }
    if (hit) { *why = 0; return true; }
    return false;
}
// before the run: true = acting defers the P5c early refusal (only lot-keyed writes since the cut, judged after the run)
static bool StlActDefer(int slm, LONG w0) {
    if (slm != 1 || !g_stlFindTramp) return false;
    int why, wk;
    const bool st = StlStaleOf(w0, g_spWrites, NULL, 0, false, true, 0, &why, &wk, t_fpl);
    return !st;  // with an empty read set only world-level / unknown / overflow writes are stale
}
// worker, P5Plan after the clone run (non-verify: at the P5c test; verify: when the game's plan has run). Counts both
// verdicts; returns the per-lot verdict (true = stale)
static bool StlJob(int slm, LONG w0, bool* globOut, bool* fpOnly) {
    Ts3nCounters& k = ts3native_counters;
    const LONG now = g_spWrites;
    if (globOut) *globOut = now != w0;
    u64 active = 0;
    __try { active = PcActiveLot(); } __except (EXCEPTION_EXECUTE_HANDLER) { InterlockedIncrement(&k.stlFaults); if (globOut) *globOut = true; return true; }
    int why, wk, why2, wk2;
    const bool lot0 = StlStaleOf(w0, now, t_stlKeys, t_stlN, t_stlOver, g_stlFindTramp != NULL, active, &why, &wk);
    const bool lotF = StlStaleOf(w0, now, t_stlKeys, t_stlN, t_stlOver, g_stlFindTramp != NULL, active, &why2, &wk2, true);
    if (k.fplReady & 1) InterlockedIncrement(&k.fplMat[!lot0 ? (lotF ? 3 : 0) : lotF ? 2 : 1]);
    if (fpOnly) *fpOnly = lot0 && !lotF;
    if (t_fpl) why = why2, wk = wk2;
    const bool lot = t_fpl ? lotF : lot0;
    const bool glob = now != w0;
    const int arm = slm == 1 ? 1 : 0;
    InterlockedIncrement(&k.stlJobs);
    InterlockedIncrement(&k.stlMat[arm][!glob ? (lot ? 3 : 0) : lot ? 2 : 1]);
    if (why >= 0) InterlockedIncrement(&k.stlWhy[why]);
    if (why == 1) InterlockedIncrement(&k.stlWorld[wk]);
    const int n = t_stlN;
    InterlockedIncrement(&k.stlReadHist[t_stlOver ? 7 : n == 0 ? 0 : n == 1 ? 1 : n == 2 ? 2 : n <= 4 ? 3 : n <= 8 ? 4 : n <= 16 ? 5 : 6]);
    return lot;
}
// verify job: eq 0 = the clone answer equals the game's (M0 / M1), 1 = differs (M2 / M3), 2 = not comparable
static void StlVerify(int slm, LONG w0, int eq) {
    Ts3nCounters& k = ts3native_counters;
    bool glob, fpOnly;
    const bool lot = StlJob(slm, w0, &glob, &fpOnly);
    if (glob && fpOnly && (k.fplReady & 1)) {  // StaleLotFp evidence: only covered entries made the phase-2 verdict stale
        InterlockedIncrement(&k.fplVer[eq]);
        if (eq == 1 && !InterlockedExchange(&k.fplOff, 1))
            Log("RouteSnap StaleLotFp: an fp-only verify job's clone answer differs from the game's: fp keys off for the session "
                "(a covered entry changed routing input, docs/189 §9)");
    }
    if (!glob || lot) return;  // not per-lot-only
    InterlockedIncrement(&k.stlVer[eq]);
    if (eq == 1 && !InterlockedExchange(&k.stlActOff, 1))
        Log("RouteSnap StaleLot: a per-lot-only verify job's clone answer differs from the game's: acting off for the session "
            "(read set incomplete, docs/189 phase 2)");
}

static void ApplySl(const wchar_t* ini) {
    Ts3nCounters& k = ts3native_counters;
    const int m = GetPrivateProfileIntW(L"RouteSnap", L"StaleLot", 0, ini);
    if (m != 1 && m != 2) return;
    g_stlOn = 1;
    k.stlReady |= 1;
    g_stlFindTramp = InstallDetour("LotTable_Find (StaleLot read set)", STL_FIND, kStlFindPro, sizeof(kStlFindPro), (void*)StlFindStub, &g_stlFindTramp);
    if (g_stlFindTramp) k.stlReady |= 2;
    k.stlMode = m;
    const int fm = GetPrivateProfileIntW(L"RouteSnap", L"StaleLotFp", 0, ini);
    if (fm == 1) {
        g_fplOn = 1, k.fplReady |= 1, k.fplMode = 1;
        Log("OK   route snapshot StaleLotFp=1: W6 / footprint-object leaves and no-op grid removes judged by their nested leaves");
    }
    Log("%s route snapshot StaleLot=%d (%s): per-lot staleness, read set = LotTable_Find keys of the clone run + the active lot%s",
        g_stlFindTramp ? "OK  " : "WARN", m, m == 1 ? "acting" : "shadow",
        g_stlFindTramp ? "" : "; detour missing: every written window is stale (why 5)");
}
