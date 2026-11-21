// [Diag] ModTime (docs/298, default 0; a diagnostic). Included by ts3native.cpp after stallwatch.cpp (needs simloop.cpp
// SimLoopAdd, apex.cpp UserSims3Dir, decompress.cpp RefpackRef, userdiag.cpp g_udOn).
//
// Which script mods use the sim thread's time, without removing anything. A sampler thread (ModTimeHz, default 10, 10-200)
// suspends the sim thread for a few microseconds, reads eip and its Mono interpreter frame chain (TEB TLS slot [0x11F4160]
// -> context, +8 current frame, frame[0] parent, frame[1] info, info +8 MonoMethod*: the walk of autledger.cpp AsLgFrames,
// docs/04 F6a [verified]) and resumes it. No lock, heap or log while it is suspended; every read under SEH.
// After the resume: MonoMethod +8 class, +0x18 name; MonoClass +0 image, +0x34 / +0x38 name / namespace (docs/104 §4
// [verified]); MonoImage +0x18 assembly name, +0x1C module name [verified by the 2026-11-20 smoke: real assembly names,
// docs/51 I-544-1], +0x50 #GUID heap = the module MVID (docs/104 §4 [verified]).
// Weight of a sample = the sim thread's CPU cycles since the previous one (QueryThreadCycleTime), so waits weigh nothing and
// the shares are shares of sim-thread CPU. Two owners per sample: "with game code" (inclusive) = the innermost frame of a
// mod (not EA, not ours), else the innermost ts3fixes frame, else the innermost frame (game); "own" (self) = the innermost
// frame's assembly. No frame = "engine". eip inside the Boehm GC code (0xE49A00-0xE51380 [inferred range around the verified
// GC functions 0xE4A050..0xE50380, docs/130 §2, I-544-3]) also counts as gc/alloc time of the inclusive owner. Each live
// Simulate iteration >= 250 ms keeps its samples' top 3 owner:method (hitch stacks) and credits the owner with the most
// samples ("hitches topped").
// Phases: from the first Simulate iteration until ts3fixes opens the first sim hour (op 61 hi 1) the samples go to the load
// block (front end, world load, world-load-finished handlers); while a world is live to the main tables; after a world quit
// to the load block again.
// Mod -> package: a one-time scan (low-priority thread, at the first summary) of every .package under <user>\Mods (and the
// other Electronic Arts\*\Mods folders): S3SA resources (type 0x073FAA07, RefPack decoded, XOR-decrypted), MVID from the
// metadata #GUID stream, matched to the loaded image's MVID.
// Files next to ts3native.log: modtime-summary.txt (the mod cost report, docs/302 §5) and modtime.csv (top 30 owners per
// sim hour), written by the sampler thread at each sim hour, at world quit and every 10 real minutes. One in-game notice per
// game session (ts3fixes ModTime.cs reads its text bit by bit, op 61 hi 3 / 4) once 2 sim hours are sampled.
// Nothing here changes the game, a Sim or a setting (read-only: no household rule applies).
//
// For other lanes: MdtTablesInit() + MdtOwnerOf(MonoMethod*) give the cached owner of one method from any thread.

static const DWORD kMdtType = 0x073FAA07;  // S3SA
enum { MDT_ENGINE, MDT_GAME, MDT_OURS, MDT_MOD };
static const char* const kMdtKind[4] = {"engine", "game", "ours", "mod"};

