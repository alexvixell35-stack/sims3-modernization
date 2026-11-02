// [MemDiag] (docs/190 phase 1, docs/191 module 1): one "diag: mem" line after every [UserDiag] "memory at" line, numbers
// only, plus an opt-in self-test that proves each field moves by a known injected size. Included by ts3native.cpp right
// after userdiag.cpp (it reuses UdMemStat / UdMemWalk / UdMemSample / the memory worker; no second walk code).
//   diag: mem #4 load ev=travel,world-switch priv=1851 wPriv=1830 wCom=2167 free=1646 big=1525? img=310? map=120 stkRes=140
//     stkCom=9.1? miRes=320 miCom=210 eaDir=22.0? eaDirN=3? eaCom=612? eaRes=640? eaCoreN=46? rp=20.1 pl=96.0? sp=7.0? gcHeap=360? gcFree=151? gcN=812?
//     rc=388? rcCold=14.2? rcBudget=200? rcN=41000? anim=4.1? rest=402? t=734   (t = seconds since the game started)
// MB unless the key ends in N (count) or is gcN. "-" = not readable (no game, feature off). Suffix "?" = no validation has
// passed for this field (read from a documented address, unproven), "!" = it failed this session's self-test; no suffix =
// a self-test check passed (host selftest for walk / OS / routesnap fields, or this session's game self-test).
// eaCom/eaRes/eaCoreN: EA GeneralAllocator cores (MemoryMan heap + the script heap when it is a separate object), committed /
// reserved MB and count (docs/209 #96). rc is cache bytes, not 32-bit address space: under DXVK most texture data does not
// sit in TS3W's address space (docs/40 §4-5), so rc falling does not mean free space rising by as much.
// Validation in docs/191 "Goal: a correct memory measuring tool first".
#include <initializer_list>

static bool g_mdOn;
static volatile LONG g_mdSelfTest;  // 0 off, 1 pending (game: 30 s after the first world load), 2 done
static volatile LONG g_mdN, g_mdLegend, g_mdMarks;
static DWORD g_mdWorldTick;         // first world load seen by the worker (GetTickCount)
static const DWORD kMdSelfTestDelayMs = 30000;

enum {
    MD_PRIV, MD_WPRIV, MD_WCOM, MD_FREE, MD_BIG, MD_IMG, MD_MAP, MD_STKRES, MD_STKCOM, MD_MIRES, MD_MICOM, MD_EADIR, MD_EADIRN,
    MD_EACOM, MD_EARES, MD_EACOREN, MD_RP, MD_PL, MD_SP, MD_GCHEAP, MD_GCFREE, MD_GCN, MD_RC, MD_RCCOLD, MD_RCBUD, MD_RCN, MD_ANIM, MD_REST, MD_N
};
// check: 'H' = a check in the host selftest (selftest-all, every build) and in the game self-test, 'G' = game self-test
// only (needs the EA heap), 0 = no check: unvalidated until docs/191's later modules add one.
struct MdDef { const char* key; bool count; char check; };
static const MdDef kMd[MD_N] = {
    {"priv", 0, 'H'},   {"wPriv", 0, 'H'},  {"wCom", 0, 'H'},  {"free", 0, 'H'},    {"big", 0, 0},     {"img", 0, 0},
    {"map", 0, 'H'},    {"stkRes", 0, 'H'}, {"stkCom", 0, 0},  {"miRes", 0, 'H'},   {"miCom", 0, 'H'}, {"eaDir", 0, 'G'},
    {"eaDirN", 1, 'G'}, {"eaCom", 0, 0},    {"eaRes", 0, 0},     {"eaCoreN", 1, 0}, {"rp", 0, 'H'},     {"pl", 0, 0},      {"sp", 0, 0},        {"gcHeap", 0, 0},  {"gcFree", 0, 0},
    {"gcN", 1, 0},      {"rc", 0, 0},       {"rcCold", 0, 0},  {"rcBudget", 0, 0},  {"rcN", 1, 0},     {"anim", 0, 0},
    {"rest", 0, 0},
};
// 'G' fields count as validated in every session once mem-diag-verify.json passed on this build (set true then).
static bool g_mdGameVerified = false;  // not const: no constant-condition warning
static volatile LONG g_mdPass, g_mdFail;  // bit per field: this session's self-test result

