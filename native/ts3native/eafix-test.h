// TestEaCrashFixes ([EaCrashFixes] ConformActor + ThumbPose + GeomBoneIndex + MintDivGuard, eafix.cpp, docs/67 §26.4 / §27.5 /
// §28, docs/213 §8). Included by selftest.cpp.
// Fake AnimationComponents, controllers and refcounted owners; the patched ask sequence and the after-evaluation stub run
// for real on copies of the game's bytes.

static DWORD g_eaVts[3][8];  // [0] live AnimationComponent +8 stand-in, [1] another actor type, [2] slot +0x10 outside .text
static LONG g_eaAsks;
static u8* g_eaAskActor;
static bool __fastcall EaFakeAsk(u8* actor, void*, u8* ctl) {
    g_eaAsks++, g_eaAskActor = actor;
    return ctl[0x24] == 0;  // any answer the caller can see
}
static void* __fastcall EaFakeCtor(u8* comp, void*, void* owner) {
    *(void**)(comp + 4) = owner, *(DWORD*)(comp + 8) = (DWORD)(uintptr_t)g_eaVts[0];
    return comp;
}
static void __fastcall EaFakeDtor(u8* comp) { *(DWORD*)(comp + 8) = CA_ANIM_VT8; }  // EA's dtor leaves this vtable word

static LONG g_eaReleases, g_eaFreed;
static void __fastcall EaFakeRelease(u8** ref) {
    g_eaReleases++;
    if (*ref && --*(LONG*)(*ref + 4) == 0) g_eaFreed++;
}

static u8* g_eaStubObj;
static DWORD g_eaStubOut[5];  // ecx, ebx, esi, edi, eax after the stub
static void EaRunTpStub() {
    __asm {
        push ebx
        push esi
        push edi
        sub esp, 0x100
        mov eax, g_eaStubObj
        mov [esp + 0x28], eax
        lea ecx, [esp + 0x28]
        call TpDefer
        mov dword ptr [esp + 0xAC], 0x5A5A5A5A
        mov ebx, 0x11111111
        mov esi, 0x22222222
        mov edi, 0x33333333
        mov eax, 0x44444444
        call TpAfterStub
        mov g_eaStubOut[0], ecx
        mov g_eaStubOut[4], ebx
        mov g_eaStubOut[8], esi
        mov g_eaStubOut[12], edi
        mov g_eaStubOut[16], eax
        add esp, 0x100
        pop edi
        pop esi
        pop ebx
    }
}

// MintDivGuard: a stub runs on a fake eval stack; the game's targets are landing pads that record where it went
static DWORD g_mdtSp, g_mdtKind, g_mdtEsi, g_mdtEdi, g_mdtSlot;
static DWORD g_mdtFrame[4];  // ebp = &g_mdtFrame[2], so [ebp-4] = g_mdtFrame[1] (the frame's saved ip)
static __declspec(naked) void MdtBack() {  // eax = 1 the game's handler, 2 the OverflowException stub, 3 the dispatch head
    __asm {
        mov g_mdtKind, eax
        mov g_mdtEsi, esi
        mov g_mdtEdi, edi
        mov eax, [ebp - 4]
        mov g_mdtSlot, eax
        mov esp, g_mdtSp
        pop ebp
        pop edi
        pop esi
        pop ebx
        ret
    }
}
static __declspec(naked) void MdtGame() { __asm { mov eax, 1 } __asm { jmp MdtBack } }
static __declspec(naked) void MdtOvf() { __asm { mov eax, 2 } __asm { jmp MdtBack } }
static __declspec(naked) void MdtNext() { __asm { mov eax, 3 } __asm { jmp MdtBack } }
static __declspec(naked) void __cdecl MdtRun(const void* /*stub*/, DWORD* /*esi*/, DWORD /*edi*/) {
    __asm {
        push ebx
        push esi
        push edi
        push ebp
        mov g_mdtSp, esp
        mov eax, [esp + 20]
        mov esi, [esp + 24]
        mov edi, [esp + 28]
        lea ebp, [g_mdtFrame + 8]
        jmp eax
    }
}
// dividend a, divisor b in 8-byte slots ev[0..1] / ev[2..3]; esi = &ev[4] (the handler's top of stack)
static DWORD g_mdtEv[6];
static int MdtCase(const void* stub, DWORD a, DWORD b) {
    memset(g_mdtEv, 0xCC, sizeof(g_mdtEv)), memset(g_mdtFrame, 0, sizeof(g_mdtFrame));
    g_mdtEv[0] = a, g_mdtEv[2] = b, g_mdtKind = 0;
    MdtRun(stub, g_mdtEv + 4, 0x1000);
    return (int)g_mdtKind;
}

