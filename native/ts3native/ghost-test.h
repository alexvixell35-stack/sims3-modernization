// TestUnseenGhost ([UnseenGhost], ghost.cpp, docs/202 §3.9.5). Included by selftest.cpp. Fakes only; no game code runs.
// Checks: the set (stage + add + remove, duplicates, full, remove of a missing id), footprint enables of a ghost skipped and
// disables passed, other objects untouched, the agent update skip, stale entries dropped (switch off / too old), and the
// exit rule: after every remove the next enable reaches the game's body (the re-insert).
struct GhFakeObj { u8 pad[8]; DWORD lo, hi; };
static int g_ghFakeCalls, g_ghFakeLastEn;
static void __fastcall GhFakeUsf(void*, void*, void*, DWORD enable, DWORD, DWORD) { g_ghFakeCalls++, g_ghFakeLastEn = (int)(enable & 0xFF); }

static void TestUnseenGhost() {
    Ts3nCounters& k = ts3native_counters;
    const Ts3nCounters saved = k;
    const UnUsfFn savedNext = g_ghFpNext;
    const bool savedOn = g_ghOn;
    memset(g_gh, 0, sizeof g_gh);
    g_ghN = 0;
    g_ghFpNext = GhFakeUsf;
    g_ghOn = true;
    k.ghMode = 1, k.ghAdds = k.ghRemoves = k.ghLive = k.ghLiveMax = k.ghFpSkipped = k.ghFpDisables = 0;
    k.ghAgentSkipped = k.ghExpired = k.ghFull = k.ghRemoveMissing = 0;
    GhFakeObj a = {{}, 0x1111, 0x22}, b = {{}, 0x3333, 0x44};

    assert(GhMsg(21, a.lo) == 1 && GhMsg(22, a.hi) == 1 && g_ghN == 1 && k.ghAdds == 1 && k.ghLive == 1);
    assert(GhMsg(21, a.lo) == 1 && GhMsg(22, a.hi) == 1 && g_ghN == 1 && k.ghAdds == 1);  // same id again: no second slot
    assert(GhMsg(24, 0) == 1);
    // ghost: enables skipped (no remove-all + re-insert, no +0x918), disables pass
    g_ghFakeCalls = 0;
    GhFpHook(NULL, NULL, &a, 1, 0, 0);
    assert(g_ghFakeCalls == 0 && k.ghFpSkipped == 1);
    GhFpHook(NULL, NULL, &a, 0, 0, 0);
    assert(g_ghFakeCalls == 1 && g_ghFakeLastEn == 0 && k.ghFpDisables == 1);
    // anyone else: untouched
    GhFpHook(NULL, NULL, &b, 1, 0, 0);
    assert(g_ghFakeCalls == 2 && g_ghFakeLastEn == 1 && k.ghFpSkipped == 1);
    // agent update: the owner pointer at agent + 4
    u8 agentA[8], agentB[8];
    void* pa = &a;
    void* pb = &b;
    memcpy(agentA + 4, &pa, 4);
    memcpy(agentB + 4, &pb, 4);
    assert(GhAgentSkip(agentA) && !GhAgentSkip(agentB) && k.ghAgentSkipped == 1);
    // exit: removed, and the re-insert (enable) reaches the body
    assert(GhMsg(21, a.lo) == 1 && GhMsg(23, a.hi) == 1 && g_ghN == 0 && k.ghRemoves == 1 && k.ghLive == 0);
    GhFpHook(NULL, NULL, &a, 1, 0, 0);
    assert(g_ghFakeCalls == 3 && g_ghFakeLastEn == 1);
    assert(!GhAgentSkip(agentA) && GhMsg(24, 0) == 0);
    // remove of a missing id: refused and counted
    assert(GhMsg(21, a.lo) == 1 && GhMsg(23, a.hi) == 0 && k.ghRemoveMissing == 1);
    // switch off: no adds; a live ghost is dropped at its next call (counted), so the game's update runs
    assert(GhMsg(21, b.lo) == 1 && GhMsg(22, b.hi) == 1 && g_ghN == 1);
    k.ghMode = 0;
    assert(GhMsg(21, a.lo) == 1 && GhMsg(22, a.hi) == 0 && g_ghN == 1);
    GhFpHook(NULL, NULL, &b, 1, 0, 0);
    assert(g_ghFakeCalls == 4 && k.ghExpired == 1 && g_ghN == 0);
    k.ghMode = 1;
    // too old: dropped
    assert(GhMsg(21, b.lo) == 1 && GhMsg(22, b.hi) == 1 && g_ghN == 1);
    g_gh[GhFind(b.lo, b.hi)].at = GetTickCount() - kGhMaxMs - 1000;
    assert(!GhAgentSkip(agentB) && k.ghExpired == 2 && g_ghN == 0);
    // full: the 65th id is refused (the caller keeps the footprint and the game's updates)
    for (DWORD i = 0; i < GH_MAX; i++) assert(GhMsg(21, 0x9000 + i) == 1 && GhMsg(22, 1) == 1);
    assert(GhMsg(21, 0xFFFF) == 1 && GhMsg(22, 1) == 0 && k.ghFull == 1 && k.ghLiveMax == GH_MAX);
    // not installed: every message refused (ts3fixes then never ghosts)
    g_ghOn = false;
    assert(GhMsg(21, 1) == 0 && GhMsg(24, 0) == 0);

    memset(g_gh, 0, sizeof g_gh);
    g_ghN = 0;
    g_ghFpNext = savedNext;
    g_ghOn = savedOn;
    k = saved;
    printf("selftest unseen ghost ok\n");
}
