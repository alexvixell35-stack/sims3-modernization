// [JobQueue]: JobManager fixes from docs/36 §8 (C2, C3) and docs/46 §7 (RJ3, RJ4). Included by ts3native.cpp (one translation unit).
//
// Facts this relies on [verified disassembly, Steam 1.67]:
//   - A job thread record keeps its mask at rec+0x2C. Bit 31 = "also takes jobs from the shared queue" (mask -1 jobs).
//     JobManager_RunJobsLocked 0x599E60 reads bit 31 once at entry (shr eax,1Fh at 0x599E70) and keeps it for the whole pass.
//   - The sim thread's record is created at 0xD7E9F6 with mask 0x80000004, only when main != sim; when main == sim the main
//     record gets 0x80000005 instead (0x5992F8). The sim pump is `call [edx+0x24]` at 0xD81FDA = JobManager vtable 0xFD97A4
//     slot +0x24 (data 0xFD97C8 -> RunJobs 0x59A430, thiscall (record, deadline, nonblock), ret 0xC).
//   - JobManager_ReadTimeslices 0x5992D0 calls JobManager_Init with `push 1` (0x5992DD); Init makes one OS worker
//     (JobThread%i, mask 0x80000000) per set bit of that argument (loop 0x5991A0-0x5991C4).
//   - JobManager_ExecuteJob 0x599720 (thiscall, arg = job): fn at job+0x10, mask at job+0x28. Frame jobs (docs/46 §2) are the
//     fns 0x6F3C30, 0x6EEB30, 0x6EDA60, 0x6EEB50, 0x6F3C50, all mask -1.
//
// #13 SimSkipShared: the slot stub clears bit 31 of the sim record before each sim pump pass (runtime switch jqSimMode, so
//     --ab can flip it in one load) and puts it back when the switch is off. Shared jobs then run on JobThread0..n and the
//     main pump only. The vtable slot is used instead of an entry detour so [LotStream] and [Verify] keep their 0x59A430 hooks,
//     and the call site 0xD81FDA stays untouched (S3SS signature window, docs/68 §4 #2).
// #14 Workers: more generic workers (init-time; the shared queue already has up to three consumers, so no new concurrency
//     class [inferred]).
// #21 (render-job private mask, docs/46 RJ2) is not built: ReadyJob 0x599FC0 gives a job with a specific mask to the FIRST
//     matching record only, so a private bit on JobThread0 + main would serialise all culls on one thread. #13 removes frame
//     jobs from the sim pump as a side effect; jqSimRenderJobs checks it.

static const DWORD JQ_VTBL_RUNJOBS = 0x00FD97C8, JQ_RUNJOBS = 0x0059A430, JQ_EXECUTE = 0x00599720, JQ_INIT_PUSH = 0x005992DD;
static const u8 kJqExecPrologue[7] = {0x53, 0x55, 0x56, 0x8B, 0x74, 0x24, 0x10};  // push ebx; push ebp; push esi; mov esi,[esp+10h]
static const u8 kJqInitPush[6] = {0x6A, 0x01, 0x8B, 0xCE, 0xFF, 0xD2};             // push 1; mov ecx,esi; call edx
static const DWORD kJqRenderFns[5] = {0x006F3C30, 0x006EEB30, 0x006EDA60, 0x006EEB50, 0x006F3C50};
static const DWORD* g_jqSimTid = (const DWORD*)(uintptr_t)0x011D8A8C;  // sim thread id (game global, 0 until set)

typedef DWORD(__fastcall* JqRunJobsFn)(void* jm, void* edx, u8* rec, DWORD deadline, DWORD nonblock);
static JqRunJobsFn g_jqRunJobsOrig;
static void* g_jqExecOrig;
static volatile LONG g_jqTids[8];  // non-main, non-sim threads that ran a shared job

// Sim record mask for the switch: bit 31 off while jqSimMode is on. Only the dedicated sim record (low bits == 4) is touched.
static DWORD JqSimMask(DWORD mask, LONG mode) {
    if ((mask & 0x7FFFFFFF) != 4) return mask;
    return mode ? 4 : 0x80000004;
}

static DWORD __fastcall JqRunJobsHook(void* jm, void* edx, u8* rec, DWORD deadline, DWORD nonblock) {
    Ts3nCounters& k = ts3native_counters;
    const DWORD sim = *g_jqSimTid;
    if (rec && sim && GetCurrentThreadId() == sim) {
        volatile LONG* m = (volatile LONG*)(rec + 0x2C);
        const DWORD want = JqSimMask((DWORD)*m, k.jqSimMode);
        if ((want & 0x7FFFFFFF) == 4) {
            k.jqSimPumps++;  // sim thread only: no interlock needed
            if ((DWORD)*m != want) *m = (LONG)want;  // aligned 32-bit store; readers take either value under the JobManager lock
            if (!(want & 0x80000000)) k.jqSimSkipPasses++;
        }
    }
    return g_jqRunJobsOrig(jm, edx, rec, deadline, nonblock);
}