// GeomNoMaterial: GnStub runs with ebx = &part.mesh and esi = a fake builder; the two targets are landing pads
static DWORD g_gntSp, g_gntKind, g_gntEbx, g_gntEsi, g_gntEdi, g_gntEbp;
static __declspec(naked) void GntBack() {  // eax = 1 the skip tail, 2 the next instruction
    __asm {
        mov g_gntKind, eax
        mov g_gntEbx, ebx
        mov g_gntEsi, esi
        mov g_gntEdi, edi
        mov g_gntEbp, ebp
        mov esp, g_gntSp
        pop ebp
        pop edi
        pop esi
        pop ebx
        ret
    }
}
static __declspec(naked) void GntSkip() { __asm { mov eax, 1 } __asm { jmp GntBack } }
static __declspec(naked) void GntCont() { __asm { mov eax, 2 } __asm { jmp GntBack } }
static __declspec(naked) void __cdecl GntRun(const void* /*stub*/, void* /*ebx*/, void* /*esi*/) {
    __asm {
        push ebx
        push esi
        push edi
        push ebp
        mov g_gntSp, esp
        mov eax, [esp + 20]
        mov ebx, [esp + 24]
        mov esi, [esp + 28]
        mov edi, 0x5A5A5A5A
        mov ebp, 0x6B6B6B6B
        jmp eax
    }
}
static int GntCase(void* slot, void* builder) {
    g_gntKind = 0;
    GntRun((const void*)&GnStub, slot, builder);
    assert(g_gntEbx == (DWORD)(uintptr_t)slot && g_gntEsi == (DWORD)(uintptr_t)builder && g_gntEdi == 0x5A5A5A5A &&
           g_gntEbp == 0x6B6B6B6B);
    return (int)g_gntKind;
}

