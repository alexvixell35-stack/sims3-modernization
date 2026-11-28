// TestJitNorm ([MonoJit] CoreModMatch, monojit-norm.h, docs/537 §34) and the row tool `selftest.exe --jitnorm <dll> [<header offset hex>...]`.
// Included by selftest.cpp after monojit-test.h.

static DWORD JnTbStr(BYTE* s, DWORD* n, const char* v) {
    const DWORD o = *n;
    const size_t l = strlen(v) + 1;
    memcpy(s + o, v, l);
    *n += (DWORD)l;
    return o;
}
// A one-module assembly in f[4096] whose Ns.C::Main is `call <callee>; call Lib.Util::Ext; ldstr "hi"; pop; ret`.
// swap = the same TypeRefs and MethodDefs in the other row order (other token numbers, as a core mod's re-emit). Returns the IL size.
static DWORD JnTestAsm(BYTE* f, bool swap, const char* callee, DWORD* codeOff) {
    memset(f, 0, 4096);
    f[0] = 'M', f[1] = 'Z', *(DWORD*)(f + 0x3C) = 0x80;
    *(DWORD*)(f + 0x80) = 0x4550, *(WORD*)(f + 0x84) = 0x14C, *(WORD*)(f + 0x86) = 1, *(WORD*)(f + 0x94) = 224, *(WORD*)(f + 0x98) = 0x10B;
    *(DWORD*)(f + 0x98 + 208) = 0x2000, *(DWORD*)(f + 0x98 + 212) = 0x48;  // CLI header
    BYTE* sec = f + 0x98 + 224;
    memcpy(sec, ".text", 5), *(DWORD*)(sec + 8) = 0x1000, *(DWORD*)(sec + 12) = 0x2000, *(DWORD*)(sec + 16) = 0xE00, *(DWORD*)(sec + 20) = 0x200;
    BYTE* cli = f + 0x200;
    *(DWORD*)cli = 0x48, *(DWORD*)(cli + 8) = 0x2040, *(DWORD*)(cli + 12) = 0x600;
    BYTE* r = f + 0x240;
    *(DWORD*)r = 0x424A5342, *(DWORD*)(r + 12) = 12, memcpy(r + 16, "v2.0.50727", 10), *(WORD*)(r + 30) = 4;
    BYTE* h = r + 32;
    const struct { DWORD off, sz; const char* nm; DWORD pad; } st[4] = {{0x100, 0x200, "#~", 4}, {0x300, 0x100, "#Strings", 12}, {0x400, 0x80, "#US", 4}, {0x480, 0x80, "#Blob", 8}};
    for (const auto& x : st) *(DWORD*)h = x.off, *(DWORD*)(h + 4) = x.sz, memcpy(h + 8, x.nm, strlen(x.nm)), h += 8 + x.pad;
    BYTE* s = r + 0x300;
    DWORD sn = 1;
    const WORD sMod = (WORD)JnTbStr(s, &sn, "<Module>"), sNs = (WORD)JnTbStr(s, &sn, "Ns"), sC = (WORD)JnTbStr(s, &sn, "C"), sMain = (WORD)JnTbStr(s, &sn, "Main");
    const WORD sCallee = (WORD)JnTbStr(s, &sn, callee), sSys = (WORD)JnTbStr(s, &sn, "System"), sObj = (WORD)JnTbStr(s, &sn, "Object");
    const WORD sLib = (WORD)JnTbStr(s, &sn, "Lib"), sUtil = (WORD)JnTbStr(s, &sn, "Util"), sExt = (WORD)JnTbStr(s, &sn, "Ext");
    BYTE* us = r + 0x400;
    us[1] = 5, us[2] = 'h', us[4] = 'i';  // "hi" at #US 1
    BYTE* bl = r + 0x480;
    bl[1] = 3, bl[2] = 0, bl[3] = 0, bl[4] = 1;  // void() at #Blob 1
    BYTE* t = r + 0x100;
    t[4] = 2, *(unsigned long long*)(t + 8) = 1ull << 1 | 1ull << 2 | 1ull << 6 | 1ull << 10;
    *(DWORD*)(t + 24) = 2, *(DWORD*)(t + 28) = 2, *(DWORD*)(t + 32) = 2, *(DWORD*)(t + 36) = 1;
    BYTE* p = t + 40;
    auto w = [&](WORD v) { *(WORD*)p = v, p += 2; };
    auto d = [&](DWORD v) { *(DWORD*)p = v, p += 4; };
    const WORD objRow = swap ? 2 : 1, utilRow = swap ? 1 : 2, mainRow = swap ? 2 : 1, calleeRow = swap ? 1 : 2;
    for (WORD i = 1; i <= 2; i++) w(4), w(i == objRow ? sObj : sUtil), w(i == objRow ? sSys : sLib);  // TypeRef (scope: Module 1)
    d(0), w(sMod), w(0), w(0), w(1), w(1);                                                         // TypeDef <Module>
    d(1), w(sC), w(sNs), w((WORD)(objRow << 2 | 1)), w(1), w(1);                                    // TypeDef Ns.C : System.Object
    for (WORD i = 1; i <= 2; i++) d(i == mainRow ? 0x2400 : 0), w(0), w(0x16), w(i == mainRow ? sMain : sCallee), w(1), w(1);  // MethodDef
    w((WORD)(utilRow << 3 | 1)), w(sExt), w(1);                                                    // MemberRef Lib.Util::Ext
    const BYTE il[] = {0x28, 0, 0, 0, 0x06, 0x28, 1, 0, 0, 0x0A, 0x72, 1, 0, 0, 0x70, 0x26, 0x2A};
    BYTE* c = f + 0x600;  // rva 0x2400: tiny header
    c[0] = (BYTE)(sizeof(il) << 2 | 2), memcpy(c + 1, il, sizeof(il)), c[2] = (BYTE)calleeRow;
    *codeOff = 0x601;
    return sizeof(il);
}

