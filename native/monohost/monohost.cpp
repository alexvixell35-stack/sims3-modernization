// monohost: M1.1 go/no-go for the Mono offline host (docs/119 §4, §8).
// Loads a COPY of the Steam 1.67 TS3W.exe at 0x400000 as an image (LoadLibraryExW, no WinMain, no CRT init of the exe),
// redirects eglib's allocator thunks to our CRT, serves assemblies from a folder through a detour of EA's image loader
// 0xD6FCB0, calls mono_interp_init("MonoEngine"), checks corlib, loads ScriptCore and invokes one pure static method.
// Usage: monohost.exe <path to TS3W.exe copy> <folder with mscorlib.dll, ScriptCore.dll, ...> [probe | m13 <hostdata> [lenient]]
// "probe" adds the pre-M1.2 checks P1-P5 of docs/119 §9 (other assemblies, IC stubs, service object + Sleep, one GC).
// "m13" runs M1.2 + M1.3 (docs/119 §10): all 10 assemblies, typed fail-loud IC stubs, the SetData service objects and a
// .cctor sweep (CSV next to the exe); <hostdata> = `node tools/managed-callgraph.ts hostdata` output; "lenient" makes
// every non-struct stub return a typed zero instead of throwing.
// "selftest <hostdata>" (docs/119 §11) runs M1.1-M1.3 strict plus M1.4 (GC tick, handles, finalizers) and M1.5 (task
// driver with yields) against out\MonoHostTest.dll (HostTest.cs) and prints PASS/FAIL lines; exit code = failed checks.
// It also runs the M2 stubs (docs/119 §12; "m13 <hostdata> m2" for the sweep alone) and the M5 proof (docs/120 §8: the
// unchanged ts3native [MonoThread] code with a worker; both threads interpreting at once, docs/120 §9).
// docs/139 (env, selftest mode): MONOHOST_MINT=count|threaded|nocheck, MONOHOST_MINTFUSE=sub, MONOHOST_MINTBENCH=<n>.
// docs/207 §7 (env, selftest mode): MONOHOST_JIT=off|dump|on|verify method compiler prototype (monohost-jit.cpp).
// Never point it at the installed game: the exe is only mapped, but keep EA files out of git (copy under game-copy/).
#include <windows.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <limits.h>
#include <intrin.h>  // _ReturnAddress (ts3native simloop.cpp SppHook)

typedef unsigned char u8;
static const DWORD IMG_LO = 0x00400000, IMG_HI = 0x0125A000;  // SizeOfImage 0xE5A000 (Steam 1.67)
static const DWORD EXE_STAMP = 0x52DEC247;                     // TimeDateStamp of Steam 1.67 TS3W.exe
static FILE* g_log;
static char g_asmDir[MAX_PATH];
static char g_served[16][64];       // assembly files the image detour has served (references load them on demand)
static void* g_servedImg[16];
static int g_nServed, g_cacheHits;
static int g_mode;                  // 0 = M1.1, 1 = probe (docs/119 §9), 2 = m13 (M1.2 + M1.3, docs/119 §10), 3 = selftest (§11)
static char g_hostdata[MAX_PATH];   // m13/selftest: tools/managed-callgraph.ts hostdata output
static int g_pass, g_fail;          // selftest check counts
static unsigned char g_fakeHost[0x1800];  // M1.5 stand-in for the MonoScriptHost at [0x11EE514] (docs/119 §11.2)

static void Log(const char* fmt, ...) {
    va_list a;
    va_start(a, fmt);
    vprintf(fmt, a);
    va_end(a);
    if (g_log) { va_start(a, fmt); vfprintf(g_log, fmt, a); va_end(a); fflush(g_log); }
    fflush(stdout);
}

// selftest line: "PASS <id> <what>" or "FAIL <id> <what>"; the exit code counts the FAILs
static bool Check(const char* id, bool ok, const char* fmt, ...) {
    char buf[512];
    va_list a;
    va_start(a, fmt);
    _vsnprintf_s(buf, sizeof(buf), _TRUNCATE, fmt, a);
    va_end(a);
    Log("%s %s %s\n", ok ? "PASS" : "FAIL", id, buf);
    (ok ? g_pass : g_fail)++;
    return ok;
}

// Windows maps small regions at the lowest free addresses during process start, often at 0x400000, so the parent starts
// this program suspended and reserves the image range first (same trick as native/ts3native/steerro-test.cpp).
static int RunChild() {
    STARTUPINFOA si = {sizeof(si)};
    PROCESS_INFORMATION pi;
    SetEnvironmentVariableA("MONOHOST_CHILD", "1");
    if (!CreateProcessA(NULL, GetCommandLineA(), NULL, NULL, FALSE, CREATE_SUSPENDED, NULL, NULL, &si, &pi)) return 2;
    if (VirtualAllocEx(pi.hProcess, (void*)(uintptr_t)IMG_LO, IMG_HI - IMG_LO, MEM_RESERVE, PAGE_NOACCESS) != (void*)(uintptr_t)IMG_LO) {
        printf("cannot reserve 0x%08lX-0x%08lX in the child (err %lu)\n", IMG_LO, IMG_HI, GetLastError());
        TerminateProcess(pi.hProcess, 2);
        return 2;
    }
    ResumeThread(pi.hThread);
    DWORD rc = 2;
    if (WaitForSingleObject(pi.hProcess, GetEnvironmentVariableA("MONOHOST_JIT", NULL, 0) ? 900000 : 120000) == WAIT_TIMEOUT) {  // fail fast: init takes seconds
        printf("child timed out after 120 s\n");
        TerminateProcess(pi.hProcess, 4);
    }
    GetExitCodeProcess(pi.hProcess, &rc);
    return (int)rc;
}

// ---- crash reporting: exception code, EIP, registers and return addresses into the exe ----
static LONG CALLBACK Veh(EXCEPTION_POINTERS* e) {
    DWORD code = e->ExceptionRecord->ExceptionCode;
    if (code == 0x406D1388 || code == DBG_PRINTEXCEPTION_C || code == 0x4001000A) return EXCEPTION_CONTINUE_SEARCH;  // thread names, OutputDebugString
    static int seen;
    if (++seen > 20) return EXCEPTION_CONTINUE_SEARCH;
    CONTEXT* c = e->ContextRecord;
    HMODULE mod = NULL;
    char modName[MAX_PATH] = "?";
    if (GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, (LPCSTR)(uintptr_t)c->Eip, &mod)) GetModuleFileNameA(mod, modName, MAX_PATH);
    Log("EXCEPTION 0x%08lX at EIP 0x%08lX (%s +0x%lX) thread %lu", code, c->Eip, strrchr(modName, '\\') ? strrchr(modName, '\\') + 1 : modName,
        c->Eip - (DWORD)(uintptr_t)mod, GetCurrentThreadId());
    if (code == EXCEPTION_ACCESS_VIOLATION) Log(" (%s 0x%08lX)", e->ExceptionRecord->ExceptionInformation[0] ? "write" : "read", (DWORD)e->ExceptionRecord->ExceptionInformation[1]);
    Log("\n  eax=%08lX ebx=%08lX ecx=%08lX edx=%08lX esi=%08lX edi=%08lX ebp=%08lX esp=%08lX\n  exe return addresses on stack:", c->Eax, c->Ebx, c->Ecx, c->Edx, c->Esi, c->Edi, c->Ebp, c->Esp);
    DWORD* sp = (DWORD*)(uintptr_t)c->Esp;
    int n = 0;
    for (int i = 0; i < 2048 && n < 24; i++) {
        MEMORY_BASIC_INFORMATION m;
        if (!VirtualQuery(sp + i, &m, sizeof(m)) || m.State != MEM_COMMIT) break;
        DWORD v = sp[i];
        if (v > IMG_LO + 0x1000 && v < 0x00F95000) { Log(" %08lX", v); n++; }  // .text range of the exe
    }
    Log("\n");
    return EXCEPTION_CONTINUE_SEARCH;  // first chance: Mono or the exe may handle it; the unhandled filter ends the run
}

static LONG WINAPI Unhandled(EXCEPTION_POINTERS* e) {
    Log("UNHANDLED 0x%08lX at EIP 0x%08lX\n", e->ExceptionRecord->ExceptionCode, e->ContextRecord->Eip);
    TerminateProcess(GetCurrentProcess(), 3);
    return EXCEPTION_EXECUTE_HANDLER;
}

// ---- allocator: eglib thunks and EA GeneralAllocator entry points to our CRT heap ----
static void* __cdecl GMalloc(size_t n) { return malloc(n ? n : 1); }
static void* __cdecl GMalloc0(size_t n, size_t m) { return calloc(n ? n : 1, m ? m : 1); }  // EA thunk takes (n, m) and multiplies
static void __cdecl GFree(void* p) { free(p); }
static char* __cdecl GStrdup(const char* s) { return s ? _strdup(s) : NULL; }
static void* __cdecl GRealloc(void* p, size_t n) { return realloc(p, n); }
// GeneralAllocator methods are thiscall; fastcall with an unused edx matches (callee pops the stack args).
static void* __fastcall EaAlloc(void*, void*, size_t n, int flags) {
    static bool warned;
    if (flags && !warned) { warned = true; Log("note: EA alloc with flags 0x%X (size %u)\n", flags, (unsigned)n); }
    return malloc(n ? n : 1);
}
static void __fastcall EaFree(void*, void*, void* p) { free(p); }
static void* __fastcall EaRealloc(void*, void*, void* p, size_t n, int) { return realloc(p, n); }

static bool Jmp(DWORD at, const void* to) {
    DWORD old;
    if (!VirtualProtect((void*)(uintptr_t)at, 5, PAGE_EXECUTE_READWRITE, &old)) return false;
    u8* p = (u8*)(uintptr_t)at;
    p[0] = 0xE9;
    *(DWORD*)(p + 1) = (DWORD)(uintptr_t)to - (at + 5);
    VirtualProtect(p, 5, old, &old);
    FlushInstructionCache(GetCurrentProcess(), p, 5);
    return true;
}

// ---- image loader detour: EA 0xD6FCB0 (cdecl, path) -> MonoImage* via mono_image_open_from_data 0xE84860 ----
typedef void* (__cdecl* OpenFromData)(void* data, DWORD len, int needCopy, int* status);
static void* __cdecl LoadImageByName(const char* path) {
    const char* base = path;
    for (const char* q = path; *q; q++) if (*q == '\\' || *q == '/') base = q + 1;
    // name -> image cache, like the game's image map check in 0xD79C60: GAC-path reference loads reuse the loaded image
    for (int j = 0; j < g_nServed; j++)
        if (!_stricmp(g_served[j], base)) {  // no addref, like 0xD79C60; first word = MonoImage.ref_count (stock 1.2.3 layout)
            g_cacheHits++;
            Log("image request '%s' -> cached %s image %p (first word %d)\n", path, base, g_servedImg[j], *(int*)g_servedImg[j]);
            return g_servedImg[j];
        }
    char full[MAX_PATH * 2];
    _snprintf_s(full, sizeof(full), _TRUNCATE, "%s\\%s", g_asmDir, base);
    FILE* f = NULL;
    if ((fopen_s(&f, full, "rb") || !f) && !_stricmp(base, "MonoHostTest.dll")) {  // our test assembly sits next to the host exe
        GetModuleFileNameA(NULL, full, MAX_PATH);
        strcpy_s(strrchr(full, '\\') + 1, MAX_PATH, base);
        fopen_s(&f, full, "rb");
    }
    if (!f) { Log("image request '%s' -> no file %s\n", path, full); return NULL; }
    fseek(f, 0, SEEK_END);
    long len = ftell(f);
    fseek(f, 0, SEEK_SET);
    void* data = malloc(len);
    size_t got = fread(data, 1, len, f);
    fclose(f);
    int status = -1;
    void* img = got == (size_t)len ? ((OpenFromData)0xE84860)(data, (DWORD)len, 1, &status) : NULL;
    free(data);  // need_copy = 1: Mono keeps its own copy
    Log("image request '%s' -> %s (%ld bytes) image %p status %d\n", path, base, len, img, status);
    if (img && g_nServed < 16) { g_servedImg[g_nServed] = img; strcpy_s(g_served[g_nServed++], base); }
    return img;
}

// 0xD6FC40 (cdecl, char* out): machine.config path; point Mono at a file that does not exist
static void __cdecl ConfigPath(char* out) { strcpy_s(out, MAX_PATH, "C:\\monohost-no-such-dir\\machine.config"); }

static bool CheckImage(HMODULE h) {
    MEMORY_BASIC_INFORMATION m = {};
    VirtualQuery((void*)(uintptr_t)IMG_LO, &m, sizeof(m));
    IMAGE_NT_HEADERS* nt = (IMAGE_NT_HEADERS*)((u8*)h + ((IMAGE_DOS_HEADER*)h)->e_lfanew);
    Log("TS3W.exe mapped at %p, region type 0x%lX (MEM_IMAGE = 0x1000000), stamp 0x%08lX, SizeOfImage 0x%lX\n", h, m.Type, nt->FileHeader.TimeDateStamp, nt->OptionalHeader.SizeOfImage);
    if ((DWORD)(uintptr_t)h != IMG_LO || m.Type != MEM_IMAGE) { Log("FAIL: not an image at 0x400000\n"); return false; }
    if (nt->FileHeader.TimeDateStamp != EXE_STAMP || nt->OptionalHeader.SizeOfImage != IMG_HI - IMG_LO) { Log("FAIL: not the Steam 1.67 exe\n"); return false; }
    return true;
}

// The exe's static TLS block for the calling thread: a copy of its template at index EXE_TLS_INDEX in a new array that
// keeps the slots the loader gave the host and the system DLLs (a 2-entry array broke their TLS: faults in dbghelp,
// gdi32full and ntdll when the M5 worker exited). Slots past the loader's vector get whatever follows it (no DLL uses
// them). The loader's own vector goes back at thread detach, so the loader frees what it allocated.
static const DWORD EXE_TLS_INDEX = 63;
static IMAGE_TLS_DIRECTORY32* g_exeTls;
static void SetExeTls() {
    IMAGE_TLS_DIRECTORY32* td = g_exeTls;
    DWORD raw = td->EndAddressOfRawData - td->StartAddressOfRawData, size = raw + td->SizeOfZeroFill;
    u8* block = (u8*)calloc(1, size + 16);
    memcpy(block, (void*)(uintptr_t)td->StartAddressOfRawData, raw);
    void** oldArr = (void**)(uintptr_t)__readfsdword(0x2C);
    void** arr = (void**)calloc(EXE_TLS_INDEX + 2, sizeof(void*));
    for (DWORD i = 0; oldArr && i < EXE_TLS_INDEX; i++) ReadProcessMemory(GetCurrentProcess(), oldArr + i, arr + i, 4, NULL);
    arr[EXE_TLS_INDEX] = block;
    arr[EXE_TLS_INDEX + 1] = oldArr;  // the loader's vector, restored at detach
    __writefsdword(0x2C, (DWORD)(uintptr_t)arr);
}
// threads started after the exe is mapped (the M5 worker) get the exe's TLS block too
static void NTAPI HostTlsCb(PVOID, DWORD reason, PVOID) {
    if (reason == DLL_THREAD_ATTACH && g_exeTls) SetExeTls();
    if (reason == DLL_THREAD_DETACH && g_exeTls) {
        void** arr = (void**)(uintptr_t)__readfsdword(0x2C);
        if (arr && arr[EXE_TLS_INDEX + 1]) __writefsdword(0x2C, (DWORD)(uintptr_t)arr[EXE_TLS_INDEX + 1]);
    }
}
#pragma comment(linker, "/INCLUDE:__tls_used")
#pragma comment(linker, "/INCLUDE:_g_hostTlsCb")
#pragma const_seg(".CRT$XLB")
extern "C" const PIMAGE_TLS_CALLBACK g_hostTlsCb = HostTlsCb;
#pragma const_seg()

// The Windows loader maps an EXE loaded with LoadLibraryExW but does not snap its imports or set up its static TLS
// (verified 2026-10-20: IAT slot 0xF95140 still held the hint/name RVA). Do both here, for the calling thread only.
static bool ResolveImports(HMODULE h) {
    u8* b = (u8*)h;
    IMAGE_NT_HEADERS* nt = (IMAGE_NT_HEADERS*)(b + ((IMAGE_DOS_HEADER*)h)->e_lfanew);
    IMAGE_DATA_DIRECTORY& d = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
    int dlls = 0, funcs = 0;
    for (IMAGE_IMPORT_DESCRIPTOR* id = (IMAGE_IMPORT_DESCRIPTOR*)(b + d.VirtualAddress); id->Name; id++, dlls++) {
        const char* name = (const char*)(b + id->Name);
        HMODULE m = LoadLibraryA(name);
        if (!m) { Log("import DLL %s: LoadLibrary err %lu\n", name, GetLastError()); return false; }
        DWORD* names = (DWORD*)(b + (id->OriginalFirstThunk ? id->OriginalFirstThunk : id->FirstThunk));
        DWORD* iat = (DWORD*)(b + id->FirstThunk);
        for (int i = 0; names[i]; i++, funcs++) {
            const char* fn = (names[i] & IMAGE_ORDINAL_FLAG32) ? (const char*)(uintptr_t)(names[i] & 0xFFFF) : (const char*)(b + names[i] + 2);
            FARPROC p = GetProcAddress(m, fn);
            if (!p) { Log("import %s!%s missing\n", name, (names[i] & IMAGE_ORDINAL_FLAG32) ? "(ordinal)" : fn); return false; }
            DWORD old;
            VirtualProtect(&iat[i], 4, PAGE_READWRITE, &old);
            iat[i] = (DWORD)(uintptr_t)p;
            VirtualProtect(&iat[i], 4, old, &old);
        }
    }
    Log("imports: %d DLLs, %d functions resolved by the host\n", dlls, funcs);
    IMAGE_DATA_DIRECTORY& t = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_TLS];
    if (t.Size) {
        IMAGE_TLS_DIRECTORY32* td = (IMAGE_TLS_DIRECTORY32*)(b + t.VirtualAddress);
        DWORD old;
        VirtualProtect((void*)(uintptr_t)td->AddressOfIndex, 4, PAGE_READWRITE, &old);
        *(DWORD*)(uintptr_t)td->AddressOfIndex = EXE_TLS_INDEX;
        g_exeTls = td;
        SetExeTls();
        DWORD* cb = (DWORD*)(uintptr_t)td->AddressOfCallBacks;
        int ncb = 0;
        while (cb && cb[ncb]) ncb++;
        Log("static TLS: %lu bytes at index 63, %d TLS callbacks (not run)\n", td->EndAddressOfRawData - td->StartAddressOfRawData + td->SizeOfZeroFill, ncb);
    }
    return true;
}

