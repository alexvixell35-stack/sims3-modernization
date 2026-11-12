// TestRcTrim ([ResourceCache] TrimAfterCleanup, rctrim.cpp, docs/198 §4). Included by selftest.cpp. Fake manager and caches
// with the game's vtable values; a fake shrink-both stands in for 0x733E70 (no game code runs). No address-space walk.
static int g_rtFakeCalls;
static void __fastcall RtFakeShrink(u8* mgr, void*) {  // as 0x733E70: every idle entry of both caches dropped
    g_rtFakeCalls++;
    for (int off : {0x1E0, 0x1E4}) {
        u8* c = *(u8**)(mgr + off);
        if (c) *(DWORD*)(c + 0x98) = 0;
    }
}

static void TestRcTrim() {
    static_assert(offsetof(Ts3nCounters, rtOn) == 0x52D0 && offsetof(Ts3nCounters, rtBad) == 0x52E8 &&
                      offsetof(Ts3nCounters, rtSpare) == 0x52EC && sizeof(Ts3nCounters) == 6116 * 4,
                  "counters v136 layout after v135 (docs/registry.md)");
    alignas(16) static u8 mgr[0x200], rc[0x100], anim[0x100];
    *(DWORD*)mgr = RT_MGR_VT0, *(DWORD*)(mgr + 8) = RT_MGR_VT, *(u8**)(mgr + 0x1E0) = rc, *(u8**)(mgr + 0x1E4) = anim;
    *(DWORD*)rc = RT_CACHE_VT, *(DWORD*)(rc + 0x94) = 300 << 20, *(DWORD*)(rc + 0x98) = 96 << 20;
    *(DWORD*)anim = RT_CACHE_VT, *(DWORD*)(anim + 0x94) = 15 << 20, *(DWORD*)(anim + 0x98) = 4 << 20;
    const RtShrinkFn shrink0 = g_rtShrink;
    g_rtShrink = (RtShrinkFn)(void*)&RtFakeShrink, g_rtWalk = false;
    Ts3nCounters& k = ts3native_counters;
    k.rtTrims = k.rtFreedKB = k.rtLastFreedKB = k.rtStood = k.rtBad = 0;

    // 1. trim: both caches' idle bytes dropped and counted (96 + 4 MB), held untouched
    RtTrim(mgr, "selftest");
    assert(g_rtFakeCalls == 1 && k.rtTrims == 1 && k.rtFreedKB == 100 * 1024 && k.rtLastFreedKB == 100 * 1024);
    assert(*(DWORD*)(rc + 0x94) == 300u << 20 && *(DWORD*)(rc + 0x98) == 0);
    // 2. idle grew back less than before: a second trim counts only its own drop
    *(DWORD*)(rc + 0x98) = 1 << 20;
    RtTrim(mgr, "selftest");
    assert(g_rtFakeCalls == 2 && k.rtTrims == 2 && k.rtLastFreedKB == 1024 && k.rtFreedKB == 101 * 1024);
    // 3. wrong manager or ResourceCache vtable: no call
    *(DWORD*)rc = 0x12345678;
    RtTrim(mgr, "selftest");
    *(DWORD*)rc = RT_CACHE_VT, *(DWORD*)mgr = 0x12345678;
    RtTrim(mgr, "selftest");
    // the layout the check expected before the fix (0xFFE250 at +0; the game never has it at service + 0x18): no call
    *(DWORD*)mgr = RT_MGR_VT, *(DWORD*)(mgr + 8) = 0;
    RtTrim(mgr, "selftest");
    *(DWORD*)mgr = RT_MGR_VT0, *(DWORD*)(mgr + 8) = RT_MGR_VT;
    assert(g_rtFakeCalls == 2 && k.rtBad == 3);
    // 4. Apex Radiance loaded + ApexStandDown: no call; ApexStandDown=0: trims
    const int found0 = g_apexFound;
    const bool sd0 = g_apexStandDown;
    g_apexFound = 1, g_apexStandDown = true;
    RtTrim(mgr, "selftest");
    assert(g_rtFakeCalls == 2 && k.rtStood == 1);
    g_apexStandDown = false;
    RtTrim(mgr, "selftest");
    assert(g_rtFakeCalls == 3);
    g_apexFound = found0, g_apexStandDown = sd0;

    // 5. triggers: world load after the settle delay (load / reload / world-switch from the world name), periodic only after
    // the first world load
    char world0[sizeof(g_mdWorld)];
    strcpy_s(world0, g_mdWorld);
    g_rtWorlds = 0, g_rtWorldDue = false, g_rtLastTick = 0, g_rtLastWorld[0] = 0, g_rtEveryMs = 5 * 60000;
    assert(!RtDue(1000, 0) && !RtDue(10 * 60000, 0));  // no world yet: no periodic trim
    strcpy_s(g_mdWorld, "Sunset Valley");
    // long uptime before the first load (last trim 0): the load trigger comes first, no periodic one while it settles
    assert(!RtDue(3000000, 1) && !RtDue(3001000, 1));
    const char* w0 = RtDue(3002000, 1);
    assert(w0 && !strcmp(w0, "load"));
    g_rtWorlds = 0, g_rtWorldDue = false, g_rtLastTick = 0, g_rtLastWorld[0] = 0;
    assert(!RtDue(20000, 1) && !RtDue(21000, 1));
    const char* w = RtDue(22000, 1);
    assert(w && !strcmp(w, "load"));
    assert(!RtDue(22000 + 5 * 60000 - 1, 1));
    w = RtDue(22000 + 5 * 60000, 1);
    assert(w && !strcmp(w, "periodic"));
    assert(!RtDue(400000, 2));  // second load: settle again
    w = RtDue(402000, 2);
    assert(w && !strcmp(w, "reload"));
    strcpy_s(g_mdWorld, "Monte Vista");
    RtDue(500000, 3);
    w = RtDue(502000, 3);
    assert(w && !strcmp(w, "world-switch"));
    g_rtEveryMs = 0;
    assert(!RtDue(502000 + 60 * 60000, 3));  // TrimEveryMin=0: no periodic trim

    strcpy_s(g_mdWorld, world0);
    g_rtWorlds = 0, g_rtWorldDue = false, g_rtLastTick = 0, g_rtLastWorld[0] = 0;
    g_rtShrink = shrink0, g_rtWalk = true;
    k.rtTrims = k.rtFreedKB = k.rtLastFreedKB = k.rtStood = k.rtBad = 0, k.rtMaxUs = 0;
    Log("selftest: rctrim ok (trim counts, vtable checks, Apex stand-down, triggers)");
}
