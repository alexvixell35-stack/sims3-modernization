// [StallWatch] (docs/67 §25 "Logging proposal" (a) + (d), docs/69 §4 idea 5; counters v202 ss*): a sim stall sampler that
// works for players. Included by ts3native.cpp after f5watch.cpp (uses crashreport.cpp CrAfterCall / CrFrame, castrim.cpp
// CtFrames, simloop.cpp SimLoopAdd, userdiag.cpp g_udPhase).
//
// A sim-loop callback (end of every Simulate iteration, sim thread) stamps the time. A watchdog thread checks once a second:
// when no iteration has finished for StallMs, it suspends the sim thread, copies its context and stack (no lock, heap or log
// while it is suspended), resumes it, and logs eip + up to 24 return addresses as module+rva (ts3native frames: rva against
// this build's .map). Only a stack that holds a return address inside MonoScriptHost_Simulate 0xD81840-0xD82110 counts as a
// stuck iteration: a world load or travel runs outside the Simulate loop (no stamp, no Simulate frame) and logs nothing.
// One line now, then every EveryS up to Lines per stall; `stall watch: ... finished after N s` when the iteration ends.
// ssOpenMs is the open iteration's age at the last check, so a reader of /native (stutter profiler, (d)) sees a stuck
// iteration as such instead of a gap. Logging only: the game's code is not changed (one rel32 of simloop.cpp's hook).
// shortcut: (b) of the proposal (route follower id from the frame when 0x8538B0 / 0x84B590 is on the stack) is not built:
// it needs those functions' frame layouts; the module+rva walk names the functions.
// StallMs (diagnostics, default 0 = StallS as before): a stall threshold in ms; under 1000 the watchdog checks every 50 ms,
// EveryMs (default 100) spaces the lines of one stall, and lines are capped at 30 a minute (the rest counted in the end
// line). MainToo=1: each logged sim sample is followed by one of the main (render) thread, to see what that thread waits on.

static volatile LONG g_ssTick;  // GetTickCount at the end of the last Simulate iteration, 0 = none yet
static volatile DWORD g_ssSimTid;
static DWORD g_ssStallMs = 5000, g_ssEveryMs = 10000;
static LONG g_ssLines = 6;
static DWORD g_ssPollMs = 1000;    // StallMs < 1000: 50
static bool g_ssShort, g_ssMainToo;  // StallMs < 1000 (minute line cap); [StallWatch] MainToo
static DWORD g_ssMinute, g_ssMinLines, g_ssCapped;
static const DWORD SS_SIM_LO = TS3A(0x00D81840), SS_SIM_HI = TS3A(0x00D82110);  // MonoScriptHost_Simulate (simloop.cpp)

static void __cdecl SsIterate() {
    g_ssSimTid = GetCurrentThreadId();
    InterlockedExchange(&g_ssTick, (LONG)GetTickCount());
}

// eip + return-address scan of thread `tid`; *inSim = a return address inside Simulate. false = no context.
static bool SsCapture(DWORD tid, char* out, size_t cap, bool* inSim) {
    *inSim = false;
    HANDLE h = OpenThread(THREAD_SUSPEND_RESUME | THREAD_GET_CONTEXT, FALSE, tid);
    if (!h) { sprintf_s(out, cap, "not opened (err %lu)", GetLastError()); return false; }
    static DWORD stk[2048];  // the watchdog thread only
    CONTEXT c = {};
    c.ContextFlags = CONTEXT_CONTROL | CONTEXT_INTEGER;
    DWORD ns = 0;
    bool got = false;
    if (SuspendThread(h) != (DWORD)-1) {
        if (GetThreadContext(h, &c)) {
            got = true;
            MEMORY_BASIC_INFORMATION mi;
            if (VirtualQuery((const void*)(uintptr_t)c.Esp, &mi, sizeof(mi)) && mi.State == MEM_COMMIT && !(mi.Protect & (PAGE_GUARD | PAGE_NOACCESS))) {
                const DWORD end = (DWORD)(uintptr_t)mi.BaseAddress + (DWORD)mi.RegionSize;
                ns = (end - c.Esp) / 4;
                if (ns > 2048) ns = 2048;
                memcpy(stk, (const void*)(uintptr_t)c.Esp, ns * 4);
            }
        }
        ResumeThread(h);
    }
    CloseHandle(h);
    if (!got) { strcpy_s(out, cap, "no context"); return false; }
    DWORD f[24];
    int nf = 0;
    for (DWORD i = 0; i < ns; i++) {
        if (!CrAfterCall(stk[i])) continue;
        if (stk[i] > SS_SIM_LO && stk[i] < SS_SIM_HI) *inSim = true;
        if (nf < 24) f[nf++] = stk[i];
    }
    char at[48], fr[24 * 44 + 8];
    CrFrame(c.Eip, at, sizeof(at)), CtFrames(f, nf, fr, sizeof(fr));
    sprintf_s(out, cap, "eip %s, stack scan (%d):%s", at, nf, fr);
    return true;
}

