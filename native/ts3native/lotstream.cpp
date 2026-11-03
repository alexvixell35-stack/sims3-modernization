// Lot streaming measurement (docs/28 §5.5, G13). Included by ts3native.cpp (one translation unit). Measurement only.
//
// Camera streaming in live mode [verified, docs/28 §5.2]: WorldManager::Update (main thread) scores lots in 0xC6C290 and
// calls Lot::SetActive 0xAC20E0, which posts AddLotObjectsToScene 0xAC1130 (or the remove 0xAB4C00) as an async remote
// call to the sim thread. The sim thread runs every queued call in one JobManager RunJobs 0x59A430 pass with no time
// budget (MonoScriptHost::Simulate at 0xD81FC3). The remote-call objects execute through their vtable slot +0x10:
// 0xABD3C0 (add type: ecx = [call+0x0C] + [call+0x14], fn [call+0x10], two byte args) and 0xABD3E0 (remove type, one
// byte arg); both are thiscall with no stack arguments and plain `ret` [verified asm]. Hooking those (not 0xAC1130) keeps
// the numbers right with or without S3SS's object throttle, which detours 0xAC1130 itself.
// Hooks (prologues verified against the Steam 1.67 exe; all five required ones are checked before any is written):
//   lotAdd/lotRemove 0xABD3C0/0xABD3E0: QPC ms per call, objects ([lot+0x18]-[lot+0x14])/4, lot id [lot+0x48..+0x4C]
//   pump 0x59A430 (thiscall, 3 args, ret 0xC), sim thread only ([0x11D8A8C]): ms per outermost pass, lot calls inside it
//   lotScore 0xC6C290 (thiscall, 4 args, ret 0x10, main thread): ms per call, calls that switched a lot
//   lotSetActive 0xAC20E0 (thiscall, byte arg, ret 4): switches the game makes ([lot+0xC1] != arg, [lot+0xC9] == 0)
//   optional SyncWait=1: 0x7D98C0 (thiscall, selector, async; ret 8) when async == 0 and the target thread
//     ([0x11D8A8C] for selector 1, [0x11D8A88] for 0) is not the caller: the caller waits (docs/28 §5.3 item 2)
//   optional ObjScene=1: 0xABFAC0 (thiscall on the lot, 3 args, ret 0xC) per object: ms histogram, slow ones logged
// Per event (add, remove, slow obj) one JSON line in <asi>-lotstream.jsonl: {t (UTC ms), kind, lot, lotId, objs, ms,
// passMs (the enclosing pump pass, null outside one)}. Events inside a pass are buffered and written when it ends, so the
// write is never inside a timed span. Runtime switch ts3native_counters.lsMode (+0x2C8): 0 = hooks pass straight through.
// *MaxUs fields are the highest value since the last reset: tools/frida/stutter-profile.js writes 0 after reading them
// each second; with no reader they are all-time maxima.
// shortcut: counters written only by one thread use plain ++ (sim thread: add/remove/pump/sync/obj; main: score); a
// lost update from an unexpected second thread only skews the stats.

static const DWORD LS_ADD = 0x00ABD3C0, LS_REMOVE = 0x00ABD3E0, LS_PUMP = 0x0059A430, LS_SCORE = 0x00C6C290;
static const DWORD LS_SET_ACTIVE = 0x00AC20E0, LS_SYNC = 0x007D98C0, LS_OBJ = 0x00ABFAC0;
static const DWORD LS_ADD_FN = 0x00AC1130, LS_REMOVE_FN = 0x00AB4C00;
static const u8 kLsAdd[7] = {0x56, 0x8B, 0xF1, 0x0F, 0xB6, 0x4E, 0x19};          // push esi; mov esi,ecx; movzx ecx,[esi+0x19]
static const u8 kLsRemove[7] = {0x56, 0x8B, 0xF1, 0x0F, 0xB6, 0x4E, 0x18};       // push esi; mov esi,ecx; movzx ecx,[esi+0x18]
static const u8 kLsPump[8] = {0x53, 0x56, 0x8B, 0xF1, 0x57, 0x8D, 0x7E, 0x08};   // push ebx; push esi; mov esi,ecx; push edi; lea edi,[esi+8]
static const u8 kLsScore[6] = {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF0};              // push ebp; mov ebp,esp; and esp,-16
static const u8 kLsSetActive[5] = {0x53, 0x8A, 0x5C, 0x24, 0x08};                // push ebx; mov bl,[esp+8]
static const u8 kLsSync[6] = {0x51, 0x53, 0x56, 0x57, 0x8B, 0xF1};               // push ecx/ebx/esi/edi; mov esi,ecx
static const u8 kLsObj[10] = {0x83, 0xEC, 0x0C, 0x83, 0xB9, 0x64, 0x03, 0x00, 0x00, 0x00};  // sub esp,0xC; cmp [ecx+0x364],0