// ---- pure parts (selftest) ----
// the module MVID (first #GUID entry) of a .NET image somewhere in b; false = no metadata root
static bool MdtMvidOf(const u8* b, DWORD n, u8* mvid) {
    for (DWORD i = 0; i + 32 <= n; i++) {  // byte steps: in a raw S3SA body the image starts after an odd-length header
        if (memcmp(b + i, "BSJB", 4)) continue;
        const u8* r = b + i;
        const DWORD vlen = *(const DWORD*)(r + 12);
        if (vlen > 256 || i + 16 + vlen + 4 > n) continue;
        const u8* p = r + 16 + vlen;
        const WORD streams = *(const WORD*)(p + 2);
        p += 4;
        for (WORD s = 0; s < streams && p + 9 <= b + n; s++) {
            const DWORD off = *(const DWORD*)p, size = *(const DWORD*)(p + 4);
            const char* name = (const char*)(p + 8);
            size_t nl = 0;
            while (p + 8 + nl < b + n && name[nl]) nl++;
            if (nl == 5 && !memcmp(name, "#GUID", 5) && size >= 16 && i + off + 16 <= n) { memcpy(mvid, r + off, 16); return true; }
            p += 8 + ((nl + 4) & ~3u);
        }
    }
    return false;
}
// S3SA body (s3pi ScriptResource): version, [UTF-16 string if version > 1], u32, md5 (64), u16 count, count x 8-byte table,
// then XOR-chained 512-byte blocks (a table entry with bit 0 set = a zero block that takes no input). Returns the clear
// image (malloc, count x 512 bytes) or NULL. Port of tools/extract-assemblies.ts decryptS3SA [verified: smoke 2026-11-20
// found 0 MVIDs in 81 S3SA resources without it].
static u8* MdtS3saDecrypt(const u8* b, DWORD n, DWORD* outN) {
    DWORD p = 0;
    if (n < 1) return NULL;
    const u8 ver = b[p++];
    if (ver > 1) { if (p + 4 > n) return NULL; const DWORD sl = *(const DWORD*)(b + p); if (sl > 4096) return NULL; p += 4 + sl * 2; }
    p += 4 + 64;
    if (p + 2 > n) return NULL;
    const DWORD count = *(const WORD*)(b + p);
    p += 2;
    if (!count || p + count * 8 > n) return NULL;
    const u8* table = b + p;
    const DWORD tlen = count * 8;
    p += tlen;
    unsigned long long sum = 0;
    for (DWORD i = 0; i < tlen; i += 8) sum += *(const unsigned long long*)(table + i);
    DWORD seed = (DWORD)((unsigned long long)(tlen - 1) & sum);
    u8* out = (u8*)calloc(count, 512);
    if (!out) return NULL;
    DWORD r = p;
    for (DWORD blk = 0; blk < count; blk++) {
        if (table[blk * 8] & 1) continue;
        if (r + 512 > n) { free(out); return NULL; }
        for (DWORD j = 0; j < 512; j++, r++) {
            const u8 v = b[r];
            out[blk * 512 + j] = v ^ table[seed];
            seed = (seed + v) % tlen;
        }
    }
    *outN = count * 512;
    return out;
}
static bool MdtGameName(const char* a) {
    static const char* const kEa[] = {"mscorlib", "System", "System.Xml", "ScriptCore", "SimIFace", "Sims3GameplaySystems", "Sims3GameplayObjects",
                                      "Sims3StoreObjects", "Sims3Metadata", "UI", "TestObjects"};
    for (const char* e : kEa) if (!_stricmp(a, e)) return true;
    return false;
}
static int MdtKindOf(const char* asmName) { return MdtGameName(asmName) ? MDT_GAME : !_strnicmp(asmName, "ts3fixes", 8) ? MDT_OURS : MDT_MOD; }
// the inclusive owner frame of a sample: innermost mod, else innermost ours, else innermost (game); -1 = no frame.
// The own (self) owner is frame 0 when n > 0.
static int MdtPick(const int* kinds, int n) {
    for (int want = MDT_MOD; want >= MDT_OURS; want--)
        for (int i = 0; i < n; i++) if (kinds[i] == want) return i;
    return n > 0 ? 0 : -1;
}
// docs/298 §5.1: the inclusive pick is a ts3fixes frame but frame 0 (own) is game: a pass-through hook (FestivalHiddenHook,
// EcHook, TrafficCreateHook, SpatialCacheHook) running EA's body, so the time is the game's, not ours
static bool MdtViaHook(const int* kinds, int n, int pick) { return pick > 0 && pick < n && kinds[pick] == MDT_OURS && kinds[0] == MDT_GAME; }
// "game via ts3fixes hook (<class>)" from the hook frame's "Ns.Class.Method" name
static void MdtHookLabel(const char* meth, char* out, size_t cap) {
    char c[96];
    strncpy_s(c, meth ? meth : "?", _TRUNCATE);
    if (char* dot = strrchr(c, '.')) *dot = 0;  // drop the method
    const char* cls = strrchr(c, '.');
    _snprintf_s(out, cap, _TRUNCATE, "game via ts3fixes hook (%s)", cls ? cls + 1 : c);
}
// docs/302 §4.4 notice rule (info only): sim hours in a row at >= 25% of sim CPU
static int MdtHotStep(int run, double share) { return share >= 0.25 ? run + 1 : 0; }
// the package's file name without folders, "core mod: " and ".package" (into d)
static void MdtPkgBase(const char* pkg, char* d, size_t cap) {
    if (!_strnicmp(pkg, "core mod: ", 10)) pkg += 10;
    const char* s = strrchr(pkg, '\\');
    strncpy_s(d, cap, s ? s + 1 : pkg, _TRUNCATE);
    const size_t l = strlen(d);
    if (l > 8 && !_stricmp(d + l - 8, ".package")) d[l - 8] = 0;
}
// docs/COMPATIBILITY.md has an overlap row for this package (file name match, case-insensitive)
// shortcut: snapshot of the 25 overlap rows of docs/data/compat-matrix.csv (2026-11-05); refresh when that table changes.
static bool MdtCompatRow(const char* pkg) {
    static const char* const kRows[] = {"simler90GameplayCoreMod-UPDATE242-Steam", "simler90GameplayCoreMod-UPDATE242-Steam+fixes", "NRaas_ErrorTrap",
                                        "NRaas_Overwatch", "NRaas_MasterController", "NRaas_Register", "Sims 3 Store Updates 021-040 v1.4",
                                        "Sims 3 Store Updates 041-051 v1.4", "Buzz_MoarInteractions", "Library_5", "Library_7", "Library_10", "Library_14",
                                        "Library_15", "Library_16", "wexxamsims_SeasonsExpanded", "icarusallsorts_EatOutsideRestaurant_fixed", "Overrides",
                                        "tiny-ui-fix"};
    char b[128];
    MdtPkgBase(pkg, b, sizeof(b));
    for (const char* r : kRows) if (!_stricmp(b, r)) return true;
    return false;
}
// the one in-game notice of a session (docs/302 §5): busy line, top n mods own / with game code, where the file is
static void MdtNoteText(double busyPct, const char* const* names, const double* self, const double* incl, int n, char* out, size_t cap) {
    _snprintf_s(out, cap, _TRUNCATE, "Mod cost report: your sim thread was busy %.0f%% of the time%s. ", busyPct,
                busyPct >= 90 ? " (the simulation cannot keep up)" : "");
    if (!n) strncat_s(out, cap, "No script mod showed up in the samples. ", _TRUNCATE);
    else {
        strncat_s(out, cap, "Top script mods by share of sim CPU (own / with the game code they call): ", _TRUNCATE);
        for (int i = 0; i < n; i++) {
            char one[96];
            _snprintf_s(one, _TRUNCATE, "%s%.40s %.0f%% / %.0f%%", i ? ", " : "", names[i], self[i], incl[i]);
            strncat_s(out, cap, one, _TRUNCATE);
        }
        strncat_s(out, cap, ". ", _TRUNCATE);
    }
    strncat_s(out, cap, "Full list: modtime-summary.txt in the game's Bin folder.", _TRUNCATE);
}

// ---- tables (the sampler thread and MdtOwnerOf callers write under g_mdtCs) ----
struct MdtMod {
    DWORD image; char name[64]; char pkg[120]; u8 mvid[16]; bool mvidOk; int kind;
    double w, selfW, gcW, hourW, loadW, loadSelfW;  // live inclusive / own / gc, this sim hour inclusive, load phase inclusive / own
    LONG n, hitTop, hotRun, hotMax; double hotPeak;  // samples, hitches topped, sim hours >= 25% in a row (now / longest), peak share
};
struct MdtMeth { DWORD key; int mod; char name[120]; double w, selfW, gcW; LONG n; };
static MdtMod g_mdtMod[512];
static volatile int g_mdtModN;
static MdtMeth g_mdtMeth[8192];
static volatile int g_mdtMethN;
static volatile int g_mdtMethHash[16384];  // index + 1
static double g_mdtTotalW, g_mdtGcW, g_mdtHourTotal, g_mdtLoadTotalW;
static LONG g_mdtSamples, g_mdtLoadSamples, g_mdtActive, g_mdtFramesBad, g_mdtHoursDone;
static LONGLONG g_mdtOverheadUs, g_mdtStartUs, g_mdtLiveWallUs, g_mdtLiveCpu100, g_mdtLoadWallUs, g_mdtLoadCpu100;
static CRITICAL_SECTION g_mdtCs;
static LONG g_mdtOn, g_mdtHz = 10, g_mdtInit;
static wchar_t g_mdtSum[MAX_PATH], g_mdtCsv[MAX_PATH];
static DWORD g_mdtSession;
static char g_mdtNote[480];
static volatile LONG g_mdtNoteReady;  // the notice text is final (written once, then read by op 61 hi 4)

struct MdtHitch { LONG ms, hour, n, active, gc; char top[3][130]; LONG topN[3]; };
static MdtHitch g_mdtHit[20];
static int g_mdtHitN;
static LONG g_mdtHitLines, g_mdtHitAll;
static volatile LONG g_mdtHour = -1;

// sim thread state (simloop callback)
static volatile DWORD g_mdtSimTid, g_mdtTeb;
static volatile LONG g_mdtSeq;
static volatile LONGLONG g_mdtEndUs, g_mdtPrevEndUs;
static const DWORD* g_mdtTlsIdx = (const DWORD*)(uintptr_t)TS3A(0x011F4160);
static const DWORD MDT_GC_LO = TS3A(0x00E4A050) - 0x650, MDT_GC_HI = TS3A(0x00E50380) + 0x1000;  // [inferred] Boehm code range

static LONGLONG MdtUs() {
    static LARGE_INTEGER f;
    LARGE_INTEGER c;
    if (!f.QuadPart) QueryPerformanceFrequency(&f);
    QueryPerformanceCounter(&c);
    return c.QuadPart * 1000000 / f.QuadPart;
}

static void __cdecl MdtIter() {
    if (!g_mdtSimTid) { g_mdtTeb = __readfsdword(0x18); g_mdtSimTid = GetCurrentThreadId(); }
    g_mdtPrevEndUs = g_mdtEndUs;
    g_mdtEndUs = MdtUs();
    InterlockedIncrement(&g_mdtSeq);
}

