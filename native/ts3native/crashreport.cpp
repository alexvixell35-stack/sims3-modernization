// [CrashReport] (docs/135): opt-in local crash report for players. Included by ts3native.cpp (one translation unit).
// An unhandled-exception filter writes <Documents>\Electronic Arts\The Sims 3\ts3native-crashes\crash-<UTC>-<pid>.txt:
// version, exe SHA-256, enabled ini sections, fault module+offset, registers, a module+offset stack, the last 50
// ts3native.log lines, package file names, CPU/RAM/OS. User paths, the user name and the machine name are scrubbed.
// A running marker (running.txt) left behind by a session that never reached DLL_PROCESS_DETACH and wrote no report
// turns into a "hard" report on the next start. Nothing is sent from here: tools/report-crash.ts shows the report and
// sends it only after the player confirms.
// Chaining: TS3W.exe sets its own filter later (0x4D71BA sets 0x4D7140, the CRT at 0xF7F5F2) through the IAT slot of
// SetUnhandledExceptionFilter (0xF9517C) [verified: import table + call sites]. That slot is swapped so ours stays the
// top filter and every later filter becomes the one ours calls next; ours always returns the next filter's verdict, or
// EXCEPTION_CONTINUE_SEARCH when there is none (never swallows a crash).
#include <psapi.h>
#include <initializer_list>
#pragma comment(lib, "advapi32.lib")  // RegGetValueW; TS3W.exe already imports ADVAPI32, so no new DLL loads

static bool g_crOn;
static wchar_t g_crDir[MAX_PATH];   // ...\ts3native-crashes (selftest: a temp folder)
static wchar_t g_crUser[MAX_PATH];  // <Documents>\Electronic Arts\The Sims 3 ("" = not found: no package list)
static wchar_t g_crLog[MAX_PATH], g_crExe[MAX_PATH];
static char g_crSections[1024];     // "[GC] [WaterJps] ..." read at start
static char g_crSys[512];           // cpu / ram / os lines, read at start
static LPTOP_LEVEL_EXCEPTION_FILTER volatile g_crPrev;  // the filter ours calls next
static volatile LONG g_crBusy;      // one report per process
static DWORD* g_crIatSlot;          // exe IAT slot of SetUnhandledExceptionFilter (swapped)
static void* g_crIatReal;
static char g_crPrevTail[32768];    // last lines of the previous session's ts3native.log (read before it is truncated)
static size_t g_crPrevTailN;
static char g_crBuf[196608], g_crOut[229376];  // report text before / after the scrubber (static: the heap may be broken)
static size_t g_crLen;
static u8 g_crIo[65536];

static void CrPut(const char* fmt, ...) {
    va_list ap;
    va_start(ap, fmt);
    const int n = _vsnprintf_s(g_crBuf + g_crLen, sizeof(g_crBuf) - g_crLen, _TRUNCATE, fmt, ap);
    va_end(ap);
    g_crLen = n < 0 ? sizeof(g_crBuf) - 1 : g_crLen + n;
}

static void CrUtf8(const wchar_t* w, char* out, int cap) {
    if (!WideCharToMultiByte(CP_UTF8, 0, w, -1, out, cap, NULL, NULL)) out[0] = 0;
}

// --- scrubber: user paths, user name and machine name never leave the PC ---
struct CrNeedle { char from[MAX_PATH * 3]; const char* to; size_t n; };
static CrNeedle g_crNeedle[8];
static int g_crNeedleN;

// Longest first, so "<Documents>" wins over "<home>". Names shorter than minLen are skipped (a 2-letter user name would
// eat the report); they still go wherever they sit inside a \Users\ path (rule below).
static void CrAddNeedle(const wchar_t* w, const char* to, size_t minLen) {
    if (!w || !*w || g_crNeedleN >= (int)(sizeof(g_crNeedle) / sizeof(g_crNeedle[0]))) return;
    CrNeedle x;
    CrUtf8(w, x.from, sizeof(x.from));
    x.n = strlen(x.from);
    while (x.n && (x.from[x.n - 1] == '\\' || x.from[x.n - 1] == '/')) x.from[--x.n] = 0;
    if (x.n < minLen) return;
    x.to = to;
    int i = g_crNeedleN++;
    for (; i > 0 && g_crNeedle[i - 1].n < x.n; i--) g_crNeedle[i] = g_crNeedle[i - 1];
    g_crNeedle[i] = x;
}