typedef DWORD(__fastcall* LsExecFn)(u8* call, void* edx);
typedef DWORD(__fastcall* LsPumpFn)(void* jm, void* edx, DWORD a, DWORD b, DWORD c);
typedef DWORD(__fastcall* LsScoreFn)(void* wm, void* edx, DWORD a, DWORD b, DWORD c, DWORD d);
typedef DWORD(__fastcall* LsSetActiveFn)(u8* lot, void* edx, DWORD on);
typedef DWORD(__fastcall* LsSyncFn)(void* call, void* edx, DWORD selector, DWORD async);
typedef DWORD(__fastcall* LsObjFn)(u8* lot, void* edx, DWORD a, DWORD b, DWORD c);
static LsExecFn g_lsAddOrig, g_lsRemoveOrig;
static LsPumpFn g_lsPumpOrig;
static LsScoreFn g_lsScoreOrig;
static LsSetActiveFn g_lsSetActiveOrig;
static LsSyncFn g_lsSyncOrig;
static LsObjFn g_lsObjOrig;
// game globals; the selftest points them at its own
static const DWORD* g_lsSimTid = (const DWORD*)(uintptr_t)0x011D8A8C;
static const DWORD* g_lsMainTidSlot = (const DWORD*)(uintptr_t)0x011D8A88;
static double g_lsUsPerTick;
static LONG g_lsObjLogUs = 5000;

struct LsEvent { ULONGLONG t; const char* kind; DWORD lot, idLo, idHi, obj; LONG objs, us; };
static LsEvent g_lsBuf[64];   // events of the current pump pass (sim thread, inside the pass only)
static int g_lsBufN;
static DWORD g_lsPumpTid;     // sim thread while it is inside a measured pass, else 0
static LONG g_lsPumpLots;     // lot add/remove calls in the current pass
static DWORD g_lsScoreTid;    // main thread while inside lotScore
static LONG g_lsScoreChanges;
static HANDLE g_lsLog = INVALID_HANDLE_VALUE;
static SRWLOCK g_lsLogLock = SRWLOCK_INIT;
static DWORD g_lsOtherFns[8];
static int g_lsOtherFnCount;

static inline LONGLONG LsNow() { LARGE_INTEGER t; QueryPerformanceCounter(&t); return t.QuadPart; }
static inline LONG LsUs(LONGLONG t0, LONGLONG t1) { return (LONG)((t1 - t0) * g_lsUsPerTick); }
static inline void LsMax(LONG& m, LONG v) { if (v > m) m = v; }

static ULONGLONG LsUnixMs() {
    FILETIME f;
    GetSystemTimeAsFileTime(&f);
    return ((ULONGLONG)f.dwHighDateTime << 32 | f.dwLowDateTime) / 10000 - 11644473600000ULL;
}

