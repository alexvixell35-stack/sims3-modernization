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
//   optional Thumb=1 (offload inventory candidate 5, counters v212): ThumbnailManager_CreateThumbnailTexture 0xD5D910
//     (thiscall, 5 args, ret 0x14 [verified asm]; D3D CreateTexture + Lock/fill/Unlock per level, docs/43 §1): calls and
//     time on the sim thread, calls on any other thread. Tells whether the thumbnail fill is worth moving off the sim thread.
// Per event (add, remove, slow obj) one JSON line in <asi>-lotstream.jsonl: {t (UTC ms), kind, lot, lotId, objs, ms,
// passMs (the enclosing pump pass, null outside one)}. Events inside a pass are buffered and written when it ends, so the
// write is never inside a timed span. Runtime switch ts3native_counters.lsMode (+0x2C8): 0 = hooks pass straight through.
// *MaxUs fields are the highest value since the last reset: tools/frida/stutter-profile.js writes 0 after reading them
// each second; with no reader they are all-time maxima.
// shortcut: counters written only by one thread use plain ++ (sim thread: add/remove/pump/sync/obj; main: score); a
// lost update from an unexpected second thread only skews the stats.

static const DWORD LS_ADD = TS3A(0x00ABD3C0), LS_REMOVE = TS3A(0x00ABD3E0), LS_PUMP = TS3A(0x0059A430), LS_SCORE = TS3A(0x00C6C290);
static const DWORD LS_SET_ACTIVE = TS3A(0x00AC20E0), LS_SYNC = TS3A(0x007D98C0), LS_OBJ = TS3A(0x00ABFAC0);
static const DWORD LS_ADD_FN = TS3A(0x00AC1130), LS_REMOVE_FN = TS3A(0x00AB4C00), LS_THUMB = TS3A(0x00D5D910);
static const u8 kLsAdd[7] = {0x56, 0x8B, 0xF1, 0x0F, 0xB6, 0x4E, 0x19};          // push esi; mov esi,ecx; movzx ecx,[esi+0x19]
static const u8 kLsRemove[7] = {0x56, 0x8B, 0xF1, 0x0F, 0xB6, 0x4E, 0x18};       // push esi; mov esi,ecx; movzx ecx,[esi+0x18]
static const u8 kLsPump[8] = {0x53, 0x56, 0x8B, 0xF1, 0x57, 0x8D, 0x7E, 0x08};   // push ebx; push esi; mov esi,ecx; push edi; lea edi,[esi+8]
static const u8 kLsScore[6] = {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF0};              // push ebp; mov ebp,esp; and esp,-16
static const u8 kLsSetActive[5] = {0x53, 0x8A, 0x5C, 0x24, 0x08};                // push ebx; mov bl,[esp+8]
static const u8 kLsSync[6] = {0x51, 0x53, 0x56, 0x57, 0x8B, 0xF1};               // push ecx/ebx/esi/edi; mov esi,ecx
static const u8 kLsThumb[6] = {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF8};             // push ebp; mov ebp,esp; and esp,-8
static const u8 kLsObj[10] = {0x83, 0xEC, 0x0C, 0x83, 0xB9, 0x64, 0x03, 0x00, 0x00, 0x00};  // sub esp,0xC; cmp [ecx+0x364],0

