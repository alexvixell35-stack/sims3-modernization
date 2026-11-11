// ts3wrap.exe (docs/530 steps 0 and 1): the 64-bit host window of the game. Started by ts3native's [Wrapper] with the game's
// process id; the game then creates its own window as this window's child. The host owns the frame (title, size,
// minimize, close) and passes on what only a top-level window receives.
//   usage: ts3wrap.exe <game pid> [<32 hex session id>] [back]     back = shown behind the other windows, never activated
// Step 1 ([Wrapper] Channel=1: the session id is given): the host opens the game's channel (restartchan.h) and runs the
// lifecycle of hoststate.h. On "restart requested" it covers its client area ("Refreshing memory... your game is saved",
// over the last frame or black), lets the game quit its normal way, starts it again with the first game's executable,
// start folder, command line and environment plus EA's -worldFile switch for the save, waits for the new game window to
// become its child and for "ready", then drops the cover. Its window stays up the whole time. The host never kills the
// game and never ends while a game it hosts is alive; closing it asks the game to close (the game's own question).
// One host per Windows session: a second game finds the mutex taken and runs unwrapped. Log: ts3wrap.log next to the exe
// (file names only, no paths).
#include <windows.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <wchar.h>
#include "restartchan.h"
#include "hoststate.h"

static UINT g_fit, g_size;  // "ts3wrap-fit": the game asks for a client size; "ts3wrap-size": the client size we tell the game
static HWND g_host, g_cover;
static HANDLE g_log, g_game, g_evH, g_evG;
static DWORD g_gamePid;
static WrChan* g_chan;
static Hs g_hs;
static wchar_t g_cwd[MAX_PATH];
static HBITMAP g_frame;           // the last frame under the cover (NULL: black)
static int g_frameW, g_frameH;
static const wchar_t* g_note;     // a message on the cover (NULL: the restart's own text)
static DWORD g_noteUntil, g_reqTick, g_exitTick, g_windowTick, g_lastBeatLog, g_paintTick;
static bool g_beatStale;
static HFONT g_font, g_fontSmall;

static void Log(const char* fmt, ...) {
    if (!g_log) return;
    SYSTEMTIME st;
    GetLocalTime(&st);
    char b[1024];
    int n = sprintf_s(b, "%04d-%02d-%02d %02d:%02d:%02d.%03d ", st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond, st.wMilliseconds);
    va_list a;
    va_start(a, fmt);
    const int m = vsnprintf(b + n, sizeof(b) - n - 3, fmt, a);
    va_end(a);
    n += m > 0 ? m : 0;
    b[n++] = '\r', b[n++] = '\n';
    DWORD w;
    WriteFile(g_log, b, (DWORD)n, &w, NULL);
}

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

// The game window: the host's child of the current game process (the cover is an owned popup, never a child).
static HWND GameWnd() {
    for (HWND c = GetWindow(g_host, GW_CHILD); c; c = GetWindow(c, GW_HWNDNEXT)) {
        DWORD pid = 0;
        GetWindowThreadProcessId(c, &pid);
        if (pid == g_gamePid) return c;
    }
    return NULL;
}

// --- the cover: an owned, never-activated popup over the client area (the game keeps quitting / loading under it)

static void PlaceCover() {
    if (!g_cover) return;
    RECT r;
    GetClientRect(g_host, &r);
    MapWindowPoints(g_host, NULL, (POINT*)&r, 2);
    SetWindowPos(g_cover, NULL, r.left, r.top, r.right - r.left, r.bottom - r.top, SWP_NOZORDER | SWP_NOACTIVATE);
}