static void CrInitNeedles() {
    wchar_t v[MAX_PATH];
    DWORD n = MAX_PATH;
    g_crNeedleN = 0;
    CrAddNeedle(g_crUser, "<TS3User>", 4);
    wchar_t bin[MAX_PATH];
    wcscpy_s(bin, g_crExe);
    if (wchar_t* s = wcsrchr(bin, L'\\')) *s = 0;
    CrAddNeedle(bin, "<Bin>", 4);
    if (GetEnvironmentVariableW(L"USERPROFILE", v, MAX_PATH)) CrAddNeedle(v, "<home>", 4);
    if (GetEnvironmentVariableW(L"USERNAME", v, MAX_PATH)) CrAddNeedle(v, "<user>", 3);
    if (GetComputerNameW(v, &n)) CrAddNeedle(v, "<machine>", 3);
    if (GetEnvironmentVariableW(L"USERDOMAIN", v, MAX_PATH)) CrAddNeedle(v, "<domain>", 3);
}

// Case-insensitive (ASCII) replace of every needle, plus any "\Users\<x>" or "/Users/<x>" segment -> "\Users\<user>".
static size_t CrScrub(const char* in, size_t n, char* out, size_t cap) {
    size_t o = 0;
    for (size_t i = 0; i < n && o + 1 < cap;) {
        const char* rep = NULL;
        size_t skip = 0;
        for (int k = 0; k < g_crNeedleN && !rep; k++)
            if (g_crNeedle[k].n <= n - i && !_strnicmp(in + i, g_crNeedle[k].from, g_crNeedle[k].n)) rep = g_crNeedle[k].to, skip = g_crNeedle[k].n;
        if (!rep && (in[i] == '\\' || in[i] == '/') && n - i > 7 && !_strnicmp(in + i + 1, "Users", 5) && in[i + 6] == in[i]) {
            const char sep[2] = {in[i], 0};
            size_t j = i + 7;
            while (j < n && !strchr("\\/\r\n\"' ", in[j])) j++;
            for (const char* t : {sep, "Users", sep, "<user>"})
                for (; *t && o + 1 < cap; t++) out[o++] = *t;
            i = j;
            continue;
        }
        if (rep) {
            for (; *rep && o + 1 < cap; rep++) out[o++] = *rep;
            i += skip;
        } else {
            out[o++] = in[i++];
        }
    }
    out[o] = 0;
    return o;
}

// --- SHA-256 (exe identity; static state, no heap, no crypto DLL inside a crashing process) ---
struct CrSha { DWORD h[8]; u8 b[64]; unsigned long long len; };
static const DWORD kCrShaK[64] = {
    0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5, 0xd807aa98, 0x12835b01,
    0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174, 0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc,
    0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da, 0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147,
    0x06ca6351, 0x14292967, 0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
    0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070, 0x19a4c116, 0x1e376c08,
    0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3, 0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208,
    0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2};
