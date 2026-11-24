// [CrashForensics] CullRaceWatch (default 1, logging only) + [EaCrashFixes] CullDrainWait (default 1 since m1124) (docs/338, counters
// v225 cr* / cdw*, ledger docs/51 §605). Included by ts3native.cpp after savewrite.cpp (PatchCalls).
//
// The suspected EA race (v0.3.1 crash on EA 1.69, 2026-11-23) [verified disassembly, Steam 1.67]:
//   - Scene_DrainIndexList 0x6E4720 (thiscall scene): for each idx in [scene+0x40, scene+0x44): obj = [scene+4][idx]; if obj
//     obj->vfunc+0x74(); then +0x44 = +0x40. The fault 0x6E4735 is `mov ecx,[ecx+eax*4]` with eax = the index read from the
//     list: a heap pointer there means the list buffer was freed and reused.
//   - Writers: Scene_CullQuadtree 0x6E4F90 (inlined push_back at 0x6E53CD and 0x6E567B) and Scene_CullLinear 0x6E5880 (0x6E5B6B) append
//     to the same vector (+0x40 begin, +0x44 end, +0x48 cap) with no lock; growth goes through 0x8621C0, which allocates the
//     new buffer (0x4E3F90), copies, and frees the old one (0x4E3F70 at 0x8622A9).
//   - Renderer_DrawScene 0x6F3F90: when multithreaded cull is on (+0x28F) it waits 310 (if +0x28E), 314 (if +0x294), 320
//     (if +0x29C) with JobManager_Wait 0x59A500, never 318 (the main-view cull, RenderJob_MainCullFn 0x6F3C30 -> call
//     0x6F1B00 at 0x6F3C3B), then drains at 0x6F401B. Its siblings wait for 318 before their drain: DrawMainPass 0x6F1F18,
//     SceneManager_Draw 0x6F3CD8. 310/314/320 depend on 318, so a wait on any of them covers 318; with all three flags off
//     nothing does [inferred: lighting 1 / reflections 0 turn them off].
//   - Job state: handle = job+8, state at job+0x24 = handle+0x1C; EA's wait returns at 0/1/6/7 (docs/36), 3 = queued, 4 = running; only 4 is a race.
//
// Hooks (call-site retargets, each byte-checked by PatchCalls; nothing else written):
//   0x6F401B call 0x6E4720 -> CrDrainStub: CrBefore(scene = ecx, renderer = esi), then the game's drain (always).
//   0x6F3C3B call 0x6F1B00 -> CrCullHook (watch only): records the thread running the main cull, so a hit names it.
// Watch: at the drain, if +0x28F and job 318's state is 4 (running) -> crRace (+ the cull's thread when it is inside 0x6F1B00
// now); any idx >= object count ([scene+8]-[scene+4])/4 -> crBadIdx. Logged on the first hit and every LogEvery-th.
// Fix: CullDrainWait=1 calls EA's own JobManager_Wait(318) before the drain (EA's draw passes wait for it right after, so the
// cost is the wait moved earlier); cdwWaits / cdwWaitUs show it. Renderer-only: no Sim state, household rules do not apply.

static const DWORD CR_DRAIN_SITE = TS3A(0x006F401B), CR_DRAIN = TS3A(0x006E4720);
static const DWORD CR_CULL_SITE = TS3A(0x006F3C3B), CR_CULL = TS3A(0x006F1B00);
static const DWORD CR_JM_WAIT = TS3A(0x0059A500);
static const u8 kCrWaitPrologue[10] = {0x85, 0xC9, 0x53, 0x56, 0x57, 0x74, 0x07, 0x8D, 0x59, 0xF8};  // test ecx,ecx; push x3; jz; lea ebx,[ecx-8]

typedef void(__fastcall* CrWaitFn)(DWORD handle);
typedef DWORD(__fastcall* CrCullFn)(void* renderer, void* edx);  // thiscall, no stack args (call sites push nothing)
static DWORD g_crDrainOrig;
static CrCullFn g_crCullOrig;
static CrWaitFn g_cdwWait;          // EA's JobManager_Wait; a fake in the selftest
static volatile LONG g_crCullTid;   // thread inside the main cull now (0 = none)
static LONG g_crLogEvery = 64, g_cdwMode, g_crWatch;
static LARGE_INTEGER g_crQpf;

static DWORD CrSimTid() {  // game global, 0 until set; unmapped in the selftest
    __try { return *(const DWORD*)(uintptr_t)TS3A(0x011D8A8C); } __except (EXCEPTION_EXECUTE_HANDLER) { return 0; }
}

// Returns bit 0 = job 318 running, state 4 (race window), bit 1 = an index past the object array. raceState / bad* for the log.
static int CrCheck(const u8* scene, const u8* r, LONG* raceState, DWORD* bad, LONG* badAt, LONG* count, LONG* listLen) {
    int hit = 0;
    __try {
        if (r[0x28F]) {
            const u8* h = *(u8* const*)(r + 0x318);
            if (h) {
                *raceState = *(const LONG*)(h + 0x1C);
                if (*raceState == 4) hit |= 1;  // 4 = running; 3 = queued, not started (JobManager_ReadyJob stores 3, EA-verified): no overlap
            }
        }
        const DWORD* objs = *(DWORD* const*)(scene + 4);
        const DWORD* objEnd = *(DWORD* const*)(scene + 8);
        const DWORD* b = *(DWORD* const*)(scene + 0x40);
        const DWORD* e = *(DWORD* const*)(scene + 0x44);
        *count = (LONG)(objEnd - objs);
        *listLen = (LONG)(e - b);
        if (*listLen > 0 && *listLen <= (1 << 20))  // shortcut: a list longer than 1M is itself garbage; not walked
            for (LONG i = 0; i < *listLen; i++)
                if (b[i] >= (DWORD)*count) {
                    *bad = b[i], *badAt = i, hit |= 2;
                    break;
                }
    } __except (EXCEPTION_EXECUTE_HANDLER) { hit |= 2, *badAt = -1; }  // unreadable list = the stale buffer itself
    return hit;
}