static void TestJitNorm() {
    assert(IlpShaInit());
    static BYTE a[4096], b[4096], c[4096];
    DWORD ao, bo, co;
    const DWORD an = JnTestAsm(a, false, "Callee", &ao), bn = JnTestAsm(b, true, "Callee", &bo), cn = JnTestAsm(c, false, "Other", &co);
    BYTE sa[32], sb[32], sc[32], xa[32], xb[32];
    assert(IlpSha256(a + ao, an, xa) && IlpSha256(b + bo, bn, xb) && memcmp(xa, xb, 32));  // the exact hash refuses the re-emit
    assert(JnNormSha(a, 4096, a + ao, an, sa) && JnNormSha(b, 4096, b + bo, bn, sb) && !memcmp(sa, sb, 32));  // the normalized one matches
    assert(JnNormSha(c, 4096, c + co, cn, sc) && memcmp(sa, sc, 32));  // another callee name: no match
    BYTE tmp[32];
    assert(!JnNormSha(a, 4096, b + bo, bn, tmp));            // a body outside the image's bytes
    assert(!JnNormSha(a, 0x300, a + ao, an, tmp));           // metadata cut off
    BYTE z[4096];
    memcpy(z, a, 4096), z[0x60C] = 0x99;                     // ldstr row 0x99: past #US
    assert(!JnNormSha(z, 4096, z + ao, an, tmp));
    memcpy(z, a, 4096), z[0x605] = 0x09;                     // the callee token in table 0x09 (InterfaceImpl, no name): no hash
    assert(!JnNormSha(z, 4096, z + ao, an, tmp));
    // JitNormKnown: MonoMethod +8 class -> MonoClass +0 image -> raw_data / raw_data_len; the switch and the name rule
    DWORD img[4] = {0, 0, (DWORD)(uintptr_t)b, 4096}, cls[1] = {(DWORD)(uintptr_t)img}, mm[8] = {0, 0, (DWORD)(uintptr_t)cls};
    JitRow row = {"C.Main", an, {}};
    memcpy(row.sha, sa, 32);
    const JitRow* r0 = g_jitNormRows;
    const int n0 = g_jitNormRowN;
    g_jitNormRows = &row, g_jitNormRowN = 1;
    g_jitCoreMod = false;
    assert(!JitNormKnown("Ns.C.Main", (const BYTE*)mm, b + bo, bn));  // off: nothing new
    g_jitCoreMod = true;
    assert(JitNormKnown("Ns.C.Main", (const BYTE*)mm, b + bo, bn) && !JitNormKnown("Ns.C.Other", (const BYTE*)mm, b + bo, bn) &&
           !JitNormKnown("Ns.C.Main", (const BYTE*)mm, b + bo, bn - 1));
    img[2] = (DWORD)(uintptr_t)a;  // the image's bytes do not hold this body
    assert(!JitNormKnown("Ns.C.Main", (const BYTE*)mm, b + bo, bn));
    g_jitCoreMod = false, g_jitNormRows = r0, g_jitNormRowN = n0;
    printf("jit norm: ok (token-shuffled body matches, other callee and foreign body do not)\n");
}

