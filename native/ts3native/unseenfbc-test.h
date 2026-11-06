// TestUnseenFallbackSites: docs/202 §3.4 C1-C4 (unseenfbc.cpp) on fakes: the unseen list channel, the owner rules (agent
// context for C1 / C2, the running script task for C3 / C4), the timed take (taken = the block runs under the hold and is
// released once; timed out = the harmless answer, nothing released), one wait per busy job and the C3 / C4 recovery window,
// rtoro.cpp's R-acq / R-call / R-rel states and fgro.cpp's game call. No game code. Uses unseen-test.h's fake objects,
// transform and camera (on screen = |x| < 10, reach 80 m) and selftest.cpp's MonoScriptHost stand-in (task id +0xBD0).
static LONG g_ufcFkR = 1, g_ufcFkCalls, g_ufcFkRel;
static DWORD g_ufcFkDl;
static LONG __fastcall UfcFkAcq(void*, void*, const DWORD* dl) { g_ufcFkCalls++, g_ufcFkDl = *dl; return g_ufcFkR; }
static LONG __fastcall UfcFkRel(void*, void*) { g_ufcFkRel++; return 0; }
static int g_ufcFkNext;
static void __cdecl UfcFkList(void*, void*, void*, DWORD, DWORD) { g_ufcFkNext++; }
static u64 __cdecl UfcFkAvA(u8*, u8*, const float*, const void*, DWORD) { g_ufcFkNext++; return 0x77; }
static DWORD __fastcall UfcFkFg(u8*, void*, const u8*, const float*, float, void*, DWORD) { g_ufcFkNext++; return 0; }
static DWORD* __fastcall UfcFkRoute(u8*, void*, DWORD* out, u8*, u8*, const float*, u8*, DWORD, DWORD, DWORD, DWORD, DWORD) {
    g_ufcFkNext++, out[0] = 0, out[2] = out[3] = 0;
    return out;
}

