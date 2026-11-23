// Crash S fix and writer watch (docs/67 §22.7, ts3native 0.36.100; filtered watch §22.8, 0.36.101). Included by ts3native.cpp after watchpt.cpp (CrStack).
//
// 0.36.99 caught it: 184 stopped marks saw interpreter arena chunks whose last word (EA's GC descriptor, 0xE50DE0 puts it at
// chunk + 0x7FC) and the 3 words before were 0; a chunk marked by descriptor 0 scans none of its frames, so the GC frees
// what only those frames hold (the freed delegates of every Crash S). Each chunk's frame area ends at c[1] = chunk + 0x7FC,
// exactly at the descriptor, and both grant paths (method entry 0xE563B2, grow 0xD71900) give a frame that ends at c[1].
// A write a few bytes past a frame's declared size ([rm+0x38] = args + eval stack + vt stack + locals, 0xE63D0E) is harmless
// above top in mid-chunk (dead frames) but lands on the descriptor when that frame is the chunk's last.
//
// [ArenaGuard] TailPad=1: 64 bytes between every chunk's frame area and its descriptor. Six byte patches in EA's arena code,
// all or none [verified bytes]:
//   0xD718CC  lea edx,[eax+0x7FC]        -> +0x7BC   pool chunk end (refill 0xD718A0)
//   0xD71946  lea ecx,[ebp+0xC]          -> +0x4C    grow: frame + header + pad decides pool chunk (<= 0x7FC) vs own chunk
//   0xD719EA  own chunk of size+0xC+pad: end = chunk + size + 0xC (its descriptor sits >= 0x40 beyond, rounded up)
//   0xD71A4E / 0xD71AEA / 0xD71B88  "end - chunk == 0x7FC" (pool chunk: back to the pool, else GC_free) -> 0x7BC
// Every fast path compares against c[1], so no frame reaches the last 64 bytes. Applied at load, before MonoScriptHost_Init
// fills the pool; chunks that already exist keep end + 0x7FC and are GC_free'd instead of pooled when released (logged).
//
// [GcRootCheck] TailWatch=1 (needs Enabled=1, logging only): names the writer. The method-entry bump 0xE563B2 jumps to
// AtBump; a frame that leaves < 16 bytes before c[1] arms DR0 on c[1] (the descriptor) and DR1 on c[1]+4 of that chunk on
// the sim thread (a RaiseException our vectored handler answers by setting the debug registers in the context). Re-armed
// only when another chunk's frame comes near its end.
// 0.36.101 (docs/67 §22.8): data breakpoints trap AFTER the write, so a hit's eip is the next instruction. The 0.36.100 run's
// 16 lines (0xE563D3, 0xE54453, 0xD71A06/14/85/8C at end+4 / end+12) were all EA writing the ADJACENT chunk's header
// (chunks are 0x800 apart: end+4 = next c[0] top, end+12 = next c[2] link): 0xE563D1 add [chunk],size (bump),
// 0xE54451 mov [chunk],base (pop), arena 0xD71A03/0xD71A12/0xD71A83/0xD71A85. Those are legal and filtered (counted only).
// Any other write to end+0 or end+4 is logged once per distinct (eip, armed method), up to 64, and counted per eip.

static const DWORD AT_PAD = 0x40;
static const DWORD AT_ARM_CODE = 0xE0415441;  // RaiseException code our handler answers (XcptLog skips it)

