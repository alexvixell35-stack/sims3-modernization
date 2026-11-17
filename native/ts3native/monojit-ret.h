// [MonoJit] RetTeardown (docs/537 §27; census piece B3 "ret frame teardown"): a compiled normal body's ret / ret.void runs
// EA's ret + frame return (0xE591FF / 0xE59236 + 0xE543C7, docs/130 §1.3) as one native block in the code region, then
// dispatches the caller's next word itself. Without it the ret is an exit: mov [ebp-4], edi; jmp [table + op*4] into EA's
// ret, the frame return and EA's dispatch head. ret.vt and every lazy-body ret are unchanged.
//
// The block is [MonoFast] CallPath's McRet / McRetVoid / McRetTail (mintcall.cpp, host-verified bit-exact against EA's
// path, docs/139 §8) emitted once into the JIT code region: every condition under which EA's path would do anything else
// (values left on the eval stack, depth 0, yield, pending exception, newobj return, arena pop across a chunk) is tested
// before any write and leaves to EA's handler from the exit's state (the bail is the old exit, byte for byte).
//
// Guards:
// - Off = zero instructions: RetTeardown=0 emits the old exit (EmitExit unchanged).
// - Verify (Mode=2, host verify): every VerifyEvery-th compiled ret that EA's straight-line return would take (JitRtEligible,
//   the block's own tests) runs twice from one snapshot: the block, then EA's handler. Both end in a dispatch of the caller's
//   next word, which is caught by pointing the caller record's saved ip ([rec + 4]) at a private JIT_OP word for the
//   duration (registered in the ip table; JitEnter -> verify -> JitRtTrap). The second catch compares everything both paths
//   may write (McVPre's ret ranges + the frame words, EA's dead scratch slots excepted) and edi / esi / esp, puts the real ip
//   back and dispatches it. A mismatch kills the piece.
// - Session kill switch: JitRtKill writes `jmp short bail` over the 2-byte nop at each entry (every emitted site then takes
//   the old exit) and no new site is emitted; why 1 = verify mismatch, 2 = fault.
// - Fault watch: a last vectored handler; a system fault with eip in the block kills the piece; inside the read-only tests
//   (before the first write) the fault resumes at the bail, so EA's handler runs the ret from the same state.
//
// Included twice by monojit.cpp and native/monohost/monohost-jit.cpp: first for the globals (before EmitExit), then with
// JRT_PART2 for the code (after JitOrig). The includer defines JRT_CTR (the struct with the rtd* LONG fields), JRT_TABLE
// (the live jump table, a DWORD expression), JRT_UNIMPL (EA's handler for words past 0x1C0) and JRT_NL ("" or "\n").

#ifndef MONOJIT_RET_H1
#define MONOJIT_RET_H1
static bool g_jitRtOn;                       // [MonoJit] RetTeardown in force (MONOHOST_JITRETTD=1)
static volatile LONG g_jitRtDead;            // the kill switch fired
static u8* g_jitRtEntry[2];                  // ret (0x06), ret.void (0x07)
static u8* g_jitRtBail[2];
static u8* g_jitRtWrite[2];                  // first write of each entry: a fault before it resumes at the bail
static u8* g_jitRtLo, * g_jitRtHi;           // the block
static __declspec(align(4)) unsigned short g_jitRtTrapW[2] = {(unsigned short)0x1A9, 0};  // JIT_OP: the verify catch word
static DWORD g_jitRtEvery = 16, g_jitRtTick;
static PVOID g_jitRtVeh;
#endif

#if defined(JRT_PART2) && !defined(MONOJIT_RET_H2)
#define MONOJIT_RET_H2
static_assert(0x1A9 == JIT_OP, "g_jitRtTrapW holds JIT_OP");