static void Cover() {
    RECT r;
    GetClientRect(g_host, &r);
    if (g_frame) DeleteObject(g_frame), g_frame = NULL;
    // The last frame from the screen (the composed desktop), only while the host is in front and shown: never another
    // window's pixels. No message is sent to the game (its thread may be busy; a cross-process child shares our input).
    if (g_chan && g_chan->frame && GetForegroundWindow() == g_host && !IsIconic(g_host) && r.right > 0 && r.bottom > 0) {
        POINT o = {0, 0};
        ClientToScreen(g_host, &o);
        HDC scr = GetDC(NULL), mem = CreateCompatibleDC(scr);
        HBITMAP b = CreateCompatibleBitmap(scr, r.right, r.bottom);
        HGDIOBJ old = b ? SelectObject(mem, b) : NULL;
        if (b && BitBlt(mem, 0, 0, r.right, r.bottom, scr, o.x, o.y, SRCCOPY)) g_frame = b, g_frameW = r.right, g_frameH = r.bottom;
        if (old) SelectObject(mem, old);
        if (b && !g_frame) DeleteObject(b);
        DeleteDC(mem);
        ReleaseDC(NULL, scr);
    }
    g_note = NULL;
    if (!g_cover) {
        g_cover = CreateWindowExW(WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, L"ts3wrap-cover", L"", WS_POPUP, 0, 0, 0, 0, g_host, NULL,
                                  GetModuleHandleW(NULL), NULL);
        if (!g_cover) { Log("cover window not created (err %lu): the game is not covered", GetLastError()); return; }
    }
    PlaceCover();
    ShowWindow(g_cover, SW_SHOWNOACTIVATE);
    InvalidateRect(g_cover, NULL, FALSE);
    UpdateWindow(g_cover);
    Log("cover up (%s)", g_frame ? "last frame" : "black");
}

static void Uncover() {
    if (g_cover) ShowWindow(g_cover, SW_HIDE);
    if (g_frame) DeleteObject(g_frame), g_frame = NULL;
    g_note = NULL;
    const HWND game = GameWnd();
    if (game && GetForegroundWindow() == g_host) SetFocus(game);
}

static void PaintCover(HWND h) {
    PAINTSTRUCT ps;
    HDC dc = BeginPaint(h, &ps);
    RECT r;
    GetClientRect(h, &r);
    if (g_frame) {
        HDC mem = CreateCompatibleDC(dc);
        HGDIOBJ old = SelectObject(mem, g_frame);
        if (r.right == g_frameW && r.bottom == g_frameH) BitBlt(dc, 0, 0, r.right, r.bottom, mem, 0, 0, SRCCOPY);
        else SetStretchBltMode(dc, HALFTONE), StretchBlt(dc, 0, 0, r.right, r.bottom, mem, 0, 0, g_frameW, g_frameH, SRCCOPY);
        SelectObject(mem, old);
        DeleteDC(mem);
    } else FillRect(dc, &r, (HBRUSH)GetStockObject(BLACK_BRUSH));
    RECT band = {0, r.bottom / 2 - 60, r.right, r.bottom / 2 + 60};
    FillRect(dc, &band, (HBRUSH)GetStockObject(BLACK_BRUSH));
    SetBkMode(dc, TRANSPARENT);
    SetTextColor(dc, RGB(255, 255, 255));
    HGDIOBJ of = SelectObject(dc, g_font);
    RECT t = {16, band.top + 14, r.right - 16, band.top + 64};
    DrawTextW(dc, g_note ? g_note : L"Refreshing memory... your game is saved", -1, &t, DT_CENTER | DT_SINGLELINE | DT_END_ELLIPSIS | DT_NOPREFIX);
    wchar_t sub[96];
    const DWORD s = (GetTickCount() - g_reqTick) / 1000;
    const HsState st = g_hs.st;
    if (g_note) wcscpy_s(sub, L"Click to close this message.");
    else swprintf_s(sub, L"%s (%lu s)", st == HS_RESTART_REQUESTED || st == HS_WAITING_FOR_EXIT ? L"Closing the game"
                                        : st == HS_LOADING ? L"Loading your save" : L"Starting the game", s);
    SelectObject(dc, g_fontSmall);
    RECT u = {16, band.top + 68, r.right - 16, band.bottom - 8};
    SetTextColor(dc, RGB(200, 200, 200));
    DrawTextW(dc, sub, -1, &u, DT_CENTER | DT_SINGLELINE | DT_END_ELLIPSIS | DT_NOPREFIX);
    SelectObject(dc, of);
    EndPaint(h, &ps);
}

