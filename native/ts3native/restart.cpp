// [Wrapper] Channel / Restart (docs/530 §12, step 1): the game side of the host-owned lifecycle. Included before wrapper.cpp
// (which starts the host and hosts the window); needs userdiag.cpp (text channel, save end, save folder) and compaction.cpp
// (its verdict rules and the largest-free reading). Off unless [Wrapper] Enabled=1 and Channel=1; Restart=1 also needs
// [UserDiag] Enabled=1 and [SaveWrite] Enabled=1 (the save result), like [Compaction].
//   Channel: the shared block of ../ts3wrap/restartchan.h. The first game creates it when it starts the host and fills in its
//     exe, command line and the timeouts; a game the host started again opens it from TS3WRAP_CHANNEL. The game window's
//     messages write the heartbeat (wrapper.cpp WrProc), DLL_PROCESS_DETACH writes bye=1 (the host tells an exit from a crash).
//   Restart: ts3fixes (Restart.cs) asks over the text channel, op 40 (optLo low byte):
//      0 on?                       1 save task begins (a verdict counts only a save that ended after this)
//      2 verdict after the save: restart now? (compaction's rules on RestartThresholdMB, or a pending hotkey save)
//      3 ask the player?           4 the player chose Not now
//      5 record begins: the state ts3fixes captured follows as "wraprec <line>" text lines (CompactionCore's record)
//      6 commit: handoff file written, the host told and its cover confirmed (1.5 s at most): 1 = quit now
//      7 failed before the quit (reason in bits 8-15): off for the session, the host drops its cover
//      8 restart boot? (the host started this process for a restart)   14 restore wanted? (the handoff file passed every check)
//      9 restore done (what was put back in bits 8-15)   10 boot fell back (reason in bits 8-15): both drop the host's cover
//     11 hotkey pressed since the last ask (consumed)    12 hotkey on?    13 the next verdict is a yes (the hotkey's save)
//      0x10 | bit << 8 | index << 11: a payload byte's bit; 0x11 the same of the save name (UTF-8); 0x12 | bit << 8 | which << 13:
//      a length bit (which 0 payload, 1 name)
//   The handoff file (<Documents>\Electronic Arts\The Sims 3\ts3native\restart.bin, never in the save): a WrHandoff header
//   (schema, session id, the save's folder name, size, file count and newest write time, write time, payload length and
//   FNV-1a) and the record text. Checked at the next start before anything is used; deleted after use, and at a normal start.
#include "../ts3wrap/restartchan.h"

static bool g_wrChanOn;        // [Wrapper] Channel=1
static bool g_wrRestart;       // Restart=1 and all it needs
static bool g_wrAsk, g_wrHotkeyOn;
static bool g_wrOff;           // off for this session (a failure, or a restart that did not help)
static DWORD g_wrOnMB = 320, g_wrQuitS = 60, g_wrCoverS = 600;
static int g_wrFrame;
static const DWORD kWrRearmMB = 256, kWrMaxAgeMin = 15;
static WrChan* g_wrChan;
static HANDLE g_wrEvHost, g_wrEvGame;
static wchar_t g_wrId[33];     // the session id, 32 hex digits
static DWORD g_wrFirstPid;
static wchar_t g_wrCwd[MAX_PATH], g_wrExe[MAX_PATH];
static HWND g_wrChanHost;      // a relaunched game: the host window from the channel
static CpState g_wrCp;         // the verdict state: compaction's rules (CpVerdict), our threshold
static volatile LONG g_wrKey;  // hotkey presses (wrapper.cpp WrProc)
static DWORD g_wrForceTick, g_wrAskTick;
static int g_wrReason, g_wrCount;
static char g_wrRec[65536];
static DWORD g_wrRecN;
static bool g_wrRecBad, g_wrSignalled;
static bool g_wrBoot, g_wrRestore;  // this process: a restart boot / with a handoff file that passed every check
static u8* g_wrFile;                // the handoff file read at the boot (header + payload)
static DWORD g_wrFileN;
static char g_wrName8[512];         // the save's folder name as UTF-8 (ops 0x11 / 0x12)
static DWORD g_wrName8N;

