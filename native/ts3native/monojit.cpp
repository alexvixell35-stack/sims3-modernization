// docs/207, docs/527 step 1: the method compiler for the embedded mint interpreter, shared by the game (ts3native.cpp,
// [MonoJit], off by default) and native/monohost/monohost-jit.cpp (MONOHOST_JIT). Moved here from monohost-jit.cpp
// unchanged but for: JitEnter's fast path runs compiled code on g_jitMainTid only (every other thread's entry goes to
// EA's handler of the original opcode), verify sampling (g_jitVerifyEvery), a method with a verify mismatch unpatched
// (g_jitUnpatchBad), the code pool RW only while a method is written (g_jitProtect), JitSetup shared by both includers.
// The includer defines MJ_MT (the namespace prefix of satb.cpp's names: "mt::" in monohost, empty in the game), JitLog
// (one log line, no newline in the format) and JitTransformHook (the transform entry with the compiler after it), and
// includes this after satb.cpp.
//
// How it works (details in docs/207 §8):
// - a listed method gets compiled right after EA's transform 0xE643B0 + peephole.
// - every instruction start of a compiled method (but leave.s, see JitWord) gets its opcode word replaced by JIT_OP (0x1A9, an opcode EA never emits;
//   its jump-table entry points to JitEnter). The original words live in a shadow; nothing else in the code changes, so ip
//   values, operands, clause ranges and branch offsets stay EA's.
// - JitEnter maps edi (ip) to the native code of that instruction (hash) and jumps there. Native code runs inside the
//   interpreter's own C frame (ebp), with esi = eval stack pointer and every local, argument and stack slot in the
//   interpreter's frame memory, so the conservative GC sees exactly what it sees today.
// - unsupported opcodes, calls, returns and every throw path leave through "exit to handler": edi = ip, [ebp-4] = ip,
//   eax = edx = original opcode, jmp to EA's handler. The handler then dispatches the next word, which is JIT_OP again.
#ifndef MJ_MT
#error define MJ_MT (satb.cpp's namespace prefix), JitLog and JitTransformHook before including monojit.cpp
#endif

static const DWORD JIT_OP = 0x1A9, JIT_XFORM = 0xE643B0, OPLEN = 0x1195088, SUPERDESC = 0x108FCD8, JIT_TABLE = 0x00E59880;
static int g_jitMode;  // 0 off, 1 dump, 2 on, 3 verify
static int g_jitInline;  // MONOHOST_JITINLINE=1: inline tiny leaf callees at compiled call sites (docs/207 §8)
static const char* g_jitNames[128];
static int g_jitNameN;
static FILE* g_jitDump;
static CRITICAL_SECTION g_jitCs;

static const char* JitMethodName(const u8* rm, char* buf, int n) {  // "ns.Class.Method" (nested types: "Class.Method")
    const u8* mm = *(const u8* const*)(rm + 8);
    const u8* k = mm ? *(const u8* const*)(mm + 8) : NULL;
    const char* ns = k ? *(const char* const*)(k + 0x38) : NULL;
    const char* cn = k ? *(const char* const*)(k + 0x34) : NULL;
    const char* mn = mm ? *(const char* const*)(mm + 0x18) : NULL;
    // _TRUNCATE: sprintf_s on a name longer than buf (MjPost names every transformed method, explicit interface methods
    // carry the whole generic interface name) calls the CRT's invalid-parameter handler, which ends the process with
    // __fastfail 0xC0000409 past every filter. A cut name ends in "..." and so never matches a JitListed entry.
    if (_snprintf_s(buf, n, _TRUNCATE, "%s%s%s.%s", ns && *ns ? ns : "", ns && *ns ? "." : "", cn ? cn : "?", mn ? mn : "?") < 0 && n > 3)
        memcpy(buf + n - 4, "...", 4);
    return buf;
}
static bool JitListed(const char* full) {
    size_t fl = strlen(full);
    for (int i = 0; i < g_jitNameN; i++) {
        size_t l = strlen(g_jitNames[i]);
        if (l > 2 && !strcmp(g_jitNames[i] + l - 2, ".*")) {  // "Class.*": every method of that class (the B4 corpus kernels)
            const char* dot = strrchr(full, '.');
            size_t cl = dot ? (size_t)(dot - full) : 0;
            if (dot && cl >= l - 2 && !strncmp(dot - (l - 2), g_jitNames[i], l - 2) && (cl == l - 2 || dot[-(int)(l - 2) - 1] == '.'))
                return true;
            continue;
        }
        if (fl >= l && !strcmp(full + fl - l, g_jitNames[i]) && (fl == l || full[fl - l - 1] == '.')) return true;
    }
    return false;
}
// instruction length in words at p (superops: the summed base lengths); 0 = cannot walk (switch handled by the caller)
static int JitLen(const unsigned short* p) {
    unsigned op = *p;
    if (op == JIT_OP) return -1;
    if (op <= 0x1A8) return ((const u8*)(uintptr_t)OPLEN)[op];
    if (op >= 0x1AB && op <= 0x1C0) {
        const u8* e = (const u8*)(uintptr_t)(SUPERDESC + 0x48 * (op - 0x1AA));
        int m = *(const unsigned short*)(e + 4), len = 0;
        for (int k = 0; k < m; k++) len += ((const u8*)(uintptr_t)OPLEN)[((const unsigned short*)(e + 6))[k]];
        return len;
    }
    return -1;
}
static int JitSwitchLen(const unsigned short* p) { return 3 + 2 * (int)*(const DWORD*)(p + 1); }  // op, u32 n, n x i32 offsets


static bool g_jitLazy, g_jitLazyPass;  // MONOHOST_JITLAZY (docs/207 §12); the lazy body is being emitted
static void JitCompiled(u8* rm);  // compiler (below)
static void JitPreTransform(u8* rm, void* ctx);  // inliner (below)
// ---- round 10 C2 (docs/207 §13): static fields, stobj, the array and string stores ----
static void* g_jitDomain;       // the transform context's domain (EA's stsfld reads [ebp+0xC]->domain the same way); NULL until the first transform
static void* g_jitObjectClass;  // mono_defaults.object_class: an object[] takes any reference without the class check
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
    return hash && ((void*(__cdecl*)(void*, const void*))(uintptr_t)0xE45F00)(hash, field) != NULL;
}
// mono_class_vtable(domain, field->parent) as EA's stsfld and ldsflda call it every execution (0xEA2AD0); one vtable per
// domain and class, so the compiled code carries it as a constant. field+0x14 = parent, field+8 = offset [verified: the
// handlers at 0xE56F52 / 0xE56D20 / 0xE56E1E].
static void* JitStaticVt(const u8* field) {
    if (!g_jitDomain) return NULL;
    return ((void*(__cdecl*)(void*, const void*))(uintptr_t)0xEA2AD0)(g_jitDomain, *(const void* const*)(field + 0x14));
}


// ---- opcode templates ----
enum JitKind : u8 {
    K_EXIT, K_RET, K_LDARG, K_LDTHIS, K_LDARGA, K_LDLOC, K_LDLOCA, K_STLOC, K_STLOCNP, K_STINARG, K_LDFLD, K_STFLD, K_LDC,
    K_LDCS, K_LDCI4, K_DUP, K_POP, K_BRS, K_BR, K_BTS, K_BT, K_BFS, K_BF, K_BCS, K_BC, K_LDLEN, K_LDELEM, K_LDIND, K_STIND,
    K_BIN, K_BIN8, K_MUL, K_INC, K_DEC, K_SHIFT, K_UN, K_CMP, K_CEQ0, K_CONV,
    K_LDSFLD, K_LDSFLDA, K_STSFLD, K_STELEM, K_STELEMREF, K_STOBJ, K_LDFLDA, K_CKNULL, K_STRLEN, K_GETCHR,  // round 10 C2
    K_DIVUN, K_REMR8, K_SWITCH,  // round 10 C2, the throw-only handlers, rem.r8 and switch
    K_LDVT, K_STVT, K_STINARGVT, K_DUPVT, K_VTRESULT, K_CEQ8, K_LDFLDVT, K_LDSFLDVT  // round 10 C1, the value-type moves and ceq.i8 (docs/207 §15)
};
// width: 1 = i1, 2 = u1, 3 = i2, 4 = u2, 5 = 32-bit, 6 = 64-bit, 7 = r4 (x87), 8 = typed by the field or class at compile
// time (JitTypeWidth); x = kind-specific byte (cc, alu op, imm)
struct JitOpInfo { u8 kind, width; int x; };
static JitOpInfo g_opInfo[0x1C1];
static void* g_opHandler[0x1C1];  // EA handlers, read from the jump table before JIT_OP is installed

// ---- [AutShadow] Satb (docs/206 §7, docs/507 B3): compiled code and the barrier ----
// The sim's reference stores must be logged while a span is on; Satb gets the interpreter's by swapping the store handlers
// in the jump table, which compiled stores never reach. So: (1) stfld / stind templates call JitSatbStore before the
// store (SatbSimRange: frame-arena targets are private and skipped, a full log or a big store waits as the stubs do);
// (2) every exit to a handler goes through the live jump table, not the entries read at install, so a store opcode the
// compiler does not cover meets Satb's stub; (3) JitEnter sends the worker thread back to the live handler of the
// original opcode, so the worker's loads keep their redirect (act) and it never runs compiled code during a span.
static volatile LONG g_jitSatbStores, g_jitSatbBails;
static volatile LONG* const g_jitSatbOnP = &MJ_MT g_satbOn;
static volatile DWORD* const g_jitSatbWTidP = &MJ_MT g_satbWTid;
static void __cdecl JitSatbStore(DWORD addr, DWORD n) {
    if (!MJ_MT SatbSimSide()) return;
    g_jitSatbStores++;
    MJ_MT SatbSimRange(addr, n);
}
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
    void satb(int w, int base, int disp) {
        b(0x83); b(0x3D); d((DWORD)(uintptr_t)g_jitSatbOnP); b(0);  // cmp dword ptr [g_satbOn], 0
        b(0x74); u8* skip = p; b(0);                                 // je skip
        b(0x50 + base); op(0x8D, EDX, base, disp);                   // push base; lea edx, [base+disp]
        b(0x6A); b(w <= 2 ? 1 : w <= 4 ? 2 : w == 5 || w == 7 ? 4 : 8); b(0x52);  // push bytes; push edx
        b(0xE8); d((DWORD)(uintptr_t)JitSatbStore - ((DWORD)(uintptr_t)p + 4));  // call JitSatbStore
        b(0x83); b(0xC4); b(8); b(0x58 + base);                      // add esp, 8; pop base
        *skip = (u8)(p - (skip + 1));
    }
};
static void Fix(u8* rel, const u8* target) { *(int*)rel = (int)(target - (rel + 4)); }

// ---- compiled method table: ip -> native (open addressing, 16-byte entries) ----
struct JitEnt { DWORD ip; u8* native; DWORD orig; u8* rm; };
static const DWORD JIT_TAB_N = 1 << 18;  // JitEnter masks with 0x3FFFF
static JitEnt* g_jitTab;  // JIT_TAB_N entries + a 4096-entry overflow tail (no wrap in the probe loop)
static int g_jitEntN;
static u8* g_jitCode;     // native code pool (PAGE_EXECUTE_READWRITE in the host; the game would flip RW -> RX)
static u8* g_jitCodeEnd;
static size_t g_jitCodeUsed;
static bool g_jitProtect;  // game: the pool is RX except while JitCompiled writes it (compiled code runs on that thread only)
struct JitProtScope {  // the pages one compile may write: twice the pool-full bound, capped at the pool end
    u8* at;
    size_t n;
    JitProtScope(u8* a, size_t k) : at(a), n(k) {
        if (at + n > g_jitCodeEnd) n = (size_t)(g_jitCodeEnd - at);
        DWORD old;
        if (g_jitProtect) VirtualProtect(at, n, PAGE_READWRITE, &old);
    }
    ~JitProtScope() { DWORD old; if (g_jitProtect) VirtualProtect(at, n, PAGE_EXECUTE_READ, &old); }
};
struct JitMeth { u8* rm; unsigned short* code; int words; int pos0, posN; char name[96]; int nativeOps, exitOps, bytes; bool verifyOk;
                 volatile LONG verified, mismatches, skipped, inner, seen; bool dead; };
static const int JIT_METH_N = 16384;  // the game compiles every instantiation of a listed generic (al253: 1,793 in 5 min)
static JitMeth g_jitMeth[JIT_METH_N];
static int g_jitMethN;
static const int JIT_POS_N = 1 << 20;
static const DWORD JIT_CODE_BYTES = 16 << 20;
static unsigned short* g_jitPos;  // positions of all compiled methods (word pointers), [pos0, posN) per method
static unsigned short* g_jitOrigW;
static int g_jitPosN;
static bool g_jitPatched;
static bool g_jitExitAll;  // MONOHOST_JITEXITALL=1: every instruction compiles to an exit (switch-cost measurement)
static volatile LONG g_jitEnterN, g_jitExitN, g_jitCountOn;
// count mode (MONOHOST_JITCOUNT=1, B1 exit census): one counter per emitted exit, plus calls per compiled method
struct JitSite { int meth; unsigned short op; bool slow; volatile LONG n, miss; const unsigned short* ip; };
static JitSite* g_jitSite;
static int g_jitSiteN, g_jitSiteSlow;
static const u8* g_jitSiteRm;  // the method being compiled (callvirt sites read its inline-cache pairs)
static volatile LONG g_jitCalls[4096], g_jitPosRun[4096];  // count mode: calls (entries at word 0), positions run (B1)

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
    g_jitEntN++;
    return &g_jitTab[i];
}

