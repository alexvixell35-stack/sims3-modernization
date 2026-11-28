// [Compat] ApexStandDown (docs/148 §7, §7a): detects Apex Radiance (loinyx, MIT) and keeps our modules off the game sites
// Apex owns while the matching Apex feature is on, so Apex's own byte checks see the game's bytes. Included by ts3native.cpp
// before the modules that ask (savewrite, savecompress, footprint, lotstream, idcache, verify, crashreport).
// Detection [verified Apex 2.15.0 source, docs/148 §2]: Apex's DllMain takes the mutex Local\ApexRadiance.<pid>; the ASI
// loader loads in file-name order, so ApexRadiance.asi is already loaded at our DllMain. A renamed zzApexRadiance.asi loads
// after us: ApexLateThread finds it and gives back the one site that can be given back safely (SaveWrite 0xAB12AF).
// Config [verified apex_paths.cpp, apex_presets.cpp]: <Documents>\Electronic Arts\The Sims 3\Apex Radiance\ApexRadiance.toml,
// one [patches.<Name>] table per switch with `enabled = true|false`; a missing table or key = the switch's default.

enum { AXF_OBJECT_INDEX = 1, AXF_FAST_CACHE = 2, AXF_MEMORY_GUARD = 4, AXF_RES_LOOKUP = 8, AXF_FILE_LIST = 16, AXF_LOT = 32, AXF_FAST_MEMORY = 64 };
struct ApexSwitch { const char* name; int bit; bool def; };
static const ApexSwitch kApexSwitch[] = {
    {"ObjectLookupIndex", AXF_OBJECT_INDEX, true}, {"FastCacheCompression", AXF_FAST_CACHE, true},
    {"MemoryGuard", AXF_MEMORY_GUARD, true},       {"ResourceLookupCache", AXF_RES_LOOKUP, true},
    {"FileListCache", AXF_FILE_LIST, true},
    // the EA allocator (spin count, big-block release worker, trim): Apex owns it (docs/148 §6 #9, §7); enabledByDefault = true,
    // Steam only [verified apex-src-3.0.0 performance_patches.cpp FastMemoryPatch]
    {"FastMemory", AXF_FAST_MEMORY, true},
    // the lot streaming group: enabledByDefault = false in performance_patches.cpp [verified]
    {"LotLodStreaming", AXF_LOT, false},   {"LotObjectThrottle", AXF_LOT, false}, {"LotVisibilityOverride", AXF_LOT, false},
    {"LotActiveThreshold", AXF_LOT, false}, {"LotDetailRange", AXF_LOT, false}};

static const wchar_t* g_apexModules[2] = {L"ApexRadiance.asi", L"zzApexRadiance.asi"};  // selftest swaps them
static wchar_t g_apexToml[MAX_PATH];  // "" = not found: every switch at its default
static bool g_apexStandDown = true;   // [Compat] ApexStandDown
static int g_apexFound;               // bits: 1 ApexRadiance.asi, 2 zzApexRadiance.asi, 4 instance mutex
static int g_apexOn;                  // AXF_* bits of the Apex switches that are on
static volatile LONG g_apexStood;     // modules (and Verify probes) that stood down
// counters v202 ap* (docs/148 §7 "counters", lane L9 2026-11-15): what the log lines say, readable from /native
static void ApexCounters(bool late) {
    Ts3nCounters& k = ts3native_counters;
    k.apFound = g_apexFound, k.apOn = g_apexOn, k.apStood = g_apexStood, k.apStandDown = g_apexStandDown ? 1 : 0;
    if (late) k.apLate = 1;
}

// <Documents>\Electronic Arts\The Sims 3 (the folder SHGetFolderPath(CSIDL_PERSONAL) returns; OneDrive moves it)
static void UserSims3Dir(wchar_t* out) {
    wchar_t docs[MAX_PATH];
    DWORD sz = sizeof(docs);
    if (RegGetValueW(HKEY_CURRENT_USER, L"Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\User Shell Folders", L"Personal",
                     RRF_RT_REG_SZ | RRF_RT_REG_EXPAND_SZ, NULL, docs, &sz) != ERROR_SUCCESS) {
        // docs/323 L9 refuted [measured, TestOverflowSmall]: these flags succeed on Windows 11 and return the expanded folder
        wchar_t prof[MAX_PATH];
        const DWORD n = GetEnvironmentVariableW(L"USERPROFILE", prof, MAX_PATH);
        if (!n || n >= MAX_PATH) prof[0] = 0;
        if (!prof[0]) docs[0] = 0;  // no profile: "" below, never a drive-root path
        else PathFmt(docs, L"%s\\Documents", prof);  // docs/323: was wcscat_s, a kill past MAX_PATH
    }
    PathFmt(out, MAX_PATH, L"%s\\Electronic Arts\\The Sims 3", docs);  // "" when too long
}
// g_apexToml = <user>\Apex Radiance\ApexRadiance.toml; "" when missing or too long (docs/323 L4: was wcscat_s, a kill)
static void ApexTomlPath() {
    wchar_t user[MAX_PATH];
    UserSims3Dir(user);
    if (!user[0] || !PathFmt(g_apexToml, L"%s\\Apex Radiance\\ApexRadiance.toml", user) ||
        GetFileAttributesW(g_apexToml) == INVALID_FILE_ATTRIBUTES)
        g_apexToml[0] = 0;
}