typedef void* (__cdecl* InterpInit)(const char*);
typedef void* (__cdecl* AsmLoadFromFull)(void* image, const char* fname, int* status, int refonly);
typedef void* (__cdecl* ClassFromName)(void* image, const char* ns, const char* name);
typedef void* (__cdecl* MethodFromNameFlags)(void* klass, const char* name, int params, int flags);
typedef void* (__cdecl* RuntimeInvoke)(void* method, void* obj, void** params, void** exc);
typedef void(__cdecl* AddInternalCall)(const char* name, const void* fn);
typedef void* (__cdecl* ObjectNew)(void* domain, void* klass);

// ---- IC stubs (docs/119 §9.1, §10): one per exe name string, fail loud by default ----
typedef void* (__cdecl* ExcNotImpl)(const char* msg);  // mono_get_exception_not_implemented 0xE77360 (exception.c order)
typedef void(__cdecl* RaiseExc)(void* exc);            // mono_raise_exception 0xEA18F0 -> ex_handler [0x1195B64] (interp longjmp)
static const char* g_icNames[2048];
static char g_icKind[2048];       // return kind from hostdata: v void, i 32-bit, l 64-bit, f float, d double, s struct, 0 unknown
static char g_icAllow[2048];      // allow-list: 1 = typed zero, 2 = host tick
static int g_icCalls[2048];
static int g_icCount, g_icHits;
static bool g_lenient;            // sweep mode: every non-struct stub returns a typed zero instead of throwing
static long long g_tick, g_ret64; // host tick for Simulator_GetTickCountImpl; 64-bit return slot for the entry
static double g_retDbl;           // double return slot (answer 4: fld)
static int g_step[16], g_nStep;   // stubs hit during the current step (attribution for services and the .cctor sweep)
static const char* g_rets[4096]; static char g_retKinds[4096]; static int g_nRets;  // hostdata "R" lines

// ---- M2 hand-written stubs (docs/119 §12), active with g_m2: answers by IC name, keyed after "::" ----
enum { M2_ZERO = 1, M2_STOPWATCH, M2_GUID, M2_STRING, M2_XMLDOC, M2_RANDOM };
static const struct { const char* name; char code; } kM2[] = {
    {"Animation_SetPMAAnimationFinishedCallback", M2_ZERO}, {"UIManager_SetUICallbacksImpl", M2_ZERO},
    {"UIManager_InitUIHierarchyImpl", M2_ZERO}, {"UIManager_GetMainWindowImpl", M2_ZERO},{"UIManager_InitUserToolHelperImpl", M2_ZERO}, {"UIManager_LoadUIImageImpl", M2_ZERO},
    {"Simulator_AddObjectImpl", M2_ZERO}, {"Command_RegisterCommandImpl", M2_ZERO}, {"GameUtils_GetCommandLineSwitch", M2_ZERO},
    {"ThumbnailManager_SetCallbacks", M2_ZERO}, {"DownloadContent_SetSimSavedCallbackImpl", M2_ZERO},
    {"UserToolUtils_SetBuildBuyGenericCallbackImpl", M2_ZERO},
    {"StopWatch_CreateStopwatch", M2_STOPWATCH}, {"StopWatch_DestroyStopwatch", M2_ZERO}, {"StopWatch_StartStopwatch", M2_ZERO},
    {"StopWatch_StopStopwatch", M2_ZERO}, {"StopWatch_ResetStopwatch", M2_ZERO}, {"StopWatch_RestartStopwatch", M2_ZERO},
    {"StopWatch_GetElapsedTimeStopwatch", M2_ZERO}, {"StopWatch_GetElapsedTimeFloatStopwatch", M2_ZERO},
    {"StopWatch_SetElapsedTimeStopwatch", M2_ZERO}, {"StopWatch_SetElapsedTimeFloatStopwatch", M2_ZERO}, {"StopWatch_IsRunningStopwatch", M2_ZERO},
    {"NameGuidMapService_GetGuidByName", M2_GUID}, {"LocalizedStringService_GetLocalizedStringByString", M2_STRING},
    {"LocalizedStringService_GetLocalizedStringByStringKey", M2_STRING}, {"Simulator_LoadXmlAsDocument", M2_XMLDOC},
    {"Random_NextDoubleImpl", M2_RANDOM},
};
static char g_icM2[2048];
static bool g_m2;
static int g_m2Calls[8];
static unsigned g_stopwatches;
static unsigned long long g_rng = 0x5EED5EED12345678ULL;  // fixed seed: runs repeat
static void* g_stringK;  // System.String
static void Narrow(void* s, char* out, int n);

// first of the IC's stack arguments that is a MonoString (vtable +0 -> klass); instance ICs have `this` first
static void* StringArg(const DWORD* a) {
    for (int i = 0; i < 4; i++) {  // ReadProcessMemory: probing raises no first-chance faults (the VEH logs only 20)
        DWORD vt = 0, k = 0;
        if (a[i] > 0x10000 && g_stringK && ReadProcessMemory(GetCurrentProcess(), (void*)(uintptr_t)a[i], &vt, 4, NULL) &&
            ReadProcessMemory(GetCurrentProcess(), (void*)(uintptr_t)vt, &k, 4, NULL) && (void*)(uintptr_t)k == g_stringK)
            return (void*)(uintptr_t)a[i];
    }
    return NULL;
}
// M2 answer for one call: 1 = zero, 3 = g_ret64, 4 = g_retDbl; 0 = no M2 answer (fall through to fail loud)
static int M2Answer(int code, const DWORD* a) {
    g_m2Calls[code]++;
    switch (code) {
    case M2_ZERO: return 1;
    case M2_STOPWATCH: g_ret64 = ++g_stopwatches; return 3;  // distinct non-zero handles; the other StopWatch ICs ignore them
    case M2_GUID: {  // FNV-1a 64 of the name's UTF-16 code units: stable, distinct, non-zero
        void* s = StringArg(a);
        if (!s) return 0;
        unsigned long long h = 0xCBF29CE484222325ULL;
        int len = *(int*)((u8*)s + 4);
        const wchar_t* w = (const wchar_t*)((u8*)s + 8);
        for (int i = 0; i < len; i++) h = (h ^ w[i]) * 0x100000001B3ULL;
        g_ret64 = h ? h : 1;
        return 3;
    }
    case M2_STRING: {  // placeholder: the key string itself (non-null; tests must not compare localized names)
        void* s = StringArg(a);
        g_ret64 = (DWORD)(uintptr_t)s;
        return s ? 3 : 0;
    }
    case M2_XMLDOC: {
        // null = the "resource missing" answer the callers test for; an empty XmlDocument made BlueprintController and
        // BuyController dereference DocumentElement (measured). shortcut: no extracted tuning XML; load it here once M3 needs real tuning
        char name[128] = "";
        Narrow(StringArg(a), name, 120);
        Log("  M2 LoadXmlAsDocument('%s') -> null\n", name);
        g_ret64 = 0;
        return 3;
    }
    case M2_RANDOM:  // seeded 64-bit LCG, top 53 bits -> [0, 1)
        g_rng = g_rng * 6364136223846793005ULL + 1442695040888963407ULL;
        g_retDbl = (double)(g_rng >> 11) * (1.0 / 9007199254740992.0);
        return 4;
    }
    return 0;
}

// a0..a3: the IC's own stack arguments (cdecl, after the thunk's index and the trampoline's return address)
static int __cdecl IcStub(int idx, DWORD, DWORD a0, DWORD a1, DWORD a2, DWORD a3) {
    g_icHits++;
    if (g_icCalls[idx]++ == 0) Log("  IC-STUB first call: %s [%c]\n", g_icNames[idx], g_icKind[idx] ? g_icKind[idx] : '?');
    bool dup = false;
    for (int i = 0; i < g_nStep; i++) dup |= g_step[i] == idx;
    if (!dup && g_nStep < 16) g_step[g_nStep++] = idx;
    char k = g_icKind[idx];
    if (g_icAllow[idx] == 2) { g_ret64 = g_tick; return 3; }
    if (g_icAllow[idx] == 3) { g_ret64 = *(long long*)(g_fakeHost + 0xBD0); return 3; }  // running task id, 0 outside a task
    if (g_m2 && g_icM2[idx]) {
        const DWORD a[4] = {a0, a1, a2, a3};
        int r = M2Answer(g_icM2[idx], a);
        if (r == 1) return k == 'f' || k == 'd' ? 2 : 1;
        if (r) return r;
    }
    if (g_icAllow[idx] == 1 || (g_lenient && k && k != 's')) return k == 'f' || k == 'd' ? 2 : 1;
    char msg[256];
    _snprintf_s(msg, sizeof(msg), _TRUNCATE, "monohost stub: %s", g_icNames[idx]);
    ((RaiseExc)0xEA18F0)(((ExcNotImpl)0xE77360)(msg));  // longjmps back into the interpreter; does not return
    return 1;
}
// thunk: push idx; jmp IcStubEntry. Entry: [esp] = idx, [esp+4] = return into the mint trampoline (cdecl, caller cleans).
// IcStub's answer: 1 = zero in edx:eax, 2 = 0.0 on the x87 stack (float/double ICs), 3 = g_ret64 in edx:eax, 4 = g_retDbl on x87.
static __declspec(naked) void IcStubEntry() {
    __asm {
        call IcStub
        add esp, 4
        cmp eax, 4
        jne notdbl
        fld qword ptr [g_retDbl]
        xor eax, eax
        xor edx, edx
        ret
    notdbl:
        cmp eax, 2
        jne notfp
        fldz
        xor eax, eax
        xor edx, edx
        ret
    notfp:
        cmp eax, 3
        jne zero
        mov eax, dword ptr [g_ret64]
        mov edx, dword ptr [g_ret64 + 4]
        ret
    zero:
        xor eax, eax
        xor edx, edx
        ret
    }
}

static char KindOf(const char* name) {
    for (int i = 0; i < g_nRets; i++) if (!strcmp(g_rets[i], name)) return g_retKinds[i];
    return 0;
}

