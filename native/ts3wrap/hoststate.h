// docs/530 step 1: the host's lifecycle as a pure state machine (no Win32 calls): ts3wrap.cpp feeds it events and carries
// out the actions it returns; the selftest (native/ts3native/restart-test.h) drives it with a table of events and times.
//   Idle -> Running -> RestartRequested -> WaitingForExit -> Relaunching -> WaitingForWindow -> Loading -> Running
//   plus Exited / Crashed (the game ended outside a restart) and Failed (a restart could not finish without a game left).
// The host never ends while a game it hosts is alive: every give-up with a live game goes back to Running (the game keeps its
// window in the host and the player closes it as usual). Failed, Exited and Crashed have no game left, so the host quits.
#pragma once
#include <stdint.h>

enum HsState : int { HS_IDLE, HS_RUNNING, HS_RESTART_REQUESTED, HS_WAITING_FOR_EXIT, HS_RELAUNCHING, HS_WAITING_FOR_WINDOW, HS_LOADING,
                     HS_EXITED, HS_CRASHED, HS_FAILED };
static const char* const kHsStateName[] = {"Idle", "GameRunning", "RestartRequested", "WaitingForExit", "Relaunching",
                                           "WaitingForWindow", "Loading", "GameExited", "GameCrashed", "Failed"};

enum HsEvent : int {
    HE_ATTACH,       // the first game's process is open (the host starts)
    HE_REQUEST,      // game: restart requested (saved, state captured)
    HE_COVERED,      // host: the cover window is up
    HE_EXIT_OK,      // the game process ended after DLL_PROCESS_DETACH, exit code under 0xC0000000
    HE_EXIT_CRASH,   // ended without that, or with an exception code
    HE_LAUNCHED,     // host: CreateProcess of the relaunch worked
    HE_LAUNCH_FAIL,  // ... failed
    HE_WINDOW,       // the relaunched game's window is the host's child
    HE_READY,        // game: save loaded, state restored
    HE_GAME_FAIL,    // game: a step failed on its side
    HE_CLOSE,        // the player closed the host window
    HE_TICK,         // timer (timeouts)
};
static const char* const kHsEventName[] = {"attach", "restart requested", "covered", "exit", "crash", "launched", "launch failed",
                                           "window", "ready", "game failed", "close", "tick"};

enum : uint32_t {  // actions, any combination
    HA_COVER = 1,        // capture the last frame (if allowed), show the cover window over the client area
    HA_SIGNAL = 2,       // set the "-g" event: the game may quit now
    HA_LAUNCH = 4,       // start the game again (same exe, cwd, command line, environment; + -worldFile, hostCmd = restart boot)
    HA_UNCOVER = 8,      // drop the cover, focus the game window
    HA_POST_CLOSE = 16,  // PostMessage(WM_CLOSE) to the game window: the game asks its own quit question
    HA_MESSAGE = 32,     // show kHsMsg[msg] on the cover for a few seconds, then drop it (or, with HA_QUIT, in a box first)
    HA_QUIT = 64,        // the host ends (only with no game left)
};

enum : int { HM_NONE, HM_QUIT_STUCK, HM_LAUNCH_FAIL, HM_START_FAIL, HM_NO_WINDOW, HM_BOOT_FALLBACK, HM_READY_TIMEOUT };

struct Hs {
    HsState st = HS_IDLE;
    uint32_t since = 0;        // tick of the last state change
    bool closing = false;      // the player closed the host window during a restart: no relaunch, and close what comes
    int restarts = 0;          // restarts finished (ready)
    int msg = HM_NONE;         // the text of the last HA_MESSAGE
    uint32_t quitMs = 60000;   // WaitingForExit: the game's normal quit takes longer -> give up the restart (no kill)
    uint32_t windowMs = 120000;  // WaitingForWindow: no child window from the relaunched game -> give up
    uint32_t readyMs = 600000;   // Loading: no "ready" -> drop the cover, the game plays on as it is
};

static void HsGo(Hs& s, HsState to, uint32_t now) { s.st = to, s.since = now; }

