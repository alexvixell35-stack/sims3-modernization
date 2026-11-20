// TestDxDiagCache (dxdiagcache.cpp / dxdiagvram.h, docs/284): the replayed VRAM_DxDiag decision and the sidecar round trip.
// Included by selftest.cpp.
static void TestDxDiagCache() {
    static_assert(offsetof(Ts3nCounters, ddcMode) == 0x6580 && offsetof(Ts3nCounters, ddcSavedMs) == 0x658C && sizeof(Ts3nCounters) == 6798 * 4,
                  "counters v207 (docs/registry.md)");
    // ((MB + 4) << 20 & 0xFF800000) >> 20: rounds to 8 MB, wraps at 4096 (docs/37 §6)
    assert(DdcValueFromMem(L"4096 MB") == 0 && DdcValueFromMem(L"6144 MB") == 2048 && DdcValueFromMem(L"8031 MB") == 3936);
    assert(DdcValueFromMem(L"1024 MB") == 1024 && DdcValueFromMem(L"1020 MB") == 1024 && DdcValueFromMem(L"1019 MB") == 1016);
    assert(DdcValueFromMem(L"") == 0 && DdcValueFromMem(L"n/a") == 0 && DdcValueFromMem(NULL) == 0 && DdcValueFromMem(L"1e40") == 0);
    // first matching GUID decides, even with an unusable memory string; GUID_NULL rows (not a BSTR) match only GUID_NULL
    static const GUID a = {0xD7B71E3E, 0x4340, 0x11CF, {0x0E, 0x6F, 0x80, 0x67, 0xA1, 0xC2, 0xC2, 0x35}};
    static const GUID b = {0x019674C3, 0xB54C, 0x5853, {0x6B, 0x6E, 0xA1, 0x25, 0x82, 0x9A, 0x74, 0x74}};
    DdcDev d[3] = {{L"{D7B71E3E-4340-11CF-0E6F-8067A1C2C235}", L"", true}, {L"{D7B71E3E-4340-11CF-0E6F-8067A1C2C235}", L"2048 MB", true},
                   {L"", L"512 MB", false}};
    assert(DdcResolve(d, 3, &a) == 0);
    assert(DdcResolve(d + 1, 2, &a) == 2048);
    assert(DdcResolve(d, 3, &b) == 0);           // the bridge's identity: no DxDiag device has it
    assert(DdcResolve(d, 3, &GUID_NULL) == 512);  // the game compares its GUID_NULL preset too
    // sidecar: written rows read back; another machine key misses
    wchar_t tmp[MAX_PATH], path[MAX_PATH];
    GetTempPathW(MAX_PATH, tmp);
    swprintf_s(path, L"%sts3n-ddc-selftest-%lu.ini", tmp, GetCurrentProcessId());
    DeleteFileW(path);
    assert(DdcWrite(path, L"00112233AABBCCDD", d, 3, 61234, L"helper"));
    DdcDev r[DDC_MAX];
    int n = 0;
    DWORD ms = 0;
    wchar_t by[16];
    assert(DdcRead(path, L"00112233AABBCCDD", r, &n, &ms, by, 16) && n == 3 && ms == 61234 && !wcscmp(by, L"helper"));
    assert(r[0].guidBstr && !r[2].guidBstr && !wcscmp(r[1].mem, L"2048 MB") && !wcscmp(r[2].mem, L"512 MB") && DdcResolve(r + 1, 2, &a) == 2048);
    assert(!DdcRead(path, L"FFFFFFFFFFFFFFFF", r, &n, &ms, by, 16));
    DeleteFileW(path);
    assert(!DdcRead(path, L"00112233AABBCCDD", r, &n, &ms, by, 16));
    wchar_t sig1[24], sig2[24];
    DdcSig(sig1, 24);
    DdcSig(sig2, 24);
    assert(wcslen(sig1) == 16 && !wcscmp(sig1, sig2));  // stable within a session
    printf("dxdiag cache: value, resolve, sidecar OK\n");
}
