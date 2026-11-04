// [UserDiag] (docs/188): lines in ts3native.log that answer the first triage questions of a player report without asking
// the player. Logging only: no game behaviour changes. Included by ts3native.cpp after every feature file.
//   1. Session header: versions, exe edition and install class, ini switches, Bin add-ons (ASI / proxy DLLs), mods, saves,
//      CPU / RAM / OS / GPU / display, disks. DllMain part is cheap (no file I/O beyond the ini); the rest runs on a
//      low-priority worker 15 s after start (mods scan, hashes, GPU), never on the sim thread.
//   2. World load: ts3fixes sends its own lines (switches, world size, script assemblies) over the GetOption2 text channel
//      (UdTextMsg); native adds loaded modules (first load), d3d9 origin, memory and a one-line header repeat.
//   3. Save trail: "save: start" at the save entry (jobs, queues, lock owner, memory), "save: > phase" at every phase start,
//      "save: < phase N ms" at its end, "save: wait ... took N ms" for long waits, a watchdog thread that writes
//      "save: still running ..." every 5 s while one save runs, and "save: end" with the save folder size.
// Privacy: file names only; every path-derived string goes through crashreport.cpp's scrubber (CrScrub).
#include <psapi.h>
#pragma comment(lib, "advapi32.lib")
#pragma comment(lib, "user32.lib")

static bool g_udOn;
static DWORD g_udT0;  // GetTickCount at start (uptime)
static wchar_t g_udIni[MAX_PATH];
static void MdSaveStart(bool travelSave);       // memdiag.cpp ([MemDiag], docs/190 phase 1)
static void ApplyMemDiag(const wchar_t* ini);  // memdiag.cpp
static void CpSaveEnd(DWORD r);      // compaction.cpp ([Compaction], docs/204)
static void CpWorldLoad();
static void CpAfterSample(DWORD bigMB);
static u8 CpOp(DWORD lo);

// ---------------------------------------------------------------------------------------------------------------------
// pure helpers (selftest)

// Packs comma-separated items into log lines under the 512-byte Log() buffer.
struct UdList { const char* head; char b[440]; size_t n; int items, lines; };
static void UdListBegin(UdList& l, const char* head) { l.head = head; l.n = 0; l.b[0] = 0; l.items = l.lines = 0; }
static void UdListFlush(UdList& l) {
    if (!l.n) return;
    Log("%s %s", l.head, l.b);
    l.n = 0, l.b[0] = 0, l.lines++;
}
static void UdListAdd(UdList& l, const char* item) {
    size_t m = strlen(item);
    if (m > sizeof(l.b) - 4) m = sizeof(l.b) - 4;
    if (l.n && l.n + 2 + m >= sizeof(l.b)) UdListFlush(l);
    if (l.n) l.b[l.n++] = ',', l.b[l.n++] = ' ';
    memcpy(l.b + l.n, item, m);
    l.n += m;
    l.b[l.n] = 0;
    l.items++;
}

// Install class from the exe path (lower-case compare). Classes only: the path itself is never logged.
enum { UD_STEAM, UD_EAAPP, UD_ORIGIN, UD_RETAIL, UD_OTHER };
static int UdInstallClass(const wchar_t* exe) {
    wchar_t p[MAX_PATH];
    wcsncpy_s(p, exe, _TRUNCATE);
    _wcslwr_s(p);
    if (wcsstr(p, L"\\steamapps\\common\\")) return UD_STEAM;
    if (wcsstr(p, L"\\ea games\\")) return UD_EAAPP;          // EA app default: <Program Files>\EA Games\The Sims 3
    if (wcsstr(p, L"\\origin games\\")) return UD_ORIGIN;     // Origin default: <Program Files>\Origin Games\The Sims 3
    if (wcsstr(p, L"\\electronic arts\\")) return UD_RETAIL;  // disc / old Origin default: ...\Electronic Arts\The Sims 3
    return UD_OTHER;
}
static const char* const kUdClass[] = {"Steam library", "EA app", "Origin", "Electronic Arts folder (disc or old Origin)", "other folder"};

// DBPF 2.x: entries in the index of type `type`; -1 = not a DBPF 2 package or the index is not in idx. hdr = first 96
// bytes, idx = the index bytes (from the index position, idxN = its size). Index: flags DWORD, then the constant fields
// (bit 0 type, bit 1 group, bit 2 instance high), then per entry the non-constant ones of type, group, instance high,
// plus instance low, offset, file size, mem size, compressed + committed (16-bit each) = 32 bytes minus 4 per constant.
static int UdDbpfCount(const u8* hdr, size_t hdrN, const u8* idx, size_t idxN, DWORD type) {
    if (hdrN < 96 || memcmp(hdr, "DBPF", 4) || *(const DWORD*)(hdr + 4) != 2 || idxN < 4) return -1;
    const DWORD count = *(const DWORD*)(hdr + 0x24), flags = *(const DWORD*)idx;
    size_t at = 4;
    DWORD constType = 0;
    if (flags & 1) { if (idxN < at + 4) return -1; constType = *(const DWORD*)(idx + at); at += 4; }
    if (flags & 2) at += 4;
    if (flags & 4) at += 4;
    const size_t entry = 32 - 4 * ((flags & 1) + ((flags >> 1) & 1) + ((flags >> 2) & 1));
    if (at > idxN || (idxN - at) / entry < count) return -1;
    if (flags & 1) return constType == type ? (int)count : 0;
    int n = 0;
    for (DWORD i = 0; i < count; i++, at += entry) n += *(const DWORD*)(idx + at) == type;
    return n;
}
static const DWORD kUdS3SA = 0x073FAA07;  // script assembly resource

// "PackedFile <pattern>" lines of a Resource.cfg (patterns are relative: no user path); returns their count.
static int UdCfgPatterns(const char* txt, size_t n, UdList& l) {
    int k = 0;
    for (size_t i = 0; i < n;) {
        size_t e = i;
        while (e < n && txt[e] != '\n' && txt[e] != '\r') e++;
        const char* s = txt + i;
        size_t m = e - i;
        while (m && (*s == ' ' || *s == '\t')) s++, m--;
        if (m > 11 && !_strnicmp(s, "PackedFile", 10) && (s[10] == ' ' || s[10] == '\t')) {
            char item[96];
            size_t c = m - 11 < sizeof(item) - 1 ? m - 11 : sizeof(item) - 1;
            memcpy(item, s + 11, c);
            item[c] = 0;
            k++;
            UdListAdd(l, item);
        }
        i = e + 1;
    }
    return k;
}

// ---------------------------------------------------------------------------------------------------------------------
// file and system facts