static void JitVerifyEnterStub();
static void JitVerifyExitStub();

// ---- lazy frames (B3, docs/207 §12, MONOHOST_JITLAZY=1): a compiled call site whose callee is compiled calls the callee's
// lazy body natively, with no frame record, MonoInvocation, depth or ThreadContext write. The callee's frame is written
// (JitLazyMat, the same words EA's push writes) only at the callee's first exit to an EA handler; the native call is then
// abandoned and the caller resumes through EA's return, as after any interpreted call. v1: lazy bodies only for methods
// whose every position compiles natively or is ret / ret.void (static leaves), and only for direct call sites. v2 (docs/207
// §12.7): a lazy body may itself make lazy calls (the block's level counts the frames pending below it, and JitLazyMat
// writes them outermost first), con.call is a direct site with the method in word 2, and a callvirt of a virtual method
// goes through a class guard on EA's inline cache (JitGuardSite).
// Block the call site pushes (the lazy body sees it at [esp+4]; it pushes nothing itself, so in a chain the outer block
// sits right above the return address into the outer lazy body):
enum { B_ARGS = 0, B_THIS = 4, B_RIP = 8, B_RSP = 12, B_RSLOT = 16, B_RM = 20, B_EBX = 24, B_EDI = 28, B_CIP = 32, B_LVL = 36, B_VT = 40, B_SIZE = 44 };
// B_VT (round 10 C1): the lazy body's vt cursor, the word the interpreter keeps at [ebp-0xC] for a written frame; the site
// starts it at the callee's vt area, the value-type moves of the body advance it, JitLazyMat copies it into the frame.
static void JitLazyMat();
// the value-type moves' pieces: the vt cursor ([ebp-0xC], or the block's B_VT in a lazy body), a memcpy through EA's CRT
// thunk (0xF7E8A6, cdecl: eax, ecx, edx go). The block reads happen with esp at the body's entry value (before the pushes
// or after the add esp, 12), so the opEsp offsets hold.
static void VtLoad(Em& e, int r) { if (g_jitLazyPass) e.opEsp(0x8B, r, 4 + B_VT); else e.op(0x8B, r, EBP, -0xC); }
static void VtStore(Em& e, int r) { if (g_jitLazyPass) e.opEsp(0x89, r, 4 + B_VT); else e.op(0x89, r, EBP, -0xC); }
static void VtAdd(Em& e, int n) {  // add dword [cursor], n
    if (!n) return;
    if (g_jitLazyPass) { e.b(0x81); e.b(0x84); e.b(0x24); e.d(4 + B_VT); } else { e.b(0x81); e.m(0, EBP, -0xC); }
    e.d((DWORD)n);
}
static void VtCopy(Em& e, int dst, int src, DWORD n) {  // memcpy(dst, src, n): push n; push src; push dst; call thunk; add esp, 12
    e.b(0x68); e.d(n); e.b(0x50 + src); e.b(0x50 + dst);
    e.b(0xE8); e.d(0xF7E8A6 - ((DWORD)(uintptr_t)e.p + 4));
    e.b(0x83); e.b(0xC4); e.b(12);
}
static bool g_jitDumpCode;  // MONOHOST_JITDUMPCODE=1
struct JitLazyCell { const u8* rm; u8* entry; };  // callee -> its lazy body (NULL until compiled); call sites read entry
static JitLazyCell g_jitLazyCell[8192];
static int g_jitLazyCellN, g_jitLazyBodies, g_jitLazySites;
static volatile LONG g_jitLazyCalls, g_jitLazyMats;
static JitLazyCell* JitLazyCellFor(const u8* rm) {
    for (int i = 0; i < g_jitLazyCellN; i++) if (g_jitLazyCell[i].rm == rm) return &g_jitLazyCell[i];
    if (g_jitLazyCellN >= 8192) return NULL;
    g_jitLazyCell[g_jitLazyCellN].rm = rm;
    return &g_jitLazyCell[g_jitLazyCellN++];
}
// v2: a callvirt site of a virtual method. The site compares the receiver's class with EA's inline-cache class and takes
// the cache's target; when that is not the site's method yet, JitGuardFill looks the target's lazy body and frame sizes up
// (once per target change). A target that is not transformed yet leaves rm NULL (asked again at the next call, after EA's
// call has transformed it); one with no lazy body leaves entry NULL (the site bails until the cache changes, or until the
// target compiles: the publish step refills). Counters: lazy calls made from a lazy body and through a guard (count mode).
struct JitGuardSite { const u8* rm; u8* entry; DWORD w38, w3E, wLoc, w3C, wVt; volatile LONG fills; };
static JitGuardSite g_jitGuard[4096];
static int g_jitGuardN, g_jitLazyGuardSites;
static volatile LONG g_jitLazyChain, g_jitLazyGuard;
static void __cdecl JitGuardFill(JitGuardSite* s, const u8* rm) {
    s->rm = NULL, s->entry = NULL, s->fills++;
    if (!rm) return;
    const DWORD fl = *(const DWORD*)(rm + 0x44);
    if (!(fl & 0x100)) return;
    s->rm = rm;
    if (!(fl & 0x200) || *(const DWORD*)(rm + 0x24)) return;
    JitLazyCell* cell = JitLazyCellFor(rm);
    if (!cell || !cell->entry) return;
    s->w38 = *(const unsigned short*)(rm + 0x38), s->w3E = *(const unsigned short*)(rm + 0x3E);
    s->wVt = s->w3E + *(const unsigned short*)(rm + 0x34);
    s->wLoc = s->wVt + *(const unsigned short*)(rm + 0x36);
    s->w3C = (fl & 0x800) ? *(const unsigned short*)(rm + 0x3C) : 0;
    s->entry = cell->entry;
}

static bool JitConRef(const u8* k);
// exit to EA's handler of base opcode `op` at ip (state is exactly the interpreter's: esi, frame memory)
static void EmitExit(Em& e, const unsigned short* ip, DWORD op, bool ret) {
    if (g_jitLazyPass) {  // lazy body: write the frame first (JitLazyMat), then the same exit
        e.b(0xBF); e.d((DWORD)(uintptr_t)ip);
        e.movRI(EAX, op);
        e.jmpTo((const void*)JitLazyMat);
        return;
    }
    if (g_jitCountOn) e.incAbs(&g_jitExitN);
    if (g_jitCountOn && g_jitSite && g_jitSiteN < 65536) {  // meth = the index this method gets if it compiles
        JitSite& s = g_jitSite[g_jitSiteN++];
        s.meth = g_jitMethN, s.op = (unsigned short)op, s.slow = g_jitSiteSlow != 0, s.n = 0, s.miss = 0, s.ip = ip;
        e.incAbs(&s.n);
        const DWORD* sitems = g_jitSiteRm ? *(const DWORD* const*)(g_jitSiteRm + 0x14) : NULL;
        if ((op == 0xEC || (op == 0xED && JitConRef((const u8*)(uintptr_t)sitems[ip[3]]))) && !s.slow && g_jitSiteRm) {
            // B3 edge census: receiver class != the site's inline-cache class (con.callvirt: the receiver is *ptr)
            const u8* decl = (const u8*)(uintptr_t)sitems[op == 0xEC ? ip[3] : ip[4]];
            e.op(0x8B, EAX, ESI, -(decl[0x40] + 1) * 8);
            e.rr(0x85, EAX, EAX); u8* j0 = e.jcc(0x84);
            u8* jn = NULL;
            if (op == 0xED) { e.op(0x8B, EAX, EAX, 0); e.rr(0x85, EAX, EAX); jn = e.jcc(0x84); }
            e.op(0x8B, EAX, EAX, 0); e.op(0x8B, EAX, EAX, 0);
            e.b(0x3B); e.b(0x05); e.d((DWORD)(uintptr_t)&sitems[ip[1]]); u8* j1 = e.jcc(0x84);  // cmp eax, [pair.class]
            e.incAbs(&s.miss);
            Fix(j0, e.p), Fix(j1, e.p);
            if (jn) Fix(jn, e.p);
        }
    }
    e.b(0xBF); e.d((DWORD)(uintptr_t)ip);   // mov edi, ip
    if (ret && g_jitMode == 3) { e.jmpTo((const void*)JitVerifyExitStub); return; }
    e.op(0x89, EDI, EBP, -4);                // mov [ebp-4], edi
    e.movRI(EAX, op);
    e.rr(0x8B, EAX, EDX);                    // mov edx, eax (rr: r/m operand first, reg operand second)
    e.b(0xFF); e.b(0x25); e.d(JIT_TABLE + op * 4);  // jmp dword ptr [table + op*4]: the live entry (Satb's stub in a span)
}

