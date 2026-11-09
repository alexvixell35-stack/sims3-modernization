// TestFloat5Watch ([Float5Sticky], [Float5Watch], [UafCheck] TravelHold; f5watch.cpp, docs/539 §21). Included by selftest.cpp
// after uafcheck-test.h (UafTChunk, UafTFree, g_uafTHeap). Real DR hits through WpVeh (the sticky watch, a rotation watch), a
// fake outfit / SlotComponent / Portal, the sweep, the window texts, a thread capture and the travel hold.
static volatile DWORD g_f5tVar, g_f5tVar2;
static int g_f5tPortalCalls;
static DWORD __fastcall F5tPortalSave(u8*, void*, void*) { return g_f5tPortalCalls++, 1; }
static DWORD WINAPI F5tSleeper(LPVOID ev) { WaitForSingleObject((HANDLE)ev, 10000); return 0; }

static void TestFloat5Watch() {
    wchar_t lp[MAX_PATH];
    GetTempPathW(MAX_PATH, lp);
    wcscat_s(lp, L"ts3native-f5watch-selftest.log");  // the new lines, for the queue regex cases (docs/539 §21)
    const HANDLE savedLog = g_log;
    g_log = CreateFileW(lp, GENERIC_WRITE, FILE_SHARE_READ, NULL, CREATE_ALWAYS, 0, NULL);
    WpInstall();
    // --- [Float5Sticky]: the first address gets a keep watch; writes of 5.0 and of other values are logged; it never moves
    g_f5sOn = true, g_f5sAddr = 0, g_f5sSlot = -1;
    const DWORD a = (DWORD)(uintptr_t)&g_f5tVar, a2 = (DWORD)(uintptr_t)&g_f5tVar2;
    F5sNote(a, "selftest first");
    F5sNote(a2, "selftest second");  // logged, ignored
    assert((DWORD)g_f5sAddr == a);
    F5sStep();
    const LONG k = g_f5sSlot;
    assert(k >= 0 && g_wp[k].addr == a && g_wp[k].fn == F5sHit && g_wp[k].keep);
    const LONG w0 = g_f5sWrites, f0 = g_f5sF5;
    g_f5tVar = kF5wF5;
    g_f5tVar = 7;
    assert(g_f5sWrites == w0 + 2 && g_f5sF5 == f0 + 1);
    F5sCheck("selftest save start", 1);
    // a plain arm cannot take the keep slot: fill the other three, the next plain arm gets none
    static volatile DWORD fill[3];
    int got[3];
    for (int i = 0; i < 3; i++) got[i] = WpArm((const void*)&fill[i], 4, true);
    assert(got[0] >= 0 && got[1] >= 0 && got[2] >= 0 && WpArm((const void*)&g_f5tVar2, 4, true) == -1 && g_wp[k].addr == a);
    for (int i = 0; i < 3; i++) WpDisarm(got[i]);
    F5sStep();  // live: refresh only
    assert(g_f5sSlot == k);

    // --- [Float5Sticky] PortalCheck: the vector checks, guard 0 passes to EA, guard 1 answers al = 0
    DWORD pv[3] = {0, 0, 0};
    assert(!F5pWhat(pv));
    pv[0] = kF5wF5, pv[1] = 0x40A00038;
    assert(F5pWhat(pv) && strstr(F5pWhat(pv), "float 5.0"));
    static DWORD elems[0x38 / 4 * 2];
    pv[0] = (DWORD)(uintptr_t)elems, pv[1] = pv[0] + 0x38, pv[2] = pv[0] + 0x70;
    assert(!F5pWhat(pv));
    pv[1] = pv[0] + 0x30;
    assert(F5pWhat(pv));  // not a multiple of 0x38
    pv[0] = 0x1000, pv[1] = 0x1038;
    assert(F5pWhat(pv) && !strcmp(F5pWhat(pv), "not a heap pointer"));
    static DWORD portal[0x70 / 4];
    portal[0] = kF5wPortalVt;
    g_f5pSave = F5tPortalSave, g_f5sPortalGuard = false;
    assert(F5pSaveHook((u8*)portal, NULL, NULL) == 1 && g_f5tPortalCalls == 1);  // empty vector: EA saves it
    portal[0x5C / 4] = kF5wF5, portal[0x60 / 4] = kF5wF5 + 0x38;
    const LONG pb0 = g_f5pBad;
    assert(F5pSaveHook((u8*)portal, NULL, NULL) == 1 && g_f5tPortalCalls == 2 && g_f5pBad == pb0 + 1);
    g_f5sPortalGuard = true;
    assert(F5pSaveHook((u8*)portal, NULL, NULL) == 0 && g_f5tPortalCalls == 2 && g_f5pSkip >= 1);
    g_f5sPortalGuard = false, g_f5pSave = (F5pSaveFn)(uintptr_t)0x0064E9A0;
    assert((DWORD)g_f5sAddr == a);  // the portal's float-5 address was only logged: the watch stays

    // --- [Float5Watch]: table, outfit / slot registration, window texts, sweep, gone
    g_f5wOn = true;
    if (!g_f5w) g_f5w = (F5wEnt*)VirtualAlloc(NULL, kF5wN * sizeof(F5wEnt), MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
    assert(g_f5w);
    u8* mem = (u8*)VirtualAlloc(NULL, 0x5000, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
    assert(mem);
    DWORD* o = (DWORD*)(mem + 0x100);
    o[0] = 0x00FE4A10;
    DWORD* cells = (DWORD*)(mem + 0x1000);  // 16-aligned: entries 3, 7, ... are 0xC mod 16
    o[0xBC / 4] = (DWORD)(uintptr_t)cells, o[0xC0 / 4] = (DWORD)(uintptr_t)(cells + 8);
    u8* rec = mem + 0x2000;
    o[0x98 / 4] = (DWORD)(uintptr_t)rec, o[0x9C / 4] = (DWORD)(uintptr_t)(rec + 0x68);
    DWORD* arr = (DWORD*)(mem + 0x3000);
    *(DWORD*)(rec + 0x14) = (DWORD)(uintptr_t)arr, *(DWORD*)(rec + 0x18) = (DWORD)(uintptr_t)(arr + 5);
    u8* so = mem + 0x3100;
    *(DWORD*)(rec + 0x5C) = (DWORD)(uintptr_t)so;
    DWORD* comp = (DWORD*)(mem + 0x4008);
    comp[0] = kF5wSlotVt;
    assert(F5wOutfitCell((DWORD)(uintptr_t)cells, 8) == 3 && F5wOutfitCell((DWORD)(uintptr_t)cells, 121) == 87 && F5wOutfitCell(0x1004, 2) == -1);
    g_f5wDr = true, g_f5wDrMax = 2, g_f5wCandN = 0;
    F5wEvent("save start", 1);
    F5wOutfit((const u8*)o, true, "selftest inventory", true);
    const DWORD oi = F5wIdx((DWORD)(uintptr_t)o);
    assert(g_f5w[oi].obj == (DWORD)(uintptr_t)o && g_f5w[oi].kind == F5K_OUTFIT && g_f5wCandN == 1 && g_f5wCand[0].slot == (DWORD)(uintptr_t)(cells + 3));
    assert(F5wOk((DWORD)(uintptr_t)comp, F5K_SLOT, "selftest load end", true) && g_f5wCandN == 2 && g_f5wCand[1].slot == (DWORD)(uintptr_t)comp + 0x14);
    assert(!F5wOk((DWORD)(uintptr_t)comp, F5K_SLOT, "selftest load end", true));  // known: no second candidate
    char t[600];
    F5wOkText(g_f5w[oi], (DWORD)(uintptr_t)o, GetTickCount(), t, sizeof(t));
    assert(strstr(t, "last OK ") && strstr(t, " ms ago at selftest inventory"));
    F5wOkText(g_f5w[oi], 0x1234, GetTickCount(), t, sizeof(t));
    assert(strstr(t, "last OK never"));
    F5wEvText(0, true, GetTickCount(), t, sizeof(t));
    assert(strstr(t, "save start #1"));
    g_f5wCur = 0;
    F5wSweep(GetTickCount());
    assert(g_f5w[oi].okSite && !strcmp(g_f5w[oi].okSite, "sweep"));
    if (F5fBad(kF5wF5)) {  // 0x40A00000 not a real block in this process (it never is here unless something maps it)
        const LONG fd0 = g_f5wFinds;
        cells[3] = kF5wF5;
        F5wSweep(GetTickCount());
        assert(g_f5wFinds == fd0 + 1 && g_f5w[oi].badAt == (DWORD)(uintptr_t)(cells + 3));
        F5wSweep(GetTickCount());
        assert(g_f5wFinds == fd0 + 1);  // reported once
        cells[3] = 0, arr[2] = kF5wF5;
        F5wSweep(GetTickCount());
        assert(g_f5wFinds == fd0 + 2);  // record array slot: another slot of the same object
        arr[2] = 0, *(DWORD*)(so + 0x2C) = kF5wF5;
        F5wSweep(GetTickCount());
        assert(g_f5wFinds == fd0 + 3);  // string object begin
        *(DWORD*)(so + 0x2C) = 0, comp[0x14 / 4] = kF5wF5;
        F5wSweep(GetTickCount());
        assert(g_f5wFinds == fd0 + 4);  // slot component +0x14
        comp[0x14 / 4] = 0;
    }
    // --- DR rotation: a travel save start opens the window; the newest candidate of each kind is watched
    g_f5wRotAt = 0;
    F5wEvent("travel save start", 2);
    F5wRotate(g_f5wWinAt);  // the window's own tick (now | 1): GetTickCount() on an even tick reads as before the window
    int mine = 0, cellSlot = -1;
    for (int s = 0; s < 4; s++)
        if (g_wp[s].fn == F5wDrHit) mine++, cellSlot = g_wp[s].addr == (DWORD)(uintptr_t)(cells + 3) ? s : cellSlot;
    assert(mine == 2 && cellSlot >= 0 && g_wp[k].fn == F5sHit);  // the sticky watch kept its slot
    const LONG dh0 = g_f5wDrHits, df0 = g_f5wDrF5;
    cells[3] = kF5wF5;
    assert(g_f5wDrHits == dh0 + 1 && g_f5wDrF5 == df0 + 1 && g_f5wArm[cellSlot].writes == 1);
    cells[3] = 0;
    F5wRotate(GetTickCount());  // same set within RotateSec: nothing changes
    // a plain arm (UafCheck) may take a rotation slot, never the sticky one
    const int pl = WpArm((const void*)&fill[0], 4, true);
    assert(pl >= 0 && pl != k);
    WpDisarm(pl);
    for (int s = 0; s < 4; s++) if (g_wp[s].fn == F5wDrHit) WpDisarm(s);
    // vtable change = gone; F5wGone
    o[0] = 0x1234;
    F5wSweep(GetTickCount());
    assert(g_f5w[oi].obj == 0);
    F5wGone((DWORD)(uintptr_t)comp);
    assert(g_f5w[F5wIdx((DWORD)(uintptr_t)comp)].obj == 0);
    // window line (logs; F5wBad also notes the sticky address, which stays)
    F5wBad("selftest site", (DWORD)(uintptr_t)o, (DWORD)(uintptr_t)(cells + 3), kF5wF5);
    assert((DWORD)g_f5sAddr == a);
    // thread capture: another thread's eip and stack
    HANDLE ev = CreateEventW(NULL, TRUE, FALSE, NULL);
    DWORD tid = 0;
    HANDLE th = CreateThread(NULL, 0, F5tSleeper, ev, 0, &tid);
    assert(th);
    Sleep(50);
    F5wThreadNow(tid, t, sizeof(t));
    assert(!strncmp(t, "eip ", 4));
    F5wThreadNow(GetCurrentThreadId(), t, sizeof(t));
    assert(!strncmp(t, "this thread", 11));
    SetEvent(ev), WaitForSingleObject(th, 5000), CloseHandle(th), CloseHandle(ev);
    F5wCeText(1000, t, sizeof(t));
    F5wStatus();

    // --- [UafCheck] TravelHold: the travel cap from a travel save start to the next world load end
    g_uafHeapAt = &g_uafTHeap, g_uafFree = UafTFree, g_uafMin = 64, g_uafMax = 4096, g_uafDrArms = 0, g_uafDrWant = 0, g_uafOn = true, g_uafTravel = true;
    assert(UafInit(1, 32, 2) && g_uafTravelBytes == 2u << 20);
    g_uafMaxBytes = 3 * 0x70, g_uafTravelBytes = 16 * 0x70;
    u8* um = (u8*)VirtualAlloc(NULL, 16 * 0x100, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
    assert(um);
    u8* ub[16];
    for (int i = 0; i < 16; i++) ub[i] = UafTChunk(um + i * 0x100, 0x60);
    const int fr0 = g_uafTFreedN;
    for (int i = 0; i < 5; i++) UafFreeHook(g_uafTHeap, NULL, ub[i]);
    assert(g_uafN == 3 && g_uafTFreedN == fr0 + 2);
    F5wEvent("travel save start", 3);  // hold on
    assert(g_uafHold);
    for (int i = 5; i < 13; i++) UafFreeHook(g_uafTHeap, NULL, ub[i]);
    assert(g_uafN == 11 && g_uafTFreedN == fr0 + 2 && g_uafHoldIn == 8 && g_uafHoldEvict == 0);
    F5wEvent("world load", 4);  // hold off: the next free drains 8 and passes
    assert(!g_uafHold);
    UafFreeHook(g_uafTHeap, NULL, ub[13]);
    assert(g_uafN == 3 && g_uafTFreedN == fr0 + 2 + 9);
    assert(UafInit(48, 262144, 0) && g_uafTravelBytes == g_uafMaxBytes);  // TravelMaxMB 0 = MaxMB
    assert(UafInit(60, 262144, 64) && g_uafTravelBytes + (12u << 20) <= (64u << 20));  // the 64 MB clamp holds for the travel cap
    VirtualFree(g_uafRing, 0, MEM_RELEASE), g_uafRing = NULL;
    g_uafOn = false, g_uafTravel = false, g_uafHold = 0;
    g_uafHeapAt = (void* const*)(uintptr_t)0x011CB864, g_uafFree = NULL;
    VirtualFree(um, 0, MEM_RELEASE);

    WpDisarm(k);
    g_f5sOn = false, g_f5sAddr = 0, g_f5sSlot = -1, g_f5wOn = false, g_f5wDr = false, g_f5wWinAt = 0;
    VirtualFree(mem, 0, MEM_RELEASE);
    WpUninstall();
    if (g_log != INVALID_HANDLE_VALUE) CloseHandle(g_log);
    g_log = savedLog;
    printf("float5 sticky / watch: sticky keep watch (5.0 and other writes, never moved, never taken), portal check guard 0 / 1, last-OK table, "
           "window texts, sweep finds (cell, record array, string, slot +0x14) once each, DR rotation hits, thread capture, travel hold ok\n");
}