static void LsWrite(const LsEvent* e, int n, LONG passUs) {  // passUs < 0: not inside a pump pass
    if (g_lsLog == INVALID_HANDLE_VALUE || n <= 0) return;
    static char buf[64 * 200];  // used under g_lsLogLock only
    char pass[24], objs[16];
    if (passUs >= 0) sprintf_s(pass, "%.3f", passUs / 1000.0); else strcpy_s(pass, "null");
    AcquireSRWLockExclusive(&g_lsLogLock);
    int len = 0, done = 0;
    for (; done < n; done++) {
        const LsEvent& x = e[done];
        if (x.objs >= 0) sprintf_s(objs, "%ld", x.objs); else strcpy_s(objs, "null");
        int w = _snprintf_s(buf + len, sizeof(buf) - len, _TRUNCATE,
                            "{\"t\":%llu,\"kind\":\"%s\",\"lot\":\"0x%08X\",\"lotId\":\"0x%08X%08X\",\"objs\":%s,\"ms\":%.3f,\"passMs\":%s",
                            x.t, x.kind, x.lot, x.idHi, x.idLo, objs, x.us / 1000.0, pass);
        if (w < 0) break;
        const int w2 = x.obj ? _snprintf_s(buf + len + w, sizeof(buf) - len - w, _TRUNCATE, ",\"obj\":\"0x%08X\"}\n", x.obj)
                             : _snprintf_s(buf + len + w, sizeof(buf) - len - w, _TRUNCATE, "}\n");
        if (w2 < 0) break;
        len += w + w2;
    }
    DWORD wrote;
    if (!WriteFile(g_lsLog, buf, (DWORD)len, &wrote, NULL)) done = 0;
    ts3native_counters.lsLogDropped += n - done;
    ReleaseSRWLockExclusive(&g_lsLogLock);
}

// inside the sim thread's pass: buffered until the pass ends (passMs known); anywhere else: written now
static void LsEmit(const char* kind, const u8* lot, LONG objs, LONG us, DWORD obj) {
    if (g_lsLog == INVALID_HANDLE_VALUE) return;
    LsEvent e = {LsUnixMs(), kind, (DWORD)(uintptr_t)lot, *(const DWORD*)(lot + 0x48), *(const DWORD*)(lot + 0x4C), obj, objs, us};
    if (g_lsPumpTid && g_lsPumpTid == GetCurrentThreadId()) {
        if (g_lsBufN < 64) g_lsBuf[g_lsBufN++] = e; else ts3native_counters.lsLogDropped++;
        return;
    }
    LsWrite(&e, 1, -1);
}

static void LsOtherFn(DWORD fn) {
    ts3native_counters.lsOtherFns++;
    if (g_lsOtherFnCount >= 8) return;
    for (int i = 0; i < g_lsOtherFnCount; i++)
        if (g_lsOtherFns[i] == fn) return;
    g_lsOtherFns[g_lsOtherFnCount++] = fn;
    Log("lot stream: remote call fn 0x%08X on the add/remove execute (not counted)", fn);
}

static DWORD LsExec(u8* call, void* edx, LsExecFn orig, DWORD want, bool add) {
    Ts3nCounters& k = ts3native_counters;
    if (k.lsMode != 1) return orig(call, edx);
    const DWORD fn = *(const DWORD*)(call + 0x10);
    if (fn != want) { LsOtherFn(fn); return orig(call, edx); }
    const u8* lot = *(u8* const*)(call + 0x0C) + *(const int*)(call + 0x14);  // the `this` the execute passes to fn
    const LONG objs = (LONG)((*(const DWORD*)(lot + 0x18) - *(const DWORD*)(lot + 0x14)) / 4);
    const LONGLONG t0 = LsNow();
    const DWORD r = orig(call, edx);
    const LONG us = LsUs(t0, LsNow());
    if (add) { k.lotAdds++; k.lotAddUs += us; LsMax(k.lotAddMaxUs, us); k.lotAddObjs += objs; LsMax(k.lotAddMaxObjs, objs); }
    else { k.lotRemoves++; k.lotRemoveUs += us; LsMax(k.lotRemoveMaxUs, us); }
    if (g_lsPumpTid && g_lsPumpTid == GetCurrentThreadId()) g_lsPumpLots++;
    LsEmit(add ? "add" : "remove", lot, objs, us, 0);
    return r;
}
static DWORD __fastcall LsAddHook(u8* call, void* edx) { return LsExec(call, edx, g_lsAddOrig, LS_ADD_FN, true); }
static DWORD __fastcall LsRemoveHook(u8* call, void* edx) { return LsExec(call, edx, g_lsRemoveOrig, LS_REMOVE_FN, false); }

