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

// TestAutRacesR2c: [AutShadow] Races bit 2 (R2c worker drain at world load / travel / quit) on the real run state: g_asRun,
// g_mtBusy (a helper thread stands in for the worker leaving), the stop flag, LastRun, the cut hold and the late leave
static DWORD WINAPI ArTestLeave(void* ms) {
    Sleep((DWORD)(uintptr_t)ms);
    InterlockedExchange(&g_mtBusy, 0);
    return 0;
}
static void TestAutRacesR2c() {
    static_assert(offsetof(Ts3nCounters, r2cOn) == 0x5FD0 && offsetof(Ts3nCounters, r2cReopens) == 0x6004 && sizeof(Ts3nCounters) == 0x62CC,
                  "counters v187 layout (docs/registry.md)");
    static Ts3nCounters save;
    save = ts3native_counters;
    const Ts3nAutShadow ksave = ts3native_autshadow;
    const int on0 = g_arOn, ms0 = g_arDrainMs, satbMode0 = g_satbMode;
    void* const fin0 = g_asMFinish;
    const LONG run0 = g_asRun, busy0 = g_mtBusy, stop0 = g_asStop, last0 = g_asLast, req0 = g_asReqId;
    g_asMFinish = NULL;  // AsFinish: no managed call
    Ts3nCounters& c = ts3native_counters;
    c.r2cCloses = c.r2cIdle = c.r2cDrainReq = c.r2cDrained = c.r2cAborted = c.r2cSatb = c.r2cWaitUs = c.r2cWaitMaxUs = 0;
    c.r2cLate = c.r2cGateAborts = c.r2cHeld = c.r2cReopens = 0;
    g_arLatePending = false;
    // off (bit 2 clear): going-away events leave the gate alone
    g_arOn = 2, g_arGate = 0;
    ArWorldEvent(3);
    assert(!g_arGate && c.r2cCloses == 0);
    // on: closed until the first event 7 (as ApplyAutRaces leaves it), which opens it once
    g_arOn = 4, g_arGate = 1, g_arDrainMs = 2000;
    ArWorldEvent(5);
    assert(g_arGate && c.r2cCloses == 0 && c.r2cIdle == 1);  // already closed: no second close
    ArWorldEvent(7);
    assert(!g_arGate && c.r2cReopens == 1);
    ArWorldEvent(7);
    assert(!g_arGate && c.r2cReopens == 1);
    // world quit with no job: close, idle; 0x11 and 9 are not going-away events
    g_asRun = AS_RUN_IDLE, g_mtBusy = 0;
    ArWorldEvent(0x11), ArWorldEvent(9);
    assert(!g_arGate && c.r2cCloses == 0);
    ArWorldEvent(3);
    assert(g_arGate && c.r2cCloses == 1 && c.r2cIdle == 2 && c.r2cDrainReq == 0);
    // a request posted while closed: no cut, finished as an epoch drop
    g_asReqId = 77, g_asBatchN = 1, g_asLast = AS_LAST_OK;
    const LONG cuts0 = ts3native_autshadow.cutReq;
    AsSimLeave();
    assert(c.r2cHeld == 1 && g_asLast == AS_LAST_EPOCH && !g_asReqId && g_asRun == AS_RUN_IDLE && ts3native_autshadow.cutReq == cuts0);
    ArWorldEvent(7);
    assert(!g_arGate && c.r2cReopens == 2);
    // travel out (event 3) with a job inside: stop, epoch drop, the job leaves after ~20 ms = drained
    g_asRun = AS_RUN_WAIT, g_mtBusy = 1, g_asStop = 0, g_asLast = AS_LAST_STOPPED;  // an overrun's kept part is dropped too
    HANDLE th = CreateThread(NULL, 0, ArTestLeave, (void*)(uintptr_t)20, 0, NULL);
    assert(th);
    ArWorldEvent(3);
    assert(g_arGate && g_asStop == 1 && g_asLast == AS_LAST_EPOCH && !g_mtBusy);
    assert(c.r2cDrainReq == 1 && c.r2cDrained == 1 && c.r2cAborted == 0 && c.r2cSatb == 0 && !g_arLatePending);
    assert(c.r2cWaitUs >= 10000 && c.r2cWaitUs < 2000000 && c.r2cWaitMaxUs == c.r2cWaitUs);
    WaitForSingleObject(th, INFINITE), CloseHandle(th);
    // the job's hook at phase 0 while the gate is closed: the stop flag goes on even with no epoch move
    void* const work0 = g_asMWork;
    const LONG ep0 = g_asRunEpoch;
    const unsigned short simCw0 = g_asSimCw;
    g_asMWork = (void*)(uintptr_t)0x4321, g_asSimCw = 0, g_asRunEpoch = g_asEpoch, g_asStop = 0;
    AsJobHook(0, g_asMWork);
    assert(g_asStop == 1);
    AsJobHook(1, g_asMWork);
    g_asMWork = work0, g_asSimCw = simCw0, g_asRunEpoch = ep0;
    // the finish of the drained job, then load started (5) / finished (7): the return trip is the same sequence
    g_asRun = AS_RUN_IDLE, g_asReqId = 0;
    ArWorldEvent(5);
    assert(g_arGate && c.r2cCloses == 2 && c.r2cIdle == 3);
    ArWorldEvent(7);
    assert(!g_arGate && c.r2cReopens == 3);
    // return trip under Satb act with a job that never leaves within DrainMs: abort fallback, then it leaves later
    g_satbMode = SATB_ACT, g_satbLive = 1;
    g_arDrainMs = 1;
    g_asRun = AS_RUN_WAIT, g_mtBusy = 1, g_asStop = 0, g_asLast = AS_LAST_OK;
    ArWorldEvent(3);
    assert(g_arGate && g_asStop == 1 && g_asLast == AS_LAST_EPOCH && g_mtBusy == 1);
    assert(c.r2cDrainReq == 2 && c.r2cDrained == 1 && c.r2cAborted == 1 && c.r2cSatb == 1 && g_arLatePending);
    g_satbLive = 0, g_satbMode = satbMode0;
    ArWorldEvent(5);  // still inside: a second bounded wait, a second abort; the late leave is counted once
    assert(c.r2cDrainReq == 3 && c.r2cAborted == 2 && c.r2cCloses == 3);
    g_mtBusy = 0, g_asJobExc = 0, g_asTrack0 = ts3native_mtprobe.workerTrackSkips;
    AsSimLeave();  // the WAIT branch sees the job out: late leave, finish as an epoch drop
    assert(c.r2cLate == 1 && !g_arLatePending && g_asRun == AS_RUN_IDLE && g_asLast == AS_LAST_EPOCH);
    ArWorldEvent(7);
    assert(!g_arGate && c.r2cReopens == 4);
    // quit app (4) and load failed (8) close it too and nothing reopens it without event 7
    ArWorldEvent(4);
    assert(g_arGate && c.r2cCloses == 4);
    ArWorldEvent(7);
    ArWorldEvent(8);
    assert(g_arGate && c.r2cCloses == 5 && c.r2cReopens == 5);
    g_arGate = 0, g_arOn = on0, g_arDrainMs = ms0, g_arLatePending = false;
    g_asMFinish = fin0, g_asRun = run0, g_mtBusy = busy0, g_asStop = stop0, g_asLast = last0, g_asReqId = req0, g_asBatchN = 0;
    ts3native_counters = save;
    ts3native_autshadow = ksave;
    printf("autraces: R2c drain, abort fallback, cut hold, late leave, reopen ok\n");
}
