// TestThumbLod ([ThumbStall] Actions 3 and 4, thumbstall.cpp, 2026-11-12). Included by selftest.cpp.
// A fake SimService for TsLodRead and the ready-job pick, and the fallback's start and end on a fake manager.
static u8* g_tsTestPickRet;
static u8* __fastcall TsTestPickOrig(u8*) { return g_tsTestPickRet; }
static DWORD g_tsTestNow;
static DWORD WINAPI TsTestNow() { return g_tsTestNow; }
static void TestThumbLod() {
    static u8 svc[0x80], nodes[4][0x198];
    static u8* svcp = svc;
    const DWORD savedAt = g_tsSvcAt;
    const TsPickFn savedPick = g_tsPickOrig;
    g_tsSvcAt = (DWORD)(uintptr_t)&svcp;
    g_tsPickOrig = &TsTestPickOrig;
    *(DWORD*)svc = TS_SVC_VT;
    *(long*)(svc + 0x30) = 1, *(long*)(svc + 0x40) = 200, *(long*)(svc + 0x4C) = 31, *(long*)(svc + 0x50) = 60;
    u8* const h58 = svc + 0x58;
    u8* const h60 = svc + 0x60;
    *(u8**)h58 = nodes[0], *(u8**)nodes[0] = h58, *(u8**)(nodes[0] + 4) = h58, *(u8**)(h58 + 4) = nodes[0];  // loading: one
    *(u8**)h60 = nodes[1], *(u8**)nodes[1] = nodes[2], *(u8**)nodes[2] = nodes[3], *(u8**)nodes[3] = h60;  // ready: three
    *(u8**)(nodes[1] + 4) = h60, *(u8**)(nodes[2] + 4) = nodes[1], *(u8**)(nodes[3] + 4) = nodes[2], *(u8**)(h60 + 4) = nodes[3];
    *(u8**)(svc + 0x68) = 0;
    const LONG obj = 0x12345678;
    *(LONG*)(nodes[2] + 0x18) = obj, *(long*)(nodes[2] + 8) = 2, *(long*)(nodes[2] + 0xC) = 7, *(long*)(nodes[2] + 0x190) = 6;
    *(LONG*)(nodes[3] + 0x18) = obj, *(long*)(nodes[3] + 8) = 1;
    TsLod L;
    TsLodRead((const u8*)(uintptr_t)obj, &L);
    assert(L.inuse == 31 && L.limit == 200 && L.cached == 60 && L.minLod == 1);
    assert(L.jobs == 1 && L.ready == 3 && L.current == 0);
    assert(!strcmp(L.job, "ready #1 lod 2 requests 7 state 6"));
    TsLodRead((const u8*)(uintptr_t)0x1111, &L);
    assert(!strcmp(L.job, "none"));
    *(DWORD*)svc = 0;
    TsLodRead((const u8*)(uintptr_t)obj, &L);
    assert(L.inuse == -1 && !strcmp(L.job, "no service"));
    *(DWORD*)svc = TS_SVC_VT;

    // the pick: nothing asked = EA's picker; asked = that object's job with the wanted LOD, unlinked; then the other one
    g_tsTestPickRet = nodes[1];
    InterlockedExchange(&g_tsPickObj, 0);
    assert(TsPick(svc) == nodes[1]);
    InterlockedExchange(&g_tsPickObj, obj), InterlockedExchange(&g_tsPickLod, 1);
    g_tsFbPicked = -1;
    const LONG picks0 = g_tsPicks;
    assert(TsPick(svc) == nodes[3] && g_tsFbPicked == 2);
    assert(*(u8**)nodes[2] == h60 && *(u8**)(h60 + 4) == nodes[2]);  // node 3 left the ring
    assert(TsPick(svc) == nodes[2] && g_tsPicks - picks0 == 2);
    assert(*(u8**)nodes[1] == h60 && *(u8**)h60 == nodes[1] && *(u8**)(h60 + 4) == nodes[1]);
    assert(TsPick(svc) == nodes[1]);  // none of that object left: EA's picker
    InterlockedExchange(&g_tsPickObj, 0);

    // start and end: a fake manager whose front request is still in 0x11, then moves on
    static u8 mgr[0x120], req[0x248], blk[0x2A0];
    *(u8**)(mgr + 0x100) = req, *(u8**)(mgr + 0x110) = req + 0x248;
    *(DWORD*)(req + 0x23C) = 77, *(DWORD*)(req + 0x108) = 0x11;
    *(long*)(blk + 0x210 + 0x14) = -1, *(long*)(blk + 0x210 + 0x18) = 0, blk[0x210 + 0x22] = 1, blk[0x210 + 0x23] = 0xFF;
    *(DWORD*)(blk + 0x24C) = 0x4328;  // the interface; the job names the object 0x28 below it
    const int savedAction = g_tsAction;
    g_tsAction = 3;
    TsFbStart(req, 77, blk, GetTickCount());
    assert(g_tsFbReq == req && g_tsPickObj == 0x4300 && g_tsPickLod == 1);  // wanted 0 raised to MinSimLOD 1
    const LONG r0 = g_tsFbRendered, o0 = g_tsFbOther, a0 = g_tsFbAborted;
    TsFbWatch(mgr);
    assert(g_tsFbReq == req && g_tsPickObj == 0x4300);  // still waiting
    *(DWORD*)(req + 0x108) = 0x12;
    TsFbWatch(mgr);
    assert(!g_tsFbReq && !g_tsPickObj && g_tsFbRendered == r0 + 1);
    *(DWORD*)(req + 0x108) = 0x11;  // 0x1B without a current LOD: other; with LOD 1: rendered
    TsFbStart(req, 77, blk, GetTickCount());
    *(DWORD*)(req + 0x108) = 0x1B;
    TsFbWatch(mgr);
    assert(!g_tsFbReq && g_tsFbOther == o0 + 1 && g_tsFbRendered == r0 + 1);
    *(DWORD*)(req + 0x108) = 0x11;
    TsFbStart(req, 77, blk, GetTickCount());
    *(long*)(blk + 0x210 + 0x14) = 1, *(DWORD*)(req + 0x108) = 0x1B;
    TsFbWatch(mgr);
    assert(!g_tsFbReq && g_tsFbRendered == r0 + 2);
    *(long*)(blk + 0x210 + 0x14) = -1, *(DWORD*)(req + 0x108) = 0x11;
    TsFbStart(req, 77, blk, GetTickCount());
    *(DWORD*)(req + 0x23C) = 78;  // a new request in the same slot
    TsFbWatch(mgr);
    assert(!g_tsFbReq && !g_tsPickObj && g_tsFbOther == o0 + 2);
    *(DWORD*)(req + 0x23C) = 77;
    g_tsAction = 4;
    TsFbStart(req, 77, blk, GetTickCount());
    assert(blk[0x210 + 0x22] == 0 && !g_tsPickObj);
    TsFbEnd(1, 0x11, true);
    assert(!g_tsFbReq && g_tsFbAborted == a0 + 1);

    // TsBlocked, Action 3 with PickMs 300 and KeepBuilding 2 on a fake clock: the pick after 300 ms, the abort TimeoutMs
    // after it, held off twice while the SimService's current job (+0x68) is this object's
    DWORD(WINAPI* const savedNow)() = g_tsNow;
    const DWORD savedTo = g_tsTimeoutMs, savedPm = g_tsPickMs;
    const int savedKeep = g_tsKeep;
    g_tsNow = &TsTestNow, g_tsAction = 3, g_tsTimeoutMs = 3000, g_tsPickMs = 300, g_tsKeep = 2;
    g_tsReq = 0, g_tsFbReq = 0;
    InterlockedExchange(&g_tsPickObj, 0);
    *(u8**)(req + 0x244) = blk, *(DWORD*)(req + 0x23C) = 90, *(DWORD*)(req + 0x108) = 0x11;
    *(long*)(blk + 0x210 + 0x14) = -1;
    *(LONG*)(nodes[0] + 0x18) = 0x4300;  // the job being built is the portrait's
    *(u8**)(svc + 0x68) = nodes[0];
    const LONG a1 = g_tsFbAborted;
    g_tsTestNow = 1000;
    assert(TsBlocked(req) == 0 && g_tsReq == req && !g_tsFbReq);
    g_tsTestNow = 1299;
    assert(TsBlocked(req) == 0 && !g_tsFbReq);
    g_tsTestNow = 1300;
    assert(TsBlocked(req) == 0 && g_tsFbReq == req && g_tsPickObj == 0x4300);  // the pick
    g_tsTestNow = 4299;
    assert(TsBlocked(req) == 0 && g_tsFbKept == 0);
    g_tsTestNow = 4300;
    assert(TsBlocked(req) == 0 && g_tsFbKept == 1 && g_tsFbReq == req);  // building: kept
    g_tsTestNow = 7300;
    assert(TsBlocked(req) == 0 && g_tsFbKept == 2);
    g_tsTestNow = 10300;
    assert(TsBlocked(req) == 1 && !g_tsFbReq && !g_tsPickObj && g_tsFbAborted == a1 + 1);  // kept twice: abort
    // another object's job being built: no keep, the abort TimeoutMs after the pick
    *(DWORD*)(req + 0x23C) = 91, *(LONG*)(nodes[0] + 0x18) = 0x9900;
    g_tsTestNow = 20000;
    assert(TsBlocked(req) == 0);
    g_tsTestNow = 20300;
    assert(TsBlocked(req) == 0 && g_tsFbReq == req);
    g_tsTestNow = 23300;
    assert(TsBlocked(req) == 1 && g_tsFbAborted == a1 + 2 && g_tsFbKept == 0);
    // PickMs 0: the pick at TimeoutMs, as before
    g_tsPickMs = 0, *(DWORD*)(req + 0x23C) = 92;
    g_tsTestNow = 30000;
    assert(TsBlocked(req) == 0);
    g_tsTestNow = 30300;
    assert(TsBlocked(req) == 0 && !g_tsFbReq);
    g_tsTestNow = 33000;
    assert(TsBlocked(req) == 0 && g_tsFbReq == req);
    TsFbEnd(2, 0, false);
    *(u8**)(svc + 0x68) = 0, g_tsReq = 0;
    g_tsNow = savedNow, g_tsTimeoutMs = savedTo, g_tsPickMs = savedPm, g_tsKeep = savedKeep;
    g_tsAction = savedAction;
    g_tsSvcAt = savedAt;
    g_tsPickOrig = savedPick;
}

