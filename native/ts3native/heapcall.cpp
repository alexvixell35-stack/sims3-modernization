// [Crash] HeapCall (docs/51 §948, docs/260 §8; default 1, log only, Rodney OK 2026-11-26 (keep-crash-logging); needs [Crash] Log=1):
// crash logging for a call into data. [Crash] HeapCallDump (default 0) adds the minidumps.
// Included by ts3native.cpp after stallwatch.cpp (CrStack / CrFrame of crashreport.cpp, SsNameOf / SsJitAt of stallwatch.cpp).
//
// m1124c bp-vbowner-correct-5 died with 0xC0000096 and no line of ours (I-561-1: the fault never reached the VEH), but 10 s
// earlier the main thread ran heap bytes: `exception C0000005 at 0x68746182`, eax 0x68746170 = ASCII "path", one stack ref.
// The same swallowed heap-call AVs show up in passing vbowner runs (m1124b, bundle1124, m1122). TS3W.exe is not NX-compatible,
// so such a call runs heap bytes until something faults; an EA __except swallows that fault and the damage surfaces later
// (docs/260: "Ligh" = 0x6867694C, a destroyed light's vtable slot). This logs the swallowed call where it happens:
//   - any exception whose eip is in no loaded image and not in [MonoJit] code (heap / stack / free memory), and every
//     0xC0000096 / 0xC000001D wherever it is: one `heapcall:` line (eip region, bytes at eip, the registers, the first dword
//     at ecx / esi / edi / eax with the region it points into, thread, local time with ms, tick, the repro step a harness
//     agent wrote to ts3native_reprostep) and one `heapcall stack:` line (16 return addresses, module+rva[name]);
//   - with [Crash] HeapCallDump=1, for 0xC0000096 / 0xC000001D also a minidump next to the log (ts3native-heapcall-<pid>-<n>.dmp),
//     written by a helper thread with a 30 s wait, so a held loader or heap lock costs a timeout, never a hang.
// Rate limit: 48 lines (heap eip) + 8 lines (C0000096 / C000001D) + 2 dumps per process. Log only: XcptLog goes on as before
// and returns EXCEPTION_CONTINUE_SEARCH (the game's handlers and Windows decide as without it).
// m1124g tags (docs/260 §9, docs/51 §948):
//   - " in GC heap sect N" after the eip region when eip lies in a Boehm heap section (GC_heap_sects 0x122D714 {start, bytes},
//     GC_n_heap_sects [0x11F2FC0], docs/198 §3.2): heap bytes run as code are a managed object's or a free GC cell's;
//   - "  heapcall #N: handled, continued" 3 s after a logged line when the process still runs and no unhandled-exception
//     report started (g_crBusy): some __except swallowed it and the game went on (one timer-queue timer per logged line);
//   - counter hcdAv656277 (v226, +0x6B8C): AVs at TS3W 0x656277, the in-module AV of Apex 4.1.0's stale bake lamp (a vtable
//     +0x20 call on a light deleted in Buy mode, BakeTest via TerrainStamps::Update), so a queue can forbid it
//     (nativeRequire "@hcdAv656277<=0"). Steam 1.67 only: the three addresses have 0 rows on EA 1.69 / retail.
#include <dbghelp.h>  // MINIDUMP_* types only; dbghelp.dll is loaded on the helper thread, at the first dump

extern "C" __declspec(dllexport) volatile LONG ts3native_reprostep = 0;  // a harness agent's step counter (profile-world.ts), 0 = none
static bool g_hcdOn;
static volatile LONG g_hcdN, g_hcdWildN, g_hcdDumpN, g_hcdDumpBusy;
static LONG g_hcdMax = 48, g_hcdWildMax = 8, g_hcdDumpMax = 2;
static wchar_t g_hcdDir[MAX_PATH];  // the log's folder ("" = no dump: [Crash] HeapCallDump=0)
static const LONG* g_hcdSectN = (const LONG*)(uintptr_t)TS3A(0x011F2FC0);    // GC_n_heap_sects (0 on EA / retail: no tag)
static const DWORD* g_hcdSects = (const DWORD*)(uintptr_t)TS3A(0x0122D714);  // GC_heap_sects[], {start, bytes} each
static DWORD g_hcdAvEip = TS3A(0x00656277);  // Apex 4.1.0 stale bake lamp AV site (docs/260 §9); 0 = not counted
static DWORD g_hcdAliveMs = 3000;            // "handled, continued" check delay; 0 = no timer (selftest)
static DWORD g_hcdTid[2][64];                // [wild][#-1]: the faulting thread of each logged line

