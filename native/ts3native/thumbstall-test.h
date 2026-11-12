// TestThumbLod ([ThumbStall] Actions 3 and 4, thumbstall.cpp, 2026-11-12). Included by selftest.cpp.
// A fake SimService for TsLodRead, the two ActiveLODLimit stubs run on a frame shaped like their sites, and the fallback's
// end detection on a fake manager.
static void* g_tsTestStub;
static u8 *g_tsTestEsi, *g_tsTestEbp;
static DWORD g_tsTestProv, g_tsTestOut;  // out: bit 0 the site's jle is taken, bit 1 ecx kept, bit 2 edx kept
static __declspec(naked) void TsLimHarness() {
    __asm {
        push esi
        push ebp
        push ebx
        mov esi, g_tsTestEsi
        mov ebp, g_tsTestEbp
        push g_tsTestProv  // the provider sits at [esp+10h] at both sites
        push 0
        push 0
        push 0
        push 0
        mov ecx, 0x5A5A5A5A
        mov edx, 0x3C3C3C3C
        call dword ptr g_tsTestStub
        setle bl
        movzx ebx, bl
        cmp ecx, 0x5A5A5A5A
        jne h1
        or ebx, 2
    h1:
        cmp edx, 0x3C3C3C3C
        jne h2
        or ebx, 4
    h2:
        mov g_tsTestOut, ebx
        add esp, 0x14
        pop ebx
        pop ebp
        pop esi
        ret
    }
}
static DWORD TsLimRun(void* stub, u8* esiSvc, u8* ebpSvc, DWORD prov) {
    g_tsTestStub = stub, g_tsTestEsi = esiSvc, g_tsTestEbp = ebpSvc, g_tsTestProv = prov;
    TsLimHarness();
    return g_tsTestOut;
}
static void TestThumbLod() {
    static u8 svc[0x80], low[0x80], nodes[3][0x198];
    static u8* svcp = svc;
    const DWORD savedAt = g_tsSvcAt;
    g_tsSvcAt = (DWORD)(uintptr_t)&svcp;
    *(DWORD*)svc = TS_SVC_VT;
    *(long*)(svc + 0x30) = 1, *(long*)(svc + 0x40) = 200, *(long*)(svc + 0x4C) = 231, *(long*)(svc + 0x50) = 260;
    u8* const h58 = svc + 0x58;
    u8* const h60 = svc + 0x60;
    *(u8**)h58 = nodes[0], *(u8**)nodes[0] = nodes[1], *(u8**)nodes[1] = h58;  // loading: two jobs
    *(u8**)h60 = h60;                                                        // ready: none
    *(u8**)(svc + 0x68) = nodes[2];                                          // one current
    const u8* const prov = (const u8*)(uintptr_t)0x12345678;
    *(const u8**)(nodes[1] + 0x18) = prov;
    *(long*)(nodes[1] + 8) = 1, *(long*)(nodes[1] + 0xC) = 7, *(long*)(nodes[1] + 0x190) = 4;
    TsLod L;
    TsLodRead(prov, &L);
    assert(L.inuse == 231 && L.limit == 200 && L.cached == 260 && L.minLod == 1);
    assert(L.jobs == 2 && L.ready == 0 && L.current == 1);
    assert(!strcmp(L.job, "loading #1 lod 1 requests 7 state 4"));
    TsLodRead((const u8*)(uintptr_t)0x1111, &L);
    assert(!strcmp(L.job, "none"));
    *(const u8**)(nodes[2] + 0x18) = (const u8*)(uintptr_t)0x1111;
    TsLodRead((const u8*)(uintptr_t)0x1111, &L);
    assert(!strncmp(L.job, "current lod 0", 13));
    *(DWORD*)svc = 0;
    TsLodRead(prov, &L);
    assert(L.inuse == -1 && !strcmp(L.job, "no service"));
    *(DWORD*)svc = TS_SVC_VT;
    g_tsSvcAt = savedAt;

    // the stubs: over the limit (231 > 200) the site's jle is not taken, except for the exempt provider
    *(long*)(low + 0x40) = 200, *(long*)(low + 0x4C) = 5;
    InterlockedExchange(&g_tsExProv, 0);
    const LONG uses0 = g_tsExUses;
    assert(TsLimRun((void*)&TsLimReqStub, svc, low, 0x1234) == 6);
    assert(TsLimRun((void*)&TsLimEnqStub, low, svc, 0x1234) == 6);  // the enqueue site reads ebp, not esi
    assert(TsLimRun((void*)&TsLimReqStub, low, svc, 0x1234) == 7);  // under the limit: EA's own result
    InterlockedExchange(&g_tsExProv, 0x1234);
    assert(TsLimRun((void*)&TsLimReqStub, svc, low, 0x1234) == 7);
    assert(TsLimRun((void*)&TsLimEnqStub, low, svc, 0x1234) == 7);
    assert(TsLimRun((void*)&TsLimReqStub, svc, low, 0x9999) == 6);  // another provider stays refused
    assert(g_tsExUses - uses0 == 2);
    InterlockedExchange(&g_tsExProv, 0);
    assert(TsLimRun((void*)&TsLimReqStub, svc, low, 0) == 6);

    // end detection: a fake manager whose front request is still in 0x11, then moves on
    static u8 mgr[0x120], req[0x248], blk[0x260];
    *(u8**)(mgr + 0x100) = req, *(u8**)(mgr + 0x110) = req + 0x248;
    *(DWORD*)(req + 0x23C) = 77, *(DWORD*)(req + 0x108) = 0x11;
    *(long*)(blk + 0x210 + 0x14) = -1, *(long*)(blk + 0x210 + 0x18) = 1, blk[0x210 + 0x22] = 1, blk[0x210 + 0x23] = 0xFF;
    *(DWORD*)(blk + 0x24C) = 0x4321;
    const int savedAction = g_tsAction;
    g_tsAction = 3;
    TsFbStart(req, 77, blk, GetTickCount());
    assert(g_tsFbReq == req && g_tsExProv == 0x4321);
    const LONG r0 = g_tsFbRendered, o0 = g_tsFbOther, a0 = g_tsFbAborted;
    TsFbWatch(mgr);
    assert(g_tsFbReq == req && g_tsExProv == 0x4321);  // still waiting
    *(DWORD*)(req + 0x108) = 0x12;
    TsFbWatch(mgr);
    assert(!g_tsFbReq && !g_tsExProv && g_tsFbRendered == r0 + 1);
    *(DWORD*)(req + 0x108) = 0x11;
    TsFbStart(req, 77, blk, GetTickCount());
    *(DWORD*)(req + 0x23C) = 78;  // a new request in the same slot
    TsFbWatch(mgr);
    assert(!g_tsFbReq && !g_tsExProv && g_tsFbOther == o0 + 1);
    *(DWORD*)(req + 0x23C) = 77;
    g_tsAction = 4;
    TsFbStart(req, 77, blk, GetTickCount());
    assert(blk[0x210 + 0x22] == 0 && !g_tsExProv);
    TsFbEnd(1, 0x11, true);
    assert(!g_tsFbReq && g_tsFbAborted == a0 + 1);
    g_tsAction = savedAction;
}