static int ApexProbe() {
    int f = 0;
    for (int i = 0; i < 2; i++)
        if (GetModuleHandleW(g_apexModules[i])) f |= 1 << i;
    wchar_t m[64];
    PathFmt(m, L"Local\\ApexRadiance.%lu", GetCurrentProcessId());
    if (HANDLE h = OpenMutexW(SYNCHRONIZE, FALSE, m)) { CloseHandle(h); f |= 4; }
    return f;
}

// AXF_* bits of the switches that are on in the toml text (NUL-terminated); NULL text = every default
static int ApexSwitchesOn(const char* t) {
    bool on[sizeof(kApexSwitch) / sizeof(kApexSwitch[0])];
    const int n = (int)(sizeof(on) / sizeof(on[0]));
    for (int i = 0; i < n; i++) on[i] = kApexSwitch[i].def;
    int cur = -1;  // switch whose [patches.X] table the line is in
    for (const char* p = t; p && *p;) {
        const char* e = p;
        while (*e && *e != '\n') e++;
        const char* s = p;
        while (s < e && (*s == ' ' || *s == '\t')) s++;
        if (*s == '[') {
            cur = -1;
            for (int i = 0; i < n; i++) {
                char h[64];
                const int hl = SFmt(h, "[patches.%s]", kApexSwitch[i].name);
                if (e - s >= hl && !strncmp(s, h, hl)) cur = i;
            }
        } else if (cur >= 0 && !strncmp(s, "enabled", 7)) {
            const char* v = s + 7;
            while (v < e && (*v == ' ' || *v == '\t' || *v == '=')) v++;
            if (e - v >= 4 && !strncmp(v, "true", 4)) on[cur] = true;
            else if (e - v >= 5 && !strncmp(v, "false", 5)) on[cur] = false;
        }
        p = *e ? e + 1 : e;
    }
    int bits = 0;
    for (int i = 0; i < n; i++)
        if (on[i]) bits |= kApexSwitch[i].bit;
#ifdef TS3A_TABLE
    bits &= ~AXF_FAST_MEMORY;  // FastMemory is a Steam-only Apex patch (supportedVersions = VERSION_STEAM)
#endif
    return bits;
}

static int ApexReadToml() {
    static char t[262144];  // their file is a few KB (presets ~150 lines)
    DWORD r = 0;
    HANDLE h = g_apexToml[0] ? CreateFileW(g_apexToml, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL, OPEN_EXISTING, 0, NULL)
                             : INVALID_HANDLE_VALUE;
    if (h == INVALID_HANDLE_VALUE) return ApexSwitchesOn(NULL);
    ReadFile(h, t, sizeof(t) - 1, &r, NULL);
    CloseHandle(h);
    t[r] = 0;
    return ApexSwitchesOn(t);
}

static void ApexLogFound(const char* when) {
    char names[256] = "";
    for (const ApexSwitch& s : kApexSwitch)
        if ((g_apexOn & s.bit) && strlen(names) + strlen(s.name) + 2 < sizeof(names)) SCat(names, " "), SCat(names, s.name);
    Log("compat: Apex Radiance found %s (%s%s%s), %s, owned switches on:%s; ApexStandDown=%d", when,
        (g_apexFound & 1) ? "ApexRadiance.asi " : "", (g_apexFound & 2) ? "zzApexRadiance.asi " : "",
        (g_apexFound & 4) ? "mutex" : "", g_apexToml[0] ? "ApexRadiance.toml read" : "no ApexRadiance.toml: defaults", names[0] ? names : " none",
        g_apexStandDown ? 1 : 0);
}

