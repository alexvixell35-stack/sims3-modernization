// [PcCheck] (docs/299; Enabled=1 Notify=0 by default: the log part is read-only, user-mode APIs only, no admin, no driver; the in-game notice is opt-in). Included by
// ts3native.cpp after userdiag.cpp. Shared by the normal build and [Diag] ModTime (modtime.cpp adds this block to its summary).
//
// "Is this PC able to give the game (and our worker threads) the CPU it needs?" Once at start: CPU name, logical / physical
// cores (SMT), RAM, power plan (+ the Windows 11 power-mode overlay), AC or battery, TS3W's affinity, priority class and
// efficiency mode (process power throttling), Game Mode setting. Every 10 s on its own thread: per-core MHz from
// CallNtPowerInformation(ProcessorInformation) (CurrentMhz vs MaxMhz, MhzLimit), system CPU (GetSystemTimes) minus TS3W's
// own (GetProcessTimes) = other programs, the top 3 other processes (Toolhelp + GetProcessTimes), TS3W's threads busy >= 50%
// of a core and who started them (ts3native / TS3W / other module), the sim thread's CPU vs wall when modtime.cpp names it.
// Per window (one sim hour when ts3fixes sends op 62, else 60 real minutes) and per session: one `pccheck:` log line, the
// file pccheck-summary.txt next to ts3native.log and, from modtime.cpp, a block of modtime-summary.txt + CSV columns.
// Verdict (PcVerdict, pure, selftest fixtures): clock limited (> 20% of busy samples with MhzLimit < MaxMhz or the fastest
// core < 90% of MaxMhz), power saver plan / best-efficiency overlay, battery, affinity < all cores, efficiency mode, low
// priority class (these six are "problems": ts3fixes shows one notice per session when Notify=1), plus advice lines that are
// not problems: other programs >= 25% CPU, few cores for the busy threads (our workers compete with the game).
// Nothing here changes the game, a thread or a setting. Windows 11 reports CurrentMhz = the base clock and MhzLimit 0
// [verified: smoke 2026-11-20, i7-11800H: 2304 / 0 in all 100 samples], so the clock comes from PDH when it has the counters:
// \Processor Information(_Total)\% Processor Performance (effective clock in % of base, > 100 = boost) x Processor Frequency
// (base MHz) and Performance Limit Flags (non-zero = a firmware / OS limit is active). A busy sample is "slow" when its
// performance is below 85% of the session's own highest busy sample (PcPerfSlow); without PDH the CallNtPowerInformation
// rule stays (docs/51 I-544-5).
#include <tlhelp32.h>

enum { PC_THROTTLE = 1, PC_POWERSAVER = 2, PC_BATTERY = 4, PC_AFFINITY = 8, PC_EFFICIENCY = 16, PC_PRIORITY = 32,  // problems (notice)
       PC_OTHERLOAD = 64, PC_FEWCORES = 128 };                                                                       // advice only
static const int kPcProblems = PC_THROTTLE | PC_POWERSAVER | PC_BATTERY | PC_AFFINITY | PC_EFFICIENCY | PC_PRIORITY;

// verdict input: one window or the session
struct PcFacts {
    LONG logical, physical, affinityCpus;
    LONG busySamples;       // 10 s samples with TS3W busy (>= half a core)
    LONG slowSamples;       // of those: fastest core < 90% of MaxMhz
    LONG limitSamples;      // of those: some core's MhzLimit < its MaxMhz
    LONG maxMhz, minLimitMhz, meanCurMhz;
    double otherCpuPct;     // other programs, % of all logical cores
    double busyThreads;     // TS3W threads >= 50% of a core, mean per sample
    LONG ourThreads, gameThreads;  // threads started in ts3native / in TS3W.exe
    bool powerSaver, onBattery, efficiency, lowPriority;
};

static void PcCat(char* out, size_t cap, const char* fmt, ...) {
    const size_t n = strlen(out);
    if (n + 2 >= cap) return;
    va_list ap;
    va_start(ap, fmt);
    _vsnprintf_s(out + n, cap - n, _TRUNCATE, fmt, ap);
    va_end(ap);
}

// flags (PC_*); out = verdict lines ("\n" between them)
static int PcVerdict(const PcFacts& f, char* out, size_t cap) {
    int fl = 0;
    out[0] = 0;
    const LONG bs = f.busySamples > 0 ? f.busySamples : 0;
    const LONG lim = f.limitSamples > f.slowSamples ? f.limitSamples : f.slowSamples;
    const LONG pct = bs ? lim * 100 / bs : 0;
    if (bs >= 3 && pct > 20) {
        fl |= PC_THROTTLE;
        PcCat(out, cap, "CPU: clock limited %ld%% of the time (limit %ld MHz of %ld): cooling or power limit; clean the vents, plug in, use a balanced or high performance plan\n",
              pct, f.minLimitMhz ? f.minLimitMhz : f.meanCurMhz, f.maxMhz);
    } else if (bs >= 3) PcCat(out, cap, "CPU: no throttle seen (%ld busy samples, %ld%% limited)\n", bs, pct);
    else PcCat(out, cap, "CPU: too few busy samples to judge the clock (%ld)\n", bs);
    if (f.powerSaver) fl |= PC_POWERSAVER, PcCat(out, cap, "power plan: power saver / best power efficiency: set Balanced or Best performance in Windows power settings\n");
    if (f.onBattery) fl |= PC_BATTERY, PcCat(out, cap, "power: on battery: plug in the charger, laptops slow the CPU on battery\n");
    if (f.affinityCpus > 0 && f.affinityCpus < f.logical)
        fl |= PC_AFFINITY, PcCat(out, cap, "affinity limits TS3W to %ld of %ld cores: remove the affinity setting (Task Manager, a launcher or a tweak tool)\n", f.affinityCpus, f.logical);
    if (f.efficiency) fl |= PC_EFFICIENCY, PcCat(out, cap, "efficiency mode is on for TS3W: turn it off in Task Manager (right-click the game)\n");
    if (f.lowPriority) fl |= PC_PRIORITY, PcCat(out, cap, "TS3W runs at below-normal or idle priority: set it back to normal\n");
    if (f.otherCpuPct >= 25) fl |= PC_OTHERLOAD, PcCat(out, cap, "other programs used %.0f%% CPU: close what you do not need while playing\n", f.otherCpuPct);
    // headroom: the game's busy threads + other programs' share vs the cores TS3W may use
    const double cores = (double)(f.affinityCpus > 0 && f.affinityCpus < f.logical ? f.affinityCpus : f.logical);
    const double need = f.busyThreads + f.otherCpuPct / 100 * f.logical;
    if (cores > 0 && (cores <= 4 || need + 1 > cores)) {
        fl |= PC_FEWCORES;
        PcCat(out, cap, "%.0f threads for %.1f busy game threads + %.1f cores of other programs: our worker threads (%ld) compete with the game; expect smaller gains\n",
              cores, f.busyThreads, f.otherCpuPct / 100 * f.logical, f.ourThreads);
    }
    if (!(fl & (kPcProblems | PC_OTHERLOAD))) PcCat(out, cap, "PC: no problem found\n");
    return fl;
}