struct AtPatch { DWORD at; u8 len; u8 old[15]; u8 neu[15]; };
static const AtPatch kAtPatches[6] = {
    {TS3A(0x00D718CC), 6, {0x8D, 0x90, 0xFC, 0x07, 0x00, 0x00}, {0x8D, 0x90, 0xBC, 0x07, 0x00, 0x00}},
    {TS3A(0x00D71946), 3, {0x8D, 0x4D, 0x0C}, {0x8D, 0x4D, 0x4C}},
    // mov edi,eax; add esp,8; lea edx,[edi+0xC]; add esi,edi; mov [edi],edx; mov [edi+4],esi  ->
    // xchg eax,edi; pop ecx; pop ecx; lea edx,[edi+0xC]; mov [edi],edx; lea esi,[esi+edi-0x40]; mov [edi+4],esi
    // (eax and ecx are reloaded at 0xD719F9 / 0xD719FD)
    {TS3A(0x00D719EA), 15, {0x8B, 0xF8, 0x83, 0xC4, 0x08, 0x8D, 0x57, 0x0C, 0x03, 0xF7, 0x89, 0x17, 0x89, 0x77, 0x04},
     {0x97, 0x59, 0x59, 0x8D, 0x57, 0x0C, 0x89, 0x17, 0x8D, 0x74, 0x3E, 0xC0, 0x89, 0x77, 0x04}},
    {TS3A(0x00D71A4E), 6, {0x81, 0xFA, 0xFC, 0x07, 0x00, 0x00}, {0x81, 0xFA, 0xBC, 0x07, 0x00, 0x00}},
    {TS3A(0x00D71AEA), 5, {0x3D, 0xFC, 0x07, 0x00, 0x00}, {0x3D, 0xBC, 0x07, 0x00, 0x00}},
    {TS3A(0x00D71B88), 6, {0x81, 0xF9, 0xFC, 0x07, 0x00, 0x00}, {0x81, 0xF9, 0xBC, 0x07, 0x00, 0x00}},
};
static int g_atPadOn;
static DWORD g_atPoolAtPatch;  // EA's pool sentinel when the pad went in (0 = before MonoScriptHost_Init, the normal case)

// all six or none; base = where the image's 0xD71000 page sits (the game: 0, so absolute; selftest: a copy)
static bool AtApplyPad(u8* (*at)(DWORD va)) {
    for (const AtPatch& p : kAtPatches)
        if (!Matches(at(p.at), p.old, p.len)) return false;
    for (const AtPatch& p : kAtPatches)
        if (!Write(at(p.at), p.neu, p.len)) return false;  // shortcut: a failed write after the first leaves a partial patch; VirtualProtect on .text does not fail in practice
    return true;
}
static u8* AtGameVa(DWORD va) { return (u8*)(uintptr_t)va; }

// --- InitobjFix (docs/67 §22.9, 0.36.102): the writer ---
// 0.36.101 caught it at end+0: trap eip MSVCR80 memset+0x61 (after `rep stosd`), return address 0xE58A09 = MINT_INITOBJ
// (handler 0xE589F7: memset(sp[-1].p, 0, size32)), in List<ulong>.RemoveAt (`ldloca 0; initobj !T`, its only local, last
// in the frame). The transform emits that size at 0xE63427 as mono_class_value_size 0xE6F5A0 = instance_size - 4 (EA's
// MonoObject is one word); Int64/UInt64/Double (and enums over them) align their field to 8 (mono_type_size 0xE9C3A0), so
// the size is 12 while the slot is 8: every such initobj zeroes 4 bytes past its slot. [ArenaGuard] InitobjFix=1 retargets
// that call to AtInitobjSize: the smaller of EA's size and the storage size (equal for value types; never more than EA).
typedef int(__cdecl* AtSizeFn)(void* klassOrType, int* align);
static AtSizeFn g_atValueSize = (AtSizeFn)(uintptr_t)TS3A(0x00E6F5A0), g_atTypeSize = (AtSizeFn)(uintptr_t)TS3A(0x00E9C3A0);  // selftest swaps them
static volatile LONG g_atInitobjClamped;
static int g_atInitobjOn;
static int __cdecl AtInitobjSize(void* klass, int* align) {
    const int v = g_atValueSize(klass, align);
    u8* type = (u8*)klass + 0xA0;  // &klass->byval_arg (0xE6339A); MonoType.type at +6 (0xE9C3B9)
    if (type[6] == 0x13 || type[6] == 0x1E) return v;  // VAR / MVAR: open generic, keep EA's size
    int a = 0;
    const int s = g_atTypeSize(type, &a);
    if (s <= 0 || s >= v) return v;
    InterlockedIncrement(&g_atInitobjClamped);
    return s;
}
// site = the 5-byte `call oldTarget`; false (nothing written) when it is not that call
static bool AtApplyInitobj(u8* site, const void* oldTarget) {
    u8 b[5] = {0xE8};
    *(DWORD*)(b + 1) = (DWORD)(uintptr_t)oldTarget - ((DWORD)(uintptr_t)site + 5);
    if (!Matches(site, b, 5)) return false;
    *(DWORD*)(b + 1) = (DWORD)(uintptr_t)AtInitobjSize - ((DWORD)(uintptr_t)site + 5);
    return Write(site, b, 5);
}

