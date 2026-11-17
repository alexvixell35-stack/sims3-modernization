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
