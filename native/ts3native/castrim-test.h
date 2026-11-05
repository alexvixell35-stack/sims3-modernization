// TestCasTrim ([CasTrim], castrim.cpp, docs/191 step 2). Included by selftest.cpp. A fake compositor service map
// (buckets +0x108, count +0x10C, entries +0x110; node {key, compositor +0x10, next +0x18}), fake compositors with their
// own two vtables and refs at +4 >> 8, a fake erase (unlinks the node) for 0x5EE640, fake insert / Find originals: no
// game code runs.
static u8 g_ctsSvc[0x114];
static u8 g_ctsNode[4][0x1C];
static DWORD g_ctsBkt[2], g_ctsComp[4][4], g_ctsErased, g_ctsInserted;
static DWORD g_ctsVt[2][2];
static bool __fastcall CtsErase(u8* svc, void*, const void* key) {  // as 0x5EE640: find the key's node, unlink it
    void* o = NULL;
    CtFindHook(NULL, NULL, key, &o);  // as EA's unregister: 0x4B0070 asks each cache for the key (a failed find, not a miss)
    DWORD* b = *(DWORD**)(svc + 0x108);
    for (DWORD i = 0; i < *(DWORD*)(svc + 0x10C); i++)
        for (DWORD* p = &b[i]; *p; p = (DWORD*)(uintptr_t)(*p + 0x18))
            if (!memcmp((const void*)(uintptr_t)*p, key, 16)) {
                *p = *(DWORD*)(uintptr_t)(*p + 0x18), (*(DWORD*)(svc + 0x110))--, g_ctsErased++;
                return true;
            }
    return false;
}
static int __fastcall CtsInsert(u8*, void*, u8*) { g_ctsInserted++; return 1; }
static const void* g_ctsFound;  // what the fake Find returns (NULL = not found)
static bool __fastcall CtsFind(void*, void*, const void*, void** out) { *out = (void*)g_ctsFound; return g_ctsFound != NULL; }