// --- TailWatch ---
static volatile LONG g_atNear, g_atArms, g_atHits, g_atHitLines, g_atHdr, g_atDistOver;
static DWORD g_atArmChunk, g_atArmAddr, g_atArmRm, g_atArmBase, g_atArmEnd;
static DWORD g_atLastEip, g_atLastAddr;  // selftest
static PVOID g_atVeh;
static DWORD g_atBack = TS3A(0x00E563BF), g_atSlow = TS3A(0x00E563D5);
static const DWORD AT_DR7 = (0xDDu << 16) | 5;  // L0, L1, each R/W 01 (write) LEN 11 (4 bytes)
static const int AT_DIST = 64;
struct AtDist { DWORD eip, rm, off; LONG n; };
static AtDist g_atDist[AT_DIST];  // sim thread only (DRs are armed there), no lock
static LONG g_atDistN;

// trap eip (one past the write) of EA's own writes to the next chunk's header: end+4 = its top, end+12 = its link
static bool AtHeaderWriter(DWORD eip, DWORD off) {
    if (off != 4) return false;
    return (eip >= TS3A(0x00D71900) && eip < TS3A(0x00D71C00)) || eip == TS3A(0x00E563D3) || eip == TS3A(0x00E54453);
}

static void AtArm(DWORD addr) {  // DR0-DR3 = [addr, addr+16) on this thread, 0 = disarm
    g_atArmAddr = addr;
    RaiseException(AT_ARM_CODE, 0, 0, NULL);
}

static bool AtOwnsXcpt(const EXCEPTION_POINTERS* e) {  // XcptLog: ours, not a crash
    const DWORD code = e->ExceptionRecord->ExceptionCode;
    if (code == AT_ARM_CODE) return true;
    const CONTEXT* c = e->ContextRecord;
    return code == EXCEPTION_SINGLE_STEP && g_atArmAddr && (c->Dr6 & 0xF) && c->Dr0 == g_atArmAddr;
}

static void AtHitLine(const CONTEXT* c, DWORD addr) {
    char armed[120] = "?", st[700] = "unreadable", cur[120] = "-";
    DWORD a[5] = {};
    __try {
        FbName(armed, sizeof(armed), *(const DWORD*)(uintptr_t)(g_atArmRm + 8));
        const DWORD rm = g_atArmRm;
        a[0] = *(const WORD*)(uintptr_t)(rm + 0x3E), a[1] = *(const WORD*)(uintptr_t)(rm + 0x34), a[2] = *(const WORD*)(uintptr_t)(rm + 0x36),
        a[3] = *(const WORD*)(uintptr_t)(rm + 0x3C), a[4] = *(const WORD*)(uintptr_t)(rm + 0x38);
    } __except (EXCEPTION_EXECUTE_HANDLER) {}
    DWORD irm = 0;  // inside ves_exec (0xE53EB0, helpers keep ebp): the running RuntimeMethod at [ebp-0x10] (MonoFaultText)
    if (c->Eip >= TS3A(0x00E51000) && c->Eip < TS3A(0x00E5A600)) {
        __try {
            irm = *(const DWORD*)(uintptr_t)(c->Ebp - 0x10);
            if (irm) FbName(cur, sizeof(cur), *(const DWORD*)(uintptr_t)(irm + 8));
        } __except (EXCEPTION_EXECUTE_HANDLER) {}
    }
    if (FbStackText(st, sizeof(st), 12) <= 0) strcpy_s(st, "unreadable");
    DWORD v = 0, f[6] = {};
    __try { v = *(const DWORD*)(uintptr_t)addr; } __except (EXCEPTION_EXECUTE_HANDLER) {}
    const int nf = CrStack(c->Esp, f, 6);
    Log("arena tail watch: hit %ld: %08lX (end %08lX + %lu) written by eip %08lX (value now %08lX); armed for %s (rm %08lX base %08lX, args %lX "
        "stack %lX vt %lX locals %lX = %lX) in chunk %08lX end %08lX; interpreter now %s (rm %08lX); script stack: %s; native stack (%d) %08lX %08lX "
        "%08lX %08lX %08lX %08lX",
        g_atHits, addr, g_atArmEnd, addr - g_atArmEnd, c->Eip, v, armed, g_atArmRm, g_atArmBase, a[0], a[1], a[2], a[3], a[4], g_atArmChunk,
        g_atArmEnd, cur, irm, st, nf, f[0], f[1], f[2], f[3], f[4], f[5]);
}

