// TestMonoJit ([MonoJit], monojit.cpp, docs/527 step 1). Included by selftest.cpp. No EA interpreter here: a fake opcode table
// whose entries stop the run (JitTestStop) stands in for EA's handlers, so compiled templates really execute: what a run must
// leave is the state an exit hands the interpreter (edi = ip, eax = op, esi, arena). The verify test adds a one-opcode
// interpreter (JitTestLdc) for pass 2. Not covered: EA's real opcode numbers and handlers (the monohost corpus has those).

enum { JT_LDC2 = 1, JT_LDC3, JT_ADD, JT_STLOC, JT_LDLOC, JT_RET, JT_BRS, JT_BRTRUE, JT_LDFLD, JT_STFLD, JT_LDELEM, JT_LDFLDR4, JT_LDLOCVT, JT_LDCS, JT_MYSTERY, JT_INITOBJ, JT_ISINST, JT_SHRUN8, JT_LDCR4, JT_LDCR8, JT_LDLOCR4, JT_LDLOCR8, JT_STLOCR4, JT_STLOCR8, JT_CEQR8, JT_BLTR8S, JT_LDTHIS, JT_LDIND, JT_LDLEN, JT_ADDR8, JT_CONVU4R8, JT_CONVI8R8, JT_CONVR4R8, JT_LDINDR8, JT_STINDR4, JT_LDSTR, JT_LDTOKEN, JT_CASTCLASS, JT_LEAVES = 0x9C, JT_SWITCH = 0xE8 };
static const char* g_jtNames[0x1A9];
static BYTE g_jtLen[0x1C1];
static DWORD g_jtTable[0x1C1];
static DWORD g_jtEsp, g_jtEdi, g_jtEsi, g_jtEax, g_jtEbpIn, g_jtSpIn, g_jtIpIn, g_jtInterp;
static DWORD g_jtFr[64], g_jtArena[64], g_jtLocals[16], g_jtVt[8];
static BYTE g_jtInv[0x20], g_jtState[0x150];
static DWORD* const g_jtEbp = g_jtFr + 32;

static __declspec(naked) void JitTestStop() {
    __asm {
        mov g_jtEdi, edi
        mov g_jtEsi, esi
        mov g_jtEax, eax
        mov esp, g_jtEsp
        pop edi
        pop esi
        pop ebx
        pop ebp
        ret
    }
}
static __declspec(naked) void JitTestRun() {  // the interpreter's dispatch of the first word
    __asm {
        push ebp
        push ebx
        push esi
        push edi
        mov g_jtEsp, esp
        mov ebp, g_jtEbpIn
        mov esi, g_jtSpIn
        mov edi, g_jtIpIn
        movzx eax, word ptr [edi]
        mov edx, eax
        mov ecx, g_jitTableP
        jmp dword ptr [ecx + eax * 4]
    }
}
static __declspec(naked) void JitTestLdc() {  // pass 2's interpreter: one opcode, pushes g_jtInterp, dispatches the next word
    __asm {
        mov eax, g_jtInterp
        mov [esi], eax
        add esi, 8
        add edi, 2
        movzx eax, word ptr [edi]
        mov edx, eax
        mov ecx, g_jitTableP
        jmp dword ptr [ecx + eax * 4]
    }
}
static void __cdecl JtMemcpy(void* d, const void* s, size_t n) { memmove(d, s, n); }
static void __cdecl JtMemset(void* d, int v, size_t n) { memset(d, v, n); }
static int __cdecl JtIfaceOff(const BYTE* klass, DWORD iid) { return klass[0x20] == iid ? 8 : -1; }  // the test's classes list one interface
static __declspec(naked) void JtAullshr() { __asm { shrd eax, edx, cl __asm shr edx, cl __asm ret } }  // counts below 32 are enough here
static BYTE* __cdecl JtNoBase(const void*) { return NULL; }
static DWORD g_jtHeap[4];
static BYTE* __cdecl JtHeapBase(const void* p) { return p >= g_jtHeap && p < g_jtHeap + 4 ? (BYTE*)g_jtHeap : NULL; }
static void __cdecl JtNoRoots(void*, void*) {}

static void JtFrame() {
    memset(g_jtFr, 0, sizeof(g_jtFr)), memset(g_jtArena, 0, sizeof(g_jtArena)), memset(g_jtLocals, 0, sizeof(g_jtLocals)), memset(g_jtVt, 0, sizeof(g_jtVt));
    memset(g_jtInv, 0, sizeof(g_jtInv)), memset(g_jtState, 0, sizeof(g_jtState));
    *(DWORD**)(g_jtInv + 0x10) = g_jtArena;
    g_jtEbp[-2] = (DWORD)(uintptr_t)g_jtInv;     // [ebp-8] MonoInvocation
    g_jtEbp[-3] = (DWORD)(uintptr_t)g_jtVt;      // [ebp-0xC] the vt cursor
    g_jtEbp[-5] = (DWORD)(uintptr_t)g_jtLocals;  // [ebp-0x14] locals
    g_jtEbp[2] = (DWORD)(uintptr_t)g_jtState;    // [ebp+8] the thread's interpreter state
}
static void JtGo(const unsigned short* ip, DWORD spWords = 0) {  // spWords: arena dwords already on the eval stack
    g_jtEbpIn = (DWORD)(uintptr_t)g_jtEbp, g_jtSpIn = (DWORD)(uintptr_t)(g_jtArena + spWords), g_jtIpIn = (DWORD)(uintptr_t)ip;
    JitTestRun();
}
static DWORD WINAPI JtThread(void*) { JitTestRun(); return 0; }

struct JtMeth { BYTE rm[0x48], mm[0x20], klass[0x40]; };
static void JtInit(JtMeth& m, unsigned short* code, int words, const char* name = "Meth") {
    memset(&m, 0, sizeof(m));
    *(unsigned short**)m.rm = code;
    *(BYTE**)(m.rm + 8) = m.mm;
    *(WORD*)(m.rm + 0x34) = 64;   // with +0x3E: where the eval stack ends (the verifier's dead area)
    *(WORD*)(m.rm + 0x38) = sizeof(g_jtArena);
    *(WORD*)(m.rm + 0x3A) = (WORD)words;
    *(WORD*)(m.rm + 0x3E) = 32;
    *(BYTE**)(m.mm + 8) = m.klass;
    *(const char**)(m.mm + 0x18) = name;
    *(const char**)(m.klass + 0x34) = "Cls";
    *(const char**)(m.klass + 0x38) = "Ns";
}
static void JtCompile(JtMeth& m, unsigned short* code, int words) {
    JtInit(m, code, words);
    assert(JitCompile(m.rm) == NULL);
}

// [MonoJit] Direct (docs/537): the frame JitLazyMat writes needs the real layout below ebp, so these runs get their own frame:
// the record array at ebp-0x4C74, the depth high-water mark [ebp-0x30], the arena chunk, the ThreadContext at [ebp+0xC].
static BYTE g_jtBig[0x5000], g_jtCtx[16], g_jtInvs[6][0x28], g_jtForceByte = 0x75;
static DWORD* const g_jtL = (DWORD*)(g_jtBig + 0x4E00);
static DWORD g_jtChunkWords[512], g_jtChunk[2];
static void* g_jtProxy;
static const u8* JtRec(int d) { return (const u8*)g_jtL + d * 0x130 - 0x4C74; }
static void JtLazyFrame() {
    JtFrame();
    memset(g_jtBig, 0, sizeof(g_jtBig)), memset(g_jtCtx, 0, sizeof(g_jtCtx)), memset(g_jtInvs, 0, sizeof(g_jtInvs));
    *(DWORD**)(g_jtInvs[0] + 0x10) = g_jtArena;
    g_jtL[-2] = (DWORD)(uintptr_t)g_jtInvs[0], g_jtL[-3] = (DWORD)(uintptr_t)g_jtVt, g_jtL[-5] = (DWORD)(uintptr_t)g_jtLocals;
    g_jtL[2] = (DWORD)(uintptr_t)g_jtState, g_jtL[3] = (DWORD)(uintptr_t)g_jtCtx;
    g_jtL[-0x30 / 4] = 5;  // slots 0..4 hold a MonoInvocation
    for (int d = 0; d < 5; d++) *(u8**)JtRec(d) = g_jtInvs[d];
    g_jtChunk[0] = (DWORD)(uintptr_t)g_jtChunkWords, g_jtChunk[1] = g_jtChunk[0] + sizeof(g_jtChunkWords);
    *(DWORD**)(g_jtState + 0x13C) = g_jtChunk;
}
static void JtLazyGo(const unsigned short* ip, DWORD spWords = 0) {
    g_jtEbpIn = (DWORD)(uintptr_t)g_jtL, g_jtSpIn = (DWORD)(uintptr_t)(g_jtArena + spWords), g_jtIpIn = (DWORD)(uintptr_t)ip;
    JitTestRun();
}
static DWORD g_jtWatched;
static HANDLE g_jtGoRead, g_jtArmed;
static DWORD WINAPI JtReader(void*) {  // another thread reads a watched word: the trap must see it
    SetEvent(g_jtArmed);
    WaitForSingleObject(g_jtGoRead, 10000);
    return g_jtWatched;
}
static void JtMake(JtMeth& m, unsigned short* code, int words, DWORD flags, const DWORD* items = NULL, int loc = 0) {
    JtInit(m, code, words);
    *(DWORD*)(m.rm + 0x44) = flags | 0x100;  // transformed; low byte 1 = void
    *(const DWORD**)(m.rm + 0x14) = items;
    *(WORD*)(m.rm + 0x38) = 256, *(WORD*)(m.rm + 0x34) = 64, *(WORD*)(m.rm + 0x36) = 8, *(WORD*)(m.rm + 0x3E) = 32;
    if (loc) *(WORD*)(m.rm + 0x3C) = (WORD)loc;
    assert(JitCompile(m.rm) == NULL);
}

// [MonoJit] Inline (docs/537 §9): a getter's body runs at the call site; every test leaves EA's call with the eval stack as it was
static void TestMonoJitInline() {
    Ts3nCounters& C = ts3native_counters;
    const LONG in0 = C.jitInlined, calls0 = C.jitLazyCalls;
    const bool lazy0 = g_jitLazy;
    g_jitMode = 1, g_jitVerifyOn = 0, g_jitLazy = false, g_jitInline = 1;
    g_jitForceJit = &g_jtForceByte, g_jitProxyClassP = (const void* const*)&g_jtProxy;
    g_jtLen[0xEA] = 2, g_jtLen[0xEB] = 3, g_jtLen[0xEC] = 4;
    static BYTE klassA[8], klassP[8];
    static DWORD vtA[1], vtP[1], arr[3], inner[3], obj[4], objP[4];
    vtA[0] = (DWORD)(uintptr_t)klassA, vtP[0] = (DWORD)(uintptr_t)klassP, g_jtProxy = klassP;
    obj[0] = (DWORD)(uintptr_t)vtA, obj[1] = (DWORD)(uintptr_t)inner, obj[2] = 77, objP[0] = (DWORD)(uintptr_t)vtP;
    inner[2] = 88, arr[2] = 6;
    static unsigned short t1[] = {JT_LDTHIS, JT_LDFLD, 8, JT_RET}, t2[] = {JT_LDTHIS, JT_LDFLD, 4, JT_LDFLD, 8, JT_RET};
    static unsigned short t3[] = {JT_LDTHIS, JT_LDIND, JT_RET}, t4[] = {JT_LDTHIS, JT_LDFLD, 4, JT_LDLEN, JT_RET};
    static JtMeth m1, m2, m3, m4, c1, c2, c3, c4, cc3;
    JtMake(m1, t1, 4, 0x200), JtMake(m2, t2, 6, 0x200), JtMake(m3, t3, 3, 0x600), JtMake(m4, t4, 5, 0x200);
    static DWORD i1[1], i2[1], i3[2], i4[1];
    i1[0] = (DWORD)(uintptr_t)m1.rm, i2[0] = (DWORD)(uintptr_t)m2.rm, i3[0] = (DWORD)(uintptr_t)m3.rm, i4[0] = (DWORD)(uintptr_t)m4.rm;
    const auto site = [&](JtMeth& m, unsigned short* f, int words, const DWORD* items) { JtMake(m, f, words, 0, items); };
    static unsigned short f1[] = {0xEA, 0, JT_RET}, f2[] = {0xEA, 0, JT_RET}, f3[] = {0xEA, 0, JT_RET}, f4[] = {0xEA, 0, JT_RET}, f5[] = {0xEB, 1, 0, JT_RET};
    site(c1, f1, 3, i1);
    assert(C.jitInlined == in0 + 1);
    const LONG no0 = C.jitInlineNo;
    site(c2, f2, 3, i2);  // tier 1: ldfld on this only
    assert(C.jitInlined == in0 + 1 && C.jitInlineNo == no0 + 1);
    site(c3, f3, 3, i3);  // tier 1: no value-type this
    assert(C.jitInlined == in0 + 1);
    const auto run = [&](const unsigned short* f, DWORD self) {
        JtLazyFrame();
        g_jtChunkWords[(32 + 4) / 4] = 0x11223344;      // the callee's eval slot 0: the high dword EA's return copy carries
        g_jtArena[0] = self, g_jtArena[1] = 0xAAAAAAAA;
        JtLazyGo(f, 2);
    };
    run(f1, (DWORD)(uintptr_t)obj);
    assert(g_jtEax == JT_RET && g_jtEdi == (DWORD)(uintptr_t)(f1 + 2) && g_jtArena[0] == 77 && g_jtArena[1] == 0x11223344 && g_jtEsi == (DWORD)(uintptr_t)(g_jtArena + 2));
    assert(C.jitLazyCalls == calls0 && !*(DWORD*)(g_jtState + 0x140));
    run(f1, 0);
    assert(g_jtEax == 0xEA && g_jtEdi == (DWORD)(uintptr_t)f1 && g_jtArena[1] == 0xAAAAAAAA && !g_jtArena[0] && g_jtEsi == (DWORD)(uintptr_t)(g_jtArena + 2));
    run(f1, (DWORD)(uintptr_t)objP);
    assert(g_jtEax == 0xEA && g_jtArena[1] == 0xAAAAAAAA);  // the remoting class
    g_jtForceByte = 0x74;
    run(f1, (DWORD)(uintptr_t)obj);
    assert(g_jtEax == 0xEA && g_jtArena[1] == 0xAAAAAAAA);
    g_jtForceByte = 0x75;
    JtLazyFrame();
    g_jtChunk[1] = g_jtChunk[0] + 16;  // no room for the callee's frame
    g_jtArena[0] = (DWORD)(uintptr_t)obj, g_jtArena[1] = 0xAAAAAAAA;
    JtLazyGo(f1, 2);
    assert(g_jtEax == 0xEA && g_jtArena[1] == 0xAAAAAAAA);

    // tier 2: a field of a field, a null inner base leaves before any write; ldind on a value-type this; ldlen; con.call
    g_jitInline = 2;
    static unsigned short f2b[] = {0xEA, 0, JT_RET}, f3b[] = {0xEA, 0, JT_RET};
    static JtMeth c2b, c3b;
    site(c2b, f2b, 3, i2), site(c3b, f3b, 3, i3), site(c4, f4, 3, i4);
    i3[1] = i3[0];
    site(cc3, f5, 4, i3);
    assert(C.jitInlined == in0 + 5);
    run(f2b, (DWORD)(uintptr_t)obj);
    assert(g_jtEax == JT_RET && g_jtArena[0] == 88 && g_jtArena[1] == 0x11223344);
    obj[1] = 0;
    run(f2b, (DWORD)(uintptr_t)obj);
    assert(g_jtEax == 0xEA && g_jtEdi == (DWORD)(uintptr_t)f2b && g_jtArena[0] == (DWORD)(uintptr_t)obj && g_jtArena[1] == 0xAAAAAAAA);
    obj[1] = (DWORD)(uintptr_t)inner;
    static DWORD val = 1234;
    run(f3b, (DWORD)(uintptr_t)&val);  // a byref: no vtable to read
    assert(g_jtEax == JT_RET && g_jtArena[0] == 1234);
    run(f5, (DWORD)(uintptr_t)&val);
    assert(g_jtEax == JT_RET && g_jtEdi == (DWORD)(uintptr_t)(f5 + 3) && g_jtArena[0] == 1234);
    obj[1] = (DWORD)(uintptr_t)arr;
    run(f4, (DWORD)(uintptr_t)obj);
    assert(g_jtEax == JT_RET && g_jtArena[0] == 6);
    g_jitInline = 0, g_jitLazy = lazy0;
    C.jitInlined = C.jitInlineNo = 0;
    g_jitForceJit = (const BYTE*)(uintptr_t)TS3A(0x00E5637D), g_jitProxyClassP = (const void* const*)(uintptr_t)TS3A(0x01220EC0);
}

