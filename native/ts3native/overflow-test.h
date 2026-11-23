// TestOverflow (docs/323, the overflow audit fixes). Included by selftest.cpp. Each fix: an overflow input that used to kill
// the process (the /MT CRT's invalid-parameter path) or write past a table must now fail soft, and a normal input must give
// what the old code gave.

// a file tree whose deepest names pass MAX_PATH (made through \\?\ paths, as an unpacked CC archive can leave them)
struct OvTree {
    wchar_t base[MAX_PATH], deep[MAX_PATH];  // base\A..A\B..B: under MAX_PATH; its long file and long folder are not
    wchar_t xLongFile[600], xLongDir[600], xNormal[MAX_PATH];
};
static void OvMakeFile(const wchar_t* path, const char* text) {
    HANDLE h = CreateFileW(path, GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, 0, NULL);
    assert(h != INVALID_HANDLE_VALUE);
    DWORD w;
    WriteFile(h, text, (DWORD)strlen(text), &w, NULL);
    CloseHandle(h);
}
static void OvTreeMake(OvTree& t) {
    wchar_t tmp[MAX_PATH], a[101], b[101], x[160];
    GetTempPathW(MAX_PATH, tmp);
    swprintf_s(t.base, L"%sts3n-ovf-%lu", tmp, GetCurrentProcessId());
    const size_t room = MAX_PATH - 1 - wcslen(t.base) - 2 - 3;  // base\A\B\* still fits
    assert(room > 40);
    const size_t la = room / 2 < 100 ? room / 2 : 100, lb = room - la < 100 ? room - la : 100;
    for (size_t i = 0; i < la; i++) a[i] = L'a';
    a[la] = 0;
    for (size_t i = 0; i < lb; i++) b[i] = L'b';
    b[lb] = 0;
    for (int i = 0; i < 150; i++) x[i] = L'x';
    x[150] = 0;
    wchar_t xp[600];
    swprintf_s(xp, L"\\\\?\\%s", t.base);
    CreateDirectoryW(xp, NULL);
    swprintf_s(xp, L"\\\\?\\%s\\%s", t.base, a);
    assert(CreateDirectoryW(xp, NULL) || GetLastError() == ERROR_ALREADY_EXISTS);
    swprintf_s(t.deep, L"%s\\%s\\%s", t.base, a, b);
    assert(wcslen(t.deep) + 2 < MAX_PATH);
    swprintf_s(xp, L"\\\\?\\%s", t.deep);
    assert(CreateDirectoryW(xp, NULL) || GetLastError() == ERROR_ALREADY_EXISTS);
    swprintf_s(t.xLongFile, L"\\\\?\\%s\\%s.package", t.deep, x);  // 250-300+ chars as a plain path
    OvMakeFile(t.xLongFile, "not a dbpf");
    swprintf_s(t.xLongDir, L"\\\\?\\%s\\%s", t.deep, x);
    assert(CreateDirectoryW(t.xLongDir, NULL) || GetLastError() == ERROR_ALREADY_EXISTS);
    swprintf_s(t.xNormal, L"%s\\normal.package", t.base);
    OvMakeFile(t.xNormal, "not a dbpf either, but a .package name");
    assert(wcslen(t.xLongFile) - 4 >= 250);
}
static void OvTreeFree(OvTree& t) {
    DeleteFileW(t.xLongFile), RemoveDirectoryW(t.xLongDir), DeleteFileW(t.xNormal);
    wchar_t xp[600];
    swprintf_s(xp, L"\\\\?\\%s", t.deep);
    RemoveDirectoryW(xp);
    if (wchar_t* s = wcsrchr(xp, L'\\')) *s = 0;
    RemoveDirectoryW(xp);
    swprintf_s(xp, L"\\\\?\\%s", t.base);
    assert(RemoveDirectoryW(xp));
}

