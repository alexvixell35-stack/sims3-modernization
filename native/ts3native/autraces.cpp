// [AutShadow] Races (docs/data/races-handoff.md R1b, R2b, R3a; ledger docs/51 §467), default 0, correctness runs only.
// Bit 0 (R1b): pre-warm the lazy first-fill caches the worker could reach (docs/131 §9.2-9.3) once at world event 7, on
//   the sim thread, then check every guard; a guard still clear sets g_arR1Bad and the worker aborts every answer
//   (AS_AB_OTHER, r1Aborts) until the next event 7 pre-warm leaves them all set. Q2 pick: world load only, no re-warm at
//   cuts (the guards only go 0 -> 1, so a cut re-check adds nothing).
// Bit 1 (R2b + R3a): witness of the 37 R2 load-time words and the 6 R3 init words: hash each at the first cut after
//   event 7, compare at every later cut, log every change. Paused from world events 2/3/5 to the next event 7 (the load
//   legitimately replaces world-scope words; the worker must not span it, R2c). Q3/Q7 pick: a witness is enough.
// Bit 2 (R2c): the worker never spans a world teardown or swap. World events 2 (leave not-in-world), 3 (world quit),
//   4 (quit app), 5 (load started) and 8 (load failed) (Sims3.SimIFace.WorldEvent) close the worker gate on the sim
//   thread, in routesnap's World_FireEvent detour before the game's handlers run. A job in flight gets the stop flag and
//   its result becomes an epoch drop; the sim then waits up to DrainMs for it to leave the worker (drain). Past the bound
//   the sim goes on (abort fallback): the stop flag and the closed gate make every later IC answer of that job abort, so
//   it reads no game state, and the job leaves on its own (r2cLate). While the gate is closed there is no cut (a posted
//   request finishes as an epoch drop). Event 7 opens it after the R1 pre-warm; the epoch bump at 7 makes the next cut
//   rebuild the snapshot. Travel and the return trip are the same sequence (3, 5, 7). Q5 (abort vs drain) was open in the
//   handoff: drain with a bounded wait and abort fallback, both logged. Before R2c the old path only set the stop flag at
//   the next Simulate top, never under Satb act, and not at all for an event fired inside the window (frame limiter).
// Household rule: no Sim, household or object state is read or written here; process globals only (handoff §2). R2c only
// drops worker answers (the game scores the request itself), so household and selected Sims see vanilla.
// Reads and writes only fixed game globals; every game call runs inside __try (a fault logs and turns the bit off).

static int g_arOn;                 // ini [AutShadow] Races
static volatile LONG g_arR1Bad;    // 1: a pre-warm guard is clear; the worker aborts (AsWorkerAnswer)
static int g_arR2State;            // 0 paused (no world yet or load running), 1 take a baseline at the next cut, 2 compare
static intptr_t g_arShift;         // selftest: game address -> test buffer
static u8* ArAt(DWORD a) { return (u8*)(uintptr_t)a + g_arShift; }

// R1a map [verified bytes, ledger §467]: zero-argument cdecl getters with their done flag (size 1: byte & mask, size 4:
// dword != 0). fn 0 = witness only (no call).
struct ArPw { DWORD done; u8 size, mask; DWORD fn; const char* what; };
static const ArPw kArPw[] = {
    {TS3A(0x011D7518), 1, 1, TS3A(0x007935B0), "static empty string A 0x11D7508-0x11D7518 (+ CRT atexit 0x123437C/80)"},
    {TS3A(0x011DA058), 1, 1, TS3A(0x008A79B0), "static empty string B 0x11DA048-0x11DA058 (+ CRT atexit)"},
    {TS3A(0x011C7984), 4, 0, TS3A(0x004D9AD0), "default allocator 0x11C7984 + block 0x11DC168-0x11DC184 (0x931020)"},
    {TS3A(0x011E96C1), 1, 0xFF, TS3A(0x009B0530), "option byte 0x11E96C0/1"},
    {TS3A(0x011ECEED), 1, 0xFF, TS3A(0x00C88D80), "option byte 0x11ECEEC/D"},
    // 0x4F98E0's first call also empties the hash map 0x114CA10 (frees its nodes): never called by us, witness only
    {TS3A(0x011CC928), 1, 0xFF, 0, "EAText handler fill 0x114CA08/0C, 0x11CB928 (0x4F98E0, witness only)"},
};
static const int AR_NPW = sizeof(kArPw) / sizeof(kArPw[0]);
// host +0x50 converter 0xD7BD40: function-local class caches, guard dword 0x11EE588 bit i -> cache, ScriptHost_FindClass
// (host, "Sims3.SimIFace", name) [verified bytes]; we repeat the game's own block: or the bit, then store the class
static const DWORD AR_CVT_GUARD = TS3A(0x011EE588), AR_CVT_ALL = 0x7FFF;
static const struct { DWORD cache; const char* name; } kArCvt[15] = {
    {TS3A(0x011EE584), "Vector2"}, {TS3A(0x011EE580), "Vector3"}, {TS3A(0x011EE57C), "Matrix44"}, {TS3A(0x011EE578), "Rect"},
    {TS3A(0x011EE574), "BoundingBox"}, {TS3A(0x011EE570), "ResourceKey"}, {TS3A(0x011EE56C), "RoutingSlotInfo"},
    {TS3A(0x011EE568), "ContainmentSlotInfo"}, {TS3A(0x011EE564), "LotCorners"}, {TS3A(0x011EE560), "LotDisplayLevelInfo"},
    {TS3A(0x011EE55C), "PondInfo"}, {TS3A(0x011EE558), "RoutePlanResult"}, {TS3A(0x011EE554), "RoomInfo"}, {TS3A(0x011EE550), "Quaternion"},
    {TS3A(0x011EE54C), "ClipInfoNative"}};
