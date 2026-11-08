// docs/207 P0: method compiler prototype for the embedded mint interpreter (monohost only, never in the game).
// Env MONOHOST_JIT=off|dump|on|verify (default off), MONOHOST_JITLIST=<comma list of "Class.Method" suffixes> (default kJitList).
// Included from monohost.cpp after the docs/139 mint code (uses Log, mt::Write, MintName).
//
// How it works (details in docs/207 §8):
// - transform 0xE643B0 is wrapped; a listed method gets compiled right after EA's transform + peephole.
// - every instruction start of a compiled method (but leave.s, see JitWord) gets its opcode word replaced by JIT_OP (0x1A9, an opcode EA never emits;
//   its jump-table entry points to JitEnter). The original words live in a shadow; nothing else in the code changes, so ip
//   values, operands, clause ranges and branch offsets stay EA's.
// - JitEnter maps edi (ip) to the native code of that instruction (hash) and jumps there. Native code runs inside the
//   interpreter's own C frame (ebp), with esi = eval stack pointer and every local, argument and stack slot in the
//   interpreter's frame memory, so the conservative GC sees exactly what it sees today.
// - unsupported opcodes, calls, returns and every throw path leave through "exit to handler": edi = ip, [ebp-4] = ip,
//   eax = edx = original opcode, jmp to EA's handler. The handler then dispatches the next word, which is JIT_OP again.

static const DWORD JIT_OP = 0x1A9, JIT_XFORM = 0xE643B0, OPLEN = 0x1195088, SUPERDESC = 0x108FCD8;
static int g_jitMode;  // 0 off, 1 dump, 2 on, 3 verify
static int g_jitInline;  // MONOHOST_JITINLINE=1|2: inline tiny leaf callees at compiled call sites (docs/207 §8); 2 = tier 2, docs/537 §9
static const char* const kJitList[] = {
    // docs/207 §3 top never-yielding methods that run offline (BCL + SimIFace); Sims3Gameplay* accessors need world objects
    "Dictionary`2.GetPrev", "Dictionary`2.TryGetValue", "Dictionary`2.GetSlot", "Dictionary`2.ContainsKey", "Dictionary`2.set_Item",
    "Dictionary`2.DoAdd", "Enumerator.MoveNext", "Enumerator.get_Current", "Enumerator..ctor", "Enumerator.CurrentSlot",
    "Enum.Equals", "Enum.GetHashCode", "RuntimeTypeHandle.get_Value", "Type.GetTypeFromHandle", "Array.Resize", "Array.IndexOf",
    "DefaultComparer.Equals", "DefaultComparer.GetHashCode", "IEquatableOfTEqualityComparer`1.GetHashCode",
    "IEquatableOfTEqualityComparer`1.Equals", "List`1.GetEnumerator", "List`1.Add", "List`1.get_Item", "List`1.GrowIfNeeded",
    "List`1.get_Count", "UInt64.GetHashCode", "UInt64.Equals", "Int32.GetHashCode", "Int32.Equals", "ObjectGuid.op_Inequality",
    "ObjectGuid.op_Equality", "CASUtils.CASAGSAvailabilityFlagsFromCASAgeGenderFlags", "Math.Max",
    // the callees the hot rows wait on; Math.Min, Single.IsNaN back with the float compares (docs/537 §8)
    "Math.Min", "Single.IsNaN", "Double.IsNaN", "Enumerator.CurrentSlot", "List`1.get_Capacity", "KeyValuePair`2..ctor", "Dictionary`2.DoHash",
    "ValueType.DefaultEquals", "Enumerator.VerifyState", "Array.Copy", "Array.GetUpperBound", "Dictionary`2.set_Count", "List`1.set_Capacity", "Object.Equals",
    "Slots.GetParentId", "Vector3..ctor", "ObjectGuid.GetHashCode", "ObjectGuid.Equals",
    // docs/data/monojit-exits-handoff.md C2: callees of the call exits (lazy bodies); ObjectComponent.get_BaseType overrides, one per class
    "SimClock.ElapsedTime", "SimClock.get_CurrentTicks", "SimClock.ConvertFromTicks", "ScriptObject.get_Proxy", "ScriptProxy.get_ObjectId", "ObjectGuid..ctor",
    "ItemComponent.get_BaseType", "PortalComponent.get_BaseType", "DonatableComponent.get_BaseType", "ObjectOwnerComponent.get_BaseType",
    "RoutingComponent.get_BaseType", "MaintenanceComponent.get_BaseType", "InteractionsDisabledComponent.get_BaseType",
    "BoobyTrapComponent.get_BaseType", "CleanableComponent.get_BaseType", "PartComponent.get_BaseType", "PetSurfaceComponent.get_BaseType",
    "LootComponent.get_BaseType", "DoorRouteThroughComponent.get_BaseType", "RepairableComponent.get_BaseType", "CatHuntingComponent.get_BaseType",
    "TombObjectStaticComponent.get_BaseType", "TombObjectDynamicComponent.get_BaseType", "SandsOfUnderstandingComponent.get_BaseType",
    "ResortStaffedObjectComponent.get_BaseType", "SittableComponent.get_BaseType", "HarvestableComponent.get_BaseType",
    "PlantableComponent.get_BaseType", "UpgradableComponent.get_BaseType", "AdventureComponent.get_BaseType", "NameComponent.get_BaseType",
    "AdventureFXComponent.get_BaseType", "ScienceComponent.get_BaseType", "ComeAndSeeComponent.get_BaseType", "SyphonableComponent.get_BaseType",
    "InventoryComponent.get_BaseType", "FertilizerComponent.get_BaseType", "StairsRouteThroughComponent.get_BaseType",
    "OpportunitySetupObjectComponent.get_BaseType", "SculptureComponent.get_BaseType", "OverlayComponent.get_BaseType",
    "TreasureComponent.get_BaseType", "WorldBuilderDeathComponent.get_BaseType",
    "HostTest.JitId", "HostTest.JitOut", "HostTest.JitSum", "HostTest.JitInl", "HostTest.JitLz", "HostTest.LzClamp", "LzBox.Get",
    "HostTest.JitLzChain", "HostTest.LzMid", "HostTest.JitLzVirt", "LzSq.Area", "HostTest.JitLzAlloc", "HostTest.LzAlloc",
};
static char g_jitListBuf[8192];
static const char* g_jitNames[256];
static int g_jitNameN;
static FILE* g_jitDump;
static CRITICAL_SECTION g_jitCs;

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

static void JitDumpMethod(const u8* rm, const char* name) {
    if (!g_jitDump) return;
    const unsigned short* code = *(const unsigned short* const*)rm;
    int nw = *(const unsigned short*)(rm + 0x3A);
    fprintf(g_jitDump, "== %s rm %p words %d flags %08lX params %d clauses %d arena %u eval@%u areas %u %u\n", name, rm, nw,
            *(const DWORD*)(rm + 0x44), rm[0x40], rm[0x41], *(const DWORD*)(rm + 0x38), *(const unsigned short*)(rm + 0x3E),
            *(const unsigned short*)(rm + 0x34), *(const unsigned short*)(rm + 0x36));
    char b[16];
    for (int i = 0; i < nw;) {
        int len = code[i] == 0xE8 ? JitSwitchLen(code + i) : JitLen(code + i);
        fprintf(g_jitDump, "  %04X %-16s", i, MintName(code[i], b));
        for (int k = 1; k < len && k < 8; k++) fprintf(g_jitDump, " %04X", code[i + k]);
        fprintf(g_jitDump, "\n");
        if (len <= 0) { fprintf(g_jitDump, "  (stop: length %d)\n", len); break; }
        i += len;
    }
    fflush(g_jitDump);
}

static const BYTE* const g_jitSse2 = (const BYTE*)(uintptr_t)0x01234374;  // EA's _sse2_available (docs/221)
static bool g_jitSse3;  // MONOHOST_JITWIDE=1: group 512 and methods with clauses are unchanged here (docs/537 §10)
static bool g_jitC5 = true;  // MONOHOST_JITC2 bit 256: the direct internal calls (the templates of the group go by table)
static bool g_jitLazy, g_jitLazyPass;  // MONOHOST_JITLAZY (docs/207 §12); the lazy body is being emitted
// docs/537 §20 A, off by default. MONOHOST_JITCALLEXIT=1 ([MonoJit] CallExit): a callee with hot exits still gets a lazy body
// (its exits write the frame first, as the cold ones do), the pure Array icalls GetLength / GetLowerBound / GetRank run inline,
// and a guarded callvirt takes a boxed value-type receiver (this = box + 4, as EA's 0xE54B3E).
static bool g_jitCallExit, g_jitPureIcalls;
// docs/537 §20 B1, off by default. MONOHOST_JITVTOPS=1 ([MonoJit] VtOps): stfld.vt, ldelem.any.vt, stelem.any.vt, ldobj (value
// type) and stobj.vt compile natively (template group 1024)
static bool g_jitVtOps;
// docs/537 §20 B2b, off by default. MONOHOST_JITNEWVT=1 ([MonoJit] NewVt, game key not added yet): newobj of a value-type class
// calls the ctor's lazy body natively (this = the vt cursor, EA's 0xE5620C), see EmitLazyCall
static bool g_jitNewVt;
static int g_jitNewVtSites;
// handoff C3, off by default. MONOHOST_JITFASTCOPY=1 (game key not added yet): Array.FastCopy runs inline at its call site for
// same-element value-type arrays without references (EA's memmove path); every other input exits to EA's call (JitFastCopy)
static bool g_jitFastCopy;
// handoff LDSFLDA, off by default. MONOHOST_JITLDSFLDA=1 (game key not added yet): ldsflda compiles to its template (class-init
// exit, then [vt+0xC] + offset; EA's 0xE56D20). Without it the row's width 0 sends every ldsflda to an exit (as before)
static bool g_jitLdsflda;
// [MonoJit] Capture=1 (monojit-capture.h): on before any compile, so the compiler emits the lazy-body and inline-site sample calls only then
static volatile int g_jcOn;
static void JcLazyStub();
static void JcInlineStub();
static void* g_jcInlineStubP = (void*)JcInlineStub;  // an inlined site calls through this cell: its code is built in a scratch buffer
static void JitCompiled(u8* rm);  // compiler (below)
static void JitPreTransform(u8* rm, void* ctx);  // inliner (below)
static u8* g_xformTramp;
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

static void* __cdecl JitTransformHook(u8* rm, void* ctx) {
    void* exc = ((void*(__cdecl*)(u8*, void*))(void*)g_xformTramp)(rm, ctx);
    if (ctx && !g_jitDomain) { g_jitDomain = *(void**)ctx; Log("jit: domain %p (the transform context's)\n", g_jitDomain); }
    if (exc || !rm || !*(void**)rm) return exc;
    char name[256];
    JitMethodName(rm, name, sizeof(name));
    if (!JitListed(name)) return exc;
    EnterCriticalSection(&g_jitCs);
    if (g_jitMode >= 2 && (g_jitInline || g_jitLazy)) JitPreTransform(rm, ctx);  // first: callees dump and compile before rm
    JitDumpMethod(rm, name);
    if (g_jitMode >= 2) JitCompiled(rm);
    LeaveCriticalSection(&g_jitCs);
    return exc;
}