static LRESULT CALLBACK CoverProc(HWND h, UINT m, WPARAM w, LPARAM l) {
    switch (m) {
    case WM_PAINT: PaintCover(h); return 0;
    case WM_ERASEBKGND: return 1;
    case WM_MOUSEACTIVATE: return MA_NOACTIVATE;
    case WM_LBUTTONDOWN:
        if (g_note && g_hs.st == HS_RUNNING) Uncover(), Log("message closed by a click");
        return 0;
    }
    return DefWindowProcW(h, m, w, l);
}

// --- the lifecycle

static const wchar_t* MsgText(int msg) {
    switch (msg) {
    case HM_QUIT_STUCK: return L"The game did not finish closing, so it was not restarted. It keeps running: save and quit as usual.";
    case HM_LAUNCH_FAIL: return L"The game could not be started again. Your game was saved: start The Sims 3 as usual.";
    case HM_START_FAIL: return L"The game closed while it was starting again. Your game was saved: start The Sims 3 as usual.";
    case HM_NO_WINDOW: return L"The game did not come back into this window in 2 minutes. If it opens in its own window, play there.";
    case HM_BOOT_FALLBACK:
        return g_chan && g_chan->gameWhy == 3 ? L"Your save is loaded, but speed, camera and selection were not put back."
                                              : L"The game started again but did not load your save: load it from the menu.";
    case HM_READY_TIMEOUT: return L"Loading your save takes long: the game is shown as it is.";
    }
    return L"";
}

static void Feed(HsEvent e);

static void Launch() {
    static wchar_t cmd[(sizeof(WrChan::cmd) + sizeof(WrChan::save)) / sizeof(wchar_t) + 32];
    if (!g_chan || !g_chan->exe[0] || !g_chan->cmd[0] || !g_chan->save[0] ||
        swprintf_s(cmd, L"%.2047s -worldFile:\"%.127s\"", g_chan->cmd, g_chan->save) < 0) {  // bounded: the block is shared memory
        Log("relaunch: the channel has no executable, command line or save");
        Feed(HE_LAUNCH_FAIL);
        return;
    }
    InterlockedExchange((volatile LONG*)&g_chan->hostCmd, WR_H_RESTART_BOOT);
    InterlockedExchange((volatile LONG*)&g_chan->gameMsg, WR_G_NONE);
    STARTUPINFOW si = {sizeof(si)};
    PROCESS_INFORMATION pi;
    // the first game's executable, start folder (ours: the game started us in it) and environment (ours: inherited from it,
    // plus TS3WRAP_CHANNEL); its command line plus -worldFile (EA's MainMenuState.Startup loads that save at the first menu)
    if (!CreateProcessW(g_chan->exe, cmd, NULL, NULL, FALSE, 0, NULL, g_cwd[0] ? g_cwd : NULL, &si, &pi)) {
        Log("relaunch: CreateProcess failed (err %lu)", GetLastError());
        InterlockedExchange((volatile LONG*)&g_chan->hostCmd, WR_H_NONE);
        Feed(HE_LAUNCH_FAIL);
        return;
    }
    CloseHandle(pi.hThread);
    g_game = pi.hProcess, g_gamePid = pi.dwProcessId;
    const wchar_t* exe = wcsrchr(g_chan->exe, L'\\');
    Log("relaunch: %.259ls started (pid %lu) with -worldFile for %.127ls", exe ? exe + 1 : g_chan->exe, g_gamePid, g_chan->save);
    Feed(HE_LAUNCHED);
}

static void Do(uint32_t a) {
    if (a & HA_COVER) {
        g_reqTick = GetTickCount();
        Cover();
        Feed(HE_COVERED);
    }
    if (a & HA_SIGNAL && g_evG) SetEvent(g_evG);  // the game quits now
    if (a & HA_LAUNCH) Launch();
    if (a & HA_UNCOVER) Uncover();
    if (a & HA_POST_CLOSE) {
        const HWND game = GameWnd();
        if (game) PostMessageW(game, WM_CLOSE, 0, 0);
        Log("close passed to the game window%s", game ? "" : " (none)");
    }
    if (a & HA_MESSAGE) {
        const wchar_t* t = MsgText(g_hs.msg);
        Log("message %d shown: %ls", g_hs.msg, t);
        if (a & HA_QUIT) MessageBoxW(g_host, t, L"The Sims 3", MB_OK | MB_ICONWARNING | MB_SETFOREGROUND);
        else {
            if (!g_cover || !IsWindowVisible(g_cover)) Cover();
            g_note = t, g_noteUntil = GetTickCount() + 10000;
            if (g_cover) InvalidateRect(g_cover, NULL, FALSE);
        }
    }
    if (a & HA_QUIT) {
        Log("host ends (%s, %d restarts)", kHsStateName[g_hs.st], g_hs.restarts);
        DestroyWindow(g_host);
    }
}

