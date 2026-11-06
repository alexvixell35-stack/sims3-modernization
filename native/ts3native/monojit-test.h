// TestMonoJit ([MonoJit], monojit.cpp, docs/527 step 1). Included by selftest.cpp. The parts that run without the game:
// the transform post-pass (after a transform with no exception only), the unpatch of a method after a verify mismatch
// (and the switch leaving it unpatched), the install gates on a process without EA's tables. The compiler itself is
// tested by native/monohost (MONOHOST_JIT=verify over the corpus: docs/207 §11).
static void* __cdecl MjtXform(BYTE* rm, void*) { return rm[0] == 1 ? (void*)rm : NULL; }  // rm[0] == 1: "threw"
static int g_mjtPosts;
static void __cdecl MjtPost(BYTE*, void*) { g_mjtPosts++; }
static void TestMonoJit() {
    static_assert(offsetof(Ts3nCounters, jitSwitch) == 0x5C7C && offsetof(Ts3nCounters, jitSampled) == 0x5CB0 &&
                      sizeof(Ts3nCounters) == 5933 * 4, "counters v910 [MonoJit] layout");
    {  // post-pass order: EA's transform, then the compiler; never after an exception
        const IlpXformFn next0 = g_ilpNext;
        void (*cold0)(int, DWORD) = g_ilpColdEnter;
        g_ilpNext = MjtXform, g_ilpPost = MjtPost, g_ilpColdEnter = NULL;
        BYTE ok[64] = {0}, bad[64] = {1};
        assert(IlpXform(ok, NULL) == NULL && g_mjtPosts == 1);
        assert(IlpXform(bad, NULL) == bad && g_mjtPosts == 1);
        g_ilpPost = NULL;
        assert(IlpXform(ok, NULL) == NULL && g_mjtPosts == 1);
        g_ilpNext = next0, g_ilpColdEnter = cold0;
    }
    {  // a mismatched method: EA's words back, JitSetPatched(true) leaves it alone, the other method is patched again
        unsigned short* const pos0 = g_jitPos;
        unsigned short* const orig0 = g_jitOrigW;
        const int methN0 = g_jitMethN, cellN0 = g_jitLazyCellN, guardN0 = g_jitGuardN;
        const bool patched0 = g_jitPatched;
        static unsigned short pos[5] = {0, 2, 3, 0, 1}, orig[5] = {0x10, 0x9C, 0x30, 0x40, 0x50};
        unsigned short a[4] = {0x10, 0x11, 0x9C, 0x30}, b[2] = {0x40, 0x50};
        BYTE rmA[64] = {0}, rmB[64] = {0};
        g_jitPos = pos, g_jitOrigW = orig, g_jitMethN = 2, g_jitLazyCellN = 0, g_jitGuardN = 0;
        JitMeth& A = g_jitMeth[0];
        JitMeth& B = g_jitMeth[1];
        memset(&A, 0, sizeof(A)), memset(&B, 0, sizeof(B));
        A.rm = rmA, A.code = a, A.pos0 = 0, A.posN = 3, A.verifyOk = true;
        strcpy_s(A.name, "Test.A");
        B.rm = rmB, B.code = b, B.pos0 = 3, B.posN = 5, B.verifyOk = true;
        JitSetPatched(true);
        assert(a[0] == JIT_OP && a[1] == 0x11 && a[2] == 0x9C && a[3] == JIT_OP && b[0] == JIT_OP && b[1] == JIT_OP);  // leave.s stays EA's
        JitUnpatchMeth(&A);
        assert(a[0] == 0x10 && a[3] == 0x30 && A.dead && !A.verifyOk && b[0] == JIT_OP);
        JitSetPatched(false);
        assert(a[0] == 0x10 && b[0] == 0x40 && b[1] == 0x50);
        JitSetPatched(true);
        assert(a[0] == 0x10 && a[3] == 0x30 && b[0] == JIT_OP && b[1] == JIT_OP);
        JitSetPatched(false);
        memset(&A, 0, sizeof(A)), memset(&B, 0, sizeof(B));
        g_jitPos = pos0, g_jitOrigW = orig0, g_jitMethN = methN0, g_jitLazyCellN = cellN0, g_jitGuardN = guardN0;
        g_jitPatched = patched0;
    }
    {  // free-block test of the verify snapshot (al253): word 0 = 0 is a free-list tail; a vtable-like word 0 below the GC
       // heap is a live object (the link test calls GC_base only for a word >= 0x10000, not reached here)
        DWORD blk[12] = {0};
        assert(JitLooksFree((const u8*)blk, sizeof(blk)));
        blk[0] = 0x1234, blk[3] = 7;
        assert(!JitLooksFree((const u8*)blk, sizeof(blk)));
        blk[0] = 0x1236;
        assert(!JitLooksFree((const u8*)blk, sizeof(blk)));
    }
    {  // install gates: off by default; on in a process without EA's jump table nothing is installed
        wchar_t tmp[MAX_PATH], ini[MAX_PATH];
        GetTempPathW(MAX_PATH, tmp);
        swprintf_s(ini, L"%sts3n-mjtest-%lu.ini", tmp, GetCurrentProcessId());
        WritePrivateProfileStringW(L"MonoJit", L"Mode", L"1", ini);
        const IlpXformFn next0 = g_ilpNext;
        assert(!ApplyMonoJit(ini) && g_ilpPost == NULL && ts3native_counters.jitOn == 0);
        WritePrivateProfileStringW(L"MonoJit", L"Enabled", L"1", ini);
        assert(!ApplyMonoJit(ini) && g_ilpPost == NULL && g_ilpNext == next0 && ts3native_counters.jitOn == 0);
        DeleteFileW(ini);
    }
    {  // a method name longer than the buffer (MjPost names every transformed method): cut with "...", never listed, no
       // CRT invalid-parameter __fastfail (0xC0000409, the al260 BP exit candidate), the fitting name unchanged
        static char ns[200], cn[200], mn[] = "List`1.Add";  // an explicit interface method's name can be that long
        memset(ns, 'N', sizeof(ns) - 1), memset(cn, 'C', sizeof(cn) - 1);
        const char* kls[16] = {};
        const char* meth[8] = {};
        kls[0x34 / 4] = cn, kls[0x38 / 4] = ns, meth[2] = (const char*)kls, meth[0x18 / 4] = mn;
        const char* rmN[4] = {};
        rmN[2] = (const char*)meth;
        const char* const name0 = g_jitNames[0];
        const int nameN0 = g_jitNameN;
        g_jitNames[0] = "List`1.Add", g_jitNameN = 1;
        char b256[256], b96[96];
        assert(JitMethodName((const u8*)rmN, b256, sizeof(b256)) == b256 && strlen(b256) == 255 && !strcmp(b256 + 252, "..."));
        assert(!JitListed(b256));
        assert(JitMethodName((const u8*)rmN, b96, sizeof(b96)) == b96 && strlen(b96) == 95 && !JitListed(b96));
        ns[0] = 0, cn[0] = 'L', cn[1] = 0;
        assert(!strcmp(JitMethodName((const u8*)rmN, b96, sizeof(b96)), "L.List`1.Add") && JitListed(b96));
        g_jitNames[0] = name0, g_jitNameN = nameN0;
    }
    {  // a chained lazy call site copies its own block's B_LVL after B_VT's push: run the emitted push on a fake block
       // (cdecl arguments = the block above the return address, as a lazy body's entry sees it)
        u8* x = (u8*)VirtualAlloc(NULL, 4096, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
        assert(x);
        Em e = {x};
        e.b(0x50);                // push eax (B_VT)
        PushOwnBlk(e, 4, B_LVL);  // push the own block's B_LVL
        e.b(0x58); e.b(0x59); e.b(0xC3);  // pop eax; pop ecx; ret
        typedef DWORD(__cdecl * BlkFn)(DWORD, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD);
        static_assert(B_LVL == 36 && B_CIP == 32 && B_SIZE == 44, "block layout of the call below");
        assert(((BlkFn)x)(0xA0, 0xA4, 0xA8, 0xAC, 0xB0, 0xB4, 0xB8, 0xBC, 0x80001234 /* B_CIP */, 3 /* B_LVL */, 0xC8) == 3);
        VirtualFree(x, 0, MEM_RELEASE);
    }
    {  // a method with an EA native body (+0x24, bound in EA's transform before the post-pass) is not compiled, and a
       // direct call site to one is not a lazy site (EA's call goes through the dispatcher's exception frame)
        DWORD c[0x48 / 4] = {}, caller[0x18 / 4] = {}, mm[8] = {};
        DWORD items[2] = {0, (DWORD)(uintptr_t)c};
        caller[0x14 / 4] = (DWORD)(uintptr_t)items;
        c[8 / 4] = (DWORD)(uintptr_t)mm, c[0x44 / 4] = 0x100;  // transformed, static
        const unsigned short site[3] = {0xEA, 1, 0};           // call, item 1
        const u8* got = NULL; bool guard = true;
        assert(JitLazySite(site, 0xEA, (const u8*)caller, &got, &guard) && got == (const u8*)c && !guard);
        c[0x24 / 4] = 0xDAE970;
        assert(!JitLazySite(site, 0xEA, (const u8*)caller, &got, &guard));
        JitEnt* const tab0 = g_jitTab;
        const u8* const siteRm0 = g_jitSiteRm;
        const int methN0 = g_jitMethN;
        g_jitTab = (JitEnt*)items, g_jitSiteRm = NULL;  // non-NULL table: past the first gate; nothing may be read or written
        JitCompiled((u8*)c);
        assert(g_jitSiteRm == NULL && g_jitMethN == methN0);
        g_jitTab = tab0, g_jitSiteRm = siteRm0;
    }
    printf("monojit: post-pass order, unpatch after a mismatch, free-block test, install gates, long method names, lazy level push, native-body gates ok\n");
}
