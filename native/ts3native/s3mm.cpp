// [Compat] S3mmStandDown (docs/291 §6): detects Sims3MonoModder (S3MM, sims3fiend, GPL-3.0; nothing of it is copied) and keeps
// [MonoJit], [MonoFast] CallPath and [AutShadow] off. Why [inferred, docs/51 §528]: compiled callers keep the old body after
// S3MM overwrites a RuntimeMethod in place (4.4), our callvirt handler faults where S3MM recovers EA's (4.3), and the AutShadow
// worker runs hooked game methods on a second interpreter thread (4.5). Included by ts3native.cpp after apex.cpp, before the
// modules that ask (mintcall, monojit, autshadow).
// Detection: the ASI loader loads plugins in file-name order, so Sims3MonoModder.asi ("S" < "t") is loaded at our DllMain; the
// file in Bin, scripts\ or plugins\ also counts (a load order we did not expect). A renamed file is not found (shortcut: name
// only, no byte probe of its detours; add the 0xE525C0 / 0xE59FD0 jump check if a renamed S3MM shows up in a report).

static const wchar_t* g_s3mmModule = L"Sims3MonoModder.asi";  // selftest swaps it
static bool g_s3mmStandDown = true;                            // [Compat] S3mmStandDown
static int g_s3mmFound;                                        // bits: 1 module loaded, 2 file in Bin / scripts / plugins
static int g_s3mmStood;                                        // modules that stood down

// bits as g_s3mmFound; `bin` = the exe's folder with a trailing backslash (NULL: GetModuleFileName(NULL))
static int S3mmProbe(const wchar_t* bin = NULL) {
    int f = GetModuleHandleW(g_s3mmModule) ? 1 : 0;
    wchar_t dir[MAX_PATH], p[MAX_PATH];
    if (bin) WCpy(dir, bin);
    else {
        if (!GetModuleFileNameW(NULL, dir, MAX_PATH)) return f;
        if (wchar_t* s = wcsrchr(dir, L'\\')) s[1] = 0;
    }
    static const wchar_t* const kSub[3] = {L"", L"scripts\\", L"plugins\\"};
    for (const wchar_t* sub : kSub) {
        PathFmt(p, L"%s%s%s", dir, sub, g_s3mmModule);
        if (GetFileAttributesW(p) != INVALID_FILE_ATTRIBUTES) f |= 2;
    }
    return f;
}

// DllMain, after ApexDetect, before every install
static void S3mmDetect(const wchar_t* ini) {
    g_s3mmStandDown = GetPrivateProfileIntW(L"Compat", L"S3mmStandDown", 1, ini) != 0;
    g_s3mmFound = S3mmProbe();
    if (!g_s3mmFound) return;
    Log("compat: S3MM detected, %s (%s%s%s; [Compat] S3mmStandDown=%d, docs/291)",
        g_s3mmStandDown ? "standing down [MonoJit], [MonoFast] CallPath, [AutShadow], [Verify] 0xE59FD0" : "not standing down",
        (g_s3mmFound & 1) ? "module loaded" : "", g_s3mmFound == 3 ? ", " : "", (g_s3mmFound & 2) ? "file found" : "", g_s3mmStandDown ? 1 : 0);
}

// True (and one log line) when `what` must not install: S3MM found and [Compat] S3mmStandDown=1.
static bool S3mmStandsDown(const char* what) {
    if (!g_s3mmFound || !g_s3mmStandDown) return false;
    g_s3mmStood++;
    Log("compat: stood down for S3MM: %s not installed (docs/291 §6)", what);
    return true;
}