static const DWORD kWrHoMagic = 0x52573354;  // "T3WR"
static const WORD kWrHoSchema = 1;
struct WrHandoff {
    DWORD magic;
    WORD schema, headerSize;
    u8 session[16];
    u64 writtenFt;  // UTC FILETIME of the write
    u64 saveBytes, saveNewestFt;
    DWORD saveFiles;
    DWORD payloadLen, payloadSum;
    DWORD bigBeforeMB, reason;
    wchar_t save[128];  // "<name>.sims3"
};
struct WrSaveId { u64 bytes = 0, newestFt = 0; DWORD files = 0; };

// op 7 / 10 reasons (ts3fixes Restart.cs kWrWhy* / kWrBoot*)
static const char* const kWrWhy[] = {"?", "state refused after the offer", "no save file name", "the quit threw", "the record did not go through"};
static const char* const kWrBootWhy[] = {"?", "the main menu came up without a load", "another save was loaded", "the record did not parse"};

// ---------------------------------------------------------------------------------------------------------------------
// pure helpers (selftest)

static DWORD WrFnv(const u8* p, size_t n) {
    DWORD h = 2166136261u;
    for (size_t i = 0; i < n; i++) h = (h ^ p[i]) * 16777619u;
    return h;
}

// 32 hex digits -> 16 bytes
static bool WrIdBytes(const wchar_t* id, u8* out) {
    if (!id || wcslen(id) != 32) return false;
    for (int i = 0; i < 16; i++) {
        unsigned v = 0;
        for (int k = 0; k < 2; k++) {
            const wchar_t c = id[i * 2 + k];
            const int d = c >= L'0' && c <= L'9' ? c - L'0' : c >= L'a' && c <= L'f' ? c - L'a' + 10 : c >= L'A' && c <= L'F' ? c - L'A' + 10 : -1;
            if (d < 0) return false;
            v = v << 4 | (unsigned)d;
        }
        out[i] = (u8)v;
    }
    return true;
}

// The whole file: header + payload. 0 when it does not fit cap or the name does not fit the header.
static size_t WrHandoffBuild(u8* out, size_t cap, const u8* session, u64 nowFt, const wchar_t* save, const WrSaveId& id, DWORD bigMB,
                             DWORD reason, const char* payload, size_t payloadN) {
    if (cap < sizeof(WrHandoff) || payloadN > cap - sizeof(WrHandoff) || wcslen(save) >= 128) return 0;
    WrHandoff h = {};
    h.magic = kWrHoMagic, h.schema = kWrHoSchema, h.headerSize = (WORD)sizeof(WrHandoff);
    memcpy(h.session, session, 16);
    h.writtenFt = nowFt, h.saveBytes = id.bytes, h.saveNewestFt = id.newestFt, h.saveFiles = id.files;
    h.payloadLen = (DWORD)payloadN, h.payloadSum = WrFnv((const u8*)payload, payloadN);
    h.bigBeforeMB = bigMB, h.reason = reason;
    wcscpy_s(h.save, save);
    memcpy(out, &h, sizeof(h));
    memcpy(out + sizeof(h), payload, payloadN);
    return sizeof(h) + payloadN;
}

// NULL = the file is whole, ours, for this session, for the save `save` as it is on disk now (`disk`) and at most maxAgeMin
// old; else why not.
static const char* WrHandoffWhy(const u8* f, size_t n, const u8* session, const wchar_t* save, const WrSaveId& disk, u64 nowFt, DWORD maxAgeMin) {
    if (!f || n < 8) return "too short";
    WrHandoff h;
    memcpy(&h, f, n < sizeof(h) ? n : sizeof(h));
    if (h.magic != kWrHoMagic) return "not a handoff file";
    if (h.schema != kWrHoSchema || h.headerSize != sizeof(WrHandoff)) return "another schema";
    if (n < sizeof(WrHandoff) || n - sizeof(WrHandoff) != h.payloadLen) return "truncated or too long";
    if (WrFnv(f + sizeof(WrHandoff), h.payloadLen) != h.payloadSum) return "checksum differs";
    if (memcmp(h.session, session, 16)) return "another session";
    h.save[127] = 0;
    if (!save || _wcsicmp(h.save, save)) return "another save";
    if (disk.bytes != h.saveBytes || disk.files != h.saveFiles || disk.newestFt != h.saveNewestFt) return "the save changed since";
    if (nowFt < h.writtenFt || nowFt - h.writtenFt > (u64)maxAgeMin * 600000000ull) return "too old";
    return NULL;
}

