// TestFloat5Free ([Float5Free] float5free.cpp, docs/539 §19). Included by selftest.cpp after uafcheck-test.h (UafTChunk, UafTFree).
// F5fBad on fake EA chunks; UafFreeHook with [UafCheck] off: Guard 0 passes a float-5 free to EA, Guard 1 skips it, good frees pass;
// the SACS Walker owner from 0x727E10's frame; the deque check (pointer array, begin / end slots, subarray pointers) and its hook.
static int g_f5tDtors;
static void __fastcall F5tDtor(DWORD*, void*) { g_f5tDtors++; }

static void TestFloat5Free() {
    u8* mem = (u8*)VirtualAlloc(NULL, 0x2000, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
    u8* hole = (u8*)VirtualAlloc(NULL, 0x1000, MEM_RESERVE, PAGE_READWRITE);  // reserved only: unreadable
    assert(mem && hole);
    u8* good = UafTChunk(mem, 0x60);
    u8* sub = UafTChunk(mem + 0x100, 0x100);
    u8* freed = UafTChunk(mem + 0x300, 0x60, false);
    const DWORD g = (DWORD)(uintptr_t)good;
    assert(!F5fBad(g) && !F5fBad((DWORD)(uintptr_t)sub));
    // 0x40A00000 unmapped here (the 11-09 crash: [0x40A00000-0xC] unreadable): flagged
    assert(!strcmp(F5fBad(0x40A00000), "0x40A00000 range (float 5.0), chunk header unreadable") && F5fBad(0x40A0FFF8) &&
           (!F5fBad(0x40A10000) || strncmp(F5fBad(0x40A10000), "0x40A00000 range", 16)));
    assert(!strcmp(F5fBad(g + 4), "not a heap pointer") && F5fBad(0x8000) && F5fBad(0xFFFE0000));
    assert(!strcmp(F5fBad((DWORD)(uintptr_t)freed), "chunk not in use (freed)"));
    assert(!strcmp(F5fBad((DWORD)(uintptr_t)hole + 0x10), "chunk header unreadable"));
    {   // 0.36.233: real heap blocks in the range pass (float5-round4 all-off-1 leaked 26 such frees, e.g. 0x40A07BD0)
        u8* r = (u8*)VirtualAlloc((void*)(uintptr_t)0x409F0000, 0x20000, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
        assert(r == (u8*)(uintptr_t)0x409F0000);
        UafTChunk((u8*)(uintptr_t)0x40A07BC0, 0x60);         // in-use block at 0x40A07BD0
        UafTChunk((u8*)(uintptr_t)0x409FFFF0, 0x60);         // in-use block at exactly 0x40A00000
        UafTChunk((u8*)(uintptr_t)0x40A01000, 0x60, false);  // freed block at 0x40A01010
        assert(!F5fBad(0x40A07BD0) && !F5fBad(0x40A00000));
        assert(!strcmp(F5fBad(0x40A01010), "0x40A00000 range (float 5.0), chunk not in use (freed)"));
        assert(!strcmp(F5fBad(0x40A04000), "0x40A00000 range (float 5.0), chunk header invalid"));  // zero memory, no chunk
        static void* rheap = (void*)0x1234;
        void* const* heapAt0 = g_uafHeapAt;
        const EaFreeFn free0 = g_uafFree;
        const bool uafOn0 = g_uafOn;
        g_uafHeapAt = &rheap, g_uafFree = UafTFree, g_uafOn = false, g_f5fOn = true, g_f5fGuard = true;
        const LONG bad0 = g_f5fBad, pass0 = g_f5fRangePass;
        const int n0 = g_uafTFreedN;
        UafFreeHook(rheap, NULL, (void*)(uintptr_t)0x40A07BD0);  // Guard=1: a valid block still reaches EA
        UafFreeHook(rheap, NULL, (void*)(uintptr_t)0x40A01010);  // freed: skipped
        assert(g_uafTFreedN == n0 + 1 && g_uafTFreed[n0 & 63] == 0x40A07BD0 && g_f5fBad == bad0 + 1 && g_f5fRangePass == pass0 + 1);
        g_uafHeapAt = heapAt0, g_uafFree = free0, g_uafOn = uafOn0, g_f5fOn = false, g_f5fGuard = false;
        VirtualFree(r, 0, MEM_RELEASE);
    }

    // the Free hook with [UafCheck] off: only [Float5Free] runs, the rest goes to EA
    static void* heap = (void*)0x1234;
    void* const* heapAt0 = g_uafHeapAt;
    const EaFreeFn free0 = g_uafFree;
    const bool uafOn0 = g_uafOn;
    g_uafHeapAt = &heap, g_uafFree = UafTFree, g_uafOn = false, g_f5fOn = true;
    const LONG bad0 = g_f5fBad, skip0 = g_f5fSkip;
    int n0 = g_uafTFreedN;
    g_f5fGuard = false;
    UafFreeHook(heap, NULL, (void*)(uintptr_t)0x40A00000);
    UafFreeHook(heap, NULL, good);
    assert(g_uafTFreedN == n0 + 2 && g_uafTFreed[n0 & 63] == 0x40A00000 && g_f5fBad == bad0 + 1 && g_f5fSkip == skip0);
    g_f5fGuard = true, n0 = g_uafTFreedN;
    UafFreeHook(heap, NULL, (void*)(uintptr_t)0x40A00000);
    UafFreeHook(heap, NULL, freed);  // a free of freed memory: skipped too
    UafFreeHook(heap, NULL, good);
    assert(g_uafTFreedN == n0 + 1 && g_uafTFreed[n0 & 63] == g && g_f5fBad == bad0 + 3 && g_f5fSkip == skip0 + 2);
    g_f5fOn = false;
    UafFreeHook(heap, NULL, (void*)(uintptr_t)0x40A00000);  // [Float5Free] off: straight to EA
    assert(g_uafTFreedN == n0 + 2 && g_f5fBad == bad0 + 3);

    // the crash's frame: EA_Free 0x4E3F84, ptr, 0x727E5C, ptr, ebx, edi, esi = deque = SACS Walker + 0x2C
    DWORD* walker = (DWORD*)(mem + 0x600);
    walker[0] = kF5fWalkerVt;
    const DWORD ra[7] = {0x004E3F84, 0x40A00000, kF5fRet727, 0x40A00000, 0, 0, (DWORD)(uintptr_t)walker + 0x2C};
    assert(F5fFreeCheck(heap, 0x40A00000, ra) && g_f5fBad == bad0 + 4);  // logs the Walker

    // deque: pointer array (8 slots) with begin slot 1, end slot 2 -> two subarrays
    DWORD* dq = walker + 0x2C / 4;
    DWORD* arr = (DWORD*)UafTChunk(mem + 0x800, 0x20);
    memset(dq, 0, 0x28);
    dq[0] = (DWORD)(uintptr_t)arr, dq[1] = 8, dq[5] = (DWORD)(uintptr_t)(arr + 1), dq[9] = (DWORD)(uintptr_t)(arr + 2);
    arr[1] = (DWORD)(uintptr_t)sub, arr[2] = g;
    g_f5fGuard = false;
    const LONG dbad0 = g_f5dBad, dskip0 = g_f5dSkip;
    assert(F5dCheck(dq, kF5dRetWalker, NULL) == 0);
    arr[2] = 0x40A00000;  // the crash: end slot holds float 5.0
    assert(F5dCheck(dq, kF5dRetWalker, NULL) == 1 && g_f5dBad == dbad0 + 1);
    arr[2] = g;
    dq[9] = (DWORD)(uintptr_t)(arr + 8);  // end slot past the array
    assert(F5dCheck(dq, 0, NULL) == 1);
    dq[9] = (DWORD)(uintptr_t)(arr + 2);
    *(DWORD*)((u8*)arr - 0x10 + 0x30 + 4) &= ~1u;  // the pointer array's chunk freed (the crash's dangling array)
    assert(F5dCheck(dq, kF5dRetWalker, NULL) == 1 && g_f5dBad == dbad0 + 3);
    DWORD empty[10] = {};
    assert(F5dCheck(empty, 0, NULL) == 0);
    // the hook: Guard 1 skips EA's destruction of a bad deque, a good one (and Guard 0) reach EA
    const F5dDtorFn dtor0 = g_f5dDtor;
    g_f5dDtor = F5tDtor, g_f5fGuard = true;
    F5dDtorHook(dq, NULL);
    assert(g_f5tDtors == 0 && g_f5dSkip == dskip0 + 1);
    *(DWORD*)((u8*)arr - 0x10 + 0x30 + 4) |= 1u;
    F5dDtorHook(dq, NULL);
    assert(g_f5tDtors == 1);
    g_f5fGuard = false, arr[1] = 0x40A00000;
    F5dDtorHook(dq, NULL);
    assert(g_f5tDtors == 2 && g_f5dBad == dbad0 + 5);

    g_f5dDtor = dtor0, g_uafHeapAt = heapAt0, g_uafFree = free0, g_uafOn = uafOn0, g_f5fOn = false, g_f5fGuard = false;
    VirtualFree(hole, 0, MEM_RELEASE), VirtualFree(mem, 0, MEM_RELEASE);
    printf("float5free: bad pointers (0x40A00000 range unreadable / freed / zero header, in-use range blocks pass, misaligned, freed chunk, unreadable), Free hook Guard 0 / 1 with UafCheck off, "
           "Walker owner, deque pointer array / slots / freed array, deque hook Guard 0 / 1 ok\n");
}
