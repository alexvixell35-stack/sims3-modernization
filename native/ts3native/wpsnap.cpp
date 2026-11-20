// docs/679: [WaypointSnap]. World_ArePointsConnectedByLand (IC 0x787240 -> 0x65DF90) is two closest-waypoint snaps
// (0x65BB90: up to three tries of 0x6595B0) and one compare of the two waypoints' land colour bytes. A snap that misses the
// game's own candidate cache gathers every waypoint of 1-4 chunks of 32 x 32 (9-16 on the second try, 25-36 on the
// third) in 0x654790, with a lot lookup for each lot waypoint, then scores and sorts them; managed loops call the IC once
// per lot or parking spot of the world (stall watch s149-s189: frames of 322-563 ms under this IC, hit in the gather).
//  - Memo (default off): the answer of one call is kept per thread under (the four coordinate words, the routing objects,
//    [RouteSnap]'s write sequence) for at most MemoMs. A later call with the same words in the same state gets the stored
//    answer; anything else runs the game's call. Mode 2 (shadow) runs the game's call as well and uses its answer.
//  - Probe (default off, observe only): calls, time, gathers and candidates per call, gathers by try, and one line per
//    burst of calls (one managed loop) that took LogMs or more.
// Sites, both E8 call sites (no entry detour: plancand.cpp's CandidateSplit owns the entries of 0x65DF90 / 0x65BB90):
//   0x787282 in the IC 0x787240: call 0x65DF90, thiscall (ECX the routing object), four stack words, ret 0x10, AL;
//   0x659787 in 0x6595B0: call 0x654790, ECX the candidate area, no stack argument, AL (Probe only; any thread).
// Threads: the IC runs on the script thread that calls it (the sim thread); the memo and the burst state are per thread and
// the counters interlocked, so a second thread only gets its own table. The gather stub also runs on the route worker
// inside plans (it counts into a separate counter there). No lock is taken or needed by this file.
#include "wpsnap-core.h"

struct WpSnapCounters {
    volatile LONG wsMode;         // +0x00 runtime switch: 0 off, 1 answer from the memo, 2 shadow
    volatile LONG wsProbe;        // +0x04 runtime switch: 1 = count and log bursts
    volatile LONG wsReady;        // +0x08 bit 0 the IC's call site, bit 1 the gather call site
    volatile LONG wsMemoMs;       // +0x0C a row's longest life
    volatile LONG wsLogMs;        // +0x10 burst line threshold
    volatile LONG wsCalls;        // +0x14 IC calls seen
    volatile LONG wsHits;         // +0x18 answered from the memo (mode 1) or found there (mode 2)
    volatile LONG wsStored;       // +0x1C rows written
    volatile LONG wsNoSeq;        // +0x20 not served / not stored: no write sequence, a write during the call, no routing object
    volatile LONG wsShadow;       // +0x24 shadow compares
    volatile LONG wsMismatch;     // +0x28 of them different (must stay 0)
    volatile LONG wsGathers;      // +0x2C gathers under the IC
    volatile LONG wsCands;        // +0x30 candidates they produced
    volatile LONG wsGatherTry[3]; // +0x34 gathers under the IC by try (0 = 1-4 chunks, 1 = 9-16, 2 = 25-36)
    volatile LONG wsGatherOther;  // +0x40 gathers outside the IC (plans, other callers; any thread)
    volatile LONG wsBursts;       // +0x44 burst lines written
    volatile LONG wsMaxUs;        // +0x48 longest single call
    volatile LONG wsPad;          // +0x4C
    volatile LONGLONG wsUs;       // +0x50 time in the IC's call (memo answers included)
    volatile LONGLONG wsGatherUs; // +0x58 of it in gathers
    volatile LONGLONG wsGatherOtherUs;  // +0x60 gather time outside the IC
};
extern "C" __declspec(dllexport) WpSnapCounters ts3native_wpsnap = {};

static const DWORD WPS_IC = TS3A(0x00787240), WPS_TRY = TS3A(0x006595B0);
enum : DWORD { WPS_IC_OFF = 0x42, WPS_TRY_OFF = 0x1D7, WPS_GAP_US = 3000 };

