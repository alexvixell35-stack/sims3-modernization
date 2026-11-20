// docs/671: [FglProf] trampolines (fglprof.cpp) against fake callees of both conventions: the game's caller must see its
// callee's eax / ecx / edx / flags and its own ebx / esi / edi and stack exactly as without the hook, whether the callee
// pops its arguments (`ret 4`, the s150 crash) or not, on the pass-through path and on the timed path. Windows selftest only.
#pragma once

static DWORD g_ftSeen[5], g_ftOut[6], g_ftEsp0, g_ftEsp1, g_ftCarry, g_ftArg0, g_ftArg1;
static void* g_ftCall;

// records the registers it was entered with; answers its 1st / 2nd stack argument; leaves ecx, edx and carry for its caller
static __declspec(naked) void FtFakeStd1() {
    __asm mov dword ptr [g_ftSeen], ecx __asm mov dword ptr [g_ftSeen + 4], edx __asm mov dword ptr [g_ftSeen + 8], ebx
    __asm mov dword ptr [g_ftSeen + 12], esi __asm mov dword ptr [g_ftSeen + 16], edi __asm mov eax, [esp + 4]
    __asm mov ecx, 0x11111111 __asm mov edx, 0x22222222 __asm stc __asm ret 4
}
static __declspec(naked) void FtFakeCdecl2() {
    __asm mov dword ptr [g_ftSeen], ecx __asm mov dword ptr [g_ftSeen + 4], edx __asm mov dword ptr [g_ftSeen + 8], ebx
    __asm mov dword ptr [g_ftSeen + 12], esi __asm mov dword ptr [g_ftSeen + 16], edi __asm mov eax, [esp + 8]
    __asm mov ecx, 0x11111111 __asm mov edx, 0x22222222 __asm stc __asm ret
}
// a game-like call site: live registers, two pushed words, the call, then everything it can observe
static void FtCall() {
    __asm {
        pushad
        mov g_ftEsp0, esp
        mov ecx, 0xC1C1C1C1
        mov edx, 0xD1D1D1D1
        mov ebx, 0xB1B1B1B1
        mov esi, 0x51515151
        mov edi, 0xE1E1E1E1
        push g_ftArg1
        push g_ftArg0
        clc
        call dword ptr [g_ftCall]
        mov dword ptr [g_ftOut], eax
        mov dword ptr [g_ftOut + 4], ecx
        mov dword ptr [g_ftOut + 8], edx
        mov dword ptr [g_ftOut + 12], ebx
        mov dword ptr [g_ftOut + 16], esi
        mov dword ptr [g_ftOut + 20], edi
        mov eax, 0
        setc al
        mov g_ftCarry, eax
        mov g_ftEsp1, esp
        mov esp, g_ftEsp0
        popad
    }
}
static bool FtTransparent(DWORD eax, DWORD popped) {
    return g_ftOut[0] == eax && g_ftOut[1] == 0x11111111 && g_ftOut[2] == 0x22222222 && g_ftOut[3] == 0xB1B1B1B1 && g_ftOut[4] == 0x51515151 &&
           g_ftOut[5] == 0xE1E1E1E1 && g_ftCarry == 1 && g_ftSeen[0] == 0xC1C1C1C1 && g_ftSeen[1] == 0xD1D1D1D1 && g_ftSeen[2] == 0xB1B1B1B1 &&
           g_ftSeen[3] == 0x51515151 && g_ftSeen[4] == 0xE1E1E1E1 && g_ftEsp1 == g_ftEsp0 - 8 + popped;
}