// File version from the RT_VERSION resource of a loaded module (exe) or a data-file mapping. "" when none.
static void UdModVersion(HMODULE m, char* out, size_t cap) {
    out[0] = 0;
    HRSRC r = m ? FindResourceW(m, MAKEINTRESOURCEW(1), MAKEINTRESOURCEW(16)) : NULL;
    HGLOBAL g = r ? LoadResource(m, r) : NULL;
    const u8* p = g ? (const u8*)LockResource(g) : NULL;
    const DWORD n = r ? SizeofResource(m, r) : 0;
    for (DWORD i = 0; p && i + 52 <= n; i += 4)
        if (*(const DWORD*)(p + i) == 0xFEEF04BD) {
            const VS_FIXEDFILEINFO* f = (const VS_FIXEDFILEINFO*)(p + i);
            sprintf_s(out, cap, "%lu.%lu.%lu.%lu", f->dwFileVersionMS >> 16, f->dwFileVersionMS & 0xFFFF, f->dwFileVersionLS >> 16,
                      f->dwFileVersionLS & 0xFFFF);
            return;
        }
}
static void UdFileVersion(const wchar_t* path, char* out, size_t cap) {  // worker only (maps the file)
    HMODULE m = LoadLibraryExW(path, NULL, LOAD_LIBRARY_AS_DATAFILE | LOAD_LIBRARY_AS_IMAGE_RESOURCE);
    UdModVersion(m, out, cap);
    if (m) FreeLibrary(m);
}
static u64 UdFileSize(const wchar_t* path) {
    WIN32_FILE_ATTRIBUTE_DATA a;
    if (!GetFileAttributesExW(path, GetFileExInfoStandard, &a)) return ~0ull;
    return ((u64)a.nFileSizeHigh << 32) | a.nFileSizeLow;
}
static bool UdFileHas(const wchar_t* path, const char* needle) {  // worker only: whole-file byte search
    HANDLE h = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, NULL, OPEN_EXISTING, 0, NULL);
    if (h == INVALID_HANDLE_VALUE) return false;
    static char buf[65536 + 16];
    const size_t m = strlen(needle);
    size_t keep = 0;
    DWORD r;
    bool hit = false;
    while (!hit && ReadFile(h, buf + keep, 65536, &r, NULL) && r) {
        const size_t n = keep + r;
        for (size_t i = 0; i + m <= n && !hit; i++) hit = !memcmp(buf + i, needle, m);
        keep = n < m ? n : m - 1;
        memmove(buf, buf + n - keep, keep);
    }
    CloseHandle(h);
    return hit;
}

static char g_udSum[200];  // one-line summary repeated at each world load (worker fills it)
static volatile LONG g_udScanDone;

// Address space (docs/188 §6): one VirtualQuery walk, on the memory worker (UdMemMain) or at exit, never per frame.
// "save: memory at start #3: private 1851 MB, committed 2167 MB; limit 4 GB (...), free 1646 MB, largest free 1525 MB
// (2nd 80), session low 1301 MB; modules 310 MB, mapped files 120 MB, heaps and other private 2010 MB; walk 410 us, 9123 regions"
struct UdMemStat {
    DWORD regions;
    u64 freeB, big1, big2, image, mapped, priv, commit;  // image/mapped/priv = reserved + committed by region type
    // [MemDiag] (memdiag.cpp, docs/190 phase 1): MEM_PRIVATE committed, and private allocations grouped by AllocationBase:
    // thread stacks (a PAGE_GUARD region in the allocation) and 64 / 128 MiB reserves (S3SS mimalloc arenas [inferred owner])
    u64 privCommit, stkRes, stkCom, miRes, miCom;
    const void* gBase;  // allocation being summed (regions of one allocation are contiguous in the walk)
    u64 gRes, gCom;
    bool gPriv, gGuard;
    uintptr_t ownLo, ownLen;  // [MemDiag] self-test view: MEM_MAPPED regions starting in [ownLo, ownLo + ownLen) -> ownMapped
    u64 ownMapped;
};
static bool AsIsDriverRegion(DWORD base);  // allocsites.cpp: reserved by a GPU driver (only with [MemDiag] AllocSites on)
static void UdMemGroupEnd(UdMemStat& s) {  // classify the allocation summed so far
    if (s.gPriv && s.gGuard) s.stkRes += s.gRes, s.stkCom += s.gCom;
    else if (s.gPriv && (s.gRes == 64ull << 20 || s.gRes == 128ull << 20) && !AsIsDriverRegion((DWORD)(uintptr_t)s.gBase))
        s.miRes += s.gRes, s.miCom += s.gCom;
    s.gBase = NULL, s.gRes = s.gCom = 0, s.gPriv = s.gGuard = false;
}
static void UdMemAdd(UdMemStat& s, const MEMORY_BASIC_INFORMATION& m) {
    s.regions++;
    if (m.State == MEM_FREE || m.AllocationBase != s.gBase) UdMemGroupEnd(s);
    if (m.State != MEM_FREE) {
        s.gBase = m.AllocationBase;
        s.gRes += m.RegionSize;
        if (m.State == MEM_COMMIT) s.gCom += m.RegionSize;
        if (m.State == MEM_COMMIT && m.Type == MEM_PRIVATE) s.privCommit += m.RegionSize;
        s.gPriv = m.Type == MEM_PRIVATE;
        s.gGuard = s.gGuard || (m.State == MEM_COMMIT && (m.Protect & PAGE_GUARD));
    }
    if (m.State == MEM_FREE) {
        s.freeB += m.RegionSize;
        if (m.RegionSize > s.big1) s.big2 = s.big1, s.big1 = m.RegionSize;
        else if (m.RegionSize > s.big2) s.big2 = m.RegionSize;
        return;
    }
    if (m.State == MEM_COMMIT) s.commit += m.RegionSize;
    (m.Type == MEM_IMAGE ? s.image : m.Type == MEM_MAPPED ? s.mapped : s.priv) += m.RegionSize;
    if (m.Type == MEM_MAPPED && (uintptr_t)m.BaseAddress - s.ownLo < s.ownLen) s.ownMapped += m.RegionSize;
}
static const DWORD kUdLowBigMB = 256;  // largest free block under this: Error 12 / crash-on-save territory (docs/66)
static const char* UdLimitText(u64 top) {  // top = highest user address + 1
    return top > (3ull << 30) ? "4 GB (large address aware, 64-bit Windows)" : top > (2ull << 30) ? "3 GB (large address aware, /3GB)"
                                                                               : "2 GB (NOT large address aware)";
}
static void UdMemFormat(char* out, size_t cap, const char* what, const UdMemStat& s, u64 top, u64 privUsage, DWORD lowMB, LONG walkUs) {
    sprintf_s(out, cap,
              "%s: private %lu MB, committed %lu MB; limit %s, free %lu MB, largest free %lu MB (2nd %lu), session low %lu MB; modules %lu MB, "
              "mapped files %lu MB, heaps and other private %lu MB; walk %ld us, %lu regions",
              what, (DWORD)(privUsage >> 20), (DWORD)(s.commit >> 20), UdLimitText(top), (DWORD)(s.freeB >> 20), (DWORD)(s.big1 >> 20),
              (DWORD)(s.big2 >> 20), lowMB, (DWORD)(s.image >> 20), (DWORD)(s.mapped >> 20), (DWORD)(s.priv >> 20), walkUs, s.regions);
}
static bool UdMemLow(char* out, size_t cap, DWORD bigMB) {  // the warning line, false = no warning
    if (bigMB >= kUdLowBigMB) return false;
    sprintf_s(out, cap, "diag: low address space: largest free block %lu MB (under %lu) - risk of Error 12 / crash on save", bigMB, kUdLowBigMB);
    return true;
}
static volatile DWORD g_udLowBigMB = 0xFFFFFFFF;  // session low of the largest free block
static LONG UdMemWalk(UdMemStat& s, u64* top, const void* ownLo = NULL, size_t ownLen = 0) {  // one VirtualQuery walk; returns its cost in us
    LARGE_INTEGER t0;
    QueryPerformanceCounter(&t0);
    SYSTEM_INFO si;
    GetSystemInfo(&si);
    s = {};
    s.ownLo = (uintptr_t)ownLo, s.ownLen = ownLen;
    MEMORY_BASIC_INFORMATION m;
    for (uintptr_t a = (uintptr_t)si.lpMinimumApplicationAddress; a < (uintptr_t)si.lpMaximumApplicationAddress;) {
        if (!VirtualQuery((void*)a, &m, sizeof(m))) break;
        UdMemAdd(s, m);
        const uintptr_t next = (uintptr_t)m.BaseAddress + m.RegionSize;
        if (next <= a) break;
        a = next;
    }
    UdMemGroupEnd(s);
    if (top) *top = (u64)(uintptr_t)si.lpMaximumApplicationAddress + 1;
    return UsSince(t0);
}
static u64 UdPrivateUsage() {
    PROCESS_MEMORY_COUNTERS_EX pc = {sizeof(pc)};
    K32GetProcessMemoryInfo(GetCurrentProcess(), (PROCESS_MEMORY_COUNTERS*)&pc, sizeof(pc));
    return pc.PrivateUsage;
}
static void MdAfterSample(const char* what, const char* ev, const UdMemStat& s, u64 privUsage);  // memdiag.cpp
static void UdMemSample(const char* what, const char* ev = NULL) {
    UdMemStat s;
    u64 top;
    const LONG us = UdMemWalk(s, &top);
    const u64 privUsage = UdPrivateUsage();
    const DWORD big = (DWORD)(s.big1 >> 20);
    if (big < g_udLowBigMB) g_udLowBigMB = big;
    char line[480];
    UdMemFormat(line, sizeof(line), what, s, top, privUsage, g_udLowBigMB, us);
    Log("%s", line);
    if (UdMemLow(line, sizeof(line), big)) Log("%s", line);
    LuAsk();  // [RouteSnap] LotUseReadout: the replay consumer walks the clone now
    MdAfterSample(what, ev, s, privUsage);  // [MemDiag]: the "diag: mem" line right after (no-op when off)
    CpAfterSample(big);  // [Compaction]: re-arm above threshold + rearm, the "+N min" line
}

