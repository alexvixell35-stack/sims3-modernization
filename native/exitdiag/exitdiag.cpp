// exitdiag.asi: logging-only diagnostics for the quit-to-desktop crash in UI/TriggerService teardown (docs/67 §47).
// Standalone (works with or without ts3native). Every hook logs and then runs the original instructions exactly; a site whose
// bytes are not the Steam 1.67 originals (someone else patched it, or another exe) is logged SKIP and left alone.
// Log: <Documents>\Electronic Arts\The Sims 3\ExitDiag\exitdiag-<time>-<pid>.log (+ inventory-<time>-<pid>.txt).
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <bcrypt.h>
#include <psapi.h>
#include <stdarg.h>
#include "exitdiag-core.h"

#define EXITDIAG_VERSION "1.0.0"

static HMODULE g_self;
static HANDLE g_log = INVALID_HANDLE_VALUE;
static CRITICAL_SECTION g_cs;
static volatile LONG g_exiting, g_lines, g_dropped;
static const LONG kMaxLines = 20000;
static wchar_t g_user[MAX_PATH], g_diag[MAX_PATH], g_bin[MAX_PATH], g_game[MAX_PATH], g_profile[MAX_PATH], g_uname[128];
static wchar_t g_stamp[64];
static bool g_exeOk;
static __declspec(thread) int t_probe;  // our own guarded reads: the VEH ignores their faults

static void LogRaw(const char* s, int n) {
    if (g_log == INVALID_HANDLE_VALUE) return;
    DWORD w;
    const bool lock = !g_exiting;  // at process exit other threads are gone, maybe holding g_cs
    if (lock) EnterCriticalSection(&g_cs);
    WriteFile(g_log, s, (DWORD)n, &w, NULL);  // unbuffered: what WriteFile returned survives a crash
    if (lock) LeaveCriticalSection(&g_cs);
}
static void Log(const char* fmt, ...) {
    if (InterlockedIncrement(&g_lines) > kMaxLines) { InterlockedIncrement(&g_dropped); return; }
    char buf[1100];
    SYSTEMTIME t;
    GetLocalTime(&t);
    int n = _snprintf_s(buf, sizeof(buf), _TRUNCATE, "%02u:%02u:%02u.%03u %5lu ", t.wHour, t.wMinute, t.wSecond,
                        t.wMilliseconds, GetCurrentThreadId());
    va_list a;
    va_start(a, fmt);
    const int m = _vsnprintf_s(buf + n, sizeof(buf) - n - 2, _TRUNCATE, fmt, a);
    va_end(a);
    n += m < 0 ? (int)strlen(buf + n) : m;
    buf[n++] = '\r';
    buf[n++] = '\n';
    LogRaw(buf, n);
}
static void Utf8(const wchar_t* w, char* out, int cap) {
    if (!WideCharToMultiByte(CP_UTF8, 0, w, -1, out, cap, NULL, NULL)) out[0] = 0;
}
static void MaskA(const wchar_t* in, char* out, int cap) {
    wchar_t m[1024];
    MaskPath(in, g_game, g_profile, g_uname, m, 1024);
    Utf8(m, out, cap);
}

static bool Rd(uint32_t a, void* d, uint32_t n, void*) {
    bool ok = true;
    t_probe++;
    __try { memcpy(d, (const void*)(uintptr_t)a, n); } __except (EXCEPTION_EXECUTE_HANDLER) { ok = false; }
    t_probe--;
    return ok;
}
static uint32_t Rd32(uint32_t a, bool* ok) {
    uint32_t v = 0;
    *ok = Rd(a, &v, 4, NULL);
    return v;
}

// ---- module table (snapshot; refreshed after a load/unload notification) ----
static ModRange g_mods[512];
static int g_nmods;
static volatile LONG g_modsDirty = 1;
static int Snap(ModRange* t, int max) {
    HMODULE h[512];
    DWORD need = 0;
    if (!K32EnumProcessModules(GetCurrentProcess(), h, sizeof(h), &need)) return 0;
    int n = (int)(need / sizeof(HMODULE));
    if (n > 512) n = 512;
    if (n > max) n = max;
    int o = 0;
    for (int i = 0; i < n; i++) {
        MODULEINFO mi;
        if (!K32GetModuleInformation(GetCurrentProcess(), h[i], &mi, sizeof(mi))) continue;
        t[o].base = (uint32_t)(uintptr_t)mi.lpBaseOfDll;
        t[o].size = mi.SizeOfImage;
        if (!K32GetModuleBaseNameA(GetCurrentProcess(), h[i], t[o].name, sizeof(t[o].name))) strcpy_s(t[o].name, "?");
        o++;
    }
    return o;
}
static void ModsFresh() {
    if (InterlockedExchange(&g_modsDirty, 0)) g_nmods = Snap(g_mods, 512);
}
struct A { char s[112]; };  // one formatted address
static A Fa(uint32_t a) {
    A r;
    ModFmt(g_mods, g_nmods, a, r.s, sizeof(r.s));
    return r;
}
static bool InExe(uint32_t a) { return a - 0x400000u < kExeImageSize; }
static bool InRdata(uint32_t a) { return a >= kRdataLo && a < kRdataHi; }