// lane A: PathFmt and every walker / path builder that used swprintf_s / wcscat_s on MAX_PATH buffers
static void TestOverflowPaths() {
    // 1. PathFmt: normal input = swprintf_s's text; exact fit; one char over = false and "" (never a cut path)
    wchar_t a[MAX_PATH], b[MAX_PATH], name[300];
    swprintf_s(b, L"%s\\%s", L"C:\\dir", L"file.package");
    assert(PathFmt(a, L"%s\\%s", L"C:\\dir", L"file.package") && !wcscmp(a, b));
    assert(PathFmt(a, MAX_PATH, L"%s.backup", L"C:\\x") && !wcscmp(a, L"C:\\x.backup"));
    for (int i = 0; i < 299; i++) name[i] = L'n';
    name[299] = 0;
    const LONG long0 = g_pathLong;
    name[MAX_PATH - 1] = 0;  // 259 chars + NUL = exactly MAX_PATH
    assert(PathFmt(a, L"%s", name) && wcslen(a) == MAX_PATH - 1 && g_pathLong == long0);
    name[MAX_PATH - 1] = L'n';
    name[MAX_PATH] = 0;  // 260 chars: one over
    a[0] = L'?';
    assert(!PathFmt(a, L"%s", name) && a[0] == 0 && g_pathLong == long0 + 1);
    name[MAX_PATH] = L'n';
    assert(!PathFmt(a, L"C:\\Users\\%s\\Documents", name) && a[0] == 0);  // 300-char folder name
    wchar_t small[8];
    assert(!PathFmt(small, L"%s.backup", L"C:\\x") && small[0] == 0);
    assert(!PathFmt((wchar_t*)NULL, 4, L"x") && !PathFmt(small, 0, L"x"));

    // 2. SaveWrite: Join of a long name fails (the job is skipped); the .backup name of a src near MAX_PATH fails too
    wchar_t dir[MAX_PATH];
    for (int i = 0; i < 255; i++) dir[i] = L'd';  // a src within 4 chars of MAX_PATH: ".backup" does not fit
    dir[255] = 0;
    assert(Join(a, L"C:\\Saves", L"x.sims3") && !wcscmp(a, L"C:\\Saves\\x.sims3"));
    assert(!Join(a, dir, L"ts3native-backup.tmp") && a[0] == 0);
    assert(!PathFmt(g_sj.bak, L"%s.backup", dir) && g_sj.bak[0] == 0);
    g_sj.bak[0] = 0;

    // 3. the walkers on a real tree with a 280+ char file and folder: no kill, the long entries skipped, the rest counted
    OvTree t;
    OvTreeMake(t);
    const LONG long1 = g_pathLong;
    UdScan s = {};
    UdListBegin(s.names, "test names:");
    UdListBegin(s.scripts, "test scripts:");
    UdScanPackages(t.base, 0, s);
    assert(s.files == 1 && s.script == 0 && g_pathLong >= long1 + 2);  // normal.package only; long file + long folder skipped
    int files = 0;
    assert(UdTreeBytes(t.base, 0, &files) > 0 && files == 2);  // files are counted by name (no join); the long folder is skipped
    int n = 0;
    g_crLen = 0;
    CrPackages(t.base, 0, &n);
    assert(n == 2 && strstr(g_crBuf, "normal.package"));  // names only (no join for files); the long folder is skipped
    g_crLen = 0, g_crBuf[0] = 0;
    static const wchar_t* const kPkg[] = {L".package", NULL};
    unsigned long long h = 0xCBF29CE484222325ull;
    LONG hf = 0;
    AdHashTree(t.base, 0, false, kPkg, &h, &hf);
    assert(hf == 1);
    MdtWalk(t.base, L"", 0);  // not a DBPF: nothing scanned, nothing killed
    wchar_t rel[MAX_PATH];
    for (int i = 0; i < 240; i++) rel[i] = L'r';
    rel[240] = 0;
    MdtWalk(t.base, rel, 0);  // subRel = rel + name passes MAX_PATH for "normal.package": skipped, not a kill
    // SaveWrite's tree walkers on the same tree: TreeHash and CopyTree report failure instead of a kill
    u64 th = 0, tb = 0;
    TreeHash(t.base, &th, &tb);
    wchar_t dst[MAX_PATH];
    swprintf_s(dst, L"%s-copy", t.base);
    assert(!CopyTree(t.base, dst));  // the long file cannot be copied by a MAX_PATH path: the job fails, the game's copy runs
    RemoveTree(dst);
    OvTreeFree(t);

    // 4. S3MM probe from a Bin folder of ~250 chars: no kill, nothing found
    wchar_t bin[MAX_PATH];
    for (int i = 0; i < 250; i++) bin[i] = L'q';
    bin[0] = L'C', bin[1] = L':', bin[2] = L'\\', bin[249] = L'\\', bin[250] = 0;
    const wchar_t* mod0 = g_s3mmModule;
    g_s3mmModule = L"ts3n-no-such-s3mm.asi";
    assert(S3mmProbe(bin) == 0);
    g_s3mmModule = mod0;
    printf("overflow paths ok (PathFmt fit / over by one, Join, UdScanPackages, UdTreeBytes, CrPackages, AdHashTree, MdtWalk, "
           "TreeHash, CopyTree, S3mmProbe on 250-300 char paths)\n");
}