typedef void* (__fastcall* ArFindClassFn)(void* host, void* edx, const char* ns, const char* name);  // 0xD79CA0, ret 8
static ArFindClassFn g_arFindClass = (ArFindClassFn)(uintptr_t)TS3A(0x00D79CA0);
typedef DWORD(__cdecl* ArGetFn)();
static void (*g_arCall)(DWORD fn) = NULL;  // selftest: instead of calling game code

static bool ArDone(const ArPw& p) { return p.size == 4 ? *(DWORD*)ArAt(p.done) != 0 : (*ArAt(p.done) & p.mask) != 0; }

// one guarded call; false = it faulted
static bool ArCallGetter(DWORD fn) {
    __try {
        if (g_arCall) g_arCall(fn); else ((ArGetFn)(uintptr_t)fn)();
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}
static bool ArCvtFill(void* host, int i, DWORD bit) {
    __try {
        *(DWORD*)ArAt(AR_CVT_GUARD) |= bit;
        *(DWORD*)ArAt(kArCvt[i].cache) = (DWORD)(uintptr_t)g_arFindClass(host, NULL, "Sims3.SimIFace", kArCvt[i].name);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}

// R1b, sim thread, world event 7
static void ArPreWarm() {
    Ts3nAutShadow& k = ts3native_autshadow;
    for (int i = 0; i < AR_NPW; i++) {
        if (!kArPw[i].fn || ArDone(kArPw[i])) continue;
        if (!ArCallGetter(kArPw[i].fn)) { Log("FAIL autshadow races R1: %s faulted, Races bit 0 off", kArPw[i].what); g_arOn &= ~1; break; }
        k.r1Calls++;
    }
    void* host = *(void**)ArAt(TS3A(0x011EE514));
    for (int i = 0; i < 15 && (g_arOn & 1) && host; i++) {
        const DWORD bit = 1u << i;
        if (*(DWORD*)ArAt(AR_CVT_GUARD) & bit) continue;
        if (!ArCvtFill(host, i, bit)) { Log("FAIL autshadow races R1: FindClass %s faulted, Races bit 0 off", kArCvt[i].name); g_arOn &= ~1; break; }
        k.r1Fills++;
    }
    // witness: every guard after the pre-warm (witness-only rows included)
    LONG clear = 0;
    char miss[256] = "";
    for (int i = 0; i < AR_NPW; i++)
        if (!ArDone(kArPw[i])) clear++, strncat_s(miss, kArPw[i].what, _TRUNCATE), strncat_s(miss, "; ", _TRUNCATE);
    const DWORD g = *(DWORD*)ArAt(AR_CVT_GUARD) & AR_CVT_ALL;
    for (int i = 0; i < 15; i++) clear += !(g & (1u << i));
    k.r1Guard = (LONG)g, k.r1Clear = clear;
    InterlockedExchange(&g_arR1Bad, clear || !(g_arOn & 1) ? 1 : 0);
    Log("%s autshadow races R1 pre-warm: %ld getters called, %ld converter kinds filled, guards 0x%04lX, %ld still clear%s%s",
        clear ? "PART" : "OK  ", k.r1Calls, k.r1Fills, g, clear, clear ? ": " : "", miss);
}

// R2b + R3a witness rows: address, bytes, may-change flag (R2a runtime classes, P3 counters), r2a class [ledger §467]
struct ArWit { DWORD a; WORD len; u8 mayChange; const char* cls; };
static const ArWit kArWit[] = {
    {TS3A(0x011D4BC8), 4, 0, "load"}, {TS3A(0x011CB864), 4, 0, "load"}, {TS3A(0x011ECBC4), 4, 0, "load-world"}, {TS3A(0x011E9158), 4, 0, "load"},
    {TS3A(0x011ED034), 4, 0, "load-world"}, {TS3A(0x011E9114), 4, 0, "load"}, {TS3A(0x0114CA0C), 4, 1, "runtime-lazy"},
    {TS3A(0x011ECF08), 4, 0, "load"}, {TS3A(0x011E8FA8), 4, 0, "load"}, {TS3A(0x011AF670), 16, 0, "load (CRT constant vector)"},
    {TS3A(0x011D4068), 4, 0, "load-world"}, {TS3A(0x011D8888), 4, 0, "load-world"}, {TS3A(0x011E8FC4), 4, 0, "load"},
    {TS3A(0x011E90FC), 4, 0, "load"}, {TS3A(0x0114CA08), 4, 1, "runtime-lazy"}, {TS3A(0x011C78D8), 4, 0, "load"},
    {TS3A(0x011CB928), 0x1000, 1, "runtime-lazy (handler table)"}, {TS3A(0x011CDD00), 4, 0, "load"}, {TS3A(0x011CDD10), 4, 0, "load"},
    {TS3A(0x011CE2D0), 4, 0, "load"}, {TS3A(0x011CF490), 4, 0, "load-world"}, {TS3A(0x011D3E28), 4, 0, "unknown"},
    {TS3A(0x011D4BBC), 4, 0, "load"}, {TS3A(0x011D4BC0), 4, 0, "load"}, {TS3A(0x011D6A78), 0x4C * 36, 1, "runtime (callback table)"},
    {TS3A(0x011D9128), 4, 0, "load-world"}, {TS3A(0x011D9848), 4, 0, "load-world"}, {TS3A(0x011DC350), 4, 0, "load-world"},
    {TS3A(0x011E8FC0), 4, 0, "load"}, {TS3A(0x011E90F8), 4, 0, "load"}, {TS3A(0x011E9290), 4, 0, "load"}, {TS3A(0x011E92A8), 4, 0, "load"},
    {TS3A(0x011E94F8), 4, 0, "load"}, {TS3A(0x011EB490), 4, 0, "load"}, {TS3A(0x011EBAE0), 4, 0, "load"},
    {TS3A(0x011ED780), 4, 1, "runtime (user tool manager)"}, {TS3A(0x011EE140), 16, 1, "runtime (per-tick scratch)"},
    // R3a: Boehm and script-host init words (P3, docs/120)
    {TS3A(0x011EE528), 4, 0, "R3 GC init flag"}, {TS3A(0x011EE514), 4, 0, "R3 script host"}, {TS3A(0x011EE520), 4, 1, "R3 heap-size counter"},
    {TS3A(0x011DC18C), 4, 0, "R3 tracking mode"}, {TS3A(0x0114C908), 4, 0, "R3 thread record TLS index"}, {TS3A(0x011CC964), 4, 0, "R3"},
};
static const int AR_NWIT = sizeof(kArWit) / sizeof(kArWit[0]);
static DWORD g_arWitHash[AR_NWIT];

static DWORD ArHash(const ArWit& w) {
    const u8* p = ArAt(w.a);
    if (w.len == 4) return *(const DWORD*)p;
    DWORD h = 2166136261u;
    for (int i = 0; i < w.len; i++) h = (h ^ p[i]) * 16777619u;
    return h;
}

// R2c (bit 2), see the header
static volatile LONG g_arGate;   // 1: world going away (events 2/3/4/5/8 until 7): worker answers abort, no cut
static int g_arDrainMs = 250;    // ini [AutShadow] DrainMs
static bool g_arLatePending;     // an aborted drain's job has not left the worker yet
static bool AsDrainBusy();       // autshadow-run.cpp: our job is inside the worker
static bool AsDrainStop();       // autshadow-run.cpp: stop flag + epoch drop for it; true = it runs under Satb act

static bool ArGoingAway(DWORD id) { return id == 2 || id == 3 || id == 4 || id == 5 || id == 8; }

// sim thread, World_FireEvent entry
static void ArDrain(DWORD id) {
    Ts3nCounters& c = ts3native_counters;
    if (!g_arGate) {
        InterlockedExchange(&g_arGate, 1), c.r2cCloses++;
        Log("autshadow races R2c: world event %lu closes the worker gate", id);
    }
    if (!AsDrainBusy()) { c.r2cIdle++; return; }
    c.r2cDrainReq++;
    if (AsDrainStop()) c.r2cSatb++;
    LARGE_INTEGER f, t0, t;
    QueryPerformanceFrequency(&f), QueryPerformanceCounter(&t0);
    LONG us = 0;
    for (int spin = 0;; spin++) {
        QueryPerformanceCounter(&t);
        us = (LONG)((t.QuadPart - t0.QuadPart) * 1000000 / f.QuadPart);
        if (!AsDrainBusy() || us >= g_arDrainMs * 1000) break;
        if (spin < 64) YieldProcessor();
        else if (spin < 256) SwitchToThread();
        else Sleep(1);
    }
    const bool left = !AsDrainBusy();
    c.r2cWaitUs += us;
    if (us > c.r2cWaitMaxUs) c.r2cWaitMaxUs = us;
    if (left) {
        c.r2cDrained++;
        Log("OK   autshadow races R2c: world event %lu, worker job drained in %ld us", id, us);
    } else {
        c.r2cAborted++, g_arLatePending = true;
        Log("WARN autshadow races R2c: world event %lu, worker job still inside after %d ms: abort (stop flag set, every answer of "
            "it refused, result dropped)", id, g_arDrainMs);
    }
}

// sim thread, AsSimLeave: our job left the worker (before its finish)
static void ArJobLeft() {
    if (!g_arLatePending) return;
    g_arLatePending = false, ts3native_counters.r2cLate++;
    Log("autshadow races R2c: the aborted worker job left the worker (gate %s)", g_arGate ? "closed" : "open");
}

static void ArWorldEvent(DWORD id) {
    if (!g_arOn) return;
    if (id == 2 || id == 3 || id == 5) g_arR2State = 0;
    if ((g_arOn & 4) && ArGoingAway(id)) ArDrain(id);
    if (id != 7) return;
    if (g_arOn & 1) ArPreWarm();
    if (g_arOn & 2) g_arR2State = 1;
    if ((g_arOn & 4) && g_arGate) {
        InterlockedExchange(&g_arGate, 0), ts3native_counters.r2cReopens++;
        Log("autshadow races R2c: world event 7 opens the worker gate");
    }
}

// sim thread, every cut (AsCut)
static void ArCut() {
    if (!(g_arOn & 2) || !g_arR2State) return;
    Ts3nAutShadow& k = ts3native_autshadow;
    if (g_arR2State == 1) {
        for (int i = 0; i < AR_NWIT; i++) g_arWitHash[i] = ArHash(kArWit[i]);
        g_arR2State = 2, k.r2Bases++;
        Log("autshadow races R2 witness: baseline of %d rows", AR_NWIT);
        return;
    }
    k.r2Cuts++;
    for (int i = 0; i < AR_NWIT; i++) {
        const DWORD h = ArHash(kArWit[i]);
        if (h == g_arWitHash[i]) continue;
        const ArWit& w = kArWit[i];
        const LONG n = w.mayChange ? ++k.r2Runtime : ++k.r2Unexp;
        if (!w.mayChange && !k.r2First) k.r2First = i + 1;
        if (n <= 32)
            Log("autshadow races R2 witness: %s 0x%08lX (%s) %s 0x%08lX -> 0x%08lX at cut %ld", w.mayChange ? "runtime" : "UNEXPECTED",
                w.a, w.cls, w.len == 4 ? "value" : "hash", g_arWitHash[i], h, k.r2Cuts);
        g_arWitHash[i] = h;
    }
}

static void ApplyAutRaces(const wchar_t* ini) {
    g_arOn = GetPrivateProfileIntW(L"AutShadow", L"Races", 0, ini) & 7;
    g_arDrainMs = max(1, min(5000, (int)GetPrivateProfileIntW(L"AutShadow", L"DrainMs", 250, ini)));
    ts3native_autshadow.r1On = g_arOn, ts3native_autshadow.r2Rows = (g_arOn & 2) ? AR_NWIT : 0;
    ts3native_counters.r2cOn = (g_arOn & 4) ? 1 : 0, ts3native_counters.r2cDrainMs = g_arDrainMs;
    g_arR1Bad = (g_arOn & 1) ? 1 : 0;  // until the first event 7 pre-warm
    g_arGate = (g_arOn & 4) ? 1 : 0;   // no world yet: closed until the first event 7
    Log("autonomy shadow: Races=%d (bit 0 R1 pre-warm at world event 7 + worker abort while a guard is clear, bit 1 R2/R3 witness of %d rows, "
        "bit 2 R2c worker drain at world events 2/3/4/5/8, DrainMs=%d; docs/data/races-handoff.md)", g_arOn, AR_NWIT, g_arDrainMs);
}
