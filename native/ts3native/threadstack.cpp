// [ThreadStack] (docs/66 candidate 2, §6): smaller stack reservations for the threads TS3W.exe starts itself. Off by default.
// Included by ts3native.cpp (one translation unit). Address space only: no game code or data is patched, two IAT slots are.
//
// The exe starts threads at 7 call sites through 2 IAT slots [verified, disassembly of Steam 1.67, 2026-10-20]:
//   KERNEL32!CreateThread 0xF95244: 4 sites in the third-party block 0xF49820..0xF6F969, all with stack size 0;
//   MSVCR80!_beginthreadex 0xF955D0: file watchers 0x4DB355 (size 0; S3SS OversizedThreadStackFix rewrites that call),
//     EAThread_Begin 0x4E2FCE and EAThread_BeginRunnable 0x4E323E (size = their params +4, 0 without params; entries
//     0x4E2E30 / 0x4E30A0 get a block whose +0x10 is the user function, or the runnable object for 0x4E30A0).
// A non-zero size without STACK_SIZE_PARAM_IS_A_RESERVATION is a commit size: Windows still reserves the PE default
// (SizeOfStackReserve 1 MB), so every exe thread reserves >= 1 MB today. Only the exe's IAT is swapped, so DLL threads
// (DXVK, driver, S3SS, Frida, our own workers) keep their stacks; S3PP ThreadStackDiet (kernel32 detour, size 0 only)
// sees our explicit size and passes it on unchanged, so the two compose.
// Rule: reserve = max(requested, ReserveKB) with the reservation flag, only when that is below the PE default and the
// caller did not set the flag itself. A thread whose start (or EAThread user function) is in Skip keeps the game's stack
// (default: SimulatorThread_Proc 0x76B470, the Mono interpreter's deep native recursion runs there, docs/36).
// ReserveKB=0: observe only, every start logged with its real reservation and nothing changed.
// MSVCR80's _beginthreadex hands initflag on to CreateThread [inferred from the VC8 CRT source]; the "reserved" readout
// in the log (TEB StackBase - DeallocationStack) checks it per thread.

static const DWORD TS_IAT_CREATETHREAD = TS3A(0x00F95244), TS_IAT_BEGINTHREADEX = TS3A(0x00F955D0);
static const DWORD TS_EATHREAD_ENTRY = TS3A(0x004E2E30), TS_EATHREAD_ENTRY_RUNNABLE = TS3A(0x004E30A0);

typedef HANDLE(WINAPI* TsCreateThreadFn)(LPSECURITY_ATTRIBUTES, SIZE_T, LPTHREAD_START_ROUTINE, LPVOID, DWORD, LPDWORD);
typedef uintptr_t(__cdecl* TsBeginThreadExFn)(void*, unsigned, unsigned(__stdcall*)(void*), void*, unsigned, unsigned*);

static TsCreateThreadFn g_tsCreateOrig;
static TsBeginThreadExFn g_tsBeginOrig;
static DWORD g_tsReserve;             // bytes; 0 = observe only
static DWORD g_tsDefault = 0x100000;  // the exe's PE SizeOfStackReserve (read at install)
static DWORD g_tsSkip[8];
static int g_tsSkipN;
static LONG g_tsLogMax = 64;
static volatile LONG g_tsSeen, g_tsCapped, g_tsSavedKB;

// New size and flags for one thread start; true when changed (pure, selftest)
static bool TsDecide(DWORD size, DWORD flags, DWORD userFn, DWORD* outSize, DWORD* outFlags) {
    *outSize = size;
    *outFlags = flags;
    if (!g_tsReserve || (flags & STACK_SIZE_PARAM_IS_A_RESERVATION)) return false;
    for (int i = 0; i < g_tsSkipN; i++)
        if (g_tsSkip[i] == userFn) return false;
    const DWORD r = size > g_tsReserve ? size : g_tsReserve;
    if (r >= g_tsDefault) return false;
    *outSize = r;
    *outFlags = flags | STACK_SIZE_PARAM_IS_A_RESERVATION;
    return true;
}