typedef bool(__fastcall* WpsSameFn)(void* self, void* edx, DWORD ax, DWORD az, DWORD bx, DWORD bz);
typedef bool(__fastcall* WpsGatherFn)(void* area);
static WpsSameFn g_wpsSameOrig;
static WpsGatherFn g_wpsGatherOrig;

struct WpsTh {
    int depth;  // inside the IC's call on this thread
    WpsBurst burst;
    WpsTab tab;
};
static __declspec(thread) WpsTh t_wps;

static LONGLONG WpsNowUs() {
    LARGE_INTEGER q;
    q.QuadPart = 0;
    QueryPerformanceCounter(&q);
    return g_qpf.QuadPart ? q.QuadPart * 1000000 / g_qpf.QuadPart : 0;
}

// The routing objects an answer was read from: the object itself, its waypoint graph R (+0x10), R's chunk table (+0x18),
// colour base (+0x28) and "components built" byte (+0x14). false = no graph (the game answers "no" without a snap).
static bool WpsCtx(void* self, unsigned int* ctx) {
    const DWORD* s = (const DWORD*)self;
    if (!s) return false;
    const DWORD r = s[4];
    if (!r) return false;
    const DWORD* rp = (const DWORD*)(uintptr_t)r;
    unsigned int h = (unsigned int)(uintptr_t)self * 0x9E3779B1u;
    h = (h ^ (unsigned int)r) * 0x85EBCA6Bu;
    h = (h ^ (unsigned int)rp[6]) * 0xC2B2AE35u;
    h = (h ^ (unsigned int)rp[10]) * 0x27D4EB2Fu;
    h ^= (unsigned int)(*(const u8*)(rp + 5)) << 7;
    *ctx = h;
    return true;
}

static void WpsBurstLine(const WpsBurst& d) {
    WpSnapCounters& c = ts3native_wpsnap;
    if (d.us < (LONGLONG)c.wsLogMs * 1000) return;
    if (InterlockedIncrement(&c.wsBursts) > 200) return;
    Log("waypoint snap burst: %lu ArePointsConnectedByLand calls in %ld ms (%lu from the memo), %lu gathers, %lu candidates (most in one gather %lu), thread %lu",
        (DWORD)d.calls, (LONG)(d.us / 1000), (DWORD)d.hits, (DWORD)d.gathers, (DWORD)d.cands, (DWORD)d.most, GetCurrentThreadId());
}

// 0x787282: the IC's call of 0x65DF90
static bool __fastcall WpsSameHook(void* self, void* edx, DWORD ax, DWORD az, DWORD bx, DWORD bz) {
    WpSnapCounters& c = ts3native_wpsnap;
    const LONG mode = c.wsMode, probe = c.wsProbe;
    if (!mode && !probe) return g_wpsSameOrig(self, edx, ax, az, bx, bz);
    WpsTh& t = t_wps;
    if (t.depth) return g_wpsSameOrig(self, edx, ax, az, bx, bz);  // a call from inside a call: the game's, untouched
    const LONGLONG t0 = WpsNowUs();
    WpsBurst done = {};
    if (probe && WpsBurstStart(&t.burst, t0, WPS_GAP_US, &done)) WpsBurstLine(done);
    InterlockedIncrement(&c.wsCalls);
    const WpsKey k = {{(unsigned int)ax, (unsigned int)az, (unsigned int)bx, (unsigned int)bz}};
    unsigned int ctx = 0;
    const bool keyed = mode && g_snOn && WpsCtx(self, &ctx);
    const unsigned int seq = (unsigned int)g_snWriteSeq, now = (unsigned int)GetTickCount();
    bool memo = false;
    const bool hit = keyed && WpsGet(&t.tab, k, ctx, seq, now, (unsigned int)c.wsMemoMs, &memo);
    bool ans = memo;
    if (hit) InterlockedIncrement(&c.wsHits);
    if (!hit || mode == 2) {
        t.depth = 1;
        ans = g_wpsSameOrig(self, edx, ax, az, bx, bz);
        t.depth = 0;
        if (hit) {
            InterlockedIncrement(&c.wsShadow);
            if (ans != memo && InterlockedIncrement(&c.wsMismatch) <= 10)
                Log("WAYPOINT SNAP MISMATCH: memo %d, game %d for (%08lX %08lX) (%08lX %08lX), write seq %lu", (int)memo, (int)ans, ax, az, bx, bz, (DWORD)seq);
        }
        // kept only when nothing wrote the routing data while the game computed it
        unsigned int ctx2 = 0;
        if (keyed && seq == (unsigned int)g_snWriteSeq && WpsCtx(self, &ctx2) && ctx2 == ctx) {
            WpsPut(&t.tab, k, ctx, seq, now, ans);
            if (!hit) InterlockedIncrement(&c.wsStored);
        } else if (mode) {
            InterlockedIncrement(&c.wsNoSeq);
        }
    }
    const LONGLONG t1 = WpsNowUs(), us = t1 - t0;
    InterlockedExchangeAdd64(&c.wsUs, us);
    if (us > c.wsMaxUs) c.wsMaxUs = (LONG)us;
    if (probe) WpsBurstEnd(&t.burst, t1, us, hit && mode == 1);
    return ans;
}

