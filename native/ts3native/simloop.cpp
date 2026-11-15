// Sim loop iteration hook (docs/51 §188, I-188-2). Included by ts3native.cpp before monothread.cpp (one translation unit).
//
// MonoScriptHost_Simulate 0xD81840 is not one frame. It is entered once through the vtable slot 0x108B6B0 and loops until
// the host's run flag host+0xC08 drops: the loop-back is `mov ecx,[esi+0xC08]; cmp ecx,1; je 0xD818C0` at 0xD81FF6
// [verified: bytes of the Steam 1.67 exe; the only backward jump to the loop head in 0xD81840-0xD82110; ledger lgTicks
// stayed 1 for a 10 min window on 2026-10-20]. So a hook on the slot (top of Simulate, return of Simulate) runs once per
// session, not per frame. This section retargets that je's rel32 to SlpThunk: the registered callbacks run on the sim
// thread at the end of every iteration that loops, then the jump goes on to the loop head. A run that leaves the loop
// (host stopping) returns through the normal epilogue, where the slot owners' return hooks still fire once.
// The written bytes 0xD82001-0xD82004 are outside the S3SS window 0xD81FC3-0xD81FE0 (docs/68 §4 #2; 30 bytes after it)
// and outside every other known plugin site in Simulate (docs/68 §1, #9). Installed at load, before the sim thread runs.
static const DWORD SLP_SITE = TS3A(0x00D81FF6), SLP_LOOP_TOP = TS3A(0x00D818C0);
static const u8 kSlpSite[11] = {0x8B, 0x8E, 0x08, 0x0C, 0x00, 0x00, 0x83, 0xF9, 0x01, 0x0F, 0x84};  // mov ecx,[esi+0xC08]; cmp ecx,1; je
typedef void(__cdecl* SlpFn)();
static SlpFn g_slpFns[4];
static volatile LONG g_slpN;
static DWORD g_slpBack;  // the je's original target (the loop head)
static LONG g_slpState;  // 0 not tried, 1 installed, -1 refused

static void __cdecl SlpRun() {
    for (LONG i = 0; i < g_slpN; i++) g_slpFns[i]();
}

// reached only when the je is taken (ZF set); every register and flag is kept for the loop head
static __declspec(naked) void SlpThunk() {
    __asm {
        pushad
        pushfd
        call SlpRun
        popfd
        popad
        jmp dword ptr [g_slpBack]
    }
}

// site = the 11 checked bytes, then the je's rel32 (game: SLP_SITE; selftest: a copy whose je points at its own loop head)
static bool SlpInstall(u8* site, DWORD wantTarget) {
    if (!Matches(site, kSlpSite, sizeof(kSlpSite))) return false;
    u8* rel = site + sizeof(kSlpSite);
    const DWORD target = (DWORD)(uintptr_t)(rel + 4) + (DWORD)*(const LONG*)rel;
    if (target != wantTarget) return false;
    g_slpBack = target;
    const LONG nrel = (LONG)((DWORD)(uintptr_t)SlpThunk - (DWORD)(uintptr_t)(rel + 4));
    return Write(rel, &nrel, 4);
}

// An end-of-iteration callback (sim thread). The first call patches the site; false = not hooked (the log says why).
static bool SimLoopAdd(SlpFn fn, const char* who, u8* site = (u8*)(uintptr_t)SLP_SITE, DWORD wantTarget = SLP_LOOP_TOP) {
    if (!g_slpState) {
        g_slpState = SlpInstall(site, wantTarget) ? 1 : -1;
        if (g_slpState > 0) Log("OK   sim loop: je at 0x%08X -> SlpThunk, then 0x%08X (every Simulate iteration)", (DWORD)(uintptr_t)(site + 9), wantTarget);
        else Log("SKIP sim loop: bytes at 0x%08X differ (not the Steam 1.67 Simulate loop-back), nothing written", (DWORD)(uintptr_t)site);
    }
    if (g_slpState < 0 || g_slpN >= 4) { Log("SKIP sim loop: %s not added", who); return false; }
    g_slpFns[g_slpN] = fn;
    InterlockedIncrement(&g_slpN);
    Log("OK   sim loop: %s runs at the end of every Simulate iteration", who);
    return true;
}