// Every "ScriptCore.<Class>::<Method>" string in the mapped exe's .rdata/.data is an IC name the providers register in
// the game (docs/62); register a stub for each, typed by the managed return type (hostdata). The 164 game externs
// without an exe name stay unregistered, as in the game (EntryPointNotFoundException at the call).
static int RegisterStubs() {
    static const char pre[] = "ScriptCore.";
    u8* lo = (u8*)(uintptr_t)0x00F95000; u8* hi = (u8*)(uintptr_t)0x01234000;  // .rdata + .data
    u8* code = (u8*)VirtualAlloc(NULL, 2048 * 10, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
    for (u8* p = lo; p < hi - 16 && g_icCount < 2048; p++) {
        if (memcmp(p, pre, 11) || (p > lo && p[-1] >= 0x20 && p[-1] < 0x7F)) continue;  // must start a string
        const char* s = (const char*)p;
        const char* sep = strstr(s, "::");
        size_t n = strnlen(s, 200);
        if (!sep || sep > s + n || n >= 200) continue;
        bool ok = true;
        for (size_t i = 0; i < n && ok; i++) ok = isalnum(p[i]) || p[i] == '.' || p[i] == '_' || p[i] == ':';
        if (!ok) continue;
        u8* t = code + g_icCount * 10;
        t[0] = 0x68; *(int*)(t + 1) = g_icCount;                                      // push idx
        t[5] = 0xE9; *(DWORD*)(t + 6) = (DWORD)(uintptr_t)IcStubEntry - (DWORD)(uintptr_t)(t + 10);  // jmp
        g_icNames[g_icCount] = s;
        g_icKind[g_icCount] = KindOf(s);
        if (strstr(s, "::Simulator_GetCurrentTaskImpl")) g_icAllow[g_icCount] = 3;  // M1.5 task driver's running task (0 = none)
        if (strstr(s, "::Simulator_GetTickCountImpl")) g_icAllow[g_icCount] = 2;
        for (auto& m : kM2) if (!strcmp(sep + 2, m.name)) g_icM2[g_icCount] = m.code;
        ((AddInternalCall)0xE82550)(s, t);
        g_icCount++;
        p += n;
    }
    return g_icCount;
}

static void* Cls(void* img, const char* ns, const char* name) { return img ? ((ClassFromName)0xE6FA90)(img, ns, name) : NULL; }
static void* Meth(void* k, const char* name, int n) { return k ? ((MethodFromNameFlags)0xE70330)(k, name, n, 0) : NULL; }

static void Describe(void* exc, char* type, char* msg, void** inner);
// Invoke and name the exception (type and message through managed calls).
static void* Call(const char* what, void* m, void* obj, void** args) {
    if (!m) { Log("%s: method not found\n", what); return NULL; }
    void* exc = NULL;
    void* r = ((RuntimeInvoke)0xEA1090)(m, obj, args, &exc);
    char t[128] = "none", msg[256] = "";
    void* in = NULL;
    if (exc) Describe(exc, t, msg, &in);
    Log("%s -> ret %p, exception %p (%s%s%s)\n", what, r, exc, t, exc ? ": " : "", msg);
    return exc ? NULL : (r ? r : (void*)1);
}

static void* g_rootObj;  // in the host image's .data: MEM_IMAGE + writable, so Boehm scans it (docs/120 §1.2)

static void* Served(const char* name) {
    for (int j = 0; j < g_nServed; j++) if (!_stricmp(g_served[j], name)) return g_servedImg[j];
    return NULL;
}

static int Probe(void* domain, void* scImg) {
    void* corlib = *(void**)(uintptr_t)0x1220E40;
    // P1: the other assemblies, dependency order; references already loaded on demand through the detour are skipped
    static const char* order[] = {"SimIFace.dll", "System.Xml.dll", "System.dll", "Sims3Metadata.dll", "UI.dll", "Sims3GameplaySystems.dll",
                                  "Sims3GameplayObjects.dll", "Sims3StoreObjects.dll"};
    void* imgs[8] = {};
    for (int i = 0; i < 8; i++) {
        bool served = false;
        for (int j = 0; j < g_nServed; j++) served |= !_stricmp(g_served[j], order[i]);
        if (served) { Log("P1 %s: already loaded through a reference\n", order[i]); continue; }
        int st = -1;
        DWORD t0 = GetTickCount();
        imgs[i] = LoadImageByName(order[i]);
        void* a = imgs[i] ? ((AsmLoadFromFull)0xE68290)(imgs[i], order[i], &st, 0) : NULL;
        Log("P1 %s: assembly %p status %d (%lu ms)\n", order[i], a, st, GetTickCount() - t0);
        if (!a) return 70;
    }
    void* sys = imgs[5] ? imgs[5] : Served("Sims3GameplaySystems.dll");
    void* autonomy = Cls(sys, "Sims3.Gameplay.Autonomy", "Autonomy");
    Log("P1 class Sims3.Gameplay.Autonomy.Autonomy %p\n", autonomy);

    // P2: an EA IC with no registration (ScriptCore.Random has no .cctor): expect a managed exception, not a fault
    void* rnd = Cls(scImg, "ScriptCore", "Random");
    Call("P2 unregistered ScriptCore.Random::Random_NextDoubleImpl", Meth(rnd, "Random_NextDoubleImpl", 0), NULL, NULL);

    // P3: logging stubs for every IC name in the exe, then one stubbed IC (runs ScriptCore.Simulator's .cctor first)
    Log("P3 registered %d IC stubs from the exe's name strings\n", RegisterStubs());
    Call("P3 stubbed ScriptCore.Simulator::Simulator_GetTickCountImpl", Meth(Cls(scImg, "ScriptCore", "Simulator"), "Simulator_GetTickCountImpl", 0), NULL, NULL);

    // P4: yields. Sims3.SimIFace.Simulator.Sleep without the ScriptCore service objects, then with ScriptCore.Simulator built
    void* sifImg = Served("SimIFace.dll");
    void* sif = Cls(sifImg, "Sims3.SimIFace", "Simulator");
    unsigned ticks = 1;
    void* a2[1] = {&ticks};
    void* scSim = Cls(scImg, "ScriptCore", "Simulator");
    void* obj = scSim ? ((ObjectNew)0xEA36F0)(domain, scSim) : NULL;
    Call("P4 new ScriptCore.Simulator()", Meth(scSim, ".ctor", 0), obj, NULL);
    Call("P4 Sims3.SimIFace.Simulator.Sleep(1) outside a task", Meth(sif, "Sleep", 1), NULL, a2);

    // P5: one full collection. Object A is referenced only from host .data, object B only from CRT heap (hidden)
    void* objClass = *(void**)(uintptr_t)(0x1220E40 + 4);
    g_rootObj = ((ObjectNew)0xEA36F0)(domain, objClass);
    DWORD* heapCell = (DWORD*)malloc(4);
    *heapCell = (DWORD)(uintptr_t)((ObjectNew)0xEA36F0)(domain, objClass) ^ 0x5A5A5A5A;
    DWORD vtA = *(DWORD*)g_rootObj, vtB = *(DWORD*)(uintptr_t)(*heapCell ^ 0x5A5A5A5A);
    void* gc = Cls(corlib, "System", "GC");
    bool f = false;
    void* a3[1] = {&f};
    void* m0 = Call("P5 GC.GetTotalMemory(false) before", Meth(gc, "GetTotalMemory", 1), NULL, a3);
    if (m0 && m0 != (void*)1) Log("  heap in use %lld bytes\n", *(long long*)((u8*)m0 + 4));
    for (int i = 0; i < 20000; i++) ((ObjectNew)0xEA36F0)(domain, objClass);  // garbage
    DWORD t0 = GetTickCount();
    Call("P5 GC.Collect()", Meth(gc, "Collect", 0), NULL, NULL);
    Log("  collect took %lu ms\n", GetTickCount() - t0);
    void* m1 = Call("P5 GC.GetTotalMemory(false) after", Meth(gc, "GetTotalMemory", 1), NULL, a3);
    if (m1 && m1 != (void*)1) Log("  heap in use %lld bytes\n", *(long long*)((u8*)m1 + 4));
    DWORD* b = (DWORD*)(uintptr_t)(*heapCell ^ 0x5A5A5A5A);
    Log("P5 object A (host .data root) first word %08lX -> %08lX; object B (CRT heap only) %08lX -> %08lX\n", vtA, *(DWORD*)g_rootObj, vtB, *b);
    Log("P5 %s; %s\n", *(DWORD*)g_rootObj == vtA ? "A kept" : "A LOST", *b == vtB ? "B still intact (not reclaimed or not reused)" : "B reclaimed");
    Log("IC stub calls: %d\n", g_icHits);
    return 0;
}

// ---- M1.2 + M1.3 (docs/119 §10): all assemblies, service objects, typed fail-loud stubs, .cctor sweep ----
typedef void* (__cdecl* ClassGetFull)(void* image, DWORD token, void* ctx);  // mono_class_get_full 0xE6F840 (tail of 0xE6FA90)
static void* g_services[64];  // host .data, so Boehm keeps them (docs/120); the AppDomain holds them too
static const char* g_cAsm[12000]; static int g_cRow[12000]; static const char* g_cName[12000]; static int g_nC;

static void* SafeInvoke(void* m, void* obj, void** args, void** exc, DWORD* code) {
    __try { return ((RuntimeInvoke)0xEA1090)(m, obj, args, exc); }
    __except (*code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER) { return NULL; }
}

// MonoString in this build: vtable +0, length +4, UTF-16 +8 (no sync word, §8 step 6)
static void Narrow(void* s, char* out, int n) {
    out[0] = 0;
    if (!s) return;
    int len = *(int*)((u8*)s + 4), i = 0;
    const wchar_t* w = (const wchar_t*)((u8*)s + 8);
    for (; i < len && i < n - 1; i++) out[i] = w[i] < 0x80 && w[i] >= 0x20 ? (char)w[i] : ' ';
    out[i] = 0;
}
static void* Inv0(void* m, void* obj) {
    void* exc = NULL; DWORD code = 0;
    void* r = m ? SafeInvoke(m, obj, NULL, &exc, &code) : NULL;
    return exc || code ? NULL : r;
}
// "<type>" and "<message>" of a managed exception (GetType().FullName, Exception.Message, InnerException)
static void Describe(void* exc, char* type, char* msg, void** inner) {
    void* corlib = *(void**)(uintptr_t)0x1220E40;
    void* objClass = *(void**)(uintptr_t)(0x1220E40 + 4);
    static void *getType, *fullName, *getMsg, *getInner;
    if (!getType) {
        getType = Meth(objClass, "GetType", 0);
        fullName = Meth(Cls(corlib, "System", "MonoType"), "get_FullName", 0);
        void* ex = Cls(corlib, "System", "Exception");
        getMsg = Meth(ex, "get_Message", 0);
        getInner = Meth(ex, "get_InnerException", 0);
    }
    Narrow(Inv0(fullName, Inv0(getType, exc)), type, 120);
    Narrow(Inv0(getMsg, exc), msg, 200);
    for (char* c = msg; *c; c++) if (*c == ',' || *c == '"') *c = ' ';
    *inner = Inv0(getInner, exc);
}

static bool LoadHostdata() {
    FILE* f = NULL;
    if (fopen_s(&f, g_hostdata, "rb") || !f) { Log("cannot open hostdata %s\n", g_hostdata); return false; }
    char line[512];
    while (fgets(line, sizeof(line), f)) {
        line[strcspn(line, "\r\n")] = 0;
        char a[8], b[256], c[384];
        if (line[0] == 'R' && sscanf_s(line, "R %7s %255s %383s", a, 8, b, 256, c, 384) == 3 && g_nRets < 4096) { g_retKinds[g_nRets] = a[0]; g_rets[g_nRets++] = _strdup(c); }
        int row;
        if (line[0] == 'C' && sscanf_s(line, "C %255s %d %383s", b, 256, &row, c, 384) == 3 && g_nC < 12000) { g_cAsm[g_nC] = _strdup(b); g_cRow[g_nC] = row; g_cName[g_nC++] = _strdup(c); }
    }
    fclose(f);
    Log("hostdata: %d extern return types, %d .cctor types\n", g_nRets, g_nC);
    return g_nRets && g_nC;
}

static int M13(void* domain) {
    void* corlib = *(void**)(uintptr_t)0x1220E40;
    if (!LoadHostdata()) return 100;
    int n = RegisterStubs(), kinds[128] = {}, unknown = 0;
    for (int i = 0; i < n; i++) { kinds[(u8)g_icKind[i]]++; if (!g_icKind[i]) { unknown++; Log("  stub without a managed extern: %s\n", g_icNames[i]); } }
    Log("M1.3 stubs: %d registered (void %d, int/ref %d, int64 %d, float %d, double %d, struct %d, unknown %d); mode %s\n", n, kinds['v'], kinds['i'], kinds['l'], kinds['f'],
        kinds['d'], kinds['s'], unknown, g_lenient ? "lenient (non-struct stubs return typed zeros)" : "strict (fail loud)");
    Check("M1.3-stubs", n >= 1770 && unknown <= 5, "%d IC stubs registered, %d without a managed extern", n, unknown);

    // M1.2 a: the 10 assemblies; references already served on demand are not opened again (image cache)
    static const char* files[] = {"mscorlib.dll", "ScriptCore.dll", "SimIFace.dll", "System.Xml.dll", "System.dll", "Sims3Metadata.dll", "UI.dll",
                                  "Sims3GameplaySystems.dll", "Sims3GameplayObjects.dll", "Sims3StoreObjects.dll"};
    for (const char* fn : files) {
        if (Served(fn)) { Log("M1.2 %s: already loaded\n", fn); continue; }
        int st = -1;
        void* img = LoadImageByName(fn);
        void* a = img ? ((AsmLoadFromFull)0xE68290)(img, fn, &st, 0) : NULL;
        Log("M1.2 %s: image %p assembly %p status %d\n", fn, img, a, st);
        if (!a) return 101;
    }
    int firstC[10] = {};
    for (int i = g_nC - 1; i >= 0; i--) for (int j = 0; j < 10; j++) if (!_stricmp(g_cAsm[i], files[j])) firstC[j] = i + 1;
    for (int j = 0; j < 10; j++) {
        void* k = firstC[j] ? ((ClassGetFull)0xE6F840)(Served(files[j]), 0x02000000 | g_cRow[firstC[j] - 1], NULL) : NULL;
        if (firstC[j]) Log("M1.2 %s: token 0x%08X -> class %p (%s)\n", files[j], 0x02000000 | g_cRow[firstC[j] - 1], k, g_cName[firstC[j] - 1]);
    }
    Log("M1.2 images: %d distinct served, %d cache hits (GAC-path and repeat requests)\n", g_nServed, g_cacheHits);
    Check("M1.2-assemblies", g_nServed >= 10, "%d distinct images served for 10 assemblies", g_nServed);

    // M1.3 a: stub behaviour on one double IC: strict -> NotImplementedException with the name; lenient -> 0.0 from x87
    void* scImg = Served("ScriptCore.dll");
    void* nd = Meth(Cls(scImg, "ScriptCore", "Random"), "Random_NextDoubleImpl", 0);
    bool keep = g_lenient;
    for (int pass = 0; pass < 2; pass++) {
        g_lenient = pass == 1;
        void* exc = NULL; DWORD code = 0;
        void* r = SafeInvoke(nd, NULL, NULL, &exc, &code);
        char t[128] = "", m[256] = ""; void* in = NULL;
        if (exc) Describe(exc, t, m, &in);
        Log("M1.3 Random_NextDoubleImpl %s: ret %p, exception %s '%s', fault 0x%lX\n", pass ? "lenient" : "strict", r, t, m, code);
        if (r) Log("  boxed double %g\n", *(double*)((u8*)r + 4));
        if (pass) Check("M1.3-fp-zero", r && !exc && !code && *(double*)((u8*)r + 4) == 0.0, "lenient double IC returns 0.0 from x87");
        else Check("M1.3-fail-loud", exc && !code && !strcmp(t, "System.NotImplementedException"), "strict IC stub raises %s", t);
    }
    g_lenient = keep;
    if (g_mode == 3 || g_m2) {  // M2 stubs from here on (docs/119 §12): services and the sweep
        g_m2 = true;
        g_stringK = Cls(corlib, "System", "String");
        int n2 = 0;
        for (int j = 0; j < g_icCount; j++) n2 += g_icM2[j] != 0;
        Log("M2 hand-written stubs on: %d IC registrations\n", n2);
    }

    // M1.2 b: the ScriptCore service objects that register themselves with AppDomain.SetData in their .ctor (§9.4).
    // UTFConnector* twins skipped (no exe class names, unnamed ICs); .cctor-registered ones (EventQueueInitializer, StopWatch,
    // RouteManager, Swarm) run in the sweep.
    static const char* svc[] = {"Simulator", "Queries", "SocialFeatures", "EATrace", "Animation", "World", "UIManager", "CacheManager", "ProfilerUtils",
                                "DeviceConfig", "CommandSystem", "NameGuidMapService", "LookAtMgr", "Slots", "CTProductModularObject",
                                "LocalizedStringService", "CASUtils", "ThumbnailManager", "GameUtils", "DownloadContent", "Audio", "Reflection",
                                "EAText", "UserToolUtils", "Random", "LoadSaveManager", "SACS", "Objects", "DebugDraw", "CameraController",
                                "WeatherControl", "VideoRecorder", "DataFactory", "AutomationUtils", "OnlineFeatures", "Sims3.SimIFace.Automation"};
    int ok = 0, i = 0;
    for (const char* s : svc) {
        bool sif = !strncmp(s, "Sims3.", 6);
        void* k = sif ? Cls(Served("SimIFace.dll"), "Sims3.SimIFace", "Automation") : Cls(scImg, "ScriptCore", s);
        void* obj = k ? ((ObjectNew)0xEA36F0)(domain, k) : NULL;
        g_services[i++] = obj;
        g_nStep = 0;
        void* exc = NULL; DWORD code = 0;
        void* ctor = Meth(k, ".ctor", 0);
        if (ctor) SafeInvoke(ctor, obj, NULL, &exc, &code);
        char t[128] = "", m[256] = "", ics[512] = ""; void* in = NULL;
        if (exc) Describe(exc, t, m, &in);
        for (int j = 0; j < g_nStep; j++) { strcat_s(ics, " "); strcat_s(ics, g_icNames[g_step[j]] + 11); }
        bool good = obj && ctor && !exc && !code;
        ok += good;
        Log("M1.2 service %-24s %s%s%s%s\n", s, good ? "ok" : !ctor ? "NO .ctor()" : "FAILED ", t, exc ? ": " : "", m);
        if (g_nStep) Log("    stubs:%s\n", ics);
    }
    Log("M1.2 services: %d of %d constructed without an exception\n", ok, i);
    Check("M1.2-services", ok >= (g_lenient || g_m2 ? 36 : 32), "%d of %d service objects constructed", ok, i);

    // M1.2 c: SimIFace wrappers now reach the stubs instead of a null service
    void* sif = Cls(Served("SimIFace.dll"), "Sims3.SimIFace", "Simulator");
    g_tick = 0x100000000LL + 1234;  // int64 allow-list stub: both halves must arrive
    void* r = Call("M1.3 ScriptCore.Simulator::Simulator_GetTickCountImpl (tick stub = 0x1_000004D2)", Meth(Cls(scImg, "ScriptCore", "Simulator"), "Simulator_GetTickCountImpl", 0), NULL, NULL);
    if (r && r != (void*)1) Log("  boxed long 0x%llX\n", *(long long*)((u8*)r + 4));
    Check("M1.3-int64", r && r != (void*)1 && *(long long*)((u8*)r + 4) == g_tick, "64-bit IC return carries both halves");
    unsigned ticks = 1;
    void* a1[1] = {&ticks};
    Check("M1.2-sleep-gate", !Call("M1.2 Sims3.SimIFace.Simulator.Sleep(1) outside a task", Meth(sif, "Sleep", 1), NULL, a1), "Sleep outside a task throws");
    Call("M1.2 Sims3.SimIFace.Simulator.GetUsedBytes()", Meth(sif, "GetUsedBytes", 0), NULL, NULL);

    // M1.3 b: .cctor sweep through RuntimeHelpers.RunClassConstructor (runs mono_runtime_class_init inside an IC, so a
    // throwing .cctor comes back as a managed TypeInitializationException, and a class already initialised is not re-run)
    void* rcc = Meth(Cls(corlib, "System.Runtime.CompilerServices", "RuntimeHelpers"), "RunClassConstructor", 1);
    void* probeK = Cls(scImg, "ScriptCore", "Simulator");
    int off = 0;
    for (int o = 4; o < 0x200 && !off; o += 4) if (*(void**)((u8*)probeK + o) == probeK && ((u8*)probeK)[o + 6] == 0x12) off = o;
    void* vk = Cls(Served("SimIFace.dll"), "Sims3.SimIFace", "Vector3");
    Log("M1.3 MonoClass.byval_arg at +0x%X (class type byte 0x%02X, Vector3 0x%02X, Vector3 self %d)\n", off, off ? ((u8*)probeK)[off + 6] : 0,
        vk && off ? ((u8*)vk)[off + 6] : 0, vk && off ? *(void**)((u8*)vk + off) == vk : 0);
    if (!rcc || !off) return 102;
    char csvPath[MAX_PATH];
    GetModuleFileNameA(NULL, csvPath, MAX_PATH);
    strcat_s(csvPath, g_lenient ? ".sweep-lenient.csv" : g_m2 ? ".sweep-m2.csv" : ".sweep-strict.csv");
    FILE* csv = NULL;
    fopen_s(&csv, csvPath, "w");
    if (!csv) return 103;
    fprintf(csv, "type,assembly,result,exception,inner,inner_message,ics\n");
    int nOk = 0, nExc = 0, nFault = 0, nGeneric = 0, nIc = 0, hitsBefore = g_icHits;
    DWORD t0 = GetTickCount();
    for (int c = 0; c < g_nC; c++) {
        if (strchr(g_cName[c], '`')) { nGeneric++; continue; }  // open generic definitions: no class init
        void* k = ((ClassGetFull)0xE6F840)(Served(g_cAsm[c]), 0x02000000 | g_cRow[c], NULL);
        if (!k) { fprintf(csv, "%s,%s,noclass,,,,\n", g_cName[c], g_cAsm[c]); continue; }
        void* handle = (u8*)k + off;
        void* args[1] = {&handle};
        g_nStep = 0;
        void* exc = NULL; DWORD code = 0;
        SafeInvoke(rcc, NULL, args, &exc, &code);
        char t[128] = "", m[256] = "", it[128] = "", im[256] = "", ics[1024] = ""; void* in = NULL;
        if (exc) { Describe(exc, t, m, &in); if (in) { void* in2; Describe(in, it, im, &in2); } }
        for (int j = 0; j < g_nStep; j++) { if (j) strcat_s(ics, ";"); strcat_s(ics, g_icNames[g_step[j]]); }
        const char* res = code ? "fault" : exc ? "exception" : g_nStep ? "ok-stubs" : "ok";
        nOk += !code && !exc; nExc += exc != NULL; nFault += code != 0; nIc += g_nStep > 0;
        if (code) Log("M1.3 sweep FAULT 0x%08lX in %s\n", code, g_cName[c]);
        fprintf(csv, "%s,%s,%s,%s,%s,%s,%s\n", g_cName[c], g_cAsm[c], res, t, it, exc ? (in ? im : m) : "", ics);
        if (nFault > 20) { Log("M1.3 sweep: too many faults, stopped at %d\n", c); break; }
    }
    fclose(csv);
    Log("M1.3 sweep (%s): %d types, %d generic skipped, ok %d, exception %d, fault %d, reached a stub %d, stub calls %d, %lu ms -> %s\n",
        g_lenient ? "lenient" : g_m2 ? "strict + M2 stubs" : "strict", g_nC, nGeneric, nOk, nExc, nFault, nIc, g_icHits - hitsBefore, GetTickCount() - t0, csvPath);
    int distinct = 0;
    for (int j = 0; j < g_icCount; j++) distinct += g_icCalls[j] > 0;
    Log("IC stubs called: %d distinct, %d calls in total\n", distinct, g_icHits);
    Check("M1.2-image-cache", g_cacheHits > 0 && g_nServed == 10, "%d GAC-path/repeat image requests answered from the cache, still %d images", g_cacheHits, g_nServed);
    Check("M1.3-sweep", nFault == 0 && nOk >= 9700, "%d .cctor types ok, %d exceptions, %d faults", nOk, nExc, nFault);
    if (g_m2) {
        // M2 goal: the autonomy entry types initialise with strict stubs plus the hand-written ones (re-run = already done or rethrow)
        Log("M2 stub answers: zero %d, stopwatch %d, guid %d, string %d, xmldoc %d, random %d\n", g_m2Calls[M2_ZERO], g_m2Calls[M2_STOPWATCH],
            g_m2Calls[M2_GUID], g_m2Calls[M2_STRING], g_m2Calls[M2_XMLDOC], g_m2Calls[M2_RANDOM]);
        static const char* aut[] = {"Autonomy", "Motives", "AutonomyTuning"};
        int good = 0;
        for (const char* an : aut) {
            void* k = Cls(Served("Sims3GameplaySystems.dll"), "Sims3.Gameplay.Autonomy", an);
            void* handle = k ? (u8*)k + off : NULL;
            void* args[1] = {&handle};
            void* exc = NULL; DWORD code = 0;
            if (k) SafeInvoke(rcc, NULL, args, &exc, &code);
            char t[128] = "", m[256] = ""; void* in = NULL;
            if (exc) Describe(exc, t, m, &in);
            Log("M2 Sims3.Gameplay.Autonomy.%s: %s%s%s\n", an,k && !exc && !code ? "initialised" : "FAILED ", t, m);
            good += k && !exc && !code;
        }
        Check("M2-autonomy-cctors", good == 3, "%d of 3 (Autonomy, Motives, AutonomyTuning) initialise with strict + M2 stubs", good);
        Check("M2-sweep", nFault == 0 && nExc <= 20, "strict + M2 sweep: %d ok, %d exceptions, %d faults", nOk, nExc, nFault);
    }
    return 0;
}

// ---- M1.4 (docs/119 §11.1): host tick = full collection + finalizer drain; GC handles, finalizers, bounded heap ----
typedef void(__cdecl* GcCollect)(int generation);  // mono_gc_collect 0xE69650 -> GC_gcollect 0xE4A0B0
typedef int(__cdecl* GcInvokeFinalizers)();        // mono_gc_invoke_finalizers 0xE697F0 -> GC_invoke_finalizers 0xE4B3F0
typedef long long(__cdecl* GcUsedSize)();          // mono_gc_get_used_size 0xE69670 (heap size - free bytes)
static volatile DWORD* const GC_GC_NO = (DWORD*)(uintptr_t)0x11F2F90;     // GC_gc_no (mono_gc_collection_count 0xE69660)
static volatile DWORD* const GC_HEAPSIZE = (DWORD*)(uintptr_t)0x12225A0;  // GC_heapsize (GC_get_heap_size 0xE4DD30)
static int g_finRun;
static void HostTickGc() {
    ((GcCollect)0xE69650)(0);
    g_finRun += ((GcInvokeFinalizers)0xE697F0)();
}

static void* g_testK;  // MonoHostTest.HostTest
static void* g_callK;  // MonoHostTest.CallTest (docs/139 §8)
static void* g_asK;    // MonoHostTest.AsDriver (G-A2)
// static test method -> boxed result, (void*)1 for void, NULL on a managed exception or fault (logged)
static void* T(const char* name, int n, void** args) {
    void* m = Meth(g_testK, name, n);
    if (!m) { Log("test method %s/%d not found\n", name, n); return NULL; }
    void* exc = NULL; DWORD code = 0;
    void* r = SafeInvoke(m, NULL, args, &exc, &code);
    if (exc || code) {
        char t[128] = "", msg[256] = ""; void* in = NULL;
        if (exc) Describe(exc, t, msg, &in);
        Log("%s: exception %s '%s', fault 0x%lX\n", name, t, msg, code);
        return NULL;
    }
    return r ? r : (void*)1;
}
static int I32(void* boxed) { return boxed && boxed != (void*)1 ? *(int*)((u8*)boxed + 4) : INT_MIN; }

static int M14() {
    int st = -1;
    void* img = LoadImageByName("MonoHostTest.dll");
    void* a = img ? ((AsmLoadFromFull)0xE68290)(img, "MonoHostTest.dll", &st, 0) : NULL;
    g_testK = Cls(img, "MonoHostTest", "HostTest");
    g_asK = Cls(img, "MonoHostTest", "AsDriver");
    g_callK = Cls(img, "MonoHostTest", "CallTest");
    if (!Check("M1.4-testasm", a && g_testK, "MonoHostTest.dll assembly %p status %d, class %p", a, st, g_testK)) return 140;
    DWORD gc0 = *GC_GC_NO;
    int n = 2000, s1 = 3, s2 = 4, churn = 2000, fin = 50, ticks = 300, badGraph = 0;
    void* ab[2] = {&n, &s1};
    int h = I32(T("BuildGraph", 2, ab));  // the graph's only root is this handle (an int, so no conservative pointer)
    void* aw[2] = {&n, &s2};
    int w = I32(T("BuildWeak", 2, aw));
    void* ac[1] = {&churn};
    void* af[1] = {&fin};
    void* ag[3] = {&h, &n, &s1};
    DWORD heap50 = 0, heapMax = 0;
    long long used50 = 0, usedMax = 0;
    DWORD t0 = GetTickCount();
    for (int t = 1; t <= ticks; t++) {
        g_tick++;
        T("Churn", 1, ac);           // ~2000 * (node + 8-slot array + 16-char string) of garbage per tick
        T("MakeFinalizable", 1, af);
        HostTickGc();
        long long used = ((GcUsedSize)0xE69670)();
        if (t == 50) { heap50 = *GC_HEAPSIZE; used50 = used; }
        if (t > 50) { heapMax = max(heapMax, *GC_HEAPSIZE); usedMax = max(usedMax, used); }
        if (t % 50 == 0) {
            int bad = I32(T("CheckGraph", 3, ag));
            badGraph += bad != 0;
            Log("M1.4 tick %d: GC_gc_no %lu, heap %lu KB, used %lld KB, graph errors %d, finalizers run %d\n", t, *GC_GC_NO, *GC_HEAPSIZE >> 10, used >> 10, bad, g_finRun);
        }
    }
    Log("M1.4 %d ticks in %lu ms\n", ticks, GetTickCount() - t0);
    DWORD gcN = *GC_GC_NO - gc0;
    int managed = I32(T("Collections", 0, NULL));
    Check("M1.4-collections", gcN >= (DWORD)ticks, "%lu collections in %d host ticks (GC_gc_no [0x11F2F90])", gcN, ticks);
    Check("M1.4-gc-no", managed == (int)*GC_GC_NO, "GC.CollectionCount(0) %d == GC_gc_no %lu", managed, *GC_GC_NO);
    Check("M1.4-handle-graph", h != INT_MIN && badGraph == 0, "2000-node graph held only by a GC handle intact at %d checks", ticks / 50);
    void* ai[1] = {&w};
    void* alive = T("IsAlive", 1, ai);
    Check("M1.4-weak", alive && alive != (void*)1 && !*((u8*)alive + 4), "graph held only by a weak handle was collected");
    int finalized = I32(T("FinalizedCount", 0, NULL));
    Check("M1.4-finalizers", finalized >= fin * ticks * 9 / 10 && g_finRun >= finalized, "%d of %d finalizable objects finalized, %d finalizers run by the host",
          finalized, fin * ticks, g_finRun);
    Check("M1.4-heap-bounded", heapMax <= heap50 + heap50 / 4 && usedMax <= used50 + used50 / 4 + (1 << 20),
          "heap %lu KB at tick 50, max %lu KB after; used %lld KB at tick 50, max %lld KB after", heap50 >> 10, heapMax >> 10, used50 >> 10, usedMax >> 10);
    void* ah[1] = {&h};
    T("Free", 1, ah);
    T("Free", 1, ai);
    return 0;
}

// ---- M1.5 (docs/119 §11.2): task driver. Fake MonoScriptHost at [0x11EE514], task states 0xE52D70, resume 0xE59FD0 ----
typedef void* (__cdecl* TaskNew)(void* method, void* obj, void** args, void** exc);  // 0xE52D70: GC_malloc(0x150) state
typedef void(__cdecl* TaskRun)(void* state);                                        // 0xE59FD0: run or resume to yield/end
typedef int(__cdecl* TaskYielded)(void* state);                                     // 0xE52B40: state+0x149
typedef void(__cdecl* TaskFree)(void* state);                                       // 0xE53E80
struct HostTask { unsigned long long id; void* state; void* exc; void* result; long long wake; int runs; DWORD fault; bool done; };
static HostTask g_tasks[4];  // host .data: the GC_malloc'ed states and their frames stay reachable (docs/120 roots)
static void* g_fakeVtbl[64];
static void* g_fakeVtbl2[128];
static unsigned g_sleeps[64];
static int g_nSleep;

// Boehm's root sets after GC_register_dynamic_libraries (GC_static_roots 0x122FB14, 12-byte entries; count [0x11F3084]).
// Measured: the host exe's .data is not among them, so a pointer kept only there does not keep a GC block alive.
static bool InRootSets(const void* p) {
    int n = *(int*)(uintptr_t)0x11F3084;
    DWORD* r = (DWORD*)(uintptr_t)0x122FB14;
    bool in = false;
    for (int i = 0; i < n; i++, r += 3) {
        in |= (DWORD)(uintptr_t)p >= r[0] && (DWORD)(uintptr_t)p < r[1];
        Log("  GC root set %d: 0x%08lX-0x%08lX\n", i, r[0], r[1]);
    }
    return in;
}
// the host's own roots: pushed from Boehm's GC_push_other_roots hook [0x11949CC] (0xE4EF90 -> 0xD71820 pushes the
// collecting thread's stack) with GC_push_all_stack 0xE4CEE0, the push 0xD71820 uses
typedef void(__cdecl* PushRange)(void* lo, void* hi);
static void(__cdecl* g_pushOtherOrig)();
static void __cdecl PushHostRoots() {
    g_pushOtherOrig();
    ((PushRange)0xE4CEE0)(g_tasks, g_tasks + 4);
}

static void __cdecl FakeTrap(int slot) {  // every vtable slot outside the contract: fail loud
    Log("FAIL fake host %s vtable slot +0x%X called (outside the M1.5 contract)\n", slot >= 1000 ? "host+8" : "main", (slot % 1000) * 4);
    TerminateProcess(GetCurrentProcess(), 90);
}
// main vtable +0x20 (thiscall, ticks; ret 4), what the yield opcode reaches through 0xD70820. The game's 0xD77B20 files
// (task id host+0xBD0, ticks) in its sleep map, then marks the yield through the host+8 vtable +0x11C (0xD70F60).
static bool __fastcall FakeSleep(u8* host, void*, unsigned ticks) {
    unsigned long long id = *(unsigned long long*)(host + 0xBD0);
    for (HostTask& t : g_tasks)
        if (t.state && t.id == id) t.wake = g_tick + (ticks ? ticks : 1);  // a task runs at most once per host tick
    if (g_nSleep < 64) g_sleeps[g_nSleep++] = ticks;
    ((void(__fastcall*)(void*, void*))0xD70F60)(host + 8, NULL);  // EA mark-yield: state+0x149 = 1 on *[host+0xBC8]
    return false;
}
static void InstallFakeHost() {
    u8* code = (u8*)VirtualAlloc(NULL, 192 * 10, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
    for (int i = 0; i < 192; i++) {
        u8* t = code + i * 10;
        t[0] = 0x68; *(int*)(t + 1) = i < 64 ? i : 1000 + i - 64;                                      // push slot
        t[5] = 0xE8; *(DWORD*)(t + 6) = (DWORD)(uintptr_t)FakeTrap - (DWORD)(uintptr_t)(t + 10);  // call FakeTrap
        (i < 64 ? g_fakeVtbl[i] : g_fakeVtbl2[i - 64]) = t;
    }
    g_fakeVtbl[0x20 / 4] = (void*)FakeSleep;
    g_fakeVtbl2[0x11C / 4] = (void*)(uintptr_t)0xD70F60;
    *(void**)g_fakeHost = g_fakeVtbl;
    *(void**)(g_fakeHost + 8) = g_fakeVtbl2;
    *(void**)(uintptr_t)0x11EE514 = g_fakeHost;
}
// one run or resume, like ScriptHost_RunTask 0xD7F000; true when the task ended (returned, threw or faulted)
static bool RunTask(HostTask& t) {
    void* cur = *(void**)(uintptr_t)0x11F4170;  // innermost interpreter state; the outermost run must restore it
    *(void***)(g_fakeHost + 0xBC8) = &t.state;
    *(unsigned long long*)(g_fakeHost + 0xBD0) = t.id;
    DWORD code = 0;
    __try { ((TaskRun)0xE59FD0)(t.state); }
    __except (code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER) {}
    *(void**)(g_fakeHost + 0xBC8) = NULL;
    *(unsigned long long*)(g_fakeHost + 0xBD0) = 0;
    t.runs++;
    if (*(void**)(uintptr_t)0x11F4170 != cur) {  // seen when a managed exception leaves the task: the interpreter longjmps past its restore
        Log("  task %llX: interpreter state %p left current, restored to %p\n", t.id, *(void**)(uintptr_t)0x11F4170, cur);
        *(void**)(uintptr_t)0x11F4170 = cur;
    }
    if (code) { Log("  task %llX: FAULT 0x%08lX\n", t.id, code); t.fault = code; t.done = true; return true; }
    if (((TaskYielded)0xE52B40)(t.state)) return false;
    t.result = *(void**)((u8*)t.state + 0x20);  // boxed return value, as runtime_invoke 0xE5A600 reads it
    t.done = true;
    ((TaskFree)0xE53E80)(t.state);
    t.state = NULL;
    return true;
}

static void FakeHostOnce() {  // M1.5 and the docs/139 §8 task test (which runs first)
    static bool done;
    if (done) return;
    done = true;
    InstallFakeHost();
    g_pushOtherOrig = *(void(__cdecl**)())(uintptr_t)0x11949CC;
    *(void**)(uintptr_t)0x11949CC = (void*)PushHostRoots;  // without it the task state was collected before the 2nd resume
}
static int M15() {
    FakeHostOnce();
    bool inRoots = InRootSets(g_tasks);
    Log("M1.5 host .data (g_tasks %p) inside a Boehm root set: %d\n", g_tasks, inRoots);
    void* sleepLoop = Meth(g_testK, "SleepLoop", 1);
    // (a) runtime_invoke'd code cannot yield: its state has +0x148 clear, so the Sleep intrinsic throws
    int one = 1;
    void* a1[1] = {&one};
    void* exc = NULL; DWORD code = 0;
    SafeInvoke(sleepLoop, NULL, a1, &exc, &code);
    char t[128] = "", msg[256] = ""; void* in = NULL;
    if (exc) Describe(exc, t, msg, &in);
    Check("M1.5-invoke-no-yield", exc && !code && !strcmp(t, "System.NotSupportedException"), "Sleep under runtime_invoke throws %s '%s'", t, msg);
    int base = I32(T("GetSteps", 0, NULL));

    // (b) SleepLoop(5) as a task: sleeps 1..5 ticks, checks a local graph after each resume, host GC every tick
    static int five = 5;
    static void* targs[1] = {&five};
    HostTask& k = g_tasks[0];
    k.id = 0x0000ABCD00000011ULL;
    k.state = ((TaskNew)0xE52D70)(sleepLoop, NULL, targs, &k.exc);
    k.wake = g_tick;
    long long t0 = g_tick;
    int runAt[16], stepsAt[16], nRuns = 0;
    for (int i = 0; i < 40 && !k.done; i++, g_tick++) {
        if (k.wake <= g_tick) {
            RunTask(k);
            if (nRuns < 16) { runAt[nRuns] = (int)(g_tick - t0); stepsAt[nRuns++] = I32(T("GetSteps", 0, NULL)) - base; }
        }
        HostTickGc();  // collections between resumes: the yielded frames must survive them
    }
    char runs[256] = "";
    for (int i = 0; i < nRuns; i++) _snprintf_s(runs + strlen(runs), sizeof(runs) - strlen(runs), _TRUNCATE, " %d:%d", runAt[i], stepsAt[i]);
    Log("M1.5 SleepLoop(5) task runs (tick offset:steps):%s; sleeps", runs);
    for (int i = 0; i < g_nSleep; i++) Log(" %u", g_sleeps[i]);
    Log("; result %p exc %p fault 0x%lX\n", k.result, k.exc, k.fault);
    static const int wantAt[6] = {0, 1, 3, 6, 10, 15}, wantSteps[6] = {1, 2, 3, 4, 5, 1005};
    bool sched = nRuns == 6 && g_nSleep == 5;
    for (int i = 0; i < 6 && sched; i++) sched = runAt[i] == wantAt[i] && stepsAt[i] == wantSteps[i] && (i == 5 || g_sleeps[i] == (unsigned)i + 1);
    Check("M1.5-yield-resume", sched && k.done && !k.exc && !k.fault, "5 yields, resumed at ticks 1, 3, 6, 10, 15 with loop state intact");
    Check("M1.5-result", I32(k.result) == base + 1005, "task return value %d (want %d, locals and a 50-node graph checked after every resume)", I32(k.result), base + 1005);
    bool ids = true;
    for (int i = 0; i < 5; i++) {
        int ii = i;
        void* ai[1] = {&ii};
        void* r = T("GetSeenTask", 1, ai);
        ids &= r && r != (void*)1 && *(unsigned long long*)((u8*)r + 4) == k.id;
    }
    Check("M1.5-current-task", ids, "Simulator.CurrentTask inside the task = driver's id 0x%llX (64-bit)", k.id);
    Check("M1.5-host-fields", !*(void**)(g_fakeHost + 0xBC8) && !*(unsigned long long*)(g_fakeHost + 0xBD0), "running-task fields cleared between runs");

    // (c) a yield inside a .cctor frame is refused (0xE53550): the task ends with an exception instead of sleeping
    HostTask& c = g_tasks[1];
    c.id = 0x0000ABCD00000022ULL;
    c.state = ((TaskNew)0xE52D70)(Meth(g_testK, "SleepInCctor", 0), NULL, NULL, &c.exc);
    int sleepsBefore = g_nSleep;
    bool ended = RunTask(c);
    t[0] = msg[0] = 0;
    char it[128] = "", im[256] = "";
    if (c.exc) { Describe(c.exc, t, msg, &in); if (in) { void* in2; Describe(in, it, im, &in2); } }
    Log("M1.5 SleepInCctor task: ended %d, exception %s (inner %s '%s')\n", ended, t, it, im);
    Check("M1.5-cctor-no-yield", ended && c.exc && g_nSleep == sleepsBefore && !strcmp(it, "System.NotSupportedException"), "yield in a .cctor frame throws %s / %s", t, it);
    return 0;
}

// ---- M5 offline proof (docs/120 §7): the real [MonoThread] M5 code (P1 + P2, P3 GC lock, P4 frame TLS, worker entry) ----
// Linked unchanged from native/ts3native: monothread.cpp includes monothread-m5.cpp. The namespace gives it the four
// ts3native helpers and the counter fields it uses; nothing in ts3native is modified.
namespace mt {
#pragma warning(push)
#pragma warning(disable : 4505)  // ts3native helpers the M5 code does not call here
static DWORD g_mainTid;
struct Ts3nCounters { LONG mtMode, mtReady, mtSimTid, mtSimRegistered, mtThreads, mtPushCalls, mtCtxFails, mtStacksPushed; };
static Ts3nCounters ts3native_counters;
static DWORD* g_curState = (DWORD*)(uintptr_t)0x011F4170;  // ts3native's current-state pointer, repointed by MtInstallP4
static void Log(const char* fmt, ...) {
    char buf[512];
    va_list a;
    va_start(a, fmt);
    _vsnprintf_s(buf, sizeof(buf), _TRUNCATE, fmt, a);
    va_end(a);
    ::Log("  [mt] %s\n", buf);
}
static bool Readable(const void* p, size_t n) {
    MEMORY_BASIC_INFORMATION mbi;
    if (!VirtualQuery(p, &mbi, sizeof(mbi)) || mbi.State != MEM_COMMIT || (mbi.Protect & (PAGE_NOACCESS | PAGE_GUARD))) return false;
    return (const u8*)p + n <= (const u8*)mbi.BaseAddress + mbi.RegionSize;
}
static bool Matches(const void* addr, const u8* expect, size_t n) { return Readable(addr, n) && memcmp(addr, expect, n) == 0; }
static bool Write(void* addr, const void* bytes, size_t n) {
    DWORD old;
    if (!VirtualProtect(addr, n, PAGE_EXECUTE_READWRITE, &old)) return false;
    memcpy(addr, bytes, n);
    VirtualProtect(addr, n, old, &old);
    FlushInstructionCache(GetCurrentProcess(), addr, n);
    return true;
}
// ts3native pieces the included files call (monothread-m5 arena guard detours, autshadow FPU state, locowin shims): host no-ops
static void* InstallDetour(const char*, DWORD, const u8*, size_t, void*) { return NULL; }
#include "../ts3native/simloop.cpp"  // the per-iteration hook ApplyMonoThread adds (the host never runs Simulate)
#include "../ts3native/monothread.cpp"
// [AutShadow] (docs/122 §13-§14) on top of M5: routesnap's pieces it touches, as no-ops (no routing in the host)
typedef char(__cdecl* SnObjRegFn)(DWORD id, void* fn, LONG priority);
static SnObjRegFn g_snObjReg;
static bool SnObjListed(DWORD, void*) { return true; }
static const int SN_IC_N = 1, SN_WEV = 0;
static void (*const kSnIcShims[SN_IC_N])() = {NULL};
static void (*const kHxShims[7])() = {NULL, NULL, NULL, NULL, NULL, NULL, NULL};  // locowin.cpp's H2Ic shims: none in the host
static unsigned short GetX87() { unsigned short cw; __asm { fnstcw cw } return cw; }  // as ts3native.cpp
static void SetX87(unsigned short cw) { __asm { fldcw cw } }
static void* g_snJmp[1];
// [MonoFast] CallPath (docs/139 §8): before autshadow.cpp, whose Satb chains over our callvirt entry
struct McCtr { LONG mcSwitch, mcReady, mcCall, mcCallvirt, mcRet, mcBailCall, mcBailCallvirt, mcBailRet, mcVerified, mcMismatch, mcVSkipped, mcVerifyEvery, mcFirstBad; };
static McCtr g_mcCtr;
#define MC_CTR g_mcCtr
#include "../ts3native/mintcall.cpp"
#include "../ts3native/autshadow.cpp"
static int AsTierOf(AsU64) { return 1; }  // unseen.cpp's tier rule (TierMs is 0 in the host: never asked)
#pragma warning(pop)
}  // namespace mt

// ---- 0xD8A8E0 (ScriptHost_NativeToManaged) entry counter: which thread enters it, for F1 (docs/122 §12.5, docs/120 §9.4).
// Installed after M5's first tick, in front of whatever is at 0xD8A8E0 then (the game's bytes, or ts3native's F1 detour).
static volatile LONG g_n2mMain, g_n2mWorker;
static void* g_n2mNext;
static void __cdecl N2mCount() {
    const bool w = mt::g_mtWorkerTid && GetCurrentThreadId() == mt::g_mtWorkerTid;
    InterlockedIncrement(w ? &g_n2mWorker : &g_n2mMain);
}
static __declspec(naked) void N2mHook() {
    __asm {
        pushad
        pushfd
        call N2mCount
        popfd
        popad
        jmp dword ptr [g_n2mNext]
    }
}
static bool InstallN2mCounter() {
    static const u8 orig[5] = {0x8B, 0x44, 0x24, 0x18, 0x53};  // mov eax,[esp+0x18]; push ebx
    u8* f = (u8*)(uintptr_t)0xD8A8E0;
    u8* t = (u8*)VirtualAlloc(NULL, 16, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
    if (f[0] == 0xE9) {  // F1 detour: continue at its target
        g_n2mNext = f + 5 + *(LONG*)(f + 1);
    } else if (!memcmp(f, orig, 5)) {
        memcpy(t, orig, 5);
        t[5] = 0xE9, *(LONG*)(t + 6) = (LONG)((f + 5) - (t + 10));
        g_n2mNext = t;
    } else {
        return false;
    }
    return Jmp(0xD8A8E0, (const void*)N2mHook);
}

static volatile DWORD* const N2M_CURSOR = (DWORD*)(uintptr_t)0x11EE5A4;
// F1 test path: an internal call of our test class that enters the interpreter through 0xD8A8E0, the way EA's binder
// bodies (0xE3E570 family, docs/122 §12.1) do: MinLoop(n) on a call record, with this thread's ThreadContext.
typedef void(__cdecl* N2mFn)(void* rm, void* self, void* args, void* ret, void** exc, void* ctx);
typedef void*(__cdecl* RuntimeMethodFn)(void* method);
static void* g_n2mRm;  // RuntimeMethod of HostTest.MinLoop(int)
static int __cdecl HostN2mMinLoop(int n) {
    unsigned long long args[1] = {(unsigned)n}, ret[1] = {0};  // mint stackvals (8 bytes)
    void* exc = NULL;
    void* ctx = TlsGetValue(*(DWORD*)(uintptr_t)0x11F4160);
    ((N2mFn)(uintptr_t)0xD8A8E0)(g_n2mRm, NULL, args, ret, &exc, ctx);
    return exc ? -1000000 : (int)ret[0];
}

// chunks that sit twice in EA's frame arena pool [0x11EE508] (docs/120 §9): node +4 next, +8 chunk
static int PoolDups() {
    const DWORD* s = *(const DWORD**)(uintptr_t)0x11EE508;
    int n = 0, dup = 0;
    static DWORD seen[4096];
    for (const DWORD* x = s ? (const DWORD*)(uintptr_t)s[1] : NULL; x && x != s && n < 4096; x = (const DWORD*)(uintptr_t)x[1]) {
        for (int j = 0; j < n; j++) dup += seen[j] == x[2];
        seen[n++] = x[2];
    }
    return dup;
}

// one worker job; the main thread runs `mainWork` (allocation + a forced collection each pass) until the job ends
static int WorkerJob(const char* name, void** args, int (*mainWork)(), int* passes, DWORD* ms) {
    void* m = Meth(g_testK, name, 1);
    LONG s0 = mt::ts3native_mtprobe.seq;
    DWORD t0 = GetTickCount();
    if (!m || !mt::MtSubmit(m, NULL, args)) { Log("M5 %s: submit refused (state %ld)\n", name, mt::ts3native_mtprobe.workerState); return -1; }
    int bad = 0;
    *passes = 0;
    while (mt::ts3native_mtprobe.seq == s0 && GetTickCount() - t0 < 60000) { bad += mainWork(); (*passes)++; }
    *ms = GetTickCount() - t0;
    if (mt::ts3native_mtprobe.seq == s0) { Log("M5 %s: no result after 60 s (worker state %ld)\n", name, mt::ts3native_mtprobe.workerState); return -2; }
    return bad;
}
static void LogWorkerError(const char* job) {
    void* e = T("GetLastError", 0, NULL);
    char buf[200] = "";
    if (e && e != (void*)1) Narrow(e, buf, 200);
    if (buf[0]) Log("M5 %s: worker exception %s\n", job, buf);
}
static int MainIdle() { Sleep(1); return 0; }
static int MainChurnGc() {
    static int churn = 1500;
    void* ac[1] = {&churn};
    T("Churn", 1, ac);
    HostTickGc();
    return 0;
}
static int MainN2mLoop() {
    static int n = 5000;
    void* an[1] = {&n};
    return I32(T("MainN2m", 1, an)) != 0;
}
static int MainMinLoop() {
    static int n = 5000;
    void* an[1] = {&n};
    int bad = I32(T("MinLoop", 1, an));
    return bad != 0;
}

// ---- G-A2 (docs/122 §6.1 T3, §14.4): [AutShadow] part 2 on the M5 worker with both interpreters running ----
// Host fakes with the game's IC layouts (docs/122 §13.2) sit in a provider table in this image; ts3native's guard
// install puts its thunks in the table and the host registers the thunks under MonoHostTest.AsIc names. The main
// thread plays the sim thread: MtTick = top of Simulate (window close), MtSimLeave = its return (cut, reference with
// the RNG restored, worker submit, finish). AsDriver (HostTest.cs) is the synthetic ScoreMeta.
static void* g_hostLotArr;   // the lot's object array (HostTest keeps it alive too)
static DWORD g_hostKeyMask;  // the world name key, masked: no root but the snapshot's rooted column
static void __cdecl HostSetLotObjects(void* a) { g_hostLotArr = a; }
static void __cdecl HostSetNameKey(void* s) { g_hostKeyMask = (DWORD)(uintptr_t)s ^ 0x5A5A5A5A; }
static float* __cdecl HAsPos(float* ret, DWORD lo, DWORD) { ret[0] = (float)(lo % 13) * 1.5f, ret[1] = 0.25f, ret[2] = (float)(lo % 5); return ret; }
static unsigned long long __cdecl HAsLotLoc(float x, float y, float z, DWORD* out, DWORD room) {
    out[0] = (DWORD)(int)(x * 2), out[1] = (DWORD)(int)z, out[2] = ((DWORD)(int)(y * 4) & 0xFF) | ((room + 3) << 16);
    return 0x700000000ull + 1 + (DWORD)(int)x % 3;
}
static void* __cdecl HAsOnLot(void*, DWORD lotLo, DWORD, DWORD) { return lotLo ? g_hostLotArr : NULL; }
static float __cdecl HAsSunrise() { return 6.5f; }
static void* __cdecl HAsNameKey() { return (void*)(uintptr_t)(g_hostKeyMask ^ 0x5A5A5A5A); }
static void* __cdecl HAsMoney(DWORD, DWORD) { return mt::g_asEmptyStr; }  // the tooltip stub's answer, so scores can match
// stands for MonoScriptHost: its script MT at +0xC30, initialised and drawn by the game's own code (0x4FA990 / 0x4FAA70,
// docs/125 §2), so the per-unit streams of the reference and the worker run the real generator
static u8 g_hostRngHost[0x1800];
static u8* g_hostRngHostPtr = g_hostRngHost;
static double __cdecl HAsRand() { return ((double(__fastcall*)(void*, void*))0x4FAA70)(g_hostRngHost + 0xC30, NULL); }
static void* g_asHostTab[] = {
    (void*)HAsPos, (void*)"ScriptCore.Objects::Objects_GetPosition",
    (void*)HAsLotLoc, (void*)"ScriptCore.World::World_GetLotLocationImpl",
    (void*)HAsOnLot, (void*)"ScriptCore.Queries::Query_GetObjectsOnLot",
    (void*)HAsSunrise, (void*)"ScriptCore.World::World_GetSunriseTime",
    (void*)HAsNameKey, (void*)"ScriptCore.World::World_GetWorldNameKey",
    (void*)HAsMoney, (void*)"ScriptCore.EAText::EAText_GetMoneyString",
    (void*)HAsRand, (void*)"ScriptCore.Random::Random_NextDoubleImpl",
    NULL, NULL};
static const char* const kAsHostNames[] = {"Objects_GetPosition", "World_GetLotLocationImpl", "Query_GetObjectsOnLot", "World_GetSunriseTime",
                                           "World_GetWorldNameKey", "EAText_GetMoneyString", "Random_NextDoubleImpl"};
static void* TA(const char* name, int n, void** args) {
    void* s = g_testK;
    g_testK = g_asK;
    void* r = T(name, n, args);
    g_testK = s;
    return r;
}
static int AsGet(int what) { void* a[1] = {&what}; return I32(TA("Get", 1, a)); }

// ---- docs/206 §7 Satb offline proof: the worker scores while this ("sim") thread runs the next tick's script, which mutates
// the managed world the score reads (every store kind, List/Dictionary/Array.Copy/Clear/BlockCopy). Satb=2: A0 for every
// object (bit-equal to the reference run at the cut); negative control (no barrier): the same flow must show A1-A3.
static int AsSatbRounds(int rounds, int* seed, int* concRounds, int* mutations) {
    int bad = 0;
    *concRounds = 0, *mutations = 0;
    for (int r = 0; r < rounds; r++) {
        int id = 20000 + r;
        void* a[1] = {&id};
        if (I32(TA("Request", 1, a)) != 1) { Log("AS satb: request %d refused\n", id); return -1; }
        mt::MtSimLeave();  // cut, reference, Satb on, submit
        mt::MtTick();      // next Simulate entry: Satb act keeps the job running
        int during = 0;
        for (int s = 0; s < 200000 && mt::g_mtBusy; s++) {
            void* m[1] = {seed};
            TA("MainMutate", 1, m);
            (*seed)++, during++;
            if ((s & 7) == 7) MainChurnGc();  // collections while the job runs: the log's roots keep the cut objects alive
        }
        *mutations += during;
        *concRounds += during > 0;
        for (int s = 0; s < 1000 && mt::g_asRun != 0; s++) mt::MtSimLeave();  // finish (compare) once the job left
        bad += mt::g_asRun != 0;
    }
    return bad;
}
static int AsSatb() {
    mt::Ts3nAutShadow& k = mt::ts3native_autshadow;
    const int n = I32(TA("SatbSetup", 0, NULL));
    const int mode = mt::SatbInstall(2);
    if (!Check("AS-satb-install", n == 64 && mode == 2 && mt::g_satbRtSwapped >= 0, "SatbWorld %d objects, Satb mode %d (act), IC rows %d", n, mode,
               mt::g_satbIcMapped))
        return 170;
    // every opcode runs once on this thread first (transform, class init, vtables)
    int seed = 7, conc = 0, muts = 0;
    void* w[1] = {&seed};
    TA("MainMutate", 1, w);
    TA("SatbScoreNow", 1, w);
    static int rounds = 120;
    TA("ResetCounts", 0, NULL);
    const LONG hits0 = k.satbWHits, loads0 = k.satbWLoads, words0 = k.satbWords, give0 = k.satbWGiveUp, conf0 = k.satbWConflict,
               rt0 = k.satbRtCalls, fix0 = k.satbWRtFix, conc0 = k.satbConc, stale0 = k.satbStale, waits0 = k.satbWaits, ign0 = k.satbWArenaIgn,
               st0 = k.satbStores, fset0 = k.satbRtFset, pts0 = k.satbRtPtrStruct, rtw0 = k.satbRtWaits, grows0 = mt::g_satbWGrows;
    const int stuck = AsSatbRounds(rounds, &seed, &conc, &muts);
    const int a0 = AsGet(0), a1 = AsGet(1), a2 = AsGet(2), a3 = AsGet(3), a4 = AsGet(4), a5 = AsGet(5), runs = AsGet(6), rng = AsGet(8), other = AsGet(9),
              abandoned = AsGet(12), errLen = AsGet(13);
    void* es = TA("GetError", 0, NULL);
    char err[200] = "";
    if (es && es != (void*)1) Narrow(es, err, 200);
    Log("AS satb (act): %d rounds, %d with the main thread mutating during the job (%d mutations); runs %d, A0 %d A1 %d A2 %d A3 %d A4 %d (rng %d, other %d) "
        "A5 %d, abandoned %d; worker loads %ld, log hits %ld, arena ignores %ld, give-ups %ld (last op 0x%lX), conflicts %ld; sim stores %ld, words logged %ld, "
        "runtime writers %ld, worker copy fixes %ld, waits %ld, stale %ld, concurrent jobs %ld; %s\n", rounds, conc, muts, runs, a0, a1, a2, a3, a4, rng,
        other, a5, abandoned, k.satbWLoads - loads0, k.satbWHits - hits0, k.satbWArenaIgn - ign0, k.satbWGiveUp - give0, k.satbWGiveUpOp,
        k.satbWConflict - conf0, k.satbStores - st0, k.satbWords - words0, k.satbRtCalls - rt0, k.satbWRtFix - fix0, k.satbWaits - waits0,
        k.satbStale - stale0, k.satbConc - conc0, err);
    if (mt::g_satbDbgObj) {
        void* objClass = *(void**)(uintptr_t)(0x1220E40 + 4);
        void* fullName = Meth(Cls(*(void**)(uintptr_t)0x1220E40, "System", "MonoType"), "get_FullName", 0);
        char nm[128] = "";
        Narrow(Inv0(fullName, Inv0(Meth(objClass, "GetType", 0), (void*)(uintptr_t)mt::g_satbDbgObj)), nm, 120);
        Log("AS satb: last conflict at %08lX in a %s (object %08lX)\n", mt::g_satbDbgAddr, nm, mt::g_satbDbgObj);
    }
    const int perRun = 50;
    const bool ok = stuck == 0 && conc >= rounds / 2 && a1 + a2 + a3 == 0 && other == 0 && errLen == 0 && k.workExc == 0 &&
                    k.satbWHits - hits0 > 0 && runs >= rounds * 9 / 10 && a0 + a4 + abandoned == runs * perRun && a0 >= runs * perRun * 9 / 10;
    Check("AS-satb-exact", ok, "worker scored the cut while this thread mutated the world in %d of %d jobs: A0 %d of %d, A1-A3 %d, "
          "log hits %ld, give-ups %ld", conc, rounds, a0, runs * perRun, a1 + a2 + a3, k.satbWHits - hits0, k.satbWGiveUp - give0);
    // docs/206 §10 (0.36.136): reflection field sets and PtrToStructure(ptr, object) in the mutator are logged, never a wait
    // docs/206 §12 (bp-satb-act-1: every job stopped): a Satb=2 job submitted at the cut keeps running through the next
    // Simulate entry and is never stopped: no stale job, no A4/A5, every concurrent job compared
    Check("AS-satb-survive", stuck == 0 && k.satbStale == stale0 && a4 == 0 && a5 == 0 && abandoned == 0 && k.satbConc - conc0 > 0 && a0 == runs * perRun,
          "jobs running at the next Simulate entry %ld, stopped (stale) %ld, A0 %d of %d, A4 %d A5 %d; worker arena grows %ld (docs/206 §13: deep paths, same chunks)",
          k.satbConc - conc0, k.satbStale - stale0, a0, runs * perRun, a4, a5, mt::g_satbWGrows - grows0);
    // docs/206 §12: the worker's reflection reads (Score) are answered from the cut, never a give-up
    Check("AS-satb-rtwriters", ok && k.satbRtFset - fset0 > 0 && k.satbRtPtrStruct - pts0 > 0 && k.satbRtWaits == rtw0 && k.satbWGiveUp == give0,
          "MonoField.SetValue %ld, PtrToStructure(object) %ld during spans, runtime waits %ld, worker give-ups %ld (GetValue reads answered from the cut)",
          k.satbRtFset - fset0, k.satbRtPtrStruct - pts0, k.satbRtWaits - rtw0, k.satbWGiveUp - give0);

    // barrier cost on this thread [measured]: the same mutation and score loops with a span on (no job) vs off; ns per
    // store through the stub (first writes included) and per load through the thread test
    {
        LARGE_INTEGER f, t0, t1;
        QueryPerformanceFrequency(&f);
        static int bn = 100;  // ~45k first-written words: under the 65,534-entry log
        double offM = 1e30, onM = 1e30, offS = 1e30, onS = 1e30;
        LONG st = 0, wd = 0, ld = 0, stS = 0;
        for (int rep = 0; rep < 20; rep++) {
            for (int on = 0; on < 2; on++) {
                const LONG st0b = k.satbStores, wd0b = k.satbWords, ld0b = k.satbLoads;
                if (on) mt::SatbBegin(mt::g_mtWorkerTid, 0);
                QueryPerformanceCounter(&t0);
                for (int i = 0; i < bn; i++) { int sd = 5000 + i; void* m[1] = {&sd}; TA("MainMutate", 1, m); }
                QueryPerformanceCounter(&t1);
                const double msM = (t1.QuadPart - t0.QuadPart) * 1000.0 / f.QuadPart;
                const LONG stS0 = k.satbStores;
                QueryPerformanceCounter(&t0);
                for (int i = 0; i < bn; i++) { int sd = i; void* m[1] = {&sd}; TA("SatbScoreNow", 1, m); }
                if (on) stS = k.satbStores - stS0;
                QueryPerformanceCounter(&t1);
                const double msS = (t1.QuadPart - t0.QuadPart) * 1000.0 / f.QuadPart;
                if (on) { mt::SatbEnd(); st = k.satbStores - st0b, wd = k.satbWords - wd0b, ld = k.satbLoads - ld0b; onM = min(onM, msM), onS = min(onS, msS); }
                else offM = min(offM, msM), offS = min(offS, msS);
            }
        }
        Log("AS satb cost: %d mutations %.2f ms off / %.2f ms on (%ld stores, %ld first-written words): %.1f ns per store; %d scores %.2f ms off / %.2f ms on "
            "(%ld loads, %ld stores of its own): %.2f ns per load after its stores [measured, best of 20]\n", bn, offM, onM, st, wd,
            st ? (onM - offM) * 1e6 / st : 0.0, bn, offS, onS, ld, stS, ld && st ? ((onS - offS) * 1e6 - stS * (onM - offM) * 1e6 / st) / ld : 0.0);
    }

    // negative control: the same concurrent flow with no barrier and no redirect must see the mutations
    mt::g_satbTestNoBarrier = true;
    TA("ResetCounts", 0, NULL);
    int conc2 = 0, muts2 = 0;
    const int stuck2 = AsSatbRounds(40, &seed, &conc2, &muts2);
    mt::g_satbTestNoBarrier = false;
    const int b1 = AsGet(1), b2 = AsGet(2), b3 = AsGet(3);
    Log("AS satb negative control (no barrier): 40 rounds, %d concurrent, A0 %d A1 %d A2 %d A3 %d\n", conc2, AsGet(0), b1, b2, b3);
    Check("AS-satb-negative", stuck2 == 0 && conc2 > 0 && b1 + b2 + b3 > 0,
          "without the barrier the worker saw the main thread's writes: A1-A3 %d over %d concurrent jobs", b1 + b2 + b3, conc2);

    // overflow: a mutation that writes more words than the log holds while the job runs -> the sim stops the worker and
    // waits for it before the unlogged writes; the run is stopped (A5 here), never wrong
    TA("ResetCounts", 0, NULL);
    const LONG ov0 = k.satbOverflow, wt0 = k.satbWaits;
    int big = 0;
    {
        int id = 30000;
        void* a[1] = {&id};
        void* bg[1] = {&seed};
        TA("MainBig", 1, bg);  // allocate the array before the cut (warm), then once more inside the job
        if (I32(TA("Request", 1, a)) == 1) {
            mt::MtSimLeave();
            mt::MtTick();
            if (mt::g_mtBusy) { TA("MainBig", 1, bg); big = 1; }
            for (int s = 0; s < 1000 && mt::g_asRun != 0; s++) mt::MtSimLeave();
        }
    }
    Log("AS satb overflow: big write during the job %d, overflows %ld, waits %ld (timeouts %ld), A0 %d A1 %d A2 %d A3 %d A5 %d\n", big,
        k.satbOverflow - ov0, k.satbWaits - wt0, k.satbWaitTimeouts, AsGet(0), AsGet(1), AsGet(2), AsGet(3), AsGet(5));
    Check("AS-satb-overflow", !big || (k.satbOverflow > ov0 && k.satbWaits > wt0 && AsGet(1) + AsGet(2) + AsGet(3) == 0 && mt::g_asRun == 0),
          "a 70,000-word write during a job: overflow %ld, the sim waited %ld times, A1-A3 %d (big write in the job: %d)", k.satbOverflow - ov0,
          k.satbWaits - wt0, AsGet(1) + AsGet(2) + AsGet(3), big);

    // probe (S0): the job runs in the window as before; one job length of the next tick's stores is counted, no redirect
    mt::SatbInstall(1);
    TA("ResetCounts", 0, NULL);
    const LONG sp0 = k.satbJobSpans, js0 = k.satbJobStores, jw0 = k.satbJobWords, wl0 = k.satbWLoads;
    for (int r = 0; r < 20; r++) {
        int id = 40000 + r;
        void* a[1] = {&id};
        if (I32(TA("Request", 1, a)) != 1) break;
        mt::MtSimLeave();
        for (int s = 0; s < 60000 && mt::g_mtBusy; s++) Sleep(0);  // quiescent window: the job ends first
        mt::MtTick();  // window close: the probe span starts
        for (int m = 0; m < 3; m++) { void* mm[1] = {&seed}; TA("MainMutate", 1, mm); seed++; }
        Sleep(10);  // past one job length: the timer thread snapshots
        mt::MtSimLeave();  // span end, finish
    }
    Log("AS satb probe: spans timed %ld, per span %ld stores / %ld words; worker redirect loads %ld; A0 %d A1 %d A2 %d A3 %d\n", k.satbJobSpans - sp0,
        k.satbJobSpans > sp0 ? (k.satbJobStores - js0) / (k.satbJobSpans - sp0) : 0, k.satbJobSpans > sp0 ? (k.satbJobWords - jw0) / (k.satbJobSpans - sp0) : 0,
        k.satbWLoads - wl0, AsGet(0), AsGet(1), AsGet(2), AsGet(3));
    Check("AS-satb-probe", k.satbJobSpans - sp0 >= 18 && k.satbJobStores > js0 && k.satbJobWords > jw0 && k.satbWLoads == wl0 && AsGet(1) + AsGet(2) + AsGet(3) == 0,
          "probe spans %ld with stores counted, no worker redirect, A1-A3 %d", k.satbJobSpans - sp0, AsGet(1) + AsGet(2) + AsGet(3));
    mt::SatbInstall(0);
    return 0;
}

static int AsG2() {
    mt::Ts3nAutShadow& k = mt::ts3native_autshadow;
    mt::Ts3nMtProbe& p = mt::ts3native_mtprobe;
    const u8* base = (const u8*)GetModuleHandleW(NULL);
    const IMAGE_NT_HEADERS* nt = (const IMAGE_NT_HEADERS*)(base + ((const IMAGE_DOS_HEADER*)base)->e_lfanew);
    const IMAGE_SECTION_HEADER* sec = IMAGE_FIRST_SECTION(nt);
    DWORD textLo = 0, textHi = 0;
    for (int i = 0; i < nt->FileHeader.NumberOfSections && !textLo; i++)
        if (sec[i].Characteristics & IMAGE_SCN_MEM_EXECUTE) textLo = (DWORD)(uintptr_t)base + sec[i].VirtualAddress, textHi = textLo + sec[i].Misc.VirtualSize;
    const DWORD imgLo = (DWORD)(uintptr_t)base, imgHi = imgLo + nt->OptionalHeader.SizeOfImage;
    const int nIc = (int)(sizeof(kAsHostNames) / sizeof(kAsHostNames[0]));
    const bool guards = mt::AsAlloc() && mt::AsInstallGuards((DWORD*)g_asHostTab, (DWORD*)g_asHostTab + nIc * 2 + 2, imgLo, imgHi, textLo, textHi, true) == nIc;
    for (int i = 0; i < nIc; i++) {
        char name[96];
        sprintf_s(name, "MonoHostTest.AsIc::%s", kAsHostNames[i]);
        ((AddInternalCall)0xE82550)(_strdup(name), g_asHostTab[i * 2]);  // the thunk the install wrote into the table
    }
    ((AddInternalCall)0xE82550)("MonoHostTest.AsIc::HostSetLotObjects", (const void*)HostSetLotObjects);
    ((AddInternalCall)0xE82550)("MonoHostTest.AsIc::HostSetNameKey", (const void*)HostSetNameKey);
    mt::g_asAudit = 5;
    ((void(__fastcall*)(void*, void*, DWORD))0x4FA990)(g_hostRngHost + 0xC30, NULL, 12345);
    mt::g_asHostPtr = &g_hostRngHostPtr;
    mt::AsInstallRun(NULL, true);
    mt::g_asMode = 1;  // R: reference on this thread, then the worker
    mt::MtTick();      // first Simulate entry: the AutShadowNative ICs register
    const int ver = I32(TA("Setup", 0, NULL));
    if (!Check("AS-install", guards && k.descIcs == nIc && k.icRegistered == 31 && ver == 10 && p.workerState == 2,
               "guards %d (%ld descriptor ICs), AutShadowNative ICs %ld, Setup -> Version %d, worker state %ld", guards, k.descIcs,
               k.icRegistered, ver, p.workerState))
        return 160;

    // warm-up requests: learned keys (position-keyed lot location, per-lot lists) come in from the worker's misses
    static int warm = 4, rounds = 200;
    int requests = 0, waits = 0, mainPasses = 0, overruns = 0;
    DWORD t0 = GetTickCount(), gc0 = *GC_GC_NO;
    const LONG push0 = mt::ts3native_counters.mtStacksPushed;
    for (int r = 0; r < warm + rounds; r++) {
        if (r == warm) {  // measured part starts here
            TA("ResetCounts", 0, NULL);
            t0 = GetTickCount(), gc0 = *GC_GC_NO;
        }
        int id = r + 1;
        void* a[1] = {&id};
        if (I32(TA("Request", 1, a)) != 1) { Log("AS request %d refused (run %ld)\n", id, mt::g_asRun); break; }
        requests++;
        mt::MtSimLeave();  // cut, reference, submit
        // the window: this thread allocates, collects and runs its own pass-through passes until the job is out
        for (int s = 0; s < 60000 && mt::g_mtBusy; s++) {
            MainChurnGc();
            if (r >= warm) { TA("MainPass", 0, NULL); mainPasses++; }
            waits++;
        }
        overruns += mt::g_mtBusy != 0;
        mt::MtTick();      // next Simulate entry: window closes
        mt::MtSimLeave();  // finish (compare)
    }
    const DWORD ms = GetTickCount() - t0;
    const int a0 = AsGet(0), a1 = AsGet(1), a2 = AsGet(2), a3 = AsGet(3), a4 = AsGet(4), a5 = AsGet(5), runs = AsGet(6), rng = AsGet(8),
              other = AsGet(9), mainBad = AsGet(10), abandoned = AsGet(12), errLen = AsGet(13);
    void* es = TA("GetError", 0, NULL);
    char err[200] = "";
    if (es && es != (void*)1) Narrow(es, err, 200);
    const LONG gcs = (LONG)(*GC_GC_NO - gc0), pushed = mt::ts3native_counters.mtStacksPushed - push0;
    Log("AS G-A2: %d requests (%d measured) in %lu ms: runs %d, A0 %d A1 %d A2 %d A3 %d A4 %d (rng %d, other %d) A5 %d, abandoned %d; main passes %d "
        "(wrong %d); collections %ld, P2 pushes %ld; cuts %ld (max %ld us), refs %ld (RNG restored %ld), worker runs %ld (max %ld us, exc %ld), "
        "overrun %ld, worker hits %ld misses %ld, learned %ld, audits %ld (S1 %ld), aborts %ld; %s\n", requests, rounds, ms, runs, a0, a1, a2,
        a3, a4, rng, other, a5, abandoned, mainPasses, mainBad, gcs, pushed, k.cutReq, k.cutMaxUs, k.refRuns, k.rngDraws, k.workRuns,
        k.workMaxUs, k.workExc, k.overrun, k.workerHits, k.workerMisses, k.learned, k.auditN, k.auditMis[0], k.abortTotal, err);
    const int perRun = 50, rngPerRun = 0;  // objects 0, 7, ..., 49 draw inside their RngUnit: no rng aborts (docs/125)
    Check("AS-G-A2", requests == warm + rounds && runs == rounds && a0 == rounds * (perRun - rngPerRun) && a1 + a2 + a3 == 0 &&
                         rng == rounds * rngPerRun && other == 0 && a5 == 0 && abandoned == 0 && errLen == 0 && mainBad == 0 &&
                         mainPasses >= rounds && gcs >= rounds && k.workExc == 0 && k.auditMis[0] == 0 && overruns == 0 &&
                         k.rngPause >= rounds * 8 && k.rngPauseDraws == k.rngPause,
          "%d shadow searches x %d objects with the main thread interpreting, allocating and collecting: A0 %d of %d, A1-A3 %d, rng aborts %d, "
          "other aborts %d, A5 %d; %d main passes wrong %d; %ld collections; reference RNG pause brackets %ld (draws %ld)", rounds, perRun, a0, rounds * (perRun - rngPerRun), a1 + a2 + a3, rng,
          other, a5, mainPasses, mainBad, gcs, k.rngPause, k.rngPauseDraws);
    (void)waits;

    // overrun: the next Simulate entry comes while the worker is still inside -> stop flag, A5, and the worker leaves
    int id = 9999;
    void* a[1] = {&id};
    const LONG over0 = k.overrun;
    const int a5b = AsGet(5);
    bool posted = I32(TA("Request", 1, a)) == 1;
    mt::MtSimLeave();
    mt::MtTick();  // at once: the worker has hardly started
    for (int s = 0; s < 5000 && mt::g_mtBusy; s++) Sleep(1);
    mt::MtSimLeave();
    const bool stopped = k.overrun > over0 ? AsGet(5) == a5b + 1 && AsGet(11) == 1 : AsGet(11) == 0;  // a fast worker may finish first
    Check("AS-overrun", posted && !mt::g_mtBusy && mt::g_asRun == 0 && stopped && AsGet(13) == 0,
          "request posted %d, overrun %ld, last status %d, worker idle %d", posted, k.overrun - over0, AsGet(11), !mt::g_mtBusy);
    {  // docs/124 §17 go-live (Mode 3): every request reaches the worker and the finish; the reference only at every 4th cut
        const LONG ref0 = k.refRuns, wr0 = k.workRuns, ver0 = k.alVerify;
        const int runs0 = AsGet(6);
        mt::g_asMode = 3, mt::g_asLiveVerify = 4;
        int okReq = 0;
        for (int r = 0; r < 8; r++) {
            int lid = 20000 + r;
            void* la[1] = {&lid};
            okReq += I32(TA("Request", 1, la)) == 1;
            mt::MtSimLeave();
            for (int s = 0; s < 5000 && mt::g_mtBusy; s++) Sleep(1);
            mt::MtTick();
            mt::MtSimLeave();
        }
        mt::g_asMode = 1, mt::g_asLiveVerify = 50;
        Check("AS-live-verify", okReq == 8 && k.workRuns - wr0 == 8 && AsGet(6) - runs0 == 8 && k.refRuns - ref0 == 2 && k.alVerify - ver0 == 2 &&
                                    mt::g_asRun == 0,
              "Mode 3: %d requests, worker runs %ld, finished runs %d, reference runs %ld (VerifyEvery 4: want 2)", okReq, k.workRuns - wr0,
              AsGet(6) - runs0, k.refRuns - ref0);
    }
    char sv[4] = "";
    GetEnvironmentVariableA("MONOHOST_SATB", sv, sizeof(sv));
    if (sv[0] == '1') {
        const int rc = AsSatb();
        if (rc) return rc;
    }
    mt::g_mtOnEnter = NULL, mt::g_mtOnLeave = NULL, mt::g_mtJobHook = NULL, mt::g_asWorkerTid = 0;
    return 0;
}

static int M5() {
    // M1.5 hooked [0x11949CC]; hand it back so ApplyMonoThread finds the game's push and chains it
    *(void**)(uintptr_t)0x11949CC = (void*)g_pushOtherOrig;
    wchar_t ini[MAX_PATH];
    GetModuleFileNameW(NULL, ini, MAX_PATH);
    wcscat_s(ini, L".m5.ini");
    WritePrivateProfileStringW(L"MonoThread", L"Enabled", L"1", ini);
    WritePrivateProfileStringW(L"MonoThread", L"Mode", L"1", ini);
    WritePrivateProfileStringW(L"MonoThread", L"GcLock", L"1", ini);
    WritePrivateProfileStringW(L"MonoThread", L"FrameTls", L"1", ini);
    // negative control: MONOHOST_ARENAFIX=0 runs without the frame arena pool fix (the concurrent jobs then fail, docs/120 §9)
    char fixEnv[4] = "";
    GetEnvironmentVariableA("MONOHOST_ARENAFIX", fixEnv, sizeof(fixEnv));
    WritePrivateProfileStringW(L"MonoThread", L"ArenaFix", fixEnv[0] == '0' ? L"0" : L"1", ini);
    // MONOHOST_F1=0 runs the F1 job without the worker-private call records (the hazard, docs/120 §9.4)
    char f1Env[4] = "";
    GetEnvironmentVariableA("MONOHOST_F1", f1Env, sizeof(f1Env));
    const bool f1Private = f1Env[0] != '0';
    WritePrivateProfileStringW(L"MonoThread", L"N2mPrivate", f1Private ? L"1" : L"0", ini);
    mt::g_mainTid = GetCurrentThreadId();
    const bool applied = mt::ApplyMonoThread(ini);
    // F1: our IC enters through 0xD8A8E0; its call-record cursor starts at the chain's sentinel (0xD9A520 sets it in the game)
    ((AddInternalCall)0xE82550)("MonoHostTest.HostTest::N2mMinLoop", (const void*)HostN2mMinLoop);
    if (!*N2M_CURSOR) *N2M_CURSOR = 0x11944C8;
    g_n2mRm = ((RuntimeMethodFn)(uintptr_t)0xE51F20)(Meth(g_testK, "MinLoop", 1));
    // warm-up on the main ("sim") thread: every method and IC signature the worker uses runs here first (docs/30 §5)
    static int warm = 3, rounds = 3000, minN = 400000, gn = 1000, gs = 9, concRounds = 5;
    void* aw[1] = {&warm};
    T("WorkerGraph", 1, aw);
    T("WorkerMin", 1, aw);
    T("MinLoop", 1, aw);
    T("GetLastWorker", 0, NULL);
    void* ag[2] = {&gn, &gs};
    int h = I32(T("BuildGraph", 2, ag));  // M1.4-style handle graph, checked after both jobs
    const int dups0 = PoolDups();  // M1.5's SleepInCctor task left one (docs/120 §9): the arena fix drops it at install
    mt::MtTick();  // first Simulate tick: P1 registers this thread, P3 + P4 install, Mode=1 starts the worker
    for (int i = 0; i < 500 && mt::ts3native_mtprobe.workerState == 1; i++) Sleep(10);
    mt::Ts3nMtProbe& p = mt::ts3native_mtprobe;
    const bool n2m = InstallN2mCounter() && p.n2mReady == (f1Private ? 1 : 0);
    // the 0xD8A8E0 path nests two states (the caller's and the call record's): warm it up only after the arena fix
    // dropped the duplicate chunk, which two nested states would otherwise share
    T("MainN2m", 1, aw);
    T("WorkerN2m", 1, aw);
    Check("M5-install", applied && n2m && p.p3Sites == 19 && p.p4Ready == 0xF && p.workerState == 2 && mt::ts3native_counters.mtSimRegistered >= 0,
          "ApplyMonoThread %d, n2m hook %d, P3 sites %ld, P4 0x%lX, worker state %ld, main registered %ld", applied, n2m, p.p3Sites, p.p4Ready,
          p.workerState, mt::ts3native_counters.mtSimRegistered);
    if (p.workerState != 2) return 150;
    Check("M5-arena-dedupe", (p.p3Ready & 16) && dups0 > 0 && p.arenaDropped >= dups0 && PoolDups() == 0,
          "frame arena pool: %d duplicate chunks before install (M1.5's exception task), %ld nodes dropped, %d duplicates after", dups0, p.arenaDropped, PoolDups());

    // a task that ends with a managed exception (yield refused in a .cctor) frees its state twice; the fix clears the
    // chain after the first release-all, so its chunk goes into the pool once
    HostTask& x = g_tasks[2];
    x.id = 0x0000ABCD00000033ULL;
    x.state = ((TaskNew)0xE52D70)(Meth(g_testK, "SleepInCctor2", 0), NULL, NULL, &x.exc);
    const LONG rel0 = p.arenaRelNonEmpty;
    const bool xEnded = RunTask(x);
    Check("M5-arena-release", xEnded && x.exc && p.arenaRelNonEmpty > rel0 && PoolDups() == 0,
          "exception task: ended %d, exception %p, release-all of a non-empty chain %ld, pool duplicates after %d", xEnded, x.exc,
          p.arenaRelNonEmpty - rel0, PoolDups());

    // job 0: the same job with the main thread idle (only the worker allocates and collects)
    static int rounds0 = 1000;
    void* ar0[1] = {&rounds0};
    int passes = 0;
    DWORD ms = 0, gcA = *GC_GC_NO;
    int j0 = WorkerJob("WorkerGraph", ar0, MainIdle, &passes, &ms);
    int w0 = I32(T("GetLastWorker", 0, NULL));
    LogWorkerError("WorkerGraph alone");
    Log("M5 job WorkerGraph(%d) with the main thread idle: result %d, %lu ms, collections %lu\n", rounds0, w0, ms, *GC_GC_NO - gcA);
    Check("M5-worker-alone", j0 == 0 && w0 == rounds0 && p.workerCollects > 0 && mt::ts3native_counters.mtStacksPushed > 0 && !p.lockOverflow,
          "worker alone (main idle): %d of %d rounds clean, %ld collections run by the worker (main stack pushed by P2 %ld times)", w0, rounds0,
          p.workerCollects, mt::ts3native_counters.mtStacksPushed);

    // job A: both threads interpret at once with no allocation in the loop (MinLoop: float compares; docs/122 §12 names
    // Math.Min as a 0xD8A8E0 user in the game, where EA's binder bodies are registered; the counter shows whether it is
    // reached here). Before the arena fix this faulted at 0xE5444D or ended the worker's job in microseconds (docs/120 §9).
    DWORD cur0 = *N2M_CURSOR;
    g_n2mMain = g_n2mWorker = 0;
    void* am[1] = {&minN};
    int workerBad = 0, mainBad = 0, totalPasses = 0;
    LONG minRunUs = LONG_MAX;
    for (int r = 0; r < concRounds; r++) {
        const LONG run0 = p.workerRunUs;
        mainBad += WorkerJob("WorkerMin", am, MainMinLoop, &passes, &ms);
        const int wb = I32(T("GetLastWorker", 0, NULL));
        workerBad += wb != 0;
        totalPasses += passes;
        if (p.workerRunUs - run0 < minRunUs) minRunUs = p.workerRunUs - run0;
        LogWorkerError("WorkerMin");
    }
    DWORD cur1 = *N2M_CURSOR;
    Log("M5 job MinLoop(%d) x %d: worker bad rounds %d, shortest worker run %ld us, main passes %d with errors %d; 0xD8A8E0 entries main %ld worker %ld; "
        "cursor [0x11EE5A4] %08lX -> %08lX\n", minN, concRounds, workerBad, minRunUs, totalPasses, mainBad, g_n2mMain, g_n2mWorker, cur0, cur1);
    Check("M5-concurrent-noalloc", workerBad == 0 && mainBad == 0 && totalPasses >= 10 * concRounds && minRunUs >= 5000,
          "both threads interpreting, no allocation: %d rounds, wrong results worker %d main %d over %d main passes, shortest worker run %ld us; "
          "0xD8A8E0 entries %ld/%ld (main/worker)", concRounds, workerBad, mainBad, totalPasses, minRunUs, g_n2mMain, g_n2mWorker);

    // job F1: both threads enter the interpreter through 0xD8A8E0 at once (worker: WorkerN2m -> IC -> 0xD8A8E0 -> MinLoop;
    // main: MainN2m the same way). Without N2mPrivate the cursor hands one thread the other's live record (docs/120 §9.4).
    {
        static int n2mN = 400000;
        void* an[1] = {&n2mN};
        g_n2mMain = g_n2mWorker = 0;
        const LONG pw0 = p.n2mWorker;
        const DWORD c0 = *N2M_CURSOR;  // balanced entries and exits leave the cursor where it was
        int wBad = 0, mBad = 0, mPasses = 0;
        for (int r = 0; r < concRounds; r++) {
            mBad += WorkerJob("WorkerN2m", an, MainN2mLoop, &passes, &ms);
            wBad += I32(T("GetLastWorker", 0, NULL)) != 0;
            mPasses += passes;
            LogWorkerError("WorkerN2m");
        }
        Log("M5 job N2m MinLoop(%d) x %d: worker bad rounds %d, main passes %d with errors %d; 0xD8A8E0 entries main %ld worker %ld (private %ld, "
            "max depth %ld, overflow %ld); cursor [0x11EE5A4] %08lX -> %08lX\n", n2mN, concRounds, wBad, mPasses, mBad, g_n2mMain, g_n2mWorker,
            p.n2mWorker - pw0, p.n2mMaxDepth, p.n2mOverflow, c0, *N2M_CURSOR);
        Check("M5-n2m-private", wBad == 0 && mBad == 0 && mPasses >= 10 * concRounds && g_n2mWorker >= concRounds && g_n2mMain >= mPasses &&
              p.n2mWorker - pw0 == g_n2mWorker && !p.n2mOverflow && *N2M_CURSOR == c0,
              "both threads through 0xD8A8E0: %d rounds, wrong results worker %d main %d over %d main passes; worker entries %ld on private records %ld",
              concRounds, wBad, mBad, mPasses, g_n2mWorker, p.n2mWorker - pw0);
    }

    // job B: a 400-node graph only on the worker's stack while the main thread allocates and collects. A collection the
    // main thread runs suspends the worker and P2 pushes the worker's registers and stack (I-120-15).
    DWORD gc0 = *GC_GC_NO;
    LONG push0 = mt::ts3native_counters.mtStacksPushed, coll0 = p.workerCollects, calls0 = p.lockedCalls, wcalls0 = p.lockedWorker;
    void* ar[1] = {&rounds};
    int jr = WorkerJob("WorkerGraph", ar, MainChurnGc, &passes, &ms);
    int wr = I32(T("GetLastWorker", 0, NULL));
    LogWorkerError("WorkerGraph");
    const LONG gcs = (LONG)(*GC_GC_NO - gc0), wcoll = p.workerCollects - coll0, pushed = mt::ts3native_counters.mtStacksPushed - push0;
    Log("M5 job WorkerGraph(%d) with main allocating: result %d, %lu ms, main passes %d, collections %ld (worker-started %ld), stacks pushed by P2 %ld, "
        "locked calls %ld (worker %ld), lock waits %ld (max %ld us), max depth %ld, overflow %ld\n", rounds, wr, ms, passes, gcs, wcoll, pushed,
        p.lockedCalls - calls0, p.lockedWorker - wcalls0, p.lockWaits, p.lockMaxWaitUs, p.lockMaxDepth, p.lockOverflow);
    Check("M5-concurrent-alloc", jr == 0 && wr == rounds && gcs >= 100 && gcs - wcoll >= 100 && pushed >= gcs,
          "graph only on the worker's frame intact after %d rounds with main allocating (result %d); %ld collections, %ld run by the main thread "
          "with the worker's stack pushed by P2 (%ld pushes)", rounds, wr, gcs, gcs - wcoll, pushed);

    // G-A2: [AutShadow] part 2 (cut, reference, worker scoring from the snapshot, compare) on this worker
    char asEnv[4] = "";
    GetEnvironmentVariableA("MONOHOST_AUTSHADOW", asEnv, sizeof(asEnv));
    if (asEnv[0] != '0') {
        const int rc = AsG2();
        if (rc) return rc;
    }

    void* ah[3] = {&h, &gn, &gs};
    int hb = I32(T("CheckGraph", 3, ah));
    void* af[1] = {&h};
    T("Free", 1, af);
    mt::ts3native_counters.mtMode = 0;
    mt::MtTick();  // Mode 0: the worker unregisters and exits, then the gate goes off
    Check("M5-stop", hb == 0 && p.workerState == 4 && !p.locking && !p.workerStopFails && !p.workerExceptions && !p.workerCtxSet && !PoolDups(),
          "handle graph errors %d; worker state %ld, gate %ld, stop fails %ld, worker exceptions %ld, jobs %ld, pool duplicates %d, arena nodes dropped %ld",
          hb, p.workerState, p.locking, p.workerStopFails, p.workerExceptions, p.workerJobs, PoolDups(), p.arenaDropped);
    return 0;
}

// docs/128 G-M4 / G-M5 one-time cost under the game's Mono 1.2.3 (mint): ts3fixes' closure list resolved by reflection in
// the loaded script assemblies, an L3 klass read per method, then the override census over the non-game assemblies.
static int GuardSweepTest() {
    LARGE_INTEGER f, t0, t1;
    QueryPerformanceFrequency(&f);
    QueryPerformanceCounter(&t0);
    const int found = I32(T("GuardSweep", 0, NULL));
    QueryPerformanceCounter(&t1);
    const double ms = (t1.QuadPart - t0.QuadPart) * 1000.0 / f.QuadPart;
    int w[5];
    for (int i = 0; i < 5; i++) { void* a[1] = {&i}; w[i] = I32(T("GetSweep", 1, a)); }
    QueryPerformanceCounter(&t0);
    T("GuardSweep", 0, NULL);  // again: the cost per load after the first (types and reflection data cached)
    QueryPerformanceCounter(&t1);
    const double ms2 = (t1.QuadPart - t0.QuadPart) * 1000.0 / f.QuadPart;
    Check("GM4-sweep", found == w[4] && w[1] == 0 && w[3] == 0,
          "G-M4: %d of %d closure methods resolved in %d game assemblies, %d swapped; G-M5: %d overriding types; first sweep %.1f ms, second %.1f ms",
          found, w[4], w[2], w[1], w[3], ms, ms2);
    return 0;
}

// ---- docs/139: mint dispatch instrumentation. Env MONOHOST_MINT=count: every jump-table entry of 0xE59880 goes through a
// counter (opcode and opcode-pair counts, callvirt cache misses at 0xE54B0B); =threaded: each of the 417 handler-tail
// `jmp 0xE544D5` gets its own byte copy of the dispatch head (same instructions, own indirect-branch site).
// Env MONOHOST_MINTBENCH=<n>: after the selftest, time HostTest.MintBench(n) 7 times (QPC) and log the checksum.
static const DWORD MINT_HEAD = 0xE544D5, MINT_TABLE = 0xE59880, MINT_NOP = 0x1C1, MINT_UNIMPL = 0xE596C2;
static const u8 kMintHead[25] = {0x0F, 0xB7, 0x07, 0x0F, 0xB7, 0xD0, 0x81, 0xFA, 0xC0, 0x01, 0x00, 0x00, 0x0F,
                                 0x87, 0xDB, 0x51, 0x00, 0x00, 0xFF, 0x24, 0x95, 0x80, 0x98, 0xE5, 0x00};
static DWORD g_opN[MINT_NOP], g_pairN[MINT_NOP * MINT_NOP], g_prevOp, g_cvMiss;
static void* g_opOrig[MINT_NOP];
static void* g_cvMissNext = (void*)(uintptr_t)0xE51FC0;
static int g_mintMode;  // 0 off, 1 count, 2 threaded
static __declspec(naked) void MintCount() {  // entry state of every handler: eax = edx = opcode; keep all registers and flags
    __asm {
        pushfd
        push ecx
        inc dword ptr g_opN[edx*4]
        mov ecx, g_prevOp
        imul ecx, ecx, 0x1C1
        add ecx, edx
        inc dword ptr g_pairN[ecx*4]
        mov g_prevOp, edx
        pop ecx
        popfd
        jmp dword ptr g_opOrig[edx*4]
    }
}
static __declspec(naked) void MintMiss() {  // replaces `call 0xE51FC0` in callvirt (ecx = receiver); flags are dead after a call
    __asm {
        inc g_cvMiss
        jmp g_cvMissNext
    }
}
static bool InstallMint() {
    char v[16] = "";
    GetEnvironmentVariableA("MONOHOST_MINT", v, sizeof(v));
    g_mintMode = !strcmp(v, "count") ? 1 : !strcmp(v, "threaded") ? 2 : !strcmp(v, "nocheck") ? 3 : 0;
    if (!g_mintMode) return true;
    if (memcmp((void*)(uintptr_t)MINT_HEAD, kMintHead, sizeof(kMintHead))) { Log("mint: dispatch head bytes differ\n"); return false; }
    DWORD* table = (DWORD*)(uintptr_t)MINT_TABLE;
    if (g_mintMode == 3) {  // drop the opcode range check: `jmp [edx*4+table]` right after the two movzx (experiment only)
        if (!mt::Write((void*)(uintptr_t)(MINT_HEAD + 6), kMintHead + 18, 7)) return false;
        Log("mint: nocheck mode, range check at 0x%lX skipped\n", MINT_HEAD + 6);
        return true;
    }
    if (g_mintMode == 1) {
        DWORD mine[MINT_NOP];
        for (DWORD i = 0; i < MINT_NOP; i++) g_opOrig[i] = (void*)(uintptr_t)table[i], mine[i] = (DWORD)(uintptr_t)MintCount;
        u8 call[5] = {0xE8};
        *(DWORD*)(call + 1) = (DWORD)(uintptr_t)MintMiss - (0xE54B0B + 5);
        const u8* at = (const u8*)(uintptr_t)0xE54B0B;
        if (at[0] != 0xE8 || 0xE54B0B + 5 + *(const int*)(at + 1) != 0xE51FC0) { Log("mint: callvirt miss call differs\n"); return false; }
        if (!mt::Write(table, mine, sizeof(mine)) || !mt::Write((void*)at, call, 5)) return false;
        Log("mint: count mode, %u jump-table entries -> counter, callvirt miss call hooked\n", MINT_NOP);
        return true;
    }
    // threaded: scan ves_exec_method_with_context for `E9 rel32` -> head (docs/139 §2: 417 sites, capstone sweep agrees)
    u8* lo = (u8*)(uintptr_t)0xE53EB0;
    u8* copies = (u8*)VirtualAlloc(NULL, 0x10000, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
    int n = 0;
    for (u8* p = lo; p + 5 <= (u8*)(uintptr_t)MINT_TABLE && copies; p++) {
        if (*p != 0xE9 || (DWORD)(uintptr_t)p + 5 + *(int*)(p + 1) != MINT_HEAD) continue;
        u8* c = copies + n * 32;
        memcpy(c, kMintHead, sizeof(kMintHead));
        *(int*)(c + 14) = (int)(MINT_UNIMPL - ((DWORD)(uintptr_t)c + 18));  // ja rel32 re-based to the copy
        int rel = (int)((DWORD)(uintptr_t)c - ((DWORD)(uintptr_t)p + 5));
        if (!mt::Write(p + 1, &rel, 4)) return false;
        n++;
        p += 4;
    }
    FlushInstructionCache(GetCurrentProcess(), copies, 0x10000);
    Log("mint: threaded mode, %d handler tails got their own dispatch copy\n", n);
    return n == 417;
}
// subfuse (docs/139 §4): EA's peephole 0xE5C660 hands the matcher 0xD89930 one whole run (no branch target inside, cut
// before every call/ret/newobj-class op) and fuses only when the WHOLE run equals one of the 22 descriptors at 0x108FCD8.
// This replacement also fuses descriptors found inside a run: greedy, longest first, left to right. Same encoding (first
// opcode word replaced, operands and later words in place), so branch targets and fault ips stay as docs/130 §9.1.
static volatile LONG g_subRuns, g_subWhole, g_subInner;
static int __cdecl SubFuse(const unsigned short* win, int n, unsigned short* out, unsigned short* code) {
    const u8* oplen = (const u8*)(uintptr_t)0x1195088;
    unsigned short* pos[32];
    bool gap[33] = {};  // gap[k]: an instruction not in the window (nop, the only "skip" op) sits right before op k
    unsigned short* p = code;
    for (int k = 0, guard = 0; k < n && guard < 64; guard++) {
        unsigned short op = *p;
        int len = op < 0x1A9 && oplen[op] ? oplen[op] : -1;
        if (len < 0) return 0;  // switch or out of table: leave the run alone
        if (op == win[k]) pos[k++] = p; else gap[k] = true;
        p += len;
    }
    g_subRuns++;
    *out = 0;
    for (int i = 0; i < n;) {
        int best = 0, bestLen = 1;
        for (int j = 1; j <= 22; j++) {
            const u8* e = (const u8*)(uintptr_t)(0x108FCD8 + 0x48 * j);
            int m = *(const unsigned short*)(e + 4);
            if (m <= bestLen || m > n - i) continue;
            bool ok = !memcmp(e + 6, win + i, 2 * m);
            for (int k = i + 1; ok && k < i + m; k++) ok = !gap[k];
            if (ok) best = j, bestLen = m;
        }
        if (!best) { i++; continue; }
        if (i == 0 && bestLen == n) *out = (unsigned short)(0x1AA + best), g_subWhole++;  // EA's own case: caller writes it
        else *pos[i] = (unsigned short)(0x1AA + best), g_subInner++;
        i += bestLen;
    }
    return 0;
}
static __declspec(naked) void SubFuseShim() {  // replaces `call 0xD89930` at 0xE5C8CA; edi = run start (word index)
    __asm {
        mov eax, [esp + 0x80]  // the pass's first argument (transform context; caller reads it at [esp+0x70])
        mov eax, [eax + 0x38]  // mint code base
        lea eax, [eax + edi * 2]
        push eax
        push dword ptr [esp + 0x10]
        push dword ptr [esp + 0x10]
        push dword ptr [esp + 0x10]
        call SubFuse
        add esp, 16
        ret
    }
}
static bool InstallSubFuse() {
    char v[64] = "";
    GetEnvironmentVariableA("MONOHOST_MINTFUSE", v, sizeof(v));
    if (strcmp(v, "sub")) return true;
    const u8* at = (const u8*)(uintptr_t)0xE5C8CA;
    if (at[0] != 0xE8 || 0xE5C8CA + 5 + *(const int*)(at + 1) != 0xD89930) { Log("mint: matcher call differs\n"); return false; }
    u8 call[5] = {0xE8};
    *(DWORD*)(call + 1) = (DWORD)(uintptr_t)SubFuseShim - (0xE5C8CA + 5);
    Log("mint: subfuse on (descriptors inside runs)\n");
    return mt::Write((void*)at, call, 5);
}
static const char* MintName(DWORD op, char* buf) {
    const char* const* names = (const char* const*)(uintptr_t)0x11949E0;
    if (op <= 0x1A8) return names[op];
    sprintf_s(buf, 16, "super_%03X", op);
    return buf;
}
// log the top opcodes and pairs of this phase, append every non-zero pair to mint-pairs.csv next to the exe, reset
static void MintDump(const char* phase) {
    if (g_subRuns) Log("mint %s: subfuse runs %ld, whole-run fusions %ld, inner fusions %ld\n", phase, g_subRuns, g_subWhole, g_subInner);
    if (g_mintMode != 1) return;
    unsigned long long tot = 0;
    for (DWORD i = 0; i < MINT_NOP; i++) tot += g_opN[i];
    Log("mint %s: %llu dispatches; callvirt %u, cache misses %u (%.2f%%)\n", phase, tot, g_opN[0xEC], g_cvMiss,
        g_opN[0xEC] ? 100.0 * g_cvMiss / g_opN[0xEC] : 0.0);
    char path[MAX_PATH], a[16], b[16];
    GetModuleFileNameA(NULL, path, MAX_PATH);
    strcpy_s(strrchr(path, '\\') + 1, 64, "mint-pairs.csv");
    FILE* f = NULL;
    bool fresh = !strcmp(phase, "m13");
    fopen_s(&f, path, fresh ? "w" : "a");
    if (f && fresh) fprintf(f, "phase,op1,name1,op2,name2,count\n");
    for (DWORD i = 0; i < MINT_NOP; i++)
        if (g_opN[i] && f) fprintf(f, "%s,0x%03X,%s,,,%u\n", phase, i, MintName(i, a), g_opN[i]);
    for (DWORD i = 0; i < MINT_NOP * MINT_NOP; i++)
        if (g_pairN[i] && f) fprintf(f, "%s,0x%03X,%s,0x%03X,%s,%u\n", phase, i / MINT_NOP, MintName(i / MINT_NOP, a), i % MINT_NOP, MintName(i % MINT_NOP, b), g_pairN[i]);
    if (f) fclose(f);
    memset(g_opN, 0, sizeof(g_opN));
    memset(g_pairN, 0, sizeof(g_pairN));
    g_cvMiss = 0;
}
static void MintBench() {
    char v[16] = "";
    if (!GetEnvironmentVariableA("MONOHOST_MINTBENCH", v, sizeof(v))) return;
    int n = atoi(v), sum = 0;
    void* args[1] = {&n};
    LARGE_INTEGER fq, t0, t1;
    QueryPerformanceFrequency(&fq);
    double best = 1e9;
    SetThreadAffinityMask(GetCurrentThread(), 1 << 2);  // one core, high priority: less noise between A/B processes
    SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_HIGHEST);
    for (int r = 0; r < 7; r++) {
        QueryPerformanceCounter(&t0);
        sum = I32(T("MintBench", 1, args));
        QueryPerformanceCounter(&t1);
        double ms = 1000.0 * (t1.QuadPart - t0.QuadPart) / fq.QuadPart;
        if (ms < best) best = ms;
        Log("mintbench mode %d run %d: %.1f ms, checksum %d\n", g_mintMode, r, ms, sum);
    }
    Log("mintbench mode %d n %d: best %.1f ms, checksum %d\n", g_mintMode, n, best, sum);
}

// ---- docs/139 §8: call/return fast path (native/ts3native/mintcall.cpp). Env MONOHOST_MINTCALL=off|fast|verify
// (MONOHOST_MINTCALLEVERY=<n>: verify every n-th eligible call), MONOHOST_MCBENCH=<n>: after the selftest, CallBench
// kinds and MintBench timed with the four stock entries and with ours, interleaved in one process (pinned, best of 9).
static int g_mcMode;  // installed: 0 off, 1 fast, 2 verify, -1 refused
static bool InstallMintCall() {
    char v[16] = "", e[16] = "";
    GetEnvironmentVariableA("MONOHOST_MINTCALL", v, sizeof(v));
    GetEnvironmentVariableA("MONOHOST_MINTCALLEVERY", e, sizeof(e));
    const int mode = !strcmp(v, "fast") ? 1 : !strcmp(v, "verify") ? 2 : 0;
    if (!mode) return true;
    mt::g_mcCtr.mcSwitch = 1;
    mt::g_mcvNegative = GetEnvironmentVariableA("MONOHOST_MCNEG", NULL, 0) > 0;
    g_mcMode = mt::McInstall(mode, (DWORD)atoi(e));
    return g_mcMode > 0;
}
static int McInvoke(const char* name, int n, void** args) {
    void* m = Meth(g_callK, name, n);
    void* exc = NULL;
    DWORD code = 0;
    void* r = m ? SafeInvoke(m, NULL, args, &exc, &code) : NULL;
    if (!m || exc || code) { Log("CallTest.%s: missing %d, exception %p, fault 0x%lX\n", name, !m, exc, code); return INT_MIN; }
    return I32(r);
}
static void McLogCounters(const char* when) {
    const mt::McCtr& c = mt::g_mcCtr;
    Log("mintcall %s: mode %d ready %ld; fast call %ld callvirt %ld ret %ld; bail call %ld callvirt %ld ret %ld; verified %ld mismatch %ld skipped %ld\n",
        when, g_mcMode, c.mcReady, c.mcCall, c.mcCallvirt, c.mcRet, c.mcBailCall, c.mcBailCallvirt, c.mcBailRet, c.mcVerified, c.mcMismatch, c.mcVSkipped);
}
// every mode runs these; the checksums must be the same with MONOHOST_MINTCALL=off, fast and verify
static int McTests() {
    int n = 300;
    void* a[1] = {&n};
    const int sum = McInvoke("Run", 1, a);
    Log("mintcall tests: CallTest.Run(300) checksum %d\n", sum);
    Check("MC-run", sum != INT_MIN, "call shapes (static, instance, virtual, interface, value-type, wide/struct returns, cache misses, throw through finally, stack traces, null receiver, depth overflow) checksum %d", sum);
    // a task that yields 6 frames deep: the save / resume of every frame between (records, MonoInvocation, arena)
    static int five = 5;
    static void* targs[1] = {&five};
    FakeHostOnce();
    HostTask& k = g_tasks[2];
    k = HostTask{};
    k.id = 0x0000ABCD00000033ULL;
    k.state = ((TaskNew)0xE52D70)(Meth(g_callK, "SleepDeep", 1), NULL, targs, &k.exc);
    k.wake = g_tick;
    int runs = 0;
    for (int i = 0; i < 40 && !k.done; i++, g_tick++) {
        if (k.wake <= g_tick) RunTask(k), runs++;
        HostTickGc();
    }
    Log("mintcall tests: SleepDeep(5) runs %d result %d exc %p fault 0x%lX\n", runs, I32(k.result), k.exc, k.fault);
    Check("MC-deep-yield", k.done && !k.exc && !k.fault && runs == 6 && I32(k.result) == 775, "5 yields 6 frames deep, resumed, result %d (want 775)", I32(k.result));
    g_nSleep = 0;  // M1.5 counts its own sleeps from 0
    McLogCounters("after tests");
    return 0;
}
static double McTime(const char* name, int n, int kind, int* sum) {
    LARGE_INTEGER fq, t0, t1;
    QueryPerformanceFrequency(&fq);
    void* a[2] = {&n, &kind};
    QueryPerformanceCounter(&t0);
    *sum = McInvoke(name, kind < 0 ? 1 : 2, a);
    QueryPerformanceCounter(&t1);
    return 1000.0 * (t1.QuadPart - t0.QuadPart) / fq.QuadPart;
}
static void McBench() {
    char v[16] = "";
    if (!GetEnvironmentVariableA("MONOHOST_MCBENCH", v, sizeof(v)) || g_mcMode != 1) return;
    const int n = atoi(v), mb = 1000;
    static const char* const kKind[5] = {"empty loop", "call static int", "call static void", "callvirt override", "callvirt non-virtual"};
    double best[2][6];
    int sums[2][6];
    for (auto& b : best) for (double& x : b) x = 1e9;
    char cv[8] = "";
    SetThreadAffinityMask(GetCurrentThread(), (DWORD_PTR)1 << (GetEnvironmentVariableA("MONOHOST_MCCORE", cv, sizeof(cv)) ? atoi(cv) : 2));
    SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_HIGHEST);
    char rv[16] = "";
    const int reps = GetEnvironmentVariableA("MONOHOST_MCREPS", rv, sizeof(rv)) ? atoi(rv) : 25;
    for (int r = 0; r < reps; r++)
        for (int arm = 0; arm < 2; arm++) {  // 0 stock entries, 1 ours
            mt::McRestoreStock(arm == 0);
            for (int k = 0; k < 5; k++) best[arm][k] = min(best[arm][k], McTime("CallBench", n, k, &sums[arm][k]));
            void* a[1] = {(void*)&mb};
            LARGE_INTEGER fq, t0, t1;
            QueryPerformanceFrequency(&fq);
            QueryPerformanceCounter(&t0);
            sums[arm][5] = I32(T("MintBench", 1, a));
            QueryPerformanceCounter(&t1);
            best[arm][5] = min(best[arm][5], 1000.0 * (t1.QuadPart - t0.QuadPart) / fq.QuadPart);
        }
    mt::McRestoreStock(false);
    for (int k = 0; k < 5; k++) {
        const double s = k ? (best[0][k] - best[0][0]) * 1e6 / n : best[0][k] * 1e6 / n, f = k ? (best[1][k] - best[1][0]) * 1e6 / n : best[1][k] * 1e6 / n;
        Log("mcbench %-22s n %d: stock %.1f ms, fast %.1f ms; per call stock %.2f ns, fast %.2f ns (%+.1f%%); checksums %d / %d\n", kKind[k], n,
            best[0][k], best[1][k], s, f, k ? 100.0 * (f - s) / s : 0.0, sums[0][k], sums[1][k]);
    }
    Log("mcbench MintBench(%d): stock %.1f ms, fast %.1f ms (%+.1f%%); checksums %d / %d\n", mb, best[0][5], best[1][5],
        100.0 * (best[1][5] - best[0][5]) / best[0][5], sums[0][5], sums[1][5]);
}

#include "monohost-jit.cpp"  // docs/207 P0 method compiler (MONOHOST_JIT)

static int SelfTest(void* domain) {
    int rc = M13(domain);
    MintDump("m13");  // docs/139: the .cctor sweep = EA script code; the rest is HostTest code
    if (!rc) rc = M14();
    if (!rc) rc = McTests();  // docs/139 §8, before M1.5 (its exception task makes the pool state M5-arena-dedupe checks)
    if (!rc) rc = M15();
    JitAtomic(false);  // docs/207 verify: M5 installs P3 detours on the GC entry points
    if (!rc) rc = M5();
    if (!rc) rc = GuardSweepTest();  // after M5: M5-arena-dedupe expects M1.5's arena state
    MintDump("selftest-rest");
    // docs/207: the JIT runs go on past an early stop (M5's [AutShadow] install check belongs to another lane)
    if (!rc || g_jitMode) MintBench();
    MintDump("bench");
    if (!rc || g_jitMode) JitBenchRun();
    McBench();
    McLogCounters("end");
    Log("SELFTEST %s: %d passed, %d failed%s\n", g_fail || rc ? "FAILED" : "OK", g_pass, g_fail, rc ? " (stopped early)" : "");
    return rc ? rc : g_fail;
}

static int Run(const wchar_t* exe) {
    // MSVCR80/MSVCP80 are side-by-side assemblies: activate the exe's own manifest (resource 1) for the import resolution
    ACTCTXW ac = {sizeof(ac)};
    ac.dwFlags = ACTCTX_FLAG_RESOURCE_NAME_VALID;
    ac.lpSource = exe;
    ac.lpResourceName = MAKEINTRESOURCEW(1);
    HANDLE actx = CreateActCtxW(&ac);
    ULONG_PTR cookie = 0;
    if (actx == INVALID_HANDLE_VALUE || !ActivateActCtx(actx, &cookie)) Log("warning: activation context failed (err %lu)\n", GetLastError());
    if (!VirtualFree((void*)(uintptr_t)IMG_LO, 0, MEM_RELEASE)) Log("warning: no reservation to release (err %lu)\n", GetLastError());
    HMODULE h = LoadLibraryExW(exe, NULL, LOAD_WITH_ALTERED_SEARCH_PATH);
    if (!h) { Log("STEP 1 FAIL: LoadLibraryExW err %lu\n", GetLastError()); return 10; }
    if (!CheckImage(h)) return 11;
    Log("IAT [0xF95140] after LoadLibraryExW = %08lX (GetModuleFileNameW)\n", *(DWORD*)(uintptr_t)0xF95140);
    if (!ResolveImports(h)) { Log("STEP 1 FAIL: imports\n"); return 12; }
    Log("STEP 1 OK: exe mapped as image, imports resolved, entry point not run\n");

    static const struct { DWORD at; const void* to; } patches[] = {
        {0xD70A10, (const void*)GMalloc}, {0xD70A30, (const void*)GMalloc0}, {0xD70A60, (const void*)GFree},
        {0xD70A80, (const void*)GStrdup}, {0xD71860, (const void*)GRealloc},
        {0x4E69D0, (const void*)EaAlloc}, {0x4E5920, (const void*)EaFree}, {0x4E6A70, (const void*)EaRealloc},
        {0xD6FCB0, (const void*)LoadImageByName}, {0xD6FC40, (const void*)ConfigPath},
    };
    for (auto& p : patches)
        if (!Jmp(p.at, p.to)) { Log("STEP 2 FAIL: patch 0x%08lX\n", p.at); return 20; }
    Log("STEP 2 OK: eglib g_malloc/g_malloc0/g_free/g_strdup/g_realloc and GeneralAllocator alloc/free/realloc -> host CRT\n");
    Log("STEP 3 OK: image loader 0xD6FCB0 and config path 0xD6FC40 detoured (assemblies from %s)\n", g_asmDir);
    // MintCall before the JIT: its exits jump to the table entries read at install
    if (!InstallMint() || !InstallSubFuse() || !InstallMintCall() || !InstallJit()) { Log("STEP 3 FAIL: mint instrumentation (docs/139)\n"); return 30; }
    {  // docs/206 §7: MONOHOST_SATB=1 wraps mono's runtime icall table before any icall is resolved (as ts3native at DllMain)
        char sv[4] = "";
        GetEnvironmentVariableA("MONOHOST_SATB", sv, sizeof(sv));
        if (sv[0] == '1') Log("satb: %d runtime icall table entries wrapped\n", mt::SatbInstallRt());
    }

    void* domain = ((InterpInit)0xE5A680)("MonoEngine");
    void* corlib = *(void**)(uintptr_t)0x1220E40;          // mono_defaults.corlib
    void* objClass = *(void**)(uintptr_t)(0x1220E40 + 4);  // mono_defaults.object_class
    Log("mono_interp_init -> domain %p, corlib image %p, object_class %p\n", domain, corlib, objClass);
    if (!domain || !corlib || !objClass) { Log("STEP 4 FAIL\n"); return 40; }
    Log("STEP 4 OK: root domain with corlib\n");

    int status = -1;
    void* img = LoadImageByName("ScriptCore.dll");
    void* asmb = img ? ((AsmLoadFromFull)0xE68290)(img, "ScriptCore.dll", &status, 0) : NULL;
    Log("ScriptCore image %p assembly %p status %d\n", img, asmb, status);
    if (!asmb) { Log("STEP 5 FAIL: ScriptCore assembly\n"); return 50; }
    void* klass = ((ClassFromName)0xE6FA90)(img, "ScriptCore", "Route");
    void* method = klass ? ((MethodFromNameFlags)0xE70330)(klass, "GetNumPointsForZeroToDiameterApprox", 2, 0) : NULL;
    Log("class ScriptCore.Route %p, method GetNumPointsForZeroToDiameterApprox %p\n", klass, method);
    if (!method) { Log("STEP 5 FAIL: method lookup\n"); return 51; }
    Log("STEP 5 OK: ScriptCore loaded, static method resolved\n");
    if (g_mode == 3) {
        // as EA's assembly load 0xD7C5A0 does once SimIFace is in: the transform (0xE5DD1C) then turns calls to
        // Sims3.SimIFace.Simulator::Sleep into the yield opcode 0x1A6 and CheckYieldingContext into 0x1A7/0x1A8
        void* simK = Cls(Served("SimIFace.dll"), "Sims3.SimIFace", "Simulator");
        *(void**)(uintptr_t)0x1220F34 = simK;
        Log("yield intrinsics: [0x1220F34] = Sims3.SimIFace.Simulator %p\n", simK);
    }
    // (diameter, spacing) -> uint: (100, 1) = 7920 (10000 / 1.262586), (1, 0) = 1; checks that the body really ran
    static const float cases[2][3] = {{100.0f, 1.0f, 7920.0f}, {1.0f, 0.0f, 1.0f}};
    for (auto& t : cases) {
        float a = t[0], b = t[1];
        void* args[2] = {&a, &b};
        void* exc = NULL;
        void* ret = ((RuntimeInvoke)0xEA1090)(method, NULL, args, &exc);
        DWORD* o = (DWORD*)ret;
        Log("invoke(%g, %g) -> boxed %p, exception %p", a, b, ret, exc);
        if (o) Log(", object words %08lX %08lX %08lX %08lX", o[0], o[1], o[2], o[3]);
        Log("\n");
        // the boxed value sits at +4: this build's MonoObject header is the vtable pointer only (no sync word)
        if (!ret || exc || o[1] != (DWORD)t[2]) { Log("STEP 6 FAIL: expected %u\n", (unsigned)t[2]); return 60; }
    }
    Log("STEP 6 OK: managed static method ran in the offline host with the expected results\n");
    if (g_mode == 3) Check("M1.1-invoke", true, "exe mapped, imports, allocator, Mono init, ScriptCore, Route method (100, 1) -> 7920");
    return g_mode == 1 ? Probe(domain, img) : g_mode == 2 ? M13(domain) : g_mode == 3 ? SelfTest(domain) : 0;
}

#include "monohost-image.cpp"  // M3.1 imagecap / M3.2 image (docs/123 §8)

int wmain(int argc, wchar_t** argv) {
    if (argc < 3) { printf("usage: monohost <TS3W.exe copy> <assembly folder>\n"); return 1; }
    char imgDir[MAX_PATH] = "", helper[MAX_PATH] = "";
    const int image = argc > 4 && !wcscmp(argv[3], L"image") ? 2 : argc > 4 && !wcscmp(argv[3], L"imagecap") ? 1 : 0;
    if (image) WideCharToMultiByte(CP_ACP, 0, argv[4], -1, imgDir, MAX_PATH, NULL, NULL);
    if (!GetEnvironmentVariableA("MONOHOST_CHILD", NULL, 0)) return image == 2 ? ImageParent(imgDir) : RunChild();
    WideCharToMultiByte(CP_ACP, 0, argv[2], -1, g_asmDir, MAX_PATH, NULL, NULL);
    g_mode = argc > 3 && !wcscmp(argv[3], L"probe") ? 1 : argc > 4 && !wcscmp(argv[3], L"m13") ? 2 : argc > 4 && !wcscmp(argv[3], L"selftest") ? 3 : 0;
    if (g_mode >= 2) {
        WideCharToMultiByte(CP_ACP, 0, argv[4], -1, g_hostdata, MAX_PATH, NULL, NULL);
        g_lenient = argc > 5 && !wcscmp(argv[5], L"lenient");
        g_m2 = argc > 5 && !wcscmp(argv[5], L"m2");  // m13 with the M2 hand-written stubs (selftest always uses them)
    }
    char logPath[MAX_PATH];
    GetModuleFileNameA(NULL, logPath, MAX_PATH);
    strcat_s(logPath, ".log");
    fopen_s(&g_log, logPath, "w");
    AddVectoredExceptionHandler(1, Veh);
    SetUnhandledExceptionFilter(Unhandled);
    int rc = image == 2 ? ImageChild(argv[1], imgDir) : Run(argv[1]);
    if (!rc && image == 1) {  // helper: argument 5, else native\worldimage\out\worldimage.exe next to this tree
        if (argc > 5) WideCharToMultiByte(CP_ACP, 0, argv[5], -1, helper, MAX_PATH, NULL, NULL);
        else { GetModuleFileNameA(NULL, helper, MAX_PATH); strcpy_s(strrchr(helper, '\\') + 1, 64, "..\\..\\worldimage\\out\\worldimage.exe"); }
        rc = ImageCapture(imgDir, helper);
    }
    Log("exit %d\n", rc);
    if (g_log) fclose(g_log);
    TerminateProcess(GetCurrentProcess(), rc);  // no teardown of the mapped game
    return rc;
}
