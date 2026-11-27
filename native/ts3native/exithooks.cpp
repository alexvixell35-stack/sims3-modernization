// [Fixes] ExitStaleHooks / ExitHookDiag (docs/67 §47, docs/51 §628): EA bug fix + detector for the exit crash in UI/TriggerService's
// teardown (player report 2026-11-27: wild call 0xA8EAB8A2). App_Shutdown 0xEC9B40 -> 0x589410 -> 0x59F060 -> TriggerService
// (vtable 0x101E5D8, singleton [0x11D8C44]) slot +0x18 = 0x7F10B0, which removes every hook left through 0x7F0EF0 (call 0x7F10EF).
// 0x7F0EF0 (thiscall, handle; also EA's RemoveTriggerHook IC 0x779790 via call 0x7797A0) finds the node with 0x7FF6B0 and, when
// the node's flag byte +0x1C has 0x10 (active), calls slot +0x10C (remove winproc) of the node's window +0x18 with the service's
// handlers +0x3D0 / +0x3D4 [verified disassembly TS3W 1.67]. A hook whose window was destroyed without RemoveTriggerHook leaves a
// dangling window: in the player's dump it was freed and reused, so the slot read a heap value [verified: dump].
// Map (service +0x10): buckets +0x14, bucket count +0x18 (bucket = handle % count, the end marker sits at buckets[count]),
// elements +0x1C; node: handle +0, window +0x18, flags +0x1C, chain next +0x24 (find 0x7FF6B0) [verified]. Hooks are added only by
// the AddTriggerHook IC 0x77CC00 (the one caller of add 0x7F0E50) [verified: xref], so its map name names the owner script.
// ExitHookDiag=1 (detector, log only): the IC slot 0x1161FF8 records handle -> map name (dropped on remove); slot +0x18 of the
// service vtable (0x101E5F0) logs every live hook before EA's teardown runs (handle, map, flags, window, its vtable, ok or why
// not); a remove of an active hook whose window fails the check logs one line ("exit hooks: stale"; first 20, then every 100th).
// ExitStaleHooks=1 (fix, needs nothing else): both calls of 0x7F0EF0 go through XhRemove; an active hook whose window is not
// readable, whose vtable is in no loaded module, or whose slot +0x10C is in no loaded module gets its active bit cleared under
// the service lock (+0x380), so EA's own remove still unhooks the input map and erases the node but makes no call into the window.
// A window freed but not yet reused still passes (vanilla behaviour). Not covered: the moment a window dies (no generic UTFWin
// destructor mapped); the next hit's lines name the map and the window instead.
// Steam 1.67 only (exact bytes at each site; another owner or exe = SKIP). EA 1.69 / retail builds: not installed.
#ifndef TS3A_TABLE
static const DWORD XH_SHUT_SLOT = 0x0101E5F0, XH_SHUT = 0x007F10B0, XH_REMOVE = 0x007F0EF0,
                   XH_CALL_IC = 0x007797A0, XH_CALL_SHUT = 0x007F10EF, XH_ADD_SLOT = 0x01161FF8, XH_ADD = 0x0077CC00;
static const u8 kXhCallIcPre[7] = {0x8B, 0x4C, 0x24, 0x04, 0x51, 0x8B, 0xC8};  // mov ecx,[esp+4]; push ecx; mov ecx,eax
static const u8 kXhCallShutPre[7] = {0x8B, 0x00, 0x8B, 0x00, 0x50, 0x8B, 0xCE};  // mov eax,[eax]; mov eax,[eax]; push eax; mov ecx,esi
static const u8 kXhRemovePro[9] = {0x83, 0xEC, 0x08, 0x53, 0x55, 0x56, 0x8B, 0xF1, 0x57};
static const u8 kXhShutPro[5] = {0x53, 0x56, 0x8B, 0xF1, 0x57};
static const u8 kXhAddPro[3] = {0x53, 0x57, 0xE8};

