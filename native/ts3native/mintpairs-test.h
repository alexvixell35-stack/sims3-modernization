// TestMintPairs ([MonoFast] PairCount, mintpairs.cpp, docs/139 §3). Included by selftest.cpp.
// A copy of the dispatch head bytes takes the jmp; MpHead runs on a fake instruction stream with its back-jump pointed at a
// `ret`, so the counts, the registers it hands on (eax = edx = op, ecx kept) and the other-thread skip are checked.
static __declspec(naked) void MptRet() { __asm ret }
static DWORD g_mptEdx, g_mptEcx;
static __declspec(naked) DWORD __cdecl MptRun(const unsigned short*) {  // the instruction pointer, read at [esp + 12]
    __asm {
        push edi
        push ecx
        mov edi, [esp + 12]
        mov ecx, 0x5A5A5A5A
        call MpHead              // ends in jmp [g_mpBack] = MptRet: returns here
        mov g_mptEdx, edx
        mov g_mptEcx, ecx
        pop ecx
        pop edi
        ret
    }
}
static DWORD WINAPI MptOther(LPVOID ip) { return MptRun((const unsigned short*)ip); }

static void TestMintPairs() {
    // install on a copy of the head: jmp MpHead + nop, the cmp after it untouched
    u8 head[25] = {0x0F, 0xB7, 0x07, 0x0F, 0xB7, 0xD0, 0x81, 0xFA, 0xC0, 0x01, 0x00, 0x00, 0x0F, 0x87, 0xDB, 0x51, 0x00, 0x00,
                   0xFF, 0x24, 0x95, 0x80, 0x98, 0xE5, 0x00};
    u8 bad[25];
    memcpy(bad, head, sizeof(bad));
    bad[2] = 0x06;  // movzx eax, word [esi]: another owner or build
    assert(!MpInstall(bad) && bad[0] == 0x0F);
    assert(MpInstall(head));
    assert(head[0] == 0xE9 && head[5] == 0x90 && head[6] == 0x81);
    assert((DWORD)(uintptr_t)(head + 5) + *(const DWORD*)(head + 1) == (DWORD)(uintptr_t)MpHead);
    assert(g_mpPairs);
    // counting: sim thread only, ops > 0x1C0 skipped, pair = (previous op, op)
    const DWORD back = g_mpBack;
    g_mpBack = (DWORD)(uintptr_t)MptRet;
    g_mpSimTid = GetCurrentThreadId();
    g_mpPrev = 0;
    const unsigned short code[] = {0x0AB, 0x0AD, 0x0AB, 0x0AD, 0x1C5, 0x0AB};
    for (int i = 0; i < 6; i++) {
        assert(MptRun(code + i) == code[i]);
        assert(g_mptEdx == code[i] && g_mptEcx == 0x5A5A5A5A);
    }
    assert(g_mpPairs[0 * MP_OPS + 0x0AB] == 1);      // the first op pairs with the initial 0
    assert(g_mpPairs[0x0AB * MP_OPS + 0x0AD] == 2);
    assert(g_mpPairs[0x0AD * MP_OPS + 0x0AB] == 2);  // 0x1C5 skipped: 0x0AD -> 0x0AB
    HANDLE t = CreateThread(NULL, 0, MptOther, (LPVOID)code, 0, NULL);
    assert(t);
    WaitForSingleObject(t, INFINITE);
    CloseHandle(t);
    assert(g_mpPairs[0x0AB * MP_OPS + 0x0AB] == 0);  // another thread: not counted
    // 64-bit carry
    g_mpPairs[0x0AB * MP_OPS + 0x0AD] = 0xFFFFFFFFull;
    g_mpPrev = 0x0AB;
    MptRun(code + 1);
    assert(g_mpPairs[0x0AB * MP_OPS + 0x0AD] == 0x100000000ull);
    // top-n order
    const unsigned __int64 v[6] = {5, 0, 9, 1, 9, 3};
    int idx[3];
    assert(MpTop(v, 6, idx, 3) == 3 && idx[0] == 2 && idx[1] == 4 && idx[2] == 0);
    assert(MpTop(v, 2, idx, 3) == 1 && idx[0] == 0);
    g_mpBack = back;
    g_mpSimTid = 0;
    memset(g_mpPairs, 0, MP_OPS * MP_OPS * 8);
    printf("selftest mint pairs ok\n");
}