// Memory worker: samples asked for by the sim thread (world load, save start / end) plus one every 10 min of play (5 min
// with [MemDiag] on or Light, the release default since 0.36.79).
static HANDLE g_udMemEv;
static char g_udMemQ[8][48], g_udMemQEv[8][40];  // what, [MemDiag] event tag ("" = none)
static volatile LONG g_udMemHead, g_udMemTail;  // single producer (sim thread), single consumer (worker)
static DWORD g_udMemPeriodMs = 10 * 60 * 1000;
static LONG g_udWorlds;
static void UdMemAsk(const char* fmt, LONG n, const char* ev = NULL) {
    char what[48];
    sprintf_s(what, fmt, n);
    if (!g_udMemEv) { UdMemSample(what, ev); return; }  // no worker (CreateThread failed, selftest): inline
    if (g_udMemHead - g_udMemTail >= 8) return;  // shortcut: a full queue drops the sample; 8 asks within one walk do not happen
    strcpy_s(g_udMemQ[g_udMemHead & 7], what);
    strncpy_s(g_udMemQEv[g_udMemHead & 7], ev ? ev : "", _TRUNCATE);
    InterlockedIncrement(&g_udMemHead);
    SetEvent(g_udMemEv);
}
static DWORD MdWorkerPoll();  // memdiag.cpp: runs a pending [MemDiag] self-test; ms until it wants the worker again
static DWORD WINAPI UdMemMain(LPVOID) {
    DWORD last = GetTickCount();
    for (;;) {
        const DWORD since = GetTickCount() - last, md = MdWorkerPoll();
        const DWORD wait = since >= g_udMemPeriodMs ? 0 : g_udMemPeriodMs - since;
        WaitForSingleObject(g_udMemEv, wait < md ? wait : md);
        for (; g_udMemTail != g_udMemHead; InterlockedIncrement(&g_udMemTail))
            UdMemSample(g_udMemQ[g_udMemTail & 7], g_udMemQEv[g_udMemTail & 7][0] ? g_udMemQEv[g_udMemTail & 7] : NULL);
        if (GetTickCount() - last >= g_udMemPeriodMs) {
            last = GetTickCount();
            char what[48];
            sprintf_s(what, "diag: memory at %lu min", (last - g_udT0) / 60000);
            if (g_udWorlds) UdMemSample(what);  // play only: nothing before the first world load
        }
    }
}

static void UdScrubbed(const wchar_t* w, char* out, size_t cap) {  // UTF-8, user folders replaced
    char raw[MAX_PATH * 3];
    CrUtf8(w, raw, sizeof(raw));
    CrScrub(raw, strlen(raw), out, cap);
}

static void UdBinDir(wchar_t* out) {
    GetModuleFileNameW(NULL, out, MAX_PATH);
    if (wchar_t* s = wcsrchr(out, L'\\')) *s = 0;
}

// ---------------------------------------------------------------------------------------------------------------------
// header parts (worker)

static void UdIniLines() {
    static wchar_t names[8192], sec[16384];
    const DWORD n = GetPrivateProfileSectionNamesW(names, 8192, g_udIni);
    UdList l;
    UdListBegin(l, "diag: ini (enabled sections, all keys):");
    int sections = 0, on = 0;
    for (const wchar_t* s = names; n && *s; s += wcslen(s) + 1) {
        sections++;
        if (GetPrivateProfileIntW(s, L"Enabled", 0, g_udIni) <= 0) continue;
        on++;
        char item[128];
        CrUtf8(s, item + 1, sizeof(item) - 2);
        item[0] = '[';
        strcat_s(item, "]");
        UdListAdd(l, item);
        const DWORD m = GetPrivateProfileSectionW(s, sec, 16384, g_udIni);
        for (const wchar_t* kv = sec; m && *kv; kv += wcslen(kv) + 1) {
            if (*kv == L';' || !_wcsnicmp(kv, L"Enabled=", 8)) continue;
            char a[160];
            UdScrubbed(kv, a, sizeof(a));
            UdListAdd(l, a);
        }
    }
    UdListFlush(l);
    Log("diag: ini %d sections, %d enabled (the rest are off; compare with the release ts3native.ini)", sections, on);
}

