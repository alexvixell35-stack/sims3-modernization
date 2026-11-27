// TestWorldAStarRefs ([EaCrashFixes] WorldAStarRefs, wastar.cpp, docs/67 §30). Included by selftest.cpp.
// A synthetic routing graph R (2 x 2 chunks, one chunk record on a reserved page) and a fake A* frame; the stub runs for
// real, with its two exits pointed at recorders.

static DWORD g_waOut[7];  // exit (1 back, 2 skip), edx, ecx, eax, ebx, esi, edi at the exit
static __declspec(naked) void WaTestBack() {
    __asm {
        mov g_waOut[0], 1
        mov g_waOut[4], edx
        mov g_waOut[8], ecx
        mov g_waOut[12], eax
        mov g_waOut[16], ebx
        mov g_waOut[20], esi
        mov g_waOut[24], edi
        ret
    }
}
static __declspec(naked) void WaTestSkip() {
    __asm {
        mov g_waOut[0], 2
        mov g_waOut[12], eax
        mov g_waOut[16], ebx
        mov g_waOut[20], esi
        mov g_waOut[24], edi
        ret
    }
}

// PortalFlagRefs stubs: registers in, the same recorders out (no frame: both sites only use registers before the jump).
static void (*g_pfRun)();
static DWORD g_pfEdi = 0x33333333;  // writer 1 reads the portal through [edi]
static void PfRunStub(void (*stub)(), DWORD a, DWORD c, DWORD d, const u8* world) {
    memset(g_waOut, 0, sizeof(g_waOut));
    g_pfRun = stub;
    __asm {
        push ebx
        push esi
        push edi
        mov esi, world
        mov ebx, 0x11111111
        mov edi, g_pfEdi
        mov eax, a
        mov ecx, c
        mov edx, d
        call dword ptr [g_pfRun]
        pop edi
        pop esi
        pop ebx
    }
}

// HeightsInfLoop: flags from the site's comiss (xmm1 = the short-segment constant, xmm2 = L2 in all lanes), xmm7 = 0.5;
// the stub's exits are the recorders. g_hiX = xmm2 and xmm7 lane 0 after the stub.
static float g_hiX[2];
static float HiF(DWORD bits) { float f; memcpy(&f, &bits, 4); return f; }
static void HiRunStub(float c, float l2) {
    memset(g_waOut, 0, sizeof(g_waOut));
    g_hiX[0] = g_hiX[1] = 0;
    __asm {
        push ebx
        push esi
        push edi
        movss xmm1, c
        movss xmm2, l2
        shufps xmm2, xmm2, 0
        mov eax, 0x3F000000
        movd xmm7, eax
        mov ebx, 0x11111111
        mov edi, 0x33333333
        mov eax, 0x5A5A5A5A
        comiss xmm1, xmm2
        call HiStub
        movss g_hiX[0], xmm2
        movss g_hiX[4], xmm7
        pop edi
        pop esi
        pop ebx
    }
}

// BridgeHeightNaN: the stub runs with st0 = the bridge height, esi = ebx = a fake query object (the terrain grid at +54h),
// ebp = a fake query frame (x, z at +8 / +0Ch); its exits are the recorders. g_bhSlot = the site's [esp+14h] and [esp+20h]
// after the stub (the moved fstp writes one of them), g_bhSw = the x87 status word before the fld and after the stub.
static DWORD g_bhSlot[2];
static WORD g_bhSw[2];
static __declspec(naked) void __cdecl BhRun(void (* /*stub*/)(), DWORD /*bits*/, const void* /*obj*/, const void* /*frame*/) {
    __asm {
        push ebx
        push esi
        push edi
        push ebp
        mov esi, [esp + 28]
        mov ebx, esi
        mov edi, 0x33333333
        mov eax, [esp + 20]
        fnstsw word ptr g_bhSw
        fld dword ptr [esp + 24]
        mov ebp, [esp + 32]
        sub esp, 0x40
        mov dword ptr [esp + 0x10], 0x5A5A5A5A
        mov dword ptr [esp + 0x1C], 0x5A5A5A5A
        mov ecx, 0x22222222
        call eax
        mov eax, [esp + 0x10]
        mov g_bhSlot[0], eax
        mov eax, [esp + 0x1C]
        mov g_bhSlot[4], eax
        fnstsw word ptr g_bhSw[2]
        add esp, 0x40
        pop ebp
        pop edi
        pop esi
        pop ebx
        ret
    }
}

