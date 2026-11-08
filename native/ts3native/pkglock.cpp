// [PkgLock] (0.36.216, docs/539 §13.9, ledger docs/51 §465, counters v177 pk*): EA's package database lock-order (ABBA)
// deadlock at the main menu. Default OFF (Mode=0). Included by ts3native.cpp after routegate.cpp and eafix.cpp.
//
// The DBPF database D (vtable 0xFFE078) sits at O+8 inside its outer object O (vtable 0xFFE0D0). Two locks [verified
// disassembly, docs/539 §13.9.1]: DL = EA Lock in CS mode at D+0x40 = O+0x48 (Lock_Acquire 0x4E16F0 / Lock_Release
// 0x4E17B0), FL = plain CRITICAL_SECTION at D+0xB8 = O+0xC0. Every reader takes DL -> FL (0x734550 -> D slot 0x50
// 0x734710); the main thread's idle close, O slot 0x18 = 0x7355A0 (thiscall, char force, ret 4, returns al) -> 0x735320,
// takes FL -> DL. A worker's first read of a package the main thread is closing deadlocks both (4 kill dumps, §13.8).
//
// Mode 1 (the shipping mode; option A): O slot 0x18 -> PkCloseHook. When EA's own unlocked prechecks say a close may run
// (O+0x104 == 1, open count O+0xE0 == 1), take DL first with EA's Lock_Acquire and a deadline of 0 (one
// TryEnterCriticalSection, negative when busy). Busy = skip this pass and return 0, the value EA returns for "not idle long
// enough": the update loop 0x737672 ignores it and asks again next update. Got it = the original runs DL -> FL -> DL
// (recursive), then DL is released. The main thread never blocks on DL; force != 0 (no caller found) takes DL blocking.
// Mode 2 (EXPERIMENTAL, option A + C): also D slot 0x50 -> PkBeginHook, which takes DL (blocking) before begin access when
// the calling thread does not own it (the 0x734420 "probe open" variant, never seen in a dump).
// Detect=1 (any mode, also 0): the hooks record {O, thread, start}; a watcher thread checks once a second and logs one
// "[PkgLock] ABBA:" line when a thread inside a hook for 3 s owns FL while another thread owns DL. Read-only.
// ReproWiden=N (TEST ONLY, never in release inis): the close skips the idle interval and holds FL N ms before the original,
// so the FL -> DL window is open on every update (repro pkg-abba, docs/539 §13.9.5); only for the first 300 widened closes
// (the menu reads), so a later world load runs at normal speed. Detect=1 also writes a "pkglock:" counter line each minute.
// Household rule: not applicable (resource system, no Sim). Results equal vanilla: a busy package closes a pass later.

static const DWORD PK_CLOSE_SLOT = 0x00FFE0E8, PK_CLOSE = 0x007355A0;  // O vtable slot 0x18: close if idle
static const DWORD PK_BEGIN_SLOT = 0x00FFE0C8, PK_BEGIN = 0x00734710;  // D vtable slot 0x50: begin access (thiscall, no args)
// sub esp,8; push ebx; push esi; mov esi,ecx; mov ebx,1; cmp [esi+104h],ebx  (the O+0x104 precheck)
static const u8 kPkClose[18] = {0x83, 0xEC, 0x08, 0x53, 0x56, 0x8B, 0xF1, 0xBB, 0x01, 0x00, 0x00, 0x00, 0x39, 0x9E, 0x04, 0x01, 0x00, 0x00};
// push ebx; push ebp; push esi; mov esi,ecx; push edi; lea ebp,[esi+0B8h]  (FL = D+0xB8)
static const u8 kPkBegin[12] = {0x53, 0x55, 0x56, 0x8B, 0xF1, 0x57, 0x8D, 0xAE, 0xB8, 0x00, 0x00, 0x00};

typedef u8(__fastcall* PkCloseFn)(u8* o, void* edx, DWORD force);
typedef u8(__fastcall* PkBeginFn)(u8* d, void* edx);
static PkCloseFn g_pkClose = (PkCloseFn)(uintptr_t)PK_CLOSE;
static PkBeginFn g_pkBegin = (PkBeginFn)(uintptr_t)PK_BEGIN;
// through the game entry, so LockAcquireHook (routegate.cpp) still sees DL; selftest swaps in fakes
static LockAcquireFn g_pkAcq = (LockAcquireFn)(uintptr_t)0x004E16F0;
static LockReleaseFn g_pkRel = (LockReleaseFn)(uintptr_t)0x004E17B0;
static LONG g_pkMode, g_pkDetect;
static DWORD g_pkWidenMs;
static volatile LONG g_pkWidened;  // widened closes so far; the window ends at kPkWidenMax (menu repro, keeps a later world load fast)
static const LONG kPkWidenMax = 300;
static bool PkWiden() { return g_pkWidenMs && g_pkWidened < kPkWidenMax; }
static DWORD g_pkWatchStallMs = 3000;  // selftest shortens it