// the block: [bail 6][entry 6 ... jmp tail][bail 7][entry 7 ...][tail]; entry state of a mint handler (edi = ip of the ret,
// esi = sp, ebp = ves_exec frame; eax, ebx, ecx, edx free). Returns the end, or NULL if `room` is too small.
static u8* JitRtEmit(u8* p, size_t room) {
    if (room < 1024) return NULL;  // the block is ~0.5 KB
    Em e{p};
    u8* toTail[2];
    for (int k = 0; k < 2; k++) {
        const DWORD op = 6 + (DWORD)k;
        g_jitRtBail[k] = e.p;  // the old exit (EmitExit's bytes after mov edi, ip), counted
        e.op(0x89, EDI, EBP, -4);
        e.incAbs(&JRT_CTR.rtdBails);
        e.movRI(EAX, op);
        e.rr(0x8B, EAX, EDX);
        e.b(0xFF); e.b(0x25); e.d((DWORD)(JRT_TABLE) + op * 4);
        while ((uintptr_t)e.p & 3) e.b(0x90);
        g_jitRtEntry[k] = e.p;
        e.b(0x8B); e.b(0xFF);                                 // mov edi, edi: JitRtKill writes jmp short bail here
        u8* bail[10];
        int nb = 0;
        e.op(0x8B, ECX, EBP, -8);                             // callee MonoInvocation
        if (k == 0) { e.op(0x8D, EBX, ESI, -8); e.op(0x3B, EBX, ECX, 0x18); }  // values left on the eval stack: EA logs
        else e.op(0x3B, ESI, ECX, 0x18);
        bail[nb++] = e.jcc(0x87);
        e.op(0x8B, EAX, EBP, 8);                              // interpreter state
        e.op(0x83, 7, EAX, 0x140); e.b(0); bail[nb++] = e.jcc(0x84);   // depth 0: exit A
        e.op(0x80, 7, EAX, 0x149); e.b(0); bail[nb++] = e.jcc(0x85);   // yield: frame save
        e.op(0x83, 7, ECX, 0x20); e.b(0); bail[nb++] = e.jcc(0x85);    // pending exception
        e.op(0x8B, EDX, EBP, -0x2C); e.op(0x83, 7, EDX, 0x14); e.b(0); bail[nb++] = e.jcc(0x85);  // newobj return
        e.op(0x8B, EDX, ECX, 0x10); e.rr(0x85, EDX, EDX); bail[nb++] = e.jcc(0x84);              // no arena base
        e.op(0x8B, EAX, EAX, 0x13C); e.rr(0x85, EAX, EAX); bail[nb++] = e.jcc(0x84);             // no chunk
        e.op(0x8D, EBX, EAX, 0xC); e.rr(0x3B, EBX, EDX); bail[nb++] = e.jcc(0x86);               // base inside the chunk header
        e.op(0x3B, EDX, EAX, 0); bail[nb++] = e.jcc(0x87);                                        // base past the chunk top
        for (int i = 0; i < nb; i++) Fix(bail[i], g_jitRtBail[k]);
        g_jitRtWrite[k] = e.p;
        if (k == 0) {  // the 8-byte value to the callee's return slot ([inv+8])
            e.op(0x8B, EBX, ECX, 8);
            e.op(0x8B, EDX, ESI, -8); e.op(0x89, EDX, EBX, 0);
            e.op(0x8B, EDX, ESI, -4); e.op(0x89, EDX, EBX, 4);
        }
        toTail[k] = e.jmp();
    }
    // frame return 0xE543C7-0xE544A4 on its inline path (McRetTail): ecx = callee MonoInvocation, eax = arena chunk
    u8* const tail = e.p;
    Fix(toTail[0], tail), Fix(toTail[1], tail);
    e.op(0x8B, EDX, EBP, 8);
    e.op(0x8B, EBX, EDX, 0x140); e.b(0x4B); e.op(0x89, EBX, EDX, 0x140);   // depth--
    e.b(0x69); e.b(0xDB); e.d(0x130);                                      // imul ebx, ebx, 0x130
    e.b(0x8D); e.b(0x9C); e.b(0x1D); e.d((DWORD)-0x4C74);                  // lea ebx, [ebp + ebx - 0x4C74]: caller record
    e.op(0xFF, 6, EBP, -0x2C);                                             // push callee record
    e.op(0x8B, EDI, EBX, 4); e.op(0x8B, ESI, EBX, 8);
    e.op(0x8B, EDX, EBX, 0); e.op(0x89, EDX, EBP, -8);                     // caller MonoInvocation
    e.op(0x89, EDI, EBP, -4); e.op(0x89, EBX, EBP, -0x2C);
    e.op(0x8B, EDX, EDX, 4); e.op(0x89, EDX, EBP, -0x10);
    e.op(0x8B, EDX, EBX, 0xC); e.op(0x89, EDX, EBP, -0xC);
    e.op(0x8B, EDX, EBX, 0x10); e.op(0x89, EDX, EBP, -0x14);
    e.op(0x8B, EDX, EBP, 0xC); e.op(0xFF, 6, EBP, -8); e.op(0x8F, 0, EDX, 8);  // ThreadContext +8 = caller frame
    e.op(0x8B, EDX, ECX, 0x10); e.op(0x89, EDX, EAX, 0);                    // arena pop
    e.op(0xC7, 0, ECX, 0x10); e.d(0);
    e.op(0x8B, EDX, ECX, 4); e.op(0x80, 7, EDX, 0x44); e.b(1);             // no value to push
    e.b(0x5A);                                                             // pop edx (flags kept): callee record
    u8* next = e.jcc(0x84);
    e.op(0x8B, EDX, EDX, 0); e.op(0x8B, EDX, EDX, 8);                      // EA copies from the callee record's return slot
    e.op(0x8B, EAX, EDX, 0); e.op(0x89, EAX, ESI, 0);
    e.op(0x8B, EAX, EDX, 4); e.op(0x89, EAX, ESI, 4);
    e.addEsi(8); e.op(0x89, ESI, EBX, 8);
    Fix(next, e.p);
    e.b(0x0F); e.b(0xB7); e.b(0x07);                                       // movzx eax, word ptr [edi]
    e.b(0x0F); e.b(0xB7); e.b(0xD0);                                       // movzx edx, ax
    e.b(0x81); e.b(0xFA); e.d(0x1C0);
    u8* head = e.jcc(0x87);
    e.b(0xFF); e.b(0x24); e.b(0x95); e.d((DWORD)(JRT_TABLE));              // jmp [edx*4 + table]: the live entry
    Fix(head, e.p);
    e.jmpTo((const void*)(uintptr_t)(JRT_UNIMPL));
    g_jitRtLo = p, g_jitRtHi = e.p;
    return e.p;
}