// one fix hint for the notice (the most important problem); NULL = none
static const char* PcHint(int fl) {
    if (fl & PC_BATTERY) return "The game runs on battery: plug in the charger for full CPU speed.";
    if (fl & PC_POWERSAVER) return "Windows is in power-saver mode: choose Balanced or Best performance in the power settings.";
    if (fl & PC_EFFICIENCY) return "Efficiency mode is on for the game: turn it off in Task Manager.";
    if (fl & PC_AFFINITY) return "The game may only use some CPU cores (affinity): remove that setting.";
    if (fl & PC_PRIORITY) return "The game runs at low priority: set it back to normal.";
    if (fl & PC_THROTTLE) return "Your CPU slows down while playing (heat or power limit): check cooling and the power plan.";
    return NULL;
}

// busy samples whose performance % is below frac x maxPct (the session's highest); hist = counts per whole %
static LONG PcPerfSlow(const LONG* hist, int bins, int maxPct, double frac) {
    LONG n = 0;
    const double lim = maxPct * frac;
    for (int i = 0; i < bins && i < lim; i++) n += hist[i];
    return n;
}
// the highest bin with a count (0 = none)
static int PcPerfMax(const LONG* hist, int bins) {
    for (int i = bins - 1; i > 0; i--) if (hist[i]) return i;
    return 0;
}
static int PcPerfMin(const LONG* hist, int bins) {
    for (int i = 0; i < bins; i++) if (hist[i]) return i;
    return 0;
}

// ---- live part ----
static LONG g_pcOn, g_pcNotify = 0;
static int g_pcFlags;  // PC_* problems of the session so far (op 62)
struct PcAcc;
static PcFacts PcFactsOf(const PcAcc& a);
static volatile DWORD g_pcSimTid;  // modtime.cpp names the sim thread (0 = no sim-thread column)
static wchar_t g_pcSum[MAX_PATH];
static CRITICAL_SECTION g_pcCs;
static char g_pcStatic[1024];      // the start facts, one text block
static PcFacts g_pcBase;           // static part of the facts
static LONG g_pcSysMax;            // highest MaxMhz of any core

struct PcProc { char name[40]; double ms; };
enum { kPcPerfBins = 400 };  // % Processor Performance 0..399 (higher values land in the last bin)
struct PcAcc {
    LONG samples, busy, slow, limited, minLimit, curN;
    double curSum, otherPct, ownPct, simPct, busyThr, wallS;
    LONG perfN, perfFlagged, perfHist[kPcPerfBins];  // busy samples with a PDH reading; of those: Performance Limit Flags != 0
    double perfSum;
    PcProc top[64];
    int topN;
};
static PcAcc g_pcWin, g_pcAll;
static LONG g_pcWinNo;

static void PcProcAdd(PcAcc& a, const char* name, double ms) {
    int i = 0;
    while (i < a.topN && _stricmp(a.top[i].name, name)) i++;
    if (i == a.topN) {
        if (a.topN == 64) {  // full: replace the smallest when this one is bigger
            int lo = 0;
            for (int j = 1; j < 64; j++) if (a.top[j].ms < a.top[lo].ms) lo = j;
            if (a.top[lo].ms >= ms) return;
            i = lo;
            a.top[i].ms = 0;
        } else a.topN++, a.top[i].ms = 0;
        strncpy_s(a.top[i].name, name, _TRUNCATE);
    }
    a.top[i].ms += ms;
}
// "name 12.3%, ..." of the 3 biggest; pct of all cores over wallS
static void PcTop3(const PcAcc& a, LONG logical, char* out, size_t cap) {
    out[0] = 0;
    bool used[64] = {};
    for (int k = 0; k < 3; k++) {
        int b = -1;
        for (int i = 0; i < a.topN; i++) if (!used[i] && (b < 0 || a.top[i].ms > a.top[b].ms)) b = i;
        if (b < 0 || a.top[b].ms <= 0 || a.wallS <= 0) break;
        used[b] = true;
        PcCat(out, cap, "%s%s %.1f%%", k ? ", " : "", a.top[b].name, a.top[b].ms / 10 / (a.wallS * (logical > 0 ? logical : 1)));
    }
    if (!out[0]) strcpy_s(out, cap, "-");
}

