// [ProbeSnap] (docs/94 §7, 0.23.0, counters v43), observe only: runtime targets of the indirect call/jmp sites in the routing
// closure (Route_PlanLocked, Route_PlanCommon, the route ICs, the writer leaves, portal and lock-key ICs; site list generated
// by tools/snap-vcall-sites.py into vcallsites.h). Static analysis cannot see these targets (vtables of EP/mod objects,
// per-search callbacks, function pointers); this sampler records them, on every thread. Included by ts3native.cpp.
//
// Mechanism: int3 (0xCC) on the first byte of each site; a vectored handler (first in the list) decodes the site's own
// instruction from the table (FF /2 call or FF /4 jmp, r/m32 near forms), computes the target from the CONTEXT, records
// (site, target) with a count, and emulates the instruction (push the return address for a call, Eip = target), so the
// original byte is never needed to run the code. Sampling bounds the cost: a site disarms itself (original byte back) after
// PerWindow hits; every WindowSec all sites are re-armed and the pairs whose count changed are logged
// ("vcall SITE -> TARGET n=N [out=module]"). Nothing is armed until DelaySec after start-up, so S3SS and the other
// start-up patchers see the original bytes. Before each arming the site's bytes are compared with the table: a site another
// hook has rewritten is dropped for good (vcSkipped). A target that cannot be read (bad pointer, the game's own crash
// path) disarms the site for good and lets the original instruction run (vcFaults). Up to 8 targets per site; more are
// counted in vcOverflow and logged per site.
// Threads=1 (docs/109 §8.8): each pair also counts its hits by thread class (job = route worker inside a plan, worker =
// route worker between plans, sim, other; logged as " thr=job:N,worker:N,sim:N,other:N"), and only job hits spend the
// PerWindow budget; other hits disarm the site after VC_OTHER_MUL x PerWindow. Build with /DTS3N_VCSITES3 for the
// four-site table of probe pass 3 (vcallsites3.h), with /DTS3N_VCSITES4 for the docs/133 table of the docs/131 Mono glue sites
// that are unresolved, partial or caller-typed (vcallsites4.h, tools/snap-vcall-sites.py --only-sites).
// /DTS3N_VCSITES5: docs/133 §6, those glue sites plus the routing sites of docs/102/106/107 that are still unresolved
// or partial, and the docs/107 follow/steering rows (vcallsites5.h, --only-sites given both CSVs).
// [ProbeSnap] CallSites=1 (docs/133 §7, 0.36.64, counters v130 cp*): passive coverage in every normal build, on the
// vcallsites5.h table (no /D flag). Armed once DelaySec after start-up, never re-armed: a site restores its own byte once
// it is resolved enough (CallSitesMinHits hits with fewer than CallSitesPoly targets) or, for a site with more targets, at
// CallSitesMaxHits hits or VC_PAIRS targets. Cost lands early in a load, then ~0. Pairs are logged once a minute while
// they change ("vcall ..." lines, as above) with a "callsites min" cost line; tools/callsite-db.ts merges them per run.
// Enabled=1 (the windowed probe) wins when both are set.
struct VcSiteDef { DWORD addr; u8 len; u8 bytes[7]; };
#if defined(TS3N_VCSITES3)
#include "vcallsites3.h"
#elif defined(TS3N_VCSITES4)
#include "vcallsites4.h"
#elif defined(TS3N_VCSITES5)
#include "vcallsites5.h"
#else
#include "vcallsites.h"
#endif
#if defined(TS3N_VCSITES5)
#define kVcCovSites kVcSites
#else
#define kVcSites kVcCovSites  // the coverage table under its own name beside the probe table
#include "vcallsites5.h"
#undef kVcSites
#endif