// ---- inlining of tiny leaf instance methods at compiled call / callvirt sites (docs/207 §8, MONOHOST_JITINLINE=1) ----
// Callee: transformed, instance, no EA native body, no clauses, body = stinarg* then at most 12 of ldthis / ldarg (32-bit) /
// ldfld (32-bit, on this) / ldc / 32-bit ALU, compares, inc/dec/neg/not, narrow conv, pop, then one ret (value) or ret.void,
// no branch, call, store, static or anything that can throw once this is known non-null (ldfld only on this). callvirt
// only for a non-virtual declared method of a reference type. Inline code: this null -> the normal call path (EA throws);
// the callee frame must fit in the current arena chunk (else EA's path takes another chunk, whose stale bytes the 8-byte
// return copy would carry): the result slot gets the low dword computed here and the high dword EA's return copies from
// the callee's eval slot 0 (top + eval offset + 4), so the caller's eval stack is bit-equal to EA's call.
static volatile LONG g_jitInlined, g_jitInlineNo;
static unsigned short JitOrigWord(const unsigned short* p) {
    if (*p != JIT_OP) return *p;
    const JitEnt* en = JitFind((DWORD)(uintptr_t)p);
    return en ? (unsigned short)en->orig : 0xFFFF;
}
static const u8* JitInlineCallee(const unsigned short* ip, DWORD op, const u8* callerRm) {
    const DWORD* items = *(const DWORD* const*)(callerRm + 0x14);
    const u8* c = (const u8*)(uintptr_t)items[op == 0xEA ? ip[1] : ip[3]];
    if (!c) return NULL;
    const DWORD fl = *(const DWORD*)(c + 0x44);
    const u8* mm = *(const u8* const*)(c + 8);
    if (!(fl & 0x200) || !(fl & 0x100) || *(const DWORD*)(c + 0x24) || c[0x41] || !mm) return NULL;  // instance, transformed, no native body, no clauses
    if (op == 0xEC && ((fl & 0x400) || (*(const unsigned short*)mm & 0x40))) return NULL;          // callvirt: non-virtual, reference type
    return c;
}
static bool JitTryInline(Em& e, const unsigned short* ip, DWORD op, const u8* callerRm) {
    const u8* c = JitInlineCallee(ip, op, callerRm);
    if (!c) return false;
    const unsigned short* code = *(const unsigned short* const*)c;
    const int words = *(const unsigned short*)(c + 0x3A), n = c[0x40];
    const DWORD* argOff = *(const DWORD* const*)(c + 0x1C);
    const bool value = c[0x44] != 1;
    // decode into base ops (superops expanded), original words where the callee is compiled
    unsigned short ops[24];
    const unsigned short* at[24];
    int no = 0;
    for (int i = 0; i < words;) {
        unsigned short w = JitOrigWord(code + i);
        if (w == 0xE8 || w > 0x1C0 || w == 0x1A9 || w == 0x1AA) return g_jitInlineNo++, false;
        unsigned short tmp[2] = {w, 0};
        int len = w <= 0x1A8 ? ((const u8*)(uintptr_t)OPLEN)[w] : JitLen(tmp);
        if (len <= 0) return g_jitInlineNo++, false;
        if (w >= 0x1AB) {
            const u8* d = (const u8*)(uintptr_t)(SUPERDESC + 0x48 * (w - 0x1AA));
            int m = *(const unsigned short*)(d + 4), p = i;
            for (int k = 0; k < m && no < 24; k++) {
                unsigned short b = ((const unsigned short*)(d + 6))[k];
                ops[no] = b, at[no++] = code + p;
                p += ((const u8*)(uintptr_t)OPLEN)[b];
            }
        } else if (no < 24)
            ops[no] = w, at[no++] = code + i;
        else
            return g_jitInlineNo++, false;
        i += len;
    }
    // emit into a scratch buffer first (no partial code on refusal)
    static u8 buf[512];
    Em b = {buf};
    u8* slow[4];
    int ns = 0;
    const int thisOff = -(n + 1) * 8;  // receiver slot relative to esi
    b.op(0x8B, EAX, ESI, thisOff);                                     // mov eax, this
    b.rr(0x85, EAX, EAX); slow[ns++] = b.jcc(0x84);                   // null: EA's path throws
    b.b(0x83); b.b(0x3D); b.d(0x01220EC0); b.b(0); slow[ns++] = b.jcc(0x85);  // remoting class set: EA's path
    const unsigned short w38 = *(const unsigned short*)(c + 0x38), w3E = *(const unsigned short*)(c + 0x3E);
    if (value) {  // high dword of EA's return copy: callee eval slot 0, only if the frame fits in this chunk
        b.op(0x8B, ECX, EBP, 8); b.op(0x8B, ECX, ECX, 0x13C);          // ecx = state->chunk
        b.rr(0x85, ECX, ECX); slow[ns++] = b.jcc(0x84);
        b.op(0x8B, EDX, ECX, 0); b.b(0x81); b.b(0xC2); b.d(w38);         // edx = top + frame size
        b.op(0x3B, EDX, ECX, 4); slow[ns++] = b.jcc(0x87);             // > end: another chunk
        b.op(0x8B, EDX, ECX, 0); b.op(0x8B, EDX, EDX, w3E + 4);         // edx = [top + eval + 4]
        b.op(0x89, EDX, ESI, thisOff + 4);
    }
    int sp = 0;              // compile-time stack depth; entries: ECX, EDX
    bool isThis[3] = {};     // entry holds `this`
    const int regs[2] = {ECX, EDX};
    int k = 0, ret = -1;
    for (; k < no && ops[k] >= 0 && g_opInfo[ops[k]].kind == K_STINARG; k++) {}
    if (no - k > 12) return g_jitInlineNo++, false;
    for (; k < no; k++) {
        const JitOpInfo t = g_opInfo[ops[k]];
        const int w = t.width, u16 = at[k][1];
        int r = sp < 2 ? regs[sp] : -1, r0 = sp >= 2 ? regs[sp - 2] : -1, r1 = sp >= 1 ? regs[sp - 1] : -1;
        switch (t.kind) {
        case K_LDTHIS: if (r < 0) return g_jitInlineNo++, false; b.rr(0x8B, EAX, r); isThis[sp++] = true; break;  // mov r, eax
        case K_LDARG: {
            int idx = -1;
            for (int a = 0; a < n; a++) if ((int)argOff[a] == u16) idx = a;
            if (r < 0 || idx < 0 || w == 6) return g_jitInlineNo++, false;
            b.load(w, r, ESI, -(n - idx) * 8); isThis[sp++] = false; break;
        }
        case K_LDFLD:
            if (sp < 1 || !isThis[sp - 1] || w == 6) return g_jitInlineNo++, false;
            b.load(w, r1, EAX, u16); isThis[sp - 1] = false; break;
        case K_LDC: if (r < 0) return g_jitInlineNo++, false; b.movRI(r, (DWORD)t.x); isThis[sp++] = false; break;
        case K_LDCS: if (r < 0) return g_jitInlineNo++, false; b.movRI(r, (DWORD)(int)(short)u16); isThis[sp++] = false; break;
        case K_LDCI4: if (r < 0) return g_jitInlineNo++, false; b.movRI(r, *(const DWORD*)(at[k] + 1)); isThis[sp++] = false; break;
        case K_BIN: if (sp < 2) return g_jitInlineNo++, false; b.rr(t.x, r0, r1); sp--; isThis[sp - 1] = false; break;
        case K_MUL: if (sp < 2) return g_jitInlineNo++, false; b.b(0x0F); b.b(0xAF); b.b(0xC0 | (r0 << 3) | r1); sp--; isThis[sp - 1] = false; break;
        case K_CMP:
            if (sp < 2) return g_jitInlineNo++, false;
            b.rr(0x39, r0, r1); b.b(0x0F); b.b(t.x); b.b(0xC0 | r0); b.b(0x0F); b.b(0xB6); b.b(0xC0 | (r0 << 3) | r0);
            sp--; isThis[sp - 1] = false; break;
        case K_CEQ0:
            if (sp < 1) return g_jitInlineNo++, false;
            b.rr(0x85, r1, r1); b.b(0x0F); b.b(0x94); b.b(0xC0 | r1); b.b(0x0F); b.b(0xB6); b.b(0xC0 | (r1 << 3) | r1);
            isThis[sp - 1] = false; break;
        case K_INC: case K_DEC: if (sp < 1) return g_jitInlineNo++, false; b.b(0x83); b.b((t.kind == K_INC ? 0xC0 : 0xE8) | r1); b.b(1); isThis[sp - 1] = false; break;
        case K_UN: if (sp < 1) return g_jitInlineNo++, false; b.b(0xF7); b.b(0xC0 | (t.x << 3) | r1); isThis[sp - 1] = false; break;
        case K_CONV:
            if (sp < 1 || t.x == 1 || t.x == 2) return g_jitInlineNo++, false;
            if (t.x >= 4) { static const u8 kx[4] = {0xBE, 0xB6, 0xBF, 0xB7}; b.b(0x0F); b.b(kx[t.x - 4]); b.b(0xC0 | (r1 << 3) | r1); }
            isThis[sp - 1] = false; break;
        case K_POP: if (sp < 1) return g_jitInlineNo++, false; sp--; break;
        case K_RET: ret = k; break;
        default: return g_jitInlineNo++, false;
        }
        if (ret >= 0) break;
    }
    const char* rn = ret >= 0 ? ((const char* const*)(uintptr_t)0x11949E0)[ops[ret]] : "";
    if (ret != no - 1 || (value ? strcmp(rn, "ret") || sp != 1 : strcmp(rn, "ret.void") || sp != 0)) return g_jitInlineNo++, false;
    if (value) b.op(0x89, regs[0], ESI, thisOff);                      // result low dword into the receiver slot
    const int dsp = thisOff + (value ? 8 : 0);
    if (dsp) { b.b(0x81); b.b(dsp > 0 ? 0xC6 : 0xEE); b.d((DWORD)(dsp > 0 ? dsp : -dsp)); }
    // copy into the method's code: inline body, jmp over the out-of-line call (the normal call path from the same state)
    const int len = (int)(b.p - buf);
    u8* base = e.p;
    memcpy(e.p, buf, len);  // no relative operand inside except the slow-path jccs, fixed below
    e.p += len;
    u8* skip = e.jmp();
    for (int s = 0; s < ns; s++) Fix(slow[s] - buf + base, e.p);
    g_jitSiteSlow = 1;
    EmitExit(e, ip, op, false);
    g_jitSiteSlow = 0;
    Fix(skip, e.p);
    g_jitInlined++;
    return true;
}
// con.callvirt (0xED, round 10 C1 leftovers): EA's callvirt handler 0xE549EB serves both and reads the constrained class
// from word 3 and the method from word 4 (width 5). A struct class (0xE52160: byval type VALUETYPE and not an enum, or a
// generic instance of a struct) is copied to the vt stack; any other type goes through stackval_from_data (0xE521A0),
// which for a reference type stores *ptr in the receiver slot, and then the callvirt path runs. Only reference types
// compile here (decided at compile time): the site loads this = *ptr and writes it to the slot at commit, as EA does.
static bool JitConRef(const u8* k) {
    if (!k) return false;
    const u8 t = k[0xA6];  // byval_arg.type
    if (t == 0x15) {       // GENERICINST: the container class decides (0xE52160 reads its valuetype bit)
        const u8* gc = *(const u8* const*)(k + 0xA0);
        const u8* cc = gc ? *(const u8* const*)(gc + 4) : NULL;
        return cc && !(cc[0x14] & 8);
    }
    return t == 0x0E || t == 0x12 || t == 0x14 || t == 0x1C || t == 0x1D;  // string, class, array, object, szarray
}
// a lazy call site's target and kind: call (0xEA, method in word 1), con.call (0xEB: the class word is skipped and the
// method is word 2, docs/213 §1 row 3), callvirt (0xEC, declared method in word 3) or con.callvirt of a reference type
// (0xED, method in word 4) of a non-virtual method of a reference type (direct) or of a virtual one (class guard on EA's
// inline cache, v2). A direct target must be transformed (the site bakes in its frame sizes); a guarded site reads them
// from its JitGuardSite at run time.
static bool JitLazySite(const unsigned short* ip, DWORD op, const u8* callerRm, const u8** cOut, bool* guardOut) {
    const DWORD* items = *(const DWORD* const*)(callerRm + 0x14);
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
    if (!guard && !(fl & 0x100)) return false;  // not transformed: its frame sizes are still 0
    if (!guard && *(const DWORD*)(c + 0x24)) return false;  // EA native body: EA's call (dispatcher 0xE525C0, its exception frame); guards: JitGuardFill
    *cOut = c, *guardOut = guard;
    return true;
}
// lazy call site. Every test EA's call path (and docs/139 McCall) makes runs before any write; any of them, or no lazy
// body yet, takes the normal exit. In a lazy body (a chain) the depth test adds the frames pending below this one, the
// block's level is one higher, and the return-slot word is this body's own vt cursor (its frame is not written).
// push dword [esp + 4 + off + pushed]: word `off` of this lazy body's own block once `pushed` bytes of a new block are on the
// stack (push [esp+d] reads at the current esp). Was 4 + B_LVL after B_VT's push: a chained site took its block's B_CIP
// (an ip) as the level, so a third-level site's depth check compared garbage and, with ip >= 0x80000000, passed.
static void PushOwnBlk(Em& e, int pushed, int off) { e.b(0xFF); e.b(0xB4); e.b(0x24); e.d((DWORD)(4 + off + pushed)); }
static bool EmitLazyCall(Em& e, const unsigned short* ip, DWORD op, const u8* callerRm) {
    const u8* c; bool guard;
    if (!JitLazySite(ip, op, callerRm, &c, &guard)) return false;
    const DWORD fl = *(const DWORD*)(c + 0x44);
    const bool inst = (fl & 0x200) != 0, vtm = (fl & 0x400) != 0, lazy = g_jitLazyPass;
    JitLazyCell* cell = NULL;
    JitGuardSite* gs = NULL;
    if (guard) { if (g_jitGuardN >= 4096) return false; gs = &g_jitGuard[g_jitGuardN++]; }
    else if (!(cell = JitLazyCellFor(c))) return false;
    const DWORD* items = *(const DWORD* const*)(callerRm + 0x14);
    const int n = c[0x40], thisOff = -(n + 1) * 8, rsp = inst ? thisOff : -n * 8, ripW = op == 0xEA ? 2 : op == 0xEB ? 3 : op == 0xEC ? 4 : 5;
    const unsigned short w38 = *(const unsigned short*)(c + 0x38), w3E = *(const unsigned short*)(c + 0x3E);
    const unsigned short w34 = *(const unsigned short*)(c + 0x34), w36 = *(const unsigned short*)(c + 0x36);
    u8* bail[16];
    int nb = 0;
    if (guard) {  // receiver not null, with a vtable, not the remoting class, not a value type, of the cache's class; target = the cache's
        e.op(0x8B, ECX, ESI, thisOff); e.rr(0x85, ECX, ECX); bail[nb++] = e.jcc(0x84);
        if (op == 0xED) { e.op(0x8B, ECX, ECX, 0); e.rr(0x85, ECX, ECX); bail[nb++] = e.jcc(0x84); }  // this = *ptr
        e.op(0x8B, EAX, ECX, 0); e.rr(0x85, EAX, EAX); bail[nb++] = e.jcc(0x84);
        e.op(0x8B, EAX, EAX, 0); e.b(0x3B); e.b(0x05); e.d(0x01220EC0); bail[nb++] = e.jcc(0x84);
        e.b(0xF6); e.m(0, EAX, 0x14); e.b(8); bail[nb++] = e.jcc(0x85);
        e.b(0x3B); e.b(0x05); e.d((DWORD)(uintptr_t)&items[ip[1]]); bail[nb++] = e.jcc(0x85);  // cmp eax, [cache.class]
        e.b(0xA1); e.d((DWORD)(uintptr_t)&items[ip[1] + 1]);                                   // mov eax, [cache.target]
        e.b(0x3B); e.b(0x05); e.d((DWORD)(uintptr_t)&gs->rm); u8* same = e.jcc(0x84);
        e.b(0x60); e.b(0x50); e.b(0x68); e.d((DWORD)(uintptr_t)gs);                             // pushad; push eax; push site
        e.b(0xE8); e.d((DWORD)(uintptr_t)JitGuardFill - ((DWORD)(uintptr_t)e.p + 4));
        e.b(0x83); e.b(0xC4); e.b(8); e.b(0x61);                                                // add esp, 8; popad
        Fix(same, e.p);
        e.b(0x83); e.b(0x3D); e.d((DWORD)(uintptr_t)&gs->entry); e.b(0); bail[nb++] = e.jcc(0x84);  // no lazy body (yet)
    } else {
        e.b(0x83); e.b(0x3D); e.d((DWORD)(uintptr_t)&cell->entry); e.b(0); bail[nb++] = e.jcc(0x84);  // no lazy body (yet)
    }
    e.b(0x80); e.b(0x3D); e.d(0x00E5637D); e.b(0x75); bail[nb++] = e.jcc(0x85);                  // MonoPatcher ForceJIT active
    if (!guard) {
        if (inst && !vtm) {  // EA's remoting check reads this->vtable->klass (null: EA's path faults or throws)
            e.op(0x8B, ECX, ESI, thisOff); e.rr(0x85, ECX, ECX); bail[nb++] = e.jcc(0x84);
            if (op == 0xED) { e.op(0x8B, ECX, ECX, 0); e.rr(0x85, ECX, ECX); bail[nb++] = e.jcc(0x84); }  // this = *ptr
            e.op(0x8B, EAX, ECX, 0); e.rr(0x85, EAX, EAX); bail[nb++] = e.jcc(0x84);
            e.op(0x8B, EAX, EAX, 0); e.b(0x3B); e.b(0x05); e.d(0x01220EC0); bail[nb++] = e.jcc(0x84);
            if (op >= 0xEC) { e.b(0xF6); e.m(0, EAX, 0x14); e.b(8); bail[nb++] = e.jcc(0x85); }  // value-type receiver
        } else if (inst)
            e.op(0x8B, ECX, ESI, thisOff);  // a value-type method: this is a pointer (con.call too)
        else
            e.rr(0x33, ECX, ECX);
    }
    e.op(0x8B, EDX, EBP, 8);
    e.op(0x8B, EAX, EDX, 0x140);
    if (lazy) e.opEsp(0x03, EAX, 4 + B_LVL);                      // + the frames pending below this body
    e.b(0x40);                                                    // eax = depth + 1 (+ pending)
    e.op(0x3B, EAX, EBP, -0x30); bail[nb++] = e.jcc(0x8D);        // the slot must hold a MonoInvocation (JitLazyMat needs it; slots fill in order)
    e.b(0x69); e.b(0xC0); e.d(0x130);                             // imul eax, eax, 0x130
    e.b(0x83); e.b(0xBC); e.b(0x05); e.d((DWORD)-0x4C74); e.b(0); bail[nb++] = e.jcc(0x84);  // cmp dword [ebp+eax-0x4C74], 0
    e.op(0x8B, EDX, EDX, 0x13C); e.rr(0x85, EDX, EDX); bail[nb++] = e.jcc(0x84);              // arena chunk with room
    e.op(0x8B, EAX, EDX, 0);
    if (guard) { e.b(0x03); e.b(0x05); e.d((DWORD)(uintptr_t)&gs->w38); } else { e.b(0x05); e.d(w38); }
    e.op(0x3B, EAX, EDX, 4); bail[nb++] = e.jcc(0x87);
    // commit: arena bump, EA's return-slot word, the block, the callee's registers
    e.op(0x89, EAX, EDX, 0);                                      // [chunk] = top + size
    if (op == 0xED) e.op(0x89, ECX, ESI, thisOff);                // con.callvirt: the dereferenced receiver in its slot, as EA's handler leaves it
    if (guard) { e.b(0x2B); e.b(0x05); e.d((DWORD)(uintptr_t)&gs->w38); } else { e.b(0x2D); e.d(w38); }  // eax = callee arena base
    VtLoad(e, EDX);
    e.op(0x89, EDX, ESI, 0);                                      // [return slot] = vt cursor, as EA's call writes
    if (guard) { e.rr(0x8B, EAX, EDX); e.b(0x03); e.b(0x15); e.d((DWORD)(uintptr_t)&gs->wVt); } else e.op(0x8D, EDX, EAX, w3E + w34);
    e.b(0x50 + EDX);                                              // B_VT = the callee's vt area start
    if (g_jitCountOn) { e.incAbs(&g_jitLazyCalls); if (lazy) e.incAbs(&g_jitLazyChain); if (guard) e.incAbs(&g_jitLazyGuard); }
    if (lazy) { PushOwnBlk(e, 4, B_LVL); e.b(0xFF); e.b(0x04); e.b(0x24); }  // B_LVL = own level + 1 (B_VT is pushed already)
    else { e.b(0x6A); e.b(1); }                                                                      // B_LVL = 1
    e.b(0x68); e.d((DWORD)(uintptr_t)ip);                         // B_CIP
    e.b(0x50 + EDI); e.b(0x50 + EBX);                             // B_EDI, B_EBX
    if (guard) { e.b(0xFF); e.b(0x35); e.d((DWORD)(uintptr_t)&gs->rm); } else { e.b(0x68); e.d((DWORD)(uintptr_t)c); }  // B_RM
    e.b(0x50 + ESI);                                              // B_RSLOT
    e.op(0x8D, EDX, ESI, rsp); e.b(0x50 + EDX);                   // B_RSP
    e.b(0x68); e.d((DWORD)(uintptr_t)(ip + ripW));                // B_RIP
    e.b(0x50 + ECX);                                              // B_THIS
    e.op(0x8D, EDX, ESI, -n * 8); e.b(0x50 + EDX);                // B_ARGS
    e.rr(0x8B, EAX, EBX);                                         // mov ebx, eax
    if (guard) {
        e.rr(0x8B, EAX, ESI); e.b(0x03); e.b(0x35); e.d((DWORD)(uintptr_t)&gs->w3E);   // esi = eax + eval offset
        e.rr(0x8B, EAX, EDI); e.b(0x03); e.b(0x3D); e.d((DWORD)(uintptr_t)&gs->wLoc);  // edi = eax + locals offset
        e.b(0x8B); e.b(0x0D); e.d((DWORD)(uintptr_t)&gs->w3C); e.rr(0x85, ECX, ECX); u8* noInit = e.jcc(0x84);  // init locals, if the target has them
        e.b(0x50 + EDI); e.rr(0x33, EAX, EAX); e.b(0xF3); e.b(0xAA); e.b(0x58 + EDI);
        Fix(noInit, e.p);
        e.b(0xFF); e.b(0x15); e.d((DWORD)(uintptr_t)&gs->entry);  // call [site.entry]
    } else {
        e.op(0x8D, ESI, EAX, w3E);
        e.op(0x8D, EDI, EAX, w3E + w34 + w36);
        if (fl & 0x800) {  // init locals: EA clears rm+0x3C bytes at the locals base
            e.b(0x50 + EDI); e.movRI(ECX, *(const unsigned short*)(c + 0x3C)); e.rr(0x33, EAX, EAX); e.b(0xF3); e.b(0xAA); e.b(0x58 + EDI);
        }
        e.b(0xFF); e.b(0x15); e.d((DWORD)(uintptr_t)&cell->entry);  // call [cell.entry]
    }
    e.opEsp(0x8B, EBX, B_EBX); e.opEsp(0x8B, EDI, B_EDI); e.opEsp(0x8B, ESI, B_RSP);
    if (c[0x44] != 1) {  // EA's frame return copies the return slot's 8 bytes to the caller's new top
        e.opEsp(0x8B, EAX, B_RSLOT);
        e.op(0x8B, ECX, EAX, 0); e.op(0x89, ECX, ESI, 0); e.op(0x8B, ECX, EAX, 4); e.op(0x89, ECX, ESI, 4); e.addEsi(8);
    }
    e.b(0x83); e.b(0xC4); e.b(B_SIZE);                            // add esp, B_SIZE
    u8* skip = e.jmp();
    for (int i = 0; i < nb; i++) Fix(bail[i], e.p);
    EmitExit(e, ip, op, false);
    Fix(skip, e.p);
    g_jitLazySites++;
    if (guard) g_jitLazyGuardSites++;
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

// before a listed method is compiled: transform the instance callees of its call / callvirt sites that are not transformed
// yet (EA would at their first call), so the inliner can read their mint code. Transform builds code only; a failure
// returns its exception and is left for EA's own transform at the first call.
static void* __cdecl JitTransformHook(u8* rm, void* ctx);  // the includer's: the transform entry with the compiler after it
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
            u8* c = (u8*)(uintptr_t)items[w == 0xEA ? code[i + 1] : w == 0xEB ? code[i + 2] : w == 0xEC ? code[i + 3] : code[i + 4]];
            const u8* mm = c ? *(const u8* const*)(c + 8) : NULL;
            // instance callees for the inliner; with lazy frames static ones too (a lazy call site bakes in the callee's frame layout).
            // A virtual one is left to its guard site, except a con.call's: that target is static, and an interface implementation
            // such as UInt64.Equals(ulong) is "virtual final" in metadata (round 10 C1: its site compiled as an exit before).
            if (c && mm && !(*(const DWORD*)(c + 0x44) & 0x100) && ((*(const DWORD*)(c + 0x44) & 0x200) || g_jitLazy) && (w == 0xEB || !(*(const unsigned short*)mm & 0x40)))
                JitTransformHook(c, ctx);
        }
        i += len;
    }
}

