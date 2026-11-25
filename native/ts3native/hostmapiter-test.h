// TestHostMapIterFix ([HostMapIterFix] hostmapiter.cpp, docs/67 §43). Included by selftest.cpp.
// A fake host id map (layout as 0xD71E40 reads it) and a fake erase that records the slot it was given and unlinks like EA's.
static u8** g_hmtSlot;
static int g_hmtCalls;
static void* __fastcall HmtErase(u8* map, void*, DWORD* out, u8* node, u8** slot) {
    g_hmtCalls++, g_hmtSlot = slot;
    u8** p = slot;
    while (*p != node) p = (u8**)(*p + 0x10);  // EA walks the chain the same way (and faults at NULL)
    *p = *(u8**)(node + 0x10);
    (*(DWORD*)(map + 0xC))--;
    out[0] = 0, out[1] = (DWORD)(uintptr_t)slot;
    return out;
}

static u8** g_hmtB2;
static DWORD g_hmtRehashN;
static void __fastcall HmtRehash(u8* map, void*, DWORD n) {  // as 0xD72170: relink every node into a new array by idLo % n
    u8** const ob = *(u8***)(map + 4);
    const DWORD on = *(DWORD*)(map + 8);
    memset(g_hmtB2, 0, (n + 1) * sizeof(u8*)), g_hmtB2[n] = (u8*)(uintptr_t)0xFFFFFFFF;
    for (DWORD i = 0; i < on; i++)
        for (u8* x = ob[i]; x;) {
            u8* const nx = *(u8**)(x + 0x10);
            u8** const s = g_hmtB2 + *(DWORD*)x % n;
            *(u8**)(x + 0x10) = *s, *s = x, x = nx;
        }
    memset(ob, 0xDD, on * sizeof(u8*));  // the old array is freed
    *(u8***)(map + 4) = g_hmtB2, *(DWORD*)(map + 8) = n, g_hmtRehashN = n;
}