// printable ASCII up to cap-1 from a game string pointer; false = unreadable / empty
static bool MdtStr(const char* s, char* d, int cap) {
    int n = 0;
    __try {
        for (; s && n < cap - 1 && s[n] >= 0x20 && s[n] < 0x7F; n++) d[n] = s[n];
    } __except (EXCEPTION_EXECUTE_HANDLER) { n = 0; }
    d[n] = 0;
    return n > 0;
}
static bool MdtRead(DWORD at, void* d, int n) {
    __try { memcpy(d, (const void*)(uintptr_t)at, n); return true; } __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}

// the owner row of a MonoImage (index >= 2; 1 = table full). Caller holds g_mdtCs.
static int MdtModOf(DWORD image) {
    for (int i = 2; i < g_mdtModN; i++) if (g_mdtMod[i].image == image) return i;
    if (g_mdtModN == 512) return 1;
    MdtMod m = {};
    m.image = image;
    DWORD an = 0, mn = 0, guid = 0;
    MdtRead(image + 0x18, &an, 4), MdtRead(image + 0x1C, &mn, 4), MdtRead(image + 0x50, &guid, 4);
    if (!MdtStr((const char*)(uintptr_t)an, m.name, sizeof(m.name))) {
        if (!MdtStr((const char*)(uintptr_t)mn, m.name, sizeof(m.name))) _snprintf_s(m.name, _TRUNCATE, "image@%08lX", image);
        else if (char* dot = strrchr(m.name, '.')) if (!_stricmp(dot, ".dll")) *dot = 0;
    }
    m.mvidOk = guid && MdtRead(guid, m.mvid, 16);
    m.kind = MdtKindOf(m.name);
    g_mdtMod[g_mdtModN] = m;
    return g_mdtModN++;
}
// the "game via ts3fixes hook (<class>)" row of hook method row mi (image 0, kind game: no notice-rule weight; 1 = table full)
static int MdtViaHookOf(int mi) {
    char lab[64];
    MdtHookLabel(g_mdtMeth[mi].name, lab, sizeof(lab));
    EnterCriticalSection(&g_mdtCs);
    int r = 2;
    while (r < g_mdtModN && (g_mdtMod[r].image || strcmp(g_mdtMod[r].name, lab))) r++;
    if (r == g_mdtModN) {
        if (g_mdtModN == 512) r = 1;
        else { MdtMod m = {}; strcpy_s(m.name, lab); m.kind = MDT_GAME; g_mdtMod[g_mdtModN] = m; g_mdtModN++; }
    }
    LeaveCriticalSection(&g_mdtCs);
    return r;
}

// hash probe: the entry index, or -1 with *slot = the free slot (-1 when the hash is full)
static int MdtMethFind(DWORD method, int* slot) {
    DWORD h = (method >> 3) * 2654435761u >> 18;
    for (int probe = 0; probe < 16384; probe++, h = (h + 1) & 16383) {
        const int i = g_mdtMethHash[h];
        if (!i) { *slot = (int)h; return -1; }
        if (g_mdtMeth[i - 1].key == method) return i - 1;
    }
    *slot = -1;
    return -1;
}
// the method row of a MonoMethod* (cached; -1 = table full). Hits take no lock; a miss reads the names under g_mdtCs.
static int MdtMethOf(DWORD method) {
    int slot;
    int r = MdtMethFind(method, &slot);
    if (r >= 0) return r;
    EnterCriticalSection(&g_mdtCs);
    r = MdtMethFind(method, &slot);  // another thread may have added it meanwhile
    if (r < 0 && slot >= 0 && g_mdtMethN < 8192) {
        MdtMeth m = {};
        m.key = method;
        DWORD klass = 0, mname = 0, image = 0, cname = 0, cns = 0;
        char c[64] = "", ns[64] = "", mn[48] = "";
        if (MdtRead(method + 8, &klass, 4) && klass && MdtRead(klass, &image, 4) && image) {
            MdtRead(klass + 0x34, &cname, 4), MdtRead(klass + 0x38, &cns, 4), MdtRead(method + 0x18, &mname, 4);
            MdtStr((const char*)(uintptr_t)cns, ns, sizeof(ns)), MdtStr((const char*)(uintptr_t)cname, c, sizeof(c)), MdtStr((const char*)(uintptr_t)mname, mn, sizeof(mn));
            m.mod = MdtModOf(image);
        } else m.mod = 1;
        _snprintf_s(m.name, _TRUNCATE, "%s%s%s.%s", ns, ns[0] ? "." : "", c[0] ? c : "?", mn[0] ? mn : "?");
        r = g_mdtMethN;
        g_mdtMeth[r] = m;
        g_mdtMethN = r + 1;
        MemoryBarrier();  // the row is complete before a lock-free reader can find it
        g_mdtMethHash[slot] = r + 1;
    }
    LeaveCriticalSection(&g_mdtCs);
    return r;
}

// The tables (critical section, engine and unreadable rows) once; ApplyModTime calls it, another lane may call it in its Apply.
static void MdtTablesInit() {
    if (g_mdtInit) return;
    g_mdtInit = 1;
    InitializeCriticalSection(&g_mdtCs);
    strcpy_s(g_mdtMod[MDT_ENGINE].name, "engine (no script on the stack)"), g_mdtMod[MDT_ENGINE].kind = MDT_ENGINE;
    strcpy_s(g_mdtMod[1].name, "(unreadable method)"), g_mdtMod[1].kind = MDT_GAME;
    g_mdtModN = 2;
}
// Owner of one MonoMethod* for any caller and thread (e.g. a per-task-run seam): cached per method (8192 methods, 512
// assemblies), names read once under SEH. false = no tables (MdtTablesInit not called), method 0 or the table full.
// idx = owner row (stable for the session; 1 = unreadable), kind = MDT_GAME / MDT_OURS / MDT_MOD, name = assembly name,
// pkg = Mods package once the scan matched it ("" before), mvid valid when mvidOk. The pointers stay valid for the session.
struct MdtOwner { int idx, kind; const char* name; const char* pkg; const u8* mvid; bool mvidOk; };
bool MdtOwnerOf(DWORD monoMethod, MdtOwner* out) {  // external linkage: no asi caller yet (no C4505)
    if (!g_mdtInit || !monoMethod) return false;
    const int mi = MdtMethOf(monoMethod);
    if (mi < 0) return false;
    const MdtMod& m = g_mdtMod[g_mdtMeth[mi].mod];
    out->idx = g_mdtMeth[mi].mod, out->kind = m.kind, out->name = m.name, out->pkg = m.pkg, out->mvid = m.mvid, out->mvidOk = m.mvidOk;
    return true;
}
// The same for a MonoImage* (a class's assembly, MonoClass +0: taskseam.cpp's ScriptProxy targets). Same rows as MdtOwnerOf.
static bool MdtOwnerOfImage(DWORD image, MdtOwner* out) {
    if (!g_mdtInit || !image) return false;
    EnterCriticalSection(&g_mdtCs);
    const int i = MdtModOf(image);
    LeaveCriticalSection(&g_mdtCs);
    const MdtMod& m = g_mdtMod[i];
    out->idx = i, out->kind = m.kind, out->name = m.name, out->pkg = m.pkg, out->mvid = m.mvid, out->mvidOk = m.mvidOk;
    return true;
}