// The verdict: 0 no, 1 low memory (compaction's rules: threshold, re-arm +256 MB, no minimum minutes), 2 the hotkey's save.
static int WrVerdict(CpState& s, DWORD onMB, bool apexGuard, DWORD bigMB, DWORD now, bool saveOk, bool forced) {
    if (!saveOk) return 0;
    if (forced) { s.verdictMB = bigMB; return 2; }
    return CpVerdict(s, 1, onMB, kWrRearmMB, 0, apexGuard, bigMB, now, true);
}

// One bit of a byte or a length for ops 0x10-0x12 (out of range: 0).
static u8 WrDataBit(DWORD lo, const u8* payload, DWORD payloadN, const char* name, DWORD nameN) {
    const DWORD cmd = lo & 0xFF;
    if (cmd == 0x12) {
        const DWORD bit = (lo >> 8) & 31, v = (lo >> 13) & 1 ? nameN : payloadN;
        return (u8)(v >> bit & 1);
    }
    const DWORD bit = (lo >> 8) & 7, i = lo >> 11;
    if (cmd == 0x10) return i < payloadN ? (u8)(payload[i] >> bit & 1) : 0;
    if (cmd == 0x11) return i < nameN ? (u8)((u8)name[i] >> bit & 1) : 0;
    return 0;
}

// ---------------------------------------------------------------------------------------------------------------------
// files

static u64 WrNowFt() {
    FILETIME ft;
    GetSystemTimeAsFileTime(&ft);
    return (u64)ft.dwHighDateTime << 32 | ft.dwLowDateTime;
}

static bool WrHandoffPath(wchar_t* out) {  // played.cpp's folder
    wchar_t base[MAX_PATH], dir[MAX_PATH];
    UserSims3Dir(base);
    if (_snwprintf_s(dir, MAX_PATH, _TRUNCATE, L"%s\\ts3native", base) < 0) return false;  // _TRUNCATE: a long path fails, never aborts
    CreateDirectoryW(dir, NULL);
    return _snwprintf_s(out, MAX_PATH, _TRUNCATE, L"%s\\restart.bin", dir) > 0;
}

static bool WrSaveIdOf(const wchar_t* dir, WrSaveId& id) {  // one level: a .sims3 folder holds files
    wchar_t pat[MAX_PATH];
    if (_snwprintf_s(pat, MAX_PATH, _TRUNCATE, L"%s\\*", dir) < 0) return false;
    WIN32_FIND_DATAW fd;
    HANDLE h = FindFirstFileW(pat, &fd);
    if (h == INVALID_HANDLE_VALUE) return false;
    id = WrSaveId();
    do {
        if (!wcscmp(fd.cFileName, L".") || !wcscmp(fd.cFileName, L"..")) continue;
        id.files++;
        if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) continue;
        id.bytes += (u64)fd.nFileSizeHigh << 32 | fd.nFileSizeLow;
        const u64 t = (u64)fd.ftLastWriteTime.dwHighDateTime << 32 | fd.ftLastWriteTime.dwLowDateTime;
        if (t > id.newestFt) id.newestFt = t;
    } while (FindNextFileW(h, &fd));
    FindClose(h);
    return id.files > 0;
}

static void WrDeleteHandoff(const char* why) {
    wchar_t p[MAX_PATH];
    if (WrHandoffPath(p) && DeleteFileW(p)) Log("wrapper: handoff file deleted (%s)", why);
}

// ---------------------------------------------------------------------------------------------------------------------
// channel

static void WrSignalHost(int msg, int why) {
    if (!g_wrChan) return;
    g_wrChan->gameWhy = why;
    InterlockedExchange((volatile LONG*)&g_wrChan->gameMsg, msg);
    InterlockedIncrement((volatile LONG*)&g_wrChan->msgSeq);
    if (g_wrEvHost) SetEvent(g_wrEvHost);
}

static bool WrOpenEvents() {
    wchar_t n[96];
    if (WrChanName(n, 96, g_wrFirstPid, g_wrId, L"-h")) g_wrEvHost = CreateEventW(NULL, FALSE, FALSE, n);
    if (WrChanName(n, 96, g_wrFirstPid, g_wrId, L"-g")) g_wrEvGame = CreateEventW(NULL, FALSE, FALSE, n);
    return g_wrEvHost && g_wrEvGame;
}