// ---- (1) module load/unload ----
struct UStr { USHORT Length, MaximumLength; PWSTR Buffer; };
struct DllNoteData { ULONG Flags; const UStr* FullDllName; const UStr* BaseDllName; PVOID DllBase; ULONG SizeOfImage; };
typedef VOID(CALLBACK* DllNoteFn)(ULONG reason, const DllNoteData* d, PVOID ctx);
typedef LONG(NTAPI* LdrRegFn)(ULONG flags, DllNoteFn fn, PVOID ctx, PVOID* cookie);
typedef LONG(NTAPI* LdrUnregFn)(PVOID cookie);
static PVOID g_noteCookie, g_veh;
static volatile LONG g_loads, g_unloads;
static VOID CALLBACK DllNote(ULONG reason, const DllNoteData* d, PVOID) {
    InterlockedExchange(&g_modsDirty, 1);
    wchar_t p[1024];
    const UStr* s = reason == 1 ? d->FullDllName : d->BaseDllName;
    const int n = s && s->Buffer ? min((int)s->Length / 2, 1023) : 0;
    wmemcpy(p, n ? s->Buffer : L"", n);
    p[n] = 0;
    char m[1100];
    MaskA(p, m, sizeof(m));
    const uint32_t b = (uint32_t)(uintptr_t)d->DllBase;
    if (reason == 1) { InterlockedIncrement(&g_loads); Log("module load   %08X-%08X %s", b, b + d->SizeOfImage, m); }
    else { InterlockedIncrement(&g_unloads); Log("module UNLOAD %08X-%08X %s", b, b + d->SizeOfImage, m); }
}

// ---- (5) exceptions: VEH (first chance, logs only) + unhandled filter (chains to the previous one) ----
static ModRange g_dmods[512];
static volatile LONG g_dumps, g_vehLines, g_vehAll, g_inUef;
static LPTOP_LEVEL_EXCEPTION_FILTER g_prevUef;
static void Dump(EXCEPTION_POINTERS* ep, const char* why) {
    if (InterlockedIncrement(&g_dumps) > 4) return;
    const EXCEPTION_RECORD* er = ep->ExceptionRecord;
    const CONTEXT* c = ep->ContextRecord;
    const int nm = Snap(g_dmods, 512);
    char a[112], b[112];
    ModFmt(g_dmods, nm, c->Eip, a, sizeof(a));
    Log("=== EXCEPTION (%s) code %08X flags %X at %08X info %X %X; EIP %s", why, er->ExceptionCode, er->ExceptionFlags,
        (uint32_t)(uintptr_t)er->ExceptionAddress, er->NumberParameters > 0 ? (uint32_t)er->ExceptionInformation[0] : 0,
        er->NumberParameters > 1 ? (uint32_t)er->ExceptionInformation[1] : 0, a);
    Log("regs EAX=%08X EBX=%08X ECX=%08X EDX=%08X ESI=%08X EDI=%08X EBP=%08X ESP=%08X EFL=%08X", c->Eax, c->Ebx, c->Ecx,
        c->Edx, c->Esi, c->Edi, c->Ebp, c->Esp, c->EFlags);
    const uint32_t regs[] = {c->Eax, c->Ebx, c->Ecx, c->Edx, c->Esi, c->Edi, c->Ebp};
    const char* rn[] = {"EAX", "EBX", "ECX", "EDX", "ESI", "EDI", "EBP"};
    for (int i = 0; i < 7; i++) {
        bool ok;
        const uint32_t v0 = Rd32(regs[i], &ok);
        ModFmt(g_dmods, nm, regs[i], a, sizeof(a));
        if (ok) ModFmt(g_dmods, nm, v0, b, sizeof(b));
        Log("  %s %s -> [0] %s", rn[i], a, ok ? b : "unreadable");
    }
    // stack: values that point into executable pages of a module (return addresses, code pointers)
    const uint32_t top = (uint32_t)(uintptr_t)((NT_TIB*)NtCurrentTeb())->StackBase;
    int shown = 0;
    for (uint32_t p = c->Esp; p + 4 <= top && p < c->Esp + 0x4000 && shown < 64; p += 4) {
        bool ok;
        const uint32_t v = Rd32(p, &ok);
        if (!ok) break;
        if (ModFind(g_dmods, nm, v) < 0) continue;
        MEMORY_BASIC_INFORMATION mbi;
        if (!VirtualQuery((void*)(uintptr_t)v, &mbi, sizeof(mbi)) || !(mbi.Protect & 0xF0)) continue;  // PAGE_EXECUTE*
        ModFmt(g_dmods, nm, v, a, sizeof(a));
        Log("  stack [ESP+%04X] %s", p - c->Esp, a);
        shown++;
    }
    Log("modules (%d):", nm);
    for (int i = 0; i < nm; i++) Log("  %08X-%08X %s", g_dmods[i].base, g_dmods[i].base + g_dmods[i].size, g_dmods[i].name);
    Log("=== end of exception report");
}
static LONG CALLBACK Veh(EXCEPTION_POINTERS* ep) {
    const DWORD code = ep->ExceptionRecord->ExceptionCode;
    // C++ throws, thread names and non-error codes are normal; a stack overflow leaves no stack to log on
    if (t_probe || code < 0xC0000000 || code == 0xE06D7363 || code == 0xC00000FD) return EXCEPTION_CONTINUE_SEARCH;
    InterlockedIncrement(&g_vehAll);
    const EXCEPTION_RECORD* er = ep->ExceptionRecord;
    // an execute fault (info[0] == 8) is a wild call/jump, the §47 shape: full report now, before anyone handles it
    if (code == EXCEPTION_ACCESS_VIOLATION && er->NumberParameters > 0 && er->ExceptionInformation[0] == 8) {
        Dump(ep, "first chance, execute fault");
    } else if (InterlockedIncrement(&g_vehLines) <= 32) {  // Mono null references show up here too: first 32 only
        char a[112];
        const int nm = Snap(g_dmods, 512);
        ModFmt(g_dmods, nm, ep->ContextRecord->Eip, a, sizeof(a));
        Log("first-chance %08X at %s info %X %X", code, a, er->NumberParameters > 0 ? (uint32_t)er->ExceptionInformation[0] : 0,
            er->NumberParameters > 1 ? (uint32_t)er->ExceptionInformation[1] : 0);
    }
    return EXCEPTION_CONTINUE_SEARCH;
}
static LONG WINAPI Uef(EXCEPTION_POINTERS* ep) {
    if (InterlockedExchange(&g_inUef, 1)) return EXCEPTION_CONTINUE_SEARCH;  // a filter chain that loops back to us
    Dump(ep, "unhandled");
    const LONG r = g_prevUef ? g_prevUef(ep) : EXCEPTION_CONTINUE_SEARCH;
    g_inUef = 0;
    return r;
}
static void SetUef() {
    LPTOP_LEVEL_EXCEPTION_FILTER p = SetUnhandledExceptionFilter(Uef);
    if (p != Uef) g_prevUef = p;
}