// suspended read of the sim thread: eip + up to n method pointers, innermost first; -1 = no context
static int MdtGrab(HANDLE h, DWORD* eip, DWORD* m, int n) {
    if (SuspendThread(h) == (DWORD)-1) return -1;
    CONTEXT c = {};
    c.ContextFlags = CONTEXT_CONTROL;
    int got = -1;
    if (GetThreadContext(h, &c)) {  // also makes sure the suspend took effect
        *eip = c.Eip;
        got = 0;
        __try {
            const DWORD idx = *g_mdtTlsIdx;
            const DWORD teb = g_mdtTeb;
            DWORD ctx = 0;
            if (idx < 64) ctx = *(const DWORD*)(uintptr_t)(teb + 0xE10 + idx * 4);
            else if (idx < 1088) { const DWORD ext = *(const DWORD*)(uintptr_t)(teb + 0xF94); ctx = ext ? *(const DWORD*)(uintptr_t)(ext + (idx - 64) * 4) : 0; }
            for (const DWORD* f = ctx ? *(const DWORD* const*)(uintptr_t)(ctx + 8) : NULL; f && got < n; f = (const DWORD*)(uintptr_t)f[0]) {
                const DWORD info = f[1];
                m[got++] = info ? *(const DWORD*)(uintptr_t)(info + 8) : 0;
            }
        } __except (EXCEPTION_EXECUTE_HANDLER) { got = -2; }
    }
    ResumeThread(h);
    return got;
}

static void MdtHitchAdd(const MdtHitch& t) {
    int at = g_mdtHitN;
    while (at > 0 && g_mdtHit[at - 1].ms < t.ms) at--;
    if (at >= 20) return;
    const int last = g_mdtHitN < 20 ? g_mdtHitN : 19;
    for (int i = last; i > at; i--) g_mdtHit[i] = g_mdtHit[i - 1];
    g_mdtHit[at] = t;
    if (g_mdtHitN < 20) g_mdtHitN++;
}
// the index of the most frequent value in v[0..n) (first on ties); -1 = n 0. Used for the owner a hitch credits.
static int MdtMode(const int* v, int n, int* count) {
    int best = -1, bestN = 0;
    for (int i = 0; i < n; i++) {
        int c = 0;
        for (int j = 0; j < n; j++) c += v[j] == v[i];
        if (c > bestN) best = i, bestN = c;
    }
    *count = bestN;
    return best;
}

// the samples of one live iteration: methods (index, -1 engine), inclusive owners and gc flags
struct MdtIterBuf { int meth[256]; int mod[256]; u8 gc[256]; u8 active[256]; int n; };
static void MdtIterEnd(MdtIterBuf& b, LONG ms) {
    if (ms >= 250 && b.n > 0) {
        MdtHitch t = {};
        t.ms = ms, t.hour = g_mdtHour, t.n = b.n;
        int key[256], cnt[256], k = 0;
        for (int i = 0; i < b.n; i++) {
            t.active += b.active[i], t.gc += b.gc[i];
            int j = 0;
            while (j < k && key[j] != b.meth[i]) j++;
            if (j == k) key[k] = b.meth[i], cnt[k++] = 0;
            cnt[j]++;
        }
        int topN = 0;
        const int top = MdtMode(b.mod, b.n, &topN);
        EnterCriticalSection(&g_mdtCs);
        for (int r = 0; r < 3; r++) {
            int best = -1;
            for (int j = 0; j < k; j++) if (cnt[j] > 0 && (best < 0 || cnt[j] > cnt[best])) best = j;
            if (best < 0) break;
            const int mi = key[best];
            if (mi < 0) strcpy_s(t.top[r], "engine (no script on the stack)");
            else _snprintf_s(t.top[r], _TRUNCATE, "%s: %s", g_mdtMod[g_mdtMeth[mi].mod].name, g_mdtMeth[mi].name);
            t.topN[r] = cnt[best];
            cnt[best] = 0;
        }
        if (top >= 0) g_mdtMod[b.mod[top]].hitTop++;
        MdtHitchAdd(t);
        g_mdtHitAll++;
        LeaveCriticalSection(&g_mdtCs);
        if (g_mdtHitLines++ < 2000)
            Log("modtime: hitch %ld ms sim hour %ld, %ld samples (%ld on the CPU, %ld in gc/alloc): %s (%ld), %s (%ld), %s (%ld)", ms, t.hour, t.n, t.active, t.gc,
                t.top[0], t.topN[0], t.topN[1] ? t.top[1] : "-", t.topN[1], t.topN[2] ? t.top[2] : "-", t.topN[2]);
    }
    b.n = 0;
}

static LONGLONG MdtFt(const FILETIME& f) { return (LONGLONG)((ULONGLONG)f.dwHighDateTime << 32 | f.dwLowDateTime); }
static double MdtBusyPct() { return g_mdtLiveWallUs > 0 ? g_mdtLiveCpu100 / 10.0 * 100 / g_mdtLiveWallUs : 0; }

