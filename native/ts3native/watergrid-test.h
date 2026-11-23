// TestWaterGrid ([WaterGrid] Prof, watergrid.cpp, docs/100 §13). Included by selftest.cpp. A fake G with the docs/100 §2
// layout (sized at use: two grid sizes): the fingerprint parts move only with their own data, bad vectors and an unreadable
// G are refused, the phase hooks pass arguments and results through and time into their rows, the report line fills the
// export.
struct WgFakeVec { u8* b; u8* e; u8* cap; void* alloc; };
static_assert(sizeof(WgFakeVec) == 16, "EASTL vector shape");
static DWORD g_wgFakeArg;
static DWORD __fastcall WgFakeCells(void* g, void*, DWORD p) { g_wgFakeArg = p; Sleep(2); return (DWORD)(uintptr_t)g + 1; }

static void TestWaterGridAt(DWORD w, DWORD h) {
    static u8 g[0x7EC];
    memset(g, 0, sizeof(g));
    *(DWORD*)(g + 4) = w, *(DWORD*)(g + 8) = h, *(DWORD*)(g + 0x0C) = w * h, *(DWORD*)(g + 0x10) = 3, *(DWORD*)(g + 0x14) = 1;
    *(DWORD*)(g + 0x40) = 7, *(DWORD*)(g + 0x5C) = 2, *(DWORD*)(g + 0x78) = 5;
    u8* cells = (u8*)calloc(w * h, 2);
    u8* maps = (u8*)calloc(15, w * h);
    WgFakeVec pass[5], high[10];
    for (int i = 0; i < 5; i++) pass[i] = {maps + i * w * h, maps + (i + 1) * w * h, maps + (i + 1) * w * h, NULL};
    for (int i = 0; i < 10; i++) high[i] = {maps + (5 + i) * w * h, maps + (6 + i) * w * h, maps + (6 + i) * w * h, NULL};
    *(WgFakeVec*)(g + 0x1C) = {cells, cells + 2 * w * h, cells + 2 * w * h, NULL};
    *(WgFakeVec*)(g + 0x7B0) = {(u8*)pass, (u8*)(pass + 5), (u8*)(pass + 5), NULL};
    *(WgFakeVec*)(g + 0x7C0) = {(u8*)high, (u8*)(high + 10), (u8*)(high + 10), NULL};
    DWORD a[8], b[8];
    assert(WgHash(g, a) && a[3] == 2 * w * h && a[4] == (5u << 16 | 10) && a[5] == 7 && a[6] == 2 && a[7] == 5);
    assert(WgHash(g, b) && !memcmp(a, b, sizeof(a)));  // deterministic
    cells[2 * w * h - 1] = 0x80;  // last cell's flags: only the cells part moves
    assert(WgHash(g, b) && b[0] != a[0] && b[1] == a[1] && b[2] == a[2]);
    cells[2 * w * h - 1] = 0;
    maps[5 * w * h - 1] = 1;  // last passability byte
    assert(WgHash(g, b) && b[0] == a[0] && b[1] != a[1] && b[2] == a[2]);
    maps[5 * w * h - 1] = 0;
    maps[15 * w * h - 1] = 1;  // last high-cost byte (history marks, kept apart)
    assert(WgHash(g, b) && b[0] == a[0] && b[1] == a[1] && b[2] != a[2]);
    maps[15 * w * h - 1] = 0;
    g[0x39C + 0x3FF] = 9;  // body table
    assert(WgHash(g, b) && b[0] != a[0]);
    g[0x39C + 0x3FF] = 0;
    high[3].e = high[3].b - 1;  // end before begin
    assert(!WgHash(g, b));
    high[3].e = high[3].b + w * h;
    *(u8**)(g + 0x7B4) = (u8*)pass + 15;  // map vector not a whole number of elements
    assert(!WgHash(g, b));
    *(u8**)(g + 0x7B4) = (u8*)(pass + 5);
    assert(!WgHash((const u8*)(uintptr_t)0x10, b));  // unreadable G
    assert(WgHash(g, b) && !memcmp(a, b, sizeof(a)));
    // hooks: arguments and result pass through, the row times the call; the clear resets the build's rows
    const WgThis1 saved = g_wgCells;
    g_wgCells = WgFakeCells;
    memset(g_wgUs, 0, sizeof(g_wgUs));
    assert(WgCellsHook(g, NULL, 0x1234) == (DWORD)(uintptr_t)g + 1 && g_wgFakeArg == 0x1234 && g_wgUs[WG_CELLS] >= 1000);
    g_wgCells = saved;
    WaterGridCounters& c = ts3native_watergrid;
    const LONG n = c.builds;
    WgReport(g, NULL);
    assert(c.builds == n + 1 && (DWORD)c.hashCells == a[0] && (DWORD)c.hashPass == a[1] && (DWORD)c.hashHigh == a[2] && c.rSumA == 0);
    assert(c.us[WG_CELLS] == g_wgUs[WG_CELLS]);
    // [WaterGrid] Diff: the canonical hash ignores padding, pointers and unused body slots, and moves with real data
    DWORD k0, k1;
    assert(WgCanon(g, &k0));
    g[2] = 0x55, g[3] = 0xAA;                                       // padding after the two flag bytes
    *(DWORD*)(g + 0x39C + 8 * 5) = 0xDEAD;                          // body slot 5 > bodies (1): unused
    *(u8**)(g + 0x2C) = (u8*)(uintptr_t)0x12345678;                 // a tree pointer
    assert(WgHash(g, b) && b[0] != a[0] && WgCanon(g, &k1) && k1 == k0);
    *(DWORD*)(g + 0x39C + 8 * 1) = 7;                               // body 1's seed: in use
    assert(WgCanon(g, &k1) && k1 != k0);
    *(DWORD*)(g + 0x39C + 8 * 1) = 0;
    *(DWORD*)(g + 0x39C + 8 * 0x7F) = 3;                            // overflow slot: unused below 126 bodies, 0x7F cells or not
    cells[2 * 7] = 0x7F;                                            // a shoreline mark (0x66FD00)
    DWORD k2, k3;
    assert(WgCanon(g, &k1) && k1 != k0 && (*(DWORD*)(g + 0x39C + 8 * 0x7F) = 4, WgCanon(g, &k2)) && k2 == k1);
    *(DWORD*)(g + 0x14) = 0x7E;                                     // ids used up: the overflow slot is data
    assert(WgCanon(g, &k2) && (*(DWORD*)(g + 0x39C + 8 * 0x7F) = 5, WgCanon(g, &k3)) && k3 != k2);
    *(DWORD*)(g + 0x14) = 1;
    cells[2 * 7] = 0;
    *(DWORD*)(g + 0x39C + 8 * 0x7F) = 0;
    assert(WgCanon(g, &k1) && k1 == k0);
    // the dump diff names fields and splits the cells by byte
    static u8 ga[WG_GBYTES];
    memcpy(ga, g, WG_GBYTES);
    u8* ca = (u8*)malloc(2 * w * h);
    memcpy(ca, cells, 2 * w * h);
    g[2] = 0x11;
    cells[2 * (w + 3)] = 0x02, cells[2 * (w + 4) + 1] = 0x10, cells[2 * 9] = 0x7F, cells[2 * 9 + 1] = 0x01;
    WgDiffOut d = WgDiffDumps(ga, ca, g, cells, 2 * w * h, w);
    assert(d.fields == 1 && d.ptrFields == 0 && d.cells == 3 && d.lowOnly == 1 && d.highOnly == 1 && d.flagXor == 0x11);
    *(DWORD*)(g + 0x39C + 8 * 6) = 1;                               // unused slot
    d = WgDiffDumps(ga, ca, g, cells, 2 * w * h, w);
    assert(d.fields == 2 && d.bodySlots == 1 && d.bodySlotsUnused == 1);
    // sidecar round trip: first load writes, second load of the same key reads it back and diffs
    GetTempPathW(MAX_PATH, g_wgDir);
    wchar_t path[MAX_PATH];
    WgDumpPath(path, 0xFEEDF00D);
    DeleteFileW(path);
    assert(WgDiffAndDump(g, 0xFEEDF00D) && GetFileAttributesW(path) != INVALID_FILE_ATTRIBUTES);
    assert(WgDiffAndDump(g, 0xFEEDF00D));
    DeleteFileW(path);
    g_wgDir[0] = 0;
    memcpy(cells, ca, 2 * w * h);
    free(ca);
    free(cells);
    free(maps);
}

