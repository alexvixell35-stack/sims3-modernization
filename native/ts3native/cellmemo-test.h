// --- [CellMemoProbe] (cellmemo.cpp, docs/150 §12): layout, M key per field, repeats within a plan only, would-hit cell A* time,
// per-plan max and histogram, a full key list, the stub reading the wrapper's stack args. Included by selftest.cpp.
static void TestCellMemoProbe() {
    static_assert(offsetof(Ts3nCounters, cmReady) == 0x38AC && offsetof(Ts3nCounters, cmLegs) == 0x38B8 &&
                      offsetof(Ts3nCounters, cmCellUs) == 0x38C8 && offsetof(Ts3nCounters, cmHist) == 0x38D8 &&
                      offsetof(Ts3nCounters, cmFull) == 0x38F8 && sizeof(Ts3nCounters) == 6074 * 4,
                  "counters v88 layout after v89 (docs/registry.md)");
    Ts3nCounters& k = ts3native_counters;
    const size_t span = offsetof(Ts3nCounters, cmFull) + 4 - offsetof(Ts3nCounters, cmReady);
    memset(&k.cmReady, 0, span);
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
    const LONGLONG ms = g_qpf.QuadPart / 1000;
    // fake leg arguments (docs/98 §2): X, lot lo/hi, start, goal, threshold, goal vector, path, index out, any-room, no-nudge
    u8 X[0x60] = {};
    *(DWORD*)(X + 0x50) = 0x10000;
    float start[3] = {1.0f, 2.0f, 0.0f}, goal[3] = {5.0f, 6.0f, 0.0f};
    float recs[2][7] = {{5.0f, 6.0f, 0, 1.0f, 0, 0, 1.0f}, {7.0f, 8.0f, 0, 0, 1.0f, 0, 0.5f}};
    DWORD vec[2] = {(DWORD)(uintptr_t)recs, (DWORD)(uintptr_t)(recs + 2)};
    const float thr = 1.0f;
    DWORD in[12] = {0, (DWORD)(uintptr_t)X, 0x1234, 0x5678, (DWORD)(uintptr_t)start, (DWORD)(uintptr_t)goal, *(const DWORD*)&thr,
                    (DWORD)(uintptr_t)vec, 0, 0, 0xCCCCCC00, 0};  // any-room 0 (high bytes garbage: low byte only)
    DWORD tp[8] = {0, (DWORD)(uintptr_t)X, 0x1234, 0x5678, (DWORD)(uintptr_t)start, (DWORD)(uintptr_t)goal, 0, 1};
    DWORD q[2] = {};
    // off: nothing counted
    CmEnter(CMS_INROOM, in);
    assert(k.cmLegs[0] == 0);
    k.cmReady = 1;
    CmJob(true);
    // plan 1: InRoomPlan miss, same again = repeat; cell time lands in the hit only after the repeat
    CmEnter(CMS_QRESET, q);
    CmEnter(CMS_INROOM, in);
    assert(!t_cm.hit && k.cmLegs[0] == 1 && k.cmRepeats[0] == 0);
    CmCell(ms);
    assert(k.cmCellUs >= 990 && k.cmHitCellUs == 0);
    CmEnter(CMS_INROOM, in);
    assert(t_cm.hit && k.cmRepeats[0] == 1);
    CmCell(2 * ms);
    CmCell(ms);  // the retry loop's second search of the same leg
    assert(k.cmHitCellUs >= 2990 && k.cmHitCellN == 2);
    // any key field changed = a new leg: X+0x50, lot, start, goal point, threshold, a goal record, any-room, wrapper
    in[10] = 0xCCCCCC00;
    CmEnter(CMS_INROOM, in);  // garbage above the flag byte changed nothing
    assert(t_cm.hit && k.cmRepeats[0] == 2);
    *(DWORD*)(X + 0x50) = 0x2010000;
    CmEnter(CMS_INROOM, in);
    assert(!t_cm.hit);
    *(DWORD*)(X + 0x50) = 0x10000;
    in[3] = 0x5679;  // lot key hi
    CmEnter(CMS_INROOM, in);
    assert(!t_cm.hit);
    in[3] = 0x5678;
    start[0] = 1.5f;
    CmEnter(CMS_INROOM, in);
    assert(!t_cm.hit);
    start[0] = 1.0f;
    goal[1] = 6.5f;
    CmEnter(CMS_INROOM, in);
    assert(!t_cm.hit);
    goal[1] = 6.0f;
    in[6] = 0;
    CmEnter(CMS_INROOM, in);
    assert(!t_cm.hit);
    in[6] = *(const DWORD*)&thr;
    recs[1][6] = 0.25f;
    CmEnter(CMS_INROOM, in);
    assert(!t_cm.hit);
    recs[1][6] = 0.5f;
    in[10] = 1;
    CmEnter(CMS_INROOM, in);
    assert(!t_cm.hit);
    in[10] = 0;
    CmEnter(CMS_TOPOINT, tp);  // same start and point, other wrapper
    assert(!t_cm.hit && k.cmLegs[1] == 1);
    CmEnter(CMS_TOPOINT, tp);
    assert(t_cm.hit && k.cmRepeats[1] == 1);
    tp[7] = 0;  // no-nudge
    CmEnter(CMS_TOPOINT, tp);
    assert(!t_cm.hit);
    CmEnter(CMS_INROOM, in);  // the first key again
    assert(t_cm.hit && k.cmRepeats[0] == 3);
    // Q reset closes plan 1 (4 repeats: bucket 4-7) and forgets its keys
    CmEnter(CMS_QRESET, q);
    assert(k.cmPlans == 1 && k.cmHist[3] == 1 && k.cmMaxRepeats == 4 && t_cm.n == 0);
    CmEnter(CMS_INROOM, in);
    assert(!t_cm.hit);
    CmEnter(CMS_QRESET, q);  // plan 2: one leg, no repeat
    CmEnter(CMS_QRESET, q);  // no leg: not a plan
    assert(k.cmPlans == 2 && k.cmHist[0] == 1);
    // a full key list: 256 learned, the rest counted and not learned; the job's end closes the plan
    for (int i = 0; i < CM_KEYS + 3; i++) {
        start[0] = (float)i;
        CmEnter(CMS_TOPOINT, tp);
    }
    assert(k.cmFull == 3 && t_cm.n == CM_KEYS);
    start[0] = 0.0f;
    CmEnter(CMS_TOPOINT, tp);
    assert(t_cm.hit);
    CmJob(false);
    assert(k.cmPlans == 3 && k.cmHist[1] == 1 && k.cmMaxRepeats == 4);
    // the stub: worker only, args read at the wrapper's entry (InRoomPlan's prologue; body mov esp,ebp; pop ebp; mov eax,7; ret)
    u8* code = (u8*)VirtualAlloc(NULL, 4096, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
    assert(code);
    static const u8 kBody[] = {0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF0, 0x8B, 0xE5, 0x5D, 0xB8, 0x07, 0x00, 0x00, 0x00, 0xC3};
    memcpy(code, kBody, sizeof(kBody));
    assert(CmInstallAt(CMS_INROOM, (DWORD)(uintptr_t)code, NULL));
    assert(!CmInstallAt(CMS_TOPOINT, (DWORD)(uintptr_t)(code + 64), NULL));  // other bytes, no owner
    typedef int(__cdecl * Leg)(DWORD, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD, DWORD);
    const Leg leg = (Leg)code;
    const LONG legs0 = k.cmLegs[0], rep0 = k.cmRepeats[0];
    const DWORD tidSave = g_rjpTid;
    g_rjpTid = 0;  // not the worker: straight through
    assert(leg(in[1], in[2], in[3], in[4], in[5], in[6], in[7], 0, 0, 0, 0) == 7 && k.cmLegs[0] == legs0);
    g_rjpTid = GetCurrentThreadId();
    CmJob(true);
    assert(leg(in[1], in[2], in[3], in[4], in[5], in[6], in[7], 0, 0, 0, 0) == 7);
    assert(leg(in[1], in[2], in[3], in[4], in[5], in[6], in[7], 0, 0, 0, 0) == 7);
    assert(k.cmLegs[0] == legs0 + 2 && k.cmRepeats[0] == rep0 + 1);
    g_rjpTid = tidSave;
    VirtualFree(code, 0, MEM_RELEASE);
    memset(&k.cmReady, 0, span);
    t_cm.n = t_cm.legs = 0;
    t_cm.repeats = 0;
    t_cm.hit = false;
    printf("cell memo probe: layout, M key fields, repeats per plan, would-hit time, histogram, full list, stub ok\n");
}