static bool InstallJitHook() {
    static const u8 kPro[6] = {0x55, 0x8B, 0xEC, 0x83, 0xEC, 0x78};
    if (memcmp((void*)(uintptr_t)JIT_XFORM, kPro, 6)) { Log("jit: transform prologue differs\n"); return false; }
    g_xformTramp = (u8*)VirtualAlloc(NULL, 4096, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
    memcpy(g_xformTramp, kPro, 6);
    g_xformTramp[6] = 0xE9;
    *(DWORD*)(g_xformTramp + 7) = JIT_XFORM + 6 - ((DWORD)(uintptr_t)g_xformTramp + 11);
    return Jmp(JIT_XFORM, (const void*)JitTransformHook);
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
    K_STFLDVT, K_LDELEMVT, K_STELEMVT, K_LDOBJVT, K_STOBJVT,  // docs/537 §20 B1, group 1024 (MONOHOST_JITVTOPS=1)
    K_FCMP, K_FBRS, K_FBR,  // docs/221, the float compares
    K_FARITH, K_FNEG, K_FILD, K_FR4R8, K_CVT, K_FISTT, K_LDINDFP, K_STINDFP, K_LDSTR, K_LDTOKEN, K_CASTCLASS  // S5, group 512: stay last
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
static volatile LONG* const g_jitSatbOnP = &mt::g_satbOn;
static volatile DWORD* const g_jitSatbWTidP = &mt::g_satbWTid;
static void __cdecl JitSatbStore(DWORD addr, DWORD n) {
    if (!mt::SatbSimSide()) return;
    g_jitSatbStores++;
    mt::SatbSimRange(addr, n);
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
        b(0x83); b(0x3D); d((DWORD)(uintptr_t)g_jitSatbOnP); b(0);  // cmp dword ptr [g_satbOn], 0
        b(0x74); u8* skip = p; b(0);                                 // je skip
        b(0x50 + base); op(0x8D, EDX, base, disp);                   // push base; lea edx, [base+disp]
        b(0x68); d(bytes ? bytes : w <= 2 ? 1 : w <= 4 ? 2 : w == 5 || w == 7 ? 4 : 8); b(0x52);  // push bytes; push edx
        b(0xE8); d((DWORD)(uintptr_t)JitSatbStore - ((DWORD)(uintptr_t)p + 4));  // call JitSatbStore
        b(0x83); b(0xC4); b(8); b(0x58 + base);                      // add esp, 8; pop base
        *skip = (u8)(p - (skip + 1));
    }
};
static void Fix(u8* rel, const u8* target) { *(int*)rel = (int)(target - (rel + 4)); }

// ---- compiled method table: ip -> native (open addressing, 16-byte entries) ----
struct JitEnt { DWORD ip; u8* native; DWORD orig; u8* rm; };
static const DWORD JIT_TAB_N = 1 << 16;
static JitEnt* g_jitTab;  // JIT_TAB_N entries + a 4096-entry overflow tail (no wrap in the probe loop)
static int g_jitEntN;
static u8* g_jitCode;     // native code pool (PAGE_EXECUTE_READWRITE in the host; the game would flip RW -> RX)
static u8* g_jitCodeEnd;
static size_t g_jitCodeUsed;
struct JitMeth { u8* rm; unsigned short* code; int words; int pos0, posN; char name[96]; int nativeOps, exitOps, bytes; bool verifyOk;
                 volatile LONG verified, mismatches, skipped, inner; };
static JitMeth g_jitMeth[4096];
static int g_jitMethN;
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
// unbox (CallExit): 4 when the cache's class is a value type and the target takes a value-type this, as EA's callvirt passes
// box + 4 then (0xE54B2C-0xE54B41 [verified]); the site adds it to the receiver.
struct JitGuardSite { const u8* rm; u8* entry; DWORD w38, w3E, wLoc, w3C, wVt; volatile LONG fills; const DWORD* cls; DWORD unbox; };
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
    const u8* k = s->cls ? (const u8*)(uintptr_t)*s->cls : NULL;  // the cache's class, the one the site just compared
    s->unbox = (fl & 0x400) && k && (k[0x14] & 8) ? 4 : 0;
    s->entry = cell->entry;
}

