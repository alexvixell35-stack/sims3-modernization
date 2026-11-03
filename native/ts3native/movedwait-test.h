// TestMovedWaits ([MovedWaits], movedwait.cpp, docs/178 §9) on dummy jobs: no game code runs. Included by selftest.cpp.
static void TestMovedWaits() {
    Ts3nCounters& k = ts3native_counters;
    static_assert(offsetof(Ts3nCounters, mwReady) == 0x486C && offsetof(Ts3nCounters, mwSkips) == 0x4870 &&
                      offsetof(Ts3nCounters, mwAvoidUs) == 0x4948 && offsetof(Ts3nCounters, mwKey) == 0x4990 &&
                      offsetof(Ts3nCounters, mwKeyOther) == 0x4A20 && offsetof(Ts3nCounters, ntKey) == 0x4A24 &&
                      offsetof(Ts3nCounters, ntIk) == 0x4B14 && sizeof(Ts3nCounters) == 5836 * 4,
                  "counters v120 layout after v119 (docs/registry.md)");
    static_assert(MW_N == 9, "mw arrays are [9]");
    assert(k.version == 163);
    const size_t blk = offsetof(Ts3nCounters, ntIk) + sizeof(k.ntIk) - offsetof(Ts3nCounters, mwReady);
    static u8 save[0x400];
    memcpy(save, &k.mwReady, blk);
    const AsyncSlot as = g_async;
    const DWORD seqSave = g_asyncSeq;
    const LONG fbSave = g_jobFb, gvSave = k.gvMode, ikSave = k.ikMode;
    HaFrame& f = ts3native_hitchattr;
    const HaFrame fSave = f;
    memset(&k.mwReady, 0, blk);
    memset(g_mwPend, 0, sizeof(g_mwPend));
    for (int i = 0; i < 10; i++) k.ntKey[i] = (LONG)kNtKeys[i];
    const DWORD me = GetCurrentThreadId();
    g_async.owner = me, g_async.state = A_RUNNING, g_asyncSeq = 7, g_jobFb = 0, k.gvMode = 1, k.ikMode = 0;

    MwSkip(MW_GV);  // off: nothing
    assert(k.mwSkips[MW_GV] == 0 && MwSeqNow() == 7);
    MwWait(0x0075AA16, 100, 7);
    assert(k.ntN[0] == 0);
    k.mwReady = 1, f.enabled = 1, f.mvUs = f.mvN = f.mvSw = f.mvMaxUs = f.mvKey = 0;
    MwSkip(MW_GV), MwSkip(MW_GV);
    assert(k.mwSkips[MW_GV] == 2 && k.mwJobs[MW_GV] == 1);
    Sleep(6);
    const DWORD tok = MwSeqNow();
    WaitNote(0x0075AA16, 1500, tok);  // the gather after T6's empty answer, same job
    assert(k.mwMoved[MW_GV] == 1 && k.mwMovedUs[MW_GV] == 1500 && k.mwMovedMaxUs[MW_GV] == 1500 && k.mwMaxKey[MW_GV] == 0x0075AA16);
    assert(k.mwDelayUs[MW_GV] >= 2000 && k.mwKey[0] == 0x0075AA16 && k.mwKeyN[0] == 1 && k.mwKeyUs[0] == 1500);
    assert(k.ntN[0] == 1 && k.ntUs[0] == 1500 && k.ntMaxUs[0] == 1500 && k.ntGv[0] == 1 && k.ntIk[0] == 0 && k.ntFb[0] == 0);
    assert(f.mvUs == 1500 && f.mvN == 1 && f.mvSw == 1 << MW_GV && f.mvKey == 0x0075AA16);
    WaitNote(0x0075A64C, 900, tok);  // a second wait behind the same job: the record is closed
    assert(k.mwMoved[MW_GV] == 1 && k.ntN[1] == 1 && f.mvN == 1);
    MwJobEnd(7);
    assert(k.mwAvoidUs[MW_GV] >= 5000 && k.mwAvoidUs[MW_IK] == 0);

    g_asyncSeq = 8, g_jobFb = 1, k.ikMode = 1;  // a foreign job; IdleCheckRO onward goes on to 0x755990
    MwSkip(MW_IK);
    const DWORD tokFb = MwSeqNow();
    assert(tokFb == (8 | 0x80000000));
    WaitNote(0x00755990, 700, 9);  // a wait behind another job: not moved
    assert(k.mwMoved[MW_IK] == 0 && k.ntN[2] == 1 && k.ntFb[2] == 0);
    WaitNote(0x00755990, 800, tokFb);
    assert(k.mwMoved[MW_IK] == 1 && k.mwMovedUs[MW_IK] == 800 && k.ntN[2] == 2 && k.ntFb[2] == 1 && k.ntIk[2] == 2);
    assert(k.mwMoved[MW_GV] == 1 && f.mvSw == (1 << MW_GV | 1 << MW_IK) && f.mvUs == 2300 && f.mvMaxUs == 1500);
    WaitNote(0x00755990, 800, 0);  // no job at the wait's start: nothing
    assert(k.ntN[2] == 2);

    g_async.owner = me + 1;  // not the sim thread
    MwSkip(MW_SL);
    g_async.owner = me, g_async.state = A_IDLE;  // no job
    MwSkip(MW_SL);
    assert(k.mwSkips[MW_SL] == 0 && MwSeqNow() == 0);
    g_async.state = A_RUNNING;
    for (int i = 0; i < 13; i++) {  // key table: 12 slots, then mwKeyOther
        g_asyncSeq = 100 + i;
        MwSkip(MW_S2);
        WaitNote(0x00900000 + i, 10, MwSeqNow());
    }
    assert(k.mwMoved[MW_S2] == 13 && k.mwKeyOther == 3 && k.mwKey[1] == 0x00755990 && k.mwKey[11] == 0x00900009);
    MwLogCounters();

    memcpy(&k.mwReady, save, blk);
    memset(g_mwPend, 0, sizeof(g_mwPend));
    g_async = as, g_asyncSeq = seqSave, g_jobFb = fbSave, k.gvMode = gvSave, k.ikMode = ikSave;
    f = fSave;
    printf("MovedWaits: skip records per (switch, job), next wait behind the same job moved (key, ms, delay), job end -> avoided, "
           "next-taker keys by job kind and switch, hitch frame v2, off / not sim / no job / other job pass: ok\n");
}
