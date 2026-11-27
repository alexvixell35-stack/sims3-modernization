// TestExitHooks ([Fixes] ExitStaleHooks / ExitHookDiag, exithooks.cpp, docs/67 §47). Included by selftest.cpp.
// A fake TriggerService (map at +0x10, lock at +0x380) with three hooks: a live window (vtable and slot +0x10C in this image),
// a freed window (vtable = a heap value, as in the player's dump) and an inactive hook. A fake EA remove records the flags it saw.
static void XhtSlot() {}
static const DWORD* g_xhtVt;  // a vtable inside this image
static int g_xhtRemoves;
static u8 g_xhtFlagsSeen;
static bool __fastcall XhtRemove(u8* svc, void*, DWORD h) {
    g_xhtRemoves++;
    u8* n = XhFind(svc, h);
    g_xhtFlagsSeen = n ? n[0x1C] : 0xEE;
    return true;
}
static DWORD __cdecl XhtAdd(DWORD, const u8*, DWORD, DWORD, DWORD) { return 0x2A; }
static bool g_xhtShutRan;
static void __fastcall XhtShut(u8*, void*) { g_xhtShutRan = true; }

static void TestExitHooks() {
    static DWORD vt[0x50];
    vt[0x10C / 4] = (DWORD)(uintptr_t)&XhtSlot;
    g_xhtVt = vt;
    u8* const svc = (u8*)calloc(1, 0x400);
    InitializeCriticalSection((CRITICAL_SECTION*)(svc + 0x380));
    u8* nodes = (u8*)calloc(3, 0x28);
    u8** const b = (u8**)calloc(8, sizeof(u8*));
    const DWORD nb = 7;
    b[nb] = (u8*)(uintptr_t)0xFFFFFFFF;
    *(u8***)(svc + 0x14) = b, *(DWORD*)(svc + 0x18) = nb, *(DWORD*)(svc + 0x1C) = 3;
    DWORD liveWin[2] = {(DWORD)(uintptr_t)vt, 0};
    DWORD* const deadWin = (DWORD*)calloc(4, 4);
    deadWin[0] = (DWORD)(uintptr_t)calloc(0x200, 1);  // a heap "vtable": in no module
    const DWORD hs[3] = {9, 16, 23};  // 9 % 7 = 2, 16 % 7 = 2 (same chain), 23 % 7 = 2
    const void* wins[3] = {liveWin, deadWin, deadWin};
    const u8 fl[3] = {0x10, 0x10, 0x00};
    for (int i = 0; i < 3; i++) {
        u8* n = nodes + i * 0x28;
        *(DWORD*)n = hs[i], *(const void**)(n + 0x18) = wins[i], n[0x1C] = fl[i];
        *(u8**)(n + 0x24) = b[hs[i] % nb], b[hs[i] % nb] = n;
    }
    assert(XhFind(svc, 9) == nodes && XhFind(svc, 16) == nodes + 0x28 && XhFind(svc, 23) == nodes + 0x50 && !XhFind(svc, 30));
    DWORD v = 0;
    assert(!XhWindowBad((const u8*)liveWin, &v) && v == (DWORD)(uintptr_t)vt);
    assert(XhWindowBad((const u8*)deadWin, &v) && XhWindowBad(NULL, &v));

    const XhRemoveFn r0 = g_xhRemove;
    g_xhRemove = &XhtRemove, g_xhDiag = true;
    // fix off: EA sees the active bit on the freed window's hook (detector line only)
    g_xhFix = false;
    const LONG st0 = g_xhStale;
    XhRemove(svc, NULL, 16);
    assert(g_xhtRemoves == 1 && g_xhtFlagsSeen == 0x10 && g_xhStale == st0 + 1);
    // fix on: the active bit is cleared before EA's remove, so EA makes no window call
    g_xhFix = true;
    const LONG sk0 = g_xhSkipped;
    XhRemove(svc, NULL, 16);
    assert(g_xhtRemoves == 2 && g_xhtFlagsSeen == 0x00 && g_xhSkipped == sk0 + 1 && g_xhStale == st0 + 2);
    // a live window and an inactive hook pass untouched; an unknown handle still reaches EA
    XhRemove(svc, NULL, 9);
    assert(g_xhtFlagsSeen == 0x10 && g_xhSkipped == sk0 + 1);
    XhRemove(svc, NULL, 23);
    assert(g_xhtFlagsSeen == 0x00 && g_xhSkipped == sk0 + 1 && g_xhStale == st0 + 2);
    XhRemove(svc, NULL, 30);
    assert(g_xhtRemoves == 5 && g_xhtFlagsSeen == 0xEE);

    // names: the add IC's MonoString {vtable, length +4, UTF-16 +8} recorded by handle, dropped on remove
    const XhAddFn a0 = g_xhAdd;
    g_xhAdd = &XhtAdd;
    u8 ms[8 + 2 * 9] = {};
    *(int*)(ms + 4) = 9;
    const wchar_t* t = L"userTools";
    memcpy(ms + 8, t, 18);
    assert(XhAdd(0x30000001, ms, 0, 1, 0) == 0x2A);
    char nm[40];
    XhNameGet(0x2A, nm, sizeof(nm));
    assert(!strcmp(nm, "userTools"));
    XhNameDrop(0x2A);
    XhNameGet(0x2A, nm, sizeof(nm));
    assert(!strcmp(nm, "?"));
    XhMonoAscii(NULL, nm, sizeof(nm));
    assert(!nm[0]);

    // teardown list runs, then EA's teardown
    const XhShutFn s0 = g_xhShut;
    g_xhShut = &XhtShut;
    const LONG d0 = g_xhDumps;
    XhShutdown(svc, NULL);
    assert(g_xhtShutRan && g_xhDumps == d0 + 1);

    // a call site whose bytes differ is not retargeted
    u8 site[16] = {};
    assert(!XhRetarget(site + 8, kXhCallIcPre, sizeof(kXhCallIcPre)));
    g_xhRemove = r0, g_xhAdd = a0, g_xhShut = s0, g_xhFix = g_xhDiag = false;
    printf("exithooks: freed window found by vtable module, fix clears the active bit before EA's remove (off: passed), live and inactive "
           "hooks untouched, map names recorded and dropped, teardown list then EA, wrong site bytes refused\n");
}