// lane B: the per-call helpers (SFmt / SCpy / SCat / WCpy / WCat) and the risky sites (mono fault text, crash forensics
// module names, the AdaptDiag applied list)
static void TestOverflowCalls() {
    // 1. normal input: the CRT call's text and return
    char a[64], b[64];
    const int ra = SFmt(a, "%s %d %08lX", "x", 42, 0xBEEFul), rb = sprintf_s(b, "%s %d %08lX", "x", 42, 0xBEEFul);
    assert(ra == rb && !strcmp(a, b));
    assert(SFmt(a, sizeof(a), "%s", "abc") == 3 && !strcmp(a, "abc"));
    assert(SCpy(a, "hello") == 0 && !strcmp(a, "hello") && SCat(a, " world") == 0 && !strcmp(a, "hello world"));
    wchar_t w[16];
    assert(WCpy(w, L"C:\\a") == 0 && WCat(w, L"\\b.txt") == 0 && !wcscmp(w, L"C:\\a\\b.txt"));
    // 2. past the end: no kill; SFmt keeps what fits and returns its length, so a "+=" chain stays inside the buffer
    char s8[8];
    assert(SFmt(s8, "%s", "0123456789") == 7 && !strcmp(s8, "0123456"));
    char chain[40];
    int n = 0;
    for (int i = 0; i < 30; i++) n += SFmt(chain + n, sizeof(chain) - n, " %08lX", (DWORD)i);
    assert(n == (int)sizeof(chain) - 1 && strlen(chain) == sizeof(chain) - 1);
    assert(SCpy(s8, "0123456789") == STRUNCATE && !strcmp(s8, "0123456"));
    SCpy(s8, "abc");
    assert(SCat(s8, "defghij") == STRUNCATE && !strcmp(s8, "abcdefg"));
    const LONG long0 = g_pathLong;
    wchar_t p[MAX_PATH], big[300];
    for (int i = 0; i < 299; i++) big[i] = L'z';
    big[299] = 0;
    assert(WCpy(p, big) == STRUNCATE && p[0] == 0 && g_pathLong == long0 + 1);  // a path is never cut: ""
    big[250] = 0;
    assert(WCpy(p, big) == 0 && WCat(p, L"\\modtime-summary.txt") == STRUNCATE && p[0] == 0);
    assert(WCat(p, L"modtime.csv") == STRUNCATE && p[0] == 0);  // onto "": stays "" (no relative path in the game folder)
    assert(SFmt((char*)NULL, 4, "x") == 0 && SFmt(s8, 0, "x") == 0);
    // 3. M1: the mono fault text in a too small buffer (as the handler's own buffer for a longer text): cut, no kill
    {
        static DWORD rm[4], f3[1], f2[1], obj[1], frame[16];
        rm[2] = (DWORD)(uintptr_t)g_fbMeth[1];
        f2[0] = (DWORD)(uintptr_t)f3, obj[0] = (DWORD)(uintptr_t)f2;
        CONTEXT c = {};
        c.Eip = 0x00E5201F, c.Esi = (DWORD)(uintptr_t)g_fbMeth[0], c.Ebp = (DWORD)(uintptr_t)(frame + 12);
        frame[12 - 4] = (DWORD)(uintptr_t)rm, frame[12 - 10] = (DWORD)(uintptr_t)obj;
        char big2[2048], small[48];
        assert(MonoFaultText(&c, big2, sizeof(big2)) && strlen(big2) > sizeof(small));
        assert(MonoFaultText(&c, small, sizeof(small)) && strlen(small) == sizeof(small) - 1 && !strncmp(small, big2, sizeof(small) - 1));
    }
    // 4. L12: a module+offset name into a buffer smaller than it
    char code[8], full[96];
    assert(CdCode((DWORD)(uintptr_t)&TestOverflowCalls, full, sizeof(full)) && strstr(full, "+0x"));
    assert(CdCode((DWORD)(uintptr_t)&TestOverflowCalls, code, sizeof(code)) && strlen(code) == 7 && !strncmp(code, full, 7));
    printf("overflow calls ok (SFmt / SCpy / SCat / WCpy / WCat equal text and fail-soft, mono fault text cut, CdCode cut)\n");
}