// [MonoJit] Wide (docs/537 §10): group 512 stays out of Groups' default; its templates give EA's bits, a null or failed cast leaves through EA's handler
static void TestMonoJitWide() {
    assert(g_opInfo[JT_ADDR8].kind == K_EXIT && g_opInfo[JT_LDSTR].kind == K_EXIT);  // the 511 of the test above
    assert(JitTemplates(1023) == 38 && g_opInfo[JT_ADDR8].kind == K_FARITH && g_opInfo[JT_CASTCLASS].kind == K_CASTCLASS);
    static BYTE sse2 = 1;
    const BYTE* sse2Was = g_jitSse2;
    g_jitSse2 = &sse2, g_jitSse3 = true, g_jitMode = 1, g_jitVerifyOn = 0;
    const auto dbl = [](int w, double d) { memcpy(g_jtArena + w, &d, 8); };
    static unsigned short a[] = {JT_ADDR8, JT_RET}, b[] = {JT_CONVU4R8, JT_RET}, c[] = {JT_CONVI8R8, JT_RET}, d[] = {JT_CONVR4R8, JT_RET};
    static JtMeth ma, mb, mc, md;
    JtMake(ma, a, 2, 0), JtMake(mb, b, 2, 0), JtMake(mc, c, 2, 0), JtMake(md, d, 2, 0);
    double r;
    JtFrame(); dbl(0, 1.5), dbl(2, 2.25);
    JtGo(a, 4);
    memcpy(&r, g_jtArena, 8);
    assert(g_jtEax == JT_RET && r == 3.75 && g_jtEsi == (DWORD)(uintptr_t)(g_jtArena + 2));
    JtFrame(); dbl(0, 4000000000.75); g_jtArena[1] |= 0;
    const DWORD hi = g_jtArena[1];
    JtGo(b, 2);
    assert(g_jtArena[0] == 4000000000u && g_jtArena[1] == hi);  // the low dword only, as EA's
    JtFrame(); dbl(0, -5.5);
    JtGo(c, 2);
    assert(g_jtArena[0] == (DWORD)-5 && g_jtArena[1] == 0xFFFFFFFF);
    JtFrame(); g_jtArena[0] = 0, g_jtArena[1] = 0x7FF80000;
    JtGo(c, 2);
    assert(!g_jtArena[0] && g_jtArena[1] == 0x80000000);  // NaN: the integer indefinite
    JtFrame(); dbl(0, 1.0 / 3.0);
    JtGo(d, 2);
    memcpy(&r, g_jtArena, 8);
    assert(r == (double)(float)(1.0 / 3.0));
    // ldind.r8, stind.r4: a null leaves at the opcode with the stack as it was
    static double src = 6.5;
    static float dst;
    static unsigned short e[] = {JT_LDINDR8, JT_RET}, f[] = {JT_STINDR4, JT_RET};
    static JtMeth me, mf;
    JtMake(me, e, 2, 0), JtMake(mf, f, 2, 0);
    JtFrame(); g_jtArena[0] = (DWORD)(uintptr_t)&src;
    JtGo(e, 2);
    memcpy(&r, g_jtArena, 8);
    assert(g_jtEax == JT_RET && r == 6.5);
    JtFrame();
    JtGo(e, 2);
    assert(g_jtEax == JT_LDINDR8 && g_jtEdi == (DWORD)(uintptr_t)e);
    JtFrame(); g_jtArena[0] = (DWORD)(uintptr_t)&dst; dbl(2, 0.5);
    JtGo(f, 4);
    assert(g_jtEax == JT_RET && dst == 0.5f && g_jtEsi == (DWORD)(uintptr_t)g_jtArena);
    // ldstr and ldtoken read the method's items; ldtoken's value goes to the vt stack
    static DWORD items[2] = {0x11, 0x22};
    static unsigned short g[] = {JT_LDSTR, 1, JT_LDTOKEN, 0, JT_RET};
    static JtMeth mg;
    JtMake(mg, g, 5, 0, items);
    JtFrame();
    JtGo(g);
    assert(g_jtEax == JT_RET && g_jtArena[0] == 0x22 && g_jtArena[2] == (DWORD)(uintptr_t)g_jtVt && g_jtVt[0] == 0x11 && g_jtEbp[-3] == (DWORD)(uintptr_t)(g_jtVt + 2));
    // castclass: null passes, a match passes, a mismatch leaves
    static BYTE base[0xB0], sub[0xB0], other[0xB0];
    static const u8* superOfSub[2] = {base, sub};
    static const u8* superOfOther[2] = {other, other};
    base[0x14] = 1, *(WORD*)(base + 0x40) = 1;
    *(WORD*)(sub + 0x40) = 2, *(const u8***)(sub + 0x3C) = superOfSub;
    *(WORD*)(other + 0x40) = 2, *(const u8***)(other + 0x3C) = superOfOther;
    static DWORD vs[1], vo[1], os[2], oo[2], ci[1];
    vs[0] = (DWORD)(uintptr_t)sub, vo[0] = (DWORD)(uintptr_t)other, os[0] = (DWORD)(uintptr_t)vs, oo[0] = (DWORD)(uintptr_t)vo, ci[0] = (DWORD)(uintptr_t)base;
    static unsigned short h[] = {JT_CASTCLASS, 0, JT_RET};
    static JtMeth mh;
    JtMake(mh, h, 3, 0, ci);
    JtFrame();
    JtGo(h, 2);
    assert(g_jtEax == JT_RET && !g_jtArena[0]);
    JtFrame(); g_jtArena[0] = (DWORD)(uintptr_t)os;
    JtGo(h, 2);
    assert(g_jtEax == JT_RET && g_jtArena[0] == (DWORD)(uintptr_t)os);
    JtFrame(); g_jtArena[0] = (DWORD)(uintptr_t)oo;
    JtGo(h, 2);
    assert(g_jtEax == JT_CASTCLASS && g_jtEdi == (DWORD)(uintptr_t)h && g_jtArena[0] == (DWORD)(uintptr_t)oo);
    assert(ts3native_counters.jitWide >= 9);
    ts3native_counters.jitWide = ts3native_counters.jitWideNo = 0;
    g_jitSse2 = sse2Was, g_jitSse3 = false;
    memset(g_opInfo, 0, sizeof(g_opInfo));
    JitTemplates(511);
}

// [MonoJit] VtOps (docs/537 §20 B1): group 1024 stays out of Groups and Wide; the five ops copy as EA's handlers, a null or an
// index past the length leaves at the opcode with the stack and the vt cursor as they were, ldobj of a class with no layout or an
// enum compiles as an exit
static void TestMonoJitVtOps() {
    enum { OP_STFLDVT = 0x1A0, OP_LDELEMVT, OP_STELEMVT, OP_LDOBJ, OP_STOBJVT };
    const char* const nm[5] = {"stfld.vt", "ldelem.any.vt", "stelem.any.vt", "ldobj", "stobj.vt"};
    for (int i = 0; i < 5; i++) g_jtNames[OP_STFLDVT + i] = nm[i], g_jtLen[OP_STFLDVT + i] = i < 3 ? 4 : 2;
    const bool lazy0 = g_jitLazy;
    g_jitLazy = false, g_jitMode = 1, g_jitVerifyOn = 0;
    memset(g_opInfo, 0, sizeof(g_opInfo));
    assert(JitTemplates(1023) == 38 && g_opInfo[OP_STFLDVT].kind == K_EXIT && g_opInfo[OP_LDOBJ].kind == K_EXIT);
    memset(g_opInfo, 0, sizeof(g_opInfo));
    assert(JitTemplates(2047) == 43 && g_opInfo[OP_STFLDVT].kind == K_STFLDVT && g_opInfo[OP_STOBJVT].kind == K_STOBJVT);
    const DWORD vt = (DWORD)(uintptr_t)g_jtVt;
    static DWORD obj[8], arr[12], dst[4];
    static const DWORD src3[3] = {0x11, 0x22, 0x33};
    // stfld.vt: obj [esi-16], src [esi-8] at the cursor's last slot; 12 bytes at obj + 4, cursor back by 16
    static unsigned short a[] = {OP_STFLDVT, 4, 12, 0, JT_RET};
    static JtMeth ma;
    JtMake(ma, a, 5, 0);
    JtFrame(); memset(obj, 0, sizeof(obj)); memcpy(g_jtVt, src3, 12);
    g_jtArena[0] = (DWORD)(uintptr_t)obj, g_jtArena[2] = vt, g_jtEbp[-3] = vt + 16;
    JtGo(a, 4);
    assert(g_jtEax == JT_RET && !obj[0] && obj[1] == 0x11 && obj[3] == 0x33 && !obj[4] && g_jtEbp[-3] == vt && g_jtEsi == (DWORD)(uintptr_t)g_jtArena);
    JtFrame(); g_jtArena[2] = vt, g_jtEbp[-3] = vt + 16;
    JtGo(a, 4);
    assert(g_jtEax == OP_STFLDVT && g_jtEdi == (DWORD)(uintptr_t)a && g_jtEbp[-3] == vt + 16 && g_jtEsi == (DWORD)(uintptr_t)(g_jtArena + 4));
    // ldelem.any.vt / stelem.any.vt: 8-byte elements, length 3 at +8, data at +0x10; index 3 and -1 leave
    static unsigned short b[] = {OP_LDELEMVT, 0, 8, 0, JT_RET}, c[] = {OP_STELEMVT, 0, 8, 0, JT_RET};
    static JtMeth mb, mc;
    JtMake(mb, b, 5, 0), JtMake(mc, c, 5, 0);
    memset(arr, 0, sizeof(arr)); arr[2] = 3; arr[6] = 0xA1, arr[7] = 0xA2;
    JtFrame(); g_jtArena[0] = (DWORD)(uintptr_t)arr, g_jtArena[2] = 1;
    JtGo(b, 4);
    assert(g_jtEax == JT_RET && g_jtArena[0] == vt && g_jtVt[0] == 0xA1 && g_jtVt[1] == 0xA2 && g_jtEbp[-3] == vt + 8 && g_jtEsi == (DWORD)(uintptr_t)(g_jtArena + 2));
    for (DWORD bad : {3u, 0xFFFFFFFFu}) {
        JtFrame(); g_jtArena[0] = (DWORD)(uintptr_t)arr, g_jtArena[2] = bad;
        JtGo(b, 4);
        assert(g_jtEax == OP_LDELEMVT && g_jtArena[0] == (DWORD)(uintptr_t)arr && g_jtEbp[-3] == vt);
        JtFrame(); g_jtArena[0] = (DWORD)(uintptr_t)arr, g_jtArena[2] = bad, g_jtArena[4] = vt, g_jtEbp[-3] = vt + 8;
        JtGo(c, 6);
        assert(g_jtEax == OP_STELEMVT && g_jtEbp[-3] == vt + 8 && !arr[10]);
    }
    JtFrame(); g_jtArena[0] = 0;
    JtGo(b, 4);
    assert(g_jtEax == OP_LDELEMVT && g_jtEdi == (DWORD)(uintptr_t)b);
    JtFrame(); g_jtVt[0] = 0xB1, g_jtVt[1] = 0xB2;
    g_jtArena[0] = (DWORD)(uintptr_t)arr, g_jtArena[2] = 2, g_jtArena[4] = vt, g_jtEbp[-3] = vt + 8;
    JtGo(c, 6);
    assert(g_jtEax == JT_RET && arr[8] == 0xB1 && arr[9] == 0xB2 && arr[6] == 0xA1 && g_jtEbp[-3] == vt && g_jtEsi == (DWORD)(uintptr_t)g_jtArena);
    // ldobj / stobj.vt: a 12-byte value type (size = [class+0x54] - 4, layout flag 4); an enum or a class with no layout exits
    static BYTE cls[0xB0], en[0xB0], nolay[0xB0];
    cls[0xA6] = en[0xA6] = nolay[0xA6] = 0x11, cls[0x14] = 4, en[0x14] = 4 | 0x10;
    *(DWORD*)(cls + 0x54) = *(DWORD*)(en + 0x54) = *(DWORD*)(nolay + 0x54) = 16;
    static DWORD items[3];
    items[0] = (DWORD)(uintptr_t)cls, items[1] = (DWORD)(uintptr_t)en, items[2] = (DWORD)(uintptr_t)nolay;
    static unsigned short d[] = {OP_LDOBJ, 0, JT_RET}, f[] = {OP_STOBJVT, 0, JT_RET}, g[] = {OP_LDOBJ, 1, JT_RET}, h[] = {OP_LDOBJ, 2, JT_RET};
    static JtMeth md, mf, mg, mh;
    JtMake(md, d, 3, 0, items), JtMake(mf, f, 3, 0, items), JtMake(mg, g, 3, 0, items), JtMake(mh, h, 3, 0, items);
    for (unsigned short* x : {g, h}) {  // enum, no layout: the site is an exit, nothing changes
        JtFrame(); g_jtArena[0] = (DWORD)(uintptr_t)src3;
        JtGo(x, 2);
        assert(g_jtEax == OP_LDOBJ && g_jtEdi == (DWORD)(uintptr_t)x && g_jtArena[0] == (DWORD)(uintptr_t)src3 && g_jtEbp[-3] == vt);
    }
    JtFrame(); g_jtArena[0] = (DWORD)(uintptr_t)src3;
    JtGo(d, 2);
    assert(g_jtEax == JT_RET && g_jtArena[0] == vt && g_jtVt[0] == 0x11 && g_jtVt[2] == 0x33 && g_jtEbp[-3] == vt + 16);
    JtFrame();
    JtGo(d, 2);
    assert(g_jtEax == OP_LDOBJ && g_jtEdi == (DWORD)(uintptr_t)d && g_jtEbp[-3] == vt);
    JtFrame(); memset(dst, 0, sizeof(dst)); memcpy(g_jtVt, src3, 12);
    g_jtArena[0] = (DWORD)(uintptr_t)dst, g_jtArena[2] = vt, g_jtEbp[-3] = vt + 16;
    JtGo(f, 4);
    assert(g_jtEax == JT_RET && dst[0] == 0x11 && dst[2] == 0x33 && !dst[3] && g_jtEbp[-3] == vt && g_jtEsi == (DWORD)(uintptr_t)g_jtArena);
    JtFrame(); g_jtArena[2] = vt, g_jtEbp[-3] = vt + 16;
    JtGo(f, 4);
    assert(g_jtEax == OP_STOBJVT && g_jtEbp[-3] == vt + 16);
    for (int i = 0; i < 5; i++) g_jtNames[OP_STFLDVT + i] = NULL, g_jtLen[OP_STFLDVT + i] = 1;
    g_jitLazy = lazy0;
    memset(g_opInfo, 0, sizeof(g_opInfo));
    JitTemplates(511);
}