// " \"path\"" when the 4 bytes of v (little-endian) are printable ASCII, as the 0x68746170 / 0x6867694C vtable slots were
static int HcdAscii(DWORD v, char* out, size_t cap) {
    char s[5];
    for (int i = 0; i < 4; i++) {
        const char ch = (char)((v >> (8 * i)) & 0xFF);
        if (ch < 0x20 || ch > 0x7E) return out[0] = 0, 0;
        s[i] = ch;
    }
    s[4] = 0;
    return SFmt(out, cap, " \"%s\"", s);
}
// what v points into: "low", "free", "reserved", "TS3W+0x5F39F8[name+0x10]" (image), "jit+0x..", "stack", or
// "heap 0x356A0000+0x90C0 prot 04" (private / mapped memory); plus the ASCII form of v itself
static void HcdWhere(DWORD v, char* out, size_t cap) {
    char a[16];
    HcdAscii(v, a, sizeof(a));
    MEMORY_BASIC_INFORMATION mi;
    if (v < 0x10000) { SFmt(out, cap, "low%s", a); return; }
    if (!VirtualQuery((const void*)(uintptr_t)v, &mi, sizeof(mi)) || mi.State == MEM_FREE) { SFmt(out, cap, "free%s", a); return; }
    if (mi.State == MEM_RESERVE) { SFmt(out, cap, "reserved%s", a); return; }
    if (mi.Type == MEM_IMAGE) {
        char f[160];
        CrFrame(v, f, sizeof(f));
        DWORD off = 0;
        bool approx = false;
        const char* const nm = SsNameOf(v, &off, &approx);
        if (nm) SFmt(out, cap, "%s[%s%s+0x%lX]%s", f, approx ? "~" : "", nm, off, a);
        else SFmt(out, cap, "%s%s", f, a);
        return;
    }
    if (SsJitAt(v)) { SFmt(out, cap, "jit+0x%lX%s", v - (DWORD)(uintptr_t)g_jitCode, a); return; }
    const NT_TIB* tib = (const NT_TIB*)NtCurrentTeb();
    const DWORD lo = *(const DWORD*)((const u8*)tib + 0xE0C), hi = (DWORD)(uintptr_t)tib->StackBase;  // DeallocationStack
    if (v >= lo && v < hi) { SFmt(out, cap, "stack%s", a); return; }
    const DWORD base = (DWORD)(uintptr_t)mi.AllocationBase;
    SFmt(out, cap, "%s 0x%08lX+0x%lX prot %02lX%s", mi.Type == MEM_MAPPED ? "mapped" : "heap", base, v - base, mi.Protect, a);
}
// the Boehm heap section that holds v, -1 = none / unknown (MAX_HEAP_SECTS 0x300, docs/542)
static int HcdGcSect(DWORD v) {
    if (!g_hcdSectN || !g_hcdSects || !Readable(g_hcdSectN, 4)) return -1;
    const LONG n = *g_hcdSectN < 0x300 ? *g_hcdSectN : 0x300;
    if (n <= 0 || !Readable(g_hcdSects, (size_t)n * 8)) return -1;
    for (LONG i = 0; i < n; i++)
        if (v - g_hcdSects[2 * i] < g_hcdSects[2 * i + 1]) return (int)i;
    return -1;
}
// a logged line g_hcdAliveMs later: alive, no unhandled-exception report = an __except swallowed it and the game went on
static void HcdAlive(UINT_PTR p) {
    const int wild = (int)(p >> 16);
    const LONG n = (LONG)(p & 0xFFFF);
    if (g_crBusy || n < 1 || n > 64) return;
    const DWORD tid = g_hcdTid[wild][n - 1];
    DWORD ec = 0;
    const HANDLE t = OpenThread(THREAD_QUERY_LIMITED_INFORMATION, FALSE, tid);
    const bool run = t && GetExitCodeThread(t, &ec) && ec == STILL_ACTIVE;
    if (t) CloseHandle(t);
    Log("  heapcall #%ld%s: handled, continued (process alive %lu ms later, no crash report; thread %lu %s)", n, wild ? " wild" : "",
        g_hcdAliveMs, tid, run ? "still runs" : "gone");
}
static VOID CALLBACK HcdAliveTimer(PVOID p, BOOLEAN) { HcdAlive((UINT_PTR)p); }
// eip in no image and not in [MonoJit] code
// shortcut: Mono's own code manager chunks (private executable memory) are not skipped: Mono is linked into TS3W and no
// range or jit-info lookup is mapped, so an AV in Mono-jitted code (a NullReferenceException) logs as heapcall. Queue
// heapcall-monojit-off-1126-m1126b measures that with [MonoJit] off; skip those chunks once the lookup is mapped.
static bool HcdOutside(DWORD eip) {
    MEMORY_BASIC_INFORMATION mi;
    if (SsJitAt(eip)) return false;
    return !VirtualQuery((const void*)(uintptr_t)eip, &mi, sizeof(mi)) || mi.State != MEM_COMMIT || mi.Type != MEM_IMAGE;
}
// " ecx 356A90C0 [heap ..] -> 68746170 [..]": the register, its region and, if readable, its first dword (a vtable?) and that region
static int HcdReg(const char* n, DWORD v, char* out, size_t cap) {
    char w[200], d[200];
    HcdWhere(v, w, sizeof(w));
    if (v < 0x10000 || !Readable((const void*)(uintptr_t)v, 4)) return SFmt(out, cap, "; %s %08lX [%s]", n, v, w);
    const DWORD first = *(const DWORD*)(uintptr_t)v;
    HcdWhere(first, d, sizeof(d));
    return SFmt(out, cap, "; %s %08lX [%s] -> %08lX [%s]", n, v, w, first, d);
}

