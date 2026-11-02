// TestUnseenCost ([UnseenCost] probe, unseencost.cpp, docs/227). No game code runs. Checks: every call
// counted, 1 in 16 timed per site, nested calls are not top-level; distinct objects per pass; the calibration fills the
// self-cost and QPC floor counters.
static int UcTestInner() { return 7; }
static void TestUnseenCost() {
    Ts3nCounters& k = ts3native_counters;
    static Ts3nCounters saved;
    memcpy(&saved, &k, sizeof(k));
    const auto ucSave = g_uc;
    const bool onSave = g_ucOn;
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
    memset(&g_uc, 0, sizeof(g_uc));
    g_ucOn = true;
    for (int i = 0; i < 32; i++) {
        const int r = UcTime(UC_CLASS, [] { return UcTime(UC_LISTED, UcTestInner); });
        assert(r == 7);
    }
    assert(g_uc.n[UC_CLASS] == 32 && g_uc.top[UC_CLASS] == 32 && g_uc.sampled[UC_CLASS] == 2);
    assert(g_uc.n[UC_LISTED] == 32 && g_uc.top[UC_LISTED] == 0 && k.ucSampled[UC_LISTED] == 2 && k.ucN[UC_CLASS] == 32);
    assert(t_ucDepth == 0);
    static UcSeen t;
    memset(&t, 0, sizeof(t));
    int a = 0, b = 0;
    UcNote(t, &a, 5), UcNote(t, &a, 5), UcNote(t, &b, 5), UcNote(t, &a, 6);
    assert(t.asks == 4 && t.distinct == 3 && t.passes == 2);
    g_uc.all = 4095;  // the next timed call calibrates
    UcTime(UC_CAMERA, UcTestInner);
    assert(k.ucSelfN >= saved.ucSelfN + 64 && k.ucFloorN >= saved.ucFloorN + 1 && !g_ucCal);
    g_uc = ucSave, g_ucOn = onSave;
    memcpy(&k, &saved, sizeof(k));
    printf("unseen cost probe: ok\n");
}
