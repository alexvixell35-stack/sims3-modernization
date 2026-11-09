// [CrashForensics] (docs/157 §11.7, default off): guarded sites and the crash dump. The ring core is crashfx.h. Included by
// ts3native.cpp after watchpt.cpp (reads replay.cpp / p4.cpp state for the dump).
// docs/186 §8 adds DrawGuards=1 (four draw-site checks, below CfOnFault) and OurEvents=1 (crashev.h ring, dumped by CeDump).
// docs/186 §9 adds EffectLog=1 (Swarm render list ring), §10 ObjResLog=1 (SceneModel model handle ring); each only adds.
// docs/67 §16 adds DbIndexLog=1 (DBPF database index release ring + Contains check, Crash N).
// docs/67 §17 adds SceneTreeGuard=1 (scene node check at 0xC5FA60, Crash O) and dumps illegal instructions (wild calls) too.
// docs/67 §18 adds Light=1 (0.36.72): ring, heap check, P5c lines, our-events ring and the dump without any guard detour (every run).
//
// Guarded sites (probe-only detours: they read, count and log, then run the game's code unchanged):
//   0x5EE1E0 hash-map erase by iterator (thiscall, ret 0xC; [esp+8] = the node, its +0x10 object is released through vtable
//            slot 1 at 0x5EE23A): the audio thread's crash site of 2026-10-24 (0x5EE23C, object vtable 0). The guard reads the
//            node's object and its vtable and flags 0, unreadable or outside TS3W.exe .rdata. Prologue `mov edx,[esp+0Ch]` +
//            `push ebx` (5 bytes, no relative operand).
//   0x7B0A00 clears byte +0x48 of each 0x60-byte element of [ecx+0x30] (count [ecx+0x2C]): the main thread's crash site
//            (0x7B0A13 wrote to a gone array). The guard reads the first and last element byte. Its prologue `cmp [ecx+30h],0`
//            + `je 0x7B0A24` holds a rel8, so the trampoline is built by hand: cmp, `je 0x7B0A24` (rel32), `jmp 0x7B0A06`.
// A bad pointer is logged (first 16 per site) with the owner, the thread, the caller and the ring entry that holds the address,
// before the game uses it; then the game's code runs and faults as it would have.
//
// Crash dump: XcptLog (ts3native.cpp, [Crash] Log=1, the default) calls CfOnFault for an access violation outside ts3native
// (eip in TS3W.exe .text, first four only): fault and register addresses checked against the ring, the P5 state, the last 64 ring entries.

static const DWORD kCfEraseAt = 0x005EE1E0, kCfClrAt = 0x007B0A00;
static const u8 kCfErasePro[] = {0x8B, 0x54, 0x24, 0x0C, 0x53};        // mov edx,[esp+0Ch] ; push ebx
static const u8 kCfClrPro[] = {0x83, 0x79, 0x30, 0x00, 0x74, 0x1E};    // cmp dword ptr [ecx+30h],0 ; je 0x7B0A24
static DWORD g_cfVtLo, g_cfVtHi;     // TS3W.exe .rdata
static volatile LONG g_cfBadLogs[2], g_cfDumpN;

typedef DWORD*(__fastcall* CfEraseFn)(void* map, void*, DWORD* it, u8* node, DWORD* bucket);
typedef void(__fastcall* CfClrFn)(u8* self, void*);
static CfEraseFn g_cfEraseOrig;
static CfClrFn g_cfClrOrig;

static void CeDump(const EXCEPTION_POINTERS* x);                    // docs/186 §8, below
static void FxExplain(DWORD entry, DWORD list);                     // docs/186 §9, below
static void FxDump();                                               // docs/186 §9, below
static void OrExplain(DWORD obj);                                   // docs/186 §10, below
static void OrDump();                                               // docs/186 §10, below
static void DiDump(const CONTEXT* c);                               // docs/67 §16, below
static bool CfJmp(DWORD at, const u8* pro, size_t len, void* hook);  // below
static void CtInvFault(const CONTEXT* c);  // castrim.cpp: the outfit inventory fault decoder (docs/539 §12.5)
static void CtRelFault(const CONTEXT* c);  // castrim.cpp: the outfit release fault decoder (docs/539 §13.5)
static void CtOsFault(const CONTEXT* c);   // castrim.cpp: the outfit save string copy fault decoder (docs/539 §15)
static bool CfVtOk(DWORD vt) { return vt >= g_cfVtLo && vt < g_cfVtHi && !(vt & 3); }

// one guarded-site finding: count, ring lookup, first 16 logged
static void CfBad(int site, DWORD owner, DWORD holder, DWORD obj, DWORD vt, DWORD ret, const char* what) {
    Ts3nCounters& k = ts3native_counters;
    InterlockedIncrement(&k.cfBad[site]);
    char d1[200], d2[200];
    CfEnt e;
    const bool hit = CfFind(obj, &e) || CfFind(holder, &e);
    if (hit) InterlockedIncrement(&k.cfRingHit);
    if (InterlockedIncrement(&g_cfBadLogs[site]) > 16) return;
    CfDescribe(obj, d1, sizeof(d1));
    CfDescribe(holder, d2, sizeof(d2));
    Log("crash forensics: BAD %s at %s: owner 0x%08lX, holder 0x%08lX, pointer 0x%08lX (word 0x%08lX), thread %lu, caller 0x%08lX, "
        "g_rpUse holder %lu; pointer: %s; holder: %s",
        what, site ? "array clear 0x7B0A00" : "map erase 0x5EE1E0", owner, holder, obj, vt, GetCurrentThreadId(), ret, g_cfUseTid, d1, d2);
}

static DWORD* __fastcall CfEraseHook(void* map, void* edx, DWORD* it, u8* node, DWORD* bucket) {
    ts3native_counters.cfCalls[0]++;  // approximate: no lock prefix on the hot path
    DWORD obj = 0, vt = 0;
    bool bad = false;
    __try {
        obj = *(const DWORD*)(node + 0x10);
        if (obj) vt = *(const DWORD*)(uintptr_t)obj, bad = !CfVtOk(vt);
    } __except (EXCEPTION_EXECUTE_HANDLER) { bad = true; }
    if (bad) CfBad(0, (DWORD)(uintptr_t)map, (DWORD)(uintptr_t)node, obj, vt, (DWORD)(uintptr_t)_ReturnAddress(), "node object vtable");
    return g_cfEraseOrig(map, edx, it, node, bucket);
}