static void CrShaBlock(CrSha& s, const u8* p) {
    DWORD w[64], v[8];
    for (int i = 0; i < 16; i++) w[i] = (DWORD)p[i * 4] << 24 | (DWORD)p[i * 4 + 1] << 16 | (DWORD)p[i * 4 + 2] << 8 | p[i * 4 + 3];
    for (int i = 16; i < 64; i++) {
        const DWORD s0 = _rotr(w[i - 15], 7) ^ _rotr(w[i - 15], 18) ^ (w[i - 15] >> 3);
        const DWORD s1 = _rotr(w[i - 2], 17) ^ _rotr(w[i - 2], 19) ^ (w[i - 2] >> 10);
        w[i] = w[i - 16] + s0 + w[i - 7] + s1;
    }
    memcpy(v, s.h, sizeof(v));
    for (int i = 0; i < 64; i++) {
        const DWORD t1 = v[7] + (_rotr(v[4], 6) ^ _rotr(v[4], 11) ^ _rotr(v[4], 25)) + ((v[4] & v[5]) ^ (~v[4] & v[6])) + kCrShaK[i] + w[i];
        const DWORD t2 = (_rotr(v[0], 2) ^ _rotr(v[0], 13) ^ _rotr(v[0], 22)) + ((v[0] & v[1]) ^ (v[0] & v[2]) ^ (v[1] & v[2]));
        memmove(v + 1, v, 7 * sizeof(DWORD));
        v[4] += t1;
        v[0] = t1 + t2;
    }
    for (int i = 0; i < 8; i++) s.h[i] += v[i];
}
static void CrShaInit(CrSha& s) {
    static const DWORD h0[8] = {0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, 0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19};
    memcpy(s.h, h0, sizeof(h0));
    s.len = 0;
}
static void CrShaAdd(CrSha& s, const u8* p, size_t n) {
    for (size_t i = 0; i < n; i++) {
        s.b[s.len++ & 63] = p[i];
        if (!(s.len & 63)) CrShaBlock(s, s.b);
    }
}
static void CrShaHex(CrSha& s, char out[65]) {
    const unsigned long long bits = s.len * 8;
    const u8 pad = 0x80, zero = 0;
    CrShaAdd(s, &pad, 1);
    while ((s.len & 63) != 56) CrShaAdd(s, &zero, 1);
    for (int i = 7; i >= 0; i--) { const u8 b = (u8)(bits >> (i * 8)); CrShaAdd(s, &b, 1); }
    for (int i = 0; i < 8; i++) sprintf_s(out + i * 8, 65 - i * 8, "%08lx", s.h[i]);
}
static bool CrFileSha(const wchar_t* path, char out[65]) {
    HANDLE h = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, NULL, OPEN_EXISTING, 0, NULL);
    if (h == INVALID_HANDLE_VALUE) return false;
    CrSha s;
    CrShaInit(s);
    DWORD r;
    while (ReadFile(h, g_crIo, sizeof(g_crIo), &r, NULL) && r) CrShaAdd(s, g_crIo, r);
    CloseHandle(h);
    CrShaHex(s, out);
    return true;
}

// --- modules and stack ---
struct CrMod { DWORD base; char name[64]; };
static CrMod g_crMods[32];
static int g_crModN;

// "TS3W" for TS3W.exe; NULL if the address is in no image. No loader lock: VirtualQuery + K32GetMappedFileNameW.
static const char* CrModOf(DWORD a, DWORD* base, bool needExec) {
    MEMORY_BASIC_INFORMATION mbi;
    if (a < 0x10000 || !VirtualQuery((const void*)(uintptr_t)a, &mbi, sizeof(mbi)) || mbi.State != MEM_COMMIT || mbi.Type != MEM_IMAGE) return NULL;
    if (needExec && !(mbi.Protect & (PAGE_EXECUTE | PAGE_EXECUTE_READ | PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY))) return NULL;
    *base = (DWORD)(uintptr_t)mbi.AllocationBase;
    for (int i = 0; i < g_crModN; i++) if (g_crMods[i].base == *base) return g_crMods[i].name;
    CrMod& m = g_crMods[g_crModN < 32 ? g_crModN++ : 31];
    m.base = *base;
    wchar_t w[MAX_PATH];
    if (!K32GetMappedFileNameW(GetCurrentProcess(), mbi.AllocationBase, w, MAX_PATH)) wcscpy_s(w, L"?");
    const wchar_t* s = wcsrchr(w, L'\\');
    CrUtf8(s ? s + 1 : w, m.name, sizeof(m.name));
    if (char* dot = strrchr(m.name, '.')) *dot = 0;
    return m.name;
}

static void CrFrame(DWORD a, char* out, size_t cap) {
    DWORD base;
    const char* m = CrModOf(a, &base, false);
    if (m) sprintf_s(out, cap, "%s+0x%lX", m, a - base); else sprintf_s(out, cap, "0x%08lX", a);
}

// A return address: in executable image code, right after a call (E8 rel32, or FF /2 of 2-7 bytes).
static bool CrAfterCall(DWORD r) {
    DWORD base;
    if (!CrModOf(r, &base, true) || r - base < 8 || !Readable((const void*)(uintptr_t)(r - 7), 7)) return false;
    const u8* p = (const u8*)(uintptr_t)r;
    if (p[-5] == 0xE8) return true;
    for (int k : {2, 3, 4, 6, 7})
        if (p[-k] == 0xFF && ((p[-k + 1] >> 3) & 7) == 2) return true;
    return false;
}

