// TestStallWatch ([StallWatch], stallwatch.cpp, docs/67 §25). Included by selftest.cpp. A fake capture stands in for the
// sim thread; the real capture runs once on a helper thread parked in Sleep (its stack, no Simulate frame).
static bool g_swFakeInSim = true;
static LONG g_swFakeCalls;
static bool SwFakeCapture(DWORD, char* out, size_t cap, bool* inSim) {
    g_swFakeCalls++;
    *inSim = g_swFakeInSim;
    strcpy_s(out, cap, "eip TS3W+0x4E5EBF, stack scan (1): TS3W+0x4538B0");
    return true;
}
static DWORD WINAPI SwSleeper(LPVOID) { Sleep(3000); return 0; }
static void TestStallWatch() {
    static_assert(offsetof(Ts3nCounters, apFound) == 0x63E4 && offsetof(Ts3nCounters, ssReady) == 0x6404 && offsetof(Ts3nCounters, ssSpare) == 0x6418 &&
                      sizeof(Ts3nCounters) == 6894 * 4, "counters v202 (docs/registry.md)");
    Ts3nCounters& k = ts3native_counters;
    const LONG st0 = k.ssStalls, ln0 = k.ssLines;
    g_ssSt = {};
    g_ssTick = 0;
    SsCheck(100000, SwFakeCapture);  // no iteration seen yet: nothing
    assert(g_swFakeCalls == 0);
    g_ssTick = 100000;
    SsCheck(104000, SwFakeCapture);  // 4 s: fine
    assert(g_swFakeCalls == 0 && k.ssOpenMs == 4000);
    SsCheck(105000, SwFakeCapture);  // 5 s: first line
    assert(g_swFakeCalls == 1 && k.ssStalls == st0 + 1 && k.ssLines == ln0 + 1 && g_ssSt.in);
    SsCheck(106000, SwFakeCapture);  // inside EveryS: no line
    assert(g_swFakeCalls == 1);
    for (DWORD t = 115000; t <= 175000; t += 10000) SsCheck(t, SwFakeCapture);  // every 10 s up to Lines (6)
    assert(g_swFakeCalls == 6 && k.ssLines == ln0 + 6 && k.ssStalls == st0 + 1 && k.ssMaxMs >= 55000);
    g_ssTick = 176000;
    SsCheck(176500, SwFakeCapture);  // the iteration ended
    assert(!g_ssSt.in && k.ssOpenMs == 500);
    // no Simulate frame on the stack (world load, travel): no stall, no line
    g_swFakeInSim = false;
    SsCheck(190000, SwFakeCapture);
    assert(g_swFakeCalls == 7 && k.ssStalls == st0 + 1 && k.ssLines == ln0 + 6 && !g_ssSt.in);
    g_swFakeInSim = true;
    // the real capture on a thread in Sleep: a context and a stack scan, no Simulate frame
    DWORD tid = 0;
    HANDLE th = CreateThread(NULL, 0, SwSleeper, NULL, 0, &tid);
    Sleep(50);
    char line[1400];
    bool inSim = true;
    assert(th && SsCapture(tid, line, sizeof(line), &inSim) && !inSim && !strncmp(line, "eip ", 4));
    WaitForSingleObject(th, INFINITE), CloseHandle(th);
    g_ssTick = 0, g_ssSt = {}, k.ssOpenMs = 0;
    printf("stall watch: line at 5 s, every 10 s up to 6, end line, no line without a Simulate frame, real capture: %s\n", line);
}