static void TestMonoJitDirect() {
    Ts3nCounters& C = ts3native_counters;
    g_jitMode = 1, g_jitVerifyOn = 0, g_jitLazy = true, g_jitLeafOnly = true;
    g_jitForceJit = &g_jtForceByte, g_jitProxyClassP = (const void* const*)&g_jtProxy;
    g_jtLen[0xEA] = 2, g_jtLen[0xEC] = 4;  // the fake table's call, callvirt
    const LONG sites0 = C.jitLazySites;

    // a leaf with a lazy body; a caller's call is native: value copied, nothing written, the arena back where it was
    static unsigned short e[] = {JT_LDC3, JT_RET};
    static JtMeth mE;
    JtMake(mE, e, 2, 0);
    assert(C.jitLazyBodies == 1);
    static DWORD fItems[1];
    fItems[0] = (DWORD)(uintptr_t)mE.rm;
    static unsigned short f[] = {JT_LDC2, 0xEA, 0, JT_ADD, JT_RET};
    static JtMeth mF;
    JtMake(mF, f, 5, 0, fItems);
    assert(C.jitLazyBodies == 1 && C.jitLazySites == sites0 + 1);  // DirectLeaf: a caller gets no lazy body of its own
    JtLazyFrame();
    JtLazyGo(f);
    assert(g_jtEax == JT_RET && g_jtEdi == (DWORD)(uintptr_t)(f + 4) && g_jtArena[0] == 5 && g_jtEsi == (DWORD)(uintptr_t)(g_jtArena + 2));
    assert(C.jitLazyCalls == 1 && !C.jitLazyMats && *(DWORD*)(g_jtState + 0x140) == 0 && !*(DWORD*)(g_jtCtx + 8));
    assert(g_jtChunk[0] == (DWORD)(uintptr_t)g_jtChunkWords && g_jtL[-2] == (DWORD)(uintptr_t)g_jtInvs[0]);

    // every test before the commit: a callee with no lazy body, a full depth, a chunk with no room, ForceJIT each take EA's call
    static unsigned short g[] = {JT_MYSTERY, JT_RET};
    static JtMeth mG;
    JtMake(mG, g, 2, 0);
    assert(C.jitLazyBodies == 1);
    static DWORD g2Items[1];
    g2Items[0] = (DWORD)(uintptr_t)mG.rm;
    static unsigned short f2[] = {0xEA, 0, JT_RET};
    static JtMeth mF2;
    JtMake(mF2, f2, 3, 0, g2Items);
    JtLazyFrame();
    JtLazyGo(f2);
    assert(g_jtEax == 0xEA && g_jtEdi == (DWORD)(uintptr_t)f2 && C.jitLazyCalls == 1);
    JtLazyFrame();
    g_jtL[-0x30 / 4] = 1;  // no MonoInvocation in the next slot yet
    JtLazyGo(f);
    assert(g_jtEax == 0xEA && g_jtEdi == (DWORD)(uintptr_t)(f + 1) && C.jitLazyCalls == 1);
    JtLazyFrame();
    g_jtChunk[1] = g_jtChunk[0] + 16;
    JtLazyGo(f);
    assert(g_jtEax == 0xEA && C.jitLazyCalls == 1 && g_jtChunk[0] == (DWORD)(uintptr_t)g_jtChunkWords);
    JtLazyFrame();
    g_jtForceByte = 0x74;
    JtLazyGo(f);
    assert(g_jtEax == 0xEA && C.jitLazyCalls == 1);
    g_jtForceByte = 0x75;

    // a null in the leaf: the frame is written at that exit, with every word EA's push writes; poison proves it
    static unsigned short h[] = {JT_LDLOC, 0, JT_LDFLD, 4, JT_RET};
    static JtMeth mH;
    JtMake(mH, h, 5, 0x800, NULL, 16);
    assert(C.jitLazyBodies == 2);
    static DWORD hItems[1];
    hItems[0] = (DWORD)(uintptr_t)mH.rm;
    static unsigned short fh[] = {JT_LDC2, 0xEA, 0, JT_ADD, JT_RET};
    static JtMeth mFh;
    JtMake(mFh, fh, 5, 0, hItems);
    JtLazyFrame();
    JitTrapPoison((u8*)g_jtL, 0);
    assert(*(const DWORD*)(JtRec(1) + 8) == (JIT_TRAP_POISON | 1) && *(DWORD*)(g_jtInvs[1] + 0x24) == (JIT_TRAP_POISON | 1) && *(DWORD*)(g_jtInvs[4] + 0x24) == (JIT_TRAP_POISON | 4));
    JtLazyGo(fh);
    assert(g_jtEax == JT_LDFLD && g_jtEdi == (DWORD)(uintptr_t)(h + 2) && g_jtL[-1] == g_jtEdi);
    assert(C.jitLazyMats == 1 && C.jitLazyFrames == 1 && C.jitLazyCalls == 2);
    assert(*(DWORD*)(g_jtState + 0x140) == 1 && *(DWORD*)(g_jtState + 0x144) == 1 && *(DWORD*)(g_jtCtx + 8) == (DWORD)(uintptr_t)g_jtInvs[1]);
    const DWORD* inv = (const DWORD*)g_jtInvs[1];
    const DWORD hArena = g_jtChunk[0] - 256;
    assert(g_jtL[-2] == (DWORD)(uintptr_t)inv && inv[0] == (DWORD)(uintptr_t)g_jtInvs[0] && inv[1] == (DWORD)(uintptr_t)mH.rm && inv[4] == hArena && inv[6] == hArena + 32);
    assert(g_jtL[-5] == hArena + 32 + 64 + 8 && g_jtL[-4] == (DWORD)(uintptr_t)mH.rm);
    const DWORD* r0 = (const DWORD*)JtRec(0);
    assert(r0[1] == (DWORD)(uintptr_t)(fh + 3) && r0[2] == (DWORD)(uintptr_t)(g_jtArena + 2) && *(DWORD*)(g_jtInvs[0] + 0x1C) == (DWORD)(uintptr_t)(fh + 1));
    assert(g_jtEsi == hArena + 32 + 8);  // the leaf's own eval stack: the local it loaded
    for (int w = 0; w < 10; w++) assert((inv[w] & 0xFFFF0000) != JIT_TRAP_POISON);
    const DWORD* r1 = (const DWORD*)JtRec(1);
    for (int w : {1, 2, 3, 4, 5, 0x128 / 4, 0x12C / 4}) assert((r1[w] & 0xFFFF0000) != JIT_TRAP_POISON);
    assert(*(const DWORD*)(JtRec(2) + 8) == (JIT_TRAP_POISON | 2));  // the slots no frame took keep it

    // DirectLeaf=0: chains: a value through two lazy bodies, then two frames written at one exit
    g_jitLeafOnly = false;
    static unsigned short i2[] = {0xEA, 0, JT_RET}, j2[] = {JT_LDC2, 0xEA, 0, JT_ADD, JT_RET};
    static JtMeth mI2, mJ2;
    JtMake(mI2, i2, 3, 0, fItems);
    assert(C.jitLazyBodies == 3);
    static DWORD j2Items[1];
    j2Items[0] = (DWORD)(uintptr_t)mI2.rm;
    JtMake(mJ2, j2, 5, 0, j2Items);
    const LONG calls0 = C.jitLazyCalls;
    JtLazyFrame();
    JtLazyGo(j2);
    assert(g_jtEax == JT_RET && g_jtArena[0] == 5 && C.jitLazyCalls == calls0 + 2 && C.jitLazyFrames == 1 && *(DWORD*)(g_jtState + 0x140) == 0);
    static unsigned short i3[] = {0xEA, 0, JT_RET}, j3[] = {0xEA, 0, JT_RET};
    static JtMeth mI3, mJ3;
    JtMake(mI3, i3, 3, 0, hItems);
    static DWORD j3Items[1];
    j3Items[0] = (DWORD)(uintptr_t)mI3.rm;
    JtMake(mJ3, j3, 3, 0, j3Items);
    JtLazyFrame();
    JtLazyGo(j3);
    assert(g_jtEax == JT_LDFLD && g_jtEdi == (DWORD)(uintptr_t)(h + 2) && C.jitLazyMats == 2 && C.jitLazyFrames == 3);
    assert(*(DWORD*)(g_jtState + 0x140) == 2 && *(DWORD*)(g_jtCtx + 8) == (DWORD)(uintptr_t)g_jtInvs[2] && g_jtL[-2] == (DWORD)(uintptr_t)g_jtInvs[2]);
    assert(*(DWORD*)g_jtInvs[2] == (DWORD)(uintptr_t)g_jtInvs[1] && *(DWORD*)(g_jtInvs[2] + 4) == (DWORD)(uintptr_t)mH.rm && *(DWORD*)(g_jtInvs[1] + 4) == (DWORD)(uintptr_t)mI3.rm);
    assert(((const DWORD*)JtRec(1))[1] == (DWORD)(uintptr_t)(i3 + 2) && ((const DWORD*)JtRec(0))[1] == (DWORD)(uintptr_t)(j3 + 2));

    // a guarded callvirt: the cache's target, when the receiver's class is the cache's; another class takes EA's path
    static DWORD klass[8], klass2[8], vtable[2], vtable2[2], obj[2], obj2[2], vItems[3];
    vtable[0] = (DWORD)(uintptr_t)klass, obj[0] = (DWORD)(uintptr_t)vtable;
    vtable2[0] = (DWORD)(uintptr_t)klass2, obj2[0] = (DWORD)(uintptr_t)vtable2;
    static unsigned short t[] = {JT_LDC3, JT_RET};
    static JtMeth mT, mD;
    JtMake(mT, t, 2, 0x200);
    JtInit(mD, t, 2);
    *(DWORD*)(mD.rm + 0x44) = 0x300;
    *(unsigned short*)mD.mm = 0x40;  // virtual
    vItems[0] = (DWORD)(uintptr_t)klass, vItems[1] = (DWORD)(uintptr_t)mT.rm, vItems[2] = (DWORD)(uintptr_t)mD.rm;
    static unsigned short k[] = {JT_LDLOC, 0, 0xEC, 0, 0, 2, JT_RET};
    static JtMeth mK;
    JtMake(mK, k, 7, 0, vItems);
    assert(C.jitGuardSites == 2 && g_jitGuardN == 2);  // the lazy body and the normal body each have the site
    const LONG calls1 = C.jitLazyCalls;
    JtLazyFrame();
    g_jtLocals[0] = (DWORD)(uintptr_t)obj;
    JtLazyGo(k);
    assert(g_jtEax == JT_RET && g_jtArena[0] == 3 && C.jitLazyCalls == calls1 + 1 && g_jitGuard[1].rm == mT.rm && g_jitGuard[1].entry);
    JtLazyFrame();
    g_jtLocals[0] = (DWORD)(uintptr_t)obj2;
    JtLazyGo(k);
    assert(g_jtEax == 0xEC && g_jtEdi == (DWORD)(uintptr_t)(k + 2) && C.jitLazyCalls == calls1 + 1);

    // a method a verify mismatch turned off: no site enters its lazy body any more
    JitMeth* killT = NULL;
    for (int i = 0; i < g_jitMethN; i++) if (g_jitMeth[i].rm == mT.rm) killT = &g_jitMeth[i];
    assert(killT);
    JitKill(*killT, (DWORD)(uintptr_t)(t + 1));
    assert(!g_jitGuard[1].entry);
    JtLazyFrame();
    g_jtLocals[0] = (DWORD)(uintptr_t)obj;
    JtLazyGo(k);
    assert(g_jtEax == 0xEC && C.jitLazyCalls == calls1 + 1);

    // the reader trap: compiled code does not count, a read in a window does, a window closes, and the other threads count
    // by the census. The first read happens in a real window, on real debug registers.
    DWORD jtCur = 0, *const jtCurSave = g_curState;
    g_curState = &jtCur;
    PVOID veh = AddVectoredExceptionHandler(1, JitTrapVeh);
    g_jitTrapVeh = veh;
    g_jitTrap = true, g_jitTrapEvery = 1, g_jitLeafOnly = false;
    static unsigned short i4[] = {0xEA, 0, JT_RET}, j4[] = {JT_LDC2, 0xEA, 0, JT_ADD, JT_RET};
    static JtMeth mI4, mJ4;
    JtMake(mI4, i4, 3, 0, fItems);
    static DWORD j4Items[1];
    j4Items[0] = (DWORD)(uintptr_t)mI4.rm;
    JtMake(mJ4, j4, 5, 0, j4Items);
    JtLazyFrame();
    JtLazyGo(j4);  // the inner site's depth test reads the watched depth in the window, from compiled code
    assert(g_jtEax == JT_RET && g_jtArena[0] == 5 && C.jitTrapWindows == 1 && !C.jitTrapHits && !C.jitTrapUnknown && !g_jitTrapOpen);
    JtLazyFrame();
    JitTrapOpen((u8*)g_jtL);
    assert(g_jitTrapOpen && C.jitTrapWindows == 2);
    volatile DWORD seen = *(volatile DWORD*)(g_jtCtx + 8);  // an unknown reader on the sim thread, inside the window
    (void)seen;
    assert(C.jitTrapHits == 1 && C.jitTrapUnknown == 1);
    JitTrapClose();
    seen = *(volatile DWORD*)(g_jtCtx + 8);
    assert(C.jitTrapHits == 1 && !g_jitTrapOpen);
    CONTEXT cx = {};
    cx.Eip = TS3A(0x00E52550), cx.Esp = (DWORD)(uintptr_t)&cx;
    g_jitSimTid = 0;  // JitTrapHit reads as if from another thread
    JitTrapHit(&cx, 1);
    assert(C.jitTrapHits == 2 && C.jitTrapUnknown == 1);  // the stack walk, off the sim thread: in the census
    cx.Eip = 0x00401000;
    JitTrapHit(&cx, 1);
    assert(C.jitTrapHits == 3 && C.jitTrapUnknown == 2);  // not in the census: the gate
    g_jitTrapArmState = 0x1234;
    jtCur = 0x1234, JitTrapHit(&cx, 1);
    assert(C.jitTrapHits == 4 && C.jitTrapUnknown == 3);  // the armed state runs: still the gate
    jtCur = 0x5678, JitTrapHit(&cx, TS3A(0x011F4170));
    assert(C.jitTrapHits == 5 && C.jitTrapUnknown == 4);  // the globals always count
    JitTrapHit(&cx, 1);
    assert(C.jitTrapHits == 6 && C.jitTrapUnknown == 4);  // its run is over: a dead slot, no reader
    g_jitTrapArmState = 0;
    g_jitSimTid = GetCurrentThreadId();
    assert(JitTrapReader(TS3A(0x00E52550)) && !JitTrapReader(TS3A(0x00E4DC30)));
    assert(JitTrapProbePath("C:\\x\\frida-agent.dll") && !JitTrapProbePath("C:\\x\\ts3native.asi") && !JitTrapProbeThread(GetCurrentThread()));
    g_jtArmed = CreateEventW(NULL, TRUE, FALSE, NULL), g_jtGoRead = CreateEventW(NULL, TRUE, FALSE, NULL);
    HANDLE th = CreateThread(NULL, 0, JtReader, NULL, 0, NULL);
    assert(th);
    WaitForSingleObject(g_jtArmed, 10000);
    const DWORD addr[4] = {(DWORD)(uintptr_t)&g_jtWatched, 0, 0, 0};
    assert(JitTrapArmOthers(addr) >= 1);
    SetEvent(g_jtGoRead);
    WaitForSingleObject(th, 10000);
    assert(C.jitTrapHits == 7 && C.jitTrapUnknown == 5);
    CloseHandle(th), CloseHandle(g_jtArmed), CloseHandle(g_jtGoRead);

    RemoveVectoredExceptionHandler(veh);
    g_curState = jtCurSave;
    g_jitTrapVeh = NULL, g_jitTrap = false, g_jitTrapOther[0] = g_jitTrapOther[1] = 0, g_jitTrapLog = 0, g_jitTrapTick = 0;
    g_jitLazy = false, g_jitLeafOnly = true, g_jitLazyCellN = 0, g_jitGuardN = 0;
    g_jitForceJit = (const BYTE*)(uintptr_t)TS3A(0x00E5637D), g_jitProxyClassP = (const void* const*)(uintptr_t)TS3A(0x01220EC0);
}