// ---- hooks ----
struct Regs { DWORD efl, edi, esi, ebp, esp, ebx, edx, ecx, eax; };  // pushad + pushfd
static volatile LONG g_hits[S_COUNT], g_bad;
static DWORD g_res[S_COUNT];             // addr + len: where each stub goes on
static DWORD g_tgt59F060 = 0x59F060;     // the call the S_MGR site replaced (E8 3E5C0100 at 0x58941D)
static uint8_t g_written[S_COUNT][8];    // our patch bytes, to see later whether someone overwrote them
static bool g_hooked[S_COUNT];

static void CheckPatches(const char* when) {
    for (int i = 0; i < S_COUNT; i++)
        if (g_hooked[i] && memcmp((void*)(uintptr_t)kSites[i].addr, g_written[i], kSites[i].len) != 0)
            Log("WARN %s: our patch at %08X (%s) was overwritten by someone else", when, kSites[i].addr, kSites[i].name);
}
static void Counters(const char* when) {
    Log("counters (%s): appshut %ld, eca300 %ld, 58b310 %ld, svcmgr %ld, svc %ld, hookdump %ld, win1 %ld, win2 %ld, bad %ld; "
        "loads %ld unloads %ld; first-chance %ld; dumps %ld; lines dropped %ld", when, g_hits[0], g_hits[1], g_hits[2],
        g_hits[3], g_hits[4], g_hits[5], g_hits[6], g_hits[7], g_bad, g_loads, g_unloads, g_vehAll, g_dumps, g_dropped);
}

static HookNode g_nodes[1024];
static void HookDump(uint32_t svc) {
    bool ok;
    const uint32_t vt = Rd32(svc, &ok);
    uint32_t elems = 0;
    const int n = WalkHooks(Rd, NULL, svc, g_nodes, 1024, &elems);
    Log("TriggerService 0x7F10B0 this=%08X vt=%s%s: %d hook nodes read (map count %u)", svc, Fa(vt).s,
        vt == kTriggerSvcVt ? "" : " (NOT the TriggerService vtable)", n, elems);
    for (int i = 0; i < n; i++) {
        const HookNode& h = g_nodes[i];
        const uint32_t wvt = Rd32(h.window, &ok);
        Log("  hook h=%X node=%08X flags=%02X%s win=%08X winvt=%s inExe=%d inRdata=%d prio?(+C)=%X raw +4=%X +8=%X +10=%X "
            "+14=%X +20=%X", h.handle, h.addr, h.flags & 0xFF, (h.flags & 0x10) ? "(active)" : "", h.window,
            ok ? Fa(wvt).s : "unreadable", ok && InExe(wvt), ok && InRdata(wvt), h.dC, h.d4, h.d8, h.d10, h.d14, h.obj20);
    }
}

