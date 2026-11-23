// Mono loader lock check (docs/29 §5, G9). Included by ts3native.cpp (one translation unit). Measurement only.
//
// 0x11F43A8 is Mono's global loader_mutex: mono_loader_init 0xE84880 does InitializeCriticalSection on it plus TlsAlloc,
// and its users are loader.c functions (asserts name loader.c) [verified]. 0xE85020/0xE85030 are mono_loader_lock/unlock
// (push 0x11F43A8; call [Enter/LeaveCriticalSection]; ret). Sampled stacks put ~1.4% of sim-thread time (~14 ms/s) in
// them under 0xDA5971's callback [0x11EE6CC], mostly inside ntdll, unlock 5x more than lock [measured]. That is either a
// very high call rate or contention; this check tells which.
// We replace both wrappers (the original pair is three instructions, so nothing of the game's code runs in between):
// lock = TryEnterCriticalSection first, and only when that fails a timed EnterCriticalSection (contended). All counters are
// written while the lock is held, so plain ++ is exact. Unlock counts whether threads were waiting (Leave must wake one).
// shortcut: the 20 inline EnterCriticalSection sites in loader.c (0xE85040..0xE85E52) are not hooked, so enters and owner
// changes are a lower bound; hook those sites too if the wrapper numbers are inconclusive.

#include <intrin.h>  // _ReturnAddress

static const DWORD IL_LOCK = TS3A(0x00E85020), IL_UNLOCK = TS3A(0x00E85030);
static const u8 kIlLock[11] = {0x68, 0xA8, 0x43, 0x1F, 0x01, 0xFF, 0x15, 0x2C, 0x51, 0xF9, 0x00};    // push 0x11F43A8; call [EnterCriticalSection]
static const u8 kIlUnlock[11] = {0x68, 0xA8, 0x43, 0x1F, 0x01, 0xFF, 0x15, 0x30, 0x51, 0xF9, 0x00};  // push 0x11F43A8; call [LeaveCriticalSection]

static CRITICAL_SECTION* g_ilCs = (CRITICAL_SECTION*)(uintptr_t)TS3A(0x011F43A8);  // variables so the selftest can point them at its own
static const DWORD* g_ilCallbackSlot = (const DWORD*)(uintptr_t)TS3A(0x011EE6CC);
static double g_ilUsPerTick;
static DWORD g_ilEnterLow;    // enters below the next 1024
static DWORD g_ilCallers[32];  // distinct return addresses of lock callers, logged once each
static int g_ilCallerCount;

static void IlCounted(DWORD ret) {
    Ts3nCounters& k = ts3native_counters;
    if (++g_ilEnterLow == 1024) { g_ilEnterLow = 0; k.icLockEntersK++; }
    const LONG tid = (LONG)GetCurrentThreadId();
    if (tid != k.icLockLastTid) {
        if (k.icLockLastTid) k.icLockOwnerChanges++;
        k.icLockPrevTid = k.icLockLastTid;
        k.icLockLastTid = tid;
    }
    if (!k.icLockCallback && *g_ilCallbackSlot) {
        k.icLockCallback = (LONG)*g_ilCallbackSlot;
        Log("ic lock: callback [0x11EE6CC] = 0x%08X (thread %ld)", (DWORD)k.icLockCallback, tid);
    }
    if (g_ilCallerCount < 32) {
        for (int i = 0; i < g_ilCallerCount; i++)
            if (g_ilCallers[i] == ret) return;
        g_ilCallers[g_ilCallerCount++] = ret;
        Log("ic lock: caller 0x%08X thread %ld (main %lu)", ret, tid, g_mainTid);
    }
}

static void __cdecl IlLock() {
    const DWORD ret = (DWORD)(uintptr_t)_ReturnAddress();  // the wrapper is jumped to, so this is its caller
    if (TryEnterCriticalSection(g_ilCs)) { IlCounted(ret); return; }
    LARGE_INTEGER t0, t1;
    QueryPerformanceCounter(&t0);
    EnterCriticalSection(g_ilCs);
    QueryPerformanceCounter(&t1);
    Ts3nCounters& k = ts3native_counters;
    const LONG us = (LONG)((t1.QuadPart - t0.QuadPart) * g_ilUsPerTick);
    k.icLockContended++;
    k.icLockWaitUs += us;  // shortcut: LONG wraps after ~35 min of total wait; fine for one test run
    if (us > k.icLockMaxWaitUs) k.icLockMaxWaitUs = us;
    IlCounted(ret);
}

static void __cdecl IlUnlock() {
    // Vista+ layout: waiting threads = -1 - (LockCount >> 2) (arithmetic shift); read while we still own it
    if (-1 - (g_ilCs->LockCount >> 2) > 0) ts3native_counters.icLockLeaveWaiters++;
    LeaveCriticalSection(g_ilCs);
}

// [IcLockCheck] Enabled=1 replaces both wrappers with the counting ones (same lock, no behaviour change).
static bool ApplyIcLockCheck(const wchar_t* ini) {
    if (!GetPrivateProfileIntW(L"IcLockCheck", L"Enabled", 0, ini)) { Log("ic lock check disabled (Enabled=0)"); return false; }
    LARGE_INTEGER f;
    QueryPerformanceFrequency(&f);
    g_ilUsPerTick = 1e6 / (double)f.QuadPart;
    // both or neither: a counted lock with an uncounted unlock would still be correct, but the numbers would not pair up
    if (!Matches((const void*)(uintptr_t)IL_UNLOCK, kIlUnlock, sizeof(kIlUnlock))) { Log("SKIP ic lock check: unlock bytes differ"); return false; }
    const bool lock = InstallDetour("mono_loader_lock (ic lock check)", IL_LOCK, kIlLock, sizeof(kIlLock), (void*)IlLock) != NULL;
    const bool unlock = lock && InstallDetour("mono_loader_unlock (ic lock check)", IL_UNLOCK, kIlUnlock, sizeof(kIlUnlock), (void*)IlUnlock) != NULL;
    ts3native_counters.icLockReady = (lock ? 1 : 0) | (unlock ? 2 : 0);
    return lock;
}
