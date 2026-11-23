// TestNonSimSplit ([Diag] NonSimSplit, nonsim.cpp, docs/329). Included by selftest.cpp after taskseam-test.h. No game code runs:
// the lot table messages, the household lot test, owner rows, the band rows of lots (household / world / far) and of objects
// whose id is a household actor, the run sums, the outside-Sim-run footprint / plan sums and the dump on fake data.
static void TestNonSimSplit() {
    UnseenBandCounters& b = ts3native_unseenband;
    const UnseenBandCounters bSave = b;
    const bool onSave = g_nsOn, ubSave = g_ubOn, camOk = g_unCam.ok;
    const auto heldSave = g_utHeld;
    const auto viewSave = g_utView;
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
    if (!g_tskQpf.QuadPart) QueryPerformanceFrequency(&g_tskQpf);

    // lot table: refused while off; BEGIN, two lots, END with the right count swaps it in
    g_nsOn = false;
    assert(NsLotMsg(29, 0) == 0);
    g_nsOn = true;
    const float hx = 100, hz = 50, r = 30, fx = 900, zero = 0;
    DWORD u;
    auto lot = [&](DWORD lo, float x, float z, DWORD flags, float rad) {
        assert(NsLotMsg(30, lo) && NsLotMsg(31, 0));
        memcpy(&u, &x, 4), assert(NsLotMsg(32, u));
        memcpy(&u, &zero, 4), assert(NsLotMsg(33, u));
        memcpy(&u, &z, 4), assert(NsLotMsg(34, u));
        assert(NsLotMsg(35, flags));
        memcpy(&u, &rad, 4), assert(NsLotMsg(36, u));
    };
    assert(NsLotMsg(29, 0));
    lot(0x10, hx, hz, 1, r);   // household home lot
    lot(0x11, fx, 0, 0, r);    // a lot 900 m out
    lot(0x12, 0, 0, 2, 0);     // the world lot
    assert(NsLotMsg(37, 3) && g_nsLotN == 3 && g_nsLotHh == 1 && NsLotById(0x11, 0) && !NsLotById(0x11, 1));
    const LONG bad = g_nsLotBad;
    assert(NsLotMsg(30, 1) == 0 && g_nsLotBad == bad + 1 && g_nsLotN == 3);  // no BEGIN: refused, live table kept
    assert(NsLotMsg(29, 0) && NsLotMsg(37, 5) == 0 && g_nsLotN == 3);       // count mismatch: refused, live table kept
    for (int i = 0; i < 300; i++) {                                          // growth past the first block (no cap tied to lot count)
        if (!i) assert(NsLotMsg(29, 0));
        lot(0x100 + i, (float)i, 0, 0, 10);
    }
    assert(NsLotMsg(37, 300) && g_nsLotN == 300 && g_nsLotHh == 0 && g_nsLotCap >= 300);
    assert(NsLotMsg(29, 0));
    lot(0x10, hx, hz, 1, r), lot(0x11, fx, 0, 0, r), lot(0x12, 0, 0, 2, 0);
    assert(NsLotMsg(37, 3) && g_nsLotN == 3);
    const float in[3] = {110, 0, 60}, out[3] = {140, 0, 50};
    assert(NsInHhLot(in) && !NsInHhLot(out));

    // band rows: edges 50,150,400,1000; camera at the origin; a fresh held table with one household actor (id 5:6)
    g_ubOn = true;
    assert(UbParse(L"50,150,400,1000", b.edgeM, &b.nEdges));
    for (int i = 0; i < b.nEdges; i++) g_ubEdge2[i] = (float)b.edgeM[i] * (float)b.edgeM[i];
    g_unCam.ok = true;
    memset(g_unCam.tgt, 0, 12), memset(g_unCam.pos, 0, 12);
    g_utView.at = 0;
    g_utHeld.n = 1, g_utHeld.lo[0] = 5, g_utHeld.hi[0] = 6, g_utHeld.at = (GetTickCount() ? GetTickCount() : 1);
    NsEnt n = {};
    n.kind = NS_K_LOT, n.lo = 0x10;
    assert(NsLocate(n) == UB_HH);    // household lot: always live, whatever its distance
    n.lo = 0x11;
    assert(NsLocate(n) == 3);        // 900 m: band 400-1000
    n.lo = 0x12;
    assert(NsLocate(n) == NS_GLOB);  // the world lot: no distance
    n.lo = 0x99;
    assert(NsLocate(n) == UB_UNK);   // not in the table
    n.kind = NS_K_OBJ, n.lo = 5, n.hi = 6;
    assert(NsLocate(n) == UB_HH);    // a household actor
    g_utHeld.n = 0;
    n.kind = NS_K_LOT, n.lo = 0x11, n.hi = 0;
    assert(NsLocate(n) == UB_HH);    // no fresh held table: unknown scope counts as household
    g_utHeld = heldSave;
    n.kind = NS_K_FUNC;
    assert(NsLocate(n) == NS_GLOB);
    g_ubOn = false;
    n.kind = NS_K_OBJ, n.lo = 7;
    assert(NsLocate(n) == UB_UNK);   // no bands: no position rows

    // owner rows: same key + kind = same row, another kind = another row; the table refuses past 3/4 full
    memset(g_nsCls, 0, sizeof(g_nsCls));
    g_nsClsN = 0;
    const int r1 = NsRow(0x1000, NS_K_OBJ, 0, 0), r2 = NsRow(0x1000, NS_K_OBJ, 0, 0), r3 = NsRow(0x1000, NS_K_INV, 0, 0);
    assert(r1 >= 0 && r1 == r2 && r3 >= 0 && r3 != r1 && g_nsClsN == 2 && !strcmp(g_nsCls[r1].name, "?"));
    for (DWORD k = 0x2000; g_nsClsN < NS_CN * 3 / 4; k += 8) NsRow(k, NS_K_TASK, 0, 0);
    const LONG full = g_nsClsFull;
    assert(NsRow(0x9999990, NS_K_TASK, 0, 0) == -1 && g_nsClsFull == full + 1 && NsRow(0x1000, NS_K_OBJ, 0, 0) == r1);

    // runs: a resolved slot adds to its row; no slot goes to its own sum
    memset(g_nsEnt, 0, sizeof(g_nsEnt));
    g_nsEnt[3].cls = (short)(r1 + 1), g_nsEnt[3].kind = NS_K_OBJ, g_nsEnt[3].row = 2, g_nsEnt[3].at = GetTickCount() ? GetTickCount() : 1;
    const LONGLONG noEnt = g_nsNoEntTk;
    NsRun(3, 100, 0, 0, false);
    NsRun(3, 50, 0, 0, false);
    NsRun(-1, 7, 0, 0, false);
    const LONG selfN = g_nsSelfN;
    for (int i = 0; i < 64; i++) NsRun(-1, 0, 0, 0, false);
    assert(g_nsSelfN == selfN + 1);  // every 64th call times itself
    assert(g_nsCls[r1].tk[2] == 150 && g_nsCls[r1].n[2] == 2 && g_nsNoEntTk == noEnt + 7);
    NsReset(3 + TSK_EN);  // masked to the slot
    assert(g_nsEnt[3].cls == 0);

    // footprint / plan time of a Sim inside a non-Sim run lands in the outside sums; inside a Sim's own run it does not
    memset(g_nsOutTk, 0, sizeof(g_nsOutTk));
    g_tskInRun = 1;
    UbAdd(UB_PLAN, 3, 40);
    UbAdd(UB_FOOT, UB_HH, 9);
    g_tskInRun = 2;
    UbAdd(UB_PLAN, 3, 1000);
    g_tskInRun = 0;
    UbAdd(UB_FOOT, 3, 1000);
    assert(g_nsOutTk[1][3] == 40 && g_nsOutTk[0][UB_HH] == 9 && g_nsOutTk[0][3] == 0);

    // rows line: NS_ROWS comma-separated us values
    LONGLONG tk[NS_ROWS] = {};
    tk[0] = g_tskQpf.QuadPart / 1000, tk[NS_GLOB] = g_tskQpf.QuadPart;  // 1 ms, 1 s
    char line[256];
    NsRowsLine(line, sizeof(line), tk);
    assert(!strcmp(line, "1000,0,0,0,0,0,0,0,0,1000000"));

    memset(g_nsCls, 0, sizeof(g_nsCls)), memset(g_nsEnt, 0, sizeof(g_nsEnt)), memset(g_nsOutTk, 0, sizeof(g_nsOutTk));
    g_nsClsN = 0;
    g_nsOn = onSave, g_ubOn = ubSave, g_unCam.ok = camOk, g_utHeld = heldSave, g_utView = viewSave, b = bSave;
    printf("nonsim: lot table (order, count, growth), household lot / actor / unknown scope rows, world lot, owner rows, run sums, outside-run sums ok\n");
}