// [MonoJit] CallExit (docs/537 §20 A): the pure Array icalls run inline and a null this or a bad dimension leaves at the call with
// nothing changed; CallExit=0 leaves them EA calls; a callee with a hot exit keeps a lazy body (the exit writes the frame); a guarded
// callvirt takes a boxed value-type receiver as box + 4 when the target takes a value-type this, as the box otherwise; a null
// receiver leaves at the callvirt with nothing changed
static void TestMonoJitCallExit() {
    Ts3nCounters& C = ts3native_counters;
    g_jitMode = 1, g_jitVerifyOn = 0, g_jitLazy = false, g_jitCallExit = true, g_jitPureIcalls = true;
    g_jtLen[0xEA] = 2, g_jtLen[0xEC] = 4;
    // arrays: [0] vtable -> klass (rank at +0x10), [1] bounds (NULL for a szarray), [2] max_length; bounds = {length, lower_bound} per dim
    static BYTE k1[0x20], k2[0x20];
    k1[0x10] = 1, k2[0x10] = 2;
    static DWORD vt1[1], vt2[1], arr1[3], arr2[3], bounds2[4] = {3, 1, 7, 2};
    vt1[0] = (DWORD)(uintptr_t)k1, vt2[0] = (DWORD)(uintptr_t)k2;
    arr1[0] = (DWORD)(uintptr_t)vt1, arr1[1] = 0, arr1[2] = 5;
    arr2[0] = (DWORD)(uintptr_t)vt2, arr2[1] = (DWORD)(uintptr_t)bounds2, arr2[2] = 21;
    static unsigned short nop[] = {JT_RET};
    static JtMeth mLen, mLow, mRank;
    const char* const pn[3] = {"GetLength", "GetLowerBound", "GetRank"};
    JtMeth* const pm[3] = {&mLen, &mLow, &mRank};
    static DWORD pItems[3];
    for (int i = 0; i < 3; i++) {
        JtInit(*pm[i], nop, 1, pn[i]);
        *(const char**)(pm[i]->klass + 0x34) = "Array", *(const char**)(pm[i]->klass + 0x38) = "System";
        *(WORD*)(pm[i]->mm + 2) = 0x1000;  // internal call
        pItems[i] = (DWORD)(uintptr_t)pm[i]->rm;
    }
    static unsigned short cLen[] = {0xEA, 0, JT_RET}, cLow[] = {0xEA, 1, JT_RET}, cRank[] = {0xEA, 2, JT_RET}, cRankV[] = {0xEC, 0, 0, 2, JT_RET};
    static JtMeth mcLen, mcLow, mcRank, mcRankV;
    JtMake(mcLen, cLen, 3, 0, pItems), JtMake(mcLow, cLow, 3, 0, pItems), JtMake(mcRank, cRank, 3, 0, pItems), JtMake(mcRankV, cRankV, 5, 0, pItems);
    const DWORD a1 = (DWORD)(uintptr_t)arr1, a2 = (DWORD)(uintptr_t)arr2;
    struct { unsigned short* c; DWORD arr, dim, want; } ok[] = {{cLen, a1, 0, 5}, {cLen, a2, 0, 3}, {cLen, a2, 1, 7}, {cLow, a1, 0, 0}, {cLow, a2, 0, 1}, {cLow, a2, 1, 2}};
    for (const auto& t : ok) {  // this, dim on the stack; the result in this's slot, its high dword = the word above the arguments
        JtFrame(); g_jtArena[0] = t.arr, g_jtArena[1] = 0x5A, g_jtArena[2] = t.dim, g_jtArena[5] = 0xC0DE;
        JtGo(t.c, 4);
        assert(g_jtEax == JT_RET && g_jtArena[0] == t.want && g_jtArena[1] == 0xC0DE && g_jtEsi == (DWORD)(uintptr_t)(g_jtArena + 2));
    }
    struct { unsigned short* c; DWORD arr, dim; } bad[] = {{cLen, a1, 1}, {cLen, a2, 2}, {cLen, a1, 0xFFFFFFFF}, {cLow, a2, 0xFFFFFFFF}, {cLen, 0, 0}, {cLow, 0, 0}};
    for (const auto& t : bad) {  // exit at the call, nothing changed
        JtFrame(); g_jtArena[0] = t.arr, g_jtArena[1] = 0x5A, g_jtArena[2] = t.dim, g_jtArena[5] = 0xC0DE;
        JtGo(t.c, 4);
        assert(g_jtEax == 0xEA && g_jtEdi == (DWORD)(uintptr_t)t.c && g_jtEsi == (DWORD)(uintptr_t)(g_jtArena + 4) && g_jtArena[0] == t.arr && g_jtArena[1] == 0x5A && g_jtArena[2] == t.dim);
    }
    for (unsigned short* c : {cRank, cRankV}) {
        JtFrame(); g_jtArena[0] = a2, g_jtArena[1] = 0x5A, g_jtArena[3] = 0xC0DE;
        JtGo(c, 2);
        assert(g_jtEax == JT_RET && g_jtArena[0] == 2 && g_jtArena[1] == 0xC0DE && g_jtEsi == (DWORD)(uintptr_t)(g_jtArena + 2));
        JtFrame(); g_jtArena[1] = 0x5A;
        JtGo(c, 2);
        assert(g_jtEax == (c == cRank ? 0xEAu : 0xECu) && g_jtEdi == (DWORD)(uintptr_t)c && g_jtEsi == (DWORD)(uintptr_t)(g_jtArena + 2) && !g_jtArena[0] && g_jtArena[1] == 0x5A);
    }
    g_jitCallExit = g_jitPureIcalls = false;  // CallExit=0: EA's call, before any change
    static unsigned short cLen0[] = {0xEA, 0, JT_RET};
    static JtMeth mcLen0;
    JtMake(mcLen0, cLen0, 3, 0, pItems);
    JtFrame(); g_jtArena[0] = a1, g_jtArena[2] = 0;
    JtGo(cLen0, 4);
    assert(g_jtEax == 0xEA && g_jtEdi == (DWORD)(uintptr_t)cLen0 && g_jtArena[0] == a1);

    // lazy: a callee with a hot exit gets a lazy body only with CallExit; called lazily, the exit writes the frame
    g_jitLazy = true, g_jitLeafOnly = true;
    g_jitForceJit = &g_jtForceByte, g_jitProxyClassP = (const void* const*)&g_jtProxy;
    static unsigned short h0[] = {JT_MYSTERY, JT_RET}, h1[] = {JT_MYSTERY, JT_RET};
    static JtMeth mH0, mH1;
    LONG bodies0 = C.jitLazyBodies;
    JtMake(mH0, h0, 2, 0);
    assert(C.jitLazyBodies == bodies0);
    g_jitCallExit = true;
    JtMake(mH1, h1, 2, 0);
    assert(C.jitLazyBodies == bodies0 + 1);
    static DWORD hItems[1];
    hItems[0] = (DWORD)(uintptr_t)mH1.rm;
    static unsigned short fh[] = {JT_LDC2, 0xEA, 0, JT_ADD, JT_RET};
    static JtMeth mFh;
    JtMake(mFh, fh, 5, 0, hItems);
    assert(C.jitLazyBodies == bodies0 + 1);  // DirectLeaf: a caller still gets no lazy body of its own
    const LONG mats0 = C.jitLazyMats, calls0 = C.jitLazyCalls;
    JtLazyFrame();
    JtLazyGo(fh);
    assert(g_jtEax == JT_MYSTERY && g_jtEdi == (DWORD)(uintptr_t)h1 && g_jtL[-1] == g_jtEdi && C.jitLazyMats == mats0 + 1 && C.jitLazyCalls == calls0 + 1);

    // guarded callvirt, boxed value-type receiver: box = {vtable, 77}; the class has the value-type bit
    g_jitLeafOnly = false;
    static BYTE kb[0x20];
    kb[0x14] = 8;
    static DWORD vtb[1], box[2];
    vtb[0] = (DWORD)(uintptr_t)kb, box[0] = (DWORD)(uintptr_t)vtb, box[1] = 77;
    static unsigned short tv[] = {JT_LDTHIS, JT_LDIND, JT_RET}, ti[] = {JT_LDTHIS, JT_LDIND, JT_RET}, td[] = {JT_RET};
    static JtMeth mTv, mTi, mDv;
    JtMake(mTv, tv, 3, 0x600);  // instance, value-type this: the override (box + 4)
    JtMake(mTi, ti, 3, 0x200);  // instance, reference this: an inherited ValueType method (the box)
    JtInit(mDv, td, 1);
    *(DWORD*)(mDv.rm + 0x44) = 0x300;
    *(unsigned short*)mDv.mm = 0x40;  // the declared method: virtual
    static DWORD vItems[3], iItems[3];
    vItems[0] = iItems[0] = (DWORD)(uintptr_t)kb, vItems[1] = (DWORD)(uintptr_t)mTv.rm, iItems[1] = (DWORD)(uintptr_t)mTi.rm;
    vItems[2] = iItems[2] = (DWORD)(uintptr_t)mDv.rm;
    static unsigned short kv[] = {JT_LDLOC, 0, 0xEC, 0, 0, 2, JT_RET}, ki[] = {JT_LDLOC, 0, 0xEC, 0, 0, 2, JT_RET}, kv0[] = {JT_LDLOC, 0, 0xEC, 0, 0, 2, JT_RET};
    static JtMeth mKv, mKi, mKv0;
    JtMake(mKv, kv, 7, 0, vItems);
    JitGuardSite* const gv = &g_jitGuard[g_jitGuardN - 1];  // the normal body's site (the lazy body's comes first)
    JtMake(mKi, ki, 7, 0, iItems);
    JitGuardSite* const gi = &g_jitGuard[g_jitGuardN - 1];
    const LONG calls1 = C.jitLazyCalls;
    JtLazyFrame(); g_jtLocals[0] = (DWORD)(uintptr_t)box;
    JtLazyGo(kv);
    assert(g_jtEax == JT_RET && g_jtArena[0] == 77 && C.jitLazyCalls == calls1 + 1 && gv->rm == mTv.rm && gv->unbox == 4);
    JtLazyFrame(); g_jtLocals[0] = (DWORD)(uintptr_t)box;
    JtLazyGo(ki);
    assert(g_jtEax == JT_RET && g_jtArena[0] == (DWORD)(uintptr_t)vtb && C.jitLazyCalls == calls1 + 2 && gi->rm == mTi.rm && !gi->unbox);
    JtLazyFrame(); g_jtLocals[0] = 0;  // null receiver: EA's callvirt, nothing changed
    JtLazyGo(kv);
    assert(g_jtEax == 0xEC && g_jtEdi == (DWORD)(uintptr_t)(kv + 2) && g_jtEsi == (DWORD)(uintptr_t)(g_jtArena + 2) && !g_jtArena[0] && C.jitLazyCalls == calls1 + 2);
    g_jitCallExit = false;  // CallExit=0: a boxed receiver takes EA's callvirt
    JtMake(mKv0, kv0, 7, 0, vItems);
    JtLazyFrame(); g_jtLocals[0] = (DWORD)(uintptr_t)box;
    JtLazyGo(kv0);
    assert(g_jtEax == 0xEC && g_jtEdi == (DWORD)(uintptr_t)(kv0 + 2) && g_jtArena[0] == (DWORD)(uintptr_t)box && C.jitLazyCalls == calls1 + 2);

    g_jitLazy = false, g_jitLeafOnly = true, g_jitLazyCellN = 0, g_jitGuardN = 0;
    g_jitForceJit = (const BYTE*)(uintptr_t)TS3A(0x00E5637D), g_jitProxyClassP = (const void* const*)(uintptr_t)TS3A(0x01220EC0);
}