// Return addresses on the stack from esp up (a scan, not an unwind: the game has no frame pointers everywhere).
static int CrStack(DWORD esp, DWORD* out, int max) {
    int k = 0;
    const DWORD* sp = (const DWORD*)(uintptr_t)esp;
    for (int i = 0; i < 4096 && k < max && Readable(sp + i, 4); i++)
        if (CrAfterCall(sp[i])) out[k++] = sp[i];
    return k;
}

// signature = fault + top 3 frames; id = FNV-1a of it (what issues are searched and deduplicated by)
static DWORD CrSignature(DWORD fault, const DWORD* frames, int n, char* sig, size_t cap) {
    CrFrame(fault, sig, cap);
    for (int i = 0; i < n && i < 3; i++) {
        size_t l = strlen(sig);
        if (l + 2 >= cap) break;
        sig[l++] = ' ';
        CrFrame(frames[i], sig + l, cap - l);
    }
    DWORD h = 2166136261u;
    for (const char* p = sig; *p; p++) h = (h ^ (u8)*p) * 16777619u;
    return h;
}

// --- report pieces ---
static size_t CrTailOf(const wchar_t* path, char* out, size_t cap, int lines) {
    HANDLE h = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, NULL, OPEN_EXISTING, 0, NULL);
    if (h == INVALID_HANDLE_VALUE) return 0;
    LARGE_INTEGER sz = {};
    GetFileSizeEx(h, &sz);
    const LONGLONG want = sz.QuadPart < (LONGLONG)(cap - 1) ? sz.QuadPart : (LONGLONG)(cap - 1);
    LARGE_INTEGER at;
    at.QuadPart = sz.QuadPart - want;
    DWORD r = 0;
    if (SetFilePointerEx(h, at, NULL, FILE_BEGIN)) ReadFile(h, out, (DWORD)want, &r, NULL);
    CloseHandle(h);
    out[r] = 0;
    size_t s = r;
    for (int k = 0; s > 0; s--)
        if (out[s - 1] == '\n' && ++k > lines) break;
    memmove(out, out + s, r - s + 1);
    return r - s;
}

static void CrPackages(const wchar_t* dir, int depth, int* n) {
    wchar_t pat[MAX_PATH];
    if (swprintf_s(pat, L"%s\\*", dir) < 0) return;
    WIN32_FIND_DATAW f;
    HANDLE h = FindFirstFileExW(pat, FindExInfoBasic, &f, FindExSearchNameMatch, NULL, 0);
    if (h == INVALID_HANDLE_VALUE) return;
    do {
        if (f.cFileName[0] == L'.') continue;
        if (f.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) {
            wchar_t sub[MAX_PATH];
            if (depth < 4 && swprintf_s(sub, L"%s\\%s", dir, f.cFileName) > 0) CrPackages(sub, depth + 1, n);
        } else if (wcsrchr(f.cFileName, L'.') && !_wcsicmp(wcsrchr(f.cFileName, L'.'), L".package") && (*n)++ < 400) {
            char name[MAX_PATH * 3];
            CrUtf8(f.cFileName, name, sizeof(name));
            CrPut("  %s\r\n", name);  // names only, never the folder
        }
    } while (FindNextFileW(h, &f));
    FindClose(h);
}

static void CrSysInit() {
    int r[4];
    char brand[49] = {};
    __cpuid(r, 0x80000000);
    if ((unsigned)r[0] >= 0x80000004)
        for (int i = 0; i < 3; i++) __cpuid((int*)(brand + i * 16), 0x80000002 + i);
    const char* b = brand;
    while (*b == ' ') b++;
    SYSTEM_INFO si;
    GetNativeSystemInfo(&si);
    MEMORYSTATUSEX ms = {sizeof(ms)};
    GlobalMemoryStatusEx(&ms);
    OSVERSIONINFOW os = {sizeof(os)};
    typedef LONG(WINAPI * RtlGetVersionFn)(OSVERSIONINFOW*);
    if (RtlGetVersionFn f = (RtlGetVersionFn)(void*)GetProcAddress(GetModuleHandleW(L"ntdll.dll"), "RtlGetVersion")) f(&os);
    const u8* exe = (const u8*)GetModuleHandleW(NULL);
    const IMAGE_NT_HEADERS* nt = (const IMAGE_NT_HEADERS*)(exe + ((const IMAGE_DOS_HEADER*)exe)->e_lfanew);
    sprintf_s(g_crSys, "cpu: %s, %lu logical processors\r\nram: %llu MB\r\nos: Windows %lu.%lu.%lu\r\nlarge address aware: %s\r\n", *b ? b : "?",
              si.dwNumberOfProcessors, ms.ullTotalPhys >> 20, os.dwMajorVersion, os.dwMinorVersion, os.dwBuildNumber,
              nt->FileHeader.Characteristics & IMAGE_FILE_LARGE_ADDRESS_AWARE ? "yes" : "no");
}

