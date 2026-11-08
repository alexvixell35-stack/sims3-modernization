// TestMonoJitCapture ([MonoJit] Capture=1, monojit-capture.h, docs/537 §17). Included by monojit-test.h. Fake metadata only: names, the
// vtable identity check, the sampling arithmetic, a fault dropping its sample, the file header. The walk over a real GC heap runs in the
// host on the corpus (node tools/jit-replay.ts --capture-corpus), the dummy for this code before the game.
struct JcFakeClass { u8 b[0xC0]; };
static void JcFakeName(JcFakeClass& k, const char* ns, const char* nm, u8 type, const void* data) {
    memset(&k, 0, sizeof(k));
    *(const char**)(k.b + 0x34) = nm, *(const char**)(k.b + 0x38) = ns;
    *(const void**)(k.b + 0xA0) = data, k.b[0xA0 + 6] = type;
}
// Direct=1 and Inline=1 (docs/537 §19): the compiler emits the sample call only while capture is on; a real lazy call and a real
// inlined site each record one sample tagged with its path, and the compiled result shows the stubs kept every register. A value-type
// this outside the frames and the GC heap becomes a blob.
static void TestMonoJitCaptureVia() {
    Ts3nCounters& C = ts3native_counters;
    char path[MAX_PATH], tmp[MAX_PATH];
    assert(GetTempPathA(MAX_PATH, tmp) && GetTempFileNameA(tmp, "jc", 0, path));
    wchar_t wpath[MAX_PATH];
    MultiByteToWideChar(CP_ACP, 0, path, -1, wpath, MAX_PATH);
    const GcBaseFn gb = GcBase;
    GcBase = (GcBaseFn)JtNoBase;
    const bool lazy0 = g_jitLazy, leaf0 = g_jitLeafOnly;
    g_jitMode = 1, g_jitVerifyOn = 0, g_jitLazy = true, g_jitLeafOnly = true, g_jitInline = 0;
    g_jitForceJit = &g_jtForceByte, g_jitProxyClassP = (const void* const*)&g_jtProxy;
    g_jtLen[0xEA] = 2, g_jtLen[0xEC] = 4;
    static unsigned short e0[] = {JT_LDC3, JT_RET};  // compiled with capture off: no call at its lazy entry
    static JtMeth mE0;
    JtMake(mE0, e0, 2, 0);
    assert(JitLazyCellFor(mE0.rm)->entry && *JitLazyCellFor(mE0.rm)->entry != 0xE8);
    assert(JcInit(wpath, 8, 8, 2, 16, 4096, 1024, 1000000, 1 << 20));
    JcState& g = *g_jc;

    static unsigned short e[] = {JT_LDC3, JT_RET};  // a leaf with a lazy body, called lazily by f
    static JtMeth mE, mF;
    JtMake(mE, e, 2, 0);
    assert(*JitLazyCellFor(mE.rm)->entry == 0xE8);
    static DWORD fItems[1];
    fItems[0] = (DWORD)(uintptr_t)mE.rm;
    static unsigned short f[] = {JT_LDC2, 0xEA, 0, JT_ADD, JT_RET};
    JtMake(mF, f, 5, 0, fItems);
    const LONG lc0 = C.jitLazyCalls;
    JtLazyFrame();
    JtLazyGo(f);
    assert(g_jtEax == JT_RET && g_jtArena[0] == 5 && C.jitLazyCalls == lc0 + 1);
    assert(g.st.samples == 2 && g.st.viaLazy == 1 && !g.st.viaInline && !g.st.faults);  // f at JitEnter, e in its lazy body

    g_jitLazy = false, g_jitInline = 1;  // an inlined getter: this.field
    static BYTE klassA[8];
    static DWORD vtA[1], obj[4];
    vtA[0] = (DWORD)(uintptr_t)klassA, obj[0] = (DWORD)(uintptr_t)vtA, obj[2] = 77;
    static unsigned short t1[] = {JT_LDTHIS, JT_LDFLD, 8, JT_RET}, f1[] = {0xEA, 0, JT_RET};
    static JtMeth m1, c1;
    JtMake(m1, t1, 4, 0x200);
    static DWORD i1[1];
    i1[0] = (DWORD)(uintptr_t)m1.rm;
    const LONG in0 = C.jitInlined;
    JtMake(c1, f1, 3, 0, i1);
    assert(C.jitInlined == in0 + 1);
    JtLazyFrame();
    g_jtChunkWords[(32 + 4) / 4] = 0x11223344;
    g_jtArena[0] = (DWORD)(uintptr_t)obj, g_jtArena[1] = 0xAAAAAAAA;
    JtLazyGo(f1, 2);
    assert(g_jtEax == JT_RET && g_jtArena[0] == 77 && g_jtArena[1] == 0x11223344 && g_jtEsi == (DWORD)(uintptr_t)(g_jtArena + 2));
    assert(g.st.samples == 4 && g.st.viaInline == 1 && !g.st.faults);  // c1 at JitEnter, m1 inlined
    JtLazyFrame();
    g_jtArena[0] = 0, g_jtArena[1] = 0xAAAAAAAA;  // a null this takes EA's call before the inline sample: only c1 at JitEnter
    JtLazyGo(f1, 2);
    assert(g_jtEax == 0xEA && g.st.samples == 5 && g.st.viaInline == 1);  // only c1 at JitEnter

    // a value-type this in no frame and no GC block (a C-stack copy): a blob of the value's size; any other word there stays outside
    static DWORD val[2] = {0x1234, 0x5678};
    g.s->Clear();
    const DWORD out0 = g.st.outside;
    JcRoot((DWORD)(uintptr_t)val, NULL, 4);
    assert(g.s->n == 1 && !g.cls[0] && g.s->r[0].n == 4 && g.st.outside == out0);
    JcRoot((DWORD)(uintptr_t)(val + 1), NULL, 0);
    assert(g.s->n == 1 && g.st.outside == out0 + 1);

    // the file: each sample record names its path (version 2: the word after the slot)
    JcFlush();
    g_jcOn = 0;
    CloseHandle(g.file);
    static u8 fb[1 << 16];
    FILE* fp = NULL;
    assert(!fopen_s(&fp, path, "rb") && fp);
    const size_t fn = fread(fb, 1, sizeof(fb), fp);
    fclose(fp);
    int via[3] = {};
    for (size_t at = 8; at + 8 <= fn; at += 8 + *(const DWORD*)(fb + at + 4))
        if (*(const DWORD*)(fb + at) == 4 && *(const DWORD*)(fb + at + 16) < 3) via[*(const DWORD*)(fb + at + 16)]++;
    assert(*(const DWORD*)(fb + 4) == 2 && via[0] == 3 && via[1] == 1 && via[2] == 1);
    DeleteFileA(path);
    VirtualFree(g_jc, 0, MEM_RELEASE);
    g_jc = NULL;
    GcBase = gb;
    g_jitInline = 0, g_jitLazy = lazy0, g_jitLeafOnly = leaf0;
    C.jitInlined = C.jitInlineNo = 0;
    g_jitForceJit = (const BYTE*)(uintptr_t)TS3A(0x00E5637D), g_jitProxyClassP = (const void* const*)(uintptr_t)TS3A(0x01220EC0);
}
static void TestMonoJitCapture() {
    char nm[256];
    DWORD at = 0;
    static JcFakeClass kInt, kList, kEnum, kArr;
    JcFakeName(kInt, "System", "Int32", 0x11, &kInt);
    JcFakeName(kList, "System.Collections.Generic", "List`1", 0x12, &kList);
    // List`1[[Int32]]: MonoGenericClass +0 inst, +4 container; inst +4 argc, +8 argv
    static u8 argT[8]; *(const void**)argT = &kInt, argT[6] = 0x11;
    static const u8* argv[1] = {argT};
    static DWORD inst[3] = {7, 1, 0}; inst[2] = (DWORD)(uintptr_t)argv;
    static DWORD gc[2]; gc[0] = (DWORD)(uintptr_t)inst, gc[1] = (DWORD)(uintptr_t)&kList;
    JcFakeName(kEnum, "", "Enumerator", 0x15, gc);
    *(const void**)(kEnum.b + 0x28) = &kList;  // nested
    assert(JcClassName(kEnum.b, nm, sizeof(nm), &at, 0));
    assert(!strcmp(nm, "System.Collections.Generic.List`1[[System.Int32]]") || strstr(nm, "List`1[[System.Int32]]"));
    JcFakeName(kArr, "", "Enumerator[]", 0x1D, &kInt);
    at = 0;
    assert(JcClassName(kArr.b, nm, sizeof(nm), &at, 0) && !strcmp(nm, "System.Int32[]"));
    at = 0;
    JcFakeName(kArr, "", "x", 0x14, &kInt);  // rank > 1 has no name here
    assert(!JcClassName(kArr.b, nm, sizeof(nm), &at, 0));
    at = 0;
    *(const char**)(kInt.b + 0x34) = (const char*)(uintptr_t)0x10;  // a name pointer that reads nothing fails, it does not fault
    assert(!JcClassName(kInt.b, nm, sizeof(nm), &at, 0));
    *(const char**)(kInt.b + 0x34) = "Int32";

    char path[MAX_PATH], tmp[MAX_PATH];
    assert(GetTempPathA(MAX_PATH, tmp) && GetTempFileNameA(tmp, "jc", 0, path));
    wchar_t wpath[MAX_PATH];
    MultiByteToWideChar(CP_ACP, 0, path, -1, wpath, MAX_PATH);
    assert(JcInit(wpath, 2, 2, 2, 16, 4096, 1024, 1000000, 1 << 20));
    JcState& g = *g_jc;

    // a vtable word counts only when the class's runtime info lists it
    static DWORD vt[8], other[8];
    static DWORD ri[4];
    ri[0] = 0, ri[1] = (DWORD)(uintptr_t)vt;
    *(const void**)(kInt.b + 0xB8) = ri;
    vt[0] = other[0] = (DWORD)(uintptr_t)&kInt;
    assert(JcClassOf((DWORD)(uintptr_t)vt) && !JcClassOf((DWORD)(uintptr_t)other) && !JcClassOf((DWORD)(uintptr_t)other));
    assert(g.nClass == 1 && !JcClassOf(0x10));

    // first 2 calls, then the reservoir; a fault drops its sample; no budget left = skipped and counted
    static u8 rmB[0x80], mmB[0x40];
    static DWORD frame[8];
    static u8 inv[0x20];
    static DWORD sargs[2] = {7, 0};
    *(void**)(rmB + 8) = mmB, rmB[0x40] = 1;
    *(const void**)(mmB + 8) = &kInt;
    *(void**)rmB = rmB;
    *(void**)(inv + 4) = rmB, *(void**)(inv + 0x14) = sargs;
    frame[2] = (DWORD)(uintptr_t)inv;
    JitRegs r = {};
    r.ebp = (DWORD)(uintptr_t)&frame[4];
    r.edi = (DWORD)(uintptr_t)rmB;
    static JitMeth meth0;
    JitMeth* saveMeth = g_jitMeth;
    g_jitMeth = &meth0;
    meth0.rm = rmB, meth0.pos0 = meth0.posN = 0, strcpy_s(meth0.name, "Ns.Cls.Meth");
    JitEnt e = {};
    e.m = &meth0;
    const GcBaseFn gb = GcBase;
    GcBase = (GcBaseFn)JtNoBase;
    for (int i = 0; i < 2000; i++) JcSample(&r, &e);
    assert(g.st.first == 2 && g.st.reservoir >= 2 && g.st.reservoir < 40 && !g.st.faults);
    assert(g.meths[0].seen == 2000);
    const DWORD n0 = g.n;
    g.meths[0].taken = 0;
    frame[2] = 0;  // no invocation: the read faults, SEH drops the sample and the buffer stays where it was
    JcSample(&r, &e);
    assert(g.st.faults == 1 && g.n == n0);
    frame[2] = (DWORD)(uintptr_t)inv;
    g.budgetUs = 0;
    JcSample(&r, &e);
    assert(g.st.budget == 1 && g.n == n0);
    JcFlush();
    assert(g.written == n0 && !g.n);
    GcBase = gb;
    g_jitMeth = saveMeth;
    g_jcOn = 0;
    CloseHandle(g.file);
    char head[8] = "";
    FILE* f = NULL;
    assert(!fopen_s(&f, path, "rb") && f && fread(head, 1, 8, f) == 8);
    fclose(f);
    assert(!memcmp(head, "JCAP", 4) && *(const DWORD*)(head + 4) == JC_VERSION);
    DeleteFileA(path);
    VirtualFree(g_jc, 0, MEM_RELEASE);
    g_jc = NULL;
    g_jcFaultLog = 0;
    TestMonoJitCaptureVia();
    printf("monojit capture: ok\n");
}