extern "C" void __cdecl CrBefore(const u8* scene, const u8* r) {
    Ts3nCounters& k = ts3native_counters;
    if (InterlockedIncrement(&k.crDrains) == 1)  // no +0x28F = culls run inline in DrawScene: no race possible
        Log("cullrace: first DrawScene drain on tid %lu, multithreaded cull (+0x28F) %d, shadow %d, reflection %ld, shadow2 %d",
            GetCurrentThreadId(), r[0x28F], r[0x28E], *(const LONG*)(r + 0x294), r[0x29C]);
    LONG state = -1, badAt = 0, count = 0, len = 0;
    DWORD bad = 0;
    const DWORD self = GetCurrentThreadId(), cullTid = (DWORD)g_crCullTid;
    const int hit = g_crWatch ? CrCheck(scene, r, &state, &bad, &badAt, &count, &len) : 0;
    if (hit & 1) {
        const LONG n = InterlockedIncrement(&k.crRace);
        if (n == 1 || n % g_crLogEvery == 0)
            Log("cullrace: RACE #%ld draw tid %lu drains scene %p while job 318 state %ld (cull running on tid %lu, sim tid %lu), "
                "list %ld / objects %ld, fix %s",
                n, self, scene, state, cullTid == self ? 0 : cullTid, CrSimTid(), len, count,
                g_cdwMode ? "waits" : "off");
    }
    if (g_cdwMode && r[0x28F]) {
        const DWORD h = *(const DWORD*)(r + 0x318);
        if (h) {
            LARGE_INTEGER a, b;
            QueryPerformanceCounter(&a);
            g_cdwWait(h);
            QueryPerformanceCounter(&b);
            InterlockedIncrement(&k.cdwWaits);
            InterlockedExchangeAdd(&k.cdwWaitUs, (LONG)((b.QuadPart - a.QuadPart) * 1000000 / g_crQpf.QuadPart));
        }
    }
    if (hit & 2) {
        const LONG n = InterlockedIncrement(&k.crBadIdx);
        if (n == 1 || n % g_crLogEvery == 0)
            Log("cullrace: BADIDX #%ld draw tid %lu scene %p entry %ld idx 0x%08lX >= objects %ld (list %ld; -1 = list unreadable), "
                "cull tid %lu, job 318 state %ld",
                n, self, scene, badAt, bad, count, len, cullTid, state);
    }
}

static __declspec(naked) void CrDrainStub() {
    __asm {
        push ecx           // scene (the drain's this)
        push esi           // renderer (DrawScene's this, esi at 0x6F401B)
        push ecx
        call CrBefore
        add esp, 8
        pop ecx
        jmp dword ptr [g_crDrainOrig]  // return address 0x6F4020 is already on the stack
    }
}

static DWORD __fastcall CrCullHook(void* renderer, void* edx) {
    InterlockedExchange(&g_crCullTid, (LONG)GetCurrentThreadId());
    const DWORD rv = g_crCullOrig(renderer, edx);
    InterlockedExchange(&g_crCullTid, 0);
    return rv;
}

static bool ApplyCullRace(const wchar_t* ini) {
    Ts3nCounters& k = ts3native_counters;
    const bool watch = GetPrivateProfileIntW(L"CrashForensics", L"CullRaceWatch", 1, ini) != 0;
    const bool fix = GetPrivateProfileIntW(L"EaCrashFixes", L"CullDrainWait", 1, ini) != 0;
    if (!watch && !fix) return false;
    const LONG every = (LONG)GetPrivateProfileIntW(L"CrashForensics", L"CullRaceLogEvery", 64, ini);
    g_crLogEvery = every > 0 ? every : 64;
    QueryPerformanceFrequency(&g_crQpf);
    if (fix) {
        if (Matches((const u8*)(uintptr_t)CR_JM_WAIT, kCrWaitPrologue, sizeof kCrWaitPrologue)) g_cdwWait = (CrWaitFn)(uintptr_t)CR_JM_WAIT;
        else Log("SKIP [EaCrashFixes] CullDrainWait: JobManager_Wait 0x%08X bytes differ, no wait", CR_JM_WAIT);
    }
    g_crDrainOrig = CR_DRAIN;
    const CallSite drain = {CR_DRAIN_SITE, CR_DRAIN, (void*)CrDrainStub};
    if (!PatchCalls("cull race drain 0x6F401B", &drain, 1)) return false;
    g_cdwMode = g_cdwWait ? 1 : 0;
    g_crWatch = watch ? 1 : 0;
    k.crOn = (watch ? 1 : 0) | (g_cdwMode ? 4 : 0) | 8;  // 8 = drain site hooked
    if (watch) {
        g_crCullOrig = (CrCullFn)(uintptr_t)CR_CULL;
        const CallSite cull = {CR_CULL_SITE, CR_CULL, (void*)CrCullHook};
        if (PatchCalls("cull race main cull 0x6F3C3B (thread record)", &cull, 1)) k.crOn |= 2;
    }
    Log("OK   cull race: drain site hooked, watch %d (cull thread %s), CullDrainWait %ld, log every %ld", watch ? 1 : 0,
        (k.crOn & 2) ? "recorded" : "not recorded", g_cdwMode, g_crLogEvery);
    return false;  // logs its own hits
}