// the word a compiled position gets: JIT_OP, except leave.s. EA's leave / leave.s handler 0xE59315 re-reads [edi] to pick
// the operand size (0xE5933A cmp word ptr [edi], 0x9C): with JIT_OP there it reads leave.s's 16-bit offset plus the next
// word as a 32-bit offset and jumps to a wild ip. leave.s always exits to that handler, so its own word can stay.
static unsigned short JitWord(unsigned short orig) { return orig == 0x9C ? orig : (unsigned short)JIT_OP; }
// compile one method: returns false (and leaves the code alone) if anything does not fit
static void JitCompiled(u8* rm) {
    if (!g_jitTab || g_jitMethN >= JIT_METH_N) return;
    // docs/223 §1: EA bound a native body in this transform (0xE645FF, before our post-pass): its calls take the body, not mint
    if (*(const DWORD*)(rm + 0x24)) { JitLog("jit: has an EA native body (+0x24): not compiled"); return; }
    g_jitSiteRm = rm;
    unsigned short* code = *(unsigned short**)rm;
    int words = *(unsigned short*)(rm + 0x3A);
    // 1. positions: every instruction start, superops expanded into their base instructions
    static int posW[4096];
    static unsigned short posOp[4096];
    static bool posReal[4096];  // EA instruction start (dispatch can land here)
    int n = 0;
    for (int i = 0; i < words;) {
        unsigned op = code[i];
        if (n >= 4000) return;
        if (op == 0xE8) {  // switch: compiled as an exit (variable length)
            posW[n] = i, posOp[n] = 0xE8, posReal[n++] = true;
            i += JitSwitchLen(code + i);
            continue;
        }
        int len = JitLen(code + i);
        if (len <= 0) { JitLog("jit: cannot walk %s at word %d (op %X)", "method", i, op); return; }
        if (op >= 0x1AB) {
            const u8* d = (const u8*)(uintptr_t)(SUPERDESC + 0x48 * (op - 0x1AA));
            int m = *(const unsigned short*)(d + 4), at = i;
            for (int k = 0; k < m; k++) {
                unsigned short b = ((const unsigned short*)(d + 6))[k];
                if (k && code[at] != b) { JitLog("jit: superop %X inner word %d differs", op, k); return; }
                posW[n] = at, posOp[n] = b, posReal[n++] = k == 0;
                at += ((const u8*)(uintptr_t)OPLEN)[b];
            }
        } else
            posW[n] = i, posOp[n] = (unsigned short)op, posReal[n++] = true;
        i += len;
    }
    // 2. emit. With lazy frames, a method whose every position is native, ret / ret.void or a call this compiler makes
    // lazily (v2: its lazy body makes lazy calls; a target with no lazy body at run time means JitLazyMat, then EA's call)
    // first gets its lazy body, then the normal one; label[] and fix[] end as the normal body's.
    bool lazyOk = g_jitLazy && !g_jitExitAll && !rm[0x41];
    for (int k = 0; k < n && lazyOk; k++) {
        const JitOpInfo t = posOp[k] < 0x1C1 ? g_opInfo[posOp[k]] : JitOpInfo{K_EXIT, 0, 0};
        const u8* lc; bool lg;
        if (t.kind == K_EXIT && posOp[k] >= 0xEA && posOp[k] <= 0xED && posReal[k] && JitLazySite(code + posW[k], posOp[k], rm, &lc, &lg)) continue;
        if (t.kind == K_EXIT || (t.kind == K_RET && posOp[k] != 0x06 && posOp[k] != 0x07)) lazyOk = false;
    }
    if (g_jitCodeEnd - (g_jitCode + g_jitCodeUsed) < (lazyOk ? 512 : 256) * n + 512) { JitLog("jit: code pool full"); return; }
    JitProtScope prot(g_jitCode + g_jitCodeUsed, 2 * ((size_t)(lazyOk ? 512 : 256) * n + 512));
    static u8* label[4096];
    static struct { u8* rel; int targetW; bool abs; } fix[4096];  // abs: a switch table slot takes the label itself
    static int wordToPos[65536];
    for (int k = 0; k < words; k++) wordToPos[k] = -1;
    for (int k = 0; k < n; k++) wordToPos[posW[k]] = k;
    int nf = 0, nat = 0, ex = 0;
    Em e = {g_jitCode + g_jitCodeUsed};
    u8* lazyEntry = NULL;
    const DWORD* argOff = *(const DWORD* const*)(rm + 0x1C);
    const DWORD* items = *(const DWORD* const*)(rm + 0x14);
    for (int pass = lazyOk ? 0 : 1; pass < 2; pass++) {
        g_jitLazyPass = pass == 0;
        nf = 0, nat = 0, ex = 0;
        if (g_jitLazyPass) {
            lazyEntry = e.p;
            if (g_jitCountOn && g_jitMethN < 4096) e.incAbs(&g_jitCalls[g_jitMethN]);  // lazy entries skip JitEnter's count
        }
        for (int k = 0; k < n; k++) {
            label[k] = e.p;
            if (g_jitCountOn && g_jitMethN < 4096) e.incAbs(&g_jitPosRun[g_jitMethN]);  // the index this method gets if it compiles
            const unsigned short* ip = code + posW[k];
            DWORD op = posOp[k];
            JitOpInfo t = op < 0x1C1 ? g_opInfo[op] : JitOpInfo{K_EXIT, 0, 0};
            if (g_jitExitAll) t.kind = t.kind == K_RET ? K_RET : K_EXIT;
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
                e.fld8(ESI, -16); e.fld8(ESI, -8); e.b(0xE8); e.d((DWORD)0xF7EFB0 - ((DWORD)(uintptr_t)e.p + 4)); e.fstp8(ESI, -16); e.addEsi(-8); break;
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
                    e.b(0xFF); e.b(0x15); e.d(0xF95684); e.b(0x83); e.b(0xC4); e.b(12);   // call dword ptr [memmove]; add esp, 12
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
            case K_CEQ8:  // (a == b) over two 8-byte slots: xor the halves, or them, sete (EA's 0xE5803C); the low dword gets the result
                e.op(0x8B, EAX, ESI, -16); e.op(0x33, EAX, ESI, -8); e.op(0x8B, ECX, ESI, -12); e.op(0x33, ECX, ESI, -4);
                e.rr(0x0B, ECX, EAX); e.b(0x0F); e.b(0x94); e.b(0xC0); e.b(0x0F); e.b(0xB6); e.b(0xC0);  // or eax, ecx; sete al; movzx eax, al
                e.op(0x89, EAX, ESI, -16); e.addEsi(-8); break;
            case K_CEQ0: e.rr(0x33, EAX, EAX); e.b(0x83); e.m(7, ESI, -8); e.b(0); e.b(0x0F); e.b(0x94); e.b(0xC0); e.op(0x89, EAX, ESI, -8); break;
            case K_CONV:
                if (t.x == 1) { e.op(0x8B, EAX, ESI, -8); e.b(0x99); e.op(0x89, EDX, ESI, -4); }
                else if (t.x == 2) { e.b(0xC7); e.m(0, ESI, -4); e.d(0); }
                else if (t.x >= 4) { e.load(t.x == 4 ? 1 : t.x == 5 ? 2 : t.x == 6 ? 3 : 4, EAX, ESI, -8); e.op(0x89, EAX, ESI, -8); }
                break;  // conv.i4.i8: the low word already is the result
            default:
                if (g_jitLazyPass && (op == 0x06 || op == 0x07)) { EmitLazyRet(e, ip, op, rm, op == 0x06); nat++; continue; }
                if ((op == 0xEA || op == 0xEC) && g_jitInline && posReal[k] && JitTryInline(e, ip, op, rm)) { nat++; continue; }
                if ((op == 0xEA || op == 0xEB || op == 0xEC || op == 0xED) && g_jitLazy && posReal[k] && EmitLazyCall(e, ip, op, rm)) { nat++; continue; }
                EmitExit(e, ip, op, t.kind == K_RET);
                ex++;
                continue;
            }
            nat++;
            if (exitRel[0] || exitRel[1] || exitRel[2]) {  // out-of-line slow path: back to EA's handler with nothing changed
                u8* skip = e.jmp();
                if (exitRel[0]) Fix(exitRel[0], e.p);
                if (exitRel[1]) Fix(exitRel[1], e.p);
                if (exitRel[2]) Fix(exitRel[2], e.p);
                g_jitSiteSlow = 1;
                EmitExit(e, ip, op, false);
                g_jitSiteSlow = 0;
                Fix(skip, e.p);
            }
        }
        label[n] = e.p;  // falling off the end cannot happen (mint ends in ret/throw/br); trap it
        e.b(0xCC);
        for (int f = 0; f < nf; f++) {
            int tw = fix[f].targetW, tp = tw >= 0 && tw < words ? wordToPos[tw] : -1;
            if (tp < 0) { JitLog("jit: branch target %d not an instruction", tw); g_jitLazyPass = false; return; }
            if (fix[f].abs) memcpy(fix[f].rel, &label[tp], 4); else Fix(fix[f].rel, label[tp]);
        }
    }
    g_jitLazyPass = false;
    // 3. publish: table entries for every position, then patch the opcode words (positions of EA instruction starts and
    // superop inner positions alike; inner words are never read by the superop handlers)
    if (g_jitPosN + n > JIT_POS_N || g_jitEntN + n > (int)(JIT_TAB_N * 3 / 4)) {
        static bool logged;
        if (!logged) logged = true, JitLog("jit: tables full (%d methods, %d positions, %d entries)", g_jitMethN, g_jitPosN, g_jitEntN);
        return;
    }
    JitMeth& M = g_jitMeth[g_jitMethN];
    M.rm = rm, M.code = code, M.words = words, M.pos0 = g_jitPosN, M.nativeOps = nat, M.exitOps = ex;
    M.bytes = (int)(e.p - (g_jitCode + g_jitCodeUsed));
    JitMethodName(rm, M.name, sizeof(M.name));
    bool branch0 = false;
    for (int f = 0; f < nf; f++) branch0 |= fix[f].targetW == 0;
    M.verifyOk = !rm[0x41] && !branch0;
    for (int k = 0; k < n; k++) {
        JitEnt* en = JitAdd((DWORD)(uintptr_t)(code + posW[k]));
        if (!en) return;
        en->native = label[k], en->orig = code[posW[k]], en->rm = rm;
        g_jitPos[g_jitPosN] = (unsigned short)posW[k];
        g_jitOrigW[g_jitPosN++] = code[posW[k]];
    }
    M.posN = g_jitPosN;
    g_jitMethN++;
    g_jitCodeUsed += M.bytes + 1;
    g_jitCodeUsed = (g_jitCodeUsed + 15) & ~(size_t)15;
    if (g_jitPatched) for (int k = M.pos0; k < M.posN; k++) code[g_jitPos[k]] = JitWord(g_jitOrigW[k]);
    if (lazyEntry) {
        if (JitLazyCell* lc = JitLazyCellFor(rm)) lc->entry = lazyEntry, g_jitLazyBodies++;
        for (int i = 0; i < g_jitGuardN; i++)  // guard sites that met this method before it had a lazy body
            if (g_jitGuard[i].rm == rm && !g_jitGuard[i].entry) JitGuardFill(&g_jitGuard[i], rm);
    }
    if (g_jitDump) fprintf(g_jitDump, "   compiled: %d positions, %d native, %d exits, %d bytes, verify %s%s\n", n, nat, ex, M.bytes, M.verifyOk ? "yes" : "no",
                           lazyEntry ? ", lazy body" : "");
    if (g_jitDump && g_jitDumpCode) {  // MONOHOST_JITDUMPCODE=1: native bytes (lazy body first), for an offline disassembler
        const u8* p0 = lazyEntry ? lazyEntry : label[0];
        fprintf(g_jitDump, "   code %08X normal %08X:", (unsigned)(uintptr_t)p0, (unsigned)(uintptr_t)label[0]);
        for (const u8* p = p0; p < e.p; p++) fprintf(g_jitDump, "%02X", *p);
        fprintf(g_jitDump, "\n");
    }
}