static void WinCall(int id, Regs* r) {
    const LONG k = InterlockedIncrement(&g_hits[id]);
    bool ok, okh;
    bool oka;
    const uint32_t win = r->ebx, tgt = r->edx, arg = Rd32(r->esp, &oka);  // the stub pushed the argument (ecx is the window now)
    const uint32_t vt = Rd32(win, &ok);
    const uint32_t handle = Rd32(r->esp + 0x20, &okh);  // 0x7F0EF0's handle argument: [esp+0x1C] at the site, + the arg we pushed
    const bool bad = !ok || !InRdata(vt) || ModFind(g_mods, g_nmods, tgt) < 0;
    if (bad) InterlockedIncrement(&g_bad);
    if (k > 128 && !(bad && g_bad <= 256)) return;
    Log("%s win call %d: handle=%X svc=%08X window=%s vt=%s +0x10C=%s arg=%s", bad ? "BAD" : "ok", id == S_WIN1 ? 1 : 2,
        okh ? handle : 0, r->esi, Fa(win).s, ok ? Fa(vt).s : "unreadable", Fa(tgt).s, oka ? Fa(arg).s : "unreadable");
}

extern "C" void __cdecl OnSite(int id, Regs* r) {
    DWORD err = GetLastError();
    ModsFresh();
    switch (id) {
    case S_MGR:
        if (InterlockedIncrement(&g_hits[id]) <= 8) Log("service manager 0x589410 -> 0x59F060 this=%s", Fa(r->ecx).s);
        break;
    case S_SVC:  // ecx = service, eax = its vtable, edx = slot +0x18 (the stub ran mov edx,[eax+18])
        if (InterlockedIncrement(&g_hits[id]) <= 256)
            Log("service call this=%s vt=%s +0x18=%s", Fa(r->ecx).s, Fa(r->eax).s, Fa(r->edx).s);
        break;
    case S_HOOKS:
        if (InterlockedIncrement(&g_hits[id]) <= 4) HookDump(r->ecx);
        break;
    case S_WIN1:
    case S_WIN2:
        WinCall(id, r);
        break;
    }
    SetLastError(err);
}

// entry/exit: the entry stub swaps the return address for an exit thunk; a per-thread stack keeps the real one
struct RetRec { int id; DWORD ret, esp; };
static __declspec(thread) RetRec t_rets[16];
static __declspec(thread) int t_nrets;
extern "C" void Exit0();
extern "C" void Exit1();
extern "C" void Exit2();
static void (*const kExit[3])() = {Exit0, Exit1, Exit2};

extern "C" void __cdecl OnEnter(int id, Regs* r) {
    DWORD err = GetLastError();
    ModsFresh();
    const LONG k = InterlockedIncrement(&g_hits[id]);
    DWORD* ret = (DWORD*)(uintptr_t)r->esp;
    if (k <= 16) Log("enter %s this=%08X ret=%s", kSites[id].name, r->ecx, Fa(*ret).s);
    if (id == S_APPSHUT) {
        SetUef();  // the game or another mod may have replaced our filter since start
        CheckPatches("App_Shutdown");
        Counters("App_Shutdown entry");
    }
    if (t_nrets < 16) {
        t_rets[t_nrets++] = {id, *ret, r->esp};
        *ret = (DWORD)(uintptr_t)kExit[id];
    }
    SetLastError(err);
}
extern "C" void __cdecl OnExit(int id, Regs* r) {
    DWORD err = GetLastError();
    // r->esp = esp after `push 0` = entry esp + 4 (ret) + stack args - 4; frames skipped by an unwind sit above the match
    DWORD ret = 0;
    while (t_nrets > 0) {
        const RetRec q = t_rets[--t_nrets];
        if (q.id == id && r->esp >= q.esp && r->esp - q.esp <= 64) { ret = q.ret; break; }
    }
    if (!ret) {  // cannot happen: every swap pushed a record
        Log("FATAL exit thunk %d without a record", id);
        TerminateProcess(GetCurrentProcess(), 0xED1);
    }
    *(DWORD*)((char*)r + sizeof(Regs)) = ret;
    if (g_hits[id] <= 16) Log("exit  %s eax=%08X", kSites[id].name, r->eax);
    if (id == S_APPSHUT) Counters("App_Shutdown exit");
    SetLastError(err);
}

