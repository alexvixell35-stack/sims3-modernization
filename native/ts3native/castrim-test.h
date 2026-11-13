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
static DWORD g_ctsDbKey;  // the first dword of the one key the fake manager's database holds (0: none)
static void* __fastcall CtsDb(void*, void*, const void* k) { return g_ctsDbKey && *(const DWORD*)k == g_ctsDbKey ? (void*)1 : NULL; }
static DWORD g_ctsRmVt[0x50 / 4], g_ctsRm[1];
static void* g_ctsRmPtr;
static bool __fastcall CtsFind(void*, void*, const void*, void** out) { *out = (void*)g_ctsFound; return g_ctsFound != NULL; }
static DWORD g_ctsNew[4];  // a new compositor (not in the map) that names C, like B
static void CtsKids(DWORD o) {  // B and the new one name C (its node's key) as a child
    if (o == (DWORD)(uintptr_t)g_ctsComp[1] || o == (DWORD)(uintptr_t)g_ctsNew) CtNmAdd(g_ctsNode[2]);
}

static void TestCasTrim() {
    static_assert(offsetof(Ts3nCounters, ctMode) == 0x5CF0 && offsetof(Ts3nCounters, ctOtherThread) == 0x5D14 &&
                      sizeof(Ts3nCounters) == 6314 * 4,
                  "counters v912 layout after v908 and the held block (docs/registry.md)");
    Ts3nCounters& z = ts3native_counters;
    const CtEraseFn erase0 = g_ctErase;
    const DWORD vt0[2] = {g_ctVt[0], g_ctVt[1]}, age0 = g_ctAgeMs;
    const bool named0 = g_ctNamed, needDb0 = g_ctNeedDb, async0 = g_ctAsync;
    g_ctNamed = false, g_ctNeedDb = true, g_ctAsync = false;  // steps 2-3: NeedDb alone
    assert(CtAlloc());
    const DWORD rmAt0 = g_ctRmAt, rmVt0 = g_ctRmVt;
    g_ctsRmVt[0x44 / 4] = (DWORD)(uintptr_t)&CtsDb, g_ctsRm[0] = (DWORD)(uintptr_t)g_ctsRmVt, g_ctsRmPtr = g_ctsRm;
    g_ctRmAt = (DWORD)(uintptr_t)&g_ctsRmPtr, g_ctRmVt = (DWORD)(uintptr_t)g_ctsRmVt;
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
    // C is found at t0 + 50 s; at t0 + 61 s only A is old (B has 3 references, C was used 11 s ago), but no database holds
    // its key (NeedDb): kept. At t0 + 122 s a database has it: marked; C is old by then, with no database: kept
    assert(g_ctNeedDb);
    g_ctsFound = g_ctsComp[2];
    void* out = NULL;
    assert(CtFindHook(NULL, NULL, "key", &out) && out == g_ctsComp[2] && !z.ctWouldMiss);  // (GetTickCount: C's used is "now")
    CtSlot((DWORD)(uintptr_t)g_ctsComp[2], false)->used = t0 + 50000;
    CtStep(g_ctsSvc, t0 + 61000);
    assert(!z.ctCand && g_ctMarkDb[0] == 1 && CtSlot((DWORD)(uintptr_t)g_ctsComp[0], false)->dbNo);
    g_ctsDbKey = 0xA0;
    CtStep(g_ctsSvc, t0 + 100000);
    assert(!z.ctCand && g_ctMarkDb[0] == 1 && !g_ctMarkDb[1]);  // not asked again before AgeSec
    CtStep(g_ctsSvc, t0 + 122000);
    assert(z.ctCand == 1 && CtSlot((DWORD)(uintptr_t)g_ctsComp[0], false)->cand == t0 + 122000 && g_ctMarkDb[1] == 1 && g_ctMarkDb[0] == 2);
    assert(!CtSlot((DWORD)(uintptr_t)g_ctsComp[1], false)->cand && !CtSlot((DWORD)(uintptr_t)g_ctsComp[2], false)->cand);
    assert(!g_ctsErased && *(DWORD*)(g_ctsSvc + 0x110) == 4);  // count only
    // a find of the marked A: one "would miss", the mark is dropped and A counts as used
    g_ctsFound = g_ctsComp[0];
    assert(CtFindHook(NULL, NULL, "key", &out) && z.ctWouldMiss == 1 && !CtSlot((DWORD)(uintptr_t)g_ctsComp[0], false)->cand);
    assert(g_ctDbNo == 1 && !g_ctDbYes);  // the would-miss asked the (fake) manager: no database has the key
    // 3. mode 1 (trim): A unused again for > 60 s is erased with the (fake) EA erase; B, C, D stay
    z.ctMode = 1, g_ctsFound = NULL;
    CtSlot((DWORD)(uintptr_t)g_ctsComp[0], false)->used = t0;
    CtSlot((DWORD)(uintptr_t)g_ctsComp[2], false)->used = t0 + 200000;
    CtStep(g_ctsSvc, t0 + 230000);
    assert(g_ctsErased == 1 && z.ctErased == 1 && *(DWORD*)(g_ctsSvc + 0x110) == 3 && g_ctsBkt[0] == (DWORD)(uintptr_t)g_ctsNode[1]);
    assert(!z.ctMissed);  // the erase's own failed cache scan
    // a failed find of A's key while a database holds it: loaded again, not missed; once no database has it: missed
    g_ctsFound = NULL;
    u8 keyA[16] = {}, keyX[16] = {};
    *(DWORD*)keyA = 0xA0, *(DWORD*)(keyA + 8) = 0x033A1435, *(DWORD*)keyX = 0xEE;
    assert(!CtFindHook(NULL, NULL, keyA, &out) && !z.ctMissed && g_ctReload == 1);
    g_ctsDbKey = 0;
    assert(!CtFindHook(NULL, NULL, keyA, &out) && z.ctMissed == 1 && g_ctReload == 1);
    assert(!CtFindHook(NULL, NULL, keyX, &out) && z.ctMissed == 1);  // a key never trimmed
    CtFound(false, keyA, NULL, CT_SCAN_RET, NULL);  // the register / unregister scan's failed find of A's key: not a miss
    CtFound(false, keyA, NULL, CT_LOAD_RET, NULL);  // the parent's Load builds it again from its own data
    assert(z.ctMissed == 1 && g_ctReload == 2);
    assert(g_ctGap[0] + g_ctGap[1] + g_ctGap[2] + g_ctGap[3] + g_ctGap[4] + g_ctGap[5] >= 2);  // C's and A's finds above
    // 3b. Named (count mode, NeedDb off; 2 buckets a step = one full cycle a step): B (3 references) names C, so old C stays
    // unmarked; A, named by none, is marked. Once B is old and marked it names nothing: C keeps the last full cycle's name
    // one more step, then is marked. A new compositor naming C at its insert keeps C again
    z.ctMode = 2, g_ctNeedDb = false, g_ctNamed = true, g_ctKidsFn = &CtsKids, g_ctCursor = 0, g_ctCycles = 0, g_ctCyB = 0;
    memset(g_ct, 0, CT_SLOTS * sizeof(CtEnt));
    *(DWORD*)(g_ctsNode[0] + 0x18) = (DWORD)(uintptr_t)g_ctsNode[1], g_ctsBkt[0] = (DWORD)(uintptr_t)g_ctsNode[0];  // A back
    *(DWORD*)(g_ctsSvc + 0x110) = 4;
    const LONG cand0 = z.ctCand;
    const DWORD t1 = t0 + 400000;
    CtStep(g_ctsSvc, t1);
    assert(g_ctCycles == 1 && g_ctKids == 1);
    CtStep(g_ctsSvc, t1 + 61000);
    CtEnt *ta = CtSlot((DWORD)(uintptr_t)g_ctsComp[0], false), *tb = CtSlot((DWORD)(uintptr_t)g_ctsComp[1], false),
          *tc = CtSlot((DWORD)(uintptr_t)g_ctsComp[2], false);
    assert(ta->cand && !tc->cand && z.ctCand == cand0 + 1 && g_ctKept == 1);
    g_ctsComp[1][1] = 2 << 8 | 1;  // B at 2 references: old
    CtStep(g_ctsSvc, t1 + 122000);
    assert(tb->cand && !tc->cand);  // B named C before its own mark
    CtStep(g_ctsSvc, t1 + 183000);
    assert(!tc->cand);  // the last full cycle still has B's name for C
    CtStep(g_ctsSvc, t1 + 244000);
    assert(tc->cand && z.ctCand == cand0 + 3);
    g_ctsNew[0] = (DWORD)(uintptr_t)g_ctsVt[1], g_ctsNew[1] = 3 << 8 | 1, g_ctTid = 0, g_ctBuckets = 0;
    assert(CtInsertHook(g_ctsSvc, NULL, (u8*)g_ctsNew) == 1);  // walks no bucket: only the insert reads its names
    g_ctBuckets = 2;
    CtStep(g_ctsSvc, t1 + 250000);
    assert(!tc->cand);
    z.ctInserts = 0, g_ctsComp[1][1] = 3 << 8 | 1;
    // 3c. Async: old A, once the async loader has found it (an outfit's part), stays unmarked; with Async off it is marked
    g_ctAsync = true;
    memset(g_ct, 0, CT_SLOTS * sizeof(CtEnt));
    const DWORD t2 = t1 + 300000;
    CtStep(g_ctsSvc, t2);
    ta = CtSlot((DWORD)(uintptr_t)g_ctsComp[0], false);
    void* pa = g_ctsComp[0];
    CtFound(true, keyA, &pa, CT_ASYNC_RET, NULL);
    assert(ta->from == 1 && g_ctFromN[0] == 1);
    ta->used = t2;
    CtStep(g_ctsSvc, t2 + 61000);
    assert(!ta->cand && g_ctKeptAs >= 1);
    g_ctAsync = false;
    CtStep(g_ctsSvc, t2 + 122000);
    assert(ta->cand);
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
    printf("cas trim: selftest ok (walk, marks, NeedDb, Named + its cycles + insert, Async, would-miss, EA-erase stand-in, missed / loaded again, other thread, fault)\n");

    memset((void*)&z.ctMode, 0, 10 * sizeof(LONG));
    memset(g_ct, 0, CT_SLOTS * sizeof(CtEnt)), memset((void*)g_ctGone, 0, CT_GONE * 4);
    memset((void*)g_ctAge, 0, sizeof(g_ctAge)), memset((void*)g_ctGap, 0, sizeof(g_ctGap)), g_ctEvents = 0, g_ctTid = 0, g_ctCursor = 0;
    g_ctErase = erase0, g_ctInsert = NULL, g_ctFind = NULL, g_ctVt[0] = vt0[0], g_ctVt[1] = vt0[1], g_ctAgeMs = age0;
    g_ctBuckets = 16, g_ctRmAt = rmAt0, g_ctRmVt = rmVt0, g_ctDbYes = g_ctDbNo = 0, g_ctsDbKey = 0;
    g_ctMarkDb[0] = g_ctMarkDb[1] = g_ctReload = 0, g_ctDbQpc = 0;
    memset(g_ctNm[0], 0, CT_NAMED * 4), memset(g_ctNm[1], 0, CT_NAMED * 4), g_ctNmFull[0] = g_ctNmFull[1] = false, g_ctKidsFn = CtKidsEa;
    g_ctCycles = g_ctKids = g_ctKept = 0, g_ctCyB = g_ctCyNb = g_ctCyAt = g_ctCyMs = 0, g_ctNamed = named0, g_ctNeedDb = needDb0;
    g_ctAsync = async0, g_ctKeptAs = 0, memset((void*)g_ctFromN, 0, sizeof(g_ctFromN));
}