// one check (watchdog thread, also the selftest): now = GetTickCount
struct SsState { bool in; LONG lines; DWORD next, logged; };
static SsState g_ssSt;
static void SsCheck(DWORD now, bool (*capture)(DWORD, char*, size_t, bool*)) {
    Ts3nCounters& k = ts3native_counters;
    SsState& s = g_ssSt;
    const DWORD last = (DWORD)g_ssTick;
    if (!last) return;
    const DWORD age = now - last;
    k.ssOpenMs = (LONG)age;
    if (age < g_ssStallMs) {
        if (s.in) Log("stall watch: sim iteration finished after a stall of at least %lu ms (%ld lines, %lu over the minute cap)", s.logged, s.lines, g_ssCapped);
        s.in = false;
        return;
    }
    if (!s.in) s.lines = 0, s.next = now;
    if (s.lines >= g_ssLines || (LONG)(now - s.next) < 0) return;
    if (g_ssShort) {  // short stalls are frequent: 30 lines a minute
        if (now - g_ssMinute >= 60000) g_ssMinute = now, g_ssMinLines = 0;
        if (g_ssMinLines >= 30) { g_ssCapped++, s.next = now + g_ssEveryMs; return; }
    }
    static char line[1400];
    bool inSim = false;
    const bool got = capture(g_ssSimTid, line, sizeof(line), &inSim);
    s.next = now + g_ssEveryMs;
    // not inside a Simulate iteration (load, travel, quit): no stall. StallMs < 1000 logs it anyway, tagged (the sim thread
    // may be held between iterations, e.g. paced by the main thread); the minute cap bounds a load screen
    if (got && !inSim && !g_ssShort) return;
    if (!s.in) s.in = true, k.ssStalls++;
    s.lines++, s.logged = age, g_ssMinLines++;
    k.ssLines++;
    Max(&k.ssMaxMs, (LONG)age);
    const char* ph = g_udPhase;
    Log("stall watch: sim thread %lu: no Simulate iteration finished for %lu ms (%ld/%ld)%s%s%s: %s", g_ssSimTid, age, s.lines, g_ssLines,
        got && !inSim ? ", outside Simulate" : "", ph ? ", save phase " : "", ph ? ph : "", line);
    if (g_ssMainToo && g_mainTid && g_mainTid != g_ssSimTid) {
        bool mIn = false;
        capture(g_mainTid, line, sizeof(line), &mIn);
        Log("stall watch: main thread %lu at sim stall %lu ms (%ld/%ld): %s", g_mainTid, age, s.lines, g_ssLines, line);
    }
}

static DWORD WINAPI SsMain(LPVOID) {
    for (;;) {
        Sleep(g_ssPollMs);
        SsCheck(GetTickCount(), SsCapture);
    }
}

// [StallWatch] Enabled=1 (default 0 until a correctness run; the design wants it in every run)
static bool ApplyStallWatch(const wchar_t* ini) {
    if (!GetPrivateProfileIntW(L"StallWatch", L"Enabled", 0, ini)) return false;
#ifdef TS3A_TABLE
    if (ts3a_noaddr) { Log("SKIP stall watch ([StallWatch]): no EA 1.69 address, docs/232 §8"); return false; }
#endif
    const int stallS = (int)GetPrivateProfileIntW(L"StallWatch", L"StallS", 5, ini);
    const int everyS = (int)GetPrivateProfileIntW(L"StallWatch", L"EveryS", 10, ini);
    const int lines = (int)GetPrivateProfileIntW(L"StallWatch", L"Lines", 6, ini);
    g_ssStallMs = (DWORD)(stallS < 2 ? 2 : stallS > 120 ? 120 : stallS) * 1000;
    g_ssEveryMs = (DWORD)(everyS < 2 ? 2 : everyS > 300 ? 300 : everyS) * 1000;
    g_ssLines = lines < 1 ? 1 : lines > 30 ? 30 : lines;
    const int stallMs = (int)GetPrivateProfileIntW(L"StallWatch", L"StallMs", 0, ini);  // diagnostics: 0 = StallS
    if (stallMs > 0) {
        g_ssStallMs = (DWORD)(stallMs < 100 ? 100 : stallMs > 120000 ? 120000 : stallMs);
        if (g_ssStallMs < 1000) {
            const int everyMs = (int)GetPrivateProfileIntW(L"StallWatch", L"EveryMs", 100, ini);
            g_ssShort = true, g_ssPollMs = 50;
            g_ssEveryMs = (DWORD)(everyMs < 50 ? 50 : everyMs > 300000 ? 300000 : everyMs);
        }
    }
    g_ssMainToo = GetPrivateProfileIntW(L"StallWatch", L"MainToo", 0, ini) != 0;
    if (!SimLoopAdd(SsIterate, "stall watch: iteration stamp")) { Log("SKIP stall watch: no Simulate iteration hook"); return false; }
    HANDLE t = CreateThread(NULL, 64 * 1024, SsMain, NULL, STACK_SIZE_PARAM_IS_A_RESERVATION, NULL);
    if (!t) { Log("FAIL stall watch: thread (err %lu)", GetLastError()); return false; }
    CloseHandle(t);
    ts3native_counters.ssReady = 1;
    Log("OK   stall watch (docs/67 §25): sim thread stack logged after %lu ms without a finished Simulate iteration, every %lu ms, up to %ld lines, "
        "checked every %lu ms, main thread %s", g_ssStallMs, g_ssEveryMs, g_ssLines, g_ssPollMs, g_ssMainToo ? "too" : "not sampled");
    return true;
}
