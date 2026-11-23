// [AdaptDiag] (docs/316, interface docs/317): the adaptive diag kit's level 0 log and rule engine. Enabled=0 by default and
// absent from the release ini; only the kit's diag ini turns it on. Read-only towards the game: it logs, writes its own files
// next to ts3native.log and flips other diagnostics' runtime thresholds (never a fix, never a Sim, save or setting).
//
// Start (AdOverlay, before any section reads the ini): ts3native.adaptdiag-state.ini [Escalate] (written by the rule engine
// of the last session) and a last session that ended without a clean exit (crash -> [CrashForensics]) give allow-listed
// Section.Key=Value overrides; the ini is copied to ts3native.adapt.ini with them and every section reads that copy.
// Level 0 (always, cheap): per real minute one adaptdiag.csv row: activity tag (ts3fixes op 63: live / cas / buildbuy /
// travel / paused; save from the save trail; load otherwise), sim iterations per second, iterations >= 250 ms / >= 1 s,
// longest, largest free address-space block, committed MB, new ScriptError files, other programs' CPU, CPU performance %
// (PcCheck's samples), thermal-zone temperature (PDH, no admin), AV / indexer process names, noise grade A/B/C.
// Rules (each minute): a symptom (repeated >= 1 s hitches, stutter, script errors, address-space loss, a slow load, route
// waits) whose evidence matches the known-issue library (ts3native.adaptdiag-known.tsv) is labelled known and takes no
// slot; otherwise the drill-down for it is ranked by severity x frequency. At most MaxActive run at once, each with a TTL and
// a log-growth cap, under a CPU budget (the lowest rank goes first) and a log size cap. Runtime drill-downs change
// thresholds of detectors that are installed (hitch lines, stall watch, ModTime rate, route-wait lines); install-time ones go
// to [Escalate] for the next start. With [Session] Benchmark=1 or an A/B Arm nothing turns on at run time (deferred to the
// next session), so arms escalate identically. Every turn-on / off is logged ("adaptdiag: ...").
// Report: adaptdiag-report.txt (docs/317 §2) every 5 minutes, at world quit and at exit.

// g_logTap (ts3native.cpp): Log() hands each line to AdTap while [AdaptDiag] is on

enum { AD_LOAD, AD_LIVE, AD_CAS, AD_BUILDBUY, AD_TRAVEL, AD_PAUSED, AD_SAVE, AD_ACTS };
static const char* const kAdAct[AD_ACTS] = {"load", "live", "cas", "buildbuy", "travel", "paused", "save"};

// ---- pure helpers (selftest: adaptdiag-test.h) ----
static unsigned long long AdFnv(unsigned long long h, const void* p, size_t n) {
    const u8* b = (const u8*)p;
    for (size_t i = 0; i < n; i++) h = (h ^ b[i]) * 1099511628211ull;
    return h;
}
static const unsigned long long kAdFnv0 = 14695981039346656037ull;
// next-session keys the state file may set (anything else in [Escalate] is ignored and logged)
static bool AdAllowed(const char* key) {
    static const char* const kAllow[] = {"CrashForensics.Enabled", "CrashForensics.VbGuard", "MemDiag.Enabled", "MemDiag.AllocSites",
                                         "MemDiag.AllocSitesOwners", "LoadProf.Enabled", "RouteJobPhases.SyncPlans", "HitchAttr.Holders",
                                         "HitchAttr.HitchMs", "HitchAttr.JobNameMs", "HitchAttr.RouteLockMs", "StallWatch.Enabled",
                                         "StallWatch.StallMs", "StallWatch.EveryMs", "StallWatch.Names", "StallWatch.Managed",
                                         "StallWatch.StackKB", "StallWatch.KeepNoSim", "Diag.ModTime", "Diag.ModTimeHz",
                                         "AdaptDiag.SwapInvokeDiag"};
    for (const char* a : kAllow) if (!_stricmp(a, key)) return true;
    return false;
}
// "Section.Key=Value" -> parts; false = malformed
static bool AdSplitEntry(const char* e, char* sec, size_t sc, char* key, size_t kc, char* val, size_t vc) {
    const char* dot = strchr(e, '.');
    const char* eq = strchr(e, '=');
    if (!dot || !eq || dot > eq || dot == e || eq == dot + 1 || !eq[1]) return false;
    if ((size_t)(dot - e) >= sc || (size_t)(eq - dot - 1) >= kc || strlen(eq + 1) >= vc) return false;
    memcpy(sec, e, dot - e), sec[dot - e] = 0;
    memcpy(key, dot + 1, eq - dot - 1), key[eq - dot - 1] = 0;
    SCpy(val, vc, eq + 1);
    return true;
}
// noise grade of one minute: C = discounted in summaries (other programs >= 10% CPU, clock < 85% of the session's best,
// or a firmware / OS limit flagged in most busy samples), B = some noise, A = clean, '?' = no PcCheck data
static char AdNoiseGrade(bool have, double otherPct, double perfRel, double flaggedShare, bool avBusy) {
    if (!have) return '?';
    if (otherPct >= 10 || (perfRel > 0 && perfRel < 0.85) || flaggedShare > 0.5) return 'C';
    if (otherPct >= 5 || (perfRel > 0 && perfRel < 0.95) || flaggedShare > 0 || avBusy) return 'B';
    return 'A';
}
// one known-issue row: id \t kind \t edition \t match \t label \t doc \t fixedin \t switch ('#' = comment)
struct AdSig { char id[24], kind[8], ed[8], match[96], label[96], doc[32], fixed[24], sw[48]; };
static bool AdSigParse(const char* line, AdSig* s) {
    if (!line[0] || line[0] == '#') return false;
    char* f[8] = {s->id, s->kind, s->ed, s->match, s->label, s->doc, s->fixed, s->sw};
    const size_t cap[8] = {sizeof(s->id), sizeof(s->kind), sizeof(s->ed), sizeof(s->match), sizeof(s->label), sizeof(s->doc), sizeof(s->fixed), sizeof(s->sw)};
    const char* p = line;
    for (int i = 0; i < 8; i++) {
        const char* t = strchr(p, '\t');
        size_t n = t ? (size_t)(t - p) : strcspn(p, "\r\n");
        if (n >= cap[i]) n = cap[i] - 1;
        memcpy(f[i], p, n), f[i][n] = 0;
        if (!t) { if (i < 3) return false; for (int j = i + 1; j < 8; j++) f[j][0] = 0; break; }
        p = t + 1;
    }
    return s->match[0] != 0;
}
static const char* AdStrIStr(const char* h, const char* n) {
    const size_t nl = strlen(n);
    if (!nl) return NULL;
    for (; *h; h++) if (!_strnicmp(h, n, nl)) return h;
    return NULL;
}
// kind: "crash" / "script" / "hitch" / "log"; a signature of kind "any" matches every kind; edition "any" or ours
static bool AdSigMatch(const AdSig& s, const char* kind, const char* edition, const char* text) {
    if (_stricmp(s.kind, "any") && _stricmp(s.kind, kind)) return false;
    if (_stricmp(s.ed, "any") && _stricmp(s.ed, edition)) return false;
    return AdStrIStr(text, s.match) != NULL;
}
static void AdSigLabel(const AdSig& s, char* out, size_t cap) {
    _snprintf_s(out, cap, _TRUNCATE, "known: %s (%s)%s%s%s%s", s.label, s.doc[0] ? s.doc : "-", s.fixed[0] ? ", fixed in " : "", s.fixed,
                s.sw[0] ? " / switch " : "", s.sw);
}
// the activity of the most seconds (ties: the lower tag)
static int AdDominant(const LONG* secs) {
    int b = 0;
    for (int i = 1; i < AD_ACTS; i++) if (secs[i] > secs[b]) b = i;
    return b;
}
// drill-down picks: indices of cands (rank > 0, sorted by rank inside) that fit maxActive and the budget, the best first;
// returns how many. A candidate that does not fit is skipped, a cheaper lower one may still fit.
static int AdPick(const LONG* rank, const LONG* costUs, int n, int maxActive, LONG budgetUs, int* out) {
    int order[16], m = 0;
    for (int i = 0; i < n && m < 16; i++) if (rank[i] > 0) order[m++] = i;
    for (int i = 1; i < m; i++) for (int j = i; j > 0 && rank[order[j]] > rank[order[j - 1]]; j--) { const int t = order[j]; order[j] = order[j - 1], order[j - 1] = t; }
    int k = 0;
    LONG used = 0;
    for (int i = 0; i < m && k < maxActive; i++) {
        if (used + costUs[order[i]] > budgetUs) continue;
        used += costUs[order[i]], out[k++] = order[i];
    }
    return k;
}

// ---- state ----
static LONG g_adOn, g_adDeferred, g_adBench;
static char g_adArm[20], g_adBenchName[44], g_adBenchRun[44], g_adEdition[8];
static wchar_t g_adBin[MAX_PATH], g_adState[MAX_PATH];  // Bin folder with a trailing '\', state file
static char g_adStartNote[1600];                        // what the overlay applied (logged once the log is open)
static LONG g_adPrevUnclean, g_adSwapDiag;
static DWORD g_adSession;
static int g_adMaxActive = 3, g_adTtlMin = 15;
static LONG g_adBudgetUs = 5000, g_adLineCapKB = 4096, g_adLogCapMB = 200;