static DWORD __fastcall LsPumpHook(void* jm, void* edx, DWORD a, DWORD b, DWORD c) {
    Ts3nCounters& k = ts3native_counters;
    const DWORD tid = GetCurrentThreadId();
    if (k.lsMode != 1 || tid != *g_lsSimTid || g_lsPumpTid) return g_lsPumpOrig(jm, edx, a, b, c);  // outermost sim pass only
    g_lsPumpLots = 0;
    g_lsBufN = 0;
    g_lsPumpTid = tid;
    const LONGLONG t0 = LsNow();
    const DWORD r = g_lsPumpOrig(jm, edx, a, b, c);
    const LONG us = LsUs(t0, LsNow());
    g_lsPumpTid = 0;
    k.pumpCalls++;
    k.pumpUs += us;  // shortcut: LONG wraps after ~35 min of total pump time; fine for one test run
    LsMax(k.pumpMaxUs, us);
    if (g_lsPumpLots) { k.pumpLotPasses++; LsMax(k.pumpMaxLots, g_lsPumpLots); }
    LsWrite(g_lsBuf, g_lsBufN, us);
    return r;
}

static DWORD __fastcall LsScoreHook(void* wm, void* edx, DWORD a, DWORD b, DWORD c, DWORD d) {
    Ts3nCounters& k = ts3native_counters;
    if (k.lsMode != 1 || g_lsScoreTid) return g_lsScoreOrig(wm, edx, a, b, c, d);
    g_lsScoreChanges = 0;
    g_lsScoreTid = GetCurrentThreadId();
    const LONGLONG t0 = LsNow();
    const DWORD r = g_lsScoreOrig(wm, edx, a, b, c, d);
    const LONG us = LsUs(t0, LsNow());
    g_lsScoreTid = 0;
    k.lotScoreCalls++;
    k.lotScoreUs += us;
    LsMax(k.lotScoreMaxUs, us);
    if (g_lsScoreChanges) k.lotScoreChanged++;
    return r;
}

static DWORD __fastcall LsSetActiveHook(u8* lot, void* edx, DWORD on) {
    Ts3nCounters& k = ts3native_counters;
    if (k.lsMode == 1 && lot[0xC1] != (u8)on && lot[0xC9] == 0) {  // the game's own test before it posts the add/remove
        InterlockedIncrement((u8)on ? &k.lotSetOn : &k.lotSetOff);
        if (g_lsScoreTid == GetCurrentThreadId()) g_lsScoreChanges++;
    }
    return g_lsSetActiveOrig(lot, edx, on);
}

static DWORD __fastcall LsSyncHook(void* call, void* edx, DWORD selector, DWORD async) {
    Ts3nCounters& k = ts3native_counters;
    const DWORD tid = GetCurrentThreadId();
    if (k.lsMode != 1 || (u8)async || tid == *((u8)selector ? g_lsSimTid : g_lsMainTidSlot)) return g_lsSyncOrig(call, edx, selector, async);
    const LONGLONG t0 = LsNow();
    const DWORD r = g_lsSyncOrig(call, edx, selector, async);
    const LONG us = LsUs(t0, LsNow());
    InterlockedIncrement(&k.syncWaits);
    InterlockedExchangeAdd(&k.syncWaitUs, us);
    LsMax(k.syncWaitMaxUs, us);
    return r;
}

static DWORD __fastcall LsObjHook(u8* lot, void* edx, DWORD a, DWORD b, DWORD c) {
    Ts3nCounters& k = ts3native_counters;
    if (k.lsMode != 1) return g_lsObjOrig(lot, edx, a, b, c);
    const LONGLONG t0 = LsNow();
    const DWORD r = g_lsObjOrig(lot, edx, a, b, c);
    const LONG us = LsUs(t0, LsNow());
    k.objScenes++;
    k.objSceneUs += us;
    LsMax(k.objSceneMaxUs, us);
    k.objSceneHist[us < 100 ? 0 : us < 1000 ? 1 : us < 10000 ? 2 : 3]++;
    if (us >= g_lsObjLogUs) LsEmit("obj", lot, -1, us, a);  // a = the object [inferred from the call shape]
    return r;
}

