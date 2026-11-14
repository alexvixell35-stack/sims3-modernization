// TestPkgProbe ([PkgProbe], docs/549 §4): our Resolve copy against a fake manager (slot 17 at vtable +0x44 answers from a key
// list), fakes for the fallback key 0x7D7580 and the compare 0x54DD80; the RemoveFix re-find and its stub on a fake list.

static DWORD g_pptKnown[4][4];  // keys the fake manager finds
static int g_pptKnownN, g_pptLookups;
static void* __fastcall PpFakeLookup(void*, void*, const DWORD* key) {
    g_pptLookups++;
    for (int i = 0; i < g_pptKnownN; i++)
        if (!memcmp(g_pptKnown[i], key, 16)) return (void*)1;
    return NULL;
}
static void __cdecl PpFakeFallback(DWORD* out, const DWORD* key) {  // 0x7D7580's always-taken part: group bit flipped
    PpCopyKey(out, key);
    out[3] ^= 0x08000000;
}
static u8 __cdecl PpFakeDiffer(const DWORD* a, const DWORD* b) { return memcmp(a, b, 16) != 0; }

// the RemoveFix stub, run as EA's remove path reaches 0x4B2E78 (edi = M, ebp = database, esi = stale pair)
static u8* g_pptEsi;
static DWORD g_pptEax, g_pptEcx;
static int g_pptPath;
static __declspec(naked) void PpTBack() {
    __asm {
        mov g_pptEsi, esi
        mov g_pptEax, eax
        mov g_pptEcx, ecx
        mov g_pptPath, 1
        ret
    }
}
static __declspec(naked) void PpTSkip() {
    __asm {
        mov g_pptPath, 2
        ret
    }
}
#pragma warning(push)
#pragma warning(disable : 4100)  // naked: the arguments are read from the stack
static __declspec(naked) void __cdecl PpRunStub(u8* m, u8* db, u8* stale) {
    __asm {
        push ebx
        push esi
        push edi
        push ebp
        mov edi, [esp + 20]  // m (4 saved registers + the return address)
        mov ebp, [esp + 24]  // db
        mov esi, [esp + 28]  // stale
        call PpRemoveFixStub
        pop ebp
        pop edi
        pop esi
        pop ebx
        ret
    }
}
#pragma warning(pop)