static LONG CALLBACK AtVeh(EXCEPTION_POINTERS* e) {
    CONTEXT* c = e->ContextRecord;
    const DWORD code = e->ExceptionRecord->ExceptionCode;
    if (code == AT_ARM_CODE) {
        const DWORD a = g_atArmAddr;
        c->Dr0 = a, c->Dr1 = a ? a + 4 : 0, c->Dr2 = c->Dr3 = 0;
        c->Dr6 = 0;
        c->Dr7 = (c->Dr7 & 0x0000FF00) | (a ? AT_DR7 : 0);
        c->ContextFlags |= CONTEXT_DEBUG_REGISTERS;
        return EXCEPTION_CONTINUE_EXECUTION;
    }
    if (code != EXCEPTION_SINGLE_STEP || !g_atArmAddr || !(c->Dr6 & 0xF) || c->Dr0 != g_atArmAddr) return EXCEPTION_CONTINUE_SEARCH;
    const DWORD dr[2] = {c->Dr0, c->Dr1};
    for (int i = 0; i < 2; i++) {
        if (!(c->Dr6 & (1u << i))) continue;
        const DWORD off = dr[i] - g_atArmEnd;
        if (AtHeaderWriter(c->Eip, off)) { g_atHdr++; continue; }
        InterlockedIncrement(&g_atHits);
        g_atLastEip = c->Eip, g_atLastAddr = dr[i];
        LONG k = 0;
        while (k < g_atDistN && !(g_atDist[k].eip == c->Eip && g_atDist[k].rm == g_atArmRm)) k++;
        if (k < g_atDistN) { g_atDist[k].n++; continue; }
        if (k == AT_DIST) { g_atDistOver++; continue; }
        g_atDist[k] = {c->Eip, g_atArmRm, off, 1};
        g_atDistN++;
        if (InterlockedIncrement(&g_atHitLines) <= AT_DIST) AtHitLine(c, dr[i]);
    }
    c->Dr6 = 0;  // stays armed: data breakpoints are traps (no RF needed)
    return EXCEPTION_CONTINUE_EXECUTION;
}

// sim thread, from AtBump: a frame that ends < 16 bytes before its chunk's c[1] (top is still the frame's base here)
static void __cdecl AtNear(DWORD rm, DWORD chunk) {
    g_atNear++;
    if (chunk == g_atArmChunk) return;
    const DWORD* k = (const DWORD*)(uintptr_t)chunk;
    g_atArmChunk = chunk, g_atArmRm = rm, g_atArmBase = k[0], g_atArmEnd = k[1];
    g_atArms++;
    AtArm(k[1]);
}

// 0xE563B2..0xE563BE (13 bytes, jmp + 8 nop): eax = state's current chunk, edi = callee RuntimeMethod. Same test as EA's
// (end - top < [rm+0x38] -> 0xE563D5 grow), then AtNear when the frame leaves < 16 bytes; back at 0xE563BF (ecx, edx and
// the flags are dead there: 0xE563BF reloads eax, 0xE563C1 ecx, 0xE563C7 edx)
static __declspec(naked) void AtBump() {
    __asm {
        mov ecx, dword ptr [eax + 4]
        movzx edx, word ptr [edi + 0x38]
        sub ecx, dword ptr [eax]
        cmp ecx, edx
        jl grow
        sub ecx, edx
        cmp ecx, 16
        jb nearend
        jmp dword ptr [g_atBack]
    nearend:
        pushad
        push eax
        push edi
        call AtNear
        add esp, 8
        popad
        jmp dword ptr [g_atBack]
    grow:
        jmp dword ptr [g_atSlow]
    }
}
static const u8 kAtBumpOld[13] = {0x8B, 0x48, 0x04, 0x0F, 0xB7, 0x57, 0x38, 0x2B, 0x08, 0x3B, 0xCA, 0x7C, 0x16};

