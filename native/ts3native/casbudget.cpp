// [CasFrameBudget] (docs/534, counters v912 cfb*): a time budget for the CAS SimService frame step in live play (docs/42
// CAS2). Included by ts3native.cpp (one translation unit). Off by default.
//
// SimService_Update 0x5F0E50 (vtable 0xFE514C+0x1C, main thread, thiscall, ret 8) runs queued Sim build steps until a step
// is not ready, the list ends or a QPC deadline passes. The deadline is [this+0x2C] (unsigned, Stopwatch mode-4 units) when
// byte [this+0x139] is set, else 0xFFFFFFFF = none (0x5F0EBF..0x5F0ED0, then Stopwatch_SetDeadline 0x4F34F0) [verified
// bytes]. Only IC_CASUtils_SimServiceEnableTimeSlicing 0x5A50F0 writes +0x139 and no game script calls it [verified:
// script call graph], so every frame may run every queued step. +0x139 is read only at 0x5F0EBF in this function.
// 0x5F0ECD / 0x5F0ED0 are both jump targets, so the hook is an entry detour (prologue push ebp / mov ebp,esp /
// and esp,-16, no relative operand). With cfbMs > 0, the game clock's script pause bit clear and +0x139 still 0, it sets
// +0x139 = 1 and +0x2C = cfbMs, calls EA, then puts both back (a field someone changed during the call is kept, cfbForeign).
// Live only: pause bit 1 of the game clock 0x11CDD48 (+0x40, docs/85 §3) is cleared by LiveModeState.Startup and set by
// LiveModeState.Shutdown [verified: script call graph], so CAS, Build/Buy, Edit Town and the world load keep EA's step.
// shortcut: a player pause may also set bit 1 (Gameflow.SimulateTick calls Pause, inferred); then EA's step runs too.

typedef void(__fastcall* CfbUpdateFn)(void* self, void* edx, DWORD a, DWORD b);
static CfbUpdateFn g_cfbTramp;
static const DWORD kCfbAt = 0x005F0E50;
static const u8 kCfbPro[6] = {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF0};  // push ebp; mov ebp,esp; and esp,-16
static const volatile DWORD* g_cfbPause = (const volatile DWORD*)(uintptr_t)(0x011CDD48 + 0x40);  // selftest: a fake clock
static LONG g_cfbScaleLogged;
static LONG g_cfbEnterLogged, g_cfbPausedLogged, g_cfbArmedLogged;  // one log line each: why a call ran unbudgeted (al267: cfbCalls 0)

// Set the budget on SimService `ss` when it applies; `old` gets what to put back. True when set.
static bool CfbArm(u8* ss, DWORD pauseBits, LONG ms, DWORD old[2]) {
    if (ms <= 0 || (pauseBits & 1) || ss[0x139]) return false;
    old[0] = ss[0x139], old[1] = *(DWORD*)(ss + 0x2C);
    *(DWORD*)(ss + 0x2C) = (DWORD)ms;
    ss[0x139] = 1;
    return true;
}

// Put back each field that still holds our value; a field changed during the call is left as found and counted.
static void CfbDisarm(u8* ss, LONG ms, const DWORD old[2]) {
    bool foreign = false;
    if (ss[0x139] == 1) ss[0x139] = (u8)old[0]; else foreign = true;
    if (*(DWORD*)(ss + 0x2C) == (DWORD)ms) *(DWORD*)(ss + 0x2C) = old[1]; else foreign = true;
    if (foreign) InterlockedIncrement(&ts3native_counters.cfbForeign);
}

static void __fastcall CfbHook(void* self, void* edx, DWORD a, DWORD b) {
    Ts3nCounters& k = ts3native_counters;
    const LONG ms = k.cfbMs;
    DWORD old[2];
    const DWORD pause = *g_cfbPause;
    if (!InterlockedExchange(&g_cfbEnterLogged, 1)) Log("cas frame budget: first call, pause bits 0x%lX, +0x139 = %u", pause, self ? ((u8*)self)[0x139] : 0xFFu);
    if (!self || !CfbArm((u8*)self, pause, ms, old)) {
        if (self && ms > 0 && !(pause & 1) && ((u8*)self)[0x139] && !InterlockedExchange(&g_cfbArmedLogged, 1))
            Log("cas frame budget: not paused but +0x139 = %u already (someone else budgets), +0x2C = %lu", ((u8*)self)[0x139], *(DWORD*)((u8*)self + 0x2C));
        if (self && ms > 0 && (pause & 1) && !InterlockedExchange(&g_cfbPausedLogged, 1)) Log("cas frame budget: first paused call (pause bits 0x%lX): unbudgeted", pause);
        g_cfbTramp(self, edx, a, b);
        return;
    }
    // once: the budget in ms from EA's mode-4 scale (Stopwatch_SetClockMode 0x4F30E0 reads it from 0x11CB8FC [verified];
    // SetDeadline adds budget / scale QPC ticks), so the log confirms the unit
    if (!InterlockedExchange(&g_cfbScaleLogged, 1) && Readable((const void*)(uintptr_t)0x011CB8FC, 4)) {
        LARGE_INTEGER f;
        QueryPerformanceFrequency(&f);
        const float scale = *(const float*)(uintptr_t)0x011CB8FC;
        Log("cas frame budget: first budgeted call, %ld units = %.3f ms (mode 4 scale %g)", ms, scale > 0 ? ms / (scale * f.QuadPart) * 1000.0 : -1.0, scale);
    }
    LARGE_INTEGER t0, t1, f;
    QueryPerformanceCounter(&t0);
    __try { g_cfbTramp(self, edx, a, b); }
    __finally { CfbDisarm((u8*)self, ms, old); }  // an exception out of the step must not leave the budget armed
    QueryPerformanceCounter(&t1);
    QueryPerformanceFrequency(&f);
    InterlockedIncrement(&k.cfbCalls);
    if ((t1.QuadPart - t0.QuadPart) * 1000 >= (LONGLONG)ms * f.QuadPart) InterlockedIncrement(&k.cfbHits);
}

static bool ApplyCasFrameBudget(const wchar_t* ini) {
    if (!GetPrivateProfileIntW(L"CasFrameBudget", L"Enabled", 0, ini)) { Log("cas frame budget off (Enabled=0)"); return false; }
    const int ms = Clamp((int)GetPrivateProfileIntW(L"CasFrameBudget", L"BudgetMs", 8, ini), 0, 1000);
    if (!InstallDetour("SimService_Update (cas frame budget)", kCfbAt, kCfbPro, sizeof(kCfbPro), (void*)CfbHook, &g_cfbTramp)) {
        Log("SKIP cas frame budget: SimService_Update 0x%08lX prologue differs (another mod owns it)", kCfbAt);
        return true;
    }
    ts3native_counters.cfbMs = ms;
    Log("OK   cas frame budget: SimService_Update gets %d ms per frame in live play (cfbMs, 0 = EA's unlimited step)", ms);
    return true;
}