static void AdPath(wchar_t* out, const wchar_t* name) { PathFmt(out, MAX_PATH, L"%s%s", g_adBin, name); }

static void AdLoadLists();
// Before CrPreLog: overrides from the state file into a copy of the ini (ini is MAX_PATH and changes to the copy).
static void AdOverlay(wchar_t* ini) {
    if (!GetPrivateProfileIntW(L"AdaptDiag", L"Enabled", 0, ini)) return;
    WCpy(g_adBin, ini);
    wchar_t* slash = wcsrchr(g_adBin, L'\\');
    if (!slash) return;
    slash[1] = 0;
    AdPath(g_adState, L"ts3native.adaptdiag-state.ini");
    const bool have = GetFileAttributesW(g_adState) != INVALID_FILE_ATTRIBUTES;
    g_adPrevUnclean = have && !GetPrivateProfileIntW(L"Live", L"Clean", 1, g_adState);
    AdLoadLists();
    wchar_t sec[4096] = L"";
    const DWORD got = GetPrivateProfileSectionW(L"Escalate", sec, 4096, g_adState);
    char ent[24][96];
    int ne = 0;
    for (const wchar_t* p = sec; p < sec + got && *p && ne < 24; p += wcslen(p) + 1) {
        char a[96];
        WideCharToMultiByte(CP_ACP, 0, p, -1, a, sizeof(a), NULL, NULL);
        if (!_strnicmp(a, "Rule=", 5)) continue;
        SCpy(ent[ne++], a);
    }
    if (g_adPrevUnclean && ne < 22) SCpy(ent[ne++], "CrashForensics.Enabled=1"), SCpy(ent[ne++], "CrashForensics.VbGuard=1");
    if (!ne) return;
    wchar_t copy[MAX_PATH];
    AdPath(copy, L"ts3native.adapt.ini");
    if (!CopyFileW(ini, copy, FALSE)) { _snprintf_s(g_adStartNote, _TRUNCATE, "adaptdiag: FAIL overlay copy (err %lu): next-session turn-ons not applied", GetLastError()); return; }
    _snprintf_s(g_adStartNote, _TRUNCATE, "adaptdiag: next-session turn-on%s (ts3native.adapt.ini):", g_adPrevUnclean ? " (last session ended without a clean exit)" : "");
    wchar_t applied[2048] = L"";
    size_t an = 0;
    for (int i = 0; i < ne; i++) {
        char s[32], k[40], v[24];
        const bool ok = AdSplitEntry(ent[i], s, sizeof(s), k, sizeof(k), v, sizeof(v));
        char key[80];
        _snprintf_s(key, _TRUNCATE, "%s.%s", s, k);
        if (!ok || !AdAllowed(key)) { strncat_s(g_adStartNote, " [ignored ", _TRUNCATE), strncat_s(g_adStartNote, ent[i], _TRUNCATE), strncat_s(g_adStartNote, "]", _TRUNCATE); continue; }
        wchar_t ws[32], wk[40], wv[24];
        MultiByteToWideChar(CP_ACP, 0, s, -1, ws, 32), MultiByteToWideChar(CP_ACP, 0, k, -1, wk, 40), MultiByteToWideChar(CP_ACP, 0, v, -1, wv, 24);
        WritePrivateProfileStringW(ws, wk, wv, copy);
        strncat_s(g_adStartNote, " ", _TRUNCATE), strncat_s(g_adStartNote, ent[i], _TRUNCATE);
        // double-NUL list for WritePrivateProfileSectionW; docs/323 lane B: _TRUNCATE (an entry is up to ~100 chars past
        // an >= 2040 - 100, where swprintf_s killed the game) and room kept for the closing NUL
        const int w = _snwprintf_s(applied + an, 2047 - an, _TRUNCATE, L"%hs", ent[i]);
        if (w < 0) { applied[an] = 0; break; }
        an += w + 1;
        if (an >= 2040) break;
    }
    applied[an] = 0;
    WritePrivateProfileStringW(L"Escalate", NULL, NULL, g_adState);  // consumed: one session each
    WritePrivateProfileSectionW(L"Applied", applied, g_adState);
    WCpy(ini, MAX_PATH, copy);
}

// ---- level 0 inputs ----
static volatile LONG g_adAct = AD_LOAD;  // ts3fixes op 63 (sub 0); load until the first world is live
static LONG AdActNow() { return g_udSaveTick ? AD_SAVE : g_adAct; }
static LONGLONG g_adLastIterQ;
static LONG g_adIterAct = AD_LOAD;
static volatile LONG g_adIt[AD_ACTS], g_adIt250[AD_ACTS], g_adIt1000[AD_ACTS], g_adMaxMs;
// kit test only ([AdaptDiag] InduceHitchMs / InduceScriptErrors, default 0, docs/316 §10): the smoke's induced symptoms
static LONG g_adInduceMs, g_adInduceAfterS = 120, g_adInduceErr, g_adInduced;
static LONGLONG g_adLiveQ, g_adInduceQ;
static void __cdecl AdIter() {  // SimLoopAdd: end of every Simulate iteration (sim thread)
    static LARGE_INTEGER f;
    LARGE_INTEGER q;
    if (!f.QuadPart) QueryPerformanceFrequency(&f);
    QueryPerformanceCounter(&q);
    const LONG a = AdActNow();
    if (g_adLastIterQ) {
        const LONG ms = (LONG)((q.QuadPart - g_adLastIterQ) * 1000 / f.QuadPart);
        const LONG at = g_adIterAct == a ? a : AD_LOAD;  // an iteration across a phase change (a world load) counts as load
        g_adIt[at]++;
        if (ms >= 250) g_adIt250[at]++;
        if (ms >= 1000) g_adIt1000[at]++;
        if (at != AD_LOAD && at != AD_TRAVEL && at != AD_SAVE && ms > g_adMaxMs) g_adMaxMs = ms;
    }
    g_adLastIterQ = q.QuadPart, g_adIterAct = a;
    if (g_adInduceMs && a == AD_LIVE && g_adInduced < 3) {  // 3 sim-thread sleeps 20 s apart, InduceAfterS after the world went live
        if (!g_adLiveQ) g_adLiveQ = q.QuadPart;
        if ((q.QuadPart - g_adLiveQ) / f.QuadPart >= g_adInduceAfterS && (!g_adInduceQ || (q.QuadPart - g_adInduceQ) / f.QuadPart >= 20)) {
            g_adInduceQ = q.QuadPart;
            Log("adaptdiag: induced hitch %ld of 3: %ld ms on the sim thread (kit test, [AdaptDiag] InduceHitchMs)", ++g_adInduced, g_adInduceMs);
            Sleep((DWORD)g_adInduceMs);
        }
    }
}

// known issues and the install check list (read once at start; immutable afterwards, so the log tap reads them lock-free)
static AdSig* g_adSig;
static int g_adSigN;
static volatile LONG g_adSigHits[128];
static char g_adExp[256][64];
static volatile u8 g_adExpSeen[256];
static int g_adExpN;
static char g_adAllow[64][64];  // expect file "allow: Type.Method": ts3fixes patches this edition may fail
static int g_adAllowN;
static volatile u8 g_adExpBad[256];  // 1 = a SKIP / FAIL / REFUSED line named it
static char g_adFixFail[400];  // ts3fixes "adaptinstall:" failed patches (names), under g_adCs
static volatile LONG g_adFixSeen, g_adFixFailN;
static int AdReadLines(const wchar_t* path, char* buf, int cap) {
    HANDLE h = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL, OPEN_EXISTING, 0, NULL);
    if (h == INVALID_HANDLE_VALUE) return -1;
    DWORD n = 0;
    ReadFile(h, buf, cap - 1, &n, NULL);
    CloseHandle(h);
    buf[n] = 0;
    return (int)n;
}

// evidence lines the drill-downs and symptoms count (log tap)
enum { AP_HITCH, AP_STALL, AP_MODTIME, AP_RTJOB, AP_LOCK, AP_JOBWAIT, AP_SWAPINV, AP_MEMDIAG, AP_FORENSICS, AP_LOADPROF, AP_N };
static const char* const kAdPfx[AP_N] = {"hitch:", "stall", "modtime:", "rtjob wait:", "route lock wait:", "route jobs:", "ts3fixes: swapinvokediag",
                                         "memdiag", "forensics", "loadprof"};
static volatile LONG g_adPfxN[AP_N];
static char g_adLastHitch[600];  // the last hitch / stall line (known-issue evidence), under g_adCs (TryEnter only in the tap)
static CRITICAL_SECTION g_adCs;
static volatile LONG g_adInTap;