static void MdtWriteSummary(bool hourEnd, LONG hour);
static volatile LONG g_mdtReq, g_mdtReqHour;  // from op 61: 1 = sim hour g_mdtReqHour ended, 2 = world quit
static DWORD WINAPI MdtSampler(LPVOID) {
    SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_ABOVE_NORMAL);  // a saturated PC must not starve the sampler (sampling bias)
    while (!g_mdtSimTid) Sleep(100);
    HANDLE h = OpenThread(THREAD_SUSPEND_RESUME | THREAD_GET_CONTEXT | THREAD_QUERY_INFORMATION, FALSE, g_mdtSimTid);
    if (!h) { Log("modtime: cannot open the sim thread (err %lu): no samples", GetLastError()); return 0; }
    Log("modtime: sampling the sim thread (tid %lu) at %ld Hz from its first iteration", g_mdtSimTid, g_mdtHz);
    Ts3nCounters& k = ts3native_counters;
    g_mdtStartUs = MdtUs();
    static MdtIterBuf buf;
    LONG seenSeq = g_mdtSeq;
    ULONG64 lastCyc = 0;
    QueryThreadCycleTime(h, &lastCyc);
    FILETIME tc, te, tk, tu;
    LONGLONG lastCpu = GetThreadTimes(h, &tc, &te, &tk, &tu) ? MdtFt(tk) + MdtFt(tu) : 0, lastWall = MdtUs();
    DWORD lastWrite = GetTickCount();
    bool wasLive = false;
    for (;;) {
        Sleep(1000 / g_mdtHz);  // read per sample: [AdaptDiag] (docs/316) raises the rate for a hitch drill-down at run time
        const LONG req = InterlockedExchange(&g_mdtReq, 0);
        if (req & 1) MdtWriteSummary(true, g_mdtReqHour), lastWrite = GetTickCount();
        else if (req & 2 || GetTickCount() - lastWrite >= 600000) MdtWriteSummary(false, -1), lastWrite = GetTickCount();
        const bool live = g_mdtHour >= 0;
        if (live && !wasLive)
            Log("modtime: world live: load / menu phase so far %ld samples over %.1f real s (%.1f s sim thread CPU)", g_mdtLoadSamples, g_mdtLoadWallUs / 1e6, g_mdtLoadCpu100 / 1e7);
        wasLive = live;
        // busy share (wall vs the sim thread's kernel + user time), per phase
        const LONGLONG nowWall = MdtUs();
        const LONGLONG cpu = GetThreadTimes(h, &tc, &te, &tk, &tu) ? MdtFt(tk) + MdtFt(tu) : lastCpu;
        if (live) g_mdtLiveWallUs += nowWall - lastWall, g_mdtLiveCpu100 += cpu - lastCpu;
        else g_mdtLoadWallUs += nowWall - lastWall, g_mdtLoadCpu100 += cpu - lastCpu;
        lastWall = nowWall, lastCpu = cpu;
        const LONG seq = g_mdtSeq;
        if (!live) seenSeq = seq, buf.n = 0;  // hitch stacks while a world is live only (load has its own block)
        else if (seq != seenSeq) {
            const LONGLONG end = g_mdtEndUs, prev = g_mdtPrevEndUs;
            MdtIterEnd(buf, seq == seenSeq + 1 && prev ? (LONG)((end - prev) / 1000) : 0);  // more than one iteration passed: short ones
            seenSeq = seq;
        }
        ULONG64 cyc = 0;
        QueryThreadCycleTime(h, &cyc);
        const double w = (double)(cyc - lastCyc);
        lastCyc = cyc;
        DWORD eip = 0, m[48];
        const LONGLONG t0 = MdtUs();
        const int n = MdtGrab(h, &eip, m, 48);
        g_mdtOverheadUs += MdtUs() - t0;
        if (n == -1) continue;
        if (n == -2) g_mdtFramesBad++;
        int kinds[48], meths[48], kk = 0;
        for (int i = 0; i < n; i++) {
            if (!m[i]) continue;
            const int mi = MdtMethOf(m[i]);
            if (mi < 0) continue;
            meths[kk] = mi, kinds[kk++] = g_mdtMod[g_mdtMeth[mi].mod].kind;
        }
        const int pick = MdtPick(kinds, kk);
        const int mi = pick >= 0 ? meths[pick] : -1, mod = mi < 0 ? MDT_ENGINE : MdtViaHook(kinds, kk, pick) ? MdtViaHookOf(mi) : g_mdtMeth[mi].mod;
        const int si = kk > 0 ? meths[0] : -1, selfMod = si >= 0 ? g_mdtMeth[si].mod : MDT_ENGINE;
        const bool gc = eip >= MDT_GC_LO && eip < MDT_GC_HI;
        EnterCriticalSection(&g_mdtCs);
        if (!live) {
            g_mdtLoadSamples++;
            g_mdtLoadTotalW += w;
            g_mdtMod[mod].loadW += w, g_mdtMod[selfMod].loadSelfW += w;
        } else {
            g_mdtSamples++;
            if (w > 0) g_mdtActive++;
            g_mdtTotalW += w, g_mdtHourTotal += w;
            g_mdtMod[mod].w += w, g_mdtMod[mod].hourW += w, g_mdtMod[mod].n++;
            g_mdtMod[selfMod].selfW += w;
            if (mi >= 0) g_mdtMeth[mi].w += w, g_mdtMeth[mi].n++;
            if (si >= 0) g_mdtMeth[si].selfW += w;
            if (gc) { g_mdtGcW += w, g_mdtMod[mod].gcW += w; if (mi >= 0) g_mdtMeth[mi].gcW += w; }
        }
        LeaveCriticalSection(&g_mdtCs);
        if (live && buf.n < 256) buf.meth[buf.n] = mi, buf.mod[buf.n] = mod, buf.gc[buf.n] = gc, buf.active[buf.n] = w > 0, buf.n++;
        k.mdtSamples = g_mdtSamples, k.mdtLoadSamples = g_mdtLoadSamples, k.mdtOverheadUs = (LONG)g_mdtOverheadUs, k.mdtAsm = g_mdtModN - 2;
        k.mdtHitches = g_mdtHitAll, k.mdtBusyPct = (LONG)MdtBusyPct();
    }
}

// ---- package scan: MVID -> package file (relative to its Mods folder) ----
struct MdtPkg { u8 mvid[16]; char pkg[120]; };
static MdtPkg* g_mdtPkg;
static int g_mdtPkgN, g_mdtPkgFiles, g_mdtPkgS3sa;
static volatile LONG g_mdtScan;  // 0 not started, 1 running, 2 done