// TestCasTrimSweep: [CasTrim] Mode 3 / 4 (docs/539 §6, ledger §446) through the real hooks, in EA's order: ts3fixes' ops, the script
// marks, the save hook, the writer hook, ops 17 and 18. Fake registry, live set, outfit closure, children; no game code runs.
enum { CSW_A, CSW_LIVE, CSW_KID, CSW_GRAND, CSW_YOUNG, CSW_YKID, CSW_HH, CSW_ASYNC, CSW_REF3, CSW_OTHER, CSW_LOOSE, CSW_N };
static u8 g_cswSvc[0x114], g_cswNode[CSW_N][0x1C], g_cswLiveNode[0x1C], g_cswLive[0x20];
static DWORD g_cswBkt[4], g_cswLiveBkt[1], g_cswComp[CSW_N][4], g_cswErased;
static bool g_cswFault;
static const u8* cswKey(int i) { return g_cswNode[i]; }
static DWORD cswO(int i) { return (DWORD)(uintptr_t)g_cswComp[i]; }
static void CswKids(DWORD o) {  // LIVE names KID, KID names GRAND (a chain through 2-reference survivors), YOUNG names YKID, A names LOOSE
    if (g_cswFault) *(volatile DWORD*)16 = 0;
    if (o == cswO(CSW_LIVE)) CtNmAdd(cswKey(CSW_KID));
    if (o == cswO(CSW_KID)) CtNmAdd(cswKey(CSW_GRAND));
    if (o == cswO(CSW_YOUNG)) CtNmAdd(cswKey(CSW_YKID));
    if (o == cswO(CSW_A)) CtNmAdd(cswKey(CSW_LOOSE));
}
static void CswKeep(u8*, const u8* live) { if (g_ctKeepN) CtKeepAdd(cswKey(CSW_HH), live); }  // the closure of the pushed outfit: one compositor
static bool __fastcall CswErase(u8* svc, void*, const void* key) {
    void* o = NULL;
    CtFindHook(NULL, NULL, key, &o);
    DWORD* b = *(DWORD**)(svc + 0x108);
    const DWORD* k = (const DWORD*)key;
    for (DWORD* p = &b[(k[0] ^ k[3]) % *(DWORD*)(svc + 0x10C)]; *p; p = (DWORD*)(uintptr_t)(*p + 0x18))
        if (!memcmp((const void*)(uintptr_t)*p, key, 16)) {
            *p = *(DWORD*)(uintptr_t)(*p + 0x18), (*(DWORD*)(svc + 0x110))--, g_cswErased++;
            return true;
        }
    return false;
}
static bool g_cswSaveOk = true, g_cswWriteOk = true;
static const u8* g_cswLiveArg;
static bool __fastcall CswWrite(u8*, void*, void*, void*, void*, const u8*) { return g_cswWriteOk; }
static bool __fastcall CswSave(u8* svc, void*, void*) { CtWriteHook(svc, NULL, NULL, NULL, NULL, g_cswLiveArg); return g_cswSaveOk; }
static void __fastcall CswMark(u8*, void*, const void*) {}
static void CswReset() {  // the fixture as a save finds it: everything idle for 2 min
    Ts3nCounters& z = ts3native_counters;
    const LONG refs[CSW_N] = {2, 3, 2, 2, 2, 2, 2, 2, 3, 2, 2};  // LIVE: 3, its reference from the live set included
    memset(g_ct, 0, CT_SLOTS * sizeof(CtEnt)), memset(g_cswBkt, 0, sizeof(g_cswBkt));
    const DWORD now = GetTickCount();
    for (int i = 0; i < CSW_N; i++) {
        g_cswComp[i][0] = i == CSW_OTHER ? 0x12345678 : (DWORD)(uintptr_t)g_ctsVt[i & 1], g_cswComp[i][1] = (DWORD)refs[i] << 8 | 1;
        memset(g_cswNode[i], 0, 0x1C);
        *(DWORD*)g_cswNode[i] = 0xC00 + i, *(DWORD*)(g_cswNode[i] + 8) = 0x033A1435, *(DWORD*)(g_cswNode[i] + 12) = 0x77;
        *(DWORD*)(g_cswNode[i] + 0x10) = cswO(i);
        DWORD* slot = &g_cswBkt[(*(DWORD*)g_cswNode[i] ^ 0x77) % 4];
        *(DWORD*)(g_cswNode[i] + 0x18) = *slot, *slot = (DWORD)(uintptr_t)g_cswNode[i];
        if (i != CSW_OTHER) { CtEnt* t = CtSlot(cswO(i), true); t->seen = t->used = now - 120000; }
    }
    CtSlot(cswO(CSW_YOUNG), false)->used = now - 5000;
    InterlockedOr(&CtSlot(cswO(CSW_ASYNC), false)->from, 1);
    *(DWORD*)(g_cswSvc + 0x108) = (DWORD)(uintptr_t)g_cswBkt, *(DWORD*)(g_cswSvc + 0x10C) = 4, *(DWORD*)(g_cswSvc + 0x110) = CSW_N;
    memcpy(g_cswLiveNode, g_cswNode[CSW_LIVE], 0x18), *(DWORD*)(g_cswLiveNode + 0x18) = 0;
    g_cswLiveBkt[0] = (DWORD)(uintptr_t)g_cswLiveNode;
    memset(g_cswLive, 0, sizeof(g_cswLive));
    *(DWORD*)(g_cswLive + 4) = (DWORD)(uintptr_t)g_cswLiveBkt, *(DWORD*)(g_cswLive + 8) = 1, *(DWORD*)(g_cswLive + 12) = 1;
    g_cswLiveArg = g_cswLive, g_cswErased = 0, g_cswSaveOk = g_cswWriteOk = true, g_cswFault = false, g_ctLate = 0;
    memset((void*)&z.ctSaves, 0, 26 * sizeof(LONG)), z.ctWouldMiss = z.ctMissed = z.ctInserts = 0;
    memset((void*)g_ctGone, 0, CT_GONE * 4), g_ctFenceAt = 0, g_ctKeepN = 0, g_ctKeepOk = false, g_ctMarksF = 0, g_ctPendN = 0, g_ctPendGo = false, g_ctAfterSave = false;
}
static void CswBegin(DWORD stand, bool push, DWORD done) {  // ts3fixes before a save
    assert(CtOp(14, stand) == 1);
    if (push) for (int i = 0; i < 4; i++) assert(CtOp(15, ((const DWORD*)cswKey(CSW_HH))[i]) == 1);
    assert(CtOp(16, done) == 1);
}

__declspec(naked) static void __fastcall CswFbEnter(DWORD) {  // a frame with the flag at [esp+0x13], a jmp in, the "trampoline" = back
    __asm {
        lea eax, back
        mov g_ctFbOrig, eax
        sub esp, 0x20
        mov byte ptr [esp + 0x13], cl
        jmp CtFbStub
    back:
        add esp, 0x20
        ret
    }
}