// The first game, just before it starts the host (wrapper.cpp): the block, filled in. False: no channel, the host runs as in step 0.
static bool WrChanCreate() {
    if (!g_wrChanOn || g_wrChan) return g_wrChan != NULL;
    // 128-bit session id from the clock, counters and the process: unique per session (names only), not a secret
    LARGE_INTEGER q;
    QueryPerformanceCounter(&q);
    u64 seed[4] = {WrNowFt(), (u64)q.QuadPart, GetTickCount64() ^ ((u64)GetCurrentProcessId() << 32), (u64)(uintptr_t)&q};
    u64 a = 14695981039346656037ull, b = 1099511628211ull ^ seed[1];
    for (int i = 0; i < 32; i++) {
        const u8 c = ((const u8*)seed)[i];
        a = (a ^ c) * 1099511628211ull, b = (b ^ c ^ (u8)i) * 14695981039346656037ull + 0x9E3779B97F4A7C15ull;
    }
    swprintf_s(g_wrId, L"%016llx%016llx", a, b);
    g_wrFirstPid = GetCurrentProcessId();
    wchar_t n[96];
    if (!WrChanName(n, 96, g_wrFirstPid, g_wrId, NULL)) return false;
    HANDLE m = CreateFileMappingW(INVALID_HANDLE_VALUE, NULL, PAGE_READWRITE, 0, sizeof(WrChan), n);
    WrChan* c = m ? (WrChan*)MapViewOfFile(m, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(WrChan)) : NULL;
    if (!c || !WrOpenEvents()) {
        Log("FAIL wrapper: channel not created (err %lu), the host runs without it (no restart)", GetLastError());
        if (c) UnmapViewOfFile(c);
        if (m) CloseHandle(m);
        return false;
    }
    memset(c, 0, sizeof(*c));
    c->magic = kWrChanMagic, c->version = kWrChanVer, c->size = (uint16_t)sizeof(WrChan);
    c->firstPid = g_wrFirstPid;
    c->gamePid = (int32_t)g_wrFirstPid;
    c->quitMs = g_wrQuitS * 1000, c->windowMs = 120000, c->readyMs = g_wrCoverS * 1000, c->frame = g_wrFrame;
    const wchar_t* cmd = GetCommandLineW();
    if (wcslen(g_wrExe) < _countof(c->exe) && wcslen(cmd) < _countof(c->cmd)) wcscpy_s(c->exe, g_wrExe), wcscpy_s(c->cmd, cmd);
    else if (g_wrRestart) g_wrRestart = false, Log("wrapper: Restart off: the game's command line is over %u characters", (unsigned)_countof(c->cmd) - 1);
    g_wrChan = c;  // the handle stays open for the session (the host opens its own)
    Log("OK   wrapper: channel v%u created (%u bytes, session %.8ls...)", kWrChanVer, (unsigned)sizeof(WrChan), g_wrId);
    return true;
}

// A game the host started (TS3WRAP_CHANNEL=<pid>-<id>): open the block, read the host's word (DllMain: kernel32 only).
static void WrChanOpen(const wchar_t* v) {
    wchar_t* end = NULL;
    g_wrFirstPid = wcstoul(v, &end, 10);
    if (!end || *end != L'-' || wcslen(end + 1) != 32) { Log("wrapper: %ls is not <pid>-<id>, ignored", kWrChanEnv); return; }
    wcscpy_s(g_wrId, end + 1);
    wchar_t n[96];
    HANDLE m = WrChanName(n, 96, g_wrFirstPid, g_wrId, NULL) ? OpenFileMappingW(FILE_MAP_ALL_ACCESS, FALSE, n) : NULL;
    WrChan* c = m ? (WrChan*)MapViewOfFile(m, FILE_MAP_ALL_ACCESS, 0, 0, 0) : NULL;
    MEMORY_BASIC_INFORMATION mbi = {};
    if (c) VirtualQuery(c, &mbi, sizeof(mbi));
    if (!c || !WrChanOk(c, mbi.RegionSize)) {
        Log("wrapper: channel of the host %s, the game runs unwrapped", !c ? "not found" : "has another version or size");
        if (c) UnmapViewOfFile(c);
        if (m) CloseHandle(m);
        return;
    }
    if (!WrOpenEvents()) { Log("wrapper: channel events missing (err %lu), the game runs unwrapped", GetLastError()); UnmapViewOfFile(c); CloseHandle(m); return; }
    g_wrChan = c;
    g_wrChanHost = (HWND)(uintptr_t)c->hostWnd;
    InterlockedExchange((volatile LONG*)&c->bye, 0);
    InterlockedExchange((volatile LONG*)&c->gamePid, (LONG)GetCurrentProcessId());
    g_wrBoot = InterlockedExchange((volatile LONG*)&c->hostCmd, WR_H_NONE) == WR_H_RESTART_BOOT;
    Log("OK   wrapper: started by the host (first game pid %lu, %ld restarts of this host before)%s", g_wrFirstPid, (long)c->restarts,
        g_wrBoot ? ": restart boot" : "");
}