// `selftest.exe --jitnorm <dll> [<offset>...]`: per method header file offset (hex), "<offset> <IL size> <normalized sha256 | ->";
// no offsets = every MethodDef with a body ("<token> <offset> <size> <sha|->"). tools/jit-hot.ts --emit-native reads it.
static int JnCli(int argc, char** argv) {
    if (argc < 3 || !IlpShaInit()) { printf("usage: selftest.exe --jitnorm <dll> [<method header file offset hex>...]\n"); return 2; }
    FILE* fp = NULL;
    if (fopen_s(&fp, argv[2], "rb") || !fp) { printf("cannot open %s\n", argv[2]); return 2; }
    fseek(fp, 0, SEEK_END);
    const long n = ftell(fp);
    fseek(fp, 0, SEEK_SET);
    BYTE* b = (BYTE*)malloc(n > 0 ? n : 1);
    if (n <= 0 || !b || fread(b, 1, n, fp) != (size_t)n) { printf("cannot read %s\n", argv[2]); return 2; }
    fclose(fp);
    auto one = [&](DWORD at, char* out) {  // "<size> <sha|->"
        DWORD start, size;
        if (at >= (DWORD)n) { strcpy_s(out, 80, "0 -"); return; }
        if ((b[at] & 3) == 2) start = at + 1, size = b[at] >> 2;
        else if (at + 12 <= (DWORD)n) start = at + (*(const WORD*)(b + at) >> 12) * 4, size = *(const DWORD*)(b + at + 4);
        else { strcpy_s(out, 80, "0 -"); return; }
        BYTE sha[32];
        char hex[65] = "-";
        if (start <= (DWORD)n && size <= (DWORD)n - start && JnNormSha(b, (DWORD)n, b + start, size, sha)) IlpHex(sha, 32, hex);
        sprintf_s(out, 80, "%lu %s", size, hex);
    };
    char line[80];
    if (argc > 3) {
        for (int i = 3; i < argc; i++) one(strtoul(argv[i], NULL, 16), line), printf("%s %s\n", argv[i], line);
        return 0;
    }
    JnMd m;
    if (!JnOpen(m, b, (DWORD)n)) { printf("not an assembly this reader knows: %s\n", argv[2]); return 1; }
    for (DWORD r = 1; r <= m.rows[0x06]; r++) {
        const DWORD rva = JnCol(m, 0x06, r, 0), at = rva ? JnRvaOff(b, (DWORD)n, rva) : 0;
        if (!at) continue;
        one(at, line), printf("%08lX %lx %s\n", 0x06000000 | r, at, line);
    }
    return 0;
}