static void AdTap(const char* line) {
    if (!strncmp(line, "adaptdiag:", 10)) return;  // our own lines quote evidence: never count them again
    if (!strncmp(line, "ts3fixes: adaptinstall: ", 24)) {  // AdaptDiag.cs: "patched N, failed M: Type.Method; ..."
        long okN = 0, failN = 0;
        if (sscanf_s(line + 24, "patched %ld, failed %ld", &okN, &failN) == 2) {
            g_adFixFailN = failN, g_adFixSeen = 1;
            const char* c = strstr(line, ": ") ? strchr(line + 24, ':') : NULL;
            if (c && TryEnterCriticalSection(&g_adCs)) { strncpy_s(g_adFixFail, c + 1, _TRUNCATE); LeaveCriticalSection(&g_adCs); }
        }
        return;
    }
    for (int i = 0; i < AP_N; i++)
        if (!strncmp(line, kAdPfx[i], strlen(kAdPfx[i]))) {
            InterlockedIncrement(&g_adPfxN[i]);
            if ((i == AP_HITCH || i == AP_STALL) && TryEnterCriticalSection(&g_adCs)) {  // never blocks: a sampler may hold a suspended thread
                strncpy_s(g_adLastHitch, line, _TRUNCATE);
                LeaveCriticalSection(&g_adCs);
            }
        }
    // install check: OK lines and ts3fixes "patched" lines mark an expected part seen; SKIP / FAIL / REFUSED name it bad
    const bool ok = !strncmp(line, "OK   ", 5) || (!strncmp(line, "ts3fixes: ", 10) && strstr(line, "patched") && !strstr(line, "not patched"));
    const bool bad = !strncmp(line, "SKIP ", 5) || !strncmp(line, "FAIL ", 5) || strstr(line, "REFUSED") || strstr(line, "not patched");
    if (ok || bad)
        for (int i = 0; i < g_adExpN; i++)
            if (strstr(line, g_adExp[i])) { if (ok) g_adExpSeen[i] = 1; else g_adExpBad[i] = 1; }
    for (int i = 0; i < g_adSigN && i < 128; i++)
        if (!_stricmp(g_adSig[i].kind, "log") && AdSigMatch(g_adSig[i], "log", g_adEdition, line)) InterlockedIncrement(&g_adSigHits[i]);
}

// From AdOverlay (before the log opens, so the install check sees every start line): edition, known-issue library and the
// expected-install list (kit files next to the asi), then the log tap.
static void AdLoadLists() {
#if defined(TS3A_TABLE) && defined(TS3A_RETAIL)
    SCpy(g_adEdition, "retail");
#elif defined(TS3A_TABLE)
    SCpy(g_adEdition, "ea");
#else
    SCpy(g_adEdition, "steam");
#endif
    static char buf[131072];
    wchar_t p[MAX_PATH];
    AdPath(p, L"ts3native.adaptdiag-known.tsv");
    if (AdReadLines(p, buf, sizeof(buf)) > 0) {
        g_adSig = (AdSig*)calloc(256, sizeof(AdSig));
        char* tk = NULL;
        for (char* l = strtok_s(buf, "\r\n", &tk); l && g_adSig && g_adSigN < 256; l = strtok_s(NULL, "\r\n", &tk)) if (AdSigParse(l, &g_adSig[g_adSigN])) g_adSigN++;
    }
    AdPath(p, L"ts3native.adaptdiag-expect.txt");
    if (AdReadLines(p, buf, sizeof(buf)) > 0) {
        char* tk = NULL;
        for (char* l = strtok_s(buf, "\r\n", &tk); l; l = strtok_s(NULL, "\r\n", &tk)) {
            if (!l[0] || l[0] == '#') continue;
            if (!_strnicmp(l, "allow: ", 7)) { if (g_adAllowN < 64) strncpy_s(g_adAllow[g_adAllowN++], l + 7, _TRUNCATE); }
            else if (g_adExpN < 256) strncpy_s(g_adExp[g_adExpN++], l, _TRUNCATE);
        }
    }
    InitializeCriticalSection(&g_adCs);
    g_logTap = AdTap;
}

// newest ScriptError*.xml in the user folder since the session start: count, and the newest file's first 700 bytes
static wchar_t g_adUser[MAX_PATH];
static FILETIME g_adStartFt;
static LONG AdScriptErrors(char* head, size_t cap) {
    if (!g_adUser[0]) return 0;
    wchar_t pat[MAX_PATH], best[MAX_PATH] = L"";
    PathFmt(pat, L"%s\\ScriptError*.xml", g_adUser);
    WIN32_FIND_DATAW f;
    FILETIME newest = g_adStartFt;
    LONG n = 0;
    HANDLE h = FindFirstFileExW(pat, FindExInfoBasic, &f, FindExSearchNameMatch, NULL, 0);
    for (BOOL ok = h != INVALID_HANDLE_VALUE; ok; ok = FindNextFileW(h, &f)) {
        if (CompareFileTime(&f.ftLastWriteTime, &g_adStartFt) <= 0) continue;
        n++;
        if (CompareFileTime(&f.ftLastWriteTime, &newest) > 0) newest = f.ftLastWriteTime, PathFmt(best, L"%s\\%s", g_adUser, f.cFileName);
    }
    if (h != INVALID_HANDLE_VALUE) FindClose(h);
    if (head && best[0]) {
        char b[704];
        const int r = AdReadLines(best, b, sizeof(b));
        if (r > 0) { for (char* p = b; *p; p++) if (*p == '\r' || *p == '\n' || *p == '\t') *p = ' '; strncpy_s(head, cap, b, _TRUNCATE); }
    }
    return n;
}

static bool AdOwnFile(const wchar_t* n) {
    static const wchar_t* const kOwn[] = {L"ts3native", L"ts3fixes", L"adaptdiag", L"modtime", L"pccheck", L"taskseam", L"imgui.ini", L"hitch", L"memdiag"};
    for (const wchar_t* o : kOwn) if (!_wcsnicmp(n, o, wcslen(o))) return true;
    return false;
}
// mod set (Mods\Packages names + sizes + times) and mod config (content of small config files) hashes
static void AdHashTree(const wchar_t* dir, int depth, bool content, const wchar_t* const* exts, unsigned long long* h, LONG* files) {
    wchar_t pat[MAX_PATH], p[MAX_PATH];
    if (!PathFmt(pat, L"%s\\*", dir)) return;
    WIN32_FIND_DATAW f;
    HANDLE fh = FindFirstFileExW(pat, FindExInfoBasic, &f, FindExSearchNameMatch, NULL, 0);
    for (BOOL ok = fh != INVALID_HANDLE_VALUE; ok; ok = FindNextFileW(fh, &f)) {
        if (f.cFileName[0] == L'.' || !PathFmt(p, L"%s\\%s", dir, f.cFileName)) continue;
        if (f.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) { if (depth < 6) AdHashTree(p, depth + 1, content, exts, h, files); continue; }
        const wchar_t* ext = wcsrchr(f.cFileName, L'.');
        bool want = false;
        for (int i = 0; exts[i]; i++) if (ext && !_wcsicmp(ext, exts[i])) want = true;
        // our own files are what an A/B arm changes (asi, ini, ts3fixes.package) and our outputs change every session: not hashed
        if (!want || AdOwnFile(f.cFileName)) continue;
        wchar_t lower[MAX_PATH];
        WCpy(lower, f.cFileName), _wcslwr_s(lower);
        *h = AdFnv(*h, lower, wcslen(lower) * 2);
        (*files)++;
        if (!content) { *h = AdFnv(*h, &f.nFileSizeLow, 4), *h = AdFnv(*h, &f.ftLastWriteTime, 8); continue; }
        if (f.nFileSizeHigh || f.nFileSizeLow > (1u << 20)) { *h = AdFnv(*h, &f.nFileSizeLow, 4); continue; }
        char* b = (char*)malloc(f.nFileSizeLow + 1);
        const int r = b ? AdReadLines(p, b, (int)f.nFileSizeLow + 1) : -1;
        if (r > 0) *h = AdFnv(*h, b, r);
        free(b);
    }
    if (fh != INVALID_HANDLE_VALUE) FindClose(fh);
}
static unsigned long long g_adModSet, g_adConfig;
static LONG g_adModFiles, g_adConfFiles;
static void AdHashMods() {
    static const wchar_t* const kPkg[] = {L".package", NULL};
    static const wchar_t* const kCfg[] = {L".cfg", L".ini", L".toml", L".xml", L".json", L".conf", NULL};  // not .txt: logs and summaries
    unsigned long long m = kAdFnv0, c = kAdFnv0;
    wchar_t d[MAX_PATH];
    if (g_adUser[0] && PathFmt(d, L"%s\\Mods\\Packages", g_adUser)) AdHashTree(d, 0, false, kPkg, &m, &g_adModFiles);
    if (g_adUser[0] && PathFmt(d, L"%s\\Mods", g_adUser)) AdHashTree(d, 5, true, kCfg, &c, &g_adConfFiles);  // depth 5: Mods top + one level
    WCpy(d, g_adBin);
    d[wcslen(d) - 1] = 0;
    AdHashTree(d, 5, true, kCfg, &c, &g_adConfFiles);  // Game\Bin (ts3native.ini, S3SS, Apex configs) + one level
    g_adModSet = m, g_adConfig = c;
}