static const int VC_PAIRS = 16;
enum { VC_JOB, VC_WORKER, VC_SIM, VC_OTHER, VC_CLS };
struct VcPair { volatile LONG tgt, n; LONG logged; volatile LONG cls[VC_CLS]; };
struct VcSite {
    DWORD addr;
    u8 len, call, mod, base, index, scale, orig, dead;  // base/index 8 = none
    LONG disp;
    const u8* bytes;
    volatile LONG hits, armed, over, other;
    LONG overLogged;
    VcPair p[VC_PAIRS];
};
static VcSite* g_vc;
static int g_vcN;
static LONG g_vcPer = 16;
static int g_vcThreads;
static const LONG VC_OTHER_MUL = 16;  // shortcut: fixed share for non-job hits, make it an ini key if a pass needs another
static DWORD g_vcImgLo, g_vcImgHi;
static SRWLOCK g_vcPoke = SRWLOCK_INIT;
static PVOID g_vcVeh;
// CallSites coverage mode (0 = the windowed probe)
static int g_cpOn;
static LONG g_cpMin = 32, g_cpPoly = 4, g_cpMax = 256, g_cpWarmMin = 10;
static volatile LONG64 g_cpTicks;  // QPC ticks spent inside the handler on table sites
static DWORD g_cpCal;              // private calibration int3 (CC C3), answered first in the handler

// FF /2 (call) or FF /4 (jmp) r/m32, no prefix; false for anything else or a length that differs from the table's
static bool VcDecode(const u8* b, u8 len, VcSite& s) {
    if (len < 2 || b[0] != 0xFF) return false;
    const u8 reg = b[1] >> 3 & 7, rm = b[1] & 7;
    if (reg != 2 && reg != 4) return false;
    s.call = reg == 2;
    s.mod = b[1] >> 6;
    s.base = rm;
    s.index = 8;
    s.scale = 0;
    s.disp = 0;
    u8 n = 2;
    if (s.mod != 3) {
        if (rm == 4) {
            if (len < 3) return false;
            const u8 sib = b[2];
            n = 3;
            s.scale = sib >> 6;
            s.index = (sib >> 3 & 7) == 4 ? 8 : (sib >> 3 & 7);
            s.base = sib & 7;
            if (s.mod == 0 && s.base == 5) {
                if (len < 7) return false;
                s.base = 8;
                s.disp = *(const LONG*)(b + 3);
                n = 7;
            }
        } else if (s.mod == 0 && rm == 5) {
            return false;  // [disp32]: import thunk, not in the table
        }
        if (s.mod == 1) { if (len < n + 1) return false; s.disp = (signed char)b[n]; n += 1; }
        else if (s.mod == 2) { if (len < n + 4) return false; s.disp = *(const LONG*)(b + n); n += 4; }
    }
    return n == len;
}

static DWORD VcReg(const CONTEXT& c, int r) {
    switch (r) {
        case 0: return c.Eax; case 1: return c.Ecx; case 2: return c.Edx; case 3: return c.Ebx;
        case 4: return c.Esp; case 5: return c.Ebp; case 6: return c.Esi; default: return c.Edi;
    }
}

// the address the instruction would transfer to; false when its memory operand is unreadable
static bool VcTarget(const VcSite& s, const CONTEXT& c, DWORD* t) {
    if (s.mod == 3) { *t = VcReg(c, s.base); return true; }
    const DWORD ea = (DWORD)s.disp + (s.base < 8 ? VcReg(c, s.base) : 0) + (s.index < 8 ? VcReg(c, s.index) << s.scale : 0);
    __try { *t = *(volatile const DWORD*)(uintptr_t)ea; }
    __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
    return true;
}

static VcSite* VcFind(DWORD a) {
    int lo = 0, hi = g_vcN - 1;
    while (lo <= hi) {
        const int m = (lo + hi) / 2;
        if (g_vc[m].addr == a) return &g_vc[m];
        if (g_vc[m].addr < a) lo = m + 1; else hi = m - 1;
    }
    return NULL;
}

// caller holds g_vcPoke
static void VcPokeLocked(VcSite& s, bool arm) {
    if ((s.armed != 0) == arm) return;
    const u8 v = arm ? 0xCC : s.orig;
    if (!Write((void*)(uintptr_t)s.addr, &v, 1)) return;
    s.armed = arm;
    InterlockedExchangeAdd(&ts3native_counters.vcArmed, arm ? 1 : -1);
}

static int VcClass() {
    const DWORD me = GetCurrentThreadId();
    if (ForCaller()) return VC_JOB;
    if (me == g_workerTid) return VC_WORKER;
    return RgIsSim(me) ? VC_SIM : VC_OTHER;
}