static void CrSectionsInit(const wchar_t* ini) {
    static wchar_t names[8192];
    g_crSections[0] = 0;
    const DWORD n = GetPrivateProfileSectionNamesW(names, 8192, ini);
    size_t l = 0;
    for (const wchar_t* s = names; n && *s && l + 2 < sizeof(g_crSections); s += wcslen(s) + 1)
        if (GetPrivateProfileIntW(s, L"Enabled", 0, ini) > 0) {
            char a[128];
            CrUtf8(s, a, sizeof(a));
            const int w = sprintf_s(g_crSections + l, sizeof(g_crSections) - l, "%s[%s]", l ? " " : "", a);
            if (w < 0) break;
            l += w;
        }
}

// scrub what is in g_crBuf, append it to the file, start over
static void CrFlush(HANDLE h) {
    const size_t n = CrScrub(g_crBuf, g_crLen, g_crOut, sizeof(g_crOut));
    DWORD w;
    WriteFile(h, g_crOut, (DWORD)n, &w, NULL);
    FlushFileBuffers(h);
    g_crLen = 0;
}

static void CrCommonTail(HANDLE h, const char* tail, size_t tailN, const char* tailWhat) {
    char sha[65];
    CrPut("exe sha256: %s\r\n", CrFileSha(g_crExe, sha) ? sha : "?");
    CrPut("%s", g_crSys);
    CrFlush(h);
    int n = 0;
    CrPut("--- package files (names only) ---\r\n");
    if (g_crUser[0]) {
        wchar_t mods[MAX_PATH];
        if (swprintf_s(mods, L"%s\\Mods\\Packages", g_crUser) > 0) CrPackages(mods, 0, &n);
    }
    CrPut("packages: %d%s\r\n--- %s ---\r\n", n, n > 400 ? " (first 400 listed)" : "", tailWhat);
    CrFlush(h);
    if (tailN) {  // in pieces: the tail is up to 32 KB
        for (size_t i = 0; i < tailN; i += 16384) {
            const size_t k = tailN - i < 16384 ? tailN - i : 16384;
            memcpy(g_crBuf + g_crLen, tail + i, k);
            g_crLen += k;
            CrFlush(h);
        }
    }
    CrPut("\r\n--- end ---\r\n");
    CrFlush(h);
}

static HANDLE CrOpenReport(const char* kind) {
    SYSTEMTIME st;
    GetSystemTime(&st);
    wchar_t path[MAX_PATH];
    if (swprintf_s(path, L"%s\\crash-%04d%02d%02d-%02d%02d%02d-%lu%s.txt", g_crDir, st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute,
                   st.wSecond, GetCurrentProcessId(), *kind == 'h' ? L"-hard" : L"") < 0)
        return INVALID_HANDLE_VALUE;
    HANDLE h = CreateFileW(path, GENERIC_WRITE, FILE_SHARE_READ, NULL, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, NULL);
    g_crLen = 0;
    CrPut("ts3native crash report 1\r\nkind: %s\r\ntime: %04d-%02d-%02dT%02d:%02d:%02dZ\r\n", kind, st.wYear, st.wMonth, st.wDay,
          st.wHour, st.wMinute, st.wSecond);
    return h;
}

static void CrMarkerPath(wchar_t* out) { swprintf_s(out, MAX_PATH, L"%s\\running.txt", g_crDir); }