static void __cdecl JqNote(const u8* job) {
    const DWORD fn = *(const DWORD*)(job + 0x10);
    if (!fn) return;
    Ts3nCounters& k = ts3native_counters;
    const DWORD tid = GetCurrentThreadId(), sim = *g_jqSimTid;
    const bool shared = *(const LONG*)(job + 0x28) == -1;
    bool render = false;
    for (DWORD f : kJqRenderFns) render |= f == fn;
    if (render) InterlockedIncrement(&k.jqRenderJobs);
    if (sim && tid == sim) {
        if (shared) InterlockedIncrement(&k.jqSimSharedJobs);
        if (render) InterlockedIncrement(&k.jqSimRenderJobs);
        return;
    }
    if (!shared || tid == g_mainTid) return;
    InterlockedIncrement(&k.jqWorkerJobs);
    for (int i = 0; i < 8; i++) {
        LONG t = g_jqTids[i];
        if (!t && (t = InterlockedCompareExchange(&g_jqTids[i], (LONG)tid, 0)) == 0) { InterlockedIncrement(&k.jqWorkerTids); return; }
        if (t == (LONG)tid) return;  // slot taken by another thread: keep looking
    }
}

// ExecuteJob entry: [esp] = return, [esp+4] = job. Registers and flags kept, then the game's code.
static __declspec(naked) void JqExecStub() {
    __asm {
        pushad
        pushfd
        push dword ptr [esp + 40]
        call JqNote
        add esp, 4
        popfd
        popad
        jmp dword ptr [g_jqExecOrig]
    }
}

// JobManager_Init argument for n generic workers (one bit per JobThread%i), n clamped to 1-4.
static u8 JqWorkerBits(int n) { return (u8)((1u << Clamp(n, 1, 4)) - 1); }

static bool ApplyJobQueue(const wchar_t* ini) {
    Ts3nCounters& k = ts3native_counters;
    if (!GetPrivateProfileIntW(L"JobQueue", L"Enabled", 0, ini)) { Log("job queue disabled (Enabled=0)"); return false; }
    const int skip = GetPrivateProfileIntW(L"JobQueue", L"SimSkipShared", 0, ini);
    k.jqSimMode = skip == 1 ? 1 : 0;
    // #13: slot stub whenever SimSkipShared is 1 (on) or 2 (installed, starts off for --ab)
    DWORD* slot = (DWORD*)(uintptr_t)JQ_VTBL_RUNJOBS;
    if (skip == 1 || skip == 2) {
        if (!Readable(slot, 4) || *slot != JQ_RUNJOBS) {
            Log("SKIP job queue sim pump: vtable slot 0x%08X holds 0x%08X, not RunJobs 0x%08X", JQ_VTBL_RUNJOBS,
                Readable(slot, 4) ? *slot : 0, JQ_RUNJOBS);
        } else {
            g_jqRunJobsOrig = (JqRunJobsFn)(uintptr_t)JQ_RUNJOBS;
            const DWORD hook = (DWORD)(uintptr_t)JqRunJobsHook;
            if (Write(slot, &hook, 4)) {
                k.jqReady |= 1;
                Log("OK   job queue sim pump: vtable slot 0x%08X -> stub (SimSkipShared start %ld)", JQ_VTBL_RUNJOBS, k.jqSimMode);
            }
        }
    }
    // #14: generic worker count, read once by JobManager_Init after DllMain
    const int workers = GetPrivateProfileIntW(L"JobQueue", L"Workers", 1, ini);
    if (workers != 1) {
        u8* p = (u8*)(uintptr_t)JQ_INIT_PUSH;
        const u8 bits = JqWorkerBits(workers);
        if (!Matches(p, kJqInitPush, sizeof(kJqInitPush))) Log("SKIP job queue workers: bytes at 0x%08X differ", JQ_INIT_PUSH);
        else if (Write(p + 1, &bits, 1)) {
            k.jqReady |= 4;
            k.jqWorkers = Clamp(workers, 1, 4);
            Log("OK   job queue workers: JobManager_Init arg 1 -> 0x%X (%ld generic workers)", bits, k.jqWorkers);
        }
    }
    g_jqExecOrig = InstallDetour("ExecuteJob (job queue counters)", JQ_EXECUTE, kJqExecPrologue, sizeof(kJqExecPrologue), (void*)JqExecStub, &g_jqExecOrig);
    if (g_jqExecOrig) k.jqReady |= 2;
    return k.jqReady != 0;
}