static void TestEaCrashFixes() {
    static_assert(offsetof(Ts3nCounters, caOn) == 0x5480 && offsetof(Ts3nCounters, caSkip) == 0x5484 &&
                      offsetof(Ts3nCounters, caOther) == 0x5490 && offsetof(Ts3nCounters, caOverflow) == 0x549C &&
                      offsetof(Ts3nCounters, tpOn) == 0x54A0 && offsetof(Ts3nCounters, tpNoSlot) == 0x54AC &&
                      sizeof(Ts3nCounters) == 5468 * 4,
                  "counters v141 layout after v140 (docs/registry.md)");
    Ts3nCounters& k = ts3native_counters;
    assert(k.version == 145);

    // ---- ConformActor ----
    const DWORD rd0 = g_ecaRdLo, rd1 = g_ecaRdHi, tx0 = g_ecaTxLo, tx1 = g_ecaTxHi;
    g_ecaRdLo = (DWORD)(uintptr_t)g_eaVts, g_ecaRdHi = g_ecaRdLo + sizeof(g_eaVts);
    g_ecaTxLo = 0x10000, g_ecaTxHi = 0xFFFF0000;
    g_eaVts[0][4] = g_eaVts[1][4] = (DWORD)(uintptr_t)&EaFakeAsk;
    g_eaVts[2][4] = 0x10;
    const CaCtorFn ctor0 = g_ecaCtor;
    const CaDtorFn dtor0 = g_ecaDtor;
    g_ecaCtor = &EaFakeCtor, g_ecaDtor = &EaFakeDtor;
    if (!g_ecaSet) g_ecaSet = (DWORD*)VirtualAlloc(NULL, CA_CAP * 4, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    assert(g_ecaSet && !g_ecaLive);
    memset(k.caSkip, 0, sizeof(k.caSkip)), k.caOther = 0;

    alignas(8) static u8 comp[0x40], other[0x10], badSlot[0x10], ctl[0x48];
    static u8 owner[0x40];
    assert(CaCtorHook(comp, NULL, owner) == comp && CaHas((DWORD)(uintptr_t)comp) && k.caLive == 1);
    *(u8**)(ctl + 0x1C) = comp + 8, *(DWORD*)(ctl + 0x20) = 77;

    // live: the game's call, unchanged
    g_eaAsks = 0;
    assert(CaGuard(ctl) && g_eaAsks == 1 && g_eaAskActor == comp + 8);
    ctl[0x24] = 1;
    assert(!CaGuard(ctl) && g_eaAsks == 2);  // the actor's own "done" answer passes through
    ctl[0x24] = 0;
    // destroyed: answered "done", no call through it
    CaDtorHook(comp);
    assert(!CaHas((DWORD)(uintptr_t)comp) && k.caLive == 0);
    assert(!CaGuard(ctl) && g_eaAsks == 2 && k.caSkip[1] == 1);
    // the same address constructed again: live again
    CaCtorHook(comp, NULL, owner);
    assert(CaGuard(ctl) && g_eaAsks == 3);
    CaDtorHook(comp);
    // null actor, released page, clobbered vtable words (0x002B002B in #98, 0 in crash I)
    *(u8**)(ctl + 0x1C) = NULL;
    assert(!CaGuard(ctl) && k.caSkip[0] == 1);
    u8* page = (u8*)VirtualAlloc(NULL, 4096, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    VirtualFree(page, 0, MEM_RELEASE);
    *(u8**)(ctl + 0x1C) = page + 8;
    assert(!CaGuard(ctl) && k.caSkip[2] == 1);
    *(DWORD*)(comp + 8) = 0x002B002B, *(u8**)(ctl + 0x1C) = comp + 8;
    assert(!CaGuard(ctl) && k.caSkip[2] == 2);
    *(DWORD*)(comp + 8) = 0;
    assert(!CaGuard(ctl) && k.caSkip[2] == 3);
    // another actor type (vtable in .rdata, slot in .text): the game's call; slot outside .text: skipped
    *(DWORD*)other = (DWORD)(uintptr_t)g_eaVts[1], *(u8**)(ctl + 0x1C) = other;
    assert(CaGuard(ctl) && g_eaAsks == 4 && g_eaAskActor == other && k.caOther == 1);
    *(DWORD*)badSlot = (DWORD)(uintptr_t)g_eaVts[2], *(u8**)(ctl + 0x1C) = badSlot;
    assert(!CaGuard(ctl) && k.caSkip[2] == 4 && g_eaAsks == 4);

    // the patched ask sequence, run: mov eax,[esp+4]; <11 site bytes>; ret 4
    u8* code = (u8*)VirtualAlloc(NULL, 4096, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
    static const u8 pre[4] = {0x8B, 0x44, 0x24, 0x04}, post[3] = {0xC2, 0x04, 0x00};
    memcpy(code, pre, 4), memcpy(code + 4, kCaAsk, 11), memcpy(code + 15, post, 3);
    typedef bool(__stdcall * AskSeq)(u8* ctl);
    const AskSeq run = (AskSeq)(void*)code;
    CaCtorHook(comp, NULL, owner);
    *(u8**)(ctl + 0x1C) = comp + 8;
    assert(run(ctl) && g_eaAsks == 5);  // the game's bytes
    assert(CaPatchAsk(code + 4) && code[4] == 0x50 && code[5] == 0xE8 && code[10] == 0x0F);
    assert(run(ctl) && g_eaAsks == 6);  // patched, live
    CaDtorHook(comp);
    assert(!run(ctl) && g_eaAsks == 6 && k.caSkip[1] == 2);  // patched, destroyed
    assert(!CaPatchAsk(code + 4));                            // ours now: not patched twice
    VirtualFree(code, 0, MEM_RELEASE);

    // set churn: tombstones force rebuilds; membership stays exact
    for (DWORD i = 0; i < 200000; i++) CaAdd(0x10000000 + i * 8), CaDel(0x10000000 + i * 8);
    for (DWORD i = 0; i < 1000; i++) CaAdd(0x20000000 + i * 0x40);
    for (DWORD i = 0; i < 1000; i += 2) CaDel(0x20000000 + i * 0x40);
    for (DWORD i = 0; i < 1000; i++) assert(CaHas(0x20000000 + i * 0x40) == ((i & 1) == 1));
    assert(!CaHas(0x10000008) && k.caLive == 500 && k.caMaxLive >= 500 && !g_ecaFull && g_ecaUsed < CA_CAP / 4 * 3);
    memset(g_ecaSet, 0, CA_CAP * 4), g_ecaUsed = g_ecaLive = 0, k.caLive = k.caMaxLive = 0;
    memset(k.caSkip, 0, sizeof(k.caSkip)), k.caOther = 0;
    g_ecaCtor = ctor0, g_ecaDtor = dtor0, g_ecaRdLo = rd0, g_ecaRdHi = rd1, g_ecaTxLo = tx0, g_ecaTxHi = tx1;

    // ---- ThumbPose ----
    const TpReleaseFn rel0 = g_tpRelease;
    g_tpRelease = &EaFakeRelease;
    k.tpHeld = k.tpLastRef = k.tpNoSlot = 0;
    alignas(8) static u8 obj[0x20];
    u8* frame[0x40] = {};  // stands in for the render's stack: the RefPtr local sits at frame+0x28
    u8** local = (u8**)((u8*)frame + 0x28);
    const DWORD f = (DWORD)(uintptr_t)frame;
    // last reference: kept across "the evaluation", released after it
    *(LONG*)(obj + 4) = 1, *local = obj, g_eaReleases = g_eaFreed = 0;
    TpDefer(local);
    assert(g_eaReleases == 0 && *(LONG*)(obj + 4) == 1 && k.tpHeld == 1);
    TpAfter(f + 0x10);  // another frame: nothing
    assert(g_eaReleases == 0);
    TpAfter(f);
    assert(g_eaReleases == 1 && g_eaFreed == 1 && k.tpLastRef == 1);
    TpAfter(f);  // branch (b) / (c) next time: nothing kept, nothing released
    assert(g_eaReleases == 1);
    // shared owner: released after, not counted as last
    *(LONG*)(obj + 4) = 2;
    TpDefer(local), TpAfter(f);
    assert(g_eaReleases == 2 && *(LONG*)(obj + 4) == 1 && k.tpLastRef == 1 && k.tpHeld == 2);
    // empty RefPtr: nothing to keep
    *local = NULL;
    TpDefer(local);
    assert(k.tpHeld == 2 && g_eaReleases == 2);
    // every slot taken by other threads: released at once (the game's order)
    TpSlot saved[8];
    memcpy(saved, g_tp, sizeof(g_tp));
    for (int i = 0; i < 8; i++) g_tp[i].tid = 0x7FFF0000 + i, g_tp[i].frame = 0, g_tp[i].obj = obj;
    *(LONG*)(obj + 4) = 2, *local = obj;
    TpDefer(local);
    assert(k.tpNoSlot == 1 && g_eaReleases == 3 && *(LONG*)(obj + 4) == 1);
    memcpy(g_tp, saved, sizeof(g_tp));
    // the naked stub on a real stack: releases the frame's reference, keeps every register, runs the moved instruction
    *(LONG*)(obj + 4) = 1, g_eaStubObj = obj;
    EaRunTpStub();
    assert(g_eaReleases == 4 && g_eaFreed == 2 && k.tpLastRef == 2);
    assert(g_eaStubOut[0] == 0x5A5A5A5A && g_eaStubOut[1] == 0x11111111 && g_eaStubOut[2] == 0x22222222 &&
           g_eaStubOut[3] == 0x33333333 && g_eaStubOut[4] == 0x44444444);
    for (const TpSlot& s : g_tp) assert(!s.obj);
    // patch on dummies laid out like the two sites
    alignas(4) static u8 defer[8], after[8];
    defer[0] = 0xE8, *(LONG*)(defer + 1) = (LONG)(TP_RELEASE - ((uintptr_t)defer + 5));
    memcpy(after, kTpAfter, 7);
    u8 dOrig[8], aOrig[8];
    memcpy(dOrig, defer, 8), memcpy(aOrig, after, 8);
    after[3] = 0xA8;  // foreign bytes: nothing written anywhere
    assert(!TpPatch(defer, after) && !memcmp(defer, dOrig, 8));
    after[3] = 0xAC;
    assert(TpPatch(defer, after));
    assert(defer[0] == 0xE8 && (uintptr_t)defer + 5 + *(LONG*)(defer + 1) == (uintptr_t)&TpDefer);
    assert(after[0] == 0xE8 && (uintptr_t)after + 5 + *(LONG*)(after + 1) == (uintptr_t)&TpAfterStub && after[5] == 0x66 && after[6] == 0x90);
    assert(!TpPatch(defer, after));  // ours now
    g_tpRelease = rel0;
    k.tpHeld = k.tpLastRef = k.tpNoSlot = 0;

    // ---- GeomBoneIndex ----
    static DWORD bones[3] = {0x816E191A, 0x11111111, 0x22222222};
    alignas(4) static u8 geom[0x60];
    *(DWORD**)(geom + 0x58) = bones, *(DWORD**)(geom + 0x5C) = bones + 3;
    // the logged key: type:group:instance from the resource base (instance at +8, type +0x10, group +0x14)
    *(DWORD*)(geom + 8) = 0x8DAFEEA9, *(DWORD*)(geom + 0xC) = 0xC014AA21, *(DWORD*)(geom + 0x10) = 0x015A1849, *(DWORD*)(geom + 0x14) = 0x009B6C36;
    char gkey[40];
    GeomKey(gkey, geom);
    assert(!strcmp(gkey, "015A1849:009B6C36:C014AA218DAFEEA9"));
    g_gbBad = 0;
    assert(GbGetBone(geom, NULL, 0) == 0x816E191A && GbGetBone(geom, NULL, 2) == 0x22222222 && g_gbBad == 0);
    assert(GbGetBone(geom, NULL, 3) == 0 && GbGetBone(geom, NULL, 0x6D) == 0 && g_gbBad == 2);  // 0x6D: crash X's index
    *(DWORD**)(geom + 0x5C) = bones;  // empty list
    assert(GbGetBone(geom, NULL, 0) == 0 && g_gbBad == 3);
    *(DWORD**)(geom + 0x5C) = bones + 3;
    // the game's two getters, run on the fake, and the patch on a stand-in vtable slot
    u8* gcode = (u8*)VirtualAlloc(NULL, 4096, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
    memcpy(gcode, kGbGet, sizeof(kGbGet)), memcpy(gcode + 16, kGbCount, sizeof(kGbCount));
    typedef DWORD(__fastcall * GbGetFn)(u8* geom, void*, DWORD i);
    typedef int(__fastcall * GbCountFn)(u8* geom);
    assert(((GbGetFn)(void*)gcode)(geom, NULL, 1) == 0x11111111 && ((GbCountFn)(void*)(gcode + 16))(geom) == 3);
    static DWORD gslot;
    gslot = (DWORD)(uintptr_t)gcode;
    gcode[16 + 2] = 0x60;  // a count getter of another layout: nothing written
    assert(!GbPatch(&gslot, gcode + 16) && gslot == (DWORD)(uintptr_t)gcode);
    gcode[16 + 2] = 0x5C;
    assert(GbPatch(&gslot, gcode + 16) && gslot == (DWORD)(uintptr_t)&GbGetBone);
    assert(!GbPatch(&gslot, gcode + 16));  // ours now
    const GbGetFn patched = (GbGetFn)(uintptr_t)gslot;
    assert(patched(geom, NULL, 1) == 0x11111111 && patched(geom, NULL, 4) == 0 && g_gbBad == 4);
    VirtualFree(gcode, 0, MEM_RELEASE);
    g_gbBad = 0;

    // ---- GeomNoMaterial ----
    const DWORD gn0[3] = {g_gnSkip, g_gnCont, g_gnGet};
    u8* ncode = (u8*)VirtualAlloc(NULL, 4096, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
    memcpy(ncode, kGnGet, sizeof(kGnGet));  // the game's getter, at a stand-in address
    g_gnGet = (DWORD)(uintptr_t)ncode;
    typedef void*(__fastcall * GnGetFn)(u8* geom);
    static DWORD gnVt[0x20], otherVt[0x20];  // a GEOM vtable (slot +0x58 = the getter) and another type's
    gnVt[0x58 / 4] = g_gnGet, otherVt[0x58 / 4] = 0x12345678;
    alignas(4) static u8 gmesh[0x60], builder[0x1A4];
    static DWORD mat[4] = {0, 0x2B, 0, 0};
    *(DWORD*)(builder + 0x1A0) = 2;
    *(DWORD**)gmesh = gnVt;
    *(DWORD**)(gmesh + 0x28) = mat;
    assert(((GnGetFn)(void*)ncode)(gmesh) == mat);  // the game's getter on the fake: the material
    void* part = gmesh;
    g_gnSkip = (DWORD)(uintptr_t)&GntSkip, g_gnCont = (DWORD)(uintptr_t)&GntCont, g_gnBad = 0;
    assert(GntCase(&part, builder) == 2 && g_gnBad == 0);  // a GEOM with its material: the game's path
    *(DWORD**)(gmesh + 0x28) = NULL;
    assert(GntCase(&part, builder) == 1 && g_gnBad == 1);  // no material: skipped (issue #80)
    *(DWORD**)gmesh = otherVt;
    assert(GntCase(&part, builder) == 2 && g_gnBad == 1);  // another type with the same field 0: the game's path
    part = NULL;
    assert(GntCase(&part, builder) == 1 && g_gnBad == 1);  // no mesh: the game's own skip, not counted
    // patch on a stand-in site; foreign bytes or a foreign slot write nothing
    u8* site = ncode + 64;
    memcpy(site, kGnSite, sizeof(kGnSite));
    static DWORD nslot;
    nslot = g_gnGet + 1;
    assert(!GnPatch(site, &nslot) && !memcmp(site, kGnSite, sizeof(kGnSite)));
    nslot = g_gnGet, site[7] = 0x06;
    assert(!GnPatch(site, &nslot) && site[0] == 0x8B);
    site[7] = 0x05;
    assert(GnPatch(site, &nslot) && site[0] == 0xE9 && (uintptr_t)site + 5 + *(LONG*)(site + 1) == (uintptr_t)&GnStub && site[9] == 0x90);
    assert(!GnPatch(site, &nslot));  // ours now
    VirtualFree(ncode, 0, MEM_RELEASE);
    g_gnSkip = gn0[0], g_gnCont = gn0[1], g_gnGet = gn0[2], g_gnBad = 0;

    // ---- MintDivGuard ----
    static_assert(offsetof(Ts3nCounters, mdOn) == 0x5520 && offsetof(Ts3nCounters, mdDivOverflow) == 0x5524 &&
                      offsetof(Ts3nCounters, mdRemMinus1) == 0x5528 && offsetof(Ts3nCounters, mdSpare) == 0x552C,
                  "counters v144 layout after v143 (docs/registry.md)");
    const DWORD md0[4] = {g_mdDiv, g_mdRem, g_mdOvf, g_mdNext};
    // patch on a stand-in table whose two handlers start with EA's bytes; foreign bytes write nothing
    u8* hcode = (u8*)VirtualAlloc(NULL, 4096, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
    memcpy(hcode, kMdHead, sizeof(kMdHead)), memcpy(hcode + 16, kMdHead, sizeof(kMdHead));
    static DWORD tbl[0x1C1];
    g_mdDiv = (DWORD)(uintptr_t)hcode, g_mdRem = (DWORD)(uintptr_t)(hcode + 16);
    tbl[0x127] = g_mdDiv, tbl[0x143] = g_mdRem;
    hcode[16 + 3] = 1;  // rem handler of another build
    assert(!MdPatch(tbl) && tbl[0x127] == g_mdDiv && tbl[0x143] == g_mdRem);
    hcode[16 + 3] = 0;
    assert(MdPatch(tbl) && tbl[0x127] == (DWORD)(uintptr_t)&MdDivStub && tbl[0x143] == (DWORD)(uintptr_t)&MdRemStub);
    assert(!MdPatch(tbl));  // ours now
    VirtualFree(hcode, 0, MEM_RELEASE);
    // the stubs, run: every case but INT_MIN by -1 lands on the game's handler with the registers as they came
    g_mdDiv = g_mdRem = (DWORD)(uintptr_t)&MdtGame, g_mdOvf = (DWORD)(uintptr_t)&MdtOvf, g_mdNext = (DWORD)(uintptr_t)&MdtNext;
    k.mdDivOverflow = k.mdRemMinus1 = 0, g_mdLogged = 0;
    const DWORD kMin = 0x80000000, kM1 = 0xFFFFFFFF;
    const DWORD normal[][2] = {{7, 2}, {(DWORD)-7, 2}, {kMin, 1}, {5, kM1}, {5, 0}, {kMin, 2}, {0x7FFFFFFF, kM1}, {kMin, 0}, {0, kM1}};
    for (const auto& c : normal) {
        for (int rem = 0; rem < 2; rem++) {
            assert(MdtCase(rem ? (void*)&MdRemStub : (void*)&MdDivStub, c[0], c[1]) == 1);
            assert(g_mdtEsi == (DWORD)(uintptr_t)(g_mdtEv + 4) && g_mdtEdi == 0x1000 && g_mdtSlot == 0);
            assert(g_mdtEv[0] == c[0] && g_mdtEv[2] == c[1]);
        }
    }
    assert(k.mdDivOverflow == 0 && k.mdRemMinus1 == 0);
    // div INT_MIN / -1: the OverflowException stub, nothing popped, ip still this instruction
    assert(MdtCase((void*)&MdDivStub, kMin, kM1) == 2);
    assert(g_mdtEsi == (DWORD)(uintptr_t)(g_mdtEv + 4) && g_mdtEdi == 0x1000 && g_mdtSlot == 0 && g_mdtEv[0] == kMin);
    assert(k.mdDivOverflow == 1 && k.mdRemMinus1 == 0);
    // rem INT_MIN % -1: 0 over the dividend, divisor popped, next instruction saved and dispatched (as the game's handler)
    assert(MdtCase((void*)&MdRemStub, kMin, kM1) == 3);
    assert(g_mdtEsi == (DWORD)(uintptr_t)(g_mdtEv + 2) && g_mdtEdi == 0x1002 && g_mdtSlot == 0x1002 && g_mdtEv[0] == 0);
    assert(k.mdDivOverflow == 1 && k.mdRemMinus1 == 1 && g_mdLogged == 2);
    g_mdDiv = md0[0], g_mdRem = md0[1], g_mdOvf = md0[2], g_mdNext = md0[3];
    k.mdDivOverflow = k.mdRemMinus1 = 0, g_mdLogged = 0;
    printf("EaCrashFixes: ConformActor live call unchanged, destroyed / null / freed / clobbered actors skipped, other types "
           "called, patched sequence run; ThumbPose reference kept across the evaluation and released by the stub; "
           "GeomBoneIndex in-range bones unchanged, past-the-list indices answered 0, key printed, vtable slot patched once; "
           "GeomNoMaterial meshes with a material unchanged, no material skipped, other types and null meshes unchanged, "
           "site patched once; "
           "MintDivGuard 18 normal div/rem cases on the game's path, INT_MIN / -1 to the overflow stub, INT_MIN %% -1 = 0, "
           "table patched once: ok\n");
}