static void UdBinLines(const wchar_t* bin) {
    UdList l;
    UdListBegin(l, "diag: Bin add-ons:");
    wchar_t pat[MAX_PATH], p[MAX_PATH];
    WIN32_FIND_DATAW f;
    swprintf_s(pat, L"%s\\*", bin);
    HANDLE h = FindFirstFileExW(pat, FindExInfoBasic, &f, FindExSearchNameMatch, NULL, 0);
    // ASI plugins and the DLL names the ASI loaders and wrappers use (the game ships none of these in Bin [inferred])
    static const wchar_t* const kProxies[] = {L"d3d9.dll", L"dinput8.dll", L"dsound.dll", L"version.dll", L"winmm.dll", L"wininet.dll",
                                              L"dxgi.dll", L"d3d11.dll", L"winhttp.dll", L"xinput1_3.dll", L"vorbisFile.dll"};
    for (BOOL ok = h != INVALID_HANDLE_VALUE; ok; ok = FindNextFileW(h, &f)) {
        if (f.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) continue;
        const wchar_t* ext = wcsrchr(f.cFileName, L'.');
        bool want = ext && (!_wcsicmp(ext, L".asi") || !_wcsicmp(ext, L".orig"));
        for (const wchar_t* x : kProxies) want = want || !_wcsicmp(f.cFileName, x);
        if (!want) continue;
        swprintf_s(p, L"%s\\%s", bin, f.cFileName);
        char name[MAX_PATH * 3], ver[40], item[MAX_PATH * 3 + 96];
        CrUtf8(f.cFileName, name, sizeof(name));
        UdFileVersion(p, ver, sizeof(ver));
        const u64 sz = ((u64)f.nFileSizeHigh << 32) | f.nFileSizeLow;
        const bool dxvk = !_wcsicmp(f.cFileName, L"d3d9.dll") && UdFileHas(p, "DXVK");
        sprintf_s(item, "%s %llu KB%s%s%s", name, sz >> 10, ver[0] ? " v" : "", ver, dxvk ? " (DXVK)" : "");
        UdListAdd(l, item);
    }
    if (h != INVALID_HANDLE_VALUE) FindClose(h);
    if (!l.items) UdListAdd(l, "none");
    UdListFlush(l);
}

static const DWORD kUdIdxCap = 1 << 21;  // index read cap 2 MB (~65k entries); a bigger index counts as no script
struct UdScan { int files, script, deep; u64 bytes; u8* idx; UdList names, scripts; };
static void UdScanPackages(const wchar_t* dir, int depth, UdScan& s) {
    wchar_t pat[MAX_PATH], p[MAX_PATH];
    if (swprintf_s(pat, L"%s\\*", dir) < 0) return;
    WIN32_FIND_DATAW f;
    HANDLE h = FindFirstFileExW(pat, FindExInfoBasic, &f, FindExSearchNameMatch, NULL, FIND_FIRST_EX_LARGE_FETCH);
    if (h == INVALID_HANDLE_VALUE) return;
    static u8 hdr[96];
    do {
        if (f.cFileName[0] == L'.') continue;
        if (swprintf_s(p, L"%s\\%s", dir, f.cFileName) < 0) continue;
        if (f.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) {
            if (depth >= 5) s.deep++;  // the game reads Packages\*\* only as deep as Resource.cfg says [community]
            else UdScanPackages(p, depth + 1, s);
            continue;
        }
        const wchar_t* ext = wcsrchr(f.cFileName, L'.');
        if (!ext || _wcsicmp(ext, L".package")) continue;
        s.files++;
        s.bytes += ((u64)f.nFileSizeHigh << 32) | f.nFileSizeLow;
        char name[MAX_PATH * 3];
        CrUtf8(f.cFileName, name, sizeof(name));
        if (s.files <= 200) UdListAdd(s.names, name);
        HANDLE fh = CreateFileW(p, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL, OPEN_EXISTING, FILE_FLAG_SEQUENTIAL_SCAN, NULL);
        if (fh == INVALID_HANDLE_VALUE) continue;
        DWORD r = 0;
        if (ReadFile(fh, hdr, sizeof(hdr), &r, NULL) && r == sizeof(hdr) && !memcmp(hdr, "DBPF", 4)) {
            const DWORD pos = *(const DWORD*)(hdr + 0x40), size = *(const DWORD*)(hdr + 0x2C);
            LARGE_INTEGER at;
            at.QuadPart = pos;
            DWORD got = 0;
            if (s.idx && size && size <= kUdIdxCap && SetFilePointerEx(fh, at, NULL, FILE_BEGIN) && ReadFile(fh, s.idx, size, &got, NULL) &&
                UdDbpfCount(hdr, sizeof(hdr), s.idx, got, kUdS3SA) > 0) {
                s.script++;
                if (s.script <= 100) UdListAdd(s.scripts, name);
            }
        }
        CloseHandle(fh);
    } while (FindNextFileW(h, &f));
    FindClose(h);
}

static u64 UdTreeBytes(const wchar_t* dir, int depth, int* files) {
    wchar_t pat[MAX_PATH], p[MAX_PATH];
    WIN32_FIND_DATAW f;
    u64 b = 0;
    if (swprintf_s(pat, L"%s\\*", dir) < 0) return 0;
    HANDLE h = FindFirstFileExW(pat, FindExInfoBasic, &f, FindExSearchNameMatch, NULL, FIND_FIRST_EX_LARGE_FETCH);
    if (h == INVALID_HANDLE_VALUE) return 0;
    do {
        if (f.cFileName[0] == L'.') continue;
        if (f.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) {
            if (depth < 6 && swprintf_s(p, L"%s\\%s", dir, f.cFileName) > 0) b += UdTreeBytes(p, depth + 1, files);
        } else {
            b += ((u64)f.nFileSizeHigh << 32) | f.nFileSizeLow;
            if (files) (*files)++;
        }
    } while (FindNextFileW(h, &f));
    FindClose(h);
    return b;
}

static void UdSavesLine(const wchar_t* user) {
    wchar_t dir[MAX_PATH], pat[MAX_PATH], p[MAX_PATH];
    swprintf_s(dir, L"%s\\Saves", user);
    swprintf_s(pat, L"%s\\*", dir);
    WIN32_FIND_DATAW f;
    int saves = 0, backups = 0, files = 0;
    u64 bytes = 0, biggest = 0;
    HANDLE h = FindFirstFileExW(pat, FindExInfoBasic, &f, FindExSearchNameMatch, NULL, 0);
    for (BOOL ok = h != INVALID_HANDLE_VALUE; ok; ok = FindNextFileW(h, &f)) {
        if (f.cFileName[0] == L'.' || !(f.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY)) continue;
        const wchar_t* ext = wcsrchr(f.cFileName, L'.');
        if (ext && !_wcsicmp(ext, L".sims3")) saves++;
        else if (ext && !_wcsicmp(ext, L".backup")) backups++;
        if (swprintf_s(p, L"%s\\%s", dir, f.cFileName) < 0) continue;
        const u64 b = UdTreeBytes(p, 0, &files);
        bytes += b;
        if (ext && !_wcsicmp(ext, L".sims3") && b > biggest) biggest = b;
    }
    if (h != INVALID_HANDLE_VALUE) FindClose(h);
    Log("diag: saves %d (+%d .backup), %d files, %llu MB in Saves, biggest save %llu MB", saves, backups, files, bytes >> 20, biggest >> 20);
}

