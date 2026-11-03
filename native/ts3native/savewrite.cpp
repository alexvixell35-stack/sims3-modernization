// Phase 7: save timing and the save backup copy off the sim thread (docs/15 phase 7). Included by ts3native.cpp.
//
// Native save = FUN_00AB1030 (thiscall, 3 args), called from IC_LoadSaveManager_SaveGame_Impl at 0x742A95. In order:
//   0xAB10F6 FUN_00AAE950  fill the save info (paths; info+0x430 = "<Saves>\<name>.sims3\<world>.nhd")
//   0xAB12AF FUN_00AAC110  write the game into the working copy package (CurrentGame.sims3), Mono callbacks: sim thread only
//   0xAB134E FUN_00AB0F80  if <world>.nhd exists: back up the old save, i.e. delete <name>.sims3.backup, copy
//                          <name>.sims3 -> <name>.sims3.backup with CopyFileW, FlushFileBuffers every file
//   0xAB1363 FUN_00AB0E80  delete <name>.sims3, copy CurrentGame.sims3 -> <name>.sims3, flush every file
//   0xAB1410 FUN_00AAF5F0  write 1a5tf1l3.dat, flush the save folder again
// The backup step reads only the old save, which nothing writes during the save, so with Mode=1 a worker copies it to a
// temp folder while the sim thread writes the package; at 0xAB134E the sim thread waits for it and renames the temp copy
// to <name>.sims3.backup. The old save must be unchanged (names, sizes, write times) from the job start to that point
// and the copy must match it, else the temp copy is dropped and the game's own backup runs. Nothing is reported earlier
// than before: the game still copies and flushes the new save itself before it returns.
#include <wctype.h>

static const DWORD SAVE_GAME_SITE = 0x00742A95, SAVE_GAME = 0x00AB1030;
static const DWORD SAVE_INFO_SITE = 0x00AB10F6, SAVE_INFO = 0x00AAE950;
static const DWORD SAVE_WRITE_SITE = 0x00AB12AF, SAVE_WRITE = 0x00AAC110;
static const DWORD SAVE_BACKUP_SITE = 0x00AB134E, SAVE_BACKUP = 0x00AB0F80;
static const DWORD SAVE_COPY_SITE = 0x00AB1363, SAVE_COPY = 0x00AB0E80;
static const DWORD SAVE_FINAL_SITE = 0x00AB1410, SAVE_FINAL = 0x00AAF5F0;
static const size_t kInfoNhdPath = 0x430;  // inline wchar_t[520] path buffer in the save info

// thiscall as __fastcall with a dummy edx: same stack layout and callee cleanup. DWORD returns keep eax whole.
typedef DWORD(__fastcall* SaveGameFn)(void* lsm, void* edx, void* info, int travelSave, int traveling);  // ret 0Ch
typedef DWORD(__fastcall* SaveInfoFn)(void* lsm, void* edx, u8* info, int keepId, int travelSave);      // ret 0Ch
typedef DWORD(__fastcall* SaveWriteFn)(void* lsm, void* edx, void* pkg, int flag);                     // ret 8
typedef DWORD(__fastcall* SaveBackupFn)(void* lsm, void* edx, const wchar_t* name);                    // ret 4
typedef DWORD(__fastcall* SaveCopyFn)(void* lsm, void* edx, const wchar_t* name, int dir);             // ret 8
typedef DWORD(__fastcall* SaveFinalFn)(void* info, void* edx);                                         // ret
static SaveGameFn g_saveGameOrig = (SaveGameFn)(uintptr_t)SAVE_GAME;
static SaveInfoFn g_saveInfoOrig = (SaveInfoFn)(uintptr_t)SAVE_INFO;
static SaveWriteFn g_saveWriteOrig = (SaveWriteFn)(uintptr_t)SAVE_WRITE;
static SaveBackupFn g_saveBackupOrig = (SaveBackupFn)(uintptr_t)SAVE_BACKUP;
static SaveCopyFn g_saveCopyOrig = (SaveCopyFn)(uintptr_t)SAVE_COPY;
static SaveFinalFn g_saveFinalOrig = (SaveFinalFn)(uintptr_t)SAVE_FINAL;

// --- folder helpers (plain Win32, no game code: they run on the worker) ---
static bool Join(wchar_t* out, const wchar_t* dir, const wchar_t* name) {
    return swprintf_s(out, MAX_PATH, L"%s\\%s", dir, name) > 0;
}
static const wchar_t* Tail(const wchar_t* p) {  // last path part: log lines carry no user folders (docs/188)
    const wchar_t* s = p ? wcsrchr(p, L'\\') : NULL;
    return s ? s + 1 : p;
}
static bool IsDots(const wchar_t* n) { return !wcscmp(n, L".") || !wcscmp(n, L".."); }

