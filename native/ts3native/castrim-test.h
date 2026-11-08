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
                      sizeof(Ts3nCounters) == 6035 * 4,
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
    static_assert(offsetof(Ts3nCounters, ctSaves) == 0x5D80 && offsetof(Ts3nCounters, ctBlendKB) == 0x5DE4 && sizeof(Ts3nCounters) == 6035 * 4,
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
    static u8 recs[2][0x68], outfit[0xA0], node[0x1C], outfit2[0xA0];
    g_ctVt[0] = (DWORD)(uintptr_t)vt[0], g_ctVt[1] = (DWORD)(uintptr_t)vt[1];
    good[0] = g_ctVt[1];
    a0[0] = (DWORD)(uintptr_t)good, a1[1] = (DWORD)(uintptr_t)bad;  // the crash: [rec+0x38] slot 1, first dword 1
    *(DWORD*)(recs[0] + 0x14) = *(DWORD*)(recs[0] + 0x38) = *(DWORD*)(recs[1] + 0x14) = (DWORD)(uintptr_t)a0;
    *(DWORD*)(recs[1] + 0x38) = (DWORD)(uintptr_t)a1;
    *(DWORD*)(outfit + 0x98) = (DWORD)(uintptr_t)recs[0], *(DWORD*)(outfit + 0x9C) = (DWORD)(uintptr_t)recs[2];
    memset(node, 0x11, 16), *(DWORD*)(node + 0x10) = (DWORD)(uintptr_t)outfit;
    g_ctInvEa = &CtsInvEa, g_ctInvBad = g_ctInvLogs = g_ctInvGone = 0, g_ctInvCalls = g_ctInvRecs = g_ctInvSlots = g_ctInvBadSave = 0, g_ctInvQpc = 0;
    // one good outfit's worth: 4 slots read, 1 bad, EA called once with (outfit, set, 1), esi kept
    CtsInvCall(node, outfit);
    assert(g_ctsInvN == 1 && g_ctsInvSet == (void*)0x1234 && g_ctsInvKids == 1 && g_ctsInvOutfit == (DWORD)(uintptr_t)outfit && g_ctInvNode == node);
    assert(g_ctInvCalls == 1 && g_ctInvRecs == 2 && g_ctInvSlots == 4 && g_ctInvBad == 1 && g_ctInvBadSave == 1 && !g_ctInvGone);
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
    printf("cas trim: inventory detector selftest ok (call-site entry, bad / unreadable / null / range cases, erase ring, fault decoder)\n");

    g_ctInvEa = ea0, g_ctVt[0] = vt0[0], g_ctVt[1] = vt0[1], g_ctInvNode = NULL, g_ctInvFaults = 0, g_ctGSeq = 0, memset(g_ctGRing, 0, sizeof(g_ctGRing));
    g_ctInvBad = g_ctInvLogs = g_ctInvGone = 0, g_ctInvCalls = g_ctInvRecs = g_ctInvSlots = g_ctInvBadSave = 0, g_ctInvQpc = 0, g_ctInvSave = 0;
    g_ctsInvN = 0;
}