// One event; returns the actions. Unknown pairs do nothing.
static uint32_t HsStep(Hs& s, HsEvent e, uint32_t now) {
    const bool exitEv = e == HE_EXIT_OK || e == HE_EXIT_CRASH;
    const uint32_t age = now - s.since;
    switch (s.st) {
    case HS_IDLE:
        if (e == HE_ATTACH) HsGo(s, HS_RUNNING, now);
        return 0;
    case HS_RUNNING:
        if (e == HE_REQUEST) { HsGo(s, HS_RESTART_REQUESTED, now); return HA_COVER; }
        if (e == HE_CLOSE) return HA_POST_CLOSE;
        if (exitEv) { HsGo(s, e == HE_EXIT_OK ? HS_EXITED : HS_CRASHED, now); return HA_QUIT; }
        return 0;
    case HS_RESTART_REQUESTED:
    case HS_WAITING_FOR_EXIT:
        if (e == HE_COVERED && s.st == HS_RESTART_REQUESTED) { HsGo(s, HS_WAITING_FOR_EXIT, now); return HA_SIGNAL; }
        if (e == HE_CLOSE) { s.closing = true; return 0; }  // the game quits anyway (it was told to): it is not started again
        if (e == HE_GAME_FAIL) { HsGo(s, HS_RUNNING, now); return HA_UNCOVER; }  // the game gave up before its quit
        if (exitEv) {  // a crash after the request still left the save that ended with result 0
            if (s.closing) { HsGo(s, e == HE_EXIT_OK ? HS_EXITED : HS_CRASHED, now); return HA_QUIT; }
            HsGo(s, HS_RELAUNCHING, now);
            return HA_LAUNCH;
        }
        if (e == HE_TICK && s.st == HS_WAITING_FOR_EXIT && age >= s.quitMs) {  // stuck in its quit: not killed, the player decides
            HsGo(s, HS_RUNNING, now);
            s.msg = HM_QUIT_STUCK;
            return HA_MESSAGE;
        }
        return 0;
    case HS_RELAUNCHING:
        if (e == HE_LAUNCHED) { HsGo(s, HS_WAITING_FOR_WINDOW, now); return 0; }
        if (e == HE_LAUNCH_FAIL) { HsGo(s, HS_FAILED, now); s.msg = HM_LAUNCH_FAIL; return HA_MESSAGE | HA_QUIT; }
        if (e == HE_CLOSE) s.closing = true;
        return 0;
    case HS_WAITING_FOR_WINDOW:
        if (e == HE_WINDOW) {
            if (s.closing) { HsGo(s, HS_RUNNING, now); return HA_UNCOVER | HA_POST_CLOSE; }
            HsGo(s, HS_LOADING, now);
            return 0;
        }
        if (e == HE_CLOSE) { s.closing = true; return 0; }
        if (exitEv) { HsGo(s, HS_FAILED, now); s.msg = HM_START_FAIL; return HA_MESSAGE | HA_QUIT; }
        if (e == HE_TICK && age >= s.windowMs) {  // a game that never became our child runs as its own top-level window
            HsGo(s, HS_FAILED, now);
            s.msg = HM_NO_WINDOW;
            return HA_MESSAGE | HA_QUIT;
        }
        return 0;
    case HS_LOADING:
        if (e == HE_READY) { HsGo(s, HS_RUNNING, now); s.restarts++; return HA_UNCOVER; }
        if (e == HE_GAME_FAIL) { HsGo(s, HS_RUNNING, now); s.msg = HM_BOOT_FALLBACK; return HA_MESSAGE; }
        if (e == HE_CLOSE) { HsGo(s, HS_RUNNING, now); return HA_UNCOVER | HA_POST_CLOSE; }
        if (exitEv) { HsGo(s, e == HE_EXIT_OK ? HS_EXITED : HS_CRASHED, now); s.msg = HM_START_FAIL; return HA_MESSAGE | HA_QUIT; }
        if (e == HE_TICK && age >= s.readyMs) { HsGo(s, HS_RUNNING, now); s.msg = HM_READY_TIMEOUT; return HA_MESSAGE; }
        return 0;
    default:  // Exited, Crashed, Failed: the host is ending
        return 0;
    }
}