#define SAVE __asm pushad __asm pushfd __asm push esp
#define RESTORE __asm add esp, 8 __asm popfd __asm popad
extern "C" __declspec(naked) void Exit0() { __asm { push 0 } SAVE __asm { push 0 } __asm { call OnExit } RESTORE __asm { ret } }
extern "C" __declspec(naked) void Exit1() { __asm { push 0 } SAVE __asm { push 1 } __asm { call OnExit } RESTORE __asm { ret } }
extern "C" __declspec(naked) void Exit2() { __asm { push 0 } SAVE __asm { push 2 } __asm { call OnExit } RESTORE __asm { ret } }

__declspec(naked) static void StAppShut() {
    SAVE __asm { push 0 } __asm { call OnEnter } RESTORE
    __asm { push esi } __asm { push edi } __asm { mov edi, ecx } __asm { lea ecx, [edi + 0x10] }
    __asm { jmp dword ptr [g_res + 0 * 4] }
}
__declspec(naked) static void StEca300() {
    SAVE __asm { push 1 } __asm { call OnEnter } RESTORE
    __asm { push esi } __asm { mov esi, [ecx] } __asm { test esi, esi }  // the je at 0xECA305 reads these flags
    __asm { jmp dword ptr [g_res + 1 * 4] }
}
__declspec(naked) static void St58b310() {
    SAVE __asm { push 2 } __asm { call OnEnter } RESTORE
    __asm { push esi } __asm { push edi } __asm { mov edi, ecx } __asm { cmp dword ptr [edi], 0 }
    __asm { jmp dword ptr [g_res + 2 * 4] }
}
// call sites: push the original return address and jmp, so the callee's stack looks exactly as before
__declspec(naked) static void StMgr() {
    SAVE __asm { push 3 } __asm { call OnSite } RESTORE
    __asm { push dword ptr [g_res + 3 * 4] } __asm { jmp dword ptr [g_tgt59F060] }
}
__declspec(naked) static void StSvc() {
    __asm { mov edx, [eax + 0x18] }
    SAVE __asm { push 4 } __asm { call OnSite } RESTORE
    __asm { push dword ptr [g_res + 4 * 4] } __asm { jmp edx }
}
__declspec(naked) static void StHooks() {
    SAVE __asm { push 5 } __asm { call OnSite } RESTORE
    __asm { push ebx } __asm { push esi } __asm { mov esi, ecx } __asm { push edi }
    __asm { jmp dword ptr [g_res + 5 * 4] }
}
__declspec(naked) static void StWin1() {
    __asm { push eax } __asm { mov ecx, ebx }
    SAVE __asm { push 6 } __asm { call OnSite } RESTORE
    __asm { push dword ptr [g_res + 6 * 4] } __asm { jmp edx }
}
__declspec(naked) static void StWin2() {
    __asm { push ecx } __asm { mov ecx, ebx }
    SAVE __asm { push 7 } __asm { call OnSite } RESTORE
    __asm { push dword ptr [g_res + 7 * 4] } __asm { jmp edx }
}
static void (*const kStubs[S_COUNT])() = {StAppShut, StEca300, St58b310, StMgr, StSvc, StHooks, StWin1, StWin2};

static void HexBytes(const uint8_t* p, int n, char* out) {
    for (int i = 0; i < n; i++) sprintf_s(out + i * 3, 4, "%02X ", p[i]);
}
static void InstallHooks() {
    for (int i = 0; i < S_COUNT; i++) {
        const SiteDef& s = kSites[i];
        g_res[i] = s.addr + s.len;
        uint8_t* p = (uint8_t*)(uintptr_t)s.addr;
        uint8_t cur[8] = {};
        char hx[32] = "";
        if (!Rd(s.addr, cur, s.len, NULL)) { Log("SKIP %s: %08X unreadable", s.name, s.addr); continue; }
        HexBytes(cur, s.len, hx);
        if (!SiteBytesMatch(cur, s)) { Log("SKIP %s: bytes differ at %08X: %s(already patched by someone else?)", s.name, s.addr, hx); continue; }
        uint8_t patch[8];
        memset(patch, 0x90, sizeof(patch));
        patch[0] = 0xE9;
        const int32_t rel = (int32_t)((uintptr_t)kStubs[i] - (s.addr + 5));
        memcpy(patch + 1, &rel, 4);
        DWORD old;
        if (!VirtualProtect(p, s.len, PAGE_EXECUTE_READWRITE, &old)) { Log("SKIP %s: VirtualProtect failed %lu", s.name, GetLastError()); continue; }
        memcpy(p, patch, s.len);
        VirtualProtect(p, s.len, old, &old);
        FlushInstructionCache(GetCurrentProcess(), p, s.len);
        memcpy(g_written[i], patch, s.len);
        g_hooked[i] = true;
        Log("HOOK %s: %08X checked %s", s.name, s.addr, hx);
    }
}
static void RemoveHooks() {  // FreeLibrary only: the stubs go away with the module
    for (int i = 0; i < S_COUNT; i++) {
        if (!g_hooked[i] || memcmp((void*)(uintptr_t)kSites[i].addr, g_written[i], kSites[i].len) != 0) continue;
        DWORD old;
        uint8_t* p = (uint8_t*)(uintptr_t)kSites[i].addr;
        if (VirtualProtect(p, kSites[i].len, PAGE_EXECUTE_READWRITE, &old)) {
            memcpy(p, kSites[i].bytes, kSites[i].len);
            VirtualProtect(p, kSites[i].len, old, &old);
        }
    }
}