// TestOutfitFirstPick ([OutfitFirst], docs/684 §9): the swap hook notes only a SimService provider and passes its call
// on unchanged; the pick measures in Mode 1 (the game's order kept) and takes the noted job first in Mode 2.
static DWORD g_ofTestArgs[5];
static void __fastcall OfTestSwapOrig(u8* self, void* edx, DWORD outfit, DWORD cb, DWORD provider) {
    g_ofTestArgs[0] = (DWORD)(uintptr_t)self, g_ofTestArgs[1] = (DWORD)(uintptr_t)edx, g_ofTestArgs[2] = outfit, g_ofTestArgs[3] = cb, g_ofTestArgs[4] = provider;
}
static u8* __fastcall OfTestPickFront(u8* svc) {   // the game's pick, as far as the ring goes: the front job, unlinked
    u8* const head = svc + 0x60;
    u8* const n = *(u8**)head;
    if (n == head) return 0;
    OfUnlink(n);
    return n;
}
static void TestOutfitFirstPick() {
    static u8 svc[0x80], nodes[3][0x198], objA[0x40], objB[0x40];
    static DWORD vtGood[8], vtOther[8];
    const TsPickFn savedPick = g_tsPickOrig;
    const OfSwapFn savedSwap = g_ofSwapOrig;
    DWORD(WINAPI* const savedNow)() = g_tsNow;
    const int savedMode = g_ofMode;
    g_tsPickOrig = &OfTestPickFront, g_ofSwapOrig = &OfTestSwapOrig, g_tsNow = &TsTestNow;
    memset(&g_of, 0, sizeof(g_of));
    InterlockedExchange(&g_tsPickObj, 0);
    vtGood[4] = OF_REQ, vtOther[4] = OF_REQ + 0x10;
    *(DWORD**)(objA + 0x28) = vtGood, *(DWORD**)(objB + 0x28) = vtOther;
    u8* const h60 = svc + 0x60;
    const auto ring = [&]() {
        *(u8**)h60 = nodes[0], *(u8**)nodes[0] = nodes[1], *(u8**)nodes[1] = nodes[2], *(u8**)nodes[2] = h60;
        *(u8**)(nodes[0] + 4) = h60, *(u8**)(nodes[1] + 4) = nodes[0], *(u8**)(nodes[2] + 4) = nodes[1], *(u8**)(h60 + 4) = nodes[2];
    };
    *(DWORD*)(nodes[0] + 0x18) = 0x5000, *(DWORD*)(nodes[1] + 0x18) = 0x6000, *(DWORD*)(nodes[2] + 0x18) = (DWORD)(uintptr_t)objA;
    *(long*)(nodes[2] + 8) = 2;

    // the hook: arguments pass through; a provider with the RequestSim wrapper is noted as its object, others are not
    g_tsTestNow = 1000;
    OfSwap(svc, (void*)(uintptr_t)0x22, 0x33, 0x44, (DWORD)(uintptr_t)(objA + 0x28));
    assert(g_ofTestArgs[0] == (DWORD)(uintptr_t)svc && g_ofTestArgs[1] == 0x22 && g_ofTestArgs[2] == 0x33 && g_ofTestArgs[3] == 0x44);
    assert(g_ofTestArgs[4] == (DWORD)(uintptr_t)(objA + 0x28) && g_of.notes == 1 && OfFind(&g_of, (unsigned)(uintptr_t)objA) >= 0);
    OfSwap(svc, 0, 0, 0x44, (DWORD)(uintptr_t)(objB + 0x28));
    OfSwap(svc, 0, 0, 0x44, 0);
    OfSwap(svc, 0, 0, 0x44, 0x10);   // unreadable: caught, the call still goes on
    assert(g_of.notes == 1 && g_ofTestArgs[4] == 0x10);

    // off: the game's pick, nothing filed
    g_ofMode = 0;
    ring();
    assert(TsPick(svc) == nodes[0] && g_of.passes == 0);
    // Mode 1: the game's order (nodes 0, 1, then the swap's job), the swap's job passed twice and ready for 400 ms
    g_ofMode = 1;
    ring();
    g_tsTestNow = 2000;
    assert(TsPick(svc) == nodes[0] && g_of.passes == 1);
    g_tsTestNow = 2200;
    assert(TsPick(svc) == nodes[1] && g_of.passes == 2);
    g_tsTestNow = 2400;
    const LONG ready0 = g_ofReadyMs;
    assert(TsPick(svc) == nodes[2] && g_of.ea == 1 && g_of.first == 0 && g_ofReadyMs - ready0 == 400);
    assert(*(u8**)h60 == h60 && TsPick(svc) == 0);
    // Mode 2: the swap's job first, unlinked; then the game's order for the rest
    g_ofMode = 2;
    OfSwap(svc, 0, 0, 0x44, (DWORD)(uintptr_t)(objA + 0x28));
    ring();
    g_tsTestNow = 3000;
    assert(TsPick(svc) == nodes[2] && g_of.first == 1 && *(u8**)nodes[1] == h60 && *(u8**)(h60 + 4) == nodes[1]);
    assert(TsPick(svc) == nodes[0] && TsPick(svc) == nodes[1] && g_of.passes == 2);
    // [ThumbStall]'s own pick still comes before it
    OfSwap(svc, 0, 0, 0x44, (DWORD)(uintptr_t)(objA + 0x28));
    ring();
    InterlockedExchange(&g_tsPickObj, 0x6000), InterlockedExchange(&g_tsPickLod, 0);
    assert(TsPick(svc) == nodes[1]);
    InterlockedExchange(&g_tsPickObj, 0);
    assert(TsPick(svc) == nodes[2] && g_of.first == 2);
    memset(&g_of, 0, sizeof(g_of));
    g_tsPickOrig = savedPick, g_ofSwapOrig = savedSwap, g_tsNow = savedNow, g_ofMode = savedMode;
    printf("outfit first pick ok\n");
}

