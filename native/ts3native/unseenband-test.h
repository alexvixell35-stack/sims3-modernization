// TestUnseenBand ([UnseenProbe] Bands, unseenband.cpp, docs/322). Included by selftest.cpp after unseen-test.h. No game code runs.
static void TestUnseenBand() {
    UnseenBandCounters& b = ts3native_unseenband;
    Ts3nCounters& k = ts3native_counters;
    static_assert(offsetof(UnseenBandCounters, n) == 0x24 && offsetof(UnseenBandCounters, us) == 0x120 &&
                  offsetof(UnseenBandCounters, simUpd) == 0x21C && offsetof(UnseenBandCounters, autNamed) == 0x240,
                  "ts3native_unseenband layout (docs/registry.md)");
    const UnseenBandCounters save = b;
    const bool camOk = g_unCam.ok, onSave = g_ubOn;
    float camT[3], camP[3];
    memcpy(camT, g_unCam.tgt, 12), memcpy(camP, g_unCam.pos, 12);
    const auto viewSave = g_utView;
    const auto heldSave = g_utHeld;
    const LONG tskSave = k.tskOn;
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);

    // edges: 1-6 ascending whole metres, else nothing set
    LONG e[UB_EDGES] = {}, n = 0;
    assert(UbParse(L"50,150,400,1000", e, &n) && n == 4 && e[0] == 50 && e[3] == 1000);
    assert(UbParse(L" 50 , 150 ", e, &n) && n == 2 && e[1] == 150);
    n = 9;
    assert(!UbParse(L"", e, &n) && !UbParse(L"150,50", e, &n) && !UbParse(L"1,2,3,4,5,6,7", e, &n) && !UbParse(L"50x", e, &n) &&
           !UbParse(L"0,10", e, &n) && !UbParse(L"50,50", e, &n) && n == 9);

    assert(UbParse(L"50,150,400,1000", b.edgeM, &b.nEdges));
    for (int i = 0; i < b.nEdges; i++) g_ubEdge2[i] = (float)b.edgeM[i] * (float)b.edgeM[i];
    // camera target at the origin, camera 100 m up; one household viewer at x = 1000
    g_unCam.ok = true;
    const float tgt[3] = {0, 0, 0}, pos[3] = {0, 100, 0};
    memcpy(g_unCam.tgt, tgt, 12), memcpy(g_unCam.pos, pos, 12);
    g_utView.n = 1, g_utView.pos[0][0] = 1000, g_utView.pos[0][1] = 0, g_utView.pos[0][2] = 0, g_utView.at = (GetTickCount() ? GetTickCount() : 1);
    const float p10[3] = {10, 0, 0}, p100[3] = {100, 0, 0}, p300[3] = {300, 0, 0}, p700[3] = {700, 0, 0}, p2500[3] = {2500, 0, 0},
                p960[3] = {960, 0, 0};
    assert(UbRow(p10) == 0 && UbRow(p100) == 1 && UbRow(p300) == 2 && UbRow(p960) == 0);
    assert(UbRow(p700) == 2);   // 300 m from the viewer, 700 m from the camera: the nearest counts
    assert(UbRow(p2500) == 4);  // 1500 m from the viewer: past the last edge
    const LONG camOnly = b.camOnly;
    g_utView.at = 0;  // no fresh viewer table: camera points only
    assert(UbRow(p700) == 3 && b.camOnly == camOnly + 1);
    g_unCam.ok = false;
    assert(UbRow(p700) == UB_UNK);  // no camera, no viewers: no position class
    g_unCam.ok = true;

    // sums from QPC ticks, no floor loss
    memset(b.n, 0, sizeof(b.n)), memset(b.us, 0, sizeof(b.us)), memset(g_ubTk, 0, sizeof(g_ubTk));
    for (int i = 0; i < 1000; i++) UbAdd(UB_LOCO, 1, g_qpf.QuadPart / 2000000);  // 0.5 us each
    assert(b.n[UB_LOCO][1] == 1000 && b.us[UB_LOCO][1] >= 490 && b.us[UB_LOCO][1] <= 500);
    UbAddUs(UB_WAIT, UB_HH, 700);
    assert(b.n[UB_WAIT][UB_HH] == 1 && b.us[UB_WAIT][UB_HH] >= 699 && b.us[UB_WAIT][UB_HH] <= 700);

    // household ids: no fresh held table = household (unknown scope); listed = household; other = not
    g_utHeld.n = 0;
    assert(UbHouseholdId(5, 6));
    g_utHeld.n = 1, g_utHeld.lo[0] = 5, g_utHeld.hi[0] = 6, g_utHeld.at = (GetTickCount() ? GetTickCount() : 1);
    assert(UbHouseholdId(5, 6) && !UbHouseholdId(7, 6));
    // AutonomyManager run before any actor named: row "none"; household actor: UB_HH (never a band)
    g_ubOn = true, g_ubAutSet = false;
    const LONG none = b.autNone;
    UbTaskRun(2, 0, 0, 10);
    assert(b.autNone == none + 1 && b.n[UB_AUT][UB_UNK] == 1);
    k.tskOn = 0;
    assert(UbAutMsg(27, 5) == 0);  // no task seam: refused (ts3fixes stops this load)
    k.tskOn = 1;
    assert(UbAutMsg(27, 5) == 1 && UbAutMsg(28, 6) == 1 && g_ubAutSet && g_ubAutLo == 5 && g_ubAutHi == 6);
    UbTaskRun(2, 0, 0, 10);
    UbTaskRun(1, 5, 6, 10);
    assert(b.n[UB_AUT][UB_HH] == 1 && b.n[UB_TASK][UB_HH] == 1);
    g_ubOn = false;
    assert(UbAutMsg(27, 5) == 0);  // bands off: refused

    b = save, g_ubOn = onSave, g_ubAutSet = false, g_unCam.ok = camOk, k.tskOn = tskSave;
    memcpy(g_unCam.tgt, camT, 12), memcpy(g_unCam.pos, camP, 12);
    g_utView = viewSave, g_utHeld = heldSave;
    memset(g_ubTk, 0, sizeof(g_ubTk));
    printf("UnseenBands (docs/322): edge parse, nearest viewer (camera target / position, household table, camera only when stale), "
           "band rows, no-position row, carried sums, household / unknown scope = household row, autonomy actor channel: ok\n");
}
