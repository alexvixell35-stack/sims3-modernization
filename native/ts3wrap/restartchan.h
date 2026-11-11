// docs/530 step 1: the restart channel between ts3wrap.exe (x64 host) and ts3native.asi (x86, inside TS3W.exe). One small
// named file mapping plus two auto-reset events, all named after the FIRST game's process id and a 128-bit session id, so
// two installs or two sessions never meet:
//   Local\ts3wrap-<pid>-<32 hex>        the block below (created by the first game, kept open by the host across restarts)
//   Local\ts3wrap-<pid>-<32 hex>-h      game -> host: gameMsg changed
//   Local\ts3wrap-<pid>-<32 hex>-g      host -> game: the cover is up (the game waits for it before it quits, 1.5 s at most)
// A relaunched game finds it through the environment variable TS3WRAP_CHANNEL=<pid>-<32 hex> that the host sets for it.
// Fixed-width fields only (no pointers, the HWND as 64 bits): both builds see one layout (static_assert below). Either side
// ignores a block whose magic, version or size differs. Nothing here is ever written into a save.
#pragma once
#include <stdint.h>
#include <wchar.h>

static const uint32_t kWrChanMagic = 0x43573354;  // "T3WC"
static const uint16_t kWrChanVer = 1;
static const wchar_t kWrChanEnv[] = L"TS3WRAP_CHANNEL";

enum : int32_t {  // gameMsg: the game's last word to the host (msgSeq counts every write)
    WR_G_NONE = 0,
    WR_G_RESTART = 1,  // saved (result 0), state captured into the handoff file: cover the window, the game quits next
    WR_G_READY = 2,    // the relaunched game loaded the save and restored the state: drop the cover
    WR_G_FAILED = 3,   // a step failed on the game side (reason in gameWhy): drop the cover, play on as the game is
};
enum : int32_t {  // hostCmd: what the host tells the game it starts
    WR_H_NONE = 0,
    WR_H_RESTART_BOOT = 1,  // you were started by a restart: EA's -worldFile loads the save, restore the handoff file's state
};

struct WrChan {
    uint32_t magic;
    uint16_t version, size;
    uint32_t firstPid;           // the first game (part of the names)
    uint32_t hostPid;            // written by the host when it opens the block
    uint64_t hostWnd;            // the host's top-level window (HWND)
    volatile int32_t gamePid;    // the game that holds the channel now
    volatile int32_t beat;       // GetTickCount of the game window's last message (heartbeat, written by the game)
    volatile int32_t bye;        // 1: the game reached DLL_PROCESS_DETACH (a normal exit); 0 at every game start
    volatile int32_t gameMsg;    // WR_G_*
    volatile int32_t msgSeq;     // +1 per gameMsg write
    int32_t gameWhy;             // WR_G_FAILED reason (restart.cpp kWrWhy)
    int32_t reason;              // restart reason: 1 low memory after a save, 2 the hotkey's save
    int32_t bigBeforeMB, bigAfterMB;  // largest free block at the request / at ready
    int32_t restored;            // what the restore put back (bit 0 camera, 1 selected Sim, 2 queues, 3 HUD panel, 4 speed)
    volatile int32_t hostCmd;    // WR_H_*: set by the host before it starts a game, cleared by the game that read it
    int32_t restarts;            // restarts done in this host session (host)
    uint32_t quitMs, windowMs, readyMs;  // the host's timeouts, from the first game's ini
    int32_t frame;               // 1: the cover shows the last frame, 0: black
    wchar_t save[128];           // the save's folder name, "<name>.sims3" (the host's log and its -worldFile)
    wchar_t exe[260];            // the first game's executable and command line, captured at its start: the relaunch uses them
    wchar_t cmd[2048];
};
static_assert(sizeof(WrChan) == 20 * 4 + 8 + 2 * (128 + 260 + 2048), "WrChan: one layout for the x86 game and the x64 host");

// "Local\ts3wrap-<pid>-<id><suffix>"; id = 32 hex digits. False when it does not fit or id is not 32 hex digits.
static bool WrChanName(wchar_t* out, size_t cap, uint32_t pid, const wchar_t* id, const wchar_t* suffix) {
    if (!id || wcslen(id) != 32) return false;
    for (const wchar_t* p = id; *p; p++)
        if (!((*p >= L'0' && *p <= L'9') || (*p >= L'a' && *p <= L'f') || (*p >= L'A' && *p <= L'F'))) return false;
    return _snwprintf_s(out, cap, _TRUNCATE, L"Local\\ts3wrap-%lu-%s%s", (unsigned long)pid, id, suffix ? suffix : L"") > 0;  // too small: false, no abort
}

// The block is ours and of this build's layout (mapped = bytes the view covers).
static bool WrChanOk(const WrChan* c, size_t mapped) {
    return c && mapped >= sizeof(WrChan) && c->magic == kWrChanMagic && c->version == kWrChanVer && c->size == sizeof(WrChan);
}