// ---- (6) inventory, on a background thread (low CPU and IO priority, capped) ----
static HANDLE g_inv = INVALID_HANDLE_VALUE;
static BCRYPT_ALG_HANDLE g_sha;
static ULONGLONG g_hashBytes, g_invStart;
static int g_invLines;
static const ULONGLONG kHashTotal = 768ull << 20, kHashFile = 128ull << 20, kInvMs = 60000;
static void Inv(const char* fmt, ...) {
    if (g_inv == INVALID_HANDLE_VALUE || ++g_invLines > 30000) return;
    char buf[1200];
    va_list a;
    va_start(a, fmt);
    int n = _vsnprintf_s(buf, sizeof(buf) - 2, _TRUNCATE, fmt, a);
    va_end(a);
    if (n < 0) n = (int)strlen(buf);
    buf[n++] = '\r';
    buf[n++] = '\n';
    DWORD w;
    WriteFile(g_inv, buf, n, &w, NULL);
}
static bool Sha256(const wchar_t* path, ULONGLONG size, char* hex) {
    strcpy_s(hex, 65, size > kHashFile ? "(too big, not hashed)" : "(hash budget used up)");
    if (size > kHashFile || g_hashBytes + size > kHashTotal || GetTickCount64() - g_invStart > kInvMs || !g_sha) return false;
    HANDLE f = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, NULL, OPEN_EXISTING,
                           FILE_FLAG_SEQUENTIAL_SCAN, NULL);
    if (f == INVALID_HANDLE_VALUE) { strcpy_s(hex, 65, "(cannot open)"); return false; }
    BCRYPT_HASH_HANDLE h = NULL;
    static uint8_t buf[1 << 20];
    DWORD got;
    bool ok = BCryptCreateHash(g_sha, &h, NULL, 0, NULL, 0, 0) == 0;
    while (ok && ReadFile(f, buf, sizeof(buf), &got, NULL) && got) {
        ok = BCryptHashData(h, buf, got, 0) == 0;
        g_hashBytes += got;
    }
    uint8_t d[32];
    ok = ok && BCryptFinishHash(h, d, 32, 0) == 0;
    if (h) BCryptDestroyHash(h);
    CloseHandle(f);
    if (ok)
        for (int i = 0; i < 32; i++) sprintf_s(hex + i * 2, 3, "%02x", d[i]);
    else strcpy_s(hex, 65, "(read error)");
    return ok;
}
// mode 0 = hash every file (Game\Bin), 1 = Mods: hash small non-.package files, names + sizes only under Packages
static void ListDir(const wchar_t* root, const wchar_t* rel, int depth, int mode) {
    wchar_t pat[MAX_PATH * 2];
    swprintf(pat, MAX_PATH * 2, L"%ls%ls%ls\\*", root, *rel ? L"\\" : L"", rel);
    WIN32_FIND_DATAW fd;
    HANDLE h = FindFirstFileExW(pat, FindExInfoBasic, &fd, FindExSearchNameMatch, NULL, FIND_FIRST_EX_LARGE_FETCH);
    if (h == INVALID_HANDLE_VALUE) return;
    do {
        if (!wcscmp(fd.cFileName, L".") || !wcscmp(fd.cFileName, L"..")) continue;
        wchar_t r[MAX_PATH * 2], full[MAX_PATH * 2];
        swprintf(r, MAX_PATH * 2, L"%ls%ls%ls", rel, *rel ? L"\\" : L"", fd.cFileName);
        swprintf(full, MAX_PATH * 2, L"%ls\\%ls", root, r);
        char ra[MAX_PATH * 3];
        MaskA(r, ra, sizeof(ra));
        if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) {
            Inv("%-12s %-64s %s\\", "<dir>", "", ra);
            if (depth < 6 && !(fd.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT)) ListDir(root, r, depth + 1, mode);
            continue;
        }
        const ULONGLONG size = ((ULONGLONG)fd.nFileSizeHigh << 32) | fd.nFileSizeLow;
        const wchar_t* ext = wcsrchr(fd.cFileName, L'.');
        const bool noHash = mode == 1 && (_wcsnicmp(r, L"Packages\\", 9) == 0 || (ext && !_wcsicmp(ext, L".package")) || size > (8u << 20));
        char hex[65] = "-";
        if (!noHash) Sha256(full, size, hex);
        Inv("%12llu %-64s %s", size, hex, ra);
    } while (FindNextFileW(h, &fd));
    FindClose(h);
}
static void CleanOld() {  // ExitDiag files older than 14 days
    wchar_t pat[MAX_PATH];
    swprintf(pat, MAX_PATH, L"%ls\\*", g_diag);
    WIN32_FIND_DATAW fd;
    HANDLE h = FindFirstFileW(pat, &fd);
    if (h == INVALID_HANDLE_VALUE) return;
    FILETIME now;
    GetSystemTimeAsFileTime(&now);
    const ULONGLONG cut = (((ULONGLONG)now.dwHighDateTime << 32) | now.dwLowDateTime) - 14ull * 24 * 3600 * 10000000;
    do {
        const ULONGLONG t = ((ULONGLONG)fd.ftLastWriteTime.dwHighDateTime << 32) | fd.ftLastWriteTime.dwLowDateTime;
        if (!(fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) && t < cut) {
            wchar_t f[MAX_PATH];
            swprintf(f, MAX_PATH, L"%ls\\%ls", g_diag, fd.cFileName);
            DeleteFileW(f);
        }
    } while (FindNextFileW(h, &fd));
    FindClose(h);
}
static ModRange g_imods[512];
static DWORD WINAPI InvThread(LPVOID) {
    SetThreadPriority(GetCurrentThread(), THREAD_MODE_BACKGROUND_BEGIN);
    CleanOld();
    // collect-list.txt: full paths for collect.cmd, never zipped
    wchar_t list[2048], lp[MAX_PATH];
    BuildCollectList(g_bin, g_user, list, 2048);
    swprintf(lp, MAX_PATH, L"%ls\\collect-list.txt", g_diag);
    HANDLE f = CreateFileW(lp, GENERIC_WRITE, FILE_SHARE_READ, NULL, CREATE_ALWAYS, 0, NULL);
    if (f != INVALID_HANDLE_VALUE) {
        char u[6000];
        Utf8(list, u, sizeof(u));
        DWORD w;
        WriteFile(f, u, (DWORD)strlen(u), &w, NULL);
        CloseHandle(f);
    }
    // modules already loaded before our notification registration
    const int nm = Snap(g_imods, 512);
    Log("modules at start (%d):", nm);
    HMODULE hm[512];
    DWORD need = 0;
    K32EnumProcessModules(GetCurrentProcess(), hm, sizeof(hm), &need);
    for (int i = 0; i < nm && i < (int)(need / sizeof(HMODULE)); i++) {
        wchar_t p[MAX_PATH];
        char m[MAX_PATH * 3];
        if (!GetModuleFileNameW(hm[i], p, MAX_PATH)) p[0] = 0;
        MaskA(p, m, sizeof(m));
        Log("  %08X-%08X %s", g_imods[i].base, g_imods[i].base + g_imods[i].size, m);
    }
    wchar_t ip[MAX_PATH];
    swprintf(ip, MAX_PATH, L"%ls\\inventory-%ls.txt", g_diag, g_stamp);
    g_inv = CreateFileW(ip, GENERIC_WRITE, FILE_SHARE_READ, NULL, CREATE_ALWAYS, 0, NULL);
    g_invStart = GetTickCount64();
    if (BCryptOpenAlgorithmProvider(&g_sha, BCRYPT_SHA256_ALGORITHM, NULL, 0) != 0) g_sha = NULL;
    Inv("exitdiag %s inventory; size, sha256, path. Game\\Bin (relative):", EXITDIAG_VERSION);
    ListDir(g_bin, L"", 0, 0);
    wchar_t mods[MAX_PATH];
    swprintf(mods, MAX_PATH, L"%ls\\Mods", g_user);
    Inv("Documents\\Electronic Arts\\The Sims 3\\Mods (relative; Packages: names and sizes only):");
    ListDir(mods, L"", 0, 1);
    Inv("done: %llu MB hashed in %llu ms", g_hashBytes >> 20, GetTickCount64() - g_invStart);
    if (g_sha) BCryptCloseAlgorithmProvider(g_sha, 0);
    CloseHandle(g_inv);
    g_inv = INVALID_HANDLE_VALUE;
    Log("inventory written (%llu MB hashed, %llu ms)", g_hashBytes >> 20, GetTickCount64() - g_invStart);
    SetThreadPriority(GetCurrentThread(), THREAD_MODE_BACKGROUND_END);
    return 0;
}