static void TestCasTrimSweep() {
    static_assert(offsetof(Ts3nCounters, ctSaves) == 0x5D80 && offsetof(Ts3nCounters, ctBlendKB) == 0x5DE4 && sizeof(Ts3nCounters) == 6314 * 4,
                  "counters v171 layout (docs/registry.md)");
    Ts3nCounters& z = ts3native_counters;
    const CtEraseFn erase0 = g_ctErase;
    const DWORD vt0[2] = {g_ctVt[0], g_ctVt[1]}, minAge0 = g_ctMinAgeMs, svcAt0 = g_ctSvcAt;
    const bool named0 = g_ctNamed, async0 = g_ctAsync, probe0 = g_ctProbe;
    assert(CtAlloc());
    g_ctErase = &CswErase, g_ctFind = &CtsFind, g_ctInsert = &CtsInsert, g_ctSave = &CswSave, g_ctWrite = &CswWrite, g_ctMark = &CswMark;
    g_ctVt[0] = (DWORD)(uintptr_t)g_ctsVt[0], g_ctVt[1] = (DWORD)(uintptr_t)g_ctsVt[1];
    g_ctMinAgeMs = 60000, g_ctNamed = g_ctAsync = true, g_ctKidsFn = &CswKids, g_ctKeepFn = &CswKeep, g_ctMaxErase = 8, g_ctsFound = NULL;
    g_ctTid = GetCurrentThreadId();
    const DWORD rmAt0 = g_ctRmAt;
    g_ctsRmPtr = NULL, g_ctRmAt = (DWORD)(uintptr_t)&g_ctsRmPtr;  // no manager: "not asked"

    // modes 0-2: the ops answer 0, the save hooks only pass through; op 19 says the hooks are in
    CswReset();
    z.ctMode = 2;
    assert(CtOp(19, 0) == 1 && CtOp(14, 0) == 0 && CtOp(18, 0) == 0);
    assert(CtSaveHook(g_cswSvc, NULL, NULL) && !z.ctSaves && !z.ctDoomed);

    // Mode 4 full flow: the rules decide; nothing is erased, nothing pending. A, LOOSE and nobody else are doomed
    CswReset();
    z.ctMode = 4;
    CswBegin(0, true, 1);
    CtMarkHook(g_cswSvc, NULL, cswKey(CSW_LIVE));
    assert(CtSaveHook(g_cswSvc, NULL, NULL));
    assert(z.ctSaves == 1 && z.ctLive == 1 && z.ctRegistry == CSW_N && z.ctMarks == 1 && z.ctMarksFence == 1 && !z.ctRefused);
    assert(z.ctDoomed == 2 && CtSlot(cswO(CSW_A), false)->cand && CtSlot(cswO(CSW_LOOSE), false)->cand);
    for (int i : {CSW_LIVE, CSW_KID, CSW_GRAND, CSW_YOUNG, CSW_YKID, CSW_HH, CSW_ASYNC, CSW_REF3}) assert(!CtSlot(cswO(i), false)->cand);
    assert(z.ctKeptHousehold == 1 && z.ctKeptYoung == 1 && z.ctKeptAsync == 1 && z.ctKeptNamed == 3);  // KID, GRAND (fixed point), YKID
    assert(z.ctKeepMissed == 1);  // HH is on the list but not in EA's set: logged, still kept (the belt)
    assert(CtOp(17, 1) == 0 && CtOp(18, 0) == 0 && !g_cswErased && !z.ctSwept);
    // P4: a doomed key found later: would miss; through the async loader it also counts as an outfit load's
    void* pa = g_cswComp[CSW_A];
    CtFound(true, cswKey(CSW_A), &pa, CT_ASYNC_RET, NULL);
    assert(z.ctWouldMiss == 1 && z.ctDoomedAsync == 1 && !CtSlot(cswO(CSW_A), false)->cand);
    void* pl = g_cswComp[CSW_LOOSE];
    CtFound(true, cswKey(CSW_LOOSE), &pl, CT_GET_RET, NULL);
    assert(z.ctWouldMiss == 2 && z.ctDoomedAsync == 1);
    // P5 in mode 4: a pass after the native save is counted; no list and no marks: "would refuse", counted, still scanned
    CtMarkHook(g_cswSvc, NULL, cswKey(CSW_LIVE));
    assert(g_ctMarksLate == 1);
    CtOp(17, 1);
    assert(CtSaveHook(g_cswSvc, NULL, NULL) && z.ctRefused == 1 && z.ctSaves == 2);

    // Mode 3, order or state not met: every one refuses, nothing doomed, nothing to pump. The bad order is the P5 case
    z.ctMode = 3;
    const auto refused = [&](const char* why) {
        assert(CtSaveHook(g_cswSvc, NULL, NULL) && z.ctRefused == 1 && !z.ctDoomed && !g_ctPendN);
        assert(CtOp(17, 1) == 0 && CtOp(18, 0) == 0 && !g_cswErased);
        printf("cas trim: refused as it must: %s\n", why);
    };
    CswReset(), z.ctMode = 3;  // no keep list at all (a save ts3fixes did not see, or an old package)
    refused("no keep list");
    CswReset(), z.ctMode = 3;
    CswBegin(0, true, 1);  // the marks ran before the fence, or after the native save: none in between
    refused("P5 order: no mark before the native save");
    CswReset(), z.ctMode = 3;
    CswBegin(0, true, 0);  // a script fault or an unknown state during the push
    CtMarkHook(g_cswSvc, NULL, cswKey(CSW_LIVE));
    refused("keep list incomplete");
    CswReset(), z.ctMode = 3;
    CswBegin(1, false, 1);  // CAS or Edit Town open
    CtMarkHook(g_cswSvc, NULL, cswKey(CSW_LIVE));
    refused("CAS or Edit Town");
    CswReset(), z.ctMode = 3;
    CswBegin(0, true, 1);
    CtMarkHook(g_cswSvc, NULL, cswKey(CSW_LIVE));
    g_ctTid = GetCurrentThreadId() + 1;  // the save is not on the insert thread
    refused("save off the insert thread");
    g_ctTid = GetCurrentThreadId();
    CswReset(), z.ctMode = 3;
    CswBegin(0, true, 1);
    CtMarkHook(g_cswSvc, NULL, cswKey(CSW_LIVE));
    g_ctFenceAt = GetTickCount() - CT_FENCE_MS - 1000;  // the list of a save that never ran
    refused("stale keep list");
    CswReset(), z.ctMode = 3;
    CswBegin(0, true, 1);
    CtMarkHook(g_cswSvc, NULL, cswKey(CSW_LIVE));
    *(DWORD*)(g_cswLive + 12) = 0;
    refused("empty live set");

    // a failed writer or a failed save sweeps nothing
    CswReset(), z.ctMode = 3;
    CswBegin(0, true, 1);
    CtMarkHook(g_cswSvc, NULL, cswKey(CSW_LIVE));
    g_cswWriteOk = false;
    assert(!CtSaveHook(g_cswSvc, NULL, NULL) || !z.ctDoomed);
    assert(CtOp(17, 1) == 0);
    CswReset(), z.ctMode = 3;
    CswBegin(0, true, 1);
    CtMarkHook(g_cswSvc, NULL, cswKey(CSW_LIVE));
    g_cswSaveOk = false;
    assert(!CtSaveHook(g_cswSvc, NULL, NULL) && z.ctDoomed == 2 && CtOp(17, 0) == 0 && !g_cswErased);

    // Mode 3 sweep: nothing during the save; after it, in batches. LOOSE was found after the save (spared, a would-miss), so only A goes
    CswReset(), z.ctMode = 3;
    CswBegin(0, true, 1);
    CtMarkHook(g_cswSvc, NULL, cswKey(CSW_LIVE));
    assert(CtSaveHook(g_cswSvc, NULL, NULL) && z.ctDoomed == 2 && g_ctPendN == 2 && !g_cswErased);
    assert(CtOp(18, 0) == 0 && !g_cswErased);  // the pump before the save returned does nothing
    void* pl2 = g_cswComp[CSW_LOOSE];
    CtFound(true, cswKey(CSW_LOOSE), &pl2, CT_GET_RET, NULL);
    assert(z.ctWouldMiss == 1 && CtOp(17, 1) == 1);
    g_ctMaxErase = 1;
    while (CtOp(18, 0)) {}
    assert(z.ctSwept == 1 && g_cswErased == 1 && g_ctLate == 1 && !CtMapFind(g_cswBkt, 4, cswKey(CSW_A)) && CtMapFind(g_cswBkt, 4, cswKey(CSW_LOOSE)));
    assert(*(DWORD*)(g_cswSvc + 0x110) == CSW_N - 1);
    // MISSED works on the 2^18 erased-key table: a failed find of A's key by anyone but EA's own scan
    assert(CtFindHook(NULL, NULL, cswKey(CSW_A), &pa) == false);
    CtFound(false, cswKey(CSW_A), NULL, CT_SCAN_RET, NULL);
    assert(z.ctMissed == 1);

    // re-checks at erase time: a compositor that picked up a third reference, or left the registry, stays out
    CswReset(), z.ctMode = 3, g_ctMaxErase = 8;
    CswBegin(0, true, 1);
    CtMarkHook(g_cswSvc, NULL, cswKey(CSW_LIVE));
    assert(CtSaveHook(g_cswSvc, NULL, NULL));
    g_cswComp[CSW_A][1] = 3 << 8 | 1;
    assert(CtOp(17, 1) == 1 && CtOp(18, 0) == 0 && z.ctSwept == 1 && CtMapFind(g_cswBkt, 4, cswKey(CSW_A)));
    // a new save clears what the last one left
    CswReset(), z.ctMode = 3;
    CswBegin(0, true, 1);
    CtMarkHook(g_cswSvc, NULL, cswKey(CSW_LIVE));
    assert(CtSaveHook(g_cswSvc, NULL, NULL) && g_ctPendN == 2);
    CswBegin(0, true, 1);
    assert(!g_ctPendN && CtOp(18, 0) == 0);
    // the pump off the insert thread drops the batch
    CswReset(), z.ctMode = 3;
    CswBegin(0, true, 1);
    CtMarkHook(g_cswSvc, NULL, cswKey(CSW_LIVE));
    assert(CtSaveHook(g_cswSvc, NULL, NULL) && CtOp(17, 1) == 1);
    g_ctTid = GetCurrentThreadId() + 1;
    assert(CtOp(18, 0) == 0 && !g_cswErased && !g_ctPendN);
    g_ctTid = GetCurrentThreadId();
    // a full keep table sweeps nothing
    CswReset(), z.ctMode = 3;
    CswBegin(0, true, 1);
    CtMarkHook(g_cswSvc, NULL, cswKey(CSW_LIVE));
    g_ctKeepFn = [](u8*, const u8*) { g_ctNmFull[1] = true; };
    assert(CtSaveHook(g_cswSvc, NULL, NULL) && !z.ctDoomed && !g_ctPendN);
    g_ctKeepFn = &CswKeep;
    // a fault in the sweep turns the mode off
    CswReset(), z.ctMode = 3;
    CswBegin(0, true, 1);
    CtMarkHook(g_cswSvc, NULL, cswKey(CSW_LIVE));
    g_cswFault = true;
    assert(CtSaveHook(g_cswSvc, NULL, NULL) && !z.ctMode && !g_ctPendN);
    g_cswFault = false;
    // docs/539 §15.7 Quarantine=1: the first erase takes a reference through the (fake) AddRef and is held; the full list stops the
    // sweep (the second candidate stays in the registry) for good; the per-save scan finds 0x40A00000 at +0x28 of a held block
    static DWORD quar[1], blk[0x68 / 4];
    g_ctQuar = quar, g_ctQuarCap = 1, g_ctQuarN = 0, g_ctQuarFull = false, g_ctQuarHitLog = 0;
    g_ctAddRefFn = [](DWORD o) { ((DWORD*)(uintptr_t)o)[1] += 0x100; };
    CswReset(), z.ctMode = 3, g_ctMaxErase = 8;
    CswBegin(0, true, 1);
    CtMarkHook(g_cswSvc, NULL, cswKey(CSW_LIVE));
    assert(CtSaveHook(g_cswSvc, NULL, NULL) && g_ctPendN == 2 && CtOp(17, 1) == 1 && CtOp(18, 0) == 0);
    assert(z.ctSwept == 1 && g_cswErased == 1 && g_ctQuarN == 1 && g_ctQuarFull && !g_ctPendN && (quar[0] == cswO(CSW_A) || quar[0] == cswO(CSW_LOOSE)));
    assert(((const DWORD*)(uintptr_t)quar[0])[1] == (3 << 8 | 1));
    assert(CtQuarHold(cswO(CSW_KID)) == false && g_ctQuarN == 1);
    quar[0] = (DWORD)(uintptr_t)blk, blk[1] = 3 << 8, blk[0x28 / 4] = 0x40A00000;
    CtQuarScan();
    assert(g_ctQuarHitLog == 1);
    g_ctQuar = NULL, g_ctQuarCap = g_ctQuarN = g_ctQuarHitLog = 0, g_ctQuarFull = false, g_ctAddRefFn = CtAddRefEa;

    // the inserts' sources (P2) and no walk in modes 3 and 4
    CswReset(), z.ctMode = 4;
    assert(CtInsertHook(g_cswSvc, NULL, (u8*)g_cswComp[CSW_KID]) == 1 && z.ctInserts == 1 && !z.ctScanned);
    // P1: names up to the first digit, other past the table; a bad pointer costs nothing
    memset(g_ctNames, 0, sizeof(g_ctNames)), g_ctNameOther = 0;
    CtCountName(L"MakeSim123"), CtCountName(L"MakeSim456"), CtCountName(L"Shower");
    CtCountName((const wchar_t*)16);
    assert(z.ctCacheOutfit == 4 && g_ctNames[0].n == 2 && !strcmp(g_ctNames[0].s, "MakeSim") && g_ctNames[1].n == 1);
    wchar_t nm[4] = L"xA";
    for (int i = 0; i < 60; i++) nm[1] = (wchar_t)(L'A' + i), CtCountName(nm);
    assert(g_ctNameOther == 60 - 46);
    // P3: the sizes, read from a fake service, with a count-0 share
    static u8 outf[2][0x1C];
    static DWORD outB[2], slot;
    memset(outf, 0, sizeof(outf));
    *(DWORD*)(outf[0] + 0x14) = 3, outB[0] = (DWORD)(uintptr_t)outf[0], outB[1] = (DWORD)(uintptr_t)outf[1];
    *(DWORD*)(g_cswSvc + 0xD8) = (DWORD)(uintptr_t)outB, *(DWORD*)(g_cswSvc + 0xDC) = 2, *(DWORD*)(g_cswSvc + 0xE0) = 2;
    slot = (DWORD)(uintptr_t)g_cswSvc, g_ctSvcAt = (DWORD)(uintptr_t)&slot, g_ctProbe = true;
    CtMemLine(1, "test");
    assert(z.ctOutfits == 2 && z.ctOutfit0 == 1 && z.ctRegistry == CSW_N);
    // P6: the stub, entered by a jmp in EA's frame as the detour enters it: the flag at [esp+0x13] is counted, and it goes on to the trampoline
    for (int flag = 0; flag < 2; flag++) {
        const LONG before = z.ctFbFlag;
        void* const orig = g_ctFbOrig;
        CswFbEnter((DWORD)flag);
        g_ctFbOrig = orig;
        assert(z.ctFbFlag == before + flag);
    }
    printf("cas trim: sweep selftest ok (modes 3 and 4 through the hooks: keep rules 1-7, Named fixed point, refusals incl. P5 order, batches after the save, re-checks, missed; probes P1-P6)\n");

    memset((void*)&z.ctSaves, 0, 26 * sizeof(LONG)), z.ctWouldMiss = z.ctMissed = z.ctInserts = 0;
    z.ctMode = z.ctInserts = z.ctWouldMiss = z.ctMissed = z.ctFull = 0;
    memset(g_ct, 0, CT_SLOTS * sizeof(CtEnt)), memset((void*)g_ctGone, 0, CT_GONE * 4), memset(g_ctNames, 0, sizeof(g_ctNames));
    memset(g_ctNm[0], 0, CT_NAMED * 4), memset(g_ctNm[1], 0, CT_NAMED * 4), g_ctNmFull[0] = g_ctNmFull[1] = false;
    memset((void*)g_ctAge, 0, sizeof(g_ctAge)), memset((void*)g_ctGap, 0, sizeof(g_ctGap)), g_ctEvents = 0, g_ctTid = 0, memset((void*)g_ctFromN, 0, sizeof(g_ctFromN));
    g_ctErase = erase0, g_ctInsert = NULL, g_ctFind = NULL, g_ctSave = NULL, g_ctWrite = NULL, g_ctMark = NULL, g_ctVt[0] = vt0[0], g_ctVt[1] = vt0[1];
    g_ctRmAt = rmAt0, g_ctMinAgeMs = minAge0, g_ctSvcAt = svcAt0, g_ctNamed = named0, g_ctAsync = async0, g_ctProbe = probe0, g_ctKidsFn = CtKidsEa, g_ctKeepFn = CtKeepEa;
    g_ctNameOther = 0, g_ctMaxErase = 8, g_ctFenceAt = 0, g_ctKeepN = 0, g_ctKeepOk = false, g_ctMarksF = g_ctMarksS = g_ctMarksLate = 0, g_ctPendN = 0;
    g_ctPendGo = g_ctAfterSave = false, g_ctRefLog = g_ctMissLog = 0, g_ctLate = 0, g_ctsFound = NULL, g_ctsInserted = 0;
}