static LONGLONG PcFt(const FILETIME& f) { return (LONGLONG)((ULONGLONG)f.dwHighDateTime << 32 | f.dwLowDateTime); }

typedef LONG(WINAPI* PcCallNtPower)(int, PVOID, ULONG, PVOID, ULONG);
typedef DWORD(WINAPI* PcGetScheme)(HKEY, GUID**);
typedef DWORD(WINAPI* PcReadName)(HKEY, const GUID*, const GUID*, const GUID*, UCHAR*, DWORD*);
typedef DWORD(WINAPI* PcOverlay)(GUID*);
typedef LONG(WINAPI* PcNtQit)(HANDLE, int, PVOID, ULONG, PULONG);
typedef BOOL(WINAPI* PcGetProcInfo)(HANDLE, int, LPVOID, DWORD);
static PcCallNtPower g_pcPower;
struct PcCore { ULONG number, maxMhz, curMhz, limitMhz, maxIdle, curIdle; };  // PROCESSOR_POWER_INFORMATION

// PDH (pdh.dll, loaded at run time): % Processor Performance, Processor Frequency, Performance Limit Flags of _Total
struct PcPdhVal { DWORD status, pad; double v; };  // PDH_FMT_COUNTERVALUE with PDH_FMT_DOUBLE (x86 layout)
typedef LONG(WINAPI* PcPdhOpen)(LPCWSTR, DWORD_PTR, HANDLE*);
typedef LONG(WINAPI* PcPdhAdd)(HANDLE, LPCWSTR, DWORD_PTR, HANDLE*);
typedef LONG(WINAPI* PcPdhCollect)(HANDLE);
typedef LONG(WINAPI* PcPdhGet)(HANDLE, DWORD, LPDWORD, PcPdhVal*);
static PcPdhCollect g_pcPdhCollect;
static PcPdhGet g_pcPdhGet;
static HANDLE g_pcPdhQ, g_pcPdhPerf, g_pcPdhFreq, g_pcPdhFlags;
static bool g_pcPdhOk;  // the performance counter answered at least once
static void PcPdhInit() {
    HMODULE m = LoadLibraryW(L"pdh.dll");
    if (!m) return;
    PcPdhOpen op = (PcPdhOpen)(void*)GetProcAddress(m, "PdhOpenQueryW");
    PcPdhAdd add = (PcPdhAdd)(void*)GetProcAddress(m, "PdhAddEnglishCounterW");
    g_pcPdhCollect = (PcPdhCollect)(void*)GetProcAddress(m, "PdhCollectQueryData");
    g_pcPdhGet = (PcPdhGet)(void*)GetProcAddress(m, "PdhGetFormattedCounterValue");
    if (!op || !add || !g_pcPdhCollect || !g_pcPdhGet || op(NULL, 0, &g_pcPdhQ) != 0) { g_pcPdhQ = NULL; return; }
    if (add(g_pcPdhQ, L"\\Processor Information(_Total)\\% Processor Performance", 0, &g_pcPdhPerf) != 0) g_pcPdhPerf = NULL;
    if (add(g_pcPdhQ, L"\\Processor Information(_Total)\\Processor Frequency", 0, &g_pcPdhFreq) != 0) g_pcPdhFreq = NULL;
    if (add(g_pcPdhQ, L"\\Processor Information(_Total)\\Performance Limit Flags", 0, &g_pcPdhFlags) != 0) g_pcPdhFlags = NULL;
    g_pcPdhCollect(g_pcPdhQ);  // rate counters need a first collect
}
// false = no reading this time
static bool PcPdhRead(double* perf, double* baseMhz, double* flags) {
    if (!g_pcPdhQ || !g_pcPdhPerf || g_pcPdhCollect(g_pcPdhQ) != 0) return false;
    PcPdhVal v = {};
    const DWORD fmt = 0x200 | 0x8000;  // PDH_FMT_DOUBLE | PDH_FMT_NOCAP100 (performance goes above 100 under boost)
    if (g_pcPdhGet(g_pcPdhPerf, fmt, NULL, &v) != 0 || v.status != 0 || v.v <= 0) return false;
    *perf = v.v;
    *baseMhz = g_pcPdhFreq && g_pcPdhGet(g_pcPdhFreq, fmt, NULL, &v) == 0 && v.status == 0 ? v.v : 0;
    *flags = g_pcPdhFlags && g_pcPdhGet(g_pcPdhFlags, fmt, NULL, &v) == 0 && v.status == 0 ? v.v : 0;
    g_pcPdhOk = true;
    return true;
}
static LONG g_pcBaseMhz;  // Processor Frequency (base clock) from PDH, 0 = unknown

static bool PcGuidIs(const GUID& g, DWORD d1, WORD d2, WORD d3) { return g.Data1 == d1 && g.Data2 == d2 && g.Data3 == d3; }