static CRITICAL_SECTION* PkFl(u8* o) { return (CRITICAL_SECTION*)(o + 0xC0); }
static DWORD PkOwner(const void* cs) { return (DWORD)(uintptr_t)((const CRITICAL_SECTION*)cs)->OwningThread; }
static bool PkDlIsCs(const u8* o) { return o[0x48 + 0x24] != 0; }  // EA Lock kind byte +0x24: CS mode (else not the lock we know)

// Detector table: threads inside a hook. tid -1 = being filled.
struct PkTrack { volatile LONG tid; u8* o; DWORD start; LONG kind; DWORD said; };
static PkTrack g_pkTrack[32];

static int PkTrackIn(u8* o, LONG kind) {
    if (!g_pkDetect) return -1;
    for (int i = 0; i < 32; i++)
        if (InterlockedCompareExchange(&g_pkTrack[i].tid, -1, 0) == 0) {
            g_pkTrack[i].o = o, g_pkTrack[i].start = GetTickCount(), g_pkTrack[i].kind = kind, g_pkTrack[i].said = 0;
            InterlockedExchange(&g_pkTrack[i].tid, (LONG)GetCurrentThreadId());
            return i;
        }
    return -1;  // full: this call is not watched
}
static void PkTrackOut(int i) {
    if (i >= 0) InterlockedExchange(&g_pkTrack[i].tid, 0);
}

// One pass of the watcher; returns the lines written. A cycle line per entry, once.
static int PkWatchPass() {
    int lines = 0;
    const DWORD now = GetTickCount();
    for (int i = 0; i < 32; i++) {
        PkTrack& t = g_pkTrack[i];
        const LONG tid = t.tid;
        if (tid <= 0 || t.said || now - t.start < g_pkWatchStallMs) continue;
        u8* o = t.o;
        const DWORD fl = PkOwner(PkFl(o)), dl = PkOwner(o + 0x48);
        if (fl != (DWORD)tid || !dl || dl == (DWORD)tid || t.tid != tid) continue;
        t.said = 1;
        InterlockedIncrement(&ts3native_counters.pkAbbaSeen);
        Log("[PkgLock] ABBA: tid %ld holds O+0xC0, waits O+0x48 owned by tid %lu (O=%p, in %s for %lu ms, Mode %ld)", tid, dl, o,
            t.kind ? "begin access" : "idle close", now - t.start, g_pkMode);
        lines++;
    }
    return lines;
}
// One summary line when the counters moved since the last one (every 60 s, Detect=1): the job log shows the fix working.
static void PkSummary() {
    static LONG last[8];
    const volatile LONG* c = &ts3native_counters.pkMode;
    if (!memcmp((const void*)c, last, sizeof(last))) return;
    memcpy(last, (const void*)c, sizeof(last));
    Log("pkglock: Mode %ld, idle closes run %ld, deferred %ld (max run %ld), forced %ld, begin unowned %ld (DL taken %ld), ABBA %ld, widened %ld",
        c[0], c[1], c[2], c[7], c[3], c[4], c[5], c[6], g_pkWidened);
}
static DWORD WINAPI PkWatchMain(LPVOID) {
    for (int n = 1;; n++) {
        Sleep(1000);
        PkWatchPass();
        if (n % 60 == 0) PkSummary();
    }
}

// Consecutive defers per package object (main thread only: the update loop runs under the manager lock M+0x218).
// shortcut: 64 direct-mapped rows, a collision restarts that row's run; enough for pkDeferMax as a "never closes" flag.
static struct { u8* o; LONG run; } g_pkRun[64];
static void PkDeferNote(u8* o, bool deferred) {
    auto& r = g_pkRun[((uintptr_t)o >> 4) & 63];
    if (r.o != o) r.o = o, r.run = 0;
    if (!deferred) { r.run = 0; return; }
    if (++r.run > ts3native_counters.pkDeferMax) ts3native_counters.pkDeferMax = r.run;
}

static u8 PkCallClose(u8* o, DWORD force) {
    if (!PkWiden()) return g_pkClose(o, NULL, force);
    if (InterlockedIncrement(&g_pkWidened) == kPkWidenMax) Log("pkglock: ReproWiden window ended after %ld widened closes", kPkWidenMax);
    EnterCriticalSection(PkFl(o));  // TEST ONLY: hold the FL -> DL window open
    Sleep(g_pkWidenMs);
    const u8 r = g_pkClose(o, NULL, force);
    LeaveCriticalSection(PkFl(o));
    return r;
}

static u8 __fastcall PkCloseHook(u8* o, void*, DWORD force) {
    if (*(volatile LONG*)(o + 0x104) != 1 || *(volatile LONG*)(o + 0xE0) != 1 || !PkDlIsCs(o))
        return g_pkClose(o, NULL, force);  // EA's own fast path, no lock (or a lock kind we do not know)
    const bool callerForce = (u8)force != 0;
    if (PkWiden()) force = 1;  // TEST ONLY: close whenever the count says idle
    const int tr = PkTrackIn(o, 0);
    u8 r;
    if (g_pkMode >= 1) {
        if (!callerForce) {
            const DWORD now = 0;  // expired deadline: one TryEnterCriticalSection
            if (g_pkAcq(o + 0x48, NULL, &now) < 0) {
                InterlockedIncrement(&ts3native_counters.pkCloseDefer);
                PkDeferNote(o, true);
                PkTrackOut(tr);
                return 0;  // EA's "not idle long enough": asked again next update
            }
            InterlockedIncrement(&ts3native_counters.pkCloseOk);
        } else {
            static const DWORD inf = 0xFFFFFFFF;
            g_pkAcq(o + 0x48, NULL, &inf);
            InterlockedIncrement(&ts3native_counters.pkCloseForce);
        }
        PkDeferNote(o, false);
        r = PkCallClose(o, force);
        g_pkRel(o + 0x48, NULL);
    } else
        r = PkCallClose(o, force);
    PkTrackOut(tr);
    return r;
}

