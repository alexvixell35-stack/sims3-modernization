// Mono ledger probe ([AutShadow] Ledger=1, default 0; docs/51 I-121-5, I-121-7, I-121-9, I-121-12, I-120-4, I-119-44).
// Included by autshadow.cpp after autshadow-run.cpp. Measurement only: every watched call still runs the game's body
// through the normal guard path, nothing is answered or skipped. Counters: ts3native_autshadow v3 (docs/registry.md).
//
// Scope = the game's own meta search B13 (Autonomy.ScoreInteractionsForMetaAutonomy): ts3fixes' MetaHook calls
// AutShadowNative.Config(5) before the game's method and Config(6) after it (finally). B13 may yield across ticks, so a
// scope belongs to the running script task: the 64-bit task id at MonoScriptHost +0xBD0 (docs/119 §11.2, the field
// ScriptHost_RunTask 0xD7F000 sets per run [verified there]). A watched call is in scope when the thread is the one that
// opened scopes and the running task has an open scope; any other call counts as outside.
// Watched ICs (their guard thunks jump here first, then on to AsGuardEntry): 0 Random_NextDoubleImpl (I-121-5),
// 1 Slots_GetPositionOfSlotImpl (I-121-9; the body calls the slot helper 0x76F0A0, docs/121 §2.7), 2 Query_GetObjects
// (I-121-7) and 3 Query_GetObjectsInRange (I-121-12). In scope: query calls record the Mono class of their System.Type
// argument (MonoReflectionType +8 = MonoType*, whose first word is the MonoClass* [inferred Mono 1.x layout; ts3fixes
// AutShadow.L3Ok reads the same word from TypeHandle.Value]); every call records the interpreter frame chain (TEB TLS slot
// [0x11F4160] -> context, +8 current frame, frame[0] parent, frame[1] info, info +8 MonoMethod*: the walk of
// tools/probe/agent.js interpFrames [verified 2026-10-12, docs/04 F6a]). tools/probe/agent.js names the pointers when it
// serves /native. Every read is under SEH: a bad pointer drops the record (lgDropped), never the call.
// Per Simulate tick (AsSimEnter, [MonoThread]'s slot stub): the EA memory tracking mode [0x11DC18C] (I-120-4).
// Sleep probe (I-119-44): ts3fixes' one task per load calls Config(9) around each Simulator.Sleep(0) and Config(10)
// around each Sleep(1); the tick count between two marks of a series goes into lgSleep.

static bool g_asLedger;                     // [AutShadow] Ledger=1, read before the guards are written
static char g_asLgWatch[AS_MAXIC];          // per guard index: watched IC + 1 (0 = not watched)
static volatile LONG* g_lgTrack = (volatile LONG*)(uintptr_t)TS3A(0x011DC18C);  // EA memory tracking mode (docs/120 §2.2)
static const DWORD* g_lgTlsIdx = (const DWORD*)(uintptr_t)TS3A(0x011F4160);      // interpreter context TLS index
struct AsLgScope { AsU64 task; LONG depth; LARGE_INTEGER t0; };
static AsLgScope g_lgScope[8];
static LONG g_lgOpen;                       // scopes with depth > 0
static DWORD g_lgSimTid;                    // the thread that opened the first scope
static AsU64 g_lgSlotId[64];
static LONG g_lgSleepLast[2], g_lgSleepBase[2];

static int AsLgWatchOf(const char* name) {
    static const char* const kWatch[4] = {"ScriptCore.Random::Random_NextDoubleImpl", "ScriptCore.Slots::Slots_GetPositionOfSlotImpl",
                                          "ScriptCore.Queries::Query_GetObjects", "ScriptCore.Queries::Query_GetObjectsInRange"};
    if (!g_asLedger) return -1;
    for (int i = 0; i < 4; i++) if (!strcmp(name, kWatch[i])) return i;
    return -1;
}

// the running script task (0 outside a task or when the host is not up)
static AsU64 AsLgTask() {
    __try {
        const u8* host = g_asHostPtr ? *g_asHostPtr : NULL;
        if (!host) return 0;
        ts3native_autshadow.lgOn |= 16;
        return *(const AsU64*)(host + 0xBD0);
    } __except (EXCEPTION_EXECUTE_HANDLER) { return 0; }
}

// interpreter context of this thread, read from the TEB (TlsGetValue would reset the game's last error); selftest swaps it
static const u8* __cdecl AsLgCtxTeb() {
    const DWORD idx = *g_lgTlsIdx;
    if (idx < 64) return (const u8*)(uintptr_t)__readfsdword(0xE10 + idx * 4);
    const DWORD* ext = (const DWORD*)(uintptr_t)__readfsdword(0xF94);
    return ext && idx < 1088 ? (const u8*)(uintptr_t)ext[idx - 64] : NULL;
}
static const u8*(__cdecl* g_lgCtx)() = AsLgCtxTeb;