// DllMain, before every install
static void ApexDetect(const wchar_t* ini) {
    g_apexStandDown = GetPrivateProfileIntW(L"Compat", L"ApexStandDown", 1, ini) != 0;
    g_apexFound = ApexProbe();
    ApexCounters(false);
    if (!g_apexFound) { Log("compat: Apex Radiance not loaded at start"); return; }
    ApexTomlPath();
    g_apexOn = ApexReadToml();
    ApexCounters(false);
    ApexLogFound("at start");
}

// True (and one log line) when `what` must not install: Apex is loaded, owns the site through a switch in `bits` that is
// on, and [Compat] ApexStandDown=1.
static bool ApexStandsDown(int bits, const char* what, const char* site) {
    if (!g_apexFound || !g_apexStandDown || !(g_apexOn & bits)) return false;
    const char* feat = "?";
    for (const ApexSwitch& s : kApexSwitch)
        if (g_apexOn & bits & s.bit) { feat = s.name; break; }
    ts3native_counters.apStood = InterlockedIncrement(&g_apexStood);
    Log("compat: stood down for Apex %s: %s not installed (%s, docs/148 §6)", feat, what, site);
    return true;
}

// Apex switch bits that own a [Verify] probe site (docs/148 §6 #4-#6), 0 = none; TS3A: kVf[].at is this build's address (docs/260 §4.3)
static int ApexVerifySite(DWORD at) {
    switch (at) {
    case TS3A(0x00939100): case TS3A(0x00939170): case TS3A(0x00938D00): case TS3A(0x00C60D30): return AXF_OBJECT_INDEX;
    case TS3A(0x004AFFC0): return AXF_RES_LOOKUP;
    case TS3A(0x004B2D00): return AXF_RES_LOOKUP | AXF_FILE_LIST;
    case TS3A(0x004B1AE0): return AXF_FILE_LIST;
    case TS3A(0x00ABFAC0): case TS3A(0x00AC1130): case TS3A(0x00AC20E0): case TS3A(0x00C6C290): case TS3A(0x007D2DB0): return AXF_LOT;
    default: return 0;
    }
}

static bool SaveWriteYieldToApex(DWORD site = TS3A(0x00AB12AF));  // savewrite.cpp

// Late check (zzApexRadiance.asi: its DllMain runs after ours). Apex installs its game code after the first Present + 1 s
// [verified apex_main.cpp], so a site given back before then is seen as the game's. Only the [SaveWrite] timing call at
// 0xAB12AF is given back; the other modules' detours cannot be taken out safely and only log.
static DWORD WINAPI ApexLateThread(LPVOID) {
    for (int i = 0; i < 300; i++) {  // 100 ms steps, 30 s
        Sleep(100);
        if (!(g_apexFound = ApexProbe())) continue;
        ApexTomlPath();
        g_apexOn = ApexReadToml();
        ApexCounters(true);
        ApexLogFound("after our start");
        if ((g_apexOn & AXF_MEMORY_GUARD) && SaveWriteYieldToApex()) {
            InterlockedIncrement(&g_apexStood);
            ts3native_counters.apStood = g_apexStood;
            Log("compat: stood down for Apex MemoryGuard: [SaveWrite] call 0xAB12AF given back (late), write phase not timed");
        }
        const struct { bool on; int bit; const char* what; } late[] = {
            {ts3native_counters.idcReady != 0, AXF_OBJECT_INDEX, "[IdCache]"},
            {ts3native_counters.compressReady != 0, AXF_FAST_CACHE, "[SaveCompress]"},
            {ts3native_counters.lsReady != 0, AXF_LOT, "[LotStream]"},
            {(ts3native_counters.vfReady | ts3native_counters.vfReady2 | ts3native_counters.vfReady3) != 0,
             AXF_OBJECT_INDEX | AXF_RES_LOOKUP | AXF_FILE_LIST | AXF_LOT, "[Verify]"},
            {ts3native_counters.allocReady != 0, AXF_FAST_MEMORY, "[Alloc]"}};
        for (const auto& l : late)
            if (l.on && (g_apexOn & l.bit)) Log("compat: Apex loaded after ts3native: %s stays installed; Apex's switch on the same site stays off", l.what);
        return 0;
    }
    return 0;
}

// DllMain, after every install: the late check runs only when Apex was not there at start
static void ApexStartLate() {
    if (g_apexFound || !g_apexStandDown) return;
    if (HANDLE t = CreateThread(NULL, 64 * 1024, ApexLateThread, NULL, STACK_SIZE_PARAM_IS_A_RESERVATION, NULL)) CloseHandle(t);
}