// FglStaleObject: the stub runs with esi = the looked-up object, edi = the id entry, ebp = a fake search frame; its exits
// are the recorders.
static __declspec(naked) void __cdecl FoRun(DWORD /*esi*/, const DWORD* /*edi*/, const u8* /*ebp*/) {
    __asm {
        push ebx
        push esi
        push edi
        push ebp
        mov esi, [esp + 20]
        mov edi, [esp + 24]
        mov ebp, [esp + 28]
        mov ebx, 0x11111111
        mov eax, 0x5A5A5A5A
        call FoStub
        pop ebp
        pop edi
        pop esi
        pop ebx
        ret
    }
}

// The stub's esp is the frame below this call's return address: fields at [esp + off - 4] here are at [sp + off] there.
static void WaRunStub(const u8* r, DWORD chunk, DWORD wp, const u8* rec, const WORD* cur) {
    memset(g_waOut, 0, sizeof(g_waOut));
    __asm {
        push ebx
        push esi
        push edi
        sub esp, 0x100
        mov eax, r
        mov [esp + 0x50 - 4], eax
        mov eax, chunk
        mov word ptr [esp + 0x1C - 4], ax
        mov eax, wp
        mov word ptr [esp + 0x1E - 4], ax
        mov eax, rec
        mov [esp + 0x2C - 4], eax
        mov byte ptr [esp + 0x58 - 4], 1
        mov esi, cur
        mov ebx, 0x11111111
        mov edi, 0x33333333
        mov eax, wp
        or eax, 0xABCD0000  // the game's eax holds more than the waypoint; movzx ecx,ax must take the low word
        call WaStub
        add esp, 0x100
        pop edi
        pop esi
        pop ebx
    }
}