typedef bool(__fastcall* XhRemoveFn)(u8* svc, void* edx, DWORD handle);
typedef void(__fastcall* XhShutFn)(u8* svc, void* edx);
typedef DWORD(__cdecl* XhAddFn)(DWORD win, const u8* map, DWORD mode, DWORD prio, DWORD modal);
static XhRemoveFn g_xhRemove = (XhRemoveFn)(uintptr_t)XH_REMOVE;  // selftest swaps these
static XhShutFn g_xhShut = (XhShutFn)(uintptr_t)XH_SHUT;
static XhAddFn g_xhAdd = (XhAddFn)(uintptr_t)XH_ADD;
static bool g_xhFix, g_xhDiag;
static volatile LONG g_xhRemoves, g_xhStale, g_xhSkipped, g_xhNamed, g_xhDumps;

// handle -> map name, fixed table (hooks are few: tens); a full table drops the new name, never an old one
struct XhName { DWORD handle; char name[40]; };
static XhName g_xhNames[256];
static CRITICAL_SECTION g_xhNameCs;
static volatile LONG g_xhNameInit;
static void XhNameLock() {
    if (InterlockedCompareExchange(&g_xhNameInit, 1, 0) == 0) InitializeCriticalSection(&g_xhNameCs), g_xhNameInit = 2;
    while (g_xhNameInit != 2) Sleep(0);
    EnterCriticalSection(&g_xhNameCs);
}
static void XhNameSet(DWORD h, const char* name) {
    if (!h) return;
    XhNameLock();
    XhName* free = NULL;
    for (XhName& e : g_xhNames) { if (e.handle == h) { free = &e; break; } if (!e.handle && !free) free = &e; }
    if (free) free->handle = h, strncpy_s(free->name, name, _TRUNCATE), InterlockedIncrement(&g_xhNamed);
    LeaveCriticalSection(&g_xhNameCs);
}
static void XhNameGet(DWORD h, char* out, size_t n) {
    XhNameLock();
    strcpy_s(out, n, "?");
    for (const XhName& e : g_xhNames) if (e.handle == h) { strcpy_s(out, n, e.name); break; }
    LeaveCriticalSection(&g_xhNameCs);
}
static void XhNameDrop(DWORD h) {
    XhNameLock();
    for (XhName& e : g_xhNames) if (e.handle == h) e.handle = 0;
    LeaveCriticalSection(&g_xhNameCs);
}

// EA's MonoString {vtable, int length at +4, UTF-16 at +8} (autshadow.cpp) -> ASCII, '?' for anything else
static void XhMonoAscii(const u8* s, char* out, size_t n) {
    out[0] = 0;
    __try {
        if (!s || !Readable(s, 8)) return;
        const int len = *(const int*)(s + 4);
        size_t k = 0;
        for (int i = 0; i < len && k + 1 < n && Readable(s + 8 + 2 * i, 2); i++) {
            const wchar_t c = *(const wchar_t*)(s + 8 + 2 * i);
            out[k++] = c >= 0x20 && c < 0x7F ? (char)c : '?';
        }
        out[k] = 0;
    } __except (EXCEPTION_EXECUTE_HANDLER) { out[0] = 0; }
}

static HMODULE XhModuleOf(DWORD a) {
    HMODULE m = NULL;
    return GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, (LPCWSTR)(uintptr_t)a, &m) ? m : NULL;
}
// NULL = the window looks alive (readable, vtable and slot +0x10C inside loaded modules), else why not
static const char* XhWindowBad(const u8* w, DWORD* vtOut) {
    *vtOut = 0;
    __try {
        if (!w || !Readable(w, 4)) return "window unreadable";
        const DWORD vt = *(const DWORD*)w;
        *vtOut = vt;
        if (!XhModuleOf(vt)) return "vtable in no module (freed window)";
        if (!Readable((const void*)(uintptr_t)(vt + 0x10C), 4)) return "slot +0x10C unreadable";
        if (!XhModuleOf(*(const DWORD*)(uintptr_t)(vt + 0x10C))) return "slot +0x10C in no module";
        return NULL;
    } __except (EXCEPTION_EXECUTE_HANDLER) { return "fault while checking"; }
}