static void MdtScanFile(const wchar_t* path, const wchar_t* rel) {
    HANDLE h = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL, OPEN_EXISTING, FILE_FLAG_SEQUENTIAL_SCAN, NULL);
    if (h == INVALID_HANDLE_VALUE) return;
    u8 hd[96];
    DWORD got;
    if (ReadFile(h, hd, 96, &got, NULL) && got == 96 && !memcmp(hd, "DBPF", 4)) {
        g_mdtPkgFiles++;
        const DWORD count = *(DWORD*)(hd + 36), isz = *(DWORD*)(hd + 44), ipos = *(DWORD*)(hd + 64);
        u8* ix = isz && isz < (64u << 20) ? (u8*)malloc(isz) : NULL;
        LARGE_INTEGER at;
        at.QuadPart = ipos;
        if (ix && SetFilePointerEx(h, at, NULL, FILE_BEGIN) && ReadFile(h, ix, isz, &got, NULL) && got == isz && isz >= 4) {
            const u8* p = ix + 4;
            const u8* end = ix + isz;
            const DWORD flags = *(DWORD*)ix;
            DWORD constType = 0;
            if (flags & 1) constType = *(DWORD*)p;
            for (int b = 0; b < 3; b++) if (flags & (1u << b)) p += 4;
            for (DWORD i = 0; i < count; i++) {
                DWORD type = constType;
                for (int b = 0; b < 3; b++) if (!(flags & (1u << b))) { if (b == 0 && p + 4 <= end) type = *(DWORD*)p; p += 4; }
                if (p + 20 > end) break;
                const DWORD off = *(DWORD*)(p + 4), fsize = *(DWORD*)(p + 8) & 0x7fffffff, msize = *(DWORD*)(p + 12);
                const WORD comp = *(WORD*)(p + 16);
                p += 20;
                if (type != kMdtType || fsize < 64 || fsize > (32u << 20) || msize > (64u << 20)) continue;
                g_mdtPkgS3sa++;
                u8* raw = (u8*)malloc(fsize);
                at.QuadPart = off;
                if (raw && SetFilePointerEx(h, at, NULL, FILE_BEGIN) && ReadFile(h, raw, fsize, &got, NULL) && got == fsize) {
                    u8* data = raw;
                    DWORD dn = fsize;
                    u8* dec = NULL;
                    if (comp == 0xFFFF && msize) {
                        dec = (u8*)malloc(msize);
                        const int r = dec ? RefpackRef(dec, msize, raw, fsize) : 0;
                        if (r > 0) data = dec, dn = (DWORD)r;
                    }
                    u8 mvid[16];
                    bool got16 = MdtMvidOf(data, dn, mvid);
                    if (!got16) {
                        DWORD cn = 0;
                        if (u8* clr = MdtS3saDecrypt(data, dn, &cn)) { got16 = MdtMvidOf(clr, cn, mvid); free(clr); }
                    }
                    if (got16 && g_mdtPkgN < 4096) {
                        MdtPkg& e = g_mdtPkg[g_mdtPkgN++];
                        memcpy(e.mvid, mvid, 16);
                        WideCharToMultiByte(CP_UTF8, 0, rel, -1, e.pkg, sizeof(e.pkg), NULL, NULL);
                        e.pkg[sizeof(e.pkg) - 1] = 0;
                    }
                    free(dec);
                }
                free(raw);
            }
        }
        free(ix);
    }
    CloseHandle(h);
}
static void MdtWalk(const wchar_t* dir, const wchar_t* rel, int depth) {
    wchar_t pat[MAX_PATH];
    if (swprintf_s(pat, L"%s\\*", dir) < 0) return;
    WIN32_FIND_DATAW fd;
    HANDLE f = FindFirstFileW(pat, &fd);
    if (f == INVALID_HANDLE_VALUE) return;
    do {
        if (fd.cFileName[0] == L'.') continue;
        wchar_t sub[MAX_PATH], subRel[MAX_PATH];
        if (swprintf_s(sub, L"%s\\%s", dir, fd.cFileName) < 0 || swprintf_s(subRel, L"%s%s%s", rel, rel[0] ? L"\\" : L"", fd.cFileName) < 0) continue;
        if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) { if (depth < 6 && !(fd.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT)) MdtWalk(sub, subRel, depth + 1); }
        else {
            const size_t l = wcslen(fd.cFileName);
            if (l > 8 && !_wcsicmp(fd.cFileName + l - 8, L".package")) MdtScanFile(sub, subRel);
        }
    } while (FindNextFileW(f, &fd));
    FindClose(f);
}
static DWORD WINAPI MdtScan(LPVOID) {
    SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_LOWEST);
    g_mdtPkg = (MdtPkg*)calloc(4096, sizeof(MdtPkg));
    if (g_mdtPkg) {
        wchar_t user[MAX_PATH], mods[MAX_PATH];
        UserSims3Dir(user);
        if (swprintf_s(mods, L"%s\\Mods", user) > 0) MdtWalk(mods, L"", 0);  // the game's own folder first: its match wins
        wchar_t* cut = wcsrchr(user, L'\\');  // <Documents>\Electronic Arts: other language / copy folders
        if (cut) {
            *cut = 0;
            wchar_t pat[MAX_PATH];
            WIN32_FIND_DATAW fd;
            HANDLE f = swprintf_s(pat, L"%s\\*", user) > 0 ? FindFirstFileW(pat, &fd) : INVALID_HANDLE_VALUE;
            if (f != INVALID_HANDLE_VALUE) {
                do {
                    if (fd.cFileName[0] == L'.' || !(fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) || !_wcsicmp(fd.cFileName, L"The Sims 3")) continue;
                    wchar_t other[MAX_PATH], rel[MAX_PATH];
                    if (swprintf_s(other, L"%s\\%s\\Mods", user, fd.cFileName) > 0 && swprintf_s(rel, L"(%s)", fd.cFileName) > 0 &&
                        GetFileAttributesW(other) != INVALID_FILE_ATTRIBUTES)
                        MdtWalk(other, rel, 0);
                } while (FindNextFileW(f, &fd));
                FindClose(f);
            }
        }
    }
    Log("modtime: package scan: %d packages, %d script resources, %d assemblies with an MVID", g_mdtPkgFiles, g_mdtPkgS3sa, g_mdtPkgN);
    g_mdtScan = 2;
    return 0;
}
static void MdtMatchPackages() {  // caller holds g_mdtCs
    if (g_mdtScan != 2 || !g_mdtPkg) return;
    for (int i = 2; i < g_mdtModN; i++) {
        MdtMod& m = g_mdtMod[i];
        if (m.pkg[0] || !m.mvidOk) continue;
        for (int j = 0; j < g_mdtPkgN; j++)
            if (!memcmp(g_mdtPkg[j].mvid, m.mvid, 16)) {  // a game-named assembly found in Mods = a core mod that replaces the game DLL
                _snprintf_s(m.pkg, _TRUNCATE, "%s%s", m.kind == MDT_GAME ? "core mod: " : "", g_mdtPkg[j].pkg);
                break;
            }
        if (!m.pkg[0]) strcpy_s(m.pkg, m.kind == MDT_GAME ? "(game files)" : "(not found in Mods)");
    }
}

// ---- files ----
static void MdtPut(HANDLE f, const char* fmt, ...) {
    char b[1536];
    va_list ap;
    va_start(ap, fmt);
    int n = _vsnprintf_s(b, sizeof(b) - 2, _TRUNCATE, fmt, ap);
    va_end(ap);
    if (n < 0) n = (int)strlen(b);
    DWORD w;
    for (int i = 0; i < n; i++) { if (b[i] == '\n') WriteFile(f, "\r", 1, &w, NULL); WriteFile(f, b + i, 1, &w, NULL); }
    WriteFile(f, "\r\n", 2, &w, NULL);
}
static void MdtOrder(const double* w, int n, int* idx, int k, int* got) {  // the k largest, largest first
    *got = 0;
    for (int r = 0; r < k; r++) {
        int best = -1;
        for (int i = 0; i < n; i++) {
            bool used = false;
            for (int j = 0; j < *got; j++) used |= idx[j] == i;
            if (!used && w[i] > 0 && (best < 0 || w[i] > w[best])) best = i;
        }
        if (best < 0) return;
        idx[(*got)++] = best;
    }
}
static double MdtPct(double a, double total) { return total > 0 ? a * 100 / total : 0; }

