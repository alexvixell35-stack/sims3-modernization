// TestRestart ([Wrapper] Channel / Restart, restart.cpp and ../ts3wrap/hoststate.h, docs/530 §12). Included by selftest.cpp.
// No game, no host process: the channel names and layout check, the handoff file's build and every refusal, the verdict, the
// bit reads ts3fixes makes, and the host's state machine driven by a table of events and times.
#include "../ts3wrap/hoststate.h"

struct HsRow { HsEvent e; uint32_t now; HsState st; uint32_t act; };
static void HsRun(Hs& s, const HsRow* rows, int n, const char* what) {
    for (int i = 0; i < n; i++) {
        const uint32_t a = HsStep(s, rows[i].e, rows[i].now);
        if (s.st != rows[i].st || a != rows[i].act) {
            printf("hoststate %s row %d (%s at %u): got %s 0x%X, want %s 0x%X\n", what, i, kHsEventName[rows[i].e], rows[i].now, kHsStateName[s.st], a,
                   kHsStateName[rows[i].st], rows[i].act);
            assert(false);
        }
    }
}

static void TestRestart() {
    // --- channel: names, layout check
    wchar_t n[96];
    const wchar_t* id = L"0123456789abcdef0123456789ABCDEF";
    assert(WrChanName(n, 96, 4242, id, L"-h") && !wcscmp(n, L"Local\\ts3wrap-4242-0123456789abcdef0123456789ABCDEF-h"));
    assert(!WrChanName(n, 96, 1, L"0123", NULL) && !WrChanName(n, 96, 1, L"0123456789abcdef0123456789abcdeg", NULL) && !WrChanName(n, 96, 1, NULL, NULL));
    assert(!WrChanName(n, 20, 4242, id, NULL));  // does not fit
    static WrChan c;
    c.magic = kWrChanMagic, c.version = kWrChanVer, c.size = (uint16_t)sizeof(WrChan);
    assert(WrChanOk(&c, sizeof(c)) && WrChanOk(&c, 8192) && !WrChanOk(&c, sizeof(c) - 1) && !WrChanOk(NULL, 8192));
    c.version = kWrChanVer + 1;
    assert(!WrChanOk(&c, sizeof(c)));  // another build's layout: ignored
    c.version = kWrChanVer, c.size--;
    assert(!WrChanOk(&c, sizeof(c)));
    c.size++, c.magic ^= 1;
    assert(!WrChanOk(&c, sizeof(c)));
    assert(offsetof(WrChan, hostWnd) == 16 && offsetof(WrChan, save) == 88);  // the x64 host sees the same offsets (static_assert on size there)

    // --- session id
    u8 sess[16], other[16];
    assert(WrIdBytes(id, sess) && sess[0] == 0x01 && sess[7] == 0xEF && sess[15] == 0xEF && !WrIdBytes(L"xyz", sess));
    assert(WrIdBytes(id, sess));
    memcpy(other, sess, 16), other[3] ^= 0x40;

    // --- handoff file: built, then every refusal
    static u8 f[sizeof(WrHandoff) + 256];
    const char* rec = "R1|1|0|42|1|1|2|3|4|5|6\nS|42\n";
    const size_t recN = strlen(rec);
    WrSaveId sid;
    sid.bytes = 123456789, sid.newestFt = 133000000000000000ull, sid.files = 7;
    const u64 now = 133000000600000000ull;  // a minute later
    const size_t fn = WrHandoffBuild(f, sizeof(f), sess, now - 600000000ull, L"My Save.sims3", sid, 300, 1, rec, recN);
    assert(fn == sizeof(WrHandoff) + recN);
    assert(!WrHandoffWhy(f, fn, sess, L"my save.SIMS3", sid, now, 15));  // file names compare without case
    assert(!strcmp(WrHandoffWhy(f, fn, other, L"My Save.sims3", sid, now, 15), "another session"));
    assert(!strcmp(WrHandoffWhy(f, fn, sess, L"Other.sims3", sid, now, 15), "another save"));
    assert(!strcmp(WrHandoffWhy(f, fn, sess, NULL, sid, now, 15), "another save"));
    WrSaveId d = sid;
    d.bytes++;
    assert(!strcmp(WrHandoffWhy(f, fn, sess, L"My Save.sims3", d, now, 15), "the save changed since"));
    d = sid, d.files--;
    assert(!strcmp(WrHandoffWhy(f, fn, sess, L"My Save.sims3", d, now, 15), "the save changed since"));
    d = sid, d.newestFt += 10000000;
    assert(!strcmp(WrHandoffWhy(f, fn, sess, L"My Save.sims3", d, now, 15), "the save changed since"));
    assert(!strcmp(WrHandoffWhy(f, fn, sess, L"My Save.sims3", sid, now + 15 * 600000000ull, 15), "too old"));
    assert(!WrHandoffWhy(f, fn, sess, L"My Save.sims3", sid, now + 14 * 600000000ull, 15));
    assert(!strcmp(WrHandoffWhy(f, fn, sess, L"My Save.sims3", sid, now - 700000000ull, 15), "too old"));  // written "in the future"
    assert(!strcmp(WrHandoffWhy(f, fn - 1, sess, L"My Save.sims3", sid, now, 15), "truncated or too long"));
    assert(!strcmp(WrHandoffWhy(f, sizeof(WrHandoff) - 1, sess, L"My Save.sims3", sid, now, 15), "truncated or too long"));
    assert(!strcmp(WrHandoffWhy(f, 4, sess, L"My Save.sims3", sid, now, 15), "too short") && !strcmp(WrHandoffWhy(NULL, 0, sess, L"x", sid, now, 15), "too short"));
    f[fn] = 'x';
    assert(!strcmp(WrHandoffWhy(f, fn + 1, sess, L"My Save.sims3", sid, now, 15), "truncated or too long"));
    f[sizeof(WrHandoff) + 3] ^= 1;
    assert(!strcmp(WrHandoffWhy(f, fn, sess, L"My Save.sims3", sid, now, 15), "checksum differs"));
    f[sizeof(WrHandoff) + 3] ^= 1;
    ((WrHandoff*)f)->schema = kWrHoSchema + 1;
    assert(!strcmp(WrHandoffWhy(f, fn, sess, L"My Save.sims3", sid, now, 15), "another schema"));
    ((WrHandoff*)f)->schema = kWrHoSchema, ((WrHandoff*)f)->magic = 0;
    assert(!strcmp(WrHandoffWhy(f, fn, sess, L"My Save.sims3", sid, now, 15), "not a handoff file"));
    ((WrHandoff*)f)->magic = kWrHoMagic;
    assert(!WrHandoffWhy(f, fn, sess, L"My Save.sims3", sid, now, 15));
    wchar_t longName[140];
    wmemset(longName, L'a', 139), longName[139] = 0;
    assert(!WrHandoffBuild(f, sizeof(f), sess, now, longName, sid, 0, 0, rec, recN));          // the name does not fit the header
    assert(!WrHandoffBuild(f, sizeof(WrHandoff) + 3, sess, now, L"a.sims3", sid, 0, 0, rec, recN));  // nor the state the buffer

    // --- the bits ts3fixes reads: payload bytes, name bytes, the two lengths
    const u8 pay[] = {0xA5, 0x01};
    const char nm[] = "\xC3\xA9.sims3";  // UTF-8 e-acute
    DWORD v = 0;
    for (DWORD b = 0; b < 8; b++) v |= (DWORD)WrDataBit(0x10 | b << 8, pay, 2, nm, 8) << b;
    assert(v == 0xA5);
    v = 0;
    for (DWORD b = 0; b < 8; b++) v |= (DWORD)WrDataBit(0x11 | b << 8 | 1u << 11, pay, 2, nm, 8) << b;
    assert(v == 0xA9);
    DWORD l0 = 0, l1 = 0;
    for (DWORD b = 0; b < 32; b++) l0 |= (DWORD)WrDataBit(0x12 | b << 8, pay, 2, nm, 8) << b, l1 |= (DWORD)WrDataBit(0x12 | b << 8 | 1u << 13, pay, 2, nm, 8) << b;
    assert(l0 == 2 && l1 == 8);
    assert(!WrDataBit(0x10 | 0 << 8 | 2u << 11, pay, 2, nm, 8) && !WrDataBit(0x13, pay, 2, nm, 8) && !WrDataBit(0x10, NULL, 0, nm, 8));

    // --- the verdict: compaction's rules on our threshold, no minimum minutes; the hotkey's save; failed saves never
    const DWORD H = 3600000;
    CpState s;
    assert(!WrVerdict(s, 320, false, 100, H, false, true) && !WrVerdict(s, 320, false, 100, H, false, false));
    assert(WrVerdict(s, 320, false, 900, H, true, true) == 2 && s.verdictMB == 900);  // hotkey: any free block
    assert(!WrVerdict(s, 320, false, 320, H, true, false) && s.armed);
    assert(WrVerdict(s, 320, false, 319, H, true, false) == 1 && !s.armed && s.verdictMB == 319);
    assert(!WrVerdict(s, 320, false, 100, H + 1, true, false));                             // arm used up
    assert(!WrVerdict(s, 320, false, 576, H + 2, true, false) && s.armed);                  // back above 320 + 256
    assert(WrVerdict(s, 320, false, 300, H + 3, true, false) == 1);                          // no 60-minute wait
    CpState a;
    assert(WrVerdict(a, 200, true, 250, H, true, false) == 1);                               // Apex MemoryGuard: at least 320
    CpState f4;
    assert(WrVerdict(f4, 4000, false, 1500, H, true, false) == 1);                           // the forced test threshold

    // --- ops: everything off answers false; the record buffer refuses an overflow
    const bool r0 = g_wrRestart, b0 = g_wrBoot, s0 = g_wrRestore, k0 = g_wrHotkeyOn;
    WrChan* c0 = g_wrChan;
    g_wrRestart = g_wrBoot = g_wrRestore = false, g_wrChan = NULL;
    for (DWORD op = 0; op <= 0x12; op++) assert(!WrOp(op));
    g_wrRecN = 0, g_wrRecBad = false;
    static char big[480];
    memset(big, 'x', sizeof(big) - 1);
    for (int i = 0; i < 200; i++) WrRecLine(big);
    assert(g_wrRecBad && g_wrRecN <= sizeof(g_wrRec));
    g_wrRecN = 0, g_wrRecBad = false;
    WrRecLine("R1|0");
    assert(g_wrRecN == 5 && !memcmp(g_wrRec, "R1|0\n", 5));
    g_wrRecN = 0;
    g_wrRestart = r0, g_wrBoot = b0, g_wrRestore = s0, g_wrHotkeyOn = k0, g_wrChan = c0;

    // --- the host's state machine
    {  // a whole restart
        Hs h;
        const HsRow rows[] = {
            {HE_TICK, 0, HS_IDLE, 0},
            {HE_ATTACH, 10, HS_RUNNING, 0},
            {HE_TICK, 999999, HS_RUNNING, 0},
            {HE_READY, 1000, HS_RUNNING, 0},  // not ours here: nothing
            {HE_REQUEST, 1000, HS_RESTART_REQUESTED, HA_COVER},
            {HE_REQUEST, 1001, HS_RESTART_REQUESTED, 0},
            {HE_COVERED, 1002, HS_WAITING_FOR_EXIT, HA_SIGNAL},
            {HE_TICK, 1002 + 59999, HS_WAITING_FOR_EXIT, 0},
            {HE_EXIT_OK, 30000, HS_RELAUNCHING, HA_LAUNCH},
            {HE_LAUNCHED, 30010, HS_WAITING_FOR_WINDOW, 0},
            {HE_TICK, 30010 + 119999, HS_WAITING_FOR_WINDOW, 0},
            {HE_WINDOW, 40000, HS_LOADING, 0},
            {HE_TICK, 40000 + 599999, HS_LOADING, 0},
            {HE_READY, 200000, HS_RUNNING, HA_UNCOVER},
            {HE_CLOSE, 300000, HS_RUNNING, HA_POST_CLOSE},  // the player's close: the game asks its own question
            {HE_EXIT_OK, 310000, HS_EXITED, HA_QUIT},
            {HE_REQUEST, 310001, HS_EXITED, 0},
        };
        HsRun(h, rows, _countof(rows), "restart");
        assert(h.restarts == 1);
    }
    {  // a crash in the quit still relaunches (the save ended with result 0); a launch failure ends with a message
        Hs h;
        const HsRow rows[] = {
            {HE_ATTACH, 0, HS_RUNNING, 0}, {HE_REQUEST, 5, HS_RESTART_REQUESTED, HA_COVER}, {HE_COVERED, 5, HS_WAITING_FOR_EXIT, HA_SIGNAL},
            {HE_EXIT_CRASH, 9, HS_RELAUNCHING, HA_LAUNCH}, {HE_LAUNCH_FAIL, 9, HS_FAILED, HA_MESSAGE | HA_QUIT},
            {HE_TICK, 999999, HS_FAILED, 0},
        };
        HsRun(h, rows, _countof(rows), "crash+launch fail");
        assert(h.msg == HM_LAUNCH_FAIL);
    }
    {  // stuck in its quit: the restart is given up after quitMs, the game is not killed
        Hs h;
        h.quitMs = 1000;
        const HsRow rows[] = {
            {HE_ATTACH, 0, HS_RUNNING, 0}, {HE_REQUEST, 0, HS_RESTART_REQUESTED, HA_COVER},
            {HE_TICK, 5000, HS_RESTART_REQUESTED, 0},  // no timeout before the cover is up
            {HE_COVERED, 5000, HS_WAITING_FOR_EXIT, HA_SIGNAL}, {HE_TICK, 5999, HS_WAITING_FOR_EXIT, 0},
            {HE_TICK, 6000, HS_RUNNING, HA_MESSAGE}, {HE_EXIT_OK, 9000, HS_EXITED, HA_QUIT},
        };
        HsRun(h, rows, _countof(rows), "quit stuck");
        assert(h.msg == HM_QUIT_STUCK);
    }
    {  // the player closes the host during the quit: no relaunch
        Hs h;
        const HsRow rows[] = {
            {HE_ATTACH, 0, HS_RUNNING, 0}, {HE_REQUEST, 1, HS_RESTART_REQUESTED, HA_COVER}, {HE_CLOSE, 2, HS_RESTART_REQUESTED, 0},
            {HE_COVERED, 3, HS_WAITING_FOR_EXIT, HA_SIGNAL}, {HE_EXIT_CRASH, 4, HS_CRASHED, HA_QUIT},
        };
        HsRun(h, rows, _countof(rows), "close in quit");
    }
    {  // ... while the new game starts: its window is shown and asked to close
        Hs h;
        const HsRow rows[] = {
            {HE_ATTACH, 0, HS_RUNNING, 0}, {HE_REQUEST, 1, HS_RESTART_REQUESTED, HA_COVER}, {HE_COVERED, 1, HS_WAITING_FOR_EXIT, HA_SIGNAL},
            {HE_EXIT_OK, 2, HS_RELAUNCHING, HA_LAUNCH}, {HE_CLOSE, 2, HS_RELAUNCHING, 0}, {HE_LAUNCHED, 3, HS_WAITING_FOR_WINDOW, 0},
            {HE_CLOSE, 4, HS_WAITING_FOR_WINDOW, 0}, {HE_WINDOW, 5, HS_RUNNING, HA_UNCOVER | HA_POST_CLOSE},
        };
        HsRun(h, rows, _countof(rows), "close in start");
    }
    {  // ... while it loads
        Hs h;
        const HsRow rows[] = {
            {HE_ATTACH, 0, HS_RUNNING, 0}, {HE_REQUEST, 1, HS_RESTART_REQUESTED, HA_COVER}, {HE_COVERED, 1, HS_WAITING_FOR_EXIT, HA_SIGNAL},
            {HE_EXIT_OK, 2, HS_RELAUNCHING, HA_LAUNCH}, {HE_LAUNCHED, 3, HS_WAITING_FOR_WINDOW, 0}, {HE_WINDOW, 4, HS_LOADING, 0},
            {HE_CLOSE, 5, HS_RUNNING, HA_UNCOVER | HA_POST_CLOSE},
        };
        HsRun(h, rows, _countof(rows), "close in load");
    }
    {  // the new game: dies before its window / no window in time / dies while loading / falls back / never says ready
        const HsRow pre[] = {{HE_ATTACH, 0, HS_RUNNING, 0}, {HE_REQUEST, 1, HS_RESTART_REQUESTED, HA_COVER}, {HE_COVERED, 1, HS_WAITING_FOR_EXIT, HA_SIGNAL},
                             {HE_EXIT_OK, 2, HS_RELAUNCHING, HA_LAUNCH}, {HE_LAUNCHED, 100, HS_WAITING_FOR_WINDOW, 0}};
        Hs h1, h2, h3, h4, h5;
        HsRun(h1, pre, _countof(pre), "pre1");
        const HsRow r1[] = {{HE_EXIT_CRASH, 200, HS_FAILED, HA_MESSAGE | HA_QUIT}};
        HsRun(h1, r1, 1, "dies before its window");
        assert(h1.msg == HM_START_FAIL);
        HsRun(h2, pre, _countof(pre), "pre2");
        const HsRow r2[] = {{HE_TICK, 100 + 119999, HS_WAITING_FOR_WINDOW, 0}, {HE_TICK, 100 + 120000, HS_FAILED, HA_MESSAGE | HA_QUIT}};
        HsRun(h2, r2, 2, "no window");
        assert(h2.msg == HM_NO_WINDOW);
        HsRun(h3, pre, _countof(pre), "pre3");
        const HsRow r3[] = {{HE_WINDOW, 300, HS_LOADING, 0}, {HE_EXIT_CRASH, 400, HS_CRASHED, HA_MESSAGE | HA_QUIT}};
        HsRun(h3, r3, 2, "dies loading");
        HsRun(h4, pre, _countof(pre), "pre4");
        const HsRow r4[] = {{HE_WINDOW, 300, HS_LOADING, 0}, {HE_GAME_FAIL, 400, HS_RUNNING, HA_MESSAGE}};
        HsRun(h4, r4, 2, "boot fallback");
        assert(h4.msg == HM_BOOT_FALLBACK && h4.restarts == 0);
        HsRun(h5, pre, _countof(pre), "pre5");
        const HsRow r5[] = {{HE_WINDOW, 300, HS_LOADING, 0}, {HE_TICK, 300 + 599999, HS_LOADING, 0}, {HE_TICK, 300 + 600000, HS_RUNNING, HA_MESSAGE}};
        HsRun(h5, r5, 3, "ready timeout");
        assert(h5.msg == HM_READY_TIMEOUT);
    }
    {  // the game gives up before its quit; a plain crash; tick counter wrap
        Hs h;
        const HsRow rows[] = {
            {HE_ATTACH, 0xFFFFFF00u, HS_RUNNING, 0}, {HE_REQUEST, 0xFFFFFF00u, HS_RESTART_REQUESTED, HA_COVER},
            {HE_COVERED, 0xFFFFFF10u, HS_WAITING_FOR_EXIT, HA_SIGNAL}, {HE_TICK, 100, HS_WAITING_FOR_EXIT, 0},  // 340 ms later, across the wrap
            {HE_GAME_FAIL, 200, HS_RUNNING, HA_UNCOVER}, {HE_EXIT_CRASH, 300, HS_CRASHED, HA_QUIT},
        };
        HsRun(h, rows, _countof(rows), "give up + crash");
    }
    printf("selftest restart: channel, handoff file, verdict, bit reads, host state machine ok\n");
}