// the node of handle h (0x7FF6B0's walk), NULL if none or unreadable
static u8* XhFind(u8* svc, DWORD h) {
    __try {
        u8** const b = *(u8***)(svc + 0x14);
        const DWORD nb = *(const DWORD*)(svc + 0x18);
        if (!b || !nb) return NULL;
        int guard = 0;
        for (u8* n = b[h % nb]; n && guard < 4096; n = *(u8**)(n + 0x24), guard++)
            if (*(const DWORD*)n == h) return n;
    } __except (EXCEPTION_EXECUTE_HANDLER) {}
    return NULL;
}

static bool __fastcall XhRemove(u8* svc, void* edx, DWORD h) {
    InterlockedIncrement(&g_xhRemoves);
    CRITICAL_SECTION* const cs = (CRITICAL_SECTION*)(svc + 0x380);  // the service's own lock (0x7F0EF0 takes it too; recursive)
    EnterCriticalSection(cs);
    u8* const node = XhFind(svc, h);
    DWORD vt = 0;
    const u8* w = NULL;
    const char* why = NULL;
    __try {
        if (node && (node[0x1C] & 0x10)) w = *(u8* const*)(node + 0x18), why = XhWindowBad(w, &vt);
        if (why && g_xhFix) node[0x1C] &= (u8)~0x10, InterlockedIncrement(&g_xhSkipped);
    } __except (EXCEPTION_EXECUTE_HANDLER) { why = NULL; }
    LeaveCriticalSection(cs);
    if (why) {
        const LONG n = InterlockedIncrement(&g_xhStale);
        if (g_xhDiag && (n <= 20 || n % 100 == 0)) {
            char name[40];
            XhNameGet(h, name, sizeof(name));
            Log("exit hooks: stale hook %lu map '%s' window 0x%08lX vtable 0x%08lX: %s; %s (from 0x%08lX); stale %ld of %ld removes",
                h, name, (DWORD)(uintptr_t)w, vt, why, g_xhFix ? "window calls skipped, EA erases the node" : "passed to EA (ExitStaleHooks=0)",
                (DWORD)(uintptr_t)_ReturnAddress(), n, g_xhRemoves);
        }
    }
    XhNameDrop(h);
    return g_xhRemove(svc, edx, h);
}

