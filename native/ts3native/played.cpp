// Played-household store for ts3fixes (docs/14 section 8.6). Our data never goes into a save: the set of households that were ever
// the active one lives in a sidecar text file per save,
//   <Documents>\Electronic Arts\The Sims 3\ts3native\played\<key>.txt
// one decimal household id per line, '#' lines are comments (a header names the save key, world and write time). Script code
// cannot write files, so ts3fixes talks to this store over the [UserDiag] text channel (top byte 0x87, userdiag.cpp):
//   text line "played: open <key> <world>"    load <key>.txt (missing = empty set, bad lines skipped, both logged)
//   text line "played: saveas <key> <world>"  Save As / first save: the current set written under the new key, now current
//   text line "played: add <id>"              add one id and rewrite the file (temp file + rename)
//   text line "played: ask <id>"              answer = id in the set
//   op 6                                      the answer: after open/saveas 1 = store ready, after ask 1 = played
// Old builds and [UserDiag] off answer op 6 with 0: ts3fixes then treats the store as unavailable and the aggressive save
// cleanup stands down. Sim thread only (the text channel is called from script), so no lock.

static const int kPlMax = 4096;   // ids per save (a world has a few hundred households at most)
static u64 g_plIds[kPlMax];
static int g_plN;
static bool g_plOpen;
static u8 g_plAnswer;
static char g_plKey[104], g_plWorld[64];

// Save key: 1-100 characters of [A-Za-z0-9._-], not starting with '.' (ts3fixes sanitizes the save name to this).
static bool PlKeyOk(const char* k) {
    size_t n = strlen(k);
    if (n == 0 || n > 100 || k[0] == '.') return false;
    for (size_t i = 0; i < n; i++) {
        const char c = k[i];
        if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '.' || c == '_' || c == '-')) return false;
    }
    return true;
}

// Decimal id (1..2^64-1, nothing else on the line but spaces/CR); false for anything else.
static bool PlParseId(const char* s, const char* e, u64* out) {
    while (s < e && (*s == ' ' || *s == '\t')) s++;
    while (e > s && (e[-1] == ' ' || e[-1] == '\t' || e[-1] == '\r')) e--;
    if (s == e || e - s > 20) return false;
    u64 v = 0;
    for (const char* p = s; p < e; p++) {
        if (*p < '0' || *p > '9') return false;
        const u64 d = (u64)(*p - '0');
        if (v > (~0ULL - d) / 10) return false;  // overflow
        v = v * 10 + d;
    }
    if (!v) return false;
    *out = v;
    return true;
}

static bool PlHas(const u64* ids, int n, u64 id) {
    for (int i = 0; i < n; i++)
        if (ids[i] == id) return true;
    return false;
}

// File text -> ids (duplicates dropped, at most cap); *bad = lines that are neither blank, comment nor an id.
static int PlParse(const char* txt, size_t len, u64* ids, int cap, int* bad) {
    int n = 0;
    *bad = 0;
    const char* end = txt + len;
    for (const char* s = txt; s < end;) {
        const char* e = (const char*)memchr(s, '\n', (size_t)(end - s));
        if (!e) e = end;
        const char* t = s;
        while (t < e && (*t == ' ' || *t == '\t' || *t == '\r')) t++;
        u64 id;
        if (t < e && *t != '#') {
            if (!PlParseId(t, e, &id)) (*bad)++;
            else if (n < cap && !PlHas(ids, n, id)) ids[n++] = id;
        }
        s = e < end ? e + 1 : end;
    }
    return n;
}

static bool PlPathIn(const wchar_t* base, const char* key, wchar_t* out) {  // base: <Documents>\Electronic Arts\The Sims 3
    wchar_t dir[MAX_PATH];
    out[0] = 0;
    if (!base[0]) return false;  // docs/323: "" would put ts3native\played in the drive root
    if (!PathFmt(dir, L"%s\\ts3native", base)) return false;
    CreateDirectoryW(dir, NULL);
    if (!PathFmt(dir, L"%s\\ts3native\\played", base)) return false;
    CreateDirectoryW(dir, NULL);
    return PathFmt(out, MAX_PATH, L"%s\\%S.txt", dir, key);
}
static bool PlPath(const char* key, wchar_t* out) {
    wchar_t base[MAX_PATH];
    UserSims3Dir(base);  // apex.cpp
    return PlPathIn(base, key, out);
}

static void PlLoad(const char* key) {
    g_plN = 0;
    wchar_t path[MAX_PATH];
    if (!PlPath(key, path)) { Log("played: %s: no path, empty set", key); return; }
    HANDLE h = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, 0, NULL);
    if (h == INVALID_HANDLE_VALUE) { Log("played: %s: no file yet, empty set", key); return; }
    static char buf[256 * 1024];
    DWORD got = 0;
    const BOOL ok = ReadFile(h, buf, sizeof(buf), &got, NULL);
    const DWORD size = GetFileSize(h, NULL);
    CloseHandle(h);
    if (!ok || size > sizeof(buf)) { Log("played: %s: unreadable or over 256 KB, empty set", key); return; }
    int bad = 0;
    g_plN = PlParse(buf, got, g_plIds, kPlMax, &bad);
    Log("played: %s: %d households read%s", key, g_plN, bad ? " (bad lines skipped)" : "");
    if (bad) Log("played: %s: %d lines were not household ids", key, bad);
}