static void PcStaticFacts() {
    PcFacts& f = g_pcBase;
    char* s = g_pcStatic;
    const size_t cap = sizeof(g_pcStatic);
    s[0] = 0;
    char cpu[96] = "?";
    DWORD sz = sizeof(cpu);
    RegGetValueA(HKEY_LOCAL_MACHINE, "HARDWARE\\DESCRIPTION\\System\\CentralProcessor\\0", "ProcessorNameString", RRF_RT_REG_SZ, NULL, cpu, &sz);
    char* c = cpu;
    while (*c == ' ') c++;
    SYSTEM_INFO si;
    GetNativeSystemInfo(&si);
    f.logical = (LONG)si.dwNumberOfProcessors;
    DWORD len = 0;
    GetLogicalProcessorInformation(NULL, &len);
    SYSTEM_LOGICAL_PROCESSOR_INFORMATION* lpi = (SYSTEM_LOGICAL_PROCESSOR_INFORMATION*)malloc(len ? len : 1);
    if (lpi && GetLogicalProcessorInformation(lpi, &len))
        for (DWORD i = 0; i < len / sizeof(*lpi); i++) if (lpi[i].Relationship == RelationProcessorCore) f.physical++;
    free(lpi);
    MEMORYSTATUSEX ms = {sizeof(ms)};
    GlobalMemoryStatusEx(&ms);
    PcCat(s, cap, "CPU %s: %ld logical / %ld physical cores (SMT %s); RAM %.1f GB, %.1f GB free", c, f.logical, f.physical,
          f.physical && f.logical > f.physical ? "on" : "off", ms.ullTotalPhys / 1073741824.0, ms.ullAvailPhys / 1073741824.0);
    // power plan (+ overlay)
    HMODULE pp = LoadLibraryW(L"powrprof.dll");
    g_pcPower = pp ? (PcCallNtPower)(void*)GetProcAddress(pp, "CallNtPowerInformation") : NULL;
    PcGetScheme gs = pp ? (PcGetScheme)(void*)GetProcAddress(pp, "PowerGetActiveScheme") : NULL;
    PcReadName rn = pp ? (PcReadName)(void*)GetProcAddress(pp, "PowerReadFriendlyName") : NULL;
    PcOverlay ov = pp ? (PcOverlay)(void*)GetProcAddress(pp, "PowerGetEffectiveOverlayScheme") : NULL;
    GUID* scheme = NULL;
    char plan[80] = "?";
    DWORD planErr = 0;
    if (gs && (planErr = gs(NULL, &scheme)) == 0 && scheme) {
        // sized read: the stored name can be longer than the text (258 bytes for "High Performance" here [verified 2026-11-20])
        DWORD wsz = 0;
        planErr = rn ? rn(NULL, scheme, NULL, NULL, NULL, &wsz) : ERROR_PROC_NOT_FOUND;
        wchar_t* wn = planErr == 0 && wsz ? (wchar_t*)calloc(wsz + 2, 1) : NULL;
        if (wn) planErr = rn(NULL, scheme, NULL, NULL, (UCHAR*)wn, &wsz);
        if (wn && planErr == 0 && wn[0]) WideCharToMultiByte(CP_UTF8, 0, wn, -1, plan, sizeof(plan), NULL, NULL);
        free(wn);
        if (plan[0] == '?') {  // the stock schemes by GUID; else the error code
            const GUID& g = *scheme;
            const char* known = PcGuidIs(g, 0x381b4222, 0xf694, 0x41f0) ? "Balanced" : PcGuidIs(g, 0x8c5e7fda, 0xe8bf, 0x4a96) ? "High performance"
                              : PcGuidIs(g, 0xa1841308, 0x3541, 0x4fab) ? "Power saver" : PcGuidIs(g, 0xe9a42b02, 0xd5df, 0x448d) ? "Ultimate Performance" : NULL;
            if (known) strcpy_s(plan, known);
            else _snprintf_s(plan, _TRUNCATE, "? (scheme %08lX, error %lu)", g.Data1, planErr);
        }
        if (PcGuidIs(*scheme, 0xa1841308, 0x3541, 0x4fab)) f.powerSaver = true;  // the Power saver scheme
        LocalFree(scheme);
    } else _snprintf_s(plan, _TRUNCATE, "? (error %lu)", planErr);
    GUID o = {};
    const char* overlay = "none";
    if (ov && ov(&o) == 0) {
        if (PcGuidIs(o, 0x961cc777, 0x2547, 0x4f9d)) overlay = "best power efficiency", f.powerSaver = true;
        else if (PcGuidIs(o, 0xded574b5, 0x45a0, 0x4f42)) overlay = "best performance";
        else if (o.Data1) overlay = "balanced/other";
    }
    SYSTEM_POWER_STATUS ps = {};
    GetSystemPowerStatus(&ps);
    f.onBattery = ps.ACLineStatus == 0;
    PcCat(s, cap, "\npower plan \"%s\", power mode overlay %s, %s%s", plan, overlay, ps.ACLineStatus == 0 ? "ON BATTERY" : ps.ACLineStatus == 1 ? "on AC" : "AC unknown",
          ps.BatteryFlag != 128 && ps.BatteryFlag != 255 ? " (has a battery)" : "");
    // this process: affinity, priority, efficiency mode
    DWORD_PTR pm = 0, sm = 0;
    GetProcessAffinityMask(GetCurrentProcess(), &pm, &sm);
    for (DWORD_PTR b = pm; b; b &= b - 1) f.affinityCpus++;
    const DWORD pri = GetPriorityClass(GetCurrentProcess());
    f.lowPriority = pri == IDLE_PRIORITY_CLASS || pri == BELOW_NORMAL_PRIORITY_CLASS;
    struct { ULONG ver, control, state; } pt = {1, 0, 0};  // PROCESS_POWER_THROTTLING_STATE
    PcGetProcInfo gpi = (PcGetProcInfo)(void*)GetProcAddress(GetModuleHandleW(L"kernel32.dll"), "GetProcessInformation");
    const bool ptOk = gpi && gpi(GetCurrentProcess(), 4 /* ProcessPowerThrottling */, &pt, sizeof(pt));
    f.efficiency = ptOk && (pt.control & 1) && (pt.state & 1);  // EXECUTION_SPEED throttled = efficiency mode / EcoQoS
    DWORD gm = 1, gsz = sizeof(gm);
    const bool gmSet = RegGetValueW(HKEY_CURRENT_USER, L"Software\\Microsoft\\GameBar", L"AutoGameModeEnabled", RRF_RT_REG_DWORD, NULL, &gm, &gsz) == ERROR_SUCCESS;
    PcCat(s, cap, "\nTS3W: affinity %ld of %ld cores, priority %s, efficiency mode %s, Windows Game Mode %s", f.affinityCpus, f.logical,
          pri == IDLE_PRIORITY_CLASS ? "IDLE" : pri == BELOW_NORMAL_PRIORITY_CLASS ? "BELOW NORMAL" : pri == NORMAL_PRIORITY_CLASS ? "normal" : "above normal/high",
          !ptOk ? "unknown" : f.efficiency ? "ON" : "off", gmSet ? (gm ? "on" : "off") : "on (default)");
}

