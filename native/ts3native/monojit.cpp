// [MonoJit] (docs/527 step 1, docs/207 §7-§14, counters v908 jit*): the method compiler of native/monohost/monohost-jit.cpp
// in the game, without the inliner and count mode. Default OFF. Included by ts3native.cpp after autshadow.cpp (the store
// templates call Satb's log); ilpatch.cpp's detour of the transform entry calls JitPostPass after EA's transform.
// Direct=1 (docs/207 §12, docs/537, counters v909) adds the host's lazy frames: a call from compiled code to a compiled
// method writes no frame until the callee's first exit. Trap=1 is C6's reader trap around them.
//
// How it works (the host's, docs/207 §7): a listed method whose IL hash is known gets compiled from the mint code EA's
// transform just built. Every instruction start's opcode word becomes JIT_OP (0x1A9, EA's "Unimplemented" slot; leave.s
// keeps its word, JitWord); the jump-table entry of JIT_OP is JitEnter, which maps edi (ip) to native code through JitFind
// and jumps there on the sim thread. Native code runs inside the interpreter's own C frame, every local, argument and
// stack slot stays in arena memory, and anything without a template leaves through "exit to handler": edi = ip,
// [ebp-4] = ip, eax = edx = original opcode, jmp to the LIVE jump-table entry, so CallPath, Satb's swaps and MintDivGuard
// stay in effect. With Direct=0 exits leave no native frame behind: every call and return runs EA's or CallPath's handler.
//
// Any other thread (the [AutShadow] worker, the finalizer) and the sim thread before its first loop iteration take the
// original opcode's live handler from JitEnter: no compiled code ever runs there (docs/207 §14).
//
// ForceJIT (MonoPatcher, 0xE5637D jne -> 90 90) re-transforms a method on every call into new mint code: the post-pass
// refuses while the byte is not 0x75, and old patched words are simply not reached; only a lazy call site tests the byte
// per call, since it enters the callee without EA's transform check (compiled code is otherwise only entered through words
// in the code EA dispatches).
//
// Verify (Mode=2, docs/527 §3): one fresh call in VerifyEvery per method runs compiled, the reachable graph is restored,
// EA's interpreter runs the same call and every word must match; what stays is the interpreter's result. The first
// mismatch of a method unpatches it for the session. Not covered: a call that ends by exception (abandoned), a graph over
// the snapshot size (compiled only).

#include "ts3addr.h"  // TS3A(x): TS3W.exe addresses by their Steam 1.67 value
#include <tlhelp32.h>  // the reader trap: the other threads

static const DWORD JIT_OP = 0x1A9, JIT_EA_UNIMPLEMENTED = TS3A(0x00E596C2);
static const DWORD JIT_PE_STAMP = 0x52DEC247, JIT_PE_SIZE = TS3A(0x00E5A000);  // the exe gate: Steam 1.67 (docs/527 §4.1)
// game addresses the selftest points elsewhere
static DWORD* g_jitTableP = (DWORD*)(uintptr_t)TS3A(0x00E59880);                  // the mint jump table
static const BYTE* g_jitOpLen = (const BYTE*)(uintptr_t)TS3A(0x01195088);        // instruction length in words, per opcode
static const BYTE* g_jitSuper = (const BYTE*)(uintptr_t)TS3A(0x0108FCD8);        // superinstruction descriptors (0x48 bytes each)
static const char* const* g_jitOpNames = (const char* const*)(uintptr_t)TS3A(0x011949E0);
static const BYTE* g_jitSse2 = (const BYTE*)(uintptr_t)TS3A(0x01234374);         // EA's _sse2_available: conv.i4.r8 and friends pick their path by it (docs/221)
static bool g_jitSse3, g_jitWide;                                                // [MonoJit] Wide=1 (docs/537 §10)
// docs/537 §20 B1, off by default: [MonoJit] VtOps=1 (host MONOHOST_JITVTOPS): stfld.vt, ldelem.any.vt, stelem.any.vt, ldobj (value
// type) and stobj.vt compile natively (template group 1024)
static bool g_jitVtOps;
// docs/537 §20 A, off by default: [MonoJit] CallExit=1 (host MONOHOST_JITCALLEXIT): a callee with hot exits still gets a lazy body
// (its exits write the frame first, as the cold ones do), the pure Array icalls GetLength / GetLowerBound / GetRank run inline
// (only when EA's bytes match at install), and a guarded callvirt takes a boxed value-type receiver (this = box + 4, as EA's 0xE54B3E)
static bool g_jitCallExit, g_jitPureIcalls;
// docs/537 §20 B2b, off by default: [MonoJit] NewVt=1 (host MONOHOST_JITNEWVT): newobj of a value-type class calls the ctor's lazy
// body natively (this = the vt cursor, EA's 0xE5620C), see EmitLazyCall
static bool g_jitNewVt;
// docs/537 §20 C3, off by default: [MonoJit] FastCopy=1 (host MONOHOST_JITFASTCOPY): Array.FastCopy runs inline at its call site for
// same-element value-type arrays without references (EA's memmove path); every other input exits to EA's call (JitFastCopy)
static bool g_jitFastCopy;
// docs/537 §20 C3, off by default: [MonoJit] Ldsflda=1 (host MONOHOST_JITLDSFLDA): ldsflda compiles to its template (class-init exit,
// then [vt+0xC] + offset; EA's 0xE56D20). Without it the row's width 0 sends every ldsflda to an exit (as before)
static bool g_jitLdsflda;
static const DWORD* g_jitArrCtorP = (const DWORD*)(uintptr_t)TS3A(0x01220E8C);  // newobj's array-ctor class test (0xE5617D); selftest redirects
static const BYTE* g_jitForceJit = (const BYTE*)(uintptr_t)TS3A(0x00E5637D);     // MonoPatcher's jne; 0x75 = stock
static const void* const* g_jitProxyClassP = (const void* const*)(uintptr_t)TS3A(0x01220EC0);  // mono_defaults transparent_proxy_class (docs/130 §10.2: stays NULL here)
static DWORD g_jitMemcpy = TS3A(0x00F7E8A6), g_jitFmod = TS3A(0x00F7EFB0);        // EA's CRT memcpy thunk, _CIfmod
static DWORD g_jitMemset = TS3A(0x00F7E8B8), g_jitAullshr = TS3A(0x00F7EEE0), g_jitIfaceOff = TS3A(0x00D70870);  // memset thunk, _aullshr, Class_GetInterfaceOffset (selftest redirects)
static const DWORD* g_jitMemmove = (const DWORD*)(uintptr_t)TS3A(0x00F95684);    // the memmove import
static void** g_jitObjectClassP = (void**)(uintptr_t)(TS3A(0x01220E40) + 4);     // mono_defaults.object_class
typedef void(__cdecl* JitAddRootsFn)(void* lo, void* hi);
static JitAddRootsFn g_jitAddRoots = (JitAddRootsFn)(uintptr_t)TS3A(0x00E4D9F0);  // GC_add_roots
struct JitRow { const char* key; DWORD size; BYTE sha[32]; };
#include "monojit-list.h"  // kJitRows, kJitRowN: the built-in list, name + size + SHA-256 of each known IL body (tools/jit-hot.ts --emit-native)
static const JitRow* g_jitRows = kJitRows;
static int g_jitRowN = kJitRowN;
static char g_jitUserBuf[512];       // [MonoJit] List=: narrows the built-in list (a name not in it stays refused: no known hash)
static const char* g_jitUser[32];
static int g_jitUserN;
static int g_jitGroups = 511;        // [MonoJit] Groups: template mask, as MONOHOST_JITC2
static int g_jitMode;                // 0 off, 1 compiled, 2 verify
static DWORD g_jitEvery = 16;        // [MonoJit] VerifyEvery
static volatile DWORD g_jitSimTid;   // set by the first JitTick; 0 = nothing compiled runs yet
static bool g_jitDead;               // a fault inside the compiler: no more compiles this session
static LONG g_jitLogN;
static CRITICAL_SECTION g_jitCs;
static void* g_jitDomain;            // the transform context's domain, per compile (rm is per domain)
static void* g_jitObjectClass;       // an object[] takes any reference without the class check
static bool g_jitLazy, g_jitLazyPass;  // [MonoJit] Direct=1 (docs/207 §12); the lazy body is being emitted
// [MonoJit] Capture=1 (monojit-capture.h): on before any compile, so the compiler emits the lazy-body and inline-site sample calls only then
static volatile int g_jcOn;
static void JcLazyStub();
static void JcInlineStub();
static void* g_jcInlineStubP = (void*)JcInlineStub;  // an inlined site calls through this cell: its code is built in a scratch buffer
static bool g_jitLeafOnly = true;    // [MonoJit] DirectLeaf: docs/223 §8 "narrow scope first": no chains, no guarded callvirt
static bool g_jitTrap;               // [MonoJit] Trap=1 (docs/537 §3): call sites open the reader-trap window

static bool JitEndsWith(const char* full, const char* key) {  // "ns.Class.Method" ends in "Class.Method" at a dot
    const size_t fl = strlen(full), l = strlen(key);
    return fl >= l && !strcmp(full + fl - l, key) && (fl == l || full[fl - l - 1] == '.');
}

static const char* JitMethodName(const u8* rm, char* buf, int n) {  // "ns.Class.Method" (nested types: "Class.Method")
    const u8* mm = *(const u8* const*)(rm + 8);
    const u8* k = mm ? *(const u8* const*)(mm + 8) : NULL;
    const char* ns = k ? *(const char* const*)(k + 0x38) : NULL;
    const char* cn = k ? *(const char* const*)(k + 0x34) : NULL;
    const char* mn = mm ? *(const char* const*)(mm + 0x18) : NULL;
    sprintf_s(buf, n, "%s%s%s.%s", ns && *ns ? ns : "", ns && *ns ? "." : "", cn ? cn : "?", mn ? mn : "?");
    return buf;
}
static bool JitListed(const char* full) {
    bool hit = false;
    for (int i = 0; i < g_jitRowN && !hit; i++) hit = JitEndsWith(full, g_jitRows[i].key);
    if (!hit || !g_jitUserN) return hit;
    for (int i = 0; i < g_jitUserN; i++) if (JitEndsWith(full, g_jitUser[i])) return true;
    return false;
}

// instruction length in words at p (superops: the summed base lengths); 0 = cannot walk (switch handled by the caller)
static int JitLen(const unsigned short* p) {
    unsigned op = *p;
    if (op == JIT_OP) return -1;
    if (op <= 0x1A8) return g_jitOpLen[op];
    if (op >= 0x1AB && op <= 0x1C0) {
        const u8* e = g_jitSuper + 0x48 * (op - 0x1AA);
        int m = *(const unsigned short*)(e + 4), len = 0;
        for (int k = 0; k < m; k++) len += g_jitOpLen[((const unsigned short*)(e + 6))[k]];
        return len;
    }
    return -1;
}
static int JitSwitchLen(const unsigned short* p) { return 3 + 2 * (int)*(const DWORD*)(p + 1); }  // op, u32 n, n x i32 offsets

// width code of a MonoType (type byte at +6) as EA's stackval_to_data / stackval_from_data move it; 0 = not compiled (value
// types, generic instances, anything else: the opcode compiles as an exit)
static int JitTypeWidth(const u8* type) {
    switch (type[6]) {
    case 0x04: return 1;                        // I1
    case 0x02: case 0x05: return 2;             // BOOLEAN, U1
    case 0x06: return 3;                        // I2
    case 0x03: case 0x07: return 4;             // CHAR, U2
    case 0x08: case 0x09: case 0x0E: case 0x0F: case 0x12: case 0x14: case 0x18: case 0x19: case 0x1B: case 0x1C: case 0x1D:
        return 5;                               // I4, U4, STRING, PTR, CLASS, ARRAY, I, U, FNPTR, OBJECT, SZARRAY
    case 0x0A: case 0x0B: case 0x0D: return 6;  // I8, U8, R8
    case 0x0C: return 7;                        // R4 (x87 widen on a load, round on a store, as EA's handlers do)
    default: return 0;
    }
}
// Thread / context statics live in the domain's special-static table (domain+0x88, g_hash_table_lookup 0xE45F00) and EA's
// stsfld / ldsfld / ldsflda take the per-thread address for them; such a field compiles as an exit.
static bool JitSpecialStatic(const u8* field) {
    if (!g_jitDomain) return true;
    void* hash = *(void**)((u8*)g_jitDomain + 0x88);
    return hash && ((void*(__cdecl*)(void*, const void*))(uintptr_t)TS3A(0x00E45F00))(hash, field) != NULL;
}
// mono_class_vtable(domain, field->parent) as EA's stsfld and ldsflda call it every execution (0xEA2AD0); one vtable per
// domain and class, so the compiled code carries it as a constant. field+0x14 = parent, field+8 = offset [verified: the
// handlers at 0xE56F52 / 0xE56D20 / 0xE56E1E].
static void* JitStaticVt(const u8* field) {
    if (!g_jitDomain) return NULL;
    return ((void*(__cdecl*)(void*, const void*))(uintptr_t)TS3A(0x00EA2AD0))(g_jitDomain, *(const void* const*)(field + 0x14));
}

// ---- opcode templates ----
enum JitKind : u8 {
    K_EXIT, K_RET, K_LDARG, K_LDTHIS, K_LDARGA, K_LDLOC, K_LDLOCA, K_STLOC, K_STLOCNP, K_STINARG, K_LDFLD, K_STFLD, K_LDC,
    K_LDCS, K_LDCI4, K_DUP, K_POP, K_BRS, K_BR, K_BTS, K_BT, K_BFS, K_BF, K_BCS, K_BC, K_LDLEN, K_LDELEM, K_LDIND, K_STIND,
    K_BIN, K_BIN8, K_MUL, K_INC, K_DEC, K_SHIFT, K_UN, K_CMP, K_CEQ0, K_CONV,
    K_LDSFLD, K_LDSFLDA, K_STSFLD, K_STELEM, K_STELEMREF, K_STOBJ, K_LDFLDA, K_CKNULL, K_STRLEN, K_GETCHR,  // round 10 C2
    K_DIVUN, K_REMR8, K_SWITCH,  // round 10 C2, the throw-only handlers, rem.r8 and switch
    K_LDVT, K_STVT, K_STINARGVT, K_DUPVT, K_VTRESULT, K_CEQ8, K_LDFLDVT, K_LDSFLDVT,  // round 10 C1, the value-type moves and ceq.i8 (docs/207 §15)
    K_INITOBJ, K_ISINST, K_SHRUN8, K_LDFP, K_STFP, K_STINARGFP, K_LDCFP,  // round 10 C5 (docs/207 §16.3)
    K_STFLDVT, K_LDELEMVT, K_STELEMVT, K_LDOBJVT, K_STOBJVT,  // docs/537 §20 B1, group 1024 ([MonoJit] VtOps=1)
    K_FCMP, K_FBRS, K_FBR,  // docs/221, the float compares
    K_FARITH, K_FNEG, K_FILD, K_FR4R8, K_CVT, K_FISTT, K_LDINDFP, K_STINDFP, K_LDSTR, K_LDTOKEN, K_CASTCLASS  // S5, group 512: stay last
};
// width: 1 = i1, 2 = u1, 3 = i2, 4 = u2, 5 = 32-bit, 6 = 64-bit, 7 = r4 (x87), 8 = typed by the field or class at compile
// time (JitTypeWidth); x = kind-specific byte (cc, alu op, imm)
struct JitOpInfo { u8 kind, width; int x; };
static JitOpInfo g_opInfo[0x1C1];

// compiled stores log through Satb while a span is on (docs/507 B3); JitEnter keeps the worker out of compiled code
static void __cdecl JitSatbStore(DWORD addr, DWORD n) {
    if (SatbSimSide()) SatbSimRange(addr, n);
}

static constexpr int FCC(int second, int first, int x) { return second << 16 | first << 8 | x; }  // EmitFcmp's x: two setcc and the flags byte
static const struct { const char* name; u8 kind, width; int x; } kJitTmpl[] = {
    {"ret", K_RET, 0, 0}, {"ret.void", K_RET, 0, 0}, {"ret.vt", K_RET, 0, 0},
    {"ldarg.i1", K_LDARG, 1, 0}, {"ldarg.u1", K_LDARG, 2, 0}, {"ldarg.i2", K_LDARG, 3, 0}, {"ldarg.u2", K_LDARG, 4, 0},
    {"ldarg.i4", K_LDARG, 5, 0}, {"ldarg.o", K_LDARG, 5, 0}, {"ldarg.p", K_LDARG, 5, 0}, {"ldarg.i8", K_LDARG, 6, 0},
    {"ldthis", K_LDTHIS, 0, 0}, {"ldarga", K_LDARGA, 0, 0},
    {"ldloc.i1", K_LDLOC, 1, 0}, {"ldloc.u1", K_LDLOC, 2, 0}, {"ldloc.i2", K_LDLOC, 3, 0}, {"ldloc.u2", K_LDLOC, 4, 0},
    {"ldloc.i4", K_LDLOC, 5, 0}, {"ldloc.o", K_LDLOC, 5, 0}, {"ldloc.p", K_LDLOC, 5, 0}, {"ldloc.i8", K_LDLOC, 6, 0},
    {"ldloca.s", K_LDLOCA, 0, 0},
    {"stloc.i1", K_STLOC, 1, 0}, {"stloc.u1", K_STLOC, 2, 0}, {"stloc.i2", K_STLOC, 3, 0}, {"stloc.u2", K_STLOC, 4, 0},
    {"stloc.i4", K_STLOC, 5, 0}, {"stloc.o", K_STLOC, 5, 0}, {"stloc.p", K_STLOC, 5, 0}, {"stloc.i8", K_STLOC, 6, 0},
    {"stloc.np.i4", K_STLOCNP, 5, 0}, {"stloc.np.o", K_STLOCNP, 5, 0},
    {"stinarg.i1", K_STINARG, 1, 0}, {"stinarg.u1", K_STINARG, 2, 0}, {"stinarg.i2", K_STINARG, 3, 0}, {"stinarg.u2", K_STINARG, 4, 0},
    {"stinarg.i4", K_STINARG, 5, 0}, {"stinarg.o", K_STINARG, 5, 0}, {"stinarg.p", K_STINARG, 5, 0}, {"stinarg.i8", K_STINARG, 6, 0},
    // round 10 C1: the value-type moves; x = 0 an argument (ebx), 1 a local (edi)
    {"ldarg.vt", K_LDVT, 0, 0}, {"ldloc.vt", K_LDVT, 0, 1}, {"starg.vt", K_STVT, 0, 0}, {"stloc.vt", K_STVT, 0, 1},
    {"stinarg.vt", K_STINARGVT, 0, 0}, {"dup.vt", K_DUPVT, 0, 0}, {"vtresult", K_VTRESULT, 0, 0}, {"ldfld.vt", K_LDFLDVT, 0, 0}, {"ldsfld.vt", K_LDSFLDVT, 9, 0},
    {"ldfld.i1", K_LDFLD, 1, 0}, {"ldfld.u1", K_LDFLD, 2, 0}, {"ldfld.i2", K_LDFLD, 3, 0}, {"ldfld.u2", K_LDFLD, 4, 0},
    {"ldfld.i4", K_LDFLD, 5, 0}, {"ldfld.o", K_LDFLD, 5, 0}, {"ldfld.p", K_LDFLD, 5, 0}, {"ldfld.i8", K_LDFLD, 6, 0},
    {"stfld.i1", K_STFLD, 1, 0}, {"stfld.u1", K_STFLD, 2, 0}, {"stfld.i2", K_STFLD, 3, 0}, {"stfld.u2", K_STFLD, 4, 0},
    {"stfld.i4", K_STFLD, 5, 0}, {"stfld.o", K_STFLD, 5, 0}, {"stfld.p", K_STFLD, 5, 0}, {"stfld.i8", K_STFLD, 6, 0},
    {"ldnull", K_LDC, 0, 0}, {"ldc.i4.m1", K_LDC, 0, -1}, {"ldc.i4.0", K_LDC, 0, 0}, {"ldc.i4.1", K_LDC, 0, 1},
    {"ldc.i4.2", K_LDC, 0, 2}, {"ldc.i4.3", K_LDC, 0, 3}, {"ldc.i4.4", K_LDC, 0, 4}, {"ldc.i4.5", K_LDC, 0, 5},
    {"ldc.i4.6", K_LDC, 0, 6}, {"ldc.i4.7", K_LDC, 0, 7}, {"ldc.i4.8", K_LDC, 0, 8}, {"ldc.i4.s", K_LDCS, 0, 0}, {"ldc.i4", K_LDCI4, 0, 0},
    {"dup", K_DUP, 0, 0}, {"pop", K_POP, 0, 0}, {"br.s", K_BRS, 0, 0}, {"br", K_BR, 0, 0},
    {"brtrue.i4.s", K_BTS, 0, 0}, {"brtrue.i4", K_BT, 0, 0}, {"brfalse.i4.s", K_BFS, 0, 0}, {"brfalse.i4", K_BF, 0, 0},
    // jcc second byte (0F 8x): je 84, jne 85, jl 8C, jle 8E, jg 8F, jge 8D, jb 82, jbe 86, ja 87, jae 83
    {"beq.i4.s", K_BCS, 0, 0x84}, {"bne.un.i4.s", K_BCS, 0, 0x85}, {"blt.i4.s", K_BCS, 0, 0x8C}, {"ble.i4.s", K_BCS, 0, 0x8E},
    {"bgt.i4.s", K_BCS, 0, 0x8F}, {"bge.i4.s", K_BCS, 0, 0x8D}, {"blt.un.i4.s", K_BCS, 0, 0x82}, {"ble.un.i4.s", K_BCS, 0, 0x86},
    {"bgt.un.i4.s", K_BCS, 0, 0x87}, {"bge.un.i4.s", K_BCS, 0, 0x83},
    {"beq.i4", K_BC, 0, 0x84}, {"bne.un.i4", K_BC, 0, 0x85}, {"blt.i4", K_BC, 0, 0x8C}, {"ble.i4", K_BC, 0, 0x8E},
    {"bgt.i4", K_BC, 0, 0x8F}, {"bge.i4", K_BC, 0, 0x8D}, {"blt.un.i4", K_BC, 0, 0x82}, {"ble.un.i4", K_BC, 0, 0x86},
    {"bgt.un.i4", K_BC, 0, 0x87}, {"bge.un.i4", K_BC, 0, 0x83},
    {"ldlen", K_LDLEN, 0, 0},
    {"ldelem.i1", K_LDELEM, 1, 0}, {"ldelem.u1", K_LDELEM, 2, 0}, {"ldelem.i2", K_LDELEM, 3, 0}, {"ldelem.u2", K_LDELEM, 4, 0},
    {"ldelem.i4", K_LDELEM, 5, 0}, {"ldelem.u4", K_LDELEM, 5, 0}, {"ldelem.i", K_LDELEM, 5, 0}, {"ldelem.ref", K_LDELEM, 5, 0},
    {"ldelem.i8", K_LDELEM, 6, 0},
    {"ldind.i1", K_LDIND, 1, 0}, {"ldind.u1", K_LDIND, 2, 0}, {"ldind.i2", K_LDIND, 3, 0}, {"ldind.u2", K_LDIND, 4, 0},
    {"ldind.i4", K_LDIND, 5, 0}, {"ldind.u4", K_LDIND, 5, 0}, {"ldind.i", K_LDIND, 5, 0}, {"ldind.ref", K_LDIND, 5, 0},
    {"ldind.i8", K_LDIND, 6, 0},
    {"stind.i1", K_STIND, 1, 0}, {"stind.i2", K_STIND, 3, 0}, {"stind.i4", K_STIND, 5, 0}, {"stind.i", K_STIND, 5, 0},
    {"stind.ref", K_STIND, 5, 0}, {"stind.i8", K_STIND, 6, 0},
    // round 10 C2 (docs/207 §13): float fields, field addresses, static fields, stobj, the array and string stores
    {"ldfld.r4", K_LDFLD, 7, 0}, {"ldfld.r8", K_LDFLD, 6, 0}, {"stfld.r4", K_STFLD, 7, 0}, {"stfld.r8", K_STFLD, 6, 0}, {"ldflda", K_LDFLDA, 0, 0},
    {"ldsfld", K_LDSFLD, 8, 0}, {"ldsfld.i4", K_LDSFLD, 5, 0}, {"ldsfld.o", K_LDSFLD, 5, 0}, {"ldsflda", K_LDSFLDA, 0, 0}, {"stsfld", K_STSFLD, 8, 0},
    {"ldelem.r4", K_LDELEM, 7, 0}, {"ldelem.r8", K_LDELEM, 6, 0},
    {"stelem.i", K_STELEM, 5, 0}, {"stelem.i1", K_STELEM, 1, 0}, {"stelem.i2", K_STELEM, 3, 0}, {"stelem.i4", K_STELEM, 5, 0},
    {"stelem.i8", K_STELEM, 6, 0}, {"stelem.r4", K_STELEM, 7, 0}, {"stelem.r8", K_STELEM, 6, 0}, {"stelem.ref", K_STELEMREF, 5, 0},
    {"stobj", K_STOBJ, 8, 0}, {"cknull", K_CKNULL, 0, 0}, {"strlen", K_STRLEN, 0, 0}, {"getchr", K_GETCHR, 0, 0},
    {"div.un.i4", K_DIVUN, 0, 0}, {"rem.un.i4", K_DIVUN, 0, 1}, {"rem.r8", K_REMR8, 0, 0}, {"switch", K_SWITCH, 0, 0},
    // docs/537 §20 B1: the value-type field, element and object ops (EA's 0xE56C0A, 0xE57382, 0xE575CF, 0xE560CD, 0xE57089)
    {"stfld.vt", K_STFLDVT, 0, 0}, {"ldelem.any.vt", K_LDELEMVT, 0, 0}, {"stelem.any.vt", K_STELEMVT, 0, 0}, {"ldobj", K_LDOBJVT, 0, 0}, {"stobj.vt", K_STOBJVT, 0, 0},
    {"initobj", K_INITOBJ, 0, 0}, {"isinst", K_ISINST, 0, 0}, {"shr.un.i8", K_SHRUN8, 0, 0},  // round 10 C5
    {"add.r8", K_FARITH, 0, 0}, {"sub.r8", K_FARITH, 0, 4}, {"mul.r8", K_FARITH, 0, 1}, {"div.r8", K_FARITH, 0, 6}, {"neg.r8", K_FNEG, 0, 0},  // S5, docs/537 §10
    {"conv.r8.i4", K_FILD, 0, 0}, {"conv.r4.i4", K_FILD, 0, 0}, {"conv.r8.i8", K_FILD, 0, 1}, {"conv.r4.i8", K_FILD, 0, 1}, {"conv.r4.r8", K_FR4R8, 0, 0},
    {"conv.i4.r8", K_CVT, 0, 0}, {"conv.i1.r8", K_CVT, 0, 1}, {"conv.i2.r8", K_CVT, 0, 2}, {"conv.u1.r8", K_CVT, 0, 3}, {"conv.u2.r8", K_CVT, 0, 4},
    {"conv.u4.r8", K_FISTT, 0, 0}, {"conv.i8.r8", K_FISTT, 0, 1}, {"conv.u8.r8", K_FISTT, 0, 1},
    {"ldind.r4", K_LDINDFP, 7, 0}, {"ldind.r8", K_LDINDFP, 6, 0}, {"stind.r4", K_STINDFP, 7, 0}, {"stind.r8", K_STINDFP, 6, 0},
    {"ldstr", K_LDSTR, 0, 0}, {"ldtoken", K_LDTOKEN, 0, 0}, {"castclass", K_CASTCLASS, 0, 0},
    // the float moves, through x87 as EA's handlers (a double reaches an r4 slot by way of [ebp-0x1C], scratch in every handler, docs/139 §5); x = 0 an argument, 1 a local
    {"ldc.r4", K_LDCFP, 7, 0}, {"ldc.r8", K_LDCFP, 6, 0}, {"ldarg.r4", K_LDFP, 7, 0}, {"ldarg.r8", K_LDFP, 6, 0}, {"ldloc.r4", K_LDFP, 7, 1}, {"ldloc.r8", K_LDFP, 6, 1},
    {"stloc.r4", K_STFP, 7, 1}, {"stloc.r8", K_STFP, 6, 1}, {"stinarg.r4", K_STINARGFP, 7, 0}, {"stinarg.r8", K_STINARGFP, 6, 0},
    // docs/221 §3: the float compares and branches without EA's _isnan calls, 25 forms; x = bit 0 a in st(0), bit 1 or (else and) the
    // second setcc, byte 1 first setcc, byte 2 second setcc (0 none). a = [esi-16], b = [esi-8]; emulated against EA's handlers, 49,152 cases
    {"ceq.r8", K_FCMP, 0, FCC(0x9B, 0x94, 1)}, {"cgt.r8", K_FCMP, 0, 0x9701}, {"cgt.un.r8", K_FCMP, 0, 0x9200}, {"clt.r8", K_FCMP, 0, 0x9700},
    {"clt.un.r8", K_FCMP, 0, 0x9201}, {"beq.r8", K_FBR, 0, FCC(0x9B, 0x94, 1)}, {"beq.r8.s", K_FBRS, 0, FCC(0x9B, 0x94, 1)}, {"bge.r8", K_FBR, 0, 0x9301},
    {"bge.r8.s", K_FBRS, 0, 0x9301}, {"bgt.r8", K_FBR, 0, 0x9701}, {"bgt.r8.s", K_FBRS, 0, 0x9701}, {"blt.r8", K_FBR, 0, 0x9700},
    {"blt.r8.s", K_FBRS, 0, 0x9700}, {"ble.r8", K_FBR, 0, 0x9300}, {"ble.r8.s", K_FBRS, 0, 0x9300}, {"bne.un.r8", K_FBR, 0, FCC(0x9A, 0x95, 3)},
    {"bne.un.r8.s", K_FBRS, 0, FCC(0x9A, 0x95, 3)}, {"bge.un.r8", K_FBR, 0, 0x9600}, {"bge.un.r8.s", K_FBRS, 0, 0x9600}, {"bgt.un.r8", K_FBR, 0, 0x9200},
    {"bgt.un.r8.s", K_FBRS, 0, 0x9200}, {"ble.un.r8", K_FBR, 0, 0x9601}, {"ble.un.r8.s", K_FBRS, 0, 0x9601}, {"blt.un.r8", K_FBR, 0, 0x9201},
    {"blt.un.r8.s", K_FBRS, 0, 0x9201},
    // ALU r/m32, r32 opcodes: add 01, or 09, and 21, sub 29, xor 31 (i8: low op, high op adc 11 / sbb 19)
    {"add.i4", K_BIN, 0, 0x01}, {"sub.i4", K_BIN, 0, 0x29}, {"and.i4", K_BIN, 0, 0x21}, {"or.i4", K_BIN, 0, 0x09}, {"xor.i4", K_BIN, 0, 0x31},
    {"add.i8", K_BIN8, 0, 0x1101}, {"sub.i8", K_BIN8, 0, 0x1929}, {"and.i8", K_BIN8, 0, 0x2121}, {"or.i8", K_BIN8, 0, 0x0909},
    {"xor.i8", K_BIN8, 0, 0x3131}, {"mul.i4", K_MUL, 0, 0}, {"add1.i4", K_INC, 0, 0}, {"sub1.i4", K_DEC, 0, 0},
    {"shl.i4", K_SHIFT, 0, 4}, {"shr.i4", K_SHIFT, 0, 7}, {"shr.un.i4", K_SHIFT, 0, 5}, {"neg.i4", K_UN, 0, 3}, {"not.i4", K_UN, 0, 2},
    // setcc second byte (0F 9x): sete 94, setg 9F, setl 9C, seta 97, setb 92
    {"ceq.i4", K_CMP, 0, 0x94}, {"ceq.i8", K_CEQ8, 0, 0}, {"cgt.i4", K_CMP, 0, 0x9F}, {"clt.i4", K_CMP, 0, 0x9C}, {"cgt.un.i4", K_CMP, 0, 0x97},
    {"clt.un.i4", K_CMP, 0, 0x92}, {"ceq0.i4", K_CEQ0, 0, 0},
    {"conv.i8.i4", K_CONV, 0, 1}, {"conv.u8.i4", K_CONV, 0, 2}, {"conv.i4.i8", K_CONV, 0, 3}, {"conv.i1.i4", K_CONV, 0, 4},
    {"conv.u1.i4", K_CONV, 0, 5}, {"conv.i2.i4", K_CONV, 0, 6}, {"conv.u2.i4", K_CONV, 0, 7},
};