static void CrReport(const EXCEPTION_POINTERS* e) {
    const EXCEPTION_RECORD* r = e->ExceptionRecord;
    const CONTEXT* c = e->ContextRecord;
    HANDLE h = CrOpenReport("exception");
    if (h == INVALID_HANDLE_VALUE) return;
    static DWORD frames[32];
    const int nf = CrStack(c->Esp, frames, 32);
    const DWORD fault = (DWORD)(uintptr_t)r->ExceptionAddress;
    char sig[256], fr[96];
    const DWORD id = CrSignature(fault, frames, nf, sig, sizeof(sig));
    CrFrame(fault, fr, sizeof(fr));
    CrPut("title: [crash] %s (ts3native " TS3NATIVE_VERSION ")\r\nsignature id: crashsig-%08lx\r\nsignature: %s\r\nts3native: " TS3NATIVE_VERSION "\r\n",
          fr, id, sig);
    CrPut("code: %08lX", r->ExceptionCode);
    if (r->ExceptionCode == EXCEPTION_ACCESS_VIOLATION && r->NumberParameters >= 2)
        CrPut(" (access violation, %s 0x%08lX)", r->ExceptionInformation[0] == 0 ? "read" : r->ExceptionInformation[0] == 8 ? "execute" : "write",
              (DWORD)r->ExceptionInformation[1]);
    CrPut("\r\n");
    const DWORD tid = GetCurrentThreadId();
    char jm[160];
    if (JitFaultName(fault, jm, sizeof(jm))) CrPut("compiled method: %s\r\n", jm);  // [MonoJit]: the eip is in the code region
    CrPut("fault: %s\r\nthread: %lu%s\r\n", fr, tid,
          tid == g_mainTid ? " (main)" : Readable(g_rtSimTidSlot, 4) && *g_rtSimTidSlot == tid ? " (sim)" : "");
    CrPut("registers: eax=%08lX ebx=%08lX ecx=%08lX edx=%08lX esi=%08lX edi=%08lX ebp=%08lX esp=%08lX eip=%08lX efl=%08lX\r\n", c->Eax, c->Ebx,
          c->Ecx, c->Edx, c->Esi, c->Edi, c->Ebp, c->Esp, c->Eip, c->EFlags);
    MEMORYSTATUSEX ms = {sizeof(ms)};
    if (GlobalMemoryStatusEx(&ms)) CrPut("address space used: %llu of %llu MB\r\n", (ms.ullTotalVirtual - ms.ullAvailVirtual) >> 20, ms.ullTotalVirtual >> 20);
    CrPut("enabled sections: %s\r\nstack (return addresses found on the stack, top first):\r\n", g_crSections);
    for (int i = 0; i < nf; i++) {
        CrFrame(frames[i], fr, sizeof(fr));
        CrPut("  %s\r\n", fr);
    }
    CrFlush(h);  // the part that matters is on disk before anything that could fault again
    static char tail[32768];
    const size_t tn = CrTailOf(g_crLog, tail, sizeof(tail), 50);
    CrCommonTail(h, tail, tn, "ts3native.log (last 50 lines)");
    CloseHandle(h);
    wchar_t m[MAX_PATH];
    CrMarkerPath(m);
    DeleteFileW(m);  // captured: no "hard" report next start
}

// Apex Radiance (docs/148 §6 #8, §7a) re-sets the kernel32 filter every second through its own import, keeps the one it
// replaced (ours) and calls it after its report [verified crash_report.cpp Refresh]: chain Apex -> ours -> EA, nothing to
// fight. The guard stops a cycle (a chain that leads back to ours on the same thread) instead of recursing until the stack
// is gone.
static thread_local int t_crDepth;
static LONG WINAPI CrFilter(EXCEPTION_POINTERS* e) {
    if (t_crDepth) return EXCEPTION_CONTINUE_SEARCH;
    if (InterlockedExchange(&g_crBusy, 1) == 0) {
        __try {
            CrReport(e);
        } __except (EXCEPTION_EXECUTE_HANDLER) {
        }
    }
    const LPTOP_LEVEL_EXCEPTION_FILTER next = g_crPrev;
    if (!next || next == CrFilter) return EXCEPTION_CONTINUE_SEARCH;
    t_crDepth = 1;
    const LONG r = next(e);
    t_crDepth = 0;
    return r;
}

// Exe IAT stand-in for SetUnhandledExceptionFilter: ours stays on top, the caller's filter becomes the next one.
static LPTOP_LEVEL_EXCEPTION_FILTER WINAPI CrSetFilter(LPTOP_LEVEL_EXCEPTION_FILTER f) {
    return (LPTOP_LEVEL_EXCEPTION_FILTER)InterlockedExchangePointer((void* volatile*)&g_crPrev, (void*)f);
}

