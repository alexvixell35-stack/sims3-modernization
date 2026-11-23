// docs/321 lane 0: fill of our own fixed-size tables (always on, measurement only, no behaviour). One row per table: its
// capacity now, the most rows it held at once (high water) and how often it was full (an add refused, a live row evicted,
// a fallback taken). Own export block `ts3native_caps` (docs/registry.md), not part of `ts3native_counters`. A "caps:" line
// goes to ts3native.log at most once a minute and only when a row changed; CapLogNow writes one at once (world quit, tests).
// Rows whose table only grows (never cleared in a session) report its current size as the high water.
enum {
    CAP_SLAVE,     // eafix.cpp SlaveUnlink pairs (H4; grows, see SlGrow)
    CAP_KD,        // plandeadline.cpp KindDeadlines rows cut within 30 s (H2; grows, see KdFind)
    CAP_ASSET,     // autshadow.cpp any AsSet (3072 ids): largest set; full = an add refused
    CAP_ASSIMIDS,  // autshadow.cpp simIds (M1, never cleared)
    CAP_ASOL,      // autshadow.cpp object-lot map rows (M2; cap = 3/4 of the AS_OL_CEIL table, full = map full)
    CAP_ASTIER,    // autshadow.cpp g_asTierTab Sim tiers (M3, never cleared)
    CAP_ASLOTTIER, // autshadow.cpp g_asLotTier lot tiers (M4, never cleared)
    CAP_UTB,       // unseentable.cpp rows per generation (M6); full = the old rule answered
    CAP_UTBWALK,   // unseentable.cpp agents walked per generation (M6)
    CAP_JOBS,      // ts3native.cpp route jobs queued or running (M7); full = asyncQueueFull
    CAP_RD,        // replandedupe.cpp: full = a row younger than 1 s evicted (M8; 4-way, no fill count)
    CAP_SPDOOR,    // snap.cpp door lots per session (M9)
    CAP_SPMB,      // snap.cpp P3 image MB vs CapMB (M10); full = P3 switched off
    CAP_DMX,       // hostcalls.cpp DoorMemoX ring: full = a row still inside its life overwritten (M11)
    CAP_DMXHOLD,   // hostcalls.cpp held lots (M11); full = every lot held
    CAP_AA,        // unseenanim.cpp direct-mapped actor slots: full = a collision hold (L4; no fill count)
    CAP_GVU,       // unseenfb.cpp deferred unseen routes (L5); full = a live streak evicted
    CAP_DMXROWS,   // hostcalls.cpp DoorMemoX live rows at a put (M11 lane C, of the 4096 ceiling); full = CAP_DMX
    CAP_AAPROBE,   // unseenanim.cpp distinct actors whose home slot another actor owed in (L4 lane C); full = chain exhausted
    CAP_N
};
static const char* const kCapName[CAP_N] = {"slave", "kd", "asSet", "asSimIds", "asOl", "asTier", "asLotTier", "utb", "utbWalk",
                                            "jobs", "rd", "spDoor", "spMB", "dmx", "dmxHold", "aa", "gvu", "dmxLive", "aaDisp"};
struct CapRow { volatile LONG cap, hw, full; };
struct Ts3nCaps { LONG version, n; CapRow r[CAP_N]; };
extern "C" __declspec(dllexport) Ts3nCaps ts3native_caps = {1, CAP_N, {}};
static volatile LONG g_capDirty;
static volatile LONG64 g_capNextLog;

static void CapLogNow(const char* why) {
    char b[1024];
    int n = _snprintf_s(b, sizeof(b), _TRUNCATE, "caps (%s, docs/321 lane 0: name hw/cap full):", why);
    for (int i = 0; i < CAP_N && n > 0; i++) {
        const CapRow& r = ts3native_caps.r[i];
        const int w = _snprintf_s(b + n, sizeof(b) - n, _TRUNCATE, " %s %ld/%ld %ld", kCapName[i], r.hw, r.cap, r.full);
        if (w < 0) break;
        n += w;
    }
    InterlockedExchange(&g_capDirty, 0);
    Log("%s", b);
}
// any thread, cheap: logs only when something changed and a minute passed since the last line
static void CapLogTick() {
    if (!g_capDirty) return;
    const LONG64 now = (LONG64)GetTickCount64(), next = g_capNextLog;
    if (now >= next && InterlockedCompareExchange64(&g_capNextLog, now + 60000, next) == next) CapLogNow("minute");
}
// `used` rows out of `cap` now; keeps the high water (no lock: a lost race loses one sample, never a wrong row)
static void CapSee(int id, LONG used, LONG cap) {
    CapRow& r = ts3native_caps.r[id];
    if (r.cap != cap) r.cap = cap, g_capDirty = 1;
    for (LONG h = r.hw; used > h; h = r.hw)
        if (InterlockedCompareExchange(&r.hw, used, h) == h) { g_capDirty = 1; break; }
}
static void CapFull(int id) {
    InterlockedIncrement(&ts3native_caps.r[id].full);
    g_capDirty = 1;
    CapLogTick();
}
