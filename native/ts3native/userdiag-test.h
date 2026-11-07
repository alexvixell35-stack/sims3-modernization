// TestUserDiag ([UserDiag], userdiag.cpp, docs/188). Included by selftest.cpp. Pure helpers plus the save trail and the
// ts3fixes text channel against a temp log file.
static size_t UdtLog(char* buf, size_t cap, HANDLE h) {  // whole temp log
    LARGE_INTEGER z = {};
    SetFilePointerEx(h, z, NULL, FILE_BEGIN);
    DWORD r = 0;
    ReadFile(h, buf, (DWORD)cap - 1, &r, NULL);
    buf[r] = 0;
    SetFilePointerEx(h, z, NULL, FILE_END);
    return r;
}

static void TestUserDiag() {
    // install class
    assert(UdInstallClass(L"D:\\SteamLibrary\\steamapps\\common\\The Sims 3\\Game\\Bin\\TS3W.exe") == UD_STEAM);
    assert(UdInstallClass(L"C:\\Program Files\\EA Games\\The Sims 3\\Game\\Bin\\TS3W.exe") == UD_EAAPP);
    assert(UdInstallClass(L"C:\\Program Files (x86)\\Origin Games\\The Sims 3\\Game\\Bin\\TS3W.exe") == UD_ORIGIN);
    assert(UdInstallClass(L"C:\\Program Files (x86)\\Electronic Arts\\The Sims 3\\Game\\Bin\\TS3W.exe") == UD_RETAIL);
    assert(UdInstallClass(L"E:\\Games\\TS3\\Game\\Bin\\TS3W.exe") == UD_OTHER);

    // DBPF index: per-entry types, constant type, bad header, short index
    static u8 hdr[96], idx[4 + 32 * 3];
    memcpy(hdr, "DBPF", 4);
    *(DWORD*)(hdr + 4) = 2;
    *(DWORD*)(hdr + 0x24) = 3;
    memset(idx, 0, sizeof(idx));
    *(DWORD*)(idx + 4) = 0x0333406C, *(DWORD*)(idx + 36) = kUdS3SA, *(DWORD*)(idx + 68) = kUdS3SA;
    assert(UdDbpfCount(hdr, 96, idx, sizeof(idx), kUdS3SA) == 2);
    assert(UdDbpfCount(hdr, 96, idx, sizeof(idx) - 1, kUdS3SA) == -1);  // index shorter than 3 entries
    *(DWORD*)idx = 5;  // type and instance high constant: 8 header bytes, 24-byte entries
    *(DWORD*)(idx + 4) = kUdS3SA;
    assert(UdDbpfCount(hdr, 96, idx, 12 + 24 * 3, kUdS3SA) == 3 && UdDbpfCount(hdr, 96, idx, 12 + 24 * 3, 0x1234) == 0);
    hdr[4] = 1;
    assert(UdDbpfCount(hdr, 96, idx, sizeof(idx), kUdS3SA) == -1);
    hdr[4] = 2, hdr[0] = 'X';
    assert(UdDbpfCount(hdr, 96, idx, sizeof(idx), kUdS3SA) == -1);

    // log capture
    wchar_t tmp[MAX_PATH], lp[MAX_PATH];
    GetTempPathW(MAX_PATH, tmp);
    swprintf_s(lp, L"%sts3n-udtest-%lu.log", tmp, GetCurrentProcessId());
    HANDLE h = CreateFileW(lp, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ, NULL, CREATE_ALWAYS, FILE_FLAG_DELETE_ON_CLOSE, NULL);
    assert(h != INVALID_HANDLE_VALUE);
    const HANDLE saved = g_log;
    g_log = h;
    static char buf[65536];

    // list packing: every line under the Log() buffer, items kept whole
    UdList l;
    UdListBegin(l, "diag: test:");
    char item[64];
    for (int i = 0; i < 100; i++) { sprintf_s(item, "NRaas_Module%03d_V123.package", i); UdListAdd(l, item); }
    UdListFlush(l);
    UdtLog(buf, sizeof(buf), h);
    assert(l.items == 100 && l.lines >= 7 && strstr(buf, "NRaas_Module000_V123.package") && strstr(buf, "NRaas_Module099_V123.package"));
    for (char* s = buf; *s;) {
        char* e = strchr(s, '\n');
        assert(e && e - s < 510);
        s = e + 1;
    }

    // Resource.cfg PackedFile patterns
    const char cfg[] = "Priority 500\r\nPackedFile Packages/*.package\r\n  PackedFile Packages/*/*.package\r\nDirectoryFiles Files/... autoupdate\r\n";
    UdListBegin(l, "diag: cfg:");
    assert(UdCfgPatterns(cfg, sizeof(cfg) - 1, l) == 2 && !strcmp(l.b, "Packages/*.package, Packages/*/*.package"));
    l.n = 0;

    // ts3fixes text channel: begin, 4 bytes per message, non-printables replaced, end logs; off answers 0
    const bool wasOn = g_udOn;
    g_udOn = false;
    assert(UdTextMsg(0, kUdTextHi) == 0);
    g_udOn = true;
    const char* line = "switches on: a\tb";
    assert(UdTextMsg(0, kUdTextHi | 0 << 16) == 1);
    for (size_t i = 0; i < strlen(line); i += 4) {
        DWORD v = 0;
        for (size_t k = 0; k < 4 && i + k < strlen(line); k++) v |= (DWORD)(u8)line[i + k] << (8 * k);
        assert(UdTextMsg(v, kUdTextHi | 1 << 16) == 1);
    }
    assert(UdTextMsg(0, kUdTextHi | 2 << 16) == 1 && UdTextMsg(0, kUdTextHi | 9 << 16) == 0);
    assert(UdTextMsg(0, kUdTextHi | 8 << 16) == 0);  // [AutShadow] Unseen off
    g_asLiveUnseen = 1;
    assert(UdTextMsg(0, kUdTextHi | 8 << 16) == 1);
    g_asLiveUnseen = 0;
    UdtLog(buf, sizeof(buf), h);
    assert(strstr(buf, "ts3fixes: switches on: a?b\r\n"));
    // hitch-cause lines survive the shared cap (docs/247 §11.1), other lines stop there
    {
        const auto send = [](const char* s) {
            UdTextMsg(0, kUdTextHi | 0 << 16);
            for (size_t i = 0; i < strlen(s); i += 4) {
                DWORD v = 0;
                for (size_t k = 0; k < 4 && i + k < strlen(s); k++) v |= (DWORD)(u8)s[i + k] << (8 * k);
                UdTextMsg(v, kUdTextHi | 1 << 16);
            }
            UdTextMsg(0, kUdTextHi | 2 << 16);
        };
        assert(UdHitchLine("boatreplan: 9 ms") && UdHitchLine("owreset: x") && UdHitchLine("alarmtime: x") && UdHitchLine("lotpass: x") && !UdHitchLine("ufplan: x") && !UdHitchLine("play: x"));
        const LONG lines = g_udTxtLines;
        g_udTxtLines = 400;
        send("ufplan: past the cap");
        send("boatreplan: 91 ms");
        send("owreset: Perform");
        g_udTxtLines = lines;
        UdtLog(buf, sizeof(buf), h);
        assert(!strstr(buf, "past the cap") && strstr(buf, "ts3fixes: boatreplan: 91 ms\r\n") && strstr(buf, "ts3fixes: owreset: Perform\r\n"));
    }
    // the GetOption hook routes 0x87 to the channel
    assert(GetOptionHook(0, 0, kUdTextHi, 1) == 1);

    // address space: top-2 free blocks, per-type sums, limit text, low warning, mline format, walk cost
    {
        UdMemStat s = {};
        MEMORY_BASIC_INFORMATION mb = {};
        auto add = [&](DWORD state, DWORD type, SIZE_T mbytes) { mb.State = state, mb.Type = type, mb.RegionSize = mbytes << 20; UdMemAdd(s, mb); };
        add(MEM_FREE, 0, 100), add(MEM_FREE, 0, 300), add(MEM_FREE, 0, 50), add(MEM_FREE, 0, 200);
        add(MEM_COMMIT, MEM_IMAGE, 30), add(MEM_RESERVE, MEM_IMAGE, 2), add(MEM_COMMIT, MEM_MAPPED, 10), add(MEM_COMMIT, MEM_PRIVATE, 500), add(MEM_RESERVE, MEM_PRIVATE, 96);
        assert(s.regions == 9 && s.freeB == 650ull << 20 && s.big1 == 300ull << 20 && s.big2 == 200ull << 20);
        assert(s.image == 32ull << 20 && s.mapped == 10ull << 20 && s.priv == 596ull << 20 && s.commit == 540ull << 20);
        assert(strstr(UdLimitText(0x100000000ull), "4 GB") && strstr(UdLimitText(0xC0000000ull), "3 GB") && strstr(UdLimitText(0x80000000ull), "NOT large"));
        char mline[480];
        assert(!UdMemLow(mline, sizeof(mline), 256) && UdMemLow(mline, sizeof(mline), 255));
        assert(!strcmp(mline, "diag: low address space: largest free block 255 MB (under 256) - risk of Error 12 / crash on save"));
        UdMemFormat(mline, sizeof(mline), "save: memory at start #3", s, 0x100000000ull, 1851ull << 20, 120, 410);
        assert(!strcmp(mline, "save: memory at start #3: private 1851 MB, committed 540 MB; limit 4 GB (large address aware, 64-bit Windows), free 650 MB, "
                             "largest free 300 MB (2nd 200), session low 120 MB; modules 32 MB, mapped files 10 MB, heaps and other private 596 MB; "
                             "walk 410 us, 9 regions"));
        // walk cost on a fragmented space: 4,000 extra regions (64 KB reserves, every other one committed)
        static void* frag[4000];
        for (int i = 0; i < 4000; i++) frag[i] = VirtualAlloc(NULL, 65536, MEM_RESERVE | (i & 1 ? MEM_COMMIT : 0), PAGE_READWRITE);
        UdMemSample("diag: memory at test");
        for (void* p : frag) if (p) VirtualFree(p, 0, MEM_RELEASE);
        UdtLog(buf, sizeof(buf), h);
        const char* w = strstr(buf, "diag: memory at test: private ");
        assert(w && strstr(w, " regions\r\n"));
        const char* wu = strstr(w, "walk ");
        printf("user diag: address-space walk %ld us over %ld regions\n", strtol(wu + 5, NULL, 10), strtol(strstr(wu, " us, ") + 5, NULL, 10));
    }

    // save trail: start, phases with their end lines, a long wait, a watchdog mline, end
    g_udSaveTick = 0;
    g_udDogMs = 1000;
    UdPhase("not saving");  // no save on this thread: nothing
    UdSaveStart(0, 0);
    UdPhase("info (save paths)");
    UdWait("the running route job");
    g_udWaitTick -= 1500;  // as if it waited 1.5 s
    UdWait(NULL);
    UdPhase("write (package)");
    UdWait("+0x918 (deferred footprints)");
    WaitForSingleObject(g_udDogEv, g_udDogMs + 400);  // the watchdog writes one "still running" line meanwhile
    UdWait(NULL);
    UdSaveEnd(1);
    WaitForSingleObject(g_udDog, 5000);
    UdtLog(buf, sizeof(buf), h);
    assert(!strstr(buf, "not saving"));
    assert(strstr(buf, "save: start #") && strstr(buf, "save: at start route job none") && strstr(buf, "save: memory at start #") && strstr(buf, "save: memory at end #"));
    assert(strstr(buf, "save: > prepare (+") && strstr(buf, "save: < prepare ") && strstr(buf, "save: > info (save paths)"));
    assert(strstr(buf, "save: wait for the running route job took 15") && strstr(buf, "save: < write (package) "));
    assert(strstr(buf, "save: still running after ") && strstr(buf, ": phase write (package) for ") &&
           strstr(buf, "waiting for +0x918 (deferred footprints) for ") && strstr(buf, "; route job none, queued 0"));
    assert(strstr(buf, "save: end #") && strstr(buf, "result 1 after "));
    CloseHandle(g_udDog);
    g_udDog = NULL;
    UdPhase("after the end");  // the save is over: nothing
    UdtLog(buf, sizeof(buf), h);
    assert(!strstr(buf, "after the end"));

    // header parts on a temp TS3 user folder: Resource.cfg, a script package (S3SA in the index), a plain one, a save
    wchar_t user[MAX_PATH], p[MAX_PATH];
    swprintf_s(user, L"%sts3n-udtest-%lu", tmp, GetCurrentProcessId());
    for (const wchar_t* d : {L"", L"\\Mods", L"\\Mods\\Packages", L"\\Mods\\Packages\\NRaas", L"\\Saves", L"\\Saves\\A.sims3"}) {
        swprintf_s(p, L"%s%s", user, d);
        CreateDirectoryW(p, NULL);
    }
    auto put = [&](const wchar_t* rel, const void* data, DWORD n) {
        swprintf_s(p, L"%s\\%s", user, rel);
        HANDLE f = CreateFileW(p, GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, 0, NULL);
        DWORD w;
        WriteFile(f, data, n, &w, NULL);
        CloseHandle(f);
    };
    put(L"Mods\\Resource.cfg", cfg, (DWORD)strlen(cfg));
    static u8 pkg[96 + 4 + 32];
    memset(pkg, 0, sizeof(pkg));
    memcpy(pkg, "DBPF", 4);
    *(DWORD*)(pkg + 4) = 2, *(DWORD*)(pkg + 0x24) = 1, *(DWORD*)(pkg + 0x2C) = 36, *(DWORD*)(pkg + 0x40) = 96;
    *(DWORD*)(pkg + 100) = kUdS3SA;
    put(L"Mods\\Packages\\NRaas\\NRaas_Test_V1.package", pkg, sizeof(pkg));
    *(DWORD*)(pkg + 100) = 0x034AEECB;
    put(L"Mods\\Packages\\cc.package", pkg, sizeof(pkg));
    put(L"Saves\\A.sims3\\x.nhd", pkg, 100);
    UdModsLines(user);
    UdSavesLine(user);
    UdBinLines(tmp);  // read-only: nothing to list there is fine
    UdGpuLines();
    UdPacksLine();
    UdtLog(buf, sizeof(buf), h);
    assert(strstr(buf, "PackedFile:, Packages/*.package, Packages/*/*.package"));
    assert(strstr(buf, "diag: Mods\\Packages 2 .package files, 0 MB, 1 with scripts (S3SA), 1 other"));
    assert(strstr(buf, "diag: script mods (S3SA packages): NRaas_Test_V1.package\r\n") && strstr(buf, "cc.package"));
    assert(strstr(buf, "diag: saves 1 (+0 .backup), 1 files") && strstr(buf, "diag: Bin add-ons: none") && strstr(buf, "diag: registry packs:"));
    for (const wchar_t* f : {L"Mods\\Resource.cfg", L"Mods\\Packages\\NRaas\\NRaas_Test_V1.package", L"Mods\\Packages\\cc.package", L"Saves\\A.sims3\\x.nhd"}) {
        swprintf_s(p, L"%s\\%s", user, f);
        DeleteFileW(p);
    }
    for (const wchar_t* d : {L"\\Saves\\A.sims3", L"\\Saves", L"\\Mods\\Packages\\NRaas", L"\\Mods\\Packages", L"\\Mods", L""}) {
        swprintf_s(p, L"%s%s", user, d);
        RemoveDirectoryW(p);
    }

    g_udOn = wasOn;
    g_udDogMs = 5000;
    g_log = saved;
    CloseHandle(h);
    printf("user diag: install class, DBPF S3SA count, line packing, Resource.cfg patterns, ts3fixes text channel, save trail + watchdog, address space ok\n");
}
