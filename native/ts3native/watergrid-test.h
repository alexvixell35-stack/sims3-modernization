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
    free(cells);
    free(maps);
}

static void TestWaterGrid() {
    static_assert(sizeof(kWgSites) / sizeof(kWgSites[0]) == WG_N, "a site per phase");
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
    TestWaterGridAt(513, 512);    // a 2,048 m world (4 m cells)
    TestWaterGridAt(1025, 1024);  // a 4x-area world: sized at use, no fixed array
    memset(&ts3native_watergrid, 0, sizeof(ts3native_watergrid));
    printf("water grid prof: fingerprint parts, refusals, hook pass-through and report ok\n");
}