// 0x659787: 0x6595B0's call of the gather 0x654790 (ECX = the candidate area; +0x24 the try, +0x30 / +0x34 the candidates)
static bool __fastcall WpsGatherHook(void* area) {
    WpSnapCounters& c = ts3native_wpsnap;
    if (!c.wsProbe) return g_wpsGatherOrig(area);
    const LONGLONG t0 = WpsNowUs();
    const bool ok = g_wpsGatherOrig(area);
    const LONGLONG us = WpsNowUs() - t0;
    WpsTh& t = t_wps;
    if (!t.depth) {
        InterlockedIncrement(&c.wsGatherOther);
        InterlockedExchangeAdd64(&c.wsGatherOtherUs, us);
        return ok;
    }
    const DWORD* a = (const DWORD*)area;
    const DWORD n = (a[13] - a[12]) >> 5, tryN = a[9];
    InterlockedIncrement(&c.wsGathers);
    InterlockedExchangeAdd(&c.wsCands, (LONG)n);
    InterlockedExchangeAdd64(&c.wsGatherUs, us);
    if (tryN < 3) InterlockedIncrement(&c.wsGatherTry[tryN]);
    t.burst.gathers++;
    t.burst.cands += (unsigned int)n;
    if (n > t.burst.most) t.burst.most = (unsigned int)n;
    return ok;
}

static bool WpsCallAt(DWORD site, DWORD* target) {
    const u8* p = (const u8*)(uintptr_t)site;
    if (!Readable(p, 5) || p[0] != 0xE8) return false;
    *target = (DWORD)(site + 5 + *(const LONG*)(p + 1));
    return true;
}
static bool WpsRetarget(DWORD site, void* hook) {
    const LONG rel = (LONG)((uintptr_t)hook - (site + 5));
    return Write((u8*)(uintptr_t)site + 1, &rel, 4);
}