// the block's tests in C, same order and sense: true = EA's ret takes its straight-line path to the dispatch
static bool JitRtEligible(DWORD ebp, DWORD esi, DWORD op) {
    const DWORD inv = *(const DWORD*)(uintptr_t)(ebp - 8), st = *(const DWORD*)(uintptr_t)(ebp + 8);
    if ((op == 6 ? esi - 8 : esi) > *(const DWORD*)(uintptr_t)(inv + 0x18)) return false;
    if (!*(const DWORD*)(uintptr_t)(st + 0x140) || *(const u8*)(uintptr_t)(st + 0x149) || *(const DWORD*)(uintptr_t)(inv + 0x20)) return false;
    if (*(const DWORD*)(uintptr_t)(*(const DWORD*)(uintptr_t)(ebp - 0x2C) + 0x14)) return false;
    const DWORD base = *(const DWORD*)(uintptr_t)(inv + 0x10), chunk = *(const DWORD*)(uintptr_t)(st + 0x13C);
    return base && chunk && base > chunk + 0xC && base <= *(const DWORD*)(uintptr_t)chunk;
}

static void JitRtPatch(u8* at, u8 b0, u8 b1) {
    DWORD old;
    VirtualProtect(at, 2, PAGE_EXECUTE_READWRITE, &old);
    at[1] = b1, at[0] = b0;  // entry is 4-byte aligned: the 2 bytes never straddle a line
    VirtualProtect(at, 2, old, &old);
    FlushInstructionCache(GetCurrentProcess(), at, 2);
}
static void JitRtKill(int why, const char* what) {
    if (InterlockedExchange(&g_jitRtDead, 1)) return;
    JRT_CTR.rtdOn = 0, JRT_CTR.rtdKillWhy = why;
    for (int k = 0; k < 2; k++)
        if (g_jitRtEntry[k]) JitRtPatch(g_jitRtEntry[k], 0xEB, (u8)(g_jitRtBail[k] - (g_jitRtEntry[k] + 2)));
    Log("FAIL mono jit ret teardown: %s: RetTeardown off for the session, compiled rets take EA's handler" JRT_NL, what);
}
// first-chance system errors (0xC0000000-0xCFFFFFFF) with eip in the block only; C++ throws, breakpoints, single steps pass
static LONG CALLBACK JitRtVeh(EXCEPTION_POINTERS* x) {
    const DWORD code = x->ExceptionRecord->ExceptionCode, eip = x->ContextRecord->Eip;
    if ((code & 0xF0000000) != 0xC0000000 || eip < (DWORD)(uintptr_t)g_jitRtLo || eip >= (DWORD)(uintptr_t)g_jitRtHi) return EXCEPTION_CONTINUE_SEARCH;
    InterlockedIncrement(&JRT_CTR.rtdFaults);
    char what[96];
    sprintf_s(what, "fault 0x%08lX at eip %08lX", code, eip);
    JitRtKill(2, what);
    for (int k = 0; k < 2; k++)
        if (eip >= (DWORD)(uintptr_t)g_jitRtEntry[k] && eip < (DWORD)(uintptr_t)g_jitRtWrite[k]) {  // read-only tests: nothing written yet
            x->ContextRecord->Eip = (DWORD)(uintptr_t)g_jitRtBail[k];
            return EXCEPTION_CONTINUE_EXECUTION;
        }
    return EXCEPTION_CONTINUE_SEARCH;  // a fault after a write: no exact resume point, the game's handlers see it
}
// install into `at` (the start of the code region, `room` bytes): the live ret / ret.void entries must be EA's or CallPath's
// (same path, verified). Returns the bytes used or 0 (piece off, reason logged).
static size_t JitRtInstall(u8* at, size_t room) {
    const DWORD* tab = (const DWORD*)(uintptr_t)(JRT_TABLE);
    for (int k = 0; k < 2; k++)
        if (tab[6 + k] != MC_EA[k] && !McOwns(6 + (DWORD)k, tab[6 + k])) {
            Log("jit: RetTeardown off: jump-table entry 0x%X is 0x%08lX, not EA's ret or CallPath's" JRT_NL, 6 + k, tab[6 + k]);
            return 0;
        }
    u8* end = JitRtEmit(at, room);
    if (!end) return 0;
    if (!g_jitRtVeh) g_jitRtVeh = AddVectoredExceptionHandler(0, JitRtVeh);
    g_jitRtDead = 0, JRT_CTR.rtdOn = 1, JRT_CTR.rtdKillWhy = 0;
    return (size_t)(end - at);
}