static CRITICAL_SECTION g_mdtWr;  // one writer at a time (the sampler thread only today; kept for a later caller)
static void MdtWriteSummaryLocked(bool hourEnd, LONG hour);
static void MdtWriteSummary(bool hourEnd, LONG hour) {
    EnterCriticalSection(&g_mdtWr);
    MdtWriteSummaryLocked(hourEnd, hour);
    LeaveCriticalSection(&g_mdtWr);
}
static void MdtWriteSummaryLocked(bool hourEnd, LONG hour) {
    if (!g_mdtScan && InterlockedCompareExchange(&g_mdtScan, 1, 0) == 0) {
        HANDLE t = CreateThread(NULL, 256 * 1024, MdtScan, NULL, STACK_SIZE_PARAM_IS_A_RESERVATION, NULL);
        if (t) CloseHandle(t);
        else g_mdtScan = 2;
    }
    static MdtMod mods[512];
    static MdtMeth* meths;
    static double w[8192];
    if (!meths) meths = (MdtMeth*)malloc(sizeof(MdtMeth) * 8192);
    if (!meths) return;
    EnterCriticalSection(&g_mdtCs);
    MdtMatchPackages();
    if (hourEnd && g_mdtHourTotal > 0) {  // docs/302 §4.4 notice rule, per ended sim hour (info only)
        g_mdtHoursDone++;
        for (int i = 2; i < g_mdtModN; i++) {
            MdtMod& m = g_mdtMod[i];
            const double share = m.hourW / g_mdtHourTotal;
            m.hotRun = MdtHotStep(m.hotRun, share);
            if (m.hotRun > m.hotMax) m.hotMax = m.hotRun;
            if (m.hotRun && share > m.hotPeak) m.hotPeak = share;
        }
    }
    const int nm = g_mdtModN, nf = g_mdtMethN;
    memcpy(mods, g_mdtMod, sizeof(MdtMod) * nm);
    memcpy(meths, g_mdtMeth, sizeof(MdtMeth) * nf);
    const double total = g_mdtTotalW, gcTotal = g_mdtGcW, hourTotal = g_mdtHourTotal, loadTotal = g_mdtLoadTotalW;
    const LONG samples = g_mdtSamples, loadSamples = g_mdtLoadSamples, active = g_mdtActive, bad = g_mdtFramesBad, hitN = g_mdtHitN, hours = g_mdtHoursDone;
    MdtHitch hits[20];
    memcpy(hits, g_mdtHit, sizeof(hits));
    if (hourEnd) { for (int i = 0; i < g_mdtModN; i++) g_mdtMod[i].hourW = 0; g_mdtHourTotal = 0; }
    LeaveCriticalSection(&g_mdtCs);
    const double realS = (MdtUs() - g_mdtStartUs) / 1e6, liveS = g_mdtLiveWallUs / 1e6, busy = MdtBusyPct();
    const LONGLONG ovUs = g_mdtOverheadUs;
    // CSV: top 30 owners of the hour that just ended (inclusive)
    if (hourEnd && hourTotal > 0) {
        HANDLE c = CreateFileW(g_mdtCsv, FILE_APPEND_DATA, FILE_SHARE_READ, NULL, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
        if (c != INVALID_HANDLE_VALUE) {
            LARGE_INTEGER sz = {};
            GetFileSizeEx(c, &sz);
            if (!sz.QuadPart) MdtPut(c, "session,sim_hour,rank,name,kind,package,pct_of_sim_cpu");
            for (int i = 0; i < nm; i++) w[i] = mods[i].hourW;
            int idx[30], got;
            MdtOrder(w, nm, idx, 30, &got);
            for (int r = 0; r < got; r++)
                MdtPut(c, "%lu,%ld,%d,%s,%s,\"%s\",%.2f", g_mdtSession, hour, r + 1, mods[idx[r]].name, kMdtKind[mods[idx[r]].kind], mods[idx[r]].pkg, w[idx[r]] * 100 / hourTotal);
            CloseHandle(c);
        }
    }
    // the mods (kind mod) by inclusive share: the report's top 10 and the notice's top 3
    for (int i = 0; i < nm; i++) w[i] = mods[i].kind == MDT_MOD ? mods[i].w : 0;
    int top[10], gotMods;
    MdtOrder(w, nm, top, 10, &gotMods);
    if (hourEnd && hours >= 2 && !g_mdtNoteReady) {  // the session's notice text, frozen once written
        const char* names[3];
        double self[3], incl[3];
        const int nn = gotMods < 3 ? gotMods : 3;
        for (int i = 0; i < nn; i++) names[i] = mods[top[i]].name, self[i] = MdtPct(mods[top[i]].selfW, total), incl[i] = MdtPct(mods[top[i]].w, total);
        MdtNoteText(busy, names, self, incl, nn, g_mdtNote, sizeof(g_mdtNote));
        MemoryBarrier();
        g_mdtNoteReady = 1;
        Log("modtime: notice text ready: %s", g_mdtNote);
    }
    HANDLE f = CreateFileW(g_mdtSum, GENERIC_WRITE, FILE_SHARE_READ, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (f == INVALID_HANDLE_VALUE) return;
    MdtPut(f, "ts3native %s mod cost report ([Diag] ModTime, docs/298). Read-only: nothing in the game was changed.", TS3NATIVE_VERSION);
    MdtPut(f, "Shares are of the sim thread's CPU time (the thread that runs every script mod). 'own' = time in the mod's own code;");
    MdtPut(f, "'with game code' = also the game (EA) code the mod called. A mod is named here, not judged: a large share can be the");
    MdtPut(f, "work the mod is meant to do.");
    MdtPut(f, "");
    MdtPut(f, "Your sim thread was busy %.0f%% of the time while a world was live (%.1f real min).%s", busy, liveS / 60,
          busy >= 90 ? " At 90% or more the simulation cannot keep up: Sims stand idle and actions wait." : "");
    MdtPut(f, "");
    MdtPut(f, "Top 10 script mods (this session, share of sim-thread CPU):");
    MdtPut(f, "%4s %6s %10s %8s  %-40s %s", "rank", "own%", "with game%", "hitches", "name", "package");
    for (int r = 0; r < gotMods; r++) {
        const MdtMod& m = mods[top[r]];
        MdtPut(f, "%4d %5.1f%% %9.1f%% %8ld  %-40s %s%s", r + 1, MdtPct(m.selfW, total), MdtPct(m.w, total), m.hitTop, m.name, m.pkg[0] ? m.pkg : "-",
              m.pkg[0] && MdtCompatRow(m.pkg) ? "  (docs/COMPATIBILITY.md has a row for this package)" : "");
    }
    if (!gotMods) MdtPut(f, "   No script mod showed up in the samples yet.");
    MdtPut(f, "'hitches' = sim iterations of 250 ms or more where this mod had the most samples (of %ld such iterations).", g_mdtHitAll);
    for (int i = 2; i < nm; i++)
        if (mods[i].kind == MDT_OURS)
            MdtPut(f, "%s (ours, ts3fixes): own %.1f%%, with the game code it calls %.1f%%, hitches topped %ld", mods[i].name, MdtPct(mods[i].selfW, total),
                  MdtPct(mods[i].w, total), mods[i].hitTop);
    {
        char line[1024] = "";
        for (int i = 2; i < nm; i++)
            if (mods[i].hotMax >= 2 && mods[i].kind != MDT_GAME) {
                char one[160];
                _snprintf_s(one, _TRUNCATE, "%s%s (%ld sim hours in a row, peak %.0f%%)", line[0] ? ", " : "", mods[i].name, mods[i].hotMax, mods[i].hotPeak * 100);
                strncat_s(line, one, _TRUNCATE);
            }
        MdtPut(f, "Notice rule (docs/302 §4.4, information only): >= 25%% of sim CPU for 2 sim hours in a row: %s", line[0] ? line : "none");
    }
    MdtPut(f, "");
    MdtPut(f, "Load / menu phase (before the world went live, and after a world quit): %.1f real s covered, sim thread on the CPU %.1f s, %ld samples.",
          g_mdtLoadWallUs / 1e6, g_mdtLoadCpu100 / 1e7, loadSamples);
    MdtPut(f, "Top 5 mods by share of load-phase sim CPU (own / with game code):");
    for (int i = 0; i < nm; i++) w[i] = mods[i].kind == MDT_MOD ? mods[i].loadW : 0;
    int lt[5], lg;
    MdtOrder(w, nm, lt, 5, &lg);
    for (int r = 0; r < lg; r++)
        MdtPut(f, "%4d %5.1f%% / %5.1f%%  %-40s %s", r + 1, MdtPct(mods[lt[r]].loadSelfW, loadTotal), MdtPct(mods[lt[r]].loadW, loadTotal), mods[lt[r]].name,
              mods[lt[r]].pkg[0] ? mods[lt[r]].pkg : "-");
    if (!lg) MdtPut(f, "   No script mod showed up in the load-phase samples.");
    MdtPut(f, "");
    MdtPut(f, "Details. Live: %.0f real s sampled since start, %ld samples at %ld Hz, %ld with the sim thread on the CPU, %ld unreadable stacks;"
              " package scan %s (%d packages, %d script resources)",
          realS, samples, g_mdtHz, active, bad, g_mdtScan == 2 ? "done" : "running", g_mdtPkgFiles, g_mdtPkgS3sa);
    MdtPut(f, "overhead (measured): the sim thread was stopped %.1f us per sample on average, %.2f ms per real second = %.3f%% of one core",
          samples + loadSamples ? (double)ovUs / (samples + loadSamples) : 0, realS > 0 ? ovUs / 1000.0 / realS : 0, realS > 0 ? ovUs / 1e4 / realS : 0);
    MdtPut(f, "gc/alloc (Boehm collector code on the CPU) %.1f%% of sim-thread CPU", MdtPct(gcTotal, total));
    MdtPut(f, "");
    MdtPut(f, "Top 20 owners incl. engine, game and ours (own = innermost frame's assembly; with game = innermost mod frame, else ours, else game)");
    MdtPut(f, "%4s %6s %10s %6s %8s  %-40s %-6s %s", "rank", "own%", "with game%", "gc%", "samples", "name", "kind", "package");
    for (int i = 0; i < nm; i++) w[i] = mods[i].w > mods[i].selfW ? mods[i].w : mods[i].selfW;
    int all[20], got;
    MdtOrder(w, nm, all, 20, &got);
    for (int r = 0; r < got; r++) {
        const MdtMod& m = mods[all[r]];
        MdtPut(f, "%4d %5.1f%% %9.1f%% %5.1f%% %8ld  %-40s %-6s %s", r + 1, MdtPct(m.selfW, total), MdtPct(m.w, total), m.w > 0 ? m.gcW * 100 / m.w : 0, m.n, m.name,
              kMdtKind[m.kind], m.pkg[0] ? m.pkg : "-");
    }
    MdtPut(f, "");
    for (int r = 0; r < gotMods && r < 5; r++) {
        MdtPut(f, "Top 10 methods of %s (with game code %.1f%%):", mods[top[r]].name, MdtPct(mods[top[r]].w, total));
        for (int i = 0; i < nf; i++) w[i] = meths[i].mod == top[r] ? meths[i].w : 0;
        int mt[10], mg;
        MdtOrder(w, nf, mt, 10, &mg);
        for (int j = 0; j < mg; j++) MdtPut(f, "   %6.2f%%  %s%s", w[mt[j]] * 100 / total, meths[mt[j]].name, meths[mt[j]].gcW > 0 ? " (part gc/alloc)" : "");
    }
    MdtPut(f, "Top 10 ts3fixes methods by own time (our own cost check):");
    for (int i = 0; i < nf; i++) w[i] = mods[meths[i].mod].kind == MDT_OURS ? meths[i].selfW : 0;
    int ot[10], og;
    MdtOrder(w, nf, ot, 10, &og);
    for (int j = 0; j < og; j++) MdtPut(f, "   %6.2f%%  %s", w[ot[j]] * 100 / total, meths[ot[j]].name);
    MdtPut(f, "Top 10 game methods (EA code that no mod called on the stack):");
    for (int i = 0; i < nf; i++) w[i] = mods[meths[i].mod].kind == MDT_GAME ? meths[i].w : 0;
    int gt[10], gg;
    MdtOrder(w, nf, gt, 10, &gg);
    for (int j = 0; j < gg; j++) MdtPut(f, "   %6.2f%%  %s: %s", w[gt[j]] * 100 / total, mods[meths[gt[j]].mod].name, meths[gt[j]].name);
    MdtPut(f, "");
    MdtPut(f, "Worst %ld sim iterations >= 250 ms (hitches), with the 3 most sampled owner: method in each:", hitN);
    for (int i = 0; i < hitN; i++)
        MdtPut(f, "%2d. %6ld ms  sim hour %ld  %ld samples (%ld on the CPU, %ld gc/alloc): %s (%ld)%s%s%s", i + 1, hits[i].ms, hits[i].hour, hits[i].n, hits[i].active, hits[i].gc,
              hits[i].top[0], hits[i].topN[0], hits[i].topN[1] ? "; " : "", hits[i].topN[1] ? hits[i].top[1] : "", hits[i].topN[2] ? "; ..." : "");
    CloseHandle(f);
    if (hourEnd)
        Log("modtime: summary written (sim hour %ld, %ld samples, %ld load-phase samples, busy %.0f%%, overhead %.3f%% of a core)", hour, samples, loadSamples, busy,
            realS > 0 ? ovUs / 1e4 / realS : 0);
}

// userdiag.cpp case 61: hi 0 = on?, 1 = sim hour lo starts (the previous one is written), 2 = world quit (summary),
// 3 = the notice text is ready?, 4 = bit lo of the notice text (byte lo >> 3, bit lo & 7; 0 past the end)
static u8 MdtOp(DWORD lo, DWORD hi) {
    if (!g_mdtOn) return 0;
    switch (hi & 0xFFFF) {
        case 0: return 1;
        case 1:  // the first call opens hour lo (nothing to close yet); the sampler thread writes (no file work on the sim thread)
            if (g_mdtHour >= 0) { g_mdtReqHour = g_mdtHour; InterlockedOr(&g_mdtReq, 1); }
            g_mdtHour = (LONG)lo;
            return 1;
        case 2: g_mdtHour = -1; InterlockedOr(&g_mdtReq, 2); return 1;
        case 3: return g_mdtNoteReady ? 1 : 0;
        case 4: return g_mdtNoteReady && lo < sizeof(g_mdtNote) * 8 ? (u8)((g_mdtNote[lo >> 3] >> (lo & 7)) & 1) : 0;
    }
    return 0;
}

static bool ApplyModTime(const wchar_t* ini) {
    if (!GetPrivateProfileIntW(L"Diag", L"ModTime", 0, ini)) return false;
    if (!g_udOn) { Log("SKIP modtime ([Diag] ModTime): needs [UserDiag] Enabled=1 (the ts3fixes channel)"); return false; }
    // Steam, retail and EA 1.69: the TLS slot, the GC range ends and the Simulate loop site have rows in every table; SimLoopAdd
    // checks the site's bytes and stands down on a mismatch
    LONG hz = (LONG)GetPrivateProfileIntW(L"Diag", L"ModTimeHz", 10, ini);
    g_mdtHz = hz < 10 ? 10 : hz > 200 ? 200 : hz;
    wcscpy_s(g_mdtSum, ini);
    wchar_t* slash = wcsrchr(g_mdtSum, L'\\');
    if (!slash) return false;
    slash[1] = 0;
    wcscpy_s(g_mdtCsv, g_mdtSum);
    wcscat_s(g_mdtSum, L"modtime-summary.txt");
    wcscat_s(g_mdtCsv, L"modtime.csv");
    SYSTEMTIME st;
    GetLocalTime(&st);
    g_mdtSession = (DWORD)st.wMonth * 1000000 + st.wDay * 10000 + st.wHour * 100 + st.wMinute;
    MdtTablesInit();
    InitializeCriticalSection(&g_mdtWr);
    const bool iter = SimLoopAdd(MdtIter, "modtime: sim thread + iteration ends");
    HANDLE t = iter ? CreateThread(NULL, 64 * 1024, MdtSampler, NULL, STACK_SIZE_PARAM_IS_A_RESERVATION, NULL) : NULL;
    if (t) CloseHandle(t);
    g_mdtOn = t ? 1 : 0;
    Ts3nCounters& k = ts3native_counters;
    k.mdtOn = g_mdtOn, k.mdtHz = g_mdtHz;
    Log("%s modtime ([Diag] ModTime=1, docs/298): %ld Hz sampler of the sim thread's script stack, modtime-summary.txt + modtime.csv next to this log%s",
        t ? "OK  " : "FAIL", g_mdtHz, iter ? "" : " (no Simulate iteration hook)");
    return g_mdtOn != 0;
}