// patch (true) or restore (false) every compiled method's opcode words; the native code stays
static void JitSetPatched(bool on) {
    for (int m = 0; m < g_jitMethN; m++)
        for (int k = g_jitMeth[m].pos0; k < g_jitMeth[m].posN; k++)
            g_jitMeth[m].code[g_jitPos[k]] = on && !g_jitMeth[m].dead ? JitWord(g_jitOrigW[k]) : g_jitOrigW[k];
    g_jitPatched = on;
}
// a method with a verify mismatch: its words go back to EA's, its lazy body is unpublished (lazy call sites and guards then
// take EA's call), and JitSetPatched leaves it alone
static void JitUnpatchMeth(JitMeth* m) {
    for (int k = m->pos0; k < m->posN; k++) m->code[g_jitPos[k]] = g_jitOrigW[k];
    m->dead = true, m->verifyOk = false;
    for (int i = 0; i < g_jitLazyCellN; i++) if (g_jitLazyCell[i].rm == m->rm) g_jitLazyCell[i].entry = NULL;
    for (int i = 0; i < g_jitGuardN; i++) if (g_jitGuard[i].rm == m->rm) g_jitGuard[i].entry = NULL;
    JitLog("jit: %s unpatched after a verify mismatch (interpreted for the rest of the session)", m->name);
}

