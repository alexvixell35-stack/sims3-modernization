// TestMemAccess ([MemDiag] Access, memaccess.cpp, docs/190 phase 2). Included by selftest.cpp after memdiag-test.h (uses
// UdtLog). HyperLogLog accuracy and duplicates, the install on a fake cache vtable (the read detour refuses here: no game
// code), gets vs owner scans (out = NULL) and the fixed rc-first order, reads through the hooks, the line's deltas and
// window reset, the counters.
static DWORD g_matFs;  // what the fake read saw
static LONG g_matFly;
static bool __fastcall MatRead(void*, void*, DWORD, DWORD, DWORD fs, DWORD ms, void*) {
    g_matFs = fs, g_matFly = g_maFlyKB;
    return ms != 7;  // memSize 7 = a failed read
}
// fake Find: a hit when the key exists and its last dword is 0 (any cache) or names this cache; out may be NULL (owner scan)
static bool __fastcall MatFind(void* self, void*, const void* key, void** out) {
    const DWORD* k = (const DWORD*)key;
    const bool hit = k && (!k[3] || k[3] == (DWORD)(uintptr_t)self);
    if (out) *out = hit ? (void*)key : NULL;
    return hit;
}
static void TestMemAccess() {
    static_assert(offsetof(Ts3nCounters, maOn) == 0x6354 && offsetof(Ts3nCounters, maLines) == 0x6378 && sizeof(Ts3nCounters) == 6722 * 4,
                  "counters v199 layout after v919 (docs/registry.md)");
    // sketch: distinct counts within 3 standard errors (3 x 3.3%), duplicates add nothing, an empty sketch is 0
    {
        static MaHll s;
        const DWORD ns[] = {0, 1, 4, 50, 1000, 3000, 30000, 300000};
        for (DWORD n : ns) {
            memset((void*)&s, 0, sizeof(s));
            for (int rep = 0; rep < 2; rep++)
                for (DWORD i = 0; i < n; i++) MaHllAdd(s, i * 0x9E3779B1u ^ 0x27D4EB2Fu);
            const double e = MaHllCount(s), err = n ? fabs(e - n) / n : e;
            if (!(err <= (n < 100 ? 0.05 : 0.10))) printf("mem access: hll n=%lu est=%.1f\n", n, e);
            assert(n < 100 ? fabs(e - n) < 0.5 + 0.05 * n : err <= 0.10);
        }
    }

    wchar_t tmp[MAX_PATH], lp[MAX_PATH], ini[MAX_PATH];
    GetTempPathW(MAX_PATH, tmp);
    swprintf_s(lp, L"%sts3n-matest-%lu.log", tmp, GetCurrentProcessId());
    swprintf_s(ini, L"%sts3n-matest-%lu.ini", tmp, GetCurrentProcessId());
    HANDLE h = CreateFileW(lp, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ, NULL, CREATE_ALWAYS, FILE_FLAG_DELETE_ON_CLOSE, NULL);
    assert(h != INVALID_HANDLE_VALUE);
    const HANDLE saved = g_log;
    g_log = h;
    static char buf[1 << 15];
    Ts3nCounters& k = ts3native_counters;
    const bool wasOn = g_mdOn;
    const DWORD savedVt = g_maRcVt, savedTid = g_mainTid;
    const u8* const* savedMgr = g_mdRcMgrSlot;

    // off: Access=0 (the default) and Access=1 without [MemDiag] install nothing
    static DWORD vt[8] = {};
    vt[6] = (DWORD)(uintptr_t)&MatFind;
    g_maRcVt = (DWORD)(uintptr_t)vt;
    g_mdOn = true;
    WritePrivateProfileStringW(L"MemDiag", L"Access", L"0", ini);
    ApplyMemAccess(ini);
    assert(!g_maOn && k.maOn == 0 && vt[6] == (DWORD)(uintptr_t)&MatFind);
    WritePrivateProfileStringW(L"MemDiag", L"Access", L"1", ini);
    g_mdOn = false;
    ApplyMemAccess(ini);
    assert(!g_maOn && vt[6] == (DWORD)(uintptr_t)&MatFind);

    // on: the slot now points at our hook and chains the old value; the read detour skips (no game prologue here)
    g_mdOn = true;
    ApplyMemAccess(ini);
    DeleteFileW(ini);
    assert(g_maOn && k.maOn == 1 && vt[6] == (DWORD)(uintptr_t)&MaFind && g_maFind == (MaFindFn)&MatFind && !g_maRead);
    assert(k.maRcNs > 0 && k.maRcNs < 2000 && k.maRdNs > 0);
    UdtLog(buf, sizeof(buf), h);
    assert(strstr(buf, "mem access on (docs/190 phase 2): cache finds (slot 6 of 0x") && strstr(buf, "), reads off (0x004A8380); our work "));

    // a fake manager: cache A priority 0 (rc), cache B priority 1000 (anim)
    static u8 cacheA[16], cacheB[16], mgr[0xA8];
    static DWORD arr[4];
    arr[0] = (DWORD)(uintptr_t)cacheA, arr[1] = 0, arr[2] = (DWORD)(uintptr_t)cacheB, arr[3] = 1000;
    *(DWORD**)(mgr + 0xA0) = arr, *(DWORD**)(mgr + 0xA4) = arr + 4;
    static const u8* mgrP;
    mgrP = mgr;
    g_mdRcMgrSlot = &mgrP;
    const MaFindFn find = (MaFindFn)(uintptr_t)vt[6];
    static DWORD keys[4][4] = {{1, 0, 0x00B2D882, 0}, {2, 0, 0x00B2D882, 0}, {3, 0, 0x2F7D0004, 0}, {1, 0, 0x2F7D0004, 0}};
    void* out = NULL;
    assert(find(cacheB, NULL, keys[0], &out));  // anim asked first: it takes slot 0, the line still lists rc first
    for (int i = 0; i < 8; i++) assert(find(cacheA, NULL, keys[i & 3], &out) && out == keys[i & 3]);  // 8 hits, 4 distinct keys
    assert(!find(cacheA, NULL, NULL, &out) && !find(cacheA, NULL, NULL, &out));                         // 2 misses, no key
    // owner scans (0x4B0070 pattern: the same key asked of every cache, out = NULL) for 5 other keys owned by rc: anim
    // misses, rc hits. Counted apart and kept out of the key sketches; run memdiag2-access-1115 merged them into the gets,
    // so both caches showed the scanned key count and anim a near-0 hit rate.
    static DWORD sk[5][4];
    for (int i = 0; i < 5; i++) {
        sk[i][0] = 100 + i, sk[i][2] = 0x6B20C4F3, sk[i][3] = (DWORD)(uintptr_t)cacheA;
        assert(!find(cacheB, NULL, sk[i], NULL) && find(cacheA, NULL, sk[i], NULL));
    }

    // reads: 1 MB raw (bit 31 set, as some DBPF index entries carry) + 3 MB decoded; a failed small one
    g_maRead = &MatRead;
    g_mainTid = GetCurrentThreadId();
    static u8 rb[16];
    assert(MaRead(NULL, NULL, 0x5A42, 0x1000, 0x80000000u | (1u << 20), 3u << 20, rb));
    assert(g_matFs == (0x80000000u | (1u << 20)) && g_matFly == 4096 && g_maFlyKB == 0);  // the game gets fs unmasked
    assert(!MaRead(NULL, NULL, 0, 0x2000, 100, 7, rb));

    MaLine(5, "save-start");  // not a memaccess sample
    MaLine(5, "periodic");
    UdtLog(buf, sizeof(buf), h);
    assert(!strstr(buf, "diag: memaccess #5 save-start"));
    const char* l1 = strstr(buf, "diag: memaccess #5 periodic win=");
    assert(l1 && strstr(l1, " rc get=10 hit=80.0 miss=2 keys=4 scan=5 scanHit=100.0 anim get=1 hit=100.0 miss=0 "
                            "keys=1 scan=5 scanHit=0.0 rd n=2 MB=3.0 rawMB=1.0 maxMB=4.0 flyMaxMB=4.0 big=1 ms=") &&
           strstr(l1, " main=100.0 fail=1 ourMs=") && !strstr(l1, " other "));
    assert(strchr(strstr(l1, " win=") + 1, ' ') == strstr(l1, " rc get="));  // rc is the first cache on the line
    assert(k.maRcGets == 11 && k.maRcHits == 9 && k.maRdN == 2 && k.maRdMB == 3 && k.maRdMaxKB == 4096 && k.maRdFlyMaxKB == 4096 && k.maLines == 1);

    // the next window starts empty (sketch and window maxima reset); session maxima in the counters stay
    assert(find(cacheA, NULL, keys[2], &out));
    mgrP = NULL;  // exit: names come from the last line, the manager is not read
    MaLine(6, "exit");
    mgrP = mgr;
    UdtLog(buf, sizeof(buf), h);
    const char* l2 = strstr(buf, "diag: memaccess #6 exit win=");
    assert(l2 && strstr(l2, " rc get=1 hit=100.0 miss=0 keys=1 scan=0 scanHit=0.0 anim get=0 hit=0.0 miss=0 keys=0 scan=0 scanHit=0.0 rd n=0 MB=0.0 rawMB=0.0 maxMB=0.0 flyMaxMB=0.0 big=0 "));
    assert(k.maRcGets == 12 && k.maRdMaxKB == 4096 && k.maLines == 2);

    // a 5th cache object goes to "other"; an unlisted cache is named by its address
    static u8 extra[4][16];
    for (auto& e : extra) find(e, NULL, keys[0], &out);
    MaLine(7, "mark");
    UdtLog(buf, sizeof(buf), h);
    const char* l3 = strstr(buf, "diag: memaccess #7 mark win=");
    assert(l3 && strstr(l3, " get=1 hit=100.0 miss=0 keys=1 scan=0 scanHit=0.0 other get=2 hit=100.0 miss=0 keys=1 scan=0 scanHit=0.0 rd n=0 ") && strstr(l3, " c"));

    // undo: the slot back, state cleared
    vt[6] = (DWORD)(uintptr_t)&MatFind;
    g_maOn = false, g_maFind = NULL, g_maRead = NULL, g_maRcVt = savedVt, g_mainTid = savedTid, g_mdRcMgrSlot = savedMgr, g_mdOn = wasOn;
    memset((void*)g_maSelf, 0, sizeof(g_maSelf)), memset((void*)g_maGet, 0, sizeof(g_maGet)), memset((void*)g_maHit, 0, sizeof(g_maHit));
    memset((void*)g_maKeys, 0, sizeof(g_maKeys)), memset(g_maPrevGet, 0, sizeof(g_maPrevGet)), memset(g_maPrevHit, 0, sizeof(g_maPrevHit));
    memset((void*)g_maScan, 0, sizeof(g_maScan)), memset((void*)g_maScanHit, 0, sizeof(g_maScanHit)), memset(g_maPrevScan, 0, sizeof(g_maPrevScan));
    memset(g_maPrevScanHit, 0, sizeof(g_maPrevScanHit));
    g_maRdN = g_maRdBig = g_maRdMain = g_maRdFail = g_maFlyKB = g_maRdMaxKB = g_maFlyMaxKB = 0, g_maRdMem = g_maRdRaw = g_maRdTicks = 0;
    g_maPrevRdN = g_maPrevBig = g_maPrevMain = g_maPrevFail = 0, g_maPrevMem = g_maPrevRaw = g_maPrevTicks = 0;
    g_maPrevMs = 0, g_maSessRdMaxKB = g_maSessFlyMaxKB = 0, g_maLineN = 0;
    memset(g_maNames, 0, sizeof(g_maNames));
    memset(&k.maOn, 0, 10 * sizeof(LONG));
    g_log = saved;
    CloseHandle(h);
    printf("mem access: sketch counts (0-300k distinct, duplicates), install on a fake cache vtable (chained slot, read detour skipped "
           "without the game), gets by cache + keys, owner scans apart, rc first, reads (sizes, in flight, bit 31, failures), line deltas + window reset, counters ok\n");
}