static bool LsOpenLog(const wchar_t* path) {
    g_lsLog = CreateFileW(path, GENERIC_WRITE, FILE_SHARE_READ, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (g_lsLog == INVALID_HANDLE_VALUE) Log("lot stream: cannot create %ls (err %lu), no event log", path, GetLastError());
    return g_lsLog != INVALID_HANDLE_VALUE;
}

// [LotStream] Enabled=1 hooks; Mode = start value of lsMode; Log=1 writes the event file; SyncWait=1 / ObjScene=1 add the
// optional hooks; ObjLogMs = objScene calls at least this slow go to the event file.
static bool ApplyLotStream(const wchar_t* ini) {
    if (!GetPrivateProfileIntW(L"LotStream", L"Enabled", 0, ini)) { Log("lot stream disabled (Enabled=0)"); return false; }
    if (ApexStandsDown(AXF_LOT, "[LotStream]", "lot streaming sites")) return false;
    LARGE_INTEGER f;
    QueryPerformanceFrequency(&f);
    g_lsUsPerTick = 1e6 / (double)f.QuadPart;
    struct Req { const char* name; DWORD at; const u8* bytes; size_t len; void* hook; void** orig; LONG bit; };
    const Req req[5] = {
        {"lot add execute (lot stream)", LS_ADD, kLsAdd, sizeof(kLsAdd), (void*)LsAddHook, (void**)&g_lsAddOrig, 1},
        {"lot remove execute (lot stream)", LS_REMOVE, kLsRemove, sizeof(kLsRemove), (void*)LsRemoveHook, (void**)&g_lsRemoveOrig, 2},
        {"RunJobs (lot stream)", LS_PUMP, kLsPump, sizeof(kLsPump), (void*)LsPumpHook, (void**)&g_lsPumpOrig, 4},
        {"lot scoring (lot stream)", LS_SCORE, kLsScore, sizeof(kLsScore), (void*)LsScoreHook, (void**)&g_lsScoreOrig, 8},
        {"Lot::SetActive (lot stream)", LS_SET_ACTIVE, kLsSetActive, sizeof(kLsSetActive), (void*)LsSetActiveHook, (void**)&g_lsSetActiveOrig, 16},
    };
    char hex[64];
    for (const Req& r : req)  // all or nothing: a half-hooked path would give numbers that do not add up
        if (!Matches((const void*)(uintptr_t)r.at, r.bytes, r.len)) {
            if (Readable((const void*)(uintptr_t)r.at, r.len)) Hex((const u8*)(uintptr_t)r.at, r.len, hex, sizeof(hex)); else strcpy_s(hex, "<unreadable>");
            Log("SKIP lot stream: %s prologue at 0x%08X differs (found %s), nothing hooked", r.name, r.at, hex);
            return false;
        }
    ts3native_counters.lsMode = GetPrivateProfileIntW(L"LotStream", L"Mode", 1, ini) == 0 ? 0 : 1;
    g_lsObjLogUs = (LONG)GetPrivateProfileIntW(L"LotStream", L"ObjLogMs", 5, ini) * 1000;
    if (GetPrivateProfileIntW(L"LotStream", L"Log", 1, ini)) {
        wchar_t path[MAX_PATH];
        wcscpy_s(path, ini);
        wchar_t* dot = wcsrchr(path, L'.');
        if (dot) *dot = 0;
        wcscat_s(path, L"-lotstream.jsonl");
        LsOpenLog(path);
    }
    LONG ready = 0;
    for (const Req& r : req)
        if ((*r.orig = InstallDetour(r.name, r.at, r.bytes, r.len, r.hook, r.orig)) != NULL) ready |= r.bit;
    if (GetPrivateProfileIntW(L"LotStream", L"SyncWait", 0, ini) &&
        (g_lsSyncOrig = (LsSyncFn)InstallDetour("remote call dispatch (lot stream sync wait)", LS_SYNC, kLsSync, sizeof(kLsSync), (void*)LsSyncHook, &g_lsSyncOrig)) != NULL)
        ready |= 32;
    if (GetPrivateProfileIntW(L"LotStream", L"ObjScene", 0, ini) &&
        (g_lsObjOrig = (LsObjFn)InstallDetour("UpdateObjectSceneNode (lot stream objScene)", LS_OBJ, kLsObj, sizeof(kLsObj), (void*)LsObjHook, &g_lsObjOrig)) != NULL)
        ready |= 64;
    ts3native_counters.lsReady = ready;
    Log("%s lot stream: ready 0x%lX (Mode=%ld, event log %s, ObjLogMs=%ld)", (ready & 31) == 31 ? "OK  " : "FAIL", ready,
        ts3native_counters.lsMode, g_lsLog != INVALID_HANDLE_VALUE ? "on" : "off", g_lsObjLogUs / 1000);
    return ready != 0;
}