// ---- verify (Mode=2): one ret in flight, sim thread only ----
struct JrtRange { u8* at; DWORD n, off; };
struct JrtV {
    int state;  // 0 idle, 1 block running (trap set), 2 EA's handler running
    DWORD op, retIp, sp0, ebp, rec0, realIp, aEdi, aEsi, aEsp;
    LONG bails0;
    JrtRange rg[12];
    int nr;
    u8 s0[0x800], a[0x800];
};
static JrtV g_jrtV;
static int g_jrtLogged;
static bool JrtAdd(JrtV& v, DWORD at, DWORD n) {
    const DWORD off = v.nr ? v.rg[v.nr - 1].off + v.rg[v.nr - 1].n : 0;
    if (v.nr >= 12 || off + n > sizeof(v.s0) || at < 0x10000) return false;
    v.rg[v.nr++] = {(u8*)(uintptr_t)at, n, off};
    return true;
}
// a compiled ret in Mode=2 that no verified call owns (JitVerifySlow, fromExit): returns the block entry to run, or NULL
// (EA's handler as before). busy = a verification is in flight on this activation.
static void* JitRtBegin(JitRegs* r, DWORD op, bool busy) {
    JrtV& v = g_jrtV;
    if (!g_jitRtOn || g_jitRtDead || (op != 6 && op != 7) || v.state || busy || g_jcOn) return NULL;
    if (++g_jitRtTick % g_jitRtEvery) return NULL;
    const DWORD ebp = r->ebp;
    if (!JitRtEligible(ebp, r->esi, op)) return NULL;
    const DWORD inv = *(const DWORD*)(uintptr_t)(ebp - 8), st = *(const DWORD*)(uintptr_t)(ebp + 8);
    const DWORD d = *(const DWORD*)(uintptr_t)(st + 0x140), chunk = *(const DWORD*)(uintptr_t)(st + 0x13C);
    const DWORD rec0 = ebp - 0x4C74 + (d - 1) * 0x130;
    v.nr = 0;
    const bool ok = JrtAdd(v, ebp - 0x80, 0x80) && JrtAdd(v, st + 0x13C, 0x14) && JrtAdd(v, chunk, 0x10) &&
                    JrtAdd(v, *(const DWORD*)(uintptr_t)(ebp + 0xC), 0x18) && JrtAdd(v, rec0, 0x260) && JrtAdd(v, inv, 0x28) &&
                    JrtAdd(v, *(const DWORD*)(uintptr_t)rec0, 0x28) && JrtAdd(v, *(const DWORD*)(uintptr_t)(inv + 8), 8) &&
                    JrtAdd(v, *(const DWORD*)(uintptr_t)(rec0 + 8), 8) && JrtAdd(v, r->esi - 0x10, 0x10);
    if (!ok) { JRT_CTR.rtdVSkipped++; return NULL; }
    for (int i = 0; i < v.nr; i++) memcpy(v.s0 + v.rg[i].off, v.rg[i].at, v.rg[i].n);
    v.op = op, v.retIp = r->edi, v.sp0 = r->esi, v.ebp = ebp, v.rec0 = rec0, v.realIp = *(const DWORD*)(uintptr_t)(rec0 + 4);
    v.bails0 = JRT_CTR.rtdBails;
    *(DWORD*)(uintptr_t)(rec0 + 4) = (DWORD)(uintptr_t)g_jitRtTrapW;  // both paths' dispatch lands on the trap word
    v.state = 1;
    return g_jitRtEntry[op - 6];
}
static void* JitRtDone(JitRegs* r) {  // the real caller ip back where EA reads it, then its dispatch
    JrtV& v = g_jrtV;
    *(DWORD*)(uintptr_t)(v.rec0 + 4) = v.realIp;
    *(DWORD*)(uintptr_t)(v.ebp - 4) = v.realIp;
    v.state = 0;
    const DWORD op = *(const unsigned short*)(uintptr_t)v.realIp;
    if (op > 0x1C0) { r->edi = v.realIp, r->eax = r->edx = op; return (void*)(uintptr_t)(JRT_UNIMPL); }
    return JitOrig(r, v.realIp, op);
}
// JitVerifySlow, first thing: the trap word's dispatch (edi = g_jitRtTrapW)
static void* JitRtTrap(JitRegs* r) {
    JrtV& v = g_jrtV;
    if (v.state == 1 && JRT_CTR.rtdBails != v.bails0) { JRT_CTR.rtdVSkipped++; return JitRtDone(r); }  // the block bailed: EA already ran
    if (v.state == 1) {  // the block's result; then EA's handler from the same state
        for (int i = 0; i < v.nr; i++) memcpy(v.a + v.rg[i].off, v.rg[i].at, v.rg[i].n);
        v.aEdi = r->edi, v.aEsi = r->esi, v.aEsp = r->esp_;
        for (int i = v.nr - 1; i >= 0; i--) memcpy(v.rg[i].at, v.s0 + v.rg[i].off, v.rg[i].n);
        *(DWORD*)(uintptr_t)(v.rec0 + 4) = (DWORD)(uintptr_t)g_jitRtTrapW;
        r->esi = v.sp0;
        v.state = 2;
        return JitOrig(r, v.retIp, v.op);
    }
    char what[160] = "";
    if (r->edi != v.aEdi) sprintf_s(what, "edi block %08lX EA %08lX", v.aEdi, r->edi);
    else if (r->esi != v.aEsi) sprintf_s(what, "esi block %08lX EA %08lX", v.aEsi, r->esi);
    else if (r->esp_ != v.aEsp) sprintf_s(what, "esp block %08lX EA %08lX", v.aEsp, r->esp_);
    for (int i = 0; i < v.nr && !what[0]; i++)
        for (DWORD k = 0; k < v.rg[i].n; k++)
            if (v.rg[i].at[k] != v.a[v.rg[i].off + k] && !(i == 0 && McScratch(0x80 - (k & ~3u)))) {
                sprintf_s(what, "range %d +0x%lX (%08lX) byte block %02X EA %02X", i, k, (DWORD)(uintptr_t)(v.rg[i].at + k), v.a[v.rg[i].off + k], v.rg[i].at[k]);
                break;
            }
    const DWORD retIp = v.retIp, op = v.op;
    void* to = JitRtDone(r);
    if (!what[0]) { JRT_CTR.rtdVerified++; return to; }
    JRT_CTR.rtdMismatch++;
    if (g_jrtLogged++ < 16) Log("jit ret teardown verify MISMATCH op 0x%lX ret ip %08lX: %s" JRT_NL, op, retIp, what);
    JitRtKill(1, "verify mismatch");
    return to;
}
#endif
