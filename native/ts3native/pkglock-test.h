// TestPkgLock ([PkgLock], docs/539 §13.9.4 selftest (a)-(f)): fake package objects with real CRITICAL_SECTIONs laid out as
// EA's (DL = EA Lock in CS mode at O+0x48, count +0x20, kind byte +0x24; FL at O+0xC0; O+0x104 / O+0xE0 prechecks) and a
// fake original close that takes FL -> DL the way 0x7355A0 -> 0x735320 does.

static LONG __fastcall PkFakeAcq(void* lock, void*, const DWORD* deadline) {
    CRITICAL_SECTION* cs = (CRITICAL_SECTION*)lock;
    if (*deadline == 0xFFFFFFFF) EnterCriticalSection(cs);
    else if (!TryEnterCriticalSection(cs)) return -2;  // EA's expired deadline
    return ++*(LONG*)((u8*)lock + 0x20);
}
static LONG __fastcall PkFakeRel(void* lock, void*) {
    const LONG n = --*(LONG*)((u8*)lock + 0x20);
    LeaveCriticalSection((CRITICAL_SECTION*)lock);
    return n;
}
static volatile LONG g_pktCloses, g_pktDlHeldInClose, g_pktBegins, g_pktDlHeldInBegin;
static u8 __fastcall PkFakeClose(u8* o, void*, DWORD) {
    InterlockedIncrement(&g_pktCloses);
    if (PkOwner(o + 0x48) == GetCurrentThreadId()) InterlockedIncrement(&g_pktDlHeldInClose);
    EnterCriticalSection(PkFl(o));  // EA: FL, then DL at 0x735353
    static const DWORD inf = 0xFFFFFFFF;
    PkFakeAcq(o + 0x48, NULL, &inf);
    PkFakeRel(o + 0x48, NULL);
    LeaveCriticalSection(PkFl(o));
    return 1;
}
static u8 __fastcall PkFakeBegin(u8* d, void*) {
    InterlockedIncrement(&g_pktBegins);
    if (PkOwner(d + 0x40) == GetCurrentThreadId()) InterlockedIncrement(&g_pktDlHeldInBegin);
    return 1;
}

