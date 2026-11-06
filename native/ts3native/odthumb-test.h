// TestThumbDrainCap ([ThumbDrainCap], odthumb.cpp, docs/533). Included by selftest.cpp.
// 1. The cap decision on a fake queue run the way 0xB373A0 runs it (erase the head, handle it, back-edge): cut after N
//    with the rest kept in order, next frames finish the queue, a full drain with the cap off.
// 2. The patch on dummies: all three sites or none, foreign bytes left alone.

// one drain of the fake queue q[0..n): returns how many requests it took (handled in order into out)
static int TdFakeDrain(DWORD* q, DWORD& n, DWORD* out, int& nout) {
    TdBegin(n);
    int took = 0;
    while (n) {
        out[nout++] = q[0];  // the head, as EA's loop takes it (erased before it is handled)
        memmove(q, q + 1, --n * sizeof(DWORD));
        took++;
        if (!n) { InterlockedIncrement(&ts3native_counters.tdReqs); break; }  // TdBackStub's `last`
        if (!TdMore()) break;
    }
    return took;
}

static void TestThumbDrainCap() {
    // the stubs' jump targets agree with the bytes the patch matches: call rel32 -> drain, jne rel32 -> loop top, the sites' ends -> the exits
    assert(TD_CALL + 5 + *(const LONG*)(kTdCall + 1) == TD_FN && TD_BACK + 13 + *(const LONG*)(kTdBack + 9) == TD_LOOP);
    assert(TD_BACK + 13 == TD_EXIT && TD_CLEAR + 7 == TD_CLEAR_BACK);
    Ts3nCounters& k = ts3native_counters;
    const LONG cap0 = g_tdCap;
    const LONGLONG budget0 = g_tdBudget;
    DWORD q[20], out[20];
    DWORD n = 20;
    int nout = 0;
    for (DWORD i = 0; i < 20; i++) q[i] = 100 + i;

    g_tdCap = 8, g_tdBudget = 0;
    assert(TdFakeDrain(q, n, out, nout) == 8 && n == 12 && q[0] == 108 && q[11] == 119);  // rest kept, in order
    assert(k.tdDrains == 1 && k.tdReqs == 8 && k.tdCut == 1 && k.tdMaxQueue == 20);
    assert(TdFakeDrain(q, n, out, nout) == 8 && n == 4 && q[0] == 116);
    assert(TdFakeDrain(q, n, out, nout) == 4 && n == 0);  // last frame: queue empty, not a cut
    assert(k.tdDrains == 3 && k.tdReqs == 20 && k.tdCut == 2);
    for (int i = 0; i < 20; i++) assert(out[i] == (DWORD)(100 + i));  // every request handled once, in queue order
    assert(TdFakeDrain(q, n, out, nout) == 0 && k.tdDrains == 3);    // empty queue: not counted

    g_tdCap = 1;  // the smallest cap still takes one per frame
    n = 3, nout = 0;
    for (DWORD i = 0; i < 3; i++) q[i] = i;
    assert(TdFakeDrain(q, n, out, nout) == 1 && n == 2 && q[0] == 1);

    g_tdCap = 0;  // cap off (no budget either): the whole queue in one call, as EA
    n = 20, nout = 0;
    for (DWORD i = 0; i < 20; i++) q[i] = i;
    const LONG cut = k.tdCut;
    assert(TdFakeDrain(q, n, out, nout) == 20 && n == 0 && k.tdCut == cut);

    g_tdBudget = 1;  // a 1-tick budget: once the clock moved, the next back-edge cuts
    TdBegin(5);
    LARGE_INTEGER t0, t1;
    QueryPerformanceCounter(&t0);
    do QueryPerformanceCounter(&t1); while (t1.QuadPart == t0.QuadPart);
    assert(TdMore() == 0 && k.tdCut == cut + 1);
    g_tdBudget = 0x7FFFFFFFFFFFLL;  // a budget not yet spent: no cut
    TdBegin(5);
    assert(TdMore() == 1 && k.tdCut == cut + 1);

    // the patch on dummies with the game's bytes, then with a foreign back-edge
    static u8 call[5], back[13], clear[7];
    memcpy(call, kTdCall, 5), memcpy(back, kTdBack, 13), memcpy(clear, kTdClear, 7);
    u8* const at[3] = {call, back, clear};
    assert(TdPatch(at));
    assert(call[0] == 0xE8 && (uintptr_t)call + 5 + *(LONG*)(call + 1) == (uintptr_t)&TdBeginStub);
    assert(back[0] == 0xE9 && (uintptr_t)back + 5 + *(LONG*)(back + 1) == (uintptr_t)&TdBackStub);
    assert(clear[0] == 0xE9 && (uintptr_t)clear + 5 + *(LONG*)(clear + 1) == (uintptr_t)&TdClearStub);
    assert(!TdPatch(at));  // second apply: the bytes are ours, nothing written
    static u8 call2[5], back2[13], clear2[7];
    memcpy(call2, kTdCall, 5), memcpy(back2, kTdBack, 13), memcpy(clear2, kTdClear, 7);
    back2[0] = 0xE9;  // another mod owns the back-edge
    u8* const at2[3] = {call2, back2, clear2};
    assert(!TdPatch(at2) && !memcmp(call2, kTdCall, 5) && !memcmp(clear2, kTdClear, 7));

    g_tdCap = cap0, g_tdBudget = budget0;
    k.tdDrains = k.tdReqs = k.tdCut = k.tdMaxQueue = 0;
    printf("ThumbDrainCap: 20 requests cut 8 / 8 / 4 in order, cap 1 takes one, cap off drains all, budget cuts, patch all-or-none: ok\n");
}