// [MonoJit] NewVt (docs/537 §20 B2b): newobj (0xF4) of a value-type class calls the ctor's lazy body with this = the vt cursor; the
// post leaves [args start] = cursor, high dword = the caller record's +0x24, cursor unchanged; every bail and a ctor exit stay EA's
static void TestMonoJitNewVt() {
    Ts3nCounters& C = ts3native_counters;
    g_jitMode = 1, g_jitVerifyOn = 0, g_jitLazy = true, g_jitLeafOnly = true, g_jitCallExit = false, g_jitNewVt = true;
    g_jitForceJit = &g_jtForceByte, g_jitProxyClassP = (const void* const*)&g_jtProxy;
    static DWORD arrCtor = 0xA77A;
    g_jitArrCtorP = &arrCtor;
    g_jtLen[0xF4] = 2;
    // classes: a value type with layout, an enum, the array-ctor class; all with the valuetype flag 8
    static BYTE kv[0xB0], ke[0xB0], ka[0xB0];
    for (BYTE* k : {kv, ke, ka}) k[0xA6] = 0x11, k[0x14] = 8 | 4, *(const char**)(k + 0x34) = "S", *(const char**)(k + 0x38) = "Ns";
    ke[0x14] |= 0x10, *(DWORD*)(ka + 0x24) = arrCtor;
    auto ctor = [](JtMeth& m, unsigned short* code, int words, BYTE* k, int params) {  // JtMake with the ctor's class and flags
        JtInit(m, code, words);
        *(BYTE**)(m.mm + 8) = k;
        *(DWORD*)(m.rm + 0x44) = 0x701;  // transformed, instance, value-type this, void
        m.rm[0x40] = (BYTE)params;
        *(WORD*)(m.rm + 0x38) = 256, *(WORD*)(m.rm + 0x34) = 64, *(WORD*)(m.rm + 0x36) = 8, *(WORD*)(m.rm + 0x3E) = 32;
        { const char* why = JitCompile(m.rm); if (why) printf("newvt ctor: %s\n", why); assert(!why); }
    };
    static unsigned short cb[4][5] = {{JT_LDTHIS, JT_LDC3, JT_STFLD, 4, JT_RET}};  // this->[4] = 3; one copy per method (a compile patches word 0)
    for (int i = 1; i < 4; i++) memcpy(cb[i], cb[0], sizeof(cb[0]));
    static JtMeth mC0, mC1, mCe, mCa;
    const LONG bodies0 = C.jitLazyBodies;
    ctor(mC0, cb[0], 5, kv, 0), ctor(mC1, cb[1], 5, kv, 1), ctor(mCe, cb[2], 5, ke, 0), ctor(mCa, cb[3], 5, ka, 0);
    assert(C.jitLazyBodies == bodies0 + 4);
    static DWORD items[4];
    items[0] = (DWORD)(uintptr_t)mC0.rm, items[1] = (DWORD)(uintptr_t)mC1.rm, items[2] = (DWORD)(uintptr_t)mCe.rm, items[3] = (DWORD)(uintptr_t)mCa.rm;
    static unsigned short n0[] = {0xF4, 0, JT_RET}, n1[] = {JT_LDC2, 0xF4, 1, JT_RET}, ne[] = {0xF4, 2, JT_RET}, na[] = {0xF4, 3, JT_RET};
    static JtMeth mN0, mN1, mNe, mNa;
    const LONG sites0 = C.jitLazySites;
    JtMake(mN0, n0, 3, 0, items), JtMake(mN1, n1, 4, 0, items), JtMake(mNe, ne, 3, 0, items), JtMake(mNa, na, 3, 0, items);
    assert(C.jitLazySites == sites0 + 2);  // the enum and the array-ctor class stay EA's newobj
    const DWORD vt = (DWORD)(uintptr_t)g_jtVt;
    auto stale = []() { *(DWORD*)(const_cast<u8*>(JtRec(0)) + 0x24) = 0xC0DE; };
    // no params: B_ARGS 0, the result at the old top; one param: it is consumed, the result in its slot
    const LONG calls0 = C.jitLazyCalls, mats0 = C.jitLazyMats;
    JtLazyFrame(); stale();
    JtLazyGo(n0);
    assert(g_jtEax == JT_RET && g_jtArena[0] == vt && g_jtArena[1] == 0xC0DE && g_jtVt[1] == 3 && g_jtL[-3] == vt && g_jtEsi == (DWORD)(uintptr_t)(g_jtArena + 2));
    assert(C.jitLazyCalls == calls0 + 1 && C.jitLazyMats == mats0 && g_jtChunk[0] == (DWORD)(uintptr_t)g_jtChunkWords && *(DWORD*)(g_jtState + 0x140) == 0);
    JtLazyFrame(); stale();
    JtLazyGo(n1);
    assert(g_jtEax == JT_RET && g_jtArena[0] == vt && g_jtArena[1] == 0xC0DE && g_jtVt[1] == 3 && g_jtEsi == (DWORD)(uintptr_t)(g_jtArena + 2) && C.jitLazyCalls == calls0 + 2);
    for (unsigned short* x : {ne, na}) {  // EA's newobj, nothing changed
        JtLazyFrame();
        JtLazyGo(x);
        assert(g_jtEax == 0xF4 && g_jtEdi == (DWORD)(uintptr_t)x && g_jtEsi == (DWORD)(uintptr_t)g_jtArena && !g_jtVt[1] && C.jitLazyCalls == calls0 + 2);
    }
    JtLazyFrame();
    g_jtL[-0x30 / 4] = 1;  // no MonoInvocation in the next slot: EA's newobj before any write
    JtLazyGo(n0);
    assert(g_jtEax == 0xF4 && g_jtEdi == (DWORD)(uintptr_t)n0 && !g_jtArena[0] && !g_jtVt[1] && g_jtL[-3] == vt && C.jitLazyCalls == calls0 + 2);
    // a ctor that exits (CallExit keeps its lazy body): the mat writes EA's newobj state, so EA's frame return runs the newobj post
    g_jitCallExit = true;
    static unsigned short cx[] = {JT_MYSTERY, JT_RET};
    static JtMeth mCx, mNx;
    ctor(mCx, cx, 2, kv, 0);
    static DWORD xItems[1];
    xItems[0] = (DWORD)(uintptr_t)mCx.rm;
    static unsigned short nx[] = {0xF4, 0, JT_RET};
    JtMake(mNx, nx, 3, 0, xItems);
    const LONG mats1 = C.jitLazyMats;
    JtLazyFrame();
    JtLazyGo(nx);
    assert(g_jtEax == JT_MYSTERY && g_jtEdi == (DWORD)(uintptr_t)cx && C.jitLazyMats == mats1 + 1 && *(DWORD*)(g_jtState + 0x140) == 1);
    const DWORD* r0 = (const DWORD*)JtRec(0);
    assert(r0[0x18 / 4] == (DWORD)(uintptr_t)kv && r0[0x20 / 4] == vt && *(const DWORD*)(JtRec(1) + 0x14) == 1 && r0[1] == (DWORD)(uintptr_t)(nx + 2));
    assert(*(DWORD*)(g_jtInvs[1] + 0xC) == vt && !*(DWORD*)(g_jtInvs[1] + 0x14));  // this = the cursor, args 0
    // NewVt=0: the site is EA's newobj
    g_jitNewVt = false;
    static unsigned short noff[] = {0xF4, 0, JT_RET};
    static JtMeth mNoff;
    JtMake(mNoff, noff, 3, 0, items);
    JtLazyFrame();
    JtLazyGo(noff);
    assert(g_jtEax == 0xF4 && g_jtEdi == (DWORD)(uintptr_t)noff && !g_jtArena[0]);

    g_jtLen[0xF4] = 1;
    g_jitLazy = false, g_jitLeafOnly = true, g_jitCallExit = false, g_jitLazyCellN = 0, g_jitGuardN = 0;
    g_jitArrCtorP = (const DWORD*)(uintptr_t)TS3A(0x01220E8C);
    g_jitForceJit = (const BYTE*)(uintptr_t)TS3A(0x00E5637D), g_jitProxyClassP = (const void* const*)(uintptr_t)TS3A(0x01220EC0);
}

// [MonoJit] Count (docs/data/monojit-exits-handoff.md C0): a bail per reason, exit sites, a lazy-body mat, an enum newobj's exits and
// calls show in the report in the host's formats; Count=0 emits fewer bytes for the same method
static char g_jtcLines[64][640];
static int g_jtcN;
static void JtcSink(const char* l) { if (g_jtcN < 64) strcpy_s(g_jtcLines[g_jtcN++], l); }
static const char* JtcFind(const char* a, const char* b = "") {
    for (int i = 0; i < g_jtcN; i++) if (strstr(g_jtcLines[i], a) && strstr(g_jtcLines[i], b)) return g_jtcLines[i];
    return NULL;
}
static void TestMonoJitCount() {
    g_jitMode = 1, g_jitVerifyOn = 0, g_jitLazy = true, g_jitLeafOnly = true, g_jitCallExit = false, g_jitNewVt = false;
    g_jitForceJit = &g_jtForceByte, g_jitProxyClassP = (const void* const*)&g_jtProxy;
    g_jtLen[0xEA] = 2, g_jtLen[0xF4] = 2;
    memset(g_jitLazyCell, 0, sizeof(JitLazyCell) * JIT_CELL_N);  // earlier tests reset the count, not the cells: a reused cell keeps an old entry
    // Count=0 vs 1: the same method, more bytes with the counters
    static unsigned short z0[] = {JT_LDC2, JT_MYSTERY, JT_RET}, z1[] = {JT_LDC2, JT_MYSTERY, JT_RET};
    static JtMeth mZ0, mZ1;
    size_t u0 = g_jitCodeUsed;
    JtMake(mZ0, z0, 3, 0);
    const size_t off = g_jitCodeUsed - u0;
    assert(JcnOn());
    u0 = g_jitCodeUsed;
    JtMake(mZ1, z1, 3, 0);
    assert(g_jitCodeUsed - u0 > off);
    // a callee with no lazy body, called twice: the bail and the exit at the call count 2
    static unsigned short g[] = {JT_MYSTERY, JT_RET}, f[] = {0xEA, 0, JT_RET};
    static JtMeth mG, mF;
    JtMake(mG, g, 2, 0);
    static DWORD gItems[1];
    gItems[0] = (DWORD)(uintptr_t)mG.rm;
    JtMake(mF, f, 3, 0, gItems);
    const int fm = g_jitMethN - 1;
    for (int i = 0; i < 2; i++) { JtLazyFrame(); JtLazyGo(f); assert(g_jtEax == 0xEA); }
    // ForceJIT active: a site with a body bails with that reason
    static unsigned short e[] = {JT_LDC3, JT_RET}, h[] = {JT_LDC2, 0xEA, 0, JT_ADD, JT_RET};
    static JtMeth mE, mH;
    JtMake(mE, e, 2, 0);
    static DWORD eItems[1];
    eItems[0] = (DWORD)(uintptr_t)mE.rm;
    JtMake(mH, h, 5, 0, eItems);
    JtLazyFrame();
    g_jtForceByte = 0x74;
    JtLazyGo(h);
    g_jtForceByte = 0x75;
    assert(g_jtEax == 0xEA);
    // NewVt: a ctor that exits (CallExit keeps its lazy body) mats once at word 0; an enum newobj stays EA's (an exit, vt 0)
    g_jitCallExit = true, g_jitNewVt = true;
    static DWORD arrCtor = 0xA77A;
    g_jitArrCtorP = &arrCtor;
    static BYTE kv[0xB0], ke[0xB0];
    for (BYTE* k : {kv, ke}) k[0xA6] = 0x11, k[0x14] = 8 | 4, *(const char**)(k + 0x34) = "S", *(const char**)(k + 0x38) = "Ns";
    ke[0x14] |= 0x10;
    static unsigned short cx[] = {JT_MYSTERY, JT_RET}, ce[] = {JT_LDTHIS, JT_LDC3, JT_STFLD, 4, JT_RET};
    static JtMeth mCx, mCe;
    JtMeth* const cm[2] = {&mCx, &mCe};
    unsigned short* const cc[2] = {cx, ce};
    const int cw[2] = {2, 5};
    BYTE* const ck[2] = {kv, ke};
    for (int i = 0; i < 2; i++) {  // JtMake with the ctor's class and flags (as TestMonoJitNewVt)
        JtInit(*cm[i], cc[i], cw[i], "ctor");
        *(BYTE**)(cm[i]->mm + 8) = ck[i];
        *(DWORD*)(cm[i]->rm + 0x44) = 0x701;
        *(WORD*)(cm[i]->rm + 0x38) = 256, *(WORD*)(cm[i]->rm + 0x34) = 64, *(WORD*)(cm[i]->rm + 0x36) = 8, *(WORD*)(cm[i]->rm + 0x3E) = 32;
        assert(!JitCompile(cm[i]->rm));
    }
    static DWORD nItems[2];
    nItems[0] = (DWORD)(uintptr_t)mCx.rm, nItems[1] = (DWORD)(uintptr_t)mCe.rm;
    static unsigned short nx[] = {0xF4, 0, JT_RET}, ne[] = {0xF4, 1, JT_RET};
    static JtMeth mNx, mNe;
    JtMake(mNx, nx, 3, 0, nItems), JtMake(mNe, ne, 3, 0, nItems);
    const int nem = g_jitMethN - 1;
    JtLazyFrame();
    JtLazyGo(nx);
    assert(g_jtEax == JT_MYSTERY && g_jtEdi == (DWORD)(uintptr_t)cx);
    JtLazyFrame();
    JtLazyGo(ne);
    assert(g_jtEax == 0xF4 && g_jtEdi == (DWORD)(uintptr_t)ne);

    g_jtcN = 0;
    const int lines = JcnReportTo("test", JtcSink);
    assert(lines == g_jtcN - 2);
    char want[64];
    assert(JtcFind("jitcount: begin test #1") && JtcFind("jitcount: end test #1", " 0 left out"));
    assert(JtcFind("jitbail Ns.Cls.Meth 0 EA normal direct no lazy body=2"));
    assert(JtcFind("jitbail Ns.Cls.Meth 1 EA normal direct ForceJIT=1"));
    sprintf_s(want, "jitexit %d EA ", fm);
    assert(JtcFind(want, " op 2"));
    sprintf_s(want, "jitcalls %d 2 ", fm);
    assert(JtcFind(want));
    assert(JtcFind("jitmat 1 ", " 0 Ns.S.ctor"));
    sprintf_s(want, "jitnew %d 0 1 0 1C Ns.S.ctor", nem);
    assert(JtcFind(want));

    VirtualFree(g_jcn, 0, MEM_RELEASE), g_jcn = NULL;
    g_jtLen[0xF4] = 1;
    g_jitLazy = false, g_jitLeafOnly = true, g_jitCallExit = false, g_jitNewVt = false, g_jitLazyCellN = 0, g_jitGuardN = 0;
    g_jitArrCtorP = (const DWORD*)(uintptr_t)TS3A(0x01220E8C);
    g_jitForceJit = (const BYTE*)(uintptr_t)TS3A(0x00E5637D), g_jitProxyClassP = (const void* const*)(uintptr_t)TS3A(0x01220EC0);
}