static void VcRecord(VcSite& s, DWORD t, int cls) {
    Ts3nCounters& k = ts3native_counters;
    for (int i = 0; i < VC_PAIRS; i++) {
        LONG v = s.p[i].tgt;
        if (!v) {
            v = InterlockedCompareExchange(&s.p[i].tgt, (LONG)t, 0);
            if (!v) {
                v = (LONG)t;
                InterlockedIncrement(&k.vcPairs);
                if (t < g_vcImgLo || t >= g_vcImgHi) InterlockedIncrement(&k.vcOutside);
            }
        }
        if ((DWORD)v == t) {
            if (cls >= 0) InterlockedIncrement(&s.p[i].cls[cls]);
            InterlockedIncrement(&s.p[i].n);
            return;
        }
    }
    InterlockedIncrement(&s.over);
    InterlockedIncrement(&k.vcOverflow);
}

// coverage rule: resolved enough (MinHits with fewer than Poly targets), or a many-target site at MaxHits or a full table
static bool CpSpent(VcSite& s, LONG hits) {
    int n = 0;
    while (n < VC_PAIRS && s.p[n].tgt) n++;
    if (n >= VC_PAIRS || s.over || hits >= g_cpMax) return true;
    if (n < g_cpPoly) return hits >= g_cpMin;
    if (hits == g_cpMin) InterlockedIncrement(&ts3native_counters.cpPoly);  // once per site: its MinHits-th hit
    return false;
}

static LONG CALLBACK VcVeh(EXCEPTION_POINTERS* e) {
    const DWORD code = e->ExceptionRecord->ExceptionCode;
    if (code != EXCEPTION_BREAKPOINT && code != 0x4000001F) return EXCEPTION_CONTINUE_SEARCH;  // STATUS_WX86_BREAKPOINT
    const DWORD at = (DWORD)(uintptr_t)e->ExceptionRecord->ExceptionAddress;
    if (at == g_cpCal && at) { e->ContextRecord->Eip = at + 1; return EXCEPTION_CONTINUE_EXECUTION; }
    VcSite* s = VcFind(at);
    if (!s) return EXCEPTION_CONTINUE_SEARCH;
    LARGE_INTEGER q0 = {};
    if (g_cpOn) QueryPerformanceCounter(&q0);
    Ts3nCounters& k = ts3native_counters;
    CONTEXT* c = e->ContextRecord;
    InterlockedIncrement(&k.vcHits);
    DWORD t = 0;
    if (!VcTarget(*s, *c, &t) || !t) {  // the original instruction faults by itself: put it back and let it run
        InterlockedIncrement(&k.vcFaults);
        AcquireSRWLockExclusive(&g_vcPoke);
        s->dead = 1;
        VcPokeLocked(*s, false);
        ReleaseSRWLockExclusive(&g_vcPoke);
        c->Eip = at;
        return EXCEPTION_CONTINUE_EXECUTION;
    }
    const int cls = g_vcThreads ? VcClass() : -1;
    VcRecord(*s, t, cls);
    const bool spent = g_cpOn ? CpSpent(*s, InterlockedIncrement(&s->hits))
                     : cls < 0 || cls == VC_JOB ? InterlockedIncrement(&s->hits) >= g_vcPer
                                                : InterlockedIncrement(&s->other) >= g_vcPer * VC_OTHER_MUL;
    // a hit that raced the disarm (byte already back) still lands here and is emulated: the table, not the byte, decides
    if (spent && s->armed && TryAcquireSRWLockExclusive(&g_vcPoke)) {
        const bool was = s->armed != 0;
        VcPokeLocked(*s, false);  // busy lock: the next hit tries again (never block in here)
        if (g_cpOn && was && !s->armed) InterlockedIncrement(&ts3native_counters.cpDone);
        ReleaseSRWLockExclusive(&g_vcPoke);
    }
    if (g_cpOn) {
        LARGE_INTEGER q1;
        QueryPerformanceCounter(&q1);
        InterlockedExchangeAdd64(&g_cpTicks, q1.QuadPart - q0.QuadPart);
    }
    if (s->call) {
        c->Esp -= 4;
        *(DWORD*)(uintptr_t)c->Esp = at + s->len;
    }
    c->Eip = t;
    return EXCEPTION_CONTINUE_EXECUTION;
}