static void UdModsLines(const wchar_t* user) {
    wchar_t mods[MAX_PATH], p[MAX_PATH];
    swprintf_s(mods, L"%s\\Mods", user);
    swprintf_s(p, L"%s\\Resource.cfg", mods);
    HANDLE h = CreateFileW(p, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL, OPEN_EXISTING, 0, NULL);
    char cfg[8192];
    DWORD r = 0;
    if (h != INVALID_HANDLE_VALUE) { ReadFile(h, cfg, sizeof(cfg) - 1, &r, NULL); CloseHandle(h); }
    cfg[r] = 0;
    if (h == INVALID_HANDLE_VALUE) {
        Log("diag: Mods\\Resource.cfg missing: the game loads no mods from Mods\\Packages");
    } else {
        u64 hash = 0xCBF29CE484222325ull;
        for (DWORD i = 0; i < r; i++) hash = Fnv(hash, (u8)cfg[i]);
        UdList l;
        UdListBegin(l, "diag: Mods\\Resource.cfg");
        char head[64];
        sprintf_s(head, "%lu bytes, hash %08lx, PackedFile:", r, (DWORD)hash);
        UdListAdd(l, head);
        if (!UdCfgPatterns(cfg, r, l)) UdListAdd(l, "none");
        UdListFlush(l);
    }
    UdScan s = {};
    UdListBegin(s.names, "diag: packages (first 200 names):");
    UdListBegin(s.scripts, "diag: script mods (S3SA packages):");
    swprintf_s(p, L"%s\\Packages", mods);
    LARGE_INTEGER t0;
    QueryPerformanceCounter(&t0);
    s.idx = (u8*)VirtualAlloc(NULL, kUdIdxCap, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);  // freed after: no address space kept
    UdScanPackages(p, 0, s);
    if (s.idx) VirtualFree(s.idx, 0, MEM_RELEASE);
    // shortcut: *.package directly in Mods (not loaded by the usual Resource.cfg) and Overrides are not counted
    Log("diag: Mods\\Packages %d .package files, %llu MB, %d with scripts (S3SA), %d other (CC, tuning), %d folders deeper than 5 not read; scan %ld ms",
        s.files, s.bytes >> 20, s.script, s.files - s.script, s.deep, UsSince(t0) / 1000);
    UdListFlush(s.scripts);
    UdListFlush(s.names);
    if (s.files > 200) Log("diag: packages: %d more names not listed", s.files - 200);
    sprintf_s(g_udSum, "mods %d packages (%d script) %llu MB", s.files, s.script, s.bytes >> 20);
}

static void UdPacksLine() {  // HKLM\SOFTWARE\Sims (a 32-bit process reads WOW6432Node): one key per installed pack [inferred]
    HKEY k;
    UdList l;
    UdListBegin(l, "diag: registry packs:");
    if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, L"SOFTWARE\\Sims", 0, KEY_READ, &k) == ERROR_SUCCESS) {
        wchar_t name[256];
        for (DWORD i = 0;; i++) {
            DWORD n = 256;
            if (RegEnumKeyExW(k, i, name, &n, NULL, NULL, NULL, NULL) != ERROR_SUCCESS) break;
            char a[256 * 3];
            CrUtf8(!_wcsnicmp(name, L"The Sims 3 ", 11) ? name + 11 : name, a, sizeof(a));
            UdListAdd(l, a);
        }
        RegCloseKey(k);
    }
    if (!l.items) UdListAdd(l, "none found");
    char c[24];
    sprintf_s(c, "(%d keys)", l.items);
    UdListAdd(l, c);
    UdListFlush(l);
}

// GPU: every display adapter (name, driver version, dedicated VRAM) from the display-class registry keys Windows keeps;
// no D3D / DXGI calls in the game process. Display: current mode of the primary monitor.
static void UdGpuLines() {
    DISPLAY_DEVICEW d = {sizeof(d)};
    char seen[4][128] = {};
    int ns = 0;
    for (DWORD i = 0; ns < 4 && EnumDisplayDevicesW(NULL, i, &d, 0); i++) {
        char name[128];
        CrUtf8(d.DeviceString, name, sizeof(name));
        bool dup = false;
        for (int k = 0; k < ns; k++) dup = dup || !strcmp(seen[k], name);
        if (dup) continue;
        strcpy_s(seen[ns++], name);
        char drv[64] = "?";
        DWORD vramMB = 0;
        // DeviceKey: \Registry\Machine\System\CurrentControlSet\Control\Video\{GUID}\0000
        const wchar_t* sub = wcsstr(d.DeviceKey, L"\\System\\");
        if (!sub) sub = wcsstr(d.DeviceKey, L"\\SYSTEM\\");
        HKEY k;
        if (sub && RegOpenKeyExW(HKEY_LOCAL_MACHINE, sub + 1, 0, KEY_READ, &k) == ERROR_SUCCESS) {
            wchar_t v[64];
            DWORD sz = sizeof(v);
            if (RegQueryValueExW(k, L"DriverVersion", NULL, NULL, (BYTE*)v, &sz) == ERROR_SUCCESS) CrUtf8(v, drv, sizeof(drv));
            u64 q = 0;
            sz = sizeof(q);
            if (RegQueryValueExW(k, L"HardwareInformation.qwMemorySize", NULL, NULL, (BYTE*)&q, &sz) == ERROR_SUCCESS) vramMB = (DWORD)(q >> 20);
            else {
                DWORD dw = 0;
                sz = sizeof(dw);
                if (RegQueryValueExW(k, L"HardwareInformation.MemorySize", NULL, NULL, (BYTE*)&dw, &sz) == ERROR_SUCCESS) vramMB = dw >> 20;
            }
            RegCloseKey(k);
        }
        Log("diag: gpu %s, driver %s, VRAM %lu MB%s", name, drv, vramMB, d.StateFlags & DISPLAY_DEVICE_PRIMARY_DEVICE ? " (primary display)" : "");
    }
    DEVMODEW m = {};
    m.dmSize = sizeof(m);
    if (EnumDisplaySettingsW(NULL, ENUM_CURRENT_SETTINGS, &m))
        Log("diag: display %lux%lu @ %lu Hz, %d monitors", m.dmPelsWidth, m.dmPelsHeight, m.dmDisplayFrequency, GetSystemMetrics(SM_CMONITORS));
}

static DWORD UdFreeGB(const wchar_t* path) {
    ULARGE_INTEGER f;
    return GetDiskFreeSpaceExW(path, &f, NULL, NULL) ? (DWORD)(f.QuadPart >> 30) : 0xFFFFFFFF;
}