#include "monojit-capture-test.h"
// [MonoJit] FastCopy (docs/537 §20 C3): EA's memmove path inline, every other input exits before any change
static void TestMonoJitFastCopy() {
    // arrays: [0] vtable -> klass (+8 element class, +0x10 rank, +0x5C element size), [1] bounds, [2] max_length, data at +0x10
    static BYTE kI[0x60], kI2[0x60], kR[0x60], elI[0x20], elR[0x20], elJ[0x20];
    *(DWORD*)(elI + 0x14) = 8, *(DWORD*)(elJ + 0x14) = 8, *(DWORD*)(elR + 0x14) = 8 | 1u << 23;  // value types; elR has references
    const auto klass = [](BYTE* k, BYTE* el, int rank) { *(BYTE**)(k + 8) = el, k[0x10] = (BYTE)rank, *(int*)(k + 0x5C) = 4; };
    klass(kI, elI, 1), klass(kI2, elI, 2), klass(kR, elR, 1);
    static BYTE kJ[0x60]; klass(kJ, elJ, 1);
    static DWORD vI[1], vI2[1], vR[1], vJ[1], a[12], b[12], r2[12], ar[12], aj[12];
    vI[0] = (DWORD)(uintptr_t)kI, vI2[0] = (DWORD)(uintptr_t)kI2, vR[0] = (DWORD)(uintptr_t)kR, vJ[0] = (DWORD)(uintptr_t)kJ;
    const auto arr = [](DWORD* x, DWORD* vt) { x[0] = (DWORD)(uintptr_t)vt, x[1] = 0, x[2] = 8; for (int i = 0; i < 8; i++) x[4 + i] = 100 + i; };
    const auto U = [](DWORD* x) { return (u8*)x; };
    arr(a, vI), arr(b, vI), arr(r2, vI2), arr(ar, vR), arr(aj, vJ);
    assert(JitFastCopy(U(a), 1, U(b), 2, 3) == 1 && b[6] == 101 && b[7] == 102 && b[8] == 103 && b[5] == 101 && b[9] == 105);
    arr(a, vI);
    assert(JitFastCopy(U(a), 0, U(a), 1, 4) == 1 && a[4] == 100 && a[5] == 100 && a[8] == 103 && a[9] == 105);  // overlap, as memmove
    arr(a, vI), arr(b, vI);
    assert(JitFastCopy(U(a), 8, U(b), 8, 0) == 1 && b[4] == 100);  // empty copy at the end
    const LONG ex0 = g_jitFastCopyExits;
    assert(!JitFastCopy(NULL, 0, U(b), 0, 1) && !JitFastCopy(U(a), 0, NULL, 0, 1) && !JitFastCopy(U(a), -1, U(b), 0, 1) && !JitFastCopy(U(a), 0, U(b), -1, 1)
        && !JitFastCopy(U(a), 0, U(b), 0, -1) && !JitFastCopy(U(a), 0, U(b), 6, 3) && !JitFastCopy(U(a), 6, U(b), 0, 3) && !JitFastCopy(U(r2), 0, U(b), 0, 1)
        && !JitFastCopy(U(ar), 0, U(ar), 1, 1) && !JitFastCopy(U(aj), 0, U(b), 0, 1));
    b[1] = 1; assert(!JitFastCopy(U(a), 0, U(b), 0, 1)); b[1] = 0;  // bounds set: EA's
    assert(g_jitFastCopyExits - ex0 == 11 && b[4] == 100 && b[11] == 107 && ar[5] == 101);
    // the call site: five args in slots 0..4, result (1) in slot 0, its high dword = the word above the arguments
    g_jitMode = 1, g_jitVerifyOn = 0, g_jitLazy = false, g_jitCallExit = false, g_jitPureIcalls = false, g_jitFastCopy = true;
    g_jtLen[0xEA] = 2;
    static unsigned short nop[] = {JT_RET};
    static JtMeth mFc;
    JtInit(mFc, nop, 1, "FastCopy");
    *(const char**)(mFc.klass + 0x34) = "Array", *(const char**)(mFc.klass + 0x38) = "System";
    *(WORD*)(mFc.mm + 2) = 0x1000;  // internal call
    static DWORD fItems[1]; fItems[0] = (DWORD)(uintptr_t)mFc.rm;
    static unsigned short cFc[] = {0xEA, 0, JT_RET};
    static JtMeth mcFc;
    JtMake(mcFc, cFc, 3, 0, fItems);
    arr(a, vI), arr(b, vI);
    JtFrame(); g_jtArena[0] = (DWORD)(uintptr_t)a, g_jtArena[1] = 0x5A, g_jtArena[2] = 2, g_jtArena[4] = (DWORD)(uintptr_t)b, g_jtArena[6] = 0, g_jtArena[8] = 2, g_jtArena[11] = 0xC0DE;
    JtGo(cFc, 10);
    assert(g_jtEax == JT_RET && g_jtArena[0] == 1 && g_jtArena[1] == 0xC0DE && g_jtEsi == (DWORD)(uintptr_t)(g_jtArena + 2) && b[4] == 102 && b[5] == 103 && b[6] == 102);
    JtFrame(); g_jtArena[0] = (DWORD)(uintptr_t)r2, g_jtArena[1] = 0x5A, g_jtArena[2] = 0, g_jtArena[4] = (DWORD)(uintptr_t)b, g_jtArena[6] = 0, g_jtArena[8] = 1;
    JtGo(cFc, 10);  // rank mismatch: exit at the call, nothing changed
    assert(g_jtEax == 0xEA && g_jtEdi == (DWORD)(uintptr_t)cFc && g_jtEsi == (DWORD)(uintptr_t)(g_jtArena + 10) && g_jtArena[0] == (DWORD)(uintptr_t)r2 && g_jtArena[1] == 0x5A && b[4] == 102);
    g_jitFastCopy = false;
}