static u8 __fastcall PkBeginHook(u8* d, void*) {
    if (PkOwner(d + 0x40) == GetCurrentThreadId() || !PkDlIsCs(d - 8)) return g_pkBegin(d, NULL);  // the reader path: DL held
    InterlockedIncrement(&ts3native_counters.pkBeginUnowned);
    const int tr = PkTrackIn(d - 8, 1);
    u8 r;
    if (g_pkMode == 2) {
        static const DWORD inf = 0xFFFFFFFF;
        g_pkAcq(d + 0x40, NULL, &inf);
        InterlockedIncrement(&ts3native_counters.pkBeginLocked);
        r = g_pkBegin(d, NULL);
        g_pkRel(d + 0x40, NULL);
    } else
        r = g_pkBegin(d, NULL);
    PkTrackOut(tr);
    return r;
}

// slot: vtable entry that must name `orig`; on a match the entry names `hook`.
static bool PkSwapSlot(DWORD* slot, DWORD orig, void* hook, const char* what) {
    if (!Readable(slot, 4) || *slot != orig) {
        Log("SKIP pkglock: %s not installed: slot 0x%08X holds 0x%08X (want 0x%08X with EA's bytes)", what,
            (DWORD)(uintptr_t)slot, Readable(slot, 4) ? *slot : 0, orig);
        return false;
    }
    const DWORD h = (DWORD)(uintptr_t)hook;
    return Write(slot, &h, 4);
}

static bool PkInstall(LONG mode, LONG detect, DWORD widenMs, DWORD* closeSlot, DWORD* beginSlot) {
    g_pkMode = mode, g_pkDetect = detect, g_pkWidenMs = widenMs;
    ts3native_counters.pkMode = mode;
    if (!mode && !detect && !widenMs) return false;
    if (!PkSwapSlot(closeSlot, (DWORD)(uintptr_t)g_pkClose, (void*)PkCloseHook, "idle close (O slot 0x18)")) {
        ts3native_counters.pkMode = -1;
        return false;
    }
    if ((detect || mode == 2) && !PkSwapSlot(beginSlot, (DWORD)(uintptr_t)g_pkBegin, (void*)PkBeginHook, "begin access (D slot 0x50)") && mode == 2)
        g_pkMode = 1, ts3native_counters.pkMode = 1;  // Mode 2 without its second hook runs as Mode 1
    return true;
}

static bool ApplyPkgLock(const wchar_t* ini) {
    const LONG mode = GetPrivateProfileIntW(L"PkgLock", L"Mode", 0, ini), detect = GetPrivateProfileIntW(L"PkgLock", L"Detect", 0, ini);
    const DWORD widen = (DWORD)GetPrivateProfileIntW(L"PkgLock", L"ReproWiden", 0, ini);
    if (mode < 0 || mode > 2) {
        Log("SKIP pkglock: [PkgLock] Mode=%ld unknown (0, 1, 2)", mode);
        return false;
    }
    if ((mode || detect || widen) && (!Matches((const void*)(uintptr_t)PK_CLOSE, kPkClose, sizeof(kPkClose)) ||
                                      !Matches((const void*)(uintptr_t)PK_BEGIN, kPkBegin, sizeof(kPkBegin)))) {
        Log("SKIP pkglock: EA's idle close 0x%08X or begin access 0x%08X bytes differ (not the Steam 1.67 exe): not installed", PK_CLOSE, PK_BEGIN);
        ts3native_counters.pkMode = -1;
        return false;
    }
    if (!PkInstall(mode, detect, widen > 1000 ? 1000 : widen, (DWORD*)(uintptr_t)PK_CLOSE_SLOT, (DWORD*)(uintptr_t)PK_BEGIN_SLOT)) return false;
    if (detect) {
        HANDLE t = CreateThread(NULL, 64 * 1024, PkWatchMain, NULL, STACK_SIZE_PARAM_IS_A_RESERVATION, NULL);
        if (t) CloseHandle(t);
    }
    Log("OK   pkglock: [PkgLock] Mode=%ld%s Detect=%ld ReproWiden=%lu%s (docs/539 §13.9): package idle close %s", g_pkMode,
        g_pkMode == 2 ? " (EXPERIMENTAL)" : "", detect, g_pkWidenMs, g_pkWidenMs ? " TEST ONLY" : "",
        g_pkMode ? "tries the database lock first, defers when a reader holds it" : "unchanged (EA order)");
    return true;
}
