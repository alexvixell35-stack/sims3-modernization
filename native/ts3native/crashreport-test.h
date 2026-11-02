// TestCrashReport ([CrashReport], crashreport.cpp, docs/135). Included by selftest.cpp. Faults only in a test dummy.
static volatile LONG g_crtNext;
static LONG WINAPI CrtNext(EXCEPTION_POINTERS*) { g_crtNext++; return EXCEPTION_EXECUTE_HANDLER; }
static LONG WINAPI CrtOther(EXCEPTION_POINTERS*) { return EXCEPTION_CONTINUE_SEARCH; }
static __declspec(noinline) int CrtFault(volatile int* p) { return *p + 1; }  // test only: the forced fault
static bool CrtHasI(const char* hay, const char* needle) {
    for (const size_t n = strlen(needle); *hay; hay++)
        if (!_strnicmp(hay, needle, n)) return true;
    return false;
}
static EXCEPTION_RECORD g_crtRec;
static CONTEXT g_crtCtx;
static LONG CrtCatch(EXCEPTION_POINTERS* e) {
    g_crtRec = *e->ExceptionRecord, g_crtCtx = *e->ContextRecord;
    return CrFilter(e);
}
static __declspec(noinline) bool CrtRaise() {
    __try {
        CrtFault((volatile int*)(uintptr_t)0x10);
    } __except (CrtCatch(GetExceptionInformation())) {
        return true;
    }
    return false;
}

// newest crash-*.txt in g_crDir into buf; deletes every file there
static size_t CrtTake(char* buf, size_t cap, const wchar_t* match) {
    wchar_t pat[MAX_PATH], p[MAX_PATH];
    swprintf_s(pat, L"%s\\%s", g_crDir, match);
    WIN32_FIND_DATAW f;
    HANDLE h = FindFirstFileW(pat, &f);
    size_t n = 0;
    buf[0] = 0;
    if (h == INVALID_HANDLE_VALUE) return 0;
    do {
        swprintf_s(p, L"%s\\%s", g_crDir, f.cFileName);
        HANDLE fh = CreateFileW(p, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, 0, NULL);
        DWORD r = 0;
        if (fh != INVALID_HANDLE_VALUE) ReadFile(fh, buf, (DWORD)cap - 1, &r, NULL), CloseHandle(fh);
        buf[n = r] = 0;
        DeleteFileW(p);
    } while (FindNextFileW(h, &f));
    FindClose(h);
    return n;
}