// [WaterGrid] Par: fake game callees (same conventions as the real ones) over a synthetic world. The parallel pass with
// 1..7 workers must equal our own serial run of the same branch order (cells, written set, water count), the deferral
// path must be taken (pond polygons and bridge boxes), and the entry/end stubs' C targets must apply (G gets the result,
// 0x66E040 once per written cell) and check (equal, then a corrupted cell = MISMATCH) on a fake loop frame.
static u8 g_wgfPond[0x500], g_wgfBridge[0x40], g_wgfBox[0x20];
static void* g_wgfPonds[1] = {g_wgfPond};
static void* g_wgfBridges[1] = {g_wgfBridge};
static volatile LONG g_wgfPassCalls;
static u8 __fastcall WgfRoutable(void*, void*, float x, float z) { return ((int)x + (int)z) % 37 >= 2; }
static double __fastcall WgfHeight(void*, void*, float x, float z, void*) { return 10.0 + ((int)(x * 7 + z * 3) % 41) * 0.1; }
static u8 __fastcall WgfPoly(void* p, void*, float x, float z) { return p == g_wgfPond + 0x480 && x >= 10 && x < 14 && z >= 20 && z < 30; }
static void* __fastcall WgfPondFull(void*, void*, float x, float z, DWORD) {
    return WgfPoly(g_wgfPond + 0x480, NULL, x, z) && ((int)x & 1) ? g_wgfPond : NULL;
}
static u8 __fastcall WgfBridgeFull(void* b, void*, float x, float z, DWORD, DWORD, DWORD, DWORD) {
    const u8* box = *(const u8* const*)((const u8*)b + 0x38);
    return x >= *(const float*)(box + 0x10) && z >= *(const float*)(box + 0x14) && *(const float*)(box + 0x18) > x &&
           *(const float*)(box + 0x1C) > z && z < 45;
}
static void* __fastcall WgfLot(void*, void*, float x, float z) { return x > 50 && z < 40 ? (void*)1 : NULL; }
static void __fastcall WgfPass(void*, void*, DWORD) { InterlockedIncrement(&g_wgfPassCalls); }