static bool JitConRef(const u8* k);
// exit to EA's handler of base opcode `op` at ip (state is exactly the interpreter's: esi, frame memory)
static void EmitExit(Em& e, const unsigned short* ip, DWORD op, bool ret) {
    if (g_jitLazyPass) {  // lazy body: write the frame first (JitLazyMat), then the same exit
        if (g_jitCountOn && g_jitSite && g_jitSiteN < 65536) {  // a lazy exit is an exit site of its own (census before docs/537 §20 left these out)
            JitSite& s = g_jitSite[g_jitSiteN++];
            s.meth = g_jitMethN, s.op = (unsigned short)op, s.slow = g_jitSiteSlow != 0, s.n = 0, s.miss = 0, s.ip = ip;
            e.incAbs(&s.n);
        }
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
    e.b(0xFF); e.b(0x25); e.d(MINT_TABLE + op * 4);  // jmp dword ptr [table + op*4]: the live entry (Satb's stub in a span)
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
        e.b(0xE8); e.d(0x00D70870 - ((DWORD)(uintptr_t)e.p + 4));
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
    {"System", "Type", "internal_from_handle", 0x00E7AB60}, {"System", "Enum", "get_value", 0x00E7D520},
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
// docs/537 §20 A: the pure Array icalls, inline at a call / callvirt site. EA's functions [verified: disassembly; the icall table's
// pointers at 0x10B58D4 / 0x10B58D8 / 0x10B58DC hold these addresses, JitPureIcallsOk checks bytes and table once]:
// GetLength(this, dim) 0xE79FC0 and GetLowerBound(this, dim) 0xE7A000 raise IndexOutOfRangeException iff dim < 0 or dim >=
// klass->rank (vtable->klass +0x10), else bounds (this+4) NULL gives max_length (this+8) / 0, else bounds[dim] .length (+0) /
// .lower_bound (+4); GetRank(this) 0xE79FB0 is klass->rank. They read no frame and do not allocate, so a lazy body may run them.
// A null this or a bad dimension exits before any change: EA's call path throws. EA's icall return leaves the result slot's high
// dword as the word above the arguments ([esi+4]: its return slot, of which only the low dword is written; as EmitIcall).
static const u8 kPureGetLength[] = {0x56, 0x8B, 0x74, 0x24, 0x08, 0x57, 0x8B, 0x7C, 0x24, 0x10, 0x85, 0xFF, 0x7C, 0x0C, 0x8B, 0x06, 0x8B, 0x08, 0x0F, 0xB6,
    0x51, 0x10, 0x3B, 0xFA, 0x7C, 0x0E, 0xE8, 0xC1, 0xD2, 0xFF, 0xFF, 0x50, 0xE8, 0x0B, 0x79, 0x02, 0x00, 0x83, 0xC4, 0x04, 0x8B, 0x46, 0x04, 0x85,
    0xC0, 0x75, 0x06, 0x8B, 0x46, 0x08, 0x5F, 0x5E, 0xC3, 0x8B, 0x04, 0xF8};
static const u8 kPureGetLower[] = {0x56, 0x8B, 0x74, 0x24, 0x0C, 0x85, 0xF6, 0x57, 0x8B, 0x7C, 0x24, 0x0C, 0x7C, 0x0C, 0x8B, 0x07, 0x8B, 0x08, 0x0F, 0xB6,
    0x51, 0x10, 0x3B, 0xF2, 0x7C, 0x0E, 0xE8, 0x81, 0xD2, 0xFF, 0xFF, 0x50, 0xE8, 0xCB, 0x78, 0x02, 0x00, 0x83, 0xC4, 0x04, 0x8B, 0x47, 0x04, 0x85,
    0xC0, 0x75, 0x03, 0x5F, 0x5E, 0xC3, 0x8B, 0x44, 0xF0, 0x04, 0x5F, 0x5E, 0xC3};
static const u8 kPureGetRank[] = {0x8B, 0x44, 0x24, 0x04, 0x8B, 0x08, 0x8B, 0x11, 0x0F, 0xB6, 0x42, 0x10, 0xC3};
static bool JitPureIcallsOk() {
    return !memcmp((const void*)(uintptr_t)0xE79FC0, kPureGetLength, sizeof(kPureGetLength)) && !memcmp((const void*)(uintptr_t)0xE7A000, kPureGetLower, sizeof(kPureGetLower))
        && !memcmp((const void*)(uintptr_t)0xE79FB0, kPureGetRank, sizeof(kPureGetRank)) && *(const DWORD*)(uintptr_t)0x10B58D4 == 0xE79FC0
        && *(const DWORD*)(uintptr_t)0x10B58D8 == 0xE7A000 && *(const DWORD*)(uintptr_t)0x10B58DC == 0xE79FB0;
}
static volatile LONG g_jitPure;  // pure icall sites compiled (a lazy pass counts again)
static int JitPureIcall(const unsigned short* ip, DWORD op, const u8* callerRm) {  // 1 GetLength, 2 GetLowerBound, 3 GetRank, 0 none
    const u8* c = (const u8*)(uintptr_t)(*(const DWORD* const*)(callerRm + 0x14))[op == 0xEA ? ip[1] : ip[3]];
    const u8* mm = c ? *(const u8* const*)(c + 8) : NULL;
    if (!mm || !(*(const unsigned short*)(mm + 2) & 0x1000) || (*(const unsigned short*)mm & 0x40)) return 0;  // internal call, not virtual
    const u8* k = *(const u8* const*)(mm + 8);
    if (strcmp(*(const char* const*)(k + 0x34), "Array") || strcmp(*(const char* const*)(k + 0x38), "System")) return 0;
    const char* n = *(const char* const*)(mm + 0x18);
    return !strcmp(n, "GetLength") ? 1 : !strcmp(n, "GetLowerBound") ? 2 : !strcmp(n, "GetRank") ? 3 : !strcmp(n, "FastCopy") ? 4 : 0;
}
// handoff C3: Array.FastCopy(source, source_idx, dest, dest_idx, length) 0xE7A070 (icall table 0x10B58CC) [verified: disassembly]:
// returns false unless both ranks (klass+0x10) match and both bounds (+4) are NULL, and idx + length <= max_length (+8, unsigned)
// for dest then source; element classes (klass+8) equal and a value type (flags +0x14 bit 3) without references (bit 23) =
// memmove(dest + 0x10 + size * dest_idx, source + 0x10 + size * source_idx, size * length), size = source klass+0x5C, true. The
// other paths (object[] unboxing, castable classes, references with a barrier) stay EA's. Here only the memmove path runs; any
// other input, a null array or a negative index or length (managed Array.Copy throws before the call) returns 0 with nothing
// changed and the site exits, so EA's call runs as before. No allocation, no frame read, no reference stored; the destination
// range still goes to JitSatbStore first (a span logs every overwritten heap word, as the stfld templates and the game's
// FastCopy wrapper SatbRtFastCopy do).
static const DWORD kFastCopyFnv = 0x555A37B8;  // FNV-1a of EA's 641 bytes 0xE7A070..0xE7A2F0
static bool JitFastCopyOk() {
    DWORD h = 0x811C9DC5;
    for (const u8* p = (const u8*)(uintptr_t)0xE7A070; p < (const u8*)(uintptr_t)0xE7A2F1; p++) h = (h ^ *p) * 0x01000193;
    return h == kFastCopyFnv && *(const DWORD*)(uintptr_t)0x10B58CC == 0xE7A070;
}
static volatile LONG g_jitFastCopySites, g_jitFastCopyRuns, g_jitFastCopyExits;
static int __cdecl JitFastCopy(const u8* src, int si, u8* dst, int di, int len) {
    if (!src || !dst || si < 0 || di < 0 || len < 0) return g_jitFastCopyExits++, 0;
    const u8* sk = **(const u8* const* const*)src, *dk = **(const u8* const* const*)dst;
    if (sk[0x10] != dk[0x10] || *(void* const*)(src + 4) || *(void* const*)(dst + 4)) return g_jitFastCopyExits++, 0;
    if ((unsigned)(di + len) > *(const unsigned*)(dst + 8) || (unsigned)(si + len) > *(const unsigned*)(src + 8)) return g_jitFastCopyExits++, 0;
    const u8* el = *(const u8* const*)(sk + 8);
    const DWORD fl = el ? *(const DWORD*)(el + 0x14) : 0;
    if (el != *(const u8* const*)(dk + 8) || !(fl & 8) || (fl & 0x800000)) return g_jitFastCopyExits++, 0;
    const int sz = *(const int*)(sk + 0x5C);
    if (*g_jitSatbOnP && len) JitSatbStore((DWORD)(uintptr_t)(dst + 0x10 + sz * di), (DWORD)(sz * len));
    memmove(dst + 0x10 + sz * di, src + 0x10 + sz * si, (size_t)(unsigned)(sz * len));
    g_jitFastCopyRuns++;
    return 1;
}
static void EmitPureIcall(Em& e, int kind, u8** exitRel) {
    const int thisOff = kind == 3 ? -8 : -16;
    if (kind == 4) {  // FastCopy: five args at [esi-40]..[esi-8]; 0 = exit before any change; the bool result keeps [esi+4] high, as kind 1
        for (int d = -8; d >= -40; d -= 8) e.op(0xFF, 6, ESI, d);  // push dword [esi+d]: length .. source
        e.b(0xE8); e.d((DWORD)(uintptr_t)JitFastCopy - ((DWORD)(uintptr_t)e.p + 4)); e.b(0x83); e.b(0xC4); e.b(20);
        e.rr(0x85, EAX, EAX); exitRel[0] = e.jcc(0x84);
        e.op(0x8B, ECX, ESI, 4); e.op(0x89, EAX, ESI, -40); e.op(0x89, ECX, ESI, -36); e.addEsi(-32);
        g_jitFastCopySites++; return;
    }
    e.op(0x8B, EAX, ESI, thisOff); e.rr(0x85, EAX, EAX); exitRel[0] = e.jcc(0x84);
    e.op(0x8B, EDX, EAX, 0); e.op(0x8B, EDX, EDX, 0); e.b(0x0F); e.b(0xB6); e.b(0x52); e.b(0x10);  // movzx edx, byte [edx+0x10]: rank
    if (kind == 3) { e.op(0x8B, ECX, ESI, 4); e.op(0x89, EDX, ESI, -8); e.op(0x89, ECX, ESI, -4); g_jitPure++; return; }
    e.op(0x8B, ECX, ESI, -8); e.rr(0x3B, EDX, ECX); exitRel[1] = e.jcc(0x83);                      // cmp ecx, edx; jae (dim < 0 or >= rank)
    e.op(0x8B, EDX, EAX, 4); e.rr(0x85, EDX, EDX); e.b(0x74); u8* nob = e.p; e.b(0);              // bounds; jz nob
    e.b(0x8B); e.b(0x44); e.b(0xCA); e.b(kind == 1 ? 0 : 4);                                        // mov eax, [edx+ecx*8 (+4)]
    e.b(0xEB); u8* st = e.p; e.b(0);
    *nob = (u8)(e.p - (nob + 1));
    if (kind == 1) e.op(0x8B, EAX, EAX, 8); else e.rr(0x33, EAX, EAX);                             // max_length, or 0
    *st = (u8)(e.p - (st + 1));
    e.op(0x8B, ECX, ESI, 4); e.op(0x89, EAX, ESI, -16); e.op(0x89, ECX, ESI, -12); e.addEsi(-8);
    g_jitPure++;
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
static volatile LONG g_jitInlined, g_jitInlineNo, g_jitInlineHits;
static bool JitInlineNo(const u8* c, DWORD op) {  // the host's log row: which opcode stopped a callee
    char nm[256];
    const char* opn = op <= 0x1A8 ? ((const char* const*)(uintptr_t)0x11949E0)[op] : "superop";
    Log("jitinlineno %s %s\n", opn ? opn : "?", JitMethodName(c, nm, sizeof(nm)));
    return g_jitInlineNo++, false;
}
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
        if (w == 0xE8 || w > 0x1C0 || w == 0x1A9 || w == 0x1AA) return JitInlineNo(c, 0);
        unsigned short tmp[2] = {w, 0};
        int len = w <= 0x1A8 ? ((const u8*)(uintptr_t)OPLEN)[w] : JitLen(tmp);
        if (len <= 0) return JitInlineNo(c, 0);
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
            return JitInlineNo(c, 0);
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
    b.b(0x80); b.b(0x3D); b.d(0x00E5637D); b.b(0x75); slow[ns++] = b.jcc(0x85);  // MonoPatcher ForceJIT active
    if (!vtm) {  // EA's remoting check reads this->vtable->klass
        b.op(0x8B, ECX, EAX, 0); b.rr(0x85, ECX, ECX); slow[ns++] = b.jcc(0x84);
        b.op(0x8B, ECX, ECX, 0); b.b(0x3B); b.b(0x0D); b.d(0x01220EC0); slow[ns++] = b.jcc(0x84);
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
    if (no - k > 12) return JitInlineNo(c, 0);
    for (; k < no; k++) {
        const JitOpInfo t = g_opInfo[ops[k]];
        const int w = t.width, u16 = at[k][1];
        int r = sp < 2 ? regs[sp] : -1, r0 = sp >= 2 ? regs[sp - 2] : -1, r1 = sp >= 1 ? regs[sp - 1] : -1;
        switch (t.kind) {
        case K_LDTHIS: if (r < 0) return JitInlineNo(c, ops[k < no ? k : no - 1]); b.rr(0x8B, EAX, r); isThis[sp++] = true; break;  // mov r, eax
        case K_LDARG: {
            int idx = -1;
            for (int a = 0; a < n; a++) if ((int)argOff[a] == u16) idx = a;
            if (r < 0 || idx < 0 || w == 6) return JitInlineNo(c, ops[k < no ? k : no - 1]);
            b.load(w, r, ESI, -(n - idx) * 8); isThis[sp++] = false; break;
        }
        case K_LDFLD:
            if (sp < 1 || w >= 6 || (!isThis[sp - 1] && !tier2)) return JitInlineNo(c, ops[k < no ? k : no - 1]);  // 6, 7: the 8-byte and x87 forms
            if (isThis[sp - 1]) b.load(w, r1, EAX, u16);
            else { b.rr(0x85, r1, r1); slow[ns++] = b.jcc(0x84); b.load(w, r1, r1, u16); }  // a null: EA's path, nothing written yet
            isThis[sp - 1] = false; break;
        case K_LDIND:  // EA does not check; a null leaves the fault to EA's path
            if (sp < 1 || w >= 6 || !tier2) return JitInlineNo(c, ops[k < no ? k : no - 1]);
            if (isThis[sp - 1]) b.load(w, r1, EAX, 0);
            else { b.rr(0x85, r1, r1); slow[ns++] = b.jcc(0x84); b.load(w, r1, r1, 0); }
            isThis[sp - 1] = false; break;
        case K_LDLEN:  // array + 8; a null: EA's path throws
            if (sp < 1 || isThis[sp - 1] || !tier2) return JitInlineNo(c, ops[k < no ? k : no - 1]);
            b.rr(0x85, r1, r1); slow[ns++] = b.jcc(0x84); b.load(5, r1, r1, 8); break;
        case K_LDC: if (r < 0) return JitInlineNo(c, ops[k < no ? k : no - 1]); b.movRI(r, (DWORD)t.x); isThis[sp++] = false; break;
        case K_LDCS: if (r < 0) return JitInlineNo(c, ops[k < no ? k : no - 1]); b.movRI(r, (DWORD)(int)(short)u16); isThis[sp++] = false; break;
        case K_LDCI4: if (r < 0) return JitInlineNo(c, ops[k < no ? k : no - 1]); b.movRI(r, *(const DWORD*)(at[k] + 1)); isThis[sp++] = false; break;
        case K_BIN: if (sp < 2) return JitInlineNo(c, ops[k < no ? k : no - 1]); b.rr(t.x, r0, r1); sp--; isThis[sp - 1] = false; break;
        case K_MUL: if (sp < 2) return JitInlineNo(c, ops[k < no ? k : no - 1]); b.b(0x0F); b.b(0xAF); b.b(0xC0 | (r0 << 3) | r1); sp--; isThis[sp - 1] = false; break;
        case K_CMP:
            if (sp < 2) return JitInlineNo(c, ops[k < no ? k : no - 1]);
            b.rr(0x39, r0, r1); b.b(0x0F); b.b(t.x); b.b(0xC0 | r0); b.b(0x0F); b.b(0xB6); b.b(0xC0 | (r0 << 3) | r0);
            sp--; isThis[sp - 1] = false; break;
        case K_CEQ0:
            if (sp < 1) return JitInlineNo(c, ops[k < no ? k : no - 1]);
            b.rr(0x85, r1, r1); b.b(0x0F); b.b(0x94); b.b(0xC0 | r1); b.b(0x0F); b.b(0xB6); b.b(0xC0 | (r1 << 3) | r1);
            isThis[sp - 1] = false; break;
        case K_INC: case K_DEC: if (sp < 1) return JitInlineNo(c, ops[k < no ? k : no - 1]); b.b(0x83); b.b((t.kind == K_INC ? 0xC0 : 0xE8) | r1); b.b(1); isThis[sp - 1] = false; break;
        case K_UN: if (sp < 1) return JitInlineNo(c, ops[k < no ? k : no - 1]); b.b(0xF7); b.b(0xC0 | (t.x << 3) | r1); isThis[sp - 1] = false; break;
        case K_CONV:
            if (sp < 1 || t.x == 1 || t.x == 2) return JitInlineNo(c, ops[k < no ? k : no - 1]);
            if (t.x >= 4) { static const u8 kx[4] = {0xBE, 0xB6, 0xBF, 0xB7}; b.b(0x0F); b.b(kx[t.x - 4]); b.b(0xC0 | (r1 << 3) | r1); }
            isThis[sp - 1] = false; break;
        case K_POP: if (sp < 1) return JitInlineNo(c, ops[k < no ? k : no - 1]); sp--; break;
        case K_RET: ret = k; break;
        default: return JitInlineNo(c, ops[k < no ? k : no - 1]);
        }
        if (ret >= 0) break;
    }
    const char* rn = ret >= 0 ? ((const char* const*)(uintptr_t)0x11949E0)[ops[ret]] : "";
    if (ret != no - 1 || (value ? strcmp(rn, "ret") || sp != 1 : strcmp(rn, "ret.void") || sp != 0)) return JitInlineNo(c, ops[k < no ? k : no - 1]);
    if (value) {  // no test from here on. High dword of EA's return copy: callee eval slot 0; then the low dword
        b.op(0x8B, EAX, EBP, 8); b.op(0x8B, EAX, EAX, 0x13C); b.op(0x8B, EAX, EAX, 0); b.op(0x8B, EAX, EAX, w3E + 4);
        b.op(0x89, EAX, ESI, thisOff + 4);
        b.op(0x89, regs[0], ESI, thisOff);
    }
    if (g_jitCountOn) b.incAbs(&g_jitInlineHits);
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
    { char nm[256]; Log("jitinline %s\n", JitMethodName(c, nm, sizeof(nm))); }
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
static const char* const* const JIT_OPNAMES = (const char* const*)0x11949E0;  // EA's opcode names
// a position whose straight run ends in throw is cold: its exit writes the frame, as a null or bound exit does, on the way to an
// exception, so such a K_EXIT does not hold a lazy body back (docs/537 §8). No clauses here, so nothing resumes in the body.
static bool JitFlowName(const char* s) {  // ends a straight run: ret*, branches, switch, leave, endfinally
    return !strncmp(s, "ret", 3) || (s[0] == 'b' && strncmp(s, "box", 3)) || !strcmp(s, "switch") || !strncmp(s, "leave", 5) || !strncmp(s, "end", 3);
}
static void JitMarkCold(int n, const unsigned short* posOp, bool* cold) {
    bool c = false;
    for (int k = n - 1; k >= 0; k--) {
        const char* s = posOp[k] < 0x1A9 ? JIT_OPNAMES[posOp[k]] : NULL;
        c = s && (!strcmp(s, "throw") || (c && !JitFlowName(s)));
        cold[k] = c;
    }
}
// a lazy call site's target and kind: call (0xEA, method in word 1), con.call (0xEB: the class word is skipped and the
// method is word 2, docs/213 §1 row 3), callvirt (0xEC, declared method in word 3) or con.callvirt of a reference type
// (0xED, method in word 4) of a non-virtual method of a reference type (direct) or of a virtual one (class guard on EA's
// inline cache, v2). A direct target must be transformed (the site bakes in its frame sizes); a guarded site reads them
// from its JitGuardSite at run time.
// B2b: the class a newobj (0xF4, ctor in word 1) builds, when it takes EA's value-type branch [verified 0xE56152-0xE56215]:
// class = ctor->method->klass; not the array-ctor path ([class+0x24] == [0x1220E8C] goes to 0xE56188); valuetype flag 8, then
// 0xE52160 (VALUETYPE and not an enum, or a GENERICINST of a value-type container). Layout flag 4 is ours (EA does not read the
// size here: the vtresult after the newobj advances the cursor); a class failing any test stays EA's newobj for good.
static const u8* JitNewVtClass(const u8* c) {
    const u8* mm = c ? *(const u8* const*)(c + 8) : NULL;
    const u8* k = mm ? *(const u8* const*)(mm + 8) : NULL;
    if (!k || *(const DWORD*)(k + 0x24) == *(const DWORD*)(uintptr_t)0x1220E8C || !(k[0x14] & 8) || !(k[0x14] & 4)) return NULL;
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
    if (!guard && !(fl & 0x100)) return false;  // not transformed: its frame sizes are still 0
    *cOut = c, *guardOut = guard;
    return true;
}
// A2 census (count mode): bails per lazy call site and reason; lazy-body exits that wrote frames per (callee, exit ip)
static const char* const kLazyBail[12] = {"null this", "null *this", "null vtable", "remoting", "boxed vt", "cache class", "no lazy body",
                                          "ForceJIT", "depth slot", "no MonoInvocation", "no arena chunk", "arena full"};
struct JitLazyStat { const u8* callerRm; const unsigned short* ip; unsigned short op; bool lazy, guard; volatile LONG r[12]; };
static JitLazyStat g_jitLazyStat[4096];
static int g_jitLazyStatN;
struct JitMatStat { const u8* rm; DWORD ip; unsigned short op; LONG n; };
static JitMatStat g_jitMatStat[2048];
static int g_jitMatStatN;
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
    if (guard) { if (g_jitGuardN >= 4096) return false; gs = &g_jitGuard[g_jitGuardN++]; gs->cls = &(*(const DWORD* const*)(callerRm + 0x14))[ip[1]]; }
    else if (!(cell = JitLazyCellFor(c))) return false;
    const DWORD* items = *(const DWORD* const*)(callerRm + 0x14);
    const int n = c[0x40], thisOff = -(n + 1) * 8, rsp = inst && !nvt ? thisOff : -n * 8, ripW = op == 0xEA || nvt ? 2 : op == 0xEB ? 3 : op == 0xEC ? 4 : 5;
    const unsigned short w38 = *(const unsigned short*)(c + 0x38), w3E = *(const unsigned short*)(c + 0x3E);
    const unsigned short w34 = *(const unsigned short*)(c + 0x34), w36 = *(const unsigned short*)(c + 0x36);
    u8* bail[16];
    u8 why[16];  // count mode: the bail reason per jump (kLazyBail)
    int nb = 0;
    if (guard) {  // receiver not null, with a vtable, not the remoting class, not a value type, of the cache's class; target = the cache's
        e.op(0x8B, ECX, ESI, thisOff); e.rr(0x85, ECX, ECX); why[nb] = 0, bail[nb++] = e.jcc(0x84);
        if (op == 0xED) { e.op(0x8B, ECX, ECX, 0); e.rr(0x85, ECX, ECX); why[nb] = 1, bail[nb++] = e.jcc(0x84); }  // this = *ptr
        e.op(0x8B, EAX, ECX, 0); e.rr(0x85, EAX, EAX); why[nb] = 2, bail[nb++] = e.jcc(0x84);
        e.op(0x8B, EAX, EAX, 0); e.b(0x3B); e.b(0x05); e.d(0x01220EC0); why[nb] = 3, bail[nb++] = e.jcc(0x84);
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
    e.b(0x80); e.b(0x3D); e.d(0x00E5637D); e.b(0x75); why[nb] = 7, bail[nb++] = e.jcc(0x85);                  // MonoPatcher ForceJIT active
    if (nvt)
        VtLoad(e, ECX);  // this = the vt cursor (EA's 0xE5620C: [ebp-0x50] = [rec+0x20] = cursor); no zero fill, the ctor writes it
    else if (!guard) {
        if (inst && !vtm) {  // EA's remoting check reads this->vtable->klass (null: EA's path faults or throws)
            e.op(0x8B, ECX, ESI, thisOff); e.rr(0x85, ECX, ECX); why[nb] = 0, bail[nb++] = e.jcc(0x84);
            if (op == 0xED) { e.op(0x8B, ECX, ECX, 0); e.rr(0x85, ECX, ECX); why[nb] = 1, bail[nb++] = e.jcc(0x84); }  // this = *ptr
            e.op(0x8B, EAX, ECX, 0); e.rr(0x85, EAX, EAX); why[nb] = 2, bail[nb++] = e.jcc(0x84);
            e.op(0x8B, EAX, EAX, 0); e.b(0x3B); e.b(0x05); e.d(0x01220EC0); why[nb] = 3, bail[nb++] = e.jcc(0x84);
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
    // A3 (docs/537 §20): no slot fill here; once B_LVL was right, this bails 1559 of 15.8 M host lazy calls (a fill made it 21, census unchanged)
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
    if (g_jitCountOn) { e.incAbs(&g_jitLazyCalls); if (lazy) e.incAbs(&g_jitLazyChain); if (guard) e.incAbs(&g_jitLazyGuard); }
    if (lazy) { e.b(0xFF); e.b(0xB4); e.b(0x24); e.d(8 + B_LVL); e.b(0xFF); e.b(0x04); e.b(0x24); }  // B_LVL = own level + 1 (8: B_VT is pushed already)
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
        g_jitNewVtSites++;
    }
    u8* skip = e.jmp();
    if (g_jitCountOn && g_jitLazyStatN < 4096) {  // A2 census: one counter per bail reason, then the common exit
        JitLazyStat& st = g_jitLazyStat[g_jitLazyStatN++];
        st.callerRm = callerRm, st.ip = ip, st.op = (unsigned short)op, st.lazy = lazy, st.guard = guard;
        u8* toExit[16];
        for (int i = 0; i < nb; i++) { Fix(bail[i], e.p); e.incAbs(&st.r[why[i]]); toExit[i] = e.jmp(); }
        for (int i = 0; i < nb; i++) Fix(toExit[i], e.p);
    } else
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
static void* __cdecl JitTransformHook(u8* rm, void* ctx);
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
        if (w == 0xF4 && g_jitNewVt && g_jitLazy) {  // B2b: a value-type ctor's site bakes in its frame sizes too
            u8* c = (u8*)(uintptr_t)items[code[i + 1]];
            if (JitNewVtClass(c) && !(*(const DWORD*)(c + 0x44) & 0x100)) JitTransformHook(c, ctx);
        }
        i += len;
    }
}

// the word a compiled position gets: JIT_OP, except leave.s. EA's leave / leave.s handler 0xE59315 re-reads [edi] to pick
// the operand size (0xE5933A cmp word ptr [edi], 0x9C): with JIT_OP there it reads leave.s's 16-bit offset plus the next
// word as a 32-bit offset and jumps to a wild ip. leave.s always exits to that handler, so its own word can stay.
#include "monohost-jitgate.cpp"  // MONOHOST_JITCOVER, MONOHOST_JITBENCH (docs/537 §17)
static unsigned short JitWord(unsigned short orig) { return orig == 0x9C ? orig : (unsigned short)JIT_OP; }
// compile one method: returns false (and leaves the code alone) if anything does not fit
static void JitCompiled(u8* rm) {
    if (!g_jitTab || g_jitMethN >= 4096) return;
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
        if (len <= 0) { Log("jit: cannot walk %s at word %d (op %X)\n", "method", i, op); return; }
        if (op >= 0x1AB) {
            const u8* d = (const u8*)(uintptr_t)(SUPERDESC + 0x48 * (op - 0x1AA));
            int m = *(const unsigned short*)(d + 4), at = i;
            for (int k = 0; k < m; k++) {
                unsigned short b = ((const unsigned short*)(d + 6))[k];
                if (k && code[at] != b) { Log("jit: superop %X inner word %d differs\n", op, k); return; }
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
    static bool posCold[4096];
    if (lazyOk) JitMarkCold(n, posOp, posCold);
    for (int k = 0; k < n && lazyOk; k++) {
        const JitOpInfo t = posOp[k] < 0x1C1 ? g_opInfo[posOp[k]] : JitOpInfo{K_EXIT, 0, 0};
        const u8* lc; bool lg;
        if (t.kind == K_EXIT && ((posOp[k] >= 0xEA && posOp[k] <= 0xED) || posOp[k] == 0xF4) && posReal[k] && JitLazySite(code + posW[k], posOp[k], rm, &lc, &lg)) continue;
        // CallExit: a hot exit (and ret.vt) writes the frame first and then is EA's, as a cold one: the body stays lazy
        if ((t.kind == K_EXIT && !posCold[k] && !g_jitCallExit) || (t.kind == K_RET && posOp[k] != 0x06 && posOp[k] != 0x07 && !g_jitCallExit)) lazyOk = false;
    }
    if (g_jitCodeEnd - (g_jitCode + g_jitCodeUsed) < (lazyOk ? 512 : 256) * n + 512) { Log("jit: code pool full\n"); return; }
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
            if (g_jcOn) { e.b(0xE8); e.d((DWORD)(uintptr_t)JcLazyStub - ((DWORD)(uintptr_t)e.p + 4)); }  // capture: sample the lazy call, every register kept
        }
        for (int k = 0; k < n; k++) {
            label[k] = e.p;
            if (g_jitCover || g_jitBench) JitGateEmit(e, k, posOp[k]);
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
                else { e.b(0x68); e.d(sz); e.b(0x6A); e.b(0); e.b(0x51); e.b(0xE8); e.d(0x00F7E8B8 - ((DWORD)(uintptr_t)e.p + 4)); e.b(0x83); e.b(0xC4); e.b(12); }
                e.addEsi(-8); break;
            }
            case K_FARITH: e.fld8(ESI, -16); e.op(0xDC, t.x, ESI, -8); e.fstp8(ESI, -16); e.addEsi(-8); break;  // docs/221: x87, EA's precision and rounding
            case K_FNEG: e.fld8(ESI, -8); e.b(0xD9); e.b(0xE0); e.fstp8(ESI, -8); break;
            case K_FILD: e.op(t.x ? 0xDF : 0xDB, t.x ? 5 : 0, ESI, -8); e.fstp8(ESI, -8); break;  // conv.r4 and conv.r8 of an int: no narrowing, as EA
            case K_FR4R8: e.fld8(ESI, -8); e.fstp4(EBP, -0x1C); e.fld4(EBP, -0x1C); e.fstp8(ESI, -8); break;
            case K_CVT:  // i1, i2, i4 only with EA's own SSE2 flag set: else EA calls _ftol2_sse
                if (t.x < 3 && !*g_jitSse2) { EmitExit(e, ip, op, false); ex++; continue; }
                e.b(0xF2); e.op2(0x2C, EAX, ESI, -8);
                if (t.x) { e.b(0x0F); e.b(t.x == 1 ? 0xBE : t.x == 2 ? 0xBF : t.x == 3 ? 0xB6 : 0xB7); e.b(0xC0); }
                e.op(0x89, EAX, ESI, -8); break;
            case K_FISTT:  // fisttp for EA's control-word chop and _ftol2 (docs/221 §5: 600 M inputs on a real CPU)
                if (!g_jitSse3) { EmitExit(e, ip, op, false); ex++; continue; }
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
                if (!EmitIsinst(e, (const u8*)(uintptr_t)items[u16], exitRel)) { EmitExit(e, ip, op, false); ex++; continue; }
                break;
            case K_ISINST:
                if (!EmitIsinst(e, (const u8*)(uintptr_t)items[u16])) { EmitExit(e, ip, op, false); ex++; continue; }
                break;
            case K_SHRUN8:  // shr.un.i8 (0xE55DE6): edx:eax >> cl through the CRT's _aullshr 0xF7EEE0, as EA's handler calls it
                e.op(0x8B, EAX, ESI, -16); e.op(0x8B, EDX, ESI, -12); e.op(0x8B, ECX, ESI, -8);
                e.b(0xE8); e.d(0x00F7EEE0 - ((DWORD)(uintptr_t)e.p + 4));
                e.op(0x89, EAX, ESI, -16); e.op(0x89, EDX, ESI, -12); e.addEsi(-8); break;
            case K_LDCFP:  // ldc.r4 (0xE546EC) widens through x87, ldc.r8 (0xE54708) copies the two words
                if (w == 7) { e.b(0xD9); e.b(0x05); e.d((DWORD)(uintptr_t)(ip + 1)); e.fstp8(ESI, 0); }
                else { e.b(0xC7); e.m(0, ESI, 0); e.d(*(const DWORD*)(ip + 1)); e.b(0xC7); e.m(0, ESI, 4); e.d(*(const DWORD*)(ip + 3)); }
                e.addEsi(8); break;
            case K_LDFP: if (w == 7) e.fld4(t.x ? EDI : EBX, u16); else e.fld8(t.x ? EDI : EBX, u16); e.fstp8(ESI, 0); e.addEsi(8); break;
            case K_STFP: e.fld8(ESI, -8); EmitFpStore(e, w, EDI, u16); e.addEsi(-8); break;
            case K_STINARGFP: if (g_jitLazyPass) e.opEsp(0x8B, ECX, 4 + B_ARGS); else { e.op(0x8B, ECX, EBP, -8); e.op(0x8B, ECX, ECX, 0x14); } e.fld8(ECX, u16 * 8); EmitFpStore(e, w, EBX, (int)argOff[u16]); break;
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
            // docs/537 §20 B1 [verified handlers]: null and bounds exit before any change (EA raises in its handler); EA has no barrier,
            // ours logs heap stores for Satb. The size is words 2-3 (stfld.vt, the element ops) or the class's value size (ldobj,
            // stobj.vt: EA calls 0xE6F5A0 each time, here read once at compile time; a class with no layout yet exits for good).
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
                if (g_jitLazyPass && (op == 0x06 || op == 0x07)) { EmitLazyRet(e, ip, op, rm, op == 0x06); nat++; continue; }
                if (op == 0xEA && g_jitC5 && !g_jitLazyPass) {
                    if (const DWORD fn = JitIcallFn(ip, rm)) { exitRel[0] = EmitIcall(e, fn); break; }
                }
                if ((op == 0xEA || op == 0xEC) && (g_jitPureIcalls || g_jitFastCopy) && posReal[k])
                    if (const int pk = JitPureIcall(ip, op, rm))
                        if (pk == 4 ? g_jitFastCopy : g_jitPureIcalls) { EmitPureIcall(e, pk, exitRel); break; }
                if ((op == 0xEA || op == 0xEC || (op == 0xEB && g_jitInline >= 2)) && g_jitInline && posReal[k] && JitTryInline(e, ip, op, rm)) { nat++; continue; }
                if ((op == 0xEA || op == 0xEB || op == 0xEC || op == 0xED || (op == 0xF4 && g_jitNewVt)) && g_jitLazy && posReal[k] && EmitLazyCall(e, ip, op, rm)) { nat++; continue; }
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
            if (tp < 0) { Log("jit: branch target %d not an instruction\n", tw); g_jitLazyPass = false; return; }
            if (fix[f].abs) memcpy(fix[f].rel, &label[tp], 4); else Fix(fix[f].rel, label[tp]);
        }
    }
    g_jitLazyPass = false;
    static u8* benchStub[4096];
    JitGateStubs(e, n, posW, posOp, code, benchStub);
    // 3. publish: table entries for every position, then patch the opcode words (positions of EA instruction starts and
    // superop inner positions alike; inner words are never read by the superop handlers)
    if (g_jitPosN + n > 400000 || g_jitEntN + n > (int)(JIT_TAB_N * 3 / 4)) { Log("jit: tables full\n"); return; }
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
    JitGatePublish(n, posW, posOp, wordToPos, code, label, benchStub, words);
    g_jitMethN++;
    g_jitCodeUsed += M.bytes + 1;
    g_jitCodeUsed = (g_jitCodeUsed + 15) & ~(size_t)15;
    if (g_jitPatched) for (int k = M.pos0; k < M.posN; k++) code[g_jitPos[k]] = JitWord(g_jitOrigW[k]);
    if (lazyEntry) {
        bool leaf = true;  // the jit-hot.ts --coverage row: leaf = DirectLeaf=1 can enter it (no call but in a cold run)
        for (int k = 0; k < n; k++) leaf &= !(posOp[k] >= 0xEA && posOp[k] <= 0xED) || posCold[k];
        char lzName[256];
        Log("jitlazy %s %s\n", leaf ? "leaf" : "chain", JitMethodName(rm, lzName, sizeof(lzName)));
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
            g_jitMeth[m].code[g_jitPos[k]] = on ? JitWord(g_jitOrigW[k]) : g_jitOrigW[k];
    g_jitPatched = on;
}

// ---- JitEnter: the jump-table entry of JIT_OP (eax = edx = JIT_OP, edi = ip) ----
static int g_jitVerifyOn;
static void JcEnterStub();
static __declspec(naked) void JitEnter() {
    __asm {
        mov eax, edi
        shr eax, 1
        and eax, 0xFFFF            // JIT_TAB_N - 1
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
        cmp g_jcOn, 0
        jne cap
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
        mov ebx, [ebp - 8]
        mov ebx, [ebx + 0x10]      // arena base (arguments)
        mov edi, [ebp - 0x14]      // locals base
        jmp dword ptr [eax + 4]
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
    int hx[2 * JIT_SNAP_OBJS];  // region index of a slot
    void Clear() { n = 0, used = 0, full = false, gen++; }
    bool Contains(DWORD k) const {
        for (DWORD i = (k >> 3) & (2 * JIT_SNAP_OBJS - 1); hg[i] == gen; i = (i + 1) & (2 * JIT_SNAP_OBJS - 1))
            if (hs[i] == k) return true;
        return false;
    }
    bool Insert(const u8* p) {  // false if already present
        DWORD k = (DWORD)(uintptr_t)p, i = (k >> 3) & (2 * JIT_SNAP_OBJS - 1);
        while (hg[i] == gen) { if (hs[i] == k) return false; i = (i + 1) & (2 * JIT_SNAP_OBJS - 1); }
        hs[i] = k, hg[i] = gen, hx[i] = n;
        return true;
    }
    int RegionOf(DWORD k) const {
        for (DWORD i = (k >> 3) & (2 * JIT_SNAP_OBJS - 1); hg[i] == gen; i = (i + 1) & (2 * JIT_SNAP_OBJS - 1))
            if (hs[i] == k) return hx[i];
        return -1;
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
struct JitV { DWORD gc0; bool active; int phase; u8* rm; JitMeth* meth; DWORD sp0, ebp, fr[6]; JitSnap *s0, *s1, *s2; u8 r1[64], r2[64]; int rn, calleeRegion; bool retried; struct JitNewSet* nw; };
static const DWORD JIT_VD = 32;  // verified depths (snapshot buffers per depth)
static JitV* g_jitV;           // [JIT_VD], main thread only (worker threads run compiled code unverified)
static DWORD g_jitMainTid;
static volatile LONG g_jitOtherTid;  // entries from another thread (the M5 worker): never compiled, EA's handler runs
static volatile LONG g_jvRetried, g_jvRetryOk, g_jvInner, g_jvNested, g_jvCalls, g_jvOk, g_jvMismatch, g_jvAbandoned, g_jvAllocEq, g_jvUnverified, g_jvLarge;
static int g_jvLogged;
static bool g_jvQuiet;  // first attempt of a verified call: mismatches are logged only if the retry repeats them
static char g_jvWhat[420];
static const JitSnap* g_jvS0;  // a mismatch's log: the entry snapshot
static DWORD g_jvGc0;
static volatile LONG g_jvFreeCells;
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
// a region that holds the frame's arena is the GC block of the arena chunk (a byref argument into it): region 0 compares the
// frame, and past it lies callee memory that a direct internal call leaves alone and EA's frame push writes
static DWORD JitLiveBytes(const u8* at, DWORD n, const u8* arena) { return at <= arena && arena < at + n ? (DWORD)(arena - at) : n; }
static const u8* g_jvInv;  // the MonoInvocation being snapshotted (byref targets must lie in one of its frames' arenas)
// finish the last collection before a snapshot, as EA's per-frame step does (0xE49F60: pending [0x11F2F94], [0x11947E4] != -1 = the
// finish 0xE49A50 is still owed, GC_reclaim_all 0xE50380), so no dead cell of a swept-later block can enter it. Such a cell still holds
// its vtable, JitFreeCell cannot see it, and a stale word reaches it. The pass's first allocation then sweeps it: JitKeepNew takes
// what lands there for an old object, and the restore writes the vtable over a free-list link (docs/537 §15). The M5 worker allocates
// too: under its GC lock.
static void JitSweep() {
    if (!*(const DWORD*)(uintptr_t)0x11F2F94 || *(const DWORD*)(uintptr_t)0x11F30A8) return;
    if (mt::g_mtLockInit) EnterCriticalSection(&mt::g_mtLock);
    if (*(const DWORD*)(uintptr_t)0x11F2F94 && !((int(__cdecl*)())(uintptr_t)0xE4C060)()) {  // not while a new mark runs
        if (*(const LONG*)(uintptr_t)0x11947E4 != -1) *(LONG*)(uintptr_t)0x11947E4 = -1, ((void(__cdecl*)())(uintptr_t)0xE49A50)();
        ((int(__cdecl*)(void*, int))(uintptr_t)0xE50380)(NULL, 0);
        *(DWORD*)(uintptr_t)0x11F2F94 = 0;
    }
    if (mt::g_mtLockInit) LeaveCriticalSection(&mt::g_mtLock);
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
        if (b == (u8*)(uintptr_t)w && JitFreeCell(b)) { g_jvFreeCells++; return -1; }
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
            const int rg = g_jvS0 ? g_jvS0->RegionOf(a) : -1;
            size_t L = strlen(g_jvWhat);
            sprintf_s(g_jvWhat + L, sizeof(g_jvWhat) - L, "; diag region-of-compiled %d word0 %08lX now %08lX gcbase %d; gc %lu->%lu; kept n %d used %lu over %d", rg, rg >= 0 ? *(const DWORD*)(g_jvS0->buf + g_jvS0->r[rg].off) : 0,
                      rg >= 0 ? *(const DWORD*)(uintptr_t)a : 0, GcBase((void*)(uintptr_t)a) == (u8*)(uintptr_t)a, g_jvGc0, *(const DWORD*)(uintptr_t)0x11F2F90, g_jn ? g_jn->n : -1, g_jn ? g_jn->used : 0, g_jn ? g_jn->over : 0);
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
    if (!g_jvQuiet && g_jvLogged++ < 40) Log("jit verify MISMATCH %s: %s at +%lu (%s)\n", v.meth ? v.meth->name : "?", what, at, g_jvWhat);
}
static u8* JitNative(JitRegs* r, const JitEnt* e) {
    r->ebx = *(const DWORD*)(uintptr_t)(*(const DWORD*)(uintptr_t)(r->ebp - 8) + 0x10);
    r->edi = *(const DWORD*)(uintptr_t)(r->ebp - 0x14);
    return e->native;
}
static void* JitOrig(JitRegs* r, DWORD ip, DWORD op) {
    r->edi = ip, r->eax = r->edx = op;
    *(DWORD*)(uintptr_t)(r->ebp - 4) = ip;
    return (void*)(uintptr_t)((const DWORD*)(uintptr_t)MINT_TABLE)[op];  // the live entry, as EmitExit
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
        g_jitLazyMatFrames++;
    }
    r->ecx = L * (4 + B_SIZE);
    g_jitLazyMats++;
    if (g_jitCountOn) {  // A2 census: which lazy body's exit wrote the frames (blk[0] is the innermost callee)
        const u8* rm = (const u8*)(uintptr_t)blk[0][B_RM / 4];
        int i = 0;
        while (i < g_jitMatStatN && (g_jitMatStat[i].rm != rm || g_jitMatStat[i].ip != r->edi)) i++;
        if (i == g_jitMatStatN && i < 2048) g_jitMatStat[g_jitMatStatN++] = {rm, r->edi, (unsigned short)r->eax, 0};
        if (i < 2048) g_jitMatStat[i].n++;
    }
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
        for (DWORD d = depth; d < JIT_VD; d++) if (g_jitV[d].active && g_jitV[d].ebp == r->ebp) g_jitV[d].active = false, g_jvAbandoned++;
        JitMeth* m = JitMethOf(rm);
        g_jvCalls++;
        if (outer) { g_jvInner++; if (m) m->inner++; return outer == 2 ? JitOrig(r, r->edi, e->orig) : JitNative(r, e); }
        if (!m || !m->verifyOk) { g_jvUnverified++; if (m) m->skipped++; return JitNative(r, e); }
        v.active = true, v.phase = 1, v.rm = (u8*)rm, v.meth = m, v.sp0 = r->esi, v.ebp = r->ebp;
        for (int i = 0; i < 6; i++) v.fr[i] = *(const DWORD*)(uintptr_t)(r->ebp - 0x8 - 4 * i);
        JitSweep();
        v.s0->Clear(), v.calleeRegion = -1, v.retried = false, v.gc0 = *(const DWORD*)(uintptr_t)0x11F2F90;
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
            g_jn = v.nw, g_jvS0 = v.s0, g_jvGc0 = v.gc0;
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

#define JC_LOG_NL "\n"
#define JC_METH(e) JitMethOf((e)->rm)
#define JC_RM(e) ((e)->rm)
#define JC_METH_RM(rm) JitMethOf(rm)
#define JC_OPNAME(op) (((const char* const*)(uintptr_t)0x11949E0)[op])
#define JC_ON_SIM (GetCurrentThreadId() == g_jitMainTid)
#define JC_NEXT(r, e) (g_jitVerifyOn ? JitVerifySlow(r, e, 0) : JitNative(r, e))
#include "../ts3native/monojit-capture.h"  // docs/537 §17: the game's capture, run here on the corpus before it runs in the game

// verify only: GC_malloc_atomic 0xE4BDC0 -> GC_malloc 0xE4BE30 (docs/57 §1), so pointer-free objects come zeroed and the
// slack after a boxed value or array is the same in both passes (atomic blocks keep old bytes). Off while M5 installs its
// P3 detours (they check the original bytes) and runs.
static u8 g_atomicOrig[5];
static bool JitAtomic(bool on) {
    if (g_jitMode != 3) return true;
    return on ? Jmp(0xE4BDC0, (const void*)(uintptr_t)0xE4BE30) : mt::Write((void*)(uintptr_t)0xE4BDC0, g_atomicOrig, 5);
}

static const int JIT_CASES = 31;
static const char* const kJitCase[JIT_CASES] = {
    "Dictionary<int,Shape>.TryGetValue", "Dictionary<ulong,int>.TryGetValue", "Dictionary<ObjectGuid,int>.TryGetValue",
    "List foreach (per element)", "List get_Item + Count", "List.Add (+GrowIfNeeded)", "boxed Enum.Equals + GetHashCode",
    "GetType() == typeof", "Array.Resize<int>", "Array.IndexOf<int> (64)", "ObjectGuid !=", "CASUtils flags",
    "Dictionary foreach (per element)", "ContainsKey + set_Item", "JitId (compiled, trivial)", "JitPlain (interpreted twin)",
    "JitOut (compiled -> interpreted)", "JitOutPlain (interpreted twin)", "JitSum 64 (compiled loop, per 64)",
    "JitSumPlain 64 (interpreted twin)", "empty loop", "JitInl 64 (compiled, tiny callees)", "JitInlPlain 64 (interpreted twin)",
    "JitLz 64 (compiled, direct calls to leaves)", "JitLzPlain 64 (interpreted twin)",
    "JitLzChain 64 (compiled, lazy chain)", "JitLzChainPlain 64 (interpreted twin)", "JitLzVirt 64 (compiled, class-guarded callvirt)",
    "JitLzVirtPlain 64 (interpreted twin)", "JitLzAlloc 64 (compiled, callee exits at newobj)", "JitLzAllocPlain 64 (interpreted twin)"};
static int JitCase(int c, int n) {
    void* a[2] = {&c, &n};
    return I32(T("JitBench", 2, a));
}
static double JitTime(int c, int n, int* sum) {
    LARGE_INTEGER fq, t0, t1;
    QueryPerformanceFrequency(&fq);
    QueryPerformanceCounter(&t0);
    *sum = JitCase(c, n);
    QueryPerformanceCounter(&t1);
    return 1e9 * (t1.QuadPart - t0.QuadPart) / fq.QuadPart / n;  // ns per iteration
}
static void JitStats() {
    int nat = 0, ex = 0, bytes = 0;
    for (int m = 0; m < g_jitMethN; m++) nat += g_jitMeth[m].nativeOps, ex += g_jitMeth[m].exitOps, bytes += g_jitMeth[m].bytes;
    Log("jit: %d methods compiled (%d instantiations incl. generics), %d native ops, %d exits, code %d bytes (%.1f KB, pool use %.1f KB), "
        "table %d entries (%u KB), shadow %d words\n", g_jitMethN, g_jitMethN, nat, ex, bytes, bytes / 1024.0, g_jitCodeUsed / 1024.0,
        g_jitEntN, (unsigned)(sizeof(JitEnt) * (JIT_TAB_N + 4096) / 1024), g_jitPosN);
    Log("jit: satb barrier: %ld compiled heap stores logged in spans, %ld worker entries sent back to the interpreter\n", g_jitSatbStores, g_jitSatbBails);
    Log("jit verify: free cells left out %ld\n", g_jvFreeCells);
    if (g_jitInline) Log("jit: inlined call sites %ld, sites refused by the inliner %ld, fast paths run %ld (count mode)\n", g_jitInlined, g_jitInlineNo, g_jitInlineHits);
    if (g_jitLazy) {
        int gf = 0;
        for (int i = 0; i < g_jitGuardN; i++) gf += g_jitGuard[i].entry != NULL;
        Log("jit lazy: %d lazy bodies, %d lazy call sites (%d through a class guard, %d of them filled), %d callees seen; lazy calls %ld "
            "(count mode: %ld from a lazy body, %ld through a guard), exits that wrote frames %ld (%ld frames)\n",
            g_jitLazyBodies, g_jitLazySites, g_jitLazyGuardSites, gf, g_jitLazyCellN, g_jitLazyCalls, g_jitLazyChain, g_jitLazyGuard,
            g_jitLazyMats, g_jitLazyMatFrames);
    }
    if (g_jitCallExit) {
        int ub = 0;
        for (int i = 0; i < g_jitGuardN; i++) ub += g_jitGuard[i].entry && g_jitGuard[i].unbox;
        Log("jit callexit: pure icall sites %ld, guard sites filled with a boxed value-type receiver %d\n", g_jitPure, ub);
    }
    if (g_jitNewVt) Log("jit newvt: newobj value-type sites %d (normal and lazy bodies)\n", g_jitNewVtSites);
    if (g_jitFastCopy) Log("jit fastcopy: sites %ld, inline copies %ld, exits to EA's call %ld\n", g_jitFastCopySites, g_jitFastCopyRuns, g_jitFastCopyExits);
}
// B1 exit census (count mode): every exit site that ran, with its method and EA opcode, and calls per compiled method.
// tools/jit-exit-census.ts reads these lines ("jitcalls" / "jitexit") plus jit-dump.txt.
static void JitCountReport() {
    char nb[64];
    // every compiled method and every exit site, run or not: the static census (docs/207 §11) needs the zeros too
    for (int m = 0; m < g_jitMethN; m++)
        Log("jitcalls %d %ld %ld %d %s\n", m, g_jitCalls[m], g_jitPosRun[m], g_jitMeth[m].posN - g_jitMeth[m].pos0, g_jitMeth[m].name);
    for (int s = 0; s < g_jitSiteN; s++)
        if (g_jitSite[s].meth < g_jitMethN)
            Log("jitexit %d %X %s %s %ld\n", g_jitSite[s].meth, g_jitSite[s].op, MintName(g_jitSite[s].op, nb), g_jitSite[s].slow ? "slow" : "op",
                g_jitSite[s].n);
    // B3 call edges: callee (callvirt: the inline cache's method at the end of the run, else the declared one), the
    // callee's compiled index (-1 if not compiled), receiver-class misses, flags +0x44, native body +0x24, words, clauses
    for (int s = 0; s < g_jitSiteN; s++) {
        const JitSite& S = g_jitSite[s];
        if (S.meth >= g_jitMethN || S.slow || (S.op < 0xEA || S.op > 0xED)) continue;
        const DWORD* items = *(const DWORD* const*)(g_jitMeth[S.meth].rm + 0x14);
        const u8* c = (const u8*)(uintptr_t)(S.op == 0xEA ? items[S.ip[1]] : S.op == 0xEB ? items[S.ip[2]] : items[S.ip[1] + 1] ? items[S.ip[1] + 1] : items[S.op == 0xEC ? S.ip[3] : S.ip[4]]);
        if (!c) continue;
        int cm = -1;
        for (int m = 0; m < g_jitMethN && cm < 0; m++) if (g_jitMeth[m].rm == c) cm = m;
        char cn[128];
        Log("jitedge %d %d %X %ld %ld %d %X %X %d %d %s\n", S.meth, s, S.op, S.n, S.miss, cm, *(const DWORD*)(c + 0x44),
            *(const DWORD*)(c + 0x24), *(const unsigned short*)(c + 0x3A), c[0x41], JitMethodName(c, cn, sizeof(cn)));
    }
    // B2b census (count mode only): the class each newobj exit site builds. "jitnew <meth> <word> <exits> <vt> <class flags +0x14> <class> <ctor>",
    // vt = EA's value-type test 0xE52160 (byval type 0x11 and not an enum, or a generic instance of a value-type container)
    for (int s = 0; s < g_jitSiteN; s++) {
        const JitSite& S = g_jitSite[s];
        if (S.meth >= g_jitMethN || S.op != 0xF4) continue;
        const u8* rm = g_jitMeth[S.meth].rm;
        const DWORD* items = *(const DWORD* const*)(rm + 0x14);
        const u8* c = (const u8*)(uintptr_t)items[S.ip[1]];  // the ctor's call info, same shape as a call word's method (JitMethodName)
        const u8* mm = c ? *(const u8* const*)(c + 8) : NULL;
        const u8* k = mm ? *(const u8* const*)(mm + 8) : NULL;
        if (!k) continue;
        const bool vt = k[0xA6] == 0x11 ? !(k[0x14] & 0x10)
                      : k[0xA6] == 0x15 && (*(const DWORD*)(*(const u8* const*)(*(const u8* const*)(k + 0xA0) + 4) + 0x14) >> 3 & 1);
        char cn[128];
        Log("jitnew %d %d %ld %d %X %s\n", S.meth, (int)(S.ip - *(const unsigned short* const*)rm), S.n, vt, *(const DWORD*)(k + 0x14), JitMethodName(c, cn, sizeof(cn)));
    }
    // A2 census: "jitbail <caller> <word> <op> <lazy|normal> <guard|direct> <reason>=<n>..." and
    // "jitmat <n> <op> <word> <callee>" (lazy-body exits that wrote frames, by callee and exit position)
    for (int i = 0; i < g_jitLazyStatN; i++) {
        const JitLazyStat& S = g_jitLazyStat[i];
        char line[512], cn[128];
        int o = 0;
        for (int r = 0; r < 12; r++) if (S.r[r]) o += _snprintf_s(line + o, sizeof(line) - o, _TRUNCATE, " %s=%ld", kLazyBail[r], S.r[r]);
        if (o) Log("jitbail %s %d %X %s %s%s\n", JitMethodName(S.callerRm, cn, sizeof(cn)), (int)(S.ip - *(const unsigned short* const*)S.callerRm),
                   S.op, S.lazy ? "lazy" : "normal", S.guard ? "guard" : "direct", line);
    }
    for (int i = 0; i < g_jitMatStatN; i++) {
        const JitMatStat& S = g_jitMatStat[i];
        char cn[128];
        Log("jitmat %ld %s %d %s\n", S.n, MintName(S.op, nb), (int)((const unsigned short*)(uintptr_t)S.ip - *(const unsigned short* const*)S.rm),
            JitMethodName(S.rm, cn, sizeof(cn)));
    }
}

// B4 differential corpus (MONOHOST_JITCORPUS=1, modes on / verify): MonoHostTest's JitCorpusRun(c) returns one hash per
// case (results and caught exceptions). Each case runs twice on EA's interpreter (unpatched; the two must agree, else
// the case is "unstable" and skipped), then once compiled (in verify mode with the per-call check on). The case names
// are in tools/jit-corpus.ts, which also reads these lines.
// one case: its hash, or for a managed exception that left Run (old Mono raises runtime-thrown exceptions such as newarr's
// by unwinding to the invoke boundary, past interpreted catch blocks) a hash of its type with the top bit set; false on a
// CPU fault (host state suspect)
static bool g_corpusEscaped;  // the last JitCorpusCall's exception left Run
static bool JitCorpusCall(void* m, int c, int* h) {
    void* a[1] = {&c};
    void* exc = NULL; DWORD code = 0;
    void* r = SafeInvoke(m, NULL, a, &exc, &code);
    if (code) return false;
    g_corpusEscaped = exc != NULL;
    if (exc) {
        char t[128] = "", msg[256] = ""; void* in = NULL;
        Describe(exc, t, msg, &in);
        unsigned x = 0x5EED;
        for (const char* p = t; *p; p++) x = x * 31 + (u8)*p;
        *h = (int)(x | 0x80000000u);
        return true;
    }
    *h = I32(r);
    return true;
}
static void JitCorpusTrail(void* tm, int c, const char* arm) {
    void* a[1] = {&c};
    void* exc = NULL; DWORD code = 0;
    void* s = tm ? SafeInvoke(tm, NULL, a, &exc, &code) : NULL;
    char out[1200] = "";
    if (s && !exc && !code) Narrow(s, out, sizeof(out));
    else if (exc) {  // the exception escaped the case body (thrown before its try: the case's own transform, a fixture's type load)
        char t[128] = "", msg[256] = "", ti[128] = "", mi[256] = ""; void* in = NULL, *in2 = NULL;
        Describe(exc, t, msg, &in);
        if (in) Describe(in, ti, mi, &in2);
        _snprintf_s(out, sizeof(out), _TRUNCATE, "(escaped) %s: %s%s%s%s%s", t, msg, in ? " <- " : "", ti, in ? ": " : "", mi);
    }
    Log("jit corpus trail case %d %s: %s\n", c, arm, out[0] ? out : code ? "(trail run faulted)" : "(no exceptions)");
}
static void JitCorpusRun() {
    JitGateBeforeCorpus();
    int n = I32(T("JitCorpusCount", 0, NULL));
    void* m = Meth(g_testK, "JitCorpusRun", 1), *tm = Meth(g_testK, "JitCorpusTrail", 1);
    if (n <= 0 || n == INT_MIN || !m) { Log("jit corpus: no JitCorpusCount / JitCorpusRun in MonoHostTest.dll\n"); return; }
    const int vOn = g_jitVerifyOn;
    const LONG mm0 = g_jvMismatch, ab0 = g_jvAbandoned, ok0 = g_jvOk;
    int bad = 0, unstable = 0, c0 = 0, c1 = n - 1;
    char rv[16] = "";
    if (GetEnvironmentVariableA("MONOHOST_JITCORPUS_FROM", rv, sizeof(rv))) c0 = atoi(rv);
    if (GetEnvironmentVariableA("MONOHOST_JITCORPUS_TO", rv, sizeof(rv))) c1 = atoi(rv);
    const bool trailAll = GetEnvironmentVariableA("MONOHOST_JITCORPUS_TRAIL", rv, sizeof(rv)) && atoi(rv) > 0;  // every case's interpreted trail (round 10 C5)
    if (c0 > 0 || c1 < n - 1) Log("jit corpus: cases %d to %d of %d\n", c0, c1, n);
    // the internal calls the accessor fixtures (JitAccessors.cs) reach answer zero (no task, no parent, no world name) for the
    // corpus only: the stub's NotImplementedException escapes the case body and ended six accessor cases early (round 10 C5)
    static const char* const kCorpusZero[] = {"::Simulator_GetTaskImpl", "::Slots_GetParentIdImpl", "::GameUtils_GetWorldName"};
    char allow0[sizeof(g_icAllow) / sizeof(g_icAllow[0])];
    memcpy(allow0, g_icAllow, sizeof(allow0));
    for (int i = 0; i < g_icCount; i++) for (const char* z : kCorpusZero) if (!g_icAllow[i] && strstr(g_icNames[i], z)) g_icAllow[i] = 1;
    for (int c = c0; c <= c1 && c < n; c++) {
        if (g_jcOn) JcTick();
        int ref = 0, ref2 = 0, got = 0;
        g_jitVerifyOn = 0, JitSetPatched(false);
        const bool ok = JitCorpusCall(m, c, &ref) && JitCorpusCall(m, c, &ref2);
        JitSetPatched(true), g_jitVerifyOn = g_jitMode == 3 ? 1 : 0;
        if (!ok || !JitCorpusCall(m, c, &got)) {  // a CPU fault: stop, the interpreter state is suspect
            Log("jit corpus FAULT case %d: %s arm faulted; corpus stopped\n", c, ok ? "compiled" : "interpreted");
            bad++;
            break;
        }
        if (ref != ref2) unstable++, Log("jit corpus UNSTABLE case %d: interpreted %08X then %08X\n", c, ref, ref2);
        else if (got != ref) bad++, Log("jit corpus MISMATCH case %d: compiled %08X interpreted %08X\n", c, got, ref);
        else if (!trailAll) continue;
        else { g_jitVerifyOn = 0, JitSetPatched(false); JitCorpusTrail(tm, c, "interpreted"); JitSetPatched(true), g_jitVerifyOn = vOn; continue; }
        g_jitVerifyOn = 0, JitSetPatched(false);
        JitCorpusTrail(tm, c, "interpreted");
        JitSetPatched(true);
        JitCorpusTrail(tm, c, "compiled");
    }
    JitSetPatched(true);
    g_jitVerifyOn = vOn;
    JitGateAfterCorpus(m, c0, c1);
    memcpy(g_icAllow, allow0, sizeof(allow0));
    Log("jit corpus: %d cases, %d differ, %d unstable; per-call verify during the corpus: ok %ld, MISMATCH %ld, abandoned (exception) %ld\n",
        n, bad, unstable, g_jvOk - ok0, g_jvMismatch - mm0, g_jvAbandoned - ab0);
}
// round 10 C4 (docs/207 §14): every corpus case on the M5 worker thread with the compiled words patched, from M5 while the
// worker is idle (the corpus proper runs after the worker stopped). Each entry of a compiled method there takes JitEnter's
// other-thread path (EA's handler for the original opcode), so the worker's hash must be the interpreted one. A case is run
// compiled on this thread first (that compiles it here, not on the worker), then interpreted for the reference. A case whose
// compiled hash already differs, or whose exception leaves Run (the worker entry drops it), is skipped. MONOHOST_JITCORPUS_WORKER=0 skips.
static void JitCorpusWorkerPass() {
    char rv[16] = "";
    if (g_jitMode < 2 || !(GetEnvironmentVariableA("MONOHOST_JITCORPUS", rv, sizeof(rv)) && rv[0] == '1')) return;
    if (GetEnvironmentVariableA("MONOHOST_JITCORPUS_WORKER", rv, sizeof(rv)) && rv[0] == '0') return;
    int n = I32(T("JitCorpusCount", 0, NULL));
    void* m = Meth(g_testK, "JitCorpusRun", 1);
    if (n <= 0 || n == INT_MIN || !m) return;
    if (mt::ts3native_mtprobe.workerState != 2) { Log("jit corpus worker: no idle worker (state %ld), skipped\n", mt::ts3native_mtprobe.workerState); return; }
    const int vOn = g_jitVerifyOn;
    g_jitVerifyOn = 0;
    const LONG ot0 = g_jitOtherTid;
    int wn = 0, wbad = 0, wskip = 0, wesc = 0;
    for (int c = 0; c < n; c++) {
        int ref = 0, got = 0;
        JitSetPatched(true);
        const bool okC = JitCorpusCall(m, c, &got);
        JitSetPatched(false);
        const bool okI = okC && JitCorpusCall(m, c, &ref);
        JitSetPatched(true);
        if (!okI) { Log("jit corpus worker FAULT case %d on this thread; pass stopped\n", c); wbad++; break; }
        if (g_corpusEscaped) { wesc++; continue; }
        if (got != ref) { wskip++; continue; }
        int passes = 0; DWORD ms = 0; int cc = c; void* a[1] = {&cc};
        const int j = WorkerJob("JitCorpusWorker", a, MainIdle, &passes, &ms);
        const int w = I32(T("GetLastWorker", 0, NULL));
        wn++;
        if (j != 0 || w != ref) { wbad++; Log("jit corpus worker MISMATCH case %d: worker %08X (job %d) interpreted %08X\n", c, w, j, ref); LogWorkerError("JitCorpusWorker"); }
    }
    g_jitVerifyOn = vOn;
    Log("jit corpus worker: %d cases, %d differ; skipped: %d escaping, %d compiled differs; other-thread entries %ld\n", wn, wbad, wesc, wskip, g_jitOtherTid - ot0);
}

// B1: compile methods that cannot run offline (Sims3Gameplay accessors) so jit-dump.txt shows their mint code and P0's
// native / exit split. MONOHOST_JITPREP=<file>, one "assembly|namespace|class|method|params" line each (the method must
// also be in MONOHOST_JITLIST). Each is invoked once with this = null and zeroed arguments: the runtime-invoke entry
// transforms first (docs/130 §2), which compiles it; the run itself then mostly stops at the first field read (NRE).
static void JitPrepRun() {
    char path[MAX_PATH] = "";
    if (!GetEnvironmentVariableA("MONOHOST_JITPREP", path, sizeof(path))) return;
    FILE* f = NULL;
    if (fopen_s(&f, path, "r") || !f) { Log("jitprep: cannot open %s\n", path); return; }
    static u8 zero[8][16];
    void* args[8];
    for (int i = 0; i < 8; i++) args[i] = zero[i];
    char line[512];
    int n = 0, compiled = 0;
    while (fgets(line, sizeof(line), f)) {
        char* p[5] = {line};
        int k = 1;
        for (char* c = line; *c && k < 5; c++) if (*c == '|') *c = 0, p[k++] = c + 1;
        if (k < 5) continue;
        p[4][strcspn(p[4], "\r\n")] = 0;
        void* m = Meth(Cls(LoadImageByName(p[0]), p[1], p[2]), p[3], atoi(p[4]));
        n++;
        if (!m) { Log("jitprep %s.%s.%s/%s: not found\n", p[1], p[2], p[3], p[4]); continue; }
        memset(zero, 0, sizeof(zero));
        const int before = g_jitMethN;
        void* exc = NULL; DWORD code = 0;
        SafeInvoke(m, NULL, atoi(p[4]) ? args : NULL, &exc, &code);
        char t[128] = "", msg[256] = ""; void* in = NULL;
        if (exc) Describe(exc, t, msg, &in);
        compiled += g_jitMethN > before;
        Log("jitprep %s.%s.%s/%s: compiled %s, %s %s\n", p[1], p[2], p[3], p[4], g_jitMethN > before ? "yes" : "no",
            exc ? "exception" : code ? "fault" : "returned", exc ? t : "");
    }
    fclose(f);
    Log("jitprep: %d methods, %d compiled\n", n, compiled);
}

#include "monohost-jitreplay.cpp"  // docs/537 §17: MONOHOST_JITREPLAY

// MONOHOST_JITCAPTURE or MONOHOST_JITREPLAY: only the managed test assembly, then the corpus under capture and / or the replay, no M1-M5
static bool JitQuickRun() {
    char x[8] = "";
    const bool cap = g_jc != NULL, rep = GetEnvironmentVariableA("MONOHOST_JITREPLAY", NULL, 0) != 0;
    if (!cap && !rep) return false;
    int st = -1;
    void* img = LoadImageByName("MonoHostTest.dll");
    if (img) ((AsmLoadFromFull)0xE68290)(img, "MonoHostTest.dll", &st, 0);
    g_testK = Cls(img, "MonoHostTest", "HostTest");
    g_callK = Cls(img, "MonoHostTest", "CallTest");
    if (cap && GetEnvironmentVariableA("MONOHOST_JITCORPUS", x, sizeof(x)) && x[0] == '1') JitCorpusRun();
    if (cap) JcFlush(), JcLog();
    JitReplayRun();
    return true;
}

static void JitBenchRun() {
    JitPrepRun();
    char cv[8] = "";
    if (g_jitMode >= 2 && GetEnvironmentVariableA("MONOHOST_JITCORPUS", cv, sizeof(cv)) && cv[0] == '1') JitCorpusRun();
    if (g_jitMode < 2) {
        if (g_jitMode) for (int c = 0; c < JIT_CASES; c++) Log("jitbench warm case %d (%s): checksum %d\n", c, kJitCase[c], JitCase(c, 4096));
        return;
    }
    if (g_jitMode == 3) g_jitVerifyOn = 1, JitAtomic(true);  // MONOHOST_JITVERIFY=bench starts verifying here
    for (int c = 0; c < JIT_CASES; c++) Log("jitbench warm case %d (%s): checksum %d\n", c, kJitCase[c], JitCase(c, g_jitMode == 3 ? 1024 : 4096));
    if (g_jitMode == 3) {  // verify also the docs/139 MintBench workload, then every checksum against EA's untouched code
        int n = 300;
        void* a[1] = {&n};
        int mb = I32(T("MintBench", 1, a));
        g_jitVerifyOn = 0;
        JitSetPatched(false);
        int bad = 0;
        for (int c = 0; c < JIT_CASES; c++) {
            int ref = JitCase(c, 1024);
            g_jitVerifyOn = 1, JitSetPatched(true);
            int got = JitCase(c, 1024);  // again with verify on: the state after the first run must not have drifted
            g_jitVerifyOn = 0, JitSetPatched(false);
            if (got != ref) bad++, Log("jit verify CHECKSUM case %d: compiled+verify %d, interpreter %d\n", c, got, ref);
        }
        int mbRef = I32(T("MintBench", 1, a));
        JitSetPatched(true), g_jitVerifyOn = 1;
        Log("jit verify checksums: %d of %d cases differ from the interpreter; MintBench(300) %d vs interpreter %d\n", bad, JIT_CASES, mb, mbRef);
    }
    JitStats();
    if (g_jitMode == 3) {
        Log("jit verify: calls %ld, verified ok %ld, MISMATCH %ld, unverified (clauses / branch to 0) %ld, abandoned (exception) %ld, "
            "alloc-equal words %ld, nested activations unverified %ld, graph too large (compiled only) %ld, calls inside a verified "
            "caller %ld; retried after a first-attempt difference %ld (then equal %ld)\n", g_jvCalls, g_jvOk, g_jvMismatch, g_jvUnverified,
            g_jvAbandoned, g_jvAllocEq, g_jvNested, g_jvLarge, g_jvInner, g_jvRetried, g_jvRetryOk);
        for (int m = 0; m < g_jitMethN; m++)
            if (g_jitMeth[m].verified || g_jitMeth[m].mismatches || g_jitMeth[m].skipped || g_jitMeth[m].inner)
                Log("jit verify   %-70s ok %ld mismatch %ld unverified %ld, inside a verified caller %ld\n", g_jitMeth[m].name,
                    g_jitMeth[m].verified, g_jitMeth[m].mismatches, g_jitMeth[m].skipped, g_jitMeth[m].inner);
        return;
    }
    if (g_jitVerifyOn == 2) {  // count mode: re-entries and exits per iteration of each case
        for (int c = 0; c < JIT_CASES; c++) {
            g_jitEnterN = g_jitExitN = 0;
            JitCase(c, 20000);
            Log("jitcount case %2d %-40s enters %.2f exits %.2f per iteration\n", c, kJitCase[c], g_jitEnterN / 20000.0, g_jitExitN / 20000.0);
        }
        JitCountReport();
        return;
    }
    SetThreadAffinityMask(GetCurrentThread(), 1 << 2);
    SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_HIGHEST);
    char rv[16] = "";
    GetEnvironmentVariableA("MONOHOST_JITREPS", rv, sizeof(rv));
    const int reps = rv[0] ? atoi(rv) : 7;
    double off[JIT_CASES], on[JIT_CASES];
    for (int c = 0; c < JIT_CASES; c++) {
        const int n = 200000;
        double bOff = 1e9, bOn = 1e9;
        int sOff = 0, sOn = 0;
        for (int r = 0; r < reps; r++) {
            JitSetPatched(false);
            double t = JitTime(c, n, &sOff);
            if (t < bOff) bOff = t;
            JitSetPatched(true);
            t = JitTime(c, n, &sOn);
            if (t < bOn) bOn = t;
        }
        off[c] = bOff, on[c] = bOn;
        if (sOff != sOn) Log("jitbench case %d CHECKSUM DIFFERS: off %d on %d\n", c, sOff, sOn);
    }
    const double loop = (off[20] + on[20]) / 2;
    // the empty loop's cost per iteration; a case whose loop steps by 64 or 256 (HostTest.JitBench) pays it that many times less
    static const int kStep[JIT_CASES] = {1, 1, 1, 256, 1, 1, 1, 1, 1, 1, 1, 1, 256, 1, 1, 1, 1, 1, 64, 64, 1, 64, 64, 64, 64, 64, 64, 64, 64, 64, 64};
    Log("jitbench (best of %d, n = 200000, pinned; ns per iteration, loop overhead %.1f ns / the case's step subtracted):\n", reps, loop);
    for (int c = 0; c < JIT_CASES; c++)
        if (c != 20) Log("jitbench case %2d %-40s interp %7.1f ns  compiled %7.1f ns  speedup %.2fx  saved %6.1f ns\n", c, kJitCase[c], off[c] - loop / kStep[c],
            on[c] - loop / kStep[c], (off[c] - loop / kStep[c]) / (on[c] - loop / kStep[c]), off[c] - on[c]);
    // whole MintBench (docs/139), JIT off vs on, interleaved
    int n = 3000, s0 = 0, s1 = 0;
    void* args[1] = {&n};
    LARGE_INTEGER fq, t0, t1;
    QueryPerformanceFrequency(&fq);
    double b0 = 1e9, b1 = 1e9;
    for (int r = 0; r < reps; r++) {
        for (int on2 = 0; on2 < 2; on2++) {
            JitSetPatched(on2 == 1);
            QueryPerformanceCounter(&t0);
            int s = I32(T("MintBench", 1, args));
            QueryPerformanceCounter(&t1);
            double ms = 1000.0 * (t1.QuadPart - t0.QuadPart) / fq.QuadPart;
            if (on2) { s1 = s; if (ms < b1) b1 = ms; } else { s0 = s; if (ms < b0) b0 = ms; }
        }
    }
    Log("jitbench MintBench(3000): interp %.1f ms, compiled %.1f ms (%+.1f%%), checksums %d / %d%s\n", b0, b1, 100.0 * (b1 - b0) / b0, s0, s1,
        s0 == s1 ? "" : " DIFFER");
    JitSetPatched(true);
}

// MONOHOST_DIVGUARD=1: the two jump-table stubs of [EaCrashFixes] MintDivGuard (ts3native eafix.cpp, docs/213 §8), so
// INT_MIN / -1 throws OverflowException and INT_MIN % -1 gives 0, as in the game with the fix, instead of the CPU divide
// error that kills the host (#114). Installed before the handler table is copied, so compiled exits use the stubs too.
static DWORD g_dgDiv = 0xE55B4C, g_dgRem = 0xE55C0F, g_dgOvf = 0xE57AA5, g_dgNext = 0xE544D5;
static __declspec(naked) void DgDivStub() {
    __asm {
        cmp dword ptr [esi - 8], -1
        jne game
        cmp dword ptr [esi - 0x10], 0x80000000
        jne game
        jmp dword ptr [g_dgOvf]
    game:
        jmp dword ptr [g_dgDiv]
    }
}
static __declspec(naked) void DgRemStub() {
    __asm {
        cmp dword ptr [esi - 8], -1
        jne game
        cmp dword ptr [esi - 0x10], 0x80000000
        jne game
        add esi, -8
        mov dword ptr [esi - 8], 0
        add edi, 2
        mov dword ptr [ebp - 4], edi
        jmp dword ptr [g_dgNext]
    game:
        jmp dword ptr [g_dgRem]
    }
}
static bool InstallDivGuard() {
    DWORD* t = (DWORD*)(uintptr_t)MINT_TABLE;
    if (t[0x127] != g_dgDiv || t[0x143] != g_dgRem) return false;
    const DWORD dv = (DWORD)(uintptr_t)DgDivStub, rv = (DWORD)(uintptr_t)DgRemStub;
    return mt::Write(t + 0x127, &dv, 4) && mt::Write(t + 0x143, &rv, 4);
}

static bool InstallJit() {
    char v[16] = "";
    GetEnvironmentVariableA("MONOHOST_JIT", v, sizeof(v));
    g_jitMode = !strcmp(v, "dump") ? 1 : !strcmp(v, "on") ? 2 : !strcmp(v, "verify") ? 3 : 0;
    if (!g_jitMode) return true;
    char dg[4] = "";
    if (GetEnvironmentVariableA("MONOHOST_DIVGUARD", dg, sizeof(dg)) && dg[0] == '1')
        Log("jit: div guard (MintDivGuard stubs) %s\n", InstallDivGuard() ? "installed" : "NOT installed: table entries differ");
    InitializeCriticalSection(&g_jitCs);
    if (g_jitMode >= 2) {
        const char* const* names = (const char* const*)(uintptr_t)0x11949E0;
        char x[8] = "";
        g_jitObjectClass = *(void**)(uintptr_t)(0x1220E40 + 4);  // mono_defaults.object_class (monohost.cpp reads the same)
        DWORD* table = (DWORD*)(uintptr_t)MINT_TABLE;
        for (DWORD op = 0; op < 0x1C1; op++) g_opHandler[op] = (void*)(uintptr_t)table[op];
        // MONOHOST_JITC2=<mask> keeps only these round 10 C2 template groups (default all): 1 float fields + ldflda, 2 static
        // fields, 4 the array loads / stores added here, 8 stobj, 16 cknull / strlen / getchr, 32 div.un.i4 / rem.un.i4 / rem.r8,
        // 64 switch, 128 the value-type moves and ceq.i8 (round 10 C1), 256 initobj / isinst / shr.un.i8 and the direct internal calls (C5)
        unsigned c2 = 511;
        if (GetEnvironmentVariableA("MONOHOST_JITC2", x, sizeof(x))) c2 = (unsigned)atoi(x);
        g_jitC5 = (c2 & 256) != 0;
        if (GetEnvironmentVariableA("MONOHOST_JITWIDE", x, sizeof(x)) && x[0] == '1') c2 |= 512, g_jitSse3 = IsProcessorFeaturePresent(13) != 0;
        g_jitCallExit = GetEnvironmentVariableA("MONOHOST_JITCALLEXIT", x, sizeof(x)) && x[0] == '1';
        g_jitPureIcalls = g_jitCallExit && JitPureIcallsOk();
        if ((g_jitVtOps = GetEnvironmentVariableA("MONOHOST_JITVTOPS", x, sizeof(x)) && x[0] == '1')) c2 |= 1024;
        g_jitNewVt = GetEnvironmentVariableA("MONOHOST_JITNEWVT", x, sizeof(x)) && x[0] == '1';
        const bool fcWant = GetEnvironmentVariableA("MONOHOST_JITFASTCOPY", x, sizeof(x)) && x[0] == '1';
        g_jitFastCopy = fcWant && JitFastCopyOk();
        if (fcWant && !g_jitFastCopy) Log("jit: FastCopy: EA's Array.FastCopy bytes differ, it stays an EA call\n");
        g_jitLdsflda = GetEnvironmentVariableA("MONOHOST_JITLDSFLDA", x, sizeof(x)) && x[0] == '1';
        Log("jit: switches FastCopy=%d Ldsflda=%d\n", g_jitFastCopy ? 1 : 0, g_jitLdsflda ? 1 : 0);
        if (g_jitCallExit && !g_jitPureIcalls) Log("jit: CallExit: the Array icall bytes differ, pure icalls stay EA calls\n");
        for (auto& t : kJitTmpl) {
            const unsigned g = t.kind >= K_FARITH ? 512 : t.kind == K_LDFLDA || !strcmp(t.name, "ldfld.r4") || !strcmp(t.name, "ldfld.r8") || !strcmp(t.name, "stfld.r4") || !strcmp(t.name, "stfld.r8") ? 1
                : t.kind == K_LDSFLD || t.kind == K_LDSFLDA || t.kind == K_STSFLD ? 2
                : t.kind == K_STELEM || t.kind == K_STELEMREF || !strcmp(t.name, "ldelem.r4") || !strcmp(t.name, "ldelem.r8") ? 4
                : t.kind == K_STOBJ ? 8 : t.kind == K_CKNULL || t.kind == K_STRLEN || t.kind == K_GETCHR ? 16
                : t.kind == K_DIVUN || t.kind == K_REMR8 ? 32 : t.kind == K_SWITCH ? 64
                : t.kind == K_LDVT || t.kind == K_STVT || t.kind == K_STINARGVT || t.kind == K_DUPVT || t.kind == K_VTRESULT || t.kind == K_CEQ8 || t.kind == K_LDFLDVT || t.kind == K_LDSFLDVT ? 128
                : t.kind >= K_STFLDVT && t.kind <= K_STOBJVT ? 1024
                : t.kind == K_FCMP || t.kind == K_FBRS || t.kind == K_FBR || t.kind == K_INITOBJ || t.kind == K_ISINST || t.kind == K_SHRUN8 || t.kind == K_LDFP || t.kind == K_STFP || t.kind == K_STINARGFP || t.kind == K_LDCFP ? 256 : 0;
            if (g & ~c2) continue;
            for (DWORD op = 0; op <= 0x1A8; op++)
                if (!strcmp(names[op], t.name)) { g_opInfo[op] = {t.kind, t.width, t.x}; break; }
        }
        g_jitTab = (JitEnt*)VirtualAlloc(NULL, sizeof(JitEnt) * (JIT_TAB_N + 4096), MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
        g_jitCode = (u8*)VirtualAlloc(NULL, 4 << 20, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
        g_jitCodeEnd = g_jitCode + (4 << 20);
        g_jitPos = (unsigned short*)malloc(400000 * 2), g_jitOrigW = (unsigned short*)malloc(400000 * 2);
        g_jitPatched = true;
        g_jitExitAll = GetEnvironmentVariableA("MONOHOST_JITEXITALL", x, sizeof(x)) && x[0] == '1';
        g_jitCountOn = GetEnvironmentVariableA("MONOHOST_JITCOUNT", x, sizeof(x)) && x[0] == '1';
        if (g_jitCountOn) g_jitSite = (JitSite*)calloc(65536, sizeof(JitSite));
        g_jitInline = GetEnvironmentVariableA("MONOHOST_JITINLINE", x, sizeof(x)) ? atoi(x) : 0;
        g_jitLazy = GetEnvironmentVariableA("MONOHOST_JITLAZY", x, sizeof(x)) && x[0] == '1';
        g_jitDumpCode = GetEnvironmentVariableA("MONOHOST_JITDUMPCODE", x, sizeof(x)) && x[0] == '1';
        g_jitVerifyOn = g_jitMode == 3 ? 1 : g_jitCountOn ? 2 : 0;
        if (g_jitMode == 3 && GetEnvironmentVariableA("MONOHOST_JITVERIFY", x, sizeof(x)) && !strcmp(x, "bench")) g_jitVerifyOn = 0;
        JitGateInit();
        g_jitMainTid = GetCurrentThreadId();
        g_jitV = (JitV*)calloc(JIT_VD, sizeof(JitV));
        for (DWORD d = 0; d < JIT_VD && g_jitMode == 3; d++) {
            JitSnap** ss[3] = {&g_jitV[d].s0, &g_jitV[d].s1, &g_jitV[d].s2};
            for (JitSnap** s : ss) {
                *s = (JitSnap*)calloc(1, sizeof(JitSnap));
                (*s)->cap = 1 << 20, (*s)->buf = (u8*)VirtualAlloc(NULL, 1 << 20, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
            }
            g_jitV[d].nw = (JitNewSet*)calloc(1, sizeof(JitNewSet));
            g_jitV[d].nw->buf = (u8*)VirtualAlloc(NULL, 1 << 20, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
        }
        memcpy(g_atomicOrig, (const void*)(uintptr_t)0xE4BDC0, 5);
        if (g_jitMode == 3 && !JitAtomic(true)) { Log("jit: atomic alloc redirect failed\n"); return false; }
        DWORD ent = (DWORD)(uintptr_t)JitEnter;
        if (!g_jitTab || !g_jitCode || !mt::Write(table + JIT_OP, &ent, 4)) { Log("jit: setup failed\n"); return false; }
        Log("jit: JIT_OP 0x%X -> JitEnter, %s%s\n", JIT_OP, g_jitExitAll ? "every instruction an exit, " : "", g_jitCountOn ? "counting" : "");
        wchar_t cap[MAX_PATH] = L"";
        if (GetEnvironmentVariableW(L"MONOHOST_JITCAPTURE", cap, MAX_PATH) && !JcInit(cap, 8, 8, 4, 256, 65536, 16384, 1000000, 64u << 20)) Log("jit capture: cannot open the file\n");
    }
    if (GetEnvironmentVariableA("MONOHOST_JITLIST", g_jitListBuf, sizeof(g_jitListBuf))) {
        char* ctx = NULL;
        for (char* t = strtok_s(g_jitListBuf, ",", &ctx); t && g_jitNameN < 256; t = strtok_s(NULL, ",", &ctx)) g_jitNames[g_jitNameN++] = t;
    } else
        for (const char* n : kJitList) g_jitNames[g_jitNameN++] = n;
    char path[MAX_PATH];
    GetModuleFileNameA(NULL, path, MAX_PATH);
    strcpy_s(strrchr(path, '\\') + 1, 64, "jit-dump.txt");
    fopen_s(&g_jitDump, path, "w");
    Log("jit: mode %s, %d listed names\n", v, g_jitNameN);
    return InstallJitHook();
}