static void TestWorldAStarRefs() {
    static_assert(offsetof(Ts3nCounters, mdOn) == 0x5520 && offsetof(Ts3nCounters, waOn) == 0x5570 &&
                      offsetof(Ts3nCounters, waSkip) == 0x5574 && offsetof(Ts3nCounters, pfSkip) == 0x5584 &&
                      sizeof(Ts3nCounters) == 6894 * 4,
                  "counters v146 layout after v145 (docs/registry.md)");
    Ts3nCounters& k = ts3native_counters;

    // patch: foreign bytes refused and untouched; the game's bytes become jmp WaStub + 2-byte nop; ours are not patched again
    alignas(4) static u8 site[8];
    memcpy(site, kWaSite, 7), site[2] = 0x25;
    u8 orig[8];
    memcpy(orig, site, 8);
    assert(!WaPatch(site) && !memcmp(site, orig, 8));
    site[2] = 0x24;
    assert(WaPatch(site));
    assert(site[0] == 0xE9 && (uintptr_t)site + 5 + *(LONG*)(site + 1) == (uintptr_t)&WaStub && site[5] == 0x66 && site[6] == 0x90);
    assert(!WaPatch(site));

    // R: w 2, h 2; chunk counts 3, 1, 0; chunk 3's record on a reserved page (unreadable)
    alignas(4) static u8 area[64];
    alignas(4) static DWORD offs[4];
    alignas(4) static u8 r[0x14];
    u8* hole = (u8*)VirtualAlloc(NULL, 4096, MEM_RESERVE, PAGE_NOACCESS);
    assert(hole);
    *(WORD*)(area + 0) = 3, *(WORD*)(area + 16) = 1, *(WORD*)(area + 32) = 0;
    offs[0] = 0, offs[1] = 16, offs[2] = 32, offs[3] = (DWORD)((uintptr_t)hole - (uintptr_t)area);
    *(DWORD*)(r + 0) = 2, *(DWORD*)(r + 4) = 2, *(DWORD**)(r + 8) = offs, *(u8**)(r + 0xC) = area;
    alignas(4) static u8 rec[16] = {2, 0x10, 0, 0, 0, 0x07, 0x4E, 0x34, 0x12};  // n 2, flags, refs 0x4E07 0x1234
    static const WORD cur[2] = {21, 5};

    alignas(4) static u8 sp[0x60];
    *(u8**)(sp + 0x50) = r, *(u8**)(sp + 0x2C) = rec, sp[0x58] = 1;
    struct Case { DWORD chunk, wp; LONG ok, why; } cases[] = {
        {0, 2, 1, 0}, {0, 0, 1, 0}, {4, 0, 0, 1}, {0xFFD5, 0x207, 0, 1}, {1, 1, 0, 2}, {2, 0, 0, 2}, {3, 0, 0, 3}};
    k.waSkip[0] = k.waSkip[1] = k.waSkip[2] = 0;
    LONG want[3] = {0, 0, 0};
    for (const Case& c : cases) {
        *(WORD*)(sp + 0x1C) = (WORD)c.chunk, *(WORD*)(sp + 0x1E) = (WORD)c.wp;
        assert(WaRefOk(sp, cur) == c.ok);
        if (c.why) want[c.why - 1]++;
        assert(k.waSkip[0] == want[0] && k.waSkip[1] == want[1] && k.waSkip[2] == want[2]);
    }

    // the stub: in-range pair -> the moved instructions run and it continues at WA_BACK; out of R -> WA_SKIP; callee-saved kept
    g_waBack = (DWORD)(uintptr_t)&WaTestBack, g_waSkip = (DWORD)(uintptr_t)&WaTestSkip;
    WaRunStub(r, 0, 2, rec, cur);
    assert(g_waOut[0] == 1 && g_waOut[1] == (0u | (2u << 16)) && g_waOut[2] == 2 && g_waOut[3] == (0xABCD0000u | 2));
    assert(g_waOut[4] == 0x11111111 && g_waOut[5] == (DWORD)(uintptr_t)cur && g_waOut[6] == 0x33333333);
    WaRunStub(r, 1, 0, rec, cur);
    assert(g_waOut[0] == 1 && g_waOut[1] == (1u | (0u << 16)) && g_waOut[2] == 0);
    WaRunStub(r, 0xFFD5, 0x207, rec, cur);
    assert(g_waOut[0] == 2 && g_waOut[3] == (0xABCD0000u | 0x207) && g_waOut[4] == 0x11111111 && g_waOut[6] == 0x33333333);
    WaRunStub(r, 1, 1, rec, cur);
    assert(g_waOut[0] == 2);
    assert(k.waSkip[0] == want[0] + 1 && k.waSkip[1] == want[1] + 1);

    g_waBack = WA_BACK, g_waSkip = WA_SKIP;
    k.waSkip[0] = k.waSkip[1] = k.waSkip[2] = 0;

    // PortalFlagRefs: both patches; the same R inside a routing world (R at +0x54)
    alignas(4) static u8 s1[11], s2[6];
    memcpy(s1, kPf1Site, 11), memcpy(s2, kPf2Site, 6), s1[1] = 0x47;
    assert(!PfPatch(s1, kPf1Site, 11, PfStub1) && s1[1] == 0x47);
    s1[1] = 0x46;
    assert(PfPatch(s1, kPf1Site, 11, PfStub1) && s1[0] == 0xE9 && (uintptr_t)s1 + 5 + *(LONG*)(s1 + 1) == (uintptr_t)&PfStub1 && s1[10] == 0xCC);
    assert(!PfPatch(s1, kPf1Site, 11, PfStub1));
    assert(PfPatch(s2, kPf2Site, 6, PfStub2) && (uintptr_t)s2 + 5 + *(LONG*)(s2 + 1) == (uintptr_t)&PfStub2 && s2[5] == 0xCC);
    alignas(4) static u8 world[0x64];
    memcpy(world + 0x54, r, 0x10);
    k.pfSkip[0] = k.pfSkip[1] = 0;
    const DWORD idOk = (2u << 14) | 0 | (1u << 29), idPast = (1u << 14) | 1, idOut = 0x3FFF, idHole = 3;
    alignas(4) static u8 portal[0x58];
    static const u8* slot = portal;
    auto PfId = [](const u8* wd, DWORD id) { *(DWORD*)(portal + 0x4C) = id; return PfIdChunk(wd, portal); };
    assert(PfId(world, idOk) == 0 && PfId(world, idPast) == -1 && PfId(world, idOut) == -1 && PfId(world, idHole) == -1);
    assert(PfPairOk(world, 1, 0) == 1 && PfPairOk(world, 0, 3) == 0 && PfPairOk(world, (DWORD)-43, 0x207) == 0 && PfPairOk(world, 4, 0) == 0);
    assert(k.pfSkip[0] == 3 && k.pfSkip[1] == 3);

    // writer 1: in range -> eax = R's chunk offsets, edx = id & 0x3FFF, ecx = id, edi (the portal slot) kept; else the next portal
    g_pf1Back = g_pf2Back = (DWORD)(uintptr_t)&WaTestBack, g_pf1Skip = g_pf2Skip = (DWORD)(uintptr_t)&WaTestSkip;
    g_pfEdi = (DWORD)(uintptr_t)&slot;
    *(DWORD*)(portal + 0x4C) = idOk;
    PfRunStub(PfStub1, 0xAAAA, idOk, 0xDDDD, world);
    assert(g_waOut[0] == 1 && g_waOut[1] == (idOk & 0x3FFF) && g_waOut[2] == idOk && g_waOut[3] == (DWORD)(uintptr_t)offs);
    assert(g_waOut[4] == 0x11111111 && g_waOut[5] == (DWORD)(uintptr_t)world && g_waOut[6] == g_pfEdi);
    *(DWORD*)(portal + 0x4C) = idPast;
    PfRunStub(PfStub1, 0xAAAA, idPast, 0xDDDD, world);
    assert(g_waOut[0] == 2 && g_waOut[4] == 0x11111111 && g_waOut[5] == (DWORD)(uintptr_t)world && g_waOut[6] == g_pfEdi);
    {   // docs/362 §6: the chunk rebuild. Formula cases as Atlas's self-check (docs/026), then the stub on a 256 x 256 graph.
        int how = 0;
        long wrong = 0, notRebuilt = 0;
        for (long c = 0; c < 65536; c++)  // every chunk of 8192 m, the portal in it and in each of its 8 neighbours
            for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++) {
                    const long col = c % 256 + dx, row = c / 256 + dz;
                    if (col < 0 || col > 255 || row < 0 || row > 255) continue;
                    const DWORD id = 0xA5000000u | (7u << 14) | ((DWORD)c & 0x3FFF);
                    if (PfFullChunk(id, col * 32.0f + 16.0f, row * 32.0f + 16.0f, 0, 0, 256, 256, &how) != (DWORD)c) wrong++;
                    if (how != 1) notRebuilt++;
                }
        assert(!wrong && !notRebuilt);
        assert(PfFullChunk(16383, 1.0f, 2047.0f, 0, 0, 256, 256, &how) == 16383 && how == 1);  // last 14-bit chunk stays
        assert(PfFullChunk(0, 1.0f, 2049.0f, 0, 0, 256, 256, &how) == 16384 && how == 1);      // first chunk past 14 bits
        assert(PfFullChunk(65535 & 0x3FFF, 8191.0f, 8191.0f, 0, 0, 256, 256, &how) == 65535 && how == 1);
        assert(PfFullChunk((40000 + 31 * 256) & 0x3FFF, 2080.0f, 5000.0f, 0, 0, 256, 256, &how) == 40000 + 31 * 256 && how == 1);  // 31 rows from the portal
        assert(PfFullChunk(20000 & 0x3FFF, 1032.0f + 500.0f, 2500.0f + 300.0f, 500.0f, 300.0f, 256, 256, &how) == 20000 && how == 1);  // world origin
        assert(PfFullChunk((93 * 256 + 128) & 0x3FFF, 4100.0f, 3000.0f, 0, 0, 256, 128, &how) == 93 * 256 + 128 && how == 1);  // 8192 x 4096 m
        // 16,384 chunks or fewer: the vanilla 14 bits whatever the position says
        assert(PfFullChunk(0xFF000000u | 5000, 9000.0f, 9000.0f, 0, 0, 128, 128, &how) == 5000 && how == 0);
        assert(PfFullChunk(0xFFFFFFFF, 100.0f, 100.0f, 0, 0, 64, 64, &how) == 0x3FFF && how == 0);
        for (DWORD id = 0; id < 0x10000; id += 7) assert(PfFullChunk(id * 0x10001u, 8000.0f, 8000.0f, 0, 0, 128, 128, &how) == ((id * 0x10001u) & 0x3FFF) && how == 0);
        // could not rebuild: the stored bits
        assert(PfFullChunk(0x00004123, 0, 0, 0, 0, 256, 256, &how) == 0x123 && how == 2);  // position never set
        assert(PfFullChunk(0x20000000, 0, 0, 0, 0, 256, 256, &how) == 0 && how == 0);      // no reference, no position
        assert(PfFullChunk(100, -40.0f, 100.0f, 0, 0, 256, 256, &how) == 100 && how == 2);  // left of the graph
        assert(PfFullChunk(100, 100.0f, 9000.0f, 0, 0, 256, 256, &how) == 100 && how == 2); // below it
        assert(PfFullChunk(16000, 8.0f, 8.0f, 0, 0, 256, 256, &how) == 16000 && how == 2);  // would be negative
        assert(PfFullChunk(100, 100.0f, 100.0f, 0, 0, 512, 512, &how) == 100 && how == 2);  // past 65,536 chunks
        assert(PfFullChunk(100, HiF(0x7FC00000), 5.0f, 0, 0, 256, 256, &how) == 100 && how == 2);  // NaN
        // the stub: a 256 x 256 graph whose every chunk has the one record at area + 0 (1 waypoint)
        static DWORD offsBig[65536];
        alignas(4) static u8 worldBig[0x64], wm[0xC0];
        static const u8* wmP = wm;
        static const u8* noWm = NULL;
        *(DWORD*)(worldBig + 0x54) = 256, *(DWORD*)(worldBig + 0x58) = 256, *(DWORD**)(worldBig + 0x5C) = offsBig, *(u8**)(worldBig + 0x60) = area;
        const u8* const* savedWm = g_pfWorldMgr;
        g_pfWorldMgr = &wmP;
        const LONG rb0 = g_pfRebuilt, fb0 = g_pfFallback, sk0 = k.pfSkip[0];
        const DWORD chunk = 200 * 256 + 77;  // 51,277: needs 16 bits
        const DWORD idBig = 0x1A000000u | (chunk & 0x3FFF);
        *(DWORD*)(portal + 0x4C) = idBig, *(float*)(portal + 0x50) = 77 * 32.0f + 3.0f, *(float*)(portal + 0x54) = 200 * 32.0f + 30.0f;
        assert(*(const WORD*)area >= 1);
        PfRunStub(PfStub1, 0xAAAA, idBig, 0xDDDD, worldBig);
        assert(g_waOut[0] == 1 && g_waOut[1] == chunk && g_waOut[2] == idBig && g_waOut[3] == (DWORD)(uintptr_t)offsBig && g_waOut[6] == g_pfEdi);
        assert(g_pfRebuilt == rb0 + 1 && g_pfFallback == fb0);
        *(DWORD*)(worldBig + 0x54) = *(DWORD*)(worldBig + 0x58) = 128;  // 4096 m: the vanilla 14 bits, no rebuild counted
        PfRunStub(PfStub1, 0xAAAA, idBig, 0xDDDD, worldBig);
        assert(g_waOut[0] == 1 && g_waOut[1] == (chunk & 0x3FFF) && g_waOut[2] == idBig && g_pfRebuilt == rb0 + 1);
        *(DWORD*)(worldBig + 0x54) = *(DWORD*)(worldBig + 0x58) = 256;
        g_pfWorldMgr = &noWm;  // no world manager: the stored bits, one fallback
        PfRunStub(PfStub1, 0xAAAA, idBig, 0xDDDD, worldBig);
        assert(g_waOut[0] == 1 && g_waOut[1] == (chunk & 0x3FFF) && g_pfFallback == fb0 + 1 && k.pfSkip[0] == sk0);
        g_pfWorldMgr = savedWm, g_pfRebuilt = rb0, g_pfFallback = fb0;
    }
    g_pfEdi = 0x33333333;
    // writer 2: in range -> ecx = the chunk's record base, eax and edx kept; else the next reference
    PfRunStub(PfStub2, 0, 1, (DWORD)(uintptr_t)offs, world);
    assert(g_waOut[0] == 1 && g_waOut[2] == (DWORD)(uintptr_t)(area + 16) && g_waOut[3] == 0 && g_waOut[1] == (DWORD)(uintptr_t)offs);
    PfRunStub(PfStub2, 0x207, (DWORD)-43, (DWORD)(uintptr_t)offs, world);
    assert(g_waOut[0] == 2 && g_waOut[3] == 0x207 && g_waOut[6] == 0x33333333);
    assert(k.pfSkip[0] == 4 && k.pfSkip[1] == 4);
    g_pf1Back = PF1_BACK, g_pf1Skip = PF1_SKIP, g_pf2Back = PF2_BACK, g_pf2Skip = PF2_SKIP;
    k.pfSkip[0] = k.pfSkip[1] = 0;
    VirtualFree(hole, 0, MEM_RELEASE);
    // LotGridMiss: the je keeps its opcode and points at LgStub; other bytes are left alone; each hit counts
    alignas(4) static u8 lg[6];
    memcpy(lg, kLgSite, 6), lg[2] = 0xFA;
    assert(!LgPatch(lg) && lg[2] == 0xFA);
    lg[2] = 0xF9;
    assert(LgPatch(lg) && lg[0] == 0x0F && lg[1] == 0x84 && (uintptr_t)lg + 6 + *(LONG*)(lg + 2) == (uintptr_t)&LgStub);
    assert(!LgPatch(lg));
    alignas(4) static u8 lot[0x50];
    *(DWORD*)(lot + 0x48) = 0x536284F0, *(DWORD*)(lot + 0x4C) = 0x08F30053;
    const LONG h0 = g_lgHits;
    LgHit(lot), LgHit(lot);
    assert(g_lgHits == h0 + 2);
    g_lgHits = h0;
    // HeightsInfLoop: the ja becomes jmp HiStub + int3; only L2 = +inf changes path, registers kept
    alignas(4) static u8 hi[6];
    memcpy(hi, kHiSite, 6), hi[2] = 0xB6;
    assert(!HiPatch(hi) && hi[2] == 0xB6);
    hi[2] = 0xB5;
    assert(HiPatch(hi) && hi[0] == 0xE9 && hi[5] == 0xCC && (uintptr_t)hi + 5 + *(LONG*)(hi + 1) == (uintptr_t)&HiStub);
    assert(!HiPatch(hi));
    g_hiBack = (DWORD)(uintptr_t)&WaTestBack, g_hiSkip = (DWORD)(uintptr_t)&WaTestSkip;
    const LONG hh0 = g_hiHits;
    const float inf = HiF(0x7F800000);
    HiRunStub(4.0f, 1.0f);  // short segment: the game's ja
    assert(g_waOut[0] == 2 && g_waOut[3] == 0x5A5A5A5A && g_hiHits == hh0);
    HiRunStub(4.0f, 9.0f);  // a normal segment: on into the loop
    assert(g_waOut[0] == 1 && g_waOut[3] == 0x5A5A5A5A && g_waOut[4] == 0x11111111 && g_waOut[6] == 0x33333333 && g_hiHits == hh0);
    HiRunStub(4.0f, HiF(0x7F7FFFFF));  // FLT_MAX: finite, the loop ends (about 64 halvings)
    assert(g_waOut[0] == 1 && g_hiHits == hh0);
    HiRunStub(4.0f, HiF(0x7FC00000));  // NaN: the game's way (the loop leaves at once)
    assert(g_waOut[0] == 1 && g_hiHits == hh0);
    HiRunStub(4.0f, inf);  // +inf: the no-split path, counted, eax / ebx / edi / xmm2 / xmm7 kept
    assert(g_waOut[0] == 2 && g_waOut[3] == 0x5A5A5A5A && g_waOut[4] == 0x11111111 && g_waOut[6] == 0x33333333);
    assert(g_hiHits == hh0 + 1 && g_hiX[0] == inf && g_hiX[1] == 0.5f);
    g_hiBack = HI_BACK, g_hiSkip = HI_SKIP, g_hiHits = hh0;
    // BridgeHeightNaN: each site after a call to 0xB745A0 becomes jmp kBhStub[i] + 2 int3; foreign bytes or another call refused
    static_assert(offsetof(Ts3nCounters, bhnHits) == 0x62C8 && sizeof(Ts3nCounters) == 6894 * 4,
                  "counters v918 layout after the v195 placeholder (docs/registry.md)");
    for (int i = 0; i < 3; i++) {
        alignas(4) static u8 bh[12];
        bh[0] = 0xE8, *(LONG*)(bh + 1) = (LONG)(BH_CALL - ((DWORD)(uintptr_t)bh + 5)), memcpy(bh + 5, kBhSite[i], 7);
        bh[11] ^= 0x7C;  // another stack slot: refused
        assert(!BhPatch(bh + 5, i) && bh[5] == 0x8B);
        bh[11] ^= 0x7C, bh[1] ^= 1;  // a call to another function: refused
        assert(!BhPatch(bh + 5, i) && bh[5] == 0x8B);
        bh[1] ^= 1;
        assert(BhPatch(bh + 5, i) && bh[5] == 0xE9 && bh[10] == 0xCC && bh[11] == 0xCC &&
               (uintptr_t)bh + 10 + *(LONG*)(bh + 6) == (uintptr_t)kBhStub[i]);
        assert(!BhPatch(bh + 5, i));
    }
    // the decision, run through the real stubs: a sane bridge height is the game's answer (the moved fstp, ecx = the terrain
    // grid); NaN, +-inf, 2.26389e34 and anything above 1e6 in magnitude take the query's no-bridge path, popped and counted
    for (int i = 0; i < 3; i++) g_bhBack[i] = (DWORD)(uintptr_t)&WaTestBack, g_bhSkip[i] = (DWORD)(uintptr_t)&WaTestSkip;
    const LONG bh0 = k.bhnHits, bl0 = g_bhLogged;
    static DWORD bhObj[0x20], bhFrame[4];
    bhObj[0x54 / 4] = 0x44444444;
    bhFrame[2] = 0x44534000, bhFrame[3] = 0x44544000;  // x 845, z 849 (a point that failed on Isla Paradiso)
    static const DWORD kSane[] = {0x420ECCCD /* 35.7 */, 0xC2C80000 /* -100 */, 0, 0x80000000, 1, 0x49742400 /* 1e6 */, 0xC9742400};
    static const DWORD kBad[] = {0x7FC00000 /* NaN */, 0xFFC00000, 0x7F800000 /* +inf */, 0xFF800000 /* -inf */,
                                 0x788B85D4 /* 2.26389e34, the dump's y */, 0xF88B85D4, 0x49742401 /* just above 1e6 */};
    for (int i = 0; i < 3; i++) {
        for (DWORD h : kSane) {
            memset(g_waOut, 0, sizeof(g_waOut));
            BhRun(kBhStub[i], h, bhObj, bhFrame);
            assert(g_waOut[0] == 1 && g_waOut[2] == 0x44444444 && g_waOut[4] == (DWORD)(uintptr_t)bhObj &&
                   g_waOut[5] == (DWORD)(uintptr_t)bhObj && g_waOut[6] == 0x33333333);
            assert(g_bhSlot[i == 2] == h && g_bhSlot[i != 2] == 0x5A5A5A5A && (g_bhSw[0] & 0x3800) == (g_bhSw[1] & 0x3800));
        }
        for (DWORD h : kBad) {
            memset(g_waOut, 0, sizeof(g_waOut));
            BhRun(kBhStub[i], h, bhObj, bhFrame);
            assert(g_waOut[0] == 2 && g_waOut[4] == (DWORD)(uintptr_t)bhObj && g_waOut[5] == (DWORD)(uintptr_t)bhObj &&
                   g_waOut[6] == 0x33333333);
            assert(g_bhSlot[0] == 0x5A5A5A5A && g_bhSlot[1] == 0x5A5A5A5A && (g_bhSw[0] & 0x3800) == (g_bhSw[1] & 0x3800));
        }
    }
    assert(k.bhnHits == bh0 + 3 * (LONG)(sizeof(kBad) / sizeof(kBad[0])));
    memset(g_bhBack, 0, sizeof(g_bhBack)), memset(g_bhSkip, 0, sizeof(g_bhSkip));
    k.bhnHits = bh0, g_bhLogged = bl0;
    // FglStaleObject: the compare's 7 bytes become jmp FoStub + 2 int3; only a NULL object takes the missing-object path
    alignas(4) static u8 fo[7], foNul[4];
    memcpy(fo, kFoSite, sizeof(fo)), memcpy(foNul, kFoNull, sizeof(foNul));
    foNul[3] = 0x32;  // another NULL test: refused
    assert(!FoPatch(fo, foNul) && fo[0] == 0x33);
    foNul[3] = 0x31, fo[4] = 0x50;  // another frame slot: refused
    assert(!FoPatch(fo, foNul) && fo[0] == 0x33);
    fo[4] = 0x54;
    assert(FoPatch(fo, foNul) && fo[0] == 0xE9 && fo[5] == 0xCC && fo[6] == 0xCC &&
           (uintptr_t)fo + 5 + *(LONG*)(fo + 1) == (uintptr_t)&FoStub);
    assert(!FoPatch(fo, foNul));
    g_foBack = (DWORD)(uintptr_t)&WaTestBack, g_foNull = (DWORD)(uintptr_t)&WaTestSkip;
    const LONG fh0 = g_foHits;
    static DWORD foId[2] = {0x63EFEDE0, 0x320800F5};
    alignas(4) static u8 foFrame[0x60], foObj[0x10];
    *(u8**)(foFrame + 8) = foObj, *(DWORD*)foObj = 0x01042CA8;
    *(LONG*)(foFrame + 0x54) = 2;
    memset(g_waOut, 0, sizeof(g_waOut));
    FoRun((DWORD)(uintptr_t)foObj, foId, foFrame);  // a live object, an ignore list of 2: the game's compare at 0xB11A9C
    assert(g_waOut[0] == 1 && g_waOut[3] == 0 && g_waOut[4] == 0x11111111 && g_waOut[5] == (DWORD)(uintptr_t)foObj &&
           g_waOut[6] == (DWORD)(uintptr_t)foId && g_foHits == fh0);
    *(LONG*)(foFrame + 0x54) = 0;
    memset(g_waOut, 0, sizeof(g_waOut));
    FoRun((DWORD)(uintptr_t)foObj, foId, foFrame);  // an empty ignore list: the game's jle
    assert(g_waOut[0] == 2 && g_waOut[3] == 0 && g_waOut[5] == (DWORD)(uintptr_t)foObj && g_foHits == fh0);
    *(LONG*)(foFrame + 0x54) = 2;
    memset(g_waOut, 0, sizeof(g_waOut));
    FoRun(0, foId, foFrame);  // a deleted object: the missing-object path, logged, registers kept
    assert(g_waOut[0] == 2 && g_waOut[3] == 0x5A5A5A5A && g_waOut[4] == 0x11111111 && g_waOut[5] == 0 &&
           g_waOut[6] == (DWORD)(uintptr_t)foId && g_foHits == fh0 + 1);
    g_foBack = FO_BACK, g_foNull = FO_NULL, g_foHits = fh0;
    printf("WorldAStarRefs:patch bytes, in-range pairs kept (moved instructions run), chunk outside the map / waypoint past "
           "the chunk count / unreadable record skipped to the closed path; PortalFlagRefs: patch bytes, portal ids, the chunk rebuild above 16,384 chunks (docs/362) and lot "
           "neighbours outside R skip the blocked-flag write; HeightsInfLoop: patch bytes, only an infinite segment skips "
           "the halving loop, registers kept; BridgeHeightNaN: patch bytes, sane bridge heights kept, NaN / +-inf / 2.26e34 / "
           "above 1e6 take the no-bridge path, x87 balanced, registers kept; FglStaleObject: patch bytes, live objects and an "
           "empty ignore list keep the game's path, a deleted object takes the missing-object path, registers kept: ok\n");
}