static void WgfFill(WORD* cells, DWORD count, DWORD w) {
    for (DWORD i = 0; i < count; i++) cells[i] = (i % w == w - 1) ? 0 : 0x0080;  // the build's fill: water, last column zeroed
}

static void TestWaterGridPar() {
    const WgRoutableFn r0 = g_wgRoutable;
    const WgPondFn p0 = g_wgPond;
    const WgPolyFn y0 = g_wgPoly;
    const WgBridgeFn b0 = g_wgBridge;
    const WgLotFn l0 = g_wgLot;
    const WgPassFn s0 = g_wgPass;
    const DWORD h0 = g_wgHeightFn;
    g_wgRoutable = WgfRoutable, g_wgPond = WgfPondFull, g_wgPoly = WgfPoly, g_wgBridge = WgfBridgeFull, g_wgLot = WgfLot, g_wgPass = WgfPass;
    g_wgHeightFn = (DWORD)(uintptr_t)WgfHeight;
    DWORD n = 0;
    float* ax = WgAxis(0.0f, 8.0f, &n);  // the game's accumulation: start + 0.5, step 1, while max > v
    assert(ax && n == 8 && ax[0] == 0.5f && ax[7] == 7.5f);
    free(ax);
    static u8 wm[0x200];
    memset(wm, 0, sizeof(wm));
    const float W = 128, H = 96;  // metres; w = W/4 + 1 columns, as the game sizes G
    *(float*)(wm + 0xB4) = 0, *(float*)(wm + 0xB8) = 0, *(float*)(wm + 0xBC) = W, *(float*)(wm + 0xC0) = H;
    *(void***)(wm + 0x100) = g_wgfPonds, *(void***)(wm + 0x104) = g_wgfPonds + 1;
    *(void**)(wm + 0x54) = wm;
    *(u8**)(g_wgfBridge + 0x38) = g_wgfBox;
    *(float*)(g_wgfBox + 0x10) = 60, *(float*)(g_wgfBox + 0x14) = 30, *(float*)(g_wgfBox + 0x18) = 70, *(float*)(g_wgfBox + 0x1C) = 50;
    const DWORD w = (DWORD)W / 4 + 1, h = (DWORD)H / 4, count = w * h;
    static u8 g[0x7EC];
    memset(g, 0, sizeof(g));
    WORD* cells = (WORD*)malloc(2 * count);
    WgfFill(cells, count, w);
    *(DWORD*)(g + 4) = w, *(DWORD*)(g + 8) = h, *(DWORD*)(g + 0x0C) = count, *(LONG*)(g + 0x10) = (LONG)(count - h);
    *(WORD**)(g + 0x1C) = cells, *(WORD**)(g + 0x20) = cells + count;
    static u8 sp[0x100];
    memset(sp, 0, sizeof(sp));
    *(void**)(sp + 0x18) = (void*)0x1234, *(float*)(sp + 0x24) = 13.0f;
    *(void***)(sp + 0x80) = g_wgfBridges, *(void***)(sp + 0x84) = g_wgfBridges + 1;
    // reference: our own serial run with the full calls
    WgParCtx ref;
    const char* why = "";
    assert(WgParSetup(ref, wm, g, sp, &why) && ref.nx == (DWORD)W && ref.nz == (DWORD)H);
    LONG refDec = 0;
    for (DWORD i = 0; i < ref.nx; i++)
        for (DWORD j = 0; j < ref.nz; j++) assert(WgSample(ref, ref.xs[i], ref.cxs[i], ref.zs[j], false, &refDec) == WGS_DONE);
    DWORD land = 0, shallow = 0, lot = 0, bridge = 0, nw = 0;
    for (DWORD i = 0; i < count; i++) {
        land += (ref.cells[i] & 0xFF) == 0, shallow += (ref.cells[i] >> 8 & 0x1E) == 0x1E, lot += (ref.cells[i] >> 8 & 1);
        bridge += (ref.cells[i] >> 8 & 0x1E) == 0x1C, nw += ref.written[i];
    }
    assert(refDec > 0 && land > 0 && shallow > 0 && lot > 0 && bridge > 0 && nw > 0 && land < count);  // every branch taken
    for (int workers = 1; workers <= 7; workers += 2) {
        WgParCtx c;
        assert(WgParSetup(c, wm, g, sp, &why));
        LONG dec = 0;
        DWORD deferred = 0;
        assert(WgParRun(c, workers, &dec, &deferred, &why));
        assert(dec == refDec && deferred > 0 && !memcmp(c.cells, ref.cells, 2 * count) && !memcmp(c.written, ref.written, count));
        WgParFree(c);
    }
    // apply: the entry target writes G, the water count and one 0x66E040 per written cell
    WaterGridCounters& k = ts3native_watergrid;
    g_wgParMode = 2, g_wgParWorkers = 3, g_wgfPassCalls = 0;
    const LONG water0 = *(LONG*)(g + 0x10);
    WgParEntry(wm, g, sp);
    assert(g_wgParTook == 1 && !memcmp(cells, ref.cells, 2 * count) && *(LONG*)(g + 0x10) == water0 - refDec && g_wgfPassCalls == (LONG)nw);
    // a lot grid the lookup does not clean: fallback, G untouched
    WgfFill(cells, count, w);
    *(LONG*)(g + 0x10) = water0;
    wm[0x1CC] = 1;
    const LONG fb = k.parFallback;
    WgParEntry(wm, g, sp);
    assert(g_wgParTook == 0 && k.parFallback == fb + 1 && cells[0] == 0x0080 && *(LONG*)(g + 0x10) == water0);
    wm[0x1CC] = 0;
    // check: entry snapshots, the "game" writes the serial result, the end target compares
    g_wgParMode = 1;
    const LONG mm = k.parMismatch;
    WgParEntry(wm, g, sp);
    assert(g_wgParTook == 0 && g_wgPar.armed);
    memcpy(cells, ref.cells, 2 * count), *(LONG*)(g + 0x10) = water0 - refDec;
    WgParEnd(g, sp);
    assert(k.parMismatch == mm && !g_wgPar.armed);
    WgfFill(cells, count, w);
    *(LONG*)(g + 0x10) = water0;
    WgParEntry(wm, g, sp);
    memcpy(cells, ref.cells, 2 * count), *(LONG*)(g + 0x10) = water0 - refDec;
    cells[w + 5] ^= 0x0100;  // one flag bit the parallel pass would not set
    WgParEnd(g, sp);
    assert(k.parMismatch == mm + 1);
    WgParEnd(g, sp);  // not armed: nothing
    assert(k.parMismatch == mm + 1);
    WgParFree(ref);
    free(cells);
    g_wgParMode = 0;
    g_wgRoutable = r0, g_wgPond = p0, g_wgPoly = y0, g_wgBridge = b0, g_wgLot = l0, g_wgPass = s0, g_wgHeightFn = h0;
    static_assert(sizeof(kWgParEntryOld) == 8 && sizeof(kWgParEndOld) == 6, "stub replay lengths (docs/100 §14.4)");
}

static void TestWaterGrid() {
    static_assert(sizeof(kWgSites) / sizeof(kWgSites[0]) == WG_N, "a site per phase");
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
    TestWaterGridAt(513, 512);    // a 2,048 m world (4 m cells)
    TestWaterGridAt(1025, 1024);  // a 4x-area world: sized at use, no fixed array
    TestWaterGridPar();
    memset(&ts3native_watergrid, 0, sizeof(ts3native_watergrid));
    printf("water grid prof: fingerprint parts, refusals, hook pass-through and report ok; diff + canonical hash ok; "
           "par pass = serial (1-7 workers), apply, check, fallback ok\n");
}