// threads of this process: owner by start address, CPU per window
struct PcThr { DWORD tid; HANDLE h; LONGLONG cpu; char owner; bool seen; };  // owner: 'n' ts3native, 'g' TS3W, 'o' other
static PcThr g_pcThr[512];
static int g_pcThrN;
static char PcOwner(HANDLE h) {
    static PcNtQit q = (PcNtQit)(void*)GetProcAddress(GetModuleHandleW(L"ntdll.dll"), "NtQueryInformationThread");
    void* start = NULL;
    if (!q || q(h, 9 /* ThreadQuerySetWin32StartAddress */, &start, sizeof(start), NULL) != 0 || !start) return 'o';
    HMODULE m = NULL;
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, (LPCWSTR)start, &m)) return 'o';
    if (m == GetModuleHandleW(NULL)) return 'g';
    HMODULE self = NULL;
    GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, (LPCWSTR)(void*)&PcOwner, &self);
    return m == self ? 'n' : 'o';
}
// busy threads (>= 50% of a core over dt100ns) this sample; counts of ours / game's threads into *nOurs / *nGame
static double PcThreads(LONGLONG dt100ns, LONG* nOurs, LONG* nGame) {
    for (int i = 0; i < g_pcThrN; i++) g_pcThr[i].seen = false;
    HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
    if (snap == INVALID_HANDLE_VALUE) return 0;
    THREADENTRY32 te = {sizeof(te)};
    const DWORD pid = GetCurrentProcessId();
    int busy = 0;
    *nOurs = *nGame = 0;
    for (BOOL ok = Thread32First(snap, &te); ok; ok = Thread32Next(snap, &te)) {
        if (te.th32OwnerProcessID != pid) continue;
        int i = 0;
        while (i < g_pcThrN && g_pcThr[i].tid != te.th32ThreadID) i++;
        if (i == g_pcThrN) {
            if (g_pcThrN == 512) continue;
            HANDLE h = OpenThread(THREAD_QUERY_INFORMATION, FALSE, te.th32ThreadID);
            if (!h) continue;
            g_pcThr[g_pcThrN++] = {te.th32ThreadID, h, -1, PcOwner(h), false};
        }
        PcThr& t = g_pcThr[i];
        t.seen = true;
        if (t.owner == 'n') (*nOurs)++;
        else if (t.owner == 'g') (*nGame)++;
        FILETIME c, e, k, u;
        if (!GetThreadTimes(t.h, &c, &e, &k, &u)) continue;
        const LONGLONG cpu = PcFt(k) + PcFt(u);
        if (t.cpu >= 0 && dt100ns > 0 && (cpu - t.cpu) * 2 >= dt100ns) busy++;
        t.cpu = cpu;
    }
    CloseHandle(snap);
    for (int i = 0; i < g_pcThrN;) {  // gone threads
        if (g_pcThr[i].seen) { i++; continue; }
        CloseHandle(g_pcThr[i].h);
        g_pcThr[i] = g_pcThr[--g_pcThrN];
    }
    return busy;
}

// other processes' CPU since the last call into acc (ms per name)
struct PcPid { DWORD pid; LONGLONG cpu; bool seen; };
static PcPid g_pcPid[1024];
static int g_pcPidN;
static void PcOthers(PcAcc& win, PcAcc& all) {
    for (int i = 0; i < g_pcPidN; i++) g_pcPid[i].seen = false;
    HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snap == INVALID_HANDLE_VALUE) return;
    PROCESSENTRY32W pe = {sizeof(pe)};
    const DWORD self = GetCurrentProcessId();
    for (BOOL ok = Process32FirstW(snap, &pe); ok; ok = Process32NextW(snap, &pe)) {
        if (pe.th32ProcessID == self || pe.th32ProcessID == 0) continue;
        HANDLE h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pe.th32ProcessID);
        if (!h) continue;
        FILETIME c, e, k, u;
        const BOOL got = GetProcessTimes(h, &c, &e, &k, &u);
        CloseHandle(h);
        if (!got) continue;
        const LONGLONG cpu = PcFt(k) + PcFt(u);
        int i = 0;
        while (i < g_pcPidN && g_pcPid[i].pid != pe.th32ProcessID) i++;
        if (i == g_pcPidN) {
            if (g_pcPidN == 1024) continue;
            g_pcPid[g_pcPidN++] = {pe.th32ProcessID, cpu, true};
            continue;  // first sight: no delta yet
        }
        g_pcPid[i].seen = true;
        const double ms = (double)(cpu - g_pcPid[i].cpu) / 1e4;
        g_pcPid[i].cpu = cpu;
        if (ms <= 0) continue;
        char name[40];
        WideCharToMultiByte(CP_UTF8, 0, pe.szExeFile, -1, name, sizeof(name), NULL, NULL);
        name[sizeof(name) - 1] = 0;
        PcProcAdd(win, name, ms), PcProcAdd(all, name, ms);
    }
    CloseHandle(snap);
    for (int i = 0; i < g_pcPidN;) {
        if (g_pcPid[i].seen) { i++; continue; }
        g_pcPid[i] = g_pcPid[--g_pcPidN];
    }
}