// thermal zones (PDH, Kelvin; no admin) and CPU noise from [PcCheck]'s samples
typedef LONG(WINAPI* AdPdhArr)(HANDLE, DWORD, LPDWORD, LPDWORD, void*);
static HANDLE g_adPdhQ, g_adPdhT;
static AdPdhArr g_adPdhArr;
static PcPdhCollect g_adPdhCol;
static void AdTempInit() {
    HMODULE m = LoadLibraryW(L"pdh.dll");
    if (!m) return;
    PcPdhOpen op = (PcPdhOpen)(void*)GetProcAddress(m, "PdhOpenQueryW");
    PcPdhAdd add = (PcPdhAdd)(void*)GetProcAddress(m, "PdhAddEnglishCounterW");
    g_adPdhCol = (PcPdhCollect)(void*)GetProcAddress(m, "PdhCollectQueryData");
    g_adPdhArr = (AdPdhArr)(void*)GetProcAddress(m, "PdhGetFormattedCounterArrayW");
    if (!op || !add || !g_adPdhCol || !g_adPdhArr || op(NULL, 0, &g_adPdhQ) || add(g_adPdhQ, L"\\Thermal Zone Information(*)\\Temperature", 0, &g_adPdhT)) g_adPdhT = NULL;
}
static double AdTempC() {  // hottest zone, 0 = not readable
    if (!g_adPdhT || g_adPdhCol(g_adPdhQ)) return 0;
    struct Item { wchar_t* name; PcPdhVal val; } items[32];
    DWORD size = sizeof(items), n = 0;
    if (g_adPdhArr(g_adPdhT, 0x200 /* PDH_FMT_DOUBLE */, &size, &n, items) || n > 32) return 0;
    double best = 0;
    for (DWORD i = 0; i < n; i++) if (!items[i].val.status && items[i].val.v > best) best = items[i].val.v;
    return best > 200 ? best - 273.15 : 0;
}
static const char* const kAdAv[] = {"MsMpEng.exe", "SearchIndexer.exe", "SearchProtocolHost.exe", "avp.exe", "ekrn.exe", "bdservicehost.exe",
                                    "AvastSvc.exe", "AVGSvc.exe", "mcshield.exe", "NortonSecurity.exe", "SavService.exe", "MBAMService.exe", "onedrive.exe"};
static void AdAvNames(char* out, size_t cap) {  // AV / indexer processes present (names only)
    out[0] = 0;
    HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snap == INVALID_HANDLE_VALUE) return;
    bool seen[sizeof(kAdAv) / sizeof(kAdAv[0])] = {};
    PROCESSENTRY32W pe = {sizeof(pe)};
    for (BOOL ok = Process32FirstW(snap, &pe); ok; ok = Process32NextW(snap, &pe)) {
        char a[MAX_PATH];
        WideCharToMultiByte(CP_ACP, 0, pe.szExeFile, -1, a, sizeof(a), NULL, NULL);
        for (size_t i = 0; i < sizeof(kAdAv) / sizeof(kAdAv[0]); i++) if (!seen[i] && !_stricmp(a, kAdAv[i])) seen[i] = true;
    }
    CloseHandle(snap);
    for (size_t i = 0; i < sizeof(kAdAv) / sizeof(kAdAv[0]); i++) if (seen[i]) PcCat(out, cap, "%s%s", out[0] ? "+" : "", kAdAv[i]);
}

// ---- drill-downs ----
enum { AD_HITCHSTACK, AD_ROUTEWAIT, AD_SCRIPTERR, AD_MEMOWNER, AD_LOADSPLIT, AD_CRASH, AD_DRILLS };
struct AdDrillDef {
    const char* id; const char* detector; LONG costUs; bool runtime; const char* next; int pfx[3]; const char* step;
};
static const AdDrillDef kAdDrill[AD_DRILLS] = {
    {"hitchstack", "[HitchAttr] HitchMs 250 + [StallWatch] 1 s / 250 ms + [Diag] ModTime 50 Hz", 3500, true,
     "HitchAttr.HitchMs=250 StallWatch.Enabled=1 StallWatch.StallMs=1000 StallWatch.EveryMs=250 StallWatch.Names=1 StallWatch.Managed=8 StallWatch.StackKB=256 StallWatch.KeepNoSim=1 Diag.ModTime=1 Diag.ModTimeHz=50",
     {AP_HITCH, AP_STALL, AP_MODTIME}, "send the zip: the hitch lines give the cause class, the stall / modtime stacks the method and mod"},
    {"routewait", "[HitchAttr] JobNameMs 100 + RouteLockMs 50", 50, true, "HitchAttr.JobNameMs=100 HitchAttr.RouteLockMs=50 RouteJobPhases.SyncPlans=1 HitchAttr.Holders=1",
     {AP_RTJOB, AP_LOCK, AP_JOBWAIT}, "send the zip: rtjob / route lock lines name the route job and who held the lock"},
    {"scripterr", "ts3fixes swapinvokediag (next session) + the ScriptError files", 0, false, "AdaptDiag.SwapInvokeDiag=1", {AP_SWAPINV, -1, -1},
     "play the same way once more with the kit's diag build; send the zip with the ScriptError files"},
    {"memowner", "[MemDiag] AllocSites + owners (next session)", 0, false, "MemDiag.Enabled=1 MemDiag.AllocSites=1 MemDiag.AllocSitesOwners=1", {AP_MEMDIAG, -1, -1},
     "play once more to the same point; the memdiag lines name the allocation owners"},
    {"loadsplit", "[LoadProf] load split (next session)", 0, false, "LoadProf.Enabled=1", {AP_LOADPROF, -1, -1},
     "load the same save once more; the loadprof lines split the load time"},
    {"crashforensics", "[CrashForensics] + VbGuard (this session, after an unclean exit)", 0, false, "", {AP_FORENSICS, -1, -1},
     "play to the same point; a crash now leaves forensics lines and a crash report"},
};
struct AdIssue {  // one per drill-down kind (the symptom that asks for it)
    LONG sev, freq, rank, onMin, offMin, logAt, pfxAt[3], causeAt[HSC_N], turnOns; char evidence[600], known[200], finding[300], why[48];
    bool active, deferred, nextWritten;
};
static AdIssue g_adIss[AD_DRILLS];
static LONG g_adMin;  // minutes since start

static LONGLONG AdLogBytes() { LARGE_INTEGER s = {}; return g_log != INVALID_HANDLE_VALUE && GetFileSizeEx(g_log, &s) ? s.QuadPart : 0; }
// saved thresholds (restored on off)
static LONG g_adSaveHitchUs, g_adSaveHsLines, g_adSaveMdtHz, g_adSaveRjn, g_adSaveLock;
static DWORD g_adSaveSs[3];
static LONG g_adSaveSsLines;
static void AdRuntime(int d, bool on) {
    HsTotals& z = ts3native_hitchsum;
    Ts3nCounters& k = ts3native_counters;
    if (d == AD_HITCHSTACK) {
        if (on) {
            g_adSaveHitchUs = z.hitchUs, g_adSaveHsLines = g_hsLinesPerMin, g_adSaveMdtHz = g_mdtHz;
            g_adSaveSs[0] = g_ssStallMs, g_adSaveSs[1] = g_ssEveryMs, g_adSaveSs[2] = g_ssPollMs, g_adSaveSsLines = g_ssLines;
            if (g_hsOn && z.hitchUs > 250000) z.hitchUs = 250000, g_hsLinesPerMin = 120;
            if (k.ssReady && g_ssStallMs > 1000) g_ssStallMs = 1000, g_ssEveryMs = 250, g_ssPollMs = 333, g_ssLines = 8;
            if (g_mdtOn && g_mdtHz < 50) g_mdtHz = 50;
        } else {
            if (g_hsOn) z.hitchUs = g_adSaveHitchUs, g_hsLinesPerMin = g_adSaveHsLines;
            if (k.ssReady) g_ssStallMs = g_adSaveSs[0], g_ssEveryMs = g_adSaveSs[1], g_ssPollMs = g_adSaveSs[2], g_ssLines = g_adSaveSsLines;
            if (g_mdtOn) g_mdtHz = g_adSaveMdtHz;
        }
    } else if (d == AD_ROUTEWAIT) {
        if (on) g_adSaveRjn = k.rjnMinUs, g_adSaveLock = g_hsLockLogUs, k.rjnMinUs = 100000, InterlockedExchange(&g_hsLockLogUs, 50000);
        else k.rjnMinUs = g_adSaveRjn, InterlockedExchange(&g_hsLockLogUs, g_adSaveLock);
    }
}
static bool AdRuntimeHas(int d) {  // the runtime drill-down has an installed detector to turn up
    return d == AD_HITCHSTACK ? (g_hsOn || ts3native_counters.ssReady || g_mdtOn) : d == AD_ROUTEWAIT;
}
static void AdWriteNext(int d, const char* why) {
    AdIssue& s = g_adIss[d];
    if (s.nextWritten || !kAdDrill[d].next[0]) return;
    s.nextWritten = true;
    char buf[400];
    SCpy(buf, kAdDrill[d].next);
    char* tk = NULL;
    for (char* t = strtok_s(buf, " ", &tk); t; t = strtok_s(NULL, " ", &tk)) {
        char sec[32], key[40], val[24];
        if (!AdSplitEntry(t, sec, sizeof(sec), key, sizeof(key), val, sizeof(val))) continue;
        char e[80];
        _snprintf_s(e, _TRUNCATE, "%s.%s", sec, key);
        wchar_t we[80], wv[24];
        MultiByteToWideChar(CP_ACP, 0, e, -1, we, 80), MultiByteToWideChar(CP_ACP, 0, val, -1, wv, 24);
        WritePrivateProfileStringW(L"Escalate", we, wv, g_adState);
    }
    Log("adaptdiag: next session turns on %s for %s (%s)", kAdDrill[d].id, why, kAdDrill[d].next);
}
static void AdOn(int d) {
    AdIssue& s = g_adIss[d];
    s.active = true, s.onMin = g_adMin, s.logAt = (LONG)(AdLogBytes() >> 10), s.turnOns++;
    for (int i = 0; i < 3; i++) s.pfxAt[i] = kAdDrill[d].pfx[i] >= 0 ? g_adPfxN[kAdDrill[d].pfx[i]] : 0;
    for (int i = 0; i < HSC_N; i++) s.causeAt[i] = ts3native_hitchsum.causeN[i];
    AdRuntime(d, true);
    Log("adaptdiag: turn on %s for %s (rank %ld = severity %ld x %ld; ttl %d min, line cap %ld KB, est %ld us/s): %s", kAdDrill[d].id, s.why, s.rank, s.sev, s.freq,
        g_adTtlMin, g_adLineCapKB, kAdDrill[d].costUs, kAdDrill[d].detector);
}
static void AdFinding(int d) {
    AdIssue& s = g_adIss[d];
    s.finding[0] = 0;
    for (int i = 0; i < 3; i++) {
        const int p = kAdDrill[d].pfx[i];
        if (p >= 0) PcCat(s.finding, sizeof(s.finding), "%s%ld \"%s\" lines", i ? ", " : "", g_adPfxN[p] - (s.active || s.offMin ? s.pfxAt[i] : 0), kAdPfx[p]);
    }
    if (d == AD_HITCHSTACK && g_hsOn) {
        int b = -1;
        LONG bn = 0;
        for (int i = 0; i < HSC_N; i++) { const LONG n = ts3native_hitchsum.causeN[i] - s.causeAt[i]; if (n > bn) bn = n, b = i; }
        if (b >= 0) PcCat(s.finding, sizeof(s.finding), "; top hitch cause %s (%ld)", kHsCause[b], bn);
    }
}
static void AdOff(int d, const char* reason) {
    AdIssue& s = g_adIss[d];
    if (!s.active) return;
    AdFinding(d);
    s.active = false, s.offMin = g_adMin;
    AdRuntime(d, false);
    Log("adaptdiag: turn off %s after %ld min (%s): %s", kAdDrill[d].id, g_adMin - s.onMin, reason, s.finding);
    AdWriteNext(d, "the run-time drill-down ended: deeper detectors next session");  // HitchAttr -> stall / ModTime stacks per method
}

