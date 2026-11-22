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
// a callee that leaves a frame below itself, as an unwound and caught inner call would; answers its 2nd argument
static DWORD __cdecl FtFakeStale(DWORD, DWORD b) {
    volatile DWORD here = 0;
    FglFrame& f = g_fgl.frames[g_fgl.depth++];
    f.slot = (DWORD*)&here;
    f.ret = 0xDEAD0000;
    f.site = FGL_S_CORE;
    f.t0.QuadPart = 0;
    return b + here;
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
    // a frame left deeper than a timed call by an unwind caught inside it (no later call dropped it): the call's own return
    // must reach its caller, not the dead frame's address
    g_fglTarget[FGL_S_CORE] = (void*)FtFakeStale;
    g_ftCall = (void*)kFglStubs[FGL_S_CORE];
    g_ftArg1 = 0x5677;
    FtCall();
    assert(g_ftOut[0] == 0x5677 && g_ftOut[3] == 0xB1B1B1B1 && g_ftOut[5] == 0xE1E1E1E1 && g_ftEsp1 == g_ftEsp0 - 8 && g_fgl.depth == 1 &&
           g_fglk.stale == stale + 2);
    g_fglTarget[FGL_S_CORE] = (void*)FtFakeCdecl2;

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

// [FglFacing] / [FglMemo] (docs/671 §10-§11): the proof, the answered facings and the memo, on fakes
static int g_ffTests, g_ffFoot;
static DWORD g_ffTestAns, g_ffFootAns;
static float g_ffFootR2;
static int g_ffParts;   // parts the fake test "tests" (each through the real part stub)
static DWORD __cdecl FfPart(u8*, void*, DWORD) { return 1; }
// the game's facing test: it runs its parts through the part stub and leaves what the check stubs would
static DWORD __cdecl FfTest(u8* rec, DWORD) {
    g_ffTests++;
    typedef DWORD(__cdecl* PartFn)(u8*, void*, DWORD);
    for (int i = 0; i < g_ffParts; i++) ((PartFn)(void*)kFglStubs[FGL_S_PART_LOOP])(rec, NULL, 0);
    if (!g_ffTestAns) g_fgl.failCheck = FGC_B41120;
    return g_ffTestAns;
}
static DWORD __cdecl FfFoot(u8* rec, DWORD) {
    g_ffFoot++;
    const float* b = *(const float* const*)(rec + 0xA0);
    const float* e = *(const float* const*)(rec + 0xA4);
    float pos[2];
    memcpy(pos, rec + 8, sizeof(pos));
    g_ffFootR2 = e - b == 16 ? (b[0] - pos[0]) * (b[0] - pos[0]) + (b[1] - pos[1]) * (b[1] - pos[1]) : -1;
    return g_ffFootAns;
}
// the placement delegate searching again: one facing test of the nested search, through the real facing stub
static DWORD __cdecl FfNested(u8* rec, DWORD) {
    typedef DWORD(__cdecl* TestFn)(u8*, DWORD);
    return ((TestFn)(void*)kFglStubs[FGL_S_FACING])(rec, 0);
}
static void TestFglFacing() {
    void* savedTarget[FGL_SITES];
    memcpy(savedTarget, g_fglTarget, sizeof(savedTarget));
    const auto savedK = g_fglk;
    const auto savedF = g_fglf;
    typedef DWORD(__cdecl* TestFn)(u8*, DWORD);
    const TestFn facing = (TestFn)(void*)kFglStubs[FGL_S_FACING];
    const float step = 0.78539813f;
    static u8 rec[0x100];
    static float poly[8];
    float pos[2] = {7.5f, 3.5f};
    // a 2 x 1 rectangle about the position, or turned by 90 degrees for odd quarter turns: the disc is 0.5 either way
    auto place = [&](float x, float z, int idx) {
        pos[0] = x; pos[1] = z;
        memcpy(rec + 8, pos, sizeof(pos));
        const float ang = step * (float)idx;
        memcpy(rec + 0x14, &ang, sizeof(ang));
        const float hx = idx & 2 ? 0.5f : 1.0f, hz = idx & 2 ? 1.0f : 0.5f;
        const float q[8] = {x - hx, z - hz, x + hx, z - hz, x + hx, z + hz, x - hx, z + hz};
        memcpy(poly, q, sizeof(q));
    };
    memset(rec, 0, sizeof(rec));
    *(float**)(rec + 0xA0) = poly;
    *(float**)(rec + 0xA4) = poly + 8;
    *(DWORD*)rec = 0x1234;
    DWORD top = 0;
    g_fglk.prof = 0;
    g_fglk.logMs = 0x7FFFFFFF;
    g_fgl.owner = (LONG)GetCurrentThreadId();
    g_fgl.frames[0].slot = &top + 0x10000;
    g_fgl.frames[0].site = FGL_S_SEARCH;
    g_fgl.depth = 1;
    g_fgl.proof.on = false;
    g_fgl.spoiled = false;
    FglLearnReset(&g_fgl.learn);
    FglTallyReset(&g_fgl.t);
    memset(&g_fglf, 0, sizeof(g_fglf));
    g_fglf.ready = 1; g_fglf.mode = 1; g_fglf.noted = 16;   // no log line
    g_fglm.ready = 0;
    g_fglTarget[FGL_S_FACING] = (void*)FfTest;
    g_fglTarget[FGL_S_PART_LOOP] = (void*)FfPart;
    g_fglFootTest = FfFoot;
    g_fglStepTest = step;
    g_ffTests = g_ffFoot = 0;
    g_ffParts = 1;
    g_ffTestAns = 0; g_ffFootAns = 0;

    // tile 1: facing 0 fails at the footprint check; the inner octagon (0.5 - 1 mm) is blocked too -> proven for the facings
    // learned so far (0 only). Facings 2, 4, 6 are new: each runs once (and is learned); no second proof on a proven tile
    place(7.5f, 3.5f, 0);
    assert((u8)facing(rec, 0) == 0 && g_ffTests == 1 && g_ffFoot == 1 && g_fglf.proven == 1);
    assert(g_ffFootR2 > 0.2489f && g_ffFootR2 < 0.2491f && *(float**)(rec + 0xA0) == poly && *(float**)(rec + 0xA4) == poly + 8);   // 0.499^2
    place(7.5f, 3.5f, 0);
    assert((u8)facing(rec, 0) == 0 && g_ffTests == 1 && g_fglf.skipped == 1);   // the same facing again: answered
    for (int k = 2; k < 8; k += 2) { place(7.5f, 3.5f, k); assert((u8)facing(rec, 0) == 0); }
    assert(g_ffTests == 4 && g_fglf.unlearned == 3 && g_ffFoot == 1 && g_fglf.proven == 1);
    // tile 2: the first facing proves, the three other learned facings are answered
    place(8.5f, 3.5f, 4);
    assert((u8)facing(rec, 0) == 0 && g_ffTests == 5 && g_ffFoot == 2 && g_fglf.proven == 2);
    for (int k = 0; k < 8; k += 2) { place(8.5f, 3.5f, k); assert((u8)facing(rec, 0) == 0); }
    assert(g_ffTests == 5 && g_fglf.skipped == 5 && g_fgl.depth == 1);
    // the answered call is as clean as a real one (cdecl: nothing popped)
    g_ftCall = (void*)kFglStubs[FGL_S_FACING];
    g_ftArg0 = (DWORD)(uintptr_t)rec; g_ftArg1 = 0;
    FtCall();
    assert(g_ftOut[0] == 0 && g_ftOut[3] == 0xB1B1B1B1 && g_ftOut[4] == 0x51515151 && g_ftOut[5] == 0xE1E1E1E1 && g_ftEsp1 == g_ftEsp0 - 8 &&
           g_ffTests == 5 && g_fglf.skipped == 6);
    // an angle that is no facing (the own-facing kind) is never answered
    place(8.5f, 3.5f, 0);
    { const float ang = 0.3f; memcpy(rec + 0x14, &ang, sizeof(ang)); }
    assert((u8)facing(rec, 0) == 0 && g_ffTests == 6);
    // tile 3: the blocker does not reach the inner disc -> nothing proven, every facing runs
    place(9.5f, 3.5f, 0);
    g_ffFootAns = 1;
    assert((u8)facing(rec, 0) == 0 && g_fglf.rimOnly == 1);
    place(9.5f, 3.5f, 2);
    assert((u8)facing(rec, 0) == 0 && g_ffTests == 8 && g_fglf.proven == 2);
    // tile 4: the first facing passes -> no proof asked
    place(10.5f, 3.5f, 0);
    g_ffTestAns = 1; g_ffFootAns = 0;
    const int foot = g_ffFoot;
    assert((u8)facing(rec, 0) == 1 && g_ffFoot == foot);
    // tile 5 proven, then another object on the same record and position: the proof is not its
    place(11.5f, 3.5f, 0);
    g_ffTestAns = 0;
    assert((u8)facing(rec, 0) == 0 && g_fglf.proven == 3);
    *(DWORD*)rec = 0x5678;
    g_ffFootAns = 1;
    const int tests5 = g_ffTests;
    assert((u8)facing(rec, 0) == 0 && g_ffTests == tests5 + 1);
    *(DWORD*)rec = 0x1234;
    // Shadow: proven tiles still run every learned facing; one that passes is counted
    g_fglf.shadow = 1;
    g_ffFootAns = 0;
    place(12.5f, 3.5f, 0);
    const LONG skipped = g_fglf.skipped;
    assert((u8)facing(rec, 0) == 0 && g_fglf.proven == 4);
    place(12.5f, 3.5f, 2);
    assert((u8)facing(rec, 0) == 0 && g_fglf.shadowTests == 1 && g_fglf.shadowMismatch == 0 && g_fglf.shadowShape == 0);
    g_ffTestAns = 1;
    place(12.5f, 3.5f, 4);
    assert((u8)facing(rec, 0) == 1 && g_fglf.shadowTests == 2 && g_fglf.shadowMismatch == 1 && g_fglf.skipped == skipped);
    g_ffTestAns = 0;
    g_fglf.shadow = 0;
    // a part the position is outside of: no disc, no proof
    FglLearnReset(&g_fgl.learn);
    place(13.5f, 3.5f, 0);
    for (int i = 0; i < 4; i++) poly[2 * i] += 5.0f;
    assert((u8)facing(rec, 0) == 0 && g_fglf.noDisc == 1 && g_fglf.proven == 4);
    // a search nested in this one (the placement delegate searching again): neither it nor the rest of the outer search
    // answers from the outer's proof or learns into its table
    {
        const TestFn search = (TestFn)(void*)kFglStubs[FGL_S_SEARCH];
        g_fglTarget[FGL_S_SEARCH] = (void*)FfNested;
        FglLearnReset(&g_fgl.learn);
        place(14.5f, 3.5f, 0);
        assert((u8)facing(rec, 0) == 0 && g_fglf.proven == 5);   // proven: facing 0 would now be answered
        const LONG sk = g_fglf.skipped;
        const int tn = g_ffTests;
        assert((u8)search(rec, 0) == 0 && g_ffTests == tn + 1 && g_fglf.skipped == sk && g_fgl.depth == 1 && g_fgl.spoiled);
        place(14.5f, 3.5f, 0);
        assert((u8)facing(rec, 0) == 0 && g_ffTests == tn + 2 && g_fglf.skipped == sk);
        place(15.5f, 3.5f, 2);
        assert((u8)facing(rec, 0) == 0 && g_ffTests == tn + 3 && g_fglf.proven == 5 && g_fgl.learn.seen[0][0] && !g_fgl.learn.seen[2][0]);
        g_fglTarget[FGL_S_SEARCH] = savedTarget[FGL_S_SEARCH];
        g_fgl.spoiled = false;
    }

    // [FglMemo]: the failed tests of one search answer the same tests of the next search of the same run
    FglLearnReset(&g_fgl.learn);
    const auto savedM = g_fglm.ready;
    g_fglf.ready = 0;
    g_fglm.ready = 1; g_fglm.mode = 1; g_fglm.shadow = 0; g_fglm.gapMs = 1000; g_fglm.maxMs = 60000; g_fglm.noted = 16;
    g_fglm.live = false;
    g_fglMemoHhTest = true;
    g_fglm.hits = g_fglm.stored = g_fglm.carried = g_fglm.dropKey = g_fglm.shadowTests = g_fglm.shadowMismatch = 0;
    static u8 srec[0x60];
    memset(srec, 0, sizeof(srec));
    srec[0x2E] = 1; srec[0x30] = 1;
    g_fgl.owner = (LONG)GetCurrentThreadId();
    g_fgl.depth = 1;
    FglSearchBegin(srec);                      // search 1: a new memo
    const int t0 = g_ffTests;
    place(20.5f, 3.5f, 0);
    assert((u8)facing(rec, 0) == 0 && g_ffTests == t0 + 1 && g_fglm.stored == 1 && g_fglm.hits == 0);
    assert((u8)facing(rec, 0) == 0 && g_ffTests == t0 + 1 && g_fglm.hits == 1);    // even inside the same search
    g_ffTestAns = 1;
    place(21.5f, 3.5f, 0);
    assert((u8)facing(rec, 0) == 1 && (u8)facing(rec, 0) == 1 && g_ffTests == t0 + 3 && g_fglm.stored == 1);   // passes are not kept
    {   // a nested search (another object) is not answered from this search's memo and stores nothing in it
        const TestFn search = (TestFn)(void*)kFglStubs[FGL_S_SEARCH];
        g_fglTarget[FGL_S_SEARCH] = (void*)FfNested;
        g_ffTestAns = 0;
        place(20.5f, 3.5f, 0);
        assert((u8)search(rec, 0) == 0 && g_ffTests == t0 + 4 && g_fglm.hits == 1 && g_fglm.stored == 1);
        place(22.5f, 3.5f, 0);
        assert((u8)search(rec, 0) == 0 && g_ffTests == t0 + 5 && g_fglm.stored == 1);
        g_fglTarget[FGL_S_SEARCH] = savedTarget[FGL_S_SEARCH];
        g_ffTestAns = 1;
        g_ffTests -= 2;   // the counts below are those of the searches without this block
    }
    g_fglm.lastEnd = GetTickCount();
    FglSearchBegin(srec);                      // search 2, same key, right after: carried
    place(20.5f, 3.5f, 0);
    assert(g_fglm.carried == 1 && (u8)facing(rec, 0) == 0 && g_ffTests == t0 + 3 && g_fglm.hits == 2);
    place(20.5f, 3.5f, 2);
    g_ffTestAns = 0;
    assert((u8)facing(rec, 0) == 0 && g_ffTests == t0 + 4);                       // another facing of the tile: runs
    srec[0x2D] = 1;                            // search 3 with StayInRoom: another key, the memo starts empty
    FglSearchBegin(srec);
    place(20.5f, 3.5f, 0);
    assert(g_fglm.dropKey == 1 && (u8)facing(rec, 0) == 0 && g_ffTests == t0 + 5);
    g_fglm.lastEnd = GetTickCount() - 5000;    // search 4 long after: dropped
    FglSearchBegin(srec);
    assert(g_fglm.dropGap == 1 && (u8)facing(rec, 0) == 0 && g_ffTests == t0 + 6);
    g_fglm.shadow = 1;                         // Shadow: held tests run; a pass is a mismatch
    g_ffTestAns = 1;
    assert((u8)facing(rec, 0) == 1 && g_fglm.shadowTests == 1 && g_fglm.shadowMismatch == 1 && g_ffTests == t0 + 7);

    // [FglInside]: an armed search's facing tests on tiles without a room are answered 0 through the facing stub
    {
        static u8 savedI[sizeof(g_fgli)];
        memcpy(savedI, &g_fgli, sizeof(g_fgli));
        memset(&g_fgli, 0, sizeof(g_fgli));
        g_fgli.ready = 1; g_fgli.mode = 1; g_fgli.lines = 216;   // no log line
        g_fglm.ready = 0; g_fglm.live = false;
        g_fglRoomTest = [](void*, int tx, int tz, int level) -> int { return tx == 30 && tz == 3 && level == 1 ? 7 : tx > 100 ? 0xFFFF : 0; };
        srec[0x2D] = 0;
        *(DWORD*)(srec + 0x28) = 1;                // the record has a managed check
        assert(FglInsideOp(1) == 1 && (DWORD)g_fgli.armTid == GetCurrentThreadId());
        g_fgl.depth = 1;
        FglSearchBegin(srec);
        assert(g_fgl.ins.on && g_fgli.armTid == 0 && g_fgli.searches == 1);
        const int t1 = g_ffTests;
        g_ffTestAns = 1;
        place(31.5f, 3.5f, 0);                     // no room on levels -1, 0, 1: answered, the fake test does not run
        assert((u8)facing(rec, 0) == 0 && (u8)facing(rec, 0) == 0 && g_ffTests == t1 && g_fgl.ins.answered == 2 && g_fgl.ins.tilesOut == 1);
        place(30.5f, 3.5f, 0);                     // a room one level up: runs
        assert((u8)facing(rec, 0) == 1 && g_ffTests == t1 + 1 && g_fgl.ins.tilesIn == 1 && g_fgl.ins.passIn == 1);
        FglSearchEnd(true, 0);
        assert(g_fgli.found == 1 && g_fgli.mismatch == 0 && g_fgli.answered == 2 && !g_fgl.ins.on);
        FglSearchBegin(srec);                      // not armed: nothing answered
        place(31.5f, 3.5f, 0);
        assert(!g_fgl.ins.on && (u8)facing(rec, 0) == 1 && g_ffTests == t1 + 2);
        g_fgli.shadow = 1;                         // Shadow: the test runs; found on such a tile = mismatch
        FglInsideOp(1);
        FglSearchBegin(srec);
        assert((u8)facing(rec, 0) == 1 && g_ffTests == t1 + 3 && g_fgl.ins.ranOut == 1 && g_fgl.ins.passOut == 1);
        FglSearchEnd(true, 0);
        assert(g_fgli.mismatch == 1);
        FglInsideOp(1);                            // armed, but the record has no managed check: not the stage
        *(DWORD*)(srec + 0x28) = 0;
        FglSearchBegin(srec);
        assert(!g_fgl.ins.on && g_fgli.notStage == 1 && g_fgli.armTid == 0);
        FglInsideOp(1);
        assert(FglInsideOp(0) == 1 && g_fgli.armTid == 0);
        g_fgli.ready = 0;
        assert(FglInsideOp(1) == 0 && g_fgli.armTid == 0);
        g_fglRoomTest = NULL;
        memcpy(&g_fgli, savedI, sizeof(g_fgli));
    }

    g_fglm.ready = savedM;
    g_fglMemoHhTest = false;
    g_fglm.live = false;
    g_fglm.shadow = 0;
    g_fglFootTest = NULL;
    g_fglStepTest = 0;
    g_fgl.owner = 0;
    g_fgl.depth = 0;
    g_fgl.proof.on = false;
    g_fgl.testRec = NULL;
    g_fgl.inFacing = false;
    g_fgl.spoiled = false;
    FglTallyReset(&g_fgl.t);
    memcpy(g_fglTarget, savedTarget, sizeof(savedTarget));
    g_fglk = savedK;
    g_fglf = savedF;
    printf("ok FglFacing, FglMemo, FglInside\n");
}