// one line per live hook before EA's teardown removes them, then a summary
static void XhDump(u8* svc) {
    LONG total = 0, active = 0, bad = 0;
    CRITICAL_SECTION* const cs = (CRITICAL_SECTION*)(svc + 0x380);
    EnterCriticalSection(cs);
    __try {
        u8** const b = *(u8***)(svc + 0x14);
        const DWORD nb = *(const DWORD*)(svc + 0x18);
        for (DWORD i = 0; b && i < nb && i < 65536; i++) {
            int guard = 0;
            for (u8* n = b[i]; n && guard < 4096; n = *(u8**)(n + 0x24), guard++) {
                total++;
                const DWORD h = *(const DWORD*)n, w3 = *(const DWORD*)(n + 0xC);
                const u8 fl = n[0x1C];
                const u8* w = *(u8* const*)(n + 0x18);
                DWORD vt = 0;
                const char* why = XhWindowBad(w, &vt);
                if (fl & 0x10) active++;
                if (why) bad++;
                if (total <= 40) {
                    char name[40];
                    XhNameGet(h, name, sizeof(name));
                    Log("exit hooks: live hook %lu map '%s' w3 0x%08lX flags 0x%02X window 0x%08lX vtable 0x%08lX: %s", h, name, w3, fl,
                        (DWORD)(uintptr_t)w, vt, why ? why : "ok");
                }
            }
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) { Log("exit hooks: fault while listing the hooks"); }
    LeaveCriticalSection(cs);
    InterlockedIncrement(&g_xhDumps);
    Log("exit hooks: TriggerService teardown with %ld hooks (%ld active, %ld with a bad window); names recorded %ld; %s", total, active, bad,
        g_xhNamed, g_xhFix ? "ExitStaleHooks on" : "ExitStaleHooks off");
}

static void __fastcall XhShutdown(u8* svc, void* edx) {
    if (g_xhDiag) XhDump(svc);
    g_xhShut(svc, edx);
}

static DWORD __cdecl XhAdd(DWORD win, const u8* map, DWORD mode, DWORD prio, DWORD modal) {
    const DWORD h = g_xhAdd(win, map, mode, prio, modal);
    if (h) {
        char name[40];
        XhMonoAscii(map, name, sizeof(name));
        XhNameSet(h, name);
    }
    return h;
}

// site = a call instruction to EA's remove; pre = the bytes before it
static bool XhRetarget(u8* site, const u8* pre, size_t n) {
    if (!Matches(site - n, pre, n) || !Readable(site, 5) || site[0] != 0xE8 || (DWORD)(uintptr_t)(site + 5 + *(const LONG*)(site + 1)) != XH_REMOVE)
        return false;
    const LONG rel = (LONG)((uintptr_t)&XhRemove - ((uintptr_t)site + 5));
    return Write(site + 1, &rel, 4);
}
static bool XhSwapSlot(DWORD slot, DWORD expect, const void* to) {
    if (!Readable((const void*)(uintptr_t)slot, 4) || *(const DWORD*)(uintptr_t)slot != expect) return false;
    const DWORD v = (DWORD)(uintptr_t)to;
    return Write((void*)(uintptr_t)slot, &v, 4);
}

static bool ApplyExitHooks(const wchar_t* ini) {
    g_xhFix = GetPrivateProfileIntW(L"Fixes", L"ExitStaleHooks", 0, ini) > 0;
    g_xhDiag = GetPrivateProfileIntW(L"Fixes", L"ExitHookDiag", 0, ini) > 0;
    if (!g_xhFix && !g_xhDiag) return false;
    if (!Matches((const void*)(uintptr_t)XH_REMOVE, kXhRemovePro, sizeof(kXhRemovePro))) {
        Log("SKIP exit hooks (docs/67 §47): TriggerService remove 0x%08lX differs (another owner or exe): not installed", XH_REMOVE);
        return false;
    }
    int calls = 0;  // DllMain: no game thread runs yet
    calls += XhRetarget((u8*)(uintptr_t)XH_CALL_IC, kXhCallIcPre, sizeof(kXhCallIcPre));
    calls += XhRetarget((u8*)(uintptr_t)XH_CALL_SHUT, kXhCallShutPre, sizeof(kXhCallShutPre));
    bool shut = false, add = false;
    if (g_xhDiag) {
        shut = Matches((const void*)(uintptr_t)XH_SHUT, kXhShutPro, sizeof(kXhShutPro)) && XhSwapSlot(XH_SHUT_SLOT, XH_SHUT, (const void*)&XhShutdown);
        add = Matches((const void*)(uintptr_t)XH_ADD, kXhAddPro, sizeof(kXhAddPro)) && XhSwapSlot(XH_ADD_SLOT, XH_ADD, (const void*)&XhAdd);
    }
    Log("%s exit hooks (docs/67 §47): TriggerService remove calls %d of 2 checked (0x%08lX IC, 0x%08lX teardown)%s; diag %s (teardown list %s, map names %s)",
        calls == 2 && (!g_xhDiag || (shut && add)) ? "OK  " : "PART", calls, XH_CALL_IC, XH_CALL_SHUT,
        g_xhFix ? ", stale windows skipped (ExitStaleHooks=1)" : ", log only (ExitStaleHooks=0)", g_xhDiag ? "on" : "off",
        shut ? "on" : g_xhDiag ? "SKIP" : "off", add ? "on" : g_xhDiag ? "SKIP" : "off");
    return calls > 0 || shut || add;
}
#else
static bool ApplyExitHooks(const wchar_t* ini) {
    if (GetPrivateProfileIntW(L"Fixes", L"ExitStaleHooks", 0, ini) <= 0 && GetPrivateProfileIntW(L"Fixes", L"ExitHookDiag", 0, ini) <= 0) return false;
    Log("SKIP exit hooks (docs/67 §47): Steam 1.67 addresses only, not this build");
    return false;
}
#endif