// The function the thread really runs: EAThread entries carry it at arglist +0x10 (the Runnable entry: object, Run at vtable +4)
static DWORD TsUserFn(DWORD start, const void* arg) {
    if ((start != TS_EATHREAD_ENTRY && start != TS_EATHREAD_ENTRY_RUNNABLE) || !arg || !Readable((const u8*)arg + 0x10, 4)) return start;
    const DWORD f = *(const DWORD*)((const u8*)arg + 0x10);
    if (start == TS_EATHREAD_ENTRY || !Readable((const void*)(uintptr_t)f, 4)) return f;
    const DWORD* vt = *(const DWORD* const*)(uintptr_t)f;
    return Readable(vt + 1, 4) ? vt[1] : f;
}

// Real reservation of a just-created thread in KB: TEB StackBase (+4) minus DeallocationStack (+0xE0C); -1 = unknown
typedef LONG(WINAPI* TsNtQitFn)(HANDLE, int, void*, ULONG, ULONG*);
struct TsTbi { LONG exitStatus; void* teb; void* pid; void* tid; ULONG_PTR affinity; LONG prio, basePrio; };
static long TsReserveKB(HANDLE h) {
    static TsNtQitFn q = (TsNtQitFn)(void*)GetProcAddress(GetModuleHandleW(L"ntdll.dll"), "NtQueryInformationThread");
    TsTbi b = {};
    if (!h || !q || q(h, 0, &b, sizeof(b), NULL) < 0 || !b.teb || !Readable(b.teb, 0xE10)) return -1;
    const DWORD top = *(const DWORD*)((const u8*)b.teb + 4), lo = *(const DWORD*)((const u8*)b.teb + 0xE0C);
    return top > lo && top - lo <= (64u << 20) ? (long)((top - lo) >> 10) : -1;
}

static void TsNote(const char* via, DWORD caller, DWORD start, DWORD fn, DWORD size, DWORD flags, bool changed, bool retried, HANDLE h) {
    const LONG n = InterlockedIncrement(&g_tsSeen);
    const long got = TsReserveKB(h);
    if (changed && !retried) {
        InterlockedIncrement(&g_tsCapped);
        if (got > 0 && (DWORD)got < (g_tsDefault >> 10)) InterlockedExchangeAdd(&g_tsSavedKB, (LONG)(g_tsDefault >> 10) - got);
    }
    if (n <= g_tsLogMax)
        Log("thread stack #%ld %s from 0x%08lX: start 0x%08lX fn 0x%08lX asked %lu KB flags %08lX: %s, reserved %ld KB (capped %ld, saved %ld KB)",
            n, via, caller, start, fn, size >> 10, flags, !h ? "FAILED" : retried ? "cap refused, retried as asked" : changed ? "capped" : "as asked",
            got, g_tsCapped, g_tsSavedKB);
}

static HANDLE WINAPI TsCreateThread(LPSECURITY_ATTRIBUTES sa, SIZE_T size, LPTHREAD_START_ROUTINE start, LPVOID arg, DWORD flags, LPDWORD tid) {
    const DWORD caller = (DWORD)(uintptr_t)_ReturnAddress(), st = (DWORD)(uintptr_t)start, fn = TsUserFn(st, arg);
    DWORD s, f;
    const bool changed = TsDecide((DWORD)size, flags, fn, &s, &f);
    HANDLE h = g_tsCreateOrig(sa, s, start, arg, f, tid);
    const bool retried = changed && !h;  // never let the cap be why a thread does not start
    if (retried) h = g_tsCreateOrig(sa, size, start, arg, flags, tid);
    TsNote("CreateThread", caller, st, fn, (DWORD)size, flags, changed, retried, h);
    return h;
}

static uintptr_t __cdecl TsBeginThreadEx(void* sa, unsigned size, unsigned(__stdcall* start)(void*), void* arg, unsigned flags, unsigned* tid) {
    const DWORD caller = (DWORD)(uintptr_t)_ReturnAddress(), st = (DWORD)(uintptr_t)start, fn = TsUserFn(st, arg);
    DWORD s, f;
    const bool changed = TsDecide(size, flags, fn, &s, &f);
    uintptr_t h = g_tsBeginOrig(sa, s, start, arg, f, tid);
    const bool retried = changed && !h;
    if (retried) h = g_tsBeginOrig(sa, size, start, arg, flags, tid);
    TsNote("_beginthreadex", caller, st, fn, size, flags, changed, retried, (HANDLE)h);
    return h;
}