static DWORD* CrFindIat(HMODULE mod, const void* real) {
    const u8* b = (const u8*)mod;
    const IMAGE_NT_HEADERS* nt = (const IMAGE_NT_HEADERS*)(b + ((const IMAGE_DOS_HEADER*)b)->e_lfanew);
    const IMAGE_DATA_DIRECTORY& d = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
    if (!d.VirtualAddress || !real) return NULL;
    for (const IMAGE_IMPORT_DESCRIPTOR* i = (const IMAGE_IMPORT_DESCRIPTOR*)(b + d.VirtualAddress); i->Name; i++)
        for (DWORD* t = (DWORD*)(b + i->FirstThunk); *t; t++)
            if (*t == (DWORD)(uintptr_t)real) return t;
    return NULL;
}

// Previous session left its marker: it ended without DLL_PROCESS_DETACH and without a filter report.
static void CrHardReport(const char* marker) {
    HANDLE h = CrOpenReport("hard");
    if (h == INVALID_HANDLE_VALUE) return;
    // newest EA xcpt*.txt: only its type and address lines (the rest names the user and the machine)
    char fault[96] = "hard exit", xtype[128] = "", xaddr[160] = "";
    WIN32_FIND_DATAW f, best = {};
    wchar_t pat[MAX_PATH];
    if (g_crUser[0] && swprintf_s(pat, L"%s\\xcpt*.txt", g_crUser) > 0) {
        HANDLE fh = FindFirstFileW(pat, &f);
        if (fh != INVALID_HANDLE_VALUE) {
            do if (CompareFileTime(&f.ftLastWriteTime, &best.ftLastWriteTime) > 0) best = f; while (FindNextFileW(fh, &f));
            FindClose(fh);
        }
    }
    wchar_t mp[MAX_PATH];
    CrMarkerPath(mp);
    WIN32_FILE_ATTRIBUTE_DATA ma;
    if (best.cFileName[0] && GetFileAttributesExW(mp, GetFileExInfoStandard, &ma) && CompareFileTime(&best.ftLastWriteTime, &ma.ftLastWriteTime) > 0) {
        wchar_t xp[MAX_PATH];
        static char x[16384];
        x[0] = 0;
        if (swprintf_s(xp, L"%s\\%s", g_crUser, best.cFileName) > 0) {
            HANDLE xh = CreateFileW(xp, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL, OPEN_EXISTING, 0, NULL);
            DWORD r = 0;
            if (xh != INVALID_HANDLE_VALUE) ReadFile(xh, x, sizeof(x) - 1, &r, NULL), CloseHandle(xh);
            x[r] = 0;
        }
        for (char* l = x; *l;) {
            char* e = l + strcspn(l, "\r\n");
            const char c = *e;
            *e = 0;
            if (!_strnicmp(l, "type:", 5)) strncpy_s(xtype, l, _TRUNCATE);
            if (!_strnicmp(l, "address:", 8)) {
                strncpy_s(xaddr, l, _TRUNCATE);
                unsigned long a = 0;
                const char* q = strrchr(l, '\\');
                if (sscanf_s(l + 8, " 0x%lx", &a) == 1 && q && !_strnicmp(q + 1, "TS3W.exe", 8) && a >= 0x400000)
                    sprintf_s(fault, "TS3W+0x%lX", a - 0x400000);  // fixed base 0x400000 (no ASLR)
            }
            l = c ? e + 1 : e;
        }
    }
    DWORD id = 2166136261u;
    for (const char* p = fault; *p; p++) id = (id ^ (u8)*p) * 16777619u;
    CrPut("title: [crash] %s (ts3native %s)\r\nsignature id: crashsig-%08lx\r\nsignature: %s\r\nts3native: %s\r\n", fault, marker, id, fault, marker);
    CrPut("note: the previous session ended without a clean exit and no exception reached the filter (killed, frozen and ended, "
          "or a crash handled elsewhere)\r\n");
    if (xtype[0] || xaddr[0]) CrPut("EA xcpt %s\r\nEA xcpt %s\r\n", xtype, xaddr);  // the scrubber handles the module path
    CrPut("enabled sections (this start): %s\r\n", g_crSections);
    CrFlush(h);
    CrCommonTail(h, g_crPrevTail, g_crPrevTailN, "previous ts3native.log (last 50 lines)");
    CloseHandle(h);
}