static void PlSave() {
    wchar_t path[MAX_PATH], tmp[MAX_PATH];
    if (!PlPath(g_plKey, path) || !PathFmt(tmp, L"%s.tmp", path)) { Log("played: %s: no path, not written", g_plKey); return; }
    HANDLE h = CreateFileW(tmp, GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, 0, NULL);
    if (h == INVALID_HANDLE_VALUE) { Log("played: %s: cannot write (%lu)", g_plKey, GetLastError()); return; }
    SYSTEMTIME st;
    GetSystemTime(&st);
    char line[256];
    DWORD w;
    int n = SFmt(line, "# ts3native played households (ts3fixes save cleanup, docs/14 section 8.6). One household id per line.\r\n"
                            "# Not part of the save: deleting this file only forgets which households were played.\r\n");
    BOOL ok = n > 0 && WriteFile(h, line, (DWORD)n, &w, NULL);
    n = SFmt(line, "# save %s\r\n# world %s\r\n# written %04u-%02u-%02u %02u:%02u UTC\r\n", g_plKey, g_plWorld, st.wYear, st.wMonth, st.wDay,
                  st.wHour, st.wMinute);
    ok = ok && n > 0 && WriteFile(h, line, (DWORD)n, &w, NULL);
    for (int i = 0; ok && i < g_plN; i++) {
        n = SFmt(line, "%llu\r\n", g_plIds[i]);
        ok = n > 0 && WriteFile(h, line, (DWORD)n, &w, NULL);
    }
    CloseHandle(h);
    if (!ok || !MoveFileExW(tmp, path, MOVEFILE_REPLACE_EXISTING)) { Log("played: %s: write failed (%lu)", g_plKey, GetLastError()); DeleteFileW(tmp); }
}

// "<key> <world>" -> g_plKey / g_plWorld; false for a bad key.
static bool PlKeyWorld(const char* arg) {
    char key[104] = {0};
    const char* sp = strchr(arg, ' ');
    const size_t kn = sp ? (size_t)(sp - arg) : strlen(arg);
    if (kn >= sizeof(key)) return false;
    memcpy(key, arg, kn);
    if (!PlKeyOk(key)) return false;
    SCpy(g_plKey, key);
    strncpy_s(g_plWorld, sp ? sp + 1 : "?", _TRUNCATE);
    return true;
}

// ts3fixes sends "unsaved_<world>" for a game that has no save yet: kept in memory only, written at the first save (saveas).
static bool PlUnsaved(const char* key) { return !strncmp(key, "unsaved_", 8); }

// One "played: ..." line (prefix already cut).
static void PlLine(const char* s) {
    u64 id;
    if (!strncmp(s, "open ", 5)) {
        if (!PlKeyWorld(s + 5)) { g_plOpen = false; g_plAnswer = 0; Log("played: bad key in '%s'", s); return; }
        if (PlUnsaved(g_plKey)) { g_plN = 0; Log("played: %s: game not saved yet, set kept in memory until the first save", g_plKey); }
        else PlLoad(g_plKey);
        g_plOpen = true;
        g_plAnswer = 1;
    } else if (!strncmp(s, "saveas ", 7)) {
        char old[104];
        SCpy(old, g_plKey);
        if (!PlKeyWorld(s + 7)) { Log("played: bad key in '%s'", s); return; }
        if (!g_plOpen) { PlLoad(g_plKey); g_plOpen = true; }  // nothing open yet: read what the new key already holds
        else { PlSave(); Log("played: %d households copied from %s to %s", g_plN, old, g_plKey); }
        g_plAnswer = 1;
    } else if (!strncmp(s, "add ", 4) && PlParseId(s + 4, s + strlen(s), &id)) {
        if (!g_plOpen || PlHas(g_plIds, g_plN, id)) return;
        if (g_plN >= kPlMax) { Log("played: %s full (%d), %llu not added", g_plKey, kPlMax, id); return; }
        g_plIds[g_plN++] = id;
        if (!PlUnsaved(g_plKey)) PlSave();
        Log("played: added hh=%llu to %s (%d played)", id, g_plKey, g_plN);
    } else if (!strncmp(s, "ask ", 4) && PlParseId(s + 4, s + strlen(s), &id)) {
        g_plAnswer = g_plOpen && PlHas(g_plIds, g_plN, id) ? 1 : 0;
    } else
        Log("played: unknown line '%s'", s);
}

static u8 PlAnswer() { return g_plOpen ? g_plAnswer : 0; }