static DWORD WINAPI UdHeaderMain(LPVOID) {
    SetThreadPriority(GetCurrentThread(), THREAD_MODE_BACKGROUND_BEGIN);  // low CPU and I/O priority
    Sleep(15000);  // after the ASI loader's other plugins and out of the game's first start-up seconds
    LARGE_INTEGER t0;
    QueryPerformanceCounter(&t0);
    wchar_t bin[MAX_PATH], exe[MAX_PATH], p[MAX_PATH];
    UdBinDir(bin);
    GetModuleFileNameW(NULL, exe, MAX_PATH);
    char sha[65] = "?";
    CrFileSha(exe, sha);
    sha[12] = 0;
    swprintf_s(p, L"%s.orig", exe);
    const u64 orig = UdFileSize(p);
    swprintf_s(p, L"%s\\steam_api.dll", bin);
    char sv[40] = "";
    const u64 steamApi = UdFileSize(p);
    if (steamApi != ~0ull) UdFileVersion(p, sv, sizeof(sv));
    Log("diag: exe TS3W.exe %llu bytes, sha256 %s..., TS3W.exe.orig %s, steam_api.dll %s%s", UdFileSize(exe), sha,
        orig == ~0ull ? "absent" : "present", steamApi == ~0ull ? "absent" : "present v", sv);
    UdIniLines();
    UdBinLines(bin);
    UdPacksLine();
    UdGpuLines();
    wchar_t user[MAX_PATH];
    UserSims3Dir(user);
    wchar_t lower[MAX_PATH], prof[MAX_PATH] = L"";
    wcscpy_s(lower, user);
    _wcslwr_s(lower);
    GetEnvironmentVariableW(L"USERPROFILE", prof, MAX_PATH);
    _wcslwr_s(prof);
    const bool userOk = GetFileAttributesW(user) != INVALID_FILE_ATTRIBUTES;
    Log("diag: Documents %s%s, TS3 user folder %s; free disk: game drive %lu GB, Documents drive %lu GB",
        wcsstr(lower, L"\\onedrive") ? "in OneDrive (synced: saves can lock or roll back)" : prof[0] && wcsstr(lower, prof) == lower ? "in the user profile" : "outside the user profile",
        wcsncmp(lower, L"\\\\", 2) ? "" : " (network path)", userOk ? "found" : "NOT found (localized name or moved: mods and saves not listed)",
        UdFreeGB(bin), userOk ? UdFreeGB(user) : 0xFFFFFFFF);
    if (userOk) {
        UdModsLines(user);
        UdSavesLine(user);
    }
    Log("diag: header done in %ld ms", UsSince(t0) / 1000);
    InterlockedExchange(&g_udScanDone, 1);
    SetThreadPriority(GetCurrentThread(), THREAD_MODE_BACKGROUND_END);
    return 0;
}

// ---------------------------------------------------------------------------------------------------------------------
// world load (ts3fixes text channel) and modules

static void UdModulesLines() {
    HMODULE mods[512];
    DWORD need = 0;
    if (!K32EnumProcessModules(GetCurrentProcess(), mods, sizeof(mods), &need)) return;
    wchar_t win[MAX_PATH], bin[MAX_PATH], p[MAX_PATH];
    GetWindowsDirectoryW(win, MAX_PATH);
    UdBinDir(bin);
    const size_t wn = wcslen(win), bn = wcslen(bin);
    UdList l;
    UdListBegin(l, "diag: loaded modules outside Windows (Bin = game folder):");
    int other = 0;
    for (DWORD i = 0; i < need / sizeof(HMODULE) && i < 512; i++) {
        if (!GetModuleFileNameW(mods[i], p, MAX_PATH) || !_wcsnicmp(p, win, wn)) continue;
        const bool inBin = !_wcsnicmp(p, bin, bn) && p[bn] == L'\\';
        const wchar_t* name = wcsrchr(p, L'\\');
        name = name ? name + 1 : p;
        if (inBin && !_wcsicmp(name, L"TS3W.exe")) continue;
        char a[MAX_PATH * 3], ver[40], item[MAX_PATH * 3 + 64];
        CrUtf8(name, a, sizeof(a));
        UdModVersion(mods[i], ver, sizeof(ver));
        sprintf_s(item, "%s%s%s%s", a, ver[0] ? " v" : "", ver, inBin ? "" : " (outside Bin)");
        if (!inBin) other++;
        UdListAdd(l, item);
    }
    UdListFlush(l);
    HMODULE d9 = GetModuleHandleW(L"d3d9.dll");
    const char* d9from = "not loaded";
    if (d9 && GetModuleFileNameW(d9, p, MAX_PATH)) d9from = !_wcsnicmp(p, bin, bn) ? "Bin (wrapper, e.g. DXVK)" : !_wcsnicmp(p, win, wn) ? "Windows (system d3d9)" : "other folder";
    Log("diag: d3d9.dll from %s; %d modules outside Windows and Bin (overlays, injectors)", d9from, other);
}

static const char* MdWorldEvent(const char* worldLine);  // memdiag.cpp: "travel,world-switch" / "world-switch" / NULL
static char g_udTxt[480];  // the ts3fixes text line being received (UdTextMsg below)
static void UdWorldLoad() {  // sim thread, from the ts3fixes "world:" line
    const LONG n = InterlockedIncrement(&g_udWorlds);
    Log("diag: world load #%ld, uptime %lu min, ts3native " TS3NATIVE_VERSION ", %s", n, (GetTickCount() - g_udT0) / 60000,
        g_udScanDone ? g_udSum : "mods scan still running");
    UdMemAsk("diag: memory at world load #%ld", n, MdWorldEvent(g_udTxt));
    if (n == 1) UdModulesLines();
    CpWorldLoad();
}

