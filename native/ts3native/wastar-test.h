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
static void PfRunStub(void (*stub)(), DWORD a, DWORD c, DWORD d, const u8* world) {
    memset(g_waOut, 0, sizeof(g_waOut));
    g_pfRun = stub;
    __asm {
        push ebx
        push esi
        push edi
        mov esi, world
        mov ebx, 0x11111111
        mov edi, 0x33333333
        mov eax, a
        mov ecx, c
        mov edx, d
        call dword ptr [g_pfRun]
        pop edi
        pop esi
        pop ebx
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
                      sizeof(Ts3nCounters) == 5984 * 4,
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
    assert(PfIdOk(world, idOk) == 1 && PfIdOk(world, idPast) == 0 && PfIdOk(world, idOut) == 0 && PfIdOk(world, idHole) == 0);
    assert(PfPairOk(world, 1, 0) == 1 && PfPairOk(world, 0, 3) == 0 && PfPairOk(world, (DWORD)-43, 0x207) == 0 && PfPairOk(world, 4, 0) == 0);
    assert(k.pfSkip[0] == 3 && k.pfSkip[1] == 3);

    // writer 1: in range -> eax = R's chunk offsets, edx = id & 0x3FFF, ecx = id; else the next portal
    g_pf1Back = g_pf2Back = (DWORD)(uintptr_t)&WaTestBack, g_pf1Skip = g_pf2Skip = (DWORD)(uintptr_t)&WaTestSkip;
    PfRunStub(PfStub1, 0xAAAA, idOk, 0xDDDD, world);
    assert(g_waOut[0] == 1 && g_waOut[1] == (idOk & 0x3FFF) && g_waOut[2] == idOk && g_waOut[3] == (DWORD)(uintptr_t)offs);
    assert(g_waOut[4] == 0x11111111 && g_waOut[5] == (DWORD)(uintptr_t)world && g_waOut[6] == 0x33333333);
    PfRunStub(PfStub1, 0xAAAA, idPast, 0xDDDD, world);
    assert(g_waOut[0] == 2 && g_waOut[4] == 0x11111111 && g_waOut[5] == (DWORD)(uintptr_t)world && g_waOut[6] == 0x33333333);
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
    printf("WorldAStarRefs: patch bytes, in-range pairs kept (moved instructions run), chunk outside the map / waypoint past "
           "the chunk count / unreadable record skipped to the closed path; PortalFlagRefs: patch bytes, portal ids and lot "
           "neighbours outside R skip the blocked-flag write: ok\n");
}