// docs/539 §12.5: the inventory detector on a fake outfit, entered as EA's call site 0x5EFE07-0x5EFE11 enters it (esi = the
// outfit map node, push 1, push set, ecx = outfit, call): bad slots are counted, the fake EA gets the same arguments and
// the stack is balanced (ret 8); the erase ring and the fault decoder on a fake frame. No game code runs.
static void* g_ctsInvSet;
static DWORD g_ctsInvKids, g_ctsInvOutfit;
static int g_ctsInvN;
static void __fastcall CtsInvEa(void* outfit, void*, void* set, DWORD children) {
    g_ctsInvN++, g_ctsInvSet = set, g_ctsInvKids = children, g_ctsInvOutfit = (DWORD)(uintptr_t)outfit;
}
__declspec(naked) static void __fastcall CtsInvCall(const u8* /*node*/, const u8* /*outfit*/) {
    __asm {
        push esi
        mov esi, ecx
        mov ecx, edx
        push 1
        push 0x1234
        call CtInvStub
        pop esi
        ret
    }
}
static void TestCasTrimInv() {
    const CtInvFn ea0 = g_ctInvEa;
    const DWORD vt0[2] = {g_ctVt[0], g_ctVt[1]};
    assert(CtAlloc());
    static DWORD vt[2][2], good[4], bad[4] = {1}, a0[5], a1[5];
    static u8 recs[2][0x68], outfit[0xC8], node[0x1C], outfit2[0xC8];  // +0xBC / +0xC0: no cells
    g_ctVt[0] = (DWORD)(uintptr_t)vt[0], g_ctVt[1] = (DWORD)(uintptr_t)vt[1];
    good[0] = g_ctVt[1];
    a0[0] = (DWORD)(uintptr_t)good, a1[1] = (DWORD)(uintptr_t)bad;  // the crash: [rec+0x38] slot 1, first dword 1
    *(DWORD*)(recs[0] + 0x14) = *(DWORD*)(recs[0] + 0x38) = *(DWORD*)(recs[1] + 0x14) = (DWORD)(uintptr_t)a0;
    *(DWORD*)(recs[1] + 0x38) = (DWORD)(uintptr_t)a1;
    *(DWORD*)(outfit + 0x98) = (DWORD)(uintptr_t)recs[0], *(DWORD*)(outfit + 0x9C) = (DWORD)(uintptr_t)recs[2];
    memset(node, 0x11, 16), *(DWORD*)(node + 0x10) = (DWORD)(uintptr_t)outfit;
    g_ctInvEa = &CtsInvEa, g_ctInvBad = g_ctInvLogs = g_ctInvGone = 0, g_ctInvCalls = g_ctInvRecs = g_ctInvSlots = g_ctInvBadSave = 0, g_ctInvQpc = 0, g_ctF5 = g_ctF5Logs = 0, g_ctF5Save = 0;
    // one good outfit's worth: 4 slots read, 1 bad, EA called once with (outfit, set, 1), esi kept
    CtsInvCall(node, outfit);
    assert(g_ctsInvN == 1 && g_ctsInvSet == (void*)0x1234 && g_ctsInvKids == 1 && g_ctsInvOutfit == (DWORD)(uintptr_t)outfit && g_ctInvNode == node);
    assert(g_ctInvCalls == 1 && g_ctInvRecs == 2 && g_ctInvSlots == 4 && g_ctInvBad == 1 && g_ctInvBadSave == 1 && !g_ctInvGone);
    assert(g_ctF5 == 1 && g_ctF5Save == 1);  // docs/539 §13.5 item 2: that slot's first dword is 1 (< 0x10000)
    // the ring: an erase of the object that holds the value makes the next bad slot "erased by CasTrim"
    char t[160];
    assert(!CtGoneText((DWORD)(uintptr_t)bad, t, sizeof(t)) && strstr(t, "never erased"));
    CtGoneRec((DWORD)(uintptr_t)bad - 8, 0xABCD);
    assert(CtGoneText((DWORD)(uintptr_t)bad, t, sizeof(t)) && strstr(t, "ERASED by CasTrim") && strstr(t, "+0x8"));
    CtsInvCall(node, outfit);
    assert(g_ctInvBad == 2 && g_ctInvGone == 1 && g_ctsInvN == 2);
    for (DWORD i = 0; i < CT_GRING; i++) CtGoneRec(0x100, 1);  // pushed out of the ring
    assert(!CtGoneText((DWORD)(uintptr_t)bad, t, sizeof(t)));
    // an unreadable value, a null array pointer, a bad record range, a null outfit: logged, never a fault; EA still runs
    a1[1] = 0x10;
    CtsInvCall(node, outfit);
    assert(g_ctInvBad == 3);
    a1[1] = 0, *(DWORD*)(recs[1] + 0x38) = 0;
    CtsInvCall(node, outfit);
    assert(g_ctInvBad == 4);
    *(DWORD*)(outfit2 + 0x98) = (DWORD)(uintptr_t)recs[1], *(DWORD*)(outfit2 + 0x9C) = (DWORD)(uintptr_t)recs[0];
    CtsInvCall(node, outfit2);
    CtsInvCall(node, NULL);
    assert(g_ctInvBad == 6 && g_ctsInvN == 6);
    *(DWORD*)(recs[1] + 0x38) = (DWORD)(uintptr_t)a0;
    CtsInvCall(node, outfit);
    assert(g_ctInvBad == 6 && g_ctInvSlots > 0);
    // the per-save line resets the save's numbers; the session total stays
    CtInvSaveLog();
    assert(!g_ctInvCalls && !g_ctInvSlots && !g_ctInvBadSave && g_ctInvBad == 6);
    // the fault decoder: EA's frame at 0x5E772C (record +0x10, outfit +0x14, end +0x18, return +0x1C, set +0x20, children +0x24)
    static DWORD frame[12];
    frame[4] = (DWORD)(uintptr_t)recs[1], frame[5] = (DWORD)(uintptr_t)outfit, frame[6] = (DWORD)(uintptr_t)recs[2], frame[7] = 0x005EFE16;
    frame[8] = 0x1234, frame[9] = 1;
    CONTEXT c = {};
    c.Eip = 0x005E772C, c.Esp = (DWORD)(uintptr_t)frame, c.Esi = (DWORD)(uintptr_t)bad, c.Ebx = 4;
    g_ctInvFaults = 0;
    CtInvFault(&c);
    c.Esp = 0x10, c.Esi = 0x10;  // nothing readable: still no fault
    CtInvFault(&c);
    CtInvFault(&c);  // past the cap of 2: nothing
    assert(g_ctInvFaults == 3);
    // [CasTrim] Detect: default on with Mode >= 1 or Probe, off with Mode 0; an explicit value wins
    wchar_t tmp[MAX_PATH], ini[MAX_PATH];
    GetTempPathW(MAX_PATH, tmp);
    swprintf_s(ini, L"%sts3n-selftest-castrim-%lu.ini", tmp, GetCurrentProcessId());
    DeleteFileW(ini);
    assert(!CtDetectOn(ini, 0, false) && CtDetectOn(ini, 3, false) && CtDetectOn(ini, 1, false) && CtDetectOn(ini, 0, true));
    WritePrivateProfileStringW(L"CasTrim", L"Detect", L"0", ini);
    assert(!CtDetectOn(ini, 3, false) && !CtDetectOn(ini, 0, true));
    WritePrivateProfileStringW(L"CasTrim", L"Detect", L"1", ini);
    assert(CtDetectOn(ini, 0, false));
    DeleteFileW(ini);
    printf("cas trim: inventory detector selftest ok (call-site entry, bad / unreadable / null / range cases, erase ring, fault decoder, Detect key)\n");

    g_ctInvEa = ea0, g_ctVt[0] = vt0[0], g_ctVt[1] = vt0[1], g_ctInvNode = NULL, g_ctInvFaults = 0, g_ctGSeq = 0, memset(g_ctGRing, 0, sizeof(g_ctGRing));
    g_ctInvBad = g_ctInvLogs = g_ctInvGone = 0, g_ctInvCalls = g_ctInvRecs = g_ctInvSlots = g_ctInvBadSave = 0, g_ctInvQpc = 0, g_ctInvSave = 0;
    g_ctsInvN = 0;
}