// ---- JitEnter: the jump-table entry of JIT_OP (eax = edx = JIT_OP, edi = ip) ----
static int g_jitVerifyOn;
static DWORD g_jitMainTid;            // the one thread that runs compiled code (0 = none yet: every entry goes to EA's handler)
static volatile LONG g_jitOtherTid;  // entries from another thread (the M5 / AutShadow worker, a loader): EA's handler runs
static __declspec(naked) void JitEnter() {
    __asm {
        mov eax, edi
        shr eax, 1
        and eax, 0x3FFFF           // JIT_TAB_N - 1
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
        cmp g_jitVerifyOn, 0
        jne slow
        mov ecx, g_jitSatbOnP      // Satb span on and this is its worker: the live handler (redirect stub) of the original opcode
        cmp dword ptr [ecx], 0
        je fast
        mov ecx, dword ptr fs:[24h]
        mov edx, g_jitSatbWTidP
        cmp ecx, [edx]
        jne fast
        inc g_jitSatbBails
        mov edx, [eax + 8]         // JitEnt.orig
        mov eax, edx
        mov [ebp - 4], edi
        jmp dword ptr [edx * 4 + 0E59880h]  // MINT_TABLE
    fast:
        mov ecx, dword ptr fs:[24h]
        cmp ecx, g_jitMainTid
        jne other
        mov ebx, [ebp - 8]
        mov ebx, [ebx + 0x10]      // arena base (arguments)
        mov edi, [ebp - 0x14]      // locals base
        jmp dword ptr [eax + 4]
    other:
        lock inc g_jitOtherTid
        mov edx, [eax + 8]         // JitEnt.orig
        mov eax, edx
        mov [ebp - 4], edi
        jmp dword ptr [edx * 4 + 0E59880h]  // JIT_TABLE: the live entry
    slow:
        jmp JitVerifyEnterStub
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
struct JitV { bool active; int phase; u8* rm; JitMeth* meth; DWORD sp0, ebp, fr[6], gc0; JitSnap *s0, *s1, *s2; u8 r1[64], r2[64]; int rn, calleeRegion; bool retried; struct JitNewSet* nw; };
static const DWORD JIT_VD = 32;  // verified depths (snapshot buffers per depth)
static JitV* g_jitV;           // [JIT_VD], main thread only (worker threads run compiled code unverified)
static volatile LONG g_jvRetried, g_jvRetryOk, g_jvInner, g_jvNested, g_jvCalls, g_jvOk, g_jvMismatch, g_jvAbandoned, g_jvAllocEq, g_jvUnverified, g_jvLarge, g_jvSampled;
static int g_jitVerifyEvery = 1;  // one fresh call in N per method is verified (host: every one)
static bool g_jitUnpatchBad;      // game: a method with a mismatch runs interpreted for the rest of the session
static int g_jitFirstBad;         // method index + 1 of the first mismatch
static void JitUnpatchMeth(JitMeth* m);
static int g_jvLogged;
static bool g_jvQuiet;  // first attempt of a verified call: mismatches are logged only if the retry repeats them
static char g_jvWhat[640];
typedef u8* (__cdecl* GcBaseFn)(const void*);
static const GcBaseFn GcBase = (GcBaseFn)(uintptr_t)0xE4DC30;
static DWORD GcSize(const u8* base) {  // Boehm block size: HDR(base)->hb_sz in words (same lookup as GC_base 0xE4DC30)
    DWORD p = (DWORD)(uintptr_t)base;
    const DWORD* top = *(const DWORD* const*)(uintptr_t)(0x1233318 + (p >> 22) * 4);
    DWORD hdr = top ? top[(p >> 12) & 0x3FF] : 0;
    if (hdr <= 0xFFF) return 0;
    DWORD words = *(const DWORD*)(uintptr_t)hdr;
    return words * 4 > 0x40000 ? 0x40000 : words * 4;
}
// A free Boehm block: word 0 links the next free block of the same size, or is 0 at the end of a free list (a live
// object's word 0 is its MonoVTable, never a GC block; non-object GC memory with word 0 = 0 is treated as free too).
// Such a block reached through a stale word is not state: snapshotted, the free-list links pulled the whole free list
// in, a new object of the compiled pass then counted as old, and the restore wrote a link into it (al253: 2 mismatches
// in List`1.Add / GrowIfNeeded, compiled _items = the old free-list head, interpreted = the next block)
static volatile LONG g_jvFreeSkip, g_jvDeadSkip, g_jvGcSkip;
static bool JitLooksFree(const u8* b, DWORD sz) {
    const DWORD w0 = *(const DWORD*)b;
    return !w0 || (!(w0 & 3) && w0 >= 0x10000 && GcBase((void*)(uintptr_t)w0) == (u8*)(uintptr_t)w0 && GcSize((const u8*)(uintptr_t)w0) == sz);
}
// EA's collector may reclaim the object during the pass, so a restore would write into memory it owns: EA defers the
// reclaim past the mark (0xE49DA0 bumps GC_gc_no, 0xE49F10 reclaims on a later allocation) and skips blocks stamped this
// cycle (docs/67 §22.5-22.6, gcroots.cpp GrObj / GrStamp). Header of b's page: +0x10 map (GC_invalid_map 0x11F30D8 = a
// free block), +0x14 kind (2 / 3 uncollectable), +0x18 stamp (GC_gc_no at the last allocation), +0x1C mark bits, +0x9C
// free bits (GC_free). Unsafe: free, or unmarked in a block not stamped this cycle (dead at the last mark, reached through
// a stale word). Host al253 rerun: AV write 0x9C at 0xE4C223 in GC_malloc_atomic after such a restore [inferred].
static DWORD JitGcNo() { return *(const volatile DWORD*)(uintptr_t)0x011F2F90; }
static bool JitGcUnsafe(const u8* b) {
    const DWORD p = (DWORD)(uintptr_t)b;
    const DWORD* bot = *(const DWORD* const*)(uintptr_t)(0x1233318 + (p >> 22) * 4);
    const u8* h = bot ? (const u8*)(uintptr_t)bot[(p >> 12) & 0x3FF] : NULL;
    if ((uintptr_t)h < 0x1000 || *(const DWORD*)(h + 0x10) == *(const DWORD*)(uintptr_t)0x011F30D8) return true;
    if (h[0x14] == 2 || h[0x14] == 3) return false;
    const DWORD w = (p & 0xFFF) >> 2, bit = 1u << (w & 31), off = (w >> 5) * 4;
    if (*(const DWORD*)(h + 0x9C + off) & bit) return true;
    if (*(const DWORD*)(h + 0x1C + off) & bit) return false;
    return *(const DWORD*)(h + 0x18) != JitGcNo();
}
static const u8* g_jvInv;  // the MonoInvocation being snapshotted (byref targets must lie in one of its frames' arenas)
// add the object (or byref target) a word points at; returns the region index or -1
static int JitSnapRef(JitSnap& s, DWORD w) {
    if (w < 0x10000) return -1;
    u8* b = GcBase((void*)(uintptr_t)w);
    if (b) {
        DWORD sz = GcSize(b);
        if (sz && JitLooksFree(b, sz)) { g_jvFreeSkip++; return -1; }
        if (sz && JitGcUnsafe(b)) { g_jvDeadSkip++; return -1; }
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
struct JitNew { DWORD p, n, off; };
static const int JIT_NEW_N = 2048;
struct JitNewSet { JitNew o[JIT_NEW_N]; int n; DWORD used; u8* buf; int hs[2 * JIT_NEW_N]; DWORD hg[2 * JIT_NEW_N], gen; bool full; };  // per depth
static JitNewSet* g_jn;  // the set JitAllocEqual reads (the depth being compared)
static const JitSnap* g_jnS0;  // its entry snapshot and collection number (mismatch line only)
static DWORD g_jnGc0;
static int JitNewFind(const JitNewSet& s, DWORD w) {  // index or -1 (slot live if hg == gen)
    for (DWORD i = (w >> 3) & (2 * JIT_NEW_N - 1); s.hg[i] == s.gen; i = (i + 1) & (2 * JIT_NEW_N - 1))
        if (s.o[s.hs[i]].p == w) return s.hs[i];
    return -1;
}
// keep a copy of a word's object if it is not part of the entry snapshot (= allocated during the compiled pass), and of
// the new objects it points at
// could the compiled pass have allocated the object at w (a GC_base)? Not when it was marked at the last collection, its
// block was not allocated from this cycle, or it is free (the header bits of JitGcUnsafe); uncollectable kinds: maybe
static bool JitMaybeNew(DWORD w) {
    const DWORD* bot = *(const DWORD* const*)(uintptr_t)(0x1233318 + (w >> 22) * 4);
    const u8* h = bot ? (const u8*)(uintptr_t)bot[(w >> 12) & 0x3FF] : NULL;
    if ((uintptr_t)h < 0x1000 || *(const DWORD*)(h + 0x10) == *(const DWORD*)(uintptr_t)0x011F30D8) return false;
    if (h[0x14] == 2 || h[0x14] == 3) return true;
    const DWORD i = (w & 0xFFF) >> 2, bit = 1u << (i & 31), off = (i >> 5) * 4;
    return !(*(const DWORD*)(h + 0x1C + off) & bit) && !(*(const DWORD*)(h + 0x9C + off) & bit) && *(const DWORD*)(h + 0x18) == JitGcNo();
}
static volatile LONG g_jvNewFull;  // verifications abandoned because the new-object set filled
static void JitKeepNew(JitNewSet& s, const JitSnap& s0, DWORD w, int depth = 0) {
    if (w < 0x10000 || (w & 3) || GcBase((void*)(uintptr_t)w) != (u8*)(uintptr_t)w || JitNewFind(s, w) >= 0) return;
    if (s0.Contains(w) || !JitMaybeNew(w)) return;  // an object of the entry snapshot, or one older than the pass
    if (s.n >= JIT_NEW_N) { s.full = true; return; }
    DWORD n = GcSize((u8*)(uintptr_t)w);
    if (!n) return;
    if (s.used + n > (1u << 20)) { s.full = true; return; }
    const DWORD off = s.used;
    memcpy(s.buf + off, (void*)(uintptr_t)w, n);
    DWORD i = (w >> 3) & (2 * JIT_NEW_N - 1);
    while (s.hg[i] == s.gen) i = (i + 1) & (2 * JIT_NEW_N - 1);
    s.hs[i] = s.n, s.hg[i] = s.gen;
    s.o[s.n++] = {w, n, off};
    s.used += n;
    if (depth < 3) for (DWORD o = 0; o + 4 <= n; o += 4) JitKeepNew(s, s0, *(const DWORD*)(s.buf + off + o), depth + 1);
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
    // GC_malloc_atomic leaves a block's slack uninitialised: compare the class's bytes only. MonoVTable+0 = klass;
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
    } else if (!x[1] && !y[1] && x[2] == y[2] && x[2] && 0x10 + x[2] <= sb) {
        // array (vtable, bounds 0, length): compare 0x10 + length * the largest element size (1..8) that fits the block
        // [inferred: no element class lookup]
        DWORD es = 8;
        while (es > 1 && 0x10 + x[2] * es > sb) es >>= 1;
        used = 0x10 + x[2] * es;
    }
    for (DWORD k = 0; k < (used + 3) / 4; k++)
        if (x[k] != y[k] && !JitAllocEqual(x[k], y[k], depth + 1)) return false;
    return true;
}
static bool JitSameBytes(const u8* x, const u8* y, DWORD n, DWORD* at) {
    for (DWORD i = 0; i < n; i++) {
        if (x[i] == y[i]) continue;
        DWORD a4 = i & ~3u;
        if (a4 + 4 <= n && JitAllocEqual(*(const DWORD*)(x + a4), *(const DWORD*)(y + a4))) { g_jvAllocEq++; i = a4 + 3; continue; }
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
                      "first differing word %lu: %08lX / %08lX", a, b,
                      k, k >= 0 ? g_jn->o[k].n : 0, yb ? GcSize((const u8*)yb) : 0, xa ? xa[0] : 0, xa ? xa[1] : 0, xa ? xa[2] : 0, xa ? xa[3] : 0,
                      yb ? yb[0] : 0, yb ? yb[1] : 0, yb ? yb[2] : 0, yb ? yb[3] : 0, fk, xa && yb ? xa[fk] : 0, xa && yb ? yb[fk] : 0);
            if (k < 0) {  // why the compiled object was not kept: its GC state, and whether the entry snapshot held it
                const u8* ab = GcBase((void*)(uintptr_t)a);
                int ri = -1;
                for (int j = 0; g_jnS0 && j < g_jnS0->n && ri < 0; j++) if ((DWORD)(uintptr_t)g_jnS0->r[j].at == a) ri = j;
                const DWORD* ew = ri >= 0 ? (const DWORD*)(g_jnS0->buf + g_jnS0->r[ri].off) : NULL;
                int kind = -1, mark = -1, fr = -1;
                DWORD stamp = 0;
                __try {
                    const DWORD* bot = *(const DWORD* const*)(uintptr_t)(0x1233318 + (a >> 22) * 4);
                    const u8* h = bot ? (const u8*)(uintptr_t)bot[(a >> 12) & 0x3FF] : NULL;
                    if ((uintptr_t)h >= 0x1000) {
                        const DWORD w = (a & 0xFFF) >> 2, bit = 1u << (w & 31), off = (w >> 5) * 4;
                        kind = h[0x14], stamp = *(const DWORD*)(h + 0x18);
                        mark = (*(const DWORD*)(h + 0x1C + off) & bit) != 0, fr = (*(const DWORD*)(h + 0x9C + off) & bit) != 0;
                    }
                } __except (EXCEPTION_EXECUTE_HANDLER) {}
                const size_t L = strlen(g_jvWhat);
                sprintf_s(g_jvWhat + L, sizeof(g_jvWhat) - L, "; compiled object: base %08lX size %lu, entry region %d (entry words %08lX %08lX %08lX), "
                          "gc kind %d mark %d free %d stamp %lu gc %lu now %lu; new set %d / %lu bytes",
                          (DWORD)(uintptr_t)ab, ab ? GcSize(ab) : 0, ri, ew ? ew[0] : 0, ew ? ew[1] : 0, ew ? ew[2] : 0, kind, mark, fr, stamp,
                          g_jnGc0, JitGcNo(), g_jn ? g_jn->n : -1, g_jn ? g_jn->used : 0);
            }
        }
        return false;
    }
    return true;
}
static int JitRetBytes(const JitRegs* r, DWORD op, const unsigned short* ip, u8* out) {
    const char* nm = ((const char* const*)(uintptr_t)0x11949E0)[op];
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
    if (!g_jvQuiet && g_jvLogged++ < 40) JitLog("jit verify MISMATCH %s: %s at +%lu (%s)", v.meth ? v.meth->name : "?", what, at, g_jvWhat);
}
static u8* JitNative(JitRegs* r, const JitEnt* e) {
    r->ebx = *(const DWORD*)(uintptr_t)(*(const DWORD*)(uintptr_t)(r->ebp - 8) + 0x10);
    r->edi = *(const DWORD*)(uintptr_t)(r->ebp - 0x14);
    return e->native;
}
static void* JitOrig(JitRegs* r, DWORD ip, DWORD op) {
    r->edi = ip, r->eax = r->edx = op;
    *(DWORD*)(uintptr_t)(r->ebp - 4) = ip;
    return (void*)(uintptr_t)((const DWORD*)(uintptr_t)JIT_TABLE)[op];  // the live entry, as EmitExit
}
static JitMeth* JitMethOf(const u8* rm) {
    for (int i = 0; i < g_jitMethN; i++) if (g_jitMeth[i].rm == rm) return &g_jitMeth[i];
    return NULL;
}

// a lazy callee's first exit: write the frames EA's calls would have written at the call sites (docs/139 McCall + McPush:
// caller record, depth and max depth, callee record, MonoInvocation, ThreadContext +8, frame words), from the blocks the
// call sites pushed (r->esp_ = the return address into the innermost call site, its block above it; in a chain the outer
// blocks follow, each above the return address into its lazy body, B_LVL of them in all). Outermost first, so each frame's
// caller state is the frame just written. The arena bumps, init-locals clears and the return-slot words were done at the
// calls. JitLazyMat then drops every native call of the chain (ecx bytes) and takes the exit.
static volatile LONG g_jitLazyMatFrames;
static void __cdecl JitLazyMatC(JitRegs* r) {
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
        g_jitLazyMatFrames++;
    }
    r->ecx = L * (4 + B_SIZE);
    g_jitLazyMats++;
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
        jmp dword ptr [eax * 4 + 0E59880h]  // MINT_TABLE: the live entry, as EmitExit
    }
}
static void* __cdecl JitVerifySlow(JitRegs* r, JitEnt* e, int fromExit) {
    if (!e) e = JitFind(r->edi);
    if (g_jitVerifyOn == 2) {  // count mode
        g_jitEnterN++;
        if (!fromExit && *(unsigned short* const*)e->rm == (unsigned short*)(uintptr_t)r->edi) {  // entry at word 0: a call
            static const u8* lastRm; static int lastM = -1;
            if (e->rm != lastRm) { JitMeth* m = JitMethOf(e->rm); lastRm = e->rm, lastM = m ? (int)(m - g_jitMeth) : -1; }
            if (lastM >= 0) g_jitCalls[lastM]++;
        }
        return fromExit ? JitOrig(r, r->edi, e->orig) : JitNative(r, e);
    }
    if (GetCurrentThreadId() != g_jitMainTid) { InterlockedIncrement(&g_jitOtherTid); return fromExit ? JitOrig(r, r->edi, e->orig) : JitNative(r, e); }
    const u8* rm = e->rm;
    DWORD depth = *(const DWORD*)(uintptr_t)(*(const DWORD*)(uintptr_t)(r->ebp + 8) + 0x140);
    if (depth >= JIT_VD) return fromExit ? JitOrig(r, r->edi, e->orig) : JitNative(r, e);
    JitV& v = g_jitV[depth];
    const unsigned short* code = *(const unsigned short* const*)rm;
    const bool mine = v.active && v.rm == rm && v.ebp == r->ebp;  // ebp = the interpreter activation (nested ones restart at depth 0)
    if ((DWORD)(uintptr_t)code == r->edi && v.active && v.ebp != r->ebp) { g_jvNested++; return JitNative(r, e); }  // slot held by an outer activation
    // one verification in flight per activation: compiled callees of a verified frame run compiled in its pass 1 and
    // interpreted in its pass 2, so pass 2 is a pure-interpreter reference for the whole call tree (and cost stays 2x)
    int outer = 0;
    for (DWORD k = 0; k < depth; k++) if (g_jitV[k].active && g_jitV[k].ebp == r->ebp) outer = g_jitV[k].phase;
    if (fromExit) {  // a compiled ret
        if (!mine || v.phase != 1) return JitOrig(r, r->edi, e->orig);
        if (JitGcNo() != v.gc0) { v.active = false, g_jvAbandoned++, g_jvGcSkip++; return JitOrig(r, r->edi, e->orig); }
        v.rn = JitRetBytes(r, e->orig, (const unsigned short*)(uintptr_t)r->edi, v.r1);
        v.s0->Take(*v.s1);
        v.nw->n = 0, v.nw->used = 0, v.nw->gen++, v.nw->full = false;  // keep copies of what the compiled pass allocated (changed words that are new objects)
        DWORD kLo = r->esi - (DWORD)(uintptr_t)v.s0->r[0].at, kHi = (DWORD)*(const unsigned short*)(rm + 0x3E) + *(const unsigned short*)(rm + 0x34);
        if (kLo > kHi) kLo = kHi;  // region 0 [kLo, kHi): dead eval-stack slots, not compared (as below)
        for (int i = 0; i < v.s0->n; i++)
            for (DWORD o = 0; o + 4 <= v.s0->r[i].n; o += 4) {
                if (i == v.calleeRegion || (i == 0 && o >= kLo && o < kHi)) continue;
                DWORD a = *(const DWORD*)(v.s1->buf + v.s0->r[i].off + o);
                if (a != *(const DWORD*)(v.s0->buf + v.s0->r[i].off + o)) JitKeepNew(*v.nw, *v.s0, a);
            }
        for (int o = 0; o + 4 <= v.rn; o += 4) JitKeepNew(*v.nw, *v.s0, *(const DWORD*)(v.r1 + o));
        v.s0->Restore();
        r->esi = v.sp0;
        for (int i = 0; i < 6; i++) *(DWORD*)(uintptr_t)(r->ebp - 0x8 - 4 * i) = v.fr[i];  // interpreter frame words [ebp-8]..[ebp-0x1C] (vt area cursor [ebp-0xC])
        v.phase = 2;
        return JitOrig(r, (DWORD)(uintptr_t)code, JitFind((DWORD)(uintptr_t)code)->orig);  // pass 2 from word 0
    }
    if ((DWORD)(uintptr_t)code == r->edi && !(mine && v.phase == 2)) {  // fresh call
        for (DWORD d = depth; d < JIT_VD; d++) if (g_jitV[d].active && g_jitV[d].ebp == r->ebp) g_jitV[d].active = false, g_jvAbandoned++;
        JitMeth* m = JitMethOf(rm);
        g_jvCalls++;
        if (outer) { g_jvInner++; if (m) m->inner++; return outer == 2 ? JitOrig(r, r->edi, e->orig) : JitNative(r, e); }
        if (!m || !m->verifyOk) { g_jvUnverified++; if (m) m->skipped++; return JitNative(r, e); }
        if (g_jitVerifyEvery > 1 && InterlockedIncrement(&m->seen) % g_jitVerifyEvery) { g_jvSampled++; return JitNative(r, e); }
        v.active = true, v.phase = 1, v.rm = (u8*)rm, v.meth = m, v.sp0 = r->esi, v.ebp = r->ebp;
        for (int i = 0; i < 6; i++) v.fr[i] = *(const DWORD*)(uintptr_t)(r->ebp - 0x8 - 4 * i);
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
        v.gc0 = JitGcNo();
        const int walk = v.s0->n;
        JitSnapRef(*v.s0, *(const DWORD*)(inv + 0xC));  // this: object, or a byref for value-type methods
        for (int i = 0; i < np && sargs; i++) JitSnapRef(*v.s0, *(const DWORD*)(sargs + 8 * i));
        JitSnapGraph(*v.s0, walk);
        if (v.s0->full) { v.active = false, g_jvLarge++, m->skipped++; return JitNative(r, e); }
        for (int i = 0; i < v.s0->n; i++) memcpy(v.s0->buf + v.s0->r[i].off, v.s0->r[i].at, v.s0->r[i].n);
        return JitNative(r, e);
    }
    if (mine && v.phase == 2) {  // interpreted pass of a verified call
        if (g_opInfo[e->orig < 0x1C1 ? e->orig : 0].kind == K_RET) {
            u8 rb[64];
            int rn = JitRetBytes(r, e->orig, (const unsigned short*)(uintptr_t)r->edi, rb);
            v.s0->Take(*v.s2);
            g_jvQuiet = !v.retried;
            g_jn = v.nw, g_jnS0 = v.s0, g_jnGc0 = v.gc0;
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
                if (i != v.calleeRegion && !JitSameBytes(v.s1->buf + v.s0->r[i].off, v.s2->buf + v.s0->r[i].off, v.s0->r[i].n, &at)) {
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
            if (!ok && JitGcNo() != v.gc0) { v.active = false, g_jvAbandoned++, g_jvGcSkip++; return JitOrig(r, r->edi, e->orig); }
            if (!ok && v.nw->full) {
                if (!g_jvNewFull++) JitLog("jit verify: %s: a difference with the new-object set full (2048 objects / 1 MB): abandoned, not a mismatch", v.meth->name);
                v.active = false, g_jvAbandoned++;
                return JitOrig(r, r->edi, e->orig);
            }
            if (!ok && !v.retried) {
                // second chance from the same entry state: a one-time effect of the first compiled pass that the restore
                // cannot undo (class init, a transform, a lazily filled cache outside the graph) is then in place for both
                // passes; a real compiler difference repeats
                v.retried = true, g_jvRetried++;
                v.s0->Restore();
                r->esi = v.sp0;
                for (int i = 0; i < 6; i++) *(DWORD*)(uintptr_t)(r->ebp - 0x8 - 4 * i) = v.fr[i];
                v.phase = 1;
                return JitNative(r, JitFind((DWORD)(uintptr_t)code));
            }
            if (ok && v.retried) g_jvRetryOk++;
            (ok ? g_jvOk : g_jvMismatch)++;
            (ok ? v.meth->verified : v.meth->mismatches)++;
            v.active = false;
            if (!ok && !g_jitFirstBad) g_jitFirstBad = (int)(v.meth - g_jitMeth) + 1;
            if (!ok && g_jitUnpatchBad) JitUnpatchMeth(v.meth);
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

// verify only: GC_malloc_atomic 0xE4BDC0 -> GC_malloc 0xE4BE30 (docs/57 §1), so pointer-free objects come zeroed and the
// slack after a boxed value or array is the same in both passes (atomic blocks keep old bytes). Off while M5 installs its
// P3 detours (they check the original bytes) and runs.
static u8 g_atomicOrig[5];
static bool JitWrite(void* at, const void* bytes, size_t n) {
    DWORD old;
    if (!VirtualProtect(at, n, PAGE_EXECUTE_READWRITE, &old)) return false;
    memcpy(at, bytes, n);
    VirtualProtect(at, n, old, &old);
    FlushInstructionCache(GetCurrentProcess(), at, n);
    return true;
}
static bool JitAtomic(bool on) {
    if (g_jitMode != 3) return true;
    u8 j[5] = {0xE9};
    *(LONG*)(j + 1) = (LONG)(0xE4BE30 - (0xE4BDC0 + 5));
    return JitWrite((void*)(uintptr_t)0xE4BDC0, on ? j : g_atomicOrig, 5);
}

// ---- setup shared by both includers: template table (groups = the MONOHOST_JITC2 / [MonoJit] Groups mask), the ip table,
// the code pool, the position arrays and the verify buffers. EA's handlers are read before JIT_OP goes into the table.
// Groups: 1 float fields + ldflda, 2 static fields, 4 the array loads / stores of round 10 C2, 8 stobj, 16 cknull / strlen /
// getchr, 32 div.un.i4 / rem.un.i4 / rem.r8, 64 switch, 128 the value-type moves and ceq.i8 (round 10 C1)
static bool JitSetup(unsigned groups, bool verify) {
    const char* const* names = (const char* const*)(uintptr_t)0x11949E0;
    g_jitObjectClass = *(void**)(uintptr_t)(0x1220E40 + 4);  // mono_defaults.object_class (monohost.cpp reads the same)
    const DWORD* table = (const DWORD*)(uintptr_t)JIT_TABLE;
    for (DWORD op = 0; op < 0x1C1; op++) g_opHandler[op] = (void*)(uintptr_t)table[op];
    for (auto& t : kJitTmpl) {
        const unsigned g = t.kind == K_LDFLDA || !strcmp(t.name, "ldfld.r4") || !strcmp(t.name, "ldfld.r8") || !strcmp(t.name, "stfld.r4") || !strcmp(t.name, "stfld.r8") ? 1
            : t.kind == K_LDSFLD || t.kind == K_LDSFLDA || t.kind == K_STSFLD ? 2
            : t.kind == K_STELEM || t.kind == K_STELEMREF || !strcmp(t.name, "ldelem.r4") || !strcmp(t.name, "ldelem.r8") ? 4
            : t.kind == K_STOBJ ? 8 : t.kind == K_CKNULL || t.kind == K_STRLEN || t.kind == K_GETCHR ? 16
            : t.kind == K_DIVUN || t.kind == K_REMR8 ? 32 : t.kind == K_SWITCH ? 64
            : t.kind == K_LDVT || t.kind == K_STVT || t.kind == K_STINARGVT || t.kind == K_DUPVT || t.kind == K_VTRESULT || t.kind == K_CEQ8 || t.kind == K_LDFLDVT || t.kind == K_LDSFLDVT ? 128 : 0;
        if (g & ~groups) continue;
        for (DWORD op = 0; op <= 0x1A8; op++)
            if (!strcmp(names[op], t.name)) { g_opInfo[op] = {t.kind, t.width, t.x}; break; }
    }
    g_jitTab = (JitEnt*)VirtualAlloc(NULL, sizeof(JitEnt) * (JIT_TAB_N + 4096), MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    g_jitCode = (u8*)VirtualAlloc(NULL, JIT_CODE_BYTES, MEM_COMMIT | MEM_RESERVE, g_jitProtect ? PAGE_EXECUTE_READ : PAGE_EXECUTE_READWRITE);
    g_jitCodeEnd = g_jitCode ? g_jitCode + JIT_CODE_BYTES : NULL;
    g_jitPos = (unsigned short*)malloc(JIT_POS_N * 2), g_jitOrigW = (unsigned short*)malloc(JIT_POS_N * 2);
    g_jitV = (JitV*)calloc(JIT_VD, sizeof(JitV));
    if (!g_jitTab || !g_jitCode || !g_jitPos || !g_jitOrigW || !g_jitV) return false;
    for (DWORD d = 0; d < JIT_VD && verify; d++) {
        JitSnap** ss[3] = {&g_jitV[d].s0, &g_jitV[d].s1, &g_jitV[d].s2};
        for (JitSnap** s : ss) {
            *s = (JitSnap*)calloc(1, sizeof(JitSnap));
            if (!*s) return false;
            (*s)->cap = 1 << 20, (*s)->buf = (u8*)VirtualAlloc(NULL, 1 << 20, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
            if (!(*s)->buf) return false;
        }
        g_jitV[d].nw = (JitNewSet*)calloc(1, sizeof(JitNewSet));
        if (!g_jitV[d].nw || !(g_jitV[d].nw->buf = (u8*)VirtualAlloc(NULL, 1 << 20, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE))) return false;
    }
    memcpy(g_atomicOrig, (const void*)(uintptr_t)0xE4BDC0, 5);
    return true;
}
static void JitNoteDomain(void* ctx) {  // the transform context's domain (EA's stsfld reads [ebp+0xC]->domain the same way)
    if (ctx && !g_jitDomain) { g_jitDomain = *(void**)ctx; JitLog("jit: domain %p (the transform context's)", g_jitDomain); }
}

#ifdef MJ_GAME
// ---- [MonoJit] in the game (docs/527 step 1; off by default) ----
// The compiler is a client of ilpatch.cpp's transform detour (g_ilpPost: after EA's transform returned without an
// exception); either feature alone installs that detour. A listed method compiles on the sim thread only: one transformed
// on another thread (a loader, the M5 / AutShadow worker) or before the first Simulate iteration is queued and compiled at
// the end of the next iteration (MjTick, sim thread, no interpreter frame of ours in flight). Compiled code runs on the sim
// thread only (JitEnter's thread test). Gates at install: the transform prologue (the detour), JIT_OP's table entry EA's
// default handler, no [GcRootCheck] TailWatch at 0xE563B2, the Simulate loop hook (MjTick). Per method: MonoPatcher's
// ForceJIT off (0xE5637D byte 0x75), not compiled before, and with HashGuard=1 an IL hash in kMjHash.
// Switch: ts3native_counters.jitSwitch (--ab monojit; start value Enabled): 0 every compiled method's words are EA's again,
// 1 patched; flipped in MjTick. Verify (Mode=2): docs/207 §11's per-call verify, one fresh call in VerifyEvery per method;
// a mismatch unpatches that method for the session (jitMismatch, jitFirstBad, the log names it).
static const char* const kMjList[] = {
    // docs/207 §16 rule (docs/data/jit-hot.csv, verdict compile): 11 methods, 41 ms/s of self time in the sampler runs
    "Dictionary`2.GetPrev", "RuntimeTypeHandle.get_Value", "Dictionary`2.GetSlot", "Array.Copy",
    "CASUtils.CASAGSAvailabilityFlagsFromCASAgeGenderFlags", "List`1.Add", "List`1.get_Item", "List`1.GrowIfNeeded",
    "ObjectGuid.op_Inequality", "Slots.GetParentId", "Vector3..ctor",
};
// HashGuard=1: compile only a body whose IL (its size and the first 8 bytes of its SHA-256) is listed here. From al253's
// "monojit: <name>: IL" lines (Steam 1.67 with EA's assemblies; every instantiation of a generic shares its body).
// Another body (a core mod's, another build) runs interpreted and is logged once.
struct MjHash { const char* name; DWORD size; BYTE sha8[8]; };
static const MjHash kMjHash[] = {
    {"System.RuntimeTypeHandle.get_Value", 7, {0x5f, 0x41, 0x62, 0xbe, 0x25, 0x20, 0x05, 0xf8}},
    {"System.Collections.Generic.Dictionary`2.GetPrev", 106, {0xf8, 0xc6, 0xd0, 0x5d, 0xf8, 0xad, 0x06, 0xc3}},
    {"System.Collections.Generic.Dictionary`2.GetSlot", 29, {0x32, 0x85, 0x37, 0xba, 0x3d, 0x18, 0x74, 0x08}},
    {"System.Array.Copy", 501, {0x20, 0x73, 0x14, 0x55, 0x73, 0x3c, 0x4b, 0xb1}},
    {"System.Array.Copy", 51, {0xab, 0xfe, 0x33, 0x79, 0x48, 0x1b, 0xd5, 0x86}},
    {"System.Array.Copy", 45, {0xa3, 0x95, 0x35, 0x70, 0x28, 0xe6, 0xbc, 0xd9}},  // al256 IP / SV
    {"Sims3.SimIFace.CAS.CASUtils.CASAGSAvailabilityFlagsFromCASAgeGenderFlags", 977, {0x48, 0x54, 0xbf, 0x41, 0x33, 0x56, 0xe6, 0x22}},
    {"System.Collections.Generic.List`1.Add", 51, {0x81, 0xf7, 0xe9, 0xdb, 0xba, 0xc1, 0xd4, 0x20}},
    {"System.Collections.Generic.List`1.get_Item", 33, {0xa2, 0xc8, 0xcb, 0x7a, 0xc3, 0x59, 0x2c, 0x27}},
    {"System.Collections.Generic.List`1.GrowIfNeeded", 47, {0xe3, 0x17, 0x4f, 0xcc, 0x41, 0xfa, 0x9f, 0xe1}},
    {"Sims3.SimIFace.ObjectGuid.op_Inequality", 20, {0x48, 0xae, 0xc0, 0x6e, 0x96, 0x40, 0x1e, 0xf9}},
    {"Sims3.SimIFace.Slots.GetParentId", 12, {0x9b, 0xdb, 0x3e, 0x2c, 0xe2, 0x8c, 0x56, 0x43}},
    {"ScriptCore.Slots.GetParentId", 18, {0xd1, 0x5d, 0x10, 0x05, 0x82, 0x04, 0x8d, 0xe0}},
    {"Sims3.SimIFace.Vector3..ctor", 22, {0x95, 0xae, 0xef, 0x4b, 0xaf, 0x9e, 0xf9, 0xe1}},
    {"Sims3.SimIFace.Vector3..ctor", 40, {0xed, 0x5c, 0x0a, 0xf5, 0xf4, 0x81, 0x85, 0x70}},
    {"Sims3.SimIFace.Vector3..ctor", 40, {0xfb, 0x55, 0xfb, 0xed, 0xa4, 0x0f, 0x02, 0x2f}},
};
static struct { DWORD size; BYTE sha8[8]; } g_mjHashLogged[64];  // bodies already logged (one line per body, not per instantiation)
static int g_mjHashLoggedN, g_mjRefusedLogged;
static bool g_mjHashGuard;
static char g_mjListBuf[2048];
static u8* g_mjDefer[4096];
static int g_mjDeferN;
static volatile LONG g_mjRefused, g_mjHashMiss, g_mjFaults;
static const DWORD MJ_DEFAULT_HANDLER = 0x00E596C2;  // EA's table entry for the unused opcodes 0x1A9 / 0x1AA [verified: Steam 1.67 .text]
static const u8 kMjAtomicPro[5] = {0x8B, 0x44, 0x24, 0x04, 0xB9};  // GC_malloc_atomic 0xE4BDC0 (verify mode redirects it)

static void MjSync() {
    Ts3nCounters& z = ts3native_counters;
    z.jitMethods = g_jitMethN, z.jitRefused = g_mjRefused, z.jitHashMiss = g_mjHashMiss, z.jitVerified = g_jvOk;
    z.jitMismatch = g_jvMismatch, z.jitFirstBad = g_jitFirstBad, z.jitAbandoned = g_jvAbandoned, z.jitLarge = g_jvLarge;
    z.jitOtherTid = g_jitOtherTid, z.jitFaults = g_mjFaults, z.jitCodeKB = (LONG)(g_jitCodeUsed >> 10), z.jitSampled = g_jvSampled;
}
static void* __cdecl JitTransformHook(u8* rm, void* ctx) {  // a callee's transform (JitPreTransform): the hooked entry, so its post-pass runs too
    return ((void*(__cdecl*)(u8*, void*))(uintptr_t)JIT_XFORM)(rm, ctx);
}
static bool MjHashOk(u8* rm, const char* name) {
    BYTE sha[32];
    DWORD size = 0;
    bool got = false;
    __try {
        BYTE* mm = *(BYTE**)(rm + 8);
        const BYTE* hdr = mm ? g_ilpHeader(mm) : NULL;
        const BYTE* code = hdr ? *(const BYTE* const*)(hdr + 4) : NULL;
        size = hdr ? *(const DWORD*)hdr : 0;
        got = code && size && size <= (1u << 20) && IlpSha256(code, size, sha);
    } __except (EXCEPTION_EXECUTE_HANDLER) { got = false; }
    char hex[65];
    if (!got) { Log("monojit: %s: no readable IL body: not compiled", name); return false; }
    bool known = false;
    for (const MjHash& h : kMjHash) known |= h.size == size && !strcmp(h.name, name) && !memcmp(h.sha8, sha, 8);
    bool seen = false;
    for (int i = 0; i < g_mjHashLoggedN && !seen; i++) seen = g_mjHashLogged[i].size == size && !memcmp(g_mjHashLogged[i].sha8, sha, 8);
    if (!known) InterlockedIncrement(&g_mjHashMiss);
    if (!known && !seen) {
        if (g_mjHashLoggedN < 64) g_mjHashLogged[g_mjHashLoggedN].size = size, memcpy(g_mjHashLogged[g_mjHashLoggedN++].sha8, sha, 8);
        Log("monojit: %s: IL %lu bytes, sha256 %s: %s", name, size, IlpHex(sha, 8, hex), g_mjHashGuard ? "not a known body, not compiled (HashGuard=1)" : "not in kMjHash (HashGuard=0: compiled)");
    }
    return known || !g_mjHashGuard;
}
// compile rm now (sim thread, g_jitCs held); logs the code range so a crash address inside the pool names the method
static void MjCompile(u8* rm, void* ctx, const char* name) {
    if (JitMethOf(rm)) return;
    if (*(const u8*)(uintptr_t)0x00E5637D != 0x75) { InterlockedIncrement(&g_mjRefused); return; }  // ForceJIT: every call re-transforms
    u8* at = NULL;
    __try {
        // Direct=1: the pre-transform compiles listed callees first (their own MjPost); the start is taken after it
        if (ctx && g_jitLazy) JitPreTransform(rm, ctx);
        if (JitMethOf(rm)) return;
        at = g_jitCode + g_jitCodeUsed;
        JitCompiled(rm);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        InterlockedIncrement(&g_mjFaults);
        Log("FAIL monojit: fault 0x%08lX while compiling %s: [MonoJit] off for the session", GetExceptionCode(), name);
        JitSetPatched(false);
        ts3native_counters.jitSwitch = 0;
        g_ilpPost = NULL;
        return;
    }
    const JitMeth* M = JitMethOf(rm);
    if (!M) {
        InterlockedIncrement(&g_mjRefused);
        if (g_mjRefusedLogged++ < 20) Log("monojit: %s: refused by the compiler (see the jit: line above, if any; first 20 logged)", name);
        return;
    }
    Log("monojit: compiled %s: %d native, %d exits, code 0x%08X-0x%08X (%d bytes)%s", M->name, M->nativeOps, M->exitOps, (DWORD)(uintptr_t)at,
        (DWORD)(uintptr_t)(at + M->bytes), M->bytes, M->verifyOk ? "" : " (not verifiable: clauses or a branch to word 0)");
}
static void __cdecl MjPost(BYTE* rm, void* ctx) {
    JitNoteDomain(ctx);
    if (!rm || !*(void**)rm) return;
    char name[256];
    __try { JitMethodName(rm, name, sizeof(name)); } __except (EXCEPTION_EXECUTE_HANDLER) { return; }
    static int cutLogged;  // a cut name: the al260 0xC0000409 candidate (sprintf_s before), so the next run can confirm it
    if (cutLogged < 5 && strlen(name) == sizeof(name) - 1 && !strcmp(name + sizeof(name) - 4, "...")) cutLogged++, Log("monojit: method name over %u chars cut: %s", (unsigned)sizeof(name) - 4, name);
    if (!JitListed(name)) return;
    EnterCriticalSection(&g_jitCs);
    if (!JitMethOf(rm) && MjHashOk(rm, name)) {
        if (g_jitMainTid && GetCurrentThreadId() == g_jitMainTid) MjCompile(rm, ctx, name);
        else {
            bool queued = false;
            for (int i = 0; i < g_mjDeferN; i++) queued |= g_mjDefer[i] == rm;
            if (!queued && g_mjDeferN < 4096) g_mjDefer[g_mjDeferN++] = rm;
            else if (!queued) InterlockedIncrement(&g_mjRefused);
        }
    }
    LeaveCriticalSection(&g_jitCs);
    MjSync();
}
static void __cdecl MjTick() {  // end of every Simulate iteration, sim thread
    if (!g_jitMainTid) {
        g_jitMainTid = GetCurrentThreadId();
        Log("monojit: sim thread %lu (compiled code runs on it only)", g_jitMainTid);
    }
    const bool want = ts3native_counters.jitSwitch != 0;
    if (want != g_jitPatched || g_mjDeferN) {
        EnterCriticalSection(&g_jitCs);
        if (want != g_jitPatched) { JitSetPatched(want); Log("monojit: switch %d: %d methods %s", want ? 1 : 0, g_jitMethN, want ? "patched" : "back to EA's words"); }
        for (int i = 0; i < g_mjDeferN; i++) {
            char name[256];
            JitMethodName(g_mjDefer[i], name, sizeof(name));
            MjCompile(g_mjDefer[i], NULL, name);
        }
        g_mjDeferN = 0;
        LeaveCriticalSection(&g_jitCs);
    }
    MjSync();
}
static bool ApplyMonoJit(const wchar_t* ini) {
    const int on = GetPrivateProfileIntW(L"MonoJit", L"Enabled", 0, ini), mode = GetPrivateProfileIntW(L"MonoJit", L"Mode", 1, ini);
    const bool install = on || GetPrivateProfileIntW(L"MonoJit", L"Install", 0, ini);
    if (!install) return false;
    g_jitVerifyEvery = GetPrivateProfileIntW(L"MonoJit", L"VerifyEvery", 16, ini);
    if (g_jitVerifyEvery < 1) g_jitVerifyEvery = 1;
    g_jitLazy = GetPrivateProfileIntW(L"MonoJit", L"Direct", 0, ini) != 0;
    g_mjHashGuard = GetPrivateProfileIntW(L"MonoJit", L"HashGuard", 1, ini) != 0;
    const unsigned groups = (unsigned)GetPrivateProfileIntW(L"MonoJit", L"Groups", 255, ini);
    wchar_t wl[1024];
    GetPrivateProfileStringW(L"MonoJit", L"List", L"", wl, 1024, ini);
    const void* const ent0 = (const void*)(uintptr_t)(JIT_TABLE + JIT_OP * 4);
    if (!Readable(ent0, 4) || *(const DWORD*)ent0 != MJ_DEFAULT_HANDLER) { Log("SKIP monojit: the jump-table entry of 0x1A9 is not EA's (another owner)"); return false; }
    if (!Matches((const u8*)(uintptr_t)0x00E563B2, kAtBumpOld, sizeof(kAtBumpOld))) { Log("SKIP monojit: 0xE563B2 is patched ([GcRootCheck] TailWatch or another owner of the arena bump)"); return false; }
    if (mode == 2 && !Matches((const u8*)(uintptr_t)0xE4BDC0, kMjAtomicPro, sizeof(kMjAtomicPro))) { Log("SKIP monojit: verify needs GC_malloc_atomic 0xE4BDC0 unpatched"); return false; }
    if (!IlpShaInit()) { Log("SKIP monojit: SHA-256 (bcrypt.dll) unavailable"); return false; }
    InitializeCriticalSection(&g_jitCs);
    g_jitMode = mode == 2 ? 3 : 2;
    g_jitProtect = true, g_jitUnpatchBad = true;
    if (!JitSetup(groups, g_jitMode == 3)) { Log("FAIL monojit: no memory for the tables and the code pool"); return false; }
    g_jitVerifyOn = g_jitMode == 3 ? 1 : 0;
    g_jitPatched = on != 0;
    ts3native_counters.jitSwitch = on ? 1 : 0;
    if (wl[0]) {
        WideCharToMultiByte(CP_UTF8, 0, wl, -1, g_mjListBuf, sizeof(g_mjListBuf), NULL, NULL);
        char* c = NULL;
        for (char* t = strtok_s(g_mjListBuf, ",", &c); t && g_jitNameN < 128; t = strtok_s(NULL, ",", &c)) g_jitNames[g_jitNameN++] = t;
    } else
        for (const char* n : kMjList) g_jitNames[g_jitNameN++] = n;
    if (!SimLoopAdd(MjTick, "monojit tick")) { Log("SKIP monojit: no Simulate iteration hook (the sim thread is not known)"); return false; }
    if (!IlpInstallDetour()) { Log("SKIP monojit: the transform prologue differs (another mod or probe owns 0xE643B0)"); return false; }
    if (g_jitMode == 3 && !JitAtomic(true)) { Log("FAIL monojit: GC_malloc_atomic redirect"); return false; }
    const DWORD ent = (DWORD)(uintptr_t)JitEnter;
    if (!JitWrite((void*)(uintptr_t)(JIT_TABLE + JIT_OP * 4), &ent, 4)) { Log("FAIL monojit: jump-table write"); return false; }
    g_ilpPost = MjPost;
    ts3native_counters.jitOn = g_jitMode == 3 ? 2 : 1;
    Log("OK   monojit (docs/527): %s, %d listed names%s, VerifyEvery %d, Direct %d, Groups %u, HashGuard %d, code pool 0x%08X-0x%08X; switch %d%s",
        g_jitMode == 3 ? "verify" : "compiled", g_jitNameN, wl[0] ? " (List)" : "", g_jitVerifyEvery, g_jitLazy ? 1 : 0, groups, g_mjHashGuard ? 1 : 0,
        (DWORD)(uintptr_t)g_jitCode, (DWORD)(uintptr_t)g_jitCodeEnd, on ? 1 : 0,
        *(const u8*)(uintptr_t)0x00E5637D != 0x75 ? "; MonoPatcher ForceJIT is on now: nothing compiles while it is" : "");
    return true;
}
#endif
