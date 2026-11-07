// TestMonoJitCapture ([MonoJit] Capture=1, monojit-capture.h, docs/537 §17). Included by monojit-test.h. Fake metadata only: names, the
// vtable identity check, the sampling arithmetic, a fault dropping its sample, the file header. The walk over a real GC heap runs in the
// host on the corpus (node tools/jit-replay.ts --capture-corpus), the dummy for this code before the game.
struct JcFakeClass { u8 b[0xC0]; };
static void JcFakeName(JcFakeClass& k, const char* ns, const char* nm, u8 type, const void* data) {
    memset(&k, 0, sizeof(k));
    *(const char**)(k.b + 0x34) = nm, *(const char**)(k.b + 0x38) = ns;
    *(const void**)(k.b + 0xA0) = data, k.b[0xA0 + 6] = type;
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
    printf("monojit capture: ok\n");
}
