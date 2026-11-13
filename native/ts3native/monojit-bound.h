// [MonoJit] BoundCall (docs/537 §22): a guarded callvirt / con.callvirt site whose inline-cache target has an EA native body
// (binder, RuntimeMethod+0x24 != 0) on the allow list below calls that body through EA's own 0xE525C0 instead of exiting to
// the interpreter. Shared by ts3native (monojit.cpp) and monohost (monohost-jit.cpp); included after Em / Fix.
// EA's path [verified: disassembly 0xE56287-0xE563ED, 0xE5925F-0xE59270, 0xE525C0]: push the callee's frame (MonoInvocation
// +0 parent, +4 RuntimeMethod, +8 return slot = the caller's eval top, +0xC this, +0x14 args = top - 8 * params, +0x1C/+0x20/
// +0x24 = 0; ThreadContext+8 = the frame), then 0xE525C0(frame, ctx): setjmp, ctx+0x10 = its jmp_buf, ctx+0xC = frame, call
// [rm+0x24](frame, ctx), both put back; an exception is in frame+0x20 (written by the body, or by a native raise through the
// jmp_buf). Then the return copies the slot's 8 bytes to the caller's new top. JitBoundCall does the same with the frame on
// its own stack and no frame record, depth or arena bump (the listed bodies read none of them). Calling 0xE525C0 itself keeps
// its setjmp and any detour on it ([AutShadow] Satb's SatbBindCall), so a body runs exactly as from EA's interpreter.
#pragma once
#include "ts3addr.h"

// One row per allowed body: its invoker (+0x24), its body (+0x28), and a callee it reaches; FNV-1a of those bytes (Steam 1.67)
// must match at install, else the row stays off. All rows: readers with no managed call, allocation or throw once `this` is
// not null (docs/220 class `reader`, confidence high; the guard bails on a null receiver before this path).
struct JitBoundCallee { const char* name; DWORD inv, invN, body, bodyN, extra, extraN, fnv; };
static const JitBoundCallee kJitBound[] = {
    // hash loop over the chars; [0x11EF7BC] (OffsetToStringData's icall, 8) under the IC-nesting setjmp; null this = NRE in +0x20
    {"System.String.GetHashCode()", TS3A(0x00DBFBF0), 0x28, TS3A(0x00DBFAE0), 0x104, 0, 0, 0x958D9EA6},
    // [0x11F06E0] = Equals(string,string)'s RuntimeMethod: its bound body 0xDA4200 (compare lengths, then the chars) when +0x28 != 0
    {"System.String.Equals(string)", TS3A(0x00DDD900), 0x2E, TS3A(0x00DDD880), 0x78, TS3A(0x00DA4200), 0x10A, 0x8B23206C},
};
enum { JIT_BOUND_N = sizeof(kJitBound) / sizeof(kJitBound[0]) };
// counters v194 (docs/registry.md), 12 int32; ts3native points g_jitBc at its block, the host keeps its own
struct JitBoundStats {
    LONG on;          // the allow-list mask in force (0 = BoundCall off); a runtime switch too (--ab boundcall): 0 = every site bails as before
    LONG fills;       // guard fills that found an allowed bound body (target changes, not calls)
    LONG hits;        // bound bodies called from compiled code
    LONG exc;         // of the calls, an exception in the frame: the site exited and EA's call ran it again
    LONG notAllowed;  // guard fills that found an EA native body not on the list (the site keeps bailing "no lazy body")
    LONG vtThis;      // guard fills refused: a bound target with a value-type `this` (RuntimeMethod+0x44 & 0x400)
    LONG callee[4];   // hits by kJitBound row
    LONG spare[2];
};
static JitBoundStats g_jitBcOwn;
static JitBoundStats* g_jitBc = &g_jitBcOwn;
static DWORD g_jitBoundInv[JIT_BOUND_N];  // invoker address of each allowed row, 0 = row off
static unsigned g_jitBoundMask;            // the mask installed at apply: guard sites get the BoundCall path only when != 0
typedef void(__cdecl* JitBoundEntryFn)(DWORD frame, DWORD ctx);
static JitBoundEntryFn g_jitBoundEntry = (JitBoundEntryFn)(uintptr_t)TS3A(0x00E525C0);  // selftest: a stand-in