// ---- emitter: every memory operand is [base + disp32] (or [eax + ecx*scale + disp32]); registers by x86 number ----
enum { EAX, ECX, EDX, EBX, ESP_, EBP, ESI, EDI };
struct Em {
    u8* p;
    void b(int v) { *p++ = (u8)v; }
    void d(DWORD v) { memcpy(p, &v, 4); p += 4; }
    void m(int reg, int base, int disp) { b(0x80 | (reg << 3) | base); d((DWORD)disp); }  // mod=10
    void op(int o, int reg, int base, int disp) { b(o); m(reg, base, disp); }
    void op2(int o, int reg, int base, int disp) { b(0x0F); b(o); m(reg, base, disp); }
    void opEsp(int o, int reg, int disp) { b(o); b(0x84 | (reg << 3)); b(0x24); d((DWORD)disp); }  // o reg, [esp+disp32]
    void rr(int o, int dst, int src) { b(o); b(0xC0 | (src << 3) | dst); }  // o r/m, r with mod=11 (r/m = dst)
    void movRI(int r, DWORD v) { b(0xB8 + r); d(v); }
    void addEsi(int v) { b(0x83); b(v >= 0 ? 0xC6 : 0xEE); b(v >= 0 ? v : -v); }
    // load a value of width w from [base+disp] into r (zero or sign extended to 32 bits)
    void load(int w, int r, int base, int disp) {
        if (w == 1) op2(0xBE, r, base, disp); else if (w == 2) op2(0xB6, r, base, disp);
        else if (w == 3) op2(0xBF, r, base, disp); else if (w == 4) op2(0xB7, r, base, disp); else op(0x8B, r, base, disp);
    }
    void store(int w, int r, int base, int disp) {  // store the low 8/16/32 bits of r (r = eax/ecx/edx/ebx for 8-bit)
        if (w <= 2) op(0x88, r, base, disp); else if (w <= 4) { b(0x66); op(0x89, r, base, disp); } else op(0x89, r, base, disp);
    }
    // x87 for the r4 forms: EA widens a float with fld dword / fstp qword and rounds a double to a float with fld qword / fstp dword
    void fld4(int base, int disp) { op(0xD9, 0, base, disp); }
    void fld8(int base, int disp) { op(0xDD, 0, base, disp); }
    void fstp4(int base, int disp) { op(0xD9, 3, base, disp); }
    void fstp8(int base, int disp) { op(0xDD, 3, base, disp); }
    // [vt+0x18] bit 1: the class is initialised; clear = EA's handler runs the class constructor (the exit re-runs the instruction)
    u8* classInitExit(DWORD vt) { b(0xF6); b(0x05); d(vt + 0x18); b(2); return jcc(0x84); }
    u8* jcc(int cc) { b(0x0F); b(cc); d(0); return p - 4; }
    u8* jmp() { b(0xE9); d(0); return p - 4; }
    void jmpTo(const void* t) { b(0xE9); d((DWORD)(uintptr_t)t - ((DWORD)(uintptr_t)p + 4)); }
    void incAbs(volatile LONG* c) { b(0xFF); b(0x05); d((DWORD)(uintptr_t)c); }
    // Satb barrier before a heap store of width w at [base+disp] (docs/507 B3): outside a span one cmp and a taken jump;
    // inside, JitSatbStore(address, bytes). base survives (push/pop); eax, ecx, edx are scratch at every emitting site.
    void satb(int w, int base, int disp, DWORD bytes = 0) {  // bytes: a store of that size, w ignored
        b(0x83); b(0x3D); d((DWORD)(uintptr_t)&g_satbOn); b(0);  // cmp dword ptr [g_satbOn], 0
        b(0x74); u8* skip = p; b(0);                                 // je skip
        b(0x50 + base); op(0x8D, EDX, base, disp);                   // push base; lea edx, [base+disp]
        b(0x68); d(bytes ? bytes : w <= 2 ? 1 : w <= 4 ? 2 : w == 5 || w == 7 ? 4 : 8); b(0x52);  // push bytes; push edx
        b(0xE8); d((DWORD)(uintptr_t)JitSatbStore - ((DWORD)(uintptr_t)p + 4));  // call JitSatbStore
        b(0x83); b(0xC4); b(8); b(0x58 + base);                      // add esp, 8; pop base
        *skip = (u8)(p - (skip + 1));
    }
};
static void Fix(u8* rel, const u8* target) { *(int*)rel = (int)(target - (rel + 4)); }

// ---- the region and its tables (one allocation at install, RW; code pages flip to RX per method) ----
struct JitMeth;
struct JitEnt { DWORD ip; u8* native; DWORD orig; JitMeth* m; };
static_assert(sizeof(JitEnt) == 16 && offsetof(JitEnt, native) == 4 && offsetof(JitEnt, orig) == 8, "JitEnter reads these offsets");
static const DWORD JIT_TAB_N = 1 << 15;  // JitEnter's mask
static const int JIT_METH_N = 2048, JIT_POS_N = 24576, JIT_MAX_POS = 4000, JIT_MAX_WORDS = 16384, JIT_CODE_BYTES = 2 << 20;
struct JitMeth { u8* rm; unsigned short* code; int words; int pos0, posN; char name[96]; int nativeOps, exitOps, bytes; u8* native0; bool verifyOk, dead; DWORD tick; };
struct JitFix { u8* rel; int targetW; bool abs; };  // abs: a switch table slot takes the label itself
// posReal: an EA instruction start (dispatch can land there), not a superop's inner position
struct JitScratch { int posW[JIT_MAX_POS + 64]; unsigned short posOp[JIT_MAX_POS + 64]; bool posReal[JIT_MAX_POS + 64], posCold[JIT_MAX_POS + 64]; u8* label[JIT_MAX_POS + 64]; JitFix fix[JIT_MAX_POS + 64]; int wordToPos[JIT_MAX_WORDS]; };
static JitEnt* g_jitTab;                 // JIT_TAB_N entries + a 4096-entry overflow tail (no wrap in the probe loop)
static JitMeth* g_jitMeth;
static int g_jitMethN;
static JitScratch* g_js;                 // the compiler's work area (compiles run under g_jitCs)
static unsigned short* g_jitPos;         // positions of all compiled methods (word offsets), [pos0, posN) per method
static unsigned short* g_jitOrigW;
static int g_jitPosN;
static u8* g_jitCode;
static u8* g_jitCodeEnd;
static size_t g_jitCodeUsed;
static bool g_jitPatched;
static volatile int g_jitVerifyOn;       // JitEnter's branch to the verify path

static JitEnt* JitFind(DWORD ip) {
    for (DWORD i = (ip >> 1) & (JIT_TAB_N - 1);; i++) {
        if (g_jitTab[i].ip == ip) return &g_jitTab[i];
        if (!g_jitTab[i].ip) return NULL;
    }
}
static JitEnt* JitAdd(DWORD ip) {
    DWORD i = (ip >> 1) & (JIT_TAB_N - 1);
    while (g_jitTab[i].ip && g_jitTab[i].ip != ip) i++;
    if (i >= JIT_TAB_N + 4000) return NULL;
    g_jitTab[i].ip = ip;
    return &g_jitTab[i];
}
static void JitProt(u8* p, size_t n, DWORD prot) {
    DWORD old;
    VirtualProtect(p, n, prot, &old);
}
static void JitVerifyExitStub();

// ---- lazy frames (docs/207 §12.3, §12.7; the host's v2): a compiled call site whose callee is compiled calls the callee's
// lazy body natively, with no frame record, MonoInvocation, depth or ThreadContext write. The callee's frame is written
// (JitLazyMat, the same words EA's push writes) only at the callee's first exit to an EA handler; the native call is then
// abandoned and the caller resumes through EA's return, as after any interpreted call. Block the call site pushes (the lazy
// body sees it at [esp+4]; it pushes nothing itself, so in a chain the outer block sits right above the return address
// into the outer lazy body). B_VT: the lazy body's vt cursor, [ebp-0xC] of a written frame.
enum { B_ARGS = 0, B_THIS = 4, B_RIP = 8, B_RSP = 12, B_RSLOT = 16, B_RM = 20, B_EBX = 24, B_EDI = 28, B_CIP = 32, B_LVL = 36, B_VT = 40, B_SIZE = 44 };
static void JitLazyMat();
struct JitLazyCell { const u8* rm; u8* entry; };  // callee -> its lazy body (NULL until compiled); call sites read entry
// a callvirt site of a virtual method: compares the receiver's class with EA's inline cache and takes the cache's target;
// JitGuardFill looks the target's lazy body and frame sizes up once per target change. A target not transformed yet leaves
// rm NULL (asked again at the next call), one with no lazy body leaves entry NULL (bails until the cache changes or the
// target compiles: the publish step refills). unbox (CallExit): 4 when the cache's class is a value type and the target takes a
// value-type this, as EA's callvirt passes box + 4 then (0xE54B2C-0xE54B41 [verified]); the site adds it to the receiver.
struct JitGuardSite { const u8* rm; u8* entry; DWORD w38, w3E, wLoc, w3C, wVt; const DWORD* cls; DWORD unbox; };
static const int JIT_CELL_N = 2048, JIT_GUARD_N = 1024;
static JitLazyCell* g_jitLazyCell;   // carved from the region
static JitGuardSite* g_jitGuard;
static int g_jitLazyCellN, g_jitGuardN;
static JitLazyCell* JitLazyCellFor(const u8* rm) {
    for (int i = 0; i < g_jitLazyCellN; i++) if (g_jitLazyCell[i].rm == rm) return &g_jitLazyCell[i];
    if (g_jitLazyCellN >= JIT_CELL_N) return NULL;
    g_jitLazyCell[g_jitLazyCellN].rm = rm;
    return &g_jitLazyCell[g_jitLazyCellN++];
}
static void __cdecl JitGuardFill(JitGuardSite* s, const u8* rm) {
    s->rm = NULL, s->entry = NULL;
    if (!rm) return;
    const DWORD fl = *(const DWORD*)(rm + 0x44);
    if (!(fl & 0x100)) return;
    s->rm = rm;
    if (!(fl & 0x200) || *(const DWORD*)(rm + 0x24)) return;  // no this, or an EA native body
    JitLazyCell* cell = JitLazyCellFor(rm);
    if (!cell || !cell->entry) return;
    s->w38 = *(const unsigned short*)(rm + 0x38), s->w3E = *(const unsigned short*)(rm + 0x3E);
    s->wVt = s->w3E + *(const unsigned short*)(rm + 0x34);
    s->wLoc = s->wVt + *(const unsigned short*)(rm + 0x36);
    s->w3C = (fl & 0x800) ? *(const unsigned short*)(rm + 0x3C) : 0;
    const u8* k = s->cls ? (const u8*)(uintptr_t)*s->cls : NULL;  // the cache's class, the one the site just compared
    s->unbox = (fl & 0x400) && k && (k[0x14] & 8) ? 4 : 0;
    s->entry = cell->entry;
}
// a method off for the session (a verify mismatch): no call site enters its lazy body any more
static void JitLazyOff(const u8* rm) {
    for (int i = 0; i < g_jitLazyCellN; i++) if (g_jitLazyCell[i].rm == rm) g_jitLazyCell[i].entry = NULL;
    for (int i = 0; i < g_jitGuardN; i++) if (g_jitGuard[i].rm == rm) g_jitGuard[i].entry = NULL;
}
static void JitTrapOpenStub();
static void JitTrapCloseStub();

// ---- count mode (docs/data/monojit-exits-handoff.md C0), off by default: [MonoJit] Count=1. The host's MONOHOST_JITCOUNT census in
// game: one counter per exit site, per lazy call site and bail reason, per lazy-body exit that wrote frames (callee, exit word), calls
// and positions run per compiled method. Off = no table, no emitted byte. Counters live in one VirtualAlloc block (never in a save);
// JcnReport logs them as the host's jitbail / jitmat / jitnew / jitcalls / jitexit lines every 5 min, after each save and at exit.
// Increments are plain `inc [abs]` (not locked): compiled code runs on the sim thread (and the loading thread before the first tick).
static const int JCN_SITES = 32768, JCN_BAILS = 8192, JCN_MATS = 4096, JCN_LINES = 6000, JCN_REPORTS = 64;
static const char* const kJcnBail[12] = {"null this", "null *this", "null vtable", "remoting", "boxed vt", "cache class", "no lazy body",
                                         "ForceJIT", "depth slot", "no MonoInvocation", "no arena chunk", "arena full"};
