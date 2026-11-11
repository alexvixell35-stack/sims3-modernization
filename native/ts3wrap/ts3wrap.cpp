// ts3wrap.exe (docs/530 step 0, spike): the 64-bit host window of the game. Started by ts3native's [Wrapper] with the game's
// process id; the game then creates its own window as this window's child. The host owns the frame (title, size,
// minimize, close) and passes on what only a top-level window receives. It ends when the game process ends.
//   usage: ts3wrap.exe <game pid> [back]     back = shown behind the other windows, never activated (test runs)
#include <windows.h>
#include <stdlib.h>
#include <wchar.h>

static UINT g_fit, g_size;  // "ts3wrap-fit": the game asks for a client size; "ts3wrap-size": the client size we tell the game

// The client size the game asks for. The whole monitor or more: no frame, on the monitor (borderless). Else a framed window.
static void Fit(HWND h, int cx, int cy) {
    MONITORINFO mi = {sizeof(mi)};
    if (cx <= 0 || cy <= 0 || !GetMonitorInfoW(MonitorFromWindow(h, MONITOR_DEFAULTTONEAREST), &mi)) return;
    const bool full = cx >= mi.rcMonitor.right - mi.rcMonitor.left && cy >= mi.rcMonitor.bottom - mi.rcMonitor.top;
    const DWORD old = (DWORD)GetWindowLongW(h, GWL_STYLE);
    const DWORD style = (old & ~(DWORD)(WS_POPUP | WS_OVERLAPPEDWINDOW)) | (full ? WS_POPUP : WS_OVERLAPPEDWINDOW);
    RECT r = {0, 0, cx, cy};
    if (full) r = mi.rcMonitor;
    else AdjustWindowRectEx(&r, style, FALSE, (DWORD)GetWindowLongW(h, GWL_EXSTYLE));
    if (style != old) SetWindowLongW(h, GWL_STYLE, (LONG)style);
    SetWindowPos(h, NULL, r.left, r.top, r.right - r.left, r.bottom - r.top,
                 SWP_NOZORDER | SWP_NOACTIVATE | (full ? 0 : SWP_NOMOVE) | (style != old ? SWP_FRAMECHANGED : 0));
}

static LRESULT CALLBACK Proc(HWND h, UINT m, WPARAM w, LPARAM l) {
    const HWND game = GetWindow(h, GW_CHILD);
    if (m == g_fit && g_fit) { Fit(h, (int)w, (int)l); return 0; }
    switch (m) {
    case WM_SIZE:  // async: a game thread busy in a load must not hang the frame
        if (game && w != SIZE_MINIMIZED) PostMessageW(game, g_size, LOWORD(l), HIWORD(l));
        return 0;
    case WM_SETFOCUS:
        if (game) SetFocus(game);
        return 0;
    case WM_ACTIVATEAPP:
    case WM_DISPLAYCHANGE:  // top-level only: the game's window procedure handles both
        if (game) PostMessageW(game, m, w, l);
        break;
    case WM_CLOSE:  // the game asks its own quit question
        if (game) { PostMessageW(game, WM_CLOSE, 0, 0); return 0; }
        break;
    case WM_PARENTNOTIFY:
        if (LOWORD(w) == WM_DESTROY) DestroyWindow(h);
        break;
    case WM_ERASEBKGND:
        if (game) return 1;
        break;
    case WM_DESTROY:
        PostQuitMessage(0);
        return 0;
    }
    return DefWindowProcW(h, m, w, l);
}

int WINAPI wWinMain(HINSTANCE inst, HINSTANCE, PWSTR cmd, int) {
    // A child window takes its parent's DPI awareness. Without this the game window (aware: the usual HIGHDPIAWARE layer on
    // TS3W.exe) became a virtualized one of a 1707x1067 "monitor" on a 2560x1600 display at 150% (run wsp3).
    // shortcut: system-aware only, read the game's own awareness instead when a game without that layer has to stay unscaled
    SetProcessDPIAware();
    wchar_t* end = cmd;
    const DWORD pid = wcstoul(cmd, &end, 10);
    const HANDLE game = pid ? OpenProcess(SYNCHRONIZE, FALSE, pid) : NULL;
    if (!game) return 2;
    wchar_t cls[32];
    swprintf_s(cls, L"ts3wrap-%lu", pid);  // the game finds the window by this class name
    WNDCLASSW wc = {};
    wc.lpfnWndProc = Proc;
    wc.hInstance = inst;
    wc.lpszClassName = cls;
    wc.hCursor = LoadCursorW(NULL, IDC_ARROW);
    wc.hbrBackground = (HBRUSH)GetStockObject(BLACK_BRUSH);
    g_fit = RegisterWindowMessageW(L"ts3wrap-fit"), g_size = RegisterWindowMessageW(L"ts3wrap-size");
    if (!RegisterClassW(&wc) || !g_fit || !g_size) return 3;
    const HWND h = CreateWindowExW(0, cls, L"The Sims 3", WS_OVERLAPPEDWINDOW | WS_CLIPCHILDREN, CW_USEDEFAULT, CW_USEDEFAULT, 1024, 768,
                                   NULL, NULL, inst, NULL);
    if (!h) return 4;
    if (wcsstr(end, L"back")) SetWindowPos(h, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
    else ShowWindow(h, SW_SHOW);
    for (;;) {
        if (MsgWaitForMultipleObjects(1, &game, FALSE, INFINITE, QS_ALLINPUT) == WAIT_OBJECT_0) return 0;  // game gone (exit or crash)
        MSG msg;
        while (PeekMessageW(&msg, NULL, 0, 0, PM_REMOVE)) {
            if (msg.message == WM_QUIT) return 0;
            DispatchMessageW(&msg);
        }
    }
}