// the faulting thread's record and context are copied in: a timed-out helper must never read the handler's dead stack
struct HcdDumpReq { EXCEPTION_RECORD er; CONTEXT ctx; EXCEPTION_POINTERS x; DWORD tid; wchar_t path[MAX_PATH]; BOOL ok; DWORD err; };
static HcdDumpReq g_hcdReq;  // static: a timed-out helper thread may still read it after the handler returned
typedef BOOL(WINAPI* HcdMdwFn)(HANDLE, DWORD, HANDLE, MINIDUMP_TYPE, PMINIDUMP_EXCEPTION_INFORMATION, PMINIDUMP_USER_STREAM_INFORMATION,
                              PMINIDUMP_CALLBACK_INFORMATION);
static DWORD WINAPI HcdDumpThread(void* p) {
    HcdDumpReq* r = (HcdDumpReq*)p;
    const HMODULE h = LoadLibraryW(L"dbghelp.dll");
    const HcdMdwFn f = h ? (HcdMdwFn)(void*)GetProcAddress(h, "MiniDumpWriteDump") : NULL;
    if (!f) return r->err = GetLastError(), 0;
    const HANDLE fh = CreateFileW(r->path, GENERIC_WRITE, FILE_SHARE_READ, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (fh == INVALID_HANDLE_VALUE) return r->err = GetLastError(), 0;
    MINIDUMP_EXCEPTION_INFORMATION mei = {r->tid, &r->x, FALSE};
    const MINIDUMP_TYPE type = (MINIDUMP_TYPE)(MiniDumpWithIndirectlyReferencedMemory | MiniDumpWithUnloadedModules | MiniDumpWithHandleData |
                                               MiniDumpWithProcessThreadData | MiniDumpWithThreadInfo);
    r->ok = f(GetCurrentProcess(), GetCurrentProcessId(), fh, type, &mei, NULL, NULL);
    if (!r->ok) r->err = GetLastError();
    CloseHandle(fh);
    return 0;
}
// one dump at a time, g_hcdDumpMax per process; a helper thread because MiniDumpWriteDump should not run on the faulting thread
static void HcdDump(const EXCEPTION_POINTERS* x) {
    if (!g_hcdDir[0] || InterlockedCompareExchange(&g_hcdDumpBusy, 1, 0)) return;
    const LONG n = InterlockedIncrement(&g_hcdDumpN);
    if (n > g_hcdDumpMax) return;  // busy stays 1: no more dumps
    HcdDumpReq& r = g_hcdReq;
    r.er = *x->ExceptionRecord, r.er.ExceptionRecord = NULL;  // a chained record lives on that stack too: dropped
    r.ctx = *x->ContextRecord, r.x.ExceptionRecord = &r.er, r.x.ContextRecord = &r.ctx;
    r.tid = GetCurrentThreadId(), r.ok = FALSE, r.err = 0;
    if (!PathFmt(r.path, L"%s\\ts3native-heapcall-%lu-%ld.dmp", g_hcdDir, GetCurrentProcessId(), n)) return;
    const HANDLE t = CreateThread(NULL, 256 * 1024, HcdDumpThread, &r, STACK_SIZE_PARAM_IS_A_RESERVATION, NULL);
    if (!t) { Log("heapcall dump: thread failed (err %lu)", GetLastError()); g_hcdDumpBusy = 0; return; }
    const DWORD w = WaitForSingleObject(t, 30000);
    CloseHandle(t);
    if (w != WAIT_OBJECT_0) { Log("heapcall dump: no answer in 30 s (a lock held by this thread?), no more dumps"); return; }
    Log("heapcall dump: %s %ls (err %lu)", r.ok ? "written" : "FAILED", wcsrchr(r.path, L'\\') + 1, r.err);
    g_hcdDumpBusy = 0;
}

// called by XcptLog for every exception it does not own, before its line caps
static void HcdOnXcpt(const EXCEPTION_POINTERS* x) {
    if (!g_hcdOn) return;
    const EXCEPTION_RECORD* r = x->ExceptionRecord;
    const CONTEXT* c = x->ContextRecord;
    const bool wild = XcptWild(r->ExceptionCode);
    if (r->ExceptionCode == EXCEPTION_ACCESS_VIOLATION && g_hcdAvEip && c->Eip == g_hcdAvEip)
        InterlockedIncrement(&ts3native_counters.hcdAv656277);  // in-module: no line here (XcptLog logs it), the counter only
    if (!wild && !HcdOutside(c->Eip)) return;
    const LONG n = InterlockedIncrement(wild ? &g_hcdWildN : &g_hcdN);
    if (n > (wild ? g_hcdWildMax : g_hcdMax)) return;
    char b[2048], w[200];
    SYSTEMTIME st;
    GetLocalTime(&st);
    HcdWhere(c->Eip, w, sizeof(w));
    const int gs = HcdGcSect(c->Eip);
    char g[32] = "";
    if (gs >= 0) SFmt(g, " in GC heap sect %d", gs);
    int k = SFmt(b, "heapcall: #%ld%s code %08lX (info %08lX %08lX) eip %08lX [%s]%s thread %lu at %02u:%02u:%02u.%03u tick %lu step %ld; bytes at eip:",
                 n, wild ? " wild" : "", r->ExceptionCode, r->NumberParameters > 0 ? (DWORD)r->ExceptionInformation[0] : 0,
                 r->NumberParameters > 1 ? (DWORD)r->ExceptionInformation[1] : 0, c->Eip, w, g, GetCurrentThreadId(), st.wHour, st.wMinute,
                 st.wSecond, st.wMilliseconds, GetTickCount(), ts3native_reprostep);
    if (Readable((const void*)(uintptr_t)c->Eip, 16))
        for (int i = 0; i < 16; i++) k += SFmt(b + k, sizeof(b) - k, " %02X", ((const u8*)(uintptr_t)c->Eip)[i]);
    else k += SFmt(b + k, sizeof(b) - k, " unreadable");
    k += HcdReg("ecx", c->Ecx, b + k, sizeof(b) - k);
    k += HcdReg("esi", c->Esi, b + k, sizeof(b) - k);
    k += HcdReg("edi", c->Edi, b + k, sizeof(b) - k);
    k += HcdReg("eax", c->Eax, b + k, sizeof(b) - k);
    SFmt(b + k, sizeof(b) - k, "; ebx %08lX edx %08lX ebp %08lX esp %08lX", c->Ebx, c->Edx, c->Ebp, c->Esp);
    Log("%s", b);
    DWORD ret[16];
    const int nr = CrStack(c->Esp, ret, 16);
    k = SFmt(b, "  heapcall stack (%d return addresses from esp):", nr);
    for (int i = 0; i < nr; i++) {
        HcdWhere(ret[i], w, sizeof(w));
        k += SFmt(b + k, sizeof(b) - k, " %s", w);
    }
    Log("%s", b);
    if (wild) HcdDump(x);
    g_hcdTid[wild][n - 1] = GetCurrentThreadId();
    HANDLE tm;  // shortcut: fired one-shot timers are never deleted (56 per process at most)
    if (g_hcdAliveMs && !CreateTimerQueueTimer(&tm, NULL, HcdAliveTimer, (PVOID)(UINT_PTR)((wild ? 1u << 16 : 0u) | (DWORD)n), g_hcdAliveMs,
                                               0, WT_EXECUTEONLYONCE))
        Log("  heapcall #%ld: no alive-check timer (err %lu)", n, GetLastError());
}

static void HcdInstall(const wchar_t* ini) {
    g_hcdOn = GetPrivateProfileIntW(L"Crash", L"HeapCall", 1, ini) != 0;
    if (!g_hcdOn) { Log("crash log: heapcall detector off ([Crash] HeapCall=0)"); return; }
    if (!g_ssExe) g_ssExe = (DWORD)(uintptr_t)GetModuleHandleW(NULL), g_ssExeSize = (DWORD)TS3A_HOST_SIZE;  // SsNameOf (names only)
    g_hcdDir[0] = 0;
    if (GetPrivateProfileIntW(L"Crash", L"HeapCallDump", 0, ini)) {  // minidumps: opt-in (queues / triage)
        WCpy(g_hcdDir, ini);
        if (wchar_t* s = wcsrchr(g_hcdDir, L'\\')) *s = 0; else g_hcdDir[0] = 0;
    }
    Log("OK   crash log: heapcall detector (docs/51 §948): eip outside every module, C0000096 or C000001D -> context + 16 return "
        "addresses, %ld + %ld lines, %ld minidumps at most ([Crash] HeapCallDump=%d); tags in GC heap, handled/continued after %lu ms; "
        "AV count at 0x%08lX", g_hcdMax, g_hcdWildMax, g_hcdDir[0] ? g_hcdDumpMax : 0L, g_hcdDir[0] ? 1 : 0, g_hcdAliveMs, g_hcdAvEip);
}