// Restart boot: the handoff file, checked against the channel's save as it is on disk now.
static void WrBootCheck() {
    wchar_t p[MAX_PATH], base[MAX_PATH], dir[MAX_PATH], save[128];
    memcpy(save, g_wrChan->save, sizeof(save));
    save[127] = 0;
    const char* why = NULL;
    u8 sess[16];
    WrSaveId disk;
    UserSims3Dir(base);
    HANDLE h = WrHandoffPath(p) ? CreateFileW(p, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, 0, NULL) : INVALID_HANDLE_VALUE;
    const DWORD size = h != INVALID_HANDLE_VALUE ? GetFileSize(h, NULL) : 0;
    if (h == INVALID_HANDLE_VALUE) why = "no handoff file";
    else if (size == INVALID_FILE_SIZE || size > sizeof(WrHandoff) + sizeof(g_wrRec)) why = "handoff file too large";
    else if (!(g_wrFile = (u8*)HeapAlloc(GetProcessHeap(), 0, size ? size : 1))) why = "no memory";
    else if (!ReadFile(h, g_wrFile, size, &g_wrFileN, NULL) || g_wrFileN != size) why = "handoff file unreadable";
    if (h != INVALID_HANDLE_VALUE) CloseHandle(h);
    if (!why && !WrIdBytes(g_wrId, sess)) why = "bad session id";
    if (!why && (_snwprintf_s(dir, MAX_PATH, _TRUNCATE, L"%s\\Saves\\%s", base, save) < 0 || !WrSaveIdOf(dir, disk))) why = "the save folder is not there";
    if (!why) why = WrHandoffWhy(g_wrFile, g_wrFileN, sess, save, disk, WrNowFt(), kWrMaxAgeMin);
    if (why) {
        Log("wrapper: restart boot: the handoff file is not used (%s); the save loads without a restore", why);
        if (g_wrFile) HeapFree(GetProcessHeap(), 0, g_wrFile), g_wrFile = NULL, g_wrFileN = 0;
        WrDeleteHandoff("not used");
        return;
    }
    const int n = WideCharToMultiByte(CP_UTF8, 0, save, -1, g_wrName8, sizeof(g_wrName8), NULL, NULL);
    g_wrName8N = n > 0 ? (DWORD)n - 1 : 0;
    g_wrRestore = g_wrName8N > 0;
    const WrHandoff* hd = (const WrHandoff*)g_wrFile;
    Log("wrapper: restart boot: handoff file ok (%lu bytes of state, written %llu s ago, largest free before %lu MB)", hd->payloadLen,
        (WrNowFt() - hd->writtenFt) / 10000000ull, hd->bigBeforeMB);
}

// ---------------------------------------------------------------------------------------------------------------------
// the restart, game side

static void WrFail(const char* step, const char* why) {
    g_wrOff = true;
    Log("wrapper: restart %s: %s; Restart off for this session, the game plays on", step, why);
    if (g_wrSignalled) WrSignalHost(WR_G_FAILED, 1), g_wrSignalled = false;
    WrDeleteHandoff("restart given up");
}