// Before Run truncates ts3native.log: keep the previous session's last lines for a "hard" report.
static void CrPreLog(const wchar_t* ini, const wchar_t* log) {
    if (!GetPrivateProfileIntW(L"CrashReport", L"Enabled", 0, ini)) return;
    g_crPrevTailN = CrTailOf(log, g_crPrevTail, sizeof(g_crPrevTail), 50);
}

static void CrFindDirs() {
    UserSims3Dir(g_crUser);  // apex.cpp
    if (GetFileAttributesW(g_crUser) == INVALID_FILE_ATTRIBUTES) {
        // shortcut: localized folder names ("Die Sims 3") are not searched; the reports go next to ts3native.asi instead
        g_crUser[0] = 0;
        wcscpy_s(g_crDir, g_crLog);
        if (wchar_t* s = wcsrchr(g_crDir, L'\\')) *s = 0;
        wcscat_s(g_crDir, L"\\ts3native-crashes");
    } else {
        swprintf_s(g_crDir, L"%s\\ts3native-crashes", g_crUser);
    }
}

static void InstallCrashReport(const wchar_t* ini, const wchar_t* log) {
    if (!GetPrivateProfileIntW(L"CrashReport", L"Enabled", 0, ini)) { Log("crash report off (Enabled=0)"); return; }
    wcscpy_s(g_crLog, log);
    GetModuleFileNameW(NULL, g_crExe, MAX_PATH);
    CrFindDirs();
    CrInitNeedles();
    CrSectionsInit(ini);
    CrSysInit();
    CreateDirectoryW(g_crDir, NULL);
    wchar_t mp[MAX_PATH];
    CrMarkerPath(mp);
    char prev[64] = "?";
    HANDLE mh = CreateFileW(mp, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, 0, NULL);
    if (mh != INVALID_HANDLE_VALUE) {
        char t[256] = {};
        DWORD r = 0;
        ReadFile(mh, t, sizeof(t) - 1, &r, NULL);
        CloseHandle(mh);
        if (!strncmp(t, "ts3native ", 10)) sscanf_s(t + 10, "%63s", prev, (unsigned)sizeof(prev));
        CrHardReport(prev);
        Log("crash report: the previous session (ts3native %s) ended without a clean exit: hard report written", prev);
    }
    mh = CreateFileW(mp, GENERIC_WRITE, FILE_SHARE_READ, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (mh != INVALID_HANDLE_VALUE) {
        char t[64];
        DWORD w;
        WriteFile(mh, t, (DWORD)sprintf_s(t, "ts3native " TS3NATIVE_VERSION "\r\npid %lu\r\n", GetCurrentProcessId()), &w, NULL);
        CloseHandle(mh);
    }
    g_crOn = true;
    g_crPrev = SetUnhandledExceptionFilter(CrFilter);
    g_crIatReal = (void*)GetProcAddress(GetModuleHandleW(L"kernel32.dll"), "SetUnhandledExceptionFilter");
    g_crIatSlot = CrFindIat(GetModuleHandleW(NULL), g_crIatReal);
    const DWORD v = (DWORD)(uintptr_t)CrSetFilter;
    const bool hooked = g_crIatSlot && Write(g_crIatSlot, &v, 4);
    Log("crash report on: filter set (next 0x%p), exe IAT SetUnhandledExceptionFilter %s 0x%08lX, %d scrub needles", (void*)g_crPrev,
        hooked ? "hooked at" : "NOT hooked (a later filter replaces ours)", (DWORD)(uintptr_t)g_crIatSlot, g_crNeedleN);
    if (g_apexFound)
        Log("crash report: Apex crash report also active: chain Apex -> ours -> EA; expect ApexRadiance_Crash.txt next to ours (docs/148 §6 #8)");
}

// DLL_PROCESS_DETACH (normal quit through exit/ExitProcess): no marker = no "hard" report next start
static void CrCleanExit() {
    if (!g_crOn) return;
    wchar_t mp[MAX_PATH];
    CrMarkerPath(mp);
    DeleteFileW(mp);
}