static void TestUnseenFallbackSites() {
    Ts3nCounters& k = ts3native_counters;
    static_assert(offsetof(Ts3nCounters, ufcMode) == 0x5390 && offsetof(Ts3nCounters, ufcReady) == 0x5398 &&
                      offsetof(Ts3nCounters, ufcTry) == 0x53A8 && offsetof(Ts3nCounters, ufcSeen) == 0x53F8 &&
                      offsetof(Ts3nCounters, ufcMaxWaitUs) == 0x5418 && offsetof(Ts3nCounters, ufcStale) == 0x5420 &&
                      offsetof(Ts3nCounters, ufEffCutUs) == 0x5424 && offsetof(Ts3nCounters, ufOverSamples) == 0x543C &&
                      offsetof(Ts3nCounters, ufwMode) == 0x5440 && offsetof(Ts3nCounters, ufwRelease) == 0x545C &&
                      offsetof(Ts3nCounters, ufwFull) == 0x5470 && offsetof(Ts3nCounters, ufcBb) == 0x5474 && sizeof(Ts3nCounters) == 5933 * 4,
                  "counters v139 / v140 layout after v138 (docs/registry.md)");
    assert(k.version == 910);
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
    // save and fake
    const UnCompFn compSave = g_unComp;
    const UnPosFn posSave = g_unPos;
    const UnFrustumFn frSave = g_unFrustum;
    const UnInFrustumFn inSave = g_unInFrustum;
    const UnCamFn cpSave = g_unCamPos, ctSave = g_unCamTgt;
    const UnFindFn findSave = g_unFind;
    const DWORD *btSave = g_unBitTransform, *bsSave = g_unBitSim;
    void* const* mgrSave = g_unObjMgr;
    const AsyncSlot asyncSave = g_async;
    const LONG rgOwnSave = k.rgOwnerTid;
    g_unComp = UnFakeComp, g_unPos = UnFakePos, g_unFrustum = UnFakeFrustum, g_unInFrustum = UnFakeIn;
    g_unCamPos = UnFakeCamPos, g_unCamTgt = UnFakeCamTgt, g_unBitTransform = &kUnBitT, g_unBitSim = &kUnBitS;
    g_unFakeCam[0][0] = 0, g_unFakeCam[0][1] = 30, g_unFakeCam[0][2] = 0;
    memset(g_unFakeCam[1], 0, 12);
    g_unCam.ok = false;
    g_ufcAcq = UfcFkAcq, g_ufcRel = UfcFkRel;
    static u8 fakeWorld[0x1000];
    g_async.owner = GetCurrentThreadId(), g_async.lock = fakeWorld + 0x918, g_async.state = A_RUNNING, g_async.handle = 0x25000001;
    k.rgOwnerTid = 0;
    k.ufcReady = 15 | 16 | 32, k.ufcMode = 1, k.ufcWaitMs = 100;
    auto Send = [](int code, DWORD lo) { return GetOptionHook(0, lo, 0x83000000 | (DWORD)code << 16, 1); };

    // a far, off-screen Sim (transform fake: pos, sim flag, parent) whose id is the bit pattern of pos[2] / pos[3]
    static UnFakeObj farSim = {{200, 0, 1.5f, 2.5f}, true, {}, NULL}, nearSim = {{5, 0, 3.5f, 4.5f}, true, {}, NULL};
    DWORD farLo, farHi, nearLo, nearHi;
    memcpy(&farLo, &farSim.pos[2], 4), memcpy(&farHi, &farSim.pos[3], 4), memcpy(&nearLo, &nearSim.pos[2], 4), memcpy(&nearHi, &nearSim.pos[3], 4);
    auto Push = [&](bool withNear) {
        Send(7, 0);
        Send(8, farLo), Send(9, farHi);
        if (withNear) Send(8, nearLo), Send(9, nearHi);
        return Send(10, withNear ? 2 : 1);
    };

    // 1. list channel: BEGIN refused with no site; a good push; count mismatch; overflow; END without BEGIN
    k.ufcReady = 0;
    assert(Send(7, 0) == 0);
    k.ufcReady = 15 | 16 | 32;
    const LONG bad0 = k.ufcPushBad, push0 = k.ufcPushes;
    const u8 pr = Push(true);
    assert(pr == 1 && k.ufcList == 2 && k.ufcPushes == push0 + 1 && UfcListed(farLo, farHi) && UfcListed(nearLo, nearHi));
    Send(7, 0), Send(8, 1), Send(9, 2);
    assert(Send(10, 5) == 0 && k.ufcPushBad == bad0 + 1 && k.ufcList == 2);  // the old list stays
    Send(7, 0);
    for (int i = 0; i < UFC_MAX; i++) Send(8, i), Send(9, 0);
    assert(Send(9, 0) == 0 && k.ufcPushBad == bad0 + 2 && Send(10, UFC_MAX) == 0 && k.ufcPushBad == bad0 + 3);
    assert(Push(true) == 1);
    g_ufcList.at = GetTickCount() - 4000;  // stale (3 s list life): nothing listed
    const LONG stale0 = k.ufcStale;
    assert(!UfcListed(farLo, farHi) && k.ufcStale == stale0 + 1);
    assert(Push(true) == 1);
    // docs/202 §3.4.3: Build/Buy (world+0x97B set) drops a fresh list with no push; after the exit nothing is listed until
    // the next push
    const LONG bb0 = g_ufcBbDrops;
    fakeWorld[0x97B] = 1;
    assert(!UfcListed(farLo, farHi) && !UfcListed(nearLo, nearHi) && g_ufcList.n == 0 && k.ufcList == 0 && g_ufcBbDrops == bb0 + 1);
    assert(!UfcListed(farLo, farHi) && g_ufcBbDrops == bb0 + 1);  // dropped once
    fakeWorld[0x97B] = 0;
    assert(!UfcListed(farLo, farHi));
    assert(Push(true) == 1 && UfcListed(farLo, farHi) && UfcListed(nearLo, nearHi));
    // §3.4.4: the poll watch. No site asks in Build/Buy (sim paused): the poll's entry still drops the pre-entry list at the
    // first ask after the exit; spans / tries inside / tries in the 3 s tail are counted from the poll's view
    {
        const LONG span0 = k.ufcBb[0], in0 = k.ufcBb[1], tail0 = k.ufcBb[2], try0 = k.ufcTry[0], drop0 = g_ufcBbDrops;
        memset(&g_ufcBbW, 0, sizeof(g_ufcBbW));
        UfcBbPoll(1000);
        k.ufcTry[0]++, UfcBbPoll(1010);                       // live: not counted
        fakeWorld[0x97B] = 1, UfcBbPoll(1020);                // entry
        assert(k.ufcBb[0] == span0 + 1 && g_ufcBbPend && k.ufcBb[1] == in0 && k.ufcBb[2] == tail0);
        k.ufcTry[0]++, UfcBbPoll(1030);                       // a try while the byte stays set: counted
        assert(k.ufcBb[1] == in0 + 1);
        fakeWorld[0x97B] = 0;
        k.ufcTry[0]++, UfcBbPoll(1040);                       // the exit interval goes to the tail
        k.ufcTry[0]++, UfcBbPoll(3900);                       // still in the tail
        k.ufcTry[0]++, UfcBbPoll(4100);                       // after it
        assert(k.ufcBb[1] == in0 + 1 && k.ufcBb[2] == tail0 + 2 && k.ufcBb[0] == span0 + 1);
        assert(!UfcListed(farLo, farHi) && !g_ufcBbPend && g_ufcBbDrops == drop0 + 1);  // dropped once after the exit
        assert(Push(true) == 1 && UfcListed(farLo, farHi));
        g_ufcBbPend = 1;
        assert(Push(true) == 1 && !g_ufcBbPend && UfcListed(farLo, farHi));  // a live push after the exit keeps its list
        fakeWorld[0x97B] = 1, g_ufcBbPend = 0;
        assert(Push(true) == 1 && !UfcListed(farLo, farHi));  // a push while the byte is set is dropped by the ask
        fakeWorld[0x97B] = 0;
        assert(Push(true) == 1 && UfcListed(farLo, farHi));
        k.ufcTry[0] = try0, k.ufcBb[0] = span0, k.ufcBb[1] = in0, k.ufcBb[2] = tail0;
    }

    // 2. C1: the agent update context (unseen class, walking Sim, own part), its owner by id
    static u8 agentBuf[0x110];
    static u8 ownerBuf[0x20];
    memset(agentBuf, 0, sizeof(agentBuf));
    *(DWORD*)(ownerBuf + 8) = farLo, *(DWORD*)(ownerBuf + 0xC) = farHi;
    *(u8**)(agentBuf + 4) = ownerBuf;
    *(DWORD*)(agentBuf + 0x94) = 1;
    t_unAgent = agentBuf, t_unCls = 2, t_unKind = 0, t_unRider = false;
    g_ufcListNext = UfcFkList;
    auto C1 = [&]() { UfcListHook(agentBuf + 8, NULL, NULL, 0, 0); };
    g_ufcSpentH = 0;
    memset(g_ufcRecent, 0, sizeof(g_ufcRecent));
    // taken: the list runs once under the hold, one release
    g_ufcFkR = 1, g_ufcFkCalls = g_ufcFkRel = g_ufcFkNext = 0;
    LONG got0 = k.ufcGot[0];
    C1();
    assert(g_ufcFkCalls == 1 && g_ufcFkDl != 0 && g_ufcFkDl != INFINITE && g_ufcFkNext == 1 && g_ufcFkRel == 1 && k.ufcGot[0] == got0 + 1);
    // timed out: skipped, nothing released; the same job's next ask is a try (deadline 0) and counted as a recovery
    g_ufcFkR = -2, g_ufcFkCalls = g_ufcFkRel = g_ufcFkNext = 0;
    const LONG fake0 = k.ufcFake[0], rec0 = k.ufcRecover[0], to0 = k.ufcTimeout[0];
    C1();
    assert(g_ufcFkNext == 0 && g_ufcFkRel == 0 && k.ufcFake[0] == fake0 + 1 && k.ufcTimeout[0] == to0 + 1 && g_ufcSpentH == 0x25000001);
    C1();
    assert(g_ufcFkDl == 0 && k.ufcRecover[0] == rec0 + 1 && k.ufcFake[0] == fake0 + 2);
    g_async.handle = 0x25000002;  // the next job: a full wait again
    C1();
    assert(g_ufcFkDl != 0 && k.ufcRecover[0] == rec0 + 1);
    // the game's path: mode off, no busy job, the +0x918 owner, a managed call served, a linked part, EA's byte on, not
    // walking, a rider, a visible class, an owner not on the list
    g_ufcFkCalls = g_ufcFkNext = 0;
    const LONG seen0 = k.ufcSeen[0];
    k.ufcMode = 0, C1(), k.ufcMode = 1;
    g_async.state = A_IDLE, C1(), g_async.state = A_RUNNING;
    k.rgOwnerTid = (LONG)GetCurrentThreadId(), C1(), k.rgOwnerTid = 0;
    g_inInvoke = 1, C1(), g_inInvoke = 0;
    assert(g_ufcFkCalls == 0 && g_ufcFkNext == 4 && k.ufcSeen[0] == seen0);
    UfcListHook(agentBuf + 0x40, NULL, NULL, 0, 0);
    agentBuf[0xC8] = 1, C1(), agentBuf[0xC8] = 0;
    *(DWORD*)(agentBuf + 0x94) = 4, C1(), *(DWORD*)(agentBuf + 0x94) = 1;
    t_unRider = true, C1(), t_unRider = false;
    t_unCls = 1, C1(), t_unCls = 2;
    *(DWORD*)(ownerBuf + 8) = 0x999, C1(), *(DWORD*)(ownerBuf + 8) = farLo;
    assert(g_ufcFkCalls == 0 && g_ufcFkNext == 10 && k.ufcSeen[0] == seen0 + 6);
    // Build/Buy with a fresh list (sv-ufc-buildbuy): no take, no fake, the game's call
    const LONG fakeBb = k.ufcFake[0];
    fakeWorld[0x97B] = 1, g_ufcFkNext = 0;
    C1();
    assert(g_ufcFkCalls == 0 && g_ufcFkNext == 1 && k.ufcFake[0] == fakeBb && k.ufcSeen[0] == seen0 + 7);
    fakeWorld[0x97B] = 0;
    assert(Push(true) == 1);

    // 3. C2: RouteFollow_A's answer under the hold, or 0 on timeout
    g_ufcAvA = UfcFkAvA;
    g_async.handle = 0x25000003;
    g_ufcFkR = 1, g_ufcFkRel = g_ufcFkNext = 0;
    assert(UfcAvHook(NULL, agentBuf + 8, NULL, NULL, 0) == 0x77 && g_ufcFkRel == 1 && g_ufcFkNext == 1);
    g_ufcFkR = -2;
    const LONG fake1 = k.ufcFake[1];
    assert(UfcAvHook(NULL, agentBuf + 8, NULL, NULL, 0) == 0 && g_ufcFkRel == 1 && g_ufcFkNext == 1 && k.ufcFake[1] == fake1 + 1);
    t_unAgent = NULL, t_unCls = 0;
    assert(UfcAvHook(NULL, agentBuf + 8, NULL, NULL, 0) == 0x77);  // outside an agent update: the game's call

    // 3b. docs/202 §3.4.2 FinishWalk: a flagged Sim is held (no fake counted, its next updates skipped until released by
    //     ts3fixes, the camera class, the pushed list, staleness or time); an unflagged one gets the game's own wait
    {
        const LONG fmSave = k.ufwMode;
        g_ufwOn = true, k.ufwMode = 1;
        memset(g_ufw, 0, sizeof(g_ufw)), g_ufwN = 0;
        auto PushF = [&](bool flagFar) {
            Send(7, 0);
            Send(8, farLo), Send(9, farHi);
            if (flagFar) assert(Send(15, 1) == 1);
            Send(8, nearLo), Send(9, nearHi);
            return Send(10, 2);
        };
        assert(PushF(false) == 1);
        t_unAgent = agentBuf, t_unCls = 2;
        g_ufcFkR = -2, g_ufcFkNext = 0;
        g_async.handle = 0x25000010;
        const LONG fakeA = k.ufcFake[0], plain0 = k.ufwPlainWait, holds0 = k.ufwHolds;
        C1();  // listed, not flagged: the game's own call (its wait), no fake, no hold
        assert(g_ufcFkNext == 1 && k.ufcFake[0] == fakeA && k.ufwPlainWait == plain0 + 1 && g_ufwN == 0);
        assert(UfcAvHook(NULL, agentBuf + 8, NULL, NULL, 0) == 0x77 && g_ufcFkNext == 2 && k.ufwPlainWait == plain0 + 2);
        assert(PushF(true) == 1 && g_ufcList.fin[0] == 1 && g_ufcList.fin[1] == 0);
        C1();  // flagged: held, this tick the skipped answer, no fake counted
        assert(g_ufcFkNext == 2 && k.ufcFake[0] == fakeA && k.ufwHolds == holds0 + 1 && g_ufwN == 1);
        assert(UfcAvHook(NULL, agentBuf + 8, NULL, NULL, 0) == 0 && k.ufwHolds == holds0 + 1 && g_ufwN == 1);  // same agent: one hold
        t_unAgent = NULL, t_unCls = 0;
        // the agent-update thunk's skip: unseen = skipped; other agents untouched
        const LONG held0 = k.ufwHeldTicks;
        static u8 otherAgent[0x110];
        assert(UfwHoldSkip(agentBuf, 1) && k.ufwHeldTicks == held0 + 1 && !UfwHoldSkip(otherAgent, 1));
        // ts3fixes: any unclaimed, claim (held? by id), release as finished
        assert(Send(20, 0) == 1 && Send(16, farLo) == 1 && Send(17, farHi) == 1 && k.ufwClaims >= 1 && Send(20, 0) == 0);
        assert(Send(16, nearLo) == 1 && Send(17, nearHi) == 0);  // not held
        const LONG fin0 = k.ufwRelease[UFW_R_FINISH], gu0 = k.ufwRelease[UFW_R_GIVEUP], seen0f = k.ufwRelease[UFW_R_SEEN],
                   exp0 = k.ufwRelease[UFW_R_EXPIRED];
        Send(16, farLo);
        assert(Send(18, farHi) == 1 && g_ufwN == 0 && k.ufwRelease[UFW_R_FINISH] == fin0 + 1 && !UfwHoldSkip(agentBuf, 1));
        assert(Send(17, farHi) == 0 && Send(19, farHi) == 0);  // gone
        // released: its flag is cleared, so its stop pass meeting a busy lock gets the game's wait, not a new hold
        t_unAgent = agentBuf, t_unCls = 2;
        const LONG plain1 = k.ufwPlainWait;
        C1();
        assert(g_ufwN == 0 && k.ufwPlainWait == plain1 + 1);
        t_unAgent = NULL, t_unCls = 0;
        assert(PushF(true) == 1);
        auto Hold = [&]() {
            t_unAgent = agentBuf, t_unCls = 2;
            C1();
            t_unAgent = NULL, t_unCls = 0;
            assert(g_ufwN == 1);
        };
        // given up by ts3fixes
        Hold();
        Send(16, farLo);
        assert(Send(19, farHi) == 1 && g_ufwN == 0 && k.ufwRelease[UFW_R_GIVEUP] == gu0 + 1);
        assert(g_ufcList.fin[0] == 0 && PushF(true) == 1);  // a release clears the flag until the next list
        // the camera class turns visible: released, the update runs
        Hold();
        assert(!UfwHoldSkip(agentBuf, 0) && g_ufwN == 0 && k.ufwRelease[UFW_R_SEEN] == seen0f + 1);
        // dropped from the flag in the next push: released at END
        Hold();
        assert(PushF(false) == 1 && g_ufwN == 0 && k.ufwRelease[UFW_R_SEEN] == seen0f + 2);
        assert(PushF(true) == 1);
        // unclaimed 3 s; claimed but held 10 min; stale list; mode off
        Hold();
        for (auto& h : g_ufw) if (h.agent) h.at -= kUfwClaimMs + 1;
        assert(!UfwHoldSkip(agentBuf, 1) && g_ufwN == 0 && k.ufwRelease[UFW_R_EXPIRED] == exp0 + 1);
        Hold();
        Send(16, farLo), Send(17, farHi);
        for (auto& h : g_ufw) if (h.agent) h.at -= kUfwClaimMs + 1;
        assert(UfwHoldSkip(agentBuf, 1));  // claimed: kept past 3 s
        for (auto& h : g_ufw) if (h.agent) h.at -= kUfwMaxMs;
        assert(!UfwHoldSkip(agentBuf, 1) && k.ufwRelease[UFW_R_EXPIRED] == exp0 + 2);
        Hold();
        g_ufcList.at = GetTickCount() - 4000;
        assert(!UfwHoldSkip(agentBuf, 1) && k.ufwRelease[UFW_R_EXPIRED] == exp0 + 3);
        assert(PushF(true) == 1);
        Hold();
        k.ufwMode = 0;
        assert(!UfwHoldSkip(agentBuf, 1) && k.ufwRelease[UFW_R_EXPIRED] == exp0 + 4);
        // mode off: C1 / C2 fake as before
        t_unAgent = agentBuf, t_unCls = 2, g_ufcFkNext = 0;
        C1();
        assert(g_ufcFkNext == 0 && k.ufcFake[0] == fakeA + 1 && g_ufwN == 0);
        t_unAgent = NULL, t_unCls = 0;
        // all slots in use: the game's own wait
        k.ufwMode = 1;
        for (auto& h : g_ufw) h.agent = otherAgent, h.lo = 1, h.hi = 2, h.at = GetTickCount(), h.claimed = true;
        g_ufwN = UFW_MAX;
        const LONG full0 = k.ufwFull;
        t_unAgent = agentBuf, t_unCls = 2, g_ufcFkNext = 0;
        C1();
        assert(g_ufcFkNext == 1 && k.ufwFull == full0 + 1);
        t_unAgent = NULL, t_unCls = 0;
        memset(g_ufw, 0, sizeof(g_ufw)), g_ufwN = 0;
        // FinishWalk not installed: codes refused
        g_ufwOn = false;
        assert(Send(16, farLo) == 0 && Send(20, 0) == 0);
        k.ufwMode = fmSave;
        assert(Push(true) == 1);
    }

    // 4. C3 through rtoro.cpp's states, owner = the running script task (the host stand-in's +0xBD0): timed out = code 4,
    //    nothing released; taken = the game's route part, one release; a task Sim on screen, an unlisted task, no task = the
    //    game's path; the same Sim within 1.5 s = a try
    const RtoRouteFn routeSave = g_rtoRoute;
    const LockReleaseFn relSave = g_rtoRel;
    DWORD* const hostSave = g_hostPtr;
    g_hostPtr = &g_fakeHostPtr;
    auto Task = [&](DWORD lo, DWORD hi) { FakeTask(0, lo), *(DWORD*)(g_fakeHost + 0xBD4) = hi; };
    static UnFakeObj* const* mgrPtr;
    static void* mgrObj = &mgrPtr;
    g_unObjMgr = &mgrObj;
    g_unFind = [](void*, void*, DWORD lo, DWORD hi) -> void* {
        DWORD a, b, c, d;
        memcpy(&a, &farSim.pos[2], 4), memcpy(&b, &farSim.pos[3], 4), memcpy(&c, &nearSim.pos[2], 4), memcpy(&d, &nearSim.pos[3], 4);
        return lo == a && hi == b ? (void*)&farSim : lo == c && hi == d ? (void*)&nearSim : NULL;
    };
    g_rtoRoute = UfcFkRoute, g_rtoRel = UfcFkRel;
    g_ufcC3 = UfcC3;
    void* const lock = g_async.lock;
    DWORD o[4];
    static const DWORD kGameDl = INFINITE;
    auto C3 = [&](bool route) {
        ((LockAcquireFn)(void*)RtoAcqStub)(lock, NULL, &kGameDl);  // the naked R-acq as 0x831568 calls it
        o[0] = o[2] = o[3] = 0xEEEE;
        if (route) RtoCallStub(fakeWorld, NULL, o, NULL, (u8*)&nearSim, NULL, NULL, 0, 0, 0, 0, 1);
        RtoRelStub(lock, NULL);
        assert(t_rtoN == 0);
    };
    const LockAcquireFn rtoAcqSave = g_rtoAcq;
    g_rtoAcq = UfcFkAcq;  // the stub's pass-through (the game's take) lands on the fake too
    Task(farLo, farHi);
    g_async.handle = 0x25000004;
    g_ufcFkR = -2, g_ufcFkRel = g_ufcFkNext = 0;
    const LONG fake2 = k.ufcFake[2], nr0 = k.ufcNoRoute, rec2 = k.ufcRecover[2];
    C3(true);
    assert(o[0] == 4 && o[2] == 0 && o[3] == 0 && g_ufcFkNext == 0 && g_ufcFkRel == 0 && k.ufcFake[2] == fake2 + 1);
    g_async.handle = 0x25000005;  // another job, but the same Sim was faked < 1.5 s ago: a try
    C3(false);
    assert(g_ufcFkDl == 0 && k.ufcRecover[2] == rec2 + 1 && k.ufcNoRoute == nr0 + 1 && g_ufcFkRel == 0);
    memset(g_ufcRecent, 0, sizeof(g_ufcRecent));
    g_ufcSpentH = 0;
    g_ufcFkR = 1;
    C3(true);
    assert(o[0] == 0 && g_ufcFkNext == 1 && g_ufcFkRel == 1 && g_ufcFkDl != 0 && g_ufcFkDl != INFINITE);  // held, one release
    const LONG seen2 = k.ufcSeen[2];
    Task(nearLo, nearHi);  // listed, but on screen: the game's take (infinite deadline) and release
    C3(true);
    assert(g_ufcFkDl == INFINITE && k.ufcSeen[2] == seen2 + 1 && g_ufcFkNext == 2 && g_ufcFkRel == 2);
    Task(0x999, 0);  // a task that is not listed (an alarm, another object)
    C3(true);
    Task(0, 0);      // no task
    C3(true);
    assert(g_ufcFkDl == INFINITE && k.ufcSeen[2] == seen2 + 3 && g_ufcFkNext == 4 && g_ufcFkRel == 4);
    g_rtoRoute = routeSave, g_rtoRel = relSave, g_rtoAcq = rtoAcqSave;
    g_ufcC3 = NULL;

    // 5. C4: fgro.cpp's game call for an unseen Sim's own task: "not free" on timeout, the game's search under the hold
    const FgFn fgSave = g_fgOrig;
    g_fgOrig = UfcFkFg;
    g_ufcFg = UfcPlace;
    Task(farLo, farHi);
    g_async.handle = 0x25000006;
    g_ufcFkR = -2, g_ufcFkNext = g_ufcFkRel = 0;
    const LONG fake3 = k.ufcFake[3];
    assert(FgGame(NULL, NULL, NULL, NULL, 0, NULL, 1) == 1 && g_ufcFkNext == 0 && g_ufcFkRel == 0 && k.ufcFake[3] == fake3 + 1);
    memset(g_ufcRecent, 0, sizeof(g_ufcRecent));
    g_ufcSpentH = 0, g_ufcFkR = 1;
    assert(FgGame(NULL, NULL, NULL, NULL, 0, NULL, 1) == 0 && g_ufcFkNext == 1 && g_ufcFkRel == 1);
    const LONG seen3 = k.ufcSeen[3];
    Task(nearLo, nearHi);
    assert(FgGame(NULL, NULL, NULL, NULL, 0, NULL, 1) == 0 && k.ufcSeen[3] == seen3 + 1 && g_ufcFkRel == 1);  // on screen: plain
    g_async.state = A_IDLE;
    Task(farLo, farHi);
    assert(FgGame(NULL, NULL, NULL, NULL, 0, NULL, 1) == 0 && g_ufcFkNext == 3 && g_ufcFkRel == 1);  // no job: plain
    g_async.state = A_RUNNING;
    // §5.6: at most kUfcC4Cap fakes per Sim per second, then the game's answer; the logged Sim id reads back bit by bit
    memset(g_ufcRecent, 0, sizeof(g_ufcRecent));
    g_ufcSpentH = 0, g_ufcFkR = -2, g_ufcFkNext = 0, g_ufcC4LogSet = false;
    const LONG fake3b = k.ufcFake[3], cap0 = g_ufcC4Capped;
    for (int i = 0; i < kUfcC4Cap; i++) assert(FgGame(NULL, NULL, NULL, NULL, 0, NULL, 1) == 1);
    assert(k.ufcFake[3] == fake3b + kUfcC4Cap && g_ufcFkNext == 0 && g_ufcC4Capped == cap0);
    assert(FgGame(NULL, NULL, NULL, NULL, 0, NULL, 1) == 0 && g_ufcFkNext == 1 && g_ufcC4Capped == cap0 + 1 && k.ufcFake[3] == fake3b + kUfcC4Cap);
    {
        u64 id = 0;
        assert(UfcC4LogMsg(64) == 1);
        for (DWORD b = 0; b < 64; b++) id |= (u64)UfcC4LogMsg(b) << b;
        assert(id == ((u64)farHi << 32 | farLo) && UfcC4LogMsg(65) == 1 && UfcC4LogMsg(64) == 0);
    }
    for (auto& e : g_ufcRecent) e.c4At -= kUfcC4WindowMs;  // the second ended: fakes again
    assert(FgGame(NULL, NULL, NULL, NULL, 0, NULL, 1) == 1 && k.ufcFake[3] == fake3b + kUfcC4Cap + 1);
    g_ufcFg = NULL, g_fgOrig = fgSave;
    FakeTask(0, 0);
    g_hostPtr = hostSave;

    UfcLogCounters();
    // install gate: [RouteAsync] ready at load, no job queued yet (owner 0) = the sites install (all C off: nothing patched)
    {
        wchar_t ini[MAX_PATH];
        GetTempPathW(MAX_PATH, ini);
        wcscat_s(ini, L"ts3n-selftest-ufc.ini");
        WritePrivateProfileStringW(L"UnseenFallback", L"Sites", L"1", ini);
        WritePrivateProfileStringW(L"UnseenFallback", L"SiteWaitMs", L"37", ini);
        for (const wchar_t* c : {L"C1", L"C2", L"C3", L"C4"}) WritePrivateProfileStringW(L"UnseenFallback", c, L"0", ini);
        const LONG ready0 = k.asyncReady, wait0 = k.ufcWaitMs;
        k.asyncReady = 1, g_async.owner = 0, k.ufcReady = 0;
        WritePrivateProfileStringW(L"UnseenFallback", L"FinishWalk", L"1", ini);
        assert(ApplyUnseenFallbackSites(ini) && k.ufcWaitMs == 37 && k.ufcReady == 0 && k.ufcMode == 0);
        assert(k.ufwReady == 0 && !g_unHoldSkip && !g_ufwOn);  // FinishWalk needs C1 or C2 and the agent context: SKIP
        k.asyncReady = 0, k.ufcWaitMs = wait0;
        assert(ApplyUnseenFallbackSites(ini) && k.ufcWaitMs == wait0);  // no [RouteAsync]: SKIP before any read
        k.asyncReady = ready0;
        DeleteFileW(ini);
    }
    // restore
    g_ufcAcq = (LockAcquireFn)(uintptr_t)0x004E16F0, g_ufcRel = (LockReleaseFn)(uintptr_t)0x004E17B0;
    g_ufcListNext = (UtListFn)(uintptr_t)UT_LIST, g_ufcAvA = (UfcAvAFn)(uintptr_t)UFC_AV_A;
    g_unComp = compSave, g_unPos = posSave, g_unFrustum = frSave, g_unInFrustum = inSave, g_unCamPos = cpSave, g_unCamTgt = ctSave;
    g_unFind = findSave, g_unBitTransform = btSave, g_unBitSim = bsSave, g_unObjMgr = mgrSave;
    g_unCam.ok = false;
    g_async = asyncSave;
    k.rgOwnerTid = rgOwnSave;
    k.ufcReady = 0, k.ufcMode = 0;
    memset(&g_ufcList, 0, sizeof(g_ufcList));
    g_ufcSpentH = 0;
    memset(g_ufcRecent, 0, sizeof(g_ufcRecent));
    printf("unseen fallback sites (docs/202 §3.4): list channel, C1/C2 agent rules, timed take held/skipped, one wait per job, C3 states and "
           "C4 placement answer by the running task's Sim, FinishWalk holds (flag, claim, release, seen, list drop, expiry, plain wait) ok\n");
}