// ts3fixes text channel (UserDiag.cs): Route.GetOption2 with optHi = 0x87000000 | op << 16, 4 ASCII bytes in optLo.
// op 0 = begin a line, 1 = append (NUL pads), 2 = end: log it as "ts3fixes: <line>". Answer 1 = accepted; with [UserDiag]
// off 0, so ts3fixes stops at the first message. Without ts3native the game answers false (no RouteOption2 name there).
// Ops 3-5 memdiag.cpp; op 6 and "played: " lines played.cpp (docs/14 section 8.6); op 7 compaction.cpp (docs/204).
static const DWORD kUdTextHi = 0x87000000;  // 0x86000000 exact = the stuck-check PROBE mark (docs/163 §10)
static int g_udTxtN;
static LONG g_udTxtLines;
static void MdMark(const char* tag);  // memdiag.cpp
static void MsLine(const char* line);  // memdiag.cpp: [MemDiag] suspect counters from ts3fixes
static u8 MsOp(int op);                // memdiag.cpp: ops 3-5 (on / sample wanted / self-test wanted)
static u8 UdTextMsg(DWORD lo, DWORD hi) {
    if (!g_udOn) return 0;
    switch ((hi >> 16) & 0xFF) {
        case 3: case 4: case 5: return MsOp((hi >> 16) & 0xFF);
        case 6: return PlAnswer();  // played.cpp: store ready / household played (docs/14 section 8.6)
        case 7: return CpOp(lo);    // compaction.cpp: [Compaction] verdict and steps (docs/204)
        case 0: g_udTxtN = 0; return 1;
        case 1:
            for (int i = 0; i < 4; i++) {
                const char c = (char)(lo >> (8 * i));
                if (!c) break;
                if (g_udTxtN < (int)sizeof(g_udTxt) - 1) g_udTxt[g_udTxtN++] = c >= 0x20 && c <= 0x7E ? c : '?';
            }
            return 1;
        case 2:
            g_udTxt[g_udTxtN] = 0;
            if (!strncmp(g_udTxt, "memsus", 6)) { g_udTxtN = 0; MsLine(g_udTxt); return 1; }  // own lines and cap ([MemDiag] module 2)
            if (!strncmp(g_udTxt, "played: ", 8)) { g_udTxtN = 0; PlLine(g_udTxt + 8); return 1; }  // played.cpp logs what it does
            if (!strncmp(g_udTxt, "savecleanup: ", 13)) {  // per-action save cleanup lines (docs/14 section 8.6): own, larger cap
                static LONG s_scLines;
                if (s_scLines++ < 50000) Log("ts3fixes: %s", g_udTxt);
                g_udTxtN = 0;
                return 1;
            }
            if (g_udTxtLines++ < 400) Log("ts3fixes: %s", g_udTxt);  // cap: a looping sender cannot fill the disk
            if (!strncmp(g_udTxt, "world:", 6)) UdWorldLoad();
            else if (!strncmp(g_udTxt, "mem: ", 5)) MdMark(g_udTxt + 5);  // [MemDiag] event mark (ts3agent mem_mark)
            g_udTxtN = 0;
            return 1;
    }
    return 0;
}

// ---------------------------------------------------------------------------------------------------------------------
// save trail (savewrite.cpp, rtsites.cpp, discourage.cpp call these; all no-ops without a save on this thread)

static const char* volatile g_udPhase;
static const char* volatile g_udWaitWhat;
static volatile DWORD g_udPhaseTick, g_udWaitTick, g_udSaveTick, g_udSaveTid;
static HANDLE g_udDog, g_udDogEv;
static LONG g_udSaves;
static wchar_t g_udSaveDir[MAX_PATH];  // <Saves>\<name>.sims3 of the running save (from the save info), "" = unknown
static DWORD g_udDogMs = 5000;  // watchdog period (selftest: shorter)
static const DWORD kUdLongWaitMs = 1000;

static bool UdOnSaveThread() { return g_udOn && g_udSaveTick && GetCurrentThreadId() == g_udSaveTid; }

static void UdPhase(const char* p) {
    if (!UdOnSaveThread()) return;
    const DWORD now = GetTickCount();
    if (g_udPhase) Log("save: < %s %lu ms", g_udPhase, now - g_udPhaseTick);
    g_udWaitWhat = NULL;
    g_udPhaseTick = now;
    g_udPhase = p;
    if (p) Log("save: > %s (+%lu ms)", p, now - g_udSaveTick);
}

static void UdWait(const char* what) {  // what = NULL: the wait ended
    if (!UdOnSaveThread()) return;
    const DWORD now = GetTickCount();
    if (!what && g_udWaitWhat && now - g_udWaitTick >= kUdLongWaitMs) Log("save: wait for %s took %lu ms", g_udWaitWhat, now - g_udWaitTick);
    g_udWaitTick = now;
    g_udWaitWhat = what;
}

static void UdSaveDir(const wchar_t* dir) { if (UdOnSaveThread()) wcscpy_s(g_udSaveDir, dir); }

static int UdJobs(int state) {
    int n = 0;
    for (int i = 0; i < kJobs; i++) n += g_jobs[i].state == state;
    return n;
}

// "route job running (handle 0x25000012) / none, queued 2, parked 0, deferred footprints 3, +0x918 owner tid 1234 (the sim) / free"
static void UdRouteState(char* out, size_t cap) {
    const LONG owner = ts3native_counters.rgOwnerTid;
    char o[48];
    if (!owner) strcpy_s(o, "free");
    else sprintf_s(o, "tid %ld%s", owner, (DWORD)owner == g_async.owner ? " (sim)" : (DWORD)owner == g_workerTid ? " (route worker)" : "");
    char job[48] = "none";
    if (g_async.state == A_RUNNING) sprintf_s(job, "running (handle 0x%08lX)", g_async.handle);
    sprintf_s(out, cap, "route job %s, queued %d, parked %d, deferred footprints %d, +0x918 %s, gates open %ld", job, UdJobs(J_QUEUED),
              UdJobs(J_PEND), g_discCount, o, g_rgOffSimOpen);
}

static DWORD WINAPI UdDogMain(LPVOID) {
    for (;;) {
        if (WaitForSingleObject(g_udDogEv, g_udDogMs) != WAIT_TIMEOUT) break;
        const DWORD now = GetTickCount();
        const char* p = g_udPhase;
        const char* w = g_udWaitWhat;
        char wt[96] = "", rs[200];
        if (w) sprintf_s(wt, ", waiting for %s for %lu s", w, (now - g_udWaitTick) / 1000);
        UdRouteState(rs, sizeof(rs));
        Log("save: still running after %lu s: phase %s for %lu s%s; %s; backup worker %s %ld files %ld MB", (now - g_udSaveTick) / 1000,
            p ? p : "?", (now - g_udPhaseTick) / 1000, wt, rs, g_sjActive ? "copying," : "idle,", g_sjFiles, g_sjKb >> 10);
    }
    if (g_udSaveDir[0]) {  // after the save: its folder size, off the sim thread
        int files = 0;
        const u64 b = UdTreeBytes(g_udSaveDir, 0, &files);
        Log("save: folder now %d files, %llu MB", files, b >> 20);
    }
    return 0;
}

static void UdSaveStart(int travelSave, int traveling) {
    if (!g_udOn) return;
    if (g_udDog) {  // the previous save's watchdog (it only sized a folder after that save)
        WaitForSingleObject(g_udDog, 2000);
        CloseHandle(g_udDog);
        g_udDog = NULL;
    }
    g_udSaveTid = GetCurrentThreadId();
    g_udSaveTick = GetTickCount();
    g_udPhase = g_udWaitWhat = NULL;
    g_udSaveDir[0] = 0;
    char rs[200];
    UdRouteState(rs, sizeof(rs));
    const LONG n = ++g_udSaves;
    // trigger: the native save cannot tell a manual save from NRaas Saver; travel flags come from the call
    Log("save: start #%ld, %s, uptime %lu min, save mode %ld, flush mode %ld (previous flush %s)", n,
        travelSave & 0xFF ? "travel save" : traveling & 0xFF ? "traveling" : "normal save", (g_udSaveTick - g_udT0) / 60000,
        ts3native_counters.saveMode, ts3native_counters.flushMode, g_fj.thread ? "still running" : "done");
    Log("save: at start %s", rs);
    UdMemAsk("save: memory at start #%ld", n, travelSave & 0xFF ? "travel-save" : traveling & 0xFF ? "traveling" : NULL);
    MdSaveStart(travelSave & 0xFF);
    if (!g_udDogEv) g_udDogEv = CreateEventW(NULL, TRUE, FALSE, NULL);
    if (g_udDogEv) {
        ResetEvent(g_udDogEv);
        g_udDog = CreateThread(NULL, 64 * 1024, UdDogMain, NULL, STACK_SIZE_PARAM_IS_A_RESERVATION, NULL);
    }
    UdPhase("prepare");
}