struct JcnSite { int meth; unsigned short op; bool slow; volatile LONG n; const unsigned short* ip; };
struct JcnBail { const u8* callerRm; const unsigned short* ip; unsigned short op; bool lazy, guard; volatile LONG r[12]; };
struct JcnMat { const u8* rm; DWORD ip; unsigned short op; LONG n; };
struct JcnTabs {
    JcnSite site[JCN_SITES]; JcnBail bail[JCN_BAILS]; JcnMat mat[JCN_MATS];
    volatile LONG calls[JIT_METH_N], posRun[JIT_METH_N];
    int siteN, bailN, matN, matLast, meth, reports; bool slow; DWORD lastTick; LONG lastSave;
};
static JcnTabs* g_jcn;
static bool JcnOn() {
    if (!g_jcn) g_jcn = (JcnTabs*)VirtualAlloc(NULL, sizeof(JcnTabs), MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    if (g_jcn) g_jcn->lastTick = GetTickCount(), g_jcn->lastSave = ts3native_counters.saveCount;
    return g_jcn != NULL;
}
static void JcnExitSite(Em& e, const unsigned short* ip, DWORD op) {
    JcnTabs* t = g_jcn;
    if (!t || t->siteN >= JCN_SITES) return;
    JcnSite& s = t->site[t->siteN++];
    s.meth = t->meth, s.op = (unsigned short)op, s.slow = t->slow, s.n = 0, s.ip = ip;
    e.incAbs(&s.n);
}
static JcnBail* JcnBailSite(const u8* callerRm, const unsigned short* ip, DWORD op, bool lazy, bool guard) {
    JcnTabs* t = g_jcn;
    if (!t || t->bailN >= JCN_BAILS) return NULL;
    JcnBail& b = t->bail[t->bailN++];
    memset(&b, 0, sizeof(b));
    b.callerRm = callerRm, b.ip = ip, b.op = (unsigned short)op, b.lazy = lazy, b.guard = guard;
    return &b;
}
static void JcnMatHit(const u8* rm, DWORD ip, DWORD op) {  // JitLazyMatC: the innermost callee and its exit ip
    JcnTabs* t = g_jcn;
    int i = t->matLast;
    if (i >= t->matN || t->mat[i].rm != rm || t->mat[i].ip != ip)
        for (i = 0; i < t->matN && (t->mat[i].rm != rm || t->mat[i].ip != ip); i++) {}
    if (i == t->matN) { if (i >= JCN_MATS) return; t->mat[t->matN++] = {rm, ip, (unsigned short)op, 0}; }
    t->mat[i].n++, t->matLast = i;
}
static const char* JcnOpName(DWORD op) { return op < 0x1A9 && g_jitOpNames[op] ? g_jitOpNames[op] : "?"; }
typedef void (*JcnOut)(const char* line);
static void JcnLog(const char* line) { Log("%s", line); }
// the host's line formats (monohost-jit.cpp JitCountReport), only rows that ran; cut at JCN_LINES (bail / new / mat first)
static int JcnReportTo(const char* why, JcnOut out) {
    JcnTabs* t = g_jcn;
    char line[640], cn[128];
    int lines = 0, cut = 0;
    auto put = [&]() { if (lines < JCN_LINES) out(line), lines++; else cut++; };
    sprintf_s(line, "jitcount: begin %s #%d, %d methods, %d exit sites, %d call sites, %d mat rows", why, t->reports + 1, g_jitMethN, t->siteN, t->bailN, t->matN);
    out(line);
    for (int i = 0; i < t->bailN; i++) {
        const JcnBail& S = t->bail[i];
        int o = 0;
        char rs[400];
        rs[0] = 0;
        for (int r = 0; r < 12; r++) if (S.r[r]) o += _snprintf_s(rs + o, sizeof(rs) - o, _TRUNCATE, " %s=%ld", kJcnBail[r], S.r[r]);
        if (!o) continue;
        sprintf_s(line, "jitbail %s %d %X %s %s%s", JitMethodName(S.callerRm, cn, sizeof(cn)), (int)(S.ip - *(const unsigned short* const*)S.callerRm),
                  S.op, S.lazy ? "lazy" : "normal", S.guard ? "guard" : "direct", rs);
        put();
    }
    // "jitnew <meth> <word> <exits> <vt> <class flags +0x14> <ctor>", vt = EA's value-type test 0xE52160 (as the host's line)
    for (int s = 0; s < t->siteN; s++) {
        const JcnSite& S = t->site[s];
        if (S.op != 0xF4 || !S.n || S.meth >= g_jitMethN) continue;
        const u8* rm = g_jitMeth[S.meth].rm;
        const u8* c = (const u8*)(uintptr_t)(*(const DWORD* const*)(rm + 0x14))[S.ip[1]];
        const u8* mm = c ? *(const u8* const*)(c + 8) : NULL;
        const u8* k = mm ? *(const u8* const*)(mm + 8) : NULL;
        if (!k) continue;
        const bool vt = k[0xA6] == 0x11 ? !(k[0x14] & 0x10)
                      : k[0xA6] == 0x15 && (*(const DWORD*)(*(const u8* const*)(*(const u8* const*)(k + 0xA0) + 4) + 0x14) >> 3 & 1);
        sprintf_s(line, "jitnew %d %d %ld %d %X %s", S.meth, (int)(S.ip - *(const unsigned short* const*)rm), S.n, vt, *(const DWORD*)(k + 0x14), JitMethodName(c, cn, sizeof(cn)));
        put();
    }
    for (int i = 0; i < t->matN; i++) {
        const JcnMat& S = t->mat[i];
        sprintf_s(line, "jitmat %ld %s %d %s", S.n, JcnOpName(S.op), (int)((const unsigned short*)(uintptr_t)S.ip - *(const unsigned short* const*)S.rm),
                  JitMethodName(S.rm, cn, sizeof(cn)));
        put();
    }
    for (int m = 0; m < g_jitMethN; m++)
        if (t->calls[m] || t->posRun[m]) {
            sprintf_s(line, "jitcalls %d %ld %ld %d %s", m, t->calls[m], t->posRun[m], g_jitMeth[m].posN - g_jitMeth[m].pos0, g_jitMeth[m].name);
            put();
        }
    for (int s = 0; s < t->siteN; s++) {
        const JcnSite& S = t->site[s];
        if (!S.n || S.meth >= g_jitMethN) continue;
        sprintf_s(line, "jitexit %d %X %s %s %ld", S.meth, S.op, JcnOpName(S.op), S.slow ? "slow" : "op", S.n);
        put();
    }
    sprintf_s(line, "jitcount: end %s #%d, %d lines, %d left out (cap %d)", why, ++t->reports, lines, cut, JCN_LINES);
    out(line);
    return lines;
}
static void JcnReport(const char* why) {  // the game's tables can be half torn down at exit: a fault ends the report, never the game
    if (!g_jcn || g_jcn->reports >= JCN_REPORTS) return;
    __try { JcnReportTo(why, JcnLog); } __except (EXCEPTION_EXECUTE_HANDLER) { Log("jitcount: fault 0x%08lX in the %s report", GetExceptionCode(), why); }
}
static void JcnTick() {  // sim thread, end of a Simulate iteration
    JcnTabs* t = g_jcn;
    const DWORD now = GetTickCount();
    if (t->lastSave != ts3native_counters.saveCount) t->lastSave = ts3native_counters.saveCount, t->lastTick = now, JcnReport("save");
    else if (now - t->lastTick >= 300000) t->lastTick = now, JcnReport("interval");
}
static void JcnExit() { JcnReport("exit"); }  // DLL_PROCESS_DETACH

// exit to the live handler of base opcode `op` at ip (state is exactly the interpreter's: esi, frame memory)
static void EmitExit(Em& e, const unsigned short* ip, DWORD op, bool ret) {
    e.incAbs(&ts3native_counters.jitExits);
    if (g_jcn) JcnExitSite(e, ip, op);
    e.b(0xBF); e.d((DWORD)(uintptr_t)ip);   // mov edi, ip
    if (g_jitLazyPass) {                     // lazy body: write the frame first, then the same exit
        e.movRI(EAX, op);
        e.jmpTo((const void*)JitLazyMat);
        return;
    }
    if (ret && g_jitMode == 2) { e.jmpTo((const void*)JitVerifyExitStub); return; }
    e.op(0x89, EDI, EBP, -4);                // mov [ebp-4], edi
    e.movRI(EAX, op);
    e.rr(0x8B, EAX, EDX);                    // mov edx, eax (rr: r/m operand first, reg operand second)
    e.b(0xFF); e.b(0x25); e.d((DWORD)(uintptr_t)(g_jitTableP + op));  // jmp dword ptr [table + op*4]: the live entry (Satb's stub in a span)
}
// the value-type moves' pieces: the vt cursor ([ebp-0xC], or the block's B_VT in a lazy body), a memcpy through EA's CRT thunk
// (cdecl: eax, ecx, edx go). The block reads happen with esp at the body's entry value, so the opEsp offsets hold.
static void VtLoad(Em& e, int r) { if (g_jitLazyPass) e.opEsp(0x8B, r, 4 + B_VT); else e.op(0x8B, r, EBP, -0xC); }
static void VtStore(Em& e, int r) { if (g_jitLazyPass) e.opEsp(0x89, r, 4 + B_VT); else e.op(0x89, r, EBP, -0xC); }
static void VtAdd(Em& e, int n) {  // add dword [cursor], n
    if (!n) return;
    if (g_jitLazyPass) { e.b(0x81); e.b(0x84); e.b(0x24); e.d(4 + B_VT); } else { e.b(0x81); e.m(0, EBP, -0xC); }
    e.d((DWORD)n);
}
static void VtCopy(Em& e, int dst, int src, DWORD n) {  // memcpy(dst, src, n): push n; push src; push dst; call thunk; add esp, 12
    e.b(0x68); e.d(n); e.b(0x50 + src); e.b(0x50 + dst);
    e.b(0xE8); e.d(g_jitMemcpy - ((DWORD)(uintptr_t)e.p + 4));
    e.b(0x83); e.b(0xC4); e.b(12);
}
// round 10 C5 (docs/207 §16.3): isinst of a class whose kind is settled (EA's 0xE56502: null stays; interface through the
// max-iid test and Class_GetInterfaceOffset 0xD70870; else the supertype table at depth). The class-init bit [class+0x14]&1,
// the remoting bit 0x40000 and an array class leave the site to EA's handler.
static bool EmitIsinst(Em& e, const u8* c, u8** cast = NULL) {  // cast: castclass, a failed test takes these two jumps, the slot stays
    if (!c || !(c[0x14] & 1) || (*(const DWORD*)(c + 0x14) & 0x40000) || c[0x10]) return false;
    e.op(0x8B, ECX, ESI, -8); e.rr(0x85, ECX, ECX); u8* isNull = e.jcc(0x84);
    e.op(0x8B, EDX, ECX, 0); e.op(0x8B, EAX, EDX, 0);  // vtable, class
    u8* no0, * no1;
    if (c[0x60] & 0x20) {
        const DWORD iid = *(const unsigned short*)(c + 0x44);
        e.b(0x66); e.b(0x81); e.m(7, EDX, 0x14); e.b(iid & 0xFF); e.b(iid >> 8); no0 = e.jcc(0x82);  // cmp word [vtable+0x14], iid; jb
        e.b(0x68); e.d(iid); e.b(0x50);
        e.b(0xE8); e.d(g_jitIfaceOff - ((DWORD)(uintptr_t)e.p + 4));
        e.b(0x83); e.b(0xC4); e.b(8);
        e.b(0x83); e.b(0xF8); e.b(0xFF); no1 = e.jcc(0x84);                                          // cmp eax, -1; je
    } else {
        const DWORD depth = *(const unsigned short*)(c + 0x40);
        e.b(0x66); e.b(0x81); e.m(7, EAX, 0x40); e.b(depth & 0xFF); e.b(depth >> 8); no0 = e.jcc(0x82);  // cmp word [class+0x40], depth; jb
        e.op(0x8B, EAX, EAX, 0x3C);
        e.b(0x81); e.m(7, EAX, (int)(depth - 1) * 4); e.d((DWORD)(uintptr_t)c); no1 = e.jcc(0x85);        // cmp [supertypes + depth*4 - 4], class; jne
    }
    if (cast) { cast[0] = no0, cast[1] = no1; Fix(isNull, e.p); return true; }
    u8* skip = e.jmp();
    Fix(no0, e.p), Fix(no1, e.p);
    e.b(0xC7); e.m(0, ESI, -8); e.d(0);
    Fix(skip, e.p), Fix(isNull, e.p);
    return true;
}
// round 10 C5 (docs/207 §16.3): a direct call of an internal call that cannot raise and reads no frame [verified: disassembly
// of both, one pointer argument on the eval stack, one object back]. EA's path (frame push, 0xE52630: setjmp, thunk, result
// marshal) goes; its managed_code byte [ThreadContext+0x15] stays, its interruption checkpoint waits for the next EA call.
// Null exits: EA's path faults or throws. A lazy body never gets one: it makes no native call that can allocate.
static const struct { const char* ns, * cls, * name; DWORD fn; } kJitIcalls[] = {
    {"System", "Type", "internal_from_handle", TS3A(0x00E7AB60)}, {"System", "Enum", "get_value", TS3A(0x00E7D520)},
};
static DWORD JitIcallFn(const unsigned short* ip, const u8* callerRm) {
    const u8* c = (const u8*)(uintptr_t)(*(const DWORD* const*)(callerRm + 0x14))[ip[1]];
    const u8* mm = c ? *(const u8* const*)(c + 8) : NULL;
    if (!mm || !(*(const unsigned short*)(mm + 2) & 0x1000)) return 0;  // MonoMethod.iflags: internal call
    const u8* k = *(const u8* const*)(mm + 8);
    for (const auto& i : kJitIcalls)
        if (!strcmp(*(const char* const*)(mm + 0x18), i.name) && !strcmp(*(const char* const*)(k + 0x34), i.cls) && !strcmp(*(const char* const*)(k + 0x38), i.ns)) return i.fn;
    return 0;
}
// docs/537 §20 A: the pure Array icalls, inline at a call / callvirt site. EA's functions [verified: game disassembly 2026-11-08, the
// same bytes as the host's; the icall table's pointers at 0x10B58D4 / 0x10B58D8 / 0x10B58DC hold these addresses, JitPureIcallsOk
// checks bytes and table once at install]: GetLength(this, dim) 0xE79FC0 and GetLowerBound(this, dim) 0xE7A000 raise
// IndexOutOfRangeException iff dim < 0 or dim >= klass->rank (vtable->klass +0x10), else bounds (this+4) NULL gives max_length
// (this+8) / 0, else bounds[dim] .length (+0) / .lower_bound (+4); GetRank(this) 0xE79FB0 is klass->rank. They read no frame and
// do not allocate, so a lazy body may run them. A null this or a bad dimension exits before any change: EA's call path throws.
// EA's icall return leaves the result slot's high dword as the word above the arguments ([esi+4], as EmitIcall).
static const u8 kPureGetLength[] = {0x56, 0x8B, 0x74, 0x24, 0x08, 0x57, 0x8B, 0x7C, 0x24, 0x10, 0x85, 0xFF, 0x7C, 0x0C, 0x8B, 0x06, 0x8B, 0x08, 0x0F, 0xB6,
    0x51, 0x10, 0x3B, 0xFA, 0x7C, 0x0E, 0xE8, 0xC1, 0xD2, 0xFF, 0xFF, 0x50, 0xE8, 0x0B, 0x79, 0x02, 0x00, 0x83, 0xC4, 0x04, 0x8B, 0x46, 0x04, 0x85,
    0xC0, 0x75, 0x06, 0x8B, 0x46, 0x08, 0x5F, 0x5E, 0xC3, 0x8B, 0x04, 0xF8};
static const u8 kPureGetLower[] = {0x56, 0x8B, 0x74, 0x24, 0x0C, 0x85, 0xF6, 0x57, 0x8B, 0x7C, 0x24, 0x0C, 0x7C, 0x0C, 0x8B, 0x07, 0x8B, 0x08, 0x0F, 0xB6,
    0x51, 0x10, 0x3B, 0xF2, 0x7C, 0x0E, 0xE8, 0x81, 0xD2, 0xFF, 0xFF, 0x50, 0xE8, 0xCB, 0x78, 0x02, 0x00, 0x83, 0xC4, 0x04, 0x8B, 0x47, 0x04, 0x85,
    0xC0, 0x75, 0x03, 0x5F, 0x5E, 0xC3, 0x8B, 0x44, 0xF0, 0x04, 0x5F, 0x5E, 0xC3};
static const u8 kPureGetRank[] = {0x8B, 0x44, 0x24, 0x04, 0x8B, 0x08, 0x8B, 0x11, 0x0F, 0xB6, 0x42, 0x10, 0xC3};
static bool JitPureIcallsOk() {
    return !memcmp((const void*)(uintptr_t)TS3A(0x00E79FC0), kPureGetLength, sizeof(kPureGetLength)) && !memcmp((const void*)(uintptr_t)TS3A(0x00E7A000), kPureGetLower, sizeof(kPureGetLower))
        && !memcmp((const void*)(uintptr_t)TS3A(0x00E79FB0), kPureGetRank, sizeof(kPureGetRank)) && *(const DWORD*)(uintptr_t)TS3A(0x010B58D4) == TS3A(0x00E79FC0)
        && *(const DWORD*)(uintptr_t)TS3A(0x010B58D8) == TS3A(0x00E7A000) && *(const DWORD*)(uintptr_t)TS3A(0x010B58DC) == TS3A(0x00E79FB0);
}
static int JitPureIcall(const unsigned short* ip, DWORD op, const u8* callerRm) {  // 1 GetLength, 2 GetLowerBound, 3 GetRank, 0 none
    const u8* c = (const u8*)(uintptr_t)(*(const DWORD* const*)(callerRm + 0x14))[op == 0xEA ? ip[1] : ip[3]];
    const u8* mm = c ? *(const u8* const*)(c + 8) : NULL;
    if (!mm || !(*(const unsigned short*)(mm + 2) & 0x1000) || (*(const unsigned short*)mm & 0x40)) return 0;  // internal call, not virtual
    const u8* k = *(const u8* const*)(mm + 8);
    if (strcmp(*(const char* const*)(k + 0x34), "Array") || strcmp(*(const char* const*)(k + 0x38), "System")) return 0;
    const char* n = *(const char* const*)(mm + 0x18);
    const int pk = !strcmp(n, "GetLength") ? 1 : !strcmp(n, "GetLowerBound") ? 2 : !strcmp(n, "GetRank") ? 3 : !strcmp(n, "FastCopy") ? 4 : 0;
    return pk == 4 ? (g_jitFastCopy ? 4 : 0) : g_jitPureIcalls ? pk : 0;  // each kind only with its own switch
}
// docs/537 §20 C3: Array.FastCopy(source, source_idx, dest, dest_idx, length) 0xE7A070 (icall table 0x10B58CC) [verified: game
// disassembly, handoff C3]: returns false unless both ranks (klass+0x10) match and both bounds (+4) are NULL, and idx + length <=
// max_length (+8, unsigned) for dest then source; element classes (klass+8) equal and a value type (flags +0x14 bit 3) without
// references (bit 23) = memmove(dest + 0x10 + size * dest_idx, source + 0x10 + size * source_idx, size * length), size = source
// klass+0x5C, true. Only that memmove path runs here; any other input, a null array or a negative index or length (managed
// Array.Copy throws before the call) returns 0 with nothing changed and the site exits, so EA's call (or [AutShadow] Satb's wrapper
// SatbRtFastCopy in the slot) runs as before. No allocation, no frame read, no reference stored; the destination range goes to
// JitSatbStore first (sim side of a span, as SatbRtFastCopy logs it). Compiled code runs only on the sim thread (JitEnter), so the
// wrapper's worker-side patch never applies here.
static const DWORD kFastCopyFnv = 0x555A37B8;  // FNV-1a of EA's 641 bytes 0xE7A070..0xE7A2F0
static bool JitFastCopyOk() {
    const DWORD slot = *(const DWORD*)(uintptr_t)TS3A(0x010B58CC);
    return McFnv((const u8*)(uintptr_t)TS3A(0x00E7A070), 0x281) == kFastCopyFnv && (slot == TS3A(0x00E7A070) || slot == (DWORD)(uintptr_t)SatbRtFastCopy);
}
static volatile LONG g_jitFastCopySites, g_jitFastCopyRuns, g_jitFastCopyExits;
static int __cdecl JitFastCopy(const u8* src, int si, u8* dst, int di, int len) {
    if (!src || !dst || si < 0 || di < 0 || len < 0) return g_jitFastCopyExits++, 0;
    const u8* sk = **(const u8* const* const*)src, *dk = **(const u8* const* const*)dst;
    if (sk[0x10] != dk[0x10] || *(void* const*)(src + 4) || *(void* const*)(dst + 4)) return g_jitFastCopyExits++, 0;
    if ((unsigned)(di + len) > *(const unsigned*)(dst + 8) || (unsigned)(si + len) > *(const unsigned*)(src + 8)) return g_jitFastCopyExits++, 0;
    const u8* el = *(const u8* const*)(sk + 8);
    const DWORD fl = el ? *(const DWORD*)(el + 0x14) : 0;
    if (el != *(const u8* const*)(dk + 8) || !(fl & 8) || (fl & (1u << 23))) return g_jitFastCopyExits++, 0;
    const int sz = *(const int*)(sk + 0x5C);
    if (len) JitSatbStore((DWORD)(uintptr_t)(dst + 0x10 + sz * di), (DWORD)(sz * len));
    memmove(dst + 0x10 + sz * di, src + 0x10 + sz * si, (size_t)(unsigned)(sz * len));
    g_jitFastCopyRuns++;
    return 1;
}
static void EmitPureIcall(Em& e, int kind, u8** exitRel) {
    if (kind == 4) {  // FastCopy: five args at [esi-40]..[esi-8]; 0 = exit before any change; the bool result keeps [esi+4] high, as kind 1
        for (int d = -8; d >= -40; d -= 8) e.op(0xFF, 6, ESI, d);  // push dword [esi+d]: length .. source
        e.b(0xE8); e.d((DWORD)(uintptr_t)JitFastCopy - ((DWORD)(uintptr_t)e.p + 4)); e.b(0x83); e.b(0xC4); e.b(20);
        e.rr(0x85, EAX, EAX); exitRel[0] = e.jcc(0x84);
        e.op(0x8B, ECX, ESI, 4); e.op(0x89, EAX, ESI, -40); e.op(0x89, ECX, ESI, -36); e.addEsi(-32);
        g_jitFastCopySites++; return;
    }
    const int thisOff = kind == 3 ? -8 : -16;
    e.op(0x8B, EAX, ESI, thisOff); e.rr(0x85, EAX, EAX); exitRel[0] = e.jcc(0x84);
    e.op(0x8B, EDX, EAX, 0); e.op(0x8B, EDX, EDX, 0); e.b(0x0F); e.b(0xB6); e.b(0x52); e.b(0x10);  // movzx edx, byte [edx+0x10]: rank
    if (kind == 3) { e.op(0x8B, ECX, ESI, 4); e.op(0x89, EDX, ESI, -8); e.op(0x89, ECX, ESI, -4); return; }
    e.op(0x8B, ECX, ESI, -8); e.rr(0x3B, EDX, ECX); exitRel[1] = e.jcc(0x83);                      // cmp ecx, edx; jae (dim < 0 or >= rank)
    e.op(0x8B, EDX, EAX, 4); e.rr(0x85, EDX, EDX); e.b(0x74); u8* nob = e.p; e.b(0);              // bounds; jz nob
    e.b(0x8B); e.b(0x44); e.b(0xCA); e.b(kind == 1 ? 0 : 4);                                        // mov eax, [edx+ecx*8 (+4)]
    e.b(0xEB); u8* st = e.p; e.b(0);
    *nob = (u8)(e.p - (nob + 1));
    if (kind == 1) e.op(0x8B, EAX, EAX, 8); else e.rr(0x33, EAX, EAX);                             // max_length, or 0
    *st = (u8)(e.p - (st + 1));
    e.op(0x8B, ECX, ESI, 4); e.op(0x89, EAX, ESI, -16); e.op(0x89, ECX, ESI, -12); e.addEsi(-8);
}
// fld, fld, fucomip st(1), fstp st(0), setcc al [, setcc cl; and or or] (docs/221, tools/emu/specs/mint_float.py fcmp); al = the relation
static void EmitFcmp(Em& e, int x) {
    e.fld8(ESI, x & 1 ? -8 : -16); e.fld8(ESI, x & 1 ? -16 : -8);
    e.b(0xDF); e.b(0xE9); e.b(0xDD); e.b(0xD8);
    e.b(0x0F); e.b((x >> 8) & 0xFF); e.b(0xC0);
    if ((x >> 16) & 0xFF) { e.b(0x0F); e.b((x >> 16) & 0xFF); e.b(0xC1); e.b(x & 2 ? 0x0A : 0x22); e.b(0xC1); }
}
static void EmitFpStore(Em& e, int w, int base, int disp) {  // st(0) to a slot: r4 as EA's 0xE58966, twice through a float
    if (w == 7) { e.fstp4(EBP, -0x1C); e.fld4(EBP, -0x1C); e.fstp4(base, disp); } else e.fstp8(base, disp);
}
static u8* EmitIcall(Em& e, DWORD fn) {  // returns the null exit's jcc; the slot keeps its high dword as EA's 8-byte return copy leaves it
    e.op(0x8B, EAX, ESI, -8); e.rr(0x85, EAX, EAX); u8* isNull = e.jcc(0x84);
    e.op(0x8B, ECX, EBP, 0xC); e.b(0xC6); e.m(0, ECX, 0x15); e.b(0);
    e.b(0x50); e.b(0xE8); e.d(fn - ((DWORD)(uintptr_t)e.p + 4)); e.b(0x83); e.b(0xC4); e.b(4);
    e.op(0x8B, ECX, EBP, 0xC); e.b(0xC6); e.m(0, ECX, 0x15); e.b(1);
    e.op(0x89, EAX, ESI, -8); e.op(0x8B, ECX, ESI, 4); e.op(0x89, ECX, ESI, -4);
    return isNull;
}
static void JitPatchMeth(const JitMeth& m, bool on);

// a type a con.callvirt dereferences to a reference (EA's 0xE52160 reads the container's valuetype bit)
static bool JitConRef(const u8* k) {
    if (!k) return false;
    const u8 t = k[0xA6];  // byval_arg.type
    if (t == 0x15) {       // GENERICINST: the container class decides
        const u8* gc = *(const u8* const*)(k + 0xA0);
        const u8* cc = gc ? *(const u8* const*)(gc + 4) : NULL;
        return cc && !(cc[0x14] & 8);
    }
    return t == 0x0E || t == 0x12 || t == 0x14 || t == 0x1C || t == 0x1D;  // string, class, array, object, szarray
}
// a lazy call site's target and kind: call (0xEA, method in word 1), con.call (0xEB: the class word is skipped and the
// method is word 2, docs/213 §1 row 3), callvirt (0xEC, declared method in word 3) or con.callvirt of a reference type
// (0xED, method in word 4) of a non-virtual method of a reference type (direct) or of a virtual one (class guard on EA's
// inline cache). A direct target must be transformed (the site bakes in its frame sizes); a guarded site reads them from
// its JitGuardSite at run time.
// B2b: the class a newobj (0xF4, ctor in word 1) builds, when it takes EA's value-type branch [verified 0xE56152-0xE56215]:
// class = ctor->method->klass; not the array-ctor path ([class+0x24] == [0x1220E8C] goes to 0xE56188); valuetype flag 8, then
// 0xE52160 (VALUETYPE and not an enum, or a GENERICINST of a value-type container). Layout flag 4 is ours (EA does not read the
// size here: the vtresult after the newobj advances the cursor); a class failing any test stays EA's newobj for good.
static const u8* JitNewVtClass(const u8* c) {
    const u8* mm = c ? *(const u8* const*)(c + 8) : NULL;
    const u8* k = mm ? *(const u8* const*)(mm + 8) : NULL;
    if (!k || *(const DWORD*)(k + 0x24) == *g_jitArrCtorP || !(k[0x14] & 8) || !(k[0x14] & 4)) return NULL;
    const bool vt = k[0xA6] == 0x11 ? !(k[0x14] & 0x10)
                  : k[0xA6] == 0x15 && (*(const DWORD*)(*(const u8* const*)(*(const u8* const*)(k + 0xA0) + 4) + 0x14) >> 3 & 1);
    return vt ? k : NULL;
}
static bool JitLazySite(const unsigned short* ip, DWORD op, const u8* callerRm, const u8** cOut, bool* guardOut) {
    const DWORD* items = *(const DWORD* const*)(callerRm + 0x14);
    if (op == 0xF4) {  // B2b newobj of a value type: a direct site of the (instance, value-type this) ctor
        const u8* c = (const u8*)(uintptr_t)items[ip[1]];
        if (!g_jitNewVt || !JitNewVtClass(c)) return false;
        const DWORD fl = *(const DWORD*)(c + 0x44);
        if ((fl & 0x700) != 0x700 || *(const DWORD*)(c + 0x24)) return false;  // transformed, instance, value-type this; no native body
        *cOut = c, *guardOut = false;
        return true;
    }
    const u8* c = (const u8*)(uintptr_t)items[op == 0xEA ? ip[1] : op == 0xEB ? ip[2] : op == 0xEC ? ip[3] : ip[4]];
    const u8* mm = c ? *(const u8* const*)(c + 8) : NULL;
    if (!c || !mm) return false;
    if (op == 0xED && !JitConRef((const u8*)(uintptr_t)items[ip[3]])) return false;
    const DWORD fl = *(const DWORD*)(c + 0x44);
    bool guard = false;
    if (op == 0xEC || op == 0xED) {
        if (!(fl & 0x200) || (fl & 0x400)) return false;
        guard = (*(const unsigned short*)mm & 0x40) != 0;
    }
    if (guard && g_jitLeafOnly) return false;
    if (!guard && !(fl & 0x100)) return false;  // not transformed: its frame sizes are still 0
    *cOut = c, *guardOut = guard;
    return true;
}
// lazy call site. Every test EA's call path (and docs/139 McCall) makes runs before any write; any of them, or no lazy
// body yet, takes the normal exit. In a lazy body (a chain) the depth test adds the frames pending below this one, the
// block's level is one higher, and the return-slot word is this body's own vt cursor (its frame is not written).
static bool EmitLazyCall(Em& e, const unsigned short* ip, DWORD op, const u8* callerRm) {
    const u8* c; bool guard;
    if (!JitLazySite(ip, op, callerRm, &c, &guard)) return false;
    const DWORD fl = *(const DWORD*)(c + 0x44);
    // nvt (B2b, newobj of a value type): no receiver slot, this = the caller's vt cursor; EA writes no return-slot word, and its
    // newobj post (0xE561B0) pushes the cursor at the args' start; the cursor is not advanced (the vtresult that follows does it)
    const bool inst = (fl & 0x200) != 0, vtm = (fl & 0x400) != 0, lazy = g_jitLazyPass, nvt = op == 0xF4;
    JitLazyCell* cell = NULL;
    JitGuardSite* gs = NULL;
    if (guard) { if (g_jitGuardN >= JIT_GUARD_N) return false; gs = &g_jitGuard[g_jitGuardN++]; gs->cls = &(*(const DWORD* const*)(callerRm + 0x14))[ip[1]]; }
    else if (!(cell = JitLazyCellFor(c))) return false;
    const DWORD* items = *(const DWORD* const*)(callerRm + 0x14);
    const int n = c[0x40], thisOff = -(n + 1) * 8, rsp = inst && !nvt ? thisOff : -n * 8, ripW = op == 0xEA || nvt ? 2 : op == 0xEB ? 3 : op == 0xEC ? 4 : 5;
    const unsigned short w38 = *(const unsigned short*)(c + 0x38), w3E = *(const unsigned short*)(c + 0x3E);
    const unsigned short w34 = *(const unsigned short*)(c + 0x34), w36 = *(const unsigned short*)(c + 0x36);
    const DWORD proxy = (DWORD)(uintptr_t)g_jitProxyClassP;
    u8* bail[16];
    u8 why[16];  // count mode: the bail reason per jump (kJcnBail)
    int nb = 0;
    if (guard) {  // receiver not null, with a vtable, not the remoting class, not a value type, of the cache's class; target = the cache's
        e.op(0x8B, ECX, ESI, thisOff); e.rr(0x85, ECX, ECX); why[nb] = 0, bail[nb++] = e.jcc(0x84);
        if (op == 0xED) { e.op(0x8B, ECX, ECX, 0); e.rr(0x85, ECX, ECX); why[nb] = 1, bail[nb++] = e.jcc(0x84); }  // this = *ptr
        e.op(0x8B, EAX, ECX, 0); e.rr(0x85, EAX, EAX); why[nb] = 2, bail[nb++] = e.jcc(0x84);
        e.op(0x8B, EAX, EAX, 0); e.b(0x3B); e.b(0x05); e.d(proxy); why[nb] = 3, bail[nb++] = e.jcc(0x84);
        if (!g_jitCallExit || op == 0xED) { e.b(0xF6); e.m(0, EAX, 0x14); e.b(8); why[nb] = 4, bail[nb++] = e.jcc(0x85); }  // a boxed value type: CallExit unboxes below
        e.b(0x3B); e.b(0x05); e.d((DWORD)(uintptr_t)&items[ip[1]]); why[nb] = 5, bail[nb++] = e.jcc(0x85);  // cmp eax, [cache.class]
        e.b(0xA1); e.d((DWORD)(uintptr_t)&items[ip[1] + 1]);                                   // mov eax, [cache.target]
        e.b(0x3B); e.b(0x05); e.d((DWORD)(uintptr_t)&gs->rm); u8* same = e.jcc(0x84);
        e.b(0x60); e.b(0x50); e.b(0x68); e.d((DWORD)(uintptr_t)gs);                             // pushad; push eax; push site
        e.b(0xE8); e.d((DWORD)(uintptr_t)JitGuardFill - ((DWORD)(uintptr_t)e.p + 4));
        e.b(0x83); e.b(0xC4); e.b(8); e.b(0x61);                                                // add esp, 8; popad
        Fix(same, e.p);
        e.b(0x83); e.b(0x3D); e.d((DWORD)(uintptr_t)&gs->entry); e.b(0); why[nb] = 6, bail[nb++] = e.jcc(0x84);  // no lazy body (yet)
        if (g_jitCallExit && op == 0xEC) { e.b(0x03); e.b(0x0D); e.d((DWORD)(uintptr_t)&gs->unbox); }  // add ecx, [site.unbox]: this = box + 4
    } else {
        e.b(0x83); e.b(0x3D); e.d((DWORD)(uintptr_t)&cell->entry); e.b(0); why[nb] = 6, bail[nb++] = e.jcc(0x84);  // no lazy body (yet)
    }
    e.b(0x80); e.b(0x3D); e.d((DWORD)(uintptr_t)g_jitForceJit); e.b(0x75); why[nb] = 7, bail[nb++] = e.jcc(0x85);  // MonoPatcher ForceJIT active
    if (nvt)
        VtLoad(e, ECX);  // this = the vt cursor (EA's 0xE5620C: [ebp-0x50] = [rec+0x20] = cursor); no zero fill, the ctor writes it
    else if (!guard) {
        if (inst && !vtm) {  // EA's remoting check reads this->vtable->klass (null: EA's path faults or throws)
            e.op(0x8B, ECX, ESI, thisOff); e.rr(0x85, ECX, ECX); why[nb] = 0, bail[nb++] = e.jcc(0x84);
            if (op == 0xED) { e.op(0x8B, ECX, ECX, 0); e.rr(0x85, ECX, ECX); why[nb] = 1, bail[nb++] = e.jcc(0x84); }  // this = *ptr
            e.op(0x8B, EAX, ECX, 0); e.rr(0x85, EAX, EAX); why[nb] = 2, bail[nb++] = e.jcc(0x84);
            e.op(0x8B, EAX, EAX, 0); e.b(0x3B); e.b(0x05); e.d(proxy); why[nb] = 3, bail[nb++] = e.jcc(0x84);
            if (op >= 0xEC) { e.b(0xF6); e.m(0, EAX, 0x14); e.b(8); why[nb] = 4, bail[nb++] = e.jcc(0x85); }  // value-type receiver
        } else if (inst)
            e.op(0x8B, ECX, ESI, thisOff);  // a value-type method: this is a pointer (con.call too)
        else
            e.rr(0x33, ECX, ECX);
    }
    e.op(0x8B, EDX, EBP, 8);
    e.op(0x8B, EAX, EDX, 0x140);
    if (lazy) e.opEsp(0x03, EAX, 4 + B_LVL);                      // + the frames pending below this body
    e.b(0x40);                                                    // eax = depth + 1 (+ pending)
    e.op(0x3B, EAX, EBP, -0x30); why[nb] = 8, bail[nb++] = e.jcc(0x8D);        // the slot must hold a MonoInvocation (JitLazyMat needs it; slots fill in order)
    e.b(0x69); e.b(0xC0); e.d(0x130);                             // imul eax, eax, 0x130
    e.b(0x83); e.b(0xBC); e.b(0x05); e.d((DWORD)-0x4C74); e.b(0); why[nb] = 9, bail[nb++] = e.jcc(0x84);  // cmp dword [ebp+eax-0x4C74], 0
    e.op(0x8B, EDX, EDX, 0x13C); e.rr(0x85, EDX, EDX); why[nb] = 10, bail[nb++] = e.jcc(0x84);              // arena chunk with room
    e.op(0x8B, EAX, EDX, 0);
    if (guard) { e.b(0x03); e.b(0x05); e.d((DWORD)(uintptr_t)&gs->w38); } else { e.b(0x05); e.d(w38); }
    e.op(0x3B, EAX, EDX, 4); why[nb] = 11, bail[nb++] = e.jcc(0x87);
    // commit: arena bump, EA's return-slot word, the block, the callee's registers
    e.op(0x89, EAX, EDX, 0);                                      // [chunk] = top + size
    if (op == 0xED) e.op(0x89, ECX, ESI, thisOff);                // con.callvirt: the dereferenced receiver in its slot, as EA's handler leaves it
    if (guard) { e.b(0x2B); e.b(0x05); e.d((DWORD)(uintptr_t)&gs->w38); } else { e.b(0x2D); e.d(w38); }  // eax = callee arena base
    if (!nvt) {
        VtLoad(e, EDX);
        e.op(0x89, EDX, ESI, 0);                                  // [return slot] = vt cursor, as EA's call writes
    }
    if (guard) { e.rr(0x8B, EAX, EDX); e.b(0x03); e.b(0x15); e.d((DWORD)(uintptr_t)&gs->wVt); } else e.op(0x8D, EDX, EAX, w3E + w34);
    e.b(0x50 + EDX);                                              // B_VT = the callee's vt area start
    e.incAbs(&ts3native_counters.jitLazyCalls);
    if (lazy) { e.b(0xFF); e.b(0xB4); e.b(0x24); e.d(8 + B_LVL); e.b(0xFF); e.b(0x04); e.b(0x24); }  // B_LVL = own level + 1 (8: B_VT is pushed already; docs/537 §20 A2)
    else { e.b(0x6A); e.b(1); }                                                                      // B_LVL = 1
    e.b(0x68); e.d((DWORD)(uintptr_t)ip);                         // B_CIP
    e.b(0x50 + EDI); e.b(0x50 + EBX);                             // B_EDI, B_EBX
    if (guard) { e.b(0xFF); e.b(0x35); e.d((DWORD)(uintptr_t)&gs->rm); } else { e.b(0x68); e.d((DWORD)(uintptr_t)c); }  // B_RM
    e.b(0x50 + ESI);                                              // B_RSLOT
    e.op(0x8D, EDX, ESI, rsp); e.b(0x50 + EDX);                   // B_RSP
    e.b(0x68); e.d((DWORD)(uintptr_t)(ip + ripW));                // B_RIP
    e.b(0x50 + ECX);                                              // B_THIS
    if (nvt && !n) { e.b(0x6A); e.b(0); }                         // B_ARGS: EA's newobj passes 0 with no params (0xE56277)
    else { e.op(0x8D, EDX, ESI, -n * 8); e.b(0x50 + EDX); }       // B_ARGS
    e.rr(0x8B, EAX, EBX);                                         // mov ebx, eax
    if (guard) {
        e.rr(0x8B, EAX, ESI); e.b(0x03); e.b(0x35); e.d((DWORD)(uintptr_t)&gs->w3E);   // esi = eax + eval offset
        e.rr(0x8B, EAX, EDI); e.b(0x03); e.b(0x3D); e.d((DWORD)(uintptr_t)&gs->wLoc);  // edi = eax + locals offset
        e.b(0x8B); e.b(0x0D); e.d((DWORD)(uintptr_t)&gs->w3C); e.rr(0x85, ECX, ECX); u8* noInit = e.jcc(0x84);  // init locals, if the target has them
        e.b(0x50 + EDI); e.rr(0x33, EAX, EAX); e.b(0xF3); e.b(0xAA); e.b(0x58 + EDI);
        Fix(noInit, e.p);
    } else {
        e.op(0x8D, ESI, EAX, w3E);
        e.op(0x8D, EDI, EAX, w3E + w34 + w36);
        if (fl & 0x800) {  // init locals: EA clears rm+0x3C bytes at the locals base
            e.b(0x50 + EDI); e.movRI(ECX, *(const unsigned short*)(c + 0x3C)); e.rr(0x33, EAX, EAX); e.b(0xF3); e.b(0xAA); e.b(0x58 + EDI);
        }
    }
    const bool window = g_jitTrap && !lazy;  // the outermost call of a chain opens the reader-trap window (docs/537 §3)
    if (window) { e.b(0xE8); e.d((DWORD)(uintptr_t)JitTrapOpenStub - ((DWORD)(uintptr_t)e.p + 4)); }
    e.b(0xFF); e.b(0x15); e.d(guard ? (DWORD)(uintptr_t)&gs->entry : (DWORD)(uintptr_t)&cell->entry);  // call [entry]
    if (window) { e.b(0xE8); e.d((DWORD)(uintptr_t)JitTrapCloseStub - ((DWORD)(uintptr_t)e.p + 4)); }
    e.opEsp(0x8B, EBX, B_EBX); e.opEsp(0x8B, EDI, B_EDI); e.opEsp(0x8B, ESI, B_RSP);
    if (!nvt && c[0x44] != 1) {  // EA's frame return copies (a newobj takes the post at 0xE54480 instead) the return slot's 8 bytes to the caller's new top
        e.opEsp(0x8B, EAX, B_RSLOT);
        e.op(0x8B, ECX, EAX, 0); e.op(0x89, ECX, ESI, 0); e.op(0x8B, ECX, EAX, 4); e.op(0x89, ECX, ESI, 4); e.addEsi(8);
    }
    e.b(0x83); e.b(0xC4); e.b(B_SIZE);                            // add esp, B_SIZE
    if (nvt) {  // EA's newobj post 0xE561BE: [args start] = [rec+0x20] (the cursor, unchanged), high dword [rec+0x24] of the caller's
        // record (not written by this newobj: whatever that slot holds), esi += 8. Caller's record = slot depth (+ own level in a lazy body)
        VtLoad(e, ECX); e.op(0x89, ECX, ESI, 0);
        e.op(0x8B, EDX, EBP, 8); e.op(0x8B, EAX, EDX, 0x140);
        if (lazy) e.opEsp(0x03, EAX, 4 + B_LVL);
        e.b(0x69); e.b(0xC0); e.d(0x130);                         // imul eax, eax, 0x130
        e.b(0x8B); e.b(0x84); e.b(0x05); e.d((DWORD)(-0x4C74 + 0x24));  // mov eax, [ebp + eax - 0x4C74 + 0x24]
        e.op(0x89, EAX, ESI, 4); e.addEsi(8);
    }
    u8* skip = e.jmp();
    if (JcnBail* st = g_jcn ? JcnBailSite(callerRm, ip, op, lazy, guard) : NULL) {  // count mode: one counter per bail reason, then the common exit
        u8* toExit[16];
        for (int i = 0; i < nb; i++) { Fix(bail[i], e.p); e.incAbs(&st->r[why[i]]); toExit[i] = e.jmp(); }
        for (int i = 0; i < nb; i++) Fix(toExit[i], e.p);
    } else
        for (int i = 0; i < nb; i++) Fix(bail[i], e.p);
    EmitExit(e, ip, op, false);
    Fix(skip, e.p);
    ts3native_counters.jitLazySites++;
    if (guard) ts3native_counters.jitGuardSites++;
    return true;
}
// lazy body ret / ret.void: values left on the eval stack go to EA's ret (it logs); else the 8-byte copy to the return
// slot, the arena pop and a native ret to the call site
static void EmitLazyRet(Em& e, const unsigned short* ip, DWORD op, const u8* rm, bool value) {
    const unsigned short w3E = *(const unsigned short*)(rm + 0x3E);
    e.op(0x8D, EAX, EBX, w3E + (value ? 8 : 0)); e.rr(0x39, ESI, EAX);
    u8* slow = e.jcc(0x87);
    if (value) {
        e.opEsp(0x8B, EAX, 4 + B_RSLOT);
        e.op(0x8B, ECX, ESI, -8); e.op(0x89, ECX, EAX, 0); e.op(0x8B, ECX, ESI, -4); e.op(0x89, ECX, EAX, 4);
    }
    e.op(0x8B, EAX, EBP, 8); e.op(0x8B, EAX, EAX, 0x13C); e.op(0x89, EBX, EAX, 0);  // [chunk] = callee arena base
    e.b(0xC3);
    Fix(slow, e.p);
    EmitExit(e, ip, op, true);
}

// ---- inlining of tiny leaf instance methods at compiled call / callvirt sites (docs/207 §8, docs/537 §9) ----
// Callee: transformed, compiled (so its IL passed the hash gate), instance, no EA native body, no clauses, body = stinarg*
// then at most 12 of ldthis / ldarg (32-bit) / ldfld (32-bit, on this) / ldc / 32-bit ALU, compares, inc/dec/neg/not,
// narrow conv, pop, then one ret (value) or ret.void, no branch, call, store, static. callvirt only for a non-virtual
// declared method of a reference type. Tier 2 adds ldfld on any base and ldind (a null base takes the normal call path),
// and a value-type this (call, con.call: the slot holds a byref). Inline code: every test runs before any write, so any
// of them leaves the normal call path from the same state: this null or the remoting class (EA throws or goes through the
// proxy), ForceJIT, a null base; the callee frame must fit in the current arena chunk (else EA's path takes another chunk,
// whose stale bytes the 8-byte return copy would carry): the result slot gets the low dword computed here and the high
// dword EA's return copies from the callee's eval slot 0 (top + eval offset + 4), so the caller's eval stack is bit-equal
// to EA's call.
static int g_jitInline;  // [MonoJit] Inline=1|2
static bool JitInlineNo() { ts3native_counters.jitInlineNo++; return false; }
static unsigned short JitOrigWord(const unsigned short* p) {
    if (*p != JIT_OP) return *p;
    const JitEnt* en = JitFind((DWORD)(uintptr_t)p);
    return en ? (unsigned short)en->orig : 0xFFFF;
}
static const u8* JitInlineCallee(const unsigned short* ip, DWORD op, const u8* callerRm) {
    const DWORD* items = *(const DWORD* const*)(callerRm + 0x14);
    const u8* c = (const u8*)(uintptr_t)items[op == 0xEA ? ip[1] : op == 0xEB ? ip[2] : ip[3]];
    if (!c) return NULL;
    const DWORD fl = *(const DWORD*)(c + 0x44);
    const u8* mm = *(const u8* const*)(c + 8);
    if (!(fl & 0x200) || !(fl & 0x100) || *(const DWORD*)(c + 0x24) || c[0x41] || !mm) return NULL;  // instance, transformed, no native body, no clauses
    if (op == 0xEC && ((fl & 0x400) || (*(const unsigned short*)mm & 0x40))) return NULL;          // callvirt: non-virtual, reference type
    if ((fl & 0x400) && g_jitInline < 2) return NULL;                                              // a value-type this: tier 2
    if (!JitFind((DWORD)(uintptr_t)*(const unsigned short* const*)c)) return NULL;                 // compiled: the IL hash gate holds for the bytes inlined
    return c;
}
static bool JitTryInline(Em& e, const unsigned short* ip, DWORD op, const u8* callerRm) {
    const u8* c = JitInlineCallee(ip, op, callerRm);
    if (!c) return false;
    const unsigned short* code = *(const unsigned short* const*)c;
    const int words = *(const unsigned short*)(c + 0x3A), n = c[0x40];
    const DWORD* argOff = *(const DWORD* const*)(c + 0x1C);
    const bool value = c[0x44] != 1, vtm = (*(const DWORD*)(c + 0x44) & 0x400) != 0, tier2 = g_jitInline >= 2;
    // decode into base ops (superops expanded), original words where the callee is compiled
    unsigned short ops[24];
    const unsigned short* at[24];
    int no = 0;
    for (int i = 0; i < words;) {
        unsigned short w = JitOrigWord(code + i);
        if (w == 0xE8 || w > 0x1C0 || w == 0x1A9 || w == 0x1AA) return JitInlineNo();
        unsigned short tmp[2] = {w, 0};
        int len = w <= 0x1A8 ? g_jitOpLen[w] : JitLen(tmp);
        if (len <= 0) return JitInlineNo();
        if (w >= 0x1AB) {
            const u8* d = g_jitSuper + 0x48 * (w - 0x1AA);
            int m = *(const unsigned short*)(d + 4), p = i;
            for (int k = 0; k < m && no < 24; k++) {
                unsigned short b = ((const unsigned short*)(d + 6))[k];
                ops[no] = b, at[no++] = code + p;
                p += g_jitOpLen[b];
            }
        } else if (no < 24)
            ops[no] = w, at[no++] = code + i;
        else
            return JitInlineNo();
        i += len;
    }
    // emit into a scratch buffer first (no partial code on refusal)
    static u8 buf[512];
    Em b = {buf};
    u8* slow[20];
    int ns = 0;
    const int thisOff = -(n + 1) * 8;  // receiver slot relative to esi
    b.op(0x8B, EAX, ESI, thisOff);                                     // mov eax, this
    b.rr(0x85, EAX, EAX); slow[ns++] = b.jcc(0x84);                   // null: EA's path throws
    b.b(0x80); b.b(0x3D); b.d((DWORD)(uintptr_t)g_jitForceJit); b.b(0x75); slow[ns++] = b.jcc(0x85);  // MonoPatcher ForceJIT active
    if (!vtm) {  // EA's remoting check reads this->vtable->klass
        b.op(0x8B, ECX, EAX, 0); b.rr(0x85, ECX, ECX); slow[ns++] = b.jcc(0x84);
        b.op(0x8B, ECX, ECX, 0); b.b(0x3B); b.b(0x0D); b.d((DWORD)(uintptr_t)g_jitProxyClassP); slow[ns++] = b.jcc(0x84);
    }
    const unsigned short w38 = *(const unsigned short*)(c + 0x38), w3E = *(const unsigned short*)(c + 0x3E);
    if (value) {  // the frame must fit in this chunk; the high dword is read after the body
        b.op(0x8B, ECX, EBP, 8); b.op(0x8B, ECX, ECX, 0x13C);          // ecx = state->chunk
        b.rr(0x85, ECX, ECX); slow[ns++] = b.jcc(0x84);
        b.op(0x8B, EDX, ECX, 0); b.b(0x81); b.b(0xC2); b.d(w38);         // edx = top + frame size
        b.op(0x3B, EDX, ECX, 4); slow[ns++] = b.jcc(0x87);             // > end: another chunk
    }
    if (g_jcOn) { b.b(0x68); b.d((DWORD)(uintptr_t)c); b.b(0xFF); b.b(0x15); b.d((DWORD)(uintptr_t)&g_jcInlineStubP); }  // capture: push rm; call [stub], every register kept
    int sp = 0;              // compile-time stack depth; entries: ECX, EDX
    bool isThis[3] = {};     // entry holds `this`
    const int regs[2] = {ECX, EDX};
    int k = 0, ret = -1;
    for (; k < no && ops[k] >= 0 && g_opInfo[ops[k]].kind == K_STINARG; k++) {}
    if (no - k > 12) return JitInlineNo();
    for (; k < no; k++) {
        const JitOpInfo t = g_opInfo[ops[k]];
        const int w = t.width, u16 = at[k][1];
        int r = sp < 2 ? regs[sp] : -1, r0 = sp >= 2 ? regs[sp - 2] : -1, r1 = sp >= 1 ? regs[sp - 1] : -1;
        switch (t.kind) {
        case K_LDTHIS: if (r < 0) return JitInlineNo(); b.rr(0x8B, EAX, r); isThis[sp++] = true; break;  // mov r, eax
        case K_LDARG: {
            int idx = -1;
            for (int a = 0; a < n; a++) if ((int)argOff[a] == u16) idx = a;
            if (r < 0 || idx < 0 || w == 6) return JitInlineNo();
            b.load(w, r, ESI, -(n - idx) * 8); isThis[sp++] = false; break;
        }
        case K_LDFLD:
            if (sp < 1 || w >= 6 || (!isThis[sp - 1] && !tier2)) return JitInlineNo();  // 6, 7: the 8-byte and x87 forms
            if (isThis[sp - 1]) b.load(w, r1, EAX, u16);
            else { b.rr(0x85, r1, r1); slow[ns++] = b.jcc(0x84); b.load(w, r1, r1, u16); }  // a null: EA's path, nothing written yet
            isThis[sp - 1] = false; break;
        case K_LDIND:  // EA does not check; a null leaves the fault to EA's path
            if (sp < 1 || w >= 6 || !tier2) return JitInlineNo();
            if (isThis[sp - 1]) b.load(w, r1, EAX, 0);
            else { b.rr(0x85, r1, r1); slow[ns++] = b.jcc(0x84); b.load(w, r1, r1, 0); }
            isThis[sp - 1] = false; break;
        case K_LDLEN:  // array + 8; a null: EA's path throws
            if (sp < 1 || isThis[sp - 1] || !tier2) return JitInlineNo();
            b.rr(0x85, r1, r1); slow[ns++] = b.jcc(0x84); b.load(5, r1, r1, 8); break;
        case K_LDC: if (r < 0) return JitInlineNo(); b.movRI(r, (DWORD)t.x); isThis[sp++] = false; break;
        case K_LDCS: if (r < 0) return JitInlineNo(); b.movRI(r, (DWORD)(int)(short)u16); isThis[sp++] = false; break;
        case K_LDCI4: if (r < 0) return JitInlineNo(); b.movRI(r, *(const DWORD*)(at[k] + 1)); isThis[sp++] = false; break;
        case K_BIN: if (sp < 2) return JitInlineNo(); b.rr(t.x, r0, r1); sp--; isThis[sp - 1] = false; break;
        case K_MUL: if (sp < 2) return JitInlineNo(); b.b(0x0F); b.b(0xAF); b.b(0xC0 | (r0 << 3) | r1); sp--; isThis[sp - 1] = false; break;
        case K_CMP:
            if (sp < 2) return JitInlineNo();
            b.rr(0x39, r0, r1); b.b(0x0F); b.b(t.x); b.b(0xC0 | r0); b.b(0x0F); b.b(0xB6); b.b(0xC0 | (r0 << 3) | r0);
            sp--; isThis[sp - 1] = false; break;
        case K_CEQ0:
            if (sp < 1) return JitInlineNo();
            b.rr(0x85, r1, r1); b.b(0x0F); b.b(0x94); b.b(0xC0 | r1); b.b(0x0F); b.b(0xB6); b.b(0xC0 | (r1 << 3) | r1);
            isThis[sp - 1] = false; break;
        case K_INC: case K_DEC: if (sp < 1) return JitInlineNo(); b.b(0x83); b.b((t.kind == K_INC ? 0xC0 : 0xE8) | r1); b.b(1); isThis[sp - 1] = false; break;
        case K_UN: if (sp < 1) return JitInlineNo(); b.b(0xF7); b.b(0xC0 | (t.x << 3) | r1); isThis[sp - 1] = false; break;
        case K_CONV:
            if (sp < 1 || t.x == 1 || t.x == 2) return JitInlineNo();
            if (t.x >= 4) { static const u8 kx[4] = {0xBE, 0xB6, 0xBF, 0xB7}; b.b(0x0F); b.b(kx[t.x - 4]); b.b(0xC0 | (r1 << 3) | r1); }
            isThis[sp - 1] = false; break;
        case K_POP: if (sp < 1) return JitInlineNo(); sp--; break;
        case K_RET: ret = k; break;
        default: return JitInlineNo();
        }
        if (ret >= 0) break;
    }
    const char* rn = ret >= 0 ? g_jitOpNames[ops[ret]] : "";
    if (ret != no - 1 || (value ? strcmp(rn, "ret") || sp != 1 : strcmp(rn, "ret.void") || sp != 0)) return JitInlineNo();
    if (value) {  // no test from here on. High dword of EA's return copy: callee eval slot 0; then the low dword
        b.op(0x8B, EAX, EBP, 8); b.op(0x8B, EAX, EAX, 0x13C); b.op(0x8B, EAX, EAX, 0); b.op(0x8B, EAX, EAX, w3E + 4);
        b.op(0x89, EAX, ESI, thisOff + 4);
        b.op(0x89, regs[0], ESI, thisOff);
    }
    const int dsp = thisOff + (value ? 8 : 0);
    if (dsp) { b.b(0x81); b.b(dsp > 0 ? 0xC6 : 0xEE); b.d((DWORD)(dsp > 0 ? dsp : -dsp)); }
    // copy into the method's code: inline body, jmp over the out-of-line call (the normal call path from the same state)
    const int len = (int)(b.p - buf);
    u8* base = e.p;
    memcpy(e.p, buf, len);  // no relative operand inside except the slow-path jccs, fixed below
    e.p += len;
    u8* skip = e.jmp();
    for (int s = 0; s < ns; s++) Fix(slow[s] - buf + base, e.p);
    EmitExit(e, ip, op, false);
    Fix(skip, e.p);
    ts3native_counters.jitInlined++;
    return true;
}

// a position whose straight run ends in throw is cold: its exit writes the frame, as a null or bound exit does, on the way to an
// exception, so such a K_EXIT does not hold a lazy body back (docs/537 §8). No clauses here, so nothing resumes in the body.
static bool JitFlowName(const char* s) {  // ends a straight run: ret*, branches, switch, leave, endfinally
    return !strncmp(s, "ret", 3) || (s[0] == 'b' && strncmp(s, "box", 3)) || !strcmp(s, "switch") || !strncmp(s, "leave", 5) || !strncmp(s, "end", 3);
}
static void JitMarkCold(int n, const unsigned short* posOp, bool* cold) {
    bool c = false;
    for (int k = n - 1; k >= 0; k--) {
        const char* s = posOp[k] < 0x1A9 ? g_jitOpNames[posOp[k]] : NULL;
        c = s && (!strcmp(s, "throw") || (c && !JitFlowName(s)));
        cold[k] = c;
    }
}
// before a listed method is compiled: EA's transform for its listed direct callees that are not transformed yet (a lazy
// site bakes in the callee's frame sizes, and a callee compiled first has its lazy body when the caller is emitted). Only
// listed callees: nothing else gets a lazy body. Transform builds code only; a failure returns its exception and is left
// for EA's own transform at the first call. A virtual callee is left to its guard site, except a con.call's: that target
// is static, and an interface implementation such as UInt64.Equals(ulong) is "virtual final" in metadata.
static void JitPreTransform(u8* rm, void* ctx) {
    const unsigned short* code = *(const unsigned short* const*)rm;
    const int words = *(const unsigned short*)(rm + 0x3A);
    const DWORD* items = *(const DWORD* const*)(rm + 0x14);
    for (int i = 0; i < words;) {
        const unsigned short w = code[i];
        if (w == 0xE8) { i += JitSwitchLen(code + i); continue; }
        int len = JitLen(code + i);
        if (len <= 0) return;
        if (w >= 0xEA && w <= 0xED) {
            BYTE* c = (BYTE*)(uintptr_t)items[w == 0xEA ? code[i + 1] : w == 0xEB ? code[i + 2] : w == 0xEC ? code[i + 3] : code[i + 4]];
            const u8* mm = c ? *(const u8* const*)(c + 8) : NULL;
            if (c && mm && !(*(const DWORD*)(c + 0x44) & 0x100) && (w == 0xEB || !(*(const unsigned short*)mm & 0x40))) {
                char name[256];
                JitMethodName(c, name, sizeof(name));
                if (JitListed(name)) IlpXform(c, ctx);
            }
        }
        if (w == 0xF4 && g_jitNewVt && g_jitLazy) {  // B2b: a value-type ctor's site bakes in its frame sizes too
            BYTE* c = (BYTE*)(uintptr_t)items[code[i + 1]];
            if (JitNewVtClass(c) && !(*(const DWORD*)(c + 0x44) & 0x100)) {
                char name[256];
                JitMethodName(c, name, sizeof(name));
                if (JitListed(name)) IlpXform(c, ctx);
            }
        }
        i += len;
    }
}

// the word a compiled position gets: JIT_OP, except leave.s. EA's leave / leave.s handler 0xE59315 re-reads [edi] to pick
// the operand size (0xE5933A cmp word ptr [edi], 0x9C): with JIT_OP there it reads leave.s's 16-bit offset plus the next
// word as a 32-bit offset and jumps to a wild ip. leave.s always exits to that handler, so its own word can stay.
static unsigned short JitWord(unsigned short orig) { return orig == 0x9C ? orig : (unsigned short)JIT_OP; }
// compile one method: returns why not (and leaves the code alone) if anything does not fit, else NULL
static const char* JitCompile(u8* rm) {
    if (g_jitMethN >= JIT_METH_N) return "method table full";
    if (g_jcn) g_jcn->meth = g_jitMethN;  // count mode: the index this method gets if it compiles
    if (rm[0x41] && !g_jitWide) return "exception clauses";
    unsigned short* code = *(unsigned short**)rm;
    int words = *(unsigned short*)(rm + 0x3A);
    if (words >= JIT_MAX_WORDS) return "too many mint words";
    // 1. positions: every instruction start, superops expanded into their base instructions
    int* const posW = g_js->posW;
    unsigned short* const posOp = g_js->posOp;
    bool* const posReal = g_js->posReal;
    int n = 0;
    for (int i = 0; i < words;) {
        unsigned op = code[i];
        if (n >= JIT_MAX_POS) return "too many positions";
        if (op == 0xE8) {  // switch: compiled as an exit (variable length)
            posW[n] = i, posOp[n] = 0xE8, posReal[n++] = true;
            i += JitSwitchLen(code + i);
            continue;
        }
        int len = JitLen(code + i);
        if (len <= 0) return "cannot walk the mint code";
        if (op >= 0x1AB) {
            const u8* d = g_jitSuper + 0x48 * (op - 0x1AA);
            int m = *(const unsigned short*)(d + 4), at = i;
            for (int k = 0; k < m; k++) {
                unsigned short b = ((const unsigned short*)(d + 6))[k];
                if (k && code[at] != b) return "a superop's inner word differs";
                posW[n] = at, posOp[n] = b, posReal[n++] = k == 0;
                at += g_jitOpLen[b];
            }
        } else
            posW[n] = i, posOp[n] = (unsigned short)op, posReal[n++] = true;
        i += len;
    }
    // 2. emit. With lazy frames, a method whose every position is native, ret / ret.void or a call this compiler makes lazily
    // (a target with no lazy body at run time means JitLazyMat, then EA's call; DirectLeaf: no call at all) first gets its
    // lazy body, then the normal one; label[] and fix[] end as the normal body's. A leaf or chain body with exit-free templates
    // can still exit (a null, a bound): that exit writes the frame.
    bool lazyOk = g_jitLazy && !rm[0x41];
    bool* const posCold = g_js->posCold;
    if (lazyOk) JitMarkCold(n, posOp, posCold);
    for (int k = 0; k < n && lazyOk; k++) {
        const JitOpInfo t = posOp[k] < 0x1C1 ? g_opInfo[posOp[k]] : JitOpInfo{K_EXIT, 0, 0};
        const u8* lc; bool lg;
        if (!g_jitLeafOnly && t.kind == K_EXIT && ((posOp[k] >= 0xEA && posOp[k] <= 0xED) || posOp[k] == 0xF4) && posReal[k] && JitLazySite(code + posW[k], posOp[k], rm, &lc, &lg)) continue;
        // CallExit: a hot exit (and ret.vt) writes the frame first and then is EA's, as a cold one: the body stays lazy. DirectLeaf keeps
        // its meaning (only leaves get a lazy body): a call site still drops it there (the host has no DirectLeaf).
        if ((g_jitPureIcalls || g_jitFastCopy) && (posOp[k] == 0xEA || posOp[k] == 0xEC) && posReal[k] && JitPureIcall(code + posW[k], posOp[k], rm)) continue;  // native, not a call
        const bool ce = g_jitCallExit && !(g_jitLeafOnly && posOp[k] >= 0xEA && posOp[k] <= 0xED);
        if ((t.kind == K_EXIT && !posCold[k] && !ce) || (t.kind == K_RET && posOp[k] != 0x06 && posOp[k] != 0x07 && !ce)) lazyOk = false;
    }
    // bound: 256 bytes per position, as the host's pool check (a lazy site in both bodies: 768); a switch's table is 2 bytes
    // per mint word it takes
    const size_t need = ((g_jitLazy ? 768 : 256) + (g_jcn ? 256 : 0)) * (size_t)n + 2 * (size_t)words + 512;  // count mode: an inc per position and exit, bail counters
    if ((size_t)(g_jitCodeEnd - (g_jitCode + g_jitCodeUsed)) < need) return "code region full";
    u8* const start = g_jitCode + g_jitCodeUsed;
    JitProt(start, need, PAGE_READWRITE);  // from the end of the last method on: no finished code lives here (its last page flips below)
    u8** const label = g_js->label;
    JitFix* const fix = g_js->fix;
    int* const wordToPos = g_js->wordToPos;
    for (int k = 0; k < words; k++) wordToPos[k] = -1;
    for (int k = 0; k < n; k++) wordToPos[posW[k]] = k;
    int nf = 0, nat = 0, ex = 0, pure = 0;  // pure: CallExit's inline Array icall sites (normal body)
    Em e = {start};
    const char* why = NULL;
    u8* lazyEntry = NULL;
    const DWORD* argOff = *(const DWORD* const*)(rm + 0x1C);
    const DWORD* items = *(const DWORD* const*)(rm + 0x14);
    for (int pass = lazyOk ? 0 : 1; pass < 2 && !why; pass++) {
    g_jitLazyPass = pass == 0;
    nf = 0, nat = 0, ex = 0, pure = 0;
    if (g_jitLazyPass) {
        lazyEntry = e.p;
        if (g_jcn) e.incAbs(&g_jcn->calls[g_jitMethN]);  // count mode: a lazy entry is a call (JitEnter never sees it)
        if (g_jcOn) { e.b(0xE8); e.d((DWORD)(uintptr_t)JcLazyStub - ((DWORD)(uintptr_t)e.p + 4)); }  // capture: sample the lazy call, every register kept
    }
    for (int k = 0; k < n; k++) {
        label[k] = e.p;
        if (g_jcn) {  // count mode: positions run; entering the normal body at word 0 is a call
            // shortcut: a branch back to word 0 counts as a call too (rare: M.verifyOk names those methods), fine for a census
            if (!k && !g_jitLazyPass) e.incAbs(&g_jcn->calls[g_jitMethN]);
            e.incAbs(&g_jcn->posRun[g_jitMethN]);
        }
        const unsigned short* ip = code + posW[k];
        DWORD op = posOp[k];
        JitOpInfo t = op < 0x1C1 ? g_opInfo[op] : JitOpInfo{K_EXIT, 0, 0};
        int w = t.width, o16 = (short)ip[1];
        unsigned u16 = ip[1];
        u8* exitRel[3] = {NULL, NULL, NULL};
        // round 10 C2: the static-field and stobj kinds resolve their item now (field, vtable, width); anything they
        // cannot carry as a constant (no domain yet, a thread static, a value type, an unknown type) compiles as an exit
        DWORD sVt = 0; int sOff = 0;
        if (t.kind == K_LDSFLD || t.kind == K_LDSFLDA || t.kind == K_STSFLD || t.kind == K_STOBJ || t.kind == K_LDSFLDVT) {
            const u8* item = (const u8*)(uintptr_t)items[u16];
            if (t.kind == K_STOBJ) w = item ? JitTypeWidth(item + 0xA0) : 0;  // MonoClass+0xA0 = byval_arg (docs/213)
            else if (item) {
                if (w == 8) w = JitTypeWidth(*(const u8* const*)item);
                if (t.kind == K_LDSFLDA && g_jitLdsflda) w = 5;  // an address of any field type (the row's 0 = always an exit)
                sOff = *(const int*)(item + 8);
                const bool fast = op == 0x067 || op == 0x068;  // ldsfld.i4 / ldsfld.o carry the vtable and skip the special-static lookup
                sVt = op == 0x066 || fast ? items[ip[2]] : (DWORD)(uintptr_t)JitStaticVt(item);
                if (!sVt || sOff < 0 || (!fast && JitSpecialStatic(item))) w = 0;
            } else w = 0;
            if (!w) t.kind = K_EXIT;
        }
        switch (t.kind) {
        case K_LDARG:  // [esi] = arena[u16] (i8: two words)
            e.load(w == 6 ? 5 : w, EAX, EBX, u16); e.op(0x89, EAX, ESI, 0);
            if (w == 6) { e.op(0x8B, ECX, EBX, u16 + 4); e.op(0x89, ECX, ESI, 4); }
            e.addEsi(8); break;
        case K_LDTHIS:  // MonoInvocation +0xC; lazy body: the call site's block
            if (g_jitLazyPass) e.opEsp(0x8B, EAX, 4 + B_THIS); else { e.op(0x8B, EAX, EBP, -8); e.op(0x8B, EAX, EAX, 0xC); }
            e.op(0x89, EAX, ESI, 0); e.addEsi(8); break;
        case K_LDARGA: e.op(0x8D, EAX, EBX, u16); e.op(0x89, EAX, ESI, 0); e.addEsi(8); break;
        case K_LDLOC:
            e.load(w == 6 ? 5 : w, EAX, EDI, u16); e.op(0x89, EAX, ESI, 0);
            if (w == 6) { e.op(0x8B, ECX, EDI, u16 + 4); e.op(0x89, ECX, ESI, 4); }
            e.addEsi(8); break;
        case K_LDLOCA: e.op(0x8D, EAX, EDI, u16); e.op(0x89, EAX, ESI, 0); e.addEsi(8); break;
        case K_STLOC: case K_STLOCNP:
            e.op(0x8B, EAX, ESI, -8);
            if (w == 6) { e.op(0x8B, ECX, ESI, -4); e.op(0x89, ECX, EDI, u16 + 4); }
            e.store(w, EAX, EDI, u16);
            if (t.kind == K_STLOC) e.addEsi(-8);
            break;
        case K_STINARG: {  // arena[argOff[i]] = stackargs[i] (offset constant per method)
            int off = (int)argOff[u16];
            if (g_jitLazyPass) e.opEsp(0x8B, ECX, 4 + B_ARGS); else { e.op(0x8B, ECX, EBP, -8); e.op(0x8B, ECX, ECX, 0x14); }
            e.op(0x8B, EAX, ECX, u16 * 8); e.store(w, EAX, EBX, off);
            if (w == 6) { e.op(0x8B, EDX, ECX, u16 * 8 + 4); e.op(0x89, EDX, EBX, off + 4); }
            break;
        }
        case K_LDFLD:
            e.op(0x8B, EAX, ESI, -8); e.rr(0x85, EAX, EAX); exitRel[0] = e.jcc(0x84);
            if (w == 7) { e.fld4(EAX, u16); e.fstp8(ESI, -8); break; }
            if (w == 6) { e.op(0x8B, ECX, EAX, u16 + 4); e.op(0x89, ECX, ESI, -4); }
            e.load(w == 6 ? 5 : w, EAX, EAX, u16); e.op(0x89, EAX, ESI, -8); break;
        case K_STFLD:
            e.op(0x8B, EAX, ESI, -16); e.rr(0x85, EAX, EAX); exitRel[0] = e.jcc(0x84);
            e.satb(w, EAX, u16);
            if (w == 7) { e.fld8(ESI, -8); e.fstp4(EAX, u16); e.addEsi(-16); break; }
            e.op(0x8B, ECX, ESI, -8); e.store(w, ECX, EAX, u16);
            if (w == 6) { e.op(0x8B, EDX, ESI, -4); e.op(0x89, EDX, EAX, u16 + 4); }
            e.addEsi(-16); break;
        case K_LDFLDA:
            e.op(0x8B, EAX, ESI, -8); e.rr(0x85, EAX, EAX); exitRel[0] = e.jcc(0x84);
            e.op(0x8D, EAX, EAX, u16); e.op(0x89, EAX, ESI, -8); break;
        case K_LDSFLD:  // value at [vt+0xC] (the class's static data) + the field offset
            exitRel[0] = e.classInitExit(sVt);
            e.b(0xA1); e.d(sVt + 0xC);  // mov eax, [vt+0xC]
            if (w == 7) { e.fld4(EAX, sOff); e.fstp8(ESI, 0); }
            else {
                if (w == 6) { e.op(0x8B, ECX, EAX, sOff + 4); e.op(0x89, ECX, ESI, 4); }
                e.load(w == 6 ? 5 : w, EAX, EAX, sOff); e.op(0x89, EAX, ESI, 0);
            }
            e.addEsi(8); break;
        case K_LDSFLDA:
            exitRel[0] = e.classInitExit(sVt);
            e.b(0xA1); e.d(sVt + 0xC); e.op(0x8D, EAX, EAX, sOff); e.op(0x89, EAX, ESI, 0); e.addEsi(8); break;
        case K_STSFLD:
            exitRel[0] = e.classInitExit(sVt);
            e.b(0xA1); e.d(sVt + 0xC); e.satb(w, EAX, sOff);
            if (w == 7) { e.fld8(ESI, -8); e.fstp4(EAX, sOff); }
            else {
                e.op(0x8B, ECX, ESI, -8); e.store(w, ECX, EAX, sOff);
                if (w == 6) { e.op(0x8B, ECX, ESI, -4); e.op(0x89, ECX, EAX, sOff + 4); }
            }
            e.addEsi(-8); break;
        case K_STOBJ:  // address [esi-16], value [esi-8]; EA has no null check (the handler faults), a null exits so the fault stays EA's
            e.op(0x8B, EAX, ESI, -16); e.rr(0x85, EAX, EAX); exitRel[0] = e.jcc(0x84);
            e.satb(w, EAX, 0);
            if (w == 7) { e.fld8(ESI, -8); e.fstp4(EAX, 0); }
            else {
                e.op(0x8B, ECX, ESI, -8); e.store(w, ECX, EAX, 0);
                if (w == 6) { e.op(0x8B, ECX, ESI, -4); e.op(0x89, ECX, EAX, 4); }
            }
            e.addEsi(-16); break;
        case K_STELEM: case K_STELEMREF: {  // array [esi-24], index [esi-16], value [esi-8]; null and bounds exit before any change
            int sz = w <= 2 ? 1 : w <= 4 ? 2 : w == 5 || w == 7 ? 4 : 8, sc = sz == 1 ? 0 : sz == 2 ? 1 : sz == 4 ? 2 : 3;
            e.op(0x8B, EAX, ESI, -24); e.rr(0x85, EAX, EAX); exitRel[0] = e.jcc(0x84);
            e.op(0x8B, ECX, ESI, -16); e.op(0x3B, ECX, EAX, 8); exitRel[1] = e.jcc(0x83);
            if (t.kind == K_STELEMREF) {  // a null, a value of exactly the element class (array klass+8), or any value into an
                u8* ok[2];                // object[] stores; the rest (EA: mono_object_isinst, ArrayTypeMismatchException) exits
                e.op(0x8B, EDX, ESI, -8); e.rr(0x85, EDX, EDX); ok[0] = e.jcc(0x84);
                e.op(0x8B, EDX, EDX, 0); e.op(0x8B, EDX, EDX, 0);  // the value's class
                e.op(0x8B, ECX, EAX, 0); e.op(0x8B, ECX, ECX, 0);  // the array's class
                e.op(0x3B, EDX, ECX, 8); ok[1] = e.jcc(0x84);
                e.op(0x81, 7, ECX, 8); e.d((DWORD)(uintptr_t)g_jitObjectClass); exitRel[2] = e.jcc(0x85);  // cmp [klass+8], object_class
                Fix(ok[0], e.p); Fix(ok[1], e.p);
                e.op(0x8B, ECX, ESI, -16);
            }
            e.b(0x8D); e.b(0x84); e.b((sc << 6) | (ECX << 3) | EAX); e.d(0x10);  // lea eax, [eax + ecx*sz + 0x10]
            e.satb(w, EAX, 0);
            if (w == 7) { e.fld8(ESI, -8); e.fstp4(EAX, 0); }
            else {
                e.op(0x8B, EDX, ESI, -8); e.store(w, EDX, EAX, 0);
                if (w == 6) { e.op(0x8B, EDX, ESI, -4); e.op(0x89, EDX, EAX, 4); }
            }
            e.addEsi(-24); break;
        }
        case K_INITOBJ: {  // initobj (0xE589F7): memset(address, 0, size32 in words 1-2); a null exits, the fault stays EA's
            const DWORD sz = *(const DWORD*)(ip + 1);
            e.op(0x8B, ECX, ESI, -8); e.rr(0x85, ECX, ECX); exitRel[0] = e.jcc(0x84);
            if (sz) e.satb(0, ECX, 0, sz);
            if (!(sz & 3) && sz <= 16) for (DWORD a = 0; a < sz; a += 4) { e.b(0xC7); e.m(0, ECX, (int)a); e.d(0); }
            else { e.b(0x68); e.d(sz); e.b(0x6A); e.b(0); e.b(0x51); e.b(0xE8); e.d(g_jitMemset - ((DWORD)(uintptr_t)e.p + 4)); e.b(0x83); e.b(0xC4); e.b(12); }
            e.addEsi(-8); break;
        }
        case K_FARITH: e.fld8(ESI, -16); e.op(0xDC, t.x, ESI, -8); e.fstp8(ESI, -16); e.addEsi(-8); break;  // docs/221: x87, EA's precision and rounding
        case K_FNEG: e.fld8(ESI, -8); e.b(0xD9); e.b(0xE0); e.fstp8(ESI, -8); break;
        case K_FILD: e.op(t.x ? 0xDF : 0xDB, t.x ? 5 : 0, ESI, -8); e.fstp8(ESI, -8); break;  // conv.r4 and conv.r8 of an int: no narrowing, as EA
        case K_FR4R8: e.fld8(ESI, -8); e.fstp4(EBP, -0x1C); e.fld4(EBP, -0x1C); e.fstp8(ESI, -8); break;
        case K_CVT:  // i1, i2, i4 only with EA's own SSE2 flag set: else EA calls _ftol2_sse
            if (t.x < 3 && !*g_jitSse2) { ts3native_counters.jitWideNo++; EmitExit(e, ip, op, false); ex++; continue; }
            e.b(0xF2); e.op2(0x2C, EAX, ESI, -8);
            if (t.x) { e.b(0x0F); e.b(t.x == 1 ? 0xBE : t.x == 2 ? 0xBF : t.x == 3 ? 0xB6 : 0xB7); e.b(0xC0); }
            e.op(0x89, EAX, ESI, -8); break;
        case K_FISTT:  // fisttp for EA's control-word chop and _ftol2 (docs/221 §5: 600 M inputs on a real CPU)
            if (!g_jitSse3) { ts3native_counters.jitWideNo++; EmitExit(e, ip, op, false); ex++; continue; }
            e.fld8(ESI, -8);
            if (t.x) e.op(0xDD, 1, ESI, -8);
            else { e.op(0xDD, 1, EBP, -0x20); e.op(0x8B, EAX, EBP, -0x20); e.op(0x89, EAX, ESI, -8); }
            break;
        case K_LDINDFP:  // a null exits: the fault stays in EA's handler
            e.op(0x8B, EAX, ESI, -8); e.rr(0x85, EAX, EAX); exitRel[0] = e.jcc(0x84);
            if (w == 7) e.fld4(EAX, 0); else e.fld8(EAX, 0);
            e.fstp8(ESI, -8); break;
        case K_STINDFP:
            e.op(0x8B, ECX, ESI, -16); e.rr(0x85, ECX, ECX); exitRel[0] = e.jcc(0x84);
            e.satb(w, ECX, 0);
            e.fld8(ESI, -8);
            if (w == 7) e.fstp4(ECX, 0); else e.fstp8(ECX, 0);
            e.addEsi(-16); break;
        case K_LDSTR: e.b(0xA1); e.d((DWORD)(uintptr_t)&items[u16]); e.op(0x89, EAX, ESI, 0); e.addEsi(8); break;  // 0xE56135
        case K_LDTOKEN:  // 0xE57A11: the token goes to the vt stack, the slot takes its address
            VtLoad(e, EAX); e.op(0x89, EAX, ESI, 0);
            e.b(0x8B); e.b(0x0D); e.d((DWORD)(uintptr_t)&items[u16]); e.op(0x89, ECX, EAX, 0);
            VtAdd(e, 8); e.addEsi(8); break;
        case K_CASTCLASS:  // 0xE56449: null passes; a failed test exits, EA's handler throws
            if (!EmitIsinst(e, (const u8*)(uintptr_t)items[u16], exitRel)) { ts3native_counters.jitWideNo++; EmitExit(e, ip, op, false); ex++; continue; }
            break;
        case K_ISINST:
            if (!EmitIsinst(e, (const u8*)(uintptr_t)items[u16])) { EmitExit(e, ip, op, false); ex++; continue; }
            break;
        case K_SHRUN8:  // shr.un.i8 (0xE55DE6): edx:eax >> cl through the CRT's _aullshr 0xF7EEE0, as EA's handler calls it
            e.op(0x8B, EAX, ESI, -16); e.op(0x8B, EDX, ESI, -12); e.op(0x8B, ECX, ESI, -8);
            e.b(0xE8); e.d(g_jitAullshr - ((DWORD)(uintptr_t)e.p + 4));
            e.op(0x89, EAX, ESI, -16); e.op(0x89, EDX, ESI, -12); e.addEsi(-8); break;
        case K_LDCFP:  // ldc.r4 (0xE546EC) widens through x87, ldc.r8 (0xE54708) copies the two words
            if (w == 7) { e.b(0xD9); e.b(0x05); e.d((DWORD)(uintptr_t)(ip + 1)); e.fstp8(ESI, 0); }
            else { e.b(0xC7); e.m(0, ESI, 0); e.d(*(const DWORD*)(ip + 1)); e.b(0xC7); e.m(0, ESI, 4); e.d(*(const DWORD*)(ip + 3)); }
            e.addEsi(8); break;
        case K_LDFP: if (w == 7) e.fld4(t.x ? EDI : EBX, u16); else e.fld8(t.x ? EDI : EBX, u16); e.fstp8(ESI, 0); e.addEsi(8); break;
        case K_STFP: e.fld8(ESI, -8); EmitFpStore(e, w, EDI, u16); e.addEsi(-8); break;
        case K_STINARGFP: e.op(0x8B, ECX, EBP, -8); e.op(0x8B, ECX, ECX, 0x14); e.fld8(ECX, u16 * 8); EmitFpStore(e, w, EBX, (int)argOff[u16]); break;
        case K_CKNULL: e.b(0x83); e.m(7, ESI, -8); e.b(0); exitRel[0] = e.jcc(0x84); break;  // cmp dword [esi-8], 0
        case K_STRLEN:  // EA strings: length at +4, chars at +8 (4-byte object header, docs/130 §2)
            e.op(0x8B, EAX, ESI, -8); e.rr(0x85, EAX, EAX); exitRel[0] = e.jcc(0x84);
            e.op(0x8B, EAX, EAX, 4); e.op(0x89, EAX, ESI, -8); break;
        case K_GETCHR:  // string [esi-16], index [esi-8]; one unsigned compare refuses what EA's signed pair refuses
            e.op(0x8B, EAX, ESI, -16); e.rr(0x85, EAX, EAX); exitRel[0] = e.jcc(0x84);
            e.op(0x8B, ECX, ESI, -8); e.op(0x3B, ECX, EAX, 4); exitRel[1] = e.jcc(0x83);
            e.b(0x0F); e.b(0xB7); e.b(0x84); e.b((1 << 6) | (ECX << 3) | EAX); e.d(8);  // movzx eax, word [eax + ecx*2 + 8]
            e.op(0x89, EAX, ESI, -16); e.addEsi(-8); break;
        case K_LDC: e.b(0xC7); e.m(0, ESI, 0); e.d((DWORD)t.x); e.addEsi(8); break;
        case K_LDCS: e.b(0xC7); e.m(0, ESI, 0); e.d((DWORD)o16); e.addEsi(8); break;
        case K_LDCI4: e.b(0xC7); e.m(0, ESI, 0); e.d(*(const DWORD*)(ip + 1)); e.addEsi(8); break;
        case K_DUP: e.op(0x8B, ECX, ESI, -8); e.op(0x8B, EDX, ESI, -4); e.op(0x89, ECX, ESI, 0); e.op(0x89, EDX, ESI, 4); e.addEsi(8); break;
        case K_POP: e.addEsi(-8); break;
        case K_BRS: case K_BR: {
            int tw = posW[k] + (t.kind == K_BRS ? o16 : *(const int*)(ip + 1));
            fix[nf].rel = e.jmp(), fix[nf].abs = false, fix[nf++].targetW = tw; break;
        }
        case K_BTS: case K_BT: case K_BFS: case K_BF: {
            int tw = posW[k] + (t.kind == K_BTS || t.kind == K_BFS ? o16 : *(const int*)(ip + 1));
            e.addEsi(-8); e.b(0x83); e.m(7, ESI, 0); e.b(0);  // cmp dword [esi], 0
            fix[nf].rel = e.jcc(t.kind == K_BTS || t.kind == K_BT ? 0x85 : 0x84), fix[nf].abs = false, fix[nf++].targetW = tw; break;
        }
        case K_BCS: case K_BC: {
            int tw = posW[k] + (t.kind == K_BCS ? o16 : *(const int*)(ip + 1));
            e.op(0x8B, EAX, ESI, -16); e.addEsi(-16); e.op(0x3B, EAX, ESI, 8);
            fix[nf].rel = e.jcc(t.x), fix[nf].abs = false, fix[nf++].targetW = tw; break;
        }
        case K_LDLEN: e.op(0x8B, EAX, ESI, -8); e.rr(0x85, EAX, EAX); exitRel[0] = e.jcc(0x84); e.op(0x8B, EAX, EAX, 8); e.op(0x89, EAX, ESI, -8); break;
        case K_LDELEM: {  // null and bounds checks exit before any change; element at data + 0x10
            int sz = w <= 2 ? 1 : w <= 4 ? 2 : w == 5 || w == 7 ? 4 : 8, sc = sz == 1 ? 0 : sz == 2 ? 1 : sz == 4 ? 2 : 3;
            e.op(0x8B, EAX, ESI, -16); e.rr(0x85, EAX, EAX); exitRel[0] = e.jcc(0x84);
            e.op(0x8B, ECX, ESI, -8); e.op(0x3B, ECX, EAX, 8); exitRel[1] = e.jcc(0x83);
            // lea eax, [eax + ecx*sz + 0x10]
            e.b(0x8D); e.b(0x84); e.b((sc << 6) | (ECX << 3) | EAX); e.d(0x10);
            if (w == 7) { e.fld4(EAX, 0); e.fstp8(ESI, -16); e.addEsi(-8); break; }
            if (w == 6) { e.op(0x8B, EDX, EAX, 4); e.op(0x89, EDX, ESI, -12); }
            e.load(w == 6 ? 5 : w, EDX, EAX, 0); e.op(0x89, EDX, ESI, -16); e.addEsi(-8); break;
        }
        case K_LDIND:  // EA does not check; a null exits so the fault, if any, stays in EA's handler
            e.op(0x8B, EAX, ESI, -8); e.rr(0x85, EAX, EAX); exitRel[0] = e.jcc(0x84);
            if (w == 6) { e.op(0x8B, ECX, EAX, 4); e.op(0x89, ECX, ESI, -4); }
            e.load(w == 6 ? 5 : w, EAX, EAX, 0); e.op(0x89, EAX, ESI, -8); break;
        case K_STIND:
            e.op(0x8B, ECX, ESI, -16); e.rr(0x85, ECX, ECX); exitRel[0] = e.jcc(0x84);
            e.satb(w, ECX, 0);
            e.op(0x8B, EDX, ESI, -8); e.store(w, EDX, ECX, 0);
            if (w == 6) { e.op(0x8B, EDX, ESI, -4); e.op(0x89, EDX, ECX, 4); }
            e.addEsi(-16); break;
        case K_BIN: e.op(0x8B, ECX, ESI, -8); e.op(t.x, ECX, ESI, -16); e.addEsi(-8); break;
        case K_BIN8:
            e.op(0x8B, EAX, ESI, -8); e.op(0x8B, ECX, ESI, -4);
            e.op(t.x & 0xFF, EAX, ESI, -16); e.op(t.x >> 8, ECX, ESI, -12); e.addEsi(-8); break;
        case K_MUL: e.op(0x8B, ECX, ESI, -16); e.op2(0xAF, ECX, ESI, -8); e.op(0x89, ECX, ESI, -16); e.addEsi(-8); break;
        case K_INC: e.b(0x83); e.m(0, ESI, -8); e.b(1); break;
        case K_DEC: e.b(0x83); e.m(5, ESI, -8); e.b(1); break;
        case K_SHIFT: e.op(0x8B, ECX, ESI, -8); e.op(0xD3, t.x, ESI, -16); e.addEsi(-8); break;
        case K_UN: e.op(0xF7, t.x, ESI, -8); break;
        case K_DIVUN:  // dividend [esi-16], divisor [esi-8]; a zero divisor exits before any change (EA's handler throws there)
            e.op(0x8B, ECX, ESI, -8); e.rr(0x85, ECX, ECX); exitRel[0] = e.jcc(0x84);
            e.op(0x8B, EAX, ESI, -16); e.rr(0x31, EDX, EDX); e.b(0xF7); e.b(0xF1);  // xor edx, edx; div ecx
            e.op(0x89, t.x ? EDX : EAX, ESI, -16); e.addEsi(-8); break;
        case K_REMR8:  // x [esi-16], y [esi-8]: fld x; fld y; call the CRT _CIfmod as EA's handler at 0xE55C65 does; the result replaces x
            e.fld8(ESI, -16); e.fld8(ESI, -8); e.b(0xE8); e.d(g_jitFmod - ((DWORD)(uintptr_t)e.p + 4)); e.fstp8(ESI, -16); e.addEsi(-8); break;
        case K_SWITCH: {  // value [esi-8]; u32 n at ip+1, then n i32 word offsets from the end of the instruction (EA's 0xE558B1);
            const int cnt = *(const int*)(ip + 1);  // out of range falls through
            if (cnt < 0 || cnt > 1024 || nf + cnt > 4000) { EmitExit(e, ip, op, false); ex++; continue; }
            e.op(0x8B, EAX, ESI, -8); e.addEsi(-8);
            e.b(0x3D); e.d((DWORD)cnt);  // cmp eax, n
            u8* over = e.jcc(0x83);  // jae: next instruction
            e.b(0xFF); e.b(0x24); e.b(0x85); u8* tabRef = e.p; e.d(0);  // jmp dword [eax*4 + table]
            const u8* table = e.p; memcpy(tabRef, &table, 4);
            const int endW = posW[k] + 3 + 2 * cnt;
            for (int c = 0; c < cnt; c++) { fix[nf].rel = e.p, fix[nf].abs = true, fix[nf++].targetW = endW + ((const int*)(ip + 3))[c]; e.d(0); }
            Fix(over, e.p); break;
        }
        // round 10 C1: the value-type moves, EA's handlers ported (docs/213 §3, docs/207 §15). The vt stack is the frame's own
        // area; a slot that holds a value type holds a pointer into it; sizes round up to 8 on the cursor, not in the copy.
        case K_LDVT: {  // ldarg.vt / ldloc.vt: [esi] = cursor; copy size (words 2-3) bytes there; cursor += align8
            const DWORD sz = *(const DWORD*)(ip + 2);
            VtLoad(e, EAX); e.op(0x89, EAX, ESI, 0); e.op(0x8D, ECX, t.x ? EDI : EBX, u16);
            VtCopy(e, EAX, ECX, sz); VtAdd(e, (int)((sz + 7) & ~7u)); e.addEsi(8); break;
        }
        case K_STVT: {  // starg.vt / stloc.vt: the pointed-at bytes go to the argument / local; cursor -= align8 (EA's tail 0xE56C21)
            const DWORD sz = *(const DWORD*)(ip + 2);
            e.op(0x8B, ECX, ESI, -8); e.op(0x8D, EDX, t.x ? EDI : EBX, u16);
            VtCopy(e, EDX, ECX, sz); VtAdd(e, -(int)((sz + 7) & ~7u)); e.addEsi(-8); break;
        }
        case K_STINARGVT: {  // arena[argOff[i]] = *(stackargs[i].p), size bytes; the caller's copy stays on its vt stack
            const DWORD sz = *(const DWORD*)(ip + 2);
            if (g_jitLazyPass) e.opEsp(0x8B, ECX, 4 + B_ARGS); else { e.op(0x8B, ECX, EBP, -8); e.op(0x8B, ECX, ECX, 0x14); }
            e.op(0x8B, ECX, ECX, u16 * 8); e.op(0x8D, EDX, EBX, (int)argOff[u16]);
            VtCopy(e, EDX, ECX, sz); break;
        }
        case K_DUPVT: {  // size at words 1-2: a second copy at the cursor, [esi] points at it
            const DWORD sz = *(const DWORD*)(ip + 1);
            e.op(0x8B, ECX, ESI, -8); VtLoad(e, EAX); e.op(0x89, EAX, ESI, 0);
            VtCopy(e, EAX, ECX, sz); VtAdd(e, (int)((sz + 7) & ~7u)); e.addEsi(8); break;
        }
        case K_VTRESULT: {  // after a call that returned a value type: release the callee's vt bytes (words 2-3, aligned 8); a
            // result of w1 (word 1) bytes moves down to the new cursor (memmove through EA's import, skipped when it is there
            // already) and [esi-8] points at it; then its aligned size is kept on the cursor. EA's 0xE545B0.
            const DWORD a2 = (*(const DWORD*)(ip + 2) + 7) & ~7u, a1 = (u16 + 7) & ~7u;
            VtLoad(e, ECX); e.rr(0x8B, ECX, EAX);                                   // eax = old cursor
            if (a2) { e.b(0x81); e.b(0xE9); e.d(a2); }                               // sub ecx, a2
            VtStore(e, ECX);
            if (u16) {
                e.rr(0x3B, EAX, ECX); e.b(0x74); u8* same = e.p; e.b(0);              // cmp ecx, eax; je same
                e.b(0x68); e.d(u16); e.b(0x50 + EAX); e.b(0x50 + ECX); e.op(0x89, ECX, ESI, -8);
                e.b(0xFF); e.b(0x15); e.d((DWORD)(uintptr_t)g_jitMemmove); e.b(0x83); e.b(0xC4); e.b(12);   // call dword ptr [memmove]; add esp, 12
                *same = (u8)(e.p - (same + 1));
                VtAdd(e, (int)a1);
            }
            break;
        }
        case K_CMP:
            e.op(0x8B, EDX, ESI, -16); e.rr(0x33, EAX, EAX); e.op(0x3B, EDX, ESI, -8);
            e.b(0x0F); e.b(t.x); e.b(0xC0);  // setcc al
            e.op(0x89, EAX, ESI, -16); e.addEsi(-8); break;
        case K_LDFLDVT: {  // ldfld.vt (EA's 0xE567B3): null check; the slot becomes the cursor; memcpy(cursor, obj + off, size); cursor += align8
            const DWORD sz = *(const DWORD*)(ip + 2);
            e.op(0x8B, ECX, ESI, -8); e.rr(0x85, ECX, ECX); exitRel[0] = e.jcc(0x84);
            VtLoad(e, EAX); e.op(0x89, EAX, ESI, -8); e.op(0x8D, ECX, ECX, u16);
            VtCopy(e, EAX, ECX, sz); VtAdd(e, (int)((sz + 7) & ~7u)); break;
        }
        case K_LDSFLDVT: {  // ldsfld.vt (0xE56EAE): class-init check; [esi] = cursor; memcpy(cursor, statics + off, size); cursor += align8
            const DWORD sz = *(const DWORD*)(ip + 2);
            exitRel[0] = e.classInitExit(sVt);
            e.b(0xA1); e.d(sVt + 0xC); e.op(0x8D, ECX, EAX, sOff);
            VtLoad(e, EAX); e.op(0x89, EAX, ESI, 0); VtCopy(e, EAX, ECX, sz); VtAdd(e, (int)((sz + 7) & ~7u)); e.addEsi(8); break;
        }
        // docs/537 §20 B1 [verified handlers]: null and bounds exit before any change (EA raises in its handler); EA has no barrier,
        // ours logs heap stores for Satb. The size is words 2-3 (stfld.vt, the element ops) or the class's value size (ldobj,
        // stobj.vt: EA calls 0xE6F5A0 each time, here read once at compile time; a class with no layout yet exits for good).
        // Same emitter as monohost-jit.cpp (keep in step).
        case K_STFLDVT: {  // obj [esi-16], src [esi-8]: memcpy(obj + off, src, size); cursor -= align8
            const DWORD sz = *(const DWORD*)(ip + 2);
            e.op(0x8B, EAX, ESI, -16); e.rr(0x85, EAX, EAX); exitRel[0] = e.jcc(0x84);
            e.satb(0, EAX, u16, sz);
            e.op(0x8D, EAX, EAX, u16); e.op(0x8B, ECX, ESI, -8);
            VtCopy(e, EAX, ECX, sz); VtAdd(e, -(int)((sz + 7) & ~7u)); e.addEsi(-16); break;
        }
        case K_LDELEMVT: {  // arr [esi-16], index [esi-8]: [esi-16] = cursor; memcpy(cursor, arr + 0x10 + index * size, size); cursor += align8
            const DWORD sz = *(const DWORD*)(ip + 2);
            e.op(0x8B, EAX, ESI, -16); e.rr(0x85, EAX, EAX); exitRel[0] = e.jcc(0x84);
            e.op(0x8B, ECX, ESI, -8); e.op(0x3B, ECX, EAX, 8); exitRel[1] = e.jcc(0x83);
            e.b(0x69); e.b(0xC0 | (ECX << 3) | ECX); e.d(sz);         // imul ecx, ecx, size
            e.b(0x8D); e.b(0x8C); e.b((ECX << 3) | EAX); e.d(0x10);   // lea ecx, [eax + ecx + 0x10]
            VtLoad(e, EAX); e.op(0x89, EAX, ESI, -16);
            VtCopy(e, EAX, ECX, sz); VtAdd(e, (int)((sz + 7) & ~7u)); e.addEsi(-8); break;
        }
        case K_STELEMVT: {  // arr [esi-24], index [esi-16], src [esi-8]: memcpy(arr + 0x10 + index * size, src, size); cursor -= align8
            const DWORD sz = *(const DWORD*)(ip + 2);
            e.op(0x8B, EAX, ESI, -24); e.rr(0x85, EAX, EAX); exitRel[0] = e.jcc(0x84);
            e.op(0x8B, ECX, ESI, -16); e.op(0x3B, ECX, EAX, 8); exitRel[1] = e.jcc(0x83);
            e.b(0x69); e.b(0xC0 | (ECX << 3) | ECX); e.d(sz);         // imul ecx, ecx, size
            e.b(0x8D); e.b(0x84); e.b((ECX << 3) | EAX); e.d(0x10);   // lea eax, [eax + ecx + 0x10]
            e.satb(0, EAX, 0, sz);
            e.op(0x8B, ECX, ESI, -8);
            VtCopy(e, EAX, ECX, sz); VtAdd(e, -(int)((sz + 7) & ~7u)); e.addEsi(-24); break;
        }
        case K_LDOBJVT: case K_STOBJVT: {
            const u8* cls = (const u8*)(uintptr_t)items[u16];
            // EA's value-type test 0xE52160: VALUETYPE (0x11) and not an enum, or a GENERICINST (0x15) of a value-type container
            const bool vt = cls && (cls[0xA6] == 0x11 ? !(cls[0x14] & 0x10)
                                    : cls[0xA6] == 0x15 && (*(const DWORD*)(*(const u8* const*)(*(const u8* const*)(cls + 0xA0) + 4) + 0x14) >> 3 & 1));
            const DWORD sz = vt && (cls[0x14] & 4) ? *(const DWORD*)(cls + 0x54) - 4 : 0;
            if (!sz || sz > 0x10000) { EmitExit(e, ip, op, false); ex++; continue; }  // ldobj of a non-value type: stackval_from_data, EA's
            if (t.kind == K_LDOBJVT) {  // addr [esi-8] (EA does not check; a null exits so the fault stays EA's): [esi-8] = cursor; memcpy; cursor += align8
                e.op(0x8B, ECX, ESI, -8); e.rr(0x85, ECX, ECX); exitRel[0] = e.jcc(0x84);
                VtLoad(e, EAX); e.op(0x89, EAX, ESI, -8);
                VtCopy(e, EAX, ECX, sz); VtAdd(e, (int)((sz + 7) & ~7u)); break;
            }
            // stobj.vt: addr [esi-16], src [esi-8]: memcpy(addr, src, size); cursor -= align8
            e.op(0x8B, EAX, ESI, -16); e.rr(0x85, EAX, EAX); exitRel[0] = e.jcc(0x84);
            e.satb(0, EAX, 0, sz);
            e.op(0x8B, ECX, ESI, -8);
            VtCopy(e, EAX, ECX, sz); VtAdd(e, -(int)((sz + 7) & ~7u)); e.addEsi(-16); break;
        }
        case K_CEQ8:  // (a == b) over two 8-byte slots: xor the halves, or them, sete (EA's 0xE5803C); the low dword gets the result
            e.op(0x8B, EAX, ESI, -16); e.op(0x33, EAX, ESI, -8); e.op(0x8B, ECX, ESI, -12); e.op(0x33, ECX, ESI, -4);
            e.rr(0x0B, ECX, EAX); e.b(0x0F); e.b(0x94); e.b(0xC0); e.b(0x0F); e.b(0xB6); e.b(0xC0);  // or eax, ecx; sete al; movzx eax, al
            e.op(0x89, EAX, ESI, -16); e.addEsi(-8); break;
        case K_FCMP: EmitFcmp(e, t.x); e.b(0x0F); e.b(0xB6); e.b(0xC0); e.addEsi(-8); e.op(0x89, EAX, ESI, -8); break;
        case K_FBRS: case K_FBR: {
            int tw = posW[k] + (t.kind == K_FBRS ? o16 : *(const int*)(ip + 1));
            EmitFcmp(e, t.x); e.addEsi(-16); e.b(0x84); e.b(0xC0);
            fix[nf].rel = e.jcc(0x85), fix[nf].abs = false, fix[nf++].targetW = tw; break;
        }
        case K_CEQ0: e.rr(0x33, EAX, EAX); e.b(0x83); e.m(7, ESI, -8); e.b(0); e.b(0x0F); e.b(0x94); e.b(0xC0); e.op(0x89, EAX, ESI, -8); break;
        case K_CONV:
            if (t.x == 1) { e.op(0x8B, EAX, ESI, -8); e.b(0x99); e.op(0x89, EDX, ESI, -4); }
            else if (t.x == 2) { e.b(0xC7); e.m(0, ESI, -4); e.d(0); }
            else if (t.x >= 4) { e.load(t.x == 4 ? 1 : t.x == 5 ? 2 : t.x == 6 ? 3 : 4, EAX, ESI, -8); e.op(0x89, EAX, ESI, -8); }
            break;  // conv.i4.i8: the low word already is the result
        default:
            if (op == 0xEA && (g_jitGroups & 256) && !g_jitLazyPass) {
                if (const DWORD fn = JitIcallFn(ip, rm)) { exitRel[0] = EmitIcall(e, fn); break; }
            }
            if ((op == 0xEA || op == 0xEC) && (g_jitPureIcalls || g_jitFastCopy) && posReal[k])
                if (const int pk = JitPureIcall(ip, op, rm)) { EmitPureIcall(e, pk, exitRel); pure++; break; }
            if (g_jitLazyPass && (op == 0x06 || op == 0x07)) { EmitLazyRet(e, ip, op, rm, op == 0x06); nat++; continue; }
            if ((op == 0xEA || op == 0xEC || (op == 0xEB && g_jitInline >= 2)) && g_jitInline && posReal[k] && JitTryInline(e, ip, op, rm)) { nat++; continue; }
            if (((op >= 0xEA && op <= 0xED) || (op == 0xF4 && g_jitNewVt)) && g_jitLazy && !(g_jitLeafOnly && g_jitLazyPass) && posReal[k] && EmitLazyCall(e, ip, op, rm)) { nat++; continue; }
            EmitExit(e, ip, op, t.kind == K_RET);
            ex++;
            continue;
        }
        nat++;
        if (t.kind >= K_FARITH) ts3native_counters.jitWide++;
        if (exitRel[0] || exitRel[1] || exitRel[2]) {  // out-of-line slow path: back to EA's handler with nothing changed
            u8* skip = e.jmp();
            if (exitRel[0]) Fix(exitRel[0], e.p);
            if (exitRel[1]) Fix(exitRel[1], e.p);
            if (exitRel[2]) Fix(exitRel[2], e.p);
            if (g_jcn) g_jcn->slow = true;
            EmitExit(e, ip, op, false);
            if (g_jcn) g_jcn->slow = false;
            Fix(skip, e.p);
        }
    }
    label[n] = e.p;  // falling off the end cannot happen (mint ends in ret/throw/br); trap it
    e.b(0xCC);
    for (int f = 0; f < nf; f++) {
        int tw = fix[f].targetW, tp = tw >= 0 && tw < words ? wordToPos[tw] : -1;
        if (tp < 0) { why = "a branch target is not an instruction"; break; }
        if (fix[f].abs) memcpy(fix[f].rel, &label[tp], 4); else Fix(fix[f].rel, label[tp]);
    }
    }
    g_jitLazyPass = false;
    const int bytes = (int)(e.p - start);
    JitProt(start, need, PAGE_EXECUTE_READ);
    FlushInstructionCache(GetCurrentProcess(), start, bytes);
    if (why) return why;
    // 3. publish: table entries for every position, then patch the opcode words (positions of EA instruction starts and
    // superop inner positions alike; inner words are never read by the superop handlers)
    if (g_jitPosN + n > JIT_POS_N) return "position table full";
    JitMeth& M = g_jitMeth[g_jitMethN];
    M.rm = rm, M.code = code, M.words = words, M.pos0 = g_jitPosN, M.nativeOps = nat, M.exitOps = ex, M.native0 = start, M.bytes = bytes;
    JitMethodName(rm, M.name, sizeof(M.name));
    bool branch0 = false;
    for (int f = 0; f < nf; f++) branch0 |= fix[f].targetW == 0;
    M.verifyOk = !rm[0x41] && !branch0;
    for (int k = 0; k < n; k++) {
        JitEnt* en = JitAdd((DWORD)(uintptr_t)(code + posW[k]));
        if (!en) return "ip table full";
        en->native = label[k], en->orig = code[posW[k]], en->m = &M;
        g_jitPos[g_jitPosN] = (unsigned short)posW[k];
        g_jitOrigW[g_jitPosN++] = code[posW[k]];
    }
    M.posN = g_jitPosN;
    g_jitMethN++;
    g_jitCodeUsed = (g_jitCodeUsed + bytes + 1 + 15) & ~(size_t)15;
    if (g_jitPatched) JitPatchMeth(M, true);
    ts3native_counters.jitMethods = g_jitMethN;
    ts3native_counters.jitCodeKB = (LONG)(g_jitCodeUsed >> 10);
    if (lazyEntry) {
        if (JitLazyCell* lc = JitLazyCellFor(rm)) lc->entry = lazyEntry, ts3native_counters.jitLazyBodies++;
        for (int i = 0; i < g_jitGuardN; i++)  // guard sites that met this method before it had a lazy body
            if (g_jitGuard[i].rm == rm && !g_jitGuard[i].entry) JitGuardFill(&g_jitGuard[i], rm);
    }
    if (g_jitLogN++ < 256) Log("jit: compiled %s: %d positions, %d native, %d exits, %d bytes%s%s%s", M.name, n, nat, ex, bytes, lazyEntry ? ", lazy body" : "", M.verifyOk ? "" : ", not verified (branch to word 0)", pure ? ", pure icalls" : "");
    return NULL;
}

// patch (true) or restore (false) one method's opcode words; the native code stays
static void JitPatchMeth(const JitMeth& m, bool on) {
    for (int k = m.pos0; k < m.posN; k++) m.code[g_jitPos[k]] = on && !m.dead ? JitWord(g_jitOrigW[k]) : g_jitOrigW[k];
}
static void JitSetPatched(bool on) {
    for (int m = 0; m < g_jitMethN; m++) JitPatchMeth(g_jitMeth[m], on);
    g_jitPatched = on;
}
// the first mismatch of a method: EA's words back, for the session (a yielded or suspended frame resumes at the original opcode)
static void JitKill(JitMeth& m, DWORD ip) {
    Ts3nCounters& C = ts3native_counters;
    m.dead = true;
    JitPatchMeth(m, false);
    JitLazyOff(m.rm);
    if (!C.jitFirstBad) C.jitFirstBad = (LONG)(&m - g_jitMeth) + 1;
    Log("jit verify: %s off for the session (its ret at word %ld, %ld mismatches so far)", m.name, (long)(((const unsigned short*)(uintptr_t)ip) - m.code), C.jitMismatch);
}

// ---- JitEnter: the jump-table entry of JIT_OP (eax = edx = JIT_OP, edi = ip); jitCalls counts entries: calls and returns into a method ----
static void JitVerifyEnterStub();
static void JcEnterStub();
static __declspec(naked) void JitEnter() {
    __asm {
        mov eax, edi
        shr eax, 1
        and eax, 0x7FFF            // JIT_TAB_N - 1
        shl eax, 4
        add eax, g_jitTab
    probe:
        cmp [eax], edi
        je found
        add eax, 16
        cmp dword ptr [eax], 0
        jne probe
        int 3                      // a patched word without an entry: cannot happen
    found:
        mov ecx, dword ptr fs:[24h]
        cmp ecx, g_jitSimTid
        jne other
        inc ts3native_counters.jitCalls
        cmp g_jcOn, 0
        jne cap
        cmp g_jitVerifyOn, 0
        jne slow
        mov ebx, [ebp - 8]
        mov ebx, [ebx + 0x10]      // arena base (arguments)
        mov edi, [ebp - 0x14]      // locals base
        jmp dword ptr [eax + 4]
    other:                         // not the sim thread: the original opcode's live handler (the worker's redirect stub in a span)
        lock inc ts3native_counters.jitOtherTid
        mov edx, [eax + 8]         // JitEnt.orig
        mov eax, edx
        mov [ebp - 4], edi
        mov ecx, g_jitTableP
        jmp dword ptr [ecx + edx * 4]
    slow:
        jmp JitVerifyEnterStub
    cap:
        jmp JcEnterStub
    }
}

// ---- verify mode (docs/207 §8.3): every verifiable call runs twice, compiled first, then EA's interpreter on the restored
// state; return value and every snapshotted byte must match. Snapshot = the frame's arena (arguments, locals, eval stack),
// the free arena space after it (callee frames), the stack arguments, and the whole object graph reachable from the
// receiver and the arguments (GC blocks, plus 32 bytes behind a byref into a caller frame), up to JIT_SNAP_OBJS objects /
// 1 MB. A call whose graph is larger runs compiled only and is counted as "too large" (never restored half-way).
struct JitRegs { DWORD edi, esi, ebp, esp_, ebx, edx, ecx, eax; };  // pushad layout
struct JitRegion { u8* at; DWORD n, off; };
static const int JIT_SNAP_OBJS = 4096;
struct JitSnap {
    JitRegion r[JIT_SNAP_OBJS];
    int n;
    bool full;
    u8* buf;
    DWORD used, cap;
    DWORD hs[2 * JIT_SNAP_OBJS], hg[2 * JIT_SNAP_OBJS], gen;  // open-addressing set of region starts (slot live if hg == gen)
    void Clear() { n = 0, used = 0, full = false, gen++; }
    bool Contains(DWORD k) const {
        for (DWORD i = (k >> 3) & (2 * JIT_SNAP_OBJS - 1); hg[i] == gen; i = (i + 1) & (2 * JIT_SNAP_OBJS - 1))
            if (hs[i] == k) return true;
        return false;
    }
    bool Insert(const u8* p) {  // false if already present
        DWORD k = (DWORD)(uintptr_t)p, i = (k >> 3) & (2 * JIT_SNAP_OBJS - 1);
        while (hg[i] == gen) { if (hs[i] == k) return false; i = (i + 1) & (2 * JIT_SNAP_OBJS - 1); }
        hs[i] = k, hg[i] = gen;
        return true;
    }
    bool Add(u8* p, DWORD sz) {  // true if added
        if (n >= JIT_SNAP_OBJS || used + sz > cap) { full = true; return false; }
        if (!Insert(p)) return false;
        r[n].at = p, r[n].n = sz, r[n++].off = used, used += sz;
        return true;
    }
    void Take(JitSnap& into) const {  // copy the current bytes of this snapshot's regions into `into`'s buffer
        into.n = n, into.used = used;
        memcpy(into.r, r, sizeof(JitRegion) * n);
        for (int i = 0; i < n; i++) memcpy(into.buf + r[i].off, r[i].at, r[i].n);
    }
    void Restore() const { for (int i = 0; i < n; i++) memcpy(r[i].at, buf + r[i].off, r[i].n); }
};
struct JitV { bool active; int phase; u8* rm; JitMeth* meth; DWORD sp0, ebp, fr[6]; JitSnap *s0, *s1, *s2; u8 r1[64], r2[64]; int rn, calleeRegion; bool retried; struct JitNewSet* nw; };
static const DWORD JIT_VD = 32;  // verified depths (snapshot buffers per depth)
static JitV* g_jitV;           // [JIT_VD], sim thread only
static int g_jvLogged;
static bool g_jvQuiet;  // first attempt of a verified call: mismatches are logged only if the retry repeats them
static char g_jvWhat[320];
static const struct JitSnap* g_jvS0;  // a mismatch's log: the entry snapshot
typedef u8* (__cdecl* GcBaseFn)(const void*);
static GcBaseFn GcBase = (GcBaseFn)(uintptr_t)TS3A(0x00E4DC30);
static DWORD GcSize(const u8* base) {  // Boehm block size: HDR(base)->hb_sz in words (same lookup as GC_base TS3A(0x00E4DC30))
    DWORD p = (DWORD)(uintptr_t)base;
    const DWORD* top = *(const DWORD* const*)(uintptr_t)(TS3A(0x01233318) + (p >> 22) * 4);
    DWORD hdr = top ? top[(p >> 12) & 0x3FF] : 0;
    if (hdr <= 0xFFF) return 0;
    DWORD words = *(const DWORD*)(uintptr_t)hdr;
    return words * 4 > 0x40000 ? 0x40000 : words * 4;
}
// a region that holds the frame's arena is the GC block of the arena chunk (a byref argument into it): region 0 compares the
// frame, and past it lies callee memory that a direct internal call leaves alone and EA's frame push writes
static DWORD JitLiveBytes(const u8* at, DWORD n, const u8* arena) { return at <= arena && arena < at + n ? (DWORD)(arena - at) : n; }
static const u8* g_jvInv;  // the MonoInvocation being snapshotted (byref targets must lie in one of its frames' arenas)
// finish the last collection before a snapshot, as EA's per-frame step does (0xE49F60: pending [0x11F2F94], [0x11947E4] != -1 = the
// finish 0xE49A50 is still owed, GC_reclaim_all 0xE50380), so no dead cell of a swept-later block can enter it. Such a cell still holds
// its vtable, JitFreeCell cannot see it, and a stale word reaches it. The pass's first allocation then sweeps it: JitKeepNew takes
// what lands there for an old object, and the restore writes the vtable over a free-list link (docs/537 §15). [MonoThread] worker
// allocates too: under its GC lock. Mode=2 only: the call costs one sweep's stall at the first verified call after a collection.
static DWORD* g_jvPending = (DWORD*)(uintptr_t)TS3A(0x011F2F94);
static void JitSweep() {
    DWORD* const pending = g_jvPending;
    LONG* const owed = (LONG*)(uintptr_t)TS3A(0x011947E4);
    if (!*pending || *(const DWORD*)(uintptr_t)TS3A(0x011F30A8)) return;
    if (g_mtLockInit) EnterCriticalSection(&g_mtLock);
    if (*pending && !((int(__cdecl*)())(uintptr_t)TS3A(0x00E4C060))()) {  // not while a new mark runs
        if (*owed != -1) *owed = -1, ((void(__cdecl*)())(uintptr_t)TS3A(0x00E49A50))();
        ((int(__cdecl*)(void*, int))(uintptr_t)TS3A(0x00E50380))(NULL, 0);
        *pending = 0;
    }
    if (g_mtLockInit) LeaveCriticalSection(&g_mtLock);
}
// a free GC cell: word 0 is the free-list link (or 0 in a cleared cell), never a vtable, which lives outside the GC heap. A stale
// word in the graph can reach one, and the link walks the free list. Left in, the snapshot would call what a pass allocates
// there an old object: JitKeepNew keeps no copy, and the two passes' cells differ (docs/537 §11).
static bool JitFreeCell(const u8* b) {
    const DWORD w0 = *(const DWORD*)b;
    return !w0 || GcBase((void*)(uintptr_t)w0);
}
// add the object (or byref target) a word points at; returns the region index or -1
static int JitSnapRef(JitSnap& s, DWORD w) {
    if (w < 0x10000) return -1;
    u8* b = GcBase((void*)(uintptr_t)w);
    if (b) {
        if (b == (u8*)(uintptr_t)w && JitFreeCell(b)) return -1;
        DWORD sz = GcSize(b);
        return sz && s.Add(b, sz) ? s.n - 1 : -1;
    }
    // byref into a caller frame: only inside the arena of a frame of this activation (MonoInvocation parents); arbitrary
    // writable memory (CRT heap, GC free lists) must never be restored
    for (const u8* f = g_jvInv; f; f = *(const u8* const*)f) {
        const u8* frm = *(const u8* const*)(f + 4);
        const u8* base = *(const u8* const*)(f + 0x10);
        DWORD sz = frm ? *(const unsigned short*)(frm + 0x38) : 0;
        if (base && w >= (DWORD)(uintptr_t)base && w < (DWORD)(uintptr_t)base + sz) {
            DWORD n = (DWORD)(uintptr_t)base + sz - w;
            return s.Add((u8*)(uintptr_t)w, n < 32 ? n : 32) ? s.n - 1 : -1;
        }
    }
    return -1;
}
// breadth-first over regions [from, n): every aligned word that is the start of a GC object joins the snapshot
static void JitSnapGraph(JitSnap& s, int from) {
    for (int i = from; i < s.n && !s.full; i++) {
        const u8* p = s.r[i].at;
        DWORD n = s.r[i].n;
        for (DWORD o = (4 - ((DWORD)(uintptr_t)p & 3)) & 3; o + 4 <= n; o += 4) {
            DWORD v = *(const DWORD*)(p + o);
            if (v >= 0x10000 && !(v & 3) && GcBase((void*)(uintptr_t)v) == (u8*)(uintptr_t)v) JitSnapRef(s, v);
        }
    }
}
// objects the compiled pass allocated, copied before the restore (the GC may reuse their memory during pass 2)
struct JitNew { DWORD p, n, off; int depth; };
static const int JIT_NEW_N = 2048;
struct JitNewSet { JitNew o[JIT_NEW_N]; int n, over; DWORD used; u8* buf; int hs[2 * JIT_NEW_N]; DWORD hg[2 * JIT_NEW_N], gen; };  // per depth
static JitNewSet* g_jn;  // the set JitAllocEqual reads (the depth being compared)
static int JitNewFind(const JitNewSet& s, DWORD w) {  // index or -1 (slot live if hg == gen)
    for (DWORD i = (w >> 3) & (2 * JIT_NEW_N - 1); s.hg[i] == s.gen; i = (i + 1) & (2 * JIT_NEW_N - 1))
        if (s.o[s.hs[i]].p == w) return s.hs[i];
    return -1;
}
// keep a copy of a word's object if it is not part of the entry snapshot: allocated during the compiled pass, or an old object the
// entry graph did not reach, which nothing here can tell apart. `over` counts words lost to a full set: a mismatch names it.
static void JitKeepNew(JitNewSet& s, const JitSnap& s0, DWORD w, int depth = 0) {
    if (w < 0x10000 || (w & 3) || GcBase((void*)(uintptr_t)w) != (u8*)(uintptr_t)w || JitNewFind(s, w) >= 0) return;
    if (s0.Contains(w)) return;  // an object of the entry snapshot, not a new one
    DWORD n = GcSize((u8*)(uintptr_t)w);
    if (!n) return;
    if (s.n >= JIT_NEW_N || s.used + n > (1u << 20)) { s.over++; return; }
    const DWORD off = s.used;
    memcpy(s.buf + off, (void*)(uintptr_t)w, n);
    DWORD i = (w >> 3) & (2 * JIT_NEW_N - 1);
    while (s.hg[i] == s.gen) i = (i + 1) & (2 * JIT_NEW_N - 1);
    s.hs[i] = s.n, s.hg[i] = s.gen;
    s.o[s.n++] = {w, n, off, depth};
    s.used += n;
}
// every compared word that changed first, then what those objects point at, breadth first and three levels down: one word naming a
// large old graph must not use up the set before the next word's new object (docs/537 §16). The callee area is not compared.
static void JitKeepPass(JitNewSet& s, const JitSnap& s0, const JitSnap& s1, const u8* ret, int rn, int calleeRegion) {
    s.n = 0, s.over = 0, s.used = 0, s.gen++;
    for (int i = s0.n - 1; i >= 0; i--) {  // the frame (region 0) last: a local that names old objects is the usual flood
        if (i == calleeRegion) continue;
        for (DWORD o = 0; o + 4 <= s0.r[i].n; o += 4) {
            const DWORD a = *(const DWORD*)(s1.buf + s0.r[i].off + o);
            if (a != *(const DWORD*)(s0.buf + s0.r[i].off + o)) JitKeepNew(s, s0, a);
        }
    }
    for (int o = 0; o + 4 <= rn; o += 4) JitKeepNew(s, s0, *(const DWORD*)(ret + o));
    for (int k = 0; k < s.n; k++) {
        if (s.o[k].depth >= 3) continue;
        const JitNew e = s.o[k];
        for (DWORD o = 0; o + 4 <= e.n; o += 4) JitKeepNew(s, s0, *(const DWORD*)(s.buf + e.off + o), e.depth + 1);
    }
}
// two words differ: equal when the compiled pass's word is an object it allocated (kept copy) and the interpreted pass's
// word an object of the same size whose words are equal, or again such a pair (one allocation in each pass)
static bool JitAllocEqual(DWORD a, DWORD b, int depth = 0) {
    if (!g_jn || depth > 3 || a < 0x10000 || b < 0x10000 || GcBase((void*)(uintptr_t)b) != (u8*)(uintptr_t)b) return false;
    DWORD sb = GcSize((u8*)(uintptr_t)b);
    int i = JitNewFind(*g_jn, a);
    if (i < 0 || g_jn->o[i].n != sb) return false;
    const DWORD* x = (const DWORD*)(g_jn->buf + g_jn->o[i].off);
    const DWORD* y = (const DWORD*)(uintptr_t)b;
    DWORD used = sb;
    if (x[0] != y[0]) return false;
    // GC_malloc_atomic leaves a block's slack uninitialised: compare the object's bytes only. MonoVTable+0 = klass;
    // MonoClass name +0x34 / namespace +0x38 [verified live]; instance_size +0x54 (0xE6F5A0 reads it)
    const u8* kl = *(const u8* const*)(uintptr_t)y[0];
    const char* nm = *(const char* const*)(kl + 0x34), *ns = *(const char* const*)(kl + 0x38);
    const size_t nl = strlen(nm);
    const bool arr = nl && nm[nl - 1] == ']';
    if (!arr && !strcmp(nm, "String") && !strcmp(ns, "System")) {
        if (x[1] == y[1] && 8 + 2 * x[1] <= sb) used = 8 + 2 * x[1];  // vtable, length, UTF-16 chars
    } else if (!arr) {
        const DWORD is = *(const DWORD*)(kl + 0x54);
        if (is >= 4 && is <= sb) used = is;
    } else if (const DWORD es = *(const DWORD*)(kl + 0x5C)) {  // array element size [measured: Int32[] 4, byte enum 1, ulong enum 8, refs 4]
        // vtable, bounds 0, length: the slack past the elements is not the array's (docs/537 §16)
        if (!x[1] && !y[1] && x[2] == y[2] && x[2] <= sb && 0x10 + x[2] * es <= sb) used = 0x10 + x[2] * es;
    }
    for (DWORD k = 0; k < (used + 3) / 4; k++)
        if (x[k] != y[k] && !JitAllocEqual(x[k], y[k], depth + 1)) return false;
    return true;
}
static bool JitSameBytes(const u8* x, const u8* y, DWORD n, DWORD* at) {
    for (DWORD i = 0; i < n; i++) {
        if (x[i] == y[i]) continue;
        DWORD a4 = i & ~3u;
        if (a4 + 4 <= n && JitAllocEqual(*(const DWORD*)(x + a4), *(const DWORD*)(y + a4))) { i = a4 + 3; continue; }
        *at = i;
        if (a4 + 4 <= n) {
            DWORD a = *(const DWORD*)(x + a4), b = *(const DWORD*)(y + a4);
            int k = -1;
            for (int j = 0; g_jn && j < g_jn->n; j++) if (g_jn->o[j].p == a) k = j;
            const DWORD* xa = k >= 0 ? (const DWORD*)(g_jn->buf + g_jn->o[k].off) : NULL;
            const DWORD* yb = GcBase((void*)(uintptr_t)b) == (u8*)(uintptr_t)b ? (const DWORD*)(uintptr_t)b : NULL;
            DWORD fk = 0;
            if (xa && yb) while (fk < g_jn->o[k].n / 4 && xa[fk] == yb[fk]) fk++;
            sprintf_s(g_jvWhat, "compiled %08lX interpreted %08lX; kept %d size %lu/%lu words %08lX %08lX %08lX %08lX / %08lX %08lX %08lX %08lX; "
                      "first differing word %lu: %08lX / %08lX; compiled word in entry snapshot %d, kept n %d used %lu over %d", a, b,
                      k, k >= 0 ? g_jn->o[k].n : 0, yb ? GcSize((const u8*)yb) : 0, xa ? xa[0] : 0, xa ? xa[1] : 0, xa ? xa[2] : 0, xa ? xa[3] : 0,
                      yb ? yb[0] : 0, yb ? yb[1] : 0, yb ? yb[2] : 0, yb ? yb[3] : 0, fk, xa && yb ? xa[fk] : 0, xa && yb ? yb[fk] : 0, g_jvS0 && g_jvS0->Contains(a), g_jn ? g_jn->n : -1, g_jn ? g_jn->used : 0, g_jn ? g_jn->over : 0);
        }
        return false;
    }
    return true;
}
static int JitRetBytes(const JitRegs* r, DWORD op, const unsigned short* ip, u8* out) {
    const char* nm = g_jitOpNames[op];
    if (!strcmp(nm, "ret.void")) return 0;
    const u8* top = (const u8*)(uintptr_t)(r->esi - 8);
    if (!strcmp(nm, "ret.vt")) {  // the slot holds a pointer to the value; operand = size
        DWORD sz = *(const DWORD*)(ip + 1);
        if (sz > 64) sz = 64;
        memcpy(out, *(const u8* const*)top, sz);
        return (int)sz;
    }
    memcpy(out, top, 8);
    return 8;
}
static void JitVLog(JitV& v, const char* what, DWORD at) {
    if (!g_jvQuiet && g_jvLogged++ < 40) Log("jit verify MISMATCH %s: %s at +%lu (%s)", v.meth ? v.meth->name : "?", what, at, g_jvWhat);
}
static u8* JitNative(JitRegs* r, const JitEnt* e) {
    r->ebx = *(const DWORD*)(uintptr_t)(*(const DWORD*)(uintptr_t)(r->ebp - 8) + 0x10);
    r->edi = *(const DWORD*)(uintptr_t)(r->ebp - 0x14);
    return e->native;
}
static void* JitOrig(JitRegs* r, DWORD ip, DWORD op) {
    r->edi = ip, r->eax = r->edx = op;
    *(DWORD*)(uintptr_t)(r->ebp - 4) = ip;
    return (void*)(uintptr_t)g_jitTableP[op];  // the live entry, as EmitExit
}

// ---- reader trap (docs/537 §3, docs/223 §8 risk 1): proves nothing reads a frame a lazy call did not write. A sampled window
// runs from the outermost lazy call to its return or first frame write: the dead record slots above the depth hold poison,
// and the sim thread's debug registers watch the chain head, the depth, the next slot and the current-state global. Reads
// by compiled code (its own depth test) do not count. The other threads get the same addresses every few seconds. Hits go
// to the log with module, thread and stack; a hit in a window, or off the sim thread outside the census (kJitReaders), is
// unknown: Direct=1 cannot ship with one.
static const DWORD JIT_TRAP_CODE = 0xE0503909, JIT_TRAP_POISON = 0xBADF0000;
static const DWORD JIT_TRAP_DR7 = 0xFFFF0055;  // all four: local enable, read or write, 4 bytes
static DWORD g_jitTrapEvery = 1024;  // [MonoJit] TrapEvery: lazy calls per window
static DWORD g_jitTrapTick, g_jitTrapLastArm;
static bool g_jitTrapOpen;           // sim thread only
static DWORD g_jitTrapDr[4];         // what the next arm writes
static DWORD g_jitTrapOther[2];      // the last window's chain head and depth, for the other threads
static PVOID g_jitTrapVeh;
static LONG g_jitTrapLog, g_jitTrapArmLog;
static DWORD g_jitTrapState, g_jitTrapArmState;  // the last window's interpreter state, and the one behind the other threads' registers
static LONG g_jitTrapNotCur;         // windows whose state was not the current one: the dead-slot rule below would be wrong
static const struct { DWORD lo, hi; const char* what; } kJitReaders[] = {  // docs/data/frame-readers.csv, every frame reader of the census
    {TS3A(0x00E52540), TS3A(0x00E525B8), "stack walk"}, {TS3A(0x00E529A0), TS3A(0x00E52B60), "stack trace"},
    {TS3A(0x00E53130), TS3A(0x00E53220), "interruption helpers, frame count"}, {TS3A(0x00E534E0), TS3A(0x00E53542), "records by depth"},
    {TS3A(0x00E53550), TS3A(0x00E53740), "may-yield, frame save"}, {TS3A(0x00E53740), TS3A(0x00E53EB0), "native raise, trace"},
    {TS3A(0x00E53EB0), TS3A(0x00E5A600), "ves_exec and task entry"}, {TS3A(0x00D70E00), TS3A(0x00D83000), "script host"},
};
static const char* JitTrapReader(DWORD eip) {
    for (const auto& r : kJitReaders) if (eip >= r.lo && eip < r.hi) return r.what;
    return NULL;
}
static void JitTrapArm(DWORD a, DWORD b, DWORD c, DWORD d) {  // this thread's debug registers, through the VEH; all 0 = off
    g_jitTrapDr[0] = a, g_jitTrapDr[1] = b, g_jitTrapDr[2] = c, g_jitTrapDr[3] = d;
    RaiseException(JIT_TRAP_CODE, 0, 0, NULL);
}
static bool JitOwnsXcpt(const EXCEPTION_POINTERS* e) {  // XcptLog: ours, not a crash
    if (!g_jitTrapVeh) return false;
    return e->ExceptionRecord->ExceptionCode == JIT_TRAP_CODE || (e->ExceptionRecord->ExceptionCode == EXCEPTION_SINGLE_STEP && (e->ContextRecord->Dr6 & 0xF));
}
static void JitTrapHit(const CONTEXT* c, DWORD watched) {
    Ts3nCounters& C = ts3native_counters;
    const u8* eip = (const u8*)(uintptr_t)c->Eip;
    if (eip >= g_jitCode && eip < g_jitCodeEnd) return;  // compiled code: the site's own depth test
    const DWORD tid = GetCurrentThreadId();
    const bool sim = tid == g_jitSimTid, self = c->Eip >= g_selfLo && c->Eip < g_selfHi;
    const char* known = JitTrapReader(c->Eip);
    if (sim && !g_jitTrapOpen) return;  // the VEH's last hit on the way out of a window
    InterlockedIncrement(&C.jitTrapHits);
    // the chain head and depth of the last window die with its run (GC_malloc'd task state, or runtime_invoke's on the sim stack,
    // docs/537 §13): while another state runs they are other memory, and a lazy call only ever runs in the current state
    const bool slot = watched != TS3A(0x011F4170) && watched != TS3A(0x011F416C);
    const bool dead = !sim && slot && g_jitTrapArmState && *g_curState != g_jitTrapArmState;
    const bool unknown = sim || (!known && !self && !dead);
    if (unknown) InterlockedIncrement(&C.jitTrapUnknown);
    if (InterlockedIncrement(&g_jitTrapLog) > 64) return;
    HMODULE mod = NULL;
    char path[MAX_PATH] = "?";
    if (GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, (LPCSTR)eip, &mod)) GetModuleFileNameA(mod, path, sizeof(path));
    const char* base = strrchr(path, '\\');
    DWORD st[8] = {};
    __try {  // the reader's return addresses: the first eight words on its stack that point into a module
        const DWORD* sp = (const DWORD*)(uintptr_t)c->Esp;
        for (int i = 0, k = 0; i < 256 && k < 8; i++) {
            HMODULE h;
            if (GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, (LPCSTR)(uintptr_t)sp[i], &h)) st[k++] = sp[i];
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {}
    Log("jit trap: %s hit at eip %08lX (%s, %s), thread %lu%s, watched %08lX, window %d; stack %08lX %08lX %08lX %08lX %08lX %08lX %08lX %08lX; armed state %08lX",
        unknown ? "UNKNOWN" : "known", c->Eip, base ? base + 1 : path, known ? known : self ? "ts3native" : dead ? "state not running" : "not in the census", tid, sim ? " (sim)" : "", watched, g_jitTrapOpen ? 1 : 0,
        st[0], st[1], st[2], st[3], st[4], st[5], st[6], st[7], g_jitTrapArmState);
}
static LONG CALLBACK JitTrapVeh(EXCEPTION_POINTERS* e) {
    CONTEXT* c = e->ContextRecord;
    const DWORD code = e->ExceptionRecord->ExceptionCode;
    if (code == JIT_TRAP_CODE) {
        const bool on = g_jitTrapDr[0] != 0;
        c->Dr0 = g_jitTrapDr[0], c->Dr1 = g_jitTrapDr[1], c->Dr2 = g_jitTrapDr[2], c->Dr3 = g_jitTrapDr[3];
        c->Dr6 = 0, c->Dr7 = on ? JIT_TRAP_DR7 : 0;
        c->ContextFlags |= CONTEXT_DEBUG_REGISTERS;
        return EXCEPTION_CONTINUE_EXECUTION;
    }
    if (code != EXCEPTION_SINGLE_STEP || !(c->Dr6 & 0xF)) return EXCEPTION_CONTINUE_SEARCH;
    const DWORD dr[4] = {c->Dr0, c->Dr1, c->Dr2, c->Dr3};
    for (int i = 0; i < 4; i++) if (c->Dr6 & (1u << i)) JitTrapHit(c, dr[i]);
    c->Dr6 = 0;
    c->ContextFlags |= CONTEXT_DEBUG_REGISTERS;
    return EXCEPTION_CONTINUE_EXECUTION;
}
// the dead slots above the depth (the ones a chain would take): every word JitLazyMatC, like EA's push, writes before anyone
// reads it. The poison stays after the window: a reader of a slot EA did not write crashes in the verify run, which is the point.
static void JitTrapPoison(u8* ebp, DWORD d) {
    const DWORD hwm = *(const DWORD*)(ebp - 0x30);
    for (DWORD i = d + 1; i < hwm && i <= d + 8; i++) {
        DWORD* rec = (DWORD*)(ebp + i * 0x130 - 0x4C74);
        rec[1] = rec[2] = rec[3] = rec[4] = rec[5] = JIT_TRAP_POISON | i;
        rec[0x128 / 4] = rec[0x12C / 4] = JIT_TRAP_POISON | i;
        if (DWORD* inv = (DWORD*)(uintptr_t)rec[0]) for (int w = 0; w < 10; w++) inv[w] = JIT_TRAP_POISON | i;
    }
}
static void __cdecl JitTrapOpen(u8* ebp) {
    if (++g_jitTrapTick % g_jitTrapEvery || g_jitTrapOpen) return;
    u8* st = *(u8**)(ebp + 8);
    const u8* ctx = *(const u8* const*)(ebp + 0xC);
    const DWORD d = *(const DWORD*)(st + 0x140);
    g_jitTrapState = (DWORD)(uintptr_t)st;
    if (*g_curState != g_jitTrapState) g_jitTrapNotCur++;
    JitTrapPoison(ebp, d);
    ts3native_counters.jitTrapWindows++;
    const u8* slot = *(const u8* const*)(ebp + (d + 1) * 0x130 - 0x4C74);
    g_jitTrapOther[0] = (DWORD)(uintptr_t)(ctx + 8), g_jitTrapOther[1] = (DWORD)(uintptr_t)(st + 0x140);
    g_jitTrapOpen = true;
    JitTrapArm(g_jitTrapOther[0], g_jitTrapOther[1], (DWORD)(uintptr_t)(slot + 4), TS3A(0x011F4170));
}
static void __cdecl JitTrapClose() {
    if (!g_jitTrapOpen) return;
    JitTrapArm(0, 0, 0, 0);
    g_jitTrapOpen = false;
}
static __declspec(naked) void JitTrapOpenStub() {
    __asm {
        pushad
        push ebp
        call JitTrapOpen
        add esp, 4
        popad
        ret
    }
}
static __declspec(naked) void JitTrapCloseStub() {
    __asm {
        pushad
        call JitTrapClose
        popad
        ret
    }
}
// the test probe's own threads stay unarmed: its memory scan reads the whole GC heap, a hit there is no game reader and a Frida
// read scope turns it into a failed scan, which hid the agent mailbox from the harness (docs/537 §12)
static bool JitTrapProbePath(const char* path) { return strstr(path, "frida-agent") != NULL; }
static bool JitTrapProbeThread(HANDLE t) {
    typedef LONG(WINAPI* QitFn)(HANDLE, int, void*, ULONG, ULONG*);
    static QitFn q = (QitFn)(void*)GetProcAddress(GetModuleHandleW(L"ntdll.dll"), "NtQueryInformationThread");
    void* start = NULL;  // class 9: ThreadQuerySetWin32StartAddress
    HMODULE mod = NULL;
    char path[MAX_PATH] = "";
    return q && q(t, 9, &start, sizeof(start), NULL) >= 0
        && GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, (LPCSTR)start, &mod)
        && GetModuleFileNameA(mod, path, sizeof(path)) && JitTrapProbePath(path);
}
static int g_jitTrapProbeN;  // threads left out at the last arm
// every other thread watches the last window's addresses plus the two globals (a thread can only reach the sim thread's
// frames through them, docs/537 §2); no lock is taken while a thread is suspended
static int JitTrapArmOthers(const DWORD addr[4]) {
    HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
    if (snap == INVALID_HANDLE_VALUE) return 0;
    THREADENTRY32 te = {sizeof(te)};
    int n = 0;
    g_jitTrapProbeN = 0;
    const DWORD pid = GetCurrentProcessId(), self = GetCurrentThreadId();
    for (BOOL ok = Thread32First(snap, &te); ok; ok = Thread32Next(snap, &te)) {
        if (te.th32OwnerProcessID != pid || te.th32ThreadID == self) continue;
        HANDLE t = OpenThread(THREAD_SUSPEND_RESUME | THREAD_GET_CONTEXT | THREAD_SET_CONTEXT | THREAD_QUERY_INFORMATION, FALSE, te.th32ThreadID);
        if (!t) continue;
        if (JitTrapProbeThread(t)) g_jitTrapProbeN++;
        else if (SuspendThread(t) != (DWORD)-1) {
            CONTEXT c = {};
            c.ContextFlags = CONTEXT_DEBUG_REGISTERS;
            if (GetThreadContext(t, &c)) {
                c.Dr0 = addr[0], c.Dr1 = addr[1], c.Dr2 = addr[2], c.Dr3 = addr[3], c.Dr6 = 0, c.Dr7 = JIT_TRAP_DR7;
                if (SetThreadContext(t, &c)) n++;
            }
            ResumeThread(t);
        }
        CloseHandle(t);
    }
    CloseHandle(snap);
    return n;
}
static void JitTrapTick() {  // end of a Simulate iteration: no window open
    const DWORD now = GetTickCount();
    if (!g_jitTrapOther[0] || now - g_jitTrapLastArm < 5000) return;
    g_jitTrapLastArm = now;
    const DWORD addr[4] = {TS3A(0x011F4170), TS3A(0x011F416C), g_jitTrapOther[0], g_jitTrapOther[1]};
    g_jitTrapArmState = g_jitTrapState;
    const int n = JitTrapArmOthers(addr);
    if (InterlockedIncrement(&g_jitTrapArmLog) <= 8) Log("jit trap: %d other threads watch the frame state (%ld windows so far), %d probe threads left out, %ld windows not on the current state", n, ts3native_counters.jitTrapWindows, g_jitTrapProbeN, g_jitTrapNotCur);
}

