// TestIlPatch ([IlPatch], ilpatch.cpp, docs/526). Included by selftest.cpp. Synthetic bodies through the decoder and the
// relocator (the cases of `tools/ilpatch-convert.ts --self-check`, same bytes expected), SHA-256, the header swap around a
// fake transform (restored after a return and after a fault), and the name path with a body no spec knows.

static DWORD g_ilpTestHdr[8], g_ilpTestClauses[6];
static struct { DWORD size; const BYTE* code; WORD ms; DWORD try0, calls; } g_ilpTestSeen;
static void* __cdecl IlpTestXform(BYTE* rm, void* ctx) {
    g_ilpTestSeen.size = g_ilpTestHdr[0]; g_ilpTestSeen.code = (const BYTE*)(uintptr_t)g_ilpTestHdr[1]; g_ilpTestSeen.ms = (WORD)g_ilpTestHdr[2];
    g_ilpTestSeen.try0 = g_ilpTestClauses[1]; g_ilpTestSeen.calls++;
    if (ctx) RaiseException(0xE0000001, 0, 0, NULL);
    return rm;
}
static BYTE* __cdecl IlpTestHeader(BYTE*) { return (BYTE*)g_ilpTestHdr; }

static void TestIlPatch() {
    static_assert(offsetof(Ts3nCounters, ilpOn) == 0x5C14 && offsetof(Ts3nCounters, ilpFaults) == 0x5C30 && sizeof(Ts3nCounters) == 5941 * 4,
                  "counters v907 layout after v906 (docs/registry.md)");
    assert(IlpShaInit());
    BYTE sha[32];
    assert(IlpSha256((const BYTE*)"abc", 3, sha) && sha[0] == 0xBA && sha[1] == 0x78 && sha[2] == 0x16 && sha[31] == 0xAD);

    // decoder: one-byte, two-byte, short and long branches, switch, the end checks
    {
        // 0 ldc.i4.1; 1 br.s +2 (-> 5); 3 ldc.i4.0; 4 ldc.i4.0; 5 ceq; 7 switch(1){-12 -> 4}; 16 br +0 (-> 21); 21 ret
        const BYTE c[] = {0x17, 0x2B, 0x02, 0x16, 0x16, 0xFE, 0x01, 0x45, 0x01, 0x00, 0x00, 0x00, 0xF4, 0xFF, 0xFF, 0xFF, 0x38, 0x00, 0x00, 0x00, 0x00, 0x2A};
        IlpDec d[16];
        const int n = IlpDecode(c, sizeof(c), d, 16);
        assert(n == 8 && d[1].kind == IK_BR8 && d[1].v == 5 && d[4].op == 0xFE01 && d[4].size == 2 && d[5].kind == IK_SWITCH && d[5].v == 1 &&
               d[5].size == 9 && d[6].kind == IK_BR32 && d[6].v == 21 && d[7].at == 21 && d[7].op == 0x2A);
        assert(IlpDecode(c, 2, d, 16) < 0);  // the branch operand is past the end
        const BYTE bad[] = {0xC0};
        assert(IlpDecode(bad, 1, d, 16) < 0);  // not an opcode
        assert(IlpDecode(c, sizeof(c), d, 4) < 0);  // over the cap
    }

    static const BYTE body[] = {0x17, 0x2B, 0x02, 0x16, 0x16, 0x2A};  // 0 ldc.i4.1; 1 br.s -> 5; 3 ldc.i4.0; 4 ldc.i4.0; 5 ret
    static const WORD exp16[] = {0x16}, exp1616[] = {0x16, 0x16}, exp17[] = {0x17};
    static const DWORD tok[] = {0x0A000123};
    const IlpBuild bld = {"t", 6, {0}, {0}, tok, 1};
    static const DWORD clTry35[6] = {0, 3, 2, 5, 1, 0x01000001};  // catch: try [3, 5), handler [5, 6)

    // A: 130 nops inserted at 3 on the fall-through path: the short branch over them becomes long and lands on ret; the clause moves
    static IlpIns nops[130];
    for (IlpIns& x : nops) { x.op = 0x00; x.kind = IK_NONE; x.pad = 0; x.v = 0; }
    const IlpEdit eA = {3, 0, 0, 1, 130, 0, 0, exp16, nops};
    const IlpSpec sA = {"tA", L"tA", "", "T", "m", "T::m()", 1, 1, &bld, &eA};
    IlpReloc rA;
    IlpApply(body, 6, 8, clTry35, 1, sA, bld, rA);
    assert(!rA.why && rA.size == 139 && rA.maxStack == 8);
    assert(rA.code[0] == 0x17 && rA.code[1] == 0x38 && *(const int*)(rA.code + 2) == 132 && rA.code[6] == 0 && rA.code[135] == 0 && rA.code[136] == 0x16 && rA.code[137] == 0x16 && rA.code[138] == 0x2A);
    assert(rA.clauses[0] == 0 && rA.clauses[1] == 136 && rA.clauses[2] == 2 && rA.clauses[3] == 138 && rA.clauses[4] == 1 && rA.clauses[5] == 0x01000001);

    // B: an incoming insert at ret with a token, a branch back to an original offset and a branch to one of our lines
    {
        static const IlpIns ins[4] = {{0x28, IK_TOK, 0, 0}, {0x39, IK_BR_ORIG, 0, 3}, {0x00, IK_NONE, 0, 0}, {0x38, IK_BR_OURS, 0, 2}};
        static const WORD exp2A[] = {0x2A};
        const IlpEdit e = {5, 0, 1, 1, 4, 2, 0, exp2A, ins};
        const IlpSpec s = {"tB", L"tB", "", "T", "m", "T::m()", 1, 1, &bld, &e};
        IlpReloc r;
        IlpApply(body, 6, 8, NULL, 0, s, bld, r);
        assert(!r.why && r.size == 25 && r.maxStack == 10);
        assert(r.code[1] == 0x38 && *(const int*)(r.code + 2) == 2);  // br -> 5 goes to our first line at 8
        assert(r.code[8] == 0x28 && *(const DWORD*)(r.code + 9) == 0x0A000123);
        assert(r.code[13] == 0x39 && *(const int*)(r.code + 14) == -12);  // brfalse -> original 3, now at 6
        assert(r.code[18] == 0x00 && r.code[19] == 0x38 && *(const int*)(r.code + 20) == -6 && r.code[24] == 0x2A);
        HeapFree(GetProcessHeap(), 0, r.code);
    }
    // D: a fallthrough insert whose own branch targets "at": it lands on the original instruction after our block (the core
    // patches' brtrue.s IL_007d), not on our first line; until 2026-11-02 it looped (smoke 24, b69)
    {
        static const IlpIns ins[3] = {{0x17, IK_NONE, 0, 0}, {0x3A, IK_BR_ORIG, 0, 3}, {0x00, IK_NONE, 0, 0}};
        const IlpEdit e = {3, 0, 0, 1, 3, 1, 0, exp16, ins};
        const IlpSpec s = {"tD", L"tD", "", "T", "m", "T::m()", 1, 1, &bld, &e};
        IlpReloc r;
        IlpApply(body, 6, 8, NULL, 0, s, bld, r);
        assert(!r.why && r.size == 16 && r.maxStack == 9);
        assert(r.code[1] == 0x38 && *(const int*)(r.code + 2) == 9);  // br -> 5, now 15
        assert(r.code[6] == 0x17 && r.code[7] == 0x3A && *(const int*)(r.code + 8) == 1 && r.code[12] == 0x00);  // brtrue -> original 3, now 13
        assert(r.code[13] == 0x16 && r.code[14] == 0x16 && r.code[15] == 0x2A);
        HeapFree(GetProcessHeap(), 0, r.code);
    }
    // C: a replace (ldc.i4.0 at 3 becomes ldc.i4.2); then a branch into a replaced range, a wrong expect, a cross-region branch: refused
    {
        static const IlpIns ins[1] = {{0x18, IK_NONE, 0, 0}};
        const IlpEdit e = {3, 1, 1, 1, 1, 0, 1, exp16, ins};
        const IlpSpec s = {"tC", L"tC", "", "T", "m", "T::m()", 1, 1, &bld, &e};
        IlpReloc r;
        IlpApply(body, 6, 8, NULL, 0, s, bld, r);
        assert(!r.why && r.size == 9 && r.code[1] == 0x38 && *(const int*)(r.code + 2) == 2 && r.code[6] == 0x18 && r.code[7] == 0x16 && r.code[8] == 0x2A);
        HeapFree(GetProcessHeap(), 0, r.code);
        static const BYTE body4[] = {0x17, 0x2B, 0x01, 0x16, 0x16, 0x2A};  // br.s -> 4
        const IlpEdit e2 = {3, 1, 1, 2, 1, 0, 2, exp1616, ins};
        const IlpSpec s2 = {"tC2", L"tC2", "", "T", "m", "T::m()", 1, 1, &bld, &e2};
        IlpApply(body4, 6, 8, NULL, 0, s2, bld, r);
        assert(r.why && !r.code && strstr(r.why, "replaced range"));
        const IlpEdit e3 = {3, 0, 0, 1, 1, 0, 0, exp17, ins};
        const IlpSpec s3 = {"tC3", L"tC3", "", "T", "m", "T::m()", 1, 1, &bld, &e3};
        IlpApply(body, 6, 8, NULL, 0, s3, bld, r);
        assert(r.why && strstr(r.why, "expected"));
        static const IlpIns ins4[1] = {{0x38, IK_BR_ORIG, 0, 3}};  // from 0 (outside the try) into the try at 3
        const IlpEdit e4 = {0, 0, 0, 1, 1, 0, 0, exp17, ins4};
        const IlpSpec s4 = {"tC4", L"tC4", "", "T", "m", "T::m()", 1, 1, &bld, &e4};
        IlpApply(body, 6, 8, clTry35, 1, s4, bld, r);
        assert(r.why && strstr(r.why, "exception region"));
        const IlpEdit e5 = {2, 0, 0, 0, 1, 0, 0, exp17, ins};  // 2 is inside the br.s operand
        const IlpSpec s5 = {"tC5", L"tC5", "", "T", "m", "T::m()", 1, 1, &bld, &e5};
        IlpApply(body, 6, 8, NULL, 0, s5, bld, r);
        assert(r.why && strstr(r.why, "instruction start"));
    }
    // D: a switch case is re-targeted across an insert; an insert at the end of the body
    {
        static const BYTE sw[] = {0x45, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x16, 0x2A};  // 0 switch(1){+1 -> 10}; 9 ldc.i4.0; 10 ret
        static const IlpIns three[3] = {{0x00, IK_NONE, 0, 0}, {0x00, IK_NONE, 0, 0}, {0x00, IK_NONE, 0, 0}};
        const IlpEdit e = {9, 0, 0, 1, 3, 0, 0, exp16, three};
        const IlpSpec s = {"tD", L"tD", "", "T", "m", "T::m()", 1, 1, &bld, &e};
        IlpReloc r;
        IlpApply(sw, sizeof(sw), 8, NULL, 0, s, bld, r);
        assert(!r.why && r.size == 14 && r.code[0] == 0x45 && *(const DWORD*)(r.code + 1) == 1 && *(const int*)(r.code + 5) == 4 && r.code[9] == 0 && r.code[12] == 0x16 && r.code[13] == 0x2A);
        HeapFree(GetProcessHeap(), 0, r.code);
        static const IlpIns ret1[1] = {{0x2A, IK_NONE, 0, 0}};
        static const WORD none[] = {0};
        const IlpEdit eEnd = {6, 0, 0, 0, 1, 0, 0, none, ret1};
        const IlpSpec sEnd = {"tE", L"tE", "", "T", "m", "T::m()", 1, 1, &bld, &eEnd};
        IlpApply(body, 6, 8, NULL, 0, sEnd, bld, r);
        assert(!r.why && r.size == 10 && r.code[9] == 0x2A && *(const int*)(r.code + 2) == 2);  // br -> 5 stays on the original ret at 8
        HeapFree(GetProcessHeap(), 0, r.code);
    }

    // the swap: the fake header carries body A during the fake transform only, also when the transform faults
    {
        const IlpXformFn next0 = g_ilpNext; const IlpHeaderFn header0 = g_ilpHeader;
        g_ilpNext = IlpTestXform; g_ilpHeader = IlpTestHeader;
        memcpy(g_ilpTestClauses, clTry35, sizeof(clTry35));
        g_ilpTestHdr[0] = 6; g_ilpTestHdr[1] = (DWORD)(uintptr_t)body; g_ilpTestHdr[2] = 0x8008; g_ilpTestHdr[3] = 1; g_ilpTestHdr[5] = (DWORD)(uintptr_t)g_ilpTestClauses;
        IlpPatched& P = g_ilpPatched[0];
        IlpPatched P0 = P;
        static DWORD cl01[12];
        memcpy(cl01, clTry35, 24); memcpy(cl01 + 6, rA.clauses, 24);
        BYTE rm[0x40] = {0};
        P.rm = rm; P.build = &bld; P.code = rA.code; P.size = rA.size; P.maxStack = rA.maxStack; P.nClauses = 1; P.clauses0 = cl01; P.clauses1 = cl01 + 6;
        const LONG applied0 = ts3native_counters.ilpApplied, reused0 = ts3native_counters.ilpReused;
        memset(&g_ilpTestSeen, 0, sizeof(g_ilpTestSeen));
        void* ret = IlpTransformPatched(0, rm, NULL, (BYTE*)g_ilpTestHdr, true);
        assert(ret == rm && g_ilpTestSeen.calls == 1 && g_ilpTestSeen.size == 139 && g_ilpTestSeen.code == rA.code && g_ilpTestSeen.ms == 0x8008 && g_ilpTestSeen.try0 == 136);
        assert(g_ilpTestHdr[0] == 6 && g_ilpTestHdr[1] == (DWORD)(uintptr_t)body && g_ilpTestHdr[2] == 0x8008 && g_ilpTestClauses[1] == 3 && g_ilpTestClauses[3] == 5);
        assert(ts3native_counters.ilpApplied == applied0 + 1);
        bool faulted = false;
        __try { IlpTransformPatched(0, rm, (void*)1, (BYTE*)g_ilpTestHdr, false); } __except (EXCEPTION_EXECUTE_HANDLER) { faulted = true; }
        assert(faulted && g_ilpTestSeen.calls == 2 && g_ilpTestHdr[0] == 6 && g_ilpTestHdr[1] == (DWORD)(uintptr_t)body && g_ilpTestClauses[1] == 3 && ts3native_counters.ilpReused == reused0);
        // the whole detour on a runtime method named as spec 2 whose body no build matches: hash skip, EA's transform runs. The
        // class is faked as Mono has it: a nested class carries its inner name and no namespace, the outer class is nested_in.
        BYTE mm[0x40] = {0}, klass[0x40] = {0}, outer[0x40] = {0};
        *(BYTE**)(rm + 8) = mm; *(BYTE**)(mm + 8) = klass;
        const char* plus = strrchr(kIlpSpecs[2].cls, '+');
        static char outerName[128]; static const char* emptyNs = "";
        if (plus) {
            strncpy_s(outerName, kIlpSpecs[2].cls, (size_t)(plus - kIlpSpecs[2].cls));
            *(const char**)(klass + 0x34) = plus + 1; *(const char**)(klass + 0x38) = emptyNs; *(BYTE**)(klass + 0x28) = outer;
            *(const char**)(outer + 0x34) = outerName; *(const char**)(outer + 0x38) = kIlpSpecs[2].ns;
        } else { *(const char**)(klass + 0x34) = kIlpSpecs[2].cls; *(const char**)(klass + 0x38) = kIlpSpecs[2].ns; }
        *(const char**)(mm + 0x18) = kIlpSpecs[2].name;
        assert(IlpClassIs(klass, kIlpSpecs[2].cls, kIlpSpecs[2].ns) && !IlpClassIs(klass, "Other", kIlpSpecs[2].ns) && !IlpClassIs(klass, kIlpSpecs[2].cls, "Other"));
        const bool armed0 = g_ilpArmed[2]; const LONG logged0 = g_ilpLogged[2], skip0 = ts3native_counters.ilpSkipHash;
        g_ilpArmed[2] = true; g_ilpLogged[2] = 0;
        g_ilpTestSeen.calls = 0;
        ret = IlpXform(rm, NULL);
        assert(ret == rm && g_ilpTestSeen.calls == 1 && g_ilpTestSeen.size == 6 && ts3native_counters.ilpSkipHash == skip0 + 1 && g_ilpLogged[2] == 1 && g_ilpArmed[2]);
        *(const char**)(mm + 0x18) = "Other";
        ret = IlpXform(rm, NULL);
        assert(ret == rm && g_ilpTestSeen.calls == 2 && ts3native_counters.ilpSkipHash == skip0 + 1);  // not a listed method: straight through
        g_ilpArmed[2] = armed0; g_ilpLogged[2] = logged0;
        P = P0;
        g_ilpNext = next0; g_ilpHeader = header0;
    }
    HeapFree(GetProcessHeap(), 0, rA.code);
}