// ---- start ----
static void Paths() {
    // <Documents> from the registry, as ts3native does (loader-lock safe in DllMain; OneDrive-moved folders included)
    wchar_t docs[MAX_PATH] = L"";
    DWORD sz = sizeof(docs);
    if (RegGetValueW(HKEY_CURRENT_USER, L"Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\User Shell Folders",
                     L"Personal", RRF_RT_REG_SZ | RRF_RT_REG_EXPAND_SZ, NULL, docs, &sz) != ERROR_SUCCESS) {
        wchar_t prof[MAX_PATH];
        const DWORD n = GetEnvironmentVariableW(L"USERPROFILE", prof, MAX_PATH);
        if (n && n < MAX_PATH) swprintf(docs, MAX_PATH, L"%ls\\Documents", prof);
    }
    if (docs[0]) swprintf(g_user, MAX_PATH, L"%ls\\Electronic Arts\\The Sims 3", docs);
    if (g_user[0]) swprintf(g_diag, MAX_PATH, L"%ls\\ExitDiag", g_user);
    const DWORD n = GetEnvironmentVariableW(L"USERPROFILE", g_profile, MAX_PATH);
    if (!n || n >= MAX_PATH) g_profile[0] = 0;
    const DWORD un = GetEnvironmentVariableW(L"USERNAME", g_uname, 128);
    if (!un || un >= 128) g_uname[0] = 0;
    if (GetModuleFileNameW(g_self, g_bin, MAX_PATH)) {
        if (wchar_t* c = wcsrchr(g_bin, L'\\')) *c = 0;  // Game\Bin
        wcscpy_s(g_game, g_bin);
        for (int i = 0; i < 2; i++)
            if (wchar_t* c = wcsrchr(g_game, L'\\')) *c = 0;  // install root
    }
}
static void Start() {
    InitializeCriticalSection(&g_cs);
    Paths();
    SYSTEMTIME t;
    GetLocalTime(&t);
    swprintf(g_stamp, 64, L"%04u%02u%02u-%02u%02u%02u-%lu", t.wYear, t.wMonth, t.wDay, t.wHour, t.wMinute, t.wSecond, GetCurrentProcessId());
    if (g_diag[0]) {
        CreateDirectoryW(g_user, NULL);
        CreateDirectoryW(g_diag, NULL);
        wchar_t lp[MAX_PATH];
        swprintf(lp, MAX_PATH, L"%ls\\exitdiag-%ls.log", g_diag, g_stamp);
        g_log = CreateFileW(lp, GENERIC_WRITE, FILE_SHARE_READ, NULL, CREATE_ALWAYS, 0, NULL);
    }
    if (g_log == INVALID_HANDLE_VALUE) return;  // nowhere to write: install nothing
    const uint8_t* exe = (const uint8_t*)GetModuleHandleW(NULL);
    const IMAGE_NT_HEADERS* nt = (const IMAGE_NT_HEADERS*)(exe + ((const IMAGE_DOS_HEADER*)exe)->e_lfanew);
    g_exeOk = (uintptr_t)exe == 0x400000 && nt->FileHeader.TimeDateStamp == kExeStamp && nt->OptionalHeader.SizeOfImage == kExeImageSize;
    wchar_t exeName[MAX_PATH];
    char en[MAX_PATH * 3];
    GetModuleFileNameW(NULL, exeName, MAX_PATH);
    MaskA(exeName, en, sizeof(en));
    Log("exitdiag %s; exe %s stamp %08X image %X -> %s; log in Documents\\Electronic Arts\\The Sims 3\\ExitDiag", EXITDIAG_VERSION,
        en, nt->FileHeader.TimeDateStamp, nt->OptionalHeader.SizeOfImage, g_exeOk ? "Steam 1.67, hooks on" : "not Steam 1.67: no hooks, logs only");
    if (LdrRegFn reg = (LdrRegFn)GetProcAddress(GetModuleHandleW(L"ntdll.dll"), "LdrRegisterDllNotification")) {
        const LONG st = reg(0, DllNote, NULL, &g_noteCookie);
        Log("LdrRegisterDllNotification: %08lX", st);
    }
    g_veh = AddVectoredExceptionHandler(1, Veh);
    SetUef();
    if (g_exeOk) InstallHooks();
    ModsFresh();
    if (HANDLE th = CreateThread(NULL, 0, InvThread, NULL, 0, NULL)) CloseHandle(th);
}

BOOL WINAPI DllMain(HINSTANCE inst, DWORD reason, LPVOID reserved) {
    if (reason == DLL_PROCESS_ATTACH) {
        g_self = inst;
        DisableThreadLibraryCalls(inst);
        Start();
    } else if (reason == DLL_PROCESS_DETACH && g_log != INVALID_HANDLE_VALUE) {
        if (reserved) g_exiting = 1;  // process exit: no locks
        CheckPatches("detach");
        Counters(reserved ? "process exit" : "unload");
        Log("detach (%s)", reserved ? "process exit" : "FreeLibrary");
        if (g_noteCookie)
            if (LdrUnregFn un = (LdrUnregFn)GetProcAddress(GetModuleHandleW(L"ntdll.dll"), "LdrUnregisterDllNotification")) un(g_noteCookie);
        if (!reserved) {
            RemoveHooks();
            if (g_veh) RemoveVectoredExceptionHandler(g_veh);
            if (g_prevUef) SetUnhandledExceptionFilter(g_prevUef);
        }
        CloseHandle(g_log);
        g_log = INVALID_HANDLE_VALUE;
    }
    return TRUE;
}