static void Feed(HsEvent e) {
    const HsState was = g_hs.st;
    const uint32_t a = HsStep(g_hs, e, GetTickCount());
    if (was != g_hs.st || a) Log("state %s -> %s on %s, actions 0x%X", kHsStateName[was], kHsStateName[g_hs.st], kHsEventName[e], a);
    if (was == HS_LOADING && g_hs.st == HS_RUNNING && g_chan) {  // the restart's one summary line
        const DWORD now = GetTickCount();
        Log("restart summary #%d: %s; largest free %ld MB before, %ld MB after; %.1f s to quit, %.1f s to the window, %.1f s to %s, "
            "%.1f s from the request; restored 0x%X (bit 0 camera, 1 selected Sim, 2 queues, 3 HUD panel, 4 speed)",
            g_hs.restarts, e == HE_READY ? "ready" : kHsEventName[e], (long)g_chan->bigBeforeMB, (long)g_chan->bigAfterMB,
            (g_exitTick - g_reqTick) / 1000.0, (g_windowTick - g_exitTick) / 1000.0, (now - g_windowTick) / 1000.0,
            e == HE_READY ? "ready" : "the end of the cover", (now - g_reqTick) / 1000.0, g_chan->restored);
        g_chan->restarts = g_hs.restarts;
    }
    Do(a);
}

static void GameEnded() {
    DWORD code = 0;
    GetExitCodeProcess(g_game, &code);
    const bool bye = g_chan && g_chan->bye;
    // exit vs crash: an exception code (0xC.......) or no DLL_PROCESS_DETACH (the channel's bye) is a crash
    const bool ok = code < 0xC0000000u && (bye || !g_chan);
    const long beat = g_chan ? (long)(GetTickCount() - (DWORD)g_chan->beat) : -1;
    Log("game pid %lu ended: exit code 0x%08lX, %s, last window message %ld ms before: %s", g_gamePid, code,
        !g_chan ? "no channel" : bye ? "reached DLL detach" : "no DLL detach", beat, ok ? "exit" : "crash");
    CloseHandle(g_game);
    g_game = NULL;
    g_exitTick = GetTickCount();
    Feed(ok ? HE_EXIT_OK : HE_EXIT_CRASH);
}

static void GameSaid() {
    if (!g_chan) return;
    const int32_t m = InterlockedExchange((volatile LONG*)&g_chan->gameMsg, WR_G_NONE);
    if (m == WR_G_RESTART)
        Log("game: restart requested (%s, save %.127ls, largest free %ld MB)", g_chan->reason == 2 ? "hotkey" : "low memory", g_chan->save,
            (long)g_chan->bigBeforeMB);
    else if (m == WR_G_READY) Log("game: ready (largest free %ld MB, restored 0x%X)", (long)g_chan->bigAfterMB, g_chan->restored);
    else if (m == WR_G_FAILED) Log("game: a step failed (reason %d)", g_chan->gameWhy);
    if (m == WR_G_RESTART) Feed(HE_REQUEST);
    else if (m == WR_G_READY) Feed(HE_READY);
    else if (m == WR_G_FAILED) Feed(HE_GAME_FAIL);
}