static void TestPkgProbe() {
    static_assert(offsetof(Ts3nCounters, ppMode) == 0x6314 && offsetof(Ts3nCounters, ppNoMgr) == 0x634C && sizeof(Ts3nCounters) == 6357 * 4,
                  "counters v919 layout after v198 (docs/registry.md)");
    Ts3nCounters& k = ts3native_counters;
    u8* const* const sMgrAt = g_ppMgrAt;
    const PpFallbackFn sFb = g_ppFallback;
    const PpDifferFn sDiff = g_ppDiffer;
    const DWORD sBack = g_ppRfBack, sSkip = g_ppRfSkip;
    const LONG sNext = g_ppNextLine;

    static void* vt[32];
    vt[0x44 / 4] = (void*)PpFakeLookup;
    static u8 mgr[0x80];
    *(void***)mgr = vt;
    static u8* mgrGlobal;
    g_ppMgrAt = &mgrGlobal, g_ppFallback = PpFakeFallback, g_ppDiffer = PpFakeDiffer;
    g_ppNextLine = (LONG)(GetTickCount() + 3600000);  // no summary line during the test
    memset((void*)&k.ppResolve, 0, offsetof(Ts3nCounters, ppSpare) - offsetof(Ts3nCounters, ppResolve));

    const DWORD tagged[4] = {1, 2, 3, 0x10000005}, plain[4] = {7, 0, 0x1234, 5}, other[4] = {8, 0, 0x1234, 5};
    DWORD out[4];
    // (a) no manager yet: the key itself
    mgrGlobal = NULL;
    assert(PpResolveHook(out, plain) == out && !memcmp(out, plain, 16) && k.ppNoMgr == 1 && g_pptLookups == 0);
    mgrGlobal = mgr;
    // (b) tagged key: copied, no lookup
    assert(PpResolveHook(out, tagged) == out && !memcmp(out, tagged, 16) && k.ppTagged == 1 && g_pptLookups == 0);
    // (c) first lookup finds it: the key, one lookup
    memcpy(g_pptKnown[0], plain, 16), g_pptKnownN = 1;
    assert(PpResolveHook(out, plain) == out && !memcmp(out, plain, 16) && g_pptLookups == 1 && k.ppMiss == 0);
    // (d) miss, fallback finds it: the fallback key
    DWORD fb[4];
    PpFakeFallback(fb, other);
    memcpy(g_pptKnown[0], fb, 16);
    assert(PpResolveHook(out, other) == out && !memcmp(out, fb, 16) && g_pptLookups == 3 && k.ppMiss == 1 && k.ppRetry == 1 && k.ppRetryHit == 1);
    // (e) both miss: the key; out may be the key itself
    g_pptKnownN = 0;
    DWORD inPlace[4];
    memcpy(inPlace, other, 16);
    assert(PpResolveHook(inPlace, inPlace) == inPlace && !memcmp(inPlace, other, 16) && k.ppMiss == 2 && k.ppRetry == 2 && k.ppRetryHit == 1);
    assert(k.ppResolve == 5 && g_ppInResolve == 0 && PpMounted(mgr) == -1);  // a fake vtable is no manager we know

    // (f) RemoveFix re-find: list {A,1} {B,2} {C,3}; remove B
    u8 dbA[4], dbB[4], dbC[4];
    u8* list[6] = {dbA, (u8*)1, dbB, (u8*)2, dbC, (u8*)3};
    *(u8***)(mgr + 0x30) = list, *(u8***)(mgr + 0x34) = list + 6;
    u8* const staleB = (u8*)(list + 2);
    assert(PpRefind(mgr, dbB, staleB) == staleB && k.ppRfMoved == 0 && k.ppRfGone == 0);  // unchanged list: EA's own slot
    u8* moved[8] = {dbC, (u8*)3, dbA, (u8*)1, dbC, (u8*)3, dbB, (u8*)2};  // the list changed (and moved) while unlocked
    *(u8***)(mgr + 0x30) = moved, *(u8***)(mgr + 0x34) = moved + 8;
    assert(PpRefind(mgr, dbB, staleB) == (u8*)(moved + 6) && k.ppRfMoved == 1);
    assert(PpRefind(mgr, dbC, (u8*)moved) == (u8*)moved && k.ppRfMoved == 1);  // first match, as EA's search
    *(u8***)(mgr + 0x34) = moved + 6;  // B gone
    assert(PpRefind(mgr, dbB, staleB) == NULL && k.ppRfGone == 1);

    // (g) the stub: same pair -> EA's erase with esi / eax / ecx as EA sets them; moved -> new esi; gone -> EA's unlock path
    g_ppRfBack = (DWORD)(uintptr_t)&PpTBack, g_ppRfSkip = (DWORD)(uintptr_t)&PpTSkip;
    *(u8***)(mgr + 0x30) = list, *(u8***)(mgr + 0x34) = list + 6;
    g_pptPath = 0;
    PpRunStub(mgr, dbB, staleB);
    assert(g_pptPath == 1 && g_pptEsi == staleB && g_pptEax == (DWORD)(uintptr_t)(staleB + 8) && g_pptEcx == (DWORD)(uintptr_t)(list + 6));
    *(u8***)(mgr + 0x30) = moved, *(u8***)(mgr + 0x34) = moved + 8;
    PpRunStub(mgr, dbB, staleB);
    assert(g_pptPath == 1 && g_pptEsi == (u8*)(moved + 6) && g_pptEcx == (DWORD)(uintptr_t)(moved + 8));
    *(u8***)(mgr + 0x34) = moved + 6;
    PpRunStub(mgr, dbB, staleB);
    assert(g_pptPath == 2 && k.ppRfGone == 2);

    g_ppMgrAt = sMgrAt, g_ppFallback = sFb, g_ppDiffer = sDiff, g_ppRfBack = sBack, g_ppRfSkip = sSkip, g_ppNextLine = sNext;
    memset((void*)&k.ppResolve, 0, offsetof(Ts3nCounters, ppSpare) - offsetof(Ts3nCounters, ppResolve));
    printf("selftest pkgprobe ok (Resolve copy: no manager, tagged, hit, fallback hit, both miss in place; RemoveFix re-find and stub)\n");
}