// docs/539 §13.5 items 1-4 + CellGuard: the release call sites through a real patched dummy call site, the cells check,
// the guard (bad cells set to 0, the rest released as before), the float-5 watch, the 0xD4B156 / 0x5AF67E fault decoders.
static const DWORD kCtsCellVt[2] = {0x11, 0x22};  // .rdata: a "TS3W vtable" in the selftest's own image
static DWORD g_ctsRelSeen[16], g_ctsRelN, g_ctsRelCalls;
static DWORD* g_ctsRelVec;
static void __fastcall CtsRelEa(DWORD* vec, void*, DWORD* b, DWORD* e) {  // EA's 0x5AF640: what it would release
    g_ctsRelCalls++, g_ctsRelVec = vec, g_ctsRelN = (DWORD)(e - b);
    for (DWORD i = 0; i < g_ctsRelN && i < 16; i++) g_ctsRelSeen[i] = b[i];
}
static void TestCasTrimCells() {
    static_assert(offsetof(Ts3nCounters, ctCellSeen) == 0x5EC0 && offsetof(Ts3nCounters, ctCellSkip) == 0x5EC4 && sizeof(Ts3nCounters) == 6314 * 4,
                  "counters v176 layout after the gap held for v174-v175 (docs/registry.md)");
    assert(RhRanges());
    Ts3nCounters& z = ts3native_counters;
    const CtRelFn ea0 = g_ctRelEa;
    const bool guard0 = g_ctCellGuard;
    g_ctRelEa = &CtsRelEa;
    static DWORD good[4], data[4] = {0x12345678}, zero[4], junk[4];
    good[0] = (DWORD)(uintptr_t)&kCtsCellVt[0];
    static u8 outfit[0xC8];
    static DWORD cells[8];
    const DWORD init[7] = {0, (DWORD)(uintptr_t)good, (DWORD)(uintptr_t)junk | 1, (DWORD)(uintptr_t)zero, CT_F5, (DWORD)(uintptr_t)data, (DWORD)(uintptr_t)good};
    memcpy(cells, init, sizeof(init));
    *(DWORD**)(outfit + 0xBC) = cells, *(DWORD**)(outfit + 0xC0) = cells + 7;
    // a dummy call site laid out as EA's 0x5E6E5C: push end; push begin; mov ecx,&outfit[+0xBC]; call 0x5AF640; then ret
    u8* code = (u8*)VirtualAlloc(NULL, 64, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
    assert(code);
    const DWORD vec = (DWORD)(uintptr_t)(outfit + 0xBC), rel = (DWORD)(uintptr_t)&CtsRelEa - (DWORD)(uintptr_t)(code + 20);
    code[0] = 0x68, *(DWORD*)(code + 1) = (DWORD)(uintptr_t)(cells + 7), code[5] = 0x68, *(DWORD*)(code + 6) = (DWORD)(uintptr_t)cells;
    code[10] = 0xB9, *(DWORD*)(code + 11) = vec, code[15] = 0xE8, *(DWORD*)(code + 16) = rel, code[20] = 0xC3;
    const CallSite site[] = {{(DWORD)(uintptr_t)(code + 15), (DWORD)(uintptr_t)&CtsRelEa, (void*)CtRelHook}};
    assert(PatchCalls("selftest cas trim release site", site, 1));
    void (*run)() = (void (*)())code;
    // guard off: log only, EA sees every cell as it was
    g_ctCellGuard = false, g_ctRelDetect = true;
    const LONG seen0 = z.ctCellSeen, skip0 = z.ctCellSkip, f50 = g_ctF5, bad0 = g_ctCellBad, rb0 = g_ctRelBad;
    run();
    assert(g_ctsRelCalls == 1 && g_ctsRelVec == (DWORD*)(uintptr_t)vec && g_ctsRelN == 7 && !memcmp(g_ctsRelSeen, init, sizeof(init)));
    assert(z.ctCellSeen - seen0 == 5 && z.ctCellSkip == skip0 && g_ctF5 - f50 == 2 && g_ctCellBad - bad0 == 3 && g_ctRelBad - rb0 == 3);
    // guard on: the zero-vtable cell, 0x40A00000 and the non-.rdata one are set to 0; the good and tagged ones go to EA unchanged
    g_ctCellGuard = true;
    run();
    const DWORD want[7] = {0, init[1], init[2], 0, 0, 0, init[6]};
    assert(g_ctsRelCalls == 2 && !memcmp(g_ctsRelSeen, want, sizeof(want)) && z.ctCellSkip - skip0 == 3 && z.ctCellSeen - seen0 == 10);
    // a clean outfit: released exactly as before, nothing skipped
    memcpy(cells, init, sizeof(init)), cells[3] = cells[4] = cells[5] = (DWORD)(uintptr_t)good;
    DWORD before[7];
    memcpy(before, cells, sizeof(before));
    run();
    assert(g_ctsRelCalls == 3 && !memcmp(g_ctsRelSeen, before, sizeof(before)) && z.ctCellSkip - skip0 == 3);
    // an unreadable range or begin past end: logged, EA still called; no cells: nothing
    *(DWORD**)(outfit + 0xC0) = cells;
    CtRelHook((DWORD*)(uintptr_t)vec, NULL, cells + 7, cells);
    CtRelHook((DWORD*)(uintptr_t)vec, NULL, NULL, NULL);
    assert(g_ctsRelCalls == 5);
    assert(CtRelPath(0x005E7638) == 2 && CtRelPath(0x005E6E65) == 1);
    // the save path walks the cells too (no guard there), and remembers the outfit's save
    memcpy(cells, init, sizeof(init)), *(DWORD**)(outfit + 0xC0) = cells + 7;
    g_ctInvSave = 7, g_ctInvCells = g_ctInvCellBad = g_ctF5Save = 0;
    CtOutfitCells(0, NULL, outfit);
    assert(g_ctInvCells == 5 && g_ctInvCellBad == 3 && g_ctF5Save == 2 && !memcmp(cells, init, sizeof(init)) && CtSeenSave(outfit) == 7 && CtSeenSave(outfit + 4) == -1);
    // the fault decoders: 0xD4B156 with the 0x5AF640 frame (values laid out as the 11-08 EA report), 0x5AF67E, another caller
    static DWORD fr[18];
    fr[0] = 0x007287EC, fr[1] = CT_F5, fr[5] = 0x00728DD4, fr[10] = 0x005AF66D, fr[11] = (DWORD)(uintptr_t)(cells + 4);
    fr[14] = (DWORD)(uintptr_t)outfit, fr[15] = 0x005E7638, fr[16] = (DWORD)(uintptr_t)cells, fr[17] = (DWORD)(uintptr_t)(cells + 7);
    CONTEXT c = {};
    c.Eip = 0x00D4B156, c.Esp = (DWORD)(uintptr_t)fr;
    char t[1400];
    assert(CtRelFaultText(&c, t, sizeof(t)) && strstr(t, "entry 4 of 7") && strstr(t, "destructor 0x5E75A0") && strstr(t, "(float 5.0)") && strstr(t, "save #7") &&
           strstr(t, "[40A00000]"));
    static DWORD f2[18];
    f2[1] = (DWORD)(uintptr_t)outfit, f2[2] = 0x005E6E65, f2[3] = (DWORD)(uintptr_t)cells, f2[4] = (DWORD)(uintptr_t)(cells + 7);
    c.Eip = 0x005AF67E, c.Esp = (DWORD)(uintptr_t)f2, c.Esi = (DWORD)(uintptr_t)(cells + 3), c.Ecx = (DWORD)(uintptr_t)zero;
    assert(CtRelFaultText(&c, t, sizeof(t)) && strstr(t, "entry 3 of 7") && strstr(t, "clear 0x5E6E20"));
    fr[5] = 0;
    c.Eip = 0x00D4B156, c.Esp = (DWORD)(uintptr_t)fr;
    assert(!CtRelFaultText(&c, t, sizeof(t)) && strstr(t, "value only"));
    c.Esp = 0x10;  // nothing readable: no fault
    assert(!CtRelFaultText(&c, t, sizeof(t)));
    g_ctRelFaults = 0;
    CtRelFault(&c), CtRelFault(&c), CtRelFault(&c);
    assert(g_ctRelFaults == 3);
    {  // cost: a 111-cell outfit (the 11-08 crash's size) of valid objects, warm cache (the game's misses add to this)
        static DWORD big[111];
        for (DWORD& x : big) x = (DWORD)(uintptr_t)good;
        LARGE_INTEGER q0, q1, f;
        QueryPerformanceFrequency(&f), QueryPerformanceCounter(&q0);
        for (int k = 0; k < 20000; k++) CtRelHook((DWORD*)(uintptr_t)vec, NULL, big, big + 111);
        QueryPerformanceCounter(&q1);
        printf("cas trim: release check cost: %.2f us a 111-cell outfit (warm cache, 20000 calls)\n", (double)(q1.QuadPart - q0.QuadPart) * 1e6 / (double)f.QuadPart / 20000.0);
    }
    printf("cas trim: outfit cells, CellGuard and release fault decoder selftest ok (patched dummy call site, guard off / on / clean outfit, "
           "float-5, save path, 0xD4B156 / 0x5AF67E frames)\n");
    VirtualFree(code, 0, MEM_RELEASE);
    g_ctRelEa = ea0, g_ctCellGuard = guard0, g_ctRelDetect = true, g_ctRelFaults = 0, g_ctInvSave = 0, g_ctInvCells = g_ctInvCellBad = g_ctF5Save = 0;
    g_ctRelCalls = g_ctRelCells = g_ctRelBad = g_ctRelOther = g_ctCellBad = g_ctCellLogs = g_ctCellGone = g_ctF5 = g_ctF5Logs = g_ctGuardLogs = 0, g_ctRelQpc = g_ctRelMaxQpc = 0;
    z.ctCellSeen = z.ctCellSkip = 0;
    memset(g_ctCellVt, 0, sizeof(g_ctCellVt)), memset(g_ctInvSeen, 0, sizeof(g_ctInvSeen));
    g_ctsRelCalls = 0;
}

// docs/539 §14: the 0x5D82A0 call sites (ModelBuilder part records) through a real patched dummy call site, MbGuard off / on,
// bad ranges, and the 0xD4B154 fault decoder on the 11-09 frame layout (dtor and holder callers, another loop, another caller).
static DWORD g_ctsMbSeen[12], g_ctsMbCalls;
static u8* g_ctsMbArg;
static void __fastcall CtsMbEa(u8* mb, void*) {  // EA's 0x5D82A0: the cells it would release
    g_ctsMbCalls++, g_ctsMbArg = mb;
    __try {
        const u8* b = *(u8* const*)(mb + 0xC);
        for (int r = 0; r < 3; r++) memcpy(g_ctsMbSeen + r * 4, b + r * 0x74 + 4, 16);
    } __except (EXCEPTION_EXECUTE_HANDLER) {}
}
static void TestCasTrimMb() {
    static_assert(offsetof(Ts3nCounters, mbCalls) == 0x5EE8 && offsetof(Ts3nCounters, mbSkip) == 0x5EF4 && sizeof(Ts3nCounters) == 6314 * 4,
                  "counters v178 layout after v177 (docs/registry.md)");
    assert(RhRanges());
    Ts3nCounters& z = ts3native_counters;
    const CtMbFn ea0 = g_ctMbEa;
    g_ctMbEa = &CtsMbEa;
    static DWORD good[4], data[4] = {0x12345678}, zero[4], junk[4];
    good[0] = (DWORD)(uintptr_t)&kCtsCellVt[1];
    static u8 mb[0x40], recs[3 * 0x74];
    // record 0: first dword 0 (EA skips it, bad cells there are not counted); 1: good, float 5, pending, zero vtable; 2: not .rdata, empty, good, good
    const DWORD init[12] = {CT_F5, CT_F5, CT_F5, CT_F5, (DWORD)(uintptr_t)good, CT_F5, (DWORD)(uintptr_t)junk | 1, (DWORD)(uintptr_t)zero,
                            (DWORD)(uintptr_t)data, 0, (DWORD)(uintptr_t)good, (DWORD)(uintptr_t)good};
    for (int r = 0; r < 3; r++) *(DWORD*)(recs + r * 0x74) = r ? 1 : 0, memcpy(recs + r * 0x74 + 4, init + r * 4, 16);
    *(u8**)(mb + 0xC) = recs, *(u8**)(mb + 0x10) = recs + 3 * 0x74;
    // a dummy call site: mov ecx,builder; call 0x5D82A0; ret
    u8* code = (u8*)VirtualAlloc(NULL, 64, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
    assert(code);
    code[0] = 0xB9, *(DWORD*)(code + 1) = (DWORD)(uintptr_t)mb, code[5] = 0xE8, *(DWORD*)(code + 6) = (DWORD)(uintptr_t)&CtsMbEa - (DWORD)(uintptr_t)(code + 10), code[10] = 0xC3;
    const CallSite site[] = {{(DWORD)(uintptr_t)(code + 5), (DWORD)(uintptr_t)&CtsMbEa, (void*)CtMbHook}};
    assert(PatchCalls("selftest cas trim modelbuilder site", site, 1));
    void (*run)() = (void (*)())code;
    g_ctMbGuard = false;
    const LONG c0 = z.mbCalls, n0 = z.mbCells, b0 = z.mbBad, s0 = z.mbSkip;
    run();  // log only: EA sees every cell as it was
    assert(g_ctsMbCalls == 1 && g_ctsMbArg == mb && !memcmp(g_ctsMbSeen, init, sizeof(init)));
    assert(z.mbCalls - c0 == 1 && z.mbCells - n0 == 6 && z.mbBad - b0 == 3 && z.mbSkip == s0 && g_ctMbRecs == 3);
    g_ctMbGuard = true;
    run();  // guard: float 5, zero vtable and non-.rdata cells set to 0; record 0, the good and pending ones unchanged
    DWORD want[12];
    memcpy(want, init, sizeof(want)), want[5] = want[7] = want[8] = 0;
    assert(g_ctsMbCalls == 2 && !memcmp(g_ctsMbSeen, want, sizeof(want)) && z.mbSkip - s0 == 3 && z.mbBad - b0 == 6);
    run();  // clean now: nothing more
    assert(g_ctsMbCalls == 3 && z.mbBad - b0 == 6 && z.mbSkip - s0 == 3);
    // a bad range and an unreadable record array: logged, EA still called; no builder: EA only
    *(u8**)(mb + 0x10) = recs + 5;
    run();
    *(u8**)(mb + 0xC) = (u8*)(uintptr_t)0x10, *(u8**)(mb + 0x10) = (u8*)(uintptr_t)(0x10 + 0x74);
    run();
    CtMbHook(NULL, NULL);
    assert(g_ctsMbCalls == 6 && z.mbCalls - c0 == 6);
    *(u8**)(mb + 0xC) = recs, *(u8**)(mb + 0x10) = recs + 3 * 0x74;
    // the fault decoder: the 11-09 frame (record 1 cell 1 = float 5, from the dtor call), then the holder call, loop 1, another caller
    static DWORD fr[18], holder[4];
    memcpy(recs + 0x74 + 4, init + 4, 16);
    fr[0] = 0x007287EC, fr[1] = CT_F5, fr[3] = (DWORD)(uintptr_t)(recs + 3 * 0x74), fr[4] = (DWORD)(uintptr_t)(recs + 0x74), fr[5] = 0x00728DD4;
    fr[6] = (DWORD)(uintptr_t)(recs + 0x74 + 8), fr[10] = 0x005D8377, fr[14] = (DWORD)(uintptr_t)mb, fr[17] = CT_MB_RET_DTOR;
    CONTEXT c = {};
    c.Eip = 0x00D4B154, c.Esp = (DWORD)(uintptr_t)fr;
    char t[3072];
    assert(CtMbFaultText(&c, t, sizeof(t)) && strstr(t, "0x5D82A0 part records") && strstr(t, "(float 5.0)") && strstr(t, "(rec+0x8, cell 1)") && strstr(t, "(1 of 3,") &&
           strstr(t, "ModelBuilder dtor 0x5D83B4") && strstr(t, "never erased by CasTrim") && strstr(t, "record bytes: 01 00 00 00"));
    holder[3] = (DWORD)(uintptr_t)mb, fr[14] = (DWORD)(uintptr_t)holder, fr[17] = CT_MB_RET_HOLD;
    char want2[40];
    sprintf_s(want2, "builder 0x%08lX", (DWORD)(uintptr_t)mb);
    assert(CtMbFaultText(&c, t, sizeof(t)) && strstr(t, "holder release 0x5D96AD") && strstr(t, want2));
    fr[10] = 0x005D82CF;
    assert(CtMbFaultText(&c, t, sizeof(t)) && strstr(t, "list +0x1C") && strstr(t, "record bytes: none"));
    fr[10] = 0x005AF66D;  // the outfit release: not this decoder (CtRelFaultText's)
    assert(!CtMbFaultText(&c, t, sizeof(t)) && CtRelFaultText(&c, t, sizeof(t)));
    fr[10] = 0x005D8377, c.Eip = 0x005AF67E;
    assert(!CtMbFaultText(&c, t, sizeof(t)));
    c.Eip = 0x00D4B154, c.Esp = 0x10;
    assert(!CtMbFaultText(&c, t, sizeof(t)));
    g_ctRelFaults = 0, c.Esp = (DWORD)(uintptr_t)fr;
    CtRelFault(&c), CtRelFault(&c), CtRelFault(&c);  // 2 lines, then the cap
    assert(g_ctRelFaults == 3);
    printf("cas trim: modelbuilder check and 0xD4B154 decoder selftest ok (patched dummy call site, MbGuard off / on / clean, bad ranges, dtor / holder / list frames)\n");
    VirtualFree(code, 0, MEM_RELEASE);
    g_ctMbEa = ea0, g_ctMbGuard = false, g_ctRelFaults = 0, g_ctMbLogs = g_ctMbRecs = g_ctMbGone = 0, g_ctMbQpc = g_ctMbMaxQpc = 0, g_ctsMbCalls = 0;
    z.mbCalls = z.mbCells = z.mbBad = z.mbSkip = 0;
    memset(g_ctMbVt, 0, sizeof(g_ctMbVt));
}

// docs/539 §15: the outfit save writer's call 0x5E7BC2 -> 0x5E61A0 through a real patched dummy call site: the crash's string
// (begin 0x40A00000, end EA's empty string), the same with [obj+0x28] set or another vtable (log only), an unreadable range,
// OutfitStrGuard off / on / clean, bad vectors, and the MSVCR80 memcpy fault decoder on the 11-09 stack layout.
static DWORD g_ctsOsCalls, g_ctsOsArgs[5], g_ctsOsStr[7][3];
static void __fastcall CtsOsEa(void* outfit, void*, DWORD* vec, DWORD a2, DWORD a3, DWORD a4) {  // EA's 0x5E61A0: the strings it would copy
    g_ctsOsCalls++;
    g_ctsOsArgs[0] = (DWORD)(uintptr_t)outfit, g_ctsOsArgs[1] = (DWORD)(uintptr_t)vec, g_ctsOsArgs[2] = a2, g_ctsOsArgs[3] = a3, g_ctsOsArgs[4] = a4;
    __try {
        const u8* const* b = *(const u8* const* const*)vec;
        for (int r = 0; r < 7; r++) {
            const u8* o = *(const u8* const*)(b[r] + 0x5C);
            if (o) memcpy(g_ctsOsStr[r], o + 0x2C, 12);
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {}
}
static void TestCasTrimOs() {
    static_assert(offsetof(Ts3nCounters, osCalls) == 0x5F28 && offsetof(Ts3nCounters, osFix) == 0x5F34 && sizeof(Ts3nCounters) == 6314 * 4,
                  "counters v180 layout after the v913 + v179 gap (docs/registry.md)");
    assert(RhRanges());
    Ts3nCounters& z = ts3native_counters;
    const CtOsFn ea0 = g_ctOsEa;
    const DWORD want0 = g_ctOsVtWant;
    g_ctOsEa = &CtsOsEa, g_ctOsVtWant = (DWORD)(uintptr_t)&kCtsCellVt[0];
    static u8 recs[7][0x68], objs[7][0x60], outfit[0x40];
    static wchar_t txt[] = L"Body";
    static u8* vecRecs[7];
    static DWORD vec[2];
    const DWORD vt = g_ctOsVtWant, vt2 = (DWORD)(uintptr_t)&kCtsCellVt[1], t0 = (DWORD)(uintptr_t)txt;
    // 0 no object; 1 a good string; 2 the crash (fix); 3 the crash with +0x28 set; 4 the crash on another vtable; 5 unreadable (fix); 6 EA's empty string
    const DWORD init[7][5] = {{0}, {vt, 0, t0, t0 + 8, t0 + 10}, {vt, 0, CT_F5, CT_STR_EMPTY, CT_STR_EMPTY + 2}, {vt, 0x1234, CT_F5, CT_STR_EMPTY, CT_STR_EMPTY + 2},
                              {vt2, 0, CT_F5, CT_STR_EMPTY, CT_STR_EMPTY + 2}, {vt, 0, 0x10, 0x20, 0x22}, {vt, 0, CT_STR_EMPTY, CT_STR_EMPTY, CT_STR_EMPTY + 2}};
    for (int r = 0; r < 7; r++) {
        vecRecs[r] = recs[r];
        *(u8**)(recs[r] + 0x5C) = r ? objs[r] : NULL;
        *(DWORD*)objs[r] = init[r][0], memcpy(objs[r] + 0x28, init[r] + 1, 16);
    }
    vec[0] = (DWORD)(uintptr_t)vecRecs, vec[1] = (DWORD)(uintptr_t)(vecRecs + 7);
    // a dummy call site: mov ecx,outfit; push 4; push 3; push 2; push vec; call 0x5E61A0; ret
    u8* code = (u8*)VirtualAlloc(NULL, 64, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
    assert(code);
    code[0] = 0xB9, *(DWORD*)(code + 1) = (DWORD)(uintptr_t)outfit, code[5] = 0x6A, code[6] = 4, code[7] = 0x6A, code[8] = 3, code[9] = 0x6A, code[10] = 2;
    code[11] = 0x68, *(DWORD*)(code + 12) = (DWORD)(uintptr_t)vec, code[16] = 0xE8, *(DWORD*)(code + 17) = (DWORD)(uintptr_t)&CtsOsEa - (DWORD)(uintptr_t)(code + 21), code[21] = 0xC3;
    const CallSite site[] = {{(DWORD)(uintptr_t)(code + 16), (DWORD)(uintptr_t)&CtsOsEa, (void*)CtOsHook}};
    assert(PatchCalls("selftest cas trim outfit string site", site, 1));
    void (*run)() = (void (*)())code;
    auto seen = [&](int r, DWORD b, DWORD e, DWORD c) { return g_ctsOsStr[r][0] == b && g_ctsOsStr[r][1] == e && g_ctsOsStr[r][2] == c; };
    g_ctOsGuard = false;
    const LONG c0 = z.osCalls, o0 = z.osObjs, b0 = z.osBad, f0 = z.osFix;
    run();  // log only: EA sees every string as it was, with its own arguments
    assert(g_ctsOsCalls == 1 && g_ctsOsArgs[0] == (DWORD)(uintptr_t)outfit && g_ctsOsArgs[1] == (DWORD)(uintptr_t)vec && g_ctsOsArgs[2] == 2 && g_ctsOsArgs[4] == 4);
    for (int r = 1; r < 7; r++) assert(seen(r, init[r][2], init[r][3], init[r][4]));
    assert(z.osCalls - c0 == 1 && z.osObjs - o0 == 6 && z.osBad - b0 == 4 && z.osFix == f0 && g_ctOsRecs == 7);
    g_ctOsGuard = true;
    run();  // guard: 2 and 5 reset to EA's empty string; 3 (+0x28 set) and 4 (another vtable) stay as they were
    assert(g_ctsOsCalls == 2 && seen(2, CT_STR_EMPTY, CT_STR_EMPTY, CT_STR_EMPTY + 2) && seen(5, CT_STR_EMPTY, CT_STR_EMPTY, CT_STR_EMPTY + 2));
    assert(seen(1, t0, t0 + 8, t0 + 10) && seen(3, CT_F5, CT_STR_EMPTY, CT_STR_EMPTY + 2) && seen(4, CT_F5, CT_STR_EMPTY, CT_STR_EMPTY + 2));
    assert(*(DWORD*)(objs[2] + 0x38) == 0 && *(DWORD*)(objs[2] + 0x28) == 0 && z.osBad - b0 == 8 && z.osFix - f0 == 2);
    run();  // only 3 and 4 left
    assert(g_ctsOsCalls == 3 && z.osBad - b0 == 10 && z.osFix - f0 == 2);
    // a bad vector and an unreadable one: logged, EA still called; no vector: EA only
    vec[1] = vec[0] - 4;
    run();
    vec[0] = 0x10, vec[1] = 0x14;
    run();
    CtOsHook(outfit, NULL, NULL, 2, 3, 4);
    assert(g_ctsOsCalls == 6 && z.osCalls - c0 == 6);
    // the fault decoder: the 11-09 stack (A at esp+0x100), eip in the CRT; then no 0x5BF5E0, an unreadable esp
    static DWORD st[0x100];
    const int a = 0x40;
    st[0xF] = 0x00408C3A, st[a] = 0x005E621C, st[a - 0x19] = 0x005BF5E0, st[a + 0x15] = CT_OS_RET, st[a - 0x16] = (DWORD)(uintptr_t)recs[3];
    st[a - 0x18] = CT_F5, st[a - 0x17] = CT_STR_EMPTY, st[a - 0x1B] = (DWORD)(uintptr_t)objs[3], st[a + 3] = (DWORD)(uintptr_t)outfit;
    CONTEXT c = {};
    c.Eip = 0x700973D0, c.Esp = (DWORD)(uintptr_t)st;
    char t[3072];
    assert(CtOsFaultText(&c, t, sizeof(t)) && strstr(t, "outside TS3W") && strstr(t, "0x408C3A memcpy return below it") &&
           strstr(t, "string begin 0x40A00000 (float 5.0), end 0x011C7E54 (EA's empty string)") && strstr(t, " = 0xFE2A80, +0x28 00001234") &&
           strstr(t, "never erased by CasTrim") && !strstr(t, "(not the object)"));
    st[a - 0x16] = (DWORD)(uintptr_t)recs[1];
    assert(CtOsFaultText(&c, t, sizeof(t)) && strstr(t, "(not the object)"));
    st[a - 0x19] = 0;
    assert(!CtOsFaultText(&c, t, sizeof(t)));
    c.Esp = 0x10;
    assert(!CtOsFaultText(&c, t, sizeof(t)));
    printf("cas trim: outfit string check and memcpy fault decoder selftest ok (patched dummy call site, OutfitStrGuard off / on / clean, flag and vtable exemptions, bad vectors, 11-09 stack)\n");
    VirtualFree(code, 0, MEM_RELEASE);
    g_ctOsEa = ea0, g_ctOsVtWant = want0, g_ctOsGuard = false, g_ctOsLogs = g_ctOsGone = g_ctOsRecs = 0, g_ctOsQpc = g_ctOsMaxQpc = 0, g_ctsOsCalls = 0, g_ctOsFaults = 0;
    z.osCalls = z.osObjs = z.osBad = z.osFix = 0;
    memset(g_ctOsVt, 0, sizeof(g_ctOsVt));
}

// docs/539 §16 [CasTrim] WriteTrap: candidate order, real DR write watches through WpVeh -> CtTrapHit (value, float-5 flag,
// read not caught, a thread started after the arm caught after WpRefresh), the release-site drop, a stale watch.
static volatile DWORD* g_ctsTrapCell;
static HANDLE g_ctsTrapGo;
static DWORD WINAPI CtsTrapWriter(LPVOID) {
    WaitForSingleObject(g_ctsTrapGo, INFINITE);
    *g_ctsTrapCell = CT_F5;
    return 0;
}
static void TestCasTrimTrap() {
    static_assert(offsetof(Ts3nCounters, trArms) == 0x5F38 && offsetof(Ts3nCounters, trGone) == 0x5F44 && sizeof(Ts3nCounters) == 6314 * 4,
                  "counters v181 layout after v180 (docs/registry.md)");
    assert(RhRanges());
    Ts3nCounters& z = ts3native_counters;
    wchar_t lp[MAX_PATH];  // the lines as the game writes them (queue regex cases, AGENTS.md)
    GetTempPathW(MAX_PATH, lp), wcscat_s(lp, L"ts3native-castrim-trap-selftest.log");
    const HANDLE savedLog = g_log;
    g_log = CreateFileW(lp, GENERIC_WRITE, FILE_SHARE_READ, NULL, CREATE_ALWAYS, 0, NULL);
    WpInstall();
    g_wpTrapFn = CtTrapHit, g_ctTrapOn = true;
    static u8 o121[0x100], o81[0x100], oOther[0x100];
    static DWORD c121[121], c81[81], cOther[121];
    *(DWORD*)o121 = *(DWORD*)o81 = CT_TRAP_VT, *(DWORD*)oOther = CT_TRAP_VT + 4;
    *(DWORD**)(o121 + 0xBC) = c121, *(DWORD**)(o81 + 0xBC) = c81, *(DWORD**)(oOther + 0xBC) = cOther;
    CtTrapNote(oOther, cOther, cOther + 121);  // another class: nothing
    CtTrapNote(o121, c121, c121 + 121);        // 87 of 121 preferred, 3 of 121 other
    CtTrapNote(o81, c81, c81 + 81);            // 3 of 81 preferred (87 out of range)
    assert(g_ctTrapNC[0] == 2 && g_ctTrapNC[1] == 1 && g_ctTrapC[0][0].cell == (DWORD)(uintptr_t)(c121 + 87) && g_ctTrapC[0][1].cell == (DWORD)(uintptr_t)(c81 + 3));
    const LONG a0 = z.trArms, h0 = z.trHits, f0 = z.trF5, g0 = z.trGone, save0 = g_ctInvSave;
    g_ctInvSave = 0;
    CtTrapArm();
    assert(z.trArms - a0 == 3 && g_ctTrapNC[0] == 0 && g_wp[0].trap && g_wp[2].trap && !g_wp[3].addr);
    assert(g_ctTrap[0].cell == (DWORD)(uintptr_t)(c121 + 87) && g_ctTrap[1].cell == (DWORD)(uintptr_t)(c81 + 3) && g_ctTrap[2].cell == (DWORD)(uintptr_t)(c121 + 3));
    volatile DWORD* v87 = c121 + 87;
    const DWORD r = *v87;  // a read: not caught
    assert(r == 0 && z.trHits == h0);
    *v87 = 0x1234;  // a legit write
    assert(z.trHits - h0 == 1 && z.trF5 == f0 && g_ctTrap[0].writes == 1);
    *v87 = CT_F5;  // the writer
    assert(z.trHits - h0 == 2 && z.trF5 - f0 == 1);
    g_ctsTrapCell = c81 + 3, g_ctsTrapGo = CreateEventW(NULL, TRUE, FALSE, NULL);
    HANDLE t = CreateThread(NULL, 0, CtsTrapWriter, NULL, 0, NULL);  // started after the arm: set by the refresh
    assert(t && WpRefresh() >= 2);
    SetEvent(g_ctsTrapGo), WaitForSingleObject(t, INFINITE), CloseHandle(t), CloseHandle(g_ctsTrapGo);
    assert(z.trHits - h0 == 3 && z.trF5 - f0 == 2 && g_ctTrap[1].writes == 1);
    CtTrapDrop(o81, 1);  // the outfit clear: disarmed, no more hits
    assert(!g_wp[1].addr && z.trGone - g0 == 1);
    c81[3] = 0;
    assert(z.trHits - h0 == 3);
    *(DWORD**)(o121 + 0xBC) = cOther;  // the cells moved: a stale watch, no WRITER count
    volatile DWORD* v3 = c121 + 3;
    *v3 = CT_F5;
    assert(z.trHits - h0 == 4 && z.trF5 - f0 == 2 && z.trGone - g0 == 2 && g_ctTrap[2].gone == 1);
    CtTrapArm();  // no candidates: every trap slot cleared
    assert(!g_wp[0].addr && !g_wp[2].addr);
    *v87 = 0;
    assert(z.trHits - h0 == 4);
    WpUninstall();
    if (g_log != INVALID_HANDLE_VALUE) CloseHandle(g_log);
    g_log = savedLog;
    printf("cas trim: write trap selftest ok (candidate order, DR write hits, float5 WRITER, read not caught, refresh for a later thread, drop at the clear, "
           "stale watch, clear on an empty save); lines in %ls\n", lp);
    g_wpTrapFn = NULL, g_ctTrapOn = false, g_ctInvSave = save0, g_ctTrapLogs = g_ctTrapF5Logs = 0;
    memset(g_ctTrapEip, 0, sizeof(g_ctTrapEip)), memset(g_ctTrap, 0, sizeof(g_ctTrap));
    z.trArms = z.trHits = z.trF5 = z.trGone = 0;
}

// docs/539 §16.6 realm shape check: fake realm / nodes / shapes, the hooks called directly with fake EA functions.
static LONG g_ctsRlRem, g_ctsRlAdd;
static u8* g_ctsRlAddNode;
static void __fastcall CtsRlRem(u8*, void*, u8*) { g_ctsRlRem++; }
static void __fastcall CtsRlAdd(u8*, void*, u8*, u8* node) { g_ctsRlAdd++, g_ctsRlAddNode = node; }
static void TestCasTrimRealm() {
    static_assert(offsetof(Ts3nCounters, rlCalls) == 0x5F48 && offsetof(Ts3nCounters, rlSkip) == 0x5F54 && sizeof(Ts3nCounters) == 6314 * 4,
                  "counters v182 layout after v181 (docs/registry.md)");
    assert(RhRanges());
    Ts3nCounters& z = ts3native_counters;
    const RlRemFn rem0 = g_rlRemEa;
    const RlAddFn add0 = g_rlAddEa;
    g_rlRemEa = &CtsRlRem, g_rlAddEa = &CtsRlAdd;
    static u8 realm[0x20], node[0x30], root[0x30], sh[4][0x60];
    static DWORD rv[8], nv[8], rootv[8];
    const DWORD S[4] = {(DWORD)(uintptr_t)sh[0], (DWORD)(uintptr_t)sh[1], (DWORD)(uintptr_t)sh[2], (DWORD)(uintptr_t)sh[3]}, N = (DWORD)(uintptr_t)node;
    auto shape = [&](int k, DWORD nd, DWORD ni, DWORD ri) { *(DWORD*)(sh[k] + 0x50) = nd, *(DWORD*)(sh[k] + 0x3C) = ni, *(DWORD*)(sh[k] + 0x38) = ri; };
    auto setv = [](u8* o, int at, DWORD* v, std::initializer_list<DWORD> l) { int k = 0; for (DWORD x : l) v[k++] = x; *(DWORD**)(o + at) = v, *(DWORD**)(o + at + 4) = v + k; };
    auto end = [](const u8* o, int at) { return (LONG)(*(DWORD* const*)(o + at + 4) - *(DWORD* const*)(o + at)); };
    *(DWORD*)realm = (DWORD)(uintptr_t)root;
    const LONG c0 = z.rlCalls, b0 = z.rlBad, f0 = z.rlFix, k0 = z.rlSkip;
    // node {s0, s1, lost s2 = 0x40A00000}; s2 still in the realm vector naming node index 2
    auto lost = [&]() { shape(0, N, 0, 0), shape(1, N, 1, 1), shape(2, N, 2, 2), setv(realm, 8, rv, {S[0], S[1], S[2]}), setv(node, 0x1C, nv, {S[0], S[1], CT_F5}); };
    lost(), g_rlGuard = false;
    RlRemHook(realm, NULL, sh[0]);  // log only: EA runs, nothing changed
    assert(g_ctsRlRem == 1 && z.rlCalls - c0 == 1 && z.rlBad - b0 == 1 && z.rlFix == f0 && nv[2] == CT_F5);
    lost(), g_rlGuard = true;
    RlRemHook(realm, NULL, sh[0]);  // restored from the realm vector
    assert(g_ctsRlRem == 2 && nv[2] == S[2] && end(node, 0x1C) == 3 && z.rlFix - f0 == 1);
    // node {s0, 0x40A00000, s1}: no realm shape names index 1 -> swap-removed, s1 moves to 1
    shape(0, N, 0, 0), shape(1, N, 2, 1), setv(realm, 8, rv, {S[0], S[1]}), setv(node, 0x1C, nv, {S[0], CT_F5, S[1]});
    RlRemHook(realm, NULL, sh[0]);
    assert(end(node, 0x1C) == 2 && nv[1] == S[1] && *(DWORD*)(sh[1] + 0x3C) == 1 && z.rlFix - f0 == 2 && z.rlBad - b0 == 3);
    // realm vector last = 0x40A00000: dropped
    shape(0, N, 0, 0), shape(1, N, 1, 1), setv(realm, 8, rv, {S[0], S[1], CT_F5}), setv(node, 0x1C, nv, {S[0], S[1]});
    RlRemHook(realm, NULL, sh[1]);
    assert(end(realm, 8) == 2 && z.rlFix - f0 == 3 && z.rlBad - b0 == 4 && g_ctsRlRem == 4);
    // a 0x40A00000 shape argument: not passed to EA with the guard, passed without
    RlRemHook(realm, NULL, (u8*)(uintptr_t)CT_F5);
    assert(g_ctsRlRem == 4 && z.rlSkip - k0 == 1 && z.rlBad - b0 == 5);
    g_rlGuard = false;
    RlRemHook(realm, NULL, (u8*)(uintptr_t)CT_F5);
    assert(g_ctsRlRem == 5 && z.rlSkip - k0 == 1);
    // add with node 0: EA's root [realm] is checked, EA still gets node 0
    g_rlGuard = true;
    shape(3, (DWORD)(uintptr_t)root, 0, 2), setv(realm, 8, rv, {S[0], S[1], S[3]}), setv(root, 0x1C, rootv, {CT_F5});
    RlAddHook(realm, NULL, sh[2], NULL);
    assert(g_ctsRlAdd == 1 && !g_ctsRlAddNode && rootv[0] == S[3] && z.rlFix - f0 == 4);
    // clean: no bad
    const LONG b1 = z.rlBad;
    RlAddHook(realm, NULL, sh[2], node);
    assert(g_ctsRlAdd == 2 && g_ctsRlAddNode == node && z.rlBad == b1 && z.rlCalls - c0 == 8);
    g_rlRemEa = rem0, g_rlAddEa = add0, g_rlGuard = false, g_rlLogs = 0;
    z.rlCalls = z.rlBad = z.rlFix = z.rlSkip = 0;
    printf("cas trim: realm shape selftest ok (log only, restore from the realm vector, swap-remove, realm last dropped, 0x40A00000 shape skipped, add on the root)\n");
}
