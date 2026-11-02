// TestCompaction ([Compaction], compaction.cpp, docs/204 §3). Included by selftest.cpp. Pure verdict rules plus the op 7
// value bits; no game, no walk.
static void TestCompaction() {
    const DWORD H = 3600000;  // ms per hour
    // threshold: Apex MemoryGuard lifts ours to at least 320, never lowers it
    assert(CpThreshold(320, false) == 320 && CpThreshold(200, false) == 200 && CpThreshold(200, true) == 320 && CpThreshold(500, true) == 500);
    // off / failed save / above the threshold: no
    CpState s;
    assert(!CpVerdict(s, 0, 320, 256, 60, false, 100, H, true));
    assert(!CpVerdict(s, 1, 320, 256, 60, false, 100, H, false));
    assert(!CpVerdict(s, 1, 320, 256, 60, false, 320, H, true) && s.armed);
    // first one this session: yes at once (no world load seen yet), and the arm is used up
    assert(CpVerdict(s, 1, 320, 256, 60, false, 319, H, true) && !s.armed && s.verdictMB == 319);
    // not re-armed: no, even after hours
    s.lastTick = H;
    assert(!CpVerdict(s, 1, 320, 256, 60, false, 200, 5 * H, true));
    // back above 320 + 256 re-arms (a sample or a save above it), then under 320 again but within 60 min of the last: no
    assert(!CpVerdict(s, 1, 320, 256, 60, false, 576, H + 10 * 60000, true) && s.armed);
    assert(!CpVerdict(s, 1, 320, 256, 60, false, 300, H + 59 * 60000, true) && s.armed);
    assert(CpVerdict(s, 1, 320, 256, 60, false, 300, 2 * H, true) && !s.armed);
    // 575 does not re-arm; Apex lifts a 200 threshold to 320 (and its re-arm line to 576)
    CpState a;
    a.armed = false;
    assert(!CpVerdict(a, 1, 200, 256, 0, true, 575, H, true) && !a.armed);
    assert(!CpVerdict(a, 1, 200, 256, 0, true, 576, H, true) && a.armed);
    assert(CpVerdict(a, 1, 200, 256, 0, true, 250, H, true));  // 250 < 320 with Apex, though >= our 200
    // did it help (§3.4): after >= threshold + rearm
    assert(CpHelped(576, 320, 256, false) && !CpHelped(575, 320, 256, false) && !CpHelped(500, 200, 256, true) && CpHelped(456, 200, 256, false));
    // off for the session: never compacts; the restart notice only after a compaction ran, once
    CpState o;
    o.off = true;
    assert(!CpVerdict(o, 1, 320, 256, 0, false, 100, H, true) && !o.noticeDue);  // off by a failure, nothing ran
    o.notHelped = true;
    assert(!CpVerdict(o, 1, 320, 256, 0, false, 100, H, true) && o.noticeDue);
    o.noticeDue = false, o.noticeGiven = true;
    assert(!CpVerdict(o, 1, 320, 256, 0, false, 100, H, true) && !o.noticeDue);
    // op 7 value bits: largest free at the verdict and the gain, bit by bit; mode / route answers
    const int m0 = g_cpMode, r0 = g_cpRoute;
    const CpState c0 = g_cp;
    g_cpMode = 2, g_cpRoute = 1;
    g_cp.verdictMB = 0x2A5, g_cp.beforeMB = 300, g_cp.afterMB = 1200;
    DWORD v = 0, gain = 0;
    for (DWORD b = 0; b < 16; b++) v |= (DWORD)CpOp(0x10000 | b) << b, gain |= (DWORD)CpOp(0x10000 | 1 << 5 | b) << b;
    assert(v == 0x2A5 && gain == 900 && CpOp(2) == 1 && CpOp(3) == 1);
    g_cp.afterMB = 200;
    assert(!CpOp(0x10000 | 1 << 5 | 2));  // no gain: 0, never a wrapped value
    g_cpMode = 0;
    assert(!CpOp(2) && !CpOp(0x10000));  // off: every op false
    g_cpMode = m0, g_cpRoute = r0, g_cp = c0;
    printf("selftest compaction: ok\n");
}