static void TestCasTrim() {
    static_assert(offsetof(Ts3nCounters, ctMode) == 0x5C54 && offsetof(Ts3nCounters, ctOtherThread) == 0x5C78 &&
                      sizeof(Ts3nCounters) == 5919 * 4,
                  "counters v909 layout after v908 (docs/registry.md)");
    Ts3nCounters& z = ts3native_counters;
    const CtEraseFn erase0 = g_ctErase;
    const DWORD vt0[2] = {g_ctVt[0], g_ctVt[1]}, age0 = g_ctAgeMs;
    assert(CtAlloc());
    g_ctErase = &CtsErase, g_ctInsert = &CtsInsert, g_ctFind = &CtsFind;
    g_ctVt[0] = (DWORD)(uintptr_t)g_ctsVt[0], g_ctVt[1] = (DWORD)(uintptr_t)g_ctsVt[1];
    g_ctAgeMs = 60000, g_ctBuckets = 2, g_ctMaxErase = 8, g_ctCursor = 0;
    // A: refs 2, old (the trim target); B: refs 3 (a SimBuilder part holds it); C: refs 2 but found 10 s ago; D: other type
    const LONG refs[4] = {2, 3, 2, 2};
    for (int i = 0; i < 4; i++) {
        g_ctsComp[i][0] = i == 3 ? 0x12345678 : (DWORD)(uintptr_t)g_ctsVt[i & 1], g_ctsComp[i][1] = (DWORD)refs[i] << 8 | 1;
        memset(g_ctsNode[i], 0, sizeof(g_ctsNode[i]));
        *(DWORD*)g_ctsNode[i] = 0xA0 + i, *(DWORD*)(g_ctsNode[i] + 8) = 0x033A1435;
        *(DWORD*)(g_ctsNode[i] + 0x10) = (DWORD)(uintptr_t)g_ctsComp[i];
    }
    *(DWORD*)(g_ctsNode[0] + 0x18) = (DWORD)(uintptr_t)g_ctsNode[1], *(DWORD*)(g_ctsNode[1] + 0x18) = 0;  // bucket 0: A -> B
    *(DWORD*)(g_ctsNode[2] + 0x18) = (DWORD)(uintptr_t)g_ctsNode[3], *(DWORD*)(g_ctsNode[3] + 0x18) = 0;  // bucket 1: C -> D
    g_ctsBkt[0] = (DWORD)(uintptr_t)g_ctsNode[0], g_ctsBkt[1] = (DWORD)(uintptr_t)g_ctsNode[2];
    *(DWORD*)(g_ctsSvc + 0x108) = (DWORD)(uintptr_t)g_ctsBkt, *(DWORD*)(g_ctsSvc + 0x10C) = 2, *(DWORD*)(g_ctsSvc + 0x110) = 4;
    memset((void*)&z.ctMode, 0, 10 * sizeof(LONG));
    const DWORD t0 = 1000000;

    // 1. mode 0: the hooks pass through, nothing counted
    assert(CtInsertHook(g_ctsSvc, NULL, (u8*)g_ctsComp[0]) == 1 && g_ctsInserted == 1 && !z.ctInserts && !z.ctScanned);
    // 2. mode 2 (count only), first walk at t0: all three compositors seen (D is not one), none old yet
    z.ctMode = 2;
    CtStep(g_ctsSvc, t0);
    assert(z.ctScanned == 4 && !z.ctCand && !z.ctFull);
    // C is found at t0 + 50 s; the walk at t0 + 61 s marks only A (B has 3 references, C was used 11 s ago)
    g_ctsFound = g_ctsComp[2];
    void* out = NULL;
    assert(CtFindHook(NULL, NULL, "key", &out) && out == g_ctsComp[2] && !z.ctWouldMiss);  // (GetTickCount: C's used is "now")
    CtSlot((DWORD)(uintptr_t)g_ctsComp[2], false)->used = t0 + 50000;
    CtStep(g_ctsSvc, t0 + 61000);
    assert(z.ctCand == 1 && CtSlot((DWORD)(uintptr_t)g_ctsComp[0], false)->cand == t0 + 61000);
    assert(!CtSlot((DWORD)(uintptr_t)g_ctsComp[1], false)->cand && !CtSlot((DWORD)(uintptr_t)g_ctsComp[2], false)->cand);
    assert(!g_ctsErased && *(DWORD*)(g_ctsSvc + 0x110) == 4);  // count only
    // a find of the marked A: one "would miss", the mark is dropped and A counts as used
    g_ctsFound = g_ctsComp[0];
    assert(CtFindHook(NULL, NULL, "key", &out) && z.ctWouldMiss == 1 && !CtSlot((DWORD)(uintptr_t)g_ctsComp[0], false)->cand);
    // 3. mode 1 (trim): A unused again for > 60 s is erased with the (fake) EA erase; B, C, D stay
    z.ctMode = 1, g_ctsFound = NULL;
    CtSlot((DWORD)(uintptr_t)g_ctsComp[0], false)->used = t0;
    CtSlot((DWORD)(uintptr_t)g_ctsComp[2], false)->used = t0 + 200000;
    CtStep(g_ctsSvc, t0 + 230000);
    assert(g_ctsErased == 1 && z.ctErased == 1 && *(DWORD*)(g_ctsSvc + 0x110) == 3 && g_ctsBkt[0] == (DWORD)(uintptr_t)g_ctsNode[1]);
    assert(!z.ctMissed);  // the erase's own failed cache scan
    // a failed find of A's key counts as missed; another key does not
    g_ctsFound = NULL;
    u8 keyA[16] = {}, keyX[16] = {};
    *(DWORD*)keyA = 0xA0, *(DWORD*)(keyA + 8) = 0x033A1435, *(DWORD*)keyX = 0xEE;
    assert(!CtFindHook(NULL, NULL, keyA, &out) && z.ctMissed == 1);
    assert(!CtFindHook(NULL, NULL, keyX, &out) && z.ctMissed == 1);
    CtFound(false, keyA, NULL, CT_SCAN_RET);  // the register / unregister scan's failed find of A's key: not a miss
    assert(z.ctMissed == 1);
    // 4. the insert hook (count mode): counts, walks on its first thread only
    g_ctTid = 0, z.ctMode = 2;
    const LONG scanned = z.ctScanned;
    assert(CtInsertHook(g_ctsSvc, NULL, (u8*)g_ctsComp[1]) == 1 && z.ctInserts == 1 && z.ctScanned > scanned && g_ctTid == GetCurrentThreadId());
    g_ctTid = GetCurrentThreadId() + 1;
    assert(CtInsertHook(g_ctsSvc, NULL, NULL) == 1 && z.ctInserts == 2 && z.ctOtherThread == 1);
    // 5. a walk that faults (an unreadable node) turns the mode off
    g_ctTid = 0, g_ctCursor = 0, z.ctMode = 2;
    g_ctsBkt[0] = 0x10;
    CtStep(g_ctsSvc, t0 + 300000);
    assert(z.ctMode == 0);
    CtLog(g_ctsSvc);
    printf("cas trim: selftest ok (walk, marks, would-miss, EA-erase stand-in, missed, other thread, fault)\n");

    memset((void*)&z.ctMode, 0, 10 * sizeof(LONG));
    memset(g_ct, 0, CT_SLOTS * sizeof(CtEnt)), memset((void*)g_ctGone, 0, CT_GONE * 4);
    memset((void*)g_ctAge, 0, sizeof(g_ctAge)), g_ctEvents = 0, g_ctTid = 0, g_ctCursor = 0;
    g_ctErase = erase0, g_ctInsert = NULL, g_ctFind = NULL, g_ctVt[0] = vt0[0], g_ctVt[1] = vt0[1], g_ctAgeMs = age0;
    g_ctBuckets = 16;
}