static void WrRecLine(const char* s) {  // "wraprec <line>" (userdiag.cpp)
    const size_t n = strlen(s);
    if (g_wrRecN + n + 1 > sizeof(g_wrRec)) { g_wrRecBad = true; return; }
    memcpy(g_wrRec + g_wrRecN, s, n);
    g_wrRec[g_wrRecN + n] = '\n';
    g_wrRecN += (DWORD)n + 1;
}

static u8 WrCommit() {
    const wchar_t* name = wcsrchr(g_udSaveDir, L'\\');
    name = name ? name + 1 : NULL;
    WrSaveId id;
    u8 sess[16];
    wchar_t p[MAX_PATH], tmp[MAX_PATH];
    if (!g_wrChan || !IsWindow((HWND)(uintptr_t)g_wrChan->hostWnd)) { WrFail("not started", "the host window is gone"); return 0; }
    if (g_wrRecBad || !g_wrRecN) { WrFail("not started", g_wrRecBad ? "the captured state is over 64 KB" : "no captured state"); return 0; }
    if (!name || !EndsWithI(name, L".sims3") || wcslen(name) >= _countof(g_wrChan->save)) { WrFail("not started", "the save's folder is not known"); return 0; }
    FlushWait();  // savewrite.cpp / saveslim.cpp: the save on disk and done before it is named and the game quits
    SlimWait();
    if (!WrSaveIdOf(g_udSaveDir, id)) { WrFail("not started", "the save's folder cannot be read"); return 0; }
    if (!WrIdBytes(g_wrId, sess) || !WrHandoffPath(p) || _snwprintf_s(tmp, MAX_PATH, _TRUNCATE, L"%s.tmp", p) < 0) { WrFail("not started", "no handoff path"); return 0; }
    static u8 file[sizeof(WrHandoff) + sizeof(g_wrRec)];
    const size_t n = WrHandoffBuild(file, sizeof(file), sess, WrNowFt(), name, id, g_wrCp.verdictMB, (DWORD)g_wrReason, g_wrRec, g_wrRecN);
    HANDLE h = n ? CreateFileW(tmp, GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL) : INVALID_HANDLE_VALUE;
    DWORD w = 0;
    const bool ok = h != INVALID_HANDLE_VALUE && WriteFile(h, file, (DWORD)n, &w, NULL) && w == n && FlushFileBuffers(h);
    if (h != INVALID_HANDLE_VALUE) CloseHandle(h);
    if (!ok || !MoveFileExW(tmp, p, MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)) {
        DeleteFileW(tmp);
        WrFail("not started", "the handoff file was not written");
        return 0;
    }
    wcscpy_s(g_wrChan->save, name);
    g_wrChan->reason = g_wrReason, g_wrChan->bigBeforeMB = (int32_t)g_wrCp.verdictMB, g_wrChan->bigAfterMB = 0, g_wrChan->restored = 0;
    WaitForSingleObject(g_wrEvGame, 0);  // a stale "covered" from an earlier try
    const DWORD t0 = GetTickCount();
    WrSignalHost(WR_G_RESTART, 0);
    g_wrSignalled = true;
    if (WaitForSingleObject(g_wrEvGame, 1500) != WAIT_OBJECT_0) { WrFail("not started", "the host did not confirm its cover in 1.5 s"); return 0; }
    g_wrCount++;
    Log("wrapper: restart #%d (%s) after save #%ld: largest free %lu MB, %lu bytes of state in the handoff file, host covered in %lu ms; quitting",
        g_wrCount, g_wrReason == 2 ? "hotkey" : "low memory", g_cp.lastSaveN, g_wrCp.verdictMB, (DWORD)n, GetTickCount() - t0);
    return 1;
}