static void TestCrashReport() {
    // SHA-256 known answers ("abc", and 1,000,000 x 'a' crosses many blocks)
    CrSha s;
    char hex[65];
    CrShaInit(s), CrShaAdd(s, (const u8*)"abc", 3), CrShaHex(s, hex);
    assert(!strcmp(hex, "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"));
    CrShaInit(s);
    static u8 a1000[1000];
    memset(a1000, 'a', sizeof(a1000));
    for (int i = 0; i < 1000; i++) CrShaAdd(s, a1000, sizeof(a1000));
    CrShaHex(s, hex);
    assert(!strcmp(hex, "cdc76e5c9914fb9281a1c7e284d73e67f1809a48a497200e046d39ccc7112cd0"));

    // scrubber: fake profile "alice" on machine "ALICE-PC"
    g_crNeedleN = 0;
    CrAddNeedle(L"C:\\Users\\alice\\OneDrive\\Documents\\Electronic Arts\\The Sims 3\\", "<TS3User>", 4);
    CrAddNeedle(L"D:\\Games\\Steam\\steamapps\\common\\The Sims 3\\Game\\Bin", "<Bin>", 4);
    CrAddNeedle(L"C:\\Users\\alice", "<home>", 4);
    CrAddNeedle(L"alice", "<user>", 3);
    CrAddNeedle(L"ALICE-PC", "<machine>", 3);
    CrAddNeedle(L"al", "<user>", 3);  // too short: skipped (would eat every "al")
    assert(g_crNeedleN == 5 && g_crNeedle[0].n > g_crNeedle[1].n && !strcmp(g_crNeedle[0].to, "<TS3User>"));
    const char* in =
        "host d:\\games\\steam\\steamapps\\common\\The Sims 3\\Game\\Bin\\TS3W.exe base 0x00400000\r\n"
        "save C:\\Users\\Alice\\OneDrive\\Documents\\Electronic Arts\\The Sims 3\\Saves\\x.sims3\r\n"
        "cfg C:\\Users\\alice\\AppData\\x.ini on alice-pc by ALICE, total 12\r\n"
        "other E:\\Users\\bob\\x and /Users/carol/y and \\Users\\\r\n";
    char out[1024];
    const size_t n = CrScrub(in, strlen(in), out, sizeof(out));
    assert(n == strlen(out));
    assert(strstr(out, "host <Bin>\\TS3W.exe base 0x00400000\r\n"));
    assert(strstr(out, "save <TS3User>\\Saves\\x.sims3\r\n"));
    assert(strstr(out, "cfg <home>\\AppData\\x.ini on <machine> by <user>, total 12\r\n"));
    assert(strstr(out, "other E:\\Users\\<user>\\x and /Users/<user>/y and \\Users\\<user>\r\n"));
    assert(!CrtHasI(out, "alice") && !strstr(out, "bob") && !strstr(out, "carol"));
    char tiny[8];
    assert(CrScrub(in, strlen(in), tiny, sizeof(tiny)) == 7 && tiny[7] == 0);  // truncates, never overruns

    // signature: fault + top 3 frames, stable id, unknown addresses as absolute hex
    const DWORD self = (DWORD)(uintptr_t)GetModuleHandleW(NULL);
    const DWORD fr[4] = {self + 0x1010, self + 0x2020, 0x00000123, self + 0x4040};
    char sig[256];
    const DWORD id = CrSignature(self + 0x1234, fr, 4, sig, sizeof(sig));
    assert(!strcmp(sig, "selftest+0x1234 selftest+0x1010 selftest+0x2020 0x00000123"));
    char sig2[256];
    assert(CrSignature(self + 0x1234, fr, 4, sig2, sizeof(sig2)) == id && CrSignature(self + 0x1235, fr, 4, sig2, sizeof(sig2)) != id);

    // IAT chaining: later SetUnhandledExceptionFilter calls queue behind ours, ours stays on top
    typedef LPTOP_LEVEL_EXCEPTION_FILTER(WINAPI * SetFn)(LPTOP_LEVEL_EXCEPTION_FILTER);
    const SetFn real = (SetFn)GetProcAddress(GetModuleHandleW(L"kernel32.dll"), "SetUnhandledExceptionFilter");
    const LPTOP_LEVEL_EXCEPTION_FILTER mainFilter = real(CrFilter);
    g_crPrev = mainFilter;
    DWORD* slot = CrFindIat(GetModuleHandleW(NULL), (void*)real);
    assert(slot && *slot == (DWORD)(uintptr_t)real);
    const DWORD hook = (DWORD)(uintptr_t)CrSetFilter;
    assert(Write(slot, &hook, 4));
    assert(SetUnhandledExceptionFilter(CrtOther) == mainFilter && g_crPrev == CrtOther);  // through the IAT: our stand-in
    assert(SetUnhandledExceptionFilter(CrtNext) == CrtOther && g_crPrev == CrtNext);
    assert(real(CrFilter) == CrFilter);  // the real top filter never changed
    const DWORD r0 = (DWORD)(uintptr_t)real;
    assert(Write(slot, &r0, 4));

    // forced fault in the dummy: report written, next filter called once, its verdict returned
    wchar_t tmp[MAX_PATH];
    GetTempPathW(MAX_PATH, tmp);
    swprintf_s(g_crUser, L"%sts3n-crtest-%lu", tmp, GetCurrentProcessId());
    swprintf_s(g_crDir, L"%s\\ts3native-crashes", g_crUser);
    swprintf_s(g_crLog, L"%s\\ts3native.log", g_crUser);
    GetModuleFileNameW(NULL, g_crExe, MAX_PATH);
    wchar_t pk[MAX_PATH];
    CreateDirectoryW(g_crUser, NULL);
    CreateDirectoryW(g_crDir, NULL);
    swprintf_s(pk, L"%s\\Mods", g_crUser), CreateDirectoryW(pk, NULL);
    swprintf_s(pk, L"%s\\Mods\\Packages", g_crUser), CreateDirectoryW(pk, NULL);
    swprintf_s(pk, L"%s\\Mods\\Packages\\alice", g_crUser), CreateDirectoryW(pk, NULL);
    wchar_t files[3][MAX_PATH];
    swprintf_s(files[0], L"%s\\Mods\\Packages\\ts3fixes.package", g_crUser);
    swprintf_s(files[1], L"%s\\Mods\\Packages\\alice\\alice-cc.package", g_crUser);
    swprintf_s(files[2], L"%s\\Mods\\Packages\\readme.txt", g_crUser);
    for (const wchar_t* f : files) CloseHandle(CreateFileW(f, GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, 0, NULL));
    HANDLE lh = CreateFileW(g_crLog, GENERIC_WRITE, FILE_SHARE_READ, NULL, CREATE_ALWAYS, 0, NULL);
    DWORD w;
    char line[96];
    for (int i = 0; i < 70; i++) WriteFile(lh, line, (DWORD)sprintf_s(line, "log line %d by alice\r\n", i), &w, NULL);
    CloseHandle(lh);
    g_crNeedleN = 0;
    CrAddNeedle(L"alice", "<user>", 3);
    CrSysInit();
    strcpy_s(g_crSections, "[GC] [CrashReport]");
    wchar_t mp[MAX_PATH];
    CrMarkerPath(mp);
    CloseHandle(CreateFileW(mp, GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, 0, NULL));
    g_crBusy = 0, g_crtNext = 0, g_crPrev = CrtNext;
    assert(CrtRaise() && g_crtNext == 1);
    assert(GetFileAttributesW(mp) == INVALID_FILE_ATTRIBUTES);  // captured: the marker is gone
    static char rep[65536];
    assert(CrtTake(rep, sizeof(rep), L"crash-*.txt") > 0);
    const char* fa = strstr(rep, "\r\nfault: selftest+0x");
    unsigned long fo = 0;
    assert(fa && sscanf_s(fa + 20, "%lx", &fo) == 1);
    const DWORD fn = (DWORD)(uintptr_t)CrtFault - self;
    assert(fo >= fn && fo < fn + 32);  // inside CrtFault (the exact instruction is the compiler's)
    assert(strstr(rep, "kind: exception\r\n") && strstr(rep, "title: [crash] selftest+0x") && strstr(rep, " (ts3native " TS3NATIVE_VERSION ")\r\n"));
    assert(strstr(rep, "code: C0000005 (access violation, read 0x00000010)\r\n") && strstr(rep, "signature id: crashsig-"));
    assert(strstr(rep, "enabled sections: [GC] [CrashReport]\r\n") && strstr(rep, "exe sha256: ") && strstr(rep, "cpu: "));
    assert(strstr(rep, "registers: eax=") && strstr(rep, "stack (return addresses"));
    assert(strstr(strstr(rep, "stack (return addresses"), "  selftest+0x"));  // CrtRaise's frame is on the stack
    assert(strstr(rep, "  ts3fixes.package\r\n") && strstr(rep, "  <user>-cc.package\r\n") && !strstr(rep, "readme"));
    assert(strstr(rep, "packages: 2\r\n") && strstr(rep, "log line 69 by <user>") && strstr(rep, "log line 20 by") && !strstr(rep, "log line 19 "));
    assert(!CrtHasI(rep, "alice") && strstr(rep, "\r\n--- end ---\r\n"));

    // no next filter: the crash goes on (EXCEPTION_CONTINUE_SEARCH), one report per process
    EXCEPTION_POINTERS ep = {&g_crtRec, &g_crtCtx};
    g_crPrev = NULL;
    assert(CrFilter(&ep) == EXCEPTION_CONTINUE_SEARCH && CrtTake(rep, sizeof(rep), L"crash-*.txt") == 0);  // busy: no second report
    g_crBusy = 0;
    assert(CrFilter(&ep) == EXCEPTION_CONTINUE_SEARCH && CrtTake(rep, sizeof(rep), L"crash-*.txt") > 0);

    // hard report from a marker the previous session left behind
    strcpy_s(g_crPrevTail, "last line before the end by alice\r\n");
    g_crPrevTailN = strlen(g_crPrevTail);
    CrHardReport("0.36.1");
    assert(CrtTake(rep, sizeof(rep), L"crash-*-hard.txt") > 0);
    assert(strstr(rep, "kind: hard\r\n") && strstr(rep, "title: [crash] hard exit (ts3native 0.36.1)\r\n") && strstr(rep, "ts3native: 0.36.1\r\n"));
    assert(strstr(rep, "previous ts3native.log") && strstr(rep, "before the end by <user>") && !CrtHasI(rep, "alice"));

    for (const wchar_t* f : files) DeleteFileW(f);
    DeleteFileW(g_crLog);
    swprintf_s(pk, L"%s\\Mods\\Packages\\alice", g_crUser), RemoveDirectoryW(pk);
    swprintf_s(pk, L"%s\\Mods\\Packages", g_crUser), RemoveDirectoryW(pk);
    swprintf_s(pk, L"%s\\Mods", g_crUser), RemoveDirectoryW(pk);
    RemoveDirectoryW(g_crDir);
    assert(RemoveDirectoryW(g_crUser));  // nothing left behind
    real(mainFilter);
    g_crPrev = NULL, g_crBusy = 0, g_crNeedleN = 0;

    // docs/172 §7: the first-chance logger names another module's address and logs it once; repeats take no slot
    {
        const DWORD k32 = (DWORD)(uintptr_t)GetModuleHandleW(L"kernel32.dll") + 0x10;  // the header page: always committed
        char m[MAX_PATH + 16];
        assert(XcptOtherModule(k32, m, sizeof(m)) && !_stricmp(m, "kernel32.dll+0x10"));
        void* const heap = malloc(64);
        assert(!XcptOtherModule((DWORD)(uintptr_t)heap, m, sizeof(m)));  // not an image: logged every time, as before
        free(heap);
        static DWORD stack[64];
        CONTEXT cx = {};
        cx.Eip = k32, cx.Esp = cx.Ebp = (DWORD)(uintptr_t)(stack + 32);
        EXCEPTION_RECORD er = {};
        er.ExceptionCode = EXCEPTION_ACCESS_VIOLATION, er.ExceptionAddress = (void*)(uintptr_t)k32, er.NumberParameters = 2;
        EXCEPTION_POINTERS xp = {&er, &cx};
        const LONG n0 = g_xcptN, other0 = g_xcptOtherN;
        assert(n0 <= 13 && other0 <= 6);
        assert(XcptLog(&xp) == EXCEPTION_CONTINUE_SEARCH && g_xcptN == n0 + 1 && g_xcptOtherN == other0 + 1 && g_xcptOther[other0] == k32);
        assert(XcptLog(&xp) == EXCEPTION_CONTINUE_SEARCH && g_xcptN == n0 + 1 && g_xcptOtherHits[other0] == 1);  // repeat: counted only
        cx.Eip = k32 + 4, er.ExceptionAddress = (void*)(uintptr_t)(k32 + 4);
        assert(XcptLog(&xp) == EXCEPTION_CONTINUE_SEARCH && g_xcptN == n0 + 2 && g_xcptOtherN == other0 + 2);  // another address: a line
        g_xcptN = n0, g_xcptOtherN = other0, g_xcptOtherHits[other0] = g_xcptOtherHits[other0 + 1] = 0;
    }
    puts("crash report ok");
}