// builds the run-time table (defs sorted by address); returns the sites kept
static int VcSetup(const VcSiteDef* defs, int n) {
    Ts3nCounters& k = ts3native_counters;
    g_vc = (VcSite*)calloc((size_t)n, sizeof(VcSite));
    if (!g_vc) return 0;
    g_vcN = 0;
    for (int i = 0; i < n; i++) {
        VcSite& s = g_vc[g_vcN];
        if (!VcDecode(defs[i].bytes, defs[i].len, s) || (g_vcN && g_vc[g_vcN - 1].addr >= defs[i].addr)) {
            k.vcSkipped++;
            memset(&s, 0, sizeof(s));
            continue;
        }
        s.addr = defs[i].addr;
        s.len = defs[i].len;
        s.orig = defs[i].bytes[0];
        s.bytes = defs[i].bytes;
        g_vcN++;
    }
    const u8* img = (const u8*)GetModuleHandleW(NULL);
    g_vcImgLo = (DWORD)(uintptr_t)img;
    g_vcImgHi = g_vcImgLo + ((const IMAGE_NT_HEADERS*)(img + ((const IMAGE_DOS_HEADER*)img)->e_lfanew))->OptionalHeader.SizeOfImage;
    k.vcSites = g_vcN;
    return g_vcN;
}

// one window start: hit budgets reset, every live site armed again (bytes checked first)
static void VcArmAll() {
    Ts3nCounters& k = ts3native_counters;
    AcquireSRWLockExclusive(&g_vcPoke);
    for (int i = 0; i < g_vcN; i++) {
        VcSite& s = g_vc[i];
        if (s.dead || s.armed) continue;
        if (!Matches((const void*)(uintptr_t)s.addr, s.bytes, s.len)) {  // another hook rewrote it: never touch it again
            s.dead = 1;
            k.vcSkipped++;
            Log("vcall skip 0x%08lX: bytes differ from the table", s.addr);
            continue;
        }
        s.hits = s.other = 0;
        VcPokeLocked(s, true);
    }
    ReleaseSRWLockExclusive(&g_vcPoke);
    k.vcWindows++;
}

// pairs whose count changed since the last call (cumulative counts), with the module of targets outside the exe;
// summary = the "vcall window" line (always in the probe; coverage logs it only when a pair changed)
static int VcLogPairs(bool summary = true) {
    Ts3nCounters& k = ts3native_counters;
    int lines = 0;
    for (int i = 0; i < g_vcN; i++) {
        VcSite& s = g_vc[i];
        for (int j = 0; j < VC_PAIRS && s.p[j].tgt; j++) {
            const LONG n = s.p[j].n;
            if (n == s.p[j].logged) continue;
            s.p[j].logged = n;
            const DWORD t = (DWORD)s.p[j].tgt;
            char mod[MAX_PATH] = "";
            if (t < g_vcImgLo || t >= g_vcImgHi) {
                HMODULE h = NULL;
                char path[MAX_PATH];
                const char* b = "anon";  // no module: heap code (Mono, Frida, a hook's trampoline)
                if (GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                                       (LPCWSTR)(uintptr_t)t, &h) && GetModuleFileNameA(h, path, MAX_PATH)) {
                    b = strrchr(path, '\\') ? strrchr(path, '\\') + 1 : path;
                }
                sprintf_s(mod, " out=%s", b);
            }
            char thr[80] = "";
            if (g_vcThreads) sprintf_s(thr, " thr=job:%ld,worker:%ld,sim:%ld,other:%ld", s.p[j].cls[VC_JOB],
                                       s.p[j].cls[VC_WORKER], s.p[j].cls[VC_SIM], s.p[j].cls[VC_OTHER]);
            Log("vcall 0x%08lX -> 0x%08lX n=%ld%s%s%s", s.addr, t, n, s.call ? "" : " jmp", mod, thr);
            lines++;
        }
        if (s.over != s.overLogged) {
            s.overLogged = s.over;
            Log("vcall 0x%08lX overflow n=%ld (more than %d targets)", s.addr, s.overLogged, VC_PAIRS);
            lines++;
        }
    }
    if (summary || lines)
        Log("vcall window %ld: hits %ld, pairs %ld, outside %ld, overflow %ld, faults %ld, skipped %ld, armed %ld", k.vcWindows,
            k.vcHits, k.vcPairs, k.vcOutside, k.vcOverflow, k.vcFaults, k.vcSkipped, k.vcArmed);
    return lines;
}