static void UdSaveEnd(DWORD r) {
    if (!UdOnSaveThread()) return;
    UdPhase(NULL);
    Log("save: end #%ld result %lu after %lu ms", g_udSaves, r, GetTickCount() - g_udSaveTick);
    UdMemAsk("save: memory at end #%ld", g_udSaves);
    CpSaveEnd(r);
    g_udSaveTick = 0;
    if (g_udDogEv) SetEvent(g_udDogEv);  // the watchdog then sizes the save folder and ends
}

// Save_WritePackage 0xAAC110 (thiscall) calls the native pre-save listeners 0xAABB80 at 0xAAC2F3, World_Save 0xC6D460 at
// 0xAAC320, the post-save listeners 0xAABBC0 at 0xAAC34A [verified disassembly 1.67.2: each listener loop is thiscall with
// one stack argument, ret 4, returns al; a false result ends the write with 0xB / 0xD]. Ours only mark the phases.
static const DWORD UD_PRE_SITE = 0x00AAC2F3, UD_PRE = 0x00AABB80, UD_POST_SITE = 0x00AAC34A, UD_POST = 0x00AABBC0;
typedef DWORD(__fastcall* UdListenFn)(void* self, void* edx, void* arg);  // ret 4
static UdListenFn g_udPreOrig = (UdListenFn)(uintptr_t)UD_PRE, g_udPostOrig = (UdListenFn)(uintptr_t)UD_POST;
static DWORD __fastcall UdPreHook(void* self, void*, void* arg) {
    UdPhase("write: pre-save listeners");
    const DWORD r = g_udPreOrig(self, NULL, arg);
    UdPhase((u8)r ? "write: world save (lots, script state)" : "write: pre-save listeners FAILED, write ends");
    return r;
}
static DWORD __fastcall UdPostHook(void* self, void*, void* arg) {
    UdPhase("write: post-save listeners");
    const DWORD r = g_udPostOrig(self, NULL, arg);
    UdPhase((u8)r ? "write: package commit" : "write: post-save listeners FAILED, write ends");
    return r;
}
static const CallSite kUdSites[] = {{UD_PRE_SITE, UD_PRE, (void*)UdPreHook}, {UD_POST_SITE, UD_POST, (void*)UdPostHook}};

// ---------------------------------------------------------------------------------------------------------------------

// DllMain part: cheap facts only (no file reads but the ini). Returns true when on (the log stays open).
static bool ApplyUserDiag(const wchar_t* ini) {
    g_udT0 = GetTickCount();
    if (!GetPrivateProfileIntW(L"UserDiag", L"Enabled", 1, ini)) { Log("user diag off (Enabled=0)"); return false; }
    g_udOn = true;
    wcscpy_s(g_udIni, ini);
    wchar_t exe[MAX_PATH];
    GetModuleFileNameW(NULL, exe, MAX_PATH);
    if (!g_crExe[0]) wcscpy_s(g_crExe, exe);  // scrubber needles (crashreport.cpp fills them only with [CrashReport] on)
    if (!g_crUser[0]) UserSims3Dir(g_crUser);
    CrInitNeedles();
    if (!g_crSys[0]) CrSysInit();
    char ver[40];
    UdModVersion(GetModuleHandleW(NULL), ver, sizeof(ver));
    const int cls = UdInstallClass(exe);
    const bool steam = cls == UD_STEAM;
    Log("diag: ts3native " TS3NATIVE_VERSION " (built " __DATE__ "), send this whole file (ts3native.log next to TS3W.exe) with a report");
    Log("diag: edition %s, TS3W.exe file version %s, install class %s%s", steam ? "Steam" : "not Steam", ver[0] ? ver : "?", kUdClass[cls],
        steam ? "" : " - UNSUPPORTED, ts3native supports the Steam TS3W.exe only (other exes skip patches whose bytes differ)");
    char sys[512];
    // g_crSys = "cpu: ...\r\nram: ...\r\nos: ...\r\nlarge address aware: ...\r\n": one log line each
    char* ctx2 = NULL;
    strcpy_s(sys, g_crSys);
    for (char* t = strtok_s(sys, "\r\n", &ctx2); t; t = strtok_s(NULL, "\r\n", &ctx2)) Log("diag: %s", t);
    if (ts3native_counters.saveReady) PatchCalls("user diag save phases", kUdSites, 2);
    else Log("user diag: save phase lines need [SaveWrite] Enabled=1 (the save entry hooks); header and world lines still on");
    HANDLE t = CreateThread(NULL, 256 * 1024, UdHeaderMain, NULL, STACK_SIZE_PARAM_IS_A_RESERVATION, NULL);
    if (t) CloseHandle(t);
    ApplyMemDiag(ini);  // its "diag: mem" lines ride on these samples; first: it sets the worker's period
    g_udMemEv = CreateEventW(NULL, FALSE, FALSE, NULL);
    t = g_udMemEv ? CreateThread(NULL, 64 * 1024, UdMemMain, NULL, STACK_SIZE_PARAM_IS_A_RESERVATION, NULL) : NULL;
    if (t) CloseHandle(t);
    else if (g_udMemEv) CloseHandle(g_udMemEv), g_udMemEv = NULL;  // no worker: samples run inline
    return true;
}

static void UdExit() {  // DLL_PROCESS_DETACH (normal quit)
    if (!g_udOn) return;
    Log("diag: exit after %lu min, %ld saves, %ld world loads", (GetTickCount() - g_udT0) / 60000, g_udSaves, g_udWorlds);
    UdMemSample("diag: memory at exit");  // inline: the worker is gone (ExitProcess ends other threads before DLL_PROCESS_DETACH)
}

static void UdInfoDir(const unsigned char* info) {  // SaveInfoHook: <Saves>\<name>.sims3 from info+0x430 (as SaveJobPaths)
    if (!UdOnSaveThread() || !info) return;
    const wchar_t* nhd = (const wchar_t*)(info + kInfoNhdPath);
    if (!Readable(nhd, 520 * sizeof(wchar_t)) || wcsnlen(nhd, 520) >= MAX_PATH || !EndsWithI(nhd, L".nhd")) return;
    wchar_t d[MAX_PATH];
    wcscpy_s(d, nhd);
    CutLast(d);
    if (EndsWithI(d, L".sims3")) UdSaveDir(d);
}
