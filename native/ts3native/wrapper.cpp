// [Wrapper] (docs/530 step 0, spike): the game's window becomes the child of a host window that another process owns
// (native/ts3wrap, x64). Nothing else changes: no restart, no session logic.
// EA's window class (Steam 1.67) [verified, static]: ctor 0x411500 leaves the parent field (+0x74) zero and nothing ever
// writes it; create 0x4120C0 calls CreateWindowExW once, at 0x4121CF, with the style of +0x80 and CW_USEDEFAULT; the style
// apply 0x411770 and the fullscreen path 0x4116C0 write GWL_STYLE with SetWindowLongA, keeping WS_CHILD (mask 0x7F30FFFF);
// the sizing 0x410AC0 is AdjustWindowRectEx + MoveWindow; 0x411E38 warps the cursor only when GetForegroundWindow is the
// game window. All of these go through TS3W.exe's import slots, so the spike is import-slot hooks filtered on the game window:
//   CreateWindowExW (the 0x4121CF call only): starts ts3wrap.exe next to the asi, waits up to 5 s for its window, then
//     creates the game window as its child. No host window: one FAIL line and the game runs unwrapped.
//   SetWindowLongA: frame styles never reach the child. AdjustWindowRectEx: identity (the child has no frame).
//   MoveWindow: the size goes to the host (message "ts3wrap-fit"), which answers with the child's size ("ts3wrap-size").
//   GetForegroundWindow: host in front = game in front.
//   The game window is also subclassed (WrProc): other modules size and style it through user32 directly, past the import
//   slots (wsp2 run: one made it WS_POPUP at the monitor size inside a smaller framed host). Any size asked of it goes to
//   the host, and only the host's answer resizes it; frame styles are stripped on every style write.
//   IsIconic / IsZoomed / ShowWindow(minimize, maximize, restore): the host's state.
// Windowed mode only: exclusive fullscreen is not handled. No threads.
// Step 1 (docs/530 §12, restart.cpp): with Channel=1 the host gets the session's channel; a game the host started again
// (TS3WRAP_CHANNEL) never starts a host of its own but becomes the child of the channel's host window; WrProc writes the
// heartbeat and counts the RestartHotkey (Ctrl+Shift+F12).
#pragma comment(lib, "user32.lib")

static HWND g_wrHost, g_wrGame;
static HMODULE g_wrSelf;
static bool g_wrBack, g_wrFromHost;
static WNDPROC g_wrOldProc;
static UINT g_wrMsgFit, g_wrMsgSize;
static int g_wrAsked;

static const DWORD kWrFrame = WS_POPUP | WS_CAPTION | WS_THICKFRAME | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX;  // 0x80CF0000, EA's own mask at 0x4117A4
static DWORD WrChildStyle(DWORD s) { return (s & ~kWrFrame) | WS_CHILD; }
static DWORD WrChildEx(DWORD s) { return s & ~(DWORD)(WS_EX_WINDOWEDGE | WS_EX_CLIENTEDGE); }  // 0x300, what 0x4122D0 sets with a sizing frame

static HWND(WINAPI* g_wrCreate)(DWORD, LPCWSTR, LPCWSTR, DWORD, int, int, int, int, HWND, HMENU, HINSTANCE, LPVOID);
static LONG(WINAPI* g_wrSetLong)(HWND, int, LONG);
static BOOL(WINAPI* g_wrMove)(HWND, int, int, int, int, BOOL);
static BOOL(WINAPI* g_wrAdjust)(RECT*, DWORD, BOOL, DWORD);
static HWND(WINAPI* g_wrForeground)();
static BOOL(WINAPI* g_wrIconic)(HWND);
static BOOL(WINAPI* g_wrZoomed)(HWND);
static BOOL(WINAPI* g_wrShow)(HWND, int);

static HWND WrStartHost() {
    wchar_t exe[MAX_PATH], cmd[MAX_PATH + 96], cls[32];
    const DWORD n = GetModuleFileNameW(g_wrSelf, exe, MAX_PATH);
    wchar_t* slash = n && n < MAX_PATH ? wcsrchr(exe, L'\\') : NULL;
    if (!slash || wcscpy_s(slash + 1, MAX_PATH - (slash + 1 - exe), L"ts3wrap.exe")) return NULL;
    const bool chan = WrChanCreate();  // restart.cpp: Channel=1 only
    swprintf_s(cmd, L"\"%s\" %lu%s%s%s", exe, GetCurrentProcessId(), chan ? L" " : L"", chan ? g_wrId : L"", g_wrBack ? L" back" : L"");
    STARTUPINFOW si = {sizeof(si)};
    PROCESS_INFORMATION pi;
    // the start folder captured at DllMain: the host relaunches the game from it
    if (!CreateProcessW(exe, cmd, NULL, NULL, FALSE, 0, NULL, g_wrCwd[0] ? g_wrCwd : NULL, &si, &pi)) {
        Log("FAIL wrapper: ts3wrap.exe did not start (err %lu)", GetLastError());  // file name only (docs/188 privacy)
        return NULL;
    }
    swprintf_s(cls, L"ts3wrap-%lu", GetCurrentProcessId());
    HWND h = NULL;
    for (int i = 0; i < 250 && !h && WaitForSingleObject(pi.hProcess, 20) == WAIT_TIMEOUT; i++) h = FindWindowW(cls, NULL);
    CloseHandle(pi.hThread);
    CloseHandle(pi.hProcess);
    return h;
}