static int AsLgFrames(DWORD* m, int n) {
    int got = 0;
    __try {
        const u8* ctx = g_lgCtx();
        for (const DWORD* f = ctx ? *(const DWORD* const*)(ctx + 8) : NULL; f && got < n; f = (const DWORD*)(uintptr_t)f[0]) {
            const u8* info = (const u8*)(uintptr_t)f[1];
            m[got++] = info ? *(const DWORD*)(info + 8) : 0;
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) { return -1; }
    return got;
}

static DWORD AsLgClass(DWORD reflType) {
    __try {
        const DWORD type = reflType ? *(const DWORD*)(uintptr_t)(reflType + 8) : 0;
        return type ? *(const DWORD*)(uintptr_t)type : 0;
    } __except (EXCEPTION_EXECUTE_HANDLER) { return 0; }
}

static bool AsLgInScope() {
    if (!g_lgOpen || GetCurrentThreadId() != g_lgSimTid) return false;
    const AsU64 t = AsLgTask();
    for (int i = 0; i < 8; i++) if (g_lgScope[i].depth > 0 && g_lgScope[i].task == t) return true;
    return false;
}

// a = the thunk's frame: a[0] guard index, a[1] return into the interpreter, a[2..] the IC's arguments (cdecl)
static void __cdecl AsLedgerHit(const DWORD* a) {
    Ts3nAutShadow& k = ts3native_autshadow;
    const int w = g_asLgWatch[a[0]] - 1;
    if (w < 0) return;
    if (!AsLgInScope()) { InterlockedIncrement(&k.lgOut[w]); return; }
    k.lgIn[w]++;
    if (w == 1 && k.lgSlotIds < 64) {  // hidden Vector3 result pointer first, then the object id
        const AsU64 id = a[3] | (AsU64)a[4] << 32;
        int i = 0;
        while (i < k.lgSlotIds && g_lgSlotId[i] != id) i++;
        if (i == k.lgSlotIds) g_lgSlotId[k.lgSlotIds++] = id;
    }
    if (w >= 2) {
        const DWORD cls = AsLgClass(a[2]);
        int i = 0;
        while (i < 16 && k.lgCls[i][2] && !(k.lgCls[i][0] == w && (DWORD)k.lgCls[i][1] == cls)) i++;
        if (i < 16) k.lgCls[i][0] = w, k.lgCls[i][1] = (LONG)cls, k.lgCls[i][2]++;
        else k.lgDropped++;
    }
    DWORD m[8] = {0};
    if (AsLgFrames(m, 8) <= 0) { k.lgDropped++; return; }
    k.lgOn |= 8;
    int i = 0;
    while (i < 32 && k.lgCall[i][1] && !(k.lgCall[i][0] == w && !memcmp(&k.lgCall[i][2], m, sizeof(m)))) i++;
    if (i == 32) { k.lgDropped++; return; }
    k.lgCall[i][0] = w, k.lgCall[i][1]++;
    memcpy(&k.lgCall[i][2], m, sizeof(m));
}

// guard thunk target for watched ICs: the worker goes straight to its answer path; other threads count, then pass through
static __declspec(naked) void AsLedgerEntry() {
    __asm {
        push eax
        mov eax, dword ptr fs:[0x24]
        cmp eax, dword ptr [g_asWorkerTid]
        pop eax
        je worker
        pushad
        pushfd
        lea eax, [esp + 36]
        push eax
        call AsLedgerHit
        add esp, 4
        popfd
        popad
        jmp AsGuardEntry
    worker:
        jmp AsCallEntry
    }
}

// top of every MonoScriptHost_Simulate (sim thread, AsSimEnter)
static void AsLgTick() {
    if (!g_asLedger) return;
    Ts3nAutShadow& k = ts3native_autshadow;
    k.lgOn |= 2;
    k.lgTicks++;
    const LONG v = *g_lgTrack;
    if (v) k.lgTrackTicks++, k.lgTrackLast = v;
}

// AutShadowNative.Config keys 5-10 (sim thread): 5 open a meta scope (1 = counted), 6 close it, 7 Ledger on (1/0),
// 8 ticks counted, 9 / 10 Sleep(0) / Sleep(1) probe mark (ticks since the last mark of that series, -1 for the first)
static int AsLgConfig(int key) {
    if (!g_asLedger) return 0;
    Ts3nAutShadow& k = ts3native_autshadow;
    switch (key) {
        case 5: {
            const AsU64 t = AsLgTask();
            if (!g_lgSimTid) g_lgSimTid = GetCurrentThreadId();
            int slot = -1;
            for (int i = 0; i < 8; i++) {
                if (g_lgScope[i].depth > 0 && g_lgScope[i].task == t) { g_lgScope[i].depth++; return 1; }
                if (g_lgScope[i].depth <= 0 && slot < 0) slot = i;
            }
            if (slot < 0) { k.lgScopeFull++; return 0; }
            g_lgScope[slot].task = t, g_lgScope[slot].depth = 1;
            QueryPerformanceCounter(&g_lgScope[slot].t0);
            g_lgOpen++, k.lgScopes++, k.lgOn |= 4;
            return 1;
        }
        case 6: {
            const AsU64 t = AsLgTask();
            for (int i = 0; i < 8; i++)
                if (g_lgScope[i].depth > 0 && g_lgScope[i].task == t) {
                    if (--g_lgScope[i].depth == 0) g_lgOpen--, k.lgScopeUs += AsUs(g_lgScope[i].t0);
                    return 0;
                }
            k.lgScopeFull++;
            return 0;
        }
        case 7: return 1;
        case 8: return k.lgTicks;
        case 9: case 10: {
            const int s = key - 9;
            const LONG d = g_lgSleepBase[s] ? k.lgTicks - g_lgSleepLast[s] : -1;
            g_lgSleepBase[s] = 1, g_lgSleepLast[s] = k.lgTicks;
            if (d >= 0) k.lgSleep[s * 4 + (d > 3 ? 3 : d)]++;
            return d;
        }
        default: return 0;
    }
}