// Ready or fallen back at the boot: the host drops its cover; the summary line; the handoff file goes.
static void WrBootDone(bool ready, int what) {
    const DWORD big = CpBigMB();
    const WrHandoff* h = g_wrRestore && g_wrFile ? (const WrHandoff*)g_wrFile : NULL;  // checked at the boot (WrBootCheck)
    const DWORD before = h ? h->bigBeforeMB : (DWORD)g_wrChan->bigBeforeMB;
    char secs[48] = "(the time is in ts3wrap.log)";
    if (h) sprintf_s(secs, "%llu s from the request", (WrNowFt() - h->writtenFt) / 10000000ull);
    g_wrChan->bigAfterMB = (int32_t)big, g_wrChan->restored = ready ? what : 0;
    // a low-memory restart that did not get back to the threshold + 256 MB: off (a hotkey restart says nothing about that)
    const bool helped = g_wrChan->reason != 1 || CpHelped(big, g_wrOnMB, kWrRearmMB, g_apexFound && (g_apexOn & AXF_MEMORY_GUARD));
    if (!helped && g_wrRestart) g_wrOff = true;
    char rs[160];
    if (!ready) sprintf_s(rs, "nothing restored (%s)", kWrBootWhy[what > 0 && what < (int)_countof(kWrBootWhy) ? what : 0]);
    else sprintf_s(rs, "restored:%s%s%s%s%s%s", what & 1 ? " camera" : "", what & 2 ? " selected Sim" : "", what & 4 ? " queues" : "",
                   what & 8 ? " HUD panel" : "", what & 16 ? " speed" : "", what & 31 ? "" : " nothing");
    Log("wrapper: restart summary: largest free %lu MB before, %lu MB after (%+ld MB), %s to %s, %s%s", before, big,
        (LONG)big - (LONG)before, secs, ready ? "ready" : "the fallback", rs,
        helped ? "" : "; still under the threshold + 256 MB: Restart off for this session");
    WrSignalHost(ready ? WR_G_READY : WR_G_FAILED, ready ? 0 : what);
    g_wrBoot = g_wrRestore = false;
    if (g_wrFile) HeapFree(GetProcessHeap(), 0, g_wrFile), g_wrFile = NULL, g_wrFileN = 0;
    WrDeleteHandoff("used");
}

static void WrWorldLoaded() {  // userdiag.cpp UdWorldLoad: a restart boot whose handoff was not usable is ready at its first world
    if (g_wrBoot && !g_wrRestore && g_wrChan) WrBootDone(true, 0);
}

static u8 WrOp(DWORD lo) {
    const DWORD cmd = lo & 0xFF;
    if (cmd >= 0x10 && cmd <= 0x12)
        return g_wrRestore ? WrDataBit(lo, g_wrFile ? g_wrFile + sizeof(WrHandoff) : NULL, g_wrFile ? g_wrFileN - (DWORD)sizeof(WrHandoff) : 0, g_wrName8, g_wrName8N) : 0;
    switch (cmd) {
        case 0: return g_wrRestart && !g_wrOff && g_wrChan;
        case 8: return g_wrBoot && g_wrChan;
        case 14: return g_wrBoot && g_wrRestore;
        case 9:
        case 10:
            if (!g_wrBoot || !g_wrChan) return 0;
            WrBootDone(cmd == 9, (int)((lo >> 8) & 0xFF));
            return 1;
        case 12: return g_wrRestart && g_wrHotkeyOn;
    }
    if (!g_wrRestart || !g_wrChan) return 0;
    switch (cmd) {
        case 1: g_wrCp.saveSnap = g_udSaves; return 1;
        case 2: {
            if (g_wrOff) return 0;
            if (!IsWindow((HWND)(uintptr_t)g_wrChan->hostWnd)) {  // the host did not start or is gone: nothing to restart into
                Log("wrapper: verdict skipped after save #%ld: no host window", g_cp.lastSaveN);
                return 0;
            }
            const DWORD now = GetTickCount();
            CpState& s = g_wrCp;
            s.lastSaveN = g_cp.lastSaveN, s.lastSaveR = g_cp.lastSaveR, s.lastSaveEnd = g_cp.lastSaveEnd;
            const bool saveOk = CpSaveOk(s, now), forced = g_wrForceTick && now - g_wrForceTick < 120000;
            const bool apex = g_apexFound && (g_apexOn & AXF_MEMORY_GUARD);
            g_wrForceTick = 0;
            const DWORD big = CpBigMB();
            const int v = WrVerdict(s, g_wrOnMB, apex, big, now, saveOk, forced);
            g_wrReason = v;
            Log("wrapper: verdict %s after save #%ld (result %lu%s): largest free %lu MB, threshold %lu MB%s%s", v == 2 ? "RESTART (hotkey)" : v ? "RESTART" : "no",
                g_cp.lastSaveN, g_cp.lastSaveR, saveOk ? "" : ", not this task's save", big, CpThreshold(g_wrOnMB, apex), apex ? " (Apex MemoryGuard)" : "",
                v || s.armed ? "" : ", not armed (back above threshold + 256 MB first)");
            return v != 0;
        }
        case 3: return g_wrAsk;
        case 4: Log("wrapper: the player chose Not now (asked again only after a re-arm)"); return 1;
        case 5: g_wrRecN = 0, g_wrRecBad = false; return 1;
        case 6: return g_wrOff ? 0 : WrCommit();
        case 7: {
            const DWORD why = (lo >> 8) & 0xFF;
            WrFail("given up", kWrWhy[why < _countof(kWrWhy) ? why : 0]);
            return 1;
        }
        case 11: return g_wrHotkeyOn && InterlockedExchange(&g_wrKey, 0) > 0;
        case 13:
            g_wrForceTick = GetTickCount();
            Log("wrapper: hotkey: a save follows, then the restart");
            return 1;
    }
    return 0;
}