static void __fastcall CfClrHook(u8* self, void* edx) {
    ts3native_counters.cfCalls[1]++;
    DWORD arr = 0;
    LONG n = 0;
    bool bad = false;
    __try {
        arr = *(const DWORD*)(self + 0x30), n = *(const LONG*)(self + 0x2C);
        if (arr && n > 0) {
            const volatile u8* a = (const volatile u8*)(uintptr_t)arr;
            (void)a[0x48];
            (void)a[(DWORD)(n - 1) * 0x60 + 0x48];
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) { bad = true; }
    if (bad) CfBad(1, (DWORD)(uintptr_t)self, (DWORD)(uintptr_t)self, arr, (DWORD)n, (DWORD)(uintptr_t)_ReturnAddress(), "element array (word = count)");
    g_cfClrOrig(self, edx);
}

// docs/67 §15: 0x958560 (transform world matrix, thiscall(this, out) ret 4; callers 0x958F10, 0x959749, 0x9599A9, 0x959C09)
// reads the parent object [this+0x30], its transform and its slot component's +0x48 list. 2026-10-25 p6fix-verify crash: the
// sim thread faulted at 0x958944 on a NULL vtable from that list, the parent being an object our P5c flush had freed 33 s
// before. Check only, before the game reads: parent vtable not vt_Object (0x1042CA8) or the building base (0x107396C) or
// unreadable = 0 freed / garbage, refcount < 1 = 1, destroyed bit (+0x28 & 0x400000) = 2 (a child still parented to a destroyed,
// still allocated object: the zombie window of docs/67 §11.3 A). First 16 lines per kind, then counted; prologue 9 bytes.
static const DWORD kXgAt = 0x00958560;
static const u8 kXgPro[] = {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF0, 0x0F, 0x57, 0xC0};  // push ebp; mov ebp,esp; and esp,-16; xorps xmm0,xmm0
typedef void*(__fastcall* XgFn)(u8* self, void*, void* out);
static XgFn g_xgOrig;
static volatile LONG g_xgBad[3], g_xgLogs[3];
static bool P5cRelFind(DWORD a, char* out, size_t cap);  // p5c.cpp
static void XgBad(int kind, const u8* self, DWORD parent, const DWORD* w, bool readable, DWORD ret) {
    InterlockedIncrement(&g_xgBad[kind]);
    CeRec(CE_XFORM_BAD, parent, (DWORD)(uintptr_t)self, ret);
    if (InterlockedIncrement(&g_xgLogs[kind]) > 16) return;
    static const char* const why[3] = {"FREED / not an object", "refcount < 1", "destroyed (still allocated)"};
    char d1[220], d2[220];
    CeDescribe(parent, d1, sizeof(d1));
    P5cRelFind(parent, d2, sizeof(d2));
    DWORD key = 0;
    __try { key = *(const DWORD*)(self + 4); } __except (EXCEPTION_EXECUTE_HANDLER) {}
    if (readable)
        Log("crash forensics: XFORM BAD %s at 0x958560: transform 0x%08lX key 0x%08lX parent 0x%08lX vt %08lX count %ld id %08lX%08lX "
            "flags %08lX key %08lX, thread %lu, caller 0x%08lX; ours: %s; P5c: %s",
            why[kind], (DWORD)(uintptr_t)self, key, parent, w[0], (LONG)w[1], w[3], w[2], w[10], w[6], GetCurrentThreadId(), ret, d1, d2);
    else
        Log("crash forensics: XFORM BAD %s at 0x958560: transform 0x%08lX key 0x%08lX parent 0x%08lX unreadable, thread %lu, caller "
            "0x%08lX; ours: %s; P5c: %s", why[kind], (DWORD)(uintptr_t)self, key, parent, GetCurrentThreadId(), ret, d1, d2);
}
static void* __fastcall XgHook(u8* self, void* edx, void* out) {
    DWORD parent = 0, w[11] = {};
    int kind = -1;
    bool readable = true;
    __try {
        parent = *(const DWORD*)(self + 0x30);
        if (parent) {
            memcpy(w, (const void*)(uintptr_t)parent, sizeof(w));
            if (w[0] != 0x01042CA8 && w[0] != 0x0107396C) kind = 0;
            else if ((LONG)w[1] < 1) kind = 1;
            else if (w[10] & 0x400000) kind = 2;
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) { kind = 0, readable = false; }
    if (kind >= 0) XgBad(kind, self, parent, w, readable, (DWORD)(uintptr_t)_ReturnAddress());
    return g_xgOrig(self, edx, out);
}

// docs/67 §17 (Crash O): SceneNode_FindRecursive 0xC5FA60, cdecl(node, idLo, idHi, counter*) -> the node with that id or 0;
// callers 0xC60D71 (WorldManager_FindLotById over WM+0x9C) and 0xC5FAC8 (itself, on a type-2 node's children) [verified E8 scan].
// 2026-10-25 hold2 crash: the sim thread faulted at 0xC5FA93 on a child node whose vtable word was 0xB07B3B93 (design-U footprint
// apply -> 0x82CBF0 -> 0x82C120 -> FindLotByIdTyped 0xC62D40). Check only, before the game reads: vtable outside TS3W.exe .rdata
// or unreadable. The caller's esi is the parent node (recursive call) or the world manager (root call). 7-byte prologue, no
// branch into it [verified E8/E9/Jcc scan]. Apex Radiance's ObjectLookupIndex reads this function: a patched prologue = SKIP.
static const DWORD kStAt = 0x00C5FA60;
static const u8 kStPro[] = {0x53, 0x55, 0x56, 0x8B, 0x74, 0x24, 0x10};  // push ebx; push ebp; push esi; mov esi,[esp+10h]
static void* g_stOrig;
static volatile LONG g_stBad, g_stLogs;
// sp = the entry esp: [0] return, [1] node, [2] id low, [3] id high
static void __stdcall StCheck(const DWORD* sp, DWORD parent) {
    const DWORD node = sp[1];
    if (!node) return;  // the game returns 0 for it
    DWORD vt = 0;
    bool readable = true;
    __try { vt = *(const DWORD*)(uintptr_t)node; } __except (EXCEPTION_EXECUTE_HANDLER) { readable = false; }
    if (readable && CfVtOk(vt)) return;
    InterlockedIncrement(&g_stBad);
    if (InterlockedIncrement(&g_stLogs) > 16) return;
    DWORD pvt = 0;
    __try { pvt = parent ? *(const DWORD*)(uintptr_t)parent : 0; } __except (EXCEPTION_EXECUTE_HANDLER) { pvt = 0xFFFFFFFF; }
    char d1[200], d2[220], d3[220];
    CfDescribe(node, d1, sizeof(d1));
    CeDescribe(node, d2, sizeof(d2));
    if (!P5cRelFind(node, d3, sizeof(d3))) strcpy_s(d3, "no");
    Log("crash forensics: SCENE NODE BAD at 0xC5FA60: node 0x%08lX %s vt %08lX, parent 0x%08lX vt %08lX, id %08lX%08lX, thread %lu, "
        "caller 0x%08lX; P5 heap ring: %s; ours: %s; P5c: %s", node, readable ? "" : "UNREADABLE", vt, parent, pvt, sp[3], sp[2],
        GetCurrentThreadId(), sp[0], d1, d2, d3);
}
static __declspec(naked) void StThunk() {
    __asm {
        mov eax, esp  // eax / ecx / edx are free at this cdecl entry (the body reads only its stack arguments)
        push esi
        push eax
        call StCheck
        jmp dword ptr [g_stOrig]
    }
}

// the dump: one line of P5 state, the fault and register addresses against the ring, then the ring tail
static void CfOnFault(const EXCEPTION_POINTERS* x) {
    // TS3W.exe .text only: every run has 3 handled first-chance AVs at start-up in other modules (queue logs 2026-10-24);
    // an illegal instruction anywhere = a wild call into data (Crash M / O: eip in the heap), dumped with the fault at eip
    const DWORD eip = x->ContextRecord->Eip;
    // docs/539 §12.5: EA's outfit inventory 0x5E7690 (the 11-08 travel-save crash at 0x5E772C), Light or not, before the dump cap
    if (eip == 0x005E772C || eip == 0x005E76DA) CtInvFault(x->ContextRecord);
    // docs/539 §13.5: the 11-08 release-smoke crash; §14: 0xD4B154 / any fault in the callback 0xD4B150 (11-09, from 0x5D82A0)
    if ((eip >= 0x00D4B150 && eip <= 0x00D4B159) || eip == 0x005AF67E) CtRelFault(x->ContextRecord);
    // docs/539 §15: the 11-09 outfit save crash faults in MSVCR80's memcpy (0x408C3A / 0x5E621C on the stack), so any AV is searched
    if (x->ExceptionRecord->ExceptionCode == EXCEPTION_ACCESS_VIOLATION) CtOsFault(x->ContextRecord);
    const bool wild = XcptWild(x->ExceptionRecord->ExceptionCode);  // C0000096 too (2026-11-02 drain crash wrote no dump)
    if (!g_cfOn || (!wild && (eip < 0x00401000 || eip >= 0x00F95000)) || InterlockedIncrement(&g_cfDumpN) > 4) return;
    Ts3nCounters& k = ts3native_counters;
    InterlockedIncrement(&k.cfDumps);
    const EXCEPTION_RECORD* r = x->ExceptionRecord;
    const CONTEXT* c = x->ContextRecord;
    const DWORD at = wild ? eip : r->NumberParameters > 1 ? (DWORD)r->ExceptionInformation[1] : 0;
    char d[200];
    CfDescribe(at, d, sizeof(d));
    Log("crash forensics: DUMP %s (%s 0x%08lX) at eip 0x%08lX thread %lu; P5 state: route job %s, g_rpPhase 0x%lX, "
        "g_rpUse holder %lu, RecsUnlocked %ld, held lot %08lX%08lX, replay head %lu tail %lu, P4 job armed %ld kind %d mode %d; "
        "heap check %s, bad frees %ld, guard hits %ld/%ld",
        wild ? "illegal instruction" : "access violation", wild ? "exec" : r->NumberParameters > 0 && r->ExceptionInformation[0] ? "write" : "read", at, c->Eip, GetCurrentThreadId(),
        g_async.state == A_RUNNING ? "running" : "idle", g_rpPhase, g_cfUseTid, g_rpRecsUnlocked, (DWORD)(g_rpHoldKey >> 32),
        (DWORD)g_rpHoldKey, g_rpHead, g_rpTail, g_p4Job.armed, g_p4Job.kind, g_p4Job.mode, g_cfHeap ? "on" : "off", k.cfHeapBad,
        k.cfBad[0], k.cfBad[1]);
    Log("crash forensics: fault address 0x%08lX: %s", at, d);
    if (wild) {  // the call site and the object whose vtable slot led here (Crash O main thread: call [edx+10h] at 0x50ED8C)
        DWORD ret = 0, vt = 0;
        __try { ret = *(const DWORD*)(uintptr_t)c->Esp; } __except (EXCEPTION_EXECUTE_HANDLER) {}
        __try { vt = *(const DWORD*)(uintptr_t)c->Ecx; } __except (EXCEPTION_EXECUTE_HANDLER) {}
        CfDescribe(c->Ecx, d, sizeof(d));
        Log("crash forensics: wild call: [esp] 0x%08lX, ecx 0x%08lX vt %08lX (%s): %s", ret, c->Ecx, vt, CfVtOk(vt) ? ".rdata" : "NOT .rdata", d);
    }
    const struct { const char* n; DWORD v; } regs[] = {{"eax", c->Eax}, {"ebx", c->Ebx}, {"ecx", c->Ecx}, {"edx", c->Edx},
                                                       {"esi", c->Esi}, {"edi", c->Edi}, {"ebp", c->Ebp}};
    CfEnt e;
    for (const auto& g : regs)
        if (g.v >= 0x10000 && CfFind(g.v, &e)) {
            InterlockedIncrement(&k.cfRingHit);
            CfDescribe(g.v, d, sizeof(d));
            Log("crash forensics: %s 0x%08lX: %s", g.n, g.v, d);
        }
    if (at >= 0x10000 && CfFind(at, &e)) InterlockedIncrement(&k.cfRingHit);
    LARGE_INTEGER q;
    QueryPerformanceCounter(&q);
    const LONG top = g_cfSeq;
    Log("crash forensics: ring tail (last 64 of %ld events, newest first: seq tag addr size thread phase job age-ms)", top);
    for (LONG s = top; s > 0 && top - s < 64; s--) {
        const CfEnt& t = g_cfRing[(DWORD)(s - 1) & (kCfRing - 1)];
        if (t.seq != s) continue;
        Log("  %ld %s %08lX %lu %lu %lX %d %.1f", s, t.tag < CF_NTAGS ? kCfTag[t.tag] : "?", t.addr, t.size, t.tid, t.phase, t.job,
            g_cfQpf ? (double)(q.QuadPart - t.qpc) * 1000.0 / (double)g_cfQpf : -1.0);
    }
    // every draw guard site, whatever crashed (docs/186 §8: several races may be at work at once)
    Log("crash forensics: draw guard hits model %ld resource %ld effect %ld binding %ld (installed bits 0x%lX); parent guard 0x958560 "
        "freed %ld refcount %ld destroyed %ld (%s)", k.cdBad[0], k.cdBad[1], k.cdBad[2], k.cdBad[3], k.cdReady, g_xgBad[0], g_xgBad[1],
        g_xgBad[2], g_xgOrig ? "on" : "off");
    Log("crash forensics: scene node guard 0xC5FA60 bad %ld (%s)", g_stBad, g_stOrig ? "on" : "off");  // docs/67 §17
    CeDump(x);  // docs/186 §8: our-events ring (OurEvents=1)
    FxDump();   // docs/186 §9: Swarm render list ring (EffectLog=1)
    OrDump();   // docs/186 §10: model handle ring (ObjResLog=1)
    DiDump(c);  // docs/67 §16: DBPF database index ring (DbIndexLog=1)
}

// ---- docs/186 §8: DrawGuards=1, the main-thread draw sites of the Apex pan crashes, and the OurEvents dump ----
// Each guard sits right before the instruction that faulted, not on a function entry: Apex Radiance detours the entry of the
// model part draw 0x6F6250 (RigTracker ModelDrawHook) and the call 0x6F68C5, so we take call sites inside the draw instead.
//   0 model    call 0x6F6B5B -> 0x68EEF0 (thiscall getter of [this+0xC0]); this = ecx came from a local of 0x6F6250 (ebx =
//              its model). 18.35 crash: this = 0x20.
//   1 resource call 0x6FA2DB -> 0x6C93D0 (getter of [this+0x44]); ecx = what 0x6DB810 returned for member edi+0x210 (edi =
//              0x6FA0B0's object). 18.37 crash: ecx = 0.
//   2 effect   0x71D562 `mov eax,[edi+4]` + `mov [esi+3130h],eax` (9 bytes, jmp to the stub): eax = the render entry's effect
//              instance, stored through at 0x71D56E (edi = entry, 0x50 bytes; esi = the effects list). 18.40 / 18.45: eax = 0.
//              Flags are dead there (mov, mov, call follow).
//   3 binding  call 0x68FDA4 -> 0x68E8F0 (cdecl key, table, count; key 0 returns the table when count != 0); table = binding
//              edi +0x50, count +0x54. 19.26 crash: table 0xFFFFFFFF.
// Good path: 2-4 compares and a jmp (no counter, no store). Bad: CdBad logs (first 8 per site) the pointer, the parent object
// with its vtable and first words, the ring entries that hold either address, and the code addresses on the stack (Apex
// frames show as ApexRadiance.asi+off); then the game's code runs unchanged and faults as before.
static DWORD g_cdModelFn = 0x0068EEF0, g_cdResFn = 0x006C93D0, g_cdFxBack = 0x0071D56B, g_cdBindFn = 0x0068E8F0;
static const DWORD kCdModelCall = 0x006F6B5B, kCdResCall = 0x006FA2DB, kCdFxAt = 0x0071D562, kCdBindCall = 0x0068FDA4;
static const u8 kCdFxPro[] = {0x8B, 0x47, 0x04, 0x89, 0x86, 0x30, 0x31, 0x00, 0x00};  // mov eax,[edi+4] ; mov [esi+3130h],eax
static volatile LONG g_cdLogs[4];
static const char* const kCdSite[4] = {"model part draw 0x6F6250 (call 0x6F6B5B -> 0x68EEF0)",
                                       "object draw 0x6FA0B0 (call 0x6FA2DB -> 0x6C93D0, resource of member +0x210)",
                                       "Swarm effect list draw 0x71D440 (0x71D562, entry effect instance)",
                                       "shader binding update 0x68FD80 (call 0x68FDA4 -> 0x68E8F0, binding table)"};

// "module+0xoff" for a code address (TS3W.exe or any image), false = not executable image memory
static bool CdCode(DWORD v, char* out, size_t cap) {
    if (v >= 0x00401000 && v < 0x00F95000) { sprintf_s(out, cap, "0x%08lX", v); return true; }
    MEMORY_BASIC_INFORMATION m;
    if (v < 0x10000 || !VirtualQuery((void*)(uintptr_t)v, &m, sizeof(m)) || m.Type != MEM_IMAGE ||
        !(m.Protect & (PAGE_EXECUTE | PAGE_EXECUTE_READ | PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY)))
        return false;
    char path[MAX_PATH];
    const DWORD n = GetModuleFileNameA((HMODULE)m.AllocationBase, path, MAX_PATH);
    const char* b = n ? strrchr(path, '\\') : NULL;
    sprintf_s(out, cap, "%s+0x%lX", b ? b + 1 : n ? path : "?", v - (DWORD)(uintptr_t)m.AllocationBase);
    return true;
}
// "w0 w1 ..." of n dwords at p, or "unreadable"
static void CdWords(DWORD p, int n, char* out, size_t cap) {
    size_t o = 0;
    out[0] = 0;
    __try {
        for (int i = 0; i < n && o + 10 < cap; i++) o += sprintf_s(out + o, cap - o, "%08lX ", ((const DWORD*)(uintptr_t)p)[i]);
    } __except (EXCEPTION_EXECUTE_HANDLER) { sprintf_s(out + o, cap - o, "unreadable"); }
}
static DWORD CdRead(DWORD p) {
    __try { return *(const DWORD*)(uintptr_t)p; } __except (EXCEPTION_EXECUTE_HANDLER) { return 0; }
}
// docs/186 §10: a scene node's pending link +0x18 {next, prev} (0 or a self-loop = not queued, else waiting in its owner's
// list for the drain to run its node update, vfunc +0x48), owner holder +0x30, the holder's +0x18 = nodes the last drain
// processed [verified Steam 1.67: MarkDirty 0x6FAC70, drain 0x6E4130; Apex's budgeted copy keeps the same counter]
static bool CdNodeQueued(DWORD node, DWORD* holder, DWORD* drained) {
    const DWORD l = CdRead(node + 0x18);
    *holder = CdRead(node + 0x30), *drained = *holder ? CdRead(*holder + 0x18) : 0;
    return l && l != node + 0x18;
}

// r = the pushad block (edi, esi, ebp, esp, ebx, edx, ecx, eax); r[3] = esp at the guard (site 0/1/3: [esp] = return address)
static void __cdecl CdBad(int site, const DWORD* r) {
    Ts3nCounters& k = ts3native_counters;
    InterlockedIncrement(&k.cdBad[site]);
    static const char* const kParent[4] = {"model (ebx)", "object (edi)", "render entry (edi)", "binding (edi)"};
    const DWORD bad = site == 0 || site == 1 ? r[6] : site == 2 ? r[7] : CdRead(r[3] + 8);
    const DWORD par = site == 0 ? r[4] : r[0];
    CeRec(CE_DRAW_BAD, bad, (DWORD)site, par);
    const LONG nlog = InterlockedIncrement(&g_cdLogs[site]);
    if (nlog > 8) {  // every later hit still gets one short line (all sites: several races may be at work), up to 256 per site
        if (nlog <= 256)
            Log("crash forensics: DRAW BAD #%ld at %s: pointer 0x%08lX, parent 0x%08lX, thread %lu", k.cdBad[site], kCdSite[site], bad, par,
                GetCurrentThreadId());
        return;
    }
    const DWORD vt = CdRead(par);
    char w[200], d1[220], d2[220], d3[220], d4[220];
    CdWords(par, site == 2 ? 20 : 16, w, sizeof(w));
    Log("crash forensics: DRAW BAD at %s: pointer 0x%08lX, %s 0x%08lX vtable 0x%08lX (%s), thread %lu, ebp 0x%08lX esi 0x%08lX; "
        "%s words: %s", kCdSite[site], bad, kParent[site], par, vt, CfVtOk(vt) ? "TS3W .rdata" : "NOT a TS3W vtable",
        GetCurrentThreadId(), r[2], r[1], kParent[site], w);
    if (site == 1) {  // the member handle 0x6DB810 read: [m+8], low bits 3 set = unresolved (returns 0)
        CdWords(par + 0x210, 4, w, sizeof(w));
        Log("crash forensics:   member +0x210 words: %s", w);
        OrExplain(par);  // docs/186 §10: the model handle's state, the node's queue state, who emptied +8

    } else if (site == 2) {  // esi = effects list, entries at [esi+0x3110], 0x50 bytes each
        const DWORD arr = CdRead(r[1] + 0x3110);
        Log("crash forensics:   effects list 0x%08lX vtable 0x%08lX, entry array 0x%08lX, entry index %ld", r[1], CdRead(r[1]),
            arr, arr && par >= arr ? (LONG)((par - arr) / 0x50) : -1L);
        FxExplain(par, r[1]);  // docs/186 §9: who removed this entry, and the list's last gather
    } else if (site == 3) {  // esi = draw context, key [esi+0x30]; [esp+4] key, [esp+0xC] count
        Log("crash forensics:   context 0x%08lX key 0x%08lX, table count %ld (0x68E8F0 with key 0 returns the table)", r[1],
            CdRead(r[3] + 4), (LONG)CdRead(r[3] + 0xC));
    }
    CfDescribe(bad, d1, sizeof(d1));
    CfDescribe(par, d2, sizeof(d2));
    CeDescribe(bad, d3, sizeof(d3));
    CeDescribe(par, d4, sizeof(d4));
    Log("crash forensics:   pointer in P5 ring: %s", d1);
    Log("crash forensics:   pointer in ours: %s", d3);
    Log("crash forensics:   %s in P5 ring: %s", kParent[site], d2);
    Log("crash forensics:   %s in ours: %s", kParent[site], d4);
    // code addresses on the stack, nearest first (frames and residue alike; Apex's ModelDrawHook shows as ApexRadiance.asi+0x292BE4)
    size_t o = 0;
    char st[440], c[96];  // Log lines are 512 bytes
    st[0] = 0;
    __try {
        const DWORD* sp = (const DWORD*)(uintptr_t)r[3];
        for (int i = 0, n = 0; i < 256 && n < 16 && o + 100 < sizeof(st); i++)
            if (CdCode(sp[i], c, sizeof(c))) o += sprintf_s(st + o, sizeof(st) - o, "%s[+0x%X] ", c, i * 4), n++;
    } __except (EXCEPTION_EXECUTE_HANDLER) {}
    Log("crash forensics:   stack code words: %s", st);
}

static __declspec(naked) void CdModelThunk() {
    __asm {
        cmp ecx, 0x10000
        jb bad
        cmp ecx, 0xFFFF0000
        jae bad
        jmp dword ptr [g_cdModelFn]
    bad:
        pushad
        push esp
        push 0
        call CdBad
        add esp, 8
        popad
        jmp dword ptr [g_cdModelFn]
    }
}
static __declspec(naked) void CdResThunk() {
    __asm {
        cmp ecx, 0x10000
        jb bad
        cmp ecx, 0xFFFF0000
        jae bad
        jmp dword ptr [g_cdResFn]
    bad:
        pushad
        push esp
        push 1
        call CdBad
        add esp, 8
        popad
        jmp dword ptr [g_cdResFn]
    }
}
static __declspec(naked) void CdFxStub() {
    __asm {
        mov eax, [edi + 4]
        mov [esi + 0x3130], eax
        cmp eax, 0x10000
        jb bad
        cmp eax, 0xFFFF0000
        jae bad
        jmp dword ptr [g_cdFxBack]
    bad:
        pushad
        push esp
        push 2
        call CdBad
        add esp, 8
        popad
        jmp dword ptr [g_cdFxBack]
    }
}
// cdecl: eax / edx are the callee's scratch registers, the arguments stay on the stack
static __declspec(naked) void CdBindThunk() {
    __asm {
        mov eax, [esp + 8]  // table
        cmp eax, 0xFFFF0000
        jae bad
        test eax, eax
        jz count
        cmp eax, 0x10000
        jb bad
    count:
        cmp dword ptr [esp + 0xC], 0x100000  // shortcut: 1M entries as the sane bound [inferred]; a hit only logs
        ja bad
        jmp dword ptr [g_cdBindFn]
    bad:
        pushad
        push esp
        push 3
        call CdBad
        add esp, 8
        popad
        jmp dword ptr [g_cdBindFn]
    }
}

static void CdInstall() {
    Ts3nCounters& k = ts3native_counters;
    const CallSite s[3] = {{kCdModelCall, g_cdModelFn, (void*)CdModelThunk}, {kCdResCall, g_cdResFn, (void*)CdResThunk},
                           {kCdBindCall, g_cdBindFn, (void*)CdBindThunk}};
    static const LONG bit[3] = {1, 2, 8};
    static const char* const name[3] = {"crash forensics draw guard model 0x6F6B5B", "crash forensics draw guard resource 0x6FA2DB",
                                        "crash forensics draw guard binding 0x68FDA4"};
    for (int i = 0; i < 3; i++) if (PatchCalls(name[i], &s[i], 1)) k.cdReady |= bit[i];
    if (CfJmp(kCdFxAt, kCdFxPro, sizeof(kCdFxPro), (void*)CdFxStub)) k.cdReady |= 4, Log("OK   crash forensics draw guard effect 0x71D562 (check only)");
}

// crash dump part 2: the fault and registers against our-events ring, the route job, the newest 64 events of each kind
static void CeDump(const EXCEPTION_POINTERS* x) {
    if (!g_ceOn) return;
    const CONTEXT* c = x->ContextRecord;
    const EXCEPTION_RECORD* r = x->ExceptionRecord;
    const DWORD at = r->NumberParameters > 1 ? (DWORD)r->ExceptionInformation[1] : 0;
    char d[220];
    CeEnt e;
    const struct { const char* n; DWORD v; } regs[] = {{"fault address", at}, {"eax", c->Eax}, {"ebx", c->Ebx}, {"ecx", c->Ecx},
                                                       {"edx", c->Edx}, {"esi", c->Esi}, {"edi", c->Edi}, {"ebp", c->Ebp}};
    for (const auto& g : regs)
        if (g.v >= 0x10000 && CeFind(g.v, &e)) {
            CeDescribe(g.v, d, sizeof(d));
            Log("crash forensics: ours: %s 0x%08lX: %s", g.n, g.v, d);
        }
    const LONG top = g_ceSeq;
    Log("crash forensics: ours: route job %s (handle 0x%08lX, lock %p), draw guard hits %ld/%ld/%ld/%ld, %ld events recorded",
        g_async.state == A_RUNNING ? "RUNNING (worker holds +0x918)" : "idle", g_async.handle, g_async.lock, ts3native_counters.cdBad[0],
        ts3native_counters.cdBad[1], ts3native_counters.cdBad[2], ts3native_counters.cdBad[3], top);
    for (u8 tag = 1; tag < CE_NTAGS; tag++) {
        int n = 0;
        for (LONG s = top; s > 0 && top - s < (LONG)kCeRing && n < 64; s--) {
            const CeEnt& t = g_ceRing[(DWORD)(s - 1) & (kCeRing - 1)];
            if (t.seq != s || t.tag != tag) continue;
            if (!n++) Log("crash forensics: ours: %s (newest first: seq a x y thread job age-ms)", kCeTag[tag]);
            Log("  %ld %08lX %lX %lX %lu %d %.1f", s, t.a, t.x, t.y, t.tid, t.job, CeAgeMs(t.qpc));
        }
    }
}

// ---- docs/186 §9: EffectLog=1, the Swarm render list (SwarmRenderer, vtable 0xFFC820) ----
// Round 1 caught entry 87 of the effects list drawn after 0x71FF10 had cleared it (owner / instance 0, object id at +0x20
// kept). The list's writers and readers [verified, Steam 1.67 disassembly]:
//   add     0x71FD20 vtable slot 0xFFC850 (+0x30), thiscall(owner, desc*, int* index), ret 0xC: pops a free index (vector
//           +0x3120/+0x3124) or grows the 0x50-byte entry array +0x3110/+0x3114; owner +0, instance +4, ids +8/+0x20, 0x4E = 1
//   remove  0x71FF10 vtable slot 0xFFC854 (+0x34), thiscall(index), ret 4: pushes the index on the free vector, releases the
//           instance (vfunc +4), clears owner, instance and 0x4E; the bucket lists are NOT touched. Caller: Swarm model effect
//           teardown 0x8F3630 (vtable 0x103EDCC slot +8, indexes +0x220 / +0x224)
//   gather  0x722190 = scene node update (vfunc +0x48, slot 0xFFC290) of the renderer's drawable (vtable 0xFFC248): calls
//           0x7212E0, which rebuilds the per-bucket node lists +0x38EC from the active entries, then MarkDirty 0x6FAC70 so it
//           is queued again. It runs inside the scene pending-node drain 0x6E4130, i.e. Scene::BeginFrame on the main thread,
//           unless Apex Radiance's SceneNodeBudget defers the node (camera moving) to a later frame or another drain
//   draw    0x71D660 (vtable slot 0xFFC260 = +0x18, the draw-list call 0x6EE872) -> 0x71D440 walks one bucket list and reads
//           the entries by index, main thread, no lock
// No lock anywhere: EA keeps it single-threaded (Swarm messages applied in the services stage, then the drain rebuilds the
// lists, then the draw). A remove between a gather and a draw leaves a bucket node pointing at a cleared entry.
// The hooks swap the four vtable slots (no code bytes; Apex patches none of them), record into a 4096-entry ring and count:
// removes since the last gather > 0 at a draw = a stale-list draw (first one per gather walks the buckets for dangling nodes).
static const DWORD kFxSlotAdd = 0x00FFC850, kFxSlotRem = 0x00FFC854, kFxSlotGather = 0x00FFC290, kFxSlotDraw = 0x00FFC260;
static DWORD g_fxAddFn = 0x0071FD20, g_fxRemFn = 0x0071FF10, g_fxGatherFn = 0x00722190, g_fxDrawFn = 0x0071D660;
enum FxTag : u8 { FX_NONE, FX_ADD, FX_REM, FX_GATHER, FX_STALE, FX_NTAGS };
static const char* const kFxTag[FX_NTAGS] = {"?", "add", "remove", "gather", "stale-draw"};
struct FxEnt {
    volatile LONG seq;  // written last
    DWORD list;         // the SwarmRenderer (effects list)
    DWORD index;        // add / remove: entry index; gather: removes since the previous gather; stale: removes since gather
    DWORD entry;        // add / remove: entry address; gather: us since the previous gather; stale: dangling bucket nodes
    DWORD owner, inst;  // add / remove: entry +0 / +4 (remove: before the clear); gather: draws since the gather, 0; stale: drawable queued, nodes of the last drain
    DWORD idLo, idHi;   // add / remove: entry +0x20 / +0x24 (the attached object's id)
    DWORD tid, code[2]; // thread; return address and the next code word on the stack
    LONG gseq;          // gather number at the event
    u8 tag, pad[3];
    DWORD pad2;
    LONGLONG qpc;
};
static_assert(sizeof(FxEnt) == 64, "effect ring entry");
static const DWORD kFxRing = 4096;  // power of two
static FxEnt g_fxRing[kFxRing];
static volatile LONG g_fxSeq, g_fxGseq, g_fxRemSince, g_fxDraws, g_fxStaleMark, g_fxStaleLogs;
static LONG g_fxOn;
static LONGLONG g_fxGatherQpc;
static DWORD g_fxApexLo, g_fxApexHi;  // ApexRadiance.asi image (code words on the stack)

typedef DWORD(__fastcall* FxAddFn)(u8* self, void*, DWORD owner, DWORD desc, LONG* index);
typedef DWORD(__fastcall* FxRemFn)(u8* self, void*, LONG index);
typedef void(__fastcall* FxNodeFn)(u8* self, void*);
typedef void(__fastcall* FxDrawFn)(u8* self, void*, DWORD a, DWORD b);

static const char* FxThr(DWORD tid) {
    if (tid == g_mainTid) return "main";
    if (g_rtSimTidSlot && Readable(g_rtSimTidSlot, 4) && *g_rtSimTidSlot && tid == *g_rtSimTidSlot) return "sim";
    return g_workerTid && tid == g_workerTid ? "route worker" : "other";
}
static bool FxIsCode(DWORD v) { return (v >= 0x00401000 && v < 0x00F95000) || (v >= g_fxApexLo && v < g_fxApexHi); }
// sp = address of our return address: code[0] = the caller, code[1] = the next TS3W / Apex code word above it
static void FxCodes(const DWORD* sp, DWORD* code) {
    code[0] = sp[0], code[1] = 0;
    __try {
        for (int i = 1; i < 48; i++) if (FxIsCode(sp[i])) { code[1] = sp[i]; break; }
    } __except (EXCEPTION_EXECUTE_HANDLER) {}
}
// the entry at index (first 10 words); false = out of range or unreadable
static bool FxEntry(const u8* list, LONG index, DWORD* at, DWORD* w) {
    __try {
        const DWORD arr = *(const DWORD*)(list + 0x3110), end = *(const DWORD*)(list + 0x3114);
        if (index < 0 || !arr || end < arr || (DWORD)index >= (end - arr) / 0x50) return false;
        *at = arr + (DWORD)index * 0x50;
        memcpy(w, (const void*)(uintptr_t)*at, 40);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}
static void FxPut(u8 tag, DWORD list, DWORD index, DWORD entry, DWORD owner, DWORD inst, DWORD idLo, DWORD idHi, const DWORD* sp) {
    const LONG s = InterlockedIncrement(&g_fxSeq);
    FxEnt& e = g_fxRing[(DWORD)(s - 1) & (kFxRing - 1)];
    e.seq = 0;
    e.list = list, e.index = index, e.entry = entry, e.owner = owner, e.inst = inst, e.idLo = idLo, e.idHi = idHi;
    e.tid = GetCurrentThreadId(), e.gseq = g_fxGseq, e.tag = tag;
    FxCodes(sp, e.code);
    LARGE_INTEGER q;
    QueryPerformanceCounter(&q);
    e.qpc = q.QuadPart;
    _WriteBarrier();
    e.seq = s;
    ts3native_counters.fxRecs = s;
}
static void FxNote(u8 tag, const u8* list, LONG index, const DWORD* sp) {
    DWORD w[10] = {}, at = 0;
    const bool live = FxEntry(list, index, &at, w);
    if (tag == FX_REM) {
        Ts3nCounters& k = ts3native_counters;
        InterlockedIncrement(&k.fxRem);
        if (GetCurrentThreadId() != g_mainTid) InterlockedIncrement(&k.fxRemOffMain);
        if (live && w[0]) InterlockedIncrement(&g_fxRemSince);  // 0x71FF10 does nothing for a free slot (owner 0)
    }
    FxPut(tag, (DWORD)(uintptr_t)list, (DWORD)index, at, w[0], w[1], w[8], w[9], sp);
}
// bucket nodes (lists +0x38EC, 12 bytes each; node {next, prev, index}; index >= bucket count [+0x3980] = an entry) whose
// entry is free (owner 0 or byte 0x4E 0): what 0x71D440 would draw through a cleared entry
static DWORD FxDangling(const u8* list) {
    DWORD bad = 0;
    __try {
        const DWORD b0 = *(const DWORD*)(list + 0x38EC), nb = *(const DWORD*)(list + 0x3980);
        const DWORD arr = *(const DWORD*)(list + 0x3110), cnt = (*(const DWORD*)(list + 0x3114) - arr) / 0x50;
        for (DWORD b = 0, steps = 0; b < nb && b < 4096; b++) {
            const DWORD head = b0 + b * 12;
            for (DWORD n = *(const DWORD*)(uintptr_t)head; n != head && steps < 65536; n = *(const DWORD*)(uintptr_t)n, steps++) {
                const LONG idx = *(const LONG*)(uintptr_t)(n + 8);
                if (idx < 0 || (DWORD)idx < nb) continue;
                const DWORD e = (DWORD)idx - nb;
                if (e >= cnt) { bad++; continue; }
                const u8* p = (const u8*)(uintptr_t)(arr + e * 0x50);
                if (!*(const DWORD*)p || !p[0x4E]) bad++;
            }
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) { bad |= 0x80000000; }
    return bad;
}

static DWORD __fastcall FxAddHook(u8* self, void* edx, DWORD owner, DWORD desc, LONG* index) {
    const DWORD r = ((FxAddFn)(uintptr_t)g_fxAddFn)(self, edx, owner, desc, index);
    if (g_fxOn && r && index) FxNote(FX_ADD, self, CdRead((DWORD)(uintptr_t)index), (const DWORD*)_AddressOfReturnAddress());
    return r;
}
static DWORD __fastcall FxRemHook(u8* self, void* edx, LONG index) {
    if (g_fxOn) FxNote(FX_REM, self, index, (const DWORD*)_AddressOfReturnAddress());  // before the clear
    return ((FxRemFn)(uintptr_t)g_fxRemFn)(self, edx, index);
}
static void __fastcall FxGatherHook(u8* self, void* edx) {
    if (g_fxOn) {
        Ts3nCounters& k = ts3native_counters;
        LARGE_INTEGER q;
        QueryPerformanceCounter(&q);
        const LONG since = InterlockedExchange(&g_fxRemSince, 0), draws = InterlockedExchange(&g_fxDraws, 0);
        const DWORD gapUs = g_fxGatherQpc && g_cfQpf ? (DWORD)((q.QuadPart - g_fxGatherQpc) * 1000000 / g_cfQpf) : 0;
        if (draws && (LONG)(gapUs / 1000) > k.fxGatherGapMaxMs) k.fxGatherGapMaxMs = (LONG)(gapUs / 1000);  // only while drawing
        g_fxGatherQpc = q.QuadPart;
        InterlockedIncrement(&g_fxGseq);
        FxPut(FX_GATHER, CdRead((DWORD)(uintptr_t)self + 0x210), (DWORD)since, gapUs, (DWORD)draws, 0, 0, 0,
              (const DWORD*)_AddressOfReturnAddress());
    }
    ((FxNodeFn)(uintptr_t)g_fxGatherFn)(self, edx);
}
static void __fastcall FxDrawHook(u8* self, void* edx, DWORD a, DWORD b) {
    if (g_fxOn) {
        InterlockedIncrement(&g_fxDraws);
        const LONG since = g_fxRemSince, g = g_fxGseq;
        if (since > 0) {
            InterlockedIncrement(&ts3native_counters.fxStaleDraw);
            if (InterlockedExchange(&g_fxStaleMark, g) != g) {  // first stale draw after this gather
                const u8* list = (const u8*)(uintptr_t)CdRead((DWORD)(uintptr_t)self + 0x210);
                const DWORD dang = list ? FxDangling(list) : 0;
                // docs/186 §10: is the drawable still waiting in the pending list (its gather deferred by a budgeted drain)?
                DWORD holder = 0, drained = 0;
                const bool queued = CdNodeQueued((DWORD)(uintptr_t)self, &holder, &drained);
                if (queued) InterlockedIncrement(&ts3native_counters.mhFxQueued);
                FxPut(FX_STALE, (DWORD)(uintptr_t)list, (DWORD)since, dang, queued, drained, 0, 0, (const DWORD*)_AddressOfReturnAddress());
                if (InterlockedIncrement(&g_fxStaleLogs) <= 16) {
                    LARGE_INTEGER q;
                    QueryPerformanceCounter(&q);
                    Log("crash forensics: Swarm draw on stale lists: %ld removes since the last gather (%.1f ms ago, gather %ld), "
                        "%lu dangling bucket nodes, thread %lu (%s); drawable node %s, holder 0x%08lX, last drain processed %lu nodes",
                        since, g_cfQpf ? (double)(q.QuadPart - g_fxGatherQpc) * 1000.0 / (double)g_cfQpf : -1.0, g, dang & 0x7FFFFFFF,
                        GetCurrentThreadId(), FxThr(GetCurrentThreadId()),
                        queued ? "QUEUED (its gather is waiting for a drain)" : "not queued", holder, drained);
                }
            }
        }
    }
    ((FxDrawFn)(uintptr_t)g_fxDrawFn)(self, edx, a, b);
}

static void FxLine(const char* pre, const FxEnt& t, const LARGE_INTEGER& now) {
    char c0[96] = "-", c1[96] = "-";
    if (!CdCode(t.code[0], c0, sizeof(c0))) sprintf_s(c0, "0x%08lX", t.code[0]);
    if (t.code[1] && !CdCode(t.code[1], c1, sizeof(c1))) sprintf_s(c1, "0x%08lX", t.code[1]);
    const double ms = g_cfQpf ? (double)(now.QuadPart - t.qpc) * 1000.0 / (double)g_cfQpf : -1.0;
    if (t.tag == FX_ADD || t.tag == FX_REM)
        Log("%s%s index %lu entry 0x%08lX owner 0x%08lX instance 0x%08lX object %08lX%08lX, %.1f ms ago, thread %lu (%s), gather %ld, "
            "from %s %s", pre, kFxTag[t.tag], t.index, t.entry, t.owner, t.inst, t.idHi, t.idLo, ms, t.tid, FxThr(t.tid), t.gseq, c0, c1);
    else if (t.tag == FX_STALE)  // owner = drawable queued (docs/186 §10), inst = nodes the holder's last drain processed
        Log("%sstale-draw list 0x%08lX, removes since %lu, dangling %lu, drawable %s (last drain %lu nodes), %.1f ms ago, thread %lu "
            "(%s), gather %ld, from %s %s", pre, t.list, t.index, t.entry & 0x7FFFFFFF, t.owner ? "QUEUED" : "not queued", t.inst, ms,
            t.tid, FxThr(t.tid), t.gseq, c0, c1);
    else
        Log("%s%s list 0x%08lX, removes since %lu, %s %lu, draws %lu, %.1f ms ago, thread %lu (%s), gather %ld, from %s %s", pre,
            t.tag < FX_NTAGS ? kFxTag[t.tag] : "?", t.list, t.index, t.tag == FX_GATHER ? "gap us" : "dangling", t.entry, t.owner, ms,
            t.tid, FxThr(t.tid), t.gseq, c0, c1);
}
// at a DRAW BAD of the effect site: the newest remove and add of that entry, the newest gather of the list
static void FxExplain(DWORD entry, DWORD list) {
    if (!g_fxOn) return;
    LARGE_INTEGER now;
    QueryPerformanceCounter(&now);
    const LONG top = g_fxSeq;
    bool rem = false, add = false, gat = false;
    for (LONG s = top; s > 0 && top - s < (LONG)kFxRing && !(rem && add && gat); s--) {
        const FxEnt& t = g_fxRing[(DWORD)(s - 1) & (kFxRing - 1)];
        if (t.seq != s || t.list != list) continue;
        if (t.tag == FX_REM && !rem && t.entry == entry) rem = true, FxLine("crash forensics:   effect log: entry ", t, now);
        else if (t.tag == FX_ADD && !add && t.entry == entry) add = true, FxLine("crash forensics:   effect log: entry ", t, now);
        else if (t.tag == FX_GATHER && !gat) gat = true, FxLine("crash forensics:   effect log: newest ", t, now);
    }
    Log("crash forensics:   effect log: %s%s%sremoves since the last gather %ld, now gather %ld, stale draws %ld, removes off main %ld",
        rem ? "" : "no remove of this entry in the ring; ", add ? "" : "no add of it in the ring; ",
        gat ? "" : "no gather of this list in the ring; ", g_fxRemSince, g_fxGseq, ts3native_counters.fxStaleDraw,
        ts3native_counters.fxRemOffMain);
}
// crash dump part 3: counters, the newest 24 removes, 8 gathers and 8 stale draws
static void FxDump() {
    if (!g_fxOn) return;
    const Ts3nCounters& k = ts3native_counters;
    Log("crash forensics: effect log: hooks 0x%lX, %ld events, removes %ld (%ld off main), stale-list draws %ld, max gather gap %ld ms, "
        "removes since the last gather %ld, gather %ld", k.fxReady, k.fxRecs, k.fxRem, k.fxRemOffMain, k.fxStaleDraw, k.fxGatherGapMaxMs,
        g_fxRemSince, g_fxGseq);
    LARGE_INTEGER now;
    QueryPerformanceCounter(&now);
    static const struct { u8 tag; int max; } want[] = {{FX_REM, 24}, {FX_GATHER, 8}, {FX_STALE, 8}, {FX_ADD, 8}};
    const LONG top = g_fxSeq;
    for (const auto& w : want) {
        int n = 0;
        for (LONG s = top; s > 0 && top - s < (LONG)kFxRing && n < w.max; s--) {
            const FxEnt& t = g_fxRing[(DWORD)(s - 1) & (kFxRing - 1)];
            if (t.seq == s && t.tag == w.tag) n++, FxLine("  ", t, now);
        }
    }
}
// one vtable slot -> hook, only while it still holds the game's function (another module's swap wins; we skip)
static bool FxSlot(DWORD slot, DWORD expect, void* hook, const char* what) {
    DWORD* s = (DWORD*)(uintptr_t)slot;
    if (!Readable(s, 4) || *s != expect) {
        Log("SKIP crash forensics effect log %s: vtable slot 0x%08lX holds 0x%08lX, not 0x%08lX", what, slot, Readable(s, 4) ? *s : 0, expect);
        return false;
    }
    const DWORD h = (DWORD)(uintptr_t)hook;
    if (!Write(s, &h, 4)) return false;
    Log("OK   crash forensics effect log %s: vtable slot 0x%08lX -> stub (record only)", what, slot);
    return true;
}
static void FxInstallApex() {  // ApexRadiance.asi's image bounds: its code words count on the stack scans
    if (const HMODULE a = GetModuleHandleW(L"ApexRadiance.asi")) {
        const u8* b = (const u8*)a;
        g_fxApexLo = (DWORD)(uintptr_t)b;
        g_fxApexHi = g_fxApexLo + ((const IMAGE_NT_HEADERS*)(b + ((const IMAGE_DOS_HEADER*)b)->e_lfanew))->OptionalHeader.SizeOfImage;
    }
}
static void FxInstall() {
    Ts3nCounters& k = ts3native_counters;
    FxInstallApex();
    g_fxOn = 1;
    if (FxSlot(kFxSlotAdd, g_fxAddFn, (void*)FxAddHook, "add 0x71FD20")) k.fxReady |= 1;
    if (FxSlot(kFxSlotRem, g_fxRemFn, (void*)FxRemHook, "remove 0x71FF10")) k.fxReady |= 2;
    if (FxSlot(kFxSlotGather, g_fxGatherFn, (void*)FxGatherHook, "gather 0x722190")) k.fxReady |= 4;
    if (FxSlot(kFxSlotDraw, g_fxDrawFn, (void*)FxDrawHook, "draw 0x71D660")) k.fxReady |= 8;
    Log("crash forensics: docs/186 §9: effect log %s (ring %lu events, hooks 0x%lX), Apex image %s", k.fxReady ? "on" : "off", kFxRing,
        k.fxReady, g_fxApexLo ? "seen" : "not loaded yet");
}

// ---- docs/186 §10: ObjResLog=1, the SceneModel model handle (object +0x210) ----
// Round 2's resource DRAW BAD: SceneModelFur (vtable 0xFF9B50, "Renderer/SceneModelFur", base SceneModel 0xFF97F8) drew with
// handle words 386EA490 0 0 0: the model (+0) loaded, the current LOD (+8) empty. The handle (ctor 0x6DD580, 0x74 bytes) holds
// five tagged resource slots (+0 model, +4 pending model, +8 current LOD = what 0x6DB810 returns, +0xC loading LOD, +0x10
// override; low bits 3 = a key still loading), a version +0x1C bumped by every release, the key +0x28..+0x34 and a ready byte
// +0x5E [verified Steam 1.67 disassembly + Ghidra]. Who empties +8:
//   0x6DBCE0 unload LODs (+4/+8/+0xC/+0x10 = 0, model kept: round 2's state; with a key at +0x30 it is the full release
//            0x6DBB50 instead). Only caller: call 0x6F734E in UpdateLOD 0x6F7300 (SceneModel vfunc +0x40, LOD / visibility
//            pass), taken when it is asked to drop the model (first argument 0)
//   0x6DBB50 full release (all five slots, model too): calls 0x6DCC93 (SetModel 0x6DCC90), 0x6DCD23 / 0x6DCD34 (reload
//            0x6DCD20, also reached from UpdateLOD's load request 0x6DCD90, call 0x6F7331), 0x6DD353 (handle destructor)
// Who fills +8: 0x6DC5B0 (LOD select and swap-in, +8 = the loaded +0xC) from the node update 0x6F7B40 (SceneModel vfunc
// +0x48, call 0x6F7B5D), which also rebuilds the draw flags +0x40. UpdateLOD MarkDirty's the node (0x6FAC70) when the version
// changed, so in the game the next BeginFrame drain refills +8 or clears the ready flags before anything draws; the fur draw
// 0x6FA0B0 reads +8 without a check (the base draw 0x6F6250 checks 0x6DBA50 for 0). Apex SceneNodeBudget's budgeted drain
// can leave that node queued for frames [inferred: the same handoff as the Swarm gather, §9.2].
// Hooks (call-site retargets, no code bytes; each target byte-checked by PatchCalls): unload 0x6F734E, load request 0x6F7331,
// full release 0x6DCC93 / 0x6DCD23 / 0x6DCD34 / 0x6DD353 (ring), node update 0x6F7B5D (ring only when +8 changes; a 4096-slot
// last-update table always). The DRAW BAD of the resource site (§8 site 1) prints the handle, the node's queue state and the
// newest ring events and node update of that handle.
static const DWORD kOrUnloadCall = 0x006F734E, kOrLoadCall = 0x006F7331, kOrUpdCall = 0x006F7B5D;
static const DWORD kOrRelCall[4] = {0x006DCC93, 0x006DCD23, 0x006DCD34, 0x006DD353};
static DWORD g_orUnloadFn = 0x006DBCE0, g_orLoadFn = 0x006DCD90, g_orRelFn = 0x006DBB50, g_orUpdFn = 0x006DC5B0;
enum OrTag : u8 { OR_NONE, OR_UNLOAD, OR_LOAD, OR_RELEASE, OR_SET, OR_NTAGS };
static const char* const kOrTag[OR_NTAGS] = {"?", "lod-unload", "load-request", "release", "lod-set"};
struct OrEnt {
    volatile LONG seq;  // written last
    DWORD h;            // the handle (object + 0x210)
    DWORD p8, p8after;  // slot +8 before (and after, lod-set)
    DWORD p0, ver, key; // slot +0, version +0x1C, key type +0x30 (non-zero: unload / load request also run the full release)
    DWORD tid, code[2]; // thread; caller and the next code word above it (unload / load: 0x6F7300's caller)
    u8 tag, pad[3];
    DWORD pad2[3];
    LONGLONG qpc;
};
static_assert(sizeof(OrEnt) == 64, "model handle ring entry");
static const DWORD kOrRing = 8192;  // power of two
static OrEnt g_orRing[kOrRing];
struct OrLast { DWORD h, caller, p8, ver; LONGLONG qpc; };  // newest node update per handle (direct-mapped, main thread)
static const DWORD kOrUpd = 4096;
static OrLast g_orUpd[kOrUpd];
static volatile LONG g_orSeq;
static LONG g_orOn;
static DWORD OrSlot(DWORD h) { return (h >> 4) & (kOrUpd - 1); }

typedef void(__fastcall* OrHandleFn)(u8* h, void*);
typedef DWORD(__fastcall* OrUpdFn)(u8* h, void*);

// sp = the caller's slot on the stack (code words via FxCodes), or NULL with code0 = the caller (node update)
static void OrPut(u8 tag, const u8* h, DWORD p8, DWORD p8after, const DWORD* sp, DWORD code0) {
    const LONG s = InterlockedIncrement(&g_orSeq);
    OrEnt& e = g_orRing[(DWORD)(s - 1) & (kOrRing - 1)];
    const DWORD a = (DWORD)(uintptr_t)h;
    e.seq = 0;
    e.h = a, e.p8 = p8, e.p8after = p8after, e.p0 = CdRead(a), e.ver = CdRead(a + 0x1C), e.key = CdRead(a + 0x30);
    e.tid = GetCurrentThreadId(), e.tag = tag;
    if (sp) FxCodes(sp, e.code);
    else e.code[0] = code0, e.code[1] = 0;
    LARGE_INTEGER q;
    QueryPerformanceCounter(&q);
    e.qpc = q.QuadPart;
    _WriteBarrier();
    e.seq = s;
    ts3native_counters.mhRecs = s;
}
// unload / load request: called inside UpdateLOD 0x6F7300 (push esi only), so [sp+2] is UpdateLOD's own return address
static void __fastcall OrUnloadHook(u8* h, void* edx) {
    if (g_orOn) {
        InterlockedIncrement(&ts3native_counters.mhUnload);
        if (CdRead((DWORD)(uintptr_t)h + 0x30)) InterlockedIncrement(&ts3native_counters.mhRelease);  // 0x6DBCE0 jumps to 0x6DBB50
        OrPut(OR_UNLOAD, h, CdRead((DWORD)(uintptr_t)h + 8), 0, (const DWORD*)_AddressOfReturnAddress() + 2, 0);
    }
    ((OrHandleFn)(uintptr_t)g_orUnloadFn)(h, edx);
}
static void __fastcall OrLoadHook(u8* h, void* edx) {  // the reload's release (0x6DCD23) has its own record
    if (g_orOn) OrPut(OR_LOAD, h, CdRead((DWORD)(uintptr_t)h + 8), 0, (const DWORD*)_AddressOfReturnAddress() + 2, 0);
    ((OrHandleFn)(uintptr_t)g_orLoadFn)(h, edx);
}
static void __fastcall OrRelHook(u8* h, void* edx) {
    if (g_orOn) {
        InterlockedIncrement(&ts3native_counters.mhRelease);
        OrPut(OR_RELEASE, h, CdRead((DWORD)(uintptr_t)h + 8), 0, (const DWORD*)_AddressOfReturnAddress(), 0);
    }
    ((OrHandleFn)(uintptr_t)g_orRelFn)(h, edx);
}
// node update: caller = 0x6F7B40's return address ([ebp+4] at the call 0x6F7B5D: the drain that ran it, 0x6E41xx = the game's,
// ApexRadiance.asi+... = Apex's budgeted copy, other TS3W = one of the other drains)
static DWORD __stdcall OrUpd(u8* h, DWORD caller) {
    const DWORD a = (DWORD)(uintptr_t)h, before = g_orOn ? CdRead(a + 8) : 0;
    const DWORD r = ((OrUpdFn)(uintptr_t)g_orUpdFn)(h, NULL);
    if (g_orOn) {
        const DWORD after = CdRead(a + 8);
        LARGE_INTEGER q;
        QueryPerformanceCounter(&q);
        OrLast& u = g_orUpd[OrSlot(a)];
        u.h = a, u.caller = caller, u.p8 = after, u.ver = CdRead(a + 0x1C), u.qpc = q.QuadPart;
        if (after != before) InterlockedIncrement(&ts3native_counters.mhLodSet), OrPut(OR_SET, h, before, after, NULL, caller);
    }
    return r;
}
static __declspec(naked) void OrUpdThunk() {
    __asm {
        push dword ptr [ebp + 4]  // 0x6F7B40 has an ebp frame (push ebp; mov ebp, esp) [verified]
        push ecx
        call OrUpd
        ret
    }
}
static void OrLine(const char* pre, const OrEnt& t, const LARGE_INTEGER& now, LONG s) {
    char c0[96] = "-", c1[96] = "", to[24] = "";
    if (!CdCode(t.code[0], c0, sizeof(c0))) sprintf_s(c0, "0x%08lX", t.code[0]);
    if (t.code[1] && !CdCode(t.code[1], c1, sizeof(c1))) sprintf_s(c1, "0x%08lX", t.code[1]);
    if (t.tag == OR_SET) sprintf_s(to, " -> 0x%08lX", t.p8after);
    Log("%s%s handle 0x%08lX (object 0x%08lX): +8 0x%08lX%s, +0 0x%08lX, version %lu, key type %lX, %.1f ms ago, thread %lu (%s), "
        "seq %ld, from %s %s", pre, t.tag < OR_NTAGS ? kOrTag[t.tag] : "?", t.h, t.h - 0x210, t.p8, to, t.p0, t.ver, t.key,
        g_cfQpf ? (double)(now.QuadPart - t.qpc) * 1000.0 / (double)g_cfQpf : -1.0, t.tid, FxThr(t.tid), s, c0, c1);
}
// at the resource site's DRAW BAD (§8 site 1, obj = 0x6FA0B0's object): handle state, queue state, who emptied +8
static void OrExplain(DWORD obj) {
    const DWORD h = obj + 0x210;
    char w[200];
    CdWords(h, 8, w, sizeof(w));
    DWORD holder = 0, drained = 0;
    const bool queued = CdNodeQueued(obj, &holder, &drained);
    Log("crash forensics:   model handle words: %s; version %lu (object saw %lu), key type %lX, ready byte %u; object flags +0x40 "
        "%08lX +0x44 %08lX, +0x29C %02lX, fur on %lu; scene node %s (link %08lX/%08lX), holder 0x%08lX, last drain processed %lu nodes",
        w, CdRead(h + 0x1C), CdRead(obj + 0x2A0), CdRead(h + 0x30), (CdRead(h + 0x5C) >> 16) & 0xFF, CdRead(obj + 0x40),
        CdRead(obj + 0x44), CdRead(obj + 0x29C) & 0xFF, CdRead(obj + 0x2DC) & 0xFF,
        queued ? "QUEUED (its node update 0x6F7B40 has not run since MarkDirty)" : "not queued", CdRead(obj + 0x18), CdRead(obj + 0x1C),
        holder, drained);
    if (!g_orOn) { Log("crash forensics:   model handle log off (ObjResLog=0)"); return; }
    LARGE_INTEGER now;
    QueryPerformanceCounter(&now);
    const OrLast& u = g_orUpd[OrSlot(h)];
    char c[96] = "-";
    if (u.h == h && !CdCode(u.caller, c, sizeof(c))) sprintf_s(c, "0x%08lX", u.caller);
    if (u.h == h)
        Log("crash forensics:   model handle log: newest node update %.1f ms ago from %s, left +8 0x%08lX, version %lu",
            g_cfQpf ? (double)(now.QuadPart - u.qpc) * 1000.0 / (double)g_cfQpf : -1.0, c, u.p8, u.ver);
    else
        Log("crash forensics:   model handle log: no node update of this handle in the table (slot holds 0x%08lX)", u.h);
    const LONG top = g_orSeq;
    int n = 0;
    for (LONG s = top; s > 0 && top - s < (LONG)kOrRing && n < 8; s--) {
        const OrEnt& t = g_orRing[(DWORD)(s - 1) & (kOrRing - 1)];
        if (t.seq == s && t.h == h) n++, OrLine("crash forensics:   model handle log: ", t, now, s);
    }
    if (!n) Log("crash forensics:   model handle log: no unload, load request, release or LOD swap of this handle in the ring (%ld events)", top);
}
// crash dump part 4: counters and the newest 12 events of each kind
static void OrDump() {
    if (!g_orOn) return;
    const Ts3nCounters& k = ts3native_counters;
    Log("crash forensics: model handle log: hooks 0x%lX, %ld events, unloads %ld, full releases %ld, LOD swaps %ld, Swarm stale draws "
        "with the drawable queued %ld", k.mhReady, k.mhRecs, k.mhUnload, k.mhRelease, k.mhLodSet, k.mhFxQueued);
    LARGE_INTEGER now;
    QueryPerformanceCounter(&now);
    const LONG top = g_orSeq;
    for (u8 tag = 1; tag < OR_NTAGS; tag++) {
        int n = 0;
        for (LONG s = top; s > 0 && top - s < (LONG)kOrRing && n < 12; s--) {
            const OrEnt& t = g_orRing[(DWORD)(s - 1) & (kOrRing - 1)];
            if (t.seq == s && t.tag == tag) n++, OrLine("  ", t, now, s);
        }
    }
}
static void OrInstall() {
    Ts3nCounters& k = ts3native_counters;
    if (!g_fxApexLo) FxInstallApex();
    const CallSite un = {kOrUnloadCall, g_orUnloadFn, (void*)OrUnloadHook}, ld = {kOrLoadCall, g_orLoadFn, (void*)OrLoadHook},
                   up = {kOrUpdCall, g_orUpdFn, (void*)OrUpdThunk};
    const CallSite rel[4] = {{kOrRelCall[0], g_orRelFn, (void*)OrRelHook}, {kOrRelCall[1], g_orRelFn, (void*)OrRelHook},
                             {kOrRelCall[2], g_orRelFn, (void*)OrRelHook}, {kOrRelCall[3], g_orRelFn, (void*)OrRelHook}};
    g_orOn = 1;
    if (PatchCalls("crash forensics model handle unload 0x6F734E (record only)", &un, 1)) k.mhReady |= 1;
    if (PatchCalls("crash forensics model handle load request 0x6F7331 (record only)", &ld, 1)) k.mhReady |= 2;
    if (PatchCalls("crash forensics model handle full release 0x6DBB50 callers (record only)", rel, 4)) k.mhReady |= 4;
    if (PatchCalls("crash forensics model handle node update 0x6F7B5D (record only)", &up, 1)) k.mhReady |= 8;
    Log("crash forensics: docs/186 §10: model handle log %s (ring %lu events, update table %lu, hooks 0x%lX)", k.mhReady ? "on" : "off",
        kOrRing, kOrUpd, k.mhReady);
}

// ---- docs/67 §16: DbIndexLog=1, the DBPF database index (Crash N, sim thread at 0x72DBDA) ----
// DatabaseIndex (alloc name "ResourceSystem/DatabaseIndex", 0x24 bytes): +4 bucket array, +8 bucket count, +0xC elements, +0x20
// refcount (AddRef 0x72D890, Release 0x72DB80 frees at 0). The DBPF database D (vtable 0xFFE078, outer object O = D - 8, vtable
// 0xFFE0D0) holds one reference at D+0xB0. Contains 0x72DBD0 (only caller 0x7345FD, D slot 0x34) runs under D's lock D+0x40;
// the releases at 0x734532 (D slot 8) and 0x734409 (O slot 0xC) take no lock [verified disassembly]. Record only: every release
// (index, refcount before, database from the caller's esi, D lock held by this thread, thread, caller) into a ring, and a check
// before Contains reads (bucket array 0 / count 0 / refcount < 1) that logs the index's ring history; then the game's code runs.
static const DWORD kDiRelAt = 0x0072DB80, kDiHasAt = 0x0072DBD0;
static const u8 kDiRelPro[] = {0x56, 0x8B, 0xF1, 0x57, 0x8D, 0x46, 0x20};              // push esi; mov esi,ecx; push edi; lea eax,[esi+20h]
static const u8 kDiHasPro[] = {0x8B, 0x41, 0x08, 0x8B, 0x51, 0x04, 0x83, 0xEC, 0x08};  // mov eax,[ecx+8]; mov edx,[ecx+4]; sub esp,8
struct DiEnt {
    volatile LONG seq;
    DWORD idx, db, ret, tid;
    LONG count;  // refcount before the release (1 = this release frees the index)
    LONG held;   // D lock owner is this thread: 1 yes, 0 no, -1 unknown (esi not a database or a mutex lock)
    LONGLONG qpc;
};
static const DWORD kDiRing = 1024;  // power of two
static DiEnt g_diRing[kDiRing];
static volatile LONG g_diSeq, g_diFrees, g_diFreesUnlocked, g_diBad, g_diLogs;
static void* g_diRelOrig;
static void* g_diHasOrig;

// the database behind the caller's esi (D or O) and whether this thread owns its lock D+0x40
static DWORD DiDb(DWORD esi, LONG* held) {
    *held = -1;
    DWORD d = 0;
    __try {
        const DWORD vt = *(const DWORD*)(uintptr_t)esi;
        d = vt == 0x00FFE078 ? esi : vt == 0x00FFE0D0 ? esi + 8 : 0;
        if (d && *(const u8*)(uintptr_t)(d + 0x40 + 0x24))  // EA lock kind byte: nonzero = CRITICAL_SECTION at +0
            *held = (DWORD)(uintptr_t)((const CRITICAL_SECTION*)(uintptr_t)(d + 0x40))->OwningThread == GetCurrentThreadId();
    } __except (EXCEPTION_EXECUTE_HANDLER) { d = 0; }
    return d;
}
static void __stdcall DiRelNote(DWORD idx, DWORD esi, DWORD ret) {
    LONG count = 0, held;
    __try { count = *(const LONG*)(uintptr_t)(idx + 0x20); } __except (EXCEPTION_EXECUTE_HANDLER) { count = -9999; }
    const DWORD db = DiDb(esi, &held);
    if (count == 1) {
        InterlockedIncrement(&g_diFrees);
        if (held != 1) InterlockedIncrement(&g_diFreesUnlocked);
    }
    const LONG s = InterlockedIncrement(&g_diSeq);
    DiEnt& e = g_diRing[(DWORD)(s - 1) & (kDiRing - 1)];
    e.seq = 0;
    e.idx = idx, e.db = db ? db : esi, e.ret = ret, e.tid = GetCurrentThreadId(), e.count = count, e.held = db ? held : -1;
    LARGE_INTEGER q;
    QueryPerformanceCounter(&q);
    e.qpc = q.QuadPart;
    _WriteBarrier();
    e.seq = s;
}
static void DiLine(const char* pre, const DiEnt& t, const LARGE_INTEGER& now) {
    Log("%sindex 0x%08lX release (count before %ld%s) db 0x%08lX D lock %s, thread %lu, caller 0x%08lX, %.1f ms ago", pre, t.idx,
        t.count, t.count == 1 ? " = FREED" : "", t.db, t.held == 1 ? "held" : t.held == 0 ? "NOT held" : "?", t.tid, t.ret,
        g_cfQpf ? (double)(now.QuadPart - t.qpc) * 1000.0 / (double)g_cfQpf : -1.0);
}
// every ring event of this index (newest 8), and our P5 heap / P5c ring entries for its address
static void DiExplain(const char* what, DWORD idx) {
    LARGE_INTEGER now;
    QueryPerformanceCounter(&now);
    const LONG top = g_diSeq;
    int n = 0;
    for (LONG s = top; s > 0 && top - s < (LONG)kDiRing && n < 8; s--) {
        const DiEnt& t = g_diRing[(DWORD)(s - 1) & (kDiRing - 1)];
        if (t.seq == s && t.idx == idx) n++, DiLine("crash forensics:   db index log: ", t, now);
    }
    char d1[220], d2[220];
    CfDescribe(idx, d1, sizeof(d1));
    if (!P5cRelFind(idx, d2, sizeof(d2))) strcpy_s(d2, "no");
    Log("crash forensics:   db index log: %s 0x%08lX: %d release events in the ring (%ld total); P5 heap ring: %s; P5c: %s", what, idx, n,
        top, d1, d2);
}
static void __stdcall DiHasCheck(DWORD idx, DWORD esi, DWORD ret) {
    DWORD w[10] = {};
    bool readable = true;
    __try { memcpy(w, (const void*)(uintptr_t)idx, sizeof(w)); } __except (EXCEPTION_EXECUTE_HANDLER) { readable = false; }
    if (readable && w[1] && w[2] && (LONG)w[8] >= 1) return;
    InterlockedIncrement(&g_diBad);
    if (InterlockedIncrement(&g_diLogs) > 16) return;
    LONG held;
    const DWORD db = DiDb(esi, &held);
    DWORD slot = 0;
    __try { slot = db ? *(const DWORD*)(uintptr_t)(db + 0xB0) : 0; } __except (EXCEPTION_EXECUTE_HANDLER) {}
    Log("crash forensics: DB INDEX BAD at 0x72DBD0: index 0x%08lX %s buckets 0x%08lX count %lu elements %lu refcount %ld, db 0x%08lX "
        "slot +0xB0 0x%08lX D lock %s, thread %lu, caller 0x%08lX", idx, readable ? "" : "UNREADABLE", w[1], w[2], w[3], (LONG)w[8],
        db ? db : esi, slot, held == 1 ? "held" : held == 0 ? "NOT held" : "?", GetCurrentThreadId(), ret);
    DiExplain("index", idx);
}
// thunks: the caller's esi (the database at the DBPF call sites) and return address, ecx (the index) kept for the game's code
static __declspec(naked) void DiRelThunk() {
    __asm {
        push ecx
        push dword ptr [esp + 4]
        push esi
        push ecx
        call DiRelNote
        pop ecx
        jmp dword ptr [g_diRelOrig]
    }
}
static __declspec(naked) void DiHasThunk() {
    __asm {
        push ecx
        push dword ptr [esp + 4]
        push esi
        push ecx
        call DiHasCheck
        pop ecx
        jmp dword ptr [g_diHasOrig]
    }
}
// crash dump part: counts, the fault registers that are in the index ring, the newest 8 frees
static void DiDump(const CONTEXT* c) {
    if (!g_diRelOrig) return;
    Log("crash forensics: db index log: %ld releases, %ld frees (%ld without the D lock), %ld bad Contains checks", g_diSeq, g_diFrees,
        g_diFreesUnlocked, g_diBad);
    const struct { const char* n; DWORD v; } regs[] = {{"eax", c->Eax}, {"ebx", c->Ebx}, {"ecx", c->Ecx}, {"edx", c->Edx},
                                                       {"esi", c->Esi}, {"edi", c->Edi}, {"ebp", c->Ebp}};
    const LONG top = g_diSeq;
    for (const auto& g : regs) {
        bool in = false;
        for (LONG s = top; s > 0 && top - s < (LONG)kDiRing && !in; s--) in = g_diRing[(DWORD)(s - 1) & (kDiRing - 1)].idx == g.v;
        if (in) DiExplain(g.n, g.v);
    }
    LARGE_INTEGER now;
    QueryPerformanceCounter(&now);
    int n = 0;
    for (LONG s = top; s > 0 && top - s < (LONG)kDiRing && n < 8; s--) {
        const DiEnt& t = g_diRing[(DWORD)(s - 1) & (kDiRing - 1)];
        if (t.seq == s && t.count == 1) n++, DiLine("crash forensics:   db index free: ", t, now);
    }
}
static void DiInstall() {
    g_diRelOrig = InstallDetour("crash forensics db index release 0x72DB80 (record only)", kDiRelAt, kDiRelPro, sizeof(kDiRelPro), (void*)DiRelThunk, &g_diRelOrig);
    g_diHasOrig = InstallDetour("crash forensics db index contains 0x72DBD0 (check only)", kDiHasAt, kDiHasPro, sizeof(kDiHasPro), (void*)DiHasThunk, &g_diHasOrig);
    Log("crash forensics: docs/67 §16: db index log release %s, contains check %s (ring %lu events)", g_diRelOrig ? "on" : "off",
        g_diHasOrig ? "on" : "off", kDiRing);
}

// TS3W.exe .rdata bounds from the loaded image (the vtables of its classes)
static void CfRdata() {
    const u8* base = (const u8*)GetModuleHandleW(NULL);
    const IMAGE_NT_HEADERS* nt = (const IMAGE_NT_HEADERS*)(base + ((const IMAGE_DOS_HEADER*)base)->e_lfanew);
    const IMAGE_SECTION_HEADER* s = IMAGE_FIRST_SECTION(nt);
    for (int i = 0; i < nt->FileHeader.NumberOfSections; i++, s++)
        if (!memcmp(s->Name, ".rdata", 6)) g_cfVtLo = (DWORD)(uintptr_t)(base + s->VirtualAddress), g_cfVtHi = g_cfVtLo + s->Misc.VirtualSize;
}

// 0x7B0A00: the stolen `je` is rel8, so InstallDetour cannot copy it; this trampoline re-encodes it
static CfClrFn CfClrTramp() {
    u8* t = (u8*)VirtualAlloc(NULL, 32, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
    if (!t) return NULL;
    memcpy(t, kCfClrPro, 4);                                                  // cmp dword ptr [ecx+30h],0
    t[4] = 0x0F, t[5] = 0x84, *(LONG*)(t + 6) = (LONG)(0x007B0A24 - ((DWORD)(uintptr_t)t + 10));  // je 0x7B0A24
    t[10] = 0xE9, *(LONG*)(t + 11) = (LONG)(0x007B0A06 - ((DWORD)(uintptr_t)t + 15));            // jmp 0x7B0A06
    return (CfClrFn)t;
}
static bool CfJmp(DWORD at, const u8* pro, size_t len, void* hook) {
    if (!Matches((const void*)(uintptr_t)at, pro, len)) { Log("SKIP crash forensics guard at 0x%08lX: prologue differs", at); return false; }
    u8 j[8];
    memset(j, 0x90, sizeof(j));
    j[0] = 0xE9, *(LONG*)(j + 1) = (LONG)((DWORD)(uintptr_t)hook - (at + 5));
    return Write((void*)(uintptr_t)at, j, len);
}

static bool ApplyCrashForensics(const wchar_t* ini) {
    // docs/67 §18: Light=1 = only the parts with no detour on a game path (P5 heap ring, heap check at our frees, P5c park /
    // release lines, our-events ring, fault dump incl. illegal instructions); Enabled=1 = all of that plus the guard switches
    const bool full = GetPrivateProfileIntW(L"CrashForensics", L"Enabled", 0, ini) != 0;
    if (!full && !GetPrivateProfileIntW(L"CrashForensics", L"Light", 0, ini)) return false;
    Ts3nCounters& k = ts3native_counters;
    LARGE_INTEGER f;
    QueryPerformanceFrequency(&f);
    g_cfQpf = f.QuadPart;
    CfRdata();
    // heap layout: the game's Free entry or our [Alloc] detour in front of it (alloc.cpp); anything else owns the chunks
    if (GetPrivateProfileIntW(L"CrashForensics", L"HeapCheck", 1, ini))
        g_cfHeap = Matches((const void*)(uintptr_t)EA_FREE, kEaLockedPrologue, sizeof(kEaLockedPrologue)) || g_eaFreeOrig != NULL;
    g_cfOn = 1;
    k.cfReady = 1 | (g_cfHeap ? 8 : 0);
    if (!full) {
        g_ceOn = 1, k.cfReady |= 16, k.cdReady |= 16;
        Log("crash forensics: light (docs/67 §18): ring %lu P5 heap events + g_rpUse holds, heap check %s, our-events ring %lu events, "
            "fault dump; no guards (Enabled=1 for those)", kCfRing, g_cfHeap ? "on" : "off (EA Free entry not the game's or ours)", kCeRing);
        return true;
    }
    if (GetPrivateProfileIntW(L"CrashForensics", L"Guards", 1, ini)) {
        g_cfEraseOrig = (CfEraseFn)InstallDetour("crash forensics map erase 0x5EE1E0 (check only)", kCfEraseAt, kCfErasePro, sizeof(kCfErasePro), (void*)CfEraseHook, &g_cfEraseOrig);
        if (g_cfEraseOrig) k.cfReady |= 2;
        CfClrFn t = Matches((const void*)(uintptr_t)kCfClrAt, kCfClrPro, sizeof(kCfClrPro)) ? CfClrTramp() : NULL;
        if (t) {
            g_cfClrOrig = t;
            if (CfJmp(kCfClrAt, kCfClrPro, sizeof(kCfClrPro), (void*)CfClrHook)) k.cfReady |= 4, Log("OK   detour crash forensics array clear 0x7B0A00 (check only)");
        } else Log("SKIP crash forensics guard at 0x7B0A00: prologue differs");
        // docs/67 §15: the parent check before the 0x958944 path
        g_xgOrig = (XgFn)InstallDetour("crash forensics transform parent 0x958560 (check only)", kXgAt, kXgPro, sizeof(kXgPro), (void*)XgHook, &g_xgOrig);
    }
    // docs/186 §8: our-events ring and the draw guards (both default off)
    if (GetPrivateProfileIntW(L"CrashForensics", L"OurEvents", 0, ini)) g_ceOn = 1, k.cdReady |= 16;
    if (GetPrivateProfileIntW(L"CrashForensics", L"DrawGuards", 0, ini)) CdInstall();
    // docs/186 §9: Swarm render list ring (default off); adds to the guards and rings above, replaces nothing
    if (GetPrivateProfileIntW(L"CrashForensics", L"EffectLog", 0, ini)) FxInstall();
    // docs/186 §10: SceneModel model handle ring (default off); adds to everything above
    if (GetPrivateProfileIntW(L"CrashForensics", L"ObjResLog", 0, ini)) OrInstall();
    // docs/67 §16: DBPF database index release ring + Contains check (default off); adds to everything above
    if (GetPrivateProfileIntW(L"CrashForensics", L"DbIndexLog", 0, ini)) DiInstall();
    // docs/67 §17: scene node check before SceneNode_FindRecursive reads (default off); adds to everything above
    if (GetPrivateProfileIntW(L"CrashForensics", L"SceneTreeGuard", 0, ini))
        g_stOrig = InstallDetour("crash forensics scene node search 0xC5FA60 (check only)", kStAt, kStPro, sizeof(kStPro), (void*)StThunk, &g_stOrig);
    Log("crash forensics: docs/186 §8: our-events ring %s (%lu events), draw guards model %s resource %s effect %s binding %s",
        g_ceOn ? "on" : "off", kCeRing, k.cdReady & 1 ? "on" : "off", k.cdReady & 2 ? "on" : "off", k.cdReady & 4 ? "on" : "off",
        k.cdReady & 8 ? "on" : "off");
    Log("crash forensics: on (docs/157 §11.7): ring %lu P5 heap events + g_rpUse holds, heap check %s, guards %s%s, .rdata 0x%08lX-0x%08lX",
        kCfRing, g_cfHeap ? "on" : "off (EA Free entry not the game's or ours)", k.cfReady & 2 ? "erase " : "", k.cfReady & 4 ? "clear" : "",
        g_cfVtLo, g_cfVtHi);
    return true;
}