// After the sim thread's remote-call pump (docs/51 I-188-3, docs/122 §14.11). The pump is `call [edx+0x24]` at 0xD81FDA,
// JobManager vtable slot 0xFD97C8 (RunJobs 0x59A430, thiscall (record, deadline, nonblock), ret 0xC; docs/36, jobqueue.cpp);
// it returns to 0xD81FDC, and the next call (0xD81FDE) is the frame limiter (EA IdleSimulationCycle 0x7694B0, a timing
// read and a sleep [verified: disassembly of its head]; S3SS Precise retargets that call's rel32). So a callback after
// that one return runs once per frame on the sim thread, after the frame's script, animation, locomotion and remote-call
// work, right before the limiter sleeps: the frame's real idle time. This writes the data slot (0xFD97C8 is outside .text,
// no code bytes, nothing in the S3SS window 0xD81FC3-0xD81FE0) and chains to what it held: RunJobs, or [JobQueue]'s stub.
// Other callers of the slot (main pump, waits) and the sim pump of a non-looping Simulate pass through untouched.
// Only when main != sim: the pump call is skipped when the host has no sim job record (host+0xAC0 == 0, 0xD81FCB).
static const DWORD SPP_SLOT = TS3A(0x00FD97C8), SPP_RUNJOBS = TS3A(0x0059A430), SPP_RET = TS3A(0x00D81FDC);
typedef DWORD(__fastcall* SppRunFn)(void* jm, void* edx, void* rec, DWORD deadline, DWORD nonblock);
static SppRunFn g_sppNext;
static DWORD g_sppRet = SPP_RET;  // selftest: its own call site
static SlpFn g_sppFns[4];
static volatile LONG g_sppN;
static LONG g_sppState;  // 0 not tried, 1 installed, -1 refused

static DWORD __fastcall SppHook(void* jm, void* edx, void* rec, DWORD deadline, DWORD nonblock) {
    const DWORD ret = (DWORD)(uintptr_t)_ReturnAddress();
    const DWORD r = g_sppNext(jm, edx, rec, deadline, nonblock);
    if (ret == g_sppRet)
        for (LONG i = 0; i < g_sppN; i++) g_sppFns[i]();
    return r;
}

// A post-pump callback (sim thread). The first call patches the slot; false = not hooked (the log says why).
static bool SimPumpAdd(SlpFn fn, const char* who, DWORD* slot = (DWORD*)(uintptr_t)SPP_SLOT, DWORD runJobs = SPP_RUNJOBS) {
    if (!g_sppState) {
        const DWORD cur = Readable(slot, 4) ? *slot : 0;
        HMODULE own = NULL, at = NULL;
        const DWORD fl = GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT;
        const bool ours = cur && GetModuleHandleExW(fl, (LPCWSTR)SppHook, &own) && GetModuleHandleExW(fl, (LPCWSTR)(uintptr_t)cur, &at) && at == own;
        const DWORD hook = (DWORD)(uintptr_t)SppHook;
        g_sppNext = (SppRunFn)(uintptr_t)cur;
        g_sppState = (cur == runJobs || ours) && cur != hook && Write(slot, &hook, 4) ? 1 : -1;
        if (g_sppState > 0) Log("OK   sim pump: slot 0x%08X -> SppHook, then 0x%08X (callbacks after the pump returns to 0x%08X)", (DWORD)(uintptr_t)slot, cur, g_sppRet);
        else Log("SKIP sim pump: slot 0x%08X holds 0x%08X (not RunJobs 0x%08X or our stub), nothing written", (DWORD)(uintptr_t)slot, cur, runJobs);
    }
    if (g_sppState < 0 || g_sppN >= 4) { Log("SKIP sim pump: %s not added", who); return false; }
    g_sppFns[g_sppN] = fn;
    InterlockedIncrement(&g_sppN);
    Log("OK   sim pump: %s runs after every sim pump, before the frame limiter", who);
    return true;
}
