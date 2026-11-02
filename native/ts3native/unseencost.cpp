// [UnseenCost] (docs/227, counters v148 uc*, Rodney 2026-11-02): measurement probe, changes no behaviour. Per native unseen-check
// site (unseen.cpp UnObjClass / UtWhy / UtViewerWhy / UnRefreshCamera, unseenfbc.cpp UfcAgentUnseen / UfcTaskSimUnseen /
// UfcListed, unseenanim.cpp AaNearVisible) it counts calls, top-level calls (not nested in another timed site) and times
// 1 call in 16 per site with QPC, so docs/227's rule can decide whether one central per-tick unseen table pays.
// The probe's own cost is measured (measurement-tool cost rule): every 4096th timed call runs 64 wrapped no-op calls through
// the same wrapper (+ 64 distinct-table notes) and one bare QPC pair; uc-report subtracts the QPC floor from each sample and
// prints the probe's own ms/s. Included by ts3native.cpp before unseen.cpp. Off = one branch per site.
enum { UC_CLASS, UC_LIST, UC_VIEWER, UC_AGENT, UC_TASKSIM, UC_LISTED, UC_NEARVIS, UC_CAMERA, UC_N };
static bool g_ucOn;
static bool g_ucCal;  // the calibration runs (no nested calibration)
static __declspec(thread) int t_ucDepth;
// [UC_N] = the calibration's own site; published to the counters on each sample (the last <= 15 calls of a site lag)
static struct { LONG n[UC_N + 1], top[UC_N + 1], sampled[UC_N + 1]; LONG64 ticks[UC_N + 1]; LONG all; } g_uc;
static LONG64 g_ucSelfTicks, g_ucFloorTicks;
static void UcCalibrate();

static LONG UcScaled(LONG64 ticks, LONG64 per) {
    const LONG64 v = g_qpf.QuadPart ? ticks * per / g_qpf.QuadPart : 0;
    return v > 0x7FFFFFFF ? 0x7FFFFFFF : (LONG)v;
}

template <class F> static auto UcTime(int site, F body) -> decltype(body()) {
    const LONG n = InterlockedIncrement(&g_uc.n[site]);
    if (!t_ucDepth) InterlockedIncrement(&g_uc.top[site]);
    const LONG all = InterlockedIncrement(&g_uc.all);  // also during the calibration: it times this same path
    if (!g_ucCal && (all & 4095) == 0) UcCalibrate();
    t_ucDepth++;
    if (n & 15) {
        auto r = body();
        t_ucDepth--;
        return r;
    }
    LARGE_INTEGER t0, t1;
    QueryPerformanceCounter(&t0);
    auto r = body();
    QueryPerformanceCounter(&t1);
    t_ucDepth--;
    const LONG s = InterlockedIncrement(&g_uc.sampled[site]);
    const LONG64 ticks = InterlockedExchangeAdd64(&g_uc.ticks[site], t1.QuadPart - t0.QuadPart) + (t1.QuadPart - t0.QuadPart);
    if (site < UC_N) {
        Ts3nCounters& k = ts3native_counters;
        k.ucN[site] = g_uc.n[site], k.ucTop[site] = g_uc.top[site], k.ucSampled[site] = s, k.ucUs[site] = UcScaled(ticks, 1000000);
    }
    return r;
}

// Distinct objects asked per locomotion pass (UnObjClass on the sim thread): asks / distinct = what a per-pass table saves
struct UcSeen { const void* obj[2048]; LONG tag[2048]; LONG asks, distinct, passes, gen; };
static UcSeen g_ucSeen, g_ucSeenCal;
static void UcNote(UcSeen& t, const void* obj, LONG pass) {
    const LONG tag = pass + 1;  // 0 = empty slot
    t.asks++;
    if (tag != t.gen) t.gen = tag, t.passes++;
    for (DWORD i = (DWORD)((uintptr_t)obj >> 4) & 2047, j = 0; j < 8; j++, i = (i + 1) & 2047) {
        if (t.tag[i] == tag && t.obj[i] == obj) return;
        if (t.tag[i] != tag) { t.tag[i] = tag, t.obj[i] = obj, t.distinct++; return; }
    }
    t.distinct++;  // shortcut: 8 probed slots all taken this pass = counted new (over-counts past ~1500 objects per pass)
}
static void UcNoteMain(const void* obj, LONG pass) {
    Ts3nCounters& k = ts3native_counters;
    UcNote(g_ucSeen, obj, pass);
    k.ucAsks = g_ucSeen.asks, k.ucDistinct = g_ucSeen.distinct, k.ucPasses = g_ucSeen.passes;
}

static int UcNop() { return 0; }
static void UcCalibrate() {
    Ts3nCounters& k = ts3native_counters;
    static LONG calPass;
    calPass++;
    g_ucCal = true;
    LARGE_INTEGER a, b, c;
    QueryPerformanceCounter(&a);
    for (int i = 0; i < 64; i++) {
        UcNote(g_ucSeenCal, (const void*)(uintptr_t)(0x1000 + 16 * i), calPass);
        UcTime(UC_N, UcNop);
    }
    QueryPerformanceCounter(&b);
    QueryPerformanceCounter(&c);
    g_ucCal = false;
    g_ucSelfTicks += b.QuadPart - a.QuadPart, g_ucFloorTicks += c.QuadPart - b.QuadPart;
    k.ucSelfN += 64, k.ucSelfNs = UcScaled(g_ucSelfTicks, 1000000000);
    k.ucFloorN++, k.ucFloorNs = UcScaled(g_ucFloorTicks, 1000000000);
}

// [UnseenCost] Enabled=1: count and sample; read back as ucReady 1 (no --ab switch: a probe, not a fix)
static bool ApplyUnseenCost(const wchar_t* ini) {
    if (!GetPrivateProfileIntW(L"UnseenCost", L"Enabled", 0, ini)) return false;
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
    g_ucOn = true;
    ts3native_counters.ucReady = 1;
    Log("OK   unseen cost probe: native unseen checks counted, 1 in 16 timed (docs/227, counters v148 uc*)");
    return true;
}