// one 10 s sample (the pccheck thread)
static void PcSample() {
    static LONGLONG lastQpc, lastIdle, lastSys, lastOwn, lastSim;
    static HANDLE simH;
    static DWORD simTid;
    LARGE_INTEGER q, fq;
    QueryPerformanceCounter(&q), QueryPerformanceFrequency(&fq);
    const LONGLONG now = q.QuadPart * 10000000 / fq.QuadPart;  // 100 ns
    FILETIME idle, kern, user, c, e, k, u;
    GetSystemTimes(&idle, &kern, &user);
    GetProcessTimes(GetCurrentProcess(), &c, &e, &k, &u);
    const LONGLONG sys = PcFt(kern) + PcFt(user), own = PcFt(k) + PcFt(u), idl = PcFt(idle);  // system kernel time includes idle
    LONGLONG sim = 0;
    if (g_pcSimTid && g_pcSimTid != simTid) { if (simH) CloseHandle(simH); simTid = g_pcSimTid; simH = OpenThread(THREAD_QUERY_INFORMATION, FALSE, simTid); }
    if (simH && GetThreadTimes(simH, &c, &e, &k, &u)) sim = PcFt(k) + PcFt(u);
    LONG nOurs = 0, nGame = 0;
    const LONGLONG dt = lastQpc ? now - lastQpc : 0;
    const double busyThr = PcThreads(dt, &nOurs, &nGame);
    // what a launcher, tool or the player can change while the game runs (the start text keeps the start values)
    SYSTEM_POWER_STATUS ps = {};
    const bool psOk = GetSystemPowerStatus(&ps) != 0;
    DWORD_PTR pm = 0, sm = 0;
    LONG aff = 0;
    if (GetProcessAffinityMask(GetCurrentProcess(), &pm, &sm)) for (DWORD_PTR b = pm; b; b &= b - 1) aff++;
    const DWORD pri = GetPriorityClass(GetCurrentProcess());
    struct { ULONG ver, control, state; } pt = {1, 0, 0};
    static PcGetProcInfo gpi = (PcGetProcInfo)(void*)GetProcAddress(GetModuleHandleW(L"kernel32.dll"), "GetProcessInformation");
    const bool ptOk = gpi && gpi(GetCurrentProcess(), 4, &pt, sizeof(pt));
    EnterCriticalSection(&g_pcCs);
    g_pcBase.ourThreads = nOurs, g_pcBase.gameThreads = nGame;
    if (psOk) g_pcBase.onBattery = ps.ACLineStatus == 0;
    if (aff) g_pcBase.affinityCpus = aff;
    if (pri) g_pcBase.lowPriority = pri == IDLE_PRIORITY_CLASS || pri == BELOW_NORMAL_PRIORITY_CLASS;
    if (ptOk) g_pcBase.efficiency = (pt.control & 1) && (pt.state & 1);
    PcOthers(g_pcWin, g_pcAll);
    if (dt > 0) {
        const LONG L = g_pcBase.logical > 0 ? g_pcBase.logical : 1;
        const double cap = (double)dt * L, busySys = (double)((sys - lastSys) - (idl - lastIdle)), ownD = (double)(own - lastOwn);
        const double otherPct = busySys > ownD ? (busySys - ownD) / cap * 100 : 0, ownPct = ownD / cap * 100, simPct = sim && lastSim ? (double)(sim - lastSim) / dt * 100 : 0;
        // per-core clocks
        PcCore cores[64];
        const ULONG nc = (ULONG)(L < 64 ? L : 64);
        bool slow = false, limited = false;
        LONG fastest = 0, minLimit = 0, sumCur = 0;
        if (g_pcPower && g_pcPower(11 /* ProcessorInformation */, NULL, 0, cores, nc * sizeof(PcCore)) == 0) {
            for (ULONG i = 0; i < nc; i++) {
                if ((LONG)cores[i].curMhz > fastest) fastest = (LONG)cores[i].curMhz;
                if ((LONG)cores[i].maxMhz > g_pcSysMax) g_pcSysMax = (LONG)cores[i].maxMhz;
                if (cores[i].limitMhz && cores[i].limitMhz < cores[i].maxMhz) { limited = true; if (!minLimit || (LONG)cores[i].limitMhz < minLimit) minLimit = (LONG)cores[i].limitMhz; }
                sumCur += (LONG)cores[i].curMhz;
            }
            slow = g_pcSysMax && fastest * 10 < g_pcSysMax * 9;
        }
        double perf = 0, baseMhz = 0, lflags = 0;
        const bool pdh = PcPdhRead(&perf, &baseMhz, &lflags);
        if (pdh && baseMhz > 0) g_pcBaseMhz = (LONG)baseMhz;
        const bool busy = ownD * 2 >= (double)dt;  // TS3W used >= half a core: clocks of an idle PC are not a verdict
        PcAcc* acc[2] = {&g_pcWin, &g_pcAll};
        for (PcAcc* a : acc) {
            a->samples++, a->wallS += dt / 1e7, a->otherPct += otherPct, a->ownPct += ownPct, a->simPct += simPct, a->busyThr += busyThr;
            if (nc && sumCur) a->curSum += (double)sumCur / nc, a->curN++;
            if (busy && pdh) {
                const int bin = perf >= kPcPerfBins - 1 ? kPcPerfBins - 1 : (int)perf;
                a->perfHist[bin]++, a->perfN++, a->perfSum += perf;
                if (lflags != 0) a->perfFlagged++;
            }
            if (busy) {
                a->busy++;
                if (slow) a->slow++;
                if (limited) { a->limited++; if (!a->minLimit || minLimit < a->minLimit) a->minLimit = minLimit; }
            }
        }
    }
    { char v[1024]; g_pcFlags = PcVerdict(PcFactsOf(g_pcAll), v, sizeof(v)) & kPcProblems; }  // op 62 answers from the latest sample
    LeaveCriticalSection(&g_pcCs);
    lastQpc = now, lastIdle = idl, lastSys = sys, lastOwn = own, lastSim = sim;
}