static void InstallWpSnap(const wchar_t* ini) {
    WpSnapCounters& c = ts3native_wpsnap;
    const int memo = Clamp(GetPrivateProfileIntW(L"WaypointSnap", L"Memo", 0, ini), 0, 2);
    const int probe = GetPrivateProfileIntW(L"WaypointSnap", L"Probe", 0, ini) ? 1 : 0;
    if (!memo && !probe) { Log("waypoint snap off ([WaypointSnap] Memo=0, Probe=0)"); return; }
    if (!WPS_IC || !WPS_TRY) { Log("SKIP waypoint snap: no address for this build, nothing written"); return; }
    static bool s_once;  // a second pass would read our own stubs as the originals: they would call themselves (docs/679 §8)
    if (s_once) { Log("SKIP waypoint snap: installed already, a second install call, nothing written"); return; }
    s_once = true;
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
    c.wsMemoMs = Clamp(GetPrivateProfileIntW(L"WaypointSnap", L"MemoMs", 2000, ini), 1, 60000);
    c.wsLogMs = Clamp(GetPrivateProfileIntW(L"WaypointSnap", L"LogMs", 20, ini), 1, 10000);
    // the IC's call: an E8 followed by pop esi; ret, whose target begins with 0x65DF90's sub esp, 0x10 (83 EC 10) or with
    // an E9 of an entry detour that is already there
    const DWORD site = WPS_IC + WPS_IC_OFF;
    DWORD target = 0;
    const u8* tail = (const u8*)(uintptr_t)(site + 5);
    if (!WpsCallAt(site, &target) || tail[0] != 0x5E || tail[1] != 0xC3 || !Readable((const void*)(uintptr_t)target, 5)) {
        Log("SKIP waypoint snap: 0x%08lX is not the IC's call of the same-land test, nothing written", site);
    } else {
        g_wpsSameOrig = (WpsSameFn)(uintptr_t)target;
        if (WpsRetarget(site, (void*)WpsSameHook)) c.wsReady |= 1;
        else Log("FAIL waypoint snap: VirtualProtect at 0x%08lX (err %lu)", site, GetLastError());
    }
    // the gather call: mov ecx, esi; call; test al, al
    const DWORD gs = WPS_TRY + WPS_TRY_OFF;
    const u8* g = (const u8*)(uintptr_t)(gs - 2);
    DWORD gt = 0;
    if (probe && (c.wsReady & 1)) {
        if (!Readable(g, 9) || g[0] != 0x8B || g[1] != 0xCE || !WpsCallAt(gs, &gt) || g[7] != 0x84 || g[8] != 0xC0) {
            Log("SKIP waypoint snap gather count: 0x%08lX is not the try's call of the gather, nothing written there", gs);
        } else {
            g_wpsGatherOrig = (WpsGatherFn)(uintptr_t)gt;
            if (WpsRetarget(gs, (void*)WpsGatherHook)) c.wsReady |= 2;
        }
    }
    c.wsProbe = (c.wsReady & 1) ? probe : 0;
    c.wsMode = (c.wsReady & 1) ? memo : 0;
    Log("OK   waypoint snap: ready 0x%lX; Memo=%ld (%s, rows live at most %ld ms, %s), Probe=%ld (burst line from %ld ms)", c.wsReady, c.wsMode,
        c.wsMode == 2 ? "shadow: the game's answer is used" : c.wsMode ? "repeat questions answered" : "off", c.wsMemoMs,
        g_snOn ? "dropped by [RouteSnap]'s write sequence" : "[RouteSnap] is off: no write sequence, the memo serves nothing", c.wsProbe, c.wsLogMs);
}

// the 60 s counter line (ts3native.cpp MaybeLogCounters)
static void WpsLogCounters() {
    const WpSnapCounters& c = ts3native_wpsnap;
    if (!c.wsReady || !c.wsCalls) return;
    Log("waypoint snap: mode %ld probe %ld ready 0x%lX: ArePointsConnectedByLand calls %ld in %ld ms (longest %ld us); memo hits %ld, stored %ld, not kept %ld; "
        "shadow checks %ld mismatch %ld; gathers under the call %ld (try 0 / 1 / 2: %ld / %ld / %ld) %ld ms, candidates %ld; gathers elsewhere %ld, %ld ms; "
        "bursts logged %ld; write seq %ld",
        c.wsMode, c.wsProbe, c.wsReady, c.wsCalls, (LONG)(c.wsUs / 1000), c.wsMaxUs, c.wsHits, c.wsStored, c.wsNoSeq, c.wsShadow, c.wsMismatch, c.wsGathers,
        c.wsGatherTry[0], c.wsGatherTry[1], c.wsGatherTry[2], (LONG)(c.wsGatherUs / 1000), c.wsCands, c.wsGatherOther, (LONG)(c.wsGatherOtherUs / 1000),
        c.wsBursts, g_snWriteSeq);
}