// lane F: L9 (does UserSims3Dir read the shell folder, moved or not?); L3 / L6 are in equalsfast-test.h / fsro-test.h
static void TestOverflowSmall() {
    static const wchar_t kKey[] = L"Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\User Shell Folders";
    wchar_t raw[MAX_PATH], exp[MAX_PATH], want[MAX_PATH], got[MAX_PATH];
    DWORD sz = sizeof(raw);
    // the audit read the API docs as "these flags fail with ERROR_INVALID_PARAMETER"; measured here they succeed (L9 refuted)
    const LONG old = RegGetValueW(HKEY_CURRENT_USER, kKey, L"Personal", RRF_RT_REG_SZ | RRF_RT_REG_EXPAND_SZ, NULL, raw, &sz);
    printf("UserSims3Dir: RRF_RT_REG_SZ | RRF_RT_REG_EXPAND_SZ -> %ld\n", old);
    assert(old == ERROR_SUCCESS);
    sz = sizeof(raw);
    if (RegGetValueW(HKEY_CURRENT_USER, kKey, L"Personal", RRF_RT_REG_SZ | RRF_RT_REG_EXPAND_SZ | RRF_NOEXPAND, NULL, raw, &sz) == ERROR_SUCCESS) {
        assert(ExpandEnvironmentStringsW(raw, exp, MAX_PATH) && PathFmt(want, L"%s\\Electronic Arts\\The Sims 3", exp));
        UserSims3Dir(got);
        assert(!_wcsicmp(got, want));  // the shell folder itself, moved or not
    }
    printf("overflow small ok (UserSims3Dir = the expanded shell Documents folder; L9 not a bug)\n");

    // review fix: an empty directory gives no path, no file and no write in the drive root
    auto rootCrashes = []() {
        WIN32_FIND_DATAW fd;
        int n = 0;
        HANDLE h = FindFirstFileW(L"\\crash-*.txt", &fd);
        if (h != INVALID_HANDLE_VALUE) { do n++; while (FindNextFileW(h, &fd)); FindClose(h); }
        return n;
    };
    const bool hadMarker = GetFileAttributesW(L"\\running.txt") != INVALID_FILE_ATTRIBUTES;
    const bool hadPlayed = GetFileAttributesW(L"\\ts3native") != INVALID_FILE_ATTRIBUTES;
    const int crashes0 = rootCrashes();
    wchar_t p[MAX_PATH] = L"x";
    assert(!PathFmt(p, L"%s\\running.txt", L"") && !p[0]);
    wcscpy_s(p, L"x");
    assert(PathFmt(p, L"%s%s", L"", L"a") && !wcscmp(p, L"a"));  // only a "%s\..." directory join is refused
    wchar_t savedDir[MAX_PATH];
    wcscpy_s(savedDir, g_crDir);
    g_crDir[0] = 0;
    CrMarkerPath(p);
    assert(!p[0]);
    assert(CrOpenReport("soft") == INVALID_HANDLE_VALUE && CrOpenReport("hard") == INVALID_HANDLE_VALUE);
    wcscpy_s(g_crDir, savedDir);
    assert(!PlPathIn(L"", "k", p) && !p[0]);
    wchar_t tmp[MAX_PATH], base[MAX_PATH], want2[MAX_PATH];
    assert(GetTempPathW(MAX_PATH, tmp) && PathFmt(base, L"%sovf-played-%lu", tmp, GetCurrentProcessId()));
    CreateDirectoryW(base, NULL);
    assert(PlPathIn(base, "k", p) && PathFmt(want2, L"%s\\ts3native\\played\\k.txt", base) && !wcscmp(p, want2));
    PathFmt(want2, L"%s\\ts3native\\played", base);
    RemoveDirectoryW(want2);
    PathFmt(want2, L"%s\\ts3native", base);
    RemoveDirectoryW(want2);
    RemoveDirectoryW(base);
    assert(rootCrashes() == crashes0);
    assert(hadMarker || GetFileAttributesW(L"\\running.txt") == INVALID_FILE_ATTRIBUTES);
    assert(hadPlayed || GetFileAttributesW(L"\\ts3native") == INVALID_FILE_ATTRIBUTES);
    printf("overflow empty dir ok (PathFmt \"%%s\\\\\" with \"\" fails, CrMarkerPath \"\", CrOpenReport refused, PlPathIn \"\" refused; nothing in the drive root)\n");
}