static DWORD g_vcDelayMs = 90000, g_vcWindowMs = 30000;
static DWORD WINAPI VcMain(void*) {
    Sleep(g_vcDelayMs);
    if (!(g_vcVeh = AddVectoredExceptionHandler(1, VcVeh))) { Log("FAIL probe snap: AddVectoredExceptionHandler"); return 0; }
    ts3native_counters.vcReady = 1;
    Log("probe snap: %d sites armed, %ld hits per site per %lu s window", g_vcN, g_vcPer, g_vcWindowMs / 1000);
    for (;;) {
        VcArmAll();
        Sleep(g_vcWindowMs);
        VcLogPairs();
    }
}

// ns per int3 round trip through the vectored handler list (ours first), from a private CC C3 page; 0 when it cannot run
static LONG CpCalibrate() {
    u8* pg = (u8*)VirtualAlloc(NULL, 16, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    if (!pg) return 0;
    pg[0] = 0xCC;
    pg[1] = 0xC3;
    DWORD old;
    VirtualProtect(pg, 16, PAGE_EXECUTE_READ, &old);
    FlushInstructionCache(GetCurrentProcess(), pg, 16);
    g_cpCal = (DWORD)(uintptr_t)pg;  // the page is kept: a late trap on it still finds its answer
    void (*f)() = (void (*)())pg;
    LARGE_INTEGER q0, q1, fq;
    QueryPerformanceFrequency(&fq);
    QueryPerformanceCounter(&q0);
    for (int i = 0; i < 256; i++) f();
    QueryPerformanceCounter(&q1);
    return (LONG)((q1.QuadPart - q0.QuadPart) * 1000000000 / fq.QuadPart / 256);
}

// one coverage minute: trap cost of the hits and handler ticks since the last call into the cp* counters; returns its us
static LONG CpMinute(LONG dh, LONG64 dTicks, LONG64 freq) {
    Ts3nCounters& k = ts3native_counters;
    const LONG64 hUs = dTicks * 1000000 / freq;
    const LONG us = (LONG)(hUs + (LONG64)dh * k.cpTrapNs / 1000);
    k.cpHandlerUs += (LONG)hUs;
    k.cpTrapUs += us;
    k.cpLastMinUs = us;
    if (us > k.cpMaxMinUs) k.cpMaxMinUs = us;
    if (++k.cpMinutes > g_cpWarmMin) { k.cpLateUs += us; k.cpLateMin++; }
    return us;
}

// coverage: arm once, then a cost line and the changed pairs once a minute
static DWORD WINAPI CpMain(void*) {
    Ts3nCounters& k = ts3native_counters;
    Sleep(g_vcDelayMs);
    if (!(g_vcVeh = AddVectoredExceptionHandler(1, VcVeh))) { Log("FAIL call sites: AddVectoredExceptionHandler"); return 0; }
    k.cpTrapNs = CpCalibrate();
    VcArmAll();
    k.vcReady = 1;
    k.cpOn = 2;
    Log("call sites: %ld of %d sites armed, %ld ns per trap; rule %ld hits under %ld targets, else to %ld hits or %d targets",
        k.vcArmed, g_vcN, k.cpTrapNs, g_cpMin, g_cpPoly, g_cpMax, VC_PAIRS);
    LARGE_INTEGER fq;
    QueryPerformanceFrequency(&fq);
    LONG lastHits = 0;
    LONG64 lastTicks = 0;
    for (;;) {
        Sleep(60000);
        const LONG hits = k.vcHits, dh = hits - lastHits;
        const LONG64 ticks = g_cpTicks;
        const LONG us = CpMinute(dh, ticks - lastTicks, fq.QuadPart);
        lastHits = hits;
        lastTicks = ticks;
        if (!dh) continue;  // nothing fired: no lines
        VcLogPairs(false);
        Log("callsites min %ld: hits %ld, trap %ld.%03ld ms (%ld.%02ld%% of the minute), armed %ld, done %ld, poly %ld, total %ld ms",
            k.cpMinutes, dh, us / 1000, us % 1000, us / 600000, us / 6000 % 100, k.vcArmed, k.cpDone, k.cpPoly, k.cpTrapUs / 1000);
    }
}

// [ProbeSnap] CallSites=1: the coverage table built now, armed DelaySec later on its own thread
static bool ApplyCallSites(const wchar_t* ini) {
    if (!GetPrivateProfileIntW(L"ProbeSnap", L"CallSites", 0, ini)) return false;
    g_cpMin = Clamp(GetPrivateProfileIntW(L"ProbeSnap", L"CallSitesMinHits", 32, ini), 1, 100000);
    g_cpPoly = Clamp(GetPrivateProfileIntW(L"ProbeSnap", L"CallSitesPoly", 4, ini), 2, VC_PAIRS);
    g_cpMax = Clamp(GetPrivateProfileIntW(L"ProbeSnap", L"CallSitesMaxHits", 256, ini), g_cpMin, 100000);
    g_cpWarmMin = Clamp(GetPrivateProfileIntW(L"ProbeSnap", L"CallSitesWarmMin", 10, ini), 0, 600);
    g_vcDelayMs = (DWORD)Clamp(GetPrivateProfileIntW(L"ProbeSnap", L"DelaySec", 90, ini), 0, 3600) * 1000;
    g_vcThreads = GetPrivateProfileIntW(L"ProbeSnap", L"Threads", 0, ini) != 0;
    if (!VcSetup(kVcCovSites, (int)(sizeof(kVcCovSites) / sizeof(kVcCovSites[0])))) { Log("FAIL call sites: no site table"); return false; }
    g_cpOn = 1;
    ts3native_counters.cpOn = 1;
    HANDLE th = CreateThread(NULL, 64 * 1024, CpMain, NULL, STACK_SIZE_PARAM_IS_A_RESERVATION, NULL);
    if (!th) { Log("FAIL call sites: thread start (err %lu)", GetLastError()); g_cpOn = 0; ts3native_counters.cpOn = 0; return false; }
    CloseHandle(th);
    Log("OK   call sites: %d indirect call/jmp sites (vcallsites5.h, %ld rejected), armed in %lu s, self-disarming", g_vcN,
        ts3native_counters.vcSkipped, g_vcDelayMs / 1000);
    return true;
}

// [ProbeSnap] Enabled=1: table built now, armed DelaySec later on its own thread; else CallSites=1 (coverage)
static bool ApplyProbeSnap(const wchar_t* ini) {
#ifdef TS3A_TABLE
    // vcallsites*.h are raw Steam 1.67 sites (2-7 byte checks only): never armed on another build (docs/273)
    if (GetPrivateProfileIntW(L"ProbeSnap", L"Enabled", 0, ini) || GetPrivateProfileIntW(L"ProbeSnap", L"CallSites", 0, ini)) {
        Log("SKIP probe snap: its call-site tables hold Steam 1.67 addresses, not " TS3A_HOST_NAME " ones");
        return false;
    }
#endif
    if (!GetPrivateProfileIntW(L"ProbeSnap", L"Enabled", 0, ini)) {
        if (ApplyCallSites(ini)) return true;
        Log("probe snap off (Enabled=0, CallSites=0)");
        return false;
    }
    if (GetPrivateProfileIntW(L"ProbeSnap", L"CallSites", 0, ini)) Log("call sites: CallSites=1 ignored, Enabled=1 runs the probe");
    g_vcPer = Clamp(GetPrivateProfileIntW(L"ProbeSnap", L"PerWindow", 16, ini), 1, 100000);
    g_vcWindowMs = (DWORD)Clamp(GetPrivateProfileIntW(L"ProbeSnap", L"WindowSec", 30, ini), 5, 3600) * 1000;
    g_vcDelayMs = (DWORD)Clamp(GetPrivateProfileIntW(L"ProbeSnap", L"DelaySec", 90, ini), 0, 3600) * 1000;
    g_vcThreads = GetPrivateProfileIntW(L"ProbeSnap", L"Threads", 0, ini) != 0;
    if (!VcSetup(kVcSites, (int)(sizeof(kVcSites) / sizeof(kVcSites[0])))) { Log("FAIL probe snap: no site table"); return false; }
    HANDLE th = CreateThread(NULL, 64 * 1024, VcMain, NULL, STACK_SIZE_PARAM_IS_A_RESERVATION, NULL);
    if (!th) { Log("FAIL probe snap: thread start (err %lu)", GetLastError()); return false; }
    CloseHandle(th);
    Log("OK   probe snap: %d indirect call/jmp sites of the routing closure (%ld rejected), armed in %lu s%s", g_vcN,
        ts3native_counters.vcSkipped, g_vcDelayMs / 1000, g_vcThreads ? ", hits by thread class" : "");
    return true;
}