static LRESULT CALLBACK WrProc(HWND h, UINT m, WPARAM w, LPARAM l) {
    if (g_wrChan) g_wrChan->beat = (int32_t)GetTickCount();  // the host's hang report: this thread pumps
    if (m == WM_KEYDOWN && w == VK_F12 && g_wrHotkeyOn && !(l & 0x40000000) && GetKeyState(VK_CONTROL) < 0 && GetKeyState(VK_SHIFT) < 0)
        InterlockedIncrement(&g_wrKey), Log("wrapper: hotkey Ctrl+Shift+F12");  // first press only, not the repeat
    if (m == g_wrMsgSize) {  // the host's client size: the only way the child changes size
        g_wrFromHost = true;
        SetWindowPos(h, NULL, 0, 0, (int)w, (int)l, SWP_NOZORDER | SWP_NOACTIVATE);
        g_wrFromHost = false;
        return 0;
    }
    if (m == WM_STYLECHANGING && l) {
        STYLESTRUCT* s = (STYLESTRUCT*)l;
        if ((int)w == GWL_STYLE) s->styleNew = WrChildStyle(s->styleNew);
        else if ((int)w == GWL_EXSTYLE) s->styleNew = WrChildEx(s->styleNew);
    } else if (m == WM_WINDOWPOSCHANGING && l && !g_wrFromHost) {
        WINDOWPOS* p = (WINDOWPOS*)l;
        RECT rc = {};
        GetClientRect(h, &rc);
        if (!(p->flags & SWP_NOSIZE) && p->cx > 0 && p->cy > 0 && (p->cx != rc.right || p->cy != rc.bottom)) {
            if (g_wrAsked < 8) g_wrAsked++, Log("wrapper: size %dx%d asked of the game window (it has %ldx%ld), passed to the host", p->cx, p->cy, rc.right, rc.bottom);
            PostMessageW(g_wrHost, g_wrMsgFit, (WPARAM)p->cx, (LPARAM)p->cy);
        }
        p->flags |= SWP_NOSIZE | SWP_NOMOVE;  // its place is the host's client origin
    }
    return CallWindowProcW(g_wrOldProc, h, m, w, l);
}

static HWND WINAPI WrCreateWindowExW(DWORD ex, LPCWSTR cls, LPCWSTR name, DWORD style, int x, int y, int w, int h, HWND parent,
                                     HMENU menu, HINSTANCE inst, LPVOID param) {
    if ((DWORD)(uintptr_t)_ReturnAddress() != 0x4121D5 || g_wrGame || parent)
        return g_wrCreate(ex, cls, name, style, x, y, w, h, parent, menu, inst, param);
    if (g_wrChanHost) {  // started by the host again: its window, never a second host
        g_wrHost = IsWindow(g_wrChanHost) ? g_wrChanHost : NULL;
        if (!g_wrHost) Log("FAIL wrapper: the channel's host window is gone, the game runs unwrapped");
    } else g_wrHost = WrStartHost();
    if (!g_wrHost) {
        if (!g_wrChanHost) Log("FAIL wrapper: no host window after 5 s, the game runs unwrapped");
        return g_wrCreate(ex, cls, name, style, x, y, w, h, parent, menu, inst, param);
    }
    RECT rc = {};
    GetClientRect(g_wrHost, &rc);
    const HWND game = g_wrCreate(WrChildEx(ex), cls, name, WrChildStyle(style), 0, 0, rc.right, rc.bottom, g_wrHost, menu, inst, param);
    if (!game) {  // a host window that went away meanwhile: the game gets its own top-level window, never none
        Log("FAIL wrapper: child CreateWindowExW (err %lu), the game runs unwrapped", GetLastError());
        g_wrHost = NULL;
        return g_wrCreate(ex, cls, name, style, x, y, w, h, parent, menu, inst, param);
    }
    Log("OK   wrapper: game window 0x%p is a child of host 0x%p (style 0x%08lX -> 0x%08lX, %ldx%ld)", game, g_wrHost, style,
             WrChildStyle(style), rc.right, rc.bottom);
    g_wrGame = game;
    if (game) g_wrOldProc = (WNDPROC)(LONG_PTR)SetWindowLongW(game, GWL_WNDPROC, (LONG)(LONG_PTR)WrProc);
    if (game && !g_wrOldProc) Log("FAIL wrapper: subclass (err %lu), sizes asked by other modules stay on the child", GetLastError());
    return game;
}

static LONG WINAPI WrSetWindowLongA(HWND h, int index, LONG v) {
    if (h && h == g_wrGame) {
        if (index == GWL_STYLE) v = (LONG)WrChildStyle((DWORD)v);
        else if (index == GWL_EXSTYLE) v = (LONG)WrChildEx((DWORD)v);
    }
    return g_wrSetLong(h, index, v);
}