// a lazy callee's first exit: write the frames EA's calls would have written at the call sites (docs/139 McCall + McPush:
// caller record, depth and max depth, callee record, MonoInvocation, ThreadContext +8, frame words), from the blocks the
// call sites pushed (r->esp_ = the return address into the innermost call site, its block above it; in a chain the outer
// blocks follow, each above the return address into its lazy body, B_LVL of them in all). Outermost first, so each frame's
// caller state is the frame just written. The arena bumps, init-locals clears and the return-slot words were done at the
// calls. JitLazyMat then drops every native call of the chain (ecx bytes) and takes the exit.
static void __cdecl JitLazyMatC(JitRegs* r) {
    Ts3nCounters& C = ts3native_counters;
    if (g_jitTrapOpen) JitTrapClose();  // what follows reads and writes what the window watches
    const DWORD* blk[64];
    int L = 0;
    for (const DWORD* B = (const DWORD*)(uintptr_t)(r->esp_ + 4); L < 64; B = (const DWORD*)((const u8*)B + B_SIZE + 4)) {
        blk[L++] = B;
        if (B[B_LVL / 4] <= 1) break;
    }
    u8* const ebp = (u8*)(uintptr_t)r->ebp;
    auto F = [ebp](int off) -> DWORD& { return *(DWORD*)(ebp + off); };
    u8* st = (u8*)(uintptr_t)F(8);
    for (int i = L - 1; i >= 0; i--) {
        const DWORD* B = blk[i];
        const DWORD ebx = i == 0 ? r->ebx : blk[i - 1][B_EBX / 4];  // this frame's arena base: the inner call site saved it
        const DWORD d = ++*(DWORD*)(st + 0x140);
        if ((int)d > *(const int*)(st + 0x144)) *(DWORD*)(st + 0x144) = d;
        u8* rec = ebp + d * 0x130 - 0x4C74;
        DWORD* prev = (DWORD*)(rec - 0x130);
        prev[0] = F(-8), prev[1] = B[B_RIP / 4], prev[2] = B[B_RSP / 4], prev[3] = F(-0xC), prev[4] = F(-0x14);
        *(DWORD*)(uintptr_t)(F(-8) + 0x1C) = B[B_CIP / 4];  // caller's throw ip
        F(-0x2C) = (DWORD)(uintptr_t)rec;
        *(DWORD*)(rec + 0x128) = 0, *(DWORD*)(rec + 0x12C) = 0, *(DWORD*)(rec + 0x14) = 0;
        u8* inv = *(u8**)rec;
        const u8* rm = (const u8*)(uintptr_t)B[B_RM / 4];
        if (g_jitNewVt && JitOrigWord((const unsigned short*)(uintptr_t)B[B_CIP / 4]) == 0xF4) {  // B2b newobj.vt: EA's 0xE56174 / 0xE56212 / 0xE56346
            prev[6] = *(const DWORD*)(*(const u8* const*)(rm + 8) + 8);  // caller record +0x18 = class (the post 0xE561B0 reads it)
            prev[8] = B[B_THIS / 4];                                   // +0x20 = the new value's address (the cursor)
            *(DWORD*)(rec + 0x14) = 1;                                 // callee record: newobj pending ([ebp-0x28] = 1)
        }
        *(DWORD*)(inv + 0) = F(-8), *(DWORD*)(inv + 4) = B[B_RM / 4], *(DWORD*)(inv + 0x14) = B[B_ARGS / 4];
        *(DWORD*)(inv + 8) = B[B_RSLOT / 4], *(DWORD*)(inv + 0xC) = B[B_THIS / 4];
        *(DWORD*)(inv + 0x20) = 0, *(DWORD*)(inv + 0x1C) = 0, *(DWORD*)(inv + 0x24) = 0;
        *(DWORD*)(*(u8**)(ebp + 0xC) + 8) = (DWORD)(uintptr_t)inv;
        F(-8) = (DWORD)(uintptr_t)inv, F(-0x10) = B[B_RM / 4];
        *(DWORD*)(inv + 0x10) = ebx;
        DWORD a = ebx + *(const unsigned short*)(rm + 0x3E);  // eval stack base
        *(DWORD*)(rec + 8) = a, *(DWORD*)(inv + 0x18) = a;
        a += *(const unsigned short*)(rm + 0x34);
        *(DWORD*)(rec + 0xC) = a, F(-0xC) = B[B_VT / 4];  // the cursor: the body's value-type moves may have advanced it
        a += *(const unsigned short*)(rm + 0x36);
        *(DWORD*)(rec + 0x10) = a, F(-0x14) = a;
        *(DWORD*)(rec + 4) = *(const DWORD*)rm, F(-4) = *(const DWORD*)rm;
        C.jitLazyFrames++;
    }
    r->ecx = L * (4 + B_SIZE);
    C.jitLazyMats++;
    if (g_jcn) JcnMatHit((const u8*)(uintptr_t)blk[0][B_RM / 4], r->edi, r->eax);  // count mode: blk[0] is the innermost callee
}
static_assert(B_SIZE == 44, "JitLazyMat drops 4 + B_SIZE bytes per pending frame");
static __declspec(naked) void JitLazyMat() {  // edi = exit ip, eax = opcode; [esp] = return address, [esp+4] = block
    __asm {
        pushad
        push esp
        call JitLazyMatC
        add esp, 4
        popad
        add esp, ecx            // the return addresses and blocks of every frame just written (JitLazyMatC: ecx)
        mov [ebp - 4], edi
        mov edx, eax
        mov ecx, g_jitTableP
        jmp dword ptr [ecx + eax * 4]  // the live entry, as EmitExit
    }
}