static bool RemoveTree(const wchar_t* dir) {
    wchar_t pat[MAX_PATH], p[MAX_PATH];
    WIN32_FIND_DATAW fd;
    if (!Join(pat, dir, L"*")) return false;
    HANDLE h = FindFirstFileW(pat, &fd);
    if (h != INVALID_HANDLE_VALUE) {
        do {
            if (IsDots(fd.cFileName) || !Join(p, dir, fd.cFileName)) continue;
            if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) RemoveTree(p);
            else { SetFileAttributesW(p, FILE_ATTRIBUTE_NORMAL); DeleteFileW(p); }
        } while (FindNextFileW(h, &fd));
        FindClose(h);
    }
    RemoveDirectoryW(dir);
    return GetFileAttributesW(dir) == INVALID_FILE_ATTRIBUTES;
}

// Names (relative), sizes and write times of every file and folder under dir, in one hash. False if unreadable.
static bool TreeHash(const wchar_t* dir, u64* h, u64* bytes) {
    wchar_t pat[MAX_PATH], p[MAX_PATH];
    WIN32_FIND_DATAW fd;
    if (!Join(pat, dir, L"*")) return false;
    HANDLE f = FindFirstFileW(pat, &fd);
    if (f == INVALID_HANDLE_VALUE) return false;
    bool ok = true;
    do {
        if (IsDots(fd.cFileName)) continue;
        for (const wchar_t* c = fd.cFileName; *c; c++) *h = Fnv(*h, towlower(*c));
        const bool isDir = (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0;
        *h = Fnv(*h, isDir ? 0xD1D1D1D1u : 0xF1F1F1F1u);
        if (isDir) ok = Join(p, dir, fd.cFileName) && TreeHash(p, h, bytes) && ok;
        else {
            *h = Fnv(Fnv(Fnv(Fnv(*h, fd.nFileSizeLow), fd.nFileSizeHigh), fd.ftLastWriteTime.dwLowDateTime), fd.ftLastWriteTime.dwHighDateTime);
            *bytes += ((u64)fd.nFileSizeHigh << 32) | fd.nFileSizeLow;
        }
    } while (FindNextFileW(f, &fd));
    FindClose(f);
    return ok;
}

static bool FlushPath(const wchar_t* path) {  // as the game's FUN_00AAF4B0 does it per file
    HANDLE h = CreateFileW(path, GENERIC_WRITE, 0, NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    if (h == INVALID_HANDLE_VALUE) return false;
    const bool ok = FlushFileBuffers(h) != 0;
    CloseHandle(h);
    return ok;
}

static volatile LONG g_sjFiles, g_sjKb;  // the backup worker's copy so far (read by the [UserDiag] save watchdog)

// CopyFileW keeps the write time, so a good copy hashes like its source.
static bool CopyTree(const wchar_t* src, const wchar_t* dst) {
    wchar_t pat[MAX_PATH], s[MAX_PATH], d[MAX_PATH];
    WIN32_FIND_DATAW fd;
    if (!CreateDirectoryW(dst, NULL) || !Join(pat, src, L"*")) return false;
    HANDLE h = FindFirstFileW(pat, &fd);
    if (h == INVALID_HANDLE_VALUE) return false;
    bool ok = true;
    do {
        if (IsDots(fd.cFileName)) continue;
        if (!Join(s, src, fd.cFileName) || !Join(d, dst, fd.cFileName)) { ok = false; break; }
        if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) ok = CopyTree(s, d);
        else {
            ok = CopyFileW(s, d, TRUE) && FlushPath(d);
            InterlockedIncrement(&g_sjFiles);  // [UserDiag] watchdog progress
            InterlockedExchangeAdd(&g_sjKb, (LONG)((((u64)fd.nFileSizeHigh << 32) | fd.nFileSizeLow) >> 10));
        }
    } while (ok && FindNextFileW(h, &fd));
    FindClose(h);
    return ok;
}

// --- the backup job ---
struct SaveJob {
    wchar_t src[MAX_PATH], bak[MAX_PATH], tmp[MAX_PATH];
    u64 hash, bytes;     // the old save at job start
    HANDLE thread;
    volatile LONG ok;    // worker result: 1 = tmp is a flushed copy of src and src did not change meanwhile
    LONG us;
};
static SaveJob g_sj;
static bool g_sjActive;  // only the saving thread touches it (the save is synchronous)
// Phase times in us, -1 = not timed this save (the step was not reached or its hook is not installed). job: 0 none,
// 1 promoted, -1 fallback. writeHow: how `write` was measured (SaveWriteText). gateIn: entry of Save_WritePackage 0xAAC110
// seen by the [RouteGate] Sites stub (rtsites.cpp, RT_SAVE), spanEnd: entry of the first step after the write (backup, copy
// or final), both since t0; with Apex owning the call 0xAB12AF (docs/148 §7a) the write is the span between them.
struct SaveTimes { LARGE_INTEGER t0; LONG infoEnd, write, backup, copy, final, wait, gateIn, spanEnd; int job; char writeHow; };
enum { SW_NONE, SW_CALL, SW_GATE, SW_INFO };
static SaveTimes g_st;
static volatile LONG g_saveTid;  // thread inside SaveGameHook, 0 = no save running

static void SaveTimesReset() {
    memset(&g_st, 0, sizeof(g_st));
    g_st.infoEnd = g_st.write = g_st.backup = g_st.copy = g_st.final = g_st.gateIn = g_st.spanEnd = -1;
}

// rtsites.cpp RT_SAVE entry: the first Save_WritePackage entry on the saving thread during a save
static void SaveGateEnter(DWORD tid) {
    if (g_saveTid && (DWORD)g_saveTid == tid && g_st.gateIn < 0) {
        g_st.gateIn = UsSince(g_st.t0);
        UdPhase("write: route gate (deferred footprints, running route job), header");
    }
}
static void SaveSpanEnd() {  // entry of backup, copy or final: the package write is over
    if (g_st.spanEnd < 0) g_st.spanEnd = UsSince(g_st.t0);
}

// Fills write/writeHow when the call hook did not time it: gate entry (else end of the info step) to the next step's entry.
// The info span also holds what runs before the call (Apex Room to Save's preparation when it owns 0xAB12AF).
static void SaveWriteResolve(SaveTimes& t) {
    if (t.writeHow == SW_CALL || t.spanEnd < 0) return;
    if (t.gateIn >= 0 && t.spanEnd >= t.gateIn) { t.write = t.spanEnd - t.gateIn; t.writeHow = SW_GATE; }
    else if (t.infoEnd >= 0 && t.spanEnd >= t.infoEnd) { t.write = t.spanEnd - t.infoEnd; t.writeHow = SW_INFO; }
}

static const char* SavePhaseText(char* b, size_t n, LONG us) {  // "12" ms, or "not measured"
    if (us < 0) return "not measured";
    sprintf_s(b, n, "%ld", us / 1000);
    return b;
}
// "3000 (call)" timed at 0xAB12AF; "~15000 (gate span)" / "~15000 (info span)" from the entries around it; "not measured"
static const char* SaveWriteText(char* b, size_t n, const SaveTimes& t) {
    if (t.write < 0 || t.writeHow == SW_NONE) return "not measured";
    if (t.writeHow == SW_CALL) sprintf_s(b, n, "%ld (call)", t.write / 1000);
    else sprintf_s(b, n, "~%ld (%s span)", t.write / 1000, t.writeHow == SW_GATE ? "gate" : "info");
    return b;
}

static bool TreeHashOf(const wchar_t* dir, u64* h, u64* bytes) {
    *h = 0xCBF29CE484222325ull;
    *bytes = 0;
    return TreeHash(dir, h, bytes);
}

static DWORD WINAPI SaveJobMain(LPVOID) {
    CeRec(CE_SAVE, 0);  // docs/186 §8
    LARGE_INTEGER t0;
    QueryPerformanceCounter(&t0);
    u64 h1, b1, h2, b2;
    bool ok = (GetFileAttributesW(g_sj.tmp) == INVALID_FILE_ATTRIBUTES || RemoveTree(g_sj.tmp)) && CopyTree(g_sj.src, g_sj.tmp) &&
              TreeHashOf(g_sj.src, &h1, &b1) && h1 == g_sj.hash && b1 == g_sj.bytes &&
              TreeHashOf(g_sj.tmp, &h2, &b2) && h2 == g_sj.hash && b2 == g_sj.bytes;
    g_sj.us = UsSince(t0);
    InterlockedExchange(&g_sj.ok, ok ? 1 : 0);
    CeRec(CE_SAVE, 1, ok);
    return 0;
}

static bool EndsWithI(const wchar_t* s, const wchar_t* tail) {
    const size_t n = wcslen(s), m = wcslen(tail);
    return n >= m && !_wcsicmp(s + n - m, tail);
}

static void CutLast(wchar_t* path) {  // "a\b\c" -> "a\b"
    wchar_t* sl = wcsrchr(path, L'\\');
    if (sl) *sl = 0; else *path = 0;
}

// Paths from the save info; false (no job) unless they look exactly as expected.
static bool SaveJobPaths(const u8* info) {
    const wchar_t* nhd = (const wchar_t*)(info + kInfoNhdPath);
    if (!Readable(nhd, 520 * sizeof(wchar_t))) return false;
    const size_t n = wcsnlen(nhd, 520);
    if (n == 0 || n >= MAX_PATH || !EndsWithI(nhd, L".nhd")) return false;
    DWORD a = GetFileAttributesW(nhd);
    if (a == INVALID_FILE_ATTRIBUTES || (a & FILE_ATTRIBUTE_DIRECTORY)) return false;  // no old save: the game skips the backup too
    wcscpy_s(g_sj.src, nhd);
    CutLast(g_sj.src);  // <Saves>\<name>.sims3
    wchar_t user[MAX_PATH];
    wcscpy_s(user, g_sj.src);
    CutLast(user);  // <Saves>
    if (!EndsWithI(g_sj.src, L".sims3") || !EndsWithI(user, L"\\Saves")) return false;
    CutLast(user);  // the TS3 user folder: same volume as Saves (rename works), not listed as a save
    return swprintf_s(g_sj.bak, L"%s.backup", g_sj.src) > 0 && Join(g_sj.tmp, user, L"ts3native-backup.tmp");
}

static void SaveJobDiscard() {  // wait for the worker and drop its copy
    if (!g_sjActive) return;
    UdWait("the backup worker (copy not used)");
    WaitForSingleObject(g_sj.thread, INFINITE);
    UdWait(NULL);
    CloseHandle(g_sj.thread);
    g_sjActive = false;
    RemoveTree(g_sj.tmp);
}

static void SaveJobStart(const u8* info) {
    SaveJobDiscard();
    if (!SaveJobPaths(info) || !TreeHashOf(g_sj.src, &g_sj.hash, &g_sj.bytes)) return;
    g_sj.ok = 0;
    g_sjFiles = g_sjKb = 0;
    g_sj.thread = CreateThread(NULL, 256 * 1024, SaveJobMain, NULL, STACK_SIZE_PARAM_IS_A_RESERVATION, NULL);
    if (!g_sj.thread) return;
    g_sjActive = true;
    InterlockedIncrement(&ts3native_counters.saveJobs);
}

// The game's backup step, done with the worker's copy. False = nothing promoted; the caller runs the game's own step.
static bool SaveJobPromote(const wchar_t* name) {
    LARGE_INTEGER t0;
    QueryPerformanceCounter(&t0);
    UdWait("the backup worker's copy of the old save");
    WaitForSingleObject(g_sj.thread, INFINITE);
    UdWait(NULL);
    g_st.wait = UsSince(t0);
    CloseHandle(g_sj.thread);
    g_sjActive = false;
    // The game's step works on <Saves>\<name> (+ ".sims3"); ours must be the same folder.
    const wchar_t* last = wcsrchr(g_sj.src, L'\\');
    wchar_t want[MAX_PATH];
    bool same = last && name && Readable(name, MAX_PATH * sizeof(wchar_t)) && wcsnlen(name, MAX_PATH) < MAX_PATH - 8 &&
                (!_wcsicmp(last + 1, name) || (swprintf_s(want, L"%s.sims3", name) > 0 && !_wcsicmp(last + 1, want)));
    u64 h, b;
    if (!g_sj.ok || !same || !TreeHashOf(g_sj.src, &h, &b) || h != g_sj.hash || b != g_sj.bytes) {
        Log("save backup: worker copy not used (ok %ld, same folder %d), game copies", g_sj.ok, same);
        RemoveTree(g_sj.tmp);
        return false;
    }
    // As FUN_00AAFC50: a file in the way is deleted; a folder is removed with up to 10 tries 500 ms apart.
    DWORD a = GetFileAttributesW(g_sj.bak);
    if (a != INVALID_FILE_ATTRIBUTES && !(a & FILE_ATTRIBUTE_DIRECTORY)) DeleteFileW(g_sj.bak);
    for (int i = 0; i < 10 && GetFileAttributesW(g_sj.bak) != INVALID_FILE_ATTRIBUTES; i++)
        if (!RemoveTree(g_sj.bak) && i < 9) Sleep(500);
    CeRec(CE_SAVE, 4);
    if (GetFileAttributesW(g_sj.bak) != INVALID_FILE_ATTRIBUTES || !MoveFileExW(g_sj.tmp, g_sj.bak, MOVEFILE_WRITE_THROUGH)) {
        Log("save backup: rename to %ls failed (err %lu), game copies", Tail(g_sj.bak), GetLastError());
        RemoveTree(g_sj.tmp);
        return false;
    }
    return true;
}

// --- [SaveFlush]: the flushes of steps 9 and 11 on a worker (docs/15 phase 10) ---
// FUN_00AAF4B0 (cdecl, path) opens every file under path and calls FlushFileBuffers. Step 9's copy (call at 0xAAFD8A in
// FUN_00AAFC50) and step 11 (call at 0xAAF737 in FUN_00AAF5F0) flush the new save. With flushMode 1 those two calls only
// record the path while the save runs; after FUN_00AB1030 returns, a worker replays them in the same order. The bytes are
// already written (in the OS file cache) when the game reports success, so a game crash loses nothing; only an OS crash or
// power loss before the worker is done can (the .backup was flushed before, at step 8). The next save (and travel) and
// the load copy (0xAB16ED, FUN_00AB0E80 dir 1) wait for the worker first, so the game never deletes or copies a save
// folder the worker still has open.
static const DWORD SAVE_FLUSH = 0x00AAF4B0, SAVE_FLUSH_COPY_SITE = 0x00AAFD8A, SAVE_FLUSH_FINAL_SITE = 0x00AAF737;
static const DWORD LOAD_COPY_SITE = 0x00AB16ED;
typedef void(__cdecl* SaveFlushFn)(const wchar_t* path);
static SaveFlushFn g_saveFlushOrig = (SaveFlushFn)(uintptr_t)SAVE_FLUSH;
static SaveCopyFn g_loadCopyOrig = (SaveCopyFn)(uintptr_t)SAVE_COPY;
struct FlushJob { wchar_t path[8][MAX_PATH]; int n; HANDLE thread; LONG files, fails, us; DWORD tid; bool defer; };
static FlushJob g_fj;

static void __cdecl SaveFlushHook(const wchar_t* path) {
    FlushJob& f = g_fj;
    if (f.defer && f.tid == GetCurrentThreadId() && f.n < 8 && path && wcsnlen(path, MAX_PATH) < MAX_PATH) {
        wcscpy_s(f.path[f.n++], path);
        InterlockedIncrement(&ts3native_counters.flushDeferred);
        return;
    }
    g_saveFlushOrig(path);
}

static void FlushFail(const wchar_t* path, const char* step) {  // worker thread; first few per save only
    if (g_fj.fails++ < 4) Log("save flush FAIL %s (err %lu): %ls", step, GetLastError(), Tail(path));
}

static void FlushTree(const wchar_t* path) {  // as FUN_00AAF4B0, but shared so readers are not refused
    FlushJob& f = g_fj;
    const DWORD a = GetFileAttributesW(path);
    if (a == INVALID_FILE_ATTRIBUTES) { FlushFail(path, "attributes"); return; }
    if (!(a & FILE_ATTRIBUTE_DIRECTORY)) {
        HANDLE h = CreateFileW(path, GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
        // Meta.data is briefly held without write sharing right after the save (err 32, Sunset Valley 2026-10-15): retry
        for (int i = 0; i < 10 && h == INVALID_HANDLE_VALUE && GetLastError() == ERROR_SHARING_VIOLATION; i++) {
            Sleep(50);
            h = CreateFileW(path, GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
        }
        if (h == INVALID_HANDLE_VALUE) { FlushFail(path, "open"); return; }
        const bool ok = FlushFileBuffers(h) != 0;
        if (!ok) FlushFail(path, "flush");
        CloseHandle(h);
        if (ok) f.files++;
        return;
    }
    wchar_t pat[MAX_PATH], p[MAX_PATH];
    WIN32_FIND_DATAW fd;
    if (!Join(pat, path, L"*")) { FlushFail(path, "path too long"); return; }
    HANDLE h = FindFirstFileW(pat, &fd);
    if (h == INVALID_HANDLE_VALUE) return;
    do {
        if (!IsDots(fd.cFileName) && Join(p, path, fd.cFileName)) FlushTree(p);
    } while (FindNextFileW(h, &fd));
    FindClose(h);
}

static DWORD WINAPI FlushMain(LPVOID) {
    FlushJob& f = g_fj;
    CeRec(CE_SAVE, 2);  // docs/186 §8
    LARGE_INTEGER t0;
    QueryPerformanceCounter(&t0);
    for (int i = 0; i < f.n; i++) FlushTree(f.path[i]);
    f.us = UsSince(t0);
    Ts3nCounters& k = ts3native_counters;
    k.flushLastMs = f.us / 1000;
    k.flushFails += f.fails;
    CeRec(CE_SAVE, 3, (DWORD)f.files);
    Log("save flush: %d calls, %ld files flushed on the worker, %ld fails, %ld ms", f.n, f.files, f.fails, f.us / 1000);
    return 0;
}

static void FlushWait() {  // sim thread: before anything that deletes, copies or rewrites a save folder
    FlushJob& f = g_fj;
    if (!f.thread) return;
    if (WaitForSingleObject(f.thread, 0) == WAIT_TIMEOUT) {
        LARGE_INTEGER t0;
        QueryPerformanceCounter(&t0);
        UdWait("the previous save's flush worker");
        WaitForSingleObject(f.thread, INFINITE);
        UdWait(NULL);
        Ts3nCounters& k = ts3native_counters;
        InterlockedIncrement(&k.flushWaits);
        k.flushWaitMs += UsSince(t0) / 1000;
    }
    CloseHandle(f.thread);
    f.thread = NULL;
}

static void FlushStart() {  // sim thread, after the save returned
    FlushJob& f = g_fj;
    f.defer = false;
    if (!f.n) return;
    f.files = f.fails = 0;
    f.thread = CreateThread(NULL, 256 * 1024, FlushMain, NULL, STACK_SIZE_PARAM_IS_A_RESERVATION, NULL);
    if (f.thread) { InterlockedIncrement(&ts3native_counters.flushJobs); return; }
    for (int i = 0; i < f.n; i++) g_saveFlushOrig(f.path[i]);  // no thread: the game's flushes, here, as before
    f.n = 0;
}

static DWORD __fastcall LoadCopyHook(void* lsm, void*, const wchar_t* name, int dir) {
    FlushWait();
    SlimWait();  // [SaveSlim]: the worker may still rewrite this save folder
    return g_loadCopyOrig(lsm, NULL, name, dir);
}

// --- call-site hooks (sim thread) ---
static DWORD __fastcall SaveGameHook(void* lsm, void*, void* info, int travelSave, int traveling) {
    UdSaveStart(travelSave, traveling);  // [UserDiag] "save: start" + watchdog, before anything here can wait
    FlushWait();  // the previous save's flushes, before this save touches any save folder
    SlimWait();   // [SaveSlim]: the previous save's rewrite, likewise
    SaveTimesReset();
    g_fj.n = 0;
    g_fj.tid = GetCurrentThreadId();
    QueryPerformanceCounter(&g_st.t0);
    const LONG outerTid = InterlockedExchange(&g_saveTid, (LONG)GetCurrentThreadId());
    const Ts3nCounters& k = ts3native_counters;
    const LONG dc0 = k.compressDirectCalls, dms0 = k.compressDirectMs, dmb0 = k.compressDirectMB, cm0 = k.compressCommits;
    const LONG ch0 = k.cacheHits, cmi0 = k.cacheMisses, chm0 = k.cacheHitMB, cx0 = k.cacheMismatches, chs0 = k.cacheHashMs;
    const DWORD r = g_saveGameOrig(lsm, NULL, info, travelSave, traveling);
    UdPhase("after the game's save (our flush and log)");
    InterlockedExchange(&g_saveTid, outerTid);
    SaveWriteResolve(g_st);
    if (k.compressReady)
        Log("save compress: this save %ld codec compress calls outside a batch (%ld MB in, %ld ms), %ld commit calls (pending at the last: %ld)",
            k.compressDirectCalls - dc0, k.compressDirectMB - dmb0, k.compressDirectMs - dms0, k.compressCommits - cm0, k.compressCommitPending);
    if (k.cacheReady)
        Log("save compress cache: hits %ld (%ld MB in), misses %ld, mismatches %ld, hash %ld ms, held %ld entries %ld KB, evicted %ld",
            k.cacheHits - ch0, k.cacheHitMB - chm0, k.cacheMisses - cmi0, k.cacheMismatches - cx0, k.cacheHashMs - chs0, k.cacheEntries,
            k.cacheKB, k.cacheEvicted);
    SaveJobDiscard();  // a save that never reached the backup step (error, travel) leaves no copy behind
    const int deferred = g_fj.n;
    FlushStart();
    SlimStart(r);  // [SaveSlim]: after a save that worked, the saved ExportDB on a worker
    const LONG total = UsSince(g_st.t0);
    ts3native_counters.saveLastUs = total;
    ts3native_counters.saveLastWriteUs = g_st.write < 0 ? 0 : g_st.write;  // counters: 0 = not measured, as before
    ts3native_counters.saveLastBackupUs = g_st.backup < 0 ? 0 : g_st.backup;
    ts3native_counters.saveLastCopyUs = g_st.copy < 0 ? 0 : g_st.copy;
    ts3native_counters.saveLastFinalUs = g_st.final < 0 ? 0 : g_st.final;
    InterlockedIncrement(&ts3native_counters.saveCount);
    char bi[16], bw[40], bb[16], bc[16], bf[16];
    // docs/25 §10: a phase that was not timed reads "not measured"; write says how it was measured
    Log("save: result %lu travel %d/%d total %ld ms: info %s, write %s, backup %s (%s, worker %ld, wait %ld), copy %s, final %s ms, flushes deferred %d",
        r, travelSave, traveling, total / 1000, SavePhaseText(bi, 16, g_st.infoEnd), SaveWriteText(bw, 40, g_st),
        SavePhaseText(bb, 16, g_st.backup), g_st.job > 0 ? "worker copy" : g_st.job < 0 ? "fallback" : "game",
        g_st.job ? g_sj.us / 1000 : 0, g_st.wait / 1000, SavePhaseText(bc, 16, g_st.copy), SavePhaseText(bf, 16, g_st.final), deferred);
    UdSaveEnd(r);
    return r;
}

static bool FlushDeferOn() { return ts3native_counters.flushReady && ts3native_counters.flushMode == 1; }

static DWORD __fastcall SaveInfoHook(void* lsm, void*, u8* info, int keepId, int travelSave) {
    UdPhase("info (save paths)");
    const DWORD r = g_saveInfoOrig(lsm, NULL, info, keepId, travelSave);
    g_st.infoEnd = UsSince(g_st.t0);
    UdInfoDir(info);
    if (r == 0 && !(travelSave & 0xFF)) SlimNote(info);
    UdPhase("write (package)");
    if (r == 0 && !(travelSave & 0xFF) && ts3native_counters.saveMode == 1) SaveJobStart(info);
    return r;
}

static DWORD __fastcall SaveWriteHook(void* lsm, void*, void* pkg, int flag) {
    LARGE_INTEGER t0;
    QueryPerformanceCounter(&t0);
    const DWORD r = g_saveWriteOrig(lsm, NULL, pkg, flag);
    g_st.write = UsSince(t0);
    g_st.writeHow = SW_CALL;
    return r;
}

static DWORD __fastcall SaveBackupHook(void* lsm, void*, const wchar_t* name) {
    SaveSpanEnd();
    UdPhase("backup (old save to .backup)");
    LARGE_INTEGER t0;
    QueryPerformanceCounter(&t0);
    DWORD r;
    const bool hadJob = g_sjActive;
    if (hadJob && SaveJobPromote(name)) {
        r = 1;  // what the game's step returns when its copy worked
        g_st.job = 1;
        InterlockedIncrement(&ts3native_counters.savePromoted);
    } else {
        if (hadJob) { g_st.job = -1; InterlockedIncrement(&ts3native_counters.saveFallbacks); }
        r = g_saveBackupOrig(lsm, NULL, name);
    }
    g_st.backup = UsSince(t0);
    return r;
}

static DWORD __fastcall SaveCopyHook(void* lsm, void*, const wchar_t* name, int dir) {
    SaveSpanEnd();
    UdPhase("copy (new save into place)");
    LARGE_INTEGER t0;
    QueryPerformanceCounter(&t0);
    g_fj.defer = FlushDeferOn();
    const DWORD r = g_saveCopyOrig(lsm, NULL, name, dir);
    g_fj.defer = false;
    g_st.copy = UsSince(t0);
    return r;
}

static DWORD __fastcall SaveFinalHook(void* info, void*) {
    SaveSpanEnd();
    UdPhase("final (marker file, folder flush)");
    LARGE_INTEGER t0;
    QueryPerformanceCounter(&t0);
    g_fj.defer = FlushDeferOn();
    const DWORD r = g_saveFinalOrig(info, NULL);
    g_fj.defer = false;
    g_st.final = UsSince(t0);
    return r;
}

// Rewrites `call rel32` sites: every site must be an E8 to its expected target, else nothing is written.
struct CallSite { DWORD site, target; void* hook; };
static bool PatchCalls(const char* name, const CallSite* s, int count) {
    for (int i = 0; i < count; i++) {
        const u8* p = (const u8*)(uintptr_t)s[i].site;
        if (!Readable(p, 5) || p[0] != 0xE8 || (DWORD)(s[i].site + 5 + *(const LONG*)(p + 1)) != s[i].target) {
            Log("SKIP %s: call at 0x%08X is not a call to 0x%08X, nothing written", name, s[i].site, s[i].target);
            return false;
        }
    }
    for (int i = 0; i < count; i++) {
        const LONG rel = (LONG)((uintptr_t)s[i].hook - (s[i].site + 5));
        if (!Write((u8*)(uintptr_t)s[i].site + 1, &rel, 4)) {
            Log("FAIL %s: VirtualProtect at 0x%08X (err %lu)", name, s[i].site, GetLastError());
            return false;  // shortcut: earlier sites stay hooked; each hook only times and calls its original
        }
    }
    Log("OK   %s: %d call sites", name, count);
    return true;
}

static const CallSite kSaveSites[] = {
    {SAVE_GAME_SITE, SAVE_GAME, (void*)SaveGameHook},       {SAVE_INFO_SITE, SAVE_INFO, (void*)SaveInfoHook},
    {SAVE_WRITE_SITE, SAVE_WRITE, (void*)SaveWriteHook},    {SAVE_BACKUP_SITE, SAVE_BACKUP, (void*)SaveBackupHook},
    {SAVE_COPY_SITE, SAVE_COPY, (void*)SaveCopyHook},       {SAVE_FINAL_SITE, SAVE_FINAL, (void*)SaveFinalHook}};

// docs/148 §7a: Apex Room to Save puts its early save preparation on 0xAB12AF only while that call still goes to 0xAAC110;
// ours there only times the write, so with Apex owning it the site is left out (backup offload and other timings stay);
// the write is then the span from the [RouteGate] Save_WritePackage entry (else the info step's end) to the next step.
static int SaveSitesFor(bool apexOwnsWrite, CallSite* out) {
    int n = 0;
    for (const CallSite& c : kSaveSites)
        if (!apexOwnsWrite || c.site != SAVE_WRITE_SITE) out[n++] = c;
    return n;
}

// [SaveWrite] Enabled=1 times every save phase (log line per save + counters); Mode is the start value of the runtime
// switch saveMode (1 = backup copy on the worker). Returns true when hooked (the log stays open for the save lines).
static bool ApplySaveWrite(const wchar_t* ini) {
    if (!GetPrivateProfileIntW(L"SaveWrite", L"Enabled", 0, ini)) { Log("save write disabled (Enabled=0)"); return false; }
    QueryPerformanceFrequency(&g_qpf);
    ts3native_counters.saveMode = Clamp(GetPrivateProfileIntW(L"SaveWrite", L"Mode", 1, ini), 0, 1);
    CallSite sites[sizeof(kSaveSites) / sizeof(kSaveSites[0])];
    const int n = SaveSitesFor(ApexStandsDown(AXF_MEMORY_GUARD, "[SaveWrite] write-phase timing call", "call 0xAB12AF"), sites);
    if (!PatchCalls("save write", sites, n)) return false;
    ts3native_counters.saveReady = 1;
    Log("save write settings: Mode=%ld", ts3native_counters.saveMode);
    return true;
}

// apex.cpp late check: puts the original rel32 back at 0xAB12AF if it still goes to SaveWriteHook (one aligned 4-byte store
// at 0xAB12B0; no save runs before a world is loaded [inferred], so no thread is inside the call). True when given back.
static bool SaveWriteYieldToApex(DWORD site) {  // site: SAVE_WRITE_SITE (selftest: a copy)
    LONG* rel = (LONG*)(uintptr_t)(site + 1);
    const u8* p = (const u8*)(uintptr_t)site;
    if (!ts3native_counters.saveReady || !Readable(p, 5) || p[0] != 0xE8 || (DWORD)(site + 5 + *rel) != (DWORD)(uintptr_t)SaveWriteHook)
        return false;
    DWORD old;
    if (!VirtualProtect(rel, 4, PAGE_EXECUTE_READWRITE, &old)) return false;
    InterlockedExchange(rel, (LONG)(SAVE_WRITE - (site + 5)));
    VirtualProtect(rel, 4, old, &old);
    FlushInstructionCache(GetCurrentProcess(), rel, 4);
    return true;
}

static const CallSite kFlushSites[] = {{SAVE_FLUSH_COPY_SITE, SAVE_FLUSH, (void*)SaveFlushHook},
                                       {SAVE_FLUSH_FINAL_SITE, SAVE_FLUSH, (void*)SaveFlushHook},
                                       {LOAD_COPY_SITE, SAVE_COPY, (void*)LoadCopyHook}};

// [SaveFlush] Enabled=1 (needs [SaveWrite] hooked): the flushes of steps 9 and 11 run on a worker after the save; Mode is
// the start value of the runtime switch flushMode (0 = the game flushes, as before).
static bool ApplySaveFlush(const wchar_t* ini) {
    if (!GetPrivateProfileIntW(L"SaveFlush", L"Enabled", 0, ini)) { Log("save flush off (Enabled=0)"); return false; }
    if (!ts3native_counters.saveReady) { Log("SKIP save flush: needs the [SaveWrite] hooks (Enabled=1)"); return false; }
    ts3native_counters.flushMode = GetPrivateProfileIntW(L"SaveFlush", L"Mode", 1, ini) ? 1 : 0;
    if (!PatchCalls("save flush", kFlushSites, sizeof(kFlushSites) / sizeof(kFlushSites[0]))) return false;
    ts3native_counters.flushReady = 1;
    Log("save flush settings: Mode=%ld", ts3native_counters.flushMode);
    return true;
}