static void WrExit() {  // DLL_PROCESS_DETACH: the host reads it as a normal exit
    if (g_wrChan && (DWORD)g_wrChan->gamePid == GetCurrentProcessId()) InterlockedExchange((volatile LONG*)&g_wrChan->bye, 1);
}

// From ApplyWrapper once its hooks are in (DllMain).
static void ApplyRestart(const wchar_t* ini) {
    g_wrChanOn = GetPrivateProfileIntW(L"Wrapper", L"Channel", 0, ini) != 0;
    g_wrRestart = GetPrivateProfileIntW(L"Wrapper", L"Restart", 0, ini) != 0;
    g_wrAsk = GetPrivateProfileIntW(L"Wrapper", L"RestartAsk", 0, ini) != 0;
    g_wrHotkeyOn = GetPrivateProfileIntW(L"Wrapper", L"RestartHotkey", 0, ini) != 0;
    g_wrFrame = GetPrivateProfileIntW(L"Wrapper", L"RestartFrame", 1, ini) != 0;
    g_wrOnMB = GetPrivateProfileIntW(L"Wrapper", L"RestartThresholdMB", 320, ini);
    g_wrQuitS = GetPrivateProfileIntW(L"Wrapper", L"QuitTimeoutS", 60, ini);
    g_wrCoverS = GetPrivateProfileIntW(L"Wrapper", L"CoverTimeoutS", 600, ini);
    if (g_wrOnMB > 4000) g_wrOnMB = 4000;
    if (g_wrQuitS < 10 || g_wrQuitS > 600) g_wrQuitS = 60;
    if (g_wrCoverS < 60 || g_wrCoverS > 3600) g_wrCoverS = 600;
    GetCurrentDirectoryW(MAX_PATH, g_wrCwd);  // the game's start folder: the host starts in it and relaunches from it
    GetModuleFileNameW(NULL, g_wrExe, MAX_PATH);
    if (g_wrRestart && !g_wrChanOn) g_wrRestart = false, Log("wrapper: Restart=1 needs Channel=1, Restart off");
    if (g_wrRestart && (!g_udOn || !ts3native_counters.saveReady))
        g_wrRestart = false, Log("wrapper: Restart=1 needs [UserDiag] Enabled=1 and [SaveWrite] Enabled=1 (text channel, save result), Restart off");
    wchar_t v[64];
    const DWORD n = GetEnvironmentVariableW(kWrChanEnv, v, _countof(v));
    if (n && n < _countof(v) && g_wrChanOn) {
        WrChanOpen(v);
        if (g_wrBoot && g_wrRestart) WrBootCheck();
        else if (g_wrBoot) Log("wrapper: restart boot, but Restart is off here: the save loads without a restore");  // ready at the world load
    } else if (g_wrRestart) WrDeleteHandoff("left from an earlier session");
    if (!g_wrChanOn) return;
    Log("wrapper: channel on%s; Restart %s (threshold %lu MB, %s, hotkey %s, cover %s, quit timeout %lu s, cover timeout %lu s)",
        g_wrChan ? " (opened)" : " (created when the host starts)", g_wrRestart ? "on" : "off", g_wrOnMB, g_wrAsk ? "asks" : "does not ask",
        g_wrHotkeyOn ? "Ctrl+Shift+F12" : "off", g_wrFrame ? "last frame" : "black", g_wrQuitS, g_wrCoverS);
}