static void TestHostMapIterFix() {
    const HmiEraseFn realErase = g_hmiErase;
    g_hmiErase = HmtErase;
    static u8 map[0x20];
    static u8* oldB[5 + 1];  // the array before the rehash (stale slots point here)
    static u8* b[11 + 1];    // 11 buckets + EA's end marker
    static u8 nodes[4][0x18];
    const DWORD ids[4] = {3, 14, 25, 7};  // 3, 14, 25 share bucket 3 (of 11); 7 in bucket 7
    memset(map, 0, sizeof(map)), memset(b, 0, sizeof(b)), memset(oldB, 0, sizeof(oldB));
    b[11] = (u8*)(uintptr_t)0xFFFFFFFF;
    *(u8***)(map + 4) = b, *(DWORD*)(map + 8) = 11, *(DWORD*)(map + 0xC) = 4;
    for (int i = 0; i < 4; i++) {
        memset(nodes[i], 0, 0x18);
        *(DWORD*)nodes[i] = ids[i], *(DWORD*)(nodes[i] + 4) = 0x11;
        u8** s = b + ids[i] % 11;
        *(u8**)(nodes[i] + 0x10) = *s, *s = nodes[i];
    }
    DWORD out[2] = {};
    const LONG st0 = g_hmiStale;
    // 1. a valid slot goes to EA unchanged
    HmiErase(map, NULL, out, nodes[3], b + 7);
    assert(g_hmtCalls == 1 && g_hmtSlot == b + 7 && !b[7] && g_hmiStale == st0 && *(DWORD*)(map + 0xC) == 3);
    // 2. a slot in the freed old array (rehash inside 0xD80460): re-found by id % buckets
    oldB[14 % 5] = NULL;
    HmiErase(map, NULL, out, nodes[1], oldB + 14 % 5);
    assert(g_hmtCalls == 2 && g_hmtSlot == b + 3 && g_hmiStale == st0 + 1 && g_hmiRehomed >= 1 && *(DWORD*)(map + 0xC) == 2);
    assert(HmiChainHas(b + 3, nodes[0]) && HmiChainHas(b + 3, nodes[2]) && !HmiChainHas(b + 3, nodes[1]));
    // 3. a node outside its hash bucket (id changed): found by the scan
    const LONG sc0 = g_hmiScanned;
    *(DWORD*)nodes[2] = 26;  // 26 % 11 = 4, the node still sits in bucket 3
    HmiErase(map, NULL, out, nodes[2], oldB + 1);
    assert(g_hmtCalls == 3 && g_hmtSlot == b + 3 && g_hmiScanned == sc0 + 1 && b[3] == nodes[0] && !*(u8**)(nodes[0] + 0x10));
    // 4. a node in no chain (erased inside 0xD80460): EA's erase is not called, the out iterator is end()
    const LONG g0 = g_hmiGone;
    HmiErase(map, NULL, out, nodes[1], oldB + 4);
    assert(g_hmtCalls == 3 && g_hmiGone == g0 + 1 && out[0] == 0xFFFFFFFF && out[1] == (DWORD)(uintptr_t)(b + 11));
    // 5. Repair=0 (detector): counted, EA gets its own iterator
    g_hmiRepair = false;
    const LONG st1 = g_hmiStale;
    oldB[0] = nodes[0];  // the stale chain still holds the node, so the fake erase can finish
    HmiErase(map, NULL, out, nodes[0], oldB);
    assert(g_hmtCalls == 4 && g_hmtSlot == oldB && g_hmiStale == st1 + 1);
    g_hmiRepair = true;
    // 6. TestRehash: the forced rehash runs before the check, so the slot the caller saved is stale and gets re-found
    static u8* b2[13];
    static u8 n6[0x18];
    memset(b, 0, sizeof(b)), b[11] = (u8*)(uintptr_t)0xFFFFFFFF, *(DWORD*)(map + 8) = 11;
    memset(n6, 0, sizeof(n6)), *(DWORD*)n6 = 30, b[30 % 11] = n6, *(DWORD*)(map + 0xC) = 1;
    g_hmtB2 = b2;
    const DWORD rehashWas = g_hmiRehashAt;
    g_hmiRehashAt = (DWORD)(uintptr_t)&HmtRehash, g_hmiTestLeft = 1;
    const LONG r0 = g_hmiRehomed;
    HmiErase(map, NULL, out, n6, b + 30 % 11);
    assert(g_hmtRehashN == 12 && *(u8***)(map + 4) == b2 && g_hmtSlot == b2 + 30 % 12 && g_hmiRehomed == r0 + 1 && !b2[30 % 12] && !g_hmiTestLeft);
    HmiErase(map, NULL, out, n6, b2);  // the budget is spent: no second rehash (n6 is gone now: end())
    assert(g_hmtRehashN == 12 && *(u8***)(map + 4) == b2);
    g_hmiRehashAt = rehashWas;
    // 7. the call-site patch: the 29 bytes before the call, its target and the erase prologue, then the rel32 points at HmiErase
    static u8 seq[sizeof(kHmiSeq) + 5], pro[sizeof(kHmiErasePro)];
    u8* const site = seq + sizeof(kHmiSeq);
    memcpy(seq, kHmiSeq, sizeof(kHmiSeq)), memcpy(pro, kHmiErasePro, sizeof(pro));
    site[0] = 0xE8, *(LONG*)(site + 1) = (LONG)((uintptr_t)pro - ((uintptr_t)site + 5));
    pro[0] = 0x90;
    assert(!HmiPatch(site, (DWORD)(uintptr_t)pro));
    pro[0] = kHmiErasePro[0], seq[3] = 0x10;
    assert(!HmiPatch(site, (DWORD)(uintptr_t)pro));
    seq[3] = kHmiSeq[3];
    assert(!HmiPatch(site, (DWORD)(uintptr_t)pro + 1));  // the call goes elsewhere (another owner's retarget)
    assert(HmiPatch(site, (DWORD)(uintptr_t)pro));
    assert((uintptr_t)(site + 5) + *(const LONG*)(site + 1) == (uintptr_t)&HmiErase);
    g_hmiErase = realErase;
    printf("hostmapiter: valid slot passes, stale slot re-found by hash and by scan, node in no chain not erased (end()), Repair=0 passes "
           "EA's iterator, TestRehash forces one rehash per budget, call-site patch byte / target checks + rel32: ok\n");
}