// game addresses (the host selftest points them at fakes)
static const u8* const* g_mdEaHeapSlot = (const u8* const*)(uintptr_t)0x011CB864;  // MemoryMan heap (docs/re/structs.md §9)
static const u8* const* g_mdRcMgrSlot = (const u8* const*)(uintptr_t)0x011C78D8;   // resource manager (structs.md §10)
static const DWORD* g_mdGc[3] = {(const DWORD*)(uintptr_t)0x012225A0, (const DWORD*)(uintptr_t)0x012225B4,
                                 (const DWORD*)(uintptr_t)0x011F2F90};  // GC_heapsize, GC_large_free_bytes, GC_gc_no (docs/66 §6.1)

// GeneralAllocator instances whose cores are summed: MemoryMan [0x11CB864] and the script host's [0x11D4BC8] (docs/130
// I-130-7: a dedicated heap or the same object; the same object is counted once)
static const u8* const* g_mdEaCoreSlot[2] = {(const u8* const*)(uintptr_t)0x011CB864, (const u8* const*)(uintptr_t)0x011D4BC8};

struct MdVals { double v[MD_N]; bool have[MD_N]; };

static bool MdEaDirect(DWORD* bytes, DWORD* count) {  // heap +0x488 count / +0x48C bytes of direct-mapped blocks
    __try {
        const u8* h = *g_mdEaHeapSlot;
        if (!h) return false;
        *count = *(const volatile DWORD*)(h + 0x488);
        *bytes = *(const volatile DWORD*)(h + 0x48C);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}
// Cores (AddCore 0x4E4E30, exe bytes): a ring through core +0x18 (next) whose head node is heap +0x448 (first core =
// [heap+0x460]); core +4 committed bytes, +8 reserved bytes. Read without the heap lock like eaDir: a core added or
// trimmed mid-walk costs one sample's accuracy; a broken ring ends at the cap or the SEH guard (no field then).
static bool MdEaCores(double* com, double* res, DWORD* n) {
    __try {
        *com = *res = 0, *n = 0;
        const u8* seen = NULL;
        for (const u8* const* slot : g_mdEaCoreSlot) {
            const u8* h = *slot;
            if (!h || h == seen) continue;
            seen = h;
            const u8* c = *(const u8* const volatile*)(h + 0x460);
            for (; c && c != h + 0x448; c = *(const u8* const volatile*)(c + 0x18)) {
                if (++*n > 4096) return false;
                *com += *(const volatile DWORD*)(c + 4), *res += *(const volatile DWORD*)(c + 8);
            }
        }
        return seen != NULL;
    } __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}
static bool MdGc(DWORD* out) {
    __try {
        for (int i = 0; i < 3; i++) out[i] = *(const volatile DWORD*)g_mdGc[i];
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}
// ResourceCache (priority 0) and AnimationCache (priority 1000), both vtable 0xFFDD94, from the manager's cache array
// +0xA0/+0xA4 of {cache*, priority} (as verify.cpp VfPollOnce). Plain aligned dword reads, no lock (docs/190 §5.4).
// out: held, cold, budget, entries, anim held; anim = ~0 when not found.
static bool MdRc(DWORD* out) {
    __try {
        const u8* m = *g_mdRcMgrSlot;
        if (!m || (*(const DWORD*)m != 0xFFE250 && *(const DWORD*)m != 0xFB2DA0)) return false;
        const DWORD* b = *(const DWORD* const*)(m + 0xA0);
        const DWORD* e = *(const DWORD* const*)(m + 0xA4);
        bool rc = false;
        out[4] = ~0u;
        for (int i = 0; b + 2 * i < e && i < 16; i++) {
            const u8* c = (const u8*)(uintptr_t)b[2 * i];
            if (!c || *(const DWORD*)c != 0xFFDD94) continue;
            if (b[2 * i + 1] == 0 && !rc) {
                out[0] = *(const volatile DWORD*)(c + 0x94), out[1] = *(const volatile DWORD*)(c + 0x98);
                out[2] = *(const volatile DWORD*)(c + 0x8C), out[3] = *(const volatile DWORD*)(c + 0x28);
                rc = true;
            } else if (b[2 * i + 1] == 1000 && out[4] == ~0u) out[4] = *(const volatile DWORD*)(c + 0x94);
        }
        return rc;
    } __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}

static void MdValues(const UdMemStat& s, u64 privUsage, MdVals& x) {
    const double MB = 1.0 / (1 << 20);
    for (int i = 0; i < MD_N; i++) x.have[i] = false;
    auto set = [&](int i, double v) { x.v[i] = v, x.have[i] = true; };
    set(MD_PRIV, privUsage * MB), set(MD_WPRIV, s.privCommit * MB), set(MD_WCOM, s.commit * MB), set(MD_FREE, s.freeB * MB);
    set(MD_BIG, s.big1 * MB), set(MD_IMG, s.image * MB), set(MD_MAP, s.mapped * MB);
    set(MD_STKRES, s.stkRes * MB), set(MD_STKCOM, s.stkCom * MB), set(MD_MIRES, s.miRes * MB), set(MD_MICOM, s.miCom * MB);
    DWORD a = 0, b = 0, r[5] = {}, g[3] = {};
    if (MdEaDirect(&a, &b)) set(MD_EADIR, a * MB), set(MD_EADIRN, b);
    double ec = 0, er = 0;
    DWORD en = 0;
    if (MdEaCores(&ec, &er, &en)) set(MD_EACOM, ec * MB), set(MD_EARES, er * MB), set(MD_EACOREN, en);
    const Ts3nCounters& k = ts3native_counters;
    set(MD_RP, k.rpKB / 1024.0), set(MD_PL, k.plKB / 1024.0), set(MD_SP, k.spKB / 1024.0);
    if (MdGc(g)) set(MD_GCHEAP, g[0] * MB), set(MD_GCFREE, g[1] * MB), set(MD_GCN, g[2]);
    if (MdRc(r)) {
        set(MD_RC, r[0] * MB), set(MD_RCCOLD, r[1] * MB), set(MD_RCBUD, r[2] * MB), set(MD_RCN, r[3]);
        if (r[4] != ~0u) set(MD_ANIM, r[4] * MB);
    }
    // remainder of the OS private bytes after the attributed owners that were readable (an upper bound when one is "-")
    double rest = x.v[MD_PRIV];
    for (int i : {MD_GCHEAP, MD_EADIR, MD_EACOM, MD_MICOM, MD_RP, MD_PL, MD_SP, MD_STKCOM})
        if (x.have[i]) rest -= x.v[i];
    set(MD_REST, rest);
}

static char MdMarkOf(int i) {  // '' validated, '?' unvalidated, '!' failed this session
    if (g_mdFail & (1 << i)) return '!';
    if (g_mdPass & (1 << i)) return 0;
    if (kMd[i].check == 'H' || (kMd[i].check == 'G' && g_mdGameVerified)) return 0;
    return '?';
}

static void MdNum(char* out, size_t cap, double v, bool count) {
    _snprintf_s(out, cap, _TRUNCATE, count || v >= 100 || v <= -100 ? "%.0f" : "%.1f", v);
}

// "diag: mem #N <tag>[ ev=<ev>] key=value[?|!] ... t=<s since start>" (~360-500 characters; the Log buffer is 1536)
static void MdFormat(char* out, size_t cap, LONG n, const char* tag, const char* ev, const MdVals& x, DWORD secs) {
    int at = _snprintf_s(out, cap, _TRUNCATE, "diag: mem #%ld %s%s%s", n, tag, ev && *ev ? " ev=" : "", ev && *ev ? ev : "");
    for (int i = 0; i < MD_N && at > 0; i++) {
        char num[32] = "-";
        const char m = x.have[i] ? MdMarkOf(i) : 0;
        if (x.have[i]) MdNum(num, sizeof(num), x.v[i], kMd[i].count);
        const int w = _snprintf_s(out + at, cap - at, _TRUNCATE, " %s=%s%s", kMd[i].key, num, m == '?' ? "?" : m == '!' ? "!" : "");
        at = w < 0 ? -1 : at + w;  // shortcut: a truncated line (not reachable at today's field count) stops at the cut
    }
    if (at > 0) _snprintf_s(out + at, cap - at, _TRUNCATE, " t=%lu", secs);
}

static const char* MdTag(const char* what) {  // the [UserDiag] sample name -> short tag
    if (strstr(what, "world load")) return "load";
    if (strstr(what, "at start")) return "save-start";
    if (strstr(what, "at end")) return "save-end";
    if (strstr(what, "at exit")) return "exit";
    if (strstr(what, "at mark")) return "mark";
    if (strstr(what, "selftest")) return "selftest";
    if (strstr(what, " min")) return "periodic";
    return "other";
}

static void MdLegend(const char* why) {
    char ok[200] = "", un[200] = "", bad[200] = "";
    for (int i = 0; i < MD_N; i++) {
        const char m = MdMarkOf(i);
        char* d = m == '!' ? bad : m == '?' ? un : ok;
        if (*d) strcat_s(d, sizeof(ok), " ");
        strcat_s(d, sizeof(ok), kMd[i].key);
    }
    Log("diag: mem fields (%s; MB, counts for keys ending in N and gcN; '?' = unvalidated, '!' = failed this session's "
        "self-test, '-' = not readable): validated %s; unvalidated %s%s%s", why, ok[0] ? ok : "none", un[0] ? un : "none",
        bad[0] ? "; FAILED " : "", bad);
}

// ---------------------------------------------------------------------------------------------------------------------
// suspect counters (docs/191 module 2): ts3fixes MemSuspects.cs reads managed counts (UIManager custom-control dictionary,
// delegate invocation lists of docs/192 #1-#5) on the sim thread. Every diag: mem line leaves a "wanted" slot (its number,
// tag, event); ts3fixes polls it over the text channel and answers with one "memsus: k=v ..." line, logged as
//   diag: memsus #N <tag>[ ev=<ev>] k=v[?|!] ... lag=<ms> t=<s>     (N = the diag: mem sample it answers)
// Ops on the 0x87 channel (UdTextMsg): 3 = [MemDiag] on (ts3fixes starts its poll task), 4 = a sample is wanted, 5 = the
// managed self-test is wanted ([MemDiag] SelfTest=1, until a "memsus selftest PASS|FAIL" line). Validation marks are
// ts3fixes' own (its self-test adds and removes a known entry per counter); native only relays.
// Wanted samples queue up (8 slots, oldest answered first): a single slot lost the world load sample whenever the native
// self-test's samples came before ts3fixes' first poll (0.36.74 bp-memsus-catalogue: load answer missing, lag ~15 s).
static SRWLOCK g_msLock = SRWLOCK_INIT;
static volatile LONG g_msLines;
static LONG g_msHead, g_msTail;  // under g_msLock
static struct { LONG n; char tag[16], ev[40]; DWORD tick; } g_msQ[8];
static volatile LONG g_msSelfTest;  // 0 off, 1 wanted, 2 done
static void MsWant(LONG n, const char* tag, const char* ev) {
    AcquireSRWLockExclusive(&g_msLock);
    if (g_msHead - g_msTail >= 8) g_msTail++;  // full: the oldest unanswered sample is dropped (8 within one poll do not happen)
    auto& q = g_msQ[g_msHead & 7];
    strncpy_s(q.tag, tag, _TRUNCATE);
    strncpy_s(q.ev, ev ? ev : "", _TRUNCATE);
    q.tick = GetTickCount();
    q.n = n;
    g_msHead++;
    ReleaseSRWLockExclusive(&g_msLock);
}
static u8 MsOp(int op) {
    if (!g_mdOn) return 0;
    if (op == 3) return 1;
    if (op == 4) { AcquireSRWLockShared(&g_msLock); const bool w = g_msHead != g_msTail; ReleaseSRWLockShared(&g_msLock); return w; }
    if (op == 5) return g_msSelfTest == 1;
    return 0;
}
static void MsLine(const char* line) {  // sim thread, a complete "memsus..." text line from ts3fixes
    if (!g_mdOn || InterlockedIncrement(&g_msLines) > 4000) return;  // cap: a looping sender cannot fill the disk
    if (strncmp(line, "memsus: ", 8)) {  // self-test steps, legend
        if (!strncmp(line, "memsus selftest PASS", 20) || !strncmp(line, "memsus selftest FAIL", 20)) g_msSelfTest = 2;
        Log("diag: %s", line);
        return;
    }
    char tag[16] = "", ev[40] = "";
    LONG n = 0;
    DWORD tick = GetTickCount();
    AcquireSRWLockExclusive(&g_msLock);
    if (g_msHead != g_msTail) {  // the oldest wanted sample; none = an unasked line (#0)
        const auto& q = g_msQ[g_msTail++ & 7];
        n = q.n;
        tick = q.tick;
        strcpy_s(tag, q.tag);
        strcpy_s(ev, q.ev);
    }
    ReleaseSRWLockExclusive(&g_msLock);
    Log("diag: memsus #%ld %s%s%s %s lag=%lu t=%lu", n, n ? tag : "unasked", ev[0] ? " ev=" : "", ev, line + 8, GetTickCount() - tick,
        (GetTickCount() - g_udT0) / 1000);
}

static void AsAfterSample(LONG n, const char* tag, const char* ev);  // allocsites.cpp: [MemDiag] AllocSites lines (docs/201)
static void MdAfterSample(const char* what, const char* ev, const UdMemStat& s, u64 privUsage) {
    if (!g_mdOn) return;
    if (!InterlockedExchange(&g_mdLegend, 1)) MdLegend("docs/190 phase 1");
    MdVals x;
    MdValues(s, privUsage, x);
    char line[640];
    const LONG n = InterlockedIncrement(&g_mdN);
    MdFormat(line, sizeof(line), n, MdTag(what), ev, x, (GetTickCount() - g_udT0) / 1000);
    Log("%s", line);
    MsWant(n, MdTag(what), ev);  // the suspect counters for this sample (ts3fixes answers on the sim thread)
    AsAfterSample(n, MdTag(what), ev);  // no-op unless [MemDiag] AllocSites=1
}

// ---------------------------------------------------------------------------------------------------------------------
// events (sim thread)

static char g_mdWorld[64];
static bool g_mdTravel;
static void MdSaveStart(bool travelSave) { if (travelSave) g_mdTravel = true; }
// "world: <name>, lots ..." (ts3fixes UserDiag.cs) -> "travel,world-switch" / "world-switch" / "travel" / NULL
static const char* MdWorldEvent(const char* worldLine) {
    static char ev[40];
    char name[64] = "";
    if (!strncmp(worldLine, "world: ", 7)) {
        const char* s = worldLine + 7;
        size_t n = strcspn(s, ",");
        if (n >= sizeof(name)) n = sizeof(name) - 1;
        memcpy(name, s, n);
        name[n] = 0;
    }
    const bool sw = g_mdWorld[0] && name[0] && strcmp(name, g_mdWorld);
    if (name[0]) strcpy_s(g_mdWorld, name);
    sprintf_s(ev, "%s%s%s", g_mdTravel ? "travel" : "", g_mdTravel && sw ? "," : "", sw ? "world-switch" : "");
    g_mdTravel = false;
    return ev[0] ? ev : NULL;
}
// "mem: <tag>" over the ts3fixes text channel (ts3agent mem_mark): one sample tagged ev=<tag> ([A-Za-z0-9_-], 31 chars).
// Sampled inline on the sender's (sim) thread since module 2: the line is written before the agent's next step runs, so a
// mark brackets an action exactly (mem_gc's gc-before must not be walked after the collection; one walk is ~ms, test-only).
static void MdMark(const char* tag) {
    if (!g_mdOn) return;
    char t[32];
    size_t n = 0;
    for (; tag[n] && n < sizeof(t) - 1; n++) t[n] = ((tag[n] | 0x20) >= 'a' && (tag[n] | 0x20) <= 'z') || (tag[n] >= '0' && tag[n] <= '9') || tag[n] == '-' || tag[n] == '_' ? tag[n] : '_';
    t[n] = 0;
    char what[48];
    sprintf_s(what, "diag: memory at mark #%ld", InterlockedIncrement(&g_mdMarks));
    UdMemSample(what, n ? t : "unnamed");
}

// ---------------------------------------------------------------------------------------------------------------------
// self-test: inject known sizes, check each field moves by them (both on the way up and back down where the step undoes)

static u64 MdTake(MdVals& x, const void* view = NULL, size_t len = 0) {  // returns the walk's MEM_MAPPED bytes in the view
    UdMemStat s;
    UdMemWalk(s, NULL, view, len);
    MdValues(s, UdPrivateUsage(), x);
    return s.ownMapped;
}

typedef void* (*MdActFn)(void* st, bool on);  // on: allocate, returns state; off: free it
struct MdWant { int field; double mb, tol; };

static DWORD WINAPI MdIdleThread(LPVOID) { return 0; }
static void* MdActCommit(void* p, bool on) {
    if (on) return VirtualAlloc(NULL, 48u << 20, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
    VirtualFree(p, 0, MEM_RELEASE);
    return NULL;
}
static void* MdActArena(void* p, bool on) {  // like a mimalloc arena: 64 MiB reserve, 16 MB committed
    if (!on) { VirtualFree(p, 0, MEM_RELEASE); return NULL; }
    void* r = VirtualAlloc(NULL, 64u << 20, MEM_RESERVE, PAGE_READWRITE);
    if (r && !VirtualAlloc(r, 16u << 20, MEM_COMMIT, PAGE_READWRITE)) { VirtualFree(r, 0, MEM_RELEASE); r = NULL; }
    return r;
}
static void* MdActStack(void* p, bool on) {  // a suspended thread with a 4 MB stack reserve; off: let it run out and exit
    if (on) return CreateThread(NULL, 4u << 20, MdIdleThread, NULL, CREATE_SUSPENDED | STACK_SIZE_PARAM_IS_A_RESERVATION, NULL);
    if (p) { ResumeThread(p); WaitForSingleObject(p, 5000); CloseHandle(p); }
    return NULL;
}
static HANDLE g_mdMapH;
static void* MdActMap(void* p, bool on) {  // 32 MB pagefile-backed view
    if (!on) { UnmapViewOfFile(p); CloseHandle(g_mdMapH); g_mdMapH = NULL; return NULL; }
    g_mdMapH = CreateFileMappingW(INVALID_HANDLE_VALUE, NULL, PAGE_READWRITE, 0, 32u << 20, NULL);
    return g_mdMapH ? MapViewOfFile(g_mdMapH, FILE_MAP_WRITE, 0, 0, 32u << 20) : NULL;
}
static void* MdActRp(void* p, bool on) {  // routesnap's own heap (replay.cpp): rpKB counts the request size
    if (on) return RpAlloc(4u << 20);
    RpFree(p);
    return NULL;
}
// EA Malloc / Free on the MemoryMan heap, flag bit 3 = direct VirtualAlloc block (docs/re/structs.md §9; thiscall, alloc.cpp)
static bool MdEaUsable() {
    const u8* h = NULL;
    __try { h = *g_mdEaHeapSlot; } __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
    const u8* f = (const u8*)(uintptr_t)EA_MALLOC;
    // the game's prologue, or a jmp (ours [Alloc] or another module's detour: same ABI)
    return h && Readable(f, sizeof(kEaLockedPrologue)) && (!memcmp(f, kEaLockedPrologue, sizeof(kEaLockedPrologue)) || f[0] == 0xE9);
}
static void* MdEaAlloc(DWORD n) { return ((EaMallocFn)(uintptr_t)EA_MALLOC)((void*)*g_mdEaHeapSlot, NULL, n, 8); }
static void MdEaFree(void* p) { if (p) ((EaFreeFn)(uintptr_t)EA_FREE)((void*)*g_mdEaHeapSlot, NULL, p); }
static void* MdActEa(void* p, bool on) {
    if (on) return MdEaAlloc(8u << 20);
    MdEaFree(p);
    return NULL;
}

// One step: up to 3 tries (other threads allocate too); a try passes when every field moved by mb +- tol from before to
// during and (down = true) from after back to during. Logs the last try per field; returns the failed field bits.
static DWORD MdStep(const char* name, MdActFn act, const MdWant* w, int n, bool down) {
    MdVals a, b, c;
    double up[MD_N] = {}, dn[MD_N] = {};
    bool ok = false;
    int tries = 0;
    DWORD bad = 0;
    while (!ok && tries < 3) {
        tries++;
        MdTake(a);
        void* st = act(NULL, true);
        if (!st) { Log("diag: mem selftest %s: allocation failed", name); bad = ~0u; break; }
        MdTake(b);
        act(st, false);
        MdTake(c);
        ok = true, bad = 0;
        for (int i = 0; i < n; i++) {
            const int f = w[i].field;
            up[f] = b.v[f] - a.v[f], dn[f] = b.v[f] - c.v[f];
            const bool fOk = a.have[f] && b.have[f] && fabs(up[f] - w[i].mb) <= w[i].tol && (!down || fabs(dn[f] - w[i].mb) <= w[i].tol);
            if (!fOk) ok = false, bad |= 1u << f;
        }
    }
    if (bad == ~0u) {  // the allocation itself failed: this step's fields fail
        bad = 0;
        for (int i = 0; i < n; i++) bad |= 1u << w[i].field;
        return bad;
    }
    for (int i = 0; i < n; i++) {
        const int f = w[i].field;
        char d[48] = "";
        if (down) sprintf_s(d, ", back %+.2f", -dn[f]);
        Log("diag: mem selftest %s %s %+.2f%s (want %+.2f +-%.2f) %s, try %d", name, kMd[f].key, up[f], d, w[i].mb, w[i].tol,
            bad & (1u << f) ? "FAIL" : "ok", tries);
    }
    return bad;
}

// File mapping: the game maps and unmaps files all the time (streaming), so the whole-process map total moves by other
// mappings during the step (run 2026-10-25: +36.12 / back -30.62). The check is the walk's own count of our view: the walk
// that sums the map field must count all 32 MB of it as MEM_MAPPED while mapped and none after the unmap. The total is
// compared too; a try whose total missed while the view was exact is retried (other mappings moved), and after 3 such tries
// the view count decides. Returns 0 ok, 1 retry, 2 fail.
static int MdMapVerdict(u64 viewDuring, u64 viewAfter, u64 want, double up, double dn, double tol, bool last) {
    if (viewDuring != want) return 2;  // the walk did not count our view exactly
    if (viewAfter) return last ? 2 : 1;  // a mapping in our range after the unmap (address reused, or the unmap failed)
    const double w = want / 1048576.0;
    if (fabs(up - w) > tol || fabs(dn - w) > tol) return last ? 0 : 1;  // the total moved by other mappings only
    return 0;
}
static DWORD MdStepMap(double tol) {
    const u64 want = 32ull << 20;
    const double MB = 1.0 / (1 << 20);
    MdVals a, b, c;
    u64 vb = 0, vc = 0;
    int tries = 0, v = 1;
    double up = 0, dn = 0;
    while (v == 1) {
        tries++;
        MdTake(a);
        void* st = MdActMap(NULL, true);
        if (!st) { Log("diag: mem selftest file mapping 32 MB: allocation failed"); return 1u << MD_MAP; }
        vb = MdTake(b, st, want);
        MdActMap(st, false);
        vc = MdTake(c, st, want);
        up = b.v[MD_MAP] - a.v[MD_MAP], dn = b.v[MD_MAP] - c.v[MD_MAP];
        v = MdMapVerdict(vb, vc, want, up, dn, tol, tries >= 3);
    }
    char note[64] = "total moved by other mappings, not compared";
    if (fabs(up - want * MB) <= tol && fabs(dn - want * MB) <= tol) sprintf_s(note, "total within +-%.2f", tol);
    Log("diag: mem selftest file mapping 32 MB map view %+.2f, after %.2f (want %+.2f exact); total %+.2f, back %+.2f, other "
        "mappings %+.2f / %+.2f (%s) %s, try %d", vb * MB, vc * MB, want * MB, up, -dn, up - vb * MB, (vb - vc) * MB - dn,
        note, v ? "FAIL" : "ok", tries);
    return v ? 1u << MD_MAP : 0;
}

// game = in the game (EA heap steps, wider tolerances for the other threads' allocations); false = host selftest.
static bool MdSelfTest(bool game) {
    const double t = game ? 3.0 : 1.0;  // OS private bytes and walk sums: MB of noise allowed per step
    DWORD bad = 0, checked = 0;
    auto run = [&](const char* name, MdActFn act, std::initializer_list<MdWant> w, bool down) {
        for (const MdWant& x : w) checked |= 1u << x.field;
        bad |= MdStep(name, act, w.begin(), (int)w.size(), down);
    };
    run("commit 48 MB", MdActCommit, {{MD_PRIV, 48, t}, {MD_WPRIV, 48, t}, {MD_WCOM, 48, t}, {MD_FREE, -48, t}, {MD_MIRES, 0, 0.5}, {MD_STKRES, 0, 0.5}}, true);
    run("arena 64 MiB reserve, 16 MB commit", MdActArena, {{MD_MIRES, 64, 0.5}, {MD_MICOM, 16, 0.5}, {MD_PRIV, 16, t}, {MD_WPRIV, 16, t}}, true);
    // the stack is freed by the thread's exit, not by us: checked on the way up only
    run("thread stack 4 MB reserve", MdActStack, {{MD_STKRES, 4, 0.5}, {MD_MIRES, 0, 0.5}}, false);
    checked |= 1u << MD_MAP;
    bad |= MdStepMap(0.5);
    run("routesnap heap 4 MB", MdActRp, {{MD_RP, 4, game ? 1.0 : 0.01}}, true);
    const bool ea = game && MdEaUsable();
    if (ea) run("EA heap direct block 8 MB", MdActEa, {{MD_EADIR, 8, 0.1}, {MD_EADIRN, 1, 0}, {MD_PRIV, 8, t}}, true);
    else if (game) Log("diag: mem selftest EA heap: skipped (heap or Malloc entry not as expected)");

    // synthetic leak: 8 x 1 MB that stay allocated (EA direct blocks in the game, VirtualAlloc in the host); the report
    // tool must see +8 MB between the two tagged samples
    UdMemSample("diag: memory at selftest leak before", "selftest-leak-before");
    MdVals a, b;
    MdTake(a);
    static void* leak[8];
    for (void*& p : leak) p = ea ? MdEaAlloc(1u << 20) : VirtualAlloc(NULL, 1u << 20, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
    MdTake(b);
    UdMemSample("diag: memory at selftest leak after", "selftest-leak-after");
    const int lf = ea ? MD_EADIR : MD_WPRIV;
    const double lt = ea ? 0.2 : t + 1;
    for (int f : {(int)MD_PRIV, lf}) {
        const double d = b.v[f] - a.v[f], tol = f == MD_PRIV ? t + 1 : lt;
        const bool ok = a.have[f] && b.have[f] && fabs(d - 8) <= tol;
        checked |= 1u << f;
        if (!ok) bad |= 1u << f;
        Log("diag: mem selftest leak 8 x 1 MB %s %+.2f (want +8.00 +-%.2f) %s", kMd[f].key, d, tol, ok ? "ok" : "FAIL");
    }
    if (!game) for (void* p : leak) VirtualFree(p, 0, MEM_RELEASE);  // the host selftest cleans up; the game keeps them

    // cross-check against the OS: walk private committed vs PrivateUsage, and the attributed owners never above it
    MdTake(a);
    const double gap = fabs(a.v[MD_PRIV] - a.v[MD_WPRIV]), gapTol = a.v[MD_PRIV] / 10 > 64 ? a.v[MD_PRIV] / 10 : 64;
    const bool xOk = gap <= gapTol && a.v[MD_REST] >= -16;
    Log("diag: mem selftest cross-check: OS private %.0f MB vs walk private committed %.0f MB (gap %.1f, allowed %.0f), rest %.1f MB "
        "(allowed >= -16) %s", a.v[MD_PRIV], a.v[MD_WPRIV], gap, gapTol, a.v[MD_REST], xOk ? "ok" : "FAIL");
    if (!xOk) bad |= 1u << MD_PRIV | 1u << MD_WPRIV;

    InterlockedOr(&g_mdFail, (LONG)bad);
    InterlockedOr(&g_mdPass, (LONG)(checked & ~bad));
    char list[200] = "";
    for (int i = 0; i < MD_N; i++)
        if (bad & (1u << i)) { if (*list) strcat_s(list, " "); strcat_s(list, kMd[i].key); }
    Log(bad ? "diag: mem selftest FAIL: %s" : "diag: mem selftest PASS%s", bad ? list : " (every checked field moved by the injected size)");
    MdLegend("after the self-test");
    return !bad;
}

// memory worker (userdiag.cpp UdMemMain): runs the pending game self-test 30 s after the first world load; returns how long
// the worker may sleep before it wants to be called again (INFINITE = nothing pending)
static DWORD MdWorkerPoll() {
    if (!g_mdOn || g_mdSelfTest != 1) return INFINITE;
    if (!g_udWorlds) return 5000;
    if (!g_mdWorldTick) g_mdWorldTick = GetTickCount() | 1;
    const DWORD since = GetTickCount() - g_mdWorldTick;
    if (since < kMdSelfTestDelayMs) return kMdSelfTestDelayMs - since;
    g_mdSelfTest = 2;
    MdSelfTest(true);
    return INFINITE;
}

// Light (0.36.79, docs/191 "Player memory lines", release default on): the same "diag: mem" / "diag: memsus" lines (numbers
// only, reads only) at world load / travel arrival, save start / end, exit and every 5 min of play, without the self-test.
// Enabled=1 = the full tool for correctness runs (SelfTest allowed); Enabled=0 Light=0 = off.
static bool g_mdLight;
static void ApplyMemDiag(const wchar_t* ini) {
    const bool full = GetPrivateProfileIntW(L"MemDiag", L"Enabled", 0, ini) > 0;
    g_mdLight = !full && GetPrivateProfileIntW(L"MemDiag", L"Light", 1, ini) > 0;
    g_mdOn = full || g_mdLight;
    g_mdSelfTest = full && GetPrivateProfileIntW(L"MemDiag", L"SelfTest", 0, ini) > 0 ? 1 : 0;
    g_msSelfTest = g_mdSelfTest;  // the managed suspect-counter self-test (ts3fixes MemSuspects.cs) runs in the same runs
    if (g_mdOn) g_udMemPeriodMs = 5 * 60 * 1000;  // set before the memory worker starts (userdiag.cpp)
    if (g_mdOn) Log("mem diag %s (docs/191): diag: mem + memsus lines at each memory at line, every 5 min of play%s", g_mdLight ? "light" : "on",
                    g_mdSelfTest ? "; self-test 30 s after the first world load (correctness runs only: leaks 8 MB on purpose)" : "");
}
