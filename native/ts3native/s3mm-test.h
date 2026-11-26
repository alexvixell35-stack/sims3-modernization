// TestS3mm ([Compat] S3mmStandDown, s3mm.cpp; Satb act refusal, satb.cpp; docs/291 §6). Included by selftest.cpp. No S3MM
// binary: a loaded system DLL stands in for the module, an empty file in a temp folder for the ASI on disk, and fake code pages
// with the game's prologues (one with a foreign jump) stand in for the Satb sites.
static void TestS3mm() {
    // 1. detection: nothing / module loaded / file in a scripts subfolder of a fake Bin
    const wchar_t* mod0 = g_s3mmModule;
    wchar_t tmp[MAX_PATH], bin[MAX_PATH], p[MAX_PATH];
    GetTempPathW(MAX_PATH, tmp);
    swprintf_s(bin, L"%sts3n-s3mm-%lu\\", tmp, GetCurrentProcessId());
    CreateDirectoryW(bin, NULL);
    swprintf_s(p, L"%sscripts", bin);
    CreateDirectoryW(p, NULL);
    g_s3mmModule = L"ts3n-no-such-s3mm.asi";
    assert(S3mmProbe(bin) == 0);
    g_s3mmModule = L"kernel32.dll";
    assert(S3mmProbe(bin) == 1);
    g_s3mmModule = L"ts3n-s3mm-test.asi";
    swprintf_s(p, L"%sscripts\\ts3n-s3mm-test.asi", bin);
    HANDLE h = CreateFileW(p, GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, 0, NULL);
    assert(h != INVALID_HANDLE_VALUE);
    CloseHandle(h);
    assert(S3mmProbe(bin) == 2);
    DeleteFileW(p);
    assert(S3mmProbe(bin) == 0);
    swprintf_s(p, L"%sscripts", bin);
    RemoveDirectoryW(p);
    RemoveDirectoryW(bin);
    g_s3mmModule = mod0;

    // 2. stand-down decisions: not found / found + S3mmStandDown=0 / found + 1 (counted)
    const bool sd0 = g_s3mmStandDown;
    const int f0 = g_s3mmFound, st0 = g_s3mmStood;
    g_s3mmFound = 0, g_s3mmStandDown = true;
    assert(!S3mmStandsDown("[MonoJit]"));
    g_s3mmFound = 1, g_s3mmStandDown = false;
    assert(!S3mmStandsDown("[MonoJit]"));
    g_s3mmStandDown = true;
    assert(S3mmStandsDown("[MonoJit]") && S3mmStandsDown("[MonoFast] CallPath") && g_s3mmStood == st0 + 2);
    g_s3mmFound = f0, g_s3mmStandDown = sd0, g_s3mmStood = st0;

    // 3. Satb act refusal (docs/291 §4.1): a foreign jump at the binder site = that detour skipped = act refused
    void* const next0 = g_satbBindNext;
    const bool stDone0 = g_satbStDone;
    const int mask0 = g_satbNatMask;
    DWORD at0[4];
    memcpy(at0, g_satbNatAt, sizeof(at0));
    for (int foreign = 1; foreign >= 0; foreign--) {
        u8* m = (u8*)VirtualAlloc(NULL, 0x1000, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
        assert(m);
        memset(m, 0xCC, 0x1000);
        memcpy(m, kSatbBind, sizeof(kSatbBind));
        if (foreign) m[0] = 0xE9, *(DWORD*)(m + 1) = 0x12345678;  // what another mod's detour leaves there
        memcpy(m + 0x100, kSatbStSet, 8), memcpy(m + 0x200, kSatbStAddr, 8), memcpy(m + 0x300, kSatbStGet, 8);
        const DWORD b = (DWORD)(uintptr_t)m;
        const DWORD at[4] = {b, b + 0x100, b + 0x200, b + 0x300};
        memcpy(g_satbNatAt, at, sizeof(at));
        g_satbBindNext = NULL, g_satbStDone = false, g_satbNatMask = 0;
        const int n = SatbInstallNative();
        if (foreign) {
            assert(n == 3 && g_satbNatMask == 0xE && !g_satbBindNext && m[0] == 0xE9 && *(DWORD*)(m + 1) == 0x12345678);
            assert(SatbActMissing(g_satbNatMask) && !strcmp(SatbActMissing(g_satbNatMask), "binder bodies 0xE525C0"));
        } else {
            assert(n == 4 && g_satbNatMask == 0xF && g_satbBindNext && m[0] == 0xE9 && !SatbActMissing(g_satbNatMask));
        }
        // the fake pages stay mapped: the trampolines jump back into them (never run)
    }
    assert(!strcmp(SatbActMissing(0xF & ~4), "static address 0xD89C60") && !strcmp(SatbActMissing(0), "binder bodies 0xE525C0"));
    memcpy(g_satbNatAt, at0, sizeof(at0));
    g_satbBindNext = next0, g_satbStDone = stDone0, g_satbNatMask = mask0;
    puts("s3mm compat ok");
}