// TestUfStrict: docs/202 §3.1.1 strict DeadlineMs: overrun learning per path (p99, floor 30 ms, 80 ms until 20 samples, the
// largest exit per context), the cut never below setting / 2, UfSyncEnd / UfJobEnd feeding it, and the pre-search +0x918 gate
// of a marked sync plan (deadline form, timed out = RunPlan answers 0 before the lock, counted as a cut). Fakes only.
static void TestUfStrict() {
    Ts3nCounters& k = ts3native_counters;
    if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf);
    static UfOver save[2][3];
    memcpy(save, g_ufOver, sizeof(save));
    const LONG us0 = k.ufUs, mode0 = k.ufMode, wm0 = k.workerMode;
    auto Reset = [] { memset(g_ufOver, 0, sizeof(g_ufOver)); };
    auto Feed = [](int ctx, unsigned mask, LONG late, int n) { for (int i = 0; i < n; i++) UfOverNote(ctx, mask, late); };
    k.ufUs = 300000;
    Reset();
    assert(UfEffUs(0) == 220000 && UfEffUs(1) == 220000 && k.ufOverP99Us[0] == 80000);  // start value 80 ms
    // learned: 30 world A* cuts 40 ms late on the sim -> p99 = the 40-44 ms bucket edge; the job context keeps its start value
    Feed(0, 1, 40000, 30);
    assert(UfEffUs(0) == 256000 && k.ufOverP99Us[0] == 44000 && UfEffUs(1) == 220000);
    // a context takes its largest exit; under 20 samples max(80 ms, the largest sample)
    Feed(0, 2, 120000, 5);
    assert(UfEffUs(0) == 180000 && k.ufOverP99Us[0] == 120000);
    Reset();
    Feed(0, 4, 10000, 3);
    assert(UfEffUs(0) == 220000);  // few samples, all small: 80 ms
    // floor 30 ms
    Reset();
    Feed(1, 1, 2000, 30);
    assert(UfEffUs(1) == 270000 && k.ufOverP99Us[1] == 30000);
    // p99: one outlier in 100 does not move it
    Reset();
    Feed(1, 1, 20000, 99), Feed(1, 1, 290000, 1);
    assert(UfEffUs(1) == 270000);
    // the cut never goes below setting / 2 (300 -> 150, 175 -> 87.5)
    Reset();
    Feed(1, 4, 250000, 30);
    assert(UfEffUs(1) == 150000);
    k.ufUs = 175000;
    assert(UfEffUs(1) == 87500);
    k.ufUs = 300000;
    // nothing learned without an exit or when in time
    Reset();
    const LONG s0 = k.ufOverSamples;
    UfOverNote(0, 0, 90000), UfOverNote(1, 1, -1);
    assert(k.ufOverSamples == s0 && !g_ufOver[0][0].n && !g_ufOver[1][0].n);
    // UfSyncEnd learns the closed session's overrun by exit; UfJobEnd the job's
    u8 res[0x40] = {};
    *(DWORD*)res = 2;
    t_pdHitLast = true, t_pdLastMask = 2, t_pdLastLateUs = 60000;
    UfSyncEnd(0x26000001, res, 280000);
    assert(g_ufOver[0][1].n == 1 && g_ufOver[0][1].maxUs == 60000 && !t_pdLastMask && !t_pdHitLast && g_ufCutH == 0x26000001);
    g_ufCutH = 0;
    AsyncJob fj = {};
    *(DWORD*)fj.result = 2;
    UfJobEnd(&fj, true, 250000, 50000, 4);
    assert(g_ufOver[1][2].n == 1 && fj.ufCut == 1);

    // pre-search +0x918 gate (RunPlan)
    static u8 world[0x1000];
    const AsyncSlot asyncSave = g_async;
    const PdSess pdSave = t_pd;
    const bool sessSave = t_ufSess;
    g_ufcAcq = UfcFkAcq, g_ufcRel = UfcFkRel;
    g_async.owner = GetCurrentThreadId(), g_async.lock = world + 0x918, g_async.state = A_RUNNING;
    k.ufMode = 1, k.workerMode = 0;
    t_pd.open = true, t_pd.dl = PdNow() + g_qpf.QuadPart / 10;  // 100 ms left
    t_ufSess = false;
    const LONG c0 = g_ufcFkCalls, w0 = k.ufLockWaits, f0 = k.ufLockFail;
    assert(UfLockGate(world) == 0 && g_ufcFkCalls == c0);  // not a marked session
    t_ufSess = true;
    static u8 other[0x1000];
    assert(UfLockGate(other) == 0 && g_ufcFkCalls == c0);  // another world's lock
    g_async.state = A_IDLE;
    assert(UfLockGate(world) == 0);  // no job busy: the game's own acquire, nothing to wait for
    g_async.state = A_RUNNING;
    // taken within the deadline: held, a finite GetTickCount deadline about 100 ms ahead
    g_ufcFkR = 1;
    const DWORD now = GetTickCount();
    assert(UfLockGate(world) == 1 && g_ufcFkCalls == c0 + 1 && g_ufcFkDl != 0 && g_ufcFkDl != INFINITE &&
           k.ufLockWaits == w0 + 1 && k.ufLockFail == f0 && !t_ufLockFail);
    TIMING_ASSERT("UnseenFallback lock deadline <= now + 110 ms", g_ufcFkDl - now <= 110);
    // timed out: the plan fails before the lock (RunPlan 0, no release), counted, and UfSyncEnd answers it as a cut
    g_ufcFkR = -2;
    const LONG rel0 = g_ufcFkRel;
    assert(RunPlan(world, NULL, NULL, NULL, NULL, 0, 1, NULL, NULL, NULL, 0, 0) == 0 && k.ufLockFail == f0 + 1 && t_ufLockFail &&
           g_ufcFkRel == rel0);
    t_pdHitLast = false;
    UfSyncEnd(0x26000002, res, 230000);
    assert(g_ufCutH == 0x26000002 && !t_ufLockFail);
    g_ufCutH = 0;
    // deadline already past: a try (deadline 0), never an infinite wait
    t_pd.dl = PdNow() - 1;
    assert(UfLockGate(world) == -2 && g_ufcFkDl == 0);
    t_ufLockFail = false;
    // restore
    g_ufcFkR = 1;
    g_ufcAcq = (LockAcquireFn)(uintptr_t)0x004E16F0, g_ufcRel = (LockReleaseFn)(uintptr_t)0x004E17B0;
    g_async = asyncSave, t_pd = pdSave, t_ufSess = sessSave;
    memcpy(g_ufOver, save, sizeof(save));
    k.ufUs = us0, k.ufMode = mode0, k.workerMode = wm0;
    printf("unseen fallback strict (docs/202 §3.1.1): overrun p99 per path, floor 30, start 80, cut >= setting / 2, pre-search lock-wait fail ok\n");
}