static void TestFglProfStubs() {
    void* savedTarget[FGL_SITES];
    memcpy(savedTarget, g_fglTarget, sizeof(savedTarget));
    const auto savedK = g_fglk;
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
    g_fglk.logMs = 60000;   // no log line from here
    const DWORD tid = GetCurrentThreadId();

    // every stub, no profiled search: pure pass-through, both conventions
    g_fgl.owner = 0;
    g_fgl.depth = 0;
    FglTallyReset(&g_fgl.t);
    for (int i = 1; i < FGL_SITES; i++) {
        const bool std1 = i == FGL_S_TILE_A || i == FGL_S_TILE_B;
        g_fglTarget[i] = std1 ? (void*)FtFakeStd1 : (void*)FtFakeCdecl2;
        g_ftCall = (void*)kFglStubs[i];
        g_ftArg0 = 0x1234; g_ftArg1 = 0x5677;
        memset(g_ftOut, 0, sizeof(g_ftOut));
        FtCall();
        assert(FtTransparent(std1 ? 0x1234 : 0x5677, std1 ? 4 : 0));
    }
    assert(g_fgl.t.tilesVisited == 0 && g_fgl.depth == 0 && g_fgl.owner == 0);

    // inside a profiled search (frame 0 = the search, higher on the stack than anything here): timed path
    DWORD top = 0;
    g_fgl.owner = (LONG)tid;
    g_fgl.frames[0].slot = &top + 0x10000;
    g_fgl.frames[0].site = FGL_S_SEARCH;
    g_fgl.depth = 1;
    g_fgl.failCheck = -1;
    g_fglTarget[FGL_S_TILE_A] = (void*)FtFakeStd1;
    g_ftCall = (void*)kFglStubs[FGL_S_TILE_A];
    g_ftArg0 = 0x1234;
    FtCall();
    assert(FtTransparent(0x1234, 4) && g_fgl.t.tilesVisited == 1 && g_fgl.t.tilesInLot == 1 && g_fgl.depth == 1);
    g_ftArg0 = 0x1200;   // al 0: outside the lot
    FtCall();
    assert(FtTransparent(0x1200, 4) && g_fgl.t.tilesVisited == 2 && g_fgl.t.tilesInLot == 1 && g_fgl.depth == 1);
    // a check that answers 0 names itself; the 0xB46160 stub alone names the unhooked 0xB41120
    g_fglTarget[FGL_S_CHECK0 + 1] = (void*)FtFakeCdecl2;
    g_ftCall = (void*)kFglStubs[FGL_S_CHECK0 + 1];
    g_ftArg1 = 0x100;
    FtCall();
    assert(FtTransparent(0x100, 0) && g_fgl.failCheck == FGC_B45B90 && g_fgl.depth == 1);
    g_fgl.failCheck = -1;
    g_fglTarget[FGL_S_CORE] = (void*)FtFakeCdecl2;
    g_ftCall = (void*)kFglStubs[FGL_S_CORE];
    FtCall();
    assert(FtTransparent(0x100, 0) && g_fgl.failCheck == FGC_B41120);
    // a placement test: tile from the record's position words, failed with the check noted during it
    static u8 rec[0x60];
    memset(rec, 0, sizeof(rec));
    rec[8] = 7;
    g_fglTarget[FGL_S_FACING] = (void*)FtFakeCdecl2;
    g_ftCall = (void*)kFglStubs[FGL_S_FACING];
    g_ftArg0 = (DWORD)(uintptr_t)rec; g_ftArg1 = 0;
    FtCall();   // the fake runs no check: "outside the part"
    assert(FtTransparent(0, 0) && g_fgl.t.tests == 1 && g_fgl.t.tilesTested == 1 && g_fgl.t.rotTiles == 1 && g_fgl.t.fail[FGC_OTHER] == 1);
    g_ftArg1 = 1;
    FtCall();   // second facing on the tile passes: a flip
    assert(FtTransparent(1, 0) && g_fgl.t.tests == 2 && g_fgl.t.passes == 1 && g_fgl.t.rotExtra == 1 && g_fgl.t.rotFlip == 1 && g_fgl.depth == 1);
    // a frame left behind by an unwound call (at or below the new call's stack position) is dropped, not returned through
    g_fgl.frames[1].slot = (DWORD*)(uintptr_t)(g_ftEsp0 - 0x400);
    g_fgl.frames[1].site = FGL_S_CORE;
    g_fgl.depth = 2;
    const LONG stale = g_fglk.stale;
    g_ftArg0 = (DWORD)(uintptr_t)rec;
    FtCall();
    assert(FtTransparent(1, 0) && g_fgl.depth == 1 && g_fglk.stale == stale + 1);

    // the search site: takes the profile, tallies, gives it back
    g_fgl.owner = 0;
    g_fgl.depth = 0;
    const LONG searches = g_fglk.searches, failed = g_fglk.failed;
    g_fglTarget[FGL_S_SEARCH] = (void*)FtFakeCdecl2;
    g_ftCall = (void*)kFglStubs[FGL_S_SEARCH];
    rec[0x2E] = 1; rec[0x30] = 1;
    g_ftArg0 = (DWORD)(uintptr_t)rec; g_ftArg1 = 0;
    FtCall();
    assert(FtTransparent(0, 0) && g_fgl.owner == 0 && g_fgl.depth == 0 && g_fglk.searches == searches + 1 && g_fglk.failed == failed + 1);
    assert(g_fgl.key.bools == 0x14 && g_fgl.key.pos[0] == 7);
    // another thread owns the profile: this thread's search and inner calls pass through untouched
    g_fgl.owner = (LONG)(tid + 4);
    FtCall();
    g_ftCall = (void*)kFglStubs[FGL_S_TILE_A];
    g_ftArg0 = 0x1234;
    FtCall();
    assert(FtTransparent(0x1234, 4) && g_fglk.searches == searches + 1 && g_fgl.depth == 0 && g_fgl.owner == (LONG)(tid + 4));

    g_fgl.owner = 0;
    g_fgl.depth = 0;
    FglTallyReset(&g_fgl.t);
    memcpy(g_fglTarget, savedTarget, sizeof(savedTarget));
    g_fglk = savedK;
    printf("ok FglProf stubs\n");
}
