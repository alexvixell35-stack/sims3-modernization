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

// [FglFacing] (docs/671 §10): the proof and the answered facings, on fakes
static int g_ffTests, g_ffFoot;
static DWORD g_ffTestAns, g_ffFootAns;
static float g_ffFootR2;
// the game's facing test: here it only leaves what the inner stubs would (the octagon part ran, 0xB46160 failed unnamed)
static DWORD __cdecl FfTest(u8*, DWORD) {
    g_ffTests++;
    g_fgl.t.octTests++;
    if (!g_ffTestAns) g_fgl.failCheck = FGC_B41120;
    return g_ffTestAns;
}
static DWORD __cdecl FfFoot(u8* rec, DWORD) {
    g_ffFoot++;
    const float* b = *(const float* const*)(rec + 0xA0);
    float pos[2];
    memcpy(pos, rec + 8, sizeof(pos));
    g_ffFootR2 = (b[0] - pos[0]) * (b[0] - pos[0]) + (b[1] - pos[1]) * (b[1] - pos[1]);
    return g_ffFootAns;
}
static void TestFglFacing() {
    void* savedTarget[FGL_SITES];
    memcpy(savedTarget, g_fglTarget, sizeof(savedTarget));
    const auto savedK = g_fglk;
    const auto savedF = g_fglf;
    typedef DWORD(__cdecl* TestFn)(u8*, DWORD);
    const TestFn facing = (TestFn)(void*)kFglStubs[FGL_S_FACING];
    const float kC = 0.70710678f * 0.25f;
    const float base[16] = {0.25f, 0, kC, kC, 0, 0.25f, -kC, kC, -0.25f, 0, -kC, -kC, 0, -0.25f, kC, -kC};
    static u8 rec[0x100];
    static float poly[16];
    float pos[2] = {7.5f, 3.5f};
    auto place = [&](float x, float z) {
        pos[0] = x; pos[1] = z;
        memcpy(rec + 8, pos, sizeof(pos));
        for (int i = 0; i < 8; i++) { poly[2 * i] = base[2 * i] + x; poly[2 * i + 1] = base[2 * i + 1] + z; }
    };
    memset(rec, 0, sizeof(rec));
    *(float**)(rec + 0xA0) = poly;
    *(float**)(rec + 0xA4) = poly + 16;
    *(DWORD*)rec = 0x1234;
    DWORD top = 0;
    g_fglk.prof = 0;
    g_fglk.logMs = 0x7FFFFFFF;
    g_fgl.owner = (LONG)GetCurrentThreadId();
    g_fgl.frames[0].slot = &top + 0x10000;
    g_fgl.frames[0].site = FGL_S_SEARCH;
    g_fgl.depth = 1;
    g_fgl.proof.on = false;
    FglTallyReset(&g_fgl.t);
    memset(&g_fglf, 0, sizeof(g_fglf));
    g_fglf.ready = 1; g_fglf.mode = 1; g_fglf.noted = 16;   // no log line
    g_fglTarget[FGL_S_FACING] = (void*)FfTest;
    g_fglFootTest = FfFoot;
    g_ffTests = g_ffFoot = 0;

    // tile 1: the first facing fails at the footprint check, blocked again with the polygon drawn in -> the next 7 are answered
    place(7.5f, 3.5f);
    float before[16];
    memcpy(before, poly, sizeof(before));
    g_ffTestAns = 0; g_ffFootAns = 0;
    assert((u8)facing(rec, 0) == 0 && g_ffTests == 1 && g_ffFoot == 1 && g_fglf.proven == 1);
    assert(g_ffFootR2 > 0.0619f && g_ffFootR2 < 0.0621f && !memcmp(before, poly, sizeof(before)));   // 0.249^2, polygon put back
    for (int i = 0; i < 7; i++) assert((u8)facing(rec, 0) == 0);
    assert(g_ffTests == 1 && g_ffFoot == 1 && g_fglf.skipped == 7 && g_fgl.depth == 1);
    // the answered call is as clean as a real one: registers, flags aside, and the caller's stack (cdecl: nothing popped)
    g_ftCall = (void*)kFglStubs[FGL_S_FACING];
    g_ftArg0 = (DWORD)(uintptr_t)rec; g_ftArg1 = 0;
    FtCall();
    assert(g_ftOut[0] == 0 && g_ftOut[3] == 0xB1B1B1B1 && g_ftOut[4] == 0x51515151 && g_ftOut[5] == 0xE1E1E1E1 && g_ftEsp1 == g_ftEsp0 - 8 &&
           g_ffTests == 1 && g_fglf.skipped == 8);
    // tile 2: only the rim is blocked -> nothing proven, every facing runs
    place(8.5f, 3.5f);
    g_ffFootAns = 1;
    assert((u8)facing(rec, 0) == 0 && (u8)facing(rec, 0) == 0 && g_ffTests == 3 && g_ffFoot == 2 && g_fglf.rimOnly == 1 && g_fglf.proven == 1);
    // tile 3: the first facing passes -> no proof asked
    place(9.5f, 3.5f);
    g_ffTestAns = 1; g_ffFootAns = 0;
    assert((u8)facing(rec, 0) == 1 && g_ffFoot == 2);
    // tile 4: not the octagon about the position (a pivot offset) -> refused
    place(10.5f, 3.5f);
    poly[0] += 0.02f;
    g_ffTestAns = 0;
    assert((u8)facing(rec, 0) == 0 && (u8)facing(rec, 0) == 0 && g_ffFoot == 2 && g_fglf.notOctagon == 1 && g_ffTests == 6);
    // tile 5 proven, then another object on the same record and position: the proof is not its
    place(11.5f, 3.5f);
    assert((u8)facing(rec, 0) == 0 && g_fglf.proven == 2 && g_ffTests == 7);
    *(DWORD*)rec = 0x5678;
    g_ffFootAns = 1;
    assert((u8)facing(rec, 0) == 0 && g_ffTests == 8);
    *(DWORD*)rec = 0x1234;
    // Shadow: proven tiles still run every facing; one that passes, or shows another polygon, is counted
    g_fglf.shadow = 1;
    place(12.5f, 3.5f);
    g_ffFootAns = 0;
    const LONG skipped = g_fglf.skipped;
    assert((u8)facing(rec, 0) == 0 && g_fglf.proven == 3);
    assert((u8)facing(rec, 0) == 0 && g_fglf.shadowTests == 1 && g_fglf.shadowMismatch == 0 && g_fglf.shadowShape == 0);
    g_ffTestAns = 1;
    assert((u8)facing(rec, 0) == 1 && g_fglf.shadowTests == 2 && g_fglf.shadowMismatch == 1 && g_fglf.skipped == skipped);
    g_ffTestAns = 0;
    poly[0] += 0.01f;
    assert((u8)facing(rec, 0) == 0 && g_fglf.shadowTests == 3 && g_fglf.shadowShape == 1);
    // Mode=0 without Shadow: nothing proven, nothing answered
    g_fglf.shadow = 0; g_fglf.mode = 0;
    place(13.5f, 3.5f);
    const int tests = g_ffTests;
    assert((u8)facing(rec, 0) == 0 && (u8)facing(rec, 0) == 0 && g_ffTests == tests + 2 && g_fglf.proven == 3);

    g_fglFootTest = NULL;
    g_fgl.owner = 0;
    g_fgl.depth = 0;
    g_fgl.proof.on = false;
    g_fgl.testRec = NULL;
    FglTallyReset(&g_fgl.t);
    memcpy(g_fglTarget, savedTarget, sizeof(savedTarget));
    g_fglk = savedK;
    g_fglf = savedF;
    printf("ok FglFacing\n");
}
