// TestAutRaces: [AutShadow] Races (autraces.cpp, docs/data/races-handoff.md R1b, R2b, R3a) on a buffer standing in for
// the game's data section (g_arShift), with fake getters and a fake FindClass
static int g_arTestFind;
static int g_arTestFault;
static void ArTestCall(DWORD fn) {
    if (g_arTestFault) *(volatile int*)0 = 1;  // access violation: the pre-warm must catch it
    for (int i = 0; i < AR_NPW; i++)
        if (kArPw[i].fn == fn) {
            if (kArPw[i].size == 4) *(DWORD*)ArAt(kArPw[i].done) = 0xABCD;
            else *ArAt(kArPw[i].done) |= kArPw[i].mask;
        }
}
static void* __fastcall ArTestFind(void* host, void*, const char* ns, const char* name) {
    assert(host == (void*)(uintptr_t)0x5000 && !strcmp(ns, "Sims3.SimIFace") && name[0]);
    g_arTestFind++;
    return (void*)(uintptr_t)(0x9000 + g_arTestFind);
}

static void TestAutRaces() {
    const DWORD lo = 0x0114C000, hi = 0x01235000;
    u8* buf = (u8*)VirtualAlloc(NULL, hi - lo, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    assert(buf);
    const Ts3nAutShadow save = ts3native_autshadow;
    const int on0 = g_arOn;
    g_arShift = (intptr_t)buf - (intptr_t)lo;
    g_arCall = ArTestCall;
    const ArFindClassFn find0 = g_arFindClass;
    g_arFindClass = ArTestFind;
    Ts3nAutShadow& k = ts3native_autshadow;
    k.r1Calls = k.r1Fills = k.r2Bases = k.r2Cuts = k.r2Unexp = k.r2Runtime = k.r2First = 0;
    *(DWORD*)ArAt(0x011EE514) = 0x5000;           // host
    *(DWORD*)ArAt(AR_CVT_GUARD) = 1;             // Vector2 already filled by the game
    *(DWORD*)ArAt(0x011EE584) = 0x777;
    *ArAt(0x011DA058) = 1;                       // static string B already built
    g_arOn = 3, g_arR1Bad = 1, g_arR2State = 0;
    // R1: 4 getters called (5 with a function, one done), 14 converter kinds (one done, its cache untouched); the
    // witness-only EAText row stays clear, so the worker keeps aborting
    ArWorldEvent(7);
    assert(k.r1Calls == 4 && k.r1Fills == 14 && g_arTestFind == 14 && *(DWORD*)ArAt(0x011EE584) == 0x777);
    assert(*(DWORD*)ArAt(AR_CVT_GUARD) == AR_CVT_ALL && *(DWORD*)ArAt(0x011EE54C) == 0x9000 + 14);
    assert(*(DWORD*)ArAt(0x011C7984) == 0xABCD && *ArAt(0x011ECEED) == 0xFF && *ArAt(0x011D7518) == 1);
    assert(k.r1Clear == 1 && k.r1Guard == AR_CVT_ALL && g_arR1Bad == 1 && g_arR2State == 1);
    // the game fills the EAText table: the next load's pre-warm calls nothing and clears the abort
    *ArAt(0x011CC928) = 1;
    ArWorldEvent(7);
    assert(k.r1Calls == 4 && k.r1Fills == 14 && k.r1Clear == 0 && g_arR1Bad == 0);
    // R2: baseline at the first cut, a load word change is unexpected, the per-tick scratch is runtime
    ArCut();
    assert(k.r2Bases == 1 && k.r2Cuts == 0 && g_arR2State == 2);
    ArCut();
    assert(k.r2Cuts == 1 && k.r2Unexp == 0 && k.r2Runtime == 0);
    *(DWORD*)ArAt(0x011ECBC4) = 0x1234;
    ArAt(0x011EE140)[7] ^= 1;
    ArAt(0x011D6A78)[36 * 0x4B] ^= 1;           // last callback slot: inside the hashed range
    ArCut();
    assert(k.r2Unexp == 1 && k.r2First == 3 && k.r2Runtime == 2);
    ArCut();
    assert(k.r2Unexp == 1 && k.r2Runtime == 2);  // each change counted once
    // load running (event 5): paused, no compare; event 7: new baseline
    ArWorldEvent(5);
    *(DWORD*)ArAt(0x011D9128) = 0x55;
    ArCut();
    assert(g_arR2State == 0 && k.r2Cuts == 3 && k.r2Unexp == 1);
    ArWorldEvent(7);
    ArCut();
    ArCut();
    assert(k.r2Bases == 2 && k.r2Unexp == 1);
    // a faulting getter turns bit 0 off and keeps the worker aborting; bit 1 stays
    *ArAt(0x011D7518) = 0;
    g_arTestFault = 1;
    ArWorldEvent(7);
    g_arTestFault = 0;
    assert(g_arOn == 2 && g_arR1Bad == 1);
    // off: nothing runs
    g_arOn = 0, g_arR2State = 0;
    *(DWORD*)ArAt(0x011ECBC4) = 0x99;
    ArWorldEvent(7), ArCut();
    assert(g_arR2State == 0 && k.r2Unexp == 1);
    g_arFindClass = find0, g_arCall = NULL, g_arShift = 0, g_arOn = on0, g_arR1Bad = 0, g_arTestFind = 0;
    ts3native_autshadow = save;
    VirtualFree(buf, 0, MEM_RELEASE);
    printf("autraces: R1 pre-warm, guard witness, R2/R3 witness ok\n");
}