static DWORD JitBoundFnv(DWORD h, DWORD at, DWORD n) {
    for (DWORD i = 0; i < n; i++) h = (h ^ ((const u8*)(uintptr_t)at)[i]) * 0x01000193;
    return h;
}
// [MonoJit] BoundCallMask bits = kJitBound rows; rows whose bytes are not EA's stay off. Returns the mask in force.
static unsigned JitBoundInit(unsigned mask) {
    unsigned on = 0;
    for (int i = 0; i < JIT_BOUND_N; i++) {
        const JitBoundCallee& c = kJitBound[i];
        g_jitBoundInv[i] = 0;
        if (!(mask >> i & 1)) continue;
        DWORD h = JitBoundFnv(JitBoundFnv(0x811C9DC5, c.inv, c.invN), c.body, c.bodyN);
        if (c.extraN) h = JitBoundFnv(h, c.extra, c.extraN);
        if (h != c.fnv) continue;
        g_jitBoundInv[i] = c.inv, on |= 1u << i;
    }
    g_jitBc->on = (LONG)on, g_jitBoundMask = on;
    return on;
}
// JitGuardFill, for a transformed target with an EA native body: its row + 1, or 0 (the site bails as before)
static int JitBoundOf(const u8* rm) {
    if (!g_jitBoundMask) return 0;  // not `on`: a site filled while --ab has it off still runs bound once it is back on
    const DWORD fl = *(const DWORD*)(rm + 0x44), inv = *(const DWORD*)(rm + 0x24);
    if (fl & 0x400) { g_jitBc->vtThis++; return 0; }  // `this` would be struct data: not on the list (docs/206 §17)
    for (int i = 0; i < JIT_BOUND_N; i++)
        if (g_jitBoundInv[i] && inv == g_jitBoundInv[i]) { g_jitBc->fills++; return i + 1; }
    g_jitBc->notAllowed++;
    return 0;
}
// The site's call: top = the eval top (the args end there, `this` 8 bytes below them), self = the receiver as EA passes it,
// ebp = the interpreter's frame (ebp-8 the current MonoInvocation, ebp+0xC the ThreadContext). Returns the new top with the
// result copied as EA's return does, or 0 when the body left an exception (nothing of the caller's was written: the site exits
// and EA's call runs it again, which only the listed readers allow).
static DWORD __cdecl JitBoundCall(const u8* rm, DWORD* top, DWORD self, const DWORD* ebp) {
    const int n = rm[0x40];
    DWORD f[10] = {};  // MonoInvocation (0x28)
    f[0] = ebp[-2], f[1] = (DWORD)(uintptr_t)rm, f[2] = (DWORD)(uintptr_t)top, f[3] = self, f[5] = (DWORD)(uintptr_t)(top - 2 * n);
    u8* ctx = (u8*)(uintptr_t)ebp[3];
    const DWORD cur = *(const DWORD*)(ctx + 8);
    *(DWORD*)(ctx + 8) = (DWORD)(uintptr_t)f;  // EA's push sets it (0xE5636A); its return puts the caller's back
    g_jitBoundEntry((DWORD)(uintptr_t)f, (DWORD)(uintptr_t)ctx);
    *(DWORD*)(ctx + 8) = cur;
    if (f[8]) { g_jitBc->exc++; return 0; }
    g_jitBc->hits++;
    const DWORD inv = *(const DWORD*)(rm + 0x24);
    for (int i = 0; i < JIT_BOUND_N; i++) if (inv == g_jitBoundInv[i]) g_jitBc->callee[i]++;
    DWORD* slot = top - 2 * (n + 1);
    if (rm[0x44] == 1) return (DWORD)(uintptr_t)slot;  // void: nothing copied (EA's ret.void)
    slot[0] = top[0], slot[1] = top[1];
    return (DWORD)(uintptr_t)(slot + 2);
}
// At the guard's "no lazy body" jump (eax = the cache's target, ecx = the receiver): a bound target calls JitBoundCall; any
// other, ForceJIT, or an exception takes the site's exit (why 6 / 7, kJcnBail). *done jumps to the code after the call.
template <class S> static void JitBoundEmit(Em& e, S* gs, u8* nolazy, DWORD forceJit, u8** bail, u8* why, int& nb, u8** done) {
    Fix(nolazy, e.p);
    e.b(0x83); e.b(0x3D); e.d((DWORD)(uintptr_t)&gs->bound); e.b(0); why[nb] = 6, bail[nb++] = e.jcc(0x84);  // no bound body either
    e.b(0x83); e.b(0x3D); e.d((DWORD)(uintptr_t)&g_jitBc->on); e.b(0); why[nb] = 6, bail[nb++] = e.jcc(0x84);  // switched off at run time
    e.b(0x80); e.b(0x3D); e.d(forceJit); e.b(0x75); why[nb] = 7, bail[nb++] = e.jcc(0x85);                    // MonoPatcher ForceJIT
    e.b(0x50 + 5); e.b(0x50 + 1); e.b(0x50 + 6);                       // push ebp; push ecx (this); push esi (top): cdecl (rm, top, self, ebp)
    e.b(0xFF); e.b(0x35); e.d((DWORD)(uintptr_t)&gs->rm);              // push [site.rm]
    e.b(0xE8); e.d((DWORD)(uintptr_t)JitBoundCall - ((DWORD)(uintptr_t)e.p + 4));
    e.b(0x83); e.b(0xC4); e.b(16);                                     // add esp, 16
    e.rr(0x85, 0, 0); why[nb] = 6, bail[nb++] = e.jcc(0x84);           // test eax, eax: an exception, EA's call runs it again
    e.rr(0x8B, 0, 6);                                                  // mov esi, eax
    *done = e.jmp();
}