// Swap one IAT slot only while it still holds the real export (someone else first = skip, never chain unknown code)
static bool TsSwap(DWORD* slot, void* real, void* hook, void** orig, const char* name) {
    if (!real || !Readable(slot, 4)) { Log("SKIP thread stack %s: IAT 0x%08lX unreadable or export missing", name, (DWORD)(uintptr_t)slot); return false; }
    if (*slot != (DWORD)(uintptr_t)real) {
        Log("SKIP thread stack %s: IAT 0x%08lX holds 0x%08lX, not the export 0x%08lX (another hook first?)", name, (DWORD)(uintptr_t)slot, *slot, (DWORD)(uintptr_t)real);
        return false;
    }
    *orig = real;
    const DWORD v = (DWORD)(uintptr_t)hook;
    return Write(slot, &v, 4);
}

// "0x76B470, 0x4DB220" -> g_tsSkip (at most 8)
static void TsParseSkip(const wchar_t* s) {
    g_tsSkipN = 0;
    while (*s && g_tsSkipN < (int)(sizeof(g_tsSkip) / sizeof(g_tsSkip[0]))) {
        wchar_t* end;
        const unsigned long v = wcstoul(s, &end, 16);
        if (end == s) { s++; continue; }
        if (v) g_tsSkip[g_tsSkipN++] = (DWORD)v;
        s = end;
    }
}

static bool ApplyThreadStack(const wchar_t* ini) {
    if (!GetPrivateProfileIntW(L"ThreadStack", L"Enabled", 0, ini)) return false;
    const int kb = GetPrivateProfileIntW(L"ThreadStack", L"ReserveKB", 512, ini);
    g_tsReserve = kb <= 0 ? 0 : (DWORD)Clamp(kb, 128, 1024) << 10;
    g_tsLogMax = Clamp(GetPrivateProfileIntW(L"ThreadStack", L"LogThreads", 64, ini), 0, 4096);
    wchar_t skip[256];
    GetPrivateProfileStringW(L"ThreadStack", L"Skip", L"0x76B470", skip, 256, ini);
    TsParseSkip(skip);
    const u8* exe = (const u8*)GetModuleHandleW(NULL);
    const IMAGE_NT_HEADERS* nt = (const IMAGE_NT_HEADERS*)(exe + ((const IMAGE_DOS_HEADER*)exe)->e_lfanew);
    if (nt->OptionalHeader.SizeOfStackReserve) g_tsDefault = nt->OptionalHeader.SizeOfStackReserve;
    HMODULE k32 = GetModuleHandleW(L"kernel32.dll"), crt = GetModuleHandleW(L"msvcr80.dll");
    const bool a = TsSwap((DWORD*)(uintptr_t)TS_IAT_CREATETHREAD, (void*)GetProcAddress(k32, "CreateThread"), (void*)TsCreateThread,
                          (void**)&g_tsCreateOrig, "CreateThread");
    const bool b = crt && TsSwap((DWORD*)(uintptr_t)TS_IAT_BEGINTHREADEX, (void*)GetProcAddress(crt, "_beginthreadex"), (void*)TsBeginThreadEx,
                                 (void**)&g_tsBeginOrig, "_beginthreadex");
    Log("thread stack: %s ReserveKB=%lu (PE default %lu KB), %d skipped fn(s) (first 0x%08lX), log %ld starts; IAT CreateThread %s, _beginthreadex %s",
        g_tsReserve ? "cap" : "observe only", g_tsReserve >> 10, g_tsDefault >> 10, g_tsSkipN, g_tsSkipN ? g_tsSkip[0] : 0, g_tsLogMax,
        a ? "hooked" : "not hooked", b ? "hooked" : crt ? "not hooked" : "not hooked (msvcr80 not loaded)");
    return a || b;
}