// ---- per minute ----
struct AdRow { u8 act; char noise; LONG it, i250, i1000, maxMs, freeMB, commitMB, err; float other, perf, temp; bool bench; };
static AdRow g_adRows[1440];
static LONG g_adRowN;
static LONG g_adActRun[AD_ACTS];  // minutes in a row of that activity (ending now)
static char g_adErrHead[704];
static wchar_t g_adCsv[MAX_PATH], g_adRep[MAX_PATH], g_adSess[MAX_PATH];
static char g_adAv[200], g_adAbNote[300], g_adInstall[1200];
static LONG g_adInstallMissing = -1;  // -1 = not checked yet (op 63 sub 1 answers 0 then)
static LONG g_adWorldLiveMin = -1;
static ULONGLONG g_adOwnCpu100;

static void AdAppend(const wchar_t* path, const char* s) {
    HANDLE h = CreateFileW(path, FILE_APPEND_DATA, FILE_SHARE_READ, NULL, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (h == INVALID_HANDLE_VALUE) return;
    DWORD w;
    WriteFile(h, s, (DWORD)strlen(s), &w, NULL);
    CloseHandle(h);
}
static bool AdKnown(const char* kind, const char* text, char* out, size_t cap) {
    for (int i = 0; i < g_adSigN; i++)
        if (AdSigMatch(g_adSig[i], kind, g_adEdition, text)) { AdSigLabel(g_adSig[i], out, cap); return true; }
    return false;
}
// the newest crash report's "fault: " line (previous session; crashreport.cpp writes crash-*.txt in g_crDir)
static bool AdLastFault(char* out, size_t cap) {
    out[0] = 0;
    if (!g_crDir[0]) return false;
    wchar_t pat[MAX_PATH], best[MAX_PATH] = L"";
    FILETIME bt = {};
    if (!PathFmt(pat, L"%s\\crash-*.txt", g_crDir)) return false;
    WIN32_FIND_DATAW f;
    HANDLE h = FindFirstFileW(pat, &f);
    if (h == INVALID_HANDLE_VALUE) return false;
    do if (CompareFileTime(&f.ftLastWriteTime, &bt) > 0) bt = f.ftLastWriteTime, WCpy(best, f.cFileName); while (FindNextFileW(h, &f));
    FindClose(h);
    wchar_t p[MAX_PATH];
    static char buf[8192];
    if (!PathFmt(p, L"%s\\%s", g_crDir, best) || AdReadLines(p, buf, sizeof(buf)) <= 0) return false;
    const char* s = strstr(buf, "fault: ");
    if (!s) return false;
    size_t n = strcspn(s, "\r\n");
    strncpy_s(out, cap, s, n < cap - 1 ? n : cap - 1);
    return true;
}

// ts3fixes' failed patches ("A.B: reason; C.D: reason") minus the edition's allow list: how many count, names into out
static LONG AdFixMissing(const char* failed, char allow[][64], int nAllow, char* out, size_t cap) {
    LONG n = 0;
    char b[400];
    strncpy_s(b, failed, _TRUNCATE);
    for (char* ctx = NULL, *t = strtok_s(b, ";", &ctx); t; t = strtok_s(NULL, ";", &ctx)) {
        while (*t == ' ') t++;
        if (!*t) continue;
        bool ok = false;
        for (int i = 0; i < nAllow && !ok; i++) ok = !_strnicmp(t, allow[i], strlen(allow[i]));
        if (!ok) n++, PcCat(out, cap, "%sts3fixes %.80s", out[0] ? " | " : "", t);
    }
    return n;
}
static void AdInstallCheck() {
    LONG miss = 0;
    g_adInstall[0] = 0;
    for (int i = 0; i < g_adExpN; i++)
        if (!g_adExpSeen[i]) { miss++; PcCat(g_adInstall, sizeof(g_adInstall), "%s%s%s", miss > 1 ? " | " : "", g_adExp[i], g_adExpBad[i] ? " (SKIP/FAIL/REFUSED)" : " (no line)"); }
    if (!g_adFixSeen) miss++, PcCat(g_adInstall, sizeof(g_adInstall), "%sts3fixes adaptinstall line (ts3fixes.package missing, old or not loaded)", miss > 1 ? " | " : "");
    else if (g_adFixFailN > 0) {
        char f[400];
        EnterCriticalSection(&g_adCs);
        SCpy(f, g_adFixFail);
        LeaveCriticalSection(&g_adCs);
        miss += AdFixMissing(f, g_adAllow, g_adAllowN, g_adInstall, sizeof(g_adInstall));
    }
    g_adInstallMissing = miss;
    if (!g_adExpN) Log("adaptdiag: install check: no expected list (ts3native.adaptdiag-expect.txt missing); ts3fixes %s", g_adFixSeen ? g_adFixFailN ? "has failed patches" : "patched all" : "not seen");
    if (!g_adExpN && miss) Log("adaptdiag: install check: MISSING %ld: %s", miss, g_adInstall);
    else if (!g_adExpN) {}
    else if (miss) Log("adaptdiag: install check: MISSING %ld of %d expected parts: %s", miss, g_adExpN, g_adInstall);
    else Log("adaptdiag: install check: OK (%d of %d expected parts installed)", g_adExpN, g_adExpN);
}

static void AdReport(const char* why);
static void AdRules() {
    const LONG n = g_adRowN;
    if (!n) return;
    // symptoms over the last minutes of play (live / cas / buildbuy / paused rows; load, travel and save rows are not hitches)
    LONG h1000 = 0, h250 = 0, err = 0, maxFree = 0, routeLines = 0;
    for (LONG i = n - 1; i >= 0 && i >= n - 30; i--) {
        const AdRow& r = g_adRows[i % 1440];
        const bool play = r.act == AD_LIVE || r.act == AD_CAS || r.act == AD_BUILDBUY || r.act == AD_PAUSED;
        if (i >= n - 5 && play) h1000 += r.i1000, h250 += r.i250;
        if (i >= n - 10) err += r.err;
        if (play && r.freeMB > maxFree) maxFree = r.freeMB;
    }
    const AdRow& now = g_adRows[(n - 1) % 1440];
    static LONG lastRoute;
    routeLines = g_adPfxN[AP_JOBWAIT] - lastRoute;  // "route jobs: sim waited" lines since the last minute (JobWaitLogMs, default 300 ms)
    lastRoute = g_adPfxN[AP_JOBWAIT];
    static LONG routeRun;
    routeRun = routeLines ? routeRun + routeLines : 0;
    AdIssue* s = g_adIss;
    auto want = [&](int d, LONG sev, LONG freq, const char* why) { s[d].sev = sev, s[d].freq = freq, s[d].rank = sev * freq; SCpy(s[d].why, why); };
    for (int d = 0; d < AD_DRILLS; d++) if (!s[d].active) s[d].rank = 0;
    if (h1000 >= 2) want(AD_HITCHSTACK, 3, h1000, "repeated >= 1 s hitches");
    else if (h250 >= 10) want(AD_HITCHSTACK, 2, h250, "stutter (>= 250 ms)");
    if (routeRun >= 3) want(AD_ROUTEWAIT, 2, routeRun, "repeated route-job waits");
    if (err >= 2) want(AD_SCRIPTERR, err >= 5 ? 3 : 2, err, "repeated script errors");
    if (now.freeMB && now.freeMB < 256) want(AD_MEMOWNER, 4, 1, "address space low");
    else if (maxFree && maxFree - now.freeMB >= 300) want(AD_MEMOWNER, 3, (maxFree - now.freeMB) / 100, "address space shrinking");
    if (g_adActRun[AD_LOAD] >= 5) want(AD_LOADSPLIT, 2, g_adActRun[AD_LOAD] / 5, "slow load");
    // known issues: evidence -> signature -> no slot
    char ev[600];
    EnterCriticalSection(&g_adCs);
    SCpy(ev, g_adLastHitch);
    LeaveCriticalSection(&g_adCs);
    for (int d = 0; d < AD_DRILLS; d++) {
        if (!s[d].rank || s[d].known[0]) continue;
        const char* kind = d == AD_SCRIPTERR ? "script" : d == AD_HITCHSTACK || d == AD_ROUTEWAIT ? "hitch" : "log";
        const char* text = d == AD_SCRIPTERR ? g_adErrHead : ev;
        if (text[0]) strncpy_s(s[d].evidence, text, _TRUNCATE);
        if (text[0] && AdKnown(kind, text, s[d].known, sizeof(s[d].known))) {
            Log("adaptdiag: %s for %s matches a known issue, no drill-down: %s", kAdDrill[d].id, s[d].why, s[d].known);
            if (s[d].active) AdOff(d, "known issue");
        }
    }
    // TTL, line cap, log size cap
    const LONG logMB = (LONG)(AdLogBytes() >> 20);
    for (int d = 0; d < AD_DRILLS; d++) {
        if (!s[d].active) continue;
        if (g_adMin - s[d].onMin >= g_adTtlMin) AdOff(d, "ttl");
        else if ((LONG)(AdLogBytes() >> 10) - s[d].logAt > g_adLineCapKB) AdOff(d, "line cap");
        else if (logMB >= g_adLogCapMB) AdOff(d, "log size cap");
    }
    // pick: rank > 0, not known; runtime ones compete for slots and budget, install-time ones go to [Escalate]
    LONG rank[AD_DRILLS], cost[AD_DRILLS];
    for (int d = 0; d < AD_DRILLS; d++) {
        const bool cand = s[d].rank > 0 && !s[d].known[0];
        if (cand && (!kAdDrill[d].runtime || g_adDeferred || !AdRuntimeHas(d))) { AdWriteNext(d, s[d].why); s[d].deferred = true; }
        rank[d] = cand && kAdDrill[d].runtime && !g_adDeferred && AdRuntimeHas(d) && logMB < g_adLogCapMB && !(s[d].offMin && !s[d].active) ? s[d].rank : 0;
        if (s[d].active && !rank[d]) rank[d] = s[d].rank ? s[d].rank : 1;  // a running one keeps its slot until ttl / cap
        cost[d] = kAdDrill[d].costUs;
    }
    int pick[AD_DRILLS];
    const int k = AdPick(rank, cost, AD_DRILLS, g_adMaxActive, g_adBudgetUs, pick);
    bool keep[AD_DRILLS] = {};
    for (int i = 0; i < k; i++) keep[pick[i]] = true;
    for (int d = 0; d < AD_DRILLS; d++) if (s[d].active && !keep[d]) AdOff(d, "budget (a higher rank needs the room)");
    for (int i = 0; i < k; i++) if (!s[pick[i]].active) AdOn(pick[i]);
}

static void AdMinute(LONG* secs) {
    static LONG lastIt[AD_ACTS], last250[AD_ACTS], last1000[AD_ACTS], lastErr;
    static PcAcc lastPc;
    AdRow r = {};
    r.act = (u8)AdDominant(secs);
    for (int a = 0; a < AD_ACTS; a++) {
        r.it += g_adIt[a] - lastIt[a], r.i250 += g_adIt250[a] - last250[a], r.i1000 += g_adIt1000[a] - last1000[a];
        lastIt[a] = g_adIt[a], last250[a] = g_adIt250[a], last1000[a] = g_adIt1000[a];
        g_adActRun[a] = a == r.act ? g_adActRun[a] + 1 : 0;
    }
    r.maxMs = InterlockedExchange(&g_adMaxMs, 0);
    UdMemStat ms = {};
    UdMemWalk(ms, NULL);
    r.freeMB = (LONG)(ms.big1 >> 20), r.commitMB = (LONG)(ms.commit >> 20);
    const LONG errs = AdScriptErrors(g_adErrHead, sizeof(g_adErrHead));
    r.err = errs - lastErr, lastErr = errs;
    bool have = false;
    double perfRel = 0, flagged = 0;
    if (g_pcOn) {
        EnterCriticalSection(&g_pcCs);
        const PcAcc& a = g_pcAll;
        const LONG ns = a.samples - lastPc.samples, np = a.perfN - lastPc.perfN;
        if (ns > 0) have = true, r.other = (float)((a.otherPct - lastPc.otherPct) / ns);
        const int best = PcPerfMax(a.perfHist, kPcPerfBins);
        if (np > 0 && best > 0) r.perf = (float)((a.perfSum - lastPc.perfSum) / np), perfRel = r.perf / best, flagged = (double)(a.perfFlagged - lastPc.perfFlagged) / np;
        lastPc.samples = a.samples, lastPc.perfN = a.perfN, lastPc.otherPct = a.otherPct, lastPc.perfSum = a.perfSum, lastPc.perfFlagged = a.perfFlagged;
        LeaveCriticalSection(&g_pcCs);
    }
    if (g_adMin % 5 == 0) AdAvNames(g_adAv, sizeof(g_adAv));
    r.temp = (float)AdTempC();
    r.noise = AdNoiseGrade(have, r.other, perfRel, flagged, false);
    r.bench = g_adBench != 0;
    g_adRows[g_adRowN % 1440] = r, g_adRowN++;
    char drill[120] = "";
    for (int d = 0; d < AD_DRILLS; d++) if (g_adIss[d].active) PcCat(drill, sizeof(drill), "%s%s", drill[0] ? "+" : "", kAdDrill[d].id);
    char line[600];
    _snprintf_s(line, _TRUNCATE, "%ld,%s,%c,%d,%s,%.1f,%ld,%ld,%ld,%ld,%ld,%ld,%.1f,%.0f,%.0f,%s,%s\r\n", g_adMin, kAdAct[r.act], r.noise, r.bench ? 1 : 0, g_adArm,
                r.it / 60.0, r.i250, r.i1000, r.maxMs, r.freeMB, r.commitMB, r.err, r.other, r.perf, r.temp, g_adAv[0] ? g_adAv : "-", drill[0] ? drill : "-");
    AdAppend(g_adCsv, line);
    if (r.act == AD_LIVE && g_adWorldLiveMin < 0) g_adWorldLiveMin = g_adMin;
    if (g_adInstallMissing < 0 && g_adWorldLiveMin >= 0 && g_adMin - g_adWorldLiveMin >= 2) AdInstallCheck();
    AdRules();
}

static int AdCmpF(const void* a, const void* b) { const float x = *(const float*)a, y = *(const float*)b; return x < y ? -1 : x > y; }
static CRITICAL_SECTION g_adRepCs;
static void AdReport(const char* why) {
    if (!g_adOn || !TryEnterCriticalSection(&g_adRepCs)) return;  // never waits: at exit a killed thread may hold it
    static char rep[24000];
    rep[0] = 0;
    char* o = rep;
    const size_t cap = sizeof(rep);
    PcCat(o, cap, "adaptdiag report (docs/316, format docs/317), ts3native " TS3NATIVE_VERSION ", written %s at minute %ld\r\n\r\n", why, g_adMin);
    PcCat(o, cap, "## session\r\nsession %08lu, edition %s, arm %s, benchmark %ld%s%s, mod set %016llx (%ld packages), mod config %016llx (%ld files), "
                  "last session %s, own cost %.0f us/s (adaptdiag thread)\r\n%s%s\r\n",
          g_adSession, g_adEdition, g_adArm[0] ? g_adArm : "-", g_adBench, g_adBench ? " " : "", g_adBenchName, g_adModSet, g_adModFiles, g_adConfig, g_adConfFiles,
          g_adPrevUnclean ? "ended WITHOUT a clean exit (crash or kill)" : "clean or first", g_adMin ? g_adOwnCpu100 / 10.0 / (g_adMin * 60.0) : 0.0,
          g_adStartNote[0] ? g_adStartNote : "no next-session turn-ons", "\r\n");
    PcCat(o, cap, "## install check\r\n%s\r\n\r\n", g_adInstallMissing < 0 ? "not checked yet (runs 2 minutes into the first live world)"
                                                  : g_adInstallMissing ? g_adInstall : "OK: every expected part reported installed");
    PcCat(o, cap, "## activity summary\r\nactivity  minutes  clean  simfps_p50  hitch1s_per_clean_min  hitch250_per_clean_min  longest_ms\r\n");
    static float fps[1440];
    const LONG rows = g_adRowN < 1440 ? g_adRowN : 1440;
    for (int a = 0; a < AD_ACTS; a++) {
        LONG mins = 0, clean = 0, h1 = 0, h2 = 0, longest = 0;
        int nf = 0;
        for (LONG i = 0; i < rows; i++) {
            const AdRow& r = g_adRows[i];
            if (r.act != a) continue;
            mins++;
            if (r.noise == 'C') continue;  // noisy minutes are discounted
            clean++, h1 += r.i1000, h2 += r.i250, fps[nf++] = r.it / 60.0f;
            if (r.maxMs > longest) longest = r.maxMs;
        }
        if (!mins) continue;
        qsort(fps, nf, sizeof(float), AdCmpF);
        PcCat(o, cap, "%-9s %7ld %6ld %11.1f %22.2f %23.2f %11ld\r\n", kAdAct[a], mins, clean, nf ? fps[nf / 2] : 0.0f, clean ? (double)h1 / clean : 0.0,
              clean ? (double)h2 / clean : 0.0, longest);
    }
    LONG gr[4] = {};
    float hot = 0;
    for (LONG i = 0; i < rows; i++) if (g_adRows[i].temp > hot) hot = g_adRows[i].temp;
    char hotS[40];
    if (hot > 0) SFmt(hotS, "%.0f C", hot); else SCpy(hotS, "not readable without admin on this PC");
    for (LONG i = 0; i < rows; i++) gr[g_adRows[i].noise == 'A' ? 0 : g_adRows[i].noise == 'B' ? 1 : g_adRows[i].noise == 'C' ? 2 : 3]++;
    const char grade = gr[2] * 4 > rows ? 'C' : gr[2] + gr[1] > rows / 2 ? 'B' : rows && gr[3] == rows ? '?' : 'A';
    PcCat(o, cap, "\r\n## noise\r\nsession grade %c: %ld clean (A), %ld some noise (B), %ld noisy (C, left out of the summaries), %ld no PcCheck data; "
                  "AV / indexer present: %s; hottest thermal zone %s\r\n\r\n", grade, gr[0], gr[1], gr[2], gr[3], g_adAv[0] ? g_adAv : "none seen", hotS);
    if (g_adBench) {
        LONG bm = 0, bc = 0;
        for (LONG i = 0; i < rows; i++) if (g_adRows[i].bench) { bm++; if (g_adRows[i].noise != 'C') bc++; }
        PcCat(o, cap, "## benchmark\r\nname %s, run %s, %ld minutes (%ld clean); per-activity numbers above are this benchmark's (all rows are bench=1); "
                      "the benchmark's own result: adaptdiag-bench.txt in the zip\r\n\r\n", g_adBenchName[0] ? g_adBenchName : "-", g_adBenchRun[0] ? g_adBenchRun : "-", bm, bc);
    }
    PcCat(o, cap, "## drill-downs (issue -> evidence -> detector -> finding -> next step)\r\n");
    int listed = 0;
    for (int d = 0; d < AD_DRILLS; d++) {
        AdIssue& s = g_adIss[d];
        if (!s.turnOns && !s.deferred && !(d == AD_CRASH && g_adPrevUnclean)) continue;
        if (s.known[0]) continue;
        if (s.active) AdFinding(d);
        listed++;
        PcCat(o, cap, "- %s\r\n  evidence: %.400s\r\n  detector: %s (%s%s)\r\n  finding: %s\r\n  next step: %s\r\n",
              d == AD_CRASH ? "last session ended without a clean exit" : s.why, s.evidence[0] ? s.evidence : "(counts in adaptdiag.csv)", kAdDrill[d].detector,
              s.active ? "running" : s.turnOns ? "ran" : d == AD_CRASH ? "on this session" : "next session", s.deferred && g_adDeferred ? ", deferred: benchmark / A/B session" : "",
              s.finding[0] ? s.finding : "-", kAdDrill[d].step);
    }
    if (!listed) PcCat(o, cap, "none: no symptom asked for a detector\r\n");
    PcCat(o, cap, "\r\n## known issues\r\n");
    int kn = 0;
    for (int d = 0; d < AD_DRILLS; d++) if (g_adIss[d].known[0]) kn++, PcCat(o, cap, "- %s: %s\r\n  evidence: %.300s\r\n", g_adIss[d].why, g_adIss[d].known, g_adIss[d].evidence);
    for (int i = 0; i < g_adSigN && i < 128; i++)
        if (g_adSigHits[i]) { char lab[200]; AdSigLabel(g_adSig[i], lab, sizeof(lab)); kn++, PcCat(o, cap, "- log line seen %ld x: %s\r\n", g_adSigHits[i], lab); }
    if (!kn) PcCat(o, cap, "none matched (library: %d signatures)\r\n", g_adSigN);
    PcCat(o, cap, "\r\n## a/b validity\r\n%s\r\n", g_adAbNote[0] ? g_adAbNote : "not an A/B session");
    HANDLE h = CreateFileW(g_adRep, GENERIC_WRITE, FILE_SHARE_READ, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (h != INVALID_HANDLE_VALUE) {
        DWORD w;
        WriteFile(h, rep, (DWORD)strlen(rep), &w, NULL);
        CloseHandle(h);
    }
    LeaveCriticalSection(&g_adRepCs);
}

// A/B validity: the last session of another arm (sessions csv) must have the same mod set and config
static void AdAbCheck() {
    if (!g_adArm[0]) return;
    static char b[65536];
    if (AdReadLines(g_adSess, b, sizeof(b)) <= 0) { _snprintf_s(g_adAbNote, _TRUNCATE, "arm %s: no earlier arm session to compare", g_adArm); return; }
    char other[200] = "";
    char* tk = NULL;
    for (char* l = strtok_s(b, "\r\n", &tk); l; l = strtok_s(NULL, "\r\n", &tk)) {  // session,start,arm,bench,benchname,modset,config,clean
        char* f[8] = {};
        int nf = 0;
        for (char* p = l; nf < 8; ) { f[nf++] = p; char* c = strchr(p, ','); if (!c) break; *c = 0, p = c + 1; }
        if (nf >= 7 && f[2][0] && _stricmp(f[2], g_adArm) && strcmp(f[0], "session")) _snprintf_s(other, _TRUNCATE, "%s,%s,%s,%s", f[0], f[2], f[5], f[6]);
    }
    if (!other[0]) { _snprintf_s(g_adAbNote, _TRUNCATE, "arm %s: no earlier session of another arm", g_adArm); return; }
    char sess[16], arm[20], ms[20], cf[20];
    if (sscanf_s(other, "%15[^,],%19[^,],%19[^,],%19s", sess, (unsigned)sizeof(sess), arm, (unsigned)sizeof(arm), ms, (unsigned)sizeof(ms), cf, (unsigned)sizeof(cf)) != 4) return;
    char mine[20], minec[20];
    SFmt(mine, "%016llx", g_adModSet), SFmt(minec, "%016llx", g_adConfig);
    const bool same = !_stricmp(ms, mine) && !_stricmp(cf, minec);
    _snprintf_s(g_adAbNote, _TRUNCATE, "arm %s vs arm %s (session %s): %s", g_adArm, arm, sess,
                same ? "same mod set and mod config: valid" : "INVALID: the mod set or a mod config changed between the arms");
    Log("adaptdiag: a/b %s", g_adAbNote);
}

static DWORD WINAPI AdMain(LPVOID) {
    AdHashMods();
    char hx[64];
    SFmt(hx, "%016llx", g_adModSet);
    wchar_t w[64];
    MultiByteToWideChar(CP_ACP, 0, hx, -1, w, 64), WritePrivateProfileStringW(L"Live", L"ModSet", w, g_adState);
    SFmt(hx, "%016llx", g_adConfig);
    MultiByteToWideChar(CP_ACP, 0, hx, -1, w, 64), WritePrivateProfileStringW(L"Live", L"Config", w, g_adState);
    Log("adaptdiag: mod set %016llx (%ld packages in Mods\\Packages), mod config %016llx (%ld config files in Mods and Bin)", g_adModSet, g_adModFiles, g_adConfig, g_adConfFiles);
    AdAbCheck();
    if (GetFileAttributesW(g_adCsv) == INVALID_FILE_ATTRIBUTES)
        AdAppend(g_adCsv, "minute,activity,noise,bench,arm,simfps,iter250,iter1000,longestms,freemb,commitmb,scripterr,othercpu,perfpct,tempc,av,drill\r\n");
    AdTempInit();
    LONG secs[AD_ACTS] = {};
    for (int t = 1;; t++) {
        Sleep(1000);
        secs[AdActNow()]++;
        if (t % 60) continue;
        g_adMin++;
        AdMinute(secs);
        memset(secs, 0, sizeof(secs));
        FILETIME c, e, k, u;
        if (GetThreadTimes(GetCurrentThread(), &c, &e, &k, &u)) g_adOwnCpu100 = PcFt(k) + PcFt(u);
        if (g_adMin % 5 == 0) AdReport("every 5 minutes");
    }
}

// op 63 (ts3fixes AdaptDiag.cs; hi bits 0-7 = sub, 8-15 = arg): sub 0 = activity lo (0xFF = probe), 1 = bit <arg> of the
// install check's missing count (0 = OK or not checked yet), 2 = world quit, 3 = swapinvokediag wanted this session
// ([AdaptDiag] SwapInvokeDiag, set by the overlay). The answer is one bit (GetOption2 is a bool).
static u8 AdOp(DWORD lo, DWORD hi) {
    if (!g_adOn) return 0;
    switch (hi & 0xFF) {
        case 0: if (lo < AD_ACTS && lo != AD_SAVE) g_adAct = (LONG)lo; return 1;
        case 1: { const LONG m = g_adInstallMissing > 255 ? 255 : g_adInstallMissing; return m > 0 ? (u8)((m >> ((hi >> 8) & 7)) & 1) : 0; }
        case 2: g_adAct = AD_LOAD; AdReport("at world quit"); return 1;
        case 3: return g_adSwapDiag ? 1 : 0;
        case 4: return (u8)((g_adInduceErr >> ((hi >> 8) & 7)) & 1);  // kit test: how many script errors ts3fixes raises
    }
    return 0;
}

static bool ApplyAdaptDiag(const wchar_t* ini) {
    if (g_adStartNote[0]) Log("%s", g_adStartNote);
    if (!GetPrivateProfileIntW(L"AdaptDiag", L"Enabled", 0, ini)) return false;
    if (!g_adState[0]) return false;  // AdOverlay found no folder
    if (!g_udOn) { g_logTap = NULL; Log("SKIP adaptdiag ([AdaptDiag]): needs [UserDiag] Enabled=1 (the ts3fixes channel)"); return false; }
    g_adMaxActive = Clamp(GetPrivateProfileIntW(L"AdaptDiag", L"MaxActive", 3, ini), 1, 3);
    g_adTtlMin = Clamp(GetPrivateProfileIntW(L"AdaptDiag", L"TtlMin", 15, ini), 1, 240);
    g_adBudgetUs = Clamp(GetPrivateProfileIntW(L"AdaptDiag", L"BudgetUsPerS", 5000, ini), 0, 100000);
    g_adLineCapKB = Clamp(GetPrivateProfileIntW(L"AdaptDiag", L"LineCapKB", 4096, ini), 64, 1 << 20);
    g_adLogCapMB = Clamp(GetPrivateProfileIntW(L"AdaptDiag", L"LogCapMB", 200, ini), 8, 4000);
    g_adSwapDiag = GetPrivateProfileIntW(L"AdaptDiag", L"SwapInvokeDiag", 0, ini) ? 1 : 0;
    g_adInduceMs = Clamp(GetPrivateProfileIntW(L"AdaptDiag", L"InduceHitchMs", 0, ini), 0, 5000);
    g_adInduceAfterS = Clamp(GetPrivateProfileIntW(L"AdaptDiag", L"InduceAfterS", 120, ini), 0, 3600);
    g_adInduceErr = Clamp(GetPrivateProfileIntW(L"AdaptDiag", L"InduceScriptErrors", 0, ini), 0, 7);
    if (g_adInduceMs || g_adInduceErr) Log("adaptdiag: KIT TEST: InduceHitchMs=%ld InduceAfterS=%ld InduceScriptErrors=%ld (induced symptoms, never in a player kit)", g_adInduceMs, g_adInduceAfterS, g_adInduceErr);
    g_adBench = GetPrivateProfileIntW(L"Session", L"Benchmark", 0, g_adState) ? 1 : 0;
    wchar_t t[48];
    GetPrivateProfileStringW(L"Session", L"Arm", L"", t, 20, g_adState), WideCharToMultiByte(CP_ACP, 0, t, -1, g_adArm, sizeof(g_adArm), NULL, NULL);
    GetPrivateProfileStringW(L"Session", L"BenchmarkName", L"", t, 44, g_adState), WideCharToMultiByte(CP_ACP, 0, t, -1, g_adBenchName, sizeof(g_adBenchName), NULL, NULL);
    GetPrivateProfileStringW(L"Session", L"BenchmarkRun", L"", t, 44, g_adState), WideCharToMultiByte(CP_ACP, 0, t, -1, g_adBenchRun, sizeof(g_adBenchRun), NULL, NULL);
    for (char* p = g_adArm; *p; p++) if (*p == ',') *p = '_';
    g_adDeferred = g_adBench || g_adArm[0];
    InitializeCriticalSection(&g_adRepCs);
    AdPath(g_adCsv, L"adaptdiag.csv"), AdPath(g_adRep, L"adaptdiag-report.txt"), AdPath(g_adSess, L"adaptdiag-sessions.csv");
    // the last session's row (its Clean is known only now), then this session's [Live]
    wchar_t ls[16] = L"", la[20] = L"", lb[8] = L"", lm[20] = L"", lc[20] = L"";
    GetPrivateProfileStringW(L"Live", L"Session", L"", ls, 16, g_adState);
    if (ls[0]) {
        GetPrivateProfileStringW(L"Live", L"Arm", L"", la, 20, g_adState), GetPrivateProfileStringW(L"Live", L"Benchmark", L"0", lb, 8, g_adState);
        GetPrivateProfileStringW(L"Live", L"ModSet", L"", lm, 20, g_adState), GetPrivateProfileStringW(L"Live", L"Config", L"", lc, 20, g_adState);
        char row[200];
        if (GetFileAttributesW(g_adSess) == INVALID_FILE_ATTRIBUTES) AdAppend(g_adSess, "session,start,arm,bench,benchname,modset,config,clean\r\n");
        _snprintf_s(row, _TRUNCATE, "%ls,%ls,%ls,%ls,-,%ls,%ls,%d\r\n", ls, ls, la, lb, lm, lc, g_adPrevUnclean ? 0 : 1);
        AdAppend(g_adSess, row);
    }
    SYSTEMTIME st;
    GetLocalTime(&st);
    GetSystemTimeAsFileTime(&g_adStartFt);
    g_adSession = (DWORD)st.wMonth * 1000000 + st.wDay * 10000 + st.wHour * 100 + st.wMinute;
    PathFmt(t, L"%08lu", g_adSession);
    WritePrivateProfileStringW(L"Live", L"Session", t, g_adState);
    WritePrivateProfileStringW(L"Live", L"Clean", L"0", g_adState);
    MultiByteToWideChar(CP_ACP, 0, g_adArm, -1, t, 20), WritePrivateProfileStringW(L"Live", L"Arm", t, g_adState);
    WritePrivateProfileStringW(L"Live", L"Benchmark", g_adBench ? L"1" : L"0", g_adState);
    UserSims3Dir(g_adUser);
    if (GetFileAttributesW(g_adUser) == INVALID_FILE_ATTRIBUTES) g_adUser[0] = 0;
    const bool iter = SimLoopAdd(AdIter, "adaptdiag: iteration times");
    HANDLE th = CreateThread(NULL, 64 * 1024, AdMain, NULL, STACK_SIZE_PARAM_IS_A_RESERVATION, NULL);
    if (th) SetThreadPriority(th, THREAD_PRIORITY_BELOW_NORMAL), CloseHandle(th);
    g_adOn = th ? 1 : 0;
    Log("%s adaptdiag ([AdaptDiag] Enabled=1, docs/316): level 0 per minute (adaptdiag.csv), rules MaxActive=%d TtlMin=%d BudgetUsPerS=%ld LineCapKB=%ld LogCapMB=%ld, "
        "%d known-issue signatures, %d expected install parts, edition %s, arm %s, benchmark %ld%s%s",
        g_adOn ? "OK  " : "FAIL", g_adMaxActive, g_adTtlMin, g_adBudgetUs, g_adLineCapKB, g_adLogCapMB, g_adSigN, g_adExpN, g_adEdition, g_adArm[0] ? g_adArm : "-", g_adBench,
        g_adDeferred ? " (deferred: runtime turn-ons go to the next session)" : "", iter ? "" : "; no Simulate iteration hook: no iteration times");
    if (g_adPrevUnclean) { g_adIss[AD_CRASH].deferred = true; SCpy(g_adIss[AD_CRASH].why, "last session ended without a clean exit"); 
        if (AdLastFault(g_adIss[AD_CRASH].evidence, sizeof(g_adIss[AD_CRASH].evidence)) && AdKnown("crash", g_adIss[AD_CRASH].evidence, g_adIss[AD_CRASH].known, sizeof(g_adIss[AD_CRASH].known)))
            Log("adaptdiag: last crash matches a known issue: %s (%s)", g_adIss[AD_CRASH].known, g_adIss[AD_CRASH].evidence);
    }
    if (g_adSwapDiag) Log("adaptdiag: swapinvokediag asked for this session (ts3fixes op 63 sub 3)");
    return g_adOn != 0;
}

static void AdExit() {
    if (!g_adOn) return;
    AdReport("at exit");
    WritePrivateProfileStringW(L"Live", L"Clean", L"1", g_adState);
}