// on first use per depth: 3 snapshots + the kept copies, 1 MB each. s0 is a GC root: an object only s0 still names (a field
// the compiled pass overwrote) must survive a collection in that pass, or the restore would bring back a freed pointer.
static bool JitVAlloc(JitV& v) {
    JitSnap** ss[3] = {&v.s0, &v.s1, &v.s2};
    for (JitSnap** s : ss) {
        if (*s) continue;
        JitSnap* p = (JitSnap*)VirtualAlloc(NULL, sizeof(JitSnap) + (1 << 20), MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
        if (!p) return false;
        p->cap = 1 << 20, p->buf = (u8*)(p + 1), *s = p;
        if (s == &v.s0) g_jitAddRoots(p->buf, p->buf + (1 << 20));
    }
    JitNewSet* n = (JitNewSet*)VirtualAlloc(NULL, sizeof(JitNewSet) + (1 << 20), MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    if (!n) return false;
    n->buf = (u8*)(n + 1), v.nw = n;
    return true;
}

static void* __cdecl JitVerifySlow(JitRegs* r, JitEnt* e, int fromExit) {
    if (!e) e = JitFind(r->edi);
    Ts3nCounters& C = ts3native_counters;
    JitMeth* const m = e->m;
    const u8* rm = m->rm;
    DWORD depth = *(const DWORD*)(uintptr_t)(*(const DWORD*)(uintptr_t)(r->ebp + 8) + 0x140);
    if (depth >= JIT_VD) return fromExit ? JitOrig(r, r->edi, e->orig) : JitNative(r, e);
    JitV& v = g_jitV[depth];
    const unsigned short* code = *(const unsigned short* const*)rm;
    const bool mine = v.active && v.rm == rm && v.ebp == r->ebp;  // ebp = the interpreter activation (nested ones restart at depth 0)
    if ((DWORD)(uintptr_t)code == r->edi && v.active && v.ebp != r->ebp) return JitNative(r, e);  // slot held by an outer activation
    // one verification in flight per activation: compiled callees of a verified frame run compiled in its pass 1 and
    // interpreted in its pass 2, so pass 2 is a pure-interpreter reference for the whole call tree (and cost stays 2x)
    int outer = 0;
    for (DWORD k = 0; k < depth; k++) if (g_jitV[k].active && g_jitV[k].ebp == r->ebp) outer = g_jitV[k].phase;
    if (fromExit) {  // a compiled ret
        if (!mine || v.phase != 1) return JitOrig(r, r->edi, e->orig);
        v.rn = JitRetBytes(r, e->orig, (const unsigned short*)(uintptr_t)r->edi, v.r1);
        v.s0->Take(*v.s1);
        JitKeepPass(*v.nw, *v.s0, *v.s1, v.r1, v.rn, v.calleeRegion);  // copies of what the compiled pass allocated, before the restore
        v.s0->Restore();
        r->esi = v.sp0;
        for (int i = 0; i < 6; i++) *(DWORD*)(uintptr_t)(r->ebp - 0x8 - 4 * i) = v.fr[i];  // interpreter frame words [ebp-8]..[ebp-0x1C] (vt area cursor [ebp-0xC])
        v.phase = 2;
        return JitOrig(r, (DWORD)(uintptr_t)code, JitFind((DWORD)(uintptr_t)code)->orig);  // pass 2 from word 0
    }
    if ((DWORD)(uintptr_t)code == r->edi && !(mine && v.phase == 2)) {  // fresh call
        for (DWORD d = depth; d < JIT_VD; d++) if (g_jitV[d].active && g_jitV[d].ebp == r->ebp) g_jitV[d].active = false, C.jitAbandoned++;
        if (outer) return outer == 2 ? JitOrig(r, r->edi, e->orig) : JitNative(r, e);
        if (!m->verifyOk || ++m->tick % g_jitEvery) return JitNative(r, e);  // one call in VerifyEvery per method
        if (!v.nw && !JitVAlloc(v)) { C.jitLarge++; return JitNative(r, e); }
        v.active = true, v.phase = 1, v.rm = (u8*)rm, v.meth = m, v.sp0 = r->esi, v.ebp = r->ebp;
        for (int i = 0; i < 6; i++) v.fr[i] = *(const DWORD*)(uintptr_t)(r->ebp - 0x8 - 4 * i);
        JitSweep();
        v.s0->Clear(), v.calleeRegion = -1, v.retried = false;
        const u8* inv = (const u8*)(uintptr_t)*(const DWORD*)(uintptr_t)(r->ebp - 8);
        u8* arena = *(u8* const*)(inv + 0x10);
        DWORD asz = *(const unsigned short*)(rm + 0x38);
        v.s0->Add(arena, asz);
        {  // callee arenas: the same leftover bytes in both passes (ret copies the dead high word of a 4-byte slot); only up to
           // the end of the current arena chunk (state+0x13C -> chunk: +0 top, +4 end), never into other heap memory
            const u8* chunk = *(const u8* const*)(uintptr_t)(*(const DWORD*)(uintptr_t)(r->ebp + 8) + 0x13C);
            const u8* cend = chunk ? *(const u8* const*)(chunk + 4) : NULL;
            if (chunk && arena >= chunk && arena + asz <= cend) {
                DWORD n = (DWORD)(cend - (arena + asz));
                if (n && v.s0->Add(arena + asz, n < 2048 ? n : 2048)) v.calleeRegion = v.s0->n - 1;
            }
        }
        u8* sargs = *(u8* const*)(inv + 0x14);
        int np = rm[0x40];
        if (sargs && np) v.s0->Add(sargs, 8 * np);
        g_jvInv = inv;
        const int walk = v.s0->n;
        JitSnapRef(*v.s0, *(const DWORD*)(inv + 0xC));  // this: object, or a byref for value-type methods
        for (int i = 0; i < np && sargs; i++) JitSnapRef(*v.s0, *(const DWORD*)(sargs + 8 * i));
        JitSnapGraph(*v.s0, walk);
        if (v.s0->full) { v.active = false, C.jitLarge++; return JitNative(r, e); }
        for (int i = 0; i < v.s0->n; i++) memcpy(v.s0->buf + v.s0->r[i].off, v.s0->r[i].at, v.s0->r[i].n);
        return JitNative(r, e);
    }
    if (mine && v.phase == 2) {  // interpreted pass of a verified call
        if (g_opInfo[e->orig < 0x1C1 ? e->orig : 0].kind == K_RET) {
            u8 rb[64];
            int rn = JitRetBytes(r, e->orig, (const unsigned short*)(uintptr_t)r->edi, rb);
            v.s0->Take(*v.s2);
            g_jvQuiet = !v.retried;
            g_jn = v.nw, g_jvS0 = v.s0;
            bool ok = true;
            DWORD at = 0;
            if (rn != v.rn || !JitSameBytes(v.r1, rb, rn, &at)) ok = false, JitVLog(v, "return value", at);
            // dead memory is restored but not compared: eval-stack slots at or above the ret's sp (EA's superinstructions skip
            // the temporary stores their base sequences make, docs/130 §9.1) and the callee-frame space after the arena
            const u8* arena = v.s0->r[0].at;
            DWORD deadLo = r->esi - (DWORD)(uintptr_t)arena, deadHi = (DWORD)*(const unsigned short*)(rm + 0x3E) + *(const unsigned short*)(rm + 0x34);
            if (deadLo > deadHi) deadLo = deadHi;
            if (!JitSameBytes(v.s1->buf, v.s2->buf, deadLo, &at) ||
                (deadHi < v.s0->r[0].n && !JitSameBytes(v.s1->buf + deadHi, v.s2->buf + deadHi, v.s0->r[0].n - deadHi, &at)))
                ok = false, JitVLog(v, "region 0 (frame arena)", at);
            for (int i = 1; ok && i < v.s0->n; i++)
                if (i != v.calleeRegion && !JitSameBytes(v.s1->buf + v.s0->r[i].off, v.s2->buf + v.s0->r[i].off, JitLiveBytes(v.s0->r[i].at, v.s0->r[i].n, arena), &at)) {
                    ok = false;
                    char what[160];
                    const u8* ra = v.s0->r[i].at;
                    const char* cls = "?";
                    if (GcBase(ra) == ra && *(const DWORD*)ra >= 0x10000) {
                        const u8* kl = *(const u8* const*)(uintptr_t)*(const DWORD*)ra;
                        if (kl) cls = *(const char* const*)(kl + 0x34);
                    }
                    sprintf_s(what, "region %d (%lu bytes, class %.60s)", i, v.s0->r[i].n, cls ? cls : "?");
                    JitVLog(v, what, at);
                }
            if (!ok && !v.retried) {
                // second chance from the same entry state: a one-time effect of the first compiled pass that the restore
                // cannot undo (class init, a transform, a lazily filled cache outside the graph) is then in place for both
                // passes; a real compiler difference repeats
                v.retried = true;
                v.s0->Restore();
                r->esi = v.sp0;
                for (int i = 0; i < 6; i++) *(DWORD*)(uintptr_t)(r->ebp - 0x8 - 4 * i) = v.fr[i];
                v.phase = 1;
                return JitNative(r, JitFind((DWORD)(uintptr_t)code));
            }
            (ok ? C.jitVerified : C.jitMismatch)++;
            v.active = false;
            if (!ok) JitKill(*v.meth, r->edi);
        }
        return JitOrig(r, r->edi, e->orig);
    }
    return outer == 2 ? JitOrig(r, r->edi, e->orig) : JitNative(r, e);  // compiled pass, a callee inside one, or unverified
}
static __declspec(naked) void JitVerifyEnterStub() {  // eax = table entry, edi = ip
    __asm {
        sub esp, 4
        pushad
        mov ecx, esp
        push 0
        push eax
        push ecx
        call JitVerifySlow
        add esp, 12
        mov [esp + 32], eax
        popad
        ret
    }
}
static __declspec(naked) void JitVerifyExitStub() {  // edi = ip of a compiled ret
    __asm {
        sub esp, 4
        pushad
        mov ecx, esp
        push 1
        push 0
        push ecx
        call JitVerifySlow
        add esp, 12
        mov [esp + 32], eax
        popad
        ret
    }
}

#define JC_LOG_NL ""
#define JC_METH(e) ((e)->m)
#define JC_RM(e) ((e)->m->rm)
static JitMeth* JcMethOfRm(const u8* rm) { const JitEnt* en = JitFind(*(const DWORD*)rm); return en ? en->m : NULL; }  // word 0 of a compiled method has its entry
#define JC_METH_RM(rm) JcMethOfRm(rm)
#define JC_OPNAME(op) g_jitOpNames[op]
#define JC_ON_SIM true  // JitEnter already sent every other thread away
#define JC_NEXT(r, e) (g_jitVerifyOn ? JitVerifySlow(r, e, 0) : JitNative(r, e))
#include "monojit-capture.h"  // [MonoJit] Capture=1: real arguments for the host's replay

// ---- the post-pass (ilpatch.cpp's detour, after EA's transform) ----
static bool JitHashKnown(const char* full, const char* mn, DWORD size, const BYTE sha[32]) {
    for (int i = 0; i < g_jitRowN; i++)
        if (g_jitRows[i].size == size && !memcmp(g_jitRows[i].sha, sha, 32) && JitEndsWith(full, g_jitRows[i].key)) return true;
    for (int i = 0; i < kIlpSpecN; i++) {  // a core that carries an [IlPatch] fix in the converter's form (docs/527 §1)
        if (strcmp(mn, kIlpSpecs[i].name)) continue;
        for (DWORD b = 0; b < kIlpSpecs[i].nBuilds; b++) if (!memcmp(kIlpSpecs[i].builds[b].patched, sha, 32)) return true;
    }
    return false;
}
// NULL = compiled or nothing to do; else why not (name and note filled)
static const char* JitOne(BYTE* rm, void* ctx, char* name, int nameN, char* note, int noteN) {
    if (!*(void**)rm) return NULL;
    JitMethodName(rm, name, nameN);
    if (!JitListed(name)) return NULL;
    if (g_jitSimTid && GetCurrentThreadId() != g_jitSimTid) return "another thread";
    if (*g_jitForceJit != 0x75) return "MonoPatcher ForceJIT is on";
    for (int i = 0; i < g_jitMethN; i++) if (g_jitMeth[i].rm == rm) return NULL;  // a repeat transform: compiled once
    if (rm[0x41] && !g_jitWide) return "exception clauses";  // before the hash and the callees: nothing here for a lazy site either
    BYTE* mm = *(BYTE**)(rm + 8);
    const BYTE* hdr = mm ? g_ilpHeader(mm) : NULL;
    const DWORD size = hdr ? *(const DWORD*)hdr : 0;
    const BYTE* code = hdr ? *(const BYTE* const*)(hdr + 4) : NULL;
    BYTE sha[32];
    if (!code || !size || size > (1u << 20) || !IlpSha256(code, size, sha)) return "no readable IL body";
    if (!JitHashKnown(name, *(const char* const*)(mm + 0x18), size, sha)) {
        char hex[17];
        sprintf_s(note, noteN, " (%lu bytes, sha256 %s...)", size, IlpHex(sha, 8, hex));
        return "the IL hash is not a known one";
    }
    g_jitDomain = ctx ? *(void**)ctx : NULL;
    g_jitObjectClass = *g_jitObjectClassP;
    if (g_jitLazy || g_jitInline) JitPreTransform(rm, ctx);
    const int s0 = g_jcn ? g_jcn->siteN : 0, b0 = g_jcn ? g_jcn->bailN : 0;
    const char* why = JitCompile(rm);
    if (why && g_jcn) g_jcn->siteN = s0, g_jcn->bailN = b0;  // count mode: a refused method leaves no rows
    return why;
}
static void __cdecl JitPostPass(BYTE* rm, void* ctx) {
    if (g_jitDead) return;
    char name[256] = "?", note[64] = "";
    const char* why = NULL;
    EnterCriticalSection(&g_jitCs);
    __try {
        why = JitOne(rm, ctx, name, sizeof(name), note, sizeof(note));
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        g_jitDead = true;
        ts3native_counters.jitFaults++;
        JitProt(g_jitCode, JIT_CODE_BYTES, PAGE_EXECUTE_READ);
        Log("jit: fault 0x%08lX while compiling %s: no more compiles this session", GetExceptionCode(), name);
    }
    LeaveCriticalSection(&g_jitCs);
    if (why) {
        ts3native_counters.jitRefused++;
        if (g_jitLogN++ < 256) Log("jit: %s refused: %s%s", name, why, note);
    }
}
// End of every Simulate iteration (sim thread): names the sim thread, applies jitSwitch
static void __cdecl JitTick() {
    if (!g_jitSimTid) {
        EnterCriticalSection(&g_jitCs);  // a compile on the loading thread finishes first: from here on only this thread compiles
        g_jitSimTid = GetCurrentThreadId();
        LeaveCriticalSection(&g_jitCs);
        Log("jit: sim thread %lu, %d methods compiled before the first tick", g_jitSimTid, g_jitMethN);
    }
    const bool on = ts3native_counters.jitSwitch != 0;
    if (on != g_jitPatched) JitSetPatched(on);
    if (g_jitTrap) JitTrapTick();
    if (g_jcOn) JcTick();
    if (g_jcn) JcnTick();
}
// for the crash report: a fault inside compiled code names its method
static bool JitFaultName(DWORD eip, char* out, int n) {
    for (int i = 0; i < g_jitMethN; i++) {
        const JitMeth& m = g_jitMeth[i];
        if (eip < (DWORD)(uintptr_t)m.native0 || eip >= (DWORD)(uintptr_t)m.native0 + (DWORD)m.bytes) continue;
        ts3native_counters.jitFaults++;
        sprintf_s(out, n, "%s +0x%lX", m.name, eip - (DWORD)(uintptr_t)m.native0);
        return true;
    }
    return false;
}
// the template table for this build's opcode numbers (names as the host; [MonoJit] Groups = MONOHOST_JITC2's bits: 1 float
// fields + ldflda, 2 static fields, 4 array loads / stores, 8 stobj, 16 cknull / strlen / getchr, 32 div.un.i4 / rem.un.i4 /
// rem.r8, 64 switch, 128 the value-type moves and ceq.i8, 256 initobj / isinst / shr.un.i8 and the direct internal calls, 512 Wide, 1024 VtOps); returns the rows that found their opcode
static int JitTemplates(unsigned groups) {
    int n = 0;
    for (auto& t : kJitTmpl) {
        const unsigned g = t.kind >= K_FARITH ? 512 : t.kind == K_LDFLDA || !strcmp(t.name, "ldfld.r4") || !strcmp(t.name, "ldfld.r8") || !strcmp(t.name, "stfld.r4") || !strcmp(t.name, "stfld.r8") ? 1
            : t.kind == K_LDSFLD || t.kind == K_LDSFLDA || t.kind == K_STSFLD ? 2
            : t.kind == K_STELEM || t.kind == K_STELEMREF || !strcmp(t.name, "ldelem.r4") || !strcmp(t.name, "ldelem.r8") ? 4
            : t.kind == K_STOBJ ? 8 : t.kind == K_CKNULL || t.kind == K_STRLEN || t.kind == K_GETCHR ? 16
            : t.kind == K_DIVUN || t.kind == K_REMR8 ? 32 : t.kind == K_SWITCH ? 64
            : t.kind == K_LDVT || t.kind == K_STVT || t.kind == K_STINARGVT || t.kind == K_DUPVT || t.kind == K_VTRESULT || t.kind == K_CEQ8 || t.kind == K_LDFLDVT || t.kind == K_LDSFLDVT ? 128
            : t.kind >= K_STFLDVT && t.kind <= K_STOBJVT ? 1024
                : t.kind == K_FCMP || t.kind == K_FBRS || t.kind == K_FBR || t.kind == K_INITOBJ || t.kind == K_ISINST || t.kind == K_SHRUN8 || t.kind == K_LDFP || t.kind == K_STFP || t.kind == K_STINARGFP || t.kind == K_LDCFP ? 256 : 0;
        if (g & ~groups) continue;
        for (DWORD op = 0; op <= 0x1A8; op++)
            if (g_jitOpNames[op] && !strcmp(g_jitOpNames[op], t.name)) { g_opInfo[op] = {t.kind, t.width, t.x}; n++; break; }
    }
    return n;
}
// the region: the tables, the work area and the code, carved from one allocation
static bool JitAlloc() {
    const size_t tab = sizeof(JitEnt) * (JIT_TAB_N + 4096), meth = sizeof(JitMeth) * JIT_METH_N, pos = 2 * sizeof(unsigned short) * JIT_POS_N;
    const size_t cells = sizeof(JitLazyCell) * JIT_CELL_N, guards = sizeof(JitGuardSite) * JIT_GUARD_N;
    const size_t data = (tab + meth + pos + cells + guards + sizeof(JitScratch) + 0xFFF) & ~(size_t)0xFFF;
    u8* p = (u8*)VirtualAlloc(NULL, data + JIT_CODE_BYTES, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    if (!p) return false;
    g_jitTab = (JitEnt*)p;
    g_jitMeth = (JitMeth*)(p + tab);
    g_jitPos = (unsigned short*)(p + tab + meth);
    g_jitOrigW = g_jitPos + JIT_POS_N;
    g_jitLazyCell = (JitLazyCell*)(p + tab + meth + pos);
    g_jitGuard = (JitGuardSite*)(p + tab + meth + pos + cells);
    g_js = (JitScratch*)(p + tab + meth + pos + cells + guards);
    g_jitCode = p + data, g_jitCodeEnd = g_jitCode + JIT_CODE_BYTES, g_jitCodeUsed = 0;
    return true;
}
static_assert(JIT_POS_N <= (int)(JIT_TAB_N * 3 / 4), "the ip table stays under 3/4 full");
// verify only: GC_malloc_atomic -> GC_malloc (docs/57 §1, the host's JitAtomic), so a new pointer-free block's slack is the
// same in both passes. Not with [MonoThread] GcLock: its detour wants these bytes (the compare stops at the array's last element either way).
static void JitAtomic(const wchar_t* ini) {
    static const u8 kAtomic[9] = {0x8B, 0x44, 0x24, 0x04, 0xB9, 0x00, 0x08, 0x00, 0x00};
    const DWORD at = TS3A(0x00E4BDC0), to = TS3A(0x00E4BE30);
    if (GetPrivateProfileIntW(L"MonoThread", L"GcLock", 0, ini) || !Matches((void*)(uintptr_t)at, kAtomic, sizeof(kAtomic))) {
        Log("jit: GC_malloc_atomic left alone ([MonoThread] GcLock or other bytes)");
        return;
    }
    u8 jmp[5] = {0xE9};
    const LONG rel = (LONG)(to - (at + 5));
    memcpy(jmp + 1, &rel, 4);
    Write((void*)(uintptr_t)at, jmp, 5);
}
static void JitReadList(const wchar_t* ini) {
    wchar_t w[512] = L"";
    GetPrivateProfileStringW(L"MonoJit", L"List", L"", w, 512, ini);
    if (!w[0] || !WideCharToMultiByte(CP_ACP, 0, w, -1, g_jitUserBuf, sizeof(g_jitUserBuf), NULL, NULL)) return;
    char* ctx = NULL;
    for (char* t = strtok_s(g_jitUserBuf, ",", &ctx); t && g_jitUserN < 32; t = strtok_s(NULL, ",", &ctx)) g_jitUser[g_jitUserN++] = t;
}

static void JitCaptureOn(const wchar_t* ini) {
    wchar_t path[MAX_PATH] = L"";
    GetPrivateProfileStringW(L"MonoJit", L"CaptureFile", L"", path, MAX_PATH, ini);
    const auto num = [ini](const wchar_t* k, int d) { return (int)GetPrivateProfileIntW(L"MonoJit", k, d, ini); };
    if (!path[0] || !JcInit(path, num(L"CaptureFirst", 8), num(L"CaptureReservoir", 8), num(L"CaptureDepth", 4), num(L"CaptureObjs", 256), (DWORD)num(L"CaptureBytes", 65536),
                            (DWORD)num(L"CaptureObjMax", 16384), (DWORD)num(L"CaptureBudgetUs", 2000), (DWORD)num(L"CaptureMaxMb", 64) << 20)) {
        Log("SKIP mono jit capture: no CaptureFile, or it cannot be opened (err %lu)", GetLastError());
        return;
    }
    Log("OK   mono jit capture (docs/537 §17, §19): sampled at JitEnter%s%s", g_jitLazy ? ", lazy bodies" : "", g_jitInline ? ", inlined sites" : "");
}

// ts3native.ini [MonoJit] Enabled=1, Mode=1 compiled | 2 verify, VerifyEvery=<n>, Groups=<mask>, List=<names>, Direct=1 lazy frames,
// DirectLeaf=1 leaves only, Trap=1 the reader trap (TrapEvery=<n>), Inline=1|2 tiny callees at compiled call sites, Wide=1 the S5 templates and methods with clauses, Capture=1 argument samples to CaptureFile=<path> (docs/537 §17; CaptureFirst, CaptureReservoir, CaptureDepth, CaptureObjs, CaptureBytes, CaptureObjMax, CaptureBudgetUs, CaptureMaxMb). true = installed (keep the log). After every other code
// patch, before [AutShadow] (docs/527 §4.1), and only on the Steam 1.67 exe.
static bool ApplyMonoJit(const wchar_t* ini) {
    if (!GetPrivateProfileIntW(L"MonoJit", L"Enabled", 0, ini)) return false;
    Ts3nCounters& C = ts3native_counters;
    const int mode = GetPrivateProfileIntW(L"MonoJit", L"Mode", 1, ini);
    if (mode != 1 && mode != 2) { Log("SKIP mono jit: Mode=%d (1 compiled, 2 verify)", mode); return false; }
    const u8* base = (const u8*)GetModuleHandleW(NULL);
    const IMAGE_NT_HEADERS* nt = (const IMAGE_NT_HEADERS*)(base + ((const IMAGE_DOS_HEADER*)base)->e_lfanew);
    if (nt->FileHeader.TimeDateStamp != JIT_PE_STAMP || nt->OptionalHeader.SizeOfImage != JIT_PE_SIZE) { Log("SKIP mono jit: not the Steam 1.67 exe"); return false; }
    for (const auto& c : kMcCode)  // [GcRootCheck] TailWatch and any other owner of the call path
        if (McFnv((const u8*)(uintptr_t)c.at, c.n) != c.fnv && (!c.fnv2 || McFnv((const u8*)(uintptr_t)c.at, c.n) != c.fnv2)) {
            Log("SKIP mono jit: EA code at 0x%08lX (%lu bytes) is not the stock Steam 1.67 bytes", c.at, c.n);
            return false;
        }
    if (g_jitTableP[JIT_OP] != JIT_EA_UNIMPLEMENTED) { Log("SKIP mono jit: jump-table entry 0x%03lX is 0x%08lX, not EA's (another owner; [MonoFast] CallPath=2 has them all)", JIT_OP, g_jitTableP[JIT_OP]); return false; }
    if (!IlpShaInit()) { Log("SKIP mono jit: SHA-256 (bcrypt.dll) unavailable"); return false; }
    g_jitGroups = GetPrivateProfileIntW(L"MonoJit", L"Groups", 511, ini);
    g_jitWide = GetPrivateProfileIntW(L"MonoJit", L"Wide", 0, ini) != 0;
    if (g_jitWide) g_jitGroups |= 512, g_jitSse3 = IsProcessorFeaturePresent(13) != 0;  // PF_SSE3_INSTRUCTIONS_AVAILABLE
    if ((g_jitVtOps = GetPrivateProfileIntW(L"MonoJit", L"VtOps", 0, ini) != 0)) g_jitGroups |= 1024;  // docs/537 §20 B1
    const int templates = JitTemplates((unsigned)g_jitGroups);
    if (!templates) { Log("SKIP mono jit: no template matches EA's opcode names"); return false; }
    if (!JitAlloc()) { Log("SKIP mono jit: no address space for the region"); return false; }
    if (mode == 2) g_jitV = (JitV*)VirtualAlloc(NULL, sizeof(JitV) * JIT_VD, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    if (mode == 2 && !g_jitV) { Log("SKIP mono jit: no memory for the verifier"); return false; }
    InitializeCriticalSection(&g_jitCs);
    JitReadList(ini);
    if (!IlpHook()) { Log("SKIP mono jit: the transform prologue differs (another owner of 0xE643B0)"); return false; }
    if (!SimLoopAdd(JitTick, "mono jit: jitSwitch and the sim thread")) { Log("SKIP mono jit: no Simulate iteration hook"); return false; }
    const DWORD ent = (DWORD)(uintptr_t)JitEnter;
    if (!Write(g_jitTableP + JIT_OP, &ent, 4)) { Log("FAIL mono jit: jump-table entry (err %lu)", GetLastError()); return false; }
    if (mode == 2) {
        const int every = GetPrivateProfileIntW(L"MonoJit", L"VerifyEvery", 16, ini);
        g_jitEvery = every > 0 ? (DWORD)every : 1;
        g_jitVerifyOn = 1;
        JitAtomic(ini);
    }
    g_jitLazy = GetPrivateProfileIntW(L"MonoJit", L"Direct", 0, ini) != 0;
    g_jitLeafOnly = GetPrivateProfileIntW(L"MonoJit", L"DirectLeaf", 1, ini) != 0;
    g_jitInline = GetPrivateProfileIntW(L"MonoJit", L"Inline", 0, ini);
    g_jitCallExit = GetPrivateProfileIntW(L"MonoJit", L"CallExit", 0, ini) != 0;  // docs/537 §20 A
    g_jitPureIcalls = g_jitCallExit && JitPureIcallsOk();
    if (g_jitCallExit && !g_jitPureIcalls) Log("jit: CallExit: the Array icall bytes differ, pure icalls stay EA calls");
    g_jitNewVt = GetPrivateProfileIntW(L"MonoJit", L"NewVt", 0, ini) != 0;  // docs/537 §20 B2b (needs Direct=1)
    if (GetPrivateProfileIntW(L"MonoJit", L"Count", 0, ini) && !JcnOn()) Log("jit: Count: no memory for the tables (%u KB), count mode off", (unsigned)(sizeof(JcnTabs) >> 10));
    const bool fcWant = GetPrivateProfileIntW(L"MonoJit", L"FastCopy", 0, ini) != 0;  // docs/537 §20 C3
    g_jitFastCopy = fcWant && JitFastCopyOk();
    if (fcWant && !g_jitFastCopy) Log("jit: FastCopy: EA's Array.FastCopy bytes differ, it stays an EA call");
    g_jitLdsflda = GetPrivateProfileIntW(L"MonoJit", L"Ldsflda", 0, ini) != 0;  // docs/537 §20 C3
    C.jitDirect = g_jitLazy ? (g_jitLeafOnly ? 1 : 2) : 0;
    if (g_jitLazy && GetPrivateProfileIntW(L"MonoJit", L"Trap", 0, ini)) {
        const int every = GetPrivateProfileIntW(L"MonoJit", L"TrapEvery", 1024, ini);
        g_jitTrapEvery = every > 0 ? (DWORD)every : 1;
        if (!(g_jitTrapVeh = AddVectoredExceptionHandler(1, JitTrapVeh))) { Log("SKIP mono jit trap: AddVectoredExceptionHandler (err %lu)", GetLastError()); return false; }
        g_jitTrap = true;
        C.jitTrapOn = 1;
    }
    if (GetPrivateProfileIntW(L"MonoJit", L"Capture", 0, ini)) JitCaptureOn(ini);
    g_jitMode = mode;
    g_jitPatched = true;
    C.jitSwitch = 1;
    C.jitOn = mode;
    g_ilpPost = JitPostPass;
    Log("OK   mono jit (docs/527): Mode=%d, %d templates (Groups=%d), %d list rows%s, VerifyEvery=%lu, code region %d KB, Direct=%ld (docs/537), Trap=%d, VtOps=%d, CallExit=%d, NewVt=%d, Count=%d, FastCopy=%d, Ldsflda=%d", mode, templates, g_jitGroups, g_jitRowN, g_jitUserN ? " narrowed by List=" : "", g_jitEvery, JIT_CODE_BYTES >> 10, C.jitDirect, g_jitTrap ? 1 : 0, g_jitVtOps ? 1 : 0, g_jitCallExit ? 1 : 0, g_jitNewVt ? 1 : 0, g_jcn ? 1 : 0, g_jitFastCopy ? 1 : 0, g_jitLdsflda ? 1 : 0);
    return true;
}