// Call-site check bytes carry each build's own rel32: Steam gives the literals back, EA 1.69 / retail 1.67 their own
// offsets (EA 0x6F565C call 0x6DDFE0 = 7F 89 FE FF, 0x5F1D75 call 0x5EE670 = F6 C8 FF FF; retail 0x5F1675 call 0x5EDEE0).
static void TestTsCallBytes() {
    u8 b[18];
    TsPickBytes(b);
    assert(memcmp(b, kTsPickSite, sizeof(kTsPickSite)) == 0);
    OfSiteBytes(b);
    assert(memcmp(b, kOfSite, sizeof(kOfSite)) == 0);
    TsCallBytes(b, kOfSite, 18, 7, 0x006DDFE0, 0x006F5656 + 11);
    static const u8 ofEa[4] = {0x7F, 0x89, 0xFE, 0xFF};
    assert(memcmp(b + 7, ofEa, 4) == 0 && b[6] == 0xE8 && b[11] == 0x80 && memcmp(b, kOfSite, 6) == 0);
    TsCallBytes(b, kTsPickSite, 12, 3, 0x005EE670, 0x005F1D73 + 7);
    static const u8 pickEa[4] = {0xF6, 0xC8, 0xFF, 0xFF};
    assert(memcmp(b + 3, pickEa, 4) == 0 && memcmp(b + 7, kTsPickSite + 7, 5) == 0);
    TsCallBytes(b, kTsPickSite, 12, 3, 0x005EDEE0, 0x005F1673 + 7);
    static const u8 pickRetail[4] = {0x66, 0xC8, 0xFF, 0xFF};
    assert(memcmp(b + 3, pickRetail, 4) == 0);
}