typedef DWORD(__fastcall* LsExecFn)(u8* call, void* edx);
typedef DWORD(__fastcall* LsPumpFn)(void* jm, void* edx, DWORD a, DWORD b, DWORD c);
typedef DWORD(__fastcall* LsScoreFn)(void* wm, void* edx, DWORD a, DWORD b, DWORD c, DWORD d);
typedef DWORD(__fastcall* LsSetActiveFn)(u8* lot, void* edx, DWORD on);
typedef DWORD(__fastcall* LsSyncFn)(void* call, void* edx, DWORD selector, DWORD async);
typedef DWORD(__fastcall* LsObjFn)(u8* lot, void* edx, DWORD a, DWORD b, DWORD c);
typedef DWORD(__fastcall* LsThumbFn)(void* tm, void* edx, DWORD a, DWORD b, DWORD c, DWORD d, DWORD e);
static LsThumbFn g_lsThumbOrig;
static LsExecFn g_lsAddOrig, g_lsRemoveOrig;
static LsPumpFn g_lsPumpOrig;
static LsScoreFn g_lsScoreOrig;
static LsSetActiveFn g_lsSetActiveOrig;
static LsSyncFn g_lsSyncOrig;
static LsObjFn g_lsObjOrig;
// game globals; the selftest points them at its own
static const DWORD* g_lsSimTid = (const DWORD*)(uintptr_t)TS3A(0x011D8A8C);
static const DWORD* g_lsMainTidSlot = (const DWORD*)(uintptr_t)TS3A(0x011D8A88);
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
    if (passUs >= 0) SFmt(pass, "%.3f", passUs / 1000.0); else SCpy(pass, "null");
    AcquireSRWLockExclusive(&g_lsLogLock);
    int len = 0, done = 0;
    for (; done < n; done++) {
        const LsEvent& x = e[done];
        if (x.objs >= 0) SFmt(objs, "%ld", x.objs); else SCpy(objs, "null");
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

// --- CP-1 lot hub (docs/321 §8): always-on lot generation counter + world-change event; no behaviour change ---
// The two execute hooks below are installed by ApplyLotHub on their own (always, [LotHub] Enabled=1 default); [LotStream]
// measurement rides on the same detours. Each run of the add fn 0xAC1130 / remove fn 0xAB4C00 bumps lhLotGen and
// lhAdds / lhRemoves after the game's call returns; the ts3fixes "world:" / "world quit" lines (userdiag.cpp) bump
// lhWorldGen and lhLotGen. Subscribers (CP-3 consumers, each behind its own default-off switch) are called on the thread
// that raised the event: the sim thread for lot add / remove [verified: they run in the sim pump, docs/28 §5.2] and for the
// world lines [verified: UdTextMsg runs on the sim thread, userdiag.cpp]. Readers on other threads poll lhLotGen /
// lhWorldGen (monotonic, interlocked) and compare with the value they cached.
enum { LH_ADD = 1, LH_REMOVE = 2, LH_WORLD_LOAD = 3, LH_WORLD_QUIT = 4 };
typedef void (*LotHubFn)(int kind, const u8* lot, LONG gen, void* ctx);  // lot NULL for world kinds; gen = the bumped lhLotGen
static const int kLhSubsMax = 16;
struct LhSub { LotHubFn fn; void* ctx; };
static LhSub g_lhSubs[kLhSubsMax];
static volatile LONG g_lhSubN;

// Install time only (Apply*, the loader thread, before the game runs); no unsubscribe. False = table full (logged).
static inline bool LotHubSubscribe(LotHubFn fn, void* ctx) {  // inline: no consumer in the asi until CP-3
    const LONG n = g_lhSubN;
    // no leak (docs/321 §8.4): the same fn + ctx again (an Apply* run twice) keeps its one row; the table is fixed at 16
    for (LONG i = 0; i < n; i++) if (g_lhSubs[i].fn == fn && g_lhSubs[i].ctx == ctx) return true;
    if (n >= kLhSubsMax) { Log("lot hub: subscriber table full (%d), one consumer gets no events", kLhSubsMax); return false; }
    g_lhSubs[n].fn = fn;
    g_lhSubs[n].ctx = ctx;
    InterlockedExchange(&g_lhSubN, n + 1);  // publish after the row is written
    return true;
}

// CP-2 managed bridge (docs/321 §9, UserDiag op 49): ts3fixes LotHub.cs drains a queue of lot events once per sim frame.
// Off until ts3fixes asks (its first pop arms it; no managed subscriber = never armed, no work). Sim thread only: lot
// add / remove, the world lines and op 49 all run there (see above), so no lock. A full queue drops the event and marks
// overflow, which the next pop hands out first as one "every lot" event (kind 0) with the queue cleared; a world event
// clears the queue (ts3fixes' OnWorldChange supersedes it).
static const int kLhQ = 64;
struct LhQEv { int kind; DWORD id; };  // id = the lot's low id dword [lot+0x48]
static LhQEv g_lhQ[kLhQ], g_lhQCur;
static int g_lhQHead, g_lhQN;
static bool g_lhQArmed, g_lhQOver;
static void LhQPush(int kind, const u8* lot) {
    if (!g_lhQArmed) return;
    if (kind >= LH_WORLD_LOAD) { g_lhQHead = g_lhQN = 0; g_lhQOver = false; return; }
    if (g_lhQN == kLhQ) { g_lhQOver = true; return; }
    LhQEv& e = g_lhQ[(g_lhQHead + g_lhQN++) % kLhQ];
    e.kind = kind;
    e.id = *(const DWORD*)(lot + 0x48);
}
// op 49, hi & 0xFF: 0 pop the next event into the current one (1 = popped; arms the queue), 1 bit (hi >> 8) of the current
// lot's id, 2 current is a remove, 3 current is the overflow event, 4 hub installed (lhReady != 0).
static u8 LhOp(DWORD hi) {
    switch (hi & 0xFF) {
        case 0:
            g_lhQArmed = true;
            if (g_lhQOver) { g_lhQOver = false; g_lhQHead = g_lhQN = 0; g_lhQCur.kind = 0; g_lhQCur.id = 0; return 1; }
            if (!g_lhQN) return 0;
            g_lhQCur = g_lhQ[g_lhQHead];
            g_lhQHead = (g_lhQHead + 1) % kLhQ;
            g_lhQN--;
            return 1;
        case 1: return (u8)((g_lhQCur.id >> ((hi >> 8) & 31)) & 1);
        case 2: return g_lhQCur.kind == LH_REMOVE ? 1 : 0;
        case 3: return g_lhQCur.kind == 0 ? 1 : 0;
        case 4: return ts3native_counters.lhReady ? 1 : 0;
    }
    return 0;
}

static void LotHubFire(int kind, const u8* lot) {
    Ts3nCounters& k = ts3native_counters;
    LhQPush(kind, lot);
    if (kind >= LH_WORLD_LOAD) InterlockedIncrement(&k.lhWorldGen);
    else InterlockedIncrement(kind == LH_ADD ? &k.lhAdds : &k.lhRemoves);
    const LONG gen = InterlockedIncrement(&k.lhLotGen);
    const LONG n = g_lhSubN;
    if (kind >= LH_WORLD_LOAD || !(gen & 63) || !(gen & (gen - 1))) {  // read-back line: world events, powers of two, every 64th
        static const char* const kName[5] = {"?", "add", "remove", "world load", "world quit"};
        Log("lot hub: %s, lot gen %ld (adds %ld, removes %ld), world gen %ld, subscribers %ld", kName[kind], gen, k.lhAdds, k.lhRemoves, k.lhWorldGen, n);
    }
    for (LONG i = 0; i < n; i++) g_lhSubs[i].fn(kind, lot, gen, g_lhSubs[i].ctx), InterlockedIncrement(&k.lhFired);
}

static DWORD LsExec(u8* call, void* edx, LsExecFn orig, DWORD want, bool add) {
    Ts3nCounters& k = ts3native_counters;
    const DWORD fn = *(const DWORD*)(call + 0x10);
    const u8* lot = *(u8* const*)(call + 0x0C) + *(const int*)(call + 0x14);  // the `this` the execute passes to fn
    if (k.lsMode != 1) {  // [LotStream] off or Mode=0: hub only
        const DWORD r = orig(call, edx);
        if (fn == want) LotHubFire(add ? LH_ADD : LH_REMOVE, lot);
        return r;
    }
    if (fn != want) { LsOtherFn(fn); return orig(call, edx); }
    const LONG objs = (LONG)((*(const DWORD*)(lot + 0x18) - *(const DWORD*)(lot + 0x14)) / 4);
    const LONGLONG t0 = LsNow();
    const DWORD r = orig(call, edx);
    const LONG us = LsUs(t0, LsNow());
    if (add) { k.lotAdds++; k.lotAddUs += us; LsMax(k.lotAddMaxUs, us); k.lotAddObjs += objs; LsMax(k.lotAddMaxObjs, objs); }
    else { k.lotRemoves++; k.lotRemoveUs += us; LsMax(k.lotRemoveMaxUs, us); }
    if (g_lsPumpTid && g_lsPumpTid == GetCurrentThreadId()) g_lsPumpLots++;
    LsEmit(add ? "add" : "remove", lot, objs, us, 0);
    LotHubFire(add ? LH_ADD : LH_REMOVE, lot);
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

static DWORD __fastcall LsThumbHook(void* tm, void* edx, DWORD a, DWORD b, DWORD c, DWORD d, DWORD e) {
    Ts3nCounters& k = ts3native_counters;
    if (k.lsMode != 1) return g_lsThumbOrig(tm, edx, a, b, c, d, e);
    if (GetCurrentThreadId() != *g_lsSimTid) { InterlockedIncrement(&k.thumbOffSim); return g_lsThumbOrig(tm, edx, a, b, c, d, e); }
    const LONGLONG t0 = LsNow();
    const DWORD r = g_lsThumbOrig(tm, edx, a, b, c, d, e);
    const LONG us = LsUs(t0, LsNow());
    k.thumbCalls++;
    k.thumbUs += us;
    LsMax(k.thumbMaxUs, us);
    return r;
}

static bool LsOpenLog(const wchar_t* path) {
    g_lsLog = CreateFileW(path, GENERIC_WRITE, FILE_SHARE_READ, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (g_lsLog == INVALID_HANDLE_VALUE) Log("lot stream: cannot create %ls (err %lu), no event log", path, GetLastError());
    return g_lsLog != INVALID_HANDLE_VALUE;
}

// [LotStream] Enabled=1 hooks; Mode = start value of lsMode; Log=1 writes the event file; SyncWait=1 / ObjScene=1 add the
// optional hooks (Thumb=1 the thumbnail texture timer); ObjLogMs = objScene calls at least this slow go to the event file.
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
    const LONG hub = ts3native_counters.lhReady & 3;  // the hub's detours already are the add / remove hooks (same functions)
    for (const Req& r : req)  // all or nothing: a half-hooked path would give numbers that do not add up
        if (!(hub & r.bit) && !Matches((const void*)(uintptr_t)r.at, r.bytes, r.len)) {
            if (Readable((const void*)(uintptr_t)r.at, r.len)) Hex((const u8*)(uintptr_t)r.at, r.len, hex, sizeof(hex)); else SCpy(hex, "<unreadable>");
            Log("SKIP lot stream: %s prologue at 0x%08X differs (found %s), nothing hooked", r.name, r.at, hex);
            return false;
        }
    ts3native_counters.lsMode = GetPrivateProfileIntW(L"LotStream", L"Mode", 1, ini) == 0 ? 0 : 1;
    g_lsObjLogUs = (LONG)GetPrivateProfileIntW(L"LotStream", L"ObjLogMs", 5, ini) * 1000;
    if (GetPrivateProfileIntW(L"LotStream", L"Log", 1, ini)) {
        wchar_t path[MAX_PATH];
        WCpy(path, ini);
        wchar_t* dot = wcsrchr(path, L'.');
        if (dot) *dot = 0;
        WCat(path, L"-lotstream.jsonl");
        LsOpenLog(path);
    }
    LONG ready = 0;
    for (const Req& r : req)
        if ((hub & r.bit) || (*r.orig = InstallDetour(r.name, r.at, r.bytes, r.len, r.hook, r.orig)) != NULL) ready |= r.bit;
    if (GetPrivateProfileIntW(L"LotStream", L"SyncWait", 0, ini) &&
        (g_lsSyncOrig = (LsSyncFn)InstallDetour("remote call dispatch (lot stream sync wait)", LS_SYNC, kLsSync, sizeof(kLsSync), (void*)LsSyncHook, &g_lsSyncOrig)) != NULL)
        ready |= 32;
    if (GetPrivateProfileIntW(L"LotStream", L"ObjScene", 0, ini) &&
        (g_lsObjOrig = (LsObjFn)InstallDetour("UpdateObjectSceneNode (lot stream objScene)", LS_OBJ, kLsObj, sizeof(kLsObj), (void*)LsObjHook, &g_lsObjOrig)) != NULL)
        ready |= 64;
    if (GetPrivateProfileIntW(L"LotStream", L"Thumb", 0, ini) &&
        (g_lsThumbOrig = (LsThumbFn)InstallDetour("CreateThumbnailTexture (lot stream thumb)", LS_THUMB, kLsThumb, sizeof(kLsThumb), (void*)LsThumbHook, &g_lsThumbOrig)) != NULL)
        ready |= 128;
    ts3native_counters.lsReady = ready;
    Log("%s lot stream: ready 0x%lX (Mode=%ld, event log %s, ObjLogMs=%ld)", (ready & 31) == 31 ? "OK  " : "FAIL", ready,
        ts3native_counters.lsMode, g_lsLog != INVALID_HANDLE_VALUE ? "on" : "off", g_lsObjLogUs / 1000);
    return ready != 0;
}

// [LotHub] Enabled=1 (default; 0 = kill switch): the always-on CP-1 hub hooks. Both prologues are checked before either is
// written, so lhLotGen never counts one direction only. Runs before ApplyLotStream (which reuses these detours) and before
// [Verify] (its lotAddExec probe 65 then finds the site taken and skips itself).
static bool ApplyLotHub(const wchar_t* ini) {
    if (!GetPrivateProfileIntW(L"LotHub", L"Enabled", 1, ini)) { Log("lot hub off ([LotHub] Enabled=0): no lot generation counter"); return false; }
    if (!Matches((const void*)(uintptr_t)LS_ADD, kLsAdd, sizeof(kLsAdd)) || !Matches((const void*)(uintptr_t)LS_REMOVE, kLsRemove, sizeof(kLsRemove))) {
        Log("SKIP lot hub: add / remove execute prologue differs (another hook?), lhLotGen stays 0, consumers keep today's behaviour");
        return false;
    }
    LONG ready = 0;
    if ((g_lsAddOrig = (LsExecFn)InstallDetour("lot add execute (lot hub)", LS_ADD, kLsAdd, sizeof(kLsAdd), (void*)LsAddHook, &g_lsAddOrig)) != NULL) ready |= 1;
    if ((g_lsRemoveOrig = (LsExecFn)InstallDetour("lot remove execute (lot hub)", LS_REMOVE, kLsRemove, sizeof(kLsRemove), (void*)LsRemoveHook, &g_lsRemoveOrig)) != NULL) ready |= 2;
    ts3native_counters.lhReady = ready;
    Log("%s lot hub: ready 0x%lX (lot generation counter + world event, docs/321 §8)", ready == 3 ? "OK  " : "FAIL", ready);
    return ready != 0;
}
