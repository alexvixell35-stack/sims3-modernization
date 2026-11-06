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
    printf("monojit: post-pass order, unpatch after a mismatch, free-block test, install gates ok\n");
}