static bool ApplyArenaTailWatch(const wchar_t* ini) {
    if (!GetPrivateProfileIntW(L"GcRootCheck", L"Enabled", 0, ini) || !GetPrivateProfileIntW(L"GcRootCheck", L"TailWatch", 0, ini)) return false;
    if (GetPrivateProfileIntW(L"Watchpoint", L"Enabled", 0, ini)) { Log("SKIP arena tail watch: [Watchpoint] owns the debug registers"); return false; }
    u8* site = (u8*)(uintptr_t)TS3A(0x00E563B2);
    if (!Matches(site, kAtBumpOld, sizeof(kAtBumpOld))) { Log("SKIP arena tail watch: 0xE563B2 bytes differ"); return false; }
    if (!(g_atVeh = AddVectoredExceptionHandler(1, AtVeh))) { Log("FAIL arena tail watch: AddVectoredExceptionHandler"); return false; }
    u8 j[13];
    j[0] = 0xE9;
    *(DWORD*)(j + 1) = (DWORD)(uintptr_t)AtBump - (TS3A(0x00E563B2) + 5);
    memset(j + 5, 0x90, 8);
    if (!Write(site, j, sizeof(j))) { Log("FAIL arena tail watch: write 0xE563B2"); return false; }
    Log("OK   arena tail watch: method-entry bump 0xE563B2 -> AtBump; DR0 on end (descriptor), DR1 on end+4 of a chunk whose new frame leaves < 16 "
        "bytes (sim thread); EA's next-chunk header writes filtered");
    return true;
}

static bool ApplyArenaTail(const wchar_t* ini) {
    bool any = false;
    if (GetPrivateProfileIntW(L"ArenaGuard", L"TailPad", 0, ini)) {
        __try { g_atPoolAtPatch = *(const DWORD*)(uintptr_t)TS3A(0x011EE508); } __except (EXCEPTION_EXECUTE_HANDLER) {}
        if (AtApplyPad(AtGameVa)) {
            g_atPadOn = 1, any = true;
            Log("OK   arena tail pad: 6 sites, %lu bytes kept free before every frame-arena chunk's descriptor%s", AT_PAD,
                g_atPoolAtPatch ? " (pool already existed: its chunks keep the old end and are GC_free'd when released)" : "");
        } else Log("SKIP arena tail pad: EA arena bytes differ, nothing written");
    } else Log("arena tail pad disabled (TailPad=0)");
    if (GetPrivateProfileIntW(L"ArenaGuard", L"InitobjFix", 0, ini)) {
        if (AtApplyInitobj((u8*)(uintptr_t)TS3A(0x00E63427), (const void*)(uintptr_t)TS3A(0x00E6F5A0))) {
            g_atInitobjOn = 1, any = true;
            Log("OK   initobj size: transform call 0xE63427 -> AtInitobjSize (Int64/UInt64/Double initobj zeroes 8 bytes, not 12)");
        } else Log("SKIP initobj size: 0xE63427 is not call 0xE6F5A0, nothing written");
    } else Log("initobj size fix disabled (InitobjFix=0)");
    any = ApplyArenaTailWatch(ini) || any;
    return any;
}

static void AtLogCounters() {
    if (g_atInitobjOn) Log("initobj size: %ld initobj sizes clamped (transform, once per method)", g_atInitobjClamped);
    if (!g_atPadOn && !g_atVeh) return;
    char per[400] = "";
    int used = 0;
    for (LONG k = 0; k < g_atDistN && used < (int)sizeof(per) - 40; k++) {  // per eip (entries of one eip summed)
        bool seen = false;
        for (LONG j = 0; j < k; j++) seen = seen || g_atDist[j].eip == g_atDist[k].eip;
        if (seen) continue;
        LONG n = 0;
        for (LONG j = k; j < g_atDistN; j++) if (g_atDist[j].eip == g_atDist[k].eip) n += g_atDist[j].n;
        used += sprintf_s(per + used, sizeof(per) - used, " %08lX=%ld", g_atDist[k].eip, n);
    }
    Log("arena tail: pad %s; watch near-end frames %ld, arms %ld, hits %ld (distinct %ld, past 64 %ld; per eip%s), next-chunk header writes %ld",
        g_atPadOn ? "on" : "off", g_atNear, g_atArms, g_atHits, g_atDistN, g_atDistOver, used ? per : " -", g_atHdr);
}