static BOOL WINAPI WrAdjustWindowRectEx(RECT* r, DWORD style, BOOL menu, DWORD ex) {
    return g_wrGame ? TRUE : g_wrAdjust(r, style, menu, ex);
}

static BOOL WINAPI WrMoveWindow(HWND h, int x, int y, int w, int ht, BOOL repaint) {
    if (!h || h != g_wrGame) return g_wrMove(h, x, y, w, ht, repaint);
    // shortcut: the host keeps its own place (the game's x/y came from child coordinates); set a place when the wrapper owns the saved window position
    return PostMessageW(g_wrHost, g_wrMsgFit, (WPARAM)w, (LPARAM)ht);
}

static HWND WINAPI WrGetForegroundWindow() {
    const HWND fg = g_wrForeground();
    return fg && fg == g_wrHost ? g_wrGame : fg;
}

static BOOL WINAPI WrIsIconic(HWND h) { return g_wrIconic(h && h == g_wrGame ? g_wrHost : h); }
static BOOL WINAPI WrIsZoomed(HWND h) { return g_wrZoomed(h && h == g_wrGame ? g_wrHost : h); }

static BOOL WINAPI WrShowWindow(HWND h, int cmd) {
    const bool frame = cmd == SW_SHOWMINIMIZED || cmd == SW_MAXIMIZE || cmd == SW_MINIMIZE || cmd == SW_SHOWMINNOACTIVE || cmd == SW_RESTORE ||
                       cmd == SW_FORCEMINIMIZE;
    return h && h == g_wrGame && frame ? ShowWindowAsync(g_wrHost, cmd) : g_wrShow(h, cmd);
}

// One import slot: the old target is kept and chained, so another mod's hook on the same slot keeps working.
static bool WrHookSlot(void** slot, void* fn, void** orig) {
    if (!Readable(slot, sizeof(void*)) || !*slot) return false;
    *orig = *slot;
    return Write(slot, &fn, sizeof(fn));
}

static bool ApplyWrapper(const wchar_t* ini, HMODULE self) {
    if (!GetPrivateProfileIntW(L"Wrapper", L"Enabled", 0, ini)) return false;
    static const u8 kCall[] = {0xFF, 0x15, 0x64, 0x58, 0xF9, 0x00};  // 0x4121CF: call [CreateWindowExW slot]
    if (!Matches((void*)0x4121CF, kCall, sizeof(kCall))) { Log("SKIP wrapper: unknown bytes at 0x4121CF, nothing written"); return false; }
    g_wrSelf = self;
    g_wrMsgFit = RegisterWindowMessageW(L"ts3wrap-fit"), g_wrMsgSize = RegisterWindowMessageW(L"ts3wrap-size");
    if (!g_wrMsgFit || !g_wrMsgSize) { Log("SKIP wrapper: RegisterWindowMessage failed, nothing written"); return false; }
    g_wrBack = GetPrivateProfileIntW(L"Wrapper", L"Back", 0, ini) != 0;
    const struct { DWORD slot; const char* name; void* fn; void** orig; } hooks[] = {
        {0xF95864, "CreateWindowExW", WrCreateWindowExW, (void**)&g_wrCreate},
        {0xF9587C, "SetWindowLongA", WrSetWindowLongA, (void**)&g_wrSetLong},
        {0xF958A0, "MoveWindow", WrMoveWindow, (void**)&g_wrMove},
        {0xF9589C, "AdjustWindowRectEx", WrAdjustWindowRectEx, (void**)&g_wrAdjust},
        {0xF958F8, "GetForegroundWindow", WrGetForegroundWindow, (void**)&g_wrForeground},
        {0xF958F4, "IsIconic", WrIsIconic, (void**)&g_wrIconic},
        {0xF958F0, "IsZoomed", WrIsZoomed, (void**)&g_wrZoomed},
        {0xF958FC, "ShowWindow", WrShowWindow, (void**)&g_wrShow},
    };
    const HMODULE user32 = GetModuleHandleW(L"user32.dll");
    for (const auto& k : hooks)  // every slot checked before the first write: all hooks or none
        if (!Readable((void*)(uintptr_t)k.slot, sizeof(void*)) || !*(void**)(uintptr_t)k.slot) {
            Log("SKIP wrapper: import slot 0x%lX (%s) is empty, nothing written", k.slot, k.name);
            return false;
        }
    for (const auto& k : hooks) {
        if (*(void**)(uintptr_t)k.slot != (void*)GetProcAddress(user32, k.name))
            Log("wrapper: slot 0x%lX (%s) already points outside user32 (another mod), chained", k.slot, k.name);
        if (!WrHookSlot((void**)(uintptr_t)k.slot, k.fn, k.orig)) { Log("FAIL wrapper: slot 0x%lX (%s) write", k.slot, k.name); return true; }
    }
    Log("OK   wrapper: 8 import slots hooked, host starts with the game window%s", g_wrBack ? " (Back=1: behind other windows)" : "");
    ApplyRestart(ini);  // restart.cpp: [Wrapper] Channel / Restart (step 1)
    return true;
}