static u8* PkNewObj() {  // leaked on purpose: the deadlock control arm leaves its threads blocked on it
    u8* o = (u8*)VirtualAlloc(NULL, 0x200, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    InitializeCriticalSection((CRITICAL_SECTION*)(o + 0x48));
    InitializeCriticalSection(PkFl(o));
    o[0x48 + 0x24] = 1;
    *(LONG*)(o + 0x104) = 1, *(LONG*)(o + 0xE0) = 1;
    return o;
}

struct PkHold { u8* o; HANDLE got, done; };
static DWORD WINAPI PkHoldDl(LPVOID p) {  // another thread owns DL until `done`
    PkHold* h = (PkHold*)p;
    static const DWORD inf = 0xFFFFFFFF;
    PkFakeAcq(h->o + 0x48, NULL, &inf);
    SetEvent(h->got);
    WaitForSingleObject(h->done, INFINITE);
    PkFakeRel(h->o + 0x48, NULL);
    return 0;
}
static DWORD WINAPI PkCloseLoop(LPVOID p) {
    for (int i = 0; i < 10000; i++) PkCloseHook((u8*)p, NULL, 0);
    return 0;
}
static DWORD WINAPI PkReaderLoop(LPVOID p) {  // EA's readers: DL -> FL
    u8* o = (u8*)p;
    static const DWORD inf = 0xFFFFFFFF;
    for (int i = 0; i < 10000; i++) {
        PkFakeAcq(o + 0x48, NULL, &inf);
        EnterCriticalSection(PkFl(o));
        LeaveCriticalSection(PkFl(o));
        PkFakeRel(o + 0x48, NULL);
    }
    return 0;
}
static DWORD WINAPI PkCloseOnce(LPVOID p) { return PkCloseHook((u8*)p, NULL, 0); }
static DWORD WINAPI PkReaderOnce(LPVOID p) {
    u8* o = (u8*)p;
    static const DWORD inf = 0xFFFFFFFF;
    PkFakeAcq(o + 0x48, NULL, &inf);
    EnterCriticalSection(PkFl(o));  // blocks: the close holds FL and wants DL
    return 0;
}

static void PkZero() {
    memset((void*)&ts3native_counters.pkMode, 0, 8 * sizeof(LONG));
    g_pktCloses = g_pktDlHeldInClose = g_pktBegins = g_pktDlHeldInBegin = 0;
}

static void TestPkgLock() {
    const PkCloseFn sClose = g_pkClose;
    const PkBeginFn sBegin = g_pkBegin;
    const LockAcquireFn sAcq = g_pkAcq;
    const LockReleaseFn sRel = g_pkRel;
    g_pkClose = PkFakeClose, g_pkBegin = PkFakeBegin, g_pkAcq = PkFakeAcq, g_pkRel = PkFakeRel;

    // (e) slot value mismatch -> not installed, pkMode -1; match -> the slot names the hook
    PkZero();
    DWORD slot = 0x12345678, bslot = (DWORD)(uintptr_t)PkFakeBegin;
    assert(!PkInstall(1, 0, 0, &slot, &bslot) && slot == 0x12345678 && ts3native_counters.pkMode == -1);
    slot = (DWORD)(uintptr_t)PkFakeClose;
    assert(PkInstall(2, 1, 0, &slot, &bslot) && slot == (DWORD)(uintptr_t)PkCloseHook && bslot == (DWORD)(uintptr_t)PkBeginHook &&
           g_pkMode == 2 && ts3native_counters.pkMode == 2);
    assert(!PkInstall(0, 0, 0, &slot, &bslot));  // all off: nothing to install

    // (a) Mode 1, DL held by another thread -> 0, original not called (FL never entered), deferred
    PkZero();
    g_pkMode = 1, g_pkDetect = 0, g_pkWidenMs = 0;
    u8* o = PkNewObj();
    PkHold h = {o, CreateEventW(NULL, TRUE, FALSE, NULL), CreateEventW(NULL, TRUE, FALSE, NULL)};
    HANDLE t = CreateThread(NULL, 0, PkHoldDl, &h, 0, NULL);
    WaitForSingleObject(h.got, INFINITE);
    assert(PkCloseHook(o, NULL, 0) == 0 && PkCloseHook(o, NULL, 0) == 0);
    assert(g_pktCloses == 0 && ts3native_counters.pkCloseDefer == 2 && ts3native_counters.pkDeferMax == 2 && PkOwner(PkFl(o)) == 0);
    SetEvent(h.done);
    WaitForSingleObject(t, INFINITE);
    CloseHandle(t);

    // (b) DL free -> the original runs with DL held, DL count back to 0, the defer run restarts
    assert(PkCloseHook(o, NULL, 0) == 1 && g_pktCloses == 1 && g_pktDlHeldInClose == 1 && ts3native_counters.pkCloseOk == 1);
    assert(*(LONG*)(o + 0x48 + 0x20) == 0 && PkOwner(o + 0x48) == 0 && g_pkRun[((uintptr_t)o >> 4) & 63].run == 0);

    // (c) precheck false -> the original, no DL taken
    *(LONG*)(o + 0xE0) = 3;  // a reader is in
    assert(PkCloseHook(o, NULL, 0) == 1 && g_pktCloses == 2 && g_pktDlHeldInClose == 1 && ts3native_counters.pkCloseOk == 1);
    *(LONG*)(o + 0xE0) = 1;

    // (d) force 1 -> DL taken blocking first
    assert(PkCloseHook(o, NULL, 1) == 1 && g_pktCloses == 3 && g_pktDlHeldInClose == 2 && ts3native_counters.pkCloseForce == 1);

    // Mode 0 -> pass-through in EA order (DL not taken by the hook)
    g_pkMode = 0;
    assert(PkCloseHook(o, NULL, 0) == 1 && g_pktCloses == 4 && g_pktDlHeldInClose == 2);

    // begin access: owner passes through; non-owner counted, Mode 2 takes DL first
    PkZero();
    u8* d = o + 8;
    g_pkMode = 1;
    assert(PkBeginHook(d, NULL) == 1 && g_pktBegins == 1 && g_pktDlHeldInBegin == 0 && ts3native_counters.pkBeginUnowned == 1 &&
           ts3native_counters.pkBeginLocked == 0);
    g_pkMode = 2;
    assert(PkBeginHook(d, NULL) == 1 && g_pktDlHeldInBegin == 1 && ts3native_counters.pkBeginUnowned == 2 && ts3native_counters.pkBeginLocked == 1);
    static const DWORD inf = 0xFFFFFFFF;
    PkFakeAcq(d + 0x40, NULL, &inf);  // a reader that holds DL: pass-through
    assert(PkBeginHook(d, NULL) == 1 && g_pktBegins == 3 && ts3native_counters.pkBeginUnowned == 2);
    PkFakeRel(d + 0x40, NULL);
    assert(*(LONG*)(o + 0x48 + 0x20) == 0);

    // (f) Mode 1: idle close against DL -> FL readers, 10k rounds each: no deadlock (30 s bound)
    PkZero();
    g_pkMode = 1;
    u8* o2 = PkNewObj();
    HANDLE th[2] = {CreateThread(NULL, 0, PkCloseLoop, o2, 0, NULL), CreateThread(NULL, 0, PkReaderLoop, o2, 0, NULL)};
    const DWORD w = WaitForMultipleObjects(2, th, TRUE, 30000);  // a deadlock never ends; 30 s keeps slow CI runners green
    assert(w != WAIT_TIMEOUT);
    CloseHandle(th[0]), CloseHandle(th[1]);
    assert(ts3native_counters.pkCloseOk + ts3native_counters.pkCloseDefer == 10000 && PkOwner(o2 + 0x48) == 0);
    printf("selftest pkglock: Mode 1 10k closes vs 10k readers: %ld ran, %ld deferred, max run %ld\n", ts3native_counters.pkCloseOk,
           ts3native_counters.pkCloseDefer, ts3native_counters.pkDeferMax);

    // ReproWiden window: ends after kPkWidenMax widened closes
    g_pkMode = 1, g_pkWidenMs = 1, g_pkWidened = kPkWidenMax - 1;
    assert(PkWiden() && PkCloseHook(o, NULL, 0) == 1 && g_pkWidened == kPkWidenMax && !PkWiden());
    g_pkWidened = 0, g_pkWidenMs = 0;
    PkSummary();  // no log file in the selftest: must not fault

    // (f) control + detector: Mode 0, ReproWiden 300 ms, a reader takes DL inside the window -> EA's ABBA, caught once
    PkZero();
    g_pkMode = 0, g_pkDetect = 1, g_pkWidenMs = 300;
    u8* o3 = PkNewObj();
    HANDLE a = CreateThread(NULL, 0, PkCloseOnce, o3, 0, NULL);
    while (!PkOwner(PkFl(o3))) Sleep(1);  // the close holds FL, sleeping in the widened window
    HANDLE b = CreateThread(NULL, 0, PkReaderOnce, o3, 0, NULL);
    assert(WaitForSingleObject(a, 2000) == WAIT_TIMEOUT);  // deadlocked (vanilla order), as expected
    g_pkWatchStallMs = 200;
    assert(PkWatchPass() == 1 && ts3native_counters.pkAbbaSeen == 1 && PkWatchPass() == 0);  // one line per stuck call
    // a Mode 1 close on another object is not reported and not stuck
    g_pkMode = 1, g_pkWidenMs = 0;
    assert(PkCloseHook(o, NULL, 0) == 1 && PkWatchPass() == 0);
    g_pkWatchStallMs = 3000, g_pkDetect = 0, g_pkMode = 0;
    CloseHandle(a), CloseHandle(b);  // both stay blocked on o3 until the process exits; their table entry stays used

    g_pkClose = sClose, g_pkBegin = sBegin, g_pkAcq = sAcq, g_pkRel = sRel;
    PkZero();
    printf("selftest pkglock ok (try-lock defer, run with DL, precheck, force, slot check, begin owner / Mode 2, 10k-round no deadlock, ABBA detector)\n");
}
