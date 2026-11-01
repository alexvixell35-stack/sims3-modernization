// [UnseenGhost] (docs/202 §3.9.5, counters v145 gh*, Rodney 2026-10-31): an unseen Sim that ts3fixes `unseenroute` holds
// for a skipped trip is a "ghost": it takes no +0x918 while it travels unseen. ts3fixes removes its dynamic footprint once
// at ghost start (the game's own DisableDynamicFootprint, the normal locked path) and re-inserts it once at the end
// (EnableDynamicFootprint, same path) after it took the id off this set. While the id is in the set:
//   - UpdateSimFootprint 0x833560 enable calls for it are skipped (no remove-all + re-insert under +0x918); disable calls
//     run (removing is what the ghost wants anyway);
//   - its locomotion agent update (0x83CF52 -> 0x957210) is skipped: no steering obstacle query 0x82EDB0 / 0x82F0B0 via
//     0x9573B7, no avoidance 0x846D21 (a held Sim follows no path [inferred], this makes it certain).
// The lock itself is untouched: every other caller (visible Sims, routes, objects) takes it as before.
// Messages on the GetOption2 0x83 channel (ts3fixes UnseenFallback.cs): 21 stage id low; 22 id high: add (needs mode 1);
// 23 id high: remove (always; 0 = was not in the set, counted); 24 any ghost live (1 = yes).
// Safety net: an entry older than kGhMaxMs, or met while the switch is off, is dropped at its next footprint / agent call
// (counted ghExpired): a ts3fixes that lost track never leaves a Sim without footprint updates for long.
// Included by ts3native.cpp after unseenfbc.cpp (shares the agent call site and the 0x833560 layer chain).
enum { GH_MAX = 64 };
static const DWORD kGhMaxMs = 900000;  // 15 min wall: holds are capped at 120 sim-minutes
struct GhEnt { DWORD lo, hi, at; };   // at 0 = free
static GhEnt g_gh[GH_MAX];
static int g_ghN;
static bool g_ghOn;
static DWORD g_ghQLo;
static UnUsfFn g_ghFpNext;

static int GhFind(DWORD lo, DWORD hi) {
    for (int i = 0; i < GH_MAX; i++)
        if (g_gh[i].at && g_gh[i].lo == lo && g_gh[i].hi == hi) return i;
    return -1;
}
static void GhDrop(int i) {
    g_gh[i].at = 0;
    g_ghN--;
    ts3native_counters.ghLive = g_ghN;
}
// true = obj is a live ghost; stale entries (switch off, too old) are dropped here
static bool GhObj(const void* obj) {
    if (!g_ghN || !obj) return false;
    DWORD lo, hi;
    __try { lo = *(const DWORD*)((const u8*)obj + 8), hi = *(const DWORD*)((const u8*)obj + 0xC); }
    __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
    const int i = GhFind(lo, hi);
    if (i < 0) return false;
    if (ts3native_counters.ghMode != 1 || GetTickCount() - g_gh[i].at > kGhMaxMs) {
        GhDrop(i);
        InterlockedIncrement(&ts3native_counters.ghExpired);
        return false;
    }
    return true;
}

static void __fastcall GhFpHook(void* world, void* edx, void* obj, DWORD enable, DWORD force, DWORD arg) {
    if (g_ghN && GhObj(obj)) {
        if (enable & 0xFF) { InterlockedIncrement(&ts3native_counters.ghFpSkipped); return; }
        InterlockedIncrement(&ts3native_counters.ghFpDisables);
    }
    g_ghFpNext(world, edx, obj, enable, force, arg);
}

static bool GhAgentSkip(u8* agent) {
    if (!g_ghN) return false;
    const void* owner = NULL;
    __try { owner = *(void* const*)(agent + 4); } __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
    if (!GhObj(owner)) return false;
    InterlockedIncrement(&ts3native_counters.ghAgentSkipped);
    return true;
}

static u8 GhMsg(int code, DWORD lo) {
    Ts3nCounters& k = ts3native_counters;
    if (!g_ghOn) return 0;
    switch (code) {
    case 21: g_ghQLo = lo; return 1;
    case 22: {
        if (k.ghMode != 1) return 0;
        if (GhFind(g_ghQLo, lo) >= 0) return 1;
        for (int i = 0; i < GH_MAX; i++) {
            if (g_gh[i].at) continue;
            const DWORD now = GetTickCount();
            g_gh[i].lo = g_ghQLo, g_gh[i].hi = lo, g_gh[i].at = now ? now : 1;
            g_ghN++;
            k.ghAdds++, k.ghLive = g_ghN;
            Max(&k.ghLiveMax, (LONG)g_ghN);
            return 1;
        }
        k.ghFull++;
        return 0;
    }
    case 23: {
        const int i = GhFind(g_ghQLo, lo);
        if (i < 0) { k.ghRemoveMissing++; return 0; }
        GhDrop(i);
        k.ghRemoves++;
        return 1;
    }
    case 24: return g_ghN > 0 ? 1 : 0;
    }
    return 0;
}

// [UnseenGhost] Enabled=1: the agent update call site (shared with the probe / throttle / C sites) and one layer in the
// 0x833560 chain (below design U's stub, the throttle's or the probe's entry detour, else our own detour)
static bool ApplyUnseenGhost(const wchar_t* ini) {
    if (!GetPrivateProfileIntW(L"UnseenGhost", L"Enabled", 0, ini)) return false;
    Ts3nCounters& k = ts3native_counters;
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);  // the agent thunk times its camera refresh with it
    const u8* s = (const u8*)(uintptr_t)UN_AGENT_CALL;
    const DWORD to = Readable(s, 5) && s[0] == 0xE8 ? UN_AGENT_CALL + 5 + *(const LONG*)(s + 1) : 0;
    const CallSite site = {UN_AGENT_CALL, UN_AGENT, (void*)UnAgentThunk};
    if (to == (DWORD)(uintptr_t)UnAgentThunk || (to == UN_AGENT && PatchCalls("unseen ghost agent update", &site, 1))) {
        g_unGhostSkip = GhAgentSkip;
        k.ghReady |= 2;
    } else {
        Log("SKIP unseen ghost agent update: the call 0x%08lX goes to 0x%08lX", UN_AGENT_CALL, to);
    }
    UnUsfFn* layer = k.usfReady && g_usfOrig ? &g_usfOrig : g_utFpOn ? &g_utFpNext : g_unUsfOrig ? &g_unUsfOrig : NULL;
    if (layer) {
        g_ghFpNext = *layer;
        *layer = GhFpHook;
        k.ghReady |= 1;
    } else if ((g_ghFpNext = (UnUsfFn)InstallDetour("UpdateSimFootprint (unseen ghost)", UN_USF, kUnUsfPrologue,
                                                    sizeof(kUnUsfPrologue), (void*)GhFpHook)) != NULL) {
        k.ghReady |= 1;
    }
    if ((k.ghReady & 3) == 3) g_ghOn = true, k.ghMode = 1;
    Log("%s unseen ghost: ready 0x%lX (bit 0 footprint layer, 1 agent update); ghosts of ts3fixes unseenroute take no +0x918 "
        "(footprint enables and agent updates skipped)", g_ghOn ? "OK  " : "FAIL", k.ghReady);
    return true;
}