static void TestMonoJit() {
    Ts3nCounters& C = ts3native_counters;
    // default: nothing installed without the switch, and the switch alone does nothing on another exe
    assert(!ApplyMonoJit(L"Z:\\no-such-dir\\none.ini") && !C.jitOn && !g_ilpPost);
    wchar_t ini[MAX_PATH + 32];
    GetTempPathW(MAX_PATH, ini);
    wcscat_s(ini, L"monojit-test.ini");
    WritePrivateProfileStringW(L"MonoJit", L"Enabled", L"1", ini);
    assert(!ApplyMonoJit(ini) && !C.jitOn && !g_ilpPost);  // the selftest exe is not Steam 1.67
    WritePrivateProfileStringW(L"MonoJit", L"Mode", L"3", ini);
    assert(!ApplyMonoJit(ini));
    DeleteFileW(ini);

    for (DWORD i = 0; i < 0x1C1; i++) g_jtTable[i] = (DWORD)(uintptr_t)JitTestStop, g_jtLen[i] = 1;
    g_jtTable[JIT_OP] = (DWORD)(uintptr_t)JitEnter;
    static const struct { int op; const char* name; int len; } kOps[] = {{JT_LDC2, "ldc.i4.2", 1}, {JT_LDC3, "ldc.i4.3", 1}, {JT_ADD, "add.i4", 1}, {JT_STLOC, "stloc.i4", 2},
        {JT_LDLOC, "ldloc.i4", 2}, {JT_RET, "ret", 1}, {JT_BRS, "br.s", 2}, {JT_BRTRUE, "brtrue.i4.s", 2}, {JT_LDFLD, "ldfld.i4", 2}, {JT_STFLD, "stfld.i4", 2},
        {JT_LDELEM, "ldelem.i4", 1}, {JT_LDFLDR4, "ldfld.r4", 2}, {JT_LDLOCVT, "ldloc.vt", 4}, {JT_LDCS, "ldc.i4.s", 2}, {JT_MYSTERY, "mystery", 1}, {JT_LEAVES, "leave.s", 2}, {JT_SWITCH, "switch", 3},
        {JT_INITOBJ, "initobj", 3}, {JT_ISINST, "isinst", 2}, {JT_SHRUN8, "shr.un.i8", 1}, {JT_LDCR4, "ldc.r4", 3}, {JT_LDCR8, "ldc.r8", 5},
        {JT_LDLOCR4, "ldloc.r4", 2}, {JT_LDLOCR8, "ldloc.r8", 2}, {JT_STLOCR4, "stloc.r4", 2}, {JT_STLOCR8, "stloc.r8", 2}, {JT_CEQR8, "ceq.r8", 1}, {JT_BLTR8S, "blt.r8.s", 2},
        {JT_LDTHIS, "ldthis", 1}, {JT_LDIND, "ldind.i4", 1}, {JT_LDLEN, "ldlen", 1},
        {JT_ADDR8, "add.r8", 1}, {JT_CONVU4R8, "conv.u4.r8", 1}, {JT_CONVI8R8, "conv.i8.r8", 1}, {JT_CONVR4R8, "conv.r4.r8", 1}, {JT_LDINDR8, "ldind.r8", 1}, {JT_STINDR4, "stind.r4", 1},
        {JT_LDSTR, "ldstr", 2}, {JT_LDTOKEN, "ldtoken", 2}, {JT_CASTCLASS, "castclass", 2}};
    for (const auto& o : kOps) g_jtNames[o.op] = o.name, g_jtLen[o.op] = (BYTE)o.len;
    g_jitTableP = g_jtTable, g_jitOpNames = g_jtNames, g_jitOpLen = g_jtLen, g_jitMemcpy = (DWORD)(uintptr_t)JtMemcpy;
    g_jitMemset = (DWORD)(uintptr_t)JtMemset, g_jitAullshr = (DWORD)(uintptr_t)JtAullshr, g_jitIfaceOff = (DWORD)(uintptr_t)JtIfaceOff;
    static DWORD noPending;  // nothing to sweep: the game's GC globals do not exist here
    g_jvPending = &noPending;
    g_jitAddRoots = JtNoRoots, GcBase = (GcBaseFn)JtHeapBase;
    {   // a free cell: a link into the heap or 0; a live object's word 0 is a vtable outside it
        static DWORD vt;
        g_jtHeap[0] = (DWORD)(uintptr_t)(g_jtHeap + 2);
        assert(JitFreeCell((const u8*)g_jtHeap));
        g_jtHeap[0] = 0;
        assert(JitFreeCell((const u8*)g_jtHeap));
        g_jtHeap[0] = (DWORD)(uintptr_t)&vt;
        assert(!JitFreeCell((const u8*)g_jtHeap));
    }
    GcBase = (GcBaseFn)JtNoBase;
    {   // JitMarkCold: only the straight run into a throw; box does not end a run, a branch does (a run past one is the hot path)
        g_jtNames[0x190] = "ldstr", g_jtNames[0x191] = "throw", g_jtNames[0x192] = "box";
        const unsigned short ops[] = {JT_LDC2, JT_BRTRUE, 0x190, 0x192, 0x191, JT_LDC3, JT_RET};
        bool cold[7];
        JitMarkCold(7, ops, cold);
        assert(!cold[0] && !cold[1] && cold[2] && cold[3] && cold[4] && !cold[5] && !cold[6]);
        g_jtNames[0x190] = g_jtNames[0x191] = g_jtNames[0x192] = NULL;
    }
    assert(JitTemplates(255) == 18);  // every row above but "mystery" and leave.s
    memset(g_opInfo, 0, sizeof(g_opInfo));
    assert(JitTemplates(255 & ~1) == 17 && g_opInfo[JT_LDFLDR4].kind == K_EXIT);  // Groups: group 1 left out
    memset(g_opInfo, 0, sizeof(g_opInfo));
    assert(JitTemplates(511) == 29);  // Groups bit 256: the nine C5 rows above and two float compares
    assert(g_opInfo[JT_RET].kind == K_RET && g_opInfo[JT_MYSTERY].kind == K_EXIT);
    assert(JitAlloc());
    InitializeCriticalSection(&g_jitCs);
    g_jitPatched = true, g_jitMode = 1, g_jitSimTid = GetCurrentThreadId(), C.jitSwitch = 1;

    // arithmetic, a local, the exit at ret: edi = ip, [ebp-4] = ip, eax = the original opcode through the live table
    static unsigned short a[] = {JT_LDC2, JT_LDC3, JT_ADD, JT_STLOC, 8, JT_LDLOC, 8, JT_RET};
    const unsigned short a0[8] = {JT_LDC2, JT_LDC3, JT_ADD, JT_STLOC, 8, JT_LDLOC, 8, JT_RET};
    static JtMeth mA;
    JtCompile(mA, a, 8);
    assert(C.jitMethods == 1 && g_jitMeth[0].nativeOps == 5 && g_jitMeth[0].exitOps == 1);
    assert(a[0] == JIT_OP && a[3] == JIT_OP && a[4] == 8 && a[7] == JIT_OP);  // operands stay
    JtFrame();
    JtGo(a);
    assert(g_jtEax == JT_RET && g_jtEdi == (DWORD)(uintptr_t)(a + 7) && g_jtEbp[-1] == g_jtEdi && g_jtEsi == (DWORD)(uintptr_t)(g_jtArena + 2));
    assert(g_jtArena[0] == 5 && g_jtLocals[2] == 5 && C.jitCalls == 1 && C.jitExits == 1);

    // another thread, and the sim thread before its first tick: the original opcode's live handler, no compiled code
    JtFrame();
    g_jtEbpIn = (DWORD)(uintptr_t)g_jtEbp, g_jtSpIn = (DWORD)(uintptr_t)g_jtArena, g_jtIpIn = (DWORD)(uintptr_t)a;
    HANDLE th = CreateThread(NULL, 0, JtThread, NULL, 0, NULL);
    assert(th && WaitForSingleObject(th, 10000) == WAIT_OBJECT_0);
    CloseHandle(th);
    assert(g_jtEax == JT_LDC2 && g_jtEdi == (DWORD)(uintptr_t)a && g_jtEbp[-1] == g_jtEdi && C.jitOtherTid == 1 && C.jitCalls == 1 && !g_jtArena[0]);
    const DWORD simTid = g_jitSimTid;
    g_jitSimTid = 0;
    JtGo(a);
    assert(g_jtEax == JT_LDC2 && C.jitOtherTid == 2 && C.jitCalls == 1);
    g_jitSimTid = simTid;

    // a word without a template is an exit; leave.s keeps its word (EA's handler re-reads it), the rest of the method is compiled
    static unsigned short b[] = {JT_LDC2, JT_LEAVES, 2, JT_MYSTERY, JT_RET};
    static JtMeth mB;
    JtCompile(mB, b, 5);
    assert(b[0] == JIT_OP && b[1] == JT_LEAVES && b[3] == JIT_OP);
    JtFrame();
    JtGo(b);
    assert(g_jtEax == JT_LEAVES && g_jtEdi == (DWORD)(uintptr_t)(b + 1) && g_jtArena[0] == 2);
    JtGo(b + 3);
    assert(g_jtEax == JT_MYSTERY && g_jtEdi == (DWORD)(uintptr_t)(b + 3));

    // branches: taken and not
    static unsigned short c0[] = {JT_LDCS, 0, JT_BRTRUE, 4, JT_LDC2, JT_RET, JT_LDC3, JT_RET}, c1[] = {JT_LDCS, 7, JT_BRTRUE, 4, JT_LDC2, JT_RET, JT_LDC3, JT_RET};
    static JtMeth mC0, mC1;
    JtCompile(mC0, c0, 8), JtCompile(mC1, c1, 8);
    JtFrame();
    JtGo(c0);
    assert(g_jtEdi == (DWORD)(uintptr_t)(c0 + 5) && g_jtArena[0] == 2);
    JtFrame();
    JtGo(c1);
    assert(g_jtEdi == (DWORD)(uintptr_t)(c1 + 7) && g_jtArena[0] == 3);

    // switch: a case, and out of range falling through
    static unsigned short s1[] = {JT_LDCS, 1, JT_SWITCH, 2, 0, 0, 0, 2, 0, JT_LDC2, JT_RET, JT_LDC3, JT_RET}, s5[] = {JT_LDCS, 5, JT_SWITCH, 2, 0, 0, 0, 2, 0, JT_LDC2, JT_RET, JT_LDC3, JT_RET};
    static JtMeth mS1, mS5;
    JtCompile(mS1, s1, 13), JtCompile(mS5, s5, 13);
    JtFrame();
    JtGo(s1);
    assert(g_jtEdi == (DWORD)(uintptr_t)(s1 + 12) && g_jtArena[0] == 3);
    JtFrame();
    JtGo(s5);
    assert(g_jtEdi == (DWORD)(uintptr_t)(s5 + 10) && g_jtArena[0] == 2);

    // fields: a read, a write, a null leaves the slot untouched at the instruction, r4 widens
    DWORD obj[3] = {0, 77, 0};
    static unsigned short f[] = {JT_LDFLD, 4, JT_RET}, w[] = {JT_STFLD, 8, JT_RET}, r[] = {JT_LDFLDR4, 0, JT_RET};
    static JtMeth mF, mW, mR;
    JtCompile(mF, f, 3), JtCompile(mW, w, 3), JtCompile(mR, r, 3);
    JtFrame();
    g_jtArena[0] = (DWORD)(uintptr_t)obj;
    JtGo(f, 2);
    assert(g_jtEax == JT_RET && g_jtArena[0] == 77);
    JtFrame();
    JtGo(f, 2);
    assert(g_jtEax == JT_LDFLD && g_jtEdi == (DWORD)(uintptr_t)f && g_jtEsi == (DWORD)(uintptr_t)(g_jtArena + 2) && !g_jtArena[0]);
    JtFrame();
    g_jtArena[0] = (DWORD)(uintptr_t)obj, g_jtArena[2] = 99;
    JtGo(w, 4);
    assert(g_jtEax == JT_RET && obj[2] == 99 && g_jtEsi == (DWORD)(uintptr_t)g_jtArena);
    const float half = 1.5f;
    JtFrame();
    g_jtArena[0] = (DWORD)(uintptr_t)&half;
    JtGo(r, 2);
    assert(g_jtEax == JT_RET && *(const double*)g_jtArena == 1.5);

    // ldelem: in bounds, out of bounds at the instruction (length at +8, data at +0x10)
    DWORD arr[6] = {0, 0, 2, 0, 10, 20};
    static unsigned short el[] = {JT_LDELEM, JT_RET};
    static JtMeth mE;
    JtCompile(mE, el, 2);
    JtFrame();
    g_jtArena[0] = (DWORD)(uintptr_t)arr, g_jtArena[2] = 1;
    JtGo(el, 4);
    assert(g_jtEax == JT_RET && g_jtArena[0] == 20 && g_jtEsi == (DWORD)(uintptr_t)(g_jtArena + 2));
    JtFrame();
    g_jtArena[0] = (DWORD)(uintptr_t)arr, g_jtArena[2] = 2;
    JtGo(el, 4);
    assert(g_jtEax == JT_LDELEM && g_jtEdi == (DWORD)(uintptr_t)el && g_jtEsi == (DWORD)(uintptr_t)(g_jtArena + 4));

    // ldloc.vt: the slot gets the cursor, the bytes go there through the memcpy thunk, the cursor moves by the aligned size
    static unsigned short v[] = {JT_LDLOCVT, 4, 12, 0, JT_RET};
    static JtMeth mV;
    JtCompile(mV, v, 5);
    JtFrame();
    g_jtLocals[1] = 11, g_jtLocals[2] = 22, g_jtLocals[3] = 33;
    JtGo(v);
    assert(g_jtEax == JT_RET && g_jtVt[0] == 11 && g_jtVt[2] == 33 && !g_jtVt[3] && g_jtArena[0] == (DWORD)(uintptr_t)g_jtVt);
    assert(g_jtEbp[-3] == (DWORD)(uintptr_t)(g_jtVt + 4));

    // round 10 C5. initobj: inline dwords with the word past them kept, the memset of a size not a multiple of 4, a null at the instruction
    static unsigned short io[] = {JT_INITOBJ, 8, 0, JT_RET}, iom[] = {JT_INITOBJ, 5, 0, JT_RET};
    static JtMeth mIo, mIom;
    JtCompile(mIo, io, 4), JtCompile(mIom, iom, 4);
    DWORD ioD[3] = {1, 2, 3};
    JtFrame();
    g_jtArena[0] = (DWORD)(uintptr_t)ioD;
    JtGo(io, 2);
    assert(g_jtEax == JT_RET && !ioD[0] && !ioD[1] && ioD[2] == 3 && g_jtEsi == (DWORD)(uintptr_t)g_jtArena);
    JtFrame();
    JtGo(io, 2);
    assert(g_jtEax == JT_INITOBJ && g_jtEdi == (DWORD)(uintptr_t)io && g_jtEsi == (DWORD)(uintptr_t)(g_jtArena + 2));
    BYTE ioB[8];
    memset(ioB, 0xAB, 8);
    JtFrame();
    g_jtArena[0] = (DWORD)(uintptr_t)ioB;
    JtGo(iom, 2);
    assert(g_jtEax == JT_RET && !ioB[4] && ioB[5] == 0xAB);

    // isinst: a class by the supertype table (hit, other class at the depth, object shallower than the class), an interface (hit,
    // miss through the offset call, miss on the max-iid), a null stays, a class not yet inited leaves the site to EA's handler
    static BYTE kP[0x70], kC[0x70], kU[0x70], kI[0x70], kCold[0x70], kOther[0x70];
    static const BYTE* superC[2] = {kP, kC}, * superU[2] = {kP, kU}, * superP[1] = {kP};
    const auto klass = [](BYTE* k, int depth, const BYTE** sup, int iid, bool iface) {
        k[0x14] = 1; *(WORD*)(k + 0x40) = (WORD)depth; *(const BYTE***)(k + 0x3C) = sup; *(WORD*)(k + 0x44) = (WORD)iid; k[0x60] = iface ? 0x20 : 0;
    };
    klass(kP, 1, superP, 0, false), klass(kC, 2, superC, 0, false), klass(kU, 2, superU, 0, false), klass(kI, 0, NULL, 3, true), klass(kOther, 0, NULL, 2, true), klass(kCold, 1, superP, 0, false);
    kC[0x20] = 3;  // implements iid 3
    kCold[0x14] = 0;
    static DWORD vtC[8], vtP[8], itemsIs[6];
    vtC[0] = (DWORD)(uintptr_t)kC, vtP[0] = (DWORD)(uintptr_t)kP, *(WORD*)((BYTE*)vtC + 0x14) = 3;
    itemsIs[0] = (DWORD)(uintptr_t)kP, itemsIs[1] = (DWORD)(uintptr_t)kC, itemsIs[2] = (DWORD)(uintptr_t)kU, itemsIs[3] = (DWORD)(uintptr_t)kI, itemsIs[4] = (DWORD)(uintptr_t)kOther, itemsIs[5] = (DWORD)(uintptr_t)kCold;
    static unsigned short is[6][3] = {{JT_ISINST, 0, JT_RET}, {JT_ISINST, 1, JT_RET}, {JT_ISINST, 2, JT_RET}, {JT_ISINST, 3, JT_RET}, {JT_ISINST, 4, JT_RET}, {JT_ISINST, 5, JT_RET}};
    static JtMeth mIs[6];
    for (int i = 0; i < 6; i++) { JtInit(mIs[i], is[i], 3); *(DWORD**)(mIs[i].rm + 0x14) = itemsIs; assert(JitCompile(mIs[i].rm) == NULL); }
    DWORD oC[2] = {(DWORD)(uintptr_t)vtC, 0}, oP[2] = {(DWORD)(uintptr_t)vtP, 0};
    const auto isinst = [&](int k, DWORD* o) { JtFrame(); g_jtArena[0] = (DWORD)(uintptr_t)o; JtGo(is[k], 2); return g_jtArena[0]; };
    assert(isinst(0, oC) == (DWORD)(uintptr_t)oC && g_jtEax == JT_RET && isinst(1, oC) == (DWORD)(uintptr_t)oC);
    assert(!isinst(2, oC) && g_jtEax == JT_RET && !isinst(1, oP) && g_jtEax == JT_RET);
    assert(isinst(3, oC) == (DWORD)(uintptr_t)oC && !isinst(3, oP) && !isinst(4, oC) && g_jtEax == JT_RET);
    assert(!isinst(0, NULL) && g_jtEax == JT_RET);
    assert(isinst(5, oC) == (DWORD)(uintptr_t)oC && g_jtEax == JT_ISINST && g_jtEdi == (DWORD)(uintptr_t)is[5]);

    // the two direct internal calls go by name and the internal-call flag only
    static BYTE imm[0x20], ikl[0x40], irm[0x48];
    static DWORD iit[1];
    static unsigned short icall[2] = {0xEA, 0};
    *(BYTE**)(irm + 0x14) = (BYTE*)iit, iit[0] = (DWORD)(uintptr_t)irm, *(BYTE**)(irm + 8) = imm;
    *(WORD*)(imm + 2) = 0x1000, *(BYTE**)(imm + 8) = ikl, *(const char**)(imm + 0x18) = "internal_from_handle", *(const char**)(ikl + 0x34) = "Type", *(const char**)(ikl + 0x38) = "System";
    assert(JitIcallFn(icall, irm) == TS3A(0x00E7AB60));
    *(const char**)(imm + 0x18) = "get_value", *(const char**)(ikl + 0x34) = "Enum";
    assert(JitIcallFn(icall, irm) == TS3A(0x00E7D520));
    *(const char**)(ikl + 0x38) = "Other";
    assert(!JitIcallFn(icall, irm));
    *(const char**)(ikl + 0x38) = "System", *(WORD*)(imm + 2) = 0;
    assert(!JitIcallFn(icall, irm));

    // shr.un.i8 through the CRT helper's registers; the float moves: r4 widens on the way in, narrows on the way out, r8 stays
    static unsigned short sh[] = {JT_SHRUN8, JT_RET};
    static JtMeth mSh;
    JtCompile(mSh, sh, 2);
    JtFrame();
    *(unsigned long long*)g_jtArena = 0x0000000180000000ULL, g_jtArena[2] = 4;
    JtGo(sh, 4);
    assert(g_jtEax == JT_RET && *(unsigned long long*)g_jtArena == 0x18000000ULL && g_jtEsi == (DWORD)(uintptr_t)(g_jtArena + 2));
    const float r4 = 1.5f;
    const double r8 = 2.25;
    static unsigned short fl[] = {JT_LDCR4, 0, 0, JT_LDCR8, 0, 0, 0, 0, JT_STLOCR8, 8, JT_STLOCR4, 0, JT_LDLOCR4, 0, JT_LDLOCR8, 8, JT_RET};
    memcpy(fl + 1, &r4, 4), memcpy(fl + 4, &r8, 8);
    static JtMeth mFl;
    JtCompile(mFl, fl, 17);
    JtFrame();
    JtGo(fl);
    assert(g_jtEax == JT_RET && *(const double*)(g_jtLocals + 2) == 2.25 && *(const float*)g_jtLocals == 1.5f);
    assert(*(const double*)g_jtArena == 1.5 && *(const double*)(g_jtArena + 2) == 2.25 && g_jtEsi == (DWORD)(uintptr_t)(g_jtArena + 4));

    // ceq.r8: NaN is unequal to itself; blt.r8.s: a NaN falls through, a < b branches; the eval stack ends empty past the result
    static unsigned short fc[] = {JT_CEQR8, JT_RET};
    static unsigned short fb[] = {JT_BLTR8S, 4, JT_LDC2, JT_RET, JT_LDC3, JT_RET};
    static JtMeth mFc, mFb;
    JtCompile(mFc, fc, 2), JtCompile(mFb, fb, 6);
    const unsigned long long fnanBits = 0x7FF8000000000000ULL; double fnan; memcpy(&fnan, &fnanBits, 8);
    const struct { double a, b; DWORD eq, lt; } kFc[] = {{1.0, 1.0, 1, 2}, {1.0, 2.0, 0, 3}, {fnan, fnan, 0, 2}, {fnan, 1.0, 0, 2}, {1.0, fnan, 0, 2}, {0.0, -0.0, 1, 2}};
    for (const auto& c : kFc) {
        JtFrame();
        *(double*)g_jtArena = c.a, *(double*)(g_jtArena + 2) = c.b;
        JtGo(fc, 4);
        assert(g_jtEax == JT_RET && g_jtArena[0] == c.eq && g_jtEsi == (DWORD)(uintptr_t)(g_jtArena + 2));
        JtFrame();
        *(double*)g_jtArena = c.a, *(double*)(g_jtArena + 2) = c.b;
        JtGo(fb, 4);
        assert(g_jtEax == JT_RET && g_jtArena[0] == c.lt && g_jtEsi == (DWORD)(uintptr_t)(g_jtArena + 2));
    }

    // the runtime byte: applied at the iteration hook, a full round trip of the words, the native code stays
    C.jitSwitch = 0;
    assert(a[0] == JIT_OP);  // only the tick acts
    JitTick();
    assert(!memcmp(a, a0, sizeof(a)) && b[0] == JT_LDC2 && b[1] == JT_LEAVES && b[3] == JT_MYSTERY && !g_jitPatched);
    const LONG calls0 = C.jitCalls;
    JtFrame();
    JtGo(a);
    assert(g_jtEax == JT_LDC2 && C.jitCalls == calls0);  // EA's opcode runs
    C.jitSwitch = 1;
    JitTick();
    assert(a[0] == JIT_OP && a[7] == JIT_OP && a[4] == 8 && b[1] == JT_LEAVES && g_jitPatched);

    // compiling with the arm off leaves the words alone until the arm comes on
    static unsigned short o[] = {JT_LDC2, JT_RET};
    static JtMeth mO;
    C.jitSwitch = 0;
    JitTick();
    JtCompile(mO, o, 2);
    assert(o[0] == JT_LDC2);
    C.jitSwitch = 1;
    JitTick();
    assert(o[0] == JIT_OP);

    // the refusals of the compiler
    static JtMeth mX;
    static unsigned short x[] = {JT_LDC2, JT_RET};
    JtInit(mX, x, 2);
    mX.rm[0x41] = 1;
    assert(JitCompile(mX.rm) != NULL && x[0] == JT_LDC2);  // clauses
    mX.rm[0x41] = 0;
    *(WORD*)(mX.rm + 0x3A) = JIT_MAX_WORDS;
    assert(JitCompile(mX.rm) != NULL);
    *(WORD*)(mX.rm + 0x3A) = 3;  // a branch target inside no instruction
    static unsigned short y[] = {JT_BRS, 1, JT_RET};
    *(unsigned short**)mX.rm = y;
    const int methN = g_jitMethN;
    assert(JitCompile(mX.rm) != NULL && g_jitMethN == methN && y[0] == JT_BRS);

    // the post-pass: the list, the hash, the gates; the header holds the body the game loaded
    BYTE il[8] = {1, 2, 3, 4, 5, 6, 7, 8}, sha[32];
    DWORD hdr[2] = {8, (DWORD)(uintptr_t)il};
    static JitRow row = {"Cls.Meth", 8, {0}};
    assert(IlpShaInit() && IlpSha256(il, 8, sha));
    memcpy(row.sha, sha, 32);
    g_jitRows = &row, g_jitRowN = 1;
    IlpHeaderFn header0 = g_ilpHeader;
    g_ilpHeader = [](BYTE*) -> BYTE* { return NULL; };
    BYTE force = 0x75;
    g_jitForceJit = &force;
    void* objectClass = (void*)0x1234, *domain = NULL, **ocP0 = g_jitObjectClassP;
    g_jitObjectClassP = &objectClass;
    static unsigned short p1[] = {JT_LDC2, JT_RET};
    static JtMeth mP, mN;
    JtInit(mP, p1, 2);
    JtInit(mN, p1, 2, "NotListed");
    static DWORD s_hdr[2];
    s_hdr[0] = hdr[0], s_hdr[1] = hdr[1];
    LONG refused = C.jitRefused;
    int compiled = g_jitMethN;
    JitPostPass(mN.rm, &domain);  // not listed: silent
    assert(C.jitRefused == refused && g_jitMethN == compiled && p1[0] == JT_LDC2);
    JitPostPass(mP.rm, &domain);  // no header
    assert(C.jitRefused == ++refused && p1[0] == JT_LDC2);
    g_ilpHeader = [](BYTE*) -> BYTE* { return (BYTE*)s_hdr; };
    row.sha[0] ^= 1;
    JitPostPass(mP.rm, &domain);  // hash miss
    assert(C.jitRefused == ++refused && g_jitMethN == compiled && p1[0] == JT_LDC2);
    row.sha[0] ^= 1;
    force = 0x90;
    JitPostPass(mP.rm, &domain);  // ForceJIT
    assert(C.jitRefused == ++refused && p1[0] == JT_LDC2);
    force = 0x75;
    g_jitSimTid = simTid + 1;
    JitPostPass(mP.rm, &domain);  // not the sim thread
    assert(C.jitRefused == ++refused && p1[0] == JT_LDC2);
    g_jitSimTid = simTid;
    mP.rm[0x41] = 1;
    JitPostPass(mP.rm, &domain);  // clauses
    assert(C.jitRefused == ++refused && p1[0] == JT_LDC2);
    mP.rm[0x41] = 0;
    JitPostPass(mP.rm, &domain);  // known
    assert(C.jitRefused == refused && g_jitMethN == compiled + 1 && p1[0] == JIT_OP && g_jitObjectClass == objectClass);
    JitPostPass(mP.rm, &domain);  // a repeat transform: compiled once
    assert(C.jitRefused == refused && g_jitMethN == compiled + 1);
    {  // R1 (docs/537 §20.0): each reason counted in its own slot; a full code region counts the body and its bound, past the name cap too
        const auto nOf = [](const char* w) { const int s = JitRefSlot(w); return s < g_jitRefWhyN ? g_jitRefWhy[s].n : 0L; };
        assert(nOf("no readable IL body") >= 1 && nOf("the IL hash is not a known one") >= 1 && nOf("MonoPatcher ForceJIT is on") >= 1 &&
               nOf("another thread") >= 1 && nOf("exception clauses") >= 1 && JitRefSlot("not a reason") == g_jitRefWhyN);
        static unsigned short pF[] = {JT_LDC2, JT_RET};
        static JtMeth mFull;
        JtInit(mFull, pF, 2);
        const size_t used0 = g_jitCodeUsed;
        const LONG fullN0 = C.jitFullN, fullB0 = C.jitFullBytes, fullR0 = nOf("code region full");
        g_jitCodeUsed = g_jitCodeBytes - 64;
        for (int i = 0; i < JIT_REF_NAMES + 3; i++) JitPostPass(mFull.rm, &domain);
        refused += JIT_REF_NAMES + 3;
        assert(C.jitRefused == refused && g_jitMethN == compiled + 1 && pF[0] == JT_LDC2);
        assert(C.jitFullN == fullN0 + JIT_REF_NAMES + 3 && C.jitFullBytes - fullB0 >= (JIT_REF_NAMES + 3) * 512 && nOf("code region full") == fullR0 + JIT_REF_NAMES + 3);
        g_jitCodeUsed = used0;
        JitPostPass(mFull.rm, &domain);  // room again: compiled
        assert(C.jitRefused == refused && g_jitMethN == compiled + 2 && pF[0] == JIT_OP);
        assert(JitCodeBytes(0) == 1024u << 10 && JitCodeBytes(4096) == 4096u << 10 && JitCodeBytes(1 << 20) == 16384u << 10);
        JitRefSummary("selftest");
    }
    g_jitUserN = 1, g_jitUser[0] = "Other.Thing";  // List= narrows
    assert(!JitListed("Ns.Cls.Meth") && JitListed("Ns.Other.Thing") == false);
    g_jitUser[0] = "Cls.Meth";
    assert(JitListed("Ns.Cls.Meth") && !JitListed("NsCls.Meth"));
    g_jitUserN = 0;
    // an [IlPatch] spec's patched form counts as known, under that method's name only
    assert(JitHashKnown("X.Y.Z", kIlpSpecs[0].name, 1, kIlpSpecs[0].builds[0].patched) && !JitHashKnown("X.Y.Z", "NotThatName", 1, kIlpSpecs[0].builds[0].patched));
    *(BYTE**)(mP.rm + 8) = (BYTE*)16;  // a fault in the compiler: counted, no more compiles, the session goes on
    JitPostPass(mP.rm, &domain);
    assert(g_jitDead && C.jitFaults == 1);
    g_jitDead = false, C.jitFaults = 0;
    g_ilpHeader = header0, g_jitObjectClassP = ocP0, g_jitRows = kJitRows, g_jitRowN = kJitRowN;

    // the crash report's name for an eip in compiled code
    char nm[160];
    assert(JitFaultName((DWORD)(uintptr_t)g_jitMeth[0].native0 + 1, nm, sizeof(nm)) && strstr(nm, "Ns.Cls.Meth +0x1") && C.jitFaults == 1);
    assert(!JitFaultName(1, nm, sizeof(nm)));
    C.jitFaults = 0;

    // verify: pass 1 compiled, restore, pass 2 interpreted, compare. Mismatch twice: counted, the method back on EA's words
    g_jitMode = 2, g_jitVerifyOn = 1, g_jitEvery = 1;
    g_jitV = (JitV*)calloc(JIT_VD, sizeof(JitV));
    assert(g_jitV);
    g_jtTable[JT_LDC2] = (DWORD)(uintptr_t)JitTestLdc;
    static unsigned short q[] = {JT_LDC2, JT_RET};
    static JtMeth mQ;
    JtCompile(mQ, q, 2);
    JitMeth& Q = g_jitMeth[g_jitMethN - 1];
    assert(Q.verifyOk);
    g_jtInterp = 2;
    JtFrame();
    JtGo(q, 8);
    assert(C.jitVerified == 1 && !C.jitMismatch && g_jtEax == JT_RET && g_jtEdi == (DWORD)(uintptr_t)(q + 1) && g_jtArena[8] == 2);
    g_jitEvery = 2;  // sampled: the next call runs compiled only, the one after is verified
    Q.tick = 0;
    JtFrame();
    JtGo(q, 8);
    assert(C.jitVerified == 1 && g_jtEax == JT_RET && g_jtArena[8] == 2);
    JtFrame();
    JtGo(q, 8);
    assert(C.jitVerified == 2);
    g_jitEvery = 1;
    g_jtInterp = 3;  // the interpreter disagrees: retried once, then the method is off, and the interpreter's result stays
    JtFrame();
    JtGo(q, 8);
    assert(C.jitMismatch == 1 && C.jitVerified == 2 && C.jitFirstBad == (LONG)(&Q - g_jitMeth) + 1 && Q.dead && q[0] == JT_LDC2 && q[1] == JT_RET && g_jtArena[8] == 3);
    const LONG calls1 = C.jitCalls;
    JtFrame();
    JtGo(q, 8);
    assert(g_jtEax == JT_RET && g_jtArena[8] == 3 && C.jitCalls == calls1);  // EA's words only
    C.jitSwitch = 0;
    JitTick();
    C.jitSwitch = 1;
    JitTick();
    assert(q[0] == JT_LDC2 && a[0] == JIT_OP);  // a dead method stays off across the runtime byte
    g_jtTable[JT_LDC2] = (DWORD)(uintptr_t)JitTestStop;

    TestMonoJitInline();
    TestMonoJitWide();
    TestMonoJitVtOps();
    TestMonoJitDirect();
    TestMonoJitCapture();
    TestMonoJitCallExit();
    TestMonoJitNewVt();
    TestMonoJitCount();
    TestMonoJitFastCopy();

    // the region: code pages are not writable once a method is done
    MEMORY_BASIC_INFORMATION mbi;
    assert(VirtualQuery(g_jitMeth[0].native0, &mbi, sizeof(mbi)) && mbi.Protect == PAGE_EXECUTE_READ);

    // leave nothing behind for the other tests
    JitSetPatched(false);
    DeleteCriticalSection(&g_jitCs);
    free(g_jitV);
    g_jitV = NULL, g_jitMode = 0, g_jitVerifyOn = 0, g_jitSimTid = 0, g_jitPatched = false, g_jitMethN = 0;
    memset(&C.jitDirect, 0, offsetof(Ts3nCounters, jitTrapUnknown) + 4 - offsetof(Ts3nCounters, jitDirect));
    memset(&C.jitOn, 0, offsetof(Ts3nCounters, jitCodeKB) + 4 - offsetof(Ts3nCounters, jitOn));
    printf("monojit: ok\n");
}
