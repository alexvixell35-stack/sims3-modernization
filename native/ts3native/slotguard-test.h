// TestSlotGuard ([SlotGuard] slotguard.cpp, [UafCheck] Object frees and foreign-jmp refusal, AsDetourCore's own trampoline page;
// docs/539 §18). Included by selftest.cpp after uafcheck-test.h. Fake Object (vt 0x1042CA8, component list +0x34) holding a fake
// SlotComponent; the hooks are called directly with fake EA continuations.
static int g_sgtClears;
static void __fastcall SgtClear(u8*, void*) { g_sgtClears++; }
static DWORD __fastcall SgtLoad(u8*, void*, void*, DWORD) { return 7; }
static DWORD __fastcall SgtClone(u8*, void*, const u8*) { return 1; }

static void TestSlotGuard() {
    u8* mem = (u8*)VirtualAlloc(NULL, 0x3000, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
    u8* hole = (u8*)VirtualAlloc(NULL, 0x1000, MEM_RESERVE, PAGE_READWRITE);  // reserved only: unreadable
    assert(mem && hole);
    DWORD* obj = (DWORD*)(mem + 0x100);
    obj[0] = kSgObjVt, obj[1] = 3, obj[2] = 0x1234, obj[3] = 0x5678;
    u8* comp = mem + 0x100 + 0xD8;  // the crash's layout: ContainmentSlots cell at Object+0xEC
    obj[0x34 / 4 + 2] = (DWORD)(uintptr_t)comp;
    DWORD* good = (DWORD*)(mem + 0x1004);  // cookie 2 at p-4, element vt 0x10440CC
    good[-1] = 2, good[0] = 0x010440CC;
    DWORD* zeroCookie = (DWORD*)(mem + 0x1104);  // cookie 0: EA_Free(p-4), the element vt is not read
    zeroCookie[-1] = 0, zeroCookie[0] = 0;
    DWORD* noVt = (DWORD*)(mem + 0x1204);
    noVt[-1] = 1, noVt[0] = 0;  // 0x951A00's case: [0x40A00000] = 0
    DWORD* pairs = (DWORD*)(comp + 8);
    auto fill = [&]() {
        pairs[0] = 2, pairs[1] = (DWORD)(uintptr_t)good;
        pairs[2] = 1, pairs[3] = kSgF5;
        pairs[4] = 0, pairs[5] = (DWORD)(uintptr_t)zeroCookie;
        pairs[6] = 0, pairs[7] = 0;
        pairs[8] = 1, pairs[9] = (DWORD)(uintptr_t)noVt;
    };
    assert(!SgBadPtr(0) && !SgBadPtr((DWORD)(uintptr_t)good) && !SgBadPtr((DWORD)(uintptr_t)zeroCookie));
    assert(SgBadPtr(kSgF5) && SgBadPtr((DWORD)(uintptr_t)noVt) && SgBadPtr((DWORD)(uintptr_t)good + 2) && SgBadPtr(0x1000));
    assert(SgBadPtr((DWORD)(uintptr_t)hole + 0x10) && !strcmp(SgBadPtr((DWORD)(uintptr_t)hole + 0x10), "unreadable"));
    assert(SgOwner((DWORD)(uintptr_t)comp) == (DWORD)(uintptr_t)obj && SgOwner((DWORD)(uintptr_t)(mem + 0x2000)) == 0);
    assert(!strcmp(SgWhen(0x00952D2F, false), "teardown (SlotComponent dtor 0x952D20)") && !strncmp(SgWhen(0, true), "clone", 5));

    g_sgClear = SgtClear, g_sgLoad = SgtLoad, g_sgClone = SgtClone;
    // Guard 0: logged, nothing changed, EA still called
    g_sgGuard = false, fill();
    const LONG bad0 = g_sgBad, fix0 = g_sgFixed;
    SgClearHook(comp, NULL);
    assert(g_sgtClears == 1 && g_sgBad == bad0 + 2 && g_sgFixed == fix0 && pairs[3] == kSgF5 && pairs[9] == (DWORD)(uintptr_t)noVt);
    // load end with a good component: remembered; a later teardown finds it bad ("last checked OK ... ago") and zeroes the pairs
    g_sgGuard = true;
    pairs[3] = 0, pairs[9] = 0;
    assert(SgLoadHook(comp, NULL, NULL, 1) == 7 && SgSlot((DWORD)(uintptr_t)comp).comp == (DWORD)(uintptr_t)comp);
    fill();
    assert(SgCheck(comp, "teardown (SlotComponent dtor 0x952D20)", 0x00952D2F, NULL) == 2);
    assert(g_sgFixed == fix0 + 2 && pairs[2] == 0 && pairs[3] == 0 && pairs[8] == 0 && pairs[9] == 0);
    assert(pairs[1] == (DWORD)(uintptr_t)good && pairs[5] == (DWORD)(uintptr_t)zeroCookie && SgSlot((DWORD)(uintptr_t)comp).comp == 0);
    // clone end with a bad pair: not remembered as good
    fill();
    assert(SgCloneHook(comp, NULL, NULL) == 1 && pairs[3] == 0 && SgSlot((DWORD)(uintptr_t)comp).comp == 0);
    g_sgClear = NULL, g_sgLoad = NULL, g_sgClone = NULL, g_sgGuard = false;

    // [UafCheck] Object frees: ICoreAllocator Free 0x4E3EF0's return 0x4E3EFD, two dwords up Object_DeletingDtor's 0x935E1B
    const DWORD raObj[3] = {0x004E3EFD, 0x11, 0x00935E1B}, raOther[3] = {0x004E3F84, 0x11, 0x00935E1B};
    assert(UafObjFree(raObj) && !UafObjFree(raOther));
    // a jmp at 0x4E5920 into another module is refused, one into ours chains
    u8 j[5] = {0xE9};
    *(LONG*)(j + 1) = (LONG)(0x6D000000 - ((DWORD)(uintptr_t)j + 5));
    const DWORD lo0 = g_selfLo, hi0 = g_selfHi;
    g_selfLo = 0x6C000000, g_selfHi = 0x6C100000;
    assert(UafForeignJmp(j) == 0x6D000000);
    g_selfHi = 0x6E000000;
    assert(UafForeignJmp(j) == 0 && UafForeignJmp(kEaLockedPrologue) == 0);
    g_selfLo = lo0, g_selfHi = hi0;

    // AsDetourCore without AllocSites' AsInit (float5uaf-1109b crash: the prologue copied to NULL): it makes its own page
    {
        u8* code = (u8*)VirtualAlloc(NULL, 0x1000, MEM_RESERVE | MEM_COMMIT, PAGE_EXECUTE_READWRITE);
        assert(code);
        memcpy(code, kEaLockedPrologue, sizeof(kEaLockedPrologue));
        code[sizeof(kEaLockedPrologue)] = 0xC3;
        u8* const tramp0 = g_asTramp;
        const int used0 = g_asTrampUsed, n0 = g_asPatchN;
        g_asTramp = NULL, g_asTrampUsed = 0;
        void* slot = NULL;
        const u8* tr = (const u8*)AsDetour("selftest no AsInit", (DWORD)(uintptr_t)code, kEaLockedPrologue, (int)sizeof(kEaLockedPrologue), (void*)SgtClear, &slot);
        assert(tr && g_asTramp && tr == g_asTramp && slot == tr && !memcmp(tr, kEaLockedPrologue, sizeof(kEaLockedPrologue)) && code[0] == 0xE9);
        assert(g_asPatchN == n0 + 1);
        const AsPatch& p = g_asPatch[--g_asPatchN];
        Write5((u8*)(uintptr_t)p.at, p.old);
        assert(!memcmp(code, kEaLockedPrologue, sizeof(kEaLockedPrologue)));
        VirtualFree(g_asTramp, 0, MEM_RELEASE);
        g_asTramp = tramp0, g_asTrampUsed = used0;
        VirtualFree(code, 0, MEM_RELEASE);
    }
    VirtualFree(hole, 0, MEM_RELEASE), VirtualFree(mem, 0, MEM_RELEASE);
    printf("slotguard: bad slot pointers (0x40A00000, no vtable, misaligned, unreadable), owner Object, Guard 0 / 1, load / clone / teardown, "
           "uaf Object frees, foreign jmp refused, AsDetour without AsInit ok\n");
}