static void Tick() {
    const DWORD now = GetTickCount();
    if (g_hs.st == HS_WAITING_FOR_WINDOW && GameWnd()) {
        g_windowTick = now;
        Log("game window 0x%p of pid %lu is the host's child", GameWnd(), g_gamePid);
        Feed(HE_WINDOW);
    }
    Feed(HE_TICK);
    if (g_note && g_hs.st == HS_RUNNING && (int)(now - g_noteUntil) >= 0) Uncover();
    if (g_cover && IsWindowVisible(g_cover) && !g_note && now - g_paintTick >= 1000)  // the seconds, once a second
        g_paintTick = now, InvalidateRect(g_cover, NULL, FALSE);
    if (g_chan && g_hs.st == HS_RUNNING && g_chan->beat) {  // heartbeat: logged only (a long load blocks the window thread too)
        const DWORD age = now - (DWORD)g_chan->beat;
        if (age > 30000 && !g_beatStale && now - g_lastBeatLog > 30000) g_beatStale = true, g_lastBeatLog = now, Log("game window silent for %lu s", age / 1000);
        else if (age < 5000 && g_beatStale) g_beatStale = false, Log("game window answers again");
    }
}

static LRESULT CALLBACK Proc(HWND h, UINT m, WPARAM w, LPARAM l) {
    const HWND game = GameWnd();
    if (m == g_fit && g_fit) { Fit(h, (int)w, (int)l); return 0; }
    switch (m) {
    case WM_SIZE:  // async: a game thread busy in a load must not hang the frame
        if (game && w != SIZE_MINIMIZED) PostMessageW(game, g_size, LOWORD(l), HIWORD(l));
        PlaceCover();
        return 0;
    case WM_MOVE:
        PlaceCover();
        break;
    case WM_SETFOCUS:  // not while covered: that game is quitting or loading
        if (game && !(g_cover && IsWindowVisible(g_cover))) SetFocus(game);
        return 0;
    case WM_ACTIVATEAPP:
    case WM_DISPLAYCHANGE:  // top-level only: the game's window procedure handles both
        if (game) PostMessageW(game, m, w, l);
        break;
    case WM_CLOSE:  // the game asks its own quit question; during a restart: no relaunch (hoststate.h)
        Feed(HE_CLOSE);
        if (g_hs.st == HS_EXITED || g_hs.st == HS_CRASHED || g_hs.st == HS_FAILED || g_hs.st == HS_IDLE) DestroyWindow(h);
        return 0;
    case WM_PARENTNOTIFY:  // the game window closed outside a restart (its own quit): hidden until the process ends, as step 0
        if (LOWORD(w) == WM_DESTROY && game && (HWND)l == game && g_hs.st == HS_RUNNING) ShowWindow(h, SW_HIDE), Log("game window closed");
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

static bool OpenChannel(DWORD pid, const wchar_t* id) {
    wchar_t n[96], ng[96], nh[96];
    if (!WrChanName(n, 96, pid, id, NULL) || !WrChanName(nh, 96, pid, id, L"-h") || !WrChanName(ng, 96, pid, id, L"-g")) return false;
    HANDLE m = OpenFileMappingW(FILE_MAP_ALL_ACCESS, FALSE, n);  // kept open: the block outlives the first game
    WrChan* c = m ? (WrChan*)MapViewOfFile(m, FILE_MAP_ALL_ACCESS, 0, 0, 0) : NULL;
    MEMORY_BASIC_INFORMATION mbi = {};
    if (c) VirtualQuery(c, &mbi, sizeof(mbi));
    if (!c || !WrChanOk(c, mbi.RegionSize)) {
        Log("channel %s: no restarts this session", !c ? "not found" : "has another version or size");
        if (c) UnmapViewOfFile(c);
        if (m) CloseHandle(m);
        return false;
    }
    g_evH = OpenEventW(SYNCHRONIZE | EVENT_MODIFY_STATE, FALSE, nh);
    g_evG = OpenEventW(SYNCHRONIZE | EVENT_MODIFY_STATE, FALSE, ng);
    if (!g_evH || !g_evG) { Log("channel events not found (err %lu): no restarts this session", GetLastError()); return false; }
    g_chan = c;
    wchar_t v[64];
    swprintf_s(v, L"%lu-%s", pid, id);
    SetEnvironmentVariableW(kWrChanEnv, v);  // every game we start finds the channel
    g_hs.quitMs = c->quitMs, g_hs.windowMs = c->windowMs, g_hs.readyMs = c->readyMs;
    Log("channel v%u opened (%u bytes, session %.8ls...), timeouts: quit %lu s, window %lu s, ready %lu s, cover %s", c->version, c->size, id,
        c->quitMs / 1000, c->windowMs / 1000, c->readyMs / 1000, c->frame ? "last frame" : "black");
    return true;
}

int WINAPI wWinMain(HINSTANCE inst, HINSTANCE, PWSTR cmd, int) {
    // A child window takes its parent's DPI awareness. Without this the game window (aware: the usual HIGHDPIAWARE layer on
    // TS3W.exe) became a virtualized one of a 1707x1067 "monitor" on a 2560x1600 display at 150% (run wsp3).
    // shortcut: system-aware only, read the game's own awareness instead when a game without that layer has to stay unscaled
    SetProcessDPIAware();
    wchar_t* end = cmd;
    const DWORD pid = wcstoul(cmd, &end, 10);
    g_game = pid ? OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid) : NULL;
    if (!g_game) return 2;
    g_gamePid = pid;
    if (CreateMutexW(NULL, FALSE, L"Local\\ts3wrap-host") && GetLastError() == ERROR_ALREADY_EXISTS) return 5;  // one host
    wchar_t id[33] = L"";
    while (*end == L' ') end++;
    if (wcsspn(end, L"0123456789abcdefABCDEF") == 32 && (end[32] == 0 || end[32] == L' ')) wcsncpy_s(id, end, 32), end += 32;
    GetCurrentDirectoryW(MAX_PATH, g_cwd);  // the first game started us in its own start folder: the relaunch uses it
    wchar_t logp[MAX_PATH];
    const DWORD n = GetModuleFileNameW(NULL, logp, MAX_PATH);
    wchar_t* slash = n && n < MAX_PATH ? wcsrchr(logp, L'\\') : NULL;
    if (slash && !wcscpy_s(slash + 1, MAX_PATH - (slash + 1 - logp), L"ts3wrap.log"))
        g_log = CreateFileW(logp, GENERIC_WRITE, FILE_SHARE_READ, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (g_log == INVALID_HANDLE_VALUE) g_log = NULL;
    Log("ts3wrap: host of game pid %lu%s", pid, id[0] ? "" : ", no channel (step 0: the window only)");

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
    WNDCLASSW cc = {};
    cc.lpfnWndProc = CoverProc;
    cc.hInstance = inst;
    cc.lpszClassName = L"ts3wrap-cover";
    cc.hCursor = LoadCursorW(NULL, IDC_WAIT);
    RegisterClassW(&cc);
    g_font = CreateFontW(-32, 0, 0, 0, FW_SEMIBOLD, 0, 0, 0, DEFAULT_CHARSET, 0, 0, CLEARTYPE_QUALITY, 0, L"Segoe UI");
    g_fontSmall = CreateFontW(-20, 0, 0, 0, FW_NORMAL, 0, 0, 0, DEFAULT_CHARSET, 0, 0, CLEARTYPE_QUALITY, 0, L"Segoe UI");
    g_host = CreateWindowExW(0, cls, L"The Sims 3", WS_OVERLAPPEDWINDOW | WS_CLIPCHILDREN, CW_USEDEFAULT, CW_USEDEFAULT, 1024, 768,
                             NULL, NULL, inst, NULL);
    if (!g_host) return 4;
    if (id[0] && OpenChannel(pid, id)) {
        g_chan->hostPid = GetCurrentProcessId();
        g_chan->hostWnd = (uint64_t)(uintptr_t)g_host;
    }
    if (wcsstr(end, L"back")) SetWindowPos(g_host, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
    else ShowWindow(g_host, SW_SHOW);
    Feed(HE_ATTACH);
    for (;;) {
        HANDLE w[2];
        DWORD k = 0;
        if (g_game) w[k++] = g_game;
        if (g_evH) w[k++] = g_evH;
        const DWORD r = MsgWaitForMultipleObjects(k, w, FALSE, 250, QS_ALLINPUT);
        if (r < WAIT_OBJECT_0 + k) {
            if (w[r - WAIT_OBJECT_0] == g_game) GameEnded();
            else GameSaid();
        }
        MSG msg;
        while (PeekMessageW(&msg, NULL, 0, 0, PM_REMOVE)) {
            if (msg.message == WM_QUIT) return 0;
            DispatchMessageW(&msg);
        }
        if (!IsWindow(g_host)) return 0;
        Tick();
    }
}