static PcFacts PcFactsOf(const PcAcc& a) {
    PcFacts f = g_pcBase;
    f.busySamples = a.busy, f.slowSamples = a.slow, f.limitSamples = a.limited, f.minLimitMhz = a.minLimit, f.maxMhz = g_pcSysMax;
    f.meanCurMhz = a.curN ? (LONG)(a.curSum / a.curN) : 0;
    if (a.perfN > 0) {  // PDH: effective clock = base x performance %; slow = below 85% of the session's best busy sample
        const int best = PcPerfMax(g_pcAll.perfHist, kPcPerfBins);
        const LONG base = g_pcBaseMhz ? g_pcBaseMhz : g_pcSysMax;
        f.slowSamples = PcPerfSlow(a.perfHist, kPcPerfBins, best, 0.85);
        f.limitSamples = a.perfFlagged;
        f.maxMhz = base * best / 100;
        f.meanCurMhz = (LONG)(base * a.perfSum / a.perfN / 100);
        f.minLimitMhz = f.slowSamples || f.limitSamples ? base * PcPerfMin(a.perfHist, kPcPerfBins) / 100 : 0;
        f.busySamples = a.perfN;
    }
    f.otherCpuPct = a.samples ? a.otherPct / a.samples : 0;
    f.busyThreads = a.samples ? a.busyThr / a.samples : 0;
    return f;
}

// the block for a summary: what = "this window" / "session"; returns the PC_* flags
static int PcBlock(const PcAcc& a, const char* what, char* out, size_t cap) {
    const PcFacts f = PcFactsOf(a);
    char v[1024], top[200];
    const int fl = PcVerdict(f, v, sizeof(v));
    PcTop3(a, f.logical, top, sizeof(top));
    const double n = a.samples ? a.samples : 1;
    _snprintf_s(out, cap, _TRUNCATE,
                "PC check (%s, %ld samples of 10 s, %.0f real s)\n%s\nclock (%s): max %ld MHz, mean %ld MHz, %ld busy samples: slow in %ld, "
                "limited in %ld (lowest %ld MHz)\nCPU use: TS3W %.1f%% of all cores (sim thread %.0f%% of one core), other programs %.1f%%; "
                "top other: %s\nthreads: %.1f busy TS3W threads (>= 50%% of a core), %ld started by ts3native, %ld by TS3W.exe\n%s",
                what, a.samples, a.wallS, g_pcStatic, a.perfN ? "PDH % Processor Performance x base, slow = < 85% of the session's best, limited = Performance Limit Flags"
                : "CallNtPowerInformation, slow = fastest core < 90% of max, limited = MhzLimit below max",
                f.maxMhz, f.meanCurMhz, f.busySamples, f.slowSamples, f.limitSamples, f.minLimitMhz, a.ownPct / n, a.simPct / n, f.otherCpuPct, top,
                f.busyThreads, f.ourThreads, f.gameThreads, v);
    if (f.logical && f.busyThreads + 1 > f.logical)  // proposal only: nothing adapts yet (docs/299 §4)
        PcCat(out, cap, "proposal (not active): fewer route workers on this PC would leave a core for the game\n");
    return fl;
}

