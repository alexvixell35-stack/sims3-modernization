// TestKeepOptions ([EaCrashFixes] KeepOptionsOnGpuChange, docs/37 §12): KoStub run on a fake DeviceConfig (isNewDevice at
// +0x74, new-device string object at +0x64, Options.ini path object at +0xF4) and fake option records (key at +0x10), with
// EA's two jump targets replaced by stand-ins that report which path was taken.

// EA 0x58E4B5: read the ini value
static __declspec(naked) int KoTestRead() {
    __asm {
        mov eax, 1
        ret
    }
}
// EA 0x58E4F8: keep the sgr default
static __declspec(naked) int KoTestDefault() {
    __asm {
        mov eax, 2
        ret
    }
}
static __declspec(naked) int __cdecl KoTestCall(const u8* /*cfg*/, const u8* /*rec*/, LONG /*left*/) {
    __asm {
        push ebp
        push esi
        push edi
        mov edi, [esp + 16]
        mov esi, [esp + 20]
        mov ebp, [esp + 24]
        call KoStub
        pop edi
        pop esi
        pop ebp
        ret
    }
}

static void TestKeepOptions() {
    static_assert(offsetof(Ts3nCounters, resV215_218) == 0x6908 && offsetof(Ts3nCounters, koMode) == 0x6A08 &&
                      offsetof(Ts3nCounters, koRedetected) == 0x6A14 && sizeof(Ts3nCounters) == 6866 * 4,
                  "counters v219 layout after the v215-v218 placeholder (docs/registry.md)");
    Ts3nCounters& k = ts3native_counters;
    const DWORD sCont = g_koCont, sSkip = g_koSkip;
    g_koCont = (DWORD)(uintptr_t)KoTestRead, g_koSkip = (DWORD)(uintptr_t)KoTestDefault;

    wchar_t path[MAX_PATH];
    GetTempPathW(MAX_PATH, path);
    wcscat_s(path, L"ts3n-keepopts-test.ini");
    WritePrivateProfileStringW(L"options", L"lastdevice", L"0;10de;1080;65535", path);
    static const wchar_t kNew[] = L"0;10de;1c82;65535";
    static u8 cfg[0x100];
    memset(cfg, 0, sizeof(cfg));
    *(const wchar_t**)(cfg + 0x64) = kNew;
    *(const wchar_t**)(cfg + 0xF4) = path;
    const wchar_t* keys[] = {L"agingstagelengthadult", L"texturequality", L"enablepets", L"EdgeSmoothing", L"twelvehourclock", NULL};
    u8 rec[6][0x3C] = {};
    for (int i = 0; i < 6; i++) *(const wchar_t**)(rec[i] + 0x10) = keys[i];

    // (a) same device: every option reads the ini (EA's path), KoKeep never runs
    k.koKept = k.koRedetected = k.koFires = 0;
    for (int i = 0; i < 6; i++) assert(KoTestCall(cfg, rec[i], 6 - i) == 1);
    assert(k.koKept == 0 && k.koRedetected == 0 && k.koFires == 0);

    // (b) new device: gameplay keys read the ini, hardware-detected keys (case-insensitive) and an unreadable key keep the default
    cfg[0x74] = 1;
    const int want[6] = {1, 2, 1, 2, 1, 2};
    for (int i = 0; i < 6; i++) assert(KoTestCall(cfg, rec[i], 6 - i) == want[i]);
    assert(k.koKept == 3 && k.koRedetected == 3 && k.koFires == 1);  // one log line, on the last record

    // (c) the predicate covers every listed key, and the string reader survives a bad pointer
    for (const wchar_t* d : kKoDetected) assert(KoIsDetected(d));
    assert(!KoIsDetected(L"lastdevice") && !KoIsDetected(L"springlength") && !KoIsDetected(L"enablevampires"));
    wchar_t buf[8];
    static u8 bad[4];
    *(DWORD*)bad = 0x10;  // unreadable begin pointer
    KoCopy(buf, 8, bad);
    assert(buf[0] == 0);
    KoCopy(buf, 8, cfg + 0x64);  // truncated, still terminated
    assert(wcslen(buf) == 7);

    DeleteFileW(path);
    g_koCont = sCont, g_koSkip = sSkip;
    k.koKept = k.koRedetected = k.koFires = 0;
    printf("keep options on GPU change ok\n");
}
