// TestUafCheck ([UafCheck], uafcheck.cpp, docs/539 §17). Included by selftest.cpp. Fake EA chunks (size word at p-0xC, the next
// chunk's in-use bit), a fake main heap pointer and a fake Free that records what it was given; UafFreeHook is called directly:
// the ring, the pattern, eviction order, write-after-free at eviction and at a verify-all, the float-5 flag, a second free
// absorbed, pass-throughs (other heap, too small, not in use, ring of larger blocks), the DR hold-aside and its release.
static void* g_uafTHeap = (void*)0x1234;
static DWORD g_uafTFreed[64];
static int g_uafTFreedN;
static void __fastcall UafTFree(void*, void*, void* p) { g_uafTFreed[g_uafTFreedN++ & 63] = (DWORD)(uintptr_t)p; }

static u8* UafTChunk(u8* mem, DWORD user, bool inUse = true) {  // a chunk at mem: header 0x10, user bytes, the next chunk's header
    const DWORD ch = user + 0x10;
    memset(mem, 0x11, ch + 0x10);
    *(DWORD*)(mem + 4) = ch | 1;
    *(DWORD*)(mem + ch + 4) = 0x40 | (inUse ? 1 : 0);
    return mem + 0x10;
}

static void TestUafCheck() {
    static_assert(offsetof(Ts3nCounters, uafHeld) == 0x5F58 && offsetof(Ts3nCounters, uafArms) == 0x5F6C && sizeof(Ts3nCounters) == 6118 * 4,
                  "counters v183 layout after v182 (docs/registry.md)");
    Ts3nCounters& z = ts3native_counters;
    const LONG held0 = z.uafHeld, freed0 = z.uafFreed, hit0 = z.uafHit, f50 = z.uafF5, pass0 = z.uafPass;
    const u8* img = (const u8*)GetModuleHandleW(NULL);
    const IMAGE_NT_HEADERS* nt = (const IMAGE_NT_HEADERS*)(img + ((const IMAGE_DOS_HEADER*)img)->e_lfanew);
    g_asExeBase = (DWORD)(uintptr_t)img;
    g_asTextLo = g_asExeBase + nt->OptionalHeader.BaseOfCode, g_asTextHi = g_asTextLo + nt->OptionalHeader.SizeOfCode;
    g_uafHeapAt = &g_uafTHeap, g_uafFree = UafTFree, g_uafMin = 64, g_uafMax = 4096, g_uafDrArms = 0, g_uafOn = true;
    assert(UafInit(1, 4));  // 4 blocks, 1 MB
    u8* mem = (u8*)VirtualAlloc(NULL, 64 * 0x100, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
    assert(mem);
    u8* b[16];
    for (int i = 0; i < 16; i++) b[i] = UafTChunk(mem + i * 0x100, 0x60);

    // 4 held, filled; the 5th evicts the oldest, which is clean and really freed
    for (int i = 0; i < 4; i++) UafFreeHook(g_uafTHeap, NULL, b[i]);
    assert(g_uafTFreedN == 0 && z.uafHeld == held0 + 4 && g_uafN == 4 && g_uafBytes == 4 * 0x70);
    for (int i = 0; i < 0x60; i += 4) assert(*(DWORD*)(b[2] + i) == kUafPat);
    assert(*(DWORD*)(b[2] + 0x60) != kUafPat);  // the next chunk's header is not touched
    UafFreeHook(g_uafTHeap, NULL, b[4]);
    assert(g_uafTFreedN == 1 && g_uafTFreed[0] == (DWORD)(uintptr_t)b[0] && z.uafHit == hit0 && z.uafFreed == freed0 + 1);

    // a float-5 store into a held block: found by the verify-all, counted, refilled (a second verify finds nothing)
    *(DWORD*)(b[1] + 0x2C) = kUafF5;
    UafVerifyAll("selftest");
    assert(z.uafHit == hit0 + 1 && z.uafF5 == f50 + 1 && *(DWORD*)(b[1] + 0x2C) == kUafPat);
    UafVerifyAll("selftest again");
    assert(z.uafHit == hit0 + 1);

    // a write found at eviction: b[1] is next out
    *(DWORD*)(b[1] + 0x8) = 0x1234;
    UafFreeHook(g_uafTHeap, NULL, b[5]);
    assert(g_uafTFreedN == 2 && g_uafTFreed[1] == (DWORD)(uintptr_t)b[1] && z.uafHit == hit0 + 2 && z.uafF5 == f50 + 1);

    // a second free of a held block (b[3]) is absorbed; a live block that only starts like the pattern is held as usual
    UafFreeHook(g_uafTHeap, NULL, b[3]);
    assert(g_uafTFreedN == 2 && z.uafPass == pass0 + 1 && g_uafN == 4);
    *(DWORD*)b[6] = kUafPat, *(DWORD*)(b[6] + 4) = kUafPat;
    UafFreeHook(g_uafTHeap, NULL, b[6]);
    assert(g_uafTFreedN == 3 && g_uafTFreed[2] == (DWORD)(uintptr_t)b[2] && z.uafPass == pass0 + 1);

    // pass-throughs: another heap, a block under MinSize, a chunk not in use, NULL
    u8* small = UafTChunk(mem + 0x1000, 0x20);
    u8* notUsed = UafTChunk(mem + 0x1100, 0x60, false);
    UafFreeHook((void*)0x5678, NULL, b[7]);
    UafFreeHook(g_uafTHeap, NULL, small);
    UafFreeHook(g_uafTHeap, NULL, notUsed);
    UafFreeHook(g_uafTHeap, NULL, NULL);
    assert(g_uafTFreedN == 7 && g_uafTFreed[3] == (DWORD)(uintptr_t)b[7] && g_uafTFreed[4] == (DWORD)(uintptr_t)small &&
           g_uafTFreed[5] == (DWORD)(uintptr_t)notUsed && g_uafTFreed[6] == 0 && *(DWORD*)small == 0x11111111);

    // byte cap: a block larger than the whole cap passes straight to EA after evicting what it can (8 a call at most)
    g_uafMaxBytes = 0x200;
    u8* big = UafTChunk(mem + 0x1200, 0x400);
    const int n0 = g_uafTFreedN;
    UafFreeHook(g_uafTHeap, NULL, big);
    assert(g_uafN == 0 && g_uafTFreedN == n0 + 5 && g_uafTFreed[(n0 + 4) & 63] == (DWORD)(uintptr_t)big && z.uafPass == pass0 + 2);
    g_uafMaxBytes = 1 << 20;

    // DR combo: a target (chunk 0x70, +0x2C) holds the next DrArms blocks of that size aside; the helper step arms (no handler
    // here: "NOT SET") and releases them after the hold, verified and freed; other sizes go to the ring
    g_uafDrArms = 1, g_uafDrChunk = 0, g_uafDrF5 = 0;
    g_uafArmEv = CreateEventW(NULL, FALSE, FALSE, NULL);
    UafFreeHook(g_uafTHeap, NULL, b[8]);
    *(DWORD*)(b[8] + 0x2C) = kUafF5;
    UafVerifyAll("selftest dr");
    assert(g_uafDrChunk == 0x70 && g_uafDrOff == 0x2C && g_uafDrF5 && g_uafDrWant == 1);
    const int n1 = g_uafTFreedN;
    UafFreeHook(g_uafTHeap, NULL, b[9]);
    assert(g_uafArm[0].state == 2 && g_uafArm[0].b.p == (DWORD)(uintptr_t)b[9] && g_uafArm[0].off == 0x2C && g_uafDrWant == 0 && g_uafTFreedN == n1);
    UafFreeHook(g_uafTHeap, NULL, b[10]);  // want spent: the ring
    assert(g_uafArm[1].state == 0 && g_uafN == 2);
    const DWORD now = GetTickCount();
    UafArmStep(now);
    assert(g_uafArm[0].state == 3);
    UafArmStep(now + 1000);
    assert(g_uafArm[0].state == 3 && g_uafTFreedN == n1);
    *(DWORD*)(b[9] + 0x2C) = kUafF5;
    const LONG hit1 = z.uafHit;
    UafArmStep(now + g_uafDrHoldMs);
    assert(g_uafArm[0].state == 0 && g_uafTFreedN == n1 + 1 && g_uafTFreed[n1 & 63] == (DWORD)(uintptr_t)b[9] && z.uafHit == hit1 + 1);

    // free tags (uaftag.h): a free under a scope stores tag + site in the record; a write after free names it; tagged pass-throughs
    // and real frees go to the recently-freed ring; UafNearLog finds the quarantine, the ring, or nothing ("no match")
    assert(t_uafTag.tag == UT_NONE);
    {
        UafTagScope ts(UT_P5C_FLUSH, (const void*)(uintptr_t)0x1234);
        {
            UafTagScope inner(UT_THUMBPOSE, (const void*)(uintptr_t)0x5678);
            assert(t_uafTag.tag == UT_THUMBPOSE && t_uafTag.site == 0x5678);
        }
        assert(t_uafTag.tag == UT_P5C_FLUSH && t_uafTag.site == 0x1234);
        UafFreeHook(g_uafTHeap, NULL, b[11]);
        UafFreeHook((void*)0x5678, NULL, b[12]);  // another heap: passes, but tagged, so the recently-freed ring keeps it
    }
    assert(t_uafTag.tag == UT_NONE && t_uafTag.site == 0);
    const UafBlk& tb = g_uafRing[(g_uafHead + g_uafN - 1) % g_uafCap];
    assert(tb.p == (DWORD)(uintptr_t)b[11] && tb.tag == UT_P5C_FLUSH && tb.site == 0x1234);
    assert(g_uafGone && g_uafGone[(g_uafGoneN - 1) & (kUafGoneN - 1)].p == (DWORD)(uintptr_t)b[12] &&
           g_uafGone[(g_uafGoneN - 1) & (kUafGoneN - 1)].tag == UT_P5C_FLUSH);
    const LONG goneN0 = g_uafGoneN;
    UafFreeHook((void*)0x5678, NULL, b[13]);  // untagged pass-through: not kept
    assert(g_uafGoneN == goneN0);
    g_uafNearN = 0;
    assert(UafNearLog((DWORD)(uintptr_t)(b[11] + 0x10), "selftest outfit cell") >= 1);  // quarantined, tag DestroyPark flush
    assert(UafNearLog((DWORD)(uintptr_t)(b[12] + 0x8), "selftest outfit") >= 1);        // recently-freed ring
    assert(UafNearLog(0x10, "selftest nothing") == 0 && UafNearLog(0, "selftest null") == 0);
    *(DWORD*)(b[11] + 0x30) = kUafF5;
    const LONG hit2 = z.uafHit;
    UafVerifyAll("selftest tag");  // logs "... freed under tag DestroyPark flush (site ts3native+0x1234) ..."
    assert(z.uafHit == hit2 + 1);
    g_uafOn = false;
    assert(UafNearLog((DWORD)(uintptr_t)(b[11] + 0x10), "selftest off") == 0);  // [UafCheck] off: no lookup
    g_uafOn = true;

    CloseHandle(g_uafArmEv), g_uafArmEv = NULL;
    g_uafDrArms = 0, g_uafDrChunk = 0, g_uafDrWant = 0, g_uafOn = false;
    g_uafHeapAt = (void* const*)(uintptr_t)0x011CB864, g_uafFree = NULL;
    VirtualFree(g_uafRing, 0, MEM_RELEASE), g_uafRing = NULL;
    VirtualFree(mem, 0, MEM_RELEASE);
    printf("uafcheck: ring, pattern, eviction, verify-all, float-5, second free, pass-throughs, byte cap, DR hold-aside, free tags, recently-freed ring, near-our-free lookup ok\n");
}