static CRITICAL_SECTION g_pcWr;  // one writer at a time (sim thread op 62, the pccheck thread's periodic write)
static wchar_t g_pcCsv[MAX_PATH];
static void PcWriteSummaryLocked(bool windowEnd, LONG hour);
static void PcWriteSummary(bool windowEnd, LONG hour) {
    EnterCriticalSection(&g_pcWr);
    PcWriteSummaryLocked(windowEnd, hour);
    LeaveCriticalSection(&g_pcWr);
}
static void PcWriteSummaryLocked(bool windowEnd, LONG hour) {
    static char a[3072], b[3072];
    EnterCriticalSection(&g_pcCs);
    const PcAcc win = g_pcWin, all = g_pcAll;
    if (windowEnd) g_pcWin = {}, g_pcWinNo++;
    LeaveCriticalSection(&g_pcCs);
    const int fw = PcBlock(win, hour >= 0 ? "last sim hour" : "last window", a, sizeof(a));
    const int fa = PcBlock(all, "this session", b, sizeof(b));
    g_pcFlags = (fw | fa) & kPcProblems;
    char first[200];
    strncpy_s(first, strstr(a, "\nCPU:") ? strstr(a, "\nCPU:") + 1 : "-", _TRUNCATE);
    if (char* nl = strchr(first, '\n')) *nl = 0;
    const char* hint = PcHint(g_pcFlags);
    if (hour != -2) Log("pccheck: window %ld (sim hour %ld): %s; other programs %.0f%%, problems 0x%X%s%s", g_pcWinNo, hour, first, PcFactsOf(win).otherCpuPct, g_pcFlags, hint ? "; " : "", hint ? hint : "");
    if (windowEnd && win.samples) {  // pccheck.csv: one row per window
        HANDLE c = CreateFileW(g_pcCsv, FILE_APPEND_DATA, FILE_SHARE_READ, NULL, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
        if (c != INVALID_HANDLE_VALUE) {
            LARGE_INTEGER sz = {};
            GetFileSizeEx(c, &sz);
            const PcFacts pf = PcFactsOf(win);
            char top[200], line[640];
            PcTop3(win, pf.logical, top, sizeof(top));
            SYSTEMTIME st;
            GetLocalTime(&st);
            int n = 0;
            if (!sz.QuadPart) n = _snprintf_s(line, _TRUNCATE, "time,sim_hour,samples,busy_samples,slow_samples,limited_samples,min_limit_mhz,max_mhz,mean_mhz,ts3w_pct,sim_thread_pct,other_pct,busy_threads,problems,top_other\r\n");
            const double sn = win.samples;
            _snprintf_s(line + (n > 0 ? n : 0), sizeof(line) - (n > 0 ? n : 0), _TRUNCATE, "%02d-%02d %02d:%02d,%ld,%ld,%ld,%ld,%ld,%ld,%ld,%ld,%.1f,%.1f,%.1f,%.2f,0x%X,\"%s\"\r\n",
                        st.wMonth, st.wDay, st.wHour, st.wMinute, hour, win.samples, pf.busySamples, pf.slowSamples, pf.limitSamples, pf.minLimitMhz, pf.maxMhz, pf.meanCurMhz,
                        win.ownPct / sn, win.simPct / sn, pf.otherCpuPct, pf.busyThreads, fw & kPcProblems, top);
            DWORD cw;
            WriteFile(c, line, (DWORD)strlen(line), &cw, NULL);
            CloseHandle(c);
        }
    }
    HANDLE f = CreateFileW(g_pcSum, GENERIC_WRITE, FILE_SHARE_READ, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (f == INVALID_HANDLE_VALUE) return;
    DWORD w;
    const char* hdr = "ts3native " TS3NATIVE_VERSION " PC check ([PcCheck], docs/299; read-only)\r\n\r\n";
    WriteFile(f, hdr, (DWORD)strlen(hdr), &w, NULL);
    const char* parts[3] = {b, "\n", a};
    for (const char* t : parts)
        for (const char* p = t; *p; p++) { if (*p == '\n') WriteFile(f, "\r", 1, &w, NULL); WriteFile(f, p, 1, &w, NULL); }
    CloseHandle(f);
}

static volatile LONG g_pcReq, g_pcReqHour;  // from op 62: 1 = sim hour g_pcReqHour ended, 2 = world quit
static DWORD WINAPI PcThread(LPVOID) {
    SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_BELOW_NORMAL);
    PcPdhInit();  // pdh.dll loads here, off the game start path
    PcSample();
    DWORD lastWrite = GetTickCount();
    LONG n = 0;
    for (;;) {
        for (int i = 0; i < 10; i++) {
            Sleep(1000);
            const LONG req = InterlockedExchange(&g_pcReq, 0);
            if (req & 1) PcWriteSummary(true, g_pcReqHour), lastWrite = GetTickCount();
            else if (req & 2) PcWriteSummary(false, -1);
        }
        PcSample();
        if (GetTickCount() - lastWrite >= 3600000) { PcWriteSummary(true, -1); lastWrite = GetTickCount(); }  // no ts3fixes hour: a real hour
        else if (++n % 60 == 0) PcWriteSummary(false, -2);  // every 10 min the file is current (a game closed without a world quit)
    }
}

// userdiag.cpp case 62: hi 0 = problem flags for the notice (0 when Notify=0 or nothing found), 1 = sim hour lo ended, 2 = world quit
static u8 PcOp(DWORD lo, DWORD hi) {
    if (!g_pcOn) return 0;
    switch (hi & 0xFFFF) {
        case 0: return !lo ? 1 : g_pcNotify && (g_pcFlags & (int)lo) ? 1 : 0;  // lo 0 = on?; else one PC_* bit (the channel answers a bool)
        case 1: g_pcReqHour = (LONG)lo; InterlockedOr(&g_pcReq, 1); return 1;  // the pccheck thread writes within a second
        case 2: InterlockedOr(&g_pcReq, 2); return 1;
    }
    return 0;
}

static bool ApplyPcCheck(const wchar_t* ini) {
    if (!GetPrivateProfileIntW(L"PcCheck", L"Enabled", 1, ini)) { Log("SKIP pccheck ([PcCheck] Enabled=0)"); return false; }
    g_pcNotify = GetPrivateProfileIntW(L"PcCheck", L"Notify", 0, ini) ? 1 : 0;
    wcscpy_s(g_pcSum, ini);
    wchar_t* slash = wcsrchr(g_pcSum, L'\\');
    if (!slash) return false;
    slash[1] = 0;
    wcscpy_s(g_pcCsv, g_pcSum);
    wcscat_s(g_pcSum, L"pccheck-summary.txt");
    wcscat_s(g_pcCsv, L"pccheck.csv");
    InitializeCriticalSection(&g_pcCs);
    InitializeCriticalSection(&g_pcWr);
    PcStaticFacts();
    char v[1024];
    g_pcFlags = PcVerdict(g_pcBase, v, sizeof(v)) & kPcProblems;  // static problems are known now (battery, plan, affinity, ...)
    for (char* p = g_pcStatic; *p; p++) if (*p == '\n') *p = ';';
    Log("pccheck: %s", g_pcStatic);
    for (char* p = g_pcStatic; *p; p++) if (*p == ';') *p = '\n';
    HANDLE t = CreateThread(NULL, 64 * 1024, PcThread, NULL, STACK_SIZE_PARAM_IS_A_RESERVATION, NULL);
    if (t) CloseHandle(t);
    g_pcOn = 1;
    Log("OK   pccheck ([PcCheck] Enabled=1 Notify=%ld, docs/299): read-only; samples every 10 s, pccheck-summary.txt next to this log; problems at start 0x%X", g_pcNotify, g_pcFlags);
    return true;
}
